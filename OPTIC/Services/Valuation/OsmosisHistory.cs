using System.Globalization;
using System.Net;
using System.Text.Json;

namespace OPTIC.Services.Valuation;

public sealed class HistoricalFetchException : Exception
{
    public HistoricalFetchException(string message) : base(message) { }
}

/// <summary>
/// Read-only historical queries against an Osmosis archive LCD. Every state query sends x-cosmos-block-height and
/// checks the height the node says it served. A pruned state, timeout or ignored header is reported as missing
/// data, never as zero liquidity. Raw responses are archived with their SHA-256.
/// </summary>
public sealed class OsmosisHistory
{
    public const string DefaultArchiveLcd = "https://lcd.archive.osmosis.zone";
    public const string ParserVersion = "osmosis-lcd/1";

    private readonly HttpClient _http;
    private readonly string _base;
    private readonly Action<string, byte[], string>? _archive;
    private static readonly SemaphoreSlim Concurrency = new(4, 4);

    public OsmosisHistory(HttpClient http, string? baseUrl = null, Action<string, byte[], string>? archive = null)
    {
        _http = http;
        _base = (baseUrl ?? DefaultArchiveLcd).TrimEnd('/');
        _archive = archive;
    }

    public sealed record LcdResponse(HttpStatusCode Status, long? ServedHeight, JsonDocument? Json, string Body, string Url, string Sha256);

    public async Task<LcdResponse> GetAsync(string path, long? height, CancellationToken ct)
    {
        var url = _base + path;
        await Concurrency.WaitAsync(ct);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (height is not null) req.Headers.Add("x-cosmos-block-height", height.Value.ToString(CultureInfo.InvariantCulture));
                HttpResponseMessage resp;
                try
                {
                    resp = await _http.SendAsync(req, ct);
                }
                catch (HttpRequestException) when (attempt < 2) { await Task.Delay(1000 * (attempt + 1), ct); continue; }
                catch (TaskCanceledException) when (!ct.IsCancellationRequested && attempt < 2) { await Task.Delay(1000 * (attempt + 1), ct); continue; }
                catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new HistoricalFetchException($"{url} timed out (missing data, not zero)."); }
                catch (HttpRequestException ex) { throw new HistoricalFetchException($"{url} failed: {ex.Message}"); }
                using (resp)
                {
                    if (((int)resp.StatusCode == 429 || (int)resp.StatusCode >= 502) && attempt < 2)
                    {
                        await Task.Delay(1500 * (attempt + 1), ct);
                        continue;
                    }
                    var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
                    var body = System.Text.Encoding.UTF8.GetString(bytes);
                    long? served = null;
                    foreach (var name in new[] { "Grpc-Metadata-X-Cosmos-Block-Height", "x-cosmos-block-height" })
                        if (resp.Headers.TryGetValues(name, out var vals) && long.TryParse(vals.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var h)) { served = h; break; }
                    JsonDocument? json = null;
                    try { json = JsonDocument.Parse(body); } catch (JsonException) { }
                    var sha = ReceiptIdentity.Sha256Hex(bytes);
                    _archive?.Invoke($"osmosis{path.Replace('/', '_').Replace('?', '_').Replace('&', '_').Replace('=', '-')}@{height}.json", bytes, url);
                    return new LcdResponse(resp.StatusCode, served, json, body, url, sha);
                }
            }
        }
        finally
        {
            Concurrency.Release();
        }
    }

    private static string Classify(LcdResponse r, long height)
    {
        if (r.Body.Contains("pruned", StringComparison.OrdinalIgnoreCase) || r.Body.Contains("version does not exist", StringComparison.OrdinalIgnoreCase))
            return $"State at height {height} is not retained by this node (pruned). This is missing data, not evidence of zero liquidity.";
        return $"HTTP {(int)r.Status} from {r.Url}: {Trim(r.Body)}";
    }

    private static string Trim(string s) => s.Length > 300 ? s[..300] + "…" : s;

    private static EvidenceRef Ev(LcdResponse r, long? height, string description) => new()
    {
        Kind = "chain-query", Description = description, Url = r.Url, Sha256 = r.Sha256, BlockHeight = height,
        CollectedUtc = DateTime.UtcNow, ParserVersion = ParserVersion,
    };

    public async Task<DateTime?> BlockTimeAsync(long height, CancellationToken ct)
    {
        var r = await GetAsync($"/cosmos/base/tendermint/v1beta1/blocks/{height}", null, ct);
        if (r.Status != HttpStatusCode.OK || r.Json is null) return null;
        var t = r.Json.RootElement.GetProperty("block").GetProperty("header").GetProperty("time").GetString();
        return DateTime.Parse(t!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
    }

    public async Task<long> LatestHeightAsync(CancellationToken ct)
    {
        var r = await GetAsync("/cosmos/base/tendermint/v1beta1/blocks/latest", null, ct);
        if (r.Json is null) throw new HistoricalFetchException("Could not read the latest block.");
        return long.Parse(r.Json.RootElement.GetProperty("block").GetProperty("header").GetProperty("height").GetString()!, CultureInfo.InvariantCulture);
    }

    // Known (height, time) points, shared across lookups so later searches start close to the answer.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, DateTime> Anchors = new();
    private static long _earliestServed;

    private async Task<DateTime?> TimeAtAsync(long height, CancellationToken ct)
    {
        if (Anchors.TryGetValue(height, out var t)) return t;
        var r = await BlockTimeAsync(height, ct);
        if (r is not null) Anchors[height] = r.Value;
        return r;
    }

    /// <summary>The last block at or before <paramref name="utc"/>: interpolation on known block times, then bisection.</summary>
    public async Task<(long Height, DateTime BlockTimeUtc)> HeightAtOrBeforeAsync(DateTime utc, CancellationToken ct)
    {
        var latest = await LatestHeightAsync(ct);
        var latestTime = await TimeAtAsync(latest, ct) ?? throw new HistoricalFetchException("No time for latest block.");
        if (utc >= latestTime) return (latest, latestTime);
        if (_earliestServed == 0)
        {
            // Earliest height this node serves (archive nodes may start after genesis).
            long a = 1, b = latest;
            if (await TimeAtAsync(1, ct) is null)
                while (a < b) { var m = a + (b - a) / 2; if (await TimeAtAsync(m, ct) is null) a = m + 1; else b = m; }
            _earliestServed = a;
        }
        var earliestTime = await TimeAtAsync(_earliestServed, ct) ?? throw new HistoricalFetchException("No time for the earliest retained block.");
        if (utc < earliestTime) throw new HistoricalFetchException($"The node does not retain blocks as early as {utc:yyyy-MM-dd HH:mm} UTC.");

        // Bracket with the nearest known anchors.
        var known = Anchors.Where(kv => kv.Key >= _earliestServed && kv.Key <= latest).ToList();
        var below = known.Where(kv => kv.Value <= utc).OrderByDescending(kv => kv.Key).First();
        var above = known.Where(kv => kv.Value > utc).OrderBy(kv => kv.Key).First();
        long lo = below.Key, hi = above.Key;
        DateTime loT = below.Value, hiT = above.Value;
        for (var step = 0; hi - lo > 1 && step < 60; step++)
        {
            long mid;
            if (step < 6 && hiT > loT)
            {
                // Interpolate on time, nudged inward so the bracket always shrinks.
                var frac = (utc - loT).TotalSeconds / (hiT - loT).TotalSeconds;
                mid = lo + (long)Math.Round((hi - lo) * frac);
                mid = Math.Clamp(mid, lo + 1, hi - 1);
            }
            else mid = lo + (hi - lo) / 2;
            var t = await TimeAtAsync(mid, ct) ?? throw new HistoricalFetchException($"No block time at {mid}.");
            if (t <= utc) { lo = mid; loT = t; } else { hi = mid; hiT = t; }
            // After interpolation lands close, probe a small window to bracket tightly.
            if (step < 6 && hi - lo > 64)
            {
                var w = Math.Max(4, (long)Math.Round((hi - lo) * 0.002));
                var probe = t <= utc ? Math.Min(hi - 1, mid + w) : Math.Max(lo + 1, mid - w);
                var pt = await TimeAtAsync(probe, ct);
                if (pt is not null) { if (pt <= utc) { lo = probe; loT = pt.Value; } else { hi = probe; hiT = pt.Value; } }
            }
        }
        return (lo, loT);
    }
    /// <summary>
    /// Pool state at a height, verified. CL pools include initialized ticks in both directions from the current tick.
    /// </summary>
    // Taker fee per pool as last read (height, fee, source); reused for nearby heights when the caller allows it.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Height, string Fee, string Source)> TakerFeeSeen = new();

    /// <param name="sellDenom">When set, only load ticks for swaps selling this denom (fewer archive requests).</param>
    /// <param name="takerFeeReuseBlocks">Reuse a taker fee read within this many blocks of the height (0 = always read).</param>
    public async Task<OsmosisPoolSnapshot> FetchPoolAsync(string poolId, long height, CancellationToken ct, string? sellDenom = null, long takerFeeReuseBlocks = 0)
    {
        var snap = new OsmosisPoolSnapshot { Id = $"osmosis-pool:{poolId}@{height}", PoolId = poolId, RequestedHeight = height, CollectedUtc = DateTime.UtcNow };
        var r = await GetAsync($"/osmosis/poolmanager/v1beta1/pools/{poolId}", height, ct);
        snap.Evidence.Add(Ev(r, height, $"Pool {poolId} state"));
        if (r.Status != HttpStatusCode.OK || r.Json is null)
        {
            // "not found" (gamm/cl) or "failed to find route for pool id" (poolmanager): the pool id was not yet assigned.
            if ((r.Body.Contains("not found", StringComparison.OrdinalIgnoreCase) && r.Body.Contains("pool", StringComparison.OrdinalIgnoreCase)
                 || r.Body.Contains("failed to find route for pool id", StringComparison.OrdinalIgnoreCase))
                && !r.Body.Contains("pruned", StringComparison.OrdinalIgnoreCase) && !r.Body.Contains("version does not exist", StringComparison.OrdinalIgnoreCase))
            {
                snap.Exists = false;
                snap.ServedHeight = r.ServedHeight;
                snap.Problems.Add($"Pool {poolId} did not exist at height {height}.");
                return snap;
            }
            throw new HistoricalFetchException(Classify(r, height));
        }
        if (r.ServedHeight != height)
            throw new HistoricalFetchException($"The node answered for height {r.ServedHeight?.ToString(CultureInfo.InvariantCulture) ?? "(none given)"} instead of {height}; the result is not used.");
        snap.ServedHeight = r.ServedHeight;
        snap.BlockTimeUtc = await BlockTimeAsync(height, ct);

        var pool = r.Json.RootElement.GetProperty("pool");
        var type = pool.TryGetProperty("@type", out var t) ? t.GetString() ?? "" : "";
        if (type.Contains("concentratedliquidity", StringComparison.OrdinalIgnoreCase))
        {
            snap.PoolType = "concentrated";
            snap.Token0 = pool.GetProperty("token0").GetString();
            snap.Token1 = pool.GetProperty("token1").GetString();
            snap.CurrentSqrtPrice = pool.GetProperty("current_sqrt_price").GetString();
            snap.CurrentTick = long.Parse(pool.GetProperty("current_tick").GetString()!, CultureInfo.InvariantCulture);
            snap.CurrentLiquidity = pool.GetProperty("current_tick_liquidity").GetString();
            snap.SpreadFactor = pool.GetProperty("spread_factor").GetString();
            if (sellDenom is null || sellDenom == snap.Token0) await LoadTicksAsync(snap, snap.Token0!, height, below: true, ct);
            if (sellDenom is null || sellDenom == snap.Token1) await LoadTicksAsync(snap, snap.Token1!, height, below: false, ct);
        }
        else if (type.Contains("balancer", StringComparison.OrdinalIgnoreCase))
        {
            snap.PoolType = "balancer";
            snap.SwapFee = pool.GetProperty("pool_params").GetProperty("swap_fee").GetString();
            foreach (var a in pool.GetProperty("pool_assets").EnumerateArray())
                snap.Assets.Add(new PoolAssetDto { Denom = a.GetProperty("token").GetProperty("denom").GetString()!, Amount = a.GetProperty("token").GetProperty("amount").GetString()!, Weight = a.GetProperty("weight").GetString() });
        }
        else
        {
            snap.PoolType = type.Contains("stableswap", StringComparison.OrdinalIgnoreCase) ? "stableswap" : type.Contains("cosmwasm", StringComparison.OrdinalIgnoreCase) ? "cosmwasm" : "unknown";
            snap.Problems.Add($"Pool type {type} is recorded but its swap math is not implemented.");
        }

        if (takerFeeReuseBlocks > 0 && TakerFeeSeen.TryGetValue(poolId, out var seen) && Math.Abs(seen.Height - height) <= takerFeeReuseBlocks)
        {
            snap.TakerFee = seen.Fee;
            snap.TakerFeeSource = $"{seen.Source} (read at height {seen.Height}, {Math.Abs(seen.Height - height)} blocks away)";
        }
        else
        {
            await LoadTakerFeeAsync(snap, height, ct);
            if (snap.TakerFee != null) TakerFeeSeen[poolId] = (height, snap.TakerFee, snap.TakerFeeSource ?? "");
        }
        return snap;
    }

    private async Task LoadTicksAsync(OsmosisPoolSnapshot snap, string tokenIn, long height, bool below, CancellationToken ct)
    {
        var r = await GetAsync($"/osmosis/concentratedliquidity/v1beta1/liquidity_net_in_direction?pool_id={snap.PoolId}&token_in={Uri.EscapeDataString(tokenIn)}&use_cur_tick=true&use_no_bound=true", height, ct);
        snap.Evidence.Add(Ev(r, height, $"Pool {snap.PoolId} initialized ticks, {(below ? "selling token0" : "selling token1")}"));
        if (r.Status != HttpStatusCode.OK || r.Json is null || r.ServedHeight != height)
        {
            snap.Problems.Add($"Ticks {(below ? "below" : "above")} the current price unavailable: {(r.ServedHeight != height && r.Status == HttpStatusCode.OK ? "height not honored" : Classify(r, height))}");
            return;
        }
        foreach (var d in r.Json.RootElement.GetProperty("liquidity_depths").EnumerateArray())
        {
            var idx = long.Parse(d.GetProperty("tick_index").GetString()!, CultureInfo.InvariantCulture);
            if (snap.Ticks.Any(x => x.Index == idx)) continue;
            snap.Ticks.Add(new PoolTickDto { Index = idx, LiquidityNet = d.GetProperty("liquidity_net").GetString()! });
        }
        snap.Ticks.Sort((a, b) => a.Index.CompareTo(b.Index));
        if (below) snap.TicksBelowComplete = true; else snap.TicksAboveComplete = true;
    }

    private async Task LoadTakerFeeAsync(OsmosisPoolSnapshot snap, long height, CancellationToken ct)
    {
        var denoms = snap.PoolType == "concentrated" ? new[] { snap.Token0!, snap.Token1! } : snap.Assets.Select(a => a.Denom).Take(2).ToArray();
        if (denoms.Length == 2)
        {
            var r = await GetAsync($"/osmosis/poolmanager/v1beta1/trading_pair_takerfee?denom_0={Uri.EscapeDataString(denoms[0])}&denom_1={Uri.EscapeDataString(denoms[1])}", height, ct);
            if (r.Status == HttpStatusCode.OK && r.ServedHeight == height && r.Json is not null && r.Json.RootElement.TryGetProperty("taker_fee", out var tf))
            {
                snap.TakerFee = tf.GetString();
                snap.TakerFeeSource = $"trading_pair_takerfee at height {height}";
                snap.Evidence.Add(Ev(r, height, "Taker fee for the pair"));
                return;
            }
        }
        var p = await GetAsync("/osmosis/poolmanager/v1beta1/Params", height, ct);
        if (p.Status == HttpStatusCode.OK && p.ServedHeight == height && p.Json is not null
            && p.Json.RootElement.TryGetProperty("params", out var ps) && ps.TryGetProperty("taker_fee_params", out var tfp) && tfp.TryGetProperty("default_taker_fee", out var dtf))
        {
            snap.TakerFee = dtf.GetString();
            snap.TakerFeeSource = $"default_taker_fee at height {height}";
            snap.Evidence.Add(Ev(p, height, "Default taker fee"));
            return;
        }
        snap.Problems.Add("Taker fee at this height could not be read; modeled as unknown.");
    }

    /// <summary>
    /// The block where the pool was created: the pool_created event from tx search, or, when the node cannot search,
    /// the first height at which the pool exists (bisection on the pool query). Null when neither works.
    /// </summary>
    /// <param name="notBefore">A height at which the pool is known not to exist yet (e.g. the creation of a lower-numbered pool).</param>
    public async Task<long?> PoolCreationHeightAsync(string poolId, CancellationToken ct, long? notBefore = null)
    {
        // With a lower bound, bisection on the pool query is quick (about 25 short requests); tx search can hang.
        if (notBefore is null)
        {
            try
            {
                using var quick = CancellationTokenSource.CreateLinkedTokenSource(ct);
                quick.CancelAfter(TimeSpan.FromSeconds(25));
                var h = await PoolCreatedEventHeightAsync(poolId, quick.Token);
                if (h is not null) return h;
            }
            catch (Exception ex) when (ex is HistoricalFetchException or OperationCanceledException && !ct.IsCancellationRequested) { }
        }
        try
        {
            // A few blocks below the tip: the newest block's state is not always queryable yet.
            long hi = await LatestHeightAsync(ct) - 20;
            if (!await PoolExistsAsync(poolId, hi, ct)) return null;
            long lo = notBefore ?? (_earliestServed > 0 ? _earliestServed : 1);
            if (await PoolExistsAsync(poolId, lo, ct)) return notBefore is null ? lo : null;
            while (hi - lo > 1)
            {
                var mid = lo + (hi - lo) / 2;
                if (await PoolExistsAsync(poolId, mid, ct)) hi = mid; else lo = mid;
            }
            return hi;
        }
        catch (HistoricalFetchException) { return null; }
    }

    /// <summary>True when the pool exists at the height; false when not yet created; throws when the state is unavailable.</summary>
    private async Task<bool> PoolExistsAsync(string poolId, long height, CancellationToken ct)
    {
        var r = await GetAsync($"/osmosis/poolmanager/v1beta1/pools/{poolId}", height, ct);
        if (r.Status == HttpStatusCode.OK && r.ServedHeight == height) return true;
        if (r.Body.Contains("pruned", StringComparison.OrdinalIgnoreCase) || r.Body.Contains("version does not exist", StringComparison.OrdinalIgnoreCase))
            throw new HistoricalFetchException(Classify(r, height));
        if (r.Body.Contains("failed to find route for pool id", StringComparison.OrdinalIgnoreCase) || (r.Body.Contains("not found", StringComparison.OrdinalIgnoreCase) && r.Body.Contains("pool", StringComparison.OrdinalIgnoreCase)))
            return false;
        throw new HistoricalFetchException(Classify(r, height));
    }

    private async Task<long?> PoolCreatedEventHeightAsync(string poolId, CancellationToken ct)
    {
        foreach (var q in new[] { $"/cosmos/tx/v1beta1/txs?query={Uri.EscapeDataString($"pool_created.pool_id='{poolId}'")}&limit=1", $"/cosmos/tx/v1beta1/txs?events={Uri.EscapeDataString($"pool_created.pool_id='{poolId}'")}&limit=1" })
        {
            var r = await GetAsync(q, null, ct);
            if (r.Status == HttpStatusCode.OK && r.Json is not null && r.Json.RootElement.TryGetProperty("tx_responses", out var txs))
                foreach (var tx in txs.EnumerateArray())
                    return long.Parse(tx.GetProperty("height").GetString()!, CultureInfo.InvariantCulture);
        }
        return null;
    }

    public sealed record SwapValidation(string TxHash, long Height, string PoolId, string TokenIn, string AmountIn, string TokenOut, string ObservedOut, string? ModeledOut, decimal? Deviation, List<string> Notes);

    /// <summary>
    /// Replays an observed single-pool swap against the pool state at the previous block and compares output.
    /// Earlier transactions in the same block can move the pool, so the comparison carries intrablock uncertainty.
    /// </summary>
    public async Task<List<SwapValidation>> ValidateSwapAsync(string txHash, CancellationToken ct)
    {
        var r = await GetAsync($"/cosmos/tx/v1beta1/txs/{txHash}", null, ct);
        if (r.Status != HttpStatusCode.OK || r.Json is null) throw new HistoricalFetchException(Classify(r, 0));
        var resp = r.Json.RootElement.GetProperty("tx_response");
        var height = long.Parse(resp.GetProperty("height").GetString()!, CultureInfo.InvariantCulture);
        var results = new List<SwapValidation>();
        var swaps = resp.GetProperty("events").EnumerateArray().Where(e => e.GetProperty("type").GetString() == "token_swapped").ToList();
        foreach (var e in swaps)
        {
            var attrs = e.GetProperty("attributes").EnumerateArray().ToDictionary(a => a.GetProperty("key").GetString()!, a => a.GetProperty("value").GetString() ?? "");
            var poolId = attrs.GetValueOrDefault("pool_id", "");
            var (amtIn, denomIn) = SplitCoin(attrs.GetValueOrDefault("tokens_in", ""));
            var (amtOut, denomOut) = SplitCoin(attrs.GetValueOrDefault("tokens_out", ""));
            var notes = new List<string> { $"State taken at height {height - 1}; earlier transactions in block {height} may have moved the pool (intrablock uncertainty)." };
            if (swaps.Count > 1) notes.Add("Multi-hop transaction: each hop is replayed alone against the previous block's state.");
            string? modeled = null;
            decimal? deviation = null;
            try
            {
                var snap = await FetchPoolAsync(poolId, height - 1, ct);
                var hop = new RouteHop(snap, denomIn, denomOut);
                var unsupported = PoolRouteMarket.Unsupported(new[] { hop });
                if (unsupported != null) notes.Add(unsupported);
                else
                {
                    var tf = Fx.Dec(snap.TakerFee ?? "0");
                    // tokens_in on the event is the amount the pool received (after taker fee).
                    var inPool = System.Numerics.BigInteger.Parse(amtIn, CultureInfo.InvariantCulture);
                    System.Numerics.BigInteger outAmt;
                    if (snap.PoolType == "concentrated")
                    {
                        var st = new ClMath.State
                        {
                            SqrtPrice = Fx.BigDec(snap.CurrentSqrtPrice!), Tick = snap.CurrentTick!.Value, Liquidity = Fx.Dec(snap.CurrentLiquidity!),
                            Ticks = snap.Ticks.Select(x => new ClTick(x.Index, Fx.Dec(x.LiquidityNet))).ToList(), SpreadFactor = Fx.Dec(snap.SpreadFactor!),
                        };
                        // A CL pool's token_swapped event reports tokens_in after the spread factor is taken out
                        // (x/concentrated-liquidity/swaps.go 786–832), so find the gross input that nets to it.
                        var sf = Fx.Dec(snap.SpreadFactor!);
                        var gross0 = Fx.CeilDiv(inPool * Fx.D18, Fx.D18 - sf);
                        ClMath.SwapResult? match = null;
                        for (var g = gross0 - 3; g <= gross0 + 3 && match is null; g++)
                        {
                            var trial = ClMath.SwapExactIn(st, g, zeroForOne: denomIn == snap.Token0);
                            if (trial.AmountIn - Fx.DCeilInt(trial.SpreadFeesDec) == inPool) match = trial;
                        }
                        match ??= ClMath.SwapExactIn(st, gross0, zeroForOne: denomIn == snap.Token0);
                        notes.Add($"Event tokens_in is net of the {snap.SpreadFactor} spread factor; replayed with gross input {match.AmountIn}.");
                        outAmt = match.AmountOut;
                    }
                    else
                    {
                        var a = snap.Assets.First(x => x.Denom == denomIn);
                        var b = snap.Assets.First(x => x.Denom == denomOut);
                        outAmt = GammMath.BalancerOutGivenIn(System.Numerics.BigInteger.Parse(a.Amount), Fx.Dec(a.Weight ?? "1"), System.Numerics.BigInteger.Parse(b.Amount), Fx.Dec(b.Weight ?? "1"), inPool, Fx.Dec(snap.SwapFee ?? "0"));
                    }
                    modeled = outAmt.ToString(CultureInfo.InvariantCulture);
                    var obs = decimal.Parse(amtOut, CultureInfo.InvariantCulture);
                    deviation = obs == 0 ? null : ((decimal)outAmt - obs) / obs;
                    if (tf != 0) notes.Add($"Taker fee {snap.TakerFee} assumed already deducted from the event's tokens_in.");
                }
            }
            catch (Exception ex) when (ex is HistoricalFetchException or SwapFailedException or KeyNotFoundException or InvalidOperationException)
            {
                notes.Add("Not replayed: " + ex.Message);
            }
            results.Add(new SwapValidation(txHash, height, poolId, denomIn, amtIn, denomOut, amtOut, modeled, deviation, notes));
        }
        return results;
    }

    private static (string Amount, string Denom) SplitCoin(string coin)
    {
        var i = 0;
        while (i < coin.Length && char.IsDigit(coin[i])) i++;
        return (coin[..i], coin[i..]);
    }
}

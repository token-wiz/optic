using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using System.Text.Json;

namespace OPTIC.Services;

/// <summary>One UTC day of OPT trading on Coinstore and in the Osmosis pools.</summary>
public sealed class MarketDay
{
    public DateTime DateUtc { get; set; }
    public decimal? CoinstoreVolumeOpt { get; set; }
    public decimal? CoinstoreVolumeUsdt { get; set; }
    public decimal? CoinstoreCloseUsdt { get; set; }
    /// <summary>OPT sold into the pools, net per transaction (arbitrage that buys and sells in one tx cancels out).</summary>
    public decimal OsmosisSoldOpt { get; set; }
    /// <summary>OPT bought from the pools, net per transaction.</summary>
    public decimal OsmosisBoughtOpt { get; set; }
    /// <summary>Every OPT leg of every swap, including arbitrage routed through several OPT pools.</summary>
    public decimal OsmosisGrossOpt { get; set; }
    /// <summary>Volume-weighted OPT price from swaps against USDC/USDT that day.</summary>
    public decimal? OsmosisPriceUsd { get; set; }
    public int OsmosisTxs { get; set; }
}

public sealed record OsmosisPoolInfo(string PoolId, string Pair, decimal? LiquidityUsd, decimal? PriceUsd, decimal? Volume24hUsd, decimal? Volume7dUsd);
public sealed record SellQuote(decimal SellOpt, decimal? ReceiveUsd, decimal? AvgPriceUsd, decimal? PriceImpact, string? Route);
public sealed record CoinstoreBook(decimal? BestBid, decimal? BestAsk, decimal BidDepthOpt, decimal BidDepthUsdt, decimal AskDepthOpt, DateTime? LastTradeUtc, decimal? LastTradePrice);

/// <summary>Whether the big price sites and MEXC still list OPT.</summary>
public sealed class ListingStatus
{
    public DateTime FetchedUtc { get; set; }
    public string? CmcStatus { get; set; }
    public DateTime? CmcLastUpdated { get; set; }
    public int? CoinGeckoTickers { get; set; }
    public DateTime? CoinGeckoLastUpdated { get; set; }
    public bool? MexcListed { get; set; }
}

public sealed class MarketSnapshot
{
    public DateTime FetchedUtc { get; set; }
    public decimal? OsmosisSpotPriceUsd { get; set; }
    public List<OsmosisPoolInfo> Pools { get; set; } = new();
    public List<SellQuote> SellQuotes { get; set; } = new();
    public CoinstoreBook? Coinstore { get; set; }
    public List<string> Errors { get; set; } = new();
}

/// <summary>
/// Daily OPT trading volume from Coinstore's public kline API and from every OPT swap on Osmosis
/// (read from the Osmosis archive node; only daily totals are kept on disk), plus the current depth on both.
/// </summary>
public static class MarketVolumeService
{
    public const string OsmosisOptDenom = "ibc/6AA47B1A9F00CC9B127796717F4BE976C12EFC843387F22461152888E1365045";
    private const string NobleUsdcDenom = "ibc/498A0751C798A0D9A389AA3691123DADA57DAA4FE165D5C75894505B876BA6E4";
    // USD stablecoins on Osmosis (all 6 decimals), used to price swaps.
    private static readonly HashSet<string> StableDenoms = new(StringComparer.Ordinal)
    {
        NobleUsdcDenom,
        "factory/osmo147h5x9pcj7lm0cttlaefx6sqq5vdfnmwfcqxkmjd7exqm9gc7grqhr75m0/alloyed/allUSDC",
        "factory/osmo1em6xs47hd82806f5cxgyufguxrrc7l0aqx7nzzptjuqgswczk8csavdxek/alloyed/allUSDT",
    };
    // Known OPT pools, used when the pool list can't be fetched.
    private static readonly string[] FallbackPools = { "3278", "3329", "3391", "3400", "3565", "3571", "3572" };
    private const string ArchiveRpc = "https://rpc.archive.osmosis.zone";
    private const string Sqs = "https://sqs.osmosis.zone";
    private const string Numia = "https://public-osmosis-api.numia.xyz";
    private const string Coinstore = "https://api.coinstore.com/api/v1/market";
    private const decimal Micro = 1_000_000m;
    public static readonly decimal[] QuoteSizesOpt = { 10_000, 50_000, 100_000, 250_000, 500_000, 1_000_000, 2_500_000, 5_000_000 };

    private static readonly HttpClient Http = CreateHttpClient();
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly string CachePath = Path.Combine(Environment.CurrentDirectory, "odata", "osmosis-opt-daily.json");

    private static SwapCache? _swaps;
    private static DateTimeOffset _swapsRefreshedAt;
    private static List<(DateTime Day, decimal VolumeOpt, decimal VolumeUsdt, decimal Close)>? _coinstoreDays;
    private static DateTimeOffset _coinstoreLoadedAt;
    private static MarketSnapshot? _snapshot;

    public static (DateTime? From, DateTime? To, int Txs) OsmosisRange
    {
        get
        {
            var traded = _swaps?.Days.Where(d => d.Txs > 0).OrderBy(d => d.Date).ToList();
            return traded is { Count: > 0 } ? (traded[0].Date, traded[^1].Date, traded.Sum(d => d.Txs)) : (null, null, 0);
        }
    }

    /// <summary>Coinstore and Osmosis volume for every UTC day from the first Coinstore candle to today.</summary>
    public static async Task<List<MarketDay>> GetDailyAsync(CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            if (_coinstoreDays is null || DateTimeOffset.UtcNow - _coinstoreLoadedAt > TimeSpan.FromHours(6))
            {
                _coinstoreDays = await FetchCoinstoreDailyAsync(ct);
                _coinstoreLoadedAt = DateTimeOffset.UtcNow;
            }
            if (_swaps is null || DateTimeOffset.UtcNow - _swapsRefreshedAt > TimeSpan.FromHours(1))
            {
                _swaps = await RefreshOsmosisSwapsAsync(ct);
                _swapsRefreshedAt = DateTimeOffset.UtcNow;
            }

            var days = new SortedDictionary<DateTime, MarketDay>();
            MarketDay Day(DateTime d) => days.TryGetValue(d.Date, out var m) ? m : days[d.Date] = new MarketDay { DateUtc = d.Date };

            foreach (var c in _coinstoreDays)
            {
                var m = Day(c.Day);
                m.CoinstoreVolumeOpt = c.VolumeOpt;
                m.CoinstoreVolumeUsdt = c.VolumeUsdt;
                m.CoinstoreCloseUsdt = c.VolumeOpt > 0 ? c.Close : null;
            }

            foreach (var t in _swaps.Days)
            {
                var m = Day(t.Date);
                m.OsmosisTxs = t.Txs;
                m.OsmosisGrossOpt = t.GrossOpt;
                m.OsmosisSoldOpt = t.SoldOpt;
                m.OsmosisBoughtOpt = t.BoughtOpt;
                m.OsmosisPriceUsd = t.StableOpt > 0 ? t.StableUsd / t.StableOpt : null;
            }

            // Fill gaps so every calendar day up to today is present.
            if (days.Count > 0)
            {
                for (var d = days.Keys.First(); d <= DateTime.UtcNow.Date; d = d.AddDays(1))
                    Day(d);
            }
            return days.Values.ToList();
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Current Osmosis pools, router sell quotes and Coinstore order book (cached for 5 minutes).</summary>
    public static async Task<MarketSnapshot> GetSnapshotAsync(CancellationToken ct)
    {
        if (_snapshot is not null && DateTime.UtcNow - _snapshot.FetchedUtc < TimeSpan.FromMinutes(5))
            return _snapshot;

        var snap = new MarketSnapshot { FetchedUtc = DateTime.UtcNow };
        var pools = Task.Run(() => FetchPoolInfoAsync(snap, ct), ct);
        var quotes = Task.WhenAll(QuoteSizesOpt.Select(size => FetchSellQuoteAsync(size, snap, ct)));
        var book = Task.Run(() => FetchCoinstoreBookAsync(snap, ct), ct);
        var spot = Task.Run(() => FetchSpotPriceAsync(snap, ct), ct);
        await Task.WhenAll(pools, quotes, book, spot);

        snap.Pools = pools.Result;
        snap.SellQuotes = quotes.Result.ToList();
        snap.Coinstore = book.Result;
        snap.OsmosisSpotPriceUsd = spot.Result;
        _snapshot = snap;
        return snap;
    }

    private static ListingStatus? _listing;

    /// <summary>CoinMarketCap status, CoinGecko exchange listings and MEXC symbol check (cached 30 minutes).</summary>
    public static async Task<ListingStatus> GetListingStatusAsync(CancellationToken ct)
    {
        if (_listing is not null && DateTime.UtcNow - _listing.FetchedUtc < TimeSpan.FromMinutes(30))
            return _listing;

        var status = new ListingStatus { FetchedUtc = DateTime.UtcNow };
        var cmcTask = GetJsonAsync("https://api.coinmarketcap.com/data-api/v3/cryptocurrency/detail?id=35828", ct);
        var cgTask = GetJsonAsync("https://api.coingecko.com/api/v3/coins/optio?localization=false&tickers=true&market_data=false&community_data=false&developer_data=false", ct);
        var mexcTask = GetJsonAsync("https://api.mexc.com/api/v3/exchangeInfo?symbol=OPTUSDT", ct, acceptErrors: true);
        await Task.WhenAll(cmcTask, cgTask, mexcTask);

        static DateTime? Time(JsonElement el, string name) =>
            el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String &&
            DateTime.TryParse(v.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? t : null;

        using (var cmc = cmcTask.Result)
        {
            if (cmc is not null && cmc.RootElement.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object)
            {
                status.CmcStatus = d.TryGetProperty("status", out var st) ? st.GetString() : null;
                status.CmcLastUpdated = Time(d, "latestUpdateTime");
            }
        }
        using (var cg = cgTask.Result)
        {
            if (cg is not null && cg.RootElement.TryGetProperty("tickers", out var tickers))
            {
                status.CoinGeckoTickers = tickers.GetArrayLength();
                status.CoinGeckoLastUpdated = Time(cg.RootElement, "last_updated");
            }
        }
        using (var mexc = mexcTask.Result)
        {
            if (mexc is not null)
                status.MexcListed = mexc.RootElement.TryGetProperty("symbols", out var sy) && sy.ValueKind == JsonValueKind.Array && sy.GetArrayLength() > 0;
        }
        _listing = status;
        return status;
    }

    // ---------------------------------------------------------------- Coinstore

    private static async Task<List<(DateTime, decimal, decimal, decimal)>> FetchCoinstoreDailyAsync(CancellationToken ct)
    {
        var byDay = new SortedDictionary<long, (decimal Vol, decimal Amount, decimal Close)>();
        // Page backwards 200 days at a time from now; an empty page means no more history.
        // Coinstore returns nothing for windows with no trading, so keep stepping back until the
        // first candles turn up, then stop at the first empty page after them.
        var endMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var stopMs = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        for (var page = 0; page < 30 && endMs > stopMs; page++)
        {
            using var doc = await GetJsonAsync($"{Coinstore}/kline/OPTUSDT?period=1day&size=200&endTime={endMs}", ct);
            if (doc is null || !doc.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("item", out var items))
                break;
            if (items.GetArrayLength() == 0)
            {
                if (byDay.Count > 0)
                    break;
                endMs -= 200L * 86_400_000;
                continue;
            }

            long earliest = long.MaxValue;
            foreach (var it in items.EnumerateArray())
            {
                var start = it.GetProperty("startTime").GetInt64();
                earliest = Math.Min(earliest, start);
                byDay[start] = (ParseDecimal(it, "volume"), ParseDecimal(it, "amount"), ParseDecimal(it, "close"));
            }
            endMs = earliest * 1000 - 1;
        }

        return byDay.Select(kv => (DateTimeOffset.FromUnixTimeSeconds(kv.Key).UtcDateTime.Date, kv.Value.Vol, kv.Value.Amount, kv.Value.Close)).ToList();
    }

    private static async Task<CoinstoreBook?> FetchCoinstoreBookAsync(MarketSnapshot snap, CancellationToken ct)
    {
        try
        {
            using var depth = await GetJsonAsync($"{Coinstore}/depth/OPTUSDT?depth=100", ct);
            using var trades = await GetJsonAsync($"{Coinstore}/trade/OPTUSDT?size=1", ct);
            if (depth is null)
            {
                snap.Errors.Add("Coinstore order book unavailable.");
                return null;
            }

            var data = depth.RootElement.GetProperty("data");
            List<(decimal Price, decimal Qty)> Side(string name) => data.TryGetProperty(name, out var arr)
                ? arr.EnumerateArray().Select(l => (Dec(l[0]), Dec(l[1]))).ToList()
                : new();
            var bids = Side("b");
            var asks = Side("a");

            DateTime? lastTime = null;
            decimal? lastPrice = null;
            if (trades is not null && trades.RootElement.TryGetProperty("data", out var td) && td.ValueKind == JsonValueKind.Array && td.GetArrayLength() > 0)
            {
                var t = td[0];
                lastTime = DateTimeOffset.FromUnixTimeSeconds(t.GetProperty("time").GetInt64()).UtcDateTime;
                lastPrice = ParseDecimal(t, "price");
            }

            return new CoinstoreBook(
                bids.Count > 0 ? bids.Max(b => b.Price) : null,
                asks.Count > 0 ? asks.Min(a => a.Price) : null,
                bids.Sum(b => b.Qty),
                bids.Sum(b => b.Qty * b.Price),
                asks.Sum(a => a.Qty),
                lastTime,
                lastPrice);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            snap.Errors.Add("Coinstore order book: " + ex.Message);
            return null;
        }
    }

    // ---------------------------------------------------------------- Osmosis: current state

    private static async Task<List<string>> FetchOptPoolIdsAsync(CancellationToken ct)
    {
        using var doc = await GetJsonAsync($"{Numia}/pairs/v1/summary", ct);
        if (doc is null || !doc.RootElement.TryGetProperty("data", out var data))
            return FallbackPools.ToList();

        var ids = data.EnumerateArray()
            .Where(p => p.GetProperty("base_address").GetString() == OsmosisOptDenom || p.GetProperty("quote_address").GetString() == OsmosisOptDenom)
            .Select(p => p.GetProperty("pool_id").GetString() ?? "")
            .Where(id => id.Length > 0);
        return ids.Union(FallbackPools).Distinct().OrderBy(id => int.Parse(id, CultureInfo.InvariantCulture)).ToList();
    }

    private static async Task<List<OsmosisPoolInfo>> FetchPoolInfoAsync(MarketSnapshot snap, CancellationToken ct)
    {
        using var doc = await GetJsonAsync($"{Numia}/pairs/v1/summary", ct);
        if (doc is null || !doc.RootElement.TryGetProperty("data", out var data))
        {
            snap.Errors.Add("Osmosis pool list unavailable.");
            return new();
        }

        decimal? Num(JsonElement p, string name) =>
            p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? (decimal)v.GetDouble() : null;

        return data.EnumerateArray()
            .Where(p => p.GetProperty("base_address").GetString() == OsmosisOptDenom || p.GetProperty("quote_address").GetString() == OsmosisOptDenom)
            .Select(p =>
            {
                var optIsBase = p.GetProperty("base_address").GetString() == OsmosisOptDenom;
                var other = optIsBase ? p.GetProperty("quote_symbol").GetString() : p.GetProperty("base_symbol").GetString();
                return new OsmosisPoolInfo(
                    p.GetProperty("pool_id").GetString() ?? "",
                    "OPT / " + other,
                    Num(p, "liquidity"),
                    null,
                    Num(p, "volume_24h"),
                    Num(p, "volume_7d"));
            })
            .OrderByDescending(p => p.LiquidityUsd ?? 0)
            .ToList();
    }

    private static async Task<decimal?> FetchSpotPriceAsync(MarketSnapshot snap, CancellationToken ct)
    {
        using var doc = await GetJsonAsync($"{Sqs}/tokens/prices?base={Uri.EscapeDataString(OsmosisOptDenom)}&humanDenoms=false", ct);
        if (doc is null || !doc.RootElement.TryGetProperty(OsmosisOptDenom, out var quotes))
        {
            snap.Errors.Add("Osmosis spot price unavailable.");
            return null;
        }
        foreach (var q in quotes.EnumerateObject())
        {
            if (decimal.TryParse(q.Value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var price))
                return price;
        }
        return null;
    }

    /// <summary>Osmosis router URL quoting a sale of the given OPT for USDC.</summary>
    public static string SellQuoteUrl(decimal sizeOpt)
    {
        var amount = (Math.Round(sizeOpt) * Micro).ToString("0", CultureInfo.InvariantCulture);
        return $"{Sqs}/router/quote?tokenIn={amount}{Uri.EscapeDataString(OsmosisOptDenom)}&tokenOutDenom={Uri.EscapeDataString(NobleUsdcDenom)}&singleRoute=false";
    }

    /// <summary>What selling this much OPT on Osmosis right now would return.</summary>
    public static Task<SellQuote> QuoteSellAsync(decimal sizeOpt, CancellationToken ct) =>
        FetchSellQuoteAsync(Math.Round(sizeOpt), new MarketSnapshot(), ct);

    private static async Task<SellQuote> FetchSellQuoteAsync(decimal sizeOpt, MarketSnapshot snap, CancellationToken ct)
    {
        using var doc = await GetJsonAsync(SellQuoteUrl(sizeOpt), ct, acceptErrors: true);
        if (doc is null || !doc.RootElement.TryGetProperty("amount_out", out var outEl))
            return new SellQuote(sizeOpt, null, null, null, null);

        var receive = Dec(outEl) / Micro;
        decimal? impact = doc.RootElement.TryGetProperty("price_impact", out var imp) ? Dec(imp) : null;
        var routes = doc.RootElement.TryGetProperty("route", out var r)
            ? string.Join(" + ", r.EnumerateArray().Select(x => string.Join("→", x.GetProperty("pools").EnumerateArray().Select(p => p.GetProperty("id").GetRawText()))))
            : null;
        return new SellQuote(sizeOpt, receive, receive / sizeOpt, impact, routes);
    }

    // ---------------------------------------------------------------- Osmosis: swap history

    private sealed class SwapTx
    {
        public long Height { get; set; }
        public DateTime TimeUtc { get; set; }
        public decimal OptIn { get; set; }
        public decimal OptOut { get; set; }
        public decimal GrossOpt { get; set; }
        public decimal StableUsd { get; set; }
        public decimal StableOpt { get; set; }
        /// <summary>Every pool the tx swapped through, so a newly found pool's history can skip txs already counted.</summary>
        public HashSet<string> Pools { get; } = new();
    }

    /// <summary>One UTC day of Osmosis OPT swaps, already totalled; this is all that is kept on disk.</summary>
    private sealed class SwapDay
    {
        public DateTime Date { get; set; }
        public int Txs { get; set; }
        public decimal SoldOpt { get; set; }
        public decimal BoughtOpt { get; set; }
        public decimal GrossOpt { get; set; }
        public decimal StableUsd { get; set; }
        public decimal StableOpt { get; set; }
    }

    private sealed class SwapCache
    {
        /// <summary>Every swap at or below this height is already in Days.</summary>
        public long ScannedToHeight { get; set; }
        /// <summary>Pools whose history up to ScannedToHeight is in Days.</summary>
        public List<string> Pools { get; set; } = new();
        public List<SwapDay> Days { get; set; } = new();
    }

    /// <summary>
    /// Reads only swaps in the OPT pools that are newer than the last scan (plus the full history of any
    /// OPT pool not seen before) and adds them to the daily totals. Individual swaps aren't kept.
    /// </summary>
    private static async Task<SwapCache> RefreshOsmosisSwapsAsync(CancellationToken ct)
    {
        var cache = _swaps ?? LoadCache();
        var head = await GetLatestHeightAsync(ct);
        if (head is null || head <= cache.ScannedToHeight)
            return cache;

        var pools = await FetchOptPoolIdsAsync(ct);
        var knownPools = new HashSet<string>(cache.Pools);
        var seen = new HashSet<string>(StringComparer.Ordinal); // hashes in this run only, for txs that touch several OPT pools
        var fresh = new List<SwapTx>();

        foreach (var pool in pools)
        {
            var isNew = !knownPools.Contains(pool);
            var from = isNew ? 0 : cache.ScannedToHeight;
            var txs = await SearchPoolSwapsAsync(pool, from, head.Value, ct);
            if (txs is null)
                return cache; // incomplete; keep the old totals and try again next time
            foreach (var (hash, swap) in txs)
            {
                if (!seen.Add(hash))
                    continue;
                // Older txs in a new pool that also went through a known pool were counted already.
                if (isNew && swap.Height <= cache.ScannedToHeight && swap.Pools.Any(knownPools.Contains))
                    continue;
                fresh.Add(swap);
            }
        }

        await ResolveTimesAsync(fresh, ct);
        if (fresh.Any(t => t.TimeUtc == default))
            return cache; // couldn't date every swap; try again next time rather than miscount

        var byDay = cache.Days.ToDictionary(d => d.Date);
        foreach (var t in fresh)
        {
            var date = t.TimeUtc.Date;
            if (!byDay.TryGetValue(date, out var d))
                byDay[date] = d = new SwapDay { Date = date };
            d.Txs++;
            d.GrossOpt += t.GrossOpt;
            var net = t.OptIn - t.OptOut;
            if (net > 0) d.SoldOpt += net; else d.BoughtOpt -= net;
            d.StableUsd += t.StableUsd;
            d.StableOpt += t.StableOpt;
        }

        var updated = new SwapCache
        {
            ScannedToHeight = head.Value,
            Pools = pools,
            Days = byDay.Values.OrderBy(d => d.Date).ToList(),
        };
        SaveCache(updated);
        return updated;
    }

    /// <summary>Swaps in one pool with from &lt; height &lt;= to, or null if a page failed.</summary>
    private static async Task<List<(string Hash, SwapTx Swap)>?> SearchPoolSwapsAsync(string pool, long from, long to, CancellationToken ct)
    {
        var output = new List<(string, SwapTx)>();
        for (var page = 1; page <= 200; page++)
        {
            var query = Uri.EscapeDataString($"token_swapped.pool_id='{pool}' AND tx.height>{from} AND tx.height<={to}");
            using var doc = await GetJsonAsync($"{ArchiveRpc}/tx_search?query=\"{query}\"&page={page}&per_page=100&order_by=\"asc\"", ct);
            if (doc is null || !doc.RootElement.TryGetProperty("result", out var result))
                return null;

            var txs = result.GetProperty("txs");
            foreach (var tx in txs.EnumerateArray())
            {
                var height = long.Parse(tx.GetProperty("height").GetString()!, CultureInfo.InvariantCulture);
                var swap = ParseSwapTx(tx, height);
                if (swap is not null)
                    output.Add((tx.GetProperty("hash").GetString()!, swap));
            }

            var total = int.Parse(result.GetProperty("total_count").GetString()!, CultureInfo.InvariantCulture);
            if (page * 100 >= total || txs.GetArrayLength() == 0)
                break;
        }
        return output;
    }

    private static async Task<long?> GetLatestHeightAsync(CancellationToken ct)
    {
        using var doc = await GetJsonAsync($"{ArchiveRpc}/status", ct);
        return doc is not null && doc.RootElement.TryGetProperty("result", out var r) &&
               long.TryParse(r.GetProperty("sync_info").GetProperty("latest_block_height").GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var h)
            ? h
            : null;
    }

    private static SwapTx? ParseSwapTx(JsonElement tx, long height)
    {
        var result = tx.GetProperty("tx_result");
        if (result.TryGetProperty("code", out var code) && code.GetInt32() != 0)
            return null;

        var swap = new SwapTx { Height = height };
        foreach (var ev in result.GetProperty("events").EnumerateArray())
        {
            if (ev.GetProperty("type").GetString() != "token_swapped")
                continue;
            string? tokensIn = null, tokensOut = null, poolId = null;
            foreach (var a in ev.GetProperty("attributes").EnumerateArray())
            {
                var key = a.GetProperty("key").GetString();
                if (key == "tokens_in") tokensIn = a.GetProperty("value").GetString();
                else if (key == "tokens_out") tokensOut = a.GetProperty("value").GetString();
                else if (key == "pool_id") poolId = a.GetProperty("value").GetString();
            }
            var (inAmt, inDenom) = ParseCoin(tokensIn);
            var (outAmt, outDenom) = ParseCoin(tokensOut);

            if ((inDenom == OsmosisOptDenom || outDenom == OsmosisOptDenom) && poolId is not null)
                swap.Pools.Add(poolId);
            if (inDenom == OsmosisOptDenom)
            {
                swap.OptIn += inAmt;
                swap.GrossOpt += inAmt;
                if (StableDenoms.Contains(outDenom)) { swap.StableUsd += outAmt; swap.StableOpt += inAmt; }
            }
            else if (outDenom == OsmosisOptDenom)
            {
                swap.OptOut += outAmt;
                swap.GrossOpt += outAmt;
                if (StableDenoms.Contains(inDenom)) { swap.StableUsd += inAmt; swap.StableOpt += outAmt; }
            }
        }
        return swap.GrossOpt > 0 ? swap : null;
    }

    private static (decimal Amount, string Denom) ParseCoin(string? coin)
    {
        if (string.IsNullOrEmpty(coin))
            return (0, "");
        var i = 0;
        while (i < coin.Length && char.IsDigit(coin[i]))
            i++;
        return i == 0 ? (0, coin) : ((decimal)BigInteger.Parse(coin[..i], CultureInfo.InvariantCulture) / Micro, coin[i..]);
    }

    private static async Task ResolveTimesAsync(List<SwapTx> txs, CancellationToken ct)
    {
        var times = new ConcurrentDictionary<long, DateTime>();
        await Parallel.ForEachAsync(txs.Select(t => t.Height).Distinct(),
            new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = ct },
            async (height, token) =>
            {
                using var doc = await GetJsonAsync($"{ArchiveRpc}/header?height={height}", token);
                if (doc is not null && doc.RootElement.TryGetProperty("result", out var r) &&
                    DateTime.TryParse(r.GetProperty("header").GetProperty("time").GetString(), CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var time))
                    times[height] = time;
            });
        foreach (var t in txs)
            t.TimeUtc = times.TryGetValue(t.Height, out var time) ? time : default;
    }

    private static SwapCache LoadCache()
    {
        try
        {
            if (File.Exists(CachePath))
                return JsonSerializer.Deserialize<SwapCache>(File.ReadAllText(CachePath)) ?? new SwapCache();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ignoring unreadable Osmosis swap cache: {ex.Message}");
        }
        return new SwapCache();
    }

    private static void SaveCache(SwapCache cache)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            var tmp = CachePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(cache));
            File.Move(tmp, CachePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not save Osmosis swap cache: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- helpers

    private static async Task<JsonDocument?> GetJsonAsync(string url, CancellationToken ct, bool acceptErrors = false)
    {
        var delayMs = 500;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var resp = await Http.GetAsync(url, ct);
                if ((int)resp.StatusCode == 429 || (int)resp.StatusCode >= 500)
                {
                    await Task.Delay(delayMs, ct);
                    delayMs *= 2;
                    continue;
                }
                if (!resp.IsSuccessStatusCode && !acceptErrors)
                    return null;
                return JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                if (attempt == 3)
                    return null;
                await Task.Delay(delayMs, ct);
                delayMs *= 2;
            }
        }
        return null;
    }

    private static decimal ParseDecimal(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) ? Dec(v) : 0m;

    private static decimal Dec(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Number => (decimal)v.GetDouble(),
        JsonValueKind.String when decimal.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
        _ => 0m,
    };

    private static HttpClient CreateHttpClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return http;
    }
}

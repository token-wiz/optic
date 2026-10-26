// Chains > TITN growth indicators, read from the chain:
//   - Fee revenue: USDC paid into THORWallet's fee splitter (ERC20Splitter3Way), per day, and how it was shared out:
//     50% to TITN stakers (the staking contract), 45% to a THORWallet wallet, 5% to the raffle (RafflePayout).
//   - Total staked: TITN in the staking contract over time.
//   - Holders: wallets holding at least 1 TITN on Base at the end of each day. TITN a wallet has staked still counts as
//     its own (transfers to and from the staking contract are left out), so stakers aren't dropped from the count.
// Everything is cached in odata/evm/titn-growth.json and brought up to date in the background at most once an hour.
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

public static partial class WebDashboard
{
    const string TitnFeeSplitter = "0x76a3b8fea6f6c7d7af5b47885563955f3acd9bc2";
    const string TitnFeeTreasury = "0x1da31de63085794f1ce317ed7da7c0167da862bf";
    const string TitnRafflePayout = "0x583ecd462e6a8f3bbba71ec3a3aab53a7b6c01c0";
    const long TitnCreatedBlock = 32901839;
    static readonly BigInteger TitnHolderMin = BigInteger.Pow(10, 18); // 1 TITN
    static readonly SemaphoreSlim TitnGrowthGate = new(1, 1);
    static string TitnGrowthPath => Path.Combine(Environment.CurrentDirectory, "odata", "evm", "titn-growth.json");

    sealed class TitnGrowthCache
    {
        public DateTime UpdatedUtc { get; set; }
        public Dictionary<string, Dictionary<string, decimal>> Fees { get; set; } = new();
        public Dictionary<string, decimal> StakedNet { get; set; } = new();
        // USDC paid into the splitter per day, by the address it came from (how many payers, which ones dominate).
        public Dictionary<string, Dictionary<string, decimal>> FeeSources { get; set; } = new();
        // USDC of THORWallet's operating wallet and of the treasury Safe: per day, in and out; and each payment out (over $1).
        public Dictionary<string, Dictionary<string, decimal>> Ops { get; set; } = new();
        public Dictionary<string, Dictionary<string, decimal>> Safe { get; set; } = new();
        public List<TitnPayment> OpsPayments { get; set; } = new();
        public TitnHolderState Holders { get; set; } = new();
    }
    sealed record TitnPayment(DateTime Time, string Wallet, string To, decimal Amount);

    sealed class TitnHolderState
    {
        public long Next { get; set; }
        public List<string> Seen { get; set; } = new();
        public Dictionary<string, string> Balances { get; set; } = new();
        public Dictionary<string, int> Days { get; set; } = new();
        public int Count { get; set; }
    }

    static TitnGrowthCache? LoadTitnGrowth()
    {
        try { return File.Exists(TitnGrowthPath) ? JsonSerializer.Deserialize<TitnGrowthCache>(File.ReadAllText(TitnGrowthPath)) : null; }
        catch (JsonException) { return null; }
    }

    // ERC-20 transfers of one token for an address, newest first, back to the start of the given day (all when null).
    static async Task<List<(DateTime Time, string From, string To, decimal Amount)>> TokenTransfersSinceAsync(string address, string token, int decimals, string? sinceDay, CancellationToken ct)
    {
        var basePath = $"{EvmExplorersAll["base"]}/addresses/{address}/token-transfers?type=ERC-20&token={token}";
        var list = new List<(DateTime, string, string, decimal)>();
        var url = basePath;
        for (var page = 0; page < 2000; page++)
        {
            using var doc = JsonDocument.Parse(await EvmHttp.GetStringAsync(url, ct));
            var older = false;
            foreach (var x in doc.RootElement.GetProperty("items").EnumerateArray())
            {
                var time = ParseUtc(x.GetProperty("timestamp").GetString()!);
                if (sinceDay is not null && string.CompareOrdinal(time.ToString("yyyy-MM-dd"), sinceDay) < 0) { older = true; break; }
                var amount = (decimal)(double.Parse(x.GetProperty("total").GetProperty("value").GetString()!, CultureInfo.InvariantCulture) / Math.Pow(10, decimals));
                list.Add((time, x.GetProperty("from").GetProperty("hash").GetString()!.ToLowerInvariant(), x.GetProperty("to").GetProperty("hash").GetString()!.ToLowerInvariant(), amount));
            }
            if (older || !doc.RootElement.TryGetProperty("next_page_params", out var next) || next.ValueKind != JsonValueKind.Object) break;
            url = basePath + "&" + string.Join("&", next.EnumerateObject().Select(p => p.Name + "=" + Uri.EscapeDataString(p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText())));
            await Task.Delay(400, ct);
        }
        return list;
    }

    // Applies TITN transfer logs from Blockscout's logs API (1,000 per call) to the holder balances, from Holders.Next on.
    static async Task UpdateTitnHoldersAsync(TitnHolderState h, CancellationToken ct)
    {
        if (h.Next < 0) return; // a history import is pending (odata/evm/titn-growth.json written by hand)
        if (h.Next == 0) h.Next = TitnCreatedBlock;
        var api = EvmExplorersAll["base"].Replace("/api/v2", "/api");
        const string topic = "0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef";
        var lastDay = h.Days.Keys.DefaultIfEmpty("").Max()!;
        for (var call = 0; call < 2000; call++)
        {
            JsonElement rows = default;
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    using var doc = JsonDocument.Parse(await EvmHttp.GetStringAsync($"{api}?module=logs&action=getLogs&fromBlock={h.Next}&toBlock=latest&address={TitnToken}&topic0={topic}", ct));
                    if (doc.RootElement.GetProperty("result") is { ValueKind: JsonValueKind.Array } r) { rows = r.Clone(); break; }
                    throw new HttpRequestException("Unexpected answer from the explorer.");
                }
                catch (HttpRequestException) when (attempt < 5) { await Task.Delay(TimeSpan.FromSeconds(10 + attempt * 15), ct); }
            }
            var seen = h.Seen.ToHashSet();
            var fresh = 0;
            foreach (var r in rows.EnumerateArray())
            {
                var key = r.GetProperty("transactionHash").GetString() + r.GetProperty("logIndex").GetString();
                if (seen.Contains(key)) continue;
                fresh++;
                var day = DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(r.GetProperty("timeStamp").GetString(), 16)).UtcDateTime.ToString("yyyy-MM-dd");
                if (day != lastDay) { if (lastDay != "") h.Days[lastDay] = h.Count; lastDay = day; }
                var topics = r.GetProperty("topics");
                var from = "0x" + topics[1].GetString()![^40..];
                var to = "0x" + topics[2].GetString()![^40..];
                if (from == TitnStakingContract || to == TitnStakingContract) continue; // staked TITN stays its owner's
                var value = BigInteger.Parse("0" + r.GetProperty("data").GetString()![2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                void Move(string a, BigInteger delta)
                {
                    if (a == "0x0000000000000000000000000000000000000000") return;
                    var before = h.Balances.TryGetValue(a, out var s) ? BigInteger.Parse(s, CultureInfo.InvariantCulture) : BigInteger.Zero;
                    var after = before + delta;
                    if (before >= TitnHolderMin && after < TitnHolderMin) h.Count--;
                    if (before < TitnHolderMin && after >= TitnHolderMin) h.Count++;
                    if (after.IsZero) h.Balances.Remove(a); else h.Balances[a] = after.ToString(CultureInfo.InvariantCulture);
                }
                Move(from, -value);
                Move(to, value);
            }
            if (lastDay != "") h.Days[lastDay] = h.Count;
            var arr = rows.EnumerateArray().ToList();
            if (arr.Count < 1000) { if (arr.Count > 0) { var lb = Convert.ToInt64(arr[^1].GetProperty("blockNumber").GetString(), 16); h.Seen = arr.Where(r => Convert.ToInt64(r.GetProperty("blockNumber").GetString(), 16) == lb).Select(r => r.GetProperty("transactionHash").GetString() + r.GetProperty("logIndex").GetString()).ToList(); h.Next = lb; } break; }
            var last = Convert.ToInt64(arr[^1].GetProperty("blockNumber").GetString(), 16);
            if (fresh == 0) last++;
            h.Seen = arr.Where(r => Convert.ToInt64(r.GetProperty("blockNumber").GetString(), 16) == last).Select(r => r.GetProperty("transactionHash").GetString() + r.GetProperty("logIndex").GetString()).ToList();
            h.Next = last;
            await Task.Delay(1500, ct);
        }
    }

    static async Task RefreshTitnGrowthAsync(CancellationToken ct)
    {
        if (!await TitnGrowthGate.WaitAsync(0, ct)) return; // already running
        try
        {
            var c = LoadTitnGrowth() ?? new TitnGrowthCache();
            // Fees: the last cached day is fetched again (it may have been partial), with everything after it.
            var feeFrom = c.Fees.Keys.DefaultIfEmpty("").Max() is { Length: > 0 } fd ? fd : null;
            var fees = await TokenTransfersSinceAsync(TitnFeeSplitter, BaseUsdc, 6, feeFrom, ct);
            foreach (var g in fees.GroupBy(f => f.Time.ToString("yyyy-MM-dd")))
            {
                var d = new Dictionary<string, decimal> { ["in"] = 0, ["stakers"] = 0, ["treasury"] = 0, ["raffle"] = 0, ["other"] = 0, ["n"] = 0 };
                foreach (var f in g)
                {
                    if (f.To == TitnFeeSplitter) { d["in"] += f.Amount; d["n"]++; }
                    else if (f.From == TitnFeeSplitter)
                        d[f.To == TitnStakingContract ? "stakers" : f.To == TitnFeeTreasury ? "treasury" : f.To == TitnRafflePayout ? "raffle" : "other"] += f.Amount;
                }
                c.Fees[g.Key] = d.ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value, 2));
                c.FeeSources[g.Key] = g.Where(f => f.To == TitnFeeSplitter).GroupBy(f => f.From).ToDictionary(x => x.Key, x => Math.Round(x.Sum(f => f.Amount), 2));
            }
            // The operating wallet and the treasury Safe: USDC in and out per day (spam transfers under $1 left out).
            foreach (var (wallet, store) in new[] { (TitnFeeTreasury, c.Ops), (TitnTreasurySafe, c.Safe) })
            {
                var from = store.Keys.DefaultIfEmpty("").Max() is { Length: > 0 } od ? od : null;
                var moves = (await TokenTransfersSinceAsync(wallet, BaseUsdc, 6, from, ct)).Where(m => m.Amount >= 1m).ToList();
                foreach (var g in moves.GroupBy(m => m.Time.ToString("yyyy-MM-dd")))
                    store[g.Key] = new Dictionary<string, decimal>
                    {
                        ["in"] = Math.Round(g.Where(m => m.To == wallet).Sum(m => m.Amount), 2),
                        ["fees"] = Math.Round(g.Where(m => m.To == wallet && m.From == TitnFeeSplitter).Sum(m => m.Amount), 2),
                        ["out"] = Math.Round(g.Where(m => m.From == wallet).Sum(m => m.Amount), 2),
                    };
                if (wallet == TitnFeeTreasury)
                {
                    var cut = from is null ? DateTime.MinValue : DateTime.ParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                    c.OpsPayments.RemoveAll(p => p.Time >= cut);
                    c.OpsPayments.AddRange(moves.Where(m => m.From == wallet).Select(m => new TitnPayment(m.Time, wallet, m.To, Math.Round(m.Amount, 2))));
                    c.OpsPayments = c.OpsPayments.OrderByDescending(p => p.Time).ToList();
                }
            }
            // Staked: TITN into the staking contract minus TITN out, per day.
            var stFrom = c.StakedNet.Keys.DefaultIfEmpty("").Max() is { Length: > 0 } sd ? sd : null;
            var staked = await TokenTransfersSinceAsync(TitnStakingContract, TitnToken, 18, stFrom, ct);
            foreach (var g in staked.GroupBy(f => f.Time.ToString("yyyy-MM-dd")))
                c.StakedNet[g.Key] = Math.Round(g.Sum(f => f.To == TitnStakingContract ? f.Amount : -f.Amount), 2);
            await UpdateTitnHoldersAsync(c.Holders, ct);
            c.UpdatedUtc = DateTime.UtcNow;
            Directory.CreateDirectory(Path.GetDirectoryName(TitnGrowthPath)!);
            File.WriteAllText(TitnGrowthPath, JsonSerializer.Serialize(c));
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Console.WriteLine($"TITN growth refresh failed: {ex.Message}"); }
        finally { TitnGrowthGate.Release(); }
    }

    static void MapTitnGrowth(WebApplication app)
    {
        // Answers from the cache at once; an hour-old cache is brought up to date in the background.
        app.MapGet("/api/titn/growth", () =>
        {
            var c = LoadTitnGrowth();
            if (c is null || DateTime.UtcNow - c.UpdatedUtc > TimeSpan.FromHours(1))
                _ = Task.Run(() => RefreshTitnGrowthAsync(CancellationToken.None));
            if (c is null) return VJson(new { building = true });
            // Total staked: daily net flows added up, anchored so today matches the contract's live balance.
            var watch = LoadTitnWatch();
            var live = watch?.Live.GetValueOrDefault("supply")?["staking"] is { } sv ? Dec(sv) : 0m;
            // Balances over time: daily net flows added up, anchored to today's live balance.
            object Balances(Dictionary<string, Dictionary<string, decimal>> store, decimal now)
            {
                var ks = store.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
                var total = ks.Sum(k => store[k].GetValueOrDefault("in") - store[k].GetValueOrDefault("out"));
                var b = now - total; var list = new List<object>();
                foreach (var k in ks) { b += store[k].GetValueOrDefault("in") - store[k].GetValueOrDefault("out"); list.Add(new { d = k, @in = store[k].GetValueOrDefault("in"), fees = store[k].GetValueOrDefault("fees"), @out = store[k].GetValueOrDefault("out"), balance = Math.Round(b, 2) }); }
                return list;
            }
            var cash = watch?.Live.GetValueOrDefault("cash");
            var days = c.StakedNet.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
            var run = 0m; var cum = new List<(string D, decimal T)>();
            foreach (var d in days) { run += c.StakedNet[d]; cum.Add((d, run)); }
            var shift = live > 0 && cum.Count > 0 ? live - run : 0m;
            return VJson(new
            {
                updatedUtc = c.UpdatedUtc,
                fees = c.Fees.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => new { d = kv.Key, @in = kv.Value.GetValueOrDefault("in"), stakers = kv.Value.GetValueOrDefault("stakers"), treasury = kv.Value.GetValueOrDefault("treasury"), raffle = kv.Value.GetValueOrDefault("raffle"), n = (int)kv.Value.GetValueOrDefault("n") }),
                staked = cum.Select(x => new { d = x.D, total = Math.Round(x.T + shift) }),
                holders = c.Holders.Days.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => new { d = kv.Key, count = kv.Value }),
                holdersNow = c.Holders.Count,
                explorerHolders = (LoadTitnWatch()?.HoldersDaily ?? new()).OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => new { d = kv.Key, @base = kv.Value.GetValueOrDefault("base"), arbitrum = kv.Value.GetValueOrDefault("arbitrum") }),
                splitter = TitnFeeSplitter,
                split = watch?.Live.GetValueOrDefault("split"),
                feeSources = c.FeeSources.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => new { d = kv.Key, src = kv.Value }),
                ops = new { address = TitnFeeTreasury, balance = Dec(cash?["ops"]), days = Balances(c.Ops, Dec(cash?["ops"])), payments = c.OpsPayments.Take(200) },
                safe = new { address = TitnTreasurySafe, balance = Dec(cash?["safe"]), days = Balances(c.Safe, Dec(cash?["safe"])) },
            });
        });
    }
}

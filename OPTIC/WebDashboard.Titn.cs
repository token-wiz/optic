using OPTIC.Services;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

// THORWallet (TITN) holdings and rewards of the user's saved Ethereum-style wallets (Base and Arbitrum).
//   - Rewards are payments from THORWallet's reward contracts: USDC from TitnStaking (Base) and from the older
//     TGTStakingBasic (Arbitrum), TITN airdrop awards via LiquidRewards (Base) and USDC from RafflePayout (Base). Each one is
//     ordinary income when received: USDC at $1, TITN at that day's DefiLlama price.
//   - TITN and TGT coming back from a staking contract is your own stake returning, not income.
//   - Anything else received is listed as "not classified" and never counted.
// Transfers come from Blockscout and are cached in odata/evm/<chain>-<address>.json; staking balances and pending
// rewards are read live from the TitnStaking contract.
public static partial class WebDashboard
{
    static readonly Regex EvmAddress = new("^0x[0-9a-f]{40}$", RegexOptions.Compiled);
    static readonly Dictionary<string, string> EvmExplorers = new()
    {
        ["base"] = "https://base.blockscout.com/api/v2",
        ["arbitrum"] = "https://arbitrum.blockscout.com/api/v2",
    };
    const string BaseRpc = "https://mainnet.base.org";
    const string TitnToken = "0xe62bfbe57763ec24c0f130426f34dbce11fc5b06";
    const string TitnStakingContract = "0x269e9c0e300dd6c84a38b5781551bf767b2b5327";
    const string BaseUsdc = "0x833589fcd6edb6e08f4c7c32d4f71b54bda02913";

    sealed record RewardSource(string Chain, string Name, string Kind);
    static readonly Dictionary<string, RewardSource> TitnRewardSources = new()
    {
        [TitnStakingContract] = new("base", "TITN staking", "staking"),
        ["0x6745c897ab1f4fda9f7700e8be6ea2ee03672759"] = new("arbitrum", "TGT staking (before the TITN merge)", "staking"),
        ["0x6d3261c6e1cb05e0fd0132b6d962ca8fabb109ac"] = new("base", "TITN airdrop award (LiquidRewards)", "airdrop"),
        ["0x583ecd462e6a8f3bbba71ec3a3aab53a7b6c01c0"] = new("base", "THORWallet raffle prize", "prize"),
    };
    static readonly HashSet<string> EvmStablecoins = new()
    {
        BaseUsdc, "0xaf88d065e77c8cc2239327c5edb3a432268e5831", "0xff970a61a04b1ca14834a43f5de4533ebddb5cc8", "0xfd086bc7cd5c481dcc9c85ebe478a1c0b69fcbb9",
    };
    static readonly Dictionary<string, string> KnownSenderNames = new()
    {
        ["0xc673b2ae9b37bd407078fe3de55c044882880efa"] = "MoonPay",
        ["0x4a9a16755a6bf64291cd2c9eb027b39c129ff58d"] = "MoonPay",
        ["0x2cff890f0378a11913b6129b2e97417a2c302680"] = "Cross-chain solver",
    };
    // Contracts whose transfers are trades, liquidity or the TGT→TITN migration, not unexplained receipts.
    static readonly HashSet<string> TitnKnownContracts = new()
    {
        "0xfa486b0e67ca52f5f9e5adf0b79b6cfeb339b6e0", // MergeTgt: TGT→TITN migration (Arbitrum)
        "0x50b5d6f6accdcc7dc048de35b2b9ba5fdd5a283e", // Aerodrome TITN/USDC pool (your liquidity position)
        "0x111111125421ca6dc452d289314280a0f8842a65", // 1inch AggregationRouterV6
        "0x68208d99746b805a1ae41421950a47b711e35681", // THORChain router (Base)
        "0x1c7f7e0258c81cf41bcea31ea4bb5191914bf7d7", // swap proxy (Stargate swap change)
    };
    static readonly HashSet<string> TitnPrincipalTokens = new() { TitnToken, "0x429fed88f10285e61b12bdf00848315fbdfcc341" /* TGT on Arbitrum */ };

    static readonly HttpClient EvmHttp = CreateEvmHttp();
    static readonly SemaphoreSlim EvmGate = new(1, 1);

    static HttpClient CreateEvmHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) OPTIC/1.0");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return http;
    }

    // Tokens shown per network on My Wallets (USDC is Circle's native USDC; USDC.e is Arbitrum's bridged USDC).
    static readonly (string Chain, string Token, string Symbol)[] EvmBalanceTokens =
    {
        ("base", BaseUsdc, "USDC"), ("ethereum", "0xa0b86991c6218b36c1d19d4a2e9eb0ce3606eb48", "USDC"),
        ("arbitrum", "0xaf88d065e77c8cc2239327c5edb3a432268e5831", "USDC"), ("arbitrum", "0xff970a61a04b1ca14834a43f5de4533ebddb5cc8", "USDC.e"),
        ("base", TitnToken, "TITN"), ("arbitrum", TitnToken, "TITN"),
        ("arbitrum", "0x429fed88f10285e61b12bdf00848315fbdfcc341", "TGT"), ("ethereum", "0x108a850856db3f85d0269a2693d896b394c80325", "TGT"),
    };

    // Balances of ETH, USDC, TITN and TGT on Ethereum, Arbitrum and Base for the user's saved 0x... wallets (10-minute cache).
    static async Task<object> BuildEvmBalancesAsync(List<(string Address, string Label)> wallets, CancellationToken ct)
    {
        var ethPx = await LoadDailyPricesAsync("coingecko:ethereum", false, ct);
        var titnPx = await LoadDailyPricesAsync(TitnPriceKey, false, ct);
        var tgtPx = await LoadDailyPricesAsync(TgtPriceKey, false, ct);
        decimal Last(SortedDictionary<string, decimal> s) => s.Count > 0 ? s.Last().Value : 0m;
        var px = new Dictionary<string, decimal> { ["ETH"] = Last(ethPx), ["USDC"] = 1m, ["USDC.e"] = 1m, ["TITN"] = Last(titnPx), ["TGT"] = Last(tgtPx) };
        var result = new List<object>();
        foreach (var w in wallets)
        {
            var path = Path.Combine(Environment.CurrentDirectory, "odata", "evm", $"balances-{w.Address}.json");
            JsonObject cached;
            if (File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < TimeSpan.FromMinutes(10))
                cached = JsonNode.Parse(await File.ReadAllTextAsync(path, ct))!.AsObject();
            else
            {
                cached = new JsonObject();
                foreach (var chain in EvmExplorersAll.Keys)
                {
                    var row = new JsonObject();
                    try
                    {
                        using var a = JsonDocument.Parse(await EvmHttp.GetStringAsync($"{EvmExplorersAll[chain]}/addresses/{w.Address}", ct));
                        row["ETH"] = a.RootElement.TryGetProperty("coin_balance", out var cb) && cb.ValueKind == JsonValueKind.String ? Scale(BigInteger.Parse(cb.GetString()!, CultureInfo.InvariantCulture), 18) : 0m;
                    }
                    catch (HttpRequestException) { row["ETH"] = 0m; }
                    try
                    {
                        using var tb = JsonDocument.Parse(await EvmHttp.GetStringAsync($"{EvmExplorersAll[chain]}/addresses/{w.Address}/token-balances", ct));
                        foreach (var b in tb.RootElement.EnumerateArray())
                        {
                            var token = b.GetProperty("token");
                            var addr = (token.TryGetProperty("address_hash", out var ah) ? ah : token.GetProperty("address")).GetString()!.ToLowerInvariant();
                            var match = EvmBalanceTokens.FirstOrDefault(t => t.Chain == chain && t.Token == addr);
                            if (match.Symbol is null) continue;
                            var dec = token.TryGetProperty("decimals", out var dd) && dd.ValueKind == JsonValueKind.String ? int.Parse(dd.GetString()!, CultureInfo.InvariantCulture) : 18;
                            row[match.Symbol] = Scale(BigInteger.Parse(b.GetProperty("value").GetString()!, CultureInfo.InvariantCulture), dec);
                        }
                    }
                    catch (HttpRequestException) { }
                    cached[chain] = row;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, cached.ToJsonString(), ct);
            }
            var chains = cached.Select(kv => new
            {
                chain = kv.Key,
                balances = kv.Value!.AsObject().Select(b => new { symbol = b.Key, amount = (decimal)b.Value!, usd = Math.Round((decimal)b.Value! * (px.TryGetValue(b.Key, out var p) ? p : 0m), 2) })
                    .Where(b => b.amount > 0.0000001m).ToList(),
            }).ToList();
            result.Add(new { address = w.Address, label = w.Label, chains, usd = chains.Sum(c => c.balances.Sum(b => b.usd)) });
        }
        return new { wallets = result, prices = px };
    }

    static void MapTitn(WebApplication app)
    {
        app.MapGet("/api/evm/balances", async (HttpContext ctx) =>
        {
            var wallets = UserEvmWallets(ctx);
            try { return VJson(await BuildEvmBalancesAsync(wallets, ctx.RequestAborted)); }
            catch (Exception ex) when (ex is not OperationCanceledException) { return VError("Could not read your balances: " + ex.Message, 502); }
        });
        app.MapGet("/api/titn/summary", async (HttpContext ctx) =>
        {
            var wallets = UserEvmWallets(ctx);
            if (wallets.Count == 0) return VJson(new { wallets = Array.Empty<object>(), rewards = Array.Empty<object>(), unclassified = Array.Empty<object>() });
            try { return VJson(await BuildTitnSummaryAsync(wallets, ctx.Request.Query["refresh"] == "1", ctx.RequestAborted)); }
            catch (Exception ex) when (ex is not OperationCanceledException) { return VError("Could not read your TITN history: " + ex.Message, 502); }
        });
    }

    // The signed-in user's saved 0x... wallets with their labels.
    static List<(string Address, string Label)> UserEvmWallets(HttpContext ctx)
    {
        var path = Path.Combine(DashboardAuth.UserDataDir(ctx), "wallets.json");
        if (!File.Exists(path)) return new();
        var arr = JsonNode.Parse(File.ReadAllText(path)) as JsonArray ?? new JsonArray();
        return arr.Select(w => (Address: (w?["a"]?.ToString() ?? "").ToLowerInvariant(), Label: w?["l"]?.ToString() ?? ""))
            .Where(w => EvmAddress.IsMatch(w.Address)).ToList();
    }

    sealed record OtherReceipt(string Chain, string From, string Symbol, int N, decimal Amount, DateTime First, DateTime Last);
    sealed record EvmTransfer(DateTime Time, string Tx, long Log, string From, string To, string Token, string Symbol, decimal Amount, string Method = "");

    static string EvmCachePath(string chain, string address) => Path.Combine(Environment.CurrentDirectory, "odata", "evm", $"{chain}-{address}.json");

    // Every ERC-20 transfer in or out of the address on one chain; new pages only after the first load (cached 3 hours).
    static async Task<List<EvmTransfer>> LoadEvmTransfersAsync(string chain, string address, bool refresh, CancellationToken ct)
    {
        var path = EvmCachePath(chain, address);
        var cache = File.Exists(path) ? JsonNode.Parse(await File.ReadAllTextAsync(path, ct))!.AsObject() : new JsonObject { ["items"] = new JsonArray() };
        if ((int?)cache["version"] != 2) cache = new JsonObject { ["version"] = 2, ["items"] = new JsonArray() }; // version 2 keeps each transfer's method
        var items = cache["items"]!.AsArray();
        DateTime? updated = cache["updatedUtc"] is { } u && DateTime.TryParse(u.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var ut) ? ut : null;
        if (refresh || updated is null || DateTime.UtcNow - updated > TimeSpan.FromHours(3))
        {
            var known = new HashSet<string>(items.Select(i => i!["tx"]!.ToString() + "#" + i["log"]!.ToString()));
            var fresh = new List<JsonNode>();
            var basePath = $"{EvmExplorersAll[chain]}/addresses/{address}/token-transfers?type=ERC-20";
            var url = basePath;
            for (var page = 0; page < 300; page++)
            {
                using var doc = JsonDocument.Parse(await EvmHttp.GetStringAsync(url, ct));
                var reachedKnown = false;
                foreach (var t in doc.RootElement.GetProperty("items").EnumerateArray())
                {
                    var tx = t.GetProperty("transaction_hash").GetString()!;
                    var log = t.TryGetProperty("log_index", out var li) && li.ValueKind == JsonValueKind.Number ? li.GetInt64() : 0;
                    if (known.Contains(tx + "#" + log)) { reachedKnown = true; continue; }
                    var token = t.GetProperty("token");
                    var total = t.GetProperty("total");
                    if (!total.TryGetProperty("value", out var v) || v.ValueKind != JsonValueKind.String) continue;
                    var decimals = total.TryGetProperty("decimals", out var dd) && dd.ValueKind == JsonValueKind.String ? int.Parse(dd.GetString()!, CultureInfo.InvariantCulture) : 18;
                    var amount = (decimal)(double)BigInteger.Parse(v.GetString()!, CultureInfo.InvariantCulture) / (decimal)Math.Pow(10, decimals);
                    fresh.Add(new JsonObject
                    {
                        ["time"] = t.GetProperty("timestamp").GetString(),
                        ["tx"] = tx,
                        ["log"] = log,
                        ["from"] = t.GetProperty("from").GetProperty("hash").GetString()!.ToLowerInvariant(),
                        ["to"] = t.GetProperty("to").GetProperty("hash").GetString()!.ToLowerInvariant(),
                        ["token"] = (token.TryGetProperty("address_hash", out var ah) ? ah : token.GetProperty("address")).GetString()!.ToLowerInvariant(),
                        ["symbol"] = token.TryGetProperty("symbol", out var sy) ? sy.GetString() ?? "" : "",
                        ["amount"] = amount,
                        ["method"] = t.TryGetProperty("method", out var me) && me.ValueKind == JsonValueKind.String ? me.GetString() : "",
                    });
                }
                if (reachedKnown || !doc.RootElement.TryGetProperty("next_page_params", out var next) || next.ValueKind != JsonValueKind.Object) break;
                url = basePath + "&" + string.Join("&", next.EnumerateObject().Select(p => p.Name + "=" + Uri.EscapeDataString(p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText())));
            }
            foreach (var f in fresh) items.Add(f);
            cache["updatedUtc"] = DateTime.UtcNow;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, cache.ToJsonString(), ct);
        }
        return items.Select(i => new EvmTransfer(
            DateTime.Parse(i!["time"]!.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
            i["tx"]!.ToString(), (long)i["log"]!, i["from"]!.ToString(), i["to"]!.ToString(), i["token"]!.ToString(), i["symbol"]!.ToString(), (decimal)i["amount"]!, i["method"]?.ToString() ?? "")).ToList();
    }

    // Daily USD prices from DefiLlama (free, full history), cached 12 hours. TITN is the Base token (first traded
    // 2025-11-03; earlier days have no price); TGT is THORWallet's original token (CoinGecko "thorwallet").
    const string TitnPriceKey = "base:" + TitnToken;
    const string TgtPriceKey = "coingecko:thorwallet";
    const string TitnPriceSource = "DefiLlama daily price (TITN on Base)";
    const string TgtPriceSource = "DefiLlama daily price (TGT, CoinGecko thorwallet)";

    static async Task<SortedDictionary<string, decimal>> LoadDailyPricesAsync(string coin, bool refresh, CancellationToken ct)
    {
        var path = Path.Combine(Environment.CurrentDirectory, "odata", "evm", "prices-" + Regex.Replace(coin, "[^a-z0-9]+", "-") + ".json");
        if (!refresh && File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < TimeSpan.FromHours(12))
            return JsonSerializer.Deserialize<SortedDictionary<string, decimal>>(await File.ReadAllTextAsync(path, ct)) ?? new();
        var days = new SortedDictionary<string, decimal>(StringComparer.Ordinal);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        for (long start = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(); start < now; start += 300L * 86400)
        {
            using var doc = JsonDocument.Parse(await EvmHttp.GetStringAsync($"https://coins.llama.fi/chart/{coin}?start={start}&span=300&period=1d", ct));
            foreach (var cn in doc.RootElement.GetProperty("coins").EnumerateObject())
                foreach (var pt in cn.Value.GetProperty("prices").EnumerateArray())
                    days[DateTimeOffset.FromUnixTimeSeconds(pt.GetProperty("timestamp").GetInt64()).UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)] = pt.GetProperty("price").GetDecimal();
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(days), ct);
        return days;
    }
    static async Task<BigInteger> EthCallAsync(string to, string selector, IEnumerable<string> addressArgs, CancellationToken ct)
    {
        var data = "0x" + selector + string.Concat(addressArgs.Select(a => a[2..].PadLeft(64, '0')));
        var body = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "eth_call", ["params"] = new JsonArray(new JsonObject { ["to"] = to, ["data"] = data }, "latest") };
        using var res = await EvmHttp.PostAsync(BaseRpc, new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), ct);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        var hex = doc.RootElement.GetProperty("result").GetString()!;
        return hex.Length < 66 ? BigInteger.Zero : BigInteger.Parse("0" + hex[2..66], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    static decimal Scale(BigInteger v, int decimals) => (decimal)((double)v / Math.Pow(10, decimals));

    static async Task<object> BuildTitnSummaryAsync(List<(string Address, string Label)> wallets, bool refresh, CancellationToken ct)
    {
        await EvmGate.WaitAsync(ct);
        try
        {
            var own = wallets.Select(w => w.Address).ToHashSet();
            var transfers = new List<(string Chain, string Wallet, EvmTransfer T)>();
            foreach (var w in wallets)
                foreach (var chain in EvmExplorers.Keys)
                    transfers.AddRange((await LoadEvmTransfersAsync(chain, w.Address, refresh, ct)).Select(t => (chain, w.Address, t)));
            var prices = await LoadDailyPricesAsync(TitnPriceKey, refresh, ct);
            var tgtPrices = await LoadDailyPricesAsync(TgtPriceKey, refresh, ct);
            // The day's price, or the nearest earlier day within 3 days when DefiLlama has a gap.
            (decimal? Price, bool Earlier) PriceOnDay(DateTime t)
            {
                for (var i = 0; i < 4; i++) if (prices.TryGetValue(t.AddDays(-i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), out var p)) return (p, i > 0);
                return (null, false);
            }
            decimal? PriceOn(DateTime t) => PriceOnDay(t).Price;

            // Transactions in which a wallet also sent something are swaps, liquidity moves or staking calls.
            var sentTx = transfers.Where(x => x.T.From == x.Wallet).Select(x => x.Wallet + x.T.Tx).ToHashSet();
            var rewards = new List<object>();
            var unclassified = new Dictionary<string, OtherReceipt>();
            foreach (var (chain, wallet, t) in transfers.OrderBy(x => x.T.Time))
            {
                if (t.To != wallet || t.Amount < 0.000001m) continue;
                if (own.Contains(t.From)) continue; // between your own wallets
                if (TitnRewardSources.TryGetValue(t.From, out var src) && src.Chain == chain)
                {
                    if (TitnPrincipalTokens.Contains(t.Token) && src.Kind == "staking") continue; // your stake coming back
                    var stable = EvmStablecoins.Contains(t.Token);
                    if (!stable && t.Token != TitnToken) continue;
                    var price = stable ? 1m : PriceOn(t.Time);
                    rewards.Add(new
                    {
                        time = t.Time, chain, wallet, source = src.Name, kind = src.Kind, symbol = stable ? "USDC" : "TITN", token = t.Token,
                        // What earned the reward (TITN staking pays in USDC, so this differs from what was paid).
                        earnedFrom = src.Name.StartsWith("TGT", StringComparison.Ordinal) ? "TGT" : "TITN",
                        amount = t.Amount, priceUsd = price, usd = price is { } p ? Math.Round(t.Amount * p, 2) : (decimal?)null,
                        priceSource = stable ? "USD stablecoin ($1.00)" : price is null ? "No TITN price for this day" : PriceOnDay(t.Time).Earlier ? TitnPriceSource + ", nearest earlier day (no price that day)" : TitnPriceSource,
                        tx = t.Tx,
                    });
                    continue;
                }
                // Received from someone else in a plain transfer: listed for you to explain, never counted. Swap proceeds
                // and bridge mints (from the zero address) are not listed.
                if (!(EvmStablecoins.Contains(t.Token) || t.Token == TitnToken)) continue;
                if (sentTx.Contains(wallet + t.Tx) || t.From == "0x0000000000000000000000000000000000000000") continue;
                if (t.Amount < 0.01m || TitnKnownContracts.Contains(t.From)) continue; // poisoning dust; contracts explained elsewhere
                var key = chain + "|" + t.From + "|" + t.Token;
                var e = unclassified.TryGetValue(key, out var cur) ? cur : new OtherReceipt(chain, t.From, EvmStablecoins.Contains(t.Token) ? "USDC" : "TITN", 0, 0m, t.Time, t.Time);
                unclassified[key] = e with { N = e.N + 1, Amount = e.Amount + t.Amount, Last = t.Time };
            }

            var current = prices.Count > 0 ? prices.Last() : default;
            var walletRows = new List<object>();
            foreach (var w in wallets)
            {
                var balance = Scale(await EthCallAsync(TitnToken, "70a08231", new[] { w.Address }, ct), 18);
                var staked = Scale(await EthCallAsync(TitnStakingContract, "f2801fe7", new[] { w.Address, BaseUsdc }, ct), 18);
                var pending = Scale(await EthCallAsync(TitnStakingContract, "9ced7e76", new[] { w.Address, BaseUsdc }, ct), 6);
                walletRows.Add(new { address = w.Address, label = w.Label, titnBalance = balance, titnStaked = staked, pendingUsdc = pending });
            }
            return new
            {
                wallets = walletRows,
                price = new { usd = current.Value, day = current.Key, source = TitnPriceSource },
                tgtPrice = new { usd = tgtPrices.Count > 0 ? tgtPrices.Last().Value : 0m, day = tgtPrices.Count > 0 ? tgtPrices.Last().Key : "", source = TgtPriceSource },
                rewards,
                unclassified = unclassified.Values.OrderByDescending(u => u.Amount)
                    .Select(u => new
                    {
                        chain = u.Chain, from = u.From, symbol = u.Symbol, count = u.N, amount = u.Amount, first = u.First, last = u.Last,
                        name = KnownSenderNames.TryGetValue(u.From, out var nm) ? nm : null,
                        // Your rule: USDC that arrived from anyone but your own wallets is USDC you bought, not income.
                        treatedAs = u.Symbol == "USDC" ? "USDC you bought (not income)"
                            : CrossChainSolvers.Contains(u.From) ? "TITN bought through a cross-chain swap (Tax Report section 4a)"
                            : "Not classified",
                    }),
                updatedUtc = DateTime.UtcNow,
            };
        }
        finally { EvmGate.Release(); }
    }
}

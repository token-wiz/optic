using OPTIC.Services;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

// Cost basis, sales and exchanges of TGT, TITN and ETH in the user's saved 0x... wallets (each address is one wallet
// across Ethereum, Arbitrum and Base). Lots are kept per wallet and used highest cost first (the user's standing order).
//   - Purchases: what was paid in the same transaction (stablecoins at $1, ETH at the day's price), or for THORChain /
//     Maya cross-chain swaps the input amount at Midgard's USD price, or for cross-chain solver swaps the payment made
//     to the one-time deposit address just before the delivery.
//   - Sales: stablecoins or ETH received in the same transaction, Midgard's USD value, or the solver's delivery.
//   - TGT→TITN migration (MergeTgt): each deposit is an exchange of TGT at its market value that day. The TITN each
//     deposit entitled the wallet to (ClaimableTitnUpdated) takes that value as its cost when received. TITN received
//     beyond those claims (the extra share for leaving TGT in the contract a full year) is additional amount realized
//     on the exchange when received, and that TITN's cost is its value that day.
//   - Aerodrome liquidity: deposits and withdrawals are followed per wallet; when the position closes (last withdrawal),
//     TITN that came back short was sold for the net USDC gained, extra TITN was bought for the net USDC paid.
//   - ETH: bought with USDC (DEX, THORChain/Maya, solver), received from exchanges (cost = that day's price until the
//     exchange's records are loaded), spent on purchases, received from sales, bridged between networks through a
//     solver (a move between your own wallets; what the bridge kept is a fee), and spent on gas.
//   - Transaction costs (Treas. Reg. §1.1012-1(h)): every network fee is ETH spent at its value that day. In a trade of
//     one digital asset for another, half of that fee is added to the cost of what was received and half reduces the
//     proceeds of what was given up. Fees for staking, bridging and moves between your own wallets are not added to
//     any cost basis. Fees taken inside a swap rate (THORChain, Maya, relayers, pools) are already in the amounts.
//   - Staking and moves between the user's own wallets are not sales. Airdrop awards are income (reported with the
//     rewards) and their TITN costs its value that day.
//   - Anything else (plain transfers to or from other addresses) is listed as not classified and never guessed; a
//     token received without a known cost is kept as a lot with unknown cost.
// Per-transaction answers from the user live in odata/users/<user>/titn-labels.json (saved from Needs Your Input):
//   { "<txHash>": { "kind": "purchase|sale|income|gift-in|gift-out|own|ignore", "usd": 12.34 or null, "note": "..." } }
//   purchase / income / gift-in: acquired (cost = usd; income without usd = that day's value; gift-in usd = the giver's basis).
//   sale: disposed for usd (or that day's value). gift-out / own (sent): leaves the wallets tracked here, not a sale.
//   own (received): acquired at usd, else unknown cost. ignore: left out.
public static partial class WebDashboard
{
    const string MergeTgtContract = "0xfa486b0e67ca52f5f9e5adf0b79b6cfeb339b6e0";
    const string AerodromeTitnPool = "0x50b5d6f6accdcc7dc048de35b2b9ba5fdd5a283e";
    const string TitnAirdropContract = "0x6d3261c6e1cb05e0fd0132b6d962ca8fabb109ac";
    const string ZeroAddress = "0x0000000000000000000000000000000000000000";
    static readonly HashSet<string> TgtTokens = new() { "0x429fed88f10285e61b12bdf00848315fbdfcc341", "0x108a850856db3f85d0269a2693d896b394c80325" };
    // Staking contracts: tokens sent in stay yours (the lots stay in the wallet); NILA's also pays its rewards from here.
    static readonly HashSet<string> TitnStakingContracts = new() { TitnStakingContract, "0x6745c897ab1f4fda9f7700e8be6ea2ee03672759", NilaStakingContract };
    static readonly HashSet<string> TokenBridgeContracts = new()
    {
        "0xa3a7b6f88361f48403514059f1f16c8e78d60eec", // Arbitrum L1 gateway router
        "0xb4b8b6f88361f48403514059f1f16c8e78d61ffd", // its aliased address on Arbitrum (returns unused bridge gas)
    };
    static readonly Dictionary<string, string> EvmExplorersAll = new()
    {
        ["ethereum"] = "https://eth.blockscout.com/api/v2",
        ["arbitrum"] = "https://arbitrum.blockscout.com/api/v2",
        ["base"] = "https://base.blockscout.com/api/v2",
    };
    static readonly Dictionary<string, string> MidgardHosts = new()
    {
        ["THORChain"] = "https://gateway.liquify.com/chain/thorchain_midgard/v2",
        ["Maya"] = "https://midgard.mayachain.info/v2",
    };
    // High-volume cross-chain solvers that deliver the other side of a swap or bridge you started.
    static readonly HashSet<string> CrossChainSolvers = new()
    {
        "0x2cff890f0378a11913b6129b2e97417a2c302680",
        "0xf5e10380213880111522dd0efd3dbb45b9f62bcc", // bridge vault: pays out ETH sent to its one-time "Deposit" contracts on another network
        "0x79001a5e762f3befc8e5871b42f6734e00498920", // the same bridge's vault on Arbitrum
        "0xf5042e6ffac5a625d4e7848e0b01373d8eb9e222", // Relay router: delivers what a MetaMask Bridge / Relay order bought
        "0xf70da97812cb96acdf810712aa562db8dfa3dbef", // Relay solver (pays out USDT / ETH)
        "0x663dc15d3c1ac63ff12e45ab68fea3f0a883c251", // cross-chain swap contract on BNB Chain (delivered NILA bought with ETH)
        "0xb685760ebd368a891f27ae547391f4e2a289895b", // Bridgers: delivered BNB for ETH
        "0xb01caea8c6c47bbf4f4b4c5080ca642043359c2e", // bridge gas top-up (small ETH / BNB with a bridge order)
    };
    // Bridge entry contracts: native coin sent to them pays for something delivered on another network.
    static readonly HashSet<string> BridgeRouters = new()
    {
        "0x0439e60f02a8900a951603950d8d4527f400c3f1", // MetaMask Bridge (MetaBridge) on Ethereum
        "0xa20ecbc821fb54064aa7b5c6ac81173b8b34df71", // MetaMask Bridge on Base
        "0x23981fc34e69eedfe2bd9a0a9fcb0719fe09dbfc", // MetaMask Bridge on Arbitrum
    };
    // Exchange deposit addresses the user sent ETH to (moves into their own exchange account, not sales).
    static readonly HashSet<string> ExchangeDepositAddresses = new()
    {
        "0x52968330c043ae19a6d12e86854a42e40be8d8a3", // forwards within minutes to the exchange wallet 0x8679…3e83
        "0xf5710b2dafedcc4ff758661b35812cac0d393340", // followed by a USDT withdrawal from the exchange wallet 0xf206…f344
    };

    static void MapTitnLots(WebApplication app)
    {
        app.MapGet("/api/titn/lots", async (HttpContext ctx) =>
        {
            var wallets = UserEvmWallets(ctx);
            var solana = UserSolanaWallets(ctx);
            if (wallets.Count == 0 && solana.Count == 0) return VJson(new { disposals = Array.Empty<object>(), holdings = Array.Empty<object>(), unclassified = Array.Empty<object>() });
            var labelsPath = Path.Combine(DashboardAuth.UserDataDir(ctx), "titn-labels.json");
            var labels = File.Exists(labelsPath) ? JsonNode.Parse(await File.ReadAllTextAsync(labelsPath, ctx.RequestAborted)) as JsonObject ?? new() : new JsonObject();
            var refresh = ctx.Request.Query["refresh"] == "1";
            try
            {
                object evm = wallets.Count > 0 ? await BuildTitnLotsAsync(wallets, labels, refresh, ctx.RequestAborted)
                    : new { disposals = Array.Empty<object>(), holdings = Array.Empty<object>(), unclassified = Array.Empty<object>(), notes = Array.Empty<string>() };
                if (solana.Count == 0) return VJson(evm);
                // Solana wallets: their own history, merged into the same lists (a failure there leaves the rest intact).
                try { return VJson(MergeSolanaLots(evm, await BuildSolanaLotsAsync(solana, labels, refresh, ctx.RequestAborted))); }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
                {
                    var node = JsonSerializer.SerializeToNode(evm) as JsonObject ?? new JsonObject();
                    (node["notes"] as JsonArray ?? (JsonArray)(node["notes"] = new JsonArray())).Add("Could not read your Solana history this time: " + ex.Message);
                    return VJson(node);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { return VError("Could not build your TGT / TITN / ETH lots: " + ex.Message, 502); }
        });
        // Your answer for one transaction that could not be classified: { tx, kind, usd?, note? }; kind null clears it.
        app.MapPost("/api/titn/labels", async (HttpContext ctx) =>
        {
            var body = await JsonNode.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted) as JsonObject;
            // An Ethereum-style hash (lowercase) or a Solana signature (base58, case-sensitive).
            var txRaw = body?["tx"]?.ToString()?.Trim() ?? "";
            var tx = System.Text.RegularExpressions.Regex.IsMatch(txRaw, "^0x[0-9a-fA-F]{64}$") ? txRaw.ToLowerInvariant()
                : System.Text.RegularExpressions.Regex.IsMatch(txRaw, "^[1-9A-HJ-NP-Za-km-z]{64,90}$") ? txRaw : "";
            if (tx == "") return VError("tx must be a transaction hash.");
            var kind = body?["kind"]?.ToString();
            if (kind is not (null or "" or "purchase" or "sale" or "income" or "gift-in" or "gift-out" or "own" or "ignore")) return VError("Unknown answer.");
            decimal? usd = body?["usd"] is JsonValue uv && uv.TryGetValue<decimal>(out var ud) && ud >= 0 ? ud : null;
            var note = (body?["note"]?.ToString() ?? "").Trim();
            if (note.Length > 500) note = note[..500];
            var labelsPath = Path.Combine(DashboardAuth.UserDataDir(ctx), "titn-labels.json");
            var labels = File.Exists(labelsPath) ? JsonNode.Parse(await File.ReadAllTextAsync(labelsPath, ctx.RequestAborted)) as JsonObject ?? new() : new JsonObject();
            if (string.IsNullOrEmpty(kind)) labels.Remove(tx);
            else labels[tx] = new JsonObject { ["kind"] = kind, ["usd"] = usd, ["note"] = note, ["answeredUtc"] = DateTime.UtcNow };
            Directory.CreateDirectory(Path.GetDirectoryName(labelsPath)!);
            await File.WriteAllTextAsync(labelsPath, labels.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), ctx.RequestAborted);
            return VJson(new { tx, kind, usd, note });
        });
        app.MapGet("/api/titn/labels", async (HttpContext ctx) =>
        {
            var labelsPath = Path.Combine(DashboardAuth.UserDataDir(ctx), "titn-labels.json");
            var labels = File.Exists(labelsPath) ? JsonNode.Parse(await File.ReadAllTextAsync(labelsPath, ctx.RequestAborted)) as JsonObject ?? new() : new JsonObject();
            labels.Remove("_settings");
            return Results.Text(labels.ToJsonString(), "application/json");
        });
        // Your choice of TGT→TITN migration treatment, kept with your per-transaction answers.
        app.MapPost("/api/titn/settings", async (HttpContext ctx) =>
        {
            var body = await JsonNode.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted) as JsonObject;
            var migration = body?["migration"]?.ToString();
            if (migration is not ("exchange" or "carryover")) return VError("migration must be exchange or carryover.");
            var labelsPath = Path.Combine(DashboardAuth.UserDataDir(ctx), "titn-labels.json");
            var labels = File.Exists(labelsPath) ? JsonNode.Parse(await File.ReadAllTextAsync(labelsPath, ctx.RequestAborted)) as JsonObject ?? new() : new JsonObject();
            var settings = labels["_settings"] as JsonObject ?? new JsonObject();
            settings["migration"] = migration;
            settings["migrationSetUtc"] = DateTime.UtcNow;
            labels["_settings"] = settings;
            Directory.CreateDirectory(Path.GetDirectoryName(labelsPath)!);
            await File.WriteAllTextAsync(labelsPath, labels.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), ctx.RequestAborted);
            return VJson(new { migration });
        });
    }

    sealed record EvmTx(DateTime Time, string Hash, string From, string To, decimal Value, string Method, decimal Fee);
    sealed record EvmInternal(DateTime Time, string Tx, string From, decimal Value);

    static async Task<List<JsonElement>> BlockscoutPagesAsync(string basePath, CancellationToken ct)
    {
        var items = new List<JsonElement>();
        var url = basePath;
        for (var page = 0; page < 200; page++)
        {
            using var doc = JsonDocument.Parse(await EvmHttp.GetStringAsync(url, ct));
            items.AddRange(doc.RootElement.GetProperty("items").EnumerateArray().Select(e => e.Clone()));
            if (!doc.RootElement.TryGetProperty("next_page_params", out var next) || next.ValueKind != JsonValueKind.Object) break;
            url = basePath + (basePath.Contains('?') ? "&" : "?") + string.Join("&", next.EnumerateObject().Select(p => p.Name + "=" + Uri.EscapeDataString(p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText())));
        }
        return items;
    }

    static DateTime ParseUtc(string s) => DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
    static decimal Wei(JsonElement e, string prop) => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? (decimal)(double.Parse(v.GetString()!, CultureInfo.InvariantCulture) / 1e18) : 0m;
    static string HashOf(JsonElement e, string prop) => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Object && v.TryGetProperty("hash", out var h) ? h.GetString()!.ToLowerInvariant() : "";

    // Transactions of the address with their network fee (cached 3 hours).
    static async Task<List<EvmTx>> LoadEvmTxsAsync(string chain, string address, bool refresh, CancellationToken ct)
    {
        if (chain == "bsc") return await LoadBscTxsAsync(address, refresh, ct);
        var path = Path.Combine(Environment.CurrentDirectory, "odata", "evm", $"{chain}-{address}-txs-v2.json");
        if (!refresh && File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < TimeSpan.FromHours(3))
            return JsonSerializer.Deserialize<List<EvmTx>>(await File.ReadAllTextAsync(path, ct)) ?? new();
        var list = new List<EvmTx>();
        foreach (var x in await BlockscoutPagesAsync($"{EvmExplorersAll[chain]}/addresses/{address}/transactions", ct))
        {
            if (x.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String && st.GetString() != "ok")
            {
                // A failed transaction still costs its fee.
                if (HashOf(x, "from") == address && x.TryGetProperty("fee", out var ff) && ff.ValueKind == JsonValueKind.Object)
                    list.Add(new EvmTx(ParseUtc(x.GetProperty("timestamp").GetString()!), x.GetProperty("hash").GetString()!.ToLowerInvariant(), address, HashOf(x, "to"), 0m, "failed", Wei(ff, "value")));
                continue;
            }
            var fee = x.TryGetProperty("fee", out var f) && f.ValueKind == JsonValueKind.Object ? Wei(f, "value") : 0m;
            list.Add(new EvmTx(ParseUtc(x.GetProperty("timestamp").GetString()!), x.GetProperty("hash").GetString()!.ToLowerInvariant(), HashOf(x, "from"), HashOf(x, "to"),
                Wei(x, "value"), x.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString()! : "", fee));
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(list), ct);
        return list;
    }

    // ETH that reached the address from contracts (swap outputs, refunds, exchange withdrawals), cached 3 hours.
    static async Task<List<EvmInternal>> LoadEvmInternalsAsync(string chain, string address, bool refresh, CancellationToken ct)
    {
        if (chain == "bsc") return await LoadBscInternalsAsync(address, refresh, ct);
        var path = Path.Combine(Environment.CurrentDirectory, "odata", "evm", $"{chain}-{address}-internal.json");
        if (!refresh && File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < TimeSpan.FromHours(3))
            return JsonSerializer.Deserialize<List<EvmInternal>>(await File.ReadAllTextAsync(path, ct)) ?? new();
        var list = new List<EvmInternal>();
        foreach (var x in await BlockscoutPagesAsync($"{EvmExplorersAll[chain]}/addresses/{address}/internal-transactions", ct))
        {
            if (x.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.False) continue;
            if (HashOf(x, "to") != address) continue;
            var v = Wei(x, "value");
            if (v <= 0) continue;
            list.Add(new EvmInternal(ParseUtc(x.GetProperty("timestamp").GetString()!), x.GetProperty("transaction_hash").GetString()!.ToLowerInvariant(), HashOf(x, "from"), v));
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(list), ct);
        return list;
    }

    sealed record MidgardLeg(string Network, string Type, DateTime Time, string InTx, string InAsset, decimal InAmount, decimal InUsd, string InAddress, List<(string Asset, decimal Amount, string Address)> Outs);

    // THORChain and Maya swaps (and refunds) that involve the address (cached 12 hours).
    static async Task<List<MidgardLeg>> LoadMidgardSwapsAsync(string address, bool refresh, CancellationToken ct)
    {
        var result = new List<MidgardLeg>();
        foreach (var (net, host) in MidgardHosts)
        {
            var path = Path.Combine(Environment.CurrentDirectory, "odata", "evm", $"midgard-{net.ToLowerInvariant()}-{address}.json");
            JsonArray actions;
            if (!refresh && File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < TimeSpan.FromHours(12))
                actions = JsonNode.Parse(await File.ReadAllTextAsync(path, ct))!.AsArray();
            else
            {
                actions = new JsonArray();
                var token = "";
                for (var i = 0; i < 40; i++)
                {
                    var page = JsonNode.Parse(await EvmHttp.GetStringAsync($"{host}/actions?address={address}&limit=50" + (token != "" ? "&nextPageToken=" + token : ""), ct))!;
                    var items = page["actions"] as JsonArray ?? new JsonArray();
                    foreach (var a in items) actions.Add(a!.DeepClone());
                    token = page["meta"]?["nextPageToken"]?.ToString() ?? "";
                    if (token == "" || items.Count == 0) break;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, actions.ToJsonString(), ct);
            }
            foreach (var a in actions)
            {
                var type = a?["type"]?.ToString();
                if (type is not ("swap" or "refund") || a!["status"]?.ToString() != "success") continue;
                var inLeg = a["in"]?[0]; var inCoin = inLeg?["coins"]?[0];
                if (inCoin is null) continue;
                static decimal Amt(JsonNode c) => decimal.Parse(c["amount"]!.ToString(), CultureInfo.InvariantCulture) / (c["asset"]!.ToString() == "MAYA.CACAO" ? 10_000_000_000m : 100_000_000m);
                var px = decimal.TryParse(a["metadata"]?["swap"]?["inPriceUSD"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? p : 0m;
                var outs = (a["out"] as JsonArray ?? new()).SelectMany(o => (o!["coins"] as JsonArray ?? new()).Select(c => (Asset: c!["asset"]!.ToString(), Amount: Amt(c), Address: (o["address"]?.ToString() ?? "").ToLowerInvariant()))).ToList();
                var txId = (inLeg!["txID"]?.ToString() ?? "").ToLowerInvariant();
                if (txId.Length == 64) txId = "0x" + txId;
                result.Add(new MidgardLeg(net, type, DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(a["date"]!.ToString(), CultureInfo.InvariantCulture) / 1_000_000).UtcDateTime,
                    txId, inCoin["asset"]!.ToString(), Amt(inCoin), Amt(inCoin) * px, (inLeg["address"]?.ToString() ?? "").ToLowerInvariant(), outs));
            }
        }
        return result;
    }

    // TITN each MergeTgt deposit entitled the wallet to (ClaimableTitnUpdated in the deposit transaction), cached forever.
    static async Task<Dictionary<string, decimal>> LoadMergeClaimsAsync(IEnumerable<string> depositTxs, CancellationToken ct)
    {
        var path = Path.Combine(Environment.CurrentDirectory, "odata", "evm", "merge-claims.json");
        var cache = File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, decimal>>(await File.ReadAllTextAsync(path, ct)) ?? new() : new();
        var changed = false;
        foreach (var tx in depositTxs.Where(t => !cache.ContainsKey(t)))
        {
            using var doc = JsonDocument.Parse(await EvmHttp.GetStringAsync($"{EvmExplorersAll["arbitrum"]}/transactions/{tx}/logs", ct));
            foreach (var l in doc.RootElement.GetProperty("items").EnumerateArray())
            {
                if (!l.TryGetProperty("decoded", out var dec) || dec.ValueKind != JsonValueKind.Object) continue;
                if (!(dec.GetProperty("method_call").GetString() ?? "").StartsWith("ClaimableTitnUpdated", StringComparison.Ordinal)) continue;
                foreach (var prm in dec.GetProperty("parameters").EnumerateArray())
                    if (prm.GetProperty("name").GetString() == "titnOut")
                    { cache[tx] = (decimal)(double.Parse(prm.GetProperty("value").GetString()!, CultureInfo.InvariantCulture) / 1e18); changed = true; }
            }
        }
        if (changed) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllTextAsync(path, JsonSerializer.Serialize(cache), ct); }
        return cache;
    }

    sealed class TitnLot
    {
        public string Id = "";
        public string Asset = "";
        public decimal Qty;
        public decimal? UnitCost; // null: cost unknown
        public DateTime Acquired;
        public string How = "";
    }

    // A payment to a one-time deposit address, or a delivery from a cross-chain solver.
    sealed class SolverLeg
    {
        public string Wallet = "", Chain = "", Tx = "", Asset = "", What = "";
        public DateTime Time;
        public decimal Qty, Usd, FeeHalf;
        public bool Used, Solver;
    }

    static readonly HashSet<string> UsdtTokens = new() { "0xdac17f958d2ee523a2206206994597c13d831ec7", "0xfd086bc7cd5c481dcc9c85ebe478a1c0b69fcbb9", BscUsdt };
    static bool IsUsdToken(string token) => EvmStablecoins.Contains(token) || token is "0xa0b86991c6218b36c1d19d4a2e9eb0ce3606eb48" or "0xdac17f958d2ee523a2206206994597c13d831ec7" or BscUsdt or BscUsdc;
    static bool IsExplainedContract(string a) => TitnKnownContracts.Contains(a) || TitnStakingContracts.Contains(a) || TokenBridgeContracts.Contains(a) || a == MergeTgtContract || a == ZeroAddress || TitnRewardSources.ContainsKey(a);

    // A payment and a delivery belong together only if their values are of the same size (a bridge or solver keeps a
    // fee, and day prices are rough); a small gas top-up never takes a large payment as its cost.
    static bool CloseValue(decimal paid, decimal? got) => got is not { } g || g <= 0 || paid <= 0 || (g >= paid * 0.25m && g <= paid * 4m);

    sealed record LegNet(string Asset, decimal Qty, HashSet<string> Counterparties, bool Plain);

    sealed class EvmRow
    {
        public string Wallet = "", Chain = "", Tx = "", Method = "", EthOutTo = "";
        public DateTime Time;
        public Dictionary<string, LegNet> Legs = new();
        public Dictionary<string, LegNet> Stables = new(); // USDC (incl. USDC.e) and USDT movements in this transaction
        public decimal Usd, EthOut, EthIn, Fee;
        public bool Signed, EthPlainIn;
        public HashSet<string> EthInFrom = new();
    }

    static async Task<object> BuildTitnLotsAsync(List<(string Address, string Label)> wallets, JsonObject labels, bool refresh, CancellationToken ct)
    {
        await EvmGate.WaitAsync(ct);
        try
        {
            var own = wallets.Select(w => w.Address).ToHashSet();
            var tgtPx = await LoadDailyPricesAsync(TgtPriceKey, refresh, ct);
            var titnPx = await LoadDailyPricesAsync(TitnPriceKey, refresh, ct);
            var ethPx = await LoadDailyPricesAsync("coingecko:ethereum", refresh, ct);
            var bnbPx = await LoadDailyPricesAsync(BnbPriceKey, refresh, ct);
            var nilaPx = await LoadDailyPricesAsync(NilaPriceKey, refresh, ct);
            var patriotPx = await LoadDailyPricesAsync(PatriotPriceKey, refresh, ct);
            static decimal? Px(SortedDictionary<string, decimal> s, DateTime t)
            {
                for (var i = 0; i < 4; i++) if (s.TryGetValue(t.AddDays(-i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), out var p)) return p;
                return null;
            }
            // The network's own coin (ETH, or BNB on BNB Chain) at its value that day.
            decimal NatUsd(string chain, decimal qty, DateTime t) => Px(chain == "bsc" ? bnbPx : ethPx, t) is { } p ? Math.Round(qty * p, 2) : 0m;
            decimal? AssetPx(string asset, DateTime t) => asset switch
            {
                "TITN" => Px(titnPx, t), "TGT" => Px(tgtPx, t), "NILA" => Px(nilaPx, t), "PATRIOT" => Px(patriotPx, t), _ => null,
            };

            // One row per (wallet, transaction): net change of TGT, TITN, stablecoins and ETH, and the fee if the wallet sent it.
            var rows = new List<EvmRow>();
            var mergeDeposits = new List<string>();
            var midgard = new List<MidgardLeg>();
            var payments = new List<SolverLeg>();
            var deliveries = new List<SolverLeg>();
            foreach (var w in wallets)
            {
                midgard.AddRange(await LoadMidgardSwapsAsync(w.Address, refresh, ct));
                foreach (var chain in EvmChainsAll)
                {
                    var nat = NativeOf(chain);
                    var transfers = (await LoadEvmTransfersAsync(chain, w.Address, refresh, ct)).Where(t => t.From == w.Address || t.To == w.Address).ToList();
                    var txList = await LoadEvmTxsAsync(chain, w.Address, refresh, ct);
                    var internals = await LoadEvmInternalsAsync(chain, w.Address, refresh, ct);
                    var byTx = new Dictionary<string, EvmRow>();
                    EvmRow RowFor(string tx, DateTime time) => byTx.TryGetValue(tx, out var r) ? r : byTx[tx] = new EvmRow { Wallet = w.Address, Chain = chain, Tx = tx, Time = time };
                    foreach (var t in transfers)
                    {
                        var r = RowFor(t.Tx, t.Time);
                        var sign = t.To == w.Address ? 1 : -1;
                        if (IsUsdToken(t.Token))
                        {
                            r.Usd += sign * t.Amount;
                            var fam = UsdtTokens.Contains(t.Token) ? "USDT" : "USDC";
                            if (!r.Stables.TryGetValue(fam, out var sl)) r.Stables[fam] = sl = new LegNet(fam, 0, new HashSet<string>(), t.Method is "transfer" or "0xa9059cbb");
                            r.Stables[fam] = sl with { Qty = sl.Qty + sign * t.Amount };
                            sl.Counterparties.Add(sign > 0 ? t.From : t.To);
                            continue;
                        }
                        var asset = TrackedTokenAsset(chain, t.Token);
                        if (asset is null) continue;
                        var cp = sign > 0 ? t.From : t.To;
                        if (!r.Legs.TryGetValue(asset, out var leg)) r.Legs[asset] = leg = new LegNet(asset, 0, new HashSet<string>(), t.Method is "transfer" or "0xa9059cbb");
                        r.Legs[asset] = leg with { Qty = leg.Qty + sign * t.Amount };
                        leg.Counterparties.Add(cp);
                    }
                    foreach (var x in txList.GroupBy(x => x.Hash).Select(g => g.First()))
                    {
                        var r = RowFor(x.Hash, x.Time);
                        r.Method = x.Method;
                        if (x.From == w.Address) { r.Signed = true; r.Fee = x.Fee; r.EthOut += x.Value; r.EthOutTo = x.To; }
                        else if (x.To == w.Address && x.Value > 0) { r.EthIn += x.Value; r.EthInFrom.Add(x.From); r.EthPlainIn = true; }
                    }
                    foreach (var i in internals) { var r = RowFor(i.Tx, i.Time); r.EthIn += i.Value; r.EthInFrom.Add(i.From); }
                    foreach (var r in byTx.Values)
                    {
                        if (r.Legs.TryGetValue("TGT", out var tl) && tl.Qty < 0 && tl.Counterparties.Contains(MergeTgtContract)) mergeDeposits.Add(r.Tx);
                        var netEth = r.EthIn - r.EthOut;
                        // Payments to one-time deposit addresses and deliveries from solvers, to pair up solver swaps and bridges.
                        if (r.Usd < -0.01m && r.Legs.Count == 0 && netEth == 0)
                        {
                            var to = transfers.Where(t => t.Tx == r.Tx && t.From == w.Address && IsUsdToken(t.Token)).Select(t => t.To).FirstOrDefault() ?? "";
                            if (!own.Contains(to) && !IsExplainedContract(to)) payments.Add(new SolverLeg { Wallet = w.Address, Chain = chain, Tx = r.Tx, Time = r.Time, Asset = "USD", Qty = -r.Usd, Usd = -r.Usd, What = $"{-r.Usd:N2} USDC/USDT on {chain}" });
                        }
                        if (r.EthOut > 0 && r.Legs.Count == 0 && Math.Abs(r.Usd) < 0.01m && !own.Contains(r.EthOutTo) && !IsExplainedContract(r.EthOutTo) && (r.Method is "" or null || BridgeRouters.Contains(r.EthOutTo)))
                            payments.Add(new SolverLeg { Wallet = w.Address, Chain = chain, Tx = r.Tx, Time = r.Time, Asset = nat, Qty = r.EthOut, Usd = NatUsd(chain, r.EthOut, r.Time), What = $"{r.EthOut:0.######} {nat} on {chain}" });
                        // Deliveries: anything from a known solver, and plain ETH from any outside address (a bridge's relayer).
                        if (r.EthIn > 0 && (r.EthInFrom.Overlaps(CrossChainSolvers) || (r.EthPlainIn && !r.EthInFrom.Overlaps(own) && !r.EthInFrom.Any(IsExplainedContract))))
                            deliveries.Add(new SolverLeg { Solver = r.EthInFrom.Overlaps(CrossChainSolvers), Wallet = w.Address, Chain = chain, Tx = r.Tx, Time = r.Time, Asset = nat, Qty = r.EthIn, Usd = NatUsd(chain, r.EthIn, r.Time), What = $"{r.EthIn:0.######} {nat} on {chain}" });
                        foreach (var t in transfers.Where(t => t.Tx == r.Tx && t.To == w.Address && CrossChainSolvers.Contains(t.From) && IsUsdToken(t.Token) && t.Amount >= 0.01m))
                            deliveries.Add(new SolverLeg { Solver = true, Wallet = w.Address, Chain = chain, Tx = r.Tx, Time = r.Time, Asset = "USD", Qty = t.Amount, Usd = t.Amount, What = $"{t.Amount:N2} {t.Symbol} on {chain}" });
                        rows.Add(r);
                    }
                }
            }
            var mergeClaims = await LoadMergeClaimsAsync(mergeDeposits, ct);
            var midgardByInTx = midgard.Where(m => m.InTx != "").GroupBy(m => m.InTx).ToDictionary(g => g.Key, g => g.OrderBy(m => m.Type == "refund" ? 1 : 0).First());

            var lots = wallets.ToDictionary(w => w.Address, _ => new List<TitnLot>());
            var disposals = new List<object>();
            var unclassified = new List<object>();
            var movedToExchange = new List<object>();
            var notes = new List<string>();
            var lotSeq = 0;
            var receivable = new Dictionary<string, Queue<(decimal Titn, decimal Usd, DateTime Time, DateTime? Acq)>>();
            // TGT→TITN migration treatment (your choice on the Tax Report): a taxable exchange at TGT's value (default), or
            // not taxable, with the TGT's cost and purchase dates carried over to the TITN.
            var carryOver = (labels["_settings"] as JsonObject)?["migration"]?.ToString() == "carryover";
            var lp = wallets.ToDictionary(w => w.Address, _ => (Titn: 0m, Usd: 0m, Fee: 0m));
            var lastLpWithdrawal = rows.Where(r => r.Legs.TryGetValue("TITN", out var l) && l.Qty > 0 && l.Counterparties.Contains(AerodromeTitnPool))
                .GroupBy(r => r.Wallet).ToDictionary(g => g.Key, g => g.Max(r => r.Time));
            var usedMidgard = new HashSet<MidgardLeg>();
            var handled = new HashSet<string>(); // wallet|tx already settled from the other side (own moves, bridges)
            var halfFeeByTx = new Dictionary<string, decimal>(); // fee share of a THORChain / Maya deposit, added to what it buys
            var disposedMidgardIn = new HashSet<string>(); // THORChain / Maya deposits whose ETH was counted as spent
            var usdFeeShare = new Dictionary<(string Wallet, int Year), (decimal Usd, decimal Fee, int N)>();
            var provisionalEth = (Qty: 0m, Usd: 0m, N: 0);
            var feeTotal = new Dictionary<int, decimal>();

            void Acquire(string wallet, string asset, decimal qty, decimal? cost, DateTime time, string how)
            {
                if (qty < 0.0000001m) return;
                lots[wallet].Add(new TitnLot { Id = $"{wallet[..6]}#{++lotSeq}", Asset = asset, Qty = qty, UnitCost = cost is { } c ? c / qty : null, Acquired = time, How = how });
            }

            // Removes qty of an asset from a wallet, highest cost first (unknown cost last), returning the slices taken.
            List<(TitnLot Lot, decimal Qty)> Take(string wallet, string asset, decimal qty)
            {
                var taken = new List<(TitnLot, decimal)>();
                foreach (var lot in lots[wallet].Where(l => l.Asset == asset && l.Qty > 0).OrderByDescending(l => l.UnitCost.HasValue).ThenByDescending(l => l.UnitCost ?? 0).ThenBy(l => l.Acquired).ToList())
                {
                    if (qty <= 0.0000001m) break;
                    var q = Math.Min(lot.Qty, qty);
                    lot.Qty -= q; qty -= q;
                    taken.Add((lot, q));
                }
                if (qty > (asset is "ETH" or "BNB" ? 0.0000001m : 0.0001m)) taken.Add((new TitnLot { Id = "missing", Asset = asset, Qty = qty, UnitCost = null, Acquired = DateTime.MinValue, How = "No recorded acquisition (held before this history, or received in a way not tracked)" }, qty));
                return taken;
            }

            void Dispose(string wallet, string chain, string asset, decimal qty, decimal? proceeds, DateTime time, string tx, string how)
            {
                foreach (var (lot, q) in Take(wallet, asset, qty))
                {
                    if (q < (asset is "ETH" or "BNB" ? 0.000000001m : 0.0001m)) continue; // rounding dust
                    var share = qty > 0 ? q / qty : 0;
                    decimal? cost = lot.UnitCost is { } u ? Math.Round(u * q, 2) : null;
                    decimal? pr = proceeds is { } p ? Math.Round(p * share, 2) : null;
                    disposals.Add(new
                    {
                        wallet, chain, asset, qty = q, acquired = lot.Acquired == DateTime.MinValue ? (DateTime?)null : lot.Acquired, sold = time,
                        proceeds = pr, cost, gain = pr is { } a && cost is { } b ? a - b : (decimal?)null,
                        term = lot.Acquired == DateTime.MinValue ? "unknown" : (time - lot.Acquired).TotalDays > 365 ? "long" : "short",
                        how, lot = lot.Id, lotHow = lot.How, tx,
                    });
                }
            }

            // Applies the user's answer for a transaction to one asset that moved in it. False when there is no answer.
            bool Answered(EvmRow r, string asset, decimal qty, bool inbound, decimal? px)
            {
                if (labels[r.Tx] is not JsonObject label) return false;
                var kind = label["kind"]?.ToString() ?? "";
                decimal? usd = label["usd"] is JsonValue uv && uv.TryGetValue<decimal>(out var ud) ? ud : null;
                var note = label["note"]?.ToString() ?? "";
                string How(string fallback) => string.IsNullOrEmpty(note) ? fallback : fallback + ": " + note;
                if (inbound)
                {
                    if (kind == "purchase") Acquire(r.Wallet, asset, qty, usd, r.Time, How("Bought (your answer)"));
                    else if (kind == "income") Acquire(r.Wallet, asset, qty, usd ?? (px is { } p0 ? qty * p0 : null), r.Time, How("Income received (your answer)"));
                    else if (kind == "gift-in") Acquire(r.Wallet, asset, qty, usd, r.Time, How("Gift received; cost is the giver's basis (your answer)"));
                    else if (kind == "own") Acquire(r.Wallet, asset, qty, usd, r.Time, How("From your own wallet or account (your answer)"));
                    else if (kind != "ignore") return false;
                    if (kind is "purchase" or "gift-in" && usd is null) notes.Add($"Cost not entered yet for {qty:N2} {asset} received {r.Time:yyyy-MM-dd}{(note == "" ? "" : " (" + note + ")")}.");
                }
                else
                {
                    if (kind == "sale") Dispose(r.Wallet, r.Chain, asset, qty, usd ?? (px is { } p1 ? qty * p1 : null), r.Time, r.Tx, How("Sale (your answer)"));
                    else if (kind is "gift-out" or "own") Take(r.Wallet, asset, qty);
                    else if (kind != "ignore") return false;
                }
                if (label["provisional"] is JsonValue pv0 && pv0.TryGetValue<bool>(out var prov) && prov) notes.Add($"Provisional cost for {qty:N2} {asset} received {r.Time:yyyy-MM-dd}: {note}.");
                return true;
            }

            // Moves lots between the user's wallets (bridges and transfers); 'arrived' can be less than 'sent' (a bridge fee).
            void Move(string from, string to, string asset, decimal sent, decimal arrived, string note)
            {
                var scale = sent > 0 ? arrived / sent : 0;
                foreach (var (lot, q) in Take(from, asset, sent))
                    lots[to].Add(new TitnLot { Id = lot.Id, Asset = asset, Qty = q * scale, UnitCost = lot.UnitCost is { } u && scale > 0 ? u / scale : lot.UnitCost, Acquired = lot.Acquired, How = lot.How + note });
            }

            // Half of a fee belongs to the stablecoin given up: its proceeds fall by that much (a loss of the same amount).
            // (Recorded per transaction and applied when that transaction's USDC / USDT is settled below.)
            decimal rowFeeShare = 0m;
            void UsdFeeShare(string wallet, DateTime time, decimal usd, decimal half) { if (half > 0) rowFeeShare += half; }

            // USDC and USDT in one transaction: lots at $1. Bought, received from sales, rewards and bridges come in; spent
            // on purchases or paid to solvers goes out at $1 less any fee share; own moves and exchange deposits move.
            void SettleStables(EvmRow r, decimal halfFee, SolverLeg? payHere)
            {
                foreach (var leg in r.Stables.Values)
                {
                    var n = leg.Qty; var s = leg.Asset; var q = Math.Abs(n); var cps = leg.Counterparties;
                    if (q < 0.01m) continue;
                    var ownCp = cps.FirstOrDefault(own.Contains);
                    if (ownCp is not null)
                    {
                        if (n < 0) { Move(r.Wallet, ownCp, s, q, q, " (moved from " + r.Wallet[..6] + "…)"); handled.Add(ownCp + "|" + r.Tx); }
                        else Acquire(r.Wallet, s, q, q, r.Time, "Received from your own wallet");
                        continue;
                    }
                    if (cps.Contains(AerodromeTitnPool)) continue; // settled with the liquidity position
                    if (n > 0 && (cps.Overlaps(TitnStakingContracts) || cps.Any(TitnRewardSources.ContainsKey))) { Acquire(r.Wallet, s, q, q, r.Time, "Reward paid in " + s + " (income at $1.00)"); continue; }
                    if (n > 0 && cps.Contains(ZeroAddress)) { Acquire(r.Wallet, s, q, q, r.Time, "Bridged in (minted by Circle, e.g. from Noble / Osmosis); cost $1.00"); continue; }
                    if (midgardByInTx.TryGetValue(r.Tx, out var mg) && n < 0 && mg.InAsset.Contains("USD"))
                    {
                        if (mg.Type == "swap") Dispose(r.Wallet, r.Chain, s, q, q - halfFee, r.Time, r.Tx, $"{s} swapped through {mg.Network}" + (halfFee > 0 ? $", less half the network fee (${halfFee:N2})" : ""));
                        continue; // a refunded deposit comes back: nothing to record
                    }
                    var mOut = n > 0 ? midgard.FirstOrDefault(m => m.Outs.Any(o => o.Address == r.Wallet && o.Asset.Contains("USD") && Math.Abs(o.Amount - q) <= Math.Max(0.02m, q * 0.01m)) && (r.Time - m.Time).TotalDays is > -0.1 and < 10) : null;
                    if (mOut is not null) { if (mOut.Type == "swap") Acquire(r.Wallet, s, q, q, r.Time, $"Bought through {mOut.Network} with {mOut.InAmount:0.####} {mOut.InAsset.Split('-')[0]}"); continue; }
                    var trade = r.Legs.Values.Any(l => Math.Abs(l.Qty) >= 0.000001m && !l.Counterparties.Overlaps(TitnStakingContracts) && !l.Counterparties.Overlaps(own) && !l.Counterparties.Contains(ZeroAddress)
                            && !l.Counterparties.Contains(MergeTgtContract) && !l.Counterparties.Contains(AerodromeTitnPool) && !l.Counterparties.Contains(TitnAirdropContract))
                        || Math.Abs(r.EthIn - r.EthOut) > 0.0000001m;
                    if (trade)
                    {
                        if (n < 0) Dispose(r.Wallet, r.Chain, s, q, q - rowFeeShare, r.Time, r.Tx, $"{s} spent on a purchase in this transaction" + (rowFeeShare > 0.004m ? $", less half the network fee (${rowFeeShare:N2})" : ""));
                        else Acquire(r.Wallet, s, q, q, r.Time, "Received from a sale or swap");
                        continue;
                    }
                    if (n < 0)
                    {
                        if (cps.Overlaps(ExchangeDepositAddresses))
                        {
                            Take(r.Wallet, s, q);
                            movedToExchange.Add(new { wallet = r.Wallet, chain = r.Chain, time = r.Time, tx = r.Tx, asset = s, qty = q, to = cps.First(), valueThatDay = (decimal?)q });
                            continue;
                        }
                        if (payHere is not null && payHere.Asset == "USD") { Dispose(r.Wallet, r.Chain, s, q, q - payHere.FeeHalf, r.Time, r.Tx, $"{s} paid to a cross-chain solver for a purchase"); continue; }
                        if (leg.Plain) { if (!Answered(r, s, q, false, 1m)) unclassified.Add(new { wallet = r.Wallet, chain = r.Chain, time = r.Time, tx = r.Tx, asset = s, qty = q, direction = "out", counterparty = string.Join(",", cps), valueThatDay = (decimal?)q }); continue; }
                        Dispose(r.Wallet, r.Chain, s, q, q, r.Time, r.Tx, $"{s} spent in a contract transaction");
                    }
                    else if (cps.Overlaps(CrossChainSolvers)) Acquire(r.Wallet, s, q, q, r.Time, "Delivered by a cross-chain solver; cost $1.00");
                    else if (leg.Plain && Answered(r, s, q, true, 1m)) { } // e.g. a MoonPay purchase: cost = the cash paid, fees included
                    else Acquire(r.Wallet, s, q, q, r.Time, leg.Plain ? $"Bought or received from {string.Join(",", cps)} (MoonPay, Coinbase, an exchange or a swap; not income); cost $1.00" : "Received in a contract transaction; cost $1.00");
                }
            }

            // What was held (and its cost) at the end of each year, per asset.
            var yearEnd = new SortedDictionary<int, object>();
            var snapYear = rows.Count > 0 ? rows.Min(r => r.Time).Year : DateTime.UtcNow.Year;
            object Snapshot() => lots.Values.SelectMany(l => l).Where(l => l.Qty > 0.0000001m).GroupBy(l => l.Asset).Select(g => new
            {
                asset = g.Key, qty = g.Sum(l => l.Qty), cost = Math.Round(g.Where(l => l.UnitCost.HasValue).Sum(l => l.UnitCost!.Value * l.Qty), 2),
                unknownCostQty = g.Where(l => !l.UnitCost.HasValue).Sum(l => l.Qty),
            }).ToList();

            // Within one transaction the sender's row goes first, so a move between own wallets is settled once.
            foreach (var r in rows.OrderBy(r => r.Time).ThenBy(r => r.Tx).ThenByDescending(r => r.Signed))
            {
                while (r.Time.Year > snapYear) { yearEnd[snapYear] = Snapshot(); snapYear++; }
                if (handled.Contains(r.Wallet + "|" + r.Tx)) continue;
                rowFeeShare = 0m;
                var nat = NativeOf(r.Chain);

                // The network fee: ETH spent at its value that day; half may be allocated to the trade below.
                var feeUsd = r.Fee > 0 ? NatUsd(r.Chain, r.Fee, r.Time) : 0m;
                if (r.Fee > 0)
                {
                    Dispose(r.Wallet, r.Chain, nat, r.Fee, feeUsd, r.Time, r.Tx, $"{nat} spent on the network fee (gas)");
                    feeTotal[r.Time.Year] = (feeTotal.TryGetValue(r.Time.Year, out var ft) ? ft : 0) + feeUsd;
                }
                var halfFee = Math.Round(feeUsd / 2, 4);
                if (r.Signed && midgardByInTx.ContainsKey(r.Tx)) halfFeeByTx[r.Tx] = halfFee;
                var payHere = payments.FirstOrDefault(p => p.Tx == r.Tx && p.Wallet == r.Wallet);
                if (payHere is not null) payHere.FeeHalf = halfFee;

                var ethNet = r.EthIn - r.EthOut;
                var ethDone = false;
                foreach (var leg in r.Legs.Values.Where(l => Math.Abs(l.Qty) >= 0.000001m))
                {
                    var asset = leg.Asset; var qty = Math.Abs(leg.Qty); var inbound = leg.Qty > 0;
                    var cps = leg.Counterparties;
                    var px = AssetPx(asset, r.Time);
                    if (Answered(r, asset, qty, inbound, px)) continue;
                    // NILA staking: a claimReward pays the reward (income at that day's value); a withdraw returns your stake.
                    if (inbound && cps.Contains(NilaStakingContract) && r.Method == NilaClaimSelector)
                    {
                        Acquire(r.Wallet, asset, qty, px is { } pn ? qty * pn : null, r.Time, "NILA staking reward claimed (income at that day's value)");
                        continue;
                    }
                    // Not a sale: staking, bridging between networks.
                    if (cps.Overlaps(TitnStakingContracts) || cps.Contains(ZeroAddress) || cps.Overlaps(TokenBridgeContracts)) continue;
                    // Between the user's own wallets: the lots move with the tokens.
                    var ownCp = cps.FirstOrDefault(own.Contains);
                    if (ownCp is not null)
                    {
                        if (!inbound) { Move(r.Wallet, ownCp, asset, qty, qty, " (moved from " + r.Wallet[..6] + "…)"); handled.Add(ownCp + "|" + r.Tx); }
                        continue;
                    }
                    if (cps.Contains(MergeTgtContract))
                    {
                        if (!receivable.TryGetValue(r.Wallet, out var queue)) receivable[r.Wallet] = queue = new();
                        if (!inbound && asset == "TGT" && carryOver)
                        {
                            // Not a sale: the TGT lots (cost and purchase date) become the TITN this deposit entitles you to.
                            if (mergeClaims.TryGetValue(r.Tx, out var titnOutC))
                                foreach (var (lot, q) in Take(r.Wallet, "TGT", qty))
                                    queue.Enqueue((titnOutC * q / qty, (lot.UnitCost ?? 0m) * q + feeUsd * q / qty, r.Time, lot.Acquired == DateTime.MinValue ? null : lot.Acquired));
                            else notes.Add($"Could not read the TITN claim for the migration deposit {r.Tx}.");
                        }
                        else if (!inbound && asset == "TGT")
                        {
                            var fmv = Px(tgtPx, r.Time) is { } tp ? qty * tp : (decimal?)null;
                            Dispose(r.Wallet, r.Chain, "TGT", qty, fmv - halfFee, r.Time, r.Tx, "Exchanged for TITN in the TGT→TITN migration (MergeTgt), at TGT's value that day" + (halfFee > 0 ? $", less half the network fee (${halfFee:N2})" : ""));
                            if (mergeClaims.TryGetValue(r.Tx, out var titnOut)) queue.Enqueue((titnOut, (fmv ?? 0m) + halfFee, r.Time, null));
                            else notes.Add($"Could not read the TITN claim for the migration deposit {r.Tx}.");
                        }
                        else if (inbound && asset == "TITN")
                        {
                            var left = qty;
                            var claimFee = feeUsd; // the claim's fee is part of what the TITN cost
                            while (left > 0.000001m && queue.Count > 0)
                            {
                                var (t, u, dt, acq) = queue.Peek();
                                var q = Math.Min(t, left);
                                // Carry-over: the TITN keeps the TGT's purchase date (holding period); otherwise it starts on receipt.
                                Acquire(r.Wallet, "TITN", q, (t > 0 ? u * q / t : 0) + claimFee * q / qty, acq ?? r.Time,
                                    carryOver ? $"From the TGT→TITN migration, TGT cost carried over (TGT deposited {dt:yyyy-MM-dd})" : $"From the TGT→TITN migration (TGT deposited {dt:yyyy-MM-dd})");
                                left -= q;
                                queue.Dequeue();
                                if (q < t - 0.000001m) { queue = new Queue<(decimal, decimal, DateTime, DateTime?)>(new[] { (t - q, u * (t - q) / t, dt, acq) }.Concat(queue)); receivable[r.Wallet] = queue; }
                            }
                            if (left > 0.000001m && carryOver)
                            {
                                // Carry-over: the extra share has no cost of its own (any gain shows when it is sold).
                                Acquire(r.Wallet, "TITN", left, claimFee * left / qty, r.Time, "Extra TITN from the migration for leaving TGT in the contract (carry-over basis: no cost)");
                            }
                            else if (left > 0.000001m)
                            {
                                // The extra share for leaving TGT in the contract: more proceeds from the TGT exchange.
                                var value = Px(titnPx, r.Time) is { } p2 ? Math.Round(left * p2, 2) : (decimal?)null;
                                disposals.Add(new
                                {
                                    wallet = r.Wallet, chain = r.Chain, asset = "TGT", qty = 0m, acquired = (DateTime?)null, sold = r.Time, proceeds = value, cost = (decimal?)0m, gain = value,
                                    term = "short", how = $"Extra {left:N2} TITN from the TGT→TITN migration for leaving TGT in the contract: additional amount realized on the TGT exchange, at TITN's value that day",
                                    lot = "", lotHow = "", tx = r.Tx,
                                });
                                Acquire(r.Wallet, "TITN", left, (value ?? 0) + claimFee * left / qty, r.Time, "Extra TITN from the migration (cost = value received)");
                            }
                        }
                        continue;
                    }
                    if (cps.Contains(AerodromeTitnPool))
                    {
                        var cur = lp[r.Wallet];
                        lp[r.Wallet] = (cur.Titn + leg.Qty, cur.Usd + r.Usd, cur.Fee + feeUsd);
                        if (inbound && lastLpWithdrawal.TryGetValue(r.Wallet, out var last) && r.Time == last)
                        {
                            var (nt, nu, nf) = lp[r.Wallet];
                            var h = Math.Round(nf / 2, 4);
                            if (nu > 0.01m) Acquire(r.Wallet, "USDC", nu, nu, r.Time, "USDC gained from the Aerodrome TITN/USDC liquidity position (net)");
                            else if (nu < -0.01m) Dispose(r.Wallet, r.Chain, "USDC", -nu, -nu - h, r.Time, r.Tx, "USDC spent inside the Aerodrome TITN/USDC liquidity position (net)");
                            if (nt < -0.000001m) Dispose(r.Wallet, r.Chain, "TITN", -nt, Math.Max(nu, 0m) - h, r.Time, r.Tx, $"Aerodrome TITN/USDC liquidity closed: {-nt:N2} TITN came back short, sold inside the pool for {nu:N2} USDC net, less half the position's network fees (${h:N2})");
                            else if (nt > 0.000001m) { Acquire(r.Wallet, "TITN", nt, Math.Max(-nu, 0m) + h, r.Time, $"Aerodrome TITN/USDC liquidity closed: {nt:N2} extra TITN bought inside the pool for {-nu:N2} USDC net, plus half the position's network fees (${h:N2})"); UsdFeeShare(r.Wallet, r.Time, Math.Max(-nu, 0m), h); }
                            lp[r.Wallet] = (0m, 0m, 0m);
                        }
                        continue;
                    }
                    if (cps.Contains(TitnAirdropContract) && inbound)
                    {
                        Acquire(r.Wallet, "TITN", qty, px is { } pa ? qty * pa : null, r.Time, "TITN airdrop award (income at that day's value)");
                        continue;
                    }
                    // Cross-chain swaps through THORChain / Maya.
                    var mAsset = asset == "TGT" ? "TGT-" : "TITN-";
                    var mid = inbound
                        ? midgard.FirstOrDefault(m => m.Type == "swap" && !usedMidgard.Contains(m) && m.Outs.Any(o => o.Asset.Contains(mAsset, StringComparison.OrdinalIgnoreCase) && o.Address == r.Wallet && Math.Abs(o.Amount - qty) < 0.01m) && (r.Time - m.Time).TotalDays is > -0.5 and < 10)
                        : midgard.FirstOrDefault(m => m.Type == "swap" && !usedMidgard.Contains(m) && m.InAsset.Contains(mAsset, StringComparison.OrdinalIgnoreCase) && m.InAddress == r.Wallet && Math.Abs(m.InAmount - qty) < 0.01m && Math.Abs((m.Time - r.Time).TotalHours) < 12);
                    if (mid is not null)
                    {
                        usedMidgard.Add(mid);
                        var srcHalf = halfFeeByTx.TryGetValue(mid.InTx, out var sh) ? sh : 0m;
                        if (inbound) Acquire(r.Wallet, asset, qty, mid.InUsd + srcHalf, r.Time, $"Bought through {mid.Network}: {mid.InAmount:0.######} {mid.InAsset.Split('-')[0]} (${mid.InUsd:N2}){(srcHalf > 0 ? $" plus half the network fee (${srcHalf:N2})" : "")}");
                        else Dispose(r.Wallet, r.Chain, asset, qty, mid.InUsd - halfFee, r.Time, r.Tx, $"Sold through {mid.Network} (${mid.InUsd:N2} at the time){(halfFee > 0 ? $", less half the network fee (${halfFee:N2})" : "")}");
                        continue;
                    }
                    // Paid or received in the same transaction (DEX swaps, aggregators, relayers).
                    var ethPaid = ethNet < 0 ? -ethNet : 0m; var ethGot = ethNet > 0 ? ethNet : 0m;
                    var ethPaidUsd = NatUsd(r.Chain, ethPaid, r.Time); var ethGotUsd = NatUsd(r.Chain, ethGot, r.Time);
                    if (inbound && (r.Usd < -0.01m || ethPaid > 0))
                    {
                        var usdPaid = Math.Max(-r.Usd, 0m); var total = usdPaid + ethPaidUsd;
                        Acquire(r.Wallet, asset, qty, total + halfFee, r.Time, $"Bought: {(usdPaid > 0 ? $"{usdPaid:N2} USDC/USDT" : "")}{(usdPaid > 0 && ethPaid > 0 ? " + " : "")}{(ethPaid > 0 ? $"{ethPaid:0.######} {nat}" : "")}{(halfFee > 0 ? $", plus half the network fee (${halfFee:N2})" : "")}");
                        if (ethPaid > 0) { Dispose(r.Wallet, r.Chain, nat, ethPaid, ethPaidUsd - (total > 0 ? halfFee * ethPaidUsd / total : 0), r.Time, r.Tx, $"{nat} spent buying {qty:N2} {asset}"); ethDone = true; }
                        if (usdPaid > 0) UsdFeeShare(r.Wallet, r.Time, usdPaid, total > 0 ? halfFee * usdPaid / total : 0);
                        continue;
                    }
                    if (!inbound && (r.Usd > 0.01m || ethGot > 0))
                    {
                        var usdGot = Math.Max(r.Usd, 0m); var total = usdGot + ethGotUsd;
                        Dispose(r.Wallet, r.Chain, asset, qty, total - halfFee, r.Time, r.Tx, $"Sold for {(usdGot > 0 ? $"{usdGot:N2} USDC" : "")}{(usdGot > 0 && ethGot > 0 ? " + " : "")}{(ethGot > 0 ? $"{ethGot:0.######} {nat}" : "")}{(halfFee > 0 ? $", less half the network fee (${halfFee:N2})" : "")}");
                        if (ethGot > 0) { Acquire(r.Wallet, nat, ethGot, ethGotUsd + (total > 0 ? halfFee * ethGotUsd / total : 0), r.Time, $"Received for {qty:N2} {asset} sold"); ethDone = true; }
                        continue;
                    }
                    // Cross-chain solver swaps: the solver delivers what you bought within minutes of your payment.
                    if (inbound && cps.Overlaps(CrossChainSolvers))
                    {
                        var pay = payments.Where(p => !p.Used && (r.Time - p.Time).TotalMinutes is >= -2 and <= 10 && CloseValue(p.Usd, px is { } pv0 ? qty * pv0 : null)).OrderBy(p => Math.Abs((r.Time - p.Time).TotalSeconds)).FirstOrDefault();
                        if (pay is not null)
                        {
                            pay.Used = true;
                            Acquire(r.Wallet, asset, qty, pay.Usd + pay.FeeHalf, r.Time, $"Bought through a cross-chain solver: paid {pay.What}{(pay.FeeHalf > 0 ? $", plus half the network fee (${pay.FeeHalf:N2})" : "")}");
                            if (pay.Asset == "USD") UsdFeeShare(pay.Wallet, pay.Time, pay.Usd, pay.FeeHalf);
                        }
                        else { Acquire(r.Wallet, asset, qty, px is { } pp ? qty * pp : null, r.Time, "Bought through a cross-chain solver; the payment was not found, cost is that day's market value"); notes.Add($"{qty:N2} {asset} delivered by a cross-chain solver on {r.Time:yyyy-MM-dd}: no matching payment found, cost set to that day's market value."); }
                        continue;
                    }
                    if (!inbound && leg.Plain)
                    {
                        var got = deliveries.Where(d => d.Solver && !d.Used && (d.Time - r.Time).TotalMinutes is >= -1 and <= 10 && CloseValue(px is { } pv1 ? qty * pv1 : 0m, d.Usd)).OrderBy(d => d.Time).FirstOrDefault();
                        if (got is not null)
                        {
                            got.Used = true;
                            Dispose(r.Wallet, r.Chain, asset, qty, got.Usd - halfFee, r.Time, r.Tx, $"Sold through a cross-chain solver: received {got.What}{(halfFee > 0 ? $", less half the network fee (${halfFee:N2})" : "")}");
                            if (got.Asset == nat) { Acquire(got.Wallet, nat, got.Qty, got.Usd + halfFee, got.Time, $"Received for {qty:N2} {asset} sold through a cross-chain solver"); handled.Add(got.Wallet + "|" + got.Tx); }
                            continue;
                        }
                    }
                    if (!inbound && !leg.Plain)
                    {
                        Dispose(r.Wallet, r.Chain, asset, qty, px is { } ps ? qty * ps : null, r.Time, r.Tx, $"Swapped (what came back is not visible); valued at {asset}'s price that day");
                        continue;
                    }
                    // Plain transfer to or from another address: not guessed.
                    if (inbound) Acquire(r.Wallet, asset, qty, null, r.Time, "Received from " + string.Join(",", cps) + " (not classified, cost unknown)");
                    unclassified.Add(new { wallet = r.Wallet, chain = r.Chain, time = r.Time, tx = r.Tx, asset, qty, direction = inbound ? "in" : "out", counterparty = string.Join(",", cps), valueThatDay = px is { } pv ? Math.Round(qty * pv, 2) : (decimal?)null });
                }

                // ETH that moved in this transaction (the fee is already handled above).
                if (ethDone || Math.Abs(ethNet) < 0.000000001m) { SettleStables(r, halfFee, payHere); continue; }
                var tokenMoves = r.Legs.Values.Any(l => Math.Abs(l.Qty) >= 0.000001m);
                if (ethNet < 0)
                {
                    var q = -ethNet; var usd = NatUsd(r.Chain, q, r.Time);
                    if (own.Contains(r.EthOutTo))
                    {
                        Move(r.Wallet, r.EthOutTo, nat, q, q, " (moved from " + r.Wallet[..6] + "…)");
                        handled.Add(r.EthOutTo + "|" + r.Tx);
                    }
                    else if (midgardByInTx.TryGetValue(r.Tx, out var mg))
                    {
                        // ETH paid into THORChain / Maya. A refunded swap is not a sale (the ETH comes back).
                        if (mg.Type == "swap") disposedMidgardIn.Add(mg.InTx);
                        if (mg.Type == "swap") Dispose(r.Wallet, r.Chain, nat, q, mg.InUsd - halfFee, r.Time, r.Tx, $"{nat} swapped through {mg.Network} for {string.Join(" + ", mg.Outs.Where(o => o.Address == r.Wallet).Select(o => $"{o.Amount:0.####} {o.Asset.Split('-')[0]}"))}{(halfFee > 0 ? $", less half the network fee (${halfFee:N2})" : "")}");
                    }
                    else if (r.Usd > 0.01m)
                    {
                        Dispose(r.Wallet, r.Chain, nat, q, r.Usd - halfFee, r.Time, r.Tx, $"{nat} sold for {r.Usd:N2} USDC/USDT{(halfFee > 0 ? $", less half the network fee (${halfFee:N2})" : "")}");
                    }
                    else if (!tokenMoves && r.Method is "" or null && !IsExplainedContract(r.EthOutTo))
                    {
                        // Plain ETH to a one-time address: the payment side of a solver bridge or swap. An ETH delivery
                        // must be 80–100% of what was sent (the rest is the bridge's fee).
                        var got = deliveries.Where(d => !d.Used && (d.Time - r.Time).TotalMinutes is >= -1 and <= 10 && (d.Asset != nat || (d.Qty <= q * 1.0001m && d.Qty >= q * 0.8m))).OrderBy(d => d.Time).FirstOrDefault();
                        var tokenDelivery = rows.Any(x => x.Legs.Values.Any(l => l.Qty > 0 && l.Counterparties.Overlaps(CrossChainSolvers)) && (x.Time - r.Time).TotalMinutes is >= -1 and <= 10);
                        if (got is not null && got.Asset == nat)
                        {
                            got.Used = true;
                            var arrived = Math.Min(q, got.Qty);
                            Move(r.Wallet, got.Wallet, nat, arrived, arrived, $" (bridged {r.Chain}→{got.Chain})");
                            // What the bridge kept is ETH spent on its fee (not added to the moved ETH's cost).
                            if (q - arrived > 0.000000001m) Dispose(r.Wallet, r.Chain, nat, q - arrived, NatUsd(r.Chain, q - arrived, r.Time), r.Time, r.Tx, $"{nat} kept by the bridge as its fee ({r.Chain}→{got.Chain})");
                            handled.Add(got.Wallet + "|" + got.Tx);
                        }
                        else if (got is not null && got.Asset != "USD" && got.Asset != nat) { got.Used = true; Dispose(r.Wallet, r.Chain, nat, q, usd - halfFee, r.Time, r.Tx, $"{nat} bridged into {got.What} (a swap), at its value that day"); }
                        else if (got is not null && got.Asset == "USD") { got.Used = true; Dispose(r.Wallet, r.Chain, nat, q, got.Usd - halfFee, r.Time, r.Tx, $"{nat} sold through a cross-chain solver for {got.What}"); }
                        else if (tokenDelivery) Dispose(r.Wallet, r.Chain, nat, q, usd - halfFee, r.Time, r.Tx, $"{nat} paid to a cross-chain solver for tokens it delivered, at its value that day");
                        else if (ExchangeDepositAddresses.Contains(r.EthOutTo))
                        {
                            // Moved into your exchange account: not a sale; the ETH leaves the wallets tracked here.
                            Take(r.Wallet, nat, q);
                            movedToExchange.Add(new { wallet = r.Wallet, chain = r.Chain, time = r.Time, tx = r.Tx, asset = nat, qty = q, to = r.EthOutTo, valueThatDay = (decimal?)usd });
                        }
                        else if (!Answered(r, nat, q, false, q > 0 ? usd / q : null)) unclassified.Add(new { wallet = r.Wallet, chain = r.Chain, time = r.Time, tx = r.Tx, asset = nat, qty = q, direction = "out", counterparty = r.EthOutTo, valueThatDay = (decimal?)usd });
                    }
                    else
                    {
                        // ETH sent along with a contract call (bridge or messaging fee, service): spent at its value.
                        Dispose(r.Wallet, r.Chain, nat, q, usd, r.Time, r.Tx, BridgeRouters.Contains(r.EthOutTo) ? $"{nat} paid into MetaMask Bridge for what it delivered on another network, at its value that day" : $"{nat} paid to a contract with the transaction (bridge or service fee)");
                    }
                }
                else
                {
                    var q = ethNet; var usd = NatUsd(r.Chain, q, r.Time);
                    var fromOwn = r.EthInFrom.FirstOrDefault(own.Contains);
                    var mOut = midgard.FirstOrDefault(m => !usedMidgard.Contains(m) && m.Outs.Any(o => o.Address == r.Wallet && o.Asset is "ETH.ETH" or "ARB.ETH" or "BASE.ETH" && Math.Abs(o.Amount - q) <= Math.Max(0.00002m, q * 0.01m)) && (r.Time - m.Time).TotalDays is > -0.1 and < 10);
                    if (fromOwn is not null) Acquire(r.Wallet, nat, q, usd, r.Time, "Received from your own wallet (not matched to its send)");
                    else if (Answered(r, nat, q, true, q > 0 ? usd / q : null)) { } // your answer, e.g. a MoonPay purchase at the cash paid
                    else if (mOut is not null)
                    {
                        usedMidgard.Add(mOut);
                        if (mOut.Type == "swap" && !mOut.InAsset.EndsWith(".ETH", StringComparison.Ordinal))
                        {
                            var srcHalf = halfFeeByTx.TryGetValue(mOut.InTx, out var sh) ? sh : 0m;
                            Acquire(r.Wallet, nat, q, mOut.InUsd + srcHalf, r.Time, $"Bought through {mOut.Network}: {mOut.InAmount:0.####} {mOut.InAsset.Split('-')[0]} (${mOut.InUsd:N2})");
                            if (mOut.InAsset.Contains("USD")) UsdFeeShare(r.Wallet, r.Time, mOut.InUsd, srcHalf);
                        }
                        else if (disposedMidgardIn.Contains(mOut.InTx) || !rows.Any(x => x.Tx == mOut.InTx))
                            // ETH coming back (a refund or change) from a deposit already counted as spent: received again at its value.
                            Acquire(r.Wallet, nat, q, usd, r.Time, $"{nat} returned by {mOut.Network} (refund or change)");
                        // Otherwise it is the refund of a deposit that was never counted as spent: nothing to record.
                    }
                    else if (r.Usd < -0.01m)
                    {
                        Acquire(r.Wallet, nat, q, -r.Usd + halfFee, r.Time, $"Bought with {-r.Usd:N2} USDC/USDT{(halfFee > 0 ? $", plus half the network fee (${halfFee:N2})" : "")}");
                        UsdFeeShare(r.Wallet, r.Time, -r.Usd, halfFee);
                    }
                    else if (r.EthInFrom.Overlaps(CrossChainSolvers))
                    {
                        var pay = payments.Where(p => !p.Used && (p.Asset == "USD" || p.Asset != nat) && CloseValue(p.Usd, usd) && (r.Time - p.Time).TotalMinutes is >= -2 and <= 10).OrderBy(p => Math.Abs((r.Time - p.Time).TotalSeconds)).FirstOrDefault();
                        if (pay is not null) { pay.Used = true; Acquire(r.Wallet, nat, q, pay.Usd + pay.FeeHalf, r.Time, $"Bought through a cross-chain solver: paid {pay.What}"); UsdFeeShare(pay.Wallet, pay.Time, pay.Usd, pay.FeeHalf); }
                        else { Acquire(r.Wallet, nat, q, usd, r.Time, "Delivered by a cross-chain solver; no matching payment found, cost is that day's value"); provisionalEth = (provisionalEth.Qty + q, provisionalEth.Usd + usd, provisionalEth.N + 1); }
                    }
                    else if (r.EthInFrom.Overlaps(TokenBridgeContracts)) Acquire(r.Wallet, nat, q, usd, r.Time, "Unused bridge gas returned (cost = its value)");
                    else
                    {
                        // From an exchange (Coinbase and others) or another outside address: bought; cost is that day's
                        // price until the exchange's own records are loaded.
                        Acquire(r.Wallet, nat, q, usd, r.Time, $"Received from {string.Join(",", r.EthInFrom)} (bought on an exchange; cost = that day's price, provisional)");
                        provisionalEth = (provisionalEth.Qty + q, provisionalEth.Usd + usd, provisionalEth.N + 1);
                    }
                }
                SettleStables(r, halfFee, payHere);
            }
            while (snapYear <= DateTime.UtcNow.Year) { yearEnd[snapYear] = Snapshot(); snapYear++; }

            if (provisionalEth.N > 0) notes.Add($"{provisionalEth.Qty:0.####} ETH received from exchanges or other outside addresses in {provisionalEth.N} transfers is costed at that day's price (${provisionalEth.Usd:N2}). Load your Coinbase transaction history to use what you actually paid.");

            decimal? LastPx(SortedDictionary<string, decimal> d) => d.Count > 0 ? d.Last().Value : null;
            var price = new Dictionary<string, decimal?> { ["TITN"] = LastPx(titnPx), ["TGT"] = LastPx(tgtPx), ["ETH"] = LastPx(ethPx), ["BNB"] = LastPx(bnbPx), ["NILA"] = LastPx(nilaPx), ["PATRIOT"] = LastPx(patriotPx) };
            return new
            {
                disposals,
                holdings = lots.SelectMany(kv => kv.Value.Where(l => l.Qty > 0.0000001m).Select(l => new { wallet = kv.Key, asset = l.Asset, qty = l.Qty, unitCost = l.UnitCost, cost = l.UnitCost is { } u ? Math.Round(u * l.Qty, 2) : (decimal?)null, acquired = l.Acquired, how = l.How, id = l.Id })),
                unclassified,
                movedToExchange,
                notes,
                price,
                yearEnd,
                migration = carryOver ? "carryover" : "exchange",
                feesByYear = feeTotal.ToDictionary(kv => kv.Key.ToString(CultureInfo.InvariantCulture), kv => Math.Round(kv.Value, 2)),
                updatedUtc = DateTime.UtcNow,
            };
        }
        finally { EvmGate.Release(); }
    }
}

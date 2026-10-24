using OPTIC.Services;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

// An Osmosis account as one of the user's wallets. Its OPT activity is turned into ledger entries the tax engine
// already understands:
//   - IBC transfers to and from the user's Optio wallets become moves between own wallets (lots carry over); they are
//     matched to the Optio side transaction by amount and time, and the entry uses the Optio transaction hash so both
//     sides pair up.
//   - Swaps out of OPT are sales at the USDC actually received (other tokens: the day's value); swaps into OPT are
//     purchases at the USDC actually paid (other tokens: the day's value).
//   - Concentrated-liquidity positions are followed by position id (adding to a position continues it). When a
//     position is withdrawn, OPT that came back short of what went in was sold inside the pool for the net USDC gained;
//     extra OPT was bought for the net USDC paid in.
//   - Spread rewards paid in OPT are income at the day's value.
// History comes from the Osmosis archive node and is cached in odata/osmosis/<address>.json (new blocks only after that).
public static partial class WebDashboard
{
    const string OsmosisArchiveLcd = "https://lcd.archive.osmosis.zone";
    const string OsmosisOptDenomPrefix = "ibc/6AA47B";
    static readonly HashSet<string> OsmosisUsdDenoms = new(StringComparer.Ordinal)
    {
        "ibc/498A0751C798A0D9A389AA3691123DADA57DAA4FE165D5C75894505B876BA6E4", // Noble USDC
        "factory/osmo147h5x9pcj7lm0cttlaefx6sqq5vdfnmwfcqxkmjd7exqm9gc7grqhr75m0/alloyed/allUSDC",
        "factory/osmo1em6xs47hd82806f5cxgyufguxrrc7l0aqx7nzzptjuqgswczk8csavdxek/alloyed/allUSDT",
    };
    static readonly SemaphoreSlim OsmosisWalletGate = new(1, 1);
    static readonly HttpClient OsmosisArchiveHttp = new() { Timeout = TimeSpan.FromSeconds(90) };

    internal static bool IsOsmoAddress(string? a) => a is not null && Regex.IsMatch(a, "^osmo1[02-9ac-hj-np-z]{38,58}$");

    static void MapOsmosisWallet(WebApplication app)
    {
        app.MapGet("/api/osmosis/ledger", async (HttpContext ctx) =>
        {
            var address = (ctx.Request.Query["address"].ToString() ?? "").Trim().ToLowerInvariant();
            if (!IsOsmoAddress(address)) return VError("Enter a valid osmo1... address.");
            var peers = (ctx.Request.Query["peers"].ToString() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(p => p.ToLowerInvariant()).Where(p => Regex.IsMatch(p, "^optio1[02-9ac-hj-np-z]{38,58}$")).Distinct().ToList();
            try { return VJson(await BuildOsmosisLedgerAsync(address, peers, ctx.Request.Query["refresh"] == "1", ctx.RequestAborted)); }
            catch (Exception ex) when (ex is not OperationCanceledException) { return VError("Could not read the Osmosis history: " + ex.Message, 502); }
        });
    }

    sealed class OsmoTx
    {
        public long Height { get; set; }
        public DateTime Time { get; set; }
        public string Hash { get; set; } = "";
        public List<string> Types { get; set; } = new();
        public List<JsonElement> Events { get; set; } = new();
        public Dictionary<string, decimal> Fee { get; set; } = new();
    }

    static string OsmosisCachePath(string address) => Path.Combine(Environment.CurrentDirectory, "odata", "osmosis", address + ".json");

    static readonly HashSet<string> OsmosisRefreshing = new(StringComparer.Ordinal);

    // Every successful transaction the address signed or received tokens in, from the archive node (cached). Once a
    // cache exists it is used straight away; when it is over 6 hours old it is brought up to date in the background, so
    // a slow or unreachable archive node never holds up a report.
    static async Task<List<OsmoTx>> LoadOsmosisTxsAsync(string address, bool refresh, CancellationToken ct)
    {
        var path = OsmosisCachePath(address);
        var cache = File.Exists(path) ? JsonNode.Parse(await File.ReadAllTextAsync(path, ct))!.AsObject() : new JsonObject { ["scannedToHeight"] = 0L, ["txs"] = new JsonArray() };
        DateTime? updated = cache["updatedUtc"] is { } u && DateTime.TryParse(u.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var ut) ? ut : null;
        if (refresh || updated is null) await UpdateOsmosisCacheAsync(address, cache, ct);
        else if (DateTime.UtcNow - updated > TimeSpan.FromHours(6))
        {
            lock (OsmosisRefreshing) if (!OsmosisRefreshing.Add(address)) goto use;
            _ = Task.Run(async () =>
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(20));
                try
                {
                    var fresh = JsonNode.Parse(await File.ReadAllTextAsync(path, cts.Token))!.AsObject();
                    await UpdateOsmosisCacheAsync(address, fresh, cts.Token);
                }
                catch (Exception ex) { Console.WriteLine($"Osmosis history refresh for {address} failed (the saved copy stays in use): {ex.Message}"); }
                finally { lock (OsmosisRefreshing) OsmosisRefreshing.Remove(address); }
            });
        }
        use:
        return OsmoTxsFromCache(cache["txs"]!.AsArray());
    }

    // Public Osmosis REST servers used when the archive node fails. They keep recent history only, so one is used only
    // if it still has the first block after what the cache already covers (no gap can be skipped).
    static readonly string[] OsmosisBackupLcds =
    {
        "https://osmosis-api.polkachu.com",
        "https://rest.cosmos.directory/osmosis",
        "https://osmosis-rest.publicnode.com",
    };
    static readonly HttpClient OsmosisHistoryHttp = new() { Timeout = TimeSpan.FromSeconds(45) };

    // Adds new transactions to the cache and saves it: from the archive node, or a backup server that covers the gap.
    static async Task UpdateOsmosisCacheAsync(string address, JsonObject cache, CancellationToken ct)
    {
        var scanned = long.Parse(cache["scannedToHeight"]!.ToJsonString(), CultureInfo.InvariantCulture);
        var errors = new List<string>();
        foreach (var lcd in new[] { OsmosisArchiveLcd }.Concat(OsmosisBackupLcds))
        {
            try
            {
                if (lcd != OsmosisArchiveLcd)
                {
                    // A backup needs the full history for a first scan, and otherwise the first block not yet covered.
                    if (scanned <= 0) continue;
                    using var block = await OsmosisHistoryHttp.GetAsync($"{lcd}/cosmos/base/tendermint/v1beta1/blocks/{scanned + 1}", ct);
                    if (!block.IsSuccessStatusCode) { errors.Add($"{lcd}: no longer has block {scanned + 1}"); continue; }
                }
                await ScanOsmosisHistoryAsync(lcd, address, cache, scanned, ct);
                if (errors.Count > 0) Console.WriteLine($"Osmosis history for {address} read from {lcd} ({string.Join("; ", errors)})");
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
            {
                errors.Add($"{lcd}: {ex.Message}");
            }
        }
        throw new InvalidOperationException("No Osmosis history server could be read: " + string.Join("; ", errors));
    }

    static async Task ScanOsmosisHistoryAsync(string lcd, string address, JsonObject cache, long scanned, CancellationToken ct)
    {
        var path = OsmosisCachePath(address);
        var txs = cache["txs"]!.AsArray();
        // The chain height before scanning: everything up to it is covered afterwards, even with no new transactions.
        long chainHeight = 0;
        using (var latest = JsonDocument.Parse(await OsmosisHistoryHttp.GetStringAsync($"{lcd}/cosmos/base/tendermint/v1beta1/blocks/latest", ct)))
            chainHeight = long.Parse(latest.RootElement.GetProperty("block").GetProperty("header").GetProperty("height").GetString()!, CultureInfo.InvariantCulture);
        {
            var seen = new HashSet<string>(txs.Select(t => t!["txhash"]!.GetValue<string>()), StringComparer.Ordinal);
            long maxHeight = Math.Max(scanned, chainHeight);
            foreach (var q in new[] { $"message.sender='{address}'", $"transfer.recipient='{address}'" })
            {
                var query = scanned > 0 ? $"{q} AND tx.height>{scanned}" : q;
                for (var page = 1; page <= 100; page++)
                {
                    var url = $"{lcd}/cosmos/tx/v1beta1/txs?query={Uri.EscapeDataString(query)}&page={page}&limit=100&order_by=ORDER_BY_ASC";
                    using var doc = JsonDocument.Parse(await (lcd == OsmosisArchiveLcd ? OsmosisArchiveHttp : OsmosisHistoryHttp).GetStringAsync(url, ct));
                    var list = doc.RootElement.TryGetProperty("tx_responses", out var tr) ? tr.EnumerateArray().ToList() : new List<JsonElement>();
                    foreach (var r in list)
                    {
                        var hash = r.GetProperty("txhash").GetString()!;
                        var h = long.Parse(r.GetProperty("height").GetString()!, CultureInfo.InvariantCulture);
                        if (h > maxHeight) maxHeight = h;
                        if (!seen.Add(hash) || r.GetProperty("code").GetInt32() != 0) continue;
                        var keep = new JsonObject
                        {
                            ["txhash"] = hash, ["height"] = h, ["timestamp"] = r.GetProperty("timestamp").GetString(),
                            ["types"] = new JsonArray(r.GetProperty("tx").GetProperty("body").GetProperty("messages").EnumerateArray().Select(m => (JsonNode)JsonValue.Create(m.GetProperty("@type").GetString()!.Split('.').Last())!).ToArray()),
                            ["fee"] = JsonNode.Parse(r.GetProperty("tx").GetProperty("auth_info").GetProperty("fee").GetProperty("amount").GetRawText()),
                            ["events"] = new JsonArray(r.GetProperty("events").EnumerateArray()
                                .Where(e => e.GetProperty("type").GetString() is "coin_spent" or "coin_received" or "create_position" or "withdraw_position" or "add_to_position" or "collect_spread_rewards")
                                .Select(e => JsonNode.Parse(e.GetRawText())).ToArray()),
                        };
                        txs.Add(keep);
                    }
                    var total = doc.RootElement.TryGetProperty("total", out var tt) && long.TryParse(tt.GetString(), out var tv) ? tv
                        : doc.RootElement.TryGetProperty("pagination", out var pg) && pg.TryGetProperty("total", out var pt) && long.TryParse(pt.GetString(), out var pv) ? pv : 0;
                    if (list.Count == 0 || page * 100 >= total) break;
                }
            }
            cache["scannedToHeight"] = maxHeight;
            cache["source"] = lcd;
            cache["updatedUtc"] = DateTime.UtcNow;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, cache.ToJsonString(), ct);
        }
    }

    static List<OsmoTx> OsmoTxsFromCache(JsonArray txs) =>
        txs.Select(t =>
        {
            using var d = JsonDocument.Parse(t!.ToJsonString());
            var r = d.RootElement;
            return new OsmoTx
            {
                Hash = r.GetProperty("txhash").GetString()!,
                Height = r.GetProperty("height").GetInt64(),
                Time = DateTime.Parse(r.GetProperty("timestamp").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
                Types = r.GetProperty("types").EnumerateArray().Select(x => x.GetString()!).ToList(),
                Events = r.GetProperty("events").EnumerateArray().Select(e => e.Clone()).ToList(),
                Fee = r.GetProperty("fee").EnumerateArray().GroupBy(c => c.GetProperty("denom").GetString()!).ToDictionary(g => g.Key, g => g.Sum(c => decimal.Parse(c.GetProperty("amount").GetString()!, CultureInfo.InvariantCulture) / 1_000_000m)),
            };
        }).OrderBy(t => t.Height).ToList();

    static Dictionary<string, string> EventAttrs(JsonElement e) =>
        e.GetProperty("attributes").EnumerateArray().GroupBy(a => a.GetProperty("key").GetString()!).ToDictionary(g => g.Key, g => g.First().GetProperty("value").GetString() ?? "");

    static IEnumerable<(decimal Amount, string Denom)> ParseCoinList(string s) =>
        s.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(c => Regex.Match(c.Trim(), @"^(\d+)(.+)$")).Where(m => m.Success)
            .Select(m => (decimal.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) / 1_000_000m, m.Groups[2].Value));

    static async Task<object> BuildOsmosisLedgerAsync(string address, List<string> peers, bool refresh, CancellationToken ct)
    {
        await OsmosisWalletGate.WaitAsync(ct);
        List<OsmoTx> txs;
        try { txs = await LoadOsmosisTxsAsync(address, refresh, ct); }
        finally { OsmosisWalletGate.Release(); }

        bool IsOpt(string d) => d.StartsWith(OsmosisOptDenomPrefix, StringComparison.Ordinal);
        var entries = new List<WalletLedgerEntry>();
        var labels = new Dictionary<string, object>(StringComparer.Ordinal);
        var links = new List<object>();
        var notes = new List<string>();
        var otherTokenFees = 0m;

        // The Optio side of the IBC transfers: MsgTransfer sends from the user's wallets and MsgRecvPacket receipts to them.
        var optioIbc = new List<(string Hash, string Wallet, string Dir, decimal Opt, DateTime Time)>();
        if (peers.Count > 0)
        {
            using var rpc = CreateRpcContext();
            // IBC escrow accounts of the open transfer channels on Optio (all lead to Osmosis): sends go into them and
            // receipts come out of them.
            var escrows = new List<string>();
            var lcd = LoadDashboardConfig().GetValueOrDefault("lcd", "http://127.0.0.1:1317");
            using (var chDoc = await LcdGetAsync(lcd, "/ibc/core/channel/v1/channels?pagination.limit=200", ct))
                if (chDoc is not null && chDoc.RootElement.TryGetProperty("channels", out var chs))
                    foreach (var ch in chs.EnumerateArray().Where(c => c.GetProperty("port_id").GetString() == "transfer"))
                    {
                        using var e = await LcdGetAsync(lcd, $"/ibc/apps/transfer/v1/channels/{ch.GetProperty("channel_id").GetString()}/ports/transfer/escrow_address", ct);
                        if (e is not null && e.RootElement.TryGetProperty("escrow_address", out var ea) && ea.GetString() is { } addr) escrows.Add(addr);
                    }
            foreach (var peer in peers)
                foreach (var (q, dir) in new[] { ($"message.sender='{peer}' AND message.action='/ibc.applications.transfer.v1.MsgTransfer'", "out") }
                    .Concat(escrows.Select(esc => ($"transfer.sender='{esc}' AND transfer.recipient='{peer}'", "in"))))
                {
                    var found = new List<(string, long, decimal)>();
                    await SearchTxsAsync(rpc.Http, q, (tx, hash, height) =>
                    {
                        var units = BigInteger.Zero;
                        foreach (var t in GetTxTransfers(tx, rpc.DenomWantedUpper))
                            if (t.Recipient != FeeCollector && (dir == "out" ? t.Sender.Equals(peer, StringComparison.OrdinalIgnoreCase) : t.Recipient.Equals(peer, StringComparison.OrdinalIgnoreCase)))
                                units += t.AmountUnits;
                        if (units > 0) found.Add((hash, height, ToDecimalOpt(units, rpc.ScaleInt)));
                    }, ct);
                    await ResolveBlockTimesAsync(rpc.Http, found.Select(f => f.Item2), ct);
                    foreach (var (hash, height, opt) in found)
                        if (_blockTimeCache.TryGetValue(height, out var time)) optioIbc.Add((hash, peer, dir, opt, time));
                }
        }
        var usedOptio = new HashSet<string>(StringComparer.Ordinal);
        (string Hash, string Wallet)? MatchOptio(string optioDir, decimal opt, DateTime time, bool optioFirst)
        {
            var m = optioIbc.Where(x => x.Dir == optioDir && !usedOptio.Contains(x.Hash) && Math.Abs(x.Opt - opt) <= 0.01m
                    && (optioFirst ? x.Time <= time.AddMinutes(2) && x.Time >= time.AddHours(-3) : x.Time >= time.AddMinutes(-2) && x.Time <= time.AddHours(3)))
                .OrderBy(x => Math.Abs((x.Time - time).TotalSeconds)).Cast<(string Hash, string Wallet, string Dir, decimal Opt, DateTime Time)?>().FirstOrDefault();
            if (m is null) return null;
            usedOptio.Add(m.Value.Hash);
            return (m.Value.Hash, m.Value.Wallet);
        }

        WalletLedgerEntry Entry(OsmoTx t, string hash, string dir, string cp, decimal opt) => new()
        {
            TimeUtc = t.Time, Height = t.Height, TxHash = hash, Memo = "", Direction = dir, Counterparty = cp, Category = "wallet", AmountOpt = Math.Round(opt, 6),
        };
        string Key(WalletLedgerEntry e) => $"{e.TxHash}|{e.Direction}|{e.Counterparty}";

        // Liquidity positions: position id -> chain (adding to a position continues the chain).
        var chainOf = new Dictionary<string, string>(StringComparer.Ordinal);
        // Liquidity still in each position (withdrawals can be partial; a position settles only when emptied).
        var liquidityOf = new Dictionary<string, double>(StringComparer.Ordinal);
        static double Liq(Dictionary<string, string> at) => double.TryParse(at.GetValueOrDefault("liquidity", "0"), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
        var chains = new Dictionary<string, (decimal OptIn, decimal UsdIn, decimal OptOut, decimal UsdOut, DateTime Opened, decimal OtherIn)>(StringComparer.Ordinal);
        // An IBC transfer out that timed out is refunded in a later MsgTimeout; both sides are left out.
        var pendingIbcOut = new List<(OsmoTx Tx, decimal Opt)>();

        foreach (var t in txs)
        {
            // Net change of this address, per denom, without the fee and without spread rewards (counted separately).
            var net = new Dictionary<string, decimal>(StringComparer.Ordinal);
            var rewards = new Dictionary<string, decimal>(StringComparer.Ordinal);
            foreach (var e in t.Events)
            {
                var type = e.GetProperty("type").GetString();
                var at = EventAttrs(e);
                if (type == "coin_received" && at.GetValueOrDefault("receiver") == address) foreach (var (a, d) in ParseCoinList(at.GetValueOrDefault("amount", ""))) net[d] = net.GetValueOrDefault(d) + a;
                if (type == "coin_spent" && at.GetValueOrDefault("spender") == address) foreach (var (a, d) in ParseCoinList(at.GetValueOrDefault("amount", ""))) net[d] = net.GetValueOrDefault(d) - a;
                if (type == "collect_spread_rewards" && at.ContainsKey("tokens_out")) foreach (var (a, d) in ParseCoinList(at["tokens_out"])) rewards[d] = rewards.GetValueOrDefault(d) + a;
            }
            foreach (var (d, a) in t.Fee) if (net.ContainsKey(d)) net[d] += a;
            foreach (var (d, a) in rewards) net[d] = net.GetValueOrDefault(d) - a;
            var opt = net.Where(kv => IsOpt(kv.Key)).Sum(kv => kv.Value);
            var usd = net.Where(kv => OsmosisUsdDenoms.Contains(kv.Key)).Sum(kv => kv.Value);
            var other = net.Where(kv => !IsOpt(kv.Key) && !OsmosisUsdDenoms.Contains(kv.Key) && Math.Abs(kv.Value) > 0.000001m).ToList();
            var types = string.Join("+", t.Types);

            var optReward = rewards.Where(kv => IsOpt(kv.Key)).Sum(kv => kv.Value);
            if (optReward > 0.000001m)
            {
                var e = Entry(t, t.Hash + ":rewards", "in", "osmosis:liquidity-rewards", optReward);
                entries.Add(e);
                labels[Key(e)] = new { kind = "income", note = "Osmosis liquidity spread rewards paid in OPT (income at the day's value)" };
            }
            otherTokenFees += rewards.Where(kv => OsmosisUsdDenoms.Contains(kv.Key)).Sum(kv => kv.Value);

            if (types.Contains("MsgRecvPacket") && opt > 0.000001m)
            {
                // OPT arriving from Optio: the move from one of the user's wallets, paired with the Optio send.
                var m = MatchOptio("out", opt, t.Time, optioFirst: true);
                if (m is { } mm) { entries.Add(Entry(t, mm.Hash, "in", mm.Wallet, opt)); links.Add(new { optioTxHash = mm.Hash, wallet = mm.Wallet, direction = "out", amountOpt = opt }); }
                else { var e = Entry(t, t.Hash, "in", "osmosis:ibc-unknown", opt); entries.Add(e); notes.Add($"OPT received by IBC on {t.Time:yyyy-MM-dd} ({opt:N2}) did not match a transfer from your saved Optio wallets; classify it."); }
                continue;
            }
            if (types.Contains("MsgTransfer") && opt < -0.000001m) { pendingIbcOut.Add((t, -opt)); continue; }
            if (types.Contains("MsgTimeout") && opt > 0.000001m)
            {
                var p = pendingIbcOut.FirstOrDefault(x => Math.Abs(x.Opt - opt) <= 0.01m);
                if (p.Tx is not null) pendingIbcOut.Remove(p);
                continue;
            }

            // Liquidity positions (create / add / withdraw); spread rewards were taken out above.
            var creates = t.Events.Where(e => e.GetProperty("type").GetString() == "create_position").Select(EventAttrs).ToList();
            var adds = t.Events.Where(e => e.GetProperty("type").GetString() == "add_to_position").Select(EventAttrs).ToList();
            var withdraws = t.Events.Where(e => e.GetProperty("type").GetString() == "withdraw_position").Select(EventAttrs).ToList();
            if (creates.Count > 0 || withdraws.Count > 0)
            {
                // A position's deposits stay "in the pool" until withdrawn. Each withdrawal (partial or full) takes its share
                // of what was deposited, by liquidity; OPT coming back short of that share was sold inside the pool for the
                // USDC above its share, and extra OPT was bought with the USDC below it.
                foreach (var a in adds) if (chainOf.TryGetValue(a["position_id"], out var ch)) chainOf[a["new_position_id"]] = ch;
                foreach (var c in creates.Where(c => !chainOf.ContainsKey(c["position_id"]))) { chainOf[c["position_id"]] = c["position_id"]; chains[c["position_id"]] = (0, 0, 0, 0, t.Time, 0); }
                var external = withdraws.Where(w => !adds.Any(a => a["position_id"] == w["position_id"])).ToList();
                var chainId = creates.Concat(withdraws).Select(x => chainOf.GetValueOrDefault(x["position_id"])).FirstOrDefault(c => c is not null);
                if (chainId is null) { notes.Add($"A liquidity change on {t.Time:yyyy-MM-dd} could not be tied to a position; it is left out."); continue; }
                var st = chains[chainId];
                if (external.Count == 0)
                {
                    // Opening or adding: the OPT and USDC that left the wallet are now deposited.
                    if (opt < 0) st.OptIn += -opt;
                    if (usd < 0) st.UsdIn += -usd;
                    foreach (var c in creates) liquidityOf[c["position_id"]] = Liq(c);
                    chains[chainId] = st;
                    continue;
                }
                var w0 = external[0];
                var before = liquidityOf.GetValueOrDefault(w0["position_id"]);
                var share = before > 0 ? Math.Min(1.0, Math.Abs(Liq(w0)) / before) : 1.0;
                var basisOpt = st.OptIn * (decimal)share; var basisUsd = st.UsdIn * (decimal)share;
                var gotOpt = Math.Max(0, opt); var gotUsd = Math.Max(0, usd);
                var dOpt = gotOpt - basisOpt; var dUsd = gotUsd - basisUsd;
                var left = before + Liq(w0);
                liquidityOf[w0["position_id"]] = left;
                st.OptIn -= basisOpt; st.UsdIn -= basisUsd;
                var full = Math.Abs(left) <= Math.Max(1e-6, Math.Abs(Liq(w0)) * 1e-9);
                var what = $"Osmosis liquidity position {chainId} (opened {st.Opened:yyyy-MM-dd}), {(full ? "withdrawn" : $"{share:P0} withdrawn")}";
                if (dOpt < -0.000001m)
                {
                    var e = Entry(t, t.Hash + ":lp", "out", "osmosis:liquidity", -dOpt);
                    entries.Add(e);
                    labels[Key(e)] = new { kind = "exchange-sale", usd = Math.Round(Math.Max(0, dUsd), 2), note = $"Sold inside {what}: {(-dOpt):N2} OPT for {dUsd:N2} USDC" };
                }
                else if (dOpt > 0.000001m)
                {
                    var e = Entry(t, t.Hash + ":lp", "in", "osmosis:liquidity", dOpt);
                    entries.Add(e);
                    labels[Key(e)] = new { kind = "purchase", usd = Math.Round(Math.Max(0, -dUsd), 2), note = $"Bought inside {what}: {dOpt:N2} OPT for {(-dUsd):N2} USDC" };
                }
                if (full) { chainOf.Remove(w0["position_id"]); chains.Remove(chainId); }
                else chains[chainId] = st;
                continue;
            }
            if (Math.Abs(opt) < 0.000001m) continue;
            if (t.Types.Any(x => x.Contains("Swap") || x.Contains("SplitRoute")))
            {
                var otherText = string.Join(", ", other.Select(o => $"{Math.Abs(o.Value):N4} {OsmoDenomName(o.Key)}"));
                if (opt < 0)
                {
                    var e = Entry(t, t.Hash, "out", "osmosis:swap", -opt);
                    entries.Add(e);
                    labels[Key(e)] = usd > 0.000001m
                        ? new { kind = "exchange-sale", usd = (decimal?)Math.Round(usd, 2), note = $"Swapped on Osmosis for {usd:N2} USDC" }
                        : new { kind = "exchange-sale", usd = (decimal?)null, note = $"Swapped on Osmosis for {otherText} (valued at the day's OPT price)" };
                }
                else
                {
                    var e = Entry(t, t.Hash, "in", "osmosis:swap", opt);
                    entries.Add(e);
                    labels[Key(e)] = usd < -0.000001m
                        ? new { kind = "purchase", usd = (decimal?)Math.Round(-usd, 2), note = $"Bought on Osmosis for {(-usd):N2} USDC" }
                        : new { kind = "purchase", usd = (decimal?)null, note = $"Bought on Osmosis with {otherText} (cost at the day's OPT price)" };
                }
                continue;
            }
            notes.Add($"OPT moved in a {types} transaction on {t.Time:yyyy-MM-dd} ({opt:N2} OPT); it is left out.");
        }
        // IBC sends out of Osmosis that were not refunded: moves to the user's Optio wallet (paired with the receipt there).
        foreach (var (t, optOut) in pendingIbcOut)
        {
            var m = MatchOptio("in", optOut, t.Time, optioFirst: false);
            if (m is { } mm) { entries.Add(Entry(t, mm.Hash, "out", mm.Wallet, optOut)); links.Add(new { optioTxHash = mm.Hash, wallet = mm.Wallet, direction = "in", amountOpt = optOut }); }
            else { var e = Entry(t, t.Hash, "out", "osmosis:ibc-unknown", optOut); entries.Add(e); notes.Add($"OPT sent by IBC on {t.Time:yyyy-MM-dd} ({optOut:N2}) did not match a receipt in your saved Optio wallets; classify it."); }
        }
        if (chains.Count > 0) notes.Add($"{chains.Count} liquidity position(s) are still open; what the pool sold or bought inside them is counted when they are withdrawn.");
        if (otherTokenFees > 0) notes.Add($"Liquidity spread rewards paid in USDC ({otherTokenFees:N2}) are income too but are not OPT, so they are not in this ledger.");

        entries.Sort((a, b) => a.TimeUtc.CompareTo(b.TimeUtc));
        var prices = await TokenPriceService.GetPricesAtAsync(entries.Select(e => e.TimeUtc).Distinct().ToList(), ct);
        foreach (var e in entries) if (prices.TryGetValue(e.TimeUtc, out var p) && p is not null) { e.PriceUsd = p.PriceUsd; e.PriceInterval = p.Interval; }
        return new { address, chain = "osmosis-1", entries, labels, links, notes, truncated = false, source = OsmosisArchiveLcd, transactions = txs.Count };
    }

    static string OsmoDenomName(string d) => d == "uosmo" ? "OSMO" : d.Contains("/alloyed/") ? d[(d.LastIndexOf('/') + 1)..] : d.StartsWith("ibc/") ? d[..10] + "…" : d;
}

// SOVR cost basis, sales and holdings across the user's Sovren wallets (sovr1...) and Osmosis accounts (SOVR on Osmosis
// is ibc/FB26E0…, transfer/channel-110679/usovr), in the same shape as the Ethereum-side and Solana lots so the Tax Report,
// Sales & Lots and Needs Your Input show it with the rest:
//   - Swaps on Osmosis into SOVR cost what was given: USDC at face value, OPT at the same value the OPT Tax Report uses as
//     that swap's sale price (OPTIC's OPT price at that moment), anything else at SOVR's DefiLlama price that day. Swaps out of
//     SOVR are sales on the same terms. SOVR that only passes through a route (OPT → SOVR → USDC) nets to zero and is ignored.
//   - IBC transfers between your Osmosis account and your Sovren wallet, and sends between your own Sovren wallets, are
//     moves: the lots carry over. They are paired on the Sovren side from the packet's sender, receiver and amount.
//   - Staking rewards withdrawn on Sovren are income at the day's value (and become lots at that cost); delegating and
//     undelegating keep the SOVR in the wallet's lots. Network fees paid in SOVR are small disposals at the day's value.
//   - Anything else is not classified and never guessed; answers from Needs Your Input (by transaction hash) apply.
// Sovren history comes from its REST API and is cached in odata/sovr/<address>.json (new blocks only after that).
using OPTIC.Services;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

public static partial class WebDashboard
{
    const string OsmosisSovrDenom = "ibc/FB26E04E71EACCCCCD6D81F0F7666D5567C3A1686E2AC577FCC03F28F5B5874F";
    static readonly SemaphoreSlim SovrHistoryGate = new(1, 1);

    static string SovrCachePath(string address) => Path.Combine(Environment.CurrentDirectory, "odata", "sovr", address + ".json");

    // Every successful transaction the address signed or received SOVR in (cached; brought up to date when over 10 minutes old,
    // and the saved copy is used if Sovren can't be reached).
    static async Task<JsonArray> LoadSovrHistoryAsync(string address, bool refresh, CancellationToken ct)
    {
        await SovrHistoryGate.WaitAsync(ct);
        try
        {
            var path = SovrCachePath(address);
            var cache = File.Exists(path) ? JsonNode.Parse(await File.ReadAllTextAsync(path, ct))!.AsObject() : new JsonObject { ["scannedToHeight"] = 0L, ["txs"] = new JsonArray() };
            DateTime? updated = cache["updatedUtc"] is { } u && DateTime.TryParse(u.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var ut) ? ut : null;
            if (!refresh && updated is { } up && DateTime.UtcNow - up < TimeSpan.FromMinutes(10)) return cache["txs"]!.AsArray();
            try
            {
                var scanned = cache["scannedToHeight"]!.GetValue<long>();
                var txs = cache["txs"]!.AsArray();
                var seen = txs.Select(t => t!["txhash"]!.ToString()).ToHashSet(StringComparer.Ordinal);
                var latest = await SovrGetAsync("/cosmos/base/tendermint/v1beta1/blocks/latest", ct);
                var maxHeight = Math.Max(scanned, long.Parse(latest!["block"]!["header"]!["height"]!.ToString(), CultureInfo.InvariantCulture));
                foreach (var q in new[] { $"message.sender='{address}'", $"transfer.recipient='{address}'" })
                {
                    var query = scanned > 0 ? $"{q} AND tx.height>{scanned}" : q;
                    for (var page = 1; page <= 50; page++)
                    {
                        var doc = await SovrGetAsync($"/cosmos/tx/v1beta1/txs?query={Uri.EscapeDataString(query)}&page={page}&limit=100&order_by=ORDER_BY_ASC", ct);
                        var list = (doc?["tx_responses"] as JsonArray ?? new()).OfType<JsonObject>().ToList();
                        foreach (var r in list)
                        {
                            var hash = r["txhash"]!.ToString();
                            if (!seen.Add(hash) || r["code"]?.GetValue<int>() != 0) continue;
                            var body = r["tx"]?["body"];
                            txs.Add(new JsonObject
                            {
                                ["txhash"] = hash,
                                ["height"] = long.Parse(r["height"]!.ToString(), CultureInfo.InvariantCulture),
                                ["timestamp"] = r["timestamp"]?.ToString(),
                                // Messages without IBC client headers (large and not needed).
                                ["messages"] = new JsonArray((body?["messages"] as JsonArray ?? new()).OfType<JsonObject>()
                                    .Where(m => m["@type"]?.ToString() is { } ty && !ty.EndsWith("MsgUpdateClient", StringComparison.Ordinal))
                                    .Select(m => { var c = (JsonObject)m.DeepClone(); c.Remove("packet"); c.Remove("proof_commitment"); return (JsonNode)c; }).ToArray()),
                                ["types"] = new JsonArray((body?["messages"] as JsonArray ?? new()).Select(m => (JsonNode)JsonValue.Create(m?["@type"]?.ToString()?.Split('.').Last() ?? "")!).ToArray()),
                                ["fee"] = r["tx"]?["auth_info"]?["fee"]?["amount"]?.DeepClone(),
                                ["events"] = new JsonArray((r["events"] as JsonArray ?? new()).OfType<JsonObject>()
                                    .Where(e => e["type"]?.ToString() is "coin_spent" or "coin_received" or "withdraw_rewards" or "fungible_token_packet" or "delegate")
                                    .Select(e => e.DeepClone()).ToArray()),
                            });
                        }
                        var total = long.TryParse(doc?["total"]?.ToString(), out var tv) ? tv : long.TryParse(doc?["pagination"]?["total"]?.ToString(), out var pv) ? pv : 0;
                        if (list.Count == 0 || page * 100 >= total) break;
                    }
                }
                cache["scannedToHeight"] = maxHeight;
                cache["updatedUtc"] = DateTime.UtcNow;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, cache.ToJsonString(), ct);
            }
            catch (HttpRequestException ex) when (updated is not null) { Console.WriteLine($"SOVR history for {address}: Sovren could not be read, using the saved copy ({ex.Message})"); }
            return cache["txs"]!.AsArray();
        }
        finally { SovrHistoryGate.Release(); }
    }

    // Net change of one address in a coin denom from a transaction's coin_received / coin_spent events (6 decimals).
    static decimal CosmosNet(IEnumerable<JsonNode?> events, string address, string denom)
    {
        var net = 0m;
        foreach (var e in events.OfType<JsonObject>())
        {
            var at = (e["attributes"] as JsonArray ?? new()).OfType<JsonObject>().GroupBy(a => a["key"]?.ToString() ?? "").ToDictionary(g => g.Key, g => g.First()["value"]?.ToString() ?? "");
            var sign = e["type"]?.ToString() == "coin_received" && at.GetValueOrDefault("receiver") == address ? 1 : e["type"]?.ToString() == "coin_spent" && at.GetValueOrDefault("spender") == address ? -1 : 0;
            if (sign != 0) foreach (var (a, d) in ParseCoinList(at.GetValueOrDefault("amount", ""))) if (d == denom) net += sign * a;
        }
        return net;
    }

    static Dictionary<string, string> NodeAttrs(JsonObject e) =>
        (e["attributes"] as JsonArray ?? new()).OfType<JsonObject>().GroupBy(a => a["key"]?.ToString() ?? "").ToDictionary(g => g.Key, g => g.First()["value"]?.ToString() ?? "");

    // One SOVR movement to place in the lots, from either chain.
    sealed class SovrStep
    {
        public string Chain = "", Wallet = "", Hash = "", Kind = ""; // Kind: swap, ibc-in, ibc-out, send-in, send-out, other
        public DateTime Time;
        public decimal Qty, Fee, Rewards; // Qty signed (+ in / − out), without fee and rewards
        public decimal? Usd; // what was paid or received in USD terms, when the transaction itself says
        public string Paid = "", Counterparty = "";
    }

    static async Task<JsonObject> BuildSovrLotsAsync(List<(string Address, string Label)> sovrWallets, List<(string Address, string Label)> osmoWallets, JsonObject labels, bool refresh, CancellationToken ct)
    {
        var sovPx = await LoadDailyPricesAsync(SovrPriceKey, refresh, ct);
        decimal? Px(DateTime t) { for (var i = 0; i < 4; i++) if (sovPx.TryGetValue(t.AddDays(-i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), out var p)) return p; return null; }
        decimal? SovUsd(decimal q, DateTime t) => Px(t) is { } p ? Math.Round(q * p, 2) : null;
        var ownSovr = sovrWallets.Select(w => w.Address).ToHashSet(StringComparer.Ordinal);
        var ownOsmo = osmoWallets.Select(w => w.Address).ToHashSet(StringComparer.Ordinal);
        var steps = new List<SovrStep>();
        var notes = new JsonArray();

        // Sovren side.
        var sovrTxs = new List<(string Wallet, JsonObject Tx)>();
        foreach (var w in sovrWallets)
            foreach (var t in (await LoadSovrHistoryAsync(w.Address, refresh, ct)).OfType<JsonObject>()) sovrTxs.Add((w.Address, t));
        foreach (var (w, t) in sovrTxs)
        {
            var events = (t["events"] as JsonArray ?? new()).ToList();
            var msgs = (t["messages"] as JsonArray ?? new()).OfType<JsonObject>().ToList();
            var time = DateTime.Parse(t["timestamp"]!.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
            var signed = msgs.Any(m => new[] { "from_address", "sender", "delegator_address", "signer" }.Any(k => m[k]?.ToString() == w));
            var fee = signed ? SovrCoins(t["fee"]) : 0m;
            var rewards = events.OfType<JsonObject>().Where(e => e["type"]?.ToString() == "withdraw_rewards").Select(NodeAttrs)
                .Where(a => a.GetValueOrDefault("delegator", w) == w).Sum(a => ParseCoinList(a.GetValueOrDefault("amount", "")).Where(c => c.Denom == "usovr").Sum(c => c.Amount));
            var delegated = msgs.Where(m => m["@type"]?.ToString()?.EndsWith("MsgDelegate", StringComparison.Ordinal) == true && m["delegator_address"]?.ToString() == w).Sum(m => SovrAmount(m["amount"]?["amount"]));
            var principal = CosmosNet(events, w, "usovr") + fee - rewards + delegated;
            var step = new SovrStep { Chain = "sovr", Wallet = w, Hash = t["txhash"]!.ToString(), Time = time, Qty = Math.Abs(principal) < 0.000001m ? 0 : principal, Fee = fee, Rewards = rewards, Kind = "other" };
            var packet = events.OfType<JsonObject>().Where(e => e["type"]?.ToString() == "fungible_token_packet").Select(NodeAttrs).FirstOrDefault(a => a.ContainsKey("sender") && a.ContainsKey("receiver"));
            var transfer = msgs.FirstOrDefault(m => m["@type"]?.ToString()?.EndsWith("MsgTransfer", StringComparison.Ordinal) == true);
            var send = msgs.FirstOrDefault(m => m["@type"]?.ToString()?.EndsWith("MsgSend", StringComparison.Ordinal) == true);
            if (step.Qty > 0 && packet is not null && packet["receiver"] == w) { step.Kind = "ibc-in"; step.Counterparty = packet["sender"]; }
            else if (step.Qty < 0 && transfer is not null) { step.Kind = "ibc-out"; step.Counterparty = transfer["receiver"]?.ToString() ?? ""; }
            else if (send is not null) { step.Kind = step.Qty > 0 ? "send-in" : "send-out"; step.Counterparty = (step.Qty > 0 ? send["from_address"] : send["to_address"])?.ToString() ?? ""; }
            steps.Add(step);
        }

        // Osmosis side: only the SOVR legs.
        foreach (var o in osmoWallets)
        {
            List<OsmoTx> txs;
            await OsmosisWalletGate.WaitAsync(ct);
            try { txs = await LoadOsmosisTxsAsync(o.Address, refresh, ct); }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException) { notes.Add("Could not read the Osmosis history for SOVR: " + ex.Message); continue; }
            finally { OsmosisWalletGate.Release(); }
            foreach (var t in txs)
            {
                var ev = t.Events.Select(e => JsonNode.Parse(e.GetRawText())).ToList();
                var q = CosmosNet(ev, o.Address, OsmosisSovrDenom);
                if (Math.Abs(q) < 0.000001m) continue;
                var step = new SovrStep { Chain = "osmosis", Wallet = o.Address, Hash = t.Hash, Time = t.Time, Qty = q, Kind = "other" };
                if (t.Types.Any(x => x.Contains("Swap") || x.Contains("SplitRoute")))
                {
                    step.Kind = "swap";
                    var usd = OsmosisUsdDenoms.Sum(d => CosmosNet(ev, o.Address, d));
                    var opt = ev.OfType<JsonObject>().Any() ? CosmosNetPrefix(ev, o.Address, OsmosisOptDenomPrefix) : 0m;
                    if (Math.Abs(usd) > 0.000001m) { step.Usd = Math.Round(Math.Abs(usd), 2); step.Paid = $"{Math.Abs(usd):N2} USDC"; }
                    else if (Math.Abs(opt) > 0.000001m) { step.Paid = $"{Math.Abs(opt):N2} OPT"; step.Counterparty = "opt:" + Math.Abs(opt).ToString(CultureInfo.InvariantCulture); }
                }
                else if (t.Types.Contains("MsgTransfer") && q < 0) step.Kind = "ibc-out";
                else if (t.Types.Contains("MsgRecvPacket") && q > 0) step.Kind = "ibc-in";
                steps.Add(step);
            }
        }

        // OPT given or received in SOVR swaps: valued as the OPT Tax Report values that swap (OPTIC's OPT price at that moment).
        var optTimes = steps.Where(s => s.Counterparty.StartsWith("opt:", StringComparison.Ordinal)).Select(s => s.Time).Distinct().ToList();
        var optPx = optTimes.Count > 0 ? await TokenPriceService.GetPricesAtAsync(optTimes, ct) : new Dictionary<DateTime, TokenPricePoint?>();
        foreach (var s in steps.Where(s => s.Counterparty.StartsWith("opt:", StringComparison.Ordinal)))
        {
            var optQty = decimal.Parse(s.Counterparty[4..], CultureInfo.InvariantCulture);
            if (optPx.TryGetValue(s.Time, out var p) && p is not null) s.Usd = Math.Round(optQty * p.PriceUsd, 2);
            s.Counterparty = "osmosis:swap";
        }

        // IBC pairs: a Sovren receipt from your Osmosis account (or a Sovren send to it) is the move; the matching Osmosis leg
        // is then skipped. Matched by account and amount within a day.
        var skip = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in steps.Where(s => s.Chain == "sovr" && s.Kind is "ibc-in" or "ibc-out" && ownOsmo.Contains(s.Counterparty)))
        {
            var mate = steps.Where(m => m.Chain == "osmosis" && m.Wallet == s.Counterparty && m.Kind == (s.Kind == "ibc-in" ? "ibc-out" : "ibc-in") && !skip.Contains(m.Hash)
                    && Math.Abs(Math.Abs(m.Qty) - Math.Abs(s.Qty)) <= 0.000001m && Math.Abs((m.Time - s.Time).TotalHours) <= 24)
                .OrderBy(m => Math.Abs((m.Time - s.Time).TotalSeconds)).FirstOrDefault();
            if (mate is not null) skip.Add(mate.Hash);
            s.Kind = s.Kind == "ibc-in" ? "move-in" : "move-out";
        }
        // Sends between your own Sovren wallets: the receiving side is settled by the send.
        foreach (var s in steps.Where(s => s.Chain == "sovr" && s.Kind == "send-out" && ownSovr.Contains(s.Counterparty)))
        {
            s.Kind = "own-send";
            foreach (var m in steps.Where(m => m.Chain == "sovr" && m.Hash == s.Hash && m.Wallet == s.Counterparty)) skip.Add(m.Hash + "|" + m.Wallet);
        }

        // Lots per wallet (Osmosis accounts and Sovren wallets), as for Solana.
        var lots = sovrWallets.Concat(osmoWallets).Select(w => w.Address).Distinct().ToDictionary(a => a, _ => new List<TitnLot>());
        var disposals = new JsonArray(); var unclassified = new JsonArray();
        var seq = 0;
        var income = new SortedDictionary<int, (decimal Qty, decimal Usd, int Count)>();
        var yearEnd = new SortedDictionary<int, JsonArray>();
        string ChainOf(string wallet) => ownOsmo.Contains(wallet) ? "osmosis" : "sovr";
        void Acquire(string wallet, decimal qty, decimal? cost, DateTime time, string how)
        {
            if (qty > 0m) lots[wallet].Add(new TitnLot { Id = $"{wallet[..7]}#v{++seq}", Asset = "SOVR", Qty = qty, UnitCost = cost is { } c ? c / qty : null, Acquired = time, How = how });
        }
        List<(TitnLot Lot, decimal Qty)> Take(string wallet, decimal qty)
        {
            var taken = new List<(TitnLot, decimal)>();
            foreach (var lot in lots[wallet].Where(l => l.Qty > 0).OrderByDescending(l => l.UnitCost.HasValue).ThenByDescending(l => l.UnitCost ?? 0).ThenBy(l => l.Acquired).ToList())
            {
                if (qty <= 0.000000001m) break;
                var q = Math.Min(lot.Qty, qty);
                lot.Qty -= q; qty -= q;
                taken.Add((lot, q));
            }
            if (qty > 0.000001m) taken.Add((new TitnLot { Id = "missing", Asset = "SOVR", Qty = qty, Acquired = DateTime.MinValue, How = "No recorded acquisition" }, qty));
            return taken;
        }
        void Dispose(string wallet, decimal qty, decimal? proceeds, DateTime time, string tx, string how)
        {
            foreach (var (lot, q) in Take(wallet, qty))
            {
                if (q < 0.000000001m) continue;
                var share = qty > 0 ? q / qty : 0;
                decimal? cost = lot.UnitCost is { } u ? Math.Round(u * q, 2) : null;
                decimal? pr = proceeds is { } p ? Math.Round(p * share, 2) : null;
                disposals.Add(new JsonObject
                {
                    ["wallet"] = wallet, ["chain"] = ChainOf(wallet), ["asset"] = "SOVR", ["qty"] = q, ["acquired"] = lot.Acquired == DateTime.MinValue ? null : (DateTime?)lot.Acquired, ["sold"] = time,
                    ["proceeds"] = pr, ["cost"] = cost, ["gain"] = pr is { } a && cost is { } b ? (decimal?)(a - b) : null,
                    ["term"] = lot.Acquired == DateTime.MinValue ? "unknown" : (time - lot.Acquired).TotalDays > 365 ? "long" : "short",
                    ["how"] = how, ["lot"] = lot.Id, ["lotHow"] = lot.How, ["tx"] = tx,
                });
            }
        }
        void Move(string from, string to, decimal qty, string what)
        {
            foreach (var (lot, q) in Take(from, qty))
                lots[to].Add(new TitnLot { Id = lot.Id, Asset = "SOVR", Qty = q, UnitCost = lot.UnitCost, Acquired = lot.Acquired, How = lot.How + $" ({what})" });
        }
        bool Answered(SovrStep s, decimal qty, bool inbound, decimal? value)
        {
            if (labels[s.Hash] is not JsonObject label) return false;
            var kind = label["kind"]?.ToString() ?? "";
            decimal? usd = label["usd"] is JsonValue uv && uv.TryGetValue<decimal>(out var ud) ? ud : null;
            var note = label["note"]?.ToString() ?? "";
            string How(string f) => note == "" ? f : f + ": " + note;
            if (inbound)
            {
                if (kind == "purchase") Acquire(s.Wallet, qty, usd, s.Time, How("Bought (your answer)"));
                else if (kind == "income") Acquire(s.Wallet, qty, usd ?? value, s.Time, How("Income received (your answer)"));
                else if (kind == "gift-in") Acquire(s.Wallet, qty, usd, s.Time, How("Gift received; cost is the giver's basis (your answer)"));
                else if (kind == "own") Acquire(s.Wallet, qty, usd, s.Time, How("From your own wallet or account (your answer)"));
                else if (kind != "ignore") return false;
                if (kind is "purchase" or "gift-in" && usd is null) notes.Add($"Cost not entered yet for {qty:N4} SOVR received {s.Time:yyyy-MM-dd}.");
            }
            else
            {
                if (kind == "sale") Dispose(s.Wallet, qty, usd ?? value, s.Time, s.Hash, How("Sale (your answer)"));
                else if (kind is "gift-out" or "own") Take(s.Wallet, qty);
                else if (kind != "ignore") return false;
            }
            return true;
        }
        JsonArray Snapshot() => new(lots.Values.SelectMany(l => l).Where(l => l.Qty > 0.000000001m).GroupBy(l => l.Asset).Select(g => (JsonNode)new JsonObject
        {
            ["asset"] = g.Key, ["qty"] = g.Sum(l => l.Qty), ["cost"] = Math.Round(g.Where(l => l.UnitCost.HasValue).Sum(l => l.UnitCost!.Value * l.Qty), 2), ["unknownCostQty"] = g.Where(l => !l.UnitCost.HasValue).Sum(l => l.Qty),
        }).ToArray());

        var snapYear = steps.Count > 0 ? steps.Min(s => s.Time).Year : DateTime.UtcNow.Year;
        // In time order; within one transaction the sending side first.
        foreach (var s in steps.OrderBy(s => s.Time).ThenBy(s => s.Hash).ThenBy(s => s.Qty < 0 ? 0 : 1))
        {
            while (s.Time.Year > snapYear) { yearEnd[snapYear] = Snapshot(); snapYear++; }
            if (s.Rewards > 0.000001m)
            {
                var v = SovUsd(s.Rewards, s.Time);
                Acquire(s.Wallet, s.Rewards, v, s.Time, "Staking rewards withdrawn (income at the day's value)");
                var y = income.GetValueOrDefault(s.Time.Year);
                income[s.Time.Year] = (y.Qty + s.Rewards, y.Usd + (v ?? 0), y.Count + 1);
            }
            if (s.Fee > 0) Dispose(s.Wallet, s.Fee, SovUsd(s.Fee, s.Time), s.Time, s.Hash, "SOVR spent on the network fee");
            if (s.Qty == 0 || skip.Contains(s.Hash) || skip.Contains(s.Hash + "|" + s.Wallet)) continue;
            var q = Math.Abs(s.Qty); var inbound = s.Qty > 0;
            switch (s.Kind)
            {
                case "swap" when inbound:
                    Acquire(s.Wallet, q, s.Usd ?? SovUsd(q, s.Time), s.Time, $"Bought on Osmosis with {s.Paid}" + (s.Usd is null ? " (cost at SOVR's price that day)" : s.Paid.EndsWith("OPT") ? " (cost: that OPT's value, as in the OPT Tax Report)" : ""));
                    continue;
                case "swap":
                    Dispose(s.Wallet, q, s.Usd ?? SovUsd(q, s.Time), s.Time, s.Hash, $"Swapped on Osmosis for {s.Paid}" + (s.Usd is null ? " (valued at SOVR's price that day)" : ""));
                    continue;
                case "move-in":
                    Move(s.Counterparty, s.Wallet, q, "moved by IBC from your Osmosis account");
                    continue;
                case "move-out":
                    Move(s.Wallet, s.Counterparty, q, "moved by IBC from your Sovren wallet");
                    continue;
                case "own-send":
                    Move(s.Wallet, s.Counterparty, q, $"moved from {s.Wallet[..10]}…");
                    continue;
            }
            var value = SovUsd(q, s.Time);
            if (Answered(s, q, inbound, value)) continue;
            if (inbound) Acquire(s.Wallet, q, null, s.Time, $"Received{(s.Counterparty != "" ? " from " + s.Counterparty : "")} (not classified, cost unknown)");
            unclassified.Add(new JsonObject { ["wallet"] = s.Wallet, ["chain"] = s.Chain, ["time"] = s.Time, ["tx"] = s.Hash, ["asset"] = "SOVR", ["qty"] = q, ["direction"] = inbound ? "in" : "out", ["counterparty"] = s.Counterparty, ["valueThatDay"] = value });
        }
        while (snapYear <= DateTime.UtcNow.Year) { yearEnd[snapYear] = Snapshot(); snapYear++; }
        foreach (var (y, v) in income) notes.Add($"SOVR staking rewards in {y}: {v.Qty:N4} SOVR worth ${v.Usd:N2} at the day's value ({v.Count} withdrawals). That is ordinary income; it is in your SOVR cost basis, not yet in the income totals.");

        var holdings = new JsonArray(lots.SelectMany(kv => kv.Value.Where(l => l.Qty > 0.000000001m).Select(l => (JsonNode)new JsonObject
        {
            ["wallet"] = kv.Key, ["asset"] = l.Asset, ["qty"] = l.Qty, ["unitCost"] = l.UnitCost, ["cost"] = l.UnitCost is { } u ? (decimal?)Math.Round(u * l.Qty, 2) : null, ["acquired"] = l.Acquired, ["how"] = l.How, ["id"] = l.Id,
        })).ToArray());
        var ye = new JsonObject();
        foreach (var (y, arr) in yearEnd) ye[y.ToString(CultureInfo.InvariantCulture)] = arr;
        return new JsonObject { ["disposals"] = disposals, ["holdings"] = holdings, ["unclassified"] = unclassified, ["notes"] = notes, ["yearEnd"] = ye, ["price"] = new JsonObject { ["SOVR"] = sovPx.Count > 0 ? (decimal?)sovPx.Last().Value : null } };
    }

    // Net change in every denom that starts with a prefix (OPT on Osmosis is ibc/6AA47B…).
    static decimal CosmosNetPrefix(IEnumerable<JsonNode?> events, string address, string prefix)
    {
        var net = 0m;
        foreach (var e in events.OfType<JsonObject>())
        {
            var at = NodeAttrs(e);
            var sign = e["type"]?.ToString() == "coin_received" && at.GetValueOrDefault("receiver") == address ? 1 : e["type"]?.ToString() == "coin_spent" && at.GetValueOrDefault("spender") == address ? -1 : 0;
            if (sign != 0) foreach (var (a, d) in ParseCoinList(at.GetValueOrDefault("amount", ""))) if (d.StartsWith(prefix, StringComparison.Ordinal)) net += sign * a;
        }
        return net;
    }
}

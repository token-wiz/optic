// Solana history and cost basis for the user's saved Solana wallets, in the same shape as the Ethereum-side lots
// (disposals, holdings, unclassified, yearEnd) so the Tax Report, Needs Your Input and Portfolio read both alike.
//   - Each transaction is reduced to this wallet's change in SOL and in each SPL token, the fee it paid, and the other
//     accounts whose balances moved the other way. Transactions that change nothing for the wallet (spam memos) are skipped,
//     and SOL dust of 0.0001 or less arriving with nothing else is ignored (address-poisoning spam).
//   - The fee is SOL spent at its value that day. Moves between your own Solana wallets keep their lots.
//   - A swap (SOL for a token or back) is a disposal of what was given up at the value of the SOL side; what came back
//     costs the same amount.
//   - Plain transfers to or from other addresses are listed as not classified and never guessed; a receipt is kept at
//     unknown cost until answered. Answers (titn-labels.json, keyed by transaction signature) work as on Ethereum.
using OPTIC.Services;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

public static partial class WebDashboard
{
    // Token names: a few fixed (the fake GNK), the rest from DexScreener, cached.
    static readonly Dictionary<string, string> SolanaTokenNames = new()
    {
        ["EPjFWdd5AufqSSqeM2qN1xzybapC8G4wEGGkZwyTDt1v"] = "USDC",
        ["Es9vMFrzaCERmJfrF4H2FYD4KCoNkY11McCe8BenwNYB"] = "USDT",
        // Sold as "GNK" on Solana; the real GNK is the Gonka chain's coin (gonka1... addresses).
        ["AE36ntk1pza8rzKTsQ8QAmJHqL8adkBvzgzXvEM4mTFU"] = "GNK (fake, Solana)",
    };

    sealed record SolTx(string Sig, DateTime Time, bool Failed, bool Payer, decimal Fee, decimal Sol, Dictionary<string, decimal> Tokens, List<string> SolFrom, List<string> SolTo, Dictionary<string, List<string>> TokenCps);

    static async Task<JsonElement?> SolanaRpcRetryAsync(string method, JsonArray prms, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            try { return await SolanaRpcAsync(method, prms, ct); }
            catch (HttpRequestException) { await Task.Delay(2000 * (attempt + 1), ct); } // rate limited: back off
        }
        return null;
    }

    // Every transaction touching the address, reduced to what changed for it. Cached in odata/solana: the full signature
    // list and the transactions read so far. Refreshed when more than 3 hours old or when any transaction is still missing
    // (public endpoints rate-limit, so a run can stop short; the next one picks up the rest).
    static async Task<List<SolTx>> LoadSolanaHistoryAsync(string address, bool refresh, CancellationToken ct)
    {
        var dir = Path.Combine(Environment.CurrentDirectory, "odata", "solana");
        var path = Path.Combine(dir, $"{address}.json");
        var sigPath = Path.Combine(dir, $"{address}-sigs.json");
        var cached = new Dictionary<string, SolTx>();
        if (File.Exists(path))
            foreach (var t in JsonSerializer.Deserialize<List<SolTx>>(await File.ReadAllTextAsync(path, ct)) ?? new()) cached[t.Sig] = t;
        var sigs = File.Exists(sigPath) ? JsonSerializer.Deserialize<List<string>>(await File.ReadAllTextAsync(sigPath, ct)) ?? new() : new List<string>();
        List<SolTx> Result() => cached.Values.OrderBy(t => t.Time).ToList();
        var complete = sigs.Count > 0 && sigs.All(cached.ContainsKey);
        if (!refresh && complete && DateTime.UtcNow - File.GetLastWriteTimeUtc(sigPath) < TimeSpan.FromHours(3)) return Result();

        // The full signature list (newest first, 1,000 per page).
        var all = new List<string>();
        string? before = null;
        for (var page = 0; page < 50; page++)
        {
            var opts = new JsonObject { ["limit"] = 1000 };
            if (before is not null) opts["before"] = before;
            var res = await SolanaRpcRetryAsync("getSignaturesForAddress", new JsonArray(address, opts), ct);
            if (res is null) { if (cached.Count > 0) return Result(); throw new HttpRequestException("Solana: could not read the transaction list."); }
            var items = res.Value.EnumerateArray().Select(x => x.GetProperty("signature").GetString()!).ToList();
            all.AddRange(items);
            if (items.Count < 1000) break;
            before = items[^1];
        }
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(sigPath, JsonSerializer.Serialize(all), ct);
        var missing = 0;
        foreach (var sig in all.Where(x => !cached.ContainsKey(x)))
        {
            var tx = await SolanaRpcRetryAsync("getTransaction", new JsonArray(sig, new JsonObject { ["encoding"] = "jsonParsed", ["maxSupportedTransactionVersion"] = 0 }), ct);
            if (tx is not { ValueKind: JsonValueKind.Object } t) { missing++; continue; }
            cached[sig] = ParseSolTx(sig, t, address);
            await Task.Delay(400, ct); // public endpoints rate-limit
            if (cached.Count % 20 == 0) await File.WriteAllTextAsync(path, JsonSerializer.Serialize(Result()), ct);
        }
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(Result()), ct);
        if (missing > 0) throw new HttpRequestException($"Solana: {missing} of {all.Count} transactions for {address[..6]}… could not be read yet (rate limited); reload in a minute.");
        return Result();
    }

    static SolTx ParseSolTx(string sig, JsonElement t, string address)
    {
        var meta = t.GetProperty("meta");
        var keys = t.GetProperty("transaction").GetProperty("message").GetProperty("accountKeys").EnumerateArray()
            .Select(k => k.ValueKind == JsonValueKind.String ? k.GetString()! : k.GetProperty("pubkey").GetString()!).ToList();
        var pre = meta.GetProperty("preBalances").EnumerateArray().Select(x => x.GetInt64()).ToList();
        var post = meta.GetProperty("postBalances").EnumerateArray().Select(x => x.GetInt64()).ToList();
        var i = keys.IndexOf(address);
        var payer = keys.Count > 0 && keys[0] == address;
        var fee = payer ? meta.GetProperty("fee").GetInt64() / 1_000_000_000m : 0m;
        var delta = i >= 0 ? (post[i] - pre[i]) / 1_000_000_000m : 0m;
        var sol = delta + fee; // the change apart from the fee
        var from = new List<string>(); var to = new List<string>();
        for (var j = 0; j < keys.Count; j++)
        {
            if (j == i) continue;
            var d = post[j] - pre[j];
            if (sol > 0 && d < 0) from.Add(keys[j]);
            if (sol < 0 && d > 0) to.Add(keys[j]);
        }
        // SPL tokens: this wallet's change per mint, and the owners whose balance of that mint moved the other way.
        var bal = new Dictionary<(string Owner, string Mint), decimal>();
        void Add(string side, int sign)
        {
            if (!meta.TryGetProperty(side, out var arr) || arr.ValueKind != JsonValueKind.Array) return;
            foreach (var b in arr.EnumerateArray())
            {
                var owner = b.TryGetProperty("owner", out var o) ? o.GetString() ?? "" : "";
                var mint = b.GetProperty("mint").GetString()!;
                var amt = decimal.Parse(b.GetProperty("uiTokenAmount").GetProperty("uiAmountString").GetString() ?? "0", NumberStyles.Float, CultureInfo.InvariantCulture);
                bal[(owner, mint)] = (bal.TryGetValue((owner, mint), out var v) ? v : 0m) + sign * amt;
            }
        }
        Add("preTokenBalances", -1);
        Add("postTokenBalances", 1);
        var tokens = bal.Where(kv => kv.Key.Owner == address && kv.Value != 0m).ToDictionary(kv => kv.Key.Mint, kv => kv.Value);
        var tokenCps = tokens.ToDictionary(kv => kv.Key, kv => bal.Where(b => b.Key.Mint == kv.Key && b.Key.Owner != address && Math.Sign(b.Value) == -Math.Sign(kv.Value)).Select(b => b.Key.Owner).Distinct().ToList());
        var time = DateTimeOffset.FromUnixTimeSeconds(t.GetProperty("blockTime").GetInt64()).UtcDateTime;
        var failed = meta.TryGetProperty("err", out var err) && err.ValueKind != JsonValueKind.Null;
        return new SolTx(sig, time, failed, payer, fee, failed ? 0m : sol, failed ? new() : tokens, from, to, tokenCps);
    }

    static async Task<string> SolanaTokenNameAsync(string mint, CancellationToken ct)
    {
        if (SolanaTokenNames.TryGetValue(mint, out var n)) return n;
        var path = Path.Combine(Environment.CurrentDirectory, "odata", "solana", "token-names.json");
        var names = File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(path, ct)) ?? new() : new();
        if (names.TryGetValue(mint, out n)) return SolanaTokenNames[mint] = n;
        try
        {
            using var doc = JsonDocument.Parse(await EvmHttp.GetStringAsync("https://api.dexscreener.com/latest/dex/tokens/" + mint, ct));
            if (doc.RootElement.TryGetProperty("pairs", out var pairs) && pairs.ValueKind == JsonValueKind.Array && pairs.GetArrayLength() > 0)
                n = pairs[0].GetProperty("baseToken").GetProperty("address").GetString() == mint ? pairs[0].GetProperty("baseToken").GetProperty("symbol").GetString() : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException) { }
        n = string.IsNullOrWhiteSpace(n) ? "SPL " + mint[..6] : n + " (Solana)";
        names[mint] = n;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(names), ct);
        return SolanaTokenNames[mint] = n;
    }

    static async Task<JsonObject> BuildSolanaLotsAsync(List<(string Address, string Label)> wallets, JsonObject labels, bool refresh, CancellationToken ct)
    {
        var own = wallets.Select(w => w.Address).ToHashSet();
        var solPx = await LoadDailyPricesAsync(SolanaPriceKey, refresh, ct);
        decimal? Px(DateTime t) { for (var i = 0; i < 4; i++) if (solPx.TryGetValue(t.AddDays(-i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), out var p)) return p; return null; }
        decimal? SolUsd(decimal q, DateTime t) => Px(t) is { } p ? Math.Round(q * p, 2) : null;

        var rows = new List<(string Wallet, SolTx Tx)>();
        foreach (var w in wallets)
            foreach (var t in await LoadSolanaHistoryAsync(w.Address, refresh, ct)) rows.Add((w.Address, t));
        var names = new Dictionary<string, string>();
        foreach (var m in rows.SelectMany(r => r.Tx.Tokens.Keys).Distinct()) names[m] = await SolanaTokenNameAsync(m, ct);
        string Name(string mint) => names.TryGetValue(mint, out var n) ? n : mint;
        decimal? UnitUsd(string asset) => asset is "USDC" or "USDT" ? 1m : null;

        var lots = wallets.ToDictionary(w => w.Address, _ => new List<TitnLot>());
        var disposals = new JsonArray(); var unclassified = new JsonArray(); var notes = new JsonArray();
        var seq = 0; var dust = 0;
        var handled = new HashSet<string>();
        var yearEnd = new SortedDictionary<int, JsonArray>();

        void Acquire(string wallet, string asset, decimal qty, decimal? cost, DateTime time, string how)
        {
            if (qty <= 0m) return;
            lots[wallet].Add(new TitnLot { Id = $"{wallet[..6]}#s{++seq}", Asset = asset, Qty = qty, UnitCost = cost is { } c ? c / qty : null, Acquired = time, How = how });
        }
        List<(TitnLot Lot, decimal Qty)> Take(string wallet, string asset, decimal qty)
        {
            var taken = new List<(TitnLot, decimal)>();
            foreach (var lot in lots[wallet].Where(l => l.Asset == asset && l.Qty > 0).OrderByDescending(l => l.UnitCost.HasValue).ThenByDescending(l => l.UnitCost ?? 0).ThenBy(l => l.Acquired).ToList())
            {
                if (qty <= 0.000000001m) break;
                var q = Math.Min(lot.Qty, qty);
                lot.Qty -= q; qty -= q;
                taken.Add((lot, q));
            }
            if (qty > (asset == "SOL" ? 0.000001m : 0.0001m)) taken.Add((new TitnLot { Id = "missing", Asset = asset, Qty = qty, Acquired = DateTime.MinValue, How = "No recorded acquisition" }, qty));
            return taken;
        }
        void Dispose(string wallet, string asset, decimal qty, decimal? proceeds, DateTime time, string tx, string how)
        {
            foreach (var (lot, q) in Take(wallet, asset, qty))
            {
                if (q < 0.000000001m) continue;
                var share = qty > 0 ? q / qty : 0;
                decimal? cost = lot.UnitCost is { } u ? Math.Round(u * q, 2) : null;
                decimal? pr = proceeds is { } p ? Math.Round(p * share, 2) : null;
                disposals.Add(new JsonObject
                {
                    ["wallet"] = wallet, ["chain"] = "solana", ["asset"] = asset, ["qty"] = q, ["acquired"] = lot.Acquired == DateTime.MinValue ? null : (DateTime?)lot.Acquired, ["sold"] = time,
                    ["proceeds"] = pr, ["cost"] = cost, ["gain"] = pr is { } a && cost is { } b ? (decimal?)(a - b) : null,
                    ["term"] = lot.Acquired == DateTime.MinValue ? "unknown" : (time - lot.Acquired).TotalDays > 365 ? "long" : "short",
                    ["how"] = how, ["lot"] = lot.Id, ["lotHow"] = lot.How, ["tx"] = tx,
                });
            }
        }
        void Move(string from, string to, string asset, decimal qty)
        {
            foreach (var (lot, q) in Take(from, asset, qty))
                lots[to].Add(new TitnLot { Id = lot.Id, Asset = asset, Qty = q, UnitCost = lot.UnitCost, Acquired = lot.Acquired, How = lot.How + $" (moved from {from[..6]}…)" });
        }
        void Unclassified(string wallet, SolTx t, string asset, decimal qty, bool inbound, IEnumerable<string> cps, decimal? value) =>
            unclassified.Add(new JsonObject { ["wallet"] = wallet, ["chain"] = "solana", ["time"] = t.Time, ["tx"] = t.Sig, ["asset"] = asset, ["qty"] = qty, ["direction"] = inbound ? "in" : "out", ["counterparty"] = string.Join(",", cps), ["valueThatDay"] = value });
        // The user's answer for a transaction, applied to one asset (as on Ethereum). False when there is none.
        bool Answered(string wallet, SolTx t, string asset, decimal qty, bool inbound, decimal? value)
        {
            if (labels[t.Sig] is not JsonObject label) return false;
            var kind = label["kind"]?.ToString() ?? "";
            decimal? usd = label["usd"] is JsonValue uv && uv.TryGetValue<decimal>(out var ud) ? ud : null;
            var note = label["note"]?.ToString() ?? "";
            string How(string f) => note == "" ? f : f + ": " + note;
            if (inbound)
            {
                if (kind == "purchase") Acquire(wallet, asset, qty, usd, t.Time, How("Bought (your answer)"));
                else if (kind == "income") Acquire(wallet, asset, qty, usd ?? value, t.Time, How("Income received (your answer)"));
                else if (kind == "gift-in") Acquire(wallet, asset, qty, usd, t.Time, How("Gift received; cost is the giver's basis (your answer)"));
                else if (kind == "own") Acquire(wallet, asset, qty, usd, t.Time, How("From your own wallet or account (your answer)"));
                else if (kind != "ignore") return false;
                if (kind is "purchase" or "gift-in" && usd is null) notes.Add($"Cost not entered yet for {qty:N4} {asset} received {t.Time:yyyy-MM-dd}.");
            }
            else
            {
                if (kind == "sale") Dispose(wallet, asset, qty, usd ?? value, t.Time, t.Sig, How("Sale (your answer)"));
                else if (kind is "gift-out" or "own") Take(wallet, asset, qty);
                else if (kind != "ignore") return false;
            }
            return true;
        }
        JsonArray Snapshot() => new(lots.Values.SelectMany(l => l).Where(l => l.Qty > 0.000000001m).GroupBy(l => l.Asset).Select(g => (JsonNode)new JsonObject
        {
            ["asset"] = g.Key, ["qty"] = g.Sum(l => l.Qty), ["cost"] = Math.Round(g.Where(l => l.UnitCost.HasValue).Sum(l => l.UnitCost!.Value * l.Qty), 2), ["unknownCostQty"] = g.Where(l => !l.UnitCost.HasValue).Sum(l => l.Qty),
        }).ToArray());

        var snapYear = rows.Count > 0 ? rows.Min(r => r.Tx.Time).Year : DateTime.UtcNow.Year;
        // Same transaction: the sending wallet first, so a move between your wallets is settled once.
        foreach (var (wallet, t) in rows.OrderBy(r => r.Tx.Time).ThenBy(r => r.Tx.Sig).ThenBy(r => r.Tx.Sol >= 0 ? 1 : 0))
        {
            while (t.Time.Year > snapYear) { yearEnd[snapYear] = Snapshot(); snapYear++; }
            if (t.Fee > 0) Dispose(wallet, "SOL", t.Fee, SolUsd(t.Fee, t.Time), t.Time, t.Sig, "SOL spent on the network fee");
            if (t.Failed || handled.Contains(wallet + "|" + t.Sig)) continue;
            var sol = t.Sol;
            var toks = t.Tokens.Where(kv => Math.Abs(kv.Value) > 0.000000001m).ToDictionary(kv => Name(kv.Key), kv => (kv.Value, Cps: t.TokenCps[kv.Key]));
            if (Math.Abs(sol) < 0.000000001m && toks.Count == 0) continue; // nothing changed for this wallet (spam)
            if (toks.Count == 0 && sol > 0 && sol <= 0.0001m && !t.Payer) { dust++; continue; } // SOL dust (address poisoning)

            var anyAnswer = labels[t.Sig] is JsonObject;
            // Moves between your own Solana wallets.
            if (toks.Count == 0 && sol < 0 && t.SolTo.FirstOrDefault(own.Contains) is { } dest && !anyAnswer)
            {
                Move(wallet, dest, "SOL", -sol);
                handled.Add(dest + "|" + t.Sig);
                continue;
            }
            if (toks.Count == 0 && sol > 0 && t.SolFrom.Any(own.Contains) && !anyAnswer) { Acquire(wallet, "SOL", sol, SolUsd(sol, t.Time), t.Time, "From your own wallet (not matched to its send)"); continue; }

            // A swap: SOL against one token (in either direction), valued at the SOL side; or a token against USDC / USDT.
            var tokIn = toks.Where(kv => kv.Value.Value > 0).ToList(); var tokOut = toks.Where(kv => kv.Value.Value < 0).ToList();
            var solOut = sol < -0.0025m; var solIn = sol > 0.0001m;
            if (!anyAnswer && ((solOut && tokIn.Count > 0 && tokOut.Count == 0) || (solIn && tokOut.Count > 0 && tokIn.Count == 0) || (tokIn.Count > 0 && tokOut.Count > 0)))
            {
                decimal? value = solOut || solIn ? SolUsd(Math.Abs(sol), t.Time) : null;
                foreach (var (a, (q, _)) in tokIn.Concat(tokOut).Select(kv => (kv.Key, kv.Value)))
                    if (value is null && UnitUsd(a) is { } u) value = Math.Round(Math.Abs(q) * u, 2);
                if (solOut) Dispose(wallet, "SOL", -sol, value, t.Time, t.Sig, $"SOL swapped for {string.Join(" + ", tokIn.Select(k => $"{k.Value.Value:N2} {k.Key}"))}");
                foreach (var k in tokOut) Dispose(wallet, k.Key, -k.Value.Value, value is { } v ? Math.Round(v / tokOut.Count, 2) : null, t.Time, t.Sig, solIn ? $"Sold for {sol:0.######} SOL" : $"Swapped for {string.Join(" + ", tokIn.Select(x => x.Key))}");
                foreach (var k in tokIn) Acquire(wallet, k.Key, k.Value.Value, value is { } v2 ? Math.Round(v2 / tokIn.Count, 2) : null, t.Time, solOut ? $"Bought with {-sol:0.######} SOL" : $"Swapped from {string.Join(" + ", tokOut.Select(x => x.Key))}");
                if (solIn) Acquire(wallet, "SOL", sol, value, t.Time, $"Received for {string.Join(" + ", tokOut.Select(x => x.Key))}");
                continue;
            }
            // Anything else, asset by asset: your answer, else not classified.
            foreach (var k in toks)
            {
                var q = Math.Abs(k.Value.Value); var inbound = k.Value.Value > 0;
                var v = UnitUsd(k.Key) is { } u ? Math.Round(q * u, 2) : (decimal?)null;
                if (Answered(wallet, t, k.Key, q, inbound, v)) continue;
                if (inbound) Acquire(wallet, k.Key, q, null, t.Time, "Received from " + string.Join(",", k.Value.Cps) + " (not classified, cost unknown)");
                Unclassified(wallet, t, k.Key, q, inbound, k.Value.Cps, v);
            }
            if (toks.Count > 0 && sol < 0 && -sol <= 0.0025m) { Dispose(wallet, "SOL", -sol, SolUsd(-sol, t.Time), t.Time, t.Sig, "SOL paid as account rent for the token transfer"); continue; }
            if (Math.Abs(sol) >= 0.000000001m)
            {
                var q = Math.Abs(sol); var inbound = sol > 0;
                if (Answered(wallet, t, "SOL", q, inbound, SolUsd(q, t.Time))) continue;
                if (inbound) Acquire(wallet, "SOL", q, null, t.Time, "Received from " + string.Join(",", t.SolFrom.Take(2)) + " (not classified, cost unknown)");
                Unclassified(wallet, t, "SOL", q, inbound, inbound ? t.SolFrom.Take(2) : t.SolTo.Take(2), SolUsd(q, t.Time));
            }
        }
        while (snapYear <= DateTime.UtcNow.Year) { yearEnd[snapYear] = Snapshot(); snapYear++; }
        if (dust > 0) notes.Add($"{dust} tiny SOL deposits (0.0001 SOL or less) from look-alike addresses were ignored as spam (address poisoning). Never copy an address from your Solana history.");

        var holdings = new JsonArray(lots.SelectMany(kv => kv.Value.Where(l => l.Qty > 0.000000001m).Select(l => (JsonNode)new JsonObject
        {
            ["wallet"] = kv.Key, ["asset"] = l.Asset, ["qty"] = l.Qty, ["unitCost"] = l.UnitCost, ["cost"] = l.UnitCost is { } u ? (decimal?)Math.Round(u * l.Qty, 2) : null, ["acquired"] = l.Acquired, ["how"] = l.How, ["id"] = l.Id,
        })).ToArray());
        var ye = new JsonObject();
        foreach (var (y, arr) in yearEnd) ye[y.ToString(CultureInfo.InvariantCulture)] = arr;
        return new JsonObject { ["disposals"] = disposals, ["holdings"] = holdings, ["unclassified"] = unclassified, ["notes"] = notes, ["yearEnd"] = ye, ["price"] = new JsonObject { ["SOL"] = solPx.Count > 0 ? (decimal?)solPx.Last().Value : null } };
    }

    // Adds the Solana results to the Ethereum-side lots (same field names; year-end holdings summed per asset).
    static JsonNode MergeSolanaLots(object evm, JsonObject sol)
    {
        var node = JsonSerializer.SerializeToNode(evm) as JsonObject ?? new JsonObject();
        foreach (var key in new[] { "disposals", "holdings", "unclassified", "notes" })
        {
            var arr = node[key] as JsonArray ?? new JsonArray();
            foreach (var x in (sol[key] as JsonArray ?? new()).ToList()) arr.Add(x!.DeepClone());
            node[key] = arr;
        }
        var price = node["price"] as JsonObject ?? new JsonObject();
        foreach (var (k, v) in sol["price"] as JsonObject ?? new()) price[k] = v?.DeepClone();
        node["price"] = price;
        var ye = node["yearEnd"] as JsonObject ?? new JsonObject();
        foreach (var (y, arr) in sol["yearEnd"] as JsonObject ?? new())
        {
            var list = ye[y] as JsonArray ?? new JsonArray();
            foreach (var h in (arr as JsonArray ?? new()).ToList())
            {
                var asset = h!["asset"]!.ToString();
                if (list.FirstOrDefault(x => x?["asset"]?.ToString() == asset) is JsonObject ex)
                    foreach (var f in new[] { "qty", "cost", "unknownCostQty" }) ex[f] = (ex[f]?.GetValue<decimal>() ?? 0m) + (h[f]?.GetValue<decimal>() ?? 0m);
                else list.Add(h.DeepClone());
            }
            ye[y] = list;
        }
        node["yearEnd"] = ye;
        return node;
    }
}

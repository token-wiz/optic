// OPT Sell Plan tracking: the plan the user started (saved per user) and the OPT they have actually sold on Osmosis since,
// so the page can say how much to sell today and whether the sales are keeping up with the plan.
//   - The saved plan is what the page showed when "Start this plan" was pressed: the goal, the finish-by date and the
//     planned amount for each day. It is kept in odata/users/<user>/sell-plan.json.
//   - Sales are swaps out of OPT in the user's saved Osmosis accounts (from the cached Osmosis history, brought up to date
//     when asked). Dollars are the stablecoins actually received; anything else received (SOVR, OSMO …) is valued at
//     the OPT price at the time of the sale and marked as an estimate. OPT sold inside a liquidity position is not counted here.
using OPTIC.Services;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

public static partial class WebDashboard
{
    static readonly object SellPlanLock = new();

    static string SellPlanPath(HttpContext ctx) => Path.Combine(DashboardAuth.UserDataDir(ctx), "sell-plan.json");

    // The user's Osmosis history, brought up to date first when the saved copy is over 5 minutes old (40 seconds at most;
    // after that the saved copy is used and the result says how old it is).
    static async Task<(List<OsmoTx> Txs, DateTime? AsOf, string? Note)> LoadOsmosisTxsRecentAsync(string address, CancellationToken ct, bool update = true)
    {
        var path = OsmosisCachePath(address);
        string? note = null;
        if (!update) { }
        else if (!await OsmosisWalletGate.WaitAsync(TimeSpan.FromSeconds(40), ct)) note = "the Osmosis history is busy updating; showing the saved copy";
        else
        {
            try
            {
                var cache = File.Exists(path) ? JsonNode.Parse(await File.ReadAllTextAsync(path, ct))!.AsObject() : new JsonObject { ["scannedToHeight"] = 0L, ["txs"] = new JsonArray() };
                DateTime? updated = cache["updatedUtc"] is { } u && DateTime.TryParse(u.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var ut) ? ut : null;
                if (updated is null || DateTime.UtcNow - updated > TimeSpan.FromMinutes(5))
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(TimeSpan.FromSeconds(updated is null ? 240 : 40));
                    try { await UpdateOsmosisCacheAsync(address, cache, cts.Token); }
                    catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { note = "could not check Osmosis for the newest sales (" + ex.Message + "); showing the saved copy"; }
                }
            }
            finally { OsmosisWalletGate.Release(); }
        }
        if (!File.Exists(path)) return (new(), null, note ?? "no Osmosis history yet");
        var saved = JsonNode.Parse(await File.ReadAllTextAsync(path, ct))!.AsObject();
        DateTime? asOf = saved["updatedUtc"] is { } su && DateTime.TryParse(su.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var st) ? st : null;
        return (OsmoTxsFromCache(saved["txs"]!.AsArray()), asOf, note);
    }

    // Swaps out of OPT since a date, across the user's Osmosis accounts.
    static async Task<object> BuildOptSalesAsync(List<(string Address, string Label)> wallets, DateTime since, CancellationToken ct, bool update = true)
    {
        bool IsOpt(string d) => d.StartsWith(OsmosisOptDenomPrefix, StringComparison.Ordinal);
        var sales = new List<(DateTime Time, string Hash, string Wallet, decimal Opt, decimal Usd, Dictionary<string, decimal> Other)>();
        var notes = new List<string>();
        DateTime? asOf = null;
        foreach (var (address, label) in wallets)
        {
            var (txs, at, note) = await LoadOsmosisTxsRecentAsync(address, ct, update);
            if (note is not null) notes.Add($"{(label is { Length: > 0 } ? label : address[..12] + "…")}: {note}.");
            if (at is { } a && (asOf is null || a < asOf)) asOf = a;
            foreach (var t in txs.Where(t => t.Time >= since && t.Types.Any(x => x.Contains("Swap") || x.Contains("SplitRoute"))))
            {
                var net = new Dictionary<string, decimal>(StringComparer.Ordinal);
                foreach (var e in t.Events)
                {
                    var type = e.GetProperty("type").GetString();
                    var attrs = EventAttrs(e);
                    if (type == "coin_received" && attrs.GetValueOrDefault("receiver") == address) foreach (var (amt, d) in ParseCoinList(attrs.GetValueOrDefault("amount", ""))) net[d] = net.GetValueOrDefault(d) + amt;
                    if (type == "coin_spent" && attrs.GetValueOrDefault("spender") == address) foreach (var (amt, d) in ParseCoinList(attrs.GetValueOrDefault("amount", ""))) net[d] = net.GetValueOrDefault(d) - amt;
                }
                foreach (var (d, amt) in t.Fee) if (net.ContainsKey(d)) net[d] += amt;
                var opt = net.Where(kv => IsOpt(kv.Key)).Sum(kv => kv.Value);
                if (opt > -0.000001m) continue;
                var usd = net.Where(kv => OsmosisUsdDenoms.Contains(kv.Key) && kv.Value > 0).Sum(kv => kv.Value);
                var other = net.Where(kv => !IsOpt(kv.Key) && !OsmosisUsdDenoms.Contains(kv.Key) && kv.Value > 0.000001m).ToDictionary(kv => kv.Key, kv => kv.Value);
                sales.Add((t.Time, t.Hash, label is { Length: > 0 } ? label : address, -opt, usd, other));
            }
        }
        // Tokens other than dollars (SOVR, OSMO …): valued at the OPT price at the time of the sale, as the Tax Report does.
        // Amounts are shown with each token's own decimals (from Osmosis's token list).
        var otherDenoms = sales.SelectMany(s => s.Other.Keys).Distinct().ToList();
        var decimals = new Dictionary<string, int>(StringComparer.Ordinal);
        var optPrices = new Dictionary<DateTime, decimal>();
        if (otherDenoms.Count > 0)
        {
            try
            {
                var meta = await SqsGetAsync($"/tokens/metadata?denoms={Uri.EscapeDataString(string.Join(",", otherDenoms))}", ct, 3600) as JsonObject ?? new();
                foreach (var d in otherDenoms) if (meta[d]?["decimals"] is JsonValue dv && dv.TryGetValue<int>(out var n)) decimals[d] = n;
            }
            catch (HttpRequestException) { }
            var times = sales.Where(s => s.Other.Count > 0).Select(s => s.Time).Distinct().ToList();
            var px = await TokenPriceService.GetPricesAtAsync(times, ct);
            foreach (var (t, p) in px) if (p is not null) optPrices[t] = p.PriceUsd;
        }
        decimal Scaled(string denom, decimal sixDecimals) => decimals.TryGetValue(denom, out var n) && n != 6 ? sixDecimals * 1_000_000m / (decimal)Math.Pow(10, n) : sixDecimals;
        var rows = sales.OrderBy(s => s.Time).Select(s =>
        {
            decimal? usd = s.Other.Count == 0 ? s.Usd
                : optPrices.TryGetValue(s.Time, out var op) ? (s.Usd > 0 ? s.Usd : s.Opt * op) : null;
            return new
            {
                time = s.Time, date = s.Time.ToString("yyyy-MM-dd"), hash = s.Hash, wallet = s.Wallet,
                opt = Math.Round(s.Opt, 6), usd = usd is { } u ? Math.Round(u, 2) : (decimal?)null,
                estimated = s.Other.Count > 0 && s.Usd <= 0,
                received = string.Join(", ", (s.Usd > 0 ? new[] { $"{s.Usd:N2} USD" } : Array.Empty<string>()).Concat(s.Other.Select(kv => $"{Scaled(kv.Key, kv.Value):N4} {OsmoDenomName(kv.Key)}"))),
            };
        }).ToList();
        return new { since = since.ToString("yyyy-MM-dd"), asOf, sales = rows, notes, wallets = wallets.Count };
    }

    static void MapSellPlanTracking(WebApplication app)
    {
        app.MapGet("/api/sell-plan/saved", (HttpContext ctx) =>
        {
            var path = SellPlanPath(ctx);
            lock (SellPlanLock)
            {
                try { return VJson(new { plan = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) : null }); }
                catch (JsonException) { return VJson(new { plan = (JsonNode?)null }); }
            }
        });

        // Body: { start, finishBy, goalOpt, mode, scenario, includeLocked, includeIncoming, schedule: [{ d, opt, usd }] }.
        app.MapPost("/api/sell-plan/saved", async (HttpContext ctx) =>
        {
            JsonObject? body;
            try { body = await JsonNode.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted) as JsonObject; }
            catch (JsonException) { return VError("Not valid JSON.", 400); }
            static bool IsDay(JsonNode? n) => DateTime.TryParseExact(n?.ToString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
            static decimal? Num(JsonNode? n) => n is JsonValue v && v.TryGetValue<decimal>(out var d) ? d : null;
            if (body is null || !IsDay(body["start"]) || !IsDay(body["finishBy"]) || Num(body["goalOpt"]) is not > 0 and <= 10_000_000_000m)
                return VError("A start date, finish-by date and an amount of OPT are needed.", 400);
            var schedule = new JsonArray();
            foreach (var r in (body["schedule"] as JsonArray ?? new()).OfType<JsonObject>().Take(2500))
                if (IsDay(r["d"]) && Num(r["opt"]) is >= 0 and var o && Num(r["usd"]) is >= 0 and var u)
                    schedule.Add(new JsonObject { ["d"] = r["d"]!.ToString(), ["opt"] = Math.Round(o, 2), ["usd"] = Math.Round(u, 2) });
            var mode = body["mode"]?.ToString() is "mine" or "unbonding" or "custom" ? body["mode"]!.ToString() : "mine";
            var plan = new JsonObject
            {
                ["start"] = body["start"]!.ToString(), ["finishBy"] = body["finishBy"]!.ToString(),
                ["goalOpt"] = Math.Round(Num(body["goalOpt"])!.Value, 2), ["goalUsd"] = Math.Round(Num(body["goalUsd"]) ?? 0m, 2),
                ["mode"] = mode, ["scenario"] = body["scenario"]?.ToString() is "30" or "90" ? body["scenario"]!.ToString() : "0",
                ["includeLocked"] = body["includeLocked"] is JsonValue lv && lv.TryGetValue<bool>(out var il) && il,
                ["includeIncoming"] = body["includeIncoming"] is not JsonValue iv || !iv.TryGetValue<bool>(out var ii) || ii,
                ["createdUtc"] = DateTime.UtcNow, ["schedule"] = schedule,
            };
            var path = SellPlanPath(ctx);
            lock (SellPlanLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path)) File.Copy(path, path + ".bak", overwrite: true);
                File.WriteAllText(path, plan.ToJsonString());
            }
            return VJson(new { plan });
        });

        // The Sell Plan page's latest re-plan of what is left (your OPT as it comes free, today's pools): { rows: [{ d, opt }] }.
        // Notifications and the bell use it for the day's amount, so they never ask for OPT that isn't free yet.
        app.MapPost("/api/sell-plan/forecast", async (HttpContext ctx) =>
        {
            JsonObject? body;
            try { body = await JsonNode.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted) as JsonObject; }
            catch (JsonException) { return VError("Not valid JSON.", 400); }
            var rows = new JsonArray();
            foreach (var r in (body?["rows"] as JsonArray ?? new()).OfType<JsonObject>().Take(400))
                if (DateTime.TryParseExact(r["d"]?.ToString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) && r["opt"] is JsonValue v && v.TryGetValue<decimal>(out var o) && o >= 0)
                    rows.Add(new JsonObject { ["d"] = r["d"]!.ToString(), ["opt"] = Math.Round(o) });
            var path = SellPlanPath(ctx);
            lock (SellPlanLock)
            {
                if (!File.Exists(path) || JsonNode.Parse(File.ReadAllText(path)) is not JsonObject plan) return VError("No plan is being followed.", 404);
                plan["forecast"] = new JsonObject { ["computedUtc"] = DateTime.UtcNow, ["rows"] = rows };
                File.WriteAllText(path, plan.ToJsonString());
            }
            return VJson(new { ok = true });
        });

        app.MapDelete("/api/sell-plan/saved", (HttpContext ctx) =>
        {
            var path = SellPlanPath(ctx);
            lock (SellPlanLock) if (File.Exists(path)) File.Move(path, path + ".bak", overwrite: true);
            return VJson(new { plan = (JsonNode?)null });
        });

        app.MapGet("/api/osmosis/opt-sales", async (HttpContext ctx) =>
        {
            if (!DateTime.TryParseExact(ctx.Request.Query["since"].ToString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var since))
                return VError("Give a start date (since=yyyy-MM-dd).");
            var wallets = UserOsmoWallets(ctx);
            if (wallets.Count == 0) return VJson(new { since = since.ToString("yyyy-MM-dd"), sales = Array.Empty<object>(), notes = new[] { "No Osmosis account is saved in My Wallets, so sales can't be followed." }, wallets = 0 });
            // update=0: straight from the saved history (instant); otherwise brought up to date first.
            try { return VJson(await BuildOptSalesAsync(wallets, since, ctx.RequestAborted, ctx.Request.Query["update"] != "0")); }
            catch (Exception ex) when (ex is not OperationCanceledException) { return VError("Could not read your Osmosis sales: " + ex.Message, 502); }
        });
    }
}

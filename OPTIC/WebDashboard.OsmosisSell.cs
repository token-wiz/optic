// Sell OPT on Osmosis: every Osmosis pool that holds OPT, with a live quote for the amount you want to sell, and which way
// gets you the most dollars. All figures come from Osmosis's own routing service (sqs.osmosis.zone), the same quotes the
// Osmosis app uses:
//   - Each pool on its own (a direct swap in that pool only), valued in USD at Osmosis's price for the token you receive.
//     For a token that isn't a dollar stablecoin (SOVR, OSMO …) the page also quotes swapping it on to USDC, since that is what
//     you actually end up with in dollars.
//   - Osmosis's router into USDC, which can split the sale across pools and hop through others.
//   - A size ladder for the router: what selling more or less would return, and how far the price falls as you sell more.
// Fees shown are the pool's swap fee plus Osmosis's taker fee, both already inside the quotes.
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

public static partial class WebDashboard
{
    const string SqsBase = "https://sqs.osmosis.zone";
    const string OsmoOptDenom = "ibc/6AA47B1A9F00CC9B127796717F4BE976C12EFC843387F22461152888E1365045";
    const string OsmoNobleUsdc = "ibc/498A0751C798A0D9A389AA3691123DADA57DAA4FE165D5C75894505B876BA6E4";
    static readonly Dictionary<string, (DateTime At, JsonNode? Data)> SqsCache = new();

    // A GET from the routing service, kept for a minute (quotes move with every trade).
    static async Task<JsonNode?> SqsGetAsync(string path, CancellationToken ct, int seconds = 60)
    {
        lock (SqsCache) if (SqsCache.TryGetValue(path, out var c) && DateTime.UtcNow - c.At < TimeSpan.FromSeconds(seconds)) return c.Data;
        using var res = await EvmHttp.GetAsync(SqsBase + path, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            string? msg = null;
            try { msg = JsonNode.Parse(text)?["message"]?.ToString(); } catch (JsonException) { }
            throw new HttpRequestException(msg is { Length: > 0 } ? msg : $"Osmosis routing service: {(int)res.StatusCode} {text[..Math.Min(text.Length, 160)].Trim()}");
        }
        var node = JsonNode.Parse(text);
        lock (SqsCache) SqsCache[path] = (DateTime.UtcNow, node);
        return node;
    }

    static decimal Dec(JsonNode? n) => decimal.TryParse(n?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0m;

    static string Units(decimal amount, int decimals) => decimal.Floor(amount * (decimal)Math.Pow(10, decimals)).ToString("0", CultureInfo.InvariantCulture);

    static async Task<object> BuildOptSellAsync(decimal sellOpt, CancellationToken ct)
    {
        var poolsNode = await SqsGetAsync($"/pools?denoms={Uri.EscapeDataString(OsmoOptDenom)}", ct, 120);
        var pools = ((poolsNode is JsonObject pobj ? pobj["data"] : poolsNode) as JsonArray ?? new()).OfType<JsonObject>()
            .Where(p => (p["balances"] as JsonArray ?? new()).OfType<JsonObject>().Any(b => b["denom"]?.ToString() == OsmoOptDenom)).ToList();
        var others = pools.SelectMany(p => (p["balances"] as JsonArray ?? new()).OfType<JsonObject>().Select(b => b["denom"]!.ToString())).Where(d => d != OsmoOptDenom).Distinct().ToList();
        var denoms = others.Append(OsmoOptDenom).Append(OsmoNobleUsdc).Distinct().ToList();
        var meta = await SqsGetAsync($"/tokens/metadata?denoms={Uri.EscapeDataString(string.Join(",", denoms))}", ct, 3600) as JsonObject ?? new();
        var priceNode = await SqsGetAsync($"/tokens/prices?base={Uri.EscapeDataString(string.Join(",", denoms))}", ct) as JsonObject ?? new();
        decimal? Usd(string denom) => priceNode[denom] is JsonObject o && o.FirstOrDefault().Value is { } v ? Dec(v) : null;
        string Sym(string d) => meta[d]?["symbol"]?.ToString() is { Length: > 0 } s ? s : d.Length > 14 ? d[..10] + "…" : d;
        int Decimals(string d) => meta[d]?["decimals"]?.GetValue<int>() ?? 6;
        bool Stable(string d) => Sym(d) is "USDC" or "USDT" or "USDC.axl" or "allUSDC" or "allUSDT" || d == OsmoNobleUsdc || d.Contains("/alloyed/allUSD", StringComparison.Ordinal);
        var spot = Usd(OsmoOptDenom);
        var inUnits = Units(sellOpt, 6);

        // Each pool alone, then (for a non-stable token) that token on to USDC by the router.
        var rows = new List<object>();
        foreach (var p in pools)
        {
            var id = p["chain_model"]?["id"]?.ToString() ?? p["id"]?.ToString() ?? "";
            var other = (p["balances"] as JsonArray ?? new()).OfType<JsonObject>().Select(b => b["denom"]!.ToString()).FirstOrDefault(d => d != OsmoOptDenom) ?? "";
            var optInPool = (p["balances"] as JsonArray ?? new()).OfType<JsonObject>().Where(b => b["denom"]?.ToString() == OsmoOptDenom).Sum(b => Dec(b["amount"])) / 1_000_000m;
            var otherInPool = (p["balances"] as JsonArray ?? new()).OfType<JsonObject>().Where(b => b["denom"]?.ToString() == other).Sum(b => Dec(b["amount"])) / (decimal)Math.Pow(10, Decimals(other));
            var liquidityUsd = Dec(p["liquidity_cap"]);
            var type = p["type"]?.GetValue<int>() switch { 0 => "Classic (balancer)", 1 => "Stableswap", 2 => "Concentrated", 3 => "CosmWasm", _ => "Other" };
            var fee = Dec(p["spread_factor"]);
            decimal? got = null, usd = null, impact = null, cashUsd = null; string? error = null, cashNote = null;
            if (liquidityUsd >= 5m)
            {
                try
                {
                    var q = await SqsGetAsync($"/router/custom-direct-quote?tokenIn={inUnits}{Uri.EscapeDataString(OsmoOptDenom)}&tokenOutDenom={Uri.EscapeDataString(other)}&poolID={id}", ct);
                    got = Dec(q?["amount_out"]) / (decimal)Math.Pow(10, Decimals(other));
                    impact = Dec(q?["price_impact"]);
                    usd = Usd(other) is { } up ? Math.Round(got.Value * up, 2) : null;
                    if (Stable(other)) cashUsd = usd;
                    else if (got > 0)
                    {
                        // What that token turns into in USDC right after.
                        try
                        {
                            var q2 = await SqsGetAsync($"/router/quote?tokenIn={Units(got.Value, Decimals(other))}{Uri.EscapeDataString(other)}&tokenOutDenom={Uri.EscapeDataString(OsmoNobleUsdc)}&singleRoute=false", ct);
                            cashUsd = Math.Round(Dec(q2?["amount_out"]) / 1_000_000m, 2);
                            cashNote = $"then {got:N4} {Sym(other)} → USDC";
                        }
                        catch (HttpRequestException ex) { cashNote = "could not quote " + Sym(other) + " → USDC: " + ex.Message; }
                    }
                }
                catch (HttpRequestException ex) { error = ex.Message.Contains("not enough liquidity", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("insufficient", StringComparison.OrdinalIgnoreCase) ? "Not enough liquidity for this amount" : ex.Message.Contains("no liquidity", StringComparison.OrdinalIgnoreCase) ? "Empty pool" : ex.Message; }
            }
            else error = "Empty pool";
            rows.Add(new
            {
                id, type, pair = $"OPT / {Sym(other)}", token = Sym(other), stable = Stable(other), fee, liquidityUsd, optInPool, otherInPool,
                receive = got, receiveUsd = usd, cashUsd, cashNote, priceImpact = impact,
                avgPrice = cashUsd is { } cu && sellOpt > 0 ? cu / sellOpt : (decimal?)null, error,
            });
        }

        // The router into USDC (may split across pools and hop), for the amount and for a ladder of sizes.
        async Task<(decimal? Usd, decimal? Impact, string Route, string? Error)> Router(decimal opt)
        {
            try
            {
                var q = await SqsGetAsync($"/router/quote?tokenIn={Units(opt, 6)}{Uri.EscapeDataString(OsmoOptDenom)}&tokenOutDenom={Uri.EscapeDataString(OsmoNobleUsdc)}&singleRoute=false", ct);
                var routes = (q?["route"] as JsonArray ?? new()).OfType<JsonObject>().Select(r =>
                {
                    var share = Dec(r["in_amount"]) / Math.Max(1m, Dec(q?["amount_in"]?["amount"]));
                    var hops = string.Join(" → ", (r["pools"] as JsonArray ?? new()).OfType<JsonObject>().Select(pl => $"{Sym(pl["token_out_denom"]?.ToString() ?? "")} (pool {pl["id"]})"));
                    return $"{share:P0}: OPT → {hops}";
                });
                return (Math.Round(Dec(q?["amount_out"]) / 1_000_000m, 2), Dec(q?["price_impact"]), string.Join("; ", routes), null);
            }
            catch (HttpRequestException ex) { return (null, null, "", ex.Message); }
        }
        var best = await Router(sellOpt);
        var ladder = new List<object>();
        foreach (var size in new[] { 10_000m, 50_000m, 100_000m, 250_000m, 500_000m, 1_000_000m, 2_500_000m, 5_000_000m, 10_000_000m })
        {
            var r = await Router(size);
            ladder.Add(new { opt = size, usd = r.Usd, avgPrice = r.Usd is { } u ? u / size : (decimal?)null, priceImpact = r.Impact, error = r.Error });
            if (r.Error is not null || r.Impact is { } im && Math.Abs(im) > 0.6m) break; // deeper sizes only get worse
        }

        return new
        {
            sellOpt, spot, spotValue = spot is { } s ? Math.Round(s * sellOpt, 2) : (decimal?)null,
            router = new { usd = best.Usd, priceImpact = best.Impact, route = best.Route, error = best.Error, avgPrice = best.Usd is { } bu && sellOpt > 0 ? bu / sellOpt : (decimal?)null },
            pools = rows, ladder, readUtc = DateTime.UtcNow, source = SqsBase,
        };
    }

    // The market model behind the OPT Sell Plan page: what selling any amount at once returns right now (live router quotes
    // into USDC on a grid of sizes, so the page can value a sale on top of OPT that hasn't been bought back yet), how much
    // OPT other traders have been buying per day (what refills the pools after a sale), and recent prices by day and weekday.
    static async Task<object> BuildSellModelAsync(CancellationToken ct)
    {
        var sizes = new[] { 5_000m, 10_000m, 25_000m, 50_000m, 100_000m, 150_000m, 250_000m, 400_000m, 600_000m, 1_000_000m, 1_500_000m, 2_500_000m, 4_000_000m, 6_000_000m, 10_000_000m, 20_000_000m, 40_000_000m };
        var meta = await SqsGetAsync($"/tokens/metadata?denoms={Uri.EscapeDataString(OsmoOptDenom)}", ct, 3600);
        var priceNode = await SqsGetAsync($"/tokens/prices?base={Uri.EscapeDataString(OsmoOptDenom)}", ct) as JsonObject ?? new();
        var spot = priceNode[OsmoOptDenom] is JsonObject po && po.FirstOrDefault().Value is { } pv ? Dec(pv) : (decimal?)null;
        var quotes = await Task.WhenAll(sizes.Select(async size =>
        {
            try
            {
                var q = await SqsGetAsync($"/router/quote?tokenIn={Units(size, 6)}{Uri.EscapeDataString(OsmoOptDenom)}&tokenOutDenom={Uri.EscapeDataString(OsmoNobleUsdc)}&singleRoute=false", ct);
                var total = Math.Max(1m, Dec(q?["amount_in"]?["amount"]));
                var routes = (q?["route"] as JsonArray ?? new()).OfType<JsonObject>().Select(r => new
                {
                    share = Math.Round(Dec(r["in_amount"]) / total, 3),
                    pools = (r["pools"] as JsonArray ?? new()).OfType<JsonObject>().Select(pl => pl["id"]?.ToString()).ToList(),
                }).ToList();
                return (object)new { opt = size, usd = Math.Round(Dec(q?["amount_out"]) / 1_000_000m, 2), priceImpact = Dec(q?["price_impact"]), routes };
            }
            catch (HttpRequestException ex) { return new { opt = size, usd = (decimal?)null, priceImpact = (decimal?)null, routes = new List<object>(), error = ex.Message }; }
        }));
        // Names for the pools the routes use.
        var routeIds = quotes.SelectMany(q => JsonSerializer.SerializeToNode(q)?["routes"] as JsonArray ?? new()).SelectMany(r => r?["pools"] as JsonArray ?? new()).Select(x => x?.ToString()).Where(x => !string.IsNullOrEmpty(x)).Distinct().ToList();
        var poolsNode = await SqsGetAsync($"/pools?IDs={string.Join(",", routeIds)}", ct, 600);
        var poolInfo = new Dictionary<string, string>();
        foreach (var p in ((poolsNode is JsonObject pobj ? pobj["data"] : poolsNode) as JsonArray ?? new()).OfType<JsonObject>())
        {
            var cm = p["chain_model"];
            var ds = (p["balances"] as JsonArray ?? new()).OfType<JsonObject>().Select(b => b["denom"]!.ToString()).ToList();
            if (ds.Count < 2 && cm?["asset_configs"] is JsonArray ac) ds = ac.OfType<JsonObject>().Select(c => c["denom"]?.ToString() ?? "").ToList();
            var id = cm?["id"]?.ToString() ?? cm?["pool_id"]?.ToString() ?? "";
            // A pool of several USDC versions (alloyed USDC) just converts between them 1:1.
            if (ds.Count > 2 && ds.All(d => SymOf(d, meta) is "USDC" || d.Contains("/alloyed/", StringComparison.Ordinal) || d.StartsWith("ibc/", StringComparison.Ordinal))) poolInfo[id] = "USDC converter";
            else if (ds.Count >= 2 && id != "") poolInfo[id] = $"{SymOf(ds[0], meta)} / {SymOf(ds[1], meta)}";
        }

        var days = await OPTIC.Services.MarketVolumeService.GetDailyAsync(ct);
        var today = DateTime.UtcNow.Date;
        var last30 = days.Where(d => d.DateUtc >= today.AddDays(-30)).ToList();
        var bought30 = last30.Sum(d => d.OsmosisBoughtOpt);
        var sold30 = last30.Sum(d => d.OsmosisSoldOpt);
        var priced = days.Where(d => d.OsmosisPriceUsd is > 0).Select(d => (d.DateUtc, P: d.OsmosisPriceUsd!.Value)).ToList();
        decimal? Median(IEnumerable<decimal> xs) { var l = xs.OrderBy(x => x).ToList(); return l.Count == 0 ? null : l[l.Count / 2]; }
        // Weekday effect: each day's price against the median of the week around it.
        var byDate = priced.ToDictionary(x => x.DateUtc.Date, x => x.P);
        var weekday = Enumerable.Range(0, 7).Select(w =>
        {
            var rel = priced.Where(x => (int)x.DateUtc.DayOfWeek == w).Select(x =>
            {
                var win = Enumerable.Range(-3, 7).Select(k => x.DateUtc.Date.AddDays(k)).Where(byDate.ContainsKey).Select(d => byDate[d]).ToList();
                return win.Count >= 5 ? (decimal?)(x.P / Median(win)!.Value - 1) : null;
            }).Where(v => v.HasValue).Select(v => v!.Value).ToList();
            return new { day = ((DayOfWeek)w).ToString(), vsWeek = rel.Count > 0 ? Math.Round(rel.Average(), 4) : (decimal?)null, samples = rel.Count };
        }).ToList();
        decimal? Change(int n) { var sub = priced.Where(x => x.DateUtc >= today.AddDays(-n)).ToList(); return sub.Count >= 2 ? sub[^1].P / sub[0].P - 1 : null; }

        return new
        {
            spot, quotes,
            pools = poolInfo,
            buyFlowPerDay = Math.Round(bought30 / 30m, 0), sellFlowPerDay = Math.Round(sold30 / 30m, 0),
            median7 = Median(priced.Where(x => x.DateUtc >= today.AddDays(-7)).Select(x => x.P)),
            median30 = Median(priced.Where(x => x.DateUtc >= today.AddDays(-30)).Select(x => x.P)),
            change30 = Change(30), change90 = Change(90),
            recent = days.Where(d => d.DateUtc >= today.AddDays(-30)).Select(d => new { date = d.DateUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), price = d.OsmosisPriceUsd, bought = d.OsmosisBoughtOpt, sold = d.OsmosisSoldOpt }),
            weekday, readUtc = DateTime.UtcNow,
        };
    }

    static string SymOf(string denom, JsonNode? meta) => denom == OsmoOptDenom ? "OPT" : denom == OsmoNobleUsdc ? "USDC" : denom == OsmosisSovrDenom ? "SOVR"
        : denom.Contains("/alloyed/allUSDC", StringComparison.Ordinal) ? "USDC" : denom.Contains("/alloyed/allUSDT", StringComparison.Ordinal) ? "USDT" : denom == "uosmo" ? "OSMO"
        : meta?[denom]?["symbol"]?.ToString() ?? (denom.Length > 12 ? denom[..10] + "…" : denom);

    static void MapOsmosisSell(WebApplication app)
    {
        app.MapGet("/api/osmosis/sell-model", async (HttpContext ctx) =>
        {
            try { return VJson(await BuildSellModelAsync(ctx.RequestAborted)); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException && !ctx.RequestAborted.IsCancellationRequested)
            { return VError("Could not read Osmosis just now: " + ex.Message, 502); }
        });
        app.MapGet("/api/osmosis/opt-sell", async (HttpContext ctx) =>
        {
            if (!decimal.TryParse(ctx.Request.Query["opt"], NumberStyles.Float, CultureInfo.InvariantCulture, out var opt) || opt <= 0 || opt > 10_000_000_000m)
                return VError("Give opt=<amount of OPT to sell>.");
            try { return VJson(await BuildOptSellAsync(Math.Round(opt, 6), ctx.RequestAborted)); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !ctx.RequestAborted.IsCancellationRequested)
            { return VError("Could not read Osmosis just now: " + ex.Message, 502); }
        });
    }
}

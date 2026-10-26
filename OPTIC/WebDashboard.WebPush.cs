// Web Push notifications: on days the user's OPT Sell Plan has OPT to sell, a notification at the hour they chose (8 AM
// unless changed; "Sell 151,000 OPT today"), and optionally a second one in the evening if it hasn't been sold yet. On days
// the sale is split into two swaps, a reminder for the second swap comes about 3 hours after the first swap is seen on
// Osmosis, at whatever time of day that is. Also the same "today" figure
// for the reminders bell on every page.
//   - The server's VAPID key pair is made once and kept in odata/auth/webpush.json; browsers sign up to it.
//   - Each user's browser subscriptions, hour, time zone and what was last sent are in odata/users/<user>/push.json.
//   - Messages are encrypted for each browser (RFC 8291, see Services/WebPushCrypto.cs) and sent to its push service
//     (Google, Mozilla, Microsoft or Apple). A subscription the push service says is gone (404/410) is removed.
//   - Today's amount: the plan's amount for today, scaled by what is left over what the plan still has from today on (so a
//     short day spreads over the days left), less what was already sold today. The Sell Plan page re-plans in full from the
//     pools, so its figure can differ slightly; the notification says "about".
// Browsers only run the notification helper (/sw.js) on a secure page: HTTPS with a certificate the device trusts, or localhost.
using OPTIC.Services;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

public static partial class WebDashboard
{
    static readonly SemaphoreSlim PushGate = new(1, 1);
    // Push services close idle connections; keep pooled ones briefly and retry once if one was closed under us.
    static readonly HttpClient PushHttp = new(new SocketsHttpHandler { PooledConnectionIdleTimeout = TimeSpan.FromSeconds(15), PooledConnectionLifetime = TimeSpan.FromMinutes(2) }) { Timeout = TimeSpan.FromSeconds(20) };
    static Timer? _pushTimer;
    static int _pushRunning;

    static string VapidPath => Path.Combine(Environment.CurrentDirectory, "odata", "auth", "webpush.json");
    static string PushPath(string userDir) => Path.Combine(userDir, "push.json");

    static (string Private, string Public, string Subject) VapidKeys()
    {
        lock (VapidPathLock)
        {
            if (File.Exists(VapidPath) && JsonNode.Parse(File.ReadAllText(VapidPath)) is JsonObject o && o["privateKey"] is not null)
                return (o["privateKey"]!.ToString(), o["publicKey"]!.ToString(), PushSubject());
            var (priv, pub) = WebPushCrypto.NewVapidKeys();
            Directory.CreateDirectory(Path.GetDirectoryName(VapidPath)!);
            File.WriteAllText(VapidPath, new JsonObject { ["privateKey"] = priv, ["publicKey"] = pub, ["createdUtc"] = DateTime.UtcNow }.ToJsonString());
            return (priv, pub, PushSubject());
        }
    }
    static readonly object VapidPathLock = new();

    // Push services want a contact for the sender (pushContact= in optic.conf, a mailto: or https: address).
    static string PushSubject()
    {
        var c = LoadDashboardConfig().GetValueOrDefault("pushContact", "").Trim();
        return c.StartsWith("mailto:") || c.StartsWith("https://") ? c : "mailto:optic-push@example.com";
    }

    static JsonObject LoadPush(string userDir)
    {
        try { if (File.Exists(PushPath(userDir)) && JsonNode.Parse(File.ReadAllText(PushPath(userDir))) is JsonObject o) return o; }
        catch (JsonException) { }
        return new JsonObject { ["subs"] = new JsonArray(), ["hour"] = 8, ["timeZone"] = "UTC", ["evening"] = false };
    }
    static void SavePush(string userDir, JsonObject o)
    {
        Directory.CreateDirectory(userDir);
        File.WriteAllText(PushPath(userDir), o.ToJsonString());
    }

    static TimeZoneInfo? ZoneOrNull(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64) return null;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch (Exception) { return null; }
    }

    static TimeZoneInfo PushZone(JsonObject push)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(push["timeZone"]?.ToString() ?? "UTC"); }
        catch (Exception) { return TimeZoneInfo.Utc; }
    }

    // Today's selling reminder for a user, or null when no plan is followed.
    // Days are the user's own calendar days (their time zone), as on the Sell Plan page, not UTC days.
    static async Task<JsonObject?> SellPlanTodayAsync(string userDir, bool update, CancellationToken ct, TimeZoneInfo? zone = null)
    {
        zone ??= PushZone(LoadPush(userDir));
        string Day(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(utc, zone).ToString("yyyy-MM-dd");
        var planPath = Path.Combine(userDir, "sell-plan.json");
        if (!File.Exists(planPath)) return null;
        JsonObject plan;
        try { plan = JsonNode.Parse(await File.ReadAllTextAsync(planPath, ct)) as JsonObject ?? new(); }
        catch (JsonException) { return null; }
        var schedule = (plan["schedule"] as JsonArray ?? new()).OfType<JsonObject>()
            .Select(r => (D: r["d"]!.ToString(), Opt: Dec(r["opt"]))).ToList();
        var goal = Dec(plan["goalOpt"]);
        var start = plan["start"]?.ToString() ?? "";
        if (goal <= 0 || start.Length != 10) return null;
        // The start of the start day in the user's zone, as UTC.
        var startDay = TimeZoneInfo.ConvertTimeToUtc(DateTime.ParseExact(start, "yyyy-MM-dd", CultureInfo.InvariantCulture), zone);
        // A plan started today counts only the sales made after it was started (as the Sell Plan page does).
        var cut = DateTime.TryParse(plan["createdUtc"]?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var created) && Day(created) == start ? created : startDay;

        var wallets = UserOsmoWalletsIn(userDir);
        var sales = new List<(DateTime Time, decimal Opt)>();
        if (wallets.Count > 0)
        {
            var node = JsonSerializer.SerializeToNode(await BuildOptSalesAsync(wallets, startDay.Date, ct, update));
            foreach (var s in node?["sales"] as JsonArray ?? new())
                if (DateTime.TryParse(s?["time"]?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var t) && t >= cut) sales.Add((t, Dec(s!["opt"])));
        }
        var today = Day(DateTime.UtcNow);
        var soldTotal = sales.Sum(s => s.Opt);
        var soldToday = sales.Where(s => Day(s.Time) == today).Sum(s => s.Opt);
        var lastSaleToday = sales.Where(s => Day(s.Time) == today).Select(s => (DateTime?)s.Time).Max();
        var soldBefore = soldTotal - soldToday;
        var plannedBefore = schedule.Where(r => string.CompareOrdinal(r.D, today) < 0).Sum(r => r.Opt);
        var plannedThrough = plannedBefore + schedule.Where(r => r.D == today).Sum(r => r.Opt);
        var plannedFromToday = schedule.Where(r => string.CompareOrdinal(r.D, today) >= 0).Sum(r => r.Opt);
        var left = Math.Max(0, goal - soldBefore);
        var scale = plannedFromToday > 0 ? left / plannedFromToday : 0;
        var target = schedule.Where(r => r.D == today).Sum(r => r.Opt) * scale;
        // The page's latest re-plan knows when your OPT comes free; when it covers today, its amounts are used.
        var forecast = (plan["forecast"]?["rows"] as JsonArray ?? new()).OfType<JsonObject>().Select(r => (D: r["d"]!.ToString(), Opt: Dec(r["opt"]))).ToList();
        var useForecast = forecast.Count > 0 && string.CompareOrdinal(forecast[0].D, today) <= 0 && forecast.Any(r => string.CompareOrdinal(r.D, today) >= 0);
        if (useForecast) target = forecast.Where(r => r.D == today).Sum(r => r.Opt);
        var leftToday = Math.Max(0, target - soldToday);
        var next = useForecast ? forecast.FirstOrDefault(r => string.CompareOrdinal(r.D, today) > 0 && r.Opt > 0) : schedule.FirstOrDefault(r => string.CompareOrdinal(r.D, today) > 0 && r.Opt > 0);
        var tol = Math.Max(1000m, goal * 0.01m);
        var done = goal - soldTotal <= Math.Max(1m, goal * 0.001m);
        var status = done ? "done" : soldTotal > plannedThrough + tol ? "ahead" : soldTotal >= plannedBefore - tol ? "on-track" : "behind";
        return new JsonObject
        {
            ["active"] = true, ["date"] = today, ["finishBy"] = plan["finishBy"]?.ToString(), ["goalOpt"] = Math.Round(goal),
            ["soldTotal"] = Math.Round(soldTotal), ["soldToday"] = Math.Round(soldToday),
            ["target"] = Math.Round(target / 1000m) * 1000m, ["leftToday"] = Math.Round(leftToday / 1000m) * 1000m,
            ["status"] = status, ["behindBy"] = Math.Round(Math.Max(0, plannedBefore - soldTotal)), ["aheadBy"] = Math.Round(Math.Max(0, soldTotal - plannedThrough)),
            ["swaps"] = target >= SplitSaleOpt ? 2 : 1, ["lastSaleUtc"] = lastSaleToday,
            ["next"] = next.D is null ? null : new JsonObject { ["date"] = next.D, ["opt"] = Math.Round(next.Opt * (useForecast ? 1 : scale) / 1000m) * 1000m },
            ["fromPage"] = useForecast,
        };
    }

    // A day's sale this big is done as two swaps a few hours apart, so arbitrage can refill the pools in between (as the page says).
    const decimal SplitSaleOpt = 150_000m;
    static readonly TimeSpan SecondSwapAfter = TimeSpan.FromHours(3);

    // How Osmosis's router would sell this much OPT into USDC right now: the pools (with each route's share and OPT) and the
    // USDC it would return. The step through the USDC converter pool (USDC versions 1:1) is left out of the text.
    static async Task<(decimal? Usd, string Pools)> OptSwapRouteAsync(decimal opt, CancellationToken ct)
    {
        try
        {
            var q = await SqsGetAsync($"/router/quote?tokenIn={Units(opt, 6)}{Uri.EscapeDataString(OsmoOptDenom)}&tokenOutDenom={Uri.EscapeDataString(OsmoNobleUsdc)}&singleRoute=false", ct);
            var total = Math.Max(1m, Dec(q?["amount_in"]?["amount"]));
            var routes = (q?["route"] as JsonArray ?? new()).OfType<JsonObject>()
                .Select(r => (Share: Dec(r["in_amount"]) / total, Pools: (r["pools"] as JsonArray ?? new()).OfType<JsonObject>().Select(pl => pl["id"]?.ToString() ?? "").ToList()))
                .OrderByDescending(r => r.Share).ToList();
            var ids = routes.SelectMany(r => r.Pools).Distinct().ToList();
            var names = new Dictionary<string, string>();
            if (ids.Count > 0)
            {
                var poolsNode = await SqsGetAsync($"/pools?IDs={string.Join(",", ids)}", ct, 600);
                foreach (var pl in ((poolsNode is JsonObject pobj ? pobj["data"] : poolsNode) as JsonArray ?? new()).OfType<JsonObject>())
                {
                    var cm = pl["chain_model"];
                    var id = cm?["id"]?.ToString() ?? cm?["pool_id"]?.ToString() ?? "";
                    var ds = (pl["balances"] as JsonArray ?? new()).OfType<JsonObject>().Select(b => b["denom"]!.ToString()).ToList();
                    if (ds.Count < 2 && cm?["asset_configs"] is JsonArray ac) ds = ac.OfType<JsonObject>().Select(c => c["denom"]?.ToString() ?? "").ToList();
                    if (ds.Count > 2 && ds.All(d => SymOf(d, null) is "USDC" || d.Contains("/alloyed/", StringComparison.Ordinal) || d.StartsWith("ibc/", StringComparison.Ordinal))) names[id] = "";
                    else if (ds.Count >= 2) names[id] = $"{SymOf(ds[0], null)}/{SymOf(ds[1], null)}";
                }
            }
            string Step(string id) => names.TryGetValue(id, out var n) ? (n.Length == 0 ? "" : $"{n} #{id}") : $"pool #{id}";
            var text = string.Join("; ", routes.Select(r => $"{(routes.Count > 1 ? $"{r.Share:P0} ({opt * r.Share:N0} OPT) via " : "")}{string.Join(" → ", r.Pools.Select(Step).Where(x => x.Length > 0))}"));
            return (Math.Round(Dec(q?["amount_out"]) / 1_000_000m, 2), text);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { return (null, ""); }
    }

    static async Task<(string Title, string Body)?> SellMessageAsync(JsonObject t, bool evening, CancellationToken ct)
    {
        var leftToday = Dec(t["leftToday"]);
        var target = Dec(t["target"]);
        if (leftToday < Math.Max(1000m, target * 0.02m)) return null;
        var status = t["status"]?.ToString() switch
        {
            "ahead" => $"You're ahead of the plan by {Dec(t["aheadBy"]):N0} OPT.",
            "behind" => $"You're behind the plan by {Dec(t["behindBy"]):N0} OPT.",
            _ => "You're on track.",
        };
        var split = leftToday >= SplitSaleOpt;
        var swap = Math.Round((split ? leftToday / 2 : leftToday) / 100m) * 100m;
        var (usd, pools) = await OptSwapRouteAsync(swap, ct);
        var how = split ? $"Swap {swap:N0} OPT → USDC on Osmosis now and the same again in about 3 hours (you'll get a reminder)." : $"Swap {swap:N0} OPT → USDC on Osmosis.";
        var where = pools.Length > 0 ? $" Pools: {pools}." : "";
        var get = usd is { } u ? $" Expect about ${u:N2}{(split ? " per swap" : "")}." : "";
        var title = evening ? $"Still to sell today: about {leftToday:N0} OPT" : $"Sell about {leftToday:N0} OPT today";
        return (title, $"{how}{where}{get} {status} {Dec(t["soldTotal"]):N0} of {Dec(t["goalOpt"]):N0} sold.");
    }

    // Sends one message to every subscription of a user; removes the ones the push service says are gone.
    static async Task<List<object>> SendPushAsync(string userDir, object message, CancellationToken ct)
    {
        var (priv, pub, subject) = VapidKeys();
        var payload = JsonSerializer.SerializeToUtf8Bytes(message);
        var results = new List<object>();
        await PushGate.WaitAsync(ct);
        try
        {
            var push = LoadPush(userDir);
            var subs = push["subs"] as JsonArray ?? new();
            var gone = new List<JsonNode>();
            foreach (var s in subs.OfType<JsonObject>())
            {
                var endpoint = s["endpoint"]!.ToString();
                var host = new Uri(endpoint).Host;
                try
                {
                    var body = WebPushCrypto.Encrypt(payload, WebPushCrypto.FromB64Url(s["p256dh"]!.ToString()), WebPushCrypto.FromB64Url(s["auth"]!.ToString()));
                    HttpRequestMessage Request()
                    {
                        var req = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new ByteArrayContent(body) };
                        req.Content.Headers.ContentType = new("application/octet-stream");
                        req.Content.Headers.ContentEncoding.Add("aes128gcm");
                        req.Headers.TryAddWithoutValidation("TTL", "43200");
                        req.Headers.TryAddWithoutValidation("Urgency", "high");
                        req.Headers.TryAddWithoutValidation("Authorization", WebPushCrypto.VapidAuthorization(endpoint, subject, priv, pub));
                        return req;
                    }
                    HttpResponseMessage res;
                    try { using var first = Request(); res = await PushHttp.SendAsync(first, ct); }
                    catch (HttpRequestException) { using var again = Request(); res = await PushHttp.SendAsync(again, ct); }
                    using var _res = res;
                    var code = (int)res.StatusCode;
                    if (code is 404 or 410) gone.Add(s);
                    var text = res.IsSuccessStatusCode ? "" : (await res.Content.ReadAsStringAsync(ct)).Trim();
                    results.Add(new { device = s["device"]?.ToString(), service = host, status = code, ok = res.IsSuccessStatusCode, error = text.Length > 0 ? text[..Math.Min(200, text.Length)] : null, removed = code is 404 or 410 });
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or ArgumentException or FormatException)
                {
                    results.Add(new { device = s["device"]?.ToString(), service = host, status = 0, ok = false, error = ex.InnerException is { } inner ? ex.Message + " " + inner.Message + (inner.InnerException is { } i2 ? " " + i2.Message : "") : ex.Message, removed = false });
                }
            }
            if (gone.Count > 0) { foreach (var g in gone) subs.Remove(g); SavePush(userDir, push); }
        }
        finally { PushGate.Release(); }
        return results;
    }

    // Every 5 minutes: users whose chosen hour has come (in their time zone) and who haven't had today's reminder yet.
    static async Task PushTickAsync()
    {
        if (Interlocked.Exchange(ref _pushRunning, 1) == 1) return;
        try
        {
            var usersDir = Path.Combine(Environment.CurrentDirectory, "odata", "users");
            if (!Directory.Exists(usersDir)) return;
            foreach (var dir in Directory.GetDirectories(usersDir))
            {
                if (!File.Exists(PushPath(dir)) || !File.Exists(Path.Combine(dir, "sell-plan.json"))) continue;
                try
                {
                    var push = LoadPush(dir);
                    if ((push["subs"] as JsonArray ?? new()).Count == 0) continue;
                    var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, PushZone(push));
                    var day = local.ToString("yyyy-MM-dd");
                    var hour = push["hour"]?.GetValue<int>() ?? 8;
                    var evening = push["evening"]?.GetValue<bool>() == true && local.Hour >= Math.Max(hour + 1, 18) && local.Hour < 22;
                    var morning = local.Hour >= hour && local.Hour < 22 && push["lastMorning"]?.ToString() != day;
                    if (!morning && push["lastMorning"]?.ToString() == day && push["lastSecond"]?.ToString() != day)
                    {
                        // After the morning reminder: is a second swap due? (Checked every 5 minutes until it is sent or not needed.)
                        using var cts2 = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                        var t2 = await SellPlanTodayAsync(dir, update: true, cts2.Token);
                        var target = t2 is null ? 0 : Dec(t2["target"]);
                        var leftNow = t2 is null ? 0 : Dec(t2["leftToday"]);
                        var soldNow = t2 is null ? 0 : Dec(t2["soldToday"]);
                        var small = leftNow < Math.Max(1000m, target * 0.02m);
                        var oneSwap = t2 is null || t2["swaps"]?.GetValue<int>() != 2;
                        DateTime? last = DateTime.TryParse(t2?["lastSaleUtc"]?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var ls) ? ls : null;
                        var due = !oneSwap && !small && soldNow >= target * 0.3m && last is { } l && DateTime.UtcNow - l >= SecondSwapAfter;
                        if (oneSwap || small || due)
                        {
                            await PushGate.WaitAsync(cts2.Token);
                            try { push = LoadPush(dir); push["lastSecond"] = day; SavePush(dir, push); }
                            finally { PushGate.Release(); }
                        }
                        if (due)
                        {
                            var title = $"Time for your second swap: sell about {leftNow:N0} OPT";
                            var (usd2, pools2) = await OptSwapRouteAsync(Math.Round(leftNow / 100m) * 100m, cts2.Token);
                            var body = $"Your first swap today sold {soldNow:N0} OPT and the pools have had about {(DateTime.UtcNow - last!.Value).TotalHours:N0} hours to refill. Swap {leftNow:N0} OPT → USDC on Osmosis.{(pools2.Length > 0 ? $" Pools: {pools2}." : "")}{(usd2 is { } u2 ? $" Expect about ${u2:N2}." : "")}";
                            var r2 = await SendPushAsync(dir, new { title, body, url = "/page/opt-sell-plan", tag = "optic-sell-" + day }, cts2.Token);
                            Console.WriteLine($"Push reminder for {Path.GetFileName(dir)}: {title} ({string.Join(", ", r2.Select(x => JsonSerializer.Serialize(x)))})");
                        }
                    }
                    if (!morning && !(evening && push["lastEvening"]?.ToString() != day)) continue;
                    using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                    var today = await SellPlanTodayAsync(dir, update: true, cts.Token);
                    await PushGate.WaitAsync(cts.Token);
                    try
                    {
                        push = LoadPush(dir);
                        push[morning ? "lastMorning" : "lastEvening"] = day;
                        if (morning) push["lastEvening"] = local.Hour >= 18 ? day : push["lastEvening"]?.ToString();
                        SavePush(dir, push);
                    }
                    finally { PushGate.Release(); }
                    if (today is not null && await SellMessageAsync(today, !morning, cts.Token) is { } m)
                    {
                        var r = await SendPushAsync(dir, new { title = m.Title, body = m.Body, url = "/page/opt-sell-plan", tag = "optic-sell-" + day }, cts.Token);
                        Console.WriteLine($"Push reminder for {Path.GetFileName(dir)}: {m.Title} ({string.Join(", ", r.Select(x => JsonSerializer.Serialize(x)))})");
                    }
                }
                catch (Exception ex) { Console.WriteLine($"Push reminder for {Path.GetFileName(dir)} failed: {ex.Message}"); }
            }
            await TitnWatchTickAsync();
        }
        finally { Interlocked.Exchange(ref _pushRunning, 0); }
    }

    const string ServiceWorkerJs = """
        // OPTIC notification helper: shows the reminders the OPTIC server sends, and opens the page when one is tapped.
        self.addEventListener('install', () => self.skipWaiting());
        self.addEventListener('activate', (e) => e.waitUntil(self.clients.claim()));
        self.addEventListener('push', (e) => {
            let d = {};
            try { d = e.data ? e.data.json() : {}; } catch (x) { d = { body: e.data ? e.data.text() : '' }; }
            const show = self.registration.showNotification(d.title || 'OPTIC', {
                body: d.body || '', icon: '/images/optic-icon-128.png', badge: '/images/optic-icon-128.png',
                tag: d.tag || 'optic', renotify: true, data: { url: d.url || '/' },
            }).then(() => 'shown', (err) => 'not shown: ' + err);
            // Tell the server the message arrived on this device (and whether it could be shown), so delivery can be checked.
            e.waitUntil(show.then((result) => fetch('/api/push/ack', { method: 'POST', credentials: 'include', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ tag: d.tag || '', result }) }).catch(() => { })));
        });
        self.addEventListener('notificationclick', (e) => {
            e.notification.close();
            const url = new URL((e.notification.data && e.notification.data.url) || '/', self.location.origin).href;
            e.waitUntil(self.clients.matchAll({ type: 'window', includeUncontrolled: true }).then((list) => {
                for (const c of list) if (c.url.startsWith(self.location.origin) && 'focus' in c) return c.focus().then(() => c.navigate(url));
                return self.clients.openWindow(url);
            }));
        });
        """;

    static void MapWebPush(WebApplication app)
    {
        // The notification helper and the app manifest (for "Add to Home Screen", which iPhones need for notifications).
        app.MapGet("/sw.js", (HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "no-cache";
            return Results.Text(ServiceWorkerJs, "text/javascript; charset=utf-8");
        });
        app.MapGet("/manifest.webmanifest", () => Results.Text(JsonSerializer.Serialize(new
        {
            name = "OPTIC", short_name = "OPTIC", start_url = "/", scope = "/", display = "standalone",
            background_color = "#ffffff", theme_color = "#0078d4",
            icons = new object[]
            {
                new { src = "/images/optic-app-192.png", sizes = "192x192", type = "image/png", purpose = "any" },
                new { src = "/images/optic-app-512.png", sizes = "512x512", type = "image/png", purpose = "any" },
                new { src = "/images/optic-app-maskable-512.png", sizes = "512x512", type = "image/png", purpose = "maskable" },
                new { src = "/images/apple-touch-icon.png", sizes = "180x180", type = "image/png" },
            },
        }), "application/manifest+json"));

        // Today's selling reminder for the bell (from the saved Osmosis history; the Sell Plan page brings it up to date).
        app.MapGet("/api/sell-plan/today", async (HttpContext ctx) =>
        {
            try { return VJson((object?)await SellPlanTodayAsync(DashboardAuth.UserDataDir(ctx), ctx.Request.Query["fresh"] == "1", ctx.RequestAborted, ZoneOrNull(ctx.Request.Query["tz"].ToString())) ?? new { active = false }); }
            catch (Exception ex) when (ex is not OperationCanceledException) { return VError("Could not work out today's sale: " + ex.Message, 502); }
        });

        app.MapGet("/api/push/status", (HttpContext ctx) =>
        {
            var push = LoadPush(DashboardAuth.UserDataDir(ctx));
            var (titnOn, titnMin, titnLiq) = TitnAlertSettings(push);
            return VJson(new
            {
                publicKey = VapidKeys().Public,
                titn = new { on = titnOn, min = titnMin, liquidity = titnLiq },
                lastReceived = push["lastReceived"]?.ToString(),
                hour = push["hour"]?.GetValue<int>() ?? 8, timeZone = push["timeZone"]?.ToString(), evening = push["evening"]?.GetValue<bool>() == true,
                devices = (push["subs"] as JsonArray ?? new()).OfType<JsonObject>().Select(s => new { endpoint = s["endpoint"]?.ToString(), device = s["device"]?.ToString(), added = s["added"]?.ToString() }),
            });
        });

        // Body: { endpoint, keys: { p256dh, auth }, device, hour, timeZone, evening }.
        app.MapPost("/api/push/subscribe", async (HttpContext ctx) =>
        {
            JsonObject? b;
            try { b = await JsonNode.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted) as JsonObject; }
            catch (JsonException) { return VError("Not valid JSON.", 400); }
            var endpoint = b?["endpoint"]?.ToString() ?? "";
            var p256dh = b?["keys"]?["p256dh"]?.ToString() ?? "";
            var auth = b?["keys"]?["auth"]?.ToString() ?? "";
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var u) || u.Scheme != "https" || endpoint.Length > 1000) return VError("The browser gave no valid push address.", 400);
            try { if (WebPushCrypto.FromB64Url(p256dh).Length != 65 || WebPushCrypto.FromB64Url(auth).Length < 16) throw new FormatException(); }
            catch (FormatException) { return VError("The browser's push keys are not valid.", 400); }
            var dir = DashboardAuth.UserDataDir(ctx);
            await PushGate.WaitAsync(ctx.RequestAborted);
            try
            {
                var push = LoadPush(dir);
                var subs = push["subs"] as JsonArray ?? new();
                foreach (var old in subs.OfType<JsonObject>().Where(s => s["endpoint"]?.ToString() == endpoint).ToList()) subs.Remove(old);
                subs.Add(new JsonObject { ["endpoint"] = endpoint, ["p256dh"] = p256dh, ["auth"] = auth, ["device"] = (b?["device"]?.ToString() ?? "")[..Math.Min(80, (b?["device"]?.ToString() ?? "").Length)], ["added"] = DateTime.UtcNow });
                push["subs"] = subs;
                ApplyPushSettings(push, b!);
                SavePush(dir, push);
            }
            finally { PushGate.Release(); }
            return VJson(new { ok = true });
        });

        app.MapPost("/api/push/settings", async (HttpContext ctx) =>
        {
            JsonObject? b;
            try { b = await JsonNode.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted) as JsonObject; }
            catch (JsonException) { return VError("Not valid JSON.", 400); }
            if (b is null) return VError("Nothing to save.", 400);
            var dir = DashboardAuth.UserDataDir(ctx);
            await PushGate.WaitAsync(ctx.RequestAborted);
            try { var push = LoadPush(dir); ApplyPushSettings(push, b); SavePush(dir, push); }
            finally { PushGate.Release(); }
            return VJson(new { ok = true });
        });

        app.MapPost("/api/push/unsubscribe", async (HttpContext ctx) =>
        {
            JsonObject? b;
            try { b = await JsonNode.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted) as JsonObject; }
            catch (JsonException) { return VError("Not valid JSON.", 400); }
            var endpoint = b?["endpoint"]?.ToString();
            var dir = DashboardAuth.UserDataDir(ctx);
            await PushGate.WaitAsync(ctx.RequestAborted);
            try
            {
                var push = LoadPush(dir);
                var subs = push["subs"] as JsonArray ?? new();
                foreach (var old in subs.OfType<JsonObject>().Where(s => endpoint is null || s["endpoint"]?.ToString() == endpoint).ToList()) subs.Remove(old);
                SavePush(dir, push);
            }
            finally { PushGate.Release(); }
            return VJson(new { ok = true });
        });

        // A test: today's real reminder if there is one, otherwise a short "notifications work" message.
        // The notification helper on a device reports each message it received.
        app.MapPost("/api/push/ack", async (HttpContext ctx) =>
        {
            JsonObject? b = null;
            try { b = await JsonNode.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted) as JsonObject; } catch (JsonException) { }
            var dir = DashboardAuth.UserDataDir(ctx);
            var note = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC · {(b?["tag"]?.ToString() ?? "")[..Math.Min(60, (b?["tag"]?.ToString() ?? "").Length)]} · {(b?["result"]?.ToString() ?? "")[..Math.Min(120, (b?["result"]?.ToString() ?? "").Length)]} · {ctx.Request.Headers.UserAgent.ToString()[..Math.Min(60, ctx.Request.Headers.UserAgent.ToString().Length)]}";
            Console.WriteLine($"Push received by a device of {Path.GetFileName(dir)}: {note}");
            await PushGate.WaitAsync(ctx.RequestAborted);
            try { var push = LoadPush(dir); push["lastReceived"] = note; SavePush(dir, push); }
            finally { PushGate.Release(); }
            return VJson(new { ok = true });
        });

        app.MapPost("/api/push/test", async (HttpContext ctx) =>
        {
            var dir = DashboardAuth.UserDataDir(ctx);
            var push = LoadPush(dir);
            if ((push["subs"] as JsonArray ?? new()).Count == 0) return VError("No device has notifications turned on yet.", 400);
            JsonObject? today = null;
            try { today = await SellPlanTodayAsync(dir, update: false, ctx.RequestAborted); } catch (Exception ex) when (ex is not OperationCanceledException) { }
            var m = today is not null ? await SellMessageAsync(today, false, ctx.RequestAborted) : null;
            var hour = push["hour"]?.GetValue<int>() ?? 8;
            var at = new DateTime(2000, 1, 1, hour, 0, 0).ToString("h tt", CultureInfo.InvariantCulture);
            var msg = m is { } mm ? new { title = mm.Title, body = mm.Body, url = "/page/opt-sell-plan", tag = "optic-test" }
                : new { title = "OPTIC notifications are on", body = $"You'll get a reminder at {at} on days your OPT Sell Plan has OPT to sell.", url = "/page/opt-sell-plan", tag = "optic-test" };
            var results = await SendPushAsync(dir, msg, ctx.RequestAborted);
            Console.WriteLine($"Push test for {Path.GetFileName(dir)}: {msg.title} ({string.Join(", ", results.Select(x => JsonSerializer.Serialize(x)))})");
            return VJson(new { sent = msg.title, body = msg.body, results });
        });

        MapAdminPush(app);
        _pushTimer ??= new Timer(_ => _ = PushTickAsync(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5));
    }

    // Admin → Notifications: your devices, a test, and every user's settings.
    const string AdminPushHtml = """

                    <div class="card" id="ad-push" style="grid-column: 1 / -1;" hidden>
                        <h2>Notifications</h2>
                        <div class="card-content">
                            <p class="ad-note">Push notifications for the OPT Sell Plan (turned on per device on that page). A test goes to your own devices: today's real reminder when there is one, otherwise a short "notifications work" message.</p>
                            <div class="ad-row" style="align-items: center; margin-bottom: 8px;">
                                <button type="button" id="ad-push-test">Send a test to my devices</button>
                                <span id="ad-push-status" class="ad-note" style="margin: 0;"></span>
                            </div>
                            <div class="data-table-wrap pnl-table-wrap" style="margin-bottom: 12px;">
                                <table class="data-table">
                                    <thead><tr><th>User</th><th style="text-align: right;">Devices</th><th>Reminder</th><th>Time zone</th><th>Last sent</th><th>Last received by a device</th></tr></thead>
                                    <tbody id="ad-push-users"><tr><td colspan="6" class="ad-note">Loading…</td></tr></tbody>
                                </table>
                            </div>
                            <strong>My devices</strong>
                            <div class="data-table-wrap pnl-table-wrap">
                                <table class="data-table">
                                    <thead><tr><th>Device</th><th>Push service</th><th>Turned on</th></tr></thead>
                                    <tbody id="ad-push-devices"></tbody>
                                </table>
                            </div>
                        </div>
                    </div>
                    <script>
                        (function () {
                            const $ = (id) => document.getElementById(id);
                            const esc = (v) => String(v ?? '').replace(/[&<>"']/g, ch => '&#' + ch.charCodeAt(0) + ';');
                            const when = (v) => v ? new Date(v).toLocaleString('en-US', { month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' }) : '–';
                            const hour = (h) => new Date(Date.UTC(2000, 0, 1, h)).toLocaleTimeString('en-US', { hour: 'numeric', timeZone: 'UTC' });
                            window.adPushLoad = async () => {
                                try {
                                    const [a, me] = await Promise.all([fetch('/api/admin/push').then(r => r.json()), fetch('/api/push/status').then(r => r.json())]);
                                    $('ad-push-users').innerHTML = (a.users || []).map(u => `<tr><td>${esc(u.user)}</td><td class="amount-cell">${u.devices}</td><td>${hour(u.hour)}${u.evening ? ' + 6 PM' : ''}</td><td>${esc(u.timeZone)}</td><td>${esc(u.lastMorning || '–')}</td><td style="font-size: 12px;">${esc(u.lastReceived || '–')}</td></tr>`).join('')
                                        || '<tr><td colspan="6" class="ad-note">No one has turned notifications on yet.</td></tr>';
                                    $('ad-push-devices').innerHTML = (me.devices || []).map(d => `<tr><td>${esc(d.device || '–')}</td><td>${esc(new URL(d.endpoint).host)}</td><td>${when(d.added)}</td></tr>`).join('')
                                        || '<tr><td colspan="3" class="ad-note">None. Turn notifications on from Market → OPT Sell Plan on each phone or computer.</td></tr>';
                                    $('ad-push-test').disabled = !(me.devices || []).length;
                                } catch (e) { $('ad-push-status').textContent = 'Could not load: ' + e.message; }
                            };
                            $('ad-push-test').addEventListener('click', async () => {
                                $('ad-push-test').disabled = true;
                                $('ad-push-status').textContent = 'Sending…';
                                const r = await fetch('/api/push/test', { method: 'POST' });
                                const j = await r.json().catch(() => ({}));
                                const res = j.results || [];
                                $('ad-push-status').textContent = !r.ok ? 'Could not send: ' + (j.error || 'HTTP ' + r.status)
                                    : `Sent "${j.sent}" to ${res.filter(x => x.ok).length} of ${res.length} device${res.length === 1 ? '' : 's'}; it should appear within a few seconds.` + res.filter(x => !x.ok).map(x => ` ${x.device || x.service}: ${x.removed ? 'no longer subscribed, removed' : 'failed (' + x.status + ' ' + (x.error || '') + ')'}.`).join('');
                                setTimeout(window.adPushLoad, 4000);
                            });
                        })();
                    </script>
""";

    static void MapAdminPush(WebApplication app)
    {
        app.MapGet("/api/admin/push", () =>
        {
            var usersDir = Path.Combine(Environment.CurrentDirectory, "odata", "users");
            var users = new List<object>();
            if (Directory.Exists(usersDir))
                foreach (var dir in Directory.GetDirectories(usersDir).Where(d => File.Exists(PushPath(d))))
                {
                    var p = LoadPush(dir);
                    users.Add(new
                    {
                        user = Path.GetFileName(dir), devices = (p["subs"] as JsonArray ?? new()).Count,
                        hour = p["hour"]?.GetValue<int>() ?? 8, timeZone = p["timeZone"]?.ToString(), evening = p["evening"]?.GetValue<bool>() == true,
                        lastMorning = p["lastMorning"]?.ToString(), lastReceived = p["lastReceived"]?.ToString(),
                    });
                }
            return VJson(new { users });
        });
    }

    static void ApplyPushSettings(JsonObject push, JsonObject b)
    {
        if (b["hour"] is JsonValue hv && hv.TryGetValue<int>(out var h) && h is >= 0 and <= 21) push["hour"] = h;
        if (b["timeZone"]?.ToString() is { Length: > 0 and <= 64 } tz)
        {
            try { TimeZoneInfo.FindSystemTimeZoneById(tz); push["timeZone"] = tz; } catch (Exception) { }
        }
        if (b["evening"] is JsonValue ev && ev.TryGetValue<bool>(out var e)) push["evening"] = e;
    }
}

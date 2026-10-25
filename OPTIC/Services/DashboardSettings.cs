using System.Diagnostics;
using System.IO.Compression;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace OPTIC.Services;

/// <summary>
/// Per-user settings (My Settings page) and system-wide settings (Admin page).
/// Each user's data lives in odata/users/&lt;username&gt;/; system-wide data in odata/system/ and optic.conf.
/// </summary>
public static class DashboardSettings
{
    static readonly object FileLock = new();
    static readonly Regex AddressPattern = new("^optio1[02-9ac-hj-np-z]{38,58}$");
    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    static string SystemDir => Path.Combine(Environment.CurrentDirectory, "odata", "system");
    static string ExchangesPath => Path.Combine(SystemDir, "exchanges.json");
    static string ConfigPath => Path.Combine(Environment.CurrentDirectory, "optic.conf");

    public sealed record Wallet(string a, string l, bool p);
    public sealed record Exchange(string a, string n);

    static readonly Exchange[] DefaultExchanges = { new("optio12e3lctqkpg2rrkvywd6596at358dr50cpmcr5x", "Coinstore") };

    /// <summary>optic.conf keys shown on the Admin page, with a check for each value.</summary>
    static readonly (string key, string label, Func<string, string?> check)[] ConfigKeys =
    {
        ("addr", "Default address", v => AddressPattern.IsMatch(v) ? null : "must be an optio1... address"),
        ("denom", "Denom", v => Regex.IsMatch(v, "^[A-Za-z0-9/]{1,32}$") ? null : "letters and digits only"),
        ("tz", "Time zone", v => { try { TimeZoneInfo.FindSystemTimeZoneById(v); return null; } catch { return "unknown time zone"; } }),
        ("grpc", "gRPC endpoint", v => Regex.IsMatch(v, @"^[A-Za-z0-9.\-]+:\d{1,5}$") ? null : "host:port, e.g. 127.0.0.1:9090"),
        ("lcd", "LCD (REST) endpoint", v => Uri.TryCreate(v, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https") ? null : "an http:// or https:// URL"),
        ("lookbackDays", "Look back (days)", v => PositiveInt(v)),
        ("pageLimit", "Page size", v => PositiveInt(v)),
        ("maxPages", "Max pages", v => PositiveInt(v)),
        ("webHost", "Dashboard listens on", v => v == "0.0.0.0" || v == "127.0.0.1" || v == "localhost" || System.Net.IPAddress.TryParse(v, out _) ? null : "0.0.0.0 (all networks), 127.0.0.1 (this computer only) or an IP address"),
        ("webPort", "HTTP port", v => int.TryParse(v, out var n) && n is >= 0 and < 65536 ? null : "0 (off) to 65535"),
        ("httpsPort", "HTTPS port", v => int.TryParse(v, out var n) && n is >= 0 and < 65536 ? null : "0 (off) to 65535"),
        ("httpsRedirect", "Send other devices to HTTPS", v => v is "true" or "false" ? null : "true or false"),
    };

    static string? PositiveInt(string v) => int.TryParse(v, out var n) && n > 0 ? null : "a whole number above 0";

    static JsonNode? ReadJson(string path)
    {
        lock (FileLock)
            return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) : null;
    }

    static void WriteJson(string path, object value)
    {
        lock (FileLock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(value, Indented));
        }
    }

    static string WalletsPath(HttpContext ctx) => Path.Combine(DashboardAuth.UserDataDir(ctx), "wallets.json");
    static string UserSettingsPath(HttpContext ctx) => Path.Combine(DashboardAuth.UserDataDir(ctx), "settings.json");

    static readonly System.Text.RegularExpressions.Regex OsmoAddressPattern = new("^osmo1[02-9ac-hj-np-z]{38,58}$", System.Text.RegularExpressions.RegexOptions.Compiled);
    // Ethereum-style addresses (one address per key on Ethereum, Base and Arbitrum); saved for reference, not read by the tax report.
    static readonly System.Text.RegularExpressions.Regex EvmAddressPattern = new("^0x[0-9a-f]{40}$", System.Text.RegularExpressions.RegexOptions.Compiled);
    static readonly System.Text.RegularExpressions.Regex SolanaAddressPattern = new("^[1-9A-HJ-NP-Za-km-z]{32,44}$", System.Text.RegularExpressions.RegexOptions.Compiled);
    static readonly System.Text.RegularExpressions.Regex GonkaAddressPattern = new("^gonka1[02-9ac-hj-np-z]{38,58}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    static List<Wallet> CleanWallets(IEnumerable<Wallet?> items)
    {
        var seen = new HashSet<string>();
        var clean = new List<Wallet>();
        foreach (var w in items)
        {
            // Solana addresses are case-sensitive and kept as entered; every other address is lowercase.
            var raw = (w?.a ?? "").Trim();
            var a = raw.ToLowerInvariant();
            if (!(AddressPattern.IsMatch(a) || OsmoAddressPattern.IsMatch(a) || EvmAddressPattern.IsMatch(a) || GonkaAddressPattern.IsMatch(a)))
            {
                if (!SolanaAddressPattern.IsMatch(raw)) continue;
                a = raw;
            }
            if (!seen.Add(a)) continue;
            var l = (w!.l ?? "").Trim();
            clean.Add(new Wallet(a, l.Length > 40 ? l[..40] : l, w.p));
        }
        // The primary wallet is always an Optio wallet; Osmosis accounts are extra wallets for the tax report.
        var primary = clean.FindIndex(w => w.p && AddressPattern.IsMatch(w.a));
        if (primary < 0) primary = clean.FindIndex(w => AddressPattern.IsMatch(w.a));
        return clean.Select((w, i) => w with { p = i == primary }).ToList();
    }

    static List<Exchange> LoadExchanges() =>
        ReadJson(ExchangesPath)?.Deserialize<List<Exchange>>() ?? DefaultExchanges.ToList();

    /// <summary>What every page needs about the signed-in user, embedded in the page so it is available synchronously.</summary>
    public static string BootScript(HttpContext ctx)
    {
        var boot = new
        {
            name = DashboardAuth.UserName(ctx),
            isAdmin = DashboardAuth.IsAdmin(ctx),
            wallets = ReadJson(WalletsPath(ctx)) ?? new JsonArray(),
            hasWallets = File.Exists(WalletsPath(ctx)),
            settings = ReadJson(UserSettingsPath(ctx)) ?? new JsonObject(),
            exchanges = LoadExchanges(),
            hasExchanges = File.Exists(ExchangesPath),
        };
        // "</" is escaped so a stored value can never close the script tag.
        var json = JsonSerializer.Serialize(boot).Replace("</", "<\\/");
        return $"\n    <script>window.opticUser = {json};\n{BootHelpers}</script>\n";
    }

    // Shared by every page: window.opticSettings saves to the signed-in account, window.opticPrefs holds small preferences.
    const string BootHelpers = """
// Settings saved to the signed-in user's account; window.opticUser is embedded in every page.
window.opticSettings = window.opticSettings || (function () {
    const U = window.opticUser = window.opticUser || { wallets: [], settings: {}, exchanges: [] };
    if (!U.settings || typeof U.settings !== 'object' || Array.isArray(U.settings)) U.settings = {};
    const post = async (url, body) => {
        const r = await fetch(url, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body), keepalive: true });
        const j = await r.json().catch(() => ({}));
        if (!r.ok) throw new Error(j.error || ('HTTP ' + r.status));
        return j;
    };
    return {
        post,
        get: () => U.settings,
        // Changes one top-level key and saves the whole settings object.
        set: (key, value) => { U.settings[key] = value; return post('/api/me/settings', U.settings); },
    };
})();
window.opticPrefs = window.opticPrefs || {
    get: (k) => (window.opticSettings.get().prefs || {})[k],
    set: (k, v) => window.opticSettings.set('prefs', { ...(window.opticSettings.get().prefs || {}), [k]: v }),
};
""";

    static async Task<T?> ReadBody<T>(HttpContext ctx)
    {
        try { return await JsonSerializer.DeserializeAsync<T>(ctx.Request.Body, cancellationToken: ctx.RequestAborted); }
        catch (JsonException) { return default; }
    }

    static IResult Bad(string error) => Results.Json(new { error }, statusCode: 400);

    public static void Map(WebApplication app, string host, int port)
    {
        // ---------- the signed-in user ----------
        app.MapGet("/api/me", (HttpContext ctx) =>
        {
            return Results.Json(new { name = DashboardAuth.UserName(ctx), isAdmin = DashboardAuth.IsAdmin(ctx) });
        });

        app.MapPost("/api/me/password", async (HttpContext ctx) =>
        {
            var body = await ReadBody<Dictionary<string, string>>(ctx);
            var name = DashboardAuth.CurrentUser(ctx);
            if (name == null) return Bad("Sign in first.");
            if (body == null || !body.TryGetValue("current", out var current) || !body.TryGetValue("next", out var next))
                return Bad("Enter your current and new password.");
            if (!DashboardAuth.VerifyPassword(name, current)) return Bad("Your current password is wrong.");
            try { DashboardAuth.SetUser(name, next); }
            catch (ArgumentException ex) { return Bad(ex.Message); }
            return Results.Json(new { ok = true });
        });

        app.MapGet("/api/me/wallets", (HttpContext ctx) =>
        {
            return Results.Json(ReadJson(WalletsPath(ctx)) ?? new JsonArray());
        });

        app.MapPost("/api/me/wallets", async (HttpContext ctx) =>
        {
            var items = await ReadBody<List<Wallet?>>(ctx);
            if (items == null) return Bad("Expected a list of wallets.");
            var clean = CleanWallets(items);
            WriteJson(WalletsPath(ctx), clean);
            return Results.Json(clean);
        });

        // Free-form per-user preferences: { prefs: {...}, exchangeMemos: { address: memo } }.
        app.MapGet("/api/me/settings", (HttpContext ctx) =>
        {
            return Results.Json(ReadJson(UserSettingsPath(ctx)) ?? new JsonObject());
        });

        app.MapPost("/api/me/settings", async (HttpContext ctx) =>
        {
            if (ctx.Request.ContentLength > 64 * 1024) return Bad("Settings are too large.");
            JsonNode? node;
            try { node = await JsonNode.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted); }
            catch (JsonException) { return Bad("Expected a JSON object."); }
            if (node is not JsonObject obj) return Bad("Expected a JSON object.");
            WriteJson(UserSettingsPath(ctx), obj);
            return Results.Json(obj);
        });

        // What the user has saved, for the My Settings page.
        app.MapGet("/api/me/summary", (HttpContext ctx) =>
        {
            var dir = DashboardAuth.UserDataDir(ctx);
            int Count(string file) => ReadJson(Path.Combine(dir, file)) switch { JsonObject o => o.Count, JsonArray a => a.Count, _ => 0 };
            var labelsDir = Path.Combine(dir, "tax-labels");
            var taxWallets = Directory.Exists(labelsDir)
                ? Directory.GetFiles(labelsDir, "*.json").Select(f =>
                {
                    var o = ReadJson(f) as JsonObject;
                    return new
                    {
                        address = Path.GetFileNameWithoutExtension(f),
                        transfers = o?.Count(kv => !kv.Key.StartsWith('_')) ?? 0,
                        expenses = (o?["_expenses"] as JsonArray)?.Count ?? 0,
                    };
                }).ToList()
                : new();
            return Results.Json(new
            {
                folder = dir,
                wallets = Count("wallets.json"),
                namedAddresses = Count("address-book.json"),
                giftWallets = Count("gift-senders.json"),
                taxWallets,
            });
        });

        // Everything this user has saved, as a zip.
        app.MapGet("/api/me/export", (HttpContext ctx) =>
        {
            var dir = DashboardAuth.UserDataDir(ctx);
            var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                if (Directory.Exists(dir))
                    foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                        zip.CreateEntryFromFile(file, Path.GetRelativePath(dir, file).Replace('\\', '/'));
            }
            return Results.File(ms.ToArray(), "application/zip", $"optic-{DashboardAuth.UserName(ctx)}-{DateTime.Now:yyyy-MM-dd}.zip");
        });

        // ---------- system-wide: exchange list (everyone reads, admins edit) ----------
        app.MapGet("/api/system/exchanges", () => Results.Json(LoadExchanges()));

        app.MapPost("/api/admin/exchanges", async (HttpContext ctx) =>
        {
            var items = await ReadBody<List<Exchange?>>(ctx);
            if (items == null) return Bad("Expected a list of exchanges.");
            var seen = new HashSet<string>();
            var clean = new List<Exchange>();
            foreach (var x in items)
            {
                var a = (x?.a ?? "").Trim().ToLowerInvariant();
                if (!AddressPattern.IsMatch(a) || !seen.Add(a)) continue;
                var n = (x!.n ?? "").Trim();
                clean.Add(new Exchange(a, n.Length == 0 ? "Exchange" : n.Length > 30 ? n[..30] : n));
            }
            WriteJson(ExchangesPath, clean);
            return Results.Json(clean);
        });

        // ---------- admin: users ----------
        app.MapGet("/api/admin/users", (HttpContext ctx) =>
        {
            var me = DashboardAuth.CurrentUser(ctx);
            var logins = DashboardAuth.LoginSummaries();
            return Results.Json(DashboardAuth.GetUsers().Select(u =>
            {
                logins.TryGetValue(u.Username, out var l);
                return new
                {
                    username = u.Username,
                    isAdmin = u.IsAdmin,
                    isYou = string.Equals(u.Username, me, StringComparison.OrdinalIgnoreCase),
                    lastSignIn = l?.LastSignIn,
                    lastSignInIp = l?.LastSignInIp,
                    signIns = l?.SignIns ?? 0,
                    failedLast30Days = l?.FailedLast30Days ?? 0,
                };
            }));
        });

        // Sign-in history for one user (newest first), or for names that are not users ("*unknown*").
        app.MapGet("/api/admin/login-log", (HttpContext ctx) =>
        {
            var user = ctx.Request.Query["user"].ToString().Trim();
            if (user.Length == 0) return Results.Json(new { error = "Choose a user." }, statusCode: 400);
            return Results.Json(DashboardAuth.LoginHistory(user).Select(e => new { utc = e.Utc, user = e.User, result = e.Result, ip = e.Ip, userAgent = e.UserAgent }));
        });

        // Addresses blocked after five failed sign-ins, and addresses with failed sign-ins so far.
        app.MapGet("/api/admin/blocked-ips", () =>
            Results.Json(DashboardAuth.IpStatus().Select(x => new
            {
                ip = x.Ip, blocked = x.Record.Blocked, blockedUtc = x.Record.BlockedUtc, failures = x.Record.Failures,
                firstFailureUtc = x.Record.FirstFailureUtc, lastFailureUtc = x.Record.LastFailureUtc, lastUser = x.Record.LastUser,
            })));

        app.MapPost("/api/admin/blocked-ips/unblock", async (HttpContext ctx) =>
        {
            var body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
            var ip = body.TryGetProperty("ip", out var v) ? v.GetString() ?? "" : "";
            if (ip.Trim().Length == 0) return Results.Json(new { error = "Give the address to unblock." }, statusCode: 400);
            var ok = DashboardAuth.UnblockIp(ip, DashboardAuth.UserName(ctx), ctx);
            return ok ? Results.Json(new { unblocked = ip.Trim() }) : Results.Json(new { error = "That address is not on the list." }, statusCode: 404);
        });

        app.MapPost("/api/admin/users", async (HttpContext ctx) =>
        {
            var body = await ReadBody<JsonObject>(ctx);
            var username = body?["username"]?.GetValue<string>()?.Trim() ?? "";
            var password = body?["password"]?.GetValue<string>() ?? "";
            var isAdmin = body?["isAdmin"]?.GetValue<bool>() ?? false;
            var me = DashboardAuth.CurrentUser(ctx);
            var isMe = string.Equals(username, me, StringComparison.OrdinalIgnoreCase);
            if (isMe && !isAdmin) return Bad("You cannot take away your own admin access.");
            var exists = DashboardAuth.GetUsers().Any(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase));
            try
            {
                if (password.Length == 0)
                {
                    if (!exists) return Bad("Enter a password for the new user.");
                    DashboardAuth.SetAdmin(username, isAdmin);
                }
                else
                {
                    DashboardAuth.SetUser(username, password, isAdmin);
                }
            }
            catch (ArgumentException ex) { return Bad(ex.Message); }
            return Results.Json(new { ok = true, created = !exists });
        });

        app.MapPost("/api/admin/users/remove", async (HttpContext ctx) =>
        {
            var body = await ReadBody<Dictionary<string, string>>(ctx);
            var username = body?.GetValueOrDefault("username")?.Trim() ?? "";
            if (string.Equals(username, DashboardAuth.CurrentUser(ctx), StringComparison.OrdinalIgnoreCase))
                return Bad("You cannot remove yourself.");
            // The user's saved data in odata/users/<name> is kept; adding the user again brings it back.
            return DashboardAuth.RemoveUser(username) ? Results.Json(new { ok = true }) : Bad("No such user.");
        });

        // ---------- admin: optic.conf ----------
        app.MapGet("/api/admin/config", () =>
        {
            var values = ReadConfig();
            return Results.Json(new
            {
                path = ConfigPath,
                keys = ConfigKeys.Select(k => new { k.key, k.label, value = values.GetValueOrDefault(k.key) ?? "" }),
            });
        });

        app.MapPost("/api/admin/config", async (HttpContext ctx) =>
        {
            var body = await ReadBody<Dictionary<string, string>>(ctx);
            if (body == null) return Bad("Expected an object of setting: value.");
            var updates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, label, check) in ConfigKeys)
            {
                if (!body.TryGetValue(key, out var raw)) continue;
                var v = (raw ?? "").Trim();
                if (v.Length > 0 && check(v) is { } problem) return Bad($"{label}: {problem}.");
                updates[key] = v;
            }
            WriteConfig(updates);
            return Results.Json(new { ok = true });
        });

        // ---------- admin: web server and network ----------
        app.MapGet("/api/admin/server", async (HttpContext ctx) =>
        {
            var addresses = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses
                    .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(a => new { adapter = n.Name, ip = a.Address.ToString() }))
                .ToList();
            var httpPort = DashboardHttps.HttpPort;
            var portPart = httpPort == 80 ? "" : ":" + httpPort;
            var allNetworks = host == "0.0.0.0" || host == "*" || host == "+";
            var dbPath = Path.Combine(Environment.CurrentDirectory, "odata", "optic.db");
            return Results.Json(new
            {
                host,
                port,
                allNetworks,
                httpPort,
                urls = httpPort == 0 ? new List<string>()
                    : allNetworks && port == 0 ? new List<string> { $"http://127.0.0.1{portPart}/" }
                    : allNetworks
                    ? addresses.Select(a => $"http://{a.ip}{portPart}/").Prepend($"http://127.0.0.1{portPart}/").ToList()
                    : new List<string> { $"http://{host}{portPart}/" },
                httpsPort = DashboardHttps.Current.Enabled ? DashboardHttps.Current.Port : (int?)null,
                httpsUrls = !DashboardHttps.Current.Enabled ? new List<string>()
                    : (allNetworks ? addresses.Select(a => a.ip).Prepend("localhost") : new[] { host }.AsEnumerable())
                        .Select(h => $"https://{h}{(DashboardHttps.Current.Port == 443 ? "" : ":" + DashboardHttps.Current.Port)}/").ToList(),
                addresses,
                networkProfiles = await NetworkProfilesAsync(),
                dataFolder = Path.Combine(Environment.CurrentDirectory, "odata"),
                databaseMb = File.Exists(dbPath) ? Math.Round(new FileInfo(dbPath).Length / 1048576.0, 1) : 0,
                syncMinutes = 5,
                machine = Environment.MachineName,
            });
        });
    }

    static Dictionary<string, string> ReadConfig()
    {
        var cfg = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(ConfigPath)) return cfg;
        foreach (var raw in File.ReadLines(ConfigPath))
        {
            var line = raw.Trim();
            var idx = line.IndexOf('=');
            if (line.StartsWith('#') || idx <= 0) continue;
            cfg[line[..idx].Trim()] = line[(idx + 1)..].Trim();
        }
        return cfg;
    }

    /// <summary>Updates keys in place, keeping comments, blank lines and keys this page does not know; an empty value removes the key.</summary>
    static void WriteConfig(Dictionary<string, string> updates)
    {
        lock (FileLock)
        {
            var lines = File.Exists(ConfigPath) ? File.ReadAllLines(ConfigPath).ToList() : new List<string>();
            var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = lines.Count - 1; i >= 0; i--)
            {
                var line = lines[i].Trim();
                var idx = line.IndexOf('=');
                if (line.StartsWith('#') || idx <= 0) continue;
                var key = line[..idx].Trim();
                if (!updates.TryGetValue(key, out var value)) continue;
                if (value.Length == 0 || done.Contains(key)) lines.RemoveAt(i);
                else { lines[i] = $"{key}={value}"; done.Add(key); }
            }
            var added = updates.Where(u => u.Value.Length > 0 && !done.Contains(u.Key)).ToList();
            if (added.Count > 0 && lines.Count > 0 && lines[^1].Trim().Length > 0) lines.Add("");
            lines.AddRange(added.Select(u => $"{u.Key}={u.Value}"));
            File.WriteAllLines(ConfigPath, lines);
        }
    }

    /// <summary>Windows network category per adapter (Public, Private, DomainAuthenticated); empty if it cannot be read.</summary>
    static async Task<List<object>> NetworkProfilesAsync()
    {
        if (!OperatingSystem.IsWindows()) return new();
        try
        {
            var psi = new ProcessStartInfo("powershell", "-NoProfile -NonInteractive -Command \"Get-NetConnectionProfile | Select-Object InterfaceAlias, @{n='Category';e={$_.NetworkCategory.ToString()}} | ConvertTo-Json -Compress\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi)!;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var output = await proc.StandardOutput.ReadToEndAsync(cts.Token);
            await proc.WaitForExitAsync(cts.Token);
            var node = output.Trim().Length == 0 ? null : JsonNode.Parse(output);
            var items = node is JsonArray arr ? arr.ToList() : new List<JsonNode?> { node };
            return items.Where(n => n != null)
                .Select(n => (object)new { adapter = n!["InterfaceAlias"]?.ToString(), category = n["Category"]?.ToString() })
                .ToList();
        }
        catch
        {
            return new();
        }
    }
}

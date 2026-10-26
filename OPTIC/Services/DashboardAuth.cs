using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace OPTIC.Services;

/// <summary>
/// Username and password sign-in for the web dashboard.
/// Users live in odata/auth/users.json as salted PBKDF2-SHA256 hashes; sessions are HMAC-signed cookies
/// keyed by odata/auth/session.key, so they survive restarts. With no users configured the dashboard
/// is only reachable from this computer.
/// </summary>
public static class DashboardAuth
{
    const string CookieName = "optic_session";
    // Admin page, the System menu (node status, validators, data sync) and the retired Advanced pages.
    static readonly HashSet<string> AdminPages = new(StringComparer.OrdinalIgnoreCase)
    {
        "/page/admin", "/page/status", "/page/validators", "/page/sync", "/page/multisend", "/page/cmc", "/page/custom",
    };
    const int Iterations = 210_000;
    static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(14);
    // Five failed sign-ins in a row from one IP address block it until an admin unblocks it.
    const int MaxFailures = 5;

    static readonly object FileLock = new();

    sealed class UserRecord
    {
        public string Username { get; set; } = "";
        public string Salt { get; set; } = "";
        public string Hash { get; set; } = "";
        public int Iterations { get; set; }
        public bool IsAdmin { get; set; }
    }

    public sealed record UserInfo(string Username, bool IsAdmin);

    static readonly System.Text.RegularExpressions.Regex UsernamePattern = new("^[A-Za-z0-9._-]{1,40}$");

    static string AuthDir => Path.Combine(Environment.CurrentDirectory, "odata", "auth");
    static string UsersPath => Path.Combine(AuthDir, "users.json");
    static string KeyPath => Path.Combine(AuthDir, "session.key");

    static List<UserRecord> LoadUsers()
    {
        lock (FileLock)
        {
            if (!File.Exists(UsersPath)) return new();
            return JsonSerializer.Deserialize<List<UserRecord>>(File.ReadAllText(UsersPath)) ?? new();
        }
    }

    static void SaveUsers(List<UserRecord> users)
    {
        lock (FileLock)
        {
            Directory.CreateDirectory(AuthDir);
            File.WriteAllText(UsersPath, JsonSerializer.Serialize(users, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    static byte[]? _key;
    static byte[] SessionKey()
    {
        if (_key != null) return _key;
        lock (FileLock)
        {
            if (File.Exists(KeyPath))
            {
                _key = Convert.FromBase64String(File.ReadAllText(KeyPath).Trim());
            }
            else
            {
                Directory.CreateDirectory(AuthDir);
                _key = RandomNumberGenerator.GetBytes(32);
                File.WriteAllText(KeyPath, Convert.ToBase64String(_key));
            }
            return _key;
        }
    }

    static byte[] HashPassword(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);

    /// <summary>
    /// Adds a user or replaces that user's password. isAdmin null keeps an existing user's role; the first user is always an admin.
    /// Returns true if the user already existed.
    /// </summary>
    public static bool SetUser(string username, string password, bool? isAdmin = null)
    {
        username = username.Trim();
        if (!UsernamePattern.IsMatch(username)) throw new ArgumentException("Usernames are 1 to 40 letters, digits, dots, dashes or underscores.");
        if (password.Length < 8) throw new ArgumentException("Password must be at least 8 characters.");
        var salt = RandomNumberGenerator.GetBytes(16);
        var users = LoadUsers();
        var old = users.FirstOrDefault(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase));
        if (old != null) users.Remove(old);
        users.Add(new UserRecord
        {
            Username = old?.Username ?? username,
            Salt = Convert.ToBase64String(salt),
            Hash = Convert.ToBase64String(HashPassword(password, salt, Iterations)),
            Iterations = Iterations,
            IsAdmin = isAdmin ?? old?.IsAdmin ?? users.Count == 0,
        });
        SaveUsers(users);
        return old != null;
    }

    public static bool SetAdmin(string username, bool isAdmin)
    {
        var users = LoadUsers();
        var user = users.FirstOrDefault(u => string.Equals(u.Username, username.Trim(), StringComparison.OrdinalIgnoreCase));
        if (user == null) return false;
        user.IsAdmin = isAdmin;
        SaveUsers(users);
        return true;
    }

    public static bool VerifyPassword(string username, string password) => CheckPassword(username, password) != null;

    public static IReadOnlyList<UserInfo> GetUsers() =>
        LoadUsers().OrderBy(u => u.Username, StringComparer.OrdinalIgnoreCase).Select(u => new UserInfo(u.Username, u.IsAdmin)).ToList();

    /// <summary>The signed-in user's name; "local" when no users are set up (only this computer can connect then).</summary>
    public static string UserName(HttpContext ctx) => CurrentUser(ctx) ?? "local";

    public static bool IsAdmin(HttpContext ctx)
    {
        var name = CurrentUser(ctx);
        if (name == null) return LoadUsers().Count == 0 && IsLoopback(ctx);
        return LoadUsers().Any(u => u.IsAdmin && string.Equals(u.Username, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Folder that holds this user's own data: wallets, settings, tax labels, named addresses, gift wallets.</summary>
    public static string UserDataDir(HttpContext ctx) =>
        Path.Combine(Environment.CurrentDirectory, "odata", "users", UserName(ctx).ToLowerInvariant());

    public static bool RemoveUser(string username)
    {
        var users = LoadUsers();
        var removed = users.RemoveAll(u => string.Equals(u.Username, username.Trim(), StringComparison.OrdinalIgnoreCase)) > 0;
        if (removed) SaveUsers(users);
        return removed;
    }

    static string? CheckPassword(string username, string password)
    {
        var user = LoadUsers().FirstOrDefault(u => string.Equals(u.Username, username.Trim(), StringComparison.OrdinalIgnoreCase));
        // Hash even for unknown users so the response time does not reveal which usernames exist.
        var salt = user != null ? Convert.FromBase64String(user.Salt) : new byte[16];
        var actual = HashPassword(password, salt, user?.Iterations ?? Iterations);
        if (user == null) return null;
        return CryptographicOperations.FixedTimeEquals(actual, Convert.FromBase64String(user.Hash)) ? user.Username : null;
    }

    static string Sign(string payload) =>
        Convert.ToBase64String(HMACSHA256.HashData(SessionKey(), Encoding.UTF8.GetBytes(payload)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    static string CreateSessionCookie(string username)
    {
        var expires = DateTimeOffset.UtcNow.Add(SessionLifetime).ToUnixTimeSeconds();
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}|{expires}"));
        return payload + "." + Sign(payload);
    }

    /// <summary>The signed-in username, or null.</summary>
    public static string? CurrentUser(HttpContext ctx)
    {
        if (!ctx.Request.Cookies.TryGetValue(CookieName, out var cookie) || string.IsNullOrEmpty(cookie)) return null;
        var dot = cookie.LastIndexOf('.');
        if (dot <= 0) return null;
        var payload = cookie[..dot];
        var expected = Encoding.ASCII.GetBytes(Sign(payload));
        if (!CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(cookie[(dot + 1)..]))) return null;
        string text;
        try { text = Encoding.UTF8.GetString(Convert.FromBase64String(payload)); }
        catch (FormatException) { return null; }
        var bar = text.LastIndexOf('|');
        if (bar <= 0 || !long.TryParse(text[(bar + 1)..], out var exp) || DateTimeOffset.UtcNow.ToUnixTimeSeconds() > exp) return null;
        var name = text[..bar];
        // A removed user's cookie stops working.
        return LoadUsers().Any(u => string.Equals(u.Username, name, StringComparison.OrdinalIgnoreCase)) ? name : null;
    }

    static bool IsLoopback(HttpContext ctx) =>
        ctx.Connection.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip);

    static string ClientKey(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString() ?? "?";

    // ----- IP guard: failed sign-ins per IP address, and blocked addresses, kept in odata/auth/ip-guard.json.
    // The OPTIC computer itself (loopback) is never blocked, so an admin can always reach the Admin page there.

    public sealed class IpRecord
    {
        public int Failures { get; set; }
        public DateTime? FirstFailureUtc { get; set; }
        public DateTime? LastFailureUtc { get; set; }
        public string? LastUser { get; set; }
        public bool Blocked { get; set; }
        public DateTime? BlockedUtc { get; set; }
    }

    static string IpGuardPath => Path.Combine(AuthDir, "ip-guard.json");
    static readonly object IpLock = new();
    static Dictionary<string, IpRecord>? _ipGuard;

    static Dictionary<string, IpRecord> IpGuard()
    {
        if (_ipGuard != null) return _ipGuard;
        try { _ipGuard = File.Exists(IpGuardPath) ? JsonSerializer.Deserialize<Dictionary<string, IpRecord>>(File.ReadAllText(IpGuardPath)) : null; }
        catch (JsonException) { _ipGuard = null; }
        return _ipGuard ??= new Dictionary<string, IpRecord>(StringComparer.OrdinalIgnoreCase);
    }

    static void SaveIpGuard()
    {
        Directory.CreateDirectory(AuthDir);
        File.WriteAllText(IpGuardPath, JsonSerializer.Serialize(_ipGuard, new JsonSerializerOptions { WriteIndented = true }));
    }

    static bool IsBlocked(HttpContext ctx)
    {
        if (IsLoopback(ctx)) return false;
        lock (IpLock) return IpGuard().TryGetValue(ClientKey(ctx), out var r) && r.Blocked;
    }

    /// <summary>Counts a failed sign-in for the caller's IP; returns true when this failure blocks the address.</summary>
    static bool RecordFailure(HttpContext ctx, string user)
    {
        if (IsLoopback(ctx)) return false;
        lock (IpLock)
        {
            var g = IpGuard();
            var ip = ClientKey(ctx);
            if (!g.TryGetValue(ip, out var r)) g[ip] = r = new IpRecord();
            r.Failures++;
            r.FirstFailureUtc ??= DateTime.UtcNow;
            r.LastFailureUtc = DateTime.UtcNow;
            r.LastUser = user.Length > 60 ? user[..60] : user;
            var justBlocked = !r.Blocked && r.Failures >= MaxFailures;
            if (justBlocked) { r.Blocked = true; r.BlockedUtc = DateTime.UtcNow; }
            SaveIpGuard();
            return justBlocked;
        }
    }

    static void ClearFailures(HttpContext ctx)
    {
        lock (IpLock)
        {
            if (IpGuard().Remove(ClientKey(ctx))) SaveIpGuard();
        }
    }

    /// <summary>Addresses that are blocked or have failed sign-ins, most recent first.</summary>
    public static List<(string Ip, IpRecord Record)> IpStatus()
    {
        lock (IpLock) return IpGuard().Select(kv => (kv.Key, kv.Value)).OrderByDescending(x => x.Value.Blocked).ThenByDescending(x => x.Value.LastFailureUtc).ToList();
    }

    /// <summary>Unblocks an address and clears its failed-attempt count. Recorded in the sign-in log.</summary>
    public static bool UnblockIp(string ip, string by, HttpContext? ctx = null)
    {
        bool removed;
        lock (IpLock)
        {
            removed = IpGuard().Remove(ip.Trim());
            if (removed) SaveIpGuard();
        }
        if (removed) AppendLoginEvent(new LoginEvent(DateTime.UtcNow, by, "ip-unblocked", ip.Trim(), ctx?.Request.Headers.UserAgent.ToString()));
        return removed;
    }

    // ----- sign-in log: one JSON line per event in odata/auth/login-log.jsonl (append-only; never holds passwords)

    public sealed record LoginEvent(DateTime Utc, string User, string Result, string? Ip, string? UserAgent);

    public const string UnknownUsersKey = "*unknown*";
    static string LoginLogPath => Path.Combine(AuthDir, "login-log.jsonl");
    static readonly object LoginLogLock = new();

    static void LogLogin(HttpContext ctx, string user, string result)
    {
        var agent = ctx.Request.Headers.UserAgent.ToString();
        AppendLoginEvent(new LoginEvent(DateTime.UtcNow, user.Length > 60 ? user[..60] : user, result, ClientKey(ctx), agent.Length > 300 ? agent[..300] : agent));
    }

    static void AppendLoginEvent(LoginEvent e)
    {
        try
        {
            lock (LoginLogLock)
            {
                Directory.CreateDirectory(AuthDir);
                File.AppendAllText(LoginLogPath, JsonSerializer.Serialize(e) + "\n");
            }
        }
        catch (IOException) { } // a full or locked disk must not stop sign-in
    }

    static List<LoginEvent> ReadLoginLog()
    {
        lock (LoginLogLock)
        {
            if (!File.Exists(LoginLogPath)) return new();
            var list = new List<LoginEvent>();
            foreach (var line in File.ReadLines(LoginLogPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try { if (JsonSerializer.Deserialize<LoginEvent>(line) is { } e) list.Add(e); } catch (JsonException) { }
            }
            return list;
        }
    }

    /// <summary>Sign-in events for one user (newest first), or for names that are not users when user is UnknownUsersKey.</summary>
    public static List<LoginEvent> LoginHistory(string user, int max = 500)
    {
        var known = LoadUsers().Select(u => u.Username).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return ReadLoginLog()
            .Where(e => user == UnknownUsersKey ? !known.Contains(e.User) : string.Equals(e.User, user, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.Utc).Take(max).ToList();
    }

    public sealed record LoginSummary(string User, DateTime? LastSignIn, string? LastSignInIp, int SignIns, int FailedLast30Days, DateTime? LastFailure);

    /// <summary>Per user: last successful sign-in, total sign-ins, and failed attempts in the last 30 days.</summary>
    public static Dictionary<string, LoginSummary> LoginSummaries()
    {
        var cutoff = DateTime.UtcNow.AddDays(-30);
        return ReadLoginLog().GroupBy(e => e.User, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g =>
        {
            var ok = g.Where(e => e.Result == "signed-in").OrderByDescending(e => e.Utc).FirstOrDefault();
            var failed = g.Where(e => e.Result is "wrong-password" or "locked-out").ToList();
            return new LoginSummary(g.Key, ok?.Utc, ok?.Ip, g.Count(e => e.Result == "signed-in"), failed.Count(e => e.Utc >= cutoff), failed.Count == 0 ? null : failed.Max(e => e.Utc));
        }, StringComparer.OrdinalIgnoreCase);
    }

    static string SafeReturnUrl(string? url) =>
        !string.IsNullOrEmpty(url) && url.StartsWith('/') && !url.StartsWith("//") && !url.StartsWith("/\\") ? url : "/";

    static void SetCookie(HttpContext ctx, string value, DateTimeOffset expires) =>
        ctx.Response.Cookies.Append(CookieName, value, new CookieOptions
        {
            HttpOnly = true,
            Secure = ctx.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            Expires = expires,
        });

    /// <summary>Adds the sign-in check and the /login and /logout endpoints. Call right after Build().</summary>
    public static void Use(WebApplication app)
    {
        app.Use(async (ctx, next) =>
        {
            var path = ctx.Request.Path.Value ?? "/";
            // A blocked address gets nothing, not even the sign-in page, until an admin unblocks it.
            if (IsBlocked(ctx) && !path.StartsWith("/images/"))
            {
                // Keep a record of sign-in attempts that arrive while the address is blocked.
                if (path == "/login" && HttpMethods.IsPost(ctx.Request.Method) && ctx.Request.HasFormContentType)
                {
                    var f = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
                    LogLogin(ctx, f["username"].ToString().Trim(), "blocked-attempt");
                }
                ctx.Response.StatusCode = 403;
                if (path.StartsWith("/api/")) await ctx.Response.WriteAsJsonAsync(new { error = "This address is blocked after too many failed sign-ins. Ask an admin to unblock it." });
                else { ctx.Response.ContentType = "text/html; charset=utf-8"; await ctx.Response.WriteAsync(LoginPage("/", "This address is blocked after too many failed sign-ins. Ask an admin to unblock it.")); }
                return;
            }
            if (path == "/login" || path == "/logout" || path.StartsWith("/images/") || path == "/sw.js" || path == "/manifest.webmanifest" || path.StartsWith("/apple-touch-icon"))
            {
                await next();
                return;
            }

            if (LoadUsers().Count == 0)
            {
                // No accounts yet: only this computer may use the dashboard.
                if (IsLoopback(ctx)) { await next(); return; }
                ctx.Response.StatusCode = 403;
                await ctx.Response.WriteAsync("OPTIC has no users set up. On the OPTIC computer run: OPTIC --add-user <username>");
                return;
            }

            if (CurrentUser(ctx) != null)
            {
                if ((AdminPages.Contains(path) || path.StartsWith("/api/admin/")) && !IsAdmin(ctx))
                {
                    ctx.Response.StatusCode = 403;
                    if (path.StartsWith("/api/")) await ctx.Response.WriteAsJsonAsync(new { error = "Only an admin can do this." });
                    else await ctx.Response.WriteAsync("Only an admin can open this page.");
                    return;
                }
                await next();
                return;
            }

            if (path.StartsWith("/api/"))
            {
                ctx.Response.StatusCode = 401;
                await ctx.Response.WriteAsJsonAsync(new { error = "Sign in required." });
                return;
            }
            ctx.Response.Redirect("/login?returnUrl=" + Uri.EscapeDataString(path + ctx.Request.QueryString));
        });

        app.MapGet("/login", (HttpContext ctx) =>
        {
            var returnUrl = SafeReturnUrl(ctx.Request.Query["returnUrl"]);
            if (CurrentUser(ctx) != null) return Results.Redirect(returnUrl);
            return Results.Content(LoginPage(returnUrl, null), "text/html; charset=utf-8");
        });

        app.MapPost("/login", async (HttpContext ctx) =>
        {
            var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
            var returnUrl = SafeReturnUrl(form["returnUrl"]);
            var attempted = form["username"].ToString().Trim();
            var known = LoadUsers().FirstOrDefault(u => string.Equals(u.Username, attempted, StringComparison.OrdinalIgnoreCase))?.Username;
            var user = CheckPassword(attempted, form["password"].ToString());
            if (user == null)
            {
                LogLogin(ctx, known ?? attempted, known is null ? "unknown-user" : "wrong-password");
                if (RecordFailure(ctx, known ?? attempted))
                {
                    LogLogin(ctx, known ?? attempted, "ip-blocked");
                    return Results.Content(LoginPage(returnUrl, "This address is blocked after too many failed sign-ins. Ask an admin to unblock it."), "text/html; charset=utf-8", statusCode: 403);
                }
                return Results.Content(LoginPage(returnUrl, "Wrong username or password."), "text/html; charset=utf-8", statusCode: 401);
            }
            ClearFailures(ctx);
            SetCookie(ctx, CreateSessionCookie(user), DateTimeOffset.UtcNow.Add(SessionLifetime));
            LogLogin(ctx, user, "signed-in");
            return Results.Redirect(returnUrl);
        });

        app.MapGet("/logout", (HttpContext ctx) =>
        {
            if (CurrentUser(ctx) is { } who) LogLogin(ctx, who, "signed-out");
            SetCookie(ctx, "", DateTimeOffset.UnixEpoch);
            return Results.Redirect("/login");
        });
    }

    static string LoginPage(string returnUrl, string? error)
    {
        var enc = System.Text.Encodings.Web.HtmlEncoder.Default;
        return $$"""
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>OPTIC Sign In</title>
<link rel="icon" type="image/png" href="/images/optic-icon.png">
<link rel="apple-touch-icon" sizes="180x180" href="{{(global::WebDashboard.AppleTouchIconHref)}}">
<style>
  :root { --bg: #f4f6f9; --surface: #ffffff; --text: #1b1f24; --muted: #5b6573; --border: #d5dbe3; --accent: #2f6fde; --error: #c4314b; }
  @media (prefers-color-scheme: dark) { :root { --bg: #12151a; --surface: #1c2027; --text: #e8ebef; --muted: #9aa4b2; --border: #343b46; --accent: #5b93f5; --error: #ff6b81; } }
  * { box-sizing: border-box; }
  body { margin: 0; min-height: 100vh; display: flex; align-items: center; justify-content: center; background: var(--bg); color: var(--text); font-family: "Segoe UI", system-ui, sans-serif; padding: 16px; }
  form { width: 100%; max-width: 360px; background: var(--surface); border: 1px solid var(--border); border-radius: 12px; padding: 28px 24px; }
  img { display: block; height: 40px; margin: 0 auto 18px; }
  h1 { font-size: 18px; margin: 0 0 18px; text-align: center; font-weight: 600; }
  label { display: block; font-size: 13px; color: var(--muted); margin: 12px 0 4px; }
  input { width: 100%; padding: 9px 10px; font-size: 15px; border: 1px solid var(--border); border-radius: 6px; background: var(--bg); color: var(--text); }
  button { width: 100%; margin-top: 20px; padding: 10px; font-size: 15px; font-weight: 600; border: 0; border-radius: 6px; background: var(--accent); color: #fff; cursor: pointer; }
  .error { color: var(--error); font-size: 13px; margin: 0 0 6px; text-align: center; }
</style>
</head>
<body>
<form method="post" action="/login">
  <img src="/images/optic-logo-color.png" alt="OPTIC">
  <h1>Sign in</h1>
  {{(error == null ? "" : $"<p class=\"error\" role=\"alert\">{enc.Encode(error)}</p>")}}
  <input type="hidden" name="returnUrl" value="{{enc.Encode(returnUrl)}}">
  <label for="username">Username</label>
  <input id="username" name="username" autocomplete="username" autocapitalize="none" spellcheck="false" required autofocus>
  <label for="password">Password</label>
  <input id="password" name="password" type="password" autocomplete="current-password" required>
  <button type="submit">Sign in</button>
</form>
</body>
</html>
""";
    }
}

/// <summary>OPTIC --add-user / --remove-user / --list-users.</summary>
public static class DashboardAuthCli
{
    public static int Run(string[] args)
    {
        var cmd = args[0].ToLowerInvariant();
        if (cmd == "--list-blocked-ips")
        {
            var rows = DashboardAuth.IpStatus();
            Console.WriteLine(rows.Count == 0 ? "No blocked addresses or failed sign-ins." : string.Join(Environment.NewLine, rows.Select(r =>
                $"{r.Ip}  {(r.Record.Blocked ? "BLOCKED since " + r.Record.BlockedUtc?.ToString("yyyy-MM-dd HH:mm") + " UTC" : "not blocked")}  {r.Record.Failures} failed  last tried: {r.Record.LastUser}")));
            return 0;
        }
        if (cmd == "--unblock-ip")
        {
            if (args.Length < 2) { Console.Error.WriteLine("Usage: OPTIC --unblock-ip <ip address>"); return 1; }
            var ok = DashboardAuth.UnblockIp(args[1], "command line");
            Console.WriteLine(ok ? $"Unblocked {args[1]}." : $"{args[1]} was not blocked.");
            return ok ? 0 : 1;
        }
        if (cmd == "--list-users")
        {
            var users = DashboardAuth.GetUsers();
            Console.WriteLine(users.Count == 0 ? "No users. The dashboard only opens on this computer." : string.Join(Environment.NewLine, users.Select(u => u.Username + (u.IsAdmin ? " (admin)" : ""))));
            return 0;
        }
        if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]))
        {
            Console.Error.WriteLine($"Usage: OPTIC {cmd} <username>");
            return 1;
        }
        var username = args[1];
        if (cmd == "--remove-user")
        {
            var removed = DashboardAuth.RemoveUser(username);
            Console.WriteLine(removed ? $"Removed {username}." : $"No user named {username}.");
            return removed ? 0 : 1;
        }

        string? password;
        if (Console.IsInputRedirected)
        {
            password = Console.ReadLine();
        }
        else
        {
            password = ReadHidden("Password: ");
            if (ReadHidden("Repeat password: ") != password)
            {
                Console.Error.WriteLine("The passwords do not match.");
                return 1;
            }
        }
        try
        {
            bool? admin = args.Any(a => string.Equals(a, "--admin", StringComparison.OrdinalIgnoreCase)) ? true : null;
            var existed = DashboardAuth.SetUser(username, password ?? "", admin);
            Console.WriteLine(existed ? $"Changed the password for {username}." : $"Added {username}.");
            return 0;
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    static string ReadHidden(string prompt)
    {
        Console.Write(prompt);
        var sb = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
            if (!char.IsControl(key.KeyChar)) sb.Append(key.KeyChar);
        }
        Console.WriteLine();
        return sb.ToString();
    }
}

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace OPTIC.Services;

/// <summary>
/// HTTPS for the web dashboard. Listens on httpsPort= from optic.conf (default 443; 0 turns it off) next to plain HTTP.
/// Uses an uploaded certificate (odata/auth/https-cert.pfx, its password in odata/auth/https.json) when there is one,
/// otherwise a self-signed certificate made for this computer's names and IP addresses.
/// </summary>
public static class DashboardHttps
{
    static string AuthDir => Path.Combine(Environment.CurrentDirectory, "odata", "auth");
    static string UploadedPfxPath => Path.Combine(AuthDir, "https-cert.pfx");
    static string UploadedSettingsPath => Path.Combine(AuthDir, "https.json");
    static string SelfSignedPath => Path.Combine(AuthDir, "https-selfsigned.pfx");

    public sealed record Status(bool Enabled, int Port, bool Redirect, string? Problem, string? Source, string? Subject, string? Names, DateTime? Expires, string? Thumbprint);

    /// <summary>What happened at startup, for the Admin page.</summary>
    public static Status Current { get; private set; } = new(false, 0, false, "Not started", null, null, null, null, null);

    static X509Certificate2? _certificate;

    static IPAddress BindAddress(string host) =>
        host is "0.0.0.0" or "*" or "+" ? IPAddress.Any
        : host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ? IPAddress.Loopback
        : IPAddress.Parse(host);

    static bool PortFree(IPAddress address, int port)
    {
        try
        {
            var probe = new TcpListener(address, port);
            probe.Start();
            probe.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>Sets up the HTTP and (when it can) HTTPS listeners. Call before builder.Build().</summary>
    public static void Configure(WebApplicationBuilder builder, string host, int port, IReadOnlyDictionary<string, string> cfg)
    {
        var address = BindAddress(host);
        var httpsPort = int.TryParse(cfg.GetValueOrDefault("httpsPort"), out var hp) ? hp : 443;
        var redirect = !string.Equals(cfg.GetValueOrDefault("httpsRedirect"), "false", StringComparison.OrdinalIgnoreCase);

        if (httpsPort <= 0 || httpsPort > 65535)
            Current = new(false, 0, false, "Turned off (httpsPort=0).", null, null, null, null, null);
        else if (httpsPort == port)
            Current = new(false, httpsPort, false, $"Port {httpsPort} is already the HTTP port.", null, null, null, null, null);
        else if (!PortFree(address, httpsPort))
            Current = new(false, httpsPort, false, $"Port {httpsPort} is already used by another program.", null, null, null, null, null);
        else
        {
            try
            {
                var (cert, source) = LoadCertificate();
                _certificate = cert;
                Current = new(true, httpsPort, redirect, null, source, cert.GetNameInfo(X509NameType.SimpleName, false),
                    SubjectNames(cert), cert.NotAfter, cert.Thumbprint);
            }
            catch (Exception ex)
            {
                Current = new(false, httpsPort, false, "Could not load the certificate: " + ex.Message, null, null, null, null, null);
            }
        }

        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(address, port);
            if (Current.Enabled)
                options.Listen(address, Current.Port, listen => listen.UseHttps(_certificate!));
        });

        Console.WriteLine(Current.Enabled
            ? $"HTTPS on port {Current.Port} ({Current.Source}, valid until {Current.Expires:yyyy-MM-dd})"
            : $"HTTPS is off: {Current.Problem}");
    }

    /// <summary>Sends browsers on other devices from http:// to https://. This computer can keep using http://.</summary>
    public static void UseRedirect(WebApplication app)
    {
        app.Use(async (ctx, next) =>
        {
            var ip = ctx.Connection.RemoteIpAddress;
            if (Current is { Enabled: true, Redirect: true } && !ctx.Request.IsHttps && ip != null && !IPAddress.IsLoopback(ip))
            {
                var hostName = ctx.Request.Host.Host;
                var portPart = Current.Port == 443 ? "" : ":" + Current.Port;
                ctx.Response.Redirect($"https://{hostName}{portPart}{ctx.Request.PathBase}{ctx.Request.Path}{ctx.Request.QueryString}", permanent: false);
                return;
            }
            await next();
        });
    }

    static (X509Certificate2 cert, string source) LoadCertificate()
    {
        if (File.Exists(UploadedPfxPath))
        {
            var password = File.Exists(UploadedSettingsPath)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(UploadedSettingsPath))?.GetValueOrDefault("password")
                : null;
            return (X509CertificateLoader.LoadPkcs12FromFile(UploadedPfxPath, password), "uploaded certificate");
        }

        var names = MachineNames();
        if (File.Exists(SelfSignedPath))
        {
            var existing = X509CertificateLoader.LoadPkcs12FromFile(SelfSignedPath, null);
            var covered = SubjectNames(existing) ?? "";
            // Reuse it while it is valid and still covers this computer's current names and addresses.
            if (existing.NotAfter > DateTime.Now.AddDays(14) && names.All(n => covered.Contains(n, StringComparison.OrdinalIgnoreCase)))
                return (existing, "self-signed certificate");
        }
        var created = CreateSelfSigned(names);
        Directory.CreateDirectory(AuthDir);
        File.WriteAllBytes(SelfSignedPath, created.Export(X509ContentType.Pfx));
        return (X509CertificateLoader.LoadPkcs12FromFile(SelfSignedPath, null), "self-signed certificate (new)");
    }

    /// <summary>localhost, the computer name and every IPv4 address this computer has.</summary>
    static List<string> MachineNames()
    {
        var names = new List<string> { "localhost", Environment.MachineName, "127.0.0.1" };
        names.AddRange(NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(a => a.Address.ToString()));
        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    static X509Certificate2 CreateSelfSigned(List<string> names)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN=OPTIC on {Environment.MachineName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        foreach (var n in names)
        {
            if (IPAddress.TryParse(n, out var ip)) san.AddIpAddress(ip);
            else san.AddDnsName(n);
        }
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
        // 825 days is the longest validity Apple devices accept for a server certificate.
        return request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(825));
    }

    static string? SubjectNames(X509Certificate2 cert) =>
        cert.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault() is { } ext
            ? string.Join(", ", ext.EnumerateDnsNames().Concat(ext.EnumerateIPAddresses().Select(ip => ip.ToString())))
            : null;

    /// <summary>Admin endpoints: status, download the certificate to trust it, upload your own, go back to self-signed.</summary>
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/admin/https", () => Results.Json(new
        {
            current = Current,
            uploaded = File.Exists(UploadedPfxPath),
            note = "Certificate and port changes take effect the next time OPTIC starts.",
        }));

        // The public certificate only (no private key), to install on a phone or another computer so it trusts OPTIC.
        app.MapGet("/api/admin/https/certificate.cer", () =>
        {
            return _certificate == null
                ? Results.NotFound()
                : Results.File(_certificate.Export(X509ContentType.Cert), "application/pkix-cert", $"optic-{Environment.MachineName}.cer");
        });

        app.MapPost("/api/admin/https/certificate", async (HttpContext ctx) =>
        {
            if (!ctx.Request.HasFormContentType) return Results.Json(new { error = "Send the .pfx file as a form upload." }, statusCode: 400);
            var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
            var file = form.Files.GetFile("pfx");
            if (file == null || file.Length == 0 || file.Length > 1_000_000) return Results.Json(new { error = "Choose a .pfx or .p12 file (under 1 MB)." }, statusCode: 400);
            var password = form["password"].ToString();
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ctx.RequestAborted);
            X509Certificate2 cert;
            try { cert = X509CertificateLoader.LoadPkcs12(ms.ToArray(), password.Length == 0 ? null : password); }
            catch (CryptographicException) { return Results.Json(new { error = "Could not open that file. Check the password." }, statusCode: 400); }
            if (!cert.HasPrivateKey) return Results.Json(new { error = "That file has no private key. Export it as .pfx with the private key." }, statusCode: 400);
            if (cert.NotAfter < DateTime.Now) return Results.Json(new { error = $"That certificate expired on {cert.NotAfter:yyyy-MM-dd}." }, statusCode: 400);
            Directory.CreateDirectory(AuthDir);
            File.WriteAllBytes(UploadedPfxPath, ms.ToArray());
            File.WriteAllText(UploadedSettingsPath, JsonSerializer.Serialize(new Dictionary<string, string> { ["password"] = password }));
            return Results.Json(new { ok = true, subject = cert.GetNameInfo(X509NameType.SimpleName, false), names = SubjectNames(cert), expires = cert.NotAfter });
        });

        app.MapPost("/api/admin/https/use-self-signed", () =>
        {
            File.Delete(UploadedPfxPath);
            File.Delete(UploadedSettingsPath);
            return Results.Json(new { ok = true });
        });
    }
}

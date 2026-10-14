using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace OPTIC.Services;

public sealed class EvidenceFile
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Url { get; set; } = "";
    public DateTime FetchedUtc { get; set; }
    public int? HttpStatus { get; set; }
    public long Bytes { get; set; }
    public string Sha256 { get; set; } = "";
    public string? Error { get; set; }
}

public sealed class EvidenceCapture
{
    public string Id { get; set; } = "";
    public DateTime CapturedUtc { get; set; }
    public List<EvidenceFile> Files { get; set; } = new();
}

/// <summary>
/// Saves the raw, unmodified responses of the public market APIs that show OPT's liquidity, with
/// the time fetched and a SHA-256 of each body, so the state of the market on a given date can be
/// shown later and checked by someone else. Each capture is a folder under odata/evidence.
/// </summary>
public static class LiquidityEvidenceService
{
    private static readonly string Root = Path.Combine(Environment.CurrentDirectory, "odata", "evidence");
    private static readonly HttpClient Http = CreateHttpClient();
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static IEnumerable<(string Name, string Description, string Url)> Sources(IEnumerable<decimal> quoteSizes)
    {
        const string cs = "https://api.coinstore.com/api/v1/market";
        var lastTradingEndMs = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        yield return ("coinstore-order-book.json", "Coinstore OPT/USDT order book now (b = buy orders, a = sell orders)", $"{cs}/depth/OPTUSDT?depth=100");
        yield return ("coinstore-recent-trades.json", "Coinstore OPT/USDT most recent trades (time = unix seconds)", $"{cs}/trade/OPTUSDT?size=50");
        yield return ("coinstore-daily-candles-recent.json", "Coinstore OPT/USDT daily candles for the last 200 days (empty = no trading)", $"{cs}/kline/OPTUSDT?period=1day&size=200");
        yield return ("coinstore-daily-candles-to-2026-02-01.json", "Coinstore OPT/USDT daily candles up to 2026-02-01, showing the last days of trading", $"{cs}/kline/OPTUSDT?period=1day&size=200&endTime={lastTradingEndMs}");
        yield return ("coinmarketcap-detail.json", "CoinMarketCap listing for Optio (status, last update)", "https://api.coinmarketcap.com/data-api/v3/cryptocurrency/detail?id=35828");
        yield return ("coinmarketcap-market-pairs.json", "CoinMarketCap exchange markets for Optio", "https://api.coinmarketcap.com/data-api/v3/cryptocurrency/market-pairs/latest?id=35828&start=1&limit=100&category=spot");
        yield return ("coingecko-coin.json", "CoinGecko listing for Optio, with its exchange tickers and market data", "https://api.coingecko.com/api/v3/coins/optio?localization=false&tickers=true&market_data=true&community_data=false&developer_data=false");
        yield return ("mexc-exchange-info.json", "MEXC symbol lookup for OPTUSDT (empty symbols = not listed)", "https://api.mexc.com/api/v3/exchangeInfo?symbol=OPTUSDT");
        yield return ("osmosis-numia-token.json", "Numia (Osmosis data) token summary for OPT", "https://public-osmosis-api.numia.xyz/tokens/v2/OPT");
        yield return ("osmosis-pools.json", "Osmosis OPT pools and their token balances", "https://sqs.osmosis.zone/pools?IDs=3278,3329,3391,3400,3565,3571,3572");
        yield return ("osmosis-spot-price.json", "Osmosis spot price of OPT", $"https://sqs.osmosis.zone/tokens/prices?base={Uri.EscapeDataString(MarketVolumeService.OsmosisOptDenom)}&humanDenoms=false");
        foreach (var size in quoteSizes)
        {
            var label = size.ToString("0", CultureInfo.InvariantCulture);
            yield return ($"osmosis-sell-quote-{label}-opt.json", $"Osmosis router quote for selling {size.ToString("N0", CultureInfo.InvariantCulture)} OPT for USDC (amount_out in micro-USDC)", MarketVolumeService.SellQuoteUrl(size));
        }
    }

    public static async Task<EvidenceCapture> CaptureAsync(IEnumerable<decimal> extraQuoteSizes, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var now = DateTime.UtcNow;
            var capture = new EvidenceCapture { Id = now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "Z", CapturedUtc = now };
            var dir = Path.Combine(Root, capture.Id);
            Directory.CreateDirectory(dir);

            var sizes = MarketVolumeService.QuoteSizesOpt.Concat(extraQuoteSizes.Where(s => s > 0).Select(s => Math.Round(s))).Distinct().OrderBy(s => s);
            var sources = Sources(sizes).ToList();
            // The Osmosis router errors when hit with many quotes at once, so those go one at a time.
            var isQuote = (string name) => name.StartsWith("osmosis-sell-quote-", StringComparison.Ordinal);
            var others = await Task.WhenAll(sources.Where(s => !isQuote(s.Name)).Select(s => FetchAsync(dir, s.Name, s.Description, s.Url, ct)));
            var quotes = new List<EvidenceFile>();
            foreach (var s in sources.Where(s => isQuote(s.Name)))
                quotes.Add(await FetchAsync(dir, s.Name, s.Description, s.Url, ct));
            capture.Files = others.Concat(quotes).ToList();

            await File.WriteAllTextAsync(Path.Combine(dir, "manifest.json"),
                JsonSerializer.Serialize(capture, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }), ct);
            await File.WriteAllTextAsync(Path.Combine(dir, "README.txt"), Readme(capture), ct);
            return capture;
        }
        finally
        {
            Gate.Release();
        }
    }

    public static EvidenceCapture? Latest()
    {
        if (!Directory.Exists(Root))
            return null;
        var latest = Directory.GetDirectories(Root)
            .Where(d => File.Exists(Path.Combine(d, "manifest.json")))
            .OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal)
            .FirstOrDefault();
        return latest is null ? null : Load(Path.GetFileName(latest));
    }

    public static EvidenceCapture? Load(string id)
    {
        if (!IsValidId(id))
            return null;
        var path = Path.Combine(Root, id, "manifest.json");
        return File.Exists(path)
            ? JsonSerializer.Deserialize<EvidenceCapture>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            : null;
    }

    /// <summary>Zips one capture folder (raw files, manifest and README) in memory.</summary>
    public static byte[]? Zip(string id)
    {
        if (!IsValidId(id) || !Directory.Exists(Path.Combine(Root, id)))
            return null;
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in Directory.GetFiles(Path.Combine(Root, id)))
                zip.CreateEntryFromFile(file, $"opt-liquidity-evidence-{id}/{Path.GetFileName(file)}");
        }
        return ms.ToArray();
    }

    private static bool IsValidId(string id) =>
        System.Text.RegularExpressions.Regex.IsMatch(id ?? "", "^[0-9]{8}-[0-9]{6}Z$");

    private static async Task<EvidenceFile> FetchAsync(string dir, string name, string description, string url, CancellationToken ct)
    {
        var file = new EvidenceFile { Name = name, Description = description, Url = url, FetchedUtc = DateTime.UtcNow };
        try
        {
            var resp = await Http.GetAsync(url, ct);
            // Retry rate limits and server errors a couple of times; the saved file is the last response.
            for (var attempt = 1; attempt < 3 && ((int)resp.StatusCode == 429 || (int)resp.StatusCode >= 500); attempt++)
            {
                resp.Dispose();
                await Task.Delay(1000 * attempt, ct);
                resp = await Http.GetAsync(url, ct);
            }
            using var _ = resp;
            var body = await resp.Content.ReadAsByteArrayAsync(ct);
            file.FetchedUtc = DateTime.UtcNow;
            file.HttpStatus = (int)resp.StatusCode;
            file.Bytes = body.LongLength;
            file.Sha256 = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
            await File.WriteAllBytesAsync(Path.Combine(dir, name), body, ct);
            if (!resp.IsSuccessStatusCode)
                file.Error = $"HTTP {(int)resp.StatusCode}";
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            file.Error = ex.Message;
        }
        return file;
    }

    private static string Readme(EvidenceCapture capture)
    {
        var lines = new List<string>
        {
            "OPT market liquidity evidence",
            $"Captured {capture.CapturedUtc:yyyy-MM-dd HH:mm:ss} UTC by OPTIC.",
            "",
            "Each .json file is the unmodified response body from the public URL listed below, saved at the time shown.",
            "manifest.json lists every file with its source URL, HTTP status, size and SHA-256 hash of the body.",
            "To check a file has not been changed since capture, compute its SHA-256 (e.g. `certutil -hashfile <file> SHA256` on Windows",
            "or `sha256sum <file>`) and compare it with the manifest. Opening the URL again shows the market as it is then, not as captured.",
            "",
        };
        foreach (var f in capture.Files)
        {
            lines.Add($"{f.Name}");
            lines.Add($"  {f.Description}");
            lines.Add($"  {f.Url}");
            lines.Add($"  fetched {f.FetchedUtc:yyyy-MM-dd HH:mm:ss} UTC, HTTP {f.HttpStatus?.ToString(CultureInfo.InvariantCulture) ?? "-"}, {f.Bytes} bytes, sha256 {f.Sha256}{(f.Error is null ? "" : ", error: " + f.Error)}");
            lines.Add("");
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static HttpClient CreateHttpClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return http;
    }
}

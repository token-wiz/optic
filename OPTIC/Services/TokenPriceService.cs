using System.Globalization;
using System.Text.Json;

namespace OPTIC.Services;

public sealed record TokenPricePoint(decimal PriceUsd, DateTime CandleOpenUtc, string Interval);

/// <summary>
/// Historical OPT/USD prices from CoinMarketCap's public data API. Hourly candles are used when
/// available, with daily candles as a fallback. Results are cached in memory for the process lifetime.
/// </summary>
public static class TokenPriceService
{
    private const int CmcId = 35828;
    private const int UsdConvertId = 2781;
    private const long HourSeconds = 3600;
    private const long DaySeconds = 86400;
    // CMC returns at most ~400 candles per request.
    private const long HourlyChunkSeconds = 400 * HourSeconds;
    private const long DailyWindowSeconds = 400 * DaySeconds;

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly Dictionary<long, decimal> Hourly = new();
    private static readonly Dictionary<long, decimal> Daily = new();
    private static readonly Dictionary<long, decimal> DailyVolumeUsd = new();
    private static readonly Dictionary<long, DateTimeOffset> HourlyChunksFetched = new();
    private static DateTimeOffset _dailyLoadedAt;
    private static long? _dataStartUnix;
    private static long? _dataEndUnix;

    private static readonly HttpClient Http = CreateHttpClient();

    public static (DateTime? FirstUtc, DateTime? LastUtc) AvailableRange =>
        (_dataStartUnix.HasValue ? DateTimeOffset.FromUnixTimeSeconds(_dataStartUnix.Value).UtcDateTime : null,
         _dataEndUnix.HasValue ? DateTimeOffset.FromUnixTimeSeconds(_dataEndUnix.Value + DaySeconds - 1).UtcDateTime : null);

    /// <summary>
    /// Returns the price for each timestamp: the close of the hourly candle containing it, or the
    /// daily close for that UTC day when no hourly candle exists. Null when CMC has no data.
    /// </summary>
    public static async Task<Dictionary<DateTime, TokenPricePoint?>> GetPricesAtAsync(
        IReadOnlyCollection<DateTime> timesUtc,
        CancellationToken ct)
    {
        var result = new Dictionary<DateTime, TokenPricePoint?>();
        if (timesUtc.Count == 0)
            return result;

        await Gate.WaitAsync(ct);
        try
        {
            await EnsureDailyAsync(ct);

            var unixTimes = timesUtc.Select(ToUnix).ToList();
            await EnsureHourlyAsync(unixTimes.Min(), unixTimes.Max(), ct);

            foreach (var t in timesUtc)
            {
                var unix = ToUnix(t);
                var hourKey = unix - (unix % HourSeconds);
                var dayKey = unix - (unix % DaySeconds);

                if (Hourly.TryGetValue(hourKey, out var hourClose))
                    result[t] = new TokenPricePoint(hourClose, FromUnix(hourKey), "1h");
                else if (Daily.TryGetValue(dayKey, out var dayClose))
                    result[t] = new TokenPricePoint(dayClose, FromUnix(dayKey), "1d");
                else
                    result[t] = null;
            }
        }
        finally
        {
            Gate.Release();
        }

        return result;
    }

    /// <summary>Every daily close CMC has for OPT, oldest first.</summary>
    public static async Task<List<(DateTime DateUtc, decimal CloseUsd)>> GetDailySeriesAsync(CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            await EnsureDailyAsync(ct);
            return Daily.OrderBy(kv => kv.Key).Select(kv => (FromUnix(kv.Key), kv.Value)).ToList();
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Every daily candle CMC has for OPT with its all-exchange USD volume, oldest first.</summary>
    public static async Task<List<(DateTime DateUtc, decimal CloseUsd, decimal? VolumeUsd)>> GetDailyCandlesAsync(CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            await EnsureDailyAsync(ct);
            return Daily.OrderBy(kv => kv.Key)
                .Select(kv => (FromUnix(kv.Key), kv.Value, DailyVolumeUsd.TryGetValue(kv.Key, out var v) ? v : (decimal?)null))
                .ToList();
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task EnsureDailyAsync(CancellationToken ct)
    {
        if (Daily.Count > 0 && DateTimeOffset.UtcNow - _dailyLoadedAt < TimeSpan.FromHours(6))
            return;

        // Walk backwards from now; each response holds the latest candles before timeEnd.
        var end = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        for (var i = 0; i < 20; i++)
        {
            var quotes = await FetchQuotesAsync(end - DailyWindowSeconds, end, "1d", ct);
            if (quotes is null || quotes.Count == 0)
                break;

            foreach (var (openUnix, close, volume) in quotes)
            {
                Daily[openUnix] = close;
                if (volume.HasValue)
                    DailyVolumeUsd[openUnix] = volume.Value;
            }

            var earliest = quotes.Min(q => q.OpenUnix);
            if (earliest >= end)
                break;
            end = earliest - 1;
        }

        if (Daily.Count > 0)
        {
            _dataStartUnix = Daily.Keys.Min();
            _dataEndUnix = Daily.Keys.Max();
        }
        _dailyLoadedAt = DateTimeOffset.UtcNow;
    }

    private static async Task EnsureHourlyAsync(long fromUnix, long toUnix, CancellationToken ct)
    {
        if (!_dataStartUnix.HasValue || !_dataEndUnix.HasValue)
            return;

        // Only ask CMC for chunks that overlap the range it actually has data for.
        var from = Math.Max(fromUnix, _dataStartUnix.Value);
        var to = Math.Min(toUnix, _dataEndUnix.Value + DaySeconds);
        if (from > to)
            return;

        var now = DateTimeOffset.UtcNow;
        for (var chunk = from / HourlyChunkSeconds; chunk <= to / HourlyChunkSeconds; chunk++)
        {
            var chunkStart = chunk * HourlyChunkSeconds;
            var chunkEnd = chunkStart + HourlyChunkSeconds;
            var isRecent = chunkEnd > now.ToUnixTimeSeconds() - HourSeconds;

            if (HourlyChunksFetched.TryGetValue(chunk, out var fetchedAt) &&
                (!isRecent || now - fetchedAt < TimeSpan.FromHours(1)))
                continue;

            // Start one candle early: CMC excludes the candle opening exactly at timeStart.
            var quotes = await FetchQuotesAsync(chunkStart - HourSeconds, chunkEnd, "1h", ct);
            if (quotes is null)
                continue; // Leave unmarked so a later request retries.

            foreach (var (openUnix, close, _) in quotes)
                Hourly[openUnix] = close;

            HourlyChunksFetched[chunk] = now;
        }
    }

    private static async Task<List<(long OpenUnix, decimal Close, decimal? VolumeUsd)>?> FetchQuotesAsync(
        long timeStart,
        long timeEnd,
        string interval,
        CancellationToken ct)
    {
        var url = "https://api.coinmarketcap.com/data-api/v3.1/cryptocurrency/historical" +
                  $"?id={CmcId}&convertId={UsdConvertId}&interval={interval}" +
                  $"&timeStart={timeStart.ToString(CultureInfo.InvariantCulture)}" +
                  $"&timeEnd={timeEnd.ToString(CultureInfo.InvariantCulture)}";

        var delayMs = 500;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var resp = await Http.GetAsync(url, ct);
                if ((int)resp.StatusCode == 429 || (int)resp.StatusCode >= 500)
                {
                    await Task.Delay(delayMs, ct);
                    delayMs *= 2;
                    continue;
                }

                if (!resp.IsSuccessStatusCode)
                    return null;

                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                return ParseQuotes(doc.RootElement);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && attempt < 3)
            {
                await Task.Delay(delayMs, ct);
                delayMs *= 2;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return null;
            }
        }

        return null;
    }

    private static List<(long OpenUnix, decimal Close, decimal? VolumeUsd)> ParseQuotes(JsonElement root)
    {
        var output = new List<(long, decimal, decimal?)>();
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("quotes", out var quotes) || quotes.ValueKind != JsonValueKind.Array)
            return output;

        foreach (var q in quotes.EnumerateArray())
        {
            if (!q.TryGetProperty("timeOpen", out var openEl) ||
                !DateTimeOffset.TryParse(openEl.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var open))
                continue;

            if (!q.TryGetProperty("quote", out var quote) ||
                !quote.TryGetProperty("close", out var closeEl) ||
                closeEl.ValueKind != JsonValueKind.Number ||
                !closeEl.TryGetDecimal(out var close))
                continue;

            decimal? volume = quote.TryGetProperty("volume", out var volumeEl) &&
                              volumeEl.ValueKind == JsonValueKind.Number &&
                              volumeEl.TryGetDecimal(out var v)
                ? v
                : null;
            output.Add((open.ToUnixTimeSeconds(), close, volume));
        }

        return output;
    }

    private static HttpClient CreateHttpClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        http.DefaultRequestHeaders.Referrer = new Uri("https://coinmarketcap.com/currencies/optio/");
        return http;
    }

    private static long ToUnix(DateTime utc) =>
        new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();

    private static DateTime FromUnix(long unix) => DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
}

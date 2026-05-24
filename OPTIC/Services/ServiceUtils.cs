using System.Globalization;
using System.Text.Json;

internal static class ServiceUtils
{
    public static async Task<JsonDocument?> TryGetJsonAsync(HttpClient http, string relativeUrl, CancellationToken ct)
    {
        return await TryGetJsonAsync(http, relativeUrl, null, ct);
    }

    public static async Task<JsonDocument?> TryGetJsonAsync(HttpClient http, string relativeUrl, long? height, CancellationToken ct)
    {
        try
        {
            if (height.HasValue && height.Value > 0)
                relativeUrl = AppendHeightQuery(relativeUrl, height.Value);

            using var resp = await http.GetAsync(relativeUrl, ct);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync(ct);
            return JsonDocument.Parse(json);
        }
        catch
        {
            return null;
        }
    }

    private static string AppendHeightQuery(string relativeUrl, long height)
    {
        if (relativeUrl.Contains("height=", StringComparison.OrdinalIgnoreCase))
            return relativeUrl;

        var separator = relativeUrl.Contains('?') ? "&" : "?";
        return $"{relativeUrl}{separator}height={height}";
    }

    public static decimal SumCoins(JsonElement coins, string denomWantedUpper)
    {
        if (coins.ValueKind != JsonValueKind.Array) return 0m;

        decimal total = 0m;
        foreach (var coin in coins.EnumerateArray())
        {
            if (!TryGetCoinAmount(coin, denomWantedUpper, out var amount))
                continue;
            total += amount;
        }

        return total;
    }

    public static bool TryGetCoinAmount(JsonElement coin, string denomWantedUpper, out decimal amountUnits)
    {
        amountUnits = 0m;
        if (coin.ValueKind != JsonValueKind.Object) return false;

        var denom = coin.TryGetProperty("denom", out var denomEl) ? denomEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(denom) ||
            !denom.Equals(denomWantedUpper, StringComparison.OrdinalIgnoreCase))
            return false;

        var amountStr = coin.TryGetProperty("amount", out var amountEl) ? amountEl.GetString() : null;
        return TryParseAmount(amountStr, out amountUnits);
    }

    public static bool TryParseAmount(string? amountStr, out decimal amountUnits)
    {
        amountUnits = 0m;
        if (string.IsNullOrWhiteSpace(amountStr)) return false;
        return decimal.TryParse(amountStr, NumberStyles.Any, CultureInfo.InvariantCulture, out amountUnits);
    }

    public static DateTimeOffset? TryParseUnixSeconds(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var num))
            return DateTimeOffset.FromUnixTimeSeconds(num);

        if (element.ValueKind == JsonValueKind.String &&
            long.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
            return DateTimeOffset.FromUnixTimeSeconds(seconds);

        return null;
    }
}

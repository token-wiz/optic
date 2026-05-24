using System.Text.Json;

internal sealed class BankService
{
    private readonly HttpClient _http;

    public BankService(HttpClient http)
    {
        _http = http;
    }

    public async Task<decimal?> GetSpendableUnitsAsync(string address, string denomWantedUpper, CancellationToken ct, long? height = null)
    {
        using var doc = await ServiceUtils.TryGetJsonAsync(
            _http,
            $"cosmos/bank/v1beta1/spendable_balances/{address}?pagination.limit=2000",
            height,
            ct);

        if (doc is null) return null;

        JsonElement arr;
        if (doc.RootElement.TryGetProperty("balances", out arr) && arr.ValueKind == JsonValueKind.Array)
        {
            return ServiceUtils.SumCoins(arr, denomWantedUpper);
        }

        if (doc.RootElement.TryGetProperty("spendable_balances", out arr) && arr.ValueKind == JsonValueKind.Array)
        {
            return ServiceUtils.SumCoins(arr, denomWantedUpper);
        }

        return null;
    }

    public async Task<Dictionary<string, decimal>> GetBalancesAsync(string address, CancellationToken ct, long? height = null)
    {
        using var doc = await ServiceUtils.TryGetJsonAsync(
            _http,
            $"cosmos/bank/v1beta1/balances/{address}?pagination.limit=2000",
            height,
            ct);

        var balances = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        if (doc is null) return balances;

        if (!doc.RootElement.TryGetProperty("balances", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return balances;

        foreach (var coin in arr.EnumerateArray())
        {
            if (coin.ValueKind != JsonValueKind.Object) continue;
            var denom = coin.TryGetProperty("denom", out var denomEl) ? denomEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(denom)) continue;

            var amountStr = coin.TryGetProperty("amount", out var amountEl) ? amountEl.GetString() : null;
            if (!ServiceUtils.TryParseAmount(amountStr, out var amount))
                continue;

            balances[denom] = amount;
        }

        return balances;
    }

    public async Task<decimal> GetBalanceAsync(string address, string denomWantedUpper, CancellationToken ct, long? height = null)
    {
        var balances = await GetBalancesAsync(address, ct, height);
        foreach (var kvp in balances)
        {
            if (kvp.Key.Equals(denomWantedUpper, StringComparison.OrdinalIgnoreCase))
                return kvp.Value;
        }
        return 0m;
    }
}

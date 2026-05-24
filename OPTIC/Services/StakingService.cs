using System.Text.Json;

internal sealed class StakingService
{
    private readonly HttpClient _http;

    public StakingService(HttpClient http)
    {
        _http = http;
    }

    public async Task<decimal?> GetDelegatedBondedUnitsAsync(string address, string denomWantedUpper, CancellationToken ct, long? height = null)
    {
        using var doc = await ServiceUtils.TryGetJsonAsync(
            _http,
            $"cosmos/staking/v1beta1/delegations/{address}?pagination.limit=2000",
            height,
            ct);

        if (doc is null) return null;

        if (!doc.RootElement.TryGetProperty("delegation_responses", out var arr) ||
            arr.ValueKind != JsonValueKind.Array)
            return null;

        decimal total = 0m;
        foreach (var dr in arr.EnumerateArray())
        {
            if (!dr.TryGetProperty("balance", out var bal) || bal.ValueKind != JsonValueKind.Object)
                continue;

            if (ServiceUtils.TryGetCoinAmount(bal, denomWantedUpper, out var amount))
                total += amount;
        }

        return total;
    }

    public async Task<decimal> GetDelegationTotalAsync(string address, CancellationToken ct, long? height = null)
    {
        // Default to uopt if no specific denom provided
        var result = await GetDelegatedBondedUnitsAsync(address, "uopt", ct, height);
        return result ?? 0m;
    }

    public async Task<decimal?> GetUnbondingAmountAsync(string address, string denomWantedUpper, CancellationToken ct, long? height = null)
    {
        using var doc = await ServiceUtils.TryGetJsonAsync(
            _http,
            $"cosmos/staking/v1beta1/delegators/{address}/unbonding_delegations?pagination.limit=2000",
            height,
            ct);

        if (doc is null) return null;

        if (!doc.RootElement.TryGetProperty("unbonding_responses", out var arr) ||
            arr.ValueKind != JsonValueKind.Array)
            return null;

        decimal total = 0m;
        foreach (var ud in arr.EnumerateArray())
        {
            if (!ud.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var entry in entries.EnumerateArray())
            {
                if (!entry.TryGetProperty("balance", out var bal) || bal.ValueKind != JsonValueKind.String)
                    continue;

                // Balance is a string representing the amount in base denom
                if (decimal.TryParse(bal.GetString(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var amount))
                {
                    total += amount;
                }
            }
        }

        return total;
    }

    public async Task<decimal> GetUnbondingTotalAsync(string address, CancellationToken ct, long? height = null)
    {
        // Default to uopt if no specific denom provided
        var result = await GetUnbondingAmountAsync(address, "uopt", ct, height);
        return result ?? 0m;
    }
}

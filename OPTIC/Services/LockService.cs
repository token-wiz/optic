using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Grpc.Net.Client;
using Grpc.Core;
using Google.Protobuf;
using Optio.Lockup;

internal sealed class LockService
{
    private readonly HttpClient _http;
    private readonly GrpcChannel _channel;

    public LockService(HttpClient http, GrpcChannel channel)
    {
        _http = http;
        _channel = channel;
    }

    public async Task<List<LockRow>> GetLockRowsAsync(
        string address,
        string denomWantedUpper,
        int scaleInt,
        CancellationToken ct,
        long? height = null)
    {
        var lockupRows = await TryGetLockupModuleLocksAsync(address, denomWantedUpper, scaleInt, ct, height);
        if (lockupRows.Count > 0) return lockupRows;

        return await GetVestingDerivedLocksAsync(address, denomWantedUpper, scaleInt, ct, height);
    }

    public async Task<List<LockRow>> GetAllActiveLockRowsAsync(
        string denomWantedUpper,
        int scaleInt,
        CancellationToken ct,
        long? height = null)
    {
        var rows = await TryGetOptioLockupActiveLocksAsync(denomWantedUpper, scaleInt, ct, height);
        return rows;
    }

    public async Task<Dictionary<string, decimal[]>> GetAllActiveLockBucketsAsync(
        string denomWantedUpper,
        int scaleInt,
        DateTimeOffset nowUtc,
        CancellationToken ct,
        long? height = null)
    {
        var bucketsByAddress = new Dictionary<string, decimal[]>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var client = new Query.QueryClient(_channel);
            var metadata = BuildHeightMetadata(height);
            ByteString? nextKey = null;

            do
            {
                var request = new QueryActiveLocksRequest();
                if (nextKey is not null)
                {
                    request.Pagination = new Cosmos.Base.Query.V1Beta1.PageRequest
                    {
                        Key = nextKey,
                        Limit = 2000,
                    };
                }
                else
                {
                    request.Pagination = new Cosmos.Base.Query.V1Beta1.PageRequest
                    {
                        Limit = 2000,
                    };
                }

                var response = await client.ActiveLocksAsync(request, metadata, cancellationToken: ct).ResponseAsync;
                if (response?.Locks is null || response.Locks.Count == 0)
                    break;

                foreach (var lockItem in response.Locks)
                {
                    if (string.IsNullOrWhiteSpace(lockItem.Address)) continue;

                    var amountUnits = 0m;
                    if (lockItem.Amount is not null &&
                        lockItem.Amount.Denom.Equals(denomWantedUpper, StringComparison.OrdinalIgnoreCase) &&
                        ServiceUtils.TryParseAmount(lockItem.Amount.Amount, out var parsed))
                    {
                        amountUnits = parsed;
                    }

                    if (amountUnits <= 0m) continue;

                    var endTime = TryParseUnlockDate(lockItem.UnlockDate);
                    if (!endTime.HasValue) continue;

                    var months = GetRemainingMonths(endTime.Value, nowUtc);
                    int bucket;
                    if (months <= 6) bucket = 0;
                    else if (months <= 12) bucket = 1;
                    else if (months <= 18) bucket = 2;
                    else if (months <= 24) bucket = 3;
                    else continue;

                    if (!bucketsByAddress.TryGetValue(lockItem.Address, out var buckets))
                    {
                        buckets = new decimal[4];
                        bucketsByAddress[lockItem.Address] = buckets;
                    }

                    buckets[bucket] += amountUnits / scaleInt;
                }

                nextKey = response.Pagination?.NextKey;
            }
            while (nextKey is not null && nextKey.Length > 0);
        }
        catch
        {
            return bucketsByAddress;
        }

        return bucketsByAddress;
    }

    private async Task<List<LockRow>> TryGetLockupModuleLocksAsync(
        string address,
        string denomWantedUpper,
        int scaleInt,
        CancellationToken ct,
        long? height = null)
    {
        var typedRows = await TryGetOptioLockupLocksAsync(address, denomWantedUpper, scaleInt, ct, height);
        if (typedRows.Count > 0) return typedRows;

        var clientType = FindLockupQueryClientType();
        if (clientType is null) return new List<LockRow>();

        object? client;
        try
        {
            var callInvoker = _channel.CreateCallInvoker();
            client = Activator.CreateInstance(clientType, callInvoker);
        }
        catch
        {
            return new List<LockRow>();
        }

        if (client is null) return new List<LockRow>();

        var method = clientType.GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(m => string.Equals(m.Name, "AccountLocksAsync", StringComparison.Ordinal)) ??
            clientType.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .FirstOrDefault(m => string.Equals(m.Name, "AccountLocks", StringComparison.Ordinal));

        if (method is null) return new List<LockRow>();

        var requestParam = method.GetParameters().FirstOrDefault();
        if (requestParam is null) return new List<LockRow>();

        object? request;
        try
        {
            request = Activator.CreateInstance(requestParam.ParameterType);
        }
        catch
        {
            return new List<LockRow>();
        }

        if (request is null) return new List<LockRow>();

        if (!TrySetOwnerOnRequest(request, address))
            return new List<LockRow>();

        var response = await InvokeGrpcUnaryAsync(client, method, request, ct, BuildHeightMetadata(height));
        if (response is null) return new List<LockRow>();

        var locksObj = GetMemberValue(response, "Locks") ?? GetMemberValue(response, "LocksList");
        if (locksObj is not System.Collections.IEnumerable locks)
            return new List<LockRow>();

        var rows = new List<LockRow>();
        foreach (var lockItem in locks)
        {
            if (lockItem is null) continue;

            var amountUnits = SumCoinsFromLock(lockItem, denomWantedUpper);
            if (amountUnits <= 0m) continue;

            var endTime = TryGetTimestamp(GetMemberValue(lockItem, "EndTime"));
            var startTime = TryGetTimestamp(GetMemberValue(lockItem, "StartTime"));
            var duration = TryGetDuration(GetMemberValue(lockItem, "Duration"));

            if (!endTime.HasValue && startTime.HasValue && duration.HasValue)
                endTime = startTime.Value + duration.Value;

            if (!endTime.HasValue) continue;

            if (!duration.HasValue && startTime.HasValue)
                duration = endTime.Value - startTime.Value;

            rows.Add(new LockRow(
                "LOCKUP_MODULE",
                amountUnits / scaleInt,
                startTime,
                endTime.Value,
                duration));
        }

        return rows;
    }

    private async Task<List<LockRow>> TryGetOptioLockupLocksAsync(
        string address,
        string denomWantedUpper,
        int scaleInt,
        CancellationToken ct,
        long? height = null)
    {
        try
        {
            var client = new Query.QueryClient(_channel);
            var metadata = BuildHeightMetadata(height);
            var response = await client.LocksAsync(
                new QueryLocksRequest { Address = address },
                metadata,
                cancellationToken: ct).ResponseAsync;

            if (response?.Locks is null || response.Locks.Count == 0)
                return new List<LockRow>();

            var rows = new List<LockRow>(response.Locks.Count);
            foreach (var lockItem in response.Locks)
            {
                var amountUnits = 0m;
                if (lockItem.Amount is not null &&
                    lockItem.Amount.Denom.Equals(denomWantedUpper, StringComparison.OrdinalIgnoreCase) &&
                    ServiceUtils.TryParseAmount(lockItem.Amount.Amount, out var parsed))
                {
                    amountUnits = parsed;
                }

                if (amountUnits <= 0m) continue;

                var endTime = TryParseUnlockDate(lockItem.UnlockDate);
                if (!endTime.HasValue) continue;

                rows.Add(new LockRow(
                    "LOCKUP_MODULE",
                    amountUnits / scaleInt,
                    null,
                    endTime.Value,
                    null));
            }

            return rows;
        }
        catch
        {
            return new List<LockRow>();
        }
    }

    private async Task<List<LockRow>> TryGetOptioLockupActiveLocksAsync(
        string denomWantedUpper,
        int scaleInt,
        CancellationToken ct,
        long? height = null)
    {
        try
        {
            var client = new Query.QueryClient(_channel);
            var rows = new List<LockRow>();
            var metadata = BuildHeightMetadata(height);
            ByteString? nextKey = null;

            do
            {
                var request = new QueryActiveLocksRequest();
                if (nextKey is not null)
                {
                    request.Pagination = new Cosmos.Base.Query.V1Beta1.PageRequest
                    {
                        Key = nextKey,
                        Limit = 2000,
                    };
                }
                else
                {
                    request.Pagination = new Cosmos.Base.Query.V1Beta1.PageRequest
                    {
                        Limit = 2000,
                    };
                }

                var response = await client.ActiveLocksAsync(request, metadata, cancellationToken: ct).ResponseAsync;
                if (response?.Locks is null || response.Locks.Count == 0)
                    break;

                foreach (var lockItem in response.Locks)
                {
                    var amountUnits = 0m;
                    if (lockItem.Amount is not null &&
                        lockItem.Amount.Denom.Equals(denomWantedUpper, StringComparison.OrdinalIgnoreCase) &&
                        ServiceUtils.TryParseAmount(lockItem.Amount.Amount, out var parsed))
                    {
                        amountUnits = parsed;
                    }

                    if (amountUnits <= 0m) continue;

                    var endTime = TryParseUnlockDate(lockItem.UnlockDate);
                    if (!endTime.HasValue) continue;

                    rows.Add(new LockRow(
                        "LOCKUP_MODULE",
                        amountUnits / scaleInt,
                        null,
                        endTime.Value,
                        null));
                }

                nextKey = response.Pagination?.NextKey;
            }
            while (nextKey is not null && nextKey.Length > 0);

            return rows;
        }
        catch
        {
            return new List<LockRow>();
        }
    }

    private async Task<List<LockRow>> GetVestingDerivedLocksAsync(
        string address,
        string denomWantedUpper,
        int scaleInt,
        CancellationToken ct,
        long? height = null)
    {
        using var doc = await ServiceUtils.TryGetJsonAsync(
            _http,
            $"cosmos/auth/v1beta1/accounts/{address}",
            height,
            ct);

        if (doc is null) return new List<LockRow>();

        if (!doc.RootElement.TryGetProperty("account", out var account) ||
            account.ValueKind != JsonValueKind.Object)
            return new List<LockRow>();

        var typeStr = account.TryGetProperty("@type", out var typeEl) ? typeEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(typeStr) ||
            !typeStr.Contains("VestingAccount", StringComparison.OrdinalIgnoreCase))
            return new List<LockRow>();

        if (!account.TryGetProperty("base_vesting_account", out var baseVesting) ||
            baseVesting.ValueKind != JsonValueKind.Object)
            return new List<LockRow>();

        var originalVestingUnits = baseVesting.TryGetProperty("original_vesting", out var orig)
            ? ServiceUtils.SumCoins(orig, denomWantedUpper)
            : 0m;

        var delegatedFreeUnits = baseVesting.TryGetProperty("delegated_free", out var df)
            ? ServiceUtils.SumCoins(df, denomWantedUpper)
            : 0m;

        var endTime = baseVesting.TryGetProperty("end_time", out var endEl)
            ? ServiceUtils.TryParseUnixSeconds(endEl)
            : null;

        var startTime = account.TryGetProperty("start_time", out var startEl)
            ? ServiceUtils.TryParseUnixSeconds(startEl)
            : null;

        var nowUtc = DateTimeOffset.UtcNow;
        var rowsUnits = new List<(decimal AmountUnits, DateTimeOffset? Start, DateTimeOffset End, TimeSpan? Duration)>();

        if (typeStr.Contains("PeriodicVestingAccount", StringComparison.OrdinalIgnoreCase))
        {
            if (!startTime.HasValue) return new List<LockRow>();
            if (!account.TryGetProperty("vesting_periods", out var periods) ||
                periods.ValueKind != JsonValueKind.Array)
                return new List<LockRow>();

            long elapsedSec = 0;
            foreach (var period in periods.EnumerateArray())
            {
                if (!period.TryGetProperty("length", out var lenEl)) continue;
                long lenSec = 0;
                if (lenEl.ValueKind == JsonValueKind.Number && lenEl.TryGetInt64(out var l))
                    lenSec = l;
                else if (lenEl.ValueKind == JsonValueKind.String &&
                         long.TryParse(lenEl.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ls))
                    lenSec = ls;

                if (lenSec <= 0) continue;

                var amountUnits = period.TryGetProperty("amount", out var amtEl)
                    ? ServiceUtils.SumCoins(amtEl, denomWantedUpper)
                    : 0m;

                if (amountUnits <= 0m) continue;

                var periodStart = startTime.Value.AddSeconds(elapsedSec);
                elapsedSec += lenSec;
                var periodEnd = startTime.Value.AddSeconds(elapsedSec);

                if (periodEnd <= nowUtc) continue;

                rowsUnits.Add((amountUnits, periodStart, periodEnd, TimeSpan.FromSeconds(lenSec)));
            }

            if (delegatedFreeUnits > 0m)
                rowsUnits = ApplyDelegatedFree(rowsUnits, delegatedFreeUnits);
        }
        else if (typeStr.Contains("ContinuousVestingAccount", StringComparison.OrdinalIgnoreCase))
        {
            if (!endTime.HasValue) return new List<LockRow>();

            var totalSeconds = startTime.HasValue
                ? (endTime.Value - startTime.Value).TotalSeconds
                : 0d;

            decimal lockedUnits;
            if (!startTime.HasValue || totalSeconds <= 0d)
            {
                lockedUnits = nowUtc < endTime.Value ? originalVestingUnits : 0m;
            }
            else if (nowUtc <= startTime.Value)
            {
                lockedUnits = originalVestingUnits;
            }
            else if (nowUtc >= endTime.Value)
            {
                lockedUnits = 0m;
            }
            else
            {
                var remainingRatio = (decimal)(endTime.Value - nowUtc).TotalSeconds / (decimal)totalSeconds;
                lockedUnits = originalVestingUnits * remainingRatio;
            }

            lockedUnits = Math.Max(0m, lockedUnits - delegatedFreeUnits);

            if (lockedUnits > 0m)
            {
                rowsUnits.Add((lockedUnits, startTime, endTime.Value,
                    startTime.HasValue ? endTime.Value - startTime.Value : null));
            }
        }
        else if (typeStr.Contains("DelayedVestingAccount", StringComparison.OrdinalIgnoreCase))
        {
            if (!endTime.HasValue) return new List<LockRow>();

            var lockedUnits = nowUtc < endTime.Value ? originalVestingUnits : 0m;
            lockedUnits = Math.Max(0m, lockedUnits - delegatedFreeUnits);

            if (lockedUnits > 0m)
            {
                rowsUnits.Add((lockedUnits, startTime, endTime.Value,
                    startTime.HasValue ? endTime.Value - startTime.Value : null));
            }
        }
        else
        {
            if (!endTime.HasValue) return new List<LockRow>();

            var lockedUnits = nowUtc < endTime.Value ? originalVestingUnits : 0m;
            lockedUnits = Math.Max(0m, lockedUnits - delegatedFreeUnits);

            if (lockedUnits > 0m)
            {
                rowsUnits.Add((lockedUnits, startTime, endTime.Value,
                    startTime.HasValue ? endTime.Value - startTime.Value : null));
            }
        }

        return rowsUnits
            .Where(r => r.AmountUnits > 0m)
            .Select(r => new LockRow(
                "VESTING_DERIVED",
                r.AmountUnits / scaleInt,
                r.Start,
                r.End,
                r.Duration))
            .ToList();
    }

    private static List<(decimal AmountUnits, DateTimeOffset? Start, DateTimeOffset End, TimeSpan? Duration)> ApplyDelegatedFree(
        List<(decimal AmountUnits, DateTimeOffset? Start, DateTimeOffset End, TimeSpan? Duration)> rows,
        decimal delegatedFreeUnits)
    {
        var remaining = delegatedFreeUnits;
        var sorted = rows.OrderBy(r => r.End).ToList();

        for (int i = 0; i < sorted.Count && remaining > 0m; i++)
        {
            var row = sorted[i];
            if (remaining >= row.AmountUnits)
            {
                remaining -= row.AmountUnits;
                row.AmountUnits = 0m;
            }
            else
            {
                row.AmountUnits -= remaining;
                remaining = 0m;
            }
            sorted[i] = row;
        }

        return sorted;
    }

    private static decimal SumCoinsFromLock(object lockItem, string denomWantedUpper)
    {
        var coinsObj = GetMemberValue(lockItem, "Coins") ?? GetMemberValue(lockItem, "CoinsList");
        if (coinsObj is not System.Collections.IEnumerable coins)
            return 0m;

        decimal total = 0m;
        foreach (var coin in coins)
        {
            if (coin is null) continue;
            var denom = GetMemberValue(coin, "Denom") as string;
            if (string.IsNullOrWhiteSpace(denom) ||
                !denom.Equals(denomWantedUpper, StringComparison.OrdinalIgnoreCase))
                continue;

            var amountStr = GetMemberValue(coin, "Amount") as string;
            if (!ServiceUtils.TryParseAmount(amountStr, out var amount))
                continue;

            total += amount;
        }

        return total;
    }

    private static object? GetMemberValue(object instance, string name)
    {
        var type = instance.GetType();
        var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (prop is not null)
            return prop.GetValue(instance);

        var field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
        return field?.GetValue(instance);
    }

    private static bool TrySetOwnerOnRequest(object request, string address)
    {
        var type = request.GetType();
        var ownerProp = type.GetProperty("Owner", BindingFlags.Public | BindingFlags.Instance);
        if (ownerProp is not null && ownerProp.CanWrite)
        {
            ownerProp.SetValue(request, address);
            return true;
        }

        var ownerField = type.GetField("Owner", BindingFlags.Public | BindingFlags.Instance);
        if (ownerField is not null)
        {
            ownerField.SetValue(request, address);
            return true;
        }

        return false;
    }

    private static async Task<object?> InvokeGrpcUnaryAsync(
        object client,
        MethodInfo method,
        object request,
        CancellationToken ct,
        Metadata? metadata)
    {
        object?[] args = new object?[method.GetParameters().Length];
        var parameters = method.GetParameters();

        for (int i = 0; i < parameters.Length; i++)
        {
            if (i == 0)
            {
                args[i] = request;
                continue;
            }

            if (parameters[i].ParameterType == typeof(CancellationToken))
            {
                args[i] = ct;
                continue;
            }

            if (parameters[i].ParameterType == typeof(Metadata))
            {
                args[i] = metadata;
                continue;
            }

            args[i] = null;
        }

        object? result;
        try
        {
            result = method.Invoke(client, args);
        }
        catch
        {
            return null;
        }

        if (result is null) return null;

        if (result is Task task)
        {
            await task.ConfigureAwait(false);
            return task.GetType().GetProperty("Result")?.GetValue(task);
        }

        var responseAsyncProp = result.GetType().GetProperty("ResponseAsync");
        if (responseAsyncProp?.GetValue(result) is Task responseTask)
        {
            await responseTask.ConfigureAwait(false);
            return responseTask.GetType().GetProperty("Result")?.GetValue(responseTask);
        }

        return result;
    }

    private static Type? FindLockupQueryClientType()
    {
        const string knownType = "Optio.Lockup.Query+QueryClient";
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var type = asm.GetType(knownType, false);
            if (type is not null) return type;
        }

        const string osmosisType = "Osmosis.Lockup.Query.Query+QueryClient";
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var type = asm.GetType(osmosisType, false);
            if (type is not null) return type;
        }

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try
            {
                types = asm.GetTypes();
            }
            catch
            {
                continue;
            }

            foreach (var type in types)
            {
                var name = type.FullName;
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (name.EndsWith(".Lockup.Query.Query+QueryClient", StringComparison.Ordinal))
                    return type;
            }
        }

        return null;
    }

    private static DateTimeOffset? TryParseUnlockDate(string unlockDate)
    {
        if (string.IsNullOrWhiteSpace(unlockDate)) return null;

        if (DateTimeOffset.TryParseExact(
            unlockDate,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed))
        {
            return parsed;
        }

        if (DateTimeOffset.TryParse(
            unlockDate,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out parsed))
        {
            return parsed;
        }

        return null;
    }

    private static int GetRemainingMonths(DateTimeOffset endTime, DateTimeOffset nowUtc)
    {
        if (endTime <= nowUtc) return 0;

        var months = (endTime.Year - nowUtc.Year) * 12 + endTime.Month - nowUtc.Month;
        if (endTime.Day < nowUtc.Day)
            months--;

        return Math.Max(0, months);
    }

    private static DateTimeOffset? TryGetTimestamp(object? value)
    {
        if (value is null) return null;

        if (value is DateTimeOffset dto)
            return dto;

        if (value is DateTime dt)
            return new DateTimeOffset(dt, TimeSpan.Zero);

        var method = value.GetType().GetMethod("ToDateTimeOffset", BindingFlags.Public | BindingFlags.Instance);
        if (method is not null)
            return (DateTimeOffset?)method.Invoke(value, null);

        method = value.GetType().GetMethod("ToDateTime", BindingFlags.Public | BindingFlags.Instance);
        if (method is not null)
        {
            if (method.Invoke(value, null) is DateTime t)
                return new DateTimeOffset(t, TimeSpan.Zero);
        }

        return null;
    }

    private static TimeSpan? TryGetDuration(object? value)
    {
        if (value is null) return null;
        if (value is TimeSpan ts) return ts;

        var method = value.GetType().GetMethod("ToTimeSpan", BindingFlags.Public | BindingFlags.Instance);
        if (method is not null)
            return (TimeSpan?)method.Invoke(value, null);

        return null;
    }

    public async Task<List<LockRow>> GetLocksAsync(string address, CancellationToken ct, long? height = null)
    {
        // Use default denom "uopt" and scale factor of 1_000_000
        return await GetLockRowsAsync(address, "uopt", 1_000_000, ct, height);
    }

    private static Metadata? BuildHeightMetadata(long? height)
    {
        if (!height.HasValue || height.Value <= 0) return null;
        return new Metadata { { "x-cosmos-block-height", height.Value.ToString(CultureInfo.InvariantCulture) } };
    }
}

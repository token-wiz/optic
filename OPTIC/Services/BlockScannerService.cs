using System.Globalization;
using System.Text.Json;
using Grpc.Net.Client;

namespace OPTIC.Services;

public class BlockScannerService
{
    private readonly HttpClient _http;
    private readonly HttpClient? _rpcHttp;
    private readonly GrpcChannel _grpcChannel;
    private const decimal ScaleInt = 1_000_000m; // uOPT to OPT
    private const int DefaultBackfillParallelism = 8;
    private const string DistributionSourceAddress = "optio103r5ejt3gqyghvyel86hs5faru55r3w9kl4dst";
    private const string FeeRecipientAddress = "optio17xpfvakm2amg962yls6f84z3kell8c5l9yn0qn";

    public BlockScannerService(HttpClient http, GrpcChannel? grpcChannel = null, HttpClient? rpcHttp = null)
    {
        _http = http;
        _rpcHttp = rpcHttp;
        _grpcChannel = grpcChannel ?? GrpcChannel.ForAddress("http://127.0.0.1:9090");
    }

    /// <summary>
    /// Scans blocks from startBlock to endBlock (inclusive) and aggregates daily statistics.
    /// Calls the onDayComplete callback when a new day is encountered.
    /// Calls the onProgress callback periodically with current day stats.
    /// </summary>
    public async Task<int> ScanBlocksAsync(
        long startBlock,
        long endBlock,
        string denomWantedUpper,
        Func<DailyStatsEntry, Task> onDayComplete,
        Func<DailyStatsEntry, Task>? onProgress = null,
        CancellationToken ct = default)
    {
        var daysProcessed = 0;
        var bankService = new BankService(_http);
        var stakingService = new StakingService(_http);
        var lockService = new LockService(_http, _grpcChannel);
        
        // Cumulative set of all unique addresses seen across all days
        var allAddressesEverSeen = new HashSet<string>();
        
        var currentDayStats = new Dictionary<string, object>
        {
            { "date", "" },
            { "startBlock", 0L },
            { "endBlock", 0L },
            { "addresses", new HashSet<string>() },
            { "distributionRecipients", new HashSet<string>() },
            { "distributedUopt", 0m },
            { "txCount", 0 },
            { "blockCount", 0L }
        };

        string? currentDate = null;

        for (long blockHeight = startBlock; blockHeight <= endBlock; blockHeight++)
        {
            try
            {
                var blockDoc = await ServiceUtils.TryGetJsonAsync(_http, $"cosmos/base/tendermint/v1beta1/blocks/{blockHeight}", ct);
                if (blockDoc == null)
                    continue;

                // Extract block header
                if (!blockDoc.RootElement.TryGetProperty("block", out var blockEl) ||
                    !blockEl.TryGetProperty("header", out var headerEl))
                    continue;

                // Get block time
                string? blockDate = null;
                if (headerEl.TryGetProperty("time", out var timeEl))
                {
                    var timeStr = timeEl.GetString() ?? "";
                    if (DateTime.TryParse(timeStr, out var blockTime))
                        blockDate = blockTime.ToString("yyyy-MM-dd");
                }

                if (string.IsNullOrEmpty(blockDate))
                    continue;

                // Date transition - save previous day's stats
                if (currentDate != null && currentDate != blockDate)
                {
                    var entry = await BuildDailyStatsEntryAsync(currentDayStats, currentDate, denomWantedUpper, bankService, stakingService, lockService, allAddressesEverSeen, ct);
                    await onDayComplete(entry);
                    daysProcessed++;

                    // Reset for new day, but keep the cumulative addresses
                    currentDayStats = new Dictionary<string, object>
                    {
                        { "date", blockDate },
                        { "startBlock", blockHeight },
                        { "endBlock", blockHeight },
                        { "addresses", new HashSet<string>() },
                        { "distributionRecipients", new HashSet<string>() },
                        { "distributedUopt", 0m },
                        { "txCount", 0 },
                        { "blockCount", 0L }
                    };
                }

                currentDate = blockDate;
                if (string.IsNullOrEmpty(currentDayStats["date"].ToString()))
                    currentDayStats["date"] = blockDate;

                currentDayStats["endBlock"] = blockHeight;
                currentDayStats["blockCount"] = ((long)currentDayStats["blockCount"]) + 1;
                await AddDistributionStatsFromBlockResultsAsync(blockHeight, denomWantedUpper, currentDayStats, ct);

                // Extract transactions from this block
                if (blockEl.TryGetProperty("data", out var dataEl) && dataEl.TryGetProperty("txs", out var txsEl) && txsEl.ValueKind == JsonValueKind.Array)
                {
                    currentDayStats["txCount"] = (int)currentDayStats["txCount"] + txsEl.GetArrayLength();

                    // Track addresses from transactions
                    var todayAddresses = (HashSet<string>)currentDayStats["addresses"];
                    foreach (var txBase64 in txsEl.EnumerateArray())
                    {
                        var txStr = txBase64.GetString() ?? "";
                        if (!string.IsNullOrEmpty(txStr))
                        {
                            try
                            {
                                var txBytes = Convert.FromBase64String(txStr);
                                // Basic parsing: look for bech32 addresses in the tx
                                var txText = System.Text.Encoding.UTF8.GetString(txBytes);
                                ExtractAddresses(txText, todayAddresses);
                                ExtractAddresses(txText, allAddressesEverSeen);
                            }
                            catch { }
                        }
                    }
                }

                // Progress callback: report current day stats every 100 blocks or when day ends
                if (onProgress != null && (blockHeight % 100 == 0 || blockHeight == endBlock))
                {
                    var progressEntry = BuildDailyStatsEntry(currentDayStats, currentDate, denomWantedUpper);
                    await onProgress(progressEntry);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Error processing block {blockHeight}: {ex.Message}");
            }
        }

        // Save final day if data exists
        if (currentDate != null && (int)currentDayStats["txCount"] > 0)
        {
            var entry = await BuildDailyStatsEntryAsync(currentDayStats, currentDate, denomWantedUpper, bankService, stakingService, lockService, allAddressesEverSeen, ct);
            await onDayComplete(entry);
            daysProcessed++;
        }

        return daysProcessed;
    }

    private DailyStatsEntry BuildDailyStatsEntry(Dictionary<string, object> dayStats, string dateStr, string denomWantedUpper)
    {
        var addresses = (HashSet<string>)dayStats["addresses"];
        var distributionRecipients = (HashSet<string>)dayStats["distributionRecipients"];
        var distributedUopt = (decimal)dayStats["distributedUopt"];
        
        return new DailyStatsEntry
        {
            Date = dateStr,
            StartBlockNumber = (int)Math.Min((long)dayStats["startBlock"], int.MaxValue),
            EndBlockNumber = (int)Math.Min((long)dayStats["endBlock"], int.MaxValue),
            TotalWallets = addresses.Count > 0 ? addresses.Count : null,
            ActiveWallets = addresses.Count > 0 ? Math.Max(1, addresses.Count / 2) : null,
            TxCount = (int)dayStats["txCount"] > 0 ? (int)dayStats["txCount"] : null,
            TotalSupply = null, // Will be fetched asynchronously
            TotalStaked = null,
            TotalLocked = null,
            TotalUnbonding = null,
            TotalLiquid = null,
            DistributedOpt = distributedUopt > 0 ? distributedUopt : null,
            EmittedOpt = null,
            NetEmittedOpt = distributedUopt > 0 ? -distributedUopt : null,
            Lock6m = null,
            Lock12m = null,
            Lock18m = null,
            Lock24m = null,
            LockOther = null,
            SentOpt = null,
            RecvOpt = null,
            UniqueCounterparties = distributionRecipients.Count > 0 ? distributionRecipients.Count : null
        };
    }

    private async Task<DailyStatsEntry> BuildDailyStatsEntryAsync(Dictionary<string, object> dayStats, string dateStr, string denomWantedUpper, BankService bankService, StakingService stakingService, LockService lockService, HashSet<string> allAddressesEverSeen, CancellationToken ct)
    {
        // TotalWallets is the count of unique addresses seen up to this day
        int totalWalletCount = allAddressesEverSeen.Count;

        var endBlock = (long)dayStats["endBlock"];
        var height = endBlock > 0 ? endBlock : (long?)null;

        decimal totalSupply = 0;
        decimal totalStaked = 0;
        decimal totalUnbonding = 0;
        decimal totalLocked = 0;
        var distributionRecipients = (HashSet<string>)dayStats["distributionRecipients"];
        var distributedUopt = (decimal)dayStats["distributedUopt"];

        try
        {
            var parallelism = DefaultBackfillParallelism;
            var parallelEnv = Environment.GetEnvironmentVariable("OPTIC_BACKFILL_PARALLELISM");
            if (int.TryParse(parallelEnv, out var parsed))
                parallelism = Math.Clamp(parsed, 1, 64);

            var totalsLock = new object();
            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = parallelism,
                CancellationToken = ct
            };

            await Parallel.ForEachAsync(allAddressesEverSeen, options, async (address, token) =>
            {
                decimal localSupply = 0m;
                decimal localStaked = 0m;
                decimal localUnbonding = 0m;
                decimal localLocked = 0m;

                try
                {
                    var balance = await bankService.GetBalanceAsync(address, denomWantedUpper, token, height);
                    if (balance > 0)
                        localSupply += balance;
                }
                catch
                {
                }

                try
                {
                    var staked = await stakingService.GetDelegationTotalAsync(address, token, height);
                    if (staked > 0)
                        localStaked += staked;
                }
                catch
                {
                }

                try
                {
                    var unbonding = await stakingService.GetUnbondingTotalAsync(address, token, height);
                    if (unbonding > 0)
                        localUnbonding += unbonding;
                }
                catch
                {
                }

                try
                {
                    var locks = await lockService.GetLocksAsync(address, token, height);
                    foreach (var lockRow in locks)
                        localLocked += lockRow.AmountOpt;
                }
                catch
                {
                }

                if (localSupply == 0m && localStaked == 0m && localUnbonding == 0m && localLocked == 0m)
                    return;

                lock (totalsLock)
                {
                    totalSupply += localSupply;
                    totalStaked += localStaked;
                    totalUnbonding += localUnbonding;
                    totalLocked += localLocked;
                }
            });
        }
        catch
        {
            // If something goes wrong with the whole batch, just use zeros
        }

        // Calculate total liquid plus (Supply + Staked + Unbonding)
        decimal? totalLiquidPlus = null;
        if (totalSupply > 0 || totalStaked > 0 || totalUnbonding > 0)
            totalLiquidPlus = totalSupply + totalStaked + totalUnbonding;

        return new DailyStatsEntry
        {
            Date = dateStr,
            StartBlockNumber = (int)Math.Min((long)dayStats["startBlock"], int.MaxValue),
            EndBlockNumber = (int)Math.Min((long)dayStats["endBlock"], int.MaxValue),
            TotalWallets = totalWalletCount > 0 ? totalWalletCount : null,
            ActiveWallets = totalWalletCount > 0 ? Math.Max(1, totalWalletCount / 2) : null,
            TxCount = (int)dayStats["txCount"] > 0 ? (int)dayStats["txCount"] : null,
            TotalSupply = totalSupply > 0 ? totalSupply : null,
            TotalStaked = totalStaked > 0 ? totalStaked : null,
            TotalUnbonding = totalUnbonding > 0 ? totalUnbonding : null,
            TotalLocked = totalLocked > 0 ? totalLocked : null,
            TotalLiquid = null,
            TotalLiquidPlus = totalLiquidPlus,
            DistributedOpt = distributedUopt > 0 ? distributedUopt : null,
            EmittedOpt = null,
            NetEmittedOpt = distributedUopt > 0 ? -distributedUopt : null,
            Lock6m = null,
            Lock12m = null,
            Lock18m = null,
            Lock24m = null,
            LockOther = null,
            SentOpt = null,
            RecvOpt = null,
            UniqueCounterparties = distributionRecipients.Count > 0 ? distributionRecipients.Count : null
        };
    }

    private void ExtractAddresses(string text, HashSet<string> addresses)
    {
        // Simple regex to find bech32 addresses (optio1...)
        var matches = System.Text.RegularExpressions.Regex.Matches(text, @"optio1[a-z0-9]{38,58}");
        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            addresses.Add(match.Value);
        }
    }

    private async Task AddDistributionStatsFromBlockResultsAsync(
        long blockHeight,
        string denomWantedUpper,
        Dictionary<string, object> dayStats,
        CancellationToken ct)
    {
        if (_rpcHttp is null)
            return;

        using var blockResults = await ServiceUtils.TryGetJsonAsync(_rpcHttp, $"block_results?height={blockHeight}", ct);
        if (blockResults is null)
            return;

        if (!blockResults.RootElement.TryGetProperty("result", out var result) ||
            !result.TryGetProperty("txs_results", out var txResults) ||
            txResults.ValueKind != JsonValueKind.Array)
            return;

        var recipients = (HashSet<string>)dayStats["distributionRecipients"];
        var distributedUopt = (decimal)dayStats["distributedUopt"];

        foreach (var tx in txResults.EnumerateArray())
        {
            if (!tx.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var ev in events.EnumerateArray())
            {
                var type = ev.TryGetProperty("type", out var typeEl) ? typeEl.GetString() ?? "" : "";
                if (!string.Equals(type, "transfer", StringComparison.Ordinal))
                    continue;

                var attrs = ReadEventAttributes(ev);
                foreach (var entry in ParseTransferEventTuples(attrs, denomWantedUpper))
                {
                    if (!entry.Sender.Equals(DistributionSourceAddress, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (entry.Recipient.Equals(DistributionSourceAddress, StringComparison.OrdinalIgnoreCase) ||
                        entry.Recipient.Equals(FeeRecipientAddress, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (entry.AmountUopt <= 0m)
                        continue;

                    distributedUopt += entry.AmountUopt;
                    recipients.Add(entry.Recipient);
                }
            }
        }

        dayStats["distributedUopt"] = distributedUopt;
    }

    private static List<(string Key, string Value)> ReadEventAttributes(JsonElement ev)
    {
        var attrs = new List<(string Key, string Value)>();
        if (!ev.TryGetProperty("attributes", out var attrArray) || attrArray.ValueKind != JsonValueKind.Array)
            return attrs;

        foreach (var attr in attrArray.EnumerateArray())
        {
            var key = attr.TryGetProperty("key", out var keyEl) ? keyEl.GetString() ?? "" : "";
            var value = attr.TryGetProperty("value", out var valueEl) ? valueEl.GetString() ?? "" : "";
            key = DecodeBase64Maybe(key);
            value = DecodeBase64Maybe(value);
            if (key.Length > 0)
                attrs.Add((key, value));
        }

        return attrs;
    }

    private static List<(string Sender, string Recipient, decimal AmountUopt)> ParseTransferEventTuples(
        List<(string Key, string Value)> attrs,
        string denomWantedUpper)
    {
        var entries = new List<(string Sender, string Recipient, decimal AmountUopt)>();
        var senders = new List<string>();
        var recipients = new List<string>();
        var amountStrings = new List<string>();

        foreach (var (key, value) in attrs)
        {
            if (key == "sender" && !string.IsNullOrWhiteSpace(value))
                senders.Add(value);
            else if (key == "recipient" && !string.IsNullOrWhiteSpace(value))
                recipients.Add(value);
            else if (key == "amount" && !string.IsNullOrWhiteSpace(value))
                amountStrings.Add(value);
        }

        var count = Math.Min(Math.Min(senders.Count, recipients.Count), amountStrings.Count);
        for (var i = 0; i < count; i++)
        {
            var amount = ParseAmountFieldUopt(amountStrings[i], denomWantedUpper);
            if (amount > 0m)
                entries.Add((senders[i], recipients[i], amount));
        }

        return entries;
    }

    private static decimal ParseAmountFieldUopt(string amountField, string denomWantedUpper)
    {
        if (string.IsNullOrWhiteSpace(amountField))
            return 0m;

        var total = 0m;
        foreach (var part in amountField.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (TryParseSingleCoinUopt(part, denomWantedUpper, out var amountUnits))
                total += amountUnits;
        }

        return total;
    }

    private static bool TryParseSingleCoinUopt(string part, string denomWantedUpper, out decimal amountUnits)
    {
        amountUnits = 0m;
        if (string.IsNullOrWhiteSpace(part))
            return false;

        var trimmed = part.Trim();
        var i = 0;
        if (trimmed[0] == '-')
            i = 1;

        for (; i < trimmed.Length; i++)
        {
            if (!char.IsDigit(trimmed[i]))
                break;
        }

        if (i == 0 || i >= trimmed.Length)
            return false;

        var denom = trimmed[i..];
        if (!string.Equals(denom.ToUpperInvariant(), denomWantedUpper, StringComparison.Ordinal))
            return false;

        return decimal.TryParse(trimmed[..i].Replace("_", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out amountUnits);
    }

    private static string DecodeBase64Maybe(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;

        try
        {
            var normalized = value.Trim();
            var mod = normalized.Length % 4;
            if (mod != 0)
                normalized += new string('=', 4 - mod);

            var bytes = Convert.FromBase64String(normalized);
            var decoded = System.Text.Encoding.UTF8.GetString(bytes);
            return IsMostlyPrintable(decoded) ? decoded : value;
        }
        catch
        {
            return value;
        }
    }

    private static bool IsMostlyPrintable(string value)
    {
        foreach (var ch in value)
        {
            if (ch is '\r' or '\n' or '\t')
                continue;
            if (ch < 32 || ch > 126)
                return false;
        }

        return value.Length > 0;
    }
}

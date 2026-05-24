using System.Globalization;
using System.Text.Json;
using Grpc.Net.Client;

namespace OPTIC.Services;

public class BlockScannerService
{
    private readonly HttpClient _http;
    private readonly GrpcChannel _grpcChannel;
    private const decimal ScaleInt = 1_000_000m; // uOPT to OPT
    private const int DefaultBackfillParallelism = 8;

    public BlockScannerService(HttpClient http, GrpcChannel? grpcChannel = null)
    {
        _http = http;
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
                        { "txCount", 0 },
                        { "blockCount", 0L }
                    };
                }

                currentDate = blockDate;
                if (string.IsNullOrEmpty(currentDayStats["date"].ToString()))
                    currentDayStats["date"] = blockDate;

                currentDayStats["endBlock"] = blockHeight;
                currentDayStats["blockCount"] = ((long)currentDayStats["blockCount"]) + 1;

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
            DistributedOpt = null,
            EmittedOpt = null,
            NetEmittedOpt = null,
            Lock6m = null,
            Lock12m = null,
            Lock18m = null,
            Lock24m = null,
            LockOther = null,
            SentOpt = null,
            RecvOpt = null,
            UniqueCounterparties = null
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
            DistributedOpt = null,
            EmittedOpt = null,
            NetEmittedOpt = null,
            Lock6m = null,
            Lock12m = null,
            Lock18m = null,
            Lock24m = null,
            LockOther = null,
            SentOpt = null,
            RecvOpt = null,
            UniqueCounterparties = null
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
}

using System.Globalization;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Numerics;
using System.Threading;
using OPTIC.Services;
using Google.Protobuf;
using Grpc.Net.Client;
using Cosmos.Bank.V1Beta1;
using Cosmos.Tx.V1Beta1;
using Cosmos.Base.Query.V1Beta1;
using Cosmos.Base.Abci.V1Beta1;

// ======================================================================
// TOP-LEVEL PROGRAM (all executable statements are here, BEFORE types)
// ======================================================================

if (ParseHelpArg(args))
{
    PrintHelp();
    return;
}

if (ParseWebArg(args))
{
    var webPort = ParseWebPortArg(args) ?? 5070;
    var webHost = ParseWebHostArg(args);
    if (string.IsNullOrWhiteSpace(webHost))
        webHost = "127.0.0.1";

    await WebDashboard.RunAsync(webHost, webPort);
    return;
}

var dailySyncMode = ParseDailySyncArg(args);
var backfillMode = ParseBackfillArg(args, out var backfillForce);

bool IsMultiSendSumEarly = ParseMultiSendSumArg(args);

if (!IsMultiSendSumEarly)
{
    Console.WriteLine("OPTIC - Optio Protocol Telemetry & Intelligence Center");
    Console.WriteLine("Address ledger (sent/recv, from/to, amount, fee, running balance)");
}

// ---------------- Defaults ----------------
const string DefaultGrpc = "127.0.0.1:9090";
const string DefaultRpcBase = "http://127.0.0.1:26657"; // CometBFT RPC (no LCD)
const string DefaultLcdBase = "http://127.0.0.1:1317";
const string DefaultDenom = "uopt";
const int DefaultLookbackDays = 3650;
const int DefaultPageLimit = 100;
const int DefaultMaxPages = 50000;
const string ConfigFile = @"C:\workspace\optic\optic\optic.conf";
const string DebugHash1 = "F91D4AF68E6B0CCF3EE514D88AC0E5AA04E4CC82564B54CEA2B0046BF7C4265B";
const string DistributionSourceAddress = "optio1u28a7gj785a4d8g9n70wqujl8xc9wh7dyj9gen";
bool HideStakingRewards = false;
bool HideFeeRows = true;
bool OnlyRecvTransactions = false;
bool TotalsOnly = ParseTotalsOnlyArg(args);
bool IncludeTotals = ParseIncludeTotalsArg(args);
bool NetworkTotals = ParseNetworkTotalsArg(args);
bool DryTotals = ParseDryTotalsArg(args);
bool StatusMode = ParseStatusArg(args);
bool ValidatorsNodesMode = ParseValidatorsNodesArg(args);
bool WalletCountMode = ParseWalletCountArg(args);
string emitterAddr = ParseEmitterArg(args) ?? DistributionSourceAddress;
bool MultiSendSumMode = ParseMultiSendSumArg(args);
string multiSendFromAddr = ParseMultiSendFromArg(args) ?? "optio103r5ejt3gqyghvyel86hs5faru55r3w9kl4dst";
long? multiSendLookbackBlocks = ParseLookbackBlocksArg(args);
string? multiSendOutPath = ParseMultiSendOutPathArg(args);
bool BlockScanMultiSend = ParseBlockScanMultiSendArg(args);
long? BlockScanStart = ParseHeightArg(args, "--start-height");
long? BlockScanEnd = ParseHeightArg(args, "--end-height");
long? QueryHeight = ParseHeightArg(args, "--height") ?? 1062;
int BlockScanTopSenders = ParseIntArg(args, "--top-senders") ?? 20;
var BlockScanSenders = ParseSendersArg(args) ?? new List<string>
{
    "optio103r5ejt3gqyghvyel86hs5faru55r3w9kl4dst",
    "optio17xpfvakm2amg962yls6f84z3kell8c5l9yn0qn",
    "optio1u28a7gj785a4d8g9n70wqujl8xc9wh7dyj9gen",
};
bool WalletBalancesMode = ParseWalletBalancesArg(args);
var WalletBalancesCsv = ParseWalletBalancesCsvArg(args);
bool WalletLocksReportMode = ParseWalletLocksReportArg(args);
bool TotalStakedMode = ParseTotalStakedArg(args);
bool TotalDistributedMode = ParseTotalDistributedArg(args);
bool TotalsAllMode = ParseTotalsAllArg(args);
string emissionAddress = ParseEmissionAddressArg(args) ?? "optio1u28a7gj785a4d8g9n70wqujl8xc9wh7dyj9gen";
var cmcDailyYear = ParseCmcDailyYearArg(args);
var cmcOutPath = ParseCmcOutPathArg(args) ?? "optio_daily_2025_cmc.csv";
var cmcId = ParseCmcIdArg(args) ?? 35828;
bool VerboseScan = ParseVerboseScanArg(args);
var DistributionsMode = ParseDistributionsArg(args, out var DistributionsAddrOverride);
var CounterpartiesMode = ParseCounterpartiesArg(args, out var CounterpartiesAddrOverride);
var LocksMode = ParseLocksArg(args, out var LocksAddrOverride);
bool LocksSummaryMode = ParseLocksSummaryArg(args);
var SendRecvMode = ParseSendRecvArg(args, out var SendRecvAddrOverride);
var WalletLocksSummaryMode = ParseWalletLocksSummaryArg(args, out var WalletLocksSummaryAddrOverride);
var SendRecvHours = ParseSendRecvHoursArg(args) ?? 24;
bool LockExtendedReportMode = ParseLockExtendedReportArg(args);
var LockExtendedDays = ParseLockExtendedDaysArg(args) ?? 7;
bool ShowHash = ParseShowHashArg(args);
bool IncludeValidators = ParseIncludeValidatorsArg(args);
const bool EnableCometMultiSendFallback = true;
const bool EnableCometGeneralRecvFallback = true;
const bool EnableCometSentFallback = true;
const bool EnableCometCoinReceivedFallback = true;

bool IsDebugHash(string hash) =>
    hash.Equals(DebugHash1, StringComparison.OrdinalIgnoreCase);

var csvPath = ParseCsvPathArg(args);
// ---------------- Diagnostics ----------------
if (!IsMultiSendSumEarly)
{
    Console.WriteLine();
    Console.WriteLine($"CWD:          {Environment.CurrentDirectory}");
    Console.WriteLine($"Config path:  {Path.GetFullPath(ConfigFile)}");
    Console.WriteLine($"Config exists:{File.Exists(ConfigFile)}");
    Console.WriteLine();
}

// ---------------- Load Config ----------------
if (!File.Exists(ConfigFile))
{
    if (!StatusMode && !ValidatorsNodesMode && !WalletCountMode)
    {
        Console.WriteLine($"Missing config file: {ConfigFile}");
        Console.WriteLine("Example:");
        Console.WriteLine("  addr=optio1...");
        Console.WriteLine("Optional:");
        Console.WriteLine("  grpc=127.0.0.1:9090");
        Console.WriteLine("  lcd=http://127.0.0.1:1317");
        Console.WriteLine("  denom=uopt");
        Console.WriteLine("  lookbackDays=3650");
        Console.WriteLine("  pageLimit=100");
        Console.WriteLine("  maxPages=50000");
        return;
    }
}

var cfg = File.Exists(ConfigFile)
    ? LoadConfig(ConfigFile)
    : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

var grpcTarget = cfg.GetValueOrDefault("grpc", DefaultGrpc);
var rpcBase = cfg.GetValueOrDefault("rpc", DefaultRpcBase);
var lcdBase = cfg.GetValueOrDefault("lcd", DefaultLcdBase);
var denom = cfg.GetValueOrDefault("denom", DefaultDenom);

var lookbackDays = int.TryParse(cfg.GetValueOrDefault("lookbackDays"), out var d) ? d : DefaultLookbackDays;
var pageLimit = int.TryParse(cfg.GetValueOrDefault("pageLimit"), out var pl) ? pl : DefaultPageLimit;
var maxPages = int.TryParse(cfg.GetValueOrDefault("maxPages"), out var mp) ? mp : DefaultMaxPages;

if (pageLimit <= 0) pageLimit = DefaultPageLimit;
if (pageLimit > 100) pageLimit = 100;
if (maxPages <= 0) maxPages = DefaultMaxPages;

// America/New_York on Windows (fallback so it never crashes)
TimeZoneInfo tz;
try { tz = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); }
catch { tz = TimeZoneInfo.Local; }

var cutoffUtc = DateTimeOffset.UtcNow.AddDays(-lookbackDays);
var denomWantedUpper = denom.ToUpperInvariant();

// denom scale (u* usually 1e6)
var scale = denom.StartsWith("u", StringComparison.OrdinalIgnoreCase) ? 1_000_000m : 1m;
var scaleInt = denom.StartsWith("u", StringComparison.OrdinalIgnoreCase) ? 1_000_000 : 1;

if (DryTotals)
{
    RunTotalsDryRun(emitterAddr, denomWantedUpper, scaleInt);
    return;
}

// ---------------- Clients ----------------
using var channel = GrpcChannel.ForAddress($"http://{grpcTarget}");
var txClient = new Service.ServiceClient(channel); // GetTxsEvent + GetTx

using var http = new HttpClient
{
    BaseAddress = new Uri(lcdBase.TrimEnd('/') + "/"),
    Timeout = TimeSpan.FromSeconds(60),
};

using var rpcHttp = new HttpClient
{
    BaseAddress = new Uri(rpcBase.TrimEnd('/') + "/"),
    Timeout = TimeSpan.FromSeconds(60),
};

// Handle test-data mode for demonstration purposes
if (args.Any(a => string.Equals(a, "--test-data", StringComparison.OrdinalIgnoreCase)))
{
    Console.WriteLine("OPTIC - Seed Test Data");
    try
    {
        var syncService = new LocalDataSyncService();
        await syncService.InitializeAsync();
        Console.WriteLine("Seeding sample data...");
        
        // Create sample data for the last 30 days
        var testData = new List<DailyStatsEntry>();
        var baseDate = DateTime.UtcNow.Date.AddDays(-29);
        
        for (int i = 0; i < 30; i++)
        {
            var date = baseDate.AddDays(i);
            var blockStart = 1000000L + (i * 7200); // ~6.5 second block time, ~1 hour per block
            var blockEnd = blockStart + 7199;
            
            testData.Add(new DailyStatsEntry
            {
                Date = date.ToString("yyyy-MM-dd"),
                StartBlockNumber = (int)Math.Min(blockStart, int.MaxValue),
                EndBlockNumber = (int)Math.Min(blockEnd, int.MaxValue),
                TotalWallets = 150 + (i * 5),
                ActiveWallets = 75 + (i * 2),
                TxCount = 200 + (i * 10),
                TotalSupply = 1000000m + (i * 50000),
                TotalStaked = 500000m + (i * 25000),
                TotalLocked = 300000m + (i * 15000),
                TotalLiquid = 200000m + (i * 10000)
            });
        }
        
        // Insert test data
        int count = 0;
        foreach (var entry in testData)
        {
            await syncService.InsertDailyStatsAsync(entry);
            count++;
            var wallets = entry.TotalWallets?.ToString("N0") ?? "?";
            var txs = entry.TxCount?.ToString("N0") ?? "?";
            var startBlock = entry.StartBlockNumber?.ToString("N0") ?? "?";
            var endBlock = entry.EndBlockNumber?.ToString("N0") ?? "?";
            Console.WriteLine($"  {entry.Date} | Blocks: {startBlock} - {endBlock} | Wallets: {wallets} | Txs: {txs}");
        }
        
        Console.WriteLine();
        Console.WriteLine($"Test data seeding complete: {count} records added");
        Console.WriteLine("You can now view the data in the web dashboard at http://127.0.0.1:5070/page/sync");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error during test data seeding: {ex.Message}");
        if (ex.InnerException != null)
            Console.WriteLine($"Details: {ex.InnerException.Message}");
    }
    return;
}

if (dailySyncMode || backfillMode)
{
    if (dailySyncMode && backfillMode)
    {
        Console.WriteLine("Running daily sync and backfill in parallel...");
        var dailyTask = RunDailySyncAsync();
        var backfillTask = RunBackfillAsync(http, rpcHttp, channel, denomWantedUpper, lcdBase, rpcBase, backfillForce);
        await Task.WhenAll(dailyTask, backfillTask);
    }
    else if (dailySyncMode)
    {
        await RunDailySyncAsync();
    }
    else
    {
        await RunBackfillAsync(http, rpcHttp, channel, denomWantedUpper, lcdBase, rpcBase, backfillForce);
    }
    return;
}

if (StatusMode)
{
    Console.WriteLine();
    Console.WriteLine("== Node Status ==");
    var status = await TryGetCometStatusAsync(rpcHttp);
    if (status is null)
    {
        Console.WriteLine("Unable to fetch CometBFT status.");
        return;
    }

    var heightText = status.LatestBlockHeight.HasValue
        ? status.LatestBlockHeight.Value.ToString(CultureInfo.InvariantCulture)
        : "unknown";
    var timeText = status.LatestBlockTime.HasValue
        ? status.LatestBlockTime.Value.ToString("O", CultureInfo.InvariantCulture)
        : "unknown";
    var catchingUpText = status.CatchingUp.HasValue
        ? (status.CatchingUp.Value ? "true" : "false")
        : "unknown";

    Console.WriteLine($"RPC:            {rpcBase}");
    Console.WriteLine($"Catching up:    {catchingUpText}");
    Console.WriteLine($"Latest height:  {heightText}");
    Console.WriteLine($"Latest time:    {timeText}");

    if (status.LatestBlockTime.HasValue)
    {
        var age = DateTimeOffset.UtcNow - status.LatestBlockTime.Value;
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        Console.WriteLine($"Block age:      {FormatDurationShort(age)}");
    }

    if (!string.IsNullOrWhiteSpace(status.Moniker))
        Console.WriteLine($"Moniker:        {status.Moniker}");
    if (!string.IsNullOrWhiteSpace(status.Network))
        Console.WriteLine($"Network:        {status.Network}");

    return;
}

if (ValidatorsNodesMode)
{
    Console.WriteLine();
    await PrintValidatorsWithIpsAsync(http, rpcHttp, scale);
    return;
}

if (WalletCountMode)
{
    Console.WriteLine();
    Console.WriteLine("== Wallet Count ==");
    var addresses = await GetAllWalletAddressesAsync(http, null);
    Console.WriteLine($"Wallets: {addresses.Count}");
    return;
}

if (!cfg.TryGetValue("addr", out var addr) || string.IsNullOrWhiteSpace(addr))
{
    addr = null;
}

string? addrOverride = null;
if (!string.IsNullOrWhiteSpace(DistributionsAddrOverride))
    addrOverride = DistributionsAddrOverride;
if (!string.IsNullOrWhiteSpace(CounterpartiesAddrOverride))
{
    if (addrOverride is not null &&
        !addrOverride.Equals(CounterpartiesAddrOverride, StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine("Conflicting address overrides from --distributions and --counterparties.");
        return;
    }
    addrOverride = CounterpartiesAddrOverride;
}
if (!string.IsNullOrWhiteSpace(LocksAddrOverride))
{
    if (addrOverride is not null &&
        !addrOverride.Equals(LocksAddrOverride, StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine("Conflicting address overrides from --locks and another mode.");
        return;
    }
    addrOverride = LocksAddrOverride;
}

if (!string.IsNullOrWhiteSpace(SendRecvAddrOverride))
{
    if (addrOverride is not null &&
        !addrOverride.Equals(SendRecvAddrOverride, StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine("Conflicting address overrides from --send-recv and another mode.");
        return;
    }
    addrOverride = SendRecvAddrOverride;
}

if (!string.IsNullOrWhiteSpace(WalletLocksSummaryAddrOverride))
{
    if (addrOverride is not null &&
        !addrOverride.Equals(WalletLocksSummaryAddrOverride, StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine("Conflicting address overrides from --wallet-locks-summary and another mode.");
        return;
    }
    addrOverride = WalletLocksSummaryAddrOverride;
}

addr = addrOverride ?? addr;
if (string.IsNullOrWhiteSpace(addr))
{
    Console.WriteLine("Config error: addr is required (or pass --distributions <addr>, --counterparties <addr>, --locks <addr>, --send-recv <addr>, or --wallet-locks-summary <addr>).");
    return;
}

if (!IsMultiSendSumEarly)
{
    Console.WriteLine("== Config ==");
    Console.WriteLine($"Addr:         {addr}");
    Console.WriteLine($"Denom:        {denom} (case-insensitive match)");
    Console.WriteLine($"Timezone:     {tz.DisplayName}");
    Console.WriteLine($"gRPC:         {grpcTarget}");
    Console.WriteLine($"RPC (26657):  {rpcBase}");
    Console.WriteLine($"REST (LCD):   {lcdBase}");
    Console.WriteLine($"LookbackDays: {lookbackDays}");
    Console.WriteLine($"PageLimit:    {pageLimit} (clamped)");
    Console.WriteLine($"MaxPages:     {maxPages}");
}

if (LockExtendedReportMode)
{
    Console.WriteLine();
    Console.WriteLine("== Lock Extended Report ==");

    var daysBack = Math.Clamp(LockExtendedDays, 1, 3650);
    var latest = await TryGetLatestHeightFromCometRpcAsync(rpcHttp);
    var endHeight = latest ?? 0;
    if (endHeight <= 0)
    {
        Console.WriteLine("Unable to resolve latest block height for lock-extended report.");
        return;
    }

    var nowUtc = DateTimeOffset.UtcNow;
    var startUtc = nowUtc.AddDays(-daysBack);
    var blockTimeCache = new ConcurrentDictionary<long, DateTime>();
    var startHeight = await FindHeightAtOrAfterTimeAsync(
        rpcHttp,
        1,
        endHeight,
        startUtc.UtcDateTime,
        blockTimeCache,
        CancellationToken.None);

    if (!startHeight.HasValue)
    {
        Console.WriteLine("Unable to resolve start height for lock-extended report.");
        return;
    }

    var baseQuery = "message.action='/optio.lockup.MsgExtend'";
    var heightQuery = $"{baseQuery} AND tx.height>={startHeight.Value} AND tx.height<={endHeight}";

    var hits = await TryGetCometTxSearchHitsWithFallbackAsync(
        rpcHttp,
        heightQuery,
        pageLimit,
        maxPages,
        null,
        CancellationToken.None);

    if (hits.Count == 0)
    {
        hits = await TryGetCometTxSearchHitsWithFallbackAsync(
            rpcHttp,
            baseQuery,
            pageLimit,
            maxPages,
            startHeight.Value,
            CancellationToken.None);
    }

    var lockRows = new List<LockExtendedRow>();
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    foreach (var hit in hits)
    {
        if (!seen.Add(hit.Hash)) continue;

        var tx = await TryGetCometTxWithRetryAsync(rpcHttp, hit.Hash, 3, CancellationToken.None);
        if (tx is null) continue;

        var height = tx.Height;
        var timeUtc = await GetBlockTimeCachedAsync(rpcHttp, height, blockTimeCache, CancellationToken.None);
        if (!timeUtc.HasValue) continue;
        if (timeUtc.Value < startUtc.UtcDateTime) continue;

        foreach (var ev in tx.Events)
        {
            if (!string.Equals(ev.Type, "lock_extended", StringComparison.OrdinalIgnoreCase)) continue;

            var address = GetCometAttrValue(ev.Attributes, "address") ?? "";
            var amountField = GetCometAttrValue(ev.Attributes, "amount") ?? "";
            var oldUnlock = GetCometAttrValue(ev.Attributes, "old_unlock_date") ?? "";
            var unlock = GetCometAttrValue(ev.Attributes, "unlock_date") ?? "";

            var amountUnits = ParseAmountFieldUopt(amountField, denomWantedUpper);
            if (amountUnits == BigInteger.Zero &&
                BigInteger.TryParse(amountField, NumberStyles.Integer, CultureInfo.InvariantCulture, out var plainUnits))
            {
                amountUnits = plainUnits;
            }
            var amountOpt = amountUnits == BigInteger.Zero ? 0m : (decimal)amountUnits / scaleInt;

            lockRows.Add(new LockExtendedRow
            {
                TxHash = hit.Hash,
                Height = height,
                Address = address,
                AmountOpt = amountOpt,
                OldUnlockDate = oldUnlock,
                UnlockDate = unlock,
                TxTimeUtc = timeUtc.Value,
            });
        }
    }

    lockRows.Sort((a, b) =>
    {
        var aTime = a.TxTimeUtc ?? DateTime.MinValue;
        var bTime = b.TxTimeUtc ?? DateTime.MinValue;
        var c = bTime.CompareTo(aTime);
        if (c != 0) return c;
        c = b.Height.CompareTo(a.Height);
        if (c != 0) return c;
        return string.Compare(a.TxHash, b.TxHash, StringComparison.OrdinalIgnoreCase);
    });

    const string lockExtendedPath = "optio-lock-extended.csv";
    var lines = new List<string>(lockRows.Count + 1)
    {
        "date_local,tx_hash,height,address,amount_opt,old_unlock_date,unlock_date"
    };

    foreach (var row in lockRows)
    {
        var localTime = row.TxTimeUtc.HasValue
            ? TimeZoneInfo.ConvertTimeFromUtc(row.TxTimeUtc.Value, tz)
            : (DateTime?)null;
        var dateStr = localTime.HasValue
            ? localTime.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : "";

        var line = string.Join(",",
            CsvEscape(dateStr),
            CsvEscape(row.TxHash),
            CsvEscape(row.Height.ToString(CultureInfo.InvariantCulture)),
            CsvEscape(row.Address),
            CsvEscape(row.AmountOpt.ToString("0.000000", CultureInfo.InvariantCulture)),
            CsvEscape(row.OldUnlockDate),
            CsvEscape(row.UnlockDate));
        lines.Add(line);
    }

    File.WriteAllLines(lockExtendedPath, lines);
    Console.WriteLine($"CSV written: {Path.GetFullPath(lockExtendedPath)}");
    return;
}

// Kyvo "Active Lockups" map to either a lockup module (if present) or vesting-derived schedules.
// We prefer explicit lockup module data when available; otherwise we derive remaining locks from vesting.
if (LocksMode || LocksSummaryMode)
{
    var lockService = new LockService(http, channel);

    if (LocksSummaryMode && !LocksMode)
    {
        var allLockRows = await lockService.GetAllActiveLockRowsAsync(denomWantedUpper, scaleInt, CancellationToken.None);
        var allSummary = SummarizeLockBuckets(allLockRows, DateTimeOffset.UtcNow);

        Console.WriteLine();
        Console.WriteLine("== Lock Summary (All Addresses) ==");
        Console.WriteLine(
            $"{Left("Length", 8)} " +
            $"{Right("Amount(OPT)", 16)}");
        Console.WriteLine(new string('-', 8 + 1 + 16));

        foreach (var bucket in new[] { 6, 12, 18, 24 })
        {
            allSummary.TryGetValue(bucket, out var amountOpt);
            Console.WriteLine(
                $"{Left($"{bucket}mo", 8)} " +
                $"{Right(amountOpt.ToString("0.000000", CultureInfo.InvariantCulture), 16)}");
        }

        if (allSummary.TryGetValue(0, out var otherOpt) && otherOpt > 0m)
        {
            Console.WriteLine(
                $"{Left("Other", 8)} " +
                $"{Right(otherOpt.ToString("0.000000", CultureInfo.InvariantCulture), 16)}");
        }

        var allTotalLockedOpt = allLockRows.Sum(r => r.AmountOpt);
        Console.WriteLine();
        Console.WriteLine($"TotalLockedOPT: {allTotalLockedOpt.ToString("0.000000", CultureInfo.InvariantCulture)}");
        return;
    }

    var bankService = new BankService(http);
    var stakingService = new StakingService(http);

    var liquidUnits = await bankService.GetSpendableUnitsAsync(addr, denomWantedUpper, CancellationToken.None);
    var stakedUnits = await stakingService.GetDelegatedBondedUnitsAsync(addr, denomWantedUpper, CancellationToken.None);
    var lockRows = await lockService.GetLockRowsAsync(addr, denomWantedUpper, scaleInt, CancellationToken.None);

    var liquidOpt = liquidUnits.HasValue ? liquidUnits.Value / scaleInt : 0m;
    var stakedOpt = stakedUnits.HasValue ? stakedUnits.Value / scaleInt : 0m;

    Console.WriteLine();
    Console.WriteLine("== Locks ==");
    Console.WriteLine($"Address: {addr}");
    Console.WriteLine($"Liquid OPT: {liquidOpt.ToString("0.000000", CultureInfo.InvariantCulture)}");
    Console.WriteLine($"Staked OPT: {stakedOpt.ToString("0.000000", CultureInfo.InvariantCulture)}");

    if (lockRows.Count == 0)
    {
        Console.WriteLine("Active Lockups: none");
        Console.WriteLine();
        if (LocksSummaryMode)
        {
            Console.WriteLine("== Lock Summary (Remaining) ==");
            Console.WriteLine(
                $"{Left("Length", 8)} " +
                $"{Right("Amount(OPT)", 16)}");
            Console.WriteLine(new string('-', 8 + 1 + 16));

            foreach (var bucket in new[] { 6, 12, 18, 24 })
            {
                Console.WriteLine(
                    $"{Left($"{bucket}mo", 8)} " +
                    $"{Right(0m.ToString("0.000000", CultureInfo.InvariantCulture), 16)}");
            }
        }
        Console.WriteLine("TotalLockedOPT: 0.000000");
        Console.WriteLine($"EffectiveLiquid: {liquidOpt.ToString("0.000000", CultureInfo.InvariantCulture)}");
        return;
    }

    Console.WriteLine("Active Lockups:");

    const int LockSourceWidth = 16;
    const int LockAmtWidth = 16;
    const int LockUtcWidth = 20;
    const int LockLocalWidth = 24;
    const int LockUntilWidth = 14;
    const int LockLenWidth = 12;

    Console.WriteLine(
        $"{Right("#", 2)} " +
        $"{Left("Source", LockSourceWidth)} " +
        $"{Right("Amount(OPT)", LockAmtWidth)} " +
        $"{Left("End Date (UTC)", LockUtcWidth)} " +
        $"{Left("End Date (Local)", LockLocalWidth)} " +
        $"{Left("Until Unlock", LockUntilWidth)} " +
        $"{Left("Lock Length", LockLenWidth)}");
    Console.WriteLine(new string('-', 2 + 1 + LockSourceWidth + 1 + LockAmtWidth + 1 + LockUtcWidth + 1 + LockLocalWidth + 1 + LockUntilWidth + 1 + LockLenWidth));

    var nowUtc = DateTimeOffset.UtcNow;
    int idx = 1;
    decimal totalLockedOpt = 0m;

    foreach (var row in lockRows.OrderBy(r => r.EndTime))
    {
        var endUtc = row.EndTime;
        var endLocal = TimeZoneInfo.ConvertTime(endUtc, tz);
        var until = endUtc - nowUtc;
        var untilText = until <= TimeSpan.Zero ? "0m" : FormatDurationShort(until);
        var length = row.Duration.HasValue ? FormatDurationShort(row.Duration.Value) : "";

        totalLockedOpt += row.AmountOpt;

        Console.WriteLine(
            $"{Right(idx.ToString(CultureInfo.InvariantCulture), 2)} " +
            $"{Left(row.Source, LockSourceWidth)} " +
            $"{Right(row.AmountOpt.ToString("0.000000", CultureInfo.InvariantCulture), LockAmtWidth)} " +
            $"{Left(endUtc.ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture), LockUtcWidth)} " +
            $"{Left(endLocal.ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture), LockLocalWidth)} " +
            $"{Left(untilText, LockUntilWidth)} " +
            $"{Left(length, LockLenWidth)}");
        idx++;
    }

    if (LocksSummaryMode)
    {
        var addrSummary = SummarizeLockBuckets(lockRows, DateTimeOffset.UtcNow);
        Console.WriteLine();
        Console.WriteLine("== Lock Summary (Remaining) ==");
        Console.WriteLine(
            $"{Left("Length", 8)} " +
            $"{Right("Amount(OPT)", 16)}");
        Console.WriteLine(new string('-', 8 + 1 + 16));

        foreach (var bucket in new[] { 6, 12, 18, 24 })
        {
            addrSummary.TryGetValue(bucket, out var amountOpt);
            Console.WriteLine(
                $"{Left($"{bucket}mo", 8)} " +
                $"{Right(amountOpt.ToString("0.000000", CultureInfo.InvariantCulture), 16)}");
        }

        if (addrSummary.TryGetValue(0, out var otherOpt) && otherOpt > 0m)
        {
            Console.WriteLine(
                $"{Left("Other", 8)} " +
                $"{Right(otherOpt.ToString("0.000000", CultureInfo.InvariantCulture), 16)}");
        }
    }

    Console.WriteLine();
    Console.WriteLine($"TotalLockedOPT: {totalLockedOpt.ToString("0.000000", CultureInfo.InvariantCulture)}");
    Console.WriteLine($"EffectiveLiquid: {liquidOpt.ToString("0.000000", CultureInfo.InvariantCulture)}");
    return;
}

if (MultiSendSumMode)
{
    var exit = await RunMultiSendSumAsync(
        rpcHttp,
        denomWantedUpper,
        scaleInt,
        multiSendFromAddr,
        multiSendLookbackBlocks,
        BlockScanStart,
        BlockScanEnd,
        multiSendOutPath,
        pageLimit,
        maxPages,
        CancellationToken.None);
    return;
}

if (BlockScanMultiSend)
{
    var latest = await TryGetLatestHeightFromCometRpcAsync(rpcHttp);
    var endHeight = BlockScanEnd ?? latest ?? 0;
    if (endHeight <= 0)
    {
        Console.WriteLine("Unable to resolve latest block height from RPC.");
        return;
    }

    var startHeight = BlockScanStart ?? 1;
    if (startHeight < 1) startHeight = 1;
    if (startHeight > endHeight)
    {
        Console.WriteLine($"Invalid height range: start={startHeight} end={endHeight}");
        return;
    }

    Console.WriteLine();
    Console.WriteLine("== MultiSend Block Scan (All-Time) ==");
    Console.WriteLine($"Range: {startHeight}..{endHeight}");
    Console.WriteLine($"Top senders: {BlockScanTopSenders}");
    Console.WriteLine($"Sender whitelist: {string.Join(", ", BlockScanSenders)}");

    await ScanMultiSendFromBlocksAsync(
        rpcHttp,
        denomWantedUpper,
        startHeight,
        endHeight,
        scaleInt,
        BlockScanTopSenders,
        BlockScanSenders);
    return;
}

if (WalletBalancesMode)
{
    Console.WriteLine();
    Console.WriteLine("== Wallet Balances (All Addresses) ==");
    var wallets = await GetAllOptWalletsWithStakingAsync(http, denomWantedUpper, scale, QueryHeight, emissionAddress);
    PrintWalletSummary(wallets);
    PrintWalletTotals(wallets);
    // Emission totals intentionally skipped for wallet balances output.
    if (!string.IsNullOrWhiteSpace(WalletBalancesCsv))
    {
        WriteWalletBalancesCsv(WalletBalancesCsv, wallets);
        Console.WriteLine($"CSV written: {Path.GetFullPath(WalletBalancesCsv)}");
    }
    return;
}

if (WalletLocksSummaryMode)
{
    Console.WriteLine();
    Console.WriteLine("== Wallet Locks Summary ==");
    Console.WriteLine($"Address: {addr}");

    var walletUnits = await GetWalletBalanceUnitsAsync(http, addr, denomWantedUpper, null);
    var stakedUnits = await GetDelegatedUnitsAsync(http, addr, denomWantedUpper, null);
    var unbondingUnits = await GetUnbondingUnitsAsync(http, addr, denomWantedUpper, null);

    var bankOpt = walletUnits / scale;
    var stakedOpt = stakedUnits / scale;
    var unbondingOpt = unbondingUnits / scale;

    Console.WriteLine($"Bank Balance OPT: {bankOpt.ToString("0.000000", CultureInfo.InvariantCulture)}");
    Console.WriteLine($"Staked OPT:       {stakedOpt.ToString("0.000000", CultureInfo.InvariantCulture)}");
    Console.WriteLine($"Unbonding OPT:    {unbondingOpt.ToString("0.000000", CultureInfo.InvariantCulture)}");
    Console.WriteLine("Lock Start:       (not available from lockup query)");

    var lockService = new LockService(http, channel);
    var lockRows = await lockService.GetLockRowsAsync(addr, denomWantedUpper, scaleInt, CancellationToken.None);
    var buckets = SummarizeLockBuckets(lockRows, DateTimeOffset.UtcNow);

    Console.WriteLine("Locks (remaining buckets):");
    Console.WriteLine($"  L6:  {buckets.GetValueOrDefault(6).ToString("0.000000", CultureInfo.InvariantCulture)}");
    Console.WriteLine($"  L12: {buckets.GetValueOrDefault(12).ToString("0.000000", CultureInfo.InvariantCulture)}");
    Console.WriteLine($"  L18: {buckets.GetValueOrDefault(18).ToString("0.000000", CultureInfo.InvariantCulture)}");
    Console.WriteLine($"  L24: {buckets.GetValueOrDefault(24).ToString("0.000000", CultureInfo.InvariantCulture)}");
    return;
}

if (WalletLocksReportMode)
{
    Console.WriteLine();
    Console.WriteLine("== Wallet Locks CSV (All Addresses) ==");

    var wallets = await GetAllOptWalletsWithStakingAsync(http, denomWantedUpper, scale, null, emissionAddress);
    var lockService = new LockService(http, channel);
    var lockBuckets = await lockService.GetAllActiveLockBucketsAsync(
        denomWantedUpper,
        scaleInt,
        DateTimeOffset.UtcNow,
        CancellationToken.None);

    const string detailsPath = "optic-wallet-details.csv";
    const string totalsPath = "optic-wallet-totals.csv";

    var lines = new List<string>(wallets.Count + 1)
    {
        "address,total_balance_opt,bank_balance_opt,staked_opt,unbonding_opt,L6,L12,L18,L24"
    };

    decimal totalWalletBalance = 0m;
    decimal totalStaked = 0m;
    decimal totalUnbonding = 0m;
    decimal totalCombined = 0m;
    decimal totalL6 = 0m;
    decimal totalL12 = 0m;
    decimal totalL18 = 0m;
    decimal totalL24 = 0m;

    foreach (var w in wallets.OrderBy(w => w.Address, StringComparer.OrdinalIgnoreCase))
    {
        lockBuckets.TryGetValue(w.Address, out var buckets);
        var lock6 = buckets is null ? 0m : buckets[0];
        var lock12 = buckets is null ? 0m : buckets[1];
        var lock18 = buckets is null ? 0m : buckets[2];
        var lock24 = buckets is null ? 0m : buckets[3];
        var totalBalance = w.WalletBalanceOPT + w.StakedOPT + w.UnbondingOPT;

        var line = string.Join(",",
            CsvEscape(w.Address),
            CsvEscape(totalBalance.ToString("0.000000", CultureInfo.InvariantCulture)),
            CsvEscape(w.WalletBalanceOPT.ToString("0.000000", CultureInfo.InvariantCulture)),
            CsvEscape(w.StakedOPT.ToString("0.000000", CultureInfo.InvariantCulture)),
            CsvEscape(w.UnbondingOPT.ToString("0.000000", CultureInfo.InvariantCulture)),
            CsvEscape(lock6.ToString("0.000000", CultureInfo.InvariantCulture)),
            CsvEscape(lock12.ToString("0.000000", CultureInfo.InvariantCulture)),
            CsvEscape(lock18.ToString("0.000000", CultureInfo.InvariantCulture)),
            CsvEscape(lock24.ToString("0.000000", CultureInfo.InvariantCulture)));
        lines.Add(line);

        totalWalletBalance += w.WalletBalanceOPT;
        totalStaked += w.StakedOPT;
        totalUnbonding += w.UnbondingOPT;
        totalCombined += totalBalance;
        totalL6 += lock6;
        totalL12 += lock12;
        totalL18 += lock18;
        totalL24 += lock24;
    }

    File.WriteAllLines(detailsPath, lines);
    Console.WriteLine($"CSV written: {Path.GetFullPath(detailsPath)}");

    var totalsLines = new List<string>(2)
    {
        "label,wallet_count,total_balance_opt,bank_balance_opt,staked_opt,unbonding_opt,L6,L12,L18,L24",
        string.Join(",",
            "Totals",
            wallets.Count.ToString(CultureInfo.InvariantCulture),
            totalCombined.ToString("0.000000", CultureInfo.InvariantCulture),
            totalWalletBalance.ToString("0.000000", CultureInfo.InvariantCulture),
            totalStaked.ToString("0.000000", CultureInfo.InvariantCulture),
            totalUnbonding.ToString("0.000000", CultureInfo.InvariantCulture),
            totalL6.ToString("0.000000", CultureInfo.InvariantCulture),
            totalL12.ToString("0.000000", CultureInfo.InvariantCulture),
            totalL18.ToString("0.000000", CultureInfo.InvariantCulture),
            totalL24.ToString("0.000000", CultureInfo.InvariantCulture))
    };

    File.WriteAllLines(totalsPath, totalsLines);
    Console.WriteLine($"CSV written: {Path.GetFullPath(totalsPath)}");
    return;
}

if (TotalStakedMode)
{
    Console.WriteLine();
    Console.WriteLine("== Total Staked (All Wallets) ==");
    var wallets = await GetAllOptWalletsWithStakingAsync(http, denomWantedUpper, scale, QueryHeight, emissionAddress);
    PrintWalletTotals(wallets);
    return;
}

if (TotalDistributedMode)
{
    var latest = await TryGetLatestHeightFromCometRpcAsync(rpcHttp);
    var endHeight = BlockScanEnd ?? latest ?? 0;
    if (endHeight <= 0)
    {
        Console.WriteLine("Unable to resolve latest block height for total distributed.");
        return;
    }

    var startHeight = BlockScanStart ?? 1;
    if (startHeight < 1) startHeight = 1;
    if (startHeight > endHeight)
    {
        Console.WriteLine($"Invalid height range: start={startHeight} end={endHeight}");
        return;
    }

    Console.WriteLine();
    Console.WriteLine("== Total Distributed (All Wallets) ==");
    Console.WriteLine($"Range: {startHeight}..{endHeight}");
    Console.WriteLine($"Emission address: {emissionAddress}");

    var distributedUopt = await GetTotalDistributedFromBlocksAsync(
        rpcHttp,
        denomWantedUpper,
        startHeight,
        endHeight,
        emissionAddress);

    Console.WriteLine($"Total Distributed: {FormatOptFromUopt(distributedUopt, scaleInt)} OPT");
    return;
}

if (TotalsAllMode)
{
    var latest = await TryGetLatestHeightFromCometRpcAsync(rpcHttp);
    var endHeight = BlockScanEnd ?? latest ?? 0;
    if (endHeight <= 0)
    {
        Console.WriteLine("Unable to resolve latest block height for totals.");
        return;
    }

    var startHeight = BlockScanStart ?? 1;
    if (startHeight < 1) startHeight = 1;
    if (startHeight > endHeight)
    {
        Console.WriteLine($"Invalid height range: start={startHeight} end={endHeight}");
        return;
    }

    Console.WriteLine();
    Console.WriteLine("== Totals (All Wallets) ==");
    Console.WriteLine($"Range: {startHeight}..{endHeight}");
    Console.WriteLine($"Emission address: {emissionAddress}");

    var wallets = await GetAllOptWalletsWithStakingAsync(http, denomWantedUpper, scale, null, emissionAddress);
    var totalStaked = wallets.Sum(w => w.StakedOPT);
    var totalWalletBalance = wallets.Sum(w => w.WalletBalanceOPT);
    var totalUnbonding = wallets.Sum(w => w.UnbondingOPT);
    var totalWalletsCombined = wallets.Sum(w => w.TotalOPT);

    var lockService = new LockService(http, channel);
    var lockRows = await lockService.GetAllActiveLockRowsAsync(denomWantedUpper, scaleInt, CancellationToken.None);
    var totalLockedOpt = lockRows.Sum(r => r.AmountOpt);

    var distributedUopt = await GetTotalDistributedFromBlocksAsync(
        rpcHttp,
        denomWantedUpper,
        startHeight,
        endHeight,
        emissionAddress);

    var distOpt = decimal.Truncate((decimal)distributedUopt / scaleInt);
    var stakedOpt = decimal.Truncate(totalStaked);
    var lockedOpt = decimal.Truncate(totalLockedOpt);
    var walletOpt = decimal.Truncate(totalWalletBalance);
    var unbondingOpt = decimal.Truncate(totalUnbonding);
    var combinedOpt = decimal.Truncate(totalWalletsCombined);

    Console.WriteLine($"Total Distributed: {distOpt.ToString("N0", CultureInfo.InvariantCulture)} OPT");
    Console.WriteLine($"Total Staked:      {stakedOpt.ToString("N0", CultureInfo.InvariantCulture)} OPT");
    Console.WriteLine($"Total Locked:      {lockedOpt.ToString("N0", CultureInfo.InvariantCulture)} OPT");
    Console.WriteLine($"Total Wallet:      {walletOpt.ToString("N0", CultureInfo.InvariantCulture)} OPT");
    Console.WriteLine($"Total Unbonding:   {unbondingOpt.ToString("N0", CultureInfo.InvariantCulture)} OPT");
    Console.WriteLine($"Total Combined:    {combinedOpt.ToString("N0", CultureInfo.InvariantCulture)} OPT");
    return;
}

if (cmcDailyYear.HasValue)
{
    var exitCode = await RunCmcDailyAsync(cmcDailyYear.Value, cmcId, cmcOutPath, CancellationToken.None);
    if (exitCode != 0)
        Environment.ExitCode = exitCode;
    return;
}

// =======================================================================
// Balances Summary (NEW) - derives locked from module state (REST)
// =======================================================================
if (NetworkTotals)
{
    Console.WriteLine();
    await PrintNetworkTotalsAllTimeAsync(http, rpcHttp, denomWantedUpper, scale, pageLimit, maxPages);
    return;
}

if (!DistributionsMode && !CounterpartiesMode && !LocksMode && !LocksSummaryMode && !TotalStakedMode && !TotalDistributedMode && !TotalsAllMode && !SendRecvMode && !WalletLocksReportMode && !WalletLocksSummaryMode && !LockExtendedReportMode)
{
    Console.WriteLine("Missing required --distributions, --counterparties, --locks, --locks-summary, --lock-extended, --total-staked, --total-distributed, --totals-all, --send-recv, --wallet-locks-report, or --wallet-locks-summary for the default ledger run.");
    Console.WriteLine("Use --help to see all options.");
    return;
}

Console.WriteLine();
Console.WriteLine("== Balances Summary ==");

var summary = await GetBalancesSummaryAsync(http, addr, denomWantedUpper);
PrintBalancesSummary(summary, denomWantedUpper, scale);

// =======================================================================
// Ledger output (unchanged aside from earlier Memo column)
// =======================================================================

// ---------------- 1) Collect TxResponses (by hash) from GetTxsEvent ----------------
if (VerboseScan)
{
    Console.WriteLine();
    Console.WriteLine("Scanning tx responses via gRPC GetTxsEvent (page-key paging)...");
}

var txByHash = new Dictionary<string, TxResponse>(StringComparer.OrdinalIgnoreCase);

var querySpecs = new (string Label, string Query)[]
{
    ("recv.transfer",      $"transfer.recipient='{addr}'"),
    ("recv.coin_received", $"coin_received.receiver='{addr}'"),
    ("sent.transfer",      $"transfer.sender='{addr}'"),
    ("sent.coin_spent",    $"coin_spent.spender='{addr}'"),
    ("sent.message",       $"message.sender='{addr}'"),
    ("sent.bank_module",   $"message.module='bank' AND message.sender='{addr}'"),
    ("sent.staking_module",$"message.module='staking' AND message.sender='{addr}'"),
};

foreach (var (label, query) in querySpecs)
{
    await AddTxResponsesForQuery_PageKey(txClient, query, txByHash, cutoffUtc, pageLimit, maxPages, label, VerboseScan);
}

if (VerboseScan)
{
    Console.WriteLine();
    Console.WriteLine($"Unique tx responses collected: {txByHash.Count}");
}
if (txByHash.Count == 0)
{
    Console.WriteLine("No transactions found.");
    return;
}

// CometBFT index sanity check: if tx_search returns nothing for common tags, warn that RPC search is incomplete.
var cometSampleLimit = Math.Min(10, pageLimit);
var cometRecv = await TryGetCometTxSearchHashesAsync(rpcHttp, $"transfer.recipient='{addr}'", cometSampleLimit, 1);
var cometRecvCoin = await TryGetCometTxSearchHashesAsync(rpcHttp, $"coin_received.receiver='{addr}'", cometSampleLimit, 1);
var cometSent = await TryGetCometTxSearchHashesAsync(rpcHttp, $"message.sender='{addr}'", cometSampleLimit, 1);
if (cometRecv.Count == 0 && cometRecvCoin.Count == 0 && cometSent.Count == 0)
{
    Console.WriteLine("WARNING: CometBFT tx_search index appears empty for this address; RPC search may be incomplete.");
}

// ---------------- CometBFT fallback (debug + multi-send when gRPC index misses them) ----------------
var cometFallbackTxs = new List<(string Hash, CometTx Tx, DateTime TimeUtc)>();
var cometFallbackHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
foreach (var debugHash in new[] { DebugHash1 })
{
    if (txByHash.ContainsKey(debugHash))
    {
        if (VerboseScan)
            Console.WriteLine($"DEBUG hash {debugHash} found via gRPC event index.");
        continue;
    }

    if (VerboseScan)
        Console.WriteLine($"DEBUG hash {debugHash} not in gRPC index; trying Comet RPC...");

    var comet = await TryGetCometTxAsync(rpcHttp, debugHash);
    if (comet is not null)
    {
        var bt = await TryGetBlockTimeFromCometRpcAsync(rpcHttp, comet.Height);
        if (bt is not null)
        {
            cometFallbackTxs.Add((debugHash, comet, bt.Value));
            cometFallbackHashes.Add(debugHash);
            if (VerboseScan)
            {
                Console.WriteLine($"DEBUG comet fallback found tx {debugHash} height={comet.Height} time={bt.Value:O}");
                DumpCometTxEventsForDebug(comet, debugHash);
            }
        }
        else
            if (VerboseScan)
                Console.WriteLine($"DEBUG comet fallback found tx {debugHash} but could not resolve block time.");
    }
    else
    {
        if (VerboseScan)
            Console.WriteLine($"DEBUG comet fallback could not find tx {debugHash}.");
    }
}

if (EnableCometMultiSendFallback)
{
    var multiSendQueries = new[]
    {
        $"message.action='/cosmos.bank.v1beta1.MsgMultiSend' AND transfer.recipient='{addr}'",
        $"message.action='/cosmos.bank.v1beta1.MsgMultiSend' AND coin_received.receiver='{addr}'"
    };

    foreach (var query in multiSendQueries)
    {
        var multiSendHashes = await TryGetCometTxSearchHashesAsync(rpcHttp, query, pageLimit, maxPages);

        foreach (var hash in multiSendHashes)
        {
            if (txByHash.ContainsKey(hash)) continue;
            if (cometFallbackHashes.Contains(hash)) continue;

            var comet = await TryGetCometTxAsync(rpcHttp, hash);
            if (comet is null) continue;

            var bt = await TryGetBlockTimeFromCometRpcAsync(rpcHttp, comet.Height);
            if (bt is null) continue;
            if (new DateTimeOffset(bt.Value, TimeSpan.Zero) < cutoffUtc) continue;

            cometFallbackTxs.Add((hash, comet, bt.Value));
            cometFallbackHashes.Add(hash);
        }
    }
}

if (EnableCometGeneralRecvFallback)
{
    var query = $"transfer.recipient='{addr}'";
    await AddCometFallbackFromQuery(rpcHttp, query, txByHash, cometFallbackTxs, cometFallbackHashes, cutoffUtc, pageLimit, maxPages);
}

if (EnableCometCoinReceivedFallback)
{
    var query = $"coin_received.receiver='{addr}'";
    await AddCometFallbackFromQuery(rpcHttp, query, txByHash, cometFallbackTxs, cometFallbackHashes, cutoffUtc, pageLimit, maxPages);
}

if (EnableCometSentFallback)
{
    var queries = new[]
    {
        $"transfer.sender='{addr}'",
        $"coin_spent.spender='{addr}'"
    };

    foreach (var query in queries)
        await AddCometFallbackFromQuery(rpcHttp, query, txByHash, cometFallbackTxs, cometFallbackHashes, cutoffUtc, pageLimit, maxPages);
}

// ---------------- 2) Resolve memo for each tx (B: real memo via GetTx; A: fallback label) ----------------
if (VerboseScan)
{
    Console.WriteLine();
    Console.WriteLine("Resolving memos via gRPC GetTx (real memo) with fallback labels...");
}

var memoByHash = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
int memoReal = 0, memoRpc = 0, memoFallback = 0;

foreach (var kv in txByHash)
{
    var h = kv.Key;
    var tr = kv.Value;

    // Prefer memo from gRPC GetTx
    var real = await TryGetRealMemoAsync(txClient, h);
    if (!string.IsNullOrWhiteSpace(real))
    {
        memoByHash[h] = real!;
        memoReal++;
        continue;
    }

    // If gRPC GetTx doesn't return memo, fall back to CometBFT RPC /tx (base64 tx bytes)
    var rpcMemo = await TryGetRealMemoFromCometRpcAsync(rpcHttp, h);
    if (!string.IsNullOrWhiteSpace(rpcMemo))
    {
        memoByHash[h] = rpcMemo!;
        memoRpc++;
        continue;
    }

    // Final fallback: label will be inferred at row build time.
    memoByHash[h] = "";
    memoFallback++;
}

if (VerboseScan)
    Console.WriteLine($"Memo: grpc={memoReal} comet_rpc={memoRpc} fallback={memoFallback}");

// ---------------- 3) Build ledger rows from TxResponse.Events ----------------
var rows = new List<LedgerRow>(txByHash.Count * 2);
var seenRow = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
var totalEmittedUopt = BigInteger.Zero;
var totalDistributedUopt = BigInteger.Zero;
var computeTotals = IncludeTotals || TotalsOnly;

foreach (var tr in txByHash.Values)
{
    var txHash = tr.Txhash ?? "";
    if (string.IsNullOrWhiteSpace(txHash)) continue;
    CometTx? cometTx = null;

    // REQUIRED DEBUG LINE (your request)
    if (VerboseScan && IsDebugHash(txHash))
        Console.WriteLine($"DEBUG saw hash {txHash} ts={tr.Timestamp} events={tr.Events.Count}");

    // Helpful: dump event attributes for that hash to explain why it's skipped
    if (VerboseScan && IsDebugHash(txHash)) DumpTxEventsForDebug(tr);

    // Prefer timestamp from gRPC TxResponse. If missing/unparseable, fall back to CometBFT /block for that height.
    DateTime timeUtc;
    if (DateTimeOffset.TryParse(tr.Timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var ts))
    {
        var tsUtc = ts.ToUniversalTime();
        if (tsUtc < cutoffUtc) continue;
        timeUtc = tsUtc.UtcDateTime;
    }
    else
    {
        // gRPC did not provide a usable timestamp; use CometBFT block time (requires height).
        // This avoids dropping valid historical txs that are missing TxResponse.Timestamp.
        var ht = (long)tr.Height;
        var bt = await TryGetBlockTimeFromCometRpcAsync(rpcHttp, ht);
        if (bt is null)
            continue; // can't time-order this tx reliably
        // cutoff check using block time
        if (new DateTimeOffset(bt.Value, TimeSpan.Zero) < cutoffUtc)
            continue;
        timeUtc = bt.Value;
    }

    // Fee + payer from "tx" event (may be missing on some historical gRPC responses; we'll allow RPC fallback below)
    var (feeUnits, feePayer) = GetFeeFromTxEvent(tr, denomWantedUpper);

    var memoRaw = memoByHash.TryGetValue(txHash, out var m) ? m : "";
    var memoLabel = NormalizeMemoLabel(InferMemoLabel(tr));
    if (VerboseScan && IsDebugHash(txHash))
        Console.WriteLine($"DEBUG memo label for {txHash}: {memoLabel}");

    // First pass: “real” transfers (filters fee-transfers)
    var transferRows = ExtractTransfersFromEvents(tr, addr, denomWantedUpper, feeUnits, feePayer).ToList();

    // RPC fallback: sometimes gRPC GetTxsEvent TxResponse is missing/empty events for historical txs.
    // If we didn't get any usable transfer rows (or this is our debug hash), pull tx_result.events from CometBFT /tx.
    if (transferRows.Count == 0 || tr.Events.Count == 0 || IsDebugHash(txHash))
    {
        cometTx = await TryGetCometTxAsync(rpcHttp, txHash);
        if (cometTx is not null)
        {
            var cometFee = GetFeeFromCometTx(cometTx, denomWantedUpper);
            if (cometFee.FeeUnits.HasValue) feeUnits = cometFee.FeeUnits.Value;
            if (!string.IsNullOrWhiteSpace(cometFee.FeePayer)) feePayer = cometFee.FeePayer;

            var cometTransfers = ExtractTransfersFromCometTx(cometTx, addr, denomWantedUpper, feeUnits, feePayer).ToList();
            if (cometTransfers.Count > 0)
                transferRows = cometTransfers;

            var cometDelegateRows = ExtractDelegateRowsFromCometTx(cometTx, addr, denomWantedUpper).ToList();
            if (cometDelegateRows.Count > 0)
            {
                foreach (var delegateRow in cometDelegateRows)
                {
                    if (!transferRows.Any(r =>
                        r.Direction.Equals("sent", StringComparison.OrdinalIgnoreCase) &&
                        r.From.Equals(delegateRow.From, StringComparison.OrdinalIgnoreCase) &&
                        r.To.Equals(delegateRow.To, StringComparison.OrdinalIgnoreCase) &&
                        r.AmountInDenomUnits == delegateRow.AmountInDenomUnits))
                    {
                        transferRows.Add(delegateRow);
                    }
                }
            }
        }
    }
    else if (HasMultiSendActionFromTxResponse(tr) &&
             !transferRows.Any(r => r.Direction.Equals("recv", StringComparison.OrdinalIgnoreCase) &&
                                    r.To.Equals(addr, StringComparison.OrdinalIgnoreCase)))
    {
        // gRPC may omit or partially index transfer events for MultiSend; refresh from Comet if target recv is missing.
        cometTx = await TryGetCometTxAsync(rpcHttp, txHash);
        if (cometTx is not null)
        {
            var cometFee = GetFeeFromCometTx(cometTx, denomWantedUpper);
            if (cometFee.FeeUnits.HasValue) feeUnits = cometFee.FeeUnits.Value;
            if (!string.IsNullOrWhiteSpace(cometFee.FeePayer)) feePayer = cometFee.FeePayer;

            var cometTransfers = ExtractTransfersFromCometTx(cometTx, addr, denomWantedUpper, feeUnits, feePayer).ToList();
            if (cometTransfers.Count > 0)
                transferRows = cometTransfers;

            var cometDelegateRows = ExtractDelegateRowsFromCometTx(cometTx, addr, denomWantedUpper).ToList();
            if (cometDelegateRows.Count > 0)
            {
                foreach (var delegateRow in cometDelegateRows)
                {
                    if (!transferRows.Any(r =>
                        r.Direction.Equals("sent", StringComparison.OrdinalIgnoreCase) &&
                        r.From.Equals(delegateRow.From, StringComparison.OrdinalIgnoreCase) &&
                        r.To.Equals(delegateRow.To, StringComparison.OrdinalIgnoreCase) &&
                        r.AmountInDenomUnits == delegateRow.AmountInDenomUnits))
                    {
                        transferRows.Add(delegateRow);
                    }
                }
            }
        }
    }

    // Add delegate rows based on staking events (bank transfer events won't show the bonded amount).
    var delegateRows = ExtractDelegateRowsFromTxResponse(tr, addr, denomWantedUpper).ToList();
    if (delegateRows.Count > 0)
    {
        foreach (var delegateRow in delegateRows)
        {
            if (!transferRows.Any(r =>
                r.Direction.Equals("sent", StringComparison.OrdinalIgnoreCase) &&
                r.From.Equals(delegateRow.From, StringComparison.OrdinalIgnoreCase) &&
                r.To.Equals(delegateRow.To, StringComparison.OrdinalIgnoreCase) &&
                r.AmountInDenomUnits == delegateRow.AmountInDenomUnits))
            {
                transferRows.Add(delegateRow);
            }
        }
    }
    else if (HasDelegateActionFromTxResponse(tr))
    {
        // gRPC often omits delegate events for historical txs; refresh from Comet to capture the bonded amount.
        if (cometTx is null)
            cometTx = await TryGetCometTxAsync(rpcHttp, txHash);

        if (cometTx is not null)
        {
            var cometFee = GetFeeFromCometTx(cometTx, denomWantedUpper);
            if (cometFee.FeeUnits.HasValue) feeUnits = cometFee.FeeUnits.Value;
            if (!string.IsNullOrWhiteSpace(cometFee.FeePayer)) feePayer = cometFee.FeePayer;

            var cometDelegateRows = ExtractDelegateRowsFromCometTx(cometTx, addr, denomWantedUpper).ToList();
            if (cometDelegateRows.Count > 0)
            {
                foreach (var delegateRow in cometDelegateRows)
                {
                    if (!transferRows.Any(r =>
                        r.Direction.Equals("sent", StringComparison.OrdinalIgnoreCase) &&
                        r.From.Equals(delegateRow.From, StringComparison.OrdinalIgnoreCase) &&
                        r.To.Equals(delegateRow.To, StringComparison.OrdinalIgnoreCase) &&
                        r.AmountInDenomUnits == delegateRow.AmountInDenomUnits))
                    {
                        transferRows.Add(delegateRow);
                    }
                }
            }
        }
    }

    // Apply fee to running only if payer is our target address
    var feeApplied = feeUnits > 0m &&
                     !string.IsNullOrWhiteSpace(feePayer) &&
                     feePayer.Equals(addr, StringComparison.OrdinalIgnoreCase);

    // If we still don't have a recv row for the target, fall back to coin_received/coin_spent rows.
    var hasTargetRecv = transferRows.Any(r =>
        r.Direction.Equals("recv", StringComparison.OrdinalIgnoreCase) &&
        r.To.Equals(addr, StringComparison.OrdinalIgnoreCase));
    var hasTargetSent = transferRows.Any(r =>
        r.Direction.Equals("sent", StringComparison.OrdinalIgnoreCase) &&
        r.From.Equals(addr, StringComparison.OrdinalIgnoreCase));
    if (!hasTargetRecv || !hasTargetSent)
    {
        var fallbackRows = ExtractCoinFallbackRows(tr, addr, denomWantedUpper).ToList();
        if (hasTargetSent)
            fallbackRows.RemoveAll(r => r.Direction.Equals("sent", StringComparison.OrdinalIgnoreCase));
        if (hasTargetRecv)
            fallbackRows.RemoveAll(r => r.Direction.Equals("recv", StringComparison.OrdinalIgnoreCase));
        if (fallbackRows.Count > 0)
            transferRows.AddRange(fallbackRows);
    }

    if (computeTotals)
    {
        if (cometTx is not null)
            AccumulateTotalsFromCometTx(cometTx, emitterAddr, denomWantedUpper, ref totalEmittedUopt, ref totalDistributedUopt);
        else
            AccumulateTotalsFromTxResponse(tr, emitterAddr, denomWantedUpper, ref totalEmittedUopt, ref totalDistributedUopt);
    }

    foreach (var r in transferRows)
    {
        r.TimeUtc = timeUtc;
        r.TxHash = txHash;
        r.FeeInDenomUnits = feeUnits;
        r.FeeApplied = feeApplied;
        r.Label = DetermineRowMemoFromTxResponse(tr, r, memoLabel, addr, denomWantedUpper);
        r.Memo = memoRaw?.Trim() ?? "";
        r.IsFeeTransfer = IsFeeTransferRow(r, feeUnits, feePayer, addr);
        if (string.Equals(r.Label, "staking:Delegate", StringComparison.OrdinalIgnoreCase))
            TryOverrideAmountWithDelegateFromTxResponse(tr, r, addr, denomWantedUpper);

        var key = $"{txHash}|{r.Direction}|{r.From}|{r.To}|{r.AmountInDenomUnits.ToString(CultureInfo.InvariantCulture)}|{r.Label}|{r.Memo}";
        if (seenRow.Add(key))
            rows.Add(r);
    }
}

// ---------------- 3b) Add comet fallback txs that never showed up in gRPC ----------------
foreach (var ft in cometFallbackTxs)
{
    var txHash = ft.Hash;
    var timeUtc = ft.TimeUtc;

    var (feeUnitsOpt, feePayer) = GetFeeFromCometTx(ft.Tx, denomWantedUpper);
    var feeUnits = feeUnitsOpt ?? 0m;
    var memoRaw = !string.IsNullOrWhiteSpace(ft.Tx.Base64Tx) ? ExtractMemoFromBase64Tx(ft.Tx.Base64Tx!) : "";
    var memoLabel = NormalizeMemoLabel(InferMemoLabelFromCometTx(ft.Tx));

    var transferRows = ExtractTransfersFromCometTx(ft.Tx, addr, denomWantedUpper, feeUnits, feePayer).ToList();
    if (transferRows.Count == 0) continue;

    if (computeTotals)
        AccumulateTotalsFromCometTx(ft.Tx, emitterAddr, denomWantedUpper, ref totalEmittedUopt, ref totalDistributedUopt);

    var feeApplied = feeUnits > 0m &&
                     !string.IsNullOrWhiteSpace(feePayer) &&
                     feePayer.Equals(addr, StringComparison.OrdinalIgnoreCase);

    foreach (var r in transferRows)
    {
        r.TimeUtc = timeUtc;
        r.TxHash = txHash;
        r.FeeInDenomUnits = feeUnits;
        r.FeeApplied = feeApplied;
        r.Label = DetermineRowMemoFromCometTx(ft.Tx, r, memoLabel, addr, denomWantedUpper);
        r.Memo = memoRaw?.Trim() ?? "";
        r.IsFeeTransfer = IsFeeTransferRow(r, feeUnits, feePayer, addr);
        if (string.Equals(r.Label, "staking:Delegate", StringComparison.OrdinalIgnoreCase))
            TryOverrideAmountWithDelegateFromCometTx(ft.Tx, r, addr, denomWantedUpper);

        var key = $"{txHash}|{r.Direction}|{r.From}|{r.To}|{r.AmountInDenomUnits.ToString(CultureInfo.InvariantCulture)}|{r.Label}|{r.Memo}";
        if (seenRow.Add(key))
            rows.Add(r);
    }
}

var hasNonFeeByTx = rows
    .GroupBy(r => r.TxHash, StringComparer.OrdinalIgnoreCase)
    .ToDictionary(g => g.Key, g => g.Any(r => !r.IsFeeTransfer), StringComparer.OrdinalIgnoreCase);

foreach (var r in rows)
{
    if (r.IsFeeTransfer && hasNonFeeByTx.TryGetValue(r.TxHash, out var hasNonFee) && hasNonFee)
        r.Label = "Fee";
}

if (computeTotals)
    totalDistributedUopt = SumDistributedToAddressUopt(rows, addr);

if (TotalsOnly && !CounterpartiesMode)
{
    PrintTotals(totalEmittedUopt, totalDistributedUopt, scaleInt);
    return;
}

if (HideStakingRewards)
    rows.RemoveAll(r => r.Label.Equals("Staking Reward", StringComparison.OrdinalIgnoreCase));

if (HideFeeRows)
    rows.RemoveAll(r => r.IsFeeTransfer && hasNonFeeByTx.TryGetValue(r.TxHash, out var hasNonFee) && hasNonFee);

if (OnlyRecvTransactions)
    rows.RemoveAll(r => !r.Direction.Equals("recv", StringComparison.OrdinalIgnoreCase));

if (rows.Count == 0)
{
    Console.WriteLine("(tx responses found, but no matching events in desired denom)");
    if (IncludeTotals)
        PrintTotals(totalEmittedUopt, totalDistributedUopt, scaleInt);
    return;
}

if (CounterpartiesMode)
{
    var statsByCounterparty = new Dictionary<string, (int SentCount, decimal SentUnits, int RecvCount, decimal RecvUnits)>(
        StringComparer.OrdinalIgnoreCase);

    foreach (var r in rows)
    {
        string? counterparty = null;
        bool isSent = false;

        if (r.Direction.Equals("sent", StringComparison.OrdinalIgnoreCase))
        {
            counterparty = r.To;
            isSent = true;
        }
        else if (r.Direction.Equals("recv", StringComparison.OrdinalIgnoreCase))
        {
            counterparty = r.From;
        }

        if (string.IsNullOrWhiteSpace(counterparty) ||
            counterparty.Equals("unknown", StringComparison.OrdinalIgnoreCase) ||
            counterparty.Equals("(unknown)", StringComparison.OrdinalIgnoreCase) ||
            counterparty.Equals(addr, StringComparison.OrdinalIgnoreCase))
            continue;

        if (!IncludeValidators &&
            counterparty.StartsWith("optiovaloper", StringComparison.OrdinalIgnoreCase))
            continue;

        if (!statsByCounterparty.TryGetValue(counterparty, out var stats))
            stats = (0, 0m, 0, 0m);

        if (isSent)
        {
            stats.SentCount++;
            stats.SentUnits += r.AmountInDenomUnits;
        }
        else
        {
            stats.RecvCount++;
            stats.RecvUnits += r.AmountInDenomUnits;
        }

        statsByCounterparty[counterparty] = stats;
    }

    Console.WriteLine();
    Console.WriteLine("== Counterparties ==");

    const int CpAddrWidth = 62;
    const int CpCptyWidth = 62;
    const int CpCountWidth = 8;
    const int CpAmtWidth = 16;

    Console.WriteLine(
        $"{Left("Address", CpAddrWidth)} " +
        $"{Left("Counterparty", CpCptyWidth)} " +
        $"{Right("Sent#", CpCountWidth)} " +
        $"{Right("Sent(OPT)", CpAmtWidth)} " +
        $"{Right("Recv#", CpCountWidth)} " +
        $"{Right("Recv(OPT)", CpAmtWidth)}");
    Console.WriteLine(new string('-', CpAddrWidth + 1 + CpCptyWidth + 1 + CpCountWidth + 1 + CpAmtWidth + 1 + CpCountWidth + 1 + CpAmtWidth));

    foreach (var kv in statsByCounterparty.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
    {
        var cp = kv.Key;
        var stats = kv.Value;
        var sentOpt = stats.SentUnits / scale;
        var recvOpt = stats.RecvUnits / scale;
        Console.WriteLine(
            $"{Left(addr, CpAddrWidth)} " +
            $"{Left(cp, CpCptyWidth)} " +
            $"{Right(stats.SentCount.ToString(CultureInfo.InvariantCulture), CpCountWidth)} " +
            $"{Right(sentOpt.ToString("0.000000", CultureInfo.InvariantCulture), CpAmtWidth)} " +
            $"{Right(stats.RecvCount.ToString(CultureInfo.InvariantCulture), CpCountWidth)} " +
            $"{Right(recvOpt.ToString("0.000000", CultureInfo.InvariantCulture), CpAmtWidth)}");
    }

    return;
}

if (SendRecvMode)
{
    Console.WriteLine();
    Console.WriteLine("== Send/Recv Transactions ==");

    const int TimeWidth2 = 19;
    const int DirWidth2 = 4;
    const int AddrWidth2 = 62;
    const int AmtWidth2 = 18;

    var hoursBack = Math.Clamp(SendRecvHours, 1, 24 * 365);
    var sinceUtc = DateTimeOffset.UtcNow.AddHours(-hoursBack);
    var recentRows = rows
        .Where(r => new DateTimeOffset(r.TimeUtc, TimeSpan.Zero) >= sinceUtc)
        .ToList();

    Console.WriteLine(
        $"{Left("Time", TimeWidth2)} " +
        $"{Left("Dir", DirWidth2)} " +
        $"{Left("From", AddrWidth2)} " +
        $"{Left("To", AddrWidth2)} " +
        $"{Right("Amount(OPT)", AmtWidth2)}");
    Console.WriteLine(new string('-', TimeWidth2 + 1 + DirWidth2 + 1 + AddrWidth2 + 1 + AddrWidth2 + 1 + AmtWidth2));

    foreach (var r in recentRows
        .OrderByDescending(r => r.Direction.Equals("recv", StringComparison.OrdinalIgnoreCase))
        .ThenByDescending(r =>
            Math.Abs(r.AmountInDenomUnits * (r.Direction == "sent" ? -1m : 1m) / scale))
        .ThenByDescending(r => r.TimeUtc))
    {
        var signedUnits = r.AmountInDenomUnits * (r.Direction == "sent" ? -1m : 1m);
        var amountOpt = Math.Abs(signedUnits / scale);
        var local = TimeZoneInfo.ConvertTimeFromUtc(r.TimeUtc, tz);
        Console.WriteLine(
            $"{Left(local.ToString("yyyy-MM-dd HH:mm:ss"), TimeWidth2)} " +
            $"{Left(r.Direction, DirWidth2)} " +
            $"{Left(r.From, AddrWidth2)} " +
            $"{Left(r.To, AddrWidth2)} " +
            $"{Right(amountOpt.ToString("N0", CultureInfo.InvariantCulture), AmtWidth2)}");
    }

    return;
}

// Sort rows
rows.Sort((a, b) =>
{
    var c = a.TimeUtc.CompareTo(b.TimeUtc);
    if (c != 0) return c;
    c = string.Compare(a.Direction, b.Direction, StringComparison.OrdinalIgnoreCase);
    if (c != 0) return c;
    c = string.Compare(a.From, b.From, StringComparison.OrdinalIgnoreCase);
    if (c != 0) return c;
    return string.Compare(a.To, b.To, StringComparison.OrdinalIgnoreCase);
});

// ---------------- 4) Print ledger (aligned headers, no TxHash, no commas) ----------------
Console.WriteLine();
Console.WriteLine("== Ledger ==");

// fee shown once per txhash
var feeByTx = rows
    .Where(r => r.FeeInDenomUnits > 0m)
    .GroupBy(r => r.TxHash, StringComparer.OrdinalIgnoreCase)
    .ToDictionary(g => g.Key, g => g.Max(x => x.FeeInDenomUnits), StringComparer.OrdinalIgnoreCase);

var feeShown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
var feeAppliedAlready = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

var feeAppliedByTx = rows
    .Where(r => r.FeeApplied)
    .GroupBy(r => r.TxHash, StringComparer.OrdinalIgnoreCase)
    .ToDictionary(g => g.Key, _ => true, StringComparer.OrdinalIgnoreCase);

var netDeltaUnits = rows.Sum(r =>
    r.AmountInDenomUnits * (r.Direction == "sent" ? -1m : 1m));

foreach (var tx in feeAppliedByTx.Keys)
{
    if (feeByTx.TryGetValue(tx, out var feeUnits))
        netDeltaUnits -= feeUnits;
}

var startingBalanceUnits = summary.BankBalanceUnits.HasValue
    ? summary.BankBalanceUnits.Value - netDeltaUnits
    : 0m;
if (startingBalanceUnits < 0m)
    startingBalanceUnits = 0m;

// widths
const int TimeWidth = 19;   // yyyy-MM-dd HH:mm:ss
const int DirWidth = 4;     // recv/sent
const int AmtWidth = 16;    // 8 left + '.' + 6 right + sign space
const int FeeWidth = 16;
const int RunWidth = 16;
const int HashWidth = 64;
const int LabelWidth = 16;
const int MemoWidth = 28;
const int FromWidth = 64;
const int ToWidth = 64;

static string Num6(decimal x, int width) => x.ToString("0.000000", CultureInfo.InvariantCulture).PadLeft(width);
static string Blank(int width) => new string(' ', width);

static string Left(string s, int width)
{
    if (s.Length > width) return s[..width];
    return s.PadRight(width);
}

static string Right(string s, int width)
{
    if (s.Length > width) return s[..width];
    return s.PadLeft(width);
}

// Header
Console.WriteLine(
    $"{Left("Time", TimeWidth)} " +
    $"{Left("Dir", DirWidth)} " +
    $"{Left("TxType", LabelWidth)} " +
    $"{Right("Amount(OPT)", AmtWidth)} " +
    $"{Right("Fee(OPT)", FeeWidth)} " +
    $"{Right("Running(OPT)", RunWidth)} " +
    $"{(ShowHash ? $"{Left("TxHash", HashWidth)} " : "")}" +
    $"{Left("Memo", MemoWidth)} " +
    $"{Left("From Address", FromWidth)} " +
    $"{Left("To Address", ToWidth)}"
);

var lineWidth = TimeWidth + 1 + DirWidth + 1 + LabelWidth + 1 + AmtWidth + 1 + FeeWidth + 1 + RunWidth + 1 + MemoWidth + 1 + FromWidth + 1 + ToWidth;
if (ShowHash)
    lineWidth += HashWidth + 1;
Console.WriteLine(new string('-', lineWidth));

decimal runningUnits = startingBalanceUnits;

foreach (var r in rows)
{
    var signedUnits = r.AmountInDenomUnits * (r.Direction == "sent" ? -1m : 1m);
    runningUnits += signedUnits;

    var local = TimeZoneInfo.ConvertTimeFromUtc(r.TimeUtc, tz);
    var amountOpt = signedUnits / scale;
    var runningOpt = runningUnits / scale;

    decimal feeToShowUnits = 0m;
    if (!feeShown.Contains(r.TxHash) && feeByTx.TryGetValue(r.TxHash, out var f))
    {
        feeToShowUnits = f;
        feeShown.Add(r.TxHash);
    }

    var feeOpt = feeToShowUnits / scale;

    if (r.FeeApplied && feeToShowUnits > 0m && !feeAppliedAlready.Contains(r.TxHash))
    {
        runningUnits -= feeToShowUnits;
        feeAppliedAlready.Add(r.TxHash);
        runningOpt = runningUnits / scale;
    }

    Console.WriteLine(
        $"{local:yyyy-MM-dd HH:mm:ss} " +
        $"{Left(r.Direction, DirWidth)} " +
        $"{Left(TruncateMiddle(r.Label ?? "", LabelWidth), LabelWidth)} " +
        $"{Num6(amountOpt, AmtWidth)} " +
        $"{(feeToShowUnits > 0m ? Num6(feeOpt, FeeWidth) : Blank(FeeWidth))} " +
        $"{Num6(runningOpt, RunWidth)} " +
        $"{(ShowHash ? $"{Left(r.TxHash, HashWidth)} " : "")}" +
        $"{Left(TruncateMiddle(r.Memo ?? "", MemoWidth), MemoWidth)} " +
        $"{Left(TruncateMiddle(r.From, FromWidth), FromWidth)} " +
        $"{Left(TruncateMiddle(r.To, ToWidth), ToWidth)}"
    );
}

if (!string.IsNullOrWhiteSpace(csvPath))
{
    WriteLedgerCsv(csvPath, rows, tz, scale, feeByTx, startingBalanceUnits);
    Console.WriteLine();
    Console.WriteLine($"CSV written: {Path.GetFullPath(csvPath)}");
}

Console.WriteLine(new string('-', lineWidth));
var endingUnits = summary.BankBalanceUnits ?? runningUnits;
Console.WriteLine($"Ending balance over window (incl fees): {(endingUnits / scale).ToString("0.000000", CultureInfo.InvariantCulture)} OPT");

if (IncludeTotals)
    PrintTotals(totalEmittedUopt, totalDistributedUopt, scaleInt);

// ======================================================================
// BELOW THIS POINT: ONLY METHODS + TYPES (NO TOP-LEVEL STATEMENTS)
// ======================================================================

static string? ParseCsvPathArg(string[] args)
{
    for (int i = 0; i < args.Length; i++)
    {
        var arg = args[i];
        if (arg.StartsWith("--csv=", StringComparison.OrdinalIgnoreCase))
        {
            var val = arg.Substring("--csv=".Length).Trim('"');
            return string.IsNullOrWhiteSpace(val) ? null : val;
        }

        if (string.Equals(arg, "--csv", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            var val = args[i + 1].Trim('"');
            return string.IsNullOrWhiteSpace(val) ? null : val;
        }
    }

    return null;
}

static bool ParseHelpArg(string[] args)
{
    foreach (var arg in args)
    {
        if (string.Equals(arg, "--help", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "-h", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "/?", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "help", StringComparison.OrdinalIgnoreCase))
            return true;
    }

    return false;
}

static bool ParseWebArg(string[] args)
{
    foreach (var arg in args)
    {
        if (string.Equals(arg, "--web", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "web", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "--web-server", StringComparison.OrdinalIgnoreCase))
            return true;
    }

    return false;
}

static int? ParseWebPortArg(string[] args)
{
    for (int i = 0; i < args.Length; i++)
    {
        var arg = args[i];
        if (arg.StartsWith("--web-port=", StringComparison.OrdinalIgnoreCase))
        {
            var val = arg.Substring("--web-port=".Length).Trim('"');
            if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port))
                return port;
        }

        if (string.Equals(arg, "--web-port", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            var val = args[i + 1].Trim('"');
            if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port))
                return port;
        }
    }

    return null;
}

static string? ParseWebHostArg(string[] args)
{
    for (int i = 0; i < args.Length; i++)
    {
        var arg = args[i];
        if (arg.StartsWith("--web-host=", StringComparison.OrdinalIgnoreCase))
            return arg.Substring("--web-host=".Length).Trim('"');

        if (string.Equals(arg, "--web-host", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            return args[i + 1].Trim('"');
    }

    return null;
}

static bool ParseDailySyncArg(string[] args)
{
    foreach (var arg in args)
    {
        if (string.Equals(arg, "--daily-sync", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "--sync-daily", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "daily-sync", StringComparison.OrdinalIgnoreCase))
            return true;
    }
    return false;
}

static bool ParseBackfillArg(string[] args, out bool force)
{
    force = false;
    foreach (var arg in args)
    {
        if (string.Equals(arg, "--backfill", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "backfill", StringComparison.OrdinalIgnoreCase))
        {
            force = args.Any(a => string.Equals(a, "--force", StringComparison.OrdinalIgnoreCase));
            return true;
        }
    }
    return false;
}

static async Task RunDailySyncAsync()
{
    Console.WriteLine("OPTIC - Daily Stats Sync");
    try
    {
        var syncService = new LocalDataSyncService();
        await syncService.InitializeAsync();
        Console.WriteLine("Recording today's daily statistics...");

        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var stats = new DailyStatsEntry
        {
            Date = today,
            TotalWallets = 0,
            ActiveWallets = 0,
            TxCount = 0
        };

        var result = await syncService.RecordDailyStatsAsync(stats);
        Console.WriteLine(result.message);
        Console.WriteLine(result.success ? "Daily stats recorded successfully." : "Failed to record daily stats.");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error during daily sync: {ex.Message}");
    }
}

static async Task RunBackfillAsync(
    HttpClient http,
    HttpClient rpcHttp,
    GrpcChannel channel,
    string denomWantedUpper,
    string? lcdBase,
    string? rpcBase,
    bool backfillForce)
{
    Console.WriteLine("OPTIC - Daily Stats Backfill (Block Scanner)");
    try
    {
        var syncService = new LocalDataSyncService();
        await syncService.InitializeAsync();
        Console.WriteLine("Scanning blocks from RPC node...");

        long latestHeight = 0;
        var latestBlockDoc = await ServiceUtils.TryGetJsonAsync(http, "cosmos/base/tendermint/v1beta1/latest_block", CancellationToken.None);

        if (latestBlockDoc != null)
        {
            if (latestBlockDoc.RootElement.TryGetProperty("block", out var blockEl) &&
                blockEl.TryGetProperty("header", out var headerEl) &&
                blockEl.TryGetProperty("height", out var heightEl) &&
                long.TryParse(heightEl.GetString() ?? "0", out var height))
            {
                latestHeight = height;
            }
        }
        else
        {
            Console.WriteLine("LCD endpoint not available, trying RPC endpoint...");
            latestBlockDoc = await ServiceUtils.TryGetJsonAsync(rpcHttp, "status", CancellationToken.None);

            if (latestBlockDoc != null)
            {
                if (latestBlockDoc.RootElement.TryGetProperty("result", out var result) &&
                    result.TryGetProperty("sync_info", out var syncInfo) &&
                    syncInfo.TryGetProperty("latest_block_height", out var heightEl) &&
                    long.TryParse(heightEl.GetString() ?? "0", out var height))
                {
                    latestHeight = height;
                }
            }
        }

        if (latestHeight == 0)
        {
            Console.WriteLine("Error: Could not fetch latest block from RPC node.");
            Console.WriteLine($"Tried LCD endpoint: {lcdBase}");
            Console.WriteLine($"Tried RPC endpoint: {rpcBase}");
            Console.WriteLine("\nEnsure your blockchain node is running and accessible.");
            return;
        }

        Console.WriteLine($"Latest block height: {latestHeight}");

        long startBlock = 0;
        if (!backfillForce)
        {
            var existingData = await syncService.GetAllDailyStatsAsync();
            if (existingData.Count > 0)
            {
                var lastDate = existingData.OrderByDescending(x => x.Date).First();
                Console.WriteLine($"Found existing data up to {lastDate.Date}, will scan from next day.");
                if (lastDate.EndBlockNumber.HasValue)
                    startBlock = lastDate.EndBlockNumber.Value + 1;
            }
        }

        Console.WriteLine($"Starting block scan from block {startBlock}...");
        Console.WriteLine();

        var scanner = new BlockScannerService(http, channel);
        var recordsAdded = 0;
        var lastDisplayedDate = "";

        await scanner.ScanBlocksAsync(startBlock, latestHeight, denomWantedUpper,
            async (dailyStats) =>
            {
                var exists = await syncService.DailyStatsExistsAsync(dailyStats.Date);
                if (exists && !backfillForce)
                {
                    Console.WriteLine($"  Skipped {dailyStats.Date} (already exists)");
                    return;
                }

                await syncService.InsertDailyStatsAsync(dailyStats);
                recordsAdded++;

                var wallets = dailyStats.TotalWallets?.ToString("N0") ?? "?";
                var active = dailyStats.ActiveWallets?.ToString("N0") ?? "?";
                var txs = dailyStats.TxCount?.ToString("N0") ?? "?";
                var blockStart = dailyStats.StartBlockNumber?.ToString("N0") ?? "?";
                var blockEnd = dailyStats.EndBlockNumber?.ToString("N0") ?? "?";

                Console.WriteLine($"✓ {dailyStats.Date} | Blocks: {blockStart} - {blockEnd} | Wallets: {wallets} | Active: {active} | Txs: {txs}");
                lastDisplayedDate = dailyStats.Date;
            },
            async (progressStats) =>
            {
                if (progressStats.Date != lastDisplayedDate)
                {
                    var wallets = progressStats.TotalWallets?.ToString("N0") ?? "0";
                    var txs = progressStats.TxCount?.ToString("N0") ?? "0";
                    var blockStart = progressStats.StartBlockNumber?.ToString("N0") ?? "?";
                    var blockEnd = progressStats.EndBlockNumber?.ToString("N0") ?? "?";

                    Console.Write($"\r  {progressStats.Date} | Blocks: {blockStart} - {blockEnd} | Wallets: {wallets} | Txs: {txs}          ");
                }
                await Task.CompletedTask;
            },
            CancellationToken.None);

        Console.WriteLine();
        Console.WriteLine($"Backfill complete: {recordsAdded} records added/updated");

        var validationIssues = await syncService.ValidateDailyStatsAsync();
        if (validationIssues.Count == 0)
        {
            Console.WriteLine("SQLite daily stats validation OK.");
        }
        else
        {
            Console.WriteLine("SQLite daily stats validation issues:");
            foreach (var issue in validationIssues)
                Console.WriteLine($"  - {issue}");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error during backfill: {ex.Message}");
        if (ex.InnerException != null)
            Console.WriteLine($"Details: {ex.InnerException.Message}");
    }
}

static bool ParseDistributionsArg(string[] args, out string? addrOverride)
{
    addrOverride = null;
    foreach (var arg in args)
    {
        if (string.Equals(arg, "--distributions", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "distributions", StringComparison.OrdinalIgnoreCase))
            return true;

        if (arg.StartsWith("--distributions=", StringComparison.OrdinalIgnoreCase))
        {
            var val = arg.Substring("--distributions=".Length).Trim('"');
            addrOverride = string.IsNullOrWhiteSpace(val) ? null : val;
            return true;
        }
    }

    for (int i = 0; i < args.Length; i++)
    {
        if (string.Equals(args[i], "--distributions", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            var val = args[i + 1].Trim('"');
            if (!string.IsNullOrWhiteSpace(val) && !val.StartsWith("-", StringComparison.Ordinal))
                addrOverride = val;
            return true;
        }
    }

    return false;
}

static bool ParseLocksArg(string[] args, out string? addrOverride)
{
    addrOverride = null;
    foreach (var arg in args)
    {
        if (string.Equals(arg, "--locks", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "locks", StringComparison.OrdinalIgnoreCase))
            return true;

        if (arg.StartsWith("--locks=", StringComparison.OrdinalIgnoreCase))
        {
            var val = arg.Substring("--locks=".Length).Trim('"');
            addrOverride = string.IsNullOrWhiteSpace(val) ? null : val;
            return true;
        }
    }

    for (int i = 0; i < args.Length; i++)
    {
        if (string.Equals(args[i], "--locks", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            var val = args[i + 1].Trim('"');
            if (!string.IsNullOrWhiteSpace(val) && !val.StartsWith("-", StringComparison.Ordinal))
                addrOverride = val;
            return true;
        }
    }

    return false;
}

static bool ParseLocksSummaryArg(string[] args)
{
    foreach (var arg in args)
    {
        if (string.Equals(arg, "--locks-summary", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "locks-summary", StringComparison.OrdinalIgnoreCase))
            return true;
    }

    return false;
}

static bool ParseSendRecvArg(string[] args, out string? addrOverride)
{
    addrOverride = null;
    foreach (var arg in args)
    {
        if (string.Equals(arg, "--send-recv", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "send-recv", StringComparison.OrdinalIgnoreCase))
            return true;

        if (arg.StartsWith("--send-recv=", StringComparison.OrdinalIgnoreCase))
        {
            var val = arg.Substring("--send-recv=".Length).Trim('"');
            addrOverride = string.IsNullOrWhiteSpace(val) ? null : val;
            return true;
        }
    }

    for (int i = 0; i < args.Length; i++)
    {
        if (string.Equals(args[i], "--send-recv", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            var val = args[i + 1].Trim('"');
            if (!string.IsNullOrWhiteSpace(val) && !val.StartsWith("-", StringComparison.Ordinal))
                addrOverride = val;
            return true;
        }
    }

    return false;
}

static bool ParseWalletLocksSummaryArg(string[] args, out string? addrOverride)
{
    addrOverride = null;
    foreach (var arg in args)
    {
        if (string.Equals(arg, "--wallet-locks-summary", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "wallet-locks-summary", StringComparison.OrdinalIgnoreCase))
            return true;

        if (arg.StartsWith("--wallet-locks-summary=", StringComparison.OrdinalIgnoreCase))
        {
            var val = arg.Substring("--wallet-locks-summary=".Length).Trim('"');
            addrOverride = string.IsNullOrWhiteSpace(val) ? null : val;
            return true;
        }
    }

    for (int i = 0; i < args.Length; i++)
    {
        if (string.Equals(args[i], "--wallet-locks-summary", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            var val = args[i + 1].Trim('"');
            if (!string.IsNullOrWhiteSpace(val) && !val.StartsWith("-", StringComparison.Ordinal))
                addrOverride = val;
            return true;
        }
    }

    return false;
}

static int? ParseSendRecvHoursArg(string[] args)
{
    for (int i = 0; i < args.Length; i++)
    {
        var arg = args[i];
        if (arg.StartsWith("--send-recv-hours=", StringComparison.OrdinalIgnoreCase))
        {
            var val = arg.Substring("--send-recv-hours=".Length);
            if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hours))
                return hours;
        }

        if (string.Equals(arg, "--send-recv-hours", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            if (int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hours))
                return hours;
        }
    }

    return null;
}

static bool ParseLockExtendedReportArg(string[] args)
{
    foreach (var arg in args)
    {
        if (string.Equals(arg, "--lock-extended", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "lock-extended", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "--lock-extended-report", StringComparison.OrdinalIgnoreCase))
            return true;
    }

    return false;
}

static int? ParseLockExtendedDaysArg(string[] args)
{
    for (int i = 0; i < args.Length; i++)
    {
        var arg = args[i];

        if (arg.StartsWith("--lock-extended-days=", StringComparison.OrdinalIgnoreCase))
        {
            var val = arg.Substring("--lock-extended-days=".Length);
            if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days))
                return days;
        }

        if (string.Equals(arg, "--lock-extended-days", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            if (int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var days))
                return days;
        }
    }

    return null;
}

static bool ParseTotalStakedArg(string[] args)
{
    foreach (var arg in args)
    {
        if (string.Equals(arg, "--total-staked", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "total-staked", StringComparison.OrdinalIgnoreCase))
            return true;
    }

    return false;
}

static bool ParseTotalDistributedArg(string[] args)
{
    foreach (var arg in args)
    {
        if (string.Equals(arg, "--total-distributed", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "total-distributed", StringComparison.OrdinalIgnoreCase))
            return true;
    }

    return false;
}

static bool ParseTotalsAllArg(string[] args)
{
    foreach (var arg in args)
    {
        if (string.Equals(arg, "--totals-all", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "totals-all", StringComparison.OrdinalIgnoreCase))
            return true;
    }

    return false;
}

static bool ParseCounterpartiesArg(string[] args, out string? addrOverride)
{
    addrOverride = null;
    foreach (var arg in args)
    {
        if (string.Equals(arg, "--counterparties", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "counterparties", StringComparison.OrdinalIgnoreCase))
            return true;

        if (arg.StartsWith("--counterparties=", StringComparison.OrdinalIgnoreCase))
        {
            var val = arg.Substring("--counterparties=".Length).Trim('"');
            addrOverride = string.IsNullOrWhiteSpace(val) ? null : val;
            return true;
        }
    }

    for (int i = 0; i < args.Length; i++)
    {
        if (string.Equals(args[i], "--counterparties", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            var val = args[i + 1].Trim('"');
            if (!string.IsNullOrWhiteSpace(val) && !val.StartsWith("-", StringComparison.Ordinal))
                addrOverride = val;
            return true;
        }
    }

    return false;
}

static bool ParseShowHashArg(string[] args)
{
    foreach (var arg in args)
    {
        if (string.Equals(arg, "--show-hash", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "--hash", StringComparison.OrdinalIgnoreCase))
            return true;
    }

    return false;
}

static bool ParseIncludeValidatorsArg(string[] args)
{
    foreach (var arg in args)
    {
        if (string.Equals(arg, "--include-validators", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "--validators", StringComparison.OrdinalIgnoreCase))
            return true;
    }

    return false;
}

static bool ParseWalletBalancesArg(string[] args)
{
    foreach (var arg in args)
    {
        if (string.Equals(arg, "--wallet-balances", StringComparison.OrdinalIgnoreCase))
            return true;
    }

    return false;
}

static string? ParseWalletBalancesCsvArg(string[] args)
{
    for (int i = 0; i < args.Length; i++)
    {
        var arg = args[i];
        if (arg.StartsWith("--wallet-balances-csv=", StringComparison.OrdinalIgnoreCase))
        {
            var val = arg.Substring("--wallet-balances-csv=".Length).Trim('"');
            return string.IsNullOrWhiteSpace(val) ? null : val;
        }

        if (string.Equals(arg, "--wallet-balances-csv", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            var val = args[i + 1].Trim('"');
            return string.IsNullOrWhiteSpace(val) ? null : val;
        }
    }

    return null;
}

static bool ParseWalletLocksReportArg(string[] args)
{
    foreach (var arg in args)
    {
        if (string.Equals(arg, "--wallet-locks-report", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "wallet-locks-report", StringComparison.OrdinalIgnoreCase))
            return true;
    }

    return false;
}

static string? ParseEmissionAddressArg(string[] args)
{
    for (int i = 0; i < args.Length; i++)
    {
        var arg = args[i];
        if (arg.StartsWith("--emission-address=", StringComparison.OrdinalIgnoreCase))
        {
            var val = arg.Substring("--emission-address=".Length).Trim('"');
            return string.IsNullOrWhiteSpace(val) ? null : val;
        }

        if (string.Equals(arg, "--emission-address", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            var val = args[i + 1].Trim('"');
            return string.IsNullOrWhiteSpace(val) ? null : val;
        }
    }

    return null;
}

static int? ParseCmcDailyYearArg(string[] args)
{
    for (int i = 0; i < args.Length; i++)
    {
        var arg = args[i];
        if (string.Equals(arg, "--cmc-daily", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            if (int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var year))
                return year;
        }

        if (arg.StartsWith("--cmc-daily=", StringComparison.OrdinalIgnoreCase))
        {
            var val = arg.Substring("--cmc-daily=".Length);
            if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year))
                return year;
        }
    }

    return null;
}

static string? ParseCmcOutPathArg(string[] args)
{
    for (int i = 0; i < args.Length; i++)
    {
        var arg = args[i];
        if (string.Equals(arg, "--out", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            var val = args[i + 1].Trim('"');
            return string.IsNullOrWhiteSpace(val) ? null : val;
        }

        if (arg.StartsWith("--out=", StringComparison.OrdinalIgnoreCase))
        {
            var val = arg.Substring("--out=".Length).Trim('"');
            return string.IsNullOrWhiteSpace(val) ? null : val;
        }
    }

    return null;
}

static int? ParseCmcIdArg(string[] args)
{
    for (int i = 0; i < args.Length; i++)
    {
        var arg = args[i];
        if (string.Equals(arg, "--cmc-id", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            if (int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                return id;
        }

        if (arg.StartsWith("--cmc-id=", StringComparison.OrdinalIgnoreCase))
        {
            var val = arg.Substring("--cmc-id=".Length);
            if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                return id;
        }
    }

    return null;
}

static bool ParseTotalsOnlyArg(string[] args)
{
    foreach (var arg in args)
    {
        if (string.Equals(arg, "--totals-only", StringComparison.OrdinalIgnoreCase))
            return true;
    }

    return false;
}

static bool ParseIncludeTotalsArg(string[] args)
{
    bool? include = null;

    foreach (var arg in args)
    {
        if (string.Equals(arg, "--no-totals", StringComparison.OrdinalIgnoreCase))
        {
            include = false;
            continue;
        }

        if (string.Equals(arg, "--totals", StringComparison.OrdinalIgnoreCase))
        {
            include = true;
            continue;
        }

        if (arg.StartsWith("--totals=", StringComparison.OrdinalIgnoreCase))
        {
            var val = arg.Substring("--totals=".Length);
            if (bool.TryParse(val, out var parsed))
                include = parsed;
        }
    }

    return include ?? true;
}

static bool ParseNetworkTotalsArg(string[] args)
{
    foreach (var arg in args)
    {
        if (string.Equals(arg, "--network-totals", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "--totals-network", StringComparison.OrdinalIgnoreCase))
            return true;
    }

    return false;
}

static bool ParseDryTotalsArg(string[] args)
{
    foreach (var arg in args)
    {
        if (string.Equals(arg, "--dry-totals", StringComparison.OrdinalIgnoreCase))
            return true;
    }

    return false;
}

static bool ParseStatusArg(string[] args)
{
    foreach (var arg in args)
    {
        if (string.Equals(arg, "--status", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "--node-status", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "status", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "node-status", StringComparison.OrdinalIgnoreCase))
            return true;
    }

    return false;
}

static bool ParseValidatorsNodesArg(string[] args)
{
    foreach (var arg in args)
    {
        if (string.Equals(arg, "--validators-nodes", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "validators-nodes", StringComparison.OrdinalIgnoreCase))
            return true;
    }

    return false;
}

static bool ParseWalletCountArg(string[] args)
{
    foreach (var arg in args)
    {
        if (string.Equals(arg, "--wallet-count", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "wallet-count", StringComparison.OrdinalIgnoreCase))
            return true;
    }

    return false;
}

static bool ParseMultiSendSumArg(string[] args)
{
    foreach (var arg in args)
    {
        if (string.Equals(arg, "--multisend-sum", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "multisend-sum", StringComparison.OrdinalIgnoreCase))
            return true;
    }

    return false;
}

static string? ParseMultiSendFromArg(string[] args)
{
    for (int i = 0; i < args.Length; i++)
    {
        var arg = args[i];
        if (arg.StartsWith("--from=", StringComparison.OrdinalIgnoreCase))
        {
            var val = arg.Substring("--from=".Length).Trim('"');
            return string.IsNullOrWhiteSpace(val) ? null : val;
        }

        if (string.Equals(arg, "--from", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            var val = args[i + 1].Trim('"');
            return string.IsNullOrWhiteSpace(val) ? null : val;
        }
    }

    return null;
}

static long? ParseLookbackBlocksArg(string[] args)
{
    for (int i = 0; i < args.Length; i++)
    {
        var arg = args[i];
        if (arg.StartsWith("--lookback-blocks=", StringComparison.OrdinalIgnoreCase))
        {
            var val = arg.Substring("--lookback-blocks=".Length);
            if (long.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
        }

        if (string.Equals(arg, "--lookback-blocks", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            if (long.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
        }
    }

    return null;
}

static string? ParseMultiSendOutPathArg(string[] args)
{
    for (int i = 0; i < args.Length; i++)
    {
        var arg = args[i];
        if (arg.StartsWith("--out=", StringComparison.OrdinalIgnoreCase))
        {
            var val = arg.Substring("--out=".Length).Trim('"');
            return string.IsNullOrWhiteSpace(val) ? null : val;
        }

        if (string.Equals(arg, "--out", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            var val = args[i + 1].Trim('"');
            return string.IsNullOrWhiteSpace(val) ? null : val;
        }
    }

    return null;
}

static bool ParseVerboseScanArg(string[] args)
{
    foreach (var arg in args)
    {
        if (string.Equals(arg, "--verbose-scan", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "--verbose", StringComparison.OrdinalIgnoreCase))
            return true;
    }

    return false;
}

static string? ParseEmitterArg(string[] args)
{
    for (int i = 0; i < args.Length; i++)
    {
        var arg = args[i];
        if (arg.StartsWith("--emitter=", StringComparison.OrdinalIgnoreCase))
        {
            var val = arg.Substring("--emitter=".Length).Trim('"');
            return string.IsNullOrWhiteSpace(val) ? null : val;
        }

        if (string.Equals(arg, "--emitter", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            var val = args[i + 1].Trim('"');
            return string.IsNullOrWhiteSpace(val) ? null : val;
        }
    }

    return null;
}

static bool ParseBlockScanMultiSendArg(string[] args)
{
    foreach (var arg in args)
    {
        if (string.Equals(arg, "--scan-multisend-blocks", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "--blockscan-multisend", StringComparison.OrdinalIgnoreCase))
            return true;
    }

    return false;
}

static long? ParseHeightArg(string[] args, string name)
{
    for (int i = 0; i < args.Length; i++)
    {
        var arg = args[i];
        if (arg.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
        {
            var val = arg.Substring(name.Length + 1);
            if (long.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
        }

        if (string.Equals(arg, name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            if (long.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
        }
    }

    return null;
}

static int? ParseIntArg(string[] args, string name)
{
    for (int i = 0; i < args.Length; i++)
    {
        var arg = args[i];
        if (arg.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
        {
            var val = arg.Substring(name.Length + 1);
            if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
        }

        if (string.Equals(arg, name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            if (int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
        }
    }

    return null;
}

static List<string>? ParseSendersArg(string[] args)
{
    for (int i = 0; i < args.Length; i++)
    {
        var arg = args[i];
        if (arg.StartsWith("--senders=", StringComparison.OrdinalIgnoreCase))
        {
            var val = arg.Substring("--senders=".Length);
            return SplitSenders(val);
        }

        if (string.Equals(arg, "--senders", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            return SplitSenders(args[i + 1]);
        }
    }

    return null;
}

static List<string> SplitSenders(string value)
{
    return value
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(s => !string.IsNullOrWhiteSpace(s))
        .ToList();
}

static void PrintHelp()
{
    Console.WriteLine("OPTIC - Optio Protocol Telemetry & Intelligence Center");
    Console.WriteLine();
    Console.WriteLine("What it does:");
    Console.WriteLine("  - Default: prints balances summary and a ledger for the configured address.");
    Console.WriteLine("  - Modes: multisend sum, multisend block scan, wallet balances, total staked, total distributed, totals all, network totals, locks, lock-extended export, CMC daily export, dry totals.");
    Console.WriteLine();
    Console.WriteLine("Config:");
    Console.WriteLine($"  Required file: {ConfigFile}");
    Console.WriteLine("  Required keys:");
    Console.WriteLine("    addr=optio1...");
    Console.WriteLine("  Optional keys (defaults in brackets):");
    Console.WriteLine($"    grpc={DefaultGrpc}");
    Console.WriteLine($"    rpc={DefaultRpcBase}");
    Console.WriteLine($"    lcd={DefaultLcdBase}");
    Console.WriteLine($"    denom={DefaultDenom}");
    Console.WriteLine($"    lookbackDays={DefaultLookbackDays}");
    Console.WriteLine($"    pageLimit={DefaultPageLimit} (clamped to 100)");
    Console.WriteLine($"    maxPages={DefaultMaxPages}");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  --help, -h, /?, help");
    Console.WriteLine("      Show this help text.");
    Console.WriteLine("  --web | web | --web-server");
    Console.WriteLine("      Start the local web dashboard.");
    Console.WriteLine("  --web-host <host>");
    Console.WriteLine("      Bind host for web dashboard (default: 127.0.0.1).");
    Console.WriteLine("  --web-port <port>");
    Console.WriteLine("      Bind port for web dashboard (default: 5070).");
    Console.WriteLine("  --distributions [addr] | --distributions=<addr> | distributions");
    Console.WriteLine("      Run the default balances summary + ledger output.");
    Console.WriteLine("      Optional addr overrides addr= in optic.conf.");
    Console.WriteLine("  --locks [addr] | --locks=<addr> | locks");
    Console.WriteLine("      Print liquid, staked, and active lockups for an address.");
    Console.WriteLine("      Optional addr overrides addr= in optic.conf.");
    Console.WriteLine("  --locks-summary | locks-summary");
    Console.WriteLine("      Print a summary of all active lockups grouped by remaining lock length.");
    Console.WriteLine("  --lock-extended | --lock-extended-report | lock-extended");
    Console.WriteLine("      Export lock_extended transactions to optio-lock-extended.csv.");
    Console.WriteLine("  --lock-extended-days <n>");
    Console.WriteLine("      Look back N days for lock_extended export (default: 7).");
    Console.WriteLine("  --counterparties [addr] | --counterparties=<addr> | counterparties");
    Console.WriteLine("      Print unique addresses the wallet has sent to or received from.");
    Console.WriteLine("      Optional addr overrides addr= in optic.conf.");
    Console.WriteLine("      Validator (optiovaloper...) addresses are excluded by default.");
    Console.WriteLine("  --send-recv [addr] | --send-recv=<addr> | send-recv");
    Console.WriteLine("      Print send/recv transactions with comma-grouped whole OPT amounts.");
    Console.WriteLine("  --send-recv-hours <n>");
    Console.WriteLine("      Look back N hours for send/recv output (default: 24).");
    Console.WriteLine("  --include-validators | --validators");
    Console.WriteLine("      Include validator (optiovaloper...) addresses in counterparties output.");
    Console.WriteLine("  --show-hash | --hash");
    Console.WriteLine("      Show TxHash in the default ledger output.");
    Console.WriteLine("  --csv <path>");
    Console.WriteLine("      Write the ledger output to CSV.");
    Console.WriteLine("  --totals-only");
    Console.WriteLine("      Only print totals (skip full ledger).");
    Console.WriteLine("  --totals | --no-totals | --totals=true|false");
    Console.WriteLine("      Include or exclude totals (default: true).");
    Console.WriteLine("  --network-totals | --totals-network");
    Console.WriteLine("      Print all-time network totals and exit.");
    Console.WriteLine("  --dry-totals");
    Console.WriteLine("      Run totals accumulator dry run and exit.");
    Console.WriteLine("  --status | --node-status | status");
    Console.WriteLine("      Show CometBFT node sync status and exit.");
    Console.WriteLine("  --validators-nodes | validators-nodes");
    Console.WriteLine("      List bonded validators with best-effort IPs and staked OPT.");
    Console.WriteLine("  --wallet-count | wallet-count");
    Console.WriteLine("      Print total number of wallet addresses and exit.");
    Console.WriteLine("  --emitter <addr>");
    Console.WriteLine($"      Emission address for totals (default: {DistributionSourceAddress}).");
    Console.WriteLine("  --verbose | --verbose-scan");
    Console.WriteLine("      Print additional scan progress information.");
    Console.WriteLine();
    Console.WriteLine("MultiSend sum mode:");
    Console.WriteLine("  --multisend-sum | multisend-sum");
    Console.WriteLine("      Summarize multisend totals for a sender and exit.");
    Console.WriteLine("  --from <addr>");
    Console.WriteLine("      Sender address (default: optio103r5ejt3gqyghvyel86hs5faru55r3w9kl4dst).");
    Console.WriteLine("  --lookback-blocks <n>");
    Console.WriteLine("      Look back N blocks from latest height.");
    Console.WriteLine("  --out <path>");
    Console.WriteLine("      Write multisend CSV output.");
    Console.WriteLine("  --start-height <n> | --end-height <n>");
    Console.WriteLine("      Restrict the height range (overrides lookback in some modes).");
    Console.WriteLine("  --height <n>");
    Console.WriteLine("      Query a specific block height for wallet balances/total staked (default: 1062).");
    Console.WriteLine();
    Console.WriteLine("MultiSend block scan mode:");
    Console.WriteLine("  --scan-multisend-blocks | --blockscan-multisend");
    Console.WriteLine("      Scan blocks for multisend activity and exit.");
    Console.WriteLine("  --top-senders <n>");
    Console.WriteLine("      Top N senders to display (default: 20).");
    Console.WriteLine("  --senders <addr1,addr2,...>");
    Console.WriteLine("      Comma-separated sender whitelist.");
    Console.WriteLine();
    Console.WriteLine("Wallet balances mode:");
    Console.WriteLine("  --wallet-balances");
    Console.WriteLine("      Print balances for all wallets.");
    Console.WriteLine("  --wallet-balances-csv <path>");
    Console.WriteLine("      Write wallet balances CSV.");
    Console.WriteLine("  --wallet-locks-report | wallet-locks-report");
    Console.WriteLine("      Write wallet balances + lock buckets CSV reports.");
    Console.WriteLine("  --wallet-locks-summary [addr] | --wallet-locks-summary=<addr> | wallet-locks-summary");
    Console.WriteLine("      Print a balance + lock bucket summary for one address.");
    Console.WriteLine("  --total-staked | total-staked");
    Console.WriteLine("      Print total staked OPT across all wallets.");
    Console.WriteLine("  --total-distributed | total-distributed");
    Console.WriteLine("      Print total distributed OPT across all wallets.");
    Console.WriteLine("  --totals-all | totals-all");
    Console.WriteLine("      Print total distributed, staked, and locked OPT across all wallets.");
    Console.WriteLine("  --emission-address <addr>");
    Console.WriteLine($"      Emission address for totals (default: {DistributionSourceAddress}).");
    Console.WriteLine();
    Console.WriteLine("CoinMarketCap daily export:");
    Console.WriteLine("  --cmc-daily <year>");
    Console.WriteLine("      Export daily CMC OHLC data for the given year.");
    Console.WriteLine("  --cmc-id <id>");
    Console.WriteLine("      CoinMarketCap asset id (default: 35828).");
    Console.WriteLine("  --out <path>");
    Console.WriteLine("      Output CSV path (default: optio_daily_2025_cmc.csv).");
    Console.WriteLine();
    Console.WriteLine("Data sync & analytics:");
    Console.WriteLine("  --daily-sync | --sync-daily | daily-sync");
    Console.WriteLine("      Record today's daily statistics to SQLite database.");
    Console.WriteLine("  --backfill | backfill");
    Console.WriteLine("      Fill missing daily statistics from first transaction to today.");
    Console.WriteLine("  --force");
    Console.WriteLine("      With --backfill, recompute existing dates (overwrite).");
    Console.WriteLine();
    Console.WriteLine("Examples:");
    Console.WriteLine("  dotnet run -- --distributions");
    Console.WriteLine("  dotnet run -- --distributions=optio1... --show-hash");
    Console.WriteLine("  dotnet run -- --locks");
    Console.WriteLine("  dotnet run -- --locks-summary");
    Console.WriteLine("  dotnet run -- --lock-extended --lock-extended-days 7");
    Console.WriteLine("  dotnet run -- --counterparties=optio1...");
    Console.WriteLine("  dotnet run -- --total-staked");
    Console.WriteLine("  dotnet run -- --total-distributed --start-height 1 --end-height 500000");
    Console.WriteLine("  dotnet run -- --totals-all --start-height 1 --end-height 500000");
}

static void WriteLedgerCsv(
    string path,
    List<LedgerRow> rows,
    TimeZoneInfo tz,
    decimal scale,
    Dictionary<string, decimal> feeByTx,
    decimal startingBalanceUnits)
{
    var lines = new List<string>(rows.Count + 1)
    {
        "Time,Dir,TxType,Amount(OPT),Fee(OPT),Running(OPT),TxHash,Memo,From,To"
    };

    var feeShown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var feeAppliedAlready = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    decimal runningUnits = startingBalanceUnits;

    foreach (var r in rows)
    {
        var signedUnits = r.AmountInDenomUnits * (r.Direction == "sent" ? -1m : 1m);
        runningUnits += signedUnits;

        decimal feeToShowUnits = 0m;
        if (!feeShown.Contains(r.TxHash) && feeByTx.TryGetValue(r.TxHash, out var f))
        {
            feeToShowUnits = f;
            feeShown.Add(r.TxHash);
        }

        var local = TimeZoneInfo.ConvertTimeFromUtc(r.TimeUtc, tz);
        var amountOpt = signedUnits / scale;
        var feeOpt = feeToShowUnits / scale;
        if (r.FeeApplied && feeToShowUnits > 0m && !feeAppliedAlready.Contains(r.TxHash))
        {
            runningUnits -= feeToShowUnits;
            feeAppliedAlready.Add(r.TxHash);
        }
        var runningOpt = runningUnits / scale;

        var line = string.Join(",",
            CsvEscape(local.ToString("yyyy-MM-dd HH:mm:ss")),
            CsvEscape(r.Direction),
            CsvEscape(r.Label),
            CsvEscape(amountOpt.ToString("0.000000", CultureInfo.InvariantCulture)),
            CsvEscape(feeOpt == 0m ? "" : feeOpt.ToString("0.000000", CultureInfo.InvariantCulture)),
            CsvEscape(runningOpt.ToString("0.000000", CultureInfo.InvariantCulture)),
            CsvEscape(r.TxHash),
            CsvEscape(r.Memo),
            CsvEscape(r.From),
            CsvEscape(r.To)
        );

        lines.Add(line);
    }

    File.WriteAllLines(path, lines);
}

static string CsvEscape(string? value)
{
    var s = value ?? "";
    if (s.Contains('"'))
        s = s.Replace("\"", "\"\"");
    if (s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains('\r'))
        return $"\"{s}\"";
    return s;
}

static void WriteWalletBalancesCsv(string path, List<WalletBalance> wallets)
{
    var lines = new List<string>(wallets.Count + 1)
    {
        "address,wallet_balance_opt,staked_opt,unbonding_opt"
    };

    foreach (var w in wallets.OrderBy(w => w.Address, StringComparer.OrdinalIgnoreCase))
    {
        var line = string.Join(",",
            CsvEscape(w.Address),
            CsvEscape(w.WalletBalanceOPT.ToString("0.000000", CultureInfo.InvariantCulture)),
            CsvEscape(w.StakedOPT.ToString("0.000000", CultureInfo.InvariantCulture)),
            CsvEscape(w.UnbondingOPT.ToString("0.000000", CultureInfo.InvariantCulture)));
        lines.Add(line);
    }

    File.WriteAllLines(path, lines);
}

static void PrintWalletSummary(List<WalletBalance> wallets)
{
    var totalWallet = wallets.Sum(w => w.WalletBalanceOPT);
    var totalStaked = wallets.Sum(w => w.StakedOPT);
    var totalUnbonding = wallets.Sum(w => w.UnbondingOPT);
    var totalCombined = wallets.Sum(w => w.TotalOPT);

    Console.WriteLine($"Total Wallet Addresses: {wallets.Count}");
    Console.WriteLine($"Total Wallet Balance:   {totalWallet.ToString("0.000000", CultureInfo.InvariantCulture)} OPT");
    Console.WriteLine($"Total Staked:           {totalStaked.ToString("0.000000", CultureInfo.InvariantCulture)} OPT");
    Console.WriteLine($"Total Unbonding:        {totalUnbonding.ToString("0.000000", CultureInfo.InvariantCulture)} OPT");
    Console.WriteLine($"Total Combined:         {totalCombined.ToString("0.000000", CultureInfo.InvariantCulture)} OPT");
    Console.WriteLine();

    const int AddressWidth = 52;
    const int AmountWidth = 18;

    Console.WriteLine(
        $"{Left("Address", AddressWidth)} " +
        $"{Right("Wallet(OPT)", AmountWidth)} " +
        $"{Right("Staked(OPT)", AmountWidth)} " +
        $"{Right("Unbonding(OPT)", AmountWidth)}");
    Console.WriteLine(new string('-', AddressWidth + 1 + AmountWidth + 1 + AmountWidth + 1 + AmountWidth));

    foreach (var w in wallets.OrderBy(w => w.Address, StringComparer.OrdinalIgnoreCase))
    {
        Console.WriteLine(
            $"{Left(TruncateMiddle(w.Address, AddressWidth), AddressWidth)} " +
            $"{Right(w.WalletBalanceOPT.ToString("0.000000", CultureInfo.InvariantCulture), AmountWidth)} " +
            $"{Right(w.StakedOPT.ToString("0.000000", CultureInfo.InvariantCulture), AmountWidth)} " +
            $"{Right(w.UnbondingOPT.ToString("0.000000", CultureInfo.InvariantCulture), AmountWidth)}");
    }
}

static void PrintWalletTotals(List<WalletBalance> wallets)
{
    var totalWallet = wallets.Sum(w => w.WalletBalanceOPT);
    var totalStaked = wallets.Sum(w => w.StakedOPT);
    var totalCombined = totalWallet + totalStaked;

    Console.WriteLine();
    Console.WriteLine("== Totals ==");
    Console.WriteLine($"Wallet Balance OPT: {totalWallet.ToString("0.000000", CultureInfo.InvariantCulture)}");
    Console.WriteLine($"Staked OPT:         {totalStaked.ToString("0.000000", CultureInfo.InvariantCulture)}");
    Console.WriteLine($"Combined OPT:       {totalCombined.ToString("0.000000", CultureInfo.InvariantCulture)}");
}

static async Task<List<WalletBalance>> GetAllOptWalletsWithStakingAsync(
    HttpClient http,
    string denomWantedUpper,
    decimal scale,
    long? height,
    string emissionAddr)
{
    var excludeAddresses = new List<string>
    {
        DistributionSourceAddress,
        emissionAddr
    };

    var addresses = height.HasValue && height.Value > 0
        ? await GetAllWalletAddressesUpToHeightAsync(http, height.Value, excludeAddresses)
        : await GetAllWalletAddressesAsync(http, null);
    var results = new List<WalletBalance>();
    var resultsLock = new object();
    var semaphore = new SemaphoreSlim(20);
    var tasks = new List<Task>(addresses.Count);

    foreach (var addr in addresses)
    {
        await semaphore.WaitAsync();
        tasks.Add(Task.Run(async () =>
        {
            try
            {
                var walletUnits = await GetWalletBalanceUnitsAsync(http, addr, denomWantedUpper, height);
                var stakedUnits = await GetDelegatedUnitsAsync(http, addr, denomWantedUpper, height);
                var unbondingUnits = await GetUnbondingUnitsAsync(http, addr, denomWantedUpper, height);

                var entry = new WalletBalance
                {
                    Address = addr,
                    WalletBalanceOPT = walletUnits / scale,
                    StakedOPT = stakedUnits / scale,
                    UnbondingOPT = unbondingUnits / scale,
                };

                lock (resultsLock)
                    results.Add(entry);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"WARN: Failed to fetch balances for {addr}: {ex.Message}");
            }
            finally
            {
                semaphore.Release();
            }
        }));
    }

    await Task.WhenAll(tasks);
    return results;
}

static async Task<List<string>> GetAllWalletAddressesAsync(HttpClient http, long? height)
{
    var addresses = new List<string>();
    string? nextKey = null;

    while (true)
    {
        var url = "cosmos/auth/v1beta1/accounts";
        if (!string.IsNullOrWhiteSpace(nextKey))
            url += $"?pagination.key={Uri.EscapeDataString(nextKey)}";

        using var doc = await GetJsonWithRetryAsync(http, url, height);
        if (doc is null) break;

        if (!doc.RootElement.TryGetProperty("accounts", out var accounts) ||
            accounts.ValueKind != JsonValueKind.Array)
            break;

        foreach (var acct in accounts.EnumerateArray())
        {
            if (IsModuleAccount(acct)) continue;
            var addr = TryGetAccountAddress(acct);
            if (!string.IsNullOrWhiteSpace(addr))
                addresses.Add(addr);
        }

        nextKey = GetNextKey(doc.RootElement);
        if (string.IsNullOrWhiteSpace(nextKey)) break;
    }

    return addresses;
}

static async Task<List<string>> GetAllWalletAddressesUpToHeightAsync(
    HttpClient http,
    long height,
    IEnumerable<string> excludeAddresses)
{
    var addresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    if (height <= 0) return addresses.ToList();
    var exclude = new HashSet<string>(excludeAddresses.Where(a => !string.IsNullOrWhiteSpace(a)),
        StringComparer.OrdinalIgnoreCase);

    for (long blockHeight = 1; blockHeight <= height; blockHeight++)
    {
        var url = $"cosmos/base/tendermint/v1beta1/blocks/{blockHeight}";
        using var doc = await GetJsonWithRetryAsync(http, url, null);
        if (doc is null) continue;

        if (!doc.RootElement.TryGetProperty("block", out var blockEl) ||
            !blockEl.TryGetProperty("data", out var dataEl) ||
            !dataEl.TryGetProperty("txs", out var txsEl) ||
            txsEl.ValueKind != JsonValueKind.Array)
            continue;

        foreach (var txBase64 in txsEl.EnumerateArray())
        {
            var txStr = txBase64.GetString();
            if (string.IsNullOrWhiteSpace(txStr)) continue;

            try
            {
                var txBytes = Convert.FromBase64String(txStr);
                var txText = System.Text.Encoding.UTF8.GetString(txBytes);
                ExtractAddressesFromText(txText, addresses);
            }
            catch
            {
                // skip invalid tx payloads
            }
        }
    }

    addresses.RemoveWhere(addr => exclude.Contains(addr));
    return addresses.ToList();
}

static void ExtractAddressesFromText(string text, HashSet<string> addresses)
{
    if (string.IsNullOrWhiteSpace(text)) return;
    var matches = System.Text.RegularExpressions.Regex.Matches(text, @"optio1[a-z0-9]{38,58}");
    foreach (System.Text.RegularExpressions.Match match in matches)
        addresses.Add(match.Value);
}

static bool IsModuleAccount(JsonElement account)
{
    if (account.TryGetProperty("@type", out var typeEl))
    {
        var type = typeEl.GetString() ?? "";
        if (type.Contains("ModuleAccount", StringComparison.OrdinalIgnoreCase))
            return true;
    }

    if (account.TryGetProperty("name", out var nameEl))
    {
        var name = nameEl.GetString();
        if (!string.IsNullOrWhiteSpace(name))
            return true;
    }

    return false;
}

static string? TryGetAccountAddress(JsonElement account)
{
    if (account.TryGetProperty("address", out var addrEl))
    {
        var addr = addrEl.GetString();
        if (!string.IsNullOrWhiteSpace(addr)) return addr;
    }

    if (account.TryGetProperty("base_account", out var baseAcct))
    {
        if (baseAcct.TryGetProperty("address", out var addr2))
        {
            var addr = addr2.GetString();
            if (!string.IsNullOrWhiteSpace(addr)) return addr;
        }

        if (baseAcct.TryGetProperty("base_account", out var inner) &&
            inner.TryGetProperty("address", out var addr3))
        {
            var addr = addr3.GetString();
            if (!string.IsNullOrWhiteSpace(addr)) return addr;
        }
    }

    return null;
}

static string? GetNextKey(JsonElement root)
{
    if (!root.TryGetProperty("pagination", out var pag)) return null;
    if (!pag.TryGetProperty("next_key", out var nk)) return null;
    var val = nk.GetString();
    return string.IsNullOrWhiteSpace(val) ? null : val;
}

static async Task<decimal> GetWalletBalanceUnitsAsync(HttpClient http, string address, string denomWantedUpper, long? height)
{
    string? nextKey = null;
    decimal total = 0m;

    while (true)
    {
        var url = $"cosmos/bank/v1beta1/balances/{address}";
        if (!string.IsNullOrWhiteSpace(nextKey))
            url += $"?pagination.key={Uri.EscapeDataString(nextKey)}";

        using var doc = await GetJsonWithRetryAsync(http, url, height);
        if (doc is null) break;

        if (doc.RootElement.TryGetProperty("balances", out var balances) &&
            balances.ValueKind == JsonValueKind.Array)
        {
            foreach (var coin in balances.EnumerateArray())
            {
                var denom = coin.TryGetProperty("denom", out var d) ? d.GetString() : null;
                var amt = coin.TryGetProperty("amount", out var a) ? a.GetString() : null;
                if (string.IsNullOrWhiteSpace(denom) || string.IsNullOrWhiteSpace(amt)) continue;
                if (!string.Equals(denom.ToUpperInvariant(), denomWantedUpper, StringComparison.Ordinal)) continue;
                if (decimal.TryParse(amt, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                    total += n;
            }
        }

        nextKey = GetNextKey(doc.RootElement);
        if (string.IsNullOrWhiteSpace(nextKey)) break;
    }

    return total;
}

static async Task<decimal> GetDelegatedUnitsAsync(HttpClient http, string address, string denomWantedUpper, long? height)
{
    string? nextKey = null;
    decimal total = 0m;

    while (true)
    {
        var url = $"cosmos/staking/v1beta1/delegations/{address}";
        if (!string.IsNullOrWhiteSpace(nextKey))
            url += $"?pagination.key={Uri.EscapeDataString(nextKey)}";

        using var doc = await GetJsonWithRetryAsync(http, url, height);
        if (doc is null) break;

        if (doc.RootElement.TryGetProperty("delegation_responses", out var delgs) &&
            delgs.ValueKind == JsonValueKind.Array)
        {
            foreach (var delg in delgs.EnumerateArray())
            {
                if (!delg.TryGetProperty("balance", out var bal)) continue;
                var denom = bal.TryGetProperty("denom", out var d) ? d.GetString() : null;
                var amt = bal.TryGetProperty("amount", out var a) ? a.GetString() : null;
                if (string.IsNullOrWhiteSpace(denom) || string.IsNullOrWhiteSpace(amt)) continue;
                if (!string.Equals(denom.ToUpperInvariant(), denomWantedUpper, StringComparison.Ordinal)) continue;
                if (decimal.TryParse(amt, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                    total += n;
            }
        }

        nextKey = GetNextKey(doc.RootElement);
        if (string.IsNullOrWhiteSpace(nextKey)) break;
    }

    return total;
}

static async Task<decimal> GetUnbondingUnitsAsync(HttpClient http, string address, string denomWantedUpper, long? height)
{
    string? nextKey = null;
    decimal total = 0m;

    while (true)
    {
        var url = $"cosmos/staking/v1beta1/delegators/{address}/unbonding_delegations";
        if (!string.IsNullOrWhiteSpace(nextKey))
            url += $"?pagination.key={Uri.EscapeDataString(nextKey)}";

        using var doc = await GetJsonWithRetryAsync(http, url, height);
        if (doc is null) break;

        if (doc.RootElement.TryGetProperty("unbonding_responses", out var responses) &&
            responses.ValueKind == JsonValueKind.Array)
        {
            foreach (var resp in responses.EnumerateArray())
            {
                if (!resp.TryGetProperty("entries", out var entries) ||
                    entries.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var entry in entries.EnumerateArray())
                {
                    var amt = entry.TryGetProperty("balance", out var a) ? a.GetString() : null;
                    if (string.IsNullOrWhiteSpace(amt)) continue;
                    if (decimal.TryParse(amt, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                        total += n;
                }
            }
        }

        nextKey = GetNextKey(doc.RootElement);
        if (string.IsNullOrWhiteSpace(nextKey)) break;
    }

    return total;
}

static async Task<JsonDocument?> GetJsonWithRetryAsync(HttpClient http, string url, long? height = null)
{
    const int maxAttempts = 4;
    int delayMs = 200;

    for (int attempt = 1; attempt <= maxAttempts; attempt++)
    {
        try
        {
            var requestUrl = AppendHeightQuery(url, height);
            using var resp = await http.GetAsync(requestUrl);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync();
                return JsonDocument.Parse(json);
            }

            var status = (int)resp.StatusCode;
            if (status == 429 || status >= 500)
            {
                await Task.Delay(delayMs);
                delayMs *= 2;
                continue;
            }

            Console.WriteLine($"WARN: GET {requestUrl} failed with status {status}");
            return null;
        }
        catch (Exception ex)
        {
            if (attempt == maxAttempts)
            {
                Console.WriteLine($"WARN: GET {AppendHeightQuery(url, height)} failed: {ex.Message}");
                return null;
            }

            await Task.Delay(delayMs);
            delayMs *= 2;
        }
    }

    return null;
}

static string AppendHeightQuery(string url, long? height)
{
    if (!height.HasValue || height.Value <= 0) return url;
    if (url.Contains("height=", StringComparison.OrdinalIgnoreCase)) return url;
    var separator = url.Contains('?') ? "&" : "?";
    return $"{url}{separator}height={height.Value.ToString(CultureInfo.InvariantCulture)}";
}

static async Task<int> RunCmcDailyAsync(int year, int cmcId, string outPath, CancellationToken ct)
{
    try
    {
        var start = new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var end = start.AddYears(1);
        var url = BuildCmcChartUrl(cmcId, start.ToUnixTimeSeconds(), end.ToUnixTimeSeconds());
        using var doc = await FetchCmcJsonAsync(url, ct);
        if (doc is null)
        {
            Console.WriteLine("Failed to fetch CoinMarketCap data.");
            return 1;
        }

        var rows = ParsePointsToDaily(doc.RootElement, year);
        WriteCmcDailyCsv(rows, outPath);
        PrintCmcDailySummary(rows);
        return 0;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"ERROR: {ex.Message}");
        return 1;
    }
}

static string BuildCmcChartUrl(int cmcId, long unixStart, long unixEnd)
{
    return $"https://api.coinmarketcap.com/data-api/v3/cryptocurrency/detail/chart?id={cmcId}&range={unixStart}~{unixEnd}";
}

static async Task<JsonDocument?> FetchCmcJsonAsync(string url, CancellationToken ct)
{
    using var http = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    http.DefaultRequestHeaders.UserAgent.ParseAdd(
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
    http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
    http.DefaultRequestHeaders.Referrer = new Uri("https://coinmarketcap.com/currencies/optio/");

    const int maxAttempts = 3;
    int delayMs = 250;

    for (int attempt = 1; attempt <= maxAttempts; attempt++)
    {
        try
        {
            using var resp = await http.GetAsync(url, ct);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync(ct);
                return JsonDocument.Parse(json);
            }

            if ((int)resp.StatusCode >= 500 || (int)resp.StatusCode == 429)
            {
                await Task.Delay(delayMs, ct);
                delayMs *= 2;
                continue;
            }

            Console.WriteLine($"ERROR: CMC request failed with status {(int)resp.StatusCode}");
            return null;
        }
        catch (Exception ex) when (attempt < maxAttempts)
        {
            await Task.Delay(delayMs, ct);
            delayMs *= 2;
            if (ex is OperationCanceledException) throw;
        }
    }

    return null;
}

static List<CmcDailyRow> ParsePointsToDaily(JsonElement root, int year)
{
    var rows = new Dictionary<DateOnly, CmcDailyRow>();

    if (!root.TryGetProperty("data", out var data)) return BuildEmptyDailyRows(year);
    if (!data.TryGetProperty("points", out var points) || points.ValueKind != JsonValueKind.Object)
        return BuildEmptyDailyRows(year);

    foreach (var prop in points.EnumerateObject())
    {
        if (!long.TryParse(prop.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ts))
            continue;

        var date = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime);
        if (date.Year != year) continue;

        if (!prop.Value.TryGetProperty("v", out var v) || v.ValueKind != JsonValueKind.Array)
            continue;

        decimal? price = GetDecimalAtIndex(v, 0);
        decimal? vol = GetDecimalAtIndex(v, 1);
        if (price is null && vol is null) continue;

        if (!rows.TryGetValue(date, out var existing) || ts > existing.LastUnixTs)
        {
            rows[date] = new CmcDailyRow
            {
                Date = date,
                PriceUsd = price,
                Vol24hUsd = vol,
                LastUnixTs = ts
            };
        }
    }

    var output = new List<CmcDailyRow>();
    var day = new DateOnly(year, 1, 1);
    var end = new DateOnly(year, 12, 31);
    while (day <= end)
    {
        if (rows.TryGetValue(day, out var row))
            output.Add(row);
        else
            output.Add(new CmcDailyRow { Date = day });
        day = day.AddDays(1);
    }

    return output;
}

static List<CmcDailyRow> BuildEmptyDailyRows(int year)
{
    var output = new List<CmcDailyRow>();
    var day = new DateOnly(year, 1, 1);
    var end = new DateOnly(year, 12, 31);
    while (day <= end)
    {
        output.Add(new CmcDailyRow { Date = day });
        day = day.AddDays(1);
    }

    return output;
}

static decimal? GetDecimalAtIndex(JsonElement arr, int index)
{
    if (arr.ValueKind != JsonValueKind.Array) return null;
    if (arr.GetArrayLength() <= index) return null;
    var el = arr[index];
    if (el.ValueKind == JsonValueKind.Null) return null;

    if (el.ValueKind == JsonValueKind.Number)
    {
        if (el.TryGetDecimal(out var d)) return d;
        if (el.TryGetDouble(out var dbl)) return (decimal)dbl;
    }

    return null;
}

static void WriteCmcDailyCsv(List<CmcDailyRow> rows, string outPath)
{
    var lines = new List<string>(rows.Count + 1)
    {
        "date,price_usd,vol24h_usd"
    };

    foreach (var row in rows)
    {
        var price = row.PriceUsd.HasValue
            ? row.PriceUsd.Value.ToString("0.########", CultureInfo.InvariantCulture)
            : "";
        var vol = row.Vol24hUsd.HasValue
            ? row.Vol24hUsd.Value.ToString("0.##", CultureInfo.InvariantCulture)
            : "";
        lines.Add($"{row.Date:yyyy-MM-dd},{price},{vol}");
    }

    File.WriteAllLines(outPath, lines);
}

static void PrintCmcDailySummary(List<CmcDailyRow> rows)
{
    var populated = rows.Where(r => r.PriceUsd.HasValue || r.Vol24hUsd.HasValue).ToList();
    var minPrice = populated.Where(r => r.PriceUsd.HasValue).Select(r => r.PriceUsd!.Value).DefaultIfEmpty(0m).Min();
    var maxPrice = populated.Where(r => r.PriceUsd.HasValue).Select(r => r.PriceUsd!.Value).DefaultIfEmpty(0m).Max();
    var minVol = populated.Where(r => r.Vol24hUsd.HasValue).Select(r => r.Vol24hUsd!.Value).DefaultIfEmpty(0m).Min();
    var maxVol = populated.Where(r => r.Vol24hUsd.HasValue).Select(r => r.Vol24hUsd!.Value).DefaultIfEmpty(0m).Max();

    Console.WriteLine();
    Console.WriteLine("== CMC Daily Summary ==");
    Console.WriteLine($"Rows written: {rows.Count}");
    Console.WriteLine($"First date:   {rows.First().Date:yyyy-MM-dd}");
    Console.WriteLine($"Last date:    {rows.Last().Date:yyyy-MM-dd}");
    Console.WriteLine($"Min price:    {minPrice.ToString("0.########", CultureInfo.InvariantCulture)}");
    Console.WriteLine($"Max price:    {maxPrice.ToString("0.########", CultureInfo.InvariantCulture)}");
    Console.WriteLine($"Min vol:      {minVol.ToString("0.##", CultureInfo.InvariantCulture)}");
    Console.WriteLine($"Max vol:      {maxVol.ToString("0.##", CultureInfo.InvariantCulture)}");
}

// ---------------- Balances Summary helpers (REST-derived) ----------------

static async Task<BalancesSummary> GetBalancesSummaryAsync(HttpClient http, string addr, string denomWantedUpper)
{
    var s = new BalancesSummary();

    s.BankBalanceUnits = await TryGetBankBalanceUnitsAsync(http, addr, denomWantedUpper);
    s.SpendableBalanceUnits = await TryGetSpendableBalanceUnitsAsync(http, addr, denomWantedUpper);

    s.DelegatedUnits = await TryGetDelegatedUnitsAsync(http, addr, denomWantedUpper);
    s.UnbondingUnits = await TryGetUnbondingUnitsAsync(http, addr, denomWantedUpper);
    s.RedelegatingUnits = await TryGetRedelegatingUnitsAsync(http, addr, denomWantedUpper);

    s.GovDepositUnits = await TryGetGovDepositsUnitsAsync(http, addr, denomWantedUpper);

    s.IsVestingAccount = await TryDetectVestingAccountAsync(http, addr);

    if (s.IsVestingAccount && s.BankBalanceUnits.HasValue && s.SpendableBalanceUnits.HasValue)
    {
        var locked = s.BankBalanceUnits.Value - s.SpendableBalanceUnits.Value;
        s.VestingLockedUnits = locked > 0m ? locked : 0m;
    }
    else
    {
        s.VestingLockedUnits = null;
    }

    s.LockedTotalUnits =
        (s.DelegatedUnits ?? 0m) +
        (s.UnbondingUnits ?? 0m) +
        (s.RedelegatingUnits ?? 0m) +
        (s.GovDepositUnits ?? 0m) +
        (s.VestingLockedUnits ?? 0m);

    return s;
}

static void PrintBalancesSummary(BalancesSummary s, string denomWantedUpper, decimal scale)
{
    static string F(decimal x) => x.ToString("0.000000", CultureInfo.InvariantCulture);
    static string ShowOpt(decimal? units, decimal scale) => units.HasValue ? F(units.Value / scale) : "(unavailable)";

    Console.WriteLine($"Bank balance:       {ShowOpt(s.BankBalanceUnits, scale)}");
    Console.WriteLine($"Spendable balance:  {ShowOpt(s.SpendableBalanceUnits, scale)}");
    Console.WriteLine($"Locked total:       {ShowOpt(s.LockedTotalUnits, scale)}");

    Console.WriteLine("Breakdown:");
    Console.WriteLine($"  Delegated:        {ShowOpt(s.DelegatedUnits, scale)}");
    Console.WriteLine($"  Unbonding:        {ShowOpt(s.UnbondingUnits, scale)}");
    Console.WriteLine($"  Redelegating:     {ShowOpt(s.RedelegatingUnits, scale)}");
    Console.WriteLine($"  Gov deposits:     {ShowOpt(s.GovDepositUnits, scale)}");
    Console.WriteLine($"  Vesting locked:   {(s.IsVestingAccount ? ShowOpt(s.VestingLockedUnits, scale) : "(not vesting)")}");
}

static async Task<JsonDocument?> TryGetJsonAsync(HttpClient http, string relativeUrl)
{
    try
    {
        using var resp = await http.GetAsync(relativeUrl);
        if (!resp.IsSuccessStatusCode) return null;
        var json = await resp.Content.ReadAsStringAsync();
        return JsonDocument.Parse(json);
    }
    catch
    {
        return null;
    }
}

static async Task<decimal?> TryGetBankBalanceUnitsAsync(HttpClient http, string addr, string denomWantedUpper)
{
    var doc = await TryGetJsonAsync(http, $"cosmos/bank/v1beta1/balances/{addr}?pagination.limit=2000");
    if (doc == null) return null;

    if (!doc.RootElement.TryGetProperty("balances", out var balances) || balances.ValueKind != JsonValueKind.Array)
        return 0m;

    decimal total = 0m;
    foreach (var coin in balances.EnumerateArray())
    {
        var denom = coin.TryGetProperty("denom", out var d) ? d.GetString() : null;
        var amt = coin.TryGetProperty("amount", out var a) ? a.GetString() : null;
        if (string.IsNullOrWhiteSpace(denom) || string.IsNullOrWhiteSpace(amt)) continue;

        if (!string.Equals(denom.ToUpperInvariant(), denomWantedUpper, StringComparison.Ordinal)) continue;
        if (decimal.TryParse(amt, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) total += n;
    }

    return total;
}

static async Task<decimal?> TryGetSpendableBalanceUnitsAsync(HttpClient http, string addr, string denomWantedUpper)
{
    var doc = await TryGetJsonAsync(http, $"cosmos/bank/v1beta1/spendable_balances/{addr}?pagination.limit=2000");
    if (doc == null) return null;

    JsonElement arr;
    if (doc.RootElement.TryGetProperty("balances", out arr) && arr.ValueKind == JsonValueKind.Array)
    {
        // ok
    }
    else if (doc.RootElement.TryGetProperty("spendable_balances", out arr) && arr.ValueKind == JsonValueKind.Array)
    {
        // ok
    }
    else
    {
        return null;
    }

    decimal total = 0m;
    foreach (var coin in arr.EnumerateArray())
    {
        var denom = coin.TryGetProperty("denom", out var d) ? d.GetString() : null;
        var amt = coin.TryGetProperty("amount", out var a) ? a.GetString() : null;
        if (string.IsNullOrWhiteSpace(denom) || string.IsNullOrWhiteSpace(amt)) continue;

        if (!string.Equals(denom.ToUpperInvariant(), denomWantedUpper, StringComparison.Ordinal)) continue;
        if (decimal.TryParse(amt, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) total += n;
    }

    return total;
}

static async Task<decimal?> TryGetDelegatedUnitsAsync(HttpClient http, string addr, string denomWantedUpper)
{
    var doc = await TryGetJsonAsync(http, $"cosmos/staking/v1beta1/delegations/{addr}?pagination.limit=2000");
    if (doc == null) return null;

    if (!doc.RootElement.TryGetProperty("delegation_responses", out var arr) || arr.ValueKind != JsonValueKind.Array)
        return 0m;

    decimal total = 0m;
    foreach (var dr in arr.EnumerateArray())
    {
        if (!dr.TryGetProperty("balance", out var bal)) continue;
        var denom = bal.TryGetProperty("denom", out var d) ? d.GetString() : null;
        var amt = bal.TryGetProperty("amount", out var a) ? a.GetString() : null;
        if (string.IsNullOrWhiteSpace(denom) || string.IsNullOrWhiteSpace(amt)) continue;

        if (!string.Equals(denom.ToUpperInvariant(), denomWantedUpper, StringComparison.Ordinal)) continue;
        if (decimal.TryParse(amt, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) total += n;
    }

    return total;
}

static async Task<decimal?> TryGetUnbondingUnitsAsync(HttpClient http, string addr, string denomWantedUpper)
{
    var doc = await TryGetJsonAsync(http, $"cosmos/staking/v1beta1/delegators/{addr}/unbonding_delegations?pagination.limit=2000");
    if (doc == null) return null;

    if (!doc.RootElement.TryGetProperty("unbonding_responses", out var arr) || arr.ValueKind != JsonValueKind.Array)
        return 0m;

    decimal total = 0m;
    foreach (var ur in arr.EnumerateArray())
    {
        if (!ur.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array) continue;
        foreach (var e in entries.EnumerateArray())
        {
            var balStr = e.TryGetProperty("balance", out var b) ? b.GetString() : null;
            if (string.IsNullOrWhiteSpace(balStr)) continue;
            if (decimal.TryParse(balStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) total += n;
        }
    }

    return total;
}

static async Task<decimal?> TryGetRedelegatingUnitsAsync(HttpClient http, string addr, string denomWantedUpper)
{
    var doc = await TryGetJsonAsync(http, $"cosmos/staking/v1beta1/delegators/{addr}/redelegations?pagination.limit=2000");
    if (doc == null) return null;

    if (!doc.RootElement.TryGetProperty("redelegation_responses", out var arr) || arr.ValueKind != JsonValueKind.Array)
        return 0m;

    decimal total = 0m;
    foreach (var rr in arr.EnumerateArray())
    {
        if (!rr.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array) continue;
        foreach (var e in entries.EnumerateArray())
        {
            var balStr = e.TryGetProperty("balance", out var b) ? b.GetString() : null;
            if (string.IsNullOrWhiteSpace(balStr)) continue;
            if (decimal.TryParse(balStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) total += n;
        }
    }

    return total;
}

static async Task<List<string>> TryGetProposalIdsAsync(HttpClient http, string govBase, string proposalStatus)
{
    var doc = await TryGetJsonAsync(http, $"{govBase}/proposals?proposal_status={Uri.EscapeDataString(proposalStatus)}&pagination.limit=2000");
    if (doc == null) return new List<string>();

    if (!doc.RootElement.TryGetProperty("proposals", out var arr) || arr.ValueKind != JsonValueKind.Array)
        return new List<string>();

    var ids = new List<string>();
    foreach (var p in arr.EnumerateArray())
    {
        if (p.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
            ids.Add(idEl.GetString() ?? "");
        else if (p.TryGetProperty("proposal_id", out var pidEl) && pidEl.ValueKind == JsonValueKind.String)
            ids.Add(pidEl.GetString() ?? "");
        else if (p.TryGetProperty("proposal_id", out var pidNum) && pidNum.ValueKind == JsonValueKind.Number)
            ids.Add(pidNum.GetRawText());
        else if (p.TryGetProperty("id", out var idNum) && idNum.ValueKind == JsonValueKind.Number)
            ids.Add(idNum.GetRawText());
    }

    return ids.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
}

static async Task<decimal?> TryGetGovDepositsUnitsAsync(HttpClient http, string depositorAddr, string denomWantedUpper)
{
    var proposalIds = new List<string>();

    proposalIds.AddRange(await TryGetProposalIdsAsync(http, "cosmos/gov/v1", "PROPOSAL_STATUS_DEPOSIT_PERIOD"));
    proposalIds.AddRange(await TryGetProposalIdsAsync(http, "cosmos/gov/v1", "PROPOSAL_STATUS_VOTING_PERIOD"));

    string govBase = "cosmos/gov/v1";
    bool usedV1 = proposalIds.Count > 0;

    if (!usedV1)
    {
        proposalIds.AddRange(await TryGetProposalIdsAsync(http, "cosmos/gov/v1beta1", "PROPOSAL_STATUS_DEPOSIT_PERIOD"));
        proposalIds.AddRange(await TryGetProposalIdsAsync(http, "cosmos/gov/v1beta1", "PROPOSAL_STATUS_VOTING_PERIOD"));
        govBase = "cosmos/gov/v1beta1";
    }

    if (proposalIds.Count == 0) return 0m;

    decimal total = 0m;

    foreach (var id in proposalIds.Distinct(StringComparer.Ordinal))
    {
        var doc = await TryGetJsonAsync(http, $"{govBase}/proposals/{id}/deposits/{depositorAddr}");
        if (doc == null) continue;

        if (!doc.RootElement.TryGetProperty("deposit", out var dep)) continue;
        if (!dep.TryGetProperty("amount", out var amtArr) || amtArr.ValueKind != JsonValueKind.Array) continue;

        foreach (var coin in amtArr.EnumerateArray())
        {
            var denom = coin.TryGetProperty("denom", out var d) ? d.GetString() : null;
            var amt = coin.TryGetProperty("amount", out var a) ? a.GetString() : null;
            if (string.IsNullOrWhiteSpace(denom) || string.IsNullOrWhiteSpace(amt)) continue;

            if (!string.Equals(denom.ToUpperInvariant(), denomWantedUpper, StringComparison.Ordinal)) continue;
            if (decimal.TryParse(amt, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) total += n;
        }
    }

    return total;
}

static async Task<bool> TryDetectVestingAccountAsync(HttpClient http, string addr)
{
    var doc = await TryGetJsonAsync(http, $"cosmos/auth/v1beta1/accounts/{addr}");
    if (doc == null) return false;

    if (!doc.RootElement.TryGetProperty("account", out var acc)) return false;

    var type = acc.TryGetProperty("@type", out var t) ? t.GetString() : null;
    if (string.IsNullOrWhiteSpace(type)) return false;

    return type.Contains("vesting", StringComparison.OrdinalIgnoreCase);
}

// ---------------- gRPC paging (PageRequest.Key) ----------------

static async Task AddTxResponsesForQuery_PageKey(
    Service.ServiceClient txClient,
    string query,
    Dictionary<string, TxResponse> txByHash,
    DateTimeOffset cutoffUtc,
    int pageLimit,
    int maxPages,
    string label,
    bool verbose)
{
    if (verbose)
    {
        Console.WriteLine();
        Console.WriteLine($"Query ({label}): {query}");
    }

    byte[]? key = null;
    ulong offset = 0;
    bool useOffset = false;
    bool any = false;
    string? prevFirstHash = null;
    string? prevLastHash = null;

    for (int page = 1; page <= maxPages; page++)
    {
        var req = new GetTxsEventRequest { Query = query };

#pragma warning disable CS0612
        var pagination = new PageRequest
        {
            Limit = (ulong)pageLimit,
            CountTotal = false
        };

        if (useOffset)
        {
            pagination.Offset = offset;
        }
        else if (key is { Length: > 0 })
        {
            pagination.Key = Google.Protobuf.ByteString.CopyFrom(key);
        }

        req.Pagination = pagination;
#pragma warning restore CS0612

        GetTxsEventResponse resp;
        try
        {
            resp = await txClient.GetTxsEventAsync(req);
        }
        catch (Grpc.Core.RpcException ex)
        {
            if (verbose)
                Console.WriteLine($"  ERROR ({ex.StatusCode}): {ex.Status.Detail}");
            break;
        }

        var count = resp.TxResponses.Count;
        if (count == 0)
        {
            if (verbose)
                Console.WriteLine(any ? $"  page={page} txs=0 (done)" : "  page=1 txs=0 (no matches)");
            break;
        }

        any = true;

        int added = 0;
        string? firstHash = resp.TxResponses.Count > 0 ? resp.TxResponses[0].Txhash : null;
        string? lastHash = resp.TxResponses.Count > 0 ? resp.TxResponses[^1].Txhash : null;
        foreach (var tr in resp.TxResponses)
        {
            if (DateTimeOffset.TryParse(tr.Timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var ts) &&
                ts.ToUniversalTime() < cutoffUtc)
            {
                continue;
            }

            var h = tr.Txhash ?? "";
            if (h.Length == 0) continue;

            if (!txByHash.ContainsKey(h))
            {
                txByHash[h] = tr;
                added++;
            }
        }

        DateTimeOffset? lastTs = null;
        var lastResp = resp.TxResponses[^1];
        if (DateTimeOffset.TryParse(lastResp.Timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var lastParsed))
            lastTs = lastParsed.ToUniversalTime();
        var pastCutoff = lastTs.HasValue && lastTs.Value < cutoffUtc;

        if (useOffset)
        {
            if (verbose)
                Console.WriteLine($"  page={page} offset={offset} txs={count} new_txs={added} total_txs={txByHash.Count}");
            if (!string.IsNullOrWhiteSpace(firstHash) &&
                !string.IsNullOrWhiteSpace(lastHash) &&
                string.Equals(firstHash, prevFirstHash, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(lastHash, prevLastHash, StringComparison.OrdinalIgnoreCase))
            {
                if (verbose)
                    Console.WriteLine("  page results repeated; stopping offset paging.");
                break;
            }
            if (pastCutoff)
                break;
            if (count < pageLimit)
                break;

            prevFirstHash = firstHash;
            prevLastHash = lastHash;
            offset += (ulong)pageLimit;
            continue;
        }

#pragma warning disable CS0612
        var nextKey = resp.Pagination?.NextKey;
#pragma warning restore CS0612

        var nextLen = nextKey?.Length ?? 0;
        if (verbose)
            Console.WriteLine($"  page={page} txs={count} new_txs={added} total_txs={txByHash.Count} nextKeyLen={nextLen}");

        if (pastCutoff)
            break;

        if (nextKey != null && nextKey.Length > 0)
        {
            key = nextKey.ToByteArray();
            continue;
        }

        if (count == pageLimit)
        {
            // Server did not return a next key; fall back to offset paging.
            useOffset = true;
            offset = (ulong)pageLimit;
            continue;
        }

        break;
    }
}

// ---------------- Memo: real via GetTx + fallback label ----------------

static async Task<string?> TryGetRealMemoAsync(Service.ServiceClient txClient, string txHash)
{
    try
    {
        var resp = await txClient.GetTxAsync(new GetTxRequest { Hash = txHash });
        var memo = resp?.Tx?.Body?.Memo;
        if (string.IsNullOrWhiteSpace(memo)) return null;
        return memo;
    }
    catch
    {
        return null;
    }
}

// ---------------- Memo: extract from CometBFT RPC /tx (base64 tx bytes) ----------------
// This works directly from the JSON returned by /tx (and the same base64 format used in /tx_search result.txs[].tx).
// No LCD is used.

static async Task<string?> TryGetRealMemoFromCometRpcAsync(HttpClient rpcHttp, string txHash)
{
    // CometBFT expects 0x-prefixed hex hash on /tx endpoint.
    var hex = txHash.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? txHash : "0x" + txHash;

    // GET /tx?hash=0x...
    JsonDocument? doc = null;
    try
    {
        using var resp = await rpcHttp.GetAsync($"tx?hash={Uri.EscapeDataString(hex)}");
        if (!resp.IsSuccessStatusCode) return null;
        var json = await resp.Content.ReadAsStringAsync();
        doc = JsonDocument.Parse(json);
    }
    catch
    {
        return null;
    }

    using (doc)
    {
        // result.tx is base64 bytes
        if (!doc.RootElement.TryGetProperty("result", out var result)) return null;
        if (!result.TryGetProperty("tx", out var txEl)) return null;
        var base64Tx = txEl.GetString();
        if (string.IsNullOrWhiteSpace(base64Tx)) return null;

        try
        {
            return ExtractMemoFromBase64Tx(base64Tx);
        }
        catch
        {
            return null;
        }
    }
}

// ---------------- CometBFT RPC /tx helpers (events) ----------------
// Used as a fallback when gRPC TxResponse.Events is missing or incomplete.

static async Task<CometTx?> TryGetCometTxAsync(HttpClient rpcHttp, string txHash)
{
    var hex = txHash.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? txHash : "0x" + txHash;

    try
    {
        using var resp = await rpcHttp.GetAsync($"tx?hash={Uri.EscapeDataString(hex)}&prove=false");
        if (!resp.IsSuccessStatusCode) return null;

        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("result", out var result)) return null;

        var base64Tx = result.TryGetProperty("tx", out var txEl) ? txEl.GetString() : null;
        var heightStr = result.TryGetProperty("height", out var h) ? h.GetString() : null;
        var height = long.TryParse(heightStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hh) ? hh : 0L;

        if (!result.TryGetProperty("tx_result", out var txr)) return null;
        if (!txr.TryGetProperty("events", out var evArr) || evArr.ValueKind != JsonValueKind.Array) return null;

        var events = new List<CometEvent>(capacity: evArr.GetArrayLength());
        foreach (var ev in evArr.EnumerateArray())
        {
            var type = ev.TryGetProperty("type", out var t) ? (t.GetString() ?? "") : "";
            if (string.IsNullOrWhiteSpace(type)) continue;

            var attrs = new List<CometAttr>();
            if (ev.TryGetProperty("attributes", out var at) && at.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in at.EnumerateArray())
                {
                    var k = a.TryGetProperty("key", out var kk) ? (kk.GetString() ?? "") : "";
                    var v = a.TryGetProperty("value", out var vv) ? (vv.GetString() ?? "") : "";
                    if (k.Length == 0) continue;
                    attrs.Add(new CometAttr(k, v));
                }
            }

            events.Add(new CometEvent(type, attrs));
        }

        return new CometTx(base64Tx, events, height);
    }
    catch
    {
        return null;
    }
}

static async Task<CometTx?> TryGetCometTxWithRetryAsync(
    HttpClient rpcHttp,
    string txHash,
    int maxAttempts,
    CancellationToken ct)
{
    var hex = txHash.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? txHash : "0x" + txHash;

    for (int attempt = 1; attempt <= maxAttempts; attempt++)
    {
        try
        {
            using var resp = await SendWithRetryAsync(
                () => rpcHttp.GetAsync($"tx?hash={Uri.EscapeDataString(hex)}&prove=false", ct),
                ct);
            if (resp is null || !resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("result", out var result)) return null;

            var base64Tx = result.TryGetProperty("tx", out var txEl) ? txEl.GetString() : null;
            var heightStr = result.TryGetProperty("height", out var h) ? h.GetString() : null;
            var height = long.TryParse(heightStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hh) ? hh : 0L;

            if (!result.TryGetProperty("tx_result", out var txr)) return null;
            if (!txr.TryGetProperty("events", out var evArr) || evArr.ValueKind != JsonValueKind.Array) return null;

            var events = new List<CometEvent>(capacity: evArr.GetArrayLength());
            foreach (var ev in evArr.EnumerateArray())
            {
                var type = ev.TryGetProperty("type", out var t) ? (t.GetString() ?? "") : "";
                if (string.IsNullOrWhiteSpace(type)) continue;

                var attrs = new List<CometAttr>();
                if (ev.TryGetProperty("attributes", out var at) && at.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in at.EnumerateArray())
                    {
                        var k = a.TryGetProperty("key", out var kk) ? (kk.GetString() ?? "") : "";
                        var v = a.TryGetProperty("value", out var vv) ? (vv.GetString() ?? "") : "";
                        if (k.Length == 0) continue;
                        attrs.Add(new CometAttr(k, v));
                    }
                }

                events.Add(new CometEvent(type, attrs));
            }

            return new CometTx(base64Tx, events, height);
        }
        catch
        {
            if (attempt == maxAttempts) return null;
        }
    }

    return null;
}

static async Task<HttpResponseMessage?> SendWithRetryAsync(
    Func<Task<HttpResponseMessage>> send,
    CancellationToken ct)
{
    var delays = new[] { 250, 750, 1500 };

    for (int attempt = 0; attempt < delays.Length + 1; attempt++)
    {
        try
        {
            var resp = await send();
            if (resp.IsSuccessStatusCode) return resp;

            var code = (int)resp.StatusCode;
            if (code != 429 && code < 500) return resp;

            resp.Dispose();
        }
        catch
        {
            if (attempt >= delays.Length) return null;
        }

        if (attempt < delays.Length)
            await Task.Delay(delays[attempt], ct);
    }

    return null;
}

static async Task<DateTime?> TryGetBlockTimeFromCometRpcAsync(HttpClient rpcHttp, long height)
{
    if (height <= 0) return null;

    try
    {
        using var resp = await rpcHttp.GetAsync($"block?height={height}");
        if (!resp.IsSuccessStatusCode) return null;

        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("result", out var result)) return null;
        if (!result.TryGetProperty("block", out var block)) return null;
        if (!block.TryGetProperty("header", out var header)) return null;
        if (!header.TryGetProperty("time", out var timeEl)) return null;

        var timeStr = timeEl.GetString();
        if (string.IsNullOrWhiteSpace(timeStr)) return null;

        if (DateTimeOffset.TryParse(timeStr, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
            return dto.ToUniversalTime().UtcDateTime;

        return null;
    }
    catch
    {
        return null;
    }
}

static async Task<DateTime?> GetBlockTimeCachedAsync(
    HttpClient rpcHttp,
    long height,
    ConcurrentDictionary<long, DateTime> cache,
    CancellationToken ct)
{
    if (cache.TryGetValue(height, out var cached))
        return cached;

    var fetched = await TryGetBlockTimeFromCometRpcAsync(rpcHttp, height);
    if (fetched.HasValue)
        cache.TryAdd(height, fetched.Value);

    return fetched;
}

static async Task<CometStatus?> TryGetCometStatusAsync(HttpClient rpcHttp)
{
    try
    {
        using var resp = await rpcHttp.GetAsync("status");
        if (!resp.IsSuccessStatusCode) return null;

        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("result", out var result)) return null;

        var status = new CometStatus();
        if (result.TryGetProperty("sync_info", out var sync))
        {
            if (sync.TryGetProperty("catching_up", out var catchingUpEl) &&
                (catchingUpEl.ValueKind == JsonValueKind.True || catchingUpEl.ValueKind == JsonValueKind.False))
            {
                status.CatchingUp = catchingUpEl.GetBoolean();
            }

            if (sync.TryGetProperty("latest_block_height", out var heightEl))
            {
                var heightStr = heightEl.GetString();
                if (long.TryParse(heightStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var height))
                    status.LatestBlockHeight = height;
            }

            if (sync.TryGetProperty("latest_block_time", out var timeEl))
            {
                var timeStr = timeEl.GetString();
                if (DateTimeOffset.TryParse(timeStr, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var ts))
                    status.LatestBlockTime = ts.ToUniversalTime();
            }
        }

        if (result.TryGetProperty("node_info", out var nodeInfo))
        {
            if (nodeInfo.TryGetProperty("moniker", out var monikerEl))
                status.Moniker = monikerEl.GetString();
            if (nodeInfo.TryGetProperty("network", out var networkEl))
                status.Network = networkEl.GetString();
        }

        return status;
    }
    catch
    {
        return null;
    }
}

static async Task<List<CometPeerInfo>> TryGetCometNetInfoPeersAsync(HttpClient rpcHttp)
{
    var peers = new List<CometPeerInfo>();
    try
    {
        using var resp = await rpcHttp.GetAsync("net_info");
        if (!resp.IsSuccessStatusCode) return peers;

        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("result", out var result)) return peers;
        if (!result.TryGetProperty("peers", out var peersEl) || peersEl.ValueKind != JsonValueKind.Array)
            return peers;

        foreach (var p in peersEl.EnumerateArray())
        {
            var info = new CometPeerInfo();

            if (p.TryGetProperty("node_info", out var nodeInfo))
            {
                if (nodeInfo.TryGetProperty("moniker", out var monikerEl))
                    info.Moniker = monikerEl.GetString() ?? "";
                if (nodeInfo.TryGetProperty("id", out var idEl))
                    info.NodeId = idEl.GetString() ?? "";
                if (nodeInfo.TryGetProperty("listen_addr", out var listenEl))
                    info.ListenAddr = listenEl.GetString() ?? "";
            }

            if (p.TryGetProperty("remote_ip", out var remoteIpEl))
                info.RemoteIp = remoteIpEl.GetString() ?? "";

            peers.Add(info);
        }
    }
    catch
    {
        return peers;
    }

    return peers;
}

static async Task<long?> TryGetLatestHeightFromCometRpcAsync(HttpClient rpcHttp)
{
    try
    {
        using var resp = await rpcHttp.GetAsync("status");
        if (!resp.IsSuccessStatusCode) return null;

        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("result", out var result)) return null;
        if (!result.TryGetProperty("sync_info", out var sync)) return null;
        if (!sync.TryGetProperty("latest_block_height", out var heightEl)) return null;

        var heightStr = heightEl.GetString();
        if (long.TryParse(heightStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var height))
            return height;
        return null;
    }
    catch
    {
        return null;
    }
}

static async Task<List<string>> TryGetCometTxSearchHashesAsync(
    HttpClient rpcHttp,
    string query,
    int pageLimit,
    int maxPages)
{
    var hashes = new List<string>();
    var perPage = Math.Clamp(pageLimit, 1, 100);
    var pages = Math.Clamp(maxPages, 1, 1000);

    for (int page = 1; page <= pages; page++)
    {
        JsonDocument? doc = null;
        try
        {
            var payload = new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "tx_search",
                @params = new
                {
                    query,
                    page = page.ToString(CultureInfo.InvariantCulture),
                    per_page = perPage.ToString(CultureInfo.InvariantCulture),
                    order_by = "desc"
                }
            };

            var jsonPayload = JsonSerializer.Serialize(payload);
            using var content = new StringContent(jsonPayload, System.Text.Encoding.UTF8, "application/json");
            using var resp = await rpcHttp.PostAsync("", content);
            if (!resp.IsSuccessStatusCode) break;

            var json = await resp.Content.ReadAsStringAsync();
            doc = JsonDocument.Parse(json);
        }
        catch
        {
            return hashes;
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("result", out var result)) break;
            if (!result.TryGetProperty("txs", out var txs) || txs.ValueKind != JsonValueKind.Array) break;

            var count = 0;
            foreach (var tx in txs.EnumerateArray())
            {
                var hash = tx.TryGetProperty("hash", out var h) ? h.GetString() : null;
                if (!string.IsNullOrWhiteSpace(hash))
                {
                    hashes.Add(hash);
                    count++;
                }
            }

            if (count == 0) break;
        }
    }

    return hashes;
}

static async Task<List<CometTxSearchHit>> TryGetCometTxSearchHitsWithFallbackAsync(
    HttpClient rpcHttp,
    string query,
    int pageLimit,
    int maxPages,
    long? minHeightInclusive,
    CancellationToken ct)
{
    var hits = await TryGetCometTxSearchHitsViaGetAsync(rpcHttp, query, pageLimit, maxPages, minHeightInclusive, ct);
    if (hits.Count > 0) return hits;

    return await TryGetCometTxSearchHitsViaPostAsync(rpcHttp, query, pageLimit, maxPages, minHeightInclusive, ct);
}

static async Task<List<CometTxSearchHit>> TryGetCometTxSearchHitsViaGetAsync(
    HttpClient rpcHttp,
    string query,
    int pageLimit,
    int maxPages,
    long? minHeightInclusive,
    CancellationToken ct)
{
    var hits = new List<CometTxSearchHit>();
    var perPage = Math.Clamp(pageLimit, 1, 100);
    var pages = Math.Clamp(maxPages, 1, 1000);

    for (int page = 1; page <= pages; page++)
    {
        var url =
            $"tx_search?query={Uri.EscapeDataString(query)}&page={page}&per_page={perPage}&order_by=desc";

        try
        {
            using var resp = await SendWithRetryAsync(() => rpcHttp.GetAsync(url, ct), ct);
            if (resp is null || !resp.IsSuccessStatusCode) return hits;

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("result", out var result)) break;
            if (!result.TryGetProperty("txs", out var txs) || txs.ValueKind != JsonValueKind.Array) break;

            var count = 0;
            var stop = false;
            foreach (var tx in txs.EnumerateArray())
            {
                var hash = tx.TryGetProperty("hash", out var h) ? h.GetString() : null;
                var heightStr = tx.TryGetProperty("height", out var he) ? he.GetString() : null;
                if (!long.TryParse(heightStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var height))
                    continue;

                if (minHeightInclusive.HasValue && height < minHeightInclusive.Value)
                {
                    stop = true;
                    break;
                }

                if (!string.IsNullOrWhiteSpace(hash))
                {
                    hits.Add(new CometTxSearchHit(hash, height));
                    count++;
                }
            }

            if (count == 0) break;
            if (stop) break;
        }
        catch
        {
            return hits;
        }
    }

    return hits;
}

static async Task<List<CometTxSearchHit>> TryGetCometTxSearchHitsViaPostAsync(
    HttpClient rpcHttp,
    string query,
    int pageLimit,
    int maxPages,
    long? minHeightInclusive,
    CancellationToken ct)
{
    var hits = new List<CometTxSearchHit>();
    var perPage = Math.Clamp(pageLimit, 1, 100);
    var pages = Math.Clamp(maxPages, 1, 1000);

    for (int page = 1; page <= pages; page++)
    {
        JsonDocument? doc = null;
        try
        {
            var payload = new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "tx_search",
                @params = new
                {
                    query,
                    page = page.ToString(CultureInfo.InvariantCulture),
                    per_page = perPage.ToString(CultureInfo.InvariantCulture),
                    order_by = "desc",
                    prove = false
                }
            };

            var jsonPayload = JsonSerializer.Serialize(payload);
            using var content = new StringContent(jsonPayload, System.Text.Encoding.UTF8, "application/json");
            using var resp = await SendWithRetryAsync(() => rpcHttp.PostAsync("", content, ct), ct);
            if (resp is null || !resp.IsSuccessStatusCode) break;

            var json = await resp.Content.ReadAsStringAsync(ct);
            doc = JsonDocument.Parse(json);
        }
        catch
        {
            return hits;
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("result", out var result)) break;
            if (!result.TryGetProperty("txs", out var txs) || txs.ValueKind != JsonValueKind.Array) break;

            var count = 0;
            var stop = false;
            foreach (var tx in txs.EnumerateArray())
            {
                var hash = tx.TryGetProperty("hash", out var h) ? h.GetString() : null;
                var heightStr = tx.TryGetProperty("height", out var he) ? he.GetString() : null;
                if (!long.TryParse(heightStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var height))
                    continue;

                if (minHeightInclusive.HasValue && height < minHeightInclusive.Value)
                {
                    stop = true;
                    break;
                }

                if (!string.IsNullOrWhiteSpace(hash))
                {
                    hits.Add(new CometTxSearchHit(hash, height));
                    count++;
                }
            }

            if (count == 0) break;
            if (stop) break;
        }
    }

    return hits;
}

static async Task ScanMultiSendFromBlocksAsync(
    HttpClient rpcHttp,
    string denomWantedUpper,
    long startHeight,
    long endHeight,
    int scale,
    int topSenders,
    List<string> allowedSenders)
{
    var totalsBySender = new Dictionary<string, BigInteger>(StringComparer.OrdinalIgnoreCase);
    BigInteger totalUopt = BigInteger.Zero;
    long txCount = 0;
    long blocksWithMultiSend = 0;
    long scannedBlocks = 0;
    object totalsLock = new object();
    var allowed = new HashSet<string>(allowedSenders, StringComparer.OrdinalIgnoreCase);

    var heights = Enumerable.Range(0, (int)(endHeight - startHeight + 1))
        .Select(i => startHeight + i)
        .ToList();

    await Parallel.ForEachAsync(
        heights,
        new ParallelOptions { MaxDegreeOfParallelism = 15 },
        async (height, ct) =>
        {
            var txEvents = await TryGetBlockResultsTxEventsAsync(rpcHttp, height);
            if (txEvents is null)
            {
                Interlocked.Increment(ref scannedBlocks);
                return;
            }

            bool blockHadMultiSend = false;
            BigInteger localTotal = BigInteger.Zero;
            long localTxCount = 0;
            var localBySender = new Dictionary<string, BigInteger>(StringComparer.OrdinalIgnoreCase);

            foreach (var evs in txEvents)
            {
                var (txTotal, perSender) = SumDistributionTransfersFromEvents(evs, denomWantedUpper, allowed);
                if (txTotal <= BigInteger.Zero) continue;

                blockHadMultiSend = true;
                localTxCount++;
                localTotal += txTotal;

                foreach (var kv in perSender)
                {
                    if (localBySender.TryGetValue(kv.Key, out var existing))
                        localBySender[kv.Key] = existing + kv.Value;
                    else
                        localBySender[kv.Key] = kv.Value;
                }
            }

            lock (totalsLock)
            {
                if (blockHadMultiSend) blocksWithMultiSend++;
                txCount += localTxCount;
                totalUopt += localTotal;
                foreach (var kv in localBySender)
                {
                    if (totalsBySender.TryGetValue(kv.Key, out var existing))
                        totalsBySender[kv.Key] = existing + kv.Value;
                    else
                        totalsBySender[kv.Key] = kv.Value;
                }
            }

            var done = Interlocked.Increment(ref scannedBlocks);
            if (done % 100 == 0)
        Console.WriteLine($"Scanned {done} blocks (blocks with MsgMultiSend: {blocksWithMultiSend})");
        });

    Console.WriteLine();
    Console.WriteLine("== Distribution Totals (MsgSend + MsgMultiSend) ==");
    Console.WriteLine($"Blocks with MsgMultiSend: {blocksWithMultiSend}");
    Console.WriteLine($"Transactions with MsgMultiSend: {txCount}");
    Console.WriteLine($"Total Distributed: {FormatOptFromUopt(totalUopt, scale)} OPT ({totalUopt} uOPT)");

    if (totalsBySender.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine("Top senders:");
        Console.WriteLine($"{Left("Address", 52)} {Right("Total(OPT)", 20)}");
        Console.WriteLine(new string('-', 52 + 1 + 20));

        foreach (var kv in totalsBySender
            .OrderByDescending(k => k.Value)
            .Take(Math.Max(1, topSenders)))
        {
            var amount = FormatOptFromUopt(kv.Value, scale);
            Console.WriteLine($"{Left(TruncateMiddle(kv.Key, 52), 52)} {Right(amount, 20)}");
        }
    }
}

static async Task<long?> FindHeightAtOrAfterTimeAsync(
    HttpClient rpcHttp,
    long low,
    long high,
    DateTime targetUtc,
    ConcurrentDictionary<long, DateTime> cache,
    CancellationToken ct)
{
    long? result = null;
    while (low <= high)
    {
        ct.ThrowIfCancellationRequested();
        var mid = low + (high - low) / 2;
        var time = await GetBlockTimeCachedAsync(rpcHttp, mid, cache, ct);
        if (!time.HasValue) return null;

        if (time.Value >= targetUtc)
        {
            result = mid;
            high = mid - 1;
        }
        else
        {
            low = mid + 1;
        }
    }

    return result;
}

static async Task<long?> FindHeightBeforeTimeAsync(
    HttpClient rpcHttp,
    long low,
    long high,
    DateTime targetUtc,
    ConcurrentDictionary<long, DateTime> cache,
    CancellationToken ct)
{
    long? result = null;
    while (low <= high)
    {
        ct.ThrowIfCancellationRequested();
        var mid = low + (high - low) / 2;
        var time = await GetBlockTimeCachedAsync(rpcHttp, mid, cache, ct);
        if (!time.HasValue) return null;

        if (time.Value < targetUtc)
        {
            result = mid;
            low = mid + 1;
        }
        else
        {
            high = mid - 1;
        }
    }

    return result;
}

static async Task<int> RunMultiSendSumAsync(
    HttpClient rpcHttp,
    string denomWantedUpper,
    int scaleInt,
    string fromAddr,
    long? lookbackBlocks,
    long? startHeightArg,
    long? endHeightArg,
    string? outPath,
    int pageLimit,
    int maxPages,
    CancellationToken ct)
{
    _ = lookbackBlocks;
    _ = startHeightArg;

    var latest = await TryGetLatestHeightFromCometRpcAsync(rpcHttp);
    var endHeight = endHeightArg ?? latest ?? 0;
    if (endHeight <= 0)
    {
        Console.WriteLine("date_local,tx_hash,height,from_address,to_address,amount_opt");
        return 1;
    }

    var localTz = TimeZoneInfo.Local;
    var nowLocal = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, localTz);
    var todayStartLocal = new DateTime(nowLocal.Year, nowLocal.Month, nowLocal.Day, 0, 0, 0, DateTimeKind.Unspecified);
    var prevStartLocal = todayStartLocal.AddDays(-1);
    var prevEndLocal = todayStartLocal;
    var prevStartUtc = TimeZoneInfo.ConvertTimeToUtc(prevStartLocal, localTz);
    var prevEndUtc = TimeZoneInfo.ConvertTimeToUtc(prevEndLocal, localTz);

    var blockTimeCache = new ConcurrentDictionary<long, DateTime>();
    var startHeight = await FindHeightAtOrAfterTimeAsync(
        rpcHttp, 1, endHeight, prevStartUtc, blockTimeCache, ct);
    var endHeightForDay = await FindHeightBeforeTimeAsync(
        rpcHttp, 1, endHeight, prevEndUtc, blockTimeCache, ct);

    if (!startHeight.HasValue || !endHeightForDay.HasValue || startHeight.Value > endHeightForDay.Value)
    {
        Console.WriteLine("date_local,tx_hash,height,from_address,to_address,amount_opt");
        return 0;
    }

    var baseQuery =
        $"message.action='/cosmos.bank.v1beta1.MsgMultiSend' AND message.sender='{fromAddr}'";
    var heightQuery =
        $"{baseQuery} AND tx.height>={startHeight.Value} AND tx.height<={endHeightForDay.Value}";

    bool filterAfter = false;
    var hits = await TryGetCometTxSearchHitsWithFallbackAsync(
        rpcHttp,
        heightQuery,
        pageLimit,
        maxPages,
        null,
        ct);

    if (hits.Count == 0)
    {
        hits = await TryGetCometTxSearchHitsWithFallbackAsync(
            rpcHttp,
            baseQuery,
            pageLimit,
            maxPages,
            startHeight.Value,
            ct);
        filterAfter = true;
    }

    if (hits.Count == 0)
    {
        Console.WriteLine("date_local,tx_hash,height,from_address,to_address,amount_opt");
        return 0;
    }

    var rows = new List<MultiSendRow>();
    var lockObj = new object();

    using var semaphore = new SemaphoreSlim(8);
    var tasks = new List<Task>();

    foreach (var hit in hits)
    {
        if (filterAfter && hit.Height < startHeight.Value)
            break;

        await semaphore.WaitAsync(ct);
        tasks.Add(Task.Run(async () =>
        {
            try
            {
                if (filterAfter && hit.Height < startHeight.Value)
                    return;
                if (filterAfter && hit.Height > endHeightForDay.Value)
                    return;

                var tx = await TryGetCometTxWithRetryAsync(rpcHttp, hit.Hash, 3, ct);
                if (tx is null) return;

                var height = tx.Height;
                if (height < startHeight.Value || height > endHeightForDay.Value) return;
                if (!HasMultiSendActionFromCometTx(tx)) return;

                var timeUtc = await GetBlockTimeCachedAsync(rpcHttp, height, blockTimeCache, ct);

                var (localRows, matchedFromAddr) =
                    ExtractMultiSendRowsFromCometTx(tx, hit.Hash, fromAddr, denomWantedUpper, scaleInt, timeUtc);
                if (!matchedFromAddr) return;

                lock (lockObj)
                {
                    foreach (var row in localRows)
                        rows.Add(row);
                }
            }
            finally
            {
                semaphore.Release();
            }
        }, ct));
    }

    await Task.WhenAll(tasks);

    rows.Sort((a, b) =>
    {
        var c = b.Height.CompareTo(a.Height);
        if (c != 0) return c;
        return string.Compare(a.TxHash, b.TxHash, StringComparison.OrdinalIgnoreCase);
    });

    var filteredRows = rows
        .Where(r => r.TimeUtc.HasValue)
        .Where(r =>
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(r.TimeUtc!.Value, DateTimeKind.Utc), localTz);
            return local >= prevStartLocal && local < prevEndLocal;
        })
        .ToList();

    Console.WriteLine("date_local,tx_hash,height,from_address,to_address,amount_opt");
    foreach (var row in filteredRows)
    {
        var local = row.TimeUtc.HasValue
            ? TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(row.TimeUtc.Value, DateTimeKind.Utc), localTz)
            : (DateTime?)null;
        var dateStr = local.HasValue
            ? local.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : "";

        var line = string.Join(",",
            CsvEscape(dateStr),
            CsvEscape(row.TxHash),
            CsvEscape(row.Height.ToString(CultureInfo.InvariantCulture)),
            CsvEscape(row.FromAddress),
            CsvEscape(row.ToAddress),
            CsvEscape(row.AmountOpt.ToString("0.000000", CultureInfo.InvariantCulture)));
        Console.WriteLine(line);
    }

    if (!string.IsNullOrWhiteSpace(outPath))
    {
        WriteMultiSendCsv(outPath, filteredRows);
    }

    return 0;
}

static (List<MultiSendRow> Rows, bool MatchedFromAddress) ExtractMultiSendRowsFromCometTx(
    CometTx tx,
    string txHash,
    string fromAddr,
    string denomWantedUpper,
    int scaleInt,
    DateTime? timeUtc)
{
    if (!HasMultiSendActionFromCometTx(tx))
        return (new List<MultiSendRow>(), false);

    var (rowsFromBytes, matchedFromAddr, parsed) =
        TryExtractMultiSendRowsFromTxBytes(tx.Base64Tx, tx.Height, txHash, fromAddr, denomWantedUpper, scaleInt, timeUtc);
    if (matchedFromAddr || parsed)
        return (rowsFromBytes, matchedFromAddr);

    var rows = new List<MultiSendRow>();
    var entries = ExtractTransferEntriesFromCometTx(tx, denomWantedUpper)
        .Where(e => e.AmountUnits > BigInteger.Zero &&
                    e.Sender.Equals(fromAddr, StringComparison.OrdinalIgnoreCase))
        .ToList();

    if (entries.Count == 0) return (rows, false);

    var (feeUnits, feePayer) = GetFeeFromCometTxUopt(tx, denomWantedUpper);
    if (!string.IsNullOrWhiteSpace(feePayer) &&
        feePayer.Equals(fromAddr, StringComparison.OrdinalIgnoreCase) &&
        feeUnits > BigInteger.Zero)
    {
        entries = entries.Where(e => e.AmountUnits != feeUnits).ToList();
    }

    foreach (var entry in entries)
    {
        var row = new MultiSendRow(
            entry.Sender,
            entry.Recipient,
            tx.Height,
            entry.AmountUnits,
            (decimal)entry.AmountUnits / scaleInt,
            timeUtc);
        row.TxHash = txHash;
        rows.Add(row);
    }

    return (rows, true);
}

static (List<MultiSendRow> Rows, bool MatchedFromAddress, bool Parsed) TryExtractMultiSendRowsFromTxBytes(
    string? base64Tx,
    long height,
    string txHash,
    string fromAddr,
    string denomWantedUpper,
    int scaleInt,
    DateTime? timeUtc)
{
    if (string.IsNullOrWhiteSpace(base64Tx))
        return (new List<MultiSendRow>(), false, false);

    Tx? tx;
    try
    {
        var txBytes = Convert.FromBase64String(base64Tx);
        tx = Tx.Parser.ParseFrom(txBytes);
    }
    catch
    {
        return (new List<MultiSendRow>(), false, false);
    }

    var rows = new List<MultiSendRow>();
    bool matched = false;

    foreach (var msgAny in tx.Body.Messages)
    {
        if (!IsMsgMultiSendTypeUrl(msgAny.TypeUrl)) continue;

        MsgMultiSend? msg;
        try
        {
            msg = MsgMultiSend.Parser.ParseFrom(msgAny.Value);
        }
        catch
        {
            continue;
        }

        if (msg.Inputs == null || msg.Inputs.Count == 0) continue;

        bool allFromAddr = true;
        foreach (var input in msg.Inputs)
        {
            if (!input.Address.Equals(fromAddr, StringComparison.OrdinalIgnoreCase))
            {
                allFromAddr = false;
                break;
            }
        }

        if (!allFromAddr) continue;
        matched = true;

        foreach (var output in msg.Outputs)
        {
            var toAddr = output.Address ?? "";
            if (string.IsNullOrWhiteSpace(toAddr)) continue;

            foreach (var coin in output.Coins)
            {
                if (!string.Equals(coin.Denom, denomWantedUpper, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!BigInteger.TryParse(coin.Amount, NumberStyles.Integer, CultureInfo.InvariantCulture, out var amt))
                    continue;

                if (amt <= BigInteger.Zero) continue;

                var row = new MultiSendRow(
                    fromAddr,
                    toAddr,
                    height,
                    amt,
                    (decimal)amt / scaleInt,
                    timeUtc);
                row.TxHash = txHash;
                rows.Add(row);
            }
        }
    }

    return (rows, matched, true);
}

static bool IsMsgMultiSendTypeUrl(string? typeUrl)
{
    if (string.IsNullOrWhiteSpace(typeUrl)) return false;
    return typeUrl.Contains("MsgMultiSend", StringComparison.OrdinalIgnoreCase);
}

static void WriteMultiSendCsv(string path, List<MultiSendRow> rows)
{
    var localTz = TimeZoneInfo.Local;
    var lines = new List<string>(rows.Count + 1)
    {
        "date_local,tx_hash,height,from_address,to_address,amount_opt"
    };

    foreach (var row in rows)
    {
        var local = row.TimeUtc.HasValue
            ? TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(row.TimeUtc.Value, DateTimeKind.Utc), localTz)
            : (DateTime?)null;
        var dateStr = local.HasValue
            ? local.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : "";
        var line = string.Join(",",
            CsvEscape(dateStr),
            CsvEscape(row.TxHash),
            CsvEscape(row.Height.ToString(CultureInfo.InvariantCulture)),
            CsvEscape(row.FromAddress),
            CsvEscape(row.ToAddress),
            CsvEscape(row.AmountOpt.ToString("0.000000", CultureInfo.InvariantCulture)));
        lines.Add(line);
    }

    File.WriteAllLines(path, lines);
}

static async Task<List<List<CometEvent>>?> TryGetBlockResultsTxEventsAsync(HttpClient rpcHttp, long height)
{
    if (height <= 0) return null;

    try
    {
        using var resp = await rpcHttp.GetAsync($"block_results?height={height}");
        if (!resp.IsSuccessStatusCode) return null;

        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("result", out var result)) return null;
        if (!result.TryGetProperty("txs_results", out var txs) || txs.ValueKind != JsonValueKind.Array)
            return new List<List<CometEvent>>();

        var output = new List<List<CometEvent>>(txs.GetArrayLength());
        foreach (var txr in txs.EnumerateArray())
        {
            if (!txr.TryGetProperty("events", out var evArr) || evArr.ValueKind != JsonValueKind.Array)
            {
                output.Add(new List<CometEvent>());
                continue;
            }

            var events = new List<CometEvent>(evArr.GetArrayLength());
            foreach (var ev in evArr.EnumerateArray())
            {
                var type = ev.TryGetProperty("type", out var t) ? (t.GetString() ?? "") : "";
                if (string.IsNullOrWhiteSpace(type)) continue;

                var attrs = new List<CometAttr>();
                if (ev.TryGetProperty("attributes", out var at) && at.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in at.EnumerateArray())
                    {
                        var k = a.TryGetProperty("key", out var kk) ? (kk.GetString() ?? "") : "";
                        var v = a.TryGetProperty("value", out var vv) ? (vv.GetString() ?? "") : "";
                        if (k.Length == 0) continue;
                        attrs.Add(new CometAttr(DecodeBase64Maybe(k), DecodeBase64Maybe(v)));
                    }
                }

                events.Add(new CometEvent(type, attrs));
            }

            output.Add(events);
        }

        return output;
    }
    catch
    {
        return null;
    }
}

static async Task<BigInteger> GetTotalDistributedFromBlocksAsync(
    HttpClient rpcHttp,
    string denomWantedUpper,
    long startHeight,
    long endHeight,
    string sender)
{
    var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { sender };
    BigInteger totalUopt = BigInteger.Zero;
    long scannedBlocks = 0;
    object totalLock = new object();

    var heights = Enumerable.Range(0, (int)(endHeight - startHeight + 1))
        .Select(i => startHeight + i)
        .ToList();

    await Parallel.ForEachAsync(
        heights,
        new ParallelOptions { MaxDegreeOfParallelism = 15 },
        async (height, ct) =>
        {
            var txEvents = await TryGetBlockResultsTxEventsAsync(rpcHttp, height);
            if (txEvents is null)
            {
                Interlocked.Increment(ref scannedBlocks);
                return;
            }

            BigInteger localTotal = BigInteger.Zero;
            foreach (var evs in txEvents)
            {
                var (txTotal, _) = SumDistributionTransfersFromEvents(evs, denomWantedUpper, allowed);
                if (txTotal <= BigInteger.Zero) continue;
                localTotal += txTotal;
            }

            if (localTotal > BigInteger.Zero)
            {
                lock (totalLock)
                    totalUopt += localTotal;
            }

            var done = Interlocked.Increment(ref scannedBlocks);
            if (done % 250 == 0)
                Console.WriteLine($"Scanned {done} blocks for emission totals...");
        });

    return totalUopt;
}

static (BigInteger Total, Dictionary<string, BigInteger> BySender) SumDistributionTransfersFromEvents(
    List<CometEvent> events,
    string denomWantedUpper,
    HashSet<string> allowedSenders)
{
    var msgIndexes = new HashSet<int>();
    bool hasDistributionAction = false;

    foreach (var ev in events)
    {
        if (!string.Equals(ev.Type, "message", StringComparison.Ordinal)) continue;

        string? action = null;
        int? msgIndex = null;
        foreach (var a in ev.Attributes)
        {
            if (a.Key == "action") action = a.Value;
            else if (a.Key == "msg_index" &&
                     int.TryParse(a.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                msgIndex = parsed;
        }

        if (!string.IsNullOrWhiteSpace(action) &&
            (action.Equals("/cosmos.bank.v1beta1.MsgMultiSend", StringComparison.OrdinalIgnoreCase) ||
             action.EndsWith(".MsgMultiSend", StringComparison.OrdinalIgnoreCase) ||
             action.Equals("/cosmos.bank.v1beta1.MsgSend", StringComparison.OrdinalIgnoreCase) ||
             action.EndsWith(".MsgSend", StringComparison.OrdinalIgnoreCase)))
        {
            hasDistributionAction = true;
            if (msgIndex.HasValue)
                msgIndexes.Add(msgIndex.Value);
        }
    }

    if (!hasDistributionAction)
        return (BigInteger.Zero, new Dictionary<string, BigInteger>(StringComparer.OrdinalIgnoreCase));

    var (feeUnits, feePayer) = GetFeeFromEventsUopt(events, denomWantedUpper);
    var totalsBySender = new Dictionary<string, BigInteger>(StringComparer.OrdinalIgnoreCase);
    BigInteger total = BigInteger.Zero;

    var transfers = events.Where(e => string.Equals(e.Type, "transfer", StringComparison.Ordinal)).ToList();
    if (transfers.Count == 0) return (BigInteger.Zero, totalsBySender);

    foreach (var ev in transfers)
    {
        var attrs = new List<(string Key, string Value)>(ev.Attributes.Count);
        foreach (var a in ev.Attributes)
            attrs.Add((a.Key ?? "", a.Value ?? ""));

        var msgIndex = TryParseMsgIndex(attrs);
        if (msgIndexes.Count > 0 && msgIndex.HasValue && !msgIndexes.Contains(msgIndex.Value))
            continue;

        var entries = ParseTransferEventTuplesBig(attrs, denomWantedUpper, out _);
        foreach (var entry in entries)
        {
            if (!allowedSenders.Contains(entry.Sender)) continue;

            bool looksLikeFeeTransfer =
                !msgIndex.HasValue &&
                feeUnits > BigInteger.Zero &&
                !string.IsNullOrWhiteSpace(feePayer) &&
                entry.AmountUnits == feeUnits &&
                entry.Sender.Equals(feePayer, StringComparison.OrdinalIgnoreCase);

            if (looksLikeFeeTransfer) continue;

            total += entry.AmountUnits;
            if (totalsBySender.TryGetValue(entry.Sender, out var existing))
                totalsBySender[entry.Sender] = existing + entry.AmountUnits;
            else
                totalsBySender[entry.Sender] = entry.AmountUnits;
        }
    }

    return (total, totalsBySender);
}

static string DecodeBase64Maybe(string value)
{
    if (string.IsNullOrWhiteSpace(value)) return value;

    try
    {
        var normalized = value.Trim();
        var mod = normalized.Length % 4;
        if (mod != 0)
            normalized = normalized + new string('=', 4 - mod);

        var bytes = Convert.FromBase64String(normalized);
        var decoded = System.Text.Encoding.UTF8.GetString(bytes);
        if (string.IsNullOrWhiteSpace(decoded)) return value;
        return IsMostlyPrintable(decoded) ? decoded : value;
    }
    catch
    {
        return value;
    }
}

static bool IsMostlyPrintable(string s)
{
    int printable = 0;
    foreach (var ch in s)
    {
        if (ch == '\r' || ch == '\n' || ch == '\t') { printable++; continue; }
        if (ch >= 32 && ch <= 126) { printable++; continue; }
        return false;
    }

    return printable == s.Length;
}

static async Task AddCometFallbackFromQuery(
    HttpClient rpcHttp,
    string query,
    Dictionary<string, TxResponse> txByHash,
    List<(string Hash, CometTx Tx, DateTime TimeUtc)> cometFallbackTxs,
    HashSet<string> cometFallbackHashes,
    DateTimeOffset cutoffUtc,
    int pageLimit,
    int maxPages)
{
    var hashes = await TryGetCometTxSearchHashesAsync(rpcHttp, query, pageLimit, maxPages);

    foreach (var hash in hashes)
    {
        if (txByHash.ContainsKey(hash)) continue;
        if (cometFallbackHashes.Contains(hash)) continue;

        var comet = await TryGetCometTxAsync(rpcHttp, hash);
        if (comet is null) continue;

        var bt = await TryGetBlockTimeFromCometRpcAsync(rpcHttp, comet.Height);
        if (bt is null) continue;
        if (new DateTimeOffset(bt.Value, TimeSpan.Zero) < cutoffUtc) continue;

        cometFallbackTxs.Add((hash, comet, bt.Value));
        cometFallbackHashes.Add(hash);
    }
}

static async Task PrintNetworkTotalsAllTimeAsync(
    HttpClient http,
    HttpClient rpcHttp,
    string denomWantedUpper,
    decimal scale,
    int pageLimit,
    int maxPages)
{
    Console.WriteLine();
    Console.WriteLine("== Network Totals (All-Time) ==");

    var bondedUnits = await TryGetStakingPoolBondedUnitsAsync(http);

    var distQuery = $"message.action='/cosmos.bank.v1beta1.MsgMultiSend' AND message.sender='{DistributionSourceAddress}'";
    var stakeQuery = "message.action='/cosmos.staking.v1beta1.MsgDelegate'";

    Console.WriteLine("Scanning MsgMultiSend transactions...");
    var distHashes = await TryGetCometTxSearchHashesAsync(rpcHttp, distQuery, pageLimit, maxPages);
    var distSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    decimal distributedUnits = 0m;

    foreach (var hash in distHashes)
    {
        if (!distSeen.Add(hash)) continue;
        var comet = await TryGetCometTxAsync(rpcHttp, hash);
        if (comet is null) continue;
        distributedUnits += SumMultiSendTransfersFromCometTx(comet, denomWantedUpper, DistributionSourceAddress);
    }

    Console.WriteLine("Scanning MsgDelegate transactions...");
    var stakeHashes = await TryGetCometTxSearchHashesAsync(rpcHttp, stakeQuery, pageLimit, maxPages);
    var stakeSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    decimal stakedUnits = 0m;

    foreach (var hash in stakeHashes)
    {
        if (!stakeSeen.Add(hash)) continue;
        var comet = await TryGetCometTxAsync(rpcHttp, hash);
        if (comet is null) continue;
        stakedUnits += SumDelegateAmountsFromCometTx(comet, denomWantedUpper);
    }

    var distributedOpt = (distributedUnits / scale).ToString("0.000000", CultureInfo.InvariantCulture);
    var stakedOpt = (stakedUnits / scale).ToString("0.000000", CultureInfo.InvariantCulture);
    Console.WriteLine($"Distributed (all addresses): {distributedOpt} OPT");
    if (bondedUnits.HasValue)
    {
        var bondedOpt = (bondedUnits.Value / scale).ToString("0.000000", CultureInfo.InvariantCulture);
        Console.WriteLine($"Staked (bonded total):       {bondedOpt} OPT");
    }
    else
    {
        Console.WriteLine($"Staked (sum of delegates):   {stakedOpt} OPT");
    }

    var validators = await GetBondedValidatorsAsync(http);
    if (validators.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine("Validators (bonded):");
        const int MonikerWidth = 24;
        const int AddressWidth = 52;
        const int AmountWidth = 20;

        static string LeftP(string s, int width) => s.Length > width ? s[..width] : s.PadRight(width);
        static string RightP(string s, int width) => s.Length > width ? s[..width] : s.PadLeft(width);

        Console.WriteLine(
            $"{LeftP("Name", MonikerWidth)} " +
            $"{LeftP("Address", AddressWidth)} " +
            $"{RightP("Staked(OPT)", AmountWidth)}");
        Console.WriteLine(new string('-', MonikerWidth + 1 + AddressWidth + 1 + AmountWidth));

        foreach (var v in validators.OrderByDescending(v => v.TokensUnits))
        {
            var amount = (v.TokensUnits / scale).ToString("0.000000", CultureInfo.InvariantCulture);
            Console.WriteLine(
                $"{LeftP(TruncateMiddle(v.Moniker, MonikerWidth), MonikerWidth)} " +
                $"{LeftP(TruncateMiddle(v.Address, AddressWidth), AddressWidth)} " +
                $"{RightP(amount, AmountWidth)}");
        }
    }
}

static async Task PrintValidatorsWithIpsAsync(
    HttpClient http,
    HttpClient rpcHttp,
    decimal scale)
{
    Console.WriteLine("== Validators (Bonded) ==");
    Console.WriteLine("Best-effort IPs are matched by moniker and may be incomplete.");
    Console.WriteLine();

    var validators = await GetBondedValidatorsAsync(http);
    var peers = await TryGetCometNetInfoPeersAsync(rpcHttp);

    var peersByMoniker = new Dictionary<string, List<CometPeerInfo>>(StringComparer.OrdinalIgnoreCase);
    foreach (var p in peers)
    {
        var moniker = p.Moniker?.Trim() ?? "";
        if (moniker.Length == 0) continue;
        if (!peersByMoniker.TryGetValue(moniker, out var list))
        {
            list = new List<CometPeerInfo>();
            peersByMoniker[moniker] = list;
        }
        list.Add(p);
    }

    if (validators.Count == 0)
    {
        Console.WriteLine("No bonded validators found.");
        return;
    }

    const int MonikerWidth = 22;
    const int AddressWidth = 52;
    const int AmountWidth = 16;
    const int IpWidth = 32;
    const int ListenWidth = 22;

    static string LeftP(string s, int width) => s.Length > width ? s[..width] : s.PadRight(width);
    static string RightP(string s, int width) => s.Length > width ? s[..width] : s.PadLeft(width);

    Console.WriteLine(
        $"{LeftP("Name", MonikerWidth)} " +
        $"{LeftP("Address", AddressWidth)} " +
        $"{RightP("Staked(OPT)", AmountWidth)} " +
        $"{LeftP("IP", IpWidth)} " +
        $"{LeftP("Listen", ListenWidth)}");
    Console.WriteLine(new string('-', MonikerWidth + 1 + AddressWidth + 1 + AmountWidth + 1 + IpWidth + 1 + ListenWidth));

    foreach (var v in validators.OrderByDescending(v => v.TokensUnits))
    {
        var moniker = v.Moniker ?? "";
        peersByMoniker.TryGetValue(moniker, out var vPeers);
        var ipList = (vPeers ?? new List<CometPeerInfo>())
            .Select(p => p.RemoteIp)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var listenList = (vPeers ?? new List<CometPeerInfo>())
            .Select(p => p.ListenAddr)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var ipText = ipList.Count > 0 ? string.Join(",", ipList) : "unknown";
        var listenText = listenList.Count > 0 ? string.Join(",", listenList) : "unknown";

        var amount = (v.TokensUnits / scale).ToString("0.000000", CultureInfo.InvariantCulture);
        Console.WriteLine(
            $"{LeftP(TruncateMiddle(moniker, MonikerWidth), MonikerWidth)} " +
            $"{LeftP(TruncateMiddle(v.Address, AddressWidth), AddressWidth)} " +
            $"{RightP(amount, AmountWidth)} " +
            $"{LeftP(TruncateMiddle(ipText, IpWidth), IpWidth)} " +
            $"{LeftP(TruncateMiddle(listenText, ListenWidth), ListenWidth)}");
    }
}

static decimal SumMultiSendTransfersFromCometTx(CometTx tx, string denomWantedUpper, string? requiredSender)
{
    var msgIndexes = GetMessageIndexesForAction(tx, "MsgMultiSend");
    var (feeUnits, feePayer) = GetFeeFromCometTx(tx, denomWantedUpper);
    decimal totalUnits = 0m;

    foreach (var ev in tx.Events)
    {
        if (!string.Equals(ev.Type, "transfer", StringComparison.Ordinal)) continue;

        var pairs = new List<(string Key, string Value)>(ev.Attributes.Count);
        foreach (var a in ev.Attributes)
            pairs.Add((a.Key ?? "", a.Value ?? ""));

        var msgIndex = TryParseMsgIndex(pairs);
        var entries = ParseTransferEventTuples(pairs, denomWantedUpper, out _);
        if (entries.Count == 0) continue;

        foreach (var e in entries)
        {
            bool looksLikeFeeTransfer =
                !msgIndex.HasValue &&
                feeUnits > 0m &&
                !string.IsNullOrWhiteSpace(feePayer) &&
                e.AmountUnits == feeUnits &&
                e.Sender.Equals(feePayer, StringComparison.OrdinalIgnoreCase);

            if (looksLikeFeeTransfer) continue;
            if (!string.IsNullOrWhiteSpace(requiredSender) &&
                !e.Sender.Equals(requiredSender, StringComparison.OrdinalIgnoreCase))
                continue;

            if (msgIndexes.Count > 0)
            {
                if (!msgIndex.HasValue || !msgIndexes.Contains(msgIndex.Value)) continue;
            }
            else if (!msgIndex.HasValue)
            {
                continue;
            }

            totalUnits += e.AmountUnits;
        }
    }

    return totalUnits;
}

static void AccumulateTotalsFromTxResponse(
    TxResponse tr,
    string emitterAddr,
    string denomWantedUpper,
    ref BigInteger totalEmittedUopt,
    ref BigInteger totalDistributedUopt)
{
    if (string.IsNullOrWhiteSpace(emitterAddr)) return;

    var minted = SumMintedUoptFromTxResponse(tr, emitterAddr, denomWantedUpper);
    if (minted > BigInteger.Zero)
        totalEmittedUopt += minted;

    var distributed = SumDistributedUoptFromTxResponse(tr, emitterAddr, denomWantedUpper);
    if (distributed > BigInteger.Zero)
        totalDistributedUopt += distributed;
}

static void AccumulateTotalsFromCometTx(
    CometTx tx,
    string emitterAddr,
    string denomWantedUpper,
    ref BigInteger totalEmittedUopt,
    ref BigInteger totalDistributedUopt)
{
    if (string.IsNullOrWhiteSpace(emitterAddr)) return;

    var minted = SumMintedUoptFromCometTx(tx, emitterAddr, denomWantedUpper);
    if (minted > BigInteger.Zero)
        totalEmittedUopt += minted;

    var distributed = SumDistributedUoptFromCometTx(tx, emitterAddr, denomWantedUpper);
    if (distributed > BigInteger.Zero)
        totalDistributedUopt += distributed;
}

static decimal SumDelegateAmountsFromCometTx(CometTx tx, string denomWantedUpper)
{
    decimal totalUnits = 0m;

    foreach (var ev in tx.Events)
    {
        if (!string.Equals(ev.Type, "delegate", StringComparison.Ordinal)) continue;

        foreach (var a in ev.Attributes)
        {
            if (a.Key == "amount")
                totalUnits += ParseEventAmountUnits(a.Value, denomWantedUpper);
        }
    }

    return totalUnits;
}

static HashSet<int> GetMessageIndexesForAction(CometTx tx, string actionContains)
{
    var indexes = new HashSet<int>();

    foreach (var ev in tx.Events)
    {
        if (!string.Equals(ev.Type, "message", StringComparison.Ordinal)) continue;

        string? action = null;
        int? msgIndex = null;

        foreach (var a in ev.Attributes)
        {
            if (a.Key == "action") action = a.Value;
            else if (a.Key == "msg_index" &&
                     int.TryParse(a.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                msgIndex = parsed;
        }

        if (msgIndex.HasValue &&
            !string.IsNullOrWhiteSpace(action) &&
            action.Contains(actionContains, StringComparison.OrdinalIgnoreCase))
            indexes.Add(msgIndex.Value);
    }

    return indexes;
}

static int? TryParseMsgIndex(List<(string Key, string Value)> attrs)
{
    foreach (var (key, value) in attrs)
    {
        if (key == "msg_index" &&
            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            return parsed;
    }

    return null;
}

static async Task<decimal?> TryGetStakingPoolBondedUnitsAsync(HttpClient http)
{
    var doc = await TryGetJsonAsync(http, "cosmos/staking/v1beta1/pool");
    if (doc is null) return null;

    using (doc)
    {
        if (!doc.RootElement.TryGetProperty("pool", out var pool)) return null;
        if (!pool.TryGetProperty("bonded_tokens", out var bonded)) return null;

        var bondedStr = bonded.GetString();
        if (string.IsNullOrWhiteSpace(bondedStr)) return null;

        if (decimal.TryParse(bondedStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var units))
            return units;
    }

    return null;
}

static async Task<List<(string Moniker, string Address, decimal TokensUnits)>> GetBondedValidatorsAsync(HttpClient http)
{
    var validators = new List<(string Moniker, string Address, decimal TokensUnits)>();
    string? nextKey = null;

    while (true)
    {
        var url = "cosmos/staking/v1beta1/validators?status=BOND_STATUS_BONDED&pagination.limit=2000";
        if (!string.IsNullOrWhiteSpace(nextKey))
            url += $"&pagination.key={Uri.EscapeDataString(nextKey)}";

        var doc = await TryGetJsonAsync(http, url);
        if (doc is null) break;

        using (doc)
        {
            if (doc.RootElement.TryGetProperty("validators", out var vals) &&
                vals.ValueKind == JsonValueKind.Array)
            {
                foreach (var v in vals.EnumerateArray())
                {
                    var addr = v.TryGetProperty("operator_address", out var addrEl) ? addrEl.GetString() ?? "" : "";
                    var moniker = "";
                    if (v.TryGetProperty("description", out var desc) &&
                        desc.TryGetProperty("moniker", out var monEl))
                        moniker = monEl.GetString() ?? "";

                    var tokensStr = v.TryGetProperty("tokens", out var tokensEl) ? tokensEl.GetString() ?? "" : "";
                    if (!decimal.TryParse(tokensStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tokensUnits))
                        tokensUnits = 0m;

                    if (!string.IsNullOrWhiteSpace(addr))
                        validators.Add((moniker, addr, tokensUnits));
                }
            }

            if (doc.RootElement.TryGetProperty("pagination", out var pagination) &&
                pagination.TryGetProperty("next_key", out var nextKeyEl))
            {
                nextKey = nextKeyEl.GetString();
            }
            else
            {
                nextKey = null;
            }
        }

        if (string.IsNullOrWhiteSpace(nextKey)) break;
    }

    return validators;
}

static (decimal? FeeUnits, string? FeePayer) GetFeeFromCometTx(CometTx comet, string denomWantedUpper)
{
    decimal total = 0m;
    string? payer = null;

    foreach (var ev in comet.Events)
    {
        if (!string.Equals(ev.Type, "tx", StringComparison.Ordinal)) continue;

        foreach (var a in ev.Attributes)
        {
            if (a.Key == "fee") total += ParseEventAmountUnits(a.Value, denomWantedUpper);
            else if (a.Key == "fee_payer" && !string.IsNullOrWhiteSpace(a.Value)) payer = a.Value;
        }
    }

    return (total, payer);
}

static IEnumerable<LedgerRow> ExtractTransfersFromCometTx(
    CometTx comet,
    string targetAddr,
    string denomWantedUpper,
    decimal feeUnits,
    string? feePayer)
{
    foreach (var ev in comet.Events)
    {
        if (!string.Equals(ev.Type, "transfer", StringComparison.Ordinal)) continue;

        var pairs = new List<(string Key, string Value)>(ev.Attributes.Count);
        foreach (var a in ev.Attributes)
            pairs.Add((a.Key, a.Value));

        var entries = ParseTransferEventTuples(pairs, denomWantedUpper, out var hasMsgIndex);
        if (entries.Count == 0) continue;

        foreach (var e in entries)
        {
            // Fee-transfer filter matches the logic used for gRPC events
            bool looksLikeFeeTransfer =
                !hasMsgIndex &&
                feeUnits > 0m &&
                e.AmountUnits == feeUnits &&
                !string.IsNullOrWhiteSpace(feePayer) &&
                e.Sender.Equals(feePayer, StringComparison.OrdinalIgnoreCase) &&
                !e.Recipient.Equals(targetAddr, StringComparison.OrdinalIgnoreCase);

            if (e.Recipient.Equals(targetAddr, StringComparison.OrdinalIgnoreCase))
                yield return new LedgerRow(DateTime.MinValue, "recv", e.Sender, e.Recipient, e.AmountUnits, "", 0m, false);

            if (e.Sender.Equals(targetAddr, StringComparison.OrdinalIgnoreCase) && !looksLikeFeeTransfer)
                yield return new LedgerRow(DateTime.MinValue, "sent", e.Sender, e.Recipient, e.AmountUnits, "", 0m, false);
        }
    }
}

// Core requirement: base64-decode CometBFT tx bytes -> cosmos.tx.v1beta1.Tx -> Tx.Body.Memo
static string ExtractMemoFromBase64Tx(string base64Tx)
{
    if (string.IsNullOrWhiteSpace(base64Tx)) return string.Empty;

    byte[] txBytes = Convert.FromBase64String(base64Tx);
    var tx = Tx.Parser.ParseFrom(txBytes);
    return tx?.Body?.Memo ?? string.Empty;
}

static string InferMemoLabel(TxResponse tr)
{
    string? action = null;
    string? module = null;

    foreach (var ev in tr.Events)
    {
        if (ev.Type != "message") continue;

        foreach (var a in ev.Attributes)
        {
            if (a.Key == "action" && !string.IsNullOrWhiteSpace(a.Value)) action ??= a.Value;
            if (a.Key == "module" && !string.IsNullOrWhiteSpace(a.Value)) module ??= a.Value;
        }
    }

    var shortAction = ShortTypeName(action);
    var friendlyAction = FriendlyActionName(shortAction);
    if (!string.IsNullOrWhiteSpace(module) && !string.IsNullOrWhiteSpace(friendlyAction))
        return $"{module}:{friendlyAction}";

    if (!string.IsNullOrWhiteSpace(friendlyAction)) return friendlyAction!;
    if (!string.IsNullOrWhiteSpace(module)) return module!;
    return "";
}

static string InferMemoLabelFromCometTx(CometTx tx)
{
    string? action = null;
    string? module = null;

    foreach (var ev in tx.Events)
    {
        if (!string.Equals(ev.Type, "message", StringComparison.Ordinal)) continue;

        foreach (var a in ev.Attributes)
        {
            if (a.Key == "action" && !string.IsNullOrWhiteSpace(a.Value)) action ??= a.Value;
            if (a.Key == "module" && !string.IsNullOrWhiteSpace(a.Value)) module ??= a.Value;
        }
    }

    var shortAction = ShortTypeName(action);
    var friendlyAction = FriendlyActionName(shortAction);
    if (!string.IsNullOrWhiteSpace(module) && !string.IsNullOrWhiteSpace(friendlyAction))
        return $"{module}:{friendlyAction}";

    if (!string.IsNullOrWhiteSpace(friendlyAction)) return friendlyAction!;
    if (!string.IsNullOrWhiteSpace(module)) return module!;
    return "";
}

static string? ShortTypeName(string? typeUrl)
{
    if (string.IsNullOrWhiteSpace(typeUrl)) return null;
    var t = typeUrl.Trim();

    var slash = t.LastIndexOf('/');
    if (slash >= 0 && slash + 1 < t.Length) return t[(slash + 1)..];

    var dot = t.LastIndexOf('.');
    if (dot >= 0 && dot + 1 < t.Length) return t[(dot + 1)..];

    return t;
}

static string? FriendlyActionName(string? shortAction)
{
    if (string.IsNullOrWhiteSpace(shortAction)) return shortAction;

    var mapped = shortAction switch
    {
        "MsgSend" => "Send",
        "MsgMultiSend" => "Batch send",
        "MsgDelegate" => "Distribution",
        "MsgBeginRedelegate" => "Redelegate",
        "MsgUndelegate" => "Undelegate",
        "MsgWithdrawDelegatorReward" => "Withdraw reward",
        "MsgWithdrawValidatorCommission" => "Withdraw commission",
        "MsgVote" => "Vote",
        "MsgDeposit" => "Deposit",
        "MsgSubmitProposal" => "Submit proposal",
        "consolidate" => "Consolidate",
        _ => shortAction
    };

    if (mapped == shortAction && shortAction.StartsWith("Msg", StringComparison.Ordinal) && shortAction.Length > 3)
        return SplitPascalCase(shortAction[3..]);

    return mapped;
}

static string NormalizeMemoLabel(string? memo)
{
    if (string.IsNullOrWhiteSpace(memo)) return memo ?? "";

    var trimmed = memo.Trim();
    string? module = null;
    var action = trimmed;

    var idx = trimmed.IndexOf(':');
    if (idx > 0 && idx + 1 < trimmed.Length)
    {
        module = trimmed[..idx].Trim();
        action = trimmed[(idx + 1)..].Trim();
    }

    var shortAction = ShortTypeName(action) ?? action;
    var friendly = FriendlyActionName(shortAction) ?? shortAction;

    if (!string.IsNullOrWhiteSpace(module))
    {
        var shortModule = ShortTypeName(module) ?? module;
        if (shortModule.Equals("bank", StringComparison.OrdinalIgnoreCase) &&
            (friendly.Equals("Send", StringComparison.OrdinalIgnoreCase) ||
             friendly.Equals("Batch send", StringComparison.OrdinalIgnoreCase)))
            return "Transaction";
        if (string.Equals(friendly, "Distribution", StringComparison.OrdinalIgnoreCase))
            return friendly;
        return $"{shortModule}:{friendly}";
    }

    return friendly;
}

static string DetermineRowMemoFromTxResponse(TxResponse tr, LedgerRow row, string defaultMemo, string addr, string denomWantedUpper)
{
    if (IsStakingRewardRowFromTxResponse(tr, row, addr, denomWantedUpper)) return "Staking Reward";
    if (IsMultiSendDistributionRowFromTxResponse(tr, row, addr, denomWantedUpper))
        return "Distribution";
    if (IsUnknownSenderDistributionRow(row, addr)) return "Distribution";
    return NormalizeDistributionLabelForDirection(row, defaultMemo);
}

static string DetermineRowMemoFromCometTx(CometTx tx, LedgerRow row, string defaultMemo, string addr, string denomWantedUpper)
{
    if (IsStakingRewardRowFromCometTx(tx, row, addr, denomWantedUpper)) return "Staking Reward";
    if (IsMultiSendDistributionRowFromCometTx(tx, row, addr, denomWantedUpper))
        return "Distribution";
    if (IsUnknownSenderDistributionRow(row, addr)) return "Distribution";
    return NormalizeDistributionLabelForDirection(row, defaultMemo);
}

static string NormalizeDistributionLabelForDirection(LedgerRow row, string memo)
{
    if (string.Equals(memo, "Distribution", StringComparison.OrdinalIgnoreCase) &&
        !IsValidDistributionRow(row))
        return "Transaction";
    return memo;
}

static bool IsValidDistributionRow(LedgerRow row)
{
    if (!row.Direction.Equals("recv", StringComparison.OrdinalIgnoreCase)) return false;
    if (row.From.Equals("(unknown)", StringComparison.OrdinalIgnoreCase)) return true;
    return row.From.Equals(DistributionSourceAddress, StringComparison.OrdinalIgnoreCase);
}

static bool IsUnknownSenderDistributionRow(LedgerRow row, string addr)
{
    if (!row.Direction.Equals("recv", StringComparison.OrdinalIgnoreCase)) return false;
    if (!row.To.Equals(addr, StringComparison.OrdinalIgnoreCase)) return false;
    return row.From.Equals("(unknown)", StringComparison.OrdinalIgnoreCase);
}

static BigInteger SumDistributedToAddressUopt(List<LedgerRow> rows, string addr)
{
    BigInteger total = BigInteger.Zero;

    foreach (var row in rows)
    {
        if (!row.Direction.Equals("recv", StringComparison.OrdinalIgnoreCase)) continue;
        if (!row.To.Equals(addr, StringComparison.OrdinalIgnoreCase)) continue;
        if (!string.Equals(row.Label, "Distribution", StringComparison.OrdinalIgnoreCase)) continue;

        total += new BigInteger(row.AmountInDenomUnits);
    }

    return total;
}

static bool IsStakingRewardRowFromTxResponse(TxResponse tr, LedgerRow row, string addr, string denomWantedUpper)
{
    if (!row.Direction.Equals("recv", StringComparison.OrdinalIgnoreCase)) return false;
    if (!row.To.Equals(addr, StringComparison.OrdinalIgnoreCase)) return false;

    foreach (var ev in tr.Events)
    {
        if (!string.Equals(ev.Type, "withdraw_rewards", StringComparison.Ordinal)) continue;

        string? delegator = null;
        decimal amountUnits = 0m;

        foreach (var a in ev.Attributes)
        {
            var k = a.Key ?? "";
            var v = a.Value ?? "";
            if (k == "delegator") delegator = v;
            else if (k == "amount") amountUnits += ParseEventAmountUnits(v, denomWantedUpper);
        }

        if (string.IsNullOrWhiteSpace(delegator)) continue;
        if (!delegator.Equals(addr, StringComparison.OrdinalIgnoreCase)) continue;
        if (amountUnits == row.AmountInDenomUnits) return true;
    }

    return false;
}

static bool IsStakingRewardRowFromCometTx(CometTx tx, LedgerRow row, string addr, string denomWantedUpper)
{
    if (!row.Direction.Equals("recv", StringComparison.OrdinalIgnoreCase)) return false;
    if (!row.To.Equals(addr, StringComparison.OrdinalIgnoreCase)) return false;

    foreach (var ev in tx.Events)
    {
        if (!string.Equals(ev.Type, "withdraw_rewards", StringComparison.Ordinal)) continue;

        string? delegator = null;
        decimal amountUnits = 0m;

        foreach (var a in ev.Attributes)
        {
            if (a.Key == "delegator") delegator = a.Value;
            else if (a.Key == "amount") amountUnits += ParseEventAmountUnits(a.Value, denomWantedUpper);
        }

        if (string.IsNullOrWhiteSpace(delegator)) continue;
        if (!delegator.Equals(addr, StringComparison.OrdinalIgnoreCase)) continue;
        if (amountUnits == row.AmountInDenomUnits) return true;
    }

    return false;
}

static bool IsMultiSendDistributionRowFromTxResponse(TxResponse tr, LedgerRow row, string addr, string denomWantedUpper)
{
    if (!row.Direction.Equals("recv", StringComparison.OrdinalIgnoreCase)) return false;
    if (!row.To.Equals(addr, StringComparison.OrdinalIgnoreCase)) return false;
    if (!HasMultiSendActionFromTxResponse(tr)) return false;

    var (transferCount, hasTargetRecipient) = GetTransferSummaryFromTxResponse(tr, addr, denomWantedUpper);
    return transferCount > 1 && hasTargetRecipient;
}

static bool IsMultiSendDistributionRowFromCometTx(CometTx tx, LedgerRow row, string addr, string denomWantedUpper)
{
    if (!row.Direction.Equals("recv", StringComparison.OrdinalIgnoreCase)) return false;
    if (!row.To.Equals(addr, StringComparison.OrdinalIgnoreCase)) return false;
    if (!HasMultiSendActionFromCometTx(tx)) return false;

    var (transferCount, hasTargetRecipient) = GetTransferSummaryFromCometTx(tx, addr, denomWantedUpper);
    return transferCount > 1 && hasTargetRecipient;
}

static bool HasMultiSendActionFromTxResponse(TxResponse tr)
{
    foreach (var ev in tr.Events)
    {
        if (ev.Type != "message") continue;
        foreach (var a in ev.Attributes)
        {
            if (a.Key == "action" &&
                (a.Value?.Contains("MsgMultiSend", StringComparison.OrdinalIgnoreCase) ?? false))
                return true;
        }
    }

    return false;
}

static bool HasDelegateActionFromTxResponse(TxResponse tr)
{
    foreach (var ev in tr.Events)
    {
        if (ev.Type != "message") continue;
        foreach (var a in ev.Attributes)
        {
            if (a.Key == "action" &&
                (a.Value?.Contains("MsgDelegate", StringComparison.OrdinalIgnoreCase) ?? false))
                return true;
        }
    }

    return false;
}

static bool HasMultiSendActionFromCometTx(CometTx tx)
{
    foreach (var ev in tx.Events)
    {
        if (!string.Equals(ev.Type, "message", StringComparison.Ordinal)) continue;
        foreach (var a in ev.Attributes)
        {
            if (a.Key == "action" &&
                a.Value.Contains("MsgMultiSend", StringComparison.OrdinalIgnoreCase))
                return true;
        }
    }

    return false;
}

static (int TransferCount, bool HasTargetRecipient) GetTransferSummaryFromTxResponse(
    TxResponse tr,
    string addr,
    string denomWantedUpper)
{
    int count = 0;
    bool hasTarget = false;

    foreach (var ev in tr.Events)
    {
        if (!string.Equals(ev.Type, "transfer", StringComparison.Ordinal)) continue;

        var pairs = new List<(string Key, string Value)>(ev.Attributes.Count);
        foreach (var a in ev.Attributes)
            pairs.Add((a.Key ?? "", a.Value ?? ""));

        var entries = ParseTransferEventTuples(pairs, denomWantedUpper, out _);
        if (entries.Count == 0) continue;

        count += entries.Count;
        foreach (var e in entries)
        {
            if (e.Recipient.Equals(addr, StringComparison.OrdinalIgnoreCase))
            {
                hasTarget = true;
                break;
            }
        }
    }

    return (count, hasTarget);
}

static (int TransferCount, bool HasTargetRecipient) GetTransferSummaryFromCometTx(
    CometTx tx,
    string addr,
    string denomWantedUpper)
{
    int count = 0;
    bool hasTarget = false;

    foreach (var ev in tx.Events)
    {
        if (!string.Equals(ev.Type, "transfer", StringComparison.Ordinal)) continue;

        var pairs = new List<(string Key, string Value)>(ev.Attributes.Count);
        foreach (var a in ev.Attributes)
            pairs.Add((a.Key, a.Value));

        var entries = ParseTransferEventTuples(pairs, denomWantedUpper, out _);
        if (entries.Count == 0) continue;

        count += entries.Count;
        foreach (var e in entries)
        {
            if (e.Recipient.Equals(addr, StringComparison.OrdinalIgnoreCase))
            {
                hasTarget = true;
                break;
            }
        }
    }

    return (count, hasTarget);
}

static bool IsFeeTransferRow(LedgerRow row, decimal feeUnits, string? feePayer, string targetAddr)
{
    if (feeUnits <= 0m) return false;
    if (string.IsNullOrWhiteSpace(feePayer)) return false;
    if (!row.Direction.Equals("sent", StringComparison.OrdinalIgnoreCase)) return false;
    if (!row.From.Equals(feePayer, StringComparison.OrdinalIgnoreCase)) return false;
    if (row.To.Equals(targetAddr, StringComparison.OrdinalIgnoreCase)) return false;
    if (row.AmountInDenomUnits != feeUnits) return false;
    return true;
}

static bool TryOverrideAmountWithDelegateFromTxResponse(TxResponse tr, LedgerRow row, string addr, string denomWantedUpper)
{
    var amt = GetDelegateAmountUnitsFromTxResponse(tr, addr, denomWantedUpper);
    if (!amt.HasValue) return false;
    row.AmountInDenomUnits = amt.Value;
    return true;
}

static bool TryOverrideAmountWithDelegateFromCometTx(CometTx tx, LedgerRow row, string addr, string denomWantedUpper)
{
    var amt = GetDelegateAmountUnitsFromCometTx(tx, addr, denomWantedUpper);
    if (!amt.HasValue) return false;
    row.AmountInDenomUnits = amt.Value;
    return true;
}

static decimal? GetDelegateAmountUnitsFromTxResponse(TxResponse tr, string addr, string denomWantedUpper)
{
    foreach (var ev in tr.Events)
    {
        if (!string.Equals(ev.Type, "delegate", StringComparison.Ordinal)) continue;

        string? delegator = null;
        decimal amountUnits = 0m;

        foreach (var a in ev.Attributes)
        {
            var k = a.Key ?? "";
            var v = a.Value ?? "";
            if (k == "delegator") delegator = v;
            else if (k == "amount") amountUnits += ParseEventAmountUnits(v, denomWantedUpper);
        }

        if (string.IsNullOrWhiteSpace(delegator)) continue;
        if (!delegator.Equals(addr, StringComparison.OrdinalIgnoreCase)) continue;
        if (amountUnits > 0m) return amountUnits;
    }

    return null;
}

static decimal? GetDelegateAmountUnitsFromCometTx(CometTx tx, string addr, string denomWantedUpper)
{
    foreach (var ev in tx.Events)
    {
        if (!string.Equals(ev.Type, "delegate", StringComparison.Ordinal)) continue;

        string? delegator = null;
        decimal amountUnits = 0m;

        foreach (var a in ev.Attributes)
        {
            if (a.Key == "delegator") delegator = a.Value;
            else if (a.Key == "amount") amountUnits += ParseEventAmountUnits(a.Value, denomWantedUpper);
        }

        if (string.IsNullOrWhiteSpace(delegator)) continue;
        if (!delegator.Equals(addr, StringComparison.OrdinalIgnoreCase)) continue;
        if (amountUnits > 0m) return amountUnits;
    }

    return null;
}

static string SplitPascalCase(string s)
{
    if (string.IsNullOrEmpty(s)) return s;
    var sb = new System.Text.StringBuilder(s.Length + 4);
    sb.Append(s[0]);
    for (int i = 1; i < s.Length; i++)
    {
        var c = s[i];
        if (char.IsUpper(c) && !char.IsUpper(s[i - 1]))
            sb.Append(' ');
        sb.Append(c);
    }
    return sb.ToString();
}

// ---------------- Event parsing ----------------

static IEnumerable<LedgerRow> ExtractTransfersFromEvents(
    TxResponse tr,
    string addr,
    string denomWantedUpper,
    decimal feeUnits,
    string? feePayer)
{
    foreach (var ev in tr.Events)
    {
        if (!string.Equals(ev.Type, "transfer", StringComparison.Ordinal)) continue;

        var pairs = new List<(string Key, string Value)>(ev.Attributes.Count);
        foreach (var a in ev.Attributes)
            pairs.Add((a.Key ?? "", a.Value ?? ""));

        var entries = ParseTransferEventTuples(pairs, denomWantedUpper, out var hasMsgIndex);
        if (entries.Count == 0) continue;

        foreach (var e in entries)
        {
            bool looksLikeFeeTransfer =
                !hasMsgIndex &&
                feeUnits > 0m &&
                e.AmountUnits == feeUnits &&
                !string.IsNullOrWhiteSpace(feePayer) &&
                e.Sender.Equals(feePayer, StringComparison.OrdinalIgnoreCase) &&
                !e.Recipient.Equals(addr, StringComparison.OrdinalIgnoreCase);

            if (e.Recipient.Equals(addr, StringComparison.OrdinalIgnoreCase))
                yield return new LedgerRow(DateTime.MinValue, "recv", e.Sender, e.Recipient, e.AmountUnits, "", 0m, false);

            if (e.Sender.Equals(addr, StringComparison.OrdinalIgnoreCase) && !looksLikeFeeTransfer)
                yield return new LedgerRow(DateTime.MinValue, "sent", e.Sender, e.Recipient, e.AmountUnits, "", 0m, false);
        }
    }
}

static IEnumerable<LedgerRow> ExtractDelegateRowsFromTxResponse(
    TxResponse tr,
    string addr,
    string denomWantedUpper)
{
    foreach (var ev in tr.Events)
    {
        if (!string.Equals(ev.Type, "delegate", StringComparison.Ordinal)) continue;

        string? delegator = null;
        string? validator = null;
        decimal amountUnits = 0m;

        foreach (var a in ev.Attributes)
        {
            var k = a.Key ?? "";
            var v = a.Value ?? "";

            if (k == "delegator") delegator = v;
            else if (k == "validator") validator = v;
            else if (k == "amount") amountUnits += ParseEventAmountUnits(v, denomWantedUpper);
        }

        if (string.IsNullOrWhiteSpace(delegator) || string.IsNullOrWhiteSpace(validator)) continue;
        if (!delegator.Equals(addr, StringComparison.OrdinalIgnoreCase)) continue;
        if (amountUnits <= 0m) continue;

        yield return new LedgerRow(DateTime.MinValue, "sent", delegator, validator, amountUnits, "", 0m, false);
    }
}

static IEnumerable<LedgerRow> ExtractDelegateRowsFromCometTx(
    CometTx tx,
    string addr,
    string denomWantedUpper)
{
    foreach (var ev in tx.Events)
    {
        if (!string.Equals(ev.Type, "delegate", StringComparison.Ordinal)) continue;

        string? delegator = null;
        string? validator = null;
        decimal amountUnits = 0m;

        foreach (var a in ev.Attributes)
        {
            if (a.Key == "delegator") delegator = a.Value;
            else if (a.Key == "validator") validator = a.Value;
            else if (a.Key == "amount") amountUnits += ParseEventAmountUnits(a.Value, denomWantedUpper);
        }

        if (string.IsNullOrWhiteSpace(delegator) || string.IsNullOrWhiteSpace(validator)) continue;
        if (!delegator.Equals(addr, StringComparison.OrdinalIgnoreCase)) continue;
        if (amountUnits <= 0m) continue;

        yield return new LedgerRow(DateTime.MinValue, "sent", delegator, validator, amountUnits, "", 0m, false);
    }
}

static List<TransferEntry> ParseTransferEventTuples(
    List<(string Key, string Value)> attrs,
    string denomWantedUpper,
    out bool hasMsgIndex)
{
    var entries = new List<TransferEntry>();
    hasMsgIndex = false;

    var senders = new List<string>();
    var recipients = new List<string>();
    var amountStrings = new List<string>();

    foreach (var (key, value) in attrs)
    {
        if (key == "msg_index")
        {
            hasMsgIndex = true;
            continue;
        }

        if (key == "sender")
        {
            if (!string.IsNullOrWhiteSpace(value)) senders.Add(value);
            continue;
        }

        if (key == "recipient")
        {
            if (!string.IsNullOrWhiteSpace(value)) recipients.Add(value);
            continue;
        }

        if (key == "amount")
        {
            if (!string.IsNullOrWhiteSpace(value)) amountStrings.Add(value);
        }
    }

    if (amountStrings.Count == 0 || recipients.Count == 0) return entries;

    int count = Math.Min(amountStrings.Count, recipients.Count);
    string? singleSender = senders.Count == 1 ? senders[0] : null;

    for (int i = 0; i < count; i++)
    {
        var sender = singleSender ?? (senders.Count > i ? senders[i] : null);
        var recipient = recipients[i];
        if (string.IsNullOrWhiteSpace(sender) || string.IsNullOrWhiteSpace(recipient)) continue;

        var amountUnits = ParseEventAmountUnits(amountStrings[i], denomWantedUpper);
        if (amountUnits <= 0m) continue;

        entries.Add(new TransferEntry(sender, recipient, amountUnits));
    }

    return entries;
}

static IEnumerable<LedgerRow> ExtractCoinFallbackRows(TxResponse tr, string addr, string denomWantedUpper)
{
    foreach (var ev in tr.Events)
    {
        if (ev.Type != "coin_received" && ev.Type != "coin_spent") continue;

        string? party = null;
        decimal amountUnits = 0m;

        foreach (var a in ev.Attributes)
        {
            var k = a.Key ?? "";
            var v = a.Value ?? "";

            if (ev.Type == "coin_received" && k == "receiver") party = v;
            if (ev.Type == "coin_spent" && k == "spender") party = v;
            if (k == "amount") amountUnits += ParseEventAmountUnits(v, denomWantedUpper);
        }

        if (amountUnits <= 0m) continue;
        if (party == null) continue;
        if (!party.Equals(addr, StringComparison.OrdinalIgnoreCase)) continue;

        if (ev.Type == "coin_received")
            yield return new LedgerRow(DateTime.MinValue, "recv", "(unknown)", addr, amountUnits, "", 0m, false);
        else
            yield return new LedgerRow(DateTime.MinValue, "sent", addr, "(unknown)", amountUnits, "", 0m, false);
    }
}

static BigInteger SumMintedUoptFromTxResponse(TxResponse tr, string emitterAddr, string denomWantedUpper)
{
    if (!HasMintActionFromTxResponse(tr) &&
        !HasMintEvidenceFromTxResponse(tr, emitterAddr, denomWantedUpper))
        return BigInteger.Zero;

    var received = SumEmitterReceivesFromTxResponse(tr, emitterAddr, denomWantedUpper);
    if (received > BigInteger.Zero)
        return received;

    return SumMintEventAmountsFromTxResponse(tr, denomWantedUpper);
}

static BigInteger SumDistributedUoptFromTxResponse(TxResponse tr, string emitterAddr, string denomWantedUpper)
{
    var transfers = ExtractTransferEntriesFromTxResponse(tr, denomWantedUpper);
    if (transfers.Count == 0) return BigInteger.Zero;

    var fromEmitter = transfers
        .Where(t => t.Sender.Equals(emitterAddr, StringComparison.OrdinalIgnoreCase))
        .ToList();
    if (fromEmitter.Count == 0) return BigInteger.Zero;

    var (feeUnits, feePayer) = GetFeeFromTxEventUopt(tr, denomWantedUpper);
    if (fromEmitter.Count == 1 &&
        feeUnits > BigInteger.Zero &&
        !string.IsNullOrWhiteSpace(feePayer) &&
        feePayer.Equals(emitterAddr, StringComparison.OrdinalIgnoreCase) &&
        fromEmitter[0].AmountUnits == feeUnits)
        return BigInteger.Zero;

    BigInteger total = BigInteger.Zero;
    foreach (var entry in fromEmitter)
        total += entry.AmountUnits;

    return total;
}

static BigInteger SumMintedUoptFromCometTx(CometTx tx, string emitterAddr, string denomWantedUpper)
{
    if (!HasMintActionFromCometTx(tx) &&
        !HasMintEvidenceFromCometTx(tx, emitterAddr, denomWantedUpper))
        return BigInteger.Zero;

    var received = SumEmitterReceivesFromCometTx(tx, emitterAddr, denomWantedUpper);
    if (received > BigInteger.Zero)
        return received;

    return SumMintEventAmountsFromCometTx(tx, denomWantedUpper);
}

static BigInteger SumDistributedUoptFromCometTx(CometTx tx, string emitterAddr, string denomWantedUpper)
{
    var transfers = ExtractTransferEntriesFromCometTx(tx, denomWantedUpper);
    if (transfers.Count == 0) return BigInteger.Zero;

    var fromEmitter = transfers
        .Where(t => t.Sender.Equals(emitterAddr, StringComparison.OrdinalIgnoreCase))
        .ToList();
    if (fromEmitter.Count == 0) return BigInteger.Zero;

    var (feeUnits, feePayer) = GetFeeFromCometTxUopt(tx, denomWantedUpper);
    if (fromEmitter.Count == 1 &&
        feeUnits > BigInteger.Zero &&
        !string.IsNullOrWhiteSpace(feePayer) &&
        feePayer.Equals(emitterAddr, StringComparison.OrdinalIgnoreCase) &&
        fromEmitter[0].AmountUnits == feeUnits)
        return BigInteger.Zero;

    BigInteger total = BigInteger.Zero;
    foreach (var entry in fromEmitter)
        total += entry.AmountUnits;

    return total;
}

static bool HasMintActionFromTxResponse(TxResponse tr)
{
    foreach (var ev in tr.Events)
    {
        if (!string.Equals(ev.Type, "message", StringComparison.Ordinal)) continue;
        foreach (var a in ev.Attributes)
        {
            if ((a.Key ?? "") != "action") continue;
            var action = a.Value ?? "";
            if (string.Equals(action, "/optio.distro.MsgMint", StringComparison.OrdinalIgnoreCase) ||
                action.EndsWith(".MsgMint", StringComparison.OrdinalIgnoreCase))
                return true;
        }
    }

    return false;
}

static bool HasMintActionFromCometTx(CometTx tx)
{
    foreach (var ev in tx.Events)
    {
        if (!string.Equals(ev.Type, "message", StringComparison.Ordinal)) continue;
        foreach (var a in ev.Attributes)
        {
            if (a.Key != "action") continue;
            var action = a.Value ?? "";
            if (string.Equals(action, "/optio.distro.MsgMint", StringComparison.OrdinalIgnoreCase) ||
                action.EndsWith(".MsgMint", StringComparison.OrdinalIgnoreCase))
                return true;
        }
    }

    return false;
}

static bool HasMintEvidenceFromTxResponse(TxResponse tr, string emitterAddr, string denomWantedUpper)
{
    bool hasMintEvent = false;
    bool hasMintModule = false;
    bool hasEmitterReceive = false;

    foreach (var ev in tr.Events)
    {
        if (ev.Type == "mint") hasMintEvent = true;

        if (ev.Type == "message")
        {
            foreach (var a in ev.Attributes)
            {
                if ((a.Key ?? "") != "module") continue;
                var module = a.Value ?? "";
                if (module.Contains("mint", StringComparison.OrdinalIgnoreCase) ||
                    module.Contains("distro", StringComparison.OrdinalIgnoreCase))
                    hasMintModule = true;
            }
        }

        if (ev.Type == "coin_received" || ev.Type == "transfer")
        {
            var attrs = new List<(string Key, string Value)>(ev.Attributes.Count);
            foreach (var a in ev.Attributes)
                attrs.Add((a.Key ?? "", a.Value ?? ""));

            if (ev.Type == "coin_received")
            {
                var entries = ParseCoinEventTuplesBig(attrs, "receiver", denomWantedUpper);
                if (entries.Any(e => e.Party.Equals(emitterAddr, StringComparison.OrdinalIgnoreCase) &&
                                     e.AmountUnits > BigInteger.Zero))
                    hasEmitterReceive = true;
            }
            else
            {
                var entries = ParseTransferEventTuplesBig(attrs, denomWantedUpper, out _);
                if (entries.Any(e => e.Recipient.Equals(emitterAddr, StringComparison.OrdinalIgnoreCase) &&
                                     e.AmountUnits > BigInteger.Zero))
                    hasEmitterReceive = true;
            }
        }
    }

    return (hasMintEvent || hasMintModule) && hasEmitterReceive;
}

static bool HasMintEvidenceFromCometTx(CometTx tx, string emitterAddr, string denomWantedUpper)
{
    bool hasMintEvent = false;
    bool hasMintModule = false;
    bool hasEmitterReceive = false;

    foreach (var ev in tx.Events)
    {
        if (ev.Type == "mint") hasMintEvent = true;

        if (ev.Type == "message")
        {
            foreach (var a in ev.Attributes)
            {
                if (a.Key != "module") continue;
                var module = a.Value ?? "";
                if (module.Contains("mint", StringComparison.OrdinalIgnoreCase) ||
                    module.Contains("distro", StringComparison.OrdinalIgnoreCase))
                    hasMintModule = true;
            }
        }

        if (ev.Type == "coin_received" || ev.Type == "transfer")
        {
            var attrs = new List<(string Key, string Value)>(ev.Attributes.Count);
            foreach (var a in ev.Attributes)
                attrs.Add((a.Key ?? "", a.Value ?? ""));

            if (ev.Type == "coin_received")
            {
                var entries = ParseCoinEventTuplesBig(attrs, "receiver", denomWantedUpper);
                if (entries.Any(e => e.Party.Equals(emitterAddr, StringComparison.OrdinalIgnoreCase) &&
                                     e.AmountUnits > BigInteger.Zero))
                    hasEmitterReceive = true;
            }
            else
            {
                var entries = ParseTransferEventTuplesBig(attrs, denomWantedUpper, out _);
                if (entries.Any(e => e.Recipient.Equals(emitterAddr, StringComparison.OrdinalIgnoreCase) &&
                                     e.AmountUnits > BigInteger.Zero))
                    hasEmitterReceive = true;
            }
        }
    }

    return (hasMintEvent || hasMintModule) && hasEmitterReceive;
}

static BigInteger SumEmitterReceivesFromTxResponse(TxResponse tr, string emitterAddr, string denomWantedUpper)
{
    BigInteger total = BigInteger.Zero;

    foreach (var ev in tr.Events)
    {
        if (ev.Type == "transfer")
        {
            var attrs = new List<(string Key, string Value)>(ev.Attributes.Count);
            foreach (var a in ev.Attributes)
                attrs.Add((a.Key ?? "", a.Value ?? ""));

            var entries = ParseTransferEventTuplesBig(attrs, denomWantedUpper, out _);
            foreach (var e in entries)
            {
                if (e.Recipient.Equals(emitterAddr, StringComparison.OrdinalIgnoreCase))
                    total += e.AmountUnits;
            }
        }
        else if (ev.Type == "coin_received")
        {
            var attrs = new List<(string Key, string Value)>(ev.Attributes.Count);
            foreach (var a in ev.Attributes)
                attrs.Add((a.Key ?? "", a.Value ?? ""));

            var entries = ParseCoinEventTuplesBig(attrs, "receiver", denomWantedUpper);
            foreach (var e in entries)
            {
                if (e.Party.Equals(emitterAddr, StringComparison.OrdinalIgnoreCase))
                    total += e.AmountUnits;
            }
        }
    }

    return total;
}

static BigInteger SumEmitterReceivesFromCometTx(CometTx tx, string emitterAddr, string denomWantedUpper)
{
    BigInteger total = BigInteger.Zero;

    foreach (var ev in tx.Events)
    {
        if (ev.Type == "transfer")
        {
            var attrs = new List<(string Key, string Value)>(ev.Attributes.Count);
            foreach (var a in ev.Attributes)
                attrs.Add((a.Key ?? "", a.Value ?? ""));

            var entries = ParseTransferEventTuplesBig(attrs, denomWantedUpper, out _);
            foreach (var e in entries)
            {
                if (e.Recipient.Equals(emitterAddr, StringComparison.OrdinalIgnoreCase))
                    total += e.AmountUnits;
            }
        }
        else if (ev.Type == "coin_received")
        {
            var attrs = new List<(string Key, string Value)>(ev.Attributes.Count);
            foreach (var a in ev.Attributes)
                attrs.Add((a.Key ?? "", a.Value ?? ""));

            var entries = ParseCoinEventTuplesBig(attrs, "receiver", denomWantedUpper);
            foreach (var e in entries)
            {
                if (e.Party.Equals(emitterAddr, StringComparison.OrdinalIgnoreCase))
                    total += e.AmountUnits;
            }
        }
    }

    return total;
}

static BigInteger SumMintEventAmountsFromTxResponse(TxResponse tr, string denomWantedUpper)
{
    BigInteger total = BigInteger.Zero;

    foreach (var ev in tr.Events)
    {
        if (ev.Type != "mint") continue;
        foreach (var a in ev.Attributes)
        {
            if ((a.Key ?? "") == "amount")
                total += ParseAmountFieldUopt(a.Value ?? "", denomWantedUpper);
        }
    }

    return total;
}

static BigInteger SumMintEventAmountsFromCometTx(CometTx tx, string denomWantedUpper)
{
    BigInteger total = BigInteger.Zero;

    foreach (var ev in tx.Events)
    {
        if (ev.Type != "mint") continue;
        foreach (var a in ev.Attributes)
        {
            if (a.Key == "amount")
                total += ParseAmountFieldUopt(a.Value ?? "", denomWantedUpper);
        }
    }

    return total;
}

static List<TransferEntryBig> ExtractTransferEntriesFromTxResponse(TxResponse tr, string denomWantedUpper)
{
    var entries = new List<TransferEntryBig>();

    foreach (var ev in tr.Events)
    {
        if (!string.Equals(ev.Type, "transfer", StringComparison.Ordinal)) continue;

        var attrs = new List<(string Key, string Value)>(ev.Attributes.Count);
        foreach (var a in ev.Attributes)
            attrs.Add((a.Key ?? "", a.Value ?? ""));

        entries.AddRange(ParseTransferEventTuplesBig(attrs, denomWantedUpper, out _));
    }

    return entries;
}

static List<TransferEntryBig> ExtractTransferEntriesFromCometTx(CometTx tx, string denomWantedUpper)
{
    var entries = new List<TransferEntryBig>();

    foreach (var ev in tx.Events)
    {
        if (!string.Equals(ev.Type, "transfer", StringComparison.Ordinal)) continue;

        var attrs = new List<(string Key, string Value)>(ev.Attributes.Count);
        foreach (var a in ev.Attributes)
            attrs.Add((a.Key ?? "", a.Value ?? ""));

        entries.AddRange(ParseTransferEventTuplesBig(attrs, denomWantedUpper, out _));
    }

    return entries;
}

static (decimal FeeUnits, string? FeePayer) GetFeeFromTxEvent(TxResponse tr, string denomWantedUpper)
{
    decimal feeUnits = 0m;
    string? feePayer = null;

    foreach (var ev in tr.Events)
    {
        if (!string.Equals(ev.Type, "tx", StringComparison.Ordinal)) continue;

        foreach (var a in ev.Attributes)
        {
            var k = a.Key ?? "";
            var v = a.Value ?? "";

            if (k == "fee") feeUnits += ParseEventAmountUnits(v, denomWantedUpper);
            else if (k == "fee_payer") feePayer = v;
        }
    }

    return (feeUnits, feePayer);
}

static (BigInteger FeeUnits, string? FeePayer) GetFeeFromTxEventUopt(TxResponse tr, string denomWantedUpper)
{
    BigInteger feeUnits = BigInteger.Zero;
    string? feePayer = null;

    foreach (var ev in tr.Events)
    {
        if (!string.Equals(ev.Type, "tx", StringComparison.Ordinal)) continue;

        foreach (var a in ev.Attributes)
        {
            var k = a.Key ?? "";
            var v = a.Value ?? "";

            if (k == "fee") feeUnits += ParseAmountFieldUopt(v, denomWantedUpper);
            else if (k == "fee_payer") feePayer = v;
        }
    }

    return (feeUnits, feePayer);
}

static (BigInteger FeeUnits, string? FeePayer) GetFeeFromCometTxUopt(CometTx comet, string denomWantedUpper)
{
    BigInteger feeUnits = BigInteger.Zero;
    string? feePayer = null;

    foreach (var ev in comet.Events)
    {
        if (!string.Equals(ev.Type, "tx", StringComparison.Ordinal)) continue;

        foreach (var a in ev.Attributes)
        {
            if (a.Key == "fee") feeUnits += ParseAmountFieldUopt(a.Value ?? "", denomWantedUpper);
            else if (a.Key == "fee_payer") feePayer = a.Value;
        }
    }

    return (feeUnits, feePayer);
}

static (BigInteger FeeUnits, string? FeePayer) GetFeeFromEventsUopt(List<CometEvent> events, string denomWantedUpper)
{
    BigInteger feeUnits = BigInteger.Zero;
    string? feePayer = null;

    foreach (var ev in events)
    {
        if (!string.Equals(ev.Type, "tx", StringComparison.Ordinal)) continue;

        foreach (var a in ev.Attributes)
        {
            if (a.Key == "fee") feeUnits += ParseAmountFieldUopt(a.Value ?? "", denomWantedUpper);
            else if (a.Key == "fee_payer") feePayer = a.Value;
        }
    }

    return (feeUnits, feePayer);
}

static decimal ParseEventAmountUnits(string amountField, string denomWantedUpper)
{
    decimal total = 0m;

    foreach (var part in amountField.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        int i = 0;
        while (i < part.Length && char.IsDigit(part[i])) i++;
        if (i == 0 || i == part.Length) continue;

        var numStr = part[..i];
        var denom = part[i..];

        if (!string.Equals(denom.ToUpperInvariant(), denomWantedUpper, StringComparison.Ordinal))
            continue;

        if (decimal.TryParse(numStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            total += n;
    }

    return total;
}

static BigInteger ParseAmountFieldUopt(string amountField, string denomWantedUpper)
{
    if (string.IsNullOrWhiteSpace(amountField)) return BigInteger.Zero;

    BigInteger total = BigInteger.Zero;
    var parts = amountField.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    foreach (var part in parts)
    {
        if (TryParseSingleCoinUopt(part, denomWantedUpper, out var amountUnits))
            total += amountUnits;
    }

    return total;
}

static bool TryParseSingleCoinUopt(string part, string denomWantedUpper, out BigInteger amountUnits)
{
    amountUnits = BigInteger.Zero;
    if (string.IsNullOrWhiteSpace(part)) return false;

    var trimmed = part.Trim();
    int i = 0;
    if (trimmed.Length == 0) return false;

    if (trimmed[0] == '-')
        i = 1;

    for (; i < trimmed.Length; i++)
    {
        if (!char.IsDigit(trimmed[i])) break;
    }

    if (i == 0 || i >= trimmed.Length) return false;

    var numStr = trimmed[..i].Replace("_", "");
    var denom = trimmed[i..];
    if (!string.Equals(denom.ToUpperInvariant(), denomWantedUpper, StringComparison.Ordinal))
        return false;

    if (BigInteger.TryParse(numStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
    {
        amountUnits = parsed;
        return true;
    }

    return false;
}

static List<TransferEntryBig> ParseTransferEventTuplesBig(
    List<(string Key, string Value)> attrs,
    string denomWantedUpper,
    out bool hasMsgIndex)
{
    var entries = new List<TransferEntryBig>();
    hasMsgIndex = false;

    var senders = new List<string>();
    var recipients = new List<string>();
    var amountStrings = new List<string>();

    foreach (var (key, value) in attrs)
    {
        if (key == "msg_index")
        {
            hasMsgIndex = true;
            continue;
        }

        if (key == "sender")
        {
            if (!string.IsNullOrWhiteSpace(value)) senders.Add(value);
            continue;
        }

        if (key == "recipient")
        {
            if (!string.IsNullOrWhiteSpace(value)) recipients.Add(value);
            continue;
        }

        if (key == "amount")
        {
            if (!string.IsNullOrWhiteSpace(value)) amountStrings.Add(value);
        }
    }

    if (amountStrings.Count == 0 || recipients.Count == 0) return entries;

    int count = Math.Min(amountStrings.Count, recipients.Count);
    string? singleSender = senders.Count == 1 ? senders[0] : null;

    for (int i = 0; i < count; i++)
    {
        var sender = singleSender ?? (senders.Count > i ? senders[i] : null);
        var recipient = recipients[i];
        if (string.IsNullOrWhiteSpace(sender) || string.IsNullOrWhiteSpace(recipient)) continue;

        var amountUnits = ParseAmountFieldUopt(amountStrings[i], denomWantedUpper);
        if (amountUnits <= BigInteger.Zero) continue;

        entries.Add(new TransferEntryBig(sender, recipient, amountUnits));
    }

    return entries;
}

static List<(string Party, BigInteger AmountUnits)> ParseCoinEventTuplesBig(
    List<(string Key, string Value)> attrs,
    string partyKey,
    string denomWantedUpper)
{
    var entries = new List<(string Party, BigInteger AmountUnits)>();
    var parties = new List<string>();
    var amountStrings = new List<string>();

    foreach (var (key, value) in attrs)
    {
        if (key == partyKey)
        {
            if (!string.IsNullOrWhiteSpace(value)) parties.Add(value);
            continue;
        }

        if (key == "amount")
        {
            if (!string.IsNullOrWhiteSpace(value)) amountStrings.Add(value);
        }
    }

    if (amountStrings.Count == 0 || parties.Count == 0) return entries;

    int count = Math.Min(amountStrings.Count, parties.Count);
    string? singleParty = parties.Count == 1 ? parties[0] : null;

    for (int i = 0; i < count; i++)
    {
        var party = singleParty ?? (parties.Count > i ? parties[i] : null);
        if (string.IsNullOrWhiteSpace(party)) continue;

        var amountUnits = ParseAmountFieldUopt(amountStrings[i], denomWantedUpper);
        if (amountUnits <= BigInteger.Zero) continue;

        entries.Add((party, amountUnits));
    }

    return entries;
}

static string FormatOptFromUopt(BigInteger uopt, int scale)
{
    if (scale <= 0) return uopt.ToString(CultureInfo.InvariantCulture);

    var sign = uopt.Sign < 0 ? "-" : "";
    var abs = BigInteger.Abs(uopt);
    var scaleBig = new BigInteger(scale);
    var whole = BigInteger.DivRem(abs, scaleBig, out var frac);
    var fracStr = frac.ToString(CultureInfo.InvariantCulture).PadLeft(6, '0');
    return $"{sign}{whole.ToString(CultureInfo.InvariantCulture)}.{fracStr}";
}

static string FormatDurationShort(TimeSpan duration)
{
    if (duration < TimeSpan.Zero)
        duration = TimeSpan.Zero;

    var days = (int)duration.TotalDays;
    var hours = duration.Hours;
    var minutes = duration.Minutes;

    var parts = new List<string>(3);
    if (days > 0)
        parts.Add($"{days}d");
    if (hours > 0 || days > 0)
        parts.Add($"{hours}h");
    parts.Add($"{minutes}m");

    return string.Join(" ", parts);
}

static Dictionary<int, decimal> SummarizeLockBuckets(List<LockRow> lockRows, DateTimeOffset nowUtc)
{
    var buckets = new Dictionary<int, decimal>
    {
        [6] = 0m,
        [12] = 0m,
        [18] = 0m,
        [24] = 0m,
        [0] = 0m, // other
    };

    foreach (var row in lockRows)
    {
        var months = GetRemainingMonths(row.EndTime, nowUtc);
        int bucket;
        if (months <= 6) bucket = 6;
        else if (months <= 12) bucket = 12;
        else if (months <= 18) bucket = 18;
        else if (months <= 24) bucket = 24;
        else bucket = 0;

        buckets[bucket] += row.AmountOpt;
    }

    return buckets;
}

static int GetRemainingMonths(DateTimeOffset endTime, DateTimeOffset nowUtc)
{
    if (endTime <= nowUtc) return 0;

    var months = (endTime.Year - nowUtc.Year) * 12 + endTime.Month - nowUtc.Month;
    if (endTime.Day < nowUtc.Day)
        months--;

    return Math.Max(0, months);
}

static void PrintTotals(BigInteger emittedUopt, BigInteger distributedUopt, int scale)
{
    Console.WriteLine();
    Console.WriteLine("== Totals ==");
    Console.WriteLine($"Total Distributed: {FormatOptFromUopt(distributedUopt, scale)} OPT ({distributedUopt} uOPT)");
}

static void RunTotalsDryRun(string emitterAddr, string denomWantedUpper, int scale)
{
    var txs = new List<CometTx>
    {
        new CometTx(
            base64Tx: null,
            events: new List<CometEvent>
            {
                new("message", new List<CometAttr>
                {
                    new("action", "/optio.distro.MsgMint"),
                    new("module", "distro"),
                    new("msg_index", "0"),
                }),
                new("coin_received", new List<CometAttr>
                {
                    new("receiver", emitterAddr),
                    new("amount", "1000000uOPT"),
                }),
            },
            height: 1),
        new CometTx(
            base64Tx: null,
            events: new List<CometEvent>
            {
                new("message", new List<CometAttr>
                {
                    new("action", "/cosmos.bank.v1beta1.MsgMultiSend"),
                    new("module", "bank"),
                    new("msg_index", "0"),
                }),
                new("tx", new List<CometAttr>
                {
                    new("fee", "500uOPT"),
                    new("fee_payer", emitterAddr),
                }),
                new("transfer", new List<CometAttr>
                {
                    new("sender", emitterAddr),
                    new("recipient", "optio1recipient"),
                    new("amount", "2000000uOPT"),
                    new("msg_index", "0"),
                }),
            },
            height: 2),
        new CometTx(
            base64Tx: null,
            events: new List<CometEvent>
            {
                new("tx", new List<CometAttr>
                {
                    new("fee", "600uOPT"),
                    new("fee_payer", emitterAddr),
                }),
                new("transfer", new List<CometAttr>
                {
                    new("sender", emitterAddr),
                    new("recipient", "optio1fee"),
                    new("amount", "600uOPT"),
                }),
            },
            height: 3),
    };

    BigInteger emitted = BigInteger.Zero;
    BigInteger distributed = BigInteger.Zero;
    foreach (var tx in txs)
        AccumulateTotalsFromCometTx(tx, emitterAddr, denomWantedUpper, ref emitted, ref distributed);

    Console.WriteLine("DRY RUN: totals accumulator");
    PrintTotals(emitted, distributed, scale);
}

// ---------------- Debug event dump ----------------

static void DumpTxEventsForDebug(TxResponse tr)
{
    Console.WriteLine($"DEBUG events for {tr.Txhash} @ {tr.Timestamp}");
    foreach (var ev in tr.Events)
    {
        Console.WriteLine($"  event: {ev.Type}");
        foreach (var a in ev.Attributes)
            Console.WriteLine($"    {a.Key} = {a.Value}");
    }
}

static void DumpCometTxEventsForDebug(CometTx tx, string txHash)
{
    Console.WriteLine($"DEBUG comet events for {txHash}");
    foreach (var ev in tx.Events)
    {
        Console.WriteLine($"  event: {ev.Type}");
        foreach (var a in ev.Attributes)
            Console.WriteLine($"    {a.Key} = {a.Value}");
    }
}

static string? GetCometAttrValue(List<CometAttr> attrs, string key)
{
    foreach (var attr in attrs)
    {
        var decodedKey = DecodeBase64Maybe(attr.Key);
        if (string.Equals(decodedKey, key, StringComparison.OrdinalIgnoreCase))
            return DecodeBase64Maybe(attr.Value);
    }

    return null;
}

// ---------------- Config + utils ----------------

static Dictionary<string, string> LoadConfig(string path)
{
    var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var line in File.ReadAllLines(path))
    {
        var t = line.Trim();
        if (t.Length == 0 || t.StartsWith("#")) continue;

        var idx = t.IndexOf('=');
        if (idx <= 0) continue;

        dict[t[..idx].Trim()] = t[(idx + 1)..].Trim();
    }
    return dict;
}

static string TruncateMiddle(string s, int maxLen)
{
    if (string.IsNullOrEmpty(s) || s.Length <= maxLen) return s;
    var keep = (maxLen - 3) / 2;
    return s[..keep] + "..." + s[^keep..];
}

// ======================================================================
// TYPES
// ======================================================================

sealed class BalancesSummary
{
    public decimal? BankBalanceUnits { get; set; }
    public decimal? SpendableBalanceUnits { get; set; }

    public decimal? DelegatedUnits { get; set; }
    public decimal? UnbondingUnits { get; set; }
    public decimal? RedelegatingUnits { get; set; }

    public decimal? GovDepositUnits { get; set; }

    public bool IsVestingAccount { get; set; }
    public decimal? VestingLockedUnits { get; set; }

    public decimal? LockedTotalUnits { get; set; }
}

sealed class WalletBalance
{
    public string Address { get; set; } = "";
    public decimal WalletBalanceOPT { get; set; }
    public decimal StakedOPT { get; set; }
    public decimal UnbondingOPT { get; set; }

    public decimal TotalOPT => WalletBalanceOPT + StakedOPT + UnbondingOPT;
}

sealed class CmcDailyRow
{
    public DateOnly Date { get; set; }
    public decimal? PriceUsd { get; set; }
    public decimal? Vol24hUsd { get; set; }
    public long LastUnixTs { get; set; }
}

sealed class MultiSendRow
{
    public MultiSendRow(string fromAddress, string toAddress, long height, BigInteger amountUopt, decimal amountOpt, DateTime? timeUtc)
    {
        FromAddress = fromAddress;
        ToAddress = toAddress;
        Height = height;
        AmountUopt = amountUopt;
        AmountOpt = amountOpt;
        TimeUtc = timeUtc;
        TxHash = "";
    }

    public string TxHash { get; set; }
    public string FromAddress { get; }
    public string ToAddress { get; }
    public long Height { get; }
    public BigInteger AmountUopt { get; }
    public decimal AmountOpt { get; }
    public DateTime? TimeUtc { get; }

    public string TxHashShort => TxHash.Length > 12 ? TxHash[..12] : TxHash;
}

sealed class LockExtendedRow
{
    public string TxHash { get; set; } = "";
    public long Height { get; set; }
    public string Address { get; set; } = "";
    public decimal AmountOpt { get; set; }
    public string OldUnlockDate { get; set; } = "";
    public string UnlockDate { get; set; } = "";
    public DateTime? TxTimeUtc { get; set; }
}

// Minimal CometBFT /tx representation for event parsing (no LCD).
sealed class CometTx
{
    public CometTx(string? base64Tx, List<CometEvent> events, long height)
    {
        Base64Tx = base64Tx;
        Events = events;
        Height = height;
    }

    public string? Base64Tx { get; }
    public List<CometEvent> Events { get; }
    public long Height { get; }
}

readonly record struct CometEvent(string Type, List<CometAttr> Attributes);
readonly record struct CometAttr(string Key, string Value);
readonly record struct CometTxSearchHit(string Hash, long Height);
readonly record struct TransferEntry(string Sender, string Recipient, decimal AmountUnits);
readonly record struct TransferEntryBig(string Sender, string Recipient, BigInteger AmountUnits);

sealed class CometStatus
{
    public bool? CatchingUp { get; set; }
    public long? LatestBlockHeight { get; set; }
    public DateTimeOffset? LatestBlockTime { get; set; }
    public string? Moniker { get; set; }
    public string? Network { get; set; }
}

sealed class CometPeerInfo
{
    public string Moniker { get; set; } = "";
    public string RemoteIp { get; set; } = "";
    public string ListenAddr { get; set; } = "";
    public string NodeId { get; set; } = "";
}

public sealed class LedgerRow
{
    public LedgerRow(DateTime timeUtc, string direction, string from, string to, decimal amountUnits, string txHash, decimal feeUnits, bool feeApplied)
    {
        TimeUtc = timeUtc;
        Direction = direction;
        From = from;
        To = to;
        AmountInDenomUnits = amountUnits;
        TxHash = txHash;
        FeeInDenomUnits = feeUnits;
        FeeApplied = feeApplied;
        Label = "";
        Memo = "";
    }

    public DateTime TimeUtc { get; set; }
    public string Direction { get; set; }
    public string From { get; set; }
    public string To { get; set; }
    public decimal AmountInDenomUnits { get; set; }
    public string TxHash { get; set; }
    public decimal FeeInDenomUnits { get; set; }
    public bool FeeApplied { get; set; }
    public string Label { get; set; }
    public string Memo { get; set; }
    public bool IsFeeTransfer { get; set; }
}

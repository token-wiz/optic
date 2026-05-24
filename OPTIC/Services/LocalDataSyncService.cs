using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace OPTIC.Services;

public class LocalDataSyncService
{
    private const int SummaryCacheHours = 12;
    private readonly string _odataPath;
    private readonly string _dbPath;
    private readonly string _syncLockFile;
    private readonly CancellationTokenSource _cancellationTokenSource;

    public LocalDataSyncService()
    {
        var workingDir = Environment.CurrentDirectory;
        _odataPath = Path.Combine(workingDir, "odata");
        _dbPath = Path.Combine(_odataPath, "optic.db");
        _syncLockFile = Path.Combine(_odataPath, ".sync.lock");
        _cancellationTokenSource = new CancellationTokenSource();
    }

    public async Task InitializeAsync()
    {
        // Create odata directory if it doesn't exist
        if (!Directory.Exists(_odataPath))
        {
            Directory.CreateDirectory(_odataPath);
            Console.WriteLine($"Created odata directory at: {_odataPath}");
        }

        // Initialize database schema
        await InitializeDatabaseAsync();
        Console.WriteLine($"Local data sync initialized at: {_dbPath}");
    }

    public Task StartSyncJobAsync(TimeSpan interval)
    {
        var task = Task.Run(async () =>
        {
            while (!_cancellationTokenSource.Token.IsCancellationRequested)
            {
                try
                {
                    await SyncDataAsync();
                    await Task.Delay(interval, _cancellationTokenSource.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error during data sync: {ex.Message}");
                    await Task.Delay(interval, _cancellationTokenSource.Token);
                }
            }
        });

        // Don't await - let it run in the background
        _ = task;
        return Task.CompletedTask;
    }

    public void Stop()
    {
        _cancellationTokenSource.Cancel();
    }

    private async Task InitializeDatabaseAsync()
    {
        using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await connection.OpenAsync();

            var command = connection.CreateCommand();
            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS SyncMetadata (
                    Id INTEGER PRIMARY KEY,
                    Key TEXT UNIQUE NOT NULL,
                    Value TEXT NOT NULL,
                    LastUpdated DATETIME DEFAULT CURRENT_TIMESTAMP
                );

                CREATE TABLE IF NOT EXISTS Distributions (
                    Id INTEGER PRIMARY KEY,
                    Address TEXT NOT NULL,
                    Amount DECIMAL NOT NULL,
                    Denom TEXT NOT NULL,
                    Timestamp DATETIME DEFAULT CURRENT_TIMESTAMP,
                    UNIQUE(Address, Denom)
                );

                CREATE TABLE IF NOT EXISTS Locks (
                    Id INTEGER PRIMARY KEY,
                    Address TEXT NOT NULL,
                    Amount DECIMAL NOT NULL,
                    Denom TEXT NOT NULL,
                    UnlockTime DATETIME,
                    Timestamp DATETIME DEFAULT CURRENT_TIMESTAMP,
                    UNIQUE(Address, Denom)
                );

                CREATE TABLE IF NOT EXISTS NetworkStats (
                    Id INTEGER PRIMARY KEY,
                    StatKey TEXT UNIQUE NOT NULL,
                    StatValue TEXT NOT NULL,
                    Timestamp DATETIME DEFAULT CURRENT_TIMESTAMP
                );

                CREATE TABLE IF NOT EXISTS SyncLog (
                    Id INTEGER PRIMARY KEY,
                    SyncTime DATETIME DEFAULT CURRENT_TIMESTAMP,
                    Status TEXT NOT NULL,
                    Message TEXT,
                    DurationMs INT
                );

                CREATE TABLE IF NOT EXISTS DailyStats (
                    Date TEXT PRIMARY KEY,
                    TotalWallets INTEGER,
                    ActiveWallets INTEGER,
                    TotalSupply DECIMAL,
                    TotalStaked DECIMAL,
                    TotalLocked DECIMAL,
                    TotalUnbonding DECIMAL,
                    TotalLiquid DECIMAL,
                    TotalLiquidPlus DECIMAL,
                    DistributedOpt DECIMAL,
                    EmittedOpt DECIMAL,
                    NetEmittedOpt DECIMAL,
                    Lock6m INTEGER,
                    Lock12m INTEGER,
                    Lock18m INTEGER,
                    Lock24m INTEGER,
                    LockOther INTEGER,
                    TxCount INTEGER,
                    SentOpt DECIMAL,
                    RecvOpt DECIMAL,
                    UniqueCounterparties INTEGER,
                    StartBlockNumber INTEGER,
                    EndBlockNumber INTEGER,
                    LastUpdated DATETIME DEFAULT CURRENT_TIMESTAMP
                );

                CREATE TABLE IF NOT EXISTS SummaryCache (
                    CacheKey TEXT PRIMARY KEY,
                    TotalWallets INTEGER,
                    TotalLiquid DECIMAL,
                    TotalStaked DECIMAL,
                    TotalLocked DECIMAL,
                    TotalDistributed DECIMAL,
                    StatsDate TEXT,
                    LastUpdated DATETIME DEFAULT CURRENT_TIMESTAMP
                );

                CREATE INDEX IF NOT EXISTS idx_distributions_address ON Distributions(Address);
                CREATE INDEX IF NOT EXISTS idx_locks_address ON Locks(Address);
                CREATE INDEX IF NOT EXISTS idx_sync_log_time ON SyncLog(SyncTime DESC);
                CREATE INDEX IF NOT EXISTS idx_daily_stats_date ON DailyStats(Date DESC);
            ";

            await command.ExecuteNonQueryAsync();
        }
    }

    private async Task SyncDataAsync()
    {
        var syncStart = DateTime.UtcNow;

        try
        {
            using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
            {
                await connection.OpenAsync();

                // Update sync metadata
                var command = connection.CreateCommand();
                command.CommandText = @"
                    INSERT OR REPLACE INTO SyncMetadata (Key, Value, LastUpdated)
                    VALUES ('lastSync', @lastSync, CURRENT_TIMESTAMP)
                ";
                command.Parameters.AddWithValue("@lastSync", DateTime.UtcNow.ToString("O"));
                await command.ExecuteNonQueryAsync();

                // Log successful sync
                var duration = (int)(DateTime.UtcNow - syncStart).TotalMilliseconds;
                var logCommand = connection.CreateCommand();
                logCommand.CommandText = @"
                    INSERT INTO SyncLog (Status, Message, DurationMs)
                    VALUES ('Success', @message, @duration)
                ";
                logCommand.Parameters.AddWithValue("@message", $"Data sync completed successfully");
                logCommand.Parameters.AddWithValue("@duration", duration);
                await logCommand.ExecuteNonQueryAsync();
            }
        }
        catch (Exception ex)
        {
            // Log failed sync
            try
            {
                using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
                {
                    await connection.OpenAsync();
                    var logCommand = connection.CreateCommand();
                    logCommand.CommandText = @"
                        INSERT INTO SyncLog (Status, Message, DurationMs)
                        VALUES ('Error', @message, @duration)
                    ";
                    var duration = (int)(DateTime.UtcNow - syncStart).TotalMilliseconds;
                    logCommand.Parameters.AddWithValue("@message", ex.Message);
                    logCommand.Parameters.AddWithValue("@duration", duration);
                    await logCommand.ExecuteNonQueryAsync();
                }
            }
            catch
            {
                // Silently fail if we can't log the error
            }

            throw;
        }
    }

    public async Task<Dictionary<string, object>> GetSyncStatusAsync()
    {
        using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await connection.OpenAsync();

            var status = new Dictionary<string, object>();

            // Get last sync time
            var command = connection.CreateCommand();
            command.CommandText = "SELECT Value FROM SyncMetadata WHERE Key = 'lastSync' LIMIT 1";
            var lastSync = await command.ExecuteScalarAsync();
            status["lastSync"] = lastSync?.ToString() ?? "Never";

            // Get sync log count
            command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM SyncLog";
            var logCount = await command.ExecuteScalarAsync();
            status["syncLogCount"] = logCount ?? 0;

            // Get distribution count
            command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM Distributions";
            var distCount = await command.ExecuteScalarAsync();
            status["distributionCount"] = distCount ?? 0;

            // Get lock count
            command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM Locks";
            var lockCount = await command.ExecuteScalarAsync();
            status["lockCount"] = lockCount ?? 0;

            // Get database file size
            var dbFile = new FileInfo(_dbPath);
            status["dbSizeBytes"] = dbFile.Exists ? dbFile.Length : 0;
            status["dbPath"] = _dbPath;
            status["odataPath"] = _odataPath;

            return status;
        }
    }

    public async Task<List<SyncLogEntry>> GetRecentSyncLogsAsync(int limit = 10)
    {
        var logs = new List<SyncLogEntry>();

        using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await connection.OpenAsync();

            var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT Id, SyncTime, Status, Message, DurationMs
                FROM SyncLog
                ORDER BY SyncTime DESC
                LIMIT @limit
            ";
            command.Parameters.AddWithValue("@limit", limit);

            using (var reader = await command.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    logs.Add(new SyncLogEntry
                    {
                        Id = reader.GetInt32(0),
                        SyncTime = reader.GetDateTime(1),
                        Status = reader.GetString(2),
                        Message = reader.IsDBNull(3) ? null : reader.GetString(3),
                        DurationMs = reader.IsDBNull(4) ? 0 : reader.GetInt32(4)
                    });
                }
            }
        }

        return logs;
    }

    public async Task<(bool success, string message)> RecordDailyStatsAsync(DailyStatsEntry stats)
    {
        try
        {
            await InsertDailyStatsAsync(stats);
            return (true, $"Daily stats recorded for {stats.Date}");
        }
        catch (Exception ex)
        {
            return (false, $"Failed to record daily stats: {ex.Message}");
        }
    }

    public async Task<(int created, int skipped, List<string> messages)> BackfillDailyStatsAsync(
        Func<string, Task<DailyStatsEntry?>> computeStatsFunc, 
        bool force = false)
    {
        var created = 0;
        var skipped = 0;
        var messages = new List<string>();

        try
        {
            var today = DateTime.UtcNow.Date;
            var startDate = await GetFirstTransactionDateAsync();
            
            if (string.IsNullOrEmpty(startDate))
            {
                messages.Add("No transaction data found to backfill from");
                return (created, skipped, messages);
            }

            var currentDate = DateTime.ParseExact(startDate, "yyyy-MM-dd", null);
            var endDate = today;

            while (currentDate <= endDate)
            {
                var dateStr = currentDate.ToString("yyyy-MM-dd");
                var exists = await DailyStatsExistsAsync(dateStr);

                if (exists && !force)
                {
                    skipped++;
                    messages.Add($"Skipped {dateStr} (already exists)");
                }
                else
                {
                    var stats = await computeStatsFunc(dateStr);
                    if (stats != null)
                    {
                        await InsertDailyStatsAsync(stats);
                        created++;
                        messages.Add($"Created stats for {dateStr}");
                    }
                    else
                    {
                        messages.Add($"Failed to compute stats for {dateStr}");
                    }
                }

                currentDate = currentDate.AddDays(1);
            }
        }
        catch (Exception ex)
        {
            messages.Add($"Backfill error: {ex.Message}");
        }

        return (created, skipped, messages);
    }

    public async Task<bool> DailyStatsExistsAsync(string date)
    {
        using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM DailyStats WHERE Date = @date";
            command.Parameters.AddWithValue("@date", date);
            var result = await command.ExecuteScalarAsync();
            return (long?)result > 0;
        }
    }

    public async Task InsertDailyStatsAsync(DailyStatsEntry stats)
    {
        using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = @"
                INSERT OR REPLACE INTO DailyStats (
                    Date, TotalWallets, ActiveWallets, TotalSupply, TotalStaked, TotalLocked,
                    TotalUnbonding, TotalLiquid, TotalLiquidPlus, DistributedOpt, EmittedOpt, NetEmittedOpt,
                    Lock6m, Lock12m, Lock18m, Lock24m, LockOther, TxCount, SentOpt, RecvOpt,
                    UniqueCounterparties, StartBlockNumber, EndBlockNumber, LastUpdated
                ) VALUES (
                    @date, @totalWallets, @activeWallets, @totalSupply, @totalStaked, @totalLocked,
                    @totalUnbonding, @totalLiquid, @totalLiquidPlus, @distributedOpt, @emittedOpt, @netEmittedOpt,
                    @lock6m, @lock12m, @lock18m, @lock24m, @lockOther, @txCount, @sentOpt, @recvOpt,
                    @uniqueCounterparties, @startBlockNumber, @endBlockNumber, CURRENT_TIMESTAMP
                )
            ";

            command.Parameters.AddWithValue("@date", stats.Date);
            command.Parameters.AddWithValue("@totalWallets", stats.TotalWallets ?? 0);
            command.Parameters.AddWithValue("@activeWallets", stats.ActiveWallets ?? 0);
            command.Parameters.AddWithValue("@totalSupply", stats.TotalSupply ?? 0m);
            command.Parameters.AddWithValue("@totalStaked", stats.TotalStaked ?? 0m);
            command.Parameters.AddWithValue("@totalLocked", stats.TotalLocked ?? 0m);
            command.Parameters.AddWithValue("@totalUnbonding", stats.TotalUnbonding ?? 0m);
            command.Parameters.AddWithValue("@totalLiquid", stats.TotalLiquid ?? 0m);
            command.Parameters.AddWithValue("@totalLiquidPlus", stats.TotalLiquidPlus ?? 0m);
            command.Parameters.AddWithValue("@distributedOpt", stats.DistributedOpt ?? 0m);
            command.Parameters.AddWithValue("@emittedOpt", stats.EmittedOpt ?? 0m);
            command.Parameters.AddWithValue("@netEmittedOpt", stats.NetEmittedOpt ?? 0m);
            command.Parameters.AddWithValue("@lock6m", stats.Lock6m ?? 0);
            command.Parameters.AddWithValue("@lock12m", stats.Lock12m ?? 0);
            command.Parameters.AddWithValue("@lock18m", stats.Lock18m ?? 0);
            command.Parameters.AddWithValue("@lock24m", stats.Lock24m ?? 0);
            command.Parameters.AddWithValue("@lockOther", stats.LockOther ?? 0);
            command.Parameters.AddWithValue("@txCount", stats.TxCount ?? 0);
            command.Parameters.AddWithValue("@sentOpt", stats.SentOpt ?? 0m);
            command.Parameters.AddWithValue("@recvOpt", stats.RecvOpt ?? 0m);
            command.Parameters.AddWithValue("@uniqueCounterparties", stats.UniqueCounterparties ?? 0);
            command.Parameters.AddWithValue("@startBlockNumber", stats.StartBlockNumber ?? 0);
            command.Parameters.AddWithValue("@endBlockNumber", stats.EndBlockNumber ?? 0);

            await command.ExecuteNonQueryAsync();
        }
    }

    public async Task<List<DailyStatsEntry>> GetDailyStatsAsync(int days = 30)
    {
        var stats = new List<DailyStatsEntry>();

        using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT Date, TotalWallets, ActiveWallets, TotalSupply, TotalStaked, TotalLocked,
                       TotalUnbonding, TotalLiquid, TotalLiquidPlus, DistributedOpt, EmittedOpt, NetEmittedOpt,
                       Lock6m, Lock12m, Lock18m, Lock24m, LockOther, TxCount, SentOpt, RecvOpt,
                       UniqueCounterparties, StartBlockNumber, EndBlockNumber,
                       SUM(DistributedOpt) OVER (ORDER BY Date ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS TotalDistributed
                FROM DailyStats
                ORDER BY Date DESC
                LIMIT @days
            ";
            command.Parameters.AddWithValue("@days", days);

            using (var reader = await command.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    stats.Add(new DailyStatsEntry
                    {
                        Date = reader.GetString(0),
                        TotalWallets = reader.IsDBNull(1) ? null : reader.GetInt32(1),
                        ActiveWallets = reader.IsDBNull(2) ? null : reader.GetInt32(2),
                        TotalSupply = reader.IsDBNull(3) ? null : reader.GetDecimal(3) / 1_000_000m,
                        TotalStaked = reader.IsDBNull(4) ? null : reader.GetDecimal(4) / 1_000_000m,
                        TotalLocked = reader.IsDBNull(5) ? null : reader.GetDecimal(5) / 1_000_000m,
                        TotalUnbonding = reader.IsDBNull(6) ? null : reader.GetDecimal(6) / 1_000_000m,
                        TotalLiquid = reader.IsDBNull(7) ? null : reader.GetDecimal(7) / 1_000_000m,
                        TotalLiquidPlus = reader.IsDBNull(8) ? null : reader.GetDecimal(8) / 1_000_000m,
                        DistributedOpt = reader.IsDBNull(9) ? null : reader.GetDecimal(9) / 1_000_000m,
                        EmittedOpt = reader.IsDBNull(10) ? null : reader.GetDecimal(10) / 1_000_000m,
                        NetEmittedOpt = reader.IsDBNull(11) ? null : reader.GetDecimal(11) / 1_000_000m,
                        Lock6m = reader.IsDBNull(12) ? null : reader.GetInt32(12),
                        Lock12m = reader.IsDBNull(13) ? null : reader.GetInt32(13),
                        Lock18m = reader.IsDBNull(14) ? null : reader.GetInt32(14),
                        Lock24m = reader.IsDBNull(15) ? null : reader.GetInt32(15),
                        LockOther = reader.IsDBNull(16) ? null : reader.GetInt32(16),
                        TxCount = reader.IsDBNull(17) ? null : reader.GetInt32(17),
                        SentOpt = reader.IsDBNull(18) ? null : reader.GetDecimal(18) / 1_000_000m,
                        RecvOpt = reader.IsDBNull(19) ? null : reader.GetDecimal(19) / 1_000_000m,
                        UniqueCounterparties = reader.IsDBNull(20) ? null : reader.GetInt32(20),
                        StartBlockNumber = reader.IsDBNull(21) ? null : reader.GetInt32(21),
                        EndBlockNumber = reader.IsDBNull(22) ? null : reader.GetInt32(22),
                        TotalDistributedOpt = reader.IsDBNull(23) ? null : reader.GetDecimal(23) / 1_000_000m
                    });
                }
            }
        }

        return stats;
    }

    public async Task<List<DailyStatsEntry>> GetAllDailyStatsAsync()
    {
        var stats = new List<DailyStatsEntry>();

        using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT Date, TotalWallets, ActiveWallets, TotalSupply, TotalStaked, TotalLocked,
                       TotalUnbonding, TotalLiquid, TotalLiquidPlus, DistributedOpt, EmittedOpt, NetEmittedOpt,
                       Lock6m, Lock12m, Lock18m, Lock24m, LockOther, TxCount, SentOpt, RecvOpt,
                       UniqueCounterparties, StartBlockNumber, EndBlockNumber,
                       SUM(DistributedOpt) OVER (ORDER BY Date ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS TotalDistributed
                FROM DailyStats
                ORDER BY Date DESC
            ";

            using (var reader = await command.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    stats.Add(new DailyStatsEntry
                    {
                        Date = reader.GetString(0),
                        TotalWallets = reader.IsDBNull(1) ? null : reader.GetInt32(1),
                        ActiveWallets = reader.IsDBNull(2) ? null : reader.GetInt32(2),
                        TotalSupply = reader.IsDBNull(3) ? null : reader.GetDecimal(3) / 1_000_000m,
                        TotalStaked = reader.IsDBNull(4) ? null : reader.GetDecimal(4) / 1_000_000m,
                        TotalLocked = reader.IsDBNull(5) ? null : reader.GetDecimal(5) / 1_000_000m,
                        TotalUnbonding = reader.IsDBNull(6) ? null : reader.GetDecimal(6) / 1_000_000m,
                        TotalLiquid = reader.IsDBNull(7) ? null : reader.GetDecimal(7) / 1_000_000m,
                        TotalLiquidPlus = reader.IsDBNull(8) ? null : reader.GetDecimal(8) / 1_000_000m,
                        DistributedOpt = reader.IsDBNull(9) ? null : reader.GetDecimal(9) / 1_000_000m,
                        EmittedOpt = reader.IsDBNull(10) ? null : reader.GetDecimal(10) / 1_000_000m,
                        NetEmittedOpt = reader.IsDBNull(11) ? null : reader.GetDecimal(11) / 1_000_000m,
                        Lock6m = reader.IsDBNull(12) ? null : reader.GetInt32(12),
                        Lock12m = reader.IsDBNull(13) ? null : reader.GetInt32(13),
                        Lock18m = reader.IsDBNull(14) ? null : reader.GetInt32(14),
                        Lock24m = reader.IsDBNull(15) ? null : reader.GetInt32(15),
                        LockOther = reader.IsDBNull(16) ? null : reader.GetInt32(16),
                        TxCount = reader.IsDBNull(17) ? null : reader.GetInt32(17),
                        SentOpt = reader.IsDBNull(18) ? null : reader.GetDecimal(18) / 1_000_000m,
                        RecvOpt = reader.IsDBNull(19) ? null : reader.GetDecimal(19) / 1_000_000m,
                        UniqueCounterparties = reader.IsDBNull(20) ? null : reader.GetInt32(20),
                        StartBlockNumber = reader.IsDBNull(21) ? null : reader.GetInt32(21),
                        EndBlockNumber = reader.IsDBNull(22) ? null : reader.GetInt32(22),
                        TotalDistributedOpt = reader.IsDBNull(23) ? null : reader.GetDecimal(23) / 1_000_000m
                    });
                }
            }
        }

        return stats;
    }

    public async Task<string?> GetFirstTransactionDateAsync()
    {
        using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = "SELECT MIN(Date) FROM DailyStats WHERE Date IS NOT NULL";
            var result = await command.ExecuteScalarAsync();
            return result?.ToString();
        }
    }

    public async Task<SummaryCacheEntry?> GetSummaryCacheAsync()
    {
        using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await connection.OpenAsync();
            await EnsureSummaryCacheAsync(connection);

            var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT CacheKey, TotalWallets, TotalLiquid, TotalStaked, TotalLocked, TotalDistributed, StatsDate, LastUpdated
                FROM SummaryCache
                WHERE CacheKey = 'latest'
                LIMIT 1
            ";

            using (var reader = await command.ExecuteReaderAsync())
            {
                if (await reader.ReadAsync())
                {
                    return new SummaryCacheEntry
                    {
                        CacheKey = reader.GetString(0),
                        TotalWallets = reader.IsDBNull(1) ? null : reader.GetInt32(1),
                        TotalLiquid = reader.IsDBNull(2) ? null : reader.GetDecimal(2),
                        TotalStaked = reader.IsDBNull(3) ? null : reader.GetDecimal(3),
                        TotalLocked = reader.IsDBNull(4) ? null : reader.GetDecimal(4),
                        TotalDistributed = reader.IsDBNull(5) ? null : reader.GetDecimal(5),
                        StatsDate = reader.IsDBNull(6) ? null : reader.GetString(6),
                        LastUpdated = reader.IsDBNull(7) ? null : reader.GetDateTime(7)
                    };
                }
            }
        }

        return null;
    }

    private async Task EnsureSummaryCacheAsync(SqliteConnection connection)
    {
        var checkCommand = connection.CreateCommand();
        checkCommand.CommandText = "SELECT LastUpdated FROM SummaryCache WHERE CacheKey = 'latest' LIMIT 1";
        var lastUpdatedObj = await checkCommand.ExecuteScalarAsync();

        if (lastUpdatedObj != null && DateTime.TryParse(lastUpdatedObj.ToString(), out var lastUpdated))
        {
            if (DateTime.UtcNow - lastUpdated.ToUniversalTime() < TimeSpan.FromHours(SummaryCacheHours))
                return;
        }

        var statsCommand = connection.CreateCommand();
        statsCommand.CommandText = @"
            SELECT Date, TotalWallets, TotalLiquid, TotalStaked, TotalLocked
            FROM DailyStats
            ORDER BY Date DESC
            LIMIT 1
        ";

        using (var reader = await statsCommand.ExecuteReaderAsync())
        {
            if (!await reader.ReadAsync())
                return;

            var statsDate = reader.IsDBNull(0) ? null : reader.GetString(0);
            var totalWallets = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
            var totalLiquid = reader.IsDBNull(2) ? 0m : reader.GetDecimal(2) / 1_000_000m;
            var totalStaked = reader.IsDBNull(3) ? 0m : reader.GetDecimal(3) / 1_000_000m;
            var totalLocked = reader.IsDBNull(4) ? 0m : reader.GetDecimal(4) / 1_000_000m;
            var totalDistributed = totalLiquid + totalStaked;

            var upsert = connection.CreateCommand();
            upsert.CommandText = @"
                INSERT INTO SummaryCache (CacheKey, TotalWallets, TotalLiquid, TotalStaked, TotalLocked, TotalDistributed, StatsDate, LastUpdated)
                VALUES ('latest', @totalWallets, @totalLiquid, @totalStaked, @totalLocked, @totalDistributed, @statsDate, CURRENT_TIMESTAMP)
                ON CONFLICT(CacheKey) DO UPDATE SET
                    TotalWallets = excluded.TotalWallets,
                    TotalLiquid = excluded.TotalLiquid,
                    TotalStaked = excluded.TotalStaked,
                    TotalLocked = excluded.TotalLocked,
                    TotalDistributed = excluded.TotalDistributed,
                    StatsDate = excluded.StatsDate,
                    LastUpdated = CURRENT_TIMESTAMP
            ";
            upsert.Parameters.AddWithValue("@totalWallets", totalWallets);
            upsert.Parameters.AddWithValue("@totalLiquid", totalLiquid);
            upsert.Parameters.AddWithValue("@totalStaked", totalStaked);
            upsert.Parameters.AddWithValue("@totalLocked", totalLocked);
            upsert.Parameters.AddWithValue("@totalDistributed", totalDistributed);
            upsert.Parameters.AddWithValue("@statsDate", statsDate ?? string.Empty);
            await upsert.ExecuteNonQueryAsync();
        }
    }

    public async Task<List<string>> ValidateDailyStatsAsync()
    {
        var issues = new List<string>();

        using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await connection.OpenAsync();

            var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM DailyStats";
            var total = Convert.ToInt64(await command.ExecuteScalarAsync() ?? 0);

            command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(DISTINCT Date) FROM DailyStats";
            var distinctDates = Convert.ToInt64(await command.ExecuteScalarAsync() ?? 0);
            if (total != distinctDates)
                issues.Add($"Duplicate dates detected: total={total}, distinctDates={distinctDates}");

            command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM DailyStats WHERE Date IS NULL OR Date = ''";
            var nullDates = Convert.ToInt64(await command.ExecuteScalarAsync() ?? 0);
            if (nullDates > 0)
                issues.Add($"Null/empty dates found: {nullDates}");

            command = connection.CreateCommand();
            command.CommandText = @"
                SELECT COUNT(*)
                FROM DailyStats
                WHERE StartBlockNumber IS NOT NULL
                  AND EndBlockNumber IS NOT NULL
                  AND EndBlockNumber < StartBlockNumber
            ";
            var badBlocks = Convert.ToInt64(await command.ExecuteScalarAsync() ?? 0);
            if (badBlocks > 0)
                issues.Add($"Invalid block ranges found: {badBlocks}");
        }

        return issues;
    }
}

public class DailyStatsEntry
{
    public string Date { get; set; } = string.Empty;
    public int? TotalWallets { get; set; }
    public int? ActiveWallets { get; set; }
    public decimal? TotalSupply { get; set; }
    public decimal? TotalStaked { get; set; }
    public decimal? TotalLocked { get; set; }
    public decimal? TotalUnbonding { get; set; }
    public decimal? TotalLiquid { get; set; }
    public decimal? DistributedOpt { get; set; }
    public decimal? EmittedOpt { get; set; }
    public decimal? NetEmittedOpt { get; set; }
    public int? Lock6m { get; set; }
    public int? Lock12m { get; set; }
    public int? Lock18m { get; set; }
    public int? Lock24m { get; set; }
    public int? LockOther { get; set; }
    public int? TxCount { get; set; }
    public decimal? SentOpt { get; set; }
    public decimal? RecvOpt { get; set; }
    public int? UniqueCounterparties { get; set; }
    public int? StartBlockNumber { get; set; }
    public int? EndBlockNumber { get; set; }
    public decimal? TotalDistributedOpt { get; set; }
    public decimal? TotalLiquidPlus { get; set; }
}

public class SyncLogEntry
{
    public int Id { get; set; }
    public DateTime SyncTime { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? Message { get; set; }
    public int DurationMs { get; set; }
}

public class SummaryCacheEntry
{
    public string CacheKey { get; set; } = string.Empty;
    public int? TotalWallets { get; set; }
    public decimal? TotalLiquid { get; set; }
    public decimal? TotalStaked { get; set; }
    public decimal? TotalLocked { get; set; }
    public decimal? TotalDistributed { get; set; }
    public string? StatsDate { get; set; }
    public DateTime? LastUpdated { get; set; }
}

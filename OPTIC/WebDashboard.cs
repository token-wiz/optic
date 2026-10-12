using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Numerics;
using System.Text.Json;
using Cosmos.Base.Abci.V1Beta1;
using Cosmos.Base.Query.V1Beta1;
using Cosmos.Tx.V1Beta1;
using Google.Protobuf;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using OPTIC.Services;

public static class WebDashboard
{
    private const string HistoricalEmissionDepositAddress = "optio103r5ejt3gqyghvyel86hs5faru55r3w9kl4dst";
    private const string DistributionSourceAddress = "optio103r5ejt3gqyghvyel86hs5faru55r3w9kl4dst";
    private const string FeeRecipientAddress = "optio17xpfvakm2amg962yls6f84z3kell8c5l9yn0qn";
    private static decimal? _historicalEmissionDepositCache;
    private static DateTimeOffset _historicalEmissionDepositCacheTime;
    private static List<DailyDistributionPoint>? _dailyDistributionCache;
    private static DateTimeOffset _dailyDistributionCacheTime;
    private static List<TopWalletEntry>? _top100WalletCache;
    private static DateTimeOffset _top100WalletCacheTime;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, DateTime> _blockTimeCache = new();

    public static async Task RunAsync(string host, int port)
    {
        var url = $"http://{host}:{port}";
        var builder = WebApplication.CreateBuilder();
        var app = builder.Build();
        var workingDir = Environment.CurrentDirectory;

        // Initialize and start local data sync service
        var syncService = new LocalDataSyncService();
        await syncService.InitializeAsync();
        await syncService.StartSyncJobAsync(TimeSpan.FromMinutes(5)); // Sync every 5 minutes

        var activeLink = (string page) =>
        {
            var requestPath = "";
            return page == "dashboard" || page == requestPath ? @""" active=""true" : @"""";
        };

        app.MapGet("/", (HttpContext ctx) =>
        {
            var page = "dashboard";
            var html = RenderDashboardHtml(url, page, activeLink);
            ctx.Response.ContentType = "text/html; charset=utf-8";
            return Results.Text(html, "text/html");
        });

        app.MapGet("/page/{page}", (string page, HttpContext ctx) =>
        {
            var html = RenderDashboardHtml(url, page, activeLink);
            ctx.Response.ContentType = "text/html; charset=utf-8";
            return Results.Text(html, "text/html");
        });

        app.MapPost("/run", async (HttpRequest req, HttpContext ctx) =>
        {
            var form = await req.ReadFormAsync();
            var mode = form.TryGetValue("mode", out var modeValue) ? modeValue.ToString() : "";
            var args = BuildArgsFromForm(form, mode);
            var result = await RunCliSubprocessAsync(args);
            var html = RenderResultHtml(result, args);
            ctx.Response.ContentType = "text/html; charset=utf-8";
            return Results.Text(html, "text/html");
        });

        app.MapGet("/files/{filename}", (string filename) =>
        {
            var safePath = TryResolveSafePath(filename, workingDir);
            if (safePath == null) return Results.NotFound();
            return Results.File(safePath, "text/csv", filename);
        });

        app.MapGet("/images/{filename}", (string filename) =>
        {
            var imagesDir = Path.Combine(workingDir, "images");
            var imagePath = Path.Combine(imagesDir, filename);
            if (!imagePath.StartsWith(imagesDir) || !File.Exists(imagePath))
                return Results.NotFound();
            var ext = Path.GetExtension(filename).ToLowerInvariant();
            var mimeType = ext switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".svg" => "image/svg+xml",
                _ => "application/octet-stream"
            };
            return Results.File(imagePath, mimeType, filename);
        });

        app.MapGet("/api/sync-status", async (HttpContext ctx) =>
        {
            var status = await syncService.GetSyncStatusAsync();
            var logs = await syncService.GetRecentSyncLogsAsync(5);
            ctx.Response.ContentType = "application/json; charset=utf-8";
            return Results.Json(new { status, recentLogs = logs });
        });

        app.MapGet("/api/daily-stats", async (HttpContext ctx) =>
        {
            try
            {
                var stats = await syncService.GetDailyStatsAsync(30);
                ctx.Response.ContentType = "application/json; charset=utf-8";
                return Results.Json(new { 
                    stats = stats.Select(s => new
                    {
                        date = s.Date,
                        totalWallets = s.TotalWallets,
                        activeWallets = s.ActiveWallets,
                        totalSupply = s.TotalSupply,
                        totalStaked = s.TotalStaked,
                        totalLocked = s.TotalLocked,
                        totalUnbonding = s.TotalUnbonding,
                        totalLiquid = s.TotalLiquid,
                        totalLiquidPlus = s.TotalLiquidPlus,
                        distributedOpt = s.DistributedOpt,
                        emittedOpt = s.EmittedOpt,
                        netEmittedOpt = s.NetEmittedOpt,
                        totalDistributedOpt = s.TotalDistributedOpt,
                        txCount = s.TxCount,
                        sentOpt = s.SentOpt,
                        recvOpt = s.RecvOpt,
                        uniqueCounterparties = s.UniqueCounterparties
                    }).ToList()
                });
            }
            catch (Exception ex)
            {
                ctx.Response.StatusCode = 500;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                return Results.Json(new { error = ex.Message });
            }
        });

        app.MapGet("/api/daily-stats/all", async (HttpContext ctx) =>
        {
            try
            {
                var stats = await syncService.GetAllDailyStatsAsync();
                ctx.Response.ContentType = "application/json; charset=utf-8";
                return Results.Json(new { 
                    stats = stats.Select(s => new
                    {
                        date = s.Date,
                        totalWallets = s.TotalWallets,
                        activeWallets = s.ActiveWallets,
                        totalSupply = s.TotalSupply,
                        totalStaked = s.TotalStaked,
                        totalLocked = s.TotalLocked,
                        totalUnbonding = s.TotalUnbonding,
                        totalLiquid = s.TotalLiquid,
                        totalLiquidPlus = s.TotalLiquidPlus,
                        distributedOpt = s.DistributedOpt,
                        emittedOpt = s.EmittedOpt,
                        netEmittedOpt = s.NetEmittedOpt,
                        totalDistributedOpt = s.TotalDistributedOpt,
                        lock6m = s.Lock6m,
                        lock12m = s.Lock12m,
                        lock18m = s.Lock18m,
                        lock24m = s.Lock24m,
                        lockOther = s.LockOther,
                        txCount = s.TxCount,
                        sentOpt = s.SentOpt,
                        recvOpt = s.RecvOpt,
                        uniqueCounterparties = s.UniqueCounterparties,
                        startBlockNumber = s.StartBlockNumber,
                        endBlockNumber = s.EndBlockNumber
                    }).ToList(),
                    count = stats.Count
                });
            }
            catch (Exception ex)
            {
                ctx.Response.StatusCode = 500;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                return Results.Json(new { error = ex.Message });
            }
        });

        app.MapGet("/api/distributions/daily", async (HttpContext ctx) =>
        {
            try
            {
                var rows = (await syncService.GetAllDailyStatsAsync())
                    .Where(s => (s.DistributedOpt ?? 0m) > 0m || (s.UniqueCounterparties ?? 0) > 0)
                    .OrderBy(s => s.Date, StringComparer.Ordinal)
                    .Select(s => new DailyDistributionPoint
                    {
                        Date = s.Date,
                        DistributedOpt = s.DistributedOpt ?? 0m,
                        WalletCount = s.UniqueCounterparties ?? 0
                    })
                    .ToList();
                ctx.Response.ContentType = "application/json; charset=utf-8";
                return Results.Json(new
                {
                    source = DistributionSourceAddress,
                    sourceType = "synced",
                    rows = rows.Select(r => new
                    {
                        date = r.Date,
                        distributedOpt = r.DistributedOpt,
                        walletCount = r.WalletCount
                    }).ToList()
                });
            }
            catch (Exception ex)
            {
                ctx.Response.StatusCode = 500;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                return Results.Json(new { error = ex.Message });
            }
        });

        app.MapGet("/api/summary", async (HttpContext ctx) =>
        {
            try
            {
                var summary = await syncService.GetSummaryCacheAsync();
                if (summary != null)
                {
                    summary.TotalEmitted = await GetHistoricalEmissionDepositsAsync();
                    await PopulateLiveLockBucketsIfNeededAsync(summary);
                }

                ctx.Response.ContentType = "application/json; charset=utf-8";
                return Results.Json(new
                {
                    summary = summary == null ? null : new
                    {
                        totalWallets = summary.TotalWallets,
                        totalLiquid = summary.TotalLiquid,
                        totalStaked = summary.TotalStaked,
                        totalLocked = summary.TotalLocked,
                        totalDistributed = summary.TotalDistributed,
                        totalEmitted = summary.TotalEmitted,
                        lock6mCount = summary.Lock6mCount,
                        lock12mCount = summary.Lock12mCount,
                        lock18mCount = summary.Lock18mCount,
                        lock24mCount = summary.Lock24mCount,
                        lock6mAmount = summary.Lock6mAmount,
                        lock12mAmount = summary.Lock12mAmount,
                        lock18mAmount = summary.Lock18mAmount,
                        lock24mAmount = summary.Lock24mAmount,
                        statsDate = summary.StatsDate,
                        lastUpdated = summary.LastUpdated
                    }
                });
            }
            catch (Exception ex)
            {
                ctx.Response.StatusCode = 500;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                return Results.Json(new { error = ex.Message });
            }
        });

        app.MapGet("/api/top100", async (HttpContext ctx) =>
        {
            try
            {
                var wallets = await GetTop100WalletsAsync();
                ctx.Response.ContentType = "application/json; charset=utf-8";
                return Results.Json(new
                {
                    wallets = wallets.Select(w => new
                    {
                        rank = w.Rank,
                        address = w.Address,
                        walletBalance = w.WalletBalance,
                        staked = w.Staked,
                        unbonding = w.Unbonding,
                        lock6Months = w.Lock6Months,
                        lock12Months = w.Lock12Months,
                        lock18Months = w.Lock18Months,
                        lock24Months = w.Lock24Months,
                        totalLocked = w.TotalLocked,
                        total = w.Total
                    }),
                    lastUpdated = _top100WalletCacheTime
                });
            }
            catch (Exception ex)
            {
                ctx.Response.ContentType = "application/json; charset=utf-8";
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        });

        app.MapGet("/api/price/daily", async (HttpContext ctx) =>
        {
            try
            {
                var series = await TokenPriceService.GetDailySeriesAsync(ctx.RequestAborted);
                return Results.Json(new
                {
                    source = "CoinMarketCap (OPT/USD)",
                    rows = series.Select(p => new { date = p.DateUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), priceUsd = p.CloseUsd })
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        });

        app.MapGet("/api/distributions/address", async (HttpContext ctx) =>
        {
            var address = (ctx.Request.Query["address"].ToString() ?? "").Trim().ToLowerInvariant();
            var asCsv = string.Equals(ctx.Request.Query["format"], "csv", StringComparison.OrdinalIgnoreCase);
            if (!System.Text.RegularExpressions.Regex.IsMatch(address, "^optio1[02-9ac-hj-np-z]{38,58}$"))
                return Results.Json(new { error = "Enter a valid optio1... wallet address." }, statusCode: 400);

            try
            {
                var report = await GetAddressDistributionReportAsync(address, ctx.RequestAborted);
                if (int.TryParse(ctx.Request.Query["year"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var year))
                    report.Rows = report.Rows.Where(r => r.TimeUtc.Year == year).ToList();
                var kind = ctx.Request.Query["type"].ToString();
                if (kind is "node" or "staking")
                    report.Rows = report.Rows.Where(r => r.Kind == kind).ToList();
                else
                    kind = "";

                if (asCsv)
                {
                    var csv = new System.Text.StringBuilder();
                    csv.AppendLine("time_utc,height,tx_hash,type,type_inferred,amount_opt,price_usd,value_usd,price_interval,price_candle_open_utc,memo");
                    foreach (var r in report.Rows)
                    {
                        csv.Append(r.TimeUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(',')
                           .Append(r.Height.ToString(CultureInfo.InvariantCulture)).Append(',')
                           .Append(r.TxHash).Append(',')
                           .Append(r.Kind == "staking" ? "staking/participation" : "node").Append(',')
                           .Append(r.KindInferred ? "yes" : "no").Append(',')
                           .Append(r.AmountOpt.ToString("0.######", CultureInfo.InvariantCulture)).Append(',')
                           .Append(r.PriceUsd?.ToString("0.##########", CultureInfo.InvariantCulture) ?? "").Append(',')
                           .Append(r.ValueUsd?.ToString("0.####", CultureInfo.InvariantCulture) ?? "").Append(',')
                           .Append(r.PriceInterval ?? "").Append(',')
                           .Append(r.PriceCandleOpenUtc?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "").Append(',')
                           .AppendLine(CsvField(r.Memo));
                    }
                    return Results.File(System.Text.Encoding.UTF8.GetBytes(csv.ToString()), "text/csv", $"distributions-{address}{(year > 0 ? "-" + year.ToString(CultureInfo.InvariantCulture) : "")}{(kind.Length > 0 ? "-" + kind : "")}.csv");
                }

                var priceRange = TokenPriceService.AvailableRange;
                return Results.Json(new
                {
                    address,
                    source = DistributionSourceAddress,
                    priceSource = "CoinMarketCap (OPT/USD)",
                    priceDataFrom = priceRange.FirstUtc,
                    priceDataTo = priceRange.LastUtc,
                    totalOpt = report.Rows.Sum(r => r.AmountOpt),
                    totalValueUsd = report.Rows.Sum(r => r.ValueUsd ?? 0m),
                    unpricedCount = report.Rows.Count(r => r.PriceUsd is null),
                    truncated = report.Truncated,
                    rows = report.Rows.Select(r => new
                    {
                        timeUtc = r.TimeUtc,
                        height = r.Height,
                        txHash = r.TxHash,
                        memo = r.Memo,
                        kind = r.Kind,
                        kindInferred = r.KindInferred,
                        amountOpt = r.AmountOpt,
                        priceUsd = r.PriceUsd,
                        valueUsd = r.ValueUsd,
                        priceInterval = r.PriceInterval
                    })
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        });

        app.MapGet("/api/wallet/ledger", async (HttpContext ctx) =>
        {
            var address = (ctx.Request.Query["address"].ToString() ?? "").Trim().ToLowerInvariant();
            if (!System.Text.RegularExpressions.Regex.IsMatch(address, "^optio1[02-9ac-hj-np-z]{38,58}$"))
                return Results.Json(new { error = "Enter a valid optio1... wallet address." }, statusCode: 400);

            try
            {
                var ledger = await GetWalletLedgerAsync(address, ctx.RequestAborted);
                var priceRange = TokenPriceService.AvailableRange;
                var daily = await TokenPriceService.GetDailySeriesAsync(ctx.RequestAborted);
                return Results.Json(new
                {
                    address,
                    distributionSource = DistributionSourceAddress,
                    priceSource = "CoinMarketCap (OPT/USD)",
                    priceDataTo = priceRange.LastUtc,
                    latestPrice = daily.Count > 0 ? daily[^1].CloseUsd : (decimal?)null,
                    latestPriceDate = daily.Count > 0 ? daily[^1].DateUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null,
                    truncated = ledger.Truncated,
                    modules = ModuleAccounts,
                    entries = ledger.Entries.Select(e => new
                    {
                        timeUtc = e.TimeUtc,
                        height = e.Height,
                        txHash = e.TxHash,
                        memo = e.Memo,
                        direction = e.Direction,
                        counterparty = e.Counterparty,
                        category = e.Category,
                        amountOpt = e.AmountOpt,
                        priceUsd = e.PriceUsd,
                        priceInterval = e.PriceInterval
                    })
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        });

        Console.WriteLine($"Starting OPTIC web dashboard at {url}");
        await app.RunAsync($"http://{host}:{port}");
    }

    static async Task<decimal> GetHistoricalEmissionDepositsAsync()
    {
        if (_historicalEmissionDepositCache.HasValue &&
            DateTimeOffset.UtcNow - _historicalEmissionDepositCacheTime < TimeSpan.FromHours(12))
        {
            return _historicalEmissionDepositCache.Value;
        }

        try
        {
            var cfg = LoadDashboardConfig();
            var rpcBase = cfg.GetValueOrDefault("rpc", "http://127.0.0.1:26657");
            var denom = cfg.GetValueOrDefault("denom", "uopt");
            var denomWantedUpper = denom.ToUpperInvariant();
            var scaleInt = denom.StartsWith("u", StringComparison.OrdinalIgnoreCase) ? 1_000_000 : 1;
            var pageLimit = TryParseConfigInt(cfg, "pageLimit", 100);
            var maxPages = TryParseConfigInt(cfg, "maxPages", 50000);

            if (pageLimit <= 0) pageLimit = 100;
            if (pageLimit > 100) pageLimit = 100;
            if (maxPages <= 0) maxPages = 50000;

            using var rpcHttp = new HttpClient
            {
                BaseAddress = new Uri(rpcBase.TrimEnd('/') + "/"),
                Timeout = TimeSpan.FromSeconds(30)
            };

            var totalUopt = await SumHistoricalReceivesFromRpcAsync(
                rpcHttp,
                $"coin_received.receiver='{HistoricalEmissionDepositAddress}'",
                HistoricalEmissionDepositAddress,
                denomWantedUpper,
                pageLimit,
                maxPages,
                useCoinReceivedEvents: true);

            if (totalUopt <= BigInteger.Zero)
            {
                totalUopt = await SumHistoricalReceivesFromRpcAsync(
                    rpcHttp,
                    $"transfer.recipient='{HistoricalEmissionDepositAddress}'",
                    HistoricalEmissionDepositAddress,
                    denomWantedUpper,
                    pageLimit,
                    maxPages,
                    useCoinReceivedEvents: false);
            }

            var totalOpt = ToDecimalOpt(totalUopt, scaleInt);
            _historicalEmissionDepositCache = totalOpt;
            _historicalEmissionDepositCacheTime = DateTimeOffset.UtcNow;
            return totalOpt;
        }
        catch
        {
            return _historicalEmissionDepositCache ?? 0m;
        }
    }

    static async Task<List<DailyDistributionPoint>> GetDailyDistributionPointsAsync()
    {
        if (_dailyDistributionCache is not null &&
            DateTimeOffset.UtcNow - _dailyDistributionCacheTime < TimeSpan.FromMinutes(30))
        {
            return _dailyDistributionCache;
        }

        var cfg = LoadDashboardConfig();
        var rpcBase = cfg.GetValueOrDefault("rpc", "http://127.0.0.1:26657");
        var denom = cfg.GetValueOrDefault("denom", "uopt");
        var denomWantedUpper = denom.ToUpperInvariant();
        var scaleInt = denom.StartsWith("u", StringComparison.OrdinalIgnoreCase) ? 1_000_000 : 1;
        var pageLimit = TryParseConfigInt(cfg, "pageLimit", 100);
        var maxPages = TryParseConfigInt(cfg, "dailyDistributionPages", 20);

        if (pageLimit <= 0) pageLimit = 100;
        if (pageLimit > 100) pageLimit = 100;
        if (maxPages <= 0) maxPages = 20;
        if (maxPages > 20) maxPages = 20;

        using var rpcHttp = new HttpClient
        {
            BaseAddress = new Uri(rpcBase.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromMinutes(5)
        };

        try
        {
            var rows = await GetDailyDistributionsFromRpcAsync(
                rpcHttp,
                $"message.sender='{DistributionSourceAddress}'",
                denomWantedUpper,
                scaleInt,
                pageLimit,
                maxPages);

            _dailyDistributionCache = rows;
            _dailyDistributionCacheTime = DateTimeOffset.UtcNow;
            return rows;
        }
        catch
        {
            return _dailyDistributionCache ?? new List<DailyDistributionPoint>();
        }
    }

    static async Task<List<DailyDistributionPoint>> GetDailyDistributionsFromRpcAsync(
        HttpClient rpcHttp,
        string query,
        string denomWantedUpper,
        int scaleInt,
        int pageLimit,
        int maxPages)
    {
        var perPage = Math.Clamp(pageLimit, 1, 100);
        var pages = Math.Clamp(maxPages, 1, 1000);
        var seenHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var transferHits = new List<(long Height, List<(string Recipient, BigInteger AmountUopt)> Transfers)>();
        var byDate = new Dictionary<string, (BigInteger AmountUopt, HashSet<string> Recipients)>(StringComparer.Ordinal);

        for (var page = 1; page <= pages; page++)
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

            using var content = new StringContent(
                JsonSerializer.Serialize(payload),
                System.Text.Encoding.UTF8,
                "application/json");

            HttpResponseMessage response;
            try
            {
                response = await rpcHttp.PostAsync("", content);
            }
            catch
            {
                break;
            }

            using (response)
            {
            if (!response.IsSuccessStatusCode)
                break;

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("result", out var result) ||
                !result.TryGetProperty("txs", out var txs) ||
                txs.ValueKind != JsonValueKind.Array)
                break;

            var count = 0;
            foreach (var tx in txs.EnumerateArray())
            {
                count++;
                var hash = tx.TryGetProperty("hash", out var hashEl) ? hashEl.GetString() : null;
                if (!string.IsNullOrWhiteSpace(hash) && !seenHashes.Add(hash))
                    continue;

                var transfers = GetDistributionTransfersFromTxSearchResult(tx, denomWantedUpper);
                if (transfers.Count == 0)
                    continue;

                if (!TryGetTxHeight(tx, out var height))
                    continue;

                transferHits.Add((height, transfers));
            }

            if (count < perPage)
                break;
            }
        }

        var uniqueHeights = transferHits
            .Select(hit => hit.Height)
            .Distinct()
            .ToList();
        var blockTimeCache = new System.Collections.Concurrent.ConcurrentDictionary<long, DateTime>();
        await Parallel.ForEachAsync(
            uniqueHeights,
            new ParallelOptions { MaxDegreeOfParallelism = 16 },
            async (height, _) =>
            {
                var fetched = await TryGetBlockTimeFromRpcAsync(rpcHttp, height);
                if (fetched.HasValue)
                    blockTimeCache[height] = fetched.Value;
            });

        foreach (var hit in transferHits)
        {
            if (!blockTimeCache.TryGetValue(hit.Height, out var blockTimeUtc))
                continue;

            var date = blockTimeUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (!byDate.TryGetValue(date, out var existing))
                    existing = (BigInteger.Zero, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

                foreach (var transfer in hit.Transfers)
                {
                    existing.AmountUopt += transfer.AmountUopt;
                    existing.Recipients.Add(transfer.Recipient);
                }

                byDate[date] = existing;
        }

        return byDate
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new DailyDistributionPoint
            {
                Date = kv.Key,
                DistributedOpt = ToDecimalOpt(kv.Value.AmountUopt, scaleInt),
                WalletCount = kv.Value.Recipients.Count
            })
            .ToList();
    }

    // Module accounts: bech32("optio", sha256(module name)[:20]).
    static readonly Dictionary<string, string> ModuleAccounts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["optio1fl48vsnmsdzcv85q5d2q4z5ajdha8yu3qxeqje"] = "bonded_tokens_pool",
        ["optio1tygms3xhhs3yv487phx3dw4a95jn7t7l5x93yd"] = "not_bonded_tokens_pool",
        ["optio1jv65s3grqf6v6jl3dp4t6c9t9rk99cd8jz5nz3"] = "distribution",
        ["optio17xpfvakm2amg962yls6f84z3kell8c5l9yn0qn"] = "fee_collector",
        ["optio10d07y265gmmuvt4z0w9aw880jnsr700j0lr2aa"] = "gov",
        ["optio1m3h30wlvsf8llruxtpukdvsy0km2kum8avhhvw"] = "mint",
        ["optio1njty28rqtpw6n59sjj4esw76enp4mg6gr7du2y"] = "lockup",
    };

    sealed record RpcContext(HttpClient Http, string DenomWantedUpper, int ScaleInt) : IDisposable
    {
        public void Dispose() => Http.Dispose();
    }

    static RpcContext CreateRpcContext()
    {
        var cfg = LoadDashboardConfig();
        var rpcBase = cfg.GetValueOrDefault("rpc", "http://127.0.0.1:26657");
        var denom = cfg.GetValueOrDefault("denom", "uopt");
        var http = new HttpClient
        {
            BaseAddress = new Uri(rpcBase.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromMinutes(2)
        };
        return new RpcContext(http, denom.ToUpperInvariant(), denom.StartsWith("u", StringComparison.OrdinalIgnoreCase) ? 1_000_000 : 1);
    }

    /// <summary>
    /// Pages through tx_search (oldest first), calling onTx once per unique tx.
    /// Returns true when the page cap was hit before the results ran out.
    /// </summary>
    static async Task<bool> SearchTxsAsync(HttpClient rpcHttp, string query, Action<JsonElement, string, long> onTx, CancellationToken ct)
    {
        const int perPage = 100;
        const int maxPages = 200;
        var seenHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var page = 1; page <= maxPages; page++)
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
                    order_by = "asc",
                    prove = false
                }
            };

            using var content = new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json");
            using var response = await rpcHttp.PostAsync("", content, ct);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Node tx_search failed with HTTP {(int)response.StatusCode}.");

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("error", out var rpcError))
            {
                // CometBFT reports paging past the last page as an error.
                if (page > 1) return false;
                throw new InvalidOperationException(rpcError.TryGetProperty("data", out var errData) ? errData.ToString() : rpcError.ToString());
            }

            if (!doc.RootElement.TryGetProperty("result", out var result) ||
                !result.TryGetProperty("txs", out var txs) ||
                txs.ValueKind != JsonValueKind.Array)
                return false;

            var count = 0;
            foreach (var tx in txs.EnumerateArray())
            {
                count++;
                var hash = tx.TryGetProperty("hash", out var hashEl) ? hashEl.GetString() ?? "" : "";
                if (hash.Length > 0 && !seenHashes.Add(hash))
                    continue;
                if (TryGetTxHeight(tx, out var height))
                    onTx(tx, hash, height);
            }

            if (count < perPage)
                return false;
        }

        return true;
    }

    /// <summary>The memo from a tx_search result's base64 TxRaw, or "" when absent or undecodable.</summary>
    static string GetTxMemo(JsonElement tx)
    {
        try
        {
            if (!tx.TryGetProperty("tx", out var txEl) || txEl.ValueKind != JsonValueKind.String)
                return "";
            var raw = TxRaw.Parser.ParseFrom(Convert.FromBase64String(txEl.GetString() ?? ""));
            return TxBody.Parser.ParseFrom(raw.BodyBytes).Memo?.Trim() ?? "";
        }
        catch
        {
            return "";
        }
    }

    static async Task ResolveBlockTimesAsync(HttpClient rpcHttp, IEnumerable<long> heights, CancellationToken ct)
    {
        var missing = heights.Distinct().Where(h => !_blockTimeCache.ContainsKey(h)).ToList();
        await Parallel.ForEachAsync(
            missing,
            new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = ct },
            async (height, _) =>
            {
                var time = await TryGetBlockHeaderTimeAsync(rpcHttp, height);
                if (time.HasValue)
                    _blockTimeCache[height] = time.Value;
            });
    }

    static async Task<AddressDistributionReport> GetAddressDistributionReportAsync(string address, CancellationToken ct)
    {
        using var rpc = CreateRpcContext();

        // Query by recipient only and filter the sender locally: adding transfer.sender to the
        // tx_search query makes the node's indexer ~25x slower.
        var report = new AddressDistributionReport();
        report.Truncated = await SearchTxsAsync(rpc.Http, $"transfer.recipient='{address}'", (tx, hash, height) =>
        {
            var amountUopt = BigInteger.Zero;
            foreach (var entry in GetTxTransfers(tx, rpc.DenomWantedUpper))
            {
                if (entry.Sender.Equals(DistributionSourceAddress, StringComparison.OrdinalIgnoreCase) &&
                    entry.Recipient.Equals(address, StringComparison.OrdinalIgnoreCase))
                    amountUopt += entry.AmountUnits;
            }

            if (amountUopt > BigInteger.Zero)
                report.Rows.Add(new AddressDistributionRow { Height = height, TxHash = hash, Memo = GetTxMemo(tx), AmountOpt = ToDecimalOpt(amountUopt, rpc.ScaleInt) });
        }, ct);

        await ResolveBlockTimesAsync(rpc.Http, report.Rows.Select(r => r.Height), ct);
        report.Rows.RemoveAll(r => !_blockTimeCache.ContainsKey(r.Height));
        foreach (var row in report.Rows)
            row.TimeUtc = _blockTimeCache[row.Height];
        report.Rows.Sort((a, b) => a.Height.CompareTo(b.Height));
        ClassifyDistributionKinds(report.Rows);

        var prices = await TokenPriceService.GetPricesAtAsync(report.Rows.Select(r => r.TimeUtc).Distinct().ToList(), ct);
        foreach (var row in report.Rows)
        {
            if (prices.TryGetValue(row.TimeUtc, out var price) && price is not null)
            {
                row.PriceUsd = price.PriceUsd;
                row.PriceInterval = price.Interval;
                row.PriceCandleOpenUtc = price.CandleOpenUtc;
            }
        }

        return report;
    }

    /// <summary>
    /// The distribution account pays both node rewards and staking/participation rewards. When both
    /// land on the same UTC day the node payout is the large one and the staking payout is far smaller
    /// (a few percent of it). Days with such a clear split are labelled directly; payouts on other days
    /// (a single payout, or several of similar size) take the kind whose amounts they are closest to
    /// on the nearest split days before and after, and are marked as inferred.
    /// </summary>
    static void ClassifyDistributionKinds(List<AddressDistributionRow> rows)
    {
        const decimal SplitRatio = 0.10m;
        var byDay = rows.GroupBy(r => r.TimeUtc.Date).OrderBy(g => g.Key).ToList();
        var splitDays = new List<List<AddressDistributionRow>>();
        var splitIndexByDay = new Dictionary<DateTime, int>();

        foreach (var day in byDay)
        {
            var max = day.Max(r => r.AmountOpt);
            if (day.Count() < 2 || day.Min(r => r.AmountOpt) >= SplitRatio * max)
                continue;
            foreach (var r in day)
            {
                r.Kind = r.AmountOpt >= SplitRatio * max ? "node" : "staking";
                r.KindInferred = false;
            }
            splitIndexByDay[day.Key] = splitDays.Count;
            splitDays.Add(day.ToList());
        }

        var splitDates = splitIndexByDay.Keys.ToList();
        foreach (var day in byDay)
        {
            if (splitIndexByDay.ContainsKey(day.Key))
                continue;
            var next = splitDates.BinarySearch(day.Key);
            next = next < 0 ? ~next : next;
            var refs = new[] { next - 1, next }
                .Where(i => i >= 0 && i < splitDays.Count)
                .SelectMany(i => splitDays[i])
                .ToList();
            foreach (var r in day)
            {
                r.KindInferred = true;
                double Distance(string kind) => refs.Where(x => x.Kind == kind && x.AmountOpt > 0 && r.AmountOpt > 0)
                    .Select(x => Math.Abs(Math.Log((double)r.AmountOpt) - Math.Log((double)x.AmountOpt)))
                    .DefaultIfEmpty(double.MaxValue).Min();
                r.Kind = Distance("staking") < Distance("node") ? "staking" : "node";
            }
        }
    }

    /// <summary>
    /// Every OPT transfer into or out of the address, one entry per tx, direction and counterparty,
    /// classified by counterparty and priced at the hour it happened.
    /// </summary>
    static async Task<WalletLedger> GetWalletLedgerAsync(string address, CancellationToken ct)
    {
        using var rpc = CreateRpcContext();
        var ledger = new WalletLedger();
        var byKey = new Dictionary<(string Hash, string Direction, string Counterparty), WalletLedgerEntry>();
        var amounts = new Dictionary<(string Hash, string Direction, string Counterparty), BigInteger>();

        void OnTx(JsonElement tx, string hash, long height)
        {
            foreach (var t in GetTxTransfers(tx, rpc.DenomWantedUpper))
            {
                var isIn = t.Recipient.Equals(address, StringComparison.OrdinalIgnoreCase);
                var isOut = t.Sender.Equals(address, StringComparison.OrdinalIgnoreCase);
                if (isIn == isOut)
                    continue; // unrelated, or a transfer to itself

                var direction = isIn ? "in" : "out";
                var counterparty = (isIn ? t.Sender : t.Recipient).ToLowerInvariant();
                var key = (hash, direction, counterparty);
                if (!byKey.ContainsKey(key))
                {
                    byKey[key] = new WalletLedgerEntry
                    {
                        Height = height,
                        TxHash = hash,
                        Memo = GetTxMemo(tx),
                        Direction = direction,
                        Counterparty = counterparty,
                        Category = ClassifyCounterparty(counterparty, isIn)
                    };
                    amounts[key] = BigInteger.Zero;
                }
                amounts[key] += t.AmountUnits;
            }
        }

        // The node's indexer serves the two sides as separate queries; SearchTxsAsync dedupes
        // within a query and byKey dedupes across them.
        var truncatedIn = await SearchTxsAsync(rpc.Http, $"transfer.recipient='{address}'", OnTx, ct);
        var truncatedOut = await SearchTxsAsync(rpc.Http, $"transfer.sender='{address}'", OnTx, ct);
        ledger.Truncated = truncatedIn || truncatedOut;

        foreach (var (key, entry) in byKey)
            entry.AmountOpt = ToDecimalOpt(amounts[key], rpc.ScaleInt);
        ledger.Entries = byKey.Values.Where(e => e.AmountOpt > 0m).ToList();

        await ResolveBlockTimesAsync(rpc.Http, ledger.Entries.Select(e => e.Height), ct);
        ledger.Entries.RemoveAll(e => !_blockTimeCache.ContainsKey(e.Height));
        foreach (var entry in ledger.Entries)
            entry.TimeUtc = _blockTimeCache[entry.Height];
        // Within a block, receipts first so a same-block send can draw on them.
        ledger.Entries.Sort((a, b) => a.Height != b.Height
            ? a.Height.CompareTo(b.Height)
            : string.CompareOrdinal(a.Direction, b.Direction));

        var prices = await TokenPriceService.GetPricesAtAsync(ledger.Entries.Select(e => e.TimeUtc).Distinct().ToList(), ct);
        foreach (var entry in ledger.Entries)
        {
            if (prices.TryGetValue(entry.TimeUtc, out var price) && price is not null)
            {
                entry.PriceUsd = price.PriceUsd;
                entry.PriceInterval = price.Interval;
            }
        }

        return ledger;
    }

    static string CsvField(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }

    static string ClassifyCounterparty(string counterparty, bool incoming)
    {
        if (counterparty.Equals(DistributionSourceAddress, StringComparison.OrdinalIgnoreCase))
            return incoming ? "distribution" : "wallet";
        if (ModuleAccounts.TryGetValue(counterparty, out var module))
        {
            if (incoming && module == "distribution") return "reward";
            if (!incoming && module == "fee_collector") return "fee";
            return "module";
        }
        return "wallet";
    }

    static async Task<DateTime?> TryGetBlockHeaderTimeAsync(HttpClient rpcHttp, long height)
    {
        try
        {
            using var response = await rpcHttp.GetAsync($"header?height={height.ToString(CultureInfo.InvariantCulture)}");
            if (!response.IsSuccessStatusCode)
                return null;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (doc.RootElement.TryGetProperty("result", out var result) &&
                result.TryGetProperty("header", out var header) &&
                header.TryGetProperty("time", out var timeEl) &&
                DateTimeOffset.TryParse(timeEl.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            {
                return parsed.UtcDateTime;
            }
        }
        catch
        {
        }

        return null;
    }

    static List<(string Recipient, BigInteger AmountUopt)> GetDistributionTransfersFromTxSearchResult(
        JsonElement tx,
        string denomWantedUpper)
    {
        var transfers = new List<(string Recipient, BigInteger AmountUopt)>();
        if (!tx.TryGetProperty("tx_result", out var txResult) ||
            !txResult.TryGetProperty("events", out var events) ||
            events.ValueKind != JsonValueKind.Array)
            return transfers;

        foreach (var ev in events.EnumerateArray())
        {
            var type = ev.TryGetProperty("type", out var typeEl) ? typeEl.GetString() ?? "" : "";
            if (!string.Equals(type, "transfer", StringComparison.Ordinal))
                continue;

            var attrs = ReadCometEventAttributes(ev);
            foreach (var entry in ParseTransferEventTuples(attrs, denomWantedUpper))
            {
                if (!entry.Sender.Equals(DistributionSourceAddress, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (entry.Recipient.Equals(DistributionSourceAddress, StringComparison.OrdinalIgnoreCase) ||
                    entry.Recipient.Equals(FeeRecipientAddress, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (entry.AmountUnits > BigInteger.Zero)
                    transfers.Add((entry.Recipient, entry.AmountUnits));
            }
        }

        return transfers;
    }

    static bool TryGetTxHeight(JsonElement tx, out long height)
    {
        height = 0;
        if (!tx.TryGetProperty("height", out var heightEl))
            return false;

        if (heightEl.ValueKind == JsonValueKind.String)
            return long.TryParse(heightEl.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out height);

        if (heightEl.ValueKind == JsonValueKind.Number)
            return heightEl.TryGetInt64(out height);

        return false;
    }

    static async Task<DateTime?> TryGetBlockTimeFromRpcAsync(HttpClient rpcHttp, long height)
    {
        try
        {
            using var response = await rpcHttp.GetAsync($"block?height={height.ToString(CultureInfo.InvariantCulture)}");
            if (!response.IsSuccessStatusCode)
                return null;

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("result", out var result) &&
                result.TryGetProperty("block", out var block) &&
                block.TryGetProperty("header", out var header) &&
                header.TryGetProperty("time", out var timeEl) &&
                DateTimeOffset.TryParse(timeEl.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            {
                return parsed.UtcDateTime;
            }
        }
        catch
        {
        }

        return null;
    }

    static async Task<BigInteger> SumHistoricalReceivesFromRpcAsync(
        HttpClient rpcHttp,
        string query,
        string recipient,
        string denomWantedUpper,
        int pageLimit,
        int maxPages,
        bool useCoinReceivedEvents)
    {
        BigInteger total = BigInteger.Zero;
        var seenHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var perPage = Math.Clamp(pageLimit, 1, 100);
        var pages = Math.Clamp(maxPages, 1, 1000);

        for (var page = 1; page <= pages; page++)
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

            using var content = new StringContent(
                JsonSerializer.Serialize(payload),
                System.Text.Encoding.UTF8,
                "application/json");

            using var response = await rpcHttp.PostAsync("", content);

            if (!response.IsSuccessStatusCode)
                break;

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("result", out var result) ||
                !result.TryGetProperty("txs", out var txs) ||
                txs.ValueKind != JsonValueKind.Array)
                break;

            var count = 0;
            foreach (var tx in txs.EnumerateArray())
            {
                count++;
                var hash = tx.TryGetProperty("hash", out var hashEl) ? hashEl.GetString() : null;
                if (!string.IsNullOrWhiteSpace(hash) && !seenHashes.Add(hash))
                    continue;

                total += SumReceiveEventsFromTxSearchResult(tx, recipient, denomWantedUpper, useCoinReceivedEvents);
            }

            if (count < perPage)
                break;
        }

        return total;
    }

    static BigInteger SumReceiveEventsFromTxSearchResult(
        JsonElement tx,
        string recipient,
        string denomWantedUpper,
        bool useCoinReceivedEvents)
    {
        if (!tx.TryGetProperty("tx_result", out var txResult) ||
            !txResult.TryGetProperty("events", out var events) ||
            events.ValueKind != JsonValueKind.Array)
            return BigInteger.Zero;

        BigInteger total = BigInteger.Zero;
        foreach (var ev in events.EnumerateArray())
        {
            var type = ev.TryGetProperty("type", out var typeEl) ? typeEl.GetString() ?? "" : "";
            if (useCoinReceivedEvents)
            {
                if (!string.Equals(type, "coin_received", StringComparison.Ordinal))
                    continue;

                var attrs = ReadCometEventAttributes(ev);
                foreach (var entry in ParseCoinEventTuples(attrs, "receiver", denomWantedUpper))
                {
                    if (entry.Party.Equals(recipient, StringComparison.OrdinalIgnoreCase))
                        total += entry.AmountUnits;
                }
            }
            else
            {
                if (!string.Equals(type, "transfer", StringComparison.Ordinal))
                    continue;

                var attrs = ReadCometEventAttributes(ev);
                foreach (var entry in ParseTransferEventTuples(attrs, denomWantedUpper))
                {
                    if (entry.Recipient.Equals(recipient, StringComparison.OrdinalIgnoreCase))
                        total += entry.AmountUnits;
                }
            }
        }

        return total;
    }

    static List<(string Key, string Value)> ReadCometEventAttributes(JsonElement ev)
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

    static async Task<BigInteger> SumHistoricalReceivesAsync(
        Service.ServiceClient txClient,
        string query,
        string recipient,
        string denomWantedUpper,
        int pageLimit,
        int maxPages,
        bool useCoinReceivedEvents)
    {
        BigInteger total = BigInteger.Zero;
        var seenHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        byte[]? key = null;
        ulong offset = 0;
        var useOffset = false;

        for (var page = 1; page <= maxPages; page++)
        {
            var req = new GetTxsEventRequest { Query = query };

#pragma warning disable CS0612
            var pagination = new PageRequest
            {
                Limit = (ulong)pageLimit,
                CountTotal = false
            };

            if (useOffset)
                pagination.Offset = offset;
            else if (key is { Length: > 0 })
                pagination.Key = ByteString.CopyFrom(key);

            req.Pagination = pagination;
#pragma warning restore CS0612

            GetTxsEventResponse response;
            try
            {
                response = await txClient.GetTxsEventAsync(req);
            }
            catch
            {
                break;
            }

            if (response.TxResponses.Count == 0)
                break;

            foreach (var tx in response.TxResponses)
            {
                var hash = tx.Txhash ?? "";
                if (hash.Length > 0 && !seenHashes.Add(hash))
                    continue;

                total += useCoinReceivedEvents
                    ? SumCoinReceivedByRecipient(tx, recipient, denomWantedUpper)
                    : SumTransfersByRecipient(tx, recipient, denomWantedUpper);
            }

#pragma warning disable CS0612
            var nextKey = response.Pagination?.NextKey;
#pragma warning restore CS0612

            if (nextKey is { Length: > 0 })
            {
                key = nextKey.ToByteArray();
                continue;
            }

            if (response.TxResponses.Count == pageLimit)
            {
                useOffset = true;
                offset += (ulong)pageLimit;
                continue;
            }

            break;
        }

        return total;
    }

    static BigInteger SumCoinReceivedByRecipient(TxResponse tx, string recipient, string denomWantedUpper)
    {
        BigInteger total = BigInteger.Zero;

        foreach (var ev in tx.Events)
        {
            if (!string.Equals(ev.Type, "coin_received", StringComparison.Ordinal))
                continue;

            var attrs = ev.Attributes
                .Select(a => (Key: a.Key ?? "", Value: a.Value ?? ""))
                .ToList();

            foreach (var entry in ParseCoinEventTuples(attrs, "receiver", denomWantedUpper))
            {
                if (entry.Party.Equals(recipient, StringComparison.OrdinalIgnoreCase))
                    total += entry.AmountUnits;
            }
        }

        return total;
    }

    static BigInteger SumTransfersByRecipient(TxResponse tx, string recipient, string denomWantedUpper)
    {
        BigInteger total = BigInteger.Zero;

        foreach (var ev in tx.Events)
        {
            if (!string.Equals(ev.Type, "transfer", StringComparison.Ordinal))
                continue;

            var attrs = ev.Attributes
                .Select(a => (Key: a.Key ?? "", Value: a.Value ?? ""))
                .ToList();

            foreach (var entry in ParseTransferEventTuples(attrs, denomWantedUpper))
            {
                if (entry.Recipient.Equals(recipient, StringComparison.OrdinalIgnoreCase))
                    total += entry.AmountUnits;
            }
        }

        return total;
    }

    static List<(string Party, BigInteger AmountUnits)> ParseCoinEventTuples(
        List<(string Key, string Value)> attrs,
        string partyKey,
        string denomWantedUpper)
    {
        var entries = new List<(string Party, BigInteger AmountUnits)>();
        var parties = new List<string>();
        var amountStrings = new List<string>();

        foreach (var (key, value) in attrs)
        {
            if (key == partyKey && !string.IsNullOrWhiteSpace(value))
                parties.Add(value);
            else if (key == "amount" && !string.IsNullOrWhiteSpace(value))
                amountStrings.Add(value);
        }

        var count = Math.Min(parties.Count == 1 ? amountStrings.Count : parties.Count, amountStrings.Count);
        for (var i = 0; i < count; i++)
        {
            var party = parties.Count == 1 ? parties[0] : parties[i];
            var amount = ParseAmountFieldUopt(amountStrings[i], denomWantedUpper);
            if (amount > BigInteger.Zero)
                entries.Add((party, amount));
        }

        return entries;
    }

    /// <summary>
    /// All transfers in a tx_search result. Older MultiSend txs emitted transfer events with no
    /// sender attribute; MultiSend has a single input, so those are attributed to the message sender.
    /// </summary>
    static List<(string Sender, string Recipient, BigInteger AmountUnits)> GetTxTransfers(JsonElement tx, string denomWantedUpper)
    {
        var transfers = new List<(string Sender, string Recipient, BigInteger AmountUnits)>();
        if (!tx.TryGetProperty("tx_result", out var txResult) ||
            !txResult.TryGetProperty("events", out var events) ||
            events.ValueKind != JsonValueKind.Array)
            return transfers;

        string? multiSendSender = null;
        var transferEvents = new List<List<(string Key, string Value)>>();
        foreach (var ev in events.EnumerateArray())
        {
            var type = ev.TryGetProperty("type", out var typeEl) ? typeEl.GetString() ?? "" : "";
            if (string.Equals(type, "transfer", StringComparison.Ordinal))
            {
                transferEvents.Add(ReadCometEventAttributes(ev));
            }
            else if (string.Equals(type, "message", StringComparison.Ordinal))
            {
                var attrs = ReadCometEventAttributes(ev);
                if (attrs.Any(a => a.Key == "action" && a.Value == "/cosmos.bank.v1beta1.MsgMultiSend"))
                    multiSendSender ??= attrs.FirstOrDefault(a => a.Key == "sender").Value;
            }
        }

        foreach (var attrs in transferEvents)
        {
            if (attrs.Any(a => a.Key == "sender"))
            {
                transfers.AddRange(ParseTransferEventTuples(attrs, denomWantedUpper));
                continue;
            }

            if (string.IsNullOrEmpty(multiSendSender))
                continue;

            var recipients = attrs.Where(a => a.Key == "recipient").Select(a => a.Value).ToList();
            var amounts = attrs.Where(a => a.Key == "amount").Select(a => a.Value).ToList();
            for (var i = 0; i < Math.Min(recipients.Count, amounts.Count); i++)
            {
                var amount = ParseAmountFieldUopt(amounts[i], denomWantedUpper);
                if (amount > BigInteger.Zero)
                    transfers.Add((multiSendSender, recipients[i], amount));
            }
        }

        return transfers;
    }

    static List<(string Sender, string Recipient, BigInteger AmountUnits)> ParseTransferEventTuples(
        List<(string Key, string Value)> attrs,
        string denomWantedUpper)
    {
        var entries = new List<(string Sender, string Recipient, BigInteger AmountUnits)>();
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
            if (amount > BigInteger.Zero)
                entries.Add((senders[i], recipients[i], amount));
        }

        return entries;
    }

    static BigInteger ParseAmountFieldUopt(string amountField, string denomWantedUpper)
    {
        if (string.IsNullOrWhiteSpace(amountField))
            return BigInteger.Zero;

        BigInteger total = BigInteger.Zero;
        foreach (var part in amountField.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (TryParseSingleCoinUopt(part, denomWantedUpper, out var amountUnits))
                total += amountUnits;
        }

        return total;
    }

    static bool TryParseSingleCoinUopt(string part, string denomWantedUpper, out BigInteger amountUnits)
    {
        amountUnits = BigInteger.Zero;
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

        return BigInteger.TryParse(trimmed[..i].Replace("_", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out amountUnits);
    }

    static string DecodeBase64Maybe(string value)
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

    static bool IsMostlyPrintable(string value)
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

    static decimal ToDecimalOpt(BigInteger amountUnits, int scaleInt)
    {
        if (amountUnits <= BigInteger.Zero || scaleInt <= 0)
            return 0m;

        return (decimal)amountUnits / scaleInt;
    }

    static int TryParseConfigInt(Dictionary<string, string> cfg, string key, int defaultValue)
    {
        return int.TryParse(cfg.GetValueOrDefault(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : defaultValue;
    }

    static async Task PopulateLiveLockBucketsIfNeededAsync(SummaryCacheEntry summary)
    {
        var hasLockBuckets =
            (summary.Lock6mAmount ?? 0m) > 0m ||
            (summary.Lock12mAmount ?? 0m) > 0m ||
            (summary.Lock18mAmount ?? 0m) > 0m ||
            (summary.Lock24mAmount ?? 0m) > 0m ||
            (summary.Lock6mCount ?? 0) > 0 ||
            (summary.Lock12mCount ?? 0) > 0 ||
            (summary.Lock18mCount ?? 0) > 0 ||
            (summary.Lock24mCount ?? 0) > 0;

        if (hasLockBuckets)
            return;

        try
        {
            var cfg = LoadDashboardConfig();
            var grpcTarget = cfg.GetValueOrDefault("grpc", "127.0.0.1:9090");
            var lcdBase = cfg.GetValueOrDefault("lcd", "http://127.0.0.1:1317");
            var denom = cfg.GetValueOrDefault("denom", "uopt");
            var denomWantedUpper = denom.ToUpperInvariant();
            var scaleInt = denom.StartsWith("u", StringComparison.OrdinalIgnoreCase) ? 1_000_000 : 1;

            using var http = new HttpClient
            {
                BaseAddress = new Uri(lcdBase.TrimEnd('/') + "/"),
                Timeout = TimeSpan.FromSeconds(20)
            };
            using var channel = GrpcChannel.ForAddress($"http://{grpcTarget}");

            var lockService = new LockService(http, channel);
            var lockRows = await lockService.GetAllActiveLockRowsAsync(denomWantedUpper, scaleInt, CancellationToken.None);
            if (lockRows.Count == 0)
                return;

            var now = DateTimeOffset.UtcNow;
            foreach (var row in lockRows)
            {
                var months = GetRemainingMonths(row.EndTime, now);
                if (months <= 6)
                {
                    summary.Lock6mCount = (summary.Lock6mCount ?? 0) + 1;
                    summary.Lock6mAmount = (summary.Lock6mAmount ?? 0m) + row.AmountOpt;
                }
                else if (months <= 12)
                {
                    summary.Lock12mCount = (summary.Lock12mCount ?? 0) + 1;
                    summary.Lock12mAmount = (summary.Lock12mAmount ?? 0m) + row.AmountOpt;
                }
                else if (months <= 18)
                {
                    summary.Lock18mCount = (summary.Lock18mCount ?? 0) + 1;
                    summary.Lock18mAmount = (summary.Lock18mAmount ?? 0m) + row.AmountOpt;
                }
                else if (months <= 24)
                {
                    summary.Lock24mCount = (summary.Lock24mCount ?? 0) + 1;
                    summary.Lock24mAmount = (summary.Lock24mAmount ?? 0m) + row.AmountOpt;
                }
            }
        }
        catch
        {
            // Keep local-cache values if the live lockup endpoint is unavailable.
        }
    }

    static async Task<List<TopWalletEntry>> GetTop100WalletsAsync()
    {
        if (_top100WalletCache is not null &&
            DateTimeOffset.UtcNow - _top100WalletCacheTime < TimeSpan.FromMinutes(10))
        {
            return _top100WalletCache;
        }

        var entries = LoadWalletBalanceEntries();
        var lockBuckets = await GetTopWalletLockBucketsAsync();

        foreach (var (address, buckets) in lockBuckets)
        {
            if (!entries.TryGetValue(address, out var entry))
            {
                entry = new TopWalletEntry { Address = address };
                entries[address] = entry;
            }

            entry.Lock6Months = buckets.Length > 0 ? buckets[0] : 0m;
            entry.Lock12Months = buckets.Length > 1 ? buckets[1] : 0m;
            entry.Lock18Months = buckets.Length > 2 ? buckets[2] : 0m;
            entry.Lock24Months = buckets.Length > 3 ? buckets[3] : 0m;
        }

        await EnrichTopWalletEntriesAsync(entries.Values.Where(w => w.TotalLocked > 0m));

        var result = entries.Values
            .OrderByDescending(w => w.Total)
            .ThenBy(w => w.Address, StringComparer.OrdinalIgnoreCase)
            .Take(100)
            .Select((w, i) =>
            {
                w.Rank = i + 1;
                return w;
            })
            .ToList();

        _top100WalletCache = result;
        _top100WalletCacheTime = DateTimeOffset.UtcNow;
        return result;
    }

    static async Task EnrichTopWalletEntriesAsync(IEnumerable<TopWalletEntry> entries)
    {
        var targets = entries
            .Where(e => !string.IsNullOrWhiteSpace(e.Address))
            .ToList();

        if (targets.Count == 0)
            return;

        try
        {
            var cfg = LoadDashboardConfig();
            var lcdBase = cfg.GetValueOrDefault("lcd", "http://127.0.0.1:1317");
            var denom = cfg.GetValueOrDefault("denom", "uopt");
            var denomWantedUpper = denom.ToUpperInvariant();
            var scaleInt = denom.StartsWith("u", StringComparison.OrdinalIgnoreCase) ? 1_000_000m : 1m;

            using var http = new HttpClient
            {
                BaseAddress = new Uri(lcdBase.TrimEnd('/') + "/"),
                Timeout = TimeSpan.FromSeconds(20)
            };

            var bankService = new BankService(http);
            var stakingService = new StakingService(http);
            using var gate = new SemaphoreSlim(12);
            var tasks = targets.Select(async entry =>
            {
                await gate.WaitAsync();
                try
                {
                    var walletUnits = await bankService.GetBalanceAsync(entry.Address, denomWantedUpper, CancellationToken.None);
                    var stakedUnits = await stakingService.GetDelegationTotalAsync(entry.Address, CancellationToken.None);
                    var unbondingUnits = await stakingService.GetUnbondingTotalAsync(entry.Address, CancellationToken.None);

                    entry.WalletBalance = walletUnits / scaleInt;
                    entry.Staked = stakedUnits / scaleInt;
                    entry.Unbonding = unbondingUnits / scaleInt;
                }
                catch
                {
                    // Keep CSV-derived values if live enrichment fails for this row.
                }
                finally
                {
                    gate.Release();
                }
            });

            await Task.WhenAll(tasks);
        }
        catch
        {
            // Top 100 should still render from CSV and lock data if live enrichment is unavailable.
        }
    }

    static Dictionary<string, TopWalletEntry> LoadWalletBalanceEntries()
    {
        var entries = new Dictionary<string, TopWalletEntry>(StringComparer.OrdinalIgnoreCase);
        var csvPath = Path.Combine(Environment.CurrentDirectory, "wallet-balances.csv");
        if (!File.Exists(csvPath))
            return entries;

        var isHeader = true;
        foreach (var line in File.ReadLines(csvPath))
        {
            if (isHeader)
            {
                isHeader = false;
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
                continue;

            var parts = SplitCsvLine(line);
            if (parts.Count < 4 || string.IsNullOrWhiteSpace(parts[0]))
                continue;

            entries[parts[0].Trim()] = new TopWalletEntry
            {
                Address = parts[0].Trim(),
                WalletBalance = ParseInvariantDecimal(parts[1]),
                Staked = ParseInvariantDecimal(parts[2]),
                Unbonding = ParseInvariantDecimal(parts[3])
            };
        }

        return entries;
    }

    static async Task<Dictionary<string, decimal[]>> GetTopWalletLockBucketsAsync()
    {
        try
        {
            var cfg = LoadDashboardConfig();
            var grpcTarget = cfg.GetValueOrDefault("grpc", "127.0.0.1:9090");
            var lcdBase = cfg.GetValueOrDefault("lcd", "http://127.0.0.1:1317");
            var denom = cfg.GetValueOrDefault("denom", "uopt");
            var denomWantedUpper = denom.ToUpperInvariant();
            var scaleInt = denom.StartsWith("u", StringComparison.OrdinalIgnoreCase) ? 1_000_000 : 1;

            using var http = new HttpClient
            {
                BaseAddress = new Uri(lcdBase.TrimEnd('/') + "/"),
                Timeout = TimeSpan.FromSeconds(20)
            };
            using var channel = GrpcChannel.ForAddress($"http://{grpcTarget}");

            var lockService = new LockService(http, channel);
            var buckets = await lockService.GetAllActiveLockBucketsAsync(
                denomWantedUpper,
                scaleInt,
                DateTimeOffset.UtcNow,
                CancellationToken.None);

            if (buckets.Count > 0)
                return buckets;
        }
        catch
        {
            // Fall back to the local SQLite lock cache below.
        }

        return await GetTopWalletLockBucketsFromDbAsync();
    }

    static async Task<Dictionary<string, decimal[]>> GetTopWalletLockBucketsFromDbAsync()
    {
        var bucketsByAddress = new Dictionary<string, decimal[]>(StringComparer.OrdinalIgnoreCase);
        var dbPath = Path.Combine(Environment.CurrentDirectory, "odata", "optic.db");
        if (!File.Exists(dbPath))
            return bucketsByAddress;

        using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT Address, Amount, UnlockTime FROM Locks WHERE UnlockTime IS NOT NULL";
        using var reader = await command.ExecuteReaderAsync();
        var now = DateTimeOffset.UtcNow;
        while (await reader.ReadAsync())
        {
            var address = reader.IsDBNull(0) ? "" : reader.GetString(0);
            if (string.IsNullOrWhiteSpace(address))
                continue;

            var amountRaw = reader.IsDBNull(1) ? 0m : reader.GetDecimal(1);
            var amount = amountRaw > 10_000_000m ? amountRaw / 1_000_000m : amountRaw;
            if (amount <= 0m)
                continue;

            if (!TryReadDateTimeOffset(reader.GetValue(2), out var unlockTime))
                continue;

            var months = GetRemainingMonths(unlockTime, now);
            var bucket = months <= 6 ? 0 : months <= 12 ? 1 : months <= 18 ? 2 : months <= 24 ? 3 : -1;
            if (bucket < 0)
                continue;

            if (!bucketsByAddress.TryGetValue(address, out var buckets))
            {
                buckets = new decimal[4];
                bucketsByAddress[address] = buckets;
            }

            buckets[bucket] += amount;
        }

        return bucketsByAddress;
    }

    static bool TryReadDateTimeOffset(object value, out DateTimeOffset date)
    {
        date = default;
        if (value is DateTime dt)
        {
            date = new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc));
            return true;
        }

        var text = Convert.ToString(value, CultureInfo.InvariantCulture);
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out date);
    }

    static decimal ParseInvariantDecimal(string value)
    {
        return decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0m;
    }

    static List<string> SplitCsvLine(string line)
    {
        var values = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (ch == ',' && !inQuotes)
            {
                values.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(ch);
            }
        }

        values.Add(current.ToString());
        return values;
    }

    static Dictionary<string, string> LoadDashboardConfig()
    {
        var configPath = Path.Combine(Environment.CurrentDirectory, "optic.conf");
        var cfg = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(configPath))
            return cfg;

        foreach (var rawLine in File.ReadLines(configPath))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                continue;

            var idx = line.IndexOf('=');
            if (idx <= 0)
                continue;

            cfg[line[..idx].Trim()] = line[(idx + 1)..].Trim();
        }

        return cfg;
    }

    static int GetRemainingMonths(DateTimeOffset endTime, DateTimeOffset nowUtc)
    {
        if (endTime <= nowUtc)
            return 0;

        var months = ((endTime.Year - nowUtc.Year) * 12) + endTime.Month - nowUtc.Month;
        if (endTime.Day > nowUtc.Day)
            months++;

        return Math.Max(0, months);
    }

    static string GetPageTitle(string page)
    {
        return page switch
        {
            "dashboard" => "Dashboard",
            "top100" => "Top 100 Wallets",
            "distributions" => "Distributions & Ledger",
            "address-distributions" => "Address Distributions",
            "pnl" => "Profit & Loss",
            "my-wallets" => "My Wallets",
            "locks" => "Locks & Staking",
            "counterparties" => "Counterparties",
            "network" => "Network Analysis",
            "wallet" => "Wallet Analysis",
            "multisend" => "MultiSend Reports",
            "cmc" => "CoinMarketCap Data",
            "custom" => "Custom Arguments",
            "status" => "Node Status",
            "validators" => "Validators & Nodes",
            "sync" => "Data Sync",
            "analytics" => "Daily Statistics",
            "synced-data" => "Synced Data Table",
            "about" => "About OPTIC",
            _ => "Dashboard"
        };
    }

    static string RenderDashboardHtml(string url, string page, Func<string, string> activeLink)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(GetCSSAndHeader(url, page, activeLink));
        
        if (page == "dashboard")
            GetDashboardContent(sb);
        else
            GetPageContent(sb, page);

        sb.Append(@"
      </main>
    </div>
  </div>
</body>
</html>");
        return sb.ToString();
    }

    static void GetCSSAndHeader(System.Text.StringBuilder sb, string url, string page, Func<string, string> activeLink)
    {
        sb.Append(@"<!DOCTYPE html>
<html lang=""en"">
<head>
    <meta charset=""UTF-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <title>OPTIC Dashboard</title>
    <link rel=""icon"" type=""image/png"" href=""/images/optic-icon.png"">
    <style>
        :root {
            color-scheme: light;
            --bg-primary: #f3f2f1;
            --bg-secondary: #ffffff;
            --surface: #ffffff;
            --surface-light: #faf9f8;
            --surface-elevated: #ffffff;
            --border: #d2d0ce;
            --border-strong: #8a8886;
            --text-primary: #1b1a19;
            --text-secondary: #323130;
            --text-muted: #605e5c;
            --accent-primary: #0078d4;
            --accent-secondary: #106ebe;
            --accent-light: #004578;
            --accent-contrast: #ffffff;
            --error: #a4262c;
            --warning: #8a6d00;
            --info: #0078d4;
            --shadow-sm: 0 1px 2px rgba(0, 0, 0, 0.08);
            --shadow-md: 0 8px 24px rgba(0, 0, 0, 0.14);
            --ring: 0 0 0 3px rgba(0, 120, 212, 0.22);
        }

        * {
            margin: 0;
            padding: 0;
            box-sizing: border-box;
        }

        /* Thin Scrollbars */
        ::-webkit-scrollbar {
            width: 6px;
            height: 6px;
        }

        ::-webkit-scrollbar-track {
            background: transparent;
        }

        ::-webkit-scrollbar-thumb {
            background: #c8c6c4;
            border-radius: 3px;
        }

        ::-webkit-scrollbar-thumb:hover {
            background: var(--accent-secondary);
        }

        /* Firefox scrollbar */
        * {
            scrollbar-width: thin;
            scrollbar-color: var(--border) transparent;
        }

        body {
            font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, 'Helvetica Neue', Arial, sans-serif;
            min-width: 320px;
            background-color: var(--bg-primary);
            background-image:
                linear-gradient(rgba(0, 0, 0, 0.025) 1px, transparent 1px),
                linear-gradient(90deg, rgba(0, 0, 0, 0.018) 1px, transparent 1px),
                linear-gradient(180deg, #ffffff 0%, #f3f2f1 100%);
            background-size: 42px 42px, 42px 42px, auto;
            color: var(--text-primary);
            line-height: 1.5;
            font-feature-settings: ""tnum"" 1, ""cv02"" 1;
            text-rendering: optimizeLegibility;
        }

        .app-container {
            display: flex;
            flex-direction: column;
            min-height: 100vh;
        }

        /* Top Navigation */
        .top-nav {
            position: sticky;
            top: 0;
            z-index: 1000;
            background: rgba(255, 255, 255, 0.96);
            border-bottom: 1px solid var(--border);
            box-shadow: 0 2px 10px rgba(0, 0, 0, 0.08);
            backdrop-filter: blur(16px);
        }

        .top-nav-inner {
            display: flex;
            align-items: center;
            justify-content: space-between;
            gap: 18px;
            width: min(1560px, 100%);
            margin: 0 auto;
            padding: 12px 30px;
        }

        .brand-link {
            display: flex;
            align-items: center;
            flex: 0 0 auto;
            text-decoration: none;
            min-width: 172px;
        }

        .brand-link img {
            display: block;
            width: auto;
            height: 42px;
            filter: none;
        }

        .top-menu {
            list-style: none;
            display: flex;
            align-items: center;
            justify-content: center;
            gap: 6px;
            flex: 1 1 auto;
            min-width: 0;
            margin: 0;
            padding: 0;
        }

        .menu-section {
            position: relative;
            padding: 0;
        }

        .menu-label {
            display: inline-flex;
            align-items: center;
            gap: 6px;
            padding: 9px 11px;
            border: 1px solid transparent;
            border-radius: 7px;
            font-size: 13px;
            font-weight: 720;
            color: var(--text-secondary);
            cursor: default;
            white-space: nowrap;
        }

        .menu-label::after {
            content: ""+"";
            color: var(--text-muted);
            font-size: 12px;
            font-weight: 600;
        }

        .menu-section:hover .menu-label,
        .menu-section:focus-within .menu-label {
            background: #f3f2f1;
            border-color: var(--border);
            color: var(--text-primary);
        }

        .menu-dropdown {
            position: absolute;
            left: 0;
            top: calc(100% + 8px);
            display: grid;
            gap: 4px;
            min-width: 208px;
            padding: 8px;
            background: #ffffff;
            border: 1px solid var(--border);
            border-radius: 8px;
            box-shadow: var(--shadow-md);
            opacity: 0;
            visibility: hidden;
            transform: translateY(-4px);
            transition: opacity 0.16s ease, transform 0.16s ease, visibility 0.16s ease;
        }

        .menu-section:hover .menu-dropdown,
        .menu-section:focus-within .menu-dropdown {
            opacity: 1;
            visibility: visible;
            transform: translateY(0);
        }

        .menu-item {
            display: inline-flex;
            align-items: center;
            padding: 9px 11px;
            color: var(--text-secondary);
            text-decoration: none;
            border: 1px solid transparent;
            border-radius: 7px;
            transition: all 0.2s ease;
            font-size: 14px;
            font-weight: 560;
            line-height: 1.3;
            position: relative;
            white-space: nowrap;
        }

        .menu-item:hover {
            background: #f3f2f1;
            border-color: var(--border);
            color: var(--text-primary);
        }

        .menu-item.active {
            background: #eff6fc;
            border-color: #deecf9;
            color: var(--accent-primary);
            font-weight: 700;
            box-shadow: inset 0 -2px 0 var(--accent-primary);
        }

        /* Main Content */
        .main-content {
            flex: 1;
            display: flex;
            flex-direction: column;
            background:
                linear-gradient(180deg, #ffffff, transparent 260px),
                var(--bg-primary);
            min-width: 0;
        }

        header {
            background: #ffffff;
            border-bottom: 1px solid var(--border);
            padding: 18px 30px;
        }

        .header-content {
            display: flex;
            justify-content: space-between;
            align-items: center;
        }

        h1 {
            font-size: 24px;
            font-weight: 780;
            color: var(--text-primary);
            letter-spacing: 0;
        }

        .header-title {
            min-width: 0;
        }

        .header-kicker {
            color: var(--accent-secondary);
            font-size: 11px;
            font-weight: 800;
            letter-spacing: 0.12em;
            text-transform: uppercase;
            margin-bottom: 2px;
        }

        .header-subtitle {
            display: flex;
            align-items: center;
            gap: 12px;
            font-size: 13px;
            color: var(--text-secondary);
        }

        .status-indicator {
            width: 8px;
            height: 8px;
            background: var(--accent-primary);
            border-radius: 50%;
            animation: pulse 2s infinite;
        }

        @keyframes pulse {
            0%, 100% { opacity: 1; }
            50% { opacity: 0.5; }
        }

        .status-badge {
            display: inline-flex;
            align-items: center;
            gap: 8px;
            background: #eff6fc;
            padding: 5px 10px;
            border-radius: 999px;
            border: 1px solid var(--border);
            color: var(--accent-primary);
            font-weight: 600;
            font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
        }

        main {
            flex: 1;
            overflow-y: auto;
            padding: 30px;
        }

        .section-title {
            margin: 28px 0 14px;
            font-size: 18px;
            color: var(--text-primary);
            font-weight: 780;
            letter-spacing: 0;
        }

        /* Dashboard Grid */
        .metrics-grid {
            display: grid;
            grid-template-columns: repeat(auto-fit, minmax(235px, 1fr));
            gap: 14px;
            margin-bottom: 30px;
        }

        .metric-card {
            background:
                linear-gradient(180deg, #ffffff, #fbfbfb),
                var(--surface);
            border: 1px solid var(--border);
            border-radius: 8px;
            padding: 18px;
            transition: border-color 0.2s ease, background 0.2s ease, box-shadow 0.2s ease, transform 0.2s ease;
            box-shadow: var(--shadow-sm);
            min-height: 112px;
            position: relative;
            overflow: hidden;
        }

        .metric-card::before {
            content: """";
            position: absolute;
            left: 0;
            right: 0;
            top: 0;
            height: 2px;
            background: linear-gradient(90deg, var(--accent-primary), var(--accent-secondary));
            opacity: 0.72;
        }

        .metric-card:hover {
            background:
                linear-gradient(180deg, #ffffff, #f8fbff),
                var(--surface-light);
            border-color: rgba(0, 120, 212, 0.45);
            transform: translateY(-1px);
            box-shadow: var(--shadow-md);
        }

        .metric-label {
            font-size: 12px;
            font-weight: 750;
            text-transform: uppercase;
            letter-spacing: 0.075em;
            color: var(--text-secondary);
            margin-bottom: 8px;
        }

        .metric-value {
            font-size: 26px;
            font-weight: 790;
            color: var(--accent-primary);
            overflow-wrap: anywhere;
            letter-spacing: 0;
            line-height: 1.12;
        }

        /* Report Cards */
        .report-cards {
            display: grid;
            grid-template-columns: repeat(auto-fit, minmax(300px, 1fr));
            gap: 16px;
        }

        .card {
            background:
                linear-gradient(180deg, #ffffff, #fbfbfb),
                var(--surface);
            border: 1px solid var(--border);
            border-radius: 8px;
            overflow: hidden;
            transition: border-color 0.2s ease, box-shadow 0.2s ease, transform 0.2s ease;
            box-shadow: var(--shadow-sm);
        }

        .card:hover {
            border-color: rgba(0, 120, 212, 0.34);
            box-shadow: var(--shadow-md);
        }

        .card h2 {
            background: #faf9f8;
            padding: 15px 16px;
            font-size: 16px;
            font-weight: 720;
            color: var(--text-primary);
            border-bottom: 1px solid var(--border);
            margin: 0;
        }

        .card-content {
            padding: 16px;
        }

        .totals-grid {
            display: grid;
            grid-template-columns: repeat(auto-fit, minmax(220px, 1fr));
            gap: 12px;
        }

        .total-item {
            background: #ffffff;
            border: 1px solid var(--border);
            border-radius: 8px;
            padding: 12px;
        }

        .total-item label {
            display: block;
            font-size: 11px;
            text-transform: uppercase;
            letter-spacing: 0.08em;
            color: var(--text-secondary);
            margin-bottom: 6px;
        }

        .total-item .total-value {
            font-size: 18px;
            font-weight: 700;
            color: var(--text-primary);
        }

        .form-group {
            margin-bottom: 16px;
        }

        .form-group:last-child {
            margin-bottom: 0;
        }

        label {
            display: block;
            margin-bottom: 8px;
            font-size: 13px;
            font-weight: 500;
            color: var(--text-primary);
        }

        input[type=""text""],
        input[type=""number""],
        select {
            width: 100%;
            padding: 11px 12px;
            border: 1px solid var(--border);
            border-radius: 7px;
            background: #ffffff;
            color: var(--text-primary);
            font-size: 13px;
            transition: border-color 0.2s ease, box-shadow 0.2s ease, background 0.2s ease;
        }

        input[type=""text""]:focus,
        input[type=""number""]:focus,
        select:focus {
            outline: none;
            border-color: var(--accent-primary);
            box-shadow: var(--ring);
            background: #ffffff;
        }

        .checkbox-group {
            display: grid;
            grid-template-columns: 1fr 1fr;
            gap: 12px;
            margin: 12px 0;
        }

        .checkbox-wrapper {
            display: flex;
            align-items: center;
            gap: 8px;
        }

        input[type=""checkbox""] {
            width: 16px;
            height: 16px;
            cursor: pointer;
            accent-color: var(--accent-primary);
        }

        .checkbox-wrapper label {
            margin: 0;
            cursor: pointer;
            font-weight: 400;
        }

        button {
            background: var(--accent-primary);
            color: var(--accent-contrast);
            border: none;
            padding: 10px 16px;
            border-radius: 7px;
            font-weight: 760;
            font-size: 13px;
            cursor: pointer;
            transition: background 0.2s ease, box-shadow 0.2s ease, transform 0.2s ease;
            text-transform: uppercase;
            letter-spacing: 0.06em;
            box-shadow: 0 10px 22px rgba(24, 184, 116, 0.18);
        }

        button:hover {
            background: var(--accent-secondary);
            transform: translateY(-2px);
            box-shadow: 0 14px 28px rgba(24, 184, 116, 0.24);
        }

        button:active {
            transform: translateY(0);
        }

        .header-links {
            display: flex;
            gap: 12px;
            align-items: center;
            flex-wrap: wrap;
            justify-content: flex-end;
        }

        .header-link {
            color: var(--text-secondary);
            text-decoration: none;
            font-size: 14px;
            transition: color 0.2s ease;
            font-weight: 650;
            padding: 7px 9px;
            border-radius: 7px;
        }

        .header-link:hover {
            color: var(--accent-primary);
            background: rgba(255, 255, 255, 0.045);
        }

        .btn-donate {
            background: var(--accent-primary);
            color: var(--accent-contrast);
            border: none;
            padding: 9px 16px;
            border-radius: 7px;
            font-weight: 760;
            cursor: pointer;
            transition: background 0.2s ease;
        }

        .btn-donate:hover {
            background: var(--accent-secondary);
        }

        /* Modal */
        .modal {
            display: none;
            position: fixed;
            z-index: 2000;
            left: 0;
            top: 0;
            width: 100%;
            height: 100%;
            background-color: rgba(0, 0, 0, 0.74);
            backdrop-filter: blur(10px);
            align-items: center;
            justify-content: center;
        }

        .modal.show {
            display: flex;
        }

        .modal-content {
            background-color: var(--bg-secondary);
            padding: 32px;
            border-radius: 8px;
            border: 1px solid var(--border);
            text-align: center;
            max-width: 500px;
            color: var(--text-primary);
            box-shadow: var(--shadow-md);
        }

        .modal-close {
            background: none;
            border: none;
            color: var(--text-secondary);
            font-size: 28px;
            cursor: pointer;
            float: right;
            transition: color 0.2s ease;
        }

        .modal-close:hover {
            color: var(--text-primary);
        }

        .qr-code {
            margin: 20px 0;
        }

        .qr-code img {
            max-width: 300px;
            width: 100%;
            border-radius: 8px;
        }

        .donation-address {
            background: var(--surface);
            padding: 16px;
            border-radius: 8px;
            border: 1px solid var(--border);
            margin: 16px 0;
            word-break: break-all;
            font-size: 12px;
            color: var(--text-secondary);
            font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
        }

        table {
            border-collapse: collapse;
            width: 100%;
        }

        th {
            color: var(--text-secondary);
            font-size: 12px;
            text-transform: uppercase;
            letter-spacing: 0.045em;
        }

        td {
            color: var(--text-primary);
        }

        tbody tr {
            transition: background 0.16s ease;
        }

        tbody tr:hover {
            background: #f3f2f1 !important;
        }

        .data-table-wrap {
            width: 100%;
            overflow-x: auto;
            overflow-y: visible;
        }

        .top100-table-wrap {
            max-height: calc(100vh - 150px);
            overflow: auto;
            border: 1px solid var(--border);
            border-radius: 8px;
        }

        #top100-status[hidden] {
            display: none;
        }

        .data-table {
            min-width: 1080px;
            font-size: 13px;
        }

        .data-table th,
        .data-table td {
            padding: 11px 12px;
            border-bottom: 1px solid var(--border);
        }

        .data-table th {
            background: var(--surface-light);
            position: sticky;
            top: 67px;
            z-index: 20;
            text-align: right;
            box-shadow: 0 1px 0 var(--border), 0 2px 8px rgba(0, 0, 0, 0.06);
        }

        .memo-cell {
            max-width: 260px;
            overflow: hidden;
            text-overflow: ellipsis;
            white-space: nowrap;
            color: var(--text-secondary);
        }

        .top100-table-wrap .data-table th,
        #addr-dist-table-wrap .data-table th,
        .pnl-table-wrap .data-table th {
            top: 0;
            z-index: 30;
        }

        .data-table th.sortable {
            cursor: pointer;
            user-select: none;
        }

        .data-table th.sortable:hover {
            background: #eff6fc;
            color: var(--accent-primary);
        }

        .data-table th.sortable::after {
            content: "" ↕"";
            color: var(--text-muted);
            font-size: 11px;
        }

        .data-table th.sortable.sort-asc::after {
            content: "" ↑"";
            color: var(--accent-primary);
        }

        .data-table th.sortable.sort-desc::after {
            content: "" ↓"";
            color: var(--accent-primary);
        }

        .data-table th:first-child,
        .data-table th:nth-child(2),
        .data-table td:first-child,
        .data-table td:nth-child(2) {
            text-align: left;
        }

        .data-table .rank-cell {
            width: 92px;
            min-width: 92px;
            color: var(--text-secondary);
            font-weight: 700;
        }

        .data-table .address-cell {
            font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
            font-size: 12px;
            color: var(--accent-light);
            white-space: nowrap;
        }

        .data-table .amount-cell {
            text-align: right;
            font-variant-numeric: tabular-nums;
            white-space: nowrap;
        }

        .form-row {
            display: grid;
            grid-template-columns: 1fr 1fr;
            gap: 12px;
        }

        .form-row-3 {
            display: grid;
            grid-template-columns: 1fr 1fr 1fr;
            gap: 12px;
        }

        /* Results */
        .result-container {
            background: linear-gradient(180deg, rgba(255, 255, 255, 0.026), rgba(255, 255, 255, 0.008)), var(--surface);
            border: 1px solid var(--border);
            border-radius: 8px;
            padding: 20px;
            margin-bottom: 20px;
        }

        .result-header {
            display: flex;
            justify-content: space-between;
            align-items: center;
            margin-bottom: 16px;
            padding-bottom: 12px;
            border-bottom: 1px solid var(--border);
        }

        .result-header h2 {
            margin: 0;
            font-size: 18px;
            color: var(--text-primary);
        }

        .status-badge-success {
            background: rgba(16, 185, 129, 0.2);
            color: var(--accent-primary);
            padding: 4px 12px;
            border-radius: 20px;
            font-size: 12px;
            font-weight: 600;
        }

        .status-badge-error {
            background: rgba(239, 68, 68, 0.2);
            color: #ff6b6b;
            padding: 4px 12px;
            border-radius: 20px;
            font-size: 12px;
            font-weight: 600;
        }

        .output-log {
            background: var(--bg-primary);
            border: 1px solid var(--border);
            border-radius: 7px;
            padding: 12px;
            font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
            font-size: 12px;
            color: var(--text-secondary);
            max-height: 300px;
            overflow-y: auto;
            margin-bottom: 12px;
            line-height: 1.4;
        }

        .file-list {
            display: grid;
            gap: 8px;
        }

        .file-item {
            background: var(--bg-primary);
            border: 1px solid var(--border);
            border-radius: 7px;
            padding: 12px;
            display: flex;
            justify-content: space-between;
            align-items: center;
        }

        .file-item a {
            color: var(--accent-primary);
            text-decoration: none;
            font-weight: 500;
            word-break: break-all;
        }

        .file-item a:hover {
            text-decoration: underline;
        }

        .back-link {
            color: var(--accent-primary);
            text-decoration: none;
            font-weight: 500;
            display: inline-block;
            margin-top: 12px;
        }

        .back-link:hover {
            text-decoration: underline;
        }

        /* Responsive */
        @media (max-width: 1120px) {
            .top-nav-inner {
                align-items: flex-start;
                flex-wrap: wrap;
                padding: 12px 18px;
            }

            .top-menu {
                order: 3;
                width: 100%;
                justify-content: flex-start;
                overflow-x: auto;
                padding-bottom: 2px;
            }

            .menu-dropdown {
                position: fixed;
                top: auto;
                left: 18px;
                right: 18px;
                width: auto;
            }
        }

        @media (max-width: 768px) {
            .form-row {
                grid-template-columns: 1fr;
            }

            .form-row-3 {
                grid-template-columns: 1fr;
            }

            .checkbox-group {
                grid-template-columns: 1fr;
            }

            .metrics-grid {
                grid-template-columns: 1fr;
            }

            .report-cards {
                grid-template-columns: 1fr;
            }

            header {
                padding: 16px;
            }

            .top-nav-inner {
                gap: 10px;
                padding: 10px 14px;
            }

            .data-table th {
                top: 106px;
            }

            .brand-link {
                min-width: 132px;
            }

            .brand-link img {
                height: 34px;
            }

            .top-menu {
                gap: 4px;
            }

            .menu-label,
            .menu-item {
                padding: 8px 9px;
                font-size: 13px;
            }

            main {
                padding: 16px;
            }

            h1 {
                font-size: 20px;
            }

            .header-content {
                flex-direction: column;
                align-items: flex-start;
                gap: 12px;
            }

            .header-subtitle {
                flex-wrap: wrap;
            }

            .header-links {
                width: 100%;
                justify-content: flex-start;
            }

            .status-badge {
                max-width: 100%;
            }
        }

        /* Busy state for buttons that wait on the server, plus an indeterminate progress bar. */
        button.is-busy {
            cursor: progress;
            opacity: 0.85;
        }

        button.is-busy::before {
            content: '';
            display: inline-block;
            width: 12px;
            height: 12px;
            margin-right: 8px;
            vertical-align: -2px;
            border: 2px solid currentColor;
            border-right-color: transparent;
            border-radius: 50%;
            animation: optic-spin 0.7s linear infinite;
        }

        .optic-progress {
            display: flex;
            align-items: center;
            gap: 10px;
            margin: 6px 0 10px;
        }

        .optic-progress-track {
            position: relative;
            flex: 1;
            max-width: 420px;
            height: 4px;
            border-radius: 2px;
            background: var(--border);
            overflow: hidden;
        }

        .optic-progress-fill {
            position: absolute;
            top: 0;
            left: 0;
            height: 100%;
            width: 35%;
            border-radius: 2px;
            background: var(--accent-primary);
            animation: optic-indeterminate 1.4s ease-in-out infinite;
        }

        .optic-progress-text {
            font-size: 12px;
            color: var(--text-secondary);
            font-variant-numeric: tabular-nums;
        }

        @keyframes optic-spin {
            to { transform: rotate(360deg); }
        }

        @keyframes optic-indeterminate {
            0% { left: -35%; }
            100% { left: 100%; }
        }

        @media (prefers-reduced-motion: reduce) {
            button.is-busy::before { animation-duration: 2s; }
            .optic-progress-fill { animation-duration: 4s; }
        }
    </style>
</head>
<body>
    <script>
        // Marks a button busy and shows a progress bar with elapsed time after the anchor element.
        // Returns a function that clears it.
        window.opticBusy = function (button, anchor, message) {
            const label = message || 'Working';
            if (button) {
                button.disabled = true;
                button.classList.add('is-busy');
                button.setAttribute('aria-busy', 'true');
            }
            const bar = document.createElement('div');
            bar.className = 'optic-progress';
            bar.setAttribute('role', 'progressbar');
            bar.setAttribute('aria-label', label);
            bar.innerHTML = '<div class=""optic-progress-track""><div class=""optic-progress-fill""></div></div><span class=""optic-progress-text"" aria-live=""off""></span>';
            if (anchor) anchor.insertAdjacentElement('afterend', bar);
            const text = bar.querySelector('.optic-progress-text');
            const started = Date.now();
            const tick = () => { text.textContent = label + '... ' + Math.floor((Date.now() - started) / 1000) + 's'; };
            tick();
            const timer = setInterval(tick, 1000);
            return function () {
                clearInterval(timer);
                bar.remove();
                if (button) {
                    button.disabled = false;
                    button.classList.remove('is-busy');
                    button.removeAttribute('aria-busy');
                }
            };
        };

        // Report forms post to /run and wait for the CLI; show progress until the result page loads.
        document.addEventListener('submit', function (e) {
            const form = e.target;
            if (!form.matches('form[action=""/run""]')) return;
            const button = form.querySelector('button[type=""submit""], button:not([type])');
            // Defer so the submit button's value still goes with the form.
            setTimeout(function () { window.opticBusy(button, form, 'Running report'); }, 0);
        });

        // Coming back via the browser's back button can restore the busy state; clear it.
        window.addEventListener('pageshow', function (e) {
            if (!e.persisted) return;
            document.querySelectorAll('.optic-progress').forEach(function (el) { el.remove(); });
            document.querySelectorAll('button.is-busy').forEach(function (b) {
                b.disabled = false;
                b.classList.remove('is-busy');
                b.removeAttribute('aria-busy');
            });
        });
    </script>
    <div class=""app-container"">
        <nav class=""top-nav"">
            <div class=""top-nav-inner"">
                <a href=""/"" class=""brand-link"" aria-label=""OPTIC Dashboard"">
                    <img src=""/images/optic-logo-color.png"" alt=""OPTIC Logo"" />
                </a>
                <ul class=""top-menu"">
                    <li class=""menu-section"">
                    <a href=""/"" class=""menu-item" + (page == "dashboard" ? @""" active" : @"""") + @""">Dashboard</a>
                    </li>
                    <li class=""menu-section"">
                        <a href=""/page/top100"" class=""menu-item" + (page == "top100" ? @""" active" : @"""") + @""">Top 100 Wallets</a>
                    </li>
                    <li class=""menu-section"">
                        <span class=""menu-label"">Reports</span>
                        <div class=""menu-dropdown"">
                            <a href=""/page/distributions"" class=""menu-item" + (page == "distributions" ? @""" active" : @"""") + @""">Distributions</a>
                            <a href=""/page/address-distributions"" class=""menu-item" + (page == "address-distributions" ? @""" active" : @"""") + @""">Address Distributions</a>
                            <a href=""/page/pnl"" class=""menu-item" + (page == "pnl" ? @""" active" : @"""") + @""">Profit &amp; Loss</a>
                            <a href=""/page/my-wallets"" class=""menu-item" + (page == "my-wallets" ? @""" active" : @"""") + @""">My Wallets</a>
                            <a href=""/page/locks"" class=""menu-item" + (page == "locks" ? @""" active" : @"""") + @""">Locks & Staking</a>
                            <a href=""/page/counterparties"" class=""menu-item" + (page == "counterparties" ? @""" active" : @"""") + @""">Counterparties</a>
                            <a href=""/page/network"" class=""menu-item" + (page == "network" ? @""" active" : @"""") + @""">Network</a>
                            <a href=""/page/wallet"" class=""menu-item" + (page == "wallet" ? @""" active" : @"""") + @""">Wallet</a>
                        </div>
                    </li>
                    <li class=""menu-section"">
                        <span class=""menu-label"">Advanced</span>
                        <div class=""menu-dropdown"">
                            <a href=""/page/multisend"" class=""menu-item" + (page == "multisend" ? @""" active" : @"""") + @""">Multisend</a>
                            <a href=""/page/cmc"" class=""menu-item" + (page == "cmc" ? @""" active" : @"""") + @""">CMC Data</a>
                            <a href=""/page/custom"" class=""menu-item" + (page == "custom" ? @""" active" : @"""") + @""">Custom Args</a>
                        </div>
                    </li>
                    <li class=""menu-section"">
                        <span class=""menu-label"">Analytics</span>
                        <div class=""menu-dropdown"">
                            <a href=""/page/analytics"" class=""menu-item" + (page == "analytics" ? @""" active" : @"""") + @""">Daily Stats</a>
                            <a href=""/page/synced-data"" class=""menu-item" + (page == "synced-data" ? @""" active" : @"""") + @""">Synced Data Table</a>
                        </div>
                    </li>
                    <li class=""menu-section"">
                        <span class=""menu-label"">System</span>
                        <div class=""menu-dropdown"">
                            <a href=""/page/status"" class=""menu-item" + (page == "status" ? @""" active" : @"""") + @""">Node Status</a>
                            <a href=""/page/validators"" class=""menu-item" + (page == "validators" ? @""" active" : @"""") + @""">Validators</a>
                            <a href=""/page/sync"" class=""menu-item" + (page == "sync" ? @""" active" : @"""") + @""">Data Sync</a>
                        </div>
                    </li>
                </ul>
                <div class=""header-links"">
                    <span class=""status-badge""><span class=""status-indicator""></span>Local Dashboard</span>
                    <a href=""/page/about"" class=""header-link"">About</a>
                    <button class=""btn-donate"" onclick=""openDonateModal()"">Donate</button>
                </div>
            </div>
        </nav>
        
        <div class=""main-content"">
            <header>
                <div class=""header-content"">
                    <div class=""header-title"">
                        <div class=""header-kicker"">Optio Protocol Intelligence</div>
                        <h1>" + GetPageTitle(page) + @"</h1>
                    </div>
                </div>
            </header>
            
            <main>
                <div id=""donateModal"" class=""modal"">
                    <div class=""modal-content"">
                        <button class=""modal-close"" onclick=""closeDonateModal()"">&times;</button>
                        <h2>Support OPTIC</h2>
                        <p>Donations help us pay for power and server costs to keep OPTIC running.</p>
                        <div class=""qr-code"">
                            <img src=""https://api.qrserver.com/v1/create-qr-code/?size=300x300&data=optio14ytvx9n5ps62l6pkuzw7n39jzdkq4dngajdrhz"" alt=""Donation QR Code"">
                        </div>
                        <p style=""margin: 16px 0; font-size: 12px;"">Scan to donate to:</p>
                        <div class=""donation-address"">optio14ytvx9n5ps62l6pkuzw7n39jzdkq4dngajdrhz</div>
                    </div>
                </div>
                <script>
                    function openDonateModal() {
                        document.getElementById('donateModal').classList.add('show');
                    }
                    function closeDonateModal() {
                        document.getElementById('donateModal').classList.remove('show');
                    }
                    window.onclick = function(event) {
                        const modal = document.getElementById('donateModal');
                        if (event.target == modal) {
                            modal.classList.remove('show');
                        }
                    }
                </script>");
    }

    static string GetCSSAndHeader(string url, string page, Func<string, string> activeLink)
    {
        var sb = new System.Text.StringBuilder();
        GetCSSAndHeader(sb, url, page, activeLink);
        return sb.ToString();
    }

    static void GetDashboardContent(System.Text.StringBuilder sb)
    {
        sb.Append(@"
                <div class=""metrics-grid"">
                    <div class=""metric-card"">
                        <div class=""metric-label"">Total Wallet Count</div>
                        <div class=""metric-value"" id=""metric-total-wallets"">--</div>
                    </div>
                    <div class=""metric-card"">
                        <div class=""metric-label"">Total Banked Amount</div>
                        <div class=""metric-value"" id=""metric-total-banked"">--</div>
                    </div>
                    <div class=""metric-card"">
                        <div class=""metric-label"">Total Staked Amount</div>
                        <div class=""metric-value"" id=""metric-total-staked"">--</div>
                    </div>
                    <div class=""metric-card"">
                        <div class=""metric-label"">Total Locked Amount</div>
                        <div class=""metric-value"" id=""metric-total-locked"">--</div>
                    </div>
                    <div class=""metric-card"">
                        <div class=""metric-label"">Total Emitted Amount</div>
                        <div class=""metric-value"" id=""metric-total-emitted"">--</div>
                    </div>
                    <div class=""metric-card"">
                        <div class=""metric-label"">Total Distributed Amount</div>
                        <div class=""metric-value"" id=""metric-total-distributed"">--</div>
                    </div>
                </div>
                <h2 class=""section-title"">Lock Period Summary</h2>
                <div class=""metrics-grid"">
                    <div class=""metric-card"">
                        <div class=""metric-label"">6 Months</div>
                        <div class=""metric-value"" id=""metric-lock-6m-count"">--</div>
                        <div class=""metric-label"" id=""metric-lock-6m-amount"">--</div>
                    </div>
                    <div class=""metric-card"">
                        <div class=""metric-label"">12 Months</div>
                        <div class=""metric-value"" id=""metric-lock-12m-count"">--</div>
                        <div class=""metric-label"" id=""metric-lock-12m-amount"">--</div>
                    </div>
                    <div class=""metric-card"">
                        <div class=""metric-label"">18 Months</div>
                        <div class=""metric-value"" id=""metric-lock-18m-count"">--</div>
                        <div class=""metric-label"" id=""metric-lock-18m-amount"">--</div>
                    </div>
                    <div class=""metric-card"">
                        <div class=""metric-label"">24 Months</div>
                        <div class=""metric-value"" id=""metric-lock-24m-count"">--</div>
                        <div class=""metric-label"" id=""metric-lock-24m-amount"">--</div>
                    </div>
                </div>
                <script>
                    (async function () {
                        try {
                            const response = await fetch('/api/summary');
                            const data = await response.json();
                            const summary = data.summary || {};
                            const totalWallets = summary.totalWallets || 0;
                            const banked = summary.totalLiquid || 0;
                            const staked = summary.totalStaked || 0;
                            const locked = summary.totalLocked || 0;
                            const distributed = summary.totalDistributed || (banked + staked);
                            const emitted = summary.totalEmitted || 0;
                            const formatAmt = (value) => Math.round(value || 0).toLocaleString('en-US');
                            const formatCount = (value) => (value || 0).toLocaleString('en-US');

                            const setText = (id, value) => {
                                const el = document.getElementById(id);
                                if (el) el.textContent = value;
                            };

                            setText('metric-total-wallets', totalWallets.toLocaleString());
                            setText('metric-total-banked', formatAmt(banked));
                            setText('metric-total-staked', formatAmt(staked));
                            setText('metric-total-locked', formatAmt(locked));
                            setText('metric-total-emitted', formatAmt(emitted));
                            setText('metric-total-distributed', formatAmt(distributed));
                            setText('metric-lock-6m-count', formatCount(summary.lock6mCount));
                            setText('metric-lock-12m-count', formatCount(summary.lock12mCount));
                            setText('metric-lock-18m-count', formatCount(summary.lock18mCount));
                            setText('metric-lock-24m-count', formatCount(summary.lock24mCount));
                            setText('metric-lock-6m-amount', formatAmt(summary.lock6mAmount) + ' OPT');
                            setText('metric-lock-12m-amount', formatAmt(summary.lock12mAmount) + ' OPT');
                            setText('metric-lock-18m-amount', formatAmt(summary.lock18mAmount) + ' OPT');
                            setText('metric-lock-24m-amount', formatAmt(summary.lock24mAmount) + ' OPT');
                        } catch (error) {
                            // leave placeholders on error
                        }
                    })();
                </script>

                <h2 class=""section-title"">Wallet Overview</h2>
                <div class=""report-cards"">
");
        AppendPriceChartCard(sb);
        AppendWalletGrowthCard(sb);
        AppendDailyStatsCard(sb);
        sb.Append(@"
                </div>
");
    }

    static void GetPageContent(System.Text.StringBuilder sb, string page)
    {
        sb.Append(@"
                <div class=""report-cards"">
");
        switch (page)
        {
            case "top100":
                AppendTop100Card(sb);
                break;
            case "distributions":
                AppendDailyDistributionReportCard(sb);
                AppendDistributionsCard(sb);
                AppendCounterpartiesCard(sb);
                break;
            case "address-distributions":
                AppendAddressDistributionsCard(sb);
                break;
            case "pnl":
                AppendProfitLossCard(sb);
                break;
            case "my-wallets":
                AppendMyWalletsCard(sb);
                break;
            case "locks":
                AppendLocksCard(sb);
                AppendLocksSummaryCard(sb);
                AppendLockExtendedCard(sb);
                AppendWalletLocksSummaryCard(sb);
                break;
            case "counterparties":
                AppendCounterpartiesCard(sb);
                AppendSendRecvCard(sb);
                break;
            case "network":
                AppendNetworkTotalsCard(sb);
                AppendDryTotalsCard(sb);
                AppendStatusCard(sb);
                AppendValidatorsNodesCard(sb);
                AppendWalletCountCard(sb);
                break;
            case "wallet":
                AppendWalletBalancesCard(sb);
                AppendWalletLocksReportCard(sb);
                AppendTotalStakedCard(sb);
                AppendTotalDistributedCard(sb);
                AppendTotalsAllCard(sb);
                break;
            case "multisend":
                AppendMultiSendSumCard(sb);
                AppendBlockScanMultiSendCard(sb);
                break;
            case "cmc":
                AppendCmcDailyCard(sb);
                break;
            case "custom":
                AppendCustomArgsCard(sb);
                break;
            case "status":
                AppendStatusCard(sb);
                break;
            case "validators":
                AppendValidatorsNodesCard(sb);
                break;
            case "sync":
                AppendDataSyncCard(sb);
                break;
            case "analytics":
                break;
            case "synced-data":
                AppendSyncedDataTableCard(sb);
                break;
            case "about":
                AppendAboutCard(sb);
                break;
        }
        sb.Append(@"
                </div>
");
    }

    // Report Card Methods
    static void AppendDailyDistributionReportCard(System.Text.StringBuilder sb)
    {
        sb.Append(@"
                    <div class=""card"" style=""grid-column: 1 / -1;"">
                        <h2>Daily Distributions</h2>
                        <div class=""card-content"">
                            <div style=""display: flex; justify-content: space-between; align-items: center; gap: 12px; margin-bottom: 8px; flex-wrap: wrap;"">
                                <div id=""daily-distribution-status"" style=""color: var(--text-secondary); font-size: 13px;"">Loading distributions from optio103r5...</div>
                                <div id=""daily-distribution-totals"" style=""color: var(--text-secondary); font-size: 13px;""></div>
                            </div>
                            <div id=""daily-distribution-chart"" style=""height: 300px; border: 1px solid var(--border); border-radius: 8px; background: var(--surface-light); display: flex; align-items: center; justify-content: center; color: var(--text-secondary); position: relative;"">
                                Loading distribution chart...
                            </div>
                            <script>
                                (function () {
                                    const statusEl = document.getElementById('daily-distribution-status');
                                    const totalsEl = document.getElementById('daily-distribution-totals');
                                    const chartEl = document.getElementById('daily-distribution-chart');

                                    const formatOpt = (value) => {
                                        const numeric = Number(value || 0);
                                        return Math.round(numeric).toLocaleString(undefined, { maximumFractionDigits: 0 });
                                    };

                                    const formatDate = (value) => {
                                        const parts = (value || '').split('-');
                                        if (parts.length !== 3) return value || '';
                                        const date = new Date(Date.UTC(Number(parts[0]), Number(parts[1]) - 1, Number(parts[2])));
                                        return date.toLocaleDateString('en-US', { year: 'numeric', month: 'short', day: '2-digit' });
                                    };

                                    const parseDate = (value) => {
                                        const parts = (value || '').split('-');
                                        if (parts.length === 3) {
                                            return new Date(Date.UTC(Number(parts[0]), Number(parts[1]) - 1, Number(parts[2])));
                                        }
                                        return new Date(value);
                                    };

                                    async function loadDailyDistributionReport() {
                                        try {
                                            const response = await fetch('/api/distributions/daily');
                                            const data = await response.json();
                                            const rows = (data.rows || [])
                                                .slice()
                                                .sort((a, b) => String(a.date || '').localeCompare(String(b.date || '')));

                                            if (rows.length === 0) {
                                                chartEl.innerHTML = '<div style=""color: var(--text-secondary);"">No distribution data found</div>';
                                                statusEl.textContent = 'No outgoing distribution transfers found for optio103r5...';
                                                totalsEl.textContent = '';
                                                return;
                                            }

                                            const amounts = rows.map(r => Number(r.distributedOpt || 0));
                                            const wallets = rows.map(r => Number(r.walletCount || 0));
                                            const totalAmount = amounts.reduce((sum, value) => sum + value, 0);
                                            const maxAmount = Math.max(...amounts, 1);
                                            const maxWallets = Math.max(...wallets, 1);
                                            const width = 1100;
                                            const height = 240;
                                            const padL = 68;
                                            const padR = 68;
                                            const padT = 16;
                                            const padB = 32;
                                            const plotW = width - padL - padR;
                                            const plotH = height - padT - padB;
                                            const xStep = rows.length > 1 ? plotW / (rows.length - 1) : 0;
                                            const toAmountY = (v) => padT + plotH - (v / maxAmount) * plotH;
                                            const toWalletY = (v) => padT + plotH - (v / maxWallets) * plotH;
                                            const xFor = (i) => padL + i * xStep;
                                            const amountPoints = amounts.map((v, i) => `${xFor(i)},${toAmountY(v)}`).join(' ');
                                            const walletPoints = wallets.map((v, i) => `${xFor(i)},${toWalletY(v)}`).join(' ');
                                            const gridLines = [0, 0.25, 0.5, 0.75, 1].map(p => {
                                                const y = padT + p * plotH;
                                                return `<line x1=""${padL}"" y1=""${y}"" x2=""${width - padR}"" y2=""${y}"" stroke=""var(--border)"" stroke-width=""1"" />`;
                                            }).join('');

                                            const maxTicks = 8;
                                            const tickStep = rows.length > maxTicks ? Math.ceil(rows.length / maxTicks) : 1;
                                            const xTickLabels = rows
                                                .map((row, idx) => ({ row, idx }))
                                                .filter(item => item.idx % tickStep === 0 || item.idx === rows.length - 1)
                                                .map(item => {
                                                    const d = parseDate(item.row.date);
                                                    const label = isNaN(d.getTime()) ? item.row.date : d.toLocaleDateString('en-US', { month: 'short', day: '2-digit' });
                                                    return `<text x=""${xFor(item.idx)}"" y=""${height - 8}"" fill=""var(--text-secondary)"" font-size=""10"" text-anchor=""middle"">${label}</text>`;
                                                }).join('');

                                            chartEl.innerHTML = `
<svg viewBox=""0 0 ${width} ${height}"" width=""100%"" height=""100%"" preserveAspectRatio=""xMinYMid meet"">
  <rect x=""0"" y=""0"" width=""${width}"" height=""${height}"" fill=""var(--surface-light)""></rect>
  ${gridLines}
  <path d=""M ${amountPoints.replace(/ /g, ' L ')}"" fill=""none"" stroke=""var(--accent-primary)"" stroke-width=""2.5""></path>
  <path d=""M ${walletPoints.replace(/ /g, ' L ')}"" fill=""none"" stroke=""#107c10"" stroke-width=""2"" stroke-dasharray=""5 5""></path>
  <text x=""${padL - 8}"" y=""${padT + 4}"" fill=""var(--accent-primary)"" font-size=""10"" text-anchor=""end"">${formatOpt(maxAmount)} OPT</text>
  <text x=""${padL - 8}"" y=""${height - padB}"" fill=""var(--accent-primary)"" font-size=""10"" text-anchor=""end"">0 OPT</text>
  <text x=""${width - padR + 8}"" y=""${padT + 4}"" fill=""#107c10"" font-size=""10"">${Math.round(maxWallets).toLocaleString()} wallets</text>
  <text x=""${width - padR + 8}"" y=""${height - padB}"" fill=""#107c10"" font-size=""10"">0 wallets</text>
  ${xTickLabels}
</svg>`;

                                            const tooltip = document.createElement('div');
                                            tooltip.style.position = 'absolute';
                                            tooltip.style.pointerEvents = 'none';
                                            tooltip.style.background = '#ffffff';
                                            tooltip.style.border = '1px solid var(--border-strong)';
                                            tooltip.style.borderRadius = '6px';
                                            tooltip.style.padding = '6px 8px';
                                            tooltip.style.fontSize = '11px';
                                            tooltip.style.color = '#1b1a19';
                                            tooltip.style.fontWeight = '600';
                                            tooltip.style.boxShadow = '0 4px 14px rgba(0, 0, 0, 0.18)';
                                            tooltip.style.opacity = '0';
                                            tooltip.style.transform = 'translate(-50%, -115%)';
                                            if (getComputedStyle(chartEl).position === 'static') chartEl.style.position = 'relative';
                                            chartEl.appendChild(tooltip);

                                            const svg = chartEl.querySelector('svg');
                                            if (svg) {
                                                const svgNS = 'http://www.w3.org/2000/svg';
                                                const hoverLine = document.createElementNS(svgNS, 'line');
                                                hoverLine.setAttribute('y1', `${padT}`);
                                                hoverLine.setAttribute('y2', `${height - padB}`);
                                                hoverLine.setAttribute('stroke', 'var(--accent-light)');
                                                hoverLine.setAttribute('stroke-width', '1');
                                                hoverLine.setAttribute('stroke-dasharray', '4 4');
                                                hoverLine.setAttribute('opacity', '0');
                                                svg.appendChild(hoverLine);

                                                const updateHover = (clientX) => {
                                                    // Map through the SVG's screen transform; preserveAspectRatio 'meet' leaves letterbox space the element box doesn't account for.
                                                    const ctm = svg.getScreenCTM();
                                                    if (!ctm) return;
                                                    const relX = (clientX - ctm.e) / ctm.a;
                                                    const clamped = Math.max(padL, Math.min(width - padR, relX));
                                                    const index = xStep === 0 ? 0 : Math.round((clamped - padL) / xStep);
                                                    const safeIndex = Math.max(0, Math.min(rows.length - 1, index));
                                                    const x = xFor(safeIndex);
                                                    const row = rows[safeIndex];
                                                    const y = Math.min(toAmountY(amounts[safeIndex]), toWalletY(wallets[safeIndex]));

                                                    hoverLine.setAttribute('x1', `${x}`);
                                                    hoverLine.setAttribute('x2', `${x}`);
                                                    hoverLine.setAttribute('opacity', '1');
                                                    tooltip.innerHTML = `${formatDate(row.date)}<br>${formatOpt(row.distributedOpt)} OPT<br>${Number(row.walletCount || 0).toLocaleString()} wallets`;
                                                    const hostRect = chartEl.getBoundingClientRect();
                                                    tooltip.style.left = `${ctm.a * x + ctm.e - hostRect.left}px`;
                                                    tooltip.style.top = `${ctm.d * y + ctm.f - hostRect.top}px`;
                                                    tooltip.style.opacity = '1';
                                                };

                                                svg.addEventListener('mousemove', (event) => updateHover(event.clientX));
                                                svg.addEventListener('mouseenter', (event) => updateHover(event.clientX));
                                                svg.addEventListener('mouseleave', () => {
                                                    hoverLine.setAttribute('opacity', '0');
                                                    tooltip.style.opacity = '0';
                                                });
                                            }

                                            statusEl.textContent = `Source: ${data.source || 'optio103r5...'}`;
                                            totalsEl.innerHTML = `<span style=""color: var(--accent-primary);"">Amount</span> / <span style=""color: #107c10;"">Wallet count</span> · ${rows.length.toLocaleString()} days · ${formatOpt(totalAmount)} OPT total`;
                                        } catch (error) {
                                            statusEl.textContent = 'Error loading daily distribution report: ' + error.message;
                                            statusEl.style.color = 'var(--error)';
                                            chartEl.innerHTML = '<div style=""color: var(--error);"">Unable to load distribution chart</div>';
                                        }
                                    }

                                    loadDailyDistributionReport();
                                })();
                            </script>
                        </div>
                    </div>
");
    }

    static void AppendDistributionsCard(System.Text.StringBuilder sb)
    {
        sb.Append(@"
                    <div class=""card"">
                        <h2>Distributions / Ledger</h2>
                        <div class=""card-content"">
                            <form method=""post"" action=""/run"">
                                <input type=""hidden"" name=""mode"" value=""distributions"">
                                <div class=""form-group"">
                                    <label>Address (optional)</label>
                                    <input type=""text"" name=""address"" placeholder=""optio1..."">
                                </div>
                                <button type=""submit"">Generate Report</button>
                            </form>
                        </div>
                    </div>
");
    }

    static void AppendLocksCard(System.Text.StringBuilder sb)
    {
        sb.Append(@"
                    <div class=""card"">
                        <h2>Locks</h2>
                        <div class=""card-content"">
                            <form method=""post"" action=""/run"">
                                <input type=""hidden"" name=""mode"" value=""locks"">
                                <div class=""form-group"">
                                    <label>Address</label>
                                    <input type=""text"" name=""address"" placeholder=""optio1..."" required>
                                </div>
                                <button type=""submit"">Get Locks</button>
                            </form>
                        </div>
                    </div>
");
    }

    static void AppendLocksSummaryCard(System.Text.StringBuilder sb)
    {
        sb.Append(@"
                    <div class=""card"">
                        <h2>Locks Summary</h2>
                        <div class=""card-content"">
                            <form method=""post"" action=""/run"">
                                <input type=""hidden"" name=""mode"" value=""locks-summary"">
                                <div class=""form-group"">
                                    <label>Address</label>
                                    <input type=""text"" name=""address"" placeholder=""optio1..."" required>
                                </div>
                                <button type=""submit"">Get Summary</button>
                            </form>
                        </div>
                    </div>
");
    }

    static void AppendLockExtendedCard(System.Text.StringBuilder sb)
    {
        sb.Append(@"
                    <div class=""card"">
                        <h2>Lock Extended Export</h2>
                        <div class=""card-content"">
                            <form method=""post"" action=""/run"">
                                <input type=""hidden"" name=""mode"" value=""lock-extended"">
                                <div class=""form-group"">
                                    <label>Address (optional)</label>
                                    <input type=""text"" name=""address"" placeholder=""optio1..."">
                                </div>
                                <button type=""submit"">Export Extended</button>
                            </form>
                        </div>
                    </div>
");
    }

    static void AppendCounterpartiesCard(System.Text.StringBuilder sb)
    {
        sb.Append(@"
                    <div class=""card"">
                        <h2>Counterparties</h2>
                        <div class=""card-content"">
                            <form method=""post"" action=""/run"">
                                <input type=""hidden"" name=""mode"" value=""counterparties"">
                                <div class=""form-group"">
                                    <label>Address</label>
                                    <input type=""text"" name=""address"" placeholder=""optio1..."" required>
                                </div>
                                <button type=""submit"">Analyze</button>
                            </form>
                        </div>
                    </div>
");
    }

    static void AppendSendRecvCard(System.Text.StringBuilder sb)
    {
        sb.Append(@"
                    <div class=""card"">
                        <h2>Send/Recv</h2>
                        <div class=""card-content"">
                            <form method=""post"" action=""/run"">
                                <input type=""hidden"" name=""mode"" value=""send-recv"">
                                <div class=""form-group"">
                                    <label>Address</label>
                                    <input type=""text"" name=""address"" placeholder=""optio1..."" required>
                                </div>
                                <button type=""submit"">Analyze Transfers</button>
                            </form>
                        </div>
                    </div>
");
    }

    static void AppendNetworkTotalsCard(System.Text.StringBuilder sb)
    {
        sb.Append(@"
                    <div class=""card"">
                        <h2>Network Totals</h2>
                        <div class=""card-content"">
                            <form method=""post"" action=""/run"">
                                <input type=""hidden"" name=""mode"" value=""network-totals"">
                                <button type=""submit"">Calculate Network Totals</button>
                            </form>
                        </div>
                    </div>
");
    }

    static void AppendDryTotalsCard(System.Text.StringBuilder sb)
    {
        sb.Append(@"
                    <div class=""card"">
                        <h2>Dry Totals</h2>
                        <div class=""card-content"">
                            <form method=""post"" action=""/run"">
                                <input type=""hidden"" name=""mode"" value=""dry-totals"">
                                <button type=""submit"">Calculate Dry Run</button>
                            </form>
                        </div>
                    </div>
");
    }

    static void AppendStatusCard(System.Text.StringBuilder sb)
    {
        sb.Append(@"
                    <div class=""card"">
                        <h2>Node Status</h2>
                        <div class=""card-content"">
                            <form method=""post"" action=""/run"">
                                <input type=""hidden"" name=""mode"" value=""status"">
                                <button type=""submit"">Check Status</button>
                            </form>
                        </div>
                    </div>
");
    }

    static void AppendValidatorsNodesCard(System.Text.StringBuilder sb)
    {
        sb.Append(@"
                    <div class=""card"">
                        <h2>Validators + Nodes</h2>
                        <div class=""card-content"">
                            <form method=""post"" action=""/run"">
                                <input type=""hidden"" name=""mode"" value=""validators"">
                                <button type=""submit"">Get Validators</button>
                            </form>
                        </div>
                    </div>
");
    }

    static void AppendWalletCountCard(System.Text.StringBuilder sb)
    {
        sb.Append(@"
                    <div class=""card"">
                        <h2>Wallet Count</h2>
                        <div class=""card-content"">
                            <form method=""post"" action=""/run"">
                                <input type=""hidden"" name=""mode"" value=""wallet-count"">
                                <button type=""submit"">Count Wallets</button>
                            </form>
                        </div>
                    </div>
");
    }

    static void AppendMultiSendSumCard(System.Text.StringBuilder sb)
    {
        sb.Append(@"
                    <div class=""card"">
                        <h2>Multisend Sum</h2>
                        <div class=""card-content"">
                            <form method=""post"" action=""/run"">
                                <input type=""hidden"" name=""mode"" value=""multisend-sum"">
                                <div class=""form-group"">
                                    <label>Emitter Address (optional)</label>
                                    <input type=""text"" name=""emitter"" placeholder=""optio1..."">
                                </div>
                                <button type=""submit"">Summarize Multisend</button>
                            </form>
                        </div>
                    </div>
");
    }

    static void AppendBlockScanMultiSendCard(System.Text.StringBuilder sb)
    {
        sb.Append(@"
                    <div class=""card"">
                        <h2>Multisend Block Scan</h2>
                        <div class=""card-content"">
                            <form method=""post"" action=""/run"">
                                <input type=""hidden"" name=""mode"" value=""block-scan-multisend"">
                                <div class=""form-row-3"">
                                    <div>
                                        <label>Start Block</label>
                                        <input type=""number"" name=""block-scan-start"" placeholder=""0"">
                                    </div>
                                    <div>
                                        <label>End Block</label>
                                        <input type=""number"" name=""block-scan-end"" placeholder=""999999"">
                                    </div>
                                    <div>
                                        <label>Emitter</label>
                                        <input type=""text"" name=""emitter"" placeholder=""optio1..."">
                                    </div>
                                </div>
                                <button type=""submit"">Scan Blocks</button>
                            </form>
                        </div>
                    </div>
");
    }

    static void AppendWalletBalancesCard(System.Text.StringBuilder sb)
    {
        sb.Append(@"
                    <div class=""card"">
                        <h2>Wallet Balances</h2>
                        <div class=""card-content"">
                            <form method=""post"" action=""/run"">
                                <input type=""hidden"" name=""mode"" value=""wallet-balances"">
                                <div class=""form-group"">
                                    <label>Wallet File (optional)</label>
                                    <input type=""text"" name=""wallet-file"" placeholder=""wallets.csv"">
                                </div>
                                <button type=""submit"">Get Balances</button>
                            </form>
                        </div>
                    </div>
");
    }

    // The user's own wallets, kept in the optic_wallets browser cookie (nothing is stored server-side).
    // Shared by the Address Distributions, Profit & Loss and My Wallets pages.
    static void AppendMyWalletsScript(System.Text.StringBuilder sb)
    {
        sb.Append(@"
                    <script>
                        window.opticWallets = window.opticWallets || (function () {
                            const NAME = 'optic_wallets';
                            const valid = (a) => /^optio1[02-9ac-hj-np-z]{38,58}$/.test(String(a || ''));
                            const esc = (v) => String(v ?? '').replace(/[&<>\x22\x27]/g, ch => '&#' + ch.charCodeAt(0) + ';');
                            const short = (a) => a.length > 20 ? a.slice(0, 12) + '...' + a.slice(-6) : a;

                            // Stored as [{ a: address, l: label, p: isPrimary }].
                            const list = () => {
                                try {
                                    const raw = document.cookie.split('; ').find(c => c.startsWith(NAME + '='));
                                    const parsed = raw ? JSON.parse(decodeURIComponent(raw.slice(NAME.length + 1))) : [];
                                    return Array.isArray(parsed) ? parsed.filter(w => w && valid(w.a)) : [];
                                } catch (e) { return []; }
                            };
                            const save = (wallets) => {
                                const seen = new Set();
                                const clean = [];
                                for (const w of wallets) {
                                    const a = String(w.a || '').trim().toLowerCase();
                                    if (!valid(a) || seen.has(a)) continue;
                                    seen.add(a);
                                    clean.push({ a, l: String(w.l || '').trim().slice(0, 40), p: !!w.p });
                                }
                                // Exactly one primary when there are any wallets.
                                const primaryIndex = clean.findIndex(w => w.p);
                                clean.forEach((w, i) => w.p = i === (primaryIndex < 0 ? 0 : primaryIndex));
                                document.cookie = NAME + '=' + encodeURIComponent(JSON.stringify(clean)) + '; path=/; max-age=315360000; SameSite=Strict';
                                return clean;
                            };
                            const find = (a) => list().find(w => w.a === String(a || '').toLowerCase()) || null;

                            // Exchange deposit addresses, stored as [{ a: address, n: name, m: the user's deposit memo }].
                            // Until the user saves the list, it starts with Coinstore.
                            const EX_NAME = 'optic_exchanges';
                            const EX_DEFAULT = [{ a: 'optio12e3lctqkpg2rrkvywd6596at358dr50cpmcr5x', n: 'Coinstore', m: '' }];
                            const exchanges = () => {
                                try {
                                    const raw = document.cookie.split('; ').find(c => c.startsWith(EX_NAME + '='));
                                    if (!raw) return EX_DEFAULT.map(x => ({ ...x }));
                                    const parsed = JSON.parse(decodeURIComponent(raw.slice(EX_NAME.length + 1)));
                                    return Array.isArray(parsed) ? parsed.filter(x => x && valid(x.a)) : [];
                                } catch (e) { return []; }
                            };
                            const saveExchanges = (items) => {
                                const seen = new Set();
                                const clean = [];
                                for (const x of items) {
                                    const a = String(x.a || '').trim().toLowerCase();
                                    if (!valid(a) || seen.has(a)) continue;
                                    seen.add(a);
                                    clean.push({ a, n: String(x.n || '').trim().slice(0, 30) || 'Exchange', m: String(x.m || '').trim().slice(0, 64) });
                                }
                                document.cookie = EX_NAME + '=' + encodeURIComponent(JSON.stringify(clean)) + '; path=/; max-age=315360000; SameSite=Strict';
                                return clean;
                            };
                            const label = (a) => { const w = find(a); return w ? (w.l || (w.p ? 'Primary' : 'My wallet')) : null; };

                            return {
                                valid,
                                list,
                                save,
                                find,
                                label,
                                isMine: (a) => !!find(a),
                                exchanges,
                                saveExchanges,
                                exchange: (a) => exchanges().find(x => x.a === String(a || '').toLowerCase()) || null,
                                primary: () => (list().find(w => w.p) || {}).a || null,
                                add: (a, l) => save([...list(), { a, l: l || '', p: false }]),
                                remove: (a) => save(list().filter(w => w.a !== String(a || '').toLowerCase())),

                                // Row of one-click buttons for the user's wallets; onPick(address) switches to one.
                                renderSwitcher: (el, current, onPick) => {
                                    const wallets = list();
                                    if (wallets.length === 0) {
                                        el.innerHTML = 'Save your own wallets on <a href=""/page/my-wallets"">My Wallets</a> to switch between them here and to spot transfers between them.';
                                        return;
                                    }
                                    el.innerHTML = '<span>My wallets:</span> ' + wallets.map(w => {
                                        const active = w.a === current;
                                        return `<button type=""button"" data-wallet=""${esc(w.a)}"" title=""${esc(w.a)}"" aria-pressed=""${active}"" style=""padding: 3px 10px; font-size: 11px; border-radius: 999px; margin: 2px 0; border: 1px solid ${active ? 'var(--accent-primary)' : 'var(--border)'}; background: ${active ? 'var(--accent-primary)' : 'var(--surface)'}; color: ${active ? 'var(--bg-primary)' : 'var(--text-secondary)'}; cursor: pointer; text-transform: none; letter-spacing: 0;"">${esc(w.l || short(w.a))}${w.p ? ' (primary)' : ''}</button>`;
                                    }).join(' ') + ' <a href=""/page/my-wallets"" style=""font-size: 12px;"">Manage</a>';
                                    el.querySelectorAll('button[data-wallet]').forEach(b => b.addEventListener('click', () => onPick(b.dataset.wallet)));
                                }
                            };
                        })();
                    </script>
");
    }

    static void AppendMyWalletsCard(System.Text.StringBuilder sb)
    {
        AppendMyWalletsScript(sb);
        sb.Append(@"
                    <div class=""card"" style=""grid-column: 1 / -1;"">
                        <h2>My Wallets</h2>
                        <div class=""card-content"">
                            <div style=""color: var(--text-secondary); font-size: 13px; margin-bottom: 12px;"">Wallets you own. The primary wallet is the one node distributions are paid to; it opens by default on Address Distributions and Profit &amp; Loss. Transfers between these wallets are labelled and left out of profit and loss. Saved only in this browser, in a cookie; nothing is stored on the server.</div>
                            <form id=""mw-form"" style=""display: flex; gap: 8px; flex-wrap: wrap; align-items: flex-end; margin-bottom: 16px;"">
                                <div class=""form-group"" style=""flex: 1 1 420px; margin-bottom: 0;"">
                                    <label for=""mw-address"">Wallet address</label>
                                    <input type=""text"" id=""mw-address"" placeholder=""optio1..."" autocomplete=""off"" spellcheck=""false"" required>
                                </div>
                                <div class=""form-group"" style=""flex: 0 1 220px; margin-bottom: 0;"">
                                    <label for=""mw-label"">Label (optional)</label>
                                    <input type=""text"" id=""mw-label"" placeholder=""e.g. Exchange, Cold storage"" maxlength=""40"" autocomplete=""off"">
                                </div>
                                <button type=""submit"">Add Wallet</button>
                            </form>
                            <div id=""mw-status"" style=""color: var(--text-secondary); font-size: 13px; margin-bottom: 8px;""></div>
                            <div class=""data-table-wrap pnl-table-wrap"">
                                <table class=""data-table"">
                                    <thead>
                                        <tr>
                                            <th>Primary</th>
                                            <th>Address</th>
                                            <th>Label</th>
                                            <th>Reports</th>
                                            <th></th>
                                        </tr>
                                    </thead>
                                    <tbody id=""mw-body""></tbody>
                                </table>
                            </div>
                        </div>
                    </div>
                    <div class=""card"" style=""grid-column: 1 / -1;"">
                        <h2>Exchanges</h2>
                        <div class=""card-content"">
                            <div style=""color: var(--text-secondary); font-size: 13px; margin-bottom: 12px;"">Exchange deposit addresses you sent OPT to or withdrew OPT from. On Profit &amp; Loss, sends to an exchange are labelled as sales there and receipts from it as purchases. If the exchange needed a memo to credit your account (Coinstore does), enter it so sends to your own account can be told apart from sends to someone else's. Saved only in this browser, in a cookie.</div>
                            <form id=""ex-form"" style=""display: flex; gap: 8px; flex-wrap: wrap; align-items: flex-end; margin-bottom: 16px;"">
                                <div class=""form-group"" style=""flex: 0 1 180px; margin-bottom: 0;"">
                                    <label for=""ex-name"">Exchange</label>
                                    <input type=""text"" id=""ex-name"" placeholder=""e.g. MEXC"" maxlength=""30"" autocomplete=""off"" required>
                                </div>
                                <div class=""form-group"" style=""flex: 1 1 380px; margin-bottom: 0;"">
                                    <label for=""ex-address"">Deposit / withdrawal address</label>
                                    <input type=""text"" id=""ex-address"" placeholder=""optio1..."" autocomplete=""off"" spellcheck=""false"" required>
                                </div>
                                <div class=""form-group"" style=""flex: 0 1 200px; margin-bottom: 0;"">
                                    <label for=""ex-memo"">My deposit memo (optional)</label>
                                    <input type=""text"" id=""ex-memo"" maxlength=""64"" autocomplete=""off"" spellcheck=""false"">
                                </div>
                                <button type=""submit"">Add Exchange</button>
                            </form>
                            <div id=""ex-status"" style=""color: var(--text-secondary); font-size: 13px; margin-bottom: 8px;""></div>
                            <div class=""data-table-wrap pnl-table-wrap"">
                                <table class=""data-table"">
                                    <thead>
                                        <tr>
                                            <th>Exchange</th>
                                            <th>Address</th>
                                            <th>My deposit memo</th>
                                            <th></th>
                                        </tr>
                                    </thead>
                                    <tbody id=""ex-body""></tbody>
                                </table>
                            </div>
                        </div>
                    </div>
                    <script>
                        (function () {
                            const W = window.opticWallets;
                            const exBody = document.getElementById('ex-body');
                            const exStatus = document.getElementById('ex-status');
                            const escEx = (v) => String(v ?? '').replace(/[&<>\x22\x27]/g, ch => '&#' + ch.charCodeAt(0) + ';');
                            const renderExchanges = () => {
                                const items = W.exchanges();
                                exBody.innerHTML = items.length === 0
                                    ? '<tr><td colspan=""4"" style=""text-align: center; color: var(--text-secondary);"">No exchanges saved.</td></tr>'
                                    : items.map(x => `<tr>
                                        <td><input type=""text"" data-ex-name=""${escEx(x.a)}"" value=""${escEx(x.n)}"" maxlength=""30"" style=""width: 100%; min-width: 120px;""></td>
                                        <td class=""address-cell"">${escEx(x.a)}</td>
                                        <td><input type=""text"" data-ex-memo=""${escEx(x.a)}"" value=""${escEx(x.m)}"" maxlength=""64"" placeholder=""none"" style=""width: 100%; min-width: 140px;""></td>
                                        <td><button type=""button"" data-ex-remove=""${escEx(x.a)}"" style=""padding: 4px 10px; font-size: 11px;"">Remove</button></td>
                                    </tr>`).join('');
                            };
                            document.getElementById('ex-form').addEventListener('submit', (e) => {
                                e.preventDefault();
                                const address = document.getElementById('ex-address').value.trim().toLowerCase();
                                if (!W.valid(address)) { exStatus.textContent = 'That is not a valid optio1... address.'; return; }
                                if (W.exchange(address)) { exStatus.textContent = 'That address is already saved.'; return; }
                                W.saveExchanges([...W.exchanges(), { a: address, n: document.getElementById('ex-name').value, m: document.getElementById('ex-memo').value }]);
                                ['ex-name', 'ex-address', 'ex-memo'].forEach(id => document.getElementById(id).value = '');
                                exStatus.textContent = 'Saved.';
                                renderExchanges();
                            });
                            exBody.addEventListener('change', (e) => {
                                const a = e.target.dataset.exName || e.target.dataset.exMemo;
                                if (!a) return;
                                const field = e.target.dataset.exName ? 'n' : 'm';
                                W.saveExchanges(W.exchanges().map(x => x.a === a ? { ...x, [field]: e.target.value } : x));
                                exStatus.textContent = 'Saved.';
                            });
                            exBody.addEventListener('click', (e) => {
                                const btn = e.target.closest('button[data-ex-remove]');
                                if (!btn) return;
                                W.saveExchanges(W.exchanges().filter(x => x.a !== btn.dataset.exRemove));
                                exStatus.textContent = 'Removed.';
                                renderExchanges();
                            });
                            renderExchanges();
                        })();
                    </script>
                    <script>
                        (function () {
                            const W = window.opticWallets;
                            const body = document.getElementById('mw-body');
                            const status = document.getElementById('mw-status');
                            const esc = (v) => String(v ?? '').replace(/[&<>\x22\x27]/g, ch => '&#' + ch.charCodeAt(0) + ';');

                            const render = () => {
                                const wallets = W.list();
                                body.innerHTML = wallets.length === 0
                                    ? '<tr><td colspan=""5"" style=""text-align: center; color: var(--text-secondary);"">No wallets saved yet. Add your distribution wallet first; it becomes the primary.</td></tr>'
                                    : wallets.map(w => `<tr>
                                        <td><input type=""radio"" name=""mw-primary"" value=""${esc(w.a)}"" ${w.p ? 'checked' : ''} aria-label=""Make ${esc(w.a)} the primary wallet""></td>
                                        <td class=""address-cell"">${esc(w.a)}</td>
                                        <td><input type=""text"" data-label=""${esc(w.a)}"" value=""${esc(w.l)}"" maxlength=""40"" placeholder=""${w.p ? 'Primary' : 'My wallet'}"" style=""width: 100%; min-width: 140px;""></td>
                                        <td style=""white-space: nowrap;""><a href=""/page/address-distributions?address=${encodeURIComponent(w.a)}"">Distributions</a> · <a href=""/page/pnl?address=${encodeURIComponent(w.a)}"">Profit &amp; Loss</a></td>
                                        <td><button type=""button"" data-remove=""${esc(w.a)}"" style=""padding: 4px 10px; font-size: 11px;"">Remove</button></td>
                                    </tr>`).join('');
                            };

                            document.getElementById('mw-form').addEventListener('submit', (e) => {
                                e.preventDefault();
                                const addressEl = document.getElementById('mw-address');
                                const labelEl = document.getElementById('mw-label');
                                const address = addressEl.value.trim().toLowerCase();
                                if (!W.valid(address)) { status.textContent = 'That is not a valid optio1... address.'; return; }
                                if (W.isMine(address)) { status.textContent = 'That wallet is already saved.'; return; }
                                W.add(address, labelEl.value);
                                addressEl.value = '';
                                labelEl.value = '';
                                status.textContent = 'Saved.';
                                render();
                            });

                            body.addEventListener('change', (e) => {
                                if (e.target.name === 'mw-primary') {
                                    W.save(W.list().map(w => ({ ...w, p: w.a === e.target.value })));
                                    status.textContent = 'Primary wallet updated.';
                                    render();
                                } else if (e.target.dataset.label) {
                                    W.save(W.list().map(w => w.a === e.target.dataset.label ? { ...w, l: e.target.value } : w));
                                    status.textContent = 'Label saved.';
                                }
                            });

                            body.addEventListener('click', (e) => {
                                const btn = e.target.closest('button[data-remove]');
                                if (!btn) return;
                                W.remove(btn.dataset.remove);
                                status.textContent = 'Removed.';
                                render();
                            });

                            render();
                        })();
                    </script>
");
    }

    static void AppendAddressDistributionsCard(System.Text.StringBuilder sb)
    {
        AppendMyWalletsScript(sb);
        sb.Append(@"
                    <div class=""card"" style=""grid-column: 1 / -1;"">
                        <h2>Distributions to an Address</h2>
                        <div class=""card-content"">
                            <form id=""addr-dist-form"" style=""display: flex; gap: 8px; flex-wrap: wrap; align-items: flex-end; margin-bottom: 12px;"">
                                <div class=""form-group"" style=""flex: 1 1 420px; margin-bottom: 0;"">
                                    <label for=""addr-dist-input"">Wallet address</label>
                                    <input type=""text"" id=""addr-dist-input"" placeholder=""optio1..."" autocomplete=""off"" spellcheck=""false"" required>
                                </div>
                                <div class=""form-group"" style=""flex: 0 0 140px; margin-bottom: 0;"">
                                    <label for=""addr-dist-year"">Year</label>
                                    <select id=""addr-dist-year""></select>
                                </div>
                                <div class=""form-group"" style=""flex: 0 0 200px; margin-bottom: 0;"">
                                    <label for=""addr-dist-type"">Type</label>
                                    <select id=""addr-dist-type"">
                                        <option value="""">Node and staking</option>
                                        <option value=""node"">Node only</option>
                                        <option value=""staking"">Staking / participation only</option>
                                    </select>
                                </div>
                                <button type=""submit"" id=""addr-dist-submit"">Get Distributions</button>
                                <a id=""addr-dist-csv"" class=""header-link"" href=""#"" hidden>Download CSV</a>
                            </form>
                            <div id=""addr-dist-wallets"" style=""font-size: 13px; color: var(--text-secondary); margin: -4px 0 10px; display: flex; gap: 6px; flex-wrap: wrap; align-items: center;""></div>
                            <div id=""addr-dist-status"" style=""color: var(--text-secondary); font-size: 13px; margin-bottom: 8px;"">Lists every payout from the node distribution account (optio103r5...) to the wallet, priced at the OPT/USD rate for the hour it was paid.</div>
                            <div id=""addr-dist-summary"" style=""display: none; grid-template-columns: repeat(auto-fit, minmax(170px, 1fr)); gap: 8px; margin-bottom: 12px;""></div>
                            <div id=""addr-dist-view"" role=""group"" aria-label=""View"" style=""display: none; gap: 6px; margin-bottom: 8px;"">
                                <button type=""button"" data-view=""list"">PAYOUTS</button>
                                <button type=""button"" data-view=""price"">BY PRICE</button>
                            </div>
                            <div class=""data-table-wrap pnl-table-wrap"" id=""addr-dist-bands-wrap"" hidden>
                                <table class=""data-table"">
                                    <thead>
                                        <tr>
                                            <th>Price range (USD)</th>
                                            <th style=""text-align: right;"">Payouts</th>
                                            <th style=""text-align: right;"">OPT</th>
                                            <th style=""width: 180px;"">Share of OPT</th>
                                            <th style=""text-align: right;"">Value (USD)</th>
                                            <th style=""text-align: right;"">Avg price</th>
                                            <th>First received</th>
                                            <th>Last received</th>
                                        </tr>
                                    </thead>
                                    <tbody id=""addr-dist-bands-body""></tbody>
                                    <tfoot id=""addr-dist-bands-foot""></tfoot>
                                </table>
                            </div>
                            <div class=""data-table-wrap"" id=""addr-dist-table-wrap"" hidden>
                                <table class=""data-table"">
                                    <thead>
                                        <tr>
                                            <th>Time (UTC)</th>
                                            <th>Block</th>
                                            <th style=""text-align: left;"">Type</th>
                                            <th style=""text-align: right;"">Amount (OPT)</th>
                                            <th style=""text-align: right;"">Price (USD)</th>
                                            <th style=""text-align: right;"">Value (USD)</th>
                                            <th>Memo</th>
                                            <th>Tx</th>
                                        </tr>
                                    </thead>
                                    <tbody id=""addr-dist-body""></tbody>
                                </table>
                            </div>
                        </div>
                    </div>
                    <script>
                        (function () {
                            const form = document.getElementById('addr-dist-form');
                            const input = document.getElementById('addr-dist-input');
                            const submit = document.getElementById('addr-dist-submit');
                            const csvLink = document.getElementById('addr-dist-csv');
                            const status = document.getElementById('addr-dist-status');
                            const summary = document.getElementById('addr-dist-summary');
                            const tableWrap = document.getElementById('addr-dist-table-wrap');
                            const body = document.getElementById('addr-dist-body');
                            const fmtOpt = (v) => Number(v || 0).toLocaleString('en-US', { maximumFractionDigits: 2 });
                            const fmtUsd = (v) => v == null ? '-' : '$' + Number(v).toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
                            const fmtPrice = (v) => v == null ? '-' : '$' + Number(v).toLocaleString('en-US', { minimumFractionDigits: 6, maximumFractionDigits: 6 });
                            const fmtTime = (v) => String(v || '').replace('T', ' ').replace(/\.\d+/, '').replace('Z', '');
                            const esc = (v) => String(v ?? '').replace(/[&<>\x22\x27]/g, ch => '&#' + ch.charCodeAt(0) + ';');
                            const tile = (label, value) => `<div class=""metric-card""><div style=""color: var(--text-secondary); font-size: 12px;"">${label}</div><div style=""font-size: 18px; font-weight: 600; margin-top: 4px;"">${value}</div></div>`;

                            const yearSelect = document.getElementById('addr-dist-year');
                            const typeSelect = document.getElementById('addr-dist-type');
                            const typeNames = { node: 'node', staking: 'staking / participation' };
                            const firstYear = 2024;
                            const currentYear = new Date().getUTCFullYear();
                            yearSelect.innerHTML = '<option value="""">All years</option>' +
                                Array.from({ length: currentYear - firstYear + 1 }, (_, i) => currentYear - i)
                                    .map(y => `<option value=""${y}"">${y}</option>`).join('');

                            let loaded = null; // { address, data } from the last successful lookup
                            let viewMode = new URLSearchParams(location.search).get('view') === 'price' ? 'price' : 'list';
                            const viewEl = document.getElementById('addr-dist-view');
                            const bandsWrap = document.getElementById('addr-dist-bands-wrap');
                            const renderViewButtons = () => {
                                viewEl.querySelectorAll('button').forEach(btn => {
                                    const active = btn.dataset.view === viewMode;
                                    btn.setAttribute('aria-pressed', String(active));
                                    btn.style.cssText = `padding: 4px 10px; font-size: 11px; border-radius: 999px; border: 1px solid ${active ? 'var(--accent-primary)' : 'var(--border)'}; background: ${active ? 'var(--accent-primary)' : 'var(--surface)'}; color: ${active ? 'var(--bg-primary)' : 'var(--text-secondary)'}; cursor: pointer;`;
                                });
                            };

                            // Price band edges on a log scale: 1-2-5 per decade, or 1-1.5-2-3-5-7 when that
                            // would give fewer than 5 bands. Returns ascending edges spanning [min, max].
                            const bandEdges = (min, max) => {
                                const build = (mults) => {
                                    const all = [];
                                    for (let e = Math.floor(Math.log10(min)) - 1; e <= Math.ceil(Math.log10(max)) + 1; e++)
                                        for (const m of mults) all.push(Number((m * Math.pow(10, e)).toPrecision(6)));
                                    const lo = all.filter(x => x <= min).pop();
                                    const hi = all.find(x => x > max);
                                    return all.filter(x => x >= lo && x <= hi);
                                };
                                const coarse = build([1, 2, 5]);
                                return coarse.length - 1 >= 5 ? coarse : build([1, 1.5, 2, 3, 5, 7]);
                            };
                            const fmtEdge = (v) => '$' + v.toLocaleString('en-US', { maximumSignificantDigits: 3, minimumFractionDigits: 2 });

                            const computeBands = (rows) => {
                                const priced = rows.filter(r => r.priceUsd != null && r.priceUsd > 0);
                                const bands = [];
                                if (priced.length > 0) {
                                    const prices = priced.map(r => r.priceUsd);
                                    const edges = bandEdges(Math.min(...prices), Math.max(...prices));
                                    for (let i = 0; i < edges.length - 1; i++)
                                        bands.push({ lo: edges[i], hi: edges[i + 1], rows: [] });
                                    for (const r of priced) {
                                        let i = bands.findIndex(b => r.priceUsd >= b.lo && r.priceUsd < b.hi);
                                        if (i < 0) i = bands.length - 1;
                                        bands[i].rows.push(r);
                                    }
                                }
                                const result = bands.filter(b => b.rows.length > 0).reverse().map(b => ({ label: `${fmtEdge(b.lo)} - ${fmtEdge(b.hi)}`, rows: b.rows }));
                                const unpriced = rows.filter(r => r.priceUsd == null || r.priceUsd <= 0);
                                if (unpriced.length > 0) result.push({ label: 'No market price', rows: unpriced, unpriced: true });
                                for (const b of result) {
                                    b.count = b.rows.length;
                                    b.opt = b.rows.reduce((s, r) => s + r.amountOpt, 0);
                                    b.value = b.unpriced ? null : b.rows.reduce((s, r) => s + (r.valueUsd || 0), 0);
                                    b.avg = b.unpriced || b.opt === 0 ? null : b.value / b.opt;
                                    b.first = b.rows[0].timeUtc;
                                    b.last = b.rows[b.rows.length - 1].timeUtc;
                                }
                                return result;
                            };

                            const renderBands = (rows, totalOpt, totalValueUsd) => {
                                const bands = computeBands(rows);
                                document.getElementById('addr-dist-bands-body').innerHTML = bands.map(b => {
                                    const share = totalOpt > 0 ? b.opt / totalOpt * 100 : 0;
                                    return `<tr style=""${b.unpriced ? 'color: var(--text-muted);' : ''}"">
                                        <td style=""white-space: nowrap;"">${b.label}</td>
                                        <td class=""amount-cell"">${b.count.toLocaleString('en-US')}</td>
                                        <td class=""amount-cell"">${fmtOpt(b.opt)}</td>
                                        <td><div style=""display: flex; align-items: center; gap: 8px;"">
                                            <div style=""flex: 1; height: 8px; background: var(--border); border-radius: 4px; overflow: hidden;""><div style=""width: ${share.toFixed(2)}%; height: 100%; background: ${b.unpriced ? 'var(--text-muted)' : 'var(--accent-primary)'}; border-radius: 4px;""></div></div>
                                            <span style=""font-size: 12px; min-width: 44px; text-align: right;"">${share.toFixed(1)}%</span>
                                        </div></td>
                                        <td class=""amount-cell"">${fmtUsd(b.value)}</td>
                                        <td class=""amount-cell"">${fmtPrice(b.avg)}</td>
                                        <td>${fmtTime(b.first).slice(0, 10)}</td>
                                        <td>${fmtTime(b.last).slice(0, 10)}</td>
                                    </tr>`;
                                }).join('');
                                document.getElementById('addr-dist-bands-foot').innerHTML = `<tr style=""font-weight: 600; border-top: 2px solid var(--border-strong);"">
                                    <td>Total</td>
                                    <td class=""amount-cell"">${rows.length.toLocaleString('en-US')}</td>
                                    <td class=""amount-cell"">${fmtOpt(totalOpt)}</td>
                                    <td></td>
                                    <td class=""amount-cell"">${fmtUsd(totalValueUsd)}</td>
                                    <td></td><td></td><td></td>
                                </tr>`;
                                return bands;
                            };
                            let currentBands = [];

                            const updateUrl = () => {
                                const params = new URLSearchParams();
                                if (loaded) params.set('address', loaded.address);
                                if (yearSelect.value) params.set('year', yearSelect.value);
                                if (typeSelect.value) params.set('type', typeSelect.value);
                                if (viewMode === 'price') params.set('view', 'price');
                                const qs = params.toString();
                                history.replaceState(null, '', qs ? '?' + qs : location.pathname);
                                return qs;
                            };

                            // Draws the summary, table and notes for the selected year from the loaded rows.
                            const kindCell = (r) => {
                                const label = r.kind === 'staking' ? 'Staking / participation' : 'Node';
                                return r.kindInferred
                                    ? `<span style=""color: var(--text-secondary);"" title=""No clear node/staking pair that day; guessed from the amounts on nearby days"">${label} (est.)</span>`
                                    : `<span title=""Same-day pair: the larger payout is from nodes, the smaller from staking / participation"">${label}</span>`;
                            };

                            const renderView = () => {
                                const data = loaded.data;
                                const year = yearSelect.value;
                                const type = typeSelect.value;
                                const qs = updateUrl();
                                const rows = (data.rows || []).filter(r => (!year || String(r.timeUtc).startsWith(year)) && (!type || r.kind === type));
                                csvLink.href = '/api/distributions/address?' + qs + '&format=csv';

                                if (rows.length === 0) {
                                    summary.style.display = 'none';
                                    tableWrap.hidden = true;
                                    bandsWrap.hidden = true;
                                    viewEl.style.display = 'none';
                                    csvLink.hidden = true;
                                    status.textContent = (data.rows || []).length === 0
                                        ? 'No distributions from optio103r5... found for this address.'
                                        : `No ${type ? typeNames[type] + ' ' : ''}distributions to this address${year ? ' in ' + year : ''}.`;
                                    return;
                                }

                                const totalOpt = rows.reduce((s, r) => s + r.amountOpt, 0);
                                const totalValueUsd = rows.reduce((s, r) => s + (r.valueUsd || 0), 0);
                                const unpricedCount = rows.filter(r => r.priceUsd == null).length;
                                const priced = rows.filter(r => r.priceUsd != null);
                                const pricedOpt = priced.reduce((s, r) => s + r.amountOpt, 0);
                                const high = priced.reduce((best, r) => !best || r.priceUsd > best.priceUsd ? r : best, null);
                                const low = priced.reduce((best, r) => !best || r.priceUsd < best.priceUsd ? r : best, null);
                                const kindTile = (label, kind) => {
                                    const list = rows.filter(r => r.kind === kind);
                                    return tile(label, `${fmtOpt(list.reduce((s, r) => s + r.amountOpt, 0))} OPT<div style=""font-size: 12px; font-weight: 400; color: var(--text-secondary); margin-top: 2px;"">${list.length.toLocaleString('en-US')} payouts · ${fmtUsd(list.reduce((s, r) => s + (r.valueUsd || 0), 0))}</div>`);
                                };
                                const priceTile = (label, r) => tile(label, r
                                    ? `${fmtPrice(r.priceUsd)}<div style=""font-size: 12px; font-weight: 400; color: var(--text-secondary); margin-top: 2px;"">${fmtTime(r.timeUtc).slice(0, 16)} UTC</div>`
                                    : '-');
                                summary.innerHTML =
                                    tile(type === 'node' ? 'Node payouts' : type === 'staking' ? 'Staking / participation payouts' : 'Distributions', rows.length.toLocaleString('en-US')) +
                                    tile('Total OPT', fmtOpt(totalOpt)) +
                                    tile('Total value (USD)', fmtUsd(totalValueUsd)) +
                                    tile('Avg price (USD)', pricedOpt > 0 ? fmtPrice(totalValueUsd / pricedOpt) : '-') +
                                    (type ? '' : kindTile('From nodes', 'node') + kindTile('Staking / participation', 'staking')) +
                                    priceTile('Highest price received', high) +
                                    priceTile('Lowest price received', low) +
                                    tile('First / last', fmtTime(rows[0].timeUtc).slice(0, 10) + ' to ' + fmtTime(rows[rows.length - 1].timeUtc).slice(0, 10));
                                summary.style.display = 'grid';

                                viewEl.style.display = 'flex';
                                renderViewButtons();
                                if (viewMode === 'price') {
                                    currentBands = renderBands(rows, totalOpt, totalValueUsd);
                                    tableWrap.hidden = true;
                                    bandsWrap.hidden = false;
                                } else {
                                    body.innerHTML = rows.slice().reverse().map(r => `
                                        <tr>
                                            <td>${fmtTime(r.timeUtc)}</td>
                                            <td>${Number(r.height).toLocaleString('en-US')}</td>
                                            <td>${kindCell(r)}</td>
                                            <td class=""amount-cell"">${fmtOpt(r.amountOpt)}</td>
                                            <td class=""amount-cell"" title=""${r.priceInterval === '1d' ? 'Daily close (no hourly price for this time)' : r.priceInterval === '1h' ? 'Hourly close' : 'No market price available'}"">${fmtPrice(r.priceUsd)}${r.priceInterval === '1d' ? '*' : ''}</td>
                                            <td class=""amount-cell"">${fmtUsd(r.valueUsd)}</td>
                                            <td class=""memo-cell"" title=""${esc(r.memo)}"">${esc(r.memo)}</td>
                                            <td class=""address-cell"" title=""${r.txHash}"">${String(r.txHash).slice(0, 10)}...</td>
                                        </tr>`).join('');
                                    bandsWrap.hidden = true;
                                    tableWrap.hidden = false;
                                }

                                const notes = [`Source: ${data.source}. Prices: ${data.priceSource}, hourly close for the hour of each payout; * = daily close.`];
                                if (unpricedCount > 0) {
                                    const to = data.priceDataTo ? fmtTime(data.priceDataTo).slice(0, 10) : null;
                                    notes.push(`${unpricedCount.toLocaleString('en-US')} payout(s) have no price` + (to ? ` (market data ends ${to})` : '') + ' and are left out of the USD total.');
                                }
                                notes.push('Type: on days with both, the larger payout is from nodes and the smaller from staking / participation; (est.) = guessed from nearby days.');
                                if (data.truncated) notes.push('Results were capped at 20,000 transactions.');
                                status.textContent = notes.join(' ');
                                csvLink.hidden = false;
                            };

                            const run = async (address) => {
                                const done = window.opticBusy(submit, status, 'Searching the node');
                                csvLink.hidden = true;
                                summary.style.display = 'none';
                                tableWrap.hidden = true;
                                status.textContent = 'Searching the node for distributions to this address. Wallets with a long history can take a minute...';
                                try {
                                    const response = await fetch('/api/distributions/address?address=' + encodeURIComponent(address));
                                    const data = await response.json();
                                    if (!response.ok) throw new Error(data.error || 'Request failed');
                                    loaded = { address: data.address, data };
                                    renderView();
                                    renderWallets();
                                } catch (error) {
                                    loaded = null;
                                    status.textContent = 'Error: ' + error.message;
                                } finally {
                                    done();
                                }
                            };

                            form.addEventListener('submit', (e) => {
                                e.preventDefault();
                                const address = input.value.trim();
                                if (address) run(address);
                            });

                            viewEl.addEventListener('click', (e) => {
                                const btn = e.target.closest('button[data-view]');
                                if (!btn || !loaded) return;
                                viewMode = btn.dataset.view;
                                renderView();
                            });

                            // In the by-price view the CSV is the band table, built here; otherwise the server's payout CSV.
                            csvLink.addEventListener('click', (e) => {
                                if (viewMode !== 'price' || !loaded) return;
                                e.preventDefault();
                                const cell = (v) => v == null ? '' : String(v);
                                const lines = [['price_range_usd', 'payouts', 'amount_opt', 'value_usd', 'avg_price_usd', 'first_received_utc', 'last_received_utc'].join(',')];
                                for (const b of currentBands)
                                    lines.push([b.label.replace(/\$/g, '').replace(' - ', ' to '), b.count, b.opt, b.value, b.avg, fmtTime(b.first), fmtTime(b.last)].map(cell).join(','));
                                const a = document.createElement('a');
                                a.href = URL.createObjectURL(new Blob([lines.join('\n')], { type: 'text/csv' }));
                                a.download = `distributions-by-price-${loaded.address}${yearSelect.value ? '-' + yearSelect.value : ''}${typeSelect.value ? '-' + typeSelect.value : ''}.csv`;
                                a.click();
                                setTimeout(() => URL.revokeObjectURL(a.href), 1000);
                            });

                            // Switching years re-filters the rows already loaded; no new node query.
                            yearSelect.addEventListener('change', () => {
                                if (loaded && loaded.address === input.value.trim()) renderView();
                                else updateUrl();
                            });
                            typeSelect.addEventListener('change', () => {
                                if (loaded && loaded.address === input.value.trim()) renderView();
                                else updateUrl();
                            });

                            const renderWallets = () => window.opticWallets.renderSwitcher(
                                document.getElementById('addr-dist-wallets'),
                                loaded ? loaded.address : null,
                                (address) => { input.value = address; run(address); });
                            renderWallets();

                            const initialParams = new URLSearchParams(location.search);
                            const initialYear = initialParams.get('year');
                            if (initialYear && yearSelect.querySelector(`option[value=""${CSS.escape(initialYear)}""]`)) yearSelect.value = initialYear;
                            const initialType = initialParams.get('type');
                            if (initialType === 'node' || initialType === 'staking') typeSelect.value = initialType;
                            // No address in the link: open the primary wallet.
                            const initial = initialParams.get('address') || window.opticWallets.primary();
                            if (initial) {
                                input.value = initial;
                                run(initial);
                            }
                        })();
                    </script>
");
    }

    static void AppendProfitLossCard(System.Text.StringBuilder sb)
    {
        AppendMyWalletsScript(sb);
        sb.Append(@"
                    <div class=""card"" style=""grid-column: 1 / -1;"">
                        <h2>Wallet Profit &amp; Loss (FIFO)</h2>
                        <div class=""card-content"">
                            <form id=""pnl-form"" style=""display: flex; gap: 8px; flex-wrap: wrap; align-items: flex-end; margin-bottom: 12px;"">
                                <div class=""form-group"" style=""flex: 1 1 420px; margin-bottom: 0;"">
                                    <label for=""pnl-input"">Wallet address</label>
                                    <input type=""text"" id=""pnl-input"" placeholder=""optio1..."" autocomplete=""off"" spellcheck=""false"" required>
                                </div>
                                <div class=""form-group"" style=""flex: 0 0 140px; margin-bottom: 0;"">
                                    <label for=""pnl-year"">Year</label>
                                    <select id=""pnl-year""></select>
                                </div>
                                <button type=""submit"" id=""pnl-submit"">Analyze</button>
                                <label style=""font-size: 13px; display: inline-flex; gap: 6px; align-items: center; cursor: pointer; padding-bottom: 10px;"" title=""Off: node distributions are left out entirely and sales are matched only to tokens bought or received from other wallets. On: distributions count as tokens acquired at the market price when received."">
                                    <input type=""checkbox"" id=""pnl-include-dist""> Count node distributions as acquired tokens
                                </label>
                            </form>
                            <div id=""pnl-wallets"" style=""font-size: 13px; color: var(--text-secondary); margin: -4px 0 10px; display: flex; gap: 6px; flex-wrap: wrap; align-items: center;""></div>
                            <div id=""pnl-status"" style=""color: var(--text-secondary); font-size: 13px; margin-bottom: 8px;"">Shows what the wallet received outside node distributions, what it sent out, and the profit or loss on each send, matching sends to the oldest tokens first (FIFO).</div>
                            <div id=""pnl-summary"" style=""display: none; grid-template-columns: repeat(auto-fit, minmax(190px, 1fr)); gap: 8px; margin-bottom: 16px;""></div>

                            <div id=""pnl-results"" hidden>
                                <div style=""display: flex; justify-content: space-between; align-items: center; gap: 12px; flex-wrap: wrap; margin: 8px 0;"">
                                    <h3 style=""margin: 0; font-size: 15px;"">Sales matched to the oldest tokens (FIFO)</h3>
                                    <div style=""display: flex; gap: 12px; align-items: center;"">
                                        <label style=""font-size: 13px; display: inline-flex; gap: 6px; align-items: center; cursor: pointer;""><input type=""checkbox"" id=""pnl-losses-only""> Losses only</label>
                                        <a href=""#"" id=""pnl-sales-csv"" class=""header-link"">Download CSV</a>
                                    </div>
                                </div>
                                <div class=""data-table-wrap pnl-table-wrap"">
                                    <table class=""data-table"">
                                        <thead>
                                            <tr>
                                                <th>Sold (UTC)</th>
                                                <th>Sent to</th>
                                                <th style=""text-align: right;"">OPT</th>
                                                <th style=""text-align: right;"">Sale price</th>
                                                <th style=""text-align: right;"">Proceeds</th>
                                                <th style=""text-align: right;"">Cost basis</th>
                                                <th style=""text-align: right;"">Gain / loss</th>
                                                <th>Acquired</th>
                                                <th>Term</th>
                                                <th>Memo</th>
                                            </tr>
                                        </thead>
                                        <tbody id=""pnl-sales-body""></tbody>
                                    </table>
                                </div>

                                <div style=""display: flex; justify-content: space-between; align-items: center; gap: 12px; flex-wrap: wrap; margin: 20px 0 8px;"">
                                    <h3 style=""margin: 0; font-size: 15px;"">Receipts (not node distributions) and sends</h3>
                                    <div style=""display: flex; gap: 16px; align-items: center;"">
                                        <label style=""font-size: 13px; display: inline-flex; gap: 6px; align-items: center; cursor: pointer;""><input type=""checkbox"" id=""pnl-show-repaid""> Show repaid loans</label>
                                        <a href=""#"" id=""pnl-transfers-csv"" class=""header-link"">Download CSV</a>
                                    </div>
                                </div>
                                <div class=""data-table-wrap pnl-table-wrap"">
                                    <table class=""data-table"">
                                        <thead>
                                            <tr>
                                                <th>Time (UTC)</th>
                                                <th>Direction</th>
                                                <th>Counterparty</th>
                                                <th>Type</th>
                                                <th style=""text-align: right;"">OPT</th>
                                                <th style=""text-align: right;"">Price</th>
                                                <th style=""text-align: right;"">Value (USD)</th>
                                                <th>Memo</th>
                                            </tr>
                                        </thead>
                                        <tbody id=""pnl-transfers-body""></tbody>
                                    </table>
                                </div>

                                <h3 style=""margin: 20px 0 4px; font-size: 15px;"">Counterparties</h3>
                                <div style=""color: var(--text-secondary); font-size: 13px; margin-bottom: 8px;"">Tick wallets you own; they are added to <a href=""/page/my-wallets"">My Wallets</a>. Their transfers are left out, so moving tokens between your own wallets isn't counted as a sale or purchase.</div>
                                <div class=""data-table-wrap pnl-table-wrap"">
                                    <table class=""data-table"">
                                        <thead>
                                            <tr>
                                                <th>My wallet</th>
                                                <th>Address</th>
                                                <th style=""text-align: right;"">Received from (OPT)</th>
                                                <th style=""text-align: right;"">Sent to (OPT)</th>
                                                <th style=""text-align: right;"">Transfers</th>
                                            </tr>
                                        </thead>
                                        <tbody id=""pnl-counterparties-body""></tbody>
                                    </table>
                                </div>

                                <div id=""pnl-notes"" style=""color: var(--text-muted); font-size: 12px; margin-top: 12px; line-height: 1.5;""></div>
                            </div>
                        </div>
                    </div>
                    <script>
                        (function () {
                            const $ = (id) => document.getElementById(id);
                            const form = $('pnl-form');
                            const input = $('pnl-input');
                            const yearSelect = $('pnl-year');
                            const submit = $('pnl-submit');
                            const status = $('pnl-status');
                            const summary = $('pnl-summary');
                            const results = $('pnl-results');
                            const lossesOnly = $('pnl-losses-only');
                            // Distributions are taxed differently, so by default they are left out of P&L entirely.
                            const includeDist = $('pnl-include-dist');
                            try { includeDist.checked = localStorage.getItem('optic-pnl-include-dist') === '1'; } catch (e) { }
                            const EPS = 1e-9;
                            const CATEGORY = {
                                distribution: 'Node distribution',
                                reward: 'Staking reward',
                                wallet: 'Wallet',
                                fee: 'Network fee',
                                module: 'Module account',
                                unknown: 'Before tracked history',
                                unmatched: 'Not matched to a purchase'
                            };

                            const esc = (v) => String(v ?? '').replace(/[&<>\x22\x27]/g, ch => '&#' + ch.charCodeAt(0) + ';');
                            // A send whose memo says gift or loan is not a sale.
                            const memoKind = (e) => e.direction === 'out' && e.category === 'wallet'
                                ? (/\bgift/i.test(e.memo || '') ? 'gift' : /\bloan/i.test(e.memo || '') ? 'loan' : null)
                                : null;
                            const KIND_LABEL = { gift: 'Gift (memo)', loan: 'Loan (memo)' };
                            const showRepaid = $('pnl-show-repaid');

                            // A loan that came back is left out of P&L entirely: OPT received later from the address a
                            // loan went to repays it (oldest loan first), and the repaid part of both the loan and the
                            // repayment is dropped. Anything received beyond the amount lent is a normal receipt.
                            // Sets e.repaidOpt on loan sends and repayment receipts; e.loanRepayment marks receipts.
                            const pairLoans = (entries) => {
                                const open = new Map(); // counterparty -> loan sends with an unpaid balance, oldest first
                                for (const e of entries) {
                                    e.repaidOpt = 0;
                                    e.loanRepayment = false;
                                    if (e.category !== 'wallet' || excluded.has(e.counterparty)) continue;
                                    if (e.direction === 'out') {
                                        if (memoKind(e) === 'loan') {
                                            if (!open.has(e.counterparty)) open.set(e.counterparty, []);
                                            open.get(e.counterparty).push(e);
                                        }
                                        continue;
                                    }
                                    const loans = open.get(e.counterparty);
                                    let left = e.amountOpt;
                                    while (loans && loans.length && left > EPS) {
                                        const loan = loans[0];
                                        const qty = Math.min(loan.amountOpt - loan.repaidOpt, left);
                                        loan.repaidOpt += qty;
                                        e.repaidOpt += qty;
                                        left -= qty;
                                        if (loan.amountOpt - loan.repaidOpt <= EPS) loans.shift();
                                    }
                                    e.loanRepayment = e.repaidOpt > EPS;
                                }
                            };
                            // Amount that still counts after removing the repaid-loan part.
                            const netOpt = (e) => Math.max(0, e.amountOpt - (e.repaidOpt || 0));
                            const fullyRepaid = (e) => (e.repaidOpt || 0) > EPS && netOpt(e) <= EPS;
                            const exchangeOf = (a) => window.opticWallets.exchange(a);
                            // Describes a transfer with a saved exchange, e.g. Exchange · Coinstore, sold (your account).
                            const exchangeLabel = (e) => {
                                const x = exchangeOf(e.counterparty);
                                if (!x) return null;
                                let text = `<strong>Exchange · ${esc(x.n)}</strong>, ${e.direction === 'out' ? 'sold' : 'bought'}`;
                                if (e.direction === 'out' && x.m) {
                                    text += (e.memo || '').trim() === x.m
                                        ? ' (your account)'
                                        : ` <span style=""color: var(--warning);"" title=""Memo differs from your saved deposit memo"">(memo ${e.memo ? 'differs' : 'missing'}: not your account?)</span>`;
                                }
                                return text;
                            };
                            const counterpartyName = (a) => { const x = exchangeOf(a); return x ? x.n : short(a); };
                            const fmtOpt = (v) => Number(v || 0).toLocaleString('en-US', { maximumFractionDigits: 2 });
                            const fmtUsd = (v) => v == null ? '-' : (v < 0 ? '-$' : '$') + Math.abs(Number(v)).toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
                            // No sign on amounts that round to $0.00.
                            const fmtSigned = (v) => v == null ? '-' : (v >= 0.005 ? '+' : v <= -0.005 ? '-' : '') + '$' + Math.abs(v).toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
                            const fmtPrice = (v) => v == null ? '-' : '$' + Number(v).toLocaleString('en-US', { minimumFractionDigits: 6, maximumFractionDigits: 6 });
                            const fmtTime = (v) => String(v || '').replace('T', ' ').replace(/\.\d+/, '').replace('Z', '');
                            const fmtDay = (v) => fmtTime(v).slice(0, 10);
                            const short = (a) => a.length > 20 ? a.slice(0, 12) + '...' + a.slice(-6) : a;
                            const tile = (label, value, sub) => `<div class=""metric-card""><div style=""color: var(--text-secondary); font-size: 12px;"">${label}</div><div style=""font-size: 18px; font-weight: 600; margin-top: 4px;"">${value}</div>${sub ? `<div style=""font-size: 12px; color: var(--text-secondary); margin-top: 2px;"">${sub}</div>` : ''}</div>`;
                            const gainCell = (v, partial) => {
                                if (v == null) return '<td class=""amount-cell"">-</td>';
                                const loss = v < -0.005;
                                const label = loss ? 'Loss' : v > 0.005 ? 'Gain' : '';
                                return `<td class=""amount-cell"" style=""${loss ? 'color: var(--error); font-weight: 600;' : ''}"" title=""${partial ? 'Cost or sale price unknown for part of this amount; only the priced part is included' : label}"">${fmtSigned(v)}${partial ? '*' : ''}${label ? ` <span style=""font-size: 11px;"">${label}</span>` : ''}</td>`;
                            };

                            const firstYear = 2024;
                            const currentYear = new Date().getUTCFullYear();
                            yearSelect.innerHTML = '<option value="""">All years</option>' +
                                Array.from({ length: currentYear - firstYear + 1 }, (_, i) => currentYear - i).map(y => `<option value=""${y}"">${y}</option>`).join('');

                            let loaded = null;        // { address, data }
                            let excluded = new Set(); // counterparties marked as the user's own wallets
                            let view = null;          // last computed FIFO result
                            const expanded = new Set();

                            // The user's other wallets (from My Wallets) are the counterparties left out of P&L.
                            const refreshExcluded = () => {
                                excluded = new Set(window.opticWallets.list().map(w => w.a).filter(a => !loaded || a !== loaded.address));
                            };
                            const ownLabel = (a) => window.opticWallets.label(a) || 'My wallet';
                            const renderWallets = () => window.opticWallets.renderSwitcher(
                                $('pnl-wallets'),
                                loaded ? loaded.address : null,
                                (address) => { input.value = address; run(address); });

                            // Walks the ledger oldest first. Receipts that are income or purchases become lots
                            // (cost = market price when received); sends to wallets are sales matched to the oldest lots.
                            const computeFifo = (entries) => {
                                const lots = [];
                                const sales = [];
                                let feeOpt = 0;
                                const giftsLoans = [];
                                pairLoans(entries);
                                for (const e of entries) {
                                    if (e.category === 'wallet' && excluded.has(e.counterparty)) continue;
                                    if (fullyRepaid(e)) continue;
                                    if (e.direction === 'in') {
                                        if ((e.category === 'distribution' && includeDist.checked) || e.category === 'reward' || e.category === 'wallet')
                                            lots.push({ time: e.timeUtc, qty: netOpt(e), price: e.priceUsd, source: e.category });
                                        continue; // module receipts return the wallet's own tokens (e.g. unbonding)
                                    }
                                    if (e.category === 'module') continue;
                                    if (e.category === 'fee') feeOpt += e.amountOpt;

                                    // Gifts and loans leave the wallet (using up the oldest tokens) but aren't sales.
                                    const kind = memoKind(e);
                                    if (kind) giftsLoans.push({ kind, time: e.timeUtc, qty: netOpt(e), cost: 0 });
                                    const sale = e.category === 'wallet' && !kind
                                        ? { time: e.timeUtc, txHash: e.txHash, counterparty: e.counterparty, memo: e.memo, qty: e.amountOpt, price: e.priceUsd, parts: [] }
                                        : null;
                                    let remaining = netOpt(e);
                                    while (remaining > EPS && lots.length) {
                                        const lot = lots[0];
                                        const qty = Math.min(lot.qty, remaining);
                                        if (sale) sale.parts.push({ qty, acqTime: lot.time, acqPrice: lot.price, source: lot.source });
                                        if (kind && lot.price != null) giftsLoans[giftsLoans.length - 1].cost += qty * lot.price;
                                        lot.qty -= qty;
                                        remaining -= qty;
                                        if (lot.qty <= EPS) lots.shift();
                                    }
                                    if (sale && remaining > EPS) sale.parts.push({ qty: remaining, acqTime: null, acqPrice: null, source: includeDist.checked ? 'unknown' : 'unmatched' });
                                    if (sale) sales.push(sale);
                                }

                                for (const s of sales) {
                                    const saleMs = Date.parse(s.time);
                                    s.proceeds = s.price != null ? s.qty * s.price : null;
                                    s.cost = 0; s.gain = 0; s.shortGain = 0; s.longGain = 0; s.unknownQty = 0;
                                    let hasShort = false, hasLong = false;
                                    for (const p of s.parts) {
                                        p.long = p.acqTime != null && (saleMs - Date.parse(p.acqTime)) / 86400000 > 365;
                                        if (p.acqTime != null) { if (p.long) hasLong = true; else hasShort = true; }
                                        if (p.acqPrice == null || s.price == null) { s.unknownQty += p.qty; continue; }
                                        const g = p.qty * (s.price - p.acqPrice);
                                        s.cost += p.qty * p.acqPrice;
                                        s.gain += g;
                                        if (p.long) s.longGain += g; else s.shortGain += g;
                                    }
                                    if (s.unknownQty >= s.qty - EPS) { s.cost = null; s.gain = null; }
                                    s.term = hasLong && hasShort ? 'Mixed' : hasLong ? 'Long' : hasShort ? 'Short' : '-';
                                    const dated = s.parts.filter(p => p.acqTime);
                                    s.acqFrom = dated.length ? dated[0].acqTime : null;
                                    s.acqTo = dated.length ? dated[dated.length - 1].acqTime : null;
                                }
                                return { lots, sales, feeOpt, giftsLoans };
                            };

                            const inYear = (time) => !yearSelect.value || String(time).startsWith(yearSelect.value);

                            const renderSummary = () => {
                                const data = loaded.data;
                                const entries = data.entries.filter(e => inYear(e.timeUtc) && !(e.category === 'wallet' && excluded.has(e.counterparty)));
                                const sum = (list, f) => list.reduce((s, e) => s + (f(e) || 0), 0);
                                const value = (e) => e.priceUsd != null ? e.amountOpt * e.priceUsd : 0;
                                const by = (dir, cat) => entries.filter(e => e.direction === dir && e.category === cat);
                                const dist = by('in', 'distribution'), other = by('in', 'wallet').filter(e => !fullyRepaid(e)), rewards = by('in', 'reward');
                                const repaidLoans = entries.filter(e => e.direction === 'out' && e.repaidOpt > EPS);
                                const repayments = entries.filter(e => e.loanRepayment);
                                const sent = by('out', 'wallet').filter(e => !memoKind(e));
                                const gl = view.giftsLoans.filter(g => inYear(g.time));
                                const ex = entries.filter(e => e.category === 'wallet' && exchangeOf(e.counterparty));
                                const exNames = [...new Set(ex.map(e => exchangeOf(e.counterparty).n))];
                                const gifted = gl.filter(g => g.kind === 'gift'), lent = gl.filter(g => g.kind === 'loan');
                                const own = data.entries.filter(e => inYear(e.timeUtc) && e.category === 'wallet' && excluded.has(e.counterparty));
                                const ownIn = own.filter(e => e.direction === 'in'), ownOut = own.filter(e => e.direction === 'out');
                                const sales = view.sales.filter(s => inYear(s.time));
                                const gains = sum(sales, s => s.gain > 0 ? s.gain : 0);
                                const losses = sum(sales, s => s.gain < 0 ? s.gain : 0);
                                const net = gains + losses;
                                const lossCount = sales.filter(s => s.gain < -0.005).length;

                                const holdQty = sum(view.lots, l => l.qty);
                                const pricedLots = view.lots.filter(l => l.price != null);
                                const holdCost = sum(pricedLots, l => l.qty * l.price);
                                const unpricedQty = holdQty - sum(pricedLots, l => l.qty);
                                const holdValue = data.latestPrice != null ? holdQty * data.latestPrice : null;
                                // Unrealized only over lots with a known cost; unpriced lots would overstate it.
                                const unrealized = data.latestPrice != null ? sum(pricedLots, l => l.qty) * data.latestPrice - holdCost : null;
                                const yearLabel = yearSelect.value || 'all years';

                                summary.innerHTML =
                                    (includeDist.checked
                                        ? tile('Received: node distributions', fmtOpt(sum(dist, e => e.amountOpt)) + ' OPT', `${fmtUsd(sum(dist, value))} when received · ${dist.length.toLocaleString('en-US')} payouts`)
                                        : '') +
                                    tile('Received: other wallets', fmtOpt(sum(other, netOpt)) + ' OPT', `${fmtUsd(sum(other, e => netOpt(e) * (e.priceUsd || 0)))} when received · ${other.length.toLocaleString('en-US')} transfers` + (repayments.length ? ' · loan repayments left out' : '')) +
                                    tile('Received: staking rewards', fmtOpt(sum(rewards, e => e.amountOpt)) + ' OPT', `${fmtUsd(sum(rewards, value))} when received`) +
                                    tile('Sold (sent to other wallets)', fmtOpt(sum(sent, e => e.amountOpt)) + ' OPT', `${fmtUsd(sum(sent, value))} when sent · ${sent.length.toLocaleString('en-US')} transfers`) +
                                    tile('Exchanges', `${fmtOpt(sum(ex.filter(e => e.direction === 'out'), e => e.amountOpt))} sent · ${fmtOpt(sum(ex.filter(e => e.direction === 'in'), e => e.amountOpt))} received`,
                                        ex.length ? esc(exNames.join(', ')) : 'Add exchange addresses on <a href=""/page/my-wallets"">My Wallets</a>') +
                                    tile('Gifts &amp; loans sent', `${fmtOpt(sum(gifted, g => g.qty))} gifted · ${fmtOpt(sum(lent, g => g.qty))} lent`,
                                        gl.length ? `${gl.length.toLocaleString('en-US')} sends with gift/loan memo · cost basis ${fmtUsd(sum(gl, g => g.cost))}; not sales` : 'Sends whose memo says gift or loan') +
                                    tile('Loans repaid (left out)', `${fmtOpt(sum(repaidLoans, e => e.repaidOpt))} OPT`,
                                        repaidLoans.length
                                            ? `${repaidLoans.length.toLocaleString('en-US')} loan(s), ${repayments.length.toLocaleString('en-US')} repayment(s); not purchases, sales or gifts`
                                            : 'Loans (memo) later returned by the same address') +
                                    tile('Between my wallets', `${fmtOpt(sum(ownIn, e => e.amountOpt))} in · ${fmtOpt(sum(ownOut, e => e.amountOpt))} out`,
                                        own.length ? `${own.length.toLocaleString('en-US')} transfers, not counted as purchases or sales` : 'Add your wallets on <a href=""/page/my-wallets"">My Wallets</a>') +
                                    tile('Realized gains', fmtSigned(gains), `${yearLabel}`) +
                                    tile('Realized losses', `<span style=""color: ${losses < 0 ? 'var(--error)' : 'inherit'};"">${fmtSigned(losses)}</span>`, `${lossCount.toLocaleString('en-US')} sale(s) at a loss`) +
                                    tile('Net realized P&amp;L', `<span style=""color: ${net < -0.005 ? 'var(--error)' : 'inherit'};"">${fmtSigned(net)}</span>`, `Short-term ${fmtSigned(sum(sales, s => s.shortGain))} · Long-term ${fmtSigned(sum(sales, s => s.longGain))}`) +
                                    tile(includeDist.checked ? 'Current holdings (FIFO)' : 'Unsold purchases (FIFO)', fmtOpt(holdQty) + ' OPT', `Cost ${fmtUsd(holdCost)} · Value ${fmtUsd(holdValue)} at ${fmtPrice(data.latestPrice)}` +
                                        (unrealized != null ? ` · Unrealized ${fmtSigned(unrealized)}` : '') +
                                        (unpricedQty > EPS ? ` · ${fmtOpt(unpricedQty)} OPT has no cost basis` : ''));
                                summary.style.display = 'grid';
                            };

                            const renderSales = () => {
                                const rows = view.sales.filter(s => inYear(s.time) && (!lossesOnly.checked || s.gain < -0.005)).reverse();
                                const body = $('pnl-sales-body');
                                if (rows.length === 0) {
                                    body.innerHTML = `<tr><td colspan=""10"" style=""text-align: center; color: var(--text-secondary);"">${lossesOnly.checked ? 'No sales at a loss' : 'No sends to other wallets'}${yearSelect.value ? ' in ' + yearSelect.value : ''}</td></tr>`;
                                    return;
                                }
                                body.innerHTML = rows.map(s => {
                                    const key = s.txHash + '|' + s.counterparty;
                                    const open = expanded.has(key);
                                    const acquired = s.acqFrom ? (fmtDay(s.acqFrom) === fmtDay(s.acqTo) ? fmtDay(s.acqFrom) : fmtDay(s.acqFrom) + ' to ' + fmtDay(s.acqTo))
                                        : s.parts.every(p => p.source === 'unmatched') ? 'Not matched to a purchase' : 'Unknown';
                                    let html = `
                                        <tr data-sale=""${esc(key)}"" style=""cursor: pointer;"" title=""Click to show the tokens this sale used"">
                                            <td>${open ? '&#9662;' : '&#9656;'} ${fmtTime(s.time)}</td>
                                            <td class=""address-cell"" title=""${esc(s.counterparty)}"">${esc(counterpartyName(s.counterparty))}</td>
                                            <td class=""amount-cell"">${fmtOpt(s.qty)}</td>
                                            <td class=""amount-cell"">${fmtPrice(s.price)}</td>
                                            <td class=""amount-cell"">${fmtUsd(s.proceeds)}</td>
                                            <td class=""amount-cell"">${fmtUsd(s.cost)}</td>
                                            ${gainCell(s.gain, s.unknownQty > EPS && s.gain != null)}
                                            <td>${acquired}</td>
                                            <td>${s.term}</td>
                                            <td class=""memo-cell"" title=""${esc(s.memo)}"">${esc(s.memo)}</td>
                                        </tr>`;
                                    if (open) {
                                        // Collapse same-day, same-source lots so long payout histories stay readable.
                                        const groups = [];
                                        for (const p of s.parts) {
                                            const day = p.acqTime ? fmtDay(p.acqTime) : 'Unknown';
                                            const last = groups[groups.length - 1];
                                            if (last && last.day === day && last.source === p.source) { last.qty += p.qty; last.cost += p.acqPrice != null ? p.qty * p.acqPrice : 0; last.unpriced ||= p.acqPrice == null; }
                                            else groups.push({ day, source: p.source, qty: p.qty, cost: p.acqPrice != null ? p.qty * p.acqPrice : 0, unpriced: p.acqPrice == null, long: p.long });
                                        }
                                        html += groups.map(g => {
                                            const unit = !g.unpriced && g.qty > 0 ? g.cost / g.qty : null;
                                            const gain = unit != null && s.price != null ? g.qty * (s.price - unit) : null;
                                            return `<tr style=""background: var(--surface-light); font-size: 12px;"">
                                                <td style=""padding-left: 28px;"">${g.day}</td>
                                                <td>${CATEGORY[g.source] || g.source}</td>
                                                <td class=""amount-cell"">${fmtOpt(g.qty)}</td>
                                                <td class=""amount-cell"" title=""Average cost per OPT"">${fmtPrice(unit)}</td>
                                                <td class=""amount-cell"">${s.price != null ? fmtUsd(g.qty * s.price) : '-'}</td>
                                                <td class=""amount-cell"">${unit != null ? fmtUsd(g.cost) : '-'}</td>
                                                ${gainCell(gain, false)}
                                                <td></td>
                                                <td>${g.day === 'Unknown' ? '-' : g.long ? 'Long' : 'Short'}</td>
                                                <td></td>
                                            </tr>`;
                                        }).join('');
                                    }
                                    return html;
                                }).join('');
                            };

                            const transferType = (e) => {
                                const repaidNote = e.repaidOpt > EPS
                                    ? (fullyRepaid(e) ? ' · repaid, left out' : ` · ${fmtOpt(e.repaidOpt)} OPT repaid, left out`)
                                    : '';
                                if (e.loanRepayment) return `<strong>Loan repayment</strong>${fullyRepaid(e) ? ', left out' : ` · ${fmtOpt(netOpt(e))} OPT above the loan counts as a purchase`}`;
                                if (memoKind(e)) return `<strong>${KIND_LABEL[memoKind(e)]}</strong>${repaidNote}`;
                                return exchangeLabel(e) || CATEGORY[e.category] || e.category;
                            };
                            const transferTypeText = (e) => e.loanRepayment ? 'Loan repayment' : memoKind(e) ? KIND_LABEL[memoKind(e)] : CATEGORY[e.category] || e.category;

                            const renderTransfers = () => {
                                const rows = loaded.data.entries.filter(e => e.category !== 'distribution' && inYear(e.timeUtc) && (showRepaid.checked || !fullyRepaid(e))).slice().reverse();
                                const body = $('pnl-transfers-body');
                                body.innerHTML = rows.length === 0
                                    ? `<tr><td colspan=""8"" style=""text-align: center; color: var(--text-secondary);"">No receipts or sends outside node distributions${yearSelect.value ? ' in ' + yearSelect.value : ''}</td></tr>`
                                    : rows.map(e => {
                                        const own = e.category === 'wallet' && excluded.has(e.counterparty);
                                        const repaid = fullyRepaid(e);
                                        return `<tr style=""${own || repaid ? 'background: var(--surface-light);' : ''}${repaid ? ' color: var(--text-secondary);' : ''}"">
                                            <td>${fmtTime(e.timeUtc)}</td>
                                            <td>${e.direction === 'in' ? 'Received' : 'Sent'}</td>
                                            <td class=""address-cell"" title=""${esc(e.counterparty)}"">${esc(short(e.counterparty))}</td>
                                            <td>${own ? `<strong>My wallet</strong> · ${esc(ownLabel(e.counterparty))}` : transferType(e)}</td>
                                            <td class=""amount-cell"">${fmtOpt(e.amountOpt)}</td>
                                            <td class=""amount-cell"">${fmtPrice(e.priceUsd)}</td>
                                            <td class=""amount-cell"">${e.priceUsd != null ? fmtUsd(e.amountOpt * e.priceUsd) : '-'}</td>
                                            <td class=""memo-cell"" title=""${esc(e.memo)}"">${esc(e.memo)}</td>
                                        </tr>`;
                                    }).join('');
                            };

                            const renderCounterparties = () => {
                                const map = new Map();
                                for (const e of loaded.data.entries) {
                                    if (e.category !== 'wallet') continue;
                                    const c = map.get(e.counterparty) || { address: e.counterparty, inOpt: 0, outOpt: 0, count: 0 };
                                    if (e.direction === 'in') c.inOpt += e.amountOpt; else c.outOpt += e.amountOpt;
                                    c.count++;
                                    map.set(e.counterparty, c);
                                }
                                // Own wallets first, then by volume.
                                const rows = [...map.values()].sort((a, b) =>
                                    (excluded.has(b.address) - excluded.has(a.address)) || ((b.inOpt + b.outOpt) - (a.inOpt + a.outOpt)));
                                $('pnl-counterparties-body').innerHTML = rows.length === 0
                                    ? '<tr><td colspan=""5"" style=""text-align: center; color: var(--text-secondary);"">No wallet-to-wallet transfers</td></tr>'
                                    : rows.map(c => `<tr>
                                        <td><input type=""checkbox"" data-own=""${esc(c.address)}"" ${excluded.has(c.address) ? 'checked' : ''} aria-label=""Mark ${esc(c.address)} as my wallet""></td>
                                        <td class=""address-cell"">${esc(c.address)}${excluded.has(c.address) ? `<div style=""font-size: 11px; color: var(--text-secondary); font-family: inherit;"">${esc(ownLabel(c.address))}</div>` : exchangeOf(c.address) ? `<div style=""font-size: 11px; color: var(--text-secondary); font-family: inherit;"">Exchange · ${esc(exchangeOf(c.address).n)}</div>` : ''}</td>
                                        <td class=""amount-cell"">${fmtOpt(c.inOpt)}</td>
                                        <td class=""amount-cell"">${fmtOpt(c.outOpt)}</td>
                                        <td class=""amount-cell"">${c.count.toLocaleString('en-US')}</td>
                                    </tr>`).join('');
                            };

                            const renderNotes = () => {
                                const d = loaded.data;
                                const notes = [
                                    'How this works: node distributions and staking rewards are counted as acquired at the market price when received; receipts from other wallets and exchanges are treated as purchases at the market price at that time; each send to another wallet or an exchange is treated as a sale at the market price at that time and matched to the oldest unsold tokens first. For exchanges, the actual trade may have happened later at a different price.',
                                    `Prices are ${esc(d.priceSource)} hourly closes, not the price you actually traded at on an exchange. Wallets saved on My Wallets (or ticked above) are treated as yours, so transfers between them aren't counted. Sends whose memo contains gift or loan are treated as gifts or loans: they use up the oldest tokens but are not sales. A loan that the same address later paid back is left out entirely, both the loan and the repayment (any amount received beyond the loan counts as a normal receipt); a loan returned from a different address isn't recognised. Long-term = held more than 365 days. This is an estimate, not tax advice.`
                                ];
                                if (view.feeOpt > 0) notes.push(`Network fees (${fmtOpt(view.feeOpt)} OPT) reduce holdings but aren't counted as sales.`);
                                const unknown = view.sales.reduce((s, x) => s + x.unknownQty, 0);
                                if (!includeDist.checked) notes.unshift('Node distributions are left out (they are taxed differently): they are not counted as tokens acquired, so sends are matched only to tokens bought or received from other wallets. Tick ""Count node distributions as acquired tokens"" to include them at their market value when received.');
                                if (unknown > EPS) notes.push(`* ${fmtOpt(unknown)} OPT of sales have no sale price or no cost basis (market data ends ${d.priceDataTo ? fmtDay(d.priceDataTo) : 'unknown'}, ${includeDist.checked ? 'or tokens came from before the tracked history' : 'or more was sent than was bought'}) and are left out of gains and losses.`);
                                if (d.truncated) notes.push('History was capped at 20,000 transactions per direction; older activity may be missing.');
                                $('pnl-notes').innerHTML = notes.map(n => `<p style=""margin: 0 0 4px;"">${n}</p>`).join('');
                            };

                            const updateUrl = () => {
                                const params = new URLSearchParams();
                                if (loaded) params.set('address', loaded.address);
                                if (yearSelect.value) params.set('year', yearSelect.value);
                                const qs = params.toString();
                                history.replaceState(null, '', qs ? '?' + qs : location.pathname);
                            };

                            const renderAll = (recompute) => {
                                if (!loaded) return;
                                if (recompute || !view) view = computeFifo(loaded.data.entries);
                                updateUrl();
                                renderSummary();
                                renderSales();
                                renderTransfers();
                                renderCounterparties();
                                renderNotes();
                                results.hidden = false;
                                status.textContent = `${loaded.data.entries.length.toLocaleString('en-US')} transfers found for ${loaded.address}.`;
                            };

                            const downloadCsv = (filename, header, rows) => {
                                const cell = (v) => { const s = v == null ? '' : String(v); return /[,\x22\n]/.test(s) ? '\x22' + s.replace(/\x22/g, '\x22\x22') + '\x22' : s; };
                                const text = [header, ...rows].map(r => r.map(cell).join(',')).join('\n');
                                const a = document.createElement('a');
                                a.href = URL.createObjectURL(new Blob([text], { type: 'text/csv' }));
                                a.download = filename;
                                a.click();
                                setTimeout(() => URL.revokeObjectURL(a.href), 1000);
                            };
                            const suffix = () => loaded.address + (yearSelect.value ? '-' + yearSelect.value : '');

                            $('pnl-sales-csv').addEventListener('click', (e) => {
                                e.preventDefault();
                                if (!view) return;
                                const rows = [];
                                for (const s of view.sales.filter(x => inYear(x.time))) {
                                    for (const p of s.parts) {
                                        const proceeds = s.price != null ? p.qty * s.price : null;
                                        const cost = p.acqPrice != null ? p.qty * p.acqPrice : null;
                                        rows.push([fmtTime(s.time), s.txHash, s.counterparty, s.memo, p.qty, s.price, proceeds, p.acqTime ? fmtTime(p.acqTime) : '', CATEGORY[p.source] || p.source, p.acqPrice, cost,
                                            proceeds != null && cost != null ? proceeds - cost : '', p.acqTime ? (p.long ? 'Long' : 'Short') : '']);
                                    }
                                }
                                downloadCsv(`pnl-sales-${suffix()}.csv`,
                                    ['sold_utc', 'tx_hash', 'sent_to', 'memo', 'qty_opt', 'sale_price_usd', 'proceeds_usd', 'acquired_utc', 'acquired_from', 'cost_price_usd', 'cost_basis_usd', 'gain_loss_usd', 'term'], rows);
                            });

                            $('pnl-transfers-csv').addEventListener('click', (e) => {
                                e.preventDefault();
                                if (!loaded) return;
                                const rows = loaded.data.entries.filter(x => x.category !== 'distribution' && inYear(x.timeUtc) && (showRepaid.checked || !fullyRepaid(x))).map(x => [
                                    fmtTime(x.timeUtc), x.height, x.txHash, x.direction === 'in' ? 'received' : 'sent', x.counterparty, transferTypeText(x),
                                    excluded.has(x.counterparty) ? 'yes' : '', exchangeOf(x.counterparty)?.n || '', x.amountOpt, x.repaidOpt || 0, x.priceUsd, x.priceUsd != null ? x.amountOpt * x.priceUsd : '', x.memo]);
                                downloadCsv(`pnl-transfers-${suffix()}.csv`,
                                    ['time_utc', 'height', 'tx_hash', 'direction', 'counterparty', 'type', 'my_wallet', 'exchange', 'amount_opt', 'loan_repaid_opt', 'price_usd', 'value_usd', 'memo'], rows);
                            });

                            $('pnl-sales-body').addEventListener('click', (e) => {
                                const row = e.target.closest('tr[data-sale]');
                                if (!row) return;
                                const key = row.dataset.sale;
                                if (expanded.has(key)) expanded.delete(key); else expanded.add(key);
                                renderSales();
                            });

                            $('pnl-counterparties-body').addEventListener('change', (e) => {
                                const box = e.target.closest('input[data-own]');
                                if (!box) return;
                                if (box.checked) window.opticWallets.add(box.dataset.own); else window.opticWallets.remove(box.dataset.own);
                                refreshExcluded();
                                renderWallets();
                                renderAll(true);
                            });

                            lossesOnly.addEventListener('change', renderSales);
                            showRepaid.addEventListener('change', renderTransfers);
                            includeDist.addEventListener('change', () => {
                                try { localStorage.setItem('optic-pnl-include-dist', includeDist.checked ? '1' : '0'); } catch (e) { }
                                expanded.clear();
                                renderAll(true);
                            });
                            yearSelect.addEventListener('change', () => { if (loaded && loaded.address === input.value.trim().toLowerCase()) renderAll(false); else updateUrl(); });

                            const run = async (address) => {
                                const done = window.opticBusy(submit, status, 'Reading wallet history');
                                results.hidden = true;
                                summary.style.display = 'none';
                                status.textContent = 'Reading the wallet\x27s full transfer history from the node. Wallets with a long history can take a minute...';
                                try {
                                    const response = await fetch('/api/wallet/ledger?address=' + encodeURIComponent(address));
                                    const data = await response.json();
                                    if (!response.ok) throw new Error(data.error || 'Request failed');
                                    loaded = { address: data.address, data };
                                    refreshExcluded();
                                    expanded.clear();
                                    renderAll(true);
                                    renderWallets();
                                } catch (error) {
                                    loaded = null;
                                    view = null;
                                    status.textContent = 'Error: ' + error.message;
                                } finally {
                                    done();
                                }
                            };

                            form.addEventListener('submit', (e) => {
                                e.preventDefault();
                                const address = input.value.trim();
                                if (address) run(address);
                            });

                            renderWallets();
                            const params = new URLSearchParams(location.search);
                            const y = params.get('year');
                            if (y && [...yearSelect.options].some(o => o.value === y)) yearSelect.value = y;
                            // No address in the link: open the primary wallet.
                            const initial = params.get('address') || window.opticWallets.primary();
                            if (initial) {
                                input.value = initial;
                                run(initial);
                            }
                        })();
                    </script>
");
    }

    static void AppendTop100Card(System.Text.StringBuilder sb)
    {
        sb.Append(@"
                    <div class=""card"" style=""grid-column: 1 / -1;"">
                        <div class=""card-content"">
                            <div id=""top100-status"" style=""color: var(--text-secondary); margin-bottom: 8px;"">Loading top wallets...</div>
                            <div class=""data-table-wrap top100-table-wrap"">
                                <table class=""data-table"">
                                    <thead>
                                        <tr>
                                            <th class=""sortable sort-asc"" data-sort=""rank"">Rank</th>
                                            <th class=""sortable"" data-sort=""address"">Wallet</th>
                                            <th class=""sortable"" data-sort=""total"">Total</th>
                                            <th class=""sortable"" data-sort=""walletBalance"">Balance</th>
                                            <th class=""sortable"" data-sort=""staked"">Staked</th>
                                            <th class=""sortable"" data-sort=""unbonding"">Unbonding</th>
                                            <th class=""sortable"" data-sort=""totalLocked"">Locked</th>
                                            <th class=""sortable"" data-sort=""lock6Months"">6 Months</th>
                                            <th class=""sortable"" data-sort=""lock12Months"">12 Months</th>
                                            <th class=""sortable"" data-sort=""lock18Months"">18 Months</th>
                                            <th class=""sortable"" data-sort=""lock24Months"">24 Months</th>
                                        </tr>
                                    </thead>
                                    <tbody id=""top100-body"">
                                        <tr><td colspan=""11"" style=""text-align: center; color: var(--text-secondary);"">Loading...</td></tr>
                                    </tbody>
                                </table>
                            </div>
                        </div>
                    </div>
                    <script>
                        (async function () {
                            const status = document.getElementById('top100-status');
                            const body = document.getElementById('top100-body');
                            const headers = Array.from(document.querySelectorAll('.data-table th.sortable'));
                            const formatAmt = (value) => Math.round(value || 0).toLocaleString('en-US');
                            let wallets = [];
                            let sortKey = 'rank';
                            let sortDirection = 'asc';
                            const escapeHtml = (value) => String(value || '').replace(/[&<>\x22\x27]/g, ch => {
                                switch (ch.charCodeAt(0)) {
                                    case 38: return '&amp;';
                                    case 60: return '&lt;';
                                    case 62: return '&gt;';
                                    case 34: return '&quot;';
                                    case 39: return '&#39;';
                                    default: return ch;
                                }
                            });
                            const renderRows = () => {
                                const sorted = [...wallets].sort((a, b) => {
                                    let result;
                                    if (sortKey === 'address') {
                                        result = String(a.address || '').localeCompare(String(b.address || ''), 'en-US', { sensitivity: 'base' });
                                    } else {
                                        result = (Number(a[sortKey]) || 0) - (Number(b[sortKey]) || 0);
                                    }
                                    return sortDirection === 'asc' ? result : -result;
                                });

                                body.innerHTML = sorted.map(w => `
                                    <tr>
                                        <td class=""rank-cell"">${w.rank}</td>
                                        <td class=""address-cell"">${escapeHtml(w.address)}</td>
                                        <td class=""amount-cell"">${formatAmt(w.total)}</td>
                                        <td class=""amount-cell"">${formatAmt(w.walletBalance)}</td>
                                        <td class=""amount-cell"">${formatAmt(w.staked)}</td>
                                        <td class=""amount-cell"">${formatAmt(w.unbonding)}</td>
                                        <td class=""amount-cell"">${formatAmt(w.totalLocked)}</td>
                                        <td class=""amount-cell"">${formatAmt(w.lock6Months)}</td>
                                        <td class=""amount-cell"">${formatAmt(w.lock12Months)}</td>
                                        <td class=""amount-cell"">${formatAmt(w.lock18Months)}</td>
                                        <td class=""amount-cell"">${formatAmt(w.lock24Months)}</td>
                                    </tr>
                                `).join('');
                            };
                            const updateSortHeaders = () => {
                                headers.forEach(header => {
                                    header.classList.toggle('sort-asc', header.dataset.sort === sortKey && sortDirection === 'asc');
                                    header.classList.toggle('sort-desc', header.dataset.sort === sortKey && sortDirection === 'desc');
                                });
                            };

                            headers.forEach(header => {
                                header.addEventListener('click', () => {
                                    const nextKey = header.dataset.sort;
                                    if (sortKey === nextKey) {
                                        sortDirection = sortDirection === 'asc' ? 'desc' : 'asc';
                                    } else {
                                        sortKey = nextKey;
                                        sortDirection = nextKey === 'address' || nextKey === 'rank' ? 'asc' : 'desc';
                                    }
                                    updateSortHeaders();
                                    renderRows();
                                });
                            });

                            try {
                                const response = await fetch('/api/top100');
                                const data = await response.json();
                                if (!response.ok) throw new Error(data.error || 'Unable to load top wallets');
                                wallets = data.wallets || [];
                                if (wallets.length === 0) {
                                    status.textContent = 'No wallet balance data found. Generate wallet-balances.csv, then refresh this page.';
                                    body.innerHTML = '<tr><td colspan=""11"" style=""text-align: center; color: var(--text-secondary);"">No data available</td></tr>';
                                    return;
                                }

                                status.hidden = true;
                                updateSortHeaders();
                                renderRows();
                            } catch (error) {
                                status.textContent = 'Error loading top wallets: ' + error.message;
                                body.innerHTML = '<tr><td colspan=""11"" style=""text-align: center; color: var(--error);"">Error loading data</td></tr>';
                            }
                        })();
                    </script>
");
    }

    static void AppendWalletLocksReportCard(System.Text.StringBuilder sb)
    {
        sb.Append(@"
                    <div class=""card"">
                        <h2>Wallet Locks Report</h2>
                        <div class=""card-content"">
                            <form method=""post"" action=""/run"">
                                <input type=""hidden"" name=""mode"" value=""wallet-locks-report"">
                                <div class=""form-group"">
                                    <label>Wallet File (optional)</label>
                                    <input type=""text"" name=""wallet-file"" placeholder=""wallets.csv"">
                                </div>
                                <button type=""submit"">Generate Report</button>
                            </form>
                        </div>
                    </div>
");
    }

    static void AppendWalletLocksSummaryCard(System.Text.StringBuilder sb)
    {
        sb.Append(@"
                    <div class=""card"">
                        <h2>Wallet Locks Summary</h2>
                        <div class=""card-content"">
                            <form method=""post"" action=""/run"">
                                <input type=""hidden"" name=""mode"" value=""wallet-locks-summary"">
                                <div class=""form-group"">
                                    <label>Wallet File (optional)</label>
                                    <input type=""text"" name=""wallet-file"" placeholder=""wallets.csv"">
                                </div>
                                <button type=""submit"">Get Summary</button>
                            </form>
                        </div>
                    </div>
");
    }

    static void AppendTotalStakedCard(System.Text.StringBuilder sb)
    {
        sb.Append(@"
                    <div class=""card"">
                        <h2>Total Staked</h2>
                        <div class=""card-content"">
                            <form method=""post"" action=""/run"">
                                <input type=""hidden"" name=""mode"" value=""total-staked"">
                                <button type=""submit"">Calculate Staking</button>
                            </form>
                        </div>
                    </div>
");
    }

    static void AppendTotalDistributedCard(System.Text.StringBuilder sb)
    {
        sb.Append(@"
                    <div class=""card"">
                        <h2>Total Distributed</h2>
                        <div class=""card-content"">
                            <form method=""post"" action=""/run"">
                                <input type=""hidden"" name=""mode"" value=""total-distributed"">
                                <button type=""submit"">Calculate Distributions</button>
                            </form>
                        </div>
                    </div>
");
    }

    static void AppendTotalsAllCard(System.Text.StringBuilder sb)
    {
        sb.Append(@"
                    <div class=""card"">
                        <h2>Totals (All)</h2>
                        <div class=""card-content"">
                            <form method=""post"" action=""/run"">
                                <input type=""hidden"" name=""mode"" value=""totals-all"">
                                <button type=""submit"">Calculate All Totals</button>
                            </form>
                        </div>
                    </div>
");
    }

    static void AppendCmcDailyCard(System.Text.StringBuilder sb)
    {
        sb.Append(@"
                    <div class=""card"">
                        <h2>CMC Daily Export</h2>
                        <div class=""card-content"">
                            <form method=""post"" action=""/run"">
                                <input type=""hidden"" name=""mode"" value=""cmc-daily"">
                                <button type=""submit"">Export CMC Data</button>
                            </form>
                        </div>
                    </div>
");
    }

    static void AppendCustomArgsCard(System.Text.StringBuilder sb)
    {
        sb.Append(@"
                    <div class=""card"">
                        <h2>Custom Args</h2>
                        <div class=""card-content"">
                            <form method=""post"" action=""/run"">
                                <input type=""hidden"" name=""mode"" value=""custom"">
                                <div class=""form-group"">
                                    <label>Custom Arguments</label>
                                    <input type=""text"" name=""custom-args"" placeholder=""--arg value"" required>
                                </div>
                                <button type=""submit"">Run Custom</button>
                            </form>
                        </div>
                    </div>
");
    }

    static void AppendDataSyncCard(System.Text.StringBuilder sb)
    {
        sb.Append(@"
                    <div class=""card"" style=""grid-column: 1 / -1; padding: 0; display: flex; flex-direction: column;"">
                        <h2 style=""padding: 16px; margin: 0; border-bottom: 1px solid var(--border);"">Data Sync</h2>
                        <div id=""sync-data"" style=""flex: 1; overflow: auto; padding: 16px; min-height: 500px; display: flex; align-items: center; justify-content: center; color: var(--text-secondary);"">
                            Loading synced data...
                        </div>
                        <script>
                            async function loadSyncData() {
                                try {
                                    const response = await fetch('/api/daily-stats/all');
                                    const data = await response.json();
                                    const stats = data.stats || [];
                                    
                                    if (stats.length === 0) {
                                        document.getElementById('sync-data').innerHTML = '<div style=""color: var(--text-secondary);"">No synced data available. Run --sync-daily --backfill to populate data.</div>';
                                        return;
                                    }
                                    
                                    let html = '<div style=""width: 100%; overflow-x: auto;""><table style=""width: 100%; border-collapse: collapse; font-size: 13px;"">';
                                    html += '<thead><tr style=""background: var(--surface-light); position: sticky; top: 0;"">';
                                    html += '<th style=""padding: 12px; text-align: left; border-bottom: 2px solid var(--border); font-weight: 600;"">Date</th>';
                                    html += '<th style=""padding: 12px; text-align: right; border-bottom: 2px solid var(--border); font-weight: 600;"">Start Block</th>';
                                    html += '<th style=""padding: 12px; text-align: right; border-bottom: 2px solid var(--border); font-weight: 600;"">End Block</th>';
                                    html += '<th style=""padding: 12px; text-align: right; border-bottom: 2px solid var(--border); font-weight: 600;"">Wallets</th>';
                                    html += '<th style=""padding: 12px; text-align: right; border-bottom: 2px solid var(--border); font-weight: 600;"">Active</th>';
                                    html += '<th style=""padding: 12px; text-align: right; border-bottom: 2px solid var(--border); font-weight: 600;"">Supply (OPT)</th>';
                                    html += '<th style=""padding: 12px; text-align: right; border-bottom: 2px solid var(--border); font-weight: 600;"">Staked (OPT)</th>';
                                    html += '<th style=""padding: 12px; text-align: right; border-bottom: 2px solid var(--border); font-weight: 600;"">Unbonding (OPT)</th>';
                                    html += '<th style=""padding: 12px; text-align: right; border-bottom: 2px solid var(--border); font-weight: 600;"">Total (OPT)</th>';
                                    html += '<th style=""padding: 12px; text-align: right; border-bottom: 2px solid var(--border); font-weight: 600;"">Locked (OPT)</th>';
                                    html += '<th style=""padding: 12px; text-align: right; border-bottom: 2px solid var(--border); font-weight: 600;"">Txs</th>';
                                    html += '</tr></thead><tbody>';
                                    
                                    stats.forEach((row, idx) => {
                                        const bgColor = idx % 2 === 0 ? 'transparent' : 'var(--surface-light)';
                                        html += '<tr style=""background: ' + bgColor + '; border-bottom: 1px solid var(--border);"">';
                                        html += '<td style=""padding: 12px; text-align: left; font-weight: 500;"">' + (row.date || '') + '</td>';
                                        html += '<td style=""padding: 12px; text-align: right;"">' + (row.startBlockNumber ? row.startBlockNumber.toLocaleString() : '-') + '</td>';
                                        html += '<td style=""padding: 12px; text-align: right;"">' + (row.endBlockNumber ? row.endBlockNumber.toLocaleString() : '-') + '</td>';
                                        html += '<td style=""padding: 12px; text-align: right;"">' + (row.totalWallets ? row.totalWallets.toLocaleString() : '0') + '</td>';
                                        html += '<td style=""padding: 12px; text-align: right;"">' + (row.activeWallets ? row.activeWallets.toLocaleString() : '0') + '</td>';
                                        html += '<td style=""padding: 12px; text-align: right;"">' + (row.totalSupply ? Math.round(row.totalSupply).toLocaleString('en-US') : '0') + '</td>';
                                        html += '<td style=""padding: 12px; text-align: right;"">' + (row.totalStaked ? Math.round(row.totalStaked).toLocaleString('en-US') : '0') + '</td>';
                                        html += '<td style=""padding: 12px; text-align: right;"">' + (row.totalUnbonding ? Math.round(row.totalUnbonding).toLocaleString('en-US') : '0') + '</td>';
                                        html += '<td style=""padding: 12px; text-align: right; font-weight: 600; color: var(--accent);"">​' + (row.totalLiquidPlus ? Math.round(row.totalLiquidPlus).toLocaleString('en-US') : '0') + '</td>';
                                        html += '<td style=""padding: 12px; text-align: right;"">' + (row.totalLocked ? Math.round(row.totalLocked).toLocaleString('en-US') : '0') + '</td>';
                                        html += '<td style=""padding: 12px; text-align: right;"">' + (row.txCount ? row.txCount.toLocaleString() : '0') + '</td>';
                                        html += '</tr>';
                                    });
                                    
                                    html += '</tbody></table></div>';
                                    html += '<div style=""padding: 12px; font-size: 12px; color: var(--text-secondary); border-top: 1px solid var(--border);"">Total Records: ' + stats.length + '</div>';
                                    document.getElementById('sync-data').innerHTML = html;
                                } catch (error) {
                                    document.getElementById('sync-data').innerHTML = '<div style=""color: var(--error);"">Error loading synced data: ' + error.message + '</div>';
                                }
                            }
                            
                            loadSyncData();
                            setInterval(loadSyncData, 60000); // Refresh every minute
                        </script>
                    </div>
");
    }

    static void AppendDailyStatsCard(System.Text.StringBuilder sb)
    {
        sb.Append(@"
                    <div class=""card"" style=""grid-column: 1 / -1;"">
                        <h2>Active Wallets</h2>
                        <div class=""card-content"">
                            <div style=""display: flex; justify-content: space-between; align-items: center; margin-bottom: 8px;"">
                                <div style=""font-size: 13px; color: var(--text-secondary);"">Daily Active Wallets</div>
                                <div id=""active-wallet-range"" style=""display: inline-flex; gap: 6px;"">
                                    <button type=""button"" data-range=""7"" style=""padding: 4px 10px; border-radius: 999px; border: 1px solid var(--border); background: var(--surface); color: var(--text-secondary); font-size: 11px; cursor: pointer;"">1W</button>
                                    <button type=""button"" data-range=""30"" style=""padding: 4px 10px; border-radius: 999px; border: 1px solid var(--border); background: var(--surface); color: var(--text-secondary); font-size: 11px; cursor: pointer;"">1M</button>
                                    <button type=""button"" data-range=""365"" style=""padding: 4px 10px; border-radius: 999px; border: 1px solid var(--border); background: var(--surface); color: var(--text-secondary); font-size: 11px; cursor: pointer;"">1Y</button>
                                    <button type=""button"" data-range=""730"" style=""padding: 4px 10px; border-radius: 999px; border: 1px solid var(--border); background: var(--surface); color: var(--text-secondary); font-size: 11px; cursor: pointer;"">2Y</button>
                                    <button type=""button"" data-range=""all"" style=""padding: 4px 10px; border-radius: 999px; border: 1px solid var(--border); background: var(--surface); color: var(--text-secondary); font-size: 11px; cursor: pointer;"">ALL</button>
                                </div>
                            </div>
                            <div id=""daily-stats-chart"" style=""height: 240px; border: 1px solid var(--border); border-radius: 8px; background: var(--surface-light); display: flex; align-items: center; justify-content: center; color: var(--text-secondary); position: relative;"">
                                Loading active wallet chart...
                            </div>
                            <script>
                                let activeRangeDays = 30;

                                function setActiveRangeButton(range) {
                                    const buttons = document.querySelectorAll('#active-wallet-range button');
                                    buttons.forEach(btn => {
                                        const isActive = btn.getAttribute('data-range') === range;
                                        btn.style.background = isActive ? 'var(--accent-primary)' : 'var(--surface)';
                                        btn.style.color = isActive ? 'var(--bg-primary)' : 'var(--text-secondary)';
                                        btn.style.borderColor = isActive ? 'var(--accent-primary)' : 'var(--border)';
                                    });
                                }

                                async function loadDailyStats() {
                                    try {
                                        const response = await fetch('/api/daily-stats/all');
                                        const data = await response.json();
                                        const stats = data.stats || [];
                                        const chartEl = document.getElementById('daily-stats-chart');
                                        
                                        if (stats.length === 0) {
                                            chartEl.innerHTML = '<div style=""color: var(--text-secondary);"">No daily statistics available</div>';
                                            return;
                                        }

                                        const chronological = stats.slice().reverse();
                                        let filtered = chronological;
                                        if (activeRangeDays !== 'all') {
                                            filtered = chronological.slice(Math.max(chronological.length - activeRangeDays, 0));
                                        }

                                        const values = filtered.map(r => r.activeWallets || 0);
                                        const maxVal = Math.max(...values);
                                        const minVal = Math.min(...values);
                                        const width = 1100;
                                        const height = 200;
                                        const padL = 56;
                                        const padR = 16;
                                        const padT = 14;
                                        const padB = 28;
                                        const range = maxVal === minVal ? 1 : (maxVal - minVal);
                                        const xStep = values.length > 1 ? (width - padL - padR) / (values.length - 1) : 0;

                                        const toY = (v) => {
                                            const t = (v - minVal) / range;
                                            return height - padB - t * (height - padT - padB);
                                        };

                                        const points = values.map((v, i) => {
                                            const x = padL + i * xStep;
                                            const y = toY(v);
                                            return `${x},${y}`;
                                        }).join(' ');

                                        const areaPath = `M ${padL} ${height - padB} L ${points} L ${width - padR} ${height - padB} Z`;
                                        const linePath = `M ${points.replace(/ /g, ' L ')}`;
                                        const lastVal = values[values.length - 1] || 0;
                                        const lastX = padL + (values.length - 1) * xStep;
                                        const lastY = toY(lastVal);

                                        const gridLines = [0, 0.25, 0.5, 0.75, 1].map(p => {
                                            const y = padT + p * (height - padT - padB);
                                            return `<line x1=""${padL}"" y1=""${y}"" x2=""${width - padR}"" y2=""${y}"" stroke=""var(--border)"" stroke-width=""1"" />`;
                                        }).join('');

                                        const labelTop = Math.round(maxVal);
                                        const labelMid = Math.round(minVal + range / 2);
                                        const labelBottom = Math.round(minVal);

                                        const parseDate = (value) => {
                                            const parts = (value || '').split('-');
                                            if (parts.length === 3) {
                                                const y = parseInt(parts[0], 10);
                                                const m = parseInt(parts[1], 10) - 1;
                                                const d = parseInt(parts[2], 10);
                                                return new Date(Date.UTC(y, m, d));
                                            }
                                            return new Date(value);
                                        };

                                        const monthYearLabel = (date) => date.toLocaleString('en-US', { month: 'short', year: 'numeric' });
                                        const xTicks = [];
                                        let lastTickKey = '';
                                        filtered.forEach((row, idx) => {
                                            const d = parseDate(row.date);
                                            if (isNaN(d.getTime())) {
                                                return;
                                            }
                                            const key = `${d.getUTCFullYear()}-${d.getUTCMonth()}`;
                                            if (key !== lastTickKey) {
                                                lastTickKey = key;
                                                xTicks.push({
                                                    idx,
                                                    label: monthYearLabel(d)
                                                });
                                            }
                                        });
                                        const maxTicks = 8;
                                        const tickStep = xTicks.length > maxTicks ? Math.ceil(xTicks.length / maxTicks) : 1;
                                        const xTickLabels = xTicks
                                            .filter((_, i) => i % tickStep === 0 || i === xTicks.length - 1)
                                            .map(tick => {
                                                const x = padL + tick.idx * xStep;
                                                return `<text x=""${x}"" y=""${height - 6}"" fill=""var(--text-secondary)"" font-size=""10"" text-anchor=""middle"">${tick.label}</text>`;
                                            })
                                            .join('');

                                        const chartHtml = `
<svg viewBox=""0 0 ${width} ${height}"" width=""100%"" height=""100%"" preserveAspectRatio=""xMinYMid meet"">
  <defs>
    <linearGradient id=""activeWalletsFill"" x1=""0"" x2=""0"" y1=""0"" y2=""1"">
      <stop offset=""0%"" stop-color=""rgba(0, 120, 212, 0.24)"" />
      <stop offset=""100%"" stop-color=""rgba(0, 120, 212, 0.03)"" />
    </linearGradient>
  </defs>
  <rect x=""0"" y=""0"" width=""${width}"" height=""${height}"" fill=""var(--surface-light)""></rect>
  ${gridLines}
  <path d=""${areaPath}"" fill=""url(#activeWalletsFill)"" stroke=""none""></path>
  <path d=""${linePath}"" fill=""none"" stroke=""var(--accent-primary)"" stroke-width=""2""></path>
  <circle cx=""${lastX}"" cy=""${lastY}"" r=""3.5"" fill=""var(--accent-primary)""></circle>
  <rect x=""${padL + 6}"" y=""${lastY - 10}"" width=""44"" height=""20"" rx=""6"" fill=""var(--accent-primary)""></rect>
  <text x=""${padL + 28}"" y=""${lastY + 5}"" fill=""#ffffff"" font-size=""11"" text-anchor=""middle"">${lastVal.toLocaleString()}</text>
  <text x=""${padL - 6}"" y=""${padT + 2}"" fill=""var(--text-secondary)"" font-size=""10"" text-anchor=""end"">${labelTop.toLocaleString()}</text>
  <text x=""${padL - 6}"" y=""${(height - padB + padT) / 2 + 4}"" fill=""var(--text-secondary)"" font-size=""10"" text-anchor=""end"">${labelMid.toLocaleString()}</text>
  <text x=""${padL - 6}"" y=""${height - padB + 10}"" fill=""var(--text-secondary)"" font-size=""10"" text-anchor=""end"">${labelBottom.toLocaleString()}</text>
  ${xTickLabels}
</svg>`;

                                        chartEl.innerHTML = chartHtml;

                                        const tooltip = document.createElement('div');
                                        tooltip.style.position = 'absolute';
                                        tooltip.style.pointerEvents = 'none';
                                        tooltip.style.background = '#ffffff';
                                        tooltip.style.border = '1px solid var(--border-strong)';
                                        tooltip.style.borderRadius = '6px';
                                        tooltip.style.padding = '6px 8px';
                                        tooltip.style.fontSize = '11px';
                                        tooltip.style.color = '#1b1a19';
                                        tooltip.style.fontWeight = '600';
                                        tooltip.style.boxShadow = '0 4px 14px rgba(0, 0, 0, 0.18)';
                                        tooltip.style.opacity = '0';
                                        tooltip.style.transform = 'translate(-50%, -110%)';
                                        if (getComputedStyle(chartEl).position === 'static') chartEl.style.position = 'relative';
                                        chartEl.appendChild(tooltip);

                                        const svg = chartEl.querySelector('svg');
                                        if (svg) {
                                            const svgNS = 'http://www.w3.org/2000/svg';
                                            const hoverLine = document.createElementNS(svgNS, 'line');
                                            hoverLine.setAttribute('y1', `${padT}`);
                                            hoverLine.setAttribute('y2', `${height - padB}`);
                                            hoverLine.setAttribute('stroke', 'var(--accent-light)');
                                            hoverLine.setAttribute('stroke-width', '1');
                                            hoverLine.setAttribute('stroke-dasharray', '4 4');
                                            hoverLine.setAttribute('opacity', '0');
                                            svg.appendChild(hoverLine);

                                            const hoverDot = document.createElementNS(svgNS, 'circle');
                                            hoverDot.setAttribute('r', '4');
                                            hoverDot.setAttribute('fill', 'var(--accent-primary)');
                                            hoverDot.setAttribute('stroke', '#ffffff');
                                            hoverDot.setAttribute('stroke-width', '2');
                                            hoverDot.setAttribute('opacity', '0');
                                            svg.appendChild(hoverDot);

                                            const updateHover = (clientX) => {
                                                if (!values.length) return;
                                                // Map through the SVG's screen transform; preserveAspectRatio 'meet' leaves letterbox space the element box doesn't account for.
                                                const ctm = svg.getScreenCTM();
                                                if (!ctm) return;
                                                const relX = (clientX - ctm.e) / ctm.a;
                                                const clamped = Math.max(padL, Math.min(width - padR, relX));
                                                const index = xStep === 0 ? 0 : Math.round((clamped - padL) / xStep);
                                                const safeIndex = Math.max(0, Math.min(values.length - 1, index));
                                                const x = padL + safeIndex * xStep;
                                                const y = toY(values[safeIndex]);
                                                const row = filtered[safeIndex];
                                                const date = parseDate(row.date);
                                                const dateLabel = isNaN(date.getTime()) ? row.date : date.toLocaleDateString('en-US', { year: 'numeric', month: 'short', day: '2-digit' });

                                                hoverLine.setAttribute('x1', `${x}`);
                                                hoverLine.setAttribute('x2', `${x}`);
                                                hoverLine.setAttribute('opacity', '1');
                                                hoverDot.setAttribute('cx', `${x}`);
                                                hoverDot.setAttribute('cy', `${y}`);
                                                hoverDot.setAttribute('opacity', '1');

                                                tooltip.textContent = `${dateLabel}: ${values[safeIndex].toLocaleString()}`;
                                                const hostRect = chartEl.getBoundingClientRect();
                                                tooltip.style.left = `${ctm.a * x + ctm.e - hostRect.left}px`;
                                                tooltip.style.top = `${ctm.d * y + ctm.f - hostRect.top}px`;
                                                tooltip.style.opacity = '1';
                                            };

                                            svg.addEventListener('mousemove', (event) => updateHover(event.clientX));
                                            svg.addEventListener('mouseenter', (event) => updateHover(event.clientX));
                                            svg.addEventListener('mouseleave', () => {
                                                hoverLine.setAttribute('opacity', '0');
                                                hoverDot.setAttribute('opacity', '0');
                                                tooltip.style.opacity = '0';
                                            });
                                        }
                                    } catch (error) {
                                        document.getElementById('daily-stats-chart').innerHTML = '<div style=""color: var(--error);"">Error loading daily stats: ' + error.message + '</div>';
                                    }
                                }

                                document.querySelectorAll('#active-wallet-range button').forEach(btn => {
                                    btn.addEventListener('click', () => {
                                        const range = btn.getAttribute('data-range');
                                        activeRangeDays = range === 'all' ? 'all' : parseInt(range, 10);
                                        setActiveRangeButton(range);
                                        loadDailyStats();
                                    });
                                });
                                
                                setActiveRangeButton('30');
                                loadDailyStats();
                                setInterval(loadDailyStats, 60000); // Refresh every minute
                            </script>
                        </div>
                    </div>
");
    }

    static void AppendPriceChartCard(System.Text.StringBuilder sb)
    {
        sb.Append(@"
                    <div class=""card"" style=""grid-column: 1 / -1;"">
                        <h2>OPT Price (USD)</h2>
                        <div class=""card-content"">
                            <div style=""display: flex; justify-content: space-between; align-items: center; gap: 12px; margin-bottom: 8px; flex-wrap: wrap;"">
                                <div id=""price-chart-summary"" style=""color: var(--text-secondary); font-size: 13px;"">Loading price history...</div>
                                <div style=""display: inline-flex; gap: 14px; flex-wrap: wrap;"">
                                    <div id=""price-chart-scale"" role=""group"" aria-label=""Price scale"" style=""display: inline-flex; gap: 6px;""></div>
                                    <div id=""price-chart-ranges"" role=""group"" aria-label=""Time range"" style=""display: inline-flex; gap: 6px;""></div>
                                </div>
                            </div>
                            <div id=""price-chart"" style=""height: 240px; border: 1px solid var(--border); border-radius: 8px; background: var(--surface-light); display: flex; align-items: center; justify-content: center; color: var(--text-secondary); position: relative;"">
                                Loading price history...
                            </div>
                            <div id=""price-chart-note"" style=""color: var(--text-muted); font-size: 12px; margin-top: 6px;""></div>
                            <script>
                                (async function () {
                                    const chartEl = document.getElementById('price-chart');
                                    const summaryEl = document.getElementById('price-chart-summary');
                                    const rangesEl = document.getElementById('price-chart-ranges');
                                    const scaleEl = document.getElementById('price-chart-scale');
                                    const noteEl = document.getElementById('price-chart-note');
                                    // Rolling ranges count back from the last close; YTD and the prior year are calendar (UTC) ranges.
                                    const thisYear = new Date().getUTCFullYear();
                                    const lastYear = String(thisYear - 1);
                                    const rolling = (days) => (rows) => {
                                        const end = parseDate(rows[rows.length - 1].date);
                                        return rows.filter(r => (end - parseDate(r.date)) / 86400000 < days);
                                    };
                                    const ranges = [
                                        ['3M', rolling(92), 'the last 3 months'],
                                        ['6M', rolling(183), 'the last 6 months'],
                                        ['1Y', rolling(365), 'the last year'],
                                        ['YTD', (rows) => rows.filter(r => r.date.startsWith(String(thisYear))), 'year to date'],
                                        [lastYear, (rows) => rows.filter(r => r.date.startsWith(lastYear)), lastYear],
                                        ['ALL', (rows) => rows, 'all history']
                                    ];
                                    const fmtPrice = (v) => '$' + Number(v).toLocaleString('en-US', { minimumFractionDigits: 4, maximumFractionDigits: 6 });
                                    const fmtAxis = (v) => v <= 0 ? '$0' : '$' + Number(v).toLocaleString('en-US', { maximumSignificantDigits: 3 });
                                    const parseDate = (value) => {
                                        const p = String(value || '').split('-').map(Number);
                                        return new Date(Date.UTC(p[0], p[1] - 1, p[2]));
                                    };
                                    const fmtDate = (value, opts) => parseDate(value).toLocaleDateString('en-US', Object.assign({ timeZone: 'UTC' }, opts));

                                    let all = [];
                                    let activeRange = 'ALL';
                                    let scale = 'linear';
                                    try { if (localStorage.getItem('optic-price-scale') === 'log') scale = 'log'; } catch (e) { }

                                    const render = () => {
                                        const range = ranges.find(r => r[0] === activeRange);
                                        const rows = range[1](all);
                                        if (rows.length === 0) {
                                            chartEl.textContent = `No price data for ${range[2]}`;
                                            summaryEl.textContent = '';
                                            return;
                                        }
                                        const prices = rows.map(r => Number(r.priceUsd));
                                        const spanDays = (parseDate(rows[rows.length - 1].date) - parseDate(rows[0].date)) / 86400000;

                                        const width = 1100;
                                        const height = 200;
                                        const padL = 72;
                                        const padR = 16;
                                        const padT = 14;
                                        const padB = 28;
                                        const plotW = width - padL - padR;
                                        const plotH = height - padT - padB;
                                        const rawMax = Math.max(...prices);
                                        const rawMin = Math.min(...prices);
                                        const isLog = scale === 'log' && rawMin > 0;
                                        let minVal, maxVal, toY, tickValues;
                                        if (isLog) {
                                            // Pad by a fixed ratio so the line never touches the frame.
                                            minVal = rawMin / 1.15;
                                            maxVal = rawMax * 1.15;
                                            const lo = Math.log10(minVal);
                                            const span = (Math.log10(maxVal) - lo) || 1;
                                            toY = (v) => padT + plotH - ((Math.log10(v) - lo) / span) * plotH;
                                            // 1-2-5 ticks per decade; thin to powers of ten when too dense.
                                            const ticks = [];
                                            for (let e = Math.floor(lo) - 1; e <= Math.ceil(Math.log10(maxVal)); e++) {
                                                for (const m of [1, 2, 5]) {
                                                    const t = m * Math.pow(10, e);
                                                    if (t >= minVal && t <= maxVal) ticks.push(t);
                                                }
                                            }
                                            const decades = ticks.filter(t => Math.abs(Math.log10(t) - Math.round(Math.log10(t))) < 1e-9);
                                            tickValues = ticks.length > 7 && decades.length >= 2 ? decades : ticks;
                                            if (tickValues.length < 2) tickValues = [minVal, Math.sqrt(minVal * maxVal), maxVal];
                                        } else {
                                            const pad = (rawMax - rawMin) * 0.08 || rawMax * 0.05 || 1;
                                            maxVal = rawMax + pad;
                                            minVal = Math.max(0, rawMin - pad);
                                            toY = (v) => padT + plotH - ((v - minVal) / (maxVal - minVal)) * plotH;
                                            tickValues = [maxVal, (maxVal + minVal) / 2, minVal];
                                        }
                                        const xStep = rows.length > 1 ? plotW / (rows.length - 1) : 0;
                                        const xFor = (i) => padL + i * xStep;

                                        const points = prices.map((v, i) => `${xFor(i)},${toY(v)}`).join(' L ');
                                        const gridLines = tickValues.map(value => {
                                            const y = toY(value);
                                            return `<line x1=""${padL}"" y1=""${y}"" x2=""${width - padR}"" y2=""${y}"" stroke=""var(--border)"" stroke-width=""1"" />` +
                                                `<text x=""${padL - 8}"" y=""${y + 4}"" fill=""var(--text-secondary)"" font-size=""10"" text-anchor=""end"">${fmtAxis(value)}</text>`;
                                        }).join('');

                                        const maxTicks = 8;
                                        const tickStep = rows.length > maxTicks ? Math.ceil(rows.length / maxTicks) : 1;
                                        const xTicks = rows
                                            .map((row, idx) => ({ row, idx }))
                                            .filter(item => item.idx % tickStep === 0)
                                            .map(item => `<text x=""${xFor(item.idx)}"" y=""${height - 8}"" fill=""var(--text-secondary)"" font-size=""10"" text-anchor=""middle"">${fmtDate(item.row.date, spanDays > 300 ? { month: 'short', year: 'numeric' } : { month: 'short', day: 'numeric' })}</text>`)
                                            .join('');

                                        chartEl.innerHTML = `
<svg viewBox=""0 0 ${width} ${height}"" width=""100%"" height=""100%"" preserveAspectRatio=""xMinYMid meet"" role=""img"" aria-label=""OPT daily closing price in USD, ${isLog ? 'log' : 'linear'} scale"">
  ${gridLines}
  <path d=""M ${points}"" fill=""none"" stroke=""var(--accent-primary)"" stroke-width=""2"" stroke-linejoin=""round"" stroke-linecap=""round""></path>
  ${xTicks}
</svg>`;

                                        const first = prices[0];
                                        const last = prices[prices.length - 1];
                                        const change = first > 0 ? (last - first) / first * 100 : 0;
                                        const sign = change > 0 ? '+' : '';
                                        summaryEl.innerHTML = `<span style=""color: var(--text-primary); font-weight: 600; font-size: 15px;"">${fmtPrice(last)}</span> ` +
                                            `<span>close ${fmtDate(rows[rows.length - 1].date, { year: 'numeric', month: 'short', day: '2-digit' })}</span> · ` +
                                            `<span>${sign}${change.toFixed(1)}% over ${range[2]}</span>`;

                                        const tooltip = document.createElement('div');
                                        Object.assign(tooltip.style, {
                                            position: 'absolute', pointerEvents: 'none', background: 'var(--surface)',
                                            border: '1px solid var(--border-strong)', borderRadius: '6px', padding: '6px 8px',
                                            fontSize: '11px', color: 'var(--text-primary)', fontWeight: '600',
                                            boxShadow: '0 4px 14px rgba(0, 0, 0, 0.18)', opacity: '0',
                                            transform: 'translate(-50%, -115%)', whiteSpace: 'nowrap'
                                        });
                                        chartEl.appendChild(tooltip);

                                        const svg = chartEl.querySelector('svg');
                                        const svgNS = 'http://www.w3.org/2000/svg';
                                        const hoverLine = document.createElementNS(svgNS, 'line');
                                        hoverLine.setAttribute('y1', `${padT}`);
                                        hoverLine.setAttribute('y2', `${height - padB}`);
                                        hoverLine.setAttribute('stroke', 'var(--text-muted)');
                                        hoverLine.setAttribute('stroke-width', '1');
                                        hoverLine.setAttribute('stroke-dasharray', '4 4');
                                        hoverLine.setAttribute('opacity', '0');
                                        svg.appendChild(hoverLine);
                                        const dot = document.createElementNS(svgNS, 'circle');
                                        dot.setAttribute('r', '4');
                                        dot.setAttribute('fill', 'var(--accent-primary)');
                                        dot.setAttribute('stroke', 'var(--surface-light)');
                                        dot.setAttribute('stroke-width', '2');
                                        dot.setAttribute('opacity', '0');
                                        svg.appendChild(dot);

                                        const updateHover = (clientX) => {
                                            const ctm = svg.getScreenCTM();
                                            if (!ctm) return;
                                            const relX = (clientX - ctm.e) / ctm.a;
                                            const clamped = Math.max(padL, Math.min(width - padR, relX));
                                            const index = xStep === 0 ? 0 : Math.round((clamped - padL) / xStep);
                                            const i = Math.max(0, Math.min(rows.length - 1, index));
                                            const x = xFor(i);
                                            const y = toY(prices[i]);
                                            hoverLine.setAttribute('x1', `${x}`);
                                            hoverLine.setAttribute('x2', `${x}`);
                                            hoverLine.setAttribute('opacity', '1');
                                            dot.setAttribute('cx', `${x}`);
                                            dot.setAttribute('cy', `${y}`);
                                            dot.setAttribute('opacity', '1');
                                            tooltip.innerHTML = `${fmtDate(rows[i].date, { year: 'numeric', month: 'short', day: '2-digit' })}<br>${fmtPrice(prices[i])}`;
                                            const hostRect = chartEl.getBoundingClientRect();
                                            tooltip.style.left = `${ctm.a * x + ctm.e - hostRect.left}px`;
                                            tooltip.style.top = `${ctm.d * y + ctm.f - hostRect.top}px`;
                                            tooltip.style.opacity = '1';
                                        };
                                        svg.addEventListener('mousemove', (e) => updateHover(e.clientX));
                                        svg.addEventListener('mouseenter', (e) => updateHover(e.clientX));
                                        svg.addEventListener('mouseleave', () => {
                                            hoverLine.setAttribute('opacity', '0');
                                            dot.setAttribute('opacity', '0');
                                            tooltip.style.opacity = '0';
                                        });
                                    };

                                    const renderRangeButtons = () => {
                                        rangesEl.innerHTML = ranges.map(([label]) => pill('data-range', label, label, label === activeRange)).join('');
                                    };
                                    const pill = (attr, value, label, active) =>
                                        `<button type=""button"" ${attr}=""${value}"" aria-pressed=""${active}"" style=""padding: 4px 10px; font-size: 11px; border-radius: 999px; border: 1px solid ${active ? 'var(--accent-primary)' : 'var(--border)'}; background: ${active ? 'var(--accent-primary)' : 'var(--surface)'}; color: ${active ? 'var(--bg-primary)' : 'var(--text-secondary)'}; cursor: pointer;"">${label}</button>`;
                                    const renderScaleButtons = () => {
                                        scaleEl.innerHTML = pill('data-scale', 'linear', 'LINEAR', scale === 'linear') + pill('data-scale', 'log', 'LOG', scale === 'log');
                                    };
                                    scaleEl.addEventListener('click', (e) => {
                                        const btn = e.target.closest('button[data-scale]');
                                        if (!btn || all.length === 0) return;
                                        scale = btn.dataset.scale;
                                        try { localStorage.setItem('optic-price-scale', scale); } catch (err) { }
                                        renderScaleButtons();
                                        render();
                                    });
                                    rangesEl.addEventListener('click', (e) => {
                                        const btn = e.target.closest('button[data-range]');
                                        if (!btn || all.length === 0) return;
                                        activeRange = btn.dataset.range;
                                        renderRangeButtons();
                                        render();
                                    });

                                    try {
                                        const response = await fetch('/api/price/daily');
                                        const data = await response.json();
                                        if (!response.ok) throw new Error(data.error || 'Request failed');
                                        all = (data.rows || []).filter(r => r.priceUsd != null);
                                        if (all.length === 0) {
                                            chartEl.textContent = 'No price data available';
                                            summaryEl.textContent = '';
                                            return;
                                        }

                                        renderScaleButtons();
                                        renderRangeButtons();
                                        render();
                                        const lastDate = parseDate(all[all.length - 1].date);
                                        const staleDays = Math.floor((Date.now() - lastDate) / 86400000);
                                        noteEl.textContent = `Daily close · ${data.source}` +
                                            (staleDays > 2 ? ` · No market data since ${fmtDate(all[all.length - 1].date, { year: 'numeric', month: 'short', day: '2-digit' })}` : '');
                                    } catch (error) {
                                        chartEl.textContent = 'Error loading price data: ' + error.message;
                                        summaryEl.textContent = '';
                                    }
                                })();
                            </script>
                        </div>
                    </div>
");
    }

    static void AppendWalletGrowthCard(System.Text.StringBuilder sb)
    {
        sb.Append(@"
                    <div class=""card"" style=""grid-column: 1 / -1;"">
                        <h2>Wallet Growth</h2>
                        <div class=""card-content"">
                            <div id=""wallet-growth-chart"" style=""height: 240px; border: 1px solid var(--border); border-radius: 8px; background: var(--surface-light); display: flex; align-items: center; justify-content: center; color: var(--text-secondary); position: relative;"">
                                Loading wallet growth...
                            </div>
                            <script>
                                (async function () {
                                    try {
                                        const response = await fetch('/api/daily-stats/all');
                                        const data = await response.json();
                                        const stats = data.stats || [];
                                        const chartEl = document.getElementById('wallet-growth-chart');

                                        if (stats.length === 0) {
                                            chartEl.innerHTML = '<div style=""color: var(--text-secondary);"">No wallet growth data available</div>';
                                            return;
                                        }

                                        const chronological = stats.slice().reverse();
                                        const values = chronological.map(r => r.totalWallets || 0);
                                        const width = 1100;
                                        const height = 200;
                                        const padL = 56;
                                        const padR = 16;
                                        const padT = 14;
                                        const padB = 28;
                                        const maxVal = Math.max(...values);
                                        const minVal = Math.min(...values);
                                        const range = maxVal === minVal ? 1 : (maxVal - minVal);
                                        const xStep = values.length > 1 ? (width - padL - padR) / (values.length - 1) : 0;

                                        const toY = (v) => {
                                            const t = (v - minVal) / range;
                                            return height - padB - t * (height - padT - padB);
                                        };

                                        const points = values.map((v, i) => {
                                            const x = padL + i * xStep;
                                            const y = toY(v);
                                            return `${x},${y}`;
                                        }).join(' ');

                                        const linePath = `M ${points.replace(/ /g, ' L ')}`;

                                        const gridLines = [0, 0.25, 0.5, 0.75, 1].map(p => {
                                            const y = padT + p * (height - padT - padB);
                                            return `<line x1=""${padL}"" y1=""${y}"" x2=""${width - padR}"" y2=""${y}"" stroke=""var(--border)"" stroke-width=""1"" />`;
                                        }).join('');

                                        const labelTop = Math.round(maxVal);
                                        const labelMid = Math.round(minVal + range / 2);
                                        const labelBottom = Math.round(minVal);

                                        const parseDate = (value) => {
                                            const parts = (value || '').split('-');
                                            if (parts.length === 3) {
                                                const y = parseInt(parts[0], 10);
                                                const m = parseInt(parts[1], 10) - 1;
                                                const d = parseInt(parts[2], 10);
                                                return new Date(Date.UTC(y, m, d));
                                            }
                                            return new Date(value);
                                        };

                                        const monthYearLabel = (date) => date.toLocaleString('en-US', { month: 'short', year: 'numeric' });
                                        const xTicks = [];
                                        let lastTickKey = '';
                                        chronological.forEach((row, idx) => {
                                            const d = parseDate(row.date);
                                            if (isNaN(d.getTime())) {
                                                return;
                                            }
                                            const key = `${d.getUTCFullYear()}-${d.getUTCMonth()}`;
                                            if (key !== lastTickKey) {
                                                lastTickKey = key;
                                                xTicks.push({
                                                    idx,
                                                    label: monthYearLabel(d)
                                                });
                                            }
                                        });
                                        const maxTicks = 8;
                                        const tickStep = xTicks.length > maxTicks ? Math.ceil(xTicks.length / maxTicks) : 1;
                                        const xTickLabels = xTicks
                                            .filter((_, i) => i % tickStep === 0 || i === xTicks.length - 1)
                                            .map(tick => {
                                                const x = padL + tick.idx * xStep;
                                                return `<text x=""${x}"" y=""${height - 6}"" fill=""var(--text-secondary)"" font-size=""10"" text-anchor=""middle"">${tick.label}</text>`;
                                            })
                                            .join('');

                                        const chartHtml = `
<svg viewBox=""0 0 ${width} ${height}"" width=""100%"" height=""100%"" preserveAspectRatio=""xMinYMid meet"">
  <rect x=""0"" y=""0"" width=""${width}"" height=""${height}"" fill=""var(--surface-light)""></rect>
  ${gridLines}
  <path d=""${linePath}"" fill=""none"" stroke=""var(--accent-primary)"" stroke-width=""2""></path>
  <text x=""${padL - 6}"" y=""${padT + 2}"" fill=""var(--text-secondary)"" font-size=""10"" text-anchor=""end"">${labelTop.toLocaleString()}</text>
  <text x=""${padL - 6}"" y=""${(height - padB + padT) / 2 + 4}"" fill=""var(--text-secondary)"" font-size=""10"" text-anchor=""end"">${labelMid.toLocaleString()}</text>
  <text x=""${padL - 6}"" y=""${height - padB + 10}"" fill=""var(--text-secondary)"" font-size=""10"" text-anchor=""end"">${labelBottom.toLocaleString()}</text>
  ${xTickLabels}
</svg>`;

                                        chartEl.innerHTML = chartHtml;

                                        const tooltip = document.createElement('div');
                                        tooltip.style.position = 'absolute';
                                        tooltip.style.pointerEvents = 'none';
                                        tooltip.style.background = '#ffffff';
                                        tooltip.style.border = '1px solid var(--border-strong)';
                                        tooltip.style.borderRadius = '6px';
                                        tooltip.style.padding = '6px 8px';
                                        tooltip.style.fontSize = '11px';
                                        tooltip.style.color = '#1b1a19';
                                        tooltip.style.fontWeight = '600';
                                        tooltip.style.boxShadow = '0 4px 14px rgba(0, 0, 0, 0.18)';
                                        tooltip.style.opacity = '0';
                                        tooltip.style.transform = 'translate(-50%, -110%)';
                                        if (getComputedStyle(chartEl).position === 'static') chartEl.style.position = 'relative';
                                        chartEl.appendChild(tooltip);

                                        const svg = chartEl.querySelector('svg');
                                        if (svg) {
                                            const svgNS = 'http://www.w3.org/2000/svg';
                                            const hoverLine = document.createElementNS(svgNS, 'line');
                                            hoverLine.setAttribute('y1', `${padT}`);
                                            hoverLine.setAttribute('y2', `${height - padB}`);
                                            hoverLine.setAttribute('stroke', 'var(--accent-light)');
                                            hoverLine.setAttribute('stroke-width', '1');
                                            hoverLine.setAttribute('stroke-dasharray', '4 4');
                                            hoverLine.setAttribute('opacity', '0');
                                            svg.appendChild(hoverLine);

                                            const hoverDot = document.createElementNS(svgNS, 'circle');
                                            hoverDot.setAttribute('r', '4');
                                            hoverDot.setAttribute('fill', 'var(--accent-primary)');
                                            hoverDot.setAttribute('stroke', '#ffffff');
                                            hoverDot.setAttribute('stroke-width', '2');
                                            hoverDot.setAttribute('opacity', '0');
                                            svg.appendChild(hoverDot);

                                            const updateHover = (clientX) => {
                                                if (!values.length) return;
                                                // Map through the SVG's screen transform; preserveAspectRatio 'meet' leaves letterbox space the element box doesn't account for.
                                                const ctm = svg.getScreenCTM();
                                                if (!ctm) return;
                                                const relX = (clientX - ctm.e) / ctm.a;
                                                const clamped = Math.max(padL, Math.min(width - padR, relX));
                                                const index = xStep === 0 ? 0 : Math.round((clamped - padL) / xStep);
                                                const safeIndex = Math.max(0, Math.min(values.length - 1, index));
                                                const x = padL + safeIndex * xStep;
                                                const y = toY(values[safeIndex]);
                                                const row = chronological[safeIndex];
                                                const date = parseDate(row.date);
                                                const dateLabel = isNaN(date.getTime()) ? row.date : date.toLocaleDateString('en-US', { year: 'numeric', month: 'short', day: '2-digit' });

                                                hoverLine.setAttribute('x1', `${x}`);
                                                hoverLine.setAttribute('x2', `${x}`);
                                                hoverLine.setAttribute('opacity', '1');
                                                hoverDot.setAttribute('cx', `${x}`);
                                                hoverDot.setAttribute('cy', `${y}`);
                                                hoverDot.setAttribute('opacity', '1');

                                                tooltip.textContent = `${dateLabel}: ${values[safeIndex].toLocaleString()}`;
                                                const hostRect = chartEl.getBoundingClientRect();
                                                tooltip.style.left = `${ctm.a * x + ctm.e - hostRect.left}px`;
                                                tooltip.style.top = `${ctm.d * y + ctm.f - hostRect.top}px`;
                                                tooltip.style.opacity = '1';
                                            };

                                            svg.addEventListener('mousemove', (event) => updateHover(event.clientX));
                                            svg.addEventListener('mouseenter', (event) => updateHover(event.clientX));
                                            svg.addEventListener('mouseleave', () => {
                                                hoverLine.setAttribute('opacity', '0');
                                                hoverDot.setAttribute('opacity', '0');
                                                tooltip.style.opacity = '0';
                                            });
                                        }
                                    } catch (error) {
                                        const chartEl = document.getElementById('wallet-growth-chart');
                                        if (chartEl) {
                                            chartEl.innerHTML = '<div style=""color: var(--error);"">Error loading wallet growth: ' + error.message + '</div>';
                                        }
                                    }
                                })();
                            </script>
                        </div>
                    </div>
");
    }

    static void AppendWalletTotalsCard(System.Text.StringBuilder sb)
    {
        sb.Append(@"
                    <div class=""card"" style=""grid-column: 1 / -1;"">
                        <h2>Network Totals</h2>
                        <div class=""card-content"">
                            <div id=""wallet-totals"" class=""totals-grid"">
                                <div style=""color: var(--text-secondary);"">Loading totals...</div>
                            </div>
                            <script>
                                (async function () {
                                    try {
                                        const response = await fetch('/api/summary');
                                        const data = await response.json();
                                        const summary = data.summary || {};
                                        const container = document.getElementById('wallet-totals');

                                        if (!summary || Object.keys(summary).length === 0) {
                                            container.innerHTML = '<div style=""color: var(--text-secondary);"">No totals available</div>';
                                            return;
                                        }

                                        const banked = summary.totalLiquid || 0;
                                        const staked = summary.totalStaked || 0;
                                        const locked = summary.totalLocked || 0;
                                        const totalWallets = summary.totalWallets || 0;
                                        const distributed = summary.totalDistributed || (banked + staked);
                                        const emitted = summary.totalEmitted || 0;
                                        const formatAmt = (value) => Math.round(value || 0).toLocaleString('en-US');
                                        const formatCount = (value) => (value || 0).toLocaleString('en-US');

                                        container.innerHTML = `
                                            <div class=""total-item"">
                                                <label>Total Wallet Count</label>
                                                <div class=""total-value"">${totalWallets.toLocaleString()}</div>
                                            </div>
                                            <div class=""total-item"">
                                                <label>Total Banked Amount</label>
                                                <div class=""total-value"">${formatAmt(banked)}</div>
                                            </div>
                                            <div class=""total-item"">
                                                <label>Total Staked Amount</label>
                                                <div class=""total-value"">${formatAmt(staked)}</div>
                                            </div>
                                            <div class=""total-item"">
                                                <label>Total Locked Amount</label>
                                                <div class=""total-value"">${formatAmt(locked)}</div>
                                            </div>
                                            <div class=""total-item"">
                                                <label>Total Emitted Amount</label>
                                                <div class=""total-value"">${formatAmt(emitted)}</div>
                                            </div>
                                            <div class=""total-item"">
                                                <label>Total Distributed Amount</label>
                                                <div class=""total-value"">${formatAmt(distributed)}</div>
                                            </div>
                                            <div class=""total-item"">
                                                <label>6 Months</label>
                                                <div class=""total-value"">${formatCount(summary.lock6mCount)} / ${formatAmt(summary.lock6mAmount)} OPT</div>
                                            </div>
                                            <div class=""total-item"">
                                                <label>12 Months</label>
                                                <div class=""total-value"">${formatCount(summary.lock12mCount)} / ${formatAmt(summary.lock12mAmount)} OPT</div>
                                            </div>
                                            <div class=""total-item"">
                                                <label>18 Months</label>
                                                <div class=""total-value"">${formatCount(summary.lock18mCount)} / ${formatAmt(summary.lock18mAmount)} OPT</div>
                                            </div>
                                            <div class=""total-item"">
                                                <label>24 Months</label>
                                                <div class=""total-value"">${formatCount(summary.lock24mCount)} / ${formatAmt(summary.lock24mAmount)} OPT</div>
                                            </div>
                                        `;
                                    } catch (error) {
                                        const container = document.getElementById('wallet-totals');
                                        if (container) {
                                            container.innerHTML = '<div style=""color: var(--error);"">Error loading totals: ' + error.message + '</div>';
                                        }
                                    }
                                })();
                            </script>
                        </div>
                    </div>
");
    }

    static string BuildArgsFromForm(IFormCollection form, string mode)
    {
        var args = new List<string>();
        args.Add("--" + mode);
        
        if (form.ContainsKey("address") && !string.IsNullOrWhiteSpace(form["address"]))
            args.Add("--address");
            args.Add(form["address"].ToString());

        if (form.ContainsKey("emitter") && !string.IsNullOrWhiteSpace(form["emitter"]))
        {
            args.Add("--emitter");
            args.Add(form["emitter"].ToString());
        }

        if (form.ContainsKey("wallet-file") && !string.IsNullOrWhiteSpace(form["wallet-file"]))
        {
            args.Add("--wallet-file");
            args.Add(form["wallet-file"].ToString());
        }

        if (form.ContainsKey("block-scan-start") && !string.IsNullOrWhiteSpace(form["block-scan-start"]))
        {
            args.Add("--BlockScanStart");
            args.Add(form["block-scan-start"].ToString());
        }

        if (form.ContainsKey("block-scan-end") && !string.IsNullOrWhiteSpace(form["block-scan-end"]))
        {
            args.Add("--BlockScanEnd");
            args.Add(form["block-scan-end"].ToString());
        }

        if (form.ContainsKey("custom-args") && !string.IsNullOrWhiteSpace(form["custom-args"]))
            return form["custom-args"].ToString();

        return string.Join(" ", args);
    }

    static async Task<(int exitCode, string output, List<string> files)> RunCliSubprocessAsync(string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"run -- {args}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using (var proc = Process.Start(psi))
        {
            if (proc == null)
                return (-1, "Failed to start process.", new List<string>());

            var output = await proc.StandardOutput.ReadToEndAsync();
            var error = await proc.StandardError.ReadToEndAsync();
            proc.WaitForExit();

            var files = ExtractOutputFiles(output + error);
            return (proc.ExitCode, output + error, files);
        }
    }

    static List<string> ExtractOutputFiles(string output)
    {
        var files = new List<string>();
        foreach (var line in output.Split('\n'))
        {
            if (line.Contains(".csv") || line.Contains(".txt"))
                files.Add(line.Trim());
        }
        return files;
    }

    static string? TryResolveSafePath(string filename, string workingDir)
    {
        var path = Path.Combine(workingDir, filename);
        if (!Path.GetFullPath(path).StartsWith(Path.GetFullPath(workingDir)))
            return null;
        return File.Exists(path) ? path : null;
    }

    static string RenderResultHtml((int exitCode, string output, List<string> files) result, string args)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(@"<!DOCTYPE html>
<html>
<head>
    <title>Execution Result</title>
    <style>
        :root {
            --bg-primary: #f3f2f1;
            --bg-secondary: #ffffff;
            --surface: #ffffff;
            --surface-light: #faf9f8;
            --border: #d2d0ce;
            --text-primary: #1b1a19;
            --text-secondary: #323130;
            --accent-primary: #0078d4;
            --accent-secondary: #106ebe;
        }

        body {
            font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, 'Helvetica Neue', Arial, sans-serif;
            background:
                linear-gradient(rgba(0, 0, 0, 0.025) 1px, transparent 1px),
                linear-gradient(90deg, rgba(0, 0, 0, 0.018) 1px, transparent 1px),
                linear-gradient(180deg, #ffffff 0%, var(--bg-primary) 100%);
            background-size: 42px 42px, 42px 42px, auto;
            color: var(--text-primary);
            padding: 20px;
            line-height: 1.6;
        }

        .result-container {
            background: #ffffff;
            border: 1px solid var(--border);
            border-radius: 8px;
            padding: 20px;
            max-width: 1400px;
            margin: 0 auto;
        }

        .result-header {
            display: flex;
            justify-content: space-between;
            align-items: center;
            margin-bottom: 20px;
            padding-bottom: 12px;
            border-bottom: 1px solid var(--border);
        }

        .result-header h2 {
            margin: 0;
            font-size: 24px;
            color: var(--text-primary);
        }

        .status-badge-success {
            background: rgba(16, 185, 129, 0.2);
            color: var(--accent-primary);
            padding: 6px 16px;
            border-radius: 20px;
            font-size: 13px;
            font-weight: 600;
        }

        .status-badge-error {
            background: rgba(239, 68, 68, 0.2);
            color: #ff6b6b;
            padding: 6px 16px;
            border-radius: 20px;
            font-size: 13px;
            font-weight: 600;
        }

        p {
            margin: 12px 0;
            color: var(--text-secondary);
        }

        code {
            background: #ffffff;
            padding: 2px 6px;
            border-radius: 4px;
            color: var(--accent-primary);
            font-family: 'Courier New', monospace;
        }

        .output-log {
            background: #ffffff;
            border: 1px solid var(--border);
            border-radius: 4px;
            padding: 12px;
            font-family: 'Courier New', monospace;
            font-size: 12px;
            color: var(--text-secondary);
            max-height: 400px;
            overflow-y: auto;
            margin: 20px 0;
            line-height: 1.4;
            white-space: pre-wrap;
            word-wrap: break-word;
            display: none;
        }

        .output-log.hidden {
            display: none;
        }

        .counterparties-table {
            width: 100%;
            border-collapse: collapse;
            margin: 20px 0;
        }

        .counterparties-table thead {
            background: var(--surface-light);
            position: sticky;
            top: 0;
        }

        .counterparties-table th {
            padding: 12px;
            text-align: left;
            font-weight: 600;
            color: var(--accent-primary);
            border-bottom: 2px solid var(--accent-primary);
            cursor: pointer;
            user-select: none;
        }

        .counterparties-table th:hover {
            background: var(--surface);
        }

        .counterparties-table td {
            padding: 10px 12px;
            border-bottom: 1px solid var(--border);
            color: var(--text-primary);
        }

        .counterparties-table tbody tr:hover {
            background: var(--surface);
        }

        .counterparties-table .address-cell {
            font-family: 'Courier New', monospace;
            font-size: 11px;
            color: var(--accent-primary);
        }

        .counterparties-table .amount-cell {
            text-align: right;
            font-variant-numeric: tabular-nums;
        }

        .counterparties-table .count-cell {
            text-align: center;
            color: var(--text-secondary);
        }

        .file-list {
            display: grid;
            gap: 8px;
            margin: 20px 0;
        }

        .file-item {
            background: #ffffff;
            border: 1px solid var(--border);
            border-radius: 4px;
            padding: 12px;
            display: flex;
            justify-content: space-between;
            align-items: center;
        }

        .file-item a {
            color: var(--accent-primary);
            text-decoration: none;
            font-weight: 500;
        }

        .file-item a:hover {
            text-decoration: underline;
        }

        .back-link {
            display: inline-block;
            margin-top: 20px;
            padding: 10px 20px;
            background: var(--accent-primary);
            color: var(--bg-primary);
            text-decoration: none;
            border-radius: 4px;
            font-weight: 600;
            transition: all 0.2s ease;
        }

        .back-link:hover {
            background: var(--accent-secondary);
            transform: translateY(-2px);
        }

        .toggle-log {
            background: none;
            border: 1px solid var(--border);
            color: var(--text-secondary);
            padding: 6px 12px;
            border-radius: 4px;
            cursor: pointer;
            font-size: 12px;
            transition: all 0.2s ease;
        }

        .toggle-log:hover {
            background: var(--surface);
            color: var(--text-primary);
        }

        .controls {
            margin: 20px 0;
            display: flex;
            gap: 8px;
            align-items: center;
        }

        .search-box {
            flex: 1;
            max-width: 300px;
            padding: 8px 12px;
            border: 1px solid var(--border);
            border-radius: 4px;
            background: var(--bg-primary);
            color: var(--text-primary);
        }

        .search-box::placeholder {
            color: var(--text-secondary);
        }
    </style>
</head>
<body>
    <div class=""result-container"">
        <div class=""result-header"">
            <h2>Execution Result</h2>
            <span class=""" + (result.exitCode == 0 ? "status-badge-success" : "status-badge-error") + @""">
                " + (result.exitCode == 0 ? "Success" : "Failed") + @"
            </span>
        </div>
        <p><strong>Command:</strong> <code>" + System.Net.WebUtility.HtmlEncode(args) + @"</code></p>
        ");

        // Check if this is recognized output and render specially
        if (args.Contains("--distributions") && result.exitCode == 0)
        {
            var distributionsHtml = ParseAndRenderDistributions(result.output);
            if (!string.IsNullOrEmpty(distributionsHtml))
                sb.Append(distributionsHtml);
        }
        else if (args.Contains("counterparties") && result.exitCode == 0)
        {
            var counterpartiesHtml = ParseAndRenderCounterparties(result.output);
            sb.Append(counterpartiesHtml);
        }
        else if (args.Contains("--locks") && result.exitCode == 0)
        {
            var locksHtml = ParseAndRenderLocks(result.output);
            if (!string.IsNullOrEmpty(locksHtml))
                sb.Append(locksHtml);
        }
        else if (args.Contains("--send-recv") && result.exitCode == 0)
        {
            var sendRecvHtml = ParseAndRenderSendRecv(result.output);
            if (!string.IsNullOrEmpty(sendRecvHtml))
                sb.Append(sendRecvHtml);
        }
        else if (args.Contains("--wallet-balances") && result.exitCode == 0)
        {
            var balancesHtml = ParseAndRenderWalletBalances(result.output);
            if (!string.IsNullOrEmpty(balancesHtml))
                sb.Append(balancesHtml);
        }

        sb.Append(@"
        <div class=""controls"">
            <button class=""toggle-log"" onclick=""document.querySelector('.output-log').classList.toggle('hidden'); this.textContent = this.textContent === 'Show Raw Log' ? 'Hide Raw Log' : 'Show Raw Log'"">Show Raw Log</button>
        </div>
        <div class=""output-log hidden"">" + System.Net.WebUtility.HtmlEncode(result.output) + @"</div>
        " + (result.files.Count > 0 ? @"
        <div class=""file-list"">
            " + string.Join("", result.files.Select(f => $@"<div class=""file-item""><a href=""/files/{f}"">{f}</a></div>")) + @"
        </div>
        " : "") + @"
        <a href=""/"" class=""back-link"">Back to Dashboard</a>
    </div>
</body>
</html>");
        return sb.ToString();
    }

    static string ParseAndRenderLocks(string output)
    {
        var lines = output.Split('\n');
        var sb = new System.Text.StringBuilder();
        var rows = new List<(string address, string amount, string unlockDate, string status)>();

        // Parse lock data from output
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("==") || trimmed.StartsWith("--"))
                continue;

            // Simple parsing - looking for pattern: address/amount/date/status
            var parts = trimmed.Split(new[] { '\t', ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3 && parts[0].StartsWith("optio"))
            {
                rows.Add((parts[0], parts.Length > 1 ? parts[1] : "N/A", 
                         parts.Length > 2 ? parts[2] : "N/A", 
                         parts.Length > 3 ? parts[3] : "Active"));
            }
        }

        if (rows.Count == 0)
            return string.Empty;

        sb.Append(@"
        <h3 style=""margin-top: 24px; color: var(--text-primary);"">Lock Analysis</h3>
        <table class=""counterparties-table"">
            <thead>
                <tr>
                    <th>Address</th>
                    <th style=""text-align: right;"">Amount (OPT)</th>
                    <th style=""text-align: center;"">Unlock Date</th>
                    <th style=""text-align: center;"">Status</th>
                </tr>
            </thead>
            <tbody>
");

        foreach (var (addr, amt, date, status) in rows)
        {
            sb.Append($@"
                <tr>
                    <td class=""address-cell"">{System.Net.WebUtility.HtmlEncode(addr)}</td>
                    <td class=""amount-cell"">{System.Net.WebUtility.HtmlEncode(amt)}</td>
                    <td style=""text-align: center;"">{System.Net.WebUtility.HtmlEncode(date)}</td>
                    <td style=""text-align: center;"">{System.Net.WebUtility.HtmlEncode(status)}</td>
                </tr>
");
        }

        sb.Append($@"
            </tbody>
        </table>
        <p style=""color: var(--text-secondary); margin-top: 12px; font-size: 13px;"">
            Showing {rows.Count} lock{(rows.Count != 1 ? "s" : "")}
        </p>
");

        return sb.ToString();
    }

    static string ParseAndRenderDistributions(string output)
    {
        var lines = output.Split('\n');
        var rows = new List<(string Date, string Emitted, string Distributed, string Net)>();
        var inDailySection = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.StartsWith("== Daily Emissions & Distributions", StringComparison.OrdinalIgnoreCase))
            {
                inDailySection = true;
                continue;
            }

            if (!inDailySection)
                continue;

            if (line.StartsWith("==") && !line.StartsWith("== Daily Emissions & Distributions", StringComparison.OrdinalIgnoreCase))
                break;

            if (line.Length == 0 ||
                line.StartsWith("Date ", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("---", StringComparison.Ordinal))
                continue;

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length >= 4 && parts[0].Length == 10 && parts[0][4] == '-' && parts[0][7] == '-')
                rows.Add((parts[0], parts[1], parts[2], parts[3]));
        }

        if (rows.Count == 0)
            return string.Empty;

        var sb = new System.Text.StringBuilder();
        sb.Append(@"
        <h3 style=""margin-top: 24px; color: var(--text-primary);"">Daily Emissions & Distributions</h3>
        <table class=""counterparties-table"">
            <thead>
                <tr>
                    <th>Date</th>
                    <th style=""text-align: right;"">Emitted (OPT)</th>
                    <th style=""text-align: right;"">Distributed (OPT)</th>
                    <th style=""text-align: right;"">Net (OPT)</th>
                </tr>
            </thead>
            <tbody>
");

        foreach (var row in rows)
        {
            sb.Append($@"
                <tr>
                    <td>{System.Net.WebUtility.HtmlEncode(row.Date)}</td>
                    <td class=""amount-cell"">{System.Net.WebUtility.HtmlEncode(row.Emitted)}</td>
                    <td class=""amount-cell"">{System.Net.WebUtility.HtmlEncode(row.Distributed)}</td>
                    <td class=""amount-cell"">{System.Net.WebUtility.HtmlEncode(row.Net)}</td>
                </tr>
");
        }

        sb.Append($@"
            </tbody>
        </table>
        <p style=""color: var(--text-secondary); margin-top: 12px; font-size: 13px;"">
            Showing the latest {rows.Count} daily page{(rows.Count != 1 ? "s" : "")} from the distribution scan.
        </p>
");

        return sb.ToString();
    }

    static string ParseAndRenderSendRecv(string output)
    {
        var lines = output.Split('\n');
        var sb = new System.Text.StringBuilder();
        var transactions = new List<(string type, string address, string amount, string time)>();

        // Parse send/recv data from output
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("==") || trimmed.StartsWith("--") || trimmed.StartsWith("Time"))
                continue;

            var parts = trimmed.Split(new[] { '\t', '|' }, System.StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3)
            {
                var txType = parts[0].ToLower().Contains("sent") ? "Sent" : "Received";
                transactions.Add((txType, parts.Length > 1 ? parts[1] : "N/A", 
                                 parts.Length > 2 ? parts[2] : "N/A",
                                 parts.Length > 3 ? parts[3] : DateTime.UtcNow.ToString("yyyy-MM-dd")));
            }
        }

        if (transactions.Count == 0)
            return string.Empty;

        sb.Append(@"
        <h3 style=""margin-top: 24px; color: var(--text-primary);"">Send/Receive Activity</h3>
        <table class=""counterparties-table"">
            <thead>
                <tr>
                    <th>Type</th>
                    <th>Address</th>
                    <th style=""text-align: right;"">Amount (OPT)</th>
                    <th style=""text-align: center;"">Time</th>
                </tr>
            </thead>
            <tbody>
");

        foreach (var (type, addr, amt, time) in transactions)
        {
            var typeColor = type == "Sent" ? "color: #ef4444;" : "color: #10b981;";
            sb.Append($@"
                <tr>
                    <td style=""{typeColor}"">{System.Net.WebUtility.HtmlEncode(type)}</td>
                    <td class=""address-cell"">{System.Net.WebUtility.HtmlEncode(addr)}</td>
                    <td class=""amount-cell"">{System.Net.WebUtility.HtmlEncode(amt)}</td>
                    <td style=""text-align: center; font-size: 12px;"">{System.Net.WebUtility.HtmlEncode(time)}</td>
                </tr>
");
        }

        sb.Append($@"
            </tbody>
        </table>
        <p style=""color: var(--text-secondary); margin-top: 12px; font-size: 13px;"">
            Showing {transactions.Count} transaction{(transactions.Count != 1 ? "s" : "")}
        </p>
");

        return sb.ToString();
    }

    static string ParseAndRenderWalletBalances(string output)
    {
        var lines = output.Split('\n');
        var sb = new System.Text.StringBuilder();
        var balances = new List<(string wallet, string balance, string liquid, string staked, string locked)>();

        // Parse wallet balance data from output
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("==") || trimmed.StartsWith("--") || trimmed.StartsWith("Wallet"))
                continue;

            var parts = trimmed.Split(new[] { '\t', '|' }, System.StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[0].StartsWith("optio"))
            {
                balances.Add((parts[0], 
                             parts.Length > 1 ? parts[1] : "0.00",
                             parts.Length > 2 ? parts[2] : "0.00",
                             parts.Length > 3 ? parts[3] : "0.00",
                             parts.Length > 4 ? parts[4] : "0.00"));
            }
        }

        if (balances.Count == 0)
            return string.Empty;

        sb.Append(@"
        <h3 style=""margin-top: 24px; color: var(--text-primary);"">Wallet Balances</h3>
        <table class=""counterparties-table"">
            <thead>
                <tr>
                    <th>Wallet Address</th>
                    <th style=""text-align: right;"">Total Balance (OPT)</th>
                    <th style=""text-align: right;"">Liquid (OPT)</th>
                    <th style=""text-align: right;"">Staked (OPT)</th>
                    <th style=""text-align: right;"">Locked (OPT)</th>
                </tr>
            </thead>
            <tbody>
");

        decimal totalBalance = 0, totalLiquid = 0, totalStaked = 0, totalLocked = 0;

        foreach (var (wallet, balance, liquid, staked, locked) in balances)
        {
            decimal.TryParse(balance.Replace(",", ""), out var bVal);
            decimal.TryParse(liquid.Replace(",", ""), out var lVal);
            decimal.TryParse(staked.Replace(",", ""), out var sVal);
            decimal.TryParse(locked.Replace(",", ""), out var kVal);
            
            totalBalance += bVal;
            totalLiquid += lVal;
            totalStaked += sVal;
            totalLocked += kVal;

            sb.Append($@"
                <tr>
                    <td class=""address-cell"">{System.Net.WebUtility.HtmlEncode(wallet)}</td>
                    <td class=""amount-cell"">{bVal:N0}</td>
                    <td class=""amount-cell"">{lVal:N0}</td>
                    <td class=""amount-cell"">{sVal:N0}</td>
                    <td class=""amount-cell"">{kVal:N0}</td>
                </tr>
");
        }

        sb.Append($@"
                <tr style=""background: var(--surface-light); font-weight: 600;"">
                    <td style=""color: var(--accent-primary);"">TOTALS</td>
                    <td class=""amount-cell"">{totalBalance:N0}</td>
                    <td class=""amount-cell"">{totalLiquid:N0}</td>
                    <td class=""amount-cell"">{totalStaked:N0}</td>
                    <td class=""amount-cell"">{totalLocked:N0}</td>
                </tr>
            </tbody>
        </table>
        <p style=""color: var(--text-secondary); margin-top: 12px; font-size: 13px;"">
            Showing {balances.Count} wallet{(balances.Count != 1 ? "s" : "")} | Total Balance: {totalBalance:N0} OPT
        </p>
");

        return sb.ToString();
    }

    static string ParseAndRenderCounterparties(string output)
    {
        var lines = output.Split('\n');
        var counterpartiesStart = -1;

        // Find the counterparties section
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains("== Counterparties =="))
            {
                counterpartiesStart = i + 2; // Skip header line
                break;
            }
        }

        if (counterpartiesStart < 0)
            return string.Empty;

        var sb = new System.Text.StringBuilder();
        sb.Append(@"
        <h3 style=""margin-top: 24px; color: var(--text-primary);"">Counterparties Analysis</h3>
        <div class=""controls"">
            <input type=""text"" class=""search-box"" id=""search"" placeholder=""Search counterparty address..."" onkeyup=""filterTable()"">
        </div>
        <table class=""counterparties-table"" id=""counterpartiesTable"">
            <thead>
                <tr>
                    <th onclick=""sortTable(1)"">Counterparty Address</th>
                    <th onclick=""sortTable(2)"" style=""text-align: right;"">Sent Count</th>
                    <th onclick=""sortTable(3)"" style=""text-align: right;"">Sent Amount (OPT)</th>
                    <th onclick=""sortTable(4)"" style=""text-align: right;"">Recv Count</th>
                    <th onclick=""sortTable(5)"" style=""text-align: right;"">Recv Amount (OPT)</th>
                    <th onclick=""sortTable(6)"" style=""text-align: right;"">Total Activity</th>
                </tr>
            </thead>
            <tbody>
");

        var rowCount = 0;
        for (int i = counterpartiesStart; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            
            // Stop at empty line or next section
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("=="))
                break;

            // Parse the line - counterparties format has addresses and numbers
            var parts = line.Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
            
            if (parts.Length >= 6 && parts[0].StartsWith("optio"))
            {
                // Format: Address Counterparty SentCount SentAmount RecvCount RecvAmount
                var address = parts[0];
                var counterparty = parts[1];
                
                if (!decimal.TryParse(parts[2], out var sentCount) || 
                    !decimal.TryParse(parts[3], out var sentAmount) ||
                    !decimal.TryParse(parts[4], out var recvCount) ||
                    !decimal.TryParse(parts[5], out var recvAmount))
                    continue;

                var totalActivity = (long)sentCount + (long)recvCount;
                var totalAmount = sentAmount + recvAmount;

                sb.Append($@"
                <tr>
                    <td class=""address-cell"">{System.Net.WebUtility.HtmlEncode(counterparty)}</td>
                    <td class=""count-cell"">{(long)sentCount}</td>
                    <td class=""amount-cell"">{sentAmount:N6}</td>
                    <td class=""count-cell"">{(long)recvCount}</td>
                    <td class=""amount-cell"">{recvAmount:N6}</td>
                    <td class=""amount-cell""><strong>{totalAmount:N6}</strong></td>
                </tr>
");
                rowCount++;
            }
        }

        sb.Append($@"
            </tbody>
        </table>
        <p style=""color: var(--text-secondary); margin-top: 12px; font-size: 13px;"">
            Showing {rowCount} counterpart{(rowCount != 1 ? "ies" : "y")} | Click headers to sort
        </p>
        <script>
            function filterTable() {{
                const searchText = document.getElementById('search').value.toLowerCase();
                const table = document.getElementById('counterpartiesTable');
                const rows = table.getElementsByTagName('tbody')[0].getElementsByTagName('tr');
                
                for (let row of rows) {{
                    const address = row.cells[0].textContent.toLowerCase();
                    row.style.display = address.includes(searchText) ? '' : 'none';
                }}
            }}
            
            function sortTable(columnIndex) {{
                const table = document.getElementById('counterpartiesTable');
                const rows = Array.from(table.getElementsByTagName('tbody')[0].getElementsByTagName('tr'));
                
                rows.sort((a, b) => {{
                    const aVal = a.cells[columnIndex - 1].textContent.trim();
                    const bVal = b.cells[columnIndex - 1].textContent.trim();
                    
                    const aNum = parseFloat(aVal.replace(/,/g, '')) || aVal;
                    const bNum = parseFloat(bVal.replace(/,/g, '')) || bVal;
                    
                    return typeof aNum === 'number' ? bNum - aNum : aVal.localeCompare(bVal);
                }});
                
                const tbody = table.getElementsByTagName('tbody')[0];
                rows.forEach(row => tbody.appendChild(row));
            }}
        </script>
");

        return sb.ToString();
    }

    static void AppendSyncedDataTableCard(System.Text.StringBuilder sb)
    {
        sb.Append(@"
                    <div class=""card"">
                        <h2>Synced Daily Statistics</h2>
                        <div class=""card-content"">
                            <p>View all daily statistics that have been synced to the database.</p>
                            <div id=""synced-data-loading"" style=""text-align: center; padding: 20px; color: var(--text-secondary);"">
                                Loading synced data...
                            </div>
                            <div id=""synced-data-container"" style=""display: none; overflow-x: auto;"">
                                <table id=""synced-data-table"" style=""width: 100%; border-collapse: collapse; font-size: 12px;"">
                                    <thead>
                                        <tr style=""background-color: var(--surface-light); border-bottom: 1px solid var(--border);"">
                                            <th style=""padding: 8px; text-align: left; border-right: 1px solid var(--border);"">Date</th>
                                            <th style=""padding: 8px; text-align: right; border-right: 1px solid var(--border);"">Total Wallets</th>
                                            <th style=""padding: 8px; text-align: right; border-right: 1px solid var(--border);"">Active Wallets</th>
                                            <th style=""padding: 8px; text-align: right; border-right: 1px solid var(--border);"">Total Supply (OPT)</th>
                                            <th style=""padding: 8px; text-align: right; border-right: 1px solid var(--border);"">Total Staked (OPT)</th>
                                            <th style=""padding: 8px; text-align: right; border-right: 1px solid var(--border);"">Total Locked (OPT)</th>
                                            <th style=""padding: 8px; text-align: right; border-right: 1px solid var(--border);"">6 Months</th>
                                            <th style=""padding: 8px; text-align: right; border-right: 1px solid var(--border);"">12 Months</th>
                                            <th style=""padding: 8px; text-align: right; border-right: 1px solid var(--border);"">18 Months</th>
                                            <th style=""padding: 8px; text-align: right; border-right: 1px solid var(--border);"">24 Months</th>
                                            <th style=""padding: 8px; text-align: right;"">Tx Count</th>
                                        </tr>
                                    </thead>
                                    <tbody id=""synced-data-tbody"">
                                    </tbody>
                                </table>
                            </div>
                            <div id=""synced-data-stats"" style=""margin-top: 16px; padding: 12px; background-color: var(--surface); border-radius: 4px; font-size: 13px;"">
                                <p id=""synced-data-count"">Total records: 0</p>
                            </div>
                        </div>
                    </div>
                    <script>
                        async function loadSyncedData() {{
                            try {{
                                const response = await fetch('/api/daily-stats/all');
                                const data = await response.json();
                                
                                if (!data.stats || data.stats.length === 0) {{
                                    document.getElementById('synced-data-loading').innerHTML = 'No synced data available yet. Run backfill to populate data.';
                                    return;
                                }}
                                
                                const tbody = document.getElementById('synced-data-tbody');
                                const count = data.count || 0;
                                
                                data.stats.forEach((stat, index) => {{
                                    const row = document.createElement('tr');
                                    row.style.backgroundColor = index % 2 === 0 ? 'transparent' : 'var(--surface)';
                                    row.style.borderBottom = '1px solid var(--border)';
                                    
                                    const formatNum = (val) => val === null ? '-' : Math.round(val).toLocaleString('en-US');
                                    
                                    row.innerHTML = `
                                        <td style=""padding: 8px; border-right: 1px solid var(--border);"">${{stat.date}}</td>
                                        <td style=""padding: 8px; text-align: right; border-right: 1px solid var(--border);"">${{formatNum(stat.totalWallets)}}</td>
                                        <td style=""padding: 8px; text-align: right; border-right: 1px solid var(--border);"">${{formatNum(stat.activeWallets)}}</td>
                                        <td style=""padding: 8px; text-align: right; border-right: 1px solid var(--border);"">${{formatNum(stat.totalSupply)}}</td>
                                        <td style=""padding: 8px; text-align: right; border-right: 1px solid var(--border);"">${{formatNum(stat.totalStaked)}}</td>
                                        <td style=""padding: 8px; text-align: right; border-right: 1px solid var(--border);"">${{formatNum(stat.totalLocked)}}</td>
                                        <td style=""padding: 8px; text-align: right; border-right: 1px solid var(--border);"">${{formatNum(stat.lock6m)}}</td>
                                        <td style=""padding: 8px; text-align: right; border-right: 1px solid var(--border);"">${{formatNum(stat.lock12m)}}</td>
                                        <td style=""padding: 8px; text-align: right; border-right: 1px solid var(--border);"">${{formatNum(stat.lock18m)}}</td>
                                        <td style=""padding: 8px; text-align: right; border-right: 1px solid var(--border);"">${{formatNum(stat.lock24m)}}</td>
                                        <td style=""padding: 8px; text-align: right;"">${{formatNum(stat.txCount)}}</td>
                                    `;
                                    
                                    tbody.appendChild(row);
                                }});
                                
                                document.getElementById('synced-data-loading').style.display = 'none';
                                document.getElementById('synced-data-container').style.display = 'block';
                                document.getElementById('synced-data-count').textContent = `Total records: ${{count}}`;
                            }} catch (error) {{
                                document.getElementById('synced-data-loading').innerHTML = 'Error loading synced data: ' + error.message;
                            }}
                        }}
                        
                        loadSyncedData();
                    </script>
");
    }

    static void AppendAboutCard(System.Text.StringBuilder sb)
    {
        sb.Append(@"
                    <div class=""card"" style=""max-width: 800px;"">
                        <h2>About OPTIC</h2>
                        <div class=""card-content"">
                            <p><strong>OPTIC</strong> (Optio Protocol Telemetry & Intelligence Center) is a comprehensive blockchain analytics platform designed to provide deep insights into the Optio Protocol ecosystem.</p>
                            
                            <h3 style=""color: var(--accent-primary); margin-top: 20px;"">Key Features</h3>
                            <ul style=""line-height: 1.8;"">
                                <li><strong>Distribution Analysis</strong> - Track token distributions across the network</li>
                                <li><strong>Lock & Staking Data</strong> - Monitor lockup periods and delegation patterns</li>
                                <li><strong>Network Statistics</strong> - Real-time blockchain network metrics</li>
                                <li><strong>Wallet Intelligence</strong> - Comprehensive wallet balance and transaction reports</li>
                                <li><strong>Daily Analytics</strong> - Historical trends and daily statistics</li>
                                <li><strong>Transaction Tracking</strong> - Multi-send and counterparty analysis</li>
                            </ul>
                            
                            <h3 style=""color: var(--accent-primary); margin-top: 20px;"">Mission</h3>
                            <p>OPTIC is built to provide transparency, clarity, and precise observation of blockchain data. We believe in making complex blockchain information accessible and understandable for researchers, developers, and stakeholders in the Optio Protocol ecosystem.</p>
                            
                            <h3 style=""color: var(--accent-primary); margin-top: 20px;"">Support OPTIC</h3>
                            <p>OPTIC is maintained through community support. Donations help us cover the costs of:</p>
                            <ul style=""line-height: 1.8;"">
                                <li>Server infrastructure and hosting</li>
                                <li>Power and bandwidth costs</li>
                                <li>Continuous development and improvements</li>
                                <li>Data aggregation and processing</li>
                            </ul>
                            
                            <p style=""margin-top: 20px; color: var(--text-secondary);"">If you find OPTIC valuable, please consider making a donation to support its ongoing development and operation.</p>
                            
                            <button class=""btn-donate"" onclick=""openDonateModal()"" style=""margin-top: 16px; padding: 12px 24px; font-size: 16px;"">Donate Now</button>
                        </div>
                    </div>
");
    }
}

sealed class TopWalletEntry
{
    public int Rank { get; set; }
    public string Address { get; set; } = "";
    public decimal WalletBalance { get; set; }
    public decimal Staked { get; set; }
    public decimal Unbonding { get; set; }
    public decimal Lock6Months { get; set; }
    public decimal Lock12Months { get; set; }
    public decimal Lock18Months { get; set; }
    public decimal Lock24Months { get; set; }
    public decimal TotalLocked => Lock6Months + Lock12Months + Lock18Months + Lock24Months;
    public decimal Total => WalletBalance + Staked + Unbonding;
}

sealed class DailyDistributionPoint
{
    public string Date { get; set; } = "";
    public decimal DistributedOpt { get; set; }
    public int WalletCount { get; set; }
}

sealed class AddressDistributionRow
{
    public DateTime TimeUtc { get; set; }
    public long Height { get; set; }
    public string TxHash { get; set; } = "";
    public string Memo { get; set; } = "";
    public decimal AmountOpt { get; set; }
    public decimal? PriceUsd { get; set; }
    public decimal? ValueUsd => PriceUsd.HasValue ? AmountOpt * PriceUsd.Value : null;
    public string? PriceInterval { get; set; }
    public DateTime? PriceCandleOpenUtc { get; set; }
    /// <summary>"node" or "staking" (staking / participation reward).</summary>
    public string Kind { get; set; } = "node";
    /// <summary>True when the day had no clear large/small pair and the kind was guessed from nearby days.</summary>
    public bool KindInferred { get; set; }
}

sealed class AddressDistributionReport
{
    public List<AddressDistributionRow> Rows { get; set; } = new();
    public bool Truncated { get; set; }
}

sealed class WalletLedgerEntry
{
    public DateTime TimeUtc { get; set; }
    public long Height { get; set; }
    public string TxHash { get; set; } = "";
    public string Memo { get; set; } = "";
    public string Direction { get; set; } = "";
    public string Counterparty { get; set; } = "";
    public string Category { get; set; } = "";
    public decimal AmountOpt { get; set; }
    public decimal? PriceUsd { get; set; }
    public string? PriceInterval { get; set; }
}

sealed class WalletLedger
{
    public List<WalletLedgerEntry> Entries { get; set; } = new();
    public bool Truncated { get; set; }
}

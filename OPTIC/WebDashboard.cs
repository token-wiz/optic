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
        DashboardHttps.Configure(builder, host, port, LoadDashboardConfig());
        var app = builder.Build();
        DashboardHttps.UseRedirect(app);
        DashboardAuth.Use(app);
        DashboardSettings.Map(app, host, port);
        DashboardHttps.Map(app);
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
            var html = RenderDashboardHtml(url, page, activeLink, ctx);
            ctx.Response.ContentType = "text/html; charset=utf-8";
            return Results.Text(html, "text/html");
        });

        app.MapGet("/page/{page}", (string page, HttpContext ctx) =>
        {
            var html = RenderDashboardHtml(url, page, activeLink, ctx);
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

        app.MapGet("/api/market/liquidity", async (HttpContext ctx) =>
        {
            try
            {
                var snapshotTask = OPTIC.Services.MarketVolumeService.GetSnapshotAsync(ctx.RequestAborted);
                var candles = await TokenPriceService.GetDailyCandlesAsync(ctx.RequestAborted);
                var days = await OPTIC.Services.MarketVolumeService.GetDailyAsync(ctx.RequestAborted);
                var snapshot = await snapshotTask;
                var cmc = candles.ToDictionary(c => c.DateUtc.Date);
                var osmosis = OPTIC.Services.MarketVolumeService.OsmosisRange;
                var coinstoreTraded = days.Where(d => d.CoinstoreVolumeOpt > 0).ToList();

                return Results.Json(new
                {
                    sources = new
                    {
                        cmcFrom = candles.Count > 0 ? candles[0].DateUtc : (DateTime?)null,
                        cmcTo = candles.Count > 0 ? candles[^1].DateUtc : (DateTime?)null,
                        coinstoreFrom = coinstoreTraded.Count > 0 ? coinstoreTraded[0].DateUtc : (DateTime?)null,
                        coinstoreTo = coinstoreTraded.Count > 0 ? coinstoreTraded[^1].DateUtc : (DateTime?)null,
                        osmosisFrom = osmosis.From,
                        osmosisTo = osmosis.To,
                        osmosisTxs = osmosis.Txs
                    },
                    snapshot,
                    days = days.Select(d =>
                    {
                        var c = cmc.TryGetValue(d.DateUtc, out var candle) ? candle : default;
                        var hasCmc = c.DateUtc != default;
                        return new
                        {
                            date = d.DateUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                            cmcClose = hasCmc ? c.CloseUsd : (decimal?)null,
                            cmcVolumeUsd = hasCmc ? c.VolumeUsd : null,
                            csVolumeOpt = d.CoinstoreVolumeOpt,
                            csVolumeUsdt = d.CoinstoreVolumeUsdt,
                            csClose = d.CoinstoreCloseUsdt,
                            osSoldOpt = d.OsmosisSoldOpt,
                            osBoughtOpt = d.OsmosisBoughtOpt,
                            osGrossOpt = d.OsmosisGrossOpt,
                            osPrice = d.OsmosisPriceUsd,
                            osTxs = d.OsmosisTxs
                        };
                    })
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        });

        app.MapGet("/api/market/coinstore-flows", async (HttpContext ctx) =>
        {
            try
            {
                return Results.Json(await GetCoinstoreFlowsAsync(ctx.RequestAborted));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        });

        app.MapGet("/api/market/status", async (HttpContext ctx) =>
        {
            var status = await OPTIC.Services.MarketVolumeService.GetListingStatusAsync(ctx.RequestAborted);
            return Results.Json(status);
        });

        app.MapGet("/api/market/quote", async (HttpContext ctx) =>
        {
            if (!decimal.TryParse(ctx.Request.Query["opt"], NumberStyles.Float, CultureInfo.InvariantCulture, out var opt) || opt <= 0 || opt > 10_000_000_000m)
                return Results.Json(new { error = "Give opt=<amount of OPT>." }, statusCode: 400);
            return Results.Json(await OPTIC.Services.MarketVolumeService.QuoteSellAsync(opt, ctx.RequestAborted));
        });

        app.MapGet("/api/market/evidence/latest", () =>
            Results.Json(OPTIC.Services.LiquidityEvidenceService.Latest()));

        app.MapPost("/api/market/evidence", async (HttpContext ctx) =>
        {
            var extra = ctx.Request.Query["opt"].ToString()
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(v => decimal.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0m)
                .Where(d => d > 0 && d <= 10_000_000_000m)
                .Take(5);
            return Results.Json(await OPTIC.Services.LiquidityEvidenceService.CaptureAsync(extra, ctx.RequestAborted));
        });

        app.MapGet("/api/market/evidence/{id}.zip", (string id) =>
        {
            var zip = OPTIC.Services.LiquidityEvidenceService.Zip(id);
            return zip is null
                ? Results.NotFound()
                : Results.File(zip, "application/zip", $"opt-liquidity-evidence-{id}.zip");
        });

        // How the user says each transfer should be treated on the tax report (sale, node purchase, gift, ...).
        // One JSON file per wallet in odata/users/<name>/tax-labels; keys are "txHash|direction|counterparty".
        // Wallets that gave the user OPT as a gift (address -> name), shared by all the user's wallets.
        app.MapGet("/api/tax/gift-senders", (HttpContext ctx) =>
        {
            var path = TaxGiftSendersPath(ctx);
            return File.Exists(path)
                ? Results.Text(File.ReadAllText(path), "application/json")
                : Results.Json(new Dictionary<string, string>());
        });

        app.MapPost("/api/tax/gift-senders", async (HttpContext ctx) =>
        {
            Dictionary<string, string>? senders;
            try { senders = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(ctx.Request.Body, cancellationToken: ctx.RequestAborted); }
            catch (JsonException) { return Results.Json(new { error = "Expected a JSON object of address: name." }, statusCode: 400); }
            if (senders is null)
                return Results.Json(new { error = "Expected a JSON object of address: name." }, statusCode: 400);
            var clean = new Dictionary<string, string>();
            foreach (var (address, name) in senders)
            {
                var a = address.Trim().ToLowerInvariant();
                if (!System.Text.RegularExpressions.Regex.IsMatch(a, "^optio1[02-9ac-hj-np-z]{38,58}$"))
                    return Results.Json(new { error = $"Not a valid optio1... address: {address}" }, statusCode: 400);
                clean[a] = (name ?? "").Trim();
            }
            var path = TaxGiftSendersPath(ctx);
            lock (TaxLabelsLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(clean, new JsonSerializerOptions { WriteIndented = true }));
            }
            return Results.Json(clean);
        });

        // Names the user gave to other people's addresses (address -> name).
        app.MapGet("/api/address-book", (HttpContext ctx) =>
        {
            var path = AddressBookPath(ctx);
            return File.Exists(path)
                ? Results.Text(File.ReadAllText(path), "application/json")
                : Results.Json(new Dictionary<string, string>());
        });

        app.MapPost("/api/address-book", async (HttpContext ctx) =>
        {
            Dictionary<string, string>? names;
            try { names = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(ctx.Request.Body, cancellationToken: ctx.RequestAborted); }
            catch (JsonException) { return Results.Json(new { error = "Expected a JSON object of address: name." }, statusCode: 400); }
            if (names is null)
                return Results.Json(new { error = "Expected a JSON object of address: name." }, statusCode: 400);
            var clean = new Dictionary<string, string>();
            foreach (var (address, name) in names)
            {
                var a = address.Trim().ToLowerInvariant();
                if (!System.Text.RegularExpressions.Regex.IsMatch(a, "^optio1[02-9ac-hj-np-z]{38,58}$"))
                    return Results.Json(new { error = $"Not a valid optio1... address: {address}" }, statusCode: 400);
                var n = (name ?? "").Trim();
                if (n.Length > 0) clean[a] = n.Length > 80 ? n[..80] : n;
            }
            var path = AddressBookPath(ctx);
            lock (TaxLabelsLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(clean, new JsonSerializerOptions { WriteIndented = true }));
            }
            return Results.Json(clean);
        });

        app.MapGet("/api/tax/labels", (HttpContext ctx) =>
        {
            var address = (ctx.Request.Query["address"].ToString() ?? "").Trim().ToLowerInvariant();
            if (!System.Text.RegularExpressions.Regex.IsMatch(address, "^optio1[02-9ac-hj-np-z]{38,58}$"))
                return Results.Json(new { error = "Enter a valid optio1... wallet address." }, statusCode: 400);
            var path = TaxLabelsPath(ctx, address);
            if (!File.Exists(path))
                return Results.Json(new Dictionary<string, JsonElement>());
            return Results.Text(File.ReadAllText(path), "application/json");
        });

        app.MapPost("/api/tax/labels", async (HttpContext ctx) =>
        {
            var address = (ctx.Request.Query["address"].ToString() ?? "").Trim().ToLowerInvariant();
            if (!System.Text.RegularExpressions.Regex.IsMatch(address, "^optio1[02-9ac-hj-np-z]{38,58}$"))
                return Results.Json(new { error = "Enter a valid optio1... wallet address." }, statusCode: 400);
            Dictionary<string, JsonElement>? update;
            try { update = await JsonSerializer.DeserializeAsync<Dictionary<string, JsonElement>>(ctx.Request.Body, cancellationToken: ctx.RequestAborted); }
            catch (JsonException) { return Results.Json(new { error = "Expected a JSON object." }, statusCode: 400); }
            if (update is null)
                return Results.Json(new { error = "Expected a JSON object." }, statusCode: 400);

            var path = TaxLabelsPath(ctx, address);
            lock (TaxLabelsLock)
            {
                var labels = File.Exists(path)
                    ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(path)) ?? new()
                    : new Dictionary<string, JsonElement>();
                // A null value removes the label, so the transfer goes back to its default treatment.
                foreach (var (key, value) in update)
                {
                    if (value.ValueKind == JsonValueKind.Null) labels.Remove(key);
                    else labels[key] = value;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(labels, new JsonSerializerOptions { WriteIndented = true }));
                return Results.Json(labels);
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

        Console.WriteLine($"Starting OPTIC web dashboard at {url}" + (DashboardHttps.Current.Enabled ? $" and https://{host}:{DashboardHttps.Current.Port}" : ""));
        await app.RunAsync();
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

    // Coinstore's OPT wallets, identified from the chain: customers deposit to the collector with their
    // account number as the memo, the collector forwards everything to the hot wallet, and withdrawals
    // are paid from the hot wallet.
    const string CoinstoreDepositCollector = "optio13ekmwuepa047es0nn03mafj5q80kg382d3sflz";
    const string CoinstoreHotWallet = "optio12e3lctqkpg2rrkvywd6596at358dr50cpmcr5x";
    static object? _coinstoreFlows;
    static DateTime _coinstoreFlowsAt;
    static readonly SemaphoreSlim _coinstoreFlowsGate = new(1, 1);

    /// <summary>
    /// Customer deposits into and withdrawals out of Coinstore per UTC day, read from the chain.
    /// Transfers between Coinstore's own two wallets are left out. Cached for an hour.
    /// </summary>
    static async Task<object> GetCoinstoreFlowsAsync(CancellationToken ct)
    {
        await _coinstoreFlowsGate.WaitAsync(ct);
        try
        {
            if (_coinstoreFlows is not null && DateTime.UtcNow - _coinstoreFlowsAt < TimeSpan.FromHours(1))
                return _coinstoreFlows;

            var collector = await GetWalletLedgerAsync(CoinstoreDepositCollector, ct);
            var hot = await GetWalletLedgerAsync(CoinstoreHotWallet, ct);
            bool Own(string a) => a == CoinstoreDepositCollector || a == CoinstoreHotWallet;

            var deposits = collector.Entries.Concat(hot.Entries)
                .Where(e => e.Direction == "in" && e.Category == "wallet" && !Own(e.Counterparty)).ToList();
            var withdrawals = collector.Entries.Concat(hot.Entries)
                .Where(e => e.Direction == "out" && e.Category == "wallet" && !Own(e.Counterparty)).ToList();

            var days = deposits.Select(e => e.TimeUtc.Date).Concat(withdrawals.Select(e => e.TimeUtc.Date)).Distinct().OrderBy(d => d)
                .Select(d => new
                {
                    date = d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    depositCount = deposits.Count(e => e.TimeUtc.Date == d),
                    // Coinstore account numbers (memo) that deposited that day; the sending wallet when there is no memo.
                    depositAccounts = deposits.Where(e => e.TimeUtc.Date == d).Select(e => e.Memo.Trim().Length > 0 ? e.Memo.Trim() : e.Counterparty).Distinct().ToList(),
                    depositOpt = deposits.Where(e => e.TimeUtc.Date == d).Sum(e => e.AmountOpt),
                    withdrawalCount = withdrawals.Count(e => e.TimeUtc.Date == d),
                    withdrawalOpt = withdrawals.Where(e => e.TimeUtc.Date == d).Sum(e => e.AmountOpt),
                })
                .ToList();

            _coinstoreFlows = new
            {
                depositCollector = CoinstoreDepositCollector,
                hotWallet = CoinstoreHotWallet,
                truncated = collector.Truncated || hot.Truncated,
                depositWallets = deposits.Select(e => e.Counterparty).Distinct().Count(),
                depositAccounts = deposits.Where(e => e.Memo.Length > 0).Select(e => e.Memo.Trim()).Distinct().Count(),
                withdrawalWallets = withdrawals.Select(e => e.Counterparty).Distinct().Count(),
                firstDeposit = deposits.Count > 0 ? deposits.Min(e => e.TimeUtc) : (DateTime?)null,
                lastDeposit = deposits.Count > 0 ? deposits.Max(e => e.TimeUtc) : (DateTime?)null,
                firstWithdrawal = withdrawals.Count > 0 ? withdrawals.Min(e => e.TimeUtc) : (DateTime?)null,
                lastWithdrawal = withdrawals.Count > 0 ? withdrawals.Max(e => e.TimeUtc) : (DateTime?)null,
                days,
            };
            _coinstoreFlowsAt = DateTime.UtcNow;
            return _coinstoreFlows;
        }
        finally
        {
            _coinstoreFlowsGate.Release();
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

    static readonly object TaxLabelsLock = new();

    // Tax labels, named addresses and gift wallets belong to the signed-in user (odata/users/<name>/).
    static string TaxLabelsPath(HttpContext ctx, string address) =>
        Path.Combine(DashboardAuth.UserDataDir(ctx), "tax-labels", address + ".json");

    static string AddressBookPath(HttpContext ctx) =>
        Path.Combine(DashboardAuth.UserDataDir(ctx), "address-book.json");

    static string TaxGiftSendersPath(HttpContext ctx) =>
        Path.Combine(DashboardAuth.UserDataDir(ctx), "gift-senders.json");

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

    internal static Dictionary<string, string> LoadDashboardConfig()
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
            "market-liquidity" => "Market Liquidity",
            "liquidity-evidence" => "Liquidity Evidence",
            "tax-report" => "Tax Report",
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
            "settings" => "My Settings",
            "admin" => "Admin",
            _ => "Dashboard"
        };
    }

    static string RenderDashboardHtml(string url, string page, Func<string, string> activeLink, HttpContext ctx)
    {
        var sb = new System.Text.StringBuilder();
        var user = WebUtility.HtmlEncode(DashboardAuth.UserName(ctx));
        var userLinks = $@"<a href=""/page/settings"" class=""header-link"" title=""My Settings"">{user}</a>"
            + (DashboardAuth.IsAdmin(ctx) ? @"<a href=""/page/admin"" class=""header-link"">Admin</a>" : "")
            + @"<a href=""/logout"" class=""header-link"" title=""Sign out of OPTIC"">Sign out</a>";
        sb.Append(GetCSSAndHeader(url, page, activeLink).Replace("<!--OPTIC-USER-LINKS-->", userLinks));
        sb.Append(DashboardSettings.BootScript(ctx));
        
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
                            <a href=""/page/market-liquidity"" class=""menu-item" + (page == "market-liquidity" ? @""" active" : @"""") + @""">Market Liquidity</a>
                            <a href=""/page/liquidity-evidence"" class=""menu-item" + (page == "liquidity-evidence" ? @""" active" : @"""") + @""">Liquidity Evidence</a>
                            <a href=""/page/tax-report"" class=""menu-item" + (page == "tax-report" ? @""" active" : @"""") + @""">Tax Report</a>
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
                    <!--OPTIC-USER-LINKS-->
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
            case "market-liquidity":
                AppendMarketLiquidityCard(sb);
                break;
            case "liquidity-evidence":
                AppendLiquidityEvidenceCard(sb);
                break;
            case "tax-report":
                AppendTaxReportCard(sb);
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
            case "settings":
                AppendSettingsCard(sb);
                break;
            case "admin":
                AppendAdminCard(sb);
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
    // Shared by Liquidity Evidence and Address Distributions: how much OPT the market could absorb each day.
    static void AppendMarketMathScript(System.Text.StringBuilder sb)
    {
        sb.Append(@"
                    <script>
                        window.opticMarket = window.opticMarket || (() => {
                            let pending = null;
                            const getJson = async (url) => {
                                const r = await fetch(url);
                                const j = await r.json();
                                if (!r.ok) throw new Error(j.error || `${url} failed`);
                                return j;
                            };
                            const addDays = (d, n) => { const t = new Date(d + 'T00:00:00Z'); t.setUTCDate(t.getUTCDate() + n); return t.toISOString().slice(0, 10); };
                            return {
                                // Market history and Coinstore's on-chain deposits/withdrawals, fetched once per page.
                                load() {
                                    if (!pending) pending = Promise.all([getJson('/api/market/liquidity'), getJson('/api/market/coinstore-flows')])
                                        .then(([market, flows]) => ({ market, flows }))
                                        .catch(e => { pending = null; throw e; });
                                    return pending;
                                },
                                // One entry per UTC day from the first market record to today. Exchange volume is either OPT
                                // withdrawn from Coinstore on chain (basis 'withdrawals') or the higher of CoinMarketCap's and
                                // Coinstore's reported volume ('reported'); net Osmosis swaps are added. absorb = share of the
                                // average daily volume over the previous 30 days.
                                daily(market, flows, basis, share) {
                                    const flowBy = new Map(flows.days.map(d => [d.date, d]));
                                    const mdBy = new Map(market.days.map(d => [d.date, d]));
                                    const start = [market.days[0]?.date, flows.days[0]?.date].filter(Boolean).sort()[0];
                                    const end = new Date().toISOString().slice(0, 10);
                                    const days = [];
                                    let lastPrice = null;
                                    for (let d = start; d && d <= end; d = addDays(d, 1)) {
                                        const m = mdBy.get(d) || {}, f = flowBy.get(d) || {};
                                        const price = m.cmcClose ?? m.csClose ?? m.osPrice ?? lastPrice;
                                        if (price != null) lastPrice = price;
                                        const cmcOpt = m.cmcVolumeUsd != null && m.cmcClose ? m.cmcVolumeUsd / m.cmcClose : 0;
                                        const reportedEx = Math.max(cmcOpt, m.csVolumeOpt || 0);
                                        const osOpt = (m.osSoldOpt || 0) + (m.osBoughtOpt || 0);
                                        const exOpt = basis === 'reported' ? reportedEx : (f.withdrawalOpt || 0);
                                        days.push({ d, price, cmcClose: m.cmcClose ?? null, csClose: m.csClose ?? null, osPrice: m.osPrice ?? null, csReported: m.csVolumeOpt || 0, reportedEx, deposit: f.depositOpt || 0, depositCount: f.depositCount || 0, depositAccounts: f.depositAccounts || [], withdraw: f.withdrawalOpt || 0, withdrawCount: f.withdrawalCount || 0, osOpt, osTxs: m.osTxs || 0, total: exOpt + osOpt });
                                    }
                                    for (let i = 0, sum = 0; i < days.length; i++) {
                                        sum += days[i].total;
                                        if (i >= 30) sum -= days[i - 30].total;
                                        days[i].absorb = sum / Math.min(i + 1, 30) * share;
                                    }
                                    return days;
                                },
                                // Coinstore counts as open to the public on a day when at least PUBLIC_ACCOUNTS different accounts
                                // deposited OPT within PUBLIC_WINDOW days either side, and not after its last deposit. In the first
                                // half of 2025 only one to a few accounts got OPT in, which is not a market the public could use.
                                PUBLIC_ACCOUNTS: 5,
                                PUBLIC_WINDOW: 7,
                                publicOpen(days, flows) {
                                    const last = flows.lastDeposit ? String(flows.lastDeposit).slice(0, 10) : null;
                                    const W = this.PUBLIC_WINDOW;
                                    return days.map((x, i) => {
                                        if (!last || x.d > last) return false;
                                        const accounts = new Set();
                                        for (let j = Math.max(0, i - W); j <= Math.min(days.length - 1, i + W); j++) for (const a of days[j].depositAccounts) accounts.add(a);
                                        return accounts.size >= this.PUBLIC_ACCOUNTS;
                                    });
                                },
                                // Runs of days Coinstore was closed to the public, from its first deposit to today.
                                closedPeriods(days, flows) {
                                    const open = this.publicOpen(days, flows);
                                    const first = flows.firstDeposit ? String(flows.firstDeposit).slice(0, 10) : null;
                                    const runs = [];
                                    let from = null;
                                    days.forEach((x, i) => {
                                        const closed = first && x.d >= first && !open[i];
                                        if (closed && from == null) from = i;
                                        if ((!closed || i === days.length - 1) && from != null) {
                                            const to = closed ? i : i - 1;
                                            runs.push({ from: days[from].d, to: days[to].d, i0: from, i1: to, open: closed && i === days.length - 1 });
                                            from = null;
                                        }
                                    });
                                    return runs;
                                },
                                // Annualised volatility of the daily price (Coinstore's close, else CoinMarketCap's) over the 90 days up to each day (log returns
                                // between days with a real quote). Under 20 returns, the first 20 returns of the history are used.
                                volatility(days) {
                                    const q = days.map(x => x.csClose ?? x.cmcClose ?? x.osPrice ?? null);
                                    const rets = [];
                                    let prev = null;
                                    q.forEach((p, i) => { if (p > 0) { if (prev != null) rets.push({ i, r: Math.log(p / q[prev]) }); prev = i; } });
                                    const sd = (xs) => { const m = xs.reduce((s, x) => s + x, 0) / xs.length; return Math.sqrt(xs.reduce((s, x) => s + (x - m) * (x - m), 0) / xs.length); };
                                    const early = rets.length >= 2 ? sd(rets.slice(0, 20).map(x => x.r)) * Math.sqrt(365) : 1;
                                    return days.map((x, i) => {
                                        const w = rets.filter(r => r.i <= i && r.i > i - 90).map(r => r.r);
                                        return w.length >= 20 ? sd(w) * Math.sqrt(365) : early;
                                    });
                                },
                                // Discount for lack of marketability: Finnerty (2012) average-strike put model, the value of being
                                // unable to sell for T years at annual volatility sigma, as a fraction of the price.
                                finnerty(sigma, T) {
                                    const s2 = sigma * sigma * T;
                                    if (!(s2 > 1e-8)) return 0;
                                    const v2 = s2 + Math.log(2 * (Math.exp(s2) - s2 - 1)) - 2 * Math.log(Math.exp(s2) - 1);
                                    const erf = (x) => { const t = 1 / (1 + 0.3275911 * Math.abs(x)); const y = 1 - ((((1.061405429 * t - 1.453152027) * t + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * Math.exp(-x * x); return x >= 0 ? y : -y; };
                                    const N = (x) => 0.5 * (1 + erf(x / Math.SQRT2));
                                    return Math.max(0, 2 * N(Math.sqrt(Math.max(0, v2)) / 2) - 1);
                                },
                                // Price on the market you could actually sell on each day (Map date -> { price, source, sellable }):
                                // Coinstore's close while it was open to the public, else the average price of that day's Osmosis
                                // swaps, else (no market you could reach) per rule: 'dlom' = the quoted daily price less a discount
                                // for lack of marketability sized to the days until you could next sell, 'next' = price on the next
                                // day you could sell, 'last' = price on the last day you could, 'quoted' = the quoted daily price.
                                accessible(days, flows, rule) {
                                    const open = this.publicOpen(days, flows);
                                    const out = days.map((x, i) => {
                                        if (open[i] && x.csClose != null) return { d: x.d, price: x.csClose, source: 'Coinstore (open to the public)', sellable: true };
                                        if (x.osPrice != null) return { d: x.d, price: x.osPrice, source: open[i] ? 'Osmosis swaps' : 'Osmosis swaps (Coinstore closed to the public)', sellable: true };
                                        return { d: x.d, price: null, source: null, sellable: false };
                                    });
                                    const fill = (list, label) => {
                                        let ref = null;
                                        for (const x of list) {
                                            if (x.sellable) ref = x;
                                            else if (x.price == null && ref) { x.price = ref.price; x.source = `No market you could reach; ${label} sellable day ${ref.d}`; }
                                        }
                                    };
                                    if (rule === 'dlom') {
                                        const vol = this.volatility(days);
                                        let next = null;
                                        for (let i = out.length - 1; i >= 0; i--) {
                                            if (out[i].sellable) { next = i; continue; }
                                            // Same exchange as on open days: Coinstore's close, else the quoted price once Coinstore stopped trading.
                                            const quoted = days[i].csClose ?? days[i].price;
                                            if (quoted == null) continue;
                                            const waitDays = next == null ? 365 : next - i;
                                            const discount = this.finnerty(vol[i], waitDays / 365);
                                            out[i] = { d: days[i].d, price: quoted * (1 - discount), source: `No market you could reach; ${days[i].csClose != null ? 'Coinstore close' : 'quoted price'} less ${(discount * 100).toFixed(1)}% marketability discount (${waitDays} day(s) until you could sell${next == null ? ', no later market so 1 year assumed' : ''}; volatility ${(vol[i] * 100).toFixed(0)}% a year)`, sellable: false, quoted, discount, waitDays, vol: vol[i] };
                                        }
                                    } else if (rule === 'quoted') {
                                        days.forEach((x, i) => { if (!out[i].sellable && x.price != null) { out[i].price = x.price; out[i].source = 'No market you could reach; quoted price'; } });
                                    } else if (rule === 'last') {
                                        fill(out, 'last');
                                        fill(out.slice().reverse(), 'next');
                                    } else {
                                        fill(out.slice().reverse(), 'next');
                                        fill(out, 'last');
                                    }
                                    return new Map(out.map(x => [x.d, x]));
                                }
                            };
                        })();
                    </script>");
    }

    static void AppendExchangesCard(System.Text.StringBuilder sb)
    {
        AppendMyWalletsScript(sb);
        sb.Append(@"
                    <div class=""card"" style=""grid-column: 1 / -1;"">
                        <h2>Exchanges</h2>
                        <div class=""card-content"">
                            <div style=""color: var(--text-secondary); font-size: 13px; margin-bottom: 12px;"">Exchange deposit addresses. On Profit &amp; Loss and the Tax Report, sends to an exchange are labelled as sales there and receipts from it as purchases. <span class=""ex-admin-only"">This list is shared by every user; only an admin can change it.</span><span class=""ex-user-only"">The list is set by the admin and shared by every user.</span> If the exchange needed a memo to credit your account (Coinstore does), enter yours so sends to your own account can be told apart from sends to someone else&#39;s. Your memo is saved to your account only.</div>
                            <form id=""ex-form"" class=""ex-admin-only"" style=""display: flex; gap: 8px; flex-wrap: wrap; align-items: flex-end; margin-bottom: 16px;"">
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
                                            <th class=""ex-admin-only""></th>
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
                            const admin = !!(window.opticUser && window.opticUser.isAdmin);
                            document.querySelectorAll(admin ? "".ex-user-only"" : "".ex-admin-only"").forEach(el => el.style.display = ""none"");
                            const exBody = document.getElementById(""ex-body"");
                            const exStatus = document.getElementById(""ex-status"");
                            const escEx = (v) => String(v ?? """").replace(/[&<>""\x27]/g, ch => ""&#"" + ch.charCodeAt(0) + "";"");
                            const renderExchanges = () => {
                                const items = W.exchanges();
                                exBody.innerHTML = items.length === 0
                                    ? `<tr><td colspan=""4"" style=""text-align: center; color: var(--text-secondary);"">No exchanges saved.</td></tr>`
                                    : items.map(x => `<tr>
                                        <td>${admin ? `<input type=""text"" data-ex-name=""${escEx(x.a)}"" value=""${escEx(x.n)}"" maxlength=""30"" style=""width: 100%; min-width: 120px;"">` : escEx(x.n)}</td>
                                        <td class=""address-cell"">${escEx(x.a)}</td>
                                        <td><input type=""text"" data-ex-memo=""${escEx(x.a)}"" value=""${escEx(x.m)}"" maxlength=""64"" placeholder=""none"" style=""width: 100%; min-width: 140px;""></td>
                                        ${admin ? `<td><button type=""button"" data-ex-remove=""${escEx(x.a)}"" style=""padding: 4px 10px; font-size: 11px;"">Remove</button></td>` : """"}
                                    </tr>`).join("""");
                            };
                            const saved = (p, text) => p.then(() => exStatus.textContent = text).catch(e => exStatus.textContent = ""Could not save: "" + e.message);
                            document.getElementById(""ex-form"").addEventListener(""submit"", (e) => {
                                e.preventDefault();
                                const address = document.getElementById(""ex-address"").value.trim().toLowerCase();
                                if (!W.valid(address)) { exStatus.textContent = ""That is not a valid optio1... address.""; return; }
                                if (W.exchange(address)) { exStatus.textContent = ""That address is already saved.""; return; }
                                const p = W.saveExchanges([...W.exchanges(), { a: address, n: document.getElementById(""ex-name"").value, m: document.getElementById(""ex-memo"").value }]);
                                [""ex-name"", ""ex-address"", ""ex-memo""].forEach(id => document.getElementById(id).value = """");
                                renderExchanges();
                                saved(p, ""Saved for everyone."");
                            });
                            exBody.addEventListener(""change"", (e) => {
                                const a = e.target.dataset.exName || e.target.dataset.exMemo;
                                if (!a) return;
                                const field = e.target.dataset.exName ? ""n"" : ""m"";
                                saved(W.saveExchanges(W.exchanges().map(x => x.a === a ? { ...x, [field]: e.target.value } : x)), field === ""m"" ? ""Memo saved to your account."" : ""Saved for everyone."");
                            });
                            exBody.addEventListener(""click"", (e) => {
                                const btn = e.target.closest(""button[data-ex-remove]"");
                                if (!btn) return;
                                const p = W.saveExchanges(W.exchanges().filter(x => x.a !== btn.dataset.exRemove));
                                renderExchanges();
                                saved(p, ""Removed for everyone."");
                            });
                            renderExchanges();
                        })();
                    </script>
");
    }

    static void AppendSettingsCard(System.Text.StringBuilder sb)
    {
        AppendMyWalletsScript(sb);
        sb.Append(@"
                    <style>
                        .st-section { margin: 0 0 22px; }
                        .st-section h3 { margin: 0 0 8px; font-size: 15px; }
                        .st-row { display: flex; gap: 8px; flex-wrap: wrap; align-items: flex-end; }
                        .st-row .form-group { margin-bottom: 0; }
                        .st-note { color: var(--text-secondary); font-size: 13px; margin: 0 0 10px; }
                        .st-badge { display: inline-block; font-size: 11px; font-weight: 600; border-radius: 10px; padding: 1px 8px; border: 1px solid currentColor; color: var(--accent-primary); }
                        .st-check { display: flex; gap: 8px; align-items: center; font-size: 14px; margin: 6px 0; cursor: pointer; }
                        .st-check input { width: auto; }
                    </style>
                    <div class=""card"" style=""grid-column: 1 / -1;"">
                        <h2>My Settings</h2>
                        <div class=""card-content"">
                            <p class=""st-note"">Everything on this page is saved to your account, so it follows you to any computer or phone you sign in from. Other users have their own wallets, labels and settings and cannot see yours.</p>
                            <div class=""st-section"">
                                <h3>Account</h3>
                                <p style=""margin: 0 0 10px;"">Signed in as <strong id=""st-name""></strong> <span id=""st-role""></span> · <a href=""/logout"">Sign out</a></p>
                                <form id=""st-pw"" class=""st-row"" autocomplete=""on"">
                                    <input type=""text"" name=""username"" id=""st-pw-user"" autocomplete=""username"" hidden>
                                    <div class=""form-group"" style=""flex: 0 1 200px;""><label for=""st-pw-current"">Current password</label><input type=""password"" id=""st-pw-current"" autocomplete=""current-password"" required></div>
                                    <div class=""form-group"" style=""flex: 0 1 200px;""><label for=""st-pw-next"">New password</label><input type=""password"" id=""st-pw-next"" autocomplete=""new-password"" minlength=""8"" required></div>
                                    <div class=""form-group"" style=""flex: 0 1 200px;""><label for=""st-pw-repeat"">Repeat new password</label><input type=""password"" id=""st-pw-repeat"" autocomplete=""new-password"" minlength=""8"" required></div>
                                    <button type=""submit"">Change password</button>
                                </form>
                                <div id=""st-pw-status"" class=""st-note"" style=""margin-top: 6px;""></div>
                            </div>
                            <div class=""st-section"">
                                <h3>Preferences</h3>
                                <label class=""st-check""><input type=""checkbox"" id=""st-pref-dist""> Include node distributions on Profit &amp; Loss</label>
                                <div class=""st-row"" style=""margin-top: 6px;"">
                                    <div class=""form-group"" style=""flex: 0 0 220px;""><label for=""st-pref-scale"">OPT price chart scale</label><select id=""st-pref-scale""><option value=""linear"">Linear</option><option value=""log"">Logarithmic</option></select></div>
                                </div>
                                <div id=""st-pref-status"" class=""st-note"" style=""margin-top: 6px;""></div>
                            </div>
                            <div class=""st-section"">
                                <h3>My saved data</h3>
                                <div class=""data-table-wrap pnl-table-wrap"">
                                    <table class=""data-table"">
                                        <thead><tr><th>What</th><th>Saved</th><th>Where to change it</th></tr></thead>
                                        <tbody id=""st-data""><tr><td colspan=""3"" style=""color: var(--text-secondary);"">Loading...</td></tr></tbody>
                                    </table>
                                </div>
                                <p class=""st-note"" style=""margin-top: 10px;""><a href=""/api/me/export"" download>Download all my data (.zip)</a> · a backup of your wallets, settings, tax labels, expenses, named addresses and gift wallets.</p>
                            </div>
                            <div class=""st-section"" id=""st-admin"" hidden>
                                <h3>Admin</h3>
                                <p class=""st-note"" style=""margin: 0;"">System-wide settings (users, the exchange list, node connection, network access) are on the <a href=""/page/admin"">Admin</a> page.</p>
                            </div>
                        </div>
                    </div>
                    <script>
                        (function () {
                            const $ = (id) => document.getElementById(id);
                            const esc = (v) => String(v ?? """").replace(/[&<>""\x27]/g, ch => ""&#"" + ch.charCodeAt(0) + "";"");
                            const U = window.opticUser || {};
                            $(""st-name"").textContent = U.name || """";
                            $(""st-pw-user"").value = U.name || """";
                            $(""st-role"").innerHTML = U.isAdmin ? `<span class=""st-badge"">Admin</span>` : """";
                            $(""st-admin"").hidden = !U.isAdmin;

                            $(""st-pw"").addEventListener(""submit"", async (e) => {
                                e.preventDefault();
                                const st = $(""st-pw-status"");
                                if ($(""st-pw-next"").value !== $(""st-pw-repeat"").value) { st.textContent = ""The new passwords do not match.""; return; }
                                try {
                                    const r = await fetch(""/api/me/password"", { method: ""POST"", headers: { ""Content-Type"": ""application/json"" }, body: JSON.stringify({ current: $(""st-pw-current"").value, next: $(""st-pw-next"").value }) });
                                    const j = await r.json();
                                    if (!r.ok) throw new Error(j.error || ""Save failed"");
                                    [""st-pw-current"", ""st-pw-next"", ""st-pw-repeat""].forEach(id => $(id).value = """");
                                    st.textContent = ""Password changed."";
                                } catch (err) { st.textContent = ""Could not change it: "" + err.message; }
                            });

                            const P = window.opticPrefs;
                            $(""st-pref-dist"").checked = P.get(""pnlIncludeDist"") === true;
                            $(""st-pref-scale"").value = P.get(""priceScale"") === ""log"" ? ""log"" : ""linear"";
                            const prefSaved = (p) => p.then(() => $(""st-pref-status"").textContent = ""Saved."").catch(err => $(""st-pref-status"").textContent = ""Could not save: "" + err.message);
                            $(""st-pref-dist"").addEventListener(""change"", (e) => prefSaved(P.set(""pnlIncludeDist"", e.target.checked)));
                            $(""st-pref-scale"").addEventListener(""change"", (e) => prefSaved(P.set(""priceScale"", e.target.value)));

                            fetch(""/api/me/summary"").then(r => r.json()).then(d => {
                                const W = window.opticWallets;
                                const short = (a) => a.length > 20 ? a.slice(0, 12) + ""..."" + a.slice(-6) : a;
                                const rows = [
                                    [""My wallets"", d.wallets, `<a href=""/page/my-wallets"">My Wallets</a>`],
                                    [""Exchange deposit memos"", Object.values((U.settings || {}).exchangeMemos || {}).filter(Boolean).length, `<a href=""/page/my-wallets"">My Wallets</a> (Exchanges)`],
                                    [""Named addresses"", d.namedAddresses, `<a href=""/page/tax-report"">Tax Report</a> (Named addresses)`],
                                    [""Gift wallets"", d.giftWallets, `<a href=""/page/tax-report"">Tax Report</a> (Gift wallets)`],
                                ].map(([what, n, where]) => `<tr><td>${what}</td><td>${n}</td><td>${where}</td></tr>`);
                                for (const t of d.taxWallets) {
                                    const label = W.label(t.address);
                                    rows.push(`<tr><td>Tax Report labels: <span class=""address-cell"" title=""${esc(t.address)}"">${esc(label ? label + "" · "" + short(t.address) : short(t.address))}</span></td><td>${t.transfers} transfer(s), ${t.expenses} expense(s)</td><td><a href=""/page/tax-report?address=${encodeURIComponent(t.address)}"">Tax Report</a></td></tr>`);
                                }
                                $(""st-data"").innerHTML = rows.join("""");
                            }).catch(err => $(""st-data"").innerHTML = `<tr><td colspan=""3"">Could not load: ${esc(err.message)}</td></tr>`);
                        })();
                    </script>
");
    }

    static void AppendAdminCard(System.Text.StringBuilder sb)
    {
        AppendMyWalletsScript(sb);
        sb.Append(@"
                    <style>
                        .ad-note { color: var(--text-secondary); font-size: 13px; margin: 0 0 10px; }
                        .ad-row { display: flex; gap: 8px; flex-wrap: wrap; align-items: flex-end; }
                        .ad-row .form-group { margin-bottom: 0; }
                        .ad-badge { display: inline-block; font-size: 11px; font-weight: 600; border-radius: 10px; padding: 1px 8px; border: 1px solid currentColor; }
                        .ad-badge.admin { color: var(--accent-primary); }
                        .ad-badge.warn { background: var(--warning); color: #000; border-color: var(--warning); }
                        .ad-badge.ok { color: var(--success, #107c10); }
                        .ad-check { display: flex; gap: 6px; align-items: center; font-size: 14px; cursor: pointer; padding-bottom: 8px; }
                        .ad-check input { width: auto; }
                        .ad-actions button { font-size: 11px; padding: 3px 10px; margin: 2px 4px 2px 0; }
                        .ad-code { display: block; font-family: Consolas, monospace; font-size: 12px; background: var(--bg-primary); border: 1px solid var(--border); border-radius: 6px; padding: 8px 10px; white-space: pre-wrap; word-break: break-all; }
                        .ad-config { display: grid; grid-template-columns: repeat(auto-fill, minmax(230px, 1fr)); gap: 8px 12px; }
                        .ad-config .form-group { margin-bottom: 0; }
                        .ad-config small { color: var(--text-secondary); }
                    </style>
                    <div class=""card"" style=""grid-column: 1 / -1;"">
                        <h2>Users</h2>
                        <div class=""card-content"">
                            <p class=""ad-note"">People who can sign in. Each user has their own wallets, tax labels, expenses, named addresses and settings. Admins can also open this page.</p>
                            <div class=""data-table-wrap pnl-table-wrap"" style=""margin-bottom: 12px;"">
                                <table class=""data-table"">
                                    <thead><tr><th>Username</th><th>Role</th><th></th></tr></thead>
                                    <tbody id=""ad-users""></tbody>
                                </table>
                            </div>
                            <form id=""ad-user-form"" class=""ad-row"" autocomplete=""off"">
                                <div class=""form-group"" style=""flex: 0 1 200px;""><label for=""ad-user-name"">Username</label><input type=""text"" id=""ad-user-name"" maxlength=""40"" autocomplete=""off"" spellcheck=""false"" required></div>
                                <div class=""form-group"" style=""flex: 0 1 200px;""><label for=""ad-user-pw"">Password (8+ characters)</label><input type=""password"" id=""ad-user-pw"" minlength=""8"" autocomplete=""new-password"" required></div>
                                <label class=""ad-check""><input type=""checkbox"" id=""ad-user-admin""> Admin</label>
                                <button type=""submit"">Add user</button>
                            </form>
                            <div id=""ad-user-status"" class=""ad-note"" style=""margin-top: 6px;""></div>
                        </div>
                    </div>
");
        AppendExchangesCard(sb);
        sb.Append(@"
                    <div class=""card"" style=""grid-column: 1 / -1;"">
                        <h2>Node connection and startup (optic.conf)</h2>
                        <div class=""card-content"">
                            <p class=""ad-note"">Saved to <span id=""ad-conf-path"" class=""address-cell""></span>. Reports use the new values straight away; the two dashboard settings take effect the next time OPTIC starts. Leave a field empty to use the built-in default.</p>
                            <form id=""ad-conf-form"">
                                <div class=""ad-config"" id=""ad-conf-fields""></div>
                                <button type=""submit"" style=""margin-top: 12px;"">Save settings</button>
                                <span id=""ad-conf-status"" class=""ad-note"" style=""margin-left: 8px;""></span>
                            </form>
                        </div>
                    </div>
                    <div class=""card"" style=""grid-column: 1 / -1;"">
                        <h2>HTTPS (secure connection)</h2>
                        <div class=""card-content"">
                            <div id=""ad-https""><p class=""ad-note"">Loading...</p></div>
                            <h3 style=""font-size: 14px; margin: 16px 0 6px;"">Use your own certificate</h3>
                            <p class=""ad-note"">If you have a certificate for a domain name (for example from Let&#39;s Encrypt, Cloudflare or your domain registrar), upload it as a .pfx or .p12 file with its private key. Browsers then trust OPTIC without any warning when you open it by that name. Otherwise OPTIC uses a self-signed certificate it made for this computer.</p>
                            <form id=""ad-https-form"" class=""ad-row"" enctype=""multipart/form-data"">
                                <div class=""form-group"" style=""flex: 1 1 260px;""><label for=""ad-https-file"">Certificate file (.pfx / .p12)</label><input type=""file"" id=""ad-https-file"" name=""pfx"" accept="".pfx,.p12"" required></div>
                                <div class=""form-group"" style=""flex: 0 1 200px;""><label for=""ad-https-pw"">File password</label><input type=""password"" id=""ad-https-pw"" name=""password"" autocomplete=""off""></div>
                                <button type=""submit"">Upload certificate</button>
                                <button type=""button"" id=""ad-https-self"" style=""display: none;"">Go back to self-signed</button>
                            </form>
                            <div id=""ad-https-status"" class=""ad-note"" style=""margin-top: 6px;""></div>
                        </div>
                    </div>
                    <script>
                        (function () {
                            const $ = (id) => document.getElementById(id);
                            const esc = (v) => String(v ?? """").replace(/[&<>""\x27]/g, ch => ""&#"" + ch.charCodeAt(0) + "";"");
                            const st = $(""ad-https-status"");
                            const load = async () => {
                                const h = await (await fetch(""/api/admin/https"")).json();
                                const c = h.current;
                                const host = location.hostname;
                                const httpsUrl = `https://${host}${c.port === 443 ? """" : "":"" + c.port}/`;
                                const selfSigned = (c.source || """").startsWith(""self-signed"");
                                $(""ad-https"").innerHTML = c.enabled ? `
                                    <p style=""margin: 0 0 6px;""><span class=""ad-badge ok"">On</span> Listening for HTTPS on port <strong>${c.port}</strong> with the ${esc(c.source)}. Open <a href=""${esc(httpsUrl)}"">${esc(httpsUrl)}</a>${location.protocol === ""https:"" ? "" (you are using it now)"" : """"}.</p>
                                    <p style=""margin: 0 0 6px;"">Certificate <strong>${esc(c.subject)}</strong> for ${esc(c.names || """")}, valid until ${esc(String(c.expires).slice(0, 10))}.</p>
                                    <p style=""margin: 0 0 6px;"">${c.redirect ? ""Other devices that open the http:// address are sent to https:// automatically. This computer can still use http://."" : ""http:// stays available from other devices (httpsRedirect=false).""}</p>
                                    ${selfSigned ? `<p class=""ad-note"">A self-signed certificate encrypts the connection, but browsers warn the first time because no public authority vouches for it. Choose Advanced &gt; Continue, or stop the warning by installing the certificate on each device: <a href=""/api/admin/https/certificate.cer"">Download certificate (.cer)</a>. On Windows, open it &gt; Install Certificate &gt; Local Machine &gt; Trusted Root Certification Authorities. On iPhone, open it, install the profile in Settings, then turn it on in Settings &gt; General &gt; About &gt; Certificate Trust Settings. On Android, Settings &gt; Security &gt; Install a certificate &gt; CA certificate.</p>` : """"}`
                                    : `<p style=""margin: 0 0 6px;""><span class=""ad-badge warn"">Off</span> ${esc(c.problem || """")}</p><p class=""ad-note"">Set the HTTPS port above (443 by default, 0 to turn it off) and restart OPTIC.</p>`;
                                $(""ad-https-self"").style.display = h.uploaded ? """" : ""none"";
                                if (h.uploaded && selfSigned) st.textContent = ""Your uploaded certificate will be used after OPTIC restarts."";
                            };
                            $(""ad-https-form"").addEventListener(""submit"", async (e) => {
                                e.preventDefault();
                                try {
                                    const r = await fetch(""/api/admin/https/certificate"", { method: ""POST"", body: new FormData(e.target) });
                                    const j = await r.json();
                                    if (!r.ok) throw new Error(j.error || ""Upload failed"");
                                    e.target.reset();
                                    st.textContent = `Saved certificate for ${j.names || j.subject}, valid until ${String(j.expires).slice(0, 10)}. Restart OPTIC to start using it.`;
                                    load();
                                } catch (err) { st.textContent = ""Not saved: "" + err.message; }
                            });
                            $(""ad-https-self"").addEventListener(""click"", async () => {
                                if (!window.confirm(""Remove the uploaded certificate and use a self-signed one after the next restart?"")) return;
                                await fetch(""/api/admin/https/use-self-signed"", { method: ""POST"" });
                                st.textContent = ""OPTIC will use a self-signed certificate after it restarts."";
                                load();
                            });
                            load().catch(err => $(""ad-https"").textContent = ""Could not load: "" + err.message);
                        })();
                    </script>
                    <div class=""card"" style=""grid-column: 1 / -1;"">
                        <h2>Network access</h2>
                        <div class=""card-content"">
                            <div id=""ad-net""><p class=""ad-note"">Loading...</p></div>
                            <h3 style=""font-size: 14px; margin: 16px 0 6px;"">Why other devices can be blocked</h3>
                            <p class=""ad-note"">Windows Firewall blocks every incoming connection unless a rule allows it, so both the HTTP and HTTPS ports need a rule. That is its default on all network types, and strictest on <strong>Public</strong> networks, which is what Windows picks for a network it does not know. When a program first listens for connections, Windows normally asks whether to allow it; OPTIC runs through <code>dotnet</code> from a terminal, so that prompt is often never shown or was dismissed, and port 80 stays closed to other devices. This computer can always reach itself, so the dashboard works here even while the firewall blocks your phone.</p>
                            <p class=""ad-note"">To let devices on your own network in, open PowerShell as Administrator and run:</p>
                            <code class=""ad-code"" id=""ad-fw-cmd""></code>
                            <p class=""ad-note"" style=""margin-top: 6px;""><button type=""button"" id=""ad-fw-copy"" style=""font-size: 11px; padding: 3px 10px;"">Copy command</button> It allows only addresses on your local network (LocalSubnet), not the internet. If this is your home network, you can also set it to Private in Settings &gt; Network &amp; internet &gt; Ethernet (or Wi-Fi) &gt; Network profile type.</p>
                            <p class=""ad-note""><strong>Internet access:</strong> the dashboard uses plain HTTP, so do not forward a router port to it. Use Tailscale (only your own devices) or a Cloudflare Tunnel (adds HTTPS) instead.</p>
                        </div>
                    </div>
                    <script>
                        (function () {
                            const $ = (id) => document.getElementById(id);
                            const esc = (v) => String(v ?? """").replace(/[&<>""\x27]/g, ch => ""&#"" + ch.charCodeAt(0) + "";"");
                            const api = async (url, body) => {
                                const r = await fetch(url, body === undefined ? {} : { method: ""POST"", headers: { ""Content-Type"": ""application/json"" }, body: JSON.stringify(body) });
                                const j = await r.json();
                                if (!r.ok) throw new Error(j.error || ""Request failed"");
                                return j;
                            };

                            // ----- users
                            const userStatus = $(""ad-user-status"");
                            const loadUsers = async () => {
                                const users = await api(""/api/admin/users"");
                                $(""ad-users"").innerHTML = users.map(u => `<tr>
                                    <td><strong>${esc(u.username)}</strong>${u.isYou ? "" (you)"" : """"}</td>
                                    <td>${u.isAdmin ? `<span class=""ad-badge admin"">Admin</span>` : ""User""}</td>
                                    <td class=""ad-actions"">
                                        <button type=""button"" data-user-pw=""${esc(u.username)}"">Set password</button>
                                        ${u.isYou ? """" : `<button type=""button"" data-user-role=""${esc(u.username)}"" data-admin=""${u.isAdmin ? ""0"" : ""1""}"">${u.isAdmin ? ""Make regular user"" : ""Make admin""}</button>
                                        <button type=""button"" data-user-remove=""${esc(u.username)}"">Remove</button>`}
                                    </td></tr>`).join("""");
                            };
                            $(""ad-user-form"").addEventListener(""submit"", async (e) => {
                                e.preventDefault();
                                const username = $(""ad-user-name"").value.trim();
                                try {
                                    const r = await api(""/api/admin/users"", { username, password: $(""ad-user-pw"").value, isAdmin: $(""ad-user-admin"").checked });
                                    [""ad-user-name"", ""ad-user-pw""].forEach(id => $(id).value = """");
                                    $(""ad-user-admin"").checked = false;
                                    userStatus.textContent = r.created ? `Added ${username}.` : `Updated ${username}.`;
                                    loadUsers();
                                } catch (err) { userStatus.textContent = ""Could not save: "" + err.message; }
                            });
                            $(""ad-users"").addEventListener(""click"", async (e) => {
                                const b = e.target.closest(""button"");
                                if (!b) return;
                                try {
                                    if (b.dataset.userPw) {
                                        const pw = window.prompt(`New password for ${b.dataset.userPw} (8+ characters)`);
                                        if (!pw) return;
                                        const users = await api(""/api/admin/users"");
                                        const u = users.find(x => x.username === b.dataset.userPw);
                                        await api(""/api/admin/users"", { username: b.dataset.userPw, password: pw, isAdmin: !!(u && u.isAdmin) });
                                        userStatus.textContent = `Password changed for ${b.dataset.userPw}.`;
                                    } else if (b.dataset.userRole) {
                                        await api(""/api/admin/users"", { username: b.dataset.userRole, password: """", isAdmin: b.dataset.admin === ""1"" });
                                        userStatus.textContent = `Updated ${b.dataset.userRole}.`;
                                    } else if (b.dataset.userRemove) {
                                        if (!window.confirm(`Remove ${b.dataset.userRemove}? They will no longer be able to sign in. Their saved data is kept and comes back if you add them again.`)) return;
                                        await api(""/api/admin/users/remove"", { username: b.dataset.userRemove });
                                        userStatus.textContent = `Removed ${b.dataset.userRemove}.`;
                                    }
                                    loadUsers();
                                } catch (err) { userStatus.textContent = ""Could not save: "" + err.message; }
                            });
                            loadUsers().catch(err => userStatus.textContent = ""Could not load users: "" + err.message);

                            // ----- optic.conf
                            const HINTS = { httpsPort: ""443 by default; 0 turns HTTPS off"", httpsRedirect: ""true (default) sends other devices from http:// to https://; false keeps both"", addr: ""optio1... used by command-line reports"", tz: ""Windows or IANA id, e.g. America/New_York"", webHost: ""0.0.0.0 = every network, 127.0.0.1 = this computer only"", webPort: ""80 unless something else uses it"" };
                            const loadConfig = async () => {
                                const c = await api(""/api/admin/config"");
                                $(""ad-conf-path"").textContent = c.path;
                                $(""ad-conf-fields"").innerHTML = c.keys.map(k => `<div class=""form-group""><label for=""ad-conf-${k.key}"">${esc(k.label)} <small>(${k.key})</small></label><input type=""text"" id=""ad-conf-${k.key}"" data-conf-key=""${k.key}"" value=""${esc(k.value)}"" spellcheck=""false"" autocomplete=""off"">${HINTS[k.key] ? `<small>${esc(HINTS[k.key])}</small>` : """"}</div>`).join("""");
                            };
                            $(""ad-conf-form"").addEventListener(""submit"", async (e) => {
                                e.preventDefault();
                                const body = {};
                                document.querySelectorAll(""[data-conf-key]"").forEach(i => body[i.dataset.confKey] = i.value);
                                try { await api(""/api/admin/config"", body); $(""ad-conf-status"").textContent = ""Saved.""; loadConfig(); }
                                catch (err) { $(""ad-conf-status"").textContent = ""Not saved: "" + err.message; }
                            });
                            loadConfig().catch(err => $(""ad-conf-status"").textContent = ""Could not load: "" + err.message);

                            // ----- network
                            api(""/api/admin/server"").then(n => {
                                const profiles = n.networkProfiles.length
                                    ? n.networkProfiles.map(p => `${esc(p.adapter)}: ${p.category === ""Public"" ? `<span class=""ad-badge warn"">Public</span>` : `<span class=""ad-badge ok"">${esc(p.category)}</span>`}`).join("" · "")
                                    : ""unknown"";
                                $(""ad-net"").innerHTML = `
                                    <p style=""margin: 0 0 6px;"">${n.allNetworks ? `<span class=""ad-badge ok"">Listening on every network</span>` : `<span class=""ad-badge warn"">This computer only</span>`} Started on <strong>${esc(n.host)}:${n.port}</strong> on ${esc(n.machine)}.</p>
                                    <p style=""margin: 0 0 6px;"">Open it at: ${n.urls.concat(n.httpsUrls).map(u => `<a href=""${esc(u)}"">${esc(u)}</a>`).join("" · "")}</p>
                                    <p style=""margin: 0 0 6px;"">Windows network type: ${profiles}</p>
                                    <p class=""ad-note"" style=""margin: 0;"">Data folder ${esc(n.dataFolder)} · database ${n.databaseMb.toLocaleString(""en-US"")} MB · chain data syncs every ${n.syncMinutes} minutes.${n.allNetworks ? """" : "" To reach it from other devices, set the dashboard address above to 0.0.0.0 and restart OPTIC.""}</p>`;
                                $(""ad-fw-cmd"").textContent = `New-NetFirewallRule -DisplayName ""OPTIC dashboard"" -Direction Inbound -Protocol TCP -LocalPort ${n.httpsPort ? n.port + "","" + n.httpsPort : n.port} -Action Allow -Profile Any -RemoteAddress LocalSubnet`;
                            }).catch(err => $(""ad-net"").textContent = ""Could not load: "" + err.message);
                            $(""ad-fw-copy"").addEventListener(""click"", () => navigator.clipboard.writeText($(""ad-fw-cmd"").textContent).then(() => $(""ad-fw-copy"").textContent = ""Copied""));
                        })();
                    </script>
");
    }

    static void AppendMyWalletsScript(System.Text.StringBuilder sb)
    {
        sb.Append(@"
                    <script>
                        window.opticWallets = window.opticWallets || (function () {
                            const NAME = 'optic_wallets';
                            const U = window.opticUser;
                            const S = window.opticSettings;
                            const readCookie = (name) => {
                                try {
                                    const raw = document.cookie.split('; ').find(c => c.startsWith(name + '='));
                                    return raw ? JSON.parse(decodeURIComponent(raw.slice(name.length + 1))) : null;
                                } catch (e) { return null; }
                            };
                            const dropCookie = (name) => { document.cookie = name + '=; path=/; max-age=0; SameSite=Strict'; };
                            const valid = (a) => /^optio1[02-9ac-hj-np-z]{38,58}$/.test(String(a || ''));
                            const esc = (v) => String(v ?? '').replace(/[&<>\x22\x27]/g, ch => '&#' + ch.charCodeAt(0) + ';');
                            const short = (a) => a.length > 20 ? a.slice(0, 12) + '...' + a.slice(-6) : a;

                            // Stored on the server per user as [{ a: address, l: label, p: isPrimary }].
                            const list = () => (Array.isArray(U.wallets) ? U.wallets : []).filter(w => w && valid(w.a)).map(w => ({ ...w }));
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
                                U.wallets = clean;
                                U.hasWallets = true;
                                S.post('/api/me/wallets', clean).catch(e => window.alert('Could not save your wallets: ' + e.message));
                                return clean;
                            };
                            const find = (a) => list().find(w => w.a === String(a || '').toLowerCase()) || null;

                            // Exchange deposit addresses as [{ a: address, n: name, m: the user's deposit memo }].
                            // The address list is system-wide (admins edit it); each user's memos are in their own settings.
                            const EX_NAME = 'optic_exchanges';
                            const memos = () => (S.get().exchangeMemos || {});
                            const exchanges = () => (Array.isArray(U.exchanges) ? U.exchanges : [])
                                .filter(x => x && valid(x.a)).map(x => ({ a: x.a, n: x.n, m: memos()[x.a] || '' }));
                            const saveExchanges = (items) => {
                                const seen = new Set();
                                const clean = [];
                                for (const x of items) {
                                    const a = String(x.a || '').trim().toLowerCase();
                                    if (!valid(a) || seen.has(a)) continue;
                                    seen.add(a);
                                    clean.push({ a, n: String(x.n || '').trim().slice(0, 30) || 'Exchange', m: String(x.m || '').trim().slice(0, 64) });
                                }
                                const nextMemos = {};
                                for (const x of clean) if (x.m) nextMemos[x.a] = x.m;
                                const saves = [S.set('exchangeMemos', nextMemos)];
                                if (U.isAdmin) {
                                    U.exchanges = clean.map(x => ({ a: x.a, n: x.n }));
                                    U.hasExchanges = true;
                                    saves.push(S.post('/api/admin/exchanges', U.exchanges));
                                }
                                return Promise.all(saves).then(() => clean);
                            };

                            // One-time move of what the admin had saved in this browser (cookies) to their account.
                            if (U.isAdmin) {
                                const oldWallets = readCookie(NAME);
                                if (Array.isArray(oldWallets) && oldWallets.length && !U.hasWallets) save(oldWallets);
                                if (Array.isArray(oldWallets) && U.hasWallets) dropCookie(NAME);
                                const oldExchanges = readCookie(EX_NAME);
                                if (Array.isArray(oldExchanges)) {
                                    if (!U.hasExchanges) saveExchanges(oldExchanges).then(() => dropCookie(EX_NAME)).catch(() => { });
                                    else dropCookie(EX_NAME);
                                }
                            }
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

    static void AppendMarketLiquidityCard(System.Text.StringBuilder sb)
    {
        sb.Append(@"
                    <div class=""card"" style=""grid-column: 1 / -1;"">
                        <h2>OPT Market Liquidity</h2>
                        <div class=""card-content"">
                            <div style=""color: var(--text-secondary); font-size: 13px; margin-bottom: 12px;"">How much OPT traded each day on exchanges and in the Osmosis pools, and from that how much could reasonably have been sold per day without swamping the market. Use it to support a discount on the quoted price when valuing more OPT than the market could absorb.</div>
                            <form id=""ml-form"" style=""display: flex; gap: 8px; flex-wrap: wrap; align-items: flex-end; margin-bottom: 12px;"">
                                <div class=""form-group"" style=""flex: 0 0 120px; margin-bottom: 0;"">
                                    <label for=""ml-year"">Year</label>
                                    <select id=""ml-year""></select>
                                </div>
                                <div class=""form-group"" style=""flex: 0 0 360px; margin-bottom: 0;"">
                                    <label for=""ml-exchange"">Exchange volume</label>
                                    <select id=""ml-exchange"">
                                        <option value=""cmc"">All exchanges (CoinMarketCap) or Coinstore, whichever is higher</option>
                                        <option value=""coinstore"">Coinstore only</option>
                                        <option value=""none"">Leave out exchanges</option>
                                    </select>
                                </div>
                                <div class=""form-group"" style=""flex: 0 0 250px; margin-bottom: 0;"">
                                    <label for=""ml-osmosis"">Osmosis volume</label>
                                    <select id=""ml-osmosis"">
                                        <option value=""net"">Net per swap (no arbitrage)</option>
                                        <option value=""gross"">Gross (every pool leg)</option>
                                        <option value=""none"">Leave out Osmosis</option>
                                    </select>
                                </div>
                                <div class=""form-group"" style=""flex: 0 0 150px; margin-bottom: 0;"">
                                    <label for=""ml-share"">Share you could sell</label>
                                    <select id=""ml-share"">
                                        <option value=""0.05"">5% of volume</option>
                                        <option value=""0.1"" selected>10% of volume</option>
                                        <option value=""0.15"">15% of volume</option>
                                        <option value=""0.2"">20% of volume</option>
                                        <option value=""0.25"">25% of volume</option>
                                        <option value=""0.5"">50% of volume</option>
                                        <option value=""1"">100% of volume</option>
                                    </select>
                                </div>
                                <div class=""form-group"" style=""flex: 0 0 170px; margin-bottom: 0;"">
                                    <label for=""ml-window"">Volume averaged over</label>
                                    <select id=""ml-window"">
                                        <option value=""1"">That day only</option>
                                        <option value=""7"">Trailing 7 days</option>
                                        <option value=""30"" selected>Trailing 30 days</option>
                                        <option value=""90"">Trailing 90 days</option>
                                    </select>
                                </div>
                                <div class=""form-group"" style=""flex: 0 0 170px; margin-bottom: 0;"">
                                    <label for=""ml-holding"">OPT to sell (optional)</label>
                                    <input type=""number"" id=""ml-holding"" min=""0"" step=""any"" placeholder=""e.g. 5000000"">
                                </div>
                                <button type=""submit"" id=""ml-submit"">Refresh</button>
                                <a id=""ml-csv"" class=""header-link"" href=""#"" hidden>Download CSV</a>
                            </form>
                            <div id=""ml-status"" style=""color: var(--text-secondary); font-size: 13px; margin-bottom: 8px;""></div>
                            <div id=""ml-results"" hidden>
                                <div id=""ml-summary"" style=""display: grid; grid-template-columns: repeat(auto-fit, minmax(190px, 1fr)); gap: 8px; margin-bottom: 16px;""></div>

                                <h3 style=""margin: 0 0 4px; font-size: 15px;"">Selling into the market today</h3>
                                <div id=""ml-now"" style=""color: var(--text-secondary); font-size: 13px; margin-bottom: 8px;""></div>
                                <div class=""data-table-wrap pnl-table-wrap"" style=""margin-bottom: 16px;"">
                                    <table class=""data-table"">
                                        <thead>
                                            <tr>
                                                <th>Sell on Osmosis (OPT)</th>
                                                <th style=""text-align: right;"">You receive (USDC)</th>
                                                <th style=""text-align: right;"">Avg price</th>
                                                <th style=""text-align: right;"">Below spot</th>
                                                <th style=""text-align: left;"">Route (pool ids)</th>
                                            </tr>
                                        </thead>
                                        <tbody id=""ml-quotes-body""></tbody>
                                    </table>
                                </div>
                                <div class=""data-table-wrap pnl-table-wrap"" style=""margin-bottom: 16px;"">
                                    <table class=""data-table"">
                                        <thead>
                                            <tr>
                                                <th>Osmosis pool</th>
                                                <th>Pair</th>
                                                <th style=""text-align: right;"">Liquidity (USD)</th>
                                                <th style=""text-align: right;"">Volume 24h (USD)</th>
                                                <th style=""text-align: right;"">Volume 7d (USD)</th>
                                            </tr>
                                        </thead>
                                        <tbody id=""ml-pools-body""></tbody>
                                    </table>
                                </div>

                                <h3 style=""margin: 0 0 8px; font-size: 15px;"">Daily volume and sellable amount</h3>
                                <div class=""data-table-wrap pnl-table-wrap"" style=""max-height: 640px;"">
                                    <table class=""data-table"">
                                        <thead>
                                            <tr>
                                                <th>Date (UTC)</th>
                                                <th style=""text-align: right;"">Price (USD)</th>
                                                <th style=""text-align: right;"">Exchange volume (OPT)</th>
                                                <th style=""text-align: left;"">Exchange source</th>
                                                <th style=""text-align: right;"">Osmosis sold (OPT)</th>
                                                <th style=""text-align: right;"">Osmosis bought (OPT)</th>
                                                <th style=""text-align: right;"">Total volume (OPT)</th>
                                                <th style=""text-align: right;"">Total volume (USD)</th>
                                                <th style=""text-align: right;"">Avg volume (OPT)</th>
                                                <th style=""text-align: right;"">Sellable (OPT)</th>
                                                <th style=""text-align: right;"">Sellable (USD)</th>
                                            </tr>
                                        </thead>
                                        <tbody id=""ml-days-body""></tbody>
                                    </table>
                                </div>
                                <div id=""ml-notes"" style=""color: var(--text-secondary); font-size: 12px; margin-top: 12px;""></div>
                            </div>
                        </div>
                    </div>
                    <script>
                        (() => {
                            const $ = (id) => document.getElementById(id);
                            const form = $('ml-form'), submit = $('ml-submit'), status = $('ml-status'), results = $('ml-results');
                            const yearSelect = $('ml-year'), exchangeSelect = $('ml-exchange'), osmosisSelect = $('ml-osmosis');
                            const shareSelect = $('ml-share'), windowSelect = $('ml-window'), holdingInput = $('ml-holding'), csvLink = $('ml-csv');
                            const esc = (v) => String(v ?? '').replace(/[&<>\x22\x27]/g, ch => '&#' + ch.charCodeAt(0) + ';');
                            const fmtOpt = (v) => v == null ? '-' : Number(v).toLocaleString('en-US', { maximumFractionDigits: 0 });
                            const fmtUsd = (v) => v == null ? '-' : '$' + Number(v).toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
                            const fmtPrice = (v) => v == null ? '-' : '$' + Number(v).toLocaleString('en-US', { minimumFractionDigits: 6, maximumFractionDigits: 6 });
                            const fmtPct = (v) => v == null ? '-' : (v * 100).toLocaleString('en-US', { maximumFractionDigits: 1 }) + '%';
                            const fmtDays = (v) => v == null || !isFinite(v) ? 'never at this rate' : v < 1 ? 'under a day' : Math.ceil(v).toLocaleString('en-US') + ' days';
                            const tile = (label, value, sub) => `<div class=""metric-card""><div style=""color: var(--text-secondary); font-size: 12px;"">${label}</div><div style=""font-size: 18px; font-weight: 600; margin-top: 4px;"">${value}</div>${sub ? `<div style=""font-size: 12px; color: var(--text-secondary); margin-top: 2px;"">${sub}</div>` : ''}</div>`;
                            const median = (list) => {
                                if (!list.length) return null;
                                const a = list.slice().sort((x, y) => x - y), m = Math.floor(a.length / 2);
                                return a.length % 2 ? a[m] : (a[m - 1] + a[m]) / 2;
                            };

                            let loaded = null;
                            let rows = [];

                            // Restore choices from the link.
                            const params = new URLSearchParams(location.search);
                            const pick = (el, key) => { const v = params.get(key); if (v && [...el.options].some(o => o.value === v)) el.value = v; };
                            pick(exchangeSelect, 'exchange'); pick(osmosisSelect, 'osmosis'); pick(shareSelect, 'share'); pick(windowSelect, 'window');
                            if (params.get('holding')) holdingInput.value = params.get('holding');

                            const updateUrl = () => {
                                const q = new URLSearchParams();
                                if (yearSelect.value) q.set('year', yearSelect.value);
                                if (exchangeSelect.value !== 'cmc') q.set('exchange', exchangeSelect.value);
                                if (osmosisSelect.value !== 'net') q.set('osmosis', osmosisSelect.value);
                                if (shareSelect.value !== '0.1') q.set('share', shareSelect.value);
                                if (windowSelect.value !== '30') q.set('window', windowSelect.value);
                                if (holdingInput.value) q.set('holding', holdingInput.value);
                                const qs = q.toString();
                                history.replaceState(null, '', qs ? '?' + qs : location.pathname);
                            };

                            // Works out each day's volume, price and sellable amount over the whole history, so
                            // trailing averages at the start of a year still look back into the previous one.
                            const compute = () => {
                                const share = Number(shareSelect.value), win = Number(windowSelect.value);
                                const out = [];
                                let lastPrice = null;
                                for (const d of loaded.days) {
                                    let price = d.cmcClose ?? d.csClose ?? d.osPrice, priceSource = d.cmcClose != null ? 'CoinMarketCap' : d.csClose != null ? 'Coinstore' : d.osPrice != null ? 'Osmosis' : null;
                                    if (price == null && lastPrice != null) { price = lastPrice; priceSource = 'carried forward'; }
                                    if (price != null) lastPrice = price;

                                    let exOpt = null, exSource = '';
                                    const cmcOpt = d.cmcVolumeUsd != null && d.cmcClose ? d.cmcVolumeUsd / d.cmcClose : null;
                                    if (exchangeSelect.value === 'cmc' && cmcOpt != null && cmcOpt >= (d.csVolumeOpt || 0)) { exOpt = cmcOpt; exSource = 'All exchanges (CMC)'; }
                                    else if (exchangeSelect.value !== 'none' && d.csVolumeOpt != null) { exOpt = d.csVolumeOpt; exSource = 'Coinstore'; }
                                    else if (exchangeSelect.value !== 'none') { exOpt = 0; exSource = d.date > (loaded.sources.coinstoreTo || '') ? 'No exchange data' : 'Coinstore'; }

                                    const osOpt = osmosisSelect.value === 'net' ? d.osSoldOpt + d.osBoughtOpt : osmosisSelect.value === 'gross' ? d.osGrossOpt : 0;
                                    const total = (exOpt || 0) + osOpt;
                                    out.push({ d, price, priceSource, exOpt, exSource, osOpt, total, totalUsd: price != null ? total * price : null });
                                }
                                for (let i = 0; i < out.length; i++) {
                                    const from = Math.max(0, i - win + 1);
                                    let sum = 0;
                                    for (let j = from; j <= i; j++) sum += out[j].total;
                                    const r = out[i];
                                    r.avg = sum / (i - from + 1);
                                    r.sellable = r.avg * share;
                                    r.sellableUsd = r.price != null ? r.sellable * r.price : null;
                                }
                                return out;
                            };

                            const render = () => {
                                updateUrl();
                                const all = compute();
                                const year = yearSelect.value;
                                rows = all.filter(r => !year || r.d.date.startsWith(year));
                                const snap = loaded.snapshot || {};
                                const holding = Number(holdingInput.value) || 0;
                                const share = Number(shareSelect.value);

                                // Summary
                                const n = rows.length || 1;
                                const avgVol = rows.reduce((s, r) => s + r.total, 0) / n;
                                const avgUsd = rows.reduce((s, r) => s + (r.totalUsd || 0), 0) / n;
                                const medVol = median(rows.map(r => r.total));
                                const avgSell = rows.reduce((s, r) => s + r.sellable, 0) / n;
                                const avgSellUsd = rows.reduce((s, r) => s + (r.sellableUsd || 0), 0) / n;
                                const quiet = rows.filter(r => r.total <= 0).length;
                                const last30 = all.slice(-30);
                                const last30Vol = last30.reduce((s, r) => s + r.total, 0) / (last30.length || 1);
                                const label = year || 'all years';
                                const liquidity = (snap.pools || []).reduce((s, p) => s + (p.liquidityUsd || 0), 0);
                                const cs = snap.coinstore;

                                let html =
                                    tile('Average daily volume', fmtOpt(avgVol) + ' OPT', `${fmtUsd(avgUsd)} a day · ${label}`) +
                                    tile('Median daily volume', fmtOpt(medVol) + ' OPT', `half of days traded less · ${quiet.toLocaleString('en-US')} day(s) with none`) +
                                    tile(`Sellable per day (${fmtPct(share)})`, fmtOpt(avgSell) + ' OPT', `${fmtUsd(avgSellUsd)} a day on average · ${label}`) +
                                    tile('Last 30 days', fmtOpt(last30Vol) + ' OPT a day', `sellable ${fmtOpt(last30Vol * share)} OPT a day`) +
                                    tile('Osmosis liquidity now', fmtUsd(liquidity), `spot ${fmtPrice(snap.osmosisSpotPriceUsd)} · ${(snap.pools || []).filter(p => p.liquidityUsd > 1).length} pool(s) over $1`) +
                                    tile('Coinstore order book now', cs ? (cs.bestBid != null ? `bid ${fmtPrice(cs.bestBid)}` : 'No buy orders') : '-',
                                        cs ? `${fmtOpt(cs.bidDepthOpt)} OPT bid ($${cs.bidDepthUsdt.toFixed(2)}) · last trade ${cs.lastTradeUtc ? String(cs.lastTradeUtc).slice(0, 10) : '-'}` : 'unavailable');
                                if (holding > 0) {
                                    html += tile(`Time to sell ${fmtOpt(holding)} OPT`, fmtDays(avgSell > 0 ? holding / avgSell : Infinity), `at ${label} average · ${fmtDays(last30Vol * share > 0 ? holding / (last30Vol * share) : Infinity)} at the last-30-day rate`);
                                }
                                $('ml-summary').innerHTML = html;

                                // Selling now
                                const spot = snap.osmosisSpotPriceUsd;
                                $('ml-now').innerHTML = `Osmosis router quotes for selling OPT for USDC right now (fetched ${esc(String(snap.fetchedUtc || '').replace('T', ' ').slice(0, 16))} UTC). A blank row means the pools can't absorb that size at all.`;
                                $('ml-quotes-body').innerHTML = (snap.sellQuotes || []).map(q => `<tr>
                                    <td>${fmtOpt(q.sellOpt)}</td>
                                    <td class=""amount-cell"" style=""text-align: right;"">${fmtUsd(q.receiveUsd)}</td>
                                    <td class=""amount-cell"">${fmtPrice(q.avgPriceUsd)}</td>
                                    <td class=""amount-cell"">${q.avgPriceUsd != null && spot ? fmtPct(1 - q.avgPriceUsd / spot) : 'no route'}</td>
                                    <td style=""text-align: left;"">${esc(q.route || '')}</td>
                                </tr>`).join('') || '<tr><td colspan=""5"" style=""text-align: center; color: var(--text-secondary);"">Quotes unavailable</td></tr>';
                                $('ml-pools-body').innerHTML = (snap.pools || []).map(p => `<tr>
                                    <td>${esc(p.poolId)}</td>
                                    <td>${esc(p.pair)}</td>
                                    <td class=""amount-cell"" title=""${p.liquidityUsd ? '' : 'Numia has no USD value for this pool (it can\x27t price every paired token)'}"">${p.liquidityUsd ? fmtUsd(p.liquidityUsd) : 'not priced'}</td>
                                    <td class=""amount-cell"">${fmtUsd(p.volume24hUsd)}</td>
                                    <td class=""amount-cell"">${fmtUsd(p.volume7dUsd)}</td>
                                </tr>`).join('') || '<tr><td colspan=""5"" style=""text-align: center; color: var(--text-secondary);"">Pool data unavailable</td></tr>';

                                // Daily table, newest first
                                $('ml-days-body').innerHTML = rows.slice().reverse().map(r => `<tr>
                                    <td>${r.d.date}</td>
                                    <td class=""amount-cell"" style=""text-align: right;"" title=""${esc(r.priceSource || 'No price')}"">${fmtPrice(r.price)}${r.priceSource && r.priceSource !== 'CoinMarketCap' ? '*' : ''}</td>
                                    <td class=""amount-cell"">${r.exOpt == null ? '-' : fmtOpt(r.exOpt)}</td>
                                    <td style=""text-align: left; color: var(--text-secondary);"">${esc(r.exSource)}</td>
                                    <td class=""amount-cell"">${fmtOpt(r.d.osSoldOpt)}</td>
                                    <td class=""amount-cell"">${fmtOpt(r.d.osBoughtOpt)}</td>
                                    <td class=""amount-cell"">${fmtOpt(r.total)}</td>
                                    <td class=""amount-cell"">${fmtUsd(r.totalUsd)}</td>
                                    <td class=""amount-cell"">${fmtOpt(r.avg)}</td>
                                    <td class=""amount-cell"" style=""font-weight: 600;"">${fmtOpt(r.sellable)}</td>
                                    <td class=""amount-cell"">${fmtUsd(r.sellableUsd)}</td>
                                </tr>`).join('');

                                const src = loaded.sources;
                                const day = (v) => v ? String(v).slice(0, 10) : '-';
                                const notes = [
                                    `Sources: CoinMarketCap all-exchange daily volume and close (${day(src.cmcFrom)} to ${day(src.cmcTo)}; CoinMarketCap stopped tracking OPT after that). Coinstore OPT/USDT daily candles from its public API (trading ${day(src.coinstoreFrom)} to ${day(src.coinstoreTo)}). Osmosis: every swap in the OPT pools, read from the Osmosis archive node (${(src.osmosisTxs || 0).toLocaleString('en-US')} transactions, ${day(src.osmosisFrom)} to ${day(src.osmosisTo)}). MEXC no longer lists OPT and its history isn't available, but its volume is inside the CoinMarketCap figure while CoinMarketCap tracked OPT.`,
                                    'CoinMarketCap volume already includes Coinstore, so the two are never added together: each day uses whichever is higher (on some days Coinstore alone reported more than CoinMarketCap\x27s total). Osmosis is added on top. Exchange volume in OPT = CoinMarketCap USD volume ÷ that day\x27s close.',
                                    'Osmosis pool liquidity and 24h/7d volume come from Numia, which can\x27t value every paired token: pool 3565 (OPT / SOVR) shows as not priced even though the router sells through it. The sell quotes above are the better guide to what the pools can absorb.',
                                    'Osmosis net volume counts each transaction once by how much OPT it actually bought or sold overall, so arbitrage that buys OPT in one pool and sells it in another in the same transaction cancels out. Gross counts every pool leg.',
                                    `Sellable per day = ${fmtPct(share)} of the average daily volume over the chosen window (ending that day). Selling a larger share than this would move the price; 10–25% is a common assumption when valuing a block of thinly traded stock. Exchange volume for small tokens is often inflated by wash trading, so treat it as an upper bound.`,
                                    '* Price not from CoinMarketCap (Coinstore close, Osmosis swap price, or carried forward from the last known price). All dates are UTC. This is an estimate to support a valuation, not tax advice.'
                                ];
                                if (snap.errors && snap.errors.length) notes.push('Unavailable right now: ' + esc(snap.errors.join(' ')));
                                $('ml-notes').innerHTML = notes.map(t => `<p style=""margin: 0 0 4px;"">${t}</p>`).join('');

                                results.hidden = false;
                                csvLink.hidden = false;
                                status.textContent = `${rows.length.toLocaleString('en-US')} day(s) shown.`;
                            };

                            const run = async () => {
                                const done = window.opticBusy(submit, status, 'Reading exchange and Osmosis history');
                                status.textContent = 'Loading. The first run reads every Osmosis swap from the archive node and can take a few minutes; later runs only fetch new swaps.';
                                try {
                                    const response = await fetch('/api/market/liquidity');
                                    const data = await response.json();
                                    if (!response.ok) throw new Error(data.error || 'Request failed');
                                    loaded = data;
                                    const years = [...new Set(data.days.map(d => d.date.slice(0, 4)))].sort().reverse();
                                    const wanted = yearSelect.value || params.get('year') || '';
                                    yearSelect.innerHTML = '<option value="""">All years</option>' + years.map(y => `<option value=""${y}"">${y}</option>`).join('');
                                    if (years.includes(wanted)) yearSelect.value = wanted;
                                    render();
                                } catch (error) {
                                    status.textContent = 'Error: ' + error.message;
                                } finally {
                                    done();
                                }
                            };

                            form.addEventListener('submit', (e) => { e.preventDefault(); run(); });
                            for (const el of [yearSelect, exchangeSelect, osmosisSelect, shareSelect, windowSelect])
                                el.addEventListener('change', () => { if (loaded) render(); });
                            holdingInput.addEventListener('input', () => { if (loaded) render(); });

                            csvLink.addEventListener('click', (e) => {
                                e.preventDefault();
                                if (!loaded) return;
                                const cell = (v) => v == null ? '' : String(v);
                                const lines = [['date_utc', 'price_usd', 'price_source', 'exchange_volume_opt', 'exchange_source', 'coinstore_volume_opt', 'cmc_volume_usd', 'osmosis_sold_opt', 'osmosis_bought_opt', 'osmosis_gross_opt', 'osmosis_txs', 'total_volume_opt', 'total_volume_usd', 'avg_volume_opt', 'sellable_opt', 'sellable_usd'].join(',')];
                                for (const r of rows)
                                    lines.push([r.d.date, r.price, r.priceSource, r.exOpt, r.exSource, r.d.csVolumeOpt, r.d.cmcVolumeUsd, r.d.osSoldOpt, r.d.osBoughtOpt, r.d.osGrossOpt, r.d.osTxs, r.total, r.totalUsd, r.avg, r.sellable, r.sellableUsd].map(cell).join(','));
                                const a = document.createElement('a');
                                a.href = URL.createObjectURL(new Blob([lines.join('\n')], { type: 'text/csv' }));
                                a.download = `opt-market-liquidity${yearSelect.value ? '-' + yearSelect.value : ''}.csv`;
                                a.click();
                                setTimeout(() => URL.revokeObjectURL(a.href), 1000);
                            });

                            run();
                        })();
                    </script>");
    }

    static void AppendLiquidityEvidenceCard(System.Text.StringBuilder sb)
    {
        AppendMyWalletsScript(sb);
        AppendMarketMathScript(sb);
        sb.Append(@"
                    <style>
                        .le-section { margin: 0 0 22px; break-inside: avoid; }
                        .le-section h3 { margin: 0 0 6px; font-size: 16px; }
                        .le-lede { color: var(--text-secondary); font-size: 13px; margin: 0 0 10px; max-width: 980px; }
                        .le-findings { margin: 0; padding-left: 20px; font-size: 14px; line-height: 1.55; max-width: 1040px; }
                        .le-findings li { margin-bottom: 8px; }
                        .le-chart { position: relative; border: 1px solid var(--border); border-radius: 8px; background: var(--surface-light); padding: 8px 8px 4px; }
                        .le-chart svg { display: block; width: 100%; height: auto; }
                        .le-legend { display: flex; gap: 16px; flex-wrap: wrap; font-size: 12px; color: var(--text-secondary); margin: 6px 0 0; }
                        .le-legend span { display: inline-flex; align-items: center; gap: 6px; }
                        .le-legend i { display: inline-block; width: 12px; height: 12px; border-radius: 3px; }
                        .le-tip { position: absolute; pointer-events: none; background: var(--surface); border: 1px solid var(--border-strong); border-radius: 6px; padding: 6px 8px; font-size: 12px; color: var(--text-primary); box-shadow: 0 4px 14px rgba(0,0,0,0.18); opacity: 0; white-space: nowrap; z-index: 2; }
                        .le-hash { font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace; font-size: 11px; word-break: break-all; }
                        .le-print-only { display: none; }
                        @media print {
                            .top-nav, #le-controls, .le-no-print, .modal { display: none !important; }
                            .le-print-only { display: block; }
                            body, .main-content, .card { background: #fff !important; box-shadow: none !important; }
                            .card { border: none !important; }
                            .data-table-wrap { max-height: none !important; overflow: visible !important; }
                            .le-section { break-inside: avoid; }
                            a { color: inherit; text-decoration: none; }
                        }
                    </style>
                    <div class=""card"" style=""grid-column: 1 / -1;"">
                        <h2>Evidence: OPT Could Not Be Sold at the Quoted Price</h2>
                        <div class=""card-content"">
                            <div id=""le-controls"">
                                <form id=""le-form"" style=""display: flex; gap: 8px; flex-wrap: wrap; align-items: flex-end; margin-bottom: 8px;"">
                                    <div class=""form-group"" style=""flex: 1 1 380px; margin-bottom: 0;"">
                                        <label for=""le-address"">Your wallet (distributions received)</label>
                                        <input type=""text"" id=""le-address"" placeholder=""optio1..."" autocomplete=""off"" spellcheck=""false"">
                                    </div>
                                    <div class=""form-group"" style=""flex: 0 0 120px; margin-bottom: 0;"">
                                        <label for=""le-year"">Tax year</label>
                                        <select id=""le-year""></select>
                                    </div>
                                    <div class=""form-group"" style=""flex: 0 0 300px; margin-bottom: 0;"">
                                        <label for=""le-basis"">Exchange volume to trust</label>
                                        <select id=""le-basis"">
                                            <option value=""withdrawals"">OPT actually withdrawn from Coinstore (on-chain)</option>
                                            <option value=""reported"">Volume the exchanges reported</option>
                                        </select>
                                    </div>
                                    <div class=""form-group"" style=""flex: 0 0 150px; margin-bottom: 0;"">
                                        <label for=""le-share"">Share you could sell</label>
                                        <select id=""le-share"">
                                            <option value=""0.1"" selected>10% of volume</option>
                                            <option value=""0.2"">20% of volume</option>
                                            <option value=""0.25"">25% of volume</option>
                                            <option value=""0.5"">50% of volume</option>
                                            <option value=""1"">100% of volume</option>
                                        </select>
                                    </div>
                                    <button type=""submit"" id=""le-submit"">Build report</button>
                                    <button type=""button"" id=""le-print"" class=""le-no-print"" disabled>Print / Save as PDF</button>
                                </form>
                                <div id=""le-wallets"" style=""font-size: 13px; color: var(--text-secondary); margin: 0 0 8px; display: flex; gap: 6px; flex-wrap: wrap; align-items: center;""></div>
                                <div id=""le-status"" style=""color: var(--text-secondary); font-size: 13px; margin-bottom: 12px;"">Builds a printable report from the Optio chain, Coinstore, CoinMarketCap, CoinGecko, MEXC and Osmosis. The first run can take a few minutes.</div>
                            </div>
                            <div id=""le-report"" hidden>
                                <div class=""le-print-only"" id=""le-print-header"" style=""font-size: 12px; color: var(--text-secondary); margin-bottom: 12px;""></div>

                                <div class=""le-section"">
                                    <h3>Findings</h3>
                                    <ol class=""le-findings"" id=""le-findings""></ol>
                                </div>

                                <div class=""le-section"">
                                    <h3>1. Coinstore reported far more trading than OPT ever reached it</h3>
                                    <p class=""le-lede"">Every OPT deposit to Coinstore had to be sent on the Optio chain to its deposit address, and every withdrawal came back out on chain, so both can be counted exactly. Trading volume is what Coinstore itself reported. Volume many times larger than the OPT deposited can't have been customers selling OPT they brought in.</p>
                                    <div class=""le-chart"" id=""le-chart-monthly""></div>
                                    <div class=""le-legend"" id=""le-legend-monthly""></div>
                                </div>

                                <div class=""le-section"">
                                    <h3>2. When OPT could move in and out of Coinstore</h3>
                                    <p class=""le-lede"">Each mark is a day with activity. Shaded stretches are when Coinstore was closed to the public: fewer than 5 different accounts deposited OPT within a week either side. During those periods the public couldn\x27t bring OPT to the exchange to sell it.</p>
                                    <div class=""le-chart"" id=""le-chart-timeline""></div>
                                </div>

                                <div class=""le-section"">
                                    <h3>3. What you received against what the market could absorb</h3>
                                    <p class=""le-lede"" id=""le-absorb-lede""></p>
                                    <div class=""le-chart"" id=""le-chart-absorb""></div>
                                    <div class=""le-legend"" id=""le-legend-absorb""></div>
                                </div>

                                <div class=""le-section"">
                                    <h3>Month by month</h3>
                                    <div class=""data-table-wrap pnl-table-wrap"">
                                        <table class=""data-table"">
                                            <thead>
                                                <tr>
                                                    <th>Month</th>
                                                    <th style=""text-align: right;"">Coinstore reported traded (OPT)</th>
                                                    <th style=""text-align: right;"">Deposited to Coinstore (OPT)</th>
                                                    <th style=""text-align: right;"">Withdrawn from Coinstore (OPT)</th>
                                                    <th style=""text-align: right;"">Reported ÷ deposited</th>
                                                    <th style=""text-align: right;"">Days with no deposits</th>
                                                    <th style=""text-align: right;"">Accounts depositing</th>
                                                    <th style=""text-align: right;"">Osmosis net (OPT)</th>
                                                    <th style=""text-align: right;"">Market could absorb (OPT)</th>
                                                    <th style=""text-align: right;"">You received (OPT)</th>
                                                    <th style=""text-align: right;"">You ÷ absorbable</th>
                                                </tr>
                                            </thead>
                                            <tbody id=""le-months-body""></tbody>
                                        </table>
                                    </div>
                                </div>

                                <div class=""le-section"">
                                    <h3>When Coinstore was closed to the public</h3>
                                    <p class=""le-lede"">Days when fewer than 5 different Coinstore accounts deposited OPT within a week either side. Deposits in these periods came from a handful of accounts, often one, so the public couldn\x27t bring OPT to the exchange to sell.</p>
                                    <div class=""data-table-wrap pnl-table-wrap"">
                                        <table class=""data-table"">
                                            <thead>
                                                <tr>
                                                    <th>First day</th>
                                                    <th>Last day</th>
                                                    <th style=""text-align: right;"">Days</th>
                                                    <th style=""text-align: right;"">Deposits</th>
                                                    <th style=""text-align: right;"">Accounts depositing</th>
                                                    <th style=""text-align: right;"">Coinstore reported traded meanwhile (OPT)</th>
                                                    <th style=""text-align: right;"">You received meanwhile (OPT)</th>
                                                </tr>
                                            </thead>
                                            <tbody id=""le-gaps-body""></tbody>
                                        </table>
                                    </div>
                                </div>

                                <div class=""le-section"">
                                    <h3>The market today</h3>
                                    <div class=""data-table-wrap pnl-table-wrap"">
                                        <table class=""data-table"">
                                            <thead><tr><th>Check</th><th style=""text-align: left;"">Result</th><th style=""text-align: left;"">Source</th></tr></thead>
                                            <tbody id=""le-today-body""></tbody>
                                        </table>
                                    </div>
                                </div>

                                <div class=""le-section"">
                                    <h3>Saved evidence</h3>
                                    <p class=""le-lede"">The unmodified responses from each public source, saved with the time fetched and a SHA-256 fingerprint so anyone can confirm they haven't been changed. Capture again on any date to record the market as it is then.</p>
                                    <div class=""le-no-print"" style=""display: flex; gap: 12px; align-items: center; margin-bottom: 8px;"">
                                        <button type=""button"" id=""le-capture"">Capture evidence now</button>
                                        <a id=""le-zip"" class=""header-link"" href=""#"" hidden>Download evidence (.zip)</a>
                                    </div>
                                    <div id=""le-evidence""></div>
                                </div>

                                <div class=""le-section"">
                                    <h3>Sources and method</h3>
                                    <div id=""le-method"" style=""color: var(--text-secondary); font-size: 12px; max-width: 1040px;""></div>
                                </div>
                            </div>
                        </div>
                    </div>
                    <script>
                        (() => {
                            const $ = (id) => document.getElementById(id);
                            const COLORS = { reported: '#0078d4', deposited: '#c05a00', withdrawn: '#8764b8', you: '#c05a00', absorb: '#0078d4', osmosis: '#8764b8' };
                            const esc = (v) => String(v ?? '').replace(/[&<>\x22\x27]/g, ch => '&#' + ch.charCodeAt(0) + ';');
                            const fmtOpt = (v) => v == null ? '-' : Number(v).toLocaleString('en-US', { maximumFractionDigits: 0 });
                            const fmtBig = (v) => v == null ? '-' : Math.abs(v) >= 1e9 ? (v / 1e9).toFixed(2) + 'B' : Math.abs(v) >= 1e6 ? (v / 1e6).toFixed(1) + 'M' : Math.abs(v) >= 1e3 ? (v / 1e3).toFixed(0) + 'k' : Math.round(v).toString();
                            const fmtUsd = (v) => v == null ? '-' : '$' + Number(v).toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
                            const fmtPrice = (v) => v == null ? '-' : '$' + Number(v).toLocaleString('en-US', { minimumFractionDigits: 6, maximumFractionDigits: 6 });
                            const fmtPct = (v) => v == null || !isFinite(v) ? '-' : (v * 100).toLocaleString('en-US', { maximumFractionDigits: v < 0.1 ? 1 : 0 }) + '%';
                            const fmtX = (v) => v == null || !isFinite(v) ? '-' : v.toLocaleString('en-US', { maximumFractionDigits: v < 10 ? 1 : 0 }) + '×';
                            const fmtDate = (d) => d ? new Date(String(d).slice(0, 10) + 'T00:00:00Z').toLocaleDateString('en-US', { year: 'numeric', month: 'long', day: 'numeric', timeZone: 'UTC' }) : '-';
                            const fmtMonth = (m) => new Date(m + '-01T00:00:00Z').toLocaleDateString('en-US', { year: 'numeric', month: 'short', timeZone: 'UTC' });
                            const addDays = (d, n) => { const t = new Date(d + 'T00:00:00Z'); t.setUTCDate(t.getUTCDate() + n); return t.toISOString().slice(0, 10); };
                            const dayDiff = (a, b) => Math.round((Date.parse(b + 'T00:00:00Z') - Date.parse(a + 'T00:00:00Z')) / 86400000);

                            const form = $('le-form'), submit = $('le-submit'), status = $('le-status'), report = $('le-report');
                            const addressInput = $('le-address'), yearSelect = $('le-year'), basisSelect = $('le-basis'), shareSelect = $('le-share');
                            const params = new URLSearchParams(location.search);
                            const currentYear = new Date().getUTCFullYear();
                            yearSelect.innerHTML = '<option value="""">All years</option>' + Array.from({ length: currentYear - 2024 }, (_, i) => currentYear - i).map(y => `<option value=""${y}"">${y}</option>`).join('');
                            for (const [el, key] of [[yearSelect, 'year'], [basisSelect, 'basis'], [shareSelect, 'share']]) {
                                const v = params.get(key);
                                if (v && [...el.options].some(o => o.value === v)) el.value = v;
                            }
                            addressInput.value = params.get('address') || window.opticWallets.primary() || '';

                            let data = null;

                            // ---------------------------------------------------------------- charts
                            // Shared hover: finds the column under the pointer and shows a tooltip.
                            const attachHover = (el, svg, geom, count, html) => {
                                const tip = document.createElement('div');
                                tip.className = 'le-tip';
                                el.appendChild(tip);
                                const ns = 'http://www.w3.org/2000/svg';
                                const guide = document.createElementNS(ns, 'line');
                                guide.setAttribute('y1', geom.top); guide.setAttribute('y2', geom.bottom);
                                guide.setAttribute('stroke', 'var(--text-muted)'); guide.setAttribute('stroke-dasharray', '3 3'); guide.setAttribute('opacity', '0');
                                svg.appendChild(guide);
                                const move = (e) => {
                                    const ctm = svg.getScreenCTM();
                                    if (!ctm) return;
                                    const x = (e.clientX - ctm.e) / ctm.a;
                                    if (x < geom.left || x > geom.right) { tip.style.opacity = '0'; guide.setAttribute('opacity', '0'); return; }
                                    const i = Math.max(0, Math.min(count - 1, Math.floor((x - geom.left) / ((geom.right - geom.left) / count))));
                                    const cx = geom.left + (i + 0.5) * (geom.right - geom.left) / count;
                                    guide.setAttribute('x1', cx); guide.setAttribute('x2', cx); guide.setAttribute('opacity', '1');
                                    tip.innerHTML = html(i);
                                    const box = el.getBoundingClientRect();
                                    const px = e.clientX - box.left;
                                    tip.style.left = Math.min(px + 12, box.width - tip.offsetWidth - 4) + 'px';
                                    tip.style.top = Math.max(4, e.clientY - box.top - tip.offsetHeight - 10) + 'px';
                                    tip.style.opacity = '1';
                                };
                                svg.addEventListener('mousemove', move);
                                svg.addEventListener('mouseleave', () => { tip.style.opacity = '0'; guide.setAttribute('opacity', '0'); });
                            };
                            const niceMax = (v) => {
                                if (!(v > 0)) return 1;
                                const p = Math.pow(10, Math.floor(Math.log10(v)));
                                return [1, 2, 2.5, 5, 10].map(m => m * p).find(m => m >= v);
                            };
                            const legend = (el, items) => { el.innerHTML = items.map(([name, color, line]) => `<span><i style=""background: ${color};${line ? ' height: 3px; border-radius: 2px;' : ''}""></i>${esc(name)}</span>`).join(''); };

                            // Grouped monthly bars.
                            const drawGrouped = (el, cats, series, labelFor) => {
                                const W = 1100, H = 300, L = 64, R = 12, T = 12, B = 34;
                                const max = niceMax(Math.max(...series.flatMap(s => s.values)));
                                const y = (v) => T + (H - T - B) * (1 - v / max);
                                const slot = (W - L - R) / cats.length, gap = 2, bw = Math.max(2, Math.min(18, (slot * 0.8 - gap * (series.length - 1)) / series.length));
                                let out = '';
                                for (const t of [0, 0.25, 0.5, 0.75, 1]) {
                                    const v = max * t, yy = y(v);
                                    out += `<line x1=""${L}"" x2=""${W - R}"" y1=""${yy}"" y2=""${yy}"" stroke=""var(--border)"" stroke-width=""1""/><text x=""${L - 6}"" y=""${yy + 4}"" font-size=""11"" text-anchor=""end"" fill=""var(--text-muted)"">${fmtBig(v)}</text>`;
                                }
                                cats.forEach((c, i) => {
                                    const x0 = L + i * slot + (slot - (bw * series.length + gap * (series.length - 1))) / 2;
                                    series.forEach((s, j) => {
                                        const v = s.values[i] || 0;
                                        if (v <= 0) return;
                                        const h = Math.max(1, H - B - y(v)), x = x0 + j * (bw + gap), top = H - B - h, r = Math.min(4, bw / 2, h);
                                        out += `<path d=""M${x},${H - B} V${top + r} Q${x},${top} ${x + r},${top} H${x + bw - r} Q${x + bw},${top} ${x + bw},${top + r} V${H - B} Z"" fill=""${s.color}""/>`;
                                    });
                                    if (cats.length <= 24 || i % 2 === 0) out += `<text x=""${L + (i + 0.5) * slot}"" y=""${H - B + 16}"" font-size=""11"" text-anchor=""middle"" fill=""var(--text-muted)"">${esc(labelFor(c))}</text>`;
                                });
                                out += `<line x1=""${L}"" x2=""${W - R}"" y1=""${H - B}"" y2=""${H - B}"" stroke=""var(--border-strong)"" stroke-width=""1""/>`;
                                el.innerHTML = `<svg viewBox=""0 0 ${W} ${H}"" role=""img"" aria-label=""Monthly bars"">${out}</svg>`;
                                return { svg: el.querySelector('svg'), geom: { left: L, right: W - R, top: T, bottom: H - B } };
                            };

                            // Daily bars with a line over them, same axis.
                            const drawBarsLine = (el, days, bars, line) => {
                                const W = 1100, H = 280, L = 64, R = 12, T = 12, B = 34;
                                const max = niceMax(Math.max(...bars.values, ...line.values.filter(v => v != null)));
                                const y = (v) => T + (H - T - B) * (1 - v / max);
                                const slot = (W - L - R) / days.length, bw = Math.max(1, slot - (slot > 4 ? 2 : 0.5));
                                let out = '';
                                for (const t of [0, 0.25, 0.5, 0.75, 1]) {
                                    const v = max * t, yy = y(v);
                                    out += `<line x1=""${L}"" x2=""${W - R}"" y1=""${yy}"" y2=""${yy}"" stroke=""var(--border)"" stroke-width=""1""/><text x=""${L - 6}"" y=""${yy + 4}"" font-size=""11"" text-anchor=""end"" fill=""var(--text-muted)"">${fmtBig(v)}</text>`;
                                }
                                days.forEach((d, i) => {
                                    const v = bars.values[i];
                                    if (v > 0) { const top = y(v); out += `<rect x=""${L + i * slot + (slot - bw) / 2}"" y=""${top}"" width=""${bw}"" height=""${Math.max(1, H - B - top)}"" rx=""${Math.min(2, bw / 2)}"" fill=""${bars.color}""/>`; }
                                });
                                const pts = line.values.map((v, i) => v == null ? null : `${L + (i + 0.5) * slot},${y(v)}`).filter(Boolean);
                                if (pts.length) out += `<path d=""M ${pts.join(' L ')}"" fill=""none"" stroke=""${line.color}"" stroke-width=""2"" stroke-linejoin=""round""/>`;
                                const step = Math.max(1, Math.ceil(days.length / 10));
                                days.forEach((d, i) => { if (i % step === 0) out += `<text x=""${L + (i + 0.5) * slot}"" y=""${H - B + 16}"" font-size=""11"" text-anchor=""middle"" fill=""var(--text-muted)"">${esc(fmtMonth(d.slice(0, 7)))}</text>`; });
                                out += `<line x1=""${L}"" x2=""${W - R}"" y1=""${H - B}"" y2=""${H - B}"" stroke=""var(--border-strong)"" stroke-width=""1""/>`;
                                el.innerHTML = `<svg viewBox=""0 0 ${W} ${H}"" role=""img"" aria-label=""Daily bars and line"">${out}</svg>`;
                                return { svg: el.querySelector('svg'), geom: { left: L, right: W - R, top: T, bottom: H - B } };
                            };

                            // Activity strips: one row per kind of activity, a tick for each active day, frozen stretches shaded.
                            const drawTimeline = (el, days, rows, shaded) => {
                                const W = 1100, L = 210, R = 12, T = 8, rowH = 30, B = 26, H = T + rows.length * rowH + B;
                                const slot = (W - L - R) / days.length, tw = Math.max(1, slot - (slot > 3 ? 1 : 0));
                                const index = new Map(days.map((d, i) => [d, i]));
                                let out = '';
                                for (const g of shaded) {
                                    const a = index.get(g.from), b = index.get(g.to);
                                    if (a == null || b == null) continue;
                                    out += `<rect x=""${L + a * slot}"" y=""${T}"" width=""${(b - a + 1) * slot}"" height=""${rows.length * rowH}"" fill=""var(--error)"" opacity=""0.10""/>`;
                                }
                                rows.forEach((r, k) => {
                                    const y0 = T + k * rowH;
                                    out += `<text x=""${L - 10}"" y=""${y0 + rowH / 2 + 4}"" font-size=""12"" text-anchor=""end"" fill=""var(--text-secondary)"">${esc(r.name)}</text>`;
                                    out += `<line x1=""${L}"" x2=""${W - R}"" y1=""${y0 + rowH - 0.5}"" y2=""${y0 + rowH - 0.5}"" stroke=""var(--border)"" stroke-width=""1""/>`;
                                    days.forEach((d, i) => { if (r.active(d)) out += `<rect x=""${L + i * slot}"" y=""${y0 + 6}"" width=""${tw}"" height=""${rowH - 12}"" fill=""${r.color}""/>`; });
                                });
                                // Label every k-th month start so labels never run into each other.
                                const starts = days.map((d, i) => [d, i]).filter(([d]) => d.endsWith('-01'));
                                const k = Math.max(1, Math.ceil(starts.length / 10));
                                starts.forEach(([d, i], n) => { if (n % k === 0) out += `<text x=""${L + i * slot}"" y=""${H - 8}"" font-size=""11"" fill=""var(--text-muted)"">${esc(fmtMonth(d.slice(0, 7)))}</text>`; });
                                el.innerHTML = `<svg viewBox=""0 0 ${W} ${H}"" role=""img"" aria-label=""Activity timeline"">${out}</svg>`;
                                return { svg: el.querySelector('svg'), geom: { left: L, right: W - R, top: T, bottom: T + rows.length * rowH } };
                            };

                            // ---------------------------------------------------------------- analysis
                            const analyse = () => {
                                const { market, flows, dist, listing, quote } = data;
                                const share = Number(shareSelect.value), basis = basisSelect.value, year = yearSelect.value;
                                const inYear = (d) => !year || d.startsWith(year);
                                const distBy = new Map();
                                for (const r of dist.rows) { const d = String(r.timeUtc).slice(0, 10); distBy.set(d, (distBy.get(d) || 0) + r.amountOpt); }

                                // Daily series over the whole history so trailing averages look back past the year start.
                                const days = window.opticMarket.daily(market, flows, basis, share);
                                for (const x of days) x.you = distBy.get(x.d) || 0;
                                const end = days.length ? days[days.length - 1].d : new Date().toISOString().slice(0, 10);

                                // Periods Coinstore was closed to the public (too few accounts depositing), from its first deposit to today.
                                const gaps = window.opticMarket.closedPeriods(days, flows);
                                for (const g of gaps) {
                                    const span = days.slice(g.i0, g.i1 + 1);
                                    g.days = span.length;
                                    g.deposits = span.reduce((s, x) => s + x.depositCount, 0);
                                    g.accounts = new Set(span.flatMap(x => x.depositAccounts)).size;
                                    g.reported = span.reduce((s, x) => s + x.csReported, 0);
                                    g.you = span.reduce((s, x) => s + x.you, 0);
                                }

                                const sel = days.filter(x => inYear(x.d));
                                const shownGaps = !year ? gaps : gaps.filter(g => g.from <= year + '-12-31' && g.to >= year + '-01-01').map(g => {
                                    const from = g.from < year + '-01-01' ? year + '-01-01' : g.from, to = g.to > year + '-12-31' ? year + '-12-31' : g.to;
                                    const i0 = days.findIndex(x => x.d === from), i1 = days.findIndex(x => x.d === to);
                                    const span = days.slice(i0, i1 + 1);
                                    return { ...g, from, to, i0, i1, cutFrom: from !== g.from, cutTo: to !== g.to, open: g.open && to === g.to,
                                        days: span.length, deposits: span.reduce((s, x) => s + x.depositCount, 0), accounts: new Set(span.flatMap(x => x.depositAccounts)).size,
                                        reported: span.reduce((s, x) => s + x.csReported, 0), you: span.reduce((s, x) => s + x.you, 0) };
                                });
                                const months = [...new Set(sel.map(x => x.d.slice(0, 7)))].map(m => {
                                    const xs = sel.filter(x => x.d.startsWith(m));
                                    const sum = (f) => xs.reduce((s, x) => s + f(x), 0);
                                    return { m, reported: sum(x => x.csReported), deposit: sum(x => x.deposit), withdraw: sum(x => x.withdraw), noDep: xs.filter(x => x.depositCount === 0).length, accounts: new Set(xs.flatMap(x => x.depositAccounts)).size, os: sum(x => x.osOpt), absorb: sum(x => x.absorb), you: sum(x => x.you), days: xs.length };
                                });
                                return { days, sel, months, gaps, shownGaps, share, basis, year, listing, quote };
                            };

                            // ---------------------------------------------------------------- render
                            const render = () => {
                                const a = analyse();
                                const { market, flows, dist, listing, quote } = data;
                                const period = a.year || 'the whole period';
                                const sum = (xs, f) => xs.reduce((s, x) => s + f(x), 0);
                                const youOpt = sum(a.sel, x => x.you), youValue = sum(a.sel, x => x.you * (x.price || 0));
                                const payoutRows = dist.rows.filter(r => !a.year || String(r.timeUtc).startsWith(a.year));
                                const payoutDays = a.sel.filter(x => x.you > 0);
                                const overDays = payoutDays.filter(x => x.you > x.absorb);
                                const absorbSel = sum(a.sel, x => x.absorb);
                                const avgAbsorb = a.sel.length ? absorbSel / a.sel.length : 0;
                                const reportedSel = sum(a.sel, x => x.csReported), depSel = sum(a.sel, x => x.deposit), wdSel = sum(a.sel, x => x.withdraw);
                                const gapsSel = a.shownGaps;
                                const lastDep = flows.lastDeposit ? String(flows.lastDeposit).slice(0, 10) : null;
                                const cs = market.snapshot?.coinstore;
                                const spot = market.snapshot?.osmosisSpotPriceUsd;
                                const liquidity = (market.snapshot?.pools || []).reduce((s, p) => s + (p.liquidityUsd || 0), 0);
                                const basisText = a.basis === 'reported' ? 'the volume exchanges reported' : 'OPT actually withdrawn from Coinstore on chain';
                                const short = (addr) => addr.slice(0, 12) + '…' + addr.slice(-6);

                                $('le-print-header').innerHTML = `Prepared ${esc(new Date().toISOString().slice(0, 16).replace('T', ' '))} UTC with OPTIC · Wallet ${esc(dist.address)} · Period: ${esc(period)} · Market volume basis: ${esc(basisText)}, ${fmtPct(a.share)} sellable`;

                                const findings = [];
                                findings.push(`<strong>You received ${fmtOpt(youOpt)} OPT</strong> in ${payoutRows.length.toLocaleString('en-US')} distribution payouts during ${esc(period)}, worth ${fmtUsd(youValue)} at the quoted daily price.`);
                                if (lastDep) findings.push(`<strong>Coinstore stopped receiving OPT deposits after ${fmtDate(lastDep)}.</strong> No customer deposit reached its deposit address (${esc(short(flows.depositCollector))}) after that day, so from then on nobody could bring OPT to the exchange to sell. Coinstore's last OPT trade was on ${fmtDate(market.sources.coinstoreTo)}${cs && cs.bestBid == null ? ', and its order book has no buy orders at all today' : ''}.`);
                                if (gapsSel.length) {
                                    const closedDays = a.sel.filter(x => a.gaps.some(g => x.d >= g.from && x.d <= g.to)).length;
                                    const youClosed = sum(a.sel.filter(x => a.gaps.some(g => x.d >= g.from && x.d <= g.to)), x => x.you);
                                    const list = gapsSel.slice(0, 4).map(g => `${fmtDate(g.from)} to ${g.open ? 'today' : fmtDate(g.to)}${g.cutTo && !g.open ? ' and beyond' : ''} (${g.days} days, ${g.accounts} account(s) depositing)`).join('; ');
                                    findings.push(`<strong>Coinstore was closed to the public for ${closedDays.toLocaleString('en-US')} of the ${a.sel.length.toLocaleString('en-US')} days${a.year ? ' of ' + esc(a.year) : ''}</strong>: ${list}. In those periods only a handful of accounts, often a single one, got OPT onto the exchange. You received ${fmtOpt(youClosed)} OPT while it was closed.`);
                                }
                                if (depSel > 0) findings.push(`<strong>Coinstore reported ${fmtOpt(reportedSel)} OPT traded${a.year ? ' in ' + esc(a.year) : ''}, but customers deposited only ${fmtOpt(depSel)} OPT</strong> (${fmtX(reportedSel / depSel)} the OPT deposited${a.year ? ' that year' : ''}) and withdrew ${fmtOpt(wdSel)} OPT. ${a.year ? `Only ${new Set(a.sel.flatMap(x => x.depositAccounts)).size} Coinstore accounts deposited OPT in ${esc(a.year)}.` : `Only ${fmtOpt(flows.depositWallets)} wallets ever deposited OPT to Coinstore.`} Reported volume on this scale can't have come from customers selling OPT they brought to the exchange, so it isn't evidence that a buyer existed for your OPT.`);
                                else if (reportedSel > 0) findings.push(`<strong>Coinstore reported ${fmtOpt(reportedSel)} OPT traded${a.year ? ' in ' + esc(a.year) : ''} although not one customer deposit reached it${a.year ? ' that year' : ''}</strong>, while customers withdrew ${fmtOpt(wdSel)} OPT. With no OPT coming in, that reported volume can't have been customers selling OPT they brought to the exchange.`);
                                findings.push(`<strong>The market could absorb about ${fmtOpt(avgAbsorb)} OPT a day</strong> (${fmtPct(a.share)} of the 30-day average daily volume, using ${esc(basisText)} plus Osmosis). ${payoutDays.length ? `On ${overDays.length.toLocaleString('en-US')} of your ${payoutDays.length.toLocaleString('en-US')} payout days you received more OPT than that.` : ''} Selling everything you received at that pace would take ${avgAbsorb > 0 ? Math.ceil(youOpt / avgAbsorb).toLocaleString('en-US') + ' days' : 'forever, because there was no volume'}.`);
                                const listings = [];
                                if (listing.cmcStatus) listings.push(`CoinMarketCap marks OPT <em>${esc(listing.cmcStatus)}</em> (last updated ${fmtDate(listing.cmcLastUpdated)})`);
                                if (listing.coinGeckoTickers != null) listings.push(`CoinGecko lists ${listing.coinGeckoTickers} exchange market(s) for it (last updated ${fmtDate(listing.coinGeckoLastUpdated)})`);
                                if (listing.mexcListed === false) listings.push('MEXC no longer lists it');
                                if (listings.length) findings.push(`<strong>OPT is no longer listed or tracked:</strong> ${listings.join('; ')}.`);
                                if (quote) {
                                    findings.push(quote.receiveUsd != null
                                        ? `<strong>Selling ${fmtOpt(quote.sellOpt)} OPT on Osmosis today would return ${fmtUsd(quote.receiveUsd)}</strong>, ${fmtPct(spot ? 1 - quote.avgPriceUsd / spot : null)} below the quoted price. All Osmosis OPT pools together hold about ${fmtUsd(liquidity)}.`
                                        : `<strong>The Osmosis pools can't take ${fmtOpt(quote.sellOpt)} OPT at all</strong>: the router finds no route for that size. All Osmosis OPT pools together hold about ${fmtUsd(liquidity)}.`);
                                }
                                $('le-findings').innerHTML = findings.map(f => `<li>${f}</li>`).join('');

                                // Chart 1: monthly reported vs deposited vs withdrawn (selected year, or Coinstore's whole life).
                                const csMonths = a.year ? [...new Set(a.sel.map(x => x.d.slice(0, 7)))].sort() : [...new Set(a.days.filter(x => x.csReported > 0 || x.deposit > 0 || x.withdraw > 0).map(x => x.d.slice(0, 7)))].sort();
                                const monthSum = (m, f) => a.days.filter(x => x.d.startsWith(m)).reduce((s, x) => s + f(x), 0);
                                const series1 = [
                                    { name: 'Coinstore reported traded', color: COLORS.reported, values: csMonths.map(m => monthSum(m, x => x.csReported)) },
                                    { name: 'Deposited to Coinstore (on chain)', color: COLORS.deposited, values: csMonths.map(m => monthSum(m, x => x.deposit)) },
                                    { name: 'Withdrawn from Coinstore (on chain)', color: COLORS.withdrawn, values: csMonths.map(m => monthSum(m, x => x.withdraw)) },
                                ];
                                const c1 = drawGrouped($('le-chart-monthly'), csMonths, series1, fmtMonth);
                                legend($('le-legend-monthly'), series1.map(s => [s.name, s.color]));
                                attachHover($('le-chart-monthly'), c1.svg, c1.geom, csMonths.length, (i) => `<strong>${fmtMonth(csMonths[i])}</strong><br>` + series1.map(s => `${esc(s.name)}: ${fmtOpt(s.values[i])} OPT`).join('<br>') + (series1[1].values[i] > 0 ? `<br>Reported ÷ deposited: ${fmtX(series1[0].values[i] / series1[1].values[i])}` : ''));

                                // Chart 2: activity timeline for the selected year (or the whole history), closed periods shaded.
                                const tlSrc = a.year ? a.sel : a.days;
                                const tlDays = tlSrc.map(x => x.d);
                                const byDay = new Map(a.days.map(x => [x.d, x]));
                                const rows = [
                                    { name: 'Deposits reached Coinstore', color: COLORS.deposited, active: (d) => byDay.get(d).depositCount > 0 },
                                    { name: 'Withdrawals left Coinstore', color: COLORS.withdrawn, active: (d) => byDay.get(d).withdrawCount > 0 },
                                    { name: 'Coinstore reported trades', color: COLORS.reported, active: (d) => byDay.get(d).csReported > 0 },
                                    { name: 'Osmosis swaps', color: 'var(--text-muted)', active: (d) => byDay.get(d).osTxs > 0 },
                                    { name: 'You received OPT', color: COLORS.you, active: (d) => byDay.get(d).you > 0 },
                                ];
                                const c2 = drawTimeline($('le-chart-timeline'), tlDays, rows, a.shownGaps);
                                attachHover($('le-chart-timeline'), c2.svg, c2.geom, tlDays.length, (i) => {
                                    const x = tlSrc[i];
                                    const gap = a.gaps.find(g => x.d >= g.from && x.d <= g.to);
                                    return `<strong>${fmtDate(x.d)}</strong>${gap ? ' · Coinstore closed to the public' : ''}<br>Deposits: ${x.depositCount} from ${x.depositAccounts.length} account(s) (${fmtOpt(x.deposit)} OPT)<br>Withdrawals: ${x.withdrawCount} (${fmtOpt(x.withdraw)} OPT)<br>Coinstore reported: ${fmtOpt(x.csReported)} OPT<br>Osmosis swaps: ${x.osTxs}<br>You received: ${fmtOpt(x.you)} OPT`;
                                });

                                // Chart 3: your receipts vs absorbable, selected period.
                                $('le-absorb-lede').textContent = `Bars are the OPT you received each day. The line is how much the whole market could have absorbed that day: ${fmtPct(a.share)} of the average daily volume over the previous 30 days, counting ${basisText} plus net Osmosis swaps. Where a bar rises above the line, you received more than could have been sold that day without pushing the price down.`;
                                const c3 = drawBarsLine($('le-chart-absorb'), a.sel.map(x => x.d), { color: COLORS.you, values: a.sel.map(x => x.you) }, { color: COLORS.absorb, values: a.sel.map(x => x.absorb) });
                                legend($('le-legend-absorb'), [['You received (OPT)', COLORS.you], ['Market could absorb per day (OPT)', COLORS.absorb, true]]);
                                attachHover($('le-chart-absorb'), c3.svg, c3.geom, a.sel.length, (i) => { const x = a.sel[i]; return `<strong>${fmtDate(x.d)}</strong><br>You received: ${fmtOpt(x.you)} OPT<br>Market could absorb: ${fmtOpt(x.absorb)} OPT<br>Price: ${fmtPrice(x.price)}`; });

                                // Tables.
                                $('le-months-body').innerHTML = a.months.slice().reverse().map(m => `<tr>
                                    <td>${fmtMonth(m.m)}</td>
                                    <td class=""amount-cell"" style=""text-align: right;"">${fmtOpt(m.reported)}</td>
                                    <td class=""amount-cell"">${fmtOpt(m.deposit)}</td>
                                    <td class=""amount-cell"">${fmtOpt(m.withdraw)}</td>
                                    <td class=""amount-cell"">${m.deposit > 0 ? fmtX(m.reported / m.deposit) : m.reported > 0 ? 'no deposits' : '-'}</td>
                                    <td class=""amount-cell"">${m.noDep} of ${m.days}</td>
                                    <td class=""amount-cell"">${m.accounts.toLocaleString('en-US')}</td>
                                    <td class=""amount-cell"">${fmtOpt(m.os)}</td>
                                    <td class=""amount-cell"">${fmtOpt(m.absorb)}</td>
                                    <td class=""amount-cell"">${fmtOpt(m.you)}</td>
                                    <td class=""amount-cell"" style=""${m.absorb > 0 && m.you > m.absorb ? 'color: var(--error); font-weight: 600;' : ''}"">${m.you > 0 ? (m.absorb > 0 ? fmtX(m.you / m.absorb) : 'no market') : '-'}</td>
                                </tr>`).join('');
                                $('le-gaps-body').innerHTML = a.shownGaps.slice().reverse().map(g => `<tr>
                                    <td>${g.from}${g.cutFrom ? ' (from earlier)' : ''}</td>
                                    <td>${g.open ? 'still closed' : g.to + (g.cutTo ? ' (continues)' : '')}</td>
                                    <td class=""amount-cell"" style=""text-align: right;"">${g.days.toLocaleString('en-US')}</td>
                                    <td class=""amount-cell"">${g.deposits.toLocaleString('en-US')}</td>
                                    <td class=""amount-cell"">${g.accounts.toLocaleString('en-US')}</td>
                                    <td class=""amount-cell"">${fmtOpt(g.reported)}</td>
                                    <td class=""amount-cell"">${fmtOpt(g.you)}</td>
                                </tr>`).join('') || '<tr><td colspan=""7"" style=""text-align: center; color: var(--text-secondary);"">None</td></tr>';

                                const today = [
                                    ['Coinstore buy orders (OPT/USDT)', cs ? (cs.bestBid == null ? 'None. Nobody is bidding for OPT.' : `${fmtOpt(cs.bidDepthOpt)} OPT bid, best ${fmtPrice(cs.bestBid)}`) : 'Unavailable', 'api.coinstore.com /market/depth/OPTUSDT'],
                                    ['Coinstore last trade', cs?.lastTradeUtc ? `${fmtDate(cs.lastTradeUtc)} at ${fmtPrice(cs.lastTradePrice)}` : '-', 'api.coinstore.com /market/trade/OPTUSDT'],
                                    ['Last deposit reaching Coinstore', lastDep ? fmtDate(lastDep) : 'None', `Optio chain, deposits to ${flows.depositCollector}`],
                                    ['Last withdrawal from Coinstore', flows.lastWithdrawal ? fmtDate(flows.lastWithdrawal) : 'None', `Optio chain, withdrawals from ${flows.hotWallet}`],
                                    ['CoinMarketCap', listing.cmcStatus ? `Status ""${esc(listing.cmcStatus)}"", last updated ${fmtDate(listing.cmcLastUpdated)}` : 'Unavailable', 'coinmarketcap.com (id 35828)'],
                                    ['CoinGecko', listing.coinGeckoTickers != null ? `${listing.coinGeckoTickers} exchange market(s), last updated ${fmtDate(listing.coinGeckoLastUpdated)}` : 'Unavailable', 'coingecko.com (optio)'],
                                    ['MEXC', listing.mexcListed == null ? 'Unavailable' : listing.mexcListed ? 'Listed' : 'OPTUSDT not listed', 'api.mexc.com exchangeInfo'],
                                    ['Osmosis pool liquidity', `${fmtUsd(liquidity)} across all OPT pools, spot ${fmtPrice(spot)}`, 'Numia / Osmosis SQS'],
                                    ...((market.snapshot?.sellQuotes || []).concat(quote ? [quote] : []).sort((x, y) => x.sellOpt - y.sellOpt).map(q => [`Sell ${fmtOpt(q.sellOpt)} OPT on Osmosis`, q.receiveUsd != null ? `${fmtUsd(q.receiveUsd)} (${fmtPct(spot ? 1 - q.avgPriceUsd / spot : null)} below spot)` : 'No route: the pools can\x27t absorb it', 'Osmosis SQS router'])),
                                ];
                                $('le-today-body').innerHTML = today.map(([k, v, src]) => `<tr><td>${esc(k)}</td><td style=""text-align: left;"">${v}</td><td style=""text-align: left; color: var(--text-secondary); font-size: 12px;"">${esc(src)}</td></tr>`).join('');

                                $('le-method').innerHTML = [
                                    `Coinstore deposits and withdrawals are read from the Optio chain. Customers deposited by sending OPT to ${esc(flows.depositCollector)} with their account number as the memo (${fmtOpt(flows.depositAccounts)} accounts, ${fmtOpt(flows.depositWallets)} wallets); Coinstore forwarded these to ${esc(flows.hotWallet)} and paid withdrawals from it (to ${fmtOpt(flows.withdrawalWallets)} wallets). Transfers between those two Coinstore wallets are not counted.`,
                                    `Coinstore traded volume is Coinstore\x27s own daily OPT/USDT candles (${fmtDate(market.sources.coinstoreFrom)} to ${fmtDate(market.sources.coinstoreTo)}). Reported all-exchange volume is CoinMarketCap\x27s (${fmtDate(market.sources.cmcFrom)} to ${fmtDate(market.sources.cmcTo)}), which includes Coinstore and MEXC. Osmosis volume is every OPT swap on chain (${fmtOpt(market.sources.osmosisTxs)} transactions), counted net per transaction so arbitrage cancels out.`,
                                    `Market could absorb = ${fmtPct(a.share)} of the average daily volume over the previous 30 days. With “OPT actually withdrawn from Coinstore”, exchange volume is the OPT customers took out of Coinstore that day, the most that real buyers could have taken delivery of; with “Volume the exchanges reported”, it is the higher of CoinMarketCap\x27s and Coinstore\x27s figure. Prices are CoinMarketCap daily closes, then Coinstore\x27s, then Osmosis\x27s, then the last known price. Distributions are payouts to the wallet from the node distribution account (${esc(dist.source)}). All dates are UTC.`,
                                    'This report states what the public records show. It is not tax or legal advice; ask your tax adviser how to use it.'
                                ].map(t => `<p style=""margin: 0 0 6px;"">${t}</p>`).join('');

                                renderEvidence(data.evidence);
                                report.hidden = false;
                                $('le-print').disabled = false;
                            };

                            const renderEvidence = (ev) => {
                                const zip = $('le-zip');
                                if (!ev || !ev.files) {
                                    $('le-evidence').innerHTML = '<p class=""le-lede"">Nothing captured yet. Use “Capture evidence now” to save today\x27s records.</p>';
                                    zip.hidden = true;
                                    return;
                                }
                                zip.href = `/api/market/evidence/${encodeURIComponent(ev.id)}.zip`;
                                zip.hidden = false;
                                $('le-evidence').innerHTML = `<p style=""font-size: 13px; margin: 0 0 6px;"">Captured ${esc(String(ev.capturedUtc).replace('T', ' ').slice(0, 19))} UTC · ${ev.files.length} files · folder odata/evidence/${esc(ev.id)}</p>
                                    <div class=""data-table-wrap pnl-table-wrap""><table class=""data-table""><thead><tr><th>File</th><th style=""text-align: left;"">What it shows</th><th style=""text-align: right;"">HTTP</th><th style=""text-align: left;"">SHA-256</th></tr></thead><tbody>` +
                                    ev.files.map(f => `<tr><td title=""${esc(f.url)}"">${esc(f.name)}</td><td style=""text-align: left;"">${esc(f.description)}${f.error ? ` <span style=""color: var(--error);"">(${esc(f.error)})</span>` : ''}</td><td class=""amount-cell"">${f.httpStatus ?? '-'}</td><td class=""le-hash"" style=""text-align: left;"">${esc(f.sha256)}</td></tr>`).join('') +
                                    '</tbody></table></div>';
                            };

                            const getJson = async (url, opts) => {
                                const r = await fetch(url, opts);
                                const j = await r.json();
                                if (!r.ok) throw new Error(j.error || `${url} failed`);
                                return j;
                            };

                            const run = async () => {
                                const address = addressInput.value.trim();
                                if (!/^optio1[02-9ac-hj-np-z]{38,58}$/.test(address)) { status.textContent = 'Enter your optio1... wallet address (or save it as primary on My Wallets).'; return; }
                                const q = new URLSearchParams({ address });
                                if (yearSelect.value) q.set('year', yearSelect.value);
                                if (basisSelect.value !== 'withdrawals') q.set('basis', basisSelect.value);
                                if (shareSelect.value !== '0.1') q.set('share', shareSelect.value);
                                history.replaceState(null, '', '?' + q.toString());
                                const done = window.opticBusy(submit, status, 'Reading the chain and market history');
                                status.textContent = 'Reading Coinstore deposits and withdrawals, market history and your distributions...';
                                try {
                                    const [{ market, flows }, dist, listing, evidence] = await Promise.all([
                                        window.opticMarket.load(),
                                        getJson('/api/distributions/address?address=' + encodeURIComponent(address)),
                                        getJson('/api/market/status'),
                                        fetch('/api/market/evidence/latest').then(r => r.json()).catch(() => null),
                                    ]);
                                    const youOpt = dist.rows.filter(r => !yearSelect.value || String(r.timeUtc).startsWith(yearSelect.value)).reduce((s, r) => s + r.amountOpt, 0);
                                    const quote = youOpt >= 1 ? await getJson('/api/market/quote?opt=' + Math.round(youOpt)).catch(() => null) : null;
                                    data = { market, flows, dist, listing, evidence, quote };
                                    render();
                                    status.textContent = flows.truncated ? 'Note: the Coinstore history hit the 20,000-transaction cap; older activity may be missing.' : '';
                                    renderWallets();
                                } catch (error) {
                                    status.textContent = 'Error: ' + error.message;
                                } finally {
                                    done();
                                }
                            };

                            const renderWallets = () => window.opticWallets.renderSwitcher($('le-wallets'), addressInput.value.trim() || null, (address) => { addressInput.value = address; run(); });
                            renderWallets();

                            form.addEventListener('submit', (e) => { e.preventDefault(); run(); });
                            for (const el of [yearSelect, basisSelect, shareSelect]) el.addEventListener('change', () => { if (data && data.dist.address === addressInput.value.trim()) run(); });
                            $('le-print').addEventListener('click', () => window.print());
                            $('le-capture').addEventListener('click', async () => {
                                const btn = $('le-capture');
                                const done = window.opticBusy(btn, btn.parentElement, 'Saving raw records from each source');
                                try {
                                    const sizes = data?.quote ? '?opt=' + Math.round(data.quote.sellOpt) : '';
                                    data.evidence = await getJson('/api/market/evidence' + sizes, { method: 'POST' });
                                    renderEvidence(data.evidence);
                                } catch (error) {
                                    $('le-evidence').textContent = 'Capture failed: ' + error.message;
                                } finally {
                                    done();
                                }
                            });

                            if (addressInput.value) run();
                        })();
                    </script>");
    }

    // One tax year for one wallet: node and staking income at fair market value with the evidence behind it,
    // and every transfer out with the user's own treatment, proceeds and notes, matched FIFO for gain or loss.
    static void AppendTaxReportCard(System.Text.StringBuilder sb)
    {
        AppendMyWalletsScript(sb);
        AppendMarketMathScript(sb);
        sb.Append(@"
                    <style>
                        .tr-section { margin: 0 0 24px; }
                        .tr-section h3 { margin: 0 0 6px; font-size: 16px; }
                        .tr-section h4 { margin: 14px 0 6px; font-size: 14px; }
                        .tr-lede { color: var(--text-secondary); font-size: 13px; margin: 0 0 10px; max-width: 1040px; }
                        .tr-summary td, .tr-summary th { padding: 6px 10px; }
                        .tr-total td { font-weight: 600; border-top: 2px solid var(--border-strong); }
                        .tr-flag { color: var(--warning); font-weight: 600; }
                        tr.tr-unconfirmed > td { background: color-mix(in srgb, var(--warning) 14%, transparent); }
                        tr.tr-unconfirmed > td:first-child { box-shadow: inset 4px 0 0 var(--warning); }
                        .tr-badge { display: inline-block; font-size: 11px; font-weight: 600; border-radius: 10px; padding: 1px 8px; margin: 0 0 4px; white-space: nowrap; }
                        .tr-badge.todo { background: var(--warning); color: #000; }
                        .tr-badge.ok { color: var(--success, #107c10); border: 1px solid currentColor; }
                        .tr-only-todo tr:not(.tr-unconfirmed):not(.tr-empty) { display: none; }
                        .tr-loss { color: var(--error); font-weight: 600; }
                        .tr-edit select, .tr-edit input { font-size: 12px; padding: 3px 6px; min-height: 0; }
                        .tr-edit input.tr-usd { width: 110px; text-align: right; }
                        .tr-edit input.tr-date { width: 130px; }
                        .tr-edit textarea { font-size: 12px; width: 240px; min-height: 30px; padding: 3px 6px; resize: vertical; }
                        .tr-note-print { display: none; white-space: pre-wrap; font-size: 12px; }
                        .tr-print-only { display: none; }
                        .tr-copy, .tr-name { font-size: 10px; padding: 1px 6px; margin-left: 4px; min-height: 0; line-height: 1.4; vertical-align: middle; cursor: pointer; }
                        .tr-legal { font-size: 13px; line-height: 1.55; max-width: 1040px; }
                        .tr-legal li { margin-bottom: 6px; }
                        @media print {
                            tr.tr-unconfirmed > td { background: none !important; box-shadow: none !important; }
                            .top-nav, #tr-controls, .tr-no-print, .tr-copy, .tr-name, .modal { display: none !important; }
                            .tr-print-only, .tr-note-print { display: block; }
                            .tr-edit select, .tr-edit input, .tr-edit textarea { border: none; background: none; padding: 0; appearance: none; -webkit-appearance: none; color: inherit; }
                            .tr-edit textarea { display: none; }
                            body, .main-content, .card { background: #fff !important; box-shadow: none !important; }
                            .card { border: none !important; }
                            .data-table-wrap { max-height: none !important; overflow: visible !important; }
                            .tr-section { break-inside: auto; }
                            tr { break-inside: avoid; }
                            a { color: inherit; text-decoration: none; }
                        }
                    </style>
                    <div class=""card"" style=""grid-column: 1 / -1;"">
                        <h2>OPT Tax Report: Node and Staking Income, Sales and Losses</h2>
                        <div class=""card-content"">
                            <div id=""tr-controls"">
                                <form id=""tr-form"" style=""display: flex; gap: 8px; flex-wrap: wrap; align-items: flex-end; margin-bottom: 8px;"">
                                    <div class=""form-group"" style=""flex: 1 1 380px; margin-bottom: 0;"">
                                        <label for=""tr-address"">Wallet address</label>
                                        <input type=""text"" id=""tr-address"" placeholder=""optio1..."" autocomplete=""off"" spellcheck=""false"">
                                    </div>
                                    <div class=""form-group"" style=""flex: 0 0 110px; margin-bottom: 0;"">
                                        <label for=""tr-year"">Tax year</label>
                                        <select id=""tr-year""></select>
                                    </div>
                                    <div class=""form-group"" style=""flex: 0 0 200px; margin-bottom: 0;"">
                                        <label for=""tr-type"">Income shown</label>
                                        <select id=""tr-type"">
                                            <option value="""">Node and staking</option>
                                            <option value=""node"">Node only</option>
                                            <option value=""staking"">Staking only</option>
                                        </select>
                                    </div>
                                    <div class=""form-group"" style=""flex: 0 0 280px; margin-bottom: 0;"" title=""How to value OPT received on days when no exchange was open to the public and Osmosis had no trades"">
                                        <label for=""tr-rule"">Days with no market you could reach</label>
                                        <select id=""tr-rule"">
                                            <option value=""dlom"">Quoted less marketability discount</option>
                                            <option value=""quoted"">Quoted price, no discount</option>
                                            <option value=""next"">Price on the next day you could sell</option>
                                            <option value=""last"">Price on the last day you could sell</option>
                                        </select>
                                    </div>
                                    <div class=""form-group"" style=""flex: 0 0 200px; margin-bottom: 0;"" title=""Optional blockage discount, applied only to OPT above what the market could absorb that day. Leave at 0 unless a valuation professional supports a figure."">
                                        <label for=""tr-blockage"">Blockage discount (%)</label>
                                        <input type=""number"" id=""tr-blockage"" min=""0"" max=""100"" step=""any"" placeholder=""0"">
                                    </div>
                                    <button type=""submit"" id=""tr-submit"">Build report</button>
                                    <button type=""button"" id=""tr-print"" disabled>Print / Save as PDF</button>
                                </form>
                                <div id=""tr-wallets"" style=""font-size: 13px; color: var(--text-secondary); margin: 0 0 8px; display: flex; gap: 6px; flex-wrap: wrap; align-items: center;""></div>
                                <details id=""tr-names-box"" style=""margin: 0 0 6px; font-size: 13px;"">
                                    <summary style=""cursor: pointer;"">Named addresses <span id=""tr-name-count"" style=""color: var(--text-secondary);""></span></summary>
                                    <p class=""tr-lede"" style=""margin: 6px 0;"">Give other people&#39;s addresses a name so the report shows who you sent OPT to or got it from. You can also use the Name button next to any address in the report. Names are saved on this computer.</p>
                                    <div id=""tr-name-list"" style=""display: flex; flex-direction: column; gap: 4px; margin-bottom: 6px;""></div>
                                    <div style=""display: flex; gap: 6px; flex-wrap: wrap; align-items: center;"">
                                        <input type=""text"" id=""tr-name-address"" placeholder=""optio1..."" autocomplete=""off"" spellcheck=""false"" style=""flex: 1 1 340px;"">
                                        <input type=""text"" id=""tr-name-name"" placeholder=""Name (e.g. John, node seller)"" autocomplete=""off"" maxlength=""80"" style=""flex: 0 0 220px;"">
                                        <button type=""button"" id=""tr-name-add"">Save name</button>
                                    </div>
                                </details>
                                <details id=""tr-gifts-box"" style=""margin: 0 0 10px; font-size: 13px;"">
                                    <summary style=""cursor: pointer;"">Wallets that gave you OPT as a gift <span id=""tr-gift-count"" style=""color: var(--text-secondary);""></span></summary>
                                    <p class=""tr-lede"" style=""margin: 6px 0;"">OPT received from these wallets is treated as a gift: not income, and not node or staking rewards. You take over the giver&#39;s cost basis and holding period, so ask them what they paid (or what it was worth when they earned it) and when, and enter it on each gift in section 3. Applies to all your wallets.</p>
                                    <div id=""tr-gift-list"" style=""display: flex; flex-direction: column; gap: 4px; margin-bottom: 6px;""></div>
                                    <div style=""display: flex; gap: 6px; flex-wrap: wrap; align-items: center;"">
                                        <input type=""text"" id=""tr-gift-address"" placeholder=""optio1... (the wallet the gift came from)"" autocomplete=""off"" spellcheck=""false"" style=""flex: 1 1 340px;"">
                                        <input type=""text"" id=""tr-gift-name"" placeholder=""Who gave it (e.g. Dad)"" autocomplete=""off"" style=""flex: 0 0 200px;"">
                                        <button type=""button"" id=""tr-gift-add"">Add gift wallet</button>
                                    </div>
                                </details>
                                <div id=""tr-status"" style=""color: var(--text-secondary); font-size: 13px; margin-bottom: 12px;"">Builds a report for one tax year: the node and staking rewards the wallet received and their fair market value, the evidence behind that value, and every transfer out with what you sold it for and the gain or loss. The first run can take a few minutes.</div>
                            </div>

                            <div id=""tr-report"" hidden>
                                <div class=""tr-print-only"" id=""tr-print-header"" style=""font-size: 12px; color: var(--text-secondary); margin-bottom: 12px;""></div>

                                <div class=""tr-section"">
                                    <h3>Summary</h3>
                                    <div class=""data-table-wrap pnl-table-wrap"">
                                        <table class=""data-table tr-summary""><tbody id=""tr-summary-body""></tbody></table>
                                    </div>
                                    <div id=""tr-review"" style=""font-size: 13px; margin-top: 8px;""></div>
                                </div>

                                <div class=""tr-section"">
                                    <h3>1. Income: OPT received from nodes and staking</h3>
                                    <p class=""tr-lede"" id=""tr-income-lede""></p>
                                    <h4>Value under each method</h4>
                                    <div class=""data-table-wrap pnl-table-wrap"">
                                        <table class=""data-table"">
                                            <thead><tr><th>Method</th><th style=""text-align: right;"">Node (USD)</th><th style=""text-align: right;"">Staking (USD)</th><th style=""text-align: right;"">Total (USD)</th><th style=""text-align: left;"">How each payout is valued</th></tr></thead>
                                            <tbody id=""tr-methods-body""></tbody>
                                        </table>
                                    </div>
                                    <h4>Month by month</h4>
                                    <div class=""data-table-wrap pnl-table-wrap"">
                                        <table class=""data-table"">
                                            <thead><tr>
                                                <th>Month</th>
                                                <th style=""text-align: right;"">Payouts</th>
                                                <th style=""text-align: right;"">Node (OPT)</th>
                                                <th style=""text-align: right;"">Staking (OPT)</th>
                                                <th style=""text-align: right;"">Days no market</th>
                                                <th style=""text-align: right;"">Quoted value (USD)</th>
                                                <th style=""text-align: right;"">Fair market value (USD)</th>
                                                <th style=""text-align: right;"">FMV ÷ quoted</th>
                                            </tr></thead>
                                            <tbody id=""tr-months-body""></tbody>
                                        </table>
                                    </div>
                                </div>

                                <div class=""tr-section"">
                                    <h3>2. Why the quoted price overstates what OPT was worth</h3>
                                    <p class=""tr-lede"" id=""tr-market-lede""></p>
                                    <ol class=""tr-legal"" id=""tr-market-findings""></ol>
                                    <h4>When Coinstore was closed to the public</h4>
                                    <div class=""data-table-wrap pnl-table-wrap"">
                                        <table class=""data-table"">
                                            <thead><tr>
                                                <th>From</th><th>To</th>
                                                <th style=""text-align: right;"">Days</th>
                                                <th style=""text-align: right;"">Accounts depositing</th>
                                                <th style=""text-align: right;"">You received (OPT)</th>
                                                <th style=""text-align: right;"">Quoted value (USD)</th>
                                                <th style=""text-align: right;"">Fair market value (USD)</th>
                                                <th style=""text-align: right;"">Discount range</th>
                                            </tr></thead>
                                            <tbody id=""tr-closed-body""></tbody>
                                        </table>
                                    </div>
                                    <h4>The market each month</h4>
                                    <div class=""data-table-wrap pnl-table-wrap"">
                                        <table class=""data-table"">
                                            <thead><tr>
                                                <th>Month</th>
                                                <th style=""text-align: right;"">Days open to the public</th>
                                                <th style=""text-align: right;"">Avg quoted price</th>
                                                <th style=""text-align: right;"">Coinstore reported traded (OPT)</th>
                                                <th style=""text-align: right;"">Deposited to Coinstore (OPT)</th>
                                                <th style=""text-align: right;"">Withdrawn from Coinstore (OPT)</th>
                                                <th style=""text-align: right;"">Accounts depositing</th>
                                                <th style=""text-align: right;"">Market could absorb (OPT)</th>
                                                <th style=""text-align: right;"">You received (OPT)</th>
                                            </tr></thead>
                                            <tbody id=""tr-market-body""></tbody>
                                        </table>
                                    </div>
                                    <p class=""tr-lede tr-no-print"" id=""tr-evidence-link"" style=""margin-top: 8px;""></p>
                                </div>

                                <div class=""tr-section"">
                                    <h3 id=""tr-section-3"">3. OPT that left the wallet: sales, node purchases and other transfers</h3>
                                    <div class=""tr-no-print"" style=""display: flex; gap: 16px; align-items: center; flex-wrap: wrap; margin: 4px 0 8px; font-size: 13px;"">
                                        <label style=""display: inline-flex; gap: 6px; align-items: center; cursor: pointer;""><input type=""checkbox"" id=""tr-only-todo""> Show only rows not confirmed yet</label>
                                        <span id=""tr-todo-count""></span>
                                    </div>
                                    <p class=""tr-lede"">Every transfer out of the wallet during the year. Choose what each one was, and for sales enter what you actually received and when (from your exchange trade history or the buyer), with a note of where that figure comes from. Choices and notes are saved on this computer (odata/tax-labels) and print with the report. A deposit to an exchange is not itself a sale: the sale happens when you trade on the exchange, so enter that trade&#39;s date and proceeds. Rows marked <span class=""tr-flag"">Review</span> still have a guessed treatment.</p>
                                    <div class=""data-table-wrap pnl-table-wrap"">
                                        <table class=""data-table"">
                                            <thead><tr>
                                                <th>Sent (UTC)</th>
                                                <th>To</th>
                                                <th style=""text-align: right;"">OPT</th>
                                                <th>What it was</th>
                                                <th style=""text-align: right;"" title=""What you received in USD. Blank = the OPT fair market value that day."">Proceeds (USD)</th>
                                                <th title=""Date of the actual sale, if later than the transfer (for example a trade on the exchange). Blank = the transfer date."">Sale date</th>
                                                <th>Note</th>
                                                <th>Tx</th>
                                            </tr></thead>
                                            <tbody id=""tr-out-body""></tbody>
                                        </table>
                                    </div>
                                    <h4>OPT received other than rewards</h4>
                                    <p class=""tr-lede"">Purchases set the cost of the OPT you later sell. Enter what you paid where you know it; blank = the quoted value when received.</p>
                                    <div class=""data-table-wrap pnl-table-wrap"">
                                        <table class=""data-table"">
                                            <thead><tr>
                                                <th>Received (UTC)</th>
                                                <th>From</th>
                                                <th style=""text-align: right;"">OPT</th>
                                                <th>What it was</th>
                                                <th style=""text-align: right;"" title=""What you paid in USD (purchases), or your basis carried over (own wallet or gift). Blank = quoted value when received; gifts default to $0."">Cost (USD)</th>
                                                <th></th>
                                                <th>Note</th>
                                                <th>Tx</th>
                                            </tr></thead>
                                            <tbody id=""tr-in-body""></tbody>
                                        </table>
                                    </div>
                                </div>

                                <div class=""tr-section"">
                                    <h3>4. Sales and exchanges: gain or loss (for Form 8949)</h3>
                                    <p class=""tr-lede"" id=""tr-sales-lede""></p>
                                    <div class=""data-table-wrap pnl-table-wrap"">
                                        <table class=""data-table"">
                                            <thead><tr>
                                                <th>(a) Description</th>
                                                <th>(b) Acquired</th>
                                                <th>(c) Sold or disposed</th>
                                                <th style=""text-align: right;"">(d) Proceeds</th>
                                                <th style=""text-align: right;"">(e) Cost basis</th>
                                                <th style=""text-align: right;"">(h) Gain or (loss)</th>
                                                <th>Term</th>
                                                <th>What it was</th>
                                                <th>Note</th>
                                            </tr></thead>
                                            <tbody id=""tr-sales-body""></tbody>
                                        </table>
                                    </div>
                                    <div id=""tr-nodes"" hidden>
                                        <h4>Nodes bought with OPT</h4>
                                        <p class=""tr-lede"">Paying for a node with OPT is a disposal of that OPT: the gain or loss is above. The node&#39;s own cost basis is what it was worth when you got it (taken here as the value of the OPT you paid unless you entered another figure). If the node is used in a business that earns the node rewards, its cost may be depreciated or amortized; ask your tax adviser.</p>
                                        <div class=""data-table-wrap pnl-table-wrap"">
                                            <table class=""data-table"">
                                                <thead><tr><th>Bought (UTC)</th><th>Paid to</th><th style=""text-align: right;"">OPT paid</th><th style=""text-align: right;"">Node cost basis (USD)</th><th style=""text-align: right;"">Cost basis of OPT paid</th><th>Note</th></tr></thead>
                                                <tbody id=""tr-nodes-body""></tbody>
                                            </table>
                                        </div>
                                    </div>
                                    <div id=""tr-gifts"" hidden>
                                        <h4>Gifts received (not income)</h4>
                                        <p class=""tr-lede"">A gift is not income to you (IRC §102). When you later sell, your cost basis is the giver&#39;s basis, and your holding period includes theirs (IRC §§1015, 1223). For a loss, the basis is the lower of the giver&#39;s basis and the OPT&#39;s value on the day of the gift, and a sale between the two gives no gain or loss. Gifts over the annual exclusion ($19,000 per giver for 2025) are reported by the giver on Form 709, not by you. An unknown giver basis is counted as $0.</p>
                                        <div class=""data-table-wrap pnl-table-wrap"">
                                            <table class=""data-table"">
                                                <thead><tr><th>Received (UTC)</th><th>From</th><th style=""text-align: right;"">OPT</th><th style=""text-align: right;"">Value on the day (USD)</th><th style=""text-align: right;"">Giver&#39;s basis (USD)</th><th>Giver acquired</th><th>Note</th></tr></thead>
                                                <tbody id=""tr-gifts-body""></tbody>
                                            </table>
                                        </div>
                                    </div>
                                    <div id=""tr-moves"" hidden>
                                        <h4>Gifts, loans and moves (not sales)</h4>
                                        <div class=""data-table-wrap pnl-table-wrap"">
                                            <table class=""data-table"">
                                                <thead><tr><th>Sent (UTC)</th><th>To</th><th style=""text-align: right;"">OPT</th><th>What it was</th><th style=""text-align: right;"">Cost basis carried (USD)</th><th style=""text-align: right;"">Value when sent (USD)</th><th>Note</th></tr></thead>
                                                <tbody id=""tr-moves-body""></tbody>
                                            </table>
                                        </div>
                                    </div>
                                </div>

                                <div class=""tr-section"">
                                    <h3 id=""tr-section-5"">5. Node expenses (IRC §162)</h3>
                                    <p class=""tr-lede"">What it cost to run your nodes this year. If you run nodes as a business, ordinary and necessary costs are deducted from node income on Schedule C (IRC §162), and equipment or node purchases are depreciated or amortized instead of deducted at once (IRC §§167, 168, 179, 197). If it is not a business, these costs are generally not deductible. Enter only the business share of anything you also use personally, and keep the receipts. Expenses paid in OPT (section 3) and network fees are added automatically.</p>
                                    <div class=""tr-no-print"" id=""tr-host-form"" style=""border: 1px solid var(--border); border-radius: 8px; padding: 8px 10px; margin-bottom: 10px; font-size: 13px;"">
                                        <div style=""margin-bottom: 6px;""><strong>Node hosting</strong> <span style=""color: var(--text-secondary);"">Monthly server or VPS cost for your nodes, a deductible ordinary and necessary business expense under IRC §162. Adds one line per tax year covered.</span></div>
                                        <div style=""display: flex; gap: 6px; flex-wrap: wrap; align-items: flex-end;"">
                                            <div class=""form-group"" style=""flex: 1 1 160px; margin-bottom: 0;""><label for=""tr-host-provider"">Provider</label><input type=""text"" id=""tr-host-provider"" placeholder=""e.g. Hetzner, AWS"" autocomplete=""off""></div>
                                            <div class=""form-group"" style=""flex: 0 0 80px; margin-bottom: 0;""><label for=""tr-host-nodes"">Nodes</label><input type=""number"" id=""tr-host-nodes"" min=""1"" step=""1"" value=""1""></div>
                                            <div class=""form-group"" style=""flex: 0 0 150px; margin-bottom: 0;""><label for=""tr-host-cost"">USD per node per month</label><input type=""number"" id=""tr-host-cost"" min=""0"" step=""any"" placeholder=""0.00""></div>
                                            <div class=""form-group"" style=""flex: 0 0 150px; margin-bottom: 0;""><label for=""tr-host-from"">From month</label><input type=""month"" id=""tr-host-from""></div>
                                            <div class=""form-group"" style=""flex: 0 0 150px; margin-bottom: 0;""><label for=""tr-host-to"">To month</label><input type=""month"" id=""tr-host-to""></div>
                                            <div class=""form-group"" style=""flex: 0 0 110px; margin-bottom: 0;"" title=""Share used for the node business; lower it if the server is also used for other things""><label for=""tr-host-share"">Business use %</label><input type=""number"" id=""tr-host-share"" min=""0"" max=""100"" step=""any"" value=""100""></div>
                                            <div class=""form-group"" style=""flex: 1 1 160px; margin-bottom: 0;""><label for=""tr-host-note"">Note / invoices</label><input type=""text"" id=""tr-host-note"" placeholder=""Monthly invoices, card"" autocomplete=""off""></div>
                                            <button type=""button"" id=""tr-host-add"">Add hosting</button>
                                        </div>
                                        <div id=""tr-host-preview"" style=""color: var(--text-secondary); margin-top: 6px;""></div>
                                    </div>
                                    <div class=""tr-no-print"" style=""font-size: 13px; margin: 0 0 4px; font-weight: 600;"">Other expenses</div>
                                    <div class=""tr-no-print"" id=""tr-exp-form"" style=""display: flex; gap: 6px; flex-wrap: wrap; align-items: flex-end; margin-bottom: 8px; font-size: 13px;"">
                                        <div class=""form-group"" style=""flex: 0 0 150px; margin-bottom: 0;""><label for=""tr-exp-date"">Date paid</label><input type=""date"" id=""tr-exp-date""></div>
                                        <div class=""form-group"" style=""flex: 0 0 250px; margin-bottom: 0;""><label for=""tr-exp-cat"">Category</label><select id=""tr-exp-cat""></select></div>
                                        <div class=""form-group"" style=""flex: 1 1 220px; margin-bottom: 0;""><label for=""tr-exp-desc"">Description</label><input type=""text"" id=""tr-exp-desc"" placeholder=""e.g. VPS for 3 nodes, Jan–Dec"" autocomplete=""off""></div>
                                        <div class=""form-group"" style=""flex: 0 0 130px; margin-bottom: 0;""><label for=""tr-exp-usd"">Amount (USD)</label><input type=""number"" id=""tr-exp-usd"" step=""any"" min=""0"" placeholder=""0.00""></div>
                                        <div class=""form-group"" style=""flex: 1 1 200px; margin-bottom: 0;""><label for=""tr-exp-note"">Note / receipt</label><input type=""text"" id=""tr-exp-note"" placeholder=""Invoice #, vendor, card"" autocomplete=""off""></div>
                                        <button type=""button"" id=""tr-exp-add"">Add expense</button>
                                    </div>
                                    <div class=""data-table-wrap pnl-table-wrap"">
                                        <table class=""data-table"">
                                            <thead><tr><th>Date</th><th>Category</th><th>Description</th><th>Treatment</th><th style=""text-align: right;"">Amount (USD)</th><th>Note</th><th class=""tr-no-print""></th></tr></thead>
                                            <tbody id=""tr-exp-body""></tbody>
                                        </table>
                                    </div>
                                </div>

                                <div class=""tr-section"">
                                    <h3>6. Payout schedule</h3>
                                    <p class=""tr-lede"">Each payout is a separate lot: its fair market value is the income reported and becomes the cost basis of those OPT. The holding period starts the day after receipt.</p>
                                    <div class=""tr-no-print"" style=""margin-bottom: 8px; display: flex; gap: 12px;"">
                                        <a href=""#"" id=""tr-income-csv"" class=""header-link"">Download payout schedule (CSV)</a>
                                        <a href=""#"" id=""tr-sales-csv"" class=""header-link"">Download sales (CSV)</a>
                                        <a href=""#"" id=""tr-exp-csv"" class=""header-link"">Download expenses (CSV)</a>
                                    </div>
                                    <div class=""data-table-wrap pnl-table-wrap"">
                                        <table class=""data-table"">
                                            <thead><tr>
                                                <th>Received (UTC)</th>
                                                <th>Type</th>
                                                <th style=""text-align: right;"">OPT</th>
                                                <th style=""text-align: right;"">Quoted price</th>
                                                <th>Market that day</th>
                                                <th style=""text-align: right;"">Discount</th>
                                                <th style=""text-align: right;"">FMV per OPT</th>
                                                <th style=""text-align: right;"">FMV (USD)</th>
                                                <th>Tx</th>
                                            </tr></thead>
                                            <tbody id=""tr-payouts-body""></tbody>
                                        </table>
                                    </div>
                                </div>

                                <div class=""tr-section"">
                                    <h3>7. Method, authority and limits</h3>
                                    <ol class=""tr-legal"" id=""tr-method""></ol>
                                    <p class=""tr-lede"" id=""tr-evidence-files""></p>
                                </div>
                            </div>
                        </div>
                    </div>
                    <script>
                        (() => {
                            const $ = (id) => document.getElementById(id);
                            const EPS = 1e-9;
                            const esc = (v) => String(v ?? '').replace(/[&<>\x22\x27]/g, ch => '&#' + ch.charCodeAt(0) + ';');
                            const fmtOpt = (v) => v == null ? '-' : Number(v).toLocaleString('en-US', { maximumFractionDigits: 2 });
                            const fmtUsd = (v) => v == null ? '-' : (v < -0.004 ? '-$' : '$') + Math.abs(Number(v)).toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
                            const fmtGain = (v) => v == null ? '-' : v < -0.004 ? '(' + fmtUsd(-v) + ')' : fmtUsd(v);
                            const fmtPrice = (v) => v == null ? '-' : '$' + Number(v).toLocaleString('en-US', { minimumFractionDigits: 6, maximumFractionDigits: 6 });
                            const fmtPct = (v) => v == null || !isFinite(v) ? '-' : (v * 100).toLocaleString('en-US', { maximumFractionDigits: 1 }) + '%';
                            const fmtTime = (v) => String(v || '').replace('T', ' ').replace(/\.\d+/, '').replace('Z', '').slice(0, 16);
                            const fmtDay = (v) => String(v || '').slice(0, 10);
                            const fmtDate = (d) => d ? new Date(String(d).slice(0, 10) + 'T00:00:00Z').toLocaleDateString('en-US', { year: 'numeric', month: 'long', day: 'numeric', timeZone: 'UTC' }) : '-';
                            const fmtMonth = (m) => new Date(m + '-01T00:00:00Z').toLocaleDateString('en-US', { year: 'numeric', month: 'short', timeZone: 'UTC' });
                            const short = (a) => a && a.length > 20 ? a.slice(0, 10) + '…' + a.slice(-6) : (a || '');
                            const sum = (xs, f) => xs.reduce((s, x) => s + (f(x) || 0), 0);

                            const form = $('tr-form'), submit = $('tr-submit'), status = $('tr-status'), report = $('tr-report');
                            const addressInput = $('tr-address'), yearSelect = $('tr-year'), typeSelect = $('tr-type'), ruleSelect = $('tr-rule'), blockageInput = $('tr-blockage');
                            const params = new URLSearchParams(location.search);
                            const currentYear = new Date().getUTCFullYear();
                            yearSelect.innerHTML = Array.from({ length: currentYear - 2023 }, (_, i) => currentYear - i).map(y => `<option value=""${y}"">${y}</option>`).join('');
                            yearSelect.value = String(currentYear - 1);
                            for (const [el, key] of [[yearSelect, 'year'], [typeSelect, 'type'], [ruleSelect, 'rule']]) {
                                const v = params.get(key);
                                if (v != null && [...el.options].some(o => o.value === v)) el.value = v;
                            }
                            if (params.get('blockage')) blockageInput.value = params.get('blockage');
                            addressInput.value = params.get('address') || window.opticWallets.primary() || '';

                            // Treatments for transfers. disposal = taxable (gain or loss); carry = leaves with its basis; skip = left out.
                            const OUT_KINDS = {
                                'exchange-sale': { label: 'Deposited to exchange and sold there', disposal: true },
                                'sale': { label: 'Sold to a person (paid in cash or other crypto)', disposal: true },
                                'node': { label: 'Bought a node with it', disposal: true },
                                'node-expense': { label: 'Paid a node expense with it (hosting etc.)', disposal: true },
                                'payment': { label: 'Paid for something else (personal)', disposal: true },
                                'worthless': { label: 'Lost / stuck on exchange (claim as loss)', disposal: true },
                                'exchange-held': { label: 'Deposited to exchange, not sold', carry: true },
                                'own': { label: 'Moved to my own wallet', carry: true },
                                'gift': { label: 'Gift', carry: true },
                                'loan': { label: 'Loan (expected back)', skip: true },
                                'ignore': { label: 'Leave out', skip: true },
                            };
                            const IN_KINDS = {
                                'purchase': { label: 'Bought (on an exchange or from a person)' },
                                'own': { label: 'From my own wallet or exchange account' },
                                'gift-in': { label: 'Gift received' },
                                'income': { label: 'Other income (payment received)' },
                                'loan-back': { label: 'Loan repaid to me', skip: true },
                                'ignore': { label: 'Leave out', skip: true },
                            };

                            let data = null, labels = {};
                            const keyOf = (e) => `${e.txHash}|${e.direction}|${e.counterparty}`;

                            const ownWallets = () => new Set(window.opticWallets.list().map(w => w.a));
                            const isExchange = (a) => !!window.opticWallets.exchange(a) || a === data.flows.depositCollector || a === data.flows.hotWallet;
                            const exchangeName = (a) => window.opticWallets.exchange(a)?.n || (a === data.flows.depositCollector || a === data.flows.hotWallet ? 'Coinstore' : null);
                            // Small button that copies the full address or hash behind a shortened one.
                            const copyBtn = (v) => `<button type=""button"" class=""tr-copy"" data-copy=""${esc(v)}"" title=""Copy ${esc(v)}"">Copy</button>`;
                            const txCell = (h) => `<td class=""address-cell"" style=""white-space: nowrap;"" title=""${esc(h)}"">${esc(String(h).slice(0, 10))}…${copyBtn(h)}</td>`;
                            let names = {};
                            const nameBtn = (a) => `<button type=""button"" class=""tr-name"" data-name-addr=""${esc(a)}"" title=""${names[a] ? 'Rename' : 'Name'} this address"">${names[a] ? 'Rename' : 'Name'}</button>`;
                            const partyName = (a) => `<span style=""white-space: nowrap;"">${partyLabel(a)}${copyBtn(a)}${nameBtn(a)}</span>`;
                            // Plain-text name for CSVs and descriptions.
                            const displayName = (a) => names[a] || giftSenders[a] || exchangeName(a) || window.opticWallets.label(a) || short(a);
                            const partyLabel = (a) => {
                                const ex = exchangeName(a);
                                if (ex) return `<span title=""${esc(a)}"">Exchange · ${esc(ex)}</span>`;
                                const own = window.opticWallets.label(a);
                                if (giftSenders[a] != null) return `<span title=""${esc(a)}"">Gift · ${esc(names[a] || giftSenders[a] || short(a))}</span>`;
                                if (ownWallets().has(a)) return `<span title=""${esc(a)}"">My wallet${own ? ' · ' + esc(own) : ''}</span>`;
                                if (names[a]) return `<span title=""${esc(a)}""><strong>${esc(names[a])}</strong> <span class=""address-cell"" style=""color: var(--text-secondary);"">${esc(short(a))}</span></span>`;
                                return `<span class=""address-cell"" title=""${esc(a)}"">${esc(short(a))}</span>`;
                            };
                            // Guessed treatment when the user hasn't chosen one: { kind, sure }.
                            const defaultOut = (e) => {
                                if (ownWallets().has(e.counterparty)) return { kind: 'own', sure: true };
                                if (isExchange(e.counterparty)) return { kind: 'exchange-sale', sure: false };
                                if (/\bgift/i.test(e.memo || '')) return { kind: 'gift', sure: false };
                                if (/\bloan/i.test(e.memo || '')) return { kind: 'loan', sure: false };
                                if (/\bnode/i.test(e.memo || '')) return { kind: 'node', sure: false };
                                return { kind: 'sale', sure: false };
                            };
                            let giftSenders = {};
                            // Node expense categories. capital = depreciated or amortized, not deducted at once.
                            const EXP_CATS = {
                                hosting: { label: 'Server, hosting or VPS' },
                                electricity: { label: 'Electricity' },
                                internet: { label: 'Internet' },
                                software: { label: 'Software and subscriptions' },
                                fees: { label: 'Transaction or network fees (cash)' },
                                professional: { label: 'Accounting, tax or legal' },
                                other: { label: 'Other node expense' },
                                node: { label: 'Node purchase or license', capital: true },
                                equipment: { label: 'Computer equipment', capital: true },
                            };
                            $('tr-exp-cat').innerHTML = Object.entries(EXP_CATS).map(([k, v]) => `<option value=""${k}"">${esc(v.label)}${v.capital ? ' (depreciate)' : ''}</option>`).join('');
                            // Your entries for the year plus the node costs found on chain: OPT paid for expenses and nodes, and fees.
                            const expenseRows = (a) => {
                                const own = (labels._expenses || []).filter(x => String(x.date || '').startsWith(a.year)).map(x => ({ ...x, cat: EXP_CATS[x.category] || EXP_CATS.other, mine: true }));
                                const paidOpt = a.sales.filter(s => s.t.kind === 'node-expense' || s.t.kind === 'node').map(s => ({
                                    date: s.soldOn, cat: s.t.kind === 'node' ? EXP_CATS.node : EXP_CATS.other,
                                    description: `${fmtOpt(s.qty)} OPT paid to ${displayName(s.e.counterparty)} (section 3)`, usd: s.proceeds, note: s.t.note, auto: true }));
                                const fees = a.feesYear.length ? [{ date: a.year + '-12-31', cat: { label: 'Network fees paid in OPT' }, description: `${a.feesYear.length} transactions, ${fmtOpt(sum(a.feesYear, f => f.e.amountOpt))} OPT at its value when paid`, usd: sum(a.feesYear, f => f.value), note: '', auto: true }] : [];
                                return own.concat(paidOpt, fees).sort((x, y) => String(x.date).localeCompare(String(y.date)));
                            };
                            const defaultIn = (e) => {
                                if (giftSenders[e.counterparty] != null) return { kind: 'gift-in', sure: true };
                                if (ownWallets().has(e.counterparty)) return { kind: 'own', sure: true };
                                if (isExchange(e.counterparty)) return { kind: 'purchase', sure: false };
                                return { kind: 'purchase', sure: false };
                            };
                            const treatment = (e) => {
                                const l = labels[keyOf(e)] || {};
                                const kinds = e.direction === 'out' ? OUT_KINDS : IN_KINDS;
                                const def = e.direction === 'out' ? defaultOut(e) : defaultIn(e);
                                const kind = kinds[l.kind] ? l.kind : def.kind;
                                const usd = l.usd === '' || l.usd == null || !isFinite(Number(l.usd)) ? null : Number(l.usd);
                                return { kind, info: kinds[kind], usd, date: l.date || null, note: l.note || '', reviewed: def.sure || Object.keys(l).length > 0, auto: def.sure && !Object.keys(l).length };
                            };

                            // ---------------------------------------------------------------- valuation
                            const build = () => {
                                const { market, flows, dist, ledger } = data;
                                const year = yearSelect.value, rule = ruleSelect.value;
                                const blockage = Math.min(100, Math.max(0, Number(blockageInput.value) || 0)) / 100;
                                const yStart = year + '-01-01', yEnd = year + '-12-31';
                                const inYear = (t) => String(t).startsWith(year);

                                const days = window.opticMarket.daily(market, flows, 'withdrawals', 0.1);
                                const dayBy = new Map(days.map(x => [x.d, x]));
                                const open = window.opticMarket.publicOpen(days, flows);
                                const openBy = new Map(days.map((x, i) => [x.d, open[i]]));
                                const accSel = window.opticMarket.accessible(days, flows, rule);
                                const methods = { quotedAcc: window.opticMarket.accessible(days, flows, 'quoted'), next: window.opticMarket.accessible(days, flows, 'next'), dlom: rule === 'dlom' ? accSel : window.opticMarket.accessible(days, flows, 'dlom') };

                                // Kind of each node-distribution payout, from the distributions report.
                                const kindBy = new Map();
                                for (const r of dist.rows) kindBy.set(r.txHash, { kind: r.kind, inferred: r.kindInferred });

                                // Income events: node distributions (node or staking per the report) and chain staking rewards.
                                const entries = ledger.entries;
                                const income = [];
                                for (const e of entries) {
                                    if (e.direction !== 'in') continue;
                                    if (e.category === 'distribution') {
                                        const k = kindBy.get(e.txHash) || { kind: 'node', inferred: true };
                                        income.push({ e, kind: k.kind, inferred: k.inferred });
                                    } else if (e.category === 'reward') income.push({ e, kind: 'staking', inferred: false, module: true });
                                    else if (e.category === 'wallet' && treatment(e).kind === 'income') income.push({ e, kind: 'other', inferred: false });
                                }
                                const dayTotal = new Map();
                                for (const x of income) { const d = fmtDay(x.e.timeUtc); dayTotal.set(d, (dayTotal.get(d) || 0) + x.e.amountOpt); }
                                // Fair market value of qty OPT received on day d under a pricing map, with the blockage discount
                                // on the share of the day\x27s receipts above what the market could absorb.
                                const valueAt = (accMap, d, qty, applyBlockage) => {
                                    const a = accMap.get(d);
                                    if (!a || a.price == null) return null;
                                    const cap = dayBy.get(d)?.absorb ?? 0, total = dayTotal.get(d) || qty;
                                    const excess = applyBlockage && total > cap ? qty * (total - cap) / total : 0;
                                    return { a, price: a.price, excess, value: qty * a.price - excess * a.price * blockage };
                                };
                                for (const x of income) {
                                    const d = fmtDay(x.e.timeUtc);
                                    x.d = d;
                                    x.quoted = x.e.priceUsd != null ? x.e.amountOpt * x.e.priceUsd : null;
                                    x.fmv = valueAt(accSel, d, x.e.amountOpt, true);
                                    x.alt = { quotedAcc: valueAt(methods.quotedAcc, d, x.e.amountOpt, false)?.value ?? null, next: valueAt(methods.next, d, x.e.amountOpt, false)?.value ?? null, dlom: valueAt(methods.dlom, d, x.e.amountOpt, false)?.value ?? null };
                                    x.open = openBy.get(d) ?? false;
                                    x.noMarket = !x.fmv || String(x.fmv.a.source).startsWith('No market');
                                }
                                const incomeBy = new Map(income.map(x => [keyOf(x.e), x]));
                                // Price per OPT used for proceeds and transfers when the user gave none: the same fair market
                                // value used for income that day, else the quoted price at the time.
                                const unitPrice = (e) => accSel.get(fmtDay(e.timeUtc))?.price ?? e.priceUsd ?? null;

                                // ---------------------------------------------------------------- FIFO through the end of the year
                                const lots = [];
                                const sales = [], carries = [], skipped = [], transfersOut = [], transfersIn = [], gifts = [], feesYear = [];
                                let feeOpt = 0;
                                const take = (qty) => {
                                    const parts = [];
                                    let left = qty;
                                    while (left > EPS && lots.length) {
                                        const lot = lots[0], q = Math.min(lot.qty, left);
                                        parts.push({ qty: q, time: lot.time, cost: lot.unit == null ? null : q * lot.unit, lossCost: lot.lossUnit == null ? (lot.unit == null ? null : q * lot.unit) : q * lot.lossUnit, source: lot.source });
                                        lot.qty -= q; left -= q;
                                        if (lot.qty <= EPS) lots.shift();
                                    }
                                    if (left > EPS) parts.push({ qty: left, time: null, cost: null, lossCost: null, source: 'none' });
                                    return parts;
                                };
                                for (const e of entries) {
                                    if (fmtDay(e.timeUtc) > yEnd) break;
                                    const t = e.category === 'wallet' ? treatment(e) : null;
                                    if (e.direction === 'in') {
                                        const inc = incomeBy.get(keyOf(e));
                                        if (inc) { lots.push({ qty: e.amountOpt, unit: inc.fmv ? inc.fmv.value / e.amountOpt : null, time: e.timeUtc, source: 'income' }); continue; }
                                        if (e.category !== 'wallet') continue; // module receipts return your own staked tokens
                                        if (inYear(e.timeUtc)) transfersIn.push({ e, t });
                                        if (t.info.skip) continue;
                                        const quotedValue = e.priceUsd != null ? e.amountOpt * e.priceUsd : null;
                                        if (t.kind === 'gift-in') {
                                            // Giver's basis for gains; the lower of that and the value on the day for losses.
                                            // The holding period runs from when the giver acquired it, if entered.
                                            const unit = unitPrice(e), dayValue = unit == null ? null : e.amountOpt * unit;
                                            const basis = t.usd ?? 0;
                                            const lossBasis = dayValue == null ? basis : Math.min(basis, dayValue);
                                            lots.push({ qty: e.amountOpt, unit: basis / e.amountOpt, lossUnit: lossBasis / e.amountOpt, time: t.date ? t.date + 'T00:00:00Z' : e.timeUtc, source: 'gift' });
                                            if (inYear(e.timeUtc)) gifts.push({ e, t, dayValue, basis, basisKnown: t.usd != null });
                                            continue;
                                        }
                                        const cost = t.usd != null ? t.usd : quotedValue;
                                        lots.push({ qty: e.amountOpt, unit: cost == null ? null : cost / e.amountOpt, time: e.timeUtc, source: t.kind, estimated: t.usd == null });
                                        continue;
                                    }
                                    if (e.category === 'module') continue; // delegation or lock: still yours
                                    if (e.category === 'fee') { feeOpt += e.amountOpt; take(e.amountOpt); if (inYear(e.timeUtc)) feesYear.push({ e, value: unitPrice(e) == null ? null : e.amountOpt * unitPrice(e) }); continue; }
                                    // Shown in the year it was sent, and in the year of its sale date if that is later.
                                    const counts = inYear(e.timeUtc) || (t.date && inYear(t.date));
                                    if (counts) transfersOut.push({ e, t });
                                    if (t.info.skip) { if (counts) skipped.push({ e, t }); continue; }
                                    const parts = take(e.amountOpt);
                                    const cost = parts.some(p => p.cost == null) ? (parts.every(p => p.cost == null) ? null : sum(parts, p => p.cost)) : sum(parts, p => p.cost);
                                    const unit = unitPrice(e);
                                    const valueWhenSent = unit == null ? null : e.amountOpt * unit;
                                    if (!inYear(e.timeUtc) && !(t.date && inYear(t.date))) continue;
                                    if (t.info.disposal) {
                                        const proceeds = t.kind === 'worthless' ? (t.usd ?? 0) : (t.usd ?? valueWhenSent);
                                        const soldOn = t.date || fmtDay(e.timeUtc);
                                        if (!String(soldOn).startsWith(year)) continue;
                                        // Split by holding period: more than one year = long term.
                                        const soldMs = Date.parse(soldOn + 'T23:59:59Z');
                                        const isLong = (p) => p.time != null && (soldMs - Date.parse(p.time)) / 86400000 > 365;
                                        const groups = [parts.filter(p => !isLong(p)), parts.filter(isLong)].filter(g => g.length);
                                        for (const g of groups) {
                                            const qty = sum(g, p => p.qty);
                                            const share = qty / e.amountOpt;
                                            const gCost = g.every(p => p.cost == null) ? null : sum(g, p => p.cost);
                                            const dated = g.filter(p => p.time).map(p => fmtDay(p.time));
                                            // Per lot: gain above the gain basis, loss below the loss basis (they differ only for gifts), else none.
                                            const gain = proceeds == null || gCost == null ? null : sum(g, p => {
                                                const pr = proceeds * p.qty / e.amountOpt, c = p.cost ?? 0, l = p.lossCost ?? c;
                                                return pr > c ? pr - c : pr < l ? pr - l : 0;
                                            });
                                            sales.push({ e, t, qty, soldOn, proceeds: proceeds == null ? null : proceeds * share, cost: gCost, gain, fromGift: g.some(p => p.source === 'gift'),
                                                long: isLong(g[0]), acquired: dated.length === 0 ? 'Unknown' : dated[0] === dated[dated.length - 1] ? dated[0] : 'Various', acqFrom: dated[0], acqTo: dated[dated.length - 1],
                                                unknownBasis: g.some(p => p.cost == null), proceedsEstimated: t.usd == null && t.kind !== 'worthless', valueWhenSent: valueWhenSent == null ? null : valueWhenSent * share });
                                        }
                                    } else {
                                        carries.push({ e, t, cost, valueWhenSent });
                                    }
                                }
                                const held = sum(lots, l => l.qty), heldCost = sum(lots, l => l.unit == null ? 0 : l.qty * l.unit);

                                // ---------------------------------------------------------------- the year\x27s market
                                const sel = days.filter(x => x.d >= yStart && x.d <= yEnd);
                                const incomeYear = income.filter(x => inYear(x.e.timeUtc));
                                const youBy = new Map();
                                for (const x of incomeYear) youBy.set(x.d, (youBy.get(x.d) || 0) + x.e.amountOpt);
                                const gaps = window.opticMarket.closedPeriods(days, flows)
                                    .filter(g => g.from <= yEnd && g.to >= yStart)
                                    .map(g => {
                                        const from = g.from < yStart ? yStart : g.from, to = g.to > yEnd ? yEnd : g.to;
                                        const span = sel.filter(x => x.d >= from && x.d <= to);
                                        const inc = incomeYear.filter(x => x.d >= from && x.d <= to);
                                        const discounts = inc.map(x => x.fmv?.a.discount).filter(v => v != null);
                                        return { from, to, cutFrom: g.from < yStart, cutTo: g.to > yEnd || g.open, days: span.length, accounts: new Set(span.flatMap(x => x.depositAccounts)).size,
                                            you: sum(inc, x => x.e.amountOpt), quoted: sum(inc, x => x.quoted), fmv: sum(inc, x => x.fmv?.value),
                                            dMin: discounts.length ? Math.min(...discounts) : null, dMax: discounts.length ? Math.max(...discounts) : null };
                                    });
                                const months = [...new Set(sel.map(x => x.d.slice(0, 7)))].map(m => {
                                    const xs = sel.filter(x => x.d.startsWith(m));
                                    const prices = xs.map(x => x.cmcClose ?? x.csClose).filter(v => v != null);
                                    return { m, days: xs.length, open: xs.filter(x => openBy.get(x.d)).length, price: prices.length ? sum(prices, v => v) / prices.length : null,
                                        reported: sum(xs, x => x.csReported), deposit: sum(xs, x => x.deposit), withdraw: sum(xs, x => x.withdraw),
                                        accounts: new Set(xs.flatMap(x => x.depositAccounts)).size, absorb: sum(xs, x => x.absorb), you: sum(xs, x => youBy.get(x.d) || 0) };
                                });
                                return { feesYear, gifts, accPrice: unitPrice, year, rule, blockage, days, sel, income, incomeYear, sales, carries, skipped, transfersOut, transfersIn, held, heldCost, lots, feeOpt, gaps, months, openBy };
                            };

                            // ---------------------------------------------------------------- render
                            const typeLabel = (x) => x.kind === 'staking' ? (x.module ? 'Staking (chain rewards)' : 'Staking / participation') : x.kind === 'other' ? 'Other income' : 'Node';
                            const RULE_TEXT = {
                                dlom: 'Coinstore\x27s daily close (CoinMarketCap\x27s once Coinstore stopped trading) less a discount for lack of marketability, sized by the Finnerty model to the number of days until you could next sell and to OPT\x27s trailing 90-day price volatility',
                                quoted: 'the quoted daily price, with no discount',
                                next: 'the price on the next day you could actually sell',
                                last: 'the price on the last day you could actually sell',
                            };

                            const editCells = (x, kinds) => {
                                const key = keyOf(x.e);
                                const opts = Object.entries(kinds).map(([k, v]) => `<option value=""${k}""${k === x.t.kind ? ' selected' : ''}>${esc(v.label)}</option>`).join('');
                                const dateCell = x.e.direction === 'out' || x.t.kind === 'gift-in'
                                    ? `<td class=""tr-edit""><input type=""date"" class=""tr-date"" data-key=""${esc(key)}"" data-field=""date"" value=""${esc(x.t.date || '')}"" title=""${x.e.direction === 'out' ? 'Date of the actual sale' : 'When the giver acquired this OPT (your holding period includes theirs)'}""></td>`
                                    : '<td></td>';
                                return `<td class=""tr-edit"">${x.t.reviewed
                                    ? `<div class=""tr-no-print""><span class=""tr-badge ok"" title=""${x.t.auto ? 'Set automatically (your own wallet or a gift wallet)' : 'You chose or confirmed this'}"">✓ ${x.t.auto ? 'Automatic' : 'Confirmed'}</span></div>`
                                    : `<div class=""tr-no-print""><span class=""tr-badge todo"">Not confirmed: guessed</span> <button type=""button"" class=""tr-confirm"" data-confirm-key=""${esc(key)}"" title=""The choice shown is right"" style=""font-size: 11px; padding: 1px 8px;"">Confirm</button></div>`}<select data-key=""${esc(key)}"" data-field=""kind"">${opts}</select></td>
                                    <td class=""tr-edit amount-cell""><input type=""number"" step=""any"" class=""tr-usd"" data-key=""${esc(key)}"" data-field=""usd"" value=""${x.t.usd ?? ''}"" placeholder=""${esc(x.placeholder ?? '')}""></td>
                                    ${dateCell}
                                    <td class=""tr-edit""><textarea data-key=""${esc(key)}"" data-field=""note"" placeholder=""e.g. Sold on Coinstore 2025-07-03, trade #…"">${esc(x.t.note)}</textarea><div class=""tr-note-print"">${esc(x.t.note)}</div></td>`;
                            };

                            const render = () => {
                                const a = build();
                                const { dist, ledger, flows, market } = data;
                                const type = typeSelect.value;
                                const shown = a.incomeYear.filter(x => !type || x.kind === type || (type === 'staking' && x.kind === 'staking'));
                                const byKind = (k) => a.incomeYear.filter(x => x.kind === k);
                                const node = byKind('node'), staking = byKind('staking'), other = byKind('other');
                                const fmvOf = (xs) => sum(xs, x => x.fmv?.value);
                                const quotedOf = (xs) => sum(xs, x => x.quoted);
                                const noFmv = shown.filter(x => !x.fmv).length;
                                const st = a.sales.filter(s => !s.long), lt = a.sales.filter(s => s.long);
                                const review = a.transfersOut.filter(x => !x.t.reviewed).length + a.transfersIn.filter(x => !x.t.reviewed).length;

                                $('tr-print-header').innerHTML = `<strong>Tax year ${esc(a.year)}</strong> · Wallet ${esc(dist.address)} · Prepared ${esc(new Date().toISOString().slice(0, 16).replace('T', ' '))} UTC with OPTIC · Days with no market: ${esc(RULE_TEXT[a.rule])}${a.blockage ? ` · Blockage discount ${fmtPct(a.blockage)} above market capacity` : ''}`;

                                const row = (label, opt, usd, sub, strong) => `<tr${strong ? ' class=""tr-total""' : ''}><td>${label}</td><td class=""amount-cell"">${opt}</td><td class=""amount-cell"">${usd}</td><td style=""text-align: left; color: var(--text-secondary); font-size: 12px;"">${sub || ''}</td></tr>`;
                                const salesGain = (xs) => sum(xs, s => s.gain);
                                $('tr-summary-body').innerHTML =
                                    '<tr><th style=""text-align: left;"">Item</th><th style=""text-align: right;"">OPT</th><th style=""text-align: right;"">USD</th><th style=""text-align: left;"">Where it goes</th></tr>' +
                                    row('Node rewards received', fmtOpt(sum(node, x => x.e.amountOpt)), fmtUsd(fmvOf(node)), `${node.length} payouts · quoted ${fmtUsd(quotedOf(node))}. Ordinary income (Schedule C if you run nodes as a business, otherwise Schedule 1)`) +
                                    row('Staking / participation rewards received', fmtOpt(sum(staking, x => x.e.amountOpt)), fmtUsd(fmvOf(staking)), `${staking.length} payouts · quoted ${fmtUsd(quotedOf(staking))}. Ordinary income (Schedule 1, line 8v)`) +
                                    (other.length ? row('Other OPT income (marked by you)', fmtOpt(sum(other, x => x.e.amountOpt)), fmtUsd(fmvOf(other)), `${other.length} receipts`) : '') +
                                    row('Total income at fair market value', fmtOpt(sum(a.incomeYear, x => x.e.amountOpt)), fmtUsd(fmvOf(a.incomeYear)), `Quoted price would give ${fmtUsd(quotedOf(a.incomeYear))}; difference ${fmtUsd(quotedOf(a.incomeYear) - fmvOf(a.incomeYear))}`, true) +
                                    (() => {
                                        const ex = expenseRows(a), ded = sum(ex.filter(x => !x.cat.capital), x => x.usd), cap = sum(ex.filter(x => x.cat.capital), x => x.usd);
                                        if (!ex.length) return row('Node expenses', '', fmtUsd(0), 'None entered (section 5)');
                                        return row('Node expenses you can deduct', '', fmtUsd(ded), 'Schedule C if you run nodes as a business (section 5)') +
                                            (cap ? row('Node and equipment costs to depreciate', '', fmtUsd(cap), 'Not deducted at once: Form 4562; your adviser picks the method') : '') +
                                            row('Node income less deductible expenses', '', fmtUsd(fmvOf(node) - ded), 'Before depreciation and self-employment tax', true);
                                    })() +
                                    (a.gifts.length ? row('Gifts received (not income)', fmtOpt(sum(a.gifts, g => g.e.amountOpt)), fmtUsd(sum(a.gifts, g => g.dayValue)), `${a.gifts.length} gift(s), value on the day. Not taxable to you; carried at the giver\x27s basis (${fmtUsd(sum(a.gifts, g => g.basis))}${a.gifts.some(g => !g.basisKnown) ? ', some unknown and counted as $0' : ''})`) : '') +
                                    row('Short-term sales (held 1 year or less)', fmtOpt(sum(st, s => s.qty)), fmtGain(salesGain(st)), `Proceeds ${fmtUsd(sum(st, s => s.proceeds))}, cost ${fmtUsd(sum(st, s => s.cost))}. Form 8949 Part I`) +
                                    row('Long-term sales (held over 1 year)', fmtOpt(sum(lt, s => s.qty)), fmtGain(salesGain(lt)), `Proceeds ${fmtUsd(sum(lt, s => s.proceeds))}, cost ${fmtUsd(sum(lt, s => s.cost))}. Form 8949 Part II`) +
                                    row('Net capital gain or (loss)', fmtOpt(sum(a.sales, s => s.qty)), fmtGain(salesGain(a.sales)), 'Schedule D. Net losses offset other income up to $3,000 a year; the rest carries forward', true) +
                                    row(`Held at the end of ${esc(a.year)}`, fmtOpt(a.held), fmtUsd(a.heldCost), 'Cost basis of OPT still in this wallet (FIFO)');
                                $('tr-review').innerHTML = (review ? `<span class=""tr-flag"">${review} transfer(s) still need you to confirm what they were</span> (section 3): pick what each one was, or press Confirm if the choice shown is right. <a href=""#tr-section-3"" id=""tr-show-todo"" class=""tr-no-print"">Show them</a> <button type=""button"" class=""tr-no-print"" id=""tr-confirm-all"" style=""font-size: 11px; padding: 2px 8px;"">Confirm all ${review} as shown</button> ` : '') +
                                    (noFmv ? `<span class=""tr-flag"">${noFmv} payout(s) have no market price at all</span> (before OPT first traded); they are left out of the totals. ` : '') +
                                    (ledger.truncated ? '<span class=""tr-flag"">The wallet history hit the 20,000-transaction cap; older transfers may be missing.</span>' : '');

                                // Income.
                                const closedIncome = shown.filter(x => x.noMarket);
                                $('tr-income-lede').innerHTML = `${fmtOpt(sum(shown, x => x.e.amountOpt))} OPT in ${shown.length.toLocaleString('en-US')} payouts during ${esc(a.year)}. Each payout is income on the day it reached the wallet, valued at what the OPT could have been sold for that day: Coinstore\x27s daily close on days the public could deposit OPT there, the average price of Osmosis swaps on days Coinstore was closed, and on days neither was available (${closedIncome.length.toLocaleString('en-US')} payouts, ${fmtOpt(sum(closedIncome, x => x.e.amountOpt))} OPT) ${esc(RULE_TEXT[a.rule])}.`;
                                const meth = (label, f, how, chosen) => {
                                    const v = (xs) => sum(xs, f);
                                    return `<tr${chosen ? ' class=""tr-total""' : ''}><td>${label}${chosen ? ' <span style=""font-weight: 400; color: var(--text-secondary);"">(used in this report)</span>' : ''}</td><td class=""amount-cell"">${fmtUsd(v(node))}</td><td class=""amount-cell"">${fmtUsd(v(staking))}</td><td class=""amount-cell"">${fmtUsd(v(a.incomeYear))}</td><td style=""text-align: left; font-size: 12px; color: var(--text-secondary);"">${how}</td></tr>`;
                                };
                                $('tr-methods-body').innerHTML =
                                    meth('Quoted price', x => x.quoted, 'CoinMarketCap OPT/USD close for the hour of each payout. The IRS accepts a price from a published source, but it says nothing about whether you could sell at it.') +
                                    meth('Accessible market, quoted when closed', x => x.alt.quotedAcc, 'Price on the market you could reach that day; on days no market was open, the quoted price with no discount.', a.rule === 'quoted' && !a.blockage) +
                                    meth('Accessible market, marketability discount when closed', x => x.alt.dlom, 'Coinstore\x27s close on every day, the same exchange and the same records throughout; on days the public could not reach it, less a Finnerty-model marketability discount for the days until you could sell.', a.rule === 'dlom' && !a.blockage) +
                                    meth('Accessible market, next sellable price when closed', x => x.alt.next, 'On days no market was open, the price on the next day you could sell. Uses a later price, so it is the easiest to challenge.', a.rule === 'next' && !a.blockage) +
                                    (a.blockage || a.rule === 'last' ? meth('This report', x => x.fmv?.value, `${esc(RULE_TEXT[a.rule])}${a.blockage ? `, then ${fmtPct(a.blockage)} off the OPT above what the market could absorb each day` : ''}.`, true) : '');
                                const incMonths = [...new Set(shown.map(x => x.d.slice(0, 7)))].sort();
                                $('tr-months-body').innerHTML = incMonths.map(m => {
                                    const xs = shown.filter(x => x.d.startsWith(m));
                                    const q = sum(xs, x => x.quoted), f = sum(xs, x => x.fmv?.value);
                                    return `<tr><td>${fmtMonth(m)}</td><td class=""amount-cell"">${xs.length}</td><td class=""amount-cell"">${fmtOpt(sum(xs.filter(x => x.kind === 'node'), x => x.e.amountOpt))}</td><td class=""amount-cell"">${fmtOpt(sum(xs.filter(x => x.kind !== 'node'), x => x.e.amountOpt))}</td>
                                        <td class=""amount-cell"">${new Set(xs.filter(x => x.noMarket).map(x => x.d)).size}</td><td class=""amount-cell"">${fmtUsd(q)}</td><td class=""amount-cell"">${fmtUsd(f)}</td><td class=""amount-cell"">${q > 0 ? fmtPct(f / q) : '-'}</td></tr>`;
                                }).join('') + `<tr class=""tr-total""><td>${esc(a.year)}</td><td class=""amount-cell"">${shown.length}</td><td class=""amount-cell"">${fmtOpt(sum(shown.filter(x => x.kind === 'node'), x => x.e.amountOpt))}</td><td class=""amount-cell"">${fmtOpt(sum(shown.filter(x => x.kind !== 'node'), x => x.e.amountOpt))}</td><td class=""amount-cell"">${new Set(closedIncome.map(x => x.d)).size}</td><td class=""amount-cell"">${fmtUsd(quotedOf(shown))}</td><td class=""amount-cell"">${fmtUsd(fmvOf(shown))}</td><td class=""amount-cell"">${quotedOf(shown) > 0 ? fmtPct(fmvOf(shown) / quotedOf(shown)) : '-'}</td></tr>`;

                                // Market evidence, this year only.
                                const closedDays = a.sel.filter(x => !a.openBy.get(x.d)).length, firstDay = a.sel[0]?.d;
                                const reported = sum(a.months, m => m.reported), deposited = sum(a.months, m => m.deposit), withdrawn = sum(a.months, m => m.withdraw);
                                const accountsYear = new Set(a.sel.flatMap(x => x.depositAccounts)).size;
                                const avgAbsorb = a.sel.length ? sum(a.sel, x => x.absorb) / a.sel.length : 0;
                                const payoutDays = [...new Set(a.incomeYear.map(x => x.d))];
                                const overDays = payoutDays.filter(d => (a.days.find(x => x.d === d)?.absorb ?? 0) < sum(a.incomeYear.filter(x => x.d === d), x => x.e.amountOpt)).length;
                                const lastDep = flows.lastDeposit ? fmtDay(flows.lastDeposit) : null;
                                $('tr-market-lede').textContent = `Everything here is from ${a.year} only, read from public records: Coinstore deposits and withdrawals on the Optio chain, Coinstore\x27s own trade history, CoinMarketCap and every OPT swap on Osmosis.`;
                                const findings = [
                                    `<strong>Coinstore was closed to the public on ${closedDays} of the ${a.sel.length} days of ${esc(a.year)}${firstDay && !firstDay.endsWith('-01-01') ? ` from ${fmtDate(firstDay)}, when its records begin` : ''}</strong> (counted from the chain: open means at least 5 different Coinstore accounts deposited OPT within a week either side). On those days the public could not get OPT onto the exchange to sell it.`,
                                    deposited > 0
                                        ? `<strong>Coinstore reported ${fmtOpt(reported)} OPT traded in ${esc(a.year)} while customers deposited only ${fmtOpt(deposited)} OPT</strong> from ${accountsYear} accounts and withdrew ${fmtOpt(withdrawn)} OPT. Reported volume ${(reported / deposited).toFixed(1)}× the OPT deposited can\x27t have been customers selling OPT they brought in, so it is not evidence a buyer existed at the quoted price.`
                                        : `<strong>Coinstore reported ${fmtOpt(reported)} OPT traded in ${esc(a.year)} with no customer deposits at all.</strong>`,
                                    `<strong>The market could absorb about ${fmtOpt(avgAbsorb)} OPT a day</strong> (10% of the 30-day average of OPT withdrawn from Coinstore plus Osmosis swaps). You received more than that on ${overDays} of your ${payoutDays.length} payout days.`,
                                ];
                                if (lastDep && lastDep.startsWith(a.year)) findings.push(`<strong>The last deposit ever to reach Coinstore was on ${fmtDate(lastDep)}.</strong> After that nobody could bring OPT to the exchange to sell.`);
                                $('tr-market-findings').innerHTML = findings.map(f => `<li>${f}</li>`).join('');
                                $('tr-closed-body').innerHTML = a.gaps.map(g => `<tr><td>${g.from}${g.cutFrom ? ' (from earlier)' : ''}</td><td>${g.to}${g.cutTo ? ' (continues)' : ''}</td><td class=""amount-cell"">${g.days}</td><td class=""amount-cell"">${g.accounts}</td><td class=""amount-cell"">${fmtOpt(g.you)}</td><td class=""amount-cell"">${fmtUsd(g.quoted)}</td><td class=""amount-cell"">${fmtUsd(g.fmv)}</td><td class=""amount-cell"">${g.dMin == null ? '-' : g.dMin === g.dMax ? fmtPct(g.dMin) : fmtPct(g.dMin) + ' – ' + fmtPct(g.dMax)}</td></tr>`).join('')
                                    || '<tr><td colspan=""8"" style=""text-align: center; color: var(--text-secondary);"">Open all year</td></tr>';
                                $('tr-market-body').innerHTML = a.months.map(m => `<tr><td>${fmtMonth(m.m)}</td><td class=""amount-cell"">${m.open} of ${m.days}</td><td class=""amount-cell"">${fmtPrice(m.price)}</td><td class=""amount-cell"">${fmtOpt(m.reported)}</td><td class=""amount-cell"">${fmtOpt(m.deposit)}</td><td class=""amount-cell"">${fmtOpt(m.withdraw)}</td><td class=""amount-cell"">${m.accounts}</td><td class=""amount-cell"">${fmtOpt(m.absorb)}</td><td class=""amount-cell${m.you > m.absorb ? ' tr-loss' : ''}"">${fmtOpt(m.you)}</td></tr>`).join('');
                                $('tr-evidence-link').innerHTML = `Charts and the full evidence for ${esc(a.year)}: <a href=""/page/liquidity-evidence?address=${encodeURIComponent(dist.address)}&year=${esc(a.year)}"">Liquidity Evidence</a>.`;

                                // Transfers.
                                const outRows = a.transfersOut.map(x => ({ ...x, placeholder: (() => { const p = a.accPrice(x.e); return p == null ? '' : (x.e.amountOpt * p).toFixed(2); })() }));
                                const todoOut = a.transfersOut.filter(x => !x.t.reviewed).length, todoIn = a.transfersIn.filter(x => !x.t.reviewed).length;
                                $('tr-todo-count').innerHTML = todoOut + todoIn
                                    ? `<span class=""tr-badge todo"">${todoOut + todoIn} not confirmed</span> ${todoOut} sent, ${todoIn} received`
                                    : `<span class=""tr-badge ok"">✓ All ${a.transfersOut.length + a.transfersIn.length} confirmed</span>`;
                                for (const id of ['tr-out-body', 'tr-in-body']) $(id).closest('table').classList.toggle('tr-only-todo', $('tr-only-todo').checked);
                                $('tr-out-body').innerHTML = outRows.map(x => `<tr class=""${x.t.reviewed ? '' : 'tr-unconfirmed'}""><td>${fmtTime(x.e.timeUtc)}</td><td>${partyName(x.e.counterparty)}${x.e.memo ? `<div style=""font-size: 11px; color: var(--text-secondary);"" title=""Memo"">${esc(x.e.memo)}</div>` : ''}</td><td class=""amount-cell"">${fmtOpt(x.e.amountOpt)}</td>${editCells(x, OUT_KINDS)}${txCell(x.e.txHash)}</tr>`).join('')
                                    || '<tr class=""tr-empty""><td colspan=""8"" style=""text-align: center; color: var(--text-secondary);"">No OPT left this wallet in ' + esc(a.year) + '</td></tr>';
                                const inRows = a.transfersIn.map(x => ({ ...x, placeholder: x.t.kind === 'gift-in' ? 'giver basis' : x.e.priceUsd != null ? (x.e.amountOpt * x.e.priceUsd).toFixed(2) : '' }));
                                $('tr-in-body').innerHTML = inRows.map(x => `<tr class=""${x.t.reviewed ? '' : 'tr-unconfirmed'}""><td>${fmtTime(x.e.timeUtc)}</td><td>${partyName(x.e.counterparty)}${x.e.memo ? `<div style=""font-size: 11px; color: var(--text-secondary);"" title=""Memo"">${esc(x.e.memo)}</div>` : ''}</td><td class=""amount-cell"">${fmtOpt(x.e.amountOpt)}</td>${editCells(x, IN_KINDS)}${txCell(x.e.txHash)}</tr>`).join('')
                                    || '<tr class=""tr-empty""><td colspan=""8"" style=""text-align: center; color: var(--text-secondary);"">None in ' + esc(a.year) + '</td></tr>';

                                // Sales (Form 8949).
                                $('tr-sales-lede').innerHTML = `Cost basis is matched first-in, first-out within this wallet (from 2025 the IRS requires basis to be tracked wallet by wallet, Rev. Proc. 2024-28): each reward\x27s basis is the fair market value reported as income above. Proceeds you didn\x27t enter are the OPT\x27s fair market value that day and are marked *; enter the real figure from your records. ${fmtUsd(sum(a.sales, s => s.proceeds))} proceeds, ${fmtUsd(sum(a.sales, s => s.cost))} cost basis, net ${fmtGain(salesGain(a.sales))}.`;
                                $('tr-sales-body').innerHTML = a.sales.map(s => `<tr><td>${fmtOpt(s.qty)} OPT</td><td title=""${s.acqFrom ? esc(s.acqFrom + ' to ' + s.acqTo) : ''}"">${esc(s.acquired)}</td><td>${esc(s.soldOn)}</td>
                                    <td class=""amount-cell"">${fmtUsd(s.proceeds)}${s.proceedsEstimated ? '*' : ''}</td><td class=""amount-cell"">${fmtUsd(s.cost)}${s.unknownBasis ? '<span class=""tr-flag"" title=""Part of this OPT has no known cost (no purchase or reward found for it); counted as $0""> †</span>' : ''}</td>
                                    <td class=""amount-cell${s.gain < -0.004 ? ' tr-loss' : ''}"">${fmtGain(s.gain)}</td><td>${s.long ? 'Long' : 'Short'}</td><td>${esc(OUT_KINDS[s.t.kind].label)}${s.fromGift ? ' <span style=""color: var(--text-secondary);"" title=""Includes OPT you were given: gift basis rules apply"">(gifted OPT)</span>' : ''}</td><td style=""font-size: 12px; white-space: pre-wrap; max-width: 260px;"">${esc(s.t.note)}</td></tr>`).join('')
                                    + (a.sales.length ? `<tr class=""tr-total""><td>${fmtOpt(sum(a.sales, s => s.qty))} OPT</td><td></td><td></td><td class=""amount-cell"">${fmtUsd(sum(a.sales, s => s.proceeds))}</td><td class=""amount-cell"">${fmtUsd(sum(a.sales, s => s.cost))}</td><td class=""amount-cell"">${fmtGain(salesGain(a.sales))}</td><td colspan=""3""></td></tr>` : '<tr><td colspan=""9"" style=""text-align: center; color: var(--text-secondary);"">No sales in ' + esc(a.year) + '</td></tr>');
                                const nodes = a.sales.filter(s => s.t.kind === 'node');
                                $('tr-nodes').hidden = !nodes.length;
                                $('tr-nodes-body').innerHTML = nodes.map(s => `<tr><td>${fmtTime(s.e.timeUtc)}</td><td>${partyName(s.e.counterparty)}</td><td class=""amount-cell"">${fmtOpt(s.qty)}</td><td class=""amount-cell"">${fmtUsd(s.proceeds)}</td><td class=""amount-cell"">${fmtUsd(s.cost)}</td><td style=""font-size: 12px; white-space: pre-wrap;"">${esc(s.t.note)}</td></tr>`).join('');
                                $('tr-gifts').hidden = !a.gifts.length;
                                $('tr-gifts-body').innerHTML = a.gifts.map(g => `<tr><td>${fmtTime(g.e.timeUtc)}</td><td>${partyName(g.e.counterparty)}</td><td class=""amount-cell"">${fmtOpt(g.e.amountOpt)}</td><td class=""amount-cell"">${fmtUsd(g.dayValue)}</td><td class=""amount-cell"">${g.basisKnown ? fmtUsd(g.basis) : '<span class=""tr-flag"">Unknown ($0)</span>'}</td><td>${esc(g.t.date || 'Not entered')}</td><td style=""font-size: 12px; white-space: pre-wrap;"">${esc(g.t.note)}</td></tr>`).join('');
                                const ex = expenseRows(a);
                                $('tr-exp-body').innerHTML = ex.map(x => `<tr><td>${esc(x.date)}</td><td>${esc(x.cat.label)}</td><td>${esc(x.description)}${x.auto ? ' <span style=""color: var(--text-secondary); font-size: 11px;"">(automatic)</span>' : ''}</td><td>${x.cat.capital ? 'Depreciate / amortize' : 'Deduct'}</td><td class=""amount-cell"">${fmtUsd(x.usd)}</td><td style=""font-size: 12px; white-space: pre-wrap;"">${esc(x.note)}</td><td class=""tr-no-print"">${x.mine ? `<button type=""button"" data-remove-exp=""${esc(x.id)}"" style=""font-size: 11px; padding: 2px 8px;"">Remove</button>` : ''}</td></tr>`).join('')
                                    + (ex.length ? `<tr class=""tr-total""><td colspan=""4"">Deduct: ${fmtUsd(sum(ex.filter(x => !x.cat.capital), x => x.usd))} · Depreciate: ${fmtUsd(sum(ex.filter(x => x.cat.capital), x => x.usd))}</td><td class=""amount-cell"">${fmtUsd(sum(ex, x => x.usd))}</td><td colspan=""2""></td></tr>` : `<tr><td colspan=""7"" style=""text-align: center; color: var(--text-secondary);"">No node expenses for ${esc(a.year)} yet</td></tr>`);
                                if (!$('tr-exp-date').value || !$('tr-exp-date').value.startsWith(a.year)) $('tr-exp-date').value = a.year + '-12-31';
                                if (!$('tr-host-from').value || !$('tr-host-from').value.startsWith(a.year)) { $('tr-host-from').value = a.year + '-01'; $('tr-host-to').value = a.year + '-12'; showHostingPreview(); }
                                const moves = a.carries.concat(a.skipped.map(x => ({ ...x, cost: null, valueWhenSent: null })));
                                $('tr-moves').hidden = !moves.length;
                                $('tr-moves-body').innerHTML = moves.map(m => `<tr><td>${fmtTime(m.e.timeUtc)}</td><td>${partyName(m.e.counterparty)}</td><td class=""amount-cell"">${fmtOpt(m.e.amountOpt)}</td><td>${esc(OUT_KINDS[m.t.kind].label)}</td><td class=""amount-cell"">${fmtUsd(m.cost)}</td><td class=""amount-cell"">${fmtUsd(m.valueWhenSent)}</td><td style=""font-size: 12px; white-space: pre-wrap;"">${esc(m.t.note)}</td></tr>`).join('');

                                // Payout schedule.
                                $('tr-payouts-body').innerHTML = shown.slice().reverse().map(x => {
                                    const acc = x.fmv?.a;
                                    const market = !acc ? 'No market yet' : acc.source.startsWith('Coinstore') ? 'Coinstore open' : acc.source.startsWith('Osmosis') ? 'Osmosis only' : 'None you could reach';
                                    return `<tr><td>${fmtTime(x.e.timeUtc)}</td><td>${esc(typeLabel(x))}${x.inferred ? ' <span style=""color: var(--text-secondary);"" title=""No same-day node/staking pair; guessed from nearby days"">(est.)</span>' : ''}</td><td class=""amount-cell"">${fmtOpt(x.e.amountOpt)}</td><td class=""amount-cell"">${fmtPrice(x.e.priceUsd)}</td>
                                        <td title=""${esc(acc?.source || '')}"">${market}</td><td class=""amount-cell"">${acc?.discount != null ? fmtPct(acc.discount) : '-'}</td><td class=""amount-cell"">${fmtPrice(x.fmv ? x.fmv.value / x.e.amountOpt : null)}</td><td class=""amount-cell"">${fmtUsd(x.fmv?.value)}</td>
                                        ${txCell(x.e.txHash)}</tr>`;
                                }).join('') || '<tr><td colspan=""9"" style=""text-align: center; color: var(--text-secondary);"">No payouts in ' + esc(a.year) + '</td></tr>';

                                // Method.
                                const ev = data.evidence;
                                $('tr-method').innerHTML = [
                                    '<strong>Rewards are income when received, at fair market value.</strong> Rev. Rul. 2023-14 (staking rewards are gross income in the year you gain dominion and control) and IRS Virtual Currency FAQ Q8–Q9. The FAQ (Q27) accepts a price from an explorer or index but lets you use another value if you can show it is an accurate representation of fair market value; this report is that showing.',
                                    '<strong>Fair market value</strong> is the price at which property would change hands between a willing buyer and a willing seller, neither being under any compulsion and both having reasonable knowledge of the facts (Treas. Reg. §1.170A-1(c)(2); United States v. Cartwright, 411 U.S. 546 (1973)). A price you could not sell at, because the public could not reach the exchange, does not meet that test on its own.',
                                    '<strong>Days with no market:</strong> property that cannot be sold for a period is worth less than freely traded property. Rev. Rul. 77-287 recognizes a discount for securities that cannot be resold right away. The discount here comes from the Finnerty (2012) average-strike put model, using the days until you could next sell and OPT\x27s trailing 90-day volatility. The days to the next sale come from the record of when Coinstore reopened, which was not known on the day itself.',
                                    a.blockage ? `<strong>Blockage:</strong> ${fmtPct(a.blockage)} off the OPT above what the market could absorb each day. Blockage discounts are recognized in estate and gift valuation (Treas. Reg. §20.2031-2(e)); for income tax it is an application of the willing-buyer standard, not a stated rule.` : '<strong>No blockage discount</strong> is applied for receipts larger than the market could absorb; the evidence above supports one if your adviser chooses to take it.',
                                    '<strong>Sales and exchanges:</strong> selling OPT, or paying for a node or anything else with it, is a disposal: gain or loss = amount realized minus cost basis (IRS FAQ Q14–Q16). Report on Form 8949 and Schedule D. Net capital losses offset gains and up to $3,000 a year of other income, with the rest carried forward. A loss on a sale to a related person is not allowed (IRC §267).',
                                    '<strong>Node expenses:</strong> deductible against node income only if running nodes is a trade or business (IRC §162, Schedule C); then node income is also subject to self-employment tax. Node purchases and equipment are capital costs recovered through depreciation or amortization (Form 4562). Expenses paid in OPT are also disposals of that OPT, with their gain or loss in section 4.',
                                    '<strong>Gifts:</strong> OPT received from the wallets you marked as givers is not income (IRC §102). It keeps the giver\x27s basis and holding period for gains; for losses the basis is the lower of the giver\x27s basis and its value on the day of the gift (IRC §1015, Treas. Reg. §1.1015-1).',
                                    '<strong>Worthless or stuck OPT:</strong> the IRS has said a token still trading somewhere is not worthless and that abandonment needs an affirmative act (Chief Counsel Advice 202302011). OPT still trades on Osmosis, so selling it there, even for very little, is the clearer way to realize a loss than claiming it is worthless.',
                                    `<strong>Data:</strong> payouts and transfers are read from the Optio chain (${ledger.entries.length.toLocaleString('en-US')} transfers for this wallet; node distributions from ${esc(dist.source)}, chain staking rewards from the distribution module). Quoted prices: ${esc(dist.priceSource)}. Coinstore deposits to ${esc(flows.depositCollector)} and withdrawals from ${esc(flows.hotWallet)}; Coinstore candles ${fmtDate(market.sources.coinstoreFrom)} to ${fmtDate(market.sources.coinstoreTo)}; Osmosis swaps from the Osmosis archive node. All dates are UTC.`,
                                    'This report organizes public records and your own notes. It is not tax or legal advice. Review it with a CPA or tax attorney; if you take a value below the quoted price, consider disclosing the position on Form 8275.',
                                ].map(t => `<li>${t}</li>`).join('');
                                $('tr-evidence-files').innerHTML = ev && ev.files
                                    ? `Raw source records were captured ${esc(String(ev.capturedUtc).replace('T', ' ').slice(0, 19))} UTC (${ev.files.length} files, each with a SHA-256 fingerprint): <a href=""/api/market/evidence/${encodeURIComponent(ev.id)}.zip"">download the evidence zip</a>.`
                                    : 'No raw records captured yet; capture them on the Liquidity Evidence page.';

                                data.view = a;
                                report.hidden = false;
                                $('tr-print').disabled = false;
                            };

                            // ---------------------------------------------------------------- editing
                            const saveLabel = async (key, field, value) => {
                                const current = { ...(labels[key] || {}) };
                                if (value === '' || value == null) delete current[field]; else current[field] = value;
                                if (field !== 'kind' && !current.kind) {
                                    const shown = report.querySelector(`select[data-field=""kind""][data-key=""${CSS.escape(key)}""]`);
                                    if (shown) current.kind = shown.value;
                                }
                                labels[key] = current;
                                const address = data.dist.address;
                                try {
                                    const r = await fetch('/api/tax/labels?address=' + encodeURIComponent(address), { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ [key]: Object.keys(current).length ? current : null }) });
                                    if (!r.ok) throw new Error((await r.json()).error || 'Save failed');
                                    status.textContent = 'Saved.';
                                } catch (error) {
                                    status.textContent = 'Could not save: ' + error.message;
                                }
                            };
                            const saveExpenses = async (list) => {
                                const r = await fetch('/api/tax/labels?address=' + encodeURIComponent(data.dist.address), { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ _expenses: list.length ? list : null }) });
                                if (!r.ok) throw new Error((await r.json()).error || 'Save failed');
                                labels._expenses = list;
                                const y = window.scrollY; render(); window.scrollTo(0, y);
                            };
                            $('tr-exp-add').addEventListener('click', async () => {
                                if (!data) return;
                                const date = $('tr-exp-date').value, usd = Number($('tr-exp-usd').value);
                                if (!date) { status.textContent = 'Enter the date the expense was paid.'; return; }
                                if (!(usd > 0)) { status.textContent = 'Enter the expense amount in USD.'; return; }
                                const item = { id: Date.now().toString(36) + Math.random().toString(36).slice(2, 6), date, category: $('tr-exp-cat').value, description: $('tr-exp-desc').value.trim(), usd, note: $('tr-exp-note').value.trim() };
                                try {
                                    await saveExpenses((labels._expenses || []).concat([item]));
                                    for (const id of ['tr-exp-desc', 'tr-exp-usd', 'tr-exp-note']) $(id).value = '';
                                    status.textContent = date.startsWith(yearSelect.value) ? 'Expense saved.' : `Expense saved under ${date.slice(0, 4)}; switch the tax year to see it.`;
                                } catch (error) { status.textContent = 'Could not save: ' + error.message; }
                            });
                            // Hosting: nodes x monthly cost x months x business share, one expense line per calendar year.
                            const hostingPlan = () => {
                                const nodes = Math.max(0, Math.floor(Number($('tr-host-nodes').value) || 0)), cost = Number($('tr-host-cost').value) || 0;
                                const share = Math.min(100, Math.max(0, Number($('tr-host-share').value) || 0)) / 100;
                                const from = $('tr-host-from').value, to = $('tr-host-to').value;
                                if (!nodes || !(cost > 0) || !from || !to || to < from || !share) return null;
                                const byYear = new Map();
                                for (let m = from; m <= to; ) {
                                    const y = m.slice(0, 4);
                                    if (!byYear.has(y)) byYear.set(y, []);
                                    byYear.get(y).push(m);
                                    const [yy, mm] = m.split('-').map(Number);
                                    m = mm === 12 ? `${yy + 1}-01` : `${yy}-${String(mm + 1).padStart(2, '0')}`;
                                }
                                const monthName = (m) => fmtMonth(m);
                                return [...byYear.entries()].map(([y, months]) => {
                                    const last = months[months.length - 1], [ly, lm] = last.split('-').map(Number);
                                    const endDay = new Date(Date.UTC(ly, lm, 0)).toISOString().slice(0, 10);
                                    return { year: y, months: months.length, usd: Math.round(nodes * cost * months.length * share * 100) / 100, date: endDay,
                                        description: `${$('tr-host-provider').value.trim() || 'Node hosting'}: ${nodes} node(s) × $${cost.toLocaleString('en-US')}/month × ${months.length} month(s) (${monthName(months[0])}–${monthName(last)})${share < 1 ? `, ${(share * 100).toLocaleString('en-US')}% business use` : ''}` };
                                });
                            };
                            const showHostingPreview = () => {
                                const plan = hostingPlan();
                                $('tr-host-preview').textContent = plan ? plan.map(x => `${x.year}: ${fmtUsd(x.usd)} (${x.months} month(s))`).join(' · ') : 'Enter the nodes, monthly cost and months to see the total.';
                            };
                            for (const id of ['tr-host-nodes', 'tr-host-cost', 'tr-host-from', 'tr-host-to', 'tr-host-share']) $(id).addEventListener('input', showHostingPreview);
                            $('tr-host-add').addEventListener('click', async () => {
                                if (!data) return;
                                const plan = hostingPlan();
                                if (!plan) { status.textContent = 'Enter the number of nodes, the cost per node per month, and the from and to months.'; return; }
                                const note = $('tr-host-note').value.trim();
                                const items = plan.map(x => ({ id: Date.now().toString(36) + Math.random().toString(36).slice(2, 6), date: x.date, category: 'hosting', description: x.description, usd: x.usd, note }));
                                try {
                                    await saveExpenses((labels._expenses || []).concat(items));
                                    for (const id of ['tr-host-cost', 'tr-host-note']) $(id).value = '';
                                    showHostingPreview();
                                    status.textContent = `Hosting saved: ${plan.map(x => `${x.year} ${fmtUsd(x.usd)}`).join(', ')}.`;
                                } catch (error) { status.textContent = 'Could not save: ' + error.message; }
                            });
                            $('tr-exp-body').addEventListener('click', async (ev) => {
                                const btn = ev.target.closest('[data-remove-exp]');
                                if (!btn || !data) return;
                                try { await saveExpenses((labels._expenses || []).filter(x => x.id !== btn.dataset.removeExp)); status.textContent = 'Expense removed.'; }
                                catch (error) { status.textContent = 'Could not save: ' + error.message; }
                            });
                            // Confirm the treatment shown: one row, or every row still flagged.
                            const confirmKeys = async (keys) => {
                                const update = {};
                                for (const key of keys) {
                                    const shown = report.querySelector(`select[data-field=""kind""][data-key=""${CSS.escape(key)}""]`);
                                    if (!shown) continue;
                                    labels[key] = { ...(labels[key] || {}), kind: shown.value };
                                    update[key] = labels[key];
                                }
                                try {
                                    const r = await fetch('/api/tax/labels?address=' + encodeURIComponent(data.dist.address), { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(update) });
                                    if (!r.ok) throw new Error((await r.json()).error || 'Save failed');
                                    status.textContent = `Confirmed ${Object.keys(update).length} transfer(s).`;
                                } catch (error) { status.textContent = 'Could not save: ' + error.message; }
                                const y = window.scrollY; render(); window.scrollTo(0, y);
                            };
                            $('tr-only-todo').addEventListener('change', () => {
                                for (const id of ['tr-out-body', 'tr-in-body']) $(id).closest('table').classList.toggle('tr-only-todo', $('tr-only-todo').checked);
                            });
                            report.addEventListener('click', (ev) => {
                                if (ev.target.closest('#tr-show-todo')) {
                                    ev.preventDefault();
                                    $('tr-only-todo').checked = true;
                                    for (const id of ['tr-out-body', 'tr-in-body']) $(id).closest('table').classList.add('tr-only-todo');
                                    $('tr-section-3').scrollIntoView({ behavior: 'smooth', block: 'start' });
                                    return;
                                }
                                const one = ev.target.closest('[data-confirm-key]');
                                if (one && data) { confirmKeys([one.dataset.confirmKey]); return; }
                                if (ev.target.closest('#tr-confirm-all') && data) confirmKeys([...report.querySelectorAll('[data-confirm-key]')].map(b => b.dataset.confirmKey));
                            });
                            report.addEventListener('change', (ev) => {
                                const el = ev.target.closest('[data-key]');
                                if (!el || !data) return;
                                const value = el.dataset.field === 'usd' ? (el.value === '' ? '' : Number(el.value)) : el.value;
                                saveLabel(el.dataset.key, el.dataset.field, value).then(() => { const y = window.scrollY; render(); window.scrollTo(0, y); });
                            });

                            // ---------------------------------------------------------------- CSV
                            const csvCell = (v) => { const s = v == null ? '' : String(v); return /[,\x22\n]/.test(s) ? '\x22' + s.replace(/\x22/g, '\x22\x22') + '\x22' : s; };
                            const download = (name, lines) => {
                                const link = document.createElement('a');
                                link.href = URL.createObjectURL(new Blob([lines.map(l => l.map(csvCell).join(',')).join('\n')], { type: 'text/csv' }));
                                link.download = name;
                                link.click();
                                setTimeout(() => URL.revokeObjectURL(link.href), 1000);
                            };
                            $('tr-income-csv').addEventListener('click', (e) => {
                                e.preventDefault();
                                if (!data?.view) return;
                                const a = data.view, type = typeSelect.value;
                                const lines = [['time_utc', 'height', 'tx_hash', 'type', 'type_estimated', 'amount_opt', 'quoted_price_usd', 'quoted_value_usd', 'market_that_day', 'marketability_discount_pct', 'days_until_sellable', 'volatility_pct', 'opt_above_capacity', 'fmv_per_opt_usd', 'fmv_usd']];
                                for (const x of a.incomeYear.filter(x => !type || x.kind === type)) {
                                    const acc = x.fmv?.a;
                                    lines.push([fmtTime(x.e.timeUtc), x.e.height, x.e.txHash, typeLabel(x), x.inferred ? 'yes' : 'no', x.e.amountOpt, x.e.priceUsd, x.quoted, acc?.source, acc?.discount != null ? (acc.discount * 100).toFixed(2) : '', acc?.waitDays ?? '', acc?.vol != null ? (acc.vol * 100).toFixed(1) : '', x.fmv?.excess, x.fmv ? x.fmv.value / x.e.amountOpt : '', x.fmv?.value]);
                                }
                                download(`opt-income-${data.dist.address}-${a.year}.csv`, lines);
                            });
                            $('tr-sales-csv').addEventListener('click', (e) => {
                                e.preventDefault();
                                if (!data?.view) return;
                                const a = data.view;
                                const lines = [['description', 'date_acquired', 'date_sold', 'proceeds_usd', 'proceeds_estimated', 'cost_basis_usd', 'gain_loss_usd', 'term', 'treatment', 'sent_utc', 'sent_to', 'sent_to_name', 'tx_hash', 'note']];
                                for (const s of a.sales) lines.push([`${s.qty} OPT`, s.acquired, s.soldOn, s.proceeds?.toFixed(2), s.proceedsEstimated ? 'yes' : 'no', s.cost?.toFixed(2), s.gain?.toFixed(2), s.long ? 'long' : 'short', OUT_KINDS[s.t.kind].label, fmtTime(s.e.timeUtc), s.e.counterparty, names[s.e.counterparty] || '', s.e.txHash, s.t.note]);
                                download(`opt-sales-${data.dist.address}-${a.year}.csv`, lines);
                            });

                            $('tr-exp-csv').addEventListener('click', (e) => {
                                e.preventDefault();
                                if (!data?.view) return;
                                const lines = [['date', 'category', 'treatment', 'description', 'amount_usd', 'source', 'note']];
                                for (const x of expenseRows(data.view)) lines.push([x.date, x.cat.label, x.cat.capital ? 'depreciate' : 'deduct', x.description, x.usd?.toFixed(2), x.auto ? 'chain' : 'entered', x.note]);
                                download(`opt-node-expenses-${data.dist.address}-${data.view.year}.csv`, lines);
                            });

                            // ---------------------------------------------------------------- load
                            const getJson = async (url) => {
                                const r = await fetch(url);
                                const j = await r.json();
                                if (!r.ok) throw new Error(j.error || `${url} failed`);
                                return j;
                            };
                            const updateUrl = () => {
                                const q = new URLSearchParams({ address: addressInput.value.trim(), year: yearSelect.value });
                                if (typeSelect.value) q.set('type', typeSelect.value);
                                if (ruleSelect.value !== 'dlom') q.set('rule', ruleSelect.value);
                                if (blockageInput.value) q.set('blockage', blockageInput.value);
                                history.replaceState(null, '', '?' + q.toString());
                            };
                            const run = async () => {
                                const address = addressInput.value.trim();
                                if (!/^optio1[02-9ac-hj-np-z]{38,58}$/.test(address)) { status.textContent = 'Enter your optio1... wallet address (or save it as primary on My Wallets).'; return; }
                                updateUrl();
                                const done = window.opticBusy(submit, status, 'Reading the chain and market history');
                                status.textContent = 'Reading your rewards and transfers, Coinstore deposits and withdrawals, and market history...';
                                try {
                                    const [{ market, flows }, dist, ledger, saved, , evidence] = await Promise.all([
                                        window.opticMarket.load(),
                                        getJson('/api/distributions/address?address=' + encodeURIComponent(address)),
                                        getJson('/api/wallet/ledger?address=' + encodeURIComponent(address)),
                                        getJson('/api/tax/labels?address=' + encodeURIComponent(address)),
                                        getJson('/api/tax/gift-senders').then(g => { giftSenders = g || {}; renderGifts(); }),
                                        fetch('/api/market/evidence/latest').then(r => r.json()).catch(() => null),
                                    ]);
                                    labels = saved || {};
                                    data = { market, flows, dist, ledger, evidence };
                                    render();
                                    status.textContent = '';
                                    renderWallets();
                                } catch (error) {
                                    status.textContent = 'Error: ' + error.message;
                                } finally {
                                    done();
                                }
                            };
                            const rerender = () => { updateUrl(); if (data && data.dist.address === addressInput.value.trim()) render(); };

                            // Gift wallets: listed in the box above the report, saved for all your wallets.
                            const renderGifts = () => {
                                const list = Object.entries(giftSenders);
                                $('tr-gift-count').textContent = list.length ? `(${list.length})` : '';
                                $('tr-gift-list').innerHTML = list.length
                                    ? list.map(([a, n]) => `<div style=""display: flex; gap: 8px; align-items: center; flex-wrap: wrap;""><strong>${esc(n || 'Gift')}</strong><span class=""address-cell"">${esc(a)}</span>${copyBtn(a)}<button type=""button"" data-remove-gift=""${esc(a)}"" style=""font-size: 11px; padding: 2px 8px;"">Remove</button></div>`).join('')
                                    : '<div style=""color: var(--text-secondary);"">None yet.</div>';
                            };
                            const saveGifts = async (next) => {
                                const r = await fetch('/api/tax/gift-senders', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(next) });
                                const j = await r.json();
                                if (!r.ok) throw new Error(j.error || 'Save failed');
                                giftSenders = j;
                                renderGifts();
                                if (data) { const y = window.scrollY; render(); window.scrollTo(0, y); }
                            };
                            // Copy buttons anywhere on the page (report and gift list).
                            document.addEventListener('click', async (ev) => {
                                const btn = ev.target.closest('.tr-copy');
                                if (!btn) return;
                                ev.preventDefault();
                                const v = btn.dataset.copy;
                                try { await navigator.clipboard.writeText(v); }
                                catch {
                                    const t = document.createElement('textarea');
                                    t.value = v; document.body.appendChild(t); t.select(); document.execCommand('copy'); t.remove();
                                }
                                const was = btn.textContent;
                                btn.textContent = 'Copied';
                                setTimeout(() => { btn.textContent = was; }, 1200);
                            });
                            $('tr-gift-add').addEventListener('click', async () => {
                                const a = $('tr-gift-address').value.trim().toLowerCase(), n = $('tr-gift-name').value.trim();
                                if (!/^optio1[02-9ac-hj-np-z]{38,58}$/.test(a)) { status.textContent = 'Enter the optio1... address the gift came from.'; return; }
                                if (data && a === data.dist.address) { status.textContent = 'That is the wallet this report is for.'; return; }
                                try { await saveGifts({ ...giftSenders, [a]: n }); $('tr-gift-address').value = ''; $('tr-gift-name').value = ''; status.textContent = 'Gift wallet saved.'; }
                                catch (error) { status.textContent = 'Could not save: ' + error.message; }
                            });
                            $('tr-gift-list').addEventListener('click', async (ev) => {
                                const btn = ev.target.closest('[data-remove-gift]');
                                if (!btn) return;
                                const next = { ...giftSenders };
                                delete next[btn.dataset.removeGift];
                                try { await saveGifts(next); } catch (error) { status.textContent = 'Could not save: ' + error.message; }
                            });
                            fetch('/api/tax/gift-senders').then(r => r.json()).then(g => { giftSenders = g || {}; renderGifts(); }).catch(() => renderGifts());

                            // Address book.
                            const renderNames = () => {
                                const list = Object.entries(names).sort((x, y) => x[1].localeCompare(y[1]));
                                $('tr-name-count').textContent = list.length ? `(${list.length})` : '';
                                $('tr-name-list').innerHTML = list.length
                                    ? list.map(([a, n]) => `<div style=""display: flex; gap: 8px; align-items: center; flex-wrap: wrap;""><strong>${esc(n)}</strong><span class=""address-cell"">${esc(a)}</span>${copyBtn(a)}<button type=""button"" class=""tr-name"" data-name-addr=""${esc(a)}"">Rename</button><button type=""button"" data-remove-name=""${esc(a)}"" style=""font-size: 11px; padding: 2px 8px;"">Remove</button></div>`).join('')
                                    : '<div style=""color: var(--text-secondary);"">None yet.</div>';
                            };
                            const saveNames = async (next) => {
                                const r = await fetch('/api/address-book', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(next) });
                                const j = await r.json();
                                if (!r.ok) throw new Error(j.error || 'Save failed');
                                names = j;
                                renderNames();
                                if (data) { const y = window.scrollY; render(); window.scrollTo(0, y); }
                            };
                            const setName = async (a, n) => {
                                const next = { ...names };
                                if (n) next[a] = n; else delete next[a];
                                try { await saveNames(next); status.textContent = n ? `Saved name: ${n}` : 'Name removed.'; }
                                catch (error) { status.textContent = 'Could not save: ' + error.message; }
                            };
                            $('tr-name-add').addEventListener('click', () => {
                                const a = $('tr-name-address').value.trim().toLowerCase(), n = $('tr-name-name').value.trim();
                                if (!/^optio1[02-9ac-hj-np-z]{38,58}$/.test(a)) { status.textContent = 'Enter a valid optio1... address to name.'; return; }
                                if (!n) { status.textContent = 'Enter a name for the address.'; return; }
                                setName(a, n).then(() => { $('tr-name-address').value = ''; $('tr-name-name').value = ''; });
                            });
                            document.addEventListener('click', (ev) => {
                                const btn = ev.target.closest('.tr-name');
                                if (btn) {
                                    ev.preventDefault();
                                    const a = btn.dataset.nameAddr;
                                    const n = window.prompt(`Name for ${a}\n(leave empty to remove the name)`, names[a] || '');
                                    if (n !== null) setName(a, n.trim());
                                    return;
                                }
                                const rm = ev.target.closest('[data-remove-name]');
                                if (rm) setName(rm.dataset.removeName, '');
                            });
                            fetch('/api/address-book').then(r => r.json()).then(n => { names = n || {}; renderNames(); if (data) render(); }).catch(() => renderNames());

                            const renderWallets = () => window.opticWallets.renderSwitcher($('tr-wallets'), addressInput.value.trim() || null, (address) => { addressInput.value = address; run(); });
                            renderWallets();
                            form.addEventListener('submit', (e) => { e.preventDefault(); run(); });
                            for (const el of [yearSelect, typeSelect, ruleSelect]) el.addEventListener('change', rerender);
                            blockageInput.addEventListener('input', rerender);
                            $('tr-print').addEventListener('click', () => window.print());
                            if (addressInput.value) run();
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
                            <div style=""color: var(--text-secondary); font-size: 13px; margin-bottom: 12px;"">Wallets you own. The primary wallet is the one node distributions are paid to; it opens by default on Address Distributions and Profit &amp; Loss. Transfers between these wallets are labelled and left out of profit and loss. Saved to your account, so they follow you to any computer or phone you sign in from.</div>
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
");
        AppendExchangesCard(sb);
        sb.Append(@"
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
        AppendMarketMathScript(sb);
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
                                <div class=""form-group"" style=""flex: 0 0 260px; margin-bottom: 0;"" title=""How to price payouts on days when no exchange was open to the public and Osmosis had no trades"">
                                    <label for=""addr-dist-closed-rule"">Price when no market was open</label>
                                    <select id=""addr-dist-closed-rule"">
                                        <option value=""dlom"">Quoted less marketability discount</option>
                                        <option value=""next"">Next day you could sell</option>
                                        <option value=""last"">Last day you could sell</option>
                                        <option value=""quoted"">Quoted price anyway</option>
                                    </select>
                                </div>
                                <div class=""form-group"" style=""flex: 0 0 230px; margin-bottom: 0;"" title=""Applied only to OPT above what the market could absorb that day. Use the discount your valuation professional supports."">
                                    <label for=""addr-dist-discount"">Discount above market capacity (%)</label>
                                    <input type=""number"" id=""addr-dist-discount"" min=""0"" max=""100"" step=""any"" placeholder=""0"">
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
                                            <th style=""text-align: right;"" title=""This payout as a share of the OPT the whole market could absorb that day (10% of the 30-day average daily volume, counting OPT withdrawn from Coinstore on chain plus Osmosis swaps)"">Of day&#39;s sellable</th>
                                            <th style=""text-align: right;"" title=""Price on the market you could actually sell on that day"">Accessible price</th>
                                            <th style=""text-align: right;"" title=""Accessible price, with the discount applied to the OPT above what the market could absorb that day"">Adjusted value (USD)</th>
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
                                if (discountInput.value) params.set('discount', discountInput.value);
                                if (closedRuleSelect.value !== 'dlom') params.set('closed', closedRuleSelect.value);
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

                            // What the market could absorb each day, loaded after the payouts so the table shows first.
                            let absorbBy = null, absorbError = null, accessibleBy = null;
                            const discountInput = document.getElementById('addr-dist-discount');
                            const closedRuleSelect = document.getElementById('addr-dist-closed-rule');
                            let marketDays = null, marketFlows = null;
                            const computeAccessible = () => { accessibleBy = marketDays ? window.opticMarket.accessible(marketDays, marketFlows, closedRuleSelect.value) : null; };
                            const discount = () => Math.min(100, Math.max(0, Number(discountInput.value) || 0)) / 100;
                            // Accessible price and liquidity-adjusted value for one payout. The day's excess over what the
                            // market could absorb is spread over that day's payouts in proportion to their size.
                            const adjust = (r) => {
                                if (!absorbBy || !accessibleBy) return null;
                                const d = String(r.timeUtc).slice(0, 10);
                                const acc = accessibleBy.get(d), cap = absorbBy.get(d);
                                if (!acc || acc.price == null || cap == null) return null;
                                const dayTotal = dayTotals().get(d) || r.amountOpt;
                                const excess = dayTotal > cap ? r.amountOpt * (dayTotal - cap) / dayTotal : 0;
                                const plain = r.amountOpt * acc.price;
                                return { price: acc.price, source: acc.source, cap, dayTotal, excess, plain, adjusted: plain - excess * acc.price * discount() };
                            };
                            const adjustedCells = (r) => {
                                if (!absorbBy) return '<td class=""amount-cell"" style=""color: var(--text-muted);"">…</td><td class=""amount-cell"" style=""color: var(--text-muted);"">…</td>';
                                const x = adjust(r);
                                if (!x) return '<td class=""amount-cell"">-</td><td class=""amount-cell"">-</td>';
                                const flagged = x.source.startsWith('No market');
                                return `<td class=""amount-cell"" title=""${esc(x.source)}"">${fmtPrice(x.price)}${flagged ? '†' : x.source.startsWith('Osmosis') ? '°' : ''}</td>` +
                                    `<td class=""amount-cell"" title=""${x.excess > 0 ? `${fmtOpt(x.excess)} OPT above market capacity, discounted ${(discount() * 100).toLocaleString('en-US')}%` : 'Within what the market could absorb'}"">${fmtUsd(x.adjusted)}</td>`;
                            };
                            const dayTotals = () => {
                                const totals = new Map();
                                for (const r of loaded.data.rows || []) { const d = String(r.timeUtc).slice(0, 10); totals.set(d, (totals.get(d) || 0) + r.amountOpt); }
                                return totals;
                            };
                            const absorbCell = (r) => {
                                if (!absorbBy) return `<td class=""amount-cell"" style=""color: var(--text-muted);"" title=""${esc(absorbError || 'Loading market volume...')}"">${absorbError ? '-' : '…'}</td>`;
                                const d = String(r.timeUtc).slice(0, 10);
                                const cap = absorbBy.get(d);
                                const day = dayTotals().get(d) || r.amountOpt;
                                if (cap == null) return '<td class=""amount-cell"">-</td>';
                                const ratio = cap > 0 ? r.amountOpt / cap : Infinity;
                                const dayRatio = cap > 0 ? day / cap : Infinity;
                                const text = !isFinite(ratio) ? 'no market' : (ratio * 100).toLocaleString('en-US', { maximumFractionDigits: ratio < 0.1 ? 1 : 0 }) + '%';
                                return `<td class=""amount-cell"" style=""${dayRatio > 1 ? 'color: var(--error); font-weight: 600;' : ''}"" title=""Market could absorb ${fmtOpt(cap)} OPT that day. All your payouts that day: ${fmtOpt(day)} OPT${isFinite(dayRatio) ? ' = ' + (dayRatio * 100).toFixed(0) + '% of it' : ''}."">${text}</td>`;
                            };
                            const loadAbsorb = () => {
                                if (absorbBy) return;
                                window.opticMarket.load()
                                    .then(({ market, flows }) => {
                                        marketDays = window.opticMarket.daily(market, flows, 'withdrawals', 0.1);
                                        marketFlows = flows;
                                        absorbBy = new Map(marketDays.map(x => [x.d, x.absorb]));
                                        computeAccessible();
                                        if (loaded) renderView();
                                    })
                                    .catch(e => { absorbError = 'Market volume unavailable: ' + e.message; if (loaded) renderView(); });
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
                                    tile('First / last', fmtTime(rows[0].timeUtc).slice(0, 10) + ' to ' + fmtTime(rows[rows.length - 1].timeUtc).slice(0, 10)) +
                                    (() => {
                                        if (!absorbBy) return '';
                                        const totals = dayTotals();
                                        const payoutDays = [...new Set(rows.map(r => String(r.timeUtc).slice(0, 10)))];
                                        const over = payoutDays.filter(d => absorbBy.has(d) && totals.get(d) > absorbBy.get(d)).length;
                                        const adj = rows.map(adjust).filter(Boolean);
                                        const sumOf = (f) => adj.reduce((s, x) => s + f(x), 0);
                                        const excessOpt = sumOf(x => x.excess);
                                        return tile('Value at accessible price', `${fmtUsd(sumOf(x => x.plain))}<div style=""font-size: 12px; font-weight: 400; color: var(--text-secondary); margin-top: 2px;"">quoted ${fmtUsd(totalValueUsd)}${unpricedCount > 0 ? ` (${unpricedCount.toLocaleString('en-US')} payouts have no quoted price)` : ''}</div>`) +
                                            tile('Liquidity-adjusted value', `${fmtUsd(sumOf(x => x.adjusted))}<div style=""font-size: 12px; font-weight: 400; color: var(--text-secondary); margin-top: 2px;"">${(discount() * 100).toLocaleString('en-US')}% off ${fmtOpt(excessOpt)} OPT above market capacity</div>`) +
                                            tile('Days above what the market could absorb', `${over.toLocaleString('en-US')} of ${payoutDays.length.toLocaleString('en-US')}<div style=""font-size: 12px; font-weight: 400; color: var(--text-secondary); margin-top: 2px;""><a href=""/page/liquidity-evidence?address=${encodeURIComponent(loaded.address)}${year ? '&year=' + year : ''}"">See the liquidity evidence</a></div>`);
                                    })();
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
                                            ${absorbCell(r)}
                                            ${adjustedCells(r)}
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
                                notes.push('Accessible price: Coinstore\x27s daily close while it was open to the public (at least 5 different accounts depositing within 7 days either side; it was not, for example, from 20 Jan to 16 Mar 2025, from 1 Apr to 23 Jun 2025, or after 7 Dec 2025); ° = average price of that day\x27s Osmosis swaps when Coinstore was closed; † = no market you could reach that day, priced by the rule chosen above (marketability discount: the quoted price less a Finnerty-model discount for the days until you could next sell, at the trailing 90-day volatility). Adjusted value applies the discount you enter only to the OPT above what the market could absorb that day, spread across that day\x27s payouts by size.');
                                notes.push('Of day\x27s sellable: the payout as a share of what the whole market could absorb that day (10% of the 30-day average volume, counting OPT withdrawn from Coinstore on chain plus Osmosis); red = your payouts that day were more than that.');
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
                                    loadAbsorb();
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
                                if (!loaded) return;
                                // Payout view with market data loaded: build the CSV here so it includes the adjusted values.
                                if (viewMode !== 'price') {
                                    if (!absorbBy) return;
                                    e.preventDefault();
                                    const cell = (v) => { const s = v == null ? '' : String(v); return /[,\x22\n]/.test(s) ? '\x22' + s.replace(/\x22/g, '\x22\x22') + '\x22' : s; };
                                    const year = yearSelect.value, type = typeSelect.value;
                                    const rows = (loaded.data.rows || []).filter(r => (!year || String(r.timeUtc).startsWith(year)) && (!type || r.kind === type));
                                    const lines = [['time_utc', 'height', 'tx_hash', 'type', 'type_inferred', 'amount_opt', 'quoted_price_usd', 'quoted_value_usd', 'quoted_price_interval', 'accessible_price_usd', 'accessible_price_source', 'day_received_opt', 'day_market_capacity_opt', 'opt_above_capacity', 'discount_pct', 'accessible_value_usd', 'adjusted_value_usd', 'memo'].join(',')];
                                    for (const r of rows) {
                                        const x = adjust(r);
                                        lines.push([fmtTime(r.timeUtc), r.height, r.txHash, r.kind === 'staking' ? 'staking/participation' : 'node', r.kindInferred ? 'yes' : 'no', r.amountOpt, r.priceUsd, r.valueUsd, r.priceInterval,
                                            x?.price, x?.source, x?.dayTotal, x?.cap, x?.excess, discount() * 100, x?.plain, x?.adjusted, r.memo].map(cell).join(','));
                                    }
                                    const a = document.createElement('a');
                                    a.href = URL.createObjectURL(new Blob([lines.join('\n')], { type: 'text/csv' }));
                                    a.download = `distributions-${loaded.address}${year ? '-' + year : ''}${type ? '-' + type : ''}.csv`;
                                    a.click();
                                    setTimeout(() => URL.revokeObjectURL(a.href), 1000);
                                    return;
                                }
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
                            closedRuleSelect.addEventListener('change', () => {
                                computeAccessible();
                                if (loaded && loaded.address === input.value.trim()) renderView();
                                else updateUrl();
                            });
                            discountInput.addEventListener('input', () => {
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
                            if (initialParams.get('discount')) discountInput.value = initialParams.get('discount');
                            if (['next', 'last', 'quoted'].includes(initialParams.get('closed'))) closedRuleSelect.value = initialParams.get('closed');
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
                            includeDist.checked = window.opticPrefs.get('pnlIncludeDist') === true;
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
                                window.opticPrefs.set('pnlIncludeDist', includeDist.checked);
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
                                    if (window.opticPrefs.get('priceScale') === 'log') scale = 'log';

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
                                        window.opticPrefs.set('priceScale', scale);
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

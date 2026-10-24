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

public static partial class WebDashboard
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
        var url = port > 0 ? $"http://{host}:{port}" : $"https://{host}";
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
            // Free-form command-line arguments can run anything OPTIC can, including managing users, so only admins may send them.
            if (form.ContainsKey("custom-args") && !string.IsNullOrWhiteSpace(form["custom-args"]) && !DashboardAuth.IsAdmin(ctx))
                return Results.Text("Only an admin can run custom arguments.", "text/plain", statusCode: 403);
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

        // Named addresses everyone sees (e.g. the genesis allocation from OptioServices/mainnet-genesis), with the OPT
        // each got at genesis. Read by every signed-in user; changed only by an admin. Personal names take precedence.
        app.MapGet("/api/address-book/global", () =>
        {
            // Detected MEXC-style deposit addresses are named automatically; entries in the file take precedence.
            var merged = SweepDepositNames();
            if (File.Exists(GlobalAddressBookPath))
                foreach (var (k, v) in JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(GlobalAddressBookPath)) ?? new())
                    merged[k] = v;
            return Results.Json(merged);
        });

        app.MapPost("/api/admin/address-book/global", async (HttpContext ctx) =>
        {
            Dictionary<string, JsonElement>? update;
            try { update = await JsonSerializer.DeserializeAsync<Dictionary<string, JsonElement>>(ctx.Request.Body, cancellationToken: ctx.RequestAborted); }
            catch (JsonException) { return Results.Json(new { error = "Expected a JSON object of address: { name, ... }." }, statusCode: 400); }
            if (update is null) return Results.Json(new { error = "Expected a JSON object." }, statusCode: 400);
            lock (TaxLabelsLock)
            {
                var book = File.Exists(GlobalAddressBookPath)
                    ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(GlobalAddressBookPath)) ?? new()
                    : new Dictionary<string, JsonElement>();
                foreach (var (address, entry) in update)
                {
                    var a = address.Trim().ToLowerInvariant();
                    if (!System.Text.RegularExpressions.Regex.IsMatch(a, "^optio1[02-9ac-hj-np-z]{38,58}$"))
                        return Results.Json(new { error = $"Not a valid optio1... address: {address}" }, statusCode: 400);
                    if (entry.ValueKind == JsonValueKind.Null) book.Remove(a);
                    else if (entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("name", out var n) && !string.IsNullOrWhiteSpace(n.GetString())) book[a] = entry;
                    else return Results.Json(new { error = $"Each entry needs a name: {address}" }, statusCode: 400);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(GlobalAddressBookPath)!);
                File.WriteAllText(GlobalAddressBookPath, JsonSerializer.Serialize(book, new JsonSerializerOptions { WriteIndented = true }));
                return Results.Json(book);
            }
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

        // Recorded lot plans (specific identification) carry the server's time of first save, which the browser can't
        // set or change later: the identification has to exist before the sale, so its timestamp is evidence.
        // The standing lot-identification order keeps the server time it was first recorded (evidence it existed before a sale).
        static JsonElement StampLotMethod(JsonElement incoming, JsonElement existing)
        {
            if (incoming.ValueKind != JsonValueKind.Object) return incoming;
            var o = System.Text.Json.Nodes.JsonNode.Parse(incoming.GetRawText())!.AsObject();
            var prior = existing.ValueKind == JsonValueKind.Object && existing.TryGetProperty("recordedUtc", out var r) ? r.GetString() : null;
            o["recordedUtc"] = prior ?? DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
            return JsonSerializer.SerializeToElement(o);
        }
        static JsonElement StampLotPlans(JsonElement incoming, JsonElement existing)
        {
            if (incoming.ValueKind != JsonValueKind.Array) return incoming;
            var recorded = new Dictionary<string, string>(StringComparer.Ordinal);
            if (existing.ValueKind == JsonValueKind.Array)
                foreach (var p in existing.EnumerateArray())
                    if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty("id", out var id) && p.TryGetProperty("recordedUtc", out var r) && id.GetString() is { } ids && r.GetString() is { } rs)
                        recorded[ids] = rs;
            var now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
            var arr = System.Text.Json.Nodes.JsonNode.Parse(incoming.GetRawText())!.AsArray();
            foreach (var node in arr)
            {
                if (node is not System.Text.Json.Nodes.JsonObject o) continue;
                var id = o["id"]?.GetValue<string>();
                o["recordedUtc"] = id != null && recorded.TryGetValue(id, out var r) ? r : now;
            }
            return JsonSerializer.SerializeToElement(arr);
        }

        app.MapGet("/api/tax/labels", (HttpContext ctx) =>
        {
            var address = (ctx.Request.Query["address"].ToString() ?? "").Trim().ToLowerInvariant();
            if (!System.Text.RegularExpressions.Regex.IsMatch(address, "^(optio|osmo)1[02-9ac-hj-np-z]{38,58}$"))
                return Results.Json(new { error = "Enter a valid optio1... wallet address." }, statusCode: 400);
            var path = TaxLabelsPath(ctx, address);
            if (!File.Exists(path))
                return Results.Json(new Dictionary<string, JsonElement>());
            return Results.Text(File.ReadAllText(path), "application/json");
        });

        app.MapPost("/api/tax/labels", async (HttpContext ctx) =>
        {
            var address = (ctx.Request.Query["address"].ToString() ?? "").Trim().ToLowerInvariant();
            if (!System.Text.RegularExpressions.Regex.IsMatch(address, "^(optio|osmo)1[02-9ac-hj-np-z]{38,58}$"))
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
                    else labels[key] = key == "_lotPlans" ? StampLotPlans(value, labels.GetValueOrDefault(key)) : key == "_lotMethod" ? StampLotMethod(value, labels.GetValueOrDefault(key)) : value;
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

        MapValuation(app, syncService);

        Console.WriteLine("Starting OPTIC web dashboard at " + string.Join(" and ", new[]
        {
            DashboardHttps.HttpPort > 0 ? $"http://{host}:{DashboardHttps.HttpPort}" : null,
            DashboardHttps.Current.Enabled ? $"https://{host}:{DashboardHttps.Current.Port}" : null,
        }.Where(u => u != null)));
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
    /// The distribution account pays both node rewards and staking/participation rewards. The user's rule: on a
    /// UTC day with more than one payout, the smaller payout(s) are staking and the larger are node rewards
    /// (PayoutClassifier: split at the largest size gap when the smaller side is at most 85% of the larger).
    /// Payouts on other days (a single payout, or several of about the same size) take the kind whose amounts
    /// they are closest to on the nearest split days before and after, and are marked as inferred.
    /// </summary>
    static void ClassifyDistributionKinds(List<AddressDistributionRow> rows)
    {
        var labels = OPTIC.Services.Valuation.PayoutClassifier.Classify(rows.Select(r => (r.TimeUtc, r.AmountOpt)).ToList());
        var byDay = rows.GroupBy(r => r.TimeUtc.Date).OrderBy(g => g.Key).ToList();
        var splitDays = new List<List<AddressDistributionRow>>();
        var splitIndexByDay = new Dictionary<DateTime, int>();

        for (var i = 0; i < rows.Count; i++)
        {
            if (labels[i].Day != OPTIC.Services.Valuation.PayoutDayKind.Split)
            {
                rows[i].KindInferred = true;
                continue;
            }
            rows[i].Kind = labels[i].Staking ? "staking" : "node";
            rows[i].KindInferred = false;
        }
        foreach (var day in byDay)
        {
            if (day.Any(r => r.KindInferred))
                continue;
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

    static string GlobalAddressBookPath => Path.Combine(Environment.CurrentDirectory, "odata", "address-book-global.json");

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
    static List<(string Sender, string Recipient, BigInteger AmountUnits)> GetTxTransfers(JsonElement tx, string denomWantedUpper) =>
        GetTxTransfersIndexed(tx, denomWantedUpper).Select(t => (t.Sender, t.Recipient, t.AmountUnits)).ToList();

    /// <summary>
    /// GetTxTransfers with the position of each transfer: the index of its event in tx_result.events and its
    /// position within that event, so two payments in one tx can be told apart.
    /// </summary>
    static List<(int EventIndex, int PairIndex, string Sender, string Recipient, BigInteger AmountUnits)> GetTxTransfersIndexed(JsonElement tx, string denomWantedUpper)
    {
        var transfers = new List<(int EventIndex, int PairIndex, string Sender, string Recipient, BigInteger AmountUnits)>();
        if (!tx.TryGetProperty("tx_result", out var txResult) ||
            !txResult.TryGetProperty("events", out var events) ||
            events.ValueKind != JsonValueKind.Array)
            return transfers;

        string? multiSendSender = null;
        var transferEvents = new List<(int Index, List<(string Key, string Value)> Attrs)>();
        var eventIndex = 0;
        foreach (var ev in events.EnumerateArray())
        {
            var type = ev.TryGetProperty("type", out var typeEl) ? typeEl.GetString() ?? "" : "";
            if (string.Equals(type, "transfer", StringComparison.Ordinal))
            {
                transferEvents.Add((eventIndex, ReadCometEventAttributes(ev)));
            }
            else if (string.Equals(type, "message", StringComparison.Ordinal))
            {
                var attrs = ReadCometEventAttributes(ev);
                if (attrs.Any(a => a.Key == "action" && a.Value == "/cosmos.bank.v1beta1.MsgMultiSend"))
                    multiSendSender ??= attrs.FirstOrDefault(a => a.Key == "sender").Value;
            }
            eventIndex++;
        }

        foreach (var (index, attrs) in transferEvents)
        {
            if (attrs.Any(a => a.Key == "sender"))
            {
                transfers.AddRange(ParseTransferEventTuples(attrs, denomWantedUpper).Select(t => (index, t.PairIndex, t.Sender, t.Recipient, t.AmountUnits)));
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
                    transfers.Add((index, i, multiSendSender, recipients[i], amount));
            }
        }

        return transfers;
    }

    static List<(int PairIndex, string Sender, string Recipient, BigInteger AmountUnits)> ParseTransferEventTuples(
        List<(string Key, string Value)> attrs,
        string denomWantedUpper)
    {
        var entries = new List<(int PairIndex, string Sender, string Recipient, BigInteger AmountUnits)>();
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
                entries.Add((i, senders[i], recipients[i], amount));
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
        var nav = NavGroups.SelectMany(g => g.Pages).FirstOrDefault(p => p.Slug == page);
        if (nav is not null) return nav.Title;
        return page switch
        {
            "dashboard" => "Dashboard",
            "top100" => "Top 100 Wallets",
            "distributions" => "Distributions & Ledger",
            "address-distributions" => "Address Distributions",
            "pnl" => "Profit & Loss",
            "market-liquidity" => "Market Liquidity",
            "liquidity-evidence" => "Liquidity Evidence",
            "exchange-flows" => "Exchange Flows",
            "tax-report" => "Tax Report",
            "sell-planner" => "Sell Planner",
            "fifo-sales" => "Sales & Lots",
            "my-wallets" => "My Wallets",
            "my-titn" => "My TITN",
            "locks" => "Locks & Staking",
            "counterparties" => "Counterparties",
            "network" => "Network Analysis",
            "wallet" => "Wallet Analysis",
            "multisend" => "MultiSend Reports",
            "cmc" => "CoinMarketCap Data",
            "custom" => "Custom Arguments",
            "status" => "Node Status",
            "validators" => "Validators",
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
        var header = GetCSSAndHeader(url, page, activeLink).Replace("<!--OPTIC-USER-LINKS-->", userLinks);
        // Menus for admins only (System) are left out of the page for everyone else.
        if (!DashboardAuth.IsAdmin(ctx))
            header = System.Text.RegularExpressions.Regex.Replace(header, @"<!--ADMIN-ONLY-->.*?<!--/ADMIN-ONLY-->", "", System.Text.RegularExpressions.RegexOptions.Singleline);
        sb.Append(header);
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

" + NavCss + @"
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

" + MobileCss + @"
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
" + NavToggleHtml + @"
                <ul class=""top-menu"" id=""top-menu"">" + NavMenuHtml(page) + @"
                </ul>
                <div class=""header-links"">
                    <a href=""/page/about"" class=""header-link"">About</a>
                    <!--OPTIC-USER-LINKS-->
                </div>
            </div>
        </nav>
        
        <div class=""main-content"">
            <header>
                <div class=""header-content"">
                    <div class=""header-title"">
                        <div class=""header-kicker"">Onchain Portfolio Tokenization Intelligence Center</div>
                        <h1>" + GetPageTitle(page) + @"</h1>" + PageHintHtml(page) + @"
                    </div>
                </div>
            </header>
            
            <main>");
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
            case "exchange-flows":
                AppendExchangeFlowsCard(sb);
                break;
            case "tax-report":
                AppendTaxReportCard(sb);
                break;
            case "sell-planner":
                AppendTaxReportCard(sb, "planner");
                break;
            case "fifo-sales":
                AppendTaxReportCard(sb, "fifo");
                break;
            case "liquidity-review":
                AppendLiquidityReviewCard(sb);
                break;
            case "my-staking":
                AppendMyStakingCard(sb);
                break;
            case "my-wallets":
                AppendMyWalletsCard(sb);
                break;
            case "my-titn":
                AppendTitnCard(sb);
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
                AppendValidatorsExplorerCard(sb);
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
                                        days.push({ d, price, cmcClose: m.cmcClose ?? null, csClose: m.csClose ?? null, osPrice: m.osPrice ?? null, csReported: m.csVolumeOpt || 0, reportedEx, deposit: f.depositOpt || 0, depositCount: f.depositCount || 0, depositAccounts: f.depositAccounts || [], withdraw: f.withdrawalOpt || 0, withdrawCount: f.withdrawalCount || 0, osOpt, osTxs: m.osTxs || 0, osSold: m.osSoldOpt || 0, osBought: m.osBoughtOpt || 0, osGross: m.osGrossOpt || 0, total: exOpt + osOpt });
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
                                    <thead><tr><th>Username</th><th>Role</th><th>Last sign-in</th><th></th></tr></thead>
                                    <tbody id=""ad-users""></tbody>
                                </table>
                            </div>
                            <div id=""ad-login-box"" style=""margin: 0 0 12px;"">
                                <div class=""ad-row"" style=""align-items: center;"">
                                    <strong id=""ad-login-title"">Sign-in history</strong>
                                    <button type=""button"" id=""ad-login-unknown"" style=""font-size: 11px; padding: 3px 10px;"">Failed attempts with names that aren&#39;t users</button>
                                    <button type=""button"" id=""ad-login-close"" style=""font-size: 11px; padding: 3px 10px;"" hidden>Close</button>
                                </div>
                                <p class=""ad-note"" id=""ad-login-note"" style=""margin: 4px 0;"">Choose Sign-in history on a user to see when they signed in or out, failed attempts, and from which address and browser.</p>
                                <div class=""data-table-wrap pnl-table-wrap"" id=""ad-login-wrap"" style=""max-height: 360px;"" hidden>
                                    <table class=""data-table"">
                                        <thead><tr><th>When</th><th>Result</th><th>User</th><th>IP address</th><th>Browser</th></tr></thead>
                                        <tbody id=""ad-login-body""></tbody>
                                    </table>
                                </div>
                            </div>
                            <div style=""margin: 0 0 12px;"">
                                <strong>Blocked IP addresses</strong>
                                <p class=""ad-note"" style=""margin: 4px 0;"">Five failed sign-ins in a row from one address block it until you unblock it here. A successful sign-in resets the count. This computer (localhost) is never blocked.</p>
                                <div class=""data-table-wrap pnl-table-wrap"">
                                    <table class=""data-table"">
                                        <thead><tr><th>IP address</th><th>Status</th><th>Failed sign-ins</th><th>Last name tried</th><th>Last attempt</th><th></th></tr></thead>
                                        <tbody id=""ad-ips""></tbody>
                                    </table>
                                </div>
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
                                    <p style=""margin: 0 0 6px;"">${!h.httpPort ? ""Plain http:// is turned off (HTTP port 0), so OPTIC only answers on https://."" : c.redirect ? ""Other devices that open the http:// address are sent to https:// automatically. This computer can still use http://."" : ""http:// stays available from other devices (httpsRedirect=false).""}</p>
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
                            // Local time, with UTC in the tooltip of the history table.
                            const when = (utc) => new Date(utc).toLocaleString(undefined, { year: ""numeric"", month: ""short"", day: ""numeric"", hour: ""2-digit"", minute: ""2-digit"" });
                            const RESULT = { ""signed-in"": [""Signed in"", ""ok""], ""signed-out"": [""Signed out"", """"], ""wrong-password"": [""Wrong password"", ""warn""], ""unknown-user"": [""Unknown username"", ""warn""], ""locked-out"": [""Blocked: too many attempts"", ""warn""], ""ip-blocked"": [""Address blocked"", ""warn""], ""ip-unblocked"": [""Address unblocked"", """"], ""blocked-attempt"": [""Attempt while blocked"", ""warn""] };
                            const loadIps = async () => {
                                const rows = await api(""/api/admin/blocked-ips"");
                                $(""ad-ips"").innerHTML = rows.map(r => `<tr>
                                    <td><strong>${esc(r.ip)}</strong></td>
                                    <td>${r.blocked ? `<span class=""ad-badge warn"">Blocked</span><div class=""ad-note"" style=""margin: 0;"">since ${esc(when(r.blockedUtc))}</div>` : `${r.failures} of 5`}</td>
                                    <td>${r.failures}</td><td>${esc(r.lastUser || ""-"")}</td><td>${r.lastFailureUtc ? esc(when(r.lastFailureUtc)) : ""-""}</td>
                                    <td class=""ad-actions""><button type=""button"" data-unblock=""${esc(r.ip)}"">${r.blocked ? ""Unblock"" : ""Reset count""}</button></td></tr>`).join("""")
                                    || `<tr><td colspan=""6"" class=""ad-note"" style=""text-align: center;"">No blocked addresses and no failed sign-ins.</td></tr>`;
                            };
                            $(""ad-ips"").addEventListener(""click"", async (e) => {
                                const b = e.target.closest(""button[data-unblock]"");
                                if (!b) return;
                                if (!window.confirm(`Unblock ${b.dataset.unblock} and reset its failed sign-in count?`)) return;
                                try { await api(""/api/admin/blocked-ips/unblock"", { ip: b.dataset.unblock }); userStatus.textContent = `Unblocked ${b.dataset.unblock}.`; loadIps(); }
                                catch (err) { userStatus.textContent = ""Could not unblock: "" + err.message; }
                            });
                            // Short browser and device from the user agent, e.g. ""Edge on Windows"", ""Safari on iPhone"".
                            const device = (ua) => {
                                if (!ua) return ""-"";
                                const b = /Edg\//.test(ua) ? ""Edge"" : /OPR\//.test(ua) ? ""Opera"" : /Firefox\//.test(ua) ? ""Firefox"" : /CriOS|Chrome\//.test(ua) ? ""Chrome"" : /Safari\//.test(ua) ? ""Safari"" : /curl|PowerShell|python|Go-http/i.test(ua) ? ""Script"" : ""Browser"";
                                const os = /iPhone/.test(ua) ? ""iPhone"" : /iPad/.test(ua) ? ""iPad"" : /Android/.test(ua) ? ""Android"" : /Windows/.test(ua) ? ""Windows"" : /Mac OS X|Macintosh/.test(ua) ? ""Mac"" : /Linux/.test(ua) ? ""Linux"" : """";
                                return os ? `${b} on ${os}` : b;
                            };
                            const loadLog = async (user) => {
                                const unknown = user === ""*unknown*"";
                                $(""ad-login-title"").textContent = unknown ? ""Failed attempts with names that aren't users"" : `Sign-in history: ${user}`;
                                $(""ad-login-note"").textContent = ""Loading..."";
                                try {
                                    const rows = await api(""/api/admin/login-log?user="" + encodeURIComponent(user));
                                    $(""ad-login-wrap"").hidden = false;
                                    $(""ad-login-close"").hidden = false;
                                    $(""ad-login-note"").textContent = rows.length ? `${rows.length} event(s), newest first${rows.length >= 500 ? "" (latest 500)"" : """"}.` : ""Nothing recorded yet. Sign-ins are recorded from now on."";
                                    $(""ad-login-body"").innerHTML = rows.map(r => {
                                        const [label, cls] = RESULT[r.result] || [r.result, """"];
                                        return `<tr><td title=""${esc(String(r.utc).replace(""T"", "" "").slice(0, 19))} UTC"">${esc(when(r.utc))}</td><td>${cls ? `<span class=""ad-badge ${cls}"">${esc(label)}</span>` : esc(label)}</td><td>${esc(r.user)}</td><td>${esc(r.ip || ""-"")}</td><td title=""${esc(r.userAgent || """")}"">${esc(device(r.userAgent))}</td></tr>`;
                                    }).join("""");
                                } catch (err) { $(""ad-login-note"").textContent = ""Could not load: "" + err.message; }
                            };
                            $(""ad-login-unknown"").addEventListener(""click"", () => loadLog(""*unknown*""));
                            $(""ad-login-close"").addEventListener(""click"", () => { $(""ad-login-wrap"").hidden = true; $(""ad-login-close"").hidden = true; $(""ad-login-title"").textContent = ""Sign-in history""; $(""ad-login-note"").textContent = ""Choose Sign-in history on a user to see when they signed in or out, failed attempts, and from which address and browser.""; });
                            const loadUsers = async () => {
                                const users = await api(""/api/admin/users"");
                                $(""ad-users"").innerHTML = users.map(u => `<tr>
                                    <td><strong>${esc(u.username)}</strong>${u.isYou ? "" (you)"" : """"}</td>
                                    <td>${u.isAdmin ? `<span class=""ad-badge admin"">Admin</span>` : ""User""}</td>
                                    <td>${u.lastSignIn ? `${esc(when(u.lastSignIn))}<div class=""ad-note"" style=""margin: 0;"">${esc(u.lastSignInIp || """")} · ${u.signIns} sign-in(s)${u.failedLast30Days ? ` · <span style=""color: var(--error);"">${u.failedLast30Days} failed in 30 days</span>` : """"}</div>` : `<span class=""ad-note"">Not recorded yet</span>${u.failedLast30Days ? `<div class=""ad-note"" style=""margin: 0; color: var(--error);"">${u.failedLast30Days} failed in 30 days</div>` : """"}`}</td>
                                    <td class=""ad-actions"">
                                        <button type=""button"" data-user-log=""${esc(u.username)}"">Sign-in history</button>
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
                                if (b.dataset.userLog) { loadLog(b.dataset.userLog); $(""ad-login-box"").scrollIntoView({ behavior: ""smooth"", block: ""nearest"" }); return; }
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
                            loadIps().catch(err => userStatus.textContent = ""Could not load blocked addresses: "" + err.message);

                            // ----- optic.conf
                            const HINTS = { httpsPort: ""443 by default; 0 turns HTTPS off"", httpsRedirect: ""true (default) sends other devices from http:// to https://; false keeps both"", addr: ""optio1... used by command-line reports"", tz: ""Windows or IANA id, e.g. America/New_York"", webHost: ""0.0.0.0 = every network, 127.0.0.1 = this computer only"", webPort: ""0 turns plain http:// off (HTTPS only)"" };
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
                                    <p style=""margin: 0 0 6px;"">${n.allNetworks ? `<span class=""ad-badge ok"">Listening on every network</span>` : `<span class=""ad-badge warn"">This computer only</span>`} Started on <strong>${esc(n.host)}</strong>, ${n.httpPort ? `HTTP port ${n.httpPort}` : ""HTTP off""}${n.httpsPort ? `, HTTPS port ${n.httpsPort}` : """"} on ${esc(n.machine)}.</p>
                                    <p style=""margin: 0 0 6px;"">Open it at: ${n.urls.concat(n.httpsUrls).map(u => `<a href=""${esc(u)}"">${esc(u)}</a>`).join("" · "")}</p>
                                    <p style=""margin: 0 0 6px;"">Windows network type: ${profiles}</p>
                                    <p class=""ad-note"" style=""margin: 0;"">Data folder ${esc(n.dataFolder)} · database ${n.databaseMb.toLocaleString(""en-US"")} MB · chain data syncs every ${n.syncMinutes} minutes.${n.allNetworks ? """" : "" To reach it from other devices, set the dashboard address above to 0.0.0.0 and restart OPTIC.""}</p>`;
                                $(""ad-fw-cmd"").textContent = `New-NetFirewallRule -DisplayName ""OPTIC dashboard"" -Direction Inbound -Protocol TCP -LocalPort ${[n.urls.some(u => !u.includes(""127.0.0.1"")) ? n.httpPort : 0, n.httpsPort].filter(Boolean).join("","") || n.port} -Action Allow -Profile Any -RemoteAddress LocalSubnet`;
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
                            // Osmosis accounts can be saved too (used by the Tax Report); pages that read Optio data use list(), Optio only.
                            const validOsmo = (a) => /^osmo1[02-9ac-hj-np-z]{38,58}$/.test(String(a || ''));
                            const validAny = (a) => valid(a) || validOsmo(a);
                            // Ethereum / Base / Arbitrum addresses are saved for reference only; no report reads them.
                            const validEvm = (a) => /^0x[0-9a-f]{40}$/.test(String(a || ''));
                            const validSaved = (a) => validAny(a) || validEvm(a);
                            const esc = (v) => String(v ?? '').replace(/[&<>\x22\x27]/g, ch => '&#' + ch.charCodeAt(0) + ';');
                            const short = (a) => a.length > 20 ? a.slice(0, 12) + '...' + a.slice(-6) : a;

                            // Stored on the server per user as [{ a: address, l: label, p: isPrimary }].
                            const list = () => (Array.isArray(U.wallets) ? U.wallets : []).filter(w => w && valid(w.a)).map(w => ({ ...w }));
                            const listAll = () => (Array.isArray(U.wallets) ? U.wallets : []).filter(w => w && validAny(w.a)).map(w => ({ ...w }));
                            const listSaved = () => (Array.isArray(U.wallets) ? U.wallets : []).filter(w => w && validSaved(w.a)).map(w => ({ ...w }));
                            // Pages that edit list() keep the saved Osmosis and EVM addresses unless keepOsmo is false.
                            const save = (wallets, keepOsmo = true) => {
                                const seen = new Set();
                                const clean = [];
                                const input = keepOsmo ? [...wallets, ...listSaved().filter(w => !valid(w.a) && !wallets.some(x => String(x.a || '').toLowerCase() === w.a))] : wallets;
                                for (const w of input) {
                                    const a = String(w.a || '').trim().toLowerCase();
                                    if (!validSaved(a) || seen.has(a)) continue;
                                    seen.add(a);
                                    clean.push({ a, l: String(w.l || '').trim().slice(0, 40), p: !!w.p });
                                }
                                // Exactly one primary (an Optio wallet) when there are any.
                                const primaryIndex = clean.findIndex(w => w.p && valid(w.a));
                                const firstOptio = clean.findIndex(w => valid(w.a));
                                clean.forEach((w, i) => w.p = i === (primaryIndex < 0 ? firstOptio : primaryIndex));
                                U.wallets = clean;
                                U.hasWallets = true;
                                S.post('/api/me/wallets', clean).catch(e => window.alert('Could not save your wallets: ' + e.message));
                                return clean;
                            };
                            const find = (a) => listSaved().find(w => w.a === String(a || '').toLowerCase()) || null;

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
                                validAny,
                                validOsmo,
                                validEvm,
                                validSaved,
                                list,
                                listAll,
                                listSaved,
                                save,
                                find,
                                label,
                                isMine: (a) => !!find(a),
                                exchanges,
                                saveExchanges,
                                exchange: (a) => exchanges().find(x => x.a === String(a || '').toLowerCase()) || null,
                                primary: () => (list().find(w => w.p) || {}).a || null,
                                add: (a, l) => save([...list(), { a, l: l || '', p: false }]),
                                remove: (a) => save(listSaved().filter(w => w.a !== String(a || '').toLowerCase()), false),

                                // Row of one-click buttons for the user's wallets; onPick(address) switches to one.
                                renderSwitcher: (el, current, onPick, all = false) => {
                                    const wallets = all ? listAll() : list();
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
                                    <h3>4. Osmosis: the decentralized market</h3>
                                    <p class=""le-lede"" id=""le-os-lede""></p>
                                    <ol class=""le-findings"" id=""le-os-findings""></ol>
                                    <div class=""le-chart"" id=""le-chart-osmosis""></div>
                                    <div class=""le-legend"" id=""le-legend-osmosis""></div>
                                    <h4 style=""margin: 12px 0 6px; font-size: 14px;"">Osmosis month by month</h4>
                                    <div class=""data-table-wrap pnl-table-wrap"">
                                        <table class=""data-table"">
                                            <thead><tr><th>Month</th><th style=""text-align: right;"">Swaps</th><th style=""text-align: right;"">Days with swaps</th><th style=""text-align: right;"">OPT sold into pools</th><th style=""text-align: right;"">OPT bought from pools</th><th style=""text-align: right;"">Net sold</th><th style=""text-align: right;"">Average price</th><th style=""text-align: right;"">You received (OPT)</th><th style=""text-align: right;"">You ÷ OPT sold</th></tr></thead>
                                            <tbody id=""le-os-body""></tbody>
                                        </table>
                                    </div>
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

                                // Osmosis: every OPT swap on the Osmosis chain, selected period.
                                const osDays = a.sel;
                                const osSold = osDays.reduce((s, x) => s + x.osSold, 0), osBought = osDays.reduce((s, x) => s + x.osBought, 0);
                                const osTxs = osDays.reduce((s, x) => s + x.osTxs, 0), osActive = osDays.filter(x => x.osTxs > 0).length;
                                const osGross = osDays.reduce((s, x) => s + (x.osPrice != null ? x.osGross : 0), 0);
                                const osVwap = osGross > 0 ? osDays.reduce((s, x) => s + (x.osPrice != null ? x.osPrice * x.osGross : 0), 0) / osGross : null;
                                const soldDays = osDays.filter(x => x.osSold > 0).map(x => x.osSold).sort((p, q) => p - q);
                                const osMedian = soldDays.length ? soldDays[Math.floor(soldDays.length / 2)] : 0;
                                const osBig = osDays.reduce((b, x) => x.osSold > (b?.osSold || 0) ? x : b, null);
                                const youSel = osDays.reduce((s, x) => s + x.you, 0);
                                const firstOs = osDays.find(x => x.osPrice != null), lastOs = [...osDays].reverse().find(x => x.osPrice != null);
                                $('le-os-lede').textContent = `Osmosis is a decentralized exchange on its own blockchain. OPT gets there by IBC transfer from Optio, and anyone can swap it for USDC or OSMO in the OPT pools at any hour; there is no account and no deposit to approve. This section counts every OPT swap on the Osmosis chain${a.year ? ' in ' + a.year : ''}, net per transaction so arbitrage that buys and sells in one transaction cancels out.`;
                                const osF = [];
                                osF.push(osTxs
                                    ? `<strong>${fmtOpt(osTxs)} swaps on ${osActive.toLocaleString('en-US')} of ${osDays.length.toLocaleString('en-US')} days</strong>${a.year ? ' in ' + esc(a.year) : ''}: ${fmtOpt(osSold)} OPT sold into the pools and ${fmtOpt(osBought)} OPT bought from them, a net ${osSold >= osBought ? 'sale of ' + fmtOpt(osSold - osBought) : 'purchase of ' + fmtOpt(osBought - osSold)} OPT.`
                                    : `<strong>No OPT swaps on Osmosis${a.year ? ' in ' + esc(a.year) : ''}.</strong>`);
                                if (osTxs) osF.push(`<strong>On a typical day with sales, ${fmtOpt(osMedian)} OPT was sold</strong> into the Osmosis pools (median). The biggest day was ${osBig ? fmtDate(osBig.d) + ', ' + fmtOpt(osBig.osSold) + ' OPT' : '-'}.`);
                                if (osVwap != null) osF.push(`<strong>Average swap price ${fmtPrice(osVwap)}</strong> (weighted by volume)${firstOs && lastOs && firstOs !== lastOs ? `, from ${fmtPrice(firstOs.osPrice)} on ${fmtDate(firstOs.d)} to ${fmtPrice(lastOs.osPrice)} on ${fmtDate(lastOs.d)}` : ''}.`);
                                if (youSel > 0 && osSold > 0) osF.push(youSel >= osSold
                                    ? `<strong>You received ${fmtOpt(youSel)} OPT, ${fmtX(youSel / osSold)} all the OPT anyone sold on Osmosis</strong> in the same period. Selling it there would have meant adding more than the pools' entire sell volume.`
                                    : `<strong>You received ${fmtOpt(youSel)} OPT, equal to ${fmtPct(youSel / osSold)} of all the OPT anyone sold on Osmosis</strong> in the same period.`);
                                if (liquidity) osF.push(`<strong>All OPT pools together hold about ${fmtUsd(liquidity)} today</strong> (spot ${fmtPrice(spot)}); the market today table below shows what selling different amounts would return.`);
                                $('le-os-findings').innerHTML = osF.map(f => `<li>${f}</li>`).join('');
                                const c4 = drawBarsLine($('le-chart-osmosis'), osDays.map(x => x.d), { color: COLORS.osmosis, values: osDays.map(x => x.osSold) }, { color: COLORS.you, values: osDays.map(x => x.you) });
                                legend($('le-legend-osmosis'), [['OPT sold into the Osmosis pools (OPT)', COLORS.osmosis], ['You received (OPT)', COLORS.you, true]]);
                                attachHover($('le-chart-osmosis'), c4.svg, c4.geom, osDays.length, (i) => { const x = osDays[i]; return `<strong>${fmtDate(x.d)}</strong><br>Sold into pools: ${fmtOpt(x.osSold)} OPT<br>Bought from pools: ${fmtOpt(x.osBought)} OPT<br>Swaps: ${x.osTxs}<br>Osmosis price: ${fmtPrice(x.osPrice)}<br>You received: ${fmtOpt(x.you)} OPT`; });
                                const osMonths = [...new Set(osDays.map(x => x.d.slice(0, 7)))].sort().reverse().map(m => {
                                    const xs = osDays.filter(x => x.d.startsWith(m));
                                    const g = xs.reduce((s, x) => s + (x.osPrice != null ? x.osGross : 0), 0);
                                    return { m, txs: xs.reduce((s, x) => s + x.osTxs, 0), active: xs.filter(x => x.osTxs > 0).length, days: xs.length, sold: xs.reduce((s, x) => s + x.osSold, 0), bought: xs.reduce((s, x) => s + x.osBought, 0), vwap: g > 0 ? xs.reduce((s, x) => s + (x.osPrice != null ? x.osPrice * x.osGross : 0), 0) / g : null, you: xs.reduce((s, x) => s + x.you, 0) };
                                }).filter(m => m.txs > 0 || m.you > 0);
                                $('le-os-body').innerHTML = osMonths.map(m => `<tr><td>${fmtMonth(m.m)}</td><td class=""amount-cell"">${m.txs.toLocaleString('en-US')}</td><td class=""amount-cell"">${m.active} of ${m.days}</td><td class=""amount-cell"">${fmtOpt(m.sold)}</td><td class=""amount-cell"">${fmtOpt(m.bought)}</td><td class=""amount-cell"">${fmtOpt(m.sold - m.bought)}</td><td class=""amount-cell"">${fmtPrice(m.vwap)}</td><td class=""amount-cell"">${fmtOpt(m.you)}</td><td class=""amount-cell"" style=""${m.sold > 0 && m.you > m.sold ? 'color: var(--error); font-weight: 600;' : ''}"">${m.you > 0 ? (m.sold > 0 ? (m.you >= m.sold ? fmtX(m.you / m.sold) : fmtPct(m.you / m.sold)) : 'no sales') : '-'}</td></tr>`).join('')
                                    || '<tr><td colspan=""9"" style=""text-align: center; color: var(--text-secondary);"">No Osmosis swaps in this period</td></tr>';
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
    // mode: "report" (the tax report), "planner" (sell for a target loss this year) or "fifo" (every sale and the
    // lots it used). All three share the same FIFO build, so their numbers agree.
    static void AppendTaxReportCard(System.Text.StringBuilder sb, string mode = "report")
    {
        AppendMyWalletsScript(sb);
        AppendMarketMathScript(sb);
        sb.Append($"<script>window.opticTrMode = '{mode}';</script>");
        if (mode != "report") sb.Append("<style>.tr-report-only { display: none !important; }</style>");
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
                        <h2 id=""tr-title"">OPT Tax Report: Node and Staking Income, Sales and Losses</h2>
                        <div class=""card-content"">
                            <div id=""tr-controls"">
                                <form id=""tr-form"" style=""display: flex; gap: 8px; flex-wrap: wrap; align-items: flex-end; margin-bottom: 8px;"">
                                    <div class=""form-group"" style=""flex: 1 1 380px; margin-bottom: 0;"">
                                        <label for=""tr-address"">Wallet address</label>
                                        <input type=""text"" id=""tr-address"" placeholder=""optio1..."" autocomplete=""off"" spellcheck=""false"">
                                    </div>
                                    <div class=""form-group tr-report-only"" style=""flex: 0 0 110px; margin-bottom: 0;"">
                                        <label for=""tr-year"">Tax year</label>
                                        <select id=""tr-year""></select>
                                    </div>
                                    <div class=""form-group tr-report-only"" style=""flex: 0 0 200px; margin-bottom: 0;"">
                                        <label for=""tr-type"">Income shown</label>
                                        <select id=""tr-type"">
                                            <option value="""">Node and staking</option>
                                            <option value=""node"">Node only</option>
                                            <option value=""staking"">Staking only</option>
                                        </select>
                                    </div>
                                    <div class=""form-group tr-report-only"" style=""flex: 0 0 220px; margin-bottom: 0;"" title=""OPT only, or every crypto asset in your saved wallets (OPT, TITN, TGT, ETH and USDC)"">
                                        <label for=""tr-assets"">Assets shown</label>
                                        <select id=""tr-assets"">
                                            <option value="""">All my crypto assets</option>
                                            <option value=""opt"">OPT only</option>
                                        </select>
                                    </div>
                                    <div class=""form-group"" style=""flex: 0 0 280px; margin-bottom: 0;"" title=""How to value OPT received on days when no exchange was open to the public and Osmosis had no trades"">
                                        <label for=""tr-rule"">Income valued at</label>
                                        <select id=""tr-rule"">
                                            <option value=""next"">Price on the next day you could sell</option>
                                            <option value=""reviewed"">Receipt-date FMV (selected values)</option>
                                            <option value=""quoted"">Quoted price, no discount</option>
                                            <option value=""dlom"">Quoted less marketability discount</option>
                                            <option value=""last"">Price on the last day you could sell</option>
                                        </select>
                                    </div>
                                    <div class=""form-group tr-report-only"" style=""flex: 0 0 200px; margin-bottom: 0;"" title=""Optional blockage discount, applied only to OPT above what the market could absorb that day. Leave at 0 unless a valuation professional supports a figure."">
                                        <label for=""tr-blockage"">Blockage discount (%)</label>
                                        <input type=""number"" id=""tr-blockage"" min=""0"" max=""100"" step=""any"" placeholder=""0"">
                                    </div>
                                    <button type=""submit"" id=""tr-submit"">Build report</button>
                                    <button type=""button"" id=""tr-print"" class=""tr-report-only"" disabled>Print / Save as PDF</button>
                                </form>
                                <div id=""tr-wallets"" style=""font-size: 13px; color: var(--text-secondary); margin: 0 0 8px; display: flex; gap: 6px; flex-wrap: wrap; align-items: center;""></div>
                                <details id=""tr-names-box"" class=""tr-report-only"" style=""margin: 0 0 6px; font-size: 13px;"">
                                    <summary style=""cursor: pointer;"">Named addresses <span id=""tr-name-count"" style=""color: var(--text-secondary);""></span></summary>
                                    <p class=""tr-lede"" style=""margin: 6px 0;"">Give other people&#39;s addresses a name so the report shows who you sent OPT to or got it from. You can also use the Name button next to any address in the report. Names are saved on this computer.</p>
                                    <div id=""tr-name-list"" style=""display: flex; flex-direction: column; gap: 4px; margin-bottom: 6px;""></div>
                                    <div style=""display: flex; gap: 6px; flex-wrap: wrap; align-items: center;"">
                                        <input type=""text"" id=""tr-name-address"" placeholder=""optio1..."" autocomplete=""off"" spellcheck=""false"" style=""flex: 1 1 340px;"">
                                        <input type=""text"" id=""tr-name-name"" placeholder=""Name (e.g. John, node seller)"" autocomplete=""off"" maxlength=""80"" style=""flex: 0 0 220px;"">
                                        <button type=""button"" id=""tr-name-add"">Save name</button>
                                    </div>
                                </details>
                                <details id=""tr-gifts-box"" class=""tr-report-only"" style=""margin: 0 0 10px; font-size: 13px;"">
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
                                <div class=""tr-disclaimer"" style=""font-size: 13px; line-height: 1.5; border-left: 4px solid var(--accent-primary); background: color-mix(in srgb, var(--accent-primary) 6%, transparent); padding: 10px 14px; border-radius: 6px; margin: 0 0 16px; max-width: 1040px;""><strong>Disclaimer:</strong> This report organizes public records and your own entries. It is not tax or legal advice. Consult a tax accountant and a tax attorney before relying on it or filing.</div>

                                <div class=""tr-section"">
                                    <h3>Summary</h3>
                                    <div class=""data-table-wrap pnl-table-wrap"">
                                        <table class=""data-table tr-summary""><tbody id=""tr-summary-body""></tbody></table>
                                    </div>
                                    <div id=""tr-review"" style=""font-size: 13px; margin-top: 8px;""></div>
                                    <div id=""tr-other-assets-status"" class=""tr-no-print"" hidden style=""font-size: 13px; margin-top: 8px; color: var(--text-secondary);""></div>
                                    <div id=""tr-portfolio"" hidden style=""margin-top: 12px;"">
                                        <h4>All crypto assets held at the end of the year</h4>
                                        <div class=""data-table-wrap pnl-table-wrap"">
                                            <table class=""data-table"">
                                                <thead><tr><th>Asset</th><th>Where</th><th style=""text-align: right;"">Quantity</th><th style=""text-align: right;"">Cost basis</th><th style=""text-align: right;"">Value now</th></tr></thead>
                                                <tbody id=""tr-portfolio-body""></tbody>
                                            </table>
                                        </div>
                                    </div>
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

                                <div class=""tr-section"" id=""tr-titn"" hidden>
                                    <h3>1a. Other crypto income: THORWallet (TITN) rewards</h3>
                                    <p class=""tr-lede"" id=""tr-titn-lede""></p>
                                    <div class=""data-table-wrap pnl-table-wrap"">
                                        <table class=""data-table"">
                                            <thead><tr><th>Source</th><th style=""text-align: right;"">Payments</th><th style=""text-align: right;"">Amount</th><th style=""text-align: right;"">Income (USD)</th></tr></thead>
                                            <tbody id=""tr-titn-sum""></tbody>
                                        </table>
                                    </div>
                                    <h4>Each payment</h4>
                                    <div class=""data-table-wrap pnl-table-wrap"">
                                        <table class=""data-table"">
                                            <thead><tr><th>Date (UTC)</th><th>Wallet</th><th>Source</th><th style=""text-align: right;"">Amount</th><th style=""text-align: right;"">Price</th><th style=""text-align: right;"">USD</th><th>Transaction</th></tr></thead>
                                            <tbody id=""tr-titn-body""></tbody>
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
                                    <h4>Osmosis: the decentralized market</h4>
                                    <p class=""tr-lede"" id=""tr-os-lede""></p>
                                    <ol class=""tr-legal"" id=""tr-os-findings""></ol>
                                    <div class=""data-table-wrap pnl-table-wrap"">
                                        <table class=""data-table"">
                                            <thead><tr><th>Month</th><th style=""text-align: right;"">Swaps</th><th style=""text-align: right;"">Days with swaps</th><th style=""text-align: right;"">OPT sold into pools</th><th style=""text-align: right;"">OPT bought from pools</th><th style=""text-align: right;"">Average price</th><th style=""text-align: right;"">You received (OPT)</th></tr></thead>
                                            <tbody id=""tr-os-body""></tbody>
                                        </table>
                                    </div>
                                    <h4>Indicated blockage discount</h4>
                                    <div id=""tr-disc"" class=""tr-legal"" style=""font-size: 13px; line-height: 1.55; max-width: 1040px;""></div>
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
                                    <div class=""tr-no-print"" style=""display: flex; gap: 8px; flex-wrap: wrap; align-items: flex-end; margin-bottom: 8px; font-size: 13px;""><div class=""form-group"" style=""flex: 0 0 300px; margin-bottom: 0;""><label for=""tr-lot-method"">Lots sold</label><select id=""tr-lot-method""><option value=""hifo"">Highest cost first (standing order)</option><option value=""fifo"">First in, first out</option></select></div><div class=""form-group"" style=""flex: 0 0 200px; margin-bottom: 0;"" title=""Sales on or after this date use the standing order. Set an earlier date only if a written standing order was in place then.""><label for=""tr-lot-from"">Standing order effective from</label><input type=""date"" id=""tr-lot-from""></div></div>
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
                                        <p class=""tr-lede"">Paying for a node with OPT is a disposal of that OPT: the gain or loss is above. The node&#39;s own cost basis is what it was worth when you got it (taken here as the value of the OPT you paid unless you entered another figure).</p>
                                        <div class=""data-table-wrap pnl-table-wrap"">
                                            <table class=""data-table"">
                                                <thead><tr><th>Bought (UTC)</th><th>Paid to</th><th style=""text-align: right;"">OPT paid</th><th style=""text-align: right;"">Node cost basis (USD)</th><th style=""text-align: right;"">Cost basis of OPT paid</th><th>Note</th></tr></thead>
                                                <tbody id=""tr-nodes-body""></tbody>
                                            </table>
                                        </div>
                                    </div>
                                    <div id=""tr-gifts"" hidden>
                                        <h4>Gifts received (not income)</h4>
                                        
                                        <div class=""data-table-wrap pnl-table-wrap"">
                                            <table class=""data-table"">
                                                <thead><tr><th>Received (UTC)</th><th>From</th><th style=""text-align: right;"">OPT</th><th style=""text-align: right;"">Value on the day (USD)</th><th style=""text-align: right;"">Giver&#39;s basis (USD)</th><th>Giver acquired</th><th>Note</th></tr></thead>
                                                <tbody id=""tr-gifts-body""></tbody>
                                            </table>
                                        </div>
                                    </div>
                                    <div id=""tr-loans"" hidden>
                                        <h4>Loans</h4>
                                        <div class=""data-table-wrap pnl-table-wrap"">
                                            <table class=""data-table"">
                                                <thead><tr><th>Person</th><th style=""text-align: right;"">Lent (OPT)</th><th style=""text-align: right;"">Repaid (OPT)</th><th style=""text-align: right;"">Of which unlabelled receipts</th><th style=""text-align: right;"">Owed to you at year end (OPT)</th></tr></thead>
                                                <tbody id=""tr-loans-body""></tbody>
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

                                <div class=""tr-section"" id=""tr-tlots"" hidden>
                                    <h3>4a. TGT, TITN and ETH: sales and exchanges (for Form 8949)</h3>
                                    <p class=""tr-lede"" id=""tr-tlots-lede""></p>
                                    <div id=""tr-tlots-flags"" style=""font-size: 13px; margin-bottom: 8px;""></div>
                                    <div class=""data-table-wrap pnl-table-wrap"">
                                        <table class=""data-table"">
                                            <thead><tr><th>Acquired</th><th>Sold</th><th>Asset</th><th style=""text-align: right;"">Quantity</th><th style=""text-align: right;"">Proceeds</th><th style=""text-align: right;"">Cost</th><th style=""text-align: right;"">Gain or (loss)</th><th>Term</th><th>What happened</th></tr></thead>
                                            <tbody id=""tr-tlots-body""></tbody>
                                        </table>
                                    </div>
                                    <h4>Held at the end of the year</h4>
                                    <div class=""data-table-wrap pnl-table-wrap"">
                                        <table class=""data-table"">
                                            <thead><tr><th>Asset</th><th style=""text-align: right;"">Quantity</th><th style=""text-align: right;"">Cost basis</th><th>Notes</th></tr></thead>
                                            <tbody id=""tr-tlots-held""></tbody>
                                        </table>
                                    </div>
                                    <h4>Not classified, and moves to an exchange</h4>
                                    <p class=""tr-lede"">Plain transfers to or from other addresses. They are not counted as sales, purchases or income until you say what they were; tokens received this way are carried with no cost, and tokens sent this way stay in your lots until classified.</p>
                                    <div class=""data-table-wrap pnl-table-wrap"">
                                        <table class=""data-table"">
                                            <thead><tr><th>Date (UTC)</th><th>Wallet</th><th>Direction</th><th style=""text-align: right;"">Quantity</th><th style=""text-align: right;"">Value that day</th><th>Other address</th></tr></thead>
                                            <tbody id=""tr-tlots-unclassified""></tbody>
                                        </table>
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
                                        <button type=""button"" id=""tr-exp-cancel"" hidden>Cancel</button>
                                    </div>
                                    <div class=""data-table-wrap pnl-table-wrap"">
                                        <table class=""data-table"">
                                            <thead><tr><th>Date</th><th>Category</th><th>Description</th><th>Treatment</th><th style=""text-align: right;"">Amount (USD)</th><th>Note</th><th class=""tr-no-print""></th></tr></thead>
                                            <tbody id=""tr-exp-body""></tbody>
                                        </table>
                                    </div>
                                    <h4>First-year deduction for node and equipment purchases (Form 4562)</h4>
                                    <div class=""tr-no-print"" style=""display: flex; gap: 8px; flex-wrap: wrap; align-items: flex-end; margin-bottom: 8px; font-size: 13px;"">
                                        <div style=""display: flex; flex-direction: column; gap: 4px; flex: 0 0 300px;""><label style=""display: flex; gap: 6px; align-items: center;""><input type=""checkbox"" id=""tr-dep-179"" checked> Section 179 expensing (IRC §179)</label><label style=""display: flex; gap: 6px; align-items: center;""><input type=""checkbox"" id=""tr-dep-bonus"" checked> Bonus depreciation (IRC §168(k))</label></div>
                                        <div class=""form-group"" style=""flex: 0 0 170px; margin-bottom: 0;"" title=""The year's IRC §179 dollar limit; check the figure the IRS published for the year""><label for=""tr-dep-limit"">Section 179 limit (USD)</label><input type=""number"" id=""tr-dep-limit"" min=""0"" step=""any""></div>
                                        <div class=""form-group"" style=""flex: 0 0 240px; margin-bottom: 0;"" title=""Section 179 cannot exceed taxable income from your active trades or businesses, which includes wages. Node income less node expenses is counted automatically; add wages and other business income here.""><label for=""tr-dep-other"">Wages and other business income (USD)</label><input type=""number"" id=""tr-dep-other"" min=""0"" step=""any"" placeholder=""0""></div>
                                    </div>
                                    <p class=""tr-lede"" id=""tr-dep-lede""></p>
                                    <div class=""data-table-wrap pnl-table-wrap"">
                                        <table class=""data-table"">
                                            <thead><tr><th>Placed in service</th><th>Item</th><th style=""text-align: right;"">Cost (USD)</th><th style=""text-align: right;"">Section 179</th><th style=""text-align: right;"">Bonus rate</th><th style=""text-align: right;"">Bonus depreciation</th><th style=""text-align: right;"">First-year deduction</th><th style=""text-align: right;"">Basis left to depreciate</th></tr></thead>
                                            <tbody id=""tr-dep-body""></tbody>
                                        </table>
                                    </div>
                                    <ul class=""tr-legal"" id=""tr-dep-notes"" style=""margin-top: 6px;""></ul>
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
                            <div id=""tr-alt"" hidden></div>
                        </div>
                    </div>
                    <script>
                        (() => {
                            const $ = (id) => document.getElementById(id);
                            const EPS = 1e-9;
                            const MODE = window.opticTrMode || 'report';
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
                            ruleSelect.value = 'dlom'; // default: quoted price less a marketability discount on days with no market you could reach
                            for (const [el, key] of [[yearSelect, 'year'], [typeSelect, 'type'], [ruleSelect, 'rule']]) {
                                const v = params.get(key);
                                if (v != null && [...el.options].some(o => o.value === v)) el.value = v;
                            }
                            if (params.get('blockage')) blockageInput.value = params.get('blockage');
                            addressInput.value = params.get('address') || window.opticWallets.primary() || '';
                            if (MODE !== 'report') {
                                // Sell Planner works on the current year; FIFO Sales on every year.
                                yearSelect.value = String(currentYear);
                                $('tr-title').textContent = MODE === 'planner' ? 'Sell Planner: Sell for a Target Loss This Year' : 'Sales & Lots: Every Sale and the Lots It Used';
                                submit.textContent = MODE === 'planner' ? 'Load wallet' : 'Show sales';
                                status.textContent = MODE === 'planner'
                                    ? 'Shows the sales already made this year, how many OPT to sell for the loss you want, and the most you can sell that is not locked. Uses the Tax Report\x27s lots, labels and cost basis. The first run can take a few minutes.'
                                    : 'Lists every sale from this wallet and the lots it used (FIFO, or the lots you identified). Uses the Tax Report\x27s labels and cost basis. The first run can take a few minutes.';
                            }

                            // Treatments for transfers. disposal = taxable (gain or loss); carry = leaves with its basis; skip = left out.
                            const OUT_KINDS = {
                                'unclassified': { label: 'Not classified yet', unclassified: true },
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
                                'unclassified': { label: 'Not classified yet', unclassified: true },
                                'purchase': { label: 'Bought (on an exchange or from a person)' },
                                'own': { label: 'From my own wallet or exchange account' },
                                'gift-in': { label: 'Gift received' },
                                'income': { label: 'Other income (payment received)' },
                                'loan-back': { label: 'Loan repaid to me', skip: true },
                                'ignore': { label: 'Leave out', skip: true },
                            };

                            let data = null, labels = {};
                            const keyOf = (e) => `${e.txHash}|${e.direction}|${e.counterparty}`;

                            const ownWallets = () => new Set(window.opticWallets.listAll().map(w => w.a));
                            const isExchange = (a) => !!window.opticWallets.exchange(a) || a === data.flows.depositCollector || a === data.flows.hotWallet;
                            const exchangeName = (a) => window.opticWallets.exchange(a)?.n || (a === data.flows.depositCollector || a === data.flows.hotWallet ? 'Coinstore' : null);
                            // Small button that copies the full address or hash behind a shortened one.
                            const copyBtn = (v) => `<button type=""button"" class=""tr-copy"" data-copy=""${esc(v)}"" title=""Copy ${esc(v)}"">Copy</button>`;
                            const txCell = (h) => `<td class=""address-cell"" style=""white-space: nowrap;"" title=""${esc(h)}"">${esc(String(h).slice(0, 10))}…${copyBtn(h)}</td>`;
                            let names = {}, globalNames = {};
                            // Shared names (e.g. the genesis allocation) fill in for addresses you haven't named yourself.
                            const sharedName = (a) => globalNames[a] ? globalNames[a].name + (globalNames[a].email ? ' · ' + globalNames[a].email : '') : '';
                            fetch('/api/address-book/global').then(r => r.ok ? r.json() : {}).then(g => { globalNames = g || {}; if (data) render(); }).catch(() => { });
                            const nameBtn = (a) => `<button type=""button"" class=""tr-name"" data-name-addr=""${esc(a)}"" title=""${names[a] ? 'Rename' : 'Name'} this address"">${names[a] ? 'Rename' : 'Name'}</button>`;
                            const partyName = (a) => `<span style=""white-space: nowrap;"">${partyLabel(a)}${copyBtn(a)}${nameBtn(a)}</span>`;
                            // Plain-text name for CSVs and descriptions.
                            const displayName = (a) => names[a] || sharedName(a) || giftSenders[a] || exchangeName(a) || window.opticWallets.label(a) || short(a);
                            const partyLabel = (a) => {
                                const ex = exchangeName(a);
                                if (ex) return `<span title=""${esc(a)}"">Exchange · ${esc(ex)}</span>`;
                                const own = window.opticWallets.label(a);
                                if (giftSenders[a] != null) return `<span title=""${esc(a)}"">Gift · ${esc(names[a] || giftSenders[a] || short(a))}</span>`;
                                if (ownWallets().has(a)) return `<span title=""${esc(a)}"">My wallet${own ? ' · ' + esc(own) : ''}</span>`;
                                if (names[a] || sharedName(a)) return `<span title=""${esc(a)}${!names[a] && globalNames[a]?.genesisOpt != null ? ' · genesis allocation ' + Number(globalNames[a].genesisOpt).toLocaleString('en-US') + ' OPT' : ''}""><strong>${esc(names[a] || sharedName(a))}</strong> <span class=""address-cell"" style=""color: var(--text-secondary);"">${esc(short(a))}</span></span>`;
                                return `<span class=""address-cell"" title=""${esc(a)}"">${esc(short(a))}</span>`;
                            };
                            // Treatment when the user hasn't chosen one: your own wallets are automatic; anything else stays
                            // not classified (never guessed as a sale or purchase) until you choose. { kind, sure }.
                            const defaultOut = (e) => ownWallets().has(e.counterparty) ? { kind: 'own', sure: true } : { kind: 'unclassified', sure: false };
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
                            // ---------------------------------------------------------------- standing lot order (highest cost first)
                            const lotMethod = () => labels._lotMethod || (data?.others || []).map(o => o.labels && o.labels._lotMethod).find(Boolean) || null;
                            const postLabels = async (address, update) => {
                                const r = await fetch('/api/tax/labels?address=' + encodeURIComponent(address), { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(update) });
                                const j = await r.json();
                                if (!r.ok) throw new Error(j.error || 'Save failed');
                                return j;
                            };
                            const setLabelsFor = (address, saved) => { if (address === data.dist.address) labels = saved; else { const o = (data.others || []).find(x => x.address === address); if (o) o.labels = saved; } };
                            // Saved to every wallet's labels so the order applies whichever wallet the report is built for.
                            const setLotMethod = async (patch) => {
                                const next = { ...(lotMethod() || { method: 'hifo' }), ...patch };
                                delete next.recordedUtc;
                                for (const address of [data.dist.address, ...(data.others || []).map(o => o.address)]) setLabelsFor(address, await postLabels(address, { _lotMethod: next }));
                            };
                            let recordingLots = false;
                            // Saves the lots each sale took under the standing order on that sale, so later changes never reshuffle them
                            // and the same lots are never used again.
                            const recordHifo = async (list) => {
                                if (recordingLots || !list || !list.length) return;
                                recordingLots = true;
                                try {
                                    const lm = lotMethod(), when = String(lm?.recordedUtc || '').slice(0, 16).replace('T', ' ');
                                    const byWallet = new Map();
                                    for (const r of list) {
                                        if (!r.parts.length) continue;
                                        const ids = new Map();
                                        for (const p of r.parts) ids.set(p.id, (ids.get(p.id) || 0) + p.qty);
                                        if (!byWallet.has(r.wallet)) byWallet.set(r.wallet, []);
                                        byWallet.get(r.wallet).push({ key: r.key, kind: r.kind, lots: [...ids].map(([id, qty]) => ({ id, qty })) });
                                    }
                                    let n = 0;
                                    for (const [address, items] of byWallet) {
                                        const lab = address === data.dist.address ? labels : ((data.others || []).find(o => o.address === address)?.labels || {});
                                        const update = {};
                                        for (const it of items) update[it.key] = { ...(lab[it.key] || {}), kind: it.kind, lots: it.lots, lotPlan: 'standing-order-hifo', identifiedBy: `Standing order (highest cost first) recorded ${when} UTC` };
                                        setLabelsFor(address, await postLabels(address, update));
                                        n += items.length;
                                    }
                                    if (n) status.textContent = `Recorded the lots used by ${n} sale(s) under the standing order (highest cost first). They will not be used again.`;
                                } catch (error) { status.textContent = 'Could not record lot identifications: ' + error.message; }
                                finally { recordingLots = false; }
                            };
                            $('tr-lot-method').addEventListener('change', async (e) => { try { await setLotMethod({ method: e.target.value }); status.textContent = 'Saved.'; } catch (error) { status.textContent = 'Could not save: ' + error.message; } render(); });
                            $('tr-lot-from').addEventListener('change', async (e) => { try { await setLotMethod({ effectiveFrom: e.target.value || null }); status.textContent = 'Saved.'; } catch (error) { status.textContent = 'Could not save: ' + error.message; } render(); });
                            // ---------------------------------------------------------------- node purchases: Section 179 and bonus depreciation
                            const DEPR_METHODS = {
                                '179-bonus': 'Section 179 up to the income limit, then bonus depreciation',
                                'bonus': 'Bonus depreciation only (IRC §168(k))',
                                '179': 'Section 179 only',
                                'regular': 'Regular depreciation or amortization only',
                            };
                            // IRC §179 dollar limit and phase-out threshold by tax year (2025 and later under P.L. 119-21; indexed after 2025).
                            const S179 = { 2023: [1160000, 2890000], 2024: [1220000, 3050000], 2025: [2500000, 4000000], 2026: [2560000, 4090000] };
                            // Bonus depreciation by acquisition date: 100% for property acquired after January 19, 2025 (P.L. 119-21),
                            // otherwise the TCJA phase-down (80% in 2023, 60% in 2024, 40% in 2025).
                            const bonusRate = (date) => date >= '2025-01-20' ? 1 : date >= '2025-01-01' ? 0.4 : date >= '2024-01-01' ? 0.6 : date >= '2023-01-01' ? 0.8 : 1;
                            const deprSettings = () => labels._depreciation || {};
                            const depreciation = (a, ex, businessIncome) => {
                                const st = deprSettings(), year = Number(a.year);
                                const method = DEPR_METHODS[st.method] ? st.method : '179-bonus';
                                const [defLimit, phaseOut] = S179[year] || S179[2026];
                                const limitIn = st.limit && st.limit[a.year] != null ? Number(st.limit[a.year]) : defLimit;
                                const other = st.other && st.other[a.year] != null ? Number(st.other[a.year]) : 0;
                                const items = ex.filter(x => x.cat.capital && x.usd > 0).map(x => ({ date: x.date, label: x.cat.label, description: x.description, cost: x.usd, rate: bonusRate(String(x.date)), s179: 0, bonus: 0 }));
                                const cost = sum(items, x => x.cost);
                                // The dollar limit falls by the cost placed in service above the phase-out threshold.
                                const limit = Math.max(0, limitIn - Math.max(0, cost - phaseOut));
                                const incomeLimit = Math.max(0, businessIncome + other);
                                let elect = 0, carry = 0;
                                if (method === '179-bonus') elect = Math.min(cost, limit, incomeLimit);
                                else if (method === '179') { elect = Math.min(cost, limit); carry = Math.max(0, elect - incomeLimit); }
                                // Section 179 goes first to the items with the lowest bonus rate, where it adds the most.
                                let left = elect;
                                for (const it of items.slice().sort((p, q) => p.rate - q.rate)) { const take = Math.min(left, it.cost); it.s179 = take; left -= take; }
                                const allowed179 = elect - carry;
                                for (const it of items) {
                                    it.bonus = method === '179-bonus' || method === 'bonus' ? (it.cost - it.s179) * it.rate : 0;
                                    it.remaining = it.cost - it.s179 - it.bonus;
                                }
                                return { method, items, cost, limit, limitIn, phaseOut, incomeLimit, businessIncome, other, elect, allowed179, carry, bonus: sum(items, x => x.bonus), remaining: sum(items, x => x.remaining) };
                            };
                            const saveDepr = async (patch) => {
                                const cur = { ...deprSettings() };
                                for (const [k, v] of Object.entries(patch)) {
                                    if (k === 'method') cur.method = v;
                                    else { cur[k] = { ...(cur[k] || {}) }; if (v.value === '' || v.value == null) delete cur[k][v.year]; else cur[k][v.year] = Number(v.value); }
                                }
                                try { await saveLabels({ _depreciation: cur }); status.textContent = 'Saved.'; } catch (error) { status.textContent = 'Could not save: ' + error.message; }
                                if (data) { const y = window.scrollY; render(); window.scrollTo(0, y); }
                            };
                            // Both options are on by default; the pair maps to the method used.
                            const deprFromBoxes = () => $('tr-dep-179').checked ? ($('tr-dep-bonus').checked ? '179-bonus' : '179') : ($('tr-dep-bonus').checked ? 'bonus' : 'regular');
                            for (const id of ['tr-dep-179', 'tr-dep-bonus']) $(id).addEventListener('change', () => saveDepr({ method: deprFromBoxes() }));
                            $('tr-dep-limit').addEventListener('change', (e) => saveDepr({ limit: { year: yearSelect.value, value: e.target.value } }));
                            $('tr-dep-other').addEventListener('change', (e) => saveDepr({ other: { year: yearSelect.value, value: e.target.value } }));
                            const defaultIn = (e) => {
                                if (giftSenders[e.counterparty] != null) return { kind: 'gift-in', sure: true };
                                if (ownWallets().has(e.counterparty)) return { kind: 'own', sure: true };
                                return { kind: 'unclassified', sure: false };
                            };
                            const treatment = (e, lab = labels) => {
                                const l = lab[keyOf(e)] || {};
                                const kinds = e.direction === 'out' ? OUT_KINDS : IN_KINDS;
                                const def = e.direction === 'out' ? defaultOut(e) : defaultIn(e);
                                const kind = kinds[l.kind] ? l.kind : def.kind;
                                const usd = l.usd === '' || l.usd == null || !isFinite(Number(l.usd)) ? null : Number(l.usd);
                                return { kind, info: kinds[kind], usd, date: l.date || null, note: l.note || '', lots: Array.isArray(l.lots) && l.lots.length ? l.lots : null, lotPlan: l.lotPlan || null, reviewed: def.sure || Object.keys(l).length > 0, auto: def.sure && !Object.keys(l).length };
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
                                // Reviewed mode takes income values only from approved reviews; transfers keep the quoted price.
                                const accSel = window.opticMarket.accessible(days, flows, rule === 'reviewed' ? 'quoted' : rule);
                                const reviewedBy = new Map(Object.entries(data.reviewed?.entries || {}).map(([k, v]) => [k.toLowerCase(), v]));
                                const methods = { quotedAcc: window.opticMarket.accessible(days, flows, 'quoted'), next: window.opticMarket.accessible(days, flows, 'next'), dlom: rule === 'dlom' ? accSel : window.opticMarket.accessible(days, flows, 'dlom') };

                                // Your saved wallets are processed together, wallet by wallet: OPT moved between them keeps its lots
                                // (basis and acquisition dates). Only the selected wallet's activity is reported below.
                                const selAddr = dist.address;
                                const walletsData = [{ address: selAddr, dist, ledger, labels, reviewed: data.reviewed }]
                                    .concat((data.others || []).map(o => ({ ...o, reviewed: data.reviewedOthers ? data.reviewedOthers[o.address] : null })));
                                const processed = new Set(walletsData.map(w => w.address));

                                // Income events per wallet: node distributions (node or staking per the report) and chain staking rewards.
                                const incomeOf = (w) => {
                                    const kindBy = new Map();
                                    for (const r of w.dist.rows) kindBy.set(r.txHash, { kind: r.kind, inferred: r.kindInferred });
                                    const reviewedBy = new Map(Object.entries((w.reviewed && w.reviewed.entries) || {}).map(([k, v]) => [k.toLowerCase(), v]));
                                    const list = [];
                                    for (const e of w.ledger.entries) {
                                        if (e.direction !== 'in') continue;
                                        if (e.category === 'distribution') {
                                            const k = kindBy.get(e.txHash) || { kind: 'node', inferred: true };
                                            list.push({ e, kind: k.kind, inferred: k.inferred });
                                        } else if (e.category === 'reward') list.push({ e, kind: 'staking', inferred: false, module: true });
                                        else if (e.category === 'wallet' && treatment(e, w.labels).kind === 'income') list.push({ e, kind: 'other', inferred: false });
                                    }
                                    const dayTotal = new Map();
                                    for (const x of list) { const d = fmtDay(x.e.timeUtc); dayTotal.set(d, (dayTotal.get(d) || 0) + x.e.amountOpt); }
                                    // Value of qty OPT received on day d under a pricing map, with the blockage discount on the share
                                    // of the day's receipts above what the market could absorb (older methods only).
                                    const valueAt = (accMap, d, qty, applyBlockage) => {
                                        const a = accMap.get(d);
                                        if (!a || a.price == null) return null;
                                        const cap = dayBy.get(d)?.absorb ?? 0, total = dayTotal.get(d) || qty;
                                        const excess = applyBlockage && total > cap ? qty * (total - cap) / total : 0;
                                        return { a, price: a.price, excess, value: qty * a.price - excess * a.price * blockage };
                                    };
                                    for (const x of list) {
                                        const d = fmtDay(x.e.timeUtc);
                                        x.d = d;
                                        x.quoted = x.e.priceUsd != null ? x.e.amountOpt * x.e.priceUsd : null;
                                        if (rule === 'reviewed') {
                                            const r = reviewedBy.get(keyOf(x.e).toLowerCase());
                                            x.fmv = r && r.valueUsd != null ? { a: { price: r.valueUsd / x.e.amountOpt, source: 'Reviewed value: ' + [...r.reviews].join(', ') }, price: r.valueUsd / x.e.amountOpt, excess: 0, value: r.valueUsd } : null;
                                        } else x.fmv = valueAt(accSel, d, x.e.amountOpt, true);
                                        x.alt = { quotedAcc: valueAt(methods.quotedAcc, d, x.e.amountOpt, false)?.value ?? null, next: valueAt(methods.next, d, x.e.amountOpt, false)?.value ?? null, dlom: valueAt(methods.dlom, d, x.e.amountOpt, false)?.value ?? null };
                                        x.open = openBy.get(d) ?? false;
                                        x.noMarket = !x.fmv || String(x.fmv.a.source).startsWith('No market');
                                    }
                                    return list;
                                };
                                const incomeByWallet = new Map(walletsData.map(w => [w.address, incomeOf(w)]));
                                const income = incomeByWallet.get(selAddr);
                                const incomeKey = (addr, e) => addr + '#' + keyOf(e);
                                const incomeBy = new Map([...incomeByWallet].flatMap(([addr, xs]) => xs.map(x => [incomeKey(addr, x.e), x])));
                                // Price per OPT used for proceeds and transfers when the user gave none: the same fair market
                                // value used for income that day, else the quoted price at the time.
                                const unitPrice = (e) => accSel.get(fmtDay(e.timeUtc))?.price ?? e.priceUsd ?? null;

                                // ---------------------------------------------------------------- FIFO through the end of the year, per wallet
                                const lotsBy = new Map(walletsData.map(w => [w.address, []]));
                                const pending = new Map(); // tx hash | receiving wallet -> lots on their way between your own wallets
                                const sales = [], carries = [], skipped = [], transfersOut = [], transfersIn = [], gifts = [], feesYear = [], unclassifiedOut = [];
                                let feeOpt = 0;
                                // Loan balances per person across your wallets: labelled loans raise what they owe, labelled repayments
                                // lower it, and an unlabelled receipt from someone who owes you counts as repayment first.
                                const loans = new Map();
                                // Highest cost first applies to sales on or after the standing order's effective date; earlier sales without
                                // an identification use FIFO. Sales that use it are recorded after the report renders (recordHifo).
                                const lm = lotMethod();
                                const hifoFrom = lm && lm.method === 'hifo' ? (lm.effectiveFrom || String(lm.recordedUtc || '').slice(0, 10) || null) : null;
                                const hifoRecord = [];
                                const loanOf = (cp) => { if (!loans.has(cp)) loans.set(cp, { cp, lent: 0, repaid: 0, auto: 0, owed: 0 }); return loans.get(cp); };
                                // Lots stay in acquisition order, so FIFO sells the earliest-acquired OPT first even when older lots
                                // arrive later from another of your wallets or as a gift with the giver's date. Unknown dates sort by arrival.
                                const addLot = (lots, at, lot) => {
                                    lot.key = lot.time || at;
                                    let i = lots.length;
                                    while (i > 0 && lots[i - 1].key > lot.key) i--;
                                    lots.splice(i, 0, lot);
                                };
                                // Each lot keeps the id of the receipt that created it, also after moving between your wallets, so a sale can
                                // name the exact lots it used. Repeats of the same receipt key get a counter, in the same order on every run.
                                const idSeen = new Map();
                                const lotId = (w, e) => {
                                    const base = w.address + '#' + keyOf(e), n = (idSeen.get(base) || 0) + 1;
                                    idSeen.set(base, n);
                                    return n === 1 ? base : base + '#' + n;
                                };
                                // Takes qty OPT from a wallet: first the identified lots (specific identification, [{ id, qty }]), then the
                                // oldest lots (FIFO). Identified OPT no longer held is listed in parts.missing and taken by FIFO instead.
                                // Lots taken here are gone for every later sale, in this year and all later years.
                                const take = (addr, qty, ident, hifo) => {
                                    const lots = lotsBy.get(addr);
                                    const parts = [], missing = [];
                                    let left = qty;
                                    const takeAt = (i, q, identified) => {
                                        const lot = lots[i];
                                        parts.push({ id: lot.id, identified, qty: q, time: lot.time, cost: lot.unit == null ? null : q * lot.unit, lossCost: lot.lossUnit == null ? (lot.unit == null ? null : q * lot.unit) : q * lot.lossUnit, source: lot.source });
                                        lot.qty -= q; left -= q;
                                        if (lot.qty <= EPS) { lots.splice(i, 1); return true; }
                                        return false;
                                    };
                                    for (const want of ident || []) {
                                        let need = Math.min(Number(want.qty) || 0, left);
                                        for (let i = 0; i < lots.length && need > EPS;) {
                                            if (lots[i].id !== want.id) { i++; continue; }
                                            const q = Math.min(lots[i].qty, need);
                                            need -= q;
                                            if (!takeAt(i, q, true)) i++;
                                        }
                                        if (need > EPS) missing.push({ id: want.id, qty: need });
                                    }
                                    // Standing order: the lots with the highest cost basis go first (for gifts, the basis that counts for a loss).
                                    if (hifo) while (left > EPS && lots.length) {
                                        let bi = 0, bb = -Infinity;
                                        for (let i = 0; i < lots.length; i++) { const b = lots[i].unit == null ? -1 : (lots[i].lossUnit ?? lots[i].unit); if (b > bb + 1e-15) { bb = b; bi = i; } }
                                        takeAt(bi, Math.min(lots[bi].qty, left), true);
                                    }
                                    while (left > EPS && lots.length) takeAt(0, Math.min(lots[0].qty, left), false);
                                    if (left > EPS) parts.push({ id: null, identified: false, qty: left, time: null, cost: null, lossCost: null, source: 'none' });
                                    parts.missing = missing;
                                    return parts;
                                };
                                const events = walletsData.flatMap(w => w.ledger.entries.filter(e => fmtDay(e.timeUtc) <= yEnd)
                                    .map(e => ({ w, e, own: e.category === 'wallet' && processed.has(e.counterparty) && treatment(e, w.labels).kind === 'own' })));
                                // Same block: receipts first, then sends, then the receiving side of moves between your own wallets.
                                const order = (x) => x.e.direction === 'out' ? 1 : x.own ? 2 : 0;
                                // By time (the Optio and Osmosis chains number blocks differently); same block: receipts, sends, then own receipts.
                                events.sort((x, y) => (Date.parse(x.e.timeUtc) - Date.parse(y.e.timeUtc)) || (order(x) - order(y)) || (x.e.height - y.e.height));
                                for (let { w, e, own } of events) {
                                    const isSel = w.address === selAddr;
                                    const lots = lotsBy.get(w.address);
                                    const t = e.category === 'wallet' ? treatment(e, w.labels) : null;
                                    if (e.direction === 'in') {
                                        const inc = incomeBy.get(incomeKey(w.address, e));
                                        if (inc) { addLot(lots, e.timeUtc, { id: lotId(w, e), qty: e.amountOpt, unit: inc.fmv ? inc.fmv.value / e.amountOpt : null, time: e.timeUtc, source: 'income' }); continue; }
                                        if (e.category !== 'wallet') continue; // module receipts return your own staked tokens
                                        if (own) {
                                            // From your own wallet: the same lots arrive, with their original basis and acquisition dates.
                                            const q = pending.get(e.txHash + '|' + w.address);
                                            const parts = q && q.length ? q.shift() : null;
                                            if (parts) for (const p of parts) addLot(lots, e.timeUtc, { id: p.id, qty: p.qty, unit: p.cost == null ? null : p.cost / p.qty, lossUnit: p.lossCost == null ? null : p.lossCost / p.qty, time: p.time, source: p.source === 'gift' ? 'gift' : 'own-wallet' });
                                            else addLot(lots, e.timeUtc, { id: lotId(w, e), qty: e.amountOpt, unit: null, time: null, source: 'own-wallet' });
                                            if (isSel && inYear(e.timeUtc)) transfersIn.push({ e, t, carried: parts ? (parts.some(p => p.cost == null) ? null : sum(parts, p => p.cost)) : null, carriedFound: !!parts });
                                            continue;
                                        }
                                        let autoRepaid = 0;
                                        const lab = w.labels[keyOf(e)];
                                        if (!(lab && lab.kind)) {
                                            const ln = loanOf(e.counterparty);
                                            autoRepaid = Math.min(ln.owed, e.amountOpt);
                                            if (autoRepaid > EPS) { ln.owed -= autoRepaid; ln.repaid += autoRepaid; ln.auto += autoRepaid; } else autoRepaid = 0;
                                        }
                                        if (t.kind === 'loan-back') { const ln = loanOf(e.counterparty); ln.repaid += e.amountOpt; ln.owed = Math.max(0, ln.owed - e.amountOpt); }
                                        if (isSel && inYear(e.timeUtc)) transfersIn.push({ e, t, autoRepaid });
                                        if (t.info.skip) continue;
                                        if (autoRepaid > EPS) { if (e.amountOpt - autoRepaid <= EPS) continue; e = { ...e, amountOpt: e.amountOpt - autoRepaid }; }
                                        if (t.kind === 'unclassified') { addLot(lots, e.timeUtc, { id: lotId(w, e), qty: e.amountOpt, unit: null, time: e.timeUtc, source: 'unclassified' }); continue; }
                                        const quotedValue = e.priceUsd != null ? e.amountOpt * e.priceUsd : null;
                                        if (t.kind === 'gift-in') {
                                            // Giver's basis for gains; the lower of that and the value on the day for losses.
                                            // The holding period runs from when the giver acquired it, if entered.
                                            const unit = unitPrice(e), dayValue = unit == null ? null : e.amountOpt * unit;
                                            const basis = t.usd ?? 0;
                                            const lossBasis = dayValue == null ? basis : Math.min(basis, dayValue);
                                            addLot(lots, e.timeUtc, { id: lotId(w, e), qty: e.amountOpt, unit: basis / e.amountOpt, lossUnit: lossBasis / e.amountOpt, time: t.date ? t.date + 'T00:00:00Z' : e.timeUtc, source: 'gift' });
                                            if (isSel && inYear(e.timeUtc)) gifts.push({ e, t, dayValue, basis, basisKnown: t.usd != null });
                                            continue;
                                        }
                                        const cost = t.usd != null ? t.usd : quotedValue;
                                        addLot(lots, e.timeUtc, { id: lotId(w, e), qty: e.amountOpt, unit: cost == null ? null : cost / e.amountOpt, time: e.timeUtc, source: t.kind, estimated: t.usd == null });
                                        continue;
                                    }
                                    if (e.category === 'module') continue; // delegation or lock: still yours
                                    if (e.category === 'fee') {
                                        take(w.address, e.amountOpt);
                                        if (isSel) { feeOpt += e.amountOpt; if (inYear(e.timeUtc)) feesYear.push({ e, value: unitPrice(e) == null ? null : e.amountOpt * unitPrice(e) }); }
                                        continue;
                                    }
                                    // Shown in the year it was sent, and in the year of its sale date if that is later.
                                    const counts = isSel && (inYear(e.timeUtc) || (t.date && inYear(t.date)));
                                    if (counts) transfersOut.push({ e, t });
                                    if (t.kind === 'loan') { const ln = loanOf(e.counterparty); ln.lent += e.amountOpt; ln.owed += e.amountOpt; }
                                    if (t.info.skip) { if (counts) skipped.push({ e, t }); continue; }
                                    const soldDay = t.date || fmtDay(e.timeUtc);
                                    // The standing order also picks the lots that move between your own wallets (identification covers
                                    // transfers as well as sales, Treas. Reg. §1.1012-1(j)); recorded moves keep their lots.
                                    const identifiable = t.info.disposal || own;
                                    const useHifo = !!(identifiable && !t.lots && hifoFrom && soldDay >= hifoFrom);
                                    const parts = take(w.address, e.amountOpt, identifiable ? t.lots : null, useHifo);
                                    if (useHifo) hifoRecord.push({ wallet: w.address, key: keyOf(e), kind: t.kind, parts: parts.filter(p => p.id) });
                                    const identMissing = sum(parts.missing, m => m.qty);
                                    const cost = parts.some(p => p.cost == null) ? (parts.every(p => p.cost == null) ? null : sum(parts, p => p.cost)) : sum(parts, p => p.cost);
                                    const unit = unitPrice(e);
                                    const valueWhenSent = unit == null ? null : e.amountOpt * unit;
                                    if (own) {
                                        const k = e.txHash + '|' + e.counterparty;
                                        if (!pending.has(k)) pending.set(k, []);
                                        pending.get(k).push(parts);
                                        if (counts) carries.push({ e, t, cost, valueWhenSent, toOwn: true });
                                        continue;
                                    }
                                    if (!counts) continue;
                                    // Not classified: the OPT left the wallet, but it is not counted as a sale until you say what it was.
                                    if (t.kind === 'unclassified') { unclassifiedOut.push({ e, t, cost, valueWhenSent }); continue; }
                                    if (t.info.disposal) {
                                        // Paying for node licenses in the one-time promotion: the amount realized is the value of the licenses
                                        // received, which the user enters. Under receipt-date FMV it is not filled from the disputed OPT price.
                                        const proceeds = t.kind === 'worthless' ? (t.usd ?? 0) : (t.usd ?? (rule === 'reviewed' && t.kind === 'node' ? null : valueWhenSent));
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
                                            const dated = g.filter(p => p.time).map(p => fmtDay(p.time)).sort();
                                            // Per lot: gain above the gain basis, loss below the loss basis (they differ only for gifts), else none.
                                            const gain = proceeds == null || gCost == null ? null : sum(g, p => {
                                                const pr = proceeds * p.qty / e.amountOpt, c = p.cost ?? 0, l = p.lossCost ?? c;
                                                return pr > c ? pr - c : pr < l ? pr - l : 0;
                                            });
                                            sales.push({ e, t, qty, soldOn, proceeds: proceeds == null ? null : proceeds * share, cost: gCost, gain, fromGift: g.some(p => p.source === 'gift'), identified: g.some(p => p.identified), identMissing, lotPlan: t.lotPlan,
                                                long: isLong(g[0]), parts: g, acquired: dated.length === 0 ? 'Unknown' : dated[0] === dated[dated.length - 1] ? dated[0] : 'Various', acqFrom: dated[0], acqTo: dated[dated.length - 1],
                                                unknownBasis: g.some(p => p.cost == null), proceedsEstimated: t.usd == null && t.kind !== 'worthless', valueWhenSent: valueWhenSent == null ? null : valueWhenSent * share });
                                        }
                                    } else {
                                        carries.push({ e, t, cost, valueWhenSent });
                                    }
                                }
                                const lots = lotsBy.get(selAddr);
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
                                // ---------------------------------------------------------------- Osmosis liquidity and the indicated blockage discount
                                const liq = (() => {
                                    const r = { swaps: sum(sel, x => x.osTxs), active: sel.filter(x => x.osTxs > 0).length, days: sel.length, sold: sum(sel, x => x.osSold), bought: sum(sel, x => x.osBought) };
                                    const gross = sum(sel, x => x.osPrice != null ? x.osGross : 0);
                                    r.vwap = gross > 0 ? sum(sel, x => x.osPrice != null ? x.osPrice * x.osGross : 0) / gross : null;
                                    const soldDays = sel.filter(x => x.osSold > 0).map(x => x.osSold).sort((p, q) => p - q);
                                    r.median = soldDays.length ? soldDays[Math.floor(soldDays.length / 2)] : 0;
                                    r.big = sel.reduce((b, x) => x.osSold > (b?.osSold || 0) ? x : b, null);
                                    const priced = sel.filter(x => x.price != null);
                                    r.p0 = priced[0] || null; r.p1 = priced[priced.length - 1] || null;
                                    r.months = [...new Set(sel.map(x => x.d.slice(0, 7)))].map(m => {
                                        const xs = sel.filter(x => x.d.startsWith(m)), g = sum(xs, x => x.osPrice != null ? x.osGross : 0);
                                        return { m, swaps: sum(xs, x => x.osTxs), active: xs.filter(x => x.osTxs > 0).length, days: xs.length, sold: sum(xs, x => x.osSold), bought: sum(xs, x => x.osBought),
                                            vwap: g > 0 ? sum(xs, x => x.osPrice != null ? x.osPrice * x.osGross : 0) / g : null, you: sum(incomeYear.filter(x => x.d.startsWith(m)), x => x.e.amountOpt) };
                                    });
                                    // Selling the year's receipts at the market's pace: each day the market can take `absorb` OPT; that day's
                                    // receipts go first and the rest waits, oldest first, for spare capacity on later days. Each part waiting is
                                    // discounted by the Finnerty value for its wait, at the trailing 90-day volatility on the day it arrived.
                                    const vol = window.opticMarket.volatility(days);
                                    const recv = new Map();
                                    for (const x of incomeYear) recv.set(x.d, (recv.get(x.d) || 0) + x.e.amountOpt);
                                    const queue = [];
                                    let excessOpt = 0, excessVal = 0, discVal = 0, waitMax = 0;
                                    for (let i = 0; i < days.length; i++) {
                                        const x = days[i];
                                        if (x.d < yStart) continue;
                                        let cap = x.absorb || 0;
                                        const got = x.d <= yEnd ? (recv.get(x.d) || 0) : 0;
                                        if (got > 0) {
                                            const now = Math.min(cap, got), rest = got - now;
                                            cap -= now;
                                            if (rest > EPS) { const pr = x.price ?? 0; queue.push({ i, qty: rest, price: pr, vol: vol[i] }); excessOpt += rest; excessVal += rest * pr; }
                                        }
                                        while (cap > EPS && queue.length) {
                                            const q = queue[0], take = Math.min(cap, q.qty), wait = i - q.i;
                                            waitMax = Math.max(waitMax, wait);
                                            discVal += take * q.price * window.opticMarket.finnerty(q.vol, wait / 365);
                                            q.qty -= take; cap -= take;
                                            if (q.qty <= EPS) queue.shift();
                                        }
                                        if (x.d > yEnd && !queue.length) break;
                                    }
                                    // Still waiting when the data ends: discounted for the wait so far, so the figure is a floor.
                                    r.unsold = sum(queue, q => q.qty);
                                    for (const q of queue) { const wait = days.length - 1 - q.i; waitMax = Math.max(waitMax, wait); discVal += q.qty * q.price * window.opticMarket.finnerty(q.vol, wait / 365); }
                                    r.received = sum(incomeYear, x => x.e.amountOpt);
                                    r.receivedVal = sum(incomeYear, x => x.e.amountOpt * (dayBy.get(x.d)?.price ?? 0));
                                    r.excessOpt = excessOpt; r.excessVal = excessVal; r.discUsd = discVal; r.waitMax = waitMax;
                                    r.discount = excessVal > 0 ? discVal / excessVal : 0;
                                    r.indicated = Math.round(r.discount * 100);
                                    return r;
                                })();
                                return { feesYear, gifts, accPrice: unitPrice, year, rule, blockage, days, sel, income, incomeYear, sales, carries, skipped, transfersOut, transfersIn, unclassifiedOut, held, heldCost, lots, feeOpt, gaps, months, openBy, ownWallets: walletsData.length - 1, liq, hifoRecord, lotMethod: lm, hifoFrom, loans: [...loans.values()].filter(l => l.lent > EPS || l.repaid > EPS).sort((x, y) => y.owed - x.owed) };
                            };

                            // ---------------------------------------------------------------- render
                            const typeLabel = (x) => x.kind === 'staking' ? (x.module ? 'Staking (chain rewards)' : 'Staking / participation') : x.kind === 'other' ? 'Other income' : 'Node';
                            const RULE_TEXT = {
                                dlom: 'Coinstore\x27s daily close (CoinMarketCap\x27s once Coinstore stopped trading) less a discount for lack of marketability, sized by the Finnerty model to the number of days until you could next sell and to OPT\x27s trailing 90-day price volatility',
                                quoted: 'the quoted daily price, with no discount',
                                next: 'the price on the next day you could actually sell',
                                reviewed: 'the value approved for each receipt on the Liquidity & Distribution Review page (no automatic adjustment)',
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
                                    : `<div class=""tr-no-print""><span class=""tr-badge todo"">Not classified</span></div>`}<select data-key=""${esc(key)}"" data-field=""kind"">${opts}</select></td>
                                    <td class=""tr-edit amount-cell""><input type=""number"" step=""any"" class=""tr-usd"" data-key=""${esc(key)}"" data-field=""usd"" value=""${x.t.usd ?? ''}"" placeholder=""${esc(x.placeholder ?? '')}""></td>
                                    ${dateCell}
                                    <td class=""tr-edit""><textarea data-key=""${esc(key)}"" data-field=""note"" placeholder=""e.g. Sold on Coinstore 2025-07-03, trade #…"">${esc(x.t.note)}</textarea><div class=""tr-note-print"">${esc(x.t.note)}</div></td>`;
                            };

                            // ---------------------------------------------------------------- Sell Planner and FIFO Sales pages
                            // Both reuse build(): the same labels, own-wallet carry, loans, gifts and income values as the Tax Report.
                            const SOURCE_TEXT = { income: 'Reward', 'own-wallet': 'From my other wallet', gift: 'Gift', purchase: 'Bought', unclassified: 'Not classified (no cost)', none: 'No record (no cost)' };
                            const sourceText = (s) => SOURCE_TEXT[s] || (IN_KINDS[s] ? IN_KINDS[s].label : s || '-');
                            // Recorded lot plans (specific identification), saved with the wallet's labels. Whatever a plan has not yet given
                            // to a sale stays reserved, so the planner never offers the same lots twice; lots used by any sale, this year or
                            // earlier, are already gone from the lots the engine hands back.
                            const lotPlans = () => Array.isArray(labels._lotPlans) ? labels._lotPlans : [];
                            const planUsed = (plan) => sum((plan.applied || []).flatMap(ap => ap.lots || []), l => Number(l.qty) || 0);
                            const planRemaining = (plan) => {
                                if (plan.released) return [];
                                const used = new Map();
                                for (const ap of plan.applied || []) for (const l of ap.lots || []) used.set(l.id, (used.get(l.id) || 0) + Number(l.qty));
                                return (plan.lots || []).map(l => ({ ...l, qty: Math.max(0, Number(l.qty) - (used.get(l.id) || 0)) })).filter(l => l.qty > EPS);
                            };
                            const saveLabels = async (update) => {
                                const r = await fetch('/api/tax/labels?address=' + encodeURIComponent(data.dist.address), { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(update) });
                                const j = await r.json();
                                if (!r.ok) throw new Error(j.error || 'Save failed');
                                labels = j;
                            };
                            const planLabel = (p) => `${fmtTime(p.recordedUtc)} UTC`;
                            const plansHtml = () => {
                                const plans = lotPlans();
                                if (!plans.length) return '<p class=""tr-lede"">No lot plans recorded for this wallet yet. Record one on the Sell Planner before you sell.</p>';
                                return `<div class=""data-table-wrap pnl-table-wrap""><table class=""data-table""><thead><tr><th>Recorded (UTC)</th><th>Lots chosen</th><th style=""text-align: right;"">OPT in plan</th><th style=""text-align: right;"">Used by sales</th><th style=""text-align: right;"">Still reserved</th><th>Lots</th><th></th></tr></thead><tbody>`
                                    + plans.slice().reverse().map(p => {
                                        const total = sum(p.lots || [], l => Number(l.qty) || 0), used = planUsed(p), rem = sum(planRemaining(p), l => l.qty);
                                        return `<tr><td>${esc(planLabel(p))}</td><td>${p.method === 'fifo' ? 'First in, first out' : 'Highest cost first'}</td><td class=""amount-cell"">${fmtOpt(total)}</td><td class=""amount-cell"">${fmtOpt(used)}${(p.applied || []).length ? `<div style=""font-size: 11px; color: var(--text-secondary);"">${p.applied.length} sale(s)</div>` : ''}</td><td class=""amount-cell"">${p.released ? 'Released' : fmtOpt(rem)}</td><td>${(p.lots || []).length}</td>
                                            <td style=""white-space: nowrap;""><a href=""#"" data-plan-csv=""${esc(p.id)}"" class=""header-link"">CSV</a>${rem > EPS ? ` <button type=""button"" data-plan-del=""${esc(p.id)}"" style=""font-size: 11px; padding: 2px 8px;"">${(p.applied || []).length ? 'Release the rest' : 'Delete'}</button>` : ''}</td></tr>`;
                                    }).join('') + '</tbody></table></div>';
                            };
                            // Plan list actions, shared by the Sell Planner and Sales & Lots pages (wired once on the container).
                            const wireAlt = (alt) => {
                                if (alt.dataset.wired) return;
                                alt.dataset.wired = '1';
                                alt.addEventListener('click', async (ev) => {
                                    const csv = ev.target.closest('[data-plan-csv]');
                                    if (csv) {
                                        ev.preventDefault();
                                        const p = lotPlans().find(x => x.id === csv.dataset.planCsv);
                                        if (!p) return;
                                        const lines = [['plan_recorded_utc', 'wallet', 'lots_chosen', 'lot_id', 'date_acquired', 'lot_source', 'opt', 'cost_per_opt_usd', 'cost_basis_usd', 'opt_still_reserved']];
                                        const rem = new Map(planRemaining(p).map(l => [l.id, l.qty]));
                                        for (const l of p.lots || []) lines.push([p.recordedUtc, p.wallet, p.method === 'fifo' ? 'FIFO' : 'specific identification, highest cost first', l.id, l.acquired || 'unknown', sourceText(l.source), l.qty, l.unit == null ? '' : Number(l.unit).toFixed(8), l.unit == null ? '' : (l.qty * l.unit).toFixed(2), rem.get(l.id) || 0]);
                                        download(`opt-lot-plan-${p.wallet}-${String(p.recordedUtc).slice(0, 10)}.csv`, lines);
                                        return;
                                    }
                                    const del = ev.target.closest('[data-plan-del]');
                                    if (del) {
                                        const p = lotPlans().find(x => x.id === del.dataset.planDel);
                                        if (!p) return;
                                        const used = (p.applied || []).length;
                                        if (!confirm(used ? 'Release the lots this plan still reserves? Sales that already use it keep their lots.' : 'Delete this lot plan? Its lots are no longer reserved.')) return;
                                        try {
                                            await saveLabels({ _lotPlans: used ? lotPlans().map(x => x.id === p.id ? { ...x, released: true } : x) : lotPlans().filter(x => x.id !== p.id) });
                                            status.textContent = used ? 'Released the rest of the plan.' : 'Plan deleted.';
                                            render();
                                        } catch (error) { status.textContent = 'Could not save: ' + error.message; }
                                        return;
                                    }
                                    const apply = ev.target.closest('[data-apply-plan]');
                                    if (apply) {
                                        const key = apply.dataset.applyPlan;
                                        const sel = alt.querySelector(`select[data-plan-for=""${CSS.escape(key)}""]`);
                                        const plan = lotPlans().find(x => x.id === sel?.value);
                                        const row = (data.fifoRows || []).find(x => keyOf(x.s.e) === key);
                                        if (!plan || !row) return;
                                        const s = row.s;
                                        // The identification has to exist by the time of the sale.
                                        const saleMs = s.t.date ? Date.parse(s.t.date + 'T23:59:59Z') : Date.parse(s.e.timeUtc);
                                        if (Date.parse(plan.recordedUtc) > saleMs) { status.textContent = `That plan was recorded ${planLabel(plan)}, after this sale, so it can't be the identification for it.`; return; }
                                        let left = s.e.amountOpt;
                                        const used = [];
                                        for (const l of planRemaining(plan)) { if (left <= EPS) break; const q = Math.min(l.qty, left); used.push({ id: l.id, qty: q }); left -= q; }
                                        const plans = lotPlans().map(x => x.id === plan.id ? { ...x, applied: (x.applied || []).concat([{ saleKey: key, lots: used, appliedUtc: new Date().toISOString() }]) } : x);
                                        try {
                                            await saveLabels({ _lotPlans: plans, [key]: { ...(labels[key] || {}), kind: s.t.kind, lots: used, lotPlan: plan.id } });
                                            status.textContent = `This sale now uses ${fmtOpt(sum(used, l => l.qty))} OPT of identified lots${left > EPS ? `; the other ${fmtOpt(left)} OPT use FIFO` : ''}.`;
                                            render();
                                        } catch (error) { status.textContent = 'Could not save: ' + error.message; }
                                        return;
                                    }
                                    const un = ev.target.closest('[data-unident]');
                                    if (un) {
                                        const key = un.dataset.unident;
                                        if (!confirm('Stop using identified lots for this sale? It goes back to FIFO and the lots return to the plan.')) return;
                                        const cur = { ...(labels[key] || {}) };
                                        delete cur.lots; delete cur.lotPlan;
                                        const plans = lotPlans().map(x => ({ ...x, applied: (x.applied || []).filter(ap => ap.saleKey !== key) }));
                                        try { await saveLabels({ _lotPlans: plans, [key]: Object.keys(cur).length ? cur : null }); status.textContent = 'The sale uses FIFO again.'; render(); }
                                        catch (error) { status.textContent = 'Could not save: ' + error.message; }
                                    }
                                });
                            };
                            const isLongAt = (time, ms) => time != null && (ms - Date.parse(time)) / 86400000 > 365;
                            const gainOf = (pr, c, l) => pr > c ? pr - c : pr < l ? pr - l : 0;
                            // Merge lots acquired the same day from the same source, so a big sale doesn't list every payout.
                            const mergeParts = (parts) => {
                                const out = [];
                                for (const p of parts) {
                                    const day = p.time ? fmtDay(p.time) : null, last = out[out.length - 1];
                                    if (last && last.day === day && last.source === p.source && last.long === p.long) {
                                        for (const k of ['qty', 'cost', 'proceeds', 'gain']) last[k] = (last[k] ?? 0) + (p[k] ?? 0);
                                        last.unknown = last.unknown || p.unknown;
                                    } else out.push({ ...p, day });
                                }
                                return out;
                            };
                            const lotRows = (parts, withSale) => mergeParts(parts).map(p => `<tr><td>${p.day ? esc(p.day) : 'Unknown'}</td><td>${esc(sourceText(p.source))}</td><td class=""amount-cell"">${fmtOpt(p.qty)}</td>
                                <td class=""amount-cell"">${p.unknown ? '<span class=""tr-flag"" title=""No known cost; counted as $0"">$0 †</span>' : fmtPrice(p.qty > EPS ? p.cost / p.qty : null)}</td><td class=""amount-cell"">${fmtUsd(p.cost)}</td>
                                ${withSale ? `<td class=""amount-cell"">${fmtUsd(p.proceeds)}</td><td class=""amount-cell${p.gain < -0.004 ? ' tr-loss' : ''}"">${fmtGain(p.gain)}</td>` : ''}<td>${p.long ? 'Long' : 'Short'}</td></tr>`).join('');

                            // Selling qty OPT today at price per OPT from the remaining lots, first in, first out.
                            // Anything beyond the known lots has no cost basis ($0).
                            const simulate = (lots, qty, price) => {
                                const now = Date.now(), parts = [];
                                let left = qty;
                                for (const lot of lots) {
                                    if (left <= EPS) break;
                                    const q = Math.min(lot.qty, left);
                                    parts.push({ id: lot.id, qty: q, time: lot.time, unit: lot.unit, lossUnit: lot.lossUnit, source: lot.source });
                                    left -= q;
                                }
                                if (left > EPS) parts.push({ qty: left, time: null, unit: null, lossUnit: null, source: 'none' });
                                for (const p of parts) {
                                    const c = p.unit == null ? 0 : p.qty * p.unit, l = p.lossUnit == null ? c : p.qty * p.lossUnit;
                                    p.unknown = p.unit == null; p.cost = c; p.proceeds = p.qty * price; p.gain = gainOf(p.proceeds, c, l); p.long = isLongAt(p.time, now);
                                }
                                return { qty, price, parts, proceeds: sum(parts, p => p.proceeds), cost: sum(parts, p => p.cost), gain: sum(parts, p => p.gain),
                                    stGain: sum(parts.filter(p => !p.long), p => p.gain), ltGain: sum(parts.filter(p => p.long), p => p.gain), unknown: sum(parts.filter(p => p.unknown), p => p.qty) };
                            };
                            // Smallest amount (up to cap) whose FIFO sale at price realizes a loss of at least target. The gain per OPT is
                            // constant within a lot, so the running gain is piecewise linear and the largest loss sits at a lot boundary.
                            const solveForLoss = (lots, target, price, cap) => {
                                if (target <= EPS) return { qty: 0, reached: true };
                                let x = 0, g = 0, best = { qty: 0, gain: 0 };
                                for (const lot of lots.concat([{ qty: Infinity, unit: null, lossUnit: null }])) {
                                    if (x >= cap - EPS) break;
                                    const q = Math.min(lot.qty, cap - x), c = lot.unit ?? 0, l = lot.lossUnit ?? c;
                                    const s = gainOf(price, c, l);
                                    if (s < 0 && g + s * q <= -target + EPS) return { qty: x + (-target - g) / s, reached: true };
                                    x += q; g += s * q;
                                    if (g < best.gain - EPS) best = { qty: x, gain: g };
                                }
                                return { qty: best.qty, reached: false, bestGain: best.gain };
                            };

                            const quoteCache = new Map();
                            // Average price per OPT for selling qty OPT on Osmosis now (includes price impact), or null.
                            const quotePrice = async (qty) => {
                                const q = Math.max(100, Math.round(qty));
                                if (!quoteCache.has(q)) quoteCache.set(q, getJson('/api/market/quote?opt=' + q).then(j => j.avgPriceUsd ?? null).catch(() => null));
                                return quoteCache.get(q);
                            };
                            let prefs = { target: '', include: true, mode: 'quote', price: '', method: 'hifo', goal: 'max-unstake' };
                            try { prefs = { ...prefs, ...JSON.parse(localStorage.getItem('optic.sellPlanner') || '{}') }; } catch { }
                            const savePrefs = () => { try { localStorage.setItem('optic.sellPlanner', JSON.stringify(prefs)); } catch { } };

                            const renderPlanner = async (alt) => {
                                const a = build(), year = a.year, addr = data.dist.address;
                                // Lots still held after every recorded sale (identified or FIFO, all years), less what recorded plans reserve.
                                const reservedBy = new Map();
                                for (const p of lotPlans()) for (const l of planRemaining(p)) reservedBy.set(l.id, (reservedBy.get(l.id) || 0) + l.qty);
                                const reservedOpt = sum([...reservedBy.values()], x => x);
                                const fifoLots = a.lots.filter(l => l.qty > EPS).map(l => {
                                    const r = Math.min(l.qty, reservedBy.get(l.id) || 0);
                                    if (r > 0) reservedBy.set(l.id, reservedBy.get(l.id) - r);
                                    return { ...l, qty: l.qty - r };
                                }).filter(l => l.qty > EPS);
                                // Highest cost first (specific identification): lots ordered by the basis that counts for a loss (for gifts,
                                // the lower of the giver's basis and the value when given), unknown cost last. Locks cover an amount of OPT,
                                // not particular lots, so any lot in the wallet can be identified.
                                const lossBasisOf = (l) => l.unit == null ? -1 : (l.lossUnit ?? l.unit);
                                const hifoLots = fifoLots.slice().sort((x, y) => lossBasisOf(y) - lossBasisOf(x) || String(x.key || x.time || '').localeCompare(String(y.key || y.time || '')));
                                let lots = fifoLots, lastSim = null;
                                const isHifo = () => $('sp-method').value !== 'fifo';
                                const useMethod = () => { lots = isHifo() ? hifoLots : fifoLots; };
                                const ytd = a.sales, ytdGain = sum(ytd, s => s.gain);
                                const ytdSt = ytd.filter(s => !s.long), ytdLt = ytd.filter(s => s.long);
                                alt.innerHTML = `
                                    <div class=""tr-disclaimer"" style=""font-size: 13px; line-height: 1.5; border-left: 4px solid var(--accent-primary); background: color-mix(in srgb, var(--accent-primary) 6%, transparent); padding: 10px 14px; border-radius: 6px; margin: 0 0 16px; max-width: 1040px;""><strong>Disclaimer:</strong> These are estimates from public records and your Tax Report entries. They are not tax or legal advice. Consult a tax accountant and a tax attorney before selling for a tax loss.</div>
                                    <div class=""tr-section"">
                                        <h3>Sales already made in ${esc(year)}</h3>
                                        <p class=""tr-lede"">Only sales in ${esc(year)} count here. OPT sold in earlier years already used up its oldest lots, so those sales are not counted again and the next sale starts from the lots still held. Same labels, income values and cost basis as the Tax Report (income valued at: ${esc(ruleSelect.options[ruleSelect.selectedIndex].text)}). <a href=""/page/fifo-sales?address=${encodeURIComponent(addr)}"">See every sale and its lots</a></p>
                                        <div class=""data-table-wrap pnl-table-wrap""><table class=""data-table tr-summary""><tbody>
                                            <tr><th style=""text-align: left;"">Item</th><th style=""text-align: right;"">OPT</th><th style=""text-align: right;"">Proceeds</th><th style=""text-align: right;"">Cost basis</th><th style=""text-align: right;"">Gain or (loss)</th></tr>
                                            <tr><td>Short-term (held 1 year or less)</td><td class=""amount-cell"">${fmtOpt(sum(ytdSt, s => s.qty))}</td><td class=""amount-cell"">${fmtUsd(sum(ytdSt, s => s.proceeds))}</td><td class=""amount-cell"">${fmtUsd(sum(ytdSt, s => s.cost))}</td><td class=""amount-cell"">${fmtGain(sum(ytdSt, s => s.gain))}</td></tr>
                                            <tr><td>Long-term (held over 1 year)</td><td class=""amount-cell"">${fmtOpt(sum(ytdLt, s => s.qty))}</td><td class=""amount-cell"">${fmtUsd(sum(ytdLt, s => s.proceeds))}</td><td class=""amount-cell"">${fmtUsd(sum(ytdLt, s => s.cost))}</td><td class=""amount-cell"">${fmtGain(sum(ytdLt, s => s.gain))}</td></tr>
                                            <tr class=""tr-total""><td>Net so far in ${esc(year)}</td><td class=""amount-cell"">${fmtOpt(sum(ytd, s => s.qty))}</td><td class=""amount-cell"">${fmtUsd(sum(ytd, s => s.proceeds))}</td><td class=""amount-cell"">${fmtUsd(sum(ytd, s => s.cost))}</td><td class=""amount-cell${ytdGain < -0.004 ? ' tr-loss' : ''}"">${fmtGain(ytdGain)}</td></tr>
                                        </tbody></table></div>
                                        ${a.unclassifiedOut.length ? `<div class=""tr-flag"" style=""font-size: 13px; margin-top: 6px;"">${a.unclassifiedOut.length} transfer(s) out in ${esc(year)} are not classified (${fmtOpt(sum(a.unclassifiedOut, x => x.e.amountOpt))} OPT); they are not counted as sales. Classify them on the <a href=""/page/tax-report?address=${encodeURIComponent(addr)}&year=${esc(year)}"">Tax Report</a>.</div>` : ''}
                                    </div>
                                    <div class=""tr-section"">
                                        <h3>Plan a sale</h3>
                                        <p class=""tr-lede"">By default the planner counts everything that can be free to sell by the end of the year (your balance, OPT already unbonding, and the stake that isn&#39;t locked) and finds the sale that gives the largest loss for ${esc(year)} at today&#39;s price; choose A target loss to size a smaller one. <span id=""sp-method-note""></span> Net capital losses offset capital gains, then up to $3,000 a year of other income; the rest carries forward.</p>
                                        <div style=""display: flex; gap: 8px; flex-wrap: wrap; align-items: flex-end; margin-bottom: 8px;"">
                                            <div class=""form-group"" style=""flex: 0 0 330px; margin-bottom: 0;""><label for=""sp-goal"">Goal</label><select id=""sp-goal""><option value=""max-unstake"">Largest loss: everything that can be free by year end</option><option value=""max-now"">Largest loss from what I can sell now</option><option value=""target"">A target loss</option></select></div>
                                            <div class=""form-group"" style=""flex: 0 0 200px; margin-bottom: 0;""><label for=""sp-target"">Target loss for ${esc(year)} (USD)</label><input type=""number"" id=""sp-target"" min=""0"" step=""any"" placeholder=""e.g. 3000"" value=""${esc(prefs.target)}""></div>
                                            <div class=""form-group"" style=""flex: 0 0 300px; margin-bottom: 0;""><label for=""sp-method"">Lots sold</label><select id=""sp-method""><option value=""hifo"">Highest cost first (specific identification)</option><option value=""fifo"">First in, first out (FIFO)</option></select></div>
                                            <div class=""form-group"" style=""flex: 0 0 290px; margin-bottom: 0;""><label for=""sp-mode"">Sale price</label><select id=""sp-mode""><option value=""quote"">Osmosis quote for the amount (with price impact)</option><option value=""fixed"">Price I enter</option></select></div>
                                            <div class=""form-group"" style=""flex: 0 0 170px; margin-bottom: 0;""><label for=""sp-price"">Price per OPT (USD)</label><input type=""number"" id=""sp-price"" min=""0"" step=""any"" value=""${esc(prefs.price)}""></div>
                                            <button type=""button"" id=""sp-calc"">Calculate</button>
                                        </div>
                                        <label style=""display: flex; gap: 6px; align-items: center; font-size: 13px; margin-bottom: 10px;""><input type=""checkbox"" id=""sp-include""${prefs.include ? ' checked' : ''}> Count the sales already made in ${esc(year)} toward the target</label>
                                        <div id=""sp-result"" style=""font-size: 14px; margin-bottom: 10px;""></div>
                                        <div id=""sp-lots-wrap"" hidden>
                                            <h4 id=""sp-lots-title"">Lots this sale would use</h4>
                                            <div class=""tr-no-print"" style=""margin-bottom: 6px; display: flex; gap: 10px; flex-wrap: wrap; align-items: center;""><button type=""button"" id=""sp-record"">Record these lots for my sale</button><a href=""#"" id=""sp-lots-csv"" class=""header-link"">Download these lots (CSV)</a></div>
                                            <p class=""tr-lede"" style=""margin: 0 0 6px;"">Recording saves this exact list with the time, before you sell, and reserves the lots so no other plan uses them. After the sale shows up on chain, match it to this plan on <a href=""/page/fifo-sales?address=${encodeURIComponent(addr)}"">Sales &amp; Lots</a>; the Tax Report then uses these lots for it.</p>
                                            <div class=""data-table-wrap pnl-table-wrap""><table class=""data-table""><thead><tr><th>Acquired</th><th>Source</th><th style=""text-align: right;"">OPT</th><th style=""text-align: right;"">Cost per OPT</th><th style=""text-align: right;"">Cost basis</th><th style=""text-align: right;"">Proceeds</th><th style=""text-align: right;"">Gain or (loss)</th><th>Term</th></tr></thead><tbody id=""sp-lots""></tbody></table></div>
                                        </div>
                                    </div>
                                    <div class=""tr-section"">
                                        <h3>Recorded lot plans</h3>
                                        ${reservedOpt > EPS ? `<p class=""tr-lede"">${fmtOpt(reservedOpt)} OPT are reserved by recorded plans that no sale has used yet. They are left out of the plan above and the amounts below.</p>` : ''}
                                        <div id=""sp-plans"">${plansHtml()}</div>
                                    </div>
                                    <div class=""tr-section"">
                                        <h3>Most you can sell</h3>
                                        <p class=""tr-lede"" id=""sp-max-lede"">Reading your balance, stake and locks...</p>
                                        <div class=""data-table-wrap pnl-table-wrap""><table class=""data-table""><thead><tr><th>Amount</th><th style=""text-align: right;"">OPT</th><th style=""text-align: right;"">Price per OPT</th><th style=""text-align: right;"">Proceeds</th><th style=""text-align: right;"">Cost basis</th><th style=""text-align: right;"">Short-term</th><th style=""text-align: right;"">Long-term</th><th style=""text-align: right;"">Gain or (loss)</th><th style=""text-align: right;"">${esc(year)} total with sales made</th></tr></thead><tbody id=""sp-max""></tbody></table></div>
                                    </div>
                                    <div class=""tr-section"">
                                        <h3>Lots still in this wallet</h3>
                                        <p class=""tr-lede"">${fmtOpt(a.held)} OPT with a cost basis of ${fmtUsd(a.heldCost)}, oldest first: the next sale uses the top rows first. Staked and locked OPT are included, since staking doesn&#39;t change which lots are yours.</p>
                                        <div class=""data-table-wrap pnl-table-wrap"" style=""max-height: 480px;""><table class=""data-table""><thead><tr><th>Acquired</th><th>Source</th><th style=""text-align: right;"">OPT</th><th style=""text-align: right;"">Cost per OPT</th><th style=""text-align: right;"">Cost basis</th><th>Term today</th></tr></thead><tbody>${lotRows(simulate(fifoLots, sum(fifoLots, l => l.qty), 0).parts.filter(p => p.source !== 'none'), false) || '<tr><td colspan=""6"" style=""text-align: center; color: var(--text-secondary);"">No OPT left in this wallet</td></tr>'}</tbody></table></div>
                                    </div>`;
                                $('sp-mode').value = prefs.mode === 'fixed' ? 'fixed' : 'quote';
                                $('sp-method').value = prefs.method === 'fifo' ? 'fifo' : 'hifo';
                                useMethod();
                                const priceInput = $('sp-price');
                                const syncMode = () => { priceInput.disabled = $('sp-mode').value === 'quote'; };
                                syncMode();

                                // Chain balance, stake and locks: a bank send needs the balance left to cover locked OPT that isn't staked.
                                let st = data.staking && data.staking.address === addr ? data.staking : null;
                                if (!st) {
                                    try { st = await getJson('/api/staking/positions?address=' + encodeURIComponent(addr)); data.staking = st; }
                                    catch (error) { st = null; $('sp-max-lede').textContent = 'Could not read the balance and locks: ' + error.message + '. Limits below use the OPT held by FIFO.'; }
                                }
                                if (!alt.isConnected || data.dist.address !== addr) return;
                                const t = st ? st.totals : null;
                                const capNow = Math.max(0, (t && t.sendableNowOpt != null ? t.sendableNowOpt : a.held) - reservedOpt);
                                const free = t && t.freeToUndelegateOpt >= 1 ? t.freeToUndelegateOpt : 0; // ignore dust left after unstaking
                                // Unbonding already under way that finishes this year, and the last day to start new unbonding in time.
                                const yearEndMs = Date.UTC(Number(year), 11, 31, 23, 59, 59);
                                const unbYear = (t && t.unbondingEntries ? t.unbondingEntries : []).filter(x => x.completes && Date.parse(x.completes) <= yearEndMs);
                                const unbOpt = sum(unbYear, x => x.opt);
                                const unbLast = unbYear.map(x => x.completes).sort().pop() || null;
                                const unbDays = Math.ceil(t && t.unbondingDays ? t.unbondingDays : 21);
                                const startBy = new Date(yearEndMs - (unbDays + 2) * 86400000);
                                const freeInTime = Date.now() <= startBy.getTime() ? free : 0;
                                const capYear = capNow + unbOpt + freeInTime;
                                const priceFor = async (qty) => $('sp-mode').value === 'fixed' ? (Number(priceInput.value) > 0 ? Number(priceInput.value) : null) : await quotePrice(qty);
                                const spot = await quotePrice(100);
                                if (spot != null && !priceInput.value) priceInput.placeholder = spot.toFixed(6);

                                const maxRow = (label, sub, sim) => `<tr><td>${label}<div style=""font-size: 11px; color: var(--text-secondary);"">${sub}</div></td><td class=""amount-cell"">${fmtOpt(sim.qty)}</td><td class=""amount-cell"">${fmtPrice(sim.price)}</td><td class=""amount-cell"">${fmtUsd(sim.proceeds)}</td><td class=""amount-cell"">${fmtUsd(sim.cost)}</td>
                                    <td class=""amount-cell"">${fmtGain(sim.stGain)}</td><td class=""amount-cell"">${fmtGain(sim.ltGain)}</td><td class=""amount-cell${sim.gain < -0.004 ? ' tr-loss' : ''}"">${fmtGain(sim.gain)}</td><td class=""amount-cell${sim.gain + ytdGain < -0.004 ? ' tr-loss' : ''}"">${fmtGain(sim.gain + ytdGain)}</td></tr>`;
                                const renderMax = async () => {
                                    useMethod();
                                    if (t) $('sp-max-lede').innerHTML = `Balance ${fmtOpt(t.balanceOpt)} OPT not staked, ${fmtOpt(t.stakedOpt)} OPT staked, ${fmtOpt(t.lockedOpt)} OPT locked. ${t.lockedOpt > t.stakedOpt ? `${fmtOpt(t.lockedOpt - t.stakedOpt)} OPT of the lock is not covered by stake, so that much of the balance can&#39;t be sent.` : 'Your locks are covered by your stake, so the whole unstaked balance can be sent.'} ${unbOpt > EPS ? `Another ${fmtOpt(unbOpt)} OPT is already unbonding and becomes free on ${fmtDate(unbLast)}. ` : ''}Unstaking the stake that isn&#39;t locked frees ${fmtOpt(free)} OPT more after the ${t.unbondingDays ? Math.round(t.unbondingDays) + '-day ' : ''}unbonding period. Prices are ${$('sp-mode').value === 'fixed' ? 'the price you entered' : 'Osmosis quotes for each amount, so larger sales get a lower average price'}. Lots sold: ${isHifo() ? 'highest cost first' : 'first in, first out'}.`;
                                    const rows = [];
                                    const p1 = await priceFor(capNow);
                                    if (p1 != null) rows.push(maxRow('Sellable now', 'Your balance that can be sent today', simulate(lots, capNow, p1)));
                                    if (unbOpt > EPS) {
                                        const p2 = await priceFor(capNow + unbOpt);
                                        if (p2 != null) rows.push(maxRow('Plus OPT already unbonding', `${fmtOpt(unbOpt)} OPT more, free on ${fmtDate(unbLast)}`, simulate(lots, capNow + unbOpt, p2)));
                                    }
                                    if (free > EPS) {
                                        const p3 = await priceFor(capNow + unbOpt + free);
                                        if (p3 != null) rows.push(maxRow('Plus unstaking what isn&#39;t locked', `${fmtOpt(free)} OPT more, free ${unbDays} days after you unstake${freeInTime > EPS ? ` (start by ${fmtDate(startBy.toISOString())} to sell in ${esc(year)})` : ` (too late to be free in ${esc(year)})`}`, simulate(lots, capNow + unbOpt + free, p3)));
                                    }
                                    $('sp-max').innerHTML = rows.join('') || '<tr><td colspan=""9"" style=""text-align: center; color: var(--text-secondary);"">No price available. Choose Price I enter and give a price.</td></tr>';
                                };
                                // Largest loss: sell as much as helps, up to what is sellable now (or after unstaking the unlocked stake).
                                const calcMax = async (goal) => {
                                    const out = $('sp-result'), fixed = $('sp-mode').value === 'fixed';
                                    const yearGoal = goal === 'max-unstake', unstake = yearGoal && freeInTime > EPS;
                                    const cap = yearGoal ? capYear : capNow;
                                    out.textContent = 'Working it out...';
                                    let price = await priceFor(fixed ? 0 : cap), r = null;
                                    if (price == null) { out.innerHTML = '<span class=""tr-flag"">No price available.</span> Choose Price I enter and give a price per OPT.'; return; }
                                    for (let i = 0; i < 6; i++) {
                                        r = solveForLoss(lots, Infinity, price, cap);
                                        if (fixed || r.qty <= EPS) break;
                                        const p = await priceFor(r.qty);
                                        if (p == null || Math.abs(p - price) <= price * 0.001) { if (p != null) price = p; break; }
                                        price = p;
                                    }
                                    r = solveForLoss(lots, Infinity, price, cap);
                                    const sim = simulate(lots, r.qty, price), total = sim.gain + ytdGain;
                                    lastSim = r.qty > EPS ? sim : null;
                                    let n = 0;
                                    const steps = (yearGoal && unbOpt > EPS ? `<div style=""margin-bottom: 6px;""><strong>${++n}. Wait for the ${fmtOpt(unbOpt)} OPT already unbonding</strong>; it is free on ${fmtDate(unbLast)}.</div>` : '')
                                        + (unstake ? `<div style=""margin-bottom: 6px;""><strong>${++n}. Unstake ${fmtOpt(free)} OPT</strong>, all the stake that isn&#39;t locked (how much from each validator is on <a href=""/page/my-staking?address=${encodeURIComponent(addr)}"">My Staking</a>). Unbonding takes ${unbDays} days, so start by <strong>${fmtDate(startBy.toISOString())}</strong> for it to be free to sell before the end of ${esc(year)}.</div>` : '')
                                        + (yearGoal && free > EPS && !unstake ? `<div class=""tr-flag"" style=""margin-bottom: 6px;"">It is too late to unstake more and have it free before the end of ${esc(year)}.</div>` : '');
                                    if (cap <= EPS) { out.innerHTML = steps + `<div class=""tr-flag"">No OPT is free to sell${reservedOpt > EPS ? `: ${fmtOpt(reservedOpt)} OPT is reserved by recorded plans` : ''}.</div>`; return; }
                                    if (r.qty <= EPS) {
                                        out.innerHTML = steps + `<div class=""tr-flag"">No sale gives a loss: none of the lots ${isHifo() ? 'in this wallet' : 'FIFO would sell first'} cost more than today&#39;s price of about ${fmtPrice(price)}.</div>`;
                                        return;
                                    }
                                    out.innerHTML = steps
                                        + `<div style=""font-size: 16px; margin-bottom: 4px;""><strong>${n ? (n + 1) + '. ' : ''}Sell ${fmtOpt(sim.qty)} OPT</strong> at about ${fmtPrice(price)} per OPT${isHifo() ? ', identifying the lots listed below' : ''}.</div>`
                                        + `<div>Proceeds ${fmtUsd(sim.proceeds)}, cost basis ${fmtUsd(sim.cost)}, <span class=""tr-loss"">loss ${fmtUsd(-sim.gain)}</span> (short-term ${fmtGain(sim.stGain)}, long-term ${fmtGain(sim.ltGain)}).</div>`
                                        + `<div>${esc(year)} net with the sales already made: <strong class=""${total < -0.004 ? 'tr-loss' : ''}"">${fmtGain(total)}</strong>.</div>`
                                        + (cap - sim.qty > 1 ? `<div style=""margin-top: 4px;"">Selling the other ${fmtOpt(cap - sim.qty)} OPT you could sell would lower the loss: those lots cost less than today&#39;s price.</div>` : '')
                                        + (sim.unknown > EPS ? `<div class=""tr-flag"" style=""margin-top: 4px;"">${fmtOpt(sim.unknown)} OPT of this sale have no known cost and are counted at $0. Classify them on the Tax Report.</div>` : '')
                                        + (fixed ? '' : '<div style=""font-size: 12px; color: var(--text-secondary); margin-top: 4px;"">Priced with an Osmosis quote for the whole amount. The price moves with every trade and with your own selling; check the quote again right before you sell, and the planner will resize the sale.</div>');
                                    $('sp-lots').innerHTML = lotRows(sim.parts, true);
                                    $('sp-lots-wrap').hidden = false;
                                };
                                const calc = async () => {
                                    prefs = { target: $('sp-target').value, include: $('sp-include').checked, mode: $('sp-mode').value, price: priceInput.value, method: $('sp-method').value, goal: $('sp-goal').value };
                                    useMethod();
                                    $('sp-target').disabled = $('sp-goal').value !== 'target';
                                    $('sp-include').closest('label').hidden = $('sp-goal').value !== 'target';
                                    $('sp-method-note').innerHTML = isHifo()
                                        ? 'It sells the lots with the highest cost basis first, which gives the largest loss per OPT. With specific identification you must record which lots you are selling, in your own books, before or at the time of the sale (Treas. Reg. &#167;1.1012-1(j)); download the list below and keep it. The lots must be in the wallet you sell from.'
                                        : 'It sells this wallet&#39;s oldest lots first (FIFO), the default when you don&#39;t identify lots.';
                                    $('sp-lots-title').textContent = isHifo() ? 'Lots to identify for this sale, highest cost first' : 'Lots this sale would use, oldest first';
                                    savePrefs();
                                    renderMax();
                                    const out = $('sp-result');
                                    $('sp-lots-wrap').hidden = true;
                                    if ($('sp-goal').value !== 'target') return calcMax($('sp-goal').value);
                                    const target = Number($('sp-target').value);
                                    if (!(target > 0)) { out.innerHTML = '<span style=""color: var(--text-secondary);"">Enter a target loss to see how much to sell.</span>'; return; }
                                    const needed = target + ($('sp-include').checked ? ytdGain : 0);
                                    if (needed <= 0.004) { out.innerHTML = `The sales already made in ${esc(year)} give a net ${fmtGain(ytdGain)}, which already meets a ${fmtUsd(target)} loss. Nothing more needs to be sold.`; return; }
                                    out.textContent = 'Working it out...';
                                    // With Osmosis quotes the price depends on the size, so solve, re-quote at that size and solve again.
                                    let price = await priceFor($('sp-mode').value === 'fixed' ? 0 : 100), r = null;
                                    if (price == null) { out.innerHTML = '<span class=""tr-flag"">No price available.</span> Choose Price I enter and give a price per OPT.'; return; }
                                    for (let i = 0; i < 6; i++) {
                                        r = solveForLoss(lots, needed, price, capNow);
                                        if ($('sp-mode').value === 'fixed') break;
                                        const p = await priceFor(r.qty);
                                        if (p == null || Math.abs(p - price) <= price * 0.001) { if (p != null) price = p; break; }
                                        price = p;
                                    }
                                    r = solveForLoss(lots, needed, price, capNow);
                                    const sim = simulate(lots, r.qty, price);
                                    const total = sim.gain + ytdGain;
                                    // When the target is out of reach, also look across every lot in the wallet, locked OPT included.
                                    const all = r.reached ? null : solveForLoss(lots, needed, price, a.held);
                                    // Next step up: unstake the stake that isn't locked, then sell (priced for that larger size).
                                    const pU = r.reached || capYear - capNow <= EPS ? null : await priceFor(capYear);
                                    const unstaked = pU == null ? null : solveForLoss(lots, needed, pU, capYear);
                                    const unstakedSim = unstaked ? simulate(lots, unstaked.qty, pU) : null;
                                    const unstakeLine = !unstakedSim || unstaked.qty <= EPS ? ''
                                        : unstaked.reached
                                            ? `<div style=""margin-bottom: 4px;"">With the OPT that can still be freed this year (${fmtOpt(capYear - capNow)} OPT from unbonding and unstaking what isn&#39;t locked), selling <strong>${fmtOpt(unstaked.qty)} OPT</strong> at about ${fmtPrice(pU)} reaches it: loss ${fmtUsd(-unstakedSim.gain)}.</div>`
                                            : `<div style=""margin-bottom: 4px;"">With the OPT that can still be freed this year (${fmtOpt(capYear - capNow)} OPT from unbonding and unstaking what isn&#39;t locked), the largest loss is <strong>${fmtUsd(-unstakedSim.gain)}</strong> from selling ${fmtOpt(unstaked.qty)} OPT at about ${fmtPrice(pU)}.</div>`;
                                    const front = simulate(lots, Math.min(capNow, a.held), price).parts;
                                    const giftFront = sum(front.filter(p => p.source === 'gift'), p => p.qty);
                                    out.innerHTML = (r.reached
                                        ? `<div style=""font-size: 16px; margin-bottom: 4px;""><strong>Sell ${fmtOpt(sim.qty)} OPT</strong> at about ${fmtPrice(price)} per OPT.</div>`
                                        : `<div class=""tr-flag"" style=""margin-bottom: 4px;"">A ${fmtUsd(target)} loss can&#39;t be reached by selling the ${fmtOpt(capNow)} OPT that isn&#39;t locked at this price.</div>`
                                            + (r.qty > EPS ? `<div style=""margin-bottom: 4px;"">The largest loss comes from selling <strong>${fmtOpt(sim.qty)} OPT</strong> at about ${fmtPrice(price)}.</div>`
                                                : isHifo() ? `<div style=""margin-bottom: 4px;"">None of your lots has a cost basis above today&#39;s price of about ${fmtPrice(price)}, so no sale gives a loss.</div>`
                                                : `<div style=""margin-bottom: 4px;"">The oldest lots, which FIFO sells first, cost less than today&#39;s price of about ${fmtPrice(price)}, so any sale of OPT you can sell now is a gain.${giftFront > EPS ? ` ${fmtOpt(giftFront)} OPT of them are gifts: their basis is the giver&#39;s basis entered on the Tax Report ($0 if none), and for a loss it is the lower of that and the value when given.` : ''}</div>`)
                                            + unstakeLine
                                            + (unstaked && unstaked.reached ? '' : all.reached ? `<div style=""margin-bottom: 4px;"">Counting locked OPT too, it would take selling ${fmtOpt(all.qty)} OPT, which isn&#39;t possible until the locks end.</div>`
                                                : all.bestGain < -0.004 ? `<div style=""margin-bottom: 4px;"">Even counting locked OPT, the largest possible loss is ${fmtUsd(-all.bestGain)} from selling ${fmtOpt(all.qty)} OPT.</div>` : ''))
                                        + (r.qty > EPS
                                            ? `<div>Proceeds ${fmtUsd(sim.proceeds)}, cost basis ${fmtUsd(sim.cost)}, <span class=""${sim.gain < -0.004 ? 'tr-loss' : ''}"">${sim.gain < 0 ? 'loss' : 'gain'} ${fmtUsd(Math.abs(sim.gain))}</span> (short-term ${fmtGain(sim.stGain)}, long-term ${fmtGain(sim.ltGain)}).</div>`
                                              + `<div>${esc(year)} net with the sales already made: <strong class=""${total < -0.004 ? 'tr-loss' : ''}"">${fmtGain(total)}</strong>. ${fmtOpt(capNow - sim.qty)} OPT not locked would be left to sell.</div>`
                                            : '')
                                        + (sim.unknown > EPS ? `<div class=""tr-flag"" style=""margin-top: 4px;"">${fmtOpt(sim.unknown)} OPT of this sale have no known cost (receipts not classified, or no record) and are counted at $0, which understates the loss. Classify them on the Tax Report.</div>` : '')
                                        + ($('sp-mode').value === 'quote' ? '<div style=""font-size: 12px; color: var(--text-secondary); margin-top: 4px;"">The Osmosis price moves with every trade; check the quote again right before you sell.</div>' : '');
                                    lastSim = r.qty > EPS ? sim : null;
                                    $('sp-lots').innerHTML = lotRows(sim.parts, true);
                                    $('sp-lots-wrap').hidden = !lastSim;
                                };
                                $('sp-calc').addEventListener('click', calc);
                                $('sp-target').addEventListener('keydown', (e) => { if (e.key === 'Enter') { e.preventDefault(); calc(); } });
                                $('sp-mode').addEventListener('change', () => { syncMode(); if ($('sp-mode').value === 'fixed' && !priceInput.value && spot != null) priceInput.value = spot.toFixed(6); calc(); });
                                $('sp-include').addEventListener('change', calc);
                                $('sp-method').addEventListener('change', calc);
                                $('sp-goal').value = ['max-unstake', 'max-now', 'target'].includes(prefs.goal) ? prefs.goal : 'max-unstake';
                                $('sp-goal').addEventListener('change', calc);
                                wireAlt(alt);
                                $('sp-record').addEventListener('click', async () => {
                                    if (!lastSim) return;
                                    const byId = new Map();
                                    for (const p of lastSim.parts) {
                                        if (!p.id) continue;
                                        const x = byId.get(p.id) || { id: p.id, qty: 0, acquired: p.time ? fmtDay(p.time) : null, source: p.source, unit: p.unit };
                                        x.qty += p.qty;
                                        byId.set(p.id, x);
                                    }
                                    const lotsChosen = [...byId.values()];
                                    if (!lotsChosen.length) return;
                                    const total = sum(lotsChosen, l => l.qty);
                                    if (!confirm(`Record ${fmtOpt(total)} OPT in ${lotsChosen.length} lots as the lots you will sell from this wallet? The time is saved and the lots are reserved until a sale uses them.`)) return;
                                    const plan = { id: 'plan-' + Date.now().toString(36) + Math.random().toString(36).slice(2, 6), wallet: addr, method: isHifo() ? 'highest-cost' : 'fifo', qty: total, plannedPrice: lastSim.price, plannedGainUsd: lastSim.gain, lots: lotsChosen, applied: [] };
                                    try { await saveLabels({ _lotPlans: lotPlans().concat([plan]) }); status.textContent = `Recorded ${fmtOpt(total)} OPT in ${lotsChosen.length} lots.`; render(); }
                                    catch (error) { status.textContent = 'Could not save: ' + error.message; }
                                });
                                // The lots to record before selling (specific identification needs the record by the time of the sale).
                                $('sp-lots-csv').addEventListener('click', (e) => {
                                    e.preventDefault();
                                    if (!lastSim) return;
                                    const lines = [['planned_on_utc', 'wallet', 'method', 'date_acquired', 'lot_source', 'opt', 'cost_per_opt_usd', 'cost_basis_usd', 'cost_known', 'term', 'planned_price_usd', 'planned_proceeds_usd', 'planned_gain_loss_usd']];
                                    const when = new Date().toISOString().slice(0, 16).replace('T', ' ');
                                    for (const p of lastSim.parts) lines.push([when, addr, isHifo() ? 'specific identification, highest cost first' : 'FIFO', p.time ? fmtDay(p.time) : 'unknown', sourceText(p.source), p.qty, p.qty > EPS ? (p.cost / p.qty).toFixed(8) : '', p.cost.toFixed(2), p.unknown ? 'no' : 'yes', p.long ? 'long' : 'short', lastSim.price.toFixed(8), p.proceeds.toFixed(2), p.gain.toFixed(2)]);
                                    download(`opt-lots-to-sell-${addr}-${when.slice(0, 10)}.csv`, lines);
                                });
                                calc();
                            };

                            const renderFifo = (alt) => {
                                const keep = yearSelect.value;
                                const years = [...yearSelect.options].map(o => o.value).sort();
                                const byYear = [];
                                let last = null;
                                for (const y of years) { yearSelect.value = y; last = build(); byYear.push({ year: y, sales: last.sales }); }
                                yearSelect.value = keep;
                                const addr = data.dist.address;
                                // Per-lot proceeds and gain: each lot gets its share of the sale.
                                const partsOf = (s) => (s.parts || []).map(p => {
                                    const proceeds = s.proceeds == null ? null : s.proceeds * p.qty / s.qty, c = p.cost ?? 0, l = p.lossCost ?? c;
                                    return { ...p, cost: c, unknown: p.cost == null, proceeds, gain: proceeds == null ? null : gainOf(proceeds, c, l), long: s.long };
                                });
                                data.fifoRows = byYear.flatMap(y => y.sales.map(s => ({ s, parts: partsOf(s) })));
                                const yearRow = (y) => {
                                    const st = y.sales.filter(s => !s.long), lt = y.sales.filter(s => s.long);
                                    return `<tr><td><a href=""#fifo-${esc(y.year)}"">${esc(y.year)}</a></td><td class=""amount-cell"">${y.sales.length}</td><td class=""amount-cell"">${fmtOpt(sum(y.sales, s => s.qty))}</td><td class=""amount-cell"">${fmtUsd(sum(y.sales, s => s.proceeds))}</td><td class=""amount-cell"">${fmtUsd(sum(y.sales, s => s.cost))}</td>
                                        <td class=""amount-cell"">${fmtGain(sum(st, s => s.gain))}</td><td class=""amount-cell"">${fmtGain(sum(lt, s => s.gain))}</td><td class=""amount-cell${sum(y.sales, s => s.gain) < -0.004 ? ' tr-loss' : ''}"">${fmtGain(sum(y.sales, s => s.gain))}</td></tr>`;
                                };
                                // Lot method of each sale, and the controls to use a recorded plan for it (once per transfer).
                                const shownKeys = new Set();
                                const identCell = (s) => {
                                    const key = keyOf(s.e), first = !shownKeys.has(key);
                                    shownKeys.add(key);
                                    const badge = s.identified ? '<span class=""tr-badge ok"">Identified lots</span>' : '<span class=""tr-badge"" style=""border: 1px solid var(--border);"">FIFO</span>';
                                    const miss = s.identMissing > EPS ? `<div class=""tr-flag"" style=""font-size: 11px;"">${fmtOpt(s.identMissing)} OPT of the identified lots were not held at the time of this sale (sold earlier or received later); FIFO lots were used instead</div>` : '';
                                    if (!first) return `<div style=""margin-bottom: 4px;"">${badge}</div>`;
                                    const avail = lotPlans().filter(p => planRemaining(p).length);
                                    const ctl = s.t.lots ? `<button type=""button"" data-unident=""${esc(key)}"" style=""font-size: 11px; padding: 2px 8px;"">Use FIFO instead</button>`
                                        : avail.length ? `<select data-plan-for=""${esc(key)}"" style=""font-size: 12px; padding: 2px 4px; max-width: 220px;"">${avail.map(p => `<option value=""${esc(p.id)}"">Plan ${esc(planLabel(p))}, ${fmtOpt(sum(planRemaining(p), l => l.qty))} OPT left</option>`).join('')}</select> <button type=""button"" data-apply-plan=""${esc(key)}"" style=""font-size: 11px; padding: 2px 8px;"">Use these lots</button>` : '';
                                    return `<div style=""margin-bottom: 4px; display: flex; gap: 6px; flex-wrap: wrap; align-items: center;"">${badge}${ctl}</div>${miss}`;
                                };
                                const saleRow = (s) => {
                                    const parts = partsOf(s), merged = mergeParts(parts);
                                    return `<tr><td>${esc(s.soldOn)}</td><td>${partyLabel(s.e.counterparty)}<div style=""font-size: 11px; color: var(--text-secondary);"">${esc(OUT_KINDS[s.t.kind].label)}</div></td><td class=""amount-cell"">${fmtOpt(s.qty)}</td>
                                        <td class=""amount-cell"">${fmtUsd(s.proceeds)}${s.proceedsEstimated ? '*' : ''}</td><td class=""amount-cell"">${fmtUsd(s.cost)}${s.unknownBasis ? '<span class=""tr-flag"" title=""Part has no known cost; counted as $0""> †</span>' : ''}</td>
                                        <td class=""amount-cell${s.gain < -0.004 ? ' tr-loss' : ''}"">${fmtGain(s.gain)}</td><td>${s.long ? 'Long' : 'Short'}</td>
                                        <td>${identCell(s)}<details><summary style=""cursor: pointer; white-space: nowrap;"">${merged.length} lot${merged.length === 1 ? '' : 's'} · ${esc(s.acquired === 'Various' ? s.acqFrom + ' to ' + s.acqTo : s.acquired)}</summary>
                                            <div class=""data-table-wrap"" style=""margin-top: 4px;""><table class=""data-table""><thead><tr><th>Acquired</th><th>Source</th><th style=""text-align: right;"">OPT</th><th style=""text-align: right;"">Cost per OPT</th><th style=""text-align: right;"">Cost basis</th><th style=""text-align: right;"">Proceeds</th><th style=""text-align: right;"">Gain or (loss)</th><th>Term</th></tr></thead><tbody>${lotRows(parts, true)}</tbody></table></div>
                                        </details></td>${txCell(s.e.txHash)}</tr>`;
                                };
                                const held = simulate(last.lots.filter(l => l.qty > EPS), last.held, 0).parts.filter(p => p.source !== 'none');
                                wireAlt(alt);
                                alt.innerHTML = `
                                    <div class=""tr-section"">
                                        <p class=""tr-lede"">Every sale from this wallet and the lots it used, tracked wallet by wallet (Rev. Proc. 2024-28): first in, first out, unless you recorded the lots for a sale before making it (specific identification). Each lot can be sold once; later years only see what is left. Built the same way as the Tax Report: your labels, income valued at ${esc(ruleSelect.options[ruleSelect.selectedIndex].text.toLowerCase())}, and cost basis carried between your own wallets. Transfers that are not classified are not counted as sales. Proceeds you didn&#39;t enter are the value that day and are marked *.</p>
                                        <div class=""tr-no-print"" style=""margin-bottom: 8px;""><a href=""#"" id=""fifo-csv"" class=""header-link"">Download every lot sold (CSV)</a> · <a href=""/page/sell-planner?address=${encodeURIComponent(addr)}"">Sell Planner</a></div>
                                        <h3>Recorded lot plans</h3>
                                        <p class=""tr-lede"">Plans recorded on the Sell Planner. To use one, pick it on the sale below (Use these lots); the sale then takes exactly those lots, and they are gone for every later sale.</p>
                                        ${plansHtml()}
                                        <h3>By year</h3>
                                        <div class=""data-table-wrap pnl-table-wrap""><table class=""data-table tr-summary""><thead><tr><th>Year</th><th style=""text-align: right;"">Sales</th><th style=""text-align: right;"">OPT</th><th style=""text-align: right;"">Proceeds</th><th style=""text-align: right;"">Cost basis</th><th style=""text-align: right;"">Short-term</th><th style=""text-align: right;"">Long-term</th><th style=""text-align: right;"">Net gain or (loss)</th></tr></thead><tbody>${byYear.slice().reverse().map(yearRow).join('')}</tbody></table></div>
                                    </div>
                                    ${byYear.slice().reverse().map(y => `<div class=""tr-section"" id=""fifo-${esc(y.year)}""><h3>${esc(y.year)} sales</h3>
                                        <div class=""data-table-wrap pnl-table-wrap""><table class=""data-table""><thead><tr><th>Sold</th><th>To / what it was</th><th style=""text-align: right;"">OPT</th><th style=""text-align: right;"">Proceeds</th><th style=""text-align: right;"">Cost basis</th><th style=""text-align: right;"">Gain or (loss)</th><th>Term</th><th>Lots used</th><th>Tx</th></tr></thead>
                                        <tbody>${y.sales.map(saleRow).join('') || `<tr><td colspan=""9"" style=""text-align: center; color: var(--text-secondary);"">No sales in ${esc(y.year)}</td></tr>`}</tbody></table></div></div>`).join('')}
                                    <div class=""tr-section""><h3>Lots still held, next to be sold first</h3>
                                        <p class=""tr-lede"">${fmtOpt(last.held)} OPT with a cost basis of ${fmtUsd(last.heldCost)}.</p>
                                        <div class=""data-table-wrap pnl-table-wrap"" style=""max-height: 480px;""><table class=""data-table""><thead><tr><th>Acquired</th><th>Source</th><th style=""text-align: right;"">OPT</th><th style=""text-align: right;"">Cost per OPT</th><th style=""text-align: right;"">Cost basis</th><th>Term today</th></tr></thead><tbody>${lotRows(held, false) || '<tr><td colspan=""6"" style=""text-align: center; color: var(--text-secondary);"">No OPT left in this wallet</td></tr>'}</tbody></table></div>
                                    </div>`;
                                $('fifo-csv').addEventListener('click', (e) => {
                                    e.preventDefault();
                                    const lines = [['date_sold', 'date_acquired', 'opt', 'proceeds_usd', 'proceeds_estimated', 'cost_basis_usd', 'cost_known', 'gain_loss_usd', 'term', 'lot_source', 'treatment', 'sent_to', 'tx_hash']];
                                    for (const { s, parts } of data.fifoRows) for (const p of parts)
                                        lines.push([s.soldOn, p.time ? fmtDay(p.time) : 'unknown', p.qty, p.proceeds?.toFixed(2), s.proceedsEstimated ? 'yes' : 'no', p.cost.toFixed(2), p.unknown ? 'no' : 'yes', p.gain?.toFixed(2), s.long ? 'long' : 'short', sourceText(p.source), OUT_KINDS[s.t.kind].label, s.e.counterparty, s.e.txHash]);
                                    download(`opt-fifo-lots-${addr}.csv`, lines);
                                });
                            };

                            const renderAlt = () => {
                                const alt = $('tr-alt');
                                alt.hidden = false;
                                if (MODE === 'fifo') renderFifo(alt);
                                else renderPlanner(alt).catch(error => { status.textContent = 'Error: ' + error.message; });
                            };

                            const render = () => {
                                if (MODE !== 'report') { renderAlt(); return; }
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
                                // THORWallet rewards received this year in your 0x... wallets (USDC at $1, TITN at the day's close).
                                const allAssets = $('tr-assets').value !== 'opt';
                                const titnYear = ((allAssets && data.titn && data.titn.rewards) || []).filter(r => String(r.time).startsWith(String(a.year)));
                                const titnUnpriced = titnYear.filter(r => r.usd == null);
                                // TGT and TITN sales and exchanges this year (section 4a).
                                const tl = allAssets ? (data.titnLots || null) : null;
                                const tlYear = ((tl && tl.disposals) || []).filter(d => String(d.sold).startsWith(String(a.year)));
                                const tlNoCost = tlYear.filter(d => d.cost == null || d.proceeds == null);
                                const tlUnclassified = ((tl && tl.unclassified) || []).filter(u => String(u.time).startsWith(String(a.year)));
                                const tlToExchange = ((tl && tl.movedToExchange) || []).filter(u => String(u.time).startsWith(String(a.year))).map(u => ({ ...u, direction: 'exchange', counterparty: u.to }));
                                const titnRow = titnYear.length
                                    ? row('THORWallet (TITN) rewards', '', fmtUsd(sum(titnYear, r => r.usd)), `${titnYear.length} payments in USDC and TITN, not OPT (section 1a)${titnUnpriced.length ? `; ${titnUnpriced.length} unpriced, not included` : ''}. Ordinary income (Schedule 1, line 8v)`)
                                    : '';
                                $('tr-summary-body').innerHTML =
                                    '<tr><th style=""text-align: left;"">Item</th><th style=""text-align: right;"">OPT</th><th style=""text-align: right;"">USD</th><th style=""text-align: left;"">Where it goes</th></tr>' +
                                    row('Node rewards received', fmtOpt(sum(node, x => x.e.amountOpt)), fmtUsd(fmvOf(node)), `${node.length} payouts · quoted ${fmtUsd(quotedOf(node))}. Ordinary income (Schedule C if you run nodes as a business, otherwise Schedule 1)`) +
                                    row('Staking / participation rewards received', fmtOpt(sum(staking, x => x.e.amountOpt)), fmtUsd(fmvOf(staking)), `${staking.length} payouts · quoted ${fmtUsd(quotedOf(staking))}. Ordinary income (Schedule 1, line 8v)`) +
                                    (other.length ? row('Other OPT income (marked by you)', fmtOpt(sum(other, x => x.e.amountOpt)), fmtUsd(fmvOf(other)), `${other.length} receipts`) : '') +
                                    (a.rule === 'reviewed'
                                        ? row('Income at selected values', fmtOpt(sum(a.incomeYear.filter(x => x.fmv), x => x.e.amountOpt)), fmtUsd(fmvOf(a.incomeYear)), `Reference value of the same receipts ${fmtUsd(quotedOf(a.incomeYear.filter(x => x.fmv)))}; adjustment ${fmtUsd(fmvOf(a.incomeYear) - quotedOf(a.incomeYear.filter(x => x.fmv)))}`, true) +
                                          (a.incomeYear.some(x => !x.fmv) ? row('<span class=""tr-flag"">Unresolved receipts (no selected value)</span>', fmtOpt(sum(a.incomeYear.filter(x => !x.fmv), x => x.e.amountOpt)), '–', `${a.incomeYear.filter(x => !x.fmv).length} payouts. Reference estimate ${fmtUsd(quotedOf(a.incomeYear.filter(x => !x.fmv)))}, not included above; resolve them before filing`) : '')
                                        : row('Total income at fair market value', fmtOpt(sum(a.incomeYear, x => x.e.amountOpt)), fmtUsd(fmvOf(a.incomeYear)), `Quoted price would give ${fmtUsd(quotedOf(a.incomeYear))}; difference ${fmtUsd(quotedOf(a.incomeYear) - fmvOf(a.incomeYear))}`, true)) +
                                    titnRow +
                                    (() => {
                                        const ex = expenseRows(a), ded = sum(ex.filter(x => !x.cat.capital), x => x.usd), cap = sum(ex.filter(x => x.cat.capital), x => x.usd);
                                        if (!ex.length) return row('Node expenses', '', fmtUsd(0), 'None entered (section 5)');
                                        const dp = depreciation(a, ex, fmvOf(node) - ded);
                                        return row('Node expenses you can deduct', '', fmtUsd(ded), 'Schedule C if you run nodes as a business (section 5)') +
                                            (cap ? row('Node and equipment purchases', '', fmtUsd(cap), esc(DEPR_METHODS[dp.method]) + ' (section 5)') : '') +
                                            (dp.allowed179 > 0.004 ? row('Section 179 deduction', '', fmtUsd(dp.allowed179), `Form 4562 Part I${dp.carry ? `; ${fmtUsd(dp.carry)} carries forward (income limit)` : ''}`) : '') +
                                            (dp.bonus > 0.004 ? row('Bonus depreciation', '', fmtUsd(dp.bonus), 'Form 4562 line 14 (special depreciation allowance)') : '') +
                                            (dp.remaining > 0.004 ? row('Basis left to depreciate in later years', '', fmtUsd(dp.remaining), 'Form 4562; regular depreciation or amortization') : '') +
                                            row('Node income less expenses and first-year deductions', '', fmtUsd(fmvOf(node) - ded - dp.allowed179 - dp.bonus), 'Before regular depreciation and self-employment tax', true);
                                    })() +
                                    (a.gifts.length ? row('Gifts received (not income)', fmtOpt(sum(a.gifts, g => g.e.amountOpt)), fmtUsd(sum(a.gifts, g => g.dayValue)), `${a.gifts.length} gift(s), value on the day; giver basis ${fmtUsd(sum(a.gifts, g => g.basis))}${a.gifts.some(g => !g.basisKnown) ? ' (some unknown, $0)' : ''}`) : '') +
                                    row('Short-term sales (held 1 year or less)', fmtOpt(sum(st, s => s.qty)), fmtGain(salesGain(st)), `Proceeds ${fmtUsd(sum(st, s => s.proceeds))}, cost ${fmtUsd(sum(st, s => s.cost))}. Form 8949 Part I`) +
                                    row('Long-term sales (held over 1 year)', fmtOpt(sum(lt, s => s.qty)), fmtGain(salesGain(lt)), `Proceeds ${fmtUsd(sum(lt, s => s.proceeds))}, cost ${fmtUsd(sum(lt, s => s.cost))}. Form 8949 Part II`) +
                                    row('Net capital gain or (loss)', fmtOpt(sum(a.sales, s => s.qty)), fmtGain(salesGain(a.sales)), 'Schedule D. Net losses offset other income up to $3,000 a year; the rest carries forward', true) +
                                    (tlYear.length ? (() => {
                                        const st2 = tlYear.filter(d => d.term !== 'long'), lt2 = tlYear.filter(d => d.term === 'long');
                                        const g = (xs) => sum(xs, d => d.gain);
                                        return (st2.length ? row('TGT, TITN and ETH, short-term', '', fmtGain(g(st2)), `Proceeds ${fmtUsd(sum(st2, d => d.proceeds))}, cost ${fmtUsd(sum(st2, d => d.cost))}. Form 8949 Part I (section 4a)${tlNoCost.length ? `; ${tlNoCost.length} without a cost, not included` : ''}`) : '') +
                                            (lt2.length ? row('TGT, TITN and ETH, long-term', '', fmtGain(g(lt2)), `Proceeds ${fmtUsd(sum(lt2, d => d.proceeds))}, cost ${fmtUsd(sum(lt2, d => d.cost))}. Form 8949 Part II (section 4a)`) : '') +
                                            row('Net capital gain or (loss), OPT, TGT, TITN and ETH', '', fmtGain(salesGain(a.sales) + g(tlYear)), 'Schedule D total of sections 4 and 4a', true);
                                    })() : '') +
                                    row(`Held at the end of ${esc(a.year)}`, fmtOpt(a.held), fmtUsd(a.heldCost), a.hifoFrom ? 'Cost basis of the lots still in this wallet' : 'Cost basis of OPT still in this wallet (FIFO)');
                                // Automatic adjustments are flagged for review, not treated as an approved safe harbor.
                                const unreviewedIncome = a.rule === 'reviewed' ? a.incomeYear.filter(x => !x.fmv).length : 0;
                                const autoRules = [a.rule === 'dlom' ? 'marketability discount' : a.rule === 'next' ? 'next sellable day price' : a.rule === 'last' ? 'last sellable day price' : null, a.blockage > 0 ? 'blockage discount' : null].filter(Boolean);
                                if (autoRules.length || unreviewedIncome) $('tr-print-header').innerHTML = '<strong>DRAFT</strong> · ' + $('tr-print-header').innerHTML;
                                $('tr-review').innerHTML = (autoRules.length ? `<div class=""tr-flag"" style=""margin-bottom: 6px;"">Needs review: this report applies an automatic valuation adjustment (${esc(autoRules.join(' and '))}). It is a method choice, not an approved safe harbor or IRS formula. Review receipt values on the <a href=""/page/liquidity-review?address=${encodeURIComponent(dist.address)}&year=${esc(a.year)}"">Liquidity &amp; Distribution Review</a> page, then choose Receipt-date FMV above to use only approved values.</div>` : '') +
                                    (unreviewedIncome ? `<div class=""tr-flag"" style=""margin-bottom: 6px;"">Draft: ${unreviewedIncome} payout(s) have no selected value yet. They are listed (payout schedule, section 6) and shown as unresolved in the summary with their reference estimate; they are not counted as zero. Resolve them on the <a href=""/page/liquidity-review?address=${encodeURIComponent(dist.address)}&year=${esc(a.year)}"">Liquidity &amp; Distribution Review</a> page.</div>` : '') +
                                    (a.rule === 'reviewed' && a.sales.some(s => s.t.kind === 'node' && s.t.usd == null) ? `<div class=""tr-flag"" style=""margin-bottom: 6px;"">${a.sales.filter(s => s.t.kind === 'node' && s.t.usd == null).length} node purchase(s) paid in OPT have no amount realized. Enter the value of the licenses you received (section 3). The one-time promotional exchange is not valued from the disputed OPT price.</div>` : '') +
                                    (review ? `<span class=""tr-flag"">${review} transfer(s) are not classified yet</span> (${fmtOpt(sum(a.transfersOut.filter(x => !x.t.reviewed), x => x.e.amountOpt))} OPT sent, ${fmtOpt(sum(a.transfersIn.filter(x => !x.t.reviewed), x => x.e.amountOpt))} OPT received). They are not counted as sales or purchases; choose what each one was in section 3. <a href=""#tr-section-3"" id=""tr-show-todo"" class=""tr-no-print"">Show them</a> ` : '') +
                                    (a.ownWallets ? `<div style=""font-size: 12px; color: var(--text-secondary); margin-top: 4px;"">Cost basis and acquisition dates move with OPT between your ${a.ownWallets + 1} saved wallets.</div>` : '') +
                                    (noFmv ? `<span class=""tr-flag"">${noFmv} payout(s) have no market price at all</span> (before OPT first traded); they are left out of the totals. ` : '') +
                                    (ledger.truncated ? '<span class=""tr-flag"">The wallet history hit the 20,000-transaction cap; older transfers may be missing.</span>' : '');

                                // Other crypto income: THORWallet rewards (section 1a).
                                $('tr-titn').hidden = !titnYear.length;
                                if (titnYear.length) {
                                    const walletName = (w) => window.opticWallets.label(w) || (w.slice(0, 6) + '…' + w.slice(-4));
                                    const scanUrl = (r) => (r.chain === 'arbitrum' ? 'https://arbiscan.io/tx/' : 'https://basescan.org/tx/') + r.tx;
                                    const amt = (v, sym) => Number(v).toLocaleString('en-US', { maximumFractionDigits: sym === 'USDC' ? 2 : 0 }) + ' ' + sym;
                                    $('tr-titn-lede').innerHTML = `Rewards from THORWallet's contracts paid into your saved Base and Arbitrum wallets during ${esc(a.year)}. Each payment is ordinary income on the day it was received (Rev. Rul. 2023-14): USDC at $1.00 and TITN at that day's DefiLlama price. The TITN you staked coming back to you is not income and is not listed. TITN received here starts a holding with a cost basis equal to the income reported.`;
                                    const srcs = [...new Set(titnYear.map(r => r.source + '|' + r.symbol))];
                                    $('tr-titn-sum').innerHTML = srcs.map(k => {
                                        const [src, sym] = k.split('|'), xs = titnYear.filter(r => r.source === src && r.symbol === sym);
                                        return `<tr><td>${esc(src)}</td><td class=""amount-cell"">${xs.length}</td><td class=""amount-cell"">${amt(sum(xs, r => r.amount), sym)}</td><td class=""amount-cell"">${fmtUsd(sum(xs, r => r.usd))}${xs.some(r => r.usd == null) ? ' <span class=""tr-flag"">some unpriced</span>' : ''}</td></tr>`;
                                    }).join('') + `<tr class=""tr-total""><td>Total</td><td class=""amount-cell"">${titnYear.length}</td><td></td><td class=""amount-cell"">${fmtUsd(sum(titnYear, r => r.usd))}</td></tr>`;
                                    $('tr-titn-body').innerHTML = titnYear.map(r => `<tr><td>${esc(String(r.time).slice(0, 16).replace('T', ' '))}</td><td>${esc(walletName(r.wallet))}</td><td>${esc(r.source)}</td><td class=""amount-cell"">${Number(r.amount).toLocaleString('en-US', { maximumFractionDigits: r.symbol === 'USDC' ? 6 : 2 })} ${r.symbol}</td><td class=""amount-cell"">${r.priceUsd == null ? '–' : r.symbol === 'USDC' ? '$1.00' : '$' + Number(r.priceUsd).toFixed(5)}</td><td class=""amount-cell"">${r.usd == null ? '<span class=""tr-flag"">no price</span>' : fmtUsd(r.usd)}</td><td style=""font-family: monospace; font-size: 11px;""><a href=""${scanUrl(r)}"" target=""_blank"" rel=""noopener"">${esc(r.tx.slice(0, 14))}…</a></td></tr>`).join('');
                                }

                                // TGT and TITN (section 4a).
                                const tlHeld = (tl && tl.yearEnd && tl.yearEnd[String(a.year)]) || [];

                                // All crypto assets held at the end of the year (when All my crypto assets is chosen).
                                $('tr-portfolio').hidden = !allAssets;
                                if (allAssets) {
                                    const px = (tl && tl.price) || {};
                                    const fmtQ = (v, s) => Number(v).toLocaleString('en-US', { maximumFractionDigits: s === 'ETH' ? 6 : 2 });
                                    const optNow = (() => { try { const s = (market && (market.series || market.prices || market.daily)) || []; const last = Array.isArray(s) && s.length ? s[s.length - 1] : null; return last ? (last.close ?? last.price ?? last.usd ?? null) : null; } catch (e) { return null; } })();
                                    const rowsP = [`<tr><td>OPT</td><td>${esc(dist.address.slice(0, 12))}… and the wallets linked to it</td><td class=""amount-cell"">${fmtOpt(a.held)}</td><td class=""amount-cell"">${fmtUsd(a.heldCost)}</td><td class=""amount-cell"">${optNow ? fmtUsd(a.held * optNow) : '–'}</td></tr>`];
                                    for (const h of tlHeld) if (h.qty > (h.asset === 'ETH' ? 0.000001 : 0.01)) rowsP.push(`<tr><td>${esc(h.asset)}</td><td>Your Ethereum, Arbitrum and Base wallets${h.asset === 'TITN' ? ' (staked and held)' : ''}</td><td class=""amount-cell"">${fmtQ(h.qty, h.asset)}</td><td class=""amount-cell"">${fmtUsd(h.cost)}${h.unknownCostQty > (h.asset === 'ETH' ? 0.00001 : 0.01) ? ' <span class=""tr-flag"">part unknown</span>' : ''}</td><td class=""amount-cell"">${px[h.asset] ? fmtUsd(h.qty * px[h.asset]) : '–'}</td></tr>`);
                                    rowsP.push(`<tr><td>USDC</td><td>Base, Ethereum and Arbitrum</td><td class=""amount-cell"" colspan=""3"" style=""text-align: left; font-size: 12px; color: var(--text-secondary);"">A dollar stablecoin carried at $1.00, so it has no gain or loss of its own; current balances are on <a href=""/page/my-wallets"">My Wallets</a>.</td></tr>`);
                                    if (!tl) rowsP.push(`<tr><td colspan=""5"" style=""font-size: 12px; color: var(--text-secondary);"">Add your Ethereum, Arbitrum or Base wallets on <a href=""/page/my-wallets"">My Wallets</a> to include TITN, TGT, ETH and USDC.</td></tr>`);
                                    $('tr-portfolio-body').innerHTML = rowsP.join('');
                                }
                                $('tr-tlots').hidden = !(tlYear.length || tlUnclassified.length || tlHeld.length);
                                if (!$('tr-tlots').hidden) {
                                    const wName = (w) => window.opticWallets.label(w) || (w.slice(0, 6) + '…' + w.slice(-4));
                                    const txUrl = (chain, tx) => (chain === 'arbitrum' ? 'https://arbiscan.io/tx/' : chain === 'ethereum' ? 'https://etherscan.io/tx/' : 'https://basescan.org/tx/') + tx;
                                    const day = (v) => v ? esc(String(v).slice(0, 10)) : '–';
                                    $('tr-tlots-lede').innerHTML = `Disposals of TGT, TITN and ETH in your saved Ethereum, Arbitrum and Base wallets during ${esc(a.year)}, kept separate from OPT. Each wallet keeps its own lots and the highest-cost lot is used first. Cost is what you paid (stablecoins at $1.00, ETH at the day's price, THORChain and Maya swaps at their recorded USD value). ETH received from an exchange is costed at that day's price until the exchange's records are loaded. Every network fee is ETH spent at its value that day; in a trade of one digital asset for another, half of the fee is added to the cost of what was received and half reduces the proceeds of what was given up (Treas. Reg. §1.1012-1(h)). Fees for staking, bridging and moves between your own wallets are not added to cost. The TGT→TITN migration is an exchange of TGT at its market value on the day it was deposited; that value is the cost of the TITN the deposit entitled you to, and TITN received beyond that (for leaving TGT in the contract a full year) is additional proceeds of the exchange when received. Liquidity in the Aerodrome TITN/USDC pool is settled when the position closed: TITN that came back short was sold for the net USDC gained, extra TITN was bought for the net USDC paid. Staking, bridging between networks and moves between your own wallets are not sales.`;
                                    $('tr-tlots-flags').innerHTML = (tlNoCost.length ? `<div class=""tr-flag"" style=""margin-bottom: 4px;"">${tlNoCost.length} line(s) have no cost or proceeds yet and are left out of the totals.</div>` : '') +
                                        (tl.notes || []).map(n => `<div class=""tr-flag"" style=""margin-bottom: 4px;"">${esc(n)}</div>`).join('') +
                                        (tlUnclassified.length ? `<div class=""tr-flag"">${tlUnclassified.length} transfer(s) this year are not classified (listed below).</div>` : '');
                                    // Gas: hundreds of tiny ETH lines a year, shown as one line per wallet (totals are unchanged).
                                    const gasRows = {};
                                    const tlShown = [];
                                    for (const d of tlYear) {
                                        if (!/^ETH spent on the network fee/.test(d.how)) { tlShown.push(d); continue; }
                                        const g = gasRows[d.wallet] = gasRows[d.wallet] || { asset: 'ETH', wallet: d.wallet, chain: d.chain, qty: 0, proceeds: 0, cost: 0, gain: 0, n: 0, noCost: 0, term: 'short', acquired: null, sold: null, tx: '', lotHow: '', how: '' };
                                        g.qty += d.qty; g.proceeds += d.proceeds || 0; g.cost += d.cost || 0; g.gain += d.gain || 0; g.n++; if (d.cost == null) g.noCost++;
                                        if (d.term === 'long') g.term = 'long';
                                    }
                                    for (const g of Object.values(gasRows)) { g.how = `ETH spent on network fees (gas): ${g.n} transactions in ${a.year}, combined${g.noCost ? `; ${g.noCost} with no recorded cost` : ''}`; g.sold = String(a.year) + '-12-31'; tlShown.push(g); }
                                    $('tr-tlots-body').innerHTML = tlShown.map(d => `<tr><td>${day(d.acquired)}</td><td>${day(d.sold)}</td><td>${esc(d.asset)}</td><td class=""amount-cell"">${Number(d.qty).toLocaleString('en-US', { maximumFractionDigits: d.asset === 'ETH' ? 6 : 2 })}</td><td class=""amount-cell"">${d.proceeds == null ? '<span class=""tr-flag"">–</span>' : fmtUsd(d.proceeds)}</td><td class=""amount-cell"">${d.cost == null ? '<span class=""tr-flag"">unknown</span>' : fmtUsd(d.cost)}</td><td class=""amount-cell"">${d.gain == null ? '–' : fmtGain(d.gain)}</td><td>${d.term === 'long' ? 'Long' : d.term === 'short' ? 'Short' : '?'}</td><td style=""font-size: 12px;"">${esc(d.how)}${d.lotHow ? `<div style=""color: var(--text-muted);"">Lot: ${esc(d.lotHow)}</div>` : ''}<div style=""color: var(--text-muted);"">${esc(wName(d.wallet))}${d.tx ? ` · <a href=""${txUrl(d.chain, d.tx)}"" target=""_blank"" rel=""noopener"">${esc(d.tx.slice(0, 12))}…</a>` : ''}</div></td></tr>`).join('')
                                        || '<tr><td colspan=""9"" style=""text-align: center; color: var(--text-secondary);"">No TGT, TITN or ETH sold or exchanged this year.</td></tr>';
                                    $('tr-tlots-held').innerHTML = tlHeld.filter(h => h.qty > (h.asset === 'ETH' ? 0.000001 : 0.01)).map(h => `<tr><td>${esc(h.asset)}</td><td class=""amount-cell"">${Number(h.qty).toLocaleString('en-US', { maximumFractionDigits: h.asset === 'ETH' ? 6 : 2 })}</td><td class=""amount-cell"">${fmtUsd(h.cost)}</td><td style=""font-size: 12px;"">${h.unknownCostQty > (h.asset === 'ETH' ? 0.00001 : 0.01) ? `${Number(h.unknownCostQty).toLocaleString('en-US', { maximumFractionDigits: 2 })} ${esc(h.asset)} have no known cost (not classified receipts or a purchase without a cost)` : ''}</td></tr>`).join('')
                                        || '<tr><td colspan=""4"" style=""text-align: center; color: var(--text-secondary);"">None.</td></tr>';
                                    $('tr-tlots-unclassified').innerHTML = [...tlUnclassified, ...tlToExchange].map(u => `<tr><td>${esc(String(u.time).slice(0, 16).replace('T', ' '))}</td><td>${esc(wName(u.wallet))}</td><td>${u.direction === 'in' ? 'Received' : u.direction === 'exchange' ? 'Moved to your exchange account (not a sale)' : 'Sent'}</td><td class=""amount-cell"">${Number(u.qty).toLocaleString('en-US', { maximumFractionDigits: 2 })} ${esc(u.asset)}</td><td class=""amount-cell"">${u.valueThatDay == null ? '–' : fmtUsd(u.valueThatDay)}</td><td style=""font-family: monospace; font-size: 11px;""><a href=""${txUrl(u.chain, u.tx)}"" target=""_blank"" rel=""noopener"">${esc(u.counterparty.slice(0, 10))}…</a></td></tr>`).join('')
                                        || '<tr><td colspan=""6"" style=""text-align: center; color: var(--text-secondary);"">None.</td></tr>';
                                }

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
                                // Osmosis liquidity and the indicated blockage discount (section 2).
                                const L = a.liq, liquidityNow = (market.snapshot?.pools || []).reduce((s, p) => s + (p.liquidityUsd || 0), 0);
                                $('tr-os-lede').textContent = `Osmosis is a decentralized exchange on its own blockchain. OPT reaches it by IBC transfer from Optio and can be swapped for USDC or OSMO in the OPT pools at any hour, with no account or deposit to approve. These figures count every OPT swap on the Osmosis chain in ${a.year}, net per transaction so arbitrage that buys and sells in one transaction cancels out.`;
                                const osF = [];
                                osF.push(L.swaps ? `<strong>${L.swaps.toLocaleString('en-US')} swaps on ${L.active} of ${L.days} days</strong>: ${fmtOpt(L.sold)} OPT sold into the pools and ${fmtOpt(L.bought)} OPT bought from them. On a typical day with sales, ${fmtOpt(L.median)} OPT was sold (median); the biggest day was ${L.big ? fmtDate(L.big.d) + ', ' + fmtOpt(L.big.osSold) + ' OPT' : '-'}.` : `<strong>No OPT swaps on Osmosis in ${esc(a.year)}.</strong>`);
                                if (L.vwap != null) osF.push(`<strong>Average swap price ${fmtPrice(L.vwap)}</strong> (weighted by volume)${L.p0 && L.p1 && L.p0 !== L.p1 ? `; the daily price went from ${fmtPrice(L.p0.price)} on ${fmtDate(L.p0.d)} to ${fmtPrice(L.p1.price)} on ${fmtDate(L.p1.d)}` : ''}.`);
                                if (L.bought > L.sold && L.p0 && L.p1 && L.p1.price < L.p0.price) osF.push(`<strong>Buyers took ${fmtOpt(L.bought - L.sold)} more OPT out of the pools than swaps put in, yet the price fell.</strong> In a pool that can only happen if the extra OPT was supplied as liquidity, which acts as standing sell orders. So liquidity providers, not independent sellers, supplied what buyers took, and that depth left with them; it is not evidence that a holder could sell large amounts at the quoted price.`);
                                if (L.received > 0 && L.sold > 0) osF.push(L.received >= L.sold ? `<strong>You received ${fmtOpt(L.received)} OPT, ${(L.received / L.sold).toFixed(1)}× all the OPT anyone sold on Osmosis in ${esc(a.year)}.</strong>` : `<strong>You received ${fmtOpt(L.received)} OPT, equal to ${fmtPct(L.received / L.sold)} of all the OPT anyone sold on Osmosis in ${esc(a.year)}</strong>, about ${L.median > 0 ? Math.round(L.received / L.median).toLocaleString('en-US') : '-'} typical days of everyone&#39;s sales.`);
                                if (liquidityNow) osF.push(`<strong>All OPT pools together hold about ${fmtUsd(liquidityNow)} today</strong> (when this report was prepared).`);
                                $('tr-os-findings').innerHTML = osF.map(f => `<li>${f}</li>`).join('');
                                $('tr-os-body').innerHTML = L.months.filter(m => m.swaps || m.you).map(m => `<tr><td>${fmtMonth(m.m)}</td><td class=""amount-cell"">${m.swaps.toLocaleString('en-US')}</td><td class=""amount-cell"">${m.active} of ${m.days}</td><td class=""amount-cell"">${fmtOpt(m.sold)}</td><td class=""amount-cell"">${fmtOpt(m.bought)}</td><td class=""amount-cell"">${fmtPrice(m.vwap)}</td><td class=""amount-cell"">${fmtOpt(m.you)}</td></tr>`).join('')
                                    || '<tr><td colspan=""7"" style=""text-align: center; color: var(--text-secondary);"">No Osmosis swaps this year</td></tr>';
                                $('tr-disc').innerHTML = !L.received ? `<p>No payouts in ${esc(a.year)}.</p>` : !L.excessOpt
                                    ? `<p><strong>Indicated blockage discount: 0%.</strong> On every payout day the market could have absorbed what you received, so no blockage discount is indicated for ${esc(a.year)}.</p>`
                                    : `<p><strong>Indicated blockage discount: ${L.indicated}%</strong>, applied only to the ${fmtOpt(L.excessOpt)} OPT (${fmtPct(L.excessOpt / L.received)} of the ${fmtOpt(L.received)} OPT you received in ${esc(a.year)}) that was more than the market could absorb on the day it arrived.</p>
                                    <p><strong>How it is measured.</strong> Each day the market could take about 10% of the trailing 30-day average volume (OPT withdrawn from Coinstore plus Osmosis swaps; the same measure as the monthly table above). Your OPT that day is sold first; the rest waits in line, oldest first, for spare capacity on later days. Each part is discounted by the Finnerty (2012) average-strike put value for the time it had to wait, at OPT&#39;s trailing 90-day volatility on the day it arrived, the same model this report uses for days with no market. The longest wait was ${L.waitMax.toLocaleString('en-US')} days. The ${L.indicated}% is the value-weighted average of those discounts.${L.unsold > EPS ? ` ${fmtOpt(L.unsold)} OPT would still be waiting when the data ends; its discount uses the wait so far, so the figure is a floor.` : ''}</p>
                                    <p><strong>Effect.</strong> At the day&#39;s quoted price that OPT was worth ${fmtUsd(L.excessVal)}; the indicated discount lowers it by ${fmtUsd(L.discUsd)}, ${fmtPct(L.receivedVal ? L.discUsd / L.receivedVal : null)} of the quoted value of everything you received in ${esc(a.year)}.</p>
                                    <p><strong>Basis.</strong> A blockage discount reflects that a block too large for the market to absorb at once cannot be sold at the quoted price without depressing it (Treas. Reg. §20.2031-2(e)); for income tax it is an application of the willing-buyer, willing-seller standard, not a stated rate. The figure is an indication from the method above, not an IRS-prescribed discount.</p>
                                    <p class=""tr-no-print"">This report currently uses a blockage discount of ${fmtPct(a.blockage)}. ${Math.round(a.blockage * 100) !== L.indicated ? `<button type=""button"" id=""tr-use-disc"" data-pct=""${L.indicated}"">Use ${L.indicated}% as the blockage discount</button>` : ''}</p>`;
                                const useDisc = $('tr-use-disc');
                                if (useDisc) useDisc.addEventListener('click', () => { blockageInput.value = useDisc.dataset.pct; rerender(); });
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
                                const inRows = a.transfersIn.map(x => ({ ...x, placeholder: x.carriedFound ? (x.carried == null ? 'carried (unknown)' : 'carried ' + x.carried.toFixed(2)) : x.t.kind === 'gift-in' ? 'giver basis' : x.t.kind === 'unclassified' ? 'unknown' : x.e.priceUsd != null ? (x.e.amountOpt * x.e.priceUsd).toFixed(2) : '' }));
                                $('tr-in-body').innerHTML = inRows.map(x => `<tr class=""${x.t.reviewed ? '' : 'tr-unconfirmed'}""><td>${fmtTime(x.e.timeUtc)}</td><td>${partyName(x.e.counterparty)}${x.e.memo ? `<div style=""font-size: 11px; color: var(--text-secondary);"" title=""Memo"">${esc(x.e.memo)}</div>` : ''}${x.autoRepaid > 0 ? `<div style=""font-size: 11px; color: var(--text-secondary);"">${fmtOpt(x.autoRepaid)} OPT counted as loan repayment</div>` : ''}</td><td class=""amount-cell"">${fmtOpt(x.e.amountOpt)}</td>${editCells(x, IN_KINDS)}${txCell(x.e.txHash)}</tr>`).join('')
                                    || '<tr class=""tr-empty""><td colspan=""8"" style=""text-align: center; color: var(--text-secondary);"">None in ' + esc(a.year) + '</td></tr>';

                                // Sales (Form 8949).
                                const lmNow = a.lotMethod;
                                $('tr-lot-method').value = lmNow?.method === 'fifo' ? 'fifo' : 'hifo';
                                $('tr-lot-from').value = a.hifoFrom || '';
                                const lotText = lmNow?.method === 'hifo' && a.hifoFrom
                                    ? `Lots are tracked wallet by wallet (Rev. Proc. 2024-28). Sales on or after ${fmtDate(a.hifoFrom)} use the highest-cost lots first under a standing order recorded ${esc(String(lmNow.recordedUtc || '').slice(0, 16).replace('T', ' '))} UTC (specific identification, Treas. Reg. §1.1012-1(j)); the lots each of those sales used are recorded with it and are not used again. Earlier sales with no identification use first-in, first-out.`
                                    : 'Cost basis is matched first-in, first-out within this wallet (from 2025 the IRS requires basis to be tracked wallet by wallet, Rev. Proc. 2024-28).';                                $('tr-sales-lede').innerHTML = `${lotText} Each reward\x27s basis is the fair market value reported as income above. Proceeds you didn\x27t enter are the OPT\x27s fair market value that day and are marked *; enter the real figure from your records. ${fmtUsd(sum(a.sales, s => s.proceeds))} proceeds, ${fmtUsd(sum(a.sales, s => s.cost))} cost basis, net ${fmtGain(salesGain(a.sales))}.`;
                                $('tr-sales-body').innerHTML = a.sales.map(s => `<tr><td>${fmtOpt(s.qty)} OPT</td><td title=""${s.acqFrom ? esc(s.acqFrom + ' to ' + s.acqTo) : ''}"">${esc(s.acquired)}</td><td>${esc(s.soldOn)}</td>
                                    <td class=""amount-cell"">${fmtUsd(s.proceeds)}${s.proceedsEstimated ? '*' : ''}</td><td class=""amount-cell"">${fmtUsd(s.cost)}${s.unknownBasis ? '<span class=""tr-flag"" title=""Part of this OPT has no known cost (no purchase or reward found for it); counted as $0""> †</span>' : ''}</td>
                                    <td class=""amount-cell${s.gain < -0.004 ? ' tr-loss' : ''}"">${fmtGain(s.gain)}</td><td>${s.long ? 'Long' : 'Short'}</td><td>${esc(OUT_KINDS[s.t.kind].label)}${s.fromGift ? ' <span style=""color: var(--text-secondary);"">(gifted OPT)</span>' : ''}${s.identified ? ' <span class=""tr-badge ok"" title=""Lots identified before the sale (specific identification)"">Identified lots</span>' : ''}${s.identMissing > EPS ? `<div class=""tr-flag"" style=""font-size: 11px;"">${fmtOpt(s.identMissing)} OPT of the identified lots were not held at the time of this sale; FIFO used</div>` : ''}</td><td style=""font-size: 12px; white-space: pre-wrap; max-width: 260px;"">${esc(s.t.note)}</td></tr>`).join('')
                                    + (a.sales.length ? `<tr class=""tr-total""><td>${fmtOpt(sum(a.sales, s => s.qty))} OPT</td><td></td><td></td><td class=""amount-cell"">${fmtUsd(sum(a.sales, s => s.proceeds))}</td><td class=""amount-cell"">${fmtUsd(sum(a.sales, s => s.cost))}</td><td class=""amount-cell"">${fmtGain(salesGain(a.sales))}</td><td colspan=""3""></td></tr>` : '<tr><td colspan=""9"" style=""text-align: center; color: var(--text-secondary);"">No sales in ' + esc(a.year) + '</td></tr>');
                                const nodes = a.sales.filter(s => s.t.kind === 'node');
                                $('tr-nodes').hidden = !nodes.length;
                                $('tr-nodes-body').innerHTML = nodes.map(s => `<tr><td>${fmtTime(s.e.timeUtc)}</td><td>${partyName(s.e.counterparty)}</td><td class=""amount-cell"">${fmtOpt(s.qty)}</td><td class=""amount-cell"">${fmtUsd(s.proceeds)}</td><td class=""amount-cell"">${fmtUsd(s.cost)}</td><td style=""font-size: 12px; white-space: pre-wrap;"">${esc(s.t.note)}</td></tr>`).join('');
                                $('tr-gifts').hidden = !a.gifts.length;
                                $('tr-gifts-body').innerHTML = a.gifts.map(g => `<tr><td>${fmtTime(g.e.timeUtc)}</td><td>${partyName(g.e.counterparty)}</td><td class=""amount-cell"">${fmtOpt(g.e.amountOpt)}</td><td class=""amount-cell"">${fmtUsd(g.dayValue)}</td><td class=""amount-cell"">${g.basisKnown ? fmtUsd(g.basis) : '<span class=""tr-flag"">Unknown ($0)</span>'}</td><td>${esc(g.t.date || 'Not entered')}</td><td style=""font-size: 12px; white-space: pre-wrap;"">${esc(g.t.note)}</td></tr>`).join('');
                                const ex = expenseRows(a);
                                $('tr-exp-body').innerHTML = ex.map(x => `<tr><td>${esc(x.date)}</td><td>${esc(x.cat.label)}</td><td>${esc(x.description)}${x.auto ? ' <span style=""color: var(--text-secondary); font-size: 11px;"">(automatic)</span>' : ''}</td><td>${x.cat.capital ? esc(DEPR_METHODS[deprSettings().method] ? (deprSettings().method === 'regular' ? 'Depreciate / amortize' : deprSettings().method === 'bonus' ? 'Bonus depreciation' : deprSettings().method === '179' ? 'Section 179' : 'Section 179 + bonus') : 'Section 179 + bonus') : 'Deduct'}</td><td class=""amount-cell"">${fmtUsd(x.usd)}</td><td style=""font-size: 12px; white-space: pre-wrap;"">${esc(x.note)}</td><td class=""tr-no-print"" style=""white-space: nowrap;"">${x.mine ? `<button type=""button"" data-edit-exp=""${esc(x.id)}"" style=""font-size: 11px; padding: 2px 8px;"">Edit</button> <button type=""button"" data-remove-exp=""${esc(x.id)}"" style=""font-size: 11px; padding: 2px 8px;"">Remove</button>` : ''}</td></tr>`).join('')
                                    + (ex.length ? `<tr class=""tr-total""><td colspan=""4"">Deduct: ${fmtUsd(sum(ex.filter(x => !x.cat.capital), x => x.usd))} · Depreciate: ${fmtUsd(sum(ex.filter(x => x.cat.capital), x => x.usd))}</td><td class=""amount-cell"">${fmtUsd(sum(ex, x => x.usd))}</td><td colspan=""2""></td></tr>` : `<tr><td colspan=""7"" style=""text-align: center; color: var(--text-secondary);"">No node expenses for ${esc(a.year)} yet</td></tr>`);
                                // First-year deduction for node and equipment purchases.
                                const dpNode = byKind('node'), dpDed = sum(ex.filter(x => !x.cat.capital), x => x.usd);
                                const dp = depreciation(a, ex, fmvOf(dpNode) - dpDed);
                                $('tr-dep-179').checked = dp.method === '179-bonus' || dp.method === '179';
                                $('tr-dep-bonus').checked = dp.method === '179-bonus' || dp.method === 'bonus';
                                $('tr-dep-limit').value = String(dp.limitIn);
                                $('tr-dep-other').value = dp.other ? String(dp.other) : '';
                                $('tr-dep-lede').innerHTML = `<strong>Method: ${esc(DEPR_METHODS[dp.method])}.</strong> Node licenses and equipment are capital purchases. ${dp.method === 'regular' ? 'Their cost is recovered over later years only.' : 'Their cost can be deducted in the year they are placed in service: Section 179 (IRC §179) is an election limited to the year&#39;s dollar limit and to taxable income from active trades or businesses; bonus depreciation (IRC §168(k)) has no income limit.'}`;
                                $('tr-dep-body').innerHTML = dp.items.map(it => `<tr><td>${esc(it.date)}</td><td>${esc(it.label)}<div style=""font-size: 11px; color: var(--text-secondary);"">${esc(it.description || '')}</div></td><td class=""amount-cell"">${fmtUsd(it.cost)}</td><td class=""amount-cell"">${fmtUsd(it.s179)}</td><td class=""amount-cell"">${dp.method === '179-bonus' || dp.method === 'bonus' ? fmtPct(it.rate) : '-'}</td><td class=""amount-cell"">${fmtUsd(it.bonus)}</td><td class=""amount-cell"">${fmtUsd(it.s179 + it.bonus)}</td><td class=""amount-cell"">${fmtUsd(it.remaining)}</td></tr>`).join('')
                                    + (dp.items.length ? `<tr class=""tr-total""><td colspan=""2"">${esc(a.year)}</td><td class=""amount-cell"">${fmtUsd(dp.cost)}</td><td class=""amount-cell"">${fmtUsd(dp.elect)}</td><td></td><td class=""amount-cell"">${fmtUsd(dp.bonus)}</td><td class=""amount-cell"">${fmtUsd(dp.allowed179 + dp.bonus)}</td><td class=""amount-cell"">${fmtUsd(dp.remaining)}</td></tr>` : `<tr><td colspan=""8"" style=""text-align: center; color: var(--text-secondary);"">No node or equipment purchases in ${esc(a.year)}</td></tr>`);
                                const dn = [];
                                if (dp.items.length && dp.method !== 'regular' && dp.method !== 'bonus') dn.push(`<strong>Section 179 limit:</strong> ${fmtUsd(dp.limitIn)} for ${esc(a.year)}${dp.limit < dp.limitIn ? `, reduced to ${fmtUsd(dp.limit)} because purchases exceeded ${fmtUsd(dp.phaseOut)}` : ''}. <strong>Income limit:</strong> ${fmtUsd(dp.incomeLimit)} (node income less node expenses${dp.other ? ` plus ${fmtUsd(dp.other)} of wages and other business income` : ''}).${dp.carry ? ` ${fmtUsd(dp.carry)} of the election is above the income limit and carries forward to next year (IRC §179(b)(3)).` : ''}${dp.method === '179-bonus' && dp.elect < dp.cost ? ` Section 179 is elected only up to the income limit; bonus depreciation covers the rest, so nothing is lost to the limit.` : ''}`);
                                if (dp.items.some(it => it.rate < 1) && (dp.method === '179-bonus' || dp.method === 'bonus')) dn.push('<strong>Bonus rate</strong> follows the acquisition date: 100% for property acquired after January 19, 2025 (P.L. 119-21); 40% for January 1–19, 2025; 60% in 2024; 80% in 2023.');
                                if (dp.remaining > 0.004) dn.push(`<strong>${fmtUsd(dp.remaining)} of basis is left</strong> to recover in later years by regular depreciation or amortization, on Form 4562.`);
                                if (dp.items.length && dp.method !== 'regular') dn.push('Section 179 is elected on Form 4562 Part I and bonus depreciation is reported on line 14; electing out of bonus depreciation is made by class of property on the return. The deductions reduce node income for income tax and self-employment tax when nodes are run as a business (Schedule C).');
                                if (a.rule === 'reviewed' && a.sales.some(s => s.t.kind === 'node' && s.proceeds == null)) dn.push('<span class=""tr-flag"">Node purchases paid in OPT with no amount entered are not included; enter the value of the licenses received in section 3.</span>');
                                $('tr-dep-notes').innerHTML = dn.map(x => `<li>${x}</li>`).join('');                                if (!$('tr-exp-date').value || !$('tr-exp-date').value.startsWith(a.year)) $('tr-exp-date').value = a.year + '-12-31';
                                if (!$('tr-host-from').value || !$('tr-host-from').value.startsWith(a.year)) { $('tr-host-from').value = a.year + '-01'; $('tr-host-to').value = a.year + '-12'; showHostingPreview(); }
                                const moves = a.carries.concat(a.unclassifiedOut, a.skipped.map(x => ({ ...x, cost: null, valueWhenSent: null }))).sort((x, y) => String(x.e.timeUtc).localeCompare(String(y.e.timeUtc)));
                                $('tr-loans').hidden = !a.loans.length;
                                $('tr-loans-body').innerHTML = a.loans.map(l => `<tr><td>${partyName(l.cp)}</td><td class=""amount-cell"">${fmtOpt(l.lent)}</td><td class=""amount-cell"">${fmtOpt(l.repaid)}</td><td class=""amount-cell"">${l.auto > EPS ? fmtOpt(l.auto) : '-'}</td><td class=""amount-cell"">${fmtOpt(l.owed)}</td></tr>`).join('');
                                $('tr-moves').hidden = !moves.length;
                                $('tr-moves-body').innerHTML = moves.map(m => `<tr><td>${fmtTime(m.e.timeUtc)}</td><td>${partyName(m.e.counterparty)}</td><td class=""amount-cell"">${fmtOpt(m.e.amountOpt)}</td><td>${esc(OUT_KINDS[m.t.kind].label)}</td><td class=""amount-cell"">${fmtUsd(m.cost)}</td><td class=""amount-cell"">${fmtUsd(m.valueWhenSent)}</td><td style=""font-size: 12px; white-space: pre-wrap;"">${esc(m.t.note)}</td></tr>`).join('');

                                // Payout schedule.
                                $('tr-payouts-body').innerHTML = shown.slice().reverse().map(x => {
                                    const acc = x.fmv?.a;
                                    const market = !acc ? (a.rule === 'reviewed' ? '<span class=""tr-flag"">Unresolved</span>' : 'No market yet') : acc.source.startsWith('Coinstore') ? 'Coinstore open' : acc.source.startsWith('Osmosis') ? 'Osmosis only' : 'None you could reach';
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
                                    '<strong>Node expenses:</strong> deductible against node income only if running nodes is a trade or business (IRC §162, Schedule C); then node income is also subject to self-employment tax. Node purchases and equipment are capital costs. They can be deducted in the year placed in service by electing IRC §179 expensing (limited to the year&#39;s dollar limit and to active business income, with any excess carried forward) and by bonus depreciation under IRC §168(k) (100% for property acquired after January 19, 2025, under P.L. 119-21); any basis left is recovered by regular depreciation or amortization. All on Form 4562; see section 5 for the method used. Expenses paid in OPT are also disposals of that OPT, with their gain or loss in section 4.',
                                    '<strong>Worthless or stuck OPT:</strong> the IRS has said a token still trading somewhere is not worthless and that abandonment needs an affirmative act (Chief Counsel Advice 202302011). OPT still trades on Osmosis, so selling it there, even for very little, is the clearer way to realize a loss than claiming it is worthless.',
                                    `<strong>Data:</strong> payouts and transfers are read from the Optio chain (${ledger.entries.length.toLocaleString('en-US')} transfers for this wallet; node distributions from ${esc(dist.source)}, chain staking rewards from the distribution module). Quoted prices: ${esc(dist.priceSource)}. Coinstore deposits to ${esc(flows.depositCollector)} and withdrawals from ${esc(flows.hotWallet)}; Coinstore candles ${fmtDate(market.sources.coinstoreFrom)} to ${fmtDate(market.sources.coinstoreTo)}; Osmosis swaps from the Osmosis archive node. All dates are UTC.`,
                                ].map(t => `<li>${t}</li>`).join('');
                                $('tr-evidence-files').innerHTML = ev && ev.files
                                    ? `Raw source records were captured ${esc(String(ev.capturedUtc).replace('T', ' ').slice(0, 19))} UTC (${ev.files.length} files, each with a SHA-256 fingerprint): <a href=""/api/market/evidence/${encodeURIComponent(ev.id)}.zip"">download the evidence zip</a>.`
                                    : 'No raw records captured yet; capture them on the Liquidity Evidence page.';

                                data.view = a;
                                report.hidden = false;
                                if (a.hifoRecord.length) setTimeout(() => recordHifo(a.hifoRecord), 0);
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
                                const item = { id: editingExp || Date.now().toString(36) + Math.random().toString(36).slice(2, 6), date, category: $('tr-exp-cat').value, description: $('tr-exp-desc').value.trim(), usd, note: $('tr-exp-note').value.trim() };
                                const list = labels._expenses || [];
                                const wasEditing = !!editingExp;
                                try {
                                    await saveExpenses(wasEditing ? list.map(x => x.id === editingExp ? item : x) : list.concat([item]));
                                    stopEditingExp();
                                    status.textContent = (wasEditing ? 'Expense updated.' : 'Expense saved.') + (date.startsWith(yearSelect.value) ? '' : ` It is under ${date.slice(0, 4)}; switch the tax year to see it.`);
                                } catch (error) { status.textContent = 'Could not save: ' + error.message; }
                            });
                            // Editing an expense you entered: the form above the table is filled in and saves over the same entry.
                            let editingExp = null;
                            const stopEditingExp = () => {
                                editingExp = null;
                                for (const id of ['tr-exp-desc', 'tr-exp-usd', 'tr-exp-note']) $(id).value = '';
                                $('tr-exp-add').textContent = 'Add expense';
                                $('tr-exp-cancel').hidden = true;
                            };
                            $('tr-exp-cancel').addEventListener('click', () => { stopEditingExp(); status.textContent = 'Edit cancelled.'; });
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
                                const edit = ev.target.closest('[data-edit-exp]');
                                if (edit && data) {
                                    const x = (labels._expenses || []).find(e => e.id === edit.dataset.editExp);
                                    if (!x) return;
                                    editingExp = x.id;
                                    $('tr-exp-date').value = x.date || '';
                                    $('tr-exp-cat').value = EXP_CATS[x.category] ? x.category : 'other';
                                    $('tr-exp-desc').value = x.description || '';
                                    $('tr-exp-usd').value = x.usd ?? '';
                                    $('tr-exp-note').value = x.note || '';
                                    $('tr-exp-add').textContent = 'Save changes';
                                    $('tr-exp-cancel').hidden = false;
                                    $('tr-exp-form').scrollIntoView({ behavior: 'smooth', block: 'center' });
                                    status.textContent = 'Editing the expense; change it above and press Save changes.';
                                    return;
                                }
                                const btn = ev.target.closest('[data-remove-exp]');
                                if (!btn || !data) return;
                                if (btn.dataset.removeExp === editingExp) stopEditingExp();
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
                                if (!window.opticWallets.validAny(address)) { status.textContent = 'Enter your optio1... wallet address (or save it as primary on My Wallets).'; return; }
                                updateUrl();
                                const done = window.opticBusy(submit, status, 'Reading the chain and market history');
                                status.textContent = 'Reading your rewards and transfers, Coinstore deposits and withdrawals, and market history...';
                                // One wallet's history: an Optio wallet from the Optio chain, an Osmosis account from its Osmosis history
                                // (swaps, liquidity positions and IBC transfers, already labelled by the server).
                                const optioWallets = () => window.opticWallets.listAll().map(w => w.a).filter(a => window.opticWallets.valid(a));
                                const loadWallet = async (w) => {
                                    if (window.opticWallets.validOsmo(w)) {
                                        const [o, s] = await Promise.all([
                                            getJson('/api/osmosis/ledger?address=' + encodeURIComponent(w) + '&peers=' + encodeURIComponent(optioWallets().join(','))),
                                            getJson('/api/tax/labels?address=' + encodeURIComponent(w)),
                                        ]);
                                        return { address: w, dist: { address: w, rows: [], source: 'none (Osmosis account)', priceSource: 'CoinMarketCap (OPT/USD)' }, ledger: { entries: o.entries || [], truncated: false }, labels: { ...(o.labels || {}), ...(s || {}) }, osmoLinks: o.links || [], osmoNotes: o.notes || [] };
                                    }
                                    const [d, l, s] = await Promise.all([
                                        getJson('/api/distributions/address?address=' + encodeURIComponent(w)),
                                        getJson('/api/wallet/ledger?address=' + encodeURIComponent(w)),
                                        getJson('/api/tax/labels?address=' + encodeURIComponent(w)),
                                    ]);
                                    return { address: w, dist: d, ledger: l, labels: s || {} };
                                };
                                try {
                                    const [{ market, flows }, selected, , evidence] = await Promise.all([
                                        window.opticMarket.load(),
                                        loadWallet(address),
                                        getJson('/api/tax/gift-senders').then(g => { giftSenders = g || {}; renderGifts(); }),
                                        fetch('/api/market/evidence/latest').then(r => r.json()).catch(() => null),
                                    ]);
                                    const { dist, ledger } = selected;
                                    labels = selected.labels || {};
                                    // Your other saved wallets, so OPT moved between them keeps its basis and acquisition dates.
                                    status.textContent = 'Reading your other saved wallets...';
                                    const others = await Promise.all(window.opticWallets.listAll().map(w => w.a).filter(w => w !== address).map(loadWallet));
                                    // IBC transfers between an Optio wallet and your Osmosis account: the Optio side shows the IBC escrow
                                    // account as the other party; point it at the Osmosis account so the move pairs up and carries its lots.
                                    const allW = [{ address, ledger }, ...others];
                                    for (const o of [selected, ...others].filter(x => x.osmoLinks))
                                        for (const lk of o.osmoLinks) {
                                            const wd = allW.find(x => x.address === lk.wallet);
                                            if (!wd) continue;
                                            for (const e of wd.ledger.entries) if (e.txHash === lk.optioTxHash && e.direction === lk.direction && !allW.some(x => x.address === e.counterparty)) e.counterparty = o.address;
                                        }
                                    data = { market, flows, dist, ledger, evidence, others, titn: null, titnLots: null, osmoNotes: [selected, ...others].flatMap(x => x.osmoNotes || []) };
                                    await loadReviewed();
                                    // Default lot method: highest cost first, recorded once as a standing order (server-stamped).
                                    if (!lotMethod()) { try { await setLotMethod({ method: 'hifo' }); } catch (error) { status.textContent = 'Could not record the standing order: ' + error.message; } }
                                    render();
                                    status.textContent = '';
                                    renderWallets();
                                    loadOtherAssets();
                                } catch (error) {
                                    status.textContent = 'Error: ' + error.message;
                                } finally {
                                    done();
                                }
                            };
                            // TITN rewards and TGT / TITN / ETH lots from your saved 0x... wallets: loaded after the OPT report is shown
                            // (the first load reads every network and can take minutes), and only when all crypto assets are shown.
                            let otherAssetsLoading = false;
                            const loadOtherAssets = () => {
                                if (!data || otherAssetsLoading || (data.titn && data.titnLots) || $('tr-assets').value === 'opt') return;
                                if (!window.opticWallets.listSaved().some(w => window.opticWallets.validEvm(w.a))) return;
                                otherAssetsLoading = true;
                                const note = $('tr-other-assets-status');
                                note.hidden = false;
                                note.textContent = 'Reading your Ethereum, Arbitrum and Base wallets for TITN, TGT and ETH. The first time can take several minutes; the OPT figures above are complete.';
                                const forData = data;
                                Promise.all([
                                    fetch('/api/titn/summary').then(r => r.ok ? r.json() : null).catch(() => null),
                                    fetch('/api/titn/lots').then(r => r.ok ? r.json() : null).catch(() => null),
                                ]).then(([titn, titnLots]) => {
                                    otherAssetsLoading = false;
                                    if (data !== forData) return;
                                    data.titn = titn; data.titnLots = titnLots;
                                    note.textContent = titn && titnLots ? '' : 'Could not read your Ethereum, Arbitrum or Base wallets this time; TITN, TGT and ETH are not included. Reload to try again.';
                                    note.hidden = !note.textContent;
                                    render();
                                });
                            };
                            // Approved values from the Liquidity & Distribution Review, fetched only when that rule is chosen.
                            const loadReviewed = async () => {
                                if (ruleSelect.value !== 'reviewed' || !data || data.reviewed) return;
                                status.textContent = 'Reading approved values...';
                                data.reviewed = await getJson('/api/valuation/selected?address=' + encodeURIComponent(data.dist.address));
                                data.reviewedOthers = Object.fromEntries(await Promise.all((data.others || []).map(async o => [o.address, await getJson('/api/valuation/selected?address=' + encodeURIComponent(o.address))])));
                                status.textContent = '';
                            };
                            const rerender = async () => {
                                updateUrl();
                                if (!data || data.dist.address !== addressInput.value.trim()) return;
                                try { await loadReviewed(); } catch (error) { status.textContent = 'Error: ' + error.message; return; }
                                render();
                            };

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

                            const renderWallets = () => window.opticWallets.renderSwitcher($('tr-wallets'), addressInput.value.trim() || null, (address) => { addressInput.value = address; run(); }, true);
                            renderWallets();
                            form.addEventListener('submit', (e) => { e.preventDefault(); run(); });
                            for (const el of [yearSelect, typeSelect, ruleSelect]) el.addEventListener('change', rerender);
                            // Assets shown: all crypto assets (default) or OPT only; remembered in this browser.
                            try { const saved = localStorage.getItem('optic_tr_assets'); if (saved === 'opt') $('tr-assets').value = 'opt'; } catch (e) { }
                            $('tr-assets').addEventListener('change', () => { try { localStorage.setItem('optic_tr_assets', $('tr-assets').value); } catch (e) { } if (data) { render(); loadOtherAssets(); } });
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
                                    <input type=""text"" id=""mw-address"" placeholder=""optio1..., osmo1... or 0x... (Ethereum / Base / Arbitrum)"" autocomplete=""off"" spellcheck=""false"" required>
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
                                            <th>Network</th>
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
                                const wallets = W.listSaved();
                                const network = (a) => W.valid(a) ? 'Optio' : W.validOsmo(a) ? 'Osmosis' : 'Ethereum · Base · Arbitrum';
                                const reports = (a) => W.valid(a)
                                    ? `<a href=""/page/address-distributions?address=${encodeURIComponent(a)}"">Distributions</a> · <a href=""/page/pnl?address=${encodeURIComponent(a)}"">Profit &amp; Loss</a>`
                                    : W.validOsmo(a)
                                        ? `<a href=""https://www.mintscan.io/osmosis/address/${encodeURIComponent(a)}"" target=""_blank"" rel=""noopener"">Mintscan</a> · used by the Tax Report`
                                        : [['Etherscan', 'https://etherscan.io/address/'], ['Basescan', 'https://basescan.org/address/'], ['Arbiscan', 'https://arbiscan.io/address/']]
                                            .map(([n, u]) => `<a href=""${u}${encodeURIComponent(a)}"" target=""_blank"" rel=""noopener"">${n}</a>`).join(' · ');
                                body.innerHTML = wallets.length === 0
                                    ? '<tr><td colspan=""6"" style=""text-align: center; color: var(--text-secondary);"">No wallets saved yet. Add your distribution wallet first; it becomes the primary.</td></tr>'
                                    : wallets.map(w => `<tr>
                                        <td>${W.valid(w.a) ? `<input type=""radio"" name=""mw-primary"" value=""${esc(w.a)}"" ${w.p ? 'checked' : ''} aria-label=""Make ${esc(w.a)} the primary wallet"">` : ''}</td>
                                        <td style=""white-space: nowrap;"">${network(w.a)}</td>
                                        <td class=""address-cell"">${esc(w.a)}${W.validEvm(w.a) ? `<div class=""mw-bal"" data-bal=""${esc(w.a)}"" style=""font-family: inherit; font-size: 12px; color: var(--text-secondary); margin-top: 4px; white-space: normal;"">Reading balances…</div>` : ''}</td>
                                        <td><input type=""text"" data-label=""${esc(w.a)}"" value=""${esc(w.l)}"" maxlength=""40"" placeholder=""${w.p ? 'Primary' : 'My wallet'}"" style=""width: 100%; min-width: 140px;""></td>
                                        <td style=""white-space: nowrap;"">${reports(w.a)}</td>
                                        <td><button type=""button"" data-remove=""${esc(w.a)}"" style=""padding: 4px 10px; font-size: 11px;"">Remove</button></td>
                                    </tr>`).join('');
                                loadBalances();
                            };

                            // ETH, USDC (Circle USDC; USDC.e on Arbitrum), TITN and TGT on each network for the 0x... wallets.
                            let balances = null;
                            const fillBalances = () => {
                                const fmt = (v, s) => Number(v).toLocaleString('en-US', { maximumFractionDigits: s === 'ETH' ? 6 : 2 });
                                const usd = (v) => '$' + Number(v).toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
                                const net = { base: 'Base', ethereum: 'Ethereum', arbitrum: 'Arbitrum' };
                                document.querySelectorAll('[data-bal]').forEach(el => {
                                    const w = balances && balances.wallets.find(x => x.address === el.dataset.bal);
                                    if (!w) { el.textContent = balances ? 'No balances found.' : el.textContent; return; }
                                    const parts = w.chains.filter(ch => ch.balances.length).map(ch => `<strong>${net[ch.chain] || ch.chain}:</strong> ${ch.balances.map(b => `${fmt(b.amount, b.symbol)} ${esc(b.symbol)}`).join(', ')}`);
                                    el.innerHTML = (parts.join(' · ') || 'Empty on Ethereum, Arbitrum and Base') + (w.usd > 0.005 ? ` · <strong>${usd(w.usd)}</strong>` : '') + ' <span style=""color: var(--text-muted);"">(staked TITN is on <a href=""/page/my-titn"">My TITN</a>)</span>';
                                });
                            };
                            const loadBalances = () => {
                                if (!document.querySelector('[data-bal]')) return;
                                if (balances) { fillBalances(); return; }
                                fetch('/api/evm/balances').then(r => r.ok ? r.json() : null).then(j => { balances = j; fillBalances(); }).catch(() => { });
                            };

                            document.getElementById('mw-form').addEventListener('submit', (e) => {
                                e.preventDefault();
                                const addressEl = document.getElementById('mw-address');
                                const labelEl = document.getElementById('mw-label');
                                const address = addressEl.value.trim().toLowerCase();
                                if (!W.validSaved(address)) { status.textContent = 'That is not a valid optio1..., osmo1... or 0x... address.'; return; }
                                if (W.isMine(address)) { status.textContent = 'That wallet is already saved.'; return; }
                                W.add(address, labelEl.value);
                                addressEl.value = '';
                                labelEl.value = '';
                                status.textContent = 'Saved.';
                                render();
                            });

                            body.addEventListener('change', (e) => {
                                if (e.target.name === 'mw-primary') {
                                    W.save(W.listSaved().map(w => ({ ...w, p: w.a === e.target.value })));
                                    status.textContent = 'Primary wallet updated.';
                                    render();
                                } else if (e.target.dataset.label) {
                                    W.save(W.listSaved().map(w => w.a === e.target.dataset.label ? { ...w, l: e.target.value } : w));
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
                                        <option value=""next"">Next day you could sell</option>
                                        <option value=""dlom"">Quoted less marketability discount</option>
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
                                if (closedRuleSelect.value !== 'next') params.set('closed', closedRuleSelect.value);
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
                            if (['dlom', 'last', 'quoted'].includes(initialParams.get('closed'))) closedRuleSelect.value = initialParams.get('closed');
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
        AppendMyWalletsScript(sb);
        sb.Append(@"
                    <style>
                        .t100-hist-btn { font-size: 11px; padding: 2px 10px; margin-top: 4px; min-height: 0; background: var(--surface); color: var(--accent-primary); border: 1px solid var(--accent-primary); box-shadow: none; text-transform: none; letter-spacing: 0; }
                        .t100-hist-row > td { background: color-mix(in srgb, var(--accent-primary) 4%, transparent); }
                        .t100-hist { padding: 8px 4px; font-size: 13px; }
                        .t100-hist-sum { display: grid; grid-template-columns: repeat(auto-fit, minmax(150px, 1fr)); gap: 6px 14px; margin-bottom: 6px; }
                        .t100-hist-sum b, .t100-hist-cols b { display: block; font-size: 11px; font-weight: 600; text-transform: uppercase; letter-spacing: 0.04em; color: var(--text-muted); }
                        .t100-hist-cols { display: grid; grid-template-columns: repeat(auto-fit, minmax(260px, 1fr)); gap: 12px; margin-top: 8px; }
                        .t100-hist .data-table { min-width: 0; }                        .t100-cp { background: none; border: none; box-shadow: none; padding: 0; min-height: 0; color: var(--accent-primary); text-decoration: underline; text-transform: none; letter-spacing: 0; font: inherit; font-weight: 600; text-align: left; cursor: pointer; overflow-wrap: anywhere; }
                        .t100-modal { position: fixed; inset: 0; z-index: 1000; background: rgba(0, 0, 0, 0.45); overflow-y: auto; -webkit-overflow-scrolling: touch; }
                        .t100-modal[hidden] { display: none; }
                        .t100-modal-panel { background: var(--bg-primary, var(--surface, #fff)); color: var(--text-primary); width: min(920px, calc(100% - 32px)); margin: 32px auto; border-radius: 10px; padding: 12px 16px 16px; box-shadow: 0 8px 30px rgba(0, 0, 0, 0.3); }
                        .t100-modal-head { display: flex; justify-content: space-between; gap: 8px; position: sticky; top: 0; background: inherit; padding: 4px 0 8px; z-index: 1; }
                        .t100-modal-btn { font-size: 13px; padding: 4px 12px; min-height: 0; background: var(--surface); color: var(--text-primary); border: 1px solid var(--border); box-shadow: none; text-transform: none; letter-spacing: 0; }
                        .t100-modal-title { display: flex; flex-direction: column; gap: 2px; margin-bottom: 8px; font-size: 13px; overflow-wrap: anywhere; }
                        .t100-modal-title strong { font-size: 16px; }
                        .t100-modal-title span { color: var(--text-secondary); }
                        .t100-between { border: 1px solid var(--border); border-radius: 8px; padding: 8px 10px; margin-bottom: 8px; font-size: 13px; }
                        .t100-between > b { display: block; font-size: 11px; font-weight: 600; text-transform: uppercase; letter-spacing: 0.04em; color: var(--text-muted); margin-bottom: 4px; }
                        .t100-between-sum { display: flex; flex-wrap: wrap; gap: 4px 16px; margin-bottom: 6px; }
                        .t100-xfers { max-height: 260px; overflow-y: auto; padding-right: 14px; }
                        .t100-xfers > div { display: grid; grid-template-columns: 92px minmax(0, 1fr) auto; gap: 8px; padding: 3px 0; border-bottom: 1px solid var(--border); font-variant-numeric: tabular-nums; }
                        .t100-xfers .t100-out { color: var(--error); }
                        .t100-xfers .t100-in { color: var(--success, #107c10); }                        .nm-tag { display: inline-block; font-weight: 700; border-radius: 6px; padding: 1px 7px; line-height: 1.45; overflow-wrap: anywhere; text-decoration: none; }
                        .nm-named { background: color-mix(in srgb, var(--accent-primary) 16%, transparent); color: var(--accent-primary); }
                        .nm-shared { background: color-mix(in srgb, #8250df 16%, transparent); color: #8250df; }
                        .nm-mine { background: #f5a623; color: #1f1300; box-shadow: 0 0 0 1px #c47f00 inset; }
                        tr.t100-mine > td { background: color-mix(in srgb, #f5a623 9%, transparent); }
                        tr.t100-mine > td:first-child { box-shadow: inset 4px 0 0 #f5a623; }
                        .t100-cp .nm-tag { text-decoration: none; }                        .t100-mlist { display: none; }
                        .t100-mcard { border: 1px solid var(--border); border-radius: 6px; padding: 6px 8px; margin: 0 0 6px; background: var(--surface); }
                        .t100-mhead { display: flex; justify-content: space-between; gap: 8px; font-size: 13px; font-variant-numeric: tabular-nums; }
                        .t100-mflows { display: flex; flex-wrap: wrap; gap: 2px 12px; font-size: 12px; color: var(--text-secondary); margin-top: 2px; font-variant-numeric: tabular-nums; }
                        .t100-mflows .t100-out { color: var(--error); }                        .t100-sort { display: none; }
                        @media (max-width: 640px) {
                            .t100-sort { display: flex; gap: 6px; align-items: center; font-size: 13px; }
                            .t100-sort select { font-size: 13px; padding: 4px 6px; min-height: 0; }
                            .t100 .top100-table-wrap { overflow: visible; max-height: none !important; }
                            .t100 table.data-table { min-width: 0; display: block; }
                            .t100 table.data-table thead { display: none; }
                            .t100 table.data-table tbody { display: block; }
                            .t100 table.data-table tr { display: grid; grid-template-columns: minmax(0, 1fr) minmax(0, 1fr); gap: 6px 12px; padding: 10px 12px; margin: 0 0 8px; border: 1px solid var(--border); border-radius: 8px; }
                            .t100 table.data-table td { display: block; border: none; padding: 0; text-align: left !important; white-space: normal; overflow-wrap: anywhere; }
                            .t100 table.data-table td::before { content: attr(data-label); display: block; font-size: 11px; font-weight: 600; text-transform: uppercase; letter-spacing: 0.04em; color: var(--text-muted); }
                            .t100 table.data-table td.t100-wide { grid-column: 1 / -1; }
                            .t100 table.data-table td.t100-rank { grid-column: 1 / -1; font-weight: 700; }
                            .t100 table.data-table td.t100-rank::before { display: none; }
                            .t100 table.data-table td.t100-zero { display: none; }
                            .t100 table.data-table td.t100-total { font-weight: 700; }
                            .t100 table.data-table tr.t100-mine { border: 2px solid #f5a623; }
                            .t100 table.data-table tr.t100-mine > td:first-child { box-shadow: none; }
                            .t100 table.data-table tr.t100-mine > td { background: none; }
                            .t100 table.data-table tr.t100-mine { background: color-mix(in srgb, #f5a623 8%, transparent); }
                            .t100 table.data-table tr.t100-hist-row { display: block; margin: -6px 0 10px; padding: 0; }
                            .t100 table.data-table tr.t100-hist-row > td { display: block; }
                            .t100 table.data-table tr.t100-hist-row > td::before { display: none; }
                            .t100 .t100-hist table.data-table, .t100 .t100-hist table.data-table tbody { display: table; width: 100%; }
                            .t100 .t100-hist table.data-table thead { display: table-header-group; }
                            .t100 .t100-hist table.data-table tr { display: table-row; border: none; padding: 0; margin: 0; }
                            .t100 .t100-hist table.data-table td { display: table-cell; padding: 3px 6px; border-bottom: 1px solid var(--border); white-space: nowrap; overflow-wrap: normal; }
                            .t100 .t100-hist table.data-table td:first-child { white-space: normal; }
                            .t100-mlist { display: block; max-height: 420px; overflow-y: auto; }
                            .t100-mtable { display: none; }
                            .t100-modal-panel { width: 100%; margin: 0; border-radius: 0; min-height: 100%; padding: 10px 12px 24px; }
                            .t100-xfers > div { grid-template-columns: 78px minmax(0, 1fr) auto; font-size: 12px; }
                            .t100 .t100-hist table.data-table td::before { display: none; }
                        }
                    </style>
                    <div class=""card t100"" style=""grid-column: 1 / -1;"">                        <div class=""card-content"">
                            <div style=""display: flex; gap: 8px; flex-wrap: wrap; align-items: center; margin-bottom: 8px;"">
                                <input type=""search"" id=""top100-search"" placeholder=""Search name, email or address"" autocomplete=""off"" spellcheck=""false"" style=""flex: 1 1 260px; max-width: 380px;"">
                                <label style=""font-size: 13px; display: flex; gap: 6px; align-items: center;""><input type=""checkbox"" id=""top100-named""> Named only</label>
                                <label class=""t100-sort"">Sort <select id=""top100-sort""><option value=""rank"">Rank</option><option value=""address"">Name</option><option value=""walletBalance"">Balance</option><option value=""staked"">Staked</option><option value=""unbonding"">Unbonding</option><option value=""totalLocked"">Locked</option><option value=""genesis"">Genesis allocation</option><option value=""distro"">From Distro Wallet</option></select></label>
                                <span id=""top100-count"" style=""font-size: 13px; color: var(--text-secondary);""></span>
                                <span id=""top100-distro"" style=""font-size: 13px; color: var(--text-secondary);""></span>
                                <span id=""top100-price"" style=""font-size: 13px; color: var(--text-secondary);""></span>
                            </div>
                            <div id=""top100-status"" style=""color: var(--text-secondary); margin-bottom: 8px;"">Loading top wallets...</div>
                            <div class=""data-table-wrap top100-table-wrap"">
                                <table class=""data-table"">
                                    <thead>
                                        <tr>
                                            <th class=""sortable sort-asc"" data-sort=""rank"">Rank</th>
                                            <th class=""sortable"" data-sort=""address"">Wallet</th>
                                            <th class=""sortable"" data-sort=""total"">Total</th>
                                            <th class=""sortable"" data-sort=""total"" title=""Total OPT at today's price"">Value (USD)</th>
                                            <th class=""sortable"" data-sort=""genesis"" title=""OPT this address received in the genesis allocation"">Genesis allocation</th>
                                            <th class=""sortable"" data-sort=""distro"" title=""All node and staking payouts from the Distro Wallet since launch, including the early MultiSend payouts"">From Distro Wallet</th>
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
                                        <tr><td colspan=""14"" style=""text-align: center; color: var(--text-secondary);"">Loading...</td></tr>
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
                            // Today's OPT price: Osmosis spot (a small sell quote), else CoinMarketCap's latest daily close.
                            let price = null;
                            const formatUsd = (opt) => price == null ? '-' : '$' + Math.round((opt || 0) * price).toLocaleString('en-US');
                            let wallets = [];
                            // Names from your named addresses (Tax Report) and your saved wallets.
                            let names = {}, globalNames = {};
                            const globalLabel = (a) => globalNames[a] ? globalNames[a].name + (globalNames[a].email ? ' · ' + globalNames[a].email : '') : '';
                            const nameOf = (a) => names[a] || globalLabel(a) || (window.opticWallets && window.opticWallets.label(a) ? 'My wallet · ' + window.opticWallets.label(a) : '');
                            // OPT received from the Distro Wallet (node and staking payouts), counted in the background by the server.
                            let distro = {}, distroRunning = false;
                            const distroOf = (w) => distro[w.address] ? Number(distro[w.address].opt) : null;
                            // Name tags: your own wallets stand out (amber), names you gave are blue, shared names (genesis list) purple.
                            const isMine = (a) => !!(window.opticWallets && window.opticWallets.list().some(x => x.a === a));
                            const labelHtml = (a, br) => {
                                const n = nameOf(a);
                                if (!n) return '';
                                const cls = isMine(a) ? 'nm-mine' : names[a] ? 'nm-named' : 'nm-shared';
                                const text = isMine(a) ? (names[a] || window.opticWallets.label(a) || 'My wallet') : n;
                                return `<span class=""nm-tag ${cls}"" title=""${cls === 'nm-mine' ? 'Your wallet' : cls === 'nm-named' ? 'Named by you' : 'Shared name'}"">${isMine(a) ? '★ ' : ''}${escapeHtml(text)}</span>${br ? '<br>' : ''}`;
                            };
                            const genesisOf = (w) => globalNames[w.address] && globalNames[w.address].genesisOpt != null ? Number(globalNames[w.address].genesisOpt) : null;
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
                            // ---------------------------------------------------------------- how a wallet's OPT built up over time
                            // From the wallet ledger (the same one the Tax Report uses): genesis, Distro Wallet payouts, staking rewards
                            // and transfers from other wallets, less what it sent out. Staking moves (delegate/undelegate) are left out:
                            // the OPT stays the wallet's.
                            const histCache = new Map(), openHist = new Set();
                            const fmtShort = (v) => Math.abs(v) >= 1e9 ? (v / 1e9).toFixed(2) + 'B' : Math.abs(v) >= 1e6 ? (v / 1e6).toFixed(1) + 'M' : Math.abs(v) >= 1e3 ? (v / 1e3).toFixed(0) + 'K' : Math.round(v).toString();
                            const shortAddr = (a) => a && a.length > 20 ? a.slice(0, 10) + '…' + a.slice(-6) : (a || '');
                            const who = (a) => labelHtml(a) || escapeHtml(distroSource && a === distroSource ? 'Distro Wallet' : shortAddr(a));
                            let distroSource = 'optio103r5ejt3gqyghvyel86hs5faru55r3w9kl4dst';
                            const buildHist = (w, L) => {
                                const genesis = genesisOf(w) || 0;
                                const months = new Map(), ins = new Map(), outs = new Map();
                                const tot = { distro: 0, rewards: 0, otherIn: 0, sent: 0, fees: 0 };
                                for (const e of L.entries || []) {
                                    if (e.category === 'module') continue;
                                    const m = String(e.timeUtc).slice(0, 7), amt = Number(e.amountOpt) || 0;
                                    if (!months.has(m)) months.set(m, { distro: 0, rewards: 0, otherIn: 0, sent: 0 });
                                    const row = months.get(m);
                                    if (e.direction === 'in') {
                                        if (e.category === 'distribution') { row.distro += amt; tot.distro += amt; }
                                        else if (e.category === 'reward') { row.rewards += amt; tot.rewards += amt; }
                                        else { row.otherIn += amt; tot.otherIn += amt; ins.set(e.counterparty, (ins.get(e.counterparty) || 0) + amt); }
                                    } else {
                                        row.sent += amt;
                                        if (e.category === 'fee') tot.fees += amt;
                                        else { tot.sent += amt; outs.set(e.counterparty, (outs.get(e.counterparty) || 0) + amt); }
                                    }
                                }
                                const keys = [...months.keys()].sort();
                                let cDistro = 0, cRewards = 0, cOther = 0, cSent = 0;
                                const series = keys.map(m => {
                                    const r = months.get(m);
                                    cDistro += r.distro; cRewards += r.rewards; cOther += r.otherIn; cSent += r.sent;
                                    return { m, ...r, cDistro, cRewards, cOther, balance: genesis + cDistro + cRewards + cOther - cSent };
                                });
                                const received = genesis + tot.distro + tot.rewards + tot.otherIn;
                                const net = received - tot.sent - tot.fees;
                                // Chart: stacked cumulative received (genesis, Distro, rewards, others) and the running balance line.
                                const W = 640, H = 190, padL = 46, padB = 22, padT = 8;
                                const maxY = Math.max(1, ...series.map(s => genesis + s.cDistro + s.cRewards + s.cOther));
                                const x = (i) => padL + (series.length <= 1 ? (W - padL - 8) / 2 : i * (W - padL - 8) / (series.length - 1));
                                const y = (v) => padT + (H - padT - padB) * (1 - v / maxY);
                                const area = (top, bottom, color) => series.length ? `<path d=""M${series.map((s, i) => `${x(i).toFixed(1)},${y(top(s)).toFixed(1)}`).join(' L')} L${series.map((s, i) => `${x(i).toFixed(1)},${y(bottom(s)).toFixed(1)}`).reverse().join(' L')} Z"" fill=""${color}"" opacity=""0.75""></path>` : '';
                                const g0 = () => genesis, g1 = (s) => genesis + s.cDistro, g2 = (s) => g1(s) + s.cRewards, g3 = (s) => g2(s) + s.cOther;
                                const ticks = [0, 0.25, 0.5, 0.75, 1].map(f => `<line x1=""${padL}"" x2=""${W - 8}"" y1=""${y(maxY * f)}"" y2=""${y(maxY * f)}"" stroke=""var(--border)"" stroke-width=""0.5""></line><text x=""${padL - 4}"" y=""${y(maxY * f) + 3}"" text-anchor=""end"" font-size=""9"" fill=""var(--text-muted)"">${fmtShort(maxY * f)}</text>`).join('');
                                const step = Math.max(1, Math.ceil(series.length / 8));
                                const labels = series.map((s, i) => i % step === 0 || i === series.length - 1 ? `<text x=""${x(i)}"" y=""${H - 6}"" text-anchor=""middle"" font-size=""9"" fill=""var(--text-muted)"">${s.m}</text>` : '').join('');
                                const line = series.length ? `<polyline points=""${series.map((s, i) => `${x(i).toFixed(1)},${y(Math.max(0, s.balance)).toFixed(1)}`).join(' ')}"" fill=""none"" stroke=""var(--text-primary)"" stroke-width=""1.6""></polyline>` : '';
                                const chart = `<svg viewBox=""0 0 ${W} ${H}"" style=""width: 100%; max-width: 760px; height: auto; display: block;"" role=""img"" aria-label=""Cumulative OPT received and balance"">${ticks}
                                    ${genesis ? area(g0, () => 0, '#8a8886') : ''}${area(g1, g0, 'var(--accent-primary)')}${area(g2, g1, '#107c10')}${area(g3, g2, '#ca5010')}${line}${labels}</svg>`;
                                const key = (c, t) => `<span style=""display: inline-flex; gap: 4px; align-items: center; margin-right: 12px;""><i style=""width: 10px; height: 10px; background: ${c}; display: inline-block; border-radius: 2px;""></i>${t}</span>`;
                                const cpRows = (list) => list.map(([a, v]) => `<tr><td><button type=""button"" class=""t100-cp"" data-cp=""${escapeHtml(a)}"" data-parent=""${escapeHtml(w.address)}"">${who(a)}</button></td><td class=""amount-cell"">${formatAmt(v)}</td></tr>`).join('');
                                const top = (map) => {
                                    const list = [...map.entries()].sort((a, b) => b[1] - a[1]);
                                    if (!list.length) return '<tr><td colspan=""2"" style=""color: var(--text-secondary);"">None</td></tr>';
                                    return cpRows(list.slice(0, 6)) + (list.length > 6 ? `<tr><td colspan=""2""><details><summary style=""cursor: pointer; font-size: 12px;"">All ${list.length}</summary><table class=""data-table""><tbody>${cpRows(list.slice(6))}</tbody></table></details></td></tr>` : '');
                                };
                                const sum = (k, v) => `<div><b>${k}</b>${v}</div>`;
                                return `<div class=""t100-hist"">
                                    <div class=""t100-hist-sum"">
                                        ${sum('Genesis allocation', genesis ? formatAmt(genesis) : '0')}
                                        ${sum('From Distro Wallet', formatAmt(tot.distro))}
                                        ${sum('Staking rewards', formatAmt(tot.rewards))}
                                        ${sum('From other wallets', formatAmt(tot.otherIn))}
                                        ${sum('Sent out', formatAmt(tot.sent) + (tot.fees ? ` <span style=""color: var(--text-muted); font-size: 11px;"">+ ${formatAmt(tot.fees)} fees</span>` : ''))}
                                        ${sum('Received − sent', formatAmt(net))}
                                    </div>
                                    <div style=""font-size: 12px; margin: 6px 0;"">${genesis ? key('#8a8886', 'Genesis') : ''}${key('var(--accent-primary)', 'Distro Wallet')}${key('#107c10', 'Staking rewards')}${key('#ca5010', 'Other wallets')}${key('var(--text-primary)', 'Balance (received − sent)')}</div>
                                    ${chart}
                                    <div class=""t100-hist-cols"">
                                        <div><b>Largest senders to this wallet</b><table class=""data-table""><tbody>${top(ins)}</tbody></table></div>
                                        <div><b>Largest recipients from this wallet</b><table class=""data-table""><tbody>${top(outs)}</tbody></table></div>
                                    </div>
                                    <details style=""margin-top: 6px;""><summary style=""cursor: pointer; font-size: 13px;"">Month by month</summary>
                                        <div class=""t100-mlist"">${series.map(s => `<div class=""t100-mcard""><div class=""t100-mhead""><strong>${s.m}</strong><span>Balance <strong>${formatAmt(s.balance)}</strong></span></div><div class=""t100-mflows"">${s.distro ? `<span>Distro +${formatAmt(s.distro)}</span>` : ''}${s.rewards ? `<span>Rewards +${formatAmt(s.rewards)}</span>` : ''}${s.otherIn ? `<span>From others +${formatAmt(s.otherIn)}</span>` : ''}${s.sent ? `<span class=""t100-out"">Sent −${formatAmt(s.sent)}</span>` : ''}</div></div>`).join('')}</div>
                                        <div class=""data-table-wrap t100-mtable"" style=""max-height: 360px;""><table class=""data-table""><thead><tr><th>Month</th><th class=""amount-cell"">From Distro</th><th class=""amount-cell"">Staking rewards</th><th class=""amount-cell"">From others</th><th class=""amount-cell"">Sent out</th><th class=""amount-cell"">Balance</th></tr></thead>
                                        <tbody>${series.map(s => `<tr><td>${s.m}</td><td class=""amount-cell"">${formatAmt(s.distro)}</td><td class=""amount-cell"">${formatAmt(s.rewards)}</td><td class=""amount-cell"">${formatAmt(s.otherIn)}</td><td class=""amount-cell"">${formatAmt(s.sent)}</td><td class=""amount-cell"">${formatAmt(s.balance)}</td></tr>`).join('')}</tbody></table></div>
                                    </details>
                                    <div style=""font-size: 11px; color: var(--text-muted); margin-top: 6px;"">From ${(L.entries || []).length.toLocaleString('en-US')} ledger entries on the Optio chain. Staking and unstaking are left out (the OPT stays in the wallet). ${w.total != null ? `Total now ${formatAmt(w.total)} OPT; the balance line can differ by pending rewards and the snapshot date.` : ''} Tap a sender or recipient to see its history.${L.truncated ? ' <strong>The history hit the 20,000-transaction cap; older entries are missing.</strong>' : ''}</div>
                                </div>`;
                            };
                            // ---------------------------------------------------------------- follow a sender or recipient
                            // Tapping a counterparty opens a sheet with the transfers between the two wallets and that wallet's own history,
                            // whose counterparties can be tapped in turn (Back returns to the previous wallet).
                            const ledgerCache = new Map();
                            const getLedger = (a) => {
                                if (!ledgerCache.has(a)) ledgerCache.set(a, fetch('/api/wallet/ledger?address=' + encodeURIComponent(a)).then(async r => {
                                    const L = await r.json();
                                    if (!r.ok) throw new Error(L.error || ('HTTP ' + r.status));
                                    return L;
                                }).catch(e => { ledgerCache.delete(a); throw e; }));
                                return ledgerCache.get(a);
                            };
                            const cpStack = [];
                            const modal = document.createElement('div');
                            modal.className = 't100-modal';
                            modal.hidden = true;
                            document.body.appendChild(modal);
                            let modalToken = 0;
                            const renderModal = async () => {
                                const cur = cpStack[cpStack.length - 1];
                                if (!cur) { modal.hidden = true; modal.innerHTML = ''; document.body.style.overflow = ''; return; }
                                const token = ++modalToken;
                                modal.hidden = false;
                                document.body.style.overflow = 'hidden';
                                const a = cur.address, parent = cur.parent;
                                const w = wallets.find(x => x.address === a) || { address: a, total: null, rank: null };
                                const head = `<div class=""t100-modal-head"">${cpStack.length > 1 ? '<button type=""button"" class=""t100-modal-btn"" data-cp-back>‹ Back</button>' : '<span></span>'}
                                    <button type=""button"" class=""t100-modal-btn"" data-cp-close aria-label=""Close"">Close ✕</button></div>
                                    <div class=""t100-modal-title""><strong>${who(a)}</strong><span class=""address-cell"">${escapeHtml(a)}</span>${w.rank ? `<span>Top 100 #${w.rank} · ${formatAmt(w.total)} OPT now</span>` : ''}</div>`;
                                const panel = (between, body) => `<div class=""t100-modal-panel"">${head}${between}${body}</div>`;
                                modal.innerHTML = panel('<div class=""t100-hist"" style=""color: var(--text-secondary);"">Reading…</div>', '');
                                let between = '';
                                try {
                                    const PL = await getLedger(parent);
                                    const xs = (PL.entries || []).filter(e => e.counterparty === a && e.category !== 'fee');
                                    const inSum = xs.filter(e => e.direction === 'in').reduce((s, e) => s + e.amountOpt, 0), outSum = xs.filter(e => e.direction !== 'in').reduce((s, e) => s + e.amountOpt, 0);
                                    between = `<div class=""t100-between""><b>Between ${who(parent)} and this wallet</b>
                                        <div class=""t100-between-sum"">${inSum ? `<span>Sent to ${who(parent)}: <strong>${formatAmt(inSum)}</strong></span>` : ''}${outSum ? `<span>Received from ${who(parent)}: <strong>${formatAmt(outSum)}</strong></span>` : ''}${!xs.length ? '<span>No direct transfers found.</span>' : ''}</div>
                                        ${xs.length ? `<div class=""t100-xfers"">${xs.slice().reverse().map(e => `<div><span>${String(e.timeUtc).slice(0, 10)}</span><span>${e.direction === 'in' ? 'Sent' : 'Received'}</span><strong class=""${e.direction === 'in' ? 't100-out' : 't100-in'}"">${e.direction === 'in' ? '−' : '+'}${formatAmt(e.amountOpt)}</strong></div>`).join('')}</div>` : ''}</div>`;
                                } catch { }
                                if (token !== modalToken) return;
                                modal.innerHTML = panel(between, '<div class=""t100-hist"" style=""color: var(--text-secondary);"">Reading this wallet&#39;s history from the chain… large wallets can take a minute.</div>');
                                let body;
                                try { body = buildHist(w, await getLedger(a)); }
                                catch (error) { body = `<div class=""t100-hist"" style=""color: var(--error);"">Could not read the history: ${escapeHtml(error.message)}</div>`; }
                                if (token !== modalToken) return;
                                modal.innerHTML = panel(between, body);
                                modal.scrollTop = 0;
                            };
                            document.addEventListener('click', (e) => {
                                const cp = e.target.closest('[data-cp]');
                                if (cp) { e.preventDefault(); cpStack.push({ address: cp.dataset.cp, parent: cp.dataset.parent }); renderModal(); return; }
                                if (e.target.closest('[data-cp-back]')) { cpStack.pop(); renderModal(); return; }
                                if (e.target.closest('[data-cp-close]') || e.target === modal) { cpStack.length = 0; renderModal(); }
                            });
                            document.addEventListener('keydown', (e) => { if (e.key === 'Escape' && cpStack.length) { cpStack.length = 0; renderModal(); } });

                            const histRow = (w) => {
                                const h = histCache.get(w.address);
                                const inner = !h ? '<div class=""t100-hist"" style=""color: var(--text-secondary);"">Reading this wallet&#39;s history from the chain… large wallets can take a minute.</div>'
                                    : h.error ? `<div class=""t100-hist"" style=""color: var(--error);"">Could not read the history: ${escapeHtml(h.error)}</div>` : h.html;
                                return `<tr class=""t100-hist-row""><td colspan=""15"">${inner}</td></tr>`;
                            };
                            const toggleHist = async (address) => {
                                if (openHist.has(address)) { openHist.delete(address); renderRows(); return; }
                                openHist.add(address);
                                renderRows();
                                if (histCache.has(address)) return;
                                const w = wallets.find(x => x.address === address);
                                try {
                                    histCache.set(address, { html: buildHist(w, await getLedger(address)) });
                                } catch (error) { histCache.set(address, { error: error.message }); }
                                renderRows();
                            };
                            body.addEventListener('click', (e) => { const b = e.target.closest('[data-hist]'); if (b) toggleHist(b.dataset.hist); });

                            const renderRows = () => {
                                const q = document.getElementById('top100-search').value.trim().toLowerCase();
                                const namedOnly = document.getElementById('top100-named').checked;
                                const shown = wallets.filter(w => (!namedOnly || nameOf(w.address)) && (!q || String(w.address).toLowerCase().includes(q) || nameOf(w.address).toLowerCase().includes(q)));
                                document.getElementById('top100-count').textContent = `Showing ${shown.length} of ${wallets.length} · ${wallets.filter(w => nameOf(w.address)).length} named`;
                                const sorted = shown.sort((a, b) => {
                                    let result;
                                    if (sortKey === 'address') {
                                        result = (nameOf(a.address) || '\uffff' + a.address).localeCompare(nameOf(b.address) || '\uffff' + b.address, 'en-US', { sensitivity: 'base' });
                                    } else if (sortKey === 'distro') {
                                        result = (distroOf(a) ?? -1) - (distroOf(b) ?? -1);
                                    } else if (sortKey === 'genesis') {
                                        result = (genesisOf(a) ?? -1) - (genesisOf(b) ?? -1);
                                    } else {
                                        result = (Number(a[sortKey]) || 0) - (Number(b[sortKey]) || 0);
                                    }
                                    return sortDirection === 'asc' ? result : -result;
                                });

                                body.innerHTML = sorted.map(w => isMine(w.address) ? rowHtml(w).replace('<tr>', '<tr class=""t100-mine"">') : rowHtml(w)).join('');
                                return;
                                function rowHtml(w) { return `
                                    <tr>
                                        <td class=""rank-cell t100-rank"">#${w.rank}${isMine(w.address) ? ' <span class=""nm-tag nm-mine"">★ Yours</span>' : ''}</td>
                                        <td class=""t100-wide"" data-label=""Wallet"">${labelHtml(w.address, true)}<a class=""address-cell"" href=""/page/address-distributions?address=${encodeURIComponent(w.address)}"" title=""Distributions received by this address"" style=""color: inherit;"">${escapeHtml(w.address)}</a><br><button type=""button"" class=""t100-hist-btn"" data-hist=""${escapeHtml(w.address)}"" aria-expanded=""${openHist.has(w.address)}"">${openHist.has(w.address) ? 'Hide history' : 'How it grew'}</button></td>
                                        <td class=""amount-cell t100-total"" data-label=""Total OPT"">${formatAmt(w.total)}</td>
                                        <td class=""amount-cell t100-total"" data-label=""Value (USD)"">${formatUsd(w.total)}</td>
                                        <td class=""amount-cell${genesisOf(w) == null ? ' t100-zero' : ''}"" data-label=""Genesis allocation"" title=""${genesisOf(w) == null ? '' : escapeHtml(globalNames[w.address].source || '')}"">${genesisOf(w) == null ? '-' : formatAmt(genesisOf(w))}</td>
                                        <td class=""amount-cell${distroOf(w) ? '' : ' t100-zero'}"" data-label=""From Distro Wallet"" title=""${distro[w.address] ? distro[w.address].payouts.toLocaleString('en-US') + ' payouts, counted to block ' + Number(distro[w.address].scannedToHeight).toLocaleString('en-US') : ''}"">${distro[w.address] ? formatAmt(distroOf(w)) : (distroRunning ? '…' : '-')}</td>
                                        <td class=""amount-cell"" data-label=""Balance"">${formatAmt(w.walletBalance)}</td>
                                        <td class=""amount-cell"" data-label=""Staked"">${formatAmt(w.staked)}</td>
                                        <td class=""amount-cell${w.unbonding ? '' : ' t100-zero'}"" data-label=""Unbonding"">${formatAmt(w.unbonding)}</td>
                                        <td class=""amount-cell${w.totalLocked ? '' : ' t100-zero'}"" data-label=""Locked"">${formatAmt(w.totalLocked)}</td>
                                        <td class=""amount-cell${w.lock6Months ? '' : ' t100-zero'}"" data-label=""Locked 6 months"">${formatAmt(w.lock6Months)}</td>
                                        <td class=""amount-cell${w.lock12Months ? '' : ' t100-zero'}"" data-label=""Locked 12 months"">${formatAmt(w.lock12Months)}</td>
                                        <td class=""amount-cell${w.lock18Months ? '' : ' t100-zero'}"" data-label=""Locked 18 months"">${formatAmt(w.lock18Months)}</td>
                                        <td class=""amount-cell${w.lock24Months ? '' : ' t100-zero'}"" data-label=""Locked 24 months"">${formatAmt(w.lock24Months)}</td>
                                    </tr>${openHist.has(w.address) ? histRow(w) : ''}
                                `; }
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

                            document.getElementById('top100-search').addEventListener('input', renderRows);
                            document.getElementById('top100-named').addEventListener('change', renderRows);
                            // Phones have no column headers to click, so sorting is a menu there.
                            document.getElementById('top100-sort').addEventListener('change', (e) => { sortKey = e.target.value; sortDirection = sortKey === 'rank' || sortKey === 'address' ? 'asc' : 'desc'; updateSortHeaders(); renderRows(); });
                            try {
                                [names, globalNames] = await Promise.all([
                                    fetch('/api/address-book').then(r => r.ok ? r.json() : {}).catch(() => ({})),
                                    fetch('/api/address-book/global').then(r => r.ok ? r.json() : {}).catch(() => ({})),
                                ]);
                                names = names || {}; globalNames = globalNames || {};
                                const [spot, daily] = await Promise.all([
                                    fetch('/api/market/quote?opt=1000').then(r => r.ok ? r.json() : null).catch(() => null),
                                    fetch('/api/price/daily').then(r => r.ok ? r.json() : null).catch(() => null),
                                ]);
                                const lastClose = daily && daily.rows && daily.rows.length ? daily.rows[daily.rows.length - 1] : null;
                                if (spot && spot.avgPriceUsd) { price = spot.avgPriceUsd; document.getElementById('top100-price').textContent = `· Value at $${price.toFixed(6)} per OPT (Osmosis spot now)`; }
                                else if (lastClose && lastClose.priceUsd) { price = lastClose.priceUsd; document.getElementById('top100-price').textContent = `· Value at $${price.toFixed(6)} per OPT (CoinMarketCap close ${lastClose.date})`; }
                                document.getElementById('top100-price').title = 'Holdings this large could not be sold at this price; the market is far too thin.';
                                const response = await fetch('/api/top100');
                                const data = await response.json();
                                if (!response.ok) throw new Error(data.error || 'Unable to load top wallets');
                                wallets = data.wallets || [];
                                if (wallets.length === 0) {
                                    status.textContent = 'No wallet balance data found. Generate wallet-balances.csv, then refresh this page.';
                                    body.innerHTML = '<tr><td colspan=""14"" style=""text-align: center; color: var(--text-secondary);"">No data available</td></tr>';
                                    return;
                                }

                                status.hidden = true;
                                updateSortHeaders();
                                renderRows();
                                // Distro totals: the server counts them in the background; poll until it is done.
                                const loadDistro = async () => {
                                    try {
                                        const d = await fetch('/api/top100/distro').then(r => r.json());
                                        distro = d.entries || {}; distroRunning = !!d.running;
                                        document.getElementById('top100-distro').textContent = d.error ? `· Distro totals stopped: ${d.error}` : d.running ? `· Counting Distro Wallet payouts: ${d.done} of ${d.total} addresses…` : '';
                                        renderRows();
                                        if (d.running) setTimeout(loadDistro, 10000);
                                    } catch { }
                                };
                                loadDistro();
                            } catch (error) {
                                status.textContent = 'Error loading top wallets: ' + error.message;
                                body.innerHTML = '<tr><td colspan=""14"" style=""text-align: center; color: var(--error);"">Error loading data</td></tr>';
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

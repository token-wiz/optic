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
    private static decimal? _historicalEmissionDepositCache;
    private static DateTimeOffset _historicalEmissionDepositCacheTime;
    private static List<TopWalletEntry>? _top100WalletCache;
    private static DateTimeOffset _top100WalletCacheTime;

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
                maxPages);

            if (totalUopt <= BigInteger.Zero)
            {
                totalUopt = await SumHistoricalReceivesFromRpcAsync(
                    rpcHttp,
                    $"transfer.recipient='{HistoricalEmissionDepositAddress}'",
                    HistoricalEmissionDepositAddress,
                    denomWantedUpper,
                    pageLimit,
                    maxPages);
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

    static async Task<BigInteger> SumHistoricalReceivesFromRpcAsync(
        HttpClient rpcHttp,
        string query,
        string recipient,
        string denomWantedUpper,
        int pageLimit,
        int maxPages)
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

                total += SumReceiveEventsFromTxSearchResult(tx, recipient, denomWantedUpper);
            }

            if (count < perPage)
                break;
        }

        return total;
    }

    static BigInteger SumReceiveEventsFromTxSearchResult(JsonElement tx, string recipient, string denomWantedUpper)
    {
        if (!tx.TryGetProperty("tx_result", out var txResult) ||
            !txResult.TryGetProperty("events", out var events) ||
            events.ValueKind != JsonValueKind.Array)
            return BigInteger.Zero;

        BigInteger total = BigInteger.Zero;
        foreach (var ev in events.EnumerateArray())
        {
            var type = ev.TryGetProperty("type", out var typeEl) ? typeEl.GetString() ?? "" : "";
            if (!string.Equals(type, "coin_received", StringComparison.Ordinal) &&
                !string.Equals(type, "transfer", StringComparison.Ordinal))
                continue;

            var attrs = ReadCometEventAttributes(ev);
            if (string.Equals(type, "coin_received", StringComparison.Ordinal))
            {
                foreach (var entry in ParseCoinEventTuples(attrs, "receiver", denomWantedUpper))
                {
                    if (entry.Party.Equals(recipient, StringComparison.OrdinalIgnoreCase))
                        total += entry.AmountUnits;
                }
            }
            else
            {
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

        .top100-table-wrap .data-table th {
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
    </style>
</head>
<body>
    <div class=""app-container"">
        <nav class=""top-nav"">
            <div class=""top-nav-inner"">
                <a href=""/"" class=""brand-link"" aria-label=""OPTIC Dashboard"">
                    <img src=""/images/optic-logo.png"" alt=""OPTIC Logo"" />
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
                AppendDistributionsCard(sb);
                AppendCounterpartiesCard(sb);
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
                                                const rect = svg.getBoundingClientRect();
                                                const relX = (clientX - rect.left) / rect.width * width;
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
                                                tooltip.style.left = `${(x / width) * 100}%`;
                                                tooltip.style.top = `${y}px`;
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
                                                const rect = svg.getBoundingClientRect();
                                                const relX = (clientX - rect.left) / rect.width * width;
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
                                                tooltip.style.left = `${(x / width) * 100}%`;
                                                tooltip.style.top = `${y}px`;
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

        // Check if this is counterparties output and render specially
        if (args.Contains("counterparties") && result.exitCode == 0)
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

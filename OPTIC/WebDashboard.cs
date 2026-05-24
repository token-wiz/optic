using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using OPTIC.Services;

public static class WebDashboard
{
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

        Console.WriteLine($"Starting OPTIC web dashboard at {url}");
        await app.RunAsync($"http://{host}:{port}");
    }

    static string GetPageTitle(string page)
    {
        return page switch
        {
            "dashboard" => "Dashboard",
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
    <title>Dashboard</title>
    <link rel=""icon"" type=""image/png"" href=""/images/optic-icon.png"">
    <style>
        :root {
            --bg-primary: #0a0e27;
            --bg-secondary: #0f1535;
            --surface: #1a2540;
            --surface-light: #243456;
            --border: #2d3e5f;
            --text-primary: #e4e6eb;
            --text-secondary: #a0a8b8;
            --accent-primary: #10b981;
            --accent-secondary: #059669;
            --accent-light: #a7f3d0;
            --error: #ef4444;
            --warning: #f97316;
            --info: #3b82f6;
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
            background: var(--border);
            border-radius: 3px;
        }

        ::-webkit-scrollbar-thumb:hover {
            background: var(--text-secondary);
        }

        /* Firefox scrollbar */
        * {
            scrollbar-width: thin;
            scrollbar-color: var(--border) transparent;
        }

        body {
            font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, 'Helvetica Neue', Arial, sans-serif;
            background: var(--bg-primary);
            color: var(--text-primary);
            line-height: 1.6;
        }

        .app-container {
            display: flex;
            height: 100vh;
        }

        /* Sidebar */
        .sidebar {
            position: fixed;
            left: 0;
            top: 0;
            width: 260px;
            height: 100vh;
            background: var(--bg-secondary);
            overflow-y: auto;
            z-index: 1000;
            padding: 0;
        }

        .sidebar-header {
            padding: 16px 12px;
            position: sticky;
            top: 0;
            background: var(--bg-secondary);
            display: flex;
            align-items: center;
            justify-content: center;
            min-height: auto;
        }

        .sidebar-logo {
            display: flex;
            align-items: center;
            justify-content: center;
            height: auto;
            width: 100%;
            max-width: 220px;
        }

        .sidebar-logo img {
            max-width: 100%;
            max-height: 100%;
            width: auto;
            height: auto;
        }

        .sidebar-menu {
            list-style: none;
            padding: 12px 0;
        }

        .menu-section {
            padding: 8px 12px;
        }

        .menu-label {
            display: block;
            padding: 8px 12px;
            font-size: 11px;
            font-weight: 700;
            text-transform: uppercase;
            letter-spacing: 0.8px;
            color: var(--text-secondary);
            margin-top: 8px;
        }

        .menu-item {
            display: block;
            padding: 10px 12px;
            color: var(--text-secondary);
            text-decoration: none;
            border-left: 3px solid transparent;
            transition: all 0.2s ease;
            font-size: 14px;
        }

        .menu-item:hover {
            background: var(--surface);
            color: var(--text-primary);
        }

        .menu-item.active {
            background: rgba(16, 185, 129, 0.1);
            border-left-color: var(--accent-primary);
            color: var(--accent-primary);
            font-weight: 600;
        }

        /* Main Content */
        .main-content {
            margin-left: 260px;
            flex: 1;
            display: flex;
            flex-direction: column;
            background: var(--bg-primary);
        }

        header {
            background: var(--bg-secondary);
            padding: 18px;
            position: sticky;
            top: 0;
            z-index: 100;
        }

        .header-content {
            display: flex;
            justify-content: space-between;
            align-items: center;
        }

        h1 {
            font-size: 28px;
            font-weight: 700;
            color: var(--text-primary);
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
            background: var(--surface);
            padding: 4px 12px;
            border-radius: 4px;
            border: 1px solid var(--border);
            color: var(--accent-primary);
            font-weight: 600;
            font-family: 'Courier New', monospace;
        }

        main {
            flex: 1;
            overflow-y: auto;
            padding: 24px;
        }

        /* Dashboard Grid */
        .metrics-grid {
            display: grid;
            grid-template-columns: repeat(auto-fit, minmax(220px, 1fr));
            gap: 16px;
            margin-bottom: 32px;
        }

        .metric-card {
            background: var(--surface);
            border: 1px solid var(--border);
            border-radius: 8px;
            padding: 20px;
            transition: all 0.3s ease;
        }

        .metric-card:hover {
            background: var(--surface-light);
            border-color: var(--accent-primary);
            transform: translateY(-2px);
            box-shadow: 0 8px 24px rgba(16, 185, 129, 0.1);
        }

        .metric-label {
            font-size: 12px;
            font-weight: 600;
            text-transform: uppercase;
            letter-spacing: 0.5px;
            color: var(--text-secondary);
            margin-bottom: 8px;
        }

        .metric-value {
            font-size: 24px;
            font-weight: 700;
            color: var(--accent-primary);
            word-break: break-all;
        }

        /* Report Cards */
        .report-cards {
            display: grid;
            grid-template-columns: repeat(auto-fit, minmax(280px, 1fr));
            gap: 20px;
        }

        .card {
            background: var(--surface);
            border: 1px solid var(--border);
            border-radius: 8px;
            overflow: hidden;
            transition: all 0.3s ease;
        }

        .card:hover {
            border-color: var(--accent-primary);
            box-shadow: 0 12px 32px rgba(16, 185, 129, 0.1);
        }

        .card h2 {
            background: linear-gradient(135deg, rgba(16, 185, 129, 0.1), rgba(5, 150, 105, 0.05));
            padding: 16px;
            font-size: 16px;
            font-weight: 600;
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
            background: var(--bg-secondary);
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
            font-weight: 600;
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
            padding: 10px;
            border: 1px solid var(--border);
            border-radius: 4px;
            background: var(--bg-primary);
            color: var(--text-primary);
            font-size: 13px;
            transition: border-color 0.2s ease;
        }

        input[type=""text""]:focus,
        input[type=""number""]:focus,
        select:focus {
            outline: none;
            border-color: var(--accent-primary);
            box-shadow: 0 0 0 3px rgba(16, 185, 129, 0.1);
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
            color: var(--bg-primary);
            border: none;
            padding: 10px 20px;
            border-radius: 4px;
            font-weight: 600;
            font-size: 13px;
            cursor: pointer;
            transition: all 0.2s ease;
            text-transform: uppercase;
            letter-spacing: 0.5px;
        }

        button:hover {
            background: var(--accent-secondary);
            transform: translateY(-2px);
            box-shadow: 0 4px 12px rgba(16, 185, 129, 0.3);
        }

        button:active {
            transform: translateY(0);
        }

        .header-links {
            display: flex;
            gap: 16px;
            align-items: center;
        }

        .header-link {
            color: var(--text-secondary);
            text-decoration: none;
            font-size: 14px;
            transition: color 0.2s ease;
        }

        .header-link:hover {
            color: var(--accent-primary);
        }

        .btn-donate {
            background: var(--accent-primary);
            color: #0a0e27;
            border: none;
            padding: 8px 16px;
            border-radius: 4px;
            font-weight: 600;
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
            background-color: rgba(0, 0, 0, 0.7);
            align-items: center;
            justify-content: center;
        }

        .modal.show {
            display: flex;
        }

        .modal-content {
            background-color: var(--bg-secondary);
            padding: 32px;
            border-radius: 12px;
            text-align: center;
            max-width: 500px;
            color: var(--text-primary);
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
            margin: 16px 0;
            word-break: break-all;
            font-size: 12px;
            color: var(--text-secondary);
            font-family: monospace;
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
            background: var(--surface);
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
            border-radius: 4px;
            padding: 12px;
            font-family: 'Courier New', monospace;
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
        @media (max-width: 768px) {
            .sidebar {
                width: 200px;
            }

            .main-content {
                margin-left: 200px;
            }

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
        }
    </style>
</head>
<body>
    <div class=""app-container"">
        <nav class=""sidebar"">
            <div class=""sidebar-header"">
                <div class=""sidebar-logo"">
                    <img src=""/images/optic-logo.png"" alt=""OPTIC Logo"" />
                </div>
            </div>
            <ul class=""sidebar-menu"">
                <li class=""menu-section"" style=""padding-top: 0; margin-bottom: 24px;"">
                    <a href=""/"" class=""menu-item" + (page == "dashboard" ? @""" active" : @"""") + @""">Dashboard</a>
                </li>
                <li class=""menu-section"">
                    <span class=""menu-label"">Reports</span>
                    <a href=""/page/distributions"" class=""menu-item" + (page == "distributions" ? @""" active" : @"""") + @""">Distributions</a>
                    <a href=""/page/locks"" class=""menu-item" + (page == "locks" ? @""" active" : @"""") + @""">Locks & Staking</a>
                    <a href=""/page/counterparties"" class=""menu-item" + (page == "counterparties" ? @""" active" : @"""") + @""">Counterparties</a>
                    <a href=""/page/network"" class=""menu-item" + (page == "network" ? @""" active" : @"""") + @""">Network</a>
                    <a href=""/page/wallet"" class=""menu-item" + (page == "wallet" ? @""" active" : @"""") + @""">Wallet</a>
                </li>
                <li class=""menu-section"">
                    <span class=""menu-label"">Advanced</span>
                    <a href=""/page/multisend"" class=""menu-item" + (page == "multisend" ? @""" active" : @"""") + @""">Multisend</a>
                    <a href=""/page/cmc"" class=""menu-item" + (page == "cmc" ? @""" active" : @"""") + @""">CMC Data</a>
                    <a href=""/page/custom"" class=""menu-item" + (page == "custom" ? @""" active" : @"""") + @""">Custom Args</a>
                </li>
                <li class=""menu-section"">
                    <span class=""menu-label"">Analytics</span>
                    <a href=""/page/analytics"" class=""menu-item" + (page == "analytics" ? @""" active" : @"""") + @""">Daily Stats</a>
                    <a href=""/page/synced-data"" class=""menu-item" + (page == "synced-data" ? @""" active" : @"""") + @""">Synced Data Table</a>
                </li>
                <li class=""menu-section"">
                    <span class=""menu-label"">System</span>
                    <a href=""/page/status"" class=""menu-item" + (page == "status" ? @""" active" : @"""") + @""">Node Status</a>
                    <a href=""/page/validators"" class=""menu-item" + (page == "validators" ? @""" active" : @"""") + @""">Validators</a>
                    <a href=""/page/sync"" class=""menu-item" + (page == "sync" ? @""" active" : @"""") + @""">Data Sync</a>
                </li>
            </ul>
        </nav>
        
        <div class=""main-content"">
            <header>
                <div class=""header-content"">
                    <h1>" + GetPageTitle(page) + @"</h1>
                    <div class=""header-links"">
                        <a href=""/page/about"" class=""header-link"">About</a>
                        <button class=""btn-donate"" onclick=""openDonateModal()"">Donate</button>
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
                        <div class=""metric-label"">Total Distributed Amount</div>
                        <div class=""metric-value"" id=""metric-total-distributed"">--</div>
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
                            const formatAmt = (value) => (value || 0).toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });

                            const setText = (id, value) => {
                                const el = document.getElementById(id);
                                if (el) el.textContent = value;
                            };

                            setText('metric-total-wallets', totalWallets.toLocaleString());
                            setText('metric-total-banked', formatAmt(banked));
                            setText('metric-total-staked', formatAmt(staked));
                            setText('metric-total-locked', formatAmt(locked));
                            setText('metric-total-distributed', formatAmt(distributed));
                        } catch (error) {
                            // leave placeholders on error
                        }
                    })();
                </script>

                <h2 style=""margin: 24px 0 16px; font-size: 18px; color: var(--text-primary);"">Wallet Overview</h2>
                <div class=""report-cards"">
");
        AppendWalletGrowthCard(sb);
        AppendWalletTotalsCard(sb);
        AppendDailyStatsCard(sb);
        sb.Append(@"
                </div>

                <h2 style=""margin: 24px 0 16px; font-size: 18px; color: var(--text-primary);"">Quick Access</h2>
                <div class=""report-cards"">
");
        AppendDistributionsCard(sb);
        AppendLocksCard(sb);
        AppendNetworkTotalsCard(sb);
        AppendWalletBalancesCard(sb);
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
                                        html += '<td style=""padding: 12px; text-align: right;"">' + (row.totalSupply ? row.totalSupply.toLocaleString('en-US', {minimumFractionDigits: 2, maximumFractionDigits: 2}) : '0.00') + '</td>';
                                        html += '<td style=""padding: 12px; text-align: right;"">' + (row.totalStaked ? row.totalStaked.toLocaleString('en-US', {minimumFractionDigits: 2, maximumFractionDigits: 2}) : '0.00') + '</td>';
                                        html += '<td style=""padding: 12px; text-align: right;"">' + (row.totalUnbonding ? row.totalUnbonding.toLocaleString('en-US', {minimumFractionDigits: 2, maximumFractionDigits: 2}) : '0.00') + '</td>';
                                        html += '<td style=""padding: 12px; text-align: right; font-weight: 600; color: var(--accent);"">​' + (row.totalLiquidPlus ? row.totalLiquidPlus.toLocaleString('en-US', {minimumFractionDigits: 2, maximumFractionDigits: 2}) : '0.00') + '</td>';
                                        html += '<td style=""padding: 12px; text-align: right;"">' + (row.totalLocked ? row.totalLocked.toLocaleString('en-US', {minimumFractionDigits: 2, maximumFractionDigits: 2}) : '0.00') + '</td>';
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
                                        const width = 760;
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
<svg viewBox=""0 0 ${width} ${height}"" width=""100%"" height=""100%"" preserveAspectRatio=""none"">
  <defs>
    <linearGradient id=""activeWalletsFill"" x1=""0"" x2=""0"" y1=""0"" y2=""1"">
      <stop offset=""0%"" stop-color=""rgba(16, 185, 129, 0.35)"" />
      <stop offset=""100%"" stop-color=""rgba(16, 185, 129, 0.02)"" />
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
                                        tooltip.style.background = 'rgba(15, 23, 42, 0.92)';
                                        tooltip.style.border = '1px solid var(--border)';
                                        tooltip.style.borderRadius = '6px';
                                        tooltip.style.padding = '6px 8px';
                                        tooltip.style.fontSize = '11px';
                                        tooltip.style.color = 'var(--text-primary)';
                                        tooltip.style.opacity = '0';
                                        tooltip.style.transform = 'translate(-50%, -8px)';
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
                                            hoverDot.setAttribute('stroke', '#0b1228');
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
                                        const width = 760;
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
<svg viewBox=""0 0 ${width} ${height}"" width=""100%"" height=""100%"" preserveAspectRatio=""none"">
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
                                        tooltip.style.background = 'rgba(15, 23, 42, 0.92)';
                                        tooltip.style.border = '1px solid var(--border)';
                                        tooltip.style.borderRadius = '6px';
                                        tooltip.style.padding = '6px 8px';
                                        tooltip.style.fontSize = '11px';
                                        tooltip.style.color = 'var(--text-primary)';
                                        tooltip.style.opacity = '0';
                                        tooltip.style.transform = 'translate(-50%, -8px)';
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
                                            hoverDot.setAttribute('stroke', '#0b1228');
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
                                        const formatAmt = (value) => (value || 0).toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });

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
                                                <label>Total Distributed Amount (Banked + Staked)</label>
                                                <div class=""total-value"">${formatAmt(distributed)}</div>
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
            --bg-primary: #0a0e27;
            --bg-secondary: #0f1535;
            --surface: #1a2540;
            --surface-light: #243456;
            --border: #2d3e5f;
            --text-primary: #e4e6eb;
            --text-secondary: #a0a8b8;
            --accent-primary: #10b981;
            --accent-secondary: #059669;
        }

        body {
            font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, 'Helvetica Neue', Arial, sans-serif;
            background: var(--bg-primary);
            color: var(--text-primary);
            padding: 20px;
            line-height: 1.6;
        }

        .result-container {
            background: var(--bg-secondary);
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
            background: var(--bg-primary);
            padding: 2px 6px;
            border-radius: 4px;
            color: var(--accent-primary);
            font-family: 'Courier New', monospace;
        }

        .output-log {
            background: var(--bg-primary);
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
            background: var(--bg-primary);
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
                    <td class=""amount-cell"">{System.Net.WebUtility.HtmlEncode(balance)}</td>
                    <td class=""amount-cell"">{System.Net.WebUtility.HtmlEncode(liquid)}</td>
                    <td class=""amount-cell"">{System.Net.WebUtility.HtmlEncode(staked)}</td>
                    <td class=""amount-cell"">{System.Net.WebUtility.HtmlEncode(locked)}</td>
                </tr>
");
        }

        sb.Append($@"
                <tr style=""background: var(--surface-light); font-weight: 600;"">
                    <td style=""color: var(--accent-primary);"">TOTALS</td>
                    <td class=""amount-cell"">{totalBalance:N2}</td>
                    <td class=""amount-cell"">{totalLiquid:N2}</td>
                    <td class=""amount-cell"">{totalStaked:N2}</td>
                    <td class=""amount-cell"">{totalLocked:N2}</td>
                </tr>
            </tbody>
        </table>
        <p style=""color: var(--text-secondary); margin-top: 12px; font-size: 13px;"">
            Showing {balances.Count} wallet{(balances.Count != 1 ? "s" : "")} | Total Balance: {totalBalance:N2} OPT
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
                                            <th style=""padding: 8px; text-align: right; border-right: 1px solid var(--border);"">Lock 6M</th>
                                            <th style=""padding: 8px; text-align: right; border-right: 1px solid var(--border);"">Lock 12M</th>
                                            <th style=""padding: 8px; text-align: right; border-right: 1px solid var(--border);"">Lock 18M</th>
                                            <th style=""padding: 8px; text-align: right; border-right: 1px solid var(--border);"">Lock 24M</th>
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
                                    
                                    const formatNum = (val) => val === null ? '-' : val.toLocaleString('en-US', {{maximumFractionDigits: 2}});
                                    
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


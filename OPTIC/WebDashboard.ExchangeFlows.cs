using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

// Exchange flows: which wallets sent OPT to exchanges and how much came back to them.
//   Coinstore: customers deposit by sending OPT to the deposit collector (account number in the memo); withdrawals are
//   paid from the hot wallet. Transfers between those two are Coinstore's own and left out.
//   Osmosis: OPT reaches Osmosis by IBC transfer, which moves it into the channel's escrow account on Optio; OPT that
//   comes back from Osmosis is paid out of the same escrow. Every open transfer channel on Optio leads to osmosis-1.
// A withdrawal can go to a different wallet than the one that deposited, so "back" is what came back to that wallet.
public static partial class WebDashboard
{
    static object? _exchangeFlows;
    static DateTime _exchangeFlowsAt;
    static readonly SemaphoreSlim ExchangeFlowsGate = new(1, 1);

    static void MapExchangeFlows(WebApplication app)
    {
        StartSweepLoop();
        app.MapGet("/api/exchange-flows", async (HttpContext ctx) =>
        {
            try { return VJson(await GetExchangeFlowsAsync(ctx.Request.Query["refresh"] == "1", ctx.RequestAborted)); }
            catch (Exception ex) when (ex is not OperationCanceledException) { return VError("Could not read exchange flows: " + ex.Message, 502); }
        });
    }

    static async Task<object> GetExchangeFlowsAsync(bool refresh, CancellationToken ct)
    {
        await ExchangeFlowsGate.WaitAsync(ct);
        try
        {
            if (!refresh && _exchangeFlows is not null && DateTime.UtcNow - _exchangeFlowsAt < TimeSpan.FromHours(1))
                return _exchangeFlows;

            var lcd = LoadDashboardConfig().GetValueOrDefault("lcd", "http://127.0.0.1:1317");
            // IBC escrow accounts of the open transfer channels (all to Osmosis).
            var escrows = new Dictionary<string, string>(StringComparer.Ordinal);
            using (var chDoc = await LcdGetAsync(lcd, "/ibc/core/channel/v1/channels?pagination.limit=200", ct))
                if (chDoc is not null && chDoc.RootElement.TryGetProperty("channels", out var chs))
                    foreach (var ch in chs.EnumerateArray())
                    {
                        if (ch.GetProperty("port_id").GetString() != "transfer" || ch.GetProperty("state").GetString() != "STATE_OPEN") continue;
                        var id = ch.GetProperty("channel_id").GetString()!;
                        using var e = await LcdGetAsync(lcd, $"/ibc/apps/transfer/v1/channels/{id}/ports/transfer/escrow_address", ct);
                        if (e is not null && e.RootElement.TryGetProperty("escrow_address", out var ea) && ea.GetString() is { } addr) escrows[addr] = id;
                    }

            var venueAddrs = new HashSet<string>(escrows.Keys, StringComparer.Ordinal) { CoinstoreDepositCollector, CoinstoreHotWallet };
            var rows = new Dictionary<string, ExchangeFlowRow>(StringComparer.Ordinal);
            ExchangeFlowRow Row(string a) => rows.TryGetValue(a, out var r) ? r : rows[a] = new ExchangeFlowRow { Address = a };

            var truncated = new List<string>();
            async Task Read(string address, string venue)
            {
                var ledger = await GetWalletLedgerAsync(address, ct);
                if (ledger.Truncated) truncated.Add(address);
                foreach (var e in ledger.Entries)
                {
                    if (e.Category != "wallet" || venueAddrs.Contains(e.Counterparty) || ModuleAccounts.ContainsKey(e.Counterparty)) continue;
                    var r = Row(e.Counterparty);
                    if (e.Direction == "in")
                    {
                        // The wallet sent OPT to the exchange.
                        if (venue == "coinstore") { r.CoinstoreSent += e.AmountOpt; if (e.Memo.Trim().Length > 0) r.CoinstoreAccounts.Add(e.Memo.Trim()); }
                        else r.OsmosisSent += e.AmountOpt;
                        r.Deposits++;
                        if (r.FirstSent is null || e.TimeUtc < r.FirstSent) r.FirstSent = e.TimeUtc;
                        if (r.LastSent is null || e.TimeUtc > r.LastSent) r.LastSent = e.TimeUtc;
                    }
                    else
                    {
                        if (venue == "coinstore") r.CoinstoreBack += e.AmountOpt; else r.OsmosisBack += e.AmountOpt;
                        r.Returns++;
                    }
                }
            }
            await Read(CoinstoreDepositCollector, "coinstore");
            await Read(CoinstoreHotWallet, "coinstore");
            foreach (var esc in escrows.Keys) await Read(esc, "osmosis");

            // Likely MEXC: wallets that paid into the per-customer deposit addresses swept to the collector.
            var sweeps = await GetSweepsAsync(refresh: false, ct);
            using (var rpc = CreateRpcContext())
                await ResolveBlockTimesAsync(rpc.Http, sweeps.Customers.Values.SelectMany(c => new[] { c.FirstH, c.LastH }).Distinct(), ct);
            DateTime? At(long h) => _blockTimeCache.TryGetValue(h, out var t) ? t : null;
            foreach (var (addr, c) in sweeps.Customers)
            {
                if (venueAddrs.Contains(addr)) continue;
                var r = Row(addr);
                r.MexcSent += c.Sent; r.MexcBack += sweeps.Back.GetValueOrDefault(addr); r.Deposits += c.Deposits;
                if (At(c.FirstH) is { } f && (r.FirstSent is null || f < r.FirstSent)) r.FirstSent = f;
                if (At(c.LastH) is { } l && (r.LastSent is null || l > r.LastSent)) r.LastSent = l;
            }

            var list = rows.Values.Where(r => r.CoinstoreSent + r.OsmosisSent + r.MexcSent > 0)
                .OrderByDescending(r => r.CoinstoreSent + r.OsmosisSent + r.MexcSent - r.CoinstoreBack - r.OsmosisBack - r.MexcBack)
                .Select(r => new
                {
                    address = r.Address,
                    coinstoreSent = r.CoinstoreSent, coinstoreBack = r.CoinstoreBack,
                    osmosisSent = r.OsmosisSent, osmosisBack = r.OsmosisBack,
                    mexcSent = r.MexcSent, mexcBack = r.MexcBack,
                    sent = r.CoinstoreSent + r.OsmosisSent + r.MexcSent, back = r.CoinstoreBack + r.OsmosisBack + r.MexcBack,
                    net = r.CoinstoreSent + r.OsmosisSent + r.MexcSent - r.CoinstoreBack - r.OsmosisBack - r.MexcBack,
                    deposits = r.Deposits, returns = r.Returns,
                    coinstoreAccounts = r.CoinstoreAccounts.Count,
                    firstSent = r.FirstSent, lastSent = r.LastSent,
                })
                .ToList();

            _exchangeFlows = new
            {
                asOfUtc = DateTime.UtcNow,
                venues = new
                {
                    coinstore = new { depositCollector = CoinstoreDepositCollector, hotWallet = CoinstoreHotWallet },
                    mexc = new { collectors = MexcCollectors, depositAddresses = sweeps.DepositAddresses.Count, customers = sweeps.Customers.Count, scannedToHeight = sweeps.ScannedToHeight, detectedUtc = sweeps.AtUtc },
                    osmosis = escrows.Select(kv => new { channel = kv.Value, escrow = kv.Key }).OrderBy(x => x.channel, StringComparer.Ordinal).ToList(),
                },
                totals = new
                {
                    wallets = list.Count,
                    coinstoreSent = list.Sum(x => x.coinstoreSent), coinstoreBack = list.Sum(x => x.coinstoreBack),
                    osmosisSent = list.Sum(x => x.osmosisSent), osmosisBack = list.Sum(x => x.osmosisBack),
                    mexcSent = list.Sum(x => x.mexcSent), mexcBack = list.Sum(x => x.mexcBack),
                    // Returned to wallets that never sent anything to an exchange (not in the list).
                    backToOthers = rows.Values.Where(r => r.CoinstoreSent + r.OsmosisSent + r.MexcSent == 0).Sum(r => r.CoinstoreBack + r.OsmosisBack),
                },
                truncated,
                wallets = list,
            };
            _exchangeFlowsAt = DateTime.UtcNow;
            return _exchangeFlows;
        }
        finally { ExchangeFlowsGate.Release(); }
    }

    sealed class ExchangeFlowRow
    {
        public string Address { get; set; } = "";
        public decimal CoinstoreSent, CoinstoreBack, OsmosisSent, OsmosisBack, MexcSent, MexcBack;
        public int Deposits, Returns;
        public HashSet<string> CoinstoreAccounts { get; } = new(StringComparer.Ordinal);
        public DateTime? FirstSent, LastSent;
    }
}

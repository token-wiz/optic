using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

// Chains > TITN "Next payout": the USDC waiting in THORWallet's fee splitter before its next distribute(), the signed-in
// user's estimated cut of it (stakers' percentage × their share of all staked TITN), what is ready to harvest now, and
// the recent payouts. The splitter only pays out when distribute() is called; until then the USDC sits in it.
public static partial class WebDashboard
{
    static (DateTime At, List<TitnPayout> List)? TitnPayoutsCache;
    static readonly SemaphoreSlim TitnPayoutsGate = new(1, 1);

    sealed record TitnPayout(DateTime Time, string Tx, decimal Total, decimal Stakers);

    // The splitter's recent payouts (its outgoing USDC grouped by transaction), at most 2 minutes old.
    static async Task<List<TitnPayout>> TitnRecentPayoutsAsync(CancellationToken ct)
    {
        if (TitnPayoutsCache is { } c && DateTime.UtcNow - c.At < TimeSpan.FromMinutes(2)) return c.List;
        await TitnPayoutsGate.WaitAsync(ct);
        try
        {
            if (TitnPayoutsCache is { } c2 && DateTime.UtcNow - c2.At < TimeSpan.FromMinutes(2)) return c2.List;
            var rows = new List<(DateTime Time, string Tx, string To, decimal Amount)>();
            var basePath = $"https://base.blockscout.com/api/v2/addresses/{TitnFeeSplitter}/token-transfers?type=ERC-20&filter=from&token={BaseUsdc}";
            var url = basePath;
            for (var page = 0; page < 3; page++)
            {
                using var doc = JsonDocument.Parse(await EvmHttp.GetStringAsync(url, ct));
                foreach (var x in doc.RootElement.GetProperty("items").EnumerateArray())
                    rows.Add((ParseUtc(x.GetProperty("timestamp").GetString()!), x.GetProperty("transaction_hash").GetString()!.ToLowerInvariant(),
                        x.GetProperty("to").GetProperty("hash").GetString()!.ToLowerInvariant(),
                        decimal.Parse(x.GetProperty("total").GetProperty("value").GetString()!, CultureInfo.InvariantCulture) / 1_000_000m));
                if (!doc.RootElement.TryGetProperty("next_page_params", out var next) || next.ValueKind != JsonValueKind.Object) break;
                url = basePath + "&" + string.Join("&", next.EnumerateObject().Select(p => p.Name + "=" + Uri.EscapeDataString(p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText())));
            }
            var list = rows.GroupBy(r => r.Tx)
                .Select(g => new TitnPayout(g.Min(r => r.Time), g.Key, Math.Round(g.Sum(r => r.Amount), 2), Math.Round(g.Where(r => r.To == TitnStakingContract).Sum(r => r.Amount), 2)))
                .OrderByDescending(p => p.Time).ToList();
            TitnPayoutsCache = (DateTime.UtcNow, list);
            return list;
        }
        finally { TitnPayoutsGate.Release(); }
    }

    static void MapTitnNextPayout(WebApplication app)
    {
        app.MapGet("/api/titn/next-payout", async (HttpContext ctx) =>
        {
            var ct = ctx.RequestAborted;
            try
            {
                var waiting = Scale(await TitnCallAsync("base", BaseUsdc, "0x70a08231" + Arg(TitnFeeSplitter), ct), 6);
                var totalStaked = Scale(await TitnCallAsync("base", TitnStakingContract, "0x1c2a1484", ct), 18); // internalTitnBalance()
                // Stakers' percentage as the splitter has it now (from the watch cache), else the known 50%.
                var pct = 50;
                if (LoadTitnWatch()?.Live.GetValueOrDefault("split")?["rows"] is System.Text.Json.Nodes.JsonArray rows
                    && rows.OfType<System.Text.Json.Nodes.JsonObject>().FirstOrDefault(r => r["to"]?.ToString() == TitnStakingContract)?["pct"] is { } p)
                    pct = p.GetValue<int>();
                decimal mine = 0, harvest = 0;
                foreach (var w in UserEvmWallets(ctx))
                {
                    mine += Scale(await TitnCallAsync("base", TitnStakingContract, "0xf2801fe7" + Arg(w.Address) + Arg(BaseUsdc), ct), 18);
                    harvest += Scale(await TitnCallAsync("base", TitnStakingContract, "0x9ced7e76" + Arg(w.Address) + Arg(BaseUsdc), ct), 6);
                }
                var share = totalStaked > 0 ? mine / totalStaked : 0m;
                var raffle = Scale(await TitnCallAsync("base", TitnRafflePayout, "0xf6698d1c", ct), 6); // getAvailableUsdc()
                List<TitnPayout> payouts = new();
                try { payouts = await TitnRecentPayoutsAsync(ct); } catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { }
                var gaps = payouts.Zip(payouts.Skip(1), (a, b) => (a.Time - b.Time).TotalHours).Where(h => h > 0).OrderBy(h => h).ToList();
                return VJson(new
                {
                    splitter = TitnFeeSplitter,
                    waiting = Math.Round(waiting, 2),
                    stakersPct = pct,
                    toStakers = Math.Round(waiting * pct / 100m, 2),
                    totalStaked = Math.Round(totalStaked, 0),
                    myStaked = Math.Round(mine, 0),
                    share,
                    myCut = Math.Round(waiting * pct / 100m * share, 4),
                    harvest = Math.Round(harvest, 2),
                    raffleAvailable = Math.Round(raffle, 2),
                    medianGapHours = gaps.Count > 0 ? Math.Round(gaps[gaps.Count / 2], 1) : (double?)null,
                    payouts = payouts.Take(30).Select(x => new { time = x.Time, tx = x.Tx, total = x.Total, stakers = x.Stakers, mine = Math.Round(x.Stakers * share, 4) }),
                    updatedUtc = DateTime.UtcNow,
                });
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                return VError("Could not read the fee splitter: " + ex.Message, 502);
            }
        });
    }
}

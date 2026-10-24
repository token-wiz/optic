namespace OPTIC.Services.Valuation;

public sealed record CandidateMarket(ISellMarket Market, AccessDecision Access, string? SnapshotProblem);

public sealed class AnalysisSettings
{
    public decimal[] Thresholds { get; init; } = { 0.01m, 0.05m, 0.10m, 0.25m };
    public int Increments { get; init; } = 200;
    /// <summary>Estimated gas, bridge and withdrawal costs in USD. Shown separately; never subtracted from receipt value.</summary>
    public decimal? TransactionCostsUsd { get; init; }
    public string? TransactionCostsBasis { get; init; }
}

/// <summary>
/// Receipt-level analysis against the taxpayer-accessible markets at the receipt time. Each result is an
/// independent counterfactual: the markets are cloned, so results for different receipts must not be summed as proceeds.
/// </summary>
public static class ReceiptAnalyzer
{
    public static ReceiptAnalysis Analyze(RewardReceipt receipt, IReadOnlyList<CandidateMarket> candidates, IReadOnlyList<QuoteConversion> conversions, AnalysisSettings settings)
    {
        var qty = receipt.QuantityOpt;
        var a = new ReceiptAnalysis
        {
            ReceiptId = receipt.Id,
            RequestedOpt = qty,
            ReferenceValueUsd = receipt.Reference.ValueUsd ?? ValuationMath.ReferenceValueUsd(qty, receipt.Reference.UnitPriceUsd),
            IndependentCounterfactual = true,
        };
        a.Assumptions.Add("Independent counterfactual: this receipt sold alone into the market as it stood; not additive across receipts.");

        foreach (var c in candidates)
        {
            if (c.SnapshotProblem != null) a.Notes.Add($"{c.Market.Id}: {c.SnapshotProblem}");
            foreach (var r in c.Access.Reasons) a.Notes.Add($"{c.Access.Venue}: {r}");
        }

        var usable = candidates.Where(c => c.SnapshotProblem == null && c.Access.TaxpayerAccessible).ToList();
        if (usable.Count == 0)
        {
            a.ExecutableOpt = 0;
            a.ResidualOpt = qty;
            a.Outcome = candidates.Count == 0 ? AnalysisOutcome.InsufficientData
                : candidates.Any(c => c.Access.Blocking == AnalysisOutcome.VerifiedVenueUnavailable) && candidates.All(c => c.Access.Blocking is not null)
                    ? (candidates.All(c => c.Access.Blocking == AnalysisOutcome.VerifiedVenueUnavailable) ? AnalysisOutcome.VerifiedVenueUnavailable : AnalysisOutcome.InsufficientData)
                : AnalysisOutcome.InsufficientData;
            a.Notes.Add("No contemporaneous, taxpayer-accessible market state; unknown is not zero.");
            return a;
        }

        // Each quote asset is simulated on its own; mixing USDC and USDT output is only done after documented USD conversion.
        var groups = usable.GroupBy(c => c.Market.QuoteAsset).ToList();
        ExecutionEstimate? best = null;
        decimal? bestUsd = null;
        string? bestUsdMissing = null;
        foreach (var g in groups)
        {
            var split = MarketSplitter.Sell(g.Select(c => c.Market), qty, settings.Increments);
            foreach (var x in split.Excluded) a.Notes.Add(x);
            var (usd, missing) = ValuationMath.ToUsd(split.Combined.OutputQuote, g.Key, DateOnly.FromDateTime(receipt.TimestampUtc), conversions);
            var better = best is null
                || split.Combined.ExecutableOpt > best.ExecutableOpt
                || (split.Combined.ExecutableOpt == best.ExecutableOpt && usd is not null && (bestUsd is null || usd > bestUsd));
            if (better) { best = split.Combined; bestUsd = usd; bestUsdMissing = missing; }
        }
        if (groups.Count > 1)
            a.Notes.Add("Markets with different quote assets were simulated separately; the result with the most coverage is shown.");

        a.Outcome = best!.Outcome;
        a.ExecutableOpt = best.ExecutableOpt;
        a.ResidualOpt = qty - best.ExecutableOpt;
        a.ModeledOutputQuote = best.OutputQuote;
        a.QuoteAsset = best.QuoteAsset;
        a.ModeledOutputUsd = bestUsd;
        a.ModeledOutputBeforeFeesQuote = best.OutputBeforeFeesQuote;
        a.ModeledOutputBeforeFeesUsd = ValuationMath.ToUsd(best.OutputBeforeFeesQuote, best.QuoteAsset, DateOnly.FromDateTime(receipt.TimestampUtc), conversions).Usd;
        a.UsdIncomplete = bestUsd is null && best.OutputQuote is not null;
        if (bestUsdMissing != null && a.UsdIncomplete) a.Notes.Add(bestUsdMissing);
        a.ModeledFillRatio = ValuationMath.FillRatio(qty, best.ExecutableOpt);
        a.EffectiveExecutionPriceUsd = ValuationMath.EffectiveExecutionPrice(bestUsd, best.ExecutableOpt);
        var shortfall = ValuationMath.ExecutionShortfall(qty, best.ExecutableOpt, bestUsd, a.ReferenceValueUsd);
        a.ExecutionShortfall = shortfall.Value;
        if (shortfall.MissingReason != null) a.Notes.Add("Execution shortfall not computed: " + shortfall.MissingReason);
        a.SnapshotIds = best.SnapshotIds;
        a.Routes = best.Routes;
        a.Assumptions.AddRange(best.Assumptions);
        a.Notes.AddRange(best.Notes);
        if (best.Approximation) a.Assumptions.Add("Includes approximations (see routes); validate against observed swaps before relying on it.");

        if (settings.TransactionCostsUsd is not null)
        {
            a.EstimatedTransactionCostsUsd = settings.TransactionCostsUsd;
            a.EstimatedNetWalletProceedsUsd = bestUsd is null ? null : bestUsd - settings.TransactionCostsUsd;
            a.Assumptions.Add($"Transaction costs {settings.TransactionCostsUsd:0.##} USD ({settings.TransactionCostsBasis ?? "user estimate"}), shown separately and not subtracted from receipt-time value.");
        }

        var bestGroup = groups.First(g => g.Key == best.QuoteAsset).Select(c => c.Market).ToList();
        foreach (var t in settings.Thresholds)
            a.Capacity.Add(MarketSplitter.CapacityAt(bestGroup, t, Math.Max(qty * 10, 1m), Math.Max(20, settings.Increments / 4)));
        return a;
    }
}

public sealed class ReplenishmentAssumption
{
    /// <summary>Fraction of consumed liquidity restored per day (0 = none). Requires a stated source.</summary>
    public decimal DailyFraction { get; init; }
    public string Source { get; init; } = "";
}

public sealed class StressDay
{
    public DateOnly Day { get; init; }
    public decimal RequestedOpt { get; set; }
    public decimal ExecutedOpt { get; set; }
    public decimal OutputQuote { get; set; }
    public decimal CarriedInventoryOpt { get; set; }
    public string StateSource { get; set; } = "";
    public List<string> Notes { get; } = new();
}

public sealed class StressResult
{
    public string Label { get; init; } = "Cumulative stress view (simulation state carried between sales; separate from per-receipt estimates).";
    public List<StressDay> Days { get; } = new();
    public decimal TotalExecutedOpt => Days.Sum(d => d.ExecutedOpt);
    public decimal TotalOutputQuote => Days.Sum(d => d.OutputQuote);
    public List<string> Assumptions { get; } = new();
    public bool Complete { get; set; } = true;
}

/// <summary>
/// Daily cumulative stress: sales consume liquidity from one evolving state. A new contemporaneous snapshot replaces
/// the state only when one exists for that day; otherwise the depleted state carries forward, adjusted only by a
/// stated replenishment assumption. Network-selling scenarios run in their own state via <see cref="Run"/> calls with
/// separate market clones.
/// </summary>
public static class StressSimulator
{
    public static StressResult Run(
        IReadOnlyList<(DateOnly Day, decimal Opt)> salesByDay,
        Func<DateOnly, IReadOnlyList<ISellMarket>?> freshSnapshotForDay,
        ReplenishmentAssumption? replenishment,
        bool carryUnsold,
        int increments = 100,
        decimal? dailyCapOpt = null)
    {
        var result = new StressResult();
        if (replenishment is { DailyFraction: > 0 } && string.IsNullOrWhiteSpace(replenishment.Source))
            throw new ArgumentException("A replenishment assumption needs a stated source.");
        result.Assumptions.Add(replenishment is { DailyFraction: > 0 }
            ? $"Consumed liquidity restored at {replenishment.DailyFraction:P1} a day ({replenishment.Source})."
            : "No replenishment: consumed liquidity is not restored unless a new contemporaneous snapshot exists.");
        result.Assumptions.Add(carryUnsold ? "Unsold quantity carries to the next day." : "Unsold quantity is not carried forward.");

        List<ISellMarket>? state = null, baseline = null;
        DateOnly? stateFrom = null;
        decimal carried = 0;
        foreach (var (day, opt) in salesByDay.OrderBy(s => s.Day))
        {
            var d = new StressDay { Day = day, RequestedOpt = opt + carried };
            var fresh = freshSnapshotForDay(day);
            if (fresh is { Count: > 0 })
            {
                state = fresh.Select(m => m.Clone()).ToList();
                baseline = fresh.Select(m => m.Clone()).ToList();
                stateFrom = day;
                d.StateSource = "contemporaneous snapshot for this day";
            }
            else if (state is not null)
            {
                d.StateSource = $"carried from {stateFrom:yyyy-MM-dd} after earlier simulated sales";
                if (replenishment is { DailyFraction: > 0 } && baseline is not null)
                    d.Notes.Add("Replenishment is applied as an assumption; it is not observed.");
            }
            else
            {
                d.StateSource = "none";
                d.Notes.Add("No market state yet; nothing simulated (unknown, not zero).");
                result.Complete = false;
                carried = carryUnsold ? d.RequestedOpt : 0;
                d.CarriedInventoryOpt = carried;
                result.Days.Add(d);
                continue;
            }

            if (replenishment is { DailyFraction: > 0 } && baseline is not null && fresh is not { Count: > 0 })
                state = Replenish(state!, baseline, replenishment.DailyFraction);

            var offered = dailyCapOpt is null ? d.RequestedOpt : Math.Min(d.RequestedOpt, dailyCapOpt.Value);
            if (offered < d.RequestedOpt) d.Notes.Add($"Limited to the assumed buyer flow of {dailyCapOpt:G8} OPT.");
            var r = MarketSplitter.Sell(state!, offered, increments, mutate: true);
            d.ExecutedOpt = r.Combined.ExecutableOpt;
            d.OutputQuote = r.Combined.OutputQuote ?? 0;
            carried = carryUnsold ? d.RequestedOpt - d.ExecutedOpt : 0;
            d.CarriedInventoryOpt = carried;
            if (d.ExecutedOpt < d.RequestedOpt) d.Notes.Add($"{d.RequestedOpt - d.ExecutedOpt:G8} OPT not absorbed.");
            result.Days.Add(d);
        }
        return result;
    }

    // Only order books can be replenished generically (restore a fraction of the gap to the baseline per level);
    // pool states are left as they are and flagged.
    private static List<ISellMarket> Replenish(List<ISellMarket> state, List<ISellMarket> baseline, decimal fraction)
    {
        var output = new List<ISellMarket>();
        foreach (var m in state)
        {
            if (m is OrderBookMarket ob && baseline.OfType<OrderBookMarket>().FirstOrDefault(b => b.Id == m.Id) is { } bb)
            {
                var current = ob.Snapshot.Bids.ToDictionary(b => b.Price, b => b.Quantity);
                var levels = bb.Snapshot.Bids.Select(b =>
                {
                    var cur = current.GetValueOrDefault(b.Price);
                    return b with { Quantity = cur + (b.Quantity - cur) * fraction };
                }).Where(b => b.Quantity > 0).ToList();
                var snap = ob.Snapshot;
                output.Add(new OrderBookMarket(new OrderBookSnapshot
                {
                    Id = snap.Id, Venue = snap.Venue, Market = snap.Market, BaseAsset = snap.BaseAsset, QuoteAsset = snap.QuoteAsset,
                    ObservedUtc = snap.ObservedUtc, CollectedUtc = snap.CollectedUtc, Bids = levels, Truncated = snap.Truncated, TakerFeeRate = snap.TakerFeeRate,
                }));
            }
            else output.Add(m);
        }
        return output;
    }
}

public sealed class MarketingHorizonAssumptions
{
    public int Days { get; init; }
    public decimal InventoryOpt { get; init; }
    public decimal? BuyerFlowOptPerDay { get; init; }
    public string? BuyerFlowSource { get; init; }
    public ReplenishmentAssumption? Replenishment { get; init; }
    public string? RouteAssumption { get; init; }

    public List<string> Missing()
    {
        var m = new List<string>();
        if (Days <= 0) m.Add("horizon length");
        if (InventoryOpt <= 0) m.Add("inventory to sell");
        if (BuyerFlowOptPerDay is null || string.IsNullOrWhiteSpace(BuyerFlowSource)) m.Add("buyer flow per day with its source");
        if (Replenishment is null || (Replenishment.DailyFraction > 0 && string.IsNullOrWhiteSpace(Replenishment.Source))) m.Add("replenishment assumption with its source");
        if (string.IsNullOrWhiteSpace(RouteAssumption)) m.Add("route assumption");
        return m;
    }
}

public static class MarketingHorizon
{
    /// <summary>
    /// Multi-day sale capacity. Refuses to run without explicit inventory, buyer-flow, replenishment and route
    /// assumptions, and never treats one static snapshot as several days of capacity.
    /// </summary>
    public static (AnalysisOutcome Outcome, StressResult? Result, List<string> Notes) Run(
        MarketingHorizonAssumptions a, IReadOnlyList<ISellMarket> startState, DateOnly start)
    {
        var missing = a.Missing();
        if (missing.Count > 0)
            return (AnalysisOutcome.InsufficientData, null, new() { "Marketing-horizon analysis needs: " + string.Join(", ", missing) + "." });
        var perDay = a.InventoryOpt / a.Days;
        var sales = Enumerable.Range(0, a.Days).Select(i => (start.AddDays(i), perDay)).ToList();
        var r = StressSimulator.Run(sales, d => d == start ? startState : null, a.Replenishment, carryUnsold: true, dailyCapOpt: a.BuyerFlowOptPerDay);
        var notes = new List<string>
        {
            $"Buyer flow assumed {a.BuyerFlowOptPerDay:G8} OPT/day ({a.BuyerFlowSource}); route: {a.RouteAssumption}.",
            "Only the first day uses an observed snapshot; later days use the depleted state plus the stated replenishment assumption.",
        };
        return (r.TotalExecutedOpt >= a.InventoryOpt ? AnalysisOutcome.SupportedHistoricalEstimate : AnalysisOutcome.PartialCapacity, r, notes);
    }
}

namespace OPTIC.Services.Valuation;

/// <summary>Sells into the bid side of a historical order book, highest bids first.</summary>
public static class OrderBookSimulator
{
    public static ExecutionEstimate Sell(OrderBookSnapshot book, decimal quantityOpt, decimal? maxDeterioration = null)
    {
        var est = new ExecutionEstimate
        {
            RequestedOpt = quantityOpt, QuoteAsset = book.QuoteAsset, Venue = book.Venue,
            SnapshotIds = { book.Id }, Routes = { $"{book.Venue} {book.Market} (sell into bids)" },
        };
        var bids = book.Bids.Where(b => b.Price > 0 && b.Quantity > 0).OrderByDescending(b => b.Price).ToList();
        if (bids.Count == 0)
        {
            est.Outcome = book.Truncated ? AnalysisOutcome.InsufficientDepthData : AnalysisOutcome.NoExecutableRouteWithinVerifiedCoverage;
            est.ExecutableOpt = 0;
            est.Notes.Add(book.Truncated ? "Snapshot holds no bids and is marked truncated." : "No bids in the snapshot.");
            return est;
        }

        var best = bids[0].Price;
        var floor = maxDeterioration is null ? 0m : best * (1m - maxDeterioration.Value);
        decimal left = quantityOpt, gross = 0m;
        decimal? worst = null;
        foreach (var level in bids)
        {
            if (left <= 0) break;
            if (level.Price < floor) break;
            var q = Math.Min(left, level.Quantity);
            gross += q * level.Price;
            left -= q;
            worst = level.Price;
        }

        var filled = quantityOpt - left;
        var fees = gross * book.TakerFeeRate;
        est.ExecutableOpt = filled;
        est.OutputQuote = gross - fees;
        est.FeesQuote = fees;
        est.OutputBeforeFeesQuote = gross;
        est.BestPrice = best;
        est.WorstPrice = worst;
        if (book.TakerFeeRate > 0) est.Assumptions.Add($"Taker fee {book.TakerFeeRate:P3} deducted from proceeds.");
        if (left <= 0)
        {
            est.Outcome = AnalysisOutcome.SupportedHistoricalEstimate;
        }
        else
        {
            est.Outcome = AnalysisOutcome.PartialCapacity;
            if (maxDeterioration is not null && bids.Any(b => b.Price < floor))
                est.Notes.Add($"Stopped at the {maxDeterioration:P1} price-deterioration limit.");
            else if (book.Truncated)
                est.Notes.Add("The snapshot is truncated: bids beyond the captured depth are unknown, so the residual is unmodeled, not unsellable.");
            else
                est.Notes.Add("The visible bid side was exhausted.");
        }
        return est;
    }

    /// <summary>
    /// Capacity at a threshold t: the quantity of bids priced at or above best × (1 − t). This measures price
    /// deterioration of the marginal fill relative to the best bid, before fees. Fees are reported separately.
    /// </summary>
    public static ThresholdCapacity CapacityAt(OrderBookSnapshot book, decimal threshold)
    {
        var bids = book.Bids.Where(b => b.Price > 0 && b.Quantity > 0).ToList();
        if (bids.Count == 0) return new(threshold, null, "No bids in snapshot.");
        var best = bids.Max(b => b.Price);
        var floor = best * (1m - threshold);
        var qty = bids.Where(b => b.Price >= floor).Sum(b => b.Quantity);
        var allLevels = bids.All(b => b.Price >= floor);
        return new(threshold, qty, $"Bids ≥ {floor:G6} {book.QuoteAsset} (best {best:G6}, marginal price decline ≤ {threshold:P1}, before fees)"
            + (book.Truncated && allLevels ? "; lower bound, book truncated" : ""));
    }
}

public sealed record ObservedTrade(DateTime TimeUtc, decimal Price, decimal QuantityOpt, string? Side);

public sealed class ObservedActivity
{
    public int Trades { get; init; }
    public decimal VolumeOpt { get; init; }
    public decimal? Vwap { get; init; }
    public decimal? LargestTradeOpt { get; init; }
    public decimal? SellSideVolumeOpt { get; init; }
    public AnalysisOutcome Outcome => AnalysisOutcome.InsufficientDepthData;
    public string Note => "Trades or candles show activity that happened, not depth that was available; no executable order book is inferred from them.";

    public static ObservedActivity From(IReadOnlyCollection<ObservedTrade> trades) => new()
    {
        Trades = trades.Count,
        VolumeOpt = trades.Sum(t => t.QuantityOpt),
        Vwap = trades.Sum(t => t.QuantityOpt) is var v && v > 0 ? trades.Sum(t => t.QuantityOpt * t.Price) / v : null,
        LargestTradeOpt = trades.Count == 0 ? null : trades.Max(t => t.QuantityOpt),
        SellSideVolumeOpt = trades.Any(t => t.Side != null) ? trades.Where(t => string.Equals(t.Side, "sell", StringComparison.OrdinalIgnoreCase)).Sum(t => t.QuantityOpt) : null,
    };
}

/// <summary>Which snapshot may stand for the market at a given time. Later data is never used as if it were known then.</summary>
public sealed class SnapshotPolicy
{
    public TimeSpan MaxAge { get; init; } = TimeSpan.FromHours(24);

    /// <summary>
    /// The latest snapshot observed at or before <paramref name="atUtc"/> and no older than MaxAge.
    /// Snapshots observed after the time are returned separately as later corroboration only.
    /// </summary>
    public (T? Contemporaneous, List<T> LaterCorroboration, string? Reason) Select<T>(IEnumerable<T> snapshots, Func<T, DateTime> observedUtc, DateTime atUtc)
    {
        var list = snapshots.ToList();
        var before = list.Where(s => observedUtc(s) <= atUtc).OrderByDescending(observedUtc).FirstOrDefault();
        var later = list.Where(s => observedUtc(s) > atUtc).OrderBy(observedUtc).ToList();
        if (before is null)
            return (default, later, list.Count == 0 ? "No snapshots for this market." : "No snapshot at or before this time.");
        var age = atUtc - observedUtc(before);
        if (age > MaxAge)
            return (default, later, $"Nearest earlier snapshot is {age.TotalHours:N1} h old (limit {MaxAge.TotalHours:N0} h).");
        return (before, later, null);
    }
}

public sealed class TaxpayerProfile
{
    public string? Jurisdiction { get; init; } = "US";
    public List<string> AccountsAt { get; init; } = new();  // venues where the taxpayer had a usable account
    public string? CustodyNote { get; init; }
    public TimeSpan TransferTime { get; init; } = TimeSpan.Zero;
}

public sealed record AccessDecision(string Venue, bool InEvidenceUniverse, bool TaxpayerAccessible, AnalysisOutcome? Blocking, List<string> Reasons);

/// <summary>Effective-dated venue access. Keeps the market-evidence universe and the taxpayer-accessible universe apart.</summary>
public static class VenueAccess
{
    public static VenueStatus? StatusAt(IEnumerable<VenueStatus> statuses, string venue, VenueAccessKind kind, DateTime atUtc, string? jurisdiction)
    {
        return statuses
            .Where(s => string.Equals(s.Venue, venue, StringComparison.OrdinalIgnoreCase) && s.Kind == kind)
            .Where(s => s.EffectiveFromUtc <= atUtc && (s.EffectiveToUtc is null || atUtc < s.EffectiveToUtc))
            .Where(s => s.Jurisdiction is null || string.Equals(s.Jurisdiction, jurisdiction, StringComparison.OrdinalIgnoreCase))
            // A jurisdiction-specific status is more specific than a global one.
            .OrderByDescending(s => s.Jurisdiction is not null)
            .ThenByDescending(s => s.EffectiveFromUtc)
            .FirstOrDefault();
    }

    public static AccessDecision Decide(IEnumerable<VenueStatus> statuses, string venue, DateTime atUtc, TaxpayerProfile taxpayer, bool onChainVenue)
    {
        var list = statuses.ToList();
        var reasons = new List<string>();
        AnalysisOutcome? blocking = null;
        var accessible = true;

        // Centralized exchanges need trading, deposits and jurisdiction access; an on-chain pool needs only trading
        // (deposits/withdrawals there mean the bridge or transfer path, recorded the same way).
        var kinds = onChainVenue
            ? new[] { VenueAccessKind.Trading, VenueAccessKind.Deposits }
            : new[] { VenueAccessKind.Trading, VenueAccessKind.Deposits, VenueAccessKind.JurisdictionAccess };
        foreach (var kind in kinds)
        {
            var s = StatusAt(list, venue, kind, atUtc, taxpayer.Jurisdiction);
            if (s is null)
            {
                reasons.Add($"{kind}: no status recorded for this date.");
                accessible = false;
                blocking ??= AnalysisOutcome.InsufficientData;
                continue;
            }
            var announcedOnly = !s.ImplementationVerified && s.State is not VenueAccessState.Available;
            var scope = s.Jurisdiction is null ? "" : $" ({s.Jurisdiction} accounts only)";
            if (s.State == VenueAccessState.Available)
            {
                reasons.Add($"{kind}: available{scope} [{s.Verification}].");
                continue;
            }
            accessible = false;
            if (announcedOnly)
            {
                reasons.Add($"{kind}: {s.State.ToString().ToLowerInvariant()}{scope} as announced, implementation time not verified.");
                blocking ??= AnalysisOutcome.InsufficientData;
            }
            else if (s.Verification is VerificationStatus.Verified or VerificationStatus.Observed)
            {
                reasons.Add($"{kind}: {s.State.ToString().ToLowerInvariant()}{scope}, verified.");
                blocking = AnalysisOutcome.VerifiedVenueUnavailable;
            }
            else
            {
                reasons.Add($"{kind}: {s.State.ToString().ToLowerInvariant()}{scope} [{s.Verification}], not independently verified.");
                blocking ??= AnalysisOutcome.InsufficientData;
            }
        }

        if (!onChainVenue && taxpayer.AccountsAt.Count > 0 && !taxpayer.AccountsAt.Contains(venue, StringComparer.OrdinalIgnoreCase))
        {
            accessible = false;
            reasons.Add("Taxpayer profile lists no account at this venue.");
            blocking ??= AnalysisOutcome.InsufficientData;
        }

        // The venue stays in the evidence universe whatever the taxpayer's access: a US restriction does not mean trading stopped.
        return new AccessDecision(venue, true, accessible, accessible ? null : blocking, reasons);
    }
}

namespace OPTIC.Services.Valuation;

/// <summary>
/// A market state OPT can be sold into. Quote never changes state; Execute consumes liquidity.
/// Clone gives an independent copy so separate simulations never share state.
/// </summary>
public interface ISellMarket
{
    string Id { get; }
    string Venue { get; }
    bool OnChain { get; }
    DateTime ObservedUtc { get; }
    string QuoteAsset { get; }
    IReadOnlyCollection<string> PoolIds { get; }
    ExecutionEstimate Quote(decimal quantityOpt);
    ExecutionEstimate Execute(decimal quantityOpt);
    /// <summary>Marginal price for the next small sale, before fees, in quote units per OPT; null if unknown.</summary>
    decimal? MarginalPrice();
    ISellMarket Clone();
}

public sealed class OrderBookMarket : ISellMarket
{
    private OrderBookSnapshot _book;

    public OrderBookMarket(OrderBookSnapshot book) => _book = Copy(book);

    public string Id => _book.Id;
    public string Venue => _book.Venue;
    public bool OnChain => false;
    public DateTime ObservedUtc => _book.ObservedUtc;
    public string QuoteAsset => _book.QuoteAsset;
    public IReadOnlyCollection<string> PoolIds => Array.Empty<string>();
    public OrderBookSnapshot Snapshot => _book;

    public ExecutionEstimate Quote(decimal quantityOpt) => OrderBookSimulator.Sell(_book, quantityOpt);

    public ExecutionEstimate Execute(decimal quantityOpt)
    {
        var est = OrderBookSimulator.Sell(_book, quantityOpt);
        // Consume the filled bids so a later sale in the same simulation sees the depleted book.
        var left = est.ExecutableOpt;
        var bids = _book.Bids.OrderByDescending(b => b.Price).ToList();
        var remaining = new List<BookLevel>();
        foreach (var b in bids)
        {
            if (left <= 0) { remaining.Add(b); continue; }
            var q = Math.Min(left, b.Quantity);
            left -= q;
            if (b.Quantity - q > 0) remaining.Add(b with { Quantity = b.Quantity - q });
        }
        _book.Bids = remaining;
        return est;
    }

    public decimal? MarginalPrice() => _book.Bids.Where(b => b.Quantity > 0).Select(b => (decimal?)b.Price).DefaultIfEmpty(null).Max();

    public ISellMarket Clone() => new OrderBookMarket(_book);

    private static OrderBookSnapshot Copy(OrderBookSnapshot b) => new()
    {
        Id = b.Id, Venue = b.Venue, Market = b.Market, BaseAsset = b.BaseAsset, QuoteAsset = b.QuoteAsset,
        ObservedUtc = b.ObservedUtc, CollectedUtc = b.CollectedUtc, Bids = b.Bids.ToList(), Truncated = b.Truncated,
        TakerFeeRate = b.TakerFeeRate, Evidence = b.Evidence,
    };
}

public sealed record SplitResult(ExecutionEstimate Combined, List<(string MarketId, decimal Opt, decimal Output)> Allocation, List<string> Excluded);

/// <summary>Sells across several markets, routing each increment to the best marginal output.</summary>
public static class MarketSplitter
{
    /// <summary>
    /// Greedy split in <paramref name="increments"/> equal parts. Markets that share a pool with an already
    /// selected market are excluded so the same liquidity is never counted twice. Each increment is a separate
    /// atomic trade; an increment a market cannot fill completely is not sent there.
    /// </summary>
    public static SplitResult Sell(IEnumerable<ISellMarket> markets, decimal quantityOpt, int increments = 200, bool mutate = false)
    {
        var excluded = new List<string>();
        var chosen = new List<ISellMarket>();
        var usedPools = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in markets)
        {
            if (m.PoolIds.Any(usedPools.Contains))
            {
                excluded.Add($"{m.Id}: shares pool(s) {string.Join(",", m.PoolIds.Where(usedPools.Contains))} with another route; not counted twice.");
                continue;
            }
            foreach (var p in m.PoolIds) usedPools.Add(p);
            chosen.Add(mutate ? m : m.Clone());
        }

        var quoteAssets = chosen.Select(m => m.QuoteAsset).Distinct().ToList();
        var combined = new ExecutionEstimate { RequestedOpt = quantityOpt, QuoteAsset = quoteAssets.Count == 1 ? quoteAssets[0] : string.Join("+", quoteAssets) };
        if (quoteAssets.Count > 1)
            throw new InvalidOperationException("Markets in one split must share a quote asset; convert first.");

        var alloc = chosen.ToDictionary(m => m.Id, _ => (Opt: 0m, Output: 0m));
        var step = quantityOpt / Math.Max(1, increments);
        var left = quantityOpt;
        decimal output = 0m, fees = 0m, beforeFees = 0m;
        var beforeFeesKnown = true;
        var sawFailure = false;
        while (left > 0 && step > 0)
        {
            var size = Math.Min(step, left);
            ISellMarket? best = null;
            decimal bestOut = 0m;
            foreach (var m in chosen)
            {
                var q = m.Quote(size);
                if (q.ExecutableOpt < size || q.OutputQuote is null) { sawFailure = true; continue; }
                if (q.OutputQuote > bestOut) { best = m; bestOut = q.OutputQuote.Value; }
            }
            if (best is null) break;
            var done = best.Execute(size);
            output += done.OutputQuote ?? 0;
            fees += done.FeesQuote ?? 0;
            if (done.OutputBeforeFeesQuote is null) beforeFeesKnown = false; else beforeFees += done.OutputBeforeFeesQuote.Value;
            var a = alloc[best.Id];
            alloc[best.Id] = (a.Opt + size, a.Output + (done.OutputQuote ?? 0));
            left -= size;
        }

        combined.ExecutableOpt = quantityOpt - left;
        combined.OutputQuote = combined.ExecutableOpt > 0 ? output : (chosen.Count == 0 ? null : 0m);
        combined.FeesQuote = fees;
        combined.OutputBeforeFeesQuote = combined.ExecutableOpt > 0 && beforeFeesKnown ? beforeFees : null;
        combined.SnapshotIds = chosen.Select(m => m.Id).ToList();
        combined.Routes = alloc.Where(a => a.Value.Opt > 0).Select(a => $"{a.Key}: {a.Value.Opt:G8} OPT").ToList();
        combined.Approximation = chosen.Count > 1 || chosen.Any(m => m.OnChain);
        if (combined.Approximation)
            combined.Assumptions.Add($"Sold in {increments} equal sequential trades, each routed to the market with the best output; each trade is atomic.");
        combined.Outcome = chosen.Count == 0
            ? AnalysisOutcome.NoExecutableRouteWithinVerifiedCoverage
            : left <= 0 ? AnalysisOutcome.SupportedHistoricalEstimate
            : combined.ExecutableOpt > 0 ? AnalysisOutcome.PartialCapacity
            : AnalysisOutcome.NoExecutableRouteWithinVerifiedCoverage;
        if (left > 0 && sawFailure)
            combined.Notes.Add("Remaining increments could not be filled completely by any market in coverage; a failed exact-input swap is not a partial fill.");
        return new SplitResult(combined, alloc.Select(a => (a.Key, a.Value.Opt, a.Value.Output)).ToList(), excluded);
    }

    /// <summary>
    /// Largest quantity sellable while the marginal price stays within <paramref name="threshold"/> of the starting
    /// marginal price (price impact only; fees excluded). Found by bisection on cloned state.
    /// </summary>
    public static ThresholdCapacity CapacityAt(IReadOnlyList<ISellMarket> markets, decimal threshold, decimal upperBoundOpt, int increments = 50)
    {
        var start = markets.Select(m => m.MarginalPrice()).Where(p => p is > 0).Select(p => p!.Value).DefaultIfEmpty(0).Max();
        if (start <= 0) return new(threshold, null, "No starting marginal price available.");
        var floor = start * (1 - threshold);
        decimal lo = 0, hi = upperBoundOpt;
        for (var i = 0; i < 40 && hi - lo > upperBoundOpt / 1_000_000m; i++)
        {
            var mid = (lo + hi) / 2;
            var clones = markets.Select(m => m.Clone()).ToList();
            var r = Sell(clones, mid, increments, mutate: true);
            var after = clones.Select(m => m.MarginalPrice()).Where(p => p is > 0).Select(p => p!.Value).DefaultIfEmpty(0).Max();
            if (r.Combined.ExecutableOpt >= mid && after >= floor) lo = mid; else hi = mid;
        }
        return new(threshold, lo, $"Marginal price kept ≥ {floor:G6} (start {start:G6}, decline ≤ {threshold:P1}, price impact only, fees excluded); search bound {upperBoundOpt:G8} OPT"
            + (lo >= upperBoundOpt * 0.999m ? "; reached search bound (capacity at least this)" : ""));
    }
}

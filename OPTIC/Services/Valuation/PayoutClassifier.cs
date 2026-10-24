namespace OPTIC.Services.Valuation;

public enum PayoutDayKind
{
    /// <summary>The day's payouts split into a larger group (node) and a clearly smaller group (staking).</summary>
    Split,
    /// <summary>Only one payout that day: size alone cannot say which it is.</summary>
    Single,
    /// <summary>Several payouts of about the same size: the "smaller" rule does not apply.</summary>
    EqualSizes,
}

public sealed record PayoutLabel(int Index, bool Staking, PayoutDayKind Day);

/// <summary>
/// The user's rule for distribution-account payouts: on a UTC day with more than one payout, the smaller payout(s)
/// are staking/participation rewards and the larger are node rewards. A day is split at the largest relative gap
/// between its sorted amounts, and only when the larger side of the gap is clearly larger (smaller ≤ 85% of larger);
/// days with one payout or near-equal payouts are left undecided for the user.
/// </summary>
public static class PayoutClassifier
{
    public const decimal MaxSmallerToLargerRatio = 0.85m;

    public static List<PayoutLabel> Classify(IReadOnlyList<(DateTime TimeUtc, decimal AmountOpt)> payouts)
    {
        var labels = new PayoutLabel[payouts.Count];
        foreach (var day in payouts.Select((p, i) => (p, i)).GroupBy(x => x.p.TimeUtc.Date))
        {
            var sorted = day.OrderBy(x => x.p.AmountOpt).ToList();
            if (sorted.Count < 2)
            {
                labels[sorted[0].i] = new PayoutLabel(sorted[0].i, false, PayoutDayKind.Single);
                continue;
            }
            // Largest relative gap between neighbouring amounts.
            var cut = -1;
            var bestRatio = 1m;
            for (var k = 1; k < sorted.Count; k++)
            {
                var lower = sorted[k - 1].p.AmountOpt;
                var upper = sorted[k].p.AmountOpt;
                if (upper <= 0) continue;
                var ratio = lower / upper;
                if (ratio < bestRatio) { bestRatio = ratio; cut = k; }
            }
            var split = cut > 0 && bestRatio <= MaxSmallerToLargerRatio;
            for (var k = 0; k < sorted.Count; k++)
                labels[sorted[k].i] = new PayoutLabel(sorted[k].i, split && k < cut, split ? PayoutDayKind.Split : PayoutDayKind.EqualSizes);
        }
        return labels.ToList();
    }
}

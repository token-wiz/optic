namespace OPTIC.Services.Valuation;

public sealed record SelectedValue(string ReceiptId, decimal ValueUsd, string ReviewId, int Version, ReviewMethod Method, string Reviewer, DateTime ReviewedUtc);

public sealed class ValuationCoverage
{
    public int Receipts { get; init; }
    public int Resolved { get; init; }
    public int Unresolved => Receipts - Resolved;
    public decimal KnownValueUsd { get; init; }
    public decimal UnvaluedOpt { get; init; }
    public decimal ResolvedOpt { get; init; }
    public bool Complete => Unresolved == 0;
    public string ReportStatus => Complete ? "Final (all receipts have an approved value)" : "Draft / incomplete: unresolved receipt values";
}

/// <summary>
/// Review rules. Reviews are append-only: each change is a new version of its review lineage; nothing is edited in
/// place. A proposed value has no effect on tax figures until a person approves it. Approval here is the
/// user's own decision, not IRS approval.
/// </summary>
public static class ValuationReviews
{
    public const int MinZeroExplanationLength = 40;

    public static List<string> Validate(ValuationReview r, IReadOnlyDictionary<string, RewardReceipt> receipts)
    {
        var errors = new List<string>();
        if (r.ReceiptIds.Count == 0) errors.Add("Select at least one receipt.");
        var missing = r.ReceiptIds.Where(id => !receipts.ContainsKey(id)).ToList();
        if (missing.Count > 0) errors.Add($"Unknown receipt id(s): {string.Join(", ", missing.Take(5))}.");
        if (r.ReceiptIds.Distinct().Count() != r.ReceiptIds.Count) errors.Add("A receipt is listed twice.");
        if (string.IsNullOrWhiteSpace(r.Reviewer)) errors.Add("Reviewer is required.");
        if (string.IsNullOrWhiteSpace(r.Explanation)) errors.Add("Explain the selected value.");
        if (r.SelectedValueUsd < 0) errors.Add("Selected value cannot be negative.");
        if (r.ReceiptIds.Count > 1)
        {
            if (string.IsNullOrWhiteSpace(r.GroupJustification)) errors.Add("A group review needs a justification for grouping these receipts.");
            if (r.AllocationMethod is not ("pro-rata-quantity" or "per-receipt")) errors.Add("A group review needs an allocation method: pro-rata-quantity or per-receipt.");
        }
        if (r.AllocationMethod == "per-receipt")
        {
            if (r.PerReceiptUsd is null || r.ReceiptIds.Any(id => !r.PerReceiptUsd.ContainsKey(id)))
                errors.Add("Per-receipt allocation needs a value for every receipt.");
            else if (r.PerReceiptUsd.Values.Any(v => v < 0))
                errors.Add("Per-receipt values cannot be negative.");
            else if (Math.Abs(r.PerReceiptUsd.Values.Sum() - r.SelectedValueUsd) > 0.005m)
                errors.Add("Per-receipt values must add up to the selected value.");
        }
        if (r.Method == ReviewMethod.AcceptReference && missing.Count == 0 && r.ReceiptIds.Count > 0)
        {
            var refs = r.ReceiptIds.Select(id => receipts[id].Reference.ValueUsd).ToList();
            if (refs.Any(v => v is null)) errors.Add("Accepting the reference value needs a reference value for every receipt.");
            else if (Math.Abs(refs.Sum(v => v!.Value) - r.SelectedValueUsd) > 0.01m * r.ReceiptIds.Count) errors.Add("Accepting the reference value: the selected value must equal the reference value.");
        }
        if (r.SelectedValueUsd == 0 && r.ReceiptIds.Count > 0)
        {
            if (!r.Evidence.Any(e => !e.UserReported))
                errors.Add("A zero value needs supporting evidence beyond the user's own statement.");
            if ((r.Explanation ?? "").Trim().Length < MinZeroExplanationLength)
                errors.Add($"A zero value needs a substantive explanation (at least {MinZeroExplanationLength} characters).");
        }
        return errors;
    }

    /// <summary>Current state of every lineage (the latest version of each review id).</summary>
    public static List<ValuationReview> Current(IEnumerable<ValuationReview> versions) =>
        versions.GroupBy(v => v.ReviewId).Select(g => g.OrderByDescending(v => v.Version).First()).ToList();

    /// <summary>
    /// The approved value per receipt: from approved lineages, the most recent approval wins. Receipts without one
    /// have no selected value (unresolved), which is never treated as zero.
    /// </summary>
    public static Dictionary<string, SelectedValue> Selected(IEnumerable<ValuationReview> versions, IReadOnlyDictionary<string, RewardReceipt> receipts)
    {
        var result = new Dictionary<string, SelectedValue>(StringComparer.Ordinal);
        foreach (var r in Current(versions).Where(v => v.Status == ReviewStatus.Approved).OrderBy(v => v.ReviewedUtc).ThenBy(v => v.Version))
        {
            foreach (var (id, usd) in Allocate(r, receipts))
                result[id] = new SelectedValue(id, usd, r.ReviewId, r.Version, r.Method, r.Reviewer, r.ReviewedUtc);
        }
        return result;
    }

    /// <summary>Splits a group value among its receipts. Pro-rata by quantity in exact decimals; the last receipt takes the rounding remainder.</summary>
    public static List<(string Id, decimal Usd)> Allocate(ValuationReview r, IReadOnlyDictionary<string, RewardReceipt> receipts)
    {
        var ids = r.ReceiptIds.Where(receipts.ContainsKey).ToList();
        if (ids.Count == 0) return new();
        if (ids.Count == 1) return new() { (ids[0], r.SelectedValueUsd) };
        if (r.AllocationMethod == "per-receipt" && r.PerReceiptUsd != null)
            return ids.Select(id => (id, r.PerReceiptUsd.GetValueOrDefault(id))).ToList();
        var total = ids.Sum(id => receipts[id].QuantityOpt);
        var list = new List<(string, decimal)>();
        decimal used = 0;
        for (var i = 0; i < ids.Count; i++)
        {
            var share = i == ids.Count - 1 ? r.SelectedValueUsd - used : Math.Round(r.SelectedValueUsd * receipts[ids[i]].QuantityOpt / total, 8);
            used += share;
            list.Add((ids[i], share));
        }
        return list;
    }

    public static ValuationCoverage Coverage(IReadOnlyCollection<RewardReceipt> receipts, IReadOnlyDictionary<string, SelectedValue> selected) => new()
    {
        Receipts = receipts.Count,
        Resolved = receipts.Count(r => selected.ContainsKey(r.Id)),
        KnownValueUsd = receipts.Where(r => selected.ContainsKey(r.Id)).Sum(r => selected[r.Id].ValueUsd),
        ResolvedOpt = receipts.Where(r => selected.ContainsKey(r.Id)).Sum(r => r.QuantityOpt),
        UnvaluedOpt = receipts.Where(r => !selected.ContainsKey(r.Id)).Sum(r => r.QuantityOpt),
    };

    /// <summary>Builds the next version of a lineage. Approving requires an explicit call; proposing never changes tax values.</summary>
    public static ValuationReview NextVersion(IEnumerable<ValuationReview> existing, ValuationReview draft, ReviewStatus status, string reviewer, DateTime nowUtc)
    {
        var lineage = existing.Where(v => v.ReviewId == draft.ReviewId).OrderByDescending(v => v.Version).FirstOrDefault();
        return new ValuationReview
        {
            ReviewId = string.IsNullOrEmpty(draft.ReviewId) ? Guid.NewGuid().ToString("N") : draft.ReviewId,
            Version = (lineage?.Version ?? 0) + 1,
            SupersedesVersion = lineage?.Version,
            ReceiptIds = draft.ReceiptIds.ToList(),
            GroupJustification = draft.GroupJustification,
            AllocationMethod = draft.ReceiptIds.Count == 1 ? "single" : draft.AllocationMethod,
            SelectedValueUsd = draft.SelectedValueUsd,
            PerReceiptUsd = draft.PerReceiptUsd is null ? null : new Dictionary<string, decimal>(draft.PerReceiptUsd),
            Method = draft.Method,
            Explanation = draft.Explanation,
            Evidence = draft.Evidence.ToList(),
            Reviewer = reviewer,
            ReviewedUtc = nowUtc,
            Status = status,
            ReferenceValueUsdAtReview = draft.ReferenceValueUsdAtReview,
            Source = draft.Source,
        };
    }
}

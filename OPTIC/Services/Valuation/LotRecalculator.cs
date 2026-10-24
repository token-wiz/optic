namespace OPTIC.Services.Valuation;

public enum LotEventKind
{
    RewardReceipt,
    Acquisition,
    Disposal,
    CarryOut,
    Fee,
    Unresolved,
    /// <summary>Sent to another of the user's own wallets: the lots taken move to that wallet unchanged.</summary>
    TransferOut,
    /// <summary>Received from another of the user's own wallets: takes over the lots sent under the same TransferKey.</summary>
    TransferIn,
}

/// <summary>One wallet event in time order. Values that are unknown stay null.</summary>
public sealed class LotEvent
{
    public string Id { get; init; } = "";
    public string Wallet { get; init; } = "";
    public DateTime TimeUtc { get; init; }
    public LotEventKind Kind { get; init; }
    public decimal QuantityOpt { get; init; }
    public decimal? ValueUsd { get; init; }      // income value (receipts), cost (acquisitions) or proceeds (disposals)
    public DateTime? DisposedUtc { get; init; }  // sale date when later than the transfer
    public RewardCategory? Category { get; init; }
    public string? Note { get; init; }
    /// <summary>Links a TransferOut to its TransferIn (e.g. tx hash + receiving wallet).</summary>
    public string? TransferKey { get; init; }
}

public sealed class Lot
{
    public string SourceId { get; init; } = "";
    public DateTime? AcquiredUtc { get; init; }
    public decimal QuantityOpt { get; set; }
    public decimal? UnitBasisUsd { get; init; }
    public string Source { get; init; } = "";
}

public sealed record LotPart(string SourceId, DateTime? AcquiredUtc, decimal QuantityOpt, decimal? BasisUsd);

public sealed class Disposition
{
    public string EventId { get; init; } = "";
    public DateTime DisposedUtc { get; init; }
    public decimal QuantityOpt { get; init; }
    public decimal? ProceedsUsd { get; init; }
    public decimal? BasisUsd { get; init; }
    public decimal? GainUsd => ProceedsUsd is null || BasisUsd is null ? null : ProceedsUsd - BasisUsd;
    public bool LongTerm { get; init; }
    public List<LotPart> Parts { get; init; } = new();
}

public sealed class LotResult
{
    public Dictionary<RewardCategory, decimal> IncomeUsdByCategory { get; } = new();
    public Dictionary<RewardCategory, decimal> UnvaluedIncomeOptByCategory { get; } = new();
    public List<Disposition> Dispositions { get; } = new();
    public List<Lot> RemainingLots { get; } = new();
    public decimal UnmatchedOpt { get; set; }
    public int UnresolvedEvents { get; set; }
    // Basis reconciliation: known basis in = basis disposed + carried out + fees + unresolved transfers
    // + sent to own wallets + still held. Basis received from own wallets counts as basis in.
    public decimal BasisInKnownUsd { get; set; }
    public decimal BasisInUnknownOpt { get; set; }
    public decimal BasisCarriedOutUsd { get; set; }
    public decimal BasisFeesUsd { get; set; }
    public decimal BasisUnresolvedOutUsd { get; set; }
    public decimal BasisTransferredInUsd { get; set; }
    public decimal BasisTransferredOutUsd { get; set; }
    /// <summary>Transfers from an own wallet whose sending side was not found; their lots have unknown basis.</summary>
    public int UnmatchedTransfersIn { get; set; }
    public bool Complete => UnvaluedIncomeOptByCategory.Values.All(v => v == 0) && UnresolvedEvents == 0 && Dispositions.All(d => d.GainUsd is not null) && UnmatchedOpt == 0 && UnmatchedTransfersIn == 0;
}

/// <summary>
/// First-in, first-out lot matching, wallet by wallet. A receipt's selected value is both its income and the basis
/// of its lot, so income and basis cannot drift apart. When OPT moves between the user's own wallets the lots move
/// with it, keeping their basis and acquisition dates. Acquisition dates and disposition proceeds come from the
/// events and are never changed here. Recalculating with different selected values is a new run; earlier results
/// are kept by the caller as report versions.
/// </summary>
public static class LotRecalculator
{
    /// <summary>One wallet (events without a Wallet are treated as one wallet).</summary>
    public static LotResult Run(IEnumerable<LotEvent> events) => RunWallets(events).Values.FirstOrDefault() ?? new LotResult();

    /// <summary>All of the user's wallets together, so transfers between them carry lots across. Result per wallet.</summary>
    public static Dictionary<string, LotResult> RunWallets(IEnumerable<LotEvent> events)
    {
        var results = new Dictionary<string, LotResult>(StringComparer.Ordinal);
        var lotsBy = new Dictionary<string, List<Lot>>(StringComparer.Ordinal);
        var pending = new Dictionary<string, Queue<List<LotPart>>>(StringComparer.Ordinal);
        LotResult R(string w) => results.TryGetValue(w, out var r) ? r : results[w] = new LotResult();
        List<Lot> L(string w) => lotsBy.TryGetValue(w, out var l) ? l : lotsBy[w] = new List<Lot>();

        // Same instant: receipts first, then sends, then the receiving side of own-wallet transfers.
        static int Order(LotEventKind k) => k switch
        {
            LotEventKind.RewardReceipt or LotEventKind.Acquisition => 0,
            LotEventKind.TransferIn => 2,
            _ => 1,
        };

        foreach (var e in events.OrderBy(e => e.TimeUtc).ThenBy(e => Order(e.Kind)))
        {
            var result = R(e.Wallet);
            var lots = L(e.Wallet);
            switch (e.Kind)
            {
                case LotEventKind.RewardReceipt:
                {
                    var cat = e.Category ?? RewardCategory.Unclassified;
                    if (e.ValueUsd is null)
                        result.UnvaluedIncomeOptByCategory[cat] = result.UnvaluedIncomeOptByCategory.GetValueOrDefault(cat) + e.QuantityOpt;
                    else
                        result.IncomeUsdByCategory[cat] = result.IncomeUsdByCategory.GetValueOrDefault(cat) + e.ValueUsd.Value;
                    lots.Add(new Lot { SourceId = e.Id, AcquiredUtc = e.TimeUtc, QuantityOpt = e.QuantityOpt, UnitBasisUsd = e.ValueUsd / e.QuantityOpt, Source = "income" });
                    if (e.ValueUsd is null) result.BasisInUnknownOpt += e.QuantityOpt; else result.BasisInKnownUsd += e.ValueUsd.Value;
                    break;
                }
                case LotEventKind.Acquisition:
                    lots.Add(new Lot { SourceId = e.Id, AcquiredUtc = e.TimeUtc, QuantityOpt = e.QuantityOpt, UnitBasisUsd = e.ValueUsd / e.QuantityOpt, Source = "acquisition" });
                    if (e.ValueUsd is null) result.BasisInUnknownOpt += e.QuantityOpt; else result.BasisInKnownUsd += e.ValueUsd.Value;
                    break;
                case LotEventKind.TransferOut:
                {
                    var parts = Take(lots, e.QuantityOpt, result);
                    result.BasisTransferredOutUsd += parts.Sum(p => p.BasisUsd ?? 0);
                    var key = e.TransferKey ?? e.Id;
                    if (!pending.TryGetValue(key, out var q)) pending[key] = q = new Queue<List<LotPart>>();
                    q.Enqueue(parts);
                    break;
                }
                case LotEventKind.TransferIn:
                {
                    var key = e.TransferKey ?? e.Id;
                    if (pending.TryGetValue(key, out var q) && q.Count > 0)
                    {
                        // The same lots, with their original acquisition dates and basis.
                        foreach (var p in q.Dequeue())
                        {
                            lots.Add(new Lot { SourceId = p.SourceId, AcquiredUtc = p.AcquiredUtc, QuantityOpt = p.QuantityOpt, UnitBasisUsd = p.BasisUsd / p.QuantityOpt, Source = "own-wallet" });
                            if (p.BasisUsd is null) result.BasisInUnknownOpt += p.QuantityOpt; else { result.BasisInKnownUsd += p.BasisUsd.Value; result.BasisTransferredInUsd += p.BasisUsd.Value; }
                        }
                    }
                    else
                    {
                        // The sending side is not among the wallets processed: the lot's basis and date are unknown.
                        lots.Add(new Lot { SourceId = e.Id, AcquiredUtc = null, QuantityOpt = e.QuantityOpt, UnitBasisUsd = null, Source = "own-wallet (sender not found)" });
                        result.BasisInUnknownOpt += e.QuantityOpt;
                        result.UnmatchedTransfersIn++;
                    }
                    break;
                }
                case LotEventKind.Fee:
                    result.BasisFeesUsd += Take(lots, e.QuantityOpt, result).Sum(p => p.BasisUsd ?? 0);
                    break;
                case LotEventKind.CarryOut:
                    result.BasisCarriedOutUsd += Take(lots, e.QuantityOpt, result).Sum(p => p.BasisUsd ?? 0);
                    break;
                case LotEventKind.Unresolved:
                    result.UnresolvedEvents++;
                    result.BasisUnresolvedOutUsd += Take(lots, e.QuantityOpt, result).Sum(p => p.BasisUsd ?? 0);
                    break;
                case LotEventKind.Disposal:
                {
                    var parts = Take(lots, e.QuantityOpt, result);
                    var disposed = e.DisposedUtc ?? e.TimeUtc;
                    // Split by holding period: long term when held more than one year.
                    foreach (var group in parts.GroupBy(p => p.AcquiredUtc is not null && disposed > p.AcquiredUtc.Value.AddYears(1)))
                    {
                        var g = group.ToList();
                        var qty = g.Sum(p => p.QuantityOpt);
                        result.Dispositions.Add(new Disposition
                        {
                            EventId = e.Id, DisposedUtc = disposed, QuantityOpt = qty,
                            ProceedsUsd = e.ValueUsd is null ? null : e.ValueUsd * qty / e.QuantityOpt,
                            BasisUsd = g.Any(p => p.BasisUsd is null) ? null : g.Sum(p => p.BasisUsd!.Value),
                            LongTerm = group.Key, Parts = g,
                        });
                    }
                    break;
                }
            }
        }
        foreach (var (w, lots) in lotsBy) R(w).RemainingLots.AddRange(lots.Where(l => l.QuantityOpt > 0));
        return results;
    }

    private static List<LotPart> Take(List<Lot> lots, decimal qty, LotResult result)
    {
        var parts = new List<LotPart>();
        var left = qty;
        while (left > 0 && lots.Count > 0)
        {
            var lot = lots[0];
            var q = Math.Min(lot.QuantityOpt, left);
            parts.Add(new LotPart(lot.SourceId, lot.AcquiredUtc, q, lot.UnitBasisUsd * q));
            lot.QuantityOpt -= q;
            left -= q;
            if (lot.QuantityOpt <= 0) lots.RemoveAt(0);
        }
        if (left > 0)
        {
            parts.Add(new LotPart("none", null, left, null));
            result.UnmatchedOpt += left;
        }
        return parts;
    }
}

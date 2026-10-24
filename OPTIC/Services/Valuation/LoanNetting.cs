namespace OPTIC.Services.Valuation;

/// <summary>
/// Running loan balances per counterparty, across all of the user's wallets, in time order. OPT the user labelled
/// as a loan raises what that person owes; OPT labelled as a loan repayment lowers it. An unlabelled receipt from
/// someone who still owes the user counts as repayment first, up to the balance owed; only the rest is treated by
/// the normal default (e.g. a gift from a gift wallet). Labels the user set are never changed.
/// </summary>
public sealed class LoanNetting
{
    private readonly Dictionary<string, decimal> _owed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (decimal Lent, decimal Repaid, decimal AutoRepaid)> _totals = new(StringComparer.OrdinalIgnoreCase);

    public decimal OwedBy(string counterparty) => _owed.GetValueOrDefault(counterparty);

    /// <summary>A transfer the user labelled as a loan (sent) — raises the balance owed.</summary>
    public void Lent(string counterparty, decimal opt)
    {
        _owed[counterparty] = OwedBy(counterparty) + opt;
        var t = _totals.GetValueOrDefault(counterparty);
        _totals[counterparty] = (t.Lent + opt, t.Repaid, t.AutoRepaid);
    }

    /// <summary>A receipt the user labelled as a loan repayment — lowers the balance (not below zero).</summary>
    public void Repaid(string counterparty, decimal opt)
    {
        _owed[counterparty] = Math.Max(0, OwedBy(counterparty) - opt);
        var t = _totals.GetValueOrDefault(counterparty);
        _totals[counterparty] = (t.Lent, t.Repaid + opt, t.AutoRepaid);
    }

    /// <summary>An unlabelled receipt: returns how much of it counts as repayment (the rest follows the normal default).</summary>
    public decimal AutoRepay(string counterparty, decimal opt)
    {
        var part = Math.Min(opt, OwedBy(counterparty));
        if (part <= 0) return 0;
        _owed[counterparty] = OwedBy(counterparty) - part;
        var t = _totals.GetValueOrDefault(counterparty);
        _totals[counterparty] = (t.Lent, t.Repaid + part, t.AutoRepaid + part);
        return part;
    }

    public sealed record Balance(string Counterparty, decimal LentOpt, decimal RepaidOpt, decimal AutoRepaidOpt, decimal OwedOpt);

    public List<Balance> Balances() => _totals.Select(kv => new Balance(kv.Key, kv.Value.Lent, kv.Value.Repaid, kv.Value.AutoRepaid, OwedBy(kv.Key)))
        .OrderByDescending(b => b.OwedOpt).ThenByDescending(b => b.LentOpt).ToList();
}

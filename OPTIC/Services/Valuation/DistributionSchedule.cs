using System.Globalization;

namespace OPTIC.Services.Valuation;

public enum DistributionBasis { BestAvailable, Observed, Scheduled, UserAssumed }

public sealed record ResolvedDistribution(
    DateOnly Date,
    RewardCategory Category,
    decimal? OptPerDay,
    string? Basis,                 // "observed", "scheduled", "user-assumed"
    VerificationStatus? Verification,
    string? PeriodId,
    string? MissingReason,
    bool Conflicting);

public sealed record ScenarioRow(decimal Fraction, decimal? OptOffered, decimal? ReferenceNotionalUsd, string Label, string? MissingReason);

public sealed class PeriodMetrics
{
    public DateOnly From { get; init; }
    public DateOnly To { get; init; }
    public string DayBasis { get; init; } = "UTC";
    public decimal PersonalNodeOpt { get; init; }
    public decimal PersonalStakingOpt { get; init; }
    public decimal PersonalUnclassifiedOpt { get; init; }
    public int UncertainClassifications { get; init; }
    public Known ActualNetworkNodeDistributions { get; init; }
    public Known ShareOfNodeDistributions { get; init; }
    /// <summary>(node + staking receipts) ÷ combined network distributions, when only a combined total exists.</summary>
    public Known ShareOfCombinedDistributions { get; init; }
    public Known SelectedNetworkDistribution { get; init; }
    /// <summary>Node receipts ÷ the selected-basis node amount (may be scheduled or user-assumed; see SelectedBasisSummary).</summary>
    public Known ShareOfSelectedNodeDistribution { get; init; }
    public int Days { get; init; }
    public string SelectedBasisSummary { get; init; } = "";
    public List<ScenarioRow> Scenarios { get; init; } = new();
    /// <summary>Network-wide OPT emitted (minted) in the period, from periods that record emission.</summary>
    public Known Emitted { get; init; }
    public string? EmissionBasis { get; init; }
    /// <summary>Observed OPT actually distributed (node + staking, or combined) in the period.</summary>
    public Known Distributed { get; init; }
    /// <summary>Emitted − distributed: minted OPT that was not paid out to recipients.</summary>
    public Known Undistributed { get; init; }
    public List<string> Warnings { get; init; } = new();
}

public sealed record ResolvedEmission(DateOnly Date, decimal? OptPerDay, VerificationStatus? Verification, string? PeriodId, string? MissingReason, bool Conflicting);

/// <summary>
/// Effective-dated network distribution schedule. Scheduled, observed and user-assumed amounts stay separate,
/// every period has an explicit end date, and a date no period covers resolves to "unknown", never to a default.
/// </summary>
public static class DistributionSchedule
{
    public const string ScenarioLabel = "Hypothetical network selling: not observed selling, not a tax adjustment.";

    public static ResolvedDistribution Resolve(IEnumerable<NetworkDistributionPeriod> periods, DateOnly date, RewardCategory category, DistributionBasis basis = DistributionBasis.BestAvailable)
    {
        var covering = periods.Where(p => p.Category == category && p.EffectiveFrom <= date && date <= p.EffectiveTo).ToList();
        if (covering.Count == 0)
            return new(date, category, null, null, null, null, $"No {category.ToString().ToLowerInvariant()} distribution period covers {date:yyyy-MM-dd}.", false);

        var candidates = covering.Select(p => Pick(p, basis)).Where(x => x.Amount.HasValue).ToList();
        if (candidates.Count == 0)
            return new(date, category, null, null, null, covering[0].Id, $"The period covering {date:yyyy-MM-dd} has no {BasisName(basis)} amount.", false);

        // Overlapping periods that disagree are reported, not averaged or silently chosen between.
        var distinct = candidates.Select(c => c.Amount!.Value).Distinct().ToList();
        if (distinct.Count > 1)
            return new(date, category, null, null, VerificationStatus.Conflicting, string.Join(",", candidates.Select(c => c.Period.Id)),
                $"Overlapping periods give different amounts for {date:yyyy-MM-dd}: {string.Join(" vs ", distinct.Select(d => d.ToString("N0", CultureInfo.InvariantCulture)))}.", true);

        var best = candidates[0];
        return new(date, category, best.Amount, best.Basis, best.Period.Verification, best.Period.Id, null, false);
    }

    private static (NetworkDistributionPeriod Period, decimal? Amount, string Basis) Pick(NetworkDistributionPeriod p, DistributionBasis basis) => basis switch
    {
        DistributionBasis.Observed => (p, p.ObservedOptPerDay, "observed"),
        DistributionBasis.Scheduled => (p, p.ScheduledOptPerDay, "scheduled"),
        DistributionBasis.UserAssumed => (p, p.UserAssumedOptPerDay, "user-assumed"),
        _ => p.ObservedOptPerDay is not null ? (p, p.ObservedOptPerDay, "observed")
            : p.ScheduledOptPerDay is not null ? (p, p.ScheduledOptPerDay, "scheduled")
            : (p, p.UserAssumedOptPerDay, "user-assumed"),
    };

    private static string BasisName(DistributionBasis b) => b switch
    {
        DistributionBasis.Observed => "observed",
        DistributionBasis.Scheduled => "scheduled",
        DistributionBasis.UserAssumed => "user-assumed",
        _ => "observed, scheduled or user-assumed",
    };

    /// <summary>
    /// Emission (minting) for a date, from any period that records it. Never inferred from distributions and never
    /// extended past the period end.
    /// </summary>
    public static ResolvedEmission ResolveEmission(IEnumerable<NetworkDistributionPeriod> periods, DateOnly date)
    {
        var covering = periods.Where(p => p.EmittedOptPerDay is not null && p.EffectiveFrom <= date && date <= p.EffectiveTo).ToList();
        if (covering.Count == 0) return new(date, null, null, null, $"No emission recorded for {date:yyyy-MM-dd}.", false);
        var distinct = covering.Select(p => p.EmittedOptPerDay!.Value).Distinct().ToList();
        if (distinct.Count > 1)
            return new(date, null, VerificationStatus.Conflicting, string.Join(",", covering.Select(p => p.Id)), $"Periods give different emission for {date:yyyy-MM-dd}.", true);
        return new(date, distinct[0], covering[0].EmissionVerification ?? covering[0].Verification, covering[0].Id, null, false);
    }

    /// <summary>The calendar day a receipt falls on in the schedule's day basis ("UTC" or a time-zone id).</summary>
    public static DateOnly DayOf(DateTime utc, TimeZoneInfo zone)
    {
        var u = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(u, zone));
    }

    public static TimeZoneInfo Zone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Equals("UTC", StringComparison.OrdinalIgnoreCase)) return TimeZoneInfo.Utc;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.Utc; }
        catch (InvalidTimeZoneException) { return TimeZoneInfo.Utc; }
    }

    public static PeriodMetrics Metrics(
        IReadOnlyList<RewardReceipt> receipts,
        IReadOnlyList<NetworkDistributionPeriod> periods,
        DateOnly from, DateOnly to,
        TimeZoneInfo dayZone,
        DistributionBasis scenarioBasis,
        IEnumerable<decimal> scenarioFractions,
        decimal? referenceUnitPriceUsd = null)
    {
        var warnings = new List<string>();
        var inPeriod = receipts.Where(r => { var d = DayOf(r.TimestampUtc, dayZone); return d >= from && d <= to; }).ToList();
        decimal Sum(RewardCategory c) => inPeriod.Where(r => r.Category == c).Sum(r => r.QuantityOpt);
        var node = Sum(RewardCategory.Node);
        var staking = Sum(RewardCategory.Staking);
        var unclassified = Sum(RewardCategory.Unclassified) + Sum(RewardCategory.Other);
        var uncertain = inPeriod.Count(r => r.Classification != ClassificationStatus.Confirmed);
        if (uncertain > 0)
            warnings.Add($"{uncertain} receipt(s) in this period have an inferred or uncertain node/staking classification; confirm them before relying on the node share.");
        if (unclassified > 0)
            warnings.Add("Some receipts are unclassified and are excluded from both the node and staking totals.");

        // Observed node distributions over the whole period: unknown if any day is missing.
        var days = Enumerable.Range(0, to.DayNumber - from.DayNumber + 1).Select(i => from.AddDays(i)).ToList();
        var observed = days.Select(d => Resolve(periods, d, RewardCategory.Node, DistributionBasis.Observed)).ToList();
        var missingObserved = observed.Count(o => o.OptPerDay is null);
        var actual = missingObserved == 0
            ? Known.Of(observed.Sum(o => o.OptPerDay!.Value))
            : Known.Missing($"Observed node distributions are missing for {missingObserved} of {days.Count} day(s).");
        var share = actual.HasValue ? ValuationMath.Share(node, actual.Value, "Actual network node distributions") : Known.Missing(actual.MissingReason!);

        // A combined (node + staking) network total is compared only with combined personal receipts, and labelled.
        var combinedObs = days.Select(d => Resolve(periods, d, RewardCategory.Combined, DistributionBasis.Observed)).ToList();
        var combinedMissing = combinedObs.Count(o => o.OptPerDay is null);
        var combinedShare = combinedMissing == 0
            ? ValuationMath.Share(node + staking, combinedObs.Sum(o => o.OptPerDay!.Value), "Combined network distributions")
            : Known.Missing($"Observed combined network distributions are missing for {combinedMissing} of {days.Count} day(s).");
        if (!actual.HasValue && combinedShare.HasValue)
            warnings.Add("Only a combined node + staking network total is available, so the share shown compares your node + staking receipts with it; a node-only share needs node-only network data.");

        // Selected amount for scenarios, by the chosen basis, per day.
        var selected = days.Select(d => Resolve(periods, d, RewardCategory.Node, scenarioBasis)).ToList();
        var missingSel = selected.Where(s => s.OptPerDay is null).ToList();
        var selectedTotal = missingSel.Count == 0
            ? Known.Of(selected.Sum(s => s.OptPerDay!.Value))
            : Known.Missing(missingSel[0].MissingReason + (missingSel.Count > 1 ? $" ({missingSel.Count} day(s) without an amount.)" : ""));
        var bases = selected.Where(s => s.Basis != null).GroupBy(s => s.Basis!).Select(g => $"{g.Count()} day(s) {g.Key}").ToList();
        if (selected.Any(s => s.Basis == "user-assumed"))
            warnings.Add("Part of the selected network amount is user-assumed, not verified.");

        var scenarios = scenarioFractions.Distinct().OrderBy(f => f).Select(f =>
        {
            var offered = ValuationMath.ScenarioSelling(selectedTotal.Value, f);
            var notional = offered.Value is null || referenceUnitPriceUsd is null ? (decimal?)null : offered.Value * referenceUnitPriceUsd;
            return new ScenarioRow(f, offered.Value, notional, ScenarioLabel, offered.Value is null ? selectedTotal.MissingReason : null);
        }).ToList();

        // Emission vs distribution: what was minted and what actually reached recipients are separate quantities.
        var emissions = days.Select(d => ResolveEmission(periods, d)).ToList();
        var emitMissing = emissions.Count(e => e.OptPerDay is null);
        var emitted = emitMissing == 0 ? Known.Of(emissions.Sum(e => e.OptPerDay!.Value)) : Known.Missing($"Emission is not recorded for {emitMissing} of {days.Count} day(s).");
        var emissionBasis = emissions.Where(e => e.Verification != null).GroupBy(e => e.Verification!.Value).Select(g => $"{g.Count()} day(s) {g.Key}").ToList();
        var distributed = combinedMissing == 0 ? Known.Of(combinedObs.Sum(o => o.OptPerDay!.Value))
            : actual.HasValue && days.All(d => Resolve(periods, d, RewardCategory.Staking, DistributionBasis.Observed).OptPerDay is not null)
                ? Known.Of(actual.Value!.Value + days.Sum(d => Resolve(periods, d, RewardCategory.Staking, DistributionBasis.Observed).OptPerDay!.Value))
                : Known.Missing("Observed total distributions (node + staking) are not recorded for every day.");
        var undistributed = emitted.HasValue && distributed.HasValue ? Known.Of(emitted.Value!.Value - distributed.Value!.Value)
            : Known.Missing(!emitted.HasValue ? emitted.MissingReason! : distributed.MissingReason!);
        if (emitted.HasValue && emissions.Any(e => e.Verification is VerificationStatus.UserReported or VerificationStatus.Unverified))
            warnings.Add("Emission is user-reported or unverified for part of the period.");
        if (selected.Any(s => s.Basis != null) && emitted.HasValue && selectedTotal.HasValue && selectedTotal.Value == emitted.Value)
            warnings.Add("The node amount used for scenarios equals total emission; scenarios should use what was distributed, not what was minted.");

        return new PeriodMetrics
        {
            Emitted = emitted,
            EmissionBasis = emissionBasis.Count == 0 ? null : string.Join(", ", emissionBasis),
            Distributed = distributed,
            Undistributed = undistributed,
            From = from, To = to, DayBasis = dayZone.Id,
            PersonalNodeOpt = node, PersonalStakingOpt = staking, PersonalUnclassifiedOpt = unclassified,
            UncertainClassifications = uncertain,
            ActualNetworkNodeDistributions = actual,
            ShareOfNodeDistributions = share,
            ShareOfCombinedDistributions = combinedShare,
            SelectedNetworkDistribution = selectedTotal,
            ShareOfSelectedNodeDistribution = selectedTotal.HasValue ? ValuationMath.Share(node, selectedTotal.Value, "Selected network node distributions") : Known.Missing(selectedTotal.MissingReason!),
            Days = days.Count,
            SelectedBasisSummary = bases.Count == 0 ? "none" : string.Join(", ", bases),
            Scenarios = scenarios,
            Warnings = warnings,
        };
    }

    public const string CsvHeader = "effective_from,effective_to,category,scheduled_opt_per_day,observed_opt_per_day,user_assumed_opt_per_day,minted_opt,burned_opt,reserve_release_opt,unlock_opt,reward_epoch,payment_utc,verification,evidence_url,evidence_note,notes,emitted_opt_per_day,emission_verification";

    /// <summary>Parses a schedule CSV. Rows without an end date are rejected rather than extended.</summary>
    public static (List<NetworkDistributionPeriod> Periods, List<string> Errors) ImportCsv(string text, string sourceName, string? sha256, DateTime collectedUtc)
    {
        var periods = new List<NetworkDistributionPeriod>();
        var errors = new List<string>();
        foreach (var (row, f) in Csv.ParseWithHeader(text))
        {
            var before = errors.Count;
            DateOnly? Date(string key)
            {
                if (!f.TryGetValue(key, out var s) || s.Length == 0) { errors.Add($"Row {row}: '{key}' is required."); return null; }
                if (DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
                errors.Add($"Row {row}: '{key}' must be yyyy-MM-dd ({s})."); return null;
            }
            var fromD = Date("effective_from");
            var toD = Date("effective_to");
            if (fromD is not null && toD is not null && toD < fromD) errors.Add($"Row {row}: effective_to is before effective_from.");
            var cat = f.GetValueOrDefault("category", "node");
            if (!Enum.TryParse<RewardCategory>(cat, true, out var category) || category is RewardCategory.Unclassified)
                errors.Add($"Row {row}: category must be node, staking, combined or other ({cat}).");
            var ver = f.GetValueOrDefault("verification", "");
            var verification = VerificationStatus.Unverified;
            if (ver.Length > 0 && !Enum.TryParse(ver.Replace("-", ""), true, out verification))
                errors.Add($"Row {row}: verification must be verified, observed, userreported, unverified or conflicting ({ver}).");
            var scheduled = Csv.Dec(f, "scheduled_opt_per_day", errors, row);
            var observedAmt = Csv.Dec(f, "observed_opt_per_day", errors, row);
            var assumed = Csv.Dec(f, "user_assumed_opt_per_day", errors, row);
            var emittedPerDay = Csv.Dec(f, "emitted_opt_per_day", errors, row);
            VerificationStatus? emissionVerification = null;
            var ev = f.GetValueOrDefault("emission_verification", "");
            if (ev.Length > 0) { if (Enum.TryParse(ev.Replace("-", ""), true, out VerificationStatus evs)) emissionVerification = evs; else errors.Add($"Row {row}: emission_verification is not a known status ({ev})."); }
            if (scheduled is null && observedAmt is null && assumed is null && emittedPerDay is null)
                errors.Add($"Row {row}: give at least one of scheduled, observed, user-assumed or emitted amount.");
            DateTime? payment = null;
            if (f.TryGetValue("payment_utc", out var pay) && pay.Length > 0)
            {
                if (DateTime.TryParse(pay, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var p)) payment = p;
                else errors.Add($"Row {row}: payment_utc is not a date-time ({pay}).");
            }
            var minted = Csv.Dec(f, "minted_opt", errors, row);
            var burned = Csv.Dec(f, "burned_opt", errors, row);
            var reserve = Csv.Dec(f, "reserve_release_opt", errors, row);
            var unlock = Csv.Dec(f, "unlock_opt", errors, row);
            if (errors.Count > before) continue;

            var evidence = new List<EvidenceRef>
            {
                new() { Kind = "file", Description = $"Imported from {sourceName}, row {row}", FileName = sourceName, Sha256 = sha256, CollectedUtc = collectedUtc, ParserVersion = ParserVersion },
            };
            var url = f.GetValueOrDefault("evidence_url", "");
            if (url.Length > 0 || f.GetValueOrDefault("evidence_note", "").Length > 0)
                evidence.Add(new EvidenceRef { Kind = url.Length > 0 ? "url" : "note", Url = url.Length > 0 ? url : null, Description = f.GetValueOrDefault("evidence_note", ""), CollectedUtc = collectedUtc });

            periods.Add(new NetworkDistributionPeriod
            {
                Id = Guid.NewGuid().ToString("N"),
                EffectiveFrom = fromD!.Value, EffectiveTo = toD!.Value, Category = category,
                ScheduledOptPerDay = scheduled, ObservedOptPerDay = observedAmt, UserAssumedOptPerDay = assumed,
                EmittedOptPerDay = emittedPerDay, EmissionVerification = emittedPerDay is null ? null : emissionVerification ?? verification,
                MintedOpt = minted, BurnedOpt = burned, ReserveReleaseOpt = reserve, UnlockOpt = unlock,
                RewardEpoch = NullIfEmpty(f.GetValueOrDefault("reward_epoch", "")), PaymentUtc = payment,
                Verification = verification, Evidence = evidence, Notes = NullIfEmpty(f.GetValueOrDefault("notes", "")), Origin = "csv",
            });
        }
        return (periods, errors);
    }

    public const string ParserVersion = "distribution-csv/1";

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;
}

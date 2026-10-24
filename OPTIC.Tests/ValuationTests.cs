using System.Numerics;

namespace OPTIC.Tests;

// All data in these tests is SYNTHETIC. None of it describes real receipts, markets or tax values.
public class ValuationTests
{
    static RewardReceipt Receipt(string id, DateTime utc, decimal opt, decimal? price = null, RewardCategory cat = RewardCategory.Node, ClassificationStatus cls = ClassificationStatus.Confirmed) => new()
    {
        Id = id, Wallet = "optio1synthetic", TimestampUtc = utc, Category = cat, Classification = cls,
        BaseUnits = opt * 1_000_000m, Reference = new ReferenceValuation { UnitPriceUsd = price, ValueUsd = price is null ? null : opt * price, Status = price is null ? "unpriced" : "priced" },
    };

    static NetworkDistributionPeriod Period(string from, string to, RewardCategory cat, decimal? assumed = null, decimal? observed = null, decimal? scheduled = null) => new()
    {
        Id = Guid.NewGuid().ToString("N"), EffectiveFrom = DateOnly.Parse(from), EffectiveTo = DateOnly.Parse(to), Category = cat,
        UserAssumedOptPerDay = assumed, ObservedOptPerDay = observed, ScheduledOptPerDay = scheduled,
    };

    static OrderBookSnapshot Book(DateTime observed, bool truncated, params (decimal Price, decimal Qty)[] bids) => new()
    {
        Id = "book-" + observed.Ticks, Venue = "SyntheticEx", Market = "OPT/USD", QuoteAsset = "USD", ObservedUtc = observed, Truncated = truncated,
        Bids = bids.Select(b => new BookLevel(b.Price, b.Qty)).ToList(),
    };

    // ------------------------------------------------------------ node vs staking rule

    [Fact]
    public void Smaller_same_day_payout_is_staking_and_undecidable_days_stay_open()
    {
        DateTime T(string s) => DateTime.Parse(s, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);
        var payouts = new List<(DateTime, decimal)>
        {
            (T("2026-03-10T14:05:00Z"), 21_956m), (T("2026-03-10T14:14:00Z"), 17_236m),   // similar sizes, still a clear pair
            (T("2025-08-10T17:31:00Z"), 26_239m), (T("2025-08-10T20:07:00Z"), 1_539m),     // tiny staking payout
            (T("2025-12-10T13:01:00Z"), 5_362m), (T("2025-12-10T13:11:00Z"), 29_047m), (T("2025-12-10T13:32:00Z"), 5_959m), // node + two staking
            (T("2026-06-10T03:29:00Z"), 21_803m), (T("2026-06-10T03:47:00Z"), 17_254m), (T("2026-06-10T15:04:00Z"), 21_803m), (T("2026-06-10T15:23:00Z"), 17_223m), // two pairs
            (T("2025-06-15T03:48:00Z"), 45_930m), (T("2025-06-15T15:02:00Z"), 45_901m),   // same size: undecided
            (T("2025-05-10T12:00:00Z"), 20_000m),                                           // single: undecided
        };
        var l = PayoutClassifier.Classify(payouts);
        Assert.Equal(new[] { false, true }, new[] { l[0].Staking, l[1].Staking });
        Assert.Equal(new[] { false, true }, new[] { l[2].Staking, l[3].Staking });
        Assert.Equal(new[] { true, false, true }, new[] { l[4].Staking, l[5].Staking, l[6].Staking });
        Assert.Equal(new[] { false, true, false, true }, new[] { l[7].Staking, l[8].Staking, l[9].Staking, l[10].Staking });
        Assert.All(l.Take(11), x => Assert.Equal(PayoutDayKind.Split, x.Day));
        Assert.Equal(PayoutDayKind.EqualSizes, l[11].Day);
        Assert.Equal(PayoutDayKind.EqualSizes, l[12].Day);
        Assert.Equal(PayoutDayKind.Single, l[13].Day);
    }

    // ------------------------------------------------------------ receipt-date FMV policy

    static ReceiptMarketEvidence Ev(decimal? reference, string interval = "1h", bool stale = false, decimal? pool = null, decimal? swaps = null,
        int swapsTxs = 0, decimal? dayVolume = null, decimal? impact = null, params string[] deposits) => new()
    {
        ReferenceUnitUsd = reference, ReferenceInterval = reference is null ? null : interval, ReferenceStale = reference is null ? null : stale, ReferenceUnchangedHours = stale ? 6 : 0,
        OsmosisSpotUsd = pool, OsmosisSpotQuote = pool, OsmosisSpotQuoteAsset = "USDC (Noble)", OsmosisObservedUtc = pool is null ? null : new DateTime(2026, 3, 10, 14, 0, 0, DateTimeKind.Utc), OsmosisRoute = "osmosis:3278@1",
        OsmosisDaySwapPriceUsd = swaps, OsmosisDayTxs = swapsTxs, OsmosisDayVolumeOpt = dayVolume, ReportedDayVolumeOpt = dayVolume, ModeledPriceImpact = impact,
        DepositRestrictions = deposits.ToList(),
    };

    [Fact]
    public void Policy_accepts_a_valid_reference_and_qualifies_it()
    {
        var r = Receipt("r", new DateTime(2026, 3, 10, 14, 5, 0, DateTimeKind.Utc), 21_956m, 0.0040m);
        var c = FmvCandidateBuilder.Build(r, Ev(0.0040m, pool: 0.0039m, swaps: 0.00395m, swapsTxs: 40, dayVolume: 500_000m, impact: 0.01m), FmvPolicy.Current, DateTime.UtcNow);
        Assert.Equal("reference", c.Source);
        Assert.Equal(87.82m, c.ValueUsd);
        Assert.Equal(ReviewMethod.AcceptReference, c.Method);
        Assert.True(c.Qualifies);
        Assert.Empty(c.Flags);
        Assert.Contains(c.Options, o => o.Key == "osmosis-pool");
    }

    [Fact]
    public void Policy_flags_conflicts_and_size_effects_but_never_picks_the_lower_price_or_discounts()
    {
        var r = Receipt("r", new DateTime(2026, 3, 10, 14, 5, 0, DateTimeKind.Utc), 21_956m, 0.0040m);
        // Osmosis 40% lower and a 20% modeled impact: both are review triggers; the candidate stays the valid reference.
        var c = FmvCandidateBuilder.Build(r, Ev(0.0040m, pool: 0.0024m, swapsTxs: 3, dayVolume: 50_000m, impact: 0.20m), FmvPolicy.Current, DateTime.UtcNow);
        Assert.Equal("reference", c.Source);
        Assert.Equal(87.82m, c.ValueUsd);
        Assert.False(c.Qualifies);
        Assert.Contains(c.Flags, f => f.Trigger == FmvTrigger.ConflictingEvidence);
        Assert.Contains(c.Flags, f => f.Trigger == FmvTrigger.LargeQuantityEffect);
        Assert.Contains(c.Flags, f => f.Trigger == FmvTrigger.ThinTrading);
        var sale = c.Options.Single(o => o.Key == "osmosis-pool");
        Assert.True(sale.NeedsExplanation);  // choosing the lower market price is an explained decision, not automatic
    }

    [Fact]
    public void Stale_reference_is_replaced_by_the_better_supported_market_price_by_quality_not_level()
    {
        var r = Receipt("r", new DateTime(2026, 3, 10, 14, 5, 0, DateTimeKind.Utc), 1_000m, 0.0040m);
        // The pool price here is HIGHER than the stale reference: the policy still prefers it, because of source quality.
        var c = FmvCandidateBuilder.Build(r, Ev(0.0040m, stale: true, pool: 0.0050m, swapsTxs: 10, dayVolume: 100_000m), FmvPolicy.Current, DateTime.UtcNow);
        Assert.Equal("osmosis-pool", c.Source);
        Assert.Equal(5.00m, c.ValueUsd);
        Assert.Equal(ReviewMethod.AlternativeMarketPrice, c.Method);
        Assert.Contains(c.Flags, f => f.Trigger == FmvTrigger.ReferenceStale);
        Assert.False(c.Qualifies); // a source substitution is reviewed, not bulk-approved
    }

    [Fact]
    public void No_supportable_price_leaves_the_value_unresolved_not_zero()
    {
        var r = Receipt("r", new DateTime(2025, 5, 10, 12, 0, 0, DateTimeKind.Utc), 20_000m);
        var c = FmvCandidateBuilder.Build(r, Ev(null), FmvPolicy.Current, DateTime.UtcNow);
        Assert.Null(c.ValueUsd);
        Assert.Null(c.Source);
        Assert.False(c.Qualifies);
        Assert.Contains(c.Flags, f => f.Trigger == FmvTrigger.ReferenceMissing);
        Assert.Contains(c.Flags, f => f.Trigger == FmvTrigger.NoContemporaneousMarket);
        Assert.Contains("unresolved", c.Explanation);
    }

    [Fact]
    public void Deposit_restrictions_and_daily_fallback_are_triggers_only()
    {
        var r = Receipt("r", new DateTime(2025, 8, 10, 17, 31, 0, DateTimeKind.Utc), 26_239m, 0.007m);
        var c = FmvCandidateBuilder.Build(r, Ev(0.007m, interval: "1d", swaps: 0.0068m, swapsTxs: 5, dayVolume: 1_000_000m, deposits: "Coinstore deposits suspended (UserReported)."), FmvPolicy.Current, DateTime.UtcNow);
        Assert.Equal("reference", c.Source);
        Assert.Equal(Math.Round(26_239m * 0.007m, 2), c.ValueUsd);
        Assert.Contains(c.Flags, f => f.Trigger == FmvTrigger.DepositRestriction);
        Assert.Contains(c.Flags, f => f.Trigger == FmvTrigger.ReferenceDailyFallback);
    }

    [Fact]
    public void Valuation_import_matches_receipts_and_rejects_blanks_and_unexplained_values()
    {
        var r1 = Receipt("optio:AAA:e1.0:w:uOPT", new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc), 1_000m, 0.01m);
        r1.TxHash = "AAA"; r1.EventIndex = 1;
        var r2 = Receipt("optio:BBB:e3.0:w:uOPT", new DateTime(2026, 1, 6, 0, 0, 0, DateTimeKind.Utc), 2_000m, 0.01m);
        r2.TxHash = "BBB"; r2.EventIndex = 3;
        var receipts = new Dictionary<string, RewardReceipt> { [r1.Id] = r1, [r2.Id] = r2 };
        var good = WebDashboard.ValuationImportHeader + "\n" +
            "optio:AAA:e1.0:w:uOPT,,,7.50,illiquid-market,approved,Example Valuer,2026-02-01,SYNTHETIC: illiquid-market conclusion per section 4 of the report,,\n" +
            ",bbb,3,20.00,reference,proposed,,,,,\n";
        var (drafts, errors, sha) = WebDashboard.ParseValuationImport(good, "v.csv", receipts, "me");
        Assert.Empty(errors);
        Assert.Equal(2, drafts.Count);
        Assert.Equal(ReviewStatus.Approved, drafts[0].Status);
        Assert.Equal(ReviewMethod.ImportedValuation, drafts[0].Draft.Method);
        Assert.Equal("Example Valuer", drafts[0].Draft.Reviewer);
        Assert.Contains(drafts[0].Draft.Evidence, e => e.Sha256 == sha);
        Assert.Equal(r2.Id, drafts[1].Draft.ReceiptIds[0]);  // matched by tx hash (any case) and event index

        var bad = WebDashboard.ValuationImportHeader + "\n" +
            "optio:AAA:e1.0:w:uOPT,,,,illiquid-market,approved,Valuer,,blank value,,\n" +
            "optio:BBB:e3.0:w:uOPT,,,5,appraisal,approved,,,,,\n" +
            ",CCC,9,5,reference,proposed,,,,,\n";
        var (none, errs, _) = WebDashboard.ParseValuationImport(bad, "v.csv", receipts, "me");
        Assert.Empty(none);
        Assert.Contains(errs, e => e.Contains("never zero"));
        Assert.Contains(errs, e => e.Contains("reviewer"));
        Assert.Contains(errs, e => e.Contains("no matching receipt"));
    }

    [Fact]
    public void Basis_reconciles_through_disposals_carries_fees_and_holdings()
    {
        var events = new List<LotEvent>
        {
            new() { Id = "r1", TimeUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), Kind = LotEventKind.RewardReceipt, QuantityOpt = 1_000m, ValueUsd = 40m, Category = RewardCategory.Node },
            new() { Id = "r2", TimeUtc = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc), Kind = LotEventKind.RewardReceipt, QuantityOpt = 500m, ValueUsd = null, Category = RewardCategory.Staking },
            new() { Id = "fee", TimeUtc = new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc), Kind = LotEventKind.Fee, QuantityOpt = 10m },
            new() { Id = "own", TimeUtc = new DateTime(2026, 1, 4, 0, 0, 0, DateTimeKind.Utc), Kind = LotEventKind.CarryOut, QuantityOpt = 90m },
            new() { Id = "sale", TimeUtc = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc), Kind = LotEventKind.Disposal, QuantityOpt = 600m, ValueUsd = 20m },
            new() { Id = "node", TimeUtc = new DateTime(2026, 5, 26, 0, 0, 0, DateTimeKind.Utc), Kind = LotEventKind.Disposal, QuantityOpt = 100m, ValueUsd = null, Note = "node" },
        };
        var res = LotRecalculator.Run(events);
        Assert.Equal(40m, res.BasisInKnownUsd);
        Assert.Equal(500m, res.BasisInUnknownOpt);
        Assert.Equal(0.4m, res.BasisFeesUsd);
        Assert.Equal(3.6m, res.BasisCarriedOutUsd);
        var disposed = res.Dispositions.Sum(d => d.Parts.Sum(p => p.BasisUsd ?? 0));
        var held = res.RemainingLots.Where(l => l.UnitBasisUsd != null).Sum(l => l.QuantityOpt * l.UnitBasisUsd!.Value);
        Assert.Equal(res.BasisInKnownUsd, disposed + res.BasisCarriedOutUsd + res.BasisFeesUsd + res.BasisUnresolvedOutUsd + held);
        // The node-license purchase has no amount realized entered: its gain stays unknown, never filled from the OPT price.
        var node = res.Dispositions.Single(d => d.EventId == "node");
        Assert.Null(node.ProceedsUsd);
        Assert.Null(node.GainUsd);
    }

    [Fact]
    public void Moves_between_own_wallets_carry_basis_and_acquisition_dates()
    {
        var events = new List<LotEvent>
        {
            new() { Id = "r1", Wallet = "A", TimeUtc = new DateTime(2025, 1, 10, 0, 0, 0, DateTimeKind.Utc), Kind = LotEventKind.RewardReceipt, QuantityOpt = 1_000m, ValueUsd = 50m, Category = RewardCategory.Node },
            new() { Id = "out", Wallet = "A", TimeUtc = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc), Kind = LotEventKind.TransferOut, QuantityOpt = 600m, TransferKey = "TX1|B" },
            new() { Id = "in", Wallet = "B", TimeUtc = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc), Kind = LotEventKind.TransferIn, QuantityOpt = 600m, TransferKey = "TX1|B" },
            new() { Id = "sale", Wallet = "B", TimeUtc = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc), Kind = LotEventKind.Disposal, QuantityOpt = 600m, ValueUsd = 12m },
            new() { Id = "orphan", Wallet = "B", TimeUtc = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc), Kind = LotEventKind.TransferIn, QuantityOpt = 5m, TransferKey = "TX9|B" },
        };
        var res = LotRecalculator.RunWallets(events);
        var b = res["B"];
        var sale = Assert.Single(b.Dispositions);
        Assert.Equal(30m, sale.BasisUsd);                                        // 600 of the 1,000-OPT lot at $0.05
        Assert.True(sale.LongTerm);                                               // held since 2025-01-10, not since the move
        Assert.Equal(new DateTime(2025, 1, 10, 0, 0, 0, DateTimeKind.Utc), sale.Parts[0].AcquiredUtc);
        Assert.Equal(30m, res["A"].BasisTransferredOutUsd);
        Assert.Equal(20m, res["A"].RemainingLots.Sum(l => l.QuantityOpt * l.UnitBasisUsd!.Value));
        Assert.Equal(1, b.UnmatchedTransfersIn);                                  // no sending side: unknown basis, never zero
        Assert.Contains(b.RemainingLots, l => l.UnitBasisUsd == null && l.QuantityOpt == 5m);
        // Single-wallet use is unchanged.
        Assert.Equal(50m, LotRecalculator.Run(events.Take(1)).BasisInKnownUsd);
    }

    [Fact]
    public void Unlabelled_receipts_from_a_borrower_count_as_repayment_first()
    {
        var loans = new LoanNetting();
        loans.Lent("J1", 2_500_001m);                 // labelled loans
        loans.Repaid("J1", 1_500_500m);               // labelled repayment
        loans.Lent("J1", 502_379.50m);
        Assert.Equal(1_501_880.50m, loans.OwedBy("J1"));
        Assert.Equal(1_501_880.50m, loans.AutoRepay("J1", 2_000_000m)); // unlabelled: repayment up to what is owed
        Assert.Equal(0m, loans.OwedBy("J1"));          // the remaining 498,119.50 follows the normal default (e.g. a gift)
        Assert.Equal(0m, loans.AutoRepay("J4", 1_000m)); // nothing owed: nothing treated as repayment
        var b = loans.Balances().Single(x => x.Counterparty == "J1");
        Assert.Equal(3_002_380.50m, b.LentOpt);
        Assert.Equal(1_501_880.50m, b.AutoRepaidOpt);
        Assert.Equal(3_002_380.50m, b.RepaidOpt);
    }

    // ------------------------------------------------------------ distribution share and scenarios

    [Fact]
    public void Share_of_60M_and_network_scenarios_are_exact_and_change_no_tax_value()
    {
        var day = new DateOnly(2025, 3, 1);
        var receipts = new List<RewardReceipt> { Receipt("r1", new DateTime(2025, 3, 1, 15, 0, 0, DateTimeKind.Utc), 35_000m, 0.01m) };
        var periods = new List<NetworkDistributionPeriod> { Period("2025-03-01", "2025-03-01", RewardCategory.Node, assumed: 60_000_000m, observed: 60_000_000m) };

        var m = DistributionSchedule.Metrics(receipts, periods, day, day, TimeZoneInfo.Utc, DistributionBasis.UserAssumed, ValuationMath.DefaultScenarioFractions, 0.01m);

        Assert.Equal(35_000m / 60_000_000m, m.ShareOfNodeDistributions.Value);
        Assert.Equal(0.0583333333m, Math.Round(m.ShareOfNodeDistributions.Value!.Value * 100m, 10)); // percent
        Assert.Equal(new decimal?[] { 600_000m, 3_000_000m, 6_000_000m }, m.Scenarios.Select(s => s.OptOffered).ToArray());
        Assert.All(m.Scenarios, s => Assert.Contains("Hypothetical", s.Label));
        Assert.Equal(6_000m, m.Scenarios[0].ReferenceNotionalUsd); // notional only; not cash required
        Assert.Contains(m.Warnings, w => w.Contains("user-assumed"));

        // No automatic tax change: without an approved review there is no selected value.
        var selected = ValuationReviews.Selected(Array.Empty<ValuationReview>(), receipts.ToDictionary(r => r.Id));
        Assert.Empty(selected);
    }

    [Fact]
    public void Schedule_never_extends_60M_into_unverified_periods()
    {
        var periods = new[] { Period("2025-01-01", "2025-01-31", RewardCategory.Node, assumed: 60_000_000m) };
        Assert.Equal(60_000_000m, DistributionSchedule.Resolve(periods, new DateOnly(2025, 1, 31), RewardCategory.Node).OptPerDay);
        var feb = DistributionSchedule.Resolve(periods, new DateOnly(2025, 2, 1), RewardCategory.Node);
        Assert.Null(feb.OptPerDay);
        Assert.NotNull(feb.MissingReason);
        // Observed basis does not fall back to an assumption.
        Assert.Null(DistributionSchedule.Resolve(periods, new DateOnly(2025, 1, 15), RewardCategory.Node, DistributionBasis.Observed).OptPerDay);

        // CSV rows without an end date are rejected instead of being extended.
        var (imported, errors) = DistributionSchedule.ImportCsv(DistributionSchedule.CsvHeader + "\n2025-01-01,,node,,,60000000,,,,,,,unverified,,,\n", "s.csv", "abc", DateTime.UtcNow);
        Assert.Empty(imported);
        Assert.Contains(errors, e => e.Contains("effective_to"));
    }

    [Fact]
    public void Schedule_changes_and_conflicts_are_reported_not_merged()
    {
        var periods = new[]
        {
            Period("2025-01-01", "2025-09-14", RewardCategory.Node, scheduled: 41_000_000m),
            Period("2025-09-15", "2026-09-14", RewardCategory.Node, scheduled: 20_500_000m),
            Period("2025-09-15", "2025-09-20", RewardCategory.Node, scheduled: 30_000_000m),
        };
        Assert.Equal(41_000_000m, DistributionSchedule.Resolve(periods, new DateOnly(2025, 9, 14), RewardCategory.Node).OptPerDay);
        var conflict = DistributionSchedule.Resolve(periods, new DateOnly(2025, 9, 16), RewardCategory.Node);
        Assert.True(conflict.Conflicting);
        Assert.Null(conflict.OptPerDay);
        Assert.Equal(20_500_000m, DistributionSchedule.Resolve(periods, new DateOnly(2025, 9, 21), RewardCategory.Node).OptPerDay);
    }

    [Fact]
    public void Combined_denominator_is_labelled_and_node_share_stays_unknown()
    {
        var day = new DateOnly(2025, 3, 1);
        var receipts = new List<RewardReceipt>
        {
            Receipt("n", new DateTime(2025, 3, 1, 10, 0, 0, DateTimeKind.Utc), 30_000m),
            Receipt("s", new DateTime(2025, 3, 1, 11, 0, 0, DateTimeKind.Utc), 1_000m, cat: RewardCategory.Staking),
        };
        var periods = new[] { Period("2025-03-01", "2025-03-01", RewardCategory.Combined, observed: 62_000_000m) };
        var m = DistributionSchedule.Metrics(receipts, periods, day, day, TimeZoneInfo.Utc, DistributionBasis.BestAvailable, ValuationMath.DefaultScenarioFractions);
        Assert.Null(m.ShareOfNodeDistributions.Value);
        Assert.Equal(31_000m / 62_000_000m, m.ShareOfCombinedDistributions.Value);
        Assert.Contains(m.Warnings, w => w.Contains("combined"));
        Assert.All(m.Scenarios, s => Assert.Null(s.OptOffered)); // no node amount: scenarios not computed
    }

    [Fact]
    public void Emission_is_kept_apart_from_distribution()
    {
        // Synthetic: 60M OPT/day emitted (user-reported) but only 40M/day observed paid out.
        var emission = Period("2025-03-01", "2025-03-02", RewardCategory.Combined);
        emission.EmittedOptPerDay = 60_000_000m;
        emission.EmissionVerification = VerificationStatus.UserReported;
        var paid = Period("2025-03-01", "2025-03-02", RewardCategory.Combined, observed: 40_000_000m);
        var periods = new[] { emission, paid };
        var m = DistributionSchedule.Metrics(new List<RewardReceipt>(), periods, new DateOnly(2025, 3, 1), new DateOnly(2025, 3, 2), TimeZoneInfo.Utc, DistributionBasis.BestAvailable, ValuationMath.DefaultScenarioFractions);
        Assert.Equal(120_000_000m, m.Emitted.Value);
        Assert.Equal(80_000_000m, m.Distributed.Value);
        Assert.Equal(40_000_000m, m.Undistributed.Value);
        Assert.Contains(m.Warnings, w => w.Contains("Emission is user-reported"));
        // Emission never stands in for a node distribution amount, and is not extended past its period.
        Assert.All(m.Scenarios, s => Assert.Null(s.OptOffered));
        Assert.Null(DistributionSchedule.ResolveEmission(periods, new DateOnly(2025, 3, 3)).OptPerDay);

        var csv = DistributionSchedule.CsvHeader + "\n2025-03-01,2025-03-31,combined,,,,,,,,,,unverified,,,,60000000,userreported\n";
        var (imported, errors) = DistributionSchedule.ImportCsv(csv, "e.csv", "x", DateTime.UtcNow);
        Assert.Empty(errors);
        Assert.Equal(60_000_000m, imported[0].EmittedOptPerDay);
        Assert.Equal(VerificationStatus.UserReported, imported[0].EmissionVerification);
        Assert.Null(imported[0].ObservedOptPerDay);
    }

    [Fact]
    public void Missing_distribution_supply_and_conversion_propagate_as_unknown()
    {
        var day = new DateOnly(2025, 3, 1);
        var m = DistributionSchedule.Metrics(new List<RewardReceipt>(), new List<NetworkDistributionPeriod>(), day, day, TimeZoneInfo.Utc, DistributionBasis.BestAvailable, ValuationMath.DefaultScenarioFractions);
        Assert.Null(m.ShareOfNodeDistributions.Value);
        Assert.NotNull(m.ShareOfNodeDistributions.MissingReason);
        Assert.Null(ValuationMath.NetMintSupplyGrowth(ValuationMath.NetMinted(100m, null).Value, 1000m).Value);
        Assert.Null(ValuationMath.NetMintSupplyGrowth(50m, null).Value);
        var (usd, missing) = ValuationMath.ToUsd(10m, "USDT", day, Array.Empty<QuoteConversion>());
        Assert.Null(usd);
        Assert.Contains("USDT", missing);
    }

    // ------------------------------------------------------------ execution math

    [Fact]
    public void Synthetic_350_reference_and_105_output_give_70_percent_shortfall_without_changing_selected_value()
    {
        var shortfall = ValuationMath.ExecutionShortfall(35_000m, 35_000m, 105m, 350m);
        Assert.Equal(0.70m, shortfall.Value);

        var receipt = Receipt("r", new DateTime(2025, 3, 1, 15, 0, 0, DateTimeKind.Utc), 35_000m, 0.01m);
        var book = Book(new DateTime(2025, 3, 1, 14, 0, 0, DateTimeKind.Utc), false, (0.003m, 35_000m));
        var analysis = ReceiptAnalyzer.Analyze(receipt, new[] { Accessible(new OrderBookMarket(book)) }, Array.Empty<QuoteConversion>(), new AnalysisSettings());
        Assert.Equal(AnalysisOutcome.SupportedHistoricalEstimate, analysis.Outcome);
        Assert.Equal(105m, analysis.ModeledOutputUsd);
        Assert.Equal(0.70m, analysis.ExecutionShortfall);
        Assert.Equal(350m, receipt.Reference.ValueUsd);           // reference value preserved
        Assert.Empty(ValuationReviews.Selected(Array.Empty<ValuationReview>(), new Dictionary<string, RewardReceipt> { ["r"] = receipt }));
    }

    [Fact]
    public void Negative_shortfall_is_allowed_and_partial_sales_have_no_shortfall()
    {
        Assert.Equal(-0.1m, ValuationMath.ExecutionShortfall(10m, 10m, 110m, 100m).Value);
        Assert.Null(ValuationMath.ExecutionShortfall(10m, 4m, 40m, 100m).Value);
    }

    [Fact]
    public void Partial_depth_leaves_explicit_residual_and_prices_only_the_filled_part()
    {
        var book = Book(DateTime.UtcNow, truncated: true, (0.010m, 10_000m), (0.009m, 5_000m));
        var est = OrderBookSimulator.Sell(book, 35_000m);
        Assert.Equal(AnalysisOutcome.PartialCapacity, est.Outcome);
        Assert.Equal(15_000m, est.ExecutableOpt);
        Assert.Equal(20_000m, est.ResidualOpt);
        Assert.Equal(145m, est.OutputQuote);
        Assert.Contains(est.Notes, n => n.Contains("truncated"));
        Assert.Equal(145m / 15_000m, ValuationMath.EffectiveExecutionPrice(est.OutputQuote, est.ExecutableOpt));
        Assert.Equal(15_000m / 35_000m, ValuationMath.FillRatio(35_000m, est.ExecutableOpt));
    }

    [Fact]
    public void Bids_are_consumed_highest_first_and_capacity_threshold_is_by_marginal_price()
    {
        var book = Book(DateTime.UtcNow, false, (0.008m, 1_000m), (0.010m, 1_000m), (0.0095m, 1_000m));
        var est = OrderBookSimulator.Sell(book, 1_500m);
        Assert.Equal(10m + 500m * 0.0095m, est.OutputQuote);
        Assert.Equal(0.0095m, est.WorstPrice);
        Assert.Equal(2_000m, OrderBookSimulator.CapacityAt(book, 0.05m).CapacityOpt);  // ≥ 0.0095
        Assert.Equal(3_000m, OrderBookSimulator.CapacityAt(book, 0.25m).CapacityOpt);
    }

    [Fact]
    public void Missing_markets_or_snapshots_are_insufficient_data_not_zero()
    {
        var r = Receipt("r", new DateTime(2025, 3, 1, 15, 0, 0, DateTimeKind.Utc), 1_000m, 0.01m);
        var a = ReceiptAnalyzer.Analyze(r, Array.Empty<CandidateMarket>(), Array.Empty<QuoteConversion>(), new AnalysisSettings());
        Assert.Equal(AnalysisOutcome.InsufficientData, a.Outcome);
        Assert.Null(a.ModeledOutputUsd);
        Assert.Equal(1_000m, a.ResidualOpt);

        // A later snapshot is not used for an earlier receipt, and an old one is rejected by the age limit.
        var policy = new SnapshotPolicy { MaxAge = TimeSpan.FromHours(24) };
        var later = Book(new DateTime(2025, 3, 2, 0, 0, 0, DateTimeKind.Utc), false, (0.01m, 1m));
        var old = Book(new DateTime(2025, 2, 20, 0, 0, 0, DateTimeKind.Utc), false, (0.01m, 1m));
        var (snap, corroboration, reason) = policy.Select(new[] { later, old }, b => b.ObservedUtc, r.TimestampUtc);
        Assert.Null(snap);
        Assert.Single(corroboration);
        Assert.Contains("old", reason);
    }

    [Fact]
    public void Usd_results_need_a_documented_conversion()
    {
        var r = Receipt("r", new DateTime(2025, 3, 1, 15, 0, 0, DateTimeKind.Utc), 1_000m, 0.01m);
        var book = Book(new DateTime(2025, 3, 1, 14, 0, 0, DateTimeKind.Utc), false, (0.01m, 5_000m));
        book.QuoteAsset = "USDT";
        var a = ReceiptAnalyzer.Analyze(r, new[] { Accessible(new OrderBookMarket(book)) }, Array.Empty<QuoteConversion>(), new AnalysisSettings());
        Assert.True(a.UsdIncomplete);
        Assert.Equal(10m, a.ModeledOutputQuote);
        Assert.Null(a.ModeledOutputUsd);
        var conv = new[] { new QuoteConversion { QuoteAsset = "USDT", Date = new DateOnly(2025, 3, 1), UsdPerUnit = 0.999m, Source = "synthetic" } };
        var b = ReceiptAnalyzer.Analyze(r, new[] { Accessible(new OrderBookMarket(book)) }, conv, new AnalysisSettings());
        Assert.Equal(9.99m, b.ModeledOutputUsd);
    }

    [Fact]
    public void Transaction_costs_are_shown_separately_from_execution_output()
    {
        var r = Receipt("r", new DateTime(2025, 3, 1, 15, 0, 0, DateTimeKind.Utc), 1_000m, 0.01m);
        var book = Book(new DateTime(2025, 3, 1, 14, 0, 0, DateTimeKind.Utc), false, (0.01m, 5_000m));
        var a = ReceiptAnalyzer.Analyze(r, new[] { Accessible(new OrderBookMarket(book)) }, Array.Empty<QuoteConversion>(), new AnalysisSettings { TransactionCostsUsd = 1m });
        Assert.Equal(10m, a.ModeledOutputUsd);
        Assert.Equal(9m, a.EstimatedNetWalletProceedsUsd);
        Assert.Equal(0m, a.ExecutionShortfall); // shortfall is on execution output, not net of costs
    }

    // ------------------------------------------------------------ venue access

    [Fact]
    public void Us_restriction_does_not_remove_venue_from_evidence_and_announcements_are_not_verified()
    {
        var at = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var statuses = new[]
        {
            new VenueStatus { Venue = "SyntheticEx", Kind = VenueAccessKind.Trading, State = VenueAccessState.Available, EffectiveFromUtc = at.AddYears(-1), Verification = VerificationStatus.Observed, ImplementationVerified = true },
            new VenueStatus { Venue = "SyntheticEx", Kind = VenueAccessKind.Deposits, State = VenueAccessState.Available, EffectiveFromUtc = at.AddYears(-1), Verification = VerificationStatus.Observed, ImplementationVerified = true },
            new VenueStatus { Venue = "SyntheticEx", Kind = VenueAccessKind.JurisdictionAccess, State = VenueAccessState.Restricted, Jurisdiction = "US", EffectiveFromUtc = at.AddMonths(-1), Verification = VerificationStatus.UserReported },
        };
        var us = VenueAccess.Decide(statuses, "SyntheticEx", at, new TaxpayerProfile { Jurisdiction = "US" }, onChainVenue: false);
        Assert.True(us.InEvidenceUniverse);
        Assert.False(us.TaxpayerAccessible);
        Assert.Equal(AnalysisOutcome.InsufficientData, us.Blocking); // user-reported, not independently verified
        var other = VenueAccess.Decide(statuses, "SyntheticEx", at, new TaxpayerProfile { Jurisdiction = "DE" }, onChainVenue: false);
        Assert.Contains(other.Reasons, r => r.StartsWith("JurisdictionAccess: no status"));
    }

    // ------------------------------------------------------------ pools and routes

    static OsmosisPoolSnapshot Balancer(string id, long height, long? created, decimal opt, decimal usdc, bool exists = true) => new()
    {
        Id = $"p{id}@{height}", PoolId = id, PoolType = "balancer", ServedHeight = height, RequestedHeight = height, CreatedHeight = created, Exists = exists,
        BlockTimeUtc = new DateTime(2025, 3, 1, 12, 0, 0, DateTimeKind.Utc), SwapFee = "0.003", TakerFee = "0.001", TakerFeeSource = "synthetic",
        Assets =
        {
            new PoolAssetDto { Denom = OsmosisAssets.OptDenom, Amount = ((long)(opt * 1_000_000m)).ToString(), Weight = "1" },
            new PoolAssetDto { Denom = "ibc/498A0751C798A0D9A389AA3691123DADA57DAA4FE165D5C75894505B876BA6E4", Amount = ((long)(usdc * 1_000_000m)).ToString(), Weight = "1" },
        },
    };

    [Fact]
    public void Pool_cannot_be_used_before_creation_but_funded_pool_without_trades_is_usable()
    {
        var before = Balancer("9001", 100, created: 150, 1_000_000m, 10_000m);
        var notYet = Balancer("9002", 100, created: null, 0m, 0m, exists: false);
        var funded = Balancer("9003", 100, created: 50, 1_000_000m, 10_000m); // never traded in this synthetic history
        var (routes, excluded) = PoolRouteMarket.DiscoverRoutes(new[] { before, notYet, funded }, 100);
        Assert.Single(routes);
        Assert.Equal("9003", routes[0][0].Pool.PoolId);
        Assert.Contains(excluded, e => e.Contains("9001") && e.Contains("created"));
        Assert.Contains(excluded, e => e.Contains("9002") && e.Contains("did not exist"));
        var q = new PoolRouteMarket(routes[0]).Quote(1_000m);
        Assert.Equal(AnalysisOutcome.SupportedHistoricalEstimate, q.Outcome);
        Assert.True(q.OutputQuote > 0);
        Assert.Equal("USDC (Noble)", q.QuoteAsset);
    }

    [Fact]
    public void Balancer_route_applies_taker_fee_then_pool_fee()
    {
        var pool = Balancer("9003", 100, 50, 1_000_000m, 10_000m);
        var q = new PoolRouteMarket(new[] { new RouteHop(pool, OsmosisAssets.OptDenom, pool.Assets[1].Denom) }).Quote(1_000m);
        BigInteger inNet = 1_000_000_000 - 1_000_000; // 0.1% taker fee on 1,000 OPT in uOPT
        var expected = GammMath.BalancerOutGivenIn(1_000_000_000_000, Fx.Dec("1"), 10_000_000_000, Fx.Dec("1"), inNet, Fx.Dec("0.003"));
        Assert.Equal((decimal)expected / 1_000_000m, q.OutputQuote);
    }

    [Fact]
    public void Shared_pools_are_not_counted_twice_in_split_routes()
    {
        var pool = Balancer("9003", 100, 50, 1_000_000m, 10_000m);
        var a = new PoolRouteMarket(new[] { new RouteHop(pool, OsmosisAssets.OptDenom, pool.Assets[1].Denom) });
        var b = new PoolRouteMarket(new[] { new RouteHop(pool, OsmosisAssets.OptDenom, pool.Assets[1].Denom) });
        var split = MarketSplitter.Sell(new ISellMarket[] { a, b }, 10_000m, 50);
        Assert.Single(split.Excluded);
        var single = MarketSplitter.Sell(new ISellMarket[] { a }, 10_000m, 50);
        Assert.Equal(single.Combined.OutputQuote, split.Combined.OutputQuote);
    }

    [Fact]
    public void Failed_exact_input_swap_is_not_a_partial_fill()
    {
        // One range [price 0.01, 0.1] holding about 5,000 OPT of buy-side depth; current price 0.04.
        var L = Fx.Dec("1000000000");
        var pool = new OsmosisPoolSnapshot
        {
            Id = "cl@100", PoolId = "9100", PoolType = "concentrated", ServedHeight = 100, BlockTimeUtc = DateTime.UtcNow,
            Token0 = OsmosisAssets.OptDenom, Token1 = "ibc/498A0751C798A0D9A389AA3691123DADA57DAA4FE165D5C75894505B876BA6E4",
            CurrentSqrtPrice = "0.2", CurrentTick = ClMath.SqrtPriceToTick(Fx.BigDec("0.2")), CurrentLiquidity = "1000000000", SpreadFactor = "0.003", TakerFee = "0",
            TicksBelowComplete = true, TicksAboveComplete = true,
            Ticks = { new PoolTickDto { Index = -18_000_000, LiquidityNet = Fx.Format(L, 18) }, new PoolTickDto { Index = -9_000_000, LiquidityNet = "-" + Fx.Format(L, 18) } },
        };
        var market = new PoolRouteMarket(new[] { new RouteHop(pool, pool.Token0!, pool.Token1!) });
        var huge = market.Quote(6_000m);
        Assert.Equal(0m, huge.ExecutableOpt);
        Assert.Null(huge.OutputQuote);
        Assert.Contains(huge.Notes, n => n.Contains("fails"));
        var small = market.Quote(1m);
        Assert.Equal(1m, small.ExecutableOpt);
        Assert.True(small.OutputQuote > 0);
    }

    // ------------------------------------------------------------ independent vs cumulative

    [Fact]
    public void Independent_receipt_estimates_are_not_cumulative_proceeds()
    {
        var book = Book(new DateTime(2025, 3, 1, 9, 0, 0, DateTimeKind.Utc), false, (0.010m, 1_000m), (0.005m, 10_000m));
        var r1 = Receipt("a", new DateTime(2025, 3, 1, 10, 0, 0, DateTimeKind.Utc), 1_000m, 0.01m);
        var r2 = Receipt("b", new DateTime(2025, 3, 1, 11, 0, 0, DateTimeKind.Utc), 1_000m, 0.01m);
        var market = new OrderBookMarket(book);
        var a1 = ReceiptAnalyzer.Analyze(r1, new[] { Accessible(market) }, Array.Empty<QuoteConversion>(), new AnalysisSettings());
        var a2 = ReceiptAnalyzer.Analyze(r2, new[] { Accessible(market) }, Array.Empty<QuoteConversion>(), new AnalysisSettings());
        Assert.True(a1.IndependentCounterfactual && a2.IndependentCounterfactual);
        Assert.Equal(10m, a1.ModeledOutputUsd);
        Assert.Equal(10m, a2.ModeledOutputUsd); // each sees the untouched book

        var stress = StressSimulator.Run(new[] { (new DateOnly(2025, 3, 1), 2_000m) }, d => new ISellMarket[] { new OrderBookMarket(book) }, null, carryUnsold: true);
        Assert.Equal(15m, stress.TotalOutputQuote);       // 1,000 × 0.010 + 1,000 × 0.005
        Assert.NotEqual(a1.ModeledOutputUsd + a2.ModeledOutputUsd, stress.TotalOutputQuote);
        Assert.Contains("Cumulative", stress.Label);
    }

    [Fact]
    public void Stress_state_carries_depletion_and_never_silently_resets()
    {
        var book = Book(new DateTime(2025, 3, 1, 0, 0, 0, DateTimeKind.Utc), false, (0.01m, 1_000m));
        var sales = new[] { (new DateOnly(2025, 3, 1), 800m), (new DateOnly(2025, 3, 2), 800m) };
        var r = StressSimulator.Run(sales, d => d == new DateOnly(2025, 3, 1) ? new ISellMarket[] { new OrderBookMarket(book) } : null, null, carryUnsold: false);
        Assert.Equal(800m, r.Days[0].ExecutedOpt);
        Assert.Equal(200m, r.Days[1].ExecutedOpt);
        Assert.Contains("carried", r.Days[1].StateSource);
        Assert.Throws<ArgumentException>(() => StressSimulator.Run(sales, _ => null, new ReplenishmentAssumption { DailyFraction = 0.5m }, false));
    }

    [Fact]
    public void Marketing_horizon_requires_explicit_assumptions()
    {
        var (outcome, result, notes) = MarketingHorizon.Run(new MarketingHorizonAssumptions { Days = 7, InventoryOpt = 10_000m }, Array.Empty<ISellMarket>(), new DateOnly(2025, 3, 1));
        Assert.Equal(AnalysisOutcome.InsufficientData, outcome);
        Assert.Null(result);
        Assert.Contains("buyer flow", notes[0]);
    }

    // ------------------------------------------------------------ identity, imports, time zones

    [Fact]
    public void Imports_keep_distinct_events_dedupe_reimports_and_keep_rows_without_hashes()
    {
        var csv = ReceiptIdentity.CsvHeader + "\n" +
            "optio1w,2025-03-01T10:00:00Z,ABCDEF,,node,inferred,1000000,uOPT,6,,,,,,,,\n" +
            "optio1w,2025-03-01T10:00:00Z,ABCDEF,,staking,inferred,1000000,uOPT,6,,,,,,,,\n" +   // same tx, same amount, second event
            "optio1w,2025-03-02T10:00:00Z,,,node,inferred,2000000,uOPT,6,,,,,,,,\n" +           // no hash
            "optio1w,2025-03-03T10:00:00Z,ABC999,4,node,confirmed,3000000,uOPT,6,,,,,,,,\n";
        var (receipts, errors, dups) = ReceiptIdentity.ImportCsv(csv, "synthetic.csv");
        Assert.Empty(errors);
        Assert.Equal(4, receipts.Count);
        Assert.Equal(0, dups);
        Assert.Equal(4, receipts.Select(r => r.Id).Distinct().Count());
        Assert.Equal("source-row", receipts[2].IdentityBasis);
        Assert.Equal("chain-event", receipts[3].IdentityBasis);
        Assert.Equal(1m, receipts[0].QuantityOpt);

        var (again, _, _) = ReceiptIdentity.ImportCsv(csv, "synthetic.csv");
        Assert.Equal(receipts.Select(r => r.Id), again.Select(r => r.Id)); // re-import maps to the same ids

        var bad = ReceiptIdentity.ImportCsv(ReceiptIdentity.CsvHeader + "\noptio1w,2025-03-01T10:00:00Z,,,node,,1.5,uOPT,6,,,,,,,,\n", "x.csv");
        Assert.Contains(bad.Errors, e => e.Contains("whole number"));
    }

    [Fact]
    public void Chain_event_ids_separate_two_payments_in_one_tx()
    {
        var a = ReceiptIdentity.ForChainEvent("optio", "abc", 7, 0, "optio1w", "uOPT");
        var b = ReceiptIdentity.ForChainEvent("optio", "ABC", 7, 1, "optio1w", "uOPT");
        var c = ReceiptIdentity.ForChainEvent("optio", "ABC", 7, 0, "OPTIO1W", "uOPT");
        Assert.NotEqual(a, b);
        Assert.Equal(a, c);
    }

    [Fact]
    public void Reporting_days_follow_eastern_time_across_dst()
    {
        var ny = TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "Eastern Standard Time" : "America/New_York");
        // 03:30 UTC on 2025-03-09 is still 8 March in New York (EST, UTC-5).
        Assert.Equal(new DateOnly(2025, 3, 8), DistributionSchedule.DayOf(new DateTime(2025, 3, 9, 3, 30, 0, DateTimeKind.Utc), ny));
        // 04:30 UTC on 2025-03-10 is 00:30 on 10 March in New York (EDT, UTC-4) after the change.
        Assert.Equal(new DateOnly(2025, 3, 10), DistributionSchedule.DayOf(new DateTime(2025, 3, 10, 4, 30, 0, DateTimeKind.Utc), ny));
        Assert.Equal(new DateOnly(2025, 3, 9), DistributionSchedule.DayOf(new DateTime(2025, 3, 10, 3, 30, 0, DateTimeKind.Utc), ny));
        // UTC timestamps stay UTC; only the summary day changes.
        var receipts = new List<RewardReceipt> { Receipt("late", new DateTime(2025, 3, 2, 2, 0, 0, DateTimeKind.Utc), 100m) };
        var m = DistributionSchedule.Metrics(receipts, new List<NetworkDistributionPeriod>(), new DateOnly(2025, 3, 1), new DateOnly(2025, 3, 1), ny, DistributionBasis.BestAvailable, new[] { 0.01m });
        Assert.Equal(100m, m.PersonalNodeOpt);
        Assert.Equal(new DateTime(2025, 3, 2, 2, 0, 0, DateTimeKind.Utc), receipts[0].TimestampUtc);
    }

    // ------------------------------------------------------------ reviews and lots

    [Fact]
    public void Reviews_need_explicit_approval_and_zero_needs_substantive_evidence()
    {
        var receipts = new Dictionary<string, RewardReceipt> { ["r"] = Receipt("r", DateTime.UtcNow, 1_000m, 0.35m) };
        var zero = new ValuationReview { ReceiptIds = { "r" }, SelectedValueUsd = 0, Method = ReviewMethod.Other, Explanation = "No buyers.", Reviewer = "me", Evidence = { new EvidenceRef { Description = "my statement", UserReported = true } } };
        var errors = ValuationReviews.Validate(zero, receipts);
        Assert.Contains(errors, e => e.Contains("beyond the user's own statement"));
        Assert.Contains(errors, e => e.Contains("substantive explanation"));

        var accept = new ValuationReview { ReceiptIds = { "r" }, SelectedValueUsd = 350m, Method = ReviewMethod.AcceptReference, Explanation = "Accept reference.", Reviewer = "me" };
        Assert.Empty(ValuationReviews.Validate(accept, receipts));
        var proposed = ValuationReviews.NextVersion(Array.Empty<ValuationReview>(), accept, ReviewStatus.Proposed, "me", DateTime.UtcNow);
        Assert.Empty(ValuationReviews.Selected(new[] { proposed }, receipts));          // proposing changes nothing
        var approved = ValuationReviews.NextVersion(new[] { proposed }, proposed, ReviewStatus.Approved, "me", DateTime.UtcNow);
        Assert.Equal(2, approved.Version);
        Assert.Equal(350m, ValuationReviews.Selected(new[] { proposed, approved }, receipts)["r"].ValueUsd);
        var withdrawn = ValuationReviews.NextVersion(new[] { proposed, approved }, approved, ReviewStatus.Withdrawn, "me", DateTime.UtcNow);
        Assert.Empty(ValuationReviews.Selected(new[] { proposed, approved, withdrawn }, receipts));

        var wrongAccept = new ValuationReview { ReceiptIds = { "r" }, SelectedValueUsd = 100m, Method = ReviewMethod.AcceptReference, Explanation = "x", Reviewer = "me" };
        Assert.Contains(ValuationReviews.Validate(wrongAccept, receipts), e => e.Contains("must equal the reference"));
    }

    [Fact]
    public void Group_review_keeps_members_and_allocates_exactly()
    {
        var receipts = new Dictionary<string, RewardReceipt>
        {
            ["a"] = Receipt("a", DateTime.UtcNow, 1m),
            ["b"] = Receipt("b", DateTime.UtcNow, 2m),
            ["c"] = Receipt("c", DateTime.UtcNow, 3m),
        };
        var g = new ValuationReview { ReceiptIds = { "a", "b", "c" }, SelectedValueUsd = 100m, Method = ReviewMethod.HistoricalExecutionEvidence, Explanation = "Same day, same market state.", Reviewer = "me", AllocationMethod = "pro-rata-quantity", GroupJustification = "Same day" };
        Assert.Empty(ValuationReviews.Validate(g, receipts));
        var alloc = ValuationReviews.Allocate(g, receipts);
        Assert.Equal(3, alloc.Count);
        Assert.Equal(100m, alloc.Sum(x => x.Usd));
        Assert.Equal(Math.Round(100m / 6m, 8), alloc[0].Usd);
        var noReason = new ValuationReview { ReceiptIds = { "a", "b" }, SelectedValueUsd = 1m, Method = ReviewMethod.Other, Explanation = "x", Reviewer = "me", AllocationMethod = "pro-rata-quantity" };
        Assert.Contains(ValuationReviews.Validate(noReason, receipts), e => e.Contains("justification"));
    }

    [Fact]
    public void Coverage_shows_known_subtotal_and_unvalued_quantity()
    {
        var rs = new[] { Receipt("a", DateTime.UtcNow, 10m), Receipt("b", DateTime.UtcNow, 5m) };
        var sel = new Dictionary<string, SelectedValue> { ["a"] = new("a", 2m, "x", 1, ReviewMethod.Other, "me", DateTime.UtcNow) };
        var c = ValuationReviews.Coverage(rs, sel);
        Assert.False(c.Complete);
        Assert.Equal(2m, c.KnownValueUsd);
        Assert.Equal(5m, c.UnvaluedOpt);
        Assert.StartsWith("Draft", c.ReportStatus);
    }

    [Fact]
    public void Revaluation_updates_income_basis_and_dispositions_but_not_proceeds_or_dates()
    {
        List<LotEvent> Events(decimal? value) => new()
        {
            new LotEvent { Id = "r1", TimeUtc = new DateTime(2024, 1, 10, 0, 0, 0, DateTimeKind.Utc), Kind = LotEventKind.RewardReceipt, QuantityOpt = 1_000m, ValueUsd = value, Category = RewardCategory.Node },
            new LotEvent { Id = "own", TimeUtc = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc), Kind = LotEventKind.CarryOut, QuantityOpt = 100m },
            new LotEvent { Id = "sale", TimeUtc = new DateTime(2025, 3, 1, 0, 0, 0, DateTimeKind.Utc), Kind = LotEventKind.Disposal, QuantityOpt = 500m, ValueUsd = 50m },
        };
        var v1 = LotRecalculator.Run(Events(350m));
        var v2 = LotRecalculator.Run(Events(105m));
        var none = LotRecalculator.Run(Events(null));

        Assert.Equal(350m, v1.IncomeUsdByCategory[RewardCategory.Node]);
        Assert.Equal(105m, v2.IncomeUsdByCategory[RewardCategory.Node]);
        Assert.Equal(175m, v1.Dispositions[0].BasisUsd);
        Assert.Equal(52.5m, v2.Dispositions[0].BasisUsd);
        Assert.Equal(50m, v1.Dispositions[0].ProceedsUsd);      // proceeds unchanged by revaluation
        Assert.Equal(50m, v2.Dispositions[0].ProceedsUsd);
        Assert.True(v1.Dispositions[0].LongTerm);               // acquisition date preserved
        Assert.Equal(new DateTime(2024, 1, 10, 0, 0, 0, DateTimeKind.Utc), v2.Dispositions[0].Parts[0].AcquiredUtc);
        Assert.Equal(400m * 0.105m, v2.RemainingLots.Sum(l => l.QuantityOpt * l.UnitBasisUsd!.Value));
        Assert.Null(none.Dispositions[0].GainUsd);              // unknown basis is not zero
        Assert.False(none.Complete);
        Assert.Equal(1_000m, none.UnvaluedIncomeOptByCategory[RewardCategory.Node]);
    }

    // ------------------------------------------------------------ storage and permissions

    [Fact]
    public void Store_keeps_report_versions_and_review_history_and_enforces_ownership()
    {
        var dir = Path.Combine(Path.GetTempPath(), "optic-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var db = Path.Combine(dir, "valuation.db");
            var store = new ValuationStore(db);
            store = new ValuationStore(db); // migrations run once and never repeat destructively
            Assert.Equal(new[] { 1, 2 }, store.AppliedMigrations());

            var first = store.AddReportVersion("alice", "optio1w", 2025, "fp1", "draft", new { income = 350m });
            var second = store.AddReportVersion("alice", "optio1w", 2025, "fp2", "final", new { income = 105m });
            Assert.Equal(2, store.ReportVersions("alice").Count);
            Assert.Contains("350", store.ReportVersionJson("alice", first));
            Assert.Contains("105", store.ReportVersionJson("alice", second));
            Assert.Null(store.ReportVersionJson("bob", first));

            var review = new ValuationReview { ReviewId = "r1", Version = 1, ReceiptIds = { "x" }, Status = ReviewStatus.Proposed, Reviewer = "alice" };
            store.AppendReview("alice", review);
            store.AppendReview("alice", new ValuationReview { ReviewId = "r1", Version = 2, ReceiptIds = { "x" }, Status = ReviewStatus.Approved, Reviewer = "alice" });
            Assert.Equal(2, store.Reviews("alice").Count);
            Assert.Empty(store.Reviews("bob"));

            store.Put("venue-status", "v1", "alice", new VenueStatus { Venue = "A" }, "alice");
            store.Put("venue-status", "v2", ValuationStore.SystemOwner, new VenueStatus { Venue = "B" }, "admin");
            Assert.Equal(2, store.List<VenueStatus>("venue-status", "alice").Count);
            Assert.Single(store.List<VenueStatus>("venue-status", "bob"));

            store.Put("venue-status", "v1", "alice", new VenueStatus { Venue = "A2" }, "alice"); // replace keeps history
            Assert.Equal("A2", store.Get<VenueStatus>("venue-status", "v1")!.Value.Venue);
            Assert.True(store.Delete("venue-status", "v1", "alice"));
            Assert.Null(store.Get<VenueStatus>("venue-status", "v1"));

            Assert.True(ValuationStore.CanWrite("alice", false, "alice"));
            Assert.False(ValuationStore.CanWrite("alice", false, "bob"));
            Assert.False(ValuationStore.CanWrite("alice", false, ValuationStore.SystemOwner));
            Assert.True(ValuationStore.CanWrite("admin", true, ValuationStore.SystemOwner));
            Assert.False(ValuationStore.CanRead("alice", "bob"));

            var sha = store.ArchiveEvidence("a.json", "{}"u8.ToArray(), "https://example.invalid", "alice");
            Assert.Equal(ReceiptIdentity.Sha256Hex("{}"u8.ToArray()), sha);
            Assert.Single(store.EvidenceFiles());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Csv_round_trips_quotes_and_neutralises_formulas()
    {
        var rows = Csv.Parse("a,b\n\"x, y\",\"he said \"\"hi\"\"\"\n");
        Assert.Equal("x, y", rows[1][0]);
        Assert.Equal("he said \"hi\"", rows[1][1]);
        Assert.Equal("'=SUM(A1)", Csv.Field("=SUM(A1)"));
        Assert.Equal("-5", Csv.Field(-5m));
    }

    static CandidateMarket Accessible(ISellMarket m) => new(m, new AccessDecision(m.Venue, true, true, null, new List<string>()), null);
}

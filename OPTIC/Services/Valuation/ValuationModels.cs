using System.Text.Json.Serialization;

namespace OPTIC.Services.Valuation;

// The three layers are kept apart on purpose:
//   1. Reference valuation  - what the configured price provider said, never edited (ReferenceValuation).
//   2. Market-capacity evidence - what the historical markets could absorb (MarketSnapshot, ExecutionEstimate, ...).
//   3. Reviewed tax valuation - what a person explicitly selected, with method and evidence (ValuationReview).
// Nothing in layer 2 writes to layer 3; only an explicit approval does.

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RewardCategory { Node, Staking, Other, Unclassified, Combined }  // Combined = node + staking, used only for network totals

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ClassificationStatus { Confirmed, Inferred, Uncertain }

/// <summary>The user's confirmation of the payout-size rule (smaller same-day payout = staking), with when and who.</summary>
public sealed class ClassificationRuleConfirmation
{
    public const string RuleId = "smaller-same-day-payout-is-staking";
    public string Rule { get; set; } = RuleId;
    public string Statement { get; set; } = "";
    public string ConfirmedBy { get; set; } = "";
    public DateTime ConfirmedUtc { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VerificationStatus { Verified, Observed, UserReported, Unverified, Conflicting }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AnalysisOutcome
{
    SupportedHistoricalEstimate,
    PartialCapacity,
    InsufficientData,
    InsufficientDepthData,
    VerifiedVenueUnavailable,
    NoExecutableRouteWithinVerifiedCoverage,
    UnsupportedPoolType,
    ConflictingEvidence,
}

/// <summary>Where a figure came from. A hash shows a file is unchanged since capture, not that it is true.</summary>
public sealed class EvidenceRef
{
    public string Kind { get; set; } = "";              // e.g. "url", "file", "chain-query", "user-statement"
    public string Description { get; set; } = "";
    public string? Url { get; set; }
    public string? FileName { get; set; }
    public string? Sha256 { get; set; }
    public long? BlockHeight { get; set; }
    public DateTime? EffectiveUtc { get; set; }         // when the fact applies (historical time)
    public DateTime? CollectedUtc { get; set; }         // when the evidence was captured
    public string? ParserVersion { get; set; }
    public string? RepositoryRef { get; set; }          // e.g. "OptioServices/optio@<sha>:path#L10"
    public bool UserReported { get; set; }
}

/// <summary>Exactly what the reference price is: provider, endpoint, field, observation time and interval.</summary>
public sealed class PriceSourceInfo
{
    public string Provider { get; set; } = "";          // e.g. "CoinMarketCap"
    public string? Endpoint { get; set; }
    public string? Field { get; set; }                  // e.g. "quote.close"
    public string? Interval { get; set; }               // e.g. "1h close", "1d close", "7d average"
    public string QuoteCurrency { get; set; } = "USD";
    public DateTime? ObservedUtc { get; set; }
    public string? Fallback { get; set; }               // set when a fallback provider supplied the price
}

public sealed class ReferenceValuation
{
    public decimal? UnitPriceUsd { get; set; }
    public decimal? ValueUsd { get; set; }
    public PriceSourceInfo? Source { get; set; }
    public string Status { get; set; } = "unpriced";    // "priced", "fallback", "unpriced"
}

/// <summary>One reward receipt. Quantity is held exactly in base units (uOPT) and as OPT.</summary>
public sealed class RewardReceipt
{
    public string Id { get; set; } = "";
    public string IdentityBasis { get; set; } = "";     // "chain-event", "chain-tx-ordinal", "source-row"
    public string Wallet { get; set; } = "";
    public string Chain { get; set; } = "optio";
    public string? TxHash { get; set; }
    public int? MsgIndex { get; set; }
    public int? EventIndex { get; set; }
    public int Ordinal { get; set; }
    public string? SourceFile { get; set; }
    public int? SourceRow { get; set; }
    public DateTime TimestampUtc { get; set; }
    public RewardCategory Category { get; set; }
    public ClassificationStatus Classification { get; set; }
    /// <summary>Why the category was given, e.g. the smaller-same-day rule, a single-payout day, or the user.</summary>
    public string? ClassificationBasis { get; set; }
    public string Denom { get; set; } = "uOPT";
    public int Decimals { get; set; } = 6;
    public decimal BaseUnits { get; set; }
    public decimal QuantityOpt => Decimals == 6 ? BaseUnits / 1_000_000m : BaseUnits / Pow10(Decimals);
    public string? Sender { get; set; }
    public ReferenceValuation Reference { get; set; } = new();
    public string? LotId { get; set; }

    internal static decimal Pow10(int n)
    {
        var r = 1m;
        for (var i = 0; i < n; i++) r *= 10m;
        return r;
    }
}

/// <summary>Network distribution for an effective period. Scheduled, observed and assumed amounts never stand in for each other.</summary>
public sealed class NetworkDistributionPeriod
{
    public string Id { get; set; } = "";
    public DateOnly EffectiveFrom { get; set; }
    public DateOnly EffectiveTo { get; set; }           // inclusive; every period is bounded so nothing is extended silently
    public RewardCategory Category { get; set; } = RewardCategory.Node;
    public decimal? ScheduledOptPerDay { get; set; }
    public decimal? ObservedOptPerDay { get; set; }
    public decimal? UserAssumedOptPerDay { get; set; }
    /// <summary>OPT emitted (minted) per day, network-wide. Emission is not distribution: minted OPT that was not paid out stays undistributed.</summary>
    public decimal? EmittedOptPerDay { get; set; }
    public VerificationStatus? EmissionVerification { get; set; }
    public decimal? MintedOpt { get; set; }
    public decimal? BurnedOpt { get; set; }
    public decimal? ReserveReleaseOpt { get; set; }
    public decimal? UnlockOpt { get; set; }
    public string? RewardEpoch { get; set; }
    public DateTime? PaymentUtc { get; set; }
    public VerificationStatus Verification { get; set; } = VerificationStatus.Unverified;
    public List<EvidenceRef> Evidence { get; set; } = new();
    public string? Notes { get; set; }
    public string Origin { get; set; } = "manual";      // "manual", "csv", "chain"
}

public sealed class SupplyObservation
{
    public string Id { get; set; } = "";
    public DateTime AtUtc { get; set; }
    public long? BlockHeight { get; set; }
    public decimal? TotalSupplyOpt { get; set; }
    public decimal? CirculatingSupplyOpt { get; set; }
    public string? CirculatingDefinition { get; set; }
    public string Source { get; set; } = "";
    public string? Restrictions { get; set; }
    public string? CoverageLimitations { get; set; }
    public List<EvidenceRef> Evidence { get; set; } = new();
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VenueAccessKind { Trading, Deposits, Withdrawals, JurisdictionAccess }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VenueAccessState { Available, Suspended, Restricted, Unavailable, Unknown }

/// <summary>Effective-dated status of one access dimension at a venue. An announcement is not its implementation.</summary>
public sealed class VenueStatus
{
    public string Id { get; set; } = "";
    public string Venue { get; set; } = "";
    public VenueAccessKind Kind { get; set; }
    public VenueAccessState State { get; set; }
    public DateTime EffectiveFromUtc { get; set; }
    public DateTime? EffectiveToUtc { get; set; }
    public DateTime? AnnouncedUtc { get; set; }
    public bool ImplementationVerified { get; set; }
    public string? Jurisdiction { get; set; }           // null = applies everywhere; "US" = only US accounts
    public VerificationStatus Verification { get; set; } = VerificationStatus.UserReported;
    public List<EvidenceRef> Evidence { get; set; } = new();
    public string? Notes { get; set; }
}

public sealed record BookLevel(decimal Price, decimal Quantity);

/// <summary>Bid side of a centralized-exchange order book at an instant.</summary>
public sealed class OrderBookSnapshot
{
    public string Id { get; set; } = "";
    public string Venue { get; set; } = "";
    public string Market { get; set; } = "";
    public string BaseAsset { get; set; } = "";         // canonical id, e.g. "optio:uOPT"
    public string QuoteAsset { get; set; } = "";        // "USD", "USDT", "USDC" are different assets
    public DateTime ObservedUtc { get; set; }
    public DateTime CollectedUtc { get; set; }
    public List<BookLevel> Bids { get; set; } = new();
    public bool Truncated { get; set; }
    public decimal TakerFeeRate { get; set; }
    public List<EvidenceRef> Evidence { get; set; } = new();
}

/// <summary>A historical conversion from a quote asset to USD.</summary>
public sealed class QuoteConversion
{
    public string QuoteAsset { get; set; } = "";
    public DateOnly Date { get; set; }
    public decimal UsdPerUnit { get; set; }
    public string Source { get; set; } = "";
}

/// <summary>Result of selling a quantity into one market state.</summary>
public sealed class ExecutionEstimate
{
    public AnalysisOutcome Outcome { get; set; }
    public decimal RequestedOpt { get; set; }
    public decimal ExecutableOpt { get; set; }
    public decimal ResidualOpt => RequestedOpt - ExecutableOpt;
    public decimal? OutputQuote { get; set; }           // before transaction costs, in QuoteAsset
    public decimal? FeesQuote { get; set; }             // venue/pool fees already deducted from OutputQuote
    public decimal? OutputBeforeFeesQuote { get; set; } // same sale with price impact only (no trading fees)
    public string QuoteAsset { get; set; } = "";
    public decimal? BestPrice { get; set; }
    public decimal? WorstPrice { get; set; }
    public string? Venue { get; set; }
    public List<string> Routes { get; set; } = new();
    public List<string> SnapshotIds { get; set; } = new();
    public List<string> Assumptions { get; set; } = new();
    public List<string> Notes { get; set; } = new();
    public bool Approximation { get; set; }
}

/// <summary>Receipt-level analysis. Independent counterfactual unless produced by a cumulative stress run.</summary>
public sealed class ReceiptAnalysis
{
    public string ReceiptId { get; set; } = "";
    public AnalysisOutcome Outcome { get; set; }
    public decimal RequestedOpt { get; set; }
    public decimal ExecutableOpt { get; set; }
    public decimal ResidualOpt { get; set; }
    public decimal? ReferenceValueUsd { get; set; }
    public decimal? ModeledOutputQuote { get; set; }
    public string? QuoteAsset { get; set; }
    public decimal? ModeledOutputUsd { get; set; }
    public decimal? ModeledOutputBeforeFeesQuote { get; set; }
    public decimal? ModeledOutputBeforeFeesUsd { get; set; }
    public decimal? ModeledFillRatio { get; set; }
    public decimal? EffectiveExecutionPriceUsd { get; set; }
    public decimal? ExecutionShortfall { get; set; }
    public decimal? EstimatedTransactionCostsUsd { get; set; }
    public decimal? EstimatedNetWalletProceedsUsd { get; set; }
    public bool UsdIncomplete { get; set; }
    public bool IndependentCounterfactual { get; set; } = true;
    public List<ThresholdCapacity> Capacity { get; set; } = new();
    public List<string> SnapshotIds { get; set; } = new();
    public List<string> Routes { get; set; } = new();
    public List<string> Assumptions { get; set; } = new();
    public List<string> Notes { get; set; } = new();
}

/// <summary>Largest quantity whose marginal price stayed within a deterioration threshold of the pre-trade best price.</summary>
public sealed record ThresholdCapacity(decimal Threshold, decimal? CapacityOpt, string Basis);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReviewMethod
{
    AcceptReference,
    HistoricalExecutionEvidence,
    IndependentAppraisal,
    ObservedTransaction,
    Other,
    AlternativeMarketPrice,   // a better-supported market price at the receipt time
    TimingApproximation,      // a market price at a documented nearby time (e.g. the day's average)
    ImportedValuation,        // a value supplied from outside OPTIC (e.g. a valuation professional), imported with its file
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReviewStatus { Proposed, Approved, Superseded, Withdrawn }

/// <summary>A reviewer's selected value for a receipt or a justified group of receipts. Versioned, never edited in place.</summary>
public sealed class ValuationReview
{
    public string ReviewId { get; set; } = "";
    public int Version { get; set; }
    public List<string> ReceiptIds { get; set; } = new();
    public string? GroupJustification { get; set; }
    public string AllocationMethod { get; set; } = "single";    // "single", "pro-rata-quantity", "per-receipt"
    public decimal SelectedValueUsd { get; set; }
    public Dictionary<string, decimal>? PerReceiptUsd { get; set; }
    public ReviewMethod Method { get; set; }
    public string Explanation { get; set; } = "";
    public List<EvidenceRef> Evidence { get; set; } = new();
    public string Reviewer { get; set; } = "";
    public DateTime ReviewedUtc { get; set; }
    public ReviewStatus Status { get; set; }
    public int? SupersedesVersion { get; set; }
    public decimal? ReferenceValueUsdAtReview { get; set; }
    /// <summary>Who drafted it: null for the user, "fmv-builder:&lt;method&gt;" for proposals made by the fair-market-value builder.</summary>
    public string? Source { get; set; }
}

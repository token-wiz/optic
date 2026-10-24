using System.Globalization;
using System.Text.Json.Serialization;

namespace OPTIC.Services.Valuation;

/// <summary>
/// Versioned source-quality policy for receipt-date fair market value. It decides which market price is the best
/// supported for a receipt and what needs a reviewer. It never applies a percentage discount and never prefers a
/// price because it is lower. Changing any parameter means a new version.
/// </summary>
public sealed class FmvPolicy
{
    public string Version { get; init; } = "receipt-date-fmv/1";
    /// <summary>Two market prices further apart than this (relative to the larger) are conflicting evidence.</summary>
    public decimal ConflictTolerance { get; init; } = 0.15m;
    /// <summary>The reference counts as stale when its hourly close is unchanged across this many preceding hours.</summary>
    public int StaleHours { get; init; } = 6;
    /// <summary>A receipt larger than this share of the day's observed OPT volume is thin trading.</summary>
    public decimal ThinTradingShare { get; init; } = 0.25m;
    /// <summary>A modeled price impact for the receipt quantity above this is a large quantity effect (a review trigger only).</summary>
    public decimal LargeQuantityImpact { get; init; } = 0.05m;
    /// <summary>Market state older than this before the receipt is not contemporaneous.</summary>
    public double MaxMarketAgeHours { get; init; } = 24;

    public static readonly FmvPolicy Current = new();

    public string Describe() => string.Join(" ", new[]
    {
        $"Policy {Version}:",
        "the candidate is the CoinMarketCap close of the hour containing the receipt, for OPT (CoinMarketCap id 35828) in USD, when it is present, hourly and not stale;",
        "otherwise the best-supported contemporaneous market price: Osmosis pool marginal price just before receipt, else the day's Osmosis swap price, converted from USDC at a documented daily rate;",
        $"no candidate when neither exists (the value stays unresolved, never zero).",
        $"Review triggers: prices differing by more than {ConflictTolerance:P0}; reference unchanged for {StaleHours}+ hours; daily-close fallback; receipt over {ThinTradingShare:P0} of the day's observed volume or no observed trades;",
        $"deposits restricted at a venue; no contemporaneous market state within {MaxMarketAgeHours:0} h; modeled price impact above {LargeQuantityImpact:P0} for the receipt quantity; missing USD conversion.",
        "Triggers send a receipt to review; they never change its value by themselves.",
    });
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FmvTrigger
{
    ReferenceMissing,
    ReferenceDailyFallback,
    ReferenceStale,
    ConflictingEvidence,
    ThinTrading,
    NoObservedTrades,
    DepositRestriction,
    NoContemporaneousMarket,
    LargeQuantityEffect,
    CurrencyConversionMissing,
}

/// <summary>Everything known about the market at one receipt, gathered before a candidate is chosen.</summary>
public sealed class ReceiptMarketEvidence
{
    public decimal? ReferenceUnitUsd { get; set; }
    public string? ReferenceInterval { get; set; }               // "1h" or "1d"
    public bool? ReferenceStale { get; set; }
    public int ReferenceUnchangedHours { get; set; }
    public decimal? OsmosisSpotQuote { get; set; }               // quote units per OPT, marginal, before fees
    public string? OsmosisSpotQuoteAsset { get; set; }
    public decimal? OsmosisSpotUsd { get; set; }
    public DateTime? OsmosisObservedUtc { get; set; }
    public string? OsmosisRoute { get; set; }
    public decimal? OsmosisDaySwapPriceUsd { get; set; }         // stable-quoted swaps that UTC day
    public int? OsmosisDayTxs { get; set; }
    public decimal? OsmosisDayVolumeOpt { get; set; }
    public decimal? ReportedDayVolumeOpt { get; set; }           // CoinMarketCap-reported volume in OPT (as reported)
    public decimal? ModeledProceedsBeforeFeesUsd { get; set; }   // evidence only: immediate sale of the whole receipt
    public decimal? ModeledFillRatio { get; set; }
    public decimal? ModeledPriceImpact { get; set; }             // 1 − proceeds ÷ (spot × quantity)
    public bool ConversionDocumented { get; set; }
    public List<string> DepositRestrictions { get; set; } = new();
    public List<string> Notes { get; set; } = new();
}

public sealed record FmvOption(string Key, string Label, decimal? ValueUsd, decimal? UnitPriceUsd, ReviewMethod Method, bool NeedsExplanation, string Detail);

public sealed record FmvFlag(FmvTrigger Trigger, string Detail);

public sealed class FmvCandidate
{
    public string ReceiptId { get; set; } = "";
    public string PolicyVersion { get; set; } = "";
    public string? Source { get; set; }                   // "reference", "osmosis-pool", "osmosis-swaps", or null
    public decimal? UnitPriceUsd { get; set; }
    public decimal? ValueUsd { get; set; }
    public ReviewMethod? Method { get; set; }
    public bool Qualifies { get; set; }
    public List<FmvFlag> Flags { get; set; } = new();
    public List<string> Checks { get; set; } = new();     // validations that passed
    public List<FmvOption> Options { get; set; } = new();
    public ReceiptMarketEvidence Evidence { get; set; } = new();
    public DateTime BuiltUtc { get; set; }
    public string Explanation { get; set; } = "";
}

/// <summary>Applies the policy to one receipt. Pure: the same receipt, evidence and policy give the same candidate.</summary>
public static class FmvCandidateBuilder
{
    public static FmvCandidate Build(RewardReceipt r, ReceiptMarketEvidence e, FmvPolicy policy, DateTime nowUtc)
    {
        var c = new FmvCandidate { ReceiptId = r.Id, PolicyVersion = policy.Version, Evidence = e, BuiltUtc = nowUtc };
        var q = r.QuantityOpt;
        void Flag(FmvTrigger t, string d) => c.Flags.Add(new FmvFlag(t, d));

        // --- reference validation (token identity and currency are fixed by the source: CMC id 35828, USD)
        var refOk = e.ReferenceUnitUsd is > 0;
        if (!refOk) Flag(FmvTrigger.ReferenceMissing, "No CoinMarketCap price for the receipt time.");
        else
        {
            c.Checks.Add("Reference is OPT (CoinMarketCap id 35828) quoted in USD.");
            if (e.ReferenceInterval != "1h") { Flag(FmvTrigger.ReferenceDailyFallback, "Only a daily close is available for this time (no hourly candle)."); }
            else c.Checks.Add("Reference is the close of the hour containing the receipt.");
            if (e.ReferenceStale == true) { Flag(FmvTrigger.ReferenceStale, $"Reference close unchanged for {e.ReferenceUnchangedHours} hours: likely no trading behind it."); refOk = false; }
            else if (e.ReferenceStale == false) c.Checks.Add($"Reference moved within the preceding {policy.StaleHours} hours.");
        }

        // --- other market prices at the time
        var poolOk = e.OsmosisSpotUsd is > 0;
        var swapsOk = e.OsmosisDaySwapPriceUsd is > 0 && (e.OsmosisDayTxs ?? 0) > 0;
        if (e.OsmosisSpotQuote is > 0 && e.OsmosisSpotUsd is null)
            Flag(FmvTrigger.CurrencyConversionMissing, $"Osmosis price is in {e.OsmosisSpotQuoteAsset} with no documented USD rate for the day.");
        if (poolOk) c.Checks.Add($"Osmosis pool state {(e.OsmosisObservedUtc is { } t ? $"at {t:yyyy-MM-dd HH:mm} UTC" : "")} ({e.OsmosisRoute}) for OPT (IBC denom from Optio channel-5).");

        // --- conflicts between independent prices
        var prices = new List<(string Name, decimal P)>();
        if (e.ReferenceUnitUsd is > 0) prices.Add(("CoinMarketCap", e.ReferenceUnitUsd.Value));
        if (poolOk) prices.Add(("Osmosis pool", e.OsmosisSpotUsd!.Value));
        else if (swapsOk) prices.Add(("Osmosis swaps that day", e.OsmosisDaySwapPriceUsd!.Value));
        if (prices.Count >= 2)
        {
            var max = prices.Max(p => p.P);
            var min = prices.Min(p => p.P);
            if ((max - min) / max > policy.ConflictTolerance)
                Flag(FmvTrigger.ConflictingEvidence, string.Join(" vs ", prices.Select(p => $"{p.Name} {p.P.ToString("0.000000", CultureInfo.InvariantCulture)}")) + $" differ by {(max - min) / max:P1}.");
            else c.Checks.Add($"Independent prices agree within {policy.ConflictTolerance:P0}.");
        }

        // --- liquidity and access: triggers only
        var observedVolume = e.OsmosisDayVolumeOpt;
        if ((e.OsmosisDayTxs ?? 0) == 0 && (e.ReportedDayVolumeOpt ?? 0) == 0)
            Flag(FmvTrigger.NoObservedTrades, "No trades observed that day on the venues covered.");
        else if (observedVolume is > 0 && q > policy.ThinTradingShare * observedVolume.Value)
            Flag(FmvTrigger.ThinTrading, $"Receipt of {q:N0} OPT is {q / observedVolume.Value:P0} of the {observedVolume.Value:N0} OPT swapped on Osmosis that day.");
        foreach (var d in e.DepositRestrictions) Flag(FmvTrigger.DepositRestriction, d);
        if (e.OsmosisObservedUtc is null && !swapsOk)
            Flag(FmvTrigger.NoContemporaneousMarket, $"No Osmosis pool state within {policy.MaxMarketAgeHours:0} h before the receipt and no swaps that day.");
        if (e.ModeledPriceImpact is { } impact && impact > policy.LargeQuantityImpact)
            Flag(FmvTrigger.LargeQuantityEffect, $"Selling the whole receipt at once moves the modeled price by {impact:P1}; this is evidence for review, not a discount.");

        // --- candidate: best-supported price by source quality, not by level
        if (refOk)
        {
            c.Source = "reference"; c.UnitPriceUsd = e.ReferenceUnitUsd; c.Method = ReviewMethod.AcceptReference;
            c.ValueUsd = r.Reference.ValueUsd is { } rv ? Math.Round(rv, 2) : Math.Round(q * e.ReferenceUnitUsd!.Value, 2);
        }
        else if (poolOk)
        {
            c.Source = "osmosis-pool"; c.UnitPriceUsd = e.OsmosisSpotUsd; c.Method = ReviewMethod.AlternativeMarketPrice;
            c.ValueUsd = Math.Round(q * e.OsmosisSpotUsd!.Value, 2);
        }
        else if (swapsOk)
        {
            c.Source = "osmosis-swaps"; c.UnitPriceUsd = e.OsmosisDaySwapPriceUsd; c.Method = ReviewMethod.TimingApproximation;
            c.ValueUsd = Math.Round(q * e.OsmosisDaySwapPriceUsd!.Value, 2);
        }
        c.Qualifies = c.ValueUsd is not null && c.Flags.Count == 0;

        // --- what a reviewer can choose instead
        if (e.ReferenceUnitUsd is > 0)
            c.Options.Add(new FmvOption("reference", "CoinMarketCap reference", Math.Round(r.Reference.ValueUsd ?? q * e.ReferenceUnitUsd.Value, 2), e.ReferenceUnitUsd, ReviewMethod.AcceptReference, false, $"{e.ReferenceInterval} close"));
        if (poolOk)
            c.Options.Add(new FmvOption("osmosis-pool", "Osmosis pool price just before receipt", Math.Round(q * e.OsmosisSpotUsd!.Value, 2), e.OsmosisSpotUsd, ReviewMethod.AlternativeMarketPrice, c.Source != "osmosis-pool", $"{e.OsmosisRoute}, marginal price before fees"));
        if (swapsOk)
            c.Options.Add(new FmvOption("osmosis-swaps", "Osmosis average swap price that day", Math.Round(q * e.OsmosisDaySwapPriceUsd!.Value, 2), e.OsmosisDaySwapPriceUsd, ReviewMethod.TimingApproximation, true, $"{e.OsmosisDayTxs} swap(s), stablecoin-quoted, UTC day"));
        if (e.ModeledProceedsBeforeFeesUsd is > 0)
            c.Options.Add(new FmvOption("modeled-sale", "Modeled immediate sale of the whole receipt", Math.Round(e.ModeledProceedsBeforeFeesUsd.Value, 2), e.ModeledProceedsBeforeFeesUsd / q, ReviewMethod.HistoricalExecutionEvidence, true, $"price impact included, before fees; fill {e.ModeledFillRatio:P0}. Proceeds of an immediate sale are not automatically fair market value."));

        c.Explanation = c.Source switch
        {
            "reference" => $"Policy {policy.Version}: CoinMarketCap {e.ReferenceInterval} close {e.ReferenceUnitUsd:0.000000} USD/OPT accepted as the receipt-time market price.",
            "osmosis-pool" => $"Policy {policy.Version}: the reference failed validation ({string.Join(", ", c.Flags.Where(f => f.Trigger is FmvTrigger.ReferenceMissing or FmvTrigger.ReferenceStale).Select(f => f.Trigger))}); Osmosis pool marginal price {e.OsmosisSpotUsd:0.000000} USD/OPT just before receipt is the best-supported market price.",
            "osmosis-swaps" => $"Policy {policy.Version}: no valid reference or pool state at the receipt time; the day's average Osmosis swap price {e.OsmosisDaySwapPriceUsd:0.000000} USD/OPT is a timing approximation.",
            _ => $"Policy {policy.Version}: no supportable market price found; the value stays unresolved (not zero).",
        };
        if (c.Flags.Count > 0) c.Explanation += " Needs review: " + string.Join("; ", c.Flags.Select(f => f.Detail));
        return c;
    }
}

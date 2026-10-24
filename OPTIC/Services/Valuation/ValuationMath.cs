namespace OPTIC.Services.Valuation;

/// <summary>A value that may be unknown, with the reason it is unknown. Missing inputs never become zero.</summary>
public readonly record struct Known(decimal? Value, string? MissingReason)
{
    public static Known Of(decimal v) => new(v, null);
    public static Known Missing(string reason) => new(null, reason);
    public bool HasValue => Value.HasValue;
}

/// <summary>
/// The formulas from the methodology, in one place, in decimal arithmetic. Each returns null with a reason
/// when an input or denominator is missing rather than guessing.
/// </summary>
public static class ValuationMath
{
    public static Known Share(decimal? part, decimal? whole, string wholeName)
    {
        if (part is null) return Known.Missing("Personal quantity unavailable.");
        if (whole is null) return Known.Missing($"{wholeName} unavailable for this period.");
        if (whole.Value <= 0) return Known.Missing($"{wholeName} is zero or negative.");
        return Known.Of(part.Value / whole.Value);
    }

    /// <summary>ScenarioSellingOPT = SelectedNetworkDistributionOPT × ScenarioPercentage (percentage as a fraction).</summary>
    public static Known ScenarioSelling(decimal? networkDistributionOpt, decimal fraction)
    {
        if (fraction < 0 || fraction > 1) throw new ArgumentOutOfRangeException(nameof(fraction), "Scenario share must be between 0 and 1.");
        return networkDistributionOpt is null
            ? Known.Missing("No network distribution amount for this period; scenario not computed.")
            : Known.Of(networkDistributionOpt.Value * fraction);
    }

    public static Known NetMinted(decimal? minted, decimal? burned)
    {
        if (minted is null) return Known.Missing("Minted amount unavailable.");
        if (burned is null) return Known.Missing("Burned amount unavailable.");
        return Known.Of(minted.Value - burned.Value);
    }

    public static Known NetMintSupplyGrowth(decimal? netMinted, decimal? totalSupplyAtStart) =>
        netMinted is null ? Known.Missing("Net minted amount unavailable.") : Share(netMinted, totalSupplyAtStart, "Total supply at period start");

    public static decimal? ReferenceValueUsd(decimal receiptOpt, decimal? referenceUnitPriceUsd) =>
        referenceUnitPriceUsd is null ? null : receiptOpt * referenceUnitPriceUsd.Value;

    public static decimal? FillRatio(decimal requestedOpt, decimal executableOpt) =>
        requestedOpt <= 0 ? null : executableOpt / requestedOpt;

    /// <summary>Price on the filled quantity only. Never divide partial proceeds by the whole receipt.</summary>
    public static decimal? EffectiveExecutionPrice(decimal? modeledOutputUsd, decimal executableOpt) =>
        modeledOutputUsd is null || executableOpt <= 0 ? null : modeledOutputUsd.Value / executableOpt;

    /// <summary>
    /// Modeled execution shortfall = 1 - output / reference. Only for a fully modeled sale with both values in USD.
    /// Negative when the modeled output exceeds the reference. This is not a tax discount.
    /// </summary>
    public static Known ExecutionShortfall(decimal requestedOpt, decimal executableOpt, decimal? modeledOutputUsd, decimal? referenceValueUsd)
    {
        if (executableOpt < requestedOpt) return Known.Missing("Sale only partly modeled; shortfall is defined for a fully modeled sale only.");
        if (modeledOutputUsd is null) return Known.Missing("Modeled output has no documented USD conversion.");
        if (referenceValueUsd is null) return Known.Missing("No reference value.");
        if (referenceValueUsd.Value <= 0) return Known.Missing("Reference value is zero.");
        return Known.Of(1m - modeledOutputUsd.Value / referenceValueUsd.Value);
    }

    /// <summary>Converts a quote-asset amount to USD only with a documented rate for that asset and date.</summary>
    public static (decimal? Usd, string? Missing) ToUsd(decimal? amount, string quoteAsset, DateOnly date, IEnumerable<QuoteConversion> conversions)
    {
        if (amount is null) return (null, "No amount.");
        if (string.Equals(quoteAsset, "USD", StringComparison.Ordinal)) return (amount, null);
        var rate = conversions.FirstOrDefault(c => string.Equals(c.QuoteAsset, quoteAsset, StringComparison.Ordinal) && c.Date == date);
        return rate is null
            ? (null, $"No documented {quoteAsset}→USD conversion for {date:yyyy-MM-dd}; result kept in {quoteAsset}.")
            : (amount.Value * rate.UsdPerUnit, null);
    }

    public static readonly decimal[] DefaultScenarioFractions = { 0.01m, 0.05m, 0.10m };
}

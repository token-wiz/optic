using System.Globalization;
using System.Numerics;

namespace OPTIC.Services.Valuation;

public sealed class PoolTickDto
{
    public long Index { get; set; }
    public string LiquidityNet { get; set; } = "0";
}

public sealed class PoolAssetDto
{
    public string Denom { get; set; } = "";
    public string Amount { get; set; } = "0";
    public string? Weight { get; set; }
}

/// <summary>Historical state of one Osmosis pool at a verified block height.</summary>
public sealed class OsmosisPoolSnapshot
{
    public string Id { get; set; } = "";
    public string PoolId { get; set; } = "";
    public string PoolType { get; set; } = "unknown";      // concentrated, balancer, stableswap, cosmwasm, unknown
    public long RequestedHeight { get; set; }
    public long? ServedHeight { get; set; }
    public DateTime? BlockTimeUtc { get; set; }
    public long? CreatedHeight { get; set; }
    public bool Exists { get; set; } = true;
    public string? Token0 { get; set; }
    public string? Token1 { get; set; }
    public string? CurrentSqrtPrice { get; set; }
    public long? CurrentTick { get; set; }
    public string? CurrentLiquidity { get; set; }
    public List<PoolTickDto> Ticks { get; set; } = new();
    public bool TicksBelowComplete { get; set; }
    public bool TicksAboveComplete { get; set; }
    public string? SpreadFactor { get; set; }
    public List<PoolAssetDto> Assets { get; set; } = new();
    public string? SwapFee { get; set; }
    public string? TakerFee { get; set; }
    public string? TakerFeeSource { get; set; }
    public DateTime CollectedUtc { get; set; }
    public List<EvidenceRef> Evidence { get; set; } = new();
    public List<string> Problems { get; set; } = new();
    public string MathVersion { get; set; } = "osmosis e4ae1fcd (v25+ spread factor)";
}

/// <summary>Known Osmosis denoms. USDC from different issuers/bridges and USDT are separate quote assets.</summary>
public static class OsmosisAssets
{
    public const string OptDenom = "ibc/6AA47B1A9F00CC9B127796717F4BE976C12EFC843387F22461152888E1365045";

    public static readonly Dictionary<string, (string Asset, int Decimals)> Known = new(StringComparer.Ordinal)
    {
        [OptDenom] = ("OPT", 6),
        ["ibc/498A0751C798A0D9A389AA3691123DADA57DAA4FE165D5C75894505B876BA6E4"] = ("USDC (Noble)", 6),
        ["factory/osmo1em6xs47hd82806f5cxgyufguxrrc7l0aqx7nzzptjuqgswczk8csavdxek/alloyed/allUSDT"] = ("USDT (alloyed)", 6),
        ["factory/osmo1em6xs47hd82806f5cxgyufguxrrc7l0aqx7nzzptjuqgswczk8csavdxek/alloyed/allUSDC"] = ("USDC (alloyed)", 6),
    };

    public static bool IsQuote(string denom) => Known.TryGetValue(denom, out var a) && a.Asset != "OPT";
}

public sealed record RouteHop(OsmosisPoolSnapshot Pool, string TokenIn, string TokenOut);

/// <summary>
/// A route of one or more Osmosis pools at a historical height, as a market OPT can be sold into. Each Quote is one
/// atomic exact-input swap: if any hop cannot fill, nothing executes.
/// </summary>
public sealed class PoolRouteMarket : ISellMarket
{
    private readonly List<RouteHop> _hops;
    private readonly Dictionary<string, ClMath.State> _cl = new();
    private readonly Dictionary<string, List<(string Denom, BigInteger Amount, BigInteger Weight)>> _bal = new();
    private readonly int _outDecimals;

    public PoolRouteMarket(IReadOnlyList<RouteHop> hops)
    {
        _hops = hops.ToList();
        var last = _hops[^1].TokenOut;
        QuoteAsset = OsmosisAssets.Known.TryGetValue(last, out var q) ? q.Asset : last;
        _outDecimals = OsmosisAssets.Known.TryGetValue(last, out var qd) ? qd.Decimals : 6;
        foreach (var h in _hops)
        {
            var p = h.Pool;
            if (p.PoolType == "concentrated" && !_cl.ContainsKey(p.PoolId))
                _cl[p.PoolId] = new ClMath.State
                {
                    SqrtPrice = Fx.BigDec(p.CurrentSqrtPrice ?? "0"),
                    Tick = p.CurrentTick ?? 0,
                    Liquidity = Fx.Dec(p.CurrentLiquidity ?? "0"),
                    Ticks = p.Ticks.Select(t => new ClTick(t.Index, Fx.Dec(t.LiquidityNet))).OrderBy(t => t.Index).ToList(),
                    SpreadFactor = Fx.Dec(p.SpreadFactor ?? "0"),
                };
            else if (p.PoolType == "balancer" && !_bal.ContainsKey(p.PoolId))
                _bal[p.PoolId] = p.Assets.Select(a => (a.Denom, BigInteger.Parse(a.Amount, CultureInfo.InvariantCulture), Fx.Dec(a.Weight ?? "1"))).ToList();
        }
        ObservedUtc = _hops.Select(h => h.Pool.BlockTimeUtc ?? DateTime.MinValue).Max();
        Id = "osmosis:" + string.Join(">", _hops.Select(h => h.Pool.PoolId)) + "@" + _hops[0].Pool.ServedHeight;
    }

    private PoolRouteMarket(PoolRouteMarket o)
    {
        _hops = o._hops;
        QuoteAsset = o.QuoteAsset;
        _outDecimals = o._outDecimals;
        ObservedUtc = o.ObservedUtc;
        Id = o.Id;
        foreach (var (k, v) in o._cl) _cl[k] = v.Clone();
        foreach (var (k, v) in o._bal) _bal[k] = v.ToList();
    }

    public string Id { get; }
    public string Venue => "Osmosis";
    public bool OnChain => true;
    public DateTime ObservedUtc { get; }
    public string QuoteAsset { get; }
    public IReadOnlyCollection<string> PoolIds => _hops.Select(h => h.Pool.PoolId).ToList();

    public static string? Unsupported(IEnumerable<RouteHop> hops)
    {
        foreach (var h in hops)
        {
            if (h.Pool.PoolType is not ("concentrated" or "balancer"))
                return $"Pool {h.Pool.PoolId} is a {h.Pool.PoolType} pool; its swap math is not implemented.";
            if (h.Pool.PoolType == "concentrated" && h.TokenIn != h.Pool.Token0 && h.TokenIn != h.Pool.Token1)
                return $"Pool {h.Pool.PoolId} does not hold {h.TokenIn}.";
            if (h.Pool.PoolType == "concentrated")
            {
                var zeroForOne = h.TokenIn == h.Pool.Token0;
                if (zeroForOne ? !h.Pool.TicksBelowComplete : !h.Pool.TicksAboveComplete)
                    return $"Pool {h.Pool.PoolId}: tick data in the swap direction was not retrieved completely.";
            }
        }
        return null;
    }

    public ExecutionEstimate Quote(decimal quantityOpt) => Run(quantityOpt, commit: false);
    public ExecutionEstimate Execute(decimal quantityOpt) => Run(quantityOpt, commit: true);
    public ISellMarket Clone() => new PoolRouteMarket(this);

    public decimal? MarginalPrice()
    {
        // Output for a tiny sale (1 OPT) as the marginal price, before fees.
        var clone = new PoolRouteMarket(this);
        var e = clone.Run(1m, commit: false, ignoreFees: true);
        return e.ExecutableOpt > 0 ? e.OutputQuote : null;
    }

    private ExecutionEstimate Run(decimal quantityOpt, bool commit, bool ignoreFees = false)
    {
        var est = new ExecutionEstimate
        {
            RequestedOpt = quantityOpt, QuoteAsset = QuoteAsset, Venue = Venue,
            SnapshotIds = _hops.Select(h => h.Pool.Id).Distinct().ToList(),
            Routes = { string.Join(" → ", _hops.Select(h => $"pool {h.Pool.PoolId}")) + $" @ height {_hops[0].Pool.ServedHeight}" },
        };
        var unsupported = Unsupported(_hops);
        if (unsupported != null)
        {
            est.Outcome = AnalysisOutcome.UnsupportedPoolType;
            est.Notes.Add(unsupported);
            return est;
        }

        var amount = new BigInteger(decimal.Truncate(quantityOpt * 1_000_000m));
        // Always swap on a copy; state is written back only if every hop succeeds, so a failed route changes nothing.
        var working = new PoolRouteMarket(this);
        BigInteger takerFees = 0;
        try
        {
            foreach (var h in _hops)
            {
                var tf = ignoreFees ? BigInteger.Zero : Fx.Dec(h.Pool.TakerFee ?? "0");
                var inNet = Fx.DTruncateInt(Fx.DMul(Fx.D18 - tf, Fx.DecFromInt(amount)));
                takerFees += amount - inNet;
                if (h.Pool.PoolType == "concentrated")
                {
                    var st = working._cl[h.Pool.PoolId];
                    if (ignoreFees) st = new ClMath.State { SqrtPrice = st.SqrtPrice, Tick = st.Tick, Liquidity = st.Liquidity, Ticks = st.Ticks, SpreadFactor = 0 };
                    var r = ClMath.SwapExactIn(st, inNet, zeroForOne: h.TokenIn == h.Pool.Token0, commit: !ignoreFees, computeEndTick: commit);
                    amount = r.AmountOut;
                }
                else
                {
                    var assets = working._bal[h.Pool.PoolId];
                    var iIn = assets.FindIndex(a => a.Denom == h.TokenIn);
                    var iOut = assets.FindIndex(a => a.Denom == h.TokenOut);
                    if (iIn < 0 || iOut < 0) throw new SwapFailedException($"Pool {h.Pool.PoolId} does not hold both {h.TokenIn} and {h.TokenOut}.");
                    var fee = ignoreFees ? BigInteger.Zero : Fx.Dec(h.Pool.SwapFee ?? "0");
                    var outAmt = GammMath.BalancerOutGivenIn(assets[iIn].Amount, assets[iIn].Weight, assets[iOut].Amount, assets[iOut].Weight, inNet, fee);
                    if (!ignoreFees)
                    {
                        assets[iIn] = assets[iIn] with { Amount = assets[iIn].Amount + inNet };
                        assets[iOut] = assets[iOut] with { Amount = assets[iOut].Amount - outAmt };
                    }
                    amount = outAmt;
                }
            }
        }
        catch (SwapFailedException ex)
        {
            est.ExecutableOpt = 0;
            est.OutputQuote = null;
            est.Outcome = AnalysisOutcome.PartialCapacity;
            est.Notes.Add("Exact-input swap fails at this size: " + ex.Message + " A smaller trade is evaluated separately.");
            return est;
        }

        if (!ignoreFees && commit)
        {
            // The same sale with price impact only, on the state before this sale, for fair-market-value use.
            var noFee = new PoolRouteMarket(this).Run(quantityOpt, commit: false, ignoreFees: true);
            est.OutputBeforeFeesQuote = noFee.ExecutableOpt > 0 ? noFee.OutputQuote : null;
        }
        if (commit && !ignoreFees)
        {
            foreach (var (k, v) in working._cl) _cl[k] = v;
            foreach (var (k, v) in working._bal) _bal[k] = v;
        }
        est.ExecutableOpt = quantityOpt;
        est.OutputQuote = (decimal)amount / Pow10(_outDecimals);
        est.Outcome = AnalysisOutcome.SupportedHistoricalEstimate;
        if (!ignoreFees)
        {
            if (_hops.Any(h => h.Pool.TakerFee is null)) est.Assumptions.Add("Taker fee unknown at this height; modeled as 0 (output may be overstated).");
            else est.Assumptions.Add($"Taker fee charged before each pool: {string.Join(", ", _hops.Select(h => h.Pool.TakerFee + " (" + h.Pool.TakerFeeSource + ")"))}.");
        }
        return est;
    }

    private static decimal Pow10(int n) { var r = 1m; for (var i = 0; i < n; i++) r *= 10m; return r; }

    /// <summary>
    /// Direct and two-hop routes from OPT to a quote asset among pools observed at the same height.
    /// Pools before their verified creation height, or not existing at the height, are left out with a reason.
    /// </summary>
    public static (List<List<RouteHop>> Routes, List<string> Excluded) DiscoverRoutes(IReadOnlyList<OsmosisPoolSnapshot> pools, long height)
    {
        var excluded = new List<string>();
        var usable = new List<OsmosisPoolSnapshot>();
        foreach (var p in pools)
        {
            if (p.ServedHeight != height) { excluded.Add($"Pool {p.PoolId}: snapshot height {p.ServedHeight?.ToString(CultureInfo.InvariantCulture) ?? "unverified"} ≠ {height}."); continue; }
            if (!p.Exists) { excluded.Add($"Pool {p.PoolId}: did not exist at height {height}."); continue; }
            if (p.CreatedHeight is long c && c > height) { excluded.Add($"Pool {p.PoolId}: created at height {c}, after {height}."); continue; }
            usable.Add(p);
        }
        IEnumerable<string> Denoms(OsmosisPoolSnapshot p) => p.PoolType == "concentrated" ? new[] { p.Token0!, p.Token1! }.Where(d => d != null) : p.Assets.Select(a => a.Denom);
        var routes = new List<List<RouteHop>>();
        foreach (var p in usable.Where(p => Denoms(p).Contains(OsmosisAssets.OptDenom)))
        {
            foreach (var other in Denoms(p).Where(d => d != OsmosisAssets.OptDenom))
            {
                if (OsmosisAssets.IsQuote(other)) routes.Add(new() { new RouteHop(p, OsmosisAssets.OptDenom, other) });
                foreach (var q in usable.Where(q => q.PoolId != p.PoolId && Denoms(q).Contains(other)))
                    foreach (var quote in Denoms(q).Where(OsmosisAssets.IsQuote).Where(d => d != other))
                        routes.Add(new() { new RouteHop(p, OsmosisAssets.OptDenom, other), new RouteHop(q, other, quote) });
            }
        }
        return (routes, excluded);
    }
}

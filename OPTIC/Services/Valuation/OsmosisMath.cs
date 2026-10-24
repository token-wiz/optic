using System.Globalization;
using System.Numerics;

namespace OPTIC.Services.Valuation;

/// <summary>
/// Fixed-point arithmetic matching osmomath: Dec has 18 decimal places, BigDec 36. Values are BigIntegers holding
/// the scaled amount. Rounding follows osmosis-labs/osmosis osmomath/decimal.go (truncate, round up, or
/// round half to even for plain Mul/Quo). All amounts in the swap paths are non-negative.
/// </summary>
public static class Fx
{
    public static readonly BigInteger D18 = BigInteger.Pow(10, 18);
    public static readonly BigInteger D36 = BigInteger.Pow(10, 36);

    public static BigInteger ParseScaled(string s, int scale)
    {
        s = s.Trim();
        var neg = s.StartsWith('-');
        if (neg) s = s[1..];
        var parts = s.Split('.');
        var frac = parts.Length > 1 ? parts[1] : "";
        if (frac.Length > scale) frac = frac[..scale]; // osmomath strings never exceed their precision
        var digits = parts[0] + frac.PadRight(scale, '0');
        var v = BigInteger.Parse(digits.Length == 0 ? "0" : digits, CultureInfo.InvariantCulture);
        return neg ? -v : v;
    }

    public static BigInteger Dec(string s) => ParseScaled(s, 18);
    public static BigInteger BigDec(string s) => ParseScaled(s, 36);
    public static BigInteger DecFromInt(BigInteger i) => i * D18;
    public static BigInteger BigDecFromDec(BigInteger dec) => dec * D18;

    public static string Format(BigInteger v, int scale)
    {
        var neg = v.Sign < 0;
        var s = BigInteger.Abs(v).ToString(CultureInfo.InvariantCulture).PadLeft(scale + 1, '0');
        var r = s[..^scale] + "." + s[^scale..];
        return (neg ? "-" : "") + r;
    }

    public static decimal ToDecimal(BigInteger v, int scale)
    {
        // Through string to keep exactness up to decimal's 28-29 significant digits.
        return decimal.Parse(Format(v, scale).TrimEnd('0').TrimEnd('.') is var t && t.Length > 0 ? t : "0", NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    public static BigInteger FloorDiv(BigInteger a, BigInteger b) => BigInteger.DivRem(a, b, out var r) is var q && r.Sign != 0 && (r.Sign < 0) != (b.Sign < 0) ? q - 1 : q;
    public static BigInteger CeilDiv(BigInteger a, BigInteger b) => BigInteger.DivRem(a, b, out var r) is var q && r.Sign != 0 && (r.Sign > 0) == (b.Sign > 0) ? q + 1 : q;

    /// <summary>a / b rounded half to even (chopPrecisionAndRound).</summary>
    public static BigInteger BankersDiv(BigInteger a, BigInteger b)
    {
        var q = BigInteger.DivRem(a, b, out var r);
        if (r.IsZero) return q;
        var twice = BigInteger.Abs(r) * 2;
        var bb = BigInteger.Abs(b);
        var sign = (a.Sign < 0) != (b.Sign < 0) ? -1 : 1;
        if (twice > bb || (twice == bb && !q.IsEven)) q += sign;
        return q;
    }

    // ---- BigDec (scale 36) x BigDec
    public static BigInteger BMul(BigInteger a, BigInteger b) => BankersDiv(a * b, D36);
    public static BigInteger BMulTruncate(BigInteger a, BigInteger b) => FloorDiv(a * b, D36);
    public static BigInteger BMulRoundUp(BigInteger a, BigInteger b) => CeilDiv(a * b, D36);
    public static BigInteger BQuo(BigInteger a, BigInteger b) => BankersDiv(a * D36, b);
    public static BigInteger BQuoTruncate(BigInteger a, BigInteger b) => FloorDiv(a * D36, b);
    public static BigInteger BQuoRoundUp(BigInteger a, BigInteger b) => CeilDiv(a * D36, b);
    /// <summary>ceil(a / b) as a whole number, returned as BigDec.</summary>
    public static BigInteger BQuoRoundUpNextInt(BigInteger a, BigInteger b) => CeilDiv(a, b) * D36;

    // ---- BigDec x Dec (scale 18 operand)
    public static BigInteger BMulDec(BigInteger a, BigInteger dec) => BankersDiv(a * dec, D18);
    public static BigInteger BMulTruncateDec(BigInteger a, BigInteger dec) => FloorDiv(a * dec, D18);
    public static BigInteger BMulRoundUpDec(BigInteger a, BigInteger dec) => CeilDiv(a * dec, D18);
    public static BigInteger BQuoTruncateDec(BigInteger a, BigInteger dec) => FloorDiv(a * D18, dec);

    /// <summary>BigDec → Dec, truncating.</summary>
    public static BigInteger BToDec(BigInteger a) => FloorDiv(a, D18);
    /// <summary>BigDec → Dec, rounding up.</summary>
    public static BigInteger BToDecRoundUp(BigInteger a) => CeilDiv(a, D18);
    /// <summary>BigDec rounded up to a whole number (still BigDec).</summary>
    public static BigInteger BCeil(BigInteger a) => CeilDiv(a, D36) * D36;

    // ---- Dec (scale 18)
    public static BigInteger DMul(BigInteger a, BigInteger b) => BankersDiv(a * b, D18);
    public static BigInteger DMulTruncate(BigInteger a, BigInteger b) => FloorDiv(a * b, D18);
    public static BigInteger DMulRoundUp(BigInteger a, BigInteger b) => CeilDiv(a * b, D18);
    public static BigInteger DQuo(BigInteger a, BigInteger b) => BankersDiv(a * D18, b);
    public static BigInteger DQuoRoundUp(BigInteger a, BigInteger b) => CeilDiv(a * D18, b);
    public static BigInteger DTruncateInt(BigInteger dec) => FloorDiv(dec, D18);
    public static BigInteger DCeilInt(BigInteger dec) => CeilDiv(dec, D18);

    public static BigInteger ISqrt(BigInteger n)
    {
        if (n.Sign <= 0) return BigInteger.Zero;
        var x = (BigInteger)Math.Sqrt((double)n);
        // Newton correction from the double estimate.
        while (true)
        {
            var y = (x + n / x) >> 1;
            if (BigInteger.Abs(y - x) <= 1)
            {
                x = y;
                break;
            }
            x = y;
        }
        while (x * x > n) x--;
        while ((x + 1) * (x + 1) <= n) x++;
        return x;
    }

    /// <summary>MonotonicSqrt for Dec: the smallest 18-dp r with r² ≥ d.</summary>
    public static BigInteger MonotonicSqrtDec(BigInteger dec)
    {
        var n = dec * D18;
        var r = ISqrt(n);
        return r * r < n ? r + 1 : r;
    }

    /// <summary>MonotonicSqrtBigDec: the smallest 36-dp r with r² ≥ d.</summary>
    public static BigInteger MonotonicSqrtBigDec(BigInteger bigDec)
    {
        var n = bigDec * D36;
        var r = ISqrt(n);
        return r * r < n ? r + 1 : r;
    }
}

public sealed class SwapFailedException : Exception
{
    public SwapFailedException(string message) : base(message) { }
}

/// <summary>One initialized tick of a concentrated-liquidity pool.</summary>
public sealed record ClTick(long Index, BigInteger LiquidityNet /* Dec */);

/// <summary>
/// Concentrated-liquidity swap math (exact amount in), following osmosis-labs/osmosis
/// x/concentrated-liquidity at e4ae1fcd (v25+ spread-factor formula). See docs for line references.
/// </summary>
public static class ClMath
{
    public const long TicksPerDecade = 9_000_000;
    public const int ExponentAtPriceOne = -6;
    public const long MinInitializedTick = -108_000_000;
    public const long MaxTick = 342_000_000;
    public static readonly BigInteger MinSqrtPriceBig = Fx.BigDec("0.000001");          // sqrt(1e-12)
    public static readonly BigInteger MaxSqrtPriceBig = Fx.BigDec("10000000000000000000"); // sqrt(1e38)

    /// <summary>Exact tick price as BigDec.</summary>
    public static BigInteger TickToPrice(long tick)
    {
        if (tick == 0) return Fx.D36;
        var geo = tick / TicksPerDecade; // truncates toward zero, as Go does
        var add = tick - geo * TicksPerDecade;
        var exp = ExponentAtPriceOne + (int)geo;
        BigInteger unscaled = 1_000_000;
        if (tick < 0) { exp -= 1; unscaled = 10_000_000; }
        var mantissa = unscaled + add;
        var shift = 36 + exp;
        return shift >= 0 ? mantissa * BigInteger.Pow(10, shift) : mantissa / BigInteger.Pow(10, -shift);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, BigInteger> SqrtCache = new();

    /// <summary>Tick sqrt price as BigDec: MonotonicSqrt of the 18-dp price for ticks ≥ MinInitializedTick. Cached; it never changes.</summary>
    public static BigInteger TickToSqrtPrice(long tick) => SqrtCache.Count > 2_000_000 ? ComputeTickSqrt(tick) : SqrtCache.GetOrAdd(tick, ComputeTickSqrt);

    private static BigInteger ComputeTickSqrt(long tick)
    {
        var price = TickToPrice(tick);
        if (tick >= MinInitializedTick)
            return Fx.BigDecFromDec(Fx.MonotonicSqrtDec(Fx.BToDec(price)));
        return Fx.MonotonicSqrtBigDec(price);
    }

    /// <summary>The tick whose bucket holds this sqrt price: largest t with TickToSqrtPrice(t) ≤ sqrt.</summary>
    public static long SqrtPriceToTick(BigInteger sqrt)
    {
        long lo = MinInitializedTick - 1, hi = MaxTick;
        while (lo < hi)
        {
            var mid = lo + (hi - lo + 1) / 2;
            if (TickToSqrtPrice(mid) <= sqrt) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    // ---- deltas (A < B are sqrt prices as BigDec, L is Dec); results BigDec
    public static BigInteger Amount0Delta(BigInteger L, BigInteger s1, BigInteger s2, bool roundUp)
    {
        var (a, b) = s1 < s2 ? (s1, s2) : (s2, s1);
        var diff = b - a;
        return roundUp
            ? Fx.BQuoRoundUpNextInt(Fx.BQuoRoundUp(Fx.BMulRoundUpDec(diff, L), b), a)
            : Fx.BQuoTruncate(Fx.BQuoTruncate(Fx.BMulTruncateDec(diff, L), b), a);
    }

    public static BigInteger Amount1Delta(BigInteger L, BigInteger s1, BigInteger s2, bool roundUp)
    {
        var (a, b) = s1 < s2 ? (s1, s2) : (s2, s1);
        var diff = b - a;
        return roundUp ? Fx.BCeil(Fx.BMulDec(diff, L)) : Fx.BMulTruncateDec(diff, L);
    }

    public static BigInteger NextSqrtFromAmount0InRoundingUp(BigInteger s, BigInteger L, BigInteger amt)
    {
        var lb = Fx.BigDecFromDec(L);
        return Fx.BQuoRoundUp(Fx.BMulRoundUp(lb, s), Fx.BMulTruncate(amt, s) + lb);
    }

    public static BigInteger NextSqrtFromAmount1InRoundingDown(BigInteger s, BigInteger L, BigInteger amt) =>
        s + Fx.BQuoTruncateDec(amt, L);

    public sealed class State
    {
        public BigInteger SqrtPrice;      // BigDec
        public long Tick;
        public BigInteger Liquidity;      // Dec
        public List<ClTick> Ticks = new(); // sorted by index
        public BigInteger SpreadFactor;   // Dec
        public bool LegacyFee;            // pre-v25 spread-factor rounding
        public State Clone() => new() { SqrtPrice = SqrtPrice, Tick = Tick, Liquidity = Liquidity, Ticks = Ticks.ToList(), SpreadFactor = SpreadFactor, LegacyFee = LegacyFee };
    }

    public sealed record SwapResult(BigInteger AmountIn, BigInteger AmountOut, BigInteger SpreadFeesDec, int TicksCrossed, BigInteger EndSqrtPrice, long EndTick, BigInteger EndLiquidity);

    /// <summary>
    /// Exact-in swap. zeroForOne = token0 in. Throws SwapFailedException where Osmosis would fail the transaction
    /// (ran out of ticks / liquidity before the input was used, or zero output), so no partial fill is invented.
    /// The state is updated only when <paramref name="commit"/> is true.
    /// </summary>
    public static SwapResult SwapExactIn(State st, BigInteger amountIn, bool zeroForOne, bool commit = false, bool computeEndTick = true)
    {
        var s = commit ? st : st.Clone();
        var sf = s.SpreadFactor;
        var oneMinusSf = Fx.D18 - sf;
        var remaining = Fx.DecFromInt(amountIn); // Dec
        var calculated = BigInteger.Zero;        // BigDec... accumulate amtOut as Dec
        var fees = BigInteger.Zero;
        var crossed = 0;
        var limit = zeroForOne ? MinSqrtPriceBig : MaxSqrtPriceBig;
        var zeroSteps = 0;

        while (remaining > 1 && s.SqrtPrice != limit)
        {
            ClTick? next = zeroForOne
                ? s.Ticks.LastOrDefault(t => t.Index <= s.Tick)
                : s.Ticks.FirstOrDefault(t => t.Index > s.Tick);
            if (next is null) throw new SwapFailedException("Ran out of initialized ticks before the input was used: the pool could not absorb this exact-input swap.");
            var tickSqrt = TickToSqrtPrice(next.Index);
            var target = zeroForOne ? BigInteger.Max(tickSqrt, limit) : BigInteger.Min(tickSqrt, limit);

            BigInteger amtInToTarget = zeroForOne ? Amount0Delta(s.Liquidity, target, s.SqrtPrice, true) : Amount1Delta(s.Liquidity, s.SqrtPrice, target, true);
            var remLessFee = remaining * oneMinusSf; // Dec × Dec = BigDec exactly
            BigInteger nextSqrt;
            if (s.Liquidity.IsZero) nextSqrt = target;
            else if (remLessFee >= amtInToTarget) nextSqrt = target;
            else nextSqrt = zeroForOne ? NextSqrtFromAmount0InRoundingUp(s.SqrtPrice, s.Liquidity, remLessFee) : NextSqrtFromAmount1InRoundingDown(s.SqrtPrice, s.Liquidity, remLessFee);
            var reached = nextSqrt == target;
            var amtIn = reached ? amtInToTarget : (zeroForOne ? Amount0Delta(s.Liquidity, nextSqrt, s.SqrtPrice, true) : Amount1Delta(s.Liquidity, s.SqrtPrice, nextSqrt, true));
            if (s.Liquidity.IsZero) amtIn = BigInteger.Zero;
            var amtOut = Fx.BToDec(zeroForOne ? Amount1Delta(s.Liquidity, nextSqrt, s.SqrtPrice, false) : Amount0Delta(s.Liquidity, s.SqrtPrice, nextSqrt, false));
            var amtInFinal = Fx.BToDecRoundUp(amtIn);
            BigInteger fee;
            if (sf.IsZero) fee = BigInteger.Zero;
            else if (reached)
                fee = s.LegacyFee ? Fx.DQuoRoundUp(Fx.DMulRoundUp(amtInFinal, sf), oneMinusSf) : Fx.DMulRoundUp(amtInFinal, Fx.DQuoRoundUp(sf, oneMinusSf));
            else fee = remaining - amtInFinal;

            if (amtInFinal.IsZero && amtOut.IsZero && !reached)
            {
                if (++zeroSteps > 100) throw new SwapFailedException("Swap made no progress (too many zero-amount steps).");
            }

            remaining -= amtInFinal + fee;
            calculated += amtOut;
            fees += fee;
            if (remaining < 0) throw new SwapFailedException("Computed input exceeded the amount given.");

            if (reached && nextSqrt == tickSqrt)
            {
                s.Liquidity += zeroForOne ? -next.LiquidityNet : next.LiquidityNet;
                s.Tick = zeroForOne ? next.Index - 1 : next.Index;
                crossed++;
            }
            else if (computeEndTick && (!reached || nextSqrt != s.SqrtPrice))
            {
                // Only the final step lands mid-bucket; its tick matters for later swaps on the same state.
                s.Tick = SqrtPriceToTick(nextSqrt);
            }
            s.SqrtPrice = nextSqrt;
        }

        if (remaining > 1)
            throw new SwapFailedException("Reached the pool's price limit before the input was used; treated as a failed exact-input swap.");
        var inUsed = Fx.DCeilInt(Fx.DecFromInt(amountIn) - remaining);
        var outInt = Fx.DTruncateInt(calculated);
        if (outInt.Sign <= 0) throw new SwapFailedException("Swap would give zero output.");
        if (commit is false) { /* nothing to write back */ }
        return new SwapResult(inUsed, outInt, fees, crossed, s.SqrtPrice, s.Tick, s.Liquidity);
    }
}

/// <summary>Balancer (weighted) and stableswap pool math from x/gamm.</summary>
public static class GammMath
{
    private static readonly BigInteger PowPrecision = Fx.Dec("0.00000001");

    /// <summary>
    /// calcOutGivenIn for weighted pools (balancer/amm.go): out = trunc(balOut × (1 − (balIn / (balIn + in×(1−fee)))^(wIn/wOut))).
    /// Balances and amounts in base units; weights and fee as Dec.
    /// </summary>
    public static BigInteger BalancerOutGivenIn(BigInteger balIn, BigInteger wIn, BigInteger balOut, BigInteger wOut, BigInteger amountIn, BigInteger swapFeeDec)
    {
        var inAfterFee = Fx.DMul(Fx.DecFromInt(amountIn), Fx.D18 - swapFeeDec);
        var y = Fx.DQuo(Fx.DecFromInt(balIn), Fx.DecFromInt(balIn) + inAfterFee);
        var exponent = Fx.DQuo(wIn, wOut);
        var pow = Pow(y, exponent);
        var outDec = Fx.DMul(Fx.DecFromInt(balOut), Fx.D18 - pow);
        var r = Fx.DTruncateInt(outDec);
        if (r.Sign <= 0) throw new SwapFailedException("Balancer swap would give zero output.");
        if (r >= balOut) throw new SwapFailedException("Balancer swap would drain the pool.");
        return r;
    }

    /// <summary>osmomath.Pow: integer part by repeated multiplication, fractional part by PowApprox.</summary>
    public static BigInteger Pow(BigInteger baseDec, BigInteger expDec)
    {
        if (baseDec.Sign <= 0 || baseDec >= 2 * Fx.D18) throw new ArgumentOutOfRangeException(nameof(baseDec), "Pow base must be in (0, 2).");
        var integer = Fx.FloorDiv(expDec, Fx.D18);
        var fractional = expDec - integer * Fx.D18;
        var result = Fx.D18;
        for (var i = 0; i < (int)integer; i++) result = Fx.DMul(result, baseDec);
        if (fractional.IsZero) return result;
        return Fx.DMul(result, PowApprox(baseDec, fractional, PowPrecision));
    }

    /// <summary>osmomath.PowApprox: binomial series (1 + x)^a, summed until a term falls below precision.</summary>
    public static BigInteger PowApprox(BigInteger baseDec, BigInteger expDec, BigInteger precision)
    {
        if (expDec.IsZero) return Fx.D18;
        if (expDec == Fx.D18 / 2) return Fx.MonotonicSqrtDec(baseDec); // ApproxSqrt path for exponent 0.5
        var x = BigInteger.Abs(baseDec - Fx.D18);
        var xneg = baseDec < Fx.D18;
        BigInteger term = Fx.D18, sum = Fx.D18, a = expDec, bigK = 0;
        var negative = false;
        for (long i = 1; term >= precision; i++)
        {
            var c = BigInteger.Abs(a - bigK);
            var cneg = a < bigK;
            bigK = i * Fx.D18;
            term = Fx.DQuo(Fx.DMul(Fx.DMul(term, c), x), bigK);
            if (term.IsZero) break;
            if (xneg) negative = !negative;
            if (cneg) negative = !negative;
            sum = negative ? sum - term : sum + term;
            if (i > 300) break;
        }
        return sum;
    }
}

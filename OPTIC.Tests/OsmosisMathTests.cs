using System.Numerics;

namespace OPTIC.Tests;

// Fixtures copied from osmosis-labs/osmosis @ e4ae1fcd:
//   app/apptesting/concentrated_liquidity.go 65–94 (pool setup)
//   x/concentrated-liquidity/swaps_test.go 48–88, 130–166, 736–770, 775–822, 824–878
//   x/concentrated-liquidity/math/tick_test.go 60–123
//   x/gamm/keeper/swap_test.go 39–68, app/apptesting/gamm.go 29–46
public class OsmosisMathTests
{
    // One position of 1,000,000 eth (token0) + 5,000,000,000 usdc (token1) in ticks [30545000, 31500000] at price 5000.
    static readonly BigInteger L1 = Fx.Dec("1517882343.751510417627556287");
    static readonly BigInteger L2 = Fx.Dec("1198735489.597250295669959397");

    static ClMath.State Pool(BigInteger liquidity, string spreadFactor, params ClTick[] ticks) => new()
    {
        SqrtPrice = Fx.BigDec("70.710678118654752441"),
        Tick = 31_000_000,
        Liquidity = liquidity,
        Ticks = ticks.OrderBy(t => t.Index).ToList(),
        SpreadFactor = Fx.Dec(spreadFactor),
    };

    // The repo compares these end sqrt prices with a tolerance (swaps_test.go 34–39); output amounts above are exact.
    static void AssertSqrt(string expected, BigInteger actual) =>
        Assert.True(BigInteger.Abs(Fx.BigDec(expected) - actual) <= Fx.BigDec("0.00000000000000001"), $"sqrt {Fx.Format(actual, 36)} vs {expected}");

    [Theory]
    [InlineData(-51630100L, "0.0000033699")]
    [InlineData(-11999800L, "0.070002")]
    [InlineData(27732100L, "1732.1")]
    [InlineData(36073200L, "10732")]
    [InlineData(342000000L, "100000000000000000000000000000000000000")]
    [InlineData(-1L, "0.9999999")]
    [InlineData(31000000L, "5000")]
    public void TickToPrice_matches_repo(long tick, string price)
    {
        Assert.Equal(Fx.BigDec(price), ClMath.TickToPrice(tick));
    }

    [Fact]
    public void Tick_sqrt_of_4545_matches_repo()
    {
        Assert.Equal(Fx.BigDec("67.416615162732695594"), ClMath.TickToSqrtPrice(30_545_000));
    }

    [Fact]
    public void ZeroForOne_single_range_no_fee()
    {
        var p = Pool(L1, "0", new ClTick(30_545_000, L1), new ClTick(31_500_000, -L1));
        var r = ClMath.SwapExactIn(p, 13370, zeroForOne: true);
        Assert.Equal(new BigInteger(66808388), r.AmountOut);
        Assert.Equal(Fx.BigDec("70.666663910857144332134093938182290274"), r.EndSqrtPrice);
        Assert.Equal(30_993_777, r.EndTick);
    }

    [Fact]
    public void OneForZero_single_range_no_fee()
    {
        var p = Pool(L1, "0", new ClTick(30_545_000, L1), new ClTick(31_500_000, -L1));
        var r = ClMath.SwapExactIn(p, 42_000_000, zeroForOne: false);
        Assert.Equal(new BigInteger(8396), r.AmountOut);
        Assert.Equal(Fx.BigDec("70.738348247484497718514800000003600626"), r.EndSqrtPrice);
        Assert.Equal(31_003_913, r.EndTick);
    }

    [Fact]
    public void OneForZero_with_spread_factor()
    {
        var p = Pool(L1, "0.01", new ClTick(30_545_000, L1), new ClTick(31_500_000, -L1));
        var r = ClMath.SwapExactIn(p, 42_000_000, zeroForOne: false);
        Assert.Equal(new BigInteger(8312), r.AmountOut);
        AssertSqrt("70.738071546196200264", r.EndSqrtPrice);
    }

    [Fact]
    public void ZeroForOne_two_identical_positions_with_spread_factor()
    {
        var p = Pool(2 * L1, "0.03", new ClTick(30_545_000, 2 * L1), new ClTick(31_500_000, -2 * L1));
        var r = ClMath.SwapExactIn(p, 13370, zeroForOne: true);
        Assert.Equal(new BigInteger(64824917), r.AmountOut);
        AssertSqrt("70.689324382628080102", r.EndSqrtPrice);
    }

    [Fact]
    public void ZeroForOne_crosses_into_adjacent_range()
    {
        var p = Pool(L1, "0.05",
            new ClTick(30_000_000, L2),
            new ClTick(30_545_000, L1 - L2),
            new ClTick(31_500_000, -L1));
        var r = ClMath.SwapExactIn(p, 2_000_000, zeroForOne: true);
        Assert.Equal(new BigInteger(8691708221), r.AmountOut);
        AssertSqrt("64.336946417392457832", r.EndSqrtPrice);
        // The repo lists ExpectedTick 30139200 here, but swapOutGivenInSpreadRewardCases are only run through the spread-reward
        // checks (swaps_test.go 2971–3061), never testSwapResult, so that value is unasserted. The tick follows the
        // CalculateSqrtPriceToTick bucket rule (math/tick.go), which the asserted no-fee vectors above match exactly.
        Assert.Equal(ClMath.SqrtPriceToTick(r.EndSqrtPrice), r.EndTick);
        Assert.True(ClMath.TickToSqrtPrice(r.EndTick) <= r.EndSqrtPrice && r.EndSqrtPrice < ClMath.TickToSqrtPrice(r.EndTick + 1));
        Assert.Equal(L2, r.EndLiquidity);
        Assert.Equal(1, r.TicksCrossed);
    }

    [Fact]
    public void Running_out_of_ticks_fails_the_whole_swap()
    {
        // Selling far more than the single range holds: Osmosis errors and reverts; no partial fill is reported.
        var p = Pool(L1, "0", new ClTick(30_545_000, L1), new ClTick(31_500_000, -L1));
        Assert.Throws<SwapFailedException>(() => ClMath.SwapExactIn(p, 100_000_000_000, zeroForOne: true));
    }

    [Fact]
    public void Quote_does_not_change_state_but_commit_does()
    {
        var p = Pool(L1, "0", new ClTick(30_545_000, L1), new ClTick(31_500_000, -L1));
        var before = p.SqrtPrice;
        ClMath.SwapExactIn(p, 13370, zeroForOne: true);
        Assert.Equal(before, p.SqrtPrice);
        ClMath.SwapExactIn(p, 13370, zeroForOne: true, commit: true);
        Assert.NotEqual(before, p.SqrtPrice);
    }

    [Fact]
    public void Balancer_out_given_in_matches_repo()
    {
        // foo weight 100 / 5,000,000; bar weight 200 / 5,000,000; 100,000 foo in.
        var w100 = Fx.Dec("100");
        var w200 = Fx.Dec("200");
        Assert.Equal(new BigInteger(49262), GammMath.BalancerOutGivenIn(5_000_000, w100, 5_000_000, w200, 100_000, Fx.Dec("0")));
        Assert.Equal(new BigInteger(46833), GammMath.BalancerOutGivenIn(5_000_000, w100, 5_000_000, w200, 100_000, Fx.Dec("0.05")));
    }

    [Fact]
    public void Balancer_equal_weights_is_constant_product()
    {
        // Equal weights: exponent 1, so out = balOut × in' / (balIn + in') exactly, truncated.
        var w = Fx.Dec("536870912000000");
        var outAmt = GammMath.BalancerOutGivenIn(139_330_142, w, 25_696, w, 1_000_000, Fx.Dec("0.003"));
        var inAfter = 1_000_000m * 0.997m;
        var expected = decimal.Truncate(25_696m * (1m - 139_330_142m / (139_330_142m + inAfter)));
        Assert.Equal(new BigInteger(expected), outAmt);
    }
}

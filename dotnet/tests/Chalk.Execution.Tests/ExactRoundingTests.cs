using System.Globalization;
using System.Numerics;
using Chalk.Execution.Numeric;

namespace Chalk.Execution.Tests;

/// <summary>
/// <c>ExactRounding</c> (F134) against an arbitrary-precision oracle: rounding to a number of digits is
/// done on the input's exact value, and the result is the nearest representable number — the
/// semantics .NET 11's <c>Math.Round</c> adopted, on every runtime. The named cases are the ones the
/// scaled algorithm gets wrong; the sweep is the regression guard.
/// </summary>
public sealed class ExactRoundingTests
{
    // ---- the cases .NET's own breaking-change note names ----

    [Fact]
    public void The_dotnet_11_examples()
    {
        Assert.Equal(655.92, ExactRounding.Round(655.925, 2, MidpointRounding.AwayFromZero));
        Assert.Equal(1111111111111111.5, ExactRounding.Round(1111111111111111.5, 1, MidpointRounding.AwayFromZero));
        Assert.Equal(1.5, ExactRounding.Round(1.5, 16, MidpointRounding.ToEven));
    }

    // ---- midpoints, by rule ----

    [Theory]
    [InlineData(2.5, 0, 3.0)]
    [InlineData(-2.5, 0, -3.0)]
    [InlineData(0.5, 0, 1.0)]
    [InlineData(-0.5, 0, -1.0)]
    [InlineData(1.5, 0, 2.0)]
    [InlineData(0.125, 2, 0.13)]      // exactly representable midpoint: rounds away
    [InlineData(-0.125, 2, -0.13)]
    [InlineData(2.675, 2, 2.67)]      // the double is 2.67499999999999982…
    [InlineData(1.005, 2, 1.0)]       // 1.00499999999999989…
    [InlineData(0.285, 2, 0.28)]      // 0.28499999999999997…
    [InlineData(0.145, 2, 0.14)]      // 0.14499999999999999…
    [InlineData(1.115, 2, 1.11)]      // 1.11499999999999999…
    [InlineData(5.015, 2, 5.01)]      // 5.01499999999999968…
    [InlineData(2.345, 2, 2.35)]      // 2.34500000000000019…: above the midpoint
    [InlineData(1234.5678, -2, 1200.0)]
    [InlineData(1250.0, -2, 1300.0)]
    [InlineData(-1250.0, -2, -1300.0)]
    [InlineData(1e20, -2, 1e20)]
    [InlineData(0.3, -1, 0.0)]
    public void Half_away_from_zero_on_the_exact_value(double value, int digits, double expected)
    {
        Assert.Equal(expected, ExactRounding.Round(value, digits, MidpointRounding.AwayFromZero));
        Assert.Equal(Oracle(value, digits, MidpointRounding.AwayFromZero), ExactRounding.Round(value, digits, MidpointRounding.AwayFromZero));
    }

    [Theory]
    [InlineData(2.5, 0, 2.0)]
    [InlineData(3.5, 0, 4.0)]
    [InlineData(-2.5, 0, -2.0)]
    [InlineData(0.125, 2, 0.12)]
    [InlineData(0.375, 2, 0.38)]
    [InlineData(1250.0, -2, 1200.0)]
    [InlineData(1350.0, -2, 1400.0)]
    public void Half_to_even_on_the_exact_value(double value, int digits, double expected)
    {
        Assert.Equal(expected, ExactRounding.Round(value, digits, MidpointRounding.ToEven));
    }

    [Fact]
    public void The_directed_modes_follow_the_sign()
    {
        Assert.Equal(2.67, ExactRounding.Round(2.675, 2, MidpointRounding.ToZero));
        Assert.Equal(-2.67, ExactRounding.Round(-2.675, 2, MidpointRounding.ToZero));
        Assert.Equal(2.68, ExactRounding.Round(2.675, 2, MidpointRounding.ToPositiveInfinity));
        Assert.Equal(-2.67, ExactRounding.Round(-2.675, 2, MidpointRounding.ToPositiveInfinity));
        Assert.Equal(2.67, ExactRounding.Round(2.675, 2, MidpointRounding.ToNegativeInfinity));
        Assert.Equal(-2.68, ExactRounding.Round(-2.675, 2, MidpointRounding.ToNegativeInfinity));
        Assert.Equal(100.0, ExactRounding.Round(0.3, -2, MidpointRounding.ToPositiveInfinity));
        Assert.Equal(0.0, ExactRounding.Round(-0.3, -2, MidpointRounding.ToPositiveInfinity));
    }

    // ---- what does not change ----

    /// <summary>
    /// A digit count finer than the value's own spacing leaves it as it is, whatever the count — there
    /// is no cap and nothing throws — and a value with fewer fractional digits than asked for is its own
    /// answer. Neither is a blanket threshold: a small value still rounds at a large count (below).
    /// </summary>
    [Fact]
    public void Digits_finer_than_the_value_leave_it_and_never_throw()
    {
        Assert.Equal(0.1, ExactRounding.Round(0.1, 17, MidpointRounding.AwayFromZero));
        Assert.Equal(0.1, ExactRounding.Round(0.1, 1000, MidpointRounding.AwayFromZero));
        Assert.Equal(0.1f, ExactRounding.Round(0.1f, 9, MidpointRounding.AwayFromZero));
        Assert.Equal(123456.789, ExactRounding.Round(123456.789, 16, MidpointRounding.AwayFromZero));
        Assert.Equal(1e300, ExactRounding.Round(1e300, 2, MidpointRounding.AwayFromZero));
        Assert.Equal(1e300, ExactRounding.Round(1e300, -30, MidpointRounding.AwayFromZero));
        Assert.Equal(double.MaxValue, ExactRounding.Round(double.MaxValue, 0, MidpointRounding.AwayFromZero));

        // Where .NET's scaled algorithm threw (digits above 15) the exact value is rounded.
        Assert.Equal(1.5, ExactRounding.Round(1.5, 16, MidpointRounding.AwayFromZero));
        Assert.Equal(Oracle(0.1, 16, MidpointRounding.AwayFromZero), ExactRounding.Round(0.1, 16, MidpointRounding.AwayFromZero));
    }

    /// <summary>
    /// A small value at a large count is not left alone: 0.00016538332427773838 to seventeen places is
    /// 0.00016538332427774, which the oracle confirms — this is the case a blanket threshold gets wrong.
    /// </summary>
    [Fact]
    public void A_small_value_still_rounds_at_a_large_digit_count()
    {
        var value = -0.00016538332427773838;
        Assert.Equal(-0.00016538332427774, ExactRounding.Round(value, 17, MidpointRounding.AwayFromZero));
        Assert.Equal(Oracle(value, 17, MidpointRounding.AwayFromZero), ExactRounding.Round(value, 17, MidpointRounding.AwayFromZero));
        Assert.Equal(-2.975E-07f, ExactRounding.Round(-2.9748955E-07f, 10, MidpointRounding.AwayFromZero));

        // Beyond the fast path's 128 bits: the arbitrary-precision path, still exact.
        Assert.Equal(1e-300, ExactRounding.Round(1e-300, 300, MidpointRounding.AwayFromZero));
        Assert.Equal(Oracle(1e-300, 305, MidpointRounding.ToEven), ExactRounding.Round(1e-300, 305, MidpointRounding.ToEven));
        Assert.Equal(Oracle(123.456e-30, 40, MidpointRounding.AwayFromZero), ExactRounding.Round(123.456e-30, 40, MidpointRounding.AwayFromZero));
        Assert.Equal(1e30, ExactRounding.Round(1.4e30, -30, MidpointRounding.AwayFromZero));
        Assert.Equal(Oracle(1.5e30, -30, MidpointRounding.AwayFromZero), ExactRounding.Round(1.5e30, -30, MidpointRounding.AwayFromZero));
    }

    [Fact]
    public void Nan_infinities_and_zeros_are_themselves()
    {
        Assert.True(double.IsNaN(ExactRounding.Round(double.NaN, 2, MidpointRounding.AwayFromZero)));
        Assert.Equal(double.PositiveInfinity, ExactRounding.Round(double.PositiveInfinity, 2, MidpointRounding.AwayFromZero));
        Assert.Equal(double.NegativeInfinity, ExactRounding.Round(double.NegativeInfinity, -2, MidpointRounding.AwayFromZero));
        Assert.Equal(0, BitConverter.DoubleToInt64Bits(ExactRounding.Round(0.0, 3, MidpointRounding.AwayFromZero)));
        Assert.Equal(long.MinValue, BitConverter.DoubleToInt64Bits(ExactRounding.Round(-0.0, 3, MidpointRounding.AwayFromZero)));
        // A negative value rounded to zero keeps its sign, as Math.Round does.
        Assert.Equal(long.MinValue, BitConverter.DoubleToInt64Bits(ExactRounding.Round(-0.3, 0, MidpointRounding.AwayFromZero)));
    }

    [Fact]
    public void Subnormals_and_the_smallest_values_round_exactly()
    {
        Assert.Equal(0.0, ExactRounding.Round(double.Epsilon, 5, MidpointRounding.AwayFromZero));
        Assert.Equal(0.0, ExactRounding.Round(1e-300, 16, MidpointRounding.AwayFromZero));
        Assert.Equal(0.0, ExactRounding.Round(1e-300, 17, MidpointRounding.AwayFromZero));
        Assert.Equal(Oracle(double.Epsilon, 16, MidpointRounding.ToPositiveInfinity), ExactRounding.Round(double.Epsilon, 16, MidpointRounding.ToPositiveInfinity));
    }

    /// <summary>
    /// The one case the shortcut has to treat apart: a power of two whose spacing below is half its
    /// spacing above, rounded down by more than a quarter of the spacing above.
    /// </summary>
    [Fact]
    public void A_power_of_two_rounded_down_can_land_on_the_double_beneath_it()
    {
        // 2^-3 = 0.125, rounded to 16 digits, is exactly itself; the oracle agrees at every digit count.
        for (var digits = 0; digits < 17; digits++)
        {
            foreach (var value in new[] { 0.125, 1.0, 4096.0, 1.0 / 1024, Math.ScaleB(1.0, -40), Math.ScaleB(1.0, 30) })
            {
                Assert.Equal(Oracle(value, digits, MidpointRounding.AwayFromZero), ExactRounding.Round(value, digits, MidpointRounding.AwayFromZero));
                Assert.Equal(Oracle(value, digits, MidpointRounding.ToEven), ExactRounding.Round(value, digits, MidpointRounding.ToEven));
            }
        }
    }

    // ---- floats ----

    [Theory]
    [InlineData(2.5f, 0, 3.0f)]
    [InlineData(2.675f, 2, 2.67f)]    // the float is 2.6749999523162841796875: below the midpoint
    [InlineData(0.125f, 2, 0.13f)]
    [InlineData(1250f, -2, 1300f)]
    public void Floats_round_on_their_own_exact_value(float value, int digits, float expected)
    {
        var actual = ExactRounding.Round(value, digits, MidpointRounding.AwayFromZero);
        Assert.Equal(OracleSingle(value, digits, MidpointRounding.AwayFromZero), actual);
        Assert.Equal(expected, actual);
    }

    // ---- decimals ----

    [Fact]
    public void Decimals_are_exact_and_never_pass_through_a_double()
    {
        Assert.Equal(1.01m, ExactRounding.Round(1.005m, 2, MidpointRounding.AwayFromZero));
        Assert.Equal(1.00m, ExactRounding.Round(1.005m, 2, MidpointRounding.ToEven));
        Assert.Equal(3m, ExactRounding.Round(2.5m, 0, MidpointRounding.AwayFromZero));
        Assert.Equal(2m, ExactRounding.Round(2.5m, 0, MidpointRounding.ToEven));
        Assert.Equal(1300m, ExactRounding.Round(1250m, -2, MidpointRounding.AwayFromZero));
        Assert.Equal(1200m, ExactRounding.Round(1250m, -2, MidpointRounding.ToEven));
        Assert.Equal(-1300m, ExactRounding.Round(-1250.0001m, -2, MidpointRounding.AwayFromZero));
        Assert.Equal(0m, ExactRounding.Round(49.9999m, -2, MidpointRounding.AwayFromZero));
        Assert.Equal(100m, ExactRounding.Round(50m, -2, MidpointRounding.AwayFromZero));
        Assert.Equal(1_234_567_890_123_456_789_012_345_678m, ExactRounding.Round(1_234_567_890_123_456_789_012_345_678m, -0, MidpointRounding.AwayFromZero));
        Assert.Equal(1_234_567_890_123_456_789_012_345_700m, ExactRounding.Round(1_234_567_890_123_456_789_012_345_678m, -2, MidpointRounding.AwayFromZero));

        // Digits at or beyond the scale are the value itself, where Math.Round throws past 28.
        var fine = 1.2345678901234567890123456789m;
        Assert.Equal(fine, ExactRounding.Round(fine, 28, MidpointRounding.AwayFromZero));
        Assert.Equal(fine, ExactRounding.Round(fine, 40, MidpointRounding.AwayFromZero));

        // A result beyond the decimal's range is refused, not wrapped.
        Assert.Throws<OverflowException>(() => ExactRounding.Round(decimal.MaxValue, -1, MidpointRounding.AwayFromZero));
    }

    // ---- the sweep ----

    /// <summary>
    /// Random doubles, every digit count that can matter and both SQL modes, bit for bit against the
    /// oracle: the sign of zero included.
    /// </summary>
    [Fact]
    public void Every_double_in_a_sweep_agrees_with_the_oracle()
    {
        var random = new Random(20260925);
        var buffer = new byte[8];
        for (var i = 0; i < 20_000; i++)
        {
            random.NextBytes(buffer);
            var value = BitConverter.ToDouble(buffer);
            if (!double.IsFinite(value))
            {
                continue;
            }

            // Half the sweep is values in the range queries round, the rest the full exponent range.
            if (i % 2 == 0)
            {
                value = Math.ScaleB(value, -(int)Math.Log2(Math.Abs(value)) + random.Next(-20, 60));
                if (!double.IsFinite(value) || value == 0)
                {
                    continue;
                }
            }

            var digits = random.Next(-4, 26);
            foreach (var mode in new[] { MidpointRounding.AwayFromZero, MidpointRounding.ToEven })
            {
                var expected = Oracle(value, digits, mode);
                var actual = ExactRounding.Round(value, digits, mode);
                Assert.True(
                    BitConverter.DoubleToInt64Bits(expected) == BitConverter.DoubleToInt64Bits(actual),
                    $"{value:R} to {digits} digits {mode}: expected {expected:R}, got {actual:R}");
            }
        }
    }

    /// <summary>Values a scaled algorithm gets wrong: just below and just above every midpoint at small scales.</summary>
    [Fact]
    public void Every_neighbour_of_a_midpoint_agrees_with_the_oracle()
    {
        for (var digits = 0; digits <= 6; digits++)
        {
            var step = Math.Pow(10, -digits);
            for (var n = 1; n < 400; n++)
            {
                var midpoint = (n + 0.5) * step;
                foreach (var value in new[] { midpoint, Math.BitDecrement(midpoint), Math.BitIncrement(midpoint), -midpoint, Math.BitDecrement(-midpoint) })
                {
                    foreach (var mode in new[] { MidpointRounding.AwayFromZero, MidpointRounding.ToEven })
                    {
                        Assert.Equal(Oracle(value, digits, mode), ExactRounding.Round(value, digits, mode));
                    }
                }
            }
        }
    }

    [Fact]
    public void Every_float_in_a_sweep_agrees_with_the_oracle()
    {
        var random = new Random(20260926);
        var buffer = new byte[4];
        for (var i = 0; i < 20_000; i++)
        {
            random.NextBytes(buffer);
            var value = BitConverter.ToSingle(buffer);
            if (!float.IsFinite(value))
            {
                continue;
            }

            if (i % 2 == 0)
            {
                value = MathF.ScaleB(value, -(int)MathF.Log2(MathF.Abs(value)) + random.Next(-10, 30));
                if (!float.IsFinite(value) || value == 0)
                {
                    continue;
                }
            }

            var digits = random.Next(-4, 14);
            foreach (var mode in new[] { MidpointRounding.AwayFromZero, MidpointRounding.ToEven })
            {
                var expected = OracleSingle(value, digits, mode);
                var actual = ExactRounding.Round(value, digits, mode);
                Assert.True(
                    BitConverter.SingleToInt32Bits(expected) == BitConverter.SingleToInt32Bits(actual),
                    $"{value:R} to {digits} digits {mode}: expected {expected:R}, got {actual:R}");
            }
        }
    }

    // ---- the oracle: arbitrary precision, then a correctly rounded parse ----

    private static double Oracle(double value, int digits, MidpointRounding mode)
    {
        if (!double.IsFinite(value) || value == 0)
        {
            return value;
        }

        var bits = BitConverter.DoubleToUInt64Bits(value);
        var negative = (bits >> 63) != 0;
        var biased = (int)((bits >> 52) & 0x7FF);
        var fraction = bits & 0xF_FFFF_FFFF_FFFFUL;
        var (m, e) = biased == 0 ? (fraction, -1074) : (fraction | (1UL << 52), biased - 1075);
        var rounded = Exact(m, e, digits, mode, negative);
        return negative ? -rounded : rounded;
    }

    private static float OracleSingle(float value, int digits, MidpointRounding mode)
    {
        if (!float.IsFinite(value) || value == 0)
        {
            return value;
        }

        var bits = BitConverter.SingleToUInt32Bits(value);
        var negative = (bits >> 31) != 0;
        var biased = (int)((bits >> 23) & 0xFF);
        var fraction = bits & 0x7F_FFFFU;
        var (m, e) = biased == 0 ? ((ulong)fraction, -149) : ((ulong)(fraction | (1U << 23)), biased - 150);
        var rounded = Exact(m, e, digits, mode, negative);
        return negative ? -(float)rounded : (float)rounded;
    }

    /// <summary>
    /// |value| = m × 2^e, rounded to 10^-digits exactly with BigInteger arithmetic, then the decimal
    /// string parsed — .NET's parser is correctly rounded, so the nearest double is what comes back.
    /// Parsing a decimal string and then narrowing to float is correctly rounded too, since the
    /// double has more than twice a float's precision.
    /// </summary>
    private static double Exact(ulong m, int e, int digits, MidpointRounding mode, bool negative)
    {
        // scaled = |value| × 10^digits = numerator / denominator
        var numerator = new BigInteger(m);
        var denominator = BigInteger.One;
        if (e >= 0)
        {
            numerator <<= e;
        }
        else
        {
            denominator <<= -e;
        }

        if (digits >= 0)
        {
            numerator *= BigInteger.Pow(10, digits);
        }
        else
        {
            denominator *= BigInteger.Pow(10, -digits);
        }

        var q = BigInteger.DivRem(numerator, denominator, out var r);
        var twice = r << 1;
        var up = !r.IsZero && mode switch
        {
            MidpointRounding.AwayFromZero => twice >= denominator,
            MidpointRounding.ToEven => twice > denominator || (twice == denominator && !q.IsEven),
            MidpointRounding.ToZero => false,
            MidpointRounding.ToPositiveInfinity => !negative,
            MidpointRounding.ToNegativeInfinity => negative,
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
        if (up)
        {
            q += BigInteger.One;
        }

        var text = digits >= 0
            ? $"{q.ToString(CultureInfo.InvariantCulture)}E{-digits}"
            : (q * BigInteger.Pow(10, -digits)).ToString(CultureInfo.InvariantCulture);
        return double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
    }
}

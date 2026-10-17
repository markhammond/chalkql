using System.Numerics;
using Chalk.Execution.Maintenance;

namespace Chalk.Execution.Tests.Maintenance;

/// <summary>
/// The exact sum a maintained aggregate keeps for <c>SUM</c> over floating point (D343, design 68 §4):
/// rows added and taken out in any order give the correctly rounded sum of what is held, checked
/// against <see cref="BigInteger"/> arithmetic in units of 2^-1074.
/// </summary>
public sealed class ExactSumTests
{
    [Fact]
    public void Simple_sums_are_exact()
    {
        var sum = new ExactSum();
        sum.Add(0.1, 1);
        sum.Add(0.2, 1);
        sum.Add(0.3, 1);
        Assert.Equal(Exact([0.1, 0.2, 0.3]), sum.Round());
        Assert.NotEqual(0.1 + 0.2 + 0.3, sum.Round());
        sum.Add(0.3, -1);
        sum.Add(0.1, -1);
        sum.Add(0.2, -1);
        Assert.Equal(0d, sum.Round());
        Assert.False(double.IsNegative(sum.Round()));
    }

    [Fact]
    public void Cancellation_leaves_the_small_term()
    {
        var sum = new ExactSum();
        sum.Add(1e300, 1);
        sum.Add(1.5, 1);
        sum.Add(-1e300, 1);
        Assert.Equal(1.5, sum.Round());
    }

    [Fact]
    public void Special_values_are_counted_apart()
    {
        var sum = new ExactSum();
        sum.Add(1, 1);
        sum.Add(double.PositiveInfinity, 1);
        Assert.Equal(double.PositiveInfinity, sum.Round());
        sum.Add(double.NegativeInfinity, 1);
        Assert.True(double.IsNaN(sum.Round()));
        sum.Add(double.PositiveInfinity, -1);
        Assert.Equal(double.NegativeInfinity, sum.Round());
        sum.Add(double.NegativeInfinity, -1);
        sum.Add(double.NaN, 2);
        Assert.True(double.IsNaN(sum.Round()));
        sum.Add(double.NaN, -2);
        Assert.Equal(1d, sum.Round());
    }

    [Fact]
    public void Overflow_and_subnormals_round_as_ieee_does()
    {
        var sum = new ExactSum();
        sum.Add(double.MaxValue, 2);
        Assert.Equal(double.PositiveInfinity, sum.Round());
        sum.Clear();
        sum.Add(double.Epsilon, 3);
        Assert.Equal(3 * double.Epsilon, sum.Round());
        sum.Add(-double.Epsilon, 5);
        Assert.Equal(-2 * double.Epsilon, sum.Round());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Random_adds_and_removals_match_big_integer_arithmetic(int seed)
    {
        var random = new Random(seed);
        var held = new List<double>();
        var heldSingles = new List<double>();
        var sum = new ExactSum();
        var single = new ExactSum();
        for (var step = 0; step < 4_000; step++)
        {
            if (held.Count > 0 && random.NextDouble() < 0.4)
            {
                var at = random.Next(held.Count);
                sum.Add(held[at], -1);
                held.RemoveAt(at);
            }
            else
            {
                var value = Draw(random);
                held.Add(value);
                sum.Add(value, 1);
            }

            if (heldSingles.Count > 0 && random.NextDouble() < 0.4)
            {
                var at = random.Next(heldSingles.Count);
                single.Add(heldSingles[at], -1);
                heldSingles.RemoveAt(at);
            }
            else if ((float)Draw(random) is var value && float.IsFinite(value))
            {
                heldSingles.Add(value);
                single.Add(value, 1);
            }

            if (step % 97 == 0)
            {
                Assert.Equal(Exact(held), sum.Round());
                Assert.Equal(ExactSingle(heldSingles), single.RoundSingle());
            }
        }

        Assert.Equal(Exact(held), sum.Round());
        Assert.Equal(ExactSingle(heldSingles), single.RoundSingle());
    }

    [Fact]
    public void Ties_round_to_even()
    {
        // 2^53 + 1 is halfway between 2^53 and 2^53 + 2: even wins, so 2^53.
        var sum = new ExactSum();
        sum.Add(9007199254740992d, 1);
        sum.Add(1d, 1);
        Assert.Equal(9007199254740992d, sum.Round());
        // 2^53 + 3 is halfway between 2^53 + 2 and 2^53 + 4: 2^53 + 4.
        sum.Add(2d, 1);
        Assert.Equal(9007199254740996d, sum.Round());
    }

    private static double Draw(Random random)
    {
        var magnitude = random.Next(6) switch
        {
            0 => Math.Pow(10, random.Next(-300, 300)),
            1 => double.Epsilon * random.Next(1, 1000),
            2 => Math.Pow(2, random.Next(-60, 60)),
            _ => Math.Pow(10, random.Next(-8, 12)),
        };
        var value = magnitude * random.NextDouble();
        return random.Next(2) == 0 ? value : -value;
    }

    /// <summary>The correctly rounded double of the exact sum, by big integers and a careful division.</summary>
    private static double Exact(IEnumerable<double> values) => Round(Units(values), 53, 0, 2098);

    private static float ExactSingle(IEnumerable<double> values) => (float)Round(Units(values), 24, 1074 - 149, 1074 + 128);

    private static BigInteger Units(IEnumerable<double> values)
    {
        var total = BigInteger.Zero;
        foreach (var value in values)
        {
            var bits = BitConverter.DoubleToInt64Bits(value);
            var exponent = (int)((bits >> 52) & 0x7FF);
            var mantissa = bits & ((1L << 52) - 1);
            if (exponent != 0)
            {
                mantissa |= 1L << 52;
            }

            var units = new BigInteger(mantissa) << (exponent == 0 ? 0 : exponent - 1);
            total += bits < 0 ? -units : units;
        }

        return total;
    }

    private static double Round(BigInteger units, int mantissaBits, int lowest, int limit)
    {
        if (units.IsZero)
        {
            return 0d;
        }

        var negative = units.Sign < 0;
        var magnitude = BigInteger.Abs(units);
        var top = (int)magnitude.GetBitLength() - 1;
        var lsb = Math.Max(lowest, top - (mantissaBits - 1));
        var significand = magnitude >> lsb;
        var remainder = magnitude - (significand << lsb);
        var half = lsb > 0 ? BigInteger.One << (lsb - 1) : BigInteger.Zero;
        if (lsb > 0 && (remainder > half || (remainder == half && !significand.IsEven)))
        {
            significand += 1;
            if (significand.GetBitLength() > mantissaBits)
            {
                significand >>= 1;
                lsb++;
            }
        }

        if (lsb + mantissaBits > limit)
        {
            return negative ? double.NegativeInfinity : double.PositiveInfinity;
        }

        var result = Math.ScaleB((double)significand, lsb - 1074);
        return negative ? -result : result;
    }
}

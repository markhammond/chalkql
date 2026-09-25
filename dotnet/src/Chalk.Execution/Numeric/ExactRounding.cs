using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Chalk.Execution.Numeric;

/// <summary>
/// Rounding to a number of decimal digits, done on the exact value (F134). This is the semantics
/// .NET 11's <c>Math.Round(double, int, MidpointRounding)</c> adopted: the input's exact binary value
/// is rounded to the requested digits with the midpoint rule, and the result is the nearest
/// representable number. Earlier runtimes compute <c>Round(value × 10ⁿ) / 10ⁿ</c>, whose two extra
/// roundings give the wrong answer for about one input in twenty — <c>655.925</c> to two digits is
/// <c>655.93</c> there, and <c>655.92</c> here, because the double spelled <c>655.925</c> is exactly
/// <c>655.92499999999995…</c>. The engine runs on those runtimes too, so it rounds for itself.
/// </summary>
/// <remarks>
/// <para>
/// A double is <c>m × 2ᵉ</c> with <c>m</c> below 2⁵³, so its value scaled by <c>10ᵈ</c> is the exact
/// rational <c>m × 10ᵈ / 2⁻ᵉ</c>: a 128-bit numerator for every <c>d</c> up to 22, a shift for the
/// denominator, and the remainder against half the divisor decides the midpoint. A value with no more
/// than <c>d</c> fractional digits — one whose binary exponent is <c>-d</c> or above, since a dyadic
/// fraction has exactly as many decimal places as binary ones — is its own answer, which is the one
/// early return the runtime's routine makes too. The rounded integer is then divided by <c>10ᵈ</c>, both exactly representable, so the one
/// IEEE division is the one rounding. Where the rounded integer would need more than 53 bits the
/// requested granularity is finer than the input's own spacing, and the nearest double to the rounded
/// value is the input itself — except just above a power of two, where the spacing below is half the
/// spacing above and the rounded value can fall nearer the double beneath. Negative digits round to a
/// multiple of <c>10ᵏ</c> the same way. A float goes through the same arithmetic with 24 bits and is
/// rounded from the double result, which is innocuous at that width.
/// </para>
/// <para>
/// A <c>decimal</c> is exact in base ten, so <c>Math.Round</c> is right for it; what this adds is a
/// result for digits beyond its scale (the value itself, where <c>Math.Round</c> throws) and negative
/// digits on the integer mantissa rather than through a scaled multiplication.
/// </para>
/// <para>
/// Nothing here allocates, except the one path for a granularity beyond 10²² either way — a digit
/// count above 22 on a value with more fractional digits than that, or rounding to 10²³ and coarser —
/// which is computed with arbitrary precision and parsed, and which no query reaches.
/// </para>
/// </remarks>
internal static class ExactRounding
{
    /// <summary>The largest power of ten the fast path scales by; beyond it the arbitrary-precision path runs.</summary>
    private const int FastDigits = 22;

    private static readonly UInt128[] Pow10 = BuildPow10();

    private static readonly double[] Pow10Double =
    [
        1e0, 1e1, 1e2, 1e3, 1e4, 1e5, 1e6, 1e7, 1e8, 1e9, 1e10, 1e11,
        1e12, 1e13, 1e14, 1e15, 1e16, 1e17, 1e18, 1e19, 1e20, 1e21, 1e22,
    ];

    /// <summary>Rounds <paramref name="value"/> to <paramref name="digits"/> decimal digits, negative digits meaning powers of ten.</summary>
    public static double Round(double value, int digits, MidpointRounding mode)
    {
        if (!double.IsFinite(value) || value == 0)
        {
            return value;
        }

        var bits = BitConverter.DoubleToUInt64Bits(value);
        var negative = (bits >> 63) != 0;
        var biased = (int)((bits >> 52) & 0x7FF);
        var fraction = bits & 0xF_FFFF_FFFF_FFFFUL;
        ulong m;
        int e;
        if (biased == 0)
        {
            m = fraction;
            e = -1074;
        }
        else
        {
            m = fraction | (1UL << 52);
            e = biased - 1075;
        }

        var magnitude = negative ? -value : value;
        var result = Compute(m, e, digits, mode, negative, precision: 53, out var outcome);
        result = outcome switch
        {
            Outcome.Unchanged => magnitude,
            Outcome.Decrement => Math.BitDecrement(magnitude),
            _ => result,
        };
        return negative ? -result : result;
    }

    /// <summary>The same for a float.</summary>
    public static float Round(float value, int digits, MidpointRounding mode)
    {
        if (!float.IsFinite(value) || value == 0)
        {
            return value;
        }

        var bits = BitConverter.SingleToUInt32Bits(value);
        var negative = (bits >> 31) != 0;
        var biased = (int)((bits >> 23) & 0xFF);
        var fraction = bits & 0x7F_FFFFU;
        ulong m;
        int e;
        if (biased == 0)
        {
            m = fraction;
            e = -149;
        }
        else
        {
            m = fraction | (1U << 23);
            e = biased - 150;
        }

        var magnitude = negative ? -value : value;
        var result = Compute(m, e, digits, mode, negative, precision: 24, out var outcome);
        var rounded = outcome switch
        {
            Outcome.Unchanged => magnitude,
            Outcome.Decrement => MathF.BitDecrement(magnitude),
            // Rounding the double result to a float is innocuous: 53 bits is more than twice 24 plus two.
            _ => (float)result,
        };
        return negative ? -rounded : rounded;
    }

    /// <summary>
    /// Rounds a decimal: exactly, as decimal arithmetic is; the value itself for digits at or beyond its
    /// scale; and to a power of ten, on the integer mantissa, for negative digits.
    /// </summary>
    public static decimal Round(decimal value, int digits, MidpointRounding mode)
    {
        if (digits >= value.Scale)
        {
            return value;
        }

        if (digits >= 0)
        {
            return Math.Round(value, digits, mode);
        }

        var k = -digits;
        Span<int> parts = stackalloc int[4];
        _ = decimal.GetBits(value, parts);
        var mantissa = ((UInt128)(uint)parts[2] << 64) | ((ulong)(uint)parts[1] << 32) | (uint)parts[0];
        var negative = value < 0;
        var position = value.Scale + k;

        UInt128 q;
        if (position > 38 || mantissa < Pow10[position] >> 1)
        {
            // Below half of the granularity: zero, or one unit for a mode that rounds away.
            q = RoundsUp(0, mantissa, UInt128.MaxValue, mode, negative) ? UInt128.One : UInt128.Zero;
        }
        else
        {
            var divisor = Pow10[position];
            var q0 = mantissa / divisor;
            var r = mantissa % divisor;
            q = RoundsUp(q0, r, divisor >> 1, mode, negative) ? q0 + 1 : q0;
        }

        if (q == 0)
        {
            return negative ? -0m : 0m;
        }

        if (k > 28)
        {
            throw new OverflowException(
                $"{value.ToString(CultureInfo.InvariantCulture)} rounded to {k} places before the point is beyond a decimal.");
        }

        var result = q * Pow10[k];
        if (result >> 96 != 0)
        {
            throw new OverflowException(
                $"{value.ToString(CultureInfo.InvariantCulture)} rounded to {k} places before the point is beyond a decimal.");
        }

        var low = (ulong)result;
        var high = (uint)(result >> 64);
        return new decimal((int)(uint)low, (int)(uint)(low >> 32), (int)high, negative, 0);
    }

    private enum Outcome
    {
        /// <summary>The returned double is the answer.</summary>
        Value,

        /// <summary>The input is its own answer: the requested granularity is finer than its spacing.</summary>
        Unchanged,

        /// <summary>The double just below the input: the same case, on a power of two rounded down.</summary>
        Decrement,
    }

    /// <summary>
    /// The exact rounding of <c>m × 2ᵉ</c> to <paramref name="digits"/>, for a format of
    /// <paramref name="precision"/> bits. Returns the nearest double where the result is computed, and
    /// says through <paramref name="outcome"/> when the input, or the double beneath it, is the answer.
    /// </summary>
    private static double Compute(
        ulong m, int e, int digits, MidpointRounding mode, bool negative, int precision, out Outcome outcome)
    {
        outcome = Outcome.Value;
        if (digits >= 0)
        {
            if (e + digits >= 0)
            {
                // An integer, or a value with no more fractional digits than asked for: a dyadic
                // fraction m / 2ˢ has exactly s decimal places.
                outcome = Outcome.Unchanged;
                return 0;
            }

            if (digits > FastDigits)
            {
                return RoundSlowly(m, e, digits, mode, negative);
            }

            var s = -e;
            var n = (UInt128)m * Pow10[digits];
            UInt128 q0, r, half;
            if (s >= 127)
            {
                // The scaled value is below one half: the numerator has at most 107 bits.
                q0 = 0;
                r = n;
                half = UInt128.MaxValue;
            }
            else
            {
                q0 = n >> s;
                r = n & ((UInt128.One << s) - 1);
                half = UInt128.One << (s - 1);
            }

            var up = RoundsUp(q0, r, half, mode, negative);
            if (q0 >= UInt128.One << precision)
            {
                // 10⁻ᵈ is below the input's spacing, so the rounded value lies within half a spacing of
                // the input — whose nearest double is itself, unless the input is a power of two, was
                // rounded down, and moved by more than a quarter of the spacing above it, which is
                // more than half the spacing below.
                outcome = m == 1UL << (precision - 1) && !up && r != 0 && 4 * r > Pow10[digits]
                    ? Outcome.Decrement
                    : Outcome.Unchanged;
                return 0;
            }

            var q = up ? q0 + 1 : q0;
            // q is at most 2^precision, and 10ᵈ at most 10²²: both exact, one correctly rounded division.
            return (double)(ulong)q / Pow10Double[digits];
        }

        var k = -digits;
        if (k > FastDigits)
        {
            return RoundSlowly(m, e, digits, mode, negative);
        }

        if (e >= 0)
        {
            if (e > 74)
            {
                // 10ᵏ is below half the spacing below the input, even at a power of two.
                outcome = Outcome.Unchanged;
                return 0;
            }

            var n = (UInt128)m << e;
            var divisor = Pow10[k];
            var q0 = n / divisor;
            var r = n % divisor;
            var q = RoundsUp(q0, r, divisor >> 1, mode, negative) ? q0 + 1 : q0;
            return ToDouble(q * divisor);
        }
        else
        {
            var s = -e;
            UInt128 q;
            if (s >= 54)
            {
                // The input is below one half, so below one twentieth of the granularity.
                q = RoundsUp(0, m, UInt128.MaxValue, mode, negative) ? UInt128.One : UInt128.Zero;
            }
            else
            {
                var divisor = Pow10[k] << s;
                var q0 = m / divisor;
                var r = m % divisor;
                q = RoundsUp(q0, r, divisor >> 1, mode, negative) ? q0 + 1 : q0;
            }

            return ToDouble(q * Pow10[k]);
        }
    }

    /// <summary>
    /// Whether the value <c>q0 + r / (2 × half)</c> rounds up to <c>q0 + 1</c> under
    /// <paramref name="mode"/>, <paramref name="negative"/> saying which way "up" is for the directed modes.
    /// </summary>
    private static bool RoundsUp(UInt128 q0, UInt128 r, UInt128 half, MidpointRounding mode, bool negative)
    {
        if (r == 0)
        {
            return false;
        }

        return mode switch
        {
            MidpointRounding.AwayFromZero => r >= half,
            MidpointRounding.ToEven => r > half || (r == half && (q0 & 1) == 1),
            MidpointRounding.ToZero => false,
            MidpointRounding.ToPositiveInfinity => !negative,
            MidpointRounding.ToNegativeInfinity => negative,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "not a midpoint rounding"),
        };
    }

    /// <summary>A 128-bit integer as the nearest double, ties to even: 53 bits kept, the rest decides.</summary>
    private static double ToDouble(UInt128 v)
    {
        if (v == 0)
        {
            return 0;
        }

        var bitLength = 128 - (int)UInt128.LeadingZeroCount(v);
        if (bitLength <= 53)
        {
            return (ulong)v;
        }

        var shift = bitLength - 53;
        var mantissa = (ulong)(v >> shift);
        var rest = v & ((UInt128.One << shift) - 1);
        var half = UInt128.One << (shift - 1);
        if (rest > half || (rest == half && (mantissa & 1) == 1))
        {
            mantissa++;
            if (mantissa == 1UL << 53)
            {
                mantissa >>= 1;
                shift++;
            }
        }

        return Math.ScaleB(mantissa, shift);
    }

    /// <summary>
    /// A granularity beyond 10²² either way, which no query reaches and the fast path's 128 bits cannot
    /// hold: the exact rational rounded with arbitrary precision, and the result parsed, which rounds it
    /// correctly to the nearest double. The one path that allocates.
    /// </summary>
    private static double RoundSlowly(ulong m, int e, int digits, MidpointRounding mode, bool negative)
    {
        // |value| × 10^digits = numerator / denominator, exactly.
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
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "not a midpoint rounding"),
        };
        if (up)
        {
            q += BigInteger.One;
        }

        var text = digits >= 0
            ? q.ToString(CultureInfo.InvariantCulture) + "E" + (-digits).ToString(CultureInfo.InvariantCulture)
            : (q * BigInteger.Pow(10, -digits)).ToString(CultureInfo.InvariantCulture);
        return double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    private static UInt128[] BuildPow10()
    {
        var table = new UInt128[39];
        table[0] = UInt128.One;
        for (var i = 1; i < table.Length; i++)
        {
            table[i] = table[i - 1] * 10;
        }

        return table;
    }
}

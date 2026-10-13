using System.Globalization;
using System.Numerics;
using Chalk.Ir;

namespace Chalk.Execution.Numeric;

/// <summary>
/// DECIMAL sums, differences, products and quotients, computed exactly on their unscaled integers
/// and rounded half away from zero to the result's scale, as every DECIMAL result is written (D338,
/// design 63 §5). The planner types a product or quotient to keep its integer digits and give up
/// scale, up to 38 digits, which <see cref="decimal"/>'s 28 cannot hold, so nothing here goes through
/// it.
/// </summary>
/// <remarks>
/// <see cref="Int128"/> holds every operand and result. Where an intermediate value might not fit
/// in it — a product of two values of more than 38 digits between them, a sum of two of 38 — the
/// value is taken in a <see cref="BigInteger"/> instead, so the answer is the same and only the cost
/// differs. A result wider than its precision overflows, as it did before.
/// </remarks>
internal static class DecimalArithmetic
{
    /// <summary>Ten to the 0th … 38th, so the hot path never loops for a power.</summary>
    private static readonly Int128[] Powers = [.. Enumerable.Range(0, 39).Select(Decimals.Pow10)];

    public static Int128 Apply(
        FunctionId function, Int128 left, int leftScale, Int128 right, int rightScale, int precision, int scale)
    {
        switch (function)
        {
            case FunctionId.Add or FunctionId.Subtract:
            {
                var common = Math.Max(leftScale, rightScale);
                var subtract = function == FunctionId.Subtract;
                if (Digits(left) + (common - leftScale) <= 37 && Digits(right) + (common - rightScale) <= 37)
                {
                    var a = left * Pow(common - leftScale);
                    var b = right * Pow(common - rightScale);
                    return Fit(Rescale(subtract ? a - b : a + b, common, scale), precision, scale);
                }

                var x = (BigInteger)left * BigInteger.Pow(10, common - leftScale);
                var y = (BigInteger)right * BigInteger.Pow(10, common - rightScale);
                return Fit(Rescale(subtract ? x - y : x + y, common, scale), precision, scale);
            }

            case FunctionId.Multiply:
                return Digits(left) + Digits(right) <= 38
                    ? Fit(Rescale(left * right, leftScale + rightScale, scale), precision, scale)
                    : Fit(Rescale((BigInteger)left * right, leftScale + rightScale, scale), precision, scale);

            case FunctionId.Divide:
            {
                if (right == Int128.Zero)
                {
                    throw new DivideByZeroException("A DECIMAL was divided by zero.");
                }

                // left / right at `scale` is left × 10^(scale + rightScale − leftScale) / right.
                var shift = scale + rightScale - leftScale;
                if (shift >= 0 ? Digits(left) + shift <= 38 : Digits(right) - shift <= 38)
                {
                    var numerator = shift >= 0 ? left * Pow(shift) : left;
                    var denominator = shift >= 0 ? right : right * Pow(-shift);
                    return Fit(Quotient(numerator, denominator), precision, scale);
                }

                var n = shift >= 0 ? (BigInteger)left * BigInteger.Pow(10, shift) : (BigInteger)left;
                var d = shift >= 0 ? (BigInteger)right : (BigInteger)right * BigInteger.Pow(10, -shift);
                return Fit(Quotient(n, d), precision, scale);
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(function), function, "Not DECIMAL arithmetic computed here.");
        }
    }

    private static Int128 Pow(int power) => Powers[power];

    /// <summary>How many decimal digits <paramref name="value"/>'s magnitude has; zero has none.</summary>
    private static int Digits(Int128 value)
    {
        var magnitude = value < Int128.Zero ? (UInt128)(-value) : (UInt128)value;
        var digits = 0;
        UInt128 power = 1;
        while (magnitude >= power)
        {
            digits++;
            if (digits == 39)
            {
                break;
            }

            power *= 10;
        }

        return digits;
    }

    private static Int128 Rescale(Int128 value, int from, int to) =>
        from == to ? value
        : from < to ? value * Pow(to - from)
        : Quotient(value, Pow(from - to));

    private static BigInteger Rescale(BigInteger value, int from, int to) =>
        from == to ? value
        : from < to ? value * BigInteger.Pow(10, to - from)
        : Quotient(value, BigInteger.Pow(10, from - to));

    /// <summary><paramref name="n"/> ÷ <paramref name="d"/>, rounded half away from zero.</summary>
    private static Int128 Quotient(Int128 n, Int128 d)
    {
        var q = Int128.DivRem(n, d);
        var remainder = q.Remainder < Int128.Zero ? -q.Remainder : q.Remainder;
        var divisor = d < Int128.Zero ? -d : d;
        if (remainder >= divisor - remainder)
        {
            return (n < Int128.Zero) == (d < Int128.Zero) ? q.Quotient + Int128.One : q.Quotient - Int128.One;
        }

        return q.Quotient;
    }

    /// <summary>The same, for values past <see cref="Int128"/>.</summary>
    private static BigInteger Quotient(BigInteger n, BigInteger d)
    {
        var q = BigInteger.DivRem(n, d, out var r);
        if (BigInteger.Abs(r) * 2 >= BigInteger.Abs(d))
        {
            return n.Sign == d.Sign ? q + BigInteger.One : q - BigInteger.One;
        }

        return q;
    }

    private static Int128 Fit(Int128 value, int precision, int scale)
    {
        var magnitude = value < Int128.Zero ? -value : value;
        if (magnitude >= Pow(precision))
        {
            throw Overflow(value.ToString(CultureInfo.InvariantCulture), precision, scale);
        }

        return value;
    }

    private static Int128 Fit(BigInteger value, int precision, int scale)
    {
        if (BigInteger.Abs(value) >= BigInteger.Pow(10, precision))
        {
            throw Overflow(value.ToString(CultureInfo.InvariantCulture), precision, scale);
        }

        return (Int128)value;
    }

    private static OverflowException Overflow(string unscaled, int precision, int scale) =>
        new($"A DECIMAL result, unscaled {unscaled}, does not fit in DECIMAL({precision},{scale}).");
}

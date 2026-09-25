using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Chalk.Execution.Numeric;

/// <summary>
/// The DECIMAL lane codec: Arrow's 16-byte little-endian two's-complement unscaled integer
/// (<c>02-ir.md</c> §3) against <see cref="decimal"/>, which is what the kernels compute in (§6.4).
/// </summary>
/// <remarks>
/// <see cref="decimal"/> carries 96 mantissa bits and a scale of at most 28, so the DECIMAL(38,s) the
/// IR permits does not always fit. A value that does not is an <see cref="OverflowException"/> rather
/// than a silently truncated answer; the operator turns it into an <c>ExecutionException</c>.
/// </remarks>
internal static class Decimals
{
    /// <summary>The largest magnitude a <see cref="decimal"/> mantissa can hold.</summary>
    private static readonly UInt128 MaxMantissa = new(0xFFFF_FFFFUL, 0xFFFF_FFFF_FFFF_FFFFUL);

    /// <summary>Reads one lane as an exact <see cref="decimal"/> at the column's scale.</summary>
    public static decimal Read(ReadOnlySpan<byte> lane, int scale) => FromUnscaled(ReadUnscaled(lane), scale);

    /// <summary>Reads one lane as its raw unscaled integer, for comparison and hashing.</summary>
    public static Int128 ReadUnscaled(ReadOnlySpan<byte> lane)
    {
        if (BitConverter.IsLittleEndian)
        {
            return MemoryMarshal.Read<Int128>(lane);
        }

        Span<byte> flipped = stackalloc byte[16];
        lane[..16].CopyTo(flipped);
        flipped.Reverse();
        return MemoryMarshal.Read<Int128>(flipped);
    }

    /// <summary>Writes a raw unscaled integer into one lane.</summary>
    public static void WriteUnscaled(Span<byte> lane, Int128 value)
    {
        MemoryMarshal.Write(lane, in value);
        if (!BitConverter.IsLittleEndian)
        {
            lane[..16].Reverse();
        }
    }

    /// <summary>
    /// Writes a decimal into one lane, rounding half away from zero to <paramref name="scale"/> and
    /// checking it against <paramref name="precision"/> — the rule every source the engine pushes to
    /// applies to a DECIMAL result and a cast (D306, which amended §6's half-even).
    /// </summary>
    public static void Write(Span<byte> lane, decimal value, int precision, int scale)
    {
        WriteUnscaled(lane, ToUnscaled(value, precision, scale));
    }

    /// <summary>Rescales a decimal to an unscaled integer, rounding half away from zero and checking precision.</summary>
    public static Int128 ToUnscaled(decimal value, int precision, int scale)
    {
        if (scale > 28)
        {
            throw new OverflowException(
                $"DECIMAL scale {scale} is beyond what System.Decimal can represent (28).");
        }

        var rounded = Math.Round(value, scale, MidpointRounding.AwayFromZero);
        Span<int> bits = stackalloc int[4];
        _ = decimal.GetBits(rounded, bits);
        var mantissa = new UInt128(
            (uint)bits[2],
            ((ulong)(uint)bits[1] << 32) | (uint)bits[0]);
        var valueScale = (bits[3] >> 16) & 0xFF;
        var negative = bits[3] < 0;

        // Math.Round leaves a value whose own scale is at most the target, so the shift is never negative.
        for (var i = valueScale; i < scale; i++)
        {
            mantissa *= 10;
        }

        var unscaled = negative ? -(Int128)mantissa : (Int128)mantissa;
        var limit = Pow10(precision);
        if (Int128.Abs(unscaled) >= limit)
        {
            throw new OverflowException(
                $"{value.ToString(CultureInfo.InvariantCulture)} does not fit in DECIMAL({precision},{scale}).");
        }

        return unscaled;
    }

    /// <summary>Turns an unscaled integer at <paramref name="scale"/> into a decimal.</summary>
    public static decimal FromUnscaled(Int128 unscaled, int scale)
    {
        if (scale > 28)
        {
            throw new OverflowException(
                $"DECIMAL scale {scale} is beyond what System.Decimal can represent (28).");
        }

        var negative = unscaled < Int128.Zero;
        var magnitude = (UInt128)(negative ? -unscaled : unscaled);
        if (magnitude > MaxMantissa)
        {
            throw new OverflowException(
                "A DECIMAL value is wider than System.Decimal's 96-bit mantissa; "
                + "this build computes DECIMAL arithmetic in System.Decimal (docs/design/04-client.md §6.4).");
        }

        var low = (ulong)magnitude;
        var high = (ulong)(magnitude >> 64);
        return new decimal((int)(uint)low, (int)(uint)(low >> 32), (int)(uint)high, negative, (byte)scale);
    }

    /// <summary>Ten to the <paramref name="power"/>, for the precision check. Power is 1..38.</summary>
    /// <summary>
    /// A double as the unscaled integer of a DECIMAL(<paramref name="precision"/>, <paramref name="scale"/>),
    /// rounded half away from zero on the double's exact value (F135): <c>m × 2ᵉ × 10ˢ</c> as a
    /// 128-bit rational, its remainder against half the divisor deciding the midpoint. Nothing goes
    /// through <see cref="decimal"/>, whose conversion keeps fifteen digits. A value that does not fit
    /// the precision, or is not a number, is an <see cref="OverflowException"/>.
    /// </summary>
    public static Int128 FromDouble(double value, int precision, int scale)
    {
        if (!double.IsFinite(value))
        {
            throw new OverflowException(
                $"{value.ToString(CultureInfo.InvariantCulture)} is not a number a DECIMAL({precision},{scale}) can hold.");
        }

        if (value == 0)
        {
            return Int128.Zero;
        }

        var bits = BitConverter.DoubleToUInt64Bits(value);
        var negative = (bits >> 63) != 0;
        var biased = (int)((bits >> 52) & 0x7FF);
        var fraction = bits & 0xF_FFFF_FFFF_FFFFUL;
        var m = biased == 0 ? fraction : fraction | (1UL << 52);
        var e = biased == 0 ? -1074 : biased - 1075;

        UInt128 q;
        if (e >= 0)
        {
            // An integer: m × 2ᵉ × 10ˢ, which fits 127 bits or fits no DECIMAL at all.
            var integerBits = 64 - BitOperations.LeadingZeroCount(m) + e;
            if (integerBits + BitLength(UnsignedPow10[scale]) > 127)
            {
                throw DoesNotFit(value, precision, scale);
            }

            q = ((UInt128)m << e) * UnsignedPow10[scale];
        }
        else if (scale > 22)
        {
            q = FromDoubleSlowly(m, -e, scale);
        }
        else
        {
            var s = -e;
            var n = (UInt128)m * UnsignedPow10[scale];
            UInt128 q0, r, half;
            if (s >= 127)
            {
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

            q = r != 0 && r >= half ? q0 + 1 : q0;
        }

        if (q >= UnsignedPow10[precision])
        {
            throw DoesNotFit(value, precision, scale);
        }

        return negative ? -(Int128)q : (Int128)q;
    }

    /// <summary>
    /// A DECIMAL's unscaled integer at <paramref name="scale"/> as the nearest double (F137): the quotient
    /// by the power of ten computed to 53 bits by integer long division, ties to even, so a value
    /// wider than <see cref="decimal"/> converts too and a narrower one is correctly rounded, which
    /// <see cref="decimal"/>'s own conversion is not.
    /// </summary>
    public static double ToDouble(Int128 unscaled, int scale)
    {
        if (unscaled == 0)
        {
            return 0;
        }

        var negative = unscaled < 0;
        var magnitude = negative ? (UInt128)(-unscaled) : (UInt128)unscaled;
        var (mantissa, exponent) = Quotient(magnitude, UnsignedPow10[scale], precision: 53);
        var result = Math.ScaleB(mantissa, exponent);
        return negative ? -result : result;
    }

    /// <summary>The same to a float, rounded once from the exact quotient and not from the double.</summary>
    public static float ToSingle(Int128 unscaled, int scale)
    {
        if (unscaled == 0)
        {
            return 0;
        }

        var negative = unscaled < 0;
        var magnitude = negative ? (UInt128)(-unscaled) : (UInt128)unscaled;
        var (mantissa, exponent) = Quotient(magnitude, UnsignedPow10[scale], precision: 24);
        var result = MathF.ScaleB(mantissa, exponent);
        return negative ? -result : result;
    }

    /// <summary>The unscaled integer and scale of a <see cref="decimal"/>, for the conversions above.</summary>
    public static (Int128 Unscaled, int Scale) Parts(decimal value)
    {
        Span<int> parts = stackalloc int[4];
        _ = decimal.GetBits(value, parts);
        var mantissa = ((UInt128)(uint)parts[2] << 64) | ((ulong)(uint)parts[1] << 32) | (uint)parts[0];
        var scale = (parts[3] >> 16) & 0xFF;
        return (parts[3] < 0 ? -(Int128)mantissa : (Int128)mantissa, scale);
    }

    /// <summary>
    /// <c>q / d</c> rounded to <paramref name="precision"/> bits, ties to even, as a mantissa and a
    /// binary exponent: the integer part's leading bits where it has enough, and bits of the fraction
    /// by long division otherwise, with the next bit and the rest of the remainder deciding.
    /// </summary>
    private static (ulong Mantissa, int Exponent) Quotient(UInt128 q, UInt128 d, int precision)
    {
        var q0 = q / d;
        var num = q % d;
        ulong mantissa;
        int exponent;
        bool guard;
        bool sticky;
        if (q0 != 0)
        {
            var bits = 128 - (int)UInt128.LeadingZeroCount(q0);
            if (bits > precision)
            {
                var shift = bits - precision;
                mantissa = (ulong)(q0 >> shift);
                var rest = q0 & ((UInt128.One << shift) - 1);
                var half = UInt128.One << (shift - 1);
                guard = (rest & half) != 0;
                sticky = (rest & (half - 1)) != 0 || num != 0;
                exponent = shift;
            }
            else
            {
                var take = precision - bits;
                mantissa = ((ulong)q0 << take) | NextBits(ref num, d, take);
                guard = NextBits(ref num, d, 1) != 0;
                sticky = num != 0;
                exponent = -take;
            }
        }
        else
        {
            // Below one: skip to the first set bit of the fraction, then take the rest of the mantissa.
            var position = 0;
            do
            {
                num <<= 1;
                position++;
            }
            while (num < d);

            num -= d;
            mantissa = (1UL << (precision - 1)) | NextBits(ref num, d, precision - 1);
            guard = NextBits(ref num, d, 1) != 0;
            sticky = num != 0;
            exponent = -(position + precision - 1);
        }

        if (guard && (sticky || (mantissa & 1) == 1))
        {
            mantissa++;
            if (mantissa == 1UL << precision)
            {
                mantissa >>= 1;
                exponent++;
            }
        }

        return (mantissa, exponent);
    }

    /// <summary>The next <paramref name="count"/> bits of <c>num / d</c>, <paramref name="num"/> advancing.</summary>
    private static ulong NextBits(ref UInt128 num, UInt128 d, int count)
    {
        ulong bits = 0;
        for (var i = 0; i < count; i++)
        {
            num <<= 1;
            var one = num >= d;
            if (one)
            {
                num -= d;
            }

            bits = (bits << 1) | (one ? 1UL : 0UL);
        }

        return bits;
    }

    /// <summary>A scale beyond 22, which the 128-bit numerator cannot hold: exact, with arbitrary precision.</summary>
    private static UInt128 FromDoubleSlowly(ulong m, int s, int scale)
    {
        var numerator = new BigInteger(m) * BigInteger.Pow(10, scale);
        var denominator = BigInteger.One << s;
        var q = BigInteger.DivRem(numerator, denominator, out var r);
        if (!r.IsZero && (r << 1) >= denominator)
        {
            q += BigInteger.One;
        }

        return q > (BigInteger)UInt128.MaxValue ? UInt128.MaxValue : (UInt128)q;
    }

    private static OverflowException DoesNotFit(double value, int precision, int scale) =>
        new($"{value.ToString("R", CultureInfo.InvariantCulture)} does not fit in DECIMAL({precision},{scale}).");

    private static int BitLength(UInt128 value) => 128 - (int)UInt128.LeadingZeroCount(value);

    private static readonly UInt128[] UnsignedPow10 = BuildUnsignedPow10();

    private static UInt128[] BuildUnsignedPow10()
    {
        var table = new UInt128[39];
        table[0] = UInt128.One;
        for (var i = 1; i < table.Length; i++)
        {
            table[i] = table[i - 1] * 10;
        }

        return table;
    }

    public static Int128 Pow10(int power)
    {
        var result = Int128.One;
        for (var i = 0; i < power; i++)
        {
            result *= 10;
        }

        return result;
    }

    /// <summary>Compares two lanes of the same scale, which I-IR-2 guarantees for every comparison.</summary>
    public static int Compare(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right) =>
        ReadUnscaled(left).CompareTo(ReadUnscaled(right));

    /// <summary>A stable hash of a lane, for grouping and IN lists.</summary>
    public static ulong Hash(ReadOnlySpan<byte> lane)
    {
        var low = BinaryPrimitives.ReadUInt64LittleEndian(lane);
        var high = BinaryPrimitives.ReadUInt64LittleEndian(lane[8..]);
        return Hashing.Mix(low ^ Hashing.Mix(high));
    }
}

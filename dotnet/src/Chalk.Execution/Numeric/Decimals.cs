using System.Buffers.Binary;
using System.Globalization;
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
    /// Writes a decimal into one lane, rounding half-even to <paramref name="scale"/> and checking it
    /// against <paramref name="precision"/> — the rounding rule §6 fixes for DECIMAL results.
    /// </summary>
    public static void Write(Span<byte> lane, decimal value, int precision, int scale)
    {
        WriteUnscaled(lane, ToUnscaled(value, precision, scale));
    }

    /// <summary>Rescales a decimal to an unscaled integer, rounding half-even and checking precision.</summary>
    public static Int128 ToUnscaled(decimal value, int precision, int scale)
    {
        if (scale > 28)
        {
            throw new OverflowException(
                $"DECIMAL scale {scale} is beyond what System.Decimal can represent (28).");
        }

        var rounded = Math.Round(value, scale, MidpointRounding.ToEven);
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

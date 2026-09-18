using Apache.Arrow;
using Chalk.Sources;

namespace Chalk.Execution.Vectors;

/// <summary>
/// Validity bitmaps. Null handling is the evaluator's job, not the kernel's (§6.4): a binary kernel's
/// result is valid exactly where both operands are, which is a bitwise AND of two bitmaps.
/// </summary>
internal static class Validity
{
    /// <summary>Bytes needed for <paramref name="length"/> bits.</summary>
    public static int ByteCount(int length) => (length + 7) / 8;

    /// <summary>Marks every one of the first <paramref name="length"/> bits valid, and the tail unset.</summary>
    public static void SetAll(Span<byte> bits, int length)
    {
        var bytes = ByteCount(length);
        bits[..bytes].Fill(0xFF);
        var tail = length & 7;
        if (tail != 0)
        {
            bits[bytes - 1] = (byte)((1 << tail) - 1);
        }
    }

    /// <summary>Copies a column's validity into <paramref name="bits"/> at offset zero.</summary>
    public static void CopyFrom(Span<byte> bits, in ColumnView source, int length)
    {
        var src = source.ValidityBits();
        if (src.IsEmpty)
        {
            SetAll(bits, length);
            return;
        }

        var offset = source.Offset;
        if (offset == 0)
        {
            src[..ByteCount(length)].CopyTo(bits);
            var tail = length & 7;
            if (tail != 0)
            {
                bits[ByteCount(length) - 1] &= (byte)((1 << tail) - 1);
            }

            return;
        }

        bits[..ByteCount(length)].Clear();
        for (var i = 0; i < length; i++)
        {
            if (BitUtility.GetBit(src, offset + i))
            {
                BitUtility.SetBit(bits, i);
            }
        }
    }

    /// <summary>Intersects a column's validity into <paramref name="bits"/> (already at offset zero).</summary>
    public static void AndFrom(Span<byte> bits, in ColumnView source, int length)
    {
        var src = source.ValidityBits();
        if (src.IsEmpty)
        {
            return;
        }

        var offset = source.Offset;
        if (offset == 0)
        {
            var bytes = ByteCount(length);
            for (var b = 0; b < bytes; b++)
            {
                bits[b] &= src[b];
            }

            return;
        }

        for (var i = 0; i < length; i++)
        {
            if (!BitUtility.GetBit(src, offset + i))
            {
                BitUtility.ClearBit(bits, i);
            }
        }
    }

    /// <summary>Number of unset bits in the first <paramref name="length"/> — that is, the null count.</summary>
    public static int CountNulls(ReadOnlySpan<byte> bits, int length) =>
        length - BitUtility.CountBits(bits, 0, length);
}

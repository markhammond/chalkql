namespace Chalk.Execution.Numeric;

/// <summary>
/// Code-point arithmetic straight on UTF-8, so <c>CHAR_LENGTH</c>, <c>SUBSTRING</c> and <c>LIKE</c>
/// never decode a row into a CLR string. <c>02-ir.md</c> §6 measures all three in code points, and a
/// UTF-8 code point is exactly one lead byte plus its continuation bytes.
/// </summary>
internal static class Utf8Text
{
    /// <summary>Number of code points, i.e. bytes that are not continuation bytes.</summary>
    public static int CodePointCount(ReadOnlySpan<byte> utf8)
    {
        var count = 0;
        foreach (var b in utf8)
        {
            if ((b & 0xC0) != 0x80)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Bytes in the code point that starts at <paramref name="index"/>.</summary>
    public static int CodePointLength(ReadOnlySpan<byte> utf8, int index)
    {
        var lead = utf8[index];
        var length = lead switch
        {
            < 0x80 => 1,
            < 0xE0 => 2,
            < 0xF0 => 3,
            _ => 4,
        };
        return Math.Min(length, utf8.Length - index);
    }

    /// <summary>
    /// The byte offset of code point <paramref name="codePoint"/>, or the length when the string is
    /// shorter than that.
    /// </summary>
    public static int ByteOffsetOf(ReadOnlySpan<byte> utf8, int codePoint)
    {
        var offset = 0;
        for (var seen = 0; seen < codePoint && offset < utf8.Length; seen++)
        {
            offset += CodePointLength(utf8, offset);
        }

        return Math.Min(offset, utf8.Length);
    }

    /// <summary>The bytes of the code points in <c>[start, start + count)</c>, clamped to the string.</summary>
    public static ReadOnlySpan<byte> Slice(ReadOnlySpan<byte> utf8, int start, int count)
    {
        var from = ByteOffsetOf(utf8, start);
        var to = ByteOffsetOf(utf8, start + count);
        return utf8[from..to];
    }
}

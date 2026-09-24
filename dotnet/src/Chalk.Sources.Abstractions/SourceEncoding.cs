using System.Buffers.Binary;
using System.Globalization;

namespace Chalk.Sources;

/// <summary>
/// The two value encodings every source has to get exactly right, in one place so two sources cannot
/// disagree about them: a DECIMAL's unscaled 16-byte lane and a UUID's byte order.
/// </summary>
/// <remarks>
/// Both were the POCO source's private business until the ADO.NET source needed the same answers
/// (M4). Sharing them is not tidiness: a decimal written one way here and another way there would
/// produce two different numbers for one value, and the executor's kernels read the lane directly.
/// </remarks>
public static class SourceEncoding
{
    private static readonly UInt128[] PowersOfTen = BuildPowersOfTen();

    /// <summary>
    /// Writes one decimal into Arrow's 16-byte little-endian two's-complement unscaled slot
    /// (<c>02-ir.md</c> §3). A value with more fractional digits than the declared scale is an
    /// error, never a rounding (D16): silently dropping digits is exactly the kind of wrong answer
    /// Chalk exists to avoid.
    /// </summary>
    /// <param name="value">The value to write.</param>
    /// <param name="precision">The column's declared precision.</param>
    /// <param name="scale">The column's declared scale.</param>
    /// <param name="destination">Exactly sixteen bytes.</param>
    /// <param name="sourceId">For the exception, if one is needed.</param>
    /// <param name="table">For the exception: the table or query the value came from.</param>
    /// <param name="column">For the exception: which column it was.</param>
    public static void WriteDecimal(
        decimal value,
        int precision,
        int scale,
        Span<byte> destination,
        string sourceId,
        string table,
        string column)
    {
        var refusal = TryWriteDecimal(value, precision, scale, destination);
        if (refusal is not null)
        {
            // What a source is told to do about it; a function's answer is told in the engine's words.
            var remedy = refusal.StartsWith("which needs scale", StringComparison.Ordinal)
                ? "Widen the scale, or round the value in the source."
                : "Widen the precision.";
            throw new SourceContractException(
                sourceId,
                table,
                $"column '{column}' is DECIMAL({precision},{scale}) but a row holds "
                + $"{value.ToString(CultureInfo.InvariantCulture)}, {refusal}. {remedy}");
        }
    }

    /// <summary>
    /// The same check and encoding, answering what is wrong instead of throwing it, so a caller
    /// that is not a source — the engine writing a function's answer (D298) — refuses in its own
    /// words. Null when the value was written; the destination is untouched otherwise. The answer is
    /// the clause that says why — "which needs scale 3" — and the caller says what to do about it.
    /// </summary>
    internal static string? TryWriteDecimal(decimal value, int precision, int scale, Span<byte> destination)
    {
        Span<int> bits = stackalloc int[4];
        decimal.GetBits(value, bits);

        var flags = bits[3];
        var valueScale = (flags >> 16) & 0xFF;
        var negative = flags < 0;
        var magnitude = new UInt128((uint)bits[2], ((ulong)(uint)bits[1] << 32) | (uint)bits[0]);

        // decimal keeps trailing zeros (1.500m has scale 3), so drop the ones that carry no
        // information before deciding whether the value really is too precise for the column.
        while (valueScale > scale && magnitude % 10 == UInt128.Zero)
        {
            magnitude /= 10;
            valueScale--;
        }

        if (valueScale > scale)
        {
            return $"which needs scale {valueScale}";
        }

        var factor = PowersOfTen[scale - valueScale];
        var limit = PowersOfTen[precision];
        if (magnitude > UInt128.MaxValue / factor || magnitude * factor >= limit)
        {
            return $"which needs more than {precision} digits";
        }

        magnitude *= factor;
        var unscaled = negative ? -(Int128)magnitude : (Int128)magnitude;
        BinaryPrimitives.WriteInt128LittleEndian(destination, unscaled);
        return null;
    }

    /// <summary>
    /// Writes one UUID as Arrow's canonical extension expects it: RFC 4122 big-endian order. The
    /// CLR's own layout is mixed-endian and would not interoperate with anything else that reads
    /// the buffer.
    /// </summary>
    public static void WriteUuid(Guid value, Span<byte> destination) =>
        value.TryWriteBytes(destination, bigEndian: true, out _);

    private static UInt128[] BuildPowersOfTen()
    {
        var powers = new UInt128[39];
        powers[0] = UInt128.One;
        for (var i = 1; i < powers.Length; i++)
        {
            powers[i] = powers[i - 1] * 10;
        }

        return powers;
    }
}

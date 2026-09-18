using System.Globalization;

namespace Chalk.Sources.Poco;

/// <summary>
/// The handful of conversions the compiled chunk writers (§5.3) and the batch encoders call into.
/// Everything here is on the scan path, so nothing allocates for a value that is in range.
/// </summary>
internal static class PocoConvert
{
    /// <summary>Ticks at 1970-01-01, the epoch every Chalk temporal counts from (<c>02-ir.md</c> §3).</summary>
    public const long UnixEpochTicks = 621_355_968_000_000_000L;

    /// <summary><see cref="DateOnly.DayNumber"/> of 1970-01-01, so DATE is days since the epoch.</summary>
    public const int UnixEpochDayNumber = 719_162;

    /// <summary>10^0 … 10^38 as <see cref="UInt128"/>, for rescaling decimals. DECIMAL precision is capped at 38.</summary>

    /// <summary>The 128 one-character ASCII strings, so <c>char</c> → STRING costs nothing per row.</summary>
    private static readonly string[] AsciiStrings = BuildAsciiStrings();

    /// <summary>
    /// <c>char.ToString()</c> allocates, and a chunk writer must not (§5.3). ASCII covers the cases
    /// that appear in bulk; anything else is rare enough to pay for itself.
    /// </summary>
    public static string CharToString(char value) =>
        value < 128 ? AsciiStrings[value] : value.ToString();

    /// <summary>
    /// The printable form of an enum value that is not one of the declared names — a combined
    /// <c>[Flags]</c> value, or a cast integer. Producing the number keeps the row visible instead
    /// of turning it into a silent NULL.
    /// </summary>
    public static string EnumFallback(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// A NULL turned up in a column the host declared non-nullable. Arrow would carry it as a valid
    /// value, so this is a wrong-answer bug in waiting; fail loudly instead (the same reasoning as
    /// D17's verification).
    /// </summary>
    public static void ThrowUnexpectedNull(string sourceId, string table, string column) =>
        throw new SourceContractException(
            sourceId,
            table,
            $"column '{column}' is declared NOT NULL but a row holds null. Declare the column nullable, "
            + "or map the member with Column(name, projection) and supply a value.");

    /// <summary>
    /// Writes one decimal into Arrow's 16-byte little-endian two's-complement unscaled slot
    /// (<c>02-ir.md</c> §3). A value with more fractional digits than the declared scale is an error,
    /// never a rounding (D16): silently dropping digits is exactly the kind of wrong answer Chalk exists to avoid.
    /// </summary>
    public static void WriteDecimal(
        decimal value, int precision, int scale, Span<byte> destination, string sourceId, string table, string column) =>
        SourceEncoding.WriteDecimal(value, precision, scale, destination, sourceId, table, column);

    private static string[] BuildAsciiStrings()
    {
        var strings = new string[128];
        for (var i = 0; i < strings.Length; i++)
        {
            strings[i] = ((char)i).ToString();
        }

        return strings;
    }
}

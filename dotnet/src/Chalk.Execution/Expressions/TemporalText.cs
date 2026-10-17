using Chalk.Execution.Numeric;
using Chalk.Ir;

namespace Chalk.Execution.Expressions;

/// <summary>
/// A date, a time or a timestamp as text (F193), as PostgreSQL and DuckDB write one: <c>2026-01-08</c>,
/// <c>12:34:56</c> and <c>2026-01-08 12:34:56</c>, with a fraction of a second after a point, its
/// trailing zeros trimmed, and no point when the second is whole. So <c>12:34:56.5</c>, and a
/// timestamp to the nanosecond when its precision holds one. A timestamp with a time zone is its
/// wall clock in UTC and the zone, <c>2026-01-08 12:34:56+00</c>, as both write one in a session at
/// UTC, which reads back as the same instant.
/// </summary>
/// <remarks>
/// A year before 1 or after 9999 has no such text, and is refused as a value the cast cannot convert,
/// which a cast that fails to NULL gives as NULL.
/// </remarks>
internal static class TemporalText
{
    /// <summary>The most bytes one value's text takes: a timestamp with a time zone, to the nanosecond.</summary>
    public const int Longest = 32;

    private const long MicrosPerSecond = 1_000_000L;

    /// <summary>Writes a DATE, as days since 1970-01-01, and returns the bytes written.</summary>
    public static int WriteDate(Span<byte> into, long days)
    {
        if (days is < int.MinValue or > int.MaxValue)
        {
            throw new FormatException("A date that far from 1970 has no year of four digits.");
        }

        CivilTime.ToCivil((int)days, out var year, out var month, out var day);
        if (year is < 1 or > 9999)
        {
            throw new FormatException($"The year {year} has no text of four digits.");
        }

        Digits(into, year, 4);
        into[4] = (byte)'-';
        Digits(into[5..], month, 2);
        into[7] = (byte)'-';
        Digits(into[8..], day, 2);
        return 10;
    }

    /// <summary>Writes a TIME, as microseconds since midnight, and returns the bytes written.</summary>
    public static int WriteTime(Span<byte> into, long micros)
    {
        var written = Clock(into, micros / MicrosPerSecond);
        return written + Fraction(into[written..], micros % MicrosPerSecond, 6);
    }

    /// <summary>Writes a TIMESTAMP of <paramref name="precision"/>, in its units since 1970, and returns the bytes written.</summary>
    public static int WriteTimestamp(Span<byte> into, long raw, int precision)
    {
        var unitsPerSecond = IrTypes.TimestampUnitsPerSecond((uint)precision);
        var seconds = CivilTime.FloorDiv(raw, unitsPerSecond);
        var days = CivilTime.FloorDiv(seconds, CivilTime.SecondsPerDay);
        var written = WriteDate(into, days);
        into[written++] = (byte)' ';
        written += Clock(into[written..], seconds - (days * CivilTime.SecondsPerDay));
        var digits = unitsPerSecond switch
        {
            1_000L => 3,
            1_000_000L => 6,
            _ => 9,
        };

        return written + Fraction(into[written..], raw - (seconds * unitsPerSecond), digits);
    }

    /// <summary>
    /// Writes a TIMESTAMP WITH LOCAL TIME ZONE of <paramref name="precision"/>, an instant in its units
    /// since 1970 UTC, as its UTC wall clock and <c>+00</c>, and returns the bytes written.
    /// </summary>
    public static int WriteInstant(Span<byte> into, long raw, int precision)
    {
        var written = WriteTimestamp(into, raw, precision);
        into[written] = (byte)'+';
        into[written + 1] = (byte)'0';
        into[written + 2] = (byte)'0';
        return written + 3;
    }

    /// <summary><c>HH:MM:SS</c> for a second of the day.</summary>
    private static int Clock(Span<byte> into, long secondOfDay)
    {
        Digits(into, (int)(secondOfDay / 3600), 2);
        into[2] = (byte)':';
        Digits(into[3..], (int)(secondOfDay / 60 % 60), 2);
        into[5] = (byte)':';
        Digits(into[6..], (int)(secondOfDay % 60), 2);
        return 8;
    }

    /// <summary>A point and the fraction's digits, its trailing zeros trimmed; nothing for a whole second.</summary>
    private static int Fraction(Span<byte> into, long fraction, int digits)
    {
        if (fraction == 0)
        {
            return 0;
        }

        while (fraction % 10 == 0)
        {
            fraction /= 10;
            digits--;
        }

        into[0] = (byte)'.';
        for (var i = digits; i > 0; i--)
        {
            into[i] = (byte)('0' + (fraction % 10));
            fraction /= 10;
        }

        return digits + 1;
    }

    /// <summary>A number of <paramref name="width"/> digits, zero-padded.</summary>
    private static void Digits(Span<byte> into, int value, int width)
    {
        for (var i = width - 1; i >= 0; i--)
        {
            into[i] = (byte)('0' + (value % 10));
            value /= 10;
        }
    }
}

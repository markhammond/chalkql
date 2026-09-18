namespace Chalk.Execution.Numeric;

/// <summary>
/// Days since 1970-01-01 against proleptic Gregorian calendar fields, without going through
/// <see cref="DateTime"/> per lane (§6.4). The two conversions are Howard Hinnant's <c>civil_from_days</c>
/// and <c>days_from_civil</c>, which are exact for every year the IR can hold.
/// </summary>
internal static class CivilTime
{
    public const long SecondsPerDay = 86_400L;

    /// <summary>Breaks a day number into year, month (1–12) and day (1–31).</summary>
    public static void ToCivil(int days, out int year, out int month, out int day)
    {
        // Shift the era so that the leap-year cycle starts on 0000-03-01.
        var z = days + 719_468L;
        var era = (z >= 0 ? z : z - 146_096) / 146_097;
        var dayOfEra = (ulong)(z - (era * 146_097));
        var yearOfEra = (dayOfEra - (dayOfEra / 1460) + (dayOfEra / 36524) - (dayOfEra / 146096)) / 365;
        var y = (long)yearOfEra + (era * 400);
        var dayOfYear = dayOfEra - ((365 * yearOfEra) + (yearOfEra / 4) - (yearOfEra / 100));
        var mp = ((5 * dayOfYear) + 2) / 153;
        day = (int)(dayOfYear - (((153 * mp) + 2) / 5) + 1);
        month = (int)(mp < 10 ? mp + 3 : mp - 9);
        year = (int)(month <= 2 ? y + 1 : y);
    }

    /// <summary>The day number of a civil date.</summary>
    public static int FromCivil(int year, int month, int day)
    {
        long y = year;
        y -= month <= 2 ? 1 : 0;
        var era = (y >= 0 ? y : y - 399) / 400;
        var yearOfEra = (ulong)(y - (era * 400));
        var mp = (ulong)(month > 2 ? month - 3 : month + 9);
        var dayOfYear = (((153 * mp) + 2) / 5) + (ulong)(day - 1);
        var dayOfEra = (365 * yearOfEra) + (yearOfEra / 4) - (yearOfEra / 100) + dayOfYear;
        return (int)((era * 146_097) + (long)dayOfEra - 719_468);
    }

    /// <summary>Day of week with 0 = Sunday, the convention <c>02-ir.md</c> §6 fixes for EXTRACT(DOW).</summary>
    public static int DayOfWeek(int days) => (int)FloorMod(days + 4L, 7L);

    /// <summary>1-based day of year.</summary>
    public static int DayOfYear(int days)
    {
        ToCivil(days, out var year, out _, out _);
        return days - FromCivil(year, 1, 1) + 1;
    }

    /// <summary>Days in a month, proleptic Gregorian.</summary>
    public static int DaysInMonth(int year, int month) => month switch
    {
        1 or 3 or 5 or 7 or 8 or 10 or 12 => 31,
        4 or 6 or 9 or 11 => 30,
        _ => IsLeapYear(year) ? 29 : 28,
    };

    public static bool IsLeapYear(int year) => (year % 4 == 0 && year % 100 != 0) || year % 400 == 0;

    /// <summary>Division that rounds toward negative infinity — what a timestamp before 1970 needs.</summary>
    public static long FloorDiv(long value, long divisor)
    {
        var quotient = value / divisor;
        return value % divisor != 0 && ((value < 0) != (divisor < 0)) ? quotient - 1 : quotient;
    }

    /// <summary>The non-negative remainder that goes with <see cref="FloorDiv"/>.</summary>
    public static long FloorMod(long value, long divisor) => value - (FloorDiv(value, divisor) * divisor);
}

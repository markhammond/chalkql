using System.Buffers.Binary;
using System.Globalization;

namespace Chalk.Sources;

/// <summary>
/// The conversions between a fixed-width lane and the CLR type a host spells it with — DATE as
/// <see cref="DateOnly"/>, TIME as <see cref="TimeOnly"/>, TIMESTAMP as <see cref="DateTime"/>,
/// TIMESTAMP_TZ as <see cref="DateTimeOffset"/>, INTERVAL_DAY as <see cref="TimeSpan"/>, UUID as
/// <see cref="Guid"/> and DECIMAL as <see cref="decimal"/> — in one place, so the POCO source's
/// chunk writer (a member's value on its way into a lane) and the engine's lane codec (a delegate's
/// argument out of a lane and its answer back in, D298) cannot disagree about a single value.
/// </summary>
/// <remarks>
/// <para>
/// The lanes are those of <c>02-ir.md</c> §3: DATE counts days and TIME microseconds since midnight;
/// a TIMESTAMP counts milliseconds, microseconds or nanoseconds since 1970-01-01 as its precision is
/// 0–3, 4–6 or 7–9; INTERVAL_DAY counts microseconds; a UUID is sixteen bytes in RFC 4122 order and a
/// DECIMAL a sixteen-byte little-endian unscaled integer.
/// </para>
/// <para>
/// A CLR temporal counts 100-nanosecond ticks. Into a lane, the conversions here divide as the POCO
/// source always has — toward zero, dropping what the lane's unit cannot hold. Out of a lane, a
/// nanosecond count is floored to the tick at or before it. Whether a value the unit cannot hold
/// is refused rather than dropped is the caller's decision (<see cref="FitsUnit"/>).
/// </para>
/// </remarks>
internal static class ClrStorage
{
    /// <summary>Ticks at 1970-01-01, the epoch every Chalk temporal counts from (<c>02-ir.md</c> §3).</summary>
    public const long UnixEpochTicks = 621_355_968_000_000_000L;

    /// <summary><see cref="DateOnly.DayNumber"/> of 1970-01-01, so DATE is days since the epoch.</summary>
    public const int UnixEpochDayNumber = 719_162;

    /// <summary>The largest magnitude a <see cref="decimal"/> holds: 2^96 − 1.</summary>
    private static readonly UInt128 DecimalMagnitudeLimit = (UInt128.One << 96) - 1;

    // ---- DATE ----

    /// <summary>A DATE lane: days since the epoch.</summary>
    public static int DaysOf(DateOnly value) => value.DayNumber - UnixEpochDayNumber;

    /// <summary>The <see cref="DateOnly"/> a DATE lane holds, exactly.</summary>
    public static DateOnly DateOf(int days)
    {
        var dayNumber = (long)days + UnixEpochDayNumber;
        if (dayNumber < DateOnly.MinValue.DayNumber || dayNumber > DateOnly.MaxValue.DayNumber)
        {
            throw new ArgumentOutOfRangeException(
                nameof(days),
                days,
                "the DATE lies outside what a DateOnly holds (0001-01-01 to 9999-12-31)");
        }

        return DateOnly.FromDayNumber((int)dayNumber);
    }

    // ---- TIME and INTERVAL_DAY: microseconds ----

    /// <summary>A TIME lane: microseconds since midnight, dropping a sub-microsecond remainder.</summary>
    public static long MicrosOf(TimeOnly value) => value.Ticks / 10;

    /// <summary>The <see cref="TimeOnly"/> a TIME lane holds, exactly.</summary>
    public static TimeOnly TimeOf(long micros)
    {
        if (micros < 0 || micros > TimeOnly.MaxValue.Ticks / 10)
        {
            throw new ArgumentOutOfRangeException(
                nameof(micros), micros, "the TIME lies outside one day, which is all a TimeOnly holds");
        }

        return new TimeOnly(micros * 10);
    }

    /// <summary>An INTERVAL_DAY lane: microseconds, dropping a sub-microsecond remainder.</summary>
    public static long MicrosOf(TimeSpan value) => value.Ticks / 10;

    /// <summary>The <see cref="TimeSpan"/> an INTERVAL_DAY lane holds, exactly.</summary>
    public static TimeSpan IntervalOf(long micros)
    {
        if (micros > TimeSpan.MaxValue.Ticks / 10 || micros < TimeSpan.MinValue.Ticks / 10)
        {
            throw new ArgumentOutOfRangeException(
                nameof(micros), micros, "the interval lies outside what a TimeSpan holds");
        }

        return new TimeSpan(micros * 10);
    }

    // ---- TIMESTAMP and TIMESTAMP_TZ ----

    /// <summary>
    /// A TIMESTAMP lane at <paramref name="precision"/>: the units since the epoch of a CLR tick
    /// count — <see cref="DateTime.Ticks"/>, or <see cref="DateTimeOffset.UtcTicks"/> for a
    /// TIMESTAMP_TZ. Nanoseconds are exact; a coarser unit drops the remainder, toward zero.
    /// </summary>
    public static long UnitsOf(long ticks, int precision)
    {
        var sinceEpoch = ticks - UnixEpochTicks;
        return precision switch
        {
            <= 3 => sinceEpoch / 10_000L,
            <= 6 => sinceEpoch / 10L,
            _ => sinceEpoch * 100L,
        };
    }

    /// <summary>
    /// Whether a CLR tick count fits a TIMESTAMP lane at <paramref name="precision"/> with nothing
    /// dropped and nothing overflowing: a whole number of the unit, inside the range a 64-bit count
    /// of it covers.
    /// </summary>
    public static bool FitsUnit(long ticks, int precision)
    {
        var sinceEpoch = ticks - UnixEpochTicks;
        return precision switch
        {
            <= 3 => sinceEpoch % 10_000L == 0,
            <= 6 => sinceEpoch % 10L == 0,
            _ => sinceEpoch is <= long.MaxValue / 100L and >= long.MinValue / 100L,
        };
    }

    /// <summary>
    /// The CLR tick count a TIMESTAMP lane at <paramref name="precision"/> stands for. Milliseconds
    /// and microseconds are exact; nanoseconds are floored to the tick at or before them.
    /// </summary>
    public static long TicksOf(long units, int precision)
    {
        var sinceEpoch = precision switch
        {
            <= 3 => checked(units * 10_000L),
            <= 6 => checked(units * 10L),
            _ => Math.DivRem(units, 100L, out var remainder) - (remainder < 0 ? 1 : 0),
        };

        var ticks = checked(sinceEpoch + UnixEpochTicks);
        if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
        {
            throw new ArgumentOutOfRangeException(
                nameof(units),
                units,
                "the TIMESTAMP lies outside what a DateTime holds (0001-01-01 to 9999-12-31)");
        }

        return ticks;
    }

    /// <summary>A TIMESTAMP lane as a wall-clock <see cref="DateTime"/>, <see cref="DateTimeKind.Unspecified"/>.</summary>
    public static DateTime DateTimeOf(long units, int precision) =>
        new(TicksOf(units, precision), DateTimeKind.Unspecified);

    /// <summary>A TIMESTAMP_TZ lane as the instant it is, in UTC.</summary>
    public static DateTimeOffset InstantOf(long units, int precision) =>
        new(TicksOf(units, precision), TimeSpan.Zero);

    // ---- UUID ----

    /// <summary>A UUID lane, RFC 4122 order, as a <see cref="Guid"/>.</summary>
    public static Guid UuidOf(ReadOnlySpan<byte> lane) => new(lane[..16], bigEndian: true);

    // ---- DECIMAL ----

    /// <summary>
    /// The <see cref="decimal"/> a DECIMAL lane of <paramref name="scale"/> holds: exact whenever the
    /// declared precision is 28 or less, which is every DECIMAL a <c>decimal</c> may spell.
    /// </summary>
    public static decimal DecimalOf(ReadOnlySpan<byte> lane, int scale)
    {
        var unscaled = BinaryPrimitives.ReadInt128LittleEndian(lane);
        var negative = unscaled < 0;
        var magnitude = negative ? (UInt128)(-unscaled) : (UInt128)unscaled;
        if (magnitude > DecimalMagnitudeLimit || scale is < 0 or > 28)
        {
            throw new OverflowException(
                $"the DECIMAL {unscaled.ToString(CultureInfo.InvariantCulture)}E-{scale} holds more "
                + "digits than a CLR decimal (28)");
        }

        var low = (ulong)magnitude;
        var high = (uint)(magnitude >> 64);
        return new decimal((int)(uint)low, (int)(uint)(low >> 32), (int)high, negative, (byte)scale);
    }

    /// <summary>
    /// Encodes one decimal into a DECIMAL(<paramref name="precision"/>, <paramref name="scale"/>)
    /// lane, or says why it cannot be: more fractional digits than the scale is never rounded (D16),
    /// and more digits than the precision never fits. The lane is untouched when it answers false.
    /// </summary>
    /// <returns>Null when written; otherwise the clause that says why — "which needs scale 3" — for a refusal to quote.</returns>
    public static string? TryWriteDecimal(decimal value, int precision, int scale, Span<byte> destination) =>
        SourceEncoding.TryWriteDecimal(value, precision, scale, destination);
}

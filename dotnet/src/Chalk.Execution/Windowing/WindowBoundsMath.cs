using Chalk.Catalog;
using Chalk.Ir;
using Chalk.Sources;

namespace Chalk.Execution.Windowing;

/// <summary>
/// The integer arithmetic {@code HOP} and {@code SESSION} share (D55,
/// <c>14-windows-ii.md</c> §1): intervals rescaled into the time column's own units, and the
/// epoch-aligned window a value falls in.
/// </summary>
/// <remarks>
/// Everything is integer. A time column holds a count of units since the epoch — days for DATE,
/// <c>Type.precision</c> units for the timestamps — and the IR carries an interval as microseconds,
/// so the two only ever meet after this conversion.
/// </remarks>
internal static class WindowBoundsMath
{
    /// <summary>Microseconds in one day, which is the DATE column's unit.</summary>
    private const long MicrosPerDay = 86_400_000_000L;

    /// <summary>
    /// An interval of <paramref name="micros"/> microseconds, in the units
    /// <paramref name="time"/> counts.
    /// </summary>
    /// <exception cref="UnsupportedFeatureException">
    /// The interval is not a whole number of the column's units — a 30-second window over a DATE
    /// column, say — which would silently truncate to nothing.
    /// </exception>
    public static long IntervalInUnits(long micros, ChalkType time, string what)
    {
        if (micros <= 0)
        {
            throw new UnsupportedFeatureException(
                $"{what} of {micros} microseconds", "A window's slide, size and gap must be positive.");
        }

        if (time.Kind == TypeKind.Date)
        {
            if (micros % MicrosPerDay != 0)
            {
                throw new UnsupportedFeatureException(
                    $"{what} of {micros} microseconds over a DATE column",
                    "A window over a DATE column measures whole days.");
            }

            return micros / MicrosPerDay;
        }

        var perSecond = IrTypes.TimestampUnitsPerSecond((uint)time.Precision);
        if (perSecond >= 1_000_000L)
        {
            return micros * (perSecond / 1_000_000L);
        }

        var perMicro = 1_000_000L / perSecond;
        if (micros % perMicro != 0)
        {
            throw new UnsupportedFeatureException(
                $"{what} of {micros} microseconds over a {time} column",
                $"The column counts {perSecond} units per second, so the interval must be a whole "
                + "number of them.");
        }

        return micros / perMicro;
    }

    /// <summary>
    /// The largest multiple of <paramref name="slide"/> at or below <paramref name="value"/>,
    /// counting from the epoch. Floors rather than truncates, so a value before the epoch lands on
    /// the boundary below it — the same rule <c>TIME_BUCKET</c> follows.
    /// </summary>
    public static long FloorToSlide(long value, long slide)
    {
        var remainder = value % slide;
        return remainder < 0 ? value - remainder - slide : value - remainder;
    }

    /// <summary>
    /// How many hopping windows contain <paramref name="value"/>: the starts are
    /// {@code floor, floor - slide, …} while each is greater than {@code value - size}. Zero when
    /// {@code size &lt; slide} leaves the value in the gap between two windows, which is the case
    /// Calcite's own implementation drops (V23).
    /// </summary>
    public static int WindowCount(long value, long slide, long size)
    {
        var span = FloorToSlide(value, slide) - value + size;
        return span <= 0 ? 0 : (int)((span + slide - 1) / slide);
    }

    /// <summary>The first (lowest) window start of the <paramref name="count"/> that contain the value.</summary>
    public static long FirstWindowStart(long value, long slide, int count) =>
        FloorToSlide(value, slide) - ((count - 1L) * slide);
}

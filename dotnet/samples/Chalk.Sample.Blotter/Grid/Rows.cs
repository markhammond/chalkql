using Apache.Arrow;
using Apache.Arrow.Arrays;

namespace Chalk.Sample.Blotter.Grid;

/// <summary>A row of a tab: identified, versioned, and stamped with when the feed sent it.</summary>
/// <remarks>
///     Times are UTC, kept to the microsecond, which is what PostgreSQL's <c>timestamp</c> keeps: a time with
///     finer ticks would compare differently in the database and in the host, and a page merged from
///     both would put a row in the wrong place.
/// </remarks>
public interface IRow
{
    /// <summary>The order's id, or the fill's: a UUID (version 7), which nothing here relies on the order of.</summary>
    Guid Id { get; }

    /// <summary>An order's version, numbered per order; a fill has only one.</summary>
    int Version { get; }

    /// <summary>When the feed sent this version.</summary>
    DateTime EventTime { get; }
}

/// <summary>Reading cells out of ChalkQL's Arrow batches.</summary>
public static class Cells
{
    /// <summary>A time truncated to the microsecond, as the database keeps it, and of no kind.</summary>
    public static DateTime Micros(DateTime time)
    {
        return new DateTime(time.Ticks - time.Ticks % 10, DateTimeKind.Unspecified);
    }

    public static long Int64(IArrowArray array, int row)
    {
        return array switch
        {
            Int64Array a => a.GetValue(row)!.Value,
            Int32Array a => a.GetValue(row)!.Value,
            _ => throw new InvalidOperationException($"expected an integer, got {array.GetType().Name}")
        };
    }

    /// <summary>A UUID: sixteen bytes, big-endian, as every ChalkQL source writes one.</summary>
    public static Guid Uuid(IArrowArray array, int row)
    {
        return new Guid(((FixedSizeBinaryArray)array).GetBytes(row), true);
    }

    public static double Double(IArrowArray array, int row)
    {
        return array switch
        {
            DoubleArray a => a.GetValue(row)!.Value,
            Int64Array a => a.GetValue(row)!.Value,
            _ => throw new InvalidOperationException($"expected a number, got {array.GetType().Name}")
        };
    }

    public static DateTime Time(IArrowArray array, int row)
    {
        return Micros(((TimestampArray)array).GetTimestamp(row)!.Value.UtcDateTime);
    }

    // The text columns hold a few hundred distinct values between them, so every row shares one
    // string per value rather than holding its own; an order's own reference is its own.
    public static string Text(IArrowArray array, int row, bool intern = true)
    {
        var text = array switch
        {
            StringViewArray a => a.GetString(row),
            StringArray a => a.GetString(row),
            _ => throw new InvalidOperationException($"expected text, got {array.GetType().Name}")
        };
        return intern ? string.Intern(text) : text;
    }
}
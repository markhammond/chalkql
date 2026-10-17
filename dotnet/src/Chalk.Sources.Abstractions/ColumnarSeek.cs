using Chalk.Catalog;
using ArrowSchema = Apache.Arrow.Schema;

namespace Chalk.Sources;

/// <summary>
/// A source that answers an as-of seek's lookups straight from the left rows' columns: one lookup a
/// left row, bounded by the row's keys and time, with no bound object made for any of them.
/// </summary>
internal interface IColumnarSeekSource
{
    /// <summary>
    /// The rows the seeks find, at most one for each and each row once, in the order the seeks first
    /// reach them; or null when the index, or the type of a bound, is not one the source seeks this way,
    /// and the lookup is asked as ranges instead.
    /// </summary>
    IColumnarScan? ColumnarSeek(ColumnarSeekRequest request, ScanContext context);
}

/// <summary>
/// An as-of seek's lookups: for each of <see cref="Rows"/>, the range of an index whose leading columns
/// hold the row's keys and whose next column is bounded by the row's time, read from its closest end
/// for one row. What an <see cref="IndexLookupRequest"/> of one <see cref="IndexKeyRange"/> a row asks,
/// with <see cref="IndexLookupRequest.RowsPerRange"/> one.
/// </summary>
internal sealed class ColumnarSeekRequest
{
    public required string Table { get; init; }

    public required string Index { get; init; }

    public required IReadOnlyList<int> Projection { get; init; }

    public required ArrowSchema OutputSchema { get; init; }

    public required int BatchSize { get; init; }

    /// <summary>The leading key columns' bounds, in the index's order, then the time's.</summary>
    public required SeekBound[] Bounds { get; init; }

    /// <summary>
    /// The left rows to seek for, in the first <see cref="RowCount"/> places. Set again for each seek
    /// of an operator that keeps its request, as the rest of it stands (F244).
    /// </summary>
    public required int[] Rows { get; set; }

    public required int RowCount { get; set; }

    /// <summary>
    /// Rows at or before each time, the closest the last (<c>&gt;=</c>, <c>&gt;</c>); otherwise rows at
    /// or after it, the closest the first.
    /// </summary>
    public required bool Backwards { get; init; }

    /// <summary>Whether a row at the time itself is in the range (<c>&gt;=</c>, <c>&lt;=</c>).</summary>
    public required bool Inclusive { get; init; }
}

/// <summary>One bound of a seek: a left row's value in a column of a type, or a value fixed for every row.</summary>
internal readonly struct SeekBound
{
    private SeekBound(ColumnView column, ChalkType type, object? value, bool isFixed)
    {
        Column = column;
        Type = type;
        Value = value;
        IsFixed = isFixed;
    }

    /// <summary>The column a left row's value is read from.</summary>
    public ColumnView Column { get; }

    public ChalkType Type { get; }

    /// <summary>The value every row is bounded by, when <see cref="IsFixed"/>, as a range bound holds it.</summary>
    public object? Value { get; }

    public bool IsFixed { get; }

    public static SeekBound Of(ColumnView column, ChalkType type) => new(column, type, null, isFixed: false);

    public static SeekBound Fixed(object value) => new(default, default!, value, isFixed: true);
}

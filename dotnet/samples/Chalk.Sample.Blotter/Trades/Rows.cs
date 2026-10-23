using System.Globalization;
using Apache.Arrow;
using Apache.Arrow.Arrays;
using DuckDB.NET.Data;

namespace Chalk.Sample.Blotter.Trades;

/// <summary>A row of either tab: identified, versioned, and stamped with when the feed sent it.</summary>
/// <remarks>
/// Times are UTC, kept to the microsecond, which is what DuckDB's <c>TIMESTAMP</c> keeps: a time with
/// finer ticks would compare differently in the database and in the host, and a page merged from
/// both would put a row in the wrong place.
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

/// <summary>
/// One version of a client's order with the house, as the feed sends it and the database stores it:
/// on a perpetual, in US dollars, through one of the client's channels, at the mark plus the
/// channel's markup. The house works it in fills; each fill, and a cancel, is a new version of the
/// order, numbered one higher. Versions are ordered per order only.
/// </summary>
public sealed record Order(
    Guid OrderId,
    int Version,
    DateTime EventTime,
    DateTime OrderTime,
    string ClOrdId,
    string Client,
    string Region,
    string Channel,
    string Instrument,
    string Side,
    double Qty,
    double FilledQty,
    double AvgPrice,
    double MarkupBps,
    string Status) : IRow
{
    /// <summary>The columns, in the order every statement selects them.</summary>
    public const string Select =
        "order_id, version, event_time, order_time, cl_ord_id, client, region, channel, instrument, side, qty, "
        + "filled_qty, avg_price, markup_bps, status";

    /// <summary>A rough size in memory, for the budget: the object and its fields; the text is interned.</summary>
    public const int Bytes = 136;

    public Guid Id => OrderId;

    public static Order Read(RecordBatch batch, int row) => new(
        Cells.Uuid(batch.Column(0), row),
        (int)Cells.Int64(batch.Column(1), row),
        Cells.Time(batch.Column(2), row),
        Cells.Time(batch.Column(3), row),
        Cells.Text(batch.Column(4), row, intern: false),
        Cells.Text(batch.Column(5), row),
        Cells.Text(batch.Column(6), row),
        Cells.Text(batch.Column(7), row),
        Cells.Text(batch.Column(8), row),
        Cells.Text(batch.Column(9), row),
        Cells.Double(batch.Column(10), row),
        Cells.Double(batch.Column(11), row),
        Cells.Double(batch.Column(12), row),
        Cells.Double(batch.Column(13), row),
        Cells.Text(batch.Column(14), row));

    public static Order Read(DuckDBDataReader reader) => new(
        reader.GetGuid(0),
        reader.GetInt32(1),
        Cells.Micros(reader.GetDateTime(2)),
        Cells.Micros(reader.GetDateTime(3)),
        reader.GetString(4),
        string.Intern(reader.GetString(5)),
        string.Intern(reader.GetString(6)),
        string.Intern(reader.GetString(7)),
        string.Intern(reader.GetString(8)),
        string.Intern(reader.GetString(9)),
        reader.GetDouble(10),
        reader.GetDouble(11),
        reader.GetDouble(12),
        reader.GetDouble(13),
        string.Intern(reader.GetString(14)));

    public object?[] Values() =>
        [OrderId, Version, EventTime, OrderTime, ClOrdId, Client, Region, Channel, Instrument, Side, Qty, FilledQty, AvgPrice, MarkupBps, Status];

    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"#{OrderId} v{Version} {Client} {Side} {Qty} {Instrument} filled {FilledQty} @ {AvgPrice} {Status}");
}

/// <summary>
/// One fill of a client order: the order's quantity the house filled at once, at a price. Fills are
/// append-only: a fill never changes, and the order it fills gets a new version beside it.
/// </summary>
public sealed record Fill(
    Guid FillId,
    Guid OrderId,
    DateTime EventTime,
    string ClOrdId,
    string Client,
    string Region,
    string Channel,
    string Instrument,
    string Side,
    double Qty,
    double Price,
    double Mid,
    double MarkupBps) : IRow
{
    public const string Select =
        "fill_id, order_id, event_time, cl_ord_id, client, region, channel, instrument, side, qty, price, mid, markup_bps";

    public const int Bytes = 120;

    public Guid Id => FillId;

    public int Version => 1;

    public static Fill Read(RecordBatch batch, int row) => new(
        Cells.Uuid(batch.Column(0), row),
        Cells.Uuid(batch.Column(1), row),
        Cells.Time(batch.Column(2), row),
        Cells.Text(batch.Column(3), row, intern: false),
        Cells.Text(batch.Column(4), row),
        Cells.Text(batch.Column(5), row),
        Cells.Text(batch.Column(6), row),
        Cells.Text(batch.Column(7), row),
        Cells.Text(batch.Column(8), row),
        Cells.Double(batch.Column(9), row),
        Cells.Double(batch.Column(10), row),
        Cells.Double(batch.Column(11), row),
        Cells.Double(batch.Column(12), row));

    public static Fill Read(DuckDBDataReader reader) => new(
        reader.GetGuid(0),
        reader.GetGuid(1),
        Cells.Micros(reader.GetDateTime(2)),
        reader.GetString(3),
        string.Intern(reader.GetString(4)),
        string.Intern(reader.GetString(5)),
        string.Intern(reader.GetString(6)),
        string.Intern(reader.GetString(7)),
        string.Intern(reader.GetString(8)),
        reader.GetDouble(9),
        reader.GetDouble(10),
        reader.GetDouble(11),
        reader.GetDouble(12));

    public object?[] Values() =>
        [FillId, OrderId, EventTime, ClOrdId, Client, Region, Channel, Instrument, Side, Qty, Price, Mid, MarkupBps];

    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture, $"fill #{FillId} of #{OrderId} {Side} {Qty} {Instrument} @ {Price}");
}

/// <summary>Reading cells out of ChalkQL's Arrow batches.</summary>
public static class Cells
{
    /// <summary>A time truncated to the microsecond, as the database keeps it, and of no kind.</summary>
    public static DateTime Micros(DateTime time) =>
        new(time.Ticks - (time.Ticks % 10), DateTimeKind.Unspecified);

    public static long Int64(IArrowArray array, int row) => array switch
    {
        Int64Array a => a.GetValue(row)!.Value,
        Int32Array a => a.GetValue(row)!.Value,
        _ => throw new InvalidOperationException($"expected an integer, got {array.GetType().Name}"),
    };

    /// <summary>A UUID: sixteen bytes, big-endian, as every ChalkQL source writes one.</summary>
    public static Guid Uuid(IArrowArray array, int row) =>
        new(((FixedSizeBinaryArray)array).GetBytes(row), bigEndian: true);

    public static double Double(IArrowArray array, int row) => array switch
    {
        DoubleArray a => a.GetValue(row)!.Value,
        Int64Array a => a.GetValue(row)!.Value,
        _ => throw new InvalidOperationException($"expected a number, got {array.GetType().Name}"),
    };

    public static DateTime Time(IArrowArray array, int row) =>
        Micros(((TimestampArray)array).GetTimestamp(row)!.Value.UtcDateTime);

    // The text columns hold a few hundred distinct values between them, so every row shares one
    // string per value rather than holding its own; an order's own reference is its own.
    public static string Text(IArrowArray array, int row, bool intern = true)
    {
        var text = array switch
        {
            StringViewArray a => a.GetString(row),
            StringArray a => a.GetString(row),
            _ => throw new InvalidOperationException($"expected text, got {array.GetType().Name}"),
        };
        return intern ? string.Intern(text) : text;
    }
}

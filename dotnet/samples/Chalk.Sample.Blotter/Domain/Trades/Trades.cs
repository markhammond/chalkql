using System.Data.Common;
using System.Globalization;
using Apache.Arrow;

namespace Chalk.Sample.Blotter.Domain;

/// <summary>
///     One version of a client's order with the house, as the feed sends it and the database stores it:
///     on a perpetual, in US dollars, through one of the client's channels, at the mark plus the
///     channel's markup. The house works it in fills; each fill, and a cancel, is a new version of the
///     order, numbered one higher. Versions are ordered per order only. <see cref="FilledAmt" /> is the
///     sum of its fills' amounts: the notional filled, in US dollars, as the house stamps it.
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
    double FilledAmt,
    double MarkupBps,
    string Status) : IRow
{
    /// <summary>The columns, in the order every statement selects them.</summary>
    public const string Select =
        "order_id, version, event_time, order_time, cl_ord_id, client, region, channel, instrument, side, qty, "
        + "filled_qty, avg_price, filled_amt, markup_bps, status";

    /// <summary>A rough size in memory, for the budget: the object and its fields; the text is interned.</summary>
    public const int Bytes = 144;

    public Guid Id => OrderId;

    public static Order Read(RecordBatch batch, int row) => new Order(
            Cells.Uuid(batch.Column(0), row),
            (int)Cells.Int64(batch.Column(1), row),
            Cells.Time(batch.Column(2), row),
            Cells.Time(batch.Column(3), row),
            Cells.Text(batch.Column(4), row, false),
            Cells.Text(batch.Column(5), row),
            Cells.Text(batch.Column(6), row),
            Cells.Text(batch.Column(7), row),
            Cells.Text(batch.Column(8), row),
            Cells.Text(batch.Column(9), row),
            Cells.Double(batch.Column(10), row),
            Cells.Double(batch.Column(11), row),
            Cells.Double(batch.Column(12), row),
            Cells.Double(batch.Column(13), row),
            Cells.Double(batch.Column(14), row),
            Cells.Text(batch.Column(15), row));

    public static Order Read(DbDataReader reader) => new Order(
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
            reader.GetDouble(14),
            string.Intern(reader.GetString(15)));

    public object?[] Values() => [
            OrderId, Version, EventTime, OrderTime, ClOrdId, Client, Region, Channel, Instrument, Side, Qty, FilledQty,
            AvgPrice, FilledAmt, MarkupBps, Status
        ];

    public override string ToString() => string.Create(
            CultureInfo.InvariantCulture,
            $"#{OrderId} v{Version} {Client} {Side} {Qty} {Instrument} filled {FilledQty} @ {AvgPrice} {Status}");
}

/// <summary>
///     One fill of a client order: the order's quantity the house filled at once, at a price, and the
///     amount, <c>qty × price</c>, the notional in US dollars. Fills are append-only: a fill never
///     changes, and the order it fills gets a new version beside it.
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
    double Amt,
    double Mid,
    double MarkupBps) : IRow
{
    public const string Select =
        "fill_id, order_id, event_time, cl_ord_id, client, region, channel, instrument, side, qty, price, amt, mid, markup_bps";

    public const int Bytes = 128;

    public Guid Id => FillId;

    public int Version => 1;

    public static Fill Read(RecordBatch batch, int row) => new Fill(
            Cells.Uuid(batch.Column(0), row),
            Cells.Uuid(batch.Column(1), row),
            Cells.Time(batch.Column(2), row),
            Cells.Text(batch.Column(3), row, false),
            Cells.Text(batch.Column(4), row),
            Cells.Text(batch.Column(5), row),
            Cells.Text(batch.Column(6), row),
            Cells.Text(batch.Column(7), row),
            Cells.Text(batch.Column(8), row),
            Cells.Double(batch.Column(9), row),
            Cells.Double(batch.Column(10), row),
            Cells.Double(batch.Column(11), row),
            Cells.Double(batch.Column(12), row),
            Cells.Double(batch.Column(13), row));

    public static Fill Read(DbDataReader reader) => new Fill(
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
            reader.GetDouble(12),
            reader.GetDouble(13));

    public object?[] Values() => [
            FillId, OrderId, EventTime, ClOrdId, Client, Region, Channel, Instrument, Side, Qty, Price, Amt, Mid,
            MarkupBps
        ];

    public override string ToString() => string.Create(
            CultureInfo.InvariantCulture, $"fill #{FillId} of #{OrderId} {Side} {Qty} {Instrument} @ {Price}");
}
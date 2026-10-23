using System.Globalization;
using Apache.Arrow;

namespace Chalk.Sample.Blotter.Trades;

/// <summary>
/// One version of one trade, exactly as the feed sends it and the database stores it. A version
/// never changes once published: a fill, a cancel or an amendment is a new version of the trade,
/// numbered one higher. Versions are ordered per trade only; nothing orders two trades' versions.
/// </summary>
/// <remarks>
/// Times are UTC, kept to the microsecond, which is what DuckDB's <c>TIMESTAMP</c> keeps: a time
/// with finer ticks would compare differently in the database and in the host, and a page merged
/// from both would put a row in the wrong place.
/// </remarks>
public sealed record TradeVersion(
    long TradeId,
    int Version,
    DateTime EventTime,
    DateTime TradeTime,
    string Client,
    string Symbol,
    string Side,
    long OrderQty,
    long FilledQty,
    double AvgPrice,
    string Status,
    string Venue)
{
    /// <summary>The columns, in the order every statement in the sample selects them.</summary>
    public const string Select =
        "trade_id, version, event_time, trade_time, client, symbol, side, order_qty, filled_qty, avg_price, status, venue";

    /// <summary>A rough size in memory, for the budget: the object and its fields; the text is interned.</summary>
    public const int Bytes = 112;

    /// <summary>A time truncated to the microsecond, as the database keeps it, and of no kind.</summary>
    public static DateTime Micros(DateTime time) =>
        new(time.Ticks - (time.Ticks % 10), DateTimeKind.Unspecified);

    /// <summary>Reads row <paramref name="row"/> of a batch selected with <see cref="Select"/>, from column <paramref name="first"/>.</summary>
    public static TradeVersion Read(RecordBatch batch, int row, int first = 0) => new(
        Int64(batch.Column(first), row),
        (int)Int64(batch.Column(first + 1), row),
        Time(batch.Column(first + 2), row),
        Time(batch.Column(first + 3), row),
        Text(batch.Column(first + 4), row),
        Text(batch.Column(first + 5), row),
        Text(batch.Column(first + 6), row),
        Int64(batch.Column(first + 7), row),
        Int64(batch.Column(first + 8), row),
        ((DoubleArray)batch.Column(first + 9)).GetValue(row)!.Value,
        Text(batch.Column(first + 10), row),
        Text(batch.Column(first + 11), row));

    internal static long Int64(IArrowArray array, int row) => array switch
    {
        Int64Array a => a.GetValue(row)!.Value,
        Int32Array a => a.GetValue(row)!.Value,
        _ => throw new InvalidOperationException($"expected an integer, got {array.GetType().Name}"),
    };

    internal static DateTime Time(IArrowArray array, int row) =>
        Micros(((TimestampArray)array).GetTimestamp(row)!.Value.UtcDateTime);

    // The text columns hold a few hundred distinct values between them, so every row shares one
    // string per value rather than holding its own.
    internal static string Text(IArrowArray array, int row) => string.Intern(array switch
    {
        StringViewArray a => a.GetString(row),
        StringArray a => a.GetString(row),
        _ => throw new InvalidOperationException($"expected text, got {array.GetType().Name}"),
    });

    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"#{TradeId} v{Version} {Client} {Side} {OrderQty} {Symbol} filled {FilledQty} @ {AvgPrice} {Status}");
}

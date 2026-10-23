using System.Globalization;

namespace Chalk.Sample.Blotter.Trades;

/// <summary>A column of the blotter: its SQL name, how the host compares and shows it.</summary>
/// <remarks>
/// The host's comparison must order rows exactly as the database's <c>ORDER BY</c> does, because a
/// page is merged from both: text byte by byte (DuckDB's default collation is binary, and every
/// value here is ASCII), numbers and times by value. No column is nullable, so no rule for nulls is
/// needed on either side.
/// </remarks>
public sealed class Column
{
    private Column(string name, string title, bool numeric, Comparison<TradeVersion> compare, Func<TradeVersion, string> show)
    {
        Name = name;
        Title = title;
        Numeric = numeric;
        Compare = compare;
        Show = show;
    }

    public string Name { get; }

    public string Title { get; }

    /// <summary>Right-aligned in the grid.</summary>
    public bool Numeric { get; }

    public Comparison<TradeVersion> Compare { get; }

    public Func<TradeVersion, string> Show { get; }

    public static readonly Column TradeId = new(
        "trade_id", "Trade", true, (a, b) => a.TradeId.CompareTo(b.TradeId), t => Number(t.TradeId));

    public static readonly Column Version = new(
        "version", "Ver", true, (a, b) => a.Version.CompareTo(b.Version), t => Number(t.Version));

    public static readonly Column TradeTime = new(
        "trade_time", "Traded", false, (a, b) => a.TradeTime.CompareTo(b.TradeTime),
        t => t.TradeTime.ToString("MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));

    public static readonly Column EventTime = new(
        "event_time", "Updated", false, (a, b) => a.EventTime.CompareTo(b.EventTime),
        t => t.EventTime.ToString("MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));

    public static readonly Column Client = new(
        "client", "Client", false, (a, b) => string.CompareOrdinal(a.Client, b.Client), t => t.Client);

    public static readonly Column Symbol = new(
        "symbol", "Symbol", false, (a, b) => string.CompareOrdinal(a.Symbol, b.Symbol), t => t.Symbol);

    public static readonly Column Side = new(
        "side", "Side", false, (a, b) => string.CompareOrdinal(a.Side, b.Side), t => t.Side);

    public static readonly Column OrderQty = new(
        "order_qty", "Qty", true, (a, b) => a.OrderQty.CompareTo(b.OrderQty), t => Number(t.OrderQty));

    public static readonly Column FilledQty = new(
        "filled_qty", "Filled", true, (a, b) => a.FilledQty.CompareTo(b.FilledQty), t => Number(t.FilledQty));

    public static readonly Column AvgPrice = new(
        "avg_price", "Avg px", true, (a, b) => a.AvgPrice.CompareTo(b.AvgPrice),
        t => t.AvgPrice.ToString("0.0000", CultureInfo.InvariantCulture));

    public static readonly Column Status = new(
        "status", "Status", false, (a, b) => string.CompareOrdinal(a.Status, b.Status), t => t.Status);

    public static readonly Column Venue = new(
        "venue", "Venue", false, (a, b) => string.CompareOrdinal(a.Venue, b.Venue), t => t.Venue);

    /// <summary>Every column, in the grid's order.</summary>
    public static readonly IReadOnlyList<Column> All =
    [
        TradeId, Version, TradeTime, Client, Symbol, Side, OrderQty, FilledQty, AvgPrice, Status, Venue, EventTime,
    ];

    public static Column Named(string name) =>
        All.FirstOrDefault(c => c.Name == name)
        ?? throw new ArgumentException($"the blotter has no column '{name}'", nameof(name));

    private static string Number(long value) => value.ToString("#,0", CultureInfo.InvariantCulture);
}

using Apache.Arrow;
using Chalk.Sample.Blotter.Market;
using System.Data.Common;

namespace Chalk.Sample.Blotter.Trades;

/// <summary>
/// A tab of the blotter: client orders, which change, or their fills, which do not. What a tab
/// reads, and how its rows are keyed and shown.
/// </summary>
/// <remarks>
/// Orders are read live from <c>orders_current</c>, one row per order, and the tail overrides an
/// order whose version there is older than the one the stream has sent. Fills never change, so a
/// fill in both the table and the tail is the same row, and nothing is overridden.
/// </remarks>
public sealed class Tab
{
    private Tab(
        string name, string title, IReadOnlyList<Column> columns, Column key, Column time, string table,
        string select, Func<RecordBatch, int, IRow> read, Func<DbDataReader, IRow> readDb, bool overrides)
    {
        Name = name;
        Title = title;
        Columns = columns;
        Key = key;
        Time = time;
        Table = table;
        Select = select;
        Read = read;
        ReadDb = readDb;
        Overrides = overrides;
    }

    public string Name { get; }

    public string Title { get; }

    /// <summary>Every column, in the grid's order.</summary>
    public IReadOnlyList<Column> Columns { get; }

    /// <summary>The unique key, which breaks every tie in a sort.</summary>
    public Column Key { get; }

    /// <summary>When the row happened: the period of interest that bounds a filter's list of values.</summary>
    public Column Time { get; }

    /// <summary>The table pages are read from, as ChalkQL names it.</summary>
    public string Table { get; }

    public string Select { get; }

    public Func<RecordBatch, int, IRow> Read { get; }

    /// <summary>A row read straight from the database, which only the oracle does.</summary>
    public Func<DbDataReader, IRow> ReadDb { get; }

    /// <summary>Whether a row in the tail stands in for the snapshot's row of the same key.</summary>
    public bool Overrides { get; }

    /// <summary>The newest first, which is what a blotter opens on.</summary>
    public SortSpec DefaultSort => SortSpec.Default(this);

    public Column Named(string name) =>
        Columns.FirstOrDefault(c => c.Name == name) ?? throw new ArgumentException($"{Name} has no column '{name}'", nameof(name));

    public static readonly Tab Orders = MakeOrders();

    public static readonly Tab Fills = MakeFills();

    public static readonly IReadOnlyList<Tab> All = [Orders, Fills];

    public override string ToString() => Name;

    private static Tab MakeOrders()
    {
        var key = Column.Uuid<Order>("order_id", "Order", o => o.OrderId);
        var time = Column.Time<Order>("order_time", "Ordered", o => o.OrderTime);
        return new Tab(
            "orders",
            "Client orders",
            [
                key,
                Column.Int<Order>("version", "Ver", o => o.Version),
                time,
                Column.Text<Order>("cl_ord_id", "ClOrdId", o => o.ClOrdId),
                Column.Text<Order>("client", "Client", o => o.Client),
                Column.Choice<Order>("region", "Region", o => o.Region, Reference.Regions),
                Column.Choice<Order>("channel", "Channel", o => o.Channel, Reference.Channels),
                Column.Text<Order>("instrument", "Instrument", o => o.Instrument),
                Column.Choice<Order>("side", "Side", o => o.Side, Reference.Sides),
                Column.Number<Order>("qty", "Qty", o => o.Qty, "#,0.####"),
                Column.Number<Order>("filled_qty", "Filled", o => o.FilledQty, "#,0.####"),
                Column.Number<Order>("avg_price", "Avg px", o => o.AvgPrice, "#,0.0000"),
                Column.Number<Order>("markup_bps", "Markup", o => o.MarkupBps, "0.#"),
                Column.Choice<Order>("status", "Status", o => o.Status, Reference.Statuses),
                Column.Time<Order>("event_time", "Updated", o => o.EventTime),
            ],
            key,
            time,
            "blotter.orders_current",
            Order.Select,
            (batch, row) => Order.Read(batch, row),
            reader => Order.Read(reader),
            overrides: true);
    }

    private static Tab MakeFills()
    {
        var key = Column.Uuid<Fill>("fill_id", "Fill", f => f.FillId);
        var time = Column.Time<Fill>("event_time", "Filled at", f => f.EventTime);
        return new Tab(
            "fills",
            "Fills",
            [
                key,
                Column.Uuid<Fill>("order_id", "Order", f => f.OrderId),
                time,
                Column.Text<Fill>("cl_ord_id", "ClOrdId", f => f.ClOrdId),
                Column.Text<Fill>("client", "Client", f => f.Client),
                Column.Choice<Fill>("region", "Region", f => f.Region, Reference.Regions),
                Column.Choice<Fill>("channel", "Channel", f => f.Channel, Reference.Channels),
                Column.Text<Fill>("instrument", "Instrument", f => f.Instrument),
                Column.Choice<Fill>("side", "Side", f => f.Side, Reference.Sides),
                Column.Number<Fill>("qty", "Qty", f => f.Qty, "#,0.####"),
                Column.Number<Fill>("price", "Price", f => f.Price, "#,0.0000"),
                Column.Number<Fill>("mid", "Mid", f => f.Mid, "#,0.0000"),
                Column.Number<Fill>("markup_bps", "Markup", f => f.MarkupBps, "0.#"),
            ],
            key,
            time,
            "blotter.fills",
            Fill.Select,
            (batch, row) => Fill.Read(batch, row),
            reader => Fill.Read(reader),
            overrides: false);
    }
}

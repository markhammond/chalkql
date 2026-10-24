using System.Collections.Immutable;
using System.Globalization;

namespace Chalk.Sample.Blotter.Domain;

/// <summary>
/// The blotter's two tabs: client orders, which change, read live from <c>orders_current</c> and
/// overridden by newer versions in the tail; and their fills, which never change.
/// </summary>
public static class Tabs
{
    public static readonly Tab Orders = MakeOrders();

    public static readonly Tab Fills = MakeFills();

    public static readonly IReadOnlyList<Tab> All = [Orders, Fills];

    private const string Recon =
        "How long the database took to hold what the streams sent, from the exchange's time: counting while it has not, "
        + "a dash once the row has left the tail";

    private static Tab MakeOrders()
    {
        var key = Column.Uuid<Order>("order_id", "Order", o => o.OrderId);
        var time = Column.Time<Order>("order_time", "Time", o => o.OrderTime, "When the client placed the order");
        return new Tab(
            "orders",
            "Client orders",
            [
                time,
                key,
                Column.Int<Order>("version", "Ver", o => o.Version),
                Column.Text<Order>("cl_ord_id", "ClOrdId", o => o.ClOrdId),
                Column.Text<Order>("client", "Client", o => o.Client),
                Column.Choice<Order>("region", "Region", o => o.Region, Reference.Regions),
                Column.Choice<Order>("channel", "Channel", o => o.Channel, Reference.Channels),
                Column.Text<Order>("instrument", "Instrument", o => o.Instrument),
                Column.Choice<Order>("side", "Side", o => o.Side, Reference.Sides),
                Column.Number<Order>("qty", "Qty", o => o.Qty, 4),
                Column.Number<Order>("filled_qty", "Filled", o => o.FilledQty, 4),
                // Narrow no-break space, and no-break space: a heading stays on one line.
                Column.Shown<Order>("filled_pct", "Fill\u202F%", o => o.Qty > 0 && o.FilledQty > 0 ? Column.Finance(100 * o.FilledQty / o.Qty, 1) + "%" : Column.Nought, numeric: true, "How much of the order is filled"),
                Column.Price<Order>("avg_price", "Avg\u00A0px", o => o.AvgPrice),
                Column.Number<Order>("markup_bps", "Markup", o => o.MarkupBps, 1),
                Column.Choice<Order>("status", "Status", o => o.Status, Reference.Statuses),
                Column.Time<Order>("event_time", "Updated", o => o.EventTime),
                Column.Reconciled("recon", "Recon", Recon),
            ],
            key,
            time,
            "blotter.orders_current",
            Order.Select,
            (batch, row) => Order.Read(batch, row),
            reader => Order.Read(reader),
            overrides: true,
            frozen: 2,
            hiddenAtFirst: ImmutableHashSet.Create(StringComparer.Ordinal, "version", "region", "channel", "filled_qty"),
            home: true,
            describe: row => row is Order o
                ? string.Create(CultureInfo.InvariantCulture, $"{o.Client} {o.Side} {Column.Finance(o.Qty, 4)} {o.Instrument} · {o.Status}")
                : row.Id.ToString());
    }

    private static Tab MakeFills()
    {
        var key = Column.Uuid<Fill>("fill_id", "Fill", f => f.FillId);
        var time = Column.Time<Fill>("event_time", "Time", f => f.EventTime, "When the order was filled");
        return new Tab(
            "fills",
            "Fills",
            [
                time,
                key,
                Column.Uuid<Fill>("order_id", "Order", f => f.OrderId),
                Column.Text<Fill>("cl_ord_id", "ClOrdId", f => f.ClOrdId),
                Column.Text<Fill>("client", "Client", f => f.Client),
                Column.Choice<Fill>("region", "Region", f => f.Region, Reference.Regions),
                Column.Choice<Fill>("channel", "Channel", f => f.Channel, Reference.Channels),
                Column.Text<Fill>("instrument", "Instrument", f => f.Instrument),
                Column.Choice<Fill>("side", "Side", f => f.Side, Reference.Sides),
                Column.Number<Fill>("qty", "Qty", f => f.Qty, 4),
                Column.Price<Fill>("price", "Price", f => f.Price),
                Column.Price<Fill>("mid", "Mid", f => f.Mid),
                Column.Number<Fill>("markup_bps", "Markup", f => f.MarkupBps, 1),
                Column.Reconciled("recon", "Recon", Recon),
            ],
            key,
            time,
            "blotter.fills",
            Fill.Select,
            (batch, row) => Fill.Read(batch, row),
            reader => Fill.Read(reader),
            overrides: false,
            frozen: 2,
            hiddenAtFirst: ImmutableHashSet.Create(StringComparer.Ordinal, "region", "channel"),
            paths: [new TabPath("order", Orders, "order_id", "The fill's order, at the newest version the host or the database knows")],
            describe: row => row is Fill f
                ? string.Create(CultureInfo.InvariantCulture, $"{f.Client} {f.Side} {Column.Finance(f.Qty, 4)} {f.Instrument} @ {Column.Significant(f.Price)}")
                : row.Id.ToString());
    }
}

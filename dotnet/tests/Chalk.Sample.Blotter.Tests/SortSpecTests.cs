using Chalk.Sample.Blotter.Live;
using Chalk.Sample.Blotter.Trades;

namespace Chalk.Sample.Blotter.Tests;

/// <summary>A tab's order: what a header click does, and how the order is written for the database.</summary>
public sealed class SortSpecTests
{
    private static readonly Tab Orders = Tab.Orders;

    [Fact]
    public void Each_tab_opens_newest_first_with_its_key_breaking_ties()
    {
        Assert.Equal("order_time desc,order_id desc", Orders.DefaultSort.Name);
        Assert.Equal("event_time desc,fill_id desc", Tab.Fills.DefaultSort.Name);
        Assert.Equal("order_time DESC, order_id DESC", Orders.DefaultSort.OrderBy());
    }

    [Fact]
    public void A_click_sorts_by_that_column_alone_and_a_second_click_turns_it_round()
    {
        var once = Orders.DefaultSort.Click(Orders.Named("client"), shift: false);
        Assert.Equal("client,order_id desc", once.Name);
        Assert.Equal("client desc,order_id desc", once.Click(Orders.Named("client"), shift: false).Name);
    }

    [Fact]
    public void A_shift_click_adds_the_column_then_turns_it_round_then_takes_it_out()
    {
        var client = Orders.DefaultSort.Click(Orders.Named("client"), shift: false);
        var filled = Orders.Named("filled_qty");

        var added = client.Click(filled, shift: true);
        Assert.Equal("client,filled_qty,order_id desc", added.Name);
        Assert.Equal(2, added.RankOf(filled));

        var turned = added.Click(filled, shift: true);
        Assert.Equal("client,filled_qty desc,order_id desc", turned.Name);

        var removed = turned.Click(filled, shift: true);
        Assert.Equal("client,order_id desc", removed.Name);
        Assert.Equal(0, removed.RankOf(filled));
    }

    [Fact]
    public void Taking_out_the_last_column_goes_back_to_newest_first()
    {
        var client = SortSpec.Of(Orders, new SortColumn(Orders.Named("client"), Descending: true));
        Assert.Equal(Orders.DefaultSort, client.Click(Orders.Named("client"), shift: true));
    }

    [Fact]
    public void Sorting_by_the_key_adds_no_second_key()
    {
        Assert.Equal("order_id", SortSpec.Of(Orders, new SortColumn(Orders.Key, Descending: false)).Name);
    }

    [Fact]
    public void The_host_orders_rows_as_the_database_would_text_by_bytes_and_ties_by_key()
    {
        var order = SortSpec.Of(Orders, new SortColumn(Orders.Named("instrument"), false), new SortColumn(Orders.Named("avg_price"), true));
        Order[] rows =
        [
            Make(1, "SOL/USD Perp", 10.5),
            Make(2, "ADA/USD Perp", 10.5),
            Make(3, "ADA/USD Perp", 99.0),
            Make(4, "ADA/USD Perp", 10.5),
            Make(5, "ada/USD Perp", 1.0),
        ];

        Array.Sort(rows, order);

        // Upper case before lower case, as a binary collation has it; then price descending; then
        // the newer order first.
        Assert.Equal([Id(3), Id(4), Id(2), Id(1), Id(5)], rows.Select(r => r.OrderId));
    }

    [Fact]
    public void Before_a_key_is_written_column_by_column_with_its_values_bound_as_their_columns_types()
    {
        var order = SortSpec.Of(Orders, new SortColumn(Orders.Named("client"), false), new SortColumn(Orders.Named("version"), true));
        var values = new Dictionary<string, object?>();

        var condition = SnapshotQueries.Before(order, Make(42, "BTC/USD Perp", 1.0) with { Version = 3 }, values);

        Assert.Equal(
            "client < @k0 OR (client = @k0 AND (version > @k1 OR (version = @k1 AND (order_id > @k2))))",
            condition);
        Assert.Equal("CEDAR", values["k0"]);
        Assert.Equal(3, values["k1"]);
        Assert.Equal(Id(42), values["k2"]);
    }

    /// <summary>Ids that sort as their numbers do, as version 7 UUIDs made a millisecond apart would.</summary>
    internal static Guid Id(long n) => Guid.Parse($"00000000-0000-7000-8000-{n:D12}");

    internal static Order Make(long id, string instrument, double price) => new(
        Id(id), 1, new DateTime(2026, 10, 23, 9, 0, 0), new DateTime(2026, 10, 23, 9, 0, 0), $"CEDAR-{id}", "CEDAR",
        "Americas", "FIX", instrument, "Buy", 1, 0, price, 6, "New");
}

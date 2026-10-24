using Chalk.Sample.Blotter.Live;
using Chalk.Sample.Blotter.Grid;
using Chalk.Sample.Blotter.Domain;

namespace Chalk.Sample.Blotter.Tests;

/// <summary>A tab's order: what a header click does, and how the order is written for the database.</summary>
public sealed class SortSpecTests
{
    private static readonly Tab Orders = Tabs.Orders;

    [Fact]
    public void Each_tab_opens_newest_first_with_its_key_breaking_ties()
    {
        Assert.Equal("order_time desc,order_id desc", Orders.DefaultSort.Name);
        Assert.Equal("event_time desc,fill_id desc", Tabs.Fills.DefaultSort.Name);
        Assert.Equal("order_time DESC, order_id DESC", Orders.DefaultSort.OrderBy());
    }

    [Fact]
    public void A_click_sorts_by_that_column_alone_descending_then_ascending_then_not_at_all()
    {
        var client = Orders.Named("client");
        var once = Orders.DefaultSort.Click(client, shift: false);
        var twice = once.Click(client, shift: false);

        Assert.Equal("client desc,order_id desc", once.Name);
        Assert.Equal("client,order_id desc", twice.Name);
        Assert.Equal(Orders.DefaultSort, twice.Click(client, shift: false));
    }

    [Fact]
    public void A_click_on_a_column_of_a_sort_by_several_sorts_by_it_alone()
    {
        var both = Orders.DefaultSort.Click(Orders.Named("client"), shift: false).Click(Orders.Named("qty"), shift: true);

        Assert.Equal("qty desc,order_id desc", both.Click(Orders.Named("qty"), shift: false).Name);
    }

    [Fact]
    public void A_shift_click_adds_the_column_then_turns_it_ascending_then_takes_it_out()
    {
        var client = Orders.DefaultSort.Click(Orders.Named("client"), shift: false);
        var filled = Orders.Named("filled_qty");

        var added = client.Click(filled, shift: true);
        Assert.Equal("client desc,filled_qty desc,order_id desc", added.Name);
        Assert.Equal(2, added.RankOf(filled));

        var turned = added.Click(filled, shift: true);
        Assert.Equal("client desc,filled_qty,order_id desc", turned.Name);

        var removed = turned.Click(filled, shift: true);
        Assert.Equal("client desc,order_id desc", removed.Name);
        Assert.Equal(0, removed.RankOf(filled));
    }

    [Fact]
    public void Taking_out_the_last_column_goes_back_to_newest_first()
    {
        var client = SortSpec.Of(Orders, new SortColumn(Orders.Named("client"), Descending: false));
        Assert.Equal(Orders.DefaultSort, client.Click(Orders.Named("client"), shift: true));
    }

    [Fact]
    public void Newest_first_shows_time_by_default_and_a_click_on_it_goes_ascending_then_back()
    {
        var time = Orders.Time;
        var none = Orders.DefaultSort;

        Assert.True(none.IsDefault);
        Assert.Equal(0, none.RankOf(time));
        Assert.True(none.ByDefault(time));
        Assert.False(none.ByDefault(Orders.Named("client")));

        // Descending already, by default: the first click turns it round.
        var ascending = none.Click(time, shift: false);
        Assert.False(ascending.IsDefault);
        Assert.False(ascending.ByDefault(time));
        Assert.Equal("order_time,order_id desc", ascending.Name);
        Assert.Equal(1, ascending.RankOf(time));
        Assert.True(ascending.Click(time, shift: false).IsDefault);

        // Descending, chosen within another sort, cycles like any column.
        var both = none.Click(Orders.Named("client"), shift: false).Click(time, shift: true);
        Assert.Equal("client desc,order_time desc,order_id desc", both.Name);
        Assert.Equal("client desc,order_time,order_id desc", both.Click(time, shift: true).Name);
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

        var condition = SourceQueries.Before(order, Make(42, "BTC/USD Perp", 1.0) with { Version = 3 }, values);

        // The leading column's bound comes first, alone, so the database can start an index there.
        Assert.Equal(
            "client <= @k0 AND (client < @k0 OR (client = @k0 AND (version > @k1 OR (version = @k1 AND (order_id > @k2)))))",
            condition);
        Assert.Equal("CEDAR", values["k0"]);
        Assert.Equal(3, values["k1"]);
        Assert.Equal(Id(42), values["k2"]);
    }

    [Fact]
    public void From_a_key_reads_on_at_it_and_after_it_and_the_leading_bound_follows_the_direction()
    {
        var order = Orders.DefaultSort;
        var values = new Dictionary<string, object?>();

        var condition = SourceQueries.Seek(order, Make(7, "SOL/USD Perp", 1.0), inclusive: true, values);

        Assert.Equal("order_time <= @k0 AND (order_time < @k0 OR (order_time = @k0 AND (order_id <= @k1)))", condition);
        Assert.Equal(Id(7), values["k1"]);
    }

    [Fact]
    public void Turned_round_every_key_runs_the_other_way_and_rows_come_nearest_first()
    {
        var order = SortSpec.Of(Orders, new SortColumn(Orders.Named("avg_price"), Descending: true));
        Order[] rows = [Make(1, "ADA/USD Perp", 1.0), Make(2, "ADA/USD Perp", 3.0), Make(3, "ADA/USD Perp", 2.0)];

        Array.Sort(rows, order.Reversed());

        Assert.Equal("avg_price,order_id", order.Reversed().Name);
        Assert.Equal([Id(1), Id(3), Id(2)], rows.Select(r => r.OrderId));
        Assert.Equal(order, order.Reversed().Reversed());
    }

    /// <summary>Ids that sort as their numbers do, as version 7 UUIDs made a millisecond apart would.</summary>
    internal static Guid Id(long n) => Guid.Parse($"00000000-0000-7000-8000-{n:D12}");

    internal static Order Make(long id, string instrument, double price) => new(
        Id(id), 1, new DateTime(2026, 10, 23, 9, 0, 0), new DateTime(2026, 10, 23, 9, 0, 0), $"CEDAR-{id}", "CEDAR",
        "Americas", "FIX", instrument, "Buy", 1, 0, price, 6, "New");
}

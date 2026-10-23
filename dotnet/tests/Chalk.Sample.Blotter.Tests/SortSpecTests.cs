using Chalk.Sample.Blotter.Live;
using Chalk.Sample.Blotter.Trades;

namespace Chalk.Sample.Blotter.Tests;

/// <summary>The grid's order: what a header click does, and how the order is written for the database.</summary>
public sealed class SortSpecTests
{
    [Fact]
    public void A_blotter_opens_newest_first_with_the_trade_id_breaking_ties()
    {
        Assert.Equal("trade_time desc,trade_id desc", SortSpec.Default.Name);
        Assert.Equal("trade_time DESC, trade_id DESC", SortSpec.Default.OrderBy());
    }

    [Fact]
    public void A_click_sorts_by_that_column_alone_and_a_second_click_turns_it_round()
    {
        var once = SortSpec.Default.Click(Column.Symbol, shift: false);
        Assert.Equal("symbol,trade_id desc", once.Name);

        var twice = once.Click(Column.Symbol, shift: false);
        Assert.Equal("symbol desc,trade_id desc", twice.Name);
    }

    [Fact]
    public void A_shift_click_adds_the_column_then_turns_it_round_then_takes_it_out()
    {
        var symbol = SortSpec.Default.Click(Column.Symbol, shift: false);

        var added = symbol.Click(Column.FilledQty, shift: true);
        Assert.Equal("symbol,filled_qty,trade_id desc", added.Name);
        Assert.Equal(2, added.RankOf(Column.FilledQty));

        var turned = added.Click(Column.FilledQty, shift: true);
        Assert.Equal("symbol,filled_qty desc,trade_id desc", turned.Name);

        var removed = turned.Click(Column.FilledQty, shift: true);
        Assert.Equal("symbol,trade_id desc", removed.Name);
        Assert.Equal(0, removed.RankOf(Column.FilledQty));
    }

    [Fact]
    public void Taking_out_the_last_column_goes_back_to_newest_first()
    {
        var symbol = SortSpec.Of(new SortColumn(Column.Symbol, Descending: true));
        Assert.Equal(SortSpec.Default, symbol.Click(Column.Symbol, shift: true));
    }

    [Fact]
    public void Sorting_by_the_trade_id_adds_no_second_trade_id()
    {
        var byId = SortSpec.Of(new SortColumn(Column.TradeId, Descending: false));
        Assert.Equal("trade_id", byId.Name);
    }

    [Fact]
    public void The_host_orders_rows_as_the_database_would_text_by_bytes_and_ties_by_trade()
    {
        var order = SortSpec.Of(new SortColumn(Column.Symbol, false), new SortColumn(Column.AvgPrice, true));
        TradeVersion[] rows =
        [
            Trade(1, "MSFT", 10.5),
            Trade(2, "AAPL", 10.5),
            Trade(3, "AAPL", 99.0),
            Trade(4, "AAPL", 10.5),
            Trade(5, "aapl", 1.0),
        ];

        Array.Sort(rows, order);

        // Upper case before lower case, as a binary collation has it; then price descending; then
        // the newer trade first.
        Assert.Equal([3L, 4, 2, 1, 5], rows.Select(r => r.TradeId));
    }

    [Fact]
    public void Before_a_key_is_written_column_by_column_with_its_values_bound()
    {
        var order = SortSpec.Of(new SortColumn(Column.Symbol, false), new SortColumn(Column.FilledQty, true));
        var values = new Dictionary<string, object?>();

        var condition = SnapshotQueries.Before(order, Trade(42, "IBM", 1.0) with { FilledQty = 300 }, values);

        Assert.Equal(
            "symbol < @k0 OR (symbol = @k0 AND (filled_qty > @k1 OR (filled_qty = @k1 AND (trade_id > @k2))))",
            condition);
        Assert.Equal("IBM", values["k0"]);
        Assert.Equal(300L, values["k1"]);
        Assert.Equal(42L, values["k2"]);
    }

    internal static TradeVersion Trade(long id, string symbol, double price) => new(
        id, 1, new DateTime(2026, 10, 23, 9, 0, 0), new DateTime(2026, 10, 23, 9, 0, 0),
        "C0001", symbol, "Buy", 1000, 0, price, "New", "XNAS");
}

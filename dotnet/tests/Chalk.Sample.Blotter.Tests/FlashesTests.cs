using Chalk.Sample.Blotter.Components;
using Chalk.Sample.Blotter.Trades;

namespace Chalk.Sample.Blotter.Tests;

/// <summary>What flashes: changed cells, and trades the feed has just opened; nothing else.</summary>
public sealed class FlashesTests
{
    private static readonly DateTime Now = new(2026, 10, 23, 9, 0, 0);
    private static readonly int Filled = IndexOf(Column.FilledQty);
    private static readonly int Price = IndexOf(Column.AvgPrice);
    private static readonly int Symbol = IndexOf(Column.Symbol);

    [Fact]
    public void The_first_page_flashes_nothing()
    {
        var flashes = new Flashes();

        var rows = flashes.Decorate([Trade(1, version: 1, opened: Now)], Now, Now);

        Assert.All(rows[0].Flash, Assert.Null);
        Assert.Null(rows[0].RowClass);
    }

    [Fact]
    public void A_newer_version_flashes_only_the_cells_that_changed()
    {
        var flashes = new Flashes();
        var before = Trade(1, version: 1, opened: Now);
        flashes.Decorate([before], Now, Now);

        var after = before with { Version = 2, FilledQty = 500, AvgPrice = 101.25 };
        var rows = flashes.Decorate([after], Now, Now.AddMilliseconds(100));

        Assert.Equal("flash-a", rows[0].Flash[Filled]);
        Assert.Equal("flash-a", rows[0].Flash[Price]);
        Assert.Null(rows[0].Flash[Symbol]);
    }

    [Fact]
    public void A_cell_that_changes_again_while_flashing_starts_its_flash_over()
    {
        var flashes = new Flashes();
        var v1 = Trade(1, version: 1, opened: Now);
        flashes.Decorate([v1], Now, Now);
        flashes.Decorate([v1 with { Version = 2, FilledQty = 500 }], Now, Now.AddMilliseconds(100));

        var rows = flashes.Decorate([v1 with { Version = 3, FilledQty = 900 }], Now, Now.AddMilliseconds(400));

        // The other name of the same animation, which the browser plays from the start.
        Assert.Equal("flash-b", rows[0].Flash[Filled]);
    }

    [Fact]
    public void A_render_in_between_keeps_the_flash_without_starting_it_again_and_it_ends()
    {
        var flashes = new Flashes();
        var v1 = Trade(1, version: 1, opened: Now);
        flashes.Decorate([v1], Now, Now);
        var v2 = v1 with { Version = 2, FilledQty = 500 };
        flashes.Decorate([v2], Now, Now.AddMilliseconds(100));

        Assert.Equal("flash-a", flashes.Decorate([v2], Now, Now.AddMilliseconds(600))[0].Flash[Filled]);
        Assert.Null(flashes.Decorate([v2], Now, Now.AddSeconds(3))[0].Flash[Filled]);
    }

    [Fact]
    public void A_trade_opened_after_the_last_page_was_read_flashes_its_row()
    {
        var flashes = new Flashes();
        flashes.Decorate([Trade(1, version: 1, opened: Now)], ingestedThrough: Now, Now);

        var opened = Trade(2, version: 1, opened: Now.AddMilliseconds(50));
        var rows = flashes.Decorate([opened, Trade(1, version: 1, opened: Now)], Now.AddMilliseconds(80), Now.AddMilliseconds(100));

        Assert.Equal("new-a", rows[0].RowClass);
        Assert.Null(rows[1].RowClass);
    }

    [Fact]
    public void A_trade_scrolled_into_view_does_not_flash()
    {
        var flashes = new Flashes();
        flashes.Decorate([Trade(1, version: 1, opened: Now)], ingestedThrough: Now, Now);

        // Older than what the last page covered, and changed long ago: it is only new to this page.
        var old = Trade(7, version: 4, opened: Now.AddHours(-2));
        var rows = flashes.Decorate([old], Now.AddMilliseconds(80), Now.AddMilliseconds(100));

        Assert.Null(rows[0].RowClass);
        Assert.All(rows[0].Flash, Assert.Null);
    }

    private static TradeVersion Trade(long id, int version, DateTime opened) => new(
        id, version, opened, opened, "C0001", "IBM", "Buy", 1000, 0, 0, "New", "XNAS");

    private static int IndexOf(Column column) => Column.All.ToList().IndexOf(column);
}

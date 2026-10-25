using Chalk.Sample.Blotter.Components;
using Chalk.Sample.Blotter.Grid;
using Chalk.Sample.Blotter.Domain;

namespace Chalk.Sample.Blotter.Tests;

/// <summary>What flashes: changed cells, and rows the streams have just sent; nothing else.</summary>
public sealed class FlashesTests
{
    private static readonly DateTime Now = new(2026, 10, 23, 9, 0, 0);
    private static readonly int Filled = IndexOf("filled_qty");
    private static readonly int Price = IndexOf("avg_price");
    private static readonly int Client = IndexOf("client");

    [Fact]
    public void The_first_page_flashes_nothing()
    {
        var rows = new Flashes(Tabs.Orders.Columns).Decorate([Make(1, version: 1, at: Now)], Now, Now);

        Assert.All(rows[0].Flash, Assert.Null);
        Assert.Null(rows[0].RowClass);
    }

    [Fact]
    public void A_newer_version_flashes_only_the_cells_that_changed()
    {
        var flashes = new Flashes(Tabs.Orders.Columns);
        var before = Make(1, version: 1, at: Now);
        flashes.Decorate([before], Now, Now);

        var rows = flashes.Decorate([before with { Version = 2, FilledQty = 0.5, AvgPrice = 101.25 }], Now, Now.AddMilliseconds(100));

        Assert.Equal("flash-a", rows[0].Flash[Filled]);
        Assert.Equal("flash-a", rows[0].Flash[Price]);
        Assert.Null(rows[0].Flash[Client]);
    }

    [Fact]
    public void A_cell_that_changes_again_while_flashing_starts_its_flash_over()
    {
        var flashes = new Flashes(Tabs.Orders.Columns);
        var v1 = Make(1, version: 1, at: Now);
        flashes.Decorate([v1], Now, Now);
        flashes.Decorate([v1 with { Version = 2, FilledQty = 0.5 }], Now, Now.AddMilliseconds(100));

        var rows = flashes.Decorate([v1 with { Version = 3, FilledQty = 0.9 }], Now, Now.AddMilliseconds(400));

        // The other name of the same animation, which the browser plays from the start.
        Assert.Equal("flash-b", rows[0].Flash[Filled]);
    }

    [Fact]
    public void A_render_in_between_keeps_the_flash_without_starting_it_again_and_it_ends()
    {
        var flashes = new Flashes(Tabs.Orders.Columns);
        var v1 = Make(1, version: 1, at: Now);
        flashes.Decorate([v1], Now, Now);
        var v2 = v1 with { Version = 2, FilledQty = 0.5 };
        flashes.Decorate([v2], Now, Now.AddMilliseconds(100));

        Assert.Equal("flash-a", flashes.Decorate([v2], Now, Now.AddMilliseconds(600))[0].Flash[Filled]);
        Assert.Null(flashes.Decorate([v2], Now, Now.AddSeconds(3))[0].Flash[Filled]);
    }

    [Fact]
    public void A_row_sent_after_the_last_page_was_read_flashes_whole_and_a_fill_counts_as_new()
    {
        var flashes = new Flashes(Tabs.Fills.Columns);
        flashes.Decorate([Fill(1, Now)], ingestedThrough: Now, Now);

        var rows = flashes.Decorate([Fill(2, Now.AddMilliseconds(50)), Fill(1, Now)], Now.AddMilliseconds(80), Now.AddMilliseconds(100));

        Assert.Equal("new-a", rows[0].RowClass);
        Assert.Null(rows[1].RowClass);
    }

    [Fact]
    public void A_row_scrolled_into_view_does_not_flash()
    {
        var flashes = new Flashes(Tabs.Orders.Columns);
        flashes.Decorate([Make(1, version: 1, at: Now)], ingestedThrough: Now, Now);

        // Older than what the last page covered, and changed long ago: it is only new to this page.
        var rows = flashes.Decorate([Make(7, version: 4, at: Now.AddHours(-2))], Now.AddMilliseconds(80), Now.AddMilliseconds(100));

        Assert.Null(rows[0].RowClass);
        Assert.All(rows[0].Flash, Assert.Null);
    }

    private static Order Make(long id, int version, DateTime at) => new(
        SortSpecTests.Id(id), version, at, at, $"CEDAR-{id}", "CEDAR", "Americas", "FIX", "BTC/USD Perp", "Buy", 1, 0, 0, 0, 6, "New");

    private static Fill Fill(long id, DateTime at) => new(
        SortSpecTests.Id(id), SortSpecTests.Id(9), at, "CEDAR-9", "CEDAR", "Americas", "FIX", "BTC/USD Perp", "Buy", 0.05, 65_000, 3_250, 64_990, 6);

    private static int IndexOf(string column) => Tabs.Orders.Columns.ToList().IndexOf(Tabs.Orders.Named(column));
}

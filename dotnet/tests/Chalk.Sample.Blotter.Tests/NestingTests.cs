using Chalk.Sample.Blotter.Components;
using Chalk.Sample.Blotter.Grid;
using Chalk.Sample.Blotter.Domain;

namespace Chalk.Sample.Blotter.Tests;

/// <summary>Fills nested under their orders: where each row of the grid comes from.</summary>
public sealed class NestingTests
{
    private static readonly Opened Second = Open(position: 1, fills: 2);
    private static readonly Opened Fifth = Open(position: 4, fills: 3);

    [Fact]
    public void With_nothing_opened_the_grid_is_the_tab()
    {
        Assert.Equal(10, Nesting.Total(10, []));
        Assert.Equal(new Slot(7, null, 0), Nesting.Locate(7, []));
    }

    [Fact]
    public void An_opened_orders_fills_follow_it_and_move_the_rows_below_down()
    {
        IReadOnlyList<Opened> opened = [Second, Fifth];

        var slots = Nesting.Map(0, 12, rows: 10, opened);

        Assert.Equal(15, Nesting.Total(10, opened));
        Assert.Equal(
            ["0", "1", "1.0", "1.1", "2", "3", "4", "4.0", "4.1", "4.2", "5", "6"],
            slots.Select(s => s.IsFill ? $"{s.Position}.{s.Fill}" : $"{s.Position}"));
    }

    [Fact]
    public void A_page_that_starts_among_an_orders_fills_starts_with_them()
    {
        var slots = Nesting.Map(3, 3, rows: 10, [Second, Fifth]);

        Assert.Equal(["1.1", "2", "3"], slots.Select(s => s.IsFill ? $"{s.Position}.{s.Fill}" : $"{s.Position}"));
    }

    [Fact]
    public void The_grid_ends_with_the_tab_and_its_last_orders_fills()
    {
        var last = Open(position: 9, fills: 2);

        var slots = Nesting.Map(9, 5, rows: 10, [last]);

        Assert.Equal(["9", "9.0", "9.1"], slots.Select(s => s.IsFill ? $"{s.Position}.{s.Fill}" : $"{s.Position}"));
    }

    [Fact]
    public void A_row_of_the_tab_is_found_in_the_grid_past_the_fills_above_it()
    {
        Assert.Equal(1, Nesting.GridPosition(1, [Second, Fifth]));
        Assert.Equal(4, Nesting.GridPosition(2, [Second, Fifth]));
        Assert.Equal(10, Nesting.GridPosition(5, [Second, Fifth]));
    }

    private static Opened Open(long position, int fills) => new(
        position,
        new Order(SortSpecTests.Id(position), 2, default, default, "", "", "", "", "", "Buy", 1, 1, 1, 1, 6, "Filled"),
        [.. Enumerable.Range(0, fills).Select(i => new Fill(SortSpecTests.Id(100 + i), SortSpecTests.Id(position), default, "", "", "", "", "", "Buy", 1, 1, 1, 1, 6))]);
}

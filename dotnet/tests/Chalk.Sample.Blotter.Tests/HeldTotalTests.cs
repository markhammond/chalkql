using Chalk.Sample.Blotter.Components;

namespace Chalk.Sample.Blotter.Tests;

/// <summary>
///     The total a held grid shows: kept through small drift, taken again past 1%, when the end is learned,
///     when rows are let in above, and afresh once the grid lets go.
/// </summary>
public sealed class HeldTotalTests
{
    [Fact]
    public void The_first_total_is_taken_as_it_is() => Assert.Equal(500_000, new HeldTotal().Hold(500_000));

    [Fact]
    public void Drift_of_1_percent_or_less_is_held()
    {
        var total = new HeldTotal();
        total.Hold(500_000);

        Assert.Equal(500_000, total.Hold(503_000));
        Assert.Equal(500_000, total.Hold(495_000));
        Assert.Equal(500_000, total.Hold(505_000));
    }

    [Fact]
    public void Drift_past_1_percent_is_taken_again()
    {
        var total = new HeldTotal();
        total.Hold(500_000);

        Assert.Equal(505_001, total.Hold(505_001));
        Assert.Equal(505_001, total.Hold(507_000));
    }

    [Fact]
    public void A_learned_end_is_the_total_from_then_on()
    {
        var total = new HeldTotal();
        total.Hold(500_000);
        total.Set(499_200);

        Assert.Equal(499_200, total.Hold(500_000));
    }

    [Fact]
    public void Rows_let_in_above_grow_it_by_as_many()
    {
        var total = new HeldTotal();
        total.Hold(500_000);
        total.Shift(30);

        Assert.Equal(500_030, total.Hold(500_030));
        Assert.Equal(500_030, total.Hold(500_010));
    }

    [Fact]
    public void Letting_go_takes_the_next_total_afresh()
    {
        var total = new HeldTotal();
        total.Hold(500_000);
        total.Clear();

        Assert.Equal(500_400, total.Hold(500_400));
    }

    [Fact]
    public void An_empty_grid_takes_any_rows()
    {
        var total = new HeldTotal();
        total.Hold(0);

        Assert.Equal(3, total.Hold(3));
    }
}

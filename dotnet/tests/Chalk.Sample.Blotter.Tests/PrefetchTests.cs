using Chalk.Sample.Blotter.Components;
using Microsoft.Extensions.Time.Testing;

namespace Chalk.Sample.Blotter.Tests;

/// <summary>
///     The rows the grid renders beyond the view, from how long its pages take to be drawn: starting small,
///     up on a fast link, down on a slow one, still between the two, never on one page alone, never on the
///     page a step brings, and a size left for being slow held back for longer each time.
/// </summary>
public sealed class PrefetchTests
{
    private static readonly TimeSpan SlowPage = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FastPage = TimeSpan.FromMilliseconds(80);
    private static readonly TimeSpan MiddlingPage = TimeSpan.FromMilliseconds(300);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 25, 9, 0, 0, TimeSpan.Zero));

    [Fact]
    public void A_grid_starts_at_five_rows_each_side() => Assert.Equal(5, new Prefetch(_time).Rows);

    [Fact]
    public void Fast_pages_step_it_up_three_pages_after_each_step_settles()
    {
        var prefetch = new Prefetch(_time);
        Assert.Equal([5, 5, 10, 10, 10, 10, 20, 20, 20, 20], Pages(prefetch, FastPage, 10));
    }

    [Fact]
    public void Slow_pages_step_it_back_down()
    {
        var prefetch = new Prefetch(_time);
        Pages(prefetch, FastPage, 7);
        Assert.Equal(20, prefetch.Rows);

        Assert.Equal([20, 20, 20, 10, 10, 10, 10, 5, 5, 5], Pages(prefetch, SlowPage, 10));
    }

    [Fact]
    public void Between_fast_and_slow_it_stays()
    {
        var prefetch = new Prefetch(_time);
        Pages(prefetch, FastPage, 3);
        Assert.Equal(10, prefetch.Rows);

        Assert.All(Pages(prefetch, MiddlingPage, 20), rows => Assert.Equal(10, rows));
    }

    [Fact]
    public void One_fast_page_among_slower_ones_moves_nothing()
    {
        var prefetch = new Prefetch(_time);
        foreach (var took in new[] { MiddlingPage, FastPage, MiddlingPage, MiddlingPage, FastPage, MiddlingPage })
        {
            Assert.False(prefetch.Observe(took, out _));
        }

        Assert.Equal(5, prefetch.Rows);
    }

    [Fact]
    public void The_page_a_step_brings_is_not_judged()
    {
        var prefetch = new Prefetch(_time);
        Pages(prefetch, FastPage, 3);
        Assert.Equal(10, prefetch.Rows);

        // The page the step brings is slow; the three after it are fast, and they decide.
        prefetch.Observe(SlowPage, out _);
        Assert.Equal([10, 10, 20], Pages(prefetch, FastPage, 3));
    }

    [Fact]
    public void A_size_left_for_being_slow_is_held_back_for_longer_each_time()
    {
        var prefetch = new Prefetch(_time);
        Pages(prefetch, FastPage, 3);
        Assert.Equal(10, prefetch.Rows);

        // Too slow at 10: back to 5, and 10 is held back for 30 s.
        Pages(prefetch, SlowPage, 4);
        Assert.Equal(5, prefetch.Rows);
        Assert.All(Pages(prefetch, FastPage, 10), rows => Assert.Equal(5, rows));

        _time.Advance(TimeSpan.FromSeconds(30));
        Pages(prefetch, FastPage, 3);
        Assert.Equal(10, prefetch.Rows);

        // Left again: held back for 60 s this time.
        Pages(prefetch, SlowPage, 4);
        Assert.Equal(5, prefetch.Rows);
        _time.Advance(TimeSpan.FromSeconds(31));
        Assert.All(Pages(prefetch, FastPage, 10), rows => Assert.Equal(5, rows));

        _time.Advance(TimeSpan.FromSeconds(30));
        Pages(prefetch, FastPage, 3);
        Assert.Equal(10, prefetch.Rows);
    }

    [Fact]
    public void Sizes_are_counted_in_views_of_the_rows_the_page_shows()
    {
        var prefetch = new Prefetch(_time);
        Assert.True(prefetch.See(32));
        Assert.Equal(8, prefetch.Rows);
        Assert.False(prefetch.See(32));

        Pages(prefetch, FastPage, 3);
        Assert.Equal(16, prefetch.Rows);
    }

    [Fact]
    public void A_step_says_what_moved_it()
    {
        var prefetch = new Prefetch(_time);
        prefetch.Observe(TimeSpan.FromMilliseconds(100), out _);
        prefetch.Observe(TimeSpan.FromMilliseconds(40), out _);
        Assert.True(prefetch.Observe(TimeSpan.FromMilliseconds(70), out var judged));

        Assert.Equal(TimeSpan.FromMilliseconds(70), judged);
    }

    private static int[] Pages(Prefetch prefetch, TimeSpan took, int count) =>
        [.. Enumerable.Range(0, count).Select(page =>
        {
            prefetch.Observe(took, out _);
            return prefetch.Rows;
        })];
}

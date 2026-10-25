using Chalk.Sample.Blotter.Components;
using Microsoft.Extensions.Time.Testing;

namespace Chalk.Sample.Blotter.Tests;

/// <summary>
///     How far the page is behind the host, on a clock turned by hand: from the oldest change a page shows
///     to the browser drawing it, the worst since the last look, and a page stuck on its way growing.
/// </summary>
public sealed class PageLagTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 25, 9, 0, 0, TimeSpan.Zero));

    [Fact]
    public void A_page_is_as_far_behind_as_the_oldest_change_it_shows()
    {
        var lag = new PageLag(_time);
        lag.Changed();
        Wait(0.3);
        lag.Changed();
        Wait(0.1);
        lag.Reading();
        Wait(0.5);
        lag.Drawn();

        Assert.Equal(TimeSpan.FromSeconds(0.9), lag.Take());
    }

    [Fact]
    public void Each_look_shows_the_worst_since_the_last_and_then_starts_afresh()
    {
        var lag = new PageLag(_time);
        Page(waits: 2.0, drawnAfter: 0.5);
        Page(waits: 0.1, drawnAfter: 0.1);

        Assert.Equal(TimeSpan.FromSeconds(2.5), lag.Take());
        Assert.Equal(TimeSpan.Zero, lag.Take());

        void Page(double waits, double drawnAfter)
        {
            lag.Changed();
            Wait(waits);
            lag.Reading();
            Wait(drawnAfter);
            lag.Drawn();
        }
    }

    [Fact]
    public void A_page_stuck_on_its_way_shows_its_lag_growing()
    {
        var lag = new PageLag(_time);
        lag.Changed();
        lag.Reading();
        Wait(1);
        Assert.Equal(TimeSpan.FromSeconds(1), lag.Take());

        Wait(4);
        Assert.Equal(TimeSpan.FromSeconds(5), lag.Take());
    }

    [Fact]
    public void Changes_no_page_has_read_yet_count_too()
    {
        var lag = new PageLag(_time);
        lag.Changed();
        Wait(3);

        Assert.Equal(TimeSpan.FromSeconds(3), lag.Take());
    }

    [Fact]
    public void A_change_after_a_read_began_waits_for_the_next_page()
    {
        var lag = new PageLag(_time);
        lag.Changed();
        lag.Reading();
        Wait(0.2);
        lag.Changed();
        Wait(0.2);
        lag.Drawn();
        Assert.Equal(TimeSpan.FromSeconds(0.4), lag.Take());

        // The second change is in no page yet: it has waited 0.2 s, and goes on waiting.
        Wait(0.3);
        Assert.Equal(TimeSpan.FromSeconds(0.5), lag.Take());
    }

    [Fact]
    public void Nothing_changed_is_no_lag()
    {
        var lag = new PageLag(_time);
        Wait(10);
        lag.Reading();
        lag.Drawn();

        Assert.Equal(TimeSpan.Zero, lag.Take());
    }

    private void Wait(double seconds) => _time.Advance(TimeSpan.FromSeconds(seconds));
}

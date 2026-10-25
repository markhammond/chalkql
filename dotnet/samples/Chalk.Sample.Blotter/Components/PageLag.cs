namespace Chalk.Sample.Blotter.Components;

/// <summary>
///     How far the page is behind the host: from a change reaching the host to the browser drawing a page
///     that shows it. One per circuit: the grid says when the host changed, when a page is read and when
///     the browser has drawn it, and the status bar shows the lag.
/// </summary>
/// <remarks>
///     Shown as the worst since the status bar last looked: the longest any change took to be drawn, or,
///     if longer, how long the oldest change not yet drawn has waited, so a page that is stuck on its way
///     shows as one, its lag growing, rather than as the last page that arrived.
/// </remarks>
public sealed class PageLag(TimeProvider time)
{
    private readonly Lock _gate = new();

    // The oldest change no page has read yet; the oldest change in the page on its way to the browser.
    private long? _waiting;
    private long? _reading;
    private TimeSpan _worst;

    /// <summary>The host has changed: a page read from now on shows it.</summary>
    public void Changed()
    {
        lock (_gate)
        {
            _waiting ??= time.GetTimestamp();
        }
    }

    /// <summary>A page is being read: it shows every change so far.</summary>
    public void Reading()
    {
        lock (_gate)
        {
            _reading ??= _waiting;
            _waiting = null;
        }
    }

    /// <summary>The browser has drawn the page read last.</summary>
    public void Drawn()
    {
        lock (_gate)
        {
            if (_reading is { } oldest)
            {
                _worst = Max(_worst, time.GetElapsedTime(oldest));
            }

            _reading = null;
        }
    }

    /// <summary>The lag to show, the worst since the last look; the next look starts afresh.</summary>
    public TimeSpan Take()
    {
        lock (_gate)
        {
            var lag = (_reading ?? _waiting) is { } oldest ? Max(_worst, time.GetElapsedTime(oldest)) : _worst;
            _worst = TimeSpan.Zero;
            return lag;
        }
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}

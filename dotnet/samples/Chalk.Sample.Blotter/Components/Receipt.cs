using System.Diagnostics;
using Microsoft.AspNetCore.Components;

namespace Chalk.Sample.Blotter.Components;

/// <summary>
///     Says when the browser has drawn what was rendered before it was asked, and how long that took: a
///     component that renders nothing, rendered again when asked, whose after-render runs only once the
///     browser has acknowledged the batch it went out in. The browser acknowledges batches in order, by
///     their number, so everything rendered before went with or ahead of it.
/// </summary>
/// <remarks>
///     A circuit sends what it renders whether or not the link can carry it, and waits only once ten
///     batches are unacknowledged. A grid that renders as fast as the feed changes then keeps a queue of
///     old pages on a slow link, and a click's answer waits behind them. Asked before each next render,
///     this keeps one page on its way at a time: what changes meanwhile waits in the scope's state, and
///     the next page shows all of it.
///     <para>
///         The time runs from this receipt being asked to the acknowledgement of its own batch, on the
///         server's clock: the batch rendered just before it, its trip, the browser drawing it, and the
///         acknowledgement's trip back, and whatever was on its way ahead of them. Asked again before the
///         browser has answered, it hands back the same wait, timed from the first asking.
///     </para>
/// </remarks>
public sealed class Receipt : ComponentBase, IDisposable
{
    private (TaskCompletionSource<TimeSpan> Drawn, long Asked)? _waiting;

    /// <summary>Completes once the browser has drawn everything rendered so far, with how long that took; cancelled if the page goes.</summary>
    public Task<TimeSpan> DrawnAsync()
    {
        if (_waiting is { } waiting)
        {
            return waiting.Drawn.Task;
        }

        var drawn = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiting = (drawn, Stopwatch.GetTimestamp());
        StateHasChanged();
        return drawn.Task;
    }

    protected override void OnAfterRender(bool firstRender)
    {
        if (_waiting is { } waiting)
        {
            _waiting = null;
            waiting.Drawn.TrySetResult(Stopwatch.GetElapsedTime(waiting.Asked));
        }
    }

    public void Dispose() => _waiting?.Drawn.TrySetCanceled();
}

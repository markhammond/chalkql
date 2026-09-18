namespace Chalk.TestKit;

/// <summary>
/// A <see cref="TimeProvider"/> whose timers fire when a test says so, and whose clock is the real
/// one.
/// </summary>
/// <remarks>
/// <para>
/// This exists so a cadence can be tested without waiting for it. The working rules forbid timing
/// assertions, and a test that sets a one-second interval and sleeps for two is exactly that — it
/// passes or fails depending on how loaded the machine is. Firing the timer by hand asserts the
/// thing actually worth asserting: that the callback does what it should when it runs.
/// </para>
/// <para>
/// The clock is deliberately not faked. Nothing here needs a controlled <c>GetUtcNow</c>, and a
/// frozen clock would quietly change what <c>CURRENT_TIMESTAMP</c> returns in the same engine.
/// </para>
/// </remarks>
public sealed class ManualTimerProvider : TimeProvider
{
    private readonly List<ManualTimer> _timers = [];

    /// <summary>How many timers have been created and not yet disposed.</summary>
    public int LiveTimers
    {
        get
        {
            lock (_timers)
            {
                return _timers.Count;
            }
        }
    }

    /// <summary>Runs every live timer's callback once, on this thread.</summary>
    public void Fire()
    {
        ManualTimer[] due;
        lock (_timers)
        {
            due = [.. _timers];
        }

        foreach (var timer in due)
        {
            timer.Fire();
        }
    }

    public override ITimer CreateTimer(
        TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (_timers)
        {
            _timers.Add(timer);
        }

        return timer;
    }

    private void Forget(ManualTimer timer)
    {
        lock (_timers)
        {
            _timers.Remove(timer);
        }
    }

    private sealed class ManualTimer(ManualTimerProvider owner, TimerCallback callback, object? state)
        : ITimer
    {
        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose() => owner.Forget(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

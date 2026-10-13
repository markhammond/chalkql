namespace Chalk.Client;

/// <summary>
/// The clock of an engine that names one, as every execution on it reads it (D341,
/// <c>docs/design/66-view-checks-and-tuning.md</c> §2). One long, so that a read never tears; moved by a
/// refresh in the same hold of the gate as its commits.
/// </summary>
internal sealed class EngineClock : Chalk.Execution.IEngineClock
{
    /// <summary>The clock's UTC ticks, or zero while no refresh has moved it.</summary>
    private long _utcTicks;

    public DateTimeOffset? Committed
    {
        get
        {
            var ticks = Volatile.Read(ref _utcTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    /// <summary>Moves the clock. The caller holds the gate, with the refresh's commits.</summary>
    public void Set(DateTimeOffset clock) => Volatile.Write(ref _utcTicks, clock.UtcTicks);
}

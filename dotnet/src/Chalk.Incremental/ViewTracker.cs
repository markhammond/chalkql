namespace Chalk.Incremental;

/// <summary>One update of a view, as a cost tracker hears of it (design 70 §1).</summary>
public readonly struct ViewUpdate
{
    internal ViewUpdate(long version, TimeSpan elapsed, long allocatedBytes, bool whole, long changes, ulong planDigest)
    {
        Version = version;
        Elapsed = elapsed;
        AllocatedBytes = allocatedBytes;
        Whole = whole;
        Changes = changes;
        PlanDigest = planDigest;
    }

    /// <summary>The version of the epoch the update brought the view to.</summary>
    public long Version { get; }

    /// <summary>How long the update took, from its start to the view holding its rows.</summary>
    public TimeSpan Elapsed { get; }

    /// <summary>
    /// The bytes allocated in the process while the update ran: the update's, and any other thread's
    /// meanwhile. Counted for the process because an update that awaits may go on on another thread.
    /// </summary>
    public long AllocatedBytes { get; }

    /// <summary>
    /// Whether the update computed the view's rows from nothing: every update of a recomputing view,
    /// and an incremental view's first, its first on a new plan, and its first after it fell behind.
    /// </summary>
    public bool Whole { get; }

    /// <summary>The keys the update changed: rows put that differ from the view's, and keys deleted.</summary>
    public long Changes { get; }

    /// <summary>The digest of the plan the update ran.</summary>
    public ulong PlanDigest { get; }
}

/// <summary>
/// What a host keeps of a view's updates (design 70 §1), bound as
/// <see cref="ViewOptions{TKey, TRow}.Tracker"/>. It is told of each update that moves the view, on
/// the update's own thread, and may be read on another: by the host's
/// <see cref="ViewOptions{TKey, TRow}.RefreshQueryPlan"/> on a background thread, say. So it keeps its
/// figures where both may read them: volatile or interlocked fields.
/// </summary>
public interface IViewCostTracker
{
    /// <summary>
    /// Told of an update the view has just taken in. It must be cheap, since the update waits for it;
    /// what it throws comes out of <c>UpdateAsync</c>, after the update has been taken in.
    /// </summary>
    void Updated(in ViewUpdate update);
}

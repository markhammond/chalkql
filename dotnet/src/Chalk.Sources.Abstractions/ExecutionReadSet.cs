namespace Chalk.Sources;

/// <summary>
/// A source that can hold a table's state for one execution (F188): pinned when the execution
/// begins, under <see cref="ExecutionReadSet.Gate"/>, so that every table one execution reads comes
/// from the same refresh — a refresh transaction is then seen whole or not at all, whatever order
/// its scans start in.
/// </summary>
internal interface IPinnableSource
{
    /// <summary>
    /// The state <paramref name="table"/> serves now, held until <see cref="Unpin"/>; null for a table
    /// this source does not hold so.
    /// </summary>
    object? Pin(string table);

    /// <summary>Lets go of a state <see cref="Pin"/> returned.</summary>
    void Unpin(string table, object pinned);
}

/// <summary>
/// The states one execution reads (F188): one per table the plan reads from a source that pins, taken
/// together when the execution begins and let go when it ends. A scan reaches it through its arena, and
/// reads the pinned state in place of the one its table serves when the scan starts.
/// </summary>
/// <remarks>
/// One per operator tree, reused by every execution the tree runs, so pinning allocates nothing once a
/// tree is warm.
/// </remarks>
internal sealed class ExecutionReadSet
{
    /// <summary>
    /// What every execution's pins and every refresh transaction's commits take in turn, across the
    /// process: an execution pins its tables before a transaction's first commit or after its last,
    /// never between two. One gate for the process rather than one per engine, because a source is
    /// shared between engines by default and one engine's commits are another's tables. Held for
    /// reference swaps and counts only; never across an await, and never while host code runs.
    /// </summary>
    public static readonly Lock Gate = new();

    private readonly (string SourceId, string Table, IPinnableSource Source)[] _tables;
    private readonly object?[] _pinned;

    public ExecutionReadSet((string SourceId, string Table, IPinnableSource Source)[] tables)
    {
        _tables = tables;
        _pinned = new object?[tables.Length];
    }

    /// <summary>Pins every table, under <see cref="Gate"/>.</summary>
    public void Pin()
    {
        lock (Gate)
        {
            PinHeld();
        }
    }

    /// <summary>Pins every table. The caller holds <see cref="Gate"/>, to read something else in the same hold.</summary>
    public void PinHeld()
    {
        for (var i = 0; i < _tables.Length; i++)
        {
            _pinned[i] = _tables[i].Source.Pin(_tables[i].Table);
        }
    }

    /// <summary>Lets every pinned table go.</summary>
    public void Unpin()
    {
        for (var i = 0; i < _tables.Length; i++)
        {
            if (_pinned[i] is { } pinned)
            {
                _pinned[i] = null;
                _tables[i].Source.Unpin(_tables[i].Table, pinned);
            }
        }
    }

    /// <summary>The state this execution pinned for a table, or null when it pinned none.</summary>
    public object? Pinned(string sourceId, string table)
    {
        for (var i = 0; i < _tables.Length; i++)
        {
            if (string.Equals(_tables[i].SourceId, sourceId, StringComparison.Ordinal)
                && string.Equals(_tables[i].Table, table, StringComparison.OrdinalIgnoreCase))
            {
                return _pinned[i];
            }
        }

        return null;
    }
}

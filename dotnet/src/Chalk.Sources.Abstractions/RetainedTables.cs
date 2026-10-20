namespace Chalk.Sources;

/// <summary>
/// The states of a view's tables as one refresh committed them, held past it for a view that must
/// publish that refresh itself (design 78 §4, §5): a barrier, or every refresh for a view that publishes
/// every version. Pinned under <see cref="ExecutionReadSet.Gate"/> just after the refresh's commits, with
/// the count of commits and the clock of the same hold, so an execution that reads them later reads that
/// refresh whole.
/// </summary>
internal sealed class RetainedTables
{
    private readonly (string SourceId, string Table, ITrackedSource Source, object? State)[] _held;
    private int _released;

    /// <summary>Pins each of <paramref name="tables"/> as it stands. The caller holds the gate.</summary>
    public RetainedTables(long commit, DateTimeOffset? clock, (string SourceId, string Table, ITrackedSource Source)[] tables)
    {
        Commit = commit;
        Clock = clock;
        _held = new (string, string, ITrackedSource, object?)[tables.Length];
        for (var i = 0; i < tables.Length; i++)
        {
            var (sourceId, table, source) = tables[i];
            _held[i] = (sourceId, table, source, source.Pin(table));
        }
    }

    /// <summary>How many refreshes the engine had committed, this one the last.</summary>
    public long Commit { get; }

    /// <summary>The engine's clock as the refresh left it, or null on an engine without one.</summary>
    public DateTimeOffset? Clock { get; }

    /// <summary>
    /// Another reference to the held state of <paramref name="table"/>, for one execution to read and let go
    /// of; null where none is held, as for a table the statement came to read only once prepared anew.
    /// </summary>
    public object? Retain(string sourceId, string table)
    {
        foreach (var (heldSource, heldTable, source, state) in _held)
        {
            if (state is not null
                && string.Equals(heldSource, sourceId, StringComparison.Ordinal)
                && string.Equals(heldTable, table, StringComparison.OrdinalIgnoreCase))
            {
                return source.Retain(table, state);
            }
        }

        return null;
    }

    /// <summary>Lets every held state go, once. Not under the gate: a source may tell its host.</summary>
    public void Release()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0)
        {
            return;
        }

        foreach (var (_, table, source, state) in _held)
        {
            if (state is not null)
            {
                source.Unpin(table, state);
            }
        }
    }
}

namespace Chalk.Sources.Poco;

/// <summary>
/// A table's replacement, built before the refresh that commits it (design 69 §3): a snapshot of the
/// host's rewrite of the rows before <see cref="Through"/>, and the table's rewrite count when it was
/// prepared, by which the commit tells whether the table has been rewritten since.
/// </summary>
/// <remarks>
/// It is committed once. The commit writes the rows appended since the cut into the room after the
/// rewrite, so a second commit would write over rows the first one published; that, and a commit after
/// the host has let it go, is refused.
/// </remarks>
internal sealed class PocoPreparedReplacement<T> : IPreparedReplacement
{
    private const int Ready = 0;
    private const int Done = 1;
    private const int Dropped = 2;

    private int _state;
    private PocoTableSnapshot<T>? _snapshot;

    public PocoPreparedReplacement(
        PocoTableRuntime<T> owner, PocoTableSnapshot<T> snapshot, int through, long rewrites, long? frontier)
    {
        Owner = owner;
        _snapshot = snapshot;
        Through = through;
        RowCount = snapshot.RowCount;
        Rewrites = rewrites;
        Frontier = frontier;
    }

    /// <summary>The table it was prepared for.</summary>
    public PocoTableRuntime<T> Owner { get; }

    /// <inheritdoc />
    public string Table => Owner.Name;

    /// <inheritdoc />
    public Type RowType => typeof(T);

    /// <inheritdoc />
    public int Through { get; }

    /// <inheritdoc />
    public int RowCount { get; }

    /// <summary>The table's rewrite count when this was prepared: its snapshot's, at the cut.</summary>
    public long Rewrites { get; }

    /// <summary>The latest time among the rewrite's rows, for a table that declares a time.</summary>
    public long? Frontier { get; }

    /// <inheritdoc />
    public bool Pending => Volatile.Read(ref _state) == Ready;

    /// <summary>
    /// The rewrite's snapshot, refusing a replacement already committed or let go.
    /// </summary>
    public PocoTableSnapshot<T> Snapshot(string sourceId)
    {
        var snapshot = Volatile.Read(ref _snapshot);
        if (snapshot is null || Volatile.Read(ref _state) != Ready)
        {
            throw new SourceContractException(
                ChalkErrorCodes.SourceRefresh,
                sourceId,
                Table,
                Volatile.Read(ref _state) == Done
                    ? "the prepared replacement has already been committed. Each one is committed once; "
                      + "prepare another for the next."
                    : "the prepared replacement was disposed before a refresh committed it.");
        }

        return snapshot;
    }

    /// <summary>
    /// Marks this committed and lets go of the rewrite's snapshot, which now belongs to the table or
    /// to the snapshot that extended it. Called by the commit, which may not fail.
    /// </summary>
    public void Committed()
    {
        Volatile.Write(ref _state, Done);
        Volatile.Write(ref _snapshot, null);
    }

    /// <summary>Lets the replacement go, when no refresh has committed it.</summary>
    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _state, Dropped, Ready) == Ready)
        {
            Volatile.Write(ref _snapshot, null);
        }
    }
}

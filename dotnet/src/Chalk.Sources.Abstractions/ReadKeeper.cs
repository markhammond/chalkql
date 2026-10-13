using Apache.Arrow;

namespace Chalk.Sources;

/// <summary>
/// What a standing view keeps of the reads its statement makes of sources outside the engine's epochs,
/// a database's tables (D340): the batches each read returned, replayed at the view's later updates
/// until a refresh names one of the read's tables. An execution that is not a view's has none, and pays
/// one null test for each such read.
/// </summary>
/// <remarks>
/// Here, beside the execution's arena and read set, because both the executor, which calls it, and
/// the views, which keep, can see this layer; a view does not reach the executor itself.
/// </remarks>
internal interface IReadKeeper
{
    /// <summary>
    /// The batches the read at <paramref name="site"/> returned when it last ran bound with
    /// <paramref name="values"/>, if no refresh has named its tables since; otherwise null, and the
    /// read runs. The batches stay the keeper's: the caller only views them.
    /// </summary>
    IReadOnlyList<RecordBatch>? Replay(ReadSite site, object?[] values);

    /// <summary>Keeps a copy of a batch the read at <paramref name="site"/> returned, in order.</summary>
    void Keep(ReadSite site, object?[] values, RecordBatch batch);

    /// <summary>
    /// The read at <paramref name="site"/> ran to its end, so what was kept of it is the whole read.
    /// A read abandoned part-way never gets here, and is not replayed.
    /// </summary>
    void Kept(ReadSite site, object?[] values);
}

/// <summary>One read of a source outside epochs, at one place in one plan, and the tables it reads.</summary>
internal sealed class ReadSite
{
    public ReadSite(string path, string sourceId, IReadOnlyList<(string SourceId, string Table)> tables)
    {
        Path = path;
        SourceId = sourceId;
        Tables = tables;
    }

    /// <summary>The read's place in its plan, which names it among the plan's reads.</summary>
    public string Path { get; }

    /// <summary>The source it reads.</summary>
    public string SourceId { get; }

    /// <summary>Every table it reads, by source and name: what a refresh would have to name.</summary>
    public IReadOnlyList<(string SourceId, string Table)> Tables { get; }
}

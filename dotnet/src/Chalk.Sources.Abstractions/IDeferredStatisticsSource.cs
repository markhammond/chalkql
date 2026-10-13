using Chalk.Catalog;

namespace Chalk.Sources;

/// <summary>
/// A source whose tables can compute, in the background, the statistics a refresh deferred, and take
/// them in at a later refresh (design 69 §4).
/// </summary>
internal interface IDeferredStatisticsSource
{
    /// <summary>
    /// For each table whose statistics a refresh deferred, statistics computed now over the rows it
    /// serves, and the table described with them. Runs beside refreshes and executions, and takes no
    /// refresh lock.
    /// </summary>
    IReadOnlyList<DeferredStatistics> ComputeDeferredStatistics(CancellationToken ct);

    /// <summary>
    /// Takes statistics computed in the background as each table's own, where the table still
    /// carries deferred ones; a table a refresh has since given statistics of its own keeps those.
    /// Called under the engine's refresh lock.
    /// </summary>
    void AdmitStatistics(IReadOnlyList<DeferredStatistics> statistics);
}

/// <summary>One table's statistics, computed in the background (design 69 §4).</summary>
internal sealed class DeferredStatistics
{
    /// <summary>The table, as it is named in the source's schema.</summary>
    public required string Table { get; init; }

    /// <summary>The table described with the statistics computed: what the planner is sent.</summary>
    public required TableDescriptor Descriptor { get; init; }

    /// <summary>The statistics in the source's own form, handed back to it at admission.</summary>
    public required object Statistics { get; init; }

    /// <summary>The rows they were computed over.</summary>
    public required int RowCount { get; init; }
}

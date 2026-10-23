using Chalk.Incremental;
using Chalk.Sample.Blotter.Market;

namespace Chalk.Sample.Blotter.Live;

/// <summary>The blotter host's settings: the database, how big the corpus is, and how much memory the host may use.</summary>
public sealed record BlotterOptions
{
    /// <summary>Client orders in the corpus the sample generates at start.</summary>
    public long Orders { get; init; } = 5_000_000;

    /// <summary>
    /// A PostgreSQL connection string to use, rather than a server of the sample's own. The sample
    /// makes a schema named <c>blotter</c> there, and refuses if one exists already.
    /// </summary>
    public string? Postgres { get; init; }

    /// <summary>The <c>bin</c> directory of a PostgreSQL installation to start a server from; found if not given.</summary>
    public string? PostgresBinaries { get; init; }

    /// <summary>How <c>orders_current</c> keeps up with <c>order_versions</c>.</summary>
    public CurrentSync Current { get; init; } = CurrentSync.Trigger;

    /// <summary>The longest any statement may run, in the database or in ChalkQL, before it is stopped.</summary>
    public TimeSpan QueryTimeLimit { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The host's budget for what ChalkQL and the blotter keep in memory: the tail, the live scopes and
    /// what pages cache. The database's own memory is not in it.
    /// </summary>
    public long BudgetBytes { get; init; } = 64L << 20;

    /// <summary>How busy the simulated market is, and how far behind its database runs.</summary>
    public ExchangeOptions Exchange { get; init; } = new();

    /// <summary>
    /// How the live scopes keep current. Incremental (experimental in ChalkQL) costs what changed per
    /// update; recompute costs the whole tail per update but holds about a quarter of the memory.
    /// </summary>
    public ViewMaintenance Maintenance { get; init; } = ViewMaintenance.Incremental;

    /// <summary>How often the tail's rows are looked up in the database, to see which it now holds.</summary>
    public TimeSpan ConfirmEvery { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// How long a row stays in the tail after the database was first seen to hold it: longer than a
    /// page read takes, so that no page merges a read from before the database had it with a tail
    /// that no longer does.
    /// </summary>
    public TimeSpan EvictAfter { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How far back the gap fill reaches when the host starts serving: the versions written since
    /// then are taken as if the stream had sent them, in case the stream sent one before the host
    /// subscribed that <c>orders_current</c> does not show yet.
    /// </summary>
    public TimeSpan GapFill { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long a scope no page uses stays open, in case a page comes back to it.</summary>
    public TimeSpan ScopeGrace { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Serve again once <c>orders_current</c> is no more than this far behind the stream.</summary>
    public TimeSpan ResumeWithin { get; init; } = TimeSpan.FromSeconds(5);
}

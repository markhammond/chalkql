using Chalk.Incremental;
using Chalk.Sample.Blotter.Fixtures;
using Chalk.Sample.Blotter.Domain;

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

    /// <summary>
    /// Opens a different database to read, in place of PostgreSQL (<see cref="Postgres"/> and
    /// <see cref="PostgresBinaries"/> are then unused); the host disposes of it when it stops.
    /// </summary>
    public Func<CancellationToken, Task<IBlotterProvider>>? Provider { get; init; }

    /// <summary>How <c>orders_current</c> keeps up with <c>order_versions</c>.</summary>
    public CurrentSync Current { get; init; } = CurrentSync.Trigger;

    /// <summary>The longest any statement may run, in the database or in ChalkQL, before it is stopped.</summary>
    public TimeSpan QueryTimeLimit { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The host's budget for what ChalkQL and the blotter keep in memory: the tail, the live scopes and
    /// what pages cache. The database's own memory is not in it. Where to start from, while
    /// <see cref="FindBudget"/> is on.
    /// </summary>
    public long BudgetBytes { get; init; } = 8L << 20;

    /// <summary>
    /// Whether the host finds the smallest budget it is not refused at: whatever would be refused for
    /// want of budget raises it to the next of <see cref="BudgetSteps"/> instead, up to the last, until
    /// a budget is chosen on the page. Off when the budget is given.
    /// </summary>
    public bool FindBudget { get; init; } = true;

    /// <summary>The budgets <see cref="FindBudget"/> tries, smallest first.</summary>
    public IReadOnlyList<long> BudgetSteps { get; init; } = [8L << 20, 16L << 20, 32L << 20, 64L << 20];

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
    /// How long a row stays in the tail after the database was first seen to hold it: longer than any
    /// window of the database's rows a page is answered from is kept (a second longer, see
    /// <see cref="Pages.WindowFor"/>), so that no page merges a read from before the database had the
    /// row with a tail that no longer does. Longer keeps windows longer, and the tail larger.
    /// </summary>
    public TimeSpan EvictAfter { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How far back the gap fill reaches when the host starts serving: the versions written since
    /// then are taken as if the stream had sent them, in case the stream sent one before the host
    /// subscribed that <c>orders_current</c> does not show yet.
    /// </summary>
    public TimeSpan GapFill { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Of the budget, the most the windows of rows kept for pages may hold; they give way to the tail and the scopes.</summary>
    public double PageShare { get; init; } = 0.25;

    /// <summary>How long a scope no page uses stays open, in case a page comes back to it.</summary>
    public TimeSpan ScopeGrace { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Serve again once <c>orders_current</c> is no more than this far behind the stream.</summary>
    public TimeSpan ResumeWithin { get; init; } = TimeSpan.FromSeconds(5);
}

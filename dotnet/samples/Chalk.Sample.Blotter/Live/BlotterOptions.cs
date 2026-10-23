using Chalk.Incremental;
using Chalk.Sample.Blotter.Market;

namespace Chalk.Sample.Blotter.Live;

/// <summary>The blotter host's settings: how big the corpus is, and how much memory the host may use.</summary>
public sealed record BlotterOptions
{
    /// <summary>Trades in the corpus the sample generates at start.</summary>
    public long Trades { get; init; } = 5_000_000;

    /// <summary>
    /// The host's budget for what ChalkQL and the blotter keep in memory: the tail, the live scopes
    /// and the cache of snapshot pages. The database's own memory is not in it.
    /// </summary>
    public long BudgetBytes { get; init; } = 64L << 20;

    /// <summary>How busy the simulated market is, and how far behind its database runs.</summary>
    public ExchangeOptions Exchange { get; init; } = new();

    /// <summary>DuckDB's own limit, standing in for a database on another machine.</summary>
    public long DatabaseBytes { get; init; } = 8L << 30;

    /// <summary>
    /// How the live scopes keep current. Incremental (experimental in ChalkQL) costs what changed per
    /// update; recompute costs the whole tail per update but holds about a quarter of the memory.
    /// </summary>
    public ViewMaintenance Maintenance { get; init; } = ViewMaintenance.Incremental;

    /// <summary>Of the budget, the most the cache of snapshot pages may hold; it gives way to the tail and the scopes.</summary>
    public double PageShare { get; init; } = 0.25;

    /// <summary>Take a new generation once the tail and the scopes hold this share of the budget…</summary>
    public double TurnoverAt { get; init; } = 0.4;

    /// <summary>…or once the current one is this old, whichever comes first.</summary>
    public TimeSpan TurnoverEvery { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long a scope no page uses stays open, in case a page comes back to it.</summary>
    public TimeSpan ScopeGrace { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Serve again once the database is no more than this far behind.</summary>
    public TimeSpan ResumeWithin { get; init; } = TimeSpan.FromSeconds(5);
}

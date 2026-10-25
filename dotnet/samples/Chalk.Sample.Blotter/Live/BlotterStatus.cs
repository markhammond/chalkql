namespace Chalk.Sample.Blotter.Live;

/// <summary>Whether the host is serving pages, and if not, why.</summary>
public enum BlotterState
{
    Starting,
    Live,

    /// <summary>
    ///     What the host would have to hold to serve correctly does not fit the budget: the tail that
    ///     covers the database's lag, and the scopes over it. Nothing is served until it would fit.
    /// </summary>
    Refusing
}

/// <summary>A refusal, said once: what the host will not do, and why.</summary>
public sealed class BlotterRefusedException(string message) : InvalidOperationException(message);

/// <summary>What the page's status bar shows.</summary>
public sealed record BlotterStatus
{
    public BlotterState State { get; init; }

    public string? Refusal { get; init; }

    public long Version { get; init; }

    /// <summary>How <c>orders_current</c> keeps up with <c>order_versions</c>.</summary>
    public CurrentSync Sync { get; init; }

    /// <summary>The longest any statement may run before it is stopped.</summary>
    public TimeSpan TimeLimit { get; init; }

    /// <summary>How long ago the oldest row the database has not been seen to hold was sent: how far behind it is, at least.</summary>
    public TimeSpan? Behind { get; init; }

    /// <summary>Order versions that have happened and that the order stream, running behind the fills, has not sent yet.</summary>
    public int OrdersHeld { get; init; }

    /// <summary>Rows the streams have sent late, and after the database had written them, while out of order.</summary>
    public long SentLate { get; init; }

    public long SentAfterDatabase { get; init; }

    public int NotYetIngested { get; init; }

    /// <summary>Versions written that the lagging job has not applied to <c>orders_current</c> yet.</summary>
    public int CurrentWaiting { get; init; }

    public bool IngestionStalled { get; init; }

    public bool CurrentStalled { get; init; }

    public double FeedRate { get; init; }

    public long Published { get; init; }

    public int TailOrders { get; init; }

    public int TailFills { get; init; }

    /// <summary>Rows of the tail the database has been seen to hold, waiting to leave.</summary>
    public int TailConfirmed { get; init; }

    public long TimedOut { get; init; }

    public long TailVersions { get; init; }

    public long TailBytes { get; init; }

    public int Scopes { get; init; }

    public long ScopeBytes { get; init; }

    public long PageBytes { get; init; }

    /// <summary>The alert views and the tables they read.</summary>
    public long AlertBytes { get; init; }

    public long PageReads { get; init; }

    public long PageLookups { get; init; }

    /// <summary>Reads a kept window answered, and windows read ahead of the scrolling.</summary>
    public long PageHits { get; init; }

    public long ReadAheads { get; init; }

    public long Statements { get; init; }

    public long Budget { get; init; }

    public long UsedBytes { get; init; }

    public long DatabaseBytes { get; init; }

    public long ManagedHeapBytes { get; init; }

    public double LastRefreshMs { get; init; }

    public int LastRefreshVersions { get; init; }

    /// <summary>Lookups of the whole tail run, and rows they let leave it.</summary>
    public long Confirmations { get; init; }

    public long Evicted { get; init; }

    /// <summary>Versions the last gap fill read, and versions gap fills have added to the tail in all.</summary>
    public int GapRead { get; init; }

    public long GapFilled { get; init; }
}
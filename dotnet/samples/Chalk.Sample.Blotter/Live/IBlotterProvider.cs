using Chalk.Sources;

namespace Chalk.Sample.Blotter.Live;

/// <summary>How many rows each table holds, for the planner to price by.</summary>
public readonly record struct TableRows(long Orders, long Versions, long Fills);

/// <summary>What the host reads the database through: ChalkQL's source, and the database's own estimates if it gives any.</summary>
public sealed record DatabaseReads(ISourceRuntime Source, IRowEstimates? Estimates);

/// <summary>
///     The database the blotter reads live, wherever it is and whatever it is: the one seam a different
///     database plugs into. The host asks of it only what a bolt-on may: a ChalkQL source over
///     <c>blotter.orders_current</c>, <c>blotter.order_versions</c> and <c>blotter.fills</c>, every statement stopped
///     at a time limit; and, if the database can give them, estimates and counts off the page's path, and
///     whether a failure was the database stopping a statement at the limit.
/// </summary>
/// <remarks>
///     <para>
///         The tables must be append-only, as the host's merge assumes: every change to an order is a new
///         version, numbered above its last and never changed once written; <c>orders_current</c> changes only
///         to a higher version; fills are only added; nothing is deleted; and no key is reused. A provider
///         that cannot promise this is not one the host can merge with its tail.
///     </para>
///     <para>
///         The source must name the tables by those catalog names, declare their keys and the indexes the
///         database has, and compare text byte by byte, as the host does when it merges the database's rows
///         with its own.
///     </para>
///     <para>
///         A provider only reads: someone else's system writes the tables. In this sample, the simulated
///         house does, through <see cref="Fixtures.IHouseDatabase" />, which hands the host its provider.
///     </para>
/// </remarks>
public interface IBlotterProvider : IAsyncDisposable
{
    /// <summary>What the database is called, in what the host says: "PostgreSQL".</summary>
    string Name { get; }

    /// <summary>
    ///     What the host reads through, every statement stopped at <paramref name="timeLimit" />, by the
    ///     database itself where it can, so that a read past the limit frees the database's work too.
    /// </summary>
    DatabaseReads Open(TableRows rows, TimeSpan timeLimit);

    /// <summary>
    ///     Whether <paramref name="failure" /> is the database stopping a statement at the time limit, however a source
    ///     wrapped it.
    /// </summary>
    bool StoppedAtTimeLimit(Exception failure);
}

/// <summary>The database's estimate of a page: the rows it expects to read and to sort, and its plan.</summary>
public sealed record ReadEstimate(double RowsRead, double RowsSorted, string PlanText);

/// <summary>
///     A database's own estimates, from its statistics, in milliseconds where a count may read a table;
///     and counts off the page's path, given longer than a page may take. Without them, the host counts
///     through ChalkQL, within the page's time limit.
/// </summary>
public interface IRowEstimates
{
    /// <summary>
    ///     What a page of <paramref name="limit" /> rows under <paramref name="filter" /> and <paramref name="order" />
    ///     would cost.
    /// </summary>
    Task<ReadEstimate> EstimateAsync(Tab tab, FilterSpec filter, SortSpec order, int limit, CancellationToken ct);

    /// <summary>About how many rows pass the filter.</summary>
    Task<long> RowsAsync(Tab tab, FilterSpec filter, CancellationToken ct);

    /// <summary>About how many rows pass the filter and sort before <paramref name="key" />.</summary>
    Task<long> RowsBeforeAsync(FilterSpec filter, SortSpec order, IRow key, CancellationToken ct);

    /// <summary>How many rows pass the filter, counted, for up to <paramref name="limit" />.</summary>
    Task<long> CountAsync(Tab tab, FilterSpec filter, TimeSpan limit, CancellationToken ct);
}
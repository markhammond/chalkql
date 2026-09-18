using Chalk.Sources;

namespace Chalk.Client;

/// <summary>
/// What one refresh transaction changes (D260, <c>docs/design/35-poco-refresh.md</c> §2): tables
/// across any number of sources, replaced or extended, published under one catalog epoch.
/// </summary>
/// <example>
/// <code>
/// await engine.RefreshAsync(refresh =>
/// {
///     refresh.Refresh(sqlite);                       // re-introspect one source
///     refresh.Refresh(orders);                       // re-describe one table, statistics included
///     refresh.Append(orderDetails, batch.Lines);     // and land a micro-batch, same epoch
/// }, ct);
/// </code>
/// </example>
/// <remarks>
/// The builder only records. Every entry is validated before any of them is built, every new state
/// is built off the execution path while executions continue on the old one, and all of it is
/// published at once — so an execution sees the whole transaction or none of it, and a scheduled
/// refresh waits rather than publishing half a set.
/// </remarks>
public sealed class RefreshBuilder
{
    private readonly List<RefreshEntry> _entries = [];
    private readonly List<IRefreshTarget> _scopes = [];

    internal RefreshBuilder()
    {
    }

    /// <summary>
    /// Re-reads what <paramref name="target"/> names (D271 (f),
    /// <c>docs/design/44-catalog-registration.md</c> §6): a source target re-introspects that source,
    /// discovery included for a source over a database and every registration re-read for one over
    /// collections; a table target re-describes that one table, statistics included, and discovers
    /// nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The targets are the typed handles: D270's <c>Source</c> and <c>Table</c> from the tenancy
    /// package, the <c>PocoTable&lt;T&gt;</c> and <c>AdoTable</c> the source builders yield (D271
    /// (h)), or <see cref="RefreshTarget"/>'s two string spellings for a host that has no handle.
    /// </para>
    /// <para>
    /// Every scope in a transaction runs before the replacements and appends beside it, under the
    /// engine's one refresh lock and under one epoch — so a re-introspection cannot land between a
    /// host's own two writes, and the rows a <c>Replace</c> supplies are the ones that survive.
    /// </para>
    /// </remarks>
    public RefreshBuilder Refresh(IRefreshTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        _scopes.Add(target);
        return this;
    }

    /// <summary>
    /// The table's rows become exactly <paramref name="rows"/>. The collection is the engine's to
    /// read from now on and must not be mutated; the one it replaces is reported released when its
    /// last reader has finished.
    /// </summary>
    /// <param name="source">A source the engine was built with, which must be able to refresh.</param>
    /// <param name="table">The table, as it is named in that source's schema.</param>
    /// <param name="rows">The new rows, of the type the table was built over.</param>
    public RefreshBuilder Replace<T>(ISourceRuntime source, string table, IReadOnlyList<T> rows) =>
        Add(source, table, SourceRefreshKind.Replace, rows);

    /// <summary>
    /// The table's rows become the rows it already holds followed by <paramref name="rows"/> — the
    /// one incremental operation (D260 §3). An execution already reading the table keeps the prefix
    /// it started with; one started afterwards sees more.
    /// </summary>
    /// <remarks>
    /// The appended rows are copied into an array Chalk owns, so the collection given here is not
    /// held afterwards and is never reported released. The rows themselves are, of course, the
    /// host's, and the rule about not mutating them is the same one.
    /// </remarks>
    /// <inheritdoc cref="Replace{T}" path="/param"/>
    /// <param name="statistics">
    /// Whether this append recomputes the table's column statistics (D271 (g),
    /// <c>docs/design/44-catalog-registration.md</c> §7).
    /// <see cref="StatisticsRefresh.Defer"/> carries the previous snapshot's forward and leaves them
    /// for the next plain refresh — which is what a micro-batch wants, since the row count moves
    /// either way and only the distribution of values ages.
    /// </param>
    /// <inheritdoc cref="Append{T}(ISourceRuntime, string, IReadOnlyList{T})" path="/param"/>
    public RefreshBuilder Append<T>(
        ISourceRuntime source,
        string table,
        IReadOnlyList<T> rows,
        StatisticsRefresh statistics) =>
        Add(source, table, SourceRefreshKind.Append, rows, statistics);

    public RefreshBuilder Append<T>(ISourceRuntime source, string table, IReadOnlyList<T> rows) =>
        Add(source, table, SourceRefreshKind.Append, rows);

    /// <summary>
    /// The same replacement, naming the table by its handle (D271 (h),
    /// <c>docs/design/44-catalog-registration.md</c> §6 (h)): the source travels with the name and
    /// the row type is checked by the compiler, so neither the wrong source nor the wrong rows can
    /// reach this call.
    /// </summary>
    /// <remarks>
    /// The <c>(source, name, rows)</c> forms stay for a host that builds its catalog from
    /// configuration at run time and has no handle to hold. What they cannot do is tell two sources
    /// holding a table of one name over one row type apart, which is why the handle exists.
    /// </remarks>
    /// <param name="table">The handle a source builder yielded, or one looked up on the source.</param>
    /// <param name="rows">The new rows, of the type the table was built over.</param>
    public RefreshBuilder Replace<T>(ITableTarget<T> table, IReadOnlyList<T> rows) =>
        Add(table, SourceRefreshKind.Replace, rows);

    /// <inheritdoc cref="Replace{T}(ITableTarget{T}, IReadOnlyList{T})"/>
    /// <remarks>
    /// The appended rows are copied into an array Chalk owns, exactly as the <c>(source, name, rows)</c>
    /// form copies them.
    /// </remarks>
    public RefreshBuilder Append<T>(ITableTarget<T> table, IReadOnlyList<T> rows) =>
        Add(table, SourceRefreshKind.Append, rows);

    /// <inheritdoc cref="Append{T}(ISourceRuntime, string, IReadOnlyList{T}, StatisticsRefresh)"/>
    public RefreshBuilder Append<T>(
        ITableTarget<T> table, IReadOnlyList<T> rows, StatisticsRefresh statistics) =>
        Add(table, SourceRefreshKind.Append, rows, statistics);

    private RefreshBuilder Add<T>(
        ITableTarget<T> table,
        SourceRefreshKind kind,
        IReadOnlyList<T> rows,
        StatisticsRefresh statistics = StatisticsRefresh.Now)
    {
        ArgumentNullException.ThrowIfNull(table);
        return Add(table.Runtime, table.Table, kind, rows, statistics);
    }

    private RefreshBuilder Add<T>(
        ISourceRuntime source,
        string table,
        SourceRefreshKind kind,
        IReadOnlyList<T> rows,
        StatisticsRefresh statistics = StatisticsRefresh.Now)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentNullException.ThrowIfNull(rows);

        _entries.Add(new RefreshEntry(
            source,
            new SourceRefreshEntry
            {
                Table = table,
                Kind = kind,
                RowType = typeof(T),
                Rows = rows,
                Statistics = statistics,
            }));

        return this;
    }

    /// <summary>The entries in the order they were written, which is the order they are validated in.</summary>
    internal IReadOnlyList<RefreshEntry> Entries => _entries;

    /// <summary>What this transaction re-reads, in the order it was written (D271 (f)).</summary>
    internal IReadOnlyList<IRefreshTarget> Scopes => _scopes;

    internal sealed record RefreshEntry(ISourceRuntime Source, SourceRefreshEntry Entry);
}

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
    private long? _version;
    private DateTimeOffset? _clock;
    private bool _deferPlanning;

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
    /// The appended rows are read while the refresh runs, and copied into an array Chalk owns: keep
    /// the collection unchanged until <c>RefreshAsync</c> returns. It is not held after that, and is
    /// never reported released. The rows themselves are, of course, the host's, and the rule about
    /// not mutating them is the same one.
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
    /// The appended rows are read while the refresh runs and copied into an array Chalk owns, exactly as
    /// the <c>(source, name, rows)</c> form copies them: keep the collection unchanged until
    /// <c>RefreshAsync</c> returns.
    /// </remarks>
    public RefreshBuilder Append<T>(ITableTarget<T> table, IReadOnlyList<T> rows) =>
        Add(table, SourceRefreshKind.Append, rows);

    /// <inheritdoc cref="Append{T}(ISourceRuntime, string, IReadOnlyList{T}, StatisticsRefresh)"/>
    public RefreshBuilder Append<T>(
        ITableTarget<T> table, IReadOnlyList<T> rows, StatisticsRefresh statistics) =>
        Add(table, SourceRefreshKind.Append, rows, statistics);

    /// <summary>
    /// The table takes a replacement prepared before this refresh (design 69 §3): the replacement's
    /// rows, then the rows the table took since its cut, published in this refresh's epoch.
    /// </summary>
    /// <remarks>
    /// Under the lock this costs what an append of the rows since the cut costs. It is refused when the
    /// table was replaced after the replacement was prepared, and when the rows since the cut do not
    /// follow the replacement in the table's declared order; nothing in the refresh is then applied.
    /// </remarks>
    /// <param name="prepared">What <c>ChalkEngine.PrepareReplaceAsync</c> returned, not yet committed.</param>
    public RefreshBuilder Commit(PreparedReplacement prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        if (!prepared.Pending)
        {
            throw new InvalidOperationException(
                $"the replacement for '{prepared.Table}' has already been committed or disposed. Each "
                + "one is committed once; prepare another for the next.");
        }

        _entries.Add(new RefreshEntry(
            prepared.Source,
            new SourceRefreshEntry
            {
                Table = prepared.Table,
                Kind = SourceRefreshKind.Commit,
                RowType = prepared.Prepared.RowType,
                Rows = prepared.Prepared,
            }));

        return this;
    }

    /// <summary>
    /// Each row replaces the row with its key, or is added where the table holds none (D339,
    /// <c>docs/design/64-incremental-views.md</c> §2). Only a table that declares <c>Keyed(…)</c>
    /// takes one; a key given twice refuses the refresh, naming both rows.
    /// </summary>
    /// <param name="source">A source the engine was built with.</param>
    /// <param name="table">The table, as it is named in that source's schema.</param>
    /// <param name="rows">The rows, of the type the table was built over.</param>
    public RefreshBuilder Upsert<T>(ISourceRuntime source, string table, IReadOnlyList<T> rows) =>
        Add(source, table, SourceRefreshKind.Upsert, rows);

    /// <inheritdoc cref="Upsert{T}(ISourceRuntime, string, IReadOnlyList{T})"/>
    /// <param name="table">The handle a source builder yielded, or one looked up on the source.</param>
    /// <param name="rows">The rows, of the type the table was built over.</param>
    public RefreshBuilder Upsert<T>(ITableTarget<T> table, IReadOnlyList<T> rows) =>
        Add(table, SourceRefreshKind.Upsert, rows);

    /// <summary>
    /// Removes the rows with the keys of <paramref name="rows"/>, of which only the key members are
    /// read (D339). A key the table does not hold removes nothing; a key also upserted in this
    /// refresh refuses it.
    /// </summary>
    /// <param name="source">A source the engine was built with.</param>
    /// <param name="table">The table, as it is named in that source's schema.</param>
    /// <param name="rows">Rows carrying the keys to remove.</param>
    public RefreshBuilder Delete<T>(ISourceRuntime source, string table, IReadOnlyList<T> rows) =>
        Add(source, table, SourceRefreshKind.Delete, rows);

    /// <inheritdoc cref="Delete{T}(ISourceRuntime, string, IReadOnlyList{T})"/>
    /// <param name="table">The handle a source builder yielded, or one looked up on the source.</param>
    /// <param name="rows">Rows carrying the keys to remove.</param>
    public RefreshBuilder Delete<T>(ITableTarget<T> table, IReadOnlyList<T> rows) =>
        Add(table, SourceRefreshKind.Delete, rows);

    /// <summary>
    /// Moves an append-only or as-of table's frontier to <paramref name="time"/> without a row
    /// (D339): no row older than it, less the table's lateness, can arrive after this. It may stand
    /// beside the same table's rows, and may not move the frontier back.
    /// </summary>
    /// <param name="source">A source the engine was built with.</param>
    /// <param name="table">The table, as it is named in that source's schema.</param>
    /// <param name="time">A time of the type the table declares.</param>
    public RefreshBuilder Advance(ISourceRuntime source, string table, DateTime time) =>
        AddAdvance(source, table, time);

    /// <inheritdoc cref="Advance(ISourceRuntime, string, DateTime)"/>
    public RefreshBuilder Advance(ISourceRuntime source, string table, DateTimeOffset time) =>
        AddAdvance(source, table, time);

    /// <inheritdoc cref="Advance(ISourceRuntime, string, DateTime)"/>
    /// <param name="table">The handle a source builder yielded, or one looked up on the source.</param>
    /// <param name="time">A time of the type the table declares.</param>
    public RefreshBuilder Advance(ITableTarget table, DateTime time)
    {
        ArgumentNullException.ThrowIfNull(table);
        return AddAdvance(table.Runtime, table.Table, time);
    }

    /// <inheritdoc cref="Advance(ITableTarget, DateTime)"/>
    public RefreshBuilder Advance(ITableTarget table, DateTimeOffset time)
    {
        ArgumentNullException.ThrowIfNull(table);
        return AddAdvance(table.Runtime, table.Table, time);
    }

    /// <summary>
    /// Moves the engine's clock (<see cref="ChalkEngineOptions.Clock"/>) to <paramref name="time"/> in
    /// this refresh's epoch (D339). A standing view reads it as the current time. It moves only
    /// forward; an engine without a clock refuses the refresh.
    /// </summary>
    public RefreshBuilder AdvanceClock(DateTimeOffset time)
    {
        if (_clock is not null)
        {
            throw new InvalidOperationException(
                "this refresh already advances the clock. An epoch has one clock time.");
        }

        _clock = time;
        return this;
    }

    /// <summary>
    /// Numbers this refresh's epoch with the host's own version of its data (D339): what a standing
    /// view hands its sink as it commits, and what <see cref="ChalkEngine.Version"/> reads after.
    /// It must exceed the engine's version; a refresh without one is numbered one past it.
    /// </summary>
    public RefreshBuilder Version(long version)
    {
        if (_version is not null)
        {
            throw new InvalidOperationException(
                $"this refresh is already numbered version {_version}. An epoch has one version.");
        }

        _version = version;
        return this;
    }

    /// <summary>
    /// Leaves this refresh's statistics and plans to the background (design 69 §4): the rows are
    /// published now, and the statistics and plans follow at a later refresh.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Under the refresh lock, the tables this refresh changes keep the statistics they had, with
    /// their row counts exact, and nothing is sent to the planner. After it, one run in the
    /// background computes their statistics, sends them to the planner, and plans again each statement
    /// whose tables' rows have moved twofold since it was planned: a standing view's, and every
    /// prepared statement this engine made that is still held. One run at a time: a refresh that
    /// defers while one is running asks for another, over whatever is published when it starts.
    /// </para>
    /// <para>
    /// What the run produced takes effect at the next refresh, deferred or not, under its lock: the
    /// statistics enter the catalog and the plans swap in. <see cref="ChalkEngine.PlanAsync"/> swaps
    /// the plans in sooner, once a run it waits for is over. A prepared statement then runs its new
    /// plan, and reports its <see cref="PreparedQuery.PlanDigest"/> and plan text; its
    /// <see cref="PreparedQuery.LogicalDigest"/> is the same, and so are its results. An execution
    /// already running keeps the plan it started with. A statement prepared through an extension
    /// takes a new plan only when the extension checks it, as entitlements do: the new plan must pass
    /// the checks the first one passed and come with the same report.
    /// </para>
    /// <para>
    /// A refresh that defers its planning re-reads nothing: a scope re-describes its tables with
    /// their statistics now, which is the opposite request, and is refused beside this.
    /// </para>
    /// </remarks>
    public RefreshBuilder DeferPlanning()
    {
        _deferPlanning = true;
        return this;
    }

    private RefreshBuilder AddAdvance(ISourceRuntime source, string table, object time)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);

        _entries.Add(new RefreshEntry(
            source,
            new SourceRefreshEntry
            {
                Table = table,
                Kind = SourceRefreshKind.Advance,
                RowType = typeof(object),
                Rows = Array.Empty<object>(),
                Frontier = time,
            }));

        return this;
    }

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

    /// <summary>The version this refresh numbers its epoch with, if it says one (D339).</summary>
    internal long? VersionGiven => _version;

    /// <summary>Where this refresh moves the clock to, if anywhere (D339).</summary>
    internal DateTimeOffset? ClockGiven => _clock;

    /// <summary>Whether this refresh leaves its statistics and plans to the background (design 69 §4).</summary>
    internal bool PlanningDeferred => _deferPlanning;

    /// <summary>What this transaction re-reads, in the order it was written (D271 (f)).</summary>
    internal IReadOnlyList<IRefreshTarget> Scopes => _scopes;

    internal sealed record RefreshEntry(ISourceRuntime Source, SourceRefreshEntry Entry);
}

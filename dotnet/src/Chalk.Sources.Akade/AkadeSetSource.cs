using Apache.Arrow;
using Chalk.Catalog;
using Chalk.Ir;

namespace Chalk.Sources.Akade;

/// <summary>
/// Common implementation for Chalk's two Akade-backed sources.
///
/// There are deliberately two consistency modes:
///
/// 1. Host-owned live mode. The host may mutate the Akade set between Chalk reads, but must quiesce
///    mutation while an execution, scoped refresh or transactional-refresh preparation may read it.
///    The currently published catalog metadata may lag host mutations until a scoped refresh re-describes it.
///
/// 2. Transactional row refresh. RefreshBuilder.Replace/Append asks Chalk to publish a new logical
///    table state atomically. Those operations therefore require an editor and always build a fresh
///    Akade set off the execution path. Commit only swaps the already-built source snapshot.
///
/// TSet remains the concrete Akade set type so the ordinary and concurrent variants retain their
/// correctly typed registration/editor all the way through refresh preparation.
/// </summary>
public abstract class AkadeSetSource<T, TSet> :
    ISourceRuntime,
    IColumnarBatchSource,
    IRefreshableSource
    where TSet : class
{
    private readonly Lock _refreshing = new();
    
    private readonly Func<TSet> _externalRegistration;

    private AkadeSourceSnapshot<T, TSet> _snapshot;

    private SourceRuntimeMixin _runtime;

    private protected AkadeSetSource(
        AkadeSourceOptions<T> options,
        Func<TSet> registration,
        IAkadeSetBackend<T, TSet> initialPlan,
        TSet initial)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(initialPlan);
        ArgumentNullException.ThrowIfNull(initial);

        Options = options;
        _runtime = new(Options.Sharing);
        _externalRegistration = registration;

        _snapshot = BuildSnapshot(
            initial,
            initialPlan,
            previous: null,
            StatisticsRefresh.Now,
            CancellationToken.None);

        Table = new AkadeTable<T>(
            this,
            options.SchemaName,
            options.TableName);
    }

    private AkadeSourceSnapshot<T, TSet> Current => Volatile.Read(ref _snapshot);

    internal AkadeSourceOptions<T> Options { get; }

    public string SourceId => Options.SourceId;
    
    public string SchemaName => Options.SchemaName;
    
    public bool TryClaimEngine(object identity, out SourceSharing mode) => _runtime.TryClaimEngine(identity, out mode);

    public void ReleaseEngine(object identity) => _runtime.ReleaseEngine(identity);

    /// <summary>The sole logical table of this source.</summary>
    public AkadeTable<T> Table { get; }

    /// <summary>
    /// Whether row-bearing RefreshBuilder.Replace/Append can construct a fresh successor set.
    /// Scoped Refresh(target) never requires an editor.
    /// </summary>
    private protected abstract bool CanPrepareRowRefresh { get; }

    /// <summary>Constructs a fresh unpublished set containing exactly <paramref name="rows"/>.</summary>
    private protected abstract TSet BuildSuccessor(IReadOnlyList<T> rows);

    /// <summary>
    /// Discovers physical Akade index topology from a source-scoped refresh. A table-scoped refresh
    /// deliberately does not call this: table refresh re-describes, source refresh re-discovers.
    /// </summary>
    private protected abstract IAkadeSetBackend<T, TSet> DiscoverBackend(TSet set);
    
    /// <summary>
    /// Table-scoped refresh re-describes the currently published set and
    /// recomputes its metadata/statistics. It does not consult the external
    /// registration and does not rediscover physical indexes.
    ///
    /// Consequently, a transactional Replace/Append remains the current table
    /// state across subsequent table refreshes.
    /// </summary>
    public ValueTask RefreshTableAsync(string table, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ct.ThrowIfCancellationRequested();
        RequireTable(table);

        lock (_refreshing)
        {
            var current = Current;

            // Current.Set is authoritative here. In particular, it may be the
            // successor published by an earlier transactional Replace/Append.
            var set = current.Set;

            ValidateTopology(current.Plan, set, table);
            
            var next = BuildSnapshot(
                set,
                current.Plan,
                current,
                StatisticsRefresh.Now,
                ct);

            Publish(next);
        }

        return ValueTask.CompletedTask;
    }
    
    public ValueTask RefreshAsync(
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        lock (_refreshing)
        {
            var current = Current;

            // Important: NOT current.Set.
            var set = ReadExternalRegistration();

            // Source refresh is allowed to rediscover physical topology.
            var plan = DiscoverBackend(set);

            var next = BuildSnapshot(
                set,
                plan,
                current,
                StatisticsRefresh.Now,
                ct);

            Publish(next);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Validates row-bearing Replace/Append entries. A source without an editor is still fully usable:
    /// it supports live host mutation and scoped refreshes; it simply cannot promise the stronger
    /// Replace/Append snapshot semantics.
    /// </summary>
    public void ValidateRefresh(IReadOnlyList<SourceRefreshEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        if (entries.Count != 1)
        {
            throw new SourceContractException(
                SourceId,
                Options.TableName,
                entries.Count == 0
                    ? "a row refresh for this one-table Akade source contains no table entry."
                    : "an Akade source contains exactly one table and a row refresh may name it only once.");
        }

        var entry = entries[0];
        RequireTable(entry.Table);

        if (entry.RowType != typeof(T))
        {
            throw new SourceContractException(
                SourceId,
                entry.Table,
                $"the rows are {entry.RowType.Name}, but this table was built over {typeof(T).Name}. "
                + "A row refresh changes data, not the CLR row shape.");
        }

        if (entry.Rows is not IReadOnlyList<T>)
        {
            throw new SourceContractException(
                SourceId,
                entry.Table,
                $"the refresh rows are {entry.Rows.GetType().Name}, not IReadOnlyList<{typeof(T).Name}>.");
        }

        if (entry.Kind is not (SourceRefreshKind.Replace or SourceRefreshKind.Append))
        {
            throw new SourceContractException(
                SourceId,
                entry.Table,
                $"{entry.Kind} is not supported by an Akade source.");
        }

        if (!CanPrepareRowRefresh)
        {
            throw new SourceContractException(
                SourceId,
                entry.Table,
                "this Akade source has no transactional row-refresh editor. Direct host mutation and "
                + "Refresh(target) are supported, but RefreshBuilder.Replace/Append require Editor(...) "
                + "or RebuildWith(...) so Chalk can construct a fresh IndexedSet successor.");
        }
    }

    /// <summary>
    /// Prepares the fresh successor required by RefreshBuilder.Replace/Append. No published Akade set
    /// is mutated here. The returned commit contains no fallible work: Commit only publishes next.
    /// </summary>
    public ValueTask<ISourceRefreshCommit> PrepareRefreshAsync(
        IReadOnlyList<SourceRefreshEntry> entries,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ValidateRefresh(entries);
        ct.ThrowIfCancellationRequested();

        AkadeSourceSnapshot<T, TSet> next;

        lock (_refreshing)
        {
            var current = Current;
            var entry = entries[0];
            var supplied = (IReadOnlyList<T>)entry.Rows;

            IReadOnlyList<T> targetRows =
                entry.Kind == SourceRefreshKind.Replace
                    ? supplied
                    : AppendRows(current, supplied, ct);

            ct.ThrowIfCancellationRequested();

            var nextSet = BuildSuccessor(targetRows);
            if (nextSet is null)
            {
                throw new SourceContractException(
                    SourceId,
                    entry.Table,
                    "the Akade editor returned null.");
            }

            if (ReferenceEquals(nextSet, current.Set))
            {
                throw new SourceContractException(
                    SourceId,
                    entry.Table,
                    "the Akade editor returned the currently published set. Replace/Append require a "
                    + "fresh set so executions already reading the old state keep that state.");
            }

            // Replace/Append changes rows under the current logical table shape; it is not a discovery
            // operation. A rebuild function that silently changes index topology is therefore refused.
            ValidateTopology(current.Plan, nextSet, entry.Table);

            next = BuildSnapshot(
                nextSet,
                current.Plan,
                current,
                entry.Statistics,
                ct);
        }

        return ValueTask.FromResult<ISourceRefreshCommit>(
            new PreparedRefresh(this, next));
    }

    private IReadOnlyList<T> AppendRows(
        AkadeSourceSnapshot<T, TSet> current,
        IReadOnlyList<T> appended,
        CancellationToken ct)
    {
        // Append must build the exact successor prefix + delta without handing the published set to a
        // mutable editor. Both ordinary IndexedSet and CHALK002 ConcurrentIndexedSet sources require
        // host-enforced quiescence while Chalk reads the currently published set.
        var oldCount = current.Plan.Count(current.Set);
        var result = new T[checked(oldCount + appended.Count)];

        var i = 0;
        foreach (var row in current.Plan.FullScan(current.Set))
        {
            ct.ThrowIfCancellationRequested();

            if (i >= oldCount)
            {
                throw ChangedDuringPreparation(oldCount, i + 1);
            }

            result[i++] = row;
        }

        if (i != oldCount)
        {
            throw ChangedDuringPreparation(oldCount, i);
        }

        for (var j = 0; j < appended.Count; j++)
        {
            result[i + j] = appended[j];
        }

        return result;
    }

    private SourceContractException ChangedDuringPreparation(int expected, int actual) =>
        new(
            SourceId,
            Options.TableName,
            $"the Akade set reported {expected} row(s) but enumerated {actual} while Append was being "
            + "prepared. The host must not mutate an Akade set concurrently with a Chalk execution, "
            + "scoped refresh or transactional Replace/Append preparation.");

    private AkadeSourceSnapshot<T, TSet> BuildSnapshot(
        TSet set,
        IAkadeSetBackend<T, TSet> plan,
        AkadeSourceSnapshot<T, TSet>? previous,
        StatisticsRefresh statistics,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var backend = plan.Build(set, Options);
        var fresh = DecorateSchema(backend.DescribeSchema());

        var deferred = statistics == StatisticsRefresh.Defer && previous is not null;
        var schema = deferred
            ? CarryColumnStatistics(previous!.Schema, fresh)
            : fresh;

        return new AkadeSourceSnapshot<T, TSet>(
            set,
            plan,
            backend,
            schema,
            deferred);
    }

    private TSet ReadExternalRegistration() =>
        _externalRegistration()
        ?? throw new SourceContractException(
            SourceId,
            Options.TableName,
            "the Akade registration returned null.");

    private void ValidateTopology(
        IAkadeSetBackend<T, TSet> plan,
        TSet set,
        string table)
    {
        try
        {
            plan.ValidateTopology(set);
        }
        catch (AkadeIndexTopologyException ex)
        {
            throw new SourceContractException(
                SourceId,
                table,
                ex.Message + " Use Refresh(source) if the physical Akade index topology intentionally changed.");
        }
    }

    private SchemaDescriptor DecorateSchema(SchemaDescriptor schema) => new()
    {
        SourceId = schema.SourceId,
        Name = schema.Name,
        Kind = SourceKind.Local,
        Dialect = null,
        Capabilities = Catalog.SourceCapabilities.None,
        DialectProfile = DialectProfileDescriptor.None,
        CostProfile = Options.CostProfile,
        Tables = schema.Tables,
        Functions = Options.Functions,
        TrustSourceRowLevelSecurity = Options.TrustSourceRowLevelSecurity,
    };

    /// <summary>
    /// Carries only value-distribution statistics. Row count and all physical/table shape come from
    /// the freshly built successor, so StatisticsRefresh.Defer never makes cardinality stale.
    /// </summary>
    private static SchemaDescriptor CarryColumnStatistics(
        SchemaDescriptor previous,
        SchemaDescriptor fresh)
    {
        var oldTable = previous.Tables.Single();
        var newTable = fresh.Tables.Single();

        if (oldTable.Columns.Count != newTable.Columns.Count)
        {
            throw new SourceContractException(
                fresh.SourceId,
                newTable.Name,
                "the row shape changed across an Akade row refresh.");
        }

        var columns = new ColumnDescriptor[newTable.Columns.Count];
        for (var i = 0; i < columns.Length; i++)
        {
            var oldColumn = oldTable.Columns[i];
            var newColumn = newTable.Columns[i];

            if (!string.Equals(oldColumn.Name, newColumn.Name, StringComparison.Ordinal)
                || !Equals(oldColumn.Type, newColumn.Type))
            {
                throw new SourceContractException(
                    fresh.SourceId,
                    newTable.Name,
                    $"column {i} changed from '{oldColumn.Name}'/{oldColumn.Type} to "
                    + $"'{newColumn.Name}'/{newColumn.Type} across row refresh.");
            }

            columns[i] = new ColumnDescriptor
            {
                Name = newColumn.Name,
                Type = newColumn.Type,
                Statistics = oldColumn.Statistics,
            };
        }

        var carriedTable = new TableDescriptor
        {
            Name = newTable.Name,
            Columns = columns,
            RowCount = newTable.RowCount,
            RowCountKind = newTable.RowCountKind,
            CostProfile = newTable.CostProfile,
            UniqueKeys = newTable.UniqueKeys,
            Collations = newTable.Collations,
            Indexes = newTable.Indexes,
            ForeignKeys = newTable.ForeignKeys,
            Partitioning = newTable.Partitioning,
            Entitlement = newTable.Entitlement,
            IsPublic = newTable.IsPublic,
        };

        return new SchemaDescriptor
        {
            SourceId = fresh.SourceId,
            Name = fresh.Name,
            Kind = fresh.Kind,
            Dialect = fresh.Dialect,
            Capabilities = fresh.Capabilities,
            DialectProfile = fresh.DialectProfile,
            CostProfile = fresh.CostProfile,
            Tables = [carriedTable],
            Functions = fresh.Functions,
            TrustSourceRowLevelSecurity = fresh.TrustSourceRowLevelSecurity,
        };
    }

    private void Publish(AkadeSourceSnapshot<T, TSet> next) =>
        Interlocked.Exchange(ref _snapshot, next);

    private sealed class PreparedRefresh(
        AkadeSetSource<T, TSet> source,
        AkadeSourceSnapshot<T, TSet> next)
        : ISourceRefreshCommit
    {
        public void Commit() => source.Publish(next);
    }

    private void RequireTable(string table)
    {
        if (!string.Equals(table, Options.TableName, StringComparison.OrdinalIgnoreCase))
        {
            throw new SourceContractException(
                SourceId,
                table,
                $"there is no such table in schema '{Options.SchemaName}'. "
                + $"The Akade source contains only '{Options.TableName}'.");
        }
    }

    public SchemaDescriptor DescribeSchema() => Current.Schema;

    public IAsyncEnumerable<RecordBatch> ScanAsync(
        ScanRequest request,
        ScanContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        RequireTable(request.Table);

        // Capture one backend. A transactional publish cannot redirect this scan to its successor.
        // Direct host mutation of the set is intentionally outside that guarantee.
        var backend = Current.Backend;
        return backend.ScanAsync(request, context, ct);
    }

    public IAsyncEnumerable<RecordBatch> IndexLookupAsync(
        IndexLookupRequest request,
        ScanContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        RequireTable(request.Table);

        var backend = Current.Backend;
        return backend.IndexLookupAsync(request, context, ct);
    }

    IColumnarScan? IColumnarBatchSource.ColumnarScan(
        ScanRequest request,
        ScanContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        RequireTable(request.Table);

        var backend = (IColumnarBatchSource)Current.Backend;
        return backend.ColumnarScan(request, context);
    }

    IColumnarScan? IColumnarBatchSource.ColumnarLookup(
        IndexLookupRequest request,
        ScanContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        RequireTable(request.Table);

        var backend = (IColumnarBatchSource)Current.Backend;
        return backend.ColumnarLookup(request, context);
    }
}

internal sealed record AkadeSourceSnapshot<T, TSet>(
    TSet Set,
    IAkadeSetBackend<T, TSet> Plan,
    Chalk.Sources.Poco.PocoSource Backend,
    SchemaDescriptor Schema,
    bool StatisticsDeferred)
    where TSet : class;

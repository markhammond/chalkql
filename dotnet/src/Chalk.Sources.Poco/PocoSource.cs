using System.Buffers;
using System.Diagnostics;
using Apache.Arrow;
using Chalk.Catalog;
using Chalk.Entitlements;
using Array = System.Array;
using ArrowSchema = Apache.Arrow.Schema;
using RowCountKind = Chalk.Ir.RowCountKind;
using SourceKind = Chalk.Ir.SourceKind;

namespace Chalk.Sources.Poco;

/// <summary>
/// An in-process source over <see cref="IReadOnlyList{T}"/> and <see cref="IReadOnlyCollection{T}"/>
/// collections of POCOs, built by <see cref="PocoSourceBuilder"/>. Registering a list makes it a
/// first-class SQL table with a real row count, real unique keys, real collations and — from M2 —
/// real indexes and statistics, which is the whole point of Chalk (rev 3 §6).
/// </summary>
public sealed class PocoSource : ISourceRuntime, IColumnarBatchSource, IRefreshableSource
{
    private readonly PocoTableRuntime[] _tables;
    private readonly FunctionDescriptor[] _functions;

    /// <summary>One refresh of this source at a time; snapshots are built one table after another.</summary>
    private readonly Lock _refreshing = new();

    /// <summary>The handles this source's tables are named by, made once per name (D271 (h)).</summary>
    private readonly Dictionary<string, PocoTableBinding> _bindings;

    private SourceRuntimeMixin _runtime;

    internal PocoSource(
        SourceSharing sharing, string sourceId, string schemaName, PocoTableRuntime[] tables, FunctionDescriptor[] functions)
        : this(sharing, sourceId, schemaName, tables, functions, bindings: null)
    {
    }

    /// <summary>
    /// The same, with the handles the builder yielded (D271 (h)), bound to this source now that there
    /// is one. A binding is made at <c>AddTable</c>, when no source exists; this is where it acquires
    /// the runtime it names.
    /// </summary>
    internal PocoSource(
        SourceSharing sharing,
        string sourceId,
        string schemaName,
        PocoTableRuntime[] tables,
        FunctionDescriptor[] functions,
        IReadOnlyDictionary<string, PocoTableBinding>? bindings)
    {
        _runtime = new(sharing);
        SourceId = sourceId;
        SchemaName = schemaName;
        _tables = tables;
        _functions = functions;
        _bindings = bindings is null
            ? new Dictionary<string, PocoTableBinding>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, PocoTableBinding>(bindings, StringComparer.OrdinalIgnoreCase);

        foreach (var binding in _bindings.Values)
        {
            binding.Bind(this);
        }

        foreach (var table in tables)
        {
            table.Released = Report;
        }
    }

    public bool TryClaimEngine(object identity, out SourceSharing mode) => _runtime.TryClaimEngine(identity, out mode);

    public void ReleaseEngine(object identity) => _runtime.ReleaseEngine(identity);

    /// <summary>
    /// The handle for one of this source's tables (D271 (h)), for a host that did not take it at
    /// registration. The row type must be the one the table was registered over; a mismatch is
    /// refused here, naming both, rather than at the refresh that would have used it.
    /// </summary>
    public PocoTable<T> Table<T>(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var declared = Require(name);
        if (declared.RowType != typeof(T))
        {
            throw new ArgumentException(
                $"table '{declared.Name}' on source '{SourceId}' was registered over "
                + $"{declared.RowType.Name}, and a handle over {typeof(T).Name} was asked for.",
                nameof(T));
        }

        // Under the dictionary's own lock, as `AdoSource.Table` is: a host may ask two threads for
        // handles to two tables of one source before anything has refreshed, and an unguarded
        // Dictionary written from two threads at once is a corrupt bucket chain rather than a lost
        // entry. One handle per name is also what D271 (h) promises, and two racing readers must not
        // each make their own (F94).
        lock (_bindings)
        {
            if (!_bindings.TryGetValue(declared.Name, out var binding))
            {
                binding = new PocoTableBinding
                {
                    SourceId = SourceId,
                    Schema = SchemaName,
                    Table = declared.Name,
                    RowType = declared.RowType,
                };
                binding.Bind(this);
                _bindings[declared.Name] = binding;
            }

            return new PocoTable<T>(binding);
        }
    }

    /// <inheritdoc />
    public string SourceId { get; }

    /// <summary>
    /// A snapshot that has been replaced and whose last reader has gone: the table it belonged to
    /// and the collection the host handed over for it (D260 §1). A host that recycles buffers learns
    /// here when it may — until this is raised, an execution may still be reading those rows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Raised once per replaced snapshot and never for the current one: at the swap when no
    /// execution holds the old snapshot, and when the last one finishes otherwise. It is therefore
    /// raised on whichever thread got there — the refreshing one, or the one disposing a scan — and
    /// a handler that throws fails that operation, so a handler does the least it can and returns.
    /// </para>
    /// <para>
    /// A snapshot an <c>Append</c> built over arrays of Chalk's own reports nothing, because the
    /// host gave it nothing to have back.
    /// </para>
    /// </remarks>
    public event Action<PocoSnapshotRelease>? SnapshotReleased;

    /// <summary>
    /// Re-reads every <c>Func</c> registration, builds the new snapshots off the execution path —
    /// extraction, permutations, clustered copies, statistics, the D17 verification — and swaps each
    /// table's reference to the one it built (D260 §2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A table whose registration reports the collection it is already serving keeps its snapshot
    /// and costs nothing, which is what a table registered with a plain list always does.
    /// </para>
    /// <para>
    /// A build that fails — a replacement that breaks a declared collation, say — leaves every table
    /// on the snapshot it was serving, because nothing is swapped until it is built. The tables
    /// before it in the source have already swapped, though: one source's refresh is not itself a
    /// transaction, and <c>ChalkEngine.RefreshAsync(plan)</c> is what makes several tables one.
    /// </para>
    /// </remarks>
    public ValueTask RefreshAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        lock (_refreshing)
        {
            foreach (var table in _tables)
            {
                ct.ThrowIfCancellationRequested();
                table.Refresh();
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Re-reads one table's registration (D271 (f)): the <c>Func</c> it was declared with, and the
    /// snapshot — rows, indexes and statistics — built from what it returns. Discovers nothing: a
    /// POCO source's tables are its registrations, and those are fixed at <c>Build()</c>.
    /// </summary>
    public ValueTask RefreshTableAsync(string table, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ct.ThrowIfCancellationRequested();

        lock (_refreshing)
        {
            Require(table).Refresh();
        }

        return ValueTask.CompletedTask;
    }

    private void Report(string table, object collection) =>
        SnapshotReleased?.Invoke(new PocoSnapshotRelease { Table = table, Collection = collection });

    /// <inheritdoc />
    public void ValidateRefresh(IReadOnlyList<SourceRefreshEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var table = Require(entry.Table);

            if (!named.Add(table.Name))
            {
                throw new SourceContractException(
                    SourceId,
                    entry.Table,
                    "a refresh names this table twice. One transaction says what a table becomes "
                    + "once; put the rows together and say it once.");
            }

            if (entry.RowType != table.RowType)
            {
                throw new SourceContractException(
                    SourceId,
                    entry.Table,
                    $"the rows are {entry.RowType.Name}, but this table was built over "
                    + $"{table.RowType.Name}. A refresh replaces a table's rows, not its shape.");
            }

            table.ValidateRefresh(SourceId, entry);
        }
    }

    /// <inheritdoc />
    public ValueTask<ISourceRefreshCommit> PrepareRefreshAsync(
        IReadOnlyList<SourceRefreshEntry> entries, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ValidateRefresh(entries);

        var prepared = new IPocoTableCommit[entries.Count];
        lock (_refreshing)
        {
            for (var i = 0; i < entries.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                prepared[i] = Require(entries[i].Table).PrepareRefresh(entries[i]);
            }
        }

        return ValueTask.FromResult<ISourceRefreshCommit>(new PreparedRefresh(prepared));
    }

    /// <summary>Every table's new snapshot, built and waiting for the engine to say when.</summary>
    private sealed class PreparedRefresh : ISourceRefreshCommit
    {
        private readonly IPocoTableCommit[] _tables;

        public PreparedRefresh(IPocoTableCommit[] tables) => _tables = tables;

        public void Commit()
        {
            foreach (var table in _tables)
            {
                table.Commit();
            }
        }
    }

    /// <summary>The SQL schema these tables live in. <c>main</c> unless the builder was told otherwise.</summary>
    public string SchemaName { get; }

    /// <inheritdoc />
    /// <remarks>
    /// Every table answers from the snapshot it is serving, so the row count, the statistics and the
    /// index descriptors of one call are one consistent reading (D260 §1). The shape — names, types,
    /// keys, collations, indexes — is fixed at <c>Build()</c>; the rows behind it move only when a
    /// refresh moves them. Statistics are computed once per snapshot, so calling this repeatedly
    /// costs nothing.
    /// </remarks>
    public SchemaDescriptor DescribeSchema() => new()
    {
        SourceId = SourceId,
        Name = SchemaName,
        Kind = SourceKind.Local,
        Capabilities = SourceCapabilities.None,
        Tables = Array.ConvertAll(_tables, t => t.Describe()),
        Functions = _functions,
    };

    /// <summary>
    /// A declared index by name, for a host that wants to inspect one or run
    /// <c>PocoIndexConformance.Verify</c> against it. Null when there is no such table or index.
    /// </summary>
    public IPocoIndex<T>? FindIndex<T>(string table, string index)
    {
        var found = _tables.FirstOrDefault(
            t => string.Equals(t.Name, table, StringComparison.OrdinalIgnoreCase));
        return found?.FindIndex(index) as IPocoIndex<T>;
    }

    /// <summary>How much memory the built-in indexes hold, per row of each table. For the build report.</summary>
    public IReadOnlyDictionary<string, long> IndexBytesPerRow()
    {
        var bytes = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var table in _tables)
        {
            foreach (var (index, perRow) in table.IndexSizes())
            {
                bytes[$"{table.Name}.{index}"] = perRow;
            }
        }

        return bytes;
    }

    /// <inheritdoc />
    public IAsyncEnumerable<RecordBatch> ScanAsync(
        ScanRequest request, ScanContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var table = Require(request.Table);

        if (request.PushedFilter is not null)
        {
            // §4: a source that never declared a pushable predicate must refuse one rather than
            // quietly ignore it, which would return too many rows.
            throw new SourceContractException(
                SourceId,
                request.Table,
                "a filter was pushed down, but this source declares SourceCapabilities.None.");
        }

        RequireBatchSize(request.BatchSize);
        return table.Scan(SourceId, request, context, ct);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<RecordBatch> IndexLookupAsync(
        IndexLookupRequest request, ScanContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var table = Require(request.Table);
        RequireBatchSize(request.BatchSize);
        return table.Lookup(SourceId, request, context, ct);
    }

    /// <summary>
    /// The columnar fast path of <c>15-zero-allocation-execution.md</c> §1 (D61): the scan fills the
    /// caller's slot straight from the chunk writers' staging, so an in-process query creates no
    /// Arrow object at all between the collection and the host's output batch.
    /// </summary>
    IColumnarScan? IColumnarBatchSource.ColumnarScan(ScanRequest request, ScanContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        if (request.PushedFilter is not null)
        {
            throw new SourceContractException(
                SourceId,
                request.Table,
                "a filter was pushed down, but this source declares SourceCapabilities.None.");
        }

        RequireBatchSize(request.BatchSize);
        return Require(request.Table).ColumnarScan(SourceId, request, context);
    }

    IColumnarScan? IColumnarBatchSource.ColumnarLookup(IndexLookupRequest request, ScanContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        RequireBatchSize(request.BatchSize);
        return Require(request.Table).ColumnarLookup(SourceId, request, context);
    }

    private PocoTableRuntime Require(string name) =>
        _tables.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? throw new SourceContractException(
            SourceId,
            name,
            $"there is no such table in schema '{SchemaName}'. Known tables: "
            + string.Join(", ", _tables.Select(t => t.Name)) + ".");

    private static void RequireBatchSize(int batchSize)
    {
        if (batchSize < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(batchSize), batchSize, "BatchSize must be at least one row.");
        }
    }
}

/// <summary>
/// A snapshot the source has finished with: the table it belonged to, and the collection the host
/// registered or handed to a replacement (D260 §1).
/// </summary>
public sealed class PocoSnapshotRelease
{
    /// <summary>The table whose rows these were, as it is named in the catalog.</summary>
    public required string Table { get; init; }

    /// <summary>
    /// The collection itself — the <c>IReadOnlyList&lt;T&gt;</c> or <c>IReadOnlyCollection&lt;T&gt;</c>
    /// the host gave, by reference, so a host that pools them knows which one it has back.
    /// </summary>
    public required object Collection { get; init; }
}

/// <summary>The non-generic view of a registered table, so <see cref="PocoSource"/> need not be generic.</summary>
internal abstract class PocoTableRuntime
{
    public abstract string Name { get; }

    /// <summary>
    /// Where a replaced snapshot's collection is reported. Set by the source that owns this table,
    /// before anything can read it.
    /// </summary>
    public Action<string, object>? Released { get; set; }

    /// <summary>
    /// Re-reads the registration and publishes a snapshot of what it reports, or keeps the current
    /// snapshot when the registration reports the collection it was built from (D260 §2).
    /// </summary>
    public abstract void Refresh();

    /// <summary>Refuses an entry this table cannot serve, before anything is built.</summary>
    public abstract void ValidateRefresh(string sourceId, SourceRefreshEntry entry);

    /// <summary>Builds the snapshot this entry asks for, without publishing it.</summary>
    public abstract IPocoTableCommit PrepareRefresh(SourceRefreshEntry entry);

    public abstract TableDescriptor Describe();

    public abstract IEnumerable<(string Index, long BytesPerRow)> IndexSizes();

    public abstract object? FindIndex(string name);

    public abstract IAsyncEnumerable<RecordBatch> Scan(
        string sourceId, ScanRequest request, ScanContext context, CancellationToken ct);

    public abstract IAsyncEnumerable<RecordBatch> Lookup(
        string sourceId, IndexLookupRequest request, ScanContext context, CancellationToken ct);

    /// <summary>The scan as a slot-filling one (§1, D61), which is what the engine prefers.</summary>
    public abstract IColumnarScan ColumnarScan(string sourceId, ScanRequest request, ScanContext context);

    /// <summary>The index lookup as a slot-filling one.</summary>
    public abstract IColumnarScan ColumnarLookup(
        string sourceId, IndexLookupRequest request, ScanContext context);

    /// <summary>The foreign keys as declared, before the parent's columns have been resolved (F14).</summary>
    public abstract IReadOnlyList<PocoForeignKey> DeclaredForeignKeys { get; }

    /// <summary>
    /// The index of a column by SQL name or by member name, case-insensitively, or -1. A foreign key
    /// names the parent's column either way round, and the parent may have renamed it.
    /// </summary>
    public abstract int ColumnIndex(string nameOrMember);

    /// <summary>Fixes the resolved keys, which is what <see cref="Describe"/> then reports.</summary>
    public abstract void ResolveForeignKeys(IReadOnlyList<ForeignKeyDescriptor> keys);

    /// <summary>The CLR row type, so a foreign key may name its parent by type (F16).</summary>
    public abstract Type RowType { get; }

    /// <summary>
    /// The distinct values of <paramref name="columns"/> over every row, as the scan would emit
    /// them. The parent side of a verified foreign key; one pass, and it boxes, because this runs
    /// once per <c>Build()</c> rather than per query.
    /// </summary>
    public abstract HashSet<object?[]> KeySet(IReadOnlyList<int> columns);

    /// <summary>
    /// Probes every row's non-NULL key against <paramref name="parentKeys"/> and raises
    /// <c>CatalogVerificationException</c> naming the first orphan (F16). A key with any NULL in it
    /// is skipped: SQL's referential constraint says nothing about those.
    /// </summary>
    public abstract void ProbeForeignKey(
        ForeignKeyDescriptor key, HashSet<object?[]> parentKeys, PocoTableRuntime parent);
}

/// <summary>One table's share of a refresh transaction, built and waiting to be published.</summary>
internal interface IPocoTableCommit
{
    void Commit();
}

/// <summary>
/// A declared foreign key whose child columns are resolved but whose parent's are not: the parent
/// table may be registered after this one (F14).
/// </summary>
internal sealed record PocoForeignKey(
    int[] Columns, string? ParentTable, Type? ParentType, string[] ParentColumns, bool Verify);

/// <summary>
/// One registered collection. Rows are reached by index (<c>rows[i]</c>) wherever the registration
/// allows it, which is what lets an index lookup gather from a permutation without materialising
/// rows (work plan §5); a collection that only enumerates is staged a batch at a time instead.
/// </summary>
internal sealed class PocoTableRuntime<T> : PocoTableRuntime
{
    private readonly PocoSnapshotFactory<T> _factory;
    private readonly PocoColumn<T>[] _columns;
    private readonly IReadOnlyList<UniqueKeyDescriptor> _uniqueKeys;
    private readonly IReadOnlyList<CollationDescriptor> _collations;
    private readonly PocoStatisticsPlan _statisticsPlan;
    private readonly IReadOnlyDictionary<string, int> _byMember;
    private IReadOnlyList<ForeignKeyDescriptor> _foreignKeys = [];

    /// <summary>
    /// This table's state, replaced whole and never mutated (D260 §1). Volatile because a refresh
    /// publishes the next one from a thread that is not the one reading it.
    /// </summary>
    private PocoTableSnapshot<T> _snapshot;

    public PocoTableRuntime(
        string name,
        PocoSnapshotFactory<T> factory,
        PocoTableSnapshot<T> snapshot,
        PocoColumn<T>[] columns,
        IReadOnlyList<UniqueKeyDescriptor> uniqueKeys,
        IReadOnlyList<CollationDescriptor> collations,
        PocoStatisticsPlan statistics,
        IReadOnlyList<PocoForeignKey> foreignKeys,
        IReadOnlyDictionary<string, int> byMember)
        : this(name, factory, snapshot, columns, uniqueKeys, collations, statistics, foreignKeys,
            byMember, entitlement: null)
    {
    }

    /// <summary>
    /// The same, with the entitlement this table declares (step 26,
    /// <c>docs/design/16-entitlements.md</c> §1). Null — every table until a host says otherwise —
    /// means every row and every column, adds no bytes to the catalog and installs no planner pass.
    /// </summary>
    public PocoTableRuntime(
        string name,
        PocoSnapshotFactory<T> factory,
        PocoTableSnapshot<T> snapshot,
        PocoColumn<T>[] columns,
        IReadOnlyList<UniqueKeyDescriptor> uniqueKeys,
        IReadOnlyList<CollationDescriptor> collations,
        PocoStatisticsPlan statistics,
        IReadOnlyList<PocoForeignKey> foreignKeys,
        IReadOnlyDictionary<string, int> byMember,
        TableEntitlementDescriptor? entitlement)
    {
        _entitlement = entitlement;
        Name = name;
        _factory = factory;
        _snapshot = snapshot;
        _snapshot.OnReleased = Report;
        _columns = columns;
        _uniqueKeys = uniqueKeys;
        _collations = collations;
        _statisticsPlan = statistics;
        DeclaredForeignKeys = foreignKeys;
        _byMember = byMember;
    }

    /// <inheritdoc />
    public override void Refresh()
    {
        var collection = _factory.Registration();

        // The registration reporting the collection this table already holds is the common case for
        // a plain list, which is registered as a Func that returns it: there is nothing to rebuild,
        // and reporting those rows released while the current snapshot still reads them would be a
        // lie. Reference identity is the whole test, because a host that mutates a collection it
        // handed over has broken the one rule it was given (§1).
        if (ReferenceEquals(collection, Current.HostCollection))
        {
            // Nothing to rebuild — but a plain refresh is what a deferral was deferred *to*
            // (D271 (g)), so the statistics carried past an append are recomputed now, over the rows
            // this snapshot already holds.
            Current.EndDeferral();
            return;
        }

        Publish(Carry(_factory.From(collection), StatisticsRefresh.Now));
    }

    /// <inheritdoc />
    public override void ValidateRefresh(string sourceId, SourceRefreshEntry entry)
    {
        if (entry.Rows is not IReadOnlyList<T>)
        {
            throw new SourceContractException(
                sourceId,
                entry.Table,
                $"the rows are {entry.Rows.GetType().Name}, which is not an "
                + $"IReadOnlyList<{typeof(T).Name}>.");
        }

        if (entry.Kind is SourceRefreshKind.Append && !_factory.RandomAccess)
        {
            throw new SourceContractException(
                sourceId,
                entry.Table,
                "the collection was registered as an IReadOnlyCollection<T>, which only enumerates, "
                + "so there are no positions to append after (§1). Register an IReadOnlyList<T>, or "
                + "replace the table's rows instead of appending to them.");
        }

        if (entry.Kind is not (SourceRefreshKind.Replace or SourceRefreshKind.Append))
        {
            throw new SourceContractException(
                sourceId,
                entry.Table,
                $"{entry.Kind} is not something this table can do.");
        }
    }

    /// <inheritdoc />
    public override IPocoTableCommit PrepareRefresh(SourceRefreshEntry entry) =>
        new PreparedSnapshot(
            this,
            Carry(
                entry.Kind == SourceRefreshKind.Append
                    ? _factory.Appended(Current, (IReadOnlyList<T>)entry.Rows)
                    : _factory.From((IReadOnlyList<T>)entry.Rows),
                entry.Statistics));

    /// <summary>
    /// Hands the next snapshot whatever statistics the current one has in force, and says whether
    /// this operation deferred them (D271 (g), §7). A snapshot that carries nothing computes its own,
    /// which is what a table with no freshness policy and no deferred operation behind it always
    /// does.
    /// </summary>
    private PocoTableSnapshot<T> Carry(PocoTableSnapshot<T> next, StatisticsRefresh statistics)
    {
        var (carried, computedOver, wasDeferred) = Current.Carryable;
        if (statistics == StatisticsRefresh.Defer)
        {
            next.Carry(carried, computedOver, deferred: true);
            return next;
        }

        // Not deferred. A deferral ends here — this is either the plain refresh it was deferred to,
        // or an operation that asked for the recomputation outright — and so does a growth policy
        // whose statistics were carried past a deferral, because what stands behind them is older
        // than the policy thinks. A snapshot that carries nothing computes its own.
        if (wasDeferred || _statisticsPlan.RefreshWhenGrownBy <= 0)
        {
            return next;
        }

        next.Carry(carried, computedOver, deferred: false);
        return next;
    }

    /// <summary>The snapshot a transaction built for this table, waiting for the engine to say when.</summary>
    private sealed class PreparedSnapshot : IPocoTableCommit
    {
        private readonly PocoTableRuntime<T> _table;
        private readonly PocoTableSnapshot<T> _snapshot;

        public PreparedSnapshot(PocoTableRuntime<T> table, PocoTableSnapshot<T> snapshot)
        {
            _table = table;
            _snapshot = snapshot;
        }

        public void Commit() => _table.Publish(_snapshot);
    }

    /// <summary>
    /// Makes <paramref name="next"/> this table's snapshot and retires the one it replaces. The
    /// publish comes first: a reader that arrives between the two takes the new snapshot, and a
    /// reader already on the old one keeps it until it is done.
    /// </summary>
    public void Publish(PocoTableSnapshot<T> next)
    {
        next.OnReleased = Report;
        var previous = Interlocked.Exchange(ref _snapshot, next);
        previous.Retire();
    }

    private void Report(PocoTableSnapshot<T> snapshot)
    {
        if (snapshot.HostCollection is { } collection)
        {
            Released?.Invoke(Name, collection);
        }
    }

    private readonly TableEntitlementDescriptor? _entitlement;

    /// <summary>
    /// The snapshot this table is serving, with a reader's reference taken. Capture-and-count is one
    /// step on purpose: a reader that read the reference and counted afterwards could count a
    /// snapshot whose last reader had already left and whose collection the host was already reusing.
    /// </summary>
    public PocoTableSnapshot<T> Acquire()
    {
        while (true)
        {
            var snapshot = Volatile.Read(ref _snapshot);
            if (snapshot.TryAcquire())
            {
                return snapshot;
            }
        }
    }

    /// <summary>
    /// A reader's reference on the snapshot a scan captured when it was created, or — when that one
    /// has been replaced <em>and</em> released in the meantime, so nobody was reading it — on the
    /// table's current one.
    /// </summary>
    public PocoTableSnapshot<T> Lease(PocoTableSnapshot<T> captured) =>
        captured.TryAcquire() ? captured : Acquire();

    /// <summary>The snapshot this table is serving, uncounted. For a caller that only asks its shape.</summary>
    public PocoTableSnapshot<T> Current => Volatile.Read(ref _snapshot);

    public override string Name { get; }

    public override IReadOnlyList<PocoForeignKey> DeclaredForeignKeys { get; }

    public override int ColumnIndex(string nameOrMember)
    {
        for (var i = 0; i < _columns.Length; i++)
        {
            if (string.Equals(_columns[i].Name, nameOrMember, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        foreach (var pair in _byMember)
        {
            if (string.Equals(pair.Key, nameOrMember, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value;
            }
        }

        return -1;
    }

    public override void ResolveForeignKeys(IReadOnlyList<ForeignKeyDescriptor> keys) =>
        _foreignKeys = keys;

    public override Type RowType => typeof(T);

    public override HashSet<object?[]> KeySet(IReadOnlyList<int> columns)
    {
        var accessors = new Func<T, object?>[columns.Count];
        for (var i = 0; i < accessors.Length; i++)
        {
            accessors[i] = _columns[columns[i]].CompileLogicalAccessor();
        }

        var rows = Current.Values;
        var set = new HashSet<object?[]>(rows.Count, PocoVerification.PocoKeyComparer.Instance);
        foreach (var row in rows)
        {
            var key = new object?[accessors.Length];
            for (var i = 0; i < accessors.Length; i++)
            {
                key[i] = accessors[i](row);
            }

            set.Add(key);
        }

        return set;
    }

    public override void ProbeForeignKey(
        ForeignKeyDescriptor key, HashSet<object?[]> parentKeys, PocoTableRuntime parent)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(parentKeys);
        ArgumentNullException.ThrowIfNull(parent);

        var accessors = new Func<T, object?>[key.Columns.Count];
        for (var i = 0; i < accessors.Length; i++)
        {
            accessors[i] = _columns[key.Columns[i]].CompileLogicalAccessor();
        }

        var declaration = "foreign key ("
            + string.Join(", ", key.Columns.Select(c => _columns[c].Name))
            + $") references {parent.Name}";

        long index = 0;
        foreach (var row in Current.Values)
        {
            var values = new object?[accessors.Length];
            var complete = true;
            for (var i = 0; i < accessors.Length; i++)
            {
                values[i] = accessors[i](row);
                complete &= values[i] is not null;
            }

            if (complete && !parentKeys.Contains(values))
            {
                throw new CatalogVerificationException(
                    Name,
                    declaration,
                    index,
                    $"key ({string.Join(", ", values.Select(PocoVerification.Render))}) has no row in "
                    + $"'{parent.Name}'");
            }

            index++;
        }
    }

    public IReadOnlyList<PocoColumn<T>> Columns => _columns;

    /// <summary>
    /// The last scan's writer set, if it projected the same columns (ADR 0020 §1). Taken atomically,
    /// so two concurrent scans never share one: whoever loses the exchange builds its own set.
    /// </summary>
    public PocoChunkWriters<T>? TakeSpareWriters(IReadOnlyList<int> projection)
    {
        var spare = Interlocked.Exchange(ref _spareWriters, null);
        return spare is not null && spare.Projects(projection) ? spare : null;
    }

    /// <summary>Keeps a released writer set for the next scan of the same projection.</summary>
    public void ReturnSpareWriters(PocoChunkWriters<T> writers) => Volatile.Write(ref _spareWriters, writers);

    private PocoChunkWriters<T>? _spareWriters;

    /// <summary>
    /// The table as the catalog sees it, off one snapshot captured here and used for all of it: the
    /// row count, the statistics and the index descriptors are one consistent set rather than three
    /// readings of a table a refresh may be moving underneath (§1).
    /// </summary>
    public override TableDescriptor Describe()
    {
        var snapshot = Acquire();
        try
        {
            var statistics = snapshot.Statistics(_columns, _collations, _statisticsPlan);

            return new TableDescriptor
            {
                Name = Name,
                Columns = [.. _columns.Select((c, i) => new ColumnDescriptor
                {
                    Name = c.Name,
                    Type = c.Type,
                    Statistics = statistics[i],
                })],
                RowCount = snapshot.RowCount,
                RowCountKind = RowCountKind.Exact,
                UniqueKeys = _uniqueKeys,
                Collations = _collations,
                Indexes = [.. snapshot.Indexes.Select(i => i.Descriptor)],
                ForeignKeys = _foreignKeys,
                Entitlement = _entitlement,
            };
        }
        finally
        {
            snapshot.Release();
        }
    }

    public override IEnumerable<(string Index, long BytesPerRow)> IndexSizes() =>
        Current.Indexes.Select(i => (i.Descriptor.Name, i.BytesPerRow));

    public override object? FindIndex(string name) => Current.Indexes.FirstOrDefault(
        i => string.Equals(i.Descriptor.Name, name, StringComparison.OrdinalIgnoreCase));

    public override IAsyncEnumerable<RecordBatch> Scan(
        string sourceId, ScanRequest request, ScanContext context, CancellationToken ct)
    {
        CheckProjection(sourceId, request.Projection, request.OutputSchema);
        return new PocoScan<T>(
            this, Current, request.Projection, request.OutputSchema, request.BatchSize,
            request.RowGoal, context, ct);
    }

    public override IColumnarScan ColumnarScan(string sourceId, ScanRequest request, ScanContext context)
    {
        CheckProjection(sourceId, request.Projection, request.OutputSchema);
        return new PocoScan<T>(
            this, Current, request.Projection, request.OutputSchema, request.BatchSize,
            request.RowGoal, context, CancellationToken.None).Columnar();
    }

    public override IColumnarScan ColumnarLookup(
        string sourceId, IndexLookupRequest request, ScanContext context) =>
        LookupScan(sourceId, request, context, CancellationToken.None) switch
        {
            PocoClusteredScan<T> clustered => clustered.Columnar(),
            PocoIndexScan<T> positional => positional.Columnar(),
            var rows => ((PocoIndexRowScan<T>)rows).Columnar(),
        };

    public override IAsyncEnumerable<RecordBatch> Lookup(
        string sourceId, IndexLookupRequest request, ScanContext context, CancellationToken ct) =>
        LookupScan(sourceId, request, context, ct);

    private IAsyncEnumerable<RecordBatch> LookupScan(
        string sourceId, IndexLookupRequest request, ScanContext context, CancellationToken ct)
    {
        CheckProjection(sourceId, request.Projection, request.OutputSchema);

        // One capture, used for all of it (§1). The index is then named by its ordinal rather than
        // by its object, because the declaration order is shape: index i of any snapshot of this
        // table is the same declared index, built over that snapshot's rows.
        var snapshot = Current;
        var indexes = snapshot.Indexes;
        var ordinal = -1;
        for (var i = 0; i < indexes.Length; i++)
        {
            if (string.Equals(indexes[i].Descriptor.Name, request.Index, StringComparison.OrdinalIgnoreCase))
            {
                ordinal = i;
                break;
            }
        }

        if (ordinal < 0)
        {
            throw new SourceContractException(
                sourceId,
                Name,
                $"there is no index named '{request.Index}' on this table. Declared: "
                + (indexes.Length == 0 ? "(none)" : string.Join(", ", indexes.Select(i => i.Descriptor.Name)))
                + ".");
        }

        var index = indexes[ordinal];

        foreach (var range in request.Ranges)
        {
            if (range.BoundedColumns > index.Descriptor.Columns.Count)
            {
                throw new SourceContractException(
                    sourceId,
                    Name,
                    $"a range bounds {range.BoundedColumns} key column(s) but index "
                    + $"'{index.Descriptor.Name}' has {index.Descriptor.Columns.Count}.");
            }

            if (index.Descriptor.Kind == Chalk.Ir.IndexKind.Hash && !IsEquality(range))
            {
                throw new SourceContractException(
                    sourceId,
                    Name,
                    $"index '{index.Descriptor.Name}' is a hash index and answers equality only, but "
                    + $"the plan asked for the range {range}.");
            }

            // D282: a prefix range belongs to a PREFIX index and nowhere else. Every other kind is
            // sent the plain half-open range the prefix stands for, resolved before the source sees
            // it, and a PREFIX index is sent nothing but prefixes.
            if (index.Descriptor.Kind == Chalk.Ir.IndexKind.Prefix && range.Prefix is null)
            {
                throw new SourceContractException(
                    sourceId,
                    Name,
                    $"index '{index.Descriptor.Name}' is a prefix index and answers prefix lookups "
                    + $"only, but the plan asked for the range {range}.");
            }

            if (index.Descriptor.Kind != Chalk.Ir.IndexKind.Prefix && range.Prefix is not null)
            {
                throw new SourceContractException(
                    sourceId,
                    Name,
                    $"index '{index.Descriptor.Name}' is {index.Descriptor.Kind} and was asked for "
                    + $"the prefix range {range}; only a PREFIX index answers one.");
            }
        }

        if (index is IPositionalPocoIndex<T> && snapshot.RandomAccess)
        {
            // D257: a clustered index whose copy carries every projected column answers in slices of
            // that copy — no gather, nothing per row. A projection that reaches outside the covering
            // set takes the positional path for every column, as it does for a permutation index;
            // the mixed case is not built.
            if (index is ClusteredIndex<T> clustered && clustered.Covers(request.Projection))
            {
                return new PocoClusteredScan<T>(
                    this,
                    snapshot,
                    ordinal,
                    new PocoIndexScan<T>(this, snapshot, ordinal, request, context, ct),
                    request,
                    context);
            }

            return new PocoIndexScan<T>(this, snapshot, ordinal, request, context, ct);
        }

        return new PocoIndexRowScan<T>(this, snapshot, ordinal, request, context, ct);
    }

    private static bool IsEquality(IndexKeyRange range) =>
        range.LowerInclusive
        && range.UpperInclusive
        && range.Lower.Count == range.Upper.Count
        && range.Lower.Count > 0
        && range.Lower.Zip(range.Upper).All(pair => Equals(pair.First, pair.Second));

    private void CheckProjection(string sourceId, IReadOnlyList<int> projection, ArrowSchema outputSchema)
    {
        for (var i = 0; i < projection.Count; i++)
        {
            if (projection[i] < 0 || projection[i] >= _columns.Length)
            {
                throw new SourceContractException(
                    sourceId,
                    Name,
                    $"projection[{i}] is column {projection[i]}, but the table has {_columns.Length} columns.");
            }
        }

        // Field by field rather than schema against schema: this runs once per scan, so building the
        // schema the projection would produce — and printing every type to compare it — was 1 672 of
        // the bytes a small query's fixed cost was made of (ADR 0020 §1). The mismatch message still
        // builds both, on the path that is about to throw.
        var fields = outputSchema.FieldsList;
        var matches = fields.Count == projection.Count;
        for (var i = 0; matches && i < projection.Count; i++)
        {
            matches = ArrowTypeMapping.AreEquivalent(_columns[projection[i]].Field, fields[i]);
        }

        if (!matches)
        {
            var expected = new ArrowSchema(projection.Select(i => _columns[i].Field), metadata: null);
            throw new SourceContractException(
                sourceId,
                Name,
                $"the requested output schema {ArrowTypeMapping.DescribeArrow(outputSchema)} is not the one "
                + $"this projection produces, {ArrowTypeMapping.DescribeArrow(expected)}.");
        }
    }
}

/// <summary>
/// The per-batch state every POCO scan shares: one chunk writer per projected column, rented from
/// the execution's arena and returned when the scan ends (ADR 0012).
/// </summary>
internal sealed class PocoChunkWriters<T> : IDisposable
{
    private readonly PocoTableRuntime<T> _table;
    private readonly int[] _projection;
    private readonly PocoChunkWriter<T>[] _writers;
    private ArrowSchema _schema;
    private bool _disposed;

    private PocoChunkWriters(PocoTableRuntime<T> table, IReadOnlyList<int> projection, ArrowSchema schema)
    {
        _table = table;
        _schema = schema;
        _projection = [.. projection];
        _writers = new PocoChunkWriter<T>[projection.Count];
        for (var i = 0; i < _writers.Length; i++)
        {
            _writers[i] = table.Columns[_projection[i]].CreateWriter();
        }
    }

    /// <summary>
    /// One scan's writers, from the table's pool when the last scan projected the same columns
    /// (ADR 0020 §1). The staging is always re-rented from <paramref name="arena"/>: only the
    /// objects are reused, which is the same bargain the engine's own scratch strikes (D64).
    /// </summary>
    public static PocoChunkWriters<T> Acquire(
        PocoTableRuntime<T> table, IReadOnlyList<int> projection, ArrowSchema schema, ExecutionArena arena, int capacity)
    {
        var writers = table.TakeSpareWriters(projection) ?? new PocoChunkWriters<T>(table, projection, schema);
        writers._schema = schema;
        writers._disposed = false;
        foreach (var writer in writers._writers)
        {
            writer.Acquire(arena, capacity);
        }

        return writers;
    }

    /// <summary>Whether this set was built for exactly <paramref name="projection"/>.</summary>
    public bool Projects(IReadOnlyList<int> projection)
    {
        if (projection.Count != _projection.Length)
        {
            return false;
        }

        for (var i = 0; i < _projection.Length; i++)
        {
            if (_projection[i] != projection[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A batch of consecutive rows.</summary>
    public RecordBatch Write(IReadOnlyList<T> rows, int start, int count)
    {
        // A fresh array per batch: the batch we hand over keeps this reference, and reusing it
        // would rewrite a batch the consumer still owns (§4). One array per batch, not per row.
        var arrays = new IArrowArray[_writers.Length];
        for (var i = 0; i < _writers.Length; i++)
        {
            arrays[i] = _writers[i].Write(rows, start, count);
            Check(arrays[i], i);
        }

        return new RecordBatch(_schema, arrays, count);
    }

    /// <summary>
    /// The same batch as column views into the writers' own staging — the columnar fast path of
    /// <c>15-zero-allocation-execution.md</c> §1. Nothing is copied and no Arrow object is made; the
    /// views are valid until the next batch, which is the contract the caller's slot already has.
    /// </summary>
    public void WriteView(ColumnarBatch slot, IReadOnlyList<T> rows, int start, int count)
    {
        slot.Begin(count);
        for (var i = 0; i < _writers.Length; i++)
        {
            slot.Set(i, _writers[i].WriteView(rows, start, count));
        }
    }

    /// <summary>The gather form of <see cref="WriteView(ColumnarBatch, IReadOnlyList{T}, int, int)"/>.</summary>
    public void WriteView(ColumnarBatch slot, IReadOnlyList<T> rows, int[] positions, int start, int count)
    {
        slot.Begin(count);
        for (var i = 0; i < _writers.Length; i++)
        {
            slot.Set(i, _writers[i].WriteView(rows, positions, start, count));
        }
    }

    /// <summary>A batch gathered through a position array.</summary>
    public RecordBatch Write(IReadOnlyList<T> rows, int[] positions, int start, int count)
    {
        var arrays = new IArrowArray[_writers.Length];
        for (var i = 0; i < _writers.Length; i++)
        {
            arrays[i] = _writers[i].Write(rows, positions, start, count);
            Check(arrays[i], i);
        }

        return new RecordBatch(_schema, arrays, count);
    }

    /// <summary>Returns the staging to the arena and this set of writers to the table's pool.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var writer in _writers)
        {
            writer.Release();
        }

        _table.ReturnSpareWriters(this);
    }

    [Conditional("DEBUG")]
    private void Check(IArrowArray array, int column) =>
        Debug.Assert(
            ArrowTypeMapping.DescribeArrow(array.Data.DataType)
                == ArrowTypeMapping.DescribeArrow(_schema.FieldsList[column].DataType),
            "a chunk writer produced an array of the wrong Arrow type");
}

/// <summary>
/// A scan in progress. Written by hand rather than as an <c>async</c> iterator because nothing here
/// awaits: the enumerator holds one chunk writer per projected column — whose staging it rents from
/// the scan's arena and returns when the scan ends — and hands each batch back synchronously, with no
/// state machine and no allocation per row (§5.3, ADR 0012).
/// </summary>
internal sealed class PocoScan<T> : IAsyncEnumerable<RecordBatch>
{
    private readonly PocoTableRuntime<T> _table;
    private readonly PocoTableSnapshot<T> _snapshot;
    private readonly IReadOnlyList<int> _projection;
    private readonly ArrowSchema _schema;
    private readonly int _batchSize;
    private readonly long? _rowGoal;
    private readonly ScanContext _context;
    private readonly CancellationToken _ct;

    public PocoScan(
        PocoTableRuntime<T> table,
        PocoTableSnapshot<T> snapshot,
        IReadOnlyList<int> projection,
        ArrowSchema schema,
        int batchSize,
        long? rowGoal,
        ScanContext context,
        CancellationToken ct)
    {
        _table = table;
        _snapshot = snapshot;
        _projection = projection;
        _schema = schema;
        _batchSize = batchSize;
        _rowGoal = rowGoal;
        _context = context;
        _ct = ct;
    }

    public IAsyncEnumerator<RecordBatch> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
        _snapshot.RandomAccess
            ? new ListEnumerator(
                _table, _snapshot, _projection, _schema, _batchSize, _rowGoal, _context, _ct, cancellationToken)
            : new CollectionEnumerator(
                _table, _snapshot, _projection, _schema, _batchSize, _rowGoal, _context, _ct, cancellationToken);

    /// <summary>The same scan as an <see cref="IColumnarScan"/>, which fills the caller's slot (§1).</summary>
    public IColumnarScan Columnar() =>
        _snapshot.RandomAccess
            ? new ListEnumerator(
                _table, _snapshot, _projection, _schema, _batchSize, _rowGoal, _context, _ct,
                CancellationToken.None)
            : new CollectionEnumerator(
                _table, _snapshot, _projection, _schema, _batchSize, _rowGoal, _context, _ct,
                CancellationToken.None);

    /// <summary>The list path: batches are windows into the collection, read in place.</summary>
    private sealed class ListEnumerator : IAsyncEnumerator<RecordBatch>, IColumnarScan
    {
        private readonly ExecutionStats _stats;
        private readonly CancellationToken _scanToken;
        private readonly CancellationToken _enumerationToken;
        private readonly PocoTableSnapshot<T> _snapshot;
        private readonly IReadOnlyList<T> _rows;
        private readonly PocoChunkWriters<T> _writers;
        private readonly int _batchSize;
        private readonly long? _rowGoal;
        private readonly int _rowCount;
        private int _position;
        private int _previousBatch;
        private bool _finished;

        public ListEnumerator(
            PocoTableRuntime<T> table,
            PocoTableSnapshot<T> captured,
            IReadOnlyList<int> projection,
            ArrowSchema schema,
            int batchSize,
            long? rowGoal,
            ScanContext context,
            CancellationToken scanToken,
            CancellationToken enumerationToken)
        {
            _stats = context.Stats;
            _scanToken = scanToken;
            _enumerationToken = enumerationToken;
            _batchSize = batchSize;
            _rowGoal = rowGoal;

            // No lock and no copy: this snapshot's rows are immutable for as long as this reference
            // is counted, so the batching walks a collection nothing can change under it (D260 §1).
            // The row count is the snapshot's rather than the collection's, which is what lets an
            // append share a buffer with the snapshot before it and still leave this scan its prefix.
            _snapshot = table.Lease(captured);
            _rows = _snapshot.Rows!;
            _rowCount = _snapshot.RowCount;

            try
            {
                _writers = PocoChunkWriters<T>.Acquire(
                    table, projection, schema, context.Arena, Math.Max(1, Math.Min(batchSize, _rowCount)));
            }
            catch
            {
                _snapshot.Release();
                throw;
            }

            Current = null!;
        }

        public RecordBatch Current { get; private set; }

        public ValueTask<bool> MoveNextAsync()
        {
            _scanToken.ThrowIfCancellationRequested();
            _enumerationToken.ThrowIfCancellationRequested();

            if (_finished || _position >= _rowCount)
            {
                Current = null!;
                Release();
                return ValueTask.FromResult(false);
            }

            var count = Math.Min(NextBatch(), _rowCount - _position);
            Current = _writers.Write(_rows, _position, count);
            _position += count;
            _stats.AddRowsScanned(count);
            return ValueTask.FromResult(true);
        }

        public bool TryNext(ColumnarBatch batch)
        {
            _scanToken.ThrowIfCancellationRequested();
            _enumerationToken.ThrowIfCancellationRequested();

            if (_finished || _position >= _rowCount)
            {
                Release();
                return false;
            }

            var count = Math.Min(NextBatch(), _rowCount - _position);
            _writers.WriteView(batch, _rows, _position, count);
            _position += count;
            _stats.AddRowsScanned(count);
            return true;
        }

        /// <summary>
        /// The ramp of <see cref="BatchRamp"/>: the batch size, unless the plan stated a row goal,
        /// in which case the first batch is about the goal and each one after it doubles.
        /// </summary>
        private int NextBatch()
        {
            _previousBatch = BatchRamp.Next(_previousBatch, _batchSize, _rowGoal);
            return _previousBatch;
        }

        /// <summary>An in-process collection is always ready, so this only ever reports the end.</summary>
        public ValueTask<bool> WaitAsync(ColumnarBatch batch, CancellationToken ct) =>
            ValueTask.FromResult(TryNext(batch));

        public ValueTask DisposeAsync()
        {
            Current = null!;
            Release();
            return default;
        }

        private void Release()
        {
            if (_finished)
            {
                return;
            }

            _finished = true;
            _writers.Dispose();

            // Last, and after everything of this scan's own is back: a host listening for the
            // release is being told it may reuse the rows, and it may only be told that once
            // nothing here is reading them.
            _snapshot.Release();
        }
    }

    /// <summary>
    /// The enumerate-only path (§1): rows are staged into a pooled <c>T[]</c> of one batch and the
    /// same compiled chunk writers extract from that. One array per scan, not per row.
    /// </summary>
    private sealed class CollectionEnumerator : IAsyncEnumerator<RecordBatch>, IColumnarScan
    {
        private readonly ExecutionStats _stats;
        private readonly CancellationToken _scanToken;
        private readonly CancellationToken _enumerationToken;
        private readonly PocoTableSnapshot<T> _snapshot;
        private readonly IEnumerator<T> _source;
        private readonly PocoChunkWriters<T> _writers;
        private readonly int _batchSize;
        private readonly long? _rowGoal;
        private T[] _staging;
        private int _previousBatch;
        private bool _finished;

        public CollectionEnumerator(
            PocoTableRuntime<T> table,
            PocoTableSnapshot<T> captured,
            IReadOnlyList<int> projection,
            ArrowSchema schema,
            int batchSize,
            long? rowGoal,
            ScanContext context,
            CancellationToken scanToken,
            CancellationToken enumerationToken)
        {
            _stats = context.Stats;
            _scanToken = scanToken;
            _enumerationToken = enumerationToken;
            _batchSize = batchSize;
            _rowGoal = rowGoal;
            _snapshot = table.Lease(captured);
            _source = _snapshot.Values.GetEnumerator();

            var capacity = Math.Max(1, Math.Min(batchSize, Math.Max(_snapshot.RowCount, 1)));

            // The arena rents unmanaged arrays only; a chunk of POCOs comes from the shared pool and
            // goes back cleared, so the scan retains no rows once it ends.
            _staging = ArrayPool<T>.Shared.Rent(capacity);
            try
            {
                _writers = PocoChunkWriters<T>.Acquire(table, projection, schema, context.Arena, capacity);
            }
            catch
            {
                ArrayPool<T>.Shared.Return(_staging, clearArray: true);
                _source.Dispose();
                _snapshot.Release();
                throw;
            }

            Current = null!;
        }

        public RecordBatch Current { get; private set; }

        public ValueTask<bool> MoveNextAsync()
        {
            _scanToken.ThrowIfCancellationRequested();
            _enumerationToken.ThrowIfCancellationRequested();

            if (_finished)
            {
                Current = null!;
                return ValueTask.FromResult(false);
            }

            var count = 0;
            var wanted = Math.Min(NextBatch(), _staging.Length);
            while (count < wanted && _source.MoveNext())
            {
                _staging[count++] = _source.Current;
            }

            if (count == 0)
            {
                Current = null!;
                Release();
                return ValueTask.FromResult(false);
            }

            Current = _writers.Write(_staging, 0, count);
            _stats.AddRowsScanned(count);
            return ValueTask.FromResult(true);
        }

        public bool TryNext(ColumnarBatch batch)
        {
            _scanToken.ThrowIfCancellationRequested();
            _enumerationToken.ThrowIfCancellationRequested();

            if (_finished)
            {
                return false;
            }

            var count = 0;
            var wanted = Math.Min(NextBatch(), _staging.Length);
            while (count < wanted && _source.MoveNext())
            {
                _staging[count++] = _source.Current;
            }

            if (count == 0)
            {
                Release();
                return false;
            }

            _writers.WriteView(batch, _staging, 0, count);
            _stats.AddRowsScanned(count);
            return true;
        }

        public ValueTask<bool> WaitAsync(ColumnarBatch batch, CancellationToken ct) =>
            ValueTask.FromResult(TryNext(batch));

        public ValueTask DisposeAsync()
        {
            Current = null!;
            Release();
            return default;
        }

        /// <summary>The ramp of <see cref="BatchRamp"/>, as the staging path takes it.</summary>
        private int NextBatch()
        {
            _previousBatch = BatchRamp.Next(_previousBatch, _batchSize, _rowGoal);
            return _previousBatch;
        }

        private void Release()
        {
            if (_finished)
            {
                return;
            }

            _finished = true;
            _source.Dispose();
            _writers.Dispose();
            ArrayPool<T>.Shared.Return(_staging, clearArray: true);
            _staging = [];
            _snapshot.Release();
        }
    }
}

/// <summary>
/// An index lookup over a positional index: the ranges are turned into position runs, and the same
/// compiled chunk writers gather straight out of the collection (§5). <c>RowsScanned</c> counts the
/// rows the lookup actually read, which is what makes the instrumentation assertions meaningful.
/// </summary>
internal sealed class PocoIndexScan<T> : IAsyncEnumerable<RecordBatch>
{
    private readonly PocoTableRuntime<T> _table;
    private readonly PocoTableSnapshot<T> _snapshot;
    private readonly int _ordinal;
    private readonly IndexLookupRequest _request;
    private readonly ScanContext _context;
    private readonly CancellationToken _ct;

    public PocoIndexScan(
        PocoTableRuntime<T> table,
        PocoTableSnapshot<T> snapshot,
        int ordinal,
        IndexLookupRequest request,
        ScanContext context,
        CancellationToken ct)
    {
        _table = table;
        _snapshot = snapshot;
        _ordinal = ordinal;
        _request = request;
        _context = context;
        _ct = ct;
    }

    public IAsyncEnumerator<RecordBatch> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
        new Enumerator(_table, _snapshot, _ordinal, _request, _context, _ct, cancellationToken);

    /// <summary>The same lookup as an <see cref="IColumnarScan"/>, which fills the caller's slot (§1).</summary>
    public IColumnarScan Columnar() =>
        new Enumerator(_table, _snapshot, _ordinal, _request, _context, _ct, CancellationToken.None);

    private sealed class Enumerator : IAsyncEnumerator<RecordBatch>, IColumnarScan
    {
        private readonly ExecutionStats _stats;
        private readonly CancellationToken _scanToken;
        private readonly CancellationToken _enumerationToken;
        private readonly PocoTableSnapshot<T> _snapshot;
        private readonly IReadOnlyList<T> _rows;
        private readonly PocoChunkWriters<T> _writers;
        private readonly IEnumerator<int> _positions;
        private readonly ExecutionArena _arena;
        private readonly int _batchSize;
        private readonly long? _rowGoal;
        private int[] _buffer;
        private int _previousBatch;
        private bool _drained;
        private bool _finished;

        public Enumerator(
            PocoTableRuntime<T> table,
            PocoTableSnapshot<T> captured,
            int ordinal,
            IndexLookupRequest request,
            ScanContext context,
            CancellationToken scanToken,
            CancellationToken enumerationToken)
        {
            _stats = context.Stats;
            _scanToken = scanToken;
            _enumerationToken = enumerationToken;
            _snapshot = table.Lease(captured);
            _rows = _snapshot.Rows!;
            _arena = context.Arena;
            _batchSize = request.BatchSize;
            _rowGoal = request.RowGoal;
            _positions = Positions((IPositionalPocoIndex<T>)_snapshot.Indexes[ordinal], request.Ranges)
                .GetEnumerator();
            _buffer = context.Arena.Rent<int>(Math.Max(1, request.BatchSize));
            try
            {
                _writers = PocoChunkWriters<T>.Acquire(
                    table, request.Projection, request.OutputSchema, context.Arena, Math.Max(1, request.BatchSize));
            }
            catch
            {
                _arena.Return(_buffer);
                _positions.Dispose();
                _snapshot.Release();
                throw;
            }

            Current = null!;
        }

        public RecordBatch Current { get; private set; }

        public ValueTask<bool> MoveNextAsync()
        {
            _scanToken.ThrowIfCancellationRequested();
            _enumerationToken.ThrowIfCancellationRequested();

            if (_finished || _drained)
            {
                Current = null!;
                Release();
                return ValueTask.FromResult(false);
            }

            var count = 0;
            var wanted = Math.Min(NextBatch(), _buffer.Length);
            while (count < wanted && _positions.MoveNext())
            {
                _buffer[count++] = _positions.Current;
            }

            if (count < wanted)
            {
                _drained = true;
            }

            if (count == 0)
            {
                Current = null!;
                Release();
                return ValueTask.FromResult(false);
            }

            Current = _writers.Write(_rows, _buffer, 0, count);
            _stats.AddRowsScanned(count);
            return ValueTask.FromResult(true);
        }

        public bool TryNext(ColumnarBatch batch)
        {
            _scanToken.ThrowIfCancellationRequested();
            _enumerationToken.ThrowIfCancellationRequested();

            if (_finished || _drained)
            {
                Release();
                return false;
            }

            var count = 0;
            var wanted = Math.Min(NextBatch(), _buffer.Length);
            while (count < wanted && _positions.MoveNext())
            {
                _buffer[count++] = _positions.Current;
            }

            if (count < wanted)
            {
                _drained = true;
            }

            if (count == 0)
            {
                Release();
                return false;
            }

            _writers.WriteView(batch, _rows, _buffer, 0, count);
            _stats.AddRowsScanned(count);
            return true;
        }

        public ValueTask<bool> WaitAsync(ColumnarBatch batch, CancellationToken ct) =>
            ValueTask.FromResult(TryNext(batch));

        public ValueTask DisposeAsync()
        {
            Current = null!;
            Release();
            return default;
        }

        /// <summary>The ramp of <see cref="BatchRamp"/>, as the positional lookup takes it.</summary>
        private int NextBatch()
        {
            _previousBatch = BatchRamp.Next(_previousBatch, _batchSize, _rowGoal);
            return _previousBatch;
        }

        private void Release()
        {
            if (_finished)
            {
                return;
            }

            _finished = true;
            _positions.Dispose();
            _writers.Dispose();
            _arena.Return(_buffer);
            _buffer = [];
            _snapshot.Release();
        }
    }

    /// <summary>
    /// The positions the ranges match, de-duplicated once there is more than one range. The planner
    /// emits sorted, non-overlapping ranges; the bitmap is the cheap insurance §3 asks for, and it
    /// keeps the union in index order.
    /// </summary>
    private static IEnumerable<int> Positions(
        IPositionalPocoIndex<T> index,
        IReadOnlyList<IndexKeyRange> ranges)
    {
        if (ranges.Count == 1)
        {
            var range = ranges[0];

            index.GetWindow(range, out var from, out var to);

            for (var ordinal = from; ordinal < to; ordinal++)
            {
                yield return index.GetPosition(ordinal);
            }

            yield break;
        }

        var seen = new HashSet<int>();

        for (var r = 0; r < ranges.Count; r++)
        {
            index.GetWindow(ranges[r], out var from, out var to);

            for (var ordinal = from; ordinal < to; ordinal++)
            {
                var position = index.GetPosition(ordinal);

                if (seen.Add(position))
                {
                    yield return position;
                }
            }
        }
    }
}

/// <summary>
/// An index lookup over an index that yields rows rather than positions — a host-supplied structure,
/// or a collection that only enumerates. Rows are staged a batch at a time, exactly as the
/// enumerate-only scan does.
/// </summary>
internal sealed class PocoIndexRowScan<T> : IAsyncEnumerable<RecordBatch>
{
    private readonly PocoTableRuntime<T> _table;
    private readonly PocoTableSnapshot<T> _snapshot;
    private readonly int _ordinal;
    private readonly IndexLookupRequest _request;
    private readonly ScanContext _context;
    private readonly CancellationToken _ct;

    public PocoIndexRowScan(
        PocoTableRuntime<T> table,
        PocoTableSnapshot<T> snapshot,
        int ordinal,
        IndexLookupRequest request,
        ScanContext context,
        CancellationToken ct)
    {
        _table = table;
        _snapshot = snapshot;
        _ordinal = ordinal;
        _request = request;
        _context = context;
        _ct = ct;
    }

    public IAsyncEnumerator<RecordBatch> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
        new Enumerator(_table, _snapshot, _ordinal, _request, _context, _ct, cancellationToken);

    /// <summary>The same lookup as an <see cref="IColumnarScan"/>, which fills the caller's slot (§1).</summary>
    public IColumnarScan Columnar() =>
        new Enumerator(_table, _snapshot, _ordinal, _request, _context, _ct, CancellationToken.None);

    private sealed class Enumerator : IAsyncEnumerator<RecordBatch>, IColumnarScan
    {
        private readonly ExecutionStats _stats;
        private readonly CancellationToken _scanToken;
        private readonly CancellationToken _enumerationToken;
        private readonly PocoTableSnapshot<T> _snapshot;
        private readonly PocoChunkWriters<T> _writers;
        private readonly IEnumerator<T> _source;
        private readonly int _batchSize;
        private readonly long? _rowGoal;
        private T[] _staging;
        private int _previousBatch;
        private bool _finished;

        public Enumerator(
            PocoTableRuntime<T> table,
            PocoTableSnapshot<T> captured,
            int ordinal,
            IndexLookupRequest request,
            ScanContext context,
            CancellationToken scanToken,
            CancellationToken enumerationToken)
        {
            _stats = context.Stats;
            _scanToken = scanToken;
            _enumerationToken = enumerationToken;
            _batchSize = request.BatchSize;
            _rowGoal = request.RowGoal;
            _snapshot = table.Lease(captured);
            _source = Rows(_snapshot.Indexes[ordinal], request.Ranges).GetEnumerator();
            _staging = ArrayPool<T>.Shared.Rent(Math.Max(1, request.BatchSize));
            try
            {
                _writers = PocoChunkWriters<T>.Acquire(
                    table, request.Projection, request.OutputSchema, context.Arena, Math.Max(1, request.BatchSize));
            }
            catch
            {
                ArrayPool<T>.Shared.Return(_staging, clearArray: true);
                _source.Dispose();
                _snapshot.Release();
                throw;
            }

            Current = null!;
        }

        public RecordBatch Current { get; private set; }

        public ValueTask<bool> MoveNextAsync()
        {
            _scanToken.ThrowIfCancellationRequested();
            _enumerationToken.ThrowIfCancellationRequested();

            if (_finished)
            {
                Current = null!;
                return ValueTask.FromResult(false);
            }

            var count = 0;
            var wanted = Math.Min(NextBatch(), _staging.Length);
            while (count < wanted && _source.MoveNext())
            {
                _staging[count++] = _source.Current;
            }

            if (count == 0)
            {
                Current = null!;
                Release();
                return ValueTask.FromResult(false);
            }

            Current = _writers.Write(_staging, 0, count);
            _stats.AddRowsScanned(count);
            return ValueTask.FromResult(true);
        }

        public bool TryNext(ColumnarBatch batch)
        {
            _scanToken.ThrowIfCancellationRequested();
            _enumerationToken.ThrowIfCancellationRequested();

            if (_finished)
            {
                return false;
            }

            var count = 0;
            var wanted = Math.Min(NextBatch(), _staging.Length);
            while (count < wanted && _source.MoveNext())
            {
                _staging[count++] = _source.Current;
            }

            if (count == 0)
            {
                Release();
                return false;
            }

            _writers.WriteView(batch, _staging, 0, count);
            _stats.AddRowsScanned(count);
            return true;
        }

        public ValueTask<bool> WaitAsync(ColumnarBatch batch, CancellationToken ct) =>
            ValueTask.FromResult(TryNext(batch));

        public ValueTask DisposeAsync()
        {
            Current = null!;
            Release();
            return default;
        }

        /// <summary>The ramp of <see cref="BatchRamp"/>, as the row-yielding lookup takes it.</summary>
        private int NextBatch()
        {
            _previousBatch = BatchRamp.Next(_previousBatch, _batchSize, _rowGoal);
            return _previousBatch;
        }

        private void Release()
        {
            if (_finished)
            {
                return;
            }

            _finished = true;
            _source.Dispose();
            _writers.Dispose();
            ArrayPool<T>.Shared.Return(_staging, clearArray: true);
            _staging = [];
            _snapshot.Release();
        }
    }

    /// <summary>
    /// The rows the ranges match. With more than one range the union is de-duplicated by reference
    /// where the row type allows it, and structurally otherwise — a value-typed row has no identity,
    /// so the planner's promise of non-overlapping ranges is what actually keeps this honest.
    /// </summary>
    private static IEnumerable<T> Rows(IPocoIndex<T> index, IReadOnlyList<IndexKeyRange> ranges)
    {
        if (ranges.Count == 1)
        {
            return index.Lookup(ranges[0]);
        }

        return Deduplicated(index, ranges);
    }

    private static IEnumerable<T> Deduplicated(IPocoIndex<T> index, IReadOnlyList<IndexKeyRange> ranges)
    {
        var seen = new HashSet<T>();
        foreach (var range in ranges)
        {
            foreach (var row in index.Lookup(range))
            {
                if (seen.Add(row))
                {
                    yield return row;
                }
            }
        }
    }
}

/// <summary>
/// A lookup on a clustered index whose copy carries every projected column (D257,
/// <c>docs/design/34-clustered-indexes.md</c> §4): each batch is a <em>slice</em> of the copy's
/// columns over the window the range resolves to. Nothing is gathered, nothing is encoded and
/// nothing at all is allocated per row — the copy was extracted once, at <c>Build()</c>, and the
/// views a batch carries borrow it.
/// </summary>
/// <remarks>
/// The Arrow form of the same lookup — <c>IndexLookupAsync</c>'s <see cref="RecordBatch"/> stream —
/// materialises, because an Arrow array is a materialisation by definition; it keeps the positional
/// gather it has always had and produces exactly the same rows. The slice path is the columnar one,
/// which is the publish the base table's own columns use and the one the engine takes (ADR 0036).
/// </remarks>
internal sealed class PocoClusteredScan<T> : IAsyncEnumerable<RecordBatch>
{
    private readonly PocoTableRuntime<T> _table;
    private readonly PocoTableSnapshot<T> _snapshot;
    private readonly int _ordinal;
    private readonly PocoIndexScan<T> _materialising;
    private readonly IndexLookupRequest _request;
    private readonly ScanContext _context;

    public PocoClusteredScan(
        PocoTableRuntime<T> table,
        PocoTableSnapshot<T> snapshot,
        int ordinal,
        PocoIndexScan<T> materialising,
        IndexLookupRequest request,
        ScanContext context)
    {
        _table = table;
        _snapshot = snapshot;
        _ordinal = ordinal;
        _materialising = materialising;
        _request = request;
        _context = context;
    }

    public IAsyncEnumerator<RecordBatch> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
        _materialising.GetAsyncEnumerator(cancellationToken);

    /// <summary>The lookup as slices of the copy, filling the caller's slot (§1, §4).</summary>
    public IColumnarScan Columnar() => new Enumerator(_table, _snapshot, _ordinal, _request, _context);

    private sealed class Enumerator : IAsyncEnumerator<RecordBatch>, IColumnarScan
    {
        private readonly ExecutionStats _stats;
        private readonly PocoTableSnapshot<T> _snapshot;
        private readonly ClusteredIndex<T> _index;
        private readonly int[] _projection;
        private readonly int[] _windows;
        private readonly int _batchSize;
        private readonly long? _rowGoal;
        private int _window;
        private int _position;
        private int _previousBatch;
        private bool _finished;

        public Enumerator(
            PocoTableRuntime<T> table,
            PocoTableSnapshot<T> captured,
            int ordinal,
            IndexLookupRequest request,
            ScanContext context)
        {
            _stats = context.Stats;
            _snapshot = table.Lease(captured);
            _index = (ClusteredIndex<T>)_snapshot.Indexes[ordinal];
            _batchSize = request.BatchSize;
            _rowGoal = request.RowGoal;
            _projection = [.. request.Projection];
            _windows = Windows(_index, request.Ranges);
            _position = _windows.Length == 0 ? 0 : _windows[0];
            Current = null!;
        }

        public RecordBatch Current { get; private set; }

        /// <summary>
        /// The slice path fills a slot; a caller that wants Arrow objects goes through the
        /// materialising twin. Both are reached from the same scan, so this is unreachable rather
        /// than unimplemented.
        /// </summary>
        public ValueTask<bool> MoveNextAsync() =>
            throw new NotSupportedException(
                "a clustered-copy scan produces column views, not Arrow batches; enumerate the "
                + "lookup through IndexLookupAsync for those.");

        public bool TryNext(ColumnarBatch batch)
        {
            ArgumentNullException.ThrowIfNull(batch);

            while (_window < _windows.Length / 2 && _position >= _windows[(_window * 2) + 1])
            {
                _window++;
                if (_window < _windows.Length / 2)
                {
                    _position = _windows[_window * 2];
                }
            }

            if (_window >= _windows.Length / 2)
            {
                return false;
            }

            var end = _windows[(_window * 2) + 1];
            _previousBatch = BatchRamp.Next(_previousBatch, _batchSize, _rowGoal);
            var count = Math.Min(_previousBatch, end - _position);

            batch.Begin(count);
            for (var i = 0; i < _projection.Length; i++)
            {
                batch.Set(i, _index.Slice(_projection[i], _position, count));
            }

            _position += count;
            _stats.AddRowsScanned(count);
            return true;
        }

        /// <summary>An in-process copy is always ready, so this only ever reports the end.</summary>
        public ValueTask<bool> WaitAsync(ColumnarBatch batch, CancellationToken ct) =>
            ValueTask.FromResult(TryNext(batch));

        public ValueTask DisposeAsync()
        {
            Current = null!;
            if (!_finished)
            {
                _finished = true;
                _snapshot.Release();
            }

            return default;
        }

        /// <summary>
        /// The ranges as half-open ordinal windows of the copy, flattened into (from, to) pairs.
        /// The planner emits sorted, non-overlapping ranges; clipping each window to where the last
        /// one ended is the same insurance the positional path's bitmap is, done on intervals — a
        /// row is never emitted twice, and what comes out is in the index's key order, which is what
        /// the IR node promises.
        /// </summary>
        private static int[] Windows(ClusteredIndex<T> index, IReadOnlyList<IndexKeyRange> ranges)
        {
            var windows = new int[ranges.Count * 2];
            var count = 0;
            var emitted = 0;

            for (var r = 0; r < ranges.Count; r++)
            {
                index.GetWindow(ranges[r], out var from, out var to);
                from = Math.Max(from, emitted);
                if (from >= to)
                {
                    continue;
                }

                windows[count++] = from;
                windows[count++] = to;
                emitted = to;
            }

            return count == windows.Length ? windows : windows[..count];
        }
    }
}

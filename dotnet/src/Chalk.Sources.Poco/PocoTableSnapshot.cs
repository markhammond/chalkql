using Chalk.Catalog;

namespace Chalk.Sources.Poco;

/// <summary>
/// One registered table's whole state at one moment: the collection it was built from, the rows as
/// they are reached, every index built over them — permutations and clustered copies alike — and the
/// statistics computed from them (D260, <c>docs/design/35-poco-refresh.md</c> §1).
/// </summary>
/// <remarks>
/// <para>
/// A snapshot is immutable once published. Replacement is therefore never mutation: a refresh builds
/// the next snapshot off the execution path and swaps the table's reference to it, and an execution
/// that captured the old one keeps reading exactly what it captured. Nothing is freed under a reader
/// and no lock is held while data is read, which is what gives every reader a consistent view for
/// free.
/// </para>
/// <para>
/// The compiled column extractors are deliberately <em>not</em> here. They are the table's shape —
/// names, types, the chunk writers inferred from the row type — and the shape is fixed at
/// <c>Build()</c>; rebuilding them on a refresh would make every data change a schema change, which
/// is the one thing §2 promises it is not. What a snapshot holds is the column <em>data</em>, which
/// for a POCO table is the host's own collection plus the clustered copies extracted from it.
/// </para>
/// <para>
/// Readers are counted so that the last one's exit is knowable: the table holds one reference of its
/// own while the snapshot is current, every scan, lookup and describe takes another for as long as
/// it reads, and <see cref="Retire"/> drops the table's when the snapshot is replaced. The count
/// reaches zero exactly once, and whoever takes it there reports the release.
/// </para>
/// </remarks>
internal sealed class PocoTableSnapshot<T>
{
    private readonly Lock _statisticsGate = new();
    private ColumnStatistics[]? _statistics;

    /// <summary>
    /// The column statistics this snapshot inherited, and the row count they were computed over
    /// (D271 (g), <c>docs/design/44-catalog-registration.md</c> §7). Null for a snapshot that
    /// computes its own, which is every snapshot of a table with no freshness policy and no deferred
    /// operation behind it.
    /// </summary>
    private ColumnStatistics[]? _carried;
    private int _carriedAt;

    /// <summary>
    /// The operation that built this snapshot said <see cref="StatisticsRefresh.Defer"/>, so the
    /// carried statistics stand until the next plain refresh recomputes them — whatever the table's
    /// own growth policy would have said.
    /// </summary>
    private bool _deferred;

    /// <summary>The table's own reference while this snapshot is the current one.</summary>
    private int _readers = 1;

    public PocoTableSnapshot(
        IReadOnlyCollection<T> values,
        IReadOnlyList<T>? rows,
        IPocoIndex<T>[] indexes,
        object? hostCollection)
    {
        Values = values;
        Rows = rows;
        Indexes = indexes;
        HostCollection = hostCollection;
        RowCount = values.Count;
    }

    /// <summary>
    /// The rows by position, or null when the registration only enumerates
    /// (<c>IReadOnlyCollection&lt;T&gt;</c>, §1) and there are no positions to address.
    /// </summary>
    public IReadOnlyList<T>? Rows { get; }

    /// <summary>The rows however they are reached: <see cref="Rows"/> when there are positions.</summary>
    public IReadOnlyCollection<T> Values { get; }

    /// <summary>True when <see cref="Rows"/> can be indexed, which is what an index gather needs.</summary>
    public bool RandomAccess => Rows is not null;

    /// <summary>
    /// Rows as of this snapshot. Read once here rather than from the collection on every batch: a
    /// snapshot an append extended shares its buffer with the one before it, so what separates the
    /// two views is exactly this count (§3).
    /// </summary>
    public int RowCount { get; }

    /// <summary>Every declared index, in declaration order — the order the shape fixed at build.</summary>
    public IPocoIndex<T>[] Indexes { get; }

    /// <summary>
    /// The collection the host handed over, which is what a release reports. Null for a snapshot an
    /// append built over arrays of Chalk's own: nobody outside gave those, so nobody is waiting to
    /// have them back.
    /// </summary>
    public object? HostCollection { get; }

    /// <summary>
    /// Reported when this snapshot has been replaced and its last reader has gone. Set by the table
    /// before the snapshot is published, so a release can never outrun the wiring.
    /// </summary>
    public Action<PocoTableSnapshot<T>>? OnReleased { get; set; }

    /// <summary>
    /// Takes a reader's reference, or fails because this snapshot has been replaced and its last
    /// reader has already left. A caller that fails reads the table's current snapshot instead —
    /// which is correct by construction, because a snapshot nobody holds is a snapshot nobody is
    /// reading.
    /// </summary>
    public bool TryAcquire()
    {
        var count = Volatile.Read(ref _readers);
        while (count > 0)
        {
            var seen = Interlocked.CompareExchange(ref _readers, count + 1, count);
            if (seen == count)
            {
                return true;
            }

            count = seen;
        }

        return false;
    }

    /// <summary>Gives a reader's reference back. The last one out reports the release.</summary>
    public void Release()
    {
        if (Interlocked.Decrement(ref _readers) == 0)
        {
            OnReleased?.Invoke(this);
        }
    }

    /// <summary>
    /// Drops the table's own reference, which a swap does exactly once. The release follows
    /// immediately when no execution holds this snapshot, and when the last one finishes otherwise.
    /// </summary>
    public void Retire() => Release();

    /// <summary>
    /// Takes the statistics the previous snapshot had in force, and the row count they describe
    /// (D271 (g)). Called before the snapshot is published, so nothing can read half of it.
    /// </summary>
    public void Carry(ColumnStatistics[]? statistics, int computedOver, bool deferred)
    {
        _carried = statistics;
        _carriedAt = computedOver;
        _deferred = deferred;
    }

    /// <summary>
    /// What the next snapshot may carry: the statistics in force here, and the row count they were
    /// computed over — which is this snapshot's own when it computed them and the inherited one when
    /// it carried them. Null when nothing has been computed yet, in which case the next snapshot
    /// computes rather than carrying nothing forward.
    /// </summary>
    public (ColumnStatistics[]? Statistics, int ComputedOver, bool Deferred) Carryable =>
        _carried is not null ? (_carried, _carriedAt, _deferred) : (_statistics, RowCount, _deferred);

    /// <summary>
    /// Ends a deferral (D271 (g)): the next <see cref="Statistics"/> computes rather than carrying.
    /// What <c>RefreshAsync()</c> — the plain refresh, every source, discovery included — does to a
    /// table whose statistics were deferred and whose rows did not otherwise change.
    /// </summary>
    public void EndDeferral()
    {
        lock (_statisticsGate)
        {
            if (!_deferred)
            {
                // A growth policy is not a deferral and a plain refresh does not end it: what it says
                // is "these are fresh enough until the table has grown that much", and a reassembly
                // of the catalog has not changed how much it has grown.
                return;
            }

            _deferred = false;
            _carried = null;
            _carriedAt = 0;
            Volatile.Write(ref _statistics, null);
        }
    }

    /// <summary>
    /// The column statistics for these rows. Computed once per snapshot — the rows cannot change
    /// under it — so the repeated <c>DescribeSchema()</c> calls a catalog refresh and a test suite
    /// make cost one field read each (§2). A host-supplied statistics function is asked every time,
    /// which is what supplying one means.
    /// </summary>
    /// <remarks>
    /// Since D271 (g) the computation may be skipped and the previous snapshot's statistics carried
    /// forward instead: under <see cref="StatisticsRefresh.Defer"/>, and under a table's own
    /// <c>refreshWhenGrownBy</c> until the table has grown that much since they were taken. The row
    /// count is untouched either way — it is <see cref="RowCount"/>, read from a field — so what ages
    /// is the distribution of values and never the cardinality.
    /// </remarks>
    public ColumnStatistics[] Statistics(
        PocoColumn<T>[] columns,
        IReadOnlyList<CollationDescriptor> collations,
        PocoStatisticsPlan plan)
    {
        if (plan.Supplier is null && Volatile.Read(ref _statistics) is { } ready)
        {
            return ready;
        }

        lock (_statisticsGate)
        {
            if (plan.Supplier is null && _statistics is { } cached)
            {
                return cached;
            }

            if (plan.Supplier is null && _carried is { } carried && Carries(plan))
            {
                Volatile.Write(ref _statistics, carried);
                return carried;
            }

            var computed = PocoStatistics.Compute(columns, Values, collations, Indexes, plan);
            Volatile.Write(ref _statistics, computed);
            return computed;
        }
    }

    /// <summary>
    /// Whether the carried statistics still stand: always under a deferral, and under a growth
    /// policy until the table has grown by the stated fraction of what they were computed over. A
    /// policy of zero — the default — carries nothing and recomputes every time.
    /// </summary>
    private bool Carries(PocoStatisticsPlan plan)
    {
        if (_deferred)
        {
            return true;
        }

        if (plan.RefreshWhenGrownBy <= 0)
        {
            return false;
        }

        // Growth against the count the statistics describe. A table that has shrunk has not grown by
        // the fraction, so the carried statistics stand — which is the conservative reading and the
        // one that keeps an append-then-replace cycle from recomputing on the way down.
        return RowCount < _carriedAt + (_carriedAt * plan.RefreshWhenGrownBy);
    }
}

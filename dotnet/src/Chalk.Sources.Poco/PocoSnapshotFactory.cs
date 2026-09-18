using Chalk.Catalog;
using IndexKind = Chalk.Ir.IndexKind;

namespace Chalk.Sources.Poco;

/// <summary>
/// How one registered table makes a snapshot of itself: the declarations resolved once at
/// <c>Build()</c>, and the per-collection work — indexes, clustered copies, the D17 verification —
/// run again whenever a new collection arrives (D260 §2).
/// </summary>
/// <remarks>
/// <para>
/// Everything here is shape: the compiled column extractors, the index descriptors, the key
/// accessors, whether each index is backed by a declared collation. None of it depends on which rows
/// the table holds, which is exactly why replacing the rows is not a schema change. What depends on
/// the rows is what <see cref="From"/> builds, and it builds it off the execution path — the caller
/// is a refresh, and executions are meanwhile reading the snapshot this one will replace.
/// </para>
/// <para>
/// A host-supplied <see cref="IPocoIndex{T}"/> registered as an instance is carried into every
/// snapshot unchanged, so the structure behind it must not change while any snapshot built over it
/// may still be read. One registered as a factory is made here, once per snapshot, over the
/// collection that snapshot is built from — under the same gate, off the execution path — and each
/// product is checked against the declaration the catalog carries (F110): a snapshot then owns its
/// index the way it owns its columns, and a host replaces both by handing a new collection to a
/// refresh.
/// </para>
/// </remarks>
internal sealed class PocoSnapshotFactory<T>
{
    /// <summary>
    /// The permutation scratch is one set of reusable arrays shared by every index of this table, so
    /// two snapshots may not be built at once. Refreshes are serialised by the engine and by the
    /// source; this is the belt to that braces.
    /// </summary>
    private readonly Lock _gate = new();

    private readonly string _table;
    private readonly PocoRows<T> _registration;
    private readonly PocoColumn<T>[] _columns;
    private readonly IReadOnlyList<CollationDescriptor> _collations;
    private readonly IReadOnlyList<UniqueKeyDescriptor> _uniqueKeys;
    private readonly PocoIndexPlan<T>[] _indexes;
    private readonly PocoHostIndex<T>[] _hostIndexes;
    private readonly PermutationIndexScratch _scratch;
    private readonly bool _verify;

    public PocoSnapshotFactory(
        string table,
        PocoRows<T> registration,
        PocoColumn<T>[] columns,
        IReadOnlyList<CollationDescriptor> collations,
        IReadOnlyList<UniqueKeyDescriptor> uniqueKeys,
        PocoIndexPlan<T>[] indexes,
        PocoHostIndex<T>[] hostIndexes,
        PermutationIndexScratch scratch,
        bool verify)
    {
        _table = table;
        _registration = registration;
        _columns = columns;
        _collations = collations;
        _uniqueKeys = uniqueKeys;
        _indexes = indexes;
        _hostIndexes = hostIndexes;
        _scratch = scratch;
        _verify = verify;
    }

    /// <summary>Whether this table's rows can be reached by position (§1).</summary>
    public bool RandomAccess => _registration.RandomAccess;

    /// <summary>What the registration reports now — the <c>Func</c>, re-read.</summary>
    public IReadOnlyCollection<T> Registration() => _registration.Read();

    /// <summary>A snapshot of whatever the registration reports now.</summary>
    public PocoTableSnapshot<T> FromRegistration() => From(Registration());

    /// <summary>
    /// A snapshot over <paramref name="collection"/>: every index built, every clustered copy
    /// extracted, every declaration verified. The collection is remembered as the host's, so its
    /// release is reported when this snapshot is replaced and the last reader has gone.
    /// </summary>
    public PocoTableSnapshot<T> From(IReadOnlyCollection<T> collection) =>
        Build(collection, collection, RandomAccess ? (IReadOnlyList<T>)collection : null, previous: null);

    /// <summary>
    /// A snapshot of <paramref name="previous"/>'s rows followed by <paramref name="added"/> (§3).
    /// The rows live in an array Chalk owns, so there is no host collection to report released —
    /// the one the previous snapshot held is reported when that snapshot retires, which is exactly
    /// when Chalk stopped reading it.
    /// </summary>
    public PocoTableSnapshot<T> Appended(PocoTableSnapshot<T> previous, IReadOnlyList<T> added)
    {
        var existing = previous.Rows
            ?? throw new InvalidOperationException(
                $"table '{_table}' was registered as a collection that only enumerates, so its rows "
                + "have no positions to append after.");

        var rows = PocoAppendedRows<T>.Extend(existing, previous.RowCount, added);
        return Build(rows, hostCollection: null, rows, previous);
    }

    private PocoTableSnapshot<T> Build(
        IReadOnlyCollection<T> values,
        object? hostCollection,
        IReadOnlyList<T>? rows,
        PocoTableSnapshot<T>? previous)
    {
        // A collection that only enumerates has no positions, so it declares no collation and no
        // built-in index (§1); the one materialisation here is for verification, and it is dropped
        // as soon as the snapshot is built.
        var indexed = rows ?? [.. values];

        lock (_gate)
        {
            var appendedFrom = previous?.RowCount ?? 0;
            var indexes = new IPocoIndex<T>[_indexes.Length + _hostIndexes.Length];
            for (var i = 0; i < _indexes.Length; i++)
            {
                indexes[i] = _indexes[i].Build(
                    _table, indexed, _columns, _scratch, previous?.Indexes[i], appendedFrom);
            }

            for (var i = 0; i < _hostIndexes.Length; i++)
            {
                indexes[_indexes.Length + i] = _hostIndexes[i].Make(_table, values);
            }

            if (_verify)
            {
                Verify(indexed, appendedFrom);
            }

            return new PocoTableSnapshot<T>(values, rows, indexes, hostCollection);
        }
    }

    /// <summary>
    /// D17, on every snapshot rather than only on the first: a collation or a unique key that the
    /// new rows do not keep is a silent wrong-answer bug of exactly the kind one linear pass at
    /// registration was cheap insurance against, and a replacement is a registration.
    /// </summary>
    private void Verify(IReadOnlyList<T> rows, int appendedFrom)
    {
        var accessors = new Dictionary<int, Func<T, object?>>();
        Func<T, object?> Accessor(int column)
        {
            if (!accessors.TryGetValue(column, out var accessor))
            {
                accessor = _columns[column].CompileLogicalAccessor();
                accessors[column] = accessor;
            }

            return accessor;
        }

        // An append only has to be checked where it joins: the rows before it were checked when they
        // arrived, and a collation is a statement about consecutive rows, so the first appended row
        // and everything after it is the whole of what is new (§3).
        foreach (var collation in _collations)
        {
            PocoVerification.VerifyCollation(
                _table,
                rows,
                collation.Keys
                    .Select(k => (_columns[k.Column].Name, k.Direction, Accessor(k.Column)))
                    .ToArray(),
                Math.Max(1, appendedFrom));
        }

        foreach (var key in _uniqueKeys)
        {
            PocoVerification.VerifyUniqueKey(
                _table,
                rows,
                key.Columns.Select(c => _columns[c].Name).ToArray(),
                key.Columns.Select(Accessor).ToArray());
        }
    }
}

/// <summary>
/// One declared built-in index, resolved: the descriptor the catalog publishes, the compiled key
/// accessors, and whether a declared collation already puts the rows in its order. Building it over
/// a collection is all that is left to do, which is what a refresh does again.
/// </summary>
internal sealed class PocoIndexPlan<T>
{
    public required IndexDescriptor Descriptor { get; init; }

    public required Func<T, object?>[] Accessors { get; init; }

    public required bool CollationBacked { get; init; }

    public required string[] KeyColumnNames { get; init; }

    public IPocoIndex<T> Build(
        string table,
        IReadOnlyList<T> rows,
        PocoColumn<T>[] columns,
        PermutationIndexScratch scratch,
        IPocoIndex<T>? previous,
        int appendedFrom) =>
        Descriptor.Kind == IndexKind.Clustered
            ? new ClusteredIndex<T>(
                Descriptor, rows, Accessors, CollationBacked, table, KeyColumnNames, scratch, columns,
                previous as ClusteredIndex<T>, appendedFrom)
            : new PermutationIndex<T>(
                Descriptor, rows, Accessors, CollationBacked, table, KeyColumnNames, scratch,
                previous as PermutationIndex<T>, appendedFrom);
}

/// <summary>
/// One host-registered index: the descriptor the catalog publishes, and how each snapshot gets its
/// instance — the registered object for <c>Index(IPocoIndex&lt;T&gt;)</c>, a fresh one over the
/// snapshot's own rows for <c>Index(IndexDescriptor, Func)</c> (F110).
/// </summary>
internal sealed class PocoHostIndex<T>
{
    private readonly Func<IReadOnlyCollection<T>, IPocoIndex<T>> _make;

    public PocoHostIndex(IndexDescriptor descriptor, Func<IReadOnlyCollection<T>, IPocoIndex<T>> make)
    {
        Descriptor = descriptor;
        _make = make;
    }

    public IndexDescriptor Descriptor { get; }

    /// <summary>
    /// The index for a snapshot over <paramref name="rows"/>. A product whose declaration differs
    /// from the registration's is refused: the catalog has already told the planner what this index
    /// is, and a plan built on that must not meet a different one at execution.
    /// </summary>
    public IPocoIndex<T> Make(string table, IReadOnlyCollection<T> rows)
    {
        var index = _make(rows)
            ?? throw new CatalogVerificationException(
                table, $"index '{Descriptor.Name}'", 0, "the index factory returned null.");
        if (!SameDeclaration(Descriptor, index.Descriptor))
        {
            throw new CatalogVerificationException(
                table,
                $"index '{Descriptor.Name}'",
                0,
                $"the index factory returned an index declared as '{index.Descriptor.Name}' "
                + $"({Shape(index.Descriptor)}) where the registration declared {Shape(Descriptor)}; "
                + "a snapshot's index must be what the catalog says it is.");
        }

        return index;
    }

    private static string Shape(IndexDescriptor d) =>
        $"{d.Kind}, columns [{string.Join(",", d.Columns)}], unique {d.Unique}";

    private static bool SameDeclaration(IndexDescriptor a, IndexDescriptor b) =>
        a.Name == b.Name
        && a.Kind == b.Kind
        && a.Unique == b.Unique
        && a.Columns.SequenceEqual(b.Columns)
        && a.Directions.SequenceEqual(b.Directions)
        && a.Covering.SequenceEqual(b.Covering);
}

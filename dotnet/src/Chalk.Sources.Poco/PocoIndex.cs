using Chalk.Catalog;

namespace Chalk.Sources.Poco;

/// <summary>
/// An index over a registered collection (D35). Index <em>creation</em> is pluggable: the POCO
/// source talks to indexes only through this interface, ships one implementation of it
/// (<see cref="PermutationIndex{T}"/>), and takes any structure a host registers with
/// <c>PocoTableBuilder&lt;T&gt;.Index(IPocoIndex&lt;T&gt;)</c>.
/// </summary>
/// <remarks>
/// <para>
/// A host-supplied index's <see cref="Descriptor"/> is trusted as declared — the planner deletes
/// sorts and chooses lookups on the strength of it — which is why the test kit ships
/// <c>PocoIndexConformance.Verify</c>: a full-scan comparison over a battery of ranges that an
/// adapter author runs once.
/// </para>
/// <para>
/// Snapshot consistency is the host's to keep, and it is more than thread safety: an execution
/// reads one logical snapshot from its first batch to its last, and an index answers for the rows
/// of the snapshot it belongs to. An instance registered with
/// <c>PocoTableBuilder&lt;T&gt;.Index(IPocoIndex&lt;T&gt;)</c> is carried into every snapshot
/// unchanged, so the structure behind it must not change while any snapshot built over it may
/// still be read — a structure that tolerates concurrent mutation, or copies on write, still hands
/// one execution two different answers. A host whose structure follows the rows registers a
/// factory with <c>Index(IndexDescriptor, Func&lt;IReadOnlyCollection&lt;T&gt;, IPocoIndex&lt;T&gt;&gt;)</c>:
/// it is called once per snapshot, over that snapshot's rows, off the execution path, and its
/// product lives and dies with the snapshot. Chalk takes no locks.
/// </para>
/// </remarks>
public interface IPocoIndex<T>
{
    /// <summary>
    /// Name, kind, key columns in key order, key directions and uniqueness, as the catalog will
    /// carry them. The column indexes are the *table's*, which is why an index is registered on a
    /// table builder rather than standing alone.
    /// </summary>
    IndexDescriptor Descriptor { get; }

    /// <summary>
    /// The rows the range matches. <see cref="Chalk.Ir.IndexKind.Ordered"/> must yield them in the
    /// index's key order; <see cref="Chalk.Ir.IndexKind.Hash"/> may yield them in any order and may
    /// reject a non-equality range with <see cref="SourceContractException"/>.
    /// </summary>
    IEnumerable<T> Lookup(IndexKeyRange range);

    /// <summary>
    /// Distinct values of the key prefix of length <paramref name="keyPositions"/>, when the
    /// structure knows it cheaply. Null means "unknown", which is a legitimate answer.
    /// </summary>
    long? DistinctCount(int keyPositions) => null;

    /// <summary>
    /// How much memory this index holds per row of the table, for the build report. -1 = unknown.
    /// </summary>
    long BytesPerRow => -1;
}

/// <summary>
/// An index that can answer in row <em>positions</em> rather than rows. The built-in index does, and
/// the scan then gathers straight through the compiled chunk writers instead of materialising rows.
/// </summary>
/// <remarks>
/// Positions index the collection the table was registered with, as it stands at lookup time. An
/// implementation that cannot promise that — anything that holds its own copy of the rows — should
/// implement <see cref="IPocoIndex{T}"/> only.
/// </remarks>
public interface IPositionalPocoIndex<T> : IPocoIndex<T>
{
    /// <summary>
    /// Gets the half-open ordinal window [from, to) in index order.
    /// </summary>
    void GetWindow(IndexKeyRange range, out int from, out int to);

    /// <summary>
    /// Maps an index ordinal to the position in the registered row collection.
    /// </summary>
    int GetPosition(int ordinal);

    /// <summary>
    /// Compatibility/convenience API. Hot scan paths should use GetWindow/GetPosition.
    /// </summary>
    IEnumerable<int> LookupPositions(IndexKeyRange range);
}

namespace Chalk.Sources.Poco;

/// <summary>
/// Where a registered table's rows come from, and whether they can be reached by position.
/// </summary>
/// <remarks>
/// <para>
/// <c>IReadOnlyList&lt;T&gt;</c> is the fast path everything before M2 assumed: scans and index
/// gathers index straight into it. <c>IReadOnlyCollection&lt;T&gt;</c> joined it in M2 (§1) for
/// structures that only enumerate — an <c>IndexedSet&lt;T&gt;</c>, for instance — which then declare
/// no collation and no built-in index, and bring their own <see cref="IPocoIndex{T}"/> instead.
/// </para>
/// <para>
/// This is the <em>registration</em>, not the state: <see cref="Read"/> is called at <c>Build()</c>
/// and at every refresh, and what it returns becomes a snapshot (D260 §2). A scan reads the
/// snapshot, never this, which is what lets a host swap the collection behind a <c>Func</c> without
/// an execution in flight ever seeing half of the swap.
/// </para>
/// </remarks>
internal sealed class PocoRows<T>
{
    private readonly Func<IReadOnlyCollection<T>> _get;

    private PocoRows(Func<IReadOnlyCollection<T>> get, bool randomAccess)
    {
        _get = get;
        RandomAccess = randomAccess;
    }

    /// <summary>True when <see cref="Snapshot"/> returns an <see cref="IReadOnlyList{T}"/>.</summary>
    public bool RandomAccess { get; }

    public static PocoRows<T> OfList(Func<IReadOnlyList<T>> rows) => new(() => rows(), randomAccess: true);

    public static PocoRows<T> OfCollection(Func<IReadOnlyCollection<T>> rows) =>
        new(rows, randomAccess: false);

    /// <summary>The collection as it stands now. Read at <c>Build()</c> and at every refresh.</summary>
    public IReadOnlyCollection<T> Read() => _get();
}

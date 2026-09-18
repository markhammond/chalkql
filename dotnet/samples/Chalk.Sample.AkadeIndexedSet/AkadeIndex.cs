using Akade.IndexedSet;
using Chalk.Catalog;
using Chalk.Sources;
using Chalk.Sources.Poco;
using IndexKind = Chalk.Ir.IndexKind;
using SortDirection = Chalk.Ir.SortDirection;

namespace Chalk.Sample.AkadeIndexedSet;

/// <summary>
/// One <see href="https://github.com/akade/Akade.IndexedSet">Akade.IndexedSet</see> index, wrapped
/// as an <see cref="IPocoIndex{T}"/>. One Chalk index per Akade index; a compound key is a
/// tuple key selector.
/// </summary>
/// <remarks>
/// <para>
/// The point of this sample is that Chalk asks an index exactly two things — "which rows does this
/// range match" and "in what order" — and that a structure Chalk knows nothing about can answer
/// both. Everything below is the translation between Chalk's <see cref="IndexKeyRange"/> and
/// Akade's <c>Range</c> / <c>GreaterThanOrEqual</c> / <c>LessThan</c> / <c>OrderBy</c>.
/// </para>
/// <para>
/// Two things an adapter author has to get right, and this one does:
/// </para>
/// <list type="bullet">
///   <item>
///     <b>The ordering has to be Chalk's.</b> A tuple key compares its string component with
///     <c>Comparer&lt;string&gt;.Default</c>, which is culture-aware; Chalk compares by code point.
///     So the Akade index is built with a comparer that defers to
///     <see cref="SourceValueOrder"/>, and the same one answers every question here.
///   </item>
///   <item>
///     <b>A partial key prefix is a range over the whole key, and which extreme fills the rest
///     depends on inclusivity.</b> Chalk may bound only the first of two key columns; a tuple index
///     cannot express "anything" for the second. <c>symbol &gt;= 'A'</c> is the tuple
///     <c>('A', min)</c> inclusive, but <c>symbol &gt; 'A'</c> is <c>('A', max)</c> — filling with
///     the minimum there would keep every row whose symbol <em>is</em> 'A'. Symmetrically for the
///     upper bound. That is what <see cref="AkadeKey{T,TKey}.Fill"/> takes its flag for, and it is
///     the mistake <c>PocoIndexConformance.Verify</c> caught when this sample was written.
///   </item>
/// </list>
/// <para>
/// What this sample does <em>not</em> handle, deliberately: a nullable key column. Chalk's rule is
/// that a range never matches a row whose value is NULL in a column the range bounds, and expressing
/// that over a tuple key needs a sentinel this sample would have to invent. The two indexed columns
/// of <c>bars</c> are NOT NULL, and <c>PocoIndexConformance.Verify</c> is what would catch it if
/// they were not.
/// </para>
/// <para>
/// <b>Snapshot consistency, under an engine that executes concurrently.</b> A POCO table is an
/// immutable snapshot: an execution keeps the snapshot it started with while a replacement lands,
/// and executions started afterwards see the replacement. This index answers every
/// <see cref="IPocoIndex{T}.Lookup"/> from the <c>IndexedSet</c> it wraps, so it is registered as
/// a factory — <c>Index(descriptor, rows =&gt; …)</c> — that the source calls once per snapshot,
/// over that snapshot's rows, off the execution path: the snapshot owns its Akade indexes the way
/// it owns its columns. The requirement on the host is stronger than thread safety: a set handed
/// to Chalk must not change, logically, while any snapshot built over it may still be read — a
/// structure that tolerates concurrent mutation, or copies on write, still hands one execution two
/// different answers. To change the data, build a new set off the execution path, hand it to the
/// registration and call <c>ChalkEngine.RefreshAsync</c>; the old set comes back by reference in
/// <see cref="PocoSource.SnapshotReleased"/> once no execution holds the snapshot it fed, which is
/// when it may be reclaimed or reused, its indexes with it.
/// </para>
/// </remarks>
public sealed class AkadeIndex<T, TKey> : IPocoIndex<T>
    where TKey : notnull
{
    private readonly IndexedSet<T> _set;
    private readonly Func<T, TKey> _key;
    private readonly string _akadeIndexName;
    private readonly AkadeKey<T, TKey> _bounds;
    private readonly IComparer<TKey> _comparer;

    /// <param name="descriptor">What the catalog will say about this index. Trusted as declared.</param>
    /// <param name="set">The set this index lives in.</param>
    /// <param name="key">
    /// The key selector, written <em>exactly</em> as it was at registration: Akade identifies an
    /// index by the source text of its accessor (<c>CallerArgumentExpression</c>), so
    /// <paramref name="akadeIndexName"/> carries that text.
    /// </param>
    /// <param name="akadeIndexName">The accessor's source text, as Akade recorded it.</param>
    /// <param name="bounds">How a Chalk range's bound values become a key of this index.</param>
    /// <param name="comparer">The key ordering — Chalk's, not the CLR's default.</param>
    public AkadeIndex(
        IndexDescriptor descriptor,
        IndexedSet<T> set,
        Func<T, TKey> key,
        string akadeIndexName,
        AkadeKey<T, TKey> bounds,
        IComparer<TKey> comparer)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(akadeIndexName);
        ArgumentNullException.ThrowIfNull(bounds);
        ArgumentNullException.ThrowIfNull(comparer);

        Descriptor = descriptor;
        _set = set;
        _key = key;
        _akadeIndexName = akadeIndexName;
        _bounds = bounds;
        _comparer = comparer;
    }

    /// <inheritdoc />
    public IndexDescriptor Descriptor { get; }

    /// <inheritdoc />
    /// <remarks>Akade holds its own structures; how much they cost per row is its business.</remarks>
    public long BytesPerRow => -1;

    /// <inheritdoc />
    public IEnumerable<T> Lookup(IndexKeyRange range)
    {
        ArgumentNullException.ThrowIfNull(range);

        if (Descriptor.Kind == IndexKind.Hash && !IsEquality(range))
        {
            throw new SourceContractException(
                "akade",
                Descriptor.Name,
                $"this is a hash index and answers equality only, but the plan asked for {range}.");
        }

        if (Descriptor.Kind == IndexKind.Hash)
        {
            // Akade's hash index answers equality directly, in no particular order — which is what
            // INDEX_KIND_HASH promises Chalk.
            return _set.Where(_key, _bounds.Fill(range.Lower, useMaximum: false), _akadeIndexName);
        }

        var rows = Bounded(range);

        // Akade's range index is a sorted structure, but its iteration order is its own business:
        // the ordering an ORDERED index promises Chalk is a contract, so this asserts it here
        // rather than assuming it. Sorting an already-sorted sequence is cheap, and a sample is the
        // wrong place to be clever about someone else's internals.
        return rows.OrderBy(_key, _comparer);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Null: Akade does not offer a distinct count, and a guess here would be worse than silence —
    /// the planner divides by this number.
    /// </remarks>
    public long? DistinctCount(int keyPositions) => null;

    private IEnumerable<T> Bounded(IndexKeyRange range)
    {
        var hasLower = range.Lower.Count > 0;
        var hasUpper = range.Upper.Count > 0;

        if (!hasLower && !hasUpper)
        {
            return _set.FullScan();
        }

        var keyColumns = Descriptor.Columns.Count;

        // An exclusive bound on a prefix excludes every key with that prefix, so the components the
        // range does not bound take the *opposite* extreme.
        if (hasLower && hasUpper)
        {
            var start = LowerKey(range, keyColumns);
            var end = UpperKey(range, keyColumns);

            // A range whose lower bound is above its upper matches nothing. Chalk says so; Akade
            // throws, so the empty case is answered here rather than by an exception.
            if (_comparer.Compare(start, end) > 0)
            {
                return [];
            }

            return _set.Range(
                _key, start, end, range.LowerInclusive, range.UpperInclusive, _akadeIndexName);
        }

        if (hasLower)
        {
            var from = LowerKey(range, keyColumns);
            return range.LowerInclusive
                ? _set.GreaterThanOrEqual(_key, from, _akadeIndexName)
                : _set.GreaterThan(_key, from, _akadeIndexName);
        }

        var to = UpperKey(range, keyColumns);
        return range.UpperInclusive
            ? _set.LessThanOrEqual(_key, to, _akadeIndexName)
            : _set.LessThan(_key, to, _akadeIndexName);
    }

    private TKey LowerKey(IndexKeyRange range, int keyColumns) =>
        _bounds.Fill(range.Lower, useMaximum: range.Lower.Count < keyColumns && !range.LowerInclusive);

    private TKey UpperKey(IndexKeyRange range, int keyColumns) =>
        _bounds.Fill(range.Upper, useMaximum: range.Upper.Count == keyColumns || range.UpperInclusive);

    private bool IsEquality(IndexKeyRange range) =>
        range.LowerInclusive
        && range.UpperInclusive
        && range.Lower.Count == Descriptor.Columns.Count
        && range.Upper.Count == Descriptor.Columns.Count
        && range.Lower.Zip(range.Upper).All(pair => Equals(pair.First, pair.Second));
}

/// <summary>
/// How a Chalk range's bound values become a key of an Akade index, filling the components the
/// range does not bound with the smallest or the largest value of their type.
/// </summary>
public sealed class AkadeKey<T, TKey>
    where TKey : notnull
{
    private readonly Func<IReadOnlyList<object?>, bool, TKey> _fill;

    /// <param name="fill">
    /// Bound values, and whether the components they do not cover take their type's maximum rather
    /// than its minimum, to a key.
    /// </param>
    public AkadeKey(Func<IReadOnlyList<object?>, bool, TKey> fill)
    {
        ArgumentNullException.ThrowIfNull(fill);
        _fill = fill;
    }

    /// <summary>The key a bound stands for.</summary>
    public TKey Fill(IReadOnlyList<object?> bounds, bool useMaximum) => _fill(bounds, useMaximum);
}

/// <summary>
/// Chalk's ordering, as an <see cref="IComparer{T}"/> Akade can be built with: strings by code
/// point, NaN last, NULLs where the declared direction puts them.
/// </summary>
public sealed class AkadeKeyComparer<TKey> : IComparer<TKey>
{
    private readonly Func<TKey, object?[]> _components;
    private readonly SortDirection[] _directions;

    /// <param name="components">The key's components, in key order.</param>
    /// <param name="directions">One direction per component.</param>
    public AkadeKeyComparer(Func<TKey, object?[]> components, params SortDirection[] directions)
    {
        ArgumentNullException.ThrowIfNull(components);
        ArgumentNullException.ThrowIfNull(directions);
        _components = components;
        _directions = directions;
    }

    public int Compare(TKey? x, TKey? y)
    {
        if (x is null || y is null)
        {
            return x is null && y is null ? 0 : x is null ? -1 : 1;
        }

        var left = _components(x);
        var right = _components(y);
        for (var i = 0; i < left.Length && i < right.Length; i++)
        {
            var comparison = SourceValueOrder.Compare(
                left[i],
                right[i],
                i < _directions.Length ? _directions[i] : SortDirection.AscNullsLast);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return 0;
    }
}

/// <summary>
/// An <see cref="IndexedSet{T}"/> as a collection Chalk can scan. It implements no collection
/// interface of its own, which is exactly the case <c>AddTable(name, IReadOnlyCollection&lt;T&gt;)</c>
/// exists for: scans stage a batch of rows at a time instead of indexing into a list.
/// </summary>
public sealed class AkadeRows<T> : IReadOnlyCollection<T>
{
    private readonly IndexedSet<T> _set;

    public AkadeRows(IndexedSet<T> set)
    {
        ArgumentNullException.ThrowIfNull(set);
        _set = set;
    }

    /// <summary>The set itself, so an index factory can build this snapshot's indexes over it.</summary>
    public IndexedSet<T> Set => _set;

    public int Count => _set.Count;

    public IEnumerator<T> GetEnumerator() => _set.FullScan().GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

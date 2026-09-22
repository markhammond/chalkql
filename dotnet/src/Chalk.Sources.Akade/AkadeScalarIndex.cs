using Akade.IndexedSet;
using Chalk.Catalog;
using Chalk.Sources;
using Chalk.Sources.Poco;
using IndexKind = Chalk.Ir.IndexKind;
using IndexReversal = Chalk.Ir.IndexReversal;

namespace Chalk.Sources.Akade;

/// <summary>
/// Adapts one scalar, direct-member Akade hash/range index to Chalk's host-index contract.
/// </summary>
/// <remarks>
/// Discovery deliberately limits this adapter to key types whose equality/order semantics currently
/// match Chalk's catalog contract. Computed, compound, nullable and comparer-sensitive keys are not
/// registered here; they remain available to the host through Akade itself.
/// </remarks>
internal sealed class AkadeScalarIndex<T, TKey> : IPocoIndex<T>, IReversiblePocoIndex<T>
    where TKey : notnull
{
    private readonly IndexedSet<T> _set;
    private readonly Func<T, TKey> _key;
    private readonly string _akadeIndexName;
    private readonly string _sourceId;
    private readonly string _table;
    private readonly IComparer<TKey> _comparer;
    private readonly long? _distinctKeys;

    public AkadeScalarIndex(
        IndexDescriptor descriptor,
        IndexedSet<T> set,
        Func<T, TKey> key,
        string akadeIndexName,
        string sourceId,
        string table,
        IComparer<TKey>? comparer = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(akadeIndexName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);

        Descriptor = descriptor;
        _set = set;
        _key = key;
        _akadeIndexName = akadeIndexName;
        _sourceId = sourceId;
        _table = table;
        _comparer = comparer ?? AkadeKeyOrder.Ascending<TKey>() ?? Comparer<TKey>.Default;

        // Read once, here, off the execution path: the index is built once per snapshot.
        _distinctKeys = descriptor.Unique
            ? set.Count
            : AkadePhysicalStatistics.DistinctKeys(set, akadeIndexName);

    }

    public IndexDescriptor Descriptor { get; }

    /// <summary>Akade owns the storage; Chalk does not attempt to account for it per row.</summary>
    public long BytesPerRow => -1;

    public IEnumerable<T> Lookup(IndexKeyRange range)
    {
        ArgumentNullException.ThrowIfNull(range);

        return Descriptor.Kind switch
        {
            IndexKind.Hash => Hash(range),
            IndexKind.Ordered => Ordered(range),
            _ => throw new InvalidOperationException(
                $"Akade index '{Descriptor.Name}' has unsupported Chalk kind {Descriptor.Kind}."),
        };
    }

    /// <summary>
    /// The distinct keys the physical index holds. A unique index has one per row; a hash index is a
    /// dictionary of keys and knows its own count; a range index does not, and unknown is the only
    /// honest alternative to an exact answer, because the planner divides by this.
    /// </summary>
    public long? DistinctCount(int keyPositions) => keyPositions == 1 ? _distinctKeys : null;

    private IEnumerable<T> Hash(IndexKeyRange range)
    {
        if (!range.LowerInclusive
            || !range.UpperInclusive
            || range.Lower.Count != 1
            || range.Upper.Count != 1)
        {
            throw NonEqualityHashRange(range);
        }

        if (!TryKey(range.Lower[0], out var lower)
            || !TryKey(range.Upper[0], out var upper))
        {
            // SQL equality against NULL cannot match a non-null indexed member.
            return [];
        }

        if (!EqualityComparer<TKey>.Default.Equals(lower, upper))
        {
            throw NonEqualityHashRange(range);
        }

        return _set.Where(_key, lower, _akadeIndexName);
    }

    /// <summary>
    /// A hash index is allowed to refuse a non-equality range, and says so in the engine's own
    /// vocabulary for "a source was asked something it does not serve".
    /// </summary>
    private SourceContractException NonEqualityHashRange(IndexKeyRange range) =>
        new(
            _sourceId,
            _table,
            $"Akade hash index '{Descriptor.Name}' was asked for non-equality range {range}.");

    private IEnumerable<T> Ordered(IndexKeyRange range)
    {
        if (range.Lower.Count > 1 || range.Upper.Count > 1)
        {
            throw new InvalidOperationException(
                $"Akade scalar index '{Descriptor.Name}' was asked to bound more than one key column.");
        }

        var hasLower = range.Lower.Count != 0;
        var hasUpper = range.Upper.Count != 0;
        var lower = default(TKey)!;
        var upper = default(TKey)!;

        if (hasLower && !TryKey(range.Lower[0], out lower))
        {
            return [];
        }

        if (hasUpper && !TryKey(range.Upper[0], out upper))
        {
            return [];
        }

        if (!hasLower && !hasUpper)
        {
            // The whole-range ordered scan. Akade documents OrderBy as "the order defined by the
            // index", which is exactly the contract; the range shapes below have no such promise,
            // which is what the guard is for.
            return InKeyOrder(_set.OrderBy(_key, 0, _akadeIndexName));
        }

        if (_set.Count == 0)
        {
            return [];
        }

        // One side open becomes the index's own extreme on that side, because Akade's one-sided
        // shapes do not honour the comparer the index was built with (D281).
        var start = hasLower ? lower : _set.Min(_key, _akadeIndexName);
        var end = hasUpper ? upper : _set.Max(_key, _akadeIndexName);

        // A range whose start is past its end matches nothing. Chalk says so; Akade throws, so the
        // empty case is answered here rather than by an exception.
        if (_comparer.Compare(start, end) > 0)
        {
            return [];
        }

        return InKeyOrder(_set.Range(
            _key,
            start,
            end,
            !hasLower || range.LowerInclusive,
            !hasUpper || range.UpperInclusive,
            _akadeIndexName));
    }

    /// <summary>
    /// A range with no upper bound, the whole index included (D283).
    /// </summary>
    /// <remarks>
    /// That is what Akade's public surface serves backwards without a copy: <c>OrderByDescending</c>
    /// starts at the last key and every row down to the lower bound is wanted, so nothing is skipped
    /// and nothing is buffered. A range with an upper bound is not offered, because reaching it
    /// backwards would mean skipping every row above it or buffering the matched range to reverse
    /// it, which is the copy D277 removed; the planner sorts those instead.
    /// </remarks>
    public IndexReversal Reversal =>
        Descriptor.Kind == IndexKind.Ordered ? IndexReversal.OpenAbove : IndexReversal.Unspecified;

    /// <inheritdoc />
    public IEnumerable<T> LookupReversed(IndexKeyRange range)
    {
        ArgumentNullException.ThrowIfNull(range);

        if (Descriptor.Kind != IndexKind.Ordered || range.Upper.Count != 0)
        {
            throw new SourceContractException(
                _sourceId,
                _table,
                $"Akade index '{_akadeIndexName}', behind the index '{Descriptor.Name}', was asked "
                + $"for {range} backwards. Only a range with no upper bound is offered that way: "
                + "reaching one backwards would mean skipping every row above it, or buffering the "
                + "matched range to reverse it.");
        }

        var hasLower = range.Lower.Count != 0;
        var lower = default(TKey)!;
        if (hasLower && !TryKey(range.Lower[0], out lower))
        {
            return [];
        }

        // Akade documents OrderByDescending as the index's order, walked the other way. The rows
        // above the bound are exactly the ones wanted, so the walk simply stops at the first key
        // below it — no seek, no skipping, and a consumer that reads one row reads one row.
        return InReverseKeyOrder(
            _set.OrderByDescending(_key, 0, _akadeIndexName),
            lower,
            hasLower,
            range.LowerInclusive);
    }

    /// <summary>
    /// The guard, reversed (D283): the same one comparison per row and the same nothing allocated,
    /// against the order this enumeration claims rather than the one the index is stored in.
    /// </summary>
    internal IEnumerable<T> InReverseKeyOrder(IEnumerable<T> rows) =>
        InReverseKeyOrder(rows, default!, bounded: false, inclusive: true);

    /// <summary>
    /// The same, stopping at <paramref name="lower"/>: a descending walk reaches the rows above the
    /// bound first and wants every one of them, so the first key below it ends the walk. The bound
    /// and the order are checked from the same key, so the row path reads each key once.
    /// </summary>
    private IEnumerable<T> InReverseKeyOrder(
        IEnumerable<T> rows, TKey lower, bool bounded, bool inclusive)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var previous = default(TKey)!;
        var hasPrevious = false;

        foreach (var row in rows)
        {
            var key = _key(row);

            if (bounded)
            {
                var toBound = _comparer.Compare(key, lower);
                if (toBound < 0 || (toBound == 0 && !inclusive))
                {
                    yield break;
                }
            }

            if (hasPrevious && _comparer.Compare(previous, key) < 0)
            {
                throw new SourceContractException(
                    _sourceId,
                    _table,
                    $"Akade index '{_akadeIndexName}', behind the ORDERED index "
                    + $"'{Descriptor.Name}', yielded key '{key}' after '{previous}' while being read "
                    + "backwards. A reversed lookup must arrive in the reverse of the index's "
                    + "declared key order.");
            }

            previous = key;
            hasPrevious = true;
            yield return row;
        }
    }

    /// <summary>
    /// Akade's enumeration, yielded as it comes and verified as it goes (D277).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Chalk's ORDERED contract is about the rows a lookup returns, and Akade documents
    /// <c>OrderBy</c> as the index's order but says nothing about the enumeration order of a
    /// <c>Range</c>. Sorting the matched rows defensively would cost the whole range before the first row —
    /// which is the one thing a consumer under a <c>LIMIT 1</c> must not pay — so the adapter
    /// enumerates lazily and checks instead: one key comparison per row against the previous key,
    /// the previous key held in a local, and nothing allocated per row.
    /// </para>
    /// <para>
    /// A row that arrives out of order fails immediately and by name rather than being sorted
    /// around, because a silently reordered lookup is a wrong answer the planner has already relied
    /// on: the ordered index is why there is no sort above this at all.
    /// </para>
    /// </remarks>
    internal IEnumerable<T> InKeyOrder(IEnumerable<T> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var previous = default(TKey)!;
        var hasPrevious = false;

        foreach (var row in rows)
        {
            var key = _key(row);
            if (hasPrevious && _comparer.Compare(previous, key) > 0)
            {
                throw new SourceContractException(
                    _sourceId,
                    _table,
                    $"Akade index '{_akadeIndexName}', behind the ORDERED index "
                    + $"'{Descriptor.Name}', yielded key '{key}' after '{previous}'. An ordered "
                    + "lookup must arrive in the index's declared key order.");
            }

            previous = key;
            hasPrevious = true;
            yield return row;
        }
    }

    private static bool TryKey(object? value, out TKey key)
    {
        if (value is null)
        {
            key = default!;
            return false;
        }

        key = AkadeBound.Coerce<TKey>(value);
        return true;
    }
}

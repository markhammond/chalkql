using Akade.IndexedSet;
using Chalk.Catalog;
using Chalk.Sources;
using Chalk.Sources.Poco;
using IndexKind = Chalk.Ir.IndexKind;

namespace Chalk.Sources.Akade;

/// <summary>
/// Adapts one compound Akade index — a <c>ValueTuple</c> key of two to four direct members — to
/// Chalk's host-index contract (D280).
/// </summary>
/// <remarks>
/// <para>
/// Chalk may bound only a prefix of the key, and a tuple cannot say "anything" for the rest, so the
/// components a range does not reach take their type's minimum or maximum by the bound's
/// inclusivity: <c>symbol &gt;= 'A'</c> is <c>('A', min)</c> inclusive, and <c>symbol &gt; 'A'</c> is
/// <c>('A', max)</c>, because filling with the minimum there would keep every row whose symbol
/// <em>is</em> 'A'.
/// </para>
/// <para>
/// Tuples are structs and the composed comparer is compiled over their fields, so the ordered
/// guard's one comparison per row allocates nothing.
/// </para>
/// </remarks>
internal sealed class AkadeTupleIndex<T, TKey> : IPocoIndex<T>
    where TKey : notnull
{
    private readonly IndexedSet<T> _set;
    private readonly Func<T, TKey> _key;
    private readonly AkadeTupleKey<TKey> _shape;
    private readonly string _akadeIndexName;
    private readonly string _sourceId;
    private readonly string _table;
    private readonly long? _distinctKeys;

    public AkadeTupleIndex(
        IndexDescriptor descriptor,
        IndexedSet<T> set,
        Func<T, TKey> key,
        AkadeTupleKey<TKey> shape,
        string akadeIndexName,
        string sourceId,
        string table)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(shape);
        ArgumentException.ThrowIfNullOrWhiteSpace(akadeIndexName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);

        Descriptor = descriptor;
        _set = set;
        _key = key;
        _shape = shape;
        _akadeIndexName = akadeIndexName;
        _sourceId = sourceId;
        _table = table;

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
    /// The distinct keys the physical index holds, and only for the whole key: a prefix of a
    /// compound key is a question no Akade structure answers in constant time, and the planner
    /// divides by this number, so a shorter prefix is answered "unknown" rather than estimated.
    /// </summary>
    public long? DistinctCount(int keyPositions) =>
        keyPositions == Descriptor.Columns.Count ? _distinctKeys : null;

    private IEnumerable<T> Hash(IndexKeyRange range)
    {
        if (!range.LowerInclusive
            || !range.UpperInclusive
            || range.Lower.Count != Descriptor.Columns.Count
            || range.Upper.Count != Descriptor.Columns.Count
            || !range.Lower.Zip(range.Upper).All(pair => Equals(pair.First, pair.Second)))
        {
            throw NonEqualityHashRange(range);
        }

        if (HasNullBound(range))
        {
            // SQL equality against NULL cannot match a non-null indexed member.
            return [];
        }

        return _set.Where(_key, _shape.Fill(range.Lower, useMaximum: false), _akadeIndexName);
    }

    /// <summary>
    /// A hash index is allowed to refuse a non-equality range, and says so in the engine's own
    /// vocabulary for "a source was asked something it does not serve".
    /// </summary>
    private SourceContractException NonEqualityHashRange(IndexKeyRange range) =>
        new(
            _sourceId,
            _table,
            $"Akade compound hash index '{Descriptor.Name}' answers equality on all "
            + $"{Descriptor.Columns.Count} key column(s) and was asked for {range}.");

    private IEnumerable<T> Ordered(IndexKeyRange range)
    {
        if (range.BoundedColumns > Descriptor.Columns.Count)
        {
            throw new InvalidOperationException(
                $"Akade compound index '{Descriptor.Name}' has {Descriptor.Columns.Count} key "
                + $"column(s) and was asked to bound {range.BoundedColumns}.");
        }

        if (HasNullBound(range))
        {
            return [];
        }

        var hasLower = range.Lower.Count != 0;
        var hasUpper = range.Upper.Count != 0;

        if (!hasLower && !hasUpper)
        {
            return InKeyOrder(_set.OrderBy(_key, 0, _akadeIndexName));
        }

        if (_set.Count == 0)
        {
            return [];
        }

        // One side open becomes the index's own extreme on that side, because Akade's one-sided
        // shapes do not honour the comparer the index was built with (D281).
        var start = hasLower ? LowerKey(range) : _set.Min(_key, _akadeIndexName);
        var end = hasUpper ? UpperKey(range) : _set.Max(_key, _akadeIndexName);

        // A range whose start is past its end matches nothing. Chalk says so; Akade throws, so the
        // empty case is answered here rather than by an exception.
        if (_shape.Comparer.Compare(start, end) > 0)
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
    /// Akade's enumeration, yielded as it comes and verified as it goes, exactly as the scalar
    /// adapter does (D277) — with the composed tuple comparer as the order it checks against.
    /// </summary>
    internal IEnumerable<T> InKeyOrder(IEnumerable<T> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var previous = default(TKey)!;
        var hasPrevious = false;

        foreach (var row in rows)
        {
            var key = _key(row);
            if (hasPrevious && _shape.Comparer.Compare(previous, key) > 0)
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

    private TKey LowerKey(IndexKeyRange range) =>
        _shape.Fill(
            range.Lower,
            useMaximum: range.Lower.Count < Descriptor.Columns.Count && !range.LowerInclusive);

    private TKey UpperKey(IndexKeyRange range) =>
        _shape.Fill(
            range.Upper,
            useMaximum: range.Upper.Count == Descriptor.Columns.Count || range.UpperInclusive);

    /// <summary>
    /// Whether any bound is NULL. A range never matches a NULL in a column it bounds, whichever way
    /// the bound points, so such a range matches nothing at all.
    /// </summary>
    private static bool HasNullBound(IndexKeyRange range)
    {
        for (var i = 0; i < range.Lower.Count; i++)
        {
            if (range.Lower[i] is null)
            {
                return true;
            }
        }

        for (var i = 0; i < range.Upper.Count; i++)
        {
            if (range.Upper[i] is null)
            {
                return true;
            }
        }

        return false;
    }
}

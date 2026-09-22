using System.Globalization;
using Akade.IndexedSet;
using Chalk.Catalog;
using Chalk.Sources;
using Chalk.Sources.Poco;
using IndexKind = Chalk.Ir.IndexKind;

namespace Chalk.Sources.Akade;

/// <summary>
/// Adapts one scalar, direct-member Akade hash/range index to Chalk's host-index contract.
/// </summary>
/// <remarks>
/// Discovery deliberately limits this adapter to key types whose equality/order semantics currently
/// match Chalk's catalog contract. Computed, compound, nullable and comparer-sensitive keys are not
/// registered here; they remain available to the host through Akade itself.
/// </remarks>
internal sealed class AkadeScalarIndex<T, TKey> : IPocoIndex<T>
    where TKey : notnull
{
    private readonly IndexedSet<T> _set;
    private readonly Func<T, TKey> _key;
    private readonly string _akadeIndexName;
    private readonly string _sourceId;
    private readonly string _table;
    private readonly IComparer<TKey> _comparer = Comparer<TKey>.Default;

    public AkadeScalarIndex(
        IndexDescriptor descriptor,
        IndexedSet<T> set,
        Func<T, TKey> key,
        string akadeIndexName,
        string sourceId,
        string table)
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

    public long? DistinctCount(int keyPositions) =>
        Descriptor.Unique && keyPositions == 1
            ? _set.Count
            : null;

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

    private InvalidOperationException NonEqualityHashRange(IndexKeyRange range) =>
        new(
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

        IEnumerable<T> rows;

        if (hasLower && hasUpper)
        {
            if (_comparer.Compare(lower, upper) > 0)
            {
                return [];
            }

            rows = _set.Range(
                _key,
                lower,
                upper,
                range.LowerInclusive,
                range.UpperInclusive,
                _akadeIndexName);
        }
        else if (hasLower)
        {
            rows = range.LowerInclusive
                ? _set.GreaterThanOrEqual(_key, lower, _akadeIndexName)
                : _set.GreaterThan(_key, lower, _akadeIndexName);
        }
        else if (hasUpper)
        {
            rows = range.UpperInclusive
                ? _set.LessThanOrEqual(_key, upper, _akadeIndexName)
                : _set.LessThan(_key, upper, _akadeIndexName);
        }
        else
        {
            // The whole-range ordered scan. Akade documents OrderBy as "the order defined by the
            // index", which is exactly the contract; the range shapes above have no such promise,
            // which is what the guard below is for.
            rows = _set.OrderBy(_key, 0, _akadeIndexName);
        }

        return InKeyOrder(rows);
    }

    /// <summary>
    /// Akade's enumeration, yielded as it comes and verified as it goes (D277).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Chalk's ORDERED contract is about the rows a lookup returns, and Akade documents
    /// <c>OrderBy</c> as the index's order but says nothing about the enumeration order of a range
    /// query. Sorting the matched rows defensively would cost the whole range before the first row —
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
                    + "lookup must arrive in ascending key order.");
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

        if (value is TKey exact)
        {
            key = exact;
            return true;
        }

        try
        {
            var converted = Convert.ChangeType(
                value,
                typeof(TKey),
                CultureInfo.InvariantCulture);

            if (converted is TKey typed)
            {
                key = typed;
                return true;
            }
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
        {
            // Fall through to the source-contract failure below.
        }

        throw new InvalidOperationException(
            $"Akade index key '{DescriptorTypeName()}' cannot consume a bound value of CLR type "
            + $"{value.GetType().FullName}.");
    }

    private static string DescriptorTypeName() => typeof(TKey).FullName ?? typeof(TKey).Name;
}

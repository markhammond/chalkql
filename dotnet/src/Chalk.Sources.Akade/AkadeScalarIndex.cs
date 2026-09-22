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
    private readonly IComparer<TKey> _comparer = Comparer<TKey>.Default;

    public AkadeScalarIndex(
        IndexDescriptor descriptor,
        IndexedSet<T> set,
        Func<T, TKey> key,
        string akadeIndexName)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(akadeIndexName);

        Descriptor = descriptor;
        _set = set;
        _key = key;
        _akadeIndexName = akadeIndexName;
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
            rows = _set.FullScan();
        }

        // Chalk's ORDERED contract concerns the rows returned by the lookup. Akade documents its
        // range index as supporting indexed ordering, but does not make Range(...) enumeration order
        // part of the public contract. Sort the matched rows explicitly for now rather than relying
        // on an implementation detail; this can later be replaced by a dedicated ordered-range path.
        return rows.OrderBy(_key, _comparer);
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

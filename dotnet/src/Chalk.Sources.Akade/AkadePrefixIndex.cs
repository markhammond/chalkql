using Akade.IndexedSet;
using Chalk.Catalog;
using Chalk.Sources;
using Chalk.Sources.Poco;
using IndexKind = Chalk.Ir.IndexKind;

namespace Chalk.Sources.Akade;

/// <summary>
/// Adapts one Akade prefix index — a trie over a direct <see cref="string"/> member — to Chalk's
/// PREFIX contract (D282).
/// </summary>
/// <remarks>
/// <para>
/// The duck typing the structure invites happens once, at discovery, rather than per query: a
/// <c>PrefixIndex</c> becomes an <c>INDEX_KIND_PREFIX</c> index in the catalog, and the planner then
/// sends it the one thing it can answer — <c>col LIKE 'p%'</c> as a prefix range. Anything else is
/// refused by name, and the planner never sends one.
/// </para>
/// <para>
/// The trie's <c>FuzzyStartsWith</c> is not an access path: no range in Chalk's IR says "within one
/// edit of", so there is nothing for a plan to ask for.
/// </para>
/// </remarks>
internal sealed class AkadePrefixIndex<T> : IPocoIndex<T>
{
    private readonly IndexedSet<T> _set;
    private readonly Func<T, string> _key;
    private readonly string _akadeIndexName;
    private readonly string _sourceId;
    private readonly string _table;

    public AkadePrefixIndex(
        IndexDescriptor descriptor,
        IndexedSet<T> set,
        Func<T, string> key,
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

    /// <summary>A trie knows no key counts, and a guess here would be worse than silence.</summary>
    public long? DistinctCount(int keyPositions) => null;

    public IEnumerable<T> Lookup(IndexKeyRange range)
    {
        ArgumentNullException.ThrowIfNull(range);

        if (Descriptor.Kind != IndexKind.Prefix)
        {
            throw new InvalidOperationException(
                $"Akade index '{Descriptor.Name}' has unsupported Chalk kind {Descriptor.Kind}.");
        }

        if (range.Prefix is null)
        {
            throw new SourceContractException(
                _sourceId,
                _table,
                $"Akade prefix index '{Descriptor.Name}' answers prefix lookups only, and was asked "
                + $"for {range}. A trie has no bounds and no order to walk between them.");
        }

        return _set.StartsWith(_key, range.Prefix, _akadeIndexName);
    }
}

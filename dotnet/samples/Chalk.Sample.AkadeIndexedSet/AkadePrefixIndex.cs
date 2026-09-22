using Akade.IndexedSet;
using Chalk.Catalog;
using Chalk.Sources;
using Chalk.Sources.Poco;
using IndexKind = Chalk.Ir.IndexKind;

namespace Chalk.Sample.AkadeIndexedSet;

/// <summary>
/// One <see href="https://github.com/akade/Akade.IndexedSet">Akade.IndexedSet</see> prefix index — a
/// trie over a text column — wrapped as an <see cref="IPocoIndex{T}"/> of the PREFIX kind.
/// </summary>
/// <remarks>
/// <para>
/// The PREFIX kind exists for exactly this shape of structure: it answers "which rows have a value
/// starting with this text" and nothing else. It claims no ordering, because a trie hands its
/// matches back in whatever order it walks them, and it is priced as a hash lookup is.
/// </para>
/// <para>
/// So there is one question to answer and one to refuse. <c>WHERE name LIKE 'Int%'</c> reaches here
/// as a range whose <see cref="IndexKeyRange.Prefix"/> is <c>Int</c>; any other range is a shape the
/// planner never sends this index, and is refused by name rather than answered approximately.
/// </para>
/// <para>
/// The trie's fuzzy search is deliberately not offered. Chalk's ranges say "between these bounds"
/// and "starting with this text"; there is no range that says "within one edit of", so there is
/// nothing for a plan to ask for.
/// </para>
/// <para>
/// Snapshot consistency is the same requirement every host index carries, and for the same reason:
/// register a factory so each snapshot builds its own index over that snapshot's rows.
/// </para>
/// </remarks>
public sealed class AkadePrefixIndex<T> : IPocoIndex<T>
{
    private readonly IndexedSet<T> _set;
    private readonly Func<T, string> _key;
    private readonly string _akadeIndexName;

    /// <param name="descriptor">What the catalog will say about this index. Trusted as declared.</param>
    /// <param name="set">The set this index lives in.</param>
    /// <param name="key">The key selector, written exactly as it was at registration.</param>
    /// <param name="akadeIndexName">The accessor's source text, as Akade recorded it.</param>
    public AkadePrefixIndex(
        IndexDescriptor descriptor,
        IndexedSet<T> set,
        Func<T, string> key,
        string akadeIndexName)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(akadeIndexName);

        if (descriptor.Kind != IndexKind.Prefix)
        {
            throw new ArgumentException(
                $"index '{descriptor.Name}' is a trie, which answers prefixes; declare it "
                + $"{IndexKind.Prefix} rather than {descriptor.Kind}.",
                nameof(descriptor));
        }

        Descriptor = descriptor;
        _set = set;
        _key = key;
        _akadeIndexName = akadeIndexName;
    }

    /// <inheritdoc />
    public IndexDescriptor Descriptor { get; }

    /// <inheritdoc />
    /// <remarks>Akade holds its own structures; how much they cost per row is its business.</remarks>
    public long BytesPerRow => -1;

    /// <inheritdoc />
    /// <remarks>A trie knows no key counts, and a guess here would be worse than silence.</remarks>
    public long? DistinctCount(int keyPositions) => null;

    /// <inheritdoc />
    public IEnumerable<T> Lookup(IndexKeyRange range)
    {
        ArgumentNullException.ThrowIfNull(range);

        if (range.Prefix is null)
        {
            throw new SourceContractException(
                "akade",
                Descriptor.Name,
                $"this is a prefix index and answers prefix lookups only, but the plan asked for "
                + $"{range}.");
        }

        return _set.StartsWith(_key, range.Prefix, _akadeIndexName);
    }
}

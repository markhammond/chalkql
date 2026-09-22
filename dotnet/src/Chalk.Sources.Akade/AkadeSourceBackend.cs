using Akade.IndexedSet;
using Akade.IndexedSet.Concurrency;
using Chalk.Sources.Poco;

namespace Chalk.Sources.Akade;

/// <summary>
/// The seam between the Akade source and the existing POCO execution backend.
/// Index discovery/registration is version-sensitive reflection and deliberately lives behind the
/// AkadeIndexPlan rather than in the source itself.
/// </summary>
internal interface IAkadeSetBackend<T, in TSet>
    where TSet : class
{
    int Count(TSet set);

    IEnumerable<T> FullScan(TSet set);

    void ValidateTopology(TSet set);

    PocoSource Build(TSet set, AkadeSourceOptions<T> options);
}

internal sealed class IndexedSetBackend<T> :
    IAkadeSetBackend<T, IndexedSet<T>>
{
    private readonly AkadeIndexPlan<T, IndexedSet<T>> _indexes;

    private IndexedSetBackend(AkadeIndexPlan<T, IndexedSet<T>> indexes) =>
        _indexes = indexes;

    public static IndexedSetBackend<T> Discover(
        IndexedSet<T> initial,
        AkadeSourceOptions<T> options) =>
        new(AkadeIndexDiscovery.Discover(initial, options));

    public int Count(IndexedSet<T> set) => set.Count;

    public IEnumerable<T> FullScan(IndexedSet<T> set) => set.FullScan();

    public void ValidateTopology(IndexedSet<T> set) => _indexes.Validate(set);

    public PocoSource Build(IndexedSet<T> set, AkadeSourceOptions<T> options)
    {
        var rows = new IndexedSetRows<T>(set);
        return BuildPoco(rows, set, options, _indexes);
    }

    private static PocoSource BuildPoco(
        IndexedSetRows<T> rows,
        IndexedSet<T> set,
        AkadeSourceOptions<T> options,
        AkadeIndexPlan<T, IndexedSet<T>> indexes)
    {
        var builder = new PocoSourceBuilder(options.SourceId, options.SchemaName)
            .NamingPolicy(options.NamingPolicy)
            .DefaultDecimalScale(options.DecimalScale)
            .AddTable(
                options.TableName,
                rows,
                table =>
                {
                    options.ConfigureTable?.Invoke(table);
                    indexes.Register(table, set);
                });

        return builder.Build();
    }
}

internal sealed class ConcurrentIndexedSetBackend<T> :
    IAkadeSetBackend<T, ConcurrentIndexedSet<T>>
{
    private readonly AkadeIndexPlan<T, ConcurrentIndexedSet<T>> _indexes;

    private ConcurrentIndexedSetBackend(
        AkadeIndexPlan<T, ConcurrentIndexedSet<T>> indexes) =>
        _indexes = indexes;

    public static ConcurrentIndexedSetBackend<T> Discover(
        ConcurrentIndexedSet<T> initial,
        AkadeSourceOptions<T> options) =>
        new(AkadeIndexDiscovery.Discover(initial, options));

    public int Count(ConcurrentIndexedSet<T> set) => set.Count;

    public IEnumerable<T> FullScan(ConcurrentIndexedSet<T> set) =>
        ConcurrentIndexedSetAccess.CaptureForQuiescentRead(set).FullScan();

    public void ValidateTopology(ConcurrentIndexedSet<T> set) => _indexes.Validate(set);

    public PocoSource Build(
        ConcurrentIndexedSet<T> set,
        AkadeSourceOptions<T> options)
    {
        // Capture once for this Chalk snapshot. Scans, statistics and Akade index adapters should
        // all address this same underlying IndexedSet instance.
        var inner = ConcurrentIndexedSetAccess.CaptureForQuiescentRead(set);
        var rows = new ConcurrentIndexedSetRows<T>(set, inner);

        var builder = new PocoSourceBuilder(options.SourceId, options.SchemaName)
            .NamingPolicy(options.NamingPolicy)
            .DefaultDecimalScale(options.DecimalScale)
            .AddTable(
                options.TableName,
                rows,
                table =>
                {
                    options.ConfigureTable?.Invoke(table);
                    _indexes.Register(table, inner);
                });

        return builder.Build();
    }
}

internal sealed class IndexedSetRows<T> : IReadOnlyCollection<T>
{
    public IndexedSetRows(IndexedSet<T> set) => Set = set;

    public IndexedSet<T> Set { get; }

    public int Count => Set.Count;

    public IEnumerator<T> GetEnumerator() => Set.FullScan().GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
        GetEnumerator();
}

internal sealed class ConcurrentIndexedSetRows<T> : IReadOnlyCollection<T>
{
    public ConcurrentIndexedSetRows(
        ConcurrentIndexedSet<T> set,
        IndexedSet<T> inner)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(inner);

        Set = set;
        Inner = inner;
    }

    public ConcurrentIndexedSet<T> Set { get; }

    /// <summary>
    /// The wrapped IndexedSet captured via ConcurrentIndexedSet.Read. Chalk deliberately reads this
    /// outside Akade's reader lock under the CHALK002 host-quiescence contract.
    /// </summary>
    internal IndexedSet<T> Inner { get; }

    public int Count => Inner.Count;

    public IEnumerator<T> GetEnumerator() => Inner.FullScan().GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
        GetEnumerator();
}

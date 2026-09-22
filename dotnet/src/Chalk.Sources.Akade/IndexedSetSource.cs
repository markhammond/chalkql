using Akade.IndexedSet;

namespace Chalk.Sources.Akade;

/// <summary>
/// A one-table Chalk source over an ordinary Akade IndexedSet.
///
/// The host may mutate the set directly between Chalk reads. Chalk deliberately borrows the set
/// without copying it, so the host must not mutate it while an execution, scoped refresh or
/// transactional-refresh preparation that may read this source is in flight. Catalog statistics
/// remain those of the last build/scoped refresh. RefreshBuilder.Replace/Append are the stronger
/// publication path and require an editor so Chalk can build a fresh successor.
/// </summary>
public sealed class IndexedSetSource<T> : AkadeSetSource<T, IndexedSet<T>>
{
    private readonly IndexedSetEditor<T>? _editor;

    internal IndexedSetSource(
        AkadeSourceOptions<T> options,
        Func<IndexedSet<T>> registration,
        IndexedSetEditor<T>? editor,
        IAkadeSetBackend<T, IndexedSet<T>> initialPlan,
        IndexedSet<T> initial)
        : base(options, registration, initialPlan, initial)
    {
        _editor = editor;
    }

    private protected override bool CanPrepareRowRefresh => _editor is not null;

    private protected override IndexedSet<T> BuildSuccessor(IReadOnlyList<T> rows) =>
        _editor!.Build(rows);

    private protected override IAkadeSetBackend<T, IndexedSet<T>> DiscoverBackend(
        IndexedSet<T> set) =>
        IndexedSetBackend<T>.Discover(set, Options);
}

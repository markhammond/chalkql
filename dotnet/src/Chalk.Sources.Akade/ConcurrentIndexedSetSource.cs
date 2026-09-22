using Akade.IndexedSet;
using Akade.IndexedSet.Concurrency;

namespace Chalk.Sources.Akade;

/// <summary>
/// A one-table Chalk source over Akade's ReaderWriterLock-backed ConcurrentIndexedSet.
/// </summary>
/// <remarks>
/// Chalk deliberately does not participate in ConcurrentIndexedSet's copy-on-read protocol during
/// execution. The wrapped IndexedSet is captured through Akade's public Read callback and then read
/// directly so Chalk can retain streaming/zero-copy behaviour.
///
/// The host must therefore ensure that no mutation overlaps a Chalk execution or scoped refresh that
/// may read this source. This is the CHALK002 contract. RefreshBuilder.Replace/Append still construct
/// and publish a fresh successor; they do not mutate the currently published set.
/// </remarks>
public sealed class ConcurrentIndexedSetSource<T> :
    AkadeSetSource<T, ConcurrentIndexedSet<T>>
{
    private readonly ConcurrentIndexedSetEditor<T>? _editor;

    internal ConcurrentIndexedSetSource(
        AkadeSourceOptions<T> options,
        Func<ConcurrentIndexedSet<T>> registration,
        ConcurrentIndexedSetEditor<T>? editor,
        IAkadeSetBackend<T, ConcurrentIndexedSet<T>> initialPlan,
        ConcurrentIndexedSet<T> initial)
        : base(options, registration, initialPlan, initial)
    {
        _editor = editor;
    }

    private protected override bool CanPrepareRowRefresh => _editor is not null;

    private protected override ConcurrentIndexedSet<T> BuildSuccessor(IReadOnlyList<T> rows) =>
        _editor!.Build(rows);

    private protected override IAkadeSetBackend<T, ConcurrentIndexedSet<T>> DiscoverBackend(
        ConcurrentIndexedSet<T> set) =>
        ConcurrentIndexedSetBackend<T>.Discover(set, Options);
}

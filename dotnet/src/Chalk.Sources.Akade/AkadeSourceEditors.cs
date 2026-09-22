using Akade.IndexedSet;
using Akade.IndexedSet.Concurrency;

namespace Chalk.Sources.Akade;

/// <summary>
/// Factory for the fresh ordinary IndexedSet required by transactional RefreshBuilder.Replace/Append.
/// The input is the exact logical row sequence the successor must contain.
/// </summary>
/// <remarks>
/// This is not the ordinary mutation API for the source. A host may mutate its published IndexedSet
/// directly and accept live-data semantics. The editor exists only for Chalk's stronger row-refresh
/// protocol, where old executions must retain the old set while new executions see the successor.
/// </remarks>
public sealed class IndexedSetEditor<T>
{
    private readonly Func<IReadOnlyList<T>, IndexedSet<T>> _build;

    public IndexedSetEditor(Func<IReadOnlyList<T>, IndexedSet<T>> build)
    {
        ArgumentNullException.ThrowIfNull(build);
        _build = build;
    }

    internal IndexedSet<T> Build(IReadOnlyList<T> rows)
    {
        var set = _build(rows);
        return set ?? throw new InvalidOperationException(
            $"The {nameof(IndexedSetEditor<T>)} returned null.");
    }
}

/// <summary>
/// Factory for the fresh ConcurrentIndexedSet required by transactional
/// RefreshBuilder.Replace/Append.
/// </summary>
/// <remarks>
/// The factory may use ConcurrentIndexedSet.Update internally while assembling the unpublished
/// successor. It must return a fresh set. Direct host updates to the published concurrent set remain
/// valid live-data operations and do not pass through this editor.
/// </remarks>
public sealed class ConcurrentIndexedSetEditor<T>
{
    private readonly Func<IReadOnlyList<T>, ConcurrentIndexedSet<T>> _build;

    public ConcurrentIndexedSetEditor(Func<IReadOnlyList<T>, ConcurrentIndexedSet<T>> build)
    {
        ArgumentNullException.ThrowIfNull(build);
        _build = build;
    }

    internal ConcurrentIndexedSet<T> Build(IReadOnlyList<T> rows)
    {
        var set = _build(rows);
        return set ?? throw new InvalidOperationException(
            $"The {nameof(ConcurrentIndexedSetEditor<T>)} returned null.");
    }
}

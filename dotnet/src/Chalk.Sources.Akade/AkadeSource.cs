using System.Diagnostics.CodeAnalysis;
using Akade.IndexedSet;
using Akade.IndexedSet.Concurrency;

namespace Chalk.Sources.Akade;

/// <summary>
/// Type-inferred entry point for the one IndexedSet -> one source -> one table model.
/// </summary>
public static class AkadeSource
{
    /// <summary>
    /// Binds a fixed ordinary IndexedSet instance. The host may mutate that instance directly and
    /// accept live-data semantics. Scoped refreshes re-describe the same instance.
    /// </summary>
    public static IndexedSetSourceBuilder<T> From<T>(
        string sourceId,
        IndexedSet<T> set)
    {
        ArgumentNullException.ThrowIfNull(set);
        return new IndexedSetSourceBuilder<T>(sourceId, () => set);
    }

    /// <summary>
    /// Binds an ordinary IndexedSet registration. Use this form when the host may replace the whole
    /// set instance and wants Refresh(source/table) to re-read the current one.
    /// </summary>
    public static IndexedSetSourceBuilder<T> From<T>(
        string sourceId,
        Func<IndexedSet<T>> set)
    {
        ArgumentNullException.ThrowIfNull(set);
        return new IndexedSetSourceBuilder<T>(sourceId, set);
    }

    /// <summary>
    /// Binds a fixed concurrent set.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Chalk deliberately does not use Akade's normal copy-on-read protocol for execution.
    /// <c>ConcurrentIndexedSet.Read(...)</c> materialises the returned sequence while holding its
    /// reader lock, which would turn a full Chalk scan into an O(n) copy before streaming begins.
    /// </para>
    /// <para>
    /// Instead Chalk captures the wrapped <see cref="IndexedSet{T}"/> through Akade's public
    /// <c>Read</c> callback and subsequently reads that set directly. The host must therefore ensure
    /// that this collection is not mutated while any Chalk execution or scoped refresh that may read
    /// this source is in flight.
    /// </para>
    /// <para>
    /// Prefer <see cref="IndexedSet{T}"/> unless host-enforced read quiescence is intentional. Use
    /// <c>RefreshBuilder.Replace</c> or <c>RefreshBuilder.Append</c> for Chalk-managed publication.
    /// </para>
    /// </remarks>
    [Experimental("CHALK002")]
    public static ConcurrentIndexedSetSourceBuilder<T> From<T>(
        string sourceId,
        ConcurrentIndexedSet<T> set)
    {
        ArgumentNullException.ThrowIfNull(set);
        return new ConcurrentIndexedSetSourceBuilder<T>(sourceId, () => set);
    }

    /// <summary>
    /// Binds a concurrent-set registration so a scoped refresh can re-read a host-swapped instance.
    /// </summary>
    /// <inheritdoc cref="From{T}(string, ConcurrentIndexedSet{T})" path="/remarks"/>
    [Experimental("CHALK002")]
    public static ConcurrentIndexedSetSourceBuilder<T> From<T>(
        string sourceId,
        Func<ConcurrentIndexedSet<T>> set)
    {
        ArgumentNullException.ThrowIfNull(set);
        return new ConcurrentIndexedSetSourceBuilder<T>(sourceId, set);
    }

    // Escape hatches if a call site ever makes the Func<> overloads ambiguous.
    [Experimental("CHALK002")]
    public static ConcurrentIndexedSetSourceBuilder<T> FromConcurrent<T>(
        string sourceId,
        ConcurrentIndexedSet<T> set)
    {
        ArgumentNullException.ThrowIfNull(set);
        return new ConcurrentIndexedSetSourceBuilder<T>(sourceId, () => set);
    }

    [Experimental("CHALK002")]
    public static ConcurrentIndexedSetSourceBuilder<T> FromConcurrent<T>(
        string sourceId,
        Func<ConcurrentIndexedSet<T>> set)
    {
        ArgumentNullException.ThrowIfNull(set);
        return new ConcurrentIndexedSetSourceBuilder<T>(sourceId, set);
    }
}

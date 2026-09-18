using System.Collections.Concurrent;
using Chalk.Sources;

namespace Chalk.Client;

/// <summary>
/// A bounded pool of <see cref="ExecutionArena"/>s: one is rented per execution that does not bring
/// its own (<c>docs/design/08-execution-arena.md</c> §2.1). The engine has one, built from
/// <c>ExecutionOptions.Arena</c> and <c>ExecutionOptions.ArenaPoolSize</c> and reachable as
/// <c>ChalkEngine.Arenas</c>; a host may build its own and pass it to an execution, which is how one
/// workload's warm memory is kept apart from another's (D258).
/// </summary>
/// <remarks>
/// <para>
/// An execution beyond <see cref="Capacity"/> gets a transient arena — correct, but cold, so it pays
/// for its scratch once — and that arena is disposed rather than pooled when it finishes. The worst
/// case a pool can retain is therefore <see cref="WorstCaseBytes"/>, which is what the engine's
/// startup line reports for its own.
/// </para>
/// <para>
/// Retention, trimming and <see cref="ArenaOptions.MaxBytes"/> are per pool by construction: every
/// arena a pool hands out is one it made from its own <see cref="Options"/>, and no arena is ever
/// shared between pools.
/// </para>
/// <para>
/// A pool is safe to rent from concurrently. <see cref="Dispose"/> releases every idle arena and
/// marks the pool closed; an arena still serving an execution is <em>not</em> torn out from under it
/// — it is disposed when that execution hands it back. Under <c>OutputMemory.Pooled</c> the batches
/// a host holds are arena memory, so a pool must outlive the batches taken from it, exactly as a
/// single arena must.
/// </para>
/// </remarks>
public sealed class ArenaPool : IDisposable
{
    private readonly ConcurrentBag<ExecutionArena> _idle = [];

    /// <summary>
    /// Every arena this pool made and kept — the idle ones and the ones out on an execution — so that
    /// <see cref="RetainedBytes"/> can answer for the pool rather than for the part of it that
    /// happens to be idle. Bounded by <see cref="Capacity"/>; transient arenas are not in it.
    /// </summary>
    private readonly ConcurrentBag<ExecutionArena> _made = [];

    private int _pooled;
    private volatile bool _disposed;

    /// <summary>
    /// A pool of at most <paramref name="capacity"/> arenas, each built with
    /// <paramref name="options"/>.
    /// </summary>
    /// <param name="options">How every arena this pool makes is sized and bounded.</param>
    /// <param name="capacity">
    /// How many arenas to keep warm. Values below one are treated as one; an execution beyond this
    /// many at once gets a transient arena that is disposed when it finishes.
    /// </param>
    /// <param name="name">
    /// What to call this pool in statistics and logs. Null — the default, and what the engine's own
    /// pool uses — leaves <c>ExecutionStats.ArenaPoolName</c> unset. It names nothing else: two pools
    /// with the same name are still two pools.
    /// </param>
    public ArenaPool(ArenaOptions options, int capacity, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        Options = options;
        Capacity = Math.Max(1, capacity);
        Name = name;
    }

    /// <summary>What this pool is called in statistics and logs, or null for an unnamed one.</summary>
    public string? Name { get; }

    /// <summary>The options every arena this pool makes is built with.</summary>
    public ArenaOptions Options { get; }

    /// <summary>How many arenas the pool will keep warm.</summary>
    public int Capacity { get; }

    /// <summary>Arenas currently idle in the pool. For tests and diagnostics.</summary>
    public int IdleCount => _idle.Count;

    /// <summary>
    /// The most this pool can hold between executions: <see cref="Capacity"/> arenas each keeping
    /// <see cref="ArenaOptions.RetainBytes"/>. A ceiling, not a measurement —
    /// <see cref="RetainedBytes"/> is what it is actually holding.
    /// </summary>
    public long WorstCaseBytes => Capacity * Options.RetainBytes;

    /// <summary>
    /// What this pool's arenas are holding right now, idle or out on an execution. Zero for a pool
    /// that has never served one, and never more than <see cref="WorstCaseBytes"/> once every
    /// execution has ended and trimmed.
    /// </summary>
    public long RetainedBytes
    {
        get
        {
            long total = 0;
            foreach (var arena in _made)
            {
                total += arena.RetainedBytes;
            }

            return total;
        }
    }

    /// <summary>An arena for one execution, plus whether the caller must dispose it when finished.</summary>
    internal (ExecutionArena Arena, bool Transient) Rent()
    {
        if (_idle.TryTake(out var warm))
        {
            return (warm, false);
        }

        // A pool that has never been full still creates pooled arenas; past that, transient ones,
        // so a burst of concurrency costs memory only while it lasts.
        if (Interlocked.Increment(ref _pooled) <= Capacity)
        {
            var arena = new ExecutionArena(Options);
            _made.Add(arena);
            return (arena, false);
        }

        Interlocked.Decrement(ref _pooled);
        return (new ExecutionArena(Options), true);
    }

    /// <summary>Offers a finished arena back. Trimming has already happened in <c>EndExecution</c>.</summary>
    internal void Return(ExecutionArena arena, bool transient)
    {
        if (transient || _disposed)
        {
            // A pool disposed while this execution was running does not take its arena back: the
            // execution kept it alive to the end, and it is released here rather than refused there.
            arena.Dispose();
            return;
        }

        _idle.Add(arena);
    }

    /// <summary>
    /// Releases every idle arena and closes the pool. An arena still serving an execution is left
    /// alone and disposed when that execution returns it, so disposing a pool never pulls memory out
    /// from under a running query; a host that holds pooled output batches must still dispose those
    /// before the pool that backs them.
    /// </summary>
    public void Dispose()
    {
        _disposed = true;
        while (_idle.TryTake(out var arena))
        {
            arena.Dispose();
        }
    }
}

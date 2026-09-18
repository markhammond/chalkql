using System.Collections.Concurrent;

namespace Chalk.Execution.Operators;

/// <summary>
/// How many remote queries one execution may have in flight, per source and overall (D107,
/// <c>20-m5-federation.md</c> §4).
/// </summary>
/// <remarks>
/// <para>
/// Two ceilings, because they answer to different people. The per-source one is
/// <c>SourceOptions.MaxConcurrentQueries</c>: the adapter author knows how many connections that
/// database will tolerate, and a fan-out must never open more than the provider's pool is sized for.
/// The overall one is <c>ExecutionOptions.MaxRemoteConcurrency</c>: the host knows how much of this
/// machine one query may use, and a fan-out over eight partitions on a two-core box should not open
/// eight connections whatever each source would allow.
/// </para>
/// <para>
/// Zero means unlimited on either, and an execution with no fan-out never touches this at all — the
/// gate is entered per remote stream, not per batch.
/// </para>
/// </remarks>
internal sealed class RemoteGate : IDisposable
{
    private readonly SemaphoreSlim? _overall;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _perSource = new(StringComparer.Ordinal);
    private readonly Func<string, int> _limitFor;

    public RemoteGate(int maxRemoteConcurrency, Func<string, int> limitFor)
    {
        _overall = maxRemoteConcurrency > 0 ? new SemaphoreSlim(maxRemoteConcurrency) : null;
        _limitFor = limitFor;
    }

    /// <summary>A gate that lets everything through, for an execution with no remote source in it.</summary>
    public static RemoteGate Unlimited { get; } = new(0, _ => 0);

    /// <summary>
    /// Waits until this execution may open one more stream against <paramref name="sourceId"/>.
    /// Dispose the returned lease when the stream is done — including when it faulted.
    /// </summary>
    public async ValueTask<Lease> EnterAsync(string sourceId, CancellationToken ct)
    {
        var perSource = _limitFor(sourceId) is var limit && limit > 0
            ? _perSource.GetOrAdd(sourceId, _ => new SemaphoreSlim(limit))
            : null;

        // Overall first, then per source, and always in that order: two fan-outs taking them in
        // opposite orders is the classic way to build a deadlock out of two correct limits.
        if (_overall is not null)
        {
            await _overall.WaitAsync(ct).ConfigureAwait(false);
        }

        if (perSource is not null)
        {
            try
            {
                await perSource.WaitAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                _overall?.Release();
                throw;
            }
        }

        return new Lease(_overall, perSource);
    }

    public void Dispose()
    {
        _overall?.Dispose();
        foreach (var semaphore in _perSource.Values)
        {
            semaphore.Dispose();
        }

        _perSource.Clear();
    }

    /// <summary>One stream's place in the queue. Releasing twice is safe; not releasing is not.</summary>
    internal readonly struct Lease(SemaphoreSlim? overall, SemaphoreSlim? perSource) : IDisposable
    {
        public void Dispose()
        {
            perSource?.Release();
            overall?.Release();
        }
    }
}

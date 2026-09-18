using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Apache.Arrow;
using Chalk.Sources;

namespace Chalk.Execution.Operators;

/// <summary>
/// How every remote stream is read from M5 on (D96, D107, <c>20-m5-federation.md</c> §4): on its own
/// task, producing Arrow batches into a bounded channel the pipeline takes from.
/// </summary>
/// <remarks>
/// <para>
/// The fetch task's whole job is to turn a source's <c>IAsyncEnumerable&lt;RecordBatch&gt;</c> into
/// batches in a channel. It touches no operator's scratch, no column view and no selection vector —
/// the Arrow batch is the boundary, and everything on the pipeline side of it stays single-threaded,
/// which is what D96 means by "the arena model is unchanged". The arena itself is already safe for
/// this: it guards its rentals so that a source may build batches on its own thread, and that is the
/// only thing a fetch task asks of it.
/// </para>
/// <para>
/// The channel is bounded at <c>RemotePrefetchDepth</c> batches, so a fast source cannot pull an
/// unbounded amount of a slow query's result into memory: the writer blocks and the source's own
/// reader stops. A depth of zero turns the whole thing off and reads on the pipeline's thread, which
/// is exactly what M4 did.
/// </para>
/// <para>
/// Cancellation and failure are §5's: the token the caller passes is the execution's linked one, the
/// fetch task observes it, and a fault is re-thrown on the consuming thread <em>before</em> any
/// further batch is yielded — so nothing partial ever reaches a caller that is about to be told the
/// query failed.
/// </para>
/// </remarks>
internal static class RemoteFetch
{
    /// <summary>
    /// One remote query's batches. The caller disposes each batch, exactly as it does when it
    /// enumerates a source directly.
    /// </summary>
    public static IAsyncEnumerable<RecordBatch> RunAsync(
        ISourceRuntime source,
        RemoteQueryRequest request,
        OperatorContext context,
        int prefetchDepth,
        CancellationToken ct,
        string? redactedSubject = null)
    {
        var stream = source.ExecuteQueryAsync(request, OperatorBase.ScanContextFor(context), ct);
        if (redactedSubject is not null)
        {
            stream = Redacted(stream, source.SourceId, redactedSubject, ct);
        }

        return prefetchDepth <= 0 ? Direct(stream, ct) : Prefetched(stream, prefetchDepth, ct);
    }

    /// <summary>
    /// The adapter's failure, re-attributed to the query text with its literals redacted (D262,
    /// <c>docs/design/37-redacted-sql.md</c> §1). An adapter names what it was running, and what it
    /// was running is the pushed SQL with this statement's literals folded in — so a host that
    /// redacts its logs and then catches one of these would otherwise have the values back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <em>subject</em> is replaced and nothing else: the provider's own exception stays as the
    /// inner one, so a host loses no detail about what the source actually said.
    /// </para>
    /// <para>
    /// Present only when the engine's redaction is on, so nothing here is on the path of a host that
    /// did not ask for it — and even then it is one enumerator per remote stream and nothing per
    /// row. The redacted text itself was computed at prepare (ADR 0043 §5).
    /// </para>
    /// </remarks>
    private static async IAsyncEnumerable<RecordBatch> Redacted(
        IAsyncEnumerable<RecordBatch> stream,
        string sourceId,
        string redactedSubject,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await using var batches = stream.GetAsyncEnumerator(ct);
        while (true)
        {
            RecordBatch batch;
            try
            {
                if (!await batches.MoveNextAsync().ConfigureAwait(false))
                {
                    break;
                }

                batch = batches.Current;
            }
            catch (SourceExecutionException failure)
            {
                throw new SourceExecutionException(sourceId, redactedSubject, failure.InnerException);
            }
            catch (SourceTimeoutException failure)
            {
                throw new SourceTimeoutException(
                    sourceId, redactedSubject, failure.Timeout, failure.InnerException);
            }

            yield return batch;
        }
    }

    /// <summary>The M4 path: read on the pipeline's own thread.</summary>
    private static async IAsyncEnumerable<RecordBatch> Direct(
        IAsyncEnumerable<RecordBatch> stream,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var batch in stream.WithCancellation(ct).ConfigureAwait(false))
        {
            yield return batch;
        }
    }

    /// <summary>
    /// The M5 path: a task ahead of the pipeline by at most <paramref name="depth"/> batches.
    /// </summary>
    private static async IAsyncEnumerable<RecordBatch> Prefetched(
        IAsyncEnumerable<RecordBatch> stream,
        int depth,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateBounded<RecordBatch>(
            new BoundedChannelOptions(depth)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait,
            });

        var fetch = Task.Run(
            async () =>
            {
                try
                {
                    await foreach (var batch in stream.WithCancellation(ct).ConfigureAwait(false))
                    {
                        try
                        {
                            await channel.Writer.WriteAsync(batch, ct).ConfigureAwait(false);
                        }
                        catch
                        {
                            // The reader is gone — abandoned, cancelled, or faulted elsewhere. This
                            // batch is the fetch task's to release; nothing else knows about it.
                            batch.Dispose();
                            throw;
                        }
                    }

                    channel.Writer.TryComplete();
                }
                catch (Exception failure)
                {
                    channel.Writer.TryComplete(failure);
                }
            },
            CancellationToken.None);

        try
        {
            while (true)
            {
                RecordBatch batch;
                try
                {
                    if (!await channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
                    {
                        break;
                    }

                    if (!channel.Reader.TryRead(out batch!))
                    {
                        continue;
                    }
                }
                catch (ChannelClosedException closed) when (closed.InnerException is not null)
                {
                    // The fetch task's failure, re-thrown here with its own attribution intact: an
                    // adapter's SourceExecutionException must reach the caller as itself.
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo
                        .Capture(closed.InnerException)
                        .Throw();
                    throw;
                }

                yield return batch;
            }
        }
        finally
        {
            // Drain whatever the writer had already handed over: those batches are nobody else's,
            // and a fan-out that fails on one source must not leak the batches of another.
            channel.Writer.TryComplete();
            while (channel.Reader.TryRead(out var orphan))
            {
                orphan.Dispose();
            }

            await Complete(fetch).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Waits for the fetch task, swallowing the cancellation it was told to observe. A fault it
    /// reported has already reached the consumer through the channel; re-throwing it here would
    /// replace the first failure with the second.
    /// </summary>
    private static async ValueTask Complete(Task fetch)
    {
        try
        {
            await fetch.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
    }
}

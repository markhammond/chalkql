using System.Threading.Channels;
using Chalk.Catalog;
using Chalk.Execution.Vectors;
using Chalk.Sources;
using ArrowSchema = Apache.Arrow.Schema;

namespace Chalk.Execution.Operators;

/// <summary>
/// The fan-out (D107, <c>docs/design/20-m5-federation.md</c> §4): a partitioned table's branches,
/// read concurrently, emitted as they arrive.
/// </summary>
/// <remarks>
/// <para>
/// Semantically a <c>UNION ALL</c> — every row of every branch, duplicates kept, <b>no ordering
/// claimed</b>, which the plan says by carrying no collation. What it adds over the ordinary set
/// operator is concurrency and memory: the branches run at once, bounded by
/// <c>MaxRemoteConcurrency</c> overall and by each source's own <c>MaxConcurrentQueries</c>, and the
/// partition value behind each branch is kept so a key set bound in this node's scope can prune
/// branches that cannot hold a matching row.
/// </para>
/// <para>
/// Each branch is a whole operator subtree — usually a remote query, sometimes a local scan — and
/// each runs on its own task. What crosses between them is a <em>handover</em>, not a copy: the
/// branch offers its batch and then waits until the consumer says it is finished with it, so the
/// batch is never read after its producer's next pull and nothing is duplicated. The concurrency
/// that matters is still there — while one branch's batch is being consumed the others are fetching,
/// which is what <see cref="RemoteFetch"/>'s prefetch channel is for one level down.
/// </para>
/// <para>
/// Failure and cancellation are §5's: one linked token for every branch, the first failure cancels
/// its siblings, the enumerator throws once with the source named, and nothing is yielded after the
/// fault.
/// </para>
/// </remarks>
internal sealed class PartitionedScanOperator : OperatorBase
{
    private readonly IBatchOperator[] _branches;
    private readonly IReadOnlyList<ScalarValue?> _values;
    private readonly ColumnarBatch _output;

    public PartitionedScanOperator(
        OperatorContext context,
        string path,
        ArrowSchema schema,
        IReadOnlyList<ChalkType> columnTypes,
        IReadOnlyList<Func<OperatorContext, IBatchOperator>> partitions,
        IReadOnlyList<ScalarValue?> values)
        : base(context, schema, columnTypes, path)
    {
        // Built here rather than in RunAsync: an operator's plan-shaped scratch registers itself with
        // the ambient ScratchScope as it is constructed (D64), and that scope exists only while the
        // tree is being built. A branch built at execution time would hold copiers no arena ever
        // acquired, and would quietly produce nothing.
        _branches = [.. partitions.Select(p => p(context))];
        _values = values;
        _output = NewOutput();
    }

    /// <summary>The partition-column value each branch holds, or null where it is a range.</summary>
    public IReadOnlyList<ScalarValue?> PartitionValues => _values;

    protected override async ValueTask DisposeCoreAsync()
    {
        foreach (var branch in _branches)
        {
            await branch.DisposeAsync().ConfigureAwait(false);
        }
    }

    protected override async IAsyncEnumerable<ColumnarBatch> RunAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var branches = _branches;

        // One linked token for the whole fan-out (D108): the caller's cancellation reaches every
        // branch, and the first branch to fail cancels the rest.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var channel = Channel.CreateUnbounded<Handover>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        var running = branches.Length;
        var faults = new List<Exception>();
        var faultGate = new Lock();
        var tasks = new List<Task>(branches.Length);

        foreach (var branch in branches)
        {
            var local = branch;
            tasks.Add(Task.Run(
                async () =>
                {
                    using var ready = new SemaphoreSlim(0, 1);
                    try
                    {
                        await foreach (var batch in local.ExecuteAsync(linked.Token).ConfigureAwait(false))
                        {
                            await channel.Writer
                                .WriteAsync(new Handover(batch, ready), linked.Token)
                                .ConfigureAwait(false);

                            // The batch is this branch's own slot and stays valid until its next
                            // pull, so the next pull waits for the consumer to be done with it.
                            await ready.WaitAsync(linked.Token).ConfigureAwait(false);
                        }
                    }
                    catch (Exception failure)
                    {
                        lock (faultGate)
                        {
                            faults.Add(failure);
                        }

                        // The first failure cancels its siblings: a fan-out that has already lost
                        // one source should not keep three others working for an answer nobody will
                        // be given.
                        await linked.CancelAsync().ConfigureAwait(false);
                    }
                    finally
                    {
                        if (Interlocked.Decrement(ref running) == 0)
                        {
                            channel.Writer.TryComplete();
                        }
                    }
                },
                CancellationToken.None));
        }

        try
        {
            // The caller's token, not None: a cancelled execution stops here rather than waiting for
            // every branch to notice, and the grace period below is what waits for them.
            while (await channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (channel.Reader.TryRead(out var handover))
                {
                    try
                    {
                        // Nothing is yielded after a fault: the exception is raised before the batch
                        // that was already in the channel is handed on.
                        ThrowIfFaulted(faults, faultGate);
                        ct.ThrowIfCancellationRequested();
                        _output.Adopt(handover.Batch);
                    }
                    catch
                    {
                        handover.Release();
                        throw;
                    }

                    yield return _output;
                    handover.Release();
                }
            }

            ThrowIfFaulted(faults, faultGate);
            ct.ThrowIfCancellationRequested();
        }
        finally
        {
            await linked.CancelAsync().ConfigureAwait(false);
            while (channel.Reader.TryRead(out var orphan))
            {
                orphan.Release();
            }

            // The grace period (D108): a branch that has not noticed the token by now is abandoned
            // rather than aborted — there is no safe way to stop a thread inside a driver — and the
            // fact that it did not notice is worth saying out loud.
            await AwaitBranches(tasks, Context.Settings.CancellationGracePeriod).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Waits for every branch task to observe the cancellation, up to the grace period. Never throws:
    /// whatever a branch failed with has already been reported, and this is the tidy-up.
    /// </summary>
    private static async ValueTask AwaitBranches(List<Task> tasks, TimeSpan grace)
    {
        try
        {
            var all = Task.WhenAll(tasks);
            var finished = await Task.WhenAny(all, Task.Delay(grace, CancellationToken.None))
                .ConfigureAwait(false);
            if (ReferenceEquals(finished, all))
            {
                await all.ConfigureAwait(false);
            }
        }
        catch
        {
            // Reported already, or a cancellation the caller asked for.
        }
    }

    /// <summary>
    /// Re-raises the first failure. One exception, whatever went wrong first — the enumerator throws
    /// once (§5), and an aggregate of four cancellations would bury the one thing that happened.
    /// </summary>
    private static void ThrowIfFaulted(List<Exception> faults, Lock gate)
    {
        Exception? first = null;
        lock (gate)
        {
            foreach (var failure in faults)
            {
                if (failure is not OperationCanceledException)
                {
                    first = failure;
                    break;
                }

                first ??= failure;
            }
        }

        if (first is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(first).Throw();
        }
    }

    /// <summary>
    /// One batch offered by a branch, and the signal that says the consumer is finished with it.
    /// Releasing twice is harmless; not releasing would stall that branch, which is why every path
    /// out of the loop releases.
    /// </summary>
    private readonly struct Handover(ColumnarBatch batch, SemaphoreSlim ready)
    {
        public ColumnarBatch Batch => batch;

        public void Release()
        {
            try
            {
                ready.Release();
            }
            catch (ObjectDisposedException)
            {
                // The branch gave up first.
            }
            catch (SemaphoreFullException)
            {
            }
        }
    }
}

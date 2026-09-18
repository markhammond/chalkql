using Chalk.Catalog;
using Chalk.Sources;
using ArrowSchema = Apache.Arrow.Schema;

namespace Chalk.Execution.Operators;

/// <summary>
/// The synchronous pull path of <c>15-zero-allocation-execution.md</c> §3 (D63): a consumer calls
/// <see cref="TryNext"/> and only awaits <see cref="WaitAsync"/> when its input reports that it is
/// not ready, which the in-process sources never do.
/// </summary>
/// <remarks>
/// Awaiting an <em>incomplete</em> <see cref="ValueTask"/> is the one place the async machinery
/// allocates. Confining it here means a POCO-only pipeline allocates nothing at all between its
/// first and last batch, and a genuinely asynchronous source (M5's remote ones) still works by
/// answering "not ready" and being awaited.
/// </remarks>
internal interface IBatchPull : IAsyncDisposable
{
    /// <summary>
    /// The next batch, if one is ready. False means either the end — <see cref="IsDone"/> is then
    /// true — or that the input has to be awaited through <see cref="WaitAsync"/>.
    /// </summary>
    bool TryNext(out ColumnarBatch? batch);

    /// <summary>True once no more batches will come. Only meaningful after <see cref="TryNext"/> said false.</summary>
    bool IsDone { get; }

    /// <summary>
    /// Completes the pull <see cref="TryNext"/> could not. True when <see cref="Current"/> holds a
    /// batch, false at the end.
    /// </summary>
    ValueTask<bool> WaitAsync(CancellationToken ct);

    /// <summary>The batch the last successful pull produced.</summary>
    ColumnarBatch? Current { get; }
}

/// <summary>
/// One node of the running plan. Composition is a tree built by <c>PlanCompiler</c>; the leaf is a
/// scan and the blocking operators consume their input fully before yielding (§6.2).
/// </summary>
/// <remarks>
/// Pull, single-threaded — asynchrony only matters at sources (D22, §12). The interface is internal
/// precisely so morsel-driven parallelism can change it later without breaking a host.
/// <para>
/// Ownership since step 20 (D61): an operator owns its output <see cref="ColumnarBatch"/> and the
/// arena buffers behind it, and a batch it yields is valid until the next pull. Nothing is disposed
/// between operators any more, because nothing between them is owned; anything that must outlive a
/// batch copies, which is what the blocking operators already do.
/// </para>
/// </remarks>
internal interface IBatchOperator : IBatchPull
{
    /// <summary>The schema of every batch this operator yields.</summary>
    ArrowSchema Schema { get; }

    /// <summary>The logical column types, in order — what a view of each column carries.</summary>
    IReadOnlyList<ChalkType> ColumnTypes { get; }

    /// <summary>
    /// The batches as a sequence, for an operator body that reads more naturally as a loop. Nothing
    /// is awaited while the input answers synchronously.
    /// </summary>
    IAsyncEnumerable<ColumnarBatch> ExecuteAsync(CancellationToken ct);
}

using Chalk.Catalog;
using Chalk.Sources;
using ArrowSchema = Apache.Arrow.Schema;

namespace Chalk.Execution.Operators;

/// <summary>
/// One shared subtree's rows, for one tree (D338, design 63 §2): computed the first time any use is
/// read in an execution, copied into the execution's arena, and kept for the rest of it.
/// </summary>
/// <remarks>
/// A producer's batch does not outlive its next pull, so the rows are copied, and they count
/// against the arena's budget as any buffer does: past it, the execution fails with
/// <c>ArenaBudgetExceededException</c> as itself, naming the use that was reading. Uses are read one
/// at a time — no operator pulls two inputs at once, and a partitioned scan, which does, is never
/// shared under — so the first reader fills it and every later one finds it full.
/// </remarks>
internal sealed class SharedResult(OperatorContext context, IBatchOperator producer, IReadOnlyList<ChalkType> types)
{
    private int _filled = -1;

    /// <summary>The rows, once <see cref="FillAsync"/> has run for this execution.</summary>
    public Materialisation Materialisation { get; } = new(types);

    /// <summary>Computes the subtree once for this execution; a later call finds it done.</summary>
    public async ValueTask FillAsync(CancellationToken ct)
    {
        if (_filled == context.Generation)
        {
            return;
        }

        await Materialisation.Rows.FillAsync(producer, ct).ConfigureAwait(false);
        _filled = context.Generation;
    }

    public ValueTask DisposeAsync() => producer.DisposeAsync();
}

/// <summary>
/// A use of a shared subtree (D338): the IR's <c>SharedRef</c>. It fills the subtree's
/// <see cref="SharedResult"/> if no use has yet this execution, then replays its rows.
/// </summary>
internal sealed class SharedInputOperator : OperatorBase
{
    private readonly SharedResult _shared;
    private readonly bool _owns;
    private readonly MaterialisedInputOperator _replay;
    private readonly ColumnarBatch _output;

    /// <param name="owns">Whether this use disposes the subtree: the first the compiler built.</param>
    public SharedInputOperator(
        OperatorContext context,
        string path,
        ArrowSchema schema,
        IReadOnlyList<ChalkType> columnTypes,
        SharedResult shared,
        bool owns)
        : base(context, schema, columnTypes, path)
    {
        _shared = shared;
        _owns = owns;
        _replay = new MaterialisedInputOperator(context, path, schema, columnTypes, shared.Materialisation);
        _output = NewOutput();
    }

    protected override async ValueTask DisposeCoreAsync()
    {
        await _replay.DisposeAsync().ConfigureAwait(false);
        if (_owns)
        {
            await _shared.DisposeAsync().ConfigureAwait(false);
        }
    }

    protected override async IAsyncEnumerable<ColumnarBatch> RunAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await _shared.FillAsync(ct).ConfigureAwait(false);
        await foreach (var batch in _replay.ExecuteAsync(ct).ConfigureAwait(false))
        {
            _output.Adopt(batch);
            yield return _output;
        }
    }
}

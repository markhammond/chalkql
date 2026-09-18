using Chalk.Catalog;
using Chalk.Execution.Vectors;
using Chalk.Sources;
using ArrowSchema = Apache.Arrow.Schema;

namespace Chalk.Execution.Operators;

/// <summary>
/// Replays the batch set an enclosing <c>AdaptiveJoin</c> materialised (D97,
/// <c>docs/design/20-m5-federation.md</c> §4).
/// </summary>
/// <remarks>
/// <para>
/// A leaf that reads from a buffer instead of from a source. Both of an adaptive join's branches
/// have one where the small side would be, and both replay the <em>same</em> rows: the join
/// materialises its small input once, counts its distinct keys, and only then decides which branch
/// gets to consume it. Without this node the chosen branch would have to re-read the small side, and
/// re-reading is exactly what an adaptive decision cannot afford — it may be a remote query, and the
/// count that decided the branch would then have been taken over different rows.
/// </para>
/// <para>
/// It emits the buffer in <c>BatchSize</c> slices rather than as one batch, because the operator
/// above it is an ordinary pipeline operator and expects ordinary batches.
/// </para>
/// </remarks>
internal sealed class MaterialisedInputOperator : OperatorBase
{
    private readonly Materialisation _materialisation;
    private readonly ColumnarBatch _output;
    private readonly ColumnCopier[] _copiers;
    private int[] _rows = [];

    public MaterialisedInputOperator(
        OperatorContext context,
        string path,
        ArrowSchema schema,
        IReadOnlyList<ChalkType> columnTypes,
        Materialisation materialisation)
        : base(context, schema, columnTypes, path)
    {
        _materialisation = materialisation;
        _output = NewOutput();
        _copiers = [.. columnTypes.Select(t => new ColumnCopier(t))];
    }

    protected override ValueTask DisposeCoreAsync() => default;

    protected override async IAsyncEnumerable<ColumnarBatch> RunAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        var rows = _materialisation.Rows;
        if (rows.Count == 0)
        {
            yield break;
        }

        var arena = Context.Arena;
        var batchSize = Context.Settings.BatchSize;
        _rows = arena.Rent<int>(batchSize);
        try
        {
            for (var start = 0; start < rows.Count; start += batchSize)
            {
                ct.ThrowIfCancellationRequested();
                var count = Math.Min(batchSize, rows.Count - start);
                for (var i = 0; i < count; i++)
                {
                    _rows[i] = start + i;
                }

                _output.Begin(count);
                for (var c = 0; c < _copiers.Length; c++)
                {
                    _copiers[c].Begin();
                    _copiers[c].AppendGather(rows.Column(c), _rows.AsSpan(0, count));
                    _output.Set(c, _copiers[c].FinishView());
                }

                yield return _output;
            }
        }
        finally
        {
            arena.Return(_rows);
            _rows = [];
        }
    }
}

/// <summary>
/// The buffer an <c>AdaptiveJoin</c> fills and its branches replay. One per join per execution;
/// shared by reference with the <see cref="MaterialisedInputOperator"/>s the compiler built for it.
/// </summary>
internal sealed class Materialisation
{
    /// <summary>The materialised rows. Empty until the join has filled it.</summary>
    public Chalk.Execution.Joins.JoinRows Rows { get; }

    public Materialisation(IReadOnlyList<ChalkType> columnTypes) =>
        Rows = new Chalk.Execution.Joins.JoinRows(columnTypes);
}

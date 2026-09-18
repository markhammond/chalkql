using Chalk.Catalog;
using Chalk.Execution.Expressions;
using Chalk.Execution.Vectors;
using Chalk.Sources;
using ArrowSchema = Apache.Arrow.Schema;

namespace Chalk.Execution.Operators;

/// <summary>
/// Evaluates the projection list (§6.3). A bare field reference is forwarded rather than copied — the
/// output slot simply takes the input's view (§6.1) — while a computed column is materialised into
/// this operator's own arena buffers.
/// </summary>
/// <remarks>
/// Since step 20 there is nothing to own or dispose: a view is a borrow, so <c>SELECT a, a</c> is two
/// copies of the same view and costs nothing extra. A selection on the input is forwarded unchanged,
/// because a computed column is evaluated over every lane and read through the selection downstream
/// (D62).
/// </remarks>
internal sealed class ProjectOperator : OperatorBase
{
    private readonly IBatchOperator _input;
    private readonly IVectorExpr[] _exprs;
    private readonly ColumnCopier?[] _copiers;
    private readonly EvalContext _eval;
    private readonly ColumnarBatch _output;

    /// <summary>Input column for each output field, or -1 when the field is computed.</summary>
    private readonly int[] _forwarded;

    public ProjectOperator(
        OperatorContext context,
        string path,
        ArrowSchema schema,
        IReadOnlyList<ChalkType> columnTypes,
        IBatchOperator input,
        IVectorExpr[] exprs)
        : base(context, schema, columnTypes, path)
    {
        _input = input;
        _exprs = exprs;
        _eval = context.NewEvalContext();
        _copiers = new ColumnCopier?[exprs.Length];
        _forwarded = new int[exprs.Length];
        _output = NewOutput();

        for (var i = 0; i < exprs.Length; i++)
        {
            if (exprs[i] is FieldRefExpr reference)
            {
                _forwarded[i] = reference.Index;
                continue;
            }

            // A pass-through column never needs a copier; anything computed does.
            _forwarded[i] = -1;
            _copiers[i] = new ColumnCopier(exprs[i].Type);
        }
    }

    protected override ValueTask DisposeCoreAsync() => _input.DisposeAsync();

    protected override async IAsyncEnumerable<ColumnarBatch> RunAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var batch in _input.ExecuteAsync(ct))
        {
            var length = batch.RowCount;
            _eval.SetBatch(batch, length);
            _output.Begin(length);
            for (var i = 0; i < _exprs.Length; i++)
            {
                if (_forwarded[i] >= 0)
                {
                    var source =
                        batch.Column(_forwarded[i]);

                    _output.Set(i, source);

                    var projected =
                        _output.Column(i);

                    System.Diagnostics.Debug.Assert(
                        ReferenceEquals(
                            projected.ManagedPublisher,
                            source.ManagedPublisher));

                    continue;
                }

                var value = _exprs[i].Evaluate(_eval);
                _output.Set(i, value.IsScalar
                    ? _copiers[i]!.CopyAll(value, length)
                    : value.View);
            }

            _output.ShareSelection(batch);

            yield return _output;
        }
    }
}

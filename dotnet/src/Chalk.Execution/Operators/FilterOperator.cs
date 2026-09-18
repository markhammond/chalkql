using Chalk.Catalog;
using Chalk.Execution.Expressions;
using Chalk.Execution.Vectors;
using Chalk.Sources;
using ArrowSchema = Apache.Arrow.Schema;

namespace Chalk.Execution.Operators;

/// <summary>
/// Keeps the rows whose condition is TRUE; NULL and FALSE both drop (<c>02-ir.md</c> §4).
/// </summary>
/// <remarks>
/// The selection vector of §2 (D62): the surviving row indexes go into the output batch's selection
/// and the input's columns are forwarded unchanged whenever the fraction kept is at least
/// <c>ExecutionOptions.SelectionCompactionThreshold</c>. Below that the operator compacts by
/// gathering the survivors into its own buffers — still no allocation, one copy of what is left.
/// Everything downstream accepts a selected batch, and a batch with a selection never leaves the
/// pipeline: the root compacts.
/// </remarks>
internal sealed class FilterOperator : OperatorBase
{
    private readonly IBatchOperator _input;
    private readonly IVectorExpr _condition;
    private readonly EvalContext _eval;
    private readonly ColumnCopier[] _copiers;
    private readonly ColumnarBatch _output;
    private readonly double _threshold;
    private int[] _selection = [];

    public FilterOperator(
        OperatorContext context,
        string path,
        ArrowSchema schema,
        IReadOnlyList<ChalkType> columnTypes,
        IBatchOperator input,
        IVectorExpr condition)
        : base(context, schema, columnTypes, path)
    {
        _input = input;
        _condition = condition;
        _eval = context.NewEvalContext();
        _copiers = [.. columnTypes.Select(t => new ColumnCopier(t))];
        _output = NewOutput();
        _threshold = context.Settings.SelectionCompactionThreshold;
    }

    protected override ValueTask DisposeCoreAsync() => _input.DisposeAsync();

    protected override async IAsyncEnumerable<ColumnarBatch> RunAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        // The selection vector belongs to this execution, not to the operator: rented here and
        // returned below, whether the run ends, throws or is abandoned (ADR 0012).
        var arena = Context.Arena;
        _selection = arena.Rent<int>(Context.Settings.BatchSize);
        try
        {
            await foreach (var batch in _input.ExecuteAsync(ct))
            {
                var length = batch.RowCount;
                _eval.SetBatch(batch, length);
                var mask = _condition.Evaluate(_eval);
                var selected = Select(mask, batch);
                if (selected == 0)
                {
                    continue;
                }

                // Everything survived and the input had nothing selected away: forward as it stands.
                if (selected == batch.Count && !batch.HasSelection)
                {
                    _output.Adopt(batch);
                    yield return _output;
                    continue;
                }

                if (selected >= length * _threshold)
                {
                    _output.Begin(length);
                    _output.SetAll(batch.Columns);
                    _output.Select(_selection, selected);
                    yield return _output;
                    continue;
                }

                _output.Begin(selected);
                for (var c = 0; c < _copiers.Length; c++)
                {
                    _output.Set(c, _copiers[c].Gather(batch.Column(c), _selection.AsSpan(0, selected)));
                }

                yield return _output;
            }
        }
        finally
        {
            arena.Return(_selection);
            _selection = [];
        }
    }

    /// <summary>
    /// Fills the selection vector with the passing row indexes — in the input's own coordinates, so a
    /// selected input stays selected — and returns how many there are.
    /// </summary>
    private int Select(in Vector mask, ColumnarBatch batch)
    {
        var rows = batch.Count;
        if (_selection.Length < batch.RowCount)
        {
            // A source that ignores BatchSize is legal; growing swaps the rental rather than
            // allocating beside it, so the arena still owns every byte.
            var grown = Context.Arena.Rent<int>(batch.RowCount);
            Context.Arena.Return(_selection);
            _selection = grown;
        }

        if (mask.IsScalar)
        {
            var pass = !mask.Scalar.IsNull && mask.Scalar.Integer != 0;
            if (!pass)
            {
                return 0;
            }

            for (var i = 0; i < rows; i++)
            {
                _selection[i] = batch.RowAt(i);
            }

            return rows;
        }

        var lanes = Lanes<byte>.From(mask);
        var count = 0;
        if (lanes.AllValid)
        {
            for (var i = 0; i < rows; i++)
            {
                var row = batch.RowAt(i);
                _selection[count] = row;
                count += lanes[row] != 0 ? 1 : 0;
            }

            return count;
        }

        for (var i = 0; i < rows; i++)
        {
            var row = batch.RowAt(i);
            _selection[count] = row;
            count += lanes.IsValid(row) && lanes[row] != 0 ? 1 : 0;
        }

        return count;
    }
}

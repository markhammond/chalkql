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
internal sealed class FilterOperator : OperatorBase, IStreams
{
    private readonly IBatchOperator _input;
    private readonly Streaming _run;
    private readonly IVectorExpr _condition;
    private readonly EvalContext _eval;
    private readonly ColumnCopier[] _copiers;
    private readonly ColumnarBatch _output;
    private readonly double _threshold;
    private int[] _selection = [];
    private readonly string? _quarantine;
    private byte[] _lane = [];

    public FilterOperator(
        OperatorContext context,
        string path,
        ArrowSchema schema,
        IReadOnlyList<ChalkType> columnTypes,
        IBatchOperator input,
        IVectorExpr condition,
        string? quarantine = null)
        : base(context, schema, columnTypes, path)
    {
        _input = input;
        _condition = condition;
        _quarantine = quarantine;
        _eval = context.NewEvalContext();
        _copiers = [.. columnTypes.Select(t => new ColumnCopier(t))];
        _output = NewOutput();
        _threshold = context.Settings.SelectionCompactionThreshold;
        _run = new Streaming(this);
    }

    protected override ValueTask DisposeCoreAsync() => _input.DisposeAsync();

    protected override IAsyncEnumerator<ColumnarBatch> Begin(CancellationToken ct) => _run.Begin(ct);

    IBatchOperator IStreams.Input => _input;

    /// <summary>
    /// The selection vector belongs to the execution, not to the operator: rented as it starts and
    /// returned when its iteration ends, whether it runs out, throws or is abandoned (ADR 0012).
    /// </summary>
    void IStreams.Started() => _selection = Context.Arena.Rent<int>(Context.Settings.BatchSize);

    void IStreams.Ended()
    {
        Context.Arena.Return(_selection);
        _selection = [];
    }

    ColumnarBatch? IStreams.Step(ColumnarBatch batch)
    {
        var length = batch.RowCount;
        _eval.SetBatch(batch, length);
        int selected;
        if (_quarantine is null)
        {
            selected = Select(_condition.Evaluate(_eval), batch);
        }
        else
        {
            try
            {
                selected = Select(_condition.Evaluate(_eval), batch);
            }
            catch (Exception error) when (Chalk.Sources.IndeterminateLedger.IsDataError(error))
            {
                selected = RowByRow(batch, error);
            }
        }
        if (selected == 0)
        {
            return null;
        }

        // Everything survived and the input had nothing selected away: forward as it stands.
        if (selected == batch.Count && !batch.HasSelection)
        {
            _output.Adopt(batch);
            return _output;
        }

        if (selected >= length * _threshold)
        {
            _output.Begin(length);
            _output.SetAll(batch.Columns);
            _output.Select(_selection, selected);
            return _output;
        }

        _output.Begin(selected);
        for (var c = 0; c < _copiers.Length; c++)
        {
            _output.Set(c, _copiers[c].Gather(batch.Column(c), _selection.AsSpan(0, selected)));
        }

        return _output;
    }

    /// <summary>
    /// Inside a quarantine (F302, design 82), the condition over each row alone, after it raised over
    /// the batch: a row whose condition raises is not admitted, and is recorded as the policy's
    /// indeterminate decision, a <c>RowsNotAdmitted</c>. A row is admitted only where its condition
    /// answered true, so where its admission never depended on what failed.
    /// </summary>
    private int RowByRow(ColumnarBatch batch, Exception first)
    {
        var length = batch.RowCount;
        var rows = batch.Count;
        if (_selection.Length < length)
        {
            var grown = Context.Arena.Rent<int>(length);
            Context.Arena.Return(_selection);
            _selection = grown;
        }

        var bytes = Validity.ByteCount(length);
        if (_lane.Length < Math.Max(bytes, 1))
        {
            _lane = new byte[Math.Max(bytes, 1)];
        }

        var count = 0;
        long settled = 0;
        for (var i = 0; i < rows; i++)
        {
            var row = batch.RowAt(i);
            System.Array.Clear(_lane, 0, bytes);
            Apache.Arrow.BitUtility.SetBit(_lane, row);
            var scope = _eval.Narrow(_lane);
            try
            {
                var answer = _condition.Evaluate(_eval);
                var pass = answer.IsScalar
                    ? !answer.Scalar.IsNull && answer.Scalar.Integer != 0
                    : Lanes<byte>.From(answer) is var lanes && lanes.IsValid(row) && lanes[row] != 0;
                if (pass)
                {
                    _selection[count++] = row;
                }
            }
            catch (Exception error) when (Chalk.Sources.IndeterminateLedger.IsDataError(error))
            {
                settled++;
            }
            finally
            {
                _eval.Restore(scope);
            }
        }

        if (settled > 0)
        {
            Context.Arena.Indeterminate?.Record(
                _quarantine!, Chalk.Sources.IndeterminateResolution.RowsNotAdmitted, settled, first);
        }

        return count;
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

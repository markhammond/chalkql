using Chalk.Catalog;
using Chalk.Execution.Expressions;
using Chalk.Execution.Operators;
using Chalk.Execution.Vectors;
using Chalk.Ir;
using Chalk.Sources;
using ArrowSchema = Apache.Arrow.Schema;

namespace Chalk.Execution.Joins;

/// <summary>
/// The equi-join for two inputs that already arrive in key order: one pass over each, with the
/// equal-key group on the right cross-joined against each left row. Maps to the IR's
/// <c>MergeJoin</c> (D45).
/// </summary>
/// <remarks>
/// The right input is held whole rather than streamed, which is what the other joins do too and what
/// makes an equal-key group a pair of indexes rather than a buffer of its own. The left input is
/// streamed and never buffered, so the operator's memory is the right side plus one batch.
/// <para>
/// The cursor only ever moves forwards: <c>FirstCandidate</c> is called once per left row, in order,
/// and the left rows are in the same key order the right ones are.
/// </para>
/// </remarks>
internal sealed class MergeJoinOperator : PairJoinOperator
{
    private readonly int[] _leftKeys;
    private readonly int[] _rightKeys;
    private readonly JoinKeys _keys;
    private readonly JoinKeyOrder _order;

    private readonly Vector[] _rightKeyVectors;
    private readonly Vector[] _leftKeyVectors;
    private int _cursor;
    private int _groupEnd;

    public MergeJoinOperator(
        OperatorContext context,
        string path,
        ArrowSchema schema,
        IReadOnlyList<ChalkType> outputTypes,
        IReadOnlyList<ChalkType> leftTypes,
        IReadOnlyList<ChalkType> rightTypes,
        IBatchOperator left,
        IBatchOperator right,
        JoinType joinType,
        IVectorExpr? condition,
        IReadOnlyList<int> leftKeys,
        IReadOnlyList<int> rightKeys,
        JoinKeyOrder order,
        double estimatedRightRows = 0)
        : base(
            context, path, schema, outputTypes, leftTypes, rightTypes, left, right, joinType,
            condition, estimatedRightRows)
    {
        _leftKeys = [.. leftKeys];
        _rightKeys = [.. rightKeys];
        _keys = new JoinKeys([.. leftKeys.Select(k => leftTypes[k])]);
        _order = order;
        _rightKeyVectors = new Vector[rightKeys.Count];
        _leftKeyVectors = new Vector[leftKeys.Count];
    }

    protected override void OnBuilt(ExecutionArena arena)
    {
        JoinKeys.Columns(_rightKeyVectors, Right.Columns, _rightKeys, Right.Count);
        _cursor = 0;
        _groupEnd = 0;
    }

    protected override void OnProbeBatch(ColumnarBatch probe) =>
        JoinKeys.Columns(_leftKeyVectors, probe.Columns, _leftKeys, probe.RowCount);

    protected override int FirstCandidate(ColumnarBatch probe, int probeRow)
    {
        // A NULL key never matches, and it never moves the cursor either: whichever end of the
        // ordering the NULLs sit at, the values on the other side are still ahead of or behind
        // everything the cursor has yet to see.
        if (_keys.AnyNull(_leftKeyVectors, probeRow))
        {
            return -1;
        }

        while (_cursor < Right.Count
            && _keys.Compare(_rightKeyVectors, _cursor, _leftKeyVectors, probeRow, _order) < 0)
        {
            _cursor++;
        }

        if (_cursor >= Right.Count
            || _keys.Compare(_rightKeyVectors, _cursor, _leftKeyVectors, probeRow, _order) != 0)
        {
            _groupEnd = _cursor;
            return -1;
        }

        _groupEnd = _cursor;
        while (_groupEnd < Right.Count
            && _keys.Compare(_rightKeyVectors, _groupEnd, _leftKeyVectors, probeRow, _order) == 0)
        {
            _groupEnd++;
        }

        return _cursor;
    }

    protected override int NextCandidate(int rightRow) =>
        rightRow + 1 < _groupEnd ? rightRow + 1 : -1;

    protected override void ReleaseBuild(ExecutionArena arena)
    {
    }
}

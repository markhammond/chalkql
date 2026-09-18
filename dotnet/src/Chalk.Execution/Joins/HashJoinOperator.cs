using Chalk.Catalog;
using Chalk.Execution.Expressions;
using Chalk.Execution.Operators;
using Chalk.Execution.Vectors;
using Chalk.Ir;
using Chalk.Sources;
using ArrowSchema = Apache.Arrow.Schema;

namespace Chalk.Execution.Joins;

/// <summary>
/// The equi-join of §6.4/D45: the right input becomes a hash table, the left input streams through
/// it. Maps to the IR's <c>HashJoin</c>.
/// </summary>
internal sealed class HashJoinOperator : PairJoinOperator
{
    private readonly int[] _leftKeys;
    private readonly int[] _rightKeys;
    private readonly JoinKeys _keys;
    private readonly JoinHashTable _table;
    private readonly Vector[] _probeKeys;
    private readonly Vector[] _buildKeys;

    public HashJoinOperator(
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
        double estimatedRightRows = 0)
        : base(
            context, path, schema, outputTypes, leftTypes, rightTypes, left, right, joinType,
            condition, estimatedRightRows)
    {
        _leftKeys = [.. leftKeys];
        _rightKeys = [.. rightKeys];
        _keys = new JoinKeys([.. leftKeys.Select(k => leftTypes[k])]);
        _table = new JoinHashTable(_keys);
        _probeKeys = new Vector[leftKeys.Count];
        _buildKeys = new Vector[rightKeys.Count];
    }

    protected override void OnBuilt(ExecutionArena arena)
    {
        JoinKeys.Columns(_buildKeys, Right.Columns, _rightKeys, Right.Count);
        _table.Build(arena, _buildKeys, Right.Count);
    }

    protected override void OnProbeBatch(ColumnarBatch probe) =>
        JoinKeys.Columns(_probeKeys, probe.Columns, _leftKeys, probe.RowCount);

    protected override int FirstCandidate(ColumnarBatch probe, int probeRow) =>
        _table.First(_probeKeys, probeRow);

    protected override int NextCandidate(int rightRow) => _table.Next(rightRow);

    protected override void ReleaseBuild(ExecutionArena arena) => _table.Release();
}

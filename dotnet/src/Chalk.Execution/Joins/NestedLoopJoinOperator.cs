using Chalk.Catalog;
using Chalk.Execution.Expressions;
using Chalk.Execution.Operators;
using Chalk.Ir;
using Chalk.Sources;
using ArrowSchema = Apache.Arrow.Schema;

namespace Chalk.Execution.Joins;

/// <summary>
/// The join that always applies: every left row against every buffered right row, with the whole
/// condition evaluated vectorised over batches of candidate pairs. Maps to the IR's
/// <c>NestedLoopJoin</c>, and is the only operator that can run a cross join or a non-equi condition
/// (D45).
/// </summary>
internal sealed class NestedLoopJoinOperator : PairJoinOperator
{
    public NestedLoopJoinOperator(
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
        double estimatedRightRows = 0)
        : base(
            context, path, schema, outputTypes, leftTypes, rightTypes, left, right, joinType,
            condition, estimatedRightRows)
    {
    }

    protected override void OnBuilt(ExecutionArena arena)
    {
    }

    protected override void OnProbeBatch(ColumnarBatch probe)
    {
    }

    protected override int FirstCandidate(ColumnarBatch probe, int probeRow) =>
        Right.Count > 0 ? 0 : -1;

    protected override int NextCandidate(int rightRow) =>
        rightRow + 1 < Right.Count ? rightRow + 1 : -1;

    protected override void ReleaseBuild(ExecutionArena arena)
    {
    }
}

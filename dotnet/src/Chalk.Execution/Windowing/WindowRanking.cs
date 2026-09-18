using System.Runtime.InteropServices;
using Chalk.Catalog;
using Chalk.Execution.Expressions;
using Chalk.Execution.Vectors;
using Chalk.Ir;

namespace Chalk.Execution.Windowing;

/// <summary>
/// The ranking family: <c>ROW_NUMBER</c>, <c>RANK</c>, <c>DENSE_RANK</c>, <c>NTILE</c>,
/// <c>PERCENT_RANK</c> and <c>CUME_DIST</c>. All six operate on the whole partition and ignore the
/// frame, as SQL says, and all six are one pass with the peer groups the operator already computed
/// (§4).
/// </summary>
internal sealed class WindowRankingEvaluator : WindowValueEvaluator
{
    private readonly WindowFunctionId _function;
    private readonly WindowConstant? _buckets;

    public WindowRankingEvaluator(
        ChalkType resultType, WindowFunctionId function, WindowConstant? buckets)
        : base(resultType)
    {
        _function = function;
        _buckets = buckets;
    }

    public override void Compute(WindowRun run, int start, int end)
    {
        var size = end - start;
        long buckets = 0;
        if (_function == WindowFunctionId.Ntile)
        {
            var value = _buckets!.Bind(run.Parameters);
            buckets = value.IsNull ? 0 : value.Integer;
            if (buckets <= 0)
            {
                // Wrapped by OperatorBase into an ExecutionException naming this window (§6.7).
                throw new InvalidOperationException(
                    $"NTILE({buckets}) needs a positive bucket count.");
            }
        }

        var denseRank = 0L;
        for (var i = start; i < end; i++)
        {
            var peerStart = run.PeerStart[i];
            if (i == peerStart)
            {
                denseRank++;
            }

            Valid[i] = true;
            switch (_function)
            {
                case WindowFunctionId.RowNumber:
                    WriteInteger(i, i - start + 1);
                    break;
                case WindowFunctionId.Rank:
                    WriteInteger(i, peerStart - start + 1);
                    break;
                case WindowFunctionId.DenseRank:
                    WriteInteger(i, denseRank);
                    break;
                case WindowFunctionId.Ntile:
                    WriteInteger(i, Bucket(i - start, size, buckets));
                    break;
                case WindowFunctionId.PercentRank:
                    WriteDouble(i, size <= 1 ? 0d : (double)(peerStart - start) / (size - 1));
                    break;
                default:
                    WriteDouble(i, (double)(run.PeerEnd[i] - start) / size);
                    break;
            }
        }
    }

    /// <summary>
    /// SQL's bucket rule: divide the partition into <paramref name="buckets"/> groups whose sizes
    /// differ by at most one, with the larger groups first.
    /// </summary>
    private static long Bucket(long position, long size, long buckets)
    {
        var quotient = size / buckets;
        var remainder = size % buckets;
        var large = remainder * (quotient + 1);
        return position < large
            ? (position / (quotient + 1)) + 1
            : remainder + ((position - large) / quotient) + 1;
    }

    private void WriteInteger(int row, long value) =>
        NumberLanes.WriteInteger(ResultKind, ResultType, value, Lane(row));

    private void WriteDouble(int row, double value)
    {
        if (ResultKind == ColumnKind.Float)
        {
            MemoryMarshal.Write(Lane(row), (float)value);
            return;
        }

        MemoryMarshal.Write(Lane(row), value);
    }
}

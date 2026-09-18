using Chalk.Catalog;
using Chalk.Execution.Aggregation;
using Chalk.Execution.Operators;
using Chalk.Execution.Vectors;
using Chalk.Ir;
using Chalk.Sources;
using Array = System.Array;
using ArrowSchema = Apache.Arrow.Schema;

namespace Chalk.Execution.Joins;

/// <summary>
/// <c>ASOF JOIN</c>: for each left row, the right row with equal keys whose time is closest to the
/// left row's under the match comparison (D41, D45). Maps to the IR's <c>AsOfJoin</c>.
/// </summary>
/// <remarks>
/// The right input is partitioned by key — the same open-addressed table the hash join builds — and
/// each partition is ordered by time; a left row then costs one probe and one binary search. Right
/// rows whose time or key is NULL are left out of the partitions entirely, because neither can ever
/// be the closest match, and a left row whose time is NULL matches nothing.
/// <para>
/// <b>Ties.</b> Several right rows may share the closest time. The first in the right input's own
/// order wins (D41; Calcite leaves this unspecified), which the ordering below delivers: rows are
/// ordered by time and then by their input position, and a backwards match walks back to the start of
/// the tie group.
/// </para>
/// </remarks>
internal sealed class AsOfJoinOperator : PairJoinOperator
{
    private readonly int[] _leftKeys;
    private readonly int[] _rightKeys;
    private readonly int _leftTime;
    private readonly int _rightTime;
    private readonly AsOfMatch _match;
    private readonly bool _rightPreSorted;
    private readonly JoinKeys _keys;
    private readonly JoinHashTable _table;
    private readonly ColumnKind _timeKind;
    private readonly int _timeWidth;
    private readonly byte[] _leftScratch = new byte[16];
    private readonly byte[] _rightScratch = new byte[16];
    private readonly byte[] _tieA = new byte[16];
    private readonly byte[] _tieB = new byte[16];

    private readonly Vector[] _probeKeys;
    private readonly Vector[] _buildKeys;
    private Vector _leftTimes;
    private Vector _rightTimes;
    private int[] _order = [];
    private int[] _partitionStart = [];
    private int[] _partitionEnd = [];

    public AsOfJoinOperator(
        OperatorContext context,
        string path,
        ArrowSchema schema,
        IReadOnlyList<ChalkType> outputTypes,
        IReadOnlyList<ChalkType> leftTypes,
        IReadOnlyList<ChalkType> rightTypes,
        IBatchOperator left,
        IBatchOperator right,
        JoinType joinType,
        IReadOnlyList<int> leftKeys,
        IReadOnlyList<int> rightKeys,
        int leftTime,
        int rightTime,
        AsOfMatch match,
        bool rightPreSorted,
        double estimatedRightRows = 0)
        : base(
            context, path, schema, outputTypes, leftTypes, rightTypes, left, right, joinType,
            condition: null, estimatedRightRows)
    {
        _leftKeys = [.. leftKeys];
        _rightKeys = [.. rightKeys];
        _leftTime = leftTime;
        _rightTime = rightTime;
        _match = match;
        _rightPreSorted = rightPreSorted;
        _keys = new JoinKeys([.. leftKeys.Select(k => leftTypes[k])]);
        _table = new JoinHashTable(_keys);
        _timeKind = ColumnKinds.Of(leftTypes[leftTime]);
        _timeWidth = ColumnKinds.Width(_timeKind);
        _probeKeys = new Vector[leftKeys.Count];
        _buildKeys = new Vector[rightKeys.Count];
    }

    /// <summary>True when the comparison keeps right rows at or before the left time.</summary>
    private bool LooksBackwards => _match is AsOfMatch.Ge or AsOfMatch.Gt;

    /// <summary>True when the comparison is strict, so an equal time does not match.</summary>
    private bool IsStrict => _match is AsOfMatch.Gt or AsOfMatch.Lt;

    protected override void OnBuilt(ExecutionArena arena)
    {
        var rows = Right.Count;
        JoinKeys.Columns(_buildKeys, Right.Columns, _rightKeys, rows);
        var buildKeys = _buildKeys;
        _table.Build(arena, buildKeys, rows);
        _rightTimes = Vector.FromView(Right.Column(_rightTime), rows);

        _order = arena.Rent<int>(Math.Max(rows, 1));
        _partitionStart = arena.Rent<int>(Math.Max(rows, 1));
        _partitionEnd = arena.Rent<int>(Math.Max(rows, 1));

        var next = 0;
        for (var row = 0; row < rows; row++)
        {
            _partitionStart[row] = 0;
            _partitionEnd[row] = 0;
            if (_table.First(buildKeys, row) != row)
            {
                continue; // not the head of its bucket, or a NULL key that is in no bucket at all
            }

            var start = next;
            for (var member = row; member >= 0; member = _table.Next(member))
            {
                if (LaneAccess.IsValid(_rightTimes, member))
                {
                    _order[next++] = member;
                }
            }

            if (!_rightPreSorted && next - start > 1)
            {
                Array.Sort(_order, start, next - start, Comparer<int>.Create(CompareByTime));
            }

            _partitionStart[row] = start;
            _partitionEnd[row] = next;
        }
    }

    protected override void OnProbeBatch(ColumnarBatch probe)
    {
        JoinKeys.Columns(_probeKeys, probe.Columns, _leftKeys, probe.RowCount);
        _leftTimes = Vector.FromView(probe.Column(_leftTime), probe.RowCount);
    }

    protected override int FirstCandidate(ColumnarBatch probe, int probeRow)
    {
        if (!LaneAccess.IsValid(_leftTimes, probeRow))
        {
            return -1; // a NULL left time matches nothing (D41)
        }

        var head = _table.First(_probeKeys, probeRow);
        if (head < 0)
        {
            return -1;
        }

        var start = _partitionStart[head];
        var end = _partitionEnd[head];
        if (start == end)
        {
            return -1;
        }

        var time = LaneAccess.Read(_leftTimes, probeRow, _timeKind, _timeWidth, _leftScratch);
        return LooksBackwards ? Latest(start, end, time) : Earliest(start, end, time);
    }

    /// <summary>An ASOF join matches at most one right row per left row.</summary>
    protected override int NextCandidate(int rightRow) => -1;

    protected override void ReleaseBuild(ExecutionArena arena)
    {
        _table.Release();
        arena.Return(_order);
        arena.Return(_partitionStart);
        arena.Return(_partitionEnd);
        _order = [];
        _partitionStart = [];
        _partitionEnd = [];
    }

    /// <summary>
    /// The greatest right time at or before <paramref name="time"/> (<c>&gt;=</c>) or strictly before
    /// it (<c>&gt;</c>), as the first row of its tie group.
    /// </summary>
    private int Latest(int start, int end, ReadOnlySpan<byte> time)
    {
        var low = start;
        var high = end;
        while (low < high)
        {
            var middle = low + ((high - low) >> 1);
            var order = CompareTime(_order[middle], time);
            if (order < 0 || (order == 0 && !IsStrict))
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        if (low == start)
        {
            return -1;
        }

        // low - 1 is the last row that qualifies; the tie-break wants the first with that time.
        var found = low - 1;
        while (found > start && CompareTime(_order[found - 1], _order[found]) == 0)
        {
            found--;
        }

        return _order[found];
    }

    /// <summary>The smallest right time at or after <paramref name="time"/>, or strictly after it.</summary>
    private int Earliest(int start, int end, ReadOnlySpan<byte> time)
    {
        var low = start;
        var high = end;
        while (low < high)
        {
            var middle = low + ((high - low) >> 1);
            var order = CompareTime(_order[middle], time);
            if (order < 0 || (order == 0 && IsStrict))
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low == end ? -1 : _order[low];
    }

    /// <summary>Orders one right row's time against a left time already read into a lane.</summary>
    private int CompareTime(int rightRow, ReadOnlySpan<byte> leftTime)
    {
        var lane = LaneAccess.Read(_rightTimes, rightRow, _timeKind, _timeWidth, _rightScratch);
        return LaneComparer.Compare(_timeKind, lane, leftTime);
    }

    /// <summary>Orders two right rows' times, for the tie walk.</summary>
    private int CompareTime(int left, int right)
    {
        var a = LaneAccess.Read(_rightTimes, left, _timeKind, _timeWidth, _tieA);
        var b = LaneAccess.Read(_rightTimes, right, _timeKind, _timeWidth, _tieB);
        return LaneComparer.Compare(_timeKind, a, b);
    }

    /// <summary>Time, then input position — which is what makes the sort stable and the tie-break real.</summary>
    private int CompareByTime(int left, int right)
    {
        var order = CompareTime(left, right);
        return order != 0 ? order : left.CompareTo(right);
    }
}

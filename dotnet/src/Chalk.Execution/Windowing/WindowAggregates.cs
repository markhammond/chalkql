using System.Runtime.InteropServices;
using Chalk.Catalog;
using Chalk.Execution.Expressions;
using Chalk.Execution.Numeric;
using Chalk.Execution.Vectors;
using Chalk.Ir;
using Chalk.Sources;

namespace Chalk.Execution.Windowing;

/// <summary>
/// <c>COUNT</c>, <c>SUM</c>, <c>SUM0</c> and <c>AVG</c> over a frame, as the sliding accumulators of
/// §4: both frame bounds only move forward, so a row that enters is added once and a row that leaves
/// is removed once, and a frame of any width costs one add and one remove per row.
///
/// <para>
/// Exact kinds accumulate checked, so an overflow raises rather than wraps. Floating point
/// accumulates in the frame's own order and is <b>re-summed from scratch every 4 096 rows</b>, which
/// bounds the drift a long chain of add-then-remove would otherwise let grow without bound; the ULP
/// tolerance of <c>05-testing.md</c> §5 is what compares it with the reference executor.
/// </para>
/// <para>
/// An <c>EXCLUDE</c> other than NO OTHERS gives up sliding and recomputes each frame, which is
/// O(frame) per row and what §4 says it costs.
/// </para>
/// </summary>
internal sealed class WindowAggregateEvaluator : WindowValueEvaluator
{
    /// <summary>How many rows a floating-point sum may slide before it is rebuilt from scratch.</summary>
    public const int FloatingResumInterval = 4096;

    private readonly AggregateFunctionId _function;
    private readonly int _valueColumn;
    private readonly ChalkType _argumentType;

    /// <summary>
    /// Whether the answer is the row count and nothing else. <c>COUNT(x)</c>'s result is BIGINT, so
    /// the accumulator's switch on <see cref="WindowValueEvaluator.ResultKind"/> used to take the
    /// integer branch and sum the argument — a checked sum <see cref="WriteResult"/> never reads, and
    /// one that raised on an argument whose truncation does not fit a <c>long</c> (ADR 0044).
    /// </summary>
    private readonly bool _countsOnly;

    private readonly WindowSumTree _tree;

    private long _count;
    private long _integer;
    private double _double;
    private float _single;
    private decimal _decimal;
    private int _sinceResum;

    public WindowAggregateEvaluator(
        ChalkType resultType, AggregateFunctionId function, int valueColumn, ChalkType argumentType)
        : base(resultType)
    {
        _function = function;
        _valueColumn = valueColumn;
        _argumentType = argumentType;
        _countsOnly = function == AggregateFunctionId.Count;
        _tree = new WindowSumTree(
            ColumnKinds.Of(resultType), starCount: valueColumn < 0, countsOnly: _countsOnly);

        if (function is AggregateFunctionId.Sum or AggregateFunctionId.Sum0 or AggregateFunctionId.Avg
            && !IrTypes.IsNumeric(resultType.Kind))
        {
            throw new UnsupportedFeatureException(
                $"{function} over {resultType} in a window",
                "SUM and AVG are defined for the numeric kinds (docs/design/02-ir.md §6).");
        }
    }

    public override void Begin(ExecutionArena arena, int rows)
    {
        base.Begin(arena, rows);
        _arena = arena;
    }

    public override void Release(ExecutionArena arena)
    {
        _tree.Release(arena);
        _arena = null;
        base.Release(arena);
    }

    public override void Compute(WindowRun run, int start, int end)
    {
        var column = _valueColumn >= 0 ? run.Columns[_valueColumn] : run.Columns[0];
        if (!run.NoExclusion || WindowFrames.ForceSegmentTree)
        {
            // D59: the general frame engine. An EXCLUDE cuts one contiguous interval out of the
            // frame, so the answer is at most three range queries however wide the frame is.
            ThroughTree(run, column, start, end);
            return;
        }

        Reset();
        var low = start;
        var high = start - 1;

        for (var i = start; i < end; i++)
        {
            var lo = run.FrameLo[i];
            var hi = run.FrameHi[i];
            if (lo > hi)
            {
                Reset();
                low = lo;
                high = lo - 1;
                Write(run, column, i, lo, hi);
                continue;
            }

            if (low > high || lo > high || hi < low)
            {
                Reset();
                for (var r = lo; r <= hi; r++)
                {
                    Add(column, r);
                }
            }
            else
            {
                // Remove first: the intermediate window is then a subset of both the old frame and
                // the new one, so an exact sum never visits a value neither frame could hold.
                for (var r = low; r < lo; r++)
                {
                    Remove(column, r);
                }

                for (var r = high + 1; r <= hi; r++)
                {
                    Add(column, r);
                }
            }

            low = lo;
            high = hi;
            Write(run, column, i, lo, hi);
        }
    }

    /// <summary>
    /// Every frame answered through the segment tree of D59. The exclusions are all "the frame minus
    /// one contiguous interval", with <c>TIES</c> putting the current row back afterwards.
    /// </summary>
    private void ThroughTree(WindowRun run, WindowColumn column, int start, int end)
    {
        var arena = _arena
            ?? throw new InvalidOperationException("the window aggregate is not attached to an arena.");
        _tree.Attach(_valueColumn >= 0 ? column : null);
        _tree.Build(arena, start, end);

        for (var i = start; i < end; i++)
        {
            var lo = run.FrameLo[i];
            var hi = run.FrameHi[i];
            _tree.Reset();
            if (hi >= lo)
            {
                switch (run.Exclusion)
                {
                    case FrameExclusion.CurrentRow:
                        _tree.QueryExcluding(lo, hi, i, i);
                        break;
                    case FrameExclusion.Group:
                        _tree.QueryExcluding(lo, hi, run.PeerStart[i], run.PeerEnd[i] - 1);
                        break;
                    case FrameExclusion.Ties:
                        _tree.QueryExcluding(lo, hi, run.PeerStart[i], run.PeerEnd[i] - 1);
                        if (i >= lo && i <= hi)
                        {
                            _tree.Query(i, i);
                        }

                        break;
                    default:
                        _tree.Query(lo, hi);
                        break;
                }
            }

            _count = _tree.Count;
            _integer = _tree.Integer;
            _double = _tree.Double;
            _single = _tree.Single;
            _decimal = _tree.Decimal;
            WriteResult(i);
        }
    }

    private void Reset()
    {
        _count = 0;
        _integer = 0;
        _double = 0;
        _single = 0;
        _decimal = 0;
        _sinceResum = 0;
    }

    private void Add(WindowColumn column, int row)
    {
        if (_valueColumn < 0)
        {
            _count++;
            return;
        }

        if (!column.IsValid(row))
        {
            return;
        }

        _count++;
        if (_countsOnly)
        {
            // The count is the whole answer, so the value is never read.
            return;
        }

        switch (ResultKind)
        {
            case ColumnKind.Float:
                _single += (float)column.Double(row);
                break;
            case ColumnKind.Double:
                _double += column.Double(row);
                break;
            case ColumnKind.Decimal128:
                _decimal += column.Decimal(row);
                break;
            default:
                _integer = checked(_integer + Integer(column, row));
                break;
        }
    }

    private void Remove(WindowColumn column, int row)
    {
        if (_valueColumn < 0)
        {
            _count--;
            return;
        }

        if (!column.IsValid(row))
        {
            return;
        }

        _count--;
        if (_countsOnly)
        {
            return;
        }

        switch (ResultKind)
        {
            case ColumnKind.Float:
                _single -= (float)column.Double(row);
                break;
            case ColumnKind.Double:
                _double -= column.Double(row);
                break;
            case ColumnKind.Decimal128:
                _decimal -= column.Decimal(row);
                break;
            default:
                _integer = checked(_integer - Integer(column, row));
                break;
        }
    }

    /// <summary>The argument as an integer, matching what the hash aggregate's SUM reads.</summary>
    private long Integer(WindowColumn column, int row) => column.Kind switch
    {
        ColumnKind.Float or ColumnKind.Double => checked((long)Math.Truncate(column.Double(row))),
        ColumnKind.Decimal128 => checked((long)decimal.Truncate(column.Decimal(row))),
        _ => column.Integer(row),
    };

    /// <summary>Writes the result, rebuilding a floating sum from scratch every 4 096 rows.</summary>
    private void Write(WindowRun run, WindowColumn column, int row, int lo, int hi)
    {
        var floating = ResultKind is ColumnKind.Float or ColumnKind.Double;
        if (floating && ++_sinceResum >= FloatingResumInterval)
        {
            _sinceResum = 0;
            _single = 0;
            _double = 0;
            var count = _count;
            for (var r = lo; r <= hi; r++)
            {
                if (_valueColumn < 0 || column.IsValid(r))
                {
                    if (ResultKind == ColumnKind.Float)
                    {
                        _single += (float)column.Double(r);
                    }
                    else
                    {
                        _double += column.Double(r);
                    }
                }
            }

            _count = count;
        }

        WriteResult(row);
    }

    private void WriteResult(int row)
    {
        var lane = Lane(row);
        lane.Clear();

        switch (_function)
        {
            case AggregateFunctionId.Count:
                Valid[row] = true;
                NumberLanes.WriteInteger(ResultKind, ResultType, _count, lane);
                return;

            case AggregateFunctionId.Sum0:
                Valid[row] = true;
                WriteNumber(lane);
                return;

            case AggregateFunctionId.Avg:
                Valid[row] = _count > 0;
                if (_count > 0)
                {
                    WriteAverage(lane);
                }

                return;

            default:
                Valid[row] = _count > 0;
                if (_count > 0)
                {
                    WriteNumber(lane);
                }

                return;
        }
    }

    private void WriteNumber(Span<byte> lane)
    {
        switch (ResultKind)
        {
            case ColumnKind.Float:
                MemoryMarshal.Write(lane, _single);
                return;
            case ColumnKind.Double:
                MemoryMarshal.Write(lane, _double);
                return;
            case ColumnKind.Decimal128:
                Decimals.Write(lane, _decimal, ResultType.Precision, ResultType.Scale);
                return;
            default:
                NumberLanes.WriteInteger(ResultKind, ResultType, _integer, lane);
                return;
        }
    }

    private void WriteAverage(Span<byte> lane)
    {
        switch (ResultKind)
        {
            case ColumnKind.Float:
                MemoryMarshal.Write(lane, _single / _count);
                return;
            case ColumnKind.Double:
                MemoryMarshal.Write(lane, _double / _count);
                return;
            case ColumnKind.Decimal128:
                Decimals.Write(lane, _decimal / _count, ResultType.Precision, ResultType.Scale);
                return;
            default:
                NumberLanes.WriteInteger(ResultKind, ResultType, _integer / _count, lane);
                return;
        }
    }

    /// <summary>The argument's type, for the accumulator that has to read it.</summary>
    public ChalkType ArgumentType => _argumentType;

    private ExecutionArena? _arena;
}

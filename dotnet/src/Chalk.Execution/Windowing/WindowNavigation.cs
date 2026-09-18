using Chalk.Catalog;
using Chalk.Sources;

namespace Chalk.Execution.Windowing;

/// <summary>
/// The rows of a partition whose value column is not NULL, in order. <c>IGNORE NULLS</c> is a
/// question about positions, so the answer is a position array and a binary search rather than a
/// scan per row (§4).
/// </summary>
internal sealed class NonNullPositions
{
    private int[] _positions = [];
    private int _count;

    public void Begin(ExecutionArena arena, int rows) => _positions = arena.Rent<int>(Math.Max(rows, 1));

    public void Release(ExecutionArena arena)
    {
        arena.Return(_positions);
        _positions = [];
        _count = 0;
    }

    /// <summary>Collects the non-NULL rows of <c>[start, end)</c>.</summary>
    public void Collect(WindowColumn column, int start, int end)
    {
        _count = 0;
        for (var i = start; i < end; i++)
        {
            if (column.IsValid(i))
            {
                _positions[_count++] = i;
            }
        }
    }

    public int Count => _count;

    public int At(int index) => _positions[index];

    /// <summary>How many collected positions are strictly below <paramref name="row"/>.</summary>
    public int CountBelow(int row)
    {
        var index = Array.BinarySearch(_positions, 0, _count, row);
        return index < 0 ? ~index : index;
    }

    /// <summary>How many collected positions are at or below <paramref name="row"/>.</summary>
    public int CountAtOrBelow(int row)
    {
        var index = Array.BinarySearch(_positions, 0, _count, row);
        return index < 0 ? ~index : index + 1;
    }
}

/// <summary>
/// <c>LAG</c> and <c>LEAD</c>: direct index arithmetic over the partition, which they read whole —
/// SQL says the navigation functions ignore the frame. With <c>IGNORE NULLS</c> the arithmetic moves
/// to the non-NULL position array instead (§4).
/// </summary>
internal sealed class WindowLagLeadEvaluator : WindowRowEvaluator
{
    private readonly bool _lead;
    private readonly bool _ignoreNulls;
    private readonly WindowConstant? _offset;
    private readonly bool _hasDefault;
    private readonly NonNullPositions _positions = new();

    public WindowLagLeadEvaluator(
        ChalkType resultType,
        int valueColumn,
        bool lead,
        WindowConstant? offset,
        WindowConstant? defaultValue,
        bool ignoreNulls)
        : base(resultType, valueColumn, defaultValue)
    {
        _lead = lead;
        _offset = offset;
        _ignoreNulls = ignoreNulls;
        _hasDefault = defaultValue is not null;
    }

    public override void Begin(ExecutionArena arena, int rows)
    {
        base.Begin(arena, rows);
        if (_ignoreNulls)
        {
            _positions.Begin(arena, rows);
        }
    }

    public override void Release(ExecutionArena arena)
    {
        if (_ignoreNulls)
        {
            _positions.Release(arena);
        }

        base.Release(arena);
    }

    public override void Compute(WindowRun run, int start, int end)
    {
        var bound = _offset?.Bind(run.Parameters);
        if (bound is { IsNull: true })
        {
            for (var i = start; i < end; i++)
            {
                Source[i] = NullRow;
            }

            return;
        }

        var offset = bound?.Integer ?? 1L;
        var column = run.Columns[ValueColumn];
        var missing = _hasDefault ? DefaultRow : NullRow;

        if (_ignoreNulls)
        {
            _positions.Collect(column, start, end);
            for (var i = start; i < end; i++)
            {
                // Count the non-NULL rows the walk starts from: strictly before the current row when
                // walking back, at or before it when walking forward.
                var seen = _lead ? _positions.CountAtOrBelow(i) : _positions.CountBelow(i);
                var index = _lead ? seen + offset - 1 : seen - offset;
                Source[i] = index >= 0 && index < _positions.Count
                    ? _positions.At((int)index)
                    : missing;
            }

            return;
        }

        for (var i = start; i < end; i++)
        {
            var target = _lead ? i + offset : i - offset;
            Source[i] = target >= start && target < end ? (int)target : missing;
        }
    }
}

/// <summary>
/// <c>FIRST_VALUE</c>, <c>LAST_VALUE</c>, <c>NTH_VALUE</c> and <c>ANY_VALUE</c>: the frame's n-th
/// row, counted from whichever end the function names. These <em>do</em> respect the frame, and with
/// <c>IGNORE NULLS</c> they count only the rows that hold a value.
/// </summary>
internal sealed class WindowFrameValueEvaluator : WindowRowEvaluator
{
    private readonly Position _position;
    private readonly WindowConstant? _nth;
    private readonly bool _ignoreNulls;

    internal enum Position
    {
        First,
        Last,
        Nth,
    }

    public WindowFrameValueEvaluator(
        ChalkType resultType,
        int valueColumn,
        Position position,
        WindowConstant? nth,
        bool ignoreNulls)
        : base(resultType, valueColumn, defaultValue: null)
    {
        _position = position;
        _nth = nth;
        _ignoreNulls = ignoreNulls;
    }

    public override void Compute(WindowRun run, int start, int end)
    {
        var column = run.Columns[ValueColumn];
        long nth = 1;
        var nullNth = false;
        if (_position == Position.Nth)
        {
            var bound = _nth!.Bind(run.Parameters);
            nullNth = bound.IsNull;
            nth = bound.Integer;
            if (!nullNth && nth < 1)
            {
                throw new InvalidOperationException($"NTH_VALUE({nth}) counts from one.");
            }
        }

        for (var i = start; i < end; i++)
        {
            Source[i] = nullNth ? NullRow : Pick(run, column, i, nth);
        }
    }

    private int Pick(WindowRun run, WindowColumn column, int row, long nth)
    {
        var lo = run.FrameLo[row];
        var hi = run.FrameHi[row];
        if (lo > hi)
        {
            return NullRow;
        }

        if (_position == Position.Last)
        {
            for (var r = hi; r >= lo; r--)
            {
                if (Counts(run, column, row, r))
                {
                    return r;
                }
            }

            return NullRow;
        }

        var remaining = _position == Position.Nth ? nth : 1;
        for (var r = lo; r <= hi; r++)
        {
            if (Counts(run, column, row, r) && --remaining == 0)
            {
                return r;
            }
        }

        return NullRow;
    }

    /// <summary>Whether a candidate row counts: not excluded, and not NULL under IGNORE NULLS.</summary>
    private bool Counts(WindowRun run, WindowColumn column, int row, int candidate) =>
        !run.Excluded(row, candidate) && (!_ignoreNulls || column.IsValid(candidate));
}

/// <summary>
/// <c>ANY_VALUE</c> over a frame: any row will do, so the first one that holds a value is the
/// deterministic choice.
/// </summary>
internal sealed class WindowAnyValueEvaluator : WindowRowEvaluator
{
    public WindowAnyValueEvaluator(ChalkType resultType, int valueColumn)
        : base(resultType, valueColumn, defaultValue: null)
    {
    }

    public override void Compute(WindowRun run, int start, int end)
    {
        var column = run.Columns[ValueColumn];
        for (var i = start; i < end; i++)
        {
            Source[i] = NullRow;
            for (var r = run.FrameLo[i]; r <= run.FrameHi[i]; r++)
            {
                if (!run.Excluded(i, r) && column.IsValid(r))
                {
                    Source[i] = r;
                    break;
                }
            }
        }
    }
}

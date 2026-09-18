using System.Runtime.InteropServices;
using Chalk.Catalog;
using Chalk.Execution.Aggregation;
using Chalk.Execution.Expressions;
using Chalk.Execution.Numeric;
using Chalk.Execution.Vectors;
using Chalk.Ir;
using Chalk.Sources;

namespace Chalk.Execution.Windowing;

/// <summary>
/// How far behind the cursor one streaming call reads, which is what the operator keeps
/// (<c>13-window-functions.md</c> §4.1). <see cref="Frame"/> means "the frame's own rows", which is
/// only bounded when the frame's lower bound is; <see cref="Rows"/> is a fixed distance, which is
/// what <c>LAG</c> asks for.
/// </summary>
internal readonly record struct WindowLookBack(bool Frame, long Rows)
{
    /// <summary>Nothing behind the current row.</summary>
    public static WindowLookBack None => new(false, 0);

    /// <summary>Every row of the frame.</summary>
    public static WindowLookBack WholeFrame => new(true, 0);

    /// <summary>A fixed number of rows behind the current row.</summary>
    public static WindowLookBack Behind(long rows) => new(false, rows);
}

/// <summary>
/// One window call on the streaming path: it is handed each row in order with the frame the operator
/// has just settled, and appends that row's answer straight into the output column. There is no
/// per-row result storage, because a row's answer is written the moment it is final (D258.4).
/// </summary>
/// <remarks>
/// The arithmetic is the buffered evaluators' own, line for line, because the corpus is asserted
/// byte-identical under both paths: the same sliding accumulator with the same re-sum interval, the
/// same monotonic deque with the same tie rule, the same rank arithmetic.
/// </remarks>
internal abstract class StreamingWindowCall
{
    protected StreamingWindowCall(ChalkType resultType)
    {
        ResultType = resultType;
        ResultKind = ColumnKinds.Of(resultType);
        ResultWidth = ColumnKinds.Width(ResultKind);
        Lane = new byte[Math.Max(ResultWidth, 1)];
    }

    public ChalkType ResultType { get; }

    protected ColumnKind ResultKind { get; }

    protected int ResultWidth { get; }

    /// <summary>One reused result lane. Allocated with the operator, never per row.</summary>
    protected byte[] Lane { get; }

    /// <summary>How far behind the cursor this call reads.</summary>
    public abstract WindowLookBack LookBack { get; }

    /// <summary>Whether this call reads its row's peer group.</summary>
    public virtual bool NeedsPeers => false;

    /// <summary>Claims what this call needs for one execution.</summary>
    public virtual void Begin(ExecutionArena arena, IReadOnlyList<ScalarValue> parameters)
    {
    }

    /// <summary>Hands it back, in the run's <c>finally</c>.</summary>
    public virtual void Release(ExecutionArena arena)
    {
    }

    /// <summary>A new partition begins at <paramref name="start"/>.</summary>
    public abstract void StartPartition(long start);

    /// <summary>Appends row <paramref name="row"/>'s answer, whose frame is <c>[lo, hi]</c>.</summary>
    public abstract void Emit(
        ColumnCopier copier, StreamingWindowRun run, long row, long lo, long hi);
}

/// <summary>
/// What one window compiles to on the streaming path: the calls, in the plan's order, and the frame
/// states they share — one per distinct argument column across the calls over it (D258.4).
/// </summary>
internal sealed class StreamingWindowPlan
{
    public required StreamingWindowCall[] Calls { get; init; }

    /// <summary>
    /// The shared sliding frames, one per distinct argument. The operator advances these before the
    /// calls write, so a window's <c>AVG</c> walks its frame's edges once rather than twice.
    /// </summary>
    public required StreamingWindowArgument[] Arguments { get; init; }
}

/// <summary>
/// One frame state, shared by every sliding aggregate over the same argument (D258.4). The planner
/// reduces <c>AVG(x)</c> to <c>COUNT(x)</c> and <c>SUM(x)</c> before the window, so two calls arrive
/// over one column; the frame's edges are a property of the window and the argument, not of the
/// function, so they are walked once and every accumulator over that argument is fed from the walk.
/// </summary>
/// <remarks>
/// The walk is the buffered path's, unchanged: remove first, so the intermediate window is a subset
/// of both frames and an exact sum never visits a value neither frame could hold; recompute from
/// scratch whenever the new frame is clear of the old one. Each accumulator still sees exactly the
/// rows it saw before, in the same order, which is what keeps a floating-point sum byte-identical.
/// </remarks>
internal sealed class StreamingWindowArgument
{
    private readonly int _column;
    private readonly StreamingWindowAggregate[] _members;

    private long _low;
    private long _high;

    public StreamingWindowArgument(int column, StreamingWindowAggregate[] members)
    {
        _column = column;
        _members = members;
    }

    /// <summary>The argument column these accumulators share, or <c>-1</c> for <c>COUNT(*)</c>.</summary>
    public int Column => _column;

    public void StartPartition(long start)
    {
        foreach (var member in _members)
        {
            member.Reset();
        }

        _low = start;
        _high = start - 1;
    }

    /// <summary>Moves the shared frame to <c>[lo, hi]</c>, feeding every accumulator over it.</summary>
    public void Advance(StreamingWindowRun run, long lo, long hi)
    {
        var column = run.Column(_column >= 0 ? _column : 0);
        if (lo > hi)
        {
            foreach (var member in _members)
            {
                member.Reset();
            }

            _low = lo;
            _high = lo - 1;
            return;
        }

        if (_low > _high || lo > _high || hi < _low)
        {
            foreach (var member in _members)
            {
                member.Reset();
            }

            for (var r = lo; r <= hi; r++)
            {
                Add(run, column, r);
            }
        }
        else
        {
            for (var r = _low; r < lo; r++)
            {
                Remove(run, column, r);
            }

            for (var r = _high + 1; r <= hi; r++)
            {
                Add(run, column, r);
            }
        }

        _low = lo;
        _high = hi;
    }

    private void Add(StreamingWindowRun run, WindowColumn column, long row)
    {
        if (_column < 0)
        {
            foreach (var member in _members)
            {
                member.AddRow();
            }

            return;
        }

        // The row's position in the store and its validity are the argument's, not each call's: one
        // lookup and one bit test for however many accumulators read the column.
        var local = run.Local(row);
        if (!column.IsValid(local))
        {
            return;
        }

        foreach (var member in _members)
        {
            member.AddValue(column, local);
        }
    }

    private void Remove(StreamingWindowRun run, WindowColumn column, long row)
    {
        if (_column < 0)
        {
            foreach (var member in _members)
            {
                member.RemoveRow();
            }

            return;
        }

        var local = run.Local(row);
        if (!column.IsValid(local))
        {
            return;
        }

        foreach (var member in _members)
        {
            member.RemoveValue(column, local);
        }
    }
}

/// <summary>
/// <c>COUNT</c>, <c>SUM</c>, <c>SUM0</c> and <c>AVG</c> over a frame that ends at or before the
/// current row: the sliding accumulator of §4, fed by the <see cref="StreamingWindowArgument"/> that
/// owns the frame's edges and reading the rows the operator is still holding rather than a buffered
/// copy of the input.
/// </summary>
/// <remarks>
/// A floating-point sum is rebuilt from scratch every
/// <see cref="WindowAggregateEvaluator.FloatingResumInterval"/> rows exactly as the buffered path
/// rebuilds it, which is why such a call only streams under a bounded lower bound: the rebuild reads
/// the whole frame, and an <c>UNBOUNDED PRECEDING</c> frame is the whole partition.
/// </remarks>
internal sealed class StreamingWindowAggregate : StreamingWindowCall
{
    private readonly AggregateFunctionId _function;
    private readonly int _valueColumn;
    private readonly WindowLookBack _lookBack;

    /// <summary>
    /// As on <c>WindowAggregateEvaluator</c>, and for the same reason: a <c>COUNT(x)</c>'s result is
    /// BIGINT, so the switch below used to sum the argument into a checked long that <see cref="Write"/>
    /// never reads (ADR 0044). The two paths are byte-identical (ADR 0040), so they skip it together.
    /// </summary>
    private readonly bool _countsOnly;

    private long _count;
    private long _integer;
    private double _double;
    private float _single;
    private decimal _decimal;
    private int _sinceResum;

    public StreamingWindowAggregate(
        ChalkType resultType, AggregateFunctionId function, int valueColumn, WindowLookBack lookBack)
        : base(resultType)
    {
        _function = function;
        _valueColumn = valueColumn;
        _lookBack = lookBack;
        _countsOnly = function == AggregateFunctionId.Count;
    }

    public override WindowLookBack LookBack => _lookBack;

    /// <summary>The argument column, or <c>-1</c> for <c>COUNT(*)</c>. What groups the calls.</summary>
    public int ValueColumn => _valueColumn;

    public override void StartPartition(long start)
    {
    }

    public override void Emit(
        ColumnCopier copier, StreamingWindowRun run, long row, long lo, long hi) =>
        Write(copier, run, run.Column(_valueColumn >= 0 ? _valueColumn : 0), lo, hi);

    /// <summary>Empties the accumulator. The argument calls it; the frame's edges are its business.</summary>
    public void Reset()
    {
        _count = 0;
        _integer = 0;
        _double = 0;
        _single = 0;
        _decimal = 0;
        _sinceResum = 0;
    }

    /// <summary>One more row in the frame, for a <c>COUNT(*)</c> that reads no column.</summary>
    public void AddRow() => _count++;

    /// <summary>One fewer.</summary>
    public void RemoveRow() => _count--;

    /// <summary>One more row in the frame, whose argument the caller has already found valid.</summary>
    public void AddValue(WindowColumn column, int local)
    {
        _count++;
        if (_countsOnly)
        {
            // The count is the whole answer, so the value is never read.
            return;
        }

        switch (ResultKind)
        {
            case ColumnKind.Float:
                _single += (float)column.Double(local);
                break;
            case ColumnKind.Double:
                _double += column.Double(local);
                break;
            case ColumnKind.Decimal128:
                _decimal += column.Decimal(local);
                break;
            default:
                _integer = checked(_integer + Integer(column, local));
                break;
        }
    }

    /// <summary>One row leaving the frame, whose argument the caller has already found valid.</summary>
    public void RemoveValue(WindowColumn column, int local)
    {
        _count--;
        if (_countsOnly)
        {
            return;
        }

        switch (ResultKind)
        {
            case ColumnKind.Float:
                _single -= (float)column.Double(local);
                break;
            case ColumnKind.Double:
                _double -= column.Double(local);
                break;
            case ColumnKind.Decimal128:
                _decimal -= column.Decimal(local);
                break;
            default:
                _integer = checked(_integer - Integer(column, local));
                break;
        }
    }

    private static long Integer(WindowColumn column, int row) => column.Kind switch
    {
        ColumnKind.Float or ColumnKind.Double => checked((long)Math.Truncate(column.Double(row))),
        ColumnKind.Decimal128 => checked((long)decimal.Truncate(column.Decimal(row))),
        _ => column.Integer(row),
    };

    private void Write(
        ColumnCopier copier, StreamingWindowRun run, WindowColumn column, long lo, long hi)
    {
        var floating = ResultKind is ColumnKind.Float or ColumnKind.Double;
        if (floating && ++_sinceResum >= WindowAggregateEvaluator.FloatingResumInterval)
        {
            _sinceResum = 0;
            _single = 0;
            _double = 0;
            var count = _count;
            for (var r = lo; r <= hi; r++)
            {
                var local = run.Local(r);
                if (_valueColumn < 0 || column.IsValid(local))
                {
                    if (ResultKind == ColumnKind.Float)
                    {
                        _single += (float)column.Double(local);
                    }
                    else
                    {
                        _double += column.Double(local);
                    }
                }
            }

            _count = count;
        }

        var lane = Lane.AsSpan(0, ResultWidth);
        lane.Clear();
        switch (_function)
        {
            case AggregateFunctionId.Count:
                NumberLanes.WriteInteger(ResultKind, ResultType, _count, lane);
                copier.AppendRaw(lane, valid: true);
                return;

            case AggregateFunctionId.Sum0:
                WriteNumber(lane);
                copier.AppendRaw(lane, valid: true);
                return;

            case AggregateFunctionId.Avg:
                if (_count > 0)
                {
                    WriteAverage(lane);
                }

                copier.AppendRaw(lane, _count > 0);
                return;

            default:
                if (_count > 0)
                {
                    WriteNumber(lane);
                }

                copier.AppendRaw(lane, _count > 0);
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
}

/// <summary>
/// <c>MIN</c> and <c>MAX</c> over a frame that ends at or before the current row, through the
/// monotonic deque of §4 — here a ring whose entries are rows of the input rather than of a buffered
/// copy, so it grows with the frame and not with the query.
/// </summary>
internal sealed class StreamingWindowExtremum : StreamingWindowCall
{
    private const int InitialCapacity = 64;

    private readonly bool _max;
    private readonly int _valueColumn;

    private long[] _deque = [];
    private int _mask;
    private long _head;
    private long _tail;
    private long _filled;
    private ExecutionArena? _arena;

    public StreamingWindowExtremum(ChalkType resultType, int valueColumn, bool max)
        : base(resultType)
    {
        _valueColumn = valueColumn;
        _max = max;
    }

    public override WindowLookBack LookBack => WindowLookBack.WholeFrame;

    public override void Begin(ExecutionArena arena, IReadOnlyList<ScalarValue> parameters)
    {
        _arena = arena;
        _deque = arena.Rent<long>(InitialCapacity);
        _mask = _deque.Length - 1;
        if ((_deque.Length & _mask) != 0)
        {
            // The arena hands back at least what was asked for; the ring wants a power of two, so it
            // uses the largest one that fits in what it was given.
            var size = 1;
            while (size * 2 <= _deque.Length)
            {
                size *= 2;
            }

            _mask = size - 1;
        }

        _head = 0;
        _tail = 0;
    }

    public override void Release(ExecutionArena arena)
    {
        arena.Return(_deque);
        _deque = [];
        _arena = null;
    }

    public override void StartPartition(long start)
    {
        _head = 0;
        _tail = 0;
        _filled = start - 1;
    }

    public override void Emit(
        ColumnCopier copier, StreamingWindowRun run, long row, long lo, long hi)
    {
        if (lo > hi)
        {
            copier.AppendRaw(default, valid: false);
            return;
        }

        var column = run.Column(_valueColumn);
        if (lo > _filled)
        {
            _head = 0;
            _tail = 0;
            _filled = lo - 1;
        }

        while (_filled < hi)
        {
            _filled++;
            if (!column.IsValid(run.Local(_filled)))
            {
                continue;
            }

            while (_tail > _head && !Better(run, column, _deque[(int)(_tail - 1) & _mask], _filled))
            {
                _tail--;
            }

            Push(_filled);
        }

        while (_head < _tail && _deque[(int)_head & _mask] < lo)
        {
            _head++;
        }

        if (_head < _tail)
        {
            run.Hold.AppendRowTo(copier, _valueColumn, _deque[(int)_head & _mask]);
            return;
        }

        copier.AppendRaw(default, valid: false);
    }

    private void Push(long row)
    {
        if (_tail - _head > _mask)
        {
            Grow();
        }

        _deque[(int)_tail & _mask] = row;
        _tail++;
    }

    private void Grow()
    {
        var arena = _arena
            ?? throw new InvalidOperationException("the window extremum is not attached to an arena.");
        var size = _mask + 1;
        var grown = arena.Rent<long>(size * 2);
        var count = (int)(_tail - _head);
        for (var i = 0; i < count; i++)
        {
            grown[i] = _deque[(int)(_head + i) & _mask];
        }

        arena.Return(_deque);
        _deque = grown;

        var capacity = 1;
        while (capacity * 2 <= grown.Length)
        {
            capacity *= 2;
        }

        _mask = capacity - 1;
        _head = 0;
        _tail = count;
    }

    /// <summary>
    /// Whether the held row stays when the arriving one joins: only if it is strictly better, which
    /// is the buffered deque's rule and therefore picks the same row among equals.
    /// </summary>
    private bool Better(StreamingWindowRun run, WindowColumn column, long held, long arriving)
    {
        Span<byte> left = stackalloc byte[16];
        Span<byte> right = stackalloc byte[16];
        var comparison = LaneComparer.Compare(
            column.Kind,
            column.Lane(run.Local(held), left),
            column.Lane(run.Local(arriving), right));
        return _max ? comparison > 0 : comparison < 0;
    }
}

/// <summary>
/// <c>ROW_NUMBER</c>, <c>RANK</c> and <c>DENSE_RANK</c>: the three of the ranking family that need
/// only where the partition started and where the current peer group starts, both of which the
/// streaming operator knows without reading ahead. <c>NTILE</c>, <c>PERCENT_RANK</c> and
/// <c>CUME_DIST</c> divide by the partition's total row count and stay on the buffered path.
/// </summary>
internal sealed class StreamingWindowRanking : StreamingWindowCall
{
    private readonly WindowFunctionId _function;

    private long _denseRank;
    private long _peerStart = -1;

    public StreamingWindowRanking(ChalkType resultType, WindowFunctionId function)
        : base(resultType) => _function = function;

    public override WindowLookBack LookBack => WindowLookBack.None;

    public override bool NeedsPeers =>
        _function is WindowFunctionId.Rank or WindowFunctionId.DenseRank;

    public override void StartPartition(long start)
    {
        _denseRank = 0;
        _peerStart = -1;
    }

    public override void Emit(
        ColumnCopier copier, StreamingWindowRun run, long row, long lo, long hi)
    {
        var peerStart = run.PeerStart;
        if (peerStart != _peerStart)
        {
            _peerStart = peerStart;
            _denseRank++;
        }

        var value = _function switch
        {
            WindowFunctionId.RowNumber => row - run.PartitionStart + 1,
            WindowFunctionId.Rank => peerStart - run.PartitionStart + 1,
            _ => _denseRank,
        };

        var lane = Lane.AsSpan(0, ResultWidth);
        lane.Clear();
        NumberLanes.WriteInteger(ResultKind, ResultType, value, lane);
        copier.AppendRaw(lane, valid: true);
    }
}

/// <summary>
/// <c>LAG</c> with a literal, non-negative offset: index arithmetic over the partition, reading a row
/// the operator is still holding. <c>LEAD</c> reads forwards and stays buffered; so does a <c>LAG</c>
/// whose offset is a parameter, because a negative one would be a <c>LEAD</c> and the path is chosen
/// before any parameter is bound.
/// </summary>
internal sealed class StreamingWindowLag : StreamingWindowCall
{
    private readonly int _valueColumn;
    private readonly long _offset;
    private readonly bool _nullOffset;
    private readonly WindowConstant? _default;

    public StreamingWindowLag(
        ChalkType resultType,
        int valueColumn,
        long offset,
        bool nullOffset,
        WindowConstant? defaultValue)
        : base(resultType)
    {
        _valueColumn = valueColumn;
        _offset = offset;
        _nullOffset = nullOffset;
        _default = defaultValue;
    }

    public override WindowLookBack LookBack => WindowLookBack.Behind(_offset);

    public override void StartPartition(long start)
    {
    }

    public override void Emit(
        ColumnCopier copier, StreamingWindowRun run, long row, long lo, long hi)
    {
        if (_nullOffset)
        {
            copier.AppendRaw(default, valid: false);
            return;
        }

        var target = row - _offset;
        if (target >= run.PartitionStart)
        {
            run.Hold.AppendRowTo(copier, _valueColumn, target);
            return;
        }

        if (_default is not null)
        {
            copier.AppendConstant(_default.Bind(run.Parameters), 1);
            return;
        }

        copier.AppendRaw(default, valid: false);
    }
}

/// <summary>
/// <c>FIRST_VALUE</c>, <c>LAST_VALUE</c> and <c>ANY_VALUE</c> over a frame that ends at or before the
/// current row. Under a bounded lower bound the frame's own rows are held and the scan is the
/// buffered path's; under <c>UNBOUNDED PRECEDING</c> the answer is the partition's first counting
/// row, which is pinned once — a single row copied into a copier of its own, so it survives the hold
/// moving on beneath it.
/// </summary>
internal sealed class StreamingWindowFrameValue : StreamingWindowCall
{
    private readonly int _valueColumn;
    private readonly bool _last;
    private readonly bool _ignoreNulls;
    private readonly bool _pinned;
    private readonly WindowLookBack _lookBack;
    private readonly ColumnCopier? _pin;

    private ColumnView _pinView;
    private bool _havePin;
    private long _scanned;

    public StreamingWindowFrameValue(
        ChalkType resultType,
        ChalkType valueType,
        int valueColumn,
        bool last,
        bool ignoreNulls,
        bool pinned,
        WindowLookBack lookBack)
        : base(resultType)
    {
        _valueColumn = valueColumn;
        _last = last;
        _ignoreNulls = ignoreNulls;
        _pinned = pinned;
        _lookBack = lookBack;
        _pin = pinned ? new ColumnCopier(valueType) : null;
    }

    public override WindowLookBack LookBack => _lookBack;

    public override void StartPartition(long start)
    {
        _havePin = false;
        _scanned = start - 1;
    }

    public override void Emit(
        ColumnCopier copier, StreamingWindowRun run, long row, long lo, long hi)
    {
        if (_pinned)
        {
            EmitPinned(copier, run, lo, hi);
            return;
        }

        var column = run.Column(_valueColumn);
        var found = -1L;
        if (hi >= lo)
        {
            if (_last)
            {
                for (var r = hi; r >= lo; r--)
                {
                    if (Counts(run, column, r))
                    {
                        found = r;
                        break;
                    }
                }
            }
            else
            {
                for (var r = lo; r <= hi; r++)
                {
                    if (Counts(run, column, r))
                    {
                        found = r;
                        break;
                    }
                }
            }
        }

        if (found < 0)
        {
            copier.AppendRaw(default, valid: false);
            return;
        }

        run.Hold.AppendRowTo(copier, _valueColumn, found);
    }

    private void EmitPinned(ColumnCopier copier, StreamingWindowRun run, long lo, long hi)
    {
        if (!_havePin && hi >= lo)
        {
            var column = run.Column(_valueColumn);
            for (var r = Math.Max(lo, _scanned + 1); r <= hi; r++)
            {
                if (!Counts(run, column, r))
                {
                    continue;
                }

                _pin!.Begin();
                run.Hold.AppendRowTo(_pin, _valueColumn, r);
                _pinView = _pin.FinishView();
                _havePin = true;
                break;
            }

            _scanned = Math.Max(_scanned, hi);
        }

        if (_havePin)
        {
            copier.AppendRow(_pinView, 0);
            return;
        }

        copier.AppendRaw(default, valid: false);
    }

    private bool Counts(StreamingWindowRun run, WindowColumn column, long row) =>
        !_ignoreNulls || column.IsValid(run.Local(row));
}

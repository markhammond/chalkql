using Chalk.Catalog;
using Chalk.Execution.Vectors;
using Chalk.Ir;
using Chalk.Sources;

namespace Chalk.Execution.Windowing;

/// <summary>
/// The facts one row's calls share on the streaming path: where its partition and peer group start
/// and end, and where the rows are. The operator fills it in before each row is emitted, so a call
/// reads the same numbers the buffered path reads out of <see cref="WindowRun"/>'s arrays.
/// </summary>
internal sealed class StreamingWindowRun
{
    public required WindowHold Hold { get; init; }

    public IReadOnlyList<ScalarValue> Parameters { get; set; } = [];

    /// <summary>The first row of the partition being computed.</summary>
    public long PartitionStart { get; set; }

    /// <summary>One past its last row, or <see cref="Open"/> while the partition may still grow.</summary>
    public long PartitionEnd { get; set; } = Open;

    /// <summary>The first row of the current row's peer group.</summary>
    public long PeerStart { get; set; }

    /// <summary>One past its last row, or <see cref="Open"/> while the group may still grow.</summary>
    public long PeerEnd { get; set; } = Open;

    /// <summary>Rows read so far, over the whole input.</summary>
    public long ReadEnd { get; set; }

    /// <summary>A bound that is not yet known, because the rows that would settle it have not arrived.</summary>
    public const long Open = long.MaxValue;

    public WindowColumn Column(int column) => Hold.Column(column);

    public int Local(long row) => Hold.Local(row);
}

/// <summary>
/// Peer groups, one partition at a time and without the partition in memory
/// (<c>13-window-functions.md</c> §4.1). A group is closed by the first row with a different order
/// key or by the end of the partition; until one of those has been read, the group is
/// <see cref="StreamingWindowRun.Open"/> and the rows in it are not final.
/// </summary>
internal sealed class StreamingWindowPeers
{
    private readonly WindowRowComparer _order;
    private readonly WindowHold _hold;

    private long _start;
    private long _end;
    private long _scan;

    public StreamingWindowPeers(WindowRowComparer order, WindowHold hold)
    {
        _order = order;
        _hold = hold;
    }

    public long Start => _start;

    /// <summary>
    /// The oldest row the group scan still compares against. Groups are contiguous, so the scan asks
    /// whether each row agrees with the one before it — which means the rows a closed group left
    /// behind are not retained, only the scan's own predecessor.
    /// </summary>
    public long Retain =>
        _order.ColumnCount == 0 ? StreamingWindowRun.Open : Math.Max(_scan, _start + 1) - 1;

    public void StartPartition(long start)
    {
        _start = start;
        _end = StreamingWindowRun.Open;
        _scan = start;
    }

    /// <summary>
    /// Moves to <paramref name="row"/>'s group and says where it ends, or
    /// <see cref="StreamingWindowRun.Open"/> when the input has not settled it yet. With no order
    /// keys the whole partition is one group, which is the rule that makes
    /// <c>RANGE UNBOUNDED PRECEDING</c> over an unordered window the whole partition.
    /// </summary>
    public long Advance(long row, long readEnd, long partitionEnd)
    {
        if (_order.ColumnCount == 0)
        {
            return _end = partitionEnd;
        }

        // Groups are contiguous, so the next one starts where this one ended — and a row may be
        // several groups past the one that was open when it was last asked about, which is why this
        // is a loop and not a test.
        while (true)
        {
            if (_end != StreamingWindowRun.Open)
            {
                if (row < _end)
                {
                    return _end;
                }

                _start = _end;
                _end = StreamingWindowRun.Open;
                _scan = _start;
            }

            var cursor = Math.Max(_scan, _start + 1);
            while (cursor < readEnd && cursor < partitionEnd
                && _order.SameGroup(_hold.Local(cursor - 1), _hold.Local(cursor)))
            {
                cursor++;
            }

            _scan = cursor;
            if (cursor >= partitionEnd)
            {
                _end = partitionEnd;
            }
            else if (cursor < readEnd)
            {
                _end = cursor;
            }
            else
            {
                // The group runs to the last row read and the partition has not ended: whether the
                // next row joins it is a question the next batch answers.
                return StreamingWindowRun.Open;
            }
        }
    }
}

/// <summary>
/// Each row's frame on the streaming path: the same <c>[lo, hi]</c> the buffered
/// <see cref="WindowFrameBuilder"/> writes into its arrays, computed one row at a time from cursors
/// that only move forward (<c>13-window-functions.md</c> §4.1, D258.4).
///
/// <para>
/// Only the frames that end at or before the current row reach here, so <c>hi</c> is never past the
/// cursor and the only thing that can be unsettled is a <c>RANGE</c> upper bound waiting for the
/// first row that falls outside it. <see cref="WaterMark"/> is the other half: the lowest row any
/// frame from here on can name, which is what the operator releases below.
/// </para>
/// </summary>
internal sealed class StreamingWindowFrame
{
    private readonly WindowSpec _spec;
    private readonly bool _range;
    private readonly bool _hasOffsets;
    private readonly ColumnKind _lane;
    private readonly int _keyColumn;
    private readonly bool _descending;

    private long _lowerRows;
    private long _upperRows;
    private long _lowerLong;
    private long _upperLong;
    private double _lowerDouble;
    private double _upperDouble;
    private decimal _lowerDecimal;
    private decimal _upperDecimal;

    private long _lowerCursor;
    private long _upperCursor;
    private long _nonNullStart;

    public StreamingWindowFrame(WindowSpec spec)
    {
        _spec = spec;
        _range = spec.Mode == FrameMode.Range;
        _hasOffsets = spec.HasOffsets;
        if (_range && _hasOffsets)
        {
            var key = spec.RangeKey;
            _keyColumn = key.Column;
            _descending = key.Descending;
            _lane = WindowFrames.RangeLane(spec.InputTypes[key.Column]);
        }
    }

    /// <summary>Binds the offsets once, exactly as the buffered builder does.</summary>
    public void Begin(IReadOnlyList<ScalarValue> parameters)
    {
        if (!_range)
        {
            _lowerRows = WindowFrameBuilder.RowOffset(_spec.Lower, parameters);
            _upperRows = WindowFrameBuilder.RowOffset(_spec.Upper, parameters);
            return;
        }

        if (!_hasOffsets)
        {
            return;
        }

        var keyType = _spec.InputTypes[_keyColumn];
        switch (_lane)
        {
            case ColumnKind.Int64:
                _lowerLong = WindowFrameBuilder.LongOffset(_spec.Lower, parameters, keyType);
                _upperLong = WindowFrameBuilder.LongOffset(_spec.Upper, parameters, keyType);
                return;
            case ColumnKind.Double:
                _lowerDouble = WindowFrameBuilder.DoubleOffset(_spec.Lower, parameters);
                _upperDouble = WindowFrameBuilder.DoubleOffset(_spec.Upper, parameters);
                return;
            default:
                _lowerDecimal = WindowFrameBuilder.DecimalOffset(_spec.Lower, parameters);
                _upperDecimal = WindowFrameBuilder.DecimalOffset(_spec.Upper, parameters);
                return;
        }
    }

    public void StartPartition(long start)
    {
        _lowerCursor = start;
        _upperCursor = start;
        _nonNullStart = -1;
    }

    /// <summary>
    /// Row <paramref name="row"/>'s frame, or <see langword="false"/> when the upper bound needs a row
    /// that has not been read: a <c>RANGE</c> frame's last row is only known once a row outside it has
    /// arrived, or the partition has ended.
    /// </summary>
    public bool TryBounds(StreamingWindowRun run, long row, out long lo, out long hi)
    {
        var start = run.PartitionStart;
        if (!_range)
        {
            lo = _spec.Lower.Kind switch
            {
                FrameBoundKind.UnboundedPreceding => start,
                FrameBoundKind.Preceding => row - _lowerRows,
                _ => row,
            };
            hi = _spec.Upper.Kind == FrameBoundKind.CurrentRow ? row : row - _upperRows;
            lo = Math.Max(lo, start);
            hi = Math.Max(hi, start - 1);
            return true;
        }

        var peerEnd = run.PeerEnd;
        if (!_hasOffsets)
        {
            if (peerEnd == StreamingWindowRun.Open)
            {
                lo = hi = 0;
                return false;
            }

            lo = _spec.Lower.Kind == FrameBoundKind.UnboundedPreceding ? start : run.PeerStart;
            hi = peerEnd - 1;
            return true;
        }

        var column = run.Column(_keyColumn);
        var local = run.Local(row);
        var nullKey = !column.IsValid(local);
        if (!nullKey && _nonNullStart < 0)
        {
            _nonNullStart = row;
            _lowerCursor = row;
            _upperCursor = row;
        }

        var needsPeers = nullKey
            || _spec.Lower.Kind == FrameBoundKind.CurrentRow
            || _spec.Upper.Kind == FrameBoundKind.CurrentRow;
        if (needsPeers && peerEnd == StreamingWindowRun.Open)
        {
            lo = hi = 0;
            return false;
        }

        lo = _spec.Lower.Kind switch
        {
            FrameBoundKind.UnboundedPreceding => start,
            FrameBoundKind.CurrentRow => run.PeerStart,
            _ when nullKey => run.PeerStart,
            _ => LowerBound(run, column, row),
        };

        if (_spec.Upper.Kind == FrameBoundKind.CurrentRow || nullKey)
        {
            hi = peerEnd - 1;
            return true;
        }

        if (!TryUpperBound(run, column, row, out hi))
        {
            lo = hi = 0;
            return false;
        }

        return true;
    }

    /// <summary>
    /// The lowest row still read once every row up to <paramref name="answered"/> has its answer.
    ///
    /// <para>
    /// It is the frame of the row just answered, not of the next one: a sliding accumulator holds
    /// that frame and takes its first row out again when the frame moves on, so the row leaving is
    /// read one row after the hold would otherwise have let it go.
    /// </para>
    /// </summary>
    public long WaterMark(StreamingWindowRun run, long answered)
    {
        if (!_range)
        {
            return _spec.Lower.Kind switch
            {
                FrameBoundKind.UnboundedPreceding => run.PartitionStart,
                FrameBoundKind.Preceding => Math.Max(run.PartitionStart, answered - _lowerRows),
                _ => Math.Max(run.PartitionStart, answered),
            };
        }

        if (_spec.Lower.Kind == FrameBoundKind.UnboundedPreceding)
        {
            return run.PartitionStart;
        }

        // The lower cursor only moves forward, so wherever it stands is a floor on every bound still
        // to be computed; a partition that has not reached a non-NULL key has not moved it at all.
        // A CURRENT ROW lower bound is the peer group, which the operator retains anyway.
        return !_hasOffsets || _spec.Lower.Kind == FrameBoundKind.CurrentRow
            ? run.PeerStart
            : _nonNullStart < 0 ? run.PartitionStart : _lowerCursor;
    }

    private long LowerBound(StreamingWindowRun run, WindowColumn column, long row)
    {
        var cursor = Math.Max(_lowerCursor, _nonNullStart);
        switch (_lane)
        {
            case ColumnKind.Int64:
            {
                var target = Target(column.Integer(run.Local(row)), _spec.Lower.Kind, _lowerLong);
                while (cursor < row && Below(column.Integer(run.Local(cursor)), target))
                {
                    cursor++;
                }

                break;
            }

            case ColumnKind.Double:
            {
                var target = Target(column.Double(run.Local(row)), _spec.Lower.Kind, _lowerDouble);
                while (cursor < row && Below(column.Double(run.Local(cursor)), target))
                {
                    cursor++;
                }

                break;
            }

            default:
            {
                var target = Target(column.Decimal(run.Local(row)), _spec.Lower.Kind, _lowerDecimal);
                while (cursor < row && Below(column.Decimal(run.Local(cursor)), target))
                {
                    cursor++;
                }

                break;
            }
        }

        _lowerCursor = cursor;
        return cursor;
    }

    private bool TryUpperBound(
        StreamingWindowRun run, WindowColumn column, long row, out long hi)
    {
        var cursor = Math.Max(_upperCursor, _nonNullStart);
        var limit = Math.Min(run.ReadEnd, run.PartitionEnd);
        var settled = false;
        switch (_lane)
        {
            case ColumnKind.Int64:
            {
                var target = Target(column.Integer(run.Local(row)), _spec.Upper.Kind, _upperLong);
                while (cursor < limit
                    && column.IsValid(run.Local(cursor))
                    && AtOrBefore(column.Integer(run.Local(cursor)), target))
                {
                    cursor++;
                }

                settled = cursor < limit;
                break;
            }

            case ColumnKind.Double:
            {
                var target = Target(column.Double(run.Local(row)), _spec.Upper.Kind, _upperDouble);
                while (cursor < limit
                    && column.IsValid(run.Local(cursor))
                    && AtOrBefore(column.Double(run.Local(cursor)), target))
                {
                    cursor++;
                }

                settled = cursor < limit;
                break;
            }

            default:
            {
                var target = Target(column.Decimal(run.Local(row)), _spec.Upper.Kind, _upperDecimal);
                while (cursor < limit
                    && column.IsValid(run.Local(cursor))
                    && AtOrBefore(column.Decimal(run.Local(cursor)), target))
                {
                    cursor++;
                }

                settled = cursor < limit;
                break;
            }
        }

        _upperCursor = cursor;

        // The cursor stopped inside what has been read, or the partition itself has ended: either way
        // no later row can join the frame. Otherwise the answer waits for the next batch.
        if (!settled && run.PartitionEnd == StreamingWindowRun.Open)
        {
            hi = 0;
            return false;
        }

        hi = cursor - 1;
        return true;
    }

    /// <summary>The key a bound compares against, as <see cref="WindowFrames"/> defines it.</summary>
    private T Target<T>(T key, FrameBoundKind kind, T offset)
        where T : System.Numerics.INumber<T>
    {
        var back = kind == FrameBoundKind.Preceding;
        return back == _descending ? key + offset : key - offset;
    }

    private bool Below<T>(T key, T target)
        where T : System.Numerics.INumber<T> => _descending ? key > target : key < target;

    private bool AtOrBefore<T>(T key, T target)
        where T : System.Numerics.INumber<T> => _descending ? key >= target : key <= target;
}

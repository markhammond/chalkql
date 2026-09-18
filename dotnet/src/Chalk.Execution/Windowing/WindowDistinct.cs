using System.Runtime.InteropServices;
using Chalk.Catalog;
using Chalk.Execution.Expressions;
using Chalk.Execution.Numeric;
using Chalk.Execution.Vectors;
using Chalk.Ir;
using Chalk.Sources;

namespace Chalk.Execution.Windowing;

/// <summary>
/// A counted multiset of lane values: value → how many rows of the current frame hold it (D56,
/// <c>14-windows-ii.md</c> §2).
/// </summary>
/// <remarks>
/// Open addressing over the bytes, exactly as <see cref="ByteSet"/> does, plus a count per slot so a
/// row leaving the frame can be removed as exactly as one entering it can be added. That is what
/// makes <c>DISTINCT</c> inside a window a sliding aggregate rather than a per-row recomputation: a
/// frame whose bounds only move forward costs one add and one remove per row however wide it is.
/// </remarks>
internal sealed class LaneMultiset
{
    private byte[]?[] _keys;
    private int[] _counts;
    private int _mask;
    private int _used;

    public LaneMultiset(int capacity = 16)
    {
        var size = 16;
        while (size < Math.Max(capacity, 1) * 2)
        {
            size <<= 1;
        }

        _keys = new byte[size][];
        _counts = new int[size];
        _mask = size - 1;
    }

    /// <summary>How many distinct values the frame holds. NULLs are not values (SQL).</summary>
    public int Distinct { get; private set; }

    public void Clear()
    {
        System.Array.Clear(_keys);
        System.Array.Clear(_counts);
        _used = 0;
        Distinct = 0;
    }

    /// <summary>Adds one occurrence; true when the value was not in the frame before.</summary>
    public bool Add(ReadOnlySpan<byte> value)
    {
        if (_used * 10 >= _keys.Length * 7)
        {
            Grow();
        }

        var slot = Find(value);
        if (_keys[slot] is null)
        {
            _keys[slot] = value.ToArray();
            _counts[slot] = 1;
            _used++;
            Distinct++;
            return true;
        }

        // A slot whose count fell to zero keeps its key, so the probe chain stays intact; the value
        // is nonetheless back in the frame and back in the distinct count.
        if (_counts[slot]++ == 0)
        {
            Distinct++;
            return true;
        }

        return false;
    }

    /// <summary>Removes one occurrence; true when that was the last of them.</summary>
    public bool Remove(ReadOnlySpan<byte> value)
    {
        var slot = Find(value);
        if (_keys[slot] is null || _counts[slot] == 0)
        {
            return false;
        }

        if (--_counts[slot] == 0)
        {
            Distinct--;
            return true;
        }

        return false;
    }

    private int Find(ReadOnlySpan<byte> value)
    {
        var slot = (int)(Hashing.Bytes(value) & (ulong)_mask);
        while (_keys[slot] is { } existing && !existing.AsSpan().SequenceEqual(value))
        {
            slot = (slot + 1) & _mask;
        }

        return slot;
    }

    private void Grow()
    {
        var keys = _keys;
        var counts = _counts;
        var size = keys.Length * 2;
        _keys = new byte[size][];
        _counts = new int[size];
        _mask = size - 1;
        for (var i = 0; i < keys.Length; i++)
        {
            if (keys[i] is not { } key || counts[i] == 0)
            {
                continue;
            }

            var slot = (int)(Hashing.Bytes(key) & (ulong)_mask);
            while (_keys[slot] is not null)
            {
                slot = (slot + 1) & _mask;
            }

            _keys[slot] = key;
            _counts[slot] = counts[i];
        }
    }
}

/// <summary>
/// <c>COUNT(DISTINCT x)</c>, <c>SUM(DISTINCT x)</c>, <c>SUM0</c> and <c>AVG</c> over a frame (D56).
/// </summary>
/// <remarks>
/// <para>
/// The multiset above is the state, one per call per partition: a row entering the frame is added and
/// a row leaving it removed, and the answer is the live keys (<c>COUNT</c>) or the sum of them
/// (<c>SUM</c>), maintained as the keys appear and disappear rather than recomputed.
/// </para>
/// <para>
/// An <c>EXCLUDE</c> gives that up and rebuilds the multiset per row. §2 offered a segment tree with
/// a set-union state as the alternative; ADR 0018 records why this is a recomputation instead — the
/// tree would hold a set per node, which is O(n log n) sets of memory for a saving that only shows on
/// frames far wider than any this engine has been asked for.
/// </para>
/// </remarks>
internal sealed class WindowDistinctEvaluator : WindowValueEvaluator
{
    private readonly AggregateFunctionId _function;
    private readonly int _valueColumn;
    private readonly LaneMultiset _seen = new();
    private readonly byte[] _scratch = new byte[16];

    private long _integer;
    private double _double;
    private float _single;
    private decimal _decimal;

    public WindowDistinctEvaluator(ChalkType resultType, AggregateFunctionId function, int valueColumn)
        : base(resultType)
    {
        _function = function;
        _valueColumn = valueColumn;
        if (function is AggregateFunctionId.Sum or AggregateFunctionId.Sum0 or AggregateFunctionId.Avg
            && !IrTypes.IsNumeric(resultType.Kind))
        {
            throw new UnsupportedFeatureException(
                $"{function}(DISTINCT) over {resultType} in a window",
                "SUM and AVG are defined for the numeric kinds (docs/design/02-ir.md §6).");
        }
    }

    public override void Compute(WindowRun run, int start, int end)
    {
        var column = run.Columns[_valueColumn];
        if (!run.NoExclusion)
        {
            for (var i = start; i < end; i++)
            {
                Reset();
                for (var r = run.FrameLo[i]; r <= run.FrameHi[i]; r++)
                {
                    if (!run.Excluded(i, r))
                    {
                        Add(column, r);
                    }
                }

                Write(i);
            }

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
                Write(i);
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
            Write(i);
        }
    }

    private void Reset()
    {
        _seen.Clear();
        _integer = 0;
        _double = 0;
        _single = 0;
        _decimal = 0;
    }

    private void Add(WindowColumn column, int row)
    {
        if (!column.IsValid(row))
        {
            return;
        }

        if (_seen.Add(column.Lane(row, _scratch)))
        {
            Accumulate(column, row, sign: 1);
        }
    }

    private void Remove(WindowColumn column, int row)
    {
        if (!column.IsValid(row))
        {
            return;
        }

        if (_seen.Remove(column.Lane(row, _scratch)))
        {
            Accumulate(column, row, sign: -1);
        }
    }

    /// <summary>Keeps the distinct sum current as a value joins or leaves the live set.</summary>
    private void Accumulate(WindowColumn column, int row, int sign)
    {
        switch (ResultKind)
        {
            case ColumnKind.Float:
                _single += sign * (float)column.Double(row);
                break;
            case ColumnKind.Double:
                _double += sign * column.Double(row);
                break;
            case ColumnKind.Decimal128:
                _decimal += sign * column.Decimal(row);
                break;
            default:
                _integer = checked(_integer + (sign * Integer(column, row)));
                break;
        }
    }

    private static long Integer(WindowColumn column, int row) => column.Kind switch
    {
        ColumnKind.Float or ColumnKind.Double => checked((long)Math.Truncate(column.Double(row))),
        ColumnKind.Decimal128 => checked((long)decimal.Truncate(column.Decimal(row))),
        _ => column.Integer(row),
    };

    private void Write(int row)
    {
        var lane = Lane(row);
        lane.Clear();
        var count = _seen.Distinct;

        if (_function == AggregateFunctionId.Count)
        {
            Valid[row] = true;
            NumberLanes.WriteInteger(ResultKind, ResultType, count, lane);
            return;
        }

        Valid[row] = _function == AggregateFunctionId.Sum0 || count > 0;
        if (!Valid[row])
        {
            return;
        }

        var divisor = _function == AggregateFunctionId.Avg ? Math.Max(count, 1) : 1;
        switch (ResultKind)
        {
            case ColumnKind.Float:
                MemoryMarshal.Write(lane, _single / divisor);
                break;
            case ColumnKind.Double:
                MemoryMarshal.Write(lane, _double / divisor);
                break;
            case ColumnKind.Decimal128:
                Decimals.Write(lane, _decimal / divisor, ResultType.Precision, ResultType.Scale);
                break;
            default:
                NumberLanes.WriteInteger(ResultKind, ResultType, _integer / divisor, lane);
                break;
        }
    }
}

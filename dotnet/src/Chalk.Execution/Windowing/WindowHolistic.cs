using System.Text;
using Chalk.Catalog;
using Chalk.Execution.Aggregation;
using Chalk.Execution.Numeric;
using Chalk.Execution.Vectors;
using Chalk.Ir;
using Chalk.Sources;

namespace Chalk.Execution.Windowing;

/// <summary>
/// A counted map of lane values that also remembers a row holding each of them, and which value is
/// currently the most frequent (D57, <c>14-windows-ii.md</c> §3).
/// </summary>
/// <remarks>
/// <para>
/// The same open addressing as <see cref="LaneMultiset"/>, with two additions <c>MODE</c> needs: a
/// representative row per value, so the answer can be emitted by copying an input row whatever its
/// type is, and a running maximum.
/// </para>
/// <para>
/// §3's rule is followed exactly: adding a row can only raise the maximum, so it is maintained in
/// O(1); removing one can only lower it, and only when the value removed <em>is</em> the current
/// mode — so that is the one case that rescans, and a frame that only grows never does.
/// </para>
/// </remarks>
internal sealed class LaneCountMap
{
    private readonly ColumnKind _kind;

    private byte[]?[] _keys;
    private int[] _counts;
    private int[] _rows;
    private int _mask;
    private int _used;

    private int _bestSlot = -1;

    public LaneCountMap(ColumnKind kind, int capacity = 16)
    {
        _kind = kind;
        var size = 16;
        while (size < Math.Max(capacity, 1) * 2)
        {
            size <<= 1;
        }

        _keys = new byte[size][];
        _counts = new int[size];
        _rows = new int[size];
        _mask = size - 1;
    }

    /// <summary>A row holding the most frequent value, or −1 when no value is in the frame.</summary>
    public int BestRow => _bestSlot < 0 ? -1 : _rows[_bestSlot];

    public void Clear()
    {
        System.Array.Clear(_keys);
        System.Array.Clear(_counts);
        System.Array.Clear(_rows);
        _used = 0;
        _bestSlot = -1;
    }

    /// <summary>Adds one occurrence of the value row <paramref name="row"/> holds.</summary>
    public void Add(ReadOnlySpan<byte> value, int row)
    {
        if (_used * 10 >= _keys.Length * 7)
        {
            Grow();
        }

        var slot = Find(value);
        if (_keys[slot] is null)
        {
            _keys[slot] = value.ToArray();
            _used++;
        }

        if (_counts[slot]++ == 0)
        {
            _rows[slot] = row;
        }

        // A larger count wins; an equal one wins only if the value is smaller (D57's tie rule).
        if (_bestSlot < 0
            || _counts[slot] > _counts[_bestSlot]
            || (_counts[slot] == _counts[_bestSlot] && Smaller(slot, _bestSlot)))
        {
            _bestSlot = slot;
        }
    }

    /// <summary>Removes one occurrence of the value row <paramref name="row"/> holds.</summary>
    public void Remove(ReadOnlySpan<byte> value)
    {
        var slot = Find(value);
        if (_keys[slot] is null || _counts[slot] == 0)
        {
            return;
        }

        _counts[slot]--;

        // Only the mode's own count falling can change the answer; anything else leaves a value that
        // was already behind it still behind it.
        if (slot == _bestSlot)
        {
            Rescan();
        }
    }

    private void Rescan()
    {
        _bestSlot = -1;
        for (var i = 0; i < _keys.Length; i++)
        {
            if (_keys[i] is null || _counts[i] == 0)
            {
                continue;
            }

            if (_bestSlot < 0
                || _counts[i] > _counts[_bestSlot]
                || (_counts[i] == _counts[_bestSlot] && Smaller(i, _bestSlot)))
            {
                _bestSlot = i;
            }
        }
    }

    private bool Smaller(int slot, int other) =>
        LaneComparer.Compare(_kind, _keys[slot], _keys[other]) < 0;

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
        var rows = _rows;
        var size = keys.Length * 2;
        _keys = new byte[size][];
        _counts = new int[size];
        _rows = new int[size];
        _mask = size - 1;
        _used = 0;
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
            _rows[slot] = rows[i];
            _used++;
        }

        Rescan();
    }
}

/// <summary>
/// <c>MODE</c> over a frame (D57, §3): the most frequent non-NULL value, ties broken by the smallest.
/// </summary>
/// <remarks>
/// The answer is always one of the input's own rows, so this is a <see cref="WindowRowEvaluator"/>:
/// it records which row won and the output copies that row, which makes every type work — including
/// the variable-length ones — with no lane wide enough to hold the value.
/// </remarks>
internal sealed class WindowModeEvaluator : WindowRowEvaluator
{
    private readonly int _valueColumn;
    private readonly LaneCountMap _counts;
    private readonly byte[] _scratch = new byte[16];

    public WindowModeEvaluator(ChalkType resultType, int valueColumn, ChalkType argumentType)
        : base(resultType, valueColumn, defaultValue: null)
    {
        _valueColumn = valueColumn;
        _counts = new LaneCountMap(ColumnKinds.Of(argumentType));
    }

    public override void Compute(WindowRun run, int start, int end)
    {
        var column = run.Columns[_valueColumn];
        if (!run.NoExclusion)
        {
            // An EXCLUDE cuts rows out of the middle of the frame, so there is nothing to slide; the
            // map is rebuilt per row, which is what §2 already does for DISTINCT.
            for (var i = start; i < end; i++)
            {
                _counts.Clear();
                for (var r = run.FrameLo[i]; r <= run.FrameHi[i]; r++)
                {
                    if (!run.Excluded(i, r))
                    {
                        Add(column, r);
                    }
                }

                Source[i] = _counts.BestRow;
            }

            return;
        }

        _counts.Clear();
        var low = start;
        var high = start - 1;

        for (var i = start; i < end; i++)
        {
            var lo = run.FrameLo[i];
            var hi = run.FrameHi[i];
            if (lo > hi)
            {
                _counts.Clear();
                low = lo;
                high = lo - 1;
                Source[i] = NullRow;
                continue;
            }

            if (low > high || lo > high || hi < low)
            {
                _counts.Clear();
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
            Source[i] = _counts.BestRow;
        }
    }

    private void Add(WindowColumn column, int row)
    {
        if (column.IsValid(row))
        {
            _counts.Add(column.Lane(row, _scratch), row);
        }
    }

    private void Remove(WindowColumn column, int row)
    {
        if (column.IsValid(row))
        {
            _counts.Remove(column.Lane(row, _scratch));
        }
    }
}

/// <summary>
/// <c>LISTAGG</c> and <c>ARRAY_AGG</c> over a frame (D57, §3): recomputed per row, because the output
/// is O(frame) whatever the algorithm is, so nothing is saved by sliding it.
/// </summary>
/// <remarks>
/// Calcite 1.42 does not let either of them carry a <c>WITHIN GROUP</c> ordering over a window — an
/// ordered aggregate is not an aggregate call as far as <c>OVER</c> is concerned (V22, ADR 0018) — so
/// the frame's own row order is the order, which is what the SQL means anyway.
/// </remarks>
internal sealed class WindowCollectingEvaluator : WindowCallEvaluator
{
    private readonly AggregateFunctionId _function;
    private readonly int _valueColumn;
    private readonly string _separator;
    private readonly StringBuilder _text = new();
    private readonly byte[] _scratch = new byte[16];

    public WindowCollectingEvaluator(
        ChalkType resultType, AggregateFunctionId function, int valueColumn, string separator)
        : base(resultType)
    {
        _function = function;
        _valueColumn = valueColumn;
        _separator = separator;
    }

    public override void Begin(ExecutionArena arena, int rows)
    {
    }

    public override void Release(ExecutionArena arena)
    {
    }

    /// <summary>Nothing to precompute: the frame is walked when the row is emitted.</summary>
    public override void Compute(WindowRun run, int start, int end)
    {
    }

    public override void Emit(ColumnCopier copier, WindowRun run, int row)
    {
        var column = run.Columns[_valueColumn];
        if (_function == AggregateFunctionId.ArrayAgg)
        {
            EmitList(copier, run, column, row);
            return;
        }

        _text.Clear();
        var any = false;
        for (var r = run.FrameLo[row]; r <= run.FrameHi[row]; r++)
        {
            if (run.Excluded(row, r) || !column.IsValid(r))
            {
                continue;
            }

            if (any)
            {
                _text.Append(_separator);
            }

            _text.Append(Text(column, r, _scratch));
            any = true;
        }

        if (!any)
        {
            copier.AppendRaw(default, valid: false);
            return;
        }

        copier.AppendRaw(Encoding.UTF8.GetBytes(_text.ToString()), valid: true);
    }

    /// <summary>
    /// <c>ARRAY_AGG</c>: the frame's values as one LIST cell. NULL elements are kept, but a frame
    /// with no value in it at all — empty, or every row NULL — is NULL rather than an empty list,
    /// which is PostgreSQL's rule and §3's (D57).
    /// </summary>
    /// <remarks>
    /// The frame is walked twice because the answer's validity is only known at the end and a row
    /// closed as invalid would still own anything appended before it.
    /// </remarks>
    private static void EmitList(ColumnCopier copier, WindowRun run, WindowColumn column, int row)
    {
        var any = false;
        for (var r = run.FrameLo[row]; r <= run.FrameHi[row] && !any; r++)
        {
            any = !run.Excluded(row, r) && column.IsValid(r);
        }

        if (!any)
        {
            copier.EndList(valid: false);
            return;
        }

        for (var r = run.FrameLo[row]; r <= run.FrameHi[row]; r++)
        {
            if (run.Excluded(row, r))
            {
                continue;
            }

            copier.Elements.AppendRow(column.View, r);
            copier.ElementAppended();
        }

        copier.EndList(valid: true);
    }

    private static string Text(WindowColumn column, int row, Span<byte> scratch)
    {
        if (column.Kind != ColumnKind.Utf8)
        {
            throw new UnsupportedFeatureException(
                $"LISTAGG over a {column.Type} column in a window",
                "LISTAGG concatenates strings (docs/design/02-ir.md §6).");
        }

        return Encoding.UTF8.GetString(column.Lane(row, scratch));
    }
}

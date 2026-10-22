using Chalk.Catalog;
using Chalk.Execution.Vectors;
using Chalk.Sources;

namespace Chalk.Execution.Maintenance;

/// <summary>
/// Rows a maintained operator keeps from one update to the next (D343, design 68 §2): a join's sides,
/// an as-of join's left and right rows, an aggregate's group keys. Each row has a slot, which stays
/// its own until it is removed and is then reused.
/// </summary>
/// <remarks>
/// Managed memory, counted against the view's <c>StateBudget</c> through <see cref="Bytes"/>. A
/// fixed-width value is its lane's bytes, as the batch it came from held them; a STRING is a byte
/// array of its own.
/// </remarks>
internal sealed class RowStore
{
    private readonly StoredColumn[] _columns;
    private int[] _free = [];
    private int _freeCount;
    private int _used;

    /// <param name="types">The rows' types.</param>
    /// <param name="capacity">Slots to make room for at once: a store built to replace one of that size.</param>
    public RowStore(IReadOnlyList<ChalkType> types, int capacity = 0)
    {
        Types = [.. types];
        _columns = [.. types.Select(t => new StoredColumn(t))];
        foreach (var column in _columns)
        {
            column.Ensure(capacity);
        }
    }

    public IReadOnlyList<ChalkType> Types { get; }

    /// <summary>Rows held.</summary>
    public int Count { get; private set; }

    /// <summary>One past the highest slot ever used.</summary>
    public int Slots => _used;

    public StoredColumn Column(int index) => _columns[index];

    /// <summary>Bytes held: every column and its buffers, and the free list, at their size (F223).</summary>
    public long Bytes
    {
        get
        {
            var bytes = Self + HeapSizes.Array(_columns.Length, HeapSizes.Reference) + HeapSizes.Array(_free.Length, sizeof(int));
            foreach (var column in _columns)
            {
                bytes += column.Held;
            }

            return bytes;
        }
    }

    // The store's own object: its header, three arrays and counts.
    private const int Self = 56;

    /// <summary>Keeps row <paramref name="row"/> of <paramref name="columns"/>, the store's own types, and returns its slot.</summary>
    public int Add(ReadOnlySpan<ColumnView> columns, int row)
    {
        var slot = Allocate();
        for (var c = 0; c < _columns.Length; c++)
        {
            _columns[c].Set(slot, columns[c], row);
        }

        return slot;
    }

    /// <summary>Keeps the columns <paramref name="which"/> of row <paramref name="row"/>, in that order.</summary>
    public int Add(ReadOnlySpan<ColumnView> columns, int row, ReadOnlySpan<int> which)
    {
        var slot = Allocate();
        for (var c = 0; c < _columns.Length; c++)
        {
            _columns[c].Set(slot, columns[which[c]], row);
        }

        return slot;
    }

    /// <summary>Keeps a copy of another store's row, of the same types.</summary>
    public int Add(RowStore other, int otherSlot)
    {
        var slot = Allocate();
        for (var c = 0; c < _columns.Length; c++)
        {
            _columns[c].Set(slot, other._columns[c], otherSlot);
        }

        return slot;
    }

    /// <summary>Lets slot <paramref name="slot"/> go, for a later row to reuse.</summary>
    public void Remove(int slot)
    {
        foreach (var column in _columns)
        {
            column.Release(slot);
        }

        if (_freeCount == _free.Length)
        {
            Array.Resize(ref _free, Math.Max(16, _free.Length * 2));
        }

        _free[_freeCount++] = slot;
        Count--;
    }

    /// <summary>Forgets every row.</summary>
    public void Clear()
    {
        foreach (var column in _columns)
        {
            column.Clear();
        }

        _freeCount = 0;
        _used = 0;
        Count = 0;
    }

    private int Allocate()
    {
        Count++;
        if (_freeCount > 0)
        {
            return _free[--_freeCount];
        }

        var slot = _used++;
        foreach (var column in _columns)
        {
            column.Ensure(_used);
        }

        return slot;
    }
}

/// <summary>One column of a <see cref="RowStore"/>.</summary>
/// <remarks>
/// A STRING's bytes live in one buffer the column owns, each slot a window of it: a value set again
/// leaves its old bytes behind, and the buffer is compacted when what is left behind outweighs what is held.
/// </remarks>
internal sealed class StoredColumn
{
    private readonly ColumnKind _kind;
    private readonly int _width;
    private readonly bool _variable;
    private byte[] _lanes = [];
    private bool[] _valid = [];
    private byte[] _data = [];
    private byte[] _spare = [];
    private int _dataUsed;
    private int _garbage;
    private int[] _starts = [];
    private int[] _lengths = [];

    public StoredColumn(ChalkType type)
    {
        Type = type;
        _kind = Kinds.Of(type, "a maintained operator's column");
        _width = ColumnKinds.Width(_kind);
        _variable = ColumnKinds.IsVariableLength(_kind);
    }

    public ChalkType Type { get; }

    public ColumnKind Kind => _kind;

    /// <summary>Bytes per value for a fixed-width layout; zero for STRING.</summary>
    public int Width => _width;

    public bool Variable => _variable;

    /// <summary>The column itself and its buffers, at their size (F223).</summary>
    public long Held =>
        Self + HeapSizes.Array(_lanes.Length, 1) + HeapSizes.Array(_valid.Length, 1) + HeapSizes.Array(_data.Length, 1)
        + HeapSizes.Array(_spare.Length, 1) + HeapSizes.Array(_starts.Length, sizeof(int)) + HeapSizes.Array(_lengths.Length, sizeof(int));

    // The column's own object: its header, its type, six arrays and its counts.
    private const int Self = 120;

    public bool IsValid(int slot) => _valid[slot];

    /// <summary>A fixed-width value's lane bytes, as its batch held them. Booleans are one byte, 0 or 1.</summary>
    public ReadOnlySpan<byte> Lane(int slot) => _lanes.AsSpan(slot * _width, _width);

    /// <summary>A STRING value's bytes.</summary>
    /// <remarks>An empty value has no window: a compaction moves only what has bytes.</remarks>
    public ReadOnlySpan<byte> Bytes(int slot) =>
        _lengths[slot] == 0 ? [] : _data.AsSpan(_starts[slot], _lengths[slot]);

    public void Set(int slot, in ColumnView source, int row)
    {
        if (!source.IsValid(row))
        {
            SetNull(slot);
            return;
        }

        _valid[slot] = true;
        switch (_kind)
        {
            case ColumnKind.Boolean:
                _lanes[slot] = (byte)(source.BoolAt(row) ? 1 : 0);
                return;
            case ColumnKind.Utf8:
                SetBytes(slot, source.VarValue(row));
                return;
            default:
                source.RawLanes(_width).Slice(row * _width, _width).CopyTo(_lanes.AsSpan(slot * _width));
                return;
        }
    }

    public void Set(int slot, StoredColumn other, int otherSlot)
    {
        if (!other._valid[otherSlot])
        {
            SetNull(slot);
            return;
        }

        _valid[slot] = true;
        if (_variable)
        {
            SetBytes(slot, other.Bytes(otherSlot));
        }
        else
        {
            other.Lane(otherSlot).CopyTo(_lanes.AsSpan(slot * _width));
        }
    }

    public void Release(int slot)
    {
        if (_variable)
        {
            _garbage += _lengths[slot];
            _lengths[slot] = 0;
        }
    }

    public void Clear()
    {
        _dataUsed = 0;
        _garbage = 0;
        Array.Clear(_lengths);
    }

    public void Ensure(int slots)
    {
        if (_valid.Length >= slots)
        {
            return;
        }

        var size = StateGrowth.Next(_valid.Length, slots);
        Array.Resize(ref _valid, size);
        if (_variable)
        {
            Array.Resize(ref _starts, size);
            Array.Resize(ref _lengths, size);
        }
        else
        {
            Array.Resize(ref _lanes, size * _width);
        }
    }

    private void SetNull(int slot)
    {
        _valid[slot] = false;
        if (_variable)
        {
            Release(slot);
        }
        else
        {
            _lanes.AsSpan(slot * _width, _width).Clear();
        }
    }

    private void SetBytes(int slot, ReadOnlySpan<byte> value)
    {
        Release(slot);
        if (_dataUsed + value.Length > _data.Length)
        {
            if (_garbage > 4096 && _garbage * 2 > _dataUsed)
            {
                Compact();
            }

            if (_dataUsed + value.Length > _data.Length)
            {
                Array.Resize(ref _data, StateGrowth.Next(_data.Length, _dataUsed + value.Length, least: 256));
            }
        }

        value.CopyTo(_data.AsSpan(_dataUsed));
        _starts[slot] = _dataUsed;
        _lengths[slot] = value.Length;
        _dataUsed += value.Length;
    }

    /// <summary>Moves every held value to the front of a buffer of its own, leaving behind what was let go.</summary>
    private void Compact()
    {
        // Into the spare buffer, which the next compaction gets back: no allocation once both are grown.
        var needed = _dataUsed - _garbage;
        var data = _spare.Length >= needed ? _spare : new byte[Math.Max(256, needed * 2)];
        var used = 0;
        for (var slot = 0; slot < _lengths.Length; slot++)
        {
            var length = _lengths[slot];
            if (length == 0)
            {
                continue;
            }

            _data.AsSpan(_starts[slot], length).CopyTo(data.AsSpan(used));
            _starts[slot] = used;
            used += length;
        }

        _spare = _data;
        _data = data;
        _dataUsed = used;
        _garbage = 0;
    }
}

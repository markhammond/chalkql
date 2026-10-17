namespace Chalk.Execution.Maintenance;

/// <summary>
/// A map from byte strings to integers that allocates nothing per key (D343): the keys a maintained
/// operator encodes — a row's identity, a join or grouping key — live in one buffer it owns, and an
/// open-addressed table finds them by hash.
/// </summary>
/// <remarks>
/// An entry is a key's place in the buffer, its hash and its value, numbered from zero in the order
/// keys were added; a removed entry's number is not reused until the map is cleared or compacts. The
/// map compacts when removed keys hold more of the buffer than live ones do.
/// </remarks>
internal sealed class ByteMap
{
    private const int Empty = -1;
    private const int Removed = -2;

    private byte[] _keys = new byte[256];
    private int _keyBytes;
    private int _garbage;
    private int[] _starts = new int[16];
    private int[] _lengths = new int[16];
    private int[] _hashes = new int[16];
    private int[] _values = new int[16];
    private int _entries;
    private int[] _table = Fresh(32);
    private int _mask = 31;
    private int _occupied;

    /// <summary>Keys held.</summary>
    public int Count { get; private set; }

    /// <summary>Entries ever added since the map was cleared or compacted: the bound of <see cref="IsLive"/>.</summary>
    public int Entries => _entries;

    /// <summary>The map itself, its keys' bytes, the four arrays of its entries and its table, at their size (F223).</summary>
    public long Bytes =>
        Self + HeapSizes.Array(_keys.Length, 1) + (4 * HeapSizes.Array(_starts.Length, sizeof(int)))
        + HeapSizes.Array(_table.Length, sizeof(int));

    // The map's own object: its header, six arrays and six counts.
    private const int Self = 88;

    public bool IsLive(int entry) => _lengths[entry] >= 0;

    public ReadOnlySpan<byte> Key(int entry) => _keys.AsSpan(_starts[entry], _lengths[entry]);

    public int Value(int entry) => _values[entry];

    public void Clear()
    {
        Array.Fill(_table, Empty);
        _keyBytes = 0;
        _garbage = 0;
        _entries = 0;
        _occupied = 0;
        Count = 0;
    }

    /// <summary>Forgets every key and lets the buffers go, as a map made afresh.</summary>
    public void Trim()
    {
        _keys = new byte[256];
        _starts = new int[16];
        _lengths = new int[16];
        _hashes = new int[16];
        _values = new int[16];
        _table = Fresh(32);
        _mask = 31;
        _keyBytes = 0;
        _garbage = 0;
        _entries = 0;
        _occupied = 0;
        Count = 0;
    }

    public bool TryGet(ReadOnlySpan<byte> key, out int value)
    {
        var entry = Find(key, Hash(key));
        if (entry < 0)
        {
            value = 0;
            return false;
        }

        value = _values[entry];
        return true;
    }

    /// <summary>The key's value, by reference, adding it with a value of zero when it is new.</summary>
    public ref int GetOrAdd(ReadOnlySpan<byte> key, out bool added)
    {
        var hash = Hash(key);
        var entry = Find(key, hash);
        if (entry >= 0)
        {
            added = false;
            return ref _values[entry];
        }

        added = true;
        entry = Insert(key, hash);
        return ref _values[entry];
    }

    /// <summary>Sets the key's value, adding the key when it is new.</summary>
    public void Set(ReadOnlySpan<byte> key, int value) => GetOrAdd(key, out _) = value;

    /// <summary>Removes the key, giving its value; false when it is not held.</summary>
    public bool Remove(ReadOnlySpan<byte> key, out int value)
    {
        var hash = Hash(key);
        var position = hash & _mask;
        while (true)
        {
            var at = _table[position];
            if (at == Empty)
            {
                value = 0;
                return false;
            }

            if (at >= 0 && _hashes[at] == hash && Key(at).SequenceEqual(key))
            {
                value = _values[at];
                _table[position] = Removed;
                _garbage += _lengths[at];
                _lengths[at] = -1;
                Count--;
                if (_garbage > 4096 && _garbage > _keyBytes - _garbage)
                {
                    Compact();
                }

                return true;
            }

            position = (position + 1) & _mask;
        }
    }

    private int Find(ReadOnlySpan<byte> key, int hash)
    {
        var position = hash & _mask;
        while (true)
        {
            var at = _table[position];
            if (at == Empty)
            {
                return -1;
            }

            if (at >= 0 && _hashes[at] == hash && Key(at).SequenceEqual(key))
            {
                return at;
            }

            position = (position + 1) & _mask;
        }
    }

    private int Insert(ReadOnlySpan<byte> key, int hash)
    {
        if ((_occupied + 1) * 2 > _table.Length)
        {
            // Full of live keys: grow. Full of removed marks: sweep them at the same size.
            Rehash((Count + 1) * 4 > _table.Length ? _table.Length * 2 : _table.Length);
        }

        if (_keyBytes + key.Length > _keys.Length)
        {
            Array.Resize(ref _keys, Math.Max(_keyBytes + key.Length, _keys.Length * 2));
        }

        if (_entries == _starts.Length)
        {
            var size = _starts.Length * 2;
            Array.Resize(ref _starts, size);
            Array.Resize(ref _lengths, size);
            Array.Resize(ref _hashes, size);
            Array.Resize(ref _values, size);
        }

        var entry = _entries++;
        key.CopyTo(_keys.AsSpan(_keyBytes));
        _starts[entry] = _keyBytes;
        _lengths[entry] = key.Length;
        _hashes[entry] = hash;
        _values[entry] = 0;
        _keyBytes += key.Length;

        var position = hash & _mask;
        while (_table[position] >= 0)
        {
            position = (position + 1) & _mask;
        }

        if (_table[position] == Empty)
        {
            _occupied++;
        }

        _table[position] = entry;
        Count++;
        return entry;
    }

    /// <summary>
    /// A table of <paramref name="size"/> slots over the live entries, dropping the removed marks: the one
    /// it has, swept, when it stays that size, so that churn allocates nothing.
    /// </summary>
    private void Rehash(int size)
    {
        if (size == _table.Length)
        {
            Array.Fill(_table, Empty);
        }
        else
        {
            _table = Fresh(size);
        }

        _mask = size - 1;
        _occupied = 0;
        for (var entry = 0; entry < _entries; entry++)
        {
            if (_lengths[entry] < 0)
            {
                continue;
            }

            var position = _hashes[entry] & _mask;
            while (_table[position] != Empty)
            {
                position = (position + 1) & _mask;
            }

            _table[position] = entry;
            _occupied++;
        }
    }

    /// <summary>Moves the live keys to the front of a buffer of their own, renumbering nothing callers hold.</summary>
    /// <remarks>
    /// Entry numbers are not stable across a compaction, so a map whose values callers read by entry
    /// number must not be compacted while they do; every caller here reads values by key.
    /// </remarks>
    private void Compact()
    {
        // Entries lie in the buffer in the order they were added, so each live key moves down in place.
        var used = 0;
        var live = 0;
        for (var entry = 0; entry < _entries; entry++)
        {
            if (_lengths[entry] < 0)
            {
                continue;
            }

            Key(entry).CopyTo(_keys.AsSpan(used));
            _starts[live] = used;
            _lengths[live] = _lengths[entry];
            _hashes[live] = _hashes[entry];
            _values[live] = _values[entry];
            used += _lengths[live];
            live++;
        }

        _keyBytes = used;
        _garbage = 0;
        _entries = live;
        Rehash(_table.Length);
    }

    private static int Hash(ReadOnlySpan<byte> key)
    {
        var hash = new HashCode();
        hash.AddBytes(key);
        return hash.ToHashCode() & int.MaxValue;
    }

    private static int[] Fresh(int size)
    {
        var table = new int[size];
        Array.Fill(table, Empty);
        return table;
    }
}

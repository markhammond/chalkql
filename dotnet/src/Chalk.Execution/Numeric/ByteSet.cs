namespace Chalk.Execution.Numeric;

/// <summary>
/// A growable open-addressing set of byte sequences. It backs the IN-list membership test (§6.4) and
/// the per-group de-duplication of a <c>distinct</c> measure (§6.6): both need "have I seen these
/// exact bytes" without decoding a row into a CLR object to ask.
/// </summary>
internal sealed class ByteSet
{
    private byte[]?[] _slots;
    private int _mask;
    private int _count;

    public ByteSet(int capacity = 8)
    {
        var size = 8;
        while (size < Math.Max(capacity, 1) * 2)
        {
            size <<= 1;
        }

        _slots = new byte[size][];
        _mask = size - 1;
    }

    public int Count => _count;

    /// <summary>Adds a value; true when it was not already present.</summary>
    public bool Add(ReadOnlySpan<byte> value)
    {
        if (_count * 10 >= _slots.Length * 7)
        {
            Grow();
        }

        var slot = (int)(Hashing.Bytes(value) & (ulong)_mask);
        while (_slots[slot] is { } existing)
        {
            if (existing.AsSpan().SequenceEqual(value))
            {
                return false;
            }

            slot = (slot + 1) & _mask;
        }

        _slots[slot] = value.ToArray();
        _count++;
        return true;
    }

    public bool Contains(ReadOnlySpan<byte> value)
    {
        var slot = (int)(Hashing.Bytes(value) & (ulong)_mask);
        while (_slots[slot] is { } existing)
        {
            if (existing.AsSpan().SequenceEqual(value))
            {
                return true;
            }

            slot = (slot + 1) & _mask;
        }

        return false;
    }

    private void Grow()
    {
        var grown = new byte[_slots.Length * 2][];
        var mask = grown.Length - 1;
        foreach (var entry in _slots)
        {
            if (entry is null)
            {
                continue;
            }

            var slot = (int)(Hashing.Bytes(entry) & (ulong)mask);
            while (grown[slot] is not null)
            {
                slot = (slot + 1) & mask;
            }

            grown[slot] = entry;
        }

        _slots = grown;
        _mask = mask;
    }
}

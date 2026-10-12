using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Chalk.Incremental;

/// <summary>
/// A view's rows (D339, <c>docs/design/64-incremental-views.md</c> §4): for each key, the host's
/// mapped key and row, the position of its last change and its hash. The keys are linked in change
/// order, so a drain walks back from the newest change and costs what changed, not the result. A
/// deleted key stays, as a delete, until there are more deletes than live rows; then the oldest go.
/// </summary>
/// <remarks>Not thread-safe: the view holds its lock around every call that reads or writes it.</remarks>
internal sealed class KeyedTable<TKey, TRow>
    where TKey : unmanaged
    where TRow : unmanaged
{
    internal struct Entry
    {
        public TKey Key;
        public TRow Row;

        /// <summary>The position of the key's last change: the table's count of changes then.</summary>
        public long Position;

        /// <summary>The update that last saw the key in the result.</summary>
        public long Seen;

        public uint Hash;
        public int Previous;
        public int Next;
        public bool Live;
        public bool Used;
    }

    private readonly Dictionary<TKey, int> _index = new(Bytes<TKey>.Comparer);
    private Entry[] _entries = new Entry[16];
    private int _used;
    private int _free = -1;
    private int _first = -1;
    private int _last = -1;

    /// <summary>The position the last change took.</summary>
    public long Position { get; private set; }

    /// <summary>The greatest position of a delete dropped since: a reader behind it must reset.</summary>
    public long DroppedThrough { get; private set; }

    public int Live { get; private set; }

    public int Deleted { get; private set; }

    /// <summary>The sum of the live rows' hashes, mod 2³².</summary>
    public uint Checksum { get; private set; }

    /// <summary>What the table holds, in bytes: its entries and its index.</summary>
    public long Bytes => BytesFor(_entries.Length, _index.Count);

    public int First => _first;

    public ref readonly Entry this[int slot] => ref _entries[slot];

    public int NextOf(int slot) => _entries[slot].Next;

    public bool Contains(in TKey key) => _index.ContainsKey(key);

    /// <summary>What a table of <paramref name="slots"/> entries, <paramref name="keys"/> of them indexed, holds.</summary>
    public static long BytesFor(long slots, long keys) =>
        (slots * Unsafe.SizeOf<Entry>()) + (keys * (Unsafe.SizeOf<TKey>() + (4 * sizeof(int))));

    /// <summary>What the table would hold with <paramref name="added"/> more keys, before deletes go.</summary>
    public long BytesWith(long added)
    {
        var keys = _index.Count + added;
        long capacity = _entries.Length;
        while (capacity < keys)
        {
            capacity *= 2;
        }

        return BytesFor(capacity, keys);
    }

    /// <summary>
    /// The key's row is now <paramref name="row"/>, as of the update <paramref name="seen"/>. A row
    /// equal to the one the key holds, byte for byte, changes nothing; anything else takes the next
    /// position.
    /// </summary>
    public void Put(in TKey key, in TRow row, uint hash, long seen)
    {
        if (_index.TryGetValue(key, out var slot))
        {
            ref var entry = ref _entries[slot];
            entry.Seen = seen;
            if (entry.Live && Bytes<TRow>.Equal(entry.Row, row))
            {
                return;
            }

            if (entry.Live)
            {
                Checksum -= entry.Hash;
            }
            else
            {
                Deleted--;
                Live++;
            }

            entry.Row = row;
            entry.Hash = hash;
            entry.Live = true;
            Checksum += hash;
            Unlink(slot);
            Append(slot);
            return;
        }

        slot = Allocate();
        _entries[slot] = new Entry
        {
            Key = key,
            Row = row,
            Hash = hash,
            Seen = seen,
            Live = true,
            Used = true,
            Previous = -1,
            Next = -1,
        };
        _index.Add(key, slot);
        Live++;
        Checksum += hash;
        Append(slot);
    }

    /// <summary>Every live key the update <paramref name="seen"/> did not see becomes a delete.</summary>
    public void DeleteUnseen(long seen)
    {
        for (var slot = 0; slot < _used; slot++)
        {
            ref var entry = ref _entries[slot];
            if (entry.Used && entry.Live && entry.Seen != seen)
            {
                entry.Live = false;
                Checksum -= entry.Hash;
                Live--;
                Deleted++;
                Unlink(slot);
                Append(slot);
            }
        }
    }

    /// <summary>Drops the oldest deletes while there are more deletes than live rows.</summary>
    public void Compact()
    {
        var slot = _first;
        while (Deleted > Live && slot != -1)
        {
            var next = _entries[slot].Next;
            if (!_entries[slot].Live)
            {
                DroppedThrough = Math.Max(DroppedThrough, _entries[slot].Position);
                _index.Remove(_entries[slot].Key);
                Unlink(slot);
                _entries[slot] = default;
                _entries[slot].Next = _free;
                _free = slot;
                Deleted--;
            }

            slot = next;
        }
    }

    /// <summary>
    /// The first key changed after <paramref name="position"/>, found by walking back from the newest
    /// change, or -1; and how many changed, counted up to <paramref name="enough"/>.
    /// </summary>
    public int FirstAfter(long position, int enough, out int changed)
    {
        var slot = _last;
        var first = -1;
        changed = 0;
        while (slot != -1 && _entries[slot].Position > position)
        {
            first = slot;
            if (changed < enough)
            {
                changed++;
            }

            slot = _entries[slot].Previous;
        }

        return first;
    }

    private int Allocate()
    {
        if (_free != -1)
        {
            var slot = _free;
            _free = _entries[slot].Next;
            return slot;
        }

        if (_used == _entries.Length)
        {
            Array.Resize(ref _entries, _entries.Length * 2);
        }

        return _used++;
    }

    /// <summary>Puts the slot last in change order, at the next position.</summary>
    private void Append(int slot)
    {
        ref var entry = ref _entries[slot];
        entry.Position = ++Position;
        entry.Previous = _last;
        entry.Next = -1;
        if (_last == -1)
        {
            _first = slot;
        }
        else
        {
            _entries[_last].Next = slot;
        }

        _last = slot;
    }

    private void Unlink(int slot)
    {
        ref var entry = ref _entries[slot];
        if (entry.Previous == -1)
        {
            _first = entry.Next;
        }
        else
        {
            _entries[entry.Previous].Next = entry.Next;
        }

        if (entry.Next == -1)
        {
            _last = entry.Previous;
        }
        else
        {
            _entries[entry.Next].Previous = entry.Previous;
        }

        entry.Previous = -1;
        entry.Next = -1;
    }
}

/// <summary>An unmanaged value as its bytes: compared, hashed and handed to a host's hash so.</summary>
internal static class Bytes<T>
    where T : unmanaged
{
    public static IEqualityComparer<T> Comparer { get; } = new ByteEquality();

    public static ReadOnlySpan<byte> Of(in T value) => MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in value));

    public static bool Equal(in T x, in T y) => Of(in x).SequenceEqual(Of(in y));

    private sealed class ByteEquality : IEqualityComparer<T>
    {
        public bool Equals(T x, T y) => Equal(in x, in y);

        public int GetHashCode(T value)
        {
            var hash = new HashCode();
            hash.AddBytes(Of(in value));
            return hash.ToHashCode();
        }
    }
}

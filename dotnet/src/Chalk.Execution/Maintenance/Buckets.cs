namespace Chalk.Execution.Maintenance;

/// <summary>
/// Held slots by key (D343): a join's rows by join key, an as-of join's left rows by key. A slot is in
/// one bucket at most, and leaves it in constant time, by the slot alone.
/// </summary>
internal sealed class Buckets
{
    private readonly ByteMap _ids = new();
    private readonly List<List<int>> _members = [];
    private readonly List<byte[]> _keys = [];
    private readonly Stack<int> _free = new();
    private int[] _bucketOf = [];
    private int[] _placeOf = [];

    public long Bytes
    {
        get
        {
            var bytes = _ids.Bytes + ((_bucketOf.Length + _placeOf.Length) * 4L);
            foreach (var members in _members)
            {
                bytes += 16 + (members.Capacity * 4L);
            }

            return bytes;
        }
    }

    /// <summary>The slots under <paramref name="key"/>, or null; a list to read, never to change.</summary>
    public List<int>? Find(ReadOnlySpan<byte> key) => _ids.TryGet(key, out var id) ? _members[id] : null;

    /// <summary>Puts <paramref name="slot"/> under <paramref name="key"/>.</summary>
    public void Add(ReadOnlySpan<byte> key, int slot)
    {
        ref var id = ref _ids.GetOrAdd(key, out var added);
        if (added)
        {
            if (_free.TryPop(out var reused))
            {
                // A freed bucket keeps its key's array, which a key of the same length is copied into:
                // under churn a key leaves and comes back in one update, and allocates nothing.
                var held = _keys[reused];
                if (held.Length == key.Length)
                {
                    key.CopyTo(held);
                }
                else
                {
                    _keys[reused] = key.ToArray();
                }
            }
            else
            {
                reused = _members.Count;
                _members.Add([]);
                _keys.Add(key.ToArray());
            }

            id = reused;
        }

        var bucket = id;
        if (_bucketOf.Length <= slot)
        {
            var size = Math.Max(slot + 1, Math.Max(16, _bucketOf.Length * 2));
            Array.Resize(ref _bucketOf, size);
            Array.Resize(ref _placeOf, size);
        }

        var members = _members[bucket];
        _bucketOf[slot] = bucket;
        _placeOf[slot] = members.Count;
        members.Add(slot);
    }

    /// <summary>Takes <paramref name="slot"/> out of its bucket, and the bucket out when it empties.</summary>
    public void Remove(int slot)
    {
        var bucket = _bucketOf[slot];
        var members = _members[bucket];
        var place = _placeOf[slot];
        var last = members[^1];
        members[place] = last;
        _placeOf[last] = place;
        members.RemoveAt(members.Count - 1);
        if (members.Count == 0)
        {
            _ids.Remove(_keys[bucket], out _);
            _free.Push(bucket);
        }
    }

    /// <summary>Every bucket that holds a slot, with its key.</summary>
    public IEnumerable<(byte[] Key, List<int> Slots)> All()
    {
        for (var bucket = 0; bucket < _members.Count; bucket++)
        {
            if (_members[bucket].Count > 0)
            {
                yield return (_keys[bucket], _members[bucket]);
            }
        }
    }

    public void Clear()
    {
        _ids.Clear();
        _members.Clear();
        _keys.Clear();
        _free.Clear();
    }
}

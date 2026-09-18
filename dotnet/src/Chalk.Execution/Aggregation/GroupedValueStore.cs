using Chalk.Execution.Vectors;

namespace Chalk.Execution.Aggregation;

/// <summary>
/// Every value a group has seen, and optionally the key it is ordered by (D57,
/// <c>14-windows-ii.md</c> §3). The holistic aggregates are the ones whose answer depends on the
/// whole group rather than on a running state, so this is where their memory goes — proportional to
/// the input, which is what <c>MaxBytes</c> bounds.
/// </summary>
/// <remarks>
/// One growing byte buffer per lane and a per-group singly-linked chain of entry indices, all rented
/// from the execution's arena. Appending is O(1) and allocates nothing per row; the chain is walked
/// into a scratch index array once per group at emit time, in insertion order, which is the order SQL
/// means when the aggregate names none.
/// </remarks>
internal sealed class GroupedValueStore : ArenaScratch
{
    private readonly Comparison<int> _byKeyAscending;
    private readonly Comparison<int> _byKeyDescending;
    private readonly Comparison<int> _byValue;
    private ColumnKind _keyKind;
    private ColumnKind _valueKind;

    private int[] _head = [];
    private int[] _tail = [];
    private int[] _counts = [];

    private int[] _next = [];
    private int[] _valueStart = [];
    private int[] _valueLength = [];
    private int[] _keyStart = [];
    private int[] _keyLength = [];
    private bool[] _valueValid = [];
    private bool[] _keyValid = [];

    private byte[] _values = [];
    private byte[] _keys = [];
    private int _valuesUsed;
    private int _keysUsed;
    private int _entries;

    public GroupedValueStore(ColumnKind valueKind, ColumnKind keyKind)
    {
        _valueKind = valueKind;
        _keyKind = keyKind;
        _byKeyAscending = (a, b) => CompareKeys(a, b, descending: false);
        _byKeyDescending = (a, b) => CompareKeys(a, b, descending: true);
        _byValue = (a, b) =>
        {
            var order = LaneComparer.Compare(_valueKind, Value(a), Value(b));
            return order != 0 ? order : a.CompareTo(b);
        };
    }

    /// <summary>How many values a group has, NULLs included.</summary>
    public int Count(int group) => group < _counts.Length ? _counts[group] : 0;

    public void EnsureGroups(int groups)
    {
        if (_head.Length >= groups)
        {
            return;
        }

        var previous = _head.Length;
        _head = Grow(_head, groups);
        _tail = Grow(_tail, groups);
        _counts = Grow(_counts, groups);
        for (var g = previous; g < _head.Length; g++)
        {
            _head[g] = -1;
            _tail[g] = -1;
        }
    }

    /// <summary>Appends one value, with the key it is ordered by when the aggregate has one.</summary>
    public void Append(
        int group, ReadOnlySpan<byte> value, bool valid, ReadOnlySpan<byte> key, bool keyValid)
    {
        var entry = _entries++;
        if (_next.Length < _entries)
        {
            _next = Grow(_next, _entries);
            _valueStart = Grow(_valueStart, _entries);
            _valueLength = Grow(_valueLength, _entries);
            _keyStart = Grow(_keyStart, _entries);
            _keyLength = Grow(_keyLength, _entries);
            _valueValid = Grow(_valueValid, _entries);
            _keyValid = Grow(_keyValid, _entries);
        }

        _next[entry] = -1;
        _valueValid[entry] = valid;
        _keyValid[entry] = keyValid;
        _valueStart[entry] = _valuesUsed;
        _valueLength[entry] = valid ? value.Length : 0;
        if (valid && value.Length > 0)
        {
            if (_values.Length < _valuesUsed + value.Length)
            {
                _values = Grow(_values, _valuesUsed + value.Length);
            }

            value.CopyTo(_values.AsSpan(_valuesUsed));
            _valuesUsed += value.Length;
        }

        _keyStart[entry] = _keysUsed;
        _keyLength[entry] = keyValid ? key.Length : 0;
        if (keyValid && key.Length > 0)
        {
            if (_keys.Length < _keysUsed + key.Length)
            {
                _keys = Grow(_keys, _keysUsed + key.Length);
            }

            key.CopyTo(_keys.AsSpan(_keysUsed));
            _keysUsed += key.Length;
        }

        if (_head[group] < 0)
        {
            _head[group] = entry;
        }
        else
        {
            _next[_tail[group]] = entry;
        }

        _tail[group] = entry;
        _counts[group]++;
    }

    /// <summary>The group's entries, in insertion order, written into <paramref name="into"/>.</summary>
    public int Collect(int group, int[] into)
    {
        var n = 0;
        for (var entry = group < _head.Length ? _head[group] : -1; entry >= 0; entry = _next[entry])
        {
            into[n++] = entry;
        }

        return n;
    }

    public bool IsValid(int entry) => _valueValid[entry];

    public ReadOnlySpan<byte> Value(int entry) =>
        _values.AsSpan(_valueStart[entry], _valueLength[entry]);

    /// <summary>
    /// Orders the entries by their keys, NULLs last, ties broken by insertion order — which is what
    /// makes an ordered aggregate deterministic when two rows share a key.
    /// </summary>
    public void SortByKey(int[] entries, int count, bool descending) =>
        System.Array.Sort(entries, 0, count, Comparer<int>.Create(
            descending ? _byKeyDescending : _byKeyAscending));

    /// <summary>Orders the entries by their values. NULLs are excluded by the caller.</summary>
    public void SortByValue(int[] entries, int count) =>
        System.Array.Sort(entries, 0, count, Comparer<int>.Create(_byValue));

    protected override void ReleaseCore()
    {
        Give(ref _head);
        Give(ref _tail);
        Give(ref _counts);
        Give(ref _next);
        Give(ref _valueStart);
        Give(ref _valueLength);
        Give(ref _keyStart);
        Give(ref _keyLength);
        Give(ref _valueValid);
        Give(ref _keyValid);
        Give(ref _values);
        Give(ref _keys);
        _valuesUsed = 0;
        _keysUsed = 0;
        _entries = 0;
    }

    private int CompareKeys(int a, int b, bool descending)
    {
        var aValid = _keyValid[a];
        var bValid = _keyValid[b];
        if (aValid != bValid)
        {
            return aValid ? -1 : 1;
        }

        var order = aValid
            ? LaneComparer.Compare(_keyKind, KeyOf(a), KeyOf(b))
            : 0;
        if (descending)
        {
            order = -order;
        }

        return order != 0 ? order : a.CompareTo(b);
    }

    private ReadOnlySpan<byte> KeyOf(int entry) => _keys.AsSpan(_keyStart[entry], _keyLength[entry]);
}

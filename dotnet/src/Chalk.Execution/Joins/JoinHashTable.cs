using Chalk.Execution.Vectors;
using Chalk.Sources;

namespace Chalk.Execution.Joins;

/// <summary>
/// The build side of a hash join, indexed by key.
/// </summary>
/// <remarks>
/// The M1 aggregate's open-addressing table generalised to multi-row buckets ({@code 12-joins.md}
/// §4): a power-of-two slot array holds the <em>first</em> build row of each key, and
/// <see cref="_next"/> chains the rest — one <c>int</c> per build row, no per-key list objects. Both
/// arrays come from the arena.
/// <para>
/// Build rows with a NULL in any key column are simply not inserted, which is how "a NULL key never
/// matches" is implemented rather than checked.
/// </para>
/// </remarks>
internal sealed class JoinHashTable
{
    private const int MinimumSlots = 64;

    private readonly JoinKeys _keys;
    private ExecutionArena? _arena;
    private int[] _slots = [];
    private int[] _next = [];
    private ulong[] _hashes = [];
    private int _mask;

    public JoinHashTable(JoinKeys keys)
    {
        _keys = keys;
    }

    /// <summary>The build rows, in key order, for equality re-checks during a probe.</summary>
    public Vector[] BuildKeys { get; private set; } = [];

    /// <summary>Indexes the build side. The vectors must stay valid until <see cref="Release"/>.</summary>
    public void Build(ExecutionArena arena, Vector[] buildKeys, int rows)
    {
        _arena = arena;
        BuildKeys = buildKeys;

        var slots = MinimumSlots;
        while (slots < rows * 2)
        {
            slots <<= 1;
        }

        _slots = arena.Rent<int>(slots);
        _next = arena.Rent<int>(Math.Max(rows, 1));
        _hashes = arena.Rent<ulong>(Math.Max(rows, 1));
        _mask = slots - 1;
        Array.Fill(_slots, -1, 0, slots);

        for (var row = 0; row < rows; row++)
        {
            _next[row] = -1;
            if (_keys.Count > 0 && _keys.AnyNull(buildKeys, row))
            {
                _hashes[row] = 0;
                continue;
            }

            var hash = _keys.Count == 0 ? 0UL : _keys.Hash(buildKeys, row);
            _hashes[row] = hash;
            var slot = (int)(hash & (ulong)_mask);
            while (_slots[slot] >= 0 && !SameBucket(_slots[slot], hash, buildKeys, row))
            {
                slot = (slot + 1) & _mask;
            }

            if (_slots[slot] < 0)
            {
                _slots[slot] = row;
            }
            else
            {
                // Append to the tail so a bucket keeps the build input's order, which is what makes
                // the ASOF tie-break and every multi-match join deterministic.
                var tail = _slots[slot];
                while (_next[tail] >= 0)
                {
                    tail = _next[tail];
                }

                _next[tail] = row;
            }
        }
    }

    /// <summary>The first build row whose keys equal the probe row's, or -1.</summary>
    public int First(Vector[] probeKeys, int probeRow)
    {
        if (_keys.Count == 0)
        {
            return _slots.Length == 0 ? -1 : _slots[0];
        }

        if (_keys.AnyNull(probeKeys, probeRow))
        {
            return -1;
        }

        var hash = _keys.Hash(probeKeys, probeRow);
        var slot = (int)(hash & (ulong)_mask);
        while (true)
        {
            var row = _slots[slot];
            if (row < 0)
            {
                return -1;
            }

            if (_hashes[row] == hash && _keys.Equal(BuildKeys, row, probeKeys, probeRow))
            {
                return row;
            }

            slot = (slot + 1) & _mask;
        }
    }

    /// <summary>The next build row in the same bucket, or -1.</summary>
    public int Next(int row) => _next[row];

    public void Release()
    {
        if (_arena is null)
        {
            return;
        }

        _arena.Return(_slots);
        _arena.Return(_next);
        _arena.Return(_hashes);
        _slots = [];
        _next = [];
        _hashes = [];
        _mask = 0;
        BuildKeys = [];
        _arena = null;
    }

    private bool SameBucket(int head, ulong hash, Vector[] keys, int row) =>
        _hashes[head] == hash && _keys.Equal(keys, head, keys, row);
}

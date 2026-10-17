namespace Chalk.Sources.Poco;

/// <summary>
/// Row positions in key order, held in leaves (F202): an index's order without one array as long as
/// the table. An append writes the rows it puts at a leaf's end into the room the leaf has, copies a
/// leaf it puts rows inside, and splits one that fills, so it costs the rows it appends and the leaves
/// they land in, and one short list of leaves, rather than the whole table.
/// </summary>
/// <remarks>
/// <para>
/// A version never changes. It reads each leaf up to its own count of it, and a leaf is written only
/// past the furthest any version has claimed of it, so the versions an index's snapshots hold never
/// see each other's rows, and two versions built from one never write over each other: the second
/// copies the leaf instead.
/// </para>
/// <para>
/// Ordinals run across the leaves in order, so a window of them is as contiguous as one array's was.
/// A leaf is cut after the rows an append put inside it: an index whose rows arrive at the end of
/// their key's run — each key's latest time, say — then finds that end at a leaf's end the next time,
/// and from then on writes there, in a leaf with room.
/// </para>
/// </remarks>
internal sealed class PositionLeaves
{
    /// <summary>The most positions a leaf holds, and the room a leaf an append makes is given.</summary>
    internal const int Capacity = 1024;

    private static readonly PositionLeaves Empty = new([], [0]);

    private readonly Leaf[] _leaves;

    /// <summary>The ordinal each leaf starts at, then the count: one longer than the leaves.</summary>
    private readonly int[] _starts;

    private PositionLeaves(Leaf[] leaves, int[] starts)
    {
        _leaves = leaves;
        _starts = starts;
    }

    /// <summary>Positions this version holds.</summary>
    public int Count => _starts[^1];

    /// <summary>Leaves this version reads.</summary>
    public int LeafCount => _leaves.Length;

    /// <summary>The position at <paramref name="ordinal"/>, in key order.</summary>
    public int this[int ordinal]
    {
        get
        {
            var index = LeafOf(ordinal);
            var leaf = _leaves[index];
            return leaf.Positions[leaf.Offset + ordinal - _starts[index]];
        }
    }

    /// <summary>The leaf that holds <paramref name="ordinal"/>.</summary>
    public int LeafOf(int ordinal)
    {
        var low = 0;
        var high = _leaves.Length - 1;
        while (low < high)
        {
            var mid = (low + high + 1) >> 1;
            if (_starts[mid] <= ordinal)
            {
                low = mid;
            }
            else
            {
                high = mid - 1;
            }
        }

        return low;
    }

    /// <summary>The ordinal leaf <paramref name="leaf"/> starts at; <see cref="Count"/> past the last.</summary>
    public int StartOf(int leaf) => _starts[leaf];

    /// <summary>
    /// The array leaf <paramref name="leaf"/> holds its positions in, from <see cref="OffsetOf"/> on, read
    /// only as far as the leaf's count in this version: <c>StartOf(leaf + 1) - StartOf(leaf)</c>.
    /// </summary>
    public int[] ArrayOf(int leaf) => _leaves[leaf].Positions;

    /// <summary>Where leaf <paramref name="leaf"/>'s positions start in its array.</summary>
    public int OffsetOf(int leaf) => _leaves[leaf].Offset;

    /// <summary>The leaf's positions as this version holds them.</summary>
    public ReadOnlySpan<int> SpanOf(int leaf) =>
        _leaves[leaf].Positions.AsSpan(_leaves[leaf].Offset, _starts[leaf + 1] - _starts[leaf]);

    /// <summary>
    /// The bytes this version's leaves span, room included: what an index of it holds, near four a row
    /// when built whole and up to twice that where appends left room.
    /// </summary>
    public long Bytes
    {
        get
        {
            long bytes = 0;
            foreach (var leaf in _leaves)
            {
                bytes += (leaf.End - leaf.Offset) * (long)sizeof(int);
            }

            return bytes;
        }
    }

    /// <summary>Every position, in key order, in one array.</summary>
    public int[] ToArray()
    {
        var positions = GC.AllocateUninitializedArray<int>(Count);
        for (var leaf = 0; leaf < _leaves.Length; leaf++)
        {
            SpanOf(leaf).CopyTo(positions.AsSpan(_starts[leaf]));
        }

        return positions;
    }

    /// <summary>
    /// <paramref name="positions"/>, already in key order, as leaves that are stretches of it: the array
    /// becomes theirs, and is never written to, so an append writes past a leaf into leaves of its own
    /// and copies one it puts rows inside.
    /// </summary>
    public static PositionLeaves FromSorted(int[] positions)
    {
        if (positions.Length == 0)
        {
            return Empty;
        }

        var count = (positions.Length + Capacity - 1) / Capacity;
        var leaves = new Leaf[count];
        var starts = new int[count + 1];
        for (var leaf = 0; leaf < count; leaf++)
        {
            var start = leaf * Capacity;
            leaves[leaf] = Leaf.Stretch(positions, start, Math.Min(Capacity, positions.Length - start));
            starts[leaf] = start;
        }

        starts[count] = positions.Length;
        return new PositionLeaves(leaves, starts);
    }

    /// <summary>
    /// These positions with <paramref name="positions"/> inserted, each before the ordinal beside it in
    /// <paramref name="ordinals"/>, which never decrease: what an append makes of an index's order. A
    /// position whose ordinal is a leaf's end goes at that leaf's end, written into its room where it
    /// has room and no other version has written there.
    /// </summary>
    public PositionLeaves Insert(ReadOnlySpan<int> ordinals, ReadOnlySpan<int> positions)
    {
        if (ordinals.Length != positions.Length)
        {
            throw new ArgumentException("an ordinal is needed for each position.", nameof(ordinals));
        }

        if (positions.Length == 0)
        {
            return this;
        }

        var built = new Builder(_leaves.Length + 4);
        if (_leaves.Length == 0)
        {
            built.Fresh(positions);
            return built.Done();
        }

        var next = 0;
        for (var leaf = 0; leaf < _leaves.Length; leaf++)
        {
            var start = _starts[leaf];
            var end = _starts[leaf + 1];
            var first = next;

            // An ordinal at a leaf boundary belongs to the leaf that ends there, so that its rows go
            // where that leaf may have room; an ordinal before the first leaf's first belongs to it.
            while (next < ordinals.Length && ordinals[next] <= end)
            {
                next++;
            }

            var mine = next - first;
            if (mine == 0)
            {
                built.Keep(_leaves[leaf], end - start);
            }
            else if (ordinals[first] == end)
            {
                built.AtEnd(_leaves[leaf], end - start, positions.Slice(first, mine));
            }
            else
            {
                built.Within(
                    _leaves[leaf], end - start, start, ordinals.Slice(first, mine), positions.Slice(first, mine));
            }
        }

        if (next != ordinals.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(ordinals), "an ordinal is past the positions' count.");
        }

        var done = built.Done();
        return done.Fragmented ? done.Repacked() : done;
    }

    /// <summary>
    /// Whether appends have cut the leaves so fine that finding one costs more than it should: four
    /// times the leaves the positions fill when packed, and more.
    /// </summary>
    internal bool Fragmented => _leaves.Length > (4 * ((Count / (Capacity / 2)) + 1)) + 64;

    /// <summary>The same positions in leaves half as long as they may be: what a fragmented version becomes.</summary>
    private PositionLeaves Repacked()
    {
        var all = ToArray();
        var half = Capacity / 2;
        var count = (all.Length + half - 1) / half;
        var leaves = new Leaf[count];
        var starts = new int[count + 1];
        for (var leaf = 0; leaf < count; leaf++)
        {
            var start = leaf * half;
            leaves[leaf] = Leaf.Stretch(all, start, Math.Min(half, all.Length - start));
            starts[leaf] = start;
        }

        starts[count] = all.Length;
        return new PositionLeaves(leaves, starts);
    }

    /// <summary>
    /// One leaf: a stretch of an array, shared by every version that reads it; how far into the array
    /// it may be written; and how far any version has claimed it. A version writes past its own count
    /// only from where the claims end, so it never writes what another reads.
    /// </summary>
    private sealed class Leaf(int[] positions, int offset, int end, int claimed)
    {
        public int[] Positions { get; } = positions;

        /// <summary>Where the leaf's positions start in its array.</summary>
        public int Offset { get; } = offset;

        /// <summary>The end of what the leaf may write in its array: its last position's when it has no room.</summary>
        public int End { get; } = end;

        /// <summary>Positions some version has claimed, from <see cref="Offset"/>.</summary>
        public int Claimed = claimed;

        /// <summary>A stretch of a sorted array, with no room: the array is shared by its neighbours.</summary>
        public static Leaf Stretch(int[] positions, int offset, int length) =>
            new(positions, offset, offset + length, length);

        /// <summary>A leaf of its own, with room for as many positions as <paramref name="room"/> in all.</summary>
        public static Leaf WithRoom(ReadOnlySpan<int> positions, int room)
        {
            var array = GC.AllocateUninitializedArray<int>(Math.Max(room, positions.Length));
            positions.CopyTo(array);
            return new Leaf(array, 0, array.Length, positions.Length);
        }

        /// <summary>The leaf's first <paramref name="count"/> positions.</summary>
        public ReadOnlySpan<int> Span(int count) => Positions.AsSpan(Offset, count);
    }

    /// <summary>Leaves and their counts for a new version, built leaf by leaf.</summary>
    private sealed class Builder(int capacity)
    {
        private readonly List<Leaf> _leaves = new(capacity);
        private readonly List<int> _counts = new(capacity);

        /// <summary>A leaf this version reads as far as <paramref name="count"/>.</summary>
        public void Keep(Leaf leaf, int count)
        {
            _leaves.Add(leaf);
            _counts.Add(count);
        }

        /// <summary>Positions appended at the end of <paramref name="leaf"/>, in its room where it may be written.</summary>
        public void AtEnd(Leaf leaf, int count, ReadOnlySpan<int> added)
        {
            var room = leaf.End - leaf.Offset - count;
            var take = Math.Min(room, added.Length);
            if (take > 0 && Interlocked.CompareExchange(ref leaf.Claimed, count + take, count) == count)
            {
                added[..take].CopyTo(leaf.Positions.AsSpan(leaf.Offset + count));
                Keep(leaf, count + take);
                Fresh(added[take..]);
                return;
            }

            if (take > 0)
            {
                // Another version has written past this one's count: this version copies what it
                // reads, and writes its own rows after it.
                var copy = GC.AllocateUninitializedArray<int>(Capacity);
                leaf.Span(count).CopyTo(copy);
                added[..take].CopyTo(copy.AsSpan(count));
                Keep(new Leaf(copy, 0, copy.Length, count + take), count + take);
                Fresh(added[take..]);
                return;
            }

            Keep(leaf, count);
            Fresh(added);
        }

        /// <summary>
        /// Positions put inside <paramref name="leaf"/>: the leaf is merged with them anew, and cut after
        /// each run of them, and wherever it would hold more than <see cref="Capacity"/>. A piece that
        /// ends where rows were put is given room, since the next rows of the same key come there; the
        /// others are stretches of the merged array. However short a piece a cut leaves, the cut is
        /// made: a key whose run ended inside a leaf would otherwise copy that leaf at every append, and
        /// leaves cut too fine are packed again.
        /// </summary>
        public void Within(
            Leaf leaf, int count, int start, ReadOnlySpan<int> ordinals, ReadOnlySpan<int> added)
        {
            var total = count + added.Length;
            var merged = GC.AllocateUninitializedArray<int>(total);
            var cuts = added.Length <= 256 ? stackalloc int[added.Length] : new int[added.Length];
            var cutCount = 0;
            var old = leaf.Span(count);
            var from = 0;
            var at = 0;
            for (var i = 0; i < added.Length; i++)
            {
                var local = ordinals[i] - start;
                old[from..local].CopyTo(merged.AsSpan(at));
                at += local - from;
                from = local;
                merged[at++] = added[i];
                if (i + 1 == added.Length || ordinals[i + 1] != ordinals[i])
                {
                    cuts[cutCount++] = at;
                }
            }

            old[from..].CopyTo(merged.AsSpan(at));

            var piece = 0;
            for (var c = 0; c < cutCount; c++)
            {
                var cut = cuts[c];
                if (cut > piece && cut < total)
                {
                    Pieces(merged, piece, cut, endsAtRows: true);
                    piece = cut;
                }
            }

            Pieces(merged, piece, total, endsAtRows: cuts[cutCount - 1] == total);
        }

        /// <summary>Positions in leaves of their own, each with room.</summary>
        public void Fresh(ReadOnlySpan<int> positions)
        {
            for (var start = 0; start < positions.Length; start += Capacity)
            {
                var piece = positions.Slice(start, Math.Min(Capacity, positions.Length - start));
                Keep(Leaf.WithRoom(piece, Capacity), piece.Length);
            }
        }

        /// <summary>
        /// The merged positions from <paramref name="from"/> to <paramref name="to"/> as leaves — one, or
        /// halves when it is too long for one. The last is given room for as many again when the run
        /// ends where rows were put, so that the next rows there are written in place; every other is a
        /// stretch of the merged array.
        /// </summary>
        private void Pieces(int[] merged, int from, int to, bool endsAtRows)
        {
            var length = to - from <= Capacity ? to - from : Capacity / 2;
            for (var start = from; start < to; start += length)
            {
                var size = Math.Min(length, to - start);
                Keep(
                    endsAtRows && start + size == to
                        ? Leaf.WithRoom(merged.AsSpan(start, size), Math.Clamp(size * 2, 64, Capacity))
                        : Leaf.Stretch(merged, start, size),
                    size);
            }
        }

        public PositionLeaves Done()
        {
            var starts = new int[_counts.Count + 1];
            for (var i = 0; i < _counts.Count; i++)
            {
                starts[i + 1] = starts[i] + _counts[i];
            }

            return new PositionLeaves([.. _leaves], starts);
        }
    }
}

using System.Collections.Concurrent;

namespace Chalk.Sample.Blotter.Live;

/// <summary>Rows <see cref="Start"/> on of the merged blotter, out of <see cref="Total"/>, as of <see cref="State"/>.</summary>
public sealed record Page(long Start, TradeVersion[] Rows, long Total, ScopeState State);

/// <summary>
/// Pages of the blotter by position, merged from a snapshot generation and a live scope.
/// </summary>
/// <remarks>
/// <para>
/// The blotter is the snapshot's rows that pass the filter, less those the tail overrides, plus the
/// tail's rows that pass it, all in one order. A snapshot row at position <c>j</c> of the
/// snapshot's own order is at position <c>j − overridden before it + live before it</c> of the
/// blotter, and a tail row at <c>live before it + snapshot rows before it − overridden before
/// it</c>. Both counts of the tail come from the scope, sorted in the host; the snapshot's come from
/// the database.
/// </para>
/// <para>
/// So a page at position <c>i</c> is found by reading the snapshot near position <c>i</c> less the
/// tail's correction, placing every row of that stretch, and reading further when the stretch does
/// not cover the page. The snapshot is read in aligned chunks, kept by generation, filter and order:
/// a generation never changes, so a chunk stays right for as long as its generation is read, and
/// a page that moves only because the tail did is placed again without a query.
/// </para>
/// <para>
/// Every page is checked as it is put together: its rows' positions must be exactly the page's.
/// A page that fails the check is never shown; the failure says which rows disagree.
/// </para>
/// </remarks>
public sealed class Pages(SnapshotQueries queries)
{
    /// <summary>Rows per chunk of a snapshot read.</summary>
    public const int ChunkRows = 256;

    private readonly ConcurrentDictionary<ChunkKey, Chunk> _chunks = new();
    private readonly ConcurrentDictionary<(long Generation, string Filter), Lazy<Task<long>>> _counts = new();
    private long _cachedRows;
    private long _clock;

    /// <summary>The most snapshot rows the chunk cache keeps, set from the budget.</summary>
    public long MaxCachedRows { get; set; } = 64 * ChunkRows;

    /// <summary>What the chunk cache holds, in bytes.</summary>
    public long Bytes => Interlocked.Read(ref _cachedRows) * TradeVersion.Bytes;

    public int Chunks => _chunks.Count;

    /// <summary>Snapshot reads the page has had to make, against those the cache answered.</summary>
    public long Reads => Interlocked.Read(ref _reads);

    public long Hits => Interlocked.Read(ref _hits);

    private long _reads;
    private long _hits;

    /// <summary>How many rows of the blotter there are: the snapshot's, less those overridden, plus the tail's.</summary>
    public async Task<long> TotalAsync(ScopeState state, CancellationToken ct)
    {
        var generation = state.Generation ?? throw new InvalidOperationException("the scope has no state yet");
        var snapshot = await CountAsync(generation.Number, state.Filter, ct).ConfigureAwait(false);
        return snapshot - state.Overridden.Length + state.Live.Length;
    }

    public async Task<Page> GetAsync(ScopeState state, SortSpec order, long start, int count, CancellationToken ct)
    {
        var generation = state.Generation ?? throw new InvalidOperationException("the scope has no state yet");
        var sorted = state.Sorted(order);
        var snapshotRows = await CountAsync(generation.Number, state.Filter, ct).ConfigureAwait(false);
        var total = snapshotRows - sorted.Overridden.Length + sorted.Live.Length;
        var end = Math.Min(total, start + count);
        if (start >= end)
        {
            return new Page(start, [], total, state);
        }

        if (snapshotRows == 0)
        {
            return new Page(start, sorted.Live[(int)start..(int)end], total, state);
        }

        // Where the page's first row would be in the snapshot, were nothing overridden or live
        // before it; corrected below as the stretch read says.
        var around = start;
        var margin = 32L;
        for (var attempt = 0; attempt < 12; attempt++)
        {
            var to = Math.Clamp(around + (end - start) + margin, 1, snapshotRows);
            var from = Math.Clamp(around - margin, 0, to - 1);
            var stretch = await RangeAsync(generation.Number, state.Filter, order, from, to, snapshotRows, ct).ConfigureAwait(false);
            var placed = Place(sorted, state, stretch, from, snapshotRows, total);
            var (low, high) = (placed.Count == 0 ? long.MaxValue : placed[0].Rank, placed.Count == 0 ? long.MinValue : placed[^1].Rank);
            if (placed.Count > 0 && low <= start && high >= end - 1)
            {
                return new Page(start, Slice(placed, start, end), total, state);
            }

            if (placed.Count == 0)
            {
                margin *= 4;
                continue;
            }

            if (start < low)
            {
                around -= low - start;
            }
            else if (end - 1 > high)
            {
                around += end - 1 - high;
            }

            margin *= 2;
        }

        throw new InvalidOperationException(
            $"no stretch of generation {generation.Number} covers rows {start}–{end - 1} of {total} in {order}");
    }

    /// <summary>
    /// How many rows of the blotter sort before <paramref name="key"/>: the position a row with that
    /// key would have. A page that keeps its place while rows arrive above it reads this for its first row.
    /// </summary>
    public async Task<long> RankAsync(ScopeState state, SortSpec order, TradeVersion key, CancellationToken ct)
    {
        var generation = state.Generation ?? throw new InvalidOperationException("the scope has no state yet");
        var sorted = state.Sorted(order);
        var before = SnapshotBeforeFromCache(generation.Number, state.Filter, order, key)
            ?? await queries.CountBeforeAsync(generation.Number, state.Filter, order, key, ct).ConfigureAwait(false);
        return before - sorted.OverriddenBefore(key) + sorted.LiveBefore(key);
    }

    /// <summary>Forgets every chunk and count of generations other than <paramref name="keep"/>.</summary>
    public void Retire(IReadOnlySet<long> keep)
    {
        foreach (var key in _chunks.Keys)
        {
            if (!keep.Contains(key.Generation) && _chunks.TryRemove(key, out var gone))
            {
                Interlocked.Add(ref _cachedRows, -gone.Rows.Length);
            }
        }

        foreach (var key in _counts.Keys)
        {
            if (!keep.Contains(key.Generation))
            {
                _counts.TryRemove(key, out _);
            }
        }
    }

    /// <summary>
    /// Places every row the stretch decides: its snapshot rows not overridden, and the tail's rows
    /// between its first and last, with the tail's rows before it when it starts the snapshot and
    /// after it when it ends it. Returned in order of position.
    /// </summary>
    private static List<(long Rank, TradeVersion Row)> Place(
        SortedScope sorted, ScopeState state, TradeVersion[] stretch, long from, long snapshotRows, long total)
    {
        var placed = new List<(long Rank, TradeVersion Row)>(stretch.Length + 16);
        var order = sorted.Order;
        var startsSnapshot = from == 0;
        var endsSnapshot = from + stretch.Length == snapshotRows;

        for (var x = 0; x < stretch.Length; x++)
        {
            var row = stretch[x];
            if (!state.OverriddenIds.Contains(row.TradeId))
            {
                placed.Add((from + x - sorted.OverriddenBefore(row) + sorted.LiveBefore(row), row));
            }
        }

        var live = sorted.Live;
        var first = startsSnapshot ? 0 : sorted.LiveBefore(stretch[0]);
        for (var k = first; k < live.Length; k++)
        {
            var row = live[k];
            if (!endsSnapshot && order.Compare(row, stretch[^1]) > 0)
            {
                break;
            }

            var snapshotBefore = from + Before(stretch, row, order);
            placed.Add((k + snapshotBefore - sorted.OverriddenBefore(row), row));
        }

        placed.Sort((a, b) => a.Rank.CompareTo(b.Rank));
        for (var i = 1; i < placed.Count; i++)
        {
            if (placed[i].Rank != placed[i - 1].Rank + 1)
            {
                throw new InvalidOperationException(
                    $"the merge placed {placed[i - 1].Row} at {placed[i - 1].Rank} and {placed[i].Row} at {placed[i].Rank}: "
                    + "positions must follow one another");
            }
        }

        if (placed.Count > 0 && (placed[^1].Rank >= total || placed[0].Rank < 0))
        {
            throw new InvalidOperationException(
                $"the merge placed rows at {placed[0].Rank}–{placed[^1].Rank}, outside the blotter's {total}");
        }

        return placed;
    }

    private static TradeVersion[] Slice(List<(long Rank, TradeVersion Row)> placed, long start, long end)
    {
        var offset = (int)(start - placed[0].Rank);
        var rows = new TradeVersion[end - start];
        for (var i = 0; i < rows.Length; i++)
        {
            rows[i] = placed[offset + i].Row;
        }

        return rows;
    }

    /// <summary>How many rows of <paramref name="rows"/>, in order, sort before <paramref name="key"/>.</summary>
    private static int Before(TradeVersion[] rows, TradeVersion key, SortSpec order)
    {
        int low = 0, high = rows.Length;
        while (low < high)
        {
            var middle = (low + high) >>> 1;
            if (order.Compare(rows[middle], key) < 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    private async Task<TradeVersion[]> RangeAsync(
        long generation, FilterSpec filter, SortSpec order, long from, long to, long snapshotRows, CancellationToken ct)
    {
        var rows = new List<TradeVersion>((int)(to - from));
        for (var chunk = from / ChunkRows; chunk * ChunkRows < to; chunk++)
        {
            var chunkRows = await ChunkAsync(generation, filter, order, chunk, ct).ConfigureAwait(false);
            var chunkStart = chunk * ChunkRows;
            var expected = Math.Min(ChunkRows, snapshotRows - chunkStart);
            if (chunkRows.Length != expected)
            {
                // The generation is gone from the database, or not the size it was counted at.
                throw new InvalidOperationException(
                    $"generation {generation} gave {chunkRows.Length} rows at {chunkStart}, not {expected}");
            }

            var skip = (int)Math.Max(0, from - chunkStart);
            var take = (int)Math.Min(chunkRows.Length - skip, to - chunkStart - skip);
            rows.AddRange(chunkRows.AsSpan(skip, take));
        }

        return [.. rows];
    }

    private async Task<TradeVersion[]> ChunkAsync(long generation, FilterSpec filter, SortSpec order, long chunk, CancellationToken ct)
    {
        var key = new ChunkKey(generation, filter.Name, order.Name, chunk);
        if (_chunks.TryGetValue(key, out var cached))
        {
            cached.Used = Interlocked.Increment(ref _clock);
            Interlocked.Increment(ref _hits);
            return cached.Rows;
        }

        Interlocked.Increment(ref _reads);
        var rows = await queries.PageAsync(generation, filter, order, chunk * ChunkRows, ChunkRows, ct).ConfigureAwait(false);
        if (_chunks.TryAdd(key, new Chunk(rows, filter, order) { Used = Interlocked.Increment(ref _clock) }))
        {
            Interlocked.Add(ref _cachedRows, rows.Length);
            Evict();
        }

        return rows;
    }

    private void Evict()
    {
        while (Interlocked.Read(ref _cachedRows) > MaxCachedRows && !_chunks.IsEmpty)
        {
            var oldest = _chunks.MinBy(c => c.Value.Used);
            if (_chunks.TryRemove(oldest.Key, out var gone))
            {
                Interlocked.Add(ref _cachedRows, -gone.Rows.Length);
            }
        }
    }

    private long? SnapshotBeforeFromCache(long generation, FilterSpec filter, SortSpec order, TradeVersion key)
    {
        foreach (var (chunkKey, chunk) in _chunks)
        {
            if (chunkKey.Generation != generation || chunkKey.Filter != filter.Name || chunkKey.Order != order.Name
                || chunk.Rows.Length == 0)
            {
                continue;
            }

            // Inside the chunk, or just past its end with the next chunk's first row after the key.
            if (order.Compare(chunk.Rows[0], key) <= 0 && order.Compare(key, chunk.Rows[^1]) <= 0)
            {
                return chunkKey.Index * ChunkRows + Before(chunk.Rows, key, order);
            }

            if (chunkKey.Index == 0 && order.Compare(key, chunk.Rows[0]) < 0)
            {
                return 0;
            }
        }

        return null;
    }

    private async Task<long> CountAsync(long generation, FilterSpec filter, CancellationToken ct)
    {
        var lazy = _counts.GetOrAdd(
            (generation, filter.Name),
            _ => new Lazy<Task<long>>(() => queries.CountAsync(generation, filter, CancellationToken.None)));
        try
        {
            return await lazy.Value.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception) when (lazy.Value.IsFaulted)
        {
            _counts.TryRemove(new KeyValuePair<(long, string), Lazy<Task<long>>>((generation, filter.Name), lazy));
            throw;
        }
    }

    private readonly record struct ChunkKey(long Generation, string Filter, string Order, long Index);

    private sealed class Chunk(TradeVersion[] rows, FilterSpec filter, SortSpec order)
    {
        public TradeVersion[] Rows { get; } = rows;

        public FilterSpec Filter { get; } = filter;

        public SortSpec Order { get; } = order;

        public long Used;
    }
}

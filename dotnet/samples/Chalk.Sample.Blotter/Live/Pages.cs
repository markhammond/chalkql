using System.Collections.Concurrent;

namespace Chalk.Sample.Blotter.Live;

/// <summary>Rows <see cref="Start"/> on of a merged tab, out of <see cref="Total"/>, as of <see cref="State"/>.</summary>
public sealed record Page(long Start, IRow[] Rows, long Total, ScopeState State);

/// <summary>
/// Pages of a tab by position, merged from a snapshot generation and a live scope.
/// </summary>
/// <remarks>
/// <para>
/// A tab is the generation's rows that pass the filter, less those the tail overrides, plus the tail's
/// rows that pass it, all in one order. A generation row at position <c>j</c> of the generation's own
/// order is at position <c>j − overridden before it + live before it</c> of the tab, and a tail row at
/// <c>live before it + generation rows before it − overridden before it</c>. Both counts of the tail
/// come from the scope, sorted in the host; the generation's come from the database. Fills are never
/// overridden: the database's are those at or before the watermark, the tail's those after it.
/// </para>
/// <para>
/// So a page at position <c>i</c> is found by reading the generation near position <c>i</c> less the
/// tail's correction, placing every row of that stretch, and reading further when the stretch does
/// not cover the page. The generation is read in aligned chunks, kept by tab, generation, filter and
/// order: a generation never changes, so a chunk stays right for as long as its generation is read,
/// and a page that moves only because the tail did is placed again without a query.
/// </para>
/// <para>
/// Every page is checked as it is put together: its rows' positions must be exactly the page's. A
/// page that fails the check is never shown; the failure says which rows disagree.
/// </para>
/// </remarks>
public sealed class Pages(SnapshotQueries queries)
{
    /// <summary>Rows per chunk of a generation read.</summary>
    public const int ChunkRows = 256;

    private readonly ConcurrentDictionary<ChunkKey, Chunk> _chunks = new();
    private readonly ConcurrentDictionary<(string Tab, long Generation, string Filter), Lazy<Task<long>>> _counts = new();
    private readonly ConcurrentDictionary<(long Generation, Guid Order), Lazy<Task<Fill[]>>> _fills = new();
    private long _cachedRows;
    private long _clock;
    private long _reads;
    private long _hits;

    /// <summary>The most generation rows the chunk cache keeps, set from the budget.</summary>
    public long MaxCachedRows { get; set; } = 64 * ChunkRows;

    /// <summary>What the chunk cache holds, in bytes.</summary>
    public long Bytes => Interlocked.Read(ref _cachedRows) * Order.Bytes;

    public int Chunks => _chunks.Count;

    /// <summary>Generation reads the pages have had to make, against those the cache answered.</summary>
    public long Reads => Interlocked.Read(ref _reads);

    public long Hits => Interlocked.Read(ref _hits);

    /// <summary>How many rows the generation has that pass the filter, counted by the database.</summary>
    public Task<long> GenerationRowsAsync(ScopeState state, CancellationToken ct) =>
        CountAsync(state.Tab, Generation(state), state.Filter, ct);

    /// <summary>How many rows the tab has: the generation's, less those overridden, plus the tail's.</summary>
    public async Task<long> TotalAsync(ScopeState state, CancellationToken ct)
    {
        var rows = await CountAsync(state.Tab, Generation(state), state.Filter, ct).ConfigureAwait(false);
        return rows - state.Overridden.Length + state.Live.Length;
    }

    public async Task<Page> GetAsync(ScopeState state, SortSpec order, long start, int count, CancellationToken ct)
    {
        var generation = Generation(state);
        var sorted = state.Sorted(order);
        var generationRows = await CountAsync(state.Tab, generation, state.Filter, ct).ConfigureAwait(false);
        var total = generationRows - sorted.Overridden.Length + sorted.Live.Length;
        var end = Math.Min(total, start + count);
        if (start >= end)
        {
            return new Page(start, [], total, state);
        }

        if (generationRows == 0)
        {
            return new Page(start, sorted.Live[(int)start..(int)end], total, state);
        }

        // Where the page's first row would be in the generation, were nothing overridden or live
        // before it; corrected below as the stretch read says.
        var around = start;
        var margin = 32L;
        for (var attempt = 0; attempt < 12; attempt++)
        {
            var to = Math.Clamp(around + (end - start) + margin, 1, generationRows);
            var from = Math.Clamp(around - margin, 0, to - 1);
            var stretch = await RangeAsync(state, generation, order, from, to, generationRows, ct).ConfigureAwait(false);
            var placed = Place(sorted, state, stretch, from, generationRows, total);
            if (placed.Count == 0)
            {
                margin *= 4;
                continue;
            }

            var (low, high) = (placed[0].Rank, placed[^1].Rank);
            if (low <= start && high >= end - 1)
            {
                return new Page(start, Slice(placed, start, end), total, state);
            }

            if (start < low)
            {
                around -= low - start;
            }
            else
            {
                around += end - 1 - high;
            }

            margin *= 2;
        }

        throw new InvalidOperationException(
            $"no stretch of generation {generation.Number} covers rows {start}–{end - 1} of {total} in {order}");
    }

    /// <summary>
    /// How many rows of the tab sort before <paramref name="key"/>: the position a row with that key
    /// would have. A page that keeps its place while rows arrive above it reads this for its first row,
    /// and a page with nested rows for each order it has opened.
    /// </summary>
    public async Task<long> RankAsync(ScopeState state, SortSpec order, IRow key, CancellationToken ct)
    {
        var generation = Generation(state);
        var sorted = state.Sorted(order);
        var before = BeforeFromCache(state, generation, order, key)
            ?? await queries.CountBeforeAsync(generation, state.Filter, order, key, ct).ConfigureAwait(false);
        return before - sorted.OverriddenBefore(key) + sorted.LiveBefore(key);
    }

    /// <summary>
    /// An order's fills as of a state, newest first: the database's at or before the generation's
    /// watermark, read once per generation, and the tail's after it, as the state holds them.
    /// </summary>
    public async Task<Fill[]> FillsOfAsync(ScopeState state, Guid orderId, CancellationToken ct)
    {
        var generation = Generation(state);
        var lazy = _fills.GetOrAdd(
            (generation.Number, orderId),
            _ => new Lazy<Task<Fill[]>>(() => queries.FillsOfAsync(generation, orderId, CancellationToken.None)));
        Fill[] stored;
        try
        {
            stored = await lazy.Value.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception) when (lazy.Value.IsFaulted)
        {
            _fills.TryRemove(new KeyValuePair<(long, Guid), Lazy<Task<Fill[]>>>((generation.Number, orderId), lazy));
            throw;
        }

        var tail = state.FillsByOrder.GetValueOrDefault(orderId);
        return [.. stored.Concat(tail ?? []).OrderByDescending(f => f.EventTime).ThenByDescending(f => f.FillId)];
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

        foreach (var key in _counts.Keys.Where(k => !keep.Contains(k.Generation)))
        {
            _counts.TryRemove(key, out _);
        }

        foreach (var key in _fills.Keys.Where(k => !keep.Contains(k.Generation)))
        {
            _fills.TryRemove(key, out _);
        }
    }

    private static Generation Generation(ScopeState state) =>
        state.Generation ?? throw new InvalidOperationException("the scope has no state yet");

    /// <summary>
    /// Places every row the stretch decides: its generation rows not overridden, and the tail's rows
    /// between its first and last, with the tail's rows before it when it starts the generation and
    /// after it when it ends it. Returned in order of position.
    /// </summary>
    private static List<(long Rank, IRow Row)> Place(
        SortedScope sorted, ScopeState state, IRow[] stretch, long from, long generationRows, long total)
    {
        var placed = new List<(long Rank, IRow Row)>(stretch.Length + 16);
        var order = sorted.Order;
        var startsGeneration = from == 0;
        var endsGeneration = from + stretch.Length == generationRows;

        for (var x = 0; x < stretch.Length; x++)
        {
            var row = stretch[x];
            if (!state.OverriddenIds.Contains(row.Id))
            {
                placed.Add((from + x - sorted.OverriddenBefore(row) + sorted.LiveBefore(row), row));
            }
        }

        var live = sorted.Live;
        var first = startsGeneration ? 0 : sorted.LiveBefore(stretch[0]);
        for (var k = first; k < live.Length; k++)
        {
            var row = live[k];
            if (!endsGeneration && order.Compare(row, stretch[^1]) > 0)
            {
                break;
            }

            var generationBefore = from + Before(stretch, row, order);
            placed.Add((k + generationBefore - sorted.OverriddenBefore(row), row));
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
                $"the merge placed rows at {placed[0].Rank}–{placed[^1].Rank}, outside the tab's {total}");
        }

        return placed;
    }

    private static IRow[] Slice(List<(long Rank, IRow Row)> placed, long start, long end)
    {
        var offset = (int)(start - placed[0].Rank);
        var rows = new IRow[end - start];
        for (var i = 0; i < rows.Length; i++)
        {
            rows[i] = placed[offset + i].Row;
        }

        return rows;
    }

    /// <summary>How many rows of <paramref name="rows"/>, in order, sort before <paramref name="key"/>.</summary>
    private static int Before(IRow[] rows, IRow key, SortSpec order)
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

    private async Task<IRow[]> RangeAsync(
        ScopeState state, Generation generation, SortSpec order, long from, long to, long generationRows, CancellationToken ct)
    {
        var rows = new List<IRow>((int)(to - from));
        for (var chunk = from / ChunkRows; chunk * ChunkRows < to; chunk++)
        {
            var chunkRows = await ChunkAsync(state, generation, order, chunk, ct).ConfigureAwait(false);
            var chunkStart = chunk * ChunkRows;
            var expected = Math.Min(ChunkRows, generationRows - chunkStart);
            if (chunkRows.Length != expected)
            {
                // The generation is gone from the database, or not the size it was counted at.
                throw new InvalidOperationException(
                    $"generation {generation.Number} gave {chunkRows.Length} rows at {chunkStart}, not {expected}");
            }

            var skip = (int)Math.Max(0, from - chunkStart);
            var take = (int)Math.Min(chunkRows.Length - skip, to - chunkStart - skip);
            rows.AddRange(chunkRows.AsSpan(skip, take));
        }

        return [.. rows];
    }

    private async Task<IRow[]> ChunkAsync(ScopeState state, Generation generation, SortSpec order, long chunk, CancellationToken ct)
    {
        var key = new ChunkKey(state.Tab.Name, generation.Number, state.Filter.Name, order.Name, chunk);
        if (_chunks.TryGetValue(key, out var cached))
        {
            cached.Used = Interlocked.Increment(ref _clock);
            Interlocked.Increment(ref _hits);
            return cached.Rows;
        }

        Interlocked.Increment(ref _reads);
        var rows = await queries.PageAsync(generation, state.Filter, order, chunk * ChunkRows, ChunkRows, ct).ConfigureAwait(false);
        if (_chunks.TryAdd(key, new Chunk(rows) { Used = Interlocked.Increment(ref _clock) }))
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

    private long? BeforeFromCache(ScopeState state, Generation generation, SortSpec order, IRow key)
    {
        foreach (var (chunkKey, chunk) in _chunks)
        {
            if (chunkKey.Tab != state.Tab.Name || chunkKey.Generation != generation.Number || chunkKey.Filter != state.Filter.Name
                || chunkKey.Order != order.Name || chunk.Rows.Length == 0)
            {
                continue;
            }

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

    private async Task<long> CountAsync(Tab tab, Generation generation, FilterSpec filter, CancellationToken ct)
    {
        var key = (tab.Name, generation.Number, filter.Name);
        var lazy = _counts.GetOrAdd(key, _ => new Lazy<Task<long>>(() => queries.CountAsync(tab, generation, filter, CancellationToken.None)));
        try
        {
            return await lazy.Value.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception) when (lazy.Value.IsFaulted)
        {
            _counts.TryRemove(new KeyValuePair<(string, long, string), Lazy<Task<long>>>(key, lazy));
            throw;
        }
    }

    private readonly record struct ChunkKey(string Tab, long Generation, string Filter, string Order, long Index);

    private sealed class Chunk(IRow[] rows)
    {
        public IRow[] Rows { get; } = rows;

        public long Used;
    }
}

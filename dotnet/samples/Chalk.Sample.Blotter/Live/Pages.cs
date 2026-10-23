using System.Collections.Concurrent;

namespace Chalk.Sample.Blotter.Live;

/// <summary>Rows <see cref="Start"/> on of a tab, out of about <see cref="Total"/>, as of <see cref="State"/>.</summary>
public sealed record Page(long Start, IRow[] Rows, long Total, ScopeState State);

/// <summary>
/// A tab's rows, read live from the database and merged with a live scope.
/// </summary>
/// <remarks>
/// <para>
/// A tab is the database's rows that pass the filter, each order at the version <c>orders_current</c>
/// holds, with the tail laid over them: an order the stream has sent a newer version of is shown at
/// that version, where that version sorts, or not at all if it no longer passes. The database's row of
/// an order newer than the tail's stands. So every order is shown once, at the newest version either
/// side knows, however far <c>orders_current</c> runs behind the stream, or ahead of it.
/// </para>
/// <para>
/// Rows are read from a key: the database's first rows at or after it in order, then every row of the
/// tail between that key and the last row read. The read decides nothing past its last row, so a tail
/// row beyond it waits for the next read. An order the tail has, read in the stretch at an older
/// version, gives way to the tail's version; one whose tail version falls in the stretch but whose
/// database row did not is looked up, because the database may have moved past the tail meanwhile.
/// </para>
/// <para>
/// Nothing is cached but counts and an order's fills, each for a moment: a page is read from the
/// database each time, and is as current as the database is when it is read.
/// </para>
/// </remarks>
public sealed class Pages(SourceQueries queries)
{
    /// <summary>How long a count of the database's rows is reused: the total is approximate anyway.</summary>
    public static readonly TimeSpan CountFor = TimeSpan.FromSeconds(3);

    /// <summary>How long an order's fills in the database are reused.</summary>
    public static readonly TimeSpan FillsFor = TimeSpan.FromSeconds(1);

    // Rows read beyond those asked for, so that the tail's overrides seldom leave a read short.
    private const int Slack = 32;

    private readonly ConcurrentDictionary<(string Tab, string Filter), (DateTime At, Task<long> Count)> _counts = new();
    private readonly ConcurrentDictionary<Guid, (DateTime At, Task<Fill[]> Fills)> _fills = new();
    private long _lookups;
    private long _rereads;

    public SourceQueries Queries => queries;

    /// <summary>Lookups of orders the tail placed in a read, to see whether the database has moved past them.</summary>
    public long Lookups => Interlocked.Read(ref _lookups);

    /// <summary>Reads read again, longer, because the tail's overrides left them short.</summary>
    public long Rereads => Interlocked.Read(ref _rereads);

    /// <summary>What the cache of counts and fills holds, roughly, in bytes.</summary>
    public long Bytes => _counts.Count * 64L + _fills.Values.Sum(f => f.Fills.IsCompletedSuccessfully ? f.Fills.Result.Length * (long)Fill.Bytes : 0);

    /// <summary>A reader's place in the tab: the pages it reads, by position.</summary>
    public PageView View(SortSpec order) => new(this, order);

    /// <summary>
    /// How many rows of the database pass the filter, counted a moment ago. A count older than
    /// <see cref="CountFor"/> is counted again, and answers meanwhile: no page waits for a recount.
    /// </summary>
    public async Task<long> DatabaseRowsAsync(ScopeState state, CancellationToken ct)
    {
        var key = (state.Tab.Name, state.Filter.Name);
        var now = DateTime.UtcNow;
        var counted = _counts.GetOrAdd(key, _ => (now, queries.CountAsync(state.Tab, state.Filter, CancellationToken.None)));
        if ((now - counted.At > CountFor && counted.Count.IsCompleted) || counted.Count.IsFaulted)
        {
            var recount = (now, queries.CountAsync(state.Tab, state.Filter, CancellationToken.None));
            if (_counts.TryUpdate(key, recount, counted) && counted.Count.IsCompletedSuccessfully)
            {
                return counted.Count.Result;
            }

            counted = _counts[key];
        }

        return await counted.Count.WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>About how many rows the tab has: the database's, less the rows the tail overrides, plus the tail's.</summary>
    public async Task<long> TotalAsync(ScopeState state, CancellationToken ct)
    {
        var rows = await DatabaseRowsAsync(state, ct).ConfigureAwait(false);
        return Math.Max(0, rows - state.Overridden.Length + state.Live.Length);
    }

    /// <summary>
    /// The tab's rows from <paramref name="key"/> on (or after it), or from the first, in
    /// <paramref name="order"/>: at least <paramref name="count"/> of them if the tab has that many.
    /// </summary>
    public async Task<IRow[]> FromAsync(ScopeState state, SortSpec order, IRow? key, bool inclusive, int count, CancellationToken ct)
    {
        var sorted = state.Sorted(order);
        var limit = count + Slack;
        while (true)
        {
            var read = await queries.ReadAsync(state.Filter, order, key, inclusive, limit, ct).ConfigureAwait(false);

            // A full read decides nothing past its last row; a short one has reached the end.
            var last = read.Length == limit ? read[^1] : null;
            var candidates = sorted.LiveBetween(key, inclusive, last);
            var rows = order.Tab.Overrides
                ? await MergeOrdersAsync(state, order, key, inclusive, last, read, candidates, ct).ConfigureAwait(false)
                : MergeFills(read, candidates);
            if (rows.Count >= count || last is null)
            {
                rows.Sort(order);
                return [.. rows];
            }

            Interlocked.Increment(ref _rereads);
            limit *= 4;
        }
    }

    /// <summary>
    /// How many rows of the tab sort before <paramref name="key"/>, about: the database's count, with
    /// the tail's corrections. A held page counts what has arrived above it with this.
    /// </summary>
    public async Task<long> RankAsync(ScopeState state, SortSpec order, IRow key, CancellationToken ct)
    {
        var sorted = state.Sorted(order);
        var before = await queries.CountBeforeAsync(state.Filter, order, key, ct).ConfigureAwait(false);
        return Math.Max(0, before - sorted.OverriddenBefore(key) + sorted.LiveBefore(key));
    }

    /// <summary>An order's fills as of a state, newest first: the database's, read a moment ago at most, and the tail's.</summary>
    public async Task<Fill[]> FillsOfAsync(ScopeState state, Guid orderId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var read = _fills.AddOrUpdate(
            orderId,
            _ => (now, queries.FillsOfAsync(orderId, CancellationToken.None)),
            (_, was) => now - was.At > FillsFor || was.Fills.IsFaulted ? (now, queries.FillsOfAsync(orderId, CancellationToken.None)) : was);
        foreach (var stale in _fills.Where(f => now - f.Value.At > FillsFor * 10).Select(f => f.Key).ToList())
        {
            _fills.TryRemove(stale, out _);
        }

        var stored = await read.Fills.WaitAsync(ct).ConfigureAwait(false);
        return [.. stored.Concat(state.FillsByOrder.GetValueOrDefault(orderId) ?? [])
            .DistinctBy(f => f.FillId)
            .OrderByDescending(f => f.EventTime)
            .ThenByDescending(f => f.FillId)];
    }

    private async Task<List<IRow>> MergeOrdersAsync(
        ScopeState state, SortSpec order, IRow? key, bool inclusive, IRow? last, IRow[] read, ArraySegment<IRow> candidates,
        CancellationToken ct)
    {
        var winners = new Dictionary<Guid, IRow>(read.Length + candidates.Count);
        var readById = new Dictionary<Guid, IRow>(read.Length);
        foreach (var row in read)
        {
            readById[row.Id] = row;

            // An order the tail has as new or newer is the tail's to place, below, if it falls here at all.
            if (!(state.Latest.TryGetValue(row.Id, out var latest) && latest.Version >= row.Version))
            {
                winners[row.Id] = row;
            }
        }

        // The tail's rows here whose database rows were not read: the database may have moved past
        // them since the tail last heard, and then its row stands, wherever it sorts.
        var unread = candidates.Where(c => !readById.ContainsKey(c.Id)).Select(c => c.Id).ToList();
        var current = new Dictionary<Guid, Order>();
        if (unread.Count > 0)
        {
            Interlocked.Increment(ref _lookups);
            current = await queries.CurrentAsync(unread, ct).ConfigureAwait(false);
        }

        foreach (var candidate in candidates)
        {
            if (readById.TryGetValue(candidate.Id, out var stored) && stored.Version > candidate.Version)
            {
                continue;
            }

            if (current.TryGetValue(candidate.Id, out var now) && now.Version > candidate.Version)
            {
                if (Within(order, now, key, inclusive, last) && state.Filter.Matches(now))
                {
                    winners[now.Id] = now;
                }

                continue;
            }

            winners[candidate.Id] = candidate;
        }

        return [.. winners.Values];
    }

    /// <summary>Fills never change: one in both the database and the tail is the same row, kept once.</summary>
    private static List<IRow> MergeFills(IRow[] read, ArraySegment<IRow> candidates)
    {
        var rows = new Dictionary<Guid, IRow>(read.Length + candidates.Count);
        foreach (var row in read)
        {
            rows[row.Id] = row;
        }

        foreach (var candidate in candidates)
        {
            rows.TryAdd(candidate.Id, candidate);
        }

        return [.. rows.Values];
    }

    private static bool Within(SortSpec order, IRow row, IRow? key, bool inclusive, IRow? last) =>
        (key is null || order.Compare(row, key) is var from && (from > 0 || (inclusive && from == 0)))
        && (last is null || order.Compare(row, last) <= 0);
}

/// <summary>
/// One reader's place in a tab: the positions it has read, each tied to the row found there, so that
/// a page near one is read from that row's key rather than counted from the top.
/// </summary>
/// <remarks>
/// <para>
/// At the top, positions are counted from the first row, afresh on every state, so new rows push the
/// rest down. Once the reader scrolls away (<see cref="Held"/>), the positions hold: a page is read from
/// the nearest position known, so rows arriving above it do not move what is in view.
/// </para>
/// <para>
/// Deep in a tab, positions are approximate. A jump far from every position known is placed by the
/// database's own offset, which knows nothing of the tail; and rows arriving between two positions
/// known shift what lies between them. But every page is read whole, from one key, so a page never
/// repeats or skips a row, and every row in it is at the newest version known.
/// </para>
/// </remarks>
public sealed class PageView(Pages pages, SortSpec order)
{
    /// <summary>How far from a position known a page is read from its key; farther, it is found by offset.</summary>
    public const int SeekWithin = 4096;

    // A position is kept for every so many rows read.
    private const int Every = 64;

    private readonly Lock _gate = new();
    private readonly SortedList<long, IRow> _anchors = [];
    private long _epoch = -1;
    private bool _held;

    public SortSpec Order => order;

    /// <summary>Whether positions hold as rows arrive, or are counted afresh from the top on every state.</summary>
    public bool Held
    {
        get => _held;
        set
        {
            lock (_gate)
            {
                _held = value;
                if (!value)
                {
                    _anchors.Clear();
                }
            }
        }
    }

    /// <summary>Rows <paramref name="start"/> on, <paramref name="count"/> of them or as many as there are.</summary>
    public async Task<Page> GetAsync(ScopeState state, long start, int count, CancellationToken ct)
    {
        var total = await pages.TotalAsync(state, ct).ConfigureAwait(false);
        (long At, IRow Key)? below, above;
        lock (_gate)
        {
            if (!_held && state.Epoch != _epoch)
            {
                _anchors.Clear();
            }

            _epoch = state.Epoch;
            below = Floor(start);
            above = Ceiling(start);
        }

        IRow[] window;
        long first;
        if (below is null && start <= SeekWithin)
        {
            window = await pages.FromAsync(state, order, null, true, (int)start + count, ct).ConfigureAwait(false);
            first = 0;
        }
        else if (below is { } b && start - b.At <= SeekWithin)
        {
            window = await pages.FromAsync(state, order, b.Key, true, (int)(start - b.At) + count, ct).ConfigureAwait(false);
            first = b.At;
        }
        else if (above is { } a && a.At - start <= SeekWithin)
        {
            // Read backwards from the position known below the page: the rows before it, nearest first.
            var back = await pages.FromAsync(state, order.Reversed(), a.Key, false, (int)(a.At - start), ct).ConfigureAwait(false);
            var ahead = start + count > a.At
                ? await pages.FromAsync(state, order, a.Key, true, (int)(start + count - a.At), ct).ConfigureAwait(false)
                : [];
            window = [.. Enumerable.Reverse(back), .. ahead];
            first = a.At - back.Length;
        }
        else if (!_held && start <= SeekWithin)
        {
            window = await pages.FromAsync(state, order, null, true, (int)start + count, ct).ConfigureAwait(false);
            first = 0;
        }
        else
        {
            // Far from anything known: placed by the database's offset, and read from the row found there.
            var key = await pages.Queries.AtOffsetAsync(state.Filter, order, start, ct).ConfigureAwait(false);
            window = key is null ? [] : await pages.FromAsync(state, order, key, true, count, ct).ConfigureAwait(false);
            first = start;
        }

        Record(first, window);
        var skip = (int)Math.Clamp(start - first, 0, window.Length);
        var rows = window.AsSpan(skip, Math.Min(count, window.Length - skip)).ToArray();
        return new Page(first + skip, rows, total, state);
    }

    /// <summary>Forgets every position known, as a new sort or filter does.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _anchors.Clear();
        }
    }

    private void Record(long first, IRow[] window)
    {
        if (window.Length == 0)
        {
            return;
        }

        lock (_gate)
        {
            // What this read found replaces what earlier reads said of the same positions.
            var end = first + window.Length;
            for (var i = _anchors.Count - 1; i >= 0; i--)
            {
                if (_anchors.Keys[i] >= first && _anchors.Keys[i] < end)
                {
                    _anchors.RemoveAt(i);
                }
            }

            for (var i = 0; i < window.Length; i++)
            {
                if (i == 0 || (first + i) % Every == 0 || i == window.Length - 1)
                {
                    _anchors[first + i] = window[i];
                }
            }
        }
    }

    private (long At, IRow Key)? Floor(long position)
    {
        int low = 0, high = _anchors.Count;
        while (low < high)
        {
            var middle = (low + high) >>> 1;
            if (_anchors.Keys[middle] <= position)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low == 0 ? null : (_anchors.Keys[low - 1], _anchors.Values[low - 1]);
    }

    private (long At, IRow Key)? Ceiling(long position)
    {
        int low = 0, high = _anchors.Count;
        while (low < high)
        {
            var middle = (low + high) >>> 1;
            if (_anchors.Keys[middle] < position)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low == _anchors.Count ? null : (_anchors.Keys[low], _anchors.Values[low]);
    }
}

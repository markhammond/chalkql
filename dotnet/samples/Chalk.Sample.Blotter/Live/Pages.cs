using System.Collections.Concurrent;
using System.Diagnostics;

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
/// What the database returns is kept for a moment (<see cref="WindowFor"/>), as windows: runs of its rows
/// in one order from one key, shared by every reader of the same tab, filter and sort. A read that a
/// window covers is answered from it, and the tail is laid over it afresh each time, so the tail's rows
/// are as current as the stream. A sort whose reads are slow, because no index serves it and the
/// database sorts the whole table, is read in large windows, which cost the database about what a
/// page does; and after each page the next window in the direction of scrolling is read ahead. Counts
/// and an order's fills are kept for a moment too.
/// </para>
/// <para>
/// A window is never older than the tail lets a row stay after the database was seen to hold it, less
/// a margin: so no window is merged with a tail that has already let go of a version the window does
/// not hold.
/// </para>
/// </remarks>
public sealed class Pages(SourceQueries queries)
{
    /// <summary>How long a count of the database's rows is reused: the total is approximate anyway.</summary>
    public static readonly TimeSpan CountFor = TimeSpan.FromSeconds(3);

    /// <summary>How long an order's fills in the database are reused.</summary>
    public static readonly TimeSpan FillsFor = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long a window read from the database answers later reads: a second less than the tail keeps
    /// a row after the database was seen to hold it (<see cref="BlotterOptions.EvictAfter"/>), which the
    /// host sets it from. Zero keeps no windows.
    /// </summary>
    public TimeSpan WindowFor { get; init; } = TimeSpan.FromSeconds(4);

    /// <summary>A read slower than this marks its sort as one to read in large windows.</summary>
    public static readonly TimeSpan SlowRead = TimeSpan.FromMilliseconds(25);

    // Rows read beyond those asked for, so that the tail's overrides seldom leave a read short.
    private const int Slack = 32;

    // A slow sort's window: about what the database's whole-table sort costs for a page anyway.
    private const int LargeWindow = 2048;

    // Windows kept for one tab, filter and sort.
    private const int WindowsPerSort = 6;

    private readonly ConcurrentDictionary<(string Tab, string Filter), (DateTime At, Task<long> Count)> _counts = new();
    private readonly ConcurrentDictionary<Guid, (DateTime At, Task<Fill[]> Fills)> _fills = new();
    private readonly ConcurrentDictionary<(string Tab, string Filter, string Order), Shelf> _windows = new();
    private long _lookups;
    private long _rereads;
    private long _windowHits;
    private long _readAheads;
    private long _keptRows;

    public SourceQueries Queries => queries;

    /// <summary>Lookups of orders the tail placed in a read, to see whether the database has moved past them.</summary>
    public long Lookups => Interlocked.Read(ref _lookups);

    /// <summary>Reads read again, longer, because the tail's overrides left them short.</summary>
    public long Rereads => Interlocked.Read(ref _rereads);

    /// <summary>Reads a kept window answered, without asking the database.</summary>
    public long WindowHits => Interlocked.Read(ref _windowHits);

    /// <summary>Windows read ahead of the scrolling.</summary>
    public long ReadAheads => Interlocked.Read(ref _readAheads);

    /// <summary>The most rows the kept windows may hold, set from the budget: they give way to the tail and the scopes.</summary>
    public long MaxKeptRows { get; set; } = 64 * 1024;

    /// <summary>What the kept windows, counts and fills hold, roughly, in bytes.</summary>
    public long Bytes =>
        Interlocked.Read(ref _keptRows) * Order.Bytes
        + _counts.Count * 64L + _fills.Values.Sum(f => f.Fills.IsCompletedSuccessfully ? f.Fills.Result.Length * (long)Fill.Bytes : 0);

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
        var limit = SourceQueries.Rounded(count + Slack);
        while (true)
        {
            // A full read decides nothing past its last row; a short one has reached the end.
            var (read, last) = await DatabaseAsync(state, order, key, inclusive, limit, ct).ConfigureAwait(false);
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
            limit = SourceQueries.Rounded(limit * 4);
        }
    }

    /// <summary>
    /// Reads the window after <paramref name="after"/> in <paramref name="order"/> in the background,
    /// unless a kept window already covers the next two pages: the next scroll is then answered at once.
    /// </summary>
    public void ReadAhead(ScopeState state, SortSpec order, IRow after, int count)
    {
        var shelf = ShelfOf(state, order);
        var limit = SourceQueries.Rounded((2 * count) + Slack);
        if (Take(shelf, order, after, false, limit) is not null)
        {
            return;
        }

        // On from the end of the window the page came from, so that the two join into one; nothing,
        // when that window reached the end of the tab.
        var from = after;
        lock (shelf.Gate)
        {
            var covering = shelf.Windows.LastOrDefault(w => !Expired(w) && Contains(w, order, after));
            if (covering is { Full: false })
            {
                return;
            }

            if (covering is { Rows.Length: > 0 })
            {
                from = covering.Rows[^1];
            }
        }

        if (Interlocked.Exchange(ref shelf.Reading, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await ReadWindowAsync(shelf, state.Filter, order, from, false, limit, CancellationToken.None).ConfigureAwait(false);
                Interlocked.Increment(ref _readAheads);
            }
            catch (Exception)
            {
                // A read ahead is a guess: the page that needs the rows reads them itself, and says why if it cannot.
            }
            finally
            {
                Volatile.Write(ref shelf.Reading, 0);
            }
        });
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

    /// <summary>
    /// The database's first <paramref name="limit"/> rows from <paramref name="key"/> on, from a kept
    /// window when one covers them, or read in a window of their sort's size; and the last row the read
    /// decides, or null when it reached the end.
    /// </summary>
    private async Task<(IRow[] Read, IRow? Last)> DatabaseAsync(
        ScopeState state, SortSpec order, IRow? key, bool inclusive, int limit, CancellationToken ct)
    {
        var shelf = ShelfOf(state, order);
        if (Take(shelf, order, key, inclusive, limit) is { } kept)
        {
            Interlocked.Increment(ref _windowHits);
            return kept;
        }

        var window = await ReadWindowAsync(shelf, state.Filter, order, key, inclusive, limit, ct).ConfigureAwait(false);
        return Slice(window, order, key, inclusive, limit)
            ?? throw new InvalidOperationException("a window just read does not cover the read it was made for");
    }

    private Shelf ShelfOf(ScopeState state, SortSpec order) =>
        _windows.GetOrAdd((state.Tab.Name, state.Filter.Name, order.Name), _ => new Shelf());

    /// <summary>Reads a window from <paramref name="key"/> on: twice the rows asked for, or a large window for a slow sort; and keeps it.</summary>
    private async Task<Window> ReadWindowAsync(
        Shelf shelf, FilterSpec filter, SortSpec order, IRow? key, bool inclusive, int limit, CancellationToken ct)
    {
        var size = SourceQueries.Rounded(shelf.Slow ? Math.Max(limit, LargeWindow) : 2 * limit);
        var readAt = Stopwatch.GetTimestamp();
        var rows = await queries.ReadAsync(filter, order, key, inclusive, size, ct).ConfigureAwait(false);
        shelf.Slow = Stopwatch.GetElapsedTime(readAt) > SlowRead;
        var window = new Window(key, inclusive, rows, rows.Length == size, readAt);
        Keep(shelf, order, window);
        return window;
    }

    /// <summary>The newest kept window that covers the read, sliced to it; or null.</summary>
    private (IRow[] Read, IRow? Last)? Take(Shelf shelf, SortSpec order, IRow? key, bool inclusive, int limit)
    {
        lock (shelf.Gate)
        {
            shelf.Windows.RemoveAll(w => Expired(w) && Forget(w));
            for (var i = shelf.Windows.Count - 1; i >= 0; i--)
            {
                if (Slice(shelf.Windows[i], order, key, inclusive, limit) is { } slice)
                {
                    return slice;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Keeps a window. One that starts just after another ends continues it, and the two are kept as one,
    /// as old as the older: a page that spans both is answered from one window.
    /// </summary>
    private void Keep(Shelf shelf, SortSpec order, Window window)
    {
        lock (shelf.Gate)
        {
            shelf.Windows.RemoveAll(w => Expired(w) && Forget(w));
            if (window.From is not null && !window.Inclusive)
            {
                var before = shelf.Windows.FindIndex(w => w.Full && w.Rows.Length > 0 && order.Compare(w.Rows[^1], window.From) == 0);
                if (before >= 0)
                {
                    var joined = shelf.Windows[before];
                    Forget(joined);
                    shelf.Windows.RemoveAt(before);
                    window = new Window(
                        joined.From, joined.Inclusive, [.. joined.Rows, .. window.Rows], window.Full, Math.Min(joined.ReadAt, window.ReadAt));
                }
            }

            shelf.Windows.Add(window);
            Interlocked.Add(ref _keptRows, window.Rows.Length);
            while (shelf.Windows.Count > WindowsPerSort)
            {
                Forget(shelf.Windows[0]);
                shelf.Windows.RemoveAt(0);
            }
        }

        if (Interlocked.Read(ref _keptRows) > MaxKeptRows)
        {
            Trim();
        }
    }

    /// <summary>Lets the expired windows go, then the oldest, until the kept rows fit <see cref="MaxKeptRows"/>.</summary>
    private void Trim()
    {
        foreach (var shelf in _windows.Values)
        {
            lock (shelf.Gate)
            {
                shelf.Windows.RemoveAll(w => Expired(w) && Forget(w));
            }
        }

        while (Interlocked.Read(ref _keptRows) > MaxKeptRows)
        {
            var kept = new List<(Shelf Shelf, Window Window)>();
            foreach (var shelf in _windows.Values)
            {
                lock (shelf.Gate)
                {
                    kept.AddRange(shelf.Windows.Select(w => (shelf, w)));
                }
            }

            var oldest = kept.OrderBy(x => x.Window.ReadAt).FirstOrDefault();
            if (oldest.Shelf is null)
            {
                return;
            }

            lock (oldest.Shelf.Gate)
            {
                if (oldest.Shelf.Windows.Remove(oldest.Window))
                {
                    Forget(oldest.Window);
                }
            }
        }
    }

    private bool Forget(Window window)
    {
        Interlocked.Add(ref _keptRows, -window.Rows.Length);
        return true;
    }

    private bool Expired(Window window) => Stopwatch.GetElapsedTime(window.ReadAt) > WindowFor;

    /// <summary>
    /// The first <paramref name="limit"/> rows of <paramref name="window"/> from <paramref name="key"/> on,
    /// and the last of them; or all it has from there, with no last row, when it reached the end; or null
    /// when it does not cover the read: it starts after the key, or runs out before the limit.
    /// </summary>
    private static (IRow[] Read, IRow? Last)? Slice(Window window, SortSpec order, IRow? key, bool inclusive, int limit)
    {
        int from;
        if (key is null)
        {
            if (window.From is not null)
            {
                return null;
            }

            from = 0;
        }
        else
        {
            if (window.From is not null && order.Compare(key, window.From) is var start
                && (start < 0 || (start == 0 && inclusive && !window.Inclusive)))
            {
                return null;
            }

            if (window.Full && window.Rows.Length > 0 && order.Compare(key, window.Rows[^1]) > 0)
            {
                return null;
            }

            from = First(window.Rows, order, key, inclusive);
        }

        var available = window.Rows.Length - from;
        if (available >= limit)
        {
            var read = window.Rows[from..(from + limit)];
            return (read, read[^1]);
        }

        return window.Full ? null : (window.Rows[from..], null);
    }

    /// <summary>Whether <paramref name="row"/> falls among the rows <paramref name="window"/> read.</summary>
    private static bool Contains(Window window, SortSpec order, IRow row) =>
        window.Rows.Length > 0
        && (window.From is null || order.Compare(row, window.From) is var start && (start > 0 || (start == 0 && window.Inclusive)))
        && order.Compare(row, window.Rows[^1]) <= 0;

    /// <summary>Where the first row at or after <paramref name="key"/> (or after it) is in <paramref name="rows"/>.</summary>
    private static int First(IRow[] rows, SortSpec order, IRow key, bool inclusive)
    {
        int low = 0, high = rows.Length;
        while (low < high)
        {
            var middle = (low + high) >>> 1;
            var compared = order.Compare(rows[middle], key);
            if (compared < 0 || (compared == 0 && !inclusive))
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

    /// <summary>
    /// The database's rows in one order from one key (<see cref="From"/>, or the first row when null),
    /// as they were when read; <see cref="Full"/> when the read stopped at its limit rather than at the end.
    /// </summary>
    private sealed record Window(IRow? From, bool Inclusive, IRow[] Rows, bool Full, long ReadAt);

    /// <summary>The windows kept for one tab, filter and sort, and what reading it has shown.</summary>
    private sealed class Shelf
    {
        public readonly Lock Gate = new();
        public readonly List<Window> Windows = [];
        public volatile bool Slow;
        public int Reading;
    }
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
    private long _lastStart = -1;

    // Rows positions have moved down by, in all: a read that began before a shift places its rows by it.
    private long _shifted;

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
        long shifted;
        lock (_gate)
        {
            if (!_held && state.Epoch != _epoch)
            {
                _anchors.Clear();
            }

            _epoch = state.Epoch;
            below = Floor(start);
            above = Ceiling(start);
            shifted = _shifted;
        }

        IRow[] window;
        long first;
        if (below is null && above is null && start <= SeekWithin)
        {
            // Nothing known yet: counted from the top.
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
            // At the top, positions count from the first row. Held, they do not: the first row now may
            // have arrived since, and the rows above the first position known are read backwards from it.
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

        Record(first, window, shifted);
        var skip = (int)Math.Clamp(start - first, 0, window.Length);
        var rows = window.AsSpan(skip, Math.Min(count, window.Length - skip)).ToArray();

        // The next window in the direction the reader is scrolling, read while they look at this one.
        var down = start >= Interlocked.Exchange(ref _lastStart, start);
        if (rows.Length > 0 && (down || start > 0))
        {
            pages.ReadAhead(state, down ? order : order.Reversed(), down ? rows[^1] : rows[0], count);
        }

        return new Page(first + skip, rows, total, state);
    }

    /// <summary>
    /// The topmost position known and its row: what arrived above the view is what sorts before it,
    /// counted from here rather than from a row further down, between which and the top rows the stream
    /// sent late may have landed.
    /// </summary>
    public (long At, IRow Row)? First
    {
        get
        {
            lock (_gate)
            {
                return _anchors.Count == 0 ? null : (_anchors.Keys[0], _anchors.Values[0]);
            }
        }
    }

    /// <summary>
    /// Moves every position known down by <paramref name="rows"/>: room for rows that arrived above,
    /// which reads backwards from the first position known then find.
    /// </summary>
    public void Shift(long rows)
    {
        lock (_gate)
        {
            var moved = _anchors.Select(a => (a.Key + rows, a.Value)).ToList();
            _anchors.Clear();
            foreach (var (at, row) in moved)
            {
                _anchors[at] = row;
            }

            _lastStart += rows;
            _shifted += rows;
        }
    }

    /// <summary>
    /// Holds positions from <paramref name="at"/>, the place of <paramref name="row"/>: a view opened at a
    /// row, from a link, rather than at the top. The rows above it are read backwards from it.
    /// </summary>
    public void HoldAt(long at, IRow row)
    {
        lock (_gate)
        {
            _held = true;
            _anchors.Clear();
            _anchors[at] = row;
        }
    }

    /// <summary>Forgets every position known, as a new sort or filter does.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _anchors.Clear();
        }
    }

    /// <summary>
    /// Keeps positions for the rows a read found from <paramref name="first"/> on, in the places they had
    /// when it began; moved by any shift since, which the read did not see.
    /// </summary>
    private void Record(long first, IRow[] window, long shifted)
    {
        if (window.Length == 0)
        {
            return;
        }

        lock (_gate)
        {
            first += _shifted - shifted;
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

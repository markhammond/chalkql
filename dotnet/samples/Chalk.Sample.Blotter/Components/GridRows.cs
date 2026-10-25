using Chalk.Sample.Blotter.Live;

namespace Chalk.Sample.Blotter.Components;

/// <summary>
///     The rows one grid shows, worked out from a scope's state, with no Blazor in it: the rows at the
///     grid's positions, read from the database and the tail; the fills of opened orders nested under them;
///     flashes and reconciliation lags on each row; how far the rows run; and, while the grid is held away
///     from the live top, what has arrived above it and how much of that has been let in.
/// </summary>
/// <remarks>
///     The component asks it for rows by position (<see cref="ReadAsync" />) and tells it what the reader
///     did: scrolled away (<see cref="HoldAsync" />), came back to the top (<see cref="Release" />), scrolled
///     up near the top (<see cref="Reveal" />), opened an order (<see cref="Toggle" />), or chose a new sort or
///     filter (<see cref="Reset" />).
/// </remarks>
public sealed class GridRows(BlotterHost host, Tab tab, SortSpec order) : IDisposable
{
    /// <summary>A row's height, in pixels: the grid's positions are its scroll offset over it.</summary>
    public const double RowHeight = 30;

    // How much further the rows run when a read finds more past the end the grid has: a screenful.
    private const int GrowBy = 60;
    private readonly Flashes _fillFlashes = new(Tabs.Fills.Columns);

    private readonly Flashes _flashes = new(tab.Columns);

    // The orders opened to show their fills, by id, each with the version last seen; and where a read
    // last found each, which is where its fills are let in.
    private readonly Dictionary<Guid, Order> _open = [];
    private readonly Dictionary<Guid, long> _openAt = [];

    // Counts, from the streams, what arrives above the held rows; none while the grid follows the top.
    private ArrivalsAbove? _arrivals;
    private bool _counted;

    // How far the rows really end from where the database's estimate or count puts the end: learned
    // from reads that reach the end. A count that replaces the estimate starts it again.
    private long _endLearned;

    // Arrived above and not let in yet; let in and perhaps not scrolled up to yet; and let in in all,
    // which moves every position the grid holds.
    private long _hidden;
    private IReadOnlyList<Opened> _opened = [];

    private PageView _pages = host.Pages.View(order);
    private long _revealed;
    private long _shifted;

    /// <summary>Whether the grid is held away from the live top, its positions fixed.</summary>
    public bool Held => _pages.Held;

    /// <summary>Whether rows have been let in above that the reader may not have scrolled up to yet.</summary>
    public bool HasRevealed => _revealed > 0;

    /// <summary>Whether a row shown is still waiting for the database, so that its lag is worth showing again in a second.</summary>
    public bool Counting { get; private set; }

    public void Dispose() => StopCounting();

    public bool IsOpen(Guid orderId) => _open.ContainsKey(orderId);

    /// <summary>Opens an order's fills under it, or closes them.</summary>
    public void Toggle(Order order)
    {
        if (!_open.Remove(order.OrderId))
            _open[order.OrderId] = order;
        else
            _openAt.Remove(order.OrderId);
    }

    /// <summary>What arrived above the rows in view: not let in yet, or let in and not yet scrolled up to.</summary>
    public long NewAbove(double scrollTop) => _hidden + Math.Min(_revealed, (long)(scrollTop / RowHeight));

    /// <summary>Whether, scrolled up to <paramref name="scrollTop" />, there are rows to let in.</summary>
    public bool CanReveal(double scrollTop, int near) => _pages.Held && _hidden > 0 && scrollTop / RowHeight < near;

    /// <summary>
    ///     The row of the tab at the top of the view, scrolled to <paramref name="scrollTop" />, read afresh; null at the
    ///     end.
    /// </summary>
    public async Task<IRow?> RowAtAsync(ScopeState state, double scrollTop, CancellationToken ct)
    {
        var slot = Nesting.Locate((long)Math.Ceiling(scrollTop / RowHeight), _opened);
        var page = await _pages.GetAsync(state, slot.Position, 1, ct);
        return page.Rows.FirstOrDefault();
    }

    /// <summary>
    ///     Holds the grid where it is, scrolled away from the top: positions hold from here on, the row in
    ///     view is read so the view's place is held by its key, and what arrives above is counted.
    /// </summary>
    public async Task HoldAsync(ScopeState state, double scrollTop, Func<ScopeState> current)
    {
        _pages.Held = true;
        Count(current);
        var slot = Nesting.Locate((long)(scrollTop / RowHeight), _opened);
        await _pages.GetAsync(state, slot.Position, 1, CancellationToken.None);
    }

    /// <summary>Holds the grid at a row a link names, at its rank: as if the reader had scrolled there.</summary>
    public void HoldAt(long rank, IRow row, Func<ScopeState> current)
    {
        _pages.HoldAt(rank, row);
        Count(current);
    }

    /// <summary>Back at the live top: what arrived above is shown, and new rows push down again.</summary>
    public void Release()
    {
        _pages.Held = false;
        StopCounting();
        _hidden = 0;
        _revealed = 0;
    }

    /// <summary>
    ///     Lets in up to <paramref name="most" /> of the rows that arrived above: every position the grid holds
    ///     moves down by as many, so the rows in view stay where they are. Returns how many were let in.
    /// </summary>
    public long Reveal(int most)
    {
        var rows = Math.Min(_hidden, most);
        _pages.Shift(rows);
        foreach (var id in _openAt.Keys.ToList()) _openAt[id] += rows;

        _hidden -= rows;
        _revealed += rows;
        _shifted += rows;
        return rows;
    }

    /// <summary>A new sort or filter: nothing read, held, opened at a place or flashing carries over.</summary>
    public void Reset(SortSpec sort)
    {
        _pages = host.Pages.View(sort);
        _pages.Reset();
        _pages.Held = false;
        _openAt.Clear();
        StopCounting();
        _endLearned = 0;
        _hidden = 0;
        _revealed = 0;
        _flashes.Clear();
        _fillFlashes.Clear();
    }

    /// <summary>
    ///     The rows at positions <paramref name="start" /> on, <paramref name="count" /> of them, under
    ///     <paramref name="state" />; how many rows the grid has in all, opened orders' fills counted; and how
    ///     many have arrived above and not been let in.
    /// </summary>
    /// <exception cref="OperationCanceledException">
    ///     Rows were let in above while this was read: its rows are placed by
    ///     positions the grid no longer has.
    /// </exception>
    public async Task<(List<BlotterRow> Rows, int Total, long Hidden)> ReadAsync(ScopeState state, int start, int count,
        CancellationToken ct)
    {
        var shifted = _shifted;

        // What has arrived above the held rows, from the streams: the rows that crossed the topmost row
        // the view knows since the last state, up or down.
        if (_arrivals is not null && _pages.First is { } top)
            _hidden = Math.Max(0, _hidden + await _arrivals.TakeAsync(top.Row, ct));

        var hidden = _hidden;
        var counted = host.Pages.IsCounted(state);
        if (counted != _counted)
        {
            // A count has replaced the estimate: what was learned against the estimate no longer holds.
            _counted = counted;
            _endLearned = 0;
        }

        // Following the live top, the estimate sizes the scroll bar well enough: rows are counted only once
        // the reader scrolls away.
        var estimated = Math.Max(0, await host.Pages.TotalAsync(state, ct, _pages.Held) - hidden);
        var rows = Math.Max(0, estimated + _endLearned);

        // The opened orders are let in where a read last found them. A read that finds one somewhere else,
        // or finds another row where it was, moves it, and the grid's rows are worked out again.
        IReadOnlyList<Opened> opened = [];
        List<Slot> slots = [];
        Page? page = null;
        var asked = 0;
        for (var pass = 0; pass < 3; pass++)
        {
            opened = await OpenedAsync(state, rows, ct);
            slots = Nesting.Map(start, count, rows, opened);
            var positions = slots.Where(s => !s.IsFill).Select(s => s.Position).ToList();

            // At the end the grid has found, a row more is asked for: if there is one, the rows go on.
            asked = positions.Count == 0 ? 0 : (int)(positions[^1] - positions[0] + 1);
            var atEnd = positions.Count > 0 && positions[^1] >= rows - 1;
            page = positions.Count == 0
                ? null
                : await _pages.GetAsync(state, positions[0], asked + (atEnd ? 1 : 0), ct);
            if (!Learn(page, shifted)) break;
        }

        // Where the rows end, learned from the read: one short of what it asked for found the last row; one
        // that found more at the end the grid had goes on a screenful further.
        if (page is not null && asked > 0)
        {
            var reached = page.Start + page.Rows.Length;
            if (page.Rows.Length < asked)
            {
                _endLearned = reached - estimated;
                rows = reached;
            }
            else if (page.Rows.Length > asked)
            {
                _endLearned += GrowBy;
                rows += GrowBy;
            }
        }

        if (shifted != _shifted) throw new OperationCanceledException(ct);

        var now = DateTime.UtcNow;
        var shown = new List<BlotterRow>(slots.Count);
        foreach (var slot in slots)
            if (slot.Under is { } under)
                shown.Add(_fillFlashes.Decorate(under.Fills[slot.Fill], now, under.Order.OrderId));
            else if (page is not null && slot.Position - page.Start is var at && at >= 0 && at < page.Rows.Length)
                shown.Add(_flashes.Decorate(page.Rows[at], now));

        foreach (var row in shown)
            row.Reconciliation = state.Reconciling.TryGetValue(row.Row.Id, out var reconciliation)
                ? reconciliation
                : null;

        Counting = shown.Any(r => r.Reconciliation is { Reconciled: false });
        _flashes.Seen(state.IngestedThrough, now);
        _fillFlashes.Seen(state.IngestedThrough, now);
        _opened = opened;
        return (shown, (int)Math.Min(int.MaxValue, Nesting.Total(rows, opened)), hidden);
    }

    private void Count(Func<ScopeState> current)
    {
        _arrivals?.Dispose();
        _arrivals = new ArrivalsAbove(host, _pages.Order, current);
    }

    private void StopCounting()
    {
        _arrivals?.Dispose();
        _arrivals = null;
    }

    /// <summary>The opened orders whose place is known, in order of position, each with its fills now.</summary>
    private async Task<IReadOnlyList<Opened>> OpenedAsync(ScopeState state, long rows, CancellationToken ct)
    {
        var opened = new List<Opened>();
        foreach (var (id, position) in _openAt.ToList())
            if (position < rows && _open.TryGetValue(id, out var order))
                opened.Add(new Opened(position, order, await host.Pages.FillsOfAsync(state, id, ct)));

        return [.. opened.OrderBy(o => o.Position)];
    }

    /// <summary>What a read says of the opened orders: where each it holds is, and which are no longer where they were.</summary>
    private bool Learn(Page? page, long shifted)
    {
        if (page is null || shifted != _shifted)
            // Rows were let in above while it was read: its positions are no longer the grid's.
            return false;

        var changed = false;
        for (var i = 0; i < page.Rows.Length; i++)
            if (page.Rows[i] is Order order && _open.ContainsKey(order.OrderId))
            {
                _open[order.OrderId] = order;
                if (!_openAt.TryGetValue(order.OrderId, out var was) || was != page.Start + i)
                {
                    _openAt[order.OrderId] = page.Start + i;
                    changed = true;
                }
            }

        foreach (var (id, position) in _openAt.ToList())
        {
            var at = position - page.Start;
            if (at >= 0 && at < page.Rows.Length && page.Rows[at].Id != id)
            {
                _openAt.Remove(id);
                changed = true;
            }
        }

        return changed;
    }
}
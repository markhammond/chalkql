using System.Collections.Immutable;
using Chalk.Client;
using Chalk.Sources.Poco;

namespace Chalk.Sample.Blotter.Live;

/// <summary>How long a row has waited for the database, as <c>live.waits</c> holds it: until it was held, or for good.</summary>
public sealed record Wait(Guid Id, DateTime Since, DateTime Until);

/// <summary>
///     The tables alert views read, and the views. Kept only while some browser has an alert, and
///     changed only by the host's catch-up loop, in the same refresh as the tail, so a view sees the
///     streams, the tail and the waits at one epoch.
/// </summary>
/// <remarks>
///     <list type="bullet">
///         <item><c>live.arrived_fills</c>: every fill the streams sent in the last <see cref="FillsFor" />.</item>
///         <item>
///             <c>live.latest_orders</c>: every order the streams sent, at the newest version they sent, until it
///             has not changed for <see cref="OrdersFor" />. An order rule's view keeps an order while it passes,
///             so an order that leaves this table and comes back may fire its rules again.
///         </item>
///         <item><c>live.waits</c>: every row in the tail, and since when it has waited for the database.</item>
///         <item>
///             <c>live.match_counts</c>: how many matches each alert view a rule counts saw, by slice of
///             time, for as long as the longest window that reads them.
///         </item>
///     </list>
/// </remarks>
public sealed class AlertTables(
    PocoTable<Fill> arrivedFills,
    PocoTable<Order> latestOrders,
    PocoTable<Wait> waits,
    PocoTable<MatchCount> matchCounts)
    : IAsyncDisposable
{
    /// <summary>How long a fill stays in <c>live.arrived_fills</c>: long enough for its order to arrive.</summary>
    public static readonly TimeSpan FillsFor = TimeSpan.FromSeconds(10);

    /// <summary>How long an order that has not changed stays in <c>live.latest_orders</c>.</summary>
    public static readonly TimeSpan OrdersFor = TimeSpan.FromMinutes(10);

    // Until a lookup finds it, a wait has no end.
    private static readonly DateTime Never = new(9999, 12, 31, 0, 0, 0, DateTimeKind.Unspecified);
    private readonly Dictionary<(string Matcher, TimeSpan Slice), Counter> _counters = [];
    private readonly Dictionary<string, CountView> _counts = new(StringComparer.Ordinal);
    private readonly List<MatchCount> _countsGone = [];
    private readonly Dictionary<Guid, Fill> _fills = [];
    private readonly Queue<(DateTime At, List<Fill> Fills)> _fillsKept = new();
    private readonly List<Fill> _newFills = [];
    private readonly Dictionary<Guid, Order> _newOrders = [];
    private readonly Dictionary<Guid, (Order Order, DateTime At)> _orders = [];

    private readonly Dictionary<string, AlertView> _views = new(StringComparer.Ordinal);
    private int _lastCounter;
    private DateTime _ordersSwept = DateTime.UtcNow;
    private ImmutableDictionary<Guid, Reconciliation> _waitsShown = ImmutableDictionary<Guid, Reconciliation>.Empty;

    /// <summary>How many alert views are open, count views among them.</summary>
    public int Count => _views.Count + _counts.Count;

    /// <summary>Whether any alert view is open, and so whether the tables are kept.</summary>
    public bool Active => _views.Count > 0;

    /// <summary>Whether a view reads the clock, so that the host advances it even while nothing arrives.</summary>
    public bool WatchesTime => _counts.Count > 0 || _views.Values.Any(v => v.Spec.LagOver is not null);

    /// <summary>Whether the streams sent something, or the views counted something, the next refresh must add.</summary>
    public bool Pending => _newFills.Count > 0 || _newOrders.Count > 0 || _countsGone.Count > 0 ||
                           _counters.Values.Any(c => c.Changed.Count > 0);

    /// <summary>The views' state and the tables' rows, in bytes, roughly.</summary>
    public long Bytes =>
        _views.Values.Sum(v => v.Bytes) + _counts.Values.Sum(v => v.Bytes)
                                        + 2L * (_fills.Count * Fill.Bytes + _orders.Count * Order.Bytes) +
                                        2L * 64 * (_waitsShown.Count + _counters.Values.Sum(c => c.Slices.Count));

    /// <summary>The views no condition could be made into, and what ChalkQL said, by the view's name.</summary>
    public ImmutableDictionary<string, string> Refused { get; private set; } =
        ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal);

    public async ValueTask DisposeAsync()
    {
        foreach (var view in _counts.Values) await view.DisposeAsync().ConfigureAwait(false);

        foreach (var view in _views.Values) await view.DisposeAsync().ConfigureAwait(false);

        _counts.Clear();
        _views.Clear();
    }

    /// <summary>
    ///     Raised on the catch-up loop as rows enter a view: the view, and each row with how long it had waited, for a
    ///     wait.
    /// </summary>
    public event Action<AlertViewSpec, IReadOnlyList<(IRow Row, TimeSpan? Lag)>>? Entered;

    /// <summary>Raised on the catch-up loop as a count view's row enters it: the view, and the matches in its window.</summary>
    public event Action<AlertWindowSpec, long>? Counted;

    /// <summary>What the streams sent, for the next refresh.</summary>
    public void Note(IReadOnlyList<Order> versions, IReadOnlyList<Fill> fills)
    {
        if (!Active) return;

        _newFills.AddRange(fills);
        foreach (var version in versions)
        {
            var newest = _newOrders.TryGetValue(version.OrderId, out var pending) ? pending
                : _orders.TryGetValue(version.OrderId, out var kept) ? kept.Order : null;
            if (newest is null || version.Version > newest.Version) _newOrders[version.OrderId] = version;
        }
    }

    /// <summary>Adds to a refresh what the tables gain and lose: what arrived, what is old enough to go, and the tail's waits.</summary>
    public void AddTo(RefreshBuilder refresh, Tail tail, DateTime now)
    {
        var goneFills = new List<Fill>();
        var goneOrders = new List<Order>();
        var keepFrom = Active ? now - FillsFor : DateTime.MaxValue;
        while (_fillsKept.TryPeek(out var oldest) && oldest.At < keepFrom)
            foreach (var fill in _fillsKept.Dequeue().Fills.Where(f => _fills.Remove(f.FillId)))
                goneFills.Add(fill);

        if (!Active || now - _ordersSwept > TimeSpan.FromSeconds(1))
        {
            _ordersSwept = now;
            foreach (var (id, (order, at)) in _orders.Where(o => !Active || now - o.Value.At > OrdersFor).ToList())
            {
                _orders.Remove(id);
                goneOrders.Add(order);
            }
        }

        var newFills = _newFills.Where(f => _fills.TryAdd(f.FillId, f)).ToList();
        if (newFills.Count > 0) _fillsKept.Enqueue((now, newFills));

        var newOrders = _newOrders.Values.ToList();
        foreach (var order in newOrders) _orders[order.OrderId] = (order, now);

        _newFills.Clear();
        _newOrders.Clear();

        // The waits: what changed in the tail's since the last refresh.
        var current = Active ? tail.Reconciling : ImmutableDictionary<Guid, Reconciliation>.Empty;
        var waitsSet = new List<Wait>();
        var waitsGone = new List<Wait>();
        if (!ReferenceEquals(current, _waitsShown))
        {
            foreach (var (id, wait) in current)
                if (!_waitsShown.TryGetValue(id, out var shown) || shown.Since != wait.Since || shown.At != wait.At)
                    waitsSet.Add(Of(id, wait));

            foreach (var (id, shown) in _waitsShown)
                if (!current.ContainsKey(id))
                    waitsGone.Add(Of(id, shown));

            _waitsShown = current;
        }

        if (goneFills.Count > 0) refresh.Delete(arrivedFills, goneFills);

        if (newFills.Count > 0) refresh.Upsert(arrivedFills, newFills);

        if (goneOrders.Count > 0) refresh.Delete(latestOrders, goneOrders);

        if (newOrders.Count > 0) refresh.Upsert(latestOrders, newOrders);

        if (waitsGone.Count > 0) refresh.Delete(waits, waitsGone);

        if (waitsSet.Count > 0) refresh.Upsert(waits, waitsSet);

        // The counts: what the views counted since the last refresh, and the slices no window reads any more.
        var countsSet = new List<MatchCount>();
        var countsGone = new List<MatchCount>(_countsGone);
        _countsGone.Clear();
        foreach (var counter in _counters.Values)
        {
            countsSet.AddRange(
                counter.Changed.Select(slice => new MatchCount(counter.Id, slice, counter.Slices[slice])));
            counter.Changed.Clear();
            foreach (var slice in counter.Slices.Keys.Where(slice => slice < now - counter.KeepFor).ToList())
            {
                countsGone.Add(new MatchCount(counter.Id, slice, counter.Slices[slice]));
                counter.Slices.Remove(slice);
            }
        }

        if (countsGone.Count > 0) refresh.Delete(matchCounts, countsGone);

        if (countsSet.Count > 0) refresh.Upsert(matchCounts, countsSet);
    }

    /// <summary>
    ///     Brings every view to the latest epoch, and says what entered each. The matches the alert views
    ///     saw are counted, for the next refresh to add; the count views read those the last one added.
    /// </summary>
    public async Task UpdateAsync(Tail tail, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        foreach (var view in _views.Values.ToList())
        {
            AlertViewUpdate update;
            try
            {
                update = await view.UpdateAsync(ct).ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                await CloseAsync(view.Spec.Name, failure.Message).ConfigureAwait(false);
                continue;
            }

            if (update.Matches > 0)
                foreach (var counter in _counters.Values.Where(c => c.Matcher == view.Spec.Name))
                    counter.Add(now, update.Matches);

            var entered = update.Entered;
            if (entered.Count == 0) continue;

            var rows = new List<(IRow, TimeSpan?)>(entered.Count);
            foreach (var key in entered)
                if (Resolve(view.Spec, key.Id, tail) is { } row)
                    rows.Add((row,
                        view.Spec.LagOver is null ? null : tail.Reconciling.GetValueOrDefault(key.Id).Lag(now)));

            Entered?.Invoke(view.Spec, rows);
        }

        foreach (var view in _counts.Values.ToList())
        {
            long? matches;
            try
            {
                matches = await view.UpdateAsync(ct).ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                await CloseCountAsync(view.Spec.Name, failure.Message).ConfigureAwait(false);
                continue;
            }

            if (matches is { } inWindow) Counted?.Invoke(view.Spec, inWindow);
        }
    }

    /// <summary>
    ///     Opens a view for each of <paramref name="specs" /> and <paramref name="windows" /> not open yet, and
    ///     closes those no rule watches any more. A count view needs its alert view, and a counter for its
    ///     width of slice, which begins counting now.
    /// </summary>
    public async Task WatchAsync(
        IReadOnlyCollection<AlertViewSpec> specs, IReadOnlyCollection<AlertWindowSpec> windows, ChalkEngine engine,
        long budget, CancellationToken ct)
    {
        var wanted = specs.Concat(windows.Select(w => w.Matcher))
            .GroupBy(s => s.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var wantedCounts = windows.GroupBy(w => w.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var name in _counts.Keys.Where(n => !wantedCounts.ContainsKey(n)).ToList())
            await CloseCountAsync(name, null).ConfigureAwait(false);

        foreach (var name in _views.Keys.Where(n => !wanted.ContainsKey(n)).ToList())
            await CloseAsync(name, null).ConfigureAwait(false);

        // Counters no window reads go, their slices with them; each kept is kept for its longest window.
        foreach (var (key, counter) in _counters
                     .Where(c => !wantedCounts.Values.Any(w => (w.Matcher.Name, w.Slice) == c.Key)).ToList())
        {
            _counters.Remove(key);
            _countsGone.AddRange(counter.Slices.Select(s => new MatchCount(counter.Id, s.Key, s.Value)));
        }

        Refused = Refused.RemoveRange(Refused.Keys.Where(n => !wanted.ContainsKey(n) && !wantedCounts.ContainsKey(n)));
        foreach (var (name, spec) in wanted.Where(w => !_views.ContainsKey(w.Key) && !Refused.ContainsKey(w.Key)))
            try
            {
                _views[name] = await AlertView.CreateAsync(engine, spec, budget, ct).ConfigureAwait(false);
            }
            catch (Exception refused) when (refused is not OperationCanceledException)
            {
                Refused = Refused.SetItem(name, refused.Message);
            }

        var now = DateTime.UtcNow;
        foreach (var (name, window) in wantedCounts.Where(w =>
                     !_counts.ContainsKey(w.Key) && !Refused.ContainsKey(w.Key)))
        {
            if (Refused.GetValueOrDefault(window.Matcher.Name) is { } matcherRefused)
            {
                Refused = Refused.SetItem(name, matcherRefused);
                continue;
            }

            if (!_counters.TryGetValue((window.Matcher.Name, window.Slice), out var counter))
            {
                counter = new Counter(++_lastCounter, window.Matcher.Name, window.Slice, now);
                _counters[(window.Matcher.Name, window.Slice)] = counter;
            }

            counter.KeepFor =
                wantedCounts.Values.Where(w => (w.Matcher.Name, w.Slice) == (counter.Matcher, counter.Slice))
                    .Max(w => w.Count.Window) + counter.Slice;
            try
            {
                _counts[name] = await CountView.CreateAsync(
                        engine, window, counter.Id, Cells.Micros(counter.Began + window.Count.Window), budget, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception refused) when (refused is not OperationCanceledException)
            {
                Refused = Refused.SetItem(name, refused.Message);
            }
        }
    }

    private async Task CloseCountAsync(string name, string? why)
    {
        if (_counts.Remove(name, out var view)) await view.DisposeAsync().ConfigureAwait(false);

        if (why is not null) Refused = Refused.SetItem(name, why);
    }

    private async Task CloseAsync(string name, string? why)
    {
        if (_views.Remove(name, out var view)) await view.DisposeAsync().ConfigureAwait(false);

        if (why is not null) Refused = Refused.SetItem(name, why);
    }

    private IRow? Resolve(AlertViewSpec spec, Guid id, Tail tail)
    {
        return (spec.Condition.Tab.Overrides, spec.LagOver is null) switch
        {
            (true, true) => _orders.TryGetValue(id, out var kept) ? kept.Order : null,
            (true, false) => tail.Get(id)?.Live,
            (false, true) => _fills.GetValueOrDefault(id),
            (false, false) => tail.GetFill(id)
        };
    }

    private static Wait Of(Guid id, Reconciliation wait)
    {
        return new Wait(id, Cells.Micros(wait.Since), wait.At is { } at ? Cells.Micros(at) : Never);
    }

    /// <summary>
    ///     The matches one alert view saw, by slice of one width, since it began: what count views of
    ///     that view and width read, through <c>live.match_counts</c>.
    /// </summary>
    private sealed class Counter(int id, string matcher, TimeSpan slice, DateTime began)
    {
        public int Id => id;

        public string Matcher => matcher;

        public TimeSpan Slice => slice;

        public DateTime Began => began;

        /// <summary>How long a slice is kept: the longest window that reads it, and a slice more.</summary>
        public TimeSpan KeepFor { get; set; }

        /// <summary>The matches in each slice, by when it starts, in the tables' wall-clock time.</summary>
        public Dictionary<DateTime, long> Slices { get; } = [];

        /// <summary>The slices counted since the last refresh.</summary>
        public HashSet<DateTime> Changed { get; } = [];

        public void Add(DateTime at, int matches)
        {
            var start = Cells.Micros(new DateTime(at.Ticks - at.Ticks % slice.Ticks, DateTimeKind.Utc));
            Slices[start] = Slices.GetValueOrDefault(start) + matches;
            Changed.Add(start);
        }
    }
}
using System.Collections.Immutable;
using Chalk.Client;
using Chalk.Sample.Blotter.Domain;
using Chalk.Sample.Blotter.Grid;
using Chalk.Sources.Poco;

namespace Chalk.Sample.Blotter.Live;

/// <summary>How long a row has waited for the database, as <c>live.waits</c> holds it: until it was held, or for good.</summary>
public sealed record Wait(Guid Id, DateTime Since, DateTime Until);

/// <summary>
/// The tables alert views read, and the views. Kept only while some browser has an alert, and
/// changed only by the host's catch-up loop, in the same refresh as the tail, so a view sees the
/// streams, the tail and the waits at one epoch.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>live.arrived_fills</c>: every fill the streams sent in the last <see cref="FillsFor"/>.</item>
/// <item><c>live.latest_orders</c>: every order the streams sent, at the newest version they sent, until it
/// has not changed for <see cref="OrdersFor"/>. An order rule's view keeps an order while it passes,
/// so an order that leaves this table and comes back may fire its rules again.</item>
/// <item><c>live.waits</c>: every row in the tail, and since when it has waited for the database.</item>
/// </list>
/// </remarks>
public sealed class AlertTables(
    PocoTable<Fill> arrivedFills, PocoTable<Order> latestOrders, PocoTable<Wait> waits) : IAsyncDisposable
{
    /// <summary>How long a fill stays in <c>live.arrived_fills</c>: long enough for its order to arrive.</summary>
    public static readonly TimeSpan FillsFor = TimeSpan.FromSeconds(10);

    /// <summary>How long an order that has not changed stays in <c>live.latest_orders</c>.</summary>
    public static readonly TimeSpan OrdersFor = TimeSpan.FromMinutes(10);

    private readonly Dictionary<string, AlertView> _views = new(StringComparer.Ordinal);
    private readonly Queue<(DateTime At, List<Fill> Fills)> _fillsKept = new();
    private readonly Dictionary<Guid, Fill> _fills = [];
    private readonly Dictionary<Guid, (Order Order, DateTime At)> _orders = [];
    private readonly List<Fill> _newFills = [];
    private readonly Dictionary<Guid, Order> _newOrders = [];
    private ImmutableDictionary<Guid, Reconciliation> _waitsShown = ImmutableDictionary<Guid, Reconciliation>.Empty;
    private DateTime _ordersSwept = DateTime.UtcNow;

    /// <summary>How many alert views are open.</summary>
    public int Count => _views.Count;

    /// <summary>Whether any alert view is open, and so whether the tables are kept.</summary>
    public bool Active => _views.Count > 0;

    /// <summary>Whether a view reads the clock, so that the host advances it even while nothing arrives.</summary>
    public bool WatchesTime => _views.Values.Any(v => v.Spec.LagOver is not null);

    /// <summary>Whether the streams sent something the next refresh must add.</summary>
    public bool Pending => _newFills.Count > 0 || _newOrders.Count > 0;

    /// <summary>The views' state and the tables' rows, in bytes, roughly.</summary>
    public long Bytes =>
        _views.Values.Sum(v => v.Bytes) + 2L * (_fills.Count * Fill.Bytes + _orders.Count * Order.Bytes) + 2L * 64 * _waitsShown.Count;

    /// <summary>The views no condition could be made into, and what ChalkQL said, by the view's name.</summary>
    public ImmutableDictionary<string, string> Refused { get; private set; } = ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal);

    /// <summary>Raised on the catch-up loop as rows enter a view: the view, and each row with how long it had waited, for a wait.</summary>
    public event Action<AlertViewSpec, IReadOnlyList<(IRow Row, TimeSpan? Lag)>>? Entered;

    /// <summary>What the streams sent, for the next refresh.</summary>
    public void Note(IReadOnlyList<Order> versions, IReadOnlyList<Fill> fills)
    {
        if (!Active)
        {
            return;
        }

        _newFills.AddRange(fills);
        foreach (var version in versions)
        {
            var newest = _newOrders.TryGetValue(version.OrderId, out var pending) ? pending
                : _orders.TryGetValue(version.OrderId, out var kept) ? kept.Order : null;
            if (newest is null || version.Version > newest.Version)
            {
                _newOrders[version.OrderId] = version;
            }
        }
    }

    /// <summary>Adds to a refresh what the tables gain and lose: what arrived, what is old enough to go, and the tail's waits.</summary>
    public void AddTo(RefreshBuilder refresh, Tail tail, DateTime now)
    {
        var goneFills = new List<Fill>();
        var goneOrders = new List<Order>();
        var keepFrom = Active ? now - FillsFor : DateTime.MaxValue;
        while (_fillsKept.TryPeek(out var oldest) && oldest.At < keepFrom)
        {
            foreach (var fill in _fillsKept.Dequeue().Fills.Where(f => _fills.Remove(f.FillId)))
            {
                goneFills.Add(fill);
            }
        }

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
        if (newFills.Count > 0)
        {
            _fillsKept.Enqueue((now, newFills));
        }

        var newOrders = _newOrders.Values.ToList();
        foreach (var order in newOrders)
        {
            _orders[order.OrderId] = (order, now);
        }

        _newFills.Clear();
        _newOrders.Clear();

        // The waits: what changed in the tail's since the last refresh.
        var current = Active ? tail.Reconciling : ImmutableDictionary<Guid, Reconciliation>.Empty;
        var waitsSet = new List<Wait>();
        var waitsGone = new List<Wait>();
        if (!ReferenceEquals(current, _waitsShown))
        {
            foreach (var (id, wait) in current)
            {
                if (!_waitsShown.TryGetValue(id, out var shown) || shown.Since != wait.Since || shown.At != wait.At)
                {
                    waitsSet.Add(Of(id, wait));
                }
            }

            foreach (var (id, shown) in _waitsShown)
            {
                if (!current.ContainsKey(id))
                {
                    waitsGone.Add(Of(id, shown));
                }
            }

            _waitsShown = current;
        }

        if (goneFills.Count > 0)
        {
            refresh.Delete(arrivedFills, goneFills);
        }

        if (newFills.Count > 0)
        {
            refresh.Upsert(arrivedFills, newFills);
        }

        if (goneOrders.Count > 0)
        {
            refresh.Delete(latestOrders, goneOrders);
        }

        if (newOrders.Count > 0)
        {
            refresh.Upsert(latestOrders, newOrders);
        }

        if (waitsGone.Count > 0)
        {
            refresh.Delete(waits, waitsGone);
        }

        if (waitsSet.Count > 0)
        {
            refresh.Upsert(waits, waitsSet);
        }
    }

    /// <summary>Brings every view to the latest epoch, and says what entered each.</summary>
    public async Task UpdateAsync(Tail tail, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        foreach (var view in _views.Values.ToList())
        {
            IReadOnlyList<AlertKey> entered;
            try
            {
                entered = await view.UpdateAsync(ct).ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                await CloseAsync(view.Spec.Name, failure.Message).ConfigureAwait(false);
                continue;
            }

            if (entered.Count == 0)
            {
                continue;
            }

            var rows = new List<(IRow, TimeSpan?)>(entered.Count);
            foreach (var key in entered)
            {
                if (Resolve(view.Spec, key.Id, tail) is { } row)
                {
                    rows.Add((row, view.Spec.LagOver is null ? null : tail.Reconciling.GetValueOrDefault(key.Id).Lag(now)));
                }
            }

            Entered?.Invoke(view.Spec, rows);
        }
    }

    /// <summary>Opens a view for each of <paramref name="specs"/> not open yet, and closes those no rule watches any more.</summary>
    public async Task WatchAsync(IReadOnlyCollection<AlertViewSpec> specs, ChalkEngine engine, long budget, CancellationToken ct)
    {
        var wanted = specs.GroupBy(s => s.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var name in _views.Keys.Where(n => !wanted.ContainsKey(n)).ToList())
        {
            await CloseAsync(name, null).ConfigureAwait(false);
        }

        Refused = Refused.RemoveRange(Refused.Keys.Where(n => !wanted.ContainsKey(n)));
        foreach (var (name, spec) in wanted.Where(w => !_views.ContainsKey(w.Key) && !Refused.ContainsKey(w.Key)))
        {
            try
            {
                _views[name] = await AlertView.CreateAsync(engine, spec, budget, ct).ConfigureAwait(false);
            }
            catch (Exception refused) when (refused is not OperationCanceledException)
            {
                Refused = Refused.SetItem(name, refused.Message);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var view in _views.Values)
        {
            await view.DisposeAsync().ConfigureAwait(false);
        }

        _views.Clear();
    }

    private async Task CloseAsync(string name, string? why)
    {
        if (_views.Remove(name, out var view))
        {
            await view.DisposeAsync().ConfigureAwait(false);
        }

        if (why is not null)
        {
            Refused = Refused.SetItem(name, why);
        }
    }

    private IRow? Resolve(AlertViewSpec spec, Guid id, Tail tail) => (spec.Condition.Tab.Overrides, spec.LagOver is null) switch
    {
        (true, true) => _orders.TryGetValue(id, out var kept) ? kept.Order : null,
        (true, false) => tail.Get(id)?.Live,
        (false, true) => _fills.GetValueOrDefault(id),
        (false, false) => tail.GetFill(id),
    };

    // Until a lookup finds it, a wait has no end.
    private static readonly DateTime Never = new(9999, 12, 31, 0, 0, 0, DateTimeKind.Unspecified);

    private static Wait Of(Guid id, Reconciliation wait) =>
        new(id, Cells.Micros(wait.Since), wait.At is { } at ? Cells.Micros(at) : Never);
}

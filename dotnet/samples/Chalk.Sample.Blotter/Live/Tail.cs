using System.Collections.Immutable;

namespace Chalk.Sample.Blotter.Live;

/// <summary>
/// One order in the live tail: its latest version from the stream, beside the row the database last
/// showed for it in <c>orders_current</c>.
/// </summary>
/// <remarks>
/// Every column is here twice, the stream's version's and the database's (<c>base_</c>), so that a live
/// scope can test a filter against both in one statement: an order whose database row passes the
/// filter has a row in the database's pages that the tail now overrides, wherever its new version
/// lands. <see cref="BaseVersion"/> is zero when the database has no row for the order yet, and its other
/// columns then repeat the stream's.
/// </remarks>
public sealed record TailRow
{
    public required Guid OrderId { get; init; }
    public required int Version { get; init; }
    public required DateTime EventTime { get; init; }
    public required DateTime OrderTime { get; init; }
    public required string ClOrdId { get; init; }
    public required string Client { get; init; }
    public required string Region { get; init; }
    public required string Channel { get; init; }
    public required string Instrument { get; init; }
    public required string Side { get; init; }
    public required double Qty { get; init; }
    public required double FilledQty { get; init; }
    public required double AvgPrice { get; init; }
    public required double MarkupBps { get; init; }
    public required string Status { get; init; }

    public required Guid BaseOrderId { get; init; }
    public required int BaseVersion { get; init; }
    public required DateTime BaseEventTime { get; init; }
    public required DateTime BaseOrderTime { get; init; }
    public required string BaseClOrdId { get; init; }
    public required string BaseClient { get; init; }
    public required string BaseRegion { get; init; }
    public required string BaseChannel { get; init; }
    public required string BaseInstrument { get; init; }
    public required string BaseSide { get; init; }
    public required double BaseQty { get; init; }
    public required double BaseFilledQty { get; init; }
    public required double BaseAvgPrice { get; init; }
    public required double BaseMarkupBps { get; init; }
    public required string BaseStatus { get; init; }

    /// <summary>A rough size in memory, for the budget, counted twice: once here, once in ChalkQL's copy.</summary>
    public const int Bytes = 2 * 200;

    public static TailRow Of(Order live, Order? database)
    {
        var b = database ?? live;
        return new()
        {
            OrderId = live.OrderId, Version = live.Version, EventTime = live.EventTime, OrderTime = live.OrderTime,
            ClOrdId = live.ClOrdId, Client = live.Client, Region = live.Region, Channel = live.Channel,
            Instrument = live.Instrument, Side = live.Side, Qty = live.Qty, FilledQty = live.FilledQty,
            AvgPrice = live.AvgPrice, MarkupBps = live.MarkupBps, Status = live.Status,
            BaseOrderId = b.OrderId, BaseVersion = database?.Version ?? 0, BaseEventTime = b.EventTime,
            BaseOrderTime = b.OrderTime, BaseClOrdId = b.ClOrdId, BaseClient = b.Client, BaseRegion = b.Region,
            BaseChannel = b.Channel, BaseInstrument = b.Instrument, BaseSide = b.Side, BaseQty = b.Qty,
            BaseFilledQty = b.FilledQty, BaseAvgPrice = b.AvgPrice, BaseMarkupBps = b.MarkupBps, BaseStatus = b.Status,
        };
    }
}

/// <summary>One order in the tail.</summary>
public sealed class TailEntry(Order live, Order? database)
{
    /// <summary>The latest version the stream has sent.</summary>
    public Order Live { get; internal set; } = live;

    /// <summary>The order's row in <c>orders_current</c> when last looked up, or null when it had none.</summary>
    public Order? Base { get; internal set; } = database;

    /// <summary>The versions the stream has sent that are newer than <see cref="Base"/>, oldest first.</summary>
    public List<Order> Versions { get; } = [];

    /// <summary>When a lookup first found the database at <see cref="Live"/>'s version or later; null until then.</summary>
    public DateTime? ConfirmedAt { get; internal set; }
}

/// <summary>
/// The live tail: every order whose latest version from the stream the database has not been seen to
/// hold yet, and every fill the database has not been seen to hold. A row leaves once a lookup has
/// found it in the database and some time has passed since (<see cref="BlotterOptions.EvictAfter"/>).
/// Changed only by the host's ingest loop.
/// </summary>
public sealed class Tail
{
    private readonly Dictionary<Guid, TailEntry> _orders = [];
    private readonly Dictionary<Guid, Fill> _fills = [];
    private readonly Dictionary<Guid, DateTime> _fillsConfirmed = [];

    public int Count => _orders.Count;

    public int FillCount => _fills.Count;

    /// <summary>Versions held, the latest ones and those before them.</summary>
    public long VersionCount { get; private set; }

    /// <summary>
    /// Every order's latest version in the tail, whatever filter it passes: immutable, so a page that
    /// reads one state decides by it which of the database's rows the tail overrides.
    /// </summary>
    public ImmutableDictionary<Guid, Order> Latest { get; private set; } = ImmutableDictionary<Guid, Order>.Empty;

    /// <summary>The tail's fills by order, immutable for the same reason.</summary>
    public ImmutableDictionary<Guid, ImmutableList<Fill>> FillsByOrder { get; private set; } =
        ImmutableDictionary<Guid, ImmutableList<Fill>>.Empty;

    /// <summary>What the tail holds, in bytes: its rows here and in ChalkQL, its versions and its fills in both.</summary>
    public long Bytes =>
        _orders.Count * ((long)TailRow.Bytes + 64) + VersionCount * 2L * Order.Bytes + _fills.Count * 2L * (Fill.Bytes + 48);

    public TailEntry? Get(Guid orderId) => _orders.GetValueOrDefault(orderId);

    public Fill? GetFill(Guid fillId) => _fills.GetValueOrDefault(fillId);

    public IEnumerable<TailEntry> Entries => _orders.Values;

    public IEnumerable<Fill> Fills => _fills.Values;

    /// <summary>Orders and fills the database has been seen to hold, waiting to leave.</summary>
    public int Confirmed => _orders.Values.Count(e => e.ConfirmedAt is not null) + _fillsConfirmed.Count;

    /// <summary>When the oldest row the database has not been seen to hold was sent: how far behind it is, at least.</summary>
    public DateTime? OldestUnconfirmed
    {
        get
        {
            DateTime? oldest = null;
            foreach (var entry in _orders.Values)
            {
                if (entry.ConfirmedAt is null && (oldest is null || entry.Versions[0].EventTime < oldest))
                {
                    oldest = entry.Versions[0].EventTime;
                }
            }

            foreach (var fill in _fills.Values)
            {
                if (!_fillsConfirmed.ContainsKey(fill.FillId) && (oldest is null || fill.EventTime < oldest))
                {
                    oldest = fill.EventTime;
                }
            }

            return oldest;
        }
    }

    internal void Set(TailEntry entry, Order version)
    {
        _orders[entry.Live.OrderId] = entry;
        entry.Versions.Add(version);
        entry.ConfirmedAt = null;
        VersionCount++;
        Latest = Latest.SetItem(version.OrderId, version);
    }

    /// <summary>The database's row for an entry, newer than it was: the versions the database now holds leave, the latest stays.</summary>
    internal List<Order> Rebase(TailEntry entry, Order? database)
    {
        entry.Base = database;
        var gone = entry.Versions.Where(v => v.Version <= (database?.Version ?? 0) && v != entry.Live).ToList();
        entry.Versions.RemoveAll(gone.Contains);
        VersionCount -= gone.Count;
        return gone;
    }

    internal void Remove(Guid orderId)
    {
        if (_orders.Remove(orderId, out var entry))
        {
            VersionCount -= entry.Versions.Count;
            Latest = Latest.Remove(orderId);
        }
    }

    internal void AddFill(Fill fill)
    {
        _fills[fill.FillId] = fill;
        FillsByOrder = FillsByOrder.SetItem(
            fill.OrderId, FillsByOrder.GetValueOrDefault(fill.OrderId, ImmutableList<Fill>.Empty).Add(fill));
    }

    internal DateTime? FillConfirmedAt(Guid fillId) =>
        _fillsConfirmed.TryGetValue(fillId, out var at) ? at : null;

    internal void ConfirmFill(Guid fillId, DateTime at) => _fillsConfirmed.TryAdd(fillId, at);

    internal void RemoveFill(Fill fill)
    {
        _fills.Remove(fill.FillId);
        _fillsConfirmed.Remove(fill.FillId);
        var left = FillsByOrder[fill.OrderId].Remove(fill);
        FillsByOrder = left.IsEmpty ? FillsByOrder.Remove(fill.OrderId) : FillsByOrder.SetItem(fill.OrderId, left);
    }

    internal void Clear()
    {
        _orders.Clear();
        _fills.Clear();
        _fillsConfirmed.Clear();
        Latest = ImmutableDictionary<Guid, Order>.Empty;
        FillsByOrder = ImmutableDictionary<Guid, ImmutableList<Fill>>.Empty;
        VersionCount = 0;
    }
}

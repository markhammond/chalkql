using System.Collections.Immutable;

namespace Chalk.Sample.Blotter.Live;

/// <summary>
/// A snapshot of the database: the latest version of each order as of <see cref="OrdersThrough"/>
/// at least, written once into <c>snapshots</c> under its number; and every fill at or before
/// <see cref="FillsThrough"/>.
/// </summary>
/// <param name="Number">The generation, which order pages bind.</param>
/// <param name="OrdersThrough">The orders' watermark read before the snapshot was taken: every version at or before it is in.</param>
/// <param name="FillsThrough">The fills' watermark read with it, which fill pages bind: every fill at or before it is in.</param>
/// <param name="Rows">Orders in it, one row each.</param>
/// <param name="BuildTime">How long the database took to write it.</param>
public sealed record Generation(long Number, DateTime OrdersThrough, DateTime FillsThrough, long Rows, TimeSpan BuildTime);

/// <summary>
/// One order in the live tail: its latest version from the feed, beside what the current snapshot
/// holds for it. The tail keeps only orders whose latest version the snapshot does not have.
/// </summary>
/// <remarks>
/// Every column is here twice, the tail's version's and the snapshot's (<c>base_</c>), so that a live
/// scope can test a filter on any column against both in one statement: an order whose snapshot row
/// passes the filter has a row in the snapshot's pages that the tail now overrides, wherever its new
/// version lands, in the filter or out of it. <see cref="BaseVersion"/> is zero for an order the
/// snapshot does not hold at all, and its other columns then repeat the tail's.
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

    public static TailRow Of(Order live, Order? snapshot)
    {
        var b = snapshot ?? live;
        return new()
        {
            OrderId = live.OrderId, Version = live.Version, EventTime = live.EventTime, OrderTime = live.OrderTime,
            ClOrdId = live.ClOrdId, Client = live.Client, Region = live.Region, Channel = live.Channel,
            Instrument = live.Instrument, Side = live.Side, Qty = live.Qty, FilledQty = live.FilledQty,
            AvgPrice = live.AvgPrice, MarkupBps = live.MarkupBps, Status = live.Status,
            BaseOrderId = b.OrderId, BaseVersion = snapshot?.Version ?? 0, BaseEventTime = b.EventTime,
            BaseOrderTime = b.OrderTime, BaseClOrdId = b.ClOrdId, BaseClient = b.Client, BaseRegion = b.Region,
            BaseChannel = b.Channel, BaseInstrument = b.Instrument, BaseSide = b.Side, BaseQty = b.Qty,
            BaseFilledQty = b.FilledQty, BaseAvgPrice = b.AvgPrice, BaseMarkupBps = b.MarkupBps, BaseStatus = b.Status,
        };
    }
}

/// <summary>One order in the tail.</summary>
public sealed class TailEntry(Order live, Order? snapshot)
{
    /// <summary>The latest version the feed has sent.</summary>
    public Order Live { get; internal set; } = live;

    /// <summary>The order's row in the current generation, or null when the generation does not hold it.</summary>
    public Order? Base { get; internal set; } = snapshot;

    /// <summary>The versions the feed has sent that are newer than <see cref="Base"/>, oldest first.</summary>
    public List<Order> Versions { get; } = [];
}

/// <summary>
/// The live tail: every order whose latest version the current generation does not have, with that
/// version and the generation's own row for it; and every fill after the generation's watermark.
/// Changed only by the host's ingest loop; the scopes read it from that loop too, after each refresh.
/// </summary>
public sealed class Tail
{
    private readonly Dictionary<Guid, TailEntry> _orders = [];
    private readonly Dictionary<Guid, Fill> _fills = [];

    public int Count => _orders.Count;

    public int FillCount => _fills.Count;

    /// <summary>Versions held, the latest ones and those before them.</summary>
    public long VersionCount { get; private set; }

    /// <summary>
    /// The tail's fills by order, as of the last refresh: immutable, so that a page reading an order's
    /// fills from one state reads the same fills however the tail moves on.
    /// </summary>
    public ImmutableDictionary<Guid, ImmutableList<Fill>> FillsByOrder { get; private set; } =
        ImmutableDictionary<Guid, ImmutableList<Fill>>.Empty;

    /// <summary>What the tail holds, in bytes: its rows here and in ChalkQL, its versions and its fills in both.</summary>
    public long Bytes =>
        _orders.Count * (long)TailRow.Bytes + VersionCount * 2L * Order.Bytes + _fills.Count * 2L * (Fill.Bytes + 32);

    public TailEntry? Get(Guid orderId) => _orders.GetValueOrDefault(orderId);

    public Fill? GetFill(Guid fillId) => _fills.GetValueOrDefault(fillId);

    public IEnumerable<TailEntry> Entries => _orders.Values;

    public IEnumerable<Fill> Fills => _fills.Values;

    internal void Set(TailEntry entry, Order version)
    {
        _orders[entry.Live.OrderId] = entry;
        entry.Versions.Add(version);
        VersionCount++;
    }

    internal void Remove(Guid orderId)
    {
        if (_orders.Remove(orderId, out var entry))
        {
            VersionCount -= entry.Versions.Count;
        }
    }

    /// <summary>Drops the versions at or before <paramref name="version"/>, which the new generation has.</summary>
    internal List<Order> Trim(TailEntry entry, int version)
    {
        var gone = entry.Versions.Where(v => v.Version <= version).ToList();
        entry.Versions.RemoveAll(v => v.Version <= version);
        VersionCount -= gone.Count;
        return gone;
    }

    internal void AddFill(Fill fill)
    {
        _fills[fill.FillId] = fill;
        FillsByOrder = FillsByOrder.SetItem(
            fill.OrderId, FillsByOrder.GetValueOrDefault(fill.OrderId, ImmutableList<Fill>.Empty).Add(fill));
    }

    /// <summary>Drops the fills at or before <paramref name="through"/>, which a generation as of it has.</summary>
    internal List<Fill> TrimFills(DateTime through)
    {
        var gone = _fills.Values.Where(f => f.EventTime <= through).ToList();
        if (gone.Count == 0)
        {
            return gone;
        }

        var byOrder = FillsByOrder.ToBuilder();
        foreach (var fill in gone)
        {
            _fills.Remove(fill.FillId);
            var left = byOrder[fill.OrderId].Remove(fill);
            if (left.IsEmpty)
            {
                byOrder.Remove(fill.OrderId);
            }
            else
            {
                byOrder[fill.OrderId] = left;
            }
        }

        FillsByOrder = byOrder.ToImmutable();
        return gone;
    }

    internal void Clear()
    {
        _orders.Clear();
        _fills.Clear();
        FillsByOrder = ImmutableDictionary<Guid, ImmutableList<Fill>>.Empty;
        VersionCount = 0;
    }
}

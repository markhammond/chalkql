using System.Collections.Concurrent;
using Chalk.Client;
using Chalk.Incremental;

namespace Chalk.Sample.Blotter.Live;

/// <summary>A live scope's key: the trade.</summary>
public struct ScopeKey
{
    public long TradeId;
}

/// <summary>What a live scope keeps per trade: which versions it holds, and which of them pass the filter.</summary>
public struct ScopeRow
{
    public int Version;
    public int BaseVersion;
    public bool LiveMatch;
    public bool BaseMatch;
}

/// <summary>
/// The part of the tail one filter can see, kept current by a ChalkQL standing view: every trade
/// in the tail whose latest version passes the filter, or whose snapshot version did. Pages that
/// filter alike share one scope, whatever they sort by and wherever they have scrolled to; each
/// sorts its scope in the host, which is cheap because a scope is a slice of the tail, never of the
/// corpus.
/// </summary>
/// <remarks>
/// <para>
/// The view is maintained incrementally: a refresh's upserts reach it as the rows they changed, and
/// an update costs what changed rather than what the tail holds. Its state counts against the
/// scope's share of the budget (<see cref="ViewOptions{TKey, TRow}.StateBudget"/>), and an update past
/// it fails the scope rather than the host.
/// </para>
/// <para>
/// The scope is driven from one place, the host's ingest loop: after each refresh it updates the
/// view, drains what changed, and publishes a new <see cref="ScopeState"/>. A state is immutable and
/// whole, so a page can read one while the next is being made.
/// </para>
/// </remarks>
public sealed class LiveScope : IAsyncDisposable, IViewSink<ScopeKey, ScopeRow>
{
    private readonly StandingView<ScopeKey, ScopeRow> _view;
    private readonly Dictionary<long, ScopeRow> _rows = [];
    private ViewCursor _cursor = ViewCursor.Start;
    private ScopeState _state;

    private LiveScope(FilterSpec filter, StandingView<ScopeKey, ScopeRow> view, long budget)
    {
        Filter = filter;
        _view = view;
        Budget = budget;
        _state = ScopeState.Empty(filter);
    }

    public FilterSpec Filter { get; }

    /// <summary>The scope's share of the host's budget, which its view's state may not pass.</summary>
    public long Budget { get; }

    /// <summary>Pages using this scope; the host closes it when the last one leaves.</summary>
    public int Pages { get; internal set; }

    /// <summary>When the last page left, for the host's grace period.</summary>
    public DateTime? Idle { get; internal set; }

    /// <summary>The latest published state.</summary>
    public ScopeState State => Volatile.Read(ref _state);

    /// <summary>Trades the view holds.</summary>
    public int ViewRows => _view.Count;

    /// <summary>The view's state, in bytes, by its own count.</summary>
    public long ViewBytes => _view.StateBytes;

    /// <summary>The statement the scope's view runs.</summary>
    public string PlanText => _view.Statement.PlanText ?? "";

    /// <summary>A failure that ended the scope: a page on it shows this, and asks for a new scope.</summary>
    public Exception? Fault { get; private set; }

    public static string Sql(FilterSpec filter) => $"""
        SELECT trade_id, version, base_version,
               CASE WHEN {filter.Where()} THEN 1 ELSE 0 END AS live_match,
               CASE WHEN base_version > 0 AND {filter.Where("base_")} THEN 1 ELSE 0 END AS base_match
        FROM live.tail
        WHERE ({filter.Where()}) OR (base_version > 0 AND {filter.Where("base_")})
        """;

    internal static async Task<LiveScope> CreateAsync(
        ChalkEngine engine, FilterSpec filter, long budget, ViewMaintenance maintenance, CancellationToken ct)
    {
        var view = await engine.MaterializeAsync(Sql(filter), new ViewOptions<ScopeKey, ScopeRow>
        {
            Key = ["trade_id"],
            LocalJoinMaxRows = StandingViews.DefaultLocalJoinMaxRows,
            Map = Map,
            Parameters = filter.Parameters(),
            StateBudget = budget,
            Maintenance = maintenance,
            Prepare = new PrepareOptions { IncludePlanText = true },

            // A reader of this view is the scope itself, drained after every update: it never falls
            // far enough behind for a reset to be cheaper than the changes.
            ResetWhen = double.PositiveInfinity,
        }, ct).ConfigureAwait(false);
        return new LiveScope(filter, view, budget);
    }

    /// <summary>
    /// Brings the view to the engine's latest epoch, takes what changed, and publishes the state it
    /// now holds, with each trade's versions resolved from the tail.
    /// </summary>
    internal async Task UpdateAsync(Tail tail, long epoch, Generation generation, DateTime ingestedThrough, CancellationToken ct)
    {
        if (Fault is not null)
        {
            return;
        }

        try
        {
            await _view.UpdateAsync(ct).ConfigureAwait(false);
            while (_view.Drain(ref _cursor, this, 65536) == DrainOutcome.Partial)
            {
            }

            Volatile.Write(ref _state, ScopeState.Of(Filter, epoch, generation, ingestedThrough, tail, _rows));
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            Fault = failure;
        }
    }

    public ValueTask DisposeAsync() => _view.DisposeAsync();

    void IViewSink<ScopeKey, ScopeRow>.Reset() => _rows.Clear();

    void IViewSink<ScopeKey, ScopeRow>.Upsert(in ScopeKey key, in ScopeRow row) => _rows[key.TradeId] = row;

    void IViewSink<ScopeKey, ScopeRow>.Delete(in ScopeKey key) => _rows.Remove(key.TradeId);

    void IViewSink<ScopeKey, ScopeRow>.Commit(long version, uint checksum)
    {
    }

    private static void Map(in ViewRow row, out ScopeKey key, out ScopeRow value)
    {
        key = new ScopeKey { TradeId = row.GetInt64(0) };
        value = new ScopeRow
        {
            Version = row.GetInt32(1),
            BaseVersion = row.GetInt32(2),
            LiveMatch = row.GetInt32(3) == 1,
            BaseMatch = row.GetInt32(4) == 1,
        };
    }
}

/// <summary>
/// A live scope as of one epoch: the tail's trades that pass its filter now, and the snapshot rows
/// the tail overrides that passed it then. Immutable; sorted per order on first use.
/// </summary>
public sealed class ScopeState
{
    private readonly ConcurrentDictionary<SortSpec, SortedScope> _sorted = new();

    private ScopeState(
        FilterSpec filter,
        long epoch,
        Generation? generation,
        DateTime ingestedThrough,
        TradeVersion[] live,
        TradeVersion[] overridden,
        HashSet<long> overriddenIds,
        Dictionary<long, TradeVersion> tail)
    {
        Filter = filter;
        Epoch = epoch;
        Generation = generation;
        IngestedThrough = ingestedThrough;
        Live = live;
        Overridden = overridden;
        OverriddenIds = overriddenIds;
        Tail = tail;
    }

    public FilterSpec Filter { get; }

    /// <summary>The engine's version this state is the scope's rows at.</summary>
    public long Epoch { get; }

    /// <summary>The snapshot the tail was taken against at that epoch: the one pages must read with it.</summary>
    public Generation? Generation { get; }

    /// <summary>
    /// How far the host had read the feed: every version sent at or before this time is in this
    /// state, by the generation or by the tail. Because the feed is read in order, nothing after it is.
    /// </summary>
    public DateTime IngestedThrough { get; }

    /// <summary>The tail's latest versions that pass the filter.</summary>
    public TradeVersion[] Live { get; }

    /// <summary>The snapshot's rows that pass the filter for trades the tail holds a newer version of.</summary>
    public TradeVersion[] Overridden { get; }

    public HashSet<long> OverriddenIds { get; }

    /// <summary>Every trade in the scope by id, at its latest version, whether or not it passes the filter now.</summary>
    public Dictionary<long, TradeVersion> Tail { get; }

    /// <summary>A rough size in memory, for the budget.</summary>
    public long Bytes => 64L * (Live.Length + Overridden.Length) + 48L * (OverriddenIds.Count + Tail.Count);

    internal static ScopeState Empty(FilterSpec filter) => new(filter, 0, null, default, [], [], [], []);

    internal static ScopeState Of(
        FilterSpec filter, long epoch, Generation generation, DateTime ingestedThrough, Tail tail, Dictionary<long, ScopeRow> rows)
    {
        var live = new List<TradeVersion>(rows.Count);
        var overridden = new List<TradeVersion>();
        var overriddenIds = new HashSet<long>();
        var byId = new Dictionary<long, TradeVersion>(rows.Count);
        foreach (var (tradeId, row) in rows)
        {
            // The view's rows and the tail are of one epoch, because the ingest loop changes the
            // tail, refreshes the engine and updates the scopes one after the other. A version that
            // does not match is a bug, and a page built on it would be wrong, so it stops here.
            var entry = tail.Get(tradeId)
                ?? throw new InvalidOperationException($"the scope holds trade {tradeId}, which the tail does not");
            if (entry.Live.Version != row.Version || (entry.Base?.Version ?? 0) != row.BaseVersion)
            {
                throw new InvalidOperationException(
                    $"the scope holds trade {tradeId} at v{row.Version} over v{row.BaseVersion}, "
                    + $"the tail at v{entry.Live.Version} over v{entry.Base?.Version ?? 0}");
            }

            byId[tradeId] = entry.Live;
            if (row.LiveMatch)
            {
                live.Add(entry.Live);
            }

            if (row.BaseMatch)
            {
                overridden.Add(entry.Base!);
                overriddenIds.Add(tradeId);
            }
        }

        return new ScopeState(filter, epoch, generation, ingestedThrough, [.. live], [.. overridden], overriddenIds, byId);
    }

    /// <summary>The scope's rows in <paramref name="order"/>.</summary>
    public SortedScope Sorted(SortSpec order) => _sorted.GetOrAdd(order, o => new SortedScope(this, o));
}

/// <summary>A scope's rows in one order, and how many of each sort before a given row.</summary>
public sealed class SortedScope
{
    internal SortedScope(ScopeState state, SortSpec order)
    {
        Order = order;
        Live = [.. state.Live];
        Array.Sort(Live, order);
        Overridden = [.. state.Overridden];
        Array.Sort(Overridden, order);
    }

    public SortSpec Order { get; }

    public TradeVersion[] Live { get; }

    public TradeVersion[] Overridden { get; }

    /// <summary>How many of the tail's live rows sort before <paramref name="key"/>.</summary>
    public int LiveBefore(TradeVersion key) => Before(Live, key);

    /// <summary>How many overridden snapshot rows sort before <paramref name="key"/>.</summary>
    public int OverriddenBefore(TradeVersion key) => Before(Overridden, key);

    private int Before(TradeVersion[] rows, TradeVersion key)
    {
        int low = 0, high = rows.Length;
        while (low < high)
        {
            var middle = (low + high) >>> 1;
            if (Order.Compare(rows[middle], key) < 0)
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
}

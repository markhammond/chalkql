using System.Collections.Concurrent;
using System.Collections.Immutable;
using Chalk.Client;
using Chalk.Incremental;

namespace Chalk.Sample.Blotter.Live;

/// <summary>A live scope's key: the order, or the fill.</summary>
public struct ScopeKey
{
    public Guid Id;
}

/// <summary>What a live scope keeps per row: which versions it holds, and which of them pass the filter.</summary>
public struct ScopeRow
{
    public int Version;
    public int BaseVersion;
    public bool LiveMatch;
    public bool BaseMatch;
}

/// <summary>
/// The part of a tab's tail one filter can see, kept current by a ChalkQL standing view: every order
/// in the tail whose latest version passes the filter, or whose row in the database last did; every
/// fill in the tail that passes it. Pages that filter alike share one scope, whatever they sort by and
/// wherever they have scrolled to; each sorts its scope in the host, which is cheap because a scope is
/// a slice of the tail, never of the corpus.
/// </summary>
/// <remarks>
/// <para>
/// The view is maintained incrementally by default: a refresh's upserts reach it as the rows they
/// changed, and an update costs what changed rather than what the tail holds. Its state counts
/// against the host's budget, and an update past its own <see cref="ViewOptions{TKey, TRow}.StateBudget"/>
/// fails the scope rather than the host.
/// </para>
/// <para>
/// The scope is driven from one place, the host's catch-up loop: after each refresh it updates the view,
/// drains what changed, and publishes a new <see cref="ScopeState"/>. A state is immutable and whole,
/// so a page can read one while the next is being made.
/// </para>
/// </remarks>
public sealed class LiveScope : IAsyncDisposable, IViewSink<ScopeKey, ScopeRow>
{
    private readonly StandingView<ScopeKey, ScopeRow> _view;
    private readonly Dictionary<Guid, ScopeRow> _rows = [];
    private ViewCursor _cursor = ViewCursor.Start;
    private ScopeState _state;

    private LiveScope(Tab tab, FilterSpec filter, StandingView<ScopeKey, ScopeRow> view)
    {
        Tab = tab;
        Filter = filter;
        _view = view;
        _state = ScopeState.Empty(tab, filter);
    }

    public Tab Tab { get; }

    public FilterSpec Filter { get; }

    /// <summary>The key a host shares scopes by.</summary>
    public string Name => Tab.Name + ": " + Filter.Name;

    /// <summary>Pages using this scope; the host closes it when the last one leaves.</summary>
    public int Pages { get; internal set; }

    /// <summary>When the last page left, for the host's grace period.</summary>
    public DateTime? Idle { get; internal set; }

    /// <summary>The latest published state.</summary>
    public ScopeState State => Volatile.Read(ref _state);

    /// <summary>Rows the view holds.</summary>
    public int ViewRows => _view.Count;

    /// <summary>The view's state, in bytes, by its own count.</summary>
    public long ViewBytes => _view.StateBytes;

    /// <summary>The statement the scope's view runs.</summary>
    public string PlanText => _view.Statement.PlanText ?? "";

    /// <summary>A failure that ended the scope: a page on it shows this, and asks for a new scope.</summary>
    public Exception? Fault { get; private set; }

    public static string Sql(Tab tab, FilterSpec filter) => tab.Overrides
        ? $"""
          SELECT order_id, version, base_version,
                 CASE WHEN {filter.Where()} THEN 1 ELSE 0 END AS live_match,
                 CASE WHEN base_version > 0 AND {filter.Where("base_")} THEN 1 ELSE 0 END AS base_match
          FROM live.tail
          WHERE ({filter.Where()}) OR (base_version > 0 AND {filter.Where("base_")})
          """
        : $"""
          SELECT fill_id, 1 AS version, 0 AS base_version, 1 AS live_match, 0 AS base_match
          FROM live.tail_fills
          WHERE {filter.Where()}
          """;

    internal static async Task<LiveScope> CreateAsync(
        ChalkEngine engine, Tab tab, FilterSpec filter, long budget, ViewMaintenance maintenance, CancellationToken ct)
    {
        var view = await engine.MaterializeAsync(Sql(tab, filter), new ViewOptions<ScopeKey, ScopeRow>
        {
            Key = [tab.Key.Name],
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
        return new LiveScope(tab, filter, view);
    }

    /// <summary>
    /// Brings the view to the engine's latest epoch, takes what changed, and publishes the state it
    /// now holds, with each row resolved from the tail.
    /// </summary>
    internal async Task UpdateAsync(Tail tail, long epoch, StreamMark mark, CancellationToken ct)
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

            Volatile.Write(ref _state, ScopeState.Of(Tab, Filter, epoch, mark, tail, _rows));
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            Fault = failure;
        }
    }

    public ValueTask DisposeAsync() => _view.DisposeAsync();

    void IViewSink<ScopeKey, ScopeRow>.Reset() => _rows.Clear();

    void IViewSink<ScopeKey, ScopeRow>.Upsert(in ScopeKey key, in ScopeRow row) => _rows[key.Id] = row;

    void IViewSink<ScopeKey, ScopeRow>.Delete(in ScopeKey key) => _rows.Remove(key.Id);

    void IViewSink<ScopeKey, ScopeRow>.Commit(long version, uint checksum)
    {
    }

    private static void Map(in ViewRow row, out ScopeKey key, out ScopeRow value)
    {
        key = new ScopeKey { Id = (Guid)row.GetValue(0)! };
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
/// A live scope as of one epoch: the tail's rows that pass its filter now, and the database rows the
/// tail overrides that passed it when last looked up. Immutable; sorted per order on first use.
/// </summary>
public sealed class ScopeState
{
    private readonly ConcurrentDictionary<SortSpec, SortedScope> _sorted = new();

    private ScopeState(
        Tab tab,
        FilterSpec filter,
        long epoch,
        bool ready,
        StreamMark mark,
        IRow[] live,
        IRow[] overridden,
        HashSet<Guid> overriddenIds,
        Dictionary<Guid, IRow> tail,
        ImmutableDictionary<Guid, Order> latest,
        ImmutableDictionary<Guid, ImmutableList<Fill>> fillsByOrder,
        ImmutableDictionary<Guid, Reconciliation> reconciling)
    {
        Tab = tab;
        Filter = filter;
        Epoch = epoch;
        Ready = ready;
        Mark = mark;
        Live = live;
        Overridden = overridden;
        OverriddenIds = overriddenIds;
        Tail = tail;
        Latest = latest;
        FillsByOrder = fillsByOrder;
        Reconciling = reconciling;
    }

    public Tab Tab { get; }

    public FilterSpec Filter { get; }

    /// <summary>The engine's version this state is the scope's rows at.</summary>
    public long Epoch { get; }

    /// <summary>Whether the scope has taken its first refresh: an empty state, before then, is no answer.</summary>
    public bool Ready { get; }

    /// <summary>How far the host had read each stream when this state was taken.</summary>
    public StreamMark Mark { get; }

    /// <summary>
    /// The latest time of an order version the host had read. Mostly, everything sent before it has
    /// been read too; but a version sent late can still come.
    /// </summary>
    public DateTime OrdersThrough => Mark.OrdersThrough;

    /// <summary>The same for the fill stream, which is read apart from the orders and may run ahead of them.</summary>
    public DateTime FillsThrough => Mark.FillsThrough;

    /// <summary>How far the host had read the stream of this state's tab.</summary>
    public DateTime IngestedThrough => Tab.Overrides ? OrdersThrough : FillsThrough;

    /// <summary>The tail's rows that pass the filter: orders at their latest versions, or fills.</summary>
    public IRow[] Live { get; }

    /// <summary>The database's rows, as last looked up, that pass the filter for orders the tail holds a newer version of.</summary>
    public IRow[] Overridden { get; }

    public HashSet<Guid> OverriddenIds { get; }

    /// <summary>Every row in the scope by id, at its latest version, whether or not it passes the filter now.</summary>
    public Dictionary<Guid, IRow> Tail { get; }

    /// <summary>Every order's latest version in the tail at this epoch, whatever filter it passes.</summary>
    public ImmutableDictionary<Guid, Order> Latest { get; }

    /// <summary>The tail's fills of each order at this epoch, which an order's nested fills are read with.</summary>
    public ImmutableDictionary<Guid, ImmutableList<Fill>> FillsByOrder { get; }

    /// <summary>How long the database has taken to hold each order and fill in the tail at this epoch.</summary>
    public ImmutableDictionary<Guid, Reconciliation> Reconciling { get; }

    /// <summary>A rough size in memory, for the budget.</summary>
    public long Bytes => 64L * (Live.Length + Overridden.Length) + 48L * (OverriddenIds.Count + Tail.Count);

    internal static ScopeState Empty(Tab tab, FilterSpec filter) =>
        new(tab, filter, 0, false, default, [], [], [], [], ImmutableDictionary<Guid, Order>.Empty,
            ImmutableDictionary<Guid, ImmutableList<Fill>>.Empty, ImmutableDictionary<Guid, Reconciliation>.Empty);

    internal static ScopeState Of(
        Tab tab, FilterSpec filter, long epoch, StreamMark mark, Tail tail, Dictionary<Guid, ScopeRow> rows)
    {
        var live = new List<IRow>(rows.Count);
        var overridden = new List<IRow>();
        var overriddenIds = new HashSet<Guid>();
        var byId = new Dictionary<Guid, IRow>(rows.Count);
        foreach (var (id, row) in rows)
        {
            // The view's rows and the tail are of one epoch, because the catch-up loop changes the
            // tail, refreshes the engine and updates the scopes one after the other. A version that
            // does not match is a bug, and a page built on it would be wrong, so it stops here.
            if (!tab.Overrides)
            {
                var fill = tail.GetFill(id) ?? throw new InvalidOperationException($"the scope holds fill {id}, which the tail does not");
                byId[id] = fill;
                live.Add(fill);

                // A fill the database has been seen to hold is in its rows too: counted once, not twice.
                if (tail.FillConfirmedAt(id) is not null)
                {
                    overridden.Add(fill);
                    overriddenIds.Add(id);
                }

                continue;
            }

            var entry = tail.Get(id) ?? throw new InvalidOperationException($"the scope holds order {id}, which the tail does not");
            if (entry.Live.Version != row.Version || (entry.Base?.Version ?? 0) != row.BaseVersion)
            {
                throw new InvalidOperationException(
                    $"the scope holds order {id} at v{row.Version} over v{row.BaseVersion}, "
                    + $"the tail at v{entry.Live.Version} over v{entry.Base?.Version ?? 0}");
            }

            byId[id] = entry.Live;
            if (row.LiveMatch)
            {
                live.Add(entry.Live);
            }

            if (row.BaseMatch)
            {
                overridden.Add(entry.Base!);
                overriddenIds.Add(id);
            }
        }

        return new ScopeState(
            tab, filter, epoch, true, mark, [.. live], [.. overridden], overriddenIds, byId, tail.Latest,
            tail.FillsByOrder, tail.Reconciling);
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

    public IRow[] Live { get; }

    public IRow[] Overridden { get; }

    /// <summary>How many of the tail's live rows sort before <paramref name="key"/>.</summary>
    public int LiveBefore(IRow key) => Before(Live, key);

    /// <summary>How many overridden database rows sort before <paramref name="key"/>.</summary>
    public int OverriddenBefore(IRow key) => Before(Overridden, key);

    /// <summary>The tail's live rows from <paramref name="key"/> on (or after it), up to and including <paramref name="last"/>.</summary>
    public ArraySegment<IRow> LiveBetween(IRow? key, bool inclusive, IRow? last)
    {
        var from = key is null ? 0 : inclusive ? Before(Live, key) : After(Live, key);
        var to = last is null ? Live.Length : After(Live, last);
        return new ArraySegment<IRow>(Live, from, Math.Max(0, to - from));
    }

    private int After(IRow[] rows, IRow key)
    {
        int low = 0, high = rows.Length;
        while (low < high)
        {
            var middle = (low + high) >>> 1;
            if (Order.Compare(rows[middle], key) <= 0)
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

    private int Before(IRow[] rows, IRow key)
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

/// <summary>
/// How far the host had read each stream: the latest event time read on each, and how many rows,
/// counted from the stream's start. The count is exact whatever order rows arrive in; the time is not.
/// </summary>
public readonly record struct StreamMark(DateTime OrdersThrough, DateTime FillsThrough, long OrdersRead, long FillsRead);

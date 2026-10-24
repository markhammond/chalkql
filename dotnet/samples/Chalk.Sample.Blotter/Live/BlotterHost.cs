using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using Chalk.Catalog;
using Chalk.Client;
using Chalk.Ir;
using Chalk.Sample.Blotter.Fixtures;
using Chalk.Sample.Blotter.Domain;
using Chalk.Sources;
using Chalk.Sources.Ado;
using Chalk.Sources.Poco;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Chalk.Sample.Blotter.Live;

/// <summary>Whether the host is serving pages, and if not, why.</summary>
public enum BlotterState
{
    Starting,
    Live,

    /// <summary>
    /// What the host would have to hold to serve correctly does not fit the budget: the tail that
    /// covers the database's lag, and the scopes over it. Nothing is served until it would fit.
    /// </summary>
    Refusing,
}

/// <summary>A refusal, said once: what the host will not do, and why.</summary>
public sealed class BlotterRefusedException(string message) : InvalidOperationException(message);

/// <summary>
/// The blotter's host: the database and the simulated market, the ChalkQL engine over the database and
/// the tail, and the one loop that changes the tail.
/// </summary>
/// <remarks>
/// <para>
/// A page is the database read live and the tail laid over it. The tail is every order version and
/// fill the streams have sent since the host subscribed that the database has not been seen to hold.
/// The host looks the tail's rows up in the database every half second; a row the database holds, at
/// its version or a later one, leaves the tail a little later. It asks nothing else of the database:
/// no watermark, no snapshot, and no promise about when <c>orders_current</c> catches up with
/// <c>order_versions</c>, so it serves alike whether a trigger keeps the two in step or a job runs
/// behind.
/// </para>
/// <para>
/// The streams do not replay. When the host starts serving it subscribes, and then asks the database
/// once for every version written lately — the gap fill — and takes each as if the stream had sent
/// it: a version sent before the subscription, written to <c>order_versions</c> but not yet to
/// <c>orders_current</c>, is then shown all the same.
/// </para>
/// <para>
/// When the database is so far behind that the tail and its scopes would outgrow the budget, the host
/// stops serving and says why; it keeps reading the streams, and keeps the last of what they send.
/// Once <c>orders_current</c> has caught up, it serves again, from what it kept and a new gap fill. It
/// never drops a version to stay in budget.
/// </para>
/// </remarks>
public sealed class BlotterHost : IAsyncDisposable
{
    private readonly BlotterOptions _options;
    private readonly ILogger _log;
    private readonly Channel<Func<Task>> _actions = Channel.CreateUnbounded<Func<Task>>();
    private readonly Tail _tail = new();
    private readonly Dictionary<string, LiveScope> _scopes = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Order> _keptOrders = [];
    private readonly List<Fill> _keptFills = [];
    private PgServer? _server;
    private TradeStore? _store;
    private Exchange? _exchange;
    private PlannerProcess? _planner;
    private ChalkEngine? _engine;
    private PocoTable<TailRow>? _tailTable;
    private PocoTable<Fill>? _fillsTable;
    private PocoTable<Order>? _versionsTable;
    private AlertTables? _alerts;
    private long _lastRefresh;
    private string _alertsWatched = "";
    private DateTimeOffset _clock = DateTimeOffset.MinValue;
    private ChannelReader<Order[]>? _orderFeed;
    private ChannelReader<Fill[]>? _fillFeed;
    private Task? _loop;
    private long _version;
    private long _budget;
    private DateTime _ordersThrough;
    private DateTime _fillsThrough;
    private DateTime _newestOrderSent;
    private long _lastConfirm;
    private long _lastResumeCheck;
    private TimeSpan _refusedBehind;
    private int _disposed;

    // Until a budget is chosen: raise it rather than refuse for want of it.
    private bool _findingBudget;

    // Pages with the vital statistics open: the database's size, a query, is read only while there are any.
    private int _vitalsWatchers;

    // Rows of each stream the host has read, counted from the start of the stream, as Published is.
    private long _ordersRead;
    private long _fillsRead;

    public BlotterHost(BlotterOptions options, ILogger<BlotterHost> log)
    {
        _options = options;
        _log = log;
        _budget = options.BudgetBytes;
        _findingBudget = options.FindBudget;
    }

    public BlotterState State { get; private set; } = BlotterState.Starting;

    /// <summary>Why the host is not serving, when it is not.</summary>
    public string? Refusal { get; private set; }

    public Exchange Exchange => _exchange ?? throw new InvalidOperationException("the host has not started");

    /// <summary>The database, for the checks that read it directly.</summary>
    public TradeStore Store => _store ?? throw new InvalidOperationException("the host has not started");

    /// <summary>How the database was seeded: what the corpus holds, and the latest event in it.</summary>
    public Seeder Seeded { get; private set; } = null!;

    public SourceQueries Queries { get; private set; } = null!;

    /// <summary>The engine, for the tests' own statements.</summary>
    internal ChalkEngine Engine => _engine ?? throw new InvalidOperationException("the host has not started");

    public Pages Pages { get; private set; } = null!;

    public BlotterOptions Options => _options;

    /// <summary>The budget now; the page may change it.</summary>
    public long Budget => Interlocked.Read(ref _budget);

    /// <summary>How far the host has read each stream: everything sent from its subscription to here is in the tail or the database.</summary>
    public DateTime OrdersThrough => _ordersThrough;

    public DateTime FillsThrough => _fillsThrough;

    /// <summary>Raised after every refresh the scopes have taken, with its version.</summary>
    public event Action<long>? Published;

    /// <summary>
    /// Raised with every batch the host reads off the streams, as it reads it: each order version and
    /// fill sent, whether or not the tail takes it (it does not, when the database holds it already).
    /// On the catch-up loop: a handler only takes note.
    /// </summary>
    public event Action<IReadOnlyList<Order>, IReadOnlyList<Fill>>? Arrived;

    /// <summary>
    /// Raised on the catch-up loop as rows enter an alert view: the view, and each row with how long it
    /// had waited for the database, for a view of the wait. A handler only takes note.
    /// </summary>
    public event Action<AlertViewSpec, IReadOnlyList<(IRow Row, TimeSpan? Lag)>>? AlertsEntered;

    /// <summary>How often the clock moves while nothing arrives, for the alert views that watch the wait.</summary>
    public static readonly TimeSpan AlertTick = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// The alert views the browsers' rules need: on the catch-up loop, those not open are opened, each
    /// within the host's budget, and those no rule needs any more are closed.
    /// </summary>
    public void WatchAlerts(IReadOnlyCollection<AlertViewSpec> specs)
    {
        var names = string.Join("\u0001", specs.Select(s => s.Name).Distinct().Order(StringComparer.Ordinal));
        if (names == _alertsWatched || _engine is null)
        {
            return;
        }

        _alertsWatched = names;
        _ = OnLoopAsync(async () =>
        {
            await _alerts!.WatchAsync(specs, _engine, _budget, _stop.Token).ConfigureAwait(false);
            return true;
        });
    }

    /// <summary>How many alert views are open.</summary>
    public int AlertViewsOpen => _alerts?.Count ?? 0;

    /// <summary>The alert views ChalkQL would not make, and what it said, by the view's name.</summary>
    public ImmutableDictionary<string, string> AlertsRefused => _alerts?.Refused ?? ImmutableDictionary<string, string>.Empty;

    /// <summary>How long the database has taken to hold each row in the tail, as of the latest state.</summary>
    public ImmutableDictionary<Guid, Reconciliation> Reconciling => _tail.Reconciling;

    /// <summary>Counters for the page's status bar, refreshed by the loop.</summary>
    public BlotterStatus Status { get; private set; } = new();

    public async Task StartAsync(CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        Seeder? seeding = null;
        await using (Spinner.Start(() => seeding is null
            ? "Starting PostgreSQL"
            : seeding.Written < _options.Orders
                ? string.Create(CultureInfo.InvariantCulture,
                    $"Seeding PostgreSQL: {seeding.Stage}, {seeding.Written:N0} of {_options.Orders:N0} ({100.0 * seeding.Written / Math.Max(1, _options.Orders):F0}%)")
                : $"Seeding PostgreSQL: {seeding.Stage}"))
        {
            _server = await PgServer.StartAsync(_options.Postgres, _options.PostgresBinaries, ct).ConfigureAwait(false);
            seeding = new Seeder(new TradeStore(_server.ConnectionString));
            await seeding.GenerateAsync(
                _options.Orders, Cells.Micros(DateTime.UtcNow), TimeSpan.FromDays(5), openTail: 2000, _options.Current, ct).ConfigureAwait(false);
        }

        var store = seeding.Store;
        (_store, Seeded) = (store, seeding);
        _log.LogInformation(
            "loaded {Orders:N0} orders, {Versions:N0} versions and {Fills:N0} fills into PostgreSQL in {Load:F1} s, indexed in {Index:F1} s",
            seeding.Orders, seeding.Versions, seeding.Fills, seeding.LoadTime.TotalSeconds, seeding.IndexTime.TotalSeconds);

        // The market starts before the blotter: events are in flight, sent and not yet written, when
        // the blotter subscribes, as they would be at any real start.
        _exchange = new Exchange(store, _options.Exchange, _options.Orders, seeding.LoadedThrough, store.OpenOrders(), _options.Current);
        _exchange.Start();

        _planner = await PlannerProcess.StartAsync(new PlannerProcessOptions { JavaHome = JavaHome() }).ConfigureAwait(false);

        // The tables' text is collated "C", so the database sorts and compares strings byte by byte,
        // as the host does; told so, ChalkQL pushes sorts and ranges on text down.
        var profile = DialectProfiles.PostgreSql.With(stringCollation: StringCollation.Binary);
        var connectionString = Reading(_server.ConnectionString, _options.QueryTimeLimit);
        var database = new AdoSourceBuilder("db", () => new NpgsqlConnection(connectionString), "blotter")
            .Dialect(profile)
            .Capabilities(AdoCapabilities.For(profile))

            // No statement may run longer than the host's time limit, in the database or in ChalkQL.
            .Options(new SourceOptions { QueryTimeout = _options.QueryTimeLimit })

            // Declared rather than discovered, with the indexes and the sizes the planner prices by.
            // ChalkQL's schema is the database's, because a pushed query names a table by its
            // catalog name (F273).
            .AddTable("orders_current", OrderColumns, configure: t => t
                .UniqueKey("order_id")
                .Index("orders_current_newest", false, "order_time", "order_id")
                .RowCount(seeding.Orders))
            .AddTable("order_versions", OrderColumns, configure: t => t
                .UniqueKey("order_id", "version")
                .Index("order_versions_recent", false, "event_time")
                .RowCount(seeding.Versions))
            .AddTable("fills", FillColumns, configure: t => t
                .UniqueKey("fill_id")
                .Index("fills_by_order", false, "order_id")
                .Index("fills_newest", false, "event_time", "fill_id")
                .RowCount(seeding.Fills))
            .Build();
        var live = new PocoSourceBuilder("live", schemaName: "live")
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .AddTable("tail", (IReadOnlyList<TailRow>)[], out PocoTable<TailRow> tail, t => t.Keyed(r => r.OrderId))
            .AddTable(
                "order_versions",
                (IReadOnlyList<Order>)[],
                out PocoTable<Order> versions,
                t => t.Keyed(v => v.OrderId, v => v.Version))
            .AddTable("tail_fills", (IReadOnlyList<Fill>)[], out PocoTable<Fill> fills, t => t.Keyed(f => f.FillId))
            .AddTable("arrived_fills", (IReadOnlyList<Fill>)[], out PocoTable<Fill> arrivedFills, t => t.Keyed(f => f.FillId))
            .AddTable("latest_orders", (IReadOnlyList<Order>)[], out PocoTable<Order> latestOrders, t => t.Keyed(o => o.OrderId))
            .AddTable("waits", (IReadOnlyList<Wait>)[], out PocoTable<Wait> waits, t => t.Keyed(w => w.Id))
            .Build();
        _alerts = new AlertTables(arrivedFills, latestOrders, waits);
        _alerts.Entered += (spec, rows) => AlertsEntered?.Invoke(spec, rows);
        _tailTable = tail;
        _versionsTable = versions;
        _fillsTable = fills;
        _engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "blotter",
            Sources = [database, live],
            Planner = _planner.CreatePlanner(),

            // Alert views of the wait read the time as @now: the clock every refresh moves.
            Clock = "now",
        }, ct).ConfigureAwait(false);
        Queries = new SourceQueries(_engine, _options.QueryTimeLimit, new PostgresExplain(connectionString));
        Pages = new Pages(Queries)
        {
            MaxKeptRows = PageRows(),
            WindowFor = _options.EvictAfter > TimeSpan.FromSeconds(1) ? _options.EvictAfter - TimeSpan.FromSeconds(1) : TimeSpan.Zero,
        };

        await GoLiveAsync(ct).ConfigureAwait(false);
        _log.LogInformation("serving after {Seconds:F1} s", Stopwatch.GetElapsedTime(started).TotalSeconds);
        _loop = Task.Run(() => RunAsync(_stop.Token), CancellationToken.None);
    }

    /// <summary>
    /// The scope for <paramref name="filter"/> on <paramref name="tab"/>: an open one if a page already
    /// filters alike, or a new one if the budget has room for it. Every page that takes one gives it
    /// back with <see cref="ReleaseAsync"/>.
    /// </summary>
    /// <exception cref="BlotterRefusedException">The host is not serving, or the scope would not fit the budget.</exception>
    public Task<LiveScope> AcquireAsync(Tab tab, FilterSpec filter, CancellationToken ct) =>
        OnLoopAsync(async () =>
        {
            RefuseUnlessLive();
            var name = tab.Name + ": " + filter.Name;
            if (_scopes.TryGetValue(name, out var open) && open.Fault is null)
            {
                open.Pages++;
                open.Idle = null;
                return open;
            }

            if (open is not null)
            {
                await CloseAsync(open).ConfigureAwait(false);
            }

            // A scope holds a row for each order or fill of the tail its filter can see, so it costs about
            // what the scopes open now cost per row, times the tail. Idle scopes make room first.
            var estimate = ScopeEstimate();
            foreach (var idle in _scopes.Values.Where(s => s.Pages == 0).ToList())
            {
                if (LiveBytes() + estimate <= _budget)
                {
                    break;
                }

                await CloseAsync(idle).ConfigureAwait(false);
            }

            while (LiveBytes() + estimate > _budget && RaiseBudget($"another live scope would hold about {Megabytes(estimate)}"))
            {
            }

            if (LiveBytes() + estimate > _budget)
            {
                throw new BlotterRefusedException(string.Create(CultureInfo.InvariantCulture,
                    $"no live scope for this filter: another would hold about {Megabytes(estimate)}, and the tail and "
                    + $"{_scopes.Count} open scopes already hold {Megabytes(LiveBytes())} of the {Megabytes(_budget)} budget"));
            }

            while (true)
            {
                var scope = await LiveScope.CreateAsync(_engine!, tab, filter, _budget, _options.Maintenance, ct).ConfigureAwait(false);
                await scope.UpdateAsync(_tail, _version, Mark(), ct).ConfigureAwait(false);
                if (scope.Fault is { } fault)
                {
                    await scope.DisposeAsync().ConfigureAwait(false);
                    if (RaiseBudget($"the live scope for this filter could not start: {fault.Message}"))
                    {
                        continue;
                    }

                    throw new BlotterRefusedException($"the live scope for this filter could not start: {fault.Message}");
                }

                scope.Pages = 1;
                _scopes[name] = scope;
                return scope;
            }
        });

    public Task ReleaseAsync(LiveScope scope) =>
        OnLoopAsync(() =>
        {
            scope.Pages = Math.Max(0, scope.Pages - 1);
            if (scope.Pages == 0)
            {
                scope.Idle = DateTime.UtcNow;
            }

            return Task.FromResult(true);
        });

    /// <summary>Sets the budget. Open scopes keep the share they were made with until they close.</summary>
    public Task SetBudgetAsync(long bytes) =>
        OnLoopAsync(async () =>
        {
            Interlocked.Exchange(ref _budget, Math.Max(1L << 20, bytes));
            _findingBudget = false;
            if (State == BlotterState.Live)
            {
                await KeepWithinBudgetAsync().ConfigureAwait(false);
            }

            return true;
        });

    /// <summary>A page opens (or closes) its vital statistics: the host reads what only they show while any page has them open.</summary>
    public void WatchVitals(bool watching) => Interlocked.Add(ref _vitalsWatchers, watching ? 1 : -1);

    /// <summary>Waits until the host has read everything the streams have sent so far, and its scopes have taken it.</summary>
    public async Task CaughtUpAsync(CancellationToken ct)
    {
        var orders = _exchange!.Orders.Published;
        var fills = _exchange.Fills.Published;
        while (Interlocked.Read(ref _ordersRead) < orders || Interlocked.Read(ref _fillsRead) < fills)
        {
            await Task.Delay(20, ct).ConfigureAwait(false);
        }

        await OnLoopAsync(() => Task.FromResult(true)).ConfigureAwait(false);
    }

    /// <summary>The scopes open now, for the status page.</summary>
    public IReadOnlyList<LiveScope> Scopes => [.. _scopes.Values];

    /// <summary>Stops everything the host started; the web app's service and its container both call it, and the second does nothing.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        if (_alerts is not null)
        {
            await _alerts.DisposeAsync().ConfigureAwait(false);
        }

        foreach (var scope in _scopes.Values)
        {
            await scope.DisposeAsync().ConfigureAwait(false);
        }

        if (_exchange is not null)
        {
            await _exchange.DisposeAsync().ConfigureAwait(false);
        }

        if (_engine is not null)
        {
            await _engine.DisposeAsync().ConfigureAwait(false);
        }

        if (_planner is not null)
        {
            await _planner.DisposeAsync().ConfigureAwait(false);
        }

        if (_server is not null)
        {
            await _server.DisposeAsync().ConfigureAwait(false);
        }

        _stop.Dispose();
    }

    /// <summary>
    /// The catch-up loop, the one loop: it keeps the host caught up with both sides. It takes what the
    /// streams have sent, refreshes the tail and the alert tables with it, brings every scope and alert
    /// view to the refresh, and publishes; between batches, it runs what pages asked of it, and looks the
    /// tail up in the database when that is due, which a refresh carries to the views as well.
    /// </summary>
    private async Task RunAsync(CancellationToken ct)
    {
        var lastStatus = 0L;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await WaitForWorkAsync(ct).ConfigureAwait(false);
                while (_actions.Reader.TryRead(out var action))
                {
                    await action().ConfigureAwait(false);
                }

                if (State == BlotterState.Live)
                {
                    await IngestAsync([], [], ct).ConfigureAwait(false);
                    if (Stopwatch.GetElapsedTime(_lastConfirm) >= _options.ConfirmEvery)
                    {
                        _lastConfirm = Stopwatch.GetTimestamp();
                        await ConfirmAsync(ct).ConfigureAwait(false);
                    }

                    // A wait grows while nothing arrives: the views that watch it see the clock move.
                    if (_alerts!.WatchesTime && Stopwatch.GetElapsedTime(_lastRefresh) >= AlertTick)
                    {
                        await RefreshAsync(_ => { }, _ordersThrough, _fillsThrough, ct).ConfigureAwait(false);
                    }
                }
                else if (State == BlotterState.Refusing)
                {
                    Keep();
                    await ResumeWhenCaughtUpAsync(ct).ConfigureAwait(false);
                }

                await CloseIdleScopesAsync().ConfigureAwait(false);
                if (Stopwatch.GetElapsedTime(lastStatus).TotalMilliseconds > 500)
                {
                    lastStatus = Stopwatch.GetTimestamp();
                    Status = ReadStatus();
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception failure)
            {
                _log.LogError(failure, "the catch-up loop failed; serving stops until the database has caught up");
                await StopServingAsync($"the catch-up loop failed: {failure.Message}").ConfigureAwait(false);
            }
        }
    }

    private async Task WaitForWorkAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(100));
        var (orders, fills) = (_orderFeed, _fillFeed);
        try
        {
            List<Task> waits = [_actions.Reader.WaitToReadAsync(timeout.Token).AsTask()];
            if (orders is not null && fills is not null)
            {
                waits.Add(orders.WaitToReadAsync(timeout.Token).AsTask());
                waits.Add(fills.WaitToReadAsync(timeout.Token).AsTask());
            }

            await Task.WhenAny(waits).ConfigureAwait(false);

            // Batches that arrive together are refreshed together: a burst costs a few refreshes
            // rather than one per version.
            await Task.Delay(TimeSpan.FromMilliseconds(20), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// Takes what the streams have sent, and <paramref name="moreOrders"/> and <paramref name="moreFills"/>
    /// besides (the gap fill, and what was kept while refusing), into the tail: each order version the
    /// database does not hold yet, and each fill. Returns how many versions it took.
    /// </summary>
    private async Task<int> IngestAsync(IReadOnlyList<Order> moreOrders, IReadOnlyList<Fill> moreFills, CancellationToken ct)
    {
        // The two streams are read apart: a fill can come before the order it fills, or before the
        // version that counts it, and is taken as it comes.
        var versionsIn = new List<Order>(moreOrders);
        var fillsIn = new List<Fill>(moreFills);
        var (ordersThrough, fillsThrough) = (_ordersThrough, _fillsThrough);
        if (_orderFeed is not null && _fillFeed is not null)
        {
            var sent = versionsIn.Count;
            var sentFills = fillsIn.Count;
            while (_orderFeed.TryRead(out var batch))
            {
                versionsIn.AddRange(batch);
                ordersThrough = batch[^1].EventTime;
                Interlocked.Add(ref _ordersRead, batch.Length);
                NoteNewOrders(batch);
            }

            while (_fillFeed.TryRead(out var batch))
            {
                fillsIn.AddRange(batch);
                fillsThrough = batch[^1].EventTime;
                Interlocked.Add(ref _fillsRead, batch.Length);
            }

            // What the streams sent, not what a gap fill or a resume takes again.
            if (versionsIn.Count > sent || fillsIn.Count > sentFills)
            {
                Arrived?.Invoke(versionsIn[sent..], fillsIn[sentFills..]);
                _alerts!.Note(versionsIn[sent..], fillsIn[sentFills..]);
            }
        }

        if (versionsIn.Count == 0 && fillsIn.Count == 0)
        {
            return 0;
        }

        var started = Stopwatch.GetTimestamp();
        var fills = new List<Fill>();
        foreach (var fill in fillsIn)
        {
            if (_tail.GetFill(fill.FillId) is null)
            {
                _tail.AddFill(fill);
                fills.Add(fill);
            }
        }

        // An order new to the tail is looked up: the database may hold this version already, or a
        // later one, and then the tail has nothing to add. Ids are UUIDs, in no order the host relies
        // on, so every order it has not seen is looked up.
        var unknown = versionsIn.Where(v => _tail.Get(v.OrderId) is null).Select(v => v.OrderId).Distinct().ToList();
        var current = unknown.Count == 0 ? [] : await Queries.CurrentAsync(unknown, ct).ConfigureAwait(false);

        var upserts = new Dictionary<Guid, TailRow>();
        var versions = new List<Order>();
        foreach (var version in versionsIn.OrderBy(v => v.OrderId).ThenBy(v => v.Version))
        {
            var entry = _tail.Get(version.OrderId);
            if (entry is null)
            {
                var stored = current.GetValueOrDefault(version.OrderId);
                if (stored is not null && stored.Version >= version.Version)
                {
                    continue;
                }

                entry = new TailEntry(version, stored);
            }
            else if (version.Version <= entry.Live.Version)
            {
                // A version the tail has, or an older one: the gap fill overlaps the stream.
                continue;
            }

            entry.Live = version;
            _tail.Set(entry, version);
            versions.Add(version);
            upserts[version.OrderId] = TailRow.Of(version, entry.Base);
        }

        // What the streams sent goes into the alert tables in the same refresh as the tail.
        if (upserts.Count > 0 || fills.Count > 0 || _alerts!.Pending)
        {
            var tailRows = upserts.Values.ToList();
            await RefreshAsync(refresh =>
            {
                if (tailRows.Count > 0)
                {
                    refresh.Upsert(_tailTable!, tailRows).Upsert(_versionsTable!, versions);
                }

                if (fills.Count > 0)
                {
                    refresh.Upsert(_fillsTable!, fills);
                }

            }, ordersThrough, fillsThrough, ct).ConfigureAwait(false);
            Status = Status with
            {
                LastRefreshMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                LastRefreshVersions = versions.Count + fills.Count,
            };
        }
        else
        {
            (_ordersThrough, _fillsThrough) = (ordersThrough, fillsThrough);
        }

        await KeepWithinBudgetAsync().ConfigureAwait(false);
        return versions.Count;
    }

    /// <summary>
    /// Looks every row of the tail up in the database. An order the database holds at the tail's
    /// version or later, and a fill it holds, is confirmed, and leaves the tail once it has been for
    /// <see cref="BlotterOptions.EvictAfter"/>; an order the database has moved on for, but not as far
    /// as the tail, takes the database's row as its base.
    /// </summary>
    private async Task ConfirmAsync(CancellationToken ct)
    {
        var entries = _tail.Entries.ToList();
        var pending = _tail.Fills.ToList();
        if (entries.Count == 0 && pending.Count == 0)
        {
            return;
        }

        var current = entries.Count == 0
            ? []
            : await Queries.CurrentAsync([.. entries.Select(e => e.Live.OrderId)], ct).ConfigureAwait(false);
        var unconfirmed = pending.Where(f => _tail.FillConfirmedAt(f.FillId) is null).Select(f => f.FillId).ToList();
        var held = unconfirmed.Count == 0 ? [] : await Queries.FillsHeldAsync(unconfirmed, ct).ConfigureAwait(false);
        Status = Status with { Confirmations = Status.Confirmations + 1 };

        var now = DateTime.UtcNow;
        var upserts = new List<TailRow>();
        var deletes = new List<TailRow>();
        var versionDeletes = new List<Order>();
        foreach (var entry in entries)
        {
            var stored = current.GetValueOrDefault(entry.Live.OrderId);
            if (stored is not null && stored.Version >= entry.Live.Version)
            {
                _tail.Confirm(entry, now);
                if (now - entry.ConfirmedAt >= _options.EvictAfter)
                {
                    deletes.Add(TailRow.Of(entry.Live, entry.Base));
                    versionDeletes.AddRange(entry.Versions);
                    _tail.Remove(entry.Live.OrderId);
                }
                else if (stored.Version != (entry.Base?.Version ?? 0))
                {
                    // The database's row is now the base, so the scope counts the order once: the
                    // tail's row in place of the database's, not beside it.
                    versionDeletes.AddRange(_tail.Rebase(entry, stored));
                    upserts.Add(TailRow.Of(entry.Live, stored));
                }
            }
            else if ((stored?.Version ?? 0) != (entry.Base?.Version ?? 0))
            {
                versionDeletes.AddRange(_tail.Rebase(entry, stored));
                upserts.Add(TailRow.Of(entry.Live, stored));
            }
        }

        var fillDeletes = new List<Fill>();
        foreach (var fill in pending)
        {
            if (held.Contains(fill.FillId))
            {
                _tail.ConfirmFill(fill.FillId, now);
            }

            if (_tail.FillConfirmedAt(fill.FillId) is { } at && now - at >= _options.EvictAfter)
            {
                _tail.RemoveFill(fill);
                fillDeletes.Add(fill);
            }
        }

        if (upserts.Count == 0 && deletes.Count == 0 && versionDeletes.Count == 0 && fillDeletes.Count == 0)
        {
            return;
        }

        await RefreshAsync(refresh =>
        {
            if (upserts.Count > 0)
            {
                refresh.Upsert(_tailTable!, upserts);
            }

            if (deletes.Count > 0)
            {
                refresh.Delete(_tailTable!, deletes);
            }

            if (versionDeletes.Count > 0)
            {
                refresh.Delete(_versionsTable!, versionDeletes);
            }

            if (fillDeletes.Count > 0)
            {
                refresh.Delete(_fillsTable!, fillDeletes);
            }
        }, _ordersThrough, _fillsThrough, ct).ConfigureAwait(false);
        Status = Status with { Evicted = Status.Evicted + deletes.Count + fillDeletes.Count };
    }

    /// <summary>
    /// Past the budget, the scopes no page is using give way first. What is left cannot give way
    /// without dropping a version, so if it is still too much the host stops serving and says why.
    /// </summary>
    private async Task KeepWithinBudgetAsync()
    {
        Pages.MaxKeptRows = PageRows();
        if (LiveBytes() <= _budget)
        {
            return;
        }

        foreach (var idle in _scopes.Values.Where(s => s.Pages == 0).ToList())
        {
            await CloseAsync(idle).ConfigureAwait(false);
            if (LiveBytes() <= _budget)
            {
                return;
            }
        }

        // While no budget is chosen, the host takes the next one up rather than refuse.
        if (RaiseBudget($"the tail and {_scopes.Count} live scopes hold {Megabytes(LiveBytes())}"))
        {
            await KeepWithinBudgetAsync().ConfigureAwait(false);
            return;
        }

        // What is in the tail is what the database has not been seen to hold: the gap the host covers.
        var lag = _tail.OldestUnconfirmed is { } oldest ? DateTime.UtcNow - DateTime.SpecifyKind(oldest, DateTimeKind.Utc) : TimeSpan.Zero;
        _refusedBehind = lag;
        await StopServingAsync(string.Create(CultureInfo.InvariantCulture,
            $"the database is {lag.TotalSeconds:F0} s behind the streams: covering it takes a tail of "
            + $"{_tail.Count:N0} orders and {_tail.FillCount:N0} fills, {Megabytes(_tail.Bytes)}, and {_scopes.Count} live scopes over it "
            + $"{Megabytes(LiveBytes() - _tail.Bytes)}, more than the {Megabytes(_budget)} budget")).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts serving: subscribes to the streams if it has not, then sends the database one last
    /// request, the gap fill, for every version written lately, and takes those with whatever was
    /// kept while refusing as if the stream had sent them. Refused if the tail that makes does not fit.
    /// </summary>
    private async Task GoLiveAsync(CancellationToken ct)
    {
        if (_orderFeed is null || _fillFeed is null)
        {
            _orderFeed = _exchange!.Orders.Subscribe(out var ordersBefore);
            _fillFeed = _exchange.Fills.Subscribe(out var fillsBefore);
            (_ordersRead, _fillsRead) = (ordersBefore, fillsBefore);
            (_ordersThrough, _fillsThrough) = (Cells.Micros(DateTime.UtcNow), Cells.Micros(DateTime.UtcNow));
        }

        var gap = await Queries.VersionsSinceAsync(Cells.Micros(DateTime.UtcNow - _options.GapFill), ct).ConfigureAwait(false);
        Order[] kept = [.. _keptOrders];
        Fill[] keptFills = [.. _keptFills];
        _keptOrders.Clear();
        _keptFills.Clear();

        State = BlotterState.Live;
        Refusal = null;
        var taken = await IngestAsync([.. gap, .. kept], keptFills, ct).ConfigureAwait(false);
        Status = ReadStatus() with { GapRead = gap.Length, GapFilled = Status.GapFilled + taken };
        if (State == BlotterState.Live)
        {
            _log.LogInformation("the gap fill read {Read:N0} versions written in the last {Seconds:F0} s; the tail took {Taken:N0}",
                gap.Length, _options.GapFill.TotalSeconds, taken);
        }
    }

    /// <summary>While refusing: what the streams send is kept, as far back as the gap fill reaches, for when serving starts again.</summary>
    private void Keep()
    {
        while (_orderFeed!.TryRead(out var batch))
        {
            _keptOrders.AddRange(batch);
            _ordersThrough = batch[^1].EventTime;
            Interlocked.Add(ref _ordersRead, batch.Length);
            NoteNewOrders(batch);
        }

        while (_fillFeed!.TryRead(out var batch))
        {
            _keptFills.AddRange(batch);
            _fillsThrough = batch[^1].EventTime;
            Interlocked.Add(ref _fillsRead, batch.Length);
        }

        var horizon = Cells.Micros(DateTime.UtcNow - _options.GapFill);
        _keptOrders.RemoveAll(o => o.EventTime < horizon);
        _keptFills.RemoveAll(f => f.EventTime < horizon);
    }

    /// <summary>
    /// Serves again once <c>orders_current</c> has caught up: its newest order is no further behind
    /// the newest the stream has sent than <see cref="BlotterOptions.ResumeWithin"/>, nor than half as
    /// far as the database was behind when the host refused, so that it does not refuse again at once.
    /// </summary>
    private async Task ResumeWhenCaughtUpAsync(CancellationToken ct)
    {
        if (Stopwatch.GetElapsedTime(_lastResumeCheck) < TimeSpan.FromSeconds(1))
        {
            return;
        }

        _lastResumeCheck = Stopwatch.GetTimestamp();
        var newest = await Queries.NewestOrderAsync(ct).ConfigureAwait(false);
        var within = _refusedBehind > TimeSpan.Zero && _refusedBehind / 2 < _options.ResumeWithin ? _refusedBehind / 2 : _options.ResumeWithin;
        if (newest is not null && _newestOrderSent - newest.OrderTime > within)
        {
            return;
        }

        await GoLiveAsync(ct).ConfigureAwait(false);
        if (State == BlotterState.Live)
        {
            _log.LogInformation("serving again");
            Published?.Invoke(_version);
        }
    }

    /// <summary>
    /// Stops serving: empties the tail and closes every scope, so that nothing a page could read is
    /// left half true. What of the tail the database has not been seen to hold is kept, and the streams
    /// are still read, what they send kept after it, for when serving starts again: the gap fill finds
    /// only what the database has written.
    /// </summary>
    private async Task StopServingAsync(string why)
    {
        _log.LogWarning("refusing to serve: {Why}", why);
        State = BlotterState.Refusing;
        Refusal = why;
        foreach (var scope in _scopes.Values.ToList())
        {
            await CloseAsync(scope).ConfigureAwait(false);
        }

        var all = _tail.Entries.ToList();
        var deletes = all.Select(e => TailRow.Of(e.Live, e.Base)).ToList();
        var versionDeletes = all.SelectMany(e => e.Versions).ToList();
        var fillDeletes = _tail.Fills.ToList();
        _keptOrders.InsertRange(0, all.Where(e => e.ConfirmedAt is null).SelectMany(e => e.Versions));
        _keptFills.InsertRange(0, fillDeletes.Where(f => _tail.FillConfirmedAt(f.FillId) is null));
        _tail.Clear();
        await RefreshAsync(refresh =>
        {
            if (deletes.Count > 0)
            {
                refresh.Delete(_tailTable!, deletes);
            }

            if (versionDeletes.Count > 0)
            {
                refresh.Delete(_versionsTable!, versionDeletes);
            }

            if (fillDeletes.Count > 0)
            {
                refresh.Delete(_fillsTable!, fillDeletes);
            }
        }, _ordersThrough, _fillsThrough, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>How far the host has read each stream, now: by time, and by count.</summary>
    private StreamMark Mark() =>
        new(_ordersThrough, _fillsThrough, Interlocked.Read(ref _ordersRead), Interlocked.Read(ref _fillsRead));

    /// <summary>One refresh of the engine, then every scope brought to it, then the pages told.</summary>
    private async Task RefreshAsync(Action<RefreshBuilder> changes, DateTime ordersThrough, DateTime fillsThrough, CancellationToken ct)
    {
        var next = _version + 1;
        var now = DateTime.UtcNow;

        // The clock moves only forward, by a tick at least, however the wall clock steps.
        _clock = now > _clock ? now : _clock.AddTicks(1);
        await _engine!.RefreshAsync(refresh =>
        {
            changes(refresh.Version(next));
            _alerts!.AddTo(refresh, _tail, now);
            refresh.AdvanceClock(_clock);
        }, ct).ConfigureAwait(false);
        _version = next;
        _lastRefresh = Stopwatch.GetTimestamp();
        (_ordersThrough, _fillsThrough) = (ordersThrough, fillsThrough);
        var mark = Mark();
        foreach (var scope in _scopes.Values)
        {
            await scope.UpdateAsync(_tail, _version, mark, ct).ConfigureAwait(false);
        }

        await _alerts!.UpdateAsync(_tail, ct).ConfigureAwait(false);

        Published?.Invoke(_version);
    }

    private void NoteNewOrders(Order[] batch)
    {
        foreach (var order in batch)
        {
            if (order.Version == 1 && order.OrderTime > _newestOrderSent)
            {
                _newestOrderSent = order.OrderTime;
            }
        }
    }

    private async Task CloseIdleScopesAsync()
    {
        foreach (var scope in _scopes.Values.Where(s => s.Pages == 0 && s.Idle is { } idle
            && DateTime.UtcNow - idle > _options.ScopeGrace).ToList())
        {
            await CloseAsync(scope).ConfigureAwait(false);
        }
    }

    private async Task CloseAsync(LiveScope scope)
    {
        _scopes.Remove(scope.Name);
        await scope.DisposeAsync().ConfigureAwait(false);
    }

    private void RefuseUnlessLive()
    {
        if (State != BlotterState.Live)
        {
            throw new BlotterRefusedException(Refusal ?? "the blotter is starting");
        }
    }

    /// <summary>Runs <paramref name="work"/> on the catch-up loop, between refreshes, and returns its result.</summary>
    private Task<T> OnLoopAsync<T>(Func<Task<T>> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _actions.Writer.TryWrite(async () =>
        {
            try
            {
                done.SetResult(await work().ConfigureAwait(false));
            }
            catch (Exception failure)
            {
                done.SetException(failure);
            }
        });
        return done.Task;
    }

    private BlotterStatus ReadStatus()
    {
        var scopes = _scopes.Values.ToList();
        var scopeBytes = scopes.Sum(s => s.ViewBytes + s.State.Bytes);
        return Status with
        {
            State = State,
            Refusal = Refusal,
            Version = _version,
            Sync = _options.Current,
            TimeLimit = _options.QueryTimeLimit,
            Behind = _tail.OldestUnconfirmed is { } oldest ? DateTime.UtcNow - DateTime.SpecifyKind(oldest, DateTimeKind.Utc) : null,
            NotYetIngested = _exchange?.NotYetIngested ?? 0,
            CurrentWaiting = _exchange?.CurrentWaiting ?? 0,
            IngestionStalled = _exchange?.IngestionStalled ?? false,
            CurrentStalled = _exchange?.CurrentStalled ?? false,
            FeedRate = _exchange?.CurrentRate ?? 0,
            Published = (_exchange?.Orders.Published ?? 0) + (_exchange?.Fills.Published ?? 0),
            OrdersHeld = _exchange?.OrdersHeld ?? 0,
            SentLate = _exchange?.SentLate ?? 0,
            SentAfterDatabase = _exchange?.SentAfterDatabase ?? 0,
            TailOrders = _tail.Count,
            TailFills = _tail.FillCount,
            TailConfirmed = _tail.Confirmed,
            TimedOut = Queries.TimedOut,
            TailVersions = _tail.VersionCount,
            TailBytes = _tail.Bytes,
            Scopes = scopes.Count,
            ScopeBytes = scopeBytes,
            PageBytes = Pages.Bytes,
            AlertBytes = _alerts?.Bytes ?? 0,
            PageReads = Queries.Reads,
            PageLookups = Pages.Lookups,
            PageHits = Pages.WindowHits,
            ReadAheads = Pages.ReadAheads,
            Statements = Queries.Executed,
            Budget = _budget,
            UsedBytes = _tail.Bytes + scopeBytes + Pages.Bytes + (_alerts?.Bytes ?? 0),
            DatabaseBytes = Volatile.Read(ref _vitalsWatchers) > 0 ? SafeDatabaseBytes() : Status.DatabaseBytes,
            ManagedHeapBytes = GC.GetTotalMemory(forceFullCollection: false),
        };
    }

    private long SafeDatabaseBytes()
    {
        try
        {
            return _store?.SizeBytes() ?? 0;
        }
        catch (NpgsqlException)
        {
            return Status.DatabaseBytes;
        }
    }

    /// <summary>While no budget is chosen, raises it to the next step, if there is one.</summary>
    private bool RaiseBudget(string why)
    {
        var next = _options.BudgetSteps.FirstOrDefault(step => step > _budget);
        if (!_findingBudget || next == 0)
        {
            return false;
        }

        _log.LogInformation("raising the budget from {From} to {To}: {Why}", Megabytes(_budget), Megabytes(next), why);
        Interlocked.Exchange(ref _budget, next);
        if (Pages is not null)
        {
            Pages.MaxKeptRows = PageRows();
        }

        return true;
    }

    /// <summary>What the host cannot give back without dropping a version: the tail, and the scopes over it.</summary>
    private long LiveBytes() =>
        _tail.Bytes + _scopes.Values.Sum(s => s.ViewBytes + s.State.Bytes) + (_alerts?.Bytes ?? 0);

    /// <summary>The rows the kept windows may hold: their share of the budget, or what the tail and the scopes leave, whichever is less.</summary>
    private long PageRows()
    {
        var left = Math.Max(0, _budget - LiveBytes());
        var share = (long)(_budget * _options.PageShare);
        return Math.Min(left, share) / Order.Bytes;
    }

    /// <summary>What another scope would hold: a row for every order in the tail, at what the open ones cost a row.</summary>
    private long ScopeEstimate()
    {
        var rows = _scopes.Values.Sum(s => (long)s.ViewRows);
        var bytes = _scopes.Values.Sum(s => s.ViewBytes + s.State.Bytes);
        var perRow = rows > 256 ? bytes / rows : 512;
        return Math.Max(256 << 10, _tail.Count * perRow);
    }

    /// <summary>An order version's columns as the database holds them: times to the microsecond.</summary>
    private static readonly ColumnDescriptor[] OrderColumns =
    [
        Column("order_id", ChalkType.Uuid()),
        Column("version", ChalkType.Int32()),
        Column("event_time", ChalkType.Timestamp(6)),
        Column("order_time", ChalkType.Timestamp(6)),
        Column("cl_ord_id", ChalkType.String()),
        Column("client", ChalkType.String()),
        Column("region", ChalkType.String()),
        Column("channel", ChalkType.String()),
        Column("instrument", ChalkType.String()),
        Column("side", ChalkType.String()),
        Column("qty", ChalkType.Float64()),
        Column("filled_qty", ChalkType.Float64()),
        Column("avg_price", ChalkType.Float64()),
        Column("markup_bps", ChalkType.Float64()),
        Column("status", ChalkType.String()),
    ];

    private static readonly ColumnDescriptor[] FillColumns =
    [
        Column("fill_id", ChalkType.Uuid()),
        Column("order_id", ChalkType.Uuid()),
        Column("event_time", ChalkType.Timestamp(6)),
        Column("cl_ord_id", ChalkType.String()),
        Column("client", ChalkType.String()),
        Column("region", ChalkType.String()),
        Column("channel", ChalkType.String()),
        Column("instrument", ChalkType.String()),
        Column("side", ChalkType.String()),
        Column("qty", ChalkType.Float64()),
        Column("price", ChalkType.Float64()),
        Column("mid", ChalkType.Float64()),
        Column("markup_bps", ChalkType.Float64()),
    ];

    private static ColumnDescriptor Column(string name, ChalkType type) => new() { Name = name, Type = type };

    /// <summary>
    /// The connection string the blotter reads with: every statement on it is stopped by PostgreSQL
    /// itself at the time limit (<c>statement_timeout</c>), so a read past it frees the server's
    /// workers too, and Npgsql gives up connecting or waiting at the same limit. The seed, the
    /// market's writers and the oracle connect without it: building an index takes longer.
    /// </summary>
    private static string Reading(string connectionString, TimeSpan limit)
    {
        var seconds = (int)Math.Ceiling(limit.TotalSeconds);
        return new NpgsqlConnectionStringBuilder(connectionString)
        {
            Timeout = seconds,
            CommandTimeout = seconds,
            Options = string.Create(CultureInfo.InvariantCulture, $"-c statement_timeout={(long)limit.TotalMilliseconds}"),
        }.ConnectionString;
    }

    internal static string Megabytes(long bytes) => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1048576.0:F1} MB");

    private static string? JavaHome()
    {
        var configured = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        const string homebrew = "/opt/homebrew/opt/openjdk@21/libexec/openjdk.jdk/Contents/Home";
        return Directory.Exists(homebrew) ? homebrew : null;
    }
}

/// <summary>What the page's status bar shows.</summary>
public sealed record BlotterStatus
{
    public BlotterState State { get; init; }

    public string? Refusal { get; init; }

    public long Version { get; init; }

    /// <summary>How <c>orders_current</c> keeps up with <c>order_versions</c>.</summary>
    public CurrentSync Sync { get; init; }

    /// <summary>The longest any statement may run before it is stopped.</summary>
    public TimeSpan TimeLimit { get; init; }

    /// <summary>How long ago the oldest row the database has not been seen to hold was sent: how far behind it is, at least.</summary>
    public TimeSpan? Behind { get; init; }

    /// <summary>Order versions that have happened and that the order stream, running behind the fills, has not sent yet.</summary>
    public int OrdersHeld { get; init; }

    /// <summary>Rows the streams have sent late, and after the database had written them, while out of order.</summary>
    public long SentLate { get; init; }

    public long SentAfterDatabase { get; init; }

    public int NotYetIngested { get; init; }

    /// <summary>Versions written that the lagging job has not applied to <c>orders_current</c> yet.</summary>
    public int CurrentWaiting { get; init; }

    public bool IngestionStalled { get; init; }

    public bool CurrentStalled { get; init; }

    public double FeedRate { get; init; }

    public long Published { get; init; }

    public int TailOrders { get; init; }

    public int TailFills { get; init; }

    /// <summary>Rows of the tail the database has been seen to hold, waiting to leave.</summary>
    public int TailConfirmed { get; init; }

    public long TimedOut { get; init; }

    public long TailVersions { get; init; }

    public long TailBytes { get; init; }

    public int Scopes { get; init; }

    public long ScopeBytes { get; init; }

    public long PageBytes { get; init; }

    /// <summary>The alert views and the tables they read.</summary>
    public long AlertBytes { get; init; }

    public long PageReads { get; init; }

    public long PageLookups { get; init; }

    /// <summary>Reads a kept window answered, and windows read ahead of the scrolling.</summary>
    public long PageHits { get; init; }

    public long ReadAheads { get; init; }

    public long Statements { get; init; }

    public long Budget { get; init; }

    public long UsedBytes { get; init; }

    public long DatabaseBytes { get; init; }

    public long ManagedHeapBytes { get; init; }

    public double LastRefreshMs { get; init; }

    public int LastRefreshVersions { get; init; }

    /// <summary>Lookups of the whole tail run, and rows they let leave it.</summary>
    public long Confirmations { get; init; }

    public long Evicted { get; init; }

    /// <summary>Versions the last gap fill read, and versions gap fills have added to the tail in all.</summary>
    public int GapRead { get; init; }

    public long GapFilled { get; init; }
}

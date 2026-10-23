using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using Chalk.Catalog;
using Chalk.Client;
using Chalk.Sample.Blotter.Market;
using Chalk.Sources;
using Chalk.Sources.Ado;
using Chalk.Sources.DuckDb;
using Chalk.Sources.Poco;
using DuckDB.NET.Data;
using Microsoft.Extensions.Logging;

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
/// The blotter's host: the corpus and the simulated market, the ChalkQL engine over the database
/// and the tail, and the one loop that changes the tail.
/// </summary>
/// <remarks>
/// <para>
/// A page is a snapshot generation and the live tail, merged. The generation is the latest version
/// of each order as of the orders' watermark, written into the database once, and the fills at or
/// before the fills' watermark; the tail is every version and fill the two streams have sent since,
/// replayed and then live, each stream read apart from the other. An order's version in both is told
/// apart by its number, the higher winning, and a fill by the watermark, so neither the database's
/// lag nor the in-flight crossing between the two can show a stale or a missing row.
/// </para>
/// <para>
/// The tail grows until a newer generation takes over from it: the host reads the watermarks again,
/// has the database write a new generation, and in one refresh drops every order and fill the new
/// generation has caught up with. When the database is so far behind that the tail and its scopes
/// would outgrow the budget, the host stops serving, says why, and starts again from a new generation
/// and a new replay once the database has caught up. It never drops a version to stay in budget.
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
    private readonly List<long> _generationsInDatabase = [];
    private TradeStore? _store;
    private Exchange? _exchange;
    private PlannerProcess? _planner;
    private ChalkEngine? _engine;
    private AdoTable? _snapshotsTable;
    private PocoTable<TailRow>? _tailTable;
    private PocoTable<Fill>? _fillsTable;
    private PocoTable<Order>? _versionsTable;
    private ChannelReader<Order[]>? _orderFeed;
    private ChannelReader<Fill[]>? _fillFeed;
    private Task? _loop;
    private Task<Generation>? _building;
    private Generation? _generation;
    private long _version;
    private long _lastGeneration;
    private long _budget;
    private DateTime _ordersThrough;
    private DateTime _fillsThrough;
    private DateTime _generationTakenAt;

    public BlotterHost(BlotterOptions options, ILogger<BlotterHost> log)
    {
        _options = options;
        _log = log;
        _budget = options.BudgetBytes;
    }

    public BlotterState State { get; private set; } = BlotterState.Starting;

    /// <summary>Why the host is not serving, when it is not.</summary>
    public string? Refusal { get; private set; }

    public Exchange Exchange => _exchange ?? throw new InvalidOperationException("the host has not started");

    /// <summary>The database, for the checks that read it directly.</summary>
    public TradeStore Store => _store ?? throw new InvalidOperationException("the host has not started");

    public SnapshotQueries Queries { get; private set; } = null!;

    /// <summary>The engine, for the probe's own statements.</summary>
    internal ChalkEngine Engine => _engine ?? throw new InvalidOperationException("the host has not started");

    public Pages Pages { get; private set; } = null!;

    public Generation? Generation => _generation;

    public BlotterOptions Options => _options;

    /// <summary>The budget now; the page may change it.</summary>
    public long Budget => Interlocked.Read(ref _budget);

    /// <summary>Raised after every refresh the scopes have taken, with its version.</summary>
    public event Action<long>? Published;

    /// <summary>Counters for the page's status bar, refreshed by the loop.</summary>
    public BlotterStatus Status { get; private set; } = new();

    public async Task StartAsync(CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        _store = new TradeStore(_options.DatabaseBytes);
        var (ordersThrough, fillsThrough) = _store.Generate(
            _options.Orders, Cells.Micros(DateTime.UtcNow), TimeSpan.FromDays(5), openTail: 2000);
        _log.LogInformation("generated {Orders:N0} orders in {Seconds:F1} s", _options.Orders, _store.GenerationTime.TotalSeconds);

        // The market starts before the blotter: events are in flight, sent and not yet written,
        // when the blotter takes its first snapshot, as they would be at any real start.
        _exchange = new Exchange(
            _store, _options.Exchange, _options.Orders, ordersThrough, fillsThrough, _store.OpenOrders());
        _exchange.Start();

        _planner = await PlannerProcess.StartAsync(new PlannerProcessOptions { JavaHome = JavaHome() }).ConfigureAwait(false);
        var store = _store;
        var database = DuckDbSources.AddDuckDbSource("db", () => new DuckDBConnection(store.ConnectionString))
            .Capabilities(AdoCapabilities.For(DialectProfiles.DuckDb))

            // No statement may run longer than the host's time limit, in the database or in ChalkQL.
            .Options(new SourceOptions { QueryTimeout = _options.QueryTimeLimit })

            // The planner prices a page by the table's size: a table it thinks small it reads whole and
            // sorts itself. Counted exactly, at build and at every refresh that names the source.
            .RowCounts(AdoSourceBuilder.RowCountMode.Exact)

            // Declared rather than discovered: DuckDB does not report a TIMESTAMP's precision, and a
            // discovered one is taken as nanoseconds, finer than DuckDB keeps, so the planner would
            // sort every time locally rather than trust DuckDB with it (F274). The tables are in
            // DuckDB's main schema because a pushed query names a table by its catalog name (F273).
            .AddTable("snapshots", [Column("generation", ChalkType.Int64()), .. OrderColumns], out AdoTable snapshotsTable,
                configure: t => t.UniqueKey("generation", "order_id"))
            .AddTable("order_versions", OrderColumns, configure: t => t.UniqueKey("order_id", "version"))
            .AddTable("fills", FillColumns, configure: t => t.UniqueKey("fill_id"))
            .AddTable("ingest_watermark", [Column("complete_through", ChalkType.Timestamp(6))])
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
            .Build();
        _snapshotsTable = snapshotsTable;
        _tailTable = tail;
        _versionsTable = versions;
        _fillsTable = fills;
        _engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "blotter",
            Sources = [database, live],
            Planner = _planner.CreatePlanner(),
        }, ct).ConfigureAwait(false);
        Queries = new SnapshotQueries(_engine, _options.QueryTimeLimit);
        Pages = new Pages(Queries) { MaxCachedRows = PageRows() };

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

            if (LiveBytes() + estimate > _budget)
            {
                throw new BlotterRefusedException(string.Create(CultureInfo.InvariantCulture,
                    $"no live scope for this filter: another would hold about {Megabytes(estimate)}, and the tail and "
                    + $"{_scopes.Count} open scopes already hold {Megabytes(LiveBytes())} of the {Megabytes(_budget)} budget"));
            }

            var scope = await LiveScope.CreateAsync(_engine!, tab, filter, _budget, _options.Maintenance, ct).ConfigureAwait(false);
            await scope.UpdateAsync(_tail, _version, _generation!, _ordersThrough, _fillsThrough, ct).ConfigureAwait(false);
            if (scope.Fault is { } fault)
            {
                await scope.DisposeAsync().ConfigureAwait(false);
                throw new BlotterRefusedException($"the live scope for this filter could not start: {fault.Message}");
            }

            scope.Pages = 1;
            _scopes[name] = scope;
            return scope;
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
            if (State == BlotterState.Live)
            {
                await KeepWithinBudgetAsync(_stop.Token).ConfigureAwait(false);
            }

            return true;
        });

    /// <summary>The scopes open now, for the status page.</summary>
    public IReadOnlyList<LiveScope> Scopes => [.. _scopes.Values];

    public async ValueTask DisposeAsync()
    {
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

        _store?.Dispose();
        _stop.Dispose();
    }

    /// <summary>
    /// The one loop: takes what the feed has sent, refreshes the tail with it, brings every scope to
    /// the refresh, and publishes; between batches, runs what pages asked of it, and turns the
    /// generation over when it is due.
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
                    await IngestAsync(ct).ConfigureAwait(false);
                    await TurnOverWhenDueAsync(ct).ConfigureAwait(false);
                }
                else if (State == BlotterState.Refusing)
                {
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
                _log.LogError(failure, "the ingest loop failed; serving stops until the database is read again");
                await StopServingAsync($"the ingest loop failed: {failure.Message}").ConfigureAwait(false);
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

    private async Task IngestAsync(CancellationToken ct)
    {
        if (_orderFeed is null || _fillFeed is null)
        {
            return;
        }

        // The two streams are read apart: a fill can come before the order it fills, or before the
        // version that counts it, and is taken as it comes.
        var versionsIn = new List<Order>();
        while (_orderFeed.TryRead(out var batch))
        {
            versionsIn.AddRange(batch);
        }

        var fillsIn = new List<Fill>();
        while (_fillFeed.TryRead(out var batch))
        {
            fillsIn.AddRange(batch);
        }

        if (versionsIn.Count == 0 && fillsIn.Count == 0)
        {
            return;
        }

        // Each stream is read in order, so this is how far the tail now covers it: every row up to
        // here is in the generation or in the tail once this refresh commits.
        var ordersThrough = versionsIn.Count > 0 ? versionsIn[^1].EventTime : _ordersThrough;
        var fillsThrough = fillsIn.Count > 0 ? fillsIn[^1].EventTime : _fillsThrough;

        var started = Stopwatch.GetTimestamp();
        var generation = _generation!;

        // A fill at or before the generation's fill watermark is in the database already: the crossing.
        var fills = new List<Fill>();
        foreach (var fill in fillsIn)
        {
            if (fill.EventTime > generation.FillsThrough && _tail.GetFill(fill.FillId) is null)
            {
                _tail.AddFill(fill);
                fills.Add(fill);
            }
        }

        // Orders new to the tail need the generation's own row, to know whether the version is
        // newer than what the snapshot holds and which of the snapshot's rows it overrides. Ids are
        // UUIDs, in no order the host relies on, so every order it has not seen is looked up.
        var unknown = versionsIn
            .Where(v => _tail.Get(v.OrderId) is null)
            .Select(v => v.OrderId)
            .Distinct()
            .ToList();
        var snapshotRows = unknown.Count == 0
            ? []
            : await Queries.LookupAsync(generation, unknown, ct).ConfigureAwait(false);

        var upserts = new Dictionary<Guid, TailRow>();
        var versions = new List<Order>();
        foreach (var version in versionsIn.OrderBy(v => v.OrderId).ThenBy(v => v.Version))
        {
            var entry = _tail.Get(version.OrderId);
            var snapshot = entry is null ? snapshotRows.GetValueOrDefault(version.OrderId) : entry.Base;

            // The crossing: the generation already has this version or a later one.
            if (snapshot is not null && version.Version <= snapshot.Version)
            {
                continue;
            }

            // A version the tail already has, or one older than it: a replay can overlap the live stream.
            if (entry is not null && version.Version <= entry.Live.Version)
            {
                continue;
            }

            entry ??= new TailEntry(version, snapshot);
            entry.Live = version;
            _tail.Set(entry, version);
            versions.Add(version);
            upserts[version.OrderId] = TailRow.Of(version, snapshot);
        }

        if (upserts.Count == 0 && fills.Count == 0)
        {
            (_ordersThrough, _fillsThrough) = (ordersThrough, fillsThrough);
            return;
        }

        var tailRows = upserts.Values.ToList();
        var next = _version + 1;
        await _engine!.RefreshAsync(
            refresh =>
            {
                refresh.Version(next);
                if (tailRows.Count > 0)
                {
                    refresh.Upsert(_tailTable!, tailRows).Upsert(_versionsTable!, versions);
                }

                if (fills.Count > 0)
                {
                    refresh.Upsert(_fillsTable!, fills);
                }
            },
            ct).ConfigureAwait(false);
        _version = next;
        (_ordersThrough, _fillsThrough) = (ordersThrough, fillsThrough);
        await PublishAsync(ct).ConfigureAwait(false);
        Status = Status with { LastRefreshMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds, LastRefreshVersions = versions.Count + fills.Count };

        await KeepWithinBudgetAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Past the budget, the page cache gives way first, then the scopes no page is using, then the
    /// tail, by a new generation taken at once, which drops every order the database has caught up
    /// with. What is left cannot give way without dropping a version, so if it is still too much the
    /// host stops serving and says why.
    /// </summary>
    private async Task KeepWithinBudgetAsync(CancellationToken ct)
    {
        Pages.MaxCachedRows = PageRows();
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

        if (Moved())
        {
            var next = _building is { } building
                ? await building.ConfigureAwait(false)
                : await StartBuild(ct).ConfigureAwait(false);
            _building = null;
            await SwitchToAsync(next, ct).ConfigureAwait(false);
            Pages.MaxCachedRows = PageRows();
            if (LiveBytes() <= _budget)
            {
                return;
            }
        }

        // What is left in the tail is what the database does not have yet: the gap the host covers.
        var lag = DateTime.UtcNow - _exchange!.CompleteThrough;
        await StopServingAsync(string.Create(CultureInfo.InvariantCulture,
            $"the database is {lag.TotalSeconds:F0} s behind the feed: covering the gap takes a tail of "
            + $"{_tail.Count:N0} orders and {_tail.FillCount:N0} fills, {Megabytes(_tail.Bytes)}, and {_scopes.Count} live scopes over it "
            + $"{Megabytes(LiveBytes() - _tail.Bytes)}, more than the {Megabytes(_budget)} budget")).ConfigureAwait(false);
    }

    /// <summary>Whether the database has moved past the current generation on either stream.</summary>
    private bool Moved() =>
        _exchange!.OrdersThrough > _generation!.OrdersThrough || _exchange.FillsThrough > _generation.FillsThrough;

    /// <summary>A new generation when the tail has grown or the current one has aged, if the database has moved on.</summary>
    private async Task TurnOverWhenDueAsync(CancellationToken ct)
    {
        if (_building is { IsCompleted: true } built)
        {
            _building = null;
            await SwitchToAsync(await built.ConfigureAwait(false), ct).ConfigureAwait(false);
            return;
        }

        var due = LiveBytes() > _budget * _options.TurnoverAt
            || DateTime.UtcNow - _generationTakenAt > _options.TurnoverEvery;
        if (_building is null && due && Moved())
        {
            _building = StartBuild(ct);
        }
    }

    /// <summary>
    /// Starts the next generation's build in the background. Numbers are never used twice, and
    /// each is recorded before its rows exist, so that whatever becomes of the build, its rows are
    /// deleted with the generations the host no longer reads.
    /// </summary>
    private Task<Generation> StartBuild(CancellationToken ct)
    {
        var number = ++_lastGeneration;
        _generationsInDatabase.Add(number);
        return Task.Run(() => Build(number), ct);
    }

    /// <summary>
    /// Has the database write generation <paramref name="number"/>: the watermark first, then every
    /// order's latest version, so the generation holds at least everything through the watermarks.
    /// </summary>
    private Generation Build(long number)
    {
        var started = Stopwatch.GetTimestamp();
        using var connection = _store!.Open();
        var (ordersThrough, fillsThrough) = _store.Watermarks(connection);
        using var insert = connection.CreateCommand();
        insert.CommandText = string.Create(CultureInfo.InvariantCulture, $"""
            INSERT INTO snapshots
            SELECT {number}, {Order.Select} FROM order_versions
            QUALIFY row_number() OVER (PARTITION BY order_id ORDER BY version DESC) = 1
            ORDER BY order_id
            """);
        var rows = insert.ExecuteNonQuery();
        return new Generation(number, ordersThrough, fillsThrough, rows, Stopwatch.GetElapsedTime(started));
    }

    /// <summary>
    /// Moves the tail onto a new generation, in one refresh: an order the generation has caught up
    /// with leaves the tail; one it has not stays, over the generation's row for it. Then every scope
    /// takes the refresh, and every page reads the new generation from its next state on.
    /// </summary>
    private async Task SwitchToAsync(Generation next, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var entries = _tail.Entries.ToList();
        var known = entries.Select(e => e.Live.OrderId).ToList();
        var snapshotRows = known.Count == 0
            ? []
            : await Queries.LookupAsync(next, known, ct).ConfigureAwait(false);

        var upserts = new List<TailRow>();
        var deletes = new List<TailRow>();
        var versionDeletes = new List<Order>();
        foreach (var entry in entries)
        {
            var snapshot = snapshotRows.GetValueOrDefault(entry.Live.OrderId);
            if (snapshot is not null && snapshot.Version >= entry.Live.Version)
            {
                deletes.Add(TailRow.Of(entry.Live, entry.Base));
                versionDeletes.AddRange(entry.Versions);
                _tail.Remove(entry.Live.OrderId);
                continue;
            }

            entry.Base = snapshot;
            versionDeletes.AddRange(_tail.Trim(entry, snapshot?.Version ?? 0));
            upserts.Add(TailRow.Of(entry.Live, snapshot));
        }

        // Fills at or before the new watermark are the new generation's.
        var fillDeletes = _tail.TrimFills(next.FillsThrough);

        var version = _version + 1;
        await _engine!.RefreshAsync(
            refresh =>
            {
                // The snapshots table has grown by a generation: counted again, so plans stay priced right.
                refresh.Version(version).Refresh(_snapshotsTable!);
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
            },
            ct).ConfigureAwait(false);
        _version = version;
        var previous = _generation;
        _generation = next;
        _generationTakenAt = DateTime.UtcNow;
        await PublishAsync(ct).ConfigureAwait(false);

        // Pages still reading the previous generation finish on it; the one before it goes.
        var keep = new HashSet<long> { next.Number };
        if (previous is not null)
        {
            keep.Add(previous.Number);
        }

        Pages.Retire(keep);
        foreach (var old in _generationsInDatabase.Where(g => !keep.Contains(g)).ToList())
        {
            _store!.Execute(string.Create(CultureInfo.InvariantCulture, $"DELETE FROM snapshots WHERE generation = {old}"));
            _generationsInDatabase.Remove(old);
        }

        Status = Status with
        {
            Turnovers = Status.Turnovers + 1,
            LastBuildMs = next.BuildTime.TotalMilliseconds,
            LastSwitchMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            LastEvicted = deletes.Count,
        };
    }

    /// <summary>
    /// Starts serving from nothing: a generation as of the watermark, and the feed replayed from
    /// it. Refused when the replay alone would outgrow the tail's share of the budget.
    /// </summary>
    private async Task GoLiveAsync(CancellationToken ct)
    {
        var generation = await StartBuild(ct).ConfigureAwait(false);
        var replay = _exchange!.Orders.CountAfter(generation.OrdersThrough) + _exchange.Fills.CountAfter(generation.FillsThrough);
        var replayBytes = replay * (TailRow.Bytes + 2L * Order.Bytes);
        if (replayBytes > _budget)
        {
            _store!.Execute(string.Create(CultureInfo.InvariantCulture, $"DELETE FROM snapshots WHERE generation = {generation.Number}"));
            _generation ??= generation;
            State = BlotterState.Refusing;
            Refusal = string.Create(CultureInfo.InvariantCulture,
                $"the streams would replay {replay:N0} versions and fills since the database's watermarks, a tail of "
                + $"{Megabytes(replayBytes)} before any scope over it, more than the {Megabytes(_budget)} budget");
            return;
        }

        // The planner prices pages by the snapshots table's size, which a new generation has changed.
        var counted = _version + 1;
        await _engine!.RefreshAsync(refresh => refresh.Version(counted).Refresh(_snapshotsTable!), ct).ConfigureAwait(false);
        _version = counted;

        _orderFeed = _exchange.Orders.Subscribe(generation.OrdersThrough);
        _fillFeed = _exchange.Fills.Subscribe(generation.FillsThrough);
        (_ordersThrough, _fillsThrough) = (generation.OrdersThrough, generation.FillsThrough);
        _generation = generation;
        _generationTakenAt = DateTime.UtcNow;
        Pages.Retire(new HashSet<long> { generation.Number });
        State = BlotterState.Live;
        Refusal = null;
        Status = ReadStatus();
    }

    private async Task ResumeWhenCaughtUpAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow - _exchange!.CompleteThrough > _options.ResumeWithin)
        {
            return;
        }

        foreach (var old in _generationsInDatabase.ToList())
        {
            _store!.Execute(string.Create(CultureInfo.InvariantCulture, $"DELETE FROM snapshots WHERE generation = {old}"));
        }

        _generationsInDatabase.Clear();
        await GoLiveAsync(ct).ConfigureAwait(false);
        if (State == BlotterState.Live)
        {
            _log.LogInformation("serving again from generation {Generation}", _generation!.Number);
            await IngestAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Stops serving: unsubscribes, empties the tail and closes every scope, so that nothing a page
    /// could read is left half true. Serving starts again from a new generation and a new replay.
    /// </summary>
    private async Task StopServingAsync(string why)
    {
        _log.LogWarning("refusing to serve: {Why}", why);
        State = BlotterState.Refusing;
        Refusal = why;
        if (_orderFeed is not null)
        {
            _exchange!.Orders.Unsubscribe(_orderFeed);
            _orderFeed = null;
        }

        if (_fillFeed is not null)
        {
            _exchange!.Fills.Unsubscribe(_fillFeed);
            _fillFeed = null;
        }

        // A build in flight finishes before anything else is let go, so its rows are there to delete.
        if (_building is { } building)
        {
            _building = null;
            try
            {
                await building.ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is DuckDBException or OperationCanceledException)
            {
            }
        }

        foreach (var scope in _scopes.Values.ToList())
        {
            await CloseAsync(scope).ConfigureAwait(false);
        }

        var all = _tail.Entries.ToList();
        var deletes = all.Select(e => TailRow.Of(e.Live, e.Base)).ToList();
        var versionDeletes = all.SelectMany(e => e.Versions).ToList();
        var fillDeletes = _tail.Fills.ToList();
        _tail.Clear();
        var version = _version + 1;
        await _engine!.RefreshAsync(refresh =>
        {
            refresh.Version(version);
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
        }).ConfigureAwait(false);
        _version = version;
        Published?.Invoke(_version);
    }

    private async Task PublishAsync(CancellationToken ct)
    {
        foreach (var scope in _scopes.Values)
        {
            await scope.UpdateAsync(_tail, _version, _generation!, _ordersThrough, _fillsThrough, ct).ConfigureAwait(false);
        }

        Published?.Invoke(_version);
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

    /// <summary>Runs <paramref name="work"/> on the ingest loop, between refreshes, and returns its result.</summary>
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
            Generation = _generation?.Number ?? 0,
            GenerationRows = _generation?.Rows ?? 0,
            CompleteThrough = _generation is { } g ? (g.OrdersThrough < g.FillsThrough ? g.OrdersThrough : g.FillsThrough) : null,
            DatabaseWatermark = _exchange?.CompleteThrough,
            NotYetIngested = _exchange?.NotYetIngested ?? 0,
            IngestionStalled = _exchange?.IngestionStalled ?? false,
            FeedRate = _exchange?.CurrentRate ?? 0,
            Published = (_exchange?.Orders.Published ?? 0) + (_exchange?.Fills.Published ?? 0),
            OrdersHeld = _exchange?.OrdersHeld ?? 0,
            TailOrders = _tail.Count,
            TailFills = _tail.FillCount,
            TimedOut = Queries.TimedOut,
            TailVersions = _tail.VersionCount,
            TailBytes = _tail.Bytes,
            Scopes = scopes.Count,
            ScopeBytes = scopeBytes,
            PageBytes = Pages.Bytes,
            PageChunks = Pages.Chunks,
            PageReads = Pages.Reads,
            PageHits = Pages.Hits,
            Statements = Queries.Executed,
            Budget = _budget,
            UsedBytes = _tail.Bytes + scopeBytes + Pages.Bytes,
            DatabaseBytes = SafeDatabaseBytes(),
            ManagedHeapBytes = GC.GetTotalMemory(forceFullCollection: false),
        };
    }

    private long SafeDatabaseBytes()
    {
        try
        {
            return _store?.MemoryBytes() ?? 0;
        }
        catch (DuckDBException)
        {
            return Status.DatabaseBytes;
        }
    }

    /// <summary>What the host cannot give back without dropping a version: the tail, and the scopes over it.</summary>
    private long LiveBytes() => _tail.Bytes + _scopes.Values.Sum(s => s.ViewBytes + s.State.Bytes);

    /// <summary>What another scope would hold: a row for every order in the tail, at what the open ones cost a row.</summary>
    private long ScopeEstimate()
    {
        var rows = _scopes.Values.Sum(s => (long)s.ViewRows);
        var bytes = _scopes.Values.Sum(s => s.ViewBytes + s.State.Bytes);
        var perRow = rows > 256 ? bytes / rows : 512;
        return Math.Max(256 << 10, _tail.Count * perRow);
    }

    /// <summary>The page cache's rows: its share of the budget, or what the tail and the scopes leave, whichever is less.</summary>
    private long PageRows()
    {
        var left = Math.Max(0, _budget - LiveBytes());
        var share = (long)(_budget * _options.PageShare);
        return Math.Max(16 * Pages.ChunkRows, Math.Min(left, share) / Order.Bytes);
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

    public long Generation { get; init; }

    public long GenerationRows { get; init; }

    public DateTime? CompleteThrough { get; init; }

    /// <summary>Order versions that have happened and that the order stream, running behind the fills, has not sent yet.</summary>
    public int OrdersHeld { get; init; }

    public DateTime? DatabaseWatermark { get; init; }

    public int NotYetIngested { get; init; }

    public bool IngestionStalled { get; init; }

    public double FeedRate { get; init; }

    public long Published { get; init; }

    public int TailOrders { get; init; }

    public int TailFills { get; init; }

    public long TimedOut { get; init; }

    public long TailVersions { get; init; }

    public long TailBytes { get; init; }

    public int Scopes { get; init; }

    public long ScopeBytes { get; init; }

    public long PageBytes { get; init; }

    public int PageChunks { get; init; }

    public long PageReads { get; init; }

    public long PageHits { get; init; }

    public long Statements { get; init; }

    public long Budget { get; init; }

    public long UsedBytes { get; init; }

    public long DatabaseBytes { get; init; }

    public long ManagedHeapBytes { get; init; }

    public double LastRefreshMs { get; init; }

    public int LastRefreshVersions { get; init; }

    public int Turnovers { get; init; }

    public double LastBuildMs { get; init; }

    public double LastSwitchMs { get; init; }

    public int LastEvicted { get; init; }
}

using System.Data.Common;
using Chalk.Catalog;
using Chalk.Client;
using Chalk.Sources;
using Chalk.Sources.Ado;
using Chalk.Sources.Poco;
using Chalk.TestKit;
using Microsoft.Data.Sqlite;
using CatalogContext = Chalk.Catalog.CatalogContext;

namespace Chalk.Integration.Tests;

/// <summary>
/// The evidence of D271 §8 (<c>docs/design/44-catalog-registration.md</c>): the catalog's bytes on
/// the wire per prepare, the statistics message a micro-batch publishes, the delta a shape change
/// registers and what it strands, and the recovery from an eviction and from a restart.
/// </summary>
/// <remarks>
/// Every figure here is a count — of bytes, of messages, of tables named — taken through a counting
/// <see cref="IQueryPlanner"/> decorator around the real transport. Nothing here asserts a duration.
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class CatalogRegistrationTests(SharedSidecar sidecar, ITestOutputHelper output)
    : IDisposable
{
    private readonly List<DbConnection> _open = [];

    /// <summary>
    /// One catalog is one zone: two sources declaring two zones are refused when the engine is
    /// created, naming both, before anything reaches the planner.
    /// </summary>
    [Fact]
    public async Task Two_zones_in_one_engine_are_refused_at_creation()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var eu = new PocoSourceBuilder("eu", "eu").Zone("eu").AddTable("rows", new[] { new ZoneRow(1) }).Build();
        var us = new PocoSourceBuilder("us", "us").Zone("us").AddTable("rows", new[] { new ZoneRow(2) }).Build();

        var error = await Assert.ThrowsAsync<CatalogValidationException>(async () =>
            await ChalkEngine.CreateAsync(new ChalkEngineOptions
            {
                ContextId = $"zones-{Guid.NewGuid():n}",
                Sources = [eu, us],
                Planner = sidecar.Sidecar.CreatePlanner(),
            }));

        Assert.Contains("'eu' (eu)", error.Message, StringComparison.Ordinal);
        Assert.Contains("'us' (us)", error.Message, StringComparison.Ordinal);
    }

    private sealed record ZoneRow(int Id);

    // ------------------------------------------------------------------ §8, figure 1

    /// <summary>
    /// The catalog crossed the wire before every prepare (the D86 comment in <c>PlanAsync</c>). It
    /// now crosses once, when its version is minted, and a prepare against a steady catalog carries
    /// no catalog bytes at all.
    /// </summary>
    [Theory]
    [InlineData("tenancy")]
    [InlineData("marketplace")]
    public async Task The_catalog_costs_its_own_bytes_once_and_nothing_per_prepare(string which)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        var (source, sql) = Fixture(which);
        var counter = new CountingPlanner(sidecar.Sidecar.CreatePlanner());
        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = $"d271-bytes-{which}-{Guid.NewGuid():n}",
            Sources = [source],
            Planner = counter,
        });

        // Once, at construction: the whole catalog — every byte of it — and its numbers beside it.
        Assert.Equal(1, counter.CatalogRegistrations);
        Assert.Equal(1, counter.StatisticsRegistrations);
        var whole = CatalogSerialization.ToProto(engine.Catalog).CalculateSize();
        Assert.Equal(whole, counter.CatalogBytes);

        counter.Reset();

        // And nothing per prepare, however many there are.
        await engine.PrepareAsync(sql);
        await engine.PrepareAsync(sql);
        await engine.PrepareAsync(sql + " LIMIT 1");

        // The figure §8 quotes, in full: three prepares, and not one catalog byte among them. What
        // each of them used to carry is `whole`, which is what the assertion above measured.
        Assert.Equal(3, counter.Plans);
        Assert.Equal(0, counter.CatalogRegistrations);
        Assert.Equal(0, counter.CatalogBytes);
        Assert.Equal(0, counter.StatisticsRegistrations);
        Assert.True(whole > 0, "the catalog has no bytes at all, so the figure means nothing");
        output.WriteLine($"D271 §8: the {which} catalog is {whole} bytes, sent once, 0 per prepare.");
    }

    // ------------------------------------------------------------------ §8, figure 2

    /// <summary>
    /// A micro-batch append publishes one statistics message naming one table and nothing else: no
    /// catalog registration, because no shape moved, and no other table's numbers.
    /// </summary>
    [Fact]
    public async Task A_micro_batch_append_publishes_one_statistics_message_naming_one_table()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        var source = new PocoSourceBuilder("mem", "m")
            .AddTable("orders", TenancyFixture.Orders, out var orders)
            .AddTable("notes", TenancyFixture.Notes)
            .Build();

        var counter = new CountingPlanner(sidecar.Sidecar.CreatePlanner());
        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = $"d271-batch-{Guid.NewGuid():n}",
            Sources = [source],
            Planner = counter,
        });
        counter.Reset();

        await engine.RefreshAsync(
            r => r.Append(orders, [Micro()]), TestContext.Current.CancellationToken);

        Assert.Equal(0, counter.CatalogRegistrations);
        Assert.Equal(1, counter.StatisticsRegistrations);
        Assert.Equal(["m.orders"], counter.StatisticsTables);
    }

    /// <summary>And nothing at all under <c>Defer</c>: the row count moved, so a message goes.</summary>
    /// <remarks>
    /// A deferral is about the <em>column</em> statistics — the pass over the values — and the row
    /// count is exact whatever it says, so the message still names the one table whose count moved.
    /// What it does not carry is a recomputed distribution, which is the cost the deferral turns
    /// down; a refresh that moved nothing at all sends no message, which the next assertion shows.
    /// </remarks>
    [Fact]
    public async Task A_refresh_that_moved_nothing_publishes_nothing()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        var source = new PocoSourceBuilder("mem", "m")
            .AddTable("orders", TenancyFixture.Orders)
            .Build();

        var counter = new CountingPlanner(sidecar.Sidecar.CreatePlanner());
        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = $"d271-quiet-{Guid.NewGuid():n}",
            Sources = [source],
            Planner = counter,
        });
        counter.Reset();

        // A plain refresh over a registration that reports the same rows: the epoch moves, and
        // nothing crosses the wire, because neither the shape nor the numbers did.
        var epoch = await engine.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, epoch);
        Assert.Equal(0, counter.CatalogRegistrations);
        Assert.Equal(0, counter.StatisticsRegistrations);
    }

    // ------------------------------------------------------------------ §8, figure 3

    /// <summary>
    /// A shape change in an ADO source registers a delta naming only the changed table, and only the
    /// prepared queries that read it go stale.
    /// </summary>
    [Fact]
    public async Task A_shape_change_registers_a_delta_and_strands_only_the_queries_that_read_it()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        var (source, connection) = Database();
        var counter = new CountingPlanner(sidecar.Sidecar.CreatePlanner());
        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = $"d271-delta-{Guid.NewGuid():n}",
            Sources = [source],
            Planner = counter,
        });

        var reader = await engine.PrepareAsync("SELECT id, name FROM db.widget ORDER BY id");
        var other = await engine.PrepareAsync("SELECT id, label FROM db.gadget ORDER BY id");
        Assert.False(reader.IsStale);
        Assert.False(other.IsStale);
        counter.Reset();

        Execute(connection, "ALTER TABLE widget ADD COLUMN price REAL");
        await engine.RefreshCatalogAsync(TestContext.Current.CancellationToken);

        // One registration, and it is a delta over the version the engine last registered.
        Assert.Equal(1, counter.CatalogRegistrations);
        var delta = Assert.Single(counter.Registrations);
        Assert.NotEqual("", delta.BaseShapeVersion);
        // Naming only the changed table, whatever else the catalog holds.
        Assert.Equal(["widget"], Tables(delta.Catalog));
        Assert.Empty(delta.RemovedTables);

        // And the staleness is per table: the query that reads `widget` is stranded, the one that
        // reads `gadget` is not.
        Assert.True(reader.IsStale);
        Assert.False(other.IsStale);

        var refused = await Assert.ThrowsAsync<StalePlanException>(() => CountAsync(engine, reader));
        Assert.Equal("db.widget", refused.Table);
        Assert.Equal(2, await CountAsync(engine, other));
    }

    // ------------------------------------------------------------------ §8, figure 4

    /// <summary>
    /// A planner that evicted this engine's catalog version answers the next plan by name, and the
    /// engine registers it again and retries once. No host involvement: the prepare succeeds, and
    /// the only trace is one more registration on the wire.
    /// </summary>
    /// <remarks>
    /// The bound is driven to one so the eviction happens in the test rather than after the default
    /// four versions. Every refresh that changes a shape mints a version, so two of them are enough.
    /// </remarks>
    [Fact]
    public async Task An_eviction_mid_session_is_recovered_by_one_retry()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        // The jar the shared sidecar itself was started from: the environment first, else the built
        // one under the planner's build directory — the same resolution SidecarFixture makes, so
        // this test runs wherever the suite does and not only under a script that sets the variable.
        var jar = RepoLayout.ResolvedPlannerJar?.FullName;

        Assert.SkipWhen(string.IsNullOrWhiteSpace(jar), "no planner jar to start a bounded sidecar with");

        await using var bounded = await PlannerProcess.StartAsync(
            new PlannerProcessOptions { JarPath = jar!, Workers = 1, CatalogVersions = 1 },
            TestContext.Current.CancellationToken);

        var (source, connection) = Database();
        var counter = new CountingPlanner(
            new GrpcQueryPlanner(new GrpcPlannerOptions
            {
                Address = bounded.Address,
                Deadline = TestTimeouts.PlannerDeadline,
            }));
        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = $"d271-evict-{Guid.NewGuid():n}",
            Sources = [source],
            Planner = counter,
        });

        var query = "SELECT id, name FROM db.widget ORDER BY id";
        Assert.Equal(2, await CountAsync(engine, await engine.PrepareAsync(query)));

        // Two shape changes past a bound of one: the version the engine is on is the only one held,
        // and every predecessor is gone. Then a plan against a *stale* prepared query's version is
        // not what we want — what we want is the live one, evicted from under it.
        Execute(connection, "ALTER TABLE gadget ADD COLUMN weight REAL");
        await engine.RefreshCatalogAsync(TestContext.Current.CancellationToken);

        // Evict the engine's own live version by registering something else under a bound of one.
        await using var squatter = new GrpcQueryPlanner(new GrpcPlannerOptions
        {
            Address = bounded.Address,
            Deadline = TestTimeouts.PlannerDeadline,
        });
        await squatter.RegisterCatalogAsync(
            new CatalogRegistration
            {
                Catalog = engine.Catalog,
                InstanceId = engine.Catalog.ContextId,
                ShapeVersion = "01SQUATTERVERSIONXXXXXXXXX",
            },
            TestContext.Current.CancellationToken);

        counter.Reset();
        var after = await engine.PrepareAsync(query);

        // The prepare succeeded, and the recovery is visible as exactly one registration and one
        // statistics message — the engine's own, sent because the plan came back by name.
        Assert.Equal(2, await CountAsync(engine, after));
        Assert.Equal(1, counter.CatalogRegistrations);
        Assert.Equal(1, counter.StatisticsRegistrations);
        Assert.Equal("", Assert.Single(counter.Registrations).BaseShapeVersion);
    }

    /// <summary>
    /// A planner restart is the same recovery: everything it held is gone, and the engine registers
    /// again and retries once without the host hearing about it.
    /// </summary>
    [Fact]
    public async Task A_planner_restart_is_recovered_by_one_retry()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        // The jar the shared sidecar itself was started from: the environment first, else the built
        // one under the planner's build directory — the same resolution SidecarFixture makes, so
        // this test runs wherever the suite does and not only under a script that sets the variable.
        var jar = RepoLayout.ResolvedPlannerJar?.FullName;

        Assert.SkipWhen(string.IsNullOrWhiteSpace(jar), "no planner jar to restart");

        var (source, _) = Database();
        var first = await PlannerProcess.StartAsync(
            new PlannerProcessOptions { JarPath = jar!, Workers = 1 },
            TestContext.Current.CancellationToken);
        var address = first.Address;

        var counter = new CountingPlanner(
            new GrpcQueryPlanner(new GrpcPlannerOptions
            {
                Address = address,
                Deadline = TestTimeouts.PlannerDeadline,
            }));
        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = $"d271-restart-{Guid.NewGuid():n}",
            Sources = [source],
            Planner = counter,
        });

        var query = "SELECT id, name FROM db.widget ORDER BY id";
        Assert.Equal(2, await CountAsync(engine, await engine.PrepareAsync(query)));

        // The sidecar goes and comes back on the same address, holding nothing at all.
        var socket = PlannerAddress.IsUnix(address)
            ? PlannerAddress.RequireSocketPath(address, nameof(address))
            : null;
        await first.DisposeAsync();
        await using var restarted = await PlannerProcess.StartAsync(
            new PlannerProcessOptions
            {
                JarPath = jar!,
                Workers = 1,
                Transport = socket is null ? PlannerTransport.Tcp : PlannerTransport.UnixSocket,
                SocketPath = socket,
            },
            TestContext.Current.CancellationToken);
        Assert.SkipWhen(
            restarted.Address != address,
            "the sidecar came back on another address, so the client cannot reach it again");

        counter.Reset();
        Assert.Equal(2, await CountAsync(engine, await engine.PrepareAsync(query)));

        Assert.Equal(1, counter.CatalogRegistrations);
        Assert.Equal(1, counter.StatisticsRegistrations);
    }

    // ------------------------------------------------------------------ fixtures

    /// <summary>
    /// The two catalogs §8 measures: the tenancy fixture's, and the marketplace — the vendors, items
    /// and order lines of design 38 §8, which is the other shape a host of this size has.
    /// </summary>
    private static (ISourceRuntime Source, string Sql) Fixture(string which) => which switch
    {
        // The unentitled twin of the tenancy fixture: the same tables and the same descriptors, and a
        // prepare that needs no principal's context to get past the entitlement pass. What is being
        // measured is the catalog's size and the count of registrations, which the entitlement would
        // add to and not change the shape of.
        "tenancy" => (TenancyFixture.Unentitled.Source, "SELECT id, amount FROM orders ORDER BY id"),
        "marketplace" => (
            new PocoSourceBuilder("market", "mk")
                .AddTable("vendors", TenancyFixture.Vendors)
                .AddTable("items", TenancyFixture.Items)
                .AddTable("order_items", TenancyFixture.OrderItems)
                .Build(),
            "SELECT id, quantity FROM mk.order_items ORDER BY id"),
        _ => throw new ArgumentOutOfRangeException(nameof(which), which, "no such fixture"),
    };

    private static TenancyFixture.Order Micro() =>
        new(9_999, 1, 1, 1_000, "a micro-batch", 1, 1);

    private static IReadOnlyList<string> Tables(CatalogContext catalog) =>
        [.. catalog.Schemas.SelectMany(s => s.Tables).Select(t => t.Name)];

    /// <summary>A private SQLite database with two tables, so a change to one can leave the other.</summary>
    private (AdoSource Source, DbConnection Connection) Database()
    {
        var connectionString = $"Data Source=chalk-d271-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        DbConnection Connect() => new SqliteConnection(connectionString);

        var keepAlive = Connect();
        keepAlive.Open();
        _open.Add(keepAlive);

        Execute(keepAlive, "CREATE TABLE widget (id INTEGER NOT NULL, name TEXT)");
        Execute(keepAlive, "INSERT INTO widget VALUES (1, 'one'), (2, 'two')");
        Execute(keepAlive, "CREATE TABLE gadget (id INTEGER NOT NULL, label TEXT)");
        Execute(keepAlive, "INSERT INTO gadget VALUES (1, 'a'), (2, 'b')");

        var source = new AdoSourceBuilder("db", Connect, "db")
            .Dialect(DialectProfiles.Sqlite)
            .Capabilities(AdoCapabilities.For(DialectProfiles.Sqlite))
            .DiscoverTables()
            .Build();
        return (source, keepAlive);
    }

    private static void Execute(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static async Task<int> CountAsync(ChalkEngine engine, PreparedQuery query)
    {
        var rows = 0;
        await using var execution = await engine.ExecuteAsync(query);
        await foreach (var batch in execution.Batches.WithCancellation(TestContext.Current.CancellationToken))
        {
            rows += batch.Length;
            batch.Dispose();
        }

        return rows;
    }

    public void Dispose()
    {
        foreach (var connection in _open)
        {
            connection.Dispose();
        }
    }

    /// <summary>
    /// Counts what crosses the wire, so §8's figures are measured rather than asserted from the
    /// shape of the code: catalog registrations and their serialised size, statistics messages and
    /// the tables they name, and plans.
    /// </summary>
    private sealed class CountingPlanner(IQueryPlanner inner) : IQueryPlanner
    {
        private readonly List<CatalogRegistration> _registrations = [];
        private readonly List<string> _statisticsTables = [];

        public int CatalogRegistrations { get; private set; }

        public long CatalogBytes { get; private set; }

        public int StatisticsRegistrations { get; private set; }

        public int Plans { get; private set; }

        public IReadOnlyList<CatalogRegistration> Registrations => _registrations;

        /// <summary>Every table named by a statistics message, as <c>schema.table</c>.</summary>
        public IReadOnlyList<string> StatisticsTables => _statisticsTables;

        public void Reset()
        {
            CatalogRegistrations = 0;
            CatalogBytes = 0;
            StatisticsRegistrations = 0;
            Plans = 0;
            _registrations.Clear();
            _statisticsTables.Clear();
        }

        public bool AcceptsCatalogDeltas => inner.AcceptsCatalogDeltas;

        public ValueTask<PlannerInfo> GetInfoAsync(CancellationToken ct = default) =>
            inner.GetInfoAsync(ct);

        public ValueTask RegisterCatalogAsync(
            CatalogRegistration registration, CancellationToken ct = default)
        {
            CatalogRegistrations++;
            CatalogBytes += CatalogSerialization.ToProto(registration.Catalog).CalculateSize();
            _registrations.Add(registration);
            return inner.RegisterCatalogAsync(registration, ct);
        }

        public ValueTask RegisterStatisticsAsync(
            StatisticsRegistration statistics, CancellationToken ct = default)
        {
            StatisticsRegistrations++;
            foreach (var table in statistics.Tables)
            {
                _statisticsTables.Add($"{table.Schema}.{table.Table}");
            }

            return inner.RegisterStatisticsAsync(statistics, ct);
        }

        public ValueTask<PlanResult> PlanAsync(PlanRequest request, CancellationToken ct = default)
        {
            Plans++;
            return inner.PlanAsync(request, ct);
        }

        public ValueTask<RedactedSql> RedactSqlAsync(
            RedactSqlRequest request, CancellationToken ct = default) =>
            inner.RedactSqlAsync(request, ct);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}

using System.Data.Common;
using Chalk.Catalog;
using Chalk.Client;
using Chalk.Sources;
using Chalk.Sources.Ado;
using Chalk.TestKit;
using Microsoft.Data.Sqlite;

namespace Chalk.Integration.Tests;

/// <summary>
/// The lifecycle pieces pulled forward from M3 (D86): a catalog that can be refreshed, an epoch
/// that says which one a plan belongs to, and a prepared query that knows when it has been left
/// behind.
/// </summary>
/// <remarks>
/// The scenario is the one that matters in production and is impossible to test against a fixture
/// nobody may modify: a table grows a column while the engine is running. Each test therefore
/// builds its own private SQLite database and is free to change it.
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class CatalogLifecycleTests(SharedSidecar sidecar) : IDisposable
{
    private readonly List<DbConnection> _open = [];

    /// <summary>
    /// The whole of D86 in one pass: refresh re-introspects, the epoch moves, the query prepared
    /// before it is stale and says so, executing it is refused, and preparing it again sees the new
    /// column.
    /// </summary>
    [Fact]
    public async Task Refreshing_after_a_column_is_added_bumps_the_epoch_and_strands_old_plans()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        var (source, connection) = Database();
        await using var engine = await EngineAsync(source);

        Assert.Equal(1, engine.Catalog.Epoch);
        Assert.Equal(2, Columns(engine, "widget").Count);

        var before = await engine.PrepareAsync("SELECT id, name FROM db.widget ORDER BY id");
        Assert.False(before.IsStale);
        Assert.Equal(3, await CountAsync(engine, before));

        Execute(connection, "ALTER TABLE widget ADD COLUMN price REAL");

        // Nothing has changed yet: a schema change nobody told the engine about is invisible, which
        // is the correct behaviour -- the alternative is re-introspecting on every statement.
        Assert.Equal(2, Columns(engine, "widget").Count);
        Assert.False(before.IsStale);

        var epoch = await engine.RefreshCatalogAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, epoch);
        Assert.Equal(2, engine.Catalog.Epoch);
        Assert.Equal(3, Columns(engine, "widget").Count);
        Assert.Contains("price", Columns(engine, "widget").Select(c => c.Name), StringComparer.Ordinal);

        // The plan from the old epoch was compiled against a shape the source may no longer have.
        Assert.Equal(2, engine.ShapeEpoch);
        Assert.True(before.IsStale);
        var refused = await Assert.ThrowsAsync<StalePlanException>(() => CountAsync(engine, before));
        Assert.Equal(1, refused.PlanEpoch);
        Assert.Equal(2, refused.LiveEpoch);

        var after = await engine.PrepareAsync("SELECT id, name, price FROM db.widget ORDER BY id");
        Assert.False(after.IsStale);
        Assert.Equal(3, await CountAsync(engine, after));
    }

    /// <summary>
    /// A refresh that finds nothing changed still moves the epoch. That is deliberate: the epoch
    /// says "the catalog was reassembled", not "something differed", and a host that wants the
    /// weaker guarantee can compare the descriptors itself.
    /// </summary>
    /// <remarks>
    /// What such a refresh does <em>not</em> do, since D260, is strand the statements prepared
    /// before it: the shape is what a plan was compiled against, and a reassembly that found the
    /// same shape leaves every plan runnable. The epoch counts refreshes; the shape epoch is what
    /// staleness is measured against.
    /// </remarks>
    [Fact]
    public async Task Refreshing_an_unchanged_database_still_moves_the_epoch()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        var (source, _) = Database();
        await using var engine = await EngineAsync(source);

        var before = await engine.PrepareAsync("SELECT id, name FROM db.widget ORDER BY id");

        Assert.Equal(2, await engine.RefreshCatalogAsync(TestContext.Current.CancellationToken));
        Assert.Equal(3, await engine.RefreshCatalogAsync(TestContext.Current.CancellationToken));

        Assert.Equal(1, engine.ShapeEpoch);
        Assert.False(before.IsStale);
        Assert.Equal(3, await CountAsync(engine, before));
    }

    /// <summary>
    /// The cadence. The timer is fired by hand rather than waited for, so the test asserts the
    /// behaviour and not the clock.
    /// </summary>
    [Fact]
    public async Task The_refresh_interval_refreshes_on_its_own()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        var (source, _) = Database();
        var clock = new ManualTimerProvider();
        await using (var engine = await EngineAsync(
            source,
            new ExecutionOptions
            {
                TimeProvider = clock,
                CatalogRefreshInterval = TimeSpan.FromMinutes(5),
            }))
        {
            Assert.Equal(1, engine.Catalog.Epoch);
            Assert.Equal(1, clock.LiveTimers);

            clock.Fire();
            await WaitForEpochAsync(engine, 2);

            clock.Fire();
            await WaitForEpochAsync(engine, 3);
        }

        // The engine owns the timer and stops it when it is disposed.
        Assert.Equal(0, clock.LiveTimers);
    }

    /// <summary>An interval that is not a duration is a construction error, not a silent no-op.</summary>
    [Fact]
    public async Task A_non_positive_refresh_interval_is_refused()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        var (source, _) = Database();
        var failure = await Assert.ThrowsAsync<ArgumentException>(
            () => EngineAsync(source, new ExecutionOptions { CatalogRefreshInterval = TimeSpan.Zero })
                .AsTask());

        Assert.Contains("CatalogRefreshInterval", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Per-source options on the engine reach the source (D86). A one-millisecond budget against a
    /// table the query has to read is a <see cref="SourceTimeoutException"/> naming the source.
    /// </summary>
    [Fact]
    public async Task A_per_source_timeout_on_the_engine_reaches_the_source()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        Assert.SkipWhen(RemoteFixture.SkipReason is not null, RemoteFixture.SkipReason ?? string.Empty);

        // The 60 000-row table, read through a query with a one-tick budget.
        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = CorpusFixture.ContextId,
            Functions = CorpusFunctions.Register,
            Sources = RemoteFixture.Shared.Sources,
            Planner = sidecar.CreatePlanner(),
            Execution = new ExecutionOptions
            {
                BatchSize = 1,
                SourceOptions = new Dictionary<string, SourceOptions>(StringComparer.Ordinal)
                {
                    ["duck"] = new() { QueryTimeout = TimeSpan.FromTicks(1) },
                },
            },
        });

        var prepared = await engine.PrepareAsync("SELECT l_orderkey FROM duck.lineitem");
        var failure = await Assert.ThrowsAsync<ExecutionException>(() => CountAsync(engine, prepared));

        // The engine attributes the fault to the operator that raised it and keeps the source's own
        // exception inside, so a host reads both "which operator" and "which source" (§4).
        var timeout = Assert.IsType<SourceTimeoutException>(failure.InnerException);
        Assert.Equal("duck", timeout.SourceId);
        Assert.Equal(TimeSpan.FromTicks(1), timeout.Timeout);
    }

    /// <summary>
    /// Reads the epoch until it reaches <paramref name="epoch"/>. The refresh a timer starts is not
    /// awaited by anything -- this is the wait, and it fails the test by timing out rather than by
    /// asserting on a duration.
    /// </summary>
    private static async Task WaitForEpochAsync(ChalkEngine engine, long epoch)
    {
        using var giveUp = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (engine.Catalog.Epoch < epoch)
        {
            await Task.Delay(5, giveUp.Token);
        }

        Assert.Equal(epoch, engine.Catalog.Epoch);
    }

    private ValueTask<ChalkEngine> EngineAsync(AdoSource source, ExecutionOptions? execution = null) =>
        ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "lifecycle",
            Functions = CorpusFunctions.Register,
            Sources = [source],
            Planner = sidecar.CreatePlanner(),
            Execution = execution ?? new ExecutionOptions(),
        });

    /// <summary>A private in-memory SQLite with one small table, discovered rather than declared.</summary>
    private (AdoSource Source, DbConnection KeepAlive) Database()
    {
        var connectionString = $"Data Source=chalk-lifecycle-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        DbConnection Connect() => new SqliteConnection(connectionString);

        var keepAlive = Connect();
        keepAlive.Open();
        _open.Add(keepAlive);

        Execute(keepAlive, "CREATE TABLE widget (id INTEGER NOT NULL, name TEXT)");
        Execute(keepAlive, "INSERT INTO widget VALUES (1, 'one'), (2, 'two'), (3, 'three')");

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

    private static IReadOnlyList<ColumnDescriptor> Columns(ChalkEngine engine, string table) =>
        engine.Catalog.Schemas
            .Single(s => s.SourceId == "db")
            .FindTable(table)!
            .Columns;

    private static async Task<int> CountAsync(ChalkEngine engine, PreparedQuery query)
    {
        var rows = 0;
        await using var execution = await engine.ExecuteAsync(query, (IReadOnlyList<object?>?)null);
        await foreach (var batch in execution.Batches)
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
}

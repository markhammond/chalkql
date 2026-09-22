using System.Data.Common;
using Chalk.Catalog;
using Chalk.Client;
using Chalk.Ir;
using Chalk.Sources;
using Chalk.Sources.Ado;
using Chalk.Sources.Poco;
using Chalk.TestKit;
using DuckDB.NET.Data;
using CatalogContext = Chalk.Catalog.CatalogContext;
using SourceCapabilities = Chalk.Catalog.SourceCapabilities;

namespace Chalk.Integration.Tests;

/// <summary>
/// A parameterised <c>LIMIT</c> pushed to a source and rendered at execution, read at the place it
/// stops being Chalk's: the text the driver was handed (D288, design 50).
/// </summary>
/// <remarks>
/// <para>
/// A plan says what the planner meant and a golden file says what the plan said; neither can say
/// what the database was asked. So every claim here is made against a <see cref="DbConnection"/>
/// wrapped around the real DuckDB one, which records the text of every command run on it — the last
/// place that text exists before the driver has it.
/// </para>
/// <para>
/// The statements are the ones <c>docs/guide.md</c> shows a host, and their shipped texts are
/// asserted here rather than written out there by hand.
/// </para>
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class PushedBoundTests(SharedSidecar sidecar) : IDisposable
{
    private readonly List<DbConnection> _connections = [];
    private readonly List<DuckDbFile> _files = [];
    private readonly List<string> _sent = [];

    public void Dispose()
    {
        foreach (var connection in _connections)
        {
            connection.Dispose();
        }

        foreach (var file in _files)
        {
            file.Dispose();
        }
    }

    private sealed record Region(int Id, string Name);

    /// <summary>
    /// What the source was sent since the engine was built. The capture starts empty at that point:
    /// discovery reads the shape and counts the rows, and none of that is what is being read here.
    /// </summary>
    private IReadOnlyList<string> Shipped
    {
        get
        {
            lock (_sent)
            {
                return [.. _sent];
            }
        }
    }

    // ---------------------------------------------------------------- a literal bound

    /// <summary>
    /// The case a parameterised bound is measured against: a literal one travels, and the source is
    /// asked for twenty-five rows instead of all of them.
    /// </summary>
    [Fact]
    public async Task A_literal_bound_travels_to_the_source()
    {
        await using var engine = await EngineAsync();

        var rows = await RunAsync(
            engine,
            "SELECT id, total FROM duck.orders WHERE status = 'open' ORDER BY id DESC LIMIT 25");

        Assert.Equal(
            [
                "SELECT \"id\", \"total\" FROM \"orders\" WHERE \"status\" = 'open'"
                + " ORDER BY \"id\" DESC NULLS FIRST LIMIT 25",
            ],
            Shipped);
        Assert.Equal(25, rows.Count);
    }

    // ---------------------------------------------------------------- a parameterised one

    /// <summary>
    /// The same statement with the bound as a parameter, prepared once and executed twice: the
    /// shipped text carries each execution's own number, and the statement's other parameter is
    /// still bound by the provider.
    /// </summary>
    [Fact]
    public async Task A_parameterised_bound_is_rendered_per_execution()
    {
        await using var engine = await EngineAsync();
        const string statement =
            "SELECT id, total FROM duck.orders WHERE status = ? ORDER BY id DESC LIMIT ?";

        var prepared = await engine.PrepareAsync(
            statement, ct: TestContext.Current.CancellationToken);
        var small = await RunAsync(engine, prepared, ["open", 25L]);
        var large = await RunAsync(engine, prepared, ["open", 100L]);

        // `$p0` and not `?`: the ADO source renames the placeholders the dialect's own way, and the
        // bound is already a number by then.
        Assert.Equal(
            [
                "SELECT \"id\", \"total\" FROM \"orders\" WHERE \"status\" = $p0"
                + " ORDER BY \"id\" DESC NULLS FIRST LIMIT 25",
                "SELECT \"id\", \"total\" FROM \"orders\" WHERE \"status\" = $p0"
                + " ORDER BY \"id\" DESC NULLS FIRST LIMIT 100",
            ],
            Shipped);
        // Eighty of the hundred and twenty orders are open, so the larger page is all of them —
        // which is the right answer to `LIMIT 100` and not a sign the bound was ignored.
        Assert.Equal(25, small.Count);
        Assert.Equal(80, large.Count);

        // The plan the text came from carries the template and says which placeholder is the bound.
        var remote = prepared.Plan.SourceQueries().Single();
        Assert.EndsWith("LIMIT ?", remote.QueryText, StringComparison.Ordinal);
        Assert.Equal([1u], remote.RenderedBounds);
    }

    /// <summary>
    /// And the rows are the unpushed plan's, which is the only thing that makes the push a
    /// optimisation rather than a different query.
    /// </summary>
    [Fact]
    public async Task The_pushed_rows_are_the_unpushed_plans()
    {
        await using var engine = await EngineAsync();
        const string statement =
            "SELECT id, total FROM duck.orders WHERE status = ? ORDER BY id DESC LIMIT ?";

        var pushed = await engine.PrepareAsync(
            statement, ct: TestContext.Current.CancellationToken);
        var local = await engine.PrepareAsync(
            statement,
            new PrepareOptions { Pushdown = PushdownLevel.None },
            TestContext.Current.CancellationToken);

        Assert.NotEmpty(pushed.Plan.SourceQueries());
        Assert.Empty(local.Plan.SourceQueries());

        foreach (var bound in new object?[] { 1L, 7L, 40L })
        {
            var expected = await RunAsync(engine, local, ["open", bound]);
            var actual = await RunAsync(engine, pushed, ["open", bound]);

            Assert.Equal(expected.Count, actual.Count);
            Assert.Equal(
                expected.Select(r => r[0]).ToArray(), actual.Select(r => r[0]).ToArray());
        }
    }

    /// <summary>Zero is a bound like any other: the source is asked for no rows and answers none.</summary>
    [Fact]
    public async Task A_bound_of_zero_is_shipped_as_zero()
    {
        await using var engine = await EngineAsync();

        var rows = await RunAsync(
            engine,
            "SELECT id, total FROM duck.orders WHERE status = ? ORDER BY id DESC LIMIT ?",
            ["open", 0L]);

        Assert.Empty(rows);
        Assert.All(Shipped, sql => Assert.EndsWith("LIMIT 0", sql, StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- every branch

    /// <summary>
    /// A bound over a partitioned table is copied into every partition and rendered in every
    /// partition's own text, and the local top-N above them still decides the answer.
    /// </summary>
    [Fact]
    public async Task Every_partitions_query_carries_the_bound()
    {
        await using var engine = await EngineAsync(partitioned: true);

        var rows = await RunAsync(
            engine,
            "SELECT region, id FROM books.all_orders ORDER BY id LIMIT ?",
            [4L]);

        Assert.Equal(2, Shipped.Count);
        Assert.All(
            Shipped, sql => Assert.EndsWith("ORDER BY \"id\" LIMIT 4", sql, StringComparison.Ordinal));
        Assert.Contains(Shipped, sql => sql.Contains("\"orders_north\"", StringComparison.Ordinal));
        Assert.Contains(Shipped, sql => sql.Contains("\"orders_south\"", StringComparison.Ordinal));
        Assert.Equal(4, rows.Count);
    }

    // ---------------------------------------------------------------- when it stays local

    /// <summary>
    /// A source whose descriptor does not claim a limit is sent none — the same answer, at the cost
    /// of every row crossing the boundary before the local fetch stops pulling. Worth knowing the
    /// price of, which is why it is a case here and an example in the guide.
    /// </summary>
    [Fact]
    public async Task A_source_that_declares_no_limit_keeps_the_bound_local()
    {
        await using var engine = await EngineAsync(truncates: false);

        var rows = await RunAsync(
            engine,
            "SELECT id, total FROM duck.orders WHERE status = ? ORDER BY id DESC LIMIT ?",
            ["open", 25L]);

        Assert.Equal(25, rows.Count);
        Assert.Equal(
            ["SELECT \"id\", \"total\" FROM \"orders\" WHERE \"status\" = $p0"],
            Shipped);
    }

    // ---------------------------------------------------------------- the fixture

    private const int Orders = 120;

    private async Task<ChalkEngine> EngineAsync(
        bool partitioned = false, bool truncates = true)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        Assert.SkipWhen(RemoteFixture.SkipReason is not null, RemoteFixture.SkipReason ?? string.Empty);

        var duck = DuckSource(partitioned, truncates);
        var regions = new PocoSourceBuilder("mem", "main")
            .AddTable("regions", new[] { new Region(1, "north"), new Region(2, "south") })
            .Build();

        List<ISourceRuntime> sources = [regions, duck];
        if (partitioned)
        {
            sources.Add(PartitionedView(duck));
        }

        var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "pushed-bound",
            Sources = sources,
            Planner = sidecar.CreatePlanner(),
        });

        lock (_sent)
        {
            _sent.Clear();
        }

        return engine;
    }

    /// <summary>
    /// The DuckDB source, behind a connection that records what it is asked. It reads through the
    /// <c>DbDataReader</c> path rather than the native one: the native reader takes the provider's
    /// own connection type, and what is under test is the text rather than the reader.
    /// </summary>
    private AdoSource DuckSource(bool partitioned, bool truncates)
    {
        var file = DuckDbFile.Create("pushed-bound");
        _files.Add(file);

        var keepAlive = new DuckDBConnection(file.ConnectionString);
        keepAlive.Open();
        _connections.Add(keepAlive);

        Execute(
            keepAlive,
            "CREATE TABLE \"orders\" (\"id\" BIGINT NOT NULL, \"status\" VARCHAR NOT NULL, "
            + "\"total\" DOUBLE NOT NULL)");
        Execute(
            keepAlive,
            "INSERT INTO \"orders\" SELECT i, CASE WHEN i % 3 = 0 THEN 'shipped' ELSE 'open' END, "
            + $"i * 1.5 FROM range(0, {Orders}) t(i)");

        if (partitioned)
        {
            Execute(
                keepAlive,
                "CREATE TABLE \"orders_north\" AS SELECT 'north' AS \"region\", \"id\", \"total\""
                + " FROM \"orders\" WHERE \"id\" % 2 = 0");
            Execute(
                keepAlive,
                "CREATE TABLE \"orders_south\" AS SELECT 'south' AS \"region\", \"id\", \"total\""
                + " FROM \"orders\" WHERE \"id\" % 2 = 1");
        }

        var capabilities = truncates
            ? AdoCapabilities.For(DialectProfiles.DuckDb)
            : Narrowed(AdoCapabilities.For(DialectProfiles.DuckDb));

        return new AdoSourceBuilder(
                "duck", () => new CapturingConnection(new DuckDBConnection(file.ConnectionString), _sent), "duck")
            .Dialect(DialectProfiles.DuckDb)
            .Options(TestTimeouts.SourceOptions)
            .Capabilities(capabilities)
            .RowCounts(AdoSourceBuilder.RowCountMode.Exact)
            .DiscoverTables()
            .Build();
    }

    /// <summary>
    /// The derived descriptor with the sort and the bound taken out: what a host registers for a
    /// source it would rather not have order or truncate for it. Written out because
    /// <see cref="SourceCapabilities"/> is a class and has no copy-with-changes form.
    /// </summary>
    private static SourceCapabilities Narrowed(SourceCapabilities derived) => new()
    {
        QueryLanguage = derived.QueryLanguage,
        PushablePredicates = derived.PushablePredicates,
        PushableFunctions = derived.PushableFunctions,
        PushableAggregates = derived.PushableAggregates,
        SupportsProject = derived.SupportsProject,
        SupportsSort = false,
        SupportsLimit = false,
        SupportsOffset = false,
        SupportsDistinct = derived.SupportsDistinct,
        SupportsGroupBy = derived.SupportsGroupBy,
        SupportsHaving = derived.SupportsHaving,
        SupportsInnerJoin = derived.SupportsInnerJoin,
        SupportsOuterJoin = derived.SupportsOuterJoin,
        SupportsSemiAntiJoin = derived.SupportsSemiAntiJoin,
        MaxInList = derived.MaxInList,
        SupportsParameters = derived.SupportsParameters,
    };

    /// <summary>The logical table whose two partitions are the two physical ones in DuckDB.</summary>
    private static PartitionedViewSource PartitionedView(AdoSource duck)
    {
        var north = duck.DescribeSchema().FindTable("orders_north")
            ?? throw new InvalidOperationException("the fixture has no table 'orders_north'.");

        return new PartitionedViewSource(
            "books",
            "books",
            [
                new TableDescriptor
                {
                    Name = "all_orders",
                    Columns = north.Columns,
                    RowCount = Orders,
                    RowCountKind = RowCountKind.Exact,
                    Partitioning = new PartitioningDescriptor
                    {
                        PartitionColumn = 0,
                        Partitions =
                        [
                            new PartitionDescriptor
                            {
                                Schema = "duck",
                                Table = "orders_north",
                                Value = "north",
                                HasValue = true,
                                RowCount = Orders / 2,
                            },
                            new PartitionDescriptor
                            {
                                Schema = "duck",
                                Table = "orders_south",
                                Value = "south",
                                HasValue = true,
                                RowCount = Orders / 2,
                            },
                        ],
                    },
                },
            ]);
    }

    private static void Execute(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private async Task<List<object?[]>> RunAsync(
        ChalkEngine engine, string statement, IReadOnlyList<object?>? parameters = null)
    {
        var prepared = await engine.PrepareAsync(
            statement, ct: TestContext.Current.CancellationToken);
        return await RunAsync(engine, prepared, parameters);
    }

    private async Task<List<object?[]>> RunAsync(
        ChalkEngine engine, PreparedQuery prepared, IReadOnlyList<object?>? parameters)
    {
        var rows = new List<object?[]>();
        await using var execution = await engine.ExecuteAsync(
            prepared, parameters, ct: TestContext.Current.CancellationToken);
        await foreach (var batch in execution.Batches)
        {
            using (batch)
            {
                rows.AddRange(BatchReader.ToRows(batch));
            }
        }

        return rows;
    }
}

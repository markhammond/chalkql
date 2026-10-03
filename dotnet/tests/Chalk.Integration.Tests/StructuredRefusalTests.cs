using System.Data;
using System.Data.Common;
using Apache.Arrow;
using Apache.Arrow.Types;
using Chalk.Catalog;
using Chalk.Client;
using Chalk.Entitlements;
using Chalk.Sources;
using Chalk.Sources.Ado;
using Chalk.Sources.Poco;
using Chalk.TestKit;
using DuckDB.NET.Data;
using ArrowSchema = Apache.Arrow.Schema;

namespace Chalk.Integration.Tests;

/// <summary>
/// A refused statement still says what its result would have been (D329), an engine answers the
/// same refusal again from memory with the same model (D330), and any result's table model becomes
/// an empty <see cref="DataTable"/> (D331).
/// </summary>
/// <remarks>
/// <para>
/// A counter in organisations 1 and 2 may count <c>national_id</c> and never read it, so
/// <c>SELECT *</c> is refused. The refusal names the table, the column, the use and the aggregate
/// the column allows, and carries the three output columns in the form a prepared statement's
/// schema takes — the same types, and <c>chalk.disclosure</c> on each field — on every path a host
/// prepares by: bound as it prepares, bound partially, bound as it runs, and over a database.
/// </para>
/// <para>
/// A star under a refusing star policy is refused before the statement is validated, and carries
/// the refused use without a table model.
/// </para>
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class StructuredRefusalTests(SharedSidecar sidecar) : IDisposable
{
    private readonly List<DbConnection> _connections = [];
    private readonly List<DuckDbFile> _files = [];

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

    /// <summary>Where the rows are, and when the principal's values are bound.</summary>
    public enum Run
    {
        /// <summary>Rows in the process, every list bound as the statement is prepared.</summary>
        BoundAtPrepare,

        /// <summary>Rows in the process, the people left to execution.</summary>
        PartiallyBound,

        /// <summary>Rows in the process, every list left to execution.</summary>
        BoundAtExecute,

        /// <summary>The same rows in a DuckDB database, bound as the statement is prepared.</summary>
        FromDatabase,
    }

    public static TheoryData<Run> Runs =>
        new() { Run.BoundAtPrepare, Run.PartiallyBound, Run.BoundAtExecute, Run.FromDatabase };

    public sealed record Member(int Id, int OrgId, string NationalId);

    public sealed record Card(string Email, int Tier);

    public sealed record Profile(
        int Id,
        DateOnly Day,
        TimeOnly At,
        DateTime Stamp,
        DateTimeOffset StampTz,
        TimeSpan Span,
        Guid Uid,
        Card? Contact);

    private static readonly Member[] Members = [new(1, 1, "AA-1"), new(2, 2, "BB-1"), new(3, 3, "CC-1")];

    private static readonly Profile[] Profiles =
    [
        new(
            1,
            new DateOnly(2026, 9, 30),
            new TimeOnly(9, 30),
            new DateTime(2026, 9, 30, 9, 30, 0),
            new DateTimeOffset(2026, 9, 30, 9, 30, 0, TimeSpan.Zero),
            TimeSpan.FromHours(1),
            Guid.Parse("4f4e5be9-2aa1-4819-bae7-5696706b770e"),
            new Card("a@example.org", 2)),
    ];

    /// <summary>A counter in organisations 1 and 2, holding no one as a person.</summary>
    private static RequestContext Counter(params int[] counting) => new()
    {
        Lists = new Dictionary<string, ContextRelation>(StringComparer.Ordinal)
        {
            ["orgs"] = new() { Columns = ["id"], Rows = [[1], [2]], ColumnTypes = [ChalkType.Int32()] },
            ["counting"] = new()
            {
                Columns = ["id"],
                Rows = counting.Select(o => (IReadOnlyList<object?>)[o]).ToArray(),
                ColumnTypes = [ChalkType.Int32()],
            },
            ["people"] = new() { Columns = ["id"], Rows = [], ColumnTypes = [ChalkType.Int32()] },
        },
    };

    private static readonly TableEntitlementDescriptor MembersEntitlement = new()
    {
        RowPredicate = "org_id IN (@ctx.orgs) OR id IN (@ctx.people)",
        Columns =
        [
            new ColumnEntitlementDescriptor
            {
                Column = 2,
                AggregateOnlyFunctions = ["COUNT"],
                MinGroupSize = 3,
                Rules =
                [
                    new DisclosureRule
                    {
                        When = "org_id IN (@ctx.counting)",
                        Then = Disclosure.AggregateOnly,
                        Tests = [TestShape.Equals],
                    },
                ],
                Otherwise = Disclosure.None,
            },
        ],
    };

    [Theory]
    [MemberData(nameof(Runs))]
    public async Task A_refused_star_carries_the_table_model_it_would_have_had(Run run)
    {
        await using var engine = await EngineAsync(run);

        var refused = await Assert.ThrowsAsync<EntitlementException>(
            () => PrepareAsync(engine, run, "SELECT * FROM members"));

        var refusal = Assert.IsType<EntitlementRefusal>(refused.Refusal);
        Assert.Equal(RefusalReason.PopulationOnly, refusal.Reason);
        Assert.Equal(ChalkErrorCodes.PopulationOnly, refused.Code);
        Assert.EndsWith(" [PopulationOnly]", refused.Message, StringComparison.Ordinal);
        Assert.Equal(run == Run.FromDatabase ? "duck.members" : "main.members", refusal.Table);
        Assert.Equal("national_id", refusal.Column);
        Assert.Equal("a projection to the result", refusal.Use);
        Assert.Equal(["COUNT"], refusal.Permitted);

        var schema = Assert.IsType<ArrowSchema>(refusal.OutputSchema);
        Assert.Equal(["id", "org_id", "national_id"], schema.FieldsList.Select(f => f.Name));
        Assert.Equal(["FULL", "FULL", "AGGREGATE"], schema.FieldsList.Select(Label));

        // The same form a statement that runs is described in: the columns it shares with the
        // refused one come out exactly alike.
        var accepted = await PrepareAsync(engine, run, "SELECT id, org_id, id AS again FROM members");
        foreach (var name in new[] { "id", "org_id" })
        {
            var expected = accepted.Query.OutputSchema.GetFieldByName(name);
            var actual = schema.GetFieldByName(name);
            Assert.Equal(expected.DataType.TypeId, actual.DataType.TypeId);
            Assert.Equal(expected.IsNullable, actual.IsNullable);
        }

        Assert.Equal(ArrowTypeId.StringView, schema.GetFieldByName("national_id").DataType.TypeId);
    }

    [Theory]
    [MemberData(nameof(Runs))]
    public async Task A_guarded_aggregate_in_a_refused_statement_allows_nulls_as_it_would_running(Run run)
    {
        await using var engine = await EngineAsync(run);

        // Refused at its predicate, which is met before the COUNT above it.
        var refused = await Assert.ThrowsAsync<EntitlementException>(
            () => PrepareAsync(
                engine,
                run,
                "SELECT org_id, COUNT(national_id) AS n FROM members WHERE national_id <> 'x' GROUP BY org_id"));
        Assert.Equal("a predicate", refused.Refusal!.Use);
        var described = refused.Refusal.OutputSchema!.ToDataTable().Columns["n"]!;

        // Without the predicate the statement runs, and a group-size floor of three lets n be NULL
        // below it: the refused statement's model says the same.
        var running = (await PrepareAsync(
                engine, run, "SELECT org_id, COUNT(national_id) AS n FROM members GROUP BY org_id"))
            .Query.OutputSchema.ToDataTable().Columns["n"]!;
        Assert.True(running.AllowDBNull);
        Assert.Equal(running.AllowDBNull, described.AllowDBNull);
        Assert.Equal(running.DataType, described.DataType);
    }

    [Fact]
    public async Task The_same_refusal_is_answered_from_memory_with_the_same_model()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var planner = new CountingPlanner(sidecar.CreatePlanner());
        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "structured-refusals",
            Sources = [InProcessSource()],
            Planner = planner,
        });
        var entitled = engine.WithEntitlements();

        var first = await Assert.ThrowsAsync<EntitlementException>(
            async () => await entitled.PrepareAsync("SELECT * FROM members", Counter(1, 2)));
        var asked = planner.Plans;
        var second = await Assert.ThrowsAsync<EntitlementException>(
            async () => await entitled.PrepareAsync("SELECT * FROM members", Counter(1, 2)));

        // Not asked again, and one model for both: a host that keeps it keeps one copy.
        Assert.Equal(asked, planner.Plans);
        Assert.NotSame(first, second);
        Assert.Same(first.Refusal, second.Refusal);
        Assert.Equal(first.Message, second.Message);
        Assert.Equal(ChalkErrorCodes.PopulationOnly, second.Code);

        // Another statement, or another principal's values, is another request.
        await Assert.ThrowsAsync<EntitlementException>(
            async () => await entitled.PrepareAsync("SELECT national_id FROM members", Counter(1, 2)));
        Assert.Equal(asked + 1, planner.Plans);
        await Assert.ThrowsAsync<EntitlementException>(
            async () => await entitled.PrepareAsync("SELECT * FROM members", Counter(1)));
        Assert.Equal(asked + 2, planner.Plans);
    }

    [Fact]
    public async Task A_star_refused_before_its_columns_are_decided_carries_no_table_model()
    {
        await using var engine = await EngineAsync(Run.BoundAtPrepare);

        var refused = await Assert.ThrowsAsync<EntitlementException>(
            async () => await engine
                .WithEntitlements(new EntitlementsOptions { StarPolicy = StarPolicy.RefuseWhenEntitled })
                .PrepareAsync("SELECT * FROM members", Counter(1, 2)));

        var refusal = Assert.IsType<EntitlementRefusal>(refused.Refusal);
        Assert.Equal(RefusalReason.Star, refusal.Reason);
        Assert.Null(refusal.OutputSchema);
    }

    [Fact]
    public async Task A_prepared_statements_model_becomes_an_empty_data_table()
    {
        await using var engine = await EngineAsync(Run.BoundAtPrepare);
        // The catalog carries an entitlement, so even this unentitled table is prepared with a context.
        var prepared = await engine.PrepareAsync("SELECT * FROM profiles", Counter(1, 2));

        var table = prepared.OutputSchema.ToDataTable("profiles");

        Assert.Equal("profiles", table.TableName);
        Assert.Empty(table.Rows);
        Assert.Equal(
            [
                ("id", typeof(int), false),
                ("day", typeof(DateOnly), false),
                ("at", typeof(TimeOnly), false),
                ("stamp", typeof(DateTime), false),
                ("stamp_tz", typeof(DateTimeOffset), false),
                ("span", typeof(TimeSpan), false),
                ("uid", typeof(Guid), false),
                ("contact", typeof(object), true),
            ],
            table.Columns.Cast<DataColumn>().Select(c => (c.ColumnName, c.DataType, c.AllowDBNull)));

        // A DataTable has no nested columns: the composite's fields are described beside it, as the
        // schema's struct declares them.
        var fields = Assert.IsType<DataColumn[]>(
            table.Columns["contact"]!.ExtendedProperties[SchemaTableExtensions.CompositeFieldsProperty]);
        var declared = Assert.IsType<StructType>(prepared.OutputSchema.GetFieldByName("contact").DataType);
        Assert.Equal(
            declared.Fields.Select(f => (f.Name, f.IsNullable)),
            fields.Select(c => (c.ColumnName, c.AllowDBNull)));
        Assert.Equal([typeof(string), typeof(int)], fields.Select(c => c.DataType));
    }

    [Fact]
    public async Task A_refusals_model_becomes_a_data_table_with_its_labels()
    {
        await using var engine = await EngineAsync(Run.BoundAtPrepare);
        var refused = await Assert.ThrowsAsync<EntitlementException>(
            () => PrepareAsync(engine, Run.BoundAtPrepare, "SELECT * FROM members"));

        var table = refused.Refusal!.OutputSchema!.ToDataTable();

        Assert.Equal(["id", "org_id", "national_id"], table.Columns.Cast<DataColumn>().Select(c => c.ColumnName));
        var column = table.Columns["national_id"]!;
        Assert.Equal(typeof(string), column.DataType);
        Assert.Equal("AGGREGATE", column.ExtendedProperties[SchemaTableExtensions.DisclosureProperty]);
    }

    [Fact]
    public void A_repeated_column_name_is_numbered_as_FillSchema_numbers_it()
    {
        var schema = new ArrowSchema(
            [
                new Field("id", Int32Type.Default, false),
                new Field("id", Int32Type.Default, false),
                new Field("id", Int32Type.Default, true),
            ],
            null);

        Assert.Equal(
            ["id", "id1", "id2"],
            schema.ToDataTable().Columns.Cast<DataColumn>().Select(c => c.ColumnName));
    }

    [Fact]
    public void The_memory_keys_on_the_whole_request_and_forgets_past_its_bound()
    {
        // What belongs to one call — the id a stop addresses — is not in the key.
        static PlanRequest Asked(string call, string shapeVersion = "7") => new()
        {
            Sql = "SELECT 1",
            ContextId = "c",
            CatalogEpoch = 1,
            ShapeVersion = shapeVersion,
            PlanningRequestId = call,
            Planning = new PlanningOptions { Priority = PlanningPriority.High },
        };
        Assert.Equal(RefusalMemo.Key(Asked("one call")), RefusalMemo.Key(Asked("another call")));

        // The catalog version is: a changed catalog is another request.
        Assert.NotEqual(RefusalMemo.Key(Asked("one call")), RefusalMemo.Key(Asked("one call", shapeVersion: "8")));

        var memo = new RefusalMemo();
        Assert.False(memo.Any);
        for (var i = 0; i <= RefusalMemo.Capacity; i++)
        {
            memo.Remember(new RefusalMemo.Remembered(
                $"k{i}", [new ChalkViolation(ChalkErrorCodes.Star, "refused", PlanErrorKinds.Policy)], "refused", null, []));
        }

        Assert.True(memo.Any);
        Assert.Equal(RefusalMemo.Capacity, memo.Count);
        Assert.Null(memo.Find("k0"));
        Assert.NotNull(memo.Find($"k{RefusalMemo.Capacity}"));
    }

    // ---------------------------------------------------------------- the harness

    private static string? Label(Field field) =>
        field.Metadata is { } metadata && metadata.TryGetValue("chalk.disclosure", out var label) ? label : null;

    private async Task<ChalkEngine> EngineAsync(Run run)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        return await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "structured-refusals",
            Sources = [run == Run.FromDatabase ? DatabaseSource() : InProcessSource()],
            Planner = sidecar.CreatePlanner(),
        });
    }

    private static async Task<EntitledQuery> PrepareAsync(ChalkEngine engine, Run run, string sql)
    {
        var principal = Counter(1, 2);
        var prepareWith = run switch
        {
            Run.PartiallyBound => principal.Shape(["people"]),
            Run.BoundAtExecute => principal.Shape(),
            _ => principal,
        };
        return await engine.WithEntitlements().PrepareAsync(sql, prepareWith);
    }

    private static ISourceRuntime InProcessSource() =>
        new PocoSourceBuilder("mem")
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .AddTable("members", Members, t => t.UniqueKey(m => m.Id).Entitlement(MembersEntitlement))
            .AddTable("profiles", Profiles, t => t.UniqueKey(p => p.Id))
            .Build();

    /// <summary>The same members, and their policy, in a DuckDB database.</summary>
    private ISourceRuntime DatabaseSource()
    {
        Assert.SkipWhen(RemoteFixture.SkipReason is not null, RemoteFixture.SkipReason ?? string.Empty);

        var file = DuckDbFile.Create("structured-refusals");
        _files.Add(file);
        var connection = new DuckDBConnection(file.ConnectionString);
        connection.Open();
        _connections.Add(connection);

        Execute(
            connection,
            "CREATE TABLE members (id INTEGER NOT NULL, org_id INTEGER NOT NULL, national_id VARCHAR NOT NULL)");
        foreach (var member in Members)
        {
            Execute(
                connection, "INSERT INTO members VALUES ($1, $2, $3)", member.Id, member.OrgId, member.NationalId);
        }

        return new AdoSourceBuilder("duck", () => new DuckDBConnection(file.ConnectionString), "duck")
            .Dialect(DialectProfiles.DuckDb)
            .Options(TestTimeouts.SourceOptions)
            .Capabilities(AdoCapabilities.For(DialectProfiles.DuckDb))
            .RowCounts(AdoSourceBuilder.RowCountMode.Exact)
            .DiscoverTables()
            .Entitlement("members", MembersEntitlement)
            .Build();
    }

    private static void Execute(DbConnection connection, string sql, params object?[] values)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var value in values)
        {
            command.Parameters.Add(new DuckDBParameter(value ?? DBNull.Value));
        }

        command.ExecuteNonQuery();
    }

    /// <summary>The sidecar's planner, counting how often it is asked for a plan.</summary>
    private sealed class CountingPlanner(IQueryPlanner inner) : IQueryPlanner
    {
        private int _plans;

        internal int Plans => Volatile.Read(ref _plans);

        public ValueTask<PlannerInfo> GetInfoAsync(CancellationToken ct = default) => inner.GetInfoAsync(ct);

        public bool AcceptsCatalogDeltas => inner.AcceptsCatalogDeltas;

        public ValueTask RegisterCatalogAsync(CatalogRegistration registration, CancellationToken ct = default) =>
            inner.RegisterCatalogAsync(registration, ct);

        public ValueTask RegisterStatisticsAsync(StatisticsRegistration statistics, CancellationToken ct = default) =>
            inner.RegisterStatisticsAsync(statistics, ct);

        public ValueTask<PlanResult> PlanAsync(PlanRequest request, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _plans);
            return inner.PlanAsync(request, ct);
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}

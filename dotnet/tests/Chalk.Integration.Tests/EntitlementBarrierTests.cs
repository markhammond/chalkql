using System.Data.Common;
using Chalk.Arrow;
using Chalk.Catalog;
using Chalk.Client;
using Chalk.Entitlements;
using Chalk.Entitlements.Tenancy;
using Chalk.Sources;
using Chalk.Sources.Ado;
using Chalk.Sources.Poco;
using Chalk.TestKit;
using DuckDB.NET.Data;
using CatalogContext = Chalk.Catalog.CatalogContext;

namespace Chalk.Integration.Tests;

/// <summary>
/// An entitled relation is a barrier (F288): what a statement does with its rows is evaluated on
/// the rows the entitlement admits, so an error names no value of a row the principal is not shown,
/// and whether a statement fails says nothing about such a row.
/// </summary>
/// <remarks>
/// <para>
/// A condition put round an entitled statement, or written in its own <c>WHERE</c>, was merged into
/// the leaf's row filter and evaluated with it over every row the table held. A cast that failed
/// quoted the first row held, whoever asked; a condition that failed only for one value failed
/// exactly when a row with that value existed, admitted or not.
/// </para>
/// <para>
/// Each case runs the ways a host runs a statement: prepared with the principal's context, and
/// prepared once for a shape and run with the principal's context; over rows in the process, and
/// the same rows in DuckDB, which evaluates whatever is pushed to it in an order of its own.
/// </para>
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class EntitlementBarrierTests(SharedSidecar sidecar) : IDisposable
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
        /// <summary>Rows in the process, prepared with the principal's context.</summary>
        BoundAtPrepare,

        /// <summary>Rows in the process, prepared once for a shape and bound per execution.</summary>
        BoundAtExecute,

        /// <summary>The same rows in DuckDB, prepared once for a shape and bound per execution.</summary>
        FromDatabase,
    }

    public static TheoryData<Run> Runs => new() { Run.BoundAtPrepare, Run.BoundAtExecute, Run.FromDatabase };

    private sealed record Track(string TrackId, string Callsign, string MissionId);

    // Held in this order, so the first row the table gives is one the ALD officer is not shown.
    private static readonly Track[] Tracks =
    [
        new("TRK-TEAK11", "TEAK 11", "KESTREL"),
        new("TRK-BANYAN41", "BANYAN 41", "SENTINEL"),
        new("TRK-PETREL22", "PETREL 22", "ALD"),
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// A cast that fails, round the statement and in its own <c>WHERE</c>, fails on a row the
    /// principal is shown and names that row's value, never another's.
    /// </summary>
    [Theory]
    [MemberData(nameof(Runs))]
    public async Task A_failing_condition_names_only_a_row_the_principal_is_shown(Run run)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var fixture = await Fixture.CreateAsync(this, sidecar, run);

        foreach (var sql in new[]
                 {
                     "SELECT * FROM (SELECT TrackId, Callsign FROM s.tracks) AS t WHERE CAST(Callsign AS DOUBLE) > 1",
                     "SELECT TrackId FROM s.tracks WHERE CAST(Callsign AS DOUBLE) > 1",
                 })
        {
            var error = await Assert.ThrowsAnyAsync<Exception>(() => fixture.IdsAsync(sql, "ALD"));
            var said = Said(error);
            Assert.DoesNotContain("TEAK 11", said, StringComparison.Ordinal);
            Assert.DoesNotContain("BANYAN 41", said, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A condition that fails only for one value is no test of whether a row with that value exists:
    /// guessing a callsign the principal is not shown runs as a guess of one nobody has does, and
    /// fails only for a principal shown the row.
    /// </summary>
    [Theory]
    [MemberData(nameof(Runs))]
    public async Task Failing_says_nothing_of_a_row_the_principal_is_not_shown(Run run)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var fixture = await Fixture.CreateAsync(this, sidecar, run);

        foreach (var guess in new[]
                 {
                     "CAST(CASE WHEN Callsign = '{0}' THEN 'x' ELSE '0' END AS INTEGER) = 0",
                     "1 / (CASE WHEN Callsign = '{0}' THEN 0 ELSE 1 END) = 1",
                 })
        {
            string Sql(string callsign) =>
                "SELECT * FROM (SELECT TrackId, Callsign FROM s.tracks) AS t WHERE "
                + string.Format(System.Globalization.CultureInfo.InvariantCulture, guess, callsign);

            Assert.Equal(["TRK-PETREL22"], await fixture.IdsAsync(Sql("BANYAN 41"), "ALD"));
            Assert.Equal(["TRK-PETREL22"], await fixture.IdsAsync(Sql("NO SUCH 99"), "ALD"));
            await Assert.ThrowsAnyAsync<Exception>(() => fixture.IdsAsync(Sql("BANYAN 41"), "SENTINEL"));
        }
    }

    /// <summary>
    /// A condition that cannot leak still reaches the leaf: the rows it keeps are the same, and in
    /// DuckDB it is part of the query the source is sent.
    /// </summary>
    [Theory]
    [MemberData(nameof(Runs))]
    public async Task A_condition_that_cannot_leak_still_reaches_the_leaf(Run run)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var fixture = await Fixture.CreateAsync(this, sidecar, run);

        const string Sql = "SELECT TrackId FROM s.tracks WHERE Callsign = 'PETREL 22' OR Callsign = 'TEAK 11'";
        Assert.Equal(["TRK-PETREL22"], await fixture.IdsAsync(Sql, "ALD"));
        Assert.Equal(["TRK-PETREL22", "TRK-TEAK11"], (await fixture.IdsAsync(Sql, "ALD", "KESTREL")).Order().ToList());

        if (run == Run.FromDatabase)
        {
            var remote = await fixture.RemoteQueryAsync(Sql, "ALD");
            Assert.Contains("PETREL 22", remote, StringComparison.Ordinal);
        }
    }

    /// <summary>Everything an exception says, its inner ones included.</summary>
    private static string Said(Exception error)
    {
        var text = new System.Text.StringBuilder();
        for (Exception? e = error; e is not null; e = e.InnerException)
        {
            text.Append(e.Message).Append('\n');
        }

        return text.ToString();
    }

    /// <summary>
    /// Tracks kept by mission, a reader granted in some. Prepared as the run says, executed with the
    /// principal's context.
    /// </summary>
    private sealed class Fixture : IAsyncDisposable
    {
        private TenancyEntitlements _entitlements = null!;
        private Kind _mission;
        private Role _reader;
        private Run _run;

        public ChalkEngine Engine { get; private set; } = null!;

        public static async Task<Fixture> CreateAsync(EntitlementBarrierTests owner, SharedSidecar sidecar, Run run)
        {
            var fixture = new Fixture { _run = run };
            var fromDatabase = run == Run.FromDatabase;
            var catalog = new CatalogContext
            {
                ContextId = "entitlement-barrier",
                Schemas = [(fromDatabase ? owner.InDatabase(null) : InProcess(null)).DescribeSchema()],
            };
            var policy = TenancyPolicy.Declare(catalog);
            fixture._mission = policy.Tenancy("mission");
            fixture._reader = policy.Role("reader");
            var table = policy.Source("s").Table("tracks");
            table.Tenancy(t => t.Direct(fixture._mission, table.Column("MissionId")));
            fixture._entitlements = policy.Compile(catalog);

            fixture.Engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
            {
                ContextId = "entitlement-barrier",
                Sources = [fromDatabase ? owner.InDatabase(fixture._entitlements) : InProcess(fixture._entitlements)],
                Planner = sidecar.CreatePlanner(),
            }, Ct);
            return fixture;
        }

        public async Task<List<string>> IdsAsync(string sql, params string[] missions)
        {
            var context = Bind(missions);
            var entitled = Engine.WithEntitlements();
            var rows = new List<string>();
            QueryExecution execution = _run == Run.BoundAtPrepare
                ? await Engine.ExecuteAsync((await entitled.PrepareAsync(sql, context, ct: Ct)).Query, ct: Ct)
                : await Engine.ExecuteAsync(await entitled.PrepareAsync(sql, context.Shape(), ct: Ct), context, ct: Ct);
            await using (execution)
            {
                await foreach (var batch in execution.Batches.WithCancellation(Ct))
                {
                    using (batch)
                    {
                        rows.AddRange(batch.ToRows().Select(r => (string)r[0]!));
                    }
                }
            }

            return rows;
        }

        public async Task<string> RemoteQueryAsync(string sql, params string[] missions)
        {
            var prepared = await Engine.WithEntitlements().PrepareAsync(sql, Bind(missions), ct: Ct);
            return string.Join(
                "\n",
                Chalk.Ir.PlanWalker.Rels(prepared.Query.Plan)
                    .Where(r => r.KindCase == Chalk.Ir.Rel.KindOneofCase.RemoteQuery)
                    .Select(r => r.RemoteQuery.QueryText));
        }

        public ValueTask DisposeAsync() => Engine.DisposeAsync();

        private RequestContext Bind(string[] missions) => _entitlements.Bind(new TenancyPrincipal
        {
            User = "u",
            Grants = [.. missions.Select(m => Grant.ForTenancy(_mission, m, _reader))],
        });
    }

    private static ISourceRuntime InProcess(TenancyEntitlements? entitlements) =>
        new PocoSourceBuilder("s", "s")
            .AddTable("tracks", Tracks, t =>
            {
                t.UniqueKey(x => x.TrackId);
                if (entitlements?.For("s", "tracks") is { } d) t.Entitlement(d);
            })
            .Build();

    /// <summary>The same table and rows in DuckDB, declared rather than discovered.</summary>
    private ISourceRuntime InDatabase(TenancyEntitlements? entitlements)
    {
        Assert.SkipWhen(RemoteFixture.SkipReason is not null, RemoteFixture.SkipReason ?? string.Empty);

        var file = DuckDbFile.Create("entitlement-barrier");
        _files.Add(file);
        var connection = new DuckDBConnection(file.ConnectionString);
        connection.Open();
        _connections.Add(connection);
        Execute(connection, "CREATE TABLE \"tracks\" (\"TrackId\" VARCHAR NOT NULL PRIMARY KEY, \"Callsign\" VARCHAR NOT NULL, \"MissionId\" VARCHAR NOT NULL)");
        foreach (var track in Tracks)
        {
            Execute(connection, $"INSERT INTO \"tracks\" VALUES ('{track.TrackId}', '{track.Callsign}', '{track.MissionId}')");
        }

        var builder = new AdoSourceBuilder("s-db", () => new DuckDBConnection(file.ConnectionString), "s")
            .Dialect(DialectProfiles.DuckDb)
            .Options(TestTimeouts.SourceOptions)
            .Capabilities(AdoCapabilities.For(DialectProfiles.DuckDb))
            .RowCounts(AdoSourceBuilder.RowCountMode.Exact)
            .AddTable(
                "tracks",
                [
                    new ColumnDescriptor { Name = "TrackId", Type = ChalkType.String() },
                    new ColumnDescriptor { Name = "Callsign", Type = ChalkType.String() },
                    new ColumnDescriptor { Name = "MissionId", Type = ChalkType.String() },
                ],
                configure: t => t.UniqueKey("TrackId"));
        if (entitlements?.For("s", "tracks") is { } descriptor)
        {
            builder.Entitlement("tracks", descriptor);
        }

        return builder.Build();
    }

    private static void Execute(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}

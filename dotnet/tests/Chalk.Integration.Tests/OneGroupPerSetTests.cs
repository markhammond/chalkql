using System.Data.Common;
using System.Text.RegularExpressions;
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
/// A policy that declares no combination binds each set of kinds once on a route: a grant fills one
/// list, and its authority folds to its conjunction once (F165).
/// </summary>
/// <remarks>
/// <para>
/// Two tables. One holds three kinds on its row: a grant over all three filled a list per anchor —
/// three lists holding the same identifiers in three column orders — and folded to the one
/// conjunction written three ways. The other is Kestrel's tasking, which holds its mission and
/// reaches its asset's classification and releasability down the same steps: there a set is anchored
/// on a path's kind, and {releasability, mission} keeps its group on the releasability path although
/// mission is declared first.
/// </para>
/// <para>
/// The rows never changed and do not now. Each case runs the ways a host runs a statement: bound as
/// it is prepared, bound partially — the grant's lists left to execution — bound as it runs, and
/// partially over the same rows in DuckDB.
/// </para>
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class OneGroupPerSetTests(SharedSidecar sidecar) : IDisposable
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

        /// <summary>Rows in the process, the grant's lists left to execution.</summary>
        PartiallyBound,

        /// <summary>Rows in the process, every list left to execution.</summary>
        BoundAtExecute,

        /// <summary>The same rows in DuckDB, the grant's lists left to execution.</summary>
        FromDatabase,
    }

    public static TheoryData<Run> Runs =>
        new() { Run.BoundAtPrepare, Run.PartiallyBound, Run.BoundAtExecute, Run.FromDatabase };

    private sealed record Row(int Id, string K1, string K2, string K3);

    private sealed record Asset(string AssetId, string Classification, string Releasability);

    private sealed record Tasking(string TaskingId, string AssetId, string MissionId);

    private static readonly Row[] Rows =
    [
        new(1, "a", "b", "c"),
        new(2, "a", "b", "x"),
        new(3, "a", "y", "c"),
        new(4, "z", "b", "c"),
    ];

    private static readonly Asset[] Assets =
    [
        new("A-1", "SECRET", "REL_FVEY"),
        new("A-2", "TOP SECRET", "REL_AUS_USA"),
    ];

    private static readonly Tasking[] Taskings =
    [
        new("T-1", "A-1", "KESTREL"),
        new("T-2", "A-1", "SENTINEL"),
        new("T-3", "A-2", "KESTREL"),
    ];

    // ---------------------------------------------------------------- three kinds on one row

    public static TheoryData<Run, int> RunsAndSpellings()
    {
        var data = new TheoryData<Run, int>();
        foreach (var run in new[] { Run.BoundAtPrepare, Run.PartiallyBound, Run.BoundAtExecute, Run.FromDatabase })
        {
            foreach (var first in new[] { 1, 2, 3 })
            {
                data.Add(run, first);
            }
        }

        return data;
    }

    /// <summary>
    /// Spelt from any of its kinds, the grant reads row 1 and fills one list; bound as the statement
    /// is prepared, its authority folds to one conjunction.
    /// </summary>
    [Theory]
    [MemberData(nameof(RunsAndSpellings))]
    public async Task A_grant_over_three_kinds_on_one_row_fills_one_list(Run run, int first)
    {
        var answer = await RowAsync(run, first);

        Assert.Equal(["1"], answer.Rows);
        Assert.Equal(1, answer.Filled);
        if (run == Run.BoundAtPrepare)
        {
            Assert.Single(Regex.Matches(answer.Folded!, @"AND\("));
        }
    }

    // ---------------------------------------------------------------- along a path

    /// <summary>The three-kind grant reads the KESTREL tasking of the SECRET asset, whichever kind it names first.</summary>
    [Theory]
    [MemberData(nameof(Runs))]
    public async Task A_three_kind_grant_reaches_the_tasking_through_its_asset(Run run)
    {
        foreach (var spelling in new Func<PathModel, Grant>[]
        {
            m => Grant.ForTenancy(m.Classification, "SECRET", m.Reader)
                .Within(m.Releasability, "REL_FVEY").Within(m.Mission, "KESTREL"),
            m => Grant.ForTenancy(m.Mission, "KESTREL", m.Reader)
                .Within(m.Releasability, "REL_FVEY").Within(m.Classification, "SECRET"),
        })
        {
            var answer = await TaskingsAsync(run, spelling);

            Assert.Equal(["T-1"], answer.Rows);
            Assert.Equal(1, answer.Filled);
        }
    }

    /// <summary>
    /// {releasability, mission} is answered on the releasability path alone, although mission comes
    /// first: both spellings read the SENTINEL tasking of the REL_FVEY asset.
    /// </summary>
    [Theory]
    [MemberData(nameof(Runs))]
    public async Task A_set_with_a_kind_the_tasking_holds_is_answered_on_the_path(Run run)
    {
        foreach (var spelling in new Func<PathModel, Grant>[]
        {
            m => Grant.ForTenancy(m.Mission, "SENTINEL", m.Reader).Within(m.Releasability, "REL_FVEY"),
            m => Grant.ForTenancy(m.Releasability, "REL_FVEY", m.Reader).Within(m.Mission, "SENTINEL"),
        })
        {
            var answer = await TaskingsAsync(run, spelling);

            Assert.Equal(["T-2"], answer.Rows);
            Assert.Equal(1, answer.Filled);
        }
    }

    // ---------------------------------------------------------------- the harness

    /// <summary>The rows, the lists the grant filled, and — bound at prepare — the authority folded.</summary>
    private sealed record Answer(string[] Rows, int Filled, string? Folded);

    private sealed record PathModel(
        TenancyEntitlements Entitlements,
        TenancyPolicy Policy,
        Kind Classification,
        Kind Releasability,
        Kind Mission,
        Role Reader);

    private async Task<Answer> RowAsync(Run run, int first)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        var fromDatabase = run == Run.FromDatabase;
        ISourceRuntime Source(TenancyEntitlements? e) => fromDatabase ? RowsInDatabase(e) : RowsInProcess(e);

        var catalog = new CatalogContext { ContextId = "one-per-set", Schemas = [Source(null).DescribeSchema()] };
        var policy = TenancyPolicy.Declare(catalog);
        Kind[] kinds = [policy.Tenancy("k1"), policy.Tenancy("k2"), policy.Tenancy("k3")];
        var reader = policy.Role("reader");
        var table = policy.Source("s").Table("t");
        table.Tenancy(x => x
            .Direct(kinds[0], table.Column("K1"))
            .Direct(kinds[1], table.Column("K2"))
            .Direct(kinds[2], table.Column("K3")));
        var entitlements = policy.Compile(catalog);

        string[] ids = ["a", "b", "c"];
        var head = first - 1;
        var grant = Grant.ForTenancy(kinds[head], ids[head], reader);
        for (var i = 0; i < 3; i++)
        {
            if (i != head)
            {
                grant = grant.Within(kinds[i], ids[i]);
            }
        }

        return await AnswerAsync(
            run, entitlements, [Source(entitlements)], [], grant, "SELECT Id FROM s.t ORDER BY Id");
    }

    private async Task<Answer> TaskingsAsync(Run run, Func<PathModel, Grant> grant)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        var fromDatabase = run == Run.FromDatabase;
        ISourceRuntime[] Sources(TenancyEntitlements? e) =>
            fromDatabase ? TaskingsInDatabase(e) : TaskingsInProcess(e);

        var catalog = new CatalogContext
        {
            ContextId = "one-per-set",
            Schemas = [.. Sources(null).Select(s => s.DescribeSchema())],
        };
        var policy = TenancyPolicy.Declare(catalog);
        var classification = policy.Tenancy("classification");
        var mission = policy.Tenancy("mission");
        var releasability = policy.Tenancy("releasability");
        var reader = policy.Role("tasking_reader");

        var assets = policy.Source("aus").Table("assets");
        assets.Tenancy(t => t
            .Direct(classification, assets.Column("Classification"))
            .Direct(releasability, assets.Column("Releasability")));
        var tasking = policy.Source("coalition").Table("tasking");
        tasking.Tenancy(t => t
            .Direct(mission, tasking.Column("MissionId"))
            .Inherited(classification).Through(assets)
            .Inherited(releasability).Through(assets));
        tasking.Column("AssetId").References(assets.Column("AssetId"));

        var model = new PathModel(
            policy.Compile(catalog), policy, classification, releasability, mission, reader);
        return await AnswerAsync(
            run,
            model.Entitlements,
            Sources(model.Entitlements),
            policy.Associations,
            grant(model),
            "SELECT TaskingId FROM coalition.tasking ORDER BY TaskingId");
    }

    private async Task<Answer> AnswerAsync(
        Run run,
        TenancyEntitlements entitlements,
        ISourceRuntime[] sources,
        IReadOnlyList<AssociationDescriptor> associations,
        Grant grant,
        string sql)
    {
        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "one-per-set",
            Sources = sources,
            Associations = associations,
            Planner = sidecar.CreatePlanner(),
        });

        var context = entitlements.Bind(new TenancyPrincipal { User = "u", Grants = [grant] });
        var filled = context.Lists.Where(l => l.Value.Rows.Count > 0).Select(l => l.Key).ToArray();
        var prepareWith = run switch
        {
            Run.BoundAtPrepare => context,
            Run.BoundAtExecute => context.Shape(),
            _ => context.Shape(filled),
        };

        var audit = new FilledLists();
        var entitled = engine.WithEntitlements(audit: audit);
        var folded = run == Run.BoundAtPrepare
            ? string.Join(" ", (await entitled.ExplainAsync(sql, context)).Tables.Select(t => t.RowPredicate))
            : null;
        var prepared = await entitled.PrepareAsync(sql, prepareWith);
        var rows = await ConfinementDirectionTests.RowsAsync(
            engine, prepared, run == Run.BoundAtPrepare ? null : context);

        // Bound as it is prepared, the audit event counts the same lists. Bound later, it counts the
        // context the statement was prepared with, which holds no rows (F166), so it says nothing here.
        if (run == Run.BoundAtPrepare)
        {
            Assert.Equal(filled.Length, audit.Filled);
        }
        return new Answer(rows, filled.Count(name => !name.EndsWith("_ids", StringComparison.Ordinal)), folded);
    }

    private sealed class FilledLists : IEntitlementsAudit
    {
        public int Filled { get; private set; }

        public void Executed(EntitlementsAuditEvent audited) =>
            Filled = audited.ContextListRowCounts.Count(list => list.Value > 0);
    }

    private static ISourceRuntime RowsInProcess(TenancyEntitlements? entitlements) =>
        new PocoSourceBuilder("s", "s")
            .AddTable("t", Rows, t =>
            {
                t.UniqueKey(x => x.Id);
                if (entitlements?.For("s", "t") is { } d) t.Entitlement(d);
            })
            .Build();

    private static ISourceRuntime[] TaskingsInProcess(TenancyEntitlements? entitlements) =>
    [
        new PocoSourceBuilder("aus", "aus")
            .AddTable("assets", Assets, t =>
            {
                t.UniqueKey(x => x.AssetId);
                if (entitlements?.For("aus", "assets") is { } d) t.Entitlement(d);
            })
            .Build(),
        new PocoSourceBuilder("coalition", "coalition")
            .AddTable("tasking", Taskings, t =>
            {
                t.UniqueKey(x => x.TaskingId);
                if (entitlements?.For("coalition", "tasking") is { } d) t.Entitlement(d);
            })
            .Build(),
    ];

    private ISourceRuntime RowsInDatabase(TenancyEntitlements? entitlements)
    {
        Assert.SkipWhen(RemoteFixture.SkipReason is not null, RemoteFixture.SkipReason ?? string.Empty);

        var file = Database(
            "one-per-set-rows",
            "CREATE TABLE \"t\" (\"Id\" INTEGER NOT NULL PRIMARY KEY, \"K1\" VARCHAR NOT NULL, "
            + "\"K2\" VARCHAR NOT NULL, \"K3\" VARCHAR NOT NULL)",
            Rows.Select(r => $"INSERT INTO \"t\" VALUES ({r.Id}, '{r.K1}', '{r.K2}', '{r.K3}')"));
        return Source(
            "s",
            file,
            "t",
            [
                new ColumnDescriptor { Name = "Id", Type = ChalkType.Int32() },
                Text("K1"),
                Text("K2"),
                Text("K3"),
            ],
            entitlements?.For("s", "t"));
    }

    private ISourceRuntime[] TaskingsInDatabase(TenancyEntitlements? entitlements)
    {
        Assert.SkipWhen(RemoteFixture.SkipReason is not null, RemoteFixture.SkipReason ?? string.Empty);

        var aus = Database(
            "one-per-set-aus",
            "CREATE TABLE \"assets\" (\"AssetId\" VARCHAR NOT NULL PRIMARY KEY, "
            + "\"Classification\" VARCHAR NOT NULL, \"Releasability\" VARCHAR NOT NULL)",
            Assets.Select(a => $"INSERT INTO \"assets\" VALUES ('{a.AssetId}', '{a.Classification}', '{a.Releasability}')"));
        var coalition = Database(
            "one-per-set-coalition",
            "CREATE TABLE \"tasking\" (\"TaskingId\" VARCHAR NOT NULL PRIMARY KEY, "
            + "\"AssetId\" VARCHAR NOT NULL, \"MissionId\" VARCHAR NOT NULL)",
            Taskings.Select(t => $"INSERT INTO \"tasking\" VALUES ('{t.TaskingId}', '{t.AssetId}', '{t.MissionId}')"));

        return
        [
            Source("aus", aus, "assets", [Text("AssetId"), Text("Classification"), Text("Releasability")],
                entitlements?.For("aus", "assets")),
            Source("coalition", coalition, "tasking", [Text("TaskingId"), Text("AssetId"), Text("MissionId")],
                entitlements?.For("coalition", "tasking")),
        ];
    }

    private static ColumnDescriptor Text(string name) => new() { Name = name, Type = ChalkType.String() };

    private DuckDbFile Database(string name, string create, IEnumerable<string> inserts)
    {
        var file = DuckDbFile.Create(name);
        _files.Add(file);
        var connection = new DuckDBConnection(file.ConnectionString);
        connection.Open();
        _connections.Add(connection);

        Execute(connection, create);
        foreach (var insert in inserts)
        {
            Execute(connection, insert);
        }

        return file;
    }

    /// <summary>A table declared rather than discovered, its first column the key.</summary>
    private static AdoSource Source(
        string schema,
        DuckDbFile file,
        string table,
        IReadOnlyList<ColumnDescriptor> columns,
        TableEntitlementDescriptor? descriptor)
    {
        var builder = new AdoSourceBuilder(schema + "-db", () => new DuckDBConnection(file.ConnectionString), schema)
            .Dialect(DialectProfiles.DuckDb)
            .Options(TestTimeouts.SourceOptions)
            .Capabilities(AdoCapabilities.For(DialectProfiles.DuckDb))
            .RowCounts(AdoSourceBuilder.RowCountMode.Exact)
            .AddTable(table, columns, configure: t => t.UniqueKey(columns[0].Name));
        if (descriptor is not null)
        {
            builder.Entitlement(table, descriptor);
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

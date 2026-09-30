using System.Data.Common;
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
/// A policy that declares its combinations, over a table whose own row answers none of them: the
/// engine registers it, and every host path reads it (F164).
/// </summary>
/// <remarks>
/// <para>
/// Kestrel's asset and tasking families. An asset holds classification and releasability; a tasking
/// holds its mission and inherits the asset's two across an association. The policy declares
/// {classification, releasability} for the asset reader and {classification, releasability,
/// mission} for the tasking reader, so the tasking's combination is answered along the path to the
/// asset, beside the mission on the tasking's own row, and the tasking's row predicate is the role
/// flags alone. The engine refused that descriptor as not boolean, and nothing started.
/// </para>
/// <para>
/// Two assets, both REL_FVEY, one SECRET and one TOP SECRET; three taskings, of which only T-1 is a
/// KESTREL tasking of the SECRET asset. Each case runs the ways a host runs a statement: bound as it
/// is prepared, bound partially — the tasking reader's lists left to execution — bound as it runs,
/// and partially over the same rows in two DuckDB databases.
/// </para>
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class DeclaredPathRegistrationTests(SharedSidecar sidecar) : IDisposable
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

        /// <summary>Rows in the process, the tasking reader's lists left to execution.</summary>
        PartiallyBound,

        /// <summary>Rows in the process, every list left to execution.</summary>
        BoundAtExecute,

        /// <summary>The same rows in two DuckDB databases, the tasking reader's lists left to execution.</summary>
        FromDatabase,
    }

    public static TheoryData<Run> Runs =>
        new() { Run.BoundAtPrepare, Run.PartiallyBound, Run.BoundAtExecute, Run.FromDatabase };

    private sealed record Asset(string AssetId, string Classification, string Releasability);

    private sealed record Tasking(string TaskingId, string AssetId, string MissionId);

    private static readonly Asset[] Assets =
    [
        new("A-1", "SECRET", "REL_FVEY"),
        new("A-2", "TOP SECRET", "REL_FVEY"),
    ];

    private static readonly Tasking[] Taskings =
    [
        new("T-1", "A-1", "KESTREL"),
        new("T-2", "A-1", "SENTINEL"),
        new("T-3", "A-2", "KESTREL"),
    ];

    private const string TaskingSql = "SELECT TaskingId FROM coalition.tasking ORDER BY TaskingId";

    /// <summary>The shape under test: the tasking's own row answers no combination, and says only the flags.</summary>
    [Fact]
    public void The_taskings_own_row_answers_no_combination()
    {
        var model = Declare(Catalog(InProcessSources(null)));

        Assert.Equal(
            "(@ctx.global_tasking_reader) OR @ctx.global",
            model.Entitlements.For("coalition", "tasking")!.RowPredicate);
    }

    [Theory]
    [MemberData(nameof(Runs))]
    public async Task A_grant_reaches_the_taskings_its_combination_holds(Run run)
    {
        var rows = await RowsAsync(run, m =>
        [
            Grant.ForTenancy(m.Classification, "SECRET", m.TaskingReader)
                .Within(m.Releasability, "REL_FVEY")
                .Within(m.Mission, "KESTREL"),
        ]);

        Assert.Equal(["T-1"], rows);
    }

    [Theory]
    [MemberData(nameof(Runs))]
    public async Task A_role_wide_grant_reads_every_tasking(Run run)
    {
        var rows = await RowsAsync(run, m => [Grant.Global(m.TaskingReader)]);

        Assert.Equal(["T-1", "T-2", "T-3"], rows);
    }

    [Theory]
    [MemberData(nameof(Runs))]
    public async Task A_grant_of_another_role_reads_no_tasking(Run run)
    {
        var rows = await RowsAsync(run, m =>
        [
            Grant.ForTenancy(m.Classification, "SECRET", m.AssetReader).Within(m.Releasability, "REL_FVEY"),
        ]);

        Assert.Empty(rows);
    }

    // ---------------------------------------------------------------- the harness

    /// <summary>The compiled policy and the handles a grant is written with.</summary>
    private sealed record Model(
        TenancyEntitlements Entitlements,
        TenancyPolicy Policy,
        Kind Classification,
        Kind Releasability,
        Kind Mission,
        Role AssetReader,
        Role TaskingReader);

    private static Model Declare(CatalogContext catalog)
    {
        var policy = TenancyPolicy.Declare(catalog);
        var classification = policy.Tenancy("classification");
        var releasability = policy.Tenancy("releasability");
        var mission = policy.Tenancy("mission");
        var assetReader = policy.Role("asset_reader");
        var taskingReader = policy.Role("tasking_reader");

        var assets = policy.Source("aus").Table("assets");
        assets
            .Tenancy(t => t
                .Direct(classification, assets.Column("Classification"))
                .Direct(releasability, assets.Column("Releasability")))
            .Visible(new VisibilityRule { Roles = [assetReader] });

        var tasking = policy.Source("coalition").Table("tasking");
        tasking
            .Tenancy(t => t
                .Direct(mission, tasking.Column("MissionId"))
                .Inherited(classification).Through(assets)
                .Inherited(releasability).Through(assets))
            .Visible(new VisibilityRule { Roles = [taskingReader] });
        tasking.Column("AssetId").References(assets.Column("AssetId"));

        policy
            .Combination([assetReader], classification, releasability)
            .Combination([taskingReader], classification, releasability, mission)
            .AllowGlobalGrants();

        return new Model(
            policy.Compile(catalog), policy, classification, releasability, mission, assetReader, taskingReader);
    }

    private async Task<string[]> RowsAsync(Run run, Func<Model, IReadOnlyList<Grant>> grants)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        var fromDatabase = run == Run.FromDatabase;
        var model = Declare(Catalog(fromDatabase ? DatabaseSources(null) : InProcessSources(null)));
        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "declared-path",
            Sources = fromDatabase ? DatabaseSources(model.Entitlements) : InProcessSources(model.Entitlements),
            Associations = model.Policy.Associations,
            Planner = sidecar.CreatePlanner(),
        });

        var context = model.Entitlements.Bind(new TenancyPrincipal { User = "u", Grants = grants(model) });
        var taskingLists = context.Lists.Keys
            .Where(name => name.Contains("_tasking_reader", StringComparison.Ordinal))
            .ToArray();
        var prepareWith = run switch
        {
            Run.BoundAtPrepare => context,
            Run.BoundAtExecute => context.Shape(),
            _ => context.Shape(taskingLists),
        };
        var prepared = await engine.WithEntitlements().PrepareAsync(TaskingSql, prepareWith);

        return await ConfinementDirectionTests.RowsAsync(
            engine, prepared, run == Run.BoundAtPrepare ? null : context);
    }

    private static CatalogContext Catalog(ISourceRuntime[] sources) => new()
    {
        ContextId = "declared-path",
        Schemas = [.. sources.Select(s => s.DescribeSchema())],
    };

    private static ISourceRuntime[] InProcessSources(TenancyEntitlements? entitlements) =>
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

    /// <summary>The same tables and rows in two DuckDB databases, one per source.</summary>
    private ISourceRuntime[] DatabaseSources(TenancyEntitlements? entitlements)
    {
        Assert.SkipWhen(RemoteFixture.SkipReason is not null, RemoteFixture.SkipReason ?? string.Empty);

        var aus = Database(
            "declared-path-aus",
            "CREATE TABLE \"assets\" (\"AssetId\" VARCHAR NOT NULL PRIMARY KEY, "
            + "\"Classification\" VARCHAR NOT NULL, \"Releasability\" VARCHAR NOT NULL)",
            Assets.Select(a => $"INSERT INTO \"assets\" VALUES ('{a.AssetId}', '{a.Classification}', '{a.Releasability}')"));
        var coalition = Database(
            "declared-path-coalition",
            "CREATE TABLE \"tasking\" (\"TaskingId\" VARCHAR NOT NULL PRIMARY KEY, "
            + "\"AssetId\" VARCHAR NOT NULL, \"MissionId\" VARCHAR NOT NULL)",
            Taskings.Select(t => $"INSERT INTO \"tasking\" VALUES ('{t.TaskingId}', '{t.AssetId}', '{t.MissionId}')"));

        return
        [
            Source("aus", aus, "assets", ["AssetId", "Classification", "Releasability"], entitlements?.For("aus", "assets")),
            Source("coalition", coalition, "tasking", ["TaskingId", "AssetId", "MissionId"], entitlements?.For("coalition", "tasking")),
        ];
    }

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

    /// <summary>A table of text columns, its first the key, declared rather than discovered.</summary>
    private static AdoSource Source(
        string schema, DuckDbFile file, string table, string[] columns, TableEntitlementDescriptor? descriptor)
    {
        var builder = new AdoSourceBuilder(schema + "-db", () => new DuckDBConnection(file.ConnectionString), schema)
            .Dialect(DialectProfiles.DuckDb)
            .Options(TestTimeouts.SourceOptions)
            .Capabilities(AdoCapabilities.For(DialectProfiles.DuckDb))
            .RowCounts(AdoSourceBuilder.RowCountMode.Exact)
            .AddTable(
                table,
                [.. columns.Select(c => new ColumnDescriptor { Name = c, Type = ChalkType.String() })],
                configure: t => t.UniqueKey(columns[0]));
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

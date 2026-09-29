using Chalk.Catalog;
using Chalk.Client;
using Chalk.Entitlements;
using Chalk.Entitlements.Tenancy;
using Chalk.Sources.Poco;
using Chalk.TestKit;
using CatalogContext = Chalk.Catalog.CatalogContext;

namespace Chalk.Integration.Tests;

/// <summary>
/// A conjunction between a kind on a table's own row and a kind it inherits along a path, spelt
/// both ways round (F158), and the row each kind is read off (F159) — end to end, over a sidecar.
/// </summary>
/// <remarks>
/// <para>
/// Two taskings of one asset that is SECRET and REL_FVEY: <c>T-SECRET</c>, and <c>T-TOPSECRET</c>
/// whose own classification is TOP SECRET. Three layouts of the asset: holding releasability only
/// (A); the same with a report table that holds both kinds on one row, which let the grant bind
/// while reaching no tasking (A′, F158's silent case); and holding a classification of its own under
/// the same column name as the tasking's (B, F159 with F157 underneath).
/// </para>
/// <para>
/// Every spelling must read the tasking's own classification: a SECRET grant sees
/// <c>T-SECRET</c> and a TOP SECRET grant sees <c>T-TOPSECRET</c>, whichever kind the grant names
/// first and whatever the asset's classification is.
/// </para>
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class ConfinementDirectionTests(SharedSidecar sidecar)
{
    private sealed record Asset(string AssetId, string Releasability, string Classification);

    private sealed record Tasking(string TaskingId, string AssetId, string Classification);

    private sealed record Report(string ReportId, string Classification, string Releasability);

    private static readonly Asset[] Assets = [new("A-1", "REL_FVEY", "SECRET")];

    private static readonly Tasking[] Taskings =
    [
        new("T-SECRET", "A-1", "SECRET"),
        new("T-TOPSECRET", "A-1", "TOP SECRET"),
    ];

    public static TheoryData<string, bool, string, string[]> Answers()
    {
        var data = new TheoryData<string, bool, string, string[]>();
        foreach (var layout in new[] { "A", "A'", "B" })
        {
            foreach (var classificationFirst in new[] { true, false })
            {
                data.Add(layout, classificationFirst, "SECRET", ["T-SECRET"]);
                data.Add(layout, classificationFirst, "TOP SECRET", ["T-TOPSECRET"]);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Answers))]
    public async Task Every_spelling_reads_the_taskings_own_classification(
        string layout, bool classificationFirst, string classificationId, string[] expected)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        var endpointHoldsClassification = layout == "B";
        var withReports = layout == "A'";
        var catalog = Catalog(null, withReports);
        var policy = TenancyPolicy.Declare(catalog);
        var classification = policy.Tenancy("classification");
        var releasability = policy.Tenancy("releasability");
        var reader = policy.Role("reader");

        var assets = policy.Source("aus").Table("assets");
        assets.Tenancy(t =>
        {
            t.Direct(releasability, assets.Column("Releasability"));
            if (endpointHoldsClassification)
            {
                t.Direct(classification, assets.Column("Classification"));
            }
        });
        var tasking = policy.Source("coalition").Table("tasking");
        tasking.Tenancy(t => t
            .Direct(classification, tasking.Column("Classification"))
            .Inherited(releasability).Through(assets));
        tasking.Column("AssetId").References(assets.Column("AssetId"));
        if (withReports)
        {
            var reports = policy.Source("partner").Table("reports");
            reports.Tenancy(t => t
                .Direct(classification, reports.Column("Classification"))
                .Direct(releasability, reports.Column("Releasability")));
        }

        var entitlements = policy.Compile(catalog);
        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "direction",
            Sources = Sources(entitlements, withReports),
            Associations = policy.Associations,
            Planner = sidecar.CreatePlanner(),
        });

        var grant = classificationFirst
            ? Grant.ForTenancy(classification, classificationId, reader).Within(releasability, "REL_FVEY")
            : Grant.ForTenancy(releasability, "REL_FVEY", reader).Within(classification, classificationId);
        var prepared = await engine.WithEntitlements().PrepareAsync(
            "SELECT TaskingId FROM coalition.tasking ORDER BY TaskingId",
            entitlements.Bind(new TenancyPrincipal { User = "u", Grants = [grant] }));

        Assert.Equal(expected, await RowsAsync(engine, prepared));
    }

    private static CatalogContext Catalog(TenancyEntitlements? entitlements, bool withReports) => new()
    {
        ContextId = "direction",
        Schemas = [.. Sources(entitlements, withReports).Select(s => s.DescribeSchema())],
    };

    private static PocoSource[] Sources(TenancyEntitlements? entitlements, bool withReports)
    {
        TableEntitlementDescriptor? Of(string schema, string table) => entitlements?.For(schema, table);

        var sources = new List<PocoSource>
        {
            new PocoSourceBuilder("aus", "aus")
                .AddTable("assets", Assets, t =>
                {
                    t.UniqueKey(x => x.AssetId);
                    if (Of("aus", "assets") is { } d) t.Entitlement(d);
                })
                .Build(),
            new PocoSourceBuilder("coalition", "coalition")
                .AddTable("tasking", Taskings, t =>
                {
                    t.UniqueKey(x => x.TaskingId);
                    if (Of("coalition", "tasking") is { } d) t.Entitlement(d);
                })
                .Build(),
        };
        if (withReports)
        {
            sources.Add(new PocoSourceBuilder("partner", "partner")
                .AddTable("reports", new[] { new Report("R-1", "SECRET", "REL_FVEY") }, t =>
                {
                    t.UniqueKey(x => x.ReportId);
                    if (Of("partner", "reports") is { } d) t.Entitlement(d);
                })
                .Build());
        }

        return [.. sources];
    }

    internal static async Task<string[]> RowsAsync(ChalkEngine engine, PreparedQuery prepared, RequestContext? bind = null)
    {
        var rows = new List<string>();
        await using var execution = bind is null
            ? await engine.ExecuteAsync(prepared)
            : await engine.ExecuteAsync(prepared, bind);
        await foreach (var batch in execution.Batches)
        {
            using (batch)
            {
                rows.AddRange(BatchReader.ToRows(batch).Select(r => r[0]?.ToString() ?? "<null>"));
            }
        }

        return [.. rows];
    }
}

/// <summary>
/// A declaring policy end to end (D320): the Kestrel shape's released intelligence, five markings on
/// a row and two roles, one combination for both — prepared folded and as an execute-time shape.
/// </summary>
/// <remarks>
/// The same policy undeclared compiles 160 membership groups for the table, and prepared as a shape
/// every one of them is a bound relation joined in turn, which the client cannot parse past about
/// forty (F161). Declared, it is two.
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class DeclaredCombinationTests(SharedSidecar sidecar)
{
    private sealed record Report(
        string ReportId,
        string Classification,
        string MissionId,
        string CompartmentId,
        string Releasability,
        string Environment);

    private static readonly Report[] Reports =
    [
        new("R-1", "SECRET", "KESTREL", "ORCHID", "REL_FVEY", "SECURE"),
        new("R-2", "SECRET", "SENTINEL", "LANTERN", "REL_FVEY", "SECURE"),
        new("R-3", "SECRET", "KESTREL", "ORCHID", "REL_AUS_USA", "SECURE"),
        new("R-4", "SECRET", "KESTREL", "ORCHID", "REL_FVEY", "STANDARD"),
    ];

    public static TheoryData<string, bool, string[]> Answers() => new()
    {
        { "analyst", false, ["R-1", "R-3"] },
        { "analyst", true, ["R-1", "R-3"] },
        { "commander", false, ["R-2"] },
        { "commander", true, ["R-2"] },
    };

    [Theory]
    [MemberData(nameof(Answers))]
    public async Task A_declared_combination_answers_folded_and_as_a_shape(
        string who, bool asShape, string[] expected)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        var catalog = new CatalogContext { ContextId = "d320", Schemas = [Source(null).DescribeSchema()] };
        var policy = TenancyPolicy.Declare(catalog);
        var classification = policy.Tenancy("classification");
        var mission = policy.Tenancy("mission");
        var compartment = policy.Tenancy("compartment");
        var releasability = policy.Tenancy("releasability");
        var environment = policy.Tenancy("environment");
        var analyst = policy.Role("analyst");
        var commander = policy.Role("commander");
        var reports = policy.Source("partner").Table("reports");
        reports.Tenancy(t => t
            .Direct(classification, reports.Column("Classification"))
            .Direct(mission, reports.Column("MissionId"))
            .Direct(compartment, reports.Column("CompartmentId"))
            .Direct(releasability, reports.Column("Releasability"))
            .Direct(environment, reports.Column("Environment")));
        policy.Combination([analyst, commander], classification, mission, compartment, releasability, environment);
        var entitlements = policy.Compile(catalog);

        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "d320",
            Sources = [Source(entitlements)],
            Planner = sidecar.CreatePlanner(),
        });

        Grant Held(Role role, string missionId, string compartmentId, string releasabilityId) =>
            Grant.ForTenancy(classification, "SECRET", role)
                .Within(mission, missionId)
                .Within(compartment, compartmentId)
                .Within(releasability, releasabilityId)
                .Within(environment, "SECURE");

        var principal = new TenancyPrincipal
        {
            User = who,
            Grants = who == "analyst"
                ? [Held(analyst, "KESTREL", "ORCHID", "REL_FVEY"), Held(analyst, "KESTREL", "ORCHID", "REL_AUS_USA")]
                : [Held(commander, "SENTINEL", "LANTERN", "REL_FVEY")],
        };
        var context = entitlements.Bind(principal);

        const string Sql = "SELECT ReportId FROM partner.reports ORDER BY ReportId";
        var entitled = engine.WithEntitlements();
        var rows = asShape
            ? await ConfinementDirectionTests.RowsAsync(
                engine, await entitled.PrepareAsync(Sql, context.Shape()), context)
            : await ConfinementDirectionTests.RowsAsync(engine, await entitled.PrepareAsync(Sql, context));

        Assert.Equal(expected, rows);
    }

    private static PocoSource Source(TenancyEntitlements? entitlements) =>
        new PocoSourceBuilder("partner", "partner")
            .AddTable("reports", Reports, t =>
            {
                t.UniqueKey(x => x.ReportId);
                if (entitlements?.For("partner", "reports") is { } d) t.Entitlement(d);
            })
            .Build();
}

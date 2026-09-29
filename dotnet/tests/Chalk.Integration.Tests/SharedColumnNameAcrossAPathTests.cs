using Chalk.Catalog;
using Chalk.Client;
using Chalk.Entitlements;
using Chalk.Entitlements.Tenancy;
using Chalk.Sources.Poco;
using Chalk.TestKit;
using CatalogContext = Chalk.Catalog.CatalogContext;

namespace Chalk.Integration.Tests;

/// <summary>
/// A table that holds a kind directly and inherits another along a path, where the path's endpoint
/// has a column of the same name as the direct one (F157).
/// </summary>
/// <remarks>
/// <para>
/// The compiler writes the target's own column bare and an endpoint's as
/// <c>&lt;table&gt;.&lt;column&gt;</c>; the planner validated the target's texts with the endpoint
/// in the <c>FROM</c>, where a bare <c>Classification</c> named two columns, so every grant that
/// left a term naming it was refused at prepare as ambiguous. The children below carry their own
/// classification, which differs from their parent's for child 12, so a fix that read the parent's
/// column would show up here as a row too many.
/// </para>
/// <para>
/// Both layouts: the path stepping down a declared foreign key in one source, and down a declared
/// association across two.
/// </para>
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class SharedColumnNameAcrossAPathTests(SharedSidecar sidecar)
{
    private sealed record Parent(int Id, string Classification, string Releasability);

    private sealed record Child(int Id, int ParentId, string Classification);

    private static readonly Parent[] Parents =
    [
        new(1, "SECRET", "REL_FVEY"),
        new(2, "TOP SECRET", "REL_FVEY"),
    ];

    // Child 12 is TOP SECRET under a SECRET parent: the row that tells the two columns apart.
    private static readonly Child[] Children =
    [
        new(10, 1, "SECRET"),
        new(11, 2, "TOP SECRET"),
        new(12, 1, "TOP SECRET"),
    ];

    public static TheoryData<bool, string, string, string[]> Answers() =>
        new()
        {
            { false, "classification", "SECRET", ["10"] },
            { false, "classification", "TOP SECRET", ["11", "12"] },
            { false, "releasability", "REL_FVEY", ["10", "11", "12"] },
            { true, "classification", "SECRET", ["10"] },
            { true, "classification", "TOP SECRET", ["11", "12"] },
            { true, "releasability", "REL_FVEY", ["10", "11", "12"] },
        };

    [Theory]
    [MemberData(nameof(Answers))]
    public async Task A_grant_naming_the_shared_column_prepares_and_reads_the_targets_own(
        bool crossSource, string kind, string id, string[] expected)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        var (policy, entitlements, sources) = Declare(crossSource);
        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "f157",
            Sources = sources,
            Associations = policy.Associations,
            Planner = sidecar.CreatePlanner(),
        });

        var context = entitlements.Bind(new TenancyPrincipal
        {
            User = "u",
            Grants = [Grant.ForTenancy(policy.Tenancy(kind), id, policy.Role("reader"))],
        });
        var prepared = await engine.WithEntitlements().PrepareAsync(
            $"SELECT id FROM {(crossSource ? "c" : "one_source")}.children ORDER BY id", context);

        Assert.Equal(expected, await RowsAsync(engine, prepared));
    }

    private static (TenancyPolicy Policy, TenancyEntitlements Entitlements, PocoSource[] Sources) Declare(
        bool crossSource)
    {
        var catalog = new CatalogContext
        {
            ContextId = "f157",
            Schemas = [.. Sources(crossSource, null).Select(s => s.DescribeSchema())],
        };

        var policy = TenancyPolicy.Declare(catalog);
        var classification = policy.Tenancy("classification");
        var releasability = policy.Tenancy("releasability");
        policy.Role("reader");

        var parents = policy.Source(crossSource ? "p" : "one_source").Table("parents");
        var children = policy.Source(crossSource ? "c" : "one_source").Table("children");
        parents.Tenancy(t => t
            .Direct(classification, parents.Column("Classification"))
            .Direct(releasability, parents.Column("Releasability")));
        children.Tenancy(t => t
            .Direct(classification, children.Column("Classification"))
            .Inherited(releasability).Through(parents));
        if (crossSource)
        {
            children.Column("ParentId").References(parents.Column("Id"));
        }

        var entitlements = policy.Compile(catalog);
        return (policy, entitlements, Sources(crossSource, entitlements));
    }

    private static PocoSource[] Sources(bool crossSource, TenancyEntitlements? entitlements)
    {
        TableEntitlementDescriptor? Of(string schema, string table) => entitlements?.For(schema, table);

        if (!crossSource)
        {
            return
            [
                new PocoSourceBuilder("one_source", "one_source")
                    .AddTable("parents", Parents, t =>
                    {
                        t.UniqueKey(x => x.Id);
                        if (Of("one_source", "parents") is { } d) t.Entitlement(d);
                    })
                    .AddTable("children", Children, t =>
                    {
                        t.UniqueKey(x => x.Id)
                            .ForeignKey<Parent>(x => x.ParentId, to: "parents", x => x.Id, verify: true);
                        if (Of("one_source", "children") is { } d) t.Entitlement(d);
                    })
                    .Build(),
            ];
        }

        return
        [
            new PocoSourceBuilder("p", "p")
                .AddTable("parents", Parents, t =>
                {
                    t.UniqueKey(x => x.Id);
                    if (Of("p", "parents") is { } d) t.Entitlement(d);
                })
                .Build(),
            new PocoSourceBuilder("c", "c")
                .AddTable("children", Children, t =>
                {
                    t.UniqueKey(x => x.Id);
                    if (Of("c", "children") is { } d) t.Entitlement(d);
                })
                .Build(),
        ];
    }

    private static async Task<string[]> RowsAsync(ChalkEngine engine, PreparedQuery prepared)
    {
        var rows = new List<string>();
        await using var execution = await engine.ExecuteAsync(prepared);
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

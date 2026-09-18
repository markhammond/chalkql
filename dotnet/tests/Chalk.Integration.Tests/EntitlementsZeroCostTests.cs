using Chalk.Client;
using Chalk.Entitlements;
using Chalk.TestKit;
using Google.Protobuf;

namespace Chalk.Integration.Tests;

/// <summary>
/// §0's zero-cost property, asserted rather than assumed
/// (<c>docs/design/16-entitlements.md</c> §0 and §8's zero-cost class).
/// </summary>
/// <remarks>
/// The claim is not "entitlements are cheap" but "a catalog without one is unchanged", and the way
/// to show that is to plan the same statements over the same tables with and without the
/// descriptors: same plan bytes, same digest, no pass in the planner's stage list, no report, and
/// nothing required at execution. The corpus's own recorded plans and digests are the other half —
/// they are planned against a catalog with no entitlement at all and are unchanged by this step.
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class EntitlementsZeroCostTests(SharedSidecar sidecar)
{
    private const string Sql = "SELECT id, first_name FROM members ORDER BY id";

    /// <summary>The stage list is where "installed only when the catalog carries one" is visible.</summary>
    [Fact]
    public async Task The_pass_is_absent_from_the_stage_list_for_a_catalog_without_entitlements()
    {
        await using var plain = await EngineAsync(TenancyFixture.Unentitled);
        var unentitled = await plain.PrepareAsync(Sql, new PrepareOptions { IncludePlanText = true });

        Assert.DoesNotContain("entitlements", unentitled.PlanningStats.Stages);
        Assert.Equal(
            ["parse", "validate", "sql-to-rel", "hep", "volcano", "root-project"],
            unentitled.PlanningStats.Stages);

        await using var entitled = await EngineAsync(TenancyFixture.Shared);
        var policed = await entitled.PrepareAsync(
            Sql, TenancyFixture.U1, new PrepareOptions { IncludePlanText = true });
        Assert.Contains("entitlements", policed.PlanningStats.Stages);
    }

    /// <summary>
    /// A context bound over a catalog no descriptor refers to folds nothing and plans the same
    /// bytes: the plan message and its digest are identical to the same statement with no context.
    /// </summary>
    [Fact]
    public async Task A_context_over_an_unentitled_catalog_plans_byte_identically()
    {
        await using var engine = await EngineAsync(TenancyFixture.Unentitled);

        var bare = await engine.PrepareAsync(Sql);
        var bound = await engine.PrepareAsync(Sql, TenancyFixture.U1);

        Assert.Equal(bare.PlanDigest, bound.PlanDigest);
        Assert.Equal(bare.Plan.ToByteArray(), bound.Plan.ToByteArray());
        Assert.Empty(bare.RequiredContext);
        Assert.Empty(bound.RequiredContext);

        // And nothing came back in either response's extension slot: a core client attaches none,
        // and no handler had anything to say (step 26c, D212).
        Assert.Empty(bare.Extensions);
        Assert.Empty(bound.Extensions);
    }

    /// <summary>
    /// Every output column of an unentitled plan reads <c>Full</c>, and its batches carry the
    /// schema the compiler built — the same instance, with no metadata added.
    /// </summary>
    [Fact]
    public async Task An_unentitled_plan_reports_full_and_adds_no_field_metadata()
    {
        await using var engine = await EngineAsync(TenancyFixture.Unentitled);
        var prepared = await engine.WithEntitlements().PrepareAsync(Sql, TenancyFixture.U1);

        Assert.All(
            prepared.Columns, c => Assert.Equal(ReportedDisclosure.Full, c.Disclosure));
        Assert.All(
            prepared.OutputSchema.FieldsList,
            f => Assert.True(f.Metadata is null || !f.Metadata.ContainsKey("chalk.disclosure")));
    }

    /// <summary>A table with no entitlement adds no bytes to the catalog it travels in.</summary>
    [Fact]
    public void An_unentitled_catalog_carries_no_entitlement_bytes()
    {
        var plain = Chalk.Catalog.CatalogSerialization.ToProto(TenancyFixture.Unentitled.Catalog);
        var policed = Chalk.Catalog.CatalogSerialization.ToProto(TenancyFixture.Shared.Catalog);

        Assert.All(
            plain.Schemas[0].Tables, t => Assert.Null(t.Entitlement));
        Assert.True(plain.CalculateSize() < policed.CalculateSize());
    }

    /// <summary>
    /// The other side of the same claim (D231): a changed descriptor is a different plan, even where
    /// the change folds away for this principal and the plan is otherwise the same node for node.
    /// </summary>
    /// <remarks>
    /// The variant adds one rule for a role u1 does not hold, so its condition folds to FALSE and the
    /// sanitisers, the filter and the projections all come out identical. Before the read carried its
    /// descriptor hash the two plans were byte-identical, and §1's "a changed policy is a new plan by
    /// construction" rested on the folded literals happening to differ.
    /// </remarks>
    [Fact]
    public async Task A_changed_descriptor_is_a_different_plan_even_where_it_folds_away()
    {
        await using var engine = await EngineAsync(TenancyFixture.Shared);
        await using var other = await EngineAsync(TenancyFixture.WithAnUnreachableRule());

        var one = await engine.WithEntitlements().PrepareAsync(Sql, TenancyFixture.U1);
        var two = await other.WithEntitlements().PrepareAsync(Sql, TenancyFixture.U1);

        Assert.NotEqual(
            one.Entitlements.Tables[0].DescriptorHash, two.Entitlements.Tables[0].DescriptorHash);
        Assert.NotEqual(one.Query.PlanDigest, two.Query.PlanDigest);

        // And the only difference is the descriptor: strip it from both reads and the bytes agree.
        Assert.Equal(WithoutDescriptors(one.Query.Plan), WithoutDescriptors(two.Query.Plan));
    }

    private static byte[] WithoutDescriptors(Chalk.Ir.Plan plan)
    {
        var stripped = plan.Clone();
        stripped.PlanDigest = 0;
        Strip(stripped.Root);
        return stripped.ToByteArray();

        static void Strip(Chalk.Ir.Rel? rel)
        {
            if (rel is null)
            {
                return;
            }

            if (rel.KindCase == Chalk.Ir.Rel.KindOneofCase.Read)
            {
                rel.Read.DescriptorHash = "";
            }

            foreach (var input in Chalk.Ir.PlanWalker.Inputs(rel))
            {
                Strip(input);
            }
        }
    }

    private async Task<ChalkEngine> EngineAsync(TenancyFixture fixture) =>
        await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = TenancyFixture.ContextId,
            Sources = [fixture.Source],
            Planner = sidecar.CreatePlanner(),
        });
}

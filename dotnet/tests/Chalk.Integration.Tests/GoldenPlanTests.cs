using Chalk.Client;
using Chalk.Ir;
using Chalk.TestKit;
using PlanRequest = Chalk.Client.PlanRequest;

namespace Chalk.Integration.Tests;

/// <summary>
/// The plan-shape layer (docs/design/05-testing.md §1): every corpus query is planned live against
/// the sidecar, its digest compared with the recorded one, and its <c>-- expect:</c> lines checked.
/// A digest that moves in a pull request is the review signal that a rule or cost change altered
/// planning behaviour; the expectations say what was supposed to be true regardless.
/// </summary>
[Collection(SidecarCollection.Name)]
public sealed class GoldenPlanTests(SharedSidecar sidecar)
{
    private static readonly CorpusFixture Fixture = CorpusFixture.Shared;

    public static TheoryData<string> Queries()
    {
        var data = new TheoryData<string>();
        foreach (var query in CorpusQueries.LoadAll())
        {
            data.Add(query.Name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Queries))]
    public async Task The_live_plan_matches_the_recorded_digest_at_full(string name)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        var query = Query(name);
        var plan = await PlanAsync(query, PushdownLevel.Full);

        Assert.Equal(
            PlanDigest.Format(RecordedDigest(query, "full")),
            PlanDigest.Format(plan.PlanDigest));
    }

    [Theory]
    [MemberData(nameof(Queries))]
    public async Task The_live_plan_matches_the_recorded_digest_at_none(string name)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        var query = Query(name);
        var plan = await PlanAsync(query, PushdownLevel.None);

        Assert.Equal(
            PlanDigest.Format(RecordedDigest(query, "none")),
            PlanDigest.Format(plan.PlanDigest));
    }

    [Theory]
    [MemberData(nameof(Queries))]
    public async Task The_expect_block_holds(string name)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        var query = Query(name);
        var (plan, planText) = await PlanWithTextAsync(query, PushdownLevel.Full);

        PlanExpectations.Check(query, plan, planText);
    }

    [Theory]
    [MemberData(nameof(Queries))]
    public async Task Every_plan_passes_the_validator_at_both_levels(string name)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        var query = Query(name);
        foreach (var level in new[] { PushdownLevel.Full, PushdownLevel.None })
        {
            var plan = await PlanAsync(query, level);

            // The digest check inside the validator is the cross-language contract check: the plan
            // was digested by the Java implementation and is recomputed here by the C# one.
            PlanValidator.Validate(plan);
            Assert.Equal(Fixture.Catalog.ContextId, plan.ContextId);
            Assert.Equal(Fixture.Catalog.Epoch, plan.CatalogEpoch);
        }
    }

    /// <summary>
    /// The reference configuration must actually be a different configuration for the queries whose
    /// scans can be pruned or whose predicates can become index lookups, or the I4 differential test
    /// is comparing a plan with itself. From M2 it also has to hold that NONE never emits a lookup.
    /// </summary>
    [Fact]
    public async Task Pushdown_none_never_prunes_a_scan()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        var differed = 0;
        foreach (var query in CorpusQueries.LoadAll())
        {
            var none = await PlanAsync(query, PushdownLevel.None);
            foreach (var read in PlanWalker.Rels(none).Where(r => r.KindCase == Rel.KindOneofCase.Read))
            {
                var table = Fixture.Catalog.FindTable(read.Read.Table.Schema, read.Read.Table.Table);
                Assert.NotNull(table);
                Assert.Equal(table.Value.Table.Columns.Count, read.Read.Projection.Count);
            }

            Assert.False(
                PlanWalker.Has(none, Rel.KindOneofCase.IndexLookup),
                $"{query.Name}: PUSHDOWN_LEVEL_NONE emitted an IndexLookup");

            var full = await PlanAsync(query, PushdownLevel.Full);
            if (full.PlanDigest != none.PlanDigest)
            {
                differed++;
            }
        }

        Assert.True(differed > 0, "no corpus query planned differently at FULL and NONE");
    }

    private static CorpusQuery Query(string name) =>
        CorpusQueries.LoadAll().Single(q => q.Name == name);

    private async Task<Plan> PlanAsync(CorpusQuery query, PushdownLevel level) =>
        (await PlanWithTextAsync(query, level)).Plan;

    private async Task<(Plan Plan, string? PlanText)> PlanWithTextAsync(
        CorpusQuery query, PushdownLevel level)
    {
        await using var planner = sidecar.CreatePlanner();
        await planner.RegisterCatalogAsync(Fixture.Catalog);
        var result = await planner.PlanAsync(new PlanRequest
        {
            Sql = query.PlannerSql,
            ContextId = Fixture.Catalog.ContextId,
            CatalogEpoch = Fixture.Catalog.Epoch,
            Options = new Chalk.Client.PlannerOptions
            {
                Pushdown = level,
                Conformance = query.Conformance,
                Libraries = query.Libraries,

                // The plan text is what carries an index's *kind* (D257): the IR carries only its
                // name. Asking for it changes nothing about the plan, only what comes back with it.
                IncludePlanText = true,
            },
        });
        return (result.Plan, result.PlanText);
    }

    private static ulong RecordedDigest(CorpusQuery query, string level)
    {
        var path = Path.Combine(RepoLayout.PlansFor(query.Milestone).FullName, $"{query.Name}.{level}.digest");
        Assert.True(
            File.Exists(path),
            $"{path} is missing; record the corpus with scripts/record-plans.sh");
        return PlanDigest.Parse(File.ReadAllText(path));
    }
}

using Chalk.Client;
using Chalk.Entitlements;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// Incremental narrowing (<c>docs/design/16-entitlements.md</c> §2.1, D233): a prepared plan told
/// more than it knew, and the one thing that has to be true of the result.
/// </summary>
/// <remarks>
/// <para>
/// The claim is an identity, and it is asserted rather than argued: <c>NarrowAsync(more)</c> gives
/// the plan <c>PrepareAsync(sql, union)</c> gives, plan for plan and digest for digest. The corpus
/// runs it twice over for every statement and every principal — a base with nothing folded, narrowed
/// to the tenant's grants, narrowed again to the principal's — and compares each step against a
/// prepare with the union of everything bound so far.
/// </para>
/// <para>
/// Both halves of the sidecar's efficiency are covered by that one assertion, because a narrowing
/// whose hint is declined plans from SQL: whichever path ran, the plan is the same or the test
/// fails. <see cref="A_hint_the_sidecar_cannot_take_is_declined_and_the_plan_is_the_same"/> forces
/// the miss, and the Java side asserts the hit.
/// </para>
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class TenancyNarrowingTests(SharedSidecar sidecar)
{
    public static TheoryData<string> Queries()
    {
        var data = new TheoryData<string>();
        foreach (var query in CorpusQueries.LoadM7Tenancy())
        {
            // A statement a binding time that leaves a name open refuses for a registered reason
            // (<see cref="TenancyCorpusTests.RefusedUnderOpenBinding"/>) has no answer here to
            // compare with the folded one. It stays in the corpus exactly as written and keeps its
            // prepare-time run, under the oracle and the detector, like any other.
            if (!TenancyCorpusTests.RefusedUnderOpenBinding.ContainsKey(query.Name))
            {
                data.Add(query.Name);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Queries))]
    public async Task Every_two_step_narrowing_lands_on_the_plan_the_union_gives(string name)
    {
        var query = CorpusQueries.LoadM7Tenancy().Single(q => q.Name == name);
        var refused = query.Expectations
            .Where(e => e.StartsWith("policy(", StringComparison.Ordinal))
            .Select(e => e["policy(".Length..^1])
            .ToHashSet(StringComparer.Ordinal);
        await using var engine = await EngineAsync();
        var entitled = engine.WithEntitlements();

        var narrowings = 0;
        foreach (var (principal, full) in TenancyFixture.Principals)
        {
            if (refused.Contains(principal))
            {
                continue;
            }

            var (tenancy, subject) = Halves(full);
            EntitledQuery step;
            try
            {
                // A base with nothing folded at all, where the statement takes one.
                step = await entitled.PrepareAsync(query.Sql, full.Shape());
                step = await NarrowedAsync(entitled, query.Sql, step, tenancy, $"{name}/{principal} 1");
                narrowings++;
            }
            catch (EntitlementException)
            {
                // A statement a shared plan cannot take starts from the tenant's own instead, which
                // is the base D232 was written for; the corpus records which those are and why (V100).
                step = await entitled.PrepareAsync(query.Sql, TenancyFixture.PartiallyBound(full));
            }

            // Whichever base it started from, one step of it is the partial binding the corpus
            // records a golden for.
            Assert.Equal(
                TenancyFixture.PartiallyBound(full).CanonicalHash,
                step.Query.Context!.CanonicalHash);

            step = await NarrowedAsync(entitled, query.Sql, step, subject, $"{name}/{principal} 2");
            narrowings++;

            // And the last step lands on the principal's own folded plan — the one the m7-tenancy
            // goldens were recorded from — because the union of the steps is the whole binding.
            Assert.Equal(full.CanonicalHash, step.Query.Context!.CanonicalHash);
            Assert.Equal(
                (await entitled.PrepareAsync(query.Sql, full)).PlanDigest, step.PlanDigest);
        }

        Assert.True(narrowings > 0, $"{name} narrowed nothing, so this case asserted nothing");
    }

    /// <summary>
    /// One narrowing, held to the plan preparing with the union gives — the identity D233 states.
    /// </summary>
    private static async Task<EntitledQuery> NarrowedAsync(
        EntitledEngine entitled, string sql, EntitledQuery from, RequestContext more, string where)
    {
        var union = from.Query.Context!.Narrow(more);
        var narrowed = await from.NarrowAsync(more);
        await AssertSameAsPreparingWithAsync(entitled, sql, union, narrowed, where);
        return narrowed;
    }

    /// <summary>A value the base folded is in its leaves and in its digest, and is refused by name.</summary>
    [Fact]
    public async Task A_value_the_base_folded_cannot_be_given_another_one()
    {
        await using var engine = await EngineAsync();
        var entitled = engine.WithEntitlements();
        var full = TenancyFixture.U1;
        var (tenancy, _) = Halves(full);

        var tenantPlan = await entitled.PrepareAsync(
            "SELECT id FROM members ORDER BY id", full.Shape());
        var narrowed = await tenantPlan.NarrowAsync(tenancy);

        var refusal = await Assert.ThrowsAsync<ArgumentException>(
            async () => await narrowed.NarrowAsync(tenancy));
        Assert.Contains("manager_orgs", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("cannot be given another value", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The hint is a hint. A digest no sidecar ever produced is declined, the statement is planned
    /// from SQL, and the plan is the one the hit would have given.
    /// </summary>
    [Fact]
    public async Task A_hint_the_sidecar_cannot_take_is_declined_and_the_plan_is_the_same()
    {
        await using var engine = await EngineAsync();
        var entitled = engine.WithEntitlements();
        var full = TenancyFixture.U1;
        var (tenancy, _) = Halves(full);
        const string sql = "SELECT id, first_name FROM members ORDER BY id";

        var baseline = await entitled.PrepareAsync(sql, full.Shape());
        var narrowed = await baseline.NarrowAsync(tenancy);
        Assert.Equal(baseline.PlanDigest, narrowed.Query.NarrowedFrom);

        // The same union, prepared with no hint at all: the miss path, and the same plan.
        var fromScratch = await entitled.PrepareAsync(sql, baseline.Query.Context!.Narrow(tenancy));
        Assert.Null(fromScratch.Query.NarrowedFrom);
        Assert.Equal(fromScratch.PlanDigest, narrowed.PlanDigest);
        Assert.Equal(
            Google.Protobuf.MessageExtensions.ToByteArray(fromScratch.Plan),
            Google.Protobuf.MessageExtensions.ToByteArray(narrowed.Plan));
    }

    // ---------------------------------------------------------------- the parts

    /// <summary>
    /// The two halves of a principal's bindings: the tenant's grants, which every principal of that
    /// tenant shares, and what tells one of them from another.
    /// </summary>
    private static (RequestContext Tenancy, RequestContext Subject) Halves(RequestContext full)
    {
        var subjectNames = TenancyFixture.SubjectNames.ToHashSet(StringComparer.Ordinal);
        return (Part(full, n => !subjectNames.Contains(n)), Part(full, subjectNames.Contains));
    }

    private static RequestContext Part(RequestContext full, Func<string, bool> take) =>
        new()
        {
            Scalars = full.Scalars
                .Where(s => take(s.Key))
                .ToDictionary(s => s.Key, s => s.Value, StringComparer.Ordinal),
            Lists = full.Lists
                .Where(l => take(l.Key))
                .ToDictionary(l => l.Key, l => l.Value, StringComparer.Ordinal),
            Relations = full.Relations
                .Where(r => take(r.Key))
                .ToDictionary(r => r.Key, r => r.Value, StringComparer.Ordinal),
        };

    private static async Task AssertSameAsPreparingWithAsync(
        EntitledEngine entitled, string sql, RequestContext union, EntitledQuery narrowed, string where)
    {
        var expected = await entitled.PrepareAsync(sql, union);
        Assert.True(
            expected.PlanDigest == narrowed.PlanDigest,
            $"{where}: narrowing gave digest {narrowed.PlanDigest:x16} and preparing with the union "
            + $"gave {expected.PlanDigest:x16}");
        Assert.Equal(
            Google.Protobuf.MessageExtensions.ToByteArray(expected.Plan),
            Google.Protobuf.MessageExtensions.ToByteArray(narrowed.Plan));
        Assert.Equal(Report(expected), Report(narrowed));
    }

    private static string Report(EntitledQuery prepared) =>
        string.Join(
            ", ",
            prepared.Columns.Select(c => $"{c.Name}:{c.Disclosure}")
                .Concat(prepared.Entitlements.Tables.Select(t => $"{t.Table}:{t.Visibility}")));

    private async Task<ChalkEngine> EngineAsync() =>
        await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = TenancyFixture.ContextId,
            Sources = [TenancyFixture.Shared.Source],
            Planner = sidecar.CreatePlanner(),
            Functions = TenancyAdoFixture.RegisterFunctions,
        });
}

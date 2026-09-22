using Chalk.Client;
using Chalk.Entitlements;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// The entitlement corpus under <b>execute-time binding</b> (<c>docs/design/16-entitlements.md</c>
/// §2, D209): one plan for every principal, and the values bound when it runs.
/// </summary>
/// <remarks>
/// <para>
/// The assertion is agreement, query for query and principal for principal: the rows a principal
/// gets from the shared plan are the rows the same principal gets from their own folded plan, which
/// the sibling suite has already checked against the oracle and against a recorded golden. Saying it
/// this way rather than with goldens of its own is deliberate — what is under test is that the two
/// binding times are the same policy, and a second set of goldens would only be able to disagree with
/// the first.
/// </para>
/// <para>
/// A statement the shared plan refuses is a refusal for <em>every</em> principal, because the plan
/// cannot know whose values will arrive: <c>SELECT amount FROM orders</c> is population-only for a
/// principal who might be an auditor, and under a shape everybody might be. That is the price of the
/// shared plan and it is the conservative direction; the queries it happens to are named here.
/// </para>
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class TenancyExecuteTimeTests(SharedSidecar sidecar)
{
    /// <summary>
    /// The statements the shared plan refuses outright, whoever is asking: a star over
    /// <c>orders</c>, which projects the raw <c>amount</c>, and the value uses of the same column.
    /// Each reads a population-only column that <em>some</em> principal's rules restrict, and under a
    /// shape the fold cannot rule that principal out (§3.4) — so the refusal is every principal's.
    /// A folded plan refuses them only for the principal whose rules say so.
    /// </summary>
    /// <summary>
    /// The statement these facts prepare under a shape.
    /// </summary>
    /// <remarks>
    /// Named columns rather than a star, since D261: under a shape nothing folds, so the counting
    /// role's AGGREGATE_ONLY rule on <c>national_id</c> is reachable for every principal and a star
    /// over the table is refused for all of them (§3.4) — which is the answer
    /// <see cref="RefusedUnderAShape"/> records for the corpus. What these facts are about is what a
    /// shared plan binds and refuses, and that is the same statement either way.
    /// </remarks>
    private const string Shaped = "SELECT id, org_id, first_name, last_name FROM members";

    internal static readonly HashSet<string> RefusedUnderAShape =
        new(StringComparer.Ordinal)
        {
            "03_orders_star",
            "12_orders_value_use_of_amount",

            // D261. Under a shape nothing folds, so the counting role's AGGREGATE_ONLY rule on
            // `national_id` is reachable for every principal and the column is population-only for
            // all of them — which is §3.4's own answer, the same one `03_orders_star` is here for:
            // the leaf cannot know that this principal is a support desk rather than a counter, and
            // a value use of a raw population-only column is refused for everybody. A folded plan is
            // what discloses it, and this suite is what says the two agree where both answer.
            "01_members_star",
            "04_members_like_prefix",
            "05_members_national_id_is_null",
            "06_members_national_id",
            "14_members_global_grant",
            "20_members_outside_the_scope",
            "29_members_test_in_the_select_list",
            "30_members_test_in_the_where",
            "31_members_test_not_equals",
            "32_members_test_in_list",

            // F86 was here — `02_orders_by_org_through_the_path`, `07_orders_avg_amount` and
            // `13_orders_avg_by_org`, the corpus's guarded aggregates over `orders.amount`, whose
            // only use of the column is the allow-listed population aggregate the rules name.
            // Fixed by ADR 0062 §1: clause 2 and the client's own walk read the leaf's sanitiser by
            // shape as well as by position, so the verdict projections a declared path puts between
            // the sanitiser and the leaf no longer hide it. All three run under a shape as every
            // principal, and the list is §3.4's own answer again and holds no defect.
        };

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
    public async Task Every_principal_sees_what_prepare_time_binding_showed_them(string name)
    {
        var query = CorpusQueries.LoadM7Tenancy().Single(q => q.Name == name);
        var guarded = query.Expectations.Contains("guarded")
            || query.Expectations.Contains("tested");
        await using var engine = await EngineAsync();

        foreach (var (principal, context) in TenancyFixture.Principals)
        {
            var expected = await FoldedAsync(engine, query, context);
            var (actual, refusal) = await SharedAsync(engine, query, context);
            if (actual is null)
            {
                Assert.True(
                    RefusedUnderAShape.Contains(name),
                    $"{name} as {principal} is refused under a shape and is not on the list of "
                    + "statements a shared plan cannot take; add it there with its reason, or find "
                    + $"out why it started being refused. The refusal: {refusal}");
                continue;
            }

            if (guarded)
            {
                AssertGuardAside(expected!, actual, $"{name} as {principal}");
                continue;
            }

            Assert.Equal(Sorted(expected!), Sorted(actual));
        }
    }

    /// <summary>
    /// The same rows, except where the group-size guard the shared plan cannot avoid withheld a
    /// value the fold would have disclosed (§3.6).
    /// </summary>
    /// <remarks>
    /// Taint is a property of the column at a leaf and not of a row (§3.4), and under a shape the
    /// leaf cannot know that this principal is a manager rather than an auditor — so the guard is in
    /// the plan for everybody and a group below the floor comes back NULL. It is the design's own
    /// rule applied to a leaf nothing folded, and it is the conservative direction: the shared plan
    /// may withhold where a folded one disclosed, and never the other way round.
    /// </remarks>
    internal static void AssertGuardAside(string[] folded, string[] shared, string what)
    {
        Assert.Equal(folded.Length, shared.Length);
        for (var i = 0; i < folded.Length; i++)
        {
            var left = folded[i].Split('|');
            var right = shared[i].Split('|');
            Assert.Equal(left.Length, right.Length);
            for (var c = 0; c < left.Length; c++)
            {
                Assert.True(
                    left[c] == right[c] || right[c] == "<null>",
                    $"{what} row {i} column {c}: the folded plan said '{left[c]}' and the shared "
                    + $"plan '{right[c]}', which is neither the same value nor the guard's NULL.");
            }
        }
    }

    private static string[] Sorted(string[] rows)
    {
        var sorted = (string[])rows.Clone();
        System.Array.Sort(sorted, StringComparer.Ordinal);
        return sorted;
    }

    [Fact]
    public async Task Two_principals_of_the_same_shape_share_one_plan_and_one_digest()
    {
        await using var engine = await EngineAsync();
        var entitlements = engine.WithEntitlements();

        var first = await entitlements.PrepareAsync(
            Shaped, TenancyFixture.U1.Shape());
        var second = await entitlements.PrepareAsync(
            Shaped, TenancyFixture.U6.Shape());

        Assert.Equal(first.PlanDigest, second.PlanDigest);
        Assert.Equal(
            Chalk.Ir.PlanExtensions.ToPlanText(first.Plan), Chalk.Ir.PlanExtensions.ToPlanText(second.Plan));

        // And it is not the folded plan: a manager's carries their own organisations as literals.
        var folded = await entitlements.PrepareAsync(Shaped, TenancyFixture.U1);
        Assert.NotEqual(first.PlanDigest, folded.PlanDigest);
    }

    [Fact]
    public async Task The_plan_says_what_execution_must_bind()
    {
        await using var engine = await EngineAsync();
        var prepared = await engine
            .WithEntitlements()
            .PrepareAsync(Shaped, TenancyFixture.U1.Shape());

        Assert.Contains("mask_key", prepared.Entitlements.RequiredScalars);
        Assert.Contains("manager_orgs", prepared.Entitlements.RequiredRelations);
        string[] reported =
        [
            .. prepared.Entitlements.RequiredScalars,
            .. prepared.Entitlements.RequiredRelations,
        ];
        Assert.Equal(
            reported.OrderBy(n => n, StringComparer.Ordinal),
            prepared.Query.RequiredContext.OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_query_prepared_with_a_shape_refuses_to_run_without_the_values()
    {
        await using var engine = await EngineAsync();
        var prepared = await engine
            .WithEntitlements()
            .PrepareAsync(Shaped, TenancyFixture.U1.Shape());

        var missing = await Assert.ThrowsAsync<ContextRequiredException>(
            async () => await engine.ExecuteAsync(prepared.Query, (IReadOnlyList<object?>?)null));
        Assert.Contains("manager_orgs", missing.Names);
    }

    [Fact]
    public async Task A_context_of_another_shape_is_refused_before_anything_runs()
    {
        await using var engine = await EngineAsync();
        var prepared = await engine
            .WithEntitlements()
            .PrepareAsync(Shaped, TenancyFixture.U1.Shape());

        var other = new RequestContext
        {
            Scalars = new Dictionary<string, object?>(StringComparer.Ordinal) { ["user"] = 1 },
        };
        var refused = await Assert.ThrowsAsync<ArgumentException>(
            async () => await engine.ExecuteAsync(prepared.Query, other));
        Assert.Contains("different context shape", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_query_prepared_with_its_values_takes_no_second_context()
    {
        await using var engine = await EngineAsync();
        var prepared = await engine
            .WithEntitlements()
            .PrepareAsync(Shaped, TenancyFixture.U1);

        var refused = await Assert.ThrowsAsync<ArgumentException>(
            async () => await engine.ExecuteAsync(prepared.Query, TenancyFixture.U1));
        Assert.Contains("nothing to bind at execution", refused.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- the two binding times

    private async ValueTask<ChalkEngine> EngineAsync() =>
        await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = TenancyFixture.ContextId,
            Sources = [TenancyFixture.Shared.Source],
            Planner = sidecar.CreatePlanner(),
            Functions = TenancyAdoFixture.RegisterFunctions,
        });

    /// <summary>This principal's own plan, with their values folded into it — the primary mode.</summary>
    private static async Task<string[]?> FoldedAsync(
        ChalkEngine engine, CorpusQuery query, RequestContext context)
    {
        try
        {
            var prepared = await engine.WithEntitlements().PrepareAsync(query.Sql, context);
            return await RowsAsync(
                engine, prepared.Query, executeWith: null, CorpusQueries.Parameters(query.Name));
        }
        catch (EntitlementException)
        {
            return null;
        }
    }

    /// <summary>The shape's plan, with this principal's values bound at execution.</summary>
    private static async Task<(string[]? Rows, string Refusal)> SharedAsync(
        ChalkEngine engine, CorpusQuery query, RequestContext context)
    {
        try
        {
            var prepared =
                await engine.WithEntitlements().PrepareAsync(query.Sql, context.Shape());
            return (
                await RowsAsync(
                    engine, prepared.Query, executeWith: context, CorpusQueries.Parameters(query.Name)),
                "");
        }
        catch (EntitlementException refused)
        {
            return (null, refused.Message);
        }
    }

    private static async Task<string[]> RowsAsync(
        ChalkEngine engine,
        PreparedQuery prepared,
        RequestContext? executeWith,
        IReadOnlyList<object?>? parameters = null)
    {
        var rows = new List<string>();
        await using var execution = executeWith is null
            ? await engine.ExecuteAsync(prepared, parameters)
            : await engine.ExecuteAsync(prepared, executeWith, parameters);
        await foreach (var batch in execution.Batches)
        {
            using (batch)
            {
                rows.AddRange(BatchReader.ToRows(batch).Select(
                    r => string.Join("|", r.Select(v => v?.ToString() ?? "<null>"))));
            }
        }

        return [.. rows];
    }
}

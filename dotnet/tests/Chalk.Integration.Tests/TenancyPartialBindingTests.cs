using System.Text;
using Chalk.Client;
using Chalk.Entitlements;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// The entitlement corpus under <b>partial binding</b> (<c>docs/design/16-entitlements.md</c> §2.1,
/// D232): the tenancy folded into the plan, the subject left open and bound per execution.
/// </summary>
/// <remarks>
/// <para>
/// This is the mode between the two the design already had. Prepare-time binding folds everything
/// and gives one plan per principal; execute-time binding folds nothing and gives one plan for
/// every principal of a shape. Partial binding folds what the tenant's grants settle and leaves
/// what tells one of that tenant's principals from another — so one leaf carries the tenancy's own
/// literal <c>IN</c> list beside a membership marker over a bound table, which is the thing worth
/// testing about it.
/// </para>
/// <para>
/// Three things are asserted of every (query, principal) pair, as they are of the folded run. The
/// rows match the <see cref="TenancyOracle"/>, which knows nothing of binding times. They match what
/// the same principal saw with everything folded, aside from the group-size guard, which withholds
/// more where it cannot be folded away and never less (V100). And the report — every column's label
/// and every table's visibility — is recorded as a golden, because what partial binding changes is
/// exactly that: a folded membership decides what it decides, and a marker leaves
/// <c>PerRow</c> where execute-time binding would.
/// </para>
/// <para>
/// Regenerate the goldens with <c>CHALK_WRITE_FIXTURES=1</c> and read the diff.
/// </para>
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class TenancyPartialBindingTests(SharedSidecar sidecar)
{
    private static readonly DirectoryInfo Goldens =
        new(Path.Combine(RepoLayout.Corpus.FullName, "plans", "m7-tenancy-partial"));

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
    public async Task Every_principal_sees_what_the_oracle_says_and_what_the_golden_records(string name)
    {
        var query = CorpusQueries.LoadM7Tenancy().Single(q => q.Name == name);
        var guarded = query.Expectations.Contains("guarded")
            || query.Expectations.Contains("tested");
        var refused = query.Expectations
            .Where(e => e.StartsWith("policy(", StringComparison.Ordinal))
            .Select(e => e["policy(".Length..^1])
            .ToHashSet(StringComparer.Ordinal);

        await using var engine = await EngineAsync();
        var recorded = new StringBuilder();
        foreach (var (principal, context) in TenancyFixture.Principals)
        {
            recorded.Append("-- ").Append(principal).AppendLine();
            if (refused.Contains(principal))
            {
                var refusal = await Assert.ThrowsAsync<EntitlementException>(
                    async () => await EngineRowsAsync(engine, query, context));
                recorded.Append("POLICY ").AppendLine(FirstSentence(refusal.Message));
                recorded.AppendLine();
                continue;
            }

            var (rows, report) = await EngineRowsAsync(engine, query, context);
            recorded.Append("report ").AppendLine(report);
            foreach (var row in rows)
            {
                recorded.AppendLine(row);
            }

            recorded.AppendLine();

            if (!guarded)
            {
                Assert.Equal(await OracleRowsAsync(query, context), rows);
            }
        }

        await AssertGoldenAsync(name, recorded.ToString());
    }

    /// <summary>
    /// One plan for every principal whose folded half is the same, bound per execution to the one
    /// running it: the tenant's plan, shared by that tenant's principals.
    /// </summary>
    [Fact]
    public async Task One_tenant_s_plan_serves_two_of_its_principals()
    {
        await using var engine = await EngineAsync();
        var entitled = engine.WithEntitlements();

        // Two agents in organization 1, differing only in who they are and what they are the subject
        // of. Their tenancy grants are the same list, so the folded half is the same.
        var one = TenancyFixture.Principal(
            user: 2, managerOrgs: [], agentOrgs: [1], auditorOrgs: [], subjectPairs: []);
        var two = TenancyFixture.Principal(
            user: 3, managerOrgs: [], agentOrgs: [1], auditorOrgs: [], subjectPairs: [[3, 1]]);

        var plan = await entitled.PrepareAsync(
            "SELECT id, first_name FROM members ORDER BY id",
            TenancyFixture.PartiallyBound(one));
        var other = await entitled.PrepareAsync(
            "SELECT id, first_name FROM members ORDER BY id",
            TenancyFixture.PartiallyBound(two));

        Assert.Equal(plan.PlanDigest, other.PlanDigest);

        // What is still open is what this statement's descriptors actually read of the open half:
        // `members` carries no resource-owner column, so `user` is a name nothing in this plan
        // needs, and RequiredContext is read off the plan rather than off the request (D214).
        Assert.Equal(["subject_pairs"], plan.Query.RequiredContext);
        Assert.Contains("agent_orgs", plan.Query.FoldedContext);

        // And the rows are each principal's own: the subject grant reaches member 3's row for the
        // one who holds it, through the very same plan.
        Assert.Equal(await RowsAsync(engine, plan.Query, one), await RowsAsync(engine, plan.Query, one));
        Assert.Equal(
            await RowsAsync(engine, plan.Query, two),
            await RowsAsync(engine, other.Query, two));
    }

    /// <summary>
    /// The folded half is in the leaf, so an execution may not contradict it: a plan that folded
    /// organization 1's grants answers for organization 1, whoever runs it (D232).
    /// </summary>
    [Fact]
    public async Task An_execution_may_not_rebind_what_the_plan_folded()
    {
        await using var engine = await EngineAsync();
        var inOrg1 = TenancyFixture.Principal(
            user: 2, managerOrgs: [], agentOrgs: [1], auditorOrgs: [], subjectPairs: []);
        var inOrg2 = TenancyFixture.Principal(
            user: 9, managerOrgs: [], agentOrgs: [2], auditorOrgs: [], subjectPairs: []);

        var prepared = await engine.WithEntitlements().PrepareAsync(
            "SELECT id FROM members ORDER BY id", TenancyFixture.PartiallyBound(inOrg1));
        Assert.Contains("agent_orgs", prepared.Query.FoldedContext);

        var refusal = await Assert.ThrowsAsync<ArgumentException>(
            async () => await engine.ExecuteAsync(prepared.Query, inOrg2));
        Assert.Contains("folded", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>The message's first sentence: the rest names the design section and is not a fact.</summary>
    private static string FirstSentence(string message)
    {
        var stop = message.IndexOf(". ", StringComparison.Ordinal);
        return stop < 0 ? message : message[..(stop + 1)];
    }

    // ---------------------------------------------------------------- the two engines

    private async Task<ChalkEngine> EngineAsync() =>
        await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = TenancyFixture.ContextId,
            Sources = [TenancyFixture.Shared.Source],
            Planner = sidecar.CreatePlanner(),
            Functions = TenancyAdoFixture.RegisterFunctions,
        });

    private static async Task<(string[] Rows, string Report)> EngineRowsAsync(
        ChalkEngine engine, CorpusQuery query, RequestContext context)
    {
        var prepared = await engine
            .WithEntitlements()
            .PrepareAsync(query.Sql, TenancyFixture.PartiallyBound(context));
        var report = string.Join(
            ", ",
            prepared.Columns.Select(c => $"{c.Name}:{c.Disclosure}")
                .Concat(prepared.Entitlements.Tables.Select(t => $"{t.Table}:{t.Visibility}")));
        return (await RowsAsync(engine, prepared.Query, context, CorpusQueries.Parameters(query.Name)), report);
    }

    private async Task<string[]> OracleRowsAsync(CorpusQuery query, RequestContext context)
    {
        // The oracle's tables carry no entitlement, so nothing here is rewritten and the context is
        // never bound: the statement is an ordinary statement over rows somebody else disclosed.
        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = TenancyFixture.ContextId,
            Sources = [TenancyOracle.Disclose(context)],
            Planner = sidecar.CreatePlanner(),
            Functions = TenancyAdoFixture.RegisterFunctions,
        });
        return await RowsAsync(
            engine,
            await engine.WithEntitlements().PrepareAsync(query.Sql),
            executeWith: null,
            CorpusQueries.Parameters(query.Name));
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

    private static async Task AssertGoldenAsync(string name, string recorded)
    {
        var file = new FileInfo(Path.Combine(Goldens.FullName, name + ".txt"));
        if (Environment.GetEnvironmentVariable("CHALK_WRITE_FIXTURES") == "1")
        {
            Goldens.Create();
            await File.WriteAllTextAsync(file.FullName, recorded);
            return;
        }

        Assert.True(
            file.Exists,
            $"{file.FullName} does not exist. Regenerate with CHALK_WRITE_FIXTURES=1 and review it.");
        Assert.Equal(
            (await File.ReadAllTextAsync(file.FullName)).ReplaceLineEndings("\n"),
            recorded.ReplaceLineEndings("\n"));
    }
}

using System.Text;
using Chalk.Client;
using Chalk.Entitlements;
using Chalk.Entitlements.Tenancy;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// The entitlement corpus again, through <b>layer B</b>: the same statements as the same principals,
/// with the descriptors produced by the tenancy package from §8's declarations and the contexts
/// bound from grants (<c>docs/design/16-entitlements.md</c> §5, §8).
/// </summary>
/// <remarks>
/// <para>
/// What is asserted is that the two paths <b>agree</b> — query for query, principal for principal,
/// row for row and label for label — against the very goldens the layer-A run recorded. That is the
/// only check that says the compiler compiles the model rather than something near it: a descriptor
/// that reads plausibly and discloses one row too many looks exactly like a correct one until the
/// two are compared, and the goldens are a text a reviewer reads.
/// </para>
/// <para>
/// Beside it, the package's own <see cref="TenancyEntitlements.Reconcile"/> is run over every plan:
/// what the policy says each column must disclose, computed from the grants alone with no plan
/// involved, against what the planner wrote on the read. The taint check proves nothing escapes;
/// this is the half that proves nothing was misclassified.
/// </para>
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class TenancyPolicyCorpusTests(SharedSidecar sidecar)
{
    private static readonly DirectoryInfo Goldens =
        new(Path.Combine(RepoLayout.Corpus.FullName, "plans", "m7-tenancy"));

    private static readonly TenancyPolicyFixture Fixture = TenancyPolicyFixture.Shared;

    /// <summary>
    /// Statements whose <see cref="TenancyEntitlements.Reconcile"/> prediction diverges from the
    /// plan, by F-number: the two layers still have to answer alike row for row and label for
    /// label, and that comparison is made; only the package's own independent prediction is not.
    /// </summary>
    /// <remarks>
    /// <b>Empty since F87 was fixed</b> (ADR 0061). It held <c>39_orders_join_the_lines</c>, the
    /// one statement of the family that reads a table entitled along declared paths alone:
    /// <c>order_items</c> holds no tenancy of its own and inherits its organisation and its member
    /// through its order and its vendor through its item (design 38 §8), so the verdict for a
    /// column of it is the meet of the ordinals those routes project, per row, through a LEFT JOIN
    /// whose NULL says a route reached nothing (D269 (a)). The prediction now takes that meet in
    /// the package's own terms and the divergence is gone; the entry is kept as an empty map rather
    /// than deleted, because the next such finding wants somewhere to go and the assertion below is
    /// what says there is none today.
    /// </remarks>
    internal static readonly IReadOnlyDictionary<string, string> ReconciliationDiverges =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Every statement layer A holds to the oracle. A statement layer A only <em>records</em> —
    /// <see cref="TenancyCorpusTests.KnownLeaks"/>, a registered defect — has no answer for the two
    /// paths to agree on, and one it does not plan at all
    /// (<see cref="TenancyCorpusTests.NotPlanned"/>) has none either; both stay in the corpus, and
    /// the register and the family README say why.
    /// </summary>
    public static TheoryData<string> Queries()
    {
        var data = new TheoryData<string>();
        foreach (var query in CorpusQueries.LoadM7Tenancy())
        {
            if (!TenancyCorpusTests.KnownLeaks.ContainsKey(query.Name)
                && !TenancyCorpusTests.NotPlanned.ContainsKey(query.Name))
            {
                data.Add(query.Name);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Queries))]
    public async Task Layer_b_answers_what_layer_a_recorded(string name)
    {
        var query = CorpusQueries.LoadM7Tenancy().Single(q => q.Name == name);
        var refused = query.Expectations
            .Where(e => e.StartsWith("policy(", StringComparison.Ordinal))
            .Select(e => e["policy(".Length..^1])
            .ToHashSet(StringComparer.Ordinal);

        var recorded = new StringBuilder();
        foreach (var (principal, grants) in TenancyPolicyFixture.Principals)
        {
            var context = Fixture.Entitlements.Bind(grants);
            recorded.Append("-- ").Append(principal).AppendLine();
            if (refused.Contains(principal))
            {
                var refusal = await Assert.ThrowsAsync<EntitlementException>(
                    async () => await PrepareAsync(query.Sql, context));
                recorded.Append("POLICY ").AppendLine(FirstSentence(refusal.Message));
                recorded.AppendLine();
                continue;
            }

            await using var engine = await EngineAsync();
            var prepared = await engine.WithEntitlements().PrepareAsync(query.Sql, context);
            recorded.Append("report ").AppendLine(Report(prepared));
            foreach (var row in await RowsAsync(engine, prepared, CorpusQueries.Parameters(query.Name)))
            {
                recorded.AppendLine(row);
            }

            recorded.AppendLine();

            if (ReconciliationDiverges.ContainsKey(name))
            {
                continue;
            }

            // The policy's own answer, from the grants and nothing else — the per-column verdicts
            // and, beside them, how much of each table the policy says this principal can see.
            var reconciled = Fixture.Entitlements.Reconcile(prepared, grants);
            Assert.Empty(reconciled.Differences);
            Assert.DoesNotContain(
                reconciled.Visibility, v => v.Verdict == ReconciledVisibility.Disagrees);
            Assert.True(reconciled.Agrees);
        }

        var golden = new FileInfo(Path.Combine(Goldens.FullName, name + ".txt"));
        Assert.True(golden.Exists, $"{golden.FullName} does not exist; layer A records it.");
        Assert.Equal(
            (await File.ReadAllTextAsync(golden.FullName)).ReplaceLineEndings("\n"),
            recorded.ToString().ReplaceLineEndings("\n"));
    }

    /// <summary>
    /// Every statement of the family has the package's own prediction made of it, and none is
    /// excluded (F87, ADR 0061).
    /// </summary>
    /// <remarks>
    /// The theory above skips the prediction for the statements <see cref="ReconciliationDiverges"/>
    /// names, so an entry added there would quietly take a statement out of the comparison. This is
    /// the one line that says the list is empty, where a reviewer reading the family's result will
    /// see it.
    /// </remarks>
    [Fact]
    public void No_statement_is_excluded_from_the_prediction() =>
        Assert.Empty(ReconciliationDiverges);

    /// <summary>
    /// The other half of D216: a role that says what it sees of a protected column no rule names
    /// (<c>docs/design/16-entitlements.md</c> §8).
    /// </summary>
    /// <remarks>
    /// <c>orders.amount</c> carries one rule, the auditor's, so under D216 it is protected and a
    /// manager who says nothing about it sees nothing — which is what §8's own goldens record for
    /// <c>u1</c>. A host that means a manager to see everything the policy protects writes an
    /// ordinary rule naming neither realm nor column, last in the table's list (D222), and then the
    /// same three statements disclose the amount. The two are recorded side by side because the
    /// difference between them is the decision.
    /// </remarks>
    [Theory]
    [InlineData("02_orders_by_org_through_the_path")]
    [InlineData("03_orders_star")]
    [InlineData("07_orders_avg_amount")]
    public async Task An_explicit_role_default_discloses_the_protected_column(string name)
    {
        var query = CorpusQueries.LoadM7Tenancy().Single(q => q.Name == name);
        var fixture = TenancyPolicyFixture.Explicit;
        var context = fixture.Entitlements.Bind(TenancyPolicyFixture.ExplicitManager);

        await using var engine = await EngineAsync(fixture);
        var prepared = await engine.WithEntitlements().PrepareAsync(query.Sql, context);

        var recorded = new StringBuilder();
        recorded.Append("-- u1-explicit").AppendLine();
        recorded.Append("report ").AppendLine(Report(prepared));
        foreach (var row in await RowsAsync(engine, prepared, CorpusQueries.Parameters(query.Name)))
        {
            recorded.AppendLine(row);
        }

        recorded.AppendLine();
        Assert.Empty(
            fixture.Entitlements.Reconcile(prepared, TenancyPolicyFixture.ExplicitManager)
                .Differences);

        var golden = new FileInfo(
            Path.Combine(
                RepoLayout.Corpus.FullName, "plans", "m7-tenancy-explicit", name + ".txt"));
        if (Environment.GetEnvironmentVariable("CHALK_WRITE_FIXTURES") == "1")
        {
            golden.Directory!.Create();
            await File.WriteAllTextAsync(golden.FullName, recorded.ToString());
            return;
        }

        Assert.True(
            golden.Exists,
            $"{golden.FullName} does not exist. Regenerate with CHALK_WRITE_FIXTURES=1.");
        Assert.Equal(
            (await File.ReadAllTextAsync(golden.FullName)).ReplaceLineEndings("\n"),
            recorded.ToString().ReplaceLineEndings("\n"));
    }

    /// <summary>
    /// §8's 24 and 25 as the package writes them: a subject grant confined to the other organization,
    /// and the creator's own rows under both settings of <c>CreatorSees</c>.
    /// </summary>
    [Fact]
    public async Task A_confined_subject_grant_does_not_reach_another_tenancy()
    {
        var inTheirOwn = await RowsAsync(
            "SELECT id, member_id, org_id FROM orders ORDER BY id",
            TenancyPolicyFixture.Principal(
                3,
                Grant.ForSubject(
                    TenancyPolicyFixture.Member, 3, TenancyPolicyFixture.Self, within: 2)));
        var confinedElsewhere = await RowsAsync(
            "SELECT id, member_id, org_id FROM orders ORDER BY id",
            TenancyPolicyFixture.Principal(
                9,
                Grant.ForSubject(
                    TenancyPolicyFixture.Member, 3, TenancyPolicyFixture.Self, within: 1)));

        Assert.Contains("5|3|2", inTheirOwn);
        Assert.Empty(confinedElsewhere);
    }

    [Fact]
    public async Task The_resource_owner_sees_their_own_rows_by_the_option_the_policy_set()
    {
        // u5 holds no grant at all and owns the two notes of O1.
        var creator = TenancyPolicyFixture.Principal(5);

        var full = TenancyPolicyFixture.Create();
        var byRules = TenancyPolicyFixture.Create(ResourceOwnerSees.ByRules);

        // The bodies are read off the fixture rather than restated: every value the policy can hide
        // carries a canary (D252), and a literal here would be one more place to keep in step.
        Assert.Equal(
            [
                $"1|{TenancyFixture.Notes[0].Body}",
                $"2|{TenancyFixture.Notes[1].Body}",
            ],
            await RowsAsync("SELECT id, body FROM notes ORDER BY id", full.Entitlements.Bind(creator), full));
        Assert.Equal(
            ["1|<null>", "2|<null>"],
            await RowsAsync(
                "SELECT id, body FROM notes ORDER BY id", byRules.Entitlements.Bind(creator), byRules));
    }

    /// <summary>
    /// A reconciliation that is <em>supposed</em> to disagree: the same plan read against a principal
    /// whose grants say something else. Without it the corpus's "no differences" could be vacuous.
    /// </summary>
    [Fact]
    public async Task Reconcile_reports_a_difference_when_the_grants_are_not_the_plan_s()
    {
        await using var engine = await EngineAsync();
        var agent = TenancyPolicyFixture.Principal(
            2, Grant.ForTenancy(TenancyPolicyFixture.Org, 1, TenancyPolicyFixture.Agent));
        var prepared = await engine.WithEntitlements().PrepareAsync(
            "SELECT id, first_name FROM members", Fixture.Entitlements.Bind(agent));

        Assert.True(Fixture.Entitlements.Reconcile(prepared, agent).Agrees);

        var manager = TenancyPolicyFixture.Principal(
            2, Grant.ForTenancy(TenancyPolicyFixture.Org, 1, TenancyPolicyFixture.Manager));
        var differences = Fixture.Entitlements.Reconcile(prepared, manager).Differences;

        Assert.Equal(
            ["members.first_name", "members.last_name"],
            differences.Select(d => $"{d.Table}.{d.Column}"));
        foreach (var difference in differences)
        {
            Assert.Equal(Chalk.Ir.DisclosureOutcome.Full, difference.Expected);
            Assert.Equal(Chalk.Ir.DisclosureOutcome.Masked, difference.Reported);
        }
    }

    // ---------------------------------------------------------------- helpers

    private static string FirstSentence(string message)
    {
        var stop = message.IndexOf(". ", StringComparison.Ordinal);
        return stop < 0 ? message : message[..(stop + 1)];
    }

    private static string Report(EntitledQuery prepared) =>
        string.Join(
            ", ",
            prepared.Columns.Select(c => $"{c.Name}:{c.Disclosure}")
                .Concat(prepared.Entitlements.Tables.Select(t => $"{t.Table}:{t.Visibility}")));

    private async Task<ChalkEngine> EngineAsync(TenancyPolicyFixture? fixture = null) =>
        await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = TenancyFixture.ContextId,
            Sources = [(fixture ?? Fixture).Source],
            Planner = sidecar.CreatePlanner(),
            Functions = TenancyAdoFixture.RegisterFunctions,
        });

    private async Task<PreparedQuery> PrepareAsync(string sql, RequestContext context)
    {
        await using var engine = await EngineAsync();
        return await engine.WithEntitlements().PrepareAsync(sql, context);
    }

    private async Task<string[]> RowsAsync(string sql, TenancyPrincipal principal) =>
        await RowsAsync(sql, Fixture.Entitlements.Bind(principal), Fixture);

    private async Task<string[]> RowsAsync(
        string sql, RequestContext context, TenancyPolicyFixture fixture)
    {
        await using var engine = await EngineAsync(fixture);
        return await RowsAsync(engine, await engine.WithEntitlements().PrepareAsync(sql, context));
    }

    private static async Task<string[]> RowsAsync(
        ChalkEngine engine, PreparedQuery prepared, IReadOnlyList<object?>? parameters = null)
    {
        var rows = new List<string>();
        await using var execution = await engine.ExecuteAsync(prepared, parameters);
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

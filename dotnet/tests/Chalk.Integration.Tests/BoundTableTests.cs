using Chalk.Client;
using Chalk.Entitlements;
using Chalk.Ir;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// A bound list too large to fold, end to end (step 26,
/// <c>docs/design/16-entitlements.md</c> §2 and §4).
/// </summary>
/// <remarks>
/// <para>
/// A list of at most <c>FoldMaxRows</c> rows becomes literals in the plan; a larger one stays a
/// relation, and the row predicate becomes a semi-join against a <c>BoundTable</c> the executor
/// materialises at execution start. That is what a host with a tenancy of any size actually gets,
/// and its rows are then in no plan text, no digest and no plan cache key.
/// </para>
/// <para>
/// The fixture's lists are small, so the ceiling is lowered instead of the fixture being made large:
/// the same statements as the same principals, once with the shipped ceiling and once with a ceiling
/// of one, must return the same rows. What changes is the plan; what must not change is the answer.
/// </para>
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class BoundTableTests(SharedSidecar sidecar)
{
    private static readonly TenancyFixture Fixture = TenancyFixture.Shared;

    /// <summary>A manager in two organisations: two rows, so a ceiling of one will not fold them.</summary>
    private static readonly RequestContext TwoOrgManager = TenancyFixture.Principal(
        user: 9, managerOrgs: [1, 2], agentOrgs: [], auditorOrgs: [], subjectPairs: []);

    /// <summary>An agent in two, so the disclosure is masked and the rows still come from a list.</summary>
    private static readonly RequestContext TwoOrgAgent = TenancyFixture.Principal(
        user: 10, managerOrgs: [], agentOrgs: [1, 2], auditorOrgs: [], subjectPairs: []);

    /// <summary>A subject grant on two members in two organisations: a composite list of two rows.</summary>
    private static readonly RequestContext TwoSubjects = TenancyFixture.Principal(
        user: 11, managerOrgs: [], agentOrgs: [], auditorOrgs: [], subjectPairs: [[1, 1], [3, 2]]);

    /// <summary>
    /// Two hundred organisations, well above the shipped fold ceiling of 64, of which exactly one
    /// (O3) holds rows — and <c>u1</c>, who created three orders in O1. Both branches of the split
    /// therefore contribute: the semi-join branch O3's order, the anti-join branch the created ones.
    /// </summary>
    private static readonly RequestContext LargeTenancy = TenancyFixture.Principal(
        user: 1,
        managerOrgs: [3, .. Enumerable.Range(1001, 199)],
        agentOrgs: [],
        auditorOrgs: [],
        subjectPairs: []);

    public static TheoryData<string, string> Statements()
    {
        var data = new TheoryData<string, string>();
        foreach (var sql in new[]
        {
            "SELECT id, first_name FROM members ORDER BY id",
            "SELECT id, org_id, last_name FROM members WHERE org_id = 2 ORDER BY id",
            "SELECT COUNT(*) FROM members",
        })
        {
            data.Add(sql, "manager");
            data.Add(sql, "agent");
        }

        return data;
    }

    /// <summary>
    /// The property the ceiling must have: it changes the plan and not the answer. Lowering it to
    /// one takes every one of these principals' lists out of the plan and into a
    /// <c>BoundTable</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(Statements))]
    public async Task The_same_rows_come_back_with_the_list_folded_and_with_it_materialised(
        string sql, string principal)
    {
        var context = principal == "manager" ? TwoOrgManager : TwoOrgAgent;

        var folded = await RowsAsync(sql, context);
        var materialised = await RowsAsync(sql, TenancyFixture.WithFoldCeiling(context, 1));

        Assert.Equal(folded, materialised);
    }

    /// <summary>
    /// A list above the ceiling standing <em>inside a disjunction</em> — <c>orders</c>' created-by
    /// fail-safe puts one there — is split into a union of a semi-join branch and an anti-join
    /// branch, and answers what the oracle answers (F36).
    /// </summary>
    /// <remarks>
    /// It used to be refused naming <c>LITERAL_AGG</c>, the aggregate Calcite's three-valued rewrite
    /// needs and the IR does not carry. The rewrite is Chalk's own, in the pass: a row satisfies
    /// <c>M OR R</c> exactly when <c>M</c> holds, or <c>M</c> does not and <c>R</c> does, so each
    /// membership stands where it may answer two-valued and every row appears once.
    /// </remarks>
    [Fact]
    public async Task A_list_above_the_ceiling_inside_a_disjunction_is_split_into_two_branches()
    {
        const string Sql = "SELECT id, org_id, amount FROM orders ORDER BY id";
        var context = TenancyFixture.WithFoldCeiling(TwoOrgManager, 1);

        Assert.Equal(
            await OracleRowsAsync(Sql, context),
            await RowsAsync(Sql, context));
    }

    /// <summary>
    /// The same, at the shipped ceiling and with a tenancy nobody would fold: two hundred
    /// organisations, of which the principal's grant really covers one, plus the rows they created
    /// elsewhere. Both branches contribute, and the answer is the oracle's.
    /// </summary>
    [Fact]
    public async Task The_created_by_fail_safe_with_a_two_hundred_tenancy_list_agrees_with_the_oracle()
    {
        const string Sql = "SELECT id, org_id, amount, note FROM orders ORDER BY id";

        Assert.Equal(
            await OracleRowsAsync(Sql, LargeTenancy),
            await RowsAsync(Sql, LargeTenancy));
    }

    /// <summary>
    /// And both branches are reported: one entry for the table, its visibility the widest of the
    /// two, and <c>row_predicate_pushed</c> honestly false for a source the client scans.
    /// </summary>
    [Fact]
    public async Task Both_branches_are_reported_as_one_entitled_table()
    {
        await using var engine = await EngineAsync();
        var prepared = await engine.WithEntitlements().PrepareAsync(
            "SELECT id, org_id FROM orders ORDER BY id", LargeTenancy);

        var orders = Assert.Single(prepared.Entitlements.Tables, t => t.Table == "orders");
        Assert.Equal(TableVisibility.Some, orders.Visibility);
        Assert.False(orders.RowPredicatePushed);

        // The rows the host bound are in no artefact the plan leaves behind, however many branches
        // the split made: the plan names the relation, never its members.
        var text = PlanPrinter.Print(prepared.Plan);
        Assert.Contains("BoundTable", text, StringComparison.Ordinal);
        Assert.DoesNotContain("1199", text, StringComparison.Ordinal);
    }

    /// <summary>A composite list — the confined subject grant's pairs — takes the same path.</summary>
    [Fact]
    public async Task A_composite_list_above_the_ceiling_gives_the_same_rows()
    {
        const string Sql = "SELECT id, org_id FROM members ORDER BY id";

        Assert.Equal(
            await RowsAsync(Sql, TwoSubjects),
            await RowsAsync(Sql, TenancyFixture.WithFoldCeiling(TwoSubjects, 1)));
    }

    /// <summary>
    /// And the plan really is different: a <c>BoundTable</c> where the folded plan had literals,
    /// with nothing left for execution to bind, because the prepared query carries its own context.
    /// </summary>
    [Fact]
    public async Task A_list_above_the_ceiling_becomes_a_context_table_in_the_plan()
    {
        await using var engine = await EngineAsync();
        const string Sql = "SELECT id FROM members ORDER BY id";

        var folded = await engine.WithEntitlements().PrepareAsync(Sql, TwoOrgManager);
        Assert.DoesNotContain("BoundTable", PlanPrinter.Print(folded.Plan), StringComparison.Ordinal);

        var materialised = await engine.WithEntitlements().PrepareAsync(
            Sql, TenancyFixture.WithFoldCeiling(TwoOrgManager, 1));
        Assert.Contains("BoundTable", PlanPrinter.Print(materialised.Plan), StringComparison.Ordinal);

        // The names are bound — by the prepared query's own context — so nothing is outstanding.
        Assert.Empty(materialised.Query.RequiredContext);

        // And the rows the host bound are in no artefact the plan leaves behind: the plan text names
        // the relation, never its members.
        Assert.DoesNotContain("manager_orgs=[", PlanPrinter.Print(materialised.Plan), StringComparison.Ordinal);
    }

    /// <summary>
    /// The reference executor materialises the same relation its own way, so the oracle can be run
    /// over a plan the ceiling pushed into a context table.
    /// </summary>
    [Fact]
    public async Task The_reference_executor_materialises_the_relation_too()
    {
        const string Sql = "SELECT id, first_name FROM members ORDER BY id";
        var context = TenancyFixture.WithFoldCeiling(TwoOrgAgent, 1);

        Assert.Equal(
            await RowsAsync(Sql, context),
            await RowsAsync(Sql, context, reference: true));
    }

    // ---------------------------------------------------------------- helpers

    private async Task<ChalkEngine> EngineAsync(bool reference = false) =>
        await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = TenancyFixture.ContextId,
            Sources = [Fixture.Source],
            Planner = sidecar.CreatePlanner(),
            Execution = new ExecutionOptions
            {
                Engine = reference ? ExecutionEngine.Reference : ExecutionEngine.Vectorised,
            },
        });

    /// <summary>
    /// The same statement over rows the oracle disclosed itself, on tables carrying no entitlement:
    /// nothing is rewritten there and the context is never bound, so the two paths share only their
    /// answer.
    /// </summary>
    private async Task<string[]> OracleRowsAsync(string sql, RequestContext context)
    {
        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = TenancyFixture.ContextId,
            Sources = [TenancyOracle.Disclose(context)],
            Planner = sidecar.CreatePlanner(),
            Functions = TenancyAdoFixture.RegisterFunctions,
        });

        var prepared = await engine.WithEntitlements().PrepareAsync(sql);
        var rows = new List<string>();
        await using var execution =
            await engine.ExecuteAsync(prepared, (IReadOnlyList<object?>?)null);
        await foreach (var batch in execution.Batches)
        {
            using (batch)
            {
                rows.AddRange(BatchReader.ToRows(batch).Select(
                    r => string.Join("|", r.Select(v => v?.ToString() ?? ""))));
            }
        }

        return [.. rows];
    }

    private async Task<string[]> RowsAsync(string sql, RequestContext context, bool reference = false)
    {
        await using var engine = await EngineAsync(reference);
        var prepared = await engine.WithEntitlements().PrepareAsync(sql, context);
        var rows = new List<string>();
        await using var execution =
            await engine.ExecuteAsync(prepared, (IReadOnlyList<object?>?)null);
        await foreach (var batch in execution.Batches)
        {
            using (batch)
            {
                rows.AddRange(BatchReader.ToRows(batch).Select(
                    r => string.Join("|", r.Select(v => v?.ToString() ?? ""))));
            }
        }

        return [.. rows];
    }
}

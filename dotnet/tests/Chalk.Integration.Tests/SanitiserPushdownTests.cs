using System.Text;
using Chalk.Catalog;
using Chalk.Client;
using Chalk.Entitlements;
using Chalk.Ir;
using Chalk.Sources.Ado;
using Chalk.TestKit;
using SourceCapabilities = Chalk.Catalog.SourceCapabilities;

namespace Chalk.Integration.Tests;

/// <summary>
/// The entitled leaf's sanitiser evaluated <b>by the source</b> (D273, F104): what moves when a
/// host permits it, what does not, and that the answer is the same either way.
/// </summary>
/// <remarks>
/// <para>
/// A sanitiser is a <c>CASE</c> the pass builds from the descriptor's rules
/// (<c>docs/design/16-entitlements.md</c> §3.2), so <c>supports_case</c> is what a source needs
/// before one can travel at all. It is not what decides that one does: a sanitiser over a column
/// this principal does not see plainly is a <b>mask</b>, and §3.8's rule is unchanged — the table
/// must declare <c>push_masks</c> and the source <c>supports_mask_pushdown</c>, both, and the cost
/// model still chooses. Three conditions, and this suite is where the third one arriving is
/// measured against the first two.
/// </para>
/// <para>
/// <c>orders.note</c> is the column that shows it, and it shows it because of what its rules are
/// made of: the mask is the constant <c>'********'</c> and the fall-through is the placeholder, so
/// every arm of the sanitiser is an expression the source can evaluate. <c>members.first_name</c>
/// is the counter-example in the same fixture — its mask is <c>SUBSTRING</c>, which the in-box
/// ADO descriptor does not declare — and it stays above the boundary with every flag set, which is
/// what says the gate still judges the arms.
/// </para>
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class SanitiserPushdownTests(SharedSidecar sidecar) : IDisposable
{
    private const string Notes = "SELECT id, note FROM orders ORDER BY id";

    private readonly List<TenancyAdoFixture> _fixtures = [];

    /// <summary>
    /// The in-box DuckDB descriptor with the source's half of D153's mask opt-in, and D273's
    /// conditional either way. Everything else is <see cref="AdoCapabilities.For"/>'s own.
    /// </summary>
    private static SourceCapabilities TakingMasks(bool supportsCase)
    {
        var full = AdoCapabilities.For(DialectProfiles.DuckDb);
        return new SourceCapabilities
        {
            QueryLanguage = full.QueryLanguage,
            PushablePredicates = full.PushablePredicates,
            PushableFunctions = full.PushableFunctions,
            PushableAggregates = full.PushableAggregates,
            SupportsProject = full.SupportsProject,
            SupportsSort = full.SupportsSort,
            SupportsLimit = full.SupportsLimit,
            SupportsOffset = full.SupportsOffset,
            SupportsDistinct = full.SupportsDistinct,
            SupportsGroupBy = full.SupportsGroupBy,
            SupportsHaving = full.SupportsHaving,
            SupportsInnerJoin = full.SupportsInnerJoin,
            SupportsOuterJoin = full.SupportsOuterJoin,
            SupportsSemiAntiJoin = full.SupportsSemiAntiJoin,
            MaxInList = full.MaxInList,
            SupportsParameters = full.SupportsParameters,
            SupportsValuesJoin = full.SupportsValuesJoin,
            SupportsRowValueInList = full.SupportsRowValueInList,
            SupportsMaskPushdown = true,
            SupportsCase = supportsCase,
        };
    }

    private TenancyAdoFixture Permitting(bool supportsCase)
    {
        var fixture = TenancyAdoFixture.CreateDuckDb(
            pushMasks: true, capabilities: TakingMasks(supportsCase));
        _fixtures.Add(fixture);
        return fixture;
    }

    public void Dispose()
    {
        foreach (var fixture in _fixtures)
        {
            fixture.Dispose();
        }
    }

    // ---------------------------------------------------------------- what moves

    /// <summary>
    /// The whole sanitiser at the source: the raw column in the arm the principal is entitled to,
    /// the mask in the arm it is not, the placeholder in the fall-through — and nothing left above
    /// the boundary to evaluate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The principal is <c>u1</c>, who manages one organisation and acts as an agent in a second,
    /// which is what makes the folded disclosure of <c>note</c> genuinely per-row and the sanitiser
    /// a conditional with three arms rather than a constant the fold collapses.
    /// </para>
    /// <para>
    /// The trade this makes is D153's own and it is worth naming: a pushed mask puts the <em>raw
    /// column's name</em> in query text, which is why both flags exist and why neither is on by
    /// default. What never travels is a raw <em>value</em> — the source computes the arm and returns
    /// the mask or the placeholder for every row this principal may not read.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_sanitiser_is_pushed_whole_where_the_source_takes_a_conditional()
    {
        var (prepared, sql) = await PlanAsync(Permitting(supportsCase: true), Notes, TenancyFixture.U1);

        Assert.Contains("CASE WHEN", sql, StringComparison.Ordinal);
        Assert.Contains("THEN \"note\"", sql, StringComparison.Ordinal);
        Assert.Contains("'********'", sql, StringComparison.Ordinal);
        Assert.Contains("ELSE NULL END", sql, StringComparison.Ordinal);

        // Nothing conditional is left for this client to evaluate.
        Assert.Equal(0, Conditionals(PlanWalker.ExecutedRels(prepared.Query.Plan)));
        Assert.True(Conditionals(PlanWalker.Rels(prepared.Query.Plan)) > 0);
    }

    /// <summary>
    /// And the residual: the same policy, the same table, the same principal, over a source that
    /// says it cannot evaluate a conditional. The sanitiser is then exactly where it was before
    /// D273 — above the boundary, over the rows the pushed row predicate returned.
    /// </summary>
    [Fact]
    public async Task The_same_sanitiser_stays_above_the_boundary_where_the_source_does_not()
    {
        var (prepared, sql) = await PlanAsync(
            Permitting(supportsCase: false), Notes, TenancyFixture.U1);

        Assert.DoesNotContain("CASE WHEN", sql, StringComparison.Ordinal);
        Assert.True(Conditionals(PlanWalker.ExecutedRels(prepared.Query.Plan)) > 0);

        // The row predicate is not what changed: it is below the mask and reaches the source either
        // way (§3.7, D229).
        Assert.Contains("WHERE", sql, StringComparison.Ordinal);
    }

    /// <summary>
    /// The gate still judges the arms. <c>members.first_name</c> is masked with <c>SUBSTRING</c>,
    /// which the in-box ADO descriptor deliberately does not declare, so its sanitiser stays local
    /// with every one of the three flags set — which is what says <c>supports_case</c> admits the
    /// shape and nothing else.
    /// </summary>
    [Fact]
    public async Task A_conditional_whose_arm_the_source_cannot_evaluate_stays_local()
    {
        var (_, sql) = await PlanAsync(
            Permitting(supportsCase: true),
            "SELECT id, first_name FROM members ORDER BY id",
            TenancyFixture.U2);

        Assert.DoesNotContain("SUBSTRING", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CASE WHEN", sql, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- and the answer does not move

    /// <summary>
    /// Every principal, both sides of the boundary, the same rows — with D252's detector over
    /// everything each principal received: the rows, the report, the refusal, and the SQL the plan
    /// sends to a source.
    /// </summary>
    /// <remarks>
    /// This is the assertion the capability is accepted on. Where the sanitiser runs is a planning
    /// decision; what a principal may see is a policy one, and the first may not touch the second.
    /// The transcript is compared whole so that a disagreement names the principal it is about.
    /// </remarks>
    [Fact]
    public async Task Every_principal_sees_the_same_rows_whichever_side_evaluated_the_sanitiser()
    {
        await using var pushed = await EngineAsync(Permitting(supportsCase: true));
        await using var local = await EngineAsync(Permitting(supportsCase: false));

        var there = new StringBuilder();
        var here = new StringBuilder();
        foreach (var (statement, parameters) in Statements)
        {
            foreach (var (principal, context) in TenancyFixture.Principals)
            {
                there.Append("-- ").Append(statement).Append(' ').AppendLine(principal);
                here.Append("-- ").Append(statement).Append(' ').AppendLine(principal);
                foreach (var row in
                    await RowsAsync(pushed, statement, parameters, principal, context, "pushed"))
                {
                    there.AppendLine(row);
                }

                foreach (var row in
                    await RowsAsync(local, statement, parameters, principal, context, "local"))
                {
                    here.AppendLine(row);
                }
            }
        }

        Assert.Equal(here.ToString(), there.ToString());

        // And the transcript is not empty in the way that would make the comparison vacuous: some
        // principal saw a masked value and some principal saw a raw one.
        Assert.Contains("********", there.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The statements the comparison runs, and why each is here. The first two are the sanitiser
    /// itself, once alone and once beside the placeholder column the same leaf withholds. The third
    /// is a <b>tested</b> column (D261): its derived comparison is computed over the raw value, so
    /// it is withheld like a mask and travels under the same two flags — and when it travels, the
    /// taint check's clause 2 has to find it by <em>shape</em> rather than by position, which is the
    /// case ADR 0042 §1 wrote that half for and which nothing had put inside a pushed subtree until
    /// now.
    /// </summary>
    private static IReadOnlyList<(string Sql, IReadOnlyList<object?>? Parameters)> Statements { get; } =
    [
        (Notes, null),
        ("SELECT id, org_id, note FROM orders ORDER BY id", null),
        ("SELECT id, national_id = ? AS confirmed FROM members ORDER BY id",
            [TenancyFixture.Members[0].NationalId]),
    ];

    /// <summary>
    /// The breadcrumbs the client's own invariant reads survive the move (I-IR-E, §3.10). The
    /// entitled read is now <em>inside</em> the pushed subtree, so it is the inclusive walk that
    /// finds it (ADR 0062 §6) — and what it finds there is that read's own disclosure map
    /// (ADR 0062 §5), with the row predicate reported pushed (D229) exactly as before.
    /// </summary>
    [Fact]
    public async Task The_pushed_read_keeps_its_own_disclosures_and_the_row_predicate_flag()
    {
        var (prepared, _) = await PlanAsync(Permitting(supportsCase: true), Notes, TenancyFixture.U1);

        Assert.DoesNotContain(
            PlanWalker.ExecutedRels(prepared.Query.Plan),
            r => r.KindCase == Rel.KindOneofCase.Read && r.Read.Table.Table == "orders");

        var read = Assert.Single(
            PlanWalker.Rels(prepared.Query.Plan),
            r => r.KindCase == Rel.KindOneofCase.Read && r.Read.Table.Table == "orders").Read;

        Assert.NotEmpty(read.Disclosures);
        Assert.Contains(read.Disclosures, d => d.Outcome == DisclosureOutcome.PerRow);

        var orders = Assert.Single(prepared.Entitlements.Tables, t => t.Table == "orders");
        Assert.True(orders.RowPredicatePushed);
    }

    // ---------------------------------------------------------------- plumbing

    private async Task<(EntitledQuery Prepared, string Sql)> PlanAsync(
        TenancyAdoFixture fixture, string sql, RequestContext context)
    {
        await using var engine = await EngineAsync(fixture);
        var prepared = await engine.WithEntitlements().PrepareAsync(sql, context);
        return (prepared, string.Join(
            "\n", TenancyRemoteBindingTests.RemoteSql(prepared.Query.Plan)));
    }

    /// <summary>The rows one engine gives this principal, with the detector over all of it.</summary>
    private static async Task<string[]> RowsAsync(
        ChalkEngine engine,
        string sql,
        IReadOnlyList<object?>? parameters,
        string principal,
        RequestContext context,
        string where)
    {
        var detector = LeakDetector.For(principal, context);
        try
        {
            var prepared = await engine.WithEntitlements().PrepareAsync(sql, context);
            var rows = new List<string>();
            await using (var execution = await engine.ExecuteAsync(prepared.Query, parameters))
            {
                await foreach (var batch in execution.Batches)
                {
                    using (batch)
                    {
                        rows.AddRange(BatchReader.ToRows(batch).Select(
                            r => string.Join("|", r.Select(v => v?.ToString() ?? "<null>"))));
                    }
                }
            }

            detector.Inspect(new LeakScan
            {
                Statement = $"{sql} (the sanitiser {where})",
                Rows = rows,
                Columns = [.. prepared.Columns.Select(c => c.Name)],
                Report = string.Join(
                    ", ",
                    prepared.Columns.Select(c => $"{c.Name}:{c.Disclosure}")
                        .Concat(prepared.Entitlements.Tables.Select(t => $"{t.Table}:{t.Visibility}"))),
                RemoteSql = TenancyRemoteBindingTests.RemoteSql(prepared.Query.Plan),
            });
            return [.. rows];
        }
        catch (EntitlementException refused)
        {
            detector.Inspect(new LeakScan
            {
                Statement = $"{sql} (the sanitiser {where})",
                Refusal = refused.Message,
            });
            return ["POLICY " + refused.Message.Split(". ")[0]];
        }
    }

    private static int Conditionals(IEnumerable<Rel> rels) =>
        rels.SelectMany(PlanWalker.OwnExprs)
            .SelectMany(PlanWalker.Exprs)
            .Count(e => e.KindCase == Expr.KindOneofCase.IfThen);

    private async ValueTask<ChalkEngine> EngineAsync(TenancyAdoFixture fixture) =>
        await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = TenancyFixture.ContextId,
            Sources = fixture.Sources,
            Planner = sidecar.CreatePlanner(),
            Functions = TenancyAdoFixture.RegisterFunctions,
        });
}

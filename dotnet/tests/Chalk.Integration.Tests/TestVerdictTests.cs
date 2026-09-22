using Chalk.Client;
using Chalk.Entitlements;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// The test verdict (D261, <c>docs/design/36-test-verdict.md</c>): a column the principal may
/// compare and never read.
/// </summary>
/// <remarks>
/// <para>
/// The corpus holds the statements and records what every principal received; this holds them to the
/// <b>naive model</b>, which the corpus itself cannot. <see cref="TenancyOracle"/> discloses a source
/// and the statement runs over it, and a column that is <em>testable and not readable</em> is not a
/// value a source can hold — so the model of §36.4 lives in
/// <see cref="TenancyOracle.Tested(RequestContext, TenancyFixture.Member, string?)"/> and is applied
/// here: the raw value compared with the parameter where the rule reaches the row, three-valued, and
/// unknown where it does not (ADR 0042).
/// </para>
/// <para>
/// What the verdict must never do is disclose the value, and that is asserted here too — by the
/// report's own label, by the plan text, and by the leak detector over every row and every word of
/// the report, which the corpus runs for these statements as it does for every other.
/// </para>
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class TestVerdictTests(SharedSidecar sidecar)
{
    /// <summary>The identifier the support desk was handed: member 1's, who is in O1.</summary>
    private static string Probe => TenancyFixture.Members[0].NationalId;

    /// <summary>Every principal the fixture names, so a verdict is judged for all of them.</summary>
    public static TheoryData<string> Principals()
    {
        var data = new TheoryData<string>();
        foreach (var (name, _) in TenancyFixture.Principals)
        {
            data.Add(name);
        }

        return data;
    }

    private static RequestContext Context(string name) =>
        TenancyFixture.Principals.Single(p => p.Name == name).Context;

    /// <summary>
    /// The select-list form: one boolean per visible row, exactly what the naive model says, for
    /// every principal — <c>true</c> and <c>false</c> where the rule reaches the row, and NULL where
    /// it does not, because the placeholder is what the comparison then reads.
    /// </summary>
    [Theory]
    [MemberData(nameof(Principals))]
    public async Task The_select_list_form_answers_what_the_naive_model_says(string principal)
    {
        var context = Context(principal);
        if (Refused(principal))
        {
            await RefusedAsync("SELECT id, national_id = ? AS confirmed FROM members ORDER BY id", context);
            return;
        }

        var expected = TenancyOracle.VisibleMembers(context)
            .OrderBy(m => m.Id)
            .Select(m => $"{m.Id}|{Show(TenancyOracle.Tested(context, m, Probe))}")
            .ToArray();

        var (rows, report) = await RowsAsync(
            "SELECT id, national_id = ? AS confirmed FROM members ORDER BY id", context, [Probe]);

        Assert.Equal(expected, rows);

        // What the caller is told the column is: the one bit a permitted comparison discloses, and
        // never the value (D261 §3). Every principal the rule does not reach is handed the
        // placeholder compared, which is a NULL, and the label is what tells the two apart.
        Assert.Equal(
            Tests(context) ? ReportedDisclosure.Tested : ReportedDisclosure.Redacted,
            report.Columns[1].Disclosure);
    }

    /// <summary>
    /// The predicate form: the rows whose raw value matches, and none at all where the rule does not
    /// reach the row — a NULL predicate selects nothing, which is what the placeholder gives.
    /// </summary>
    [Theory]
    [MemberData(nameof(Principals))]
    public async Task The_predicate_form_selects_what_the_naive_model_matches(string principal)
    {
        var context = Context(principal);
        if (Refused(principal))
        {
            await RefusedAsync("SELECT id FROM members WHERE national_id = ? ORDER BY id", context);
            return;
        }

        var expected = TenancyOracle.VisibleMembers(context)
            .Where(m => TenancyOracle.Tested(context, m, Probe) == true)
            .OrderBy(m => m.Id)
            .Select(m => m.Id.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();

        var (rows, _) = await RowsAsync(
            "SELECT id FROM members WHERE national_id = ? ORDER BY id", context, [Probe]);
        Assert.Equal(expected, rows);
    }

    /// <summary>The same for <c>&lt;&gt;</c>, which is the one bit read the other way.</summary>
    [Theory]
    [MemberData(nameof(Principals))]
    public async Task The_not_equals_form_selects_what_the_naive_model_does_not_match(string principal)
    {
        var context = Context(principal);
        if (Refused(principal))
        {
            await RefusedAsync("SELECT id FROM members WHERE national_id <> ? ORDER BY id", context);
            return;
        }

        var expected = TenancyOracle.VisibleMembers(context)
            .Where(m => TenancyOracle.Tested(context, m, Probe) == false)
            .OrderBy(m => m.Id)
            .Select(m => m.Id.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();

        var (rows, _) = await RowsAsync(
            "SELECT id FROM members WHERE national_id <> ? ORDER BY id", context, [Probe]);
        Assert.Equal(expected, rows);
    }

    /// <summary>
    /// The aggregate form (§36.2): a permitted aggregate's <c>FILTER</c> over a population-only
    /// column, guarded by the floor. The group is the organisation, so one statement shows the guard
    /// withholding a group of two and disclosing a group of three.
    /// </summary>
    [Fact]
    public async Task The_aggregate_form_counts_matches_under_the_group_size_guard()
    {
        const string Sql =
            "SELECT org_id, COUNT(*) FILTER (WHERE national_id = ?) AS n "
            + "FROM members GROUP BY org_id ORDER BY org_id";
        var context = Context("u9");
        var probe = TenancyFixture.Members[2].NationalId;

        var expected = new List<string>();
        foreach (var group in TenancyOracle.VisibleMembers(context)
                     .GroupBy(m => m.OrgId)
                     .OrderBy(g => g.Key))
        {
            var matches = group.Count(m => TenancyOracle.Counted(context, m, probe) == true);
            // The floor is the whole of the guard: a group smaller than it discloses nothing, and a
            // NULL there is a withheld value rather than "no rows" (D202).
            expected.Add(
                group.Count() >= TenancyFixture.MinGroupSize
                    ? $"{group.Key}|{matches}"
                    : $"{group.Key}|<null>");
        }

        var (rows, report) = await RowsAsync(Sql, context, [probe]);
        Assert.Equal(expected, rows);
        Assert.Equal(ReportedDisclosure.Aggregate, report.Columns[1].Disclosure);
    }

    /// <summary>
    /// The audit observer's own record (§36.3): every tested column and every shape the statement
    /// used, and never a parameter's value — enumeration by repeated probes is the host's to limit,
    /// and this is what the host is handed to limit it with.
    /// </summary>
    [Fact]
    public async Task The_report_names_every_tested_column_and_shape()
    {
        var context = Context("u8");
        var (_, report) = await RowsAsync(
            "SELECT id FROM members WHERE national_id = ? OR national_id <> ? ORDER BY id",
            context,
            [Probe, Probe]);

        var tested = Assert.Single(report.Tables.Single(t => t.Table == "members").Tested);
        Assert.Equal("national_id", tested.Column);
        Assert.Equal(["EQUALS", "NOT_EQUALS"], tested.Shapes);
    }

    /// <summary>A statement that tests nothing names nothing, which is every statement until now.</summary>
    [Fact]
    public async Task A_statement_that_tests_nothing_names_nothing()
    {
        var (_, report) = await RowsAsync(
            "SELECT id FROM members ORDER BY id", Context("u8"), null);
        Assert.Empty(report.Tables.Single(t => t.Table == "members").Tested);
    }

    /// <summary>
    /// A shape the rule does not name is not a comparison the leaf computes: the support desk's rule
    /// permits equality, inequality and a list, and a <c>LIKE</c> over the same column reads the
    /// placeholder and matches nothing — the answer NONE gives.
    /// </summary>
    [Fact]
    public async Task A_shape_the_rule_does_not_name_reads_the_placeholder()
    {
        var (rows, report) = await RowsAsync(
            "SELECT id FROM members WHERE national_id LIKE 'AA%' ORDER BY id", Context("u8"), null);
        Assert.Empty(rows);
        Assert.Empty(report.Tables.Single(t => t.Table == "members").Tested);
    }

    /// <summary>
    /// The derived column in the plan, as the leaf computes it: a comparison over the <b>raw</b>
    /// column inside the entitled read's own projection, and nothing above it that reads the column
    /// itself.
    /// </summary>
    [Fact]
    public async Task The_comparison_is_computed_in_the_leaf()
    {
        await using var engine = await EngineAsync();
        var prepared = await engine.WithEntitlements().PrepareAsync(
            "SELECT id FROM members WHERE national_id = ? ORDER BY id", Context("u8"));
        var text = Chalk.Ir.PlanExtensions.ToPlanText(prepared.Plan);

        // The leaf's own projection holds the comparison; what stands above it is a reference.
        Assert.Contains("national_id", text, StringComparison.Ordinal);
        Assert.Contains("entitled", text, StringComparison.Ordinal);
        Assert.DoesNotContain(TenancyCanaries.Marker, text, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- the two engines

    /// <summary>Whether this principal may test <c>national_id</c> on any row it can see.</summary>
    private static bool Tests(RequestContext context) =>
        TenancyOracle.VisibleMembers(context).Any(m => TenancyOracle.Tested(context, m, "x") is not null);

    /// <summary>
    /// The counting role reads <c>national_id</c> as population-only, so a value use of it is
    /// refused for that principal exactly as any population-only column is (§3.4) — which is what
    /// these statements are, every one of them.
    /// </summary>
    private static bool Refused(string principal) => principal == "u9";

    private static string Show(bool? value) => value is null ? "<null>" : value.Value ? "True" : "False";

    private async Task RefusedAsync(string sql, RequestContext context)
    {
        await using var engine = await EngineAsync();
        await Assert.ThrowsAsync<EntitlementException>(
            async () => await engine.WithEntitlements().PrepareAsync(sql, context));
    }

    private async Task<(string[] Rows, EntitlementsReport Report)> RowsAsync(
        string sql, RequestContext context, IReadOnlyList<object?>? parameters)
    {
        await using var engine = await EngineAsync();
        var prepared = await engine.WithEntitlements().PrepareAsync(sql, context);
        var rows = new List<string>();
        await using var execution = await engine.ExecuteAsync(prepared.Query, parameters);
        await foreach (var batch in execution.Batches)
        {
            using (batch)
            {
                rows.AddRange(BatchReader.ToRows(batch).Select(
                    r => string.Join("|", r.Select(v => v?.ToString() ?? "<null>"))));
            }
        }

        return ([.. rows], prepared.Entitlements);
    }

    private ValueTask<ChalkEngine> EngineAsync() => ChalkEngine.CreateAsync(new ChalkEngineOptions
    {
        ContextId = TenancyFixture.ContextId,
        Sources = [TenancyFixture.Shared.Source],
        Planner = sidecar.CreatePlanner(),
    });
}

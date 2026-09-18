using Chalk.Client;
using Chalk.Entitlements;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// What the caller is told, one shape per ambiguity (step 26,
/// <c>docs/design/16-entitlements.md</c> §3.12, D202).
/// </summary>
/// <remarks>
/// <para>
/// The report's walk is structural: it starts at the leaf the pass emitted, where the fact is known,
/// and carries the outcome upward through the shapes the rewritten tree has. These are the shapes
/// where a wrong label is plausible — a window that reads a masked value as against one that reads
/// only a position, a union whose branches disagree, a join whose two occurrences of the same table
/// folded differently — and each is asserted twice: on
/// <see cref="PreparedQuery.Columns"/> and on the Arrow field metadata a typed consumer reads, so a
/// grid and a report can never disagree.
/// </para>
/// <para>
/// The exhaustiveness of the walk itself is a planner test
/// (<c>chalk.planner.entitlement.DisclosureFlowCoverageTest</c>): a rel kind added to the IR fails
/// the build until a case claims it.
/// </para>
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class DisclosureReportTests(SharedSidecar sidecar)
{
    private static readonly TenancyFixture Fixture = TenancyFixture.Shared;

    /// <summary>
    /// A window function discloses through its <em>arguments</em>. <c>COUNT(first_name) OVER (…)</c>
    /// reads a masked value and says so; <c>ROW_NUMBER()</c> over the same partition reads a
    /// position, and a masked value used as a partition key is ordinary untainted data (§3.1).
    /// </summary>
    [Fact]
    public async Task A_window_over_a_masked_column_is_masked_and_its_row_number_is_full() =>
        await AssertReportAsync(
            "SELECT COUNT(first_name) OVER (PARTITION BY org_id) AS seen, "
            + "ROW_NUMBER() OVER (PARTITION BY first_name ORDER BY id) AS rn FROM members",
            TenancyFixture.U2,
            ReportedDisclosure.Masked,
            ReportedDisclosure.Full);

    /// <summary>
    /// A union's branches are alternative <em>rows</em>, not two origins of one value: <c>u1</c>
    /// manages O1 and acts as an agent in O2, so this column really does hand back some raw names
    /// and some initials, and "decided per row" is the only true answer.
    /// </summary>
    [Fact]
    public async Task A_masked_column_unioned_with_a_full_one_is_per_row() =>
        await AssertReportAsync(
            "SELECT first_name FROM members WHERE org_id = 1 "
            + "UNION ALL SELECT first_name FROM members WHERE org_id = 2",
            TenancyFixture.U1,
            ReportedDisclosure.PerRow);

    /// <summary>
    /// Two occurrences of one table are two leaves, each folded under its own conjuncts, so the same
    /// column name can be full on one side of a join and masked on the other.
    /// </summary>
    [Fact]
    public async Task A_join_whose_sides_disclose_the_same_column_differently_reports_each() =>
        await AssertReportAsync(
            "SELECT a.first_name AS af, b.first_name AS bf FROM "
            + "(SELECT id, first_name FROM members WHERE org_id = 1) a JOIN "
            + "(SELECT id, first_name FROM members WHERE org_id = 2) b ON a.id = b.id",
            TenancyFixture.U1,
            ReportedDisclosure.Full,
            ReportedDisclosure.Masked);

    /// <summary>
    /// An aggregate over a masked column is a value derived from masks, and that is what it is
    /// called. <c>Aggregate</c> is reserved for a population-only column under the guard, where a
    /// NULL means "below the floor" rather than "no rows".
    /// </summary>
    [Fact]
    public async Task An_aggregate_over_a_masked_column_is_masked_and_not_aggregate() =>
        await AssertReportAsync(
            "SELECT MIN(first_name) AS lo, COUNT(*) AS n FROM members",
            TenancyFixture.U2,
            ReportedDisclosure.Masked,
            ReportedDisclosure.Full);

    /// <summary>A derived column takes the meet over what it reads; the value may be either.</summary>
    [Fact]
    public async Task A_coalesce_of_a_masked_column_and_a_full_one_is_masked() =>
        await AssertReportAsync(
            "SELECT COALESCE(first_name, postcode) AS name FROM members",
            TenancyFixture.U2,
            ReportedDisclosure.Masked);

    /// <summary>The pass recurses into a correlate, so a lateral's inner side is entitled too.</summary>
    [Fact]
    public async Task A_lateral_whose_inner_side_is_entitled_reports_its_columns() =>
        await AssertReportAsync(
            "SELECT o.id AS org, c.lo AS lo FROM orgs o LEFT JOIN LATERAL "
            + "(SELECT MIN(m.first_name) AS lo FROM members m WHERE m.org_id = o.id) c ON TRUE",
            TenancyFixture.U2,
            ReportedDisclosure.Full,
            ReportedDisclosure.Masked);

    /// <summary>
    /// An <c>UNNEST</c> flattens the list it was given and discloses what that list disclosed;
    /// a masked column standing beside it is unaffected, and neither borrows the other's name.
    /// </summary>
    [Fact]
    public async Task An_unnest_of_an_untainted_list_beside_a_masked_column_keeps_both_names() =>
        await AssertReportAsync(
            "SELECT m.first_name AS name, u.x AS x FROM members m "
            + "CROSS JOIN UNNEST(ARRAY[1, 2]) AS u(x)",
            TenancyFixture.U2,
            ReportedDisclosure.Masked,
            ReportedDisclosure.Full);

    /// <summary>
    /// Sorting and limiting drop and reorder rows and change no value, so a placeholder is still a
    /// placeholder at the top — and a grid must not read its NULL as data.
    /// </summary>
    [Fact]
    public async Task A_placeholder_through_an_order_by_and_a_limit_is_still_redacted() =>
        await AssertReportAsync(
            "SELECT national_id FROM members ORDER BY id LIMIT 1",
            TenancyFixture.U2,
            ReportedDisclosure.Redacted);

    /// <summary>
    /// A self-join in which one occurrence is narrowed to a single subject: that leaf folds to a
    /// constant, the other does not, and the report says so column by column.
    /// </summary>
    [Fact]
    public async Task A_self_join_with_one_subject_only_occurrence_reports_the_two_apart() =>
        await AssertReportAsync(
            "SELECT a.first_name AS any_name, b.first_name AS subject_name FROM members a JOIN "
            + "(SELECT org_id, first_name FROM members WHERE id = 3 AND org_id = 2) b "
            + "ON a.org_id = b.org_id",
            TenancyFixture.U1,
            ReportedDisclosure.PerRow,
            ReportedDisclosure.Masked);

    /// <summary>
    /// A table with no entitlement discloses in full, exactly — the report says nothing worse about
    /// it because it stands beside an entitled table in the same statement.
    /// </summary>
    [Fact]
    public async Task An_unentitled_tables_columns_are_full_beside_an_entitled_tables() =>
        await AssertReportAsync(
            "SELECT s.name AS sym, m.first_name AS name FROM symbols s CROSS JOIN members m",
            TenancyFixture.U2,
            ReportedDisclosure.Full,
            ReportedDisclosure.Masked);

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Asserts the per-column report and the Arrow field metadata agree with each other and with
    /// what the test expects. The metadata is written for every field as soon as one column
    /// discloses anything but the value, and for none of them otherwise, which is the shape a
    /// consumer branches on.
    /// </summary>
    private async Task AssertReportAsync(
        string sql, RequestContext context, params ReportedDisclosure[] expected)
    {
        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = TenancyFixture.ContextId,
            Sources = [Fixture.Source],
            Planner = sidecar.CreatePlanner(),
        });
        var prepared = await engine.WithEntitlements().PrepareAsync(sql, context);

        Assert.Equal(expected, prepared.Columns.Select(c => c.Disclosure));

        var decorated = expected.Any(d => d != ReportedDisclosure.Full);
        var fields = prepared.OutputSchema.FieldsList;
        Assert.Equal(expected.Length, fields.Count);
        for (var i = 0; i < expected.Length; i++)
        {
            var metadata = fields[i].Metadata;
            if (!decorated)
            {
                Assert.True(
                    metadata is null || !metadata.ContainsKey("chalk.disclosure"),
                    $"column {i} carries disclosure metadata for an all-full statement");
                continue;
            }

            Assert.Equal(Name(expected[i]), metadata!["chalk.disclosure"]);
        }
    }

    private static string Name(ReportedDisclosure disclosure) => disclosure switch
    {
        ReportedDisclosure.Masked => "MASKED",
        ReportedDisclosure.Redacted => "REDACTED",
        ReportedDisclosure.PerRow => "PER_ROW",
        ReportedDisclosure.Aggregate => "AGGREGATE",
        _ => "FULL",
    };
}

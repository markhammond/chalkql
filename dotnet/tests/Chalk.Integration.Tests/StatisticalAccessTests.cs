using Chalk.Client;
using Chalk.Entitlements;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// The <c>statistical</c> opt-in (<c>docs/design/16-entitlements.md</c> §3.4, D203; §8's query 22):
/// a column a host has decided to protect by <b>query-set-size control</b> rather than by a mask.
/// </summary>
/// <remarks>
/// <para>
/// With it, the raw value reaches predicates, join conditions, an aggregate's FILTER and grouping
/// keys — but only in a statement whose every output column is an aggregate or a group key, with no
/// window, and with no individual pinned or excluded. Every group below the floor is then
/// <em>dropped</em> rather than NULLed, because with a raw grouping key the key itself is the
/// disclosure and a NULLed measure beside it would say what the floor exists to withhold.
/// </para>
/// <para>
/// The limit is stated rather than discovered: trackers built across statements out of
/// quasi-identifiers remain possible. This is query-set-size control, not differential privacy.
/// </para>
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class StatisticalAccessTests(SharedSidecar sidecar)
{
    /// <summary>Two is enough to be a floor here: only one first name is shared between two rows.</summary>
    private const int Floor = 2;

    private static readonly TenancyFixture Fixture = TenancyFixture.Statistical(Floor);

    /// <summary>
    /// An agent in both organisations: <c>first_name</c> would be initial-masked for every row they
    /// can see, and the opt-in hands them the raw name under the floor instead.
    /// </summary>
    /// <summary>
    /// The one first name two visible members share, read off the fixture rather than written out:
    /// every value the policy can mask carries a canary (D252), and what this class is about is the
    /// group of two rather than the letters it is made of.
    /// </summary>
    private static string Shared => TenancyFixture.Members[0].FirstName;

    private static readonly RequestContext Agent = TenancyFixture.Principal(
        user: 2, managerOrgs: [], agentOrgs: [1, 2], auditorOrgs: [], subjectPairs: []);

    /// <summary>
    /// §8's query 22: k-anonymous keys and the filtered count, with every group below the floor
    /// dropped. Of the five members this principal can see, only one first name is held by two of
    /// them — one in each organization — so it is the only group that survives. The name itself is
    /// read off the fixture: it carries a canary (D252) and is never restated here.
    /// </summary>
    [Fact]
    public async Task A_group_key_is_raw_and_every_group_below_the_floor_is_dropped()
    {
        var rows = await RowsAsync(
            """
            SELECT first_name, COUNT(*), COUNT(*) FILTER (WHERE first_name LIKE 'T%')
            FROM members GROUP BY first_name ORDER BY first_name
            """);

        Assert.Equal([$"{Shared}|2|2"], rows);
    }

    /// <summary>The raw value in an ordinary predicate, over groups the floor admits.</summary>
    [Fact]
    public async Task The_raw_value_reaches_a_predicate()
    {
        Assert.Equal(
            [$"{Shared}|2"],
            await RowsAsync(
                $"SELECT first_name, COUNT(*) FROM members WHERE first_name = '{Shared}' "
                + "GROUP BY first_name"));

        // And the predicate really is on the raw value: the masked one would have compared 'T'.
        Assert.Empty(
            await RowsAsync(
                "SELECT first_name, COUNT(*) FROM members WHERE first_name = 'T' "
                + "GROUP BY first_name"));
    }

    // ---------------------------------------------------------------- the negatives

    /// <summary>
    /// Pinning an individual: an aggregate over one person is that person, and the difference of
    /// two such statements is their value.
    /// </summary>
    [Fact]
    public async Task A_predicate_on_a_unique_key_is_refused()
    {
        var refusal = await Assert.ThrowsAsync<EntitlementException>(
            async () => await RowsAsync(
                "SELECT first_name, COUNT(*) FROM members WHERE first_name LIKE 'T%' AND id = 1 "
                + "GROUP BY first_name"));

        Assert.Contains("main.members.id", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("query-set-size control", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>And excluding one, which the difference of two statements reads just as well.</summary>
    [Fact]
    public async Task A_negated_predicate_on_a_unique_key_is_refused()
    {
        var refusal = await Assert.ThrowsAsync<EntitlementException>(
            async () => await RowsAsync(
                "SELECT first_name, COUNT(*) FROM members WHERE id <> 1 GROUP BY first_name"));

        Assert.Contains("main.members.id", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>Grouping by a unique key pins every individual at once (§8's second negative).</summary>
    [Fact]
    public async Task Grouping_by_a_unique_key_is_refused()
    {
        var refusal = await Assert.ThrowsAsync<EntitlementException>(
            async () => await RowsAsync("SELECT id, COUNT(*) FROM members GROUP BY id"));

        Assert.Contains("main.members.id", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("groups by it", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A row-level column beside the predicate: the statement is not one the opt-in applies to, so
    /// the column keeps its mask and the predicate compares that. Not a refusal — the honest answer,
    /// and the one §3.1 gives for every masked column.
    /// </summary>
    [Fact]
    public async Task A_row_level_statement_compares_the_mask_and_not_the_raw_value()
    {
        Assert.Empty(
            await RowsAsync($"SELECT id FROM members WHERE first_name = '{Shared}' ORDER BY id"));

        Assert.Equal(
            ["1", "3", "7"],
            await RowsAsync("SELECT id FROM members WHERE first_name = 'T' ORDER BY id"));
    }

    /// <summary>
    /// A window that does not read the statistical column: the statement is not an aggregate-only
    /// one, so the column keeps its mask, and the window is an ordinary window over it.
    /// </summary>
    [Fact]
    public async Task A_window_statement_compares_the_mask_and_not_the_raw_value()
    {
        Assert.Equal(
            ["T|5", "B|5", "T|5", "A|5", "T|5"],
            await RowsAsync("SELECT first_name, COUNT(*) OVER () FROM members ORDER BY id"));
    }

    /// <summary>
    /// A window that reads the statistical column <em>is</em> refused, naming it (D203, F43). A
    /// partition can be one row, so the group suppression the opt-in trades the mask for cannot be
    /// enforced on it; the column would otherwise keep its mask, which is safe and silent where the
    /// design says refuse.
    /// </summary>
    [Fact]
    public async Task A_window_over_the_statistical_column_is_refused()
    {
        var refusal = await Assert.ThrowsAsync<EntitlementException>(
            async () => await RowsAsync(
                "SELECT first_name, COUNT(*) OVER (PARTITION BY first_name) FROM members"));

        Assert.Equal(
            "Refused by the entitlements: main.members.first_name is a statistical column and this "
            + "statement reads it under a window function. Statistical access is query-set-size "
            + "control, and a window's partition can be one row, so no suppression can be enforced "
            + "on it: a window is one of the three things the opt-in forbids "
            + "(docs/design/16-entitlements.md §3.4, D203).",
            refusal.Message);
    }

    /// <summary>
    /// And the plan says what it did: the read's own verdict for a statistical column is
    /// <c>AGGREGATE</c> — raw, and permitted only where the floor guards it.
    /// </summary>
    [Fact]
    public async Task The_read_reports_a_statistical_column_as_aggregate()
    {
        await using var engine = await EngineAsync();
        var prepared = await engine.WithEntitlements().PrepareAsync(
            "SELECT first_name, COUNT(*) FROM members GROUP BY first_name", Agent);

        var read = Reads(prepared.Plan).Single(r => r.Table.Table == "members");
        Assert.Equal(
            Chalk.Ir.DisclosureOutcome.Aggregate,
            read.Disclosures.Single(d => d.Column == 2).Outcome);
        Assert.Equal(ReportedDisclosure.Aggregate, prepared.Columns[0].Disclosure);
    }

    // ---------------------------------------------------------------- helpers

    private static IEnumerable<Chalk.Ir.Read> Reads(Chalk.Ir.Plan plan)
    {
        var found = new List<Chalk.Ir.Read>();
        Collect(plan.Root, found);
        return found;
    }

    private static void Collect(Chalk.Ir.Rel? rel, List<Chalk.Ir.Read> into)
    {
        if (rel is null)
        {
            return;
        }

        if (rel.KindCase == Chalk.Ir.Rel.KindOneofCase.Read)
        {
            into.Add(rel.Read);
        }

        foreach (var input in Chalk.Ir.PlanWalker.Inputs(rel))
        {
            Collect(input, into);
        }
    }

    private async Task<ChalkEngine> EngineAsync() =>
        await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = TenancyFixture.ContextId,
            Sources = [Fixture.Source],
            Planner = sidecar.CreatePlanner(),
            Functions = TenancyAdoFixture.RegisterFunctions,
        });

    private async Task<string[]> RowsAsync(string sql)
    {
        await using var engine = await EngineAsync();
        var prepared = await engine.WithEntitlements().PrepareAsync(sql, Agent);
        var rows = new List<string>();
        await using var execution =
            await engine.ExecuteAsync(prepared, (IReadOnlyList<object?>?)null);
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

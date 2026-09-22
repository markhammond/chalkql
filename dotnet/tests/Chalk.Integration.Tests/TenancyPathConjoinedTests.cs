using Chalk.Client;
using Chalk.Entitlements;
using Chalk.Entitlements.Tenancy;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// A conjoined confinement across an inherited path (design 47, D279): a grant for one supplier,
/// only in one warehouse, over a table that holds the warehouse on its own row and reaches the
/// supplier along a path.
/// </summary>
/// <remarks>
/// <para>
/// The claim is the one the design opens with. The reviewer for supplier A confined to Singapore
/// sees A's stock in Singapore; not A's stock in Tokyo, and not B's stock in Singapore. Both halves
/// of that sentence are a canary: each is a row one half of the conjunction admits and the other
/// refuses, so a confinement that lost either half would show up here as a row too many.
/// </para>
/// <para>
/// Every principal is read in <b>both binding modes</b> — the values folded at prepare time, and
/// the lists bound at execution — because the term is one membership over two columns and the two
/// modes spell it differently: literal tuples where it folds, a two-column key set where it does
/// not. The answers must not differ.
/// </para>
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class TenancyPathConjoinedTests(SharedSidecar sidecar)
{
    private static readonly TenancyPathConjoinedFixture Fixture = TenancyPathConjoinedFixture.Shared;

    /// <summary>
    /// What each principal may see of <c>positions</c>, by identifier. The first row of each pair
    /// is the design's own sentence written as data.
    /// </summary>
    public static TheoryData<string, string[]> Rows() =>
        new()
        {
            // A in Singapore: position 2 is A in Tokyo and 3 is B in Singapore, and neither is here.
            { "a-reviewer-in-sin", ["1", "5"] },
            // The same grant unconfined reaches A's Tokyo stock as well.
            { "a-reviewer", ["1", "2", "5"] },
            // B in Singapore reaches B's Singapore stock and nothing of A's.
            { "b-reviewer-in-sin", ["3"] },
            // The warehouse perspective is unconfined and reaches every supplier in Singapore.
            { "sin-keeper", ["1", "3", "5"] },
            { "finance", ["1", "2", "3", "4", "5"] },
        };

    /// <summary>And the same question one step further out, along the two-step path.</summary>
    public static TheoryData<string, string[]> MovementRows() =>
        new()
        {
            { "a-reviewer-in-sin", ["1", "5"] },
            { "a-reviewer", ["1", "2", "5"] },
            { "b-reviewer-in-sin", ["3"] },
            { "sin-keeper", ["1", "3", "5"] },
            { "finance", ["1", "2", "3", "4", "5"] },
        };

    [Theory]
    [MemberData(nameof(Rows))]
    public async Task A_confined_grant_reaches_the_rows_both_halves_admit(
        string principal, string[] expected)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        await using var engine = await EngineAsync();
        Assert.Equal(
            expected,
            await BothModesAsync(engine, "SELECT id FROM positions ORDER BY id", Context(principal)));
    }

    [Theory]
    [MemberData(nameof(MovementRows))]
    public async Task The_two_step_path_answers_the_same_conjunction(
        string principal, string[] expected)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        await using var engine = await EngineAsync();
        Assert.Equal(
            expected,
            await BothModesAsync(engine, "SELECT id FROM movements ORDER BY id", Context(principal)));
    }

    /// <summary>
    /// The canaries, stated as the design states them: A's Tokyo stock never reaches the Singapore
    /// reviewer, and B's Singapore stock never does either — in any statement, and whatever the
    /// binding mode. The detector reads everything the principal was handed.
    /// </summary>
    [Theory]
    [InlineData("SELECT id, quantity, cost FROM positions ORDER BY id")]
    [InlineData("SELECT p.id, w.city FROM positions p JOIN warehouses w ON w.code = p.warehouse_code"
        + " ORDER BY p.id")]
    [InlineData("SELECT warehouse_code, SUM(quantity) FROM positions GROUP BY warehouse_code"
        + " ORDER BY warehouse_code")]
    public async Task Neither_half_of_the_conjunction_leaks_on_its_own(string sql)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        await using var engine = await EngineAsync();
        foreach (var (name, _) in TenancyPathConjoinedFixture.Principals)
        {
            var context = Context(name);
            var detector = LeakDetector.For(name, context);
            var prepared = await engine.WithEntitlements().PrepareAsync(sql, context);
            detector.Inspect(new LeakScan
            {
                Statement = $"{sql} (as {name})",
                Rows = await BothModesAsync(engine, sql, context),
                Columns = [.. prepared.Columns.Select(c => c.Name)],
                Report = string.Join(
                    ", ",
                    prepared.Columns.Select(c => $"{c.Name}:{c.Disclosure}")
                        .Concat(prepared.Entitlements.Tables.Select(
                            t => $"{t.Table}:{t.Visibility}"))),
            });
        }
    }

    /// <summary>
    /// A column disclosed to the supplier's own people is decided by the same conjunction the row
    /// is: the reviewer confined to A-in-Singapore reads the cost of the rows that conjunction
    /// admits, and the warehouse's own keeper — who sees the rows — does not read it at all.
    /// </summary>
    [Fact]
    public async Task The_column_rule_of_the_perspective_is_decided_the_same_way()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        await using var engine = await EngineAsync();

        var reviewer = await engine.WithEntitlements()
            .PrepareAsync("SELECT id, cost FROM positions ORDER BY id", Context("a-reviewer-in-sin"));
        Assert.Equal(["1|1000", "5|5000"], await RowsAsync(engine, reviewer));

        var keeper = await engine.WithEntitlements()
            .PrepareAsync("SELECT id, cost FROM positions ORDER BY id", Context("sin-keeper"));
        Assert.Equal(
            ReportedDisclosure.Redacted,
            Assert.Single(keeper.Columns, c => c.Name == "cost").Disclosure);
    }

    /// <summary>
    /// The explain names the term: the path carries a path predicate, and the chain is not elided
    /// while it stands, because the marker reads the endpoint's column through those joins.
    /// </summary>
    [Fact]
    public async Task The_explain_shows_the_path_predicate_and_keeps_the_chain()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        await using var engine = await EngineAsync();
        var prepared = await engine.WithEntitlements()
            .PrepareAsync("SELECT id FROM positions ORDER BY id", Context("a-reviewer-in-sin"));

        var explained = await prepared.ExplainAsync();
        var positions = Assert.Single(explained.Tables, t => t.Table == "positions");
        var path = Assert.Single(positions.Paths);
        Assert.Equal("supplier", path.Kind);
        Assert.Equal("products", path.EndpointTable);
        Assert.False(path.Elided);
        Assert.False(path.Dropped);
        Assert.NotEqual("", path.PathPredicate);
    }

    /// <summary>
    /// And the package's own prediction agrees with what was executed, row and column, for every
    /// principal — which is what says the verdict side reads the cross-row group too.
    /// </summary>
    [Fact]
    public async Task The_reconciler_predicts_every_row_and_column_as_executed()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        await using var engine = await EngineAsync();
        foreach (var (name, principal) in TenancyPathConjoinedFixture.Principals)
        {
            var prepared = await engine.WithEntitlements().PrepareAsync(
                "SELECT id, quantity, cost FROM positions ORDER BY id", Context(name));
            var reconciled = Fixture.Entitlements.Reconcile(prepared, principal);

            Assert.Empty(reconciled.Differences);
            Assert.DoesNotContain(
                reconciled.Visibility, v => v.Verdict == ReconciledVisibility.Disagrees);
            Assert.True(reconciled.Agrees);
        }
    }

    /// <summary>
    /// The family's own corpus, recorded: every statement as every principal, with the per-column
    /// report beside the rows, so a change in what a principal is told is a diff a reviewer reads.
    /// </summary>
    /// <remarks>
    /// Regenerate with <c>CHALK_WRITE_FIXTURES=1</c> and read the diff. The recording is written by
    /// this suite rather than by the plan recorder, which records the unentitled families alone.
    /// </remarks>
    public static TheoryData<string> Queries()
    {
        var data = new TheoryData<string>();
        foreach (var query in CorpusQueries.LoadM7TenancyPathConjoined())
        {
            data.Add(query.Name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Queries))]
    public async Task Every_principal_reads_what_the_recording_says(string name)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        var query = CorpusQueries.LoadM7TenancyPathConjoined().Single(q => q.Name == name);
        var parameters = CorpusQueries.Parameters(name);
        await using var engine = await EngineAsync();

        var recorded = new System.Text.StringBuilder();
        foreach (var (principal, _) in TenancyPathConjoinedFixture.Principals)
        {
            var context = Context(principal);
            var prepared = await engine.WithEntitlements().PrepareAsync(query.Sql, context);
            recorded.Append("-- ").Append(principal).AppendLine();
            recorded.Append("report ").AppendLine(
                string.Join(
                    ", ",
                    prepared.Columns.Select(c => $"{c.Name}:{c.Disclosure}")
                        .Concat(prepared.Entitlements.Tables
                            .OrderBy(t => t.Table, StringComparer.Ordinal)
                            .Select(t => $"{t.Table}:{t.Visibility}"))));
            foreach (var row in await RowsAsync(engine, prepared, parameters: parameters))
            {
                recorded.AppendLine(row);
            }

            recorded.AppendLine();
        }

        var golden = new FileInfo(
            Path.Combine(
                RepoLayout.Corpus.FullName, "plans", "m7-tenancy-path-conjoined", name + ".txt"));
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

    // ---------------------------------------------------------------- helpers

    /// <summary>The principal's context, bound at prepare time.</summary>
    private static RequestContext Context(string name) =>
        Fixture.Entitlements.Bind(
            TenancyPathConjoinedFixture.Principals.Single(p => p.Name == name).Principal);

    private async Task<ChalkEngine> EngineAsync() =>
        await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = TenancyFixture.ContextId,
            Sources = [Fixture.Source],
            Planner = sidecar.CreatePlanner(),
        });

    private static async Task<string[]> RowsAsync(
        ChalkEngine engine,
        PreparedQuery prepared,
        RequestContext? executeWith = null,
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

    /// <summary>
    /// One statement read in <b>both binding modes</b> (D209): the lists folded into the plan, and
    /// the same lists left open for the executor to bind, where a two-column membership travels as
    /// a key set. The two must answer alike.
    /// </summary>
    private static async Task<string[]> BothModesAsync(
        ChalkEngine engine, string sql, RequestContext context)
    {
        var folded = await engine.WithEntitlements().PrepareAsync(sql, context);
        var rows = await RowsAsync(engine, folded);

        var shared = await engine.WithEntitlements().PrepareAsync(sql, context.Shape());
        Assert.Equal(rows, await RowsAsync(engine, shared, executeWith: context));
        return rows;
    }
}

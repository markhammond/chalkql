using Chalk.Client;
using Chalk.Entitlements;
using Chalk.Ir;
using Chalk.Sources;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// Design 38 §8's two-source layout over a <b>database</b>, which is the tutorial's own (design 39
/// §4): the target and the bridge co-located in DuckDB, the endpoint and the table its kind is held
/// on in process, and the step between them declared by an association (F84).
/// </summary>
/// <remarks>
/// <para>
/// The POCO pair of <see cref="TenancySplitCorpusTests"/> proves the answers; this is where the
/// three things that layout may move are read, because only a real source has them: the SQL each
/// source is <b>sent</b>, <c>row_predicate_pushed</c>, and which source each hop of the path ran in.
/// D252's detector reads the SQL sent to each source, not only the first.
/// </para>
/// <para>
/// The rows are the rows the same principal gets with all three tables in one database, which is
/// what says a source boundary changes nothing about the policy —
/// <c>TenancyRemoteBindingTests.The_co_located_chain_answers_what_the_same_principal_sees_in_process</c>
/// is the other end of the same claim.
/// </para>
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class TwoSourcePathTests(SharedSidecar sidecar)
{
    /// <summary>The statements whose answer the path decides, over both layouts.</summary>
    public static TheoryData<string> Statements() =>
    [
        "SELECT id FROM orders ORDER BY id",
        "SELECT COUNT(*) FROM orders",
        "SELECT o.id, i.id, i.quantity FROM orders o JOIN order_items i ON i.order_id = o.id"
            + " ORDER BY o.id, i.id",
    ];

    /// <summary>
    /// §8's touchstone: the vendor sees orders 1, 5 and 7 — order 7 in an organisation no other
    /// grant of the fixture reaches — and the report says SOME, never ALL, because existence is per
    /// row (§6).
    /// </summary>
    [Fact]
    public async Task A_vendor_reads_the_orders_the_path_reaches_across_the_two_sources()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        using var split = TenancyAdoFixture.CreateDuckDb(
            marketplace: MarketplacePlacement.EndpointInProcess);
        await using var engine = await EngineAsync(split);
        var prepared = await engine.WithEntitlements().PrepareAsync(
            "SELECT id FROM orders ORDER BY id", TenancyFixture.U14);

        var orders = Assert.Single(prepared.Entitlements.Tables, t => t.Table == "orders");
        Assert.Equal(TableVisibility.Some, orders.Visibility);
        Assert.Equal(["1", "5", "7"], await RowsAsync(engine, prepared.Query));
    }

    /// <summary>
    /// And every principal's answer is the co-located database layout's, statement for statement.
    /// The detector reads everything each principal received, the SQL sent to <b>each</b> source
    /// among it.
    /// </summary>
    [Theory]
    [MemberData(nameof(Statements))]
    public async Task Every_principal_reads_what_the_co_located_database_layout_showed_them(
        string sql)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        using var together = TenancyAdoFixture.CreateDuckDb();
        using var split = TenancyAdoFixture.CreateDuckDb(
            marketplace: MarketplacePlacement.EndpointInProcess);
        await using var one = await EngineAsync(together, associated: false);
        await using var two = await EngineAsync(split);

        foreach (var (principal, context) in TenancyFixture.Principals)
        {
            var detector = LeakDetector.For(principal, context);
            var expected = await RowsAsync(one, (await one.WithEntitlements()
                .PrepareAsync(sql, context)).Query);

            var prepared = await two.WithEntitlements().PrepareAsync(sql, context);
            var rows = await RowsAsync(two, prepared.Query);
            detector.Inspect(new LeakScan
            {
                Statement = $"{sql} (two sources, as {principal})",
                Rows = rows,
                Columns = [.. prepared.Columns.Select(c => c.Name)],
                Report = string.Join(
                    ", ",
                    prepared.Columns.Select(c => $"{c.Name}:{c.Disclosure}")
                        .Concat(prepared.Entitlements.Tables.Select(t => $"{t.Table}:{t.Visibility}"))),
                RemoteSql = RemoteSql(prepared.Plan),
            });

            Assert.Equal(expected, rows);
        }
    }

    /// <summary>
    /// What each source is sent. The bridge's source receives the key set the endpoint's visible
    /// keys travelled as — the first of §5's two hops — and the endpoint's own table is nowhere in
    /// that text, because it is not in that source to be joined to.
    /// </summary>
    [Fact]
    public async Task The_endpoints_keys_reach_the_bridges_source_as_a_key_set()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        using var split = TenancyAdoFixture.CreateDuckDb(
            marketplace: MarketplacePlacement.EndpointInProcess);
        await using var engine = await EngineAsync(split);
        var prepared = await engine.WithEntitlements().PrepareAsync(
            "SELECT id FROM orders ORDER BY id", TenancyFixture.U14);

        var sent = RemoteSql(prepared.Plan);
        Assert.NotEmpty(sent);
        var text = string.Join("\n", sent);

        // Only one source takes a query at all here, and what it is asked for is its own two tables.
        Assert.Contains("\"order_items\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"items\"", text.Replace("\"order_items\"", "", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.DoesNotContain("\"vendors\"", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The report names the source each hop ran in, and says honestly whether the whole marker went
    /// with the remote text (D229): here it did not, because half the chain is in the other source.
    /// </summary>
    [Fact]
    public async Task The_report_names_each_hops_source_and_is_honest_about_the_pushdown()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        using var split = TenancyAdoFixture.CreateDuckDb(
            marketplace: MarketplacePlacement.EndpointInProcess);
        await using var engine = await EngineAsync(split);
        var prepared = await engine.WithEntitlements().PrepareAsync(
            "SELECT id FROM orders ORDER BY id", TenancyFixture.U14);

        var explained = await prepared.ExplainAsync();
        var orders = Assert.Single(explained.Tables, t => t.Table == "orders");
        var path = Assert.Single(orders.Paths);
        Assert.Equal("vendor", path.Kind);
        Assert.Equal(TenancyAdoFixture.LocalSchema, path.EndpointSchema);
        Assert.Equal("items", path.EndpointTable);
        Assert.Collection(
            path.Steps,
            up =>
            {
                Assert.Equal(TenancyAdoFixture.SourceId, up.Schema);
                Assert.Equal("order_items", up.Table);
            },
            down =>
            {
                Assert.Equal(TenancyAdoFixture.LocalSchema, down.Schema);
                Assert.Equal("items", down.Table);
            });
        Assert.False(
            Assert.Single(prepared.Entitlements.Tables, t => t.Table == "orders")
                .RowPredicatePushed);
    }

    /// <summary>
    /// The chain's reads are the mechanism's own, in both sources, and the client's invariant holds
    /// each of them as disclosing nothing and unreachable from the statement (D265 §7).
    /// </summary>
    [Fact]
    public async Task The_chains_reads_are_marked_as_correlations_in_both_sources()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        using var split = TenancyAdoFixture.CreateDuckDb(
            marketplace: MarketplacePlacement.EndpointInProcess);
        await using var engine = await EngineAsync(split);
        var prepared = await engine.WithEntitlements().PrepareAsync(
            "SELECT id FROM orders ORDER BY id", TenancyFixture.U14);

        var correlations = new HashSet<string>(StringComparer.Ordinal);
        Collect(prepared.Plan.Root, correlations);

        Assert.Contains(TenancyAdoFixture.LocalSchema + ".items", correlations);
        Assert.Contains(TenancyAdoFixture.SourceId + ".order_items", correlations);

        // The bridge's read is inside the pushed subtree, which is where a read that went to a
        // source lives; the walk has to go in after it, exactly as the client's own invariant does.
        static void Collect(Rel rel, HashSet<string> into)
        {
            if (rel.KindCase == Rel.KindOneofCase.Read && rel.Read.Correlation)
            {
                into.Add($"{rel.Read.Table.Schema}.{rel.Read.Table.Table}");
            }

            if (rel.KindCase == Rel.KindOneofCase.RemoteQuery && rel.RemoteQuery.PushedPlan is { } pushed)
            {
                Collect(pushed, into);
            }

            foreach (var input in PlanWalker.Inputs(rel))
            {
                Collect(input, into);
            }
        }
    }

    // ---------------------------------------------------------------- the engine

    /// <summary>
    /// The engine over one of the two layouts. The association belongs to the split one alone: it is
    /// a claim about a table the co-located catalog does not hold, and a catalog carrying an
    /// association whose far end is not in it is refused by name.
    /// </summary>
    private async ValueTask<ChalkEngine> EngineAsync(
        TenancyAdoFixture fixture, bool associated = true) =>
        await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = TenancyFixture.ContextId,
            Sources = fixture.Sources,
            Associations = associated ? TenancyAdoFixture.MarketplaceAssociation : [],
            Planner = sidecar.CreatePlanner(),
            Functions = TenancyAdoFixture.RegisterFunctions,
        });

    private static IReadOnlyList<string> RemoteSql(Plan plan)
    {
        var texts = new List<string>();
        foreach (var rel in PlanWalker.Rels(plan.Root))
        {
            if (rel.KindCase == Rel.KindOneofCase.RemoteQuery)
            {
                texts.Add(rel.RemoteQuery.QueryText);
            }
        }

        return texts;
    }

    private static async Task<string[]> RowsAsync(ChalkEngine engine, PreparedQuery prepared)
    {
        var rows = new List<string>();
        await using var execution = await engine.ExecuteAsync(prepared);
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

using Chalk.Client;
using Chalk.Ir;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// Corpus 14: the second flavour of the pushed-subtree boundary (D84). A source whose query
/// language is the IR gets <c>pushed_plan</c> and an empty <c>query_text</c>, and answers the same
/// rows as every other copy of the data.
/// </summary>
/// <remarks>
/// The whole point of D84 carrying both fields is that neither kind of source has to pretend to be
/// the other. A SQL source reads the text; this one reads the plan and would throw if handed text.
/// The planner does not know or care which it is talking to — it fills in both and lets the
/// descriptor's <c>query_language</c> decide what is populated.
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class IrLanguageSourceTests(SharedSidecar sidecar)
{
    /// <summary>The IR source over the POCO fixture's <c>lineitem</c> and <c>orders</c>.</summary>
    private static IrPlanSource Source() =>
        new("ir", CorpusFixture.Shared.Source, "lineitem", "orders");

    [Fact]
    public async Task A_pushed_subtree_carries_the_plan_and_no_query_text()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        var source = Source();
        await using var engine = await EngineAsync(source);
        var prepared = await engine.PrepareAsync(
            "SELECT l_orderkey, l_quantity FROM ir.lineitem WHERE l_orderkey > 100 AND l_quantity < 5");

        var remote = Remotes(prepared.Plan);
        var one = Assert.Single(remote);

        // D84: an IR source gets the subtree and nothing else.
        Assert.Equal(string.Empty, one.QueryText);
        Assert.NotNull(one.PushedPlan);
        Assert.NotEqual(Rel.KindOneofCase.None, one.PushedPlan.KindCase);
    }

    /// <summary>
    /// The answers match the reference executor over the same POCO rows, which is the only claim
    /// that matters: the subtree the planner sent means what the planner thought it meant.
    /// </summary>
    [Theory]
    [InlineData("SELECT l_orderkey, l_quantity FROM ir.lineitem WHERE l_orderkey > 100 AND l_quantity < 5")]
    [InlineData("SELECT l_orderkey FROM ir.lineitem WHERE l_shipdate >= DATE '1995-01-01'")]
    [InlineData("SELECT l_orderkey, l_extendedprice FROM ir.lineitem WHERE l_discount = 0.05")]
    [InlineData("SELECT o_orderkey FROM ir.orders WHERE o_totalprice > 100000.0")]
    [InlineData("SELECT l_orderkey FROM ir.lineitem WHERE l_comment > 'z'")]
    public async Task The_ir_source_gives_the_reference_answer(string sql)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        var pushed = Source();
        await using var engine = await EngineAsync(pushed);
        await using var reference = await EngineAsync(Source(), ExecutionEngine.Reference);

        var actualPrepared = await engine.PrepareAsync(sql);
        var expectedPrepared = await reference.PrepareAsync(
            sql, new PrepareOptions { Pushdown = PushdownLevel.None });

        var actual = await ReadAsync(engine, actualPrepared);
        var expected = await ReadAsync(reference, expectedPrepared);

        try
        {
            Assert.True(pushed.QueriesRun > 0, "nothing was pushed into the IR source");
            ResultComparer.AssertEquivalent(expected, actual, ResultComparisonOptions.Multiset);
        }
        finally
        {
            foreach (var batch in actual.Concat(expected))
            {
                batch.Dispose();
            }
        }
    }

    /// <summary>
    /// Nothing undeclared is pushed (§1). The IR source declares filters, projection and limits and
    /// no aggregate, so the aggregate stays local — and if it ever did not, the source's
    /// interpreter would throw rather than answer.
    /// </summary>
    [Fact]
    public async Task An_operator_the_ir_source_never_declared_stays_local()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        var source = Source();
        await using var engine = await EngineAsync(source);
        var prepared = await engine.PrepareAsync(
            "SELECT l_returnflag, SUM(l_quantity) FROM ir.lineitem GROUP BY l_returnflag");

        foreach (var remote in Remotes(prepared.Plan))
        {
            Assert.DoesNotContain(
                PlanWalker.Rels(remote.PushedPlan),
                r => r.KindCase == Rel.KindOneofCase.Aggregate);
        }

        // And it runs: the local aggregate over the pushed read gives an answer.
        var rows = await ReadAsync(engine, prepared);
        try
        {
            Assert.True(rows.Sum(b => b.Length) > 0);
        }
        finally
        {
            foreach (var batch in rows)
            {
                batch.Dispose();
            }
        }
    }

    private static IReadOnlyList<RemoteQuery> Remotes(Plan plan) =>
        [.. PlanWalker.Rels(plan.Root)
            .Where(r => r.KindCase == Rel.KindOneofCase.RemoteQuery)
            .Select(r => r.RemoteQuery)];

    private async ValueTask<ChalkEngine> EngineAsync(
        IrPlanSource source, ExecutionEngine engine = ExecutionEngine.Vectorised) =>
        await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "ir-corpus",
            Functions = CorpusFunctions.Register,
            Sources = [CorpusFixture.Shared.Source, source],
            Planner = sidecar.CreatePlanner(),
            Execution = new ExecutionOptions { Engine = engine, BatchSize = 4096 },
        });

    private static async Task<List<Apache.Arrow.RecordBatch>> ReadAsync(
        ChalkEngine engine, PreparedQuery query)
    {
        var batches = new List<Apache.Arrow.RecordBatch>();
        await using var execution = await engine.ExecuteAsync(query, (IReadOnlyList<object?>?)null);
        await foreach (var batch in execution.Batches)
        {
            batches.Add(batch);
        }

        return batches;
    }
}

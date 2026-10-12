using Chalk.Execution.Tests.Harness;
using Chalk.Ir;
using Chalk.Sources;
using Chalk.TestKit;

namespace Chalk.Execution.Tests;

/// <summary>
/// A subtree the plan shares (D338, design 63 §2): computed once for an execution at its first use,
/// and its rows given to every use, on both engines and at every batch size. The next execution
/// computes it again.
/// </summary>
public sealed class SharedSubtreeOperatorTests
{
    private static readonly TestTable Trades = TestData.Trades;

    public static TheoryData<int> BatchSizes() => TestData.BatchSizeData;

    /// <summary>A count per symbol, shared, and a union that reads it twice.</summary>
    private static Plan TwoUses()
    {
        var row = Trades.RowType();
        var aggregate = IrBuilder.HashAggregate(
            IrBuilder.Read(Trades.Name, row),
            [0],
            [("n", IrBuilder.Agg(AggregateFunctionId.Count, IrBuilder.I64()))]);
        Rel Use() => new() { RowType = aggregate.RowType.Clone(), SharedRef = new SharedRef { Id = 1 } };
        var union = new SetOp { Kind = SetOpKind.UnionAll };
        union.Inputs.AddRange([Use(), Use()]);
        var plan = IrBuilder.Plan(new Rel { RowType = aggregate.RowType.Clone(), SetOp = union });
        plan.Shared.Add(new SharedPlan { Id = 1, Rel = aggregate });
        plan.PlanDigest = 0;
        plan.PlanDigest = PlanDigest.Compute(plan);
        return plan;
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Two_uses_read_the_source_once(int batchSize)
    {
        var source = TestData.Source(Trades);

        var rows = await Runner.RowsAsync(TwoUses(), source, batchSize);

        Assert.Equal(6, rows.Count);                       // AAA, BBB and the NULL group, twice
        Assert.Equal(1, source.Scans);
        Assert.Equal(
            rows.Take(3).Select(r => (r[0], r[1])),
            rows.Skip(3).Select(r => (r[0], r[1])));
    }

    [Fact]
    public async Task Both_engines_agree() =>
        await Runner.AssertEnginesAgreeAsync(TwoUses(), TestData.Source(Trades), ResultComparisonOptions.Multiset);

    /// <summary>A warm plan run twice computes the shared rows for each execution, once.</summary>
    [Fact]
    public async Task Each_execution_computes_it_once()
    {
        var source = TestData.Source(Trades);
        var compiled = Runner.Compile(TwoUses(), source);

        var first = await Runner.CollectAsync(compiled);
        var second = await Runner.CollectAsync(compiled);

        Assert.Equal(ResultComparer.Rows(first).Count, ResultComparer.Rows(second).Count);
        Assert.Equal(2, source.Scans);
        foreach (var batch in first.Concat(second))
        {
            batch.Dispose();
        }
    }
}

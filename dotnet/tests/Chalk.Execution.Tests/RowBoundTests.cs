using Chalk.Execution.Tests.Harness;
using Chalk.Ir;
using Chalk.Sources;
using Chalk.TestKit;
using IrType = Chalk.Ir.Type;

namespace Chalk.Execution.Tests;

/// <summary>
/// D285 — a <c>LIMIT</c> or <c>OFFSET</c> bound the plan carries as a parameter, read when the
/// execution starts.
/// </summary>
/// <remarks>
/// Each case runs through the vectorised engine and the reference executor, because the two refuse
/// and honour the same executions or one of them is wrong.
/// </remarks>
public sealed class RowBoundTests
{
    private static readonly TestTable Series = TestData.Series(20);
    private static readonly TestSource Source = TestData.Source(Series);

    public static TheoryData<int> BatchSizes() => TestData.BatchSizeData;

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task A_parameterised_limit_takes_the_bound_value(int batchSize)
    {
        var plan = Plan(offsetParam: null, countParam: 0);

        foreach (var reference in new[] { false, true })
        {
            var rows = await Runner.RowsAsync(plan, Source, batchSize, reference, [5L]);

            Assert.Equal(5, rows.Count);
            Assert.Equal(0L, rows[0][0]);
            Assert.Equal(4L, rows[^1][0]);
        }
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task A_parameterised_offset_and_limit_take_theirs(int batchSize)
    {
        var plan = Plan(offsetParam: 1, countParam: 0);

        foreach (var reference in new[] { false, true })
        {
            var rows = await Runner.RowsAsync(plan, Source, batchSize, reference, [4L, 3L]);

            Assert.Equal(4, rows.Count);
            Assert.Equal(3L, rows[0][0]);
            Assert.Equal(6L, rows[^1][0]);
        }
    }

    /// <summary>The same plan run twice: the bound is this execution's, not the compilation's.</summary>
    [Fact]
    public async Task The_bound_is_read_again_for_every_execution()
    {
        var plan = Plan(offsetParam: null, countParam: 0);

        Assert.Equal(2, (await Runner.RowsAsync(plan, Source, 7, parameters: [2L])).Count);
        Assert.Equal(9, (await Runner.RowsAsync(plan, Source, 7, parameters: [9L])).Count);
    }

    [Fact]
    public async Task A_parameterised_top_n_takes_the_bound_value()
    {
        var row = Series.RowType();
        var read = IrBuilder.Read(Series.Name, row);
        var plan = IrBuilder.Plan(
            IrBuilder.TopNParam(read, null, 0, offset: 0, count: 0, IrBuilder.Desc(0, row.Fields[0].Type)),
            parameterTypes: IrBuilder.I64());

        foreach (var reference in new[] { false, true })
        {
            var rows = await Runner.RowsAsync(plan, Source, 7, reference, [3L]);

            Assert.Equal([19L, 18L, 17L], rows.Select(r => r[0]));
        }
    }

    /// <summary>A bound is a count. A negative one is refused before any row moves.</summary>
    [Fact]
    public async Task A_negative_bound_is_refused_by_name()
    {
        var plan = Plan(offsetParam: null, countParam: 0);

        foreach (var reference in new[] { false, true })
        {
            var failure = await Assert.ThrowsAsync<ExecutionException>(
                () => Runner.RowsAsync(plan, Source, 7, reference, [-3L]));

            Assert.Contains("-3 was bound to the LIMIT bound ?0", failure.Message, StringComparison.Ordinal);
            Assert.Contains("must be zero or more", failure.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_negative_offset_is_refused_by_name()
    {
        var plan = Plan(offsetParam: 0, countParam: null, count: 5);

        var failure = await Assert.ThrowsAsync<ExecutionException>(
            () => Runner.RowsAsync(plan, Source, 7, parameters: [-1L]));

        Assert.Contains("-1 was bound to the OFFSET bound ?0", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>NULL is not a count either, and taking it for zero would look like an answer.</summary>
    [Fact]
    public async Task A_null_bound_is_refused_by_name()
    {
        var plan = Plan(offsetParam: null, countParam: 0);

        foreach (var reference in new[] { false, true })
        {
            var failure = await Assert.ThrowsAsync<ExecutionException>(
                () => Runner.RowsAsync(plan, Source, 7, reference, [null]));

            Assert.Contains("NULL was bound to the LIMIT bound ?0", failure.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A bound is a whole number of rows and never a share of them: Calcite's grammar has no
    /// <c>LIMIT 10%</c>, and a fractional value is refused rather than rounded into a count.
    /// </summary>
    [Fact]
    public async Task A_fractional_bound_is_refused_by_name()
    {
        var read = IrBuilder.Read(Series.Name, Series.RowType());
        var plan = IrBuilder.Plan(
            IrBuilder.FetchParam(read, null, 0),
            parameterTypes: IrBuilder.Dec(38, 2));

        var failure = await Assert.ThrowsAsync<ExecutionException>(
            () => Runner.RowsAsync(plan, Source, 7, parameters: [1.5m]));

        Assert.Contains("was bound to the LIMIT bound ?0", failure.Message, StringComparison.Ordinal);
        Assert.Contains("never a fraction or a share of them", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>A whole decimal is a count, which is what a bound parameter actually arrives as.</summary>
    [Fact]
    public async Task A_whole_decimal_bound_is_a_count()
    {
        var read = IrBuilder.Read(Series.Name, Series.RowType());
        var plan = IrBuilder.Plan(
            IrBuilder.FetchParam(read, null, 0),
            parameterTypes: IrBuilder.Dec(38, 0));

        Assert.Equal(3, (await Runner.RowsAsync(plan, Source, 7, parameters: [3m])).Count);
    }

    /// <summary>A plan that sets both a literal bound and a parameter for it is refused.</summary>
    [Fact]
    public void A_bound_written_twice_is_refused()
    {
        var read = IrBuilder.Read(Series.Name, Series.RowType());
        var rel = IrBuilder.FetchParam(read, null, 0);
        rel.Fetch.Count = 5;
        var plan = IrBuilder.Plan(rel, parameterTypes: IrBuilder.I64());

        var failure = Assert.Throws<InvalidPlanException>(() => PlanValidator.Validate(plan));

        Assert.Contains("Fetch.count and Fetch.count_param are both set", failure.Message, StringComparison.Ordinal);
    }

    private static Plan Plan(int? offsetParam, int? countParam, long? count = null)
    {
        var read = IrBuilder.Read(Series.Name, Series.RowType());
        var parameters = (offsetParam is null ? 0 : 1) + (countParam is null ? 0 : 1);
        var types = new IrType[parameters];
        for (var i = 0; i < parameters; i++)
        {
            types[i] = IrBuilder.I64(nullable: true);
        }

        return IrBuilder.Plan(
            IrBuilder.FetchParam(read, offsetParam, countParam, count: count),
            parameterTypes: types);
    }
}

using Chalk.Execution.Tests.Harness;
using Chalk.Ir;
using Chalk.TestKit;

namespace Chalk.Execution.Tests;

/// <summary>
/// The vectorised engine against the reference one over hand-built IR (invariant I4,
/// <c>05-testing.md</c> §5). Every operator and every kernel appears at least once, at each of the
/// three batch sizes. <c>Chalk.Integration.Tests</c> reuses the same <c>ResultComparer</c> over the
/// recorded-plan corpus.
/// </summary>
public sealed class DifferentialTests
{
    private static readonly TestTable Series = TestData.Series(37);
    private static readonly TestSource Source = TestData.Source(
        Series,
        TestData.Numbers,
        TestData.Strings,
        TestData.Instants,
        TestData.Logic,
        TestData.Sortable,
        TestData.Trades,
        TestData.Casts,
        TestData.Empty);

    public static TheoryData<int> BatchSizes() => TestData.BatchSizeData;

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Scan_and_project_agree(int batchSize)
    {
        var row = Series.RowType();
        var plan = IrBuilder.Plan(IrBuilder.Project(
            IrBuilder.Read(Series.Name, row),
            [
                ("n", IrBuilder.Ref(row, 0)),
                ("v", IrBuilder.Ref(row, 1)),
                ("label", IrBuilder.Ref(row, 2)),
            ]));

        await AssertAgreeAsync(plan, batchSize);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Filter_over_a_compound_predicate_agrees(int batchSize)
    {
        var row = TestData.Numbers.RowType();
        var read = IrBuilder.Read(TestData.Numbers.Name, row);
        var predicate = IrBuilder.Call(
            FunctionId.Or,
            IrBuilder.Bool(true),
            IrBuilder.Call(
                FunctionId.And,
                IrBuilder.Bool(true),
                IrBuilder.Call(FunctionId.Gt, IrBuilder.Bool(true), IrBuilder.Ref(row, 0), IrBuilder.Lit(1)),
                IrBuilder.Call(FunctionId.IsNotNull, IrBuilder.Bool(), IrBuilder.Ref(row, 5))),
            IrBuilder.In(IrBuilder.Ref(row, 5), IrBuilder.Bool(true), IrBuilder.Lit("zzz")));

        await AssertAgreeAsync(IrBuilder.Plan(IrBuilder.Filter(read, predicate)), batchSize);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Every_scalar_kernel_agrees_in_one_projection(int batchSize)
    {
        var row = TestData.Numbers.RowType();
        var read = IrBuilder.Read(TestData.Numbers.Name, row);
        var project = IrBuilder.Project(
            read,
            [
                ("add", IrBuilder.Call(
                    FunctionId.Add, IrBuilder.I64(true), IrBuilder.Ref(row, 1), IrBuilder.Lit(3L))),
                ("sub", IrBuilder.Call(
                    FunctionId.Subtract, IrBuilder.Fp64(true), IrBuilder.Ref(row, 2), IrBuilder.Lit(1d))),
                ("mul", IrBuilder.Call(
                    FunctionId.Multiply, IrBuilder.Dec(18, 4, true),
                    IrBuilder.Ref(row, 4), IrBuilder.LitDecimal(2m, 18, 4))),
                ("div", IrBuilder.Call(
                    FunctionId.Divide, IrBuilder.Fp32(true), IrBuilder.Ref(row, 3), IrBuilder.Lit(2f))),
                ("mod", IrBuilder.Call(
                    FunctionId.Modulus, IrBuilder.I32(true), IrBuilder.Ref(row, 0), IrBuilder.Lit(3))),
                ("neg", IrBuilder.Call(FunctionId.Negate, IrBuilder.I64(true), IrBuilder.Ref(row, 1))),
                ("abs", IrBuilder.Call(FunctionId.Abs, IrBuilder.Fp64(true), IrBuilder.Ref(row, 2))),
                ("floor", IrBuilder.Call(FunctionId.Floor, IrBuilder.Fp64(true), IrBuilder.Ref(row, 2))),
                ("ceil", IrBuilder.Call(FunctionId.Ceil, IrBuilder.Fp64(true), IrBuilder.Ref(row, 2))),
                ("round", IrBuilder.Call(FunctionId.Round, IrBuilder.Fp64(true), IrBuilder.Ref(row, 2))),
                ("eq", IrBuilder.Call(
                    FunctionId.Eq, IrBuilder.Bool(true), IrBuilder.Ref(row, 1), IrBuilder.Lit(30L))),
                ("ne", IrBuilder.Call(
                    FunctionId.Ne, IrBuilder.Bool(true), IrBuilder.Ref(row, 5), IrBuilder.Lit("alpha"))),
                ("lt", IrBuilder.Call(
                    FunctionId.Lt, IrBuilder.Bool(true), IrBuilder.Ref(row, 2), IrBuilder.Lit(0d))),
                ("ge", IrBuilder.Call(
                    FunctionId.Ge, IrBuilder.Bool(true), IrBuilder.Ref(row, 4),
                    IrBuilder.LitDecimal(0m, 18, 4))),
                ("distinct", IrBuilder.Call(
                    FunctionId.IsDistinctFrom, IrBuilder.Bool(),
                    IrBuilder.Ref(row, 2), IrBuilder.Lit(1.5d))),
                ("notdistinct", IrBuilder.Call(
                    FunctionId.IsNotDistinctFrom, IrBuilder.Bool(),
                    IrBuilder.Ref(row, 5), IrBuilder.Lit("beta"))),
                ("isnull", IrBuilder.Call(FunctionId.IsNull, IrBuilder.Bool(), IrBuilder.Ref(row, 5))),
                ("isnotfalse", IrBuilder.Call(
                    FunctionId.IsNotFalse, IrBuilder.Bool(), IrBuilder.Ref(row, 6))),
                ("not", IrBuilder.Call(FunctionId.Not, IrBuilder.Bool(true), IrBuilder.Ref(row, 6))),
                ("coalesce", IrBuilder.Call(
                    FunctionId.Coalesce, IrBuilder.I64(true), IrBuilder.Ref(row, 1), IrBuilder.Lit(-1L))),
                ("nullif", IrBuilder.Call(
                    FunctionId.Nullif, IrBuilder.I64(true), IrBuilder.Ref(row, 1), IrBuilder.Lit(30L))),
                ("case", IrBuilder.Case(
                    IrBuilder.Str(true),
                    IrBuilder.Lit("none"),
                    (IrBuilder.Ref(row, 6), IrBuilder.Lit("yes")))),
                ("inlist", IrBuilder.In(
                    IrBuilder.Ref(row, 0), IrBuilder.Bool(true), IrBuilder.Lit(1), IrBuilder.Lit(5))),
                ("cast", IrBuilder.Cast(IrBuilder.Ref(row, 1), IrBuilder.Str(true))),
                ("concat", IrBuilder.Call(
                    FunctionId.Concat, IrBuilder.Str(true), IrBuilder.Ref(row, 5), IrBuilder.Lit("!"))),
                ("upper", IrBuilder.Call(FunctionId.Upper, IrBuilder.Str(true), IrBuilder.Ref(row, 5))),
                ("lower", IrBuilder.Call(FunctionId.Lower, IrBuilder.Str(true), IrBuilder.Ref(row, 5))),
                ("len", IrBuilder.Call(
                    FunctionId.CharLength, IrBuilder.I32(true), IrBuilder.Ref(row, 5))),
                ("sub", IrBuilder.Call(
                    FunctionId.Substring, IrBuilder.Str(true),
                    IrBuilder.Ref(row, 5), IrBuilder.Lit(2), IrBuilder.Lit(3))),
                ("like", IrBuilder.Call(
                    FunctionId.Like, IrBuilder.Bool(true), IrBuilder.Ref(row, 5), IrBuilder.Lit("%e%"))),
            ]);

        await AssertAgreeAsync(IrBuilder.Plan(project), batchSize);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task The_temporal_kernels_agree(int batchSize)
    {
        var row = TestData.Instants.RowType();
        var project = IrBuilder.Project(
            IrBuilder.Read(TestData.Instants.Name, row),
            [
                ("year", IrBuilder.Call(
                    FunctionId.Extract, IrBuilder.I64(true),
                    IrBuilder.EnumArg("YEAR"), IrBuilder.Ref(row, 1))),
                ("dow", IrBuilder.Call(
                    FunctionId.Extract, IrBuilder.I64(true),
                    IrBuilder.EnumArg("DOW"), IrBuilder.Ref(row, 0))),
                ("epoch", IrBuilder.Call(
                    FunctionId.Extract, IrBuilder.I64(true),
                    IrBuilder.EnumArg("EPOCH"), IrBuilder.Ref(row, 2))),
                ("hour", IrBuilder.Call(
                    FunctionId.FloorTemporal, row.Fields[1].Type,
                    IrBuilder.Ref(row, 1), IrBuilder.EnumArg("HOUR"))),
                ("month", IrBuilder.Call(
                    FunctionId.FloorTemporal, row.Fields[0].Type,
                    IrBuilder.Ref(row, 0), IrBuilder.EnumArg("MONTH"))),
            ]);

        await AssertAgreeAsync(IrBuilder.Plan(project), batchSize);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Sort_topn_and_fetch_agree(int batchSize)
    {
        var row = TestData.Sortable.RowType();
        var read = IrBuilder.Read(TestData.Sortable.Name, row);

        await AssertAgreeAsync(
            IrBuilder.Plan(IrBuilder.Sort(
                read, IrBuilder.Asc(1, row.Fields[1].Type), IrBuilder.Desc(0, row.Fields[0].Type))),
            batchSize);
        await AssertAgreeAsync(
            IrBuilder.Plan(IrBuilder.TopN(read, 1, 3, IrBuilder.Asc(0, row.Fields[0].Type))),
            batchSize);
        await AssertAgreeAsync(IrBuilder.Plan(IrBuilder.Fetch(read, 2, 2)), batchSize);
        await AssertAgreeAsync(IrBuilder.Plan(IrBuilder.Fetch(read, 1, null)), batchSize);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Values_agrees(int batchSize)
    {
        var rowType = IrBuilder.Row(
            IrBuilder.F("a", IrBuilder.I64()),
            IrBuilder.F("b", IrBuilder.Str(true)),
            IrBuilder.F("c", IrBuilder.Dec(18, 2)));
        var plan = IrBuilder.Plan(IrBuilder.Values(
            rowType,
            [IrBuilder.Lit(1L), IrBuilder.Nullable(IrBuilder.Lit("x")), IrBuilder.LitDecimal(1.25m, 18, 2)],
            [IrBuilder.Lit(2L), IrBuilder.Null(IrBuilder.Str()), IrBuilder.LitDecimal(-3m, 18, 2)]));

        await AssertAgreeAsync(plan, batchSize);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Aggregation_agrees_including_distinct_and_filtered_measures(int batchSize)
    {
        var row = TestData.Trades.RowType();
        var plan = IrBuilder.Plan(IrBuilder.HashAggregate(
            IrBuilder.Read(TestData.Trades.Name, row),
            [0],
            [
                ("n", IrBuilder.Agg(AggregateFunctionId.Count, IrBuilder.I64())),
                ("qty", IrBuilder.Agg(
                    AggregateFunctionId.Sum, IrBuilder.I64(true), IrBuilder.Ref(row, 1))),
                ("qty0", IrBuilder.Agg(
                    AggregateFunctionId.Sum0, IrBuilder.I64(), IrBuilder.Ref(row, 1))),
                ("distinct_qty", IrBuilder.Agg(
                    AggregateFunctionId.Count, IrBuilder.I64(), IrBuilder.Ref(row, 1), distinct: true)),
                ("live", IrBuilder.Agg(
                    AggregateFunctionId.Count, IrBuilder.I64(), filter: IrBuilder.Ref(row, 3))),
                ("lo", IrBuilder.Agg(
                    AggregateFunctionId.Min, IrBuilder.Fp64(true), IrBuilder.Ref(row, 2))),
                ("hi", IrBuilder.Agg(
                    AggregateFunctionId.Max, IrBuilder.Fp64(true), IrBuilder.Ref(row, 2))),
            ]));

        // FP columns produced by aggregation compare within 4 ULPs (05-testing.md §5).
        await AssertAgreeAsync(
            plan,
            batchSize,
            new ResultComparisonOptions
            {
                CompareAsMultiset = true,
                AggregatedFloatColumns = [6, 7],
            });
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task A_floating_point_sum_agrees_within_four_ulps(int batchSize)
    {
        var row = Series.RowType();
        var plan = IrBuilder.Plan(IrBuilder.HashAggregate(
            IrBuilder.Read(Series.Name, row),
            [],
            [("total", IrBuilder.Agg(
                AggregateFunctionId.Sum, IrBuilder.Fp64(true), IrBuilder.Ref(row, 1)))]));

        await AssertAgreeAsync(
            plan, batchSize, new ResultComparisonOptions { AggregatedFloatColumns = [0] });
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task A_whole_pipeline_agrees(int batchSize)
    {
        var row = Series.RowType();
        var read = IrBuilder.Read(Series.Name, row);
        var filter = IrBuilder.Filter(
            read,
            IrBuilder.Call(FunctionId.IsNotNull, IrBuilder.Bool(), IrBuilder.Ref(row, 1)));
        var project = IrBuilder.Project(
            filter,
            [
                ("bucket", IrBuilder.Call(
                    FunctionId.Modulus, IrBuilder.I64(), IrBuilder.Ref(row, 0), IrBuilder.Lit(4L))),
                ("v", IrBuilder.Ref(row, 1)),
            ]);
        var aggregate = IrBuilder.HashAggregate(
            project,
            [0],
            [
                ("n", IrBuilder.Agg(AggregateFunctionId.Count, IrBuilder.I64())),
                ("total", IrBuilder.Agg(
                    AggregateFunctionId.Sum, IrBuilder.Fp64(true), IrBuilder.Ref(1, IrBuilder.Fp64(true)))),
            ]);
        var sort = IrBuilder.Sort(aggregate, IrBuilder.Asc(0, IrBuilder.I64()));
        var fetch = IrBuilder.Fetch(sort, 1, 2);

        await AssertAgreeAsync(
            IrBuilder.Plan(fetch),
            batchSize,
            new ResultComparisonOptions { AggregatedFloatColumns = [2] });
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task A_plan_with_parameters_agrees(int batchSize)
    {
        var row = TestData.Numbers.RowType();
        var read = IrBuilder.Read(TestData.Numbers.Name, row);
        var filter = IrBuilder.Filter(
            read,
            IrBuilder.Call(
                FunctionId.Ge,
                IrBuilder.Bool(true),
                IrBuilder.Ref(row, 1),
                IrBuilder.Param(0, IrBuilder.I64(true))));
        var plan = IrBuilder.Plan(filter, parameterTypes: [IrBuilder.I64(true)]);

        var vectorised = await Runner.RunAsync(plan, Source, batchSize, false, [30L]);
        var expected = await Runner.RunAsync(plan, Source, batchSize, true, [30L]);
        try
        {
            ResultComparer.AssertEquivalent(expected, vectorised, ResultComparisonOptions.Ordered);
        }
        finally
        {
            foreach (var batch in vectorised.Concat(expected))
            {
                batch.Dispose();
            }
        }
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Empty_input_agrees_through_every_operator(int batchSize)
    {
        var row = TestData.Empty.RowType();
        var read = IrBuilder.Read(TestData.Empty.Name, row);
        var filter = IrBuilder.Filter(read, IrBuilder.Lit(true));
        var sort = IrBuilder.Sort(filter, IrBuilder.Asc(0, row.Fields[0].Type));
        var fetch = IrBuilder.Fetch(sort, 0, 5);
        var aggregate = IrBuilder.HashAggregate(
            fetch, [], [("n", IrBuilder.Agg(AggregateFunctionId.Count, IrBuilder.I64()))]);

        await AssertAgreeAsync(IrBuilder.Plan(aggregate), batchSize);
    }

    private static async Task AssertAgreeAsync(
        Plan plan, int batchSize, ResultComparisonOptions? options = null)
    {
        var vectorised = await Runner.RunAsync(plan, Source, batchSize);
        var expected = await Runner.RunAsync(plan, Source, batchSize, reference: true);
        try
        {
            ResultComparer.AssertEquivalent(
                expected, vectorised, options ?? ResultComparisonOptions.Ordered);
        }
        finally
        {
            foreach (var batch in vectorised.Concat(expected))
            {
                batch.Dispose();
            }
        }
    }
}

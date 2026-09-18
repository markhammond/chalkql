using Chalk.Execution.Tests.Harness;
using Chalk.Ir;
using Chalk.TestKit;

namespace Chalk.Execution.Tests;

/// <summary>
/// Three-valued logic and the null-handling nodes of §6.4: Kleene AND/OR, the <c>IS</c> family,
/// COALESCE, NULLIF, CASE and IN. COALESCE and NULLIF are here because the IR permits them even
/// though the reference planner rewrites both to CASE.
/// </summary>
public sealed class LogicKernelTests
{
    private static readonly TestTable Table = TestData.Logic;
    private static readonly TestSource Source = TestData.Source(Table, TestData.Numbers, TestData.Empty);

    public static TheoryData<int> BatchSizes() => TestData.BatchSizeData;

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task And_is_Kleene(int batchSize)
    {
        var row = Table.RowType();
        var expr = IrBuilder.Call(
            FunctionId.And, IrBuilder.Bool(true), IrBuilder.Ref(row, 0), IrBuilder.Ref(row, 1));

        var values = await Runner.ProjectAsync(expr, Source, Table, batchSize);

        // (a, b) over the nine combinations of TRUE / FALSE / NULL, in fixture order.
        Assert.Equal(
            new object?[] { true, false, null, false, false, false, null, false, null },
            values);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Or_is_Kleene(int batchSize)
    {
        var row = Table.RowType();
        var expr = IrBuilder.Call(
            FunctionId.Or, IrBuilder.Bool(true), IrBuilder.Ref(row, 0), IrBuilder.Ref(row, 1));

        var values = await Runner.ProjectAsync(expr, Source, Table, batchSize);

        Assert.Equal(
            new object?[] { true, true, true, true, false, null, true, null, null },
            values);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Nary_and_short_circuits_without_changing_the_answer(int batchSize)
    {
        var row = Table.RowType();
        var expr = IrBuilder.Call(
            FunctionId.And,
            IrBuilder.Bool(true),
            IrBuilder.Ref(row, 0),
            IrBuilder.Ref(row, 1),
            IrBuilder.Lit(true));

        var values = await Runner.ProjectAsync(expr, Source, Table, batchSize);
        var expected = await Runner.ProjectAsync(expr, Source, Table, batchSize, reference: true);

        Assert.Equal(expected, values);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Not_leaves_null_alone(int batchSize)
    {
        var row = Table.RowType();
        var expr = IrBuilder.Call(FunctionId.Not, IrBuilder.Bool(true), IrBuilder.Ref(row, 0));

        var values = await Runner.ProjectAsync(expr, Source, Table, batchSize);

        Assert.Equal(false, values[0]);
        Assert.Equal(true, values[3]);
        Assert.Null(values[6]);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task The_is_family_never_returns_null(int batchSize)
    {
        var row = Table.RowType();
        foreach (var function in new[]
                 {
                     FunctionId.IsNull, FunctionId.IsNotNull, FunctionId.IsTrue,
                     FunctionId.IsNotTrue, FunctionId.IsFalse, FunctionId.IsNotFalse,
                 })
        {
            var expr = IrBuilder.Call(function, IrBuilder.Bool(), IrBuilder.Ref(row, 0));
            var values = await Runner.ProjectAsync(expr, Source, Table, batchSize);
            Assert.All(values, Assert.NotNull);

            var expected = await Runner.ProjectAsync(expr, Source, Table, batchSize, reference: true);
            Assert.Equal(expected, values);
        }
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Is_null_works_on_a_non_boolean_column(int batchSize)
    {
        var row = TestData.Numbers.RowType();
        var expr = IrBuilder.Call(FunctionId.IsNull, IrBuilder.Bool(), IrBuilder.Ref(row, 5));

        var values = await Runner.ProjectAsync(expr, Source, TestData.Numbers, batchSize);

        Assert.Equal(false, values[0]);
        Assert.Equal(true, values[3]);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Coalesce_takes_the_first_value(int batchSize)
    {
        var row = TestData.Numbers.RowType();
        var expr = IrBuilder.Call(
            FunctionId.Coalesce,
            IrBuilder.I64(true),
            IrBuilder.Ref(row, 1),
            IrBuilder.Lit(-1L));

        var values = await Runner.ProjectAsync(expr, Source, TestData.Numbers, batchSize);

        Assert.Equal(10L, values[0]);
        Assert.Equal(-1L, values[3]);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Coalesce_of_all_nulls_is_null(int batchSize)
    {
        var row = TestData.Numbers.RowType();
        var expr = IrBuilder.Call(
            FunctionId.Coalesce,
            IrBuilder.Str(true),
            IrBuilder.Ref(row, 5),
            IrBuilder.Null(IrBuilder.Str()));

        var values = await Runner.ProjectAsync(expr, Source, TestData.Numbers, batchSize);

        Assert.Equal("alpha", values[0]);
        Assert.Null(values[3]);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Nullif_blanks_a_match_and_passes_everything_else(int batchSize)
    {
        var row = TestData.Numbers.RowType();
        var expr = IrBuilder.Call(
            FunctionId.Nullif, IrBuilder.I64(true), IrBuilder.Ref(row, 1), IrBuilder.Lit(30L));

        var values = await Runner.ProjectAsync(expr, Source, TestData.Numbers, batchSize);

        Assert.Equal(10L, values[0]);
        Assert.Null(values[2]);   // 30 = 30
        Assert.Null(values[3]);   // NULL stays NULL
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Nullif_against_a_null_returns_the_left_operand(int batchSize)
    {
        var row = TestData.Numbers.RowType();
        var expr = IrBuilder.Call(
            FunctionId.Nullif,
            IrBuilder.Str(true),
            IrBuilder.Ref(row, 5),
            IrBuilder.Null(IrBuilder.Str()));

        var values = await Runner.ProjectAsync(expr, Source, TestData.Numbers, batchSize);

        Assert.Equal("alpha", values[0]);
        Assert.Null(values[3]);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Case_picks_the_first_true_clause_and_a_null_condition_is_false(int batchSize)
    {
        var row = TestData.Numbers.RowType();
        var expr = IrBuilder.Case(
            IrBuilder.Str(true),
            IrBuilder.Lit("other"),
            (IrBuilder.Call(FunctionId.Gt, IrBuilder.Bool(true), IrBuilder.Ref(row, 0), IrBuilder.Lit(4)),
                IrBuilder.Lit("big")),
            (IrBuilder.Call(FunctionId.Lt, IrBuilder.Bool(true), IrBuilder.Ref(row, 0), IrBuilder.Lit(0)),
                IrBuilder.Lit("negative")));

        var values = await Runner.ProjectAsync(expr, Source, TestData.Numbers, batchSize);

        Assert.Equal("other", values[0]);
        Assert.Equal("other", values[2]);      // NULL condition behaves as FALSE
        Assert.Equal("big", values[4]);
        Assert.Equal("negative", values[5]);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Case_can_return_null_from_a_taken_branch(int batchSize)
    {
        var row = TestData.Numbers.RowType();
        var expr = IrBuilder.Case(
            IrBuilder.I64(true),
            IrBuilder.Lit(0L),
            (IrBuilder.Lit(true), IrBuilder.Ref(row, 1)));

        var values = await Runner.ProjectAsync(expr, Source, TestData.Numbers, batchSize);

        Assert.Equal(10L, values[0]);
        Assert.Null(values[3]);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task In_list_is_three_valued(int batchSize)
    {
        var row = TestData.Numbers.RowType();
        var withNull = IrBuilder.In(
            IrBuilder.Ref(row, 0),
            IrBuilder.Bool(true),
            IrBuilder.Lit(1),
            IrBuilder.Null(IrBuilder.I32()));
        var withoutNull = IrBuilder.In(
            IrBuilder.Ref(row, 0), IrBuilder.Bool(true), IrBuilder.Lit(1), IrBuilder.Lit(2));

        var tolerant = await Runner.ProjectAsync(withNull, Source, TestData.Numbers, batchSize);
        var strict = await Runner.ProjectAsync(withoutNull, Source, TestData.Numbers, batchSize);

        Assert.Equal(true, tolerant[0]);
        Assert.Null(tolerant[1]);      // no match and an option is NULL
        Assert.Null(tolerant[2]);      // the value itself is NULL
        Assert.Equal(true, strict[1]);
        Assert.Equal(false, strict[4]);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task In_list_over_strings_and_parameters(int batchSize)
    {
        var row = TestData.Numbers.RowType();
        var expr = IrBuilder.In(
            IrBuilder.Ref(row, 5),
            IrBuilder.Bool(true),
            IrBuilder.Lit("alpha"),
            IrBuilder.Param(0, IrBuilder.Str(true)));

        var values = await Runner.ProjectAsync(
            expr,
            Source,
            TestData.Numbers,
            batchSize,
            parameters: ["zzz"],
            parameterTypes: [IrBuilder.Str(true)]);

        Assert.Equal(true, values[0]);
        Assert.Equal(false, values[1]);
        Assert.Equal(true, values[8]);
        Assert.Null(values[3]);
    }

    [Fact]
    public async Task Logic_over_empty_input_produces_no_rows()
    {
        var row = TestData.Empty.RowType();
        var expr = IrBuilder.Call(
            FunctionId.And, IrBuilder.Bool(true), IrBuilder.Ref(row, 6), IrBuilder.Lit(true));

        Assert.Empty(await Runner.ProjectAsync(expr, Source, TestData.Empty));
    }
}

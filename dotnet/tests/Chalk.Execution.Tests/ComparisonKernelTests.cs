using Chalk.Catalog;
using Chalk.Execution.Tests.Harness;
using Chalk.Ir;
using Chalk.TestKit;

namespace Chalk.Execution.Tests;

/// <summary>
/// The comparison kernels of <c>02-ir.md</c> §6, and the type rules of §3 they have to honour: NaN is
/// equal to nothing, NULL propagates, <c>IS DISTINCT FROM</c> never does, and strings order by code
/// point.
/// </summary>
public sealed class ComparisonKernelTests
{
    private static readonly TestTable Table = TestData.Numbers;
    private static readonly TestSource Source = TestData.Source(
        Table, TestData.Collated, TestData.Logic, TestData.Empty);

    /// <summary>
    /// The same rows out of a source that <em>declares</em> a case-insensitive collation. The
    /// declaration is a pushdown input (D89): it tells the planner not to push a string comparison,
    /// and it never reaches a kernel — which is what makes the two sources' answers identical, and
    /// what §5 F asks to be pinned.
    /// </summary>
    private static readonly TestSource CaseInsensitive = new("mem", "main", TestData.Collated)
    {
        Profile = DialectProfiles.Sqlite.With(
            stringCollation: StringCollation.CaseInsensitive),
    };

    public static TheoryData<int> BatchSizes() => TestData.BatchSizeData;

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Equality_propagates_null_and_rejects_NaN(int batchSize)
    {
        var row = Table.RowType();
        var expr = IrBuilder.Call(
            FunctionId.Eq, IrBuilder.Bool(true), IrBuilder.Ref(row, 2), IrBuilder.Ref(row, 2));

        var values = await Runner.ProjectAsync(expr, Source, Table, batchSize);

        Assert.Equal(true, values[0]);
        Assert.Equal(false, values[3]);   // NaN = NaN is FALSE (02-ir.md §3)
        Assert.Null(values[4]);           // NULL = NULL is NULL
        Assert.Equal(true, values[7]);    // -0.0 = -0.0
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Inequality_is_true_for_NaN(int batchSize)
    {
        var row = Table.RowType();
        var expr = IrBuilder.Call(
            FunctionId.Ne, IrBuilder.Bool(true), IrBuilder.Ref(row, 2), IrBuilder.Ref(row, 2));

        var values = await Runner.ProjectAsync(expr, Source, Table, batchSize);

        Assert.Equal(false, values[0]);
        Assert.Equal(true, values[3]);
        Assert.Null(values[4]);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Ordering_against_NaN_is_false_in_both_directions(int batchSize)
    {
        var row = Table.RowType();
        var less = IrBuilder.Call(
            FunctionId.Lt, IrBuilder.Bool(true), IrBuilder.Ref(row, 2), IrBuilder.Lit(0d));
        var greater = IrBuilder.Call(
            FunctionId.Gt, IrBuilder.Bool(true), IrBuilder.Ref(row, 2), IrBuilder.Lit(0d));

        var lt = await Runner.ProjectAsync(less, Source, Table, batchSize);
        var gt = await Runner.ProjectAsync(greater, Source, Table, batchSize);

        Assert.Equal(false, lt[3]);
        Assert.Equal(false, gt[3]);
        Assert.Equal(true, lt[1]);
        Assert.Equal(true, gt[0]);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Is_distinct_from_treats_nulls_and_NaNs_as_equal(int batchSize)
    {
        var row = Table.RowType();
        var distinct = IrBuilder.Call(
            FunctionId.IsDistinctFrom, IrBuilder.Bool(), IrBuilder.Ref(row, 2), IrBuilder.Ref(row, 2));
        var notDistinct = IrBuilder.Call(
            FunctionId.IsNotDistinctFrom, IrBuilder.Bool(), IrBuilder.Ref(row, 2), IrBuilder.Ref(row, 2));

        var isDistinct = await Runner.ProjectAsync(distinct, Source, Table, batchSize);
        var isNotDistinct = await Runner.ProjectAsync(notDistinct, Source, Table, batchSize);

        Assert.All(isDistinct, v => Assert.Equal(false, v));
        Assert.All(isNotDistinct, v => Assert.Equal(true, v));
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Is_distinct_from_separates_a_null_from_a_value(int batchSize)
    {
        var row = Table.RowType();
        var expr = IrBuilder.Call(
            FunctionId.IsDistinctFrom, IrBuilder.Bool(), IrBuilder.Ref(row, 2), IrBuilder.Lit(1.5d));

        var values = await Runner.ProjectAsync(expr, Source, Table, batchSize);

        Assert.Equal(false, values[0]);
        Assert.Equal(true, values[4]);   // NULL is distinct from 1.5
        Assert.Equal(true, values[3]);   // NaN is distinct from 1.5
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Strings_compare_by_code_point(int batchSize)
    {
        var row = Table.RowType();

        // 'Delta' < 'alpha' by code point ('D' = 0x44, 'a' = 0x61) even though a culture-aware
        // comparison would say otherwise; 'épée' sorts after every ASCII string (A7).
        var expr = IrBuilder.Call(
            FunctionId.Lt, IrBuilder.Bool(true), IrBuilder.Ref(row, 5), IrBuilder.Lit("alpha"));

        var values = await Runner.ProjectAsync(expr, Source, Table, batchSize);

        Assert.Equal(false, values[0]);
        Assert.Equal(true, values[5]);   // "Delta"
        Assert.Equal(false, values[6]);  // "épée"
        Assert.Equal(true, values[7]);   // ""
        Assert.Null(values[3]);
    }

    /// <summary>
    /// <b>Provenance.</b> This theory and the one below it are grafted from
    /// <c>ikvmnet/calcite-dotnet</c> (Apache-2.0),
    /// <c>src/Apache.Calcite.Tests/ClrEnumerableRelTests.cs</c> — its collation group and its
    /// projection-position <c>IS DISTINCT FROM</c>. The shapes are taken; no expected value is
    /// (D163). See the repository's NOTICE.
    /// <para>
    /// §5 F: a declared collation changes what is <em>pushed</em>, never what is computed. The
    /// same range, the same equality and the same ordering over a source declaring
    /// <c>StringCollation.CaseInsensitive</c> answer exactly as they do over one declaring nothing,
    /// because the executor has no collation to consult — it compares UTF-8 by code point, always.
    /// </para>
    /// </summary>
    /// <remarks>
    /// This is the local half of the pushdown corpus's
    /// <c>16_sqlite_collated_string_range</c>: there the predicate is pushed to a source whose
    /// profile declares <c>Binary</c> and the differential says the pushed answer is the local one;
    /// here the profile says otherwise and the local answer is unmoved.
    /// </remarks>
    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task A_declared_collation_does_not_reach_the_comparison_kernels(int batchSize)
    {
        var row = TestData.Collated.RowType();

        // 'C' <= label < 'e': 0x43 <= label < 0x65, so every upper-case letter from C and the
        // lower-case letters below 'e'. A case-folding collation would have taken 'A' and 'a' in
        // together and left 'E' out with 'e'.
        var inRange = IrBuilder.Call(
            FunctionId.And,
            IrBuilder.Bool(true),
            IrBuilder.Call(
                FunctionId.Ge, IrBuilder.Bool(true), IrBuilder.Ref(row, 0), IrBuilder.Lit("C")),
            IrBuilder.Call(
                FunctionId.Lt, IrBuilder.Bool(true), IrBuilder.Ref(row, 0), IrBuilder.Lit("e")));

        object?[] expectedRange = [false, true, true, true, true, true, false, null];
        Assert.Equal(
            expectedRange,
            await Runner.ProjectAsync(inRange, Source, TestData.Collated, batchSize));
        Assert.Equal(
            expectedRange,
            await Runner.ProjectAsync(inRange, CaseInsensitive, TestData.Collated, batchSize));

        // And equality is not case-folding either, on either source.
        var equal = IrBuilder.Call(
            FunctionId.Eq, IrBuilder.Bool(true), IrBuilder.Ref(row, 0), IrBuilder.Lit("c"));

        object?[] expectedEqual = [false, false, false, false, false, true, false, null];
        Assert.Equal(
            expectedEqual,
            await Runner.ProjectAsync(equal, Source, TestData.Collated, batchSize));
        Assert.Equal(
            expectedEqual,
            await Runner.ProjectAsync(equal, CaseInsensitive, TestData.Collated, batchSize));
    }

    /// <summary>
    /// §5 G: <c>IS DISTINCT FROM</c> in projection position, over the nine combinations of TRUE,
    /// FALSE and NULL. The claim is the one that separates it from <c>=</c>: it is a total
    /// function, so every one of the nine is a value and none of them is NULL — including
    /// NULL against NULL, which is <c>false</c> and not unknown.
    /// </summary>
    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Is_distinct_from_in_a_projection_is_never_null(int batchSize)
    {
        var row = TestData.Logic.RowType();
        var distinct = IrBuilder.Call(
            FunctionId.IsDistinctFrom,
            IrBuilder.Bool(),
            IrBuilder.Ref(row, 0),
            IrBuilder.Ref(row, 1));
        var notDistinct = IrBuilder.Call(
            FunctionId.IsNotDistinctFrom,
            IrBuilder.Bool(),
            IrBuilder.Ref(row, 0),
            IrBuilder.Ref(row, 1));

        // (true,true) (true,false) (true,null) (false,true) (false,false) (false,null)
        // (null,true) (null,false) (null,null)
        object?[] expected =
            [false, true, true, true, false, true, true, true, false];

        Assert.Equal(
            expected,
            await Runner.ProjectAsync(distinct, Source, TestData.Logic, batchSize));

        // And the negation really is the negation, value for value, with no NULL anywhere.
        var complement = await Runner.ProjectAsync(
            notDistinct, Source, TestData.Logic, batchSize);
        Assert.Equal([.. expected.Select(v => (object?)!(bool)v!)], complement);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Decimals_compare_by_unscaled_magnitude(int batchSize)
    {
        var row = Table.RowType();
        var expr = IrBuilder.Call(
            FunctionId.Ge,
            IrBuilder.Bool(true),
            IrBuilder.Ref(row, 4),
            IrBuilder.LitDecimal(0m, 18, 4));

        var values = await Runner.ProjectAsync(expr, Source, Table, batchSize);

        Assert.Equal(true, values[0]);
        Assert.Equal(false, values[1]);
        Assert.Equal(true, values[3]);
        Assert.Null(values[4]);
        Assert.Equal(false, values[5]);  // -0.0005
    }

    [Fact]
    public async Task Comparison_over_empty_input_produces_no_rows()
    {
        var row = TestData.Empty.RowType();
        var expr = IrBuilder.Call(
            FunctionId.Eq, IrBuilder.Bool(true), IrBuilder.Ref(row, 0), IrBuilder.Lit(1));

        var values = await Runner.ProjectAsync(expr, Source, TestData.Empty);

        Assert.Empty(values);
    }

    [Fact]
    public async Task Comparison_against_a_scalar_parameter_binds_once_per_execution()
    {
        var row = Table.RowType();
        var expr = IrBuilder.Call(
            FunctionId.Eq,
            IrBuilder.Bool(true),
            IrBuilder.Ref(row, 0),
            IrBuilder.Param(0, IrBuilder.I32(true)));

        var values = await Runner.ProjectAsync(
            expr, Source, Table, parameters: [2], parameterTypes: [IrBuilder.I32(true)]);

        Assert.Equal(false, values[0]);
        Assert.Equal(true, values[1]);
        Assert.Null(values[2]);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Both_engines_agree_on_every_comparison(int batchSize)
    {
        var row = Table.RowType();
        foreach (var function in new[]
                 {
                     FunctionId.Eq, FunctionId.Ne, FunctionId.Lt,
                     FunctionId.Le, FunctionId.Gt, FunctionId.Ge,
                 })
        {
            foreach (var (column, literal) in Operands(row))
            {
                var expr = IrBuilder.Call(function, IrBuilder.Bool(true), column, literal);
                var vectorised = await Runner.ProjectAsync(expr, Source, Table, batchSize);
                var expected = await Runner.ProjectAsync(expr, Source, Table, batchSize, reference: true);
                Assert.Equal(expected, vectorised);
            }
        }
    }

    private static IEnumerable<(Expr Column, Expr Literal)> Operands(RowType row)
    {
        yield return (IrBuilder.Ref(row, 0), IrBuilder.Lit(4));
        yield return (IrBuilder.Ref(row, 1), IrBuilder.Lit(30L));
        yield return (IrBuilder.Ref(row, 2), IrBuilder.Lit(1.5d));
        yield return (IrBuilder.Ref(row, 3), IrBuilder.Lit(1.5f));
        yield return (IrBuilder.Ref(row, 4), IrBuilder.LitDecimal(2.5m, 18, 4));
        yield return (IrBuilder.Ref(row, 5), IrBuilder.Lit("delta"));
        yield return (IrBuilder.Ref(row, 6), IrBuilder.Lit(true));
    }
}

using Chalk.Execution.Tests.Harness;
using Chalk.Catalog;
using Chalk.Ir;
using Chalk.Sources;
using Chalk.TestKit;

namespace Chalk.Execution.Tests;

/// <summary>
/// Arithmetic per <c>02-ir.md</c> §6: exact kinds are checked, division by zero raises for them and
/// follows IEEE for floating point, and DECIMAL results round half-even to the declared scale.
/// </summary>
public sealed class ArithmeticKernelTests
{
    private static readonly TestTable Table = TestData.Numbers;
    private static readonly TestSource Source = TestData.Source(
        Table, TestData.Extremes, TestData.Rounding, TestData.NullableDivisors, TestData.Empty);

    public static TheoryData<int> BatchSizes() => TestData.BatchSizeData;

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Integer_addition_propagates_null(int batchSize)
    {
        var row = Table.RowType();
        var expr = IrBuilder.Call(
            FunctionId.Add, IrBuilder.I64(true), IrBuilder.Ref(row, 1), IrBuilder.Lit(5L));

        var values = await Runner.ProjectAsync(expr, Source, Table, batchSize);

        Assert.Equal(15L, values[0]);
        Assert.Null(values[3]);
        Assert.Equal(-55L, values[5]);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Integer_overflow_raises_rather_than_wrapping(int batchSize)
    {
        var table = TestData.Extremes;
        var row = table.RowType();
        var expr = IrBuilder.Call(
            FunctionId.Add, IrBuilder.I64(true), IrBuilder.Ref(row, 0), IrBuilder.Lit(1L));

        var failure = await Assert.ThrowsAsync<ExecutionException>(
            () => Runner.ProjectAsync(expr, Source, table, batchSize));

        Assert.Contains("Project", failure.OperatorPath, StringComparison.Ordinal);
        Assert.IsType<OverflowException>(failure.InnerException);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Negating_the_minimum_integer_raises(int batchSize)
    {
        var table = TestData.Extremes;
        var row = table.RowType();
        var expr = IrBuilder.Call(FunctionId.Negate, IrBuilder.I64(true), IrBuilder.Ref(row, 1));

        await Assert.ThrowsAsync<ExecutionException>(
            () => Runner.ProjectAsync(expr, Source, table, batchSize));
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Integer_division_by_zero_raises(int batchSize)
    {
        var row = Table.RowType();
        var expr = IrBuilder.Call(
            FunctionId.Divide, IrBuilder.I32(true), IrBuilder.Lit(100), IrBuilder.Ref(row, 0));

        var failure = await Assert.ThrowsAsync<ExecutionException>(
            () => Runner.ProjectAsync(expr, Source, Table, batchSize));

        Assert.IsType<DivideByZeroException>(failure.InnerException);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Floating_point_division_by_zero_follows_IEEE(int batchSize)
    {
        var row = Table.RowType();
        var expr = IrBuilder.Call(
            FunctionId.Divide, IrBuilder.Fp64(true), IrBuilder.Lit(1d), IrBuilder.Ref(row, 2));

        var values = await Runner.ProjectAsync(expr, Source, Table, batchSize);

        Assert.Equal(double.NegativeInfinity, values[7]);   // 1 / -0.0
        Assert.Equal(double.NaN, values[3]);
        Assert.Null(values[4]);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Integer_division_truncates_towards_zero_and_modulus_takes_the_dividend_sign(
        int batchSize)
    {
        var row = Table.RowType();
        var divide = IrBuilder.Call(
            FunctionId.Divide, IrBuilder.I64(true), IrBuilder.Ref(row, 1), IrBuilder.Lit(7L));
        var modulus = IrBuilder.Call(
            FunctionId.Modulus, IrBuilder.I64(true), IrBuilder.Ref(row, 1), IrBuilder.Lit(7L));

        var quotients = await Runner.ProjectAsync(divide, Source, Table, batchSize);
        var remainders = await Runner.ProjectAsync(modulus, Source, Table, batchSize);

        Assert.Equal(1L, quotients[0]);      // 10 / 7
        Assert.Equal(3L, remainders[0]);
        Assert.Equal(-8L, quotients[5]);     // -60 / 7 truncates towards zero
        Assert.Equal(-4L, remainders[5]);    // and the remainder keeps the dividend's sign
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task A_null_lane_is_not_divided(int batchSize)
    {
        // The column is NULL exactly where a naive kernel would read a zero out of the gap and
        // raise; the evaluator has to skip those lanes (§6.4).
        var table = TestData.NullableDivisors;
        var row = table.RowType();
        var expr = IrBuilder.Call(
            FunctionId.Divide, IrBuilder.I64(true), IrBuilder.Lit(10L), IrBuilder.Ref(row, 0));

        var values = await Runner.ProjectAsync(expr, Source, table, batchSize);

        Assert.Null(values[0]);
        Assert.Equal(2L, values[1]);
        Assert.Null(values[2]);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Decimal_multiplication_rounds_half_even_to_the_target_scale(int batchSize)
    {
        var table = TestData.Rounding;
        var row = table.RowType();

        // DECIMAL(18,2) result from a DECIMAL(18,3) column: 0.125 -> 0.12 and 0.135 -> 0.14.
        var expr = IrBuilder.Cast(IrBuilder.Ref(row, 0), IrBuilder.Dec(18, 2, true));

        var values = await Runner.ProjectAsync(expr, Source, table, batchSize);

        Assert.Equal(0.12m, values[0]);
        Assert.Equal(0.14m, values[1]);
        Assert.Equal(-0.12m, values[2]);
        Assert.Equal(-0.14m, values[3]);
        Assert.Null(values[4]);
    }

    /// <summary>
    /// <b>Provenance.</b> The value list is grafted from <c>ikvmnet/calcite-dotnet</c>
    /// (Apache-2.0), <c>src/Apache.Calcite.Tests/Interop/JavaDecimalsTests.cs</c>; its BigDecimal
    /// interop mechanism has no counterpart here and none is taken, only which boundaries are worth
    /// asking about. Every expected value is Chalk's own (D163). See the repository's NOTICE.
    /// <para>
    /// Arithmetic at the widest whole number the 128-bit decimal carries (§5 C). Dividing
    /// <c>decimal.MaxValue</c> by ten lands exactly on a midpoint — ...033.5 — and half-even takes
    /// it to the even digit, at both signs; multiplying it by ten needs a thirtieth digit that
    /// DECIMAL(29,0) has not got, and the kernel says so rather than wrapping.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Decimal_arithmetic_at_the_widest_value_is_exact_or_refused(int batchSize)
    {
        var table = TestData.Rounding;
        var row = table.RowType();
        var ten = IrBuilder.LitDecimal(10m, 29, 0);

        var tenth = await Runner.ProjectAsync(
            IrBuilder.Call(
                FunctionId.Divide, IrBuilder.Dec(29, 0, true), IrBuilder.Ref(row, 1), ten),
            Source, table, batchSize);

        Assert.Equal(7922816251426433759354395034m, tenth[0]);    // ...033.5, up to even
        Assert.Equal(-7922816251426433759354395034m, tenth[1]);   // and the same below zero
        Assert.Equal(10_000_000_000_000_000_000m, tenth[2]);      // 10^20 / 10, no rounding at all
        Assert.Null(tenth[4]);

        await Assert.ThrowsAsync<ExecutionException>(() => Runner.ProjectAsync(
            IrBuilder.Call(
                FunctionId.Multiply, IrBuilder.Dec(29, 0, true), IrBuilder.Ref(row, 1), ten),
            Source, table, batchSize));
    }

    /// <summary>
    /// And at the finest scale (§5 C): the smallest positive value at scale 28 is a value and not a
    /// rounding of zero — subtracting it takes the largest value down by exactly one in its last
    /// digit, and takes itself to zero — while doubling the largest one overflows the precision it
    /// was declared at.
    /// </summary>
    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Decimal_arithmetic_at_the_finest_scale_keeps_the_last_digit(int batchSize)
    {
        var table = TestData.Rounding;
        var row = table.RowType();

        var lowered = await Runner.ProjectAsync(
            IrBuilder.Call(
                FunctionId.Subtract,
                IrBuilder.Dec(29, 28, true),
                IrBuilder.Ref(row, 2),
                IrBuilder.LitDecimal(TestData.SmallestAtScale28, 29, 28)),
            Source, table, batchSize);

        Assert.Equal(
            TestData.LargestAtScale28 - TestData.SmallestAtScale28, lowered[0]);
        Assert.Equal(0m, lowered[2]);
        Assert.Equal(TestData.TieUpAtScale28 - TestData.SmallestAtScale28, lowered[3]);

        // The largest at scale 28 doubled needs an integer digit DECIMAL(29,28) has not got.
        await Assert.ThrowsAsync<ExecutionException>(() => Runner.ProjectAsync(
            IrBuilder.Call(
                FunctionId.Multiply,
                IrBuilder.Dec(29, 28, true),
                IrBuilder.Ref(row, 2),
                IrBuilder.LitDecimal(2m, 29, 28)),
            Source, table, batchSize));
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Decimal_arithmetic_rescales_the_result(int batchSize)
    {
        var row = Table.RowType();
        var expr = IrBuilder.Call(
            FunctionId.Divide,
            IrBuilder.Dec(18, 4, true),
            IrBuilder.Ref(row, 4),
            IrBuilder.LitDecimal(3m, 18, 4));

        var values = await Runner.ProjectAsync(expr, Source, Table, batchSize);

        Assert.Equal(0.5m, values[0]);
        Assert.Equal(4115.2263m, values[6]);   // 12345.6789 / 3, rounded half-even at scale 4
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Decimal_overflow_of_the_declared_precision_raises(int batchSize)
    {
        var row = Table.RowType();
        var expr = IrBuilder.Call(
            FunctionId.Multiply,
            IrBuilder.Dec(6, 4, true),
            IrBuilder.Ref(row, 4),
            IrBuilder.LitDecimal(1000m, 6, 4));

        await Assert.ThrowsAsync<ExecutionException>(
            () => Runner.ProjectAsync(expr, Source, Table, batchSize));
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Floor_ceil_and_round_follow_SQL(int batchSize)
    {
        var row = Table.RowType();
        var floor = IrBuilder.Call(FunctionId.Floor, IrBuilder.Fp64(true), IrBuilder.Ref(row, 2));
        var ceil = IrBuilder.Call(FunctionId.Ceil, IrBuilder.Fp64(true), IrBuilder.Ref(row, 2));
        var round = IrBuilder.Call(FunctionId.Round, IrBuilder.Fp64(true), IrBuilder.Ref(row, 2));

        var floors = await Runner.ProjectAsync(floor, Source, Table, batchSize);
        var ceils = await Runner.ProjectAsync(ceil, Source, Table, batchSize);
        var rounds = await Runner.ProjectAsync(round, Source, Table, batchSize);

        Assert.Equal(1d, floors[0]);
        Assert.Equal(2d, ceils[0]);
        Assert.Equal(2d, rounds[0]);      // SQL rounds half away from zero, not half-even
        Assert.Equal(-3d, rounds[1]);     // -2.5 -> -3
        Assert.Null(floors[4]);
    }

    /// <summary>
    /// The values a scaled rounding gets wrong, through both engines (F134): a double is rounded on
    /// its exact value, so 655.925 to two places is 655.92 and 2.675 is 2.67, while the same digits
    /// as a DECIMAL are exact in base ten and round the other way; a negative count rounds to a power
    /// of ten; and a midpoint that is exactly representable rounds away from zero.
    /// </summary>
    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Round_with_digits_rounds_the_exact_value_in_both_engines(int batchSize)
    {
        var source = TestData.Source(RoundingBoundaries);
        var row = RoundingBoundaries.RowType();

        var two = IrBuilder.Call(FunctionId.Round, IrBuilder.Fp64(true), IrBuilder.Ref(row, 0), IrBuilder.Lit(2));
        var hundreds = IrBuilder.Call(FunctionId.Round, IrBuilder.Fp64(true), IrBuilder.Ref(row, 0), IrBuilder.Lit(-2));
        var singles = IrBuilder.Call(FunctionId.Round, IrBuilder.Fp32(true), IrBuilder.Ref(row, 1), IrBuilder.Lit(2));
        var decimals = IrBuilder.Call(FunctionId.Round, IrBuilder.Dec(18, 4, true), IrBuilder.Ref(row, 2), IrBuilder.Lit(2));
        var decimalHundreds = IrBuilder.Call(FunctionId.Round, IrBuilder.Dec(18, 4, true), IrBuilder.Ref(row, 2), IrBuilder.Lit(-2));

        foreach (var reference in new[] { false, true })
        {
            var doubles = await Runner.ProjectAsync(two, source, RoundingBoundaries, batchSize, reference);
            Assert.Equal([2.5, -2.5, 655.92, 2.67, 1.0, 0.28, 0.13, 1250.0, null], doubles);

            var coarse = await Runner.ProjectAsync(hundreds, source, RoundingBoundaries, batchSize, reference);
            Assert.Equal([0.0, -0.0, 700.0, 0.0, 0.0, 0.0, 0.0, 1300.0, null], coarse);

            var floats = await Runner.ProjectAsync(singles, source, RoundingBoundaries, batchSize, reference);
            Assert.Equal([2.5f, -2.5f, 655.92f, 2.67f, 1.0f, 0.28f, 0.13f, 1250f, null], floats);

            var exact = await Runner.ProjectAsync(decimals, source, RoundingBoundaries, batchSize, reference);
            Assert.Equal([2.5m, -2.5m, 655.93m, 2.68m, 1.01m, 0.29m, 0.13m, 1250m, null], exact);

            var exactCoarse = await Runner.ProjectAsync(decimalHundreds, source, RoundingBoundaries, batchSize, reference);
            Assert.Equal([0m, 0m, 700m, 0m, 0m, 0m, 0m, 1300m, null], exactCoarse);
        }

        await Runner.AssertEnginesAgreeAsync(
            IrBuilder.Plan(IrBuilder.Project(
                IrBuilder.Read(RoundingBoundaries.Name, row),
                [("a", two), ("b", hundreds), ("c", singles), ("d", decimals), ("e", decimalHundreds)])),
            source);
    }

    /// <summary>
    /// The neighbours of midpoints at two decimal places: 655.925, 2.675, 1.005 and 0.285 are below
    /// their midpoints in binary, as doubles and as floats, and round down; 0.125 is a midpoint exactly
    /// and rounds away; 1250 sits on a hundreds midpoint. The same digits as a DECIMAL are exact and
    /// round the other way. The oracle test proves the arithmetic; here the engines' answers are pinned.
    /// </summary>
    private static TestTable RoundingBoundaries { get; } = new()
    {
        Name = "rounding_boundaries",
        Columns =
        [
            ("f64", ChalkType.Float64(nullable: true)),
            ("f32", ChalkType.Float32(nullable: true)),
            ("dec", ChalkType.Decimal(18, 4, nullable: true)),
        ],
        Rows =
        [
            [2.5d, 2.5f, 2.5m],
            [-2.5d, -2.5f, -2.5m],
            [655.925d, 655.925f, 655.925m],
            [2.675d, 2.675f, 2.675m],
            [1.005d, 1.005f, 1.005m],
            [0.285d, 0.285f, 0.285m],
            [0.125d, 0.125f, 0.125m],
            [1250d, 1250f, 1250m],
            [null, null, null],
        ],
    };

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Absolute_value_and_negation_cover_the_numeric_kinds(int batchSize)
    {
        var row = Table.RowType();
        foreach (var (column, type) in new (int, Ir.Type)[]
                 {
                     (0, IrBuilder.I32(true)),
                     (1, IrBuilder.I64(true)),
                     (2, IrBuilder.Fp64(true)),
                     (3, IrBuilder.Fp32(true)),
                     (4, IrBuilder.Dec(18, 4, true)),
                 })
        {
            foreach (var function in new[] { FunctionId.Abs, FunctionId.Negate })
            {
                var expr = IrBuilder.Call(function, type, IrBuilder.Ref(row, column));
                var vectorised = await Runner.ProjectAsync(expr, Source, Table, batchSize);
                var expected = await Runner.ProjectAsync(expr, Source, Table, batchSize, reference: true);
                Assert.Equal(expected, vectorised);
            }
        }
    }

    [Fact]
    public async Task Arithmetic_over_a_single_row_and_over_no_rows()
    {
        var single = TestData.Source(TestData.SingleRow);
        var row = TestData.SingleRow.RowType();
        var expr = IrBuilder.Call(
            FunctionId.Multiply, IrBuilder.I64(), IrBuilder.Ref(row, 0), IrBuilder.Lit(3L));

        Assert.Equal([21L], await Runner.ProjectAsync(expr, single, TestData.SingleRow));

        var emptyRow = TestData.Empty.RowType();
        var emptyExpr = IrBuilder.Call(
            FunctionId.Add, IrBuilder.I64(true), IrBuilder.Ref(emptyRow, 1), IrBuilder.Lit(1L));
        Assert.Empty(await Runner.ProjectAsync(emptyExpr, Source, TestData.Empty));
    }

    [Fact]
    public void An_unsupported_kernel_is_rejected_at_compilation()
    {
        var row = Table.RowType();
        var expr = IrBuilder.Call(
            FunctionId.Sqrt, IrBuilder.Fp64(true), IrBuilder.Ref(row, 2));
        var plan = Runner.ProjectionPlan(expr, Table);

        var failure = Assert.Throws<UnsupportedFeatureException>(() => Runner.Compile(plan, Source));

        Assert.Contains("Sqrt", failure.Feature, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Modulus_on_floating_point_is_rejected_at_compilation()
    {
        var row = Table.RowType();
        var expr = IrBuilder.Call(
            FunctionId.Modulus, IrBuilder.Fp64(true), IrBuilder.Ref(row, 2), IrBuilder.Lit(2d));
        var plan = Runner.ProjectionPlan(expr, Table);

        Assert.Throws<UnsupportedFeatureException>(() => Runner.Compile(plan, Source));
    }
}

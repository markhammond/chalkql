using Chalk.Execution.Tests.Harness;
using Chalk.Ir;
using Chalk.Sources;
using Chalk.TestKit;

namespace Chalk.Execution.Tests;

/// <summary>The M1 cast matrix of <c>02-ir.md</c> §6, including what <c>CAST_FAILURE_NULL</c> changes.</summary>
public sealed class CastKernelTests
{
    private static readonly TestSource Source = TestData.Source(
        TestData.Numbers,
        TestData.Instants,
        TestData.Casts,
        TestData.Extremes,
        TestData.Rounding,
        TestData.Empty);

    public static TheoryData<int> BatchSizes() => TestData.BatchSizeData;

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Widening_and_narrowing_between_integers(int batchSize)
    {
        var row = TestData.Numbers.RowType();
        var widened = IrBuilder.Cast(IrBuilder.Ref(row, 0), IrBuilder.I64(true));

        var values = await Runner.ProjectAsync(widened, Source, TestData.Numbers, batchSize);

        Assert.Equal(1L, values[0]);
        Assert.Null(values[2]);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Narrowing_overflow_raises_or_yields_null(int batchSize)
    {
        var table = TestData.Extremes;
        var row = table.RowType();
        var strict = IrBuilder.Cast(IrBuilder.Ref(row, 0), IrBuilder.I32(true));
        var lenient = IrBuilder.Cast(IrBuilder.Ref(row, 0), IrBuilder.I32(true), CastFailure.Null);

        await Assert.ThrowsAsync<ExecutionException>(
            () => Runner.ProjectAsync(strict, Source, table, batchSize));

        var values = await Runner.ProjectAsync(lenient, Source, table, batchSize);
        Assert.Null(values[0]);    // long.MaxValue does not fit in I32
        Assert.Equal(1L, values[1]);
        Assert.Null(values[2]);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Floating_point_to_integer_truncates_towards_zero(int batchSize)
    {
        var row = TestData.Numbers.RowType();
        var expr = IrBuilder.Cast(IrBuilder.Ref(row, 2), IrBuilder.I64(true), CastFailure.Null);

        var values = await Runner.ProjectAsync(expr, Source, TestData.Numbers, batchSize);

        Assert.Equal(1L, values[0]);     // 1.5
        Assert.Equal(-2L, values[1]);    // -2.5 truncates towards zero, not down
        Assert.Null(values[3]);          // NaN cannot be an integer
        Assert.Null(values[6]);          // 1e308 does not fit
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Numeric_to_decimal_rounds_half_even_to_the_target_scale(int batchSize)
    {
        var row = TestData.Casts.RowType();
        var expr = IrBuilder.Cast(IrBuilder.Ref(row, 0), IrBuilder.Dec(18, 1, true));

        var values = await Runner.ProjectAsync(expr, Source, TestData.Casts, batchSize);

        Assert.Equal(0.2m, values[0]);    // 0.25 -> 0.2
        Assert.Equal(0.4m, values[1]);    // 0.35 -> 0.4
        Assert.Null(values[4]);
    }

    /// <summary>
    /// <b>Provenance.</b> The value list is grafted from <c>ikvmnet/calcite-dotnet</c>
    /// (Apache-2.0), <c>src/Apache.Calcite.Tests/Interop/JavaDecimalsTests.cs</c>; the values it
    /// asks about are taken and its interop mechanism is not, and every expected value here is
    /// Chalk's own (D163). See the repository's NOTICE.
    /// <para>
    /// The edges of the 128-bit decimal survive a cast that does not move them (§5 C): the
    /// widest whole number, the finest fraction, and 10^20, each read back at its own precision.
    /// </para>
    /// </summary>
    /// <remarks>
    /// A cast to the type a column already has is not a no-op in the executor — it still goes
    /// through the rescale kernel — so this is the cheapest way to ask whether the kernel can carry
    /// a value that uses every digit it has.
    /// </remarks>
    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task The_decimal_boundaries_survive_a_cast_to_their_own_type(int batchSize)
    {
        var row = TestData.Rounding.RowType();

        var whole = await Runner.ProjectAsync(
            IrBuilder.Cast(IrBuilder.Ref(row, 1), IrBuilder.Dec(29, 0, true)),
            Source, TestData.Rounding, batchSize);

        Assert.Equal(decimal.MaxValue, whole[0]);
        Assert.Equal(decimal.MinValue, whole[1]);
        Assert.Equal(TestData.TenToTheTwentieth, whole[2]);
        Assert.Equal(-TestData.TenToTheTwentieth, whole[3]);
        Assert.Null(whole[4]);

        var fine = await Runner.ProjectAsync(
            IrBuilder.Cast(IrBuilder.Ref(row, 2), IrBuilder.Dec(29, 28, true)),
            Source, TestData.Rounding, batchSize);

        Assert.Equal(TestData.LargestAtScale28, fine[0]);
        Assert.Equal(-TestData.SmallestAtScale28, fine[1]);
        Assert.Equal(TestData.SmallestAtScale28, fine[2]);
    }

    /// <summary>
    /// Half-even at the 29th significant digit, resolving both ways (§5 C): one tie whose last kept
    /// digit is odd and rounds up, one whose last kept digit is even and rounds down, both landing
    /// on the same even digit. Away-from-zero would have given the first ...568 and the second
    /// ...569, so the two together are what tells the rules apart.
    /// </summary>
    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task A_tie_at_the_twenty_ninth_digit_resolves_to_even_in_both_directions(
        int batchSize)
    {
        var row = TestData.Rounding.RowType();
        var narrowed = IrBuilder.Cast(IrBuilder.Ref(row, 2), IrBuilder.Dec(28, 27, true));

        var values = await Runner.ProjectAsync(narrowed, Source, TestData.Rounding, batchSize);

        Assert.Equal(0.123456789012345678901234568m, values[3]);  // ...5675, up to even
        Assert.Equal(0.123456789012345678901234568m, values[4]);  // ...5685, down to even

        // And the finest fraction there is disappears at a coarser scale rather than rounding to
        // anything: 1e-28 is below half of 1e-27.
        Assert.Equal(0m, values[2]);
    }

    /// <summary>
    /// A decimal that no 64-bit integer can hold is a cast failure, not a wrapped value (§5 C):
    /// 10^20 is about five times <see cref="long.MaxValue"/>, and <c>decimal.MaxValue</c> is nine
    /// orders past it.
    /// </summary>
    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task A_decimal_past_a_long_does_not_become_one(int batchSize)
    {
        var row = TestData.Rounding.RowType();
        var expr = IrBuilder.Cast(
            IrBuilder.Ref(row, 1), IrBuilder.I64(true), CastFailure.Null);

        var values = await Runner.ProjectAsync(expr, Source, TestData.Rounding, batchSize);

        Assert.Null(values[0]);   // decimal.MaxValue
        Assert.Null(values[1]);   // decimal.MinValue
        Assert.Null(values[2]);   // 10^20
        Assert.Null(values[4]);   // and the NULL is still a NULL

        await Assert.ThrowsAsync<ExecutionException>(() => Runner.ProjectAsync(
            IrBuilder.Cast(IrBuilder.Ref(row, 1), IrBuilder.I64(true)),
            Source, TestData.Rounding, batchSize));
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Numeric_and_boolean_render_as_strings(int batchSize)
    {
        var row = TestData.Numbers.RowType();
        var integer = IrBuilder.Cast(IrBuilder.Ref(row, 1), IrBuilder.Str(true));
        var real = IrBuilder.Cast(IrBuilder.Ref(row, 2), IrBuilder.Str(true));
        var boolean = IrBuilder.Cast(IrBuilder.Ref(row, 6), IrBuilder.Str(true));
        var number = IrBuilder.Cast(IrBuilder.Ref(row, 4), IrBuilder.Str(true));

        Assert.Equal("10", (await Runner.ProjectAsync(integer, Source, TestData.Numbers, batchSize))[0]);
        Assert.Equal("1.5", (await Runner.ProjectAsync(real, Source, TestData.Numbers, batchSize))[0]);
        Assert.Equal("true", (await Runner.ProjectAsync(boolean, Source, TestData.Numbers, batchSize))[0]);
        Assert.Equal("1.5000", (await Runner.ProjectAsync(number, Source, TestData.Numbers, batchSize))[0]);
        Assert.Null((await Runner.ProjectAsync(real, Source, TestData.Numbers, batchSize))[4]);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Strings_parse_with_surrounding_space_allowed(int batchSize)
    {
        var row = TestData.Casts.RowType();
        var integer = IrBuilder.Cast(IrBuilder.Ref(row, 1), IrBuilder.I64(true), CastFailure.Null);
        var real = IrBuilder.Cast(IrBuilder.Ref(row, 1), IrBuilder.Fp64(true), CastFailure.Null);
        var boolean = IrBuilder.Cast(IrBuilder.Ref(row, 2), IrBuilder.Bool(true), CastFailure.Null);

        var integers = await Runner.ProjectAsync(integer, Source, TestData.Casts, batchSize);
        var reals = await Runner.ProjectAsync(real, Source, TestData.Casts, batchSize);
        var booleans = await Runner.ProjectAsync(boolean, Source, TestData.Casts, batchSize);

        Assert.Equal(42L, integers[0]);      // "  42  "
        Assert.Equal(-7L, integers[1]);
        Assert.Null(integers[2]);            // "1.5" is not an integer literal
        Assert.Equal(1.5d, reals[2]);
        Assert.Null(integers[3]);            // "nope"
        Assert.Equal(true, booleans[0]);
        Assert.Equal(false, booleans[1]);
        Assert.Null(booleans[2]);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task A_failed_string_parse_raises_under_the_default_failure_mode(int batchSize)
    {
        var row = TestData.Casts.RowType();
        var expr = IrBuilder.Cast(IrBuilder.Ref(row, 1), IrBuilder.I64(true));

        await Assert.ThrowsAsync<ExecutionException>(
            () => Runner.ProjectAsync(expr, Source, TestData.Casts, batchSize));
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Strings_parse_into_the_temporal_kinds(int batchSize)
    {
        var row = TestData.Casts.RowType();
        var date = IrBuilder.Cast(IrBuilder.Ref(row, 3), IrBuilder.Date(true), CastFailure.Null);
        var timestamp = IrBuilder.Cast(IrBuilder.Ref(row, 4), IrBuilder.Timestamp(6, true), CastFailure.Null);

        var dates = await Runner.ProjectAsync(date, Source, TestData.Casts, batchSize);
        var timestamps = await Runner.ProjectAsync(timestamp, Source, TestData.Casts, batchSize);

        Assert.Equal(20456L, dates[0]);                       // 2026-01-03
        Assert.Equal(1_767_413_106_000_000L, timestamps[0]);  // 2026-01-03 04:05:06
        Assert.Null(dates[3]);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Timestamp_and_date_convert_both_ways(int batchSize)
    {
        var row = TestData.Instants.RowType();
        var toDate = IrBuilder.Cast(IrBuilder.Ref(row, 1), IrBuilder.Date(true));
        var toTimestamp = IrBuilder.Cast(IrBuilder.Ref(row, 0), IrBuilder.Timestamp(6, true));

        var dates = await Runner.ProjectAsync(toDate, Source, TestData.Instants, batchSize);
        var timestamps = await Runner.ProjectAsync(toTimestamp, Source, TestData.Instants, batchSize);

        Assert.Equal(20456L, dates[0]);
        Assert.Equal(-1L, dates[1]);                          // truncation moves towards the past
        Assert.Equal(1_767_398_400_000_000L, timestamps[0]);  // midnight
        Assert.Null(dates[2]);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Timestamp_precision_rescales_and_the_zone_is_the_identity(int batchSize)
    {
        var row = TestData.Instants.RowType();
        var coarser = IrBuilder.Cast(IrBuilder.Ref(row, 1), IrBuilder.Timestamp(3, true));
        var zoned = IrBuilder.Cast(IrBuilder.Ref(row, 1), IrBuilder.TimestampTz(6, true));

        var coarse = await Runner.ProjectAsync(coarser, Source, TestData.Instants, batchSize);
        var utc = await Runner.ProjectAsync(zoned, Source, TestData.Instants, batchSize);

        Assert.Equal(1_767_413_106_000L, coarse[0]);
        Assert.Equal(-1L, coarse[1]);                          // -1 µs floors to -1 ms
        Assert.Equal(1_767_413_106_000_007L, utc[0]);          // the wall clock is taken as UTC
    }

    [Fact]
    public void An_unsupported_cast_pair_is_rejected_at_compilation()
    {
        var row = TestData.Numbers.RowType();
        var expr = IrBuilder.Cast(IrBuilder.Ref(row, 5), IrBuilder.Uuid(true));
        var plan = Runner.ProjectionPlan(expr, TestData.Numbers);

        var failure = Assert.Throws<UnsupportedFeatureException>(() => Runner.Compile(plan, Source));

        Assert.Contains("CAST", failure.Feature, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cast_that_changes_only_nullability_is_a_pass_through()
    {
        var row = TestData.Numbers.RowType();
        var expr = IrBuilder.Cast(IrBuilder.Ref(row, 0), IrBuilder.I32(true));

        var values = await Runner.ProjectAsync(expr, Source, TestData.Numbers);

        Assert.Equal(1L, values[0]);
        Assert.Null(values[2]);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Both_engines_agree_on_the_cast_matrix(int batchSize)
    {
        var numbers = TestData.Numbers.RowType();
        foreach (var expr in new[]
                 {
                     IrBuilder.Cast(IrBuilder.Ref(numbers, 0), IrBuilder.I64(true)),
                     IrBuilder.Cast(IrBuilder.Ref(numbers, 1), IrBuilder.Fp64(true)),
                     IrBuilder.Cast(IrBuilder.Ref(numbers, 2), IrBuilder.Fp32(true)),
                     IrBuilder.Cast(IrBuilder.Ref(numbers, 2), IrBuilder.I64(true), CastFailure.Null),
                     IrBuilder.Cast(IrBuilder.Ref(numbers, 4), IrBuilder.Fp64(true)),
                     IrBuilder.Cast(IrBuilder.Ref(numbers, 1), IrBuilder.Dec(18, 2, true)),
                     IrBuilder.Cast(IrBuilder.Ref(numbers, 1), IrBuilder.Str(true)),
                     IrBuilder.Cast(IrBuilder.Ref(numbers, 6), IrBuilder.Str(true)),
                 })
        {
            var vectorised = await Runner.ProjectAsync(expr, Source, TestData.Numbers, batchSize);
            var expected = await Runner.ProjectAsync(
                expr, Source, TestData.Numbers, batchSize, reference: true);
            Assert.Equal(expected, vectorised);
        }

        var instants = TestData.Instants.RowType();
        foreach (var expr in new[]
                 {
                     IrBuilder.Cast(IrBuilder.Ref(instants, 1), IrBuilder.Date(true)),
                     IrBuilder.Cast(IrBuilder.Ref(instants, 0), IrBuilder.Timestamp(6, true)),
                     IrBuilder.Cast(IrBuilder.Ref(instants, 1), IrBuilder.Timestamp(3, true)),
                     IrBuilder.Cast(IrBuilder.Ref(instants, 1), IrBuilder.TimestampTz(6, true)),
                 })
        {
            var vectorised = await Runner.ProjectAsync(expr, Source, TestData.Instants, batchSize);
            var expected = await Runner.ProjectAsync(
                expr, Source, TestData.Instants, batchSize, reference: true);
            Assert.Equal(expected, vectorised);
        }
    }
}

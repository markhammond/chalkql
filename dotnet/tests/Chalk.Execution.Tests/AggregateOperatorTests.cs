using Chalk.Catalog;
using Chalk.Execution.Tests.Harness;
using Chalk.Ir;
using Chalk.Sources;
using Chalk.TestKit;

namespace Chalk.Execution.Tests;

/// <summary>
/// The hash aggregate of <c>04-client.md</c> §6.6 and the semantics <c>02-ir.md</c> §4 fixes: NULL
/// keys form one group, a global aggregate over empty input is exactly one row, and <c>distinct</c>
/// and measure <c>filter</c> both narrow what a measure sees.
/// </summary>
public sealed class AggregateOperatorTests
{
    private static readonly TestTable Trades = TestData.Trades;
    private static readonly TestSource Source = TestData.Source(
        Trades, TestData.Numbers, TestData.Series(20), TestData.Empty);

    public static TheoryData<int> BatchSizes() => TestData.BatchSizeData;

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Grouping_puts_all_nulls_in_one_group(int batchSize)
    {
        var row = Trades.RowType();
        var plan = IrBuilder.Plan(IrBuilder.HashAggregate(
            IrBuilder.Read(Trades.Name, row),
            [0],
            [("n", IrBuilder.Agg(AggregateFunctionId.Count, IrBuilder.I64()))]));

        var rows = await Runner.RowsAsync(plan, Source, batchSize);

        Assert.Equal(3, rows.Count);                                   // AAA, BBB and one NULL group
        Assert.Single(rows, r => r[0] is null);
        Assert.Equal(2L, rows.Single(r => r[0] is null)[1]);
        Assert.Equal(3L, rows.Single(r => (r[0] as string) == "AAA")[1]);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Groups_come_out_in_first_seen_order(int batchSize)
    {
        var row = Trades.RowType();
        var plan = IrBuilder.Plan(IrBuilder.HashAggregate(
            IrBuilder.Read(Trades.Name, row),
            [0],
            [("n", IrBuilder.Agg(AggregateFunctionId.Count, IrBuilder.I64()))]));

        var rows = await Runner.RowsAsync(plan, Source, batchSize);

        Assert.Equal(["AAA", "BBB", null], rows.Select(r => r[0]));
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task A_global_aggregate_over_empty_input_is_exactly_one_row(int batchSize)
    {
        var row = TestData.Empty.RowType();
        var plan = IrBuilder.Plan(IrBuilder.HashAggregate(
            IrBuilder.Read(TestData.Empty.Name, row),
            [],
            [
                ("count_star", IrBuilder.Agg(AggregateFunctionId.Count, IrBuilder.I64())),
                ("count_col", IrBuilder.Agg(
                    AggregateFunctionId.Count, IrBuilder.I64(), IrBuilder.Ref(row, 1))),
                ("sum", IrBuilder.Agg(
                    AggregateFunctionId.Sum, IrBuilder.I64(true), IrBuilder.Ref(row, 1))),
                ("sum0", IrBuilder.Agg(
                    AggregateFunctionId.Sum0, IrBuilder.I64(), IrBuilder.Ref(row, 1))),
                ("min", IrBuilder.Agg(
                    AggregateFunctionId.Min, IrBuilder.I64(true), IrBuilder.Ref(row, 1))),
                ("max", IrBuilder.Agg(
                    AggregateFunctionId.Max, IrBuilder.I64(true), IrBuilder.Ref(row, 1))),
            ]));

        var rows = await Runner.RowsAsync(plan, Source, batchSize);

        Assert.Single(rows);
        Assert.Equal(0L, rows[0][0]);
        Assert.Equal(0L, rows[0][1]);
        Assert.Null(rows[0][2]);      // SUM of nothing is NULL
        Assert.Equal(0L, rows[0][3]); // SUM0 of nothing is zero
        Assert.Null(rows[0][4]);
        Assert.Null(rows[0][5]);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task An_all_null_group_sums_to_null_and_counts_to_zero(int batchSize)
    {
        var row = Trades.RowType();
        var plan = IrBuilder.Plan(IrBuilder.HashAggregate(
            IrBuilder.Read(Trades.Name, row),
            [0],
            [
                ("n", IrBuilder.Agg(
                    AggregateFunctionId.Count, IrBuilder.I64(), IrBuilder.Ref(row, 2))),
                ("s", IrBuilder.Agg(
                    AggregateFunctionId.Sum, IrBuilder.Fp64(true), IrBuilder.Ref(row, 2))),
            ]));

        var rows = await Runner.RowsAsync(plan, Source, batchSize);
        var aaa = rows.Single(r => (r[0] as string) == "AAA");

        Assert.Equal(2L, aaa[1]);       // one of the three AAA prices is NULL
        Assert.Equal(8.0d, aaa[2]);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Min_and_max_cover_the_layouts(int batchSize)
    {
        var row = Trades.RowType();
        var plan = IrBuilder.Plan(IrBuilder.HashAggregate(
            IrBuilder.Read(Trades.Name, row),
            [],
            [
                ("min_symbol", IrBuilder.Agg(
                    AggregateFunctionId.Min, IrBuilder.Str(true), IrBuilder.Ref(row, 0))),
                ("max_symbol", IrBuilder.Agg(
                    AggregateFunctionId.Max, IrBuilder.Str(true), IrBuilder.Ref(row, 0))),
                ("min_qty", IrBuilder.Agg(
                    AggregateFunctionId.Min, IrBuilder.I64(true), IrBuilder.Ref(row, 1))),
                ("max_price", IrBuilder.Agg(
                    AggregateFunctionId.Max, IrBuilder.Fp64(true), IrBuilder.Ref(row, 2))),
            ]));

        var rows = await Runner.RowsAsync(plan, Source, batchSize);

        Assert.Equal("AAA", rows[0][0]);
        Assert.Equal("BBB", rows[0][1]);
        Assert.Equal(1L, rows[0][2]);
        Assert.Equal(7.0d, rows[0][3]);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task A_distinct_measure_deduplicates_within_the_group(int batchSize)
    {
        var row = Trades.RowType();
        var plan = IrBuilder.Plan(IrBuilder.HashAggregate(
            IrBuilder.Read(Trades.Name, row),
            [0],
            [
                ("n", IrBuilder.Agg(
                    AggregateFunctionId.Count, IrBuilder.I64(), IrBuilder.Ref(row, 1), distinct: true)),
                ("s", IrBuilder.Agg(
                    AggregateFunctionId.Sum, IrBuilder.I64(true), IrBuilder.Ref(row, 1), distinct: true)),
            ]));

        var rows = await Runner.RowsAsync(plan, Source, batchSize);
        var aaa = rows.Single(r => (r[0] as string) == "AAA");

        // AAA's quantities are 1, 3 and 1: two distinct values summing to 4.
        Assert.Equal(2L, aaa[1]);
        Assert.Equal(4L, aaa[2]);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task A_distinct_count_over_the_whole_table(int batchSize)
    {
        var row = Trades.RowType();
        var plan = IrBuilder.Plan(IrBuilder.HashAggregate(
            IrBuilder.Read(Trades.Name, row),
            [],
            [
                ("symbols", IrBuilder.Agg(
                    AggregateFunctionId.Count, IrBuilder.I64(), IrBuilder.Ref(row, 0), distinct: true)),
            ]));

        var rows = await Runner.RowsAsync(plan, Source, batchSize);

        Assert.Equal(2L, rows[0][0]);   // AAA and BBB; the NULLs do not count
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task A_measure_filter_restricts_what_that_measure_sees(int batchSize)
    {
        var row = Trades.RowType();
        var plan = IrBuilder.Plan(IrBuilder.HashAggregate(
            IrBuilder.Read(Trades.Name, row),
            [],
            [
                ("all", IrBuilder.Agg(AggregateFunctionId.Count, IrBuilder.I64())),
                ("live", IrBuilder.Agg(
                    AggregateFunctionId.Count, IrBuilder.I64(), filter: IrBuilder.Ref(row, 3))),
                ("live_qty", IrBuilder.Agg(
                    AggregateFunctionId.Sum,
                    IrBuilder.I64(true),
                    IrBuilder.Ref(row, 1),
                    filter: IrBuilder.Ref(row, 3))),
            ]));

        var rows = await Runner.RowsAsync(plan, Source, batchSize);

        Assert.Equal(7L, rows[0][0]);
        Assert.Equal(5L, rows[0][1]);
        Assert.Equal(9L, rows[0][2]);   // 1 + 3 + 4 + 1 ; the NULL quantity contributes nothing
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Distinct_with_no_grouping_keys_over_a_multi_key_grouping(int batchSize)
    {
        var row = Trades.RowType();
        var plan = IrBuilder.Plan(IrBuilder.HashAggregate(
            IrBuilder.Read(Trades.Name, row), [0, 3], []));

        var rows = await Runner.RowsAsync(plan, Source, batchSize);

        // (AAA,true) (BBB,false) (NULL,true) (BBB,true) (NULL,false)
        Assert.Equal(5, rows.Count);
        Assert.Equal(2, rows[0].Length);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task A_logical_aggregate_executes_as_a_hash_aggregate(int batchSize)
    {
        var row = Trades.RowType();
        var logical = IrBuilder.Plan(IrBuilder.Aggregate(
            IrBuilder.Read(Trades.Name, row),
            [0],
            [("n", IrBuilder.Agg(AggregateFunctionId.Count, IrBuilder.I64()))]));
        var physical = IrBuilder.Plan(IrBuilder.HashAggregate(
            IrBuilder.Read(Trades.Name, row),
            [0],
            [("n", IrBuilder.Agg(AggregateFunctionId.Count, IrBuilder.I64()))]));

        var fromLogical = await Runner.RowsAsync(logical, Source, batchSize);
        var fromPhysical = await Runner.RowsAsync(physical, Source, batchSize);

        Assert.Equal(fromPhysical.Select(r => r[1]), fromLogical.Select(r => r[1]));
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Grouping_by_a_floating_point_key_folds_NaN_and_signed_zero(int batchSize)
    {
        var row = TestData.Numbers.RowType();
        var plan = IrBuilder.Plan(IrBuilder.HashAggregate(
            IrBuilder.Read(TestData.Numbers.Name, row),
            [2],
            [("n", IrBuilder.Agg(AggregateFunctionId.Count, IrBuilder.I64()))]));

        var rows = await Runner.RowsAsync(plan, Source, batchSize);
        var zero = rows.Single(r => r[0] is double d && d == 0d);

        // 0.0 and -0.0 are one group, and every NaN is one group.
        Assert.Equal(2L, zero[1]);
        Assert.Single(rows, r => r[0] is double d && double.IsNaN(d));
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Many_groups_grow_the_hash_table(int batchSize)
    {
        var table = TestData.Series(20);
        var row = table.RowType();
        var plan = IrBuilder.Plan(IrBuilder.HashAggregate(
            IrBuilder.Read(table.Name, row),
            [0],
            [("n", IrBuilder.Agg(AggregateFunctionId.Count, IrBuilder.I64()))]));

        var rows = await Runner.RowsAsync(plan, Source, batchSize);

        Assert.Equal(20, rows.Count);
        Assert.All(rows, r => Assert.Equal(1L, r[1]));
    }

    [Fact]
    public void Avg_is_rejected_at_compilation()
    {
        var row = Trades.RowType();
        var plan = IrBuilder.Plan(IrBuilder.HashAggregate(
            IrBuilder.Read(Trades.Name, row),
            [0],
            [("a", IrBuilder.Agg(
                AggregateFunctionId.Avg, IrBuilder.Fp64(true), IrBuilder.Ref(row, 2)))]));

        var failure = Assert.Throws<UnsupportedFeatureException>(() => Runner.Compile(plan, Source));

        Assert.Equal("AVG", failure.Feature);
    }

    [Fact]
    public void An_unimplemented_aggregate_is_rejected_at_compilation()
    {
        var row = Trades.RowType();
        var plan = IrBuilder.Plan(IrBuilder.HashAggregate(
            IrBuilder.Read(Trades.Name, row),
            [0],
            [("a", IrBuilder.Agg(
                AggregateFunctionId.BoolOr, IrBuilder.Bool(true), IrBuilder.Ref(row, 3)))]));

        Assert.Throws<UnsupportedFeatureException>(() => Runner.Compile(plan, Source));
    }

    /// <summary>
    /// Keys chosen to break a sixteen-byte key image (D255) if it were trusted on its own: two keys
    /// that agree on their first sixteen bytes and differ after them, a sixteen-byte key against the
    /// seventeen-byte one it is a prefix of, and the empty string beside a NULL — whose image length
    /// is the one value a real key cannot have.
    /// </summary>
    private static readonly TestTable ImageKeys = new()
    {
        Name = "image_keys",
        Columns = [("k", ChalkType.String(nullable: true)), ("n", ChalkType.Int64())],
        Rows =
        [
            ["0123456789abcdef", 1L],                    // exactly sixteen
            ["0123456789abcdefg", 2L],                   // seventeen, the same first sixteen
            ["0123456789abcdefh", 4L],                   // seventeen, differing in the last byte only
            ["0123456789abcdef", 8L],
            [string.Empty, 16L],
            [null, 32L],
            ["0123456789abcdefg", 64L],
            [null, 128L],
            [string.Empty, 256L],
            ["short", 512L],
        ],
    };

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Keys_longer_than_their_image_are_still_distinct_groups(int batchSize)
    {
        var source = TestData.Source(ImageKeys);
        var row = ImageKeys.RowType();
        var plan = IrBuilder.Plan(IrBuilder.HashAggregate(
            IrBuilder.Read(ImageKeys.Name, row),
            [0],
            [("s", IrBuilder.Agg(
                AggregateFunctionId.Sum, IrBuilder.I64(true), IrBuilder.Ref(row, 1)))]));

        var rows = await Runner.RowsAsync(plan, source, batchSize);

        Assert.Equal(6, rows.Count);
        Assert.Equal(9L, rows.Single(r => (r[0] as string) == "0123456789abcdef")[1]);
        Assert.Equal(66L, rows.Single(r => (r[0] as string) == "0123456789abcdefg")[1]);
        Assert.Equal(4L, rows.Single(r => (r[0] as string) == "0123456789abcdefh")[1]);
        Assert.Equal(272L, rows.Single(r => (r[0] as string) == string.Empty)[1]);
        Assert.Equal(160L, rows.Single(r => r[0] is null)[1]);
        Assert.Equal(512L, rows.Single(r => (r[0] as string) == "short")[1]);
    }

    [Fact]
    public async Task Keys_longer_than_their_image_agree_with_the_reference_engine()
    {
        var source = TestData.Source(ImageKeys);
        var row = ImageKeys.RowType();
        var plan = IrBuilder.Plan(IrBuilder.HashAggregate(
            IrBuilder.Read(ImageKeys.Name, row),
            [0],
            [
                ("n", IrBuilder.Agg(AggregateFunctionId.Count, IrBuilder.I64())),
                ("s", IrBuilder.Agg(
                    AggregateFunctionId.Sum, IrBuilder.I64(true), IrBuilder.Ref(row, 1))),
            ]));

        await Runner.AssertEnginesAgreeAsync(plan, source);
    }

    /// <summary>
    /// The typed accumulate kernels of D255 against the reference executor, over the layouts they
    /// cover and the shapes that narrow them: NULL arguments, a measure <c>FILTER</c> resolved to a
    /// mask, and a grouping key of each fixed width.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task The_accumulate_kernels_agree_with_the_reference_engine(int key)
    {
        var table = TestData.Numbers;
        var source = TestData.Source(table);
        var row = table.RowType();
        var live = IrBuilder.Ref(row, 6);
        var plan = IrBuilder.Plan(IrBuilder.HashAggregate(
            IrBuilder.Read(table.Name, row),
            [key],
            [
                ("n", IrBuilder.Agg(AggregateFunctionId.Count, IrBuilder.I64())),
                ("n_i32", IrBuilder.Agg(
                    AggregateFunctionId.Count, IrBuilder.I64(), IrBuilder.Ref(row, 0))),
                ("sum_i32", IrBuilder.Agg(
                    AggregateFunctionId.Sum, IrBuilder.I64(true), IrBuilder.Ref(row, 0))),
                ("sum0_i64", IrBuilder.Agg(
                    AggregateFunctionId.Sum0, IrBuilder.I64(), IrBuilder.Ref(row, 1))),
                ("sum_f64", IrBuilder.Agg(
                    AggregateFunctionId.Sum, IrBuilder.Fp64(true), IrBuilder.Ref(row, 2))),
                ("sum_dec", IrBuilder.Agg(
                    AggregateFunctionId.Sum, IrBuilder.Dec(18, 4, nullable: true), IrBuilder.Ref(row, 4))),
                ("min_i64", IrBuilder.Agg(
                    AggregateFunctionId.Min, IrBuilder.I64(true), IrBuilder.Ref(row, 1))),
                ("max_f64", IrBuilder.Agg(
                    AggregateFunctionId.Max, IrBuilder.Fp64(true), IrBuilder.Ref(row, 2))),
                ("min_dec", IrBuilder.Agg(
                    AggregateFunctionId.Min, IrBuilder.Dec(18, 4, nullable: true), IrBuilder.Ref(row, 4))),
                ("live_sum_f64", IrBuilder.Agg(
                    AggregateFunctionId.Sum, IrBuilder.Fp64(true), IrBuilder.Ref(row, 2), filter: live)),
                ("live_max_i32", IrBuilder.Agg(
                    AggregateFunctionId.Max, IrBuilder.I32(true), IrBuilder.Ref(row, 0), filter: live)),
            ]));

        await Runner.AssertEnginesAgreeAsync(plan, source);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Integer_sum_overflow_raises(int batchSize)
    {
        var table = TestData.Extremes;
        var source = TestData.Source(table);
        var row = table.RowType();
        var plan = IrBuilder.Plan(IrBuilder.HashAggregate(
            IrBuilder.Read(table.Name, row),
            [],
            [("s", IrBuilder.Agg(
                AggregateFunctionId.Sum, IrBuilder.I64(true), IrBuilder.Ref(row, 0)))]));

        await Assert.ThrowsAsync<ExecutionException>(
            () => Runner.RowsAsync(plan, source, batchSize));
    }
}

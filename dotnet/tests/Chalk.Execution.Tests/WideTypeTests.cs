using Chalk.Execution.Tests.Harness;
using Chalk.Ir;
using Chalk.TestKit;

namespace Chalk.Execution.Tests;

/// <summary>
/// The layouts the numeric and string fixtures do not reach: BINARY, UUID, TIME, INTERVAL_DAY and
/// DECIMAL, driven through comparison, ordering, grouping and MIN/MAX so that every one of the
/// <c>02-ir.md</c> §3 kinds is executed at least once.
/// </summary>
public sealed class WideTypeTests
{
    private static readonly TestTable Table = TestData.Assorted;
    private static readonly TestSource Source = TestData.Source(Table, TestData.Numbers);

    public static TheoryData<int> BatchSizes() => TestData.BatchSizeData;

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Binary_and_uuid_compare_by_byte_order(int batchSize)
    {
        var row = Table.RowType();
        var binary = IrBuilder.Call(
            FunctionId.Eq,
            IrBuilder.Bool(true),
            IrBuilder.Ref(row, 4),
            IrBuilder.LitBytes(1, 2, 3));
        var uuid = IrBuilder.Call(
            FunctionId.Gt,
            IrBuilder.Bool(true),
            IrBuilder.Ref(row, 5),
            IrBuilder.LitUuid(Guid.Empty));

        var binaries = await Runner.ProjectAsync(binary, Source, Table, batchSize);
        var uuids = await Runner.ProjectAsync(uuid, Source, Table, batchSize);

        Assert.Equal(true, binaries[0]);
        Assert.Equal(false, binaries[1]);
        Assert.Null(binaries[2]);
        Assert.Equal(true, uuids[0]);
        Assert.Null(uuids[2]);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Time_compares_as_a_microsecond_count(int batchSize)
    {
        var row = Table.RowType();
        var time = IrBuilder.Call(
            FunctionId.Ge, IrBuilder.Bool(true), IrBuilder.Ref(row, 3), IrBuilder.LitTime(1L));

        var times = await Runner.ProjectAsync(time, Source, Table, batchSize);

        Assert.Equal(true, times[0]);
        Assert.Equal(false, times[1]);
        Assert.Null(times[2]);
    }

    [Fact]
    public void Comparing_intervals_is_refused_at_compilation()
    {
        // 02-ir.md §6 puts the INTERVAL kinds outside the comparison signature.
        var row = Table.RowType();
        var expr = IrBuilder.Call(
            FunctionId.Lt, IrBuilder.Bool(true), IrBuilder.Ref(row, 6), IrBuilder.LitIntervalDay(0L));
        var plan = Runner.ProjectionPlan(expr, Table);

        var failure = Assert.Throws<Chalk.Sources.UnsupportedFeatureException>(
            () => Runner.Compile(plan, Source));

        Assert.Contains("INTERVAL_DAY", failure.Feature, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Sorting_by_a_uuid_and_by_binary(int batchSize)
    {
        var row = Table.RowType();
        var read = IrBuilder.Read(Table.Name, row);

        var byUuid = IrBuilder.Plan(IrBuilder.Sort(read, IrBuilder.Desc(5, row.Fields[5].Type)));
        var byBinary = IrBuilder.Plan(IrBuilder.Sort(read, IrBuilder.Asc(4, row.Fields[4].Type)));

        var uuids = await Runner.RowsAsync(byUuid, Source, batchSize);
        var binaries = await Runner.RowsAsync(byBinary, Source, batchSize);

        Assert.Null(uuids[0][5]);                                   // DESC NULLS FIRST
        Assert.Equal(new byte[] { }, binaries[0][4]);               // the empty value sorts first
        Assert.Null(binaries[^1][4]);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Grouping_by_a_uuid_and_a_binary_key(int batchSize)
    {
        var row = Table.RowType();
        foreach (var key in new[] { 4, 5, 3, 6 })
        {
            var plan = IrBuilder.Plan(IrBuilder.HashAggregate(
                IrBuilder.Read(Table.Name, row),
                [key],
                [("n", IrBuilder.Agg(AggregateFunctionId.Count, IrBuilder.I64()))]));

            var rows = await Runner.RowsAsync(plan, Source, batchSize);

            Assert.Equal(3, rows.Count);
            Assert.All(rows, r => Assert.Equal(1L, r[1]));
        }
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Min_and_max_over_binary_and_decimal(int batchSize)
    {
        var row = Table.RowType();
        var plan = IrBuilder.Plan(IrBuilder.HashAggregate(
            IrBuilder.Read(Table.Name, row),
            [],
            [
                ("min_bin", IrBuilder.Agg(
                    AggregateFunctionId.Min, IrBuilder.Binary(true), IrBuilder.Ref(row, 4))),
                ("max_bin", IrBuilder.Agg(
                    AggregateFunctionId.Max, IrBuilder.Binary(true), IrBuilder.Ref(row, 4))),
                ("max_uuid", IrBuilder.Agg(
                    AggregateFunctionId.Max, IrBuilder.Uuid(true), IrBuilder.Ref(row, 5))),
            ]));

        var rows = await Runner.RowsAsync(plan, Source, batchSize);

        Assert.Equal(Array.Empty<byte>(), rows[0][0]);
        Assert.Equal(new byte[] { 1, 2, 3 }, rows[0][1]);
        Assert.NotNull(rows[0][2]);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Decimal_grouping_and_extremes(int batchSize)
    {
        var row = TestData.Numbers.RowType();
        var plan = IrBuilder.Plan(IrBuilder.HashAggregate(
            IrBuilder.Read(TestData.Numbers.Name, row),
            [4],
            [
                ("n", IrBuilder.Agg(AggregateFunctionId.Count, IrBuilder.I64())),
                ("lo", IrBuilder.Agg(
                    AggregateFunctionId.Min, IrBuilder.Dec(18, 4, true), IrBuilder.Ref(row, 4))),
            ]));

        var rows = await Runner.RowsAsync(plan, Source, batchSize);

        // 1.5, -2.5, 3.5, 0.0 (twice), NULL, -0.0005, 12345.6789 and 2.5
        Assert.Equal(8, rows.Count);
        Assert.Equal(2L, rows.Single(r => r[0] is decimal d && d == 0m)[1]);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task A_boolean_column_round_trips_through_sort_and_output(int batchSize)
    {
        var row = TestData.Numbers.RowType();
        var read = IrBuilder.Read(TestData.Numbers.Name, row);
        var plan = IrBuilder.Plan(IrBuilder.Sort(
            read, IrBuilder.Asc(6, row.Fields[6].Type), IrBuilder.Asc(0, row.Fields[0].Type)));

        var rows = await Runner.RowsAsync(plan, Source, batchSize);

        Assert.Equal(false, rows[0][6]);
        Assert.Null(rows[^1][6]);
        Assert.Equal(TestData.Numbers.Rows.Count, rows.Count);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Fetch_then_project_moves_ownership_through_a_shared_slice(int batchSize)
    {
        var row = Table.RowType();
        var fetch = IrBuilder.Fetch(IrBuilder.Read(Table.Name, row), offset: 1, count: 2);
        var project = IrBuilder.Project(
            fetch,
            [
                ("uuid", IrBuilder.Ref(row, 5)),
                ("bin", IrBuilder.Ref(row, 4)),
            ]);

        var rows = await Runner.RowsAsync(IrBuilder.Plan(project), Source, batchSize);

        Assert.Equal(2, rows.Count);
        Assert.Equal(Array.Empty<byte>(), rows[0][1]);
        Assert.Null(rows[1][0]);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Selecting_the_same_column_twice_keeps_both_copies_alive(int batchSize)
    {
        var row = Table.RowType();
        var project = IrBuilder.Project(
            IrBuilder.Read(Table.Name, row),
            [("a", IrBuilder.Ref(row, 4)), ("b", IrBuilder.Ref(row, 4))]);

        var rows = await Runner.RowsAsync(IrBuilder.Plan(project), Source, batchSize);

        Assert.All(rows, r => Assert.Equal(r[0], r[1]));
        Assert.Equal(new byte[] { 1, 2, 3 }, rows[0][0]);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Both_engines_agree_over_the_wide_layouts(int batchSize)
    {
        var row = Table.RowType();
        var read = IrBuilder.Read(Table.Name, row);
        var sort = IrBuilder.Sort(read, IrBuilder.Asc(5, row.Fields[5].Type));
        var project = IrBuilder.Project(
            sort,
            [
                ("bin", IrBuilder.Ref(row, 4)),
                ("uuid", IrBuilder.Ref(row, 5)),
                ("t", IrBuilder.Ref(row, 3)),
                ("iv", IrBuilder.Ref(row, 6)),
                ("cmp", IrBuilder.Call(
                    FunctionId.IsNotDistinctFrom,
                    IrBuilder.Bool(),
                    IrBuilder.Ref(row, 4),
                    IrBuilder.LitBytes(1, 2, 3))),
            ]);

        var vectorised = await Runner.RunAsync(IrBuilder.Plan(project), Source, batchSize);
        var expected = await Runner.RunAsync(IrBuilder.Plan(project), Source, batchSize, reference: true);
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
}

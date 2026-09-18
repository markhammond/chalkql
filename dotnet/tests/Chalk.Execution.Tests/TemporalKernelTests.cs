using Chalk.Execution.Tests.Harness;
using Chalk.Ir;
using Chalk.Sources;
using Chalk.TestKit;

namespace Chalk.Execution.Tests;

/// <summary>
/// EXTRACT and FLOOR … TO, over the units <c>02-ir.md</c> §6 puts in M1. DOW counts Sunday as 0 and
/// EPOCH counts whole seconds; TIMESTAMP_TZ is read in UTC (A5).
/// </summary>
public sealed class TemporalKernelTests
{
    private static readonly TestTable Table = TestData.Instants;
    private static readonly TestSource Source = TestData.Source(Table, TestData.Empty);

    public static TheoryData<int> BatchSizes() => TestData.BatchSizeData;

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Extract_reads_every_M1_unit_from_a_timestamp(int batchSize)
    {
        var row = Table.RowType();

        // Row 0 is 2026-01-03T04:05:06.000007 — a Saturday, day 3 of the year.
        Assert.Equal(2026L, await First("YEAR", batchSize));
        Assert.Equal(1L, await First("MONTH", batchSize));
        Assert.Equal(3L, await First("DAY", batchSize));
        Assert.Equal(4L, await First("HOUR", batchSize));
        Assert.Equal(5L, await First("MINUTE", batchSize));
        Assert.Equal(6L, await First("SECOND", batchSize));
        Assert.Equal(6L, await First("DOW", batchSize));       // Saturday, with Sunday = 0
        Assert.Equal(3L, await First("DOY", batchSize));
        Assert.Equal(1_767_413_106L, await First("EPOCH", batchSize));

        async Task<object?> First(string unit, int size) =>
            (await Runner.ProjectAsync(Extract(unit, row, 1), Source, Table, size))[0];
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Extract_handles_pre_epoch_instants_and_nulls(int batchSize)
    {
        var row = Table.RowType();
        var values = await Runner.ProjectAsync(Extract("YEAR", row, 1), Source, Table, batchSize);

        Assert.Equal(2026L, values[0]);
        Assert.Equal(1969L, values[1]);   // one microsecond before the epoch
        Assert.Null(values[2]);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Extract_dow_covers_the_whole_week(int batchSize)
    {
        var table = TestData.Week;
        var row = table.RowType();
        var expr = IrBuilder.Call(
            FunctionId.Extract, IrBuilder.I64(true), IrBuilder.EnumArg("DOW"), IrBuilder.Ref(row, 0));

        var values = await Runner.ProjectAsync(expr, TestData.Source(table), table, batchSize);

        // 2026-01-04 is a Sunday, so the seven days run 0..6.
        Assert.Equal([0L, 1L, 2L, 3L, 4L, 5L, 6L], values);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Extract_from_a_date_column(int batchSize)
    {
        var row = Table.RowType();
        var values = await Runner.ProjectAsync(Extract("DAY", row, 0), Source, Table, batchSize);

        Assert.Equal(3L, values[0]);
        Assert.Equal(31L, values[1]);   // 1969-12-31
        Assert.Null(values[2]);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Floor_truncates_to_each_unit(int batchSize)
    {
        var row = Table.RowType();

        // 2026-01-03T04:05:06.000007 at microsecond precision.
        Assert.Equal(1_767_413_106_000_000L, await First("SECOND", batchSize));
        Assert.Equal(1_767_413_100_000_000L, await First("MINUTE", batchSize));
        Assert.Equal(1_767_412_800_000_000L, await First("HOUR", batchSize));
        Assert.Equal(1_767_398_400_000_000L, await First("DAY", batchSize));
        Assert.Equal(1_767_225_600_000_000L, await First("MONTH", batchSize));
        Assert.Equal(1_767_225_600_000_000L, await First("YEAR", batchSize));

        async Task<object?> First(string unit, int size) =>
            (await Runner.ProjectAsync(Floor(unit, row, 1), Source, Table, size))[0];
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Floor_of_a_pre_epoch_instant_moves_backwards(int batchSize)
    {
        var row = Table.RowType();
        var values = await Runner.ProjectAsync(Floor("DAY", row, 1), Source, Table, batchSize);

        // -1 microsecond is 1969-12-31T23:59:59.999999; the day floor is 1969-12-31T00:00:00.
        Assert.Equal(-86_400_000_000L, values[1]);
        Assert.Null(values[2]);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Floor_of_a_date_stays_a_date(int batchSize)
    {
        var row = Table.RowType();
        var values = await Runner.ProjectAsync(Floor("MONTH", row, 0), Source, Table, batchSize);

        Assert.Equal(20_454L, values[0]);   // 2026-01-01
        Assert.Equal(-31L, values[1]);      // 1969-12-01
    }

    [Fact]
    public void An_unsupported_extract_unit_is_rejected_at_compilation()
    {
        var row = Table.RowType();
        var plan = Runner.ProjectionPlan(Extract("WEEK", row, 1), Table);

        Assert.Throws<UnsupportedFeatureException>(() => Runner.Compile(plan, Source));
    }

    [Fact]
    public void Extract_over_a_non_temporal_column_is_rejected_at_compilation()
    {
        var row = TestData.Numbers.RowType();
        var expr = IrBuilder.Call(
            FunctionId.Extract, IrBuilder.I64(true), IrBuilder.EnumArg("YEAR"), IrBuilder.Ref(row, 1));
        var plan = Runner.ProjectionPlan(expr, TestData.Numbers);

        Assert.Throws<UnsupportedFeatureException>(
            () => Runner.Compile(plan, TestData.Source(TestData.Numbers)));
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Both_engines_agree_on_the_temporal_kernels(int batchSize)
    {
        var row = Table.RowType();
        foreach (var unit in new[] { "YEAR", "MONTH", "DAY", "HOUR", "MINUTE", "SECOND", "DOW", "DOY", "EPOCH" })
        {
            foreach (var column in new[] { 0, 1, 2 })
            {
                var expr = Extract(unit, row, column);
                var vectorised = await Runner.ProjectAsync(expr, Source, Table, batchSize);
                var expected = await Runner.ProjectAsync(expr, Source, Table, batchSize, reference: true);
                Assert.Equal(expected, vectorised);
            }
        }

        foreach (var unit in new[] { "YEAR", "MONTH", "DAY", "HOUR", "MINUTE", "SECOND" })
        {
            foreach (var column in new[] { 0, 1, 2 })
            {
                var expr = Floor(unit, row, column);
                var vectorised = await Runner.ProjectAsync(expr, Source, Table, batchSize);
                var expected = await Runner.ProjectAsync(expr, Source, Table, batchSize, reference: true);
                Assert.Equal(expected, vectorised);
            }
        }
    }

    private static Expr Extract(string unit, RowType row, int column) => IrBuilder.Call(
        FunctionId.Extract, IrBuilder.I64(true), IrBuilder.EnumArg(unit), IrBuilder.Ref(row, column));

    private static Expr Floor(string unit, RowType row, int column) => IrBuilder.Call(
        FunctionId.FloorTemporal,
        row.Fields[column].Type,
        IrBuilder.Ref(row, column),
        IrBuilder.EnumArg(unit));
}

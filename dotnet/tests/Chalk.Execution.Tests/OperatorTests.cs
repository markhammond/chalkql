using Chalk.Execution.Tests.Harness;
using Chalk.Ir;
using Chalk.Sources;
using Chalk.TestKit;

namespace Chalk.Execution.Tests;

/// <summary>
/// The M1 operators of <c>04-client.md</c> §6.3, each at <c>BatchSize</c> 1, 7 and 4096 so that a
/// batch-boundary bug has nowhere to hide (<c>05-testing.md</c> §1).
/// </summary>
public sealed class OperatorTests
{
    private static readonly TestTable Series = TestData.Series(20);
    private static readonly TestTable Sortable = TestData.Sortable;
    private static readonly TestSource Source = TestData.Source(
        Series, Sortable, TestData.Numbers, TestData.SingleRow, TestData.Empty);

    public static TheoryData<int> BatchSizes() => TestData.BatchSizeData;

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Scan_produces_every_row_and_counts_them(int batchSize)
    {
        var stats = new ExecutionStats();
        var plan = IrBuilder.Plan(IrBuilder.Read(Series.Name, Series.RowType()));

        var rows = await Runner.RowsAsync(plan, Source, batchSize, stats: stats);

        Assert.Equal(20, rows.Count);
        Assert.Equal(20, stats.RowsScanned);
        Assert.Equal(20, stats.RowsProduced);
        Assert.Equal(0L, rows[0][0]);
        Assert.Equal("row-19", rows[19][2]);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Scan_of_an_empty_table_produces_no_rows(int batchSize)
    {
        var plan = IrBuilder.Plan(IrBuilder.Read(TestData.Empty.Name, TestData.Empty.RowType()));

        Assert.Empty(await Runner.RowsAsync(plan, Source, batchSize));
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Scan_honours_the_projection(int batchSize)
    {
        var row = Series.RowType();
        var projected = IrBuilder.Row(row.Fields[2], row.Fields[0]);
        var plan = IrBuilder.Plan(IrBuilder.Read(Series.Name, projected, [2, 0]));

        var rows = await Runner.RowsAsync(plan, Source, batchSize);

        Assert.Equal("row-0", rows[0][0]);
        Assert.Equal(0L, rows[0][1]);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Values_materialises_literal_rows(int batchSize)
    {
        var rowType = IrBuilder.Row(
            IrBuilder.F("n", IrBuilder.I64()), IrBuilder.F("s", IrBuilder.Str(true)));
        var values = IrBuilder.Values(
            rowType,
            [IrBuilder.Lit(1L), IrBuilder.Nullable(IrBuilder.Lit("one"))],
            [IrBuilder.Lit(2L), IrBuilder.Null(IrBuilder.Str())]);

        var rows = await Runner.RowsAsync(IrBuilder.Plan(values), Source, batchSize);

        Assert.Equal(2, rows.Count);
        Assert.Equal(1L, rows[0][0]);
        Assert.Equal("one", rows[0][1]);
        Assert.Null(rows[1][1]);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Values_with_no_rows_produces_no_batches(int batchSize)
    {
        var rowType = IrBuilder.Row(IrBuilder.F("n", IrBuilder.I64()));
        var plan = IrBuilder.Plan(IrBuilder.Values(rowType));

        var batches = await Runner.RunAsync(plan, Source, batchSize);

        Assert.Empty(batches);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Filter_keeps_all_rows_none_or_some(int batchSize)
    {
        var row = Series.RowType();
        var read = IrBuilder.Read(Series.Name, row);

        var all = IrBuilder.Plan(IrBuilder.Filter(read, IrBuilder.Lit(true)));
        var none = IrBuilder.Plan(IrBuilder.Filter(read, IrBuilder.Lit(false)));
        var some = IrBuilder.Plan(IrBuilder.Filter(
            read,
            IrBuilder.Call(FunctionId.Lt, IrBuilder.Bool(), IrBuilder.Ref(row, 0), IrBuilder.Lit(5L))));

        Assert.Equal(20, (await Runner.RowsAsync(all, Source, batchSize)).Count);
        Assert.Empty(await Runner.RowsAsync(none, Source, batchSize));
        Assert.Equal(5, (await Runner.RowsAsync(some, Source, batchSize)).Count);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Filter_drops_null_as_well_as_false(int batchSize)
    {
        var row = TestData.Numbers.RowType();
        var read = IrBuilder.Read(TestData.Numbers.Name, row);
        var plan = IrBuilder.Plan(IrBuilder.Filter(read, IrBuilder.Ref(row, 6)));

        var rows = await Runner.RowsAsync(plan, Source, batchSize);

        // The bool column is TRUE four times, FALSE three times and NULL twice.
        Assert.Equal(4, rows.Count);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Filter_over_a_single_row_table(int batchSize)
    {
        var row = TestData.SingleRow.RowType();
        var read = IrBuilder.Read(TestData.SingleRow.Name, row);
        var plan = IrBuilder.Plan(IrBuilder.Filter(
            read,
            IrBuilder.Call(FunctionId.Eq, IrBuilder.Bool(), IrBuilder.Ref(row, 0), IrBuilder.Lit(7L))));

        Assert.Single(await Runner.RowsAsync(plan, Source, batchSize));
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Project_forwards_a_field_and_computes_the_rest(int batchSize)
    {
        var row = Series.RowType();
        var read = IrBuilder.Read(Series.Name, row);
        var project = IrBuilder.Project(
            read,
            [
                ("n", IrBuilder.Ref(row, 0)),
                ("twice", IrBuilder.Call(
                    FunctionId.Multiply, IrBuilder.I64(), IrBuilder.Ref(row, 0), IrBuilder.Lit(2L))),
                ("k", IrBuilder.Lit("k")),
            ]);

        var rows = await Runner.RowsAsync(IrBuilder.Plan(project), Source, batchSize);

        Assert.Equal(20, rows.Count);
        Assert.Equal(3L, rows[3][0]);
        Assert.Equal(6L, rows[3][1]);
        Assert.Equal("k", rows[3][2]);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Sort_places_nulls_at_the_end_the_direction_asks_for(int batchSize)
    {
        var row = Sortable.RowType();
        var read = IrBuilder.Read(Sortable.Name, row);

        var ascLast = IrBuilder.Plan(IrBuilder.Sort(read, IrBuilder.Asc(0, row.Fields[0].Type)));
        var ascFirst = IrBuilder.Plan(
            IrBuilder.Sort(read, IrBuilder.Asc(0, row.Fields[0].Type, nullsFirst: true)));
        var descFirst = IrBuilder.Plan(IrBuilder.Sort(read, IrBuilder.Desc(0, row.Fields[0].Type)));

        var last = await Runner.RowsAsync(ascLast, Source, batchSize);
        var first = await Runner.RowsAsync(ascFirst, Source, batchSize);
        var down = await Runner.RowsAsync(descFirst, Source, batchSize);

        Assert.Null(last[^1][0]);
        Assert.Equal(-5L, last[0][0]);
        Assert.Null(first[0][0]);
        Assert.Null(first[1][0]);       // the fixture has two NULLs
        Assert.Equal(-5L, first[2][0]);
        Assert.Null(down[0][0]);
        Assert.Equal(-5L, down[^1][0]);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Sort_on_a_single_non_null_key_uses_the_fast_path_and_still_sorts(int batchSize)
    {
        var row = Series.RowType();
        var read = IrBuilder.Read(Series.Name, row);
        var plan = IrBuilder.Plan(IrBuilder.Sort(read, IrBuilder.Desc(0, row.Fields[0].Type)));

        var rows = await Runner.RowsAsync(plan, Source, batchSize);

        Assert.Equal(19L, rows[0][0]);
        Assert.Equal(0L, rows[^1][0]);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Sort_by_two_keys_breaks_ties_with_the_second(int batchSize)
    {
        var row = Sortable.RowType();
        var read = IrBuilder.Read(Sortable.Name, row);
        var plan = IrBuilder.Plan(IrBuilder.Sort(
            read,
            IrBuilder.Asc(1, row.Fields[1].Type),
            IrBuilder.Desc(0, row.Fields[0].Type)));

        var rows = await Runner.RowsAsync(plan, Source, batchSize);

        // ASC on g, then DESC NULLS FIRST on n: the "a" group runs NULL, 7, -5.
        Assert.Equal("a", rows[0][1]);
        Assert.Null(rows[0][0]);
        Assert.Equal(7L, rows[1][0]);
        Assert.Equal(-5L, rows[2][0]);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Sort_of_an_empty_input_produces_nothing(int batchSize)
    {
        var row = TestData.Empty.RowType();
        var read = IrBuilder.Read(TestData.Empty.Name, row);
        var plan = IrBuilder.Plan(IrBuilder.Sort(read, IrBuilder.Asc(0, row.Fields[0].Type)));

        Assert.Empty(await Runner.RowsAsync(plan, Source, batchSize));
    }

    /// <summary>
    /// The contract <c>WindowOperator</c> carries too (ADR 0012 §2): a sort that runs out of budget
    /// while it is concatenating its input hands back the columns it did finish, so an aborted
    /// execution leaves the arena as empty as a finished one. The budget here is chosen so the
    /// refusal lands on a <em>later</em> column, which is the case that used to strand memory.
    /// </summary>
    [Fact]
    public async Task A_sort_that_breaks_MaxBytes_fails_naming_the_operator_and_strands_nothing()
    {
        var wide = TestData.Series(5_000);
        var source = TestData.Source(wide);
        var row = wide.RowType();
        var plan = IrBuilder.Plan(IrBuilder.Sort(
            IrBuilder.Read(wide.Name, row), IrBuilder.Asc(2, row.Fields[2].Type)));

        using var arena = new ExecutionArena(new ArenaOptions { MaxBytes = 200_000 });
        var compiled = Runner.Compile(plan, source, batchSize: 512);

        var failure = await Assert.ThrowsAsync<ExecutionException>(async () =>
        {
            await foreach (var batch in compiled.ExecuteAsync(
                [], new ExecutionStats(), arena, TestContext.Current.CancellationToken))
            {
                batch.Dispose();
            }
        });

        Assert.Contains("Sort", failure.OperatorPath, StringComparison.Ordinal);
        Assert.IsType<ArenaBudgetExceededException>(failure.InnerException);
        Assert.Equal(0, arena.OutstandingBytes);
    }

    /// <summary>
    /// The same contract on the leaf, and for the other way an execution can end early: a scan the
    /// caller cancels part-way hands back every rental it holds, so an aborted scan leaves the arena
    /// as empty as a finished one. The cancellation lands at two different points, and once with an
    /// operator above the scan, because the batch at risk is the one in flight when the token is
    /// noticed and where it is depends on who notices first.
    /// </summary>
    /// <remarks>
    /// The rentals a cancelled scan used to strand were the source's: a batch it had built but not
    /// yet yielded, whose buffers came from the scan's arena and whose ownership had not reached the
    /// operator that disposes it. <c>ISourceRuntime.ScanAsync</c> transfers ownership on the yield,
    /// so a source that throws between building a batch and yielding it disposes it first.
    /// </remarks>
    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public async Task A_cancelled_scan_hands_back_every_rental_it_holds(int batches, bool above)
    {
        // Four thousand rows at sixty-four to a batch: cancelling after the first or the second is a
        // long way from the end, so the scan is genuinely abandoned mid-stream.
        var table = TestData.Series(4_000);
        var row = table.RowType();
        var read = IrBuilder.Read(table.Name, row);
        var plan = above
            ? IrBuilder.Plan(IrBuilder.Project(read, [("v", IrBuilder.Ref(row, 1))]))
            : IrBuilder.Plan(read);

        using var arena = new ExecutionArena();
        var compiled = Runner.Compile(plan, TestData.Source(table), batchSize: 64);
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            var seen = 0;
            await foreach (var batch in compiled.ExecuteAsync(
                [], new ExecutionStats(), arena, cancellation.Token))
            {
                batch.Dispose();
                if (++seen == batches)
                {
                    await cancellation.CancelAsync();
                }
            }
        });

        Assert.Equal(0, arena.OutstandingBytes);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Fetch_spans_a_batch_boundary(int batchSize)
    {
        var row = Series.RowType();
        var read = IrBuilder.Read(Series.Name, row);
        var plan = IrBuilder.Plan(IrBuilder.Fetch(read, offset: 5, count: 9));

        var rows = await Runner.RowsAsync(plan, Source, batchSize);

        Assert.Equal(9, rows.Count);
        Assert.Equal(5L, rows[0][0]);
        Assert.Equal(13L, rows[^1][0]);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Fetch_without_a_count_and_past_the_end(int batchSize)
    {
        var row = Series.RowType();
        var read = IrBuilder.Read(Series.Name, row);

        var unbounded = IrBuilder.Plan(IrBuilder.Fetch(read, offset: 18, count: null));
        var beyond = IrBuilder.Plan(IrBuilder.Fetch(read, offset: 100, count: 5));
        var nothing = IrBuilder.Plan(IrBuilder.Fetch(read, offset: 0, count: 0));

        Assert.Equal(2, (await Runner.RowsAsync(unbounded, Source, batchSize)).Count);
        Assert.Empty(await Runner.RowsAsync(beyond, Source, batchSize));
        Assert.Empty(await Runner.RowsAsync(nothing, Source, batchSize));
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task TopN_with_an_offset_returns_the_middle_of_the_ordering(int batchSize)
    {
        var row = Series.RowType();
        var read = IrBuilder.Read(Series.Name, row);
        var plan = IrBuilder.Plan(
            IrBuilder.TopN(read, offset: 2, count: 3, IrBuilder.Desc(0, row.Fields[0].Type)));

        var rows = await Runner.RowsAsync(plan, Source, batchSize);

        Assert.Equal([17L, 16L, 15L], rows.Select(r => r[0]));
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task TopN_of_more_rows_than_exist_returns_them_all_in_order(int batchSize)
    {
        var row = Sortable.RowType();
        var read = IrBuilder.Read(Sortable.Name, row);
        var plan = IrBuilder.Plan(
            IrBuilder.TopN(read, offset: 0, count: 100, IrBuilder.Asc(0, row.Fields[0].Type)));

        var rows = await Runner.RowsAsync(plan, Source, batchSize);

        Assert.Equal(Sortable.Rows.Count, rows.Count);
        Assert.Equal(-5L, rows[0][0]);
        Assert.Null(rows[^1][0]);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task TopN_of_zero_rows_and_of_an_empty_input(int batchSize)
    {
        var row = Series.RowType();
        var read = IrBuilder.Read(Series.Name, row);
        var none = IrBuilder.Plan(
            IrBuilder.TopN(read, offset: 0, count: 0, IrBuilder.Asc(0, row.Fields[0].Type)));

        var emptyRow = TestData.Empty.RowType();
        var overEmpty = IrBuilder.Plan(IrBuilder.TopN(
            IrBuilder.Read(TestData.Empty.Name, emptyRow),
            offset: 0,
            count: 3,
            IrBuilder.Asc(0, emptyRow.Fields[0].Type)));

        Assert.Empty(await Runner.RowsAsync(none, Source, batchSize));
        Assert.Empty(await Runner.RowsAsync(overEmpty, Source, batchSize));
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task A_pipeline_of_every_streaming_operator(int batchSize)
    {
        var row = Series.RowType();
        var read = IrBuilder.Read(Series.Name, row);
        var filter = IrBuilder.Filter(
            read,
            IrBuilder.Call(FunctionId.Ge, IrBuilder.Bool(), IrBuilder.Ref(row, 0), IrBuilder.Lit(3L)));
        var project = IrBuilder.Project(
            filter,
            [
                ("n", IrBuilder.Ref(row, 0)),
                ("label", IrBuilder.Call(
                    FunctionId.Upper, IrBuilder.Str(), IrBuilder.Ref(row, 2))),
            ]);
        var sort = IrBuilder.Sort(project, IrBuilder.Desc(0, IrBuilder.I64()));
        var fetch = IrBuilder.Fetch(sort, offset: 1, count: 4);

        var rows = await Runner.RowsAsync(IrBuilder.Plan(fetch), Source, batchSize);

        Assert.Equal([18L, 17L, 16L, 15L], rows.Select(r => r[0]));
        Assert.Equal("ROW-18", rows[0][1]);
    }

    [Fact]
    public async Task A_compiled_plan_can_be_executed_more_than_once()
    {
        var row = Series.RowType();
        var plan = IrBuilder.Plan(IrBuilder.Read(Series.Name, row));
        var compiled = Runner.Compile(plan, Source);

        var first = await Runner.CollectAsync(compiled);
        var second = await Runner.CollectAsync(compiled);
        try
        {
            Assert.Equal(20, first.Sum(b => b.Length));
            Assert.Equal(20, second.Sum(b => b.Length));
            ResultComparer.AssertEquivalent(first, second, ResultComparisonOptions.Ordered);
        }
        finally
        {
            foreach (var batch in first.Concat(second))
            {
                batch.Dispose();
            }
        }
    }

    [Fact]
    public async Task Output_memory_can_be_pooled_or_managed()
    {
        var row = Series.RowType();
        var plan = IrBuilder.Plan(IrBuilder.Read(Series.Name, row));
        var stats = new ExecutionStats();

        var pooled = await Runner.RunAsync(plan, Source, stats: stats, pooledOutput: true);
        try
        {
            Assert.Equal(20, pooled.Sum(b => b.Length));
            Assert.True(stats.PeakPooledBytes > 0);
        }
        finally
        {
            foreach (var batch in pooled)
            {
                batch.Dispose();
            }
        }

        // Managed output is GC memory, so a host may hold the batch without ever disposing it and
        // without pinning a pooled buffer — the promise §6.1 makes by default.
        var managed = await Runner.RunAsync(plan, Source, pooledOutput: false);
        var managedStats = new ExecutionStats();
        var again = await Runner.RunAsync(plan, Source, stats: managedStats, pooledOutput: false);

        Assert.Equal(20, managed.Sum(b => b.Length));
        Assert.Equal(0L, BatchReader.ToStorageRows(managed)[0][0]);
        Assert.Equal(19L, BatchReader.ToStorageRows(again)[^1][0]);
    }
}

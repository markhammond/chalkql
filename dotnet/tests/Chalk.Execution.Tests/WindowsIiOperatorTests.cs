using Apache.Arrow;
using Chalk.Catalog;
using Chalk.Execution.Tests.Harness;
using Chalk.Ir;
using Chalk.Sources;
using Chalk.TestKit;

namespace Chalk.Execution.Tests;

/// <summary>
/// The three operators step 19 adds — <c>HOP</c>, <c>SESSION</c> and <c>UNNEST</c> — at
/// <c>BatchSize</c> 1, 7 and 4096, over empty and single-row partitions, NULL times and NULL lists
/// (<c>14-windows-ii.md</c> §10).
/// </summary>
/// <remarks>
/// Every case runs on an arena of its own and asserts it is empty afterwards, so a leak shows here
/// rather than in the allocation gate. The expansions are written out by hand rather than compared
/// with the reference executor: these are the cases the property tests generate around, and a hand
/// expansion is what says what the semantics of §1 and §7 <em>are</em>.
/// </remarks>
public sealed class WindowsIiOperatorTests
{
    public static TheoryData<int> BatchSizes => TestData.BatchSizeData;

    /// <summary>One minute in the nanoseconds a TIMESTAMP(9) counts.</summary>
    private const long Minute = 60L * 1_000_000_000;

    /// <summary>The same in the microseconds an interval literal counts.</summary>
    private const long MinuteMicros = 60L * 1_000_000;

    private const int Symbol = 0;
    private const int Ts = 1;

    /// <summary>
    /// Ticks in the order a window's input arrives in: two sessions in one partition, a NULL time
    /// last inside it, and a single-row partition after it.
    /// </summary>
    private static readonly TestTable Ticks = new()
    {
        Name = "ticks",
        Columns =
        [
            ("symbol", ChalkType.String(nullable: true)),
            ("ts", ChalkType.Timestamp(9, nullable: true)),
            ("qty", ChalkType.Int64(nullable: true)),
        ],
        Rows =
        [
            ["AAA", 0L, 1L],
            ["AAA", 2 * Minute, 2L],
            ["AAA", 7 * Minute, 3L],
            ["AAA", 8 * Minute, 4L],
            ["AAA", null, 5L],
            ["BBB", 12 * Minute, 6L],
        ],
    };

    /// <summary>An empty table of the same shape, for the empty-input cases.</summary>
    private static readonly TestTable NoTicks = new()
    {
        Name = "no_ticks",
        Columns = Ticks.Columns,
        Rows = [],
    };

    /// <summary>
    /// Lists of every shape §7 names: several elements, none, NULL, one, and several again — so a
    /// batch boundary can be put inside a row's list and the elements either side compared.
    /// </summary>
    private static readonly TestTable Baskets = new()
    {
        Name = "baskets",
        Columns =
        [
            ("id", ChalkType.Int64()),
            ("xs", ChalkType.List(ChalkType.Int64(), nullable: true)),
        ],
        Rows =
        [
            [1L, new object?[] { 10L, 20L, 30L }],
            [2L, System.Array.Empty<object?>()],
            [3L, null],
            [4L, new object?[] { 40L }],
            [5L, new object?[] { 50L, 60L }],
        ],
    };

    private static readonly TestSource Source = TestData.Source(Ticks, NoTicks, Baskets);

    private static Rel Ordered() => IrBuilder.Collated(
        IrBuilder.Read(Ticks.Name, Ticks.RowType()),
        (Symbol, SortDirection.AscNullsLast),
        (Ts, SortDirection.AscNullsLast));

    private static Expr Interval(long minutes) => IrBuilder.LitIntervalDay(minutes * MinuteMicros);

    // ---- HOP (D55, §1) ----------------------------------------------------------------------------

    /// <summary>Slide equal to size is a tumbling window: every timed row lands in exactly one.</summary>
    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task A_tumbling_hop_gives_every_timed_row_one_window(int batchSize)
    {
        var plan = IrBuilder.Plan(IrBuilder.Hop(Ordered(), Ts, Interval(5), Interval(5)));

        var rows = await RunAsync(plan, batchSize);

        Assert.Equal(
            [
                ("AAA", 0L, 0L, 5 * Minute),
                ("AAA", 2 * Minute, 0L, 5 * Minute),
                ("AAA", 7 * Minute, 5 * Minute, 10 * Minute),
                ("AAA", 8 * Minute, 5 * Minute, 10 * Minute),
                ("BBB", 12 * Minute, 10 * Minute, 15 * Minute),
            ],
            Windows(rows));
    }

    /// <summary>
    /// A size wider than the slide is the overlapping case: a row is copied once per window it falls
    /// in, and its copies come out together and in ascending <c>window_start</c>, which is what lets
    /// the node claim its input's ordering.
    /// </summary>
    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task An_overlapping_hop_repeats_a_row_in_ascending_window_order(int batchSize)
    {
        var plan = IrBuilder.Plan(IrBuilder.Hop(Ordered(), Ts, Interval(5), Interval(10)));

        var rows = await RunAsync(plan, batchSize);

        Assert.Equal(
            [
                ("AAA", 0L, -5 * Minute, 5 * Minute),
                ("AAA", 0L, 0L, 10 * Minute),
                ("AAA", 2 * Minute, -5 * Minute, 5 * Minute),
                ("AAA", 2 * Minute, 0L, 10 * Minute),
                ("AAA", 7 * Minute, 0L, 10 * Minute),
                ("AAA", 7 * Minute, 5 * Minute, 15 * Minute),
                ("AAA", 8 * Minute, 0L, 10 * Minute),
                ("AAA", 8 * Minute, 5 * Minute, 15 * Minute),
                ("BBB", 12 * Minute, 5 * Minute, 15 * Minute),
                ("BBB", 12 * Minute, 10 * Minute, 20 * Minute),
            ],
            Windows(rows));
    }

    /// <summary>
    /// A size narrower than the slide leaves gaps between the windows, and a row in a gap belongs to
    /// none — the case Calcite's own implementation drops too (V23).
    /// </summary>
    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task A_hop_narrower_than_its_slide_drops_the_rows_in_the_gaps(int batchSize)
    {
        var plan = IrBuilder.Plan(IrBuilder.Hop(Ordered(), Ts, Interval(10), Interval(5)));

        var rows = await RunAsync(plan, batchSize);

        Assert.Equal(
            [
                ("AAA", 0L, 0L, 5 * Minute),
                ("AAA", 2 * Minute, 0L, 5 * Minute),
                ("BBB", 12 * Minute, 10 * Minute, 15 * Minute),
            ],
            Windows(rows));
    }

    /// <summary>A NULL time is in no window, so it produces no row at all (§1).</summary>
    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task A_hop_produces_nothing_for_a_null_time(int batchSize)
    {
        var plan = IrBuilder.Plan(IrBuilder.Hop(Ordered(), Ts, Interval(5), Interval(5)));

        var rows = await RunAsync(plan, batchSize);

        Assert.DoesNotContain(rows, r => r[Ts] is null);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task A_hop_over_an_empty_input_produces_nothing(int batchSize)
    {
        var plan = IrBuilder.Plan(IrBuilder.Hop(
            IrBuilder.Read(NoTicks.Name, NoTicks.RowType()), Ts, Interval(5), Interval(5)));

        Assert.Empty(await RunAsync(plan, batchSize));
    }

    // ---- SESSION (D55, §1) ------------------------------------------------------------------------

    /// <summary>
    /// The gap splits AAA into two sessions and leaves BBB's single row a session of its own. A NULL
    /// time belongs to no session, so its bounds are NULL — and it is still one output row, because
    /// a session window does not drop rows.
    /// </summary>
    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task A_session_window_splits_a_partition_on_the_gap(int batchSize)
    {
        var plan = IrBuilder.Plan(IrBuilder.Session(Ordered(), [Symbol], Ts, Interval(5)));

        var rows = await RunAsync(plan, batchSize);

        Assert.Equal(
            [
                ("AAA", 0L, 0L, 7 * Minute),
                ("AAA", 2 * Minute, 0L, 7 * Minute),
                ("AAA", 7 * Minute, 7 * Minute, 13 * Minute),
                ("AAA", 8 * Minute, 7 * Minute, 13 * Minute),
                ("AAA", null, null, null),
                ("BBB", 12 * Minute, 12 * Minute, 17 * Minute),
            ],
            Windows(rows));
    }

    /// <summary>A gap wide enough to swallow every distance makes one session per partition.</summary>
    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task A_wide_enough_gap_makes_one_session_per_partition(int batchSize)
    {
        var plan = IrBuilder.Plan(IrBuilder.Session(Ordered(), [Symbol], Ts, Interval(60)));

        var rows = await RunAsync(plan, batchSize);

        Assert.Equal(
            [
                ("AAA", 0L, 0L, 68 * Minute),
                ("AAA", 2 * Minute, 0L, 68 * Minute),
                ("AAA", 7 * Minute, 0L, 68 * Minute),
                ("AAA", 8 * Minute, 0L, 68 * Minute),
                ("AAA", null, null, null),
                ("BBB", 12 * Minute, 12 * Minute, 72 * Minute),
            ],
            Windows(rows));
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task A_session_over_an_empty_input_produces_nothing(int batchSize)
    {
        var plan = IrBuilder.Plan(IrBuilder.Session(
            IrBuilder.Collated(
                IrBuilder.Read(NoTicks.Name, NoTicks.RowType()),
                (Symbol, SortDirection.AscNullsLast),
                (Ts, SortDirection.AscNullsLast)),
            [Symbol],
            Ts,
            Interval(5)));

        Assert.Empty(await RunAsync(plan, batchSize));
    }

    /// <summary>
    /// A session buffers its whole input, so it is the node in this step that <c>MaxBytes</c> bites
    /// on. The refusal names the operator and hands back everything it had taken (ADR 0012 §2).
    /// </summary>
    [Fact]
    public async Task A_session_that_breaks_MaxBytes_fails_naming_the_operator_and_strands_nothing()
    {
        var wide = Wide(20_000);
        var source = TestData.Source(wide);
        var plan = IrBuilder.Plan(IrBuilder.Session(
            IrBuilder.Collated(
                IrBuilder.Read(wide.Name, wide.RowType()),
                (Symbol, SortDirection.AscNullsLast),
                (Ts, SortDirection.AscNullsLast)),
            [Symbol],
            Ts,
            Interval(5)));

        using var arena = new ExecutionArena(new ArenaOptions { MaxBytes = 200_000 });
        var failure = await Assert.ThrowsAsync<ExecutionException>(
            () => DrainAsync(Runner.Compile(plan, source, batchSize: 512), arena));

        Assert.Contains("Session", failure.OperatorPath, StringComparison.Ordinal);
        Assert.IsType<ArenaBudgetExceededException>(failure.InnerException);
        Assert.Equal(0, arena.OutstandingBytes);
    }

    // ---- UNNEST (D66, §7) -------------------------------------------------------------------------

    /// <summary>
    /// The cross-join spelling: an empty list and a NULL list contribute nothing, so five input rows
    /// become six.
    /// </summary>
    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Unnest_without_keep_empty_drops_the_empty_and_null_lists(int batchSize)
    {
        var plan = IrBuilder.Plan(IrBuilder.Unnest(Lists(), listColumn: 1));

        var rows = await RunAsync(plan, batchSize);

        Assert.Equal(
            [(1L, 10L), (1L, 20L), (1L, 30L), (4L, 40L), (5L, 50L), (5L, 60L)],
            [.. rows.Select(r => ((long?)r[0], (long?)r[2]))]);
    }

    /// <summary>
    /// The outer spelling: an empty list and a NULL list each contribute one row whose element is
    /// NULL, so five input rows become eight.
    /// </summary>
    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Unnest_with_keep_empty_pads_the_empty_and_null_lists(int batchSize)
    {
        var plan = IrBuilder.Plan(IrBuilder.Unnest(Lists(), listColumn: 1, keepEmpty: true));

        var rows = await RunAsync(plan, batchSize);

        Assert.Equal(
            [
                (1L, 10L), (1L, 20L), (1L, 30L),
                (2L, null), (3L, null),
                (4L, 40L), (5L, 50L), (5L, 60L),
            ],
            [.. rows.Select(r => ((long?)r[0], (long?)r[2]))]);
    }

    /// <summary>
    /// <c>WITH ORDINALITY</c> counts from one within each row's list, and restarts at every row. A
    /// padded row has no position, so its ordinality is NULL — which is why the column is nullable
    /// exactly when <c>keep_empty</c> is set (I-IR-15).
    /// </summary>
    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Unnest_with_ordinality_numbers_each_row_from_one(int batchSize)
    {
        var plan = IrBuilder.Plan(IrBuilder.Unnest(
            Lists(), listColumn: 1, withOrdinality: true, keepEmpty: true));

        var rows = await RunAsync(plan, batchSize);

        Assert.Equal(
            [
                (1L, 10L, 1L), (1L, 20L, 2L), (1L, 30L, 3L),
                (2L, null, null), (3L, null, null),
                (4L, 40L, 1L), (5L, 50L, 1L), (5L, 60L, 2L),
            ],
            [.. rows.Select(r => ((long?)r[0], (long?)r[2], (long?)r[3]))]);
    }

    /// <summary>
    /// A batch boundary inside one row's list. With four rows per batch the first batch ends in the
    /// middle of row 5's list, so the elements either side of the boundary have to come from the
    /// right places in the child array.
    /// </summary>
    [Fact]
    public async Task Unnest_splits_one_rows_list_across_a_batch_boundary()
    {
        var plan = IrBuilder.Plan(IrBuilder.Unnest(Lists(), listColumn: 1, withOrdinality: true));

        using var arena = new ExecutionArena();
        var batches = await CollectAsync(Runner.Compile(plan, Source, batchSize: 4), arena);
        try
        {
            Assert.Equal([4, 2], [.. batches.Select(b => b.Length)]);
            Assert.Equal(
                [(1L, 10L, 1L), (1L, 20L, 2L), (1L, 30L, 3L), (4L, 40L, 1L), (5L, 50L, 1L), (5L, 60L, 2L)],
                [.. ResultComparer.Rows(batches).Select(r => ((long?)r[0], (long?)r[2], (long?)r[3]))]);
        }
        finally
        {
            foreach (var batch in batches)
            {
                batch.Dispose();
            }
        }

        Assert.Equal(0, arena.OutstandingBytes);
    }

    /// <summary>
    /// The element column is a view on the list's own child column, not a copy of it (§7). Two rows
    /// per batch puts row 1's three elements across two output batches, and the second of them starts
    /// part-way into the child it shares: a slice carries that offset, a copy would start at zero.
    /// </summary>
    /// <remarks>
    /// Read through the pipeline's own batches rather than the host's. Since step 20 the output
    /// boundary always materialises — every operator's slot is reused, so a batch a host keeps has to
    /// own its memory (15-zero-allocation-execution.md §1) — and that copy would flatten the offset
    /// this test is about, under pooled output as much as managed.
    /// </remarks>
    [Fact]
    public async Task Unnest_returns_element_views_rather_than_copies()
    {
        var plan = IrBuilder.Plan(IrBuilder.Unnest(Lists(), listColumn: 1));

        var shapes = await Runner.ColumnarShapeAsync(plan, Source, batchSize: 2, column: 2);

        Assert.Equal([2, 1, 1, 2], [.. shapes.Select(s => s.Rows)]);
        Assert.Equal([0, 2, 0, 0], [.. shapes.Select(s => s.Offsets[0])]);
    }

    /// <summary>
    /// Padding breaks the contiguity, so that batch's element column is built rather than sliced —
    /// and the answers are the same either way, which is what the two spellings above already say.
    /// This asserts the mechanism: a padded batch starts at offset zero and carries a validity map.
    /// </summary>
    [Fact]
    public async Task A_padded_unnest_builds_its_element_column()
    {
        var plan = IrBuilder.Plan(IrBuilder.Unnest(Lists(), listColumn: 1, keepEmpty: true));

        var batches = await Runner.RunAsync(plan, Source, batchSize: 4096, pooledOutput: true);
        try
        {
            var elements = Assert.Single(batches).Column(2);
            Assert.Equal(0, elements.Data.Offset);
            Assert.Equal(2, elements.Data.NullCount);
        }
        finally
        {
            foreach (var batch in batches)
            {
                batch.Dispose();
            }
        }
    }

    /// <summary>
    /// An unnest rents three position arrays of one batch each, so a budget below them refuses before
    /// any work is done — naming the operator and leaving the arena as it found it.
    /// </summary>
    [Fact]
    public async Task An_unnest_that_breaks_MaxBytes_fails_naming_the_operator_and_strands_nothing()
    {
        var plan = IrBuilder.Plan(IrBuilder.Unnest(Lists(), listColumn: 1));

        using var arena = new ExecutionArena(new ArenaOptions { MaxBytes = 4_096 });
        var failure = await Assert.ThrowsAsync<ExecutionException>(
            () => DrainAsync(Runner.Compile(plan, Source, batchSize: 4096), arena));

        Assert.Contains("Unnest", failure.OperatorPath, StringComparison.Ordinal);
        Assert.IsType<ArenaBudgetExceededException>(failure.InnerException);
        Assert.Equal(0, arena.OutstandingBytes);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Unnest_of_an_empty_input_produces_nothing(int batchSize)
    {
        var empty = new TestTable { Name = "no_baskets", Columns = Baskets.Columns, Rows = [] };
        var plan = IrBuilder.Plan(IrBuilder.Unnest(
            IrBuilder.Read(empty.Name, empty.RowType()), listColumn: 1));

        Assert.Empty(await Runner.RowsAsync(plan, TestData.Source(empty), batchSize));
    }

    // ---- helpers ----------------------------------------------------------------------------------

    private static Rel Lists() => IrBuilder.Read(Baskets.Name, Baskets.RowType());

    /// <summary>Runs on an arena of its own and asserts nothing is left outstanding (§10).</summary>
    private static async Task<List<object?[]>> RunAsync(Plan plan, int batchSize)
    {
        using var arena = new ExecutionArena();
        var batches = await CollectAsync(Runner.Compile(plan, Source, batchSize), arena);
        try
        {
            return ResultComparer.Rows(batches);
        }
        finally
        {
            foreach (var batch in batches)
            {
                batch.Dispose();
            }

            Assert.Equal(0, arena.OutstandingBytes);
        }
    }

    /// <summary>Every batch of a run on a given arena — <c>Runner</c>'s collector takes none.</summary>
    private static async Task<List<RecordBatch>> CollectAsync(
        CompiledPlan compiled, ExecutionArena arena)
    {
        var batches = new List<RecordBatch>();
        await foreach (var batch in compiled.ExecuteAsync(
            [], new ExecutionStats(), arena, TestContext.Current.CancellationToken))
        {
            batches.Add(batch);
        }

        return batches;
    }

    private static async Task DrainAsync(CompiledPlan compiled, ExecutionArena arena)
    {
        await foreach (var batch in compiled.ExecuteAsync(
            [], new ExecutionStats(), arena, TestContext.Current.CancellationToken))
        {
            batch.Dispose();
        }
    }

    /// <summary>The (symbol, ts, window_start, window_end) of every row, for the expansions above.</summary>
    private static List<(string?, long?, long?, long?)> Windows(List<object?[]> rows) =>
        [.. rows.Select(r => ((string?)r[0], (long?)r[1], (long?)r[^2], (long?)r[^1]))];

    /// <summary>One partition of ticks, wide enough that buffering it needs more than the budget.</summary>
    private static TestTable Wide(int rows)
    {
        var data = new List<object?[]>(rows);
        for (var i = 0; i < rows; i++)
        {
            data.Add(["AAA", i * Minute, (long)i]);
        }

        return new TestTable { Name = "wide_ticks", Columns = Ticks.Columns, Rows = data };
    }
}

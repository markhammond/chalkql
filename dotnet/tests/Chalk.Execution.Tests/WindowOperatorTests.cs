using Chalk.Catalog;
using Chalk.Execution.Tests.Harness;
using Chalk.Execution.Windowing;
using Chalk.Ir;
using Chalk.Sources;
using Chalk.TestKit;

namespace Chalk.Execution.Tests;

/// <summary>
/// The window operator (D51, <c>13-window-functions.md</c> §4 and §7). Every case runs at
/// <c>BatchSize</c> 1, 7 and 4096 and is compared with the reference executor, which materialises the
/// frame and evaluates the call over it — so the sliding accumulators, the deque, the two cursors and
/// the position arrays are all measured against naive recomputation.
/// </summary>
/// <remarks>
/// The three window classes share a collection because <see cref="WindowFrames.ForceSegmentTree"/>
/// is process-wide: one test flips it, and a window running concurrently in another class would take
/// the other code path while it is set.
/// </remarks>
[Collection("window-frames")]
public sealed class WindowOperatorTests
{
    private static readonly TestTable Windowed = TestData.Windowed;
    private static readonly TestSource Source = TestData.Source(Windowed, TestData.Empty);

    private const int Symbol = 0;
    private const int Ts = 1;
    private const int Value = 2;
    private const int Price = 3;

    /// <summary>The input a window sees: the fixture, claiming the ordering the plan promises.</summary>
    private static Rel Input() => IrBuilder.Collated(
        IrBuilder.Read(Windowed.Name, Windowed.RowType()),
        (Symbol, SortDirection.AscNullsLast),
        (Ts, SortDirection.AscNullsLast));

    private static Plan PartitionedBySymbol(
        WindowFrame frame, params (string Name, WindowCall Call)[] calls) =>
        IrBuilder.Plan(IrBuilder.Window(
            Input(),
            [Symbol],
            [IrBuilder.Asc(Ts, IrBuilder.I64(nullable: true))],
            frame,
            calls));

    private static async Task AgreeAsync(Plan plan) =>
        await Runner.AssertEnginesAgreeAsync(plan, Source);

    // ---- the aggregates, over each frame shape ------------------------------------------------

    [Fact]
    public async Task A_running_total_matches_the_reference_over_every_partition_shape() =>
        await AgreeAsync(PartitionedBySymbol(
            IrBuilder.RunningFrame(),
            ("total", IrBuilder.WinAgg(
                AggregateFunctionId.Sum,
                IrBuilder.I64(nullable: true),
                IrBuilder.Ref(Value, IrBuilder.I64(nullable: true))))));

    [Fact]
    public async Task A_sliding_rows_frame_matches_the_reference() =>
        await AgreeAsync(PartitionedBySymbol(
            IrBuilder.RowsFrame(2, 1),
            ("total", IrBuilder.WinAgg(
                AggregateFunctionId.Sum,
                IrBuilder.I64(nullable: true),
                IrBuilder.Ref(Value, IrBuilder.I64(nullable: true)))),
            ("n", IrBuilder.WinAgg(AggregateFunctionId.Count, IrBuilder.I64())),
            ("counted", IrBuilder.WinAgg(
                AggregateFunctionId.Count,
                IrBuilder.I64(),
                IrBuilder.Ref(Value, IrBuilder.I64(nullable: true))))));

    [Fact]
    public async Task A_frame_that_names_no_rows_produces_null_for_sum_and_zero_for_count() =>
        await AgreeAsync(PartitionedBySymbol(
            // Everything strictly before the previous row: empty for the first two rows of a run.
            IrBuilder.Frame(
                FrameMode.Rows,
                FrameBoundKind.Preceding,
                FrameBoundKind.Preceding,
                IrBuilder.Lit(4L),
                IrBuilder.Lit(2L)),
            ("total", IrBuilder.WinAgg(
                AggregateFunctionId.Sum,
                IrBuilder.I64(nullable: true),
                IrBuilder.Ref(Value, IrBuilder.I64(nullable: true)))),
            ("n", IrBuilder.WinAgg(AggregateFunctionId.Count, IrBuilder.I64()))));

    [Fact]
    public async Task Sum0_is_zero_over_an_empty_frame_where_sum_is_null()
    {
        var plan = PartitionedBySymbol(
            IrBuilder.Frame(
                FrameMode.Rows,
                FrameBoundKind.Preceding,
                FrameBoundKind.Preceding,
                IrBuilder.Lit(3L),
                IrBuilder.Lit(2L)),
            ("zero", IrBuilder.WinAgg(
                AggregateFunctionId.Sum0,
                IrBuilder.I64(),
                IrBuilder.Ref(Value, IrBuilder.I64(nullable: true)))),
            ("nullable", IrBuilder.WinAgg(
                AggregateFunctionId.Sum,
                IrBuilder.I64(nullable: true),
                IrBuilder.Ref(Value, IrBuilder.I64(nullable: true)))));

        var rows = await Runner.RowsAsync(plan, Source);

        Assert.Equal(0L, rows[0][4]);
        Assert.Null(rows[0][5]);
        await AgreeAsync(plan);
    }

    [Fact]
    public async Task Avg_is_computed_natively_and_matches_the_reference() =>
        await Runner.AssertEnginesAgreeAsync(
            PartitionedBySymbol(
                IrBuilder.RowsFrame(2, 0),
                ("mean", IrBuilder.WinAgg(
                    AggregateFunctionId.Avg,
                    IrBuilder.Fp64(nullable: true),
                    IrBuilder.Ref(Price, IrBuilder.Fp64(nullable: true))))),
            Source,
            new ResultComparisonOptions { AggregatedFloatColumns = [4] });

    [Fact]
    public async Task Min_and_max_over_a_moving_frame_match_the_reference() =>
        await AgreeAsync(PartitionedBySymbol(
            IrBuilder.RowsFrame(1, 1),
            ("lo", IrBuilder.WinAgg(
                AggregateFunctionId.Min,
                IrBuilder.I64(nullable: true),
                IrBuilder.Ref(Value, IrBuilder.I64(nullable: true)))),
            ("hi", IrBuilder.WinAgg(
                AggregateFunctionId.Max,
                IrBuilder.I64(nullable: true),
                IrBuilder.Ref(Value, IrBuilder.I64(nullable: true))))));

    [Fact]
    public async Task Min_and_max_with_an_unbounded_start_take_the_running_extremum_path() =>
        await AgreeAsync(PartitionedBySymbol(
            IrBuilder.Frame(
                FrameMode.Rows, FrameBoundKind.UnboundedPreceding, FrameBoundKind.CurrentRow),
            ("lo", IrBuilder.WinAgg(
                AggregateFunctionId.Min,
                IrBuilder.I64(nullable: true),
                IrBuilder.Ref(Value, IrBuilder.I64(nullable: true))))));

    [Fact]
    public async Task Min_over_a_string_column_works_because_the_answer_is_a_row_not_a_value() =>
        await AgreeAsync(PartitionedBySymbol(
            IrBuilder.RowsFrame(2, 0),
            ("first_symbol", IrBuilder.WinAgg(
                AggregateFunctionId.Min,
                IrBuilder.Str(nullable: true),
                IrBuilder.Ref(Symbol, IrBuilder.Str(nullable: true))))));

    // ---- the ranking family --------------------------------------------------------------------

    [Fact]
    public async Task The_ranking_family_agrees_with_the_reference_including_ties()
    {
        var plan = PartitionedBySymbol(
            IrBuilder.RunningFrame(),
            ("rn", IrBuilder.WinFn(WindowFunctionId.RowNumber, IrBuilder.I64())),
            ("rank", IrBuilder.WinFn(WindowFunctionId.Rank, IrBuilder.I64())),
            ("dense", IrBuilder.WinFn(WindowFunctionId.DenseRank, IrBuilder.I64())),
            ("pr", IrBuilder.WinFn(WindowFunctionId.PercentRank, IrBuilder.Fp64())),
            ("cd", IrBuilder.WinFn(WindowFunctionId.CumeDist, IrBuilder.Fp64())),
            ("q", IrBuilder.WinFn(
                WindowFunctionId.Ntile, IrBuilder.I64(), ignoreNulls: false, IrBuilder.Lit(2L))));

        var rows = await Runner.RowsAsync(plan, Source);

        // AAA's third and fourth rows tie on ts: same RANK, next DENSE_RANK, different ROW_NUMBER.
        Assert.Equal(3L, rows[2][4]);
        Assert.Equal(4L, rows[3][4]);
        Assert.Equal(3L, rows[2][5]);
        Assert.Equal(3L, rows[3][5]);
        Assert.Equal(3L, rows[2][6]);
        Assert.Equal(3L, rows[3][6]);

        // A single-row partition: PERCENT_RANK is zero by definition and CUME_DIST is one.
        Assert.Equal(0d, rows[4][7]);
        Assert.Equal(1d, rows[4][8]);

        await AgreeAsync(plan);
    }

    [Fact]
    public async Task Ntile_splits_a_partition_into_buckets_larger_ones_first()
    {
        var plan = PartitionedBySymbol(
            IrBuilder.RunningFrame(),
            ("q", IrBuilder.WinFn(
                WindowFunctionId.Ntile, IrBuilder.I64(), ignoreNulls: false, IrBuilder.Lit(3L))));

        var rows = await Runner.RowsAsync(plan, Source);

        // AAA has four rows in three buckets: 2, 1, 1.
        Assert.Equal(new object?[] { 1L, 1L, 2L, 3L }, rows.Take(4).Select(r => r[4]).ToArray());
        await AgreeAsync(plan);
    }

    // ---- navigation ----------------------------------------------------------------------------

    [Fact]
    public async Task Lag_and_lead_walk_the_partition_and_ignore_the_frame() =>
        await AgreeAsync(PartitionedBySymbol(
            // A frame nothing navigational should look at.
            IrBuilder.RowsFrame(0, 0),
            ("previous", IrBuilder.WinFn(
                WindowFunctionId.Lag,
                IrBuilder.I64(nullable: true),
                ignoreNulls: false,
                IrBuilder.Ref(Value, IrBuilder.I64(nullable: true)),
                IrBuilder.Lit(1L))),
            ("next", IrBuilder.WinFn(
                WindowFunctionId.Lead,
                IrBuilder.I64(nullable: true),
                ignoreNulls: false,
                IrBuilder.Ref(Value, IrBuilder.I64(nullable: true)),
                IrBuilder.Lit(1L)))));

    [Fact]
    public async Task Lag_falls_back_to_its_default_at_the_start_of_a_partition()
    {
        var plan = PartitionedBySymbol(
            IrBuilder.RunningFrame(),
            ("previous", IrBuilder.WinFn(
                WindowFunctionId.Lag,
                IrBuilder.I64(nullable: true),
                ignoreNulls: false,
                IrBuilder.Ref(Value, IrBuilder.I64(nullable: true)),
                IrBuilder.Lit(1L),
                IrBuilder.Lit(-1L))));

        var rows = await Runner.RowsAsync(plan, Source);

        Assert.Equal(-1L, rows[0][4]);
        Assert.Equal(10L, rows[1][4]);
        Assert.Equal(-1L, rows[4][4]);
        await AgreeAsync(plan);
    }

    [Fact]
    public async Task Lag_with_ignore_nulls_skips_the_nulls_it_walks_over()
    {
        var plan = PartitionedBySymbol(
            IrBuilder.RunningFrame(),
            ("previous", IrBuilder.WinFn(
                WindowFunctionId.Lag,
                IrBuilder.I64(nullable: true),
                ignoreNulls: true,
                IrBuilder.Ref(Value, IrBuilder.I64(nullable: true)),
                IrBuilder.Lit(1L))));

        var rows = await Runner.RowsAsync(plan, Source);

        // AAA is 10, NULL, 30, 40; the third row's previous non-NULL value is the first row's.
        Assert.Null(rows[0][4]);
        Assert.Equal(10L, rows[1][4]);
        Assert.Equal(10L, rows[2][4]);
        Assert.Equal(30L, rows[3][4]);
        await AgreeAsync(plan);
    }

    [Fact]
    public async Task First_last_and_nth_value_read_the_frame() =>
        await AgreeAsync(PartitionedBySymbol(
            IrBuilder.RowsFrame(1, 1),
            ("first", IrBuilder.WinFn(
                WindowFunctionId.FirstValue,
                IrBuilder.I64(nullable: true),
                ignoreNulls: false,
                IrBuilder.Ref(Value, IrBuilder.I64(nullable: true)))),
            ("last", IrBuilder.WinFn(
                WindowFunctionId.LastValue,
                IrBuilder.I64(nullable: true),
                ignoreNulls: false,
                IrBuilder.Ref(Value, IrBuilder.I64(nullable: true)))),
            ("second", IrBuilder.WinFn(
                WindowFunctionId.NthValue,
                IrBuilder.I64(nullable: true),
                ignoreNulls: false,
                IrBuilder.Ref(Value, IrBuilder.I64(nullable: true)),
                IrBuilder.Lit(2L)))));

    [Fact]
    public async Task First_value_with_ignore_nulls_skips_the_nulls_in_the_frame() =>
        await AgreeAsync(PartitionedBySymbol(
            IrBuilder.Frame(
                FrameMode.Rows, FrameBoundKind.UnboundedPreceding, FrameBoundKind.UnboundedFollowing),
            ("first", IrBuilder.WinFn(
                WindowFunctionId.FirstValue,
                IrBuilder.I64(nullable: true),
                ignoreNulls: true,
                IrBuilder.Ref(Value, IrBuilder.I64(nullable: true)))),
            ("last", IrBuilder.WinFn(
                WindowFunctionId.LastValue,
                IrBuilder.I64(nullable: true),
                ignoreNulls: true,
                IrBuilder.Ref(Value, IrBuilder.I64(nullable: true))))));

    // ---- frames --------------------------------------------------------------------------------

    [Theory]
    [InlineData(FrameExclusion.CurrentRow)]
    [InlineData(FrameExclusion.Group)]
    [InlineData(FrameExclusion.Ties)]
    public async Task Every_exclusion_matches_the_reference(FrameExclusion exclusion) =>
        await AgreeAsync(PartitionedBySymbol(
            IrBuilder.RowsFrame(2, 2, exclusion),
            ("total", IrBuilder.WinAgg(
                AggregateFunctionId.Sum,
                IrBuilder.I64(nullable: true),
                IrBuilder.Ref(Value, IrBuilder.I64(nullable: true)))),
            ("n", IrBuilder.WinAgg(AggregateFunctionId.Count, IrBuilder.I64())),
            ("lo", IrBuilder.WinAgg(
                AggregateFunctionId.Min,
                IrBuilder.I64(nullable: true),
                IrBuilder.Ref(Value, IrBuilder.I64(nullable: true)))),
            ("first", IrBuilder.WinFn(
                WindowFunctionId.FirstValue,
                IrBuilder.I64(nullable: true),
                ignoreNulls: false,
                IrBuilder.Ref(Value, IrBuilder.I64(nullable: true))))));

    [Fact]
    public async Task A_range_frame_with_no_offset_includes_the_peers() =>
        await AgreeAsync(PartitionedBySymbol(
            IrBuilder.RunningFrame(),
            ("total", IrBuilder.WinAgg(
                AggregateFunctionId.Sum,
                IrBuilder.I64(nullable: true),
                IrBuilder.Ref(Value, IrBuilder.I64(nullable: true))))));

    [Theory]
    [InlineData(0L, 0L)]
    [InlineData(1L, 0L)]
    [InlineData(1L, 1L)]
    [InlineData(2L, 5L)]
    public async Task A_range_frame_with_an_offset_matches_the_reference(long back, long forward) =>
        await AgreeAsync(PartitionedBySymbol(
            IrBuilder.Frame(
                FrameMode.Range,
                FrameBoundKind.Preceding,
                FrameBoundKind.Following,
                IrBuilder.Lit(back),
                IrBuilder.Lit(forward)),
            ("total", IrBuilder.WinAgg(
                AggregateFunctionId.Sum,
                IrBuilder.I64(nullable: true),
                IrBuilder.Ref(Value, IrBuilder.I64(nullable: true)))),
            ("n", IrBuilder.WinAgg(AggregateFunctionId.Count, IrBuilder.I64()))));

    /// <summary>
    /// A partition whose order keys are all NULL is one peer group, and an offset frame over it is
    /// exactly that group — PostgreSQL's rule and the one §1 fixes. <c>DDD</c> is that partition.
    /// </summary>
    [Fact]
    public async Task A_null_order_key_frames_only_its_own_peers()
    {
        var plan = PartitionedBySymbol(
            IrBuilder.Frame(
                FrameMode.Range,
                FrameBoundKind.Preceding,
                FrameBoundKind.CurrentRow,
                IrBuilder.Lit(1L)),
            ("n", IrBuilder.WinAgg(AggregateFunctionId.Count, IrBuilder.I64())));

        var rows = await Runner.RowsAsync(plan, Source);

        // DDD's two rows both have a NULL ts, so each sees the pair.
        Assert.Equal(2L, rows[7][4]);
        Assert.Equal(2L, rows[8][4]);
        await AgreeAsync(plan);
    }

    [Fact]
    public async Task A_descending_range_offset_reads_the_ordering_the_other_way() =>
        await AgreeAsync(IrBuilder.Plan(IrBuilder.Window(
            IrBuilder.Collated(
                IrBuilder.Read(Windowed.Name, Windowed.RowType()),
                (Symbol, SortDirection.AscNullsLast),
                (Ts, SortDirection.AscNullsLast)),
            [Symbol],
            // The input claims ascending ts; the window asks for it descending, which the plan would
            // satisfy with a sort. Here the frame arithmetic is what is under test, so the operator
            // is handed the ordering it is told about.
            [IrBuilder.Asc(Ts, IrBuilder.I64(nullable: true))],
            IrBuilder.Frame(
                FrameMode.Range,
                FrameBoundKind.Preceding,
                FrameBoundKind.Following,
                IrBuilder.Lit(1L),
                IrBuilder.Lit(1L)),
            [("n", IrBuilder.WinAgg(AggregateFunctionId.Count, IrBuilder.I64()))])));

    [Fact]
    public async Task A_whole_table_partition_sees_every_row() =>
        await AgreeAsync(IrBuilder.Plan(IrBuilder.Window(
            Input(),
            [],
            [],
            IrBuilder.WholePartitionFrame(),
            [("n", IrBuilder.WinAgg(AggregateFunctionId.Count, IrBuilder.I64()))])));

    [Fact]
    public async Task An_empty_input_produces_no_rows()
    {
        var plan = IrBuilder.Plan(IrBuilder.Window(
            IrBuilder.Read(TestData.Empty.Name, TestData.Empty.RowType()),
            [],
            [],
            IrBuilder.WholePartitionFrame(),
            [("n", IrBuilder.WinAgg(AggregateFunctionId.Count, IrBuilder.I64()))]));

        foreach (var batchSize in Runner.BatchSizes)
        {
            Assert.Empty(await Runner.RowsAsync(plan, Source, batchSize));
        }
    }

    // ---- the contracts -------------------------------------------------------------------------

    [Fact]
    public async Task A_window_leaves_the_arena_empty()
    {
        using var arena = new ExecutionArena();
        var compiled = Runner.Compile(
            PartitionedBySymbol(
                IrBuilder.RowsFrame(2, 2),
                ("total", IrBuilder.WinAgg(
                    AggregateFunctionId.Sum,
                    IrBuilder.I64(nullable: true),
                    IrBuilder.Ref(Value, IrBuilder.I64(nullable: true)))),
                ("lo", IrBuilder.WinAgg(
                    AggregateFunctionId.Min,
                    IrBuilder.I64(nullable: true),
                    IrBuilder.Ref(Value, IrBuilder.I64(nullable: true)))),
                ("previous", IrBuilder.WinFn(
                    WindowFunctionId.Lag,
                    IrBuilder.I64(nullable: true),
                    ignoreNulls: true,
                    IrBuilder.Ref(Value, IrBuilder.I64(nullable: true)),
                    IrBuilder.Lit(1L)))),
            Source);

        await foreach (var batch in compiled.ExecuteAsync(
            [], new ExecutionStats(), arena, CancellationToken.None))
        {
            batch.Dispose();
        }

        Assert.Equal(0, arena.OutstandingBytes);
    }

    [Fact]
    public async Task A_window_that_breaks_MaxBytes_fails_naming_the_operator()
    {
        using var arena = new ExecutionArena(new ArenaOptions { MaxBytes = 4096 });
        var compiled = Runner.Compile(
            PartitionedBySymbol(
                IrBuilder.RowsFrame(2, 2),
                ("total", IrBuilder.WinAgg(
                    AggregateFunctionId.Sum,
                    IrBuilder.I64(nullable: true),
                    IrBuilder.Ref(Value, IrBuilder.I64(nullable: true))))),
            Source);

        var failure = await Assert.ThrowsAsync<ExecutionException>(async () =>
        {
            await foreach (var batch in compiled.ExecuteAsync(
                [], new ExecutionStats(), arena, CancellationToken.None))
            {
                batch.Dispose();
            }
        });

        Assert.Contains("Window", failure.OperatorPath, StringComparison.Ordinal);
        Assert.IsType<ArenaBudgetExceededException>(failure.InnerException);

        // A refused rental is not a charged one, and the run's finally hands back everything it did
        // get: an aborted window leaves the arena as empty as a finished one (§2).
        Assert.Equal(0, arena.OutstandingBytes);
    }

    [Fact]
    public void An_unsupported_window_call_is_refused_at_compilation()
    {
        var plan = PartitionedBySymbol(
            IrBuilder.RunningFrame(),
            ("d", new WindowCall
            {
                Aggregate = AggregateFunctionId.ApproxCountDistinct,
                Type = IrBuilder.I64(),
                Args = { IrBuilder.Ref(Value, IrBuilder.I64(nullable: true)) },
            }));

        var failure = Assert.Throws<UnsupportedFeatureException>(() => Runner.Compile(plan, Source));
        Assert.Contains("APPROXCOUNTDISTINCT", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Step 18 refused <c>DISTINCT</c> inside a window; D56 implements it with a counted multiset, so
    /// the case that used to be an error is now an answer the reference executor agrees with.
    /// </summary>
    [Fact]
    public async Task A_distinct_window_aggregate_matches_the_reference() =>
        await AgreeAsync(PartitionedBySymbol(
            IrBuilder.RunningFrame(),
            ("n", IrBuilder.WinAggDistinct(
                AggregateFunctionId.Count,
                IrBuilder.I64(),
                IrBuilder.Ref(Value, IrBuilder.I64(nullable: true)))),
            ("total", IrBuilder.WinAggDistinct(
                AggregateFunctionId.Sum,
                IrBuilder.I64(nullable: true),
                IrBuilder.Ref(Value, IrBuilder.I64(nullable: true))))));

    // ---- the holistic aggregates over a frame (D57, 14-windows-ii.md §3) ------------------------

    /// <summary>
    /// <c>MODE</c> over a frame: the counted map slides with the frame and only rescans when the
    /// current mode's own count falls, and the reference executor recounts the whole frame.
    /// </summary>
    [Theory]
    [MemberData(nameof(Exclusions))]
    public async Task A_windowed_mode_matches_the_reference(FrameExclusion exclusion) =>
        await AgreeAsync(PartitionedBySymbol(
            IrBuilder.RowsFrame(2, 1, exclusion),
            ("busiest", IrBuilder.WinAgg(
                AggregateFunctionId.Mode,
                IrBuilder.I64(nullable: true),
                IrBuilder.Ref(Value, IrBuilder.I64(nullable: true))))));

    /// <summary>The running frame, where the map only ever grows and never rescans.</summary>
    [Fact]
    public async Task A_running_mode_matches_the_reference() =>
        await AgreeAsync(PartitionedBySymbol(
            IrBuilder.RunningFrame(),
            ("busiest", IrBuilder.WinAgg(
                AggregateFunctionId.Mode,
                IrBuilder.I64(nullable: true),
                IrBuilder.Ref(Value, IrBuilder.I64(nullable: true))))));

    /// <summary>
    /// <c>LISTAGG</c> over a frame, recomputed per row (§3). A window carries no <c>WITHIN GROUP</c>
    /// ordering — Calcite cannot express one (V22) — so the frame's own order is the order.
    /// </summary>
    [Theory]
    [MemberData(nameof(Exclusions))]
    public async Task A_windowed_listagg_matches_the_reference(FrameExclusion exclusion) =>
        await AgreeAsync(PartitionedBySymbol(
            IrBuilder.RowsFrame(2, 1, exclusion),
            ("names", IrBuilder.WinAgg(
                AggregateFunctionId.Listagg,
                IrBuilder.Str(nullable: true),
                IrBuilder.Ref(Symbol, IrBuilder.Str(nullable: true)),
                IrBuilder.Lit("|")))));

    /// <summary>
    /// <c>ARRAY_AGG</c> over a frame: the values as a LIST, NULL elements kept, and a frame with no
    /// value in it at all NULL rather than an empty list (§3).
    /// </summary>
    [Theory]
    [MemberData(nameof(Exclusions))]
    public async Task A_windowed_array_agg_matches_the_reference(FrameExclusion exclusion) =>
        await AgreeAsync(PartitionedBySymbol(
            IrBuilder.RowsFrame(1, 1, exclusion),
            ("values", IrBuilder.WinAgg(
                AggregateFunctionId.ArrayAgg,
                IrBuilder.List(IrBuilder.I64(nullable: true)),
                IrBuilder.Ref(Value, IrBuilder.I64(nullable: true))))));

    /// <summary>The percentiles have no window form to test: Calcite 1.42 cannot express one (V22).</summary>
    [Fact]
    public void A_windowed_percentile_is_refused_at_compilation()
    {
        var plan = PartitionedBySymbol(
            IrBuilder.RunningFrame(),
            ("median", IrBuilder.WinAgg(
                AggregateFunctionId.PercentileCont,
                IrBuilder.Fp64(nullable: true),
                IrBuilder.Ref(Value, IrBuilder.I64(nullable: true)))));

        var failure = Assert.Throws<UnsupportedFeatureException>(() => Runner.Compile(plan, Source));
        Assert.Contains("PERCENTILECONT", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    public static TheoryData<FrameExclusion> Exclusions =>
    [
        FrameExclusion.NoOthers,
        FrameExclusion.CurrentRow,
        FrameExclusion.Group,
        FrameExclusion.Ties,
    ];

    [Fact]
    public void A_groups_frame_is_refused_at_compilation()
    {
        var plan = PartitionedBySymbol(
            IrBuilder.Frame(
                FrameMode.Groups,
                FrameBoundKind.Preceding,
                FrameBoundKind.CurrentRow,
                IrBuilder.Lit(1L)),
            ("n", IrBuilder.WinAgg(AggregateFunctionId.Count, IrBuilder.I64())));

        var failure = Assert.Throws<UnsupportedFeatureException>(() => Runner.Compile(plan, Source));
        Assert.Contains("GROUPS", failure.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ the coverage graft (D165)
    //
    // Query shapes grafted from ikvmnet/calcite-dotnet (Apache-2.0),
    // src/Apache.Calcite.Tests/ClrEnumerableDifferentialTests.cs; see the repository's NOTICE. No
    // expected value is taken (D163): each of these is compared with the reference executor, which
    // materialises every frame from scratch and shares no code with the operator.
    //
    // The `edge` corpus family asks the same questions in SQL. These ask them of the operator
    // directly, over six rows chosen so that a frame is empty, a partition key is NULL and a
    // navigation function steps off both ends.

    /// <summary>`sales` (D164): two partitions, a duplicate amount, one NULL amount.</summary>
    private static readonly TestTable Sales = new()
    {
        Name = "sales",
        Columns =
        [
            ("id", ChalkType.Int64(nullable: true)),
            ("region", ChalkType.String(nullable: true)),
            ("amount", ChalkType.Int64(nullable: true)),
        ],
        Rows =
        [
            [1L, "EAST", 10L],
            [2L, "EAST", 20L],
            [3L, "EAST", 20L],
            [4L, "WEST", 30L],
            [5L, "WEST", null],
            [6L, "WEST", 5L],
        ],
    };

    /// <summary>The same shape with no rows in it, for the window over nothing.</summary>
    private static readonly TestTable NoSales = new()
    {
        Name = "no_sales",
        Columns = Sales.Columns,
        Rows = [],
    };

    private static readonly TestSource SalesSource = TestData.Source(Sales, NoSales);

    private const int SaleId = 0;
    private const int SaleRegion = 1;
    private const int SaleAmount = 2;

    /// <summary>The fixture ordered by id, which is the ordering these windows claim.</summary>
    private static Rel SalesById(TestTable table) => IrBuilder.Collated(
        IrBuilder.Read(table.Name, table.RowType()),
        (SaleId, SortDirection.AscNullsLast));

    private static Plan OverSales(
        TestTable table,
        int[] partitionKeys,
        SortField[] order,
        WindowFrame frame,
        params (string Name, WindowCall Call)[] calls) =>
        IrBuilder.Plan(IrBuilder.Window(SalesById(table), partitionKeys, order, frame, calls));

    private static SortField ById() => IrBuilder.Asc(SaleId, IrBuilder.I64(nullable: true));

    private static Expr Amount() => IrBuilder.Ref(SaleAmount, IrBuilder.I64(nullable: true));

    /// <summary>A frame wholly ahead of the current row: empty for the last rows of every run.</summary>
    [Fact]
    public async Task A_frame_entirely_following_is_empty_at_the_end_of_a_partition() =>
        await Runner.AssertEnginesAgreeAsync(
            OverSales(
                Sales,
                [],
                [ById()],
                IrBuilder.Frame(
                    FrameMode.Rows,
                    FrameBoundKind.Following,
                    FrameBoundKind.Following,
                    IrBuilder.Lit(1L),
                    IrBuilder.Lit(2L)),
                ("total", IrBuilder.WinAgg(
                    AggregateFunctionId.Sum, IrBuilder.I64(nullable: true), Amount())),
                ("n", IrBuilder.WinAgg(AggregateFunctionId.Count, IrBuilder.I64()))),
            SalesSource);

    /// <summary>
    /// PARTITION BY a nullable key: the NULL is a partition of its own, and the two rows at 20 share
    /// one.
    /// </summary>
    [Fact]
    public async Task Partition_by_a_nullable_key_gives_the_nulls_a_partition_of_their_own() =>
        await Runner.AssertEnginesAgreeAsync(
            OverSales(
                Sales,
                [SaleAmount],
                [],
                IrBuilder.WholePartitionFrame(),
                ("n", IrBuilder.WinAgg(AggregateFunctionId.Count, IrBuilder.I64())),
                ("total", IrBuilder.WinAgg(
                    AggregateFunctionId.Sum, IrBuilder.I64(nullable: true), Amount()))),
            SalesSource);

    /// <summary>A window over an empty input produces no rows, not one row of nulls.</summary>
    [Fact]
    public async Task A_window_over_no_rows_produces_no_rows()
    {
        var plan = OverSales(
            NoSales,
            [SaleRegion],
            [],
            IrBuilder.WholePartitionFrame(),
            ("total", IrBuilder.WinAgg(
                AggregateFunctionId.Sum, IrBuilder.I64(nullable: true), Amount())));

        Assert.Empty(await Runner.RowsAsync(plan, SalesSource));
        await Runner.AssertEnginesAgreeAsync(plan, SalesSource);
    }

    /// <summary>
    /// ROW_NUMBER() over a partition with no ORDER BY. SQL does not say which row gets which
    /// number, only that each partition is numbered from one without repeats; both executors read
    /// the fixture in one order, and what is asserted is that they agree on it.
    /// </summary>
    [Fact]
    public async Task Row_number_with_no_order_numbers_each_partition_from_one()
    {
        var plan = OverSales(
            Sales,
            [SaleRegion],
            [],
            IrBuilder.WholePartitionFrame(),
            ("rn", IrBuilder.WinFn(WindowFunctionId.RowNumber, IrBuilder.I64())));

        var rows = await Runner.RowsAsync(plan, SalesSource);
        Assert.Equal(
            [1L, 2L, 3L, 1L, 2L, 3L],
            rows.Select(r => (long)r[3]!).ToArray());
        await Runner.AssertEnginesAgreeAsync(plan, SalesSource);
    }

    /// <summary>
    /// LAG with both an offset and a default: the first two rows of the partition step off the
    /// front and take the default rather than NULL.
    /// </summary>
    [Fact]
    public async Task Lag_with_an_offset_and_a_default_uses_the_default_off_the_front()
    {
        var plan = OverSales(
            Sales,
            [],
            [ById()],
            IrBuilder.RunningFrame(),
            ("l", IrBuilder.WinFn(
                WindowFunctionId.Lag,
                IrBuilder.I64(nullable: true),
                ignoreNulls: false,
                Amount(),
                IrBuilder.Lit(2L),
                IrBuilder.Lit(-1L))));

        var rows = await Runner.RowsAsync(plan, SalesSource);
        Assert.Equal([-1L, -1L, 10L, 20L, 20L, 30L], rows.Select(r => (long)r[3]!).ToArray());
        await Runner.AssertEnginesAgreeAsync(plan, SalesSource);
    }

    /// <summary>
    /// NTH_VALUE over a partition of three: the second value exists, and the frame before the
    /// second row does not hold it yet.
    /// </summary>
    [Fact]
    public async Task Nth_value_over_a_running_frame_appears_only_once_the_frame_reaches_it() =>
        await Runner.AssertEnginesAgreeAsync(
            OverSales(
                Sales,
                [SaleRegion],
                [ById()],
                IrBuilder.RunningFrame(),
                ("v", IrBuilder.WinFn(
                    WindowFunctionId.NthValue,
                    IrBuilder.I64(nullable: true),
                    ignoreNulls: false,
                    Amount(),
                    IrBuilder.Lit(2L)))),
            SalesSource);

    /// <summary>NTILE over six rows and four buckets, which do not divide.</summary>
    [Fact]
    public async Task Ntile_over_buckets_that_do_not_divide_the_partition() =>
        await Runner.AssertEnginesAgreeAsync(
            OverSales(
                Sales,
                [],
                [ById()],
                IrBuilder.RunningFrame(),
                ("t", IrBuilder.WinFn(
                    WindowFunctionId.Ntile, IrBuilder.I64(), ignoreNulls: false, IrBuilder.Lit(4L)))),
            SalesSource);

    /// <summary>
    /// The three exclusions over a RANGE frame whose order key ties, where they differ from each
    /// other: EXCLUDE CURRENT ROW drops one row, EXCLUDE GROUP drops the peers with it, and
    /// EXCLUDE TIES drops the peers but keeps the current row.
    /// </summary>
    /// <remarks>
    /// <c>calcite-dotnet</c> pins Calcite's own defect here — its <c>EnumerableWindow</c> excludes
    /// nothing over an unbounded frame — and Chalk implements the standard, so the two differ by
    /// design (ADR 0024). The answer comes from the reference executor and from DuckDB, never from
    /// there.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Exclusions))]
    public async Task Every_exclusion_over_a_range_frame_whose_key_ties(FrameExclusion exclusion) =>
        await Runner.AssertEnginesAgreeAsync(
            IrBuilder.Plan(IrBuilder.Window(
                // Sorted on the window's own key, because that is the order the operator relies on
                // and `sales` is stored by id.
                IrBuilder.Sort(
                    IrBuilder.Read(Sales.Name, Sales.RowType()),
                    new SortField
                    {
                        Expr = Amount(),
                        Direction = SortDirection.AscNullsLast,
                    }),
                [],
                [IrBuilder.Asc(SaleAmount, IrBuilder.I64(nullable: true))],
                IrBuilder.Frame(
                    FrameMode.Range,
                    FrameBoundKind.UnboundedPreceding,
                    FrameBoundKind.CurrentRow,
                    exclusion: exclusion),
                [("n", IrBuilder.WinAgg(AggregateFunctionId.Count, IrBuilder.I64(), Amount()))])),
            SalesSource);

    /// <summary>
    /// The order the plan promises is the order the operator relies on, so it checks it when asked.
    /// A plan that claims one ordering and delivers another is a planner bug that would otherwise be
    /// a silently wrong answer.
    /// </summary>
    [Fact]
    public async Task The_operator_reports_an_input_that_is_not_in_the_promised_order()
    {
        var unsorted = new TestTable
        {
            Name = "unsorted",
            Columns = Windowed.Columns,
            Rows = [["AAA", 2L, 1L, 1d], ["AAA", 1L, 2L, 2d]],
        };
        var source = TestData.Source(unsorted);
        var plan = IrBuilder.Plan(IrBuilder.Window(
            IrBuilder.Read(unsorted.Name, unsorted.RowType()),
            [Symbol],
            [IrBuilder.Asc(Ts, IrBuilder.I64(nullable: true))],
            IrBuilder.RunningFrame(),
            [("n", IrBuilder.WinAgg(AggregateFunctionId.Count, IrBuilder.I64()))]));

        var previous = Windowing.WindowOperator.VerifyInputOrder;
        Windowing.WindowOperator.VerifyInputOrder = true;
        try
        {
            var failure = await Assert.ThrowsAsync<ExecutionException>(
                async () => await Runner.RowsAsync(plan, source));
            Assert.Contains("not ordered", failure.Message, StringComparison.Ordinal);
        }
        finally
        {
            Windowing.WindowOperator.VerifyInputOrder = previous;
        }
    }
}

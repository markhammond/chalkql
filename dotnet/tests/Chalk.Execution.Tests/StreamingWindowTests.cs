using Chalk.Catalog;
using Chalk.Execution.Tests.Harness;
using Chalk.Execution.Windowing;
using Chalk.Ir;
using Chalk.Sources;
using Chalk.TestKit;

namespace Chalk.Execution.Tests;

/// <summary>
/// The streaming window path of D258.4 (<c>13-window-functions.md</c> §4.1) against the buffered
/// operator beside it: the same plan, the same data, the same rows down to the bit.
///
/// <para>
/// The boundaries the corpus cannot place exactly are here — a frame that crosses two batch
/// boundaries, a peer group that spans batches, NULLs in a <c>RANGE</c> order key, an empty frame,
/// single-row partitions, a batch of one and a batch larger than the input, a <c>RANGE</c> bound that
/// admits no preceding row, and a cancellation in the middle of a partition. Every one of them runs
/// both paths and compares, and the randomised property test at the end runs every streamed call kind
/// against its buffered twin over fifteen hundred generated partition sets.
/// </para>
/// </summary>
/// <remarks>
/// In the <c>window-frames</c> collection with the other window classes: they flip
/// <see cref="WindowFrames.ForceSegmentTree"/>, which is process-wide, and a window running here
/// while it is set would take a path neither test meant. The switch this class uses to pin the
/// buffered operator is per-compilation, not process-wide.
/// </remarks>
[Collection("window-frames")]
public sealed class StreamingWindowTests
{
    /// <summary>The seed the failures are reproducible from. Changing it is changing the test.</summary>
    private const int Seed = 20260915;

    private const int Cases = 1_500;

    private const int Symbol = 0;
    private const int Ts = 1;
    private const int Value = 2;
    private const int Price = 3;

    private static readonly TestTable Windowed = TestData.Windowed;
    private static readonly TestSource WindowedSource = TestData.Source(Windowed, TestData.Empty);

    // ---- which path a plan gets ------------------------------------------------------------------

    public static TheoryData<string, bool> Shapes() => new()
    {
        { "rows-preceding-to-current", true },
        { "rows-unbounded-to-current", true },
        { "rows-preceding-to-preceding", true },
        { "range-unbounded-to-current", true },
        { "range-preceding-to-current", true },
        { "range-preceding-to-preceding", true },
        { "rows-preceding-to-following", false },
        { "range-unbounded-to-unbounded", false },
        { "rows-preceding-to-current-excluding", false },
    };

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task The_path_is_chosen_from_the_frames_shape(string shape, bool streams)
    {
        var stats = new ExecutionStats();
        await Runner.RowsAsync(Plan(Windowed, Shape(shape), Sum()), WindowedSource, stats: stats);

        Assert.Equal(streams ? 1 : 0, stats.WindowsStreamed);
        Assert.Equal(streams ? 0 : 1, stats.WindowsBuffered);
    }

    [Fact]
    public async Task A_call_that_cannot_stream_buffers_the_whole_window()
    {
        // NTILE divides by the partition's total row count, which is not known until the partition
        // has ended, so it takes the sliding sum beside it down with it: the operator is one or the
        // other.
        var stats = new ExecutionStats();
        await Runner.RowsAsync(
            Plan(
                Windowed,
                Shape("rows-preceding-to-current"),
                Join(
                    Sum(),
                    [("bucket", IrBuilder.WinFn(
                        WindowFunctionId.Ntile, IrBuilder.I64(), false, IrBuilder.Lit(3L)))])),
            WindowedSource,
            stats: stats);

        Assert.Equal(0, stats.WindowsStreamed);
        Assert.Equal(1, stats.WindowsBuffered);
    }

    [Fact]
    public async Task The_host_can_pin_a_streamable_window_to_the_buffered_operator()
    {
        var stats = new ExecutionStats();
        await Runner.RowsAsync(
            Plan(Windowed, Shape("rows-preceding-to-current"), Sum()),
            WindowedSource,
            stats: stats,
            bufferedWindows: true);

        Assert.Equal(0, stats.WindowsStreamed);
        Assert.Equal(1, stats.WindowsBuffered);
    }

    // ---- the boundaries the corpus cannot place ---------------------------------------------------

    [Fact]
    public async Task A_partition_across_three_batches_with_a_frame_of_nineteen()
    {
        // Twenty-five rows in one partition: at a batch size of ten the frame of nineteen reaches back
        // across both boundaries, so every release the operator makes is checked by the answers.
        var table = Run("p", 25);
        await BothPathsAgreeAsync(
            table,
            Rows(19),
            Join(Sum(), Count(), Min(), Max(), First(), Last(), Lag(19)),
            [10, 7, 4096]);
    }

    [Fact]
    public async Task A_peer_group_spanning_batches_waits_for_its_last_row()
    {
        // Twelve rows on one order key: one peer group over three batches of five, so a RANGE frame
        // that ends at the current row cannot answer a single row until the partition has ended.
        var table = Table([.. Enumerable.Range(0, 12).Select(i => Row("p", 1L, i))]);
        await BothPathsAgreeAsync(
            table,
            Shape("range-unbounded-to-current"),
            Join(Sum(), Count(), Ranking(), Last()),
            [5, 1, 4096]);
    }

    [Fact]
    public async Task Nulls_in_the_order_key_under_a_range_frame()
    {
        // NULL keys sort last here, are peers of each other and of nothing else, and their frame is
        // their own run — which the streaming path can only close when the partition does.
        var table = Table(
        [
            Row("p", 1L, 1), Row("p", 2L, 2), Row("p", 2L, 3), Row("p", 5L, 4),
            Row("p", null, 5), Row("p", null, 6),
            Row("q", null, 7),
        ]);
        await BothPathsAgreeAsync(
            table,
            Shape("range-preceding-to-current"),
            Join(Sum(), Count(), Min(), First()),
            [1, 3, 4096]);
    }

    [Fact]
    public async Task An_empty_frame_and_a_range_bound_that_admits_no_preceding_row()
    {
        // ROWS BETWEEN 4 PRECEDING AND 2 PRECEDING names nothing for the first two rows of a run, and
        // RANGE 1 PRECEDING names nothing but the current row when the keys step by ten.
        var stepped = Table(
            [.. Enumerable.Range(0, 9).Select(i => Row(i < 5 ? "p" : "q", i * 10L, i))]);

        await BothPathsAgreeAsync(
            stepped,
            IrBuilder.Frame(
                FrameMode.Rows,
                FrameBoundKind.Preceding,
                FrameBoundKind.Preceding,
                IrBuilder.Lit(4L),
                IrBuilder.Lit(2L)),
            Join(Sum(), Count(), Min(), First(), Last()),
            [1, 4, 4096]);

        await BothPathsAgreeAsync(
            stepped,
            Shape("range-preceding-to-current"),
            Join(Sum(), Count(), Min(), First()),
            [1, 4, 4096]);
    }

    [Fact]
    public async Task Single_row_partitions_and_a_batch_larger_than_the_input()
    {
        var table = Table([.. Enumerable.Range(0, 7).Select(i => Row($"p{i}", 1L, i))]);
        await BothPathsAgreeAsync(
            table,
            Rows(3),
            Join(Sum(), Count(), Min(), Max(), Ranking(), Lag(1), First(), Last()),
            [1, 2, 1_000_000]);
    }

    /// <summary>
    /// <c>COUNT(x)</c> counts; it never adds x up. Both sliding accumulators — and D59's segment tree
    /// under them — switched on the <em>result's</em> kind, which for a COUNT is BIGINT, and so fell
    /// through to a checked integer sum of the argument that nothing ever read. A DOUBLE whose
    /// truncation is past <see cref="long.MaxValue"/> therefore raised where the answer was a count
    /// (ADR 0044). The reference executor counts, and so does DuckDB.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Count_of_a_large_magnitude_double_counts_rather_than_summing_it(bool buffered)
    {
        // Two values whose truncation is far outside a long, a NULL no COUNT(x) counts, and an
        // ordinary value after it.
        var table = Table(
        [
            ["p", 1L, 1L, 1e30d],
            ["p", 2L, 2L, -1e30d],
            ["p", 3L, 3L, null],
            ["p", 4L, 4L, 2.5d],
        ]);
        var source = TestData.Source(table);

        foreach (var batchSize in new[] { 1, 3, 4096 })
        {
            await CountsAsync(Rows(1), [1L, 2L, 1L, 1L]);
            await CountsAsync(Shape("rows-unbounded-to-current"), [1L, 2L, 2L, 3L]);

            // EXCLUDE takes D59's segment tree, whose leaves were built through the same truncation.
            // It never streams, so the forcing switch changes nothing about which operator answers.
            await CountsAsync(Rows(1, FrameExclusion.CurrentRow), [0L, 1L, 1L, 0L]);

            async Task CountsAsync(WindowFrame frame, object?[] expected)
            {
                var plan = Plan(table, frame, CountPrices());
                var stats = new ExecutionStats();
                var rows = await Runner.RowsAsync(
                    plan, source, batchSize, stats: stats, bufferedWindows: buffered);
                var reference = await Runner.RowsAsync(plan, source, batchSize, reference: true);

                Assert.Equal(expected, [.. rows.Select(r => r[^1])]);
                Assert.Equal(expected, [.. reference.Select(r => r[^1])]);
                Assert.Equal(1, stats.WindowsStreamed + stats.WindowsBuffered);
            }
        }
    }

    [Fact]
    public async Task An_empty_input_produces_no_batches()
    {
        var table = Table([]);
        var stats = new ExecutionStats();
        var rows = await Runner.RowsAsync(
            Plan(table, Rows(3), Sum()), TestData.Source(table), stats: stats);

        Assert.Empty(rows);
        Assert.Equal(1, stats.WindowsStreamed);
    }

    [Fact]
    public async Task Cancellation_mid_partition_hands_back_everything_the_window_holds()
    {
        // Zero, not "no worse than a windowless plan": the scan below used to strand the source's own
        // rentals on any cancellation, which is what this comparison used to absorb. It does not any
        // more (OperatorTests.A_cancelled_scan_hands_back_every_rental_it_holds), so the window is
        // held to the same figure a finished execution leaves.
        var table = Run("p", 4_000);
        var scanned = await CancelAfterTwoBatchesAsync(
            IrBuilder.Plan(IrBuilder.Read(table.Name, table.RowType())), table);
        var windowed = await CancelAfterTwoBatchesAsync(
            Plan(table, Rows(19), Join(Sum(), Min(), First(), Lag(5))), table);

        Assert.Equal(0, scanned);
        Assert.Equal(0, windowed);
    }

    /// <summary>
    /// Runs a plan on a fresh arena, cancels after the second batch — a long way from the four
    /// thousandth row, so the hold, the deque and the output columns are all live — and says what the
    /// arena was still owed.
    /// </summary>
    private static async Task<long> CancelAfterTwoBatchesAsync(Plan plan, TestTable table)
    {
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
                if (++seen == 2)
                {
                    await cancellation.CancelAsync();
                }
            }
        });

        return arena.OutstandingBytes;
    }

    [Fact]
    public async Task A_streaming_window_leaves_the_arena_empty()
    {
        var table = Run("p", 500);
        using var arena = new ExecutionArena();
        var compiled = Runner.Compile(
            Plan(table, Rows(19), Join(Sum(), Count(), Min(), Max(), First(), Last(), Lag(3))),
            TestData.Source(table),
            batchSize: 32);

        await foreach (var batch in compiled.ExecuteAsync(
            [], new ExecutionStats(), arena, CancellationToken.None))
        {
            batch.Dispose();
        }

        Assert.Equal(0, arena.OutstandingBytes);
    }

    // ---- every streamed evaluator against its buffered twin ---------------------------------------

    /// <summary>
    /// Fifteen hundred randomised partition sets, each under one of the six frame shapes the streaming
    /// path claims and every call it claims under that shape, run on both operators and compared bit
    /// for bit. The seed is printed with any failure, and changing it is changing the test.
    /// </summary>
    [Fact]
    public async Task The_streaming_path_agrees_with_the_buffered_one()
    {
        var random = new Random(Seed);
        var streamed = 0;
        var shapes = new HashSet<string>(StringComparer.Ordinal);

        for (var iteration = 0; iteration < Cases; iteration++)
        {
            var table = Values(random);
            var source = TestData.Source(table);
            var shape = Streamable(random);
            var frame = Shape(shape);
            shapes.Add(shape);

            var plan = Plan(table, frame, Claimed(frame));
            var batchSize = (iteration % 4) switch { 0 => 1, 1 => 3, 2 => 7, _ => 4096 };

            var streaming = new ExecutionStats();
            var buffered = new ExecutionStats();
            var actual = await Runner.RowsAsync(plan, source, batchSize, stats: streaming);
            var expected = await Runner.RowsAsync(
                plan, source, batchSize, stats: buffered, bufferedWindows: true);

            streamed += (int)streaming.WindowsStreamed;
            Assert.Equal(0, buffered.WindowsStreamed);
            Assert.True(
                Identical(expected, actual),
                $"case {iteration} (seed {Seed}, batch {batchSize}, shape {shape}) disagreed.\n"
                + $"plan: {plan.Root.ToPlanText()}\n"
                + $"buffered:  {Describe(expected)}\nstreaming: {Describe(actual)}");
        }

        Assert.Equal(Cases, streamed);
        Assert.Equal(6, shapes.Count);
    }

    // ---- the plans --------------------------------------------------------------------------------

    private static Plan Plan(
        TestTable table, WindowFrame frame, (string Name, WindowCall Call)[] calls) =>
        IrBuilder.Plan(IrBuilder.Window(
            IrBuilder.Collated(
                IrBuilder.Read(table.Name, table.RowType()),
                (Symbol, SortDirection.AscNullsLast),
                (Ts, SortDirection.AscNullsLast)),
            [Symbol],
            [IrBuilder.Asc(Ts, IrBuilder.I64(nullable: true))],
            frame,
            calls));

    private static (string Name, WindowCall Call)[] Join(
        params IEnumerable<(string Name, WindowCall Call)>[] groups) => [.. groups.SelectMany(g => g)];

    private static WindowFrame Shape(string name) => name switch
    {
        "rows-preceding-to-current" => Rows(2),
        "rows-unbounded-to-current" => IrBuilder.Frame(
            FrameMode.Rows, FrameBoundKind.UnboundedPreceding, FrameBoundKind.CurrentRow),
        "rows-preceding-to-preceding" => IrBuilder.Frame(
            FrameMode.Rows, FrameBoundKind.Preceding, FrameBoundKind.Preceding,
            IrBuilder.Lit(3L), IrBuilder.Lit(1L)),
        "range-unbounded-to-current" => IrBuilder.RunningFrame(),
        "range-preceding-to-current" => IrBuilder.Frame(
            FrameMode.Range, FrameBoundKind.Preceding, FrameBoundKind.CurrentRow, IrBuilder.Lit(1L)),
        "range-preceding-to-preceding" => IrBuilder.Frame(
            FrameMode.Range, FrameBoundKind.Preceding, FrameBoundKind.Preceding,
            IrBuilder.Lit(3L), IrBuilder.Lit(1L)),
        "rows-preceding-to-following" => IrBuilder.RowsFrame(2, 1),
        "range-unbounded-to-unbounded" => IrBuilder.WholePartitionFrame(),
        _ => Rows(2, FrameExclusion.CurrentRow),
    };

    private static string Streamable(Random random) => random.Next(6) switch
    {
        0 => "rows-preceding-to-current",
        1 => "rows-unbounded-to-current",
        2 => "rows-preceding-to-preceding",
        3 => "range-unbounded-to-current",
        4 => "range-preceding-to-current",
        _ => "range-preceding-to-preceding",
    };

    private static WindowFrame Rows(
        long preceding, FrameExclusion exclusion = FrameExclusion.NoOthers) =>
        IrBuilder.Frame(
            FrameMode.Rows,
            FrameBoundKind.Preceding,
            FrameBoundKind.CurrentRow,
            IrBuilder.Lit(preceding),
            null,
            exclusion);

    /// <summary>
    /// Every call the streaming path claims under <paramref name="frame"/>. A frame that starts at the
    /// partition's first row leaves out the ones that read the frame's own rows — the extremum's deque
    /// and a floating-point sum's periodic rebuild — because those stay buffered under it.
    /// </summary>
    private static (string Name, WindowCall Call)[] Claimed(WindowFrame frame)
    {
        var bounded = frame.Lower.Kind is FrameBoundKind.Preceding or FrameBoundKind.CurrentRow;
        var running = frame.Lower.Kind == FrameBoundKind.UnboundedPreceding
            && frame.Upper.Kind == FrameBoundKind.CurrentRow;

        var always = Join(Sum(), Count(), Ranking(), Lag(2), Last());
        if (bounded)
        {
            return Join(always, Min(), Max(), First(), Mean());
        }

        return running ? Join(always, First(), Any()) : always;
    }

    private static (string, WindowCall)[] Sum() =>
    [
        ("total", IrBuilder.WinAgg(AggregateFunctionId.Sum, IrBuilder.I64(nullable: true), ValueRef())),
    ];

    private static (string, WindowCall)[] Count() =>
    [
        ("rows", IrBuilder.WinAgg(AggregateFunctionId.Count, IrBuilder.I64())),
        ("values", IrBuilder.WinAgg(AggregateFunctionId.Count, IrBuilder.I64(), ValueRef())),
    ];

    /// <summary>A COUNT over the DOUBLE column, which is the one call that reads no value at all.</summary>
    private static (string, WindowCall)[] CountPrices() =>
    [
        ("prices", IrBuilder.WinAgg(AggregateFunctionId.Count, IrBuilder.I64(), PriceRef())),
    ];

    private static (string, WindowCall)[] Min() =>
    [
        ("lo", IrBuilder.WinAgg(AggregateFunctionId.Min, IrBuilder.I64(nullable: true), ValueRef())),
    ];

    private static (string, WindowCall)[] Max() =>
    [
        ("hi", IrBuilder.WinAgg(AggregateFunctionId.Max, IrBuilder.I64(nullable: true), ValueRef())),
    ];

    private static (string, WindowCall)[] Mean() =>
    [
        ("mean", IrBuilder.WinAgg(AggregateFunctionId.Avg, IrBuilder.Fp64(nullable: true), PriceRef())),
    ];

    private static (string, WindowCall)[] Any() =>
    [
        ("any", IrBuilder.WinAgg(
            AggregateFunctionId.AnyValue, IrBuilder.I64(nullable: true), ValueRef())),
    ];

    private static (string, WindowCall)[] Ranking() =>
    [
        ("n", IrBuilder.WinFn(WindowFunctionId.RowNumber, IrBuilder.I64())),
        ("rank", IrBuilder.WinFn(WindowFunctionId.Rank, IrBuilder.I64())),
        ("dense", IrBuilder.WinFn(WindowFunctionId.DenseRank, IrBuilder.I64())),
    ];

    private static (string, WindowCall)[] First() =>
    [
        ("first", IrBuilder.WinFn(
            WindowFunctionId.FirstValue, IrBuilder.I64(nullable: true), false, ValueRef())),
    ];

    private static (string, WindowCall)[] Last() =>
    [
        ("last", IrBuilder.WinFn(
            WindowFunctionId.LastValue, IrBuilder.I64(nullable: true), false, ValueRef())),
    ];

    private static (string, WindowCall)[] Lag(long offset) =>
    [
        ("back", IrBuilder.WinFn(
            WindowFunctionId.Lag,
            IrBuilder.I64(nullable: true),
            false,
            ValueRef(),
            IrBuilder.Lit(offset),
            IrBuilder.Lit(-1L))),
    ];

    private static Expr ValueRef() => IrBuilder.Ref(Value, IrBuilder.I64(nullable: true));

    private static Expr PriceRef() => IrBuilder.Ref(Price, IrBuilder.Fp64(nullable: true));

    // ---- the data ---------------------------------------------------------------------------------

    private static TestTable Table(IReadOnlyList<object?[]> rows) => new()
    {
        Name = "generated",
        Columns =
        [
            ("symbol", ChalkType.String(nullable: true)),
            ("ts", ChalkType.Int64(nullable: true)),
            ("value", ChalkType.Int64(nullable: true)),
            ("price", ChalkType.Float64(nullable: true)),
        ],
        Rows = rows,
    };

    private static object?[] Row(string? symbol, long? ts, int seed) =>
    [
        symbol,
        ts,
        seed % 5 == 3 ? null : (long)((seed * 7) % 23),
        seed % 7 == 5 ? null : seed * 1.5d,
    ];

    private static TestTable Run(string symbol, int rows) =>
        Table([.. Enumerable.Range(0, rows).Select(i => Row(symbol, i, i))]);

    /// <summary>
    /// A random partition set with duplicate order keys, NULL keys and NULL values, in the order the
    /// plan promises its window: (symbol ASC, ts ASC NULLS LAST). A generator that produced any other
    /// order would be testing the input-order check rather than the frames.
    /// </summary>
    private static TestTable Values(Random random)
    {
        var rows = new List<object?[]>();
        var seed = 0;
        for (var p = 0; p < random.Next(1, 4); p++)
        {
            var ts = 0L;
            var partition = new List<object?[]>();
            for (var i = 0; i < random.Next(1, 9); i++)
            {
                ts += random.Next(3) == 0 && i > 0 ? 0 : random.Next(1, 4);
                partition.Add(Row($"p{p}", random.Next(9) == 0 ? null : ts, seed++));
            }

            rows.AddRange(partition.Where(r => r[1] is not null));
            rows.AddRange(partition.Where(r => r[1] is null));
        }

        return Table(rows);
    }

    // ---- the comparison ---------------------------------------------------------------------------

    private static async Task BothPathsAgreeAsync(
        TestTable table,
        WindowFrame frame,
        (string Name, WindowCall Call)[] calls,
        int[] batchSizes)
    {
        var plan = Plan(table, frame, calls);
        var source = TestData.Source(table);
        foreach (var batchSize in batchSizes)
        {
            var streaming = new ExecutionStats();
            var buffered = new ExecutionStats();
            var actual = await Runner.RowsAsync(plan, source, batchSize, stats: streaming);
            var expected = await Runner.RowsAsync(
                plan, source, batchSize, stats: buffered, bufferedWindows: true);
            var reference = await Runner.RowsAsync(plan, source, batchSize, reference: true);

            Assert.Equal(1, streaming.WindowsStreamed);
            Assert.Equal(1, buffered.WindowsBuffered);
            Assert.True(
                Identical(expected, actual),
                $"batch {batchSize}, {table.Rows.Count} rows: the two paths disagreed.\n"
                + $"buffered:  {Describe(expected)}\nstreaming: {Describe(actual)}");
            Assert.True(
                Identical(reference, actual),
                $"batch {batchSize}, {table.Rows.Count} rows: streaming disagreed with the reference.\n"
                + $"reference: {Describe(reference)}\nstreaming: {Describe(actual)}");
        }
    }

    /// <summary>
    /// Equal down to the bit: a double is compared by its bits, so <c>-0.0</c> and <c>0.0</c> are two
    /// different answers and so are two NaNs with different payloads. "Byte-identical" is the claim
    /// D258.4 makes, and this is what checks it.
    /// </summary>
    private static bool Identical(List<object?[]> expected, List<object?[]> actual)
    {
        if (expected.Count != actual.Count)
        {
            return false;
        }

        for (var r = 0; r < expected.Count; r++)
        {
            if (expected[r].Length != actual[r].Length)
            {
                return false;
            }

            for (var c = 0; c < expected[r].Length; c++)
            {
                if (!SameValue(expected[r][c], actual[r][c]))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool SameValue(object? expected, object? actual) => (expected, actual) switch
    {
        (double left, double right) =>
            BitConverter.DoubleToInt64Bits(left) == BitConverter.DoubleToInt64Bits(right),
        (float left, float right) =>
            BitConverter.SingleToInt32Bits(left) == BitConverter.SingleToInt32Bits(right),
        _ => Equals(expected, actual),
    };

    private static string Describe(List<object?[]> rows) =>
        string.Join("; ", rows.Select(r => string.Join(",", r.Select(v => v?.ToString() ?? "null"))));
}

using Chalk.Catalog;
using Chalk.Execution.Tests.Harness;
using Chalk.Execution.Windowing;
using Chalk.Ir;
using Chalk.TestKit;

namespace Chalk.Execution.Tests;

/// <summary>
/// §10's property tests, seeded and reproducible: the five algorithms step 19 adds, each against a
/// naive implementation of the same contract.
/// </summary>
/// <remarks>
/// <para>
/// The pairs are: a hop's window membership against a literal enumeration; a session's assignment
/// against a scan that re-derives it; the counted multiset of D56 against recomputing the distinct
/// set per frame; the segment tree of D59 against the sliding fast paths it replaces; and
/// <c>MODE</c>'s tie rule against a count-and-pick.
/// </para>
/// <para>
/// §10 also asks for percentiles over a frame against sort-and-pick. Calcite 1.42 cannot express one
/// — <c>WITHIN GROUP</c> is not an aggregate call, so <c>OVER</c> refuses it (V22) — so neither the
/// rank tree nor its property test exists; ADR 0018 records that.
/// </para>
/// </remarks>
/// <remarks>
/// The three window classes share a collection because <see cref="WindowFrames.ForceSegmentTree"/>
/// is process-wide: one test flips it, and a window running concurrently in another class would take
/// the other code path while it is set.
/// </remarks>
[Collection("window-frames")]
public sealed class WindowsIiPropertyTests
{
    /// <summary>The seed the failures are reproducible from. Changing it is changing the test.</summary>
    private const int Seed = 20260909;

    private const int Cases = 1_500;

    private const int Symbol = 0;
    private const int Ts = 1;
    private const int Value = 2;

    /// <summary>Five minutes in the nanoseconds a TIMESTAMP(9) counts.</summary>
    private const long FiveMinutes = 5L * 60 * 1_000_000_000;

    // ---- D55: hop membership ---------------------------------------------------------------------

    /// <summary>
    /// A row belongs to every window <c>[s, s + size)</c> whose start is a multiple of the slide from
    /// the epoch. The operator counts down from the row's own bucket; this counts up from the
    /// earliest possible window and asks each one whether it contains the row.
    /// </summary>
    [Fact]
    public async Task Hop_membership_agrees_with_a_literal_enumeration()
    {
        var random = new Random(Seed);
        var dropped = 0;

        for (var iteration = 0; iteration < 200; iteration++)
        {
            var slide = FiveMinutes * random.Next(1, 4);
            var size = FiveMinutes * random.Next(1, 5);
            var table = Times(random, out var nulls);
            var source = TestData.Source(table);

            var plan = IrBuilder.Plan(IrBuilder.Hop(
                IrBuilder.Read(table.Name, table.RowType()),
                Ts,
                Interval(slide),
                Interval(size)));

            var expected = new List<object?[]>();
            foreach (var row in table.Rows)
            {
                if (row[Ts] is not long t)
                {
                    continue;
                }

                var lowest = FloorTo(t - size + 1, slide);
                var found = 0;
                for (var start = lowest; start <= t; start += slide)
                {
                    if (start <= t && t < start + size)
                    {
                        expected.Add([.. row, start, start + size]);
                        found++;
                    }
                }

                dropped += found == 0 ? 1 : 0;
            }

            var batchSize = iteration % 5 == 0 ? 1 : 4096;
            var actual = await Runner.RowsAsync(plan, source, batchSize);
            Assert.True(
                Same(expected, actual),
                $"case {iteration} (seed {Seed}, slide {slide}, size {size}, {nulls} NULL times) "
                + $"disagreed.\nexpected: {Describe(expected)}\nactual:   {Describe(actual)}");
        }

        Assert.True(dropped > 0, "no case produced a row that falls in no window at all");
    }

    // ---- D55: session assignment ------------------------------------------------------------------

    /// <summary>
    /// A session is a run whose consecutive gaps are all below the gap. The operator finds the runs
    /// in one pass over an ordered input; this walks the rows again and re-derives each row's bounds
    /// from scratch.
    /// </summary>
    [Fact]
    public async Task Session_assignment_agrees_with_a_naive_scan()
    {
        var random = new Random(Seed);
        var multiSession = 0;

        for (var iteration = 0; iteration < 200; iteration++)
        {
            var gap = FiveMinutes * random.Next(1, 4);
            var table = Times(random, out _);
            var source = TestData.Source(table);

            var plan = IrBuilder.Plan(IrBuilder.Session(
                IrBuilder.Collated(
                    IrBuilder.Read(table.Name, table.RowType()),
                    (Symbol, SortDirection.AscNullsLast),
                    (Ts, SortDirection.AscNullsLast)),
                [Symbol],
                Ts,
                Interval(gap)));

            var expected = new List<object?[]>();
            foreach (var group in table.Rows.GroupBy(r => (string?)r[Symbol]))
            {
                var timed = group.Where(r => r[Ts] is long).ToList();
                var i = 0;
                var sessions = 0;
                while (i < timed.Count)
                {
                    var first = (long)timed[i][Ts]!;
                    var last = first;
                    var j = i;
                    while (j + 1 < timed.Count && (long)timed[j + 1][Ts]! < last + gap)
                    {
                        j++;
                        last = (long)timed[j][Ts]!;
                    }

                    for (var k = i; k <= j; k++)
                    {
                        expected.Add([.. timed[k], first, last + gap]);
                    }

                    sessions++;
                    i = j + 1;
                }

                multiSession += sessions > 1 ? 1 : 0;
                foreach (var row in group.Where(r => r[Ts] is null))
                {
                    expected.Add([.. row, null, null]);
                }
            }

            var batchSize = iteration % 5 == 0 ? 1 : 4096;
            var actual = await Runner.RowsAsync(plan, source, batchSize);
            Assert.True(
                Same(expected, actual),
                $"case {iteration} (seed {Seed}, gap {gap}) disagreed.\n"
                + $"expected: {Describe(expected)}\nactual:   {Describe(actual)}");
        }

        Assert.True(multiSession > 0, "no case produced a partition with more than one session");
    }

    // ---- D56: the counted multiset ----------------------------------------------------------------

    /// <summary>
    /// <c>COUNT(DISTINCT)</c> and <c>SUM(DISTINCT)</c> over random frames: the operator adds a row as
    /// it enters and removes it as it leaves, and the reference executor rebuilds the distinct set
    /// per frame. Two algorithms, one contract.
    /// </summary>
    [Fact]
    public async Task The_counted_multiset_agrees_with_recomputing_the_distinct_set()
    {
        var random = new Random(Seed);

        for (var iteration = 0; iteration < Cases; iteration++)
        {
            var table = Values(random);
            var source = TestData.Source(table);
            var frame = Frame(random);

            var plan = IrBuilder.Plan(IrBuilder.Window(
                IrBuilder.Collated(
                    IrBuilder.Read(table.Name, table.RowType()),
                    (Symbol, SortDirection.AscNullsLast),
                    (Ts, SortDirection.AscNullsLast)),
                [Symbol],
                [IrBuilder.Asc(Ts, IrBuilder.I64(nullable: true))],
                frame,
                [
                    ("n", IrBuilder.WinAggDistinct(
                        AggregateFunctionId.Count,
                        IrBuilder.I64(),
                        IrBuilder.Ref(Value, IrBuilder.I64(nullable: true)))),
                    ("total", IrBuilder.WinAggDistinct(
                        AggregateFunctionId.Sum,
                        IrBuilder.I64(nullable: true),
                        IrBuilder.Ref(Value, IrBuilder.I64(nullable: true)))),
                ]));

            var batchSize = iteration % 20 == 0 ? 1 : 4096;
            var actual = await Runner.RowsAsync(plan, source, batchSize);
            var expected = await Runner.RowsAsync(plan, source, batchSize, reference: true);

            Assert.True(
                Same(expected, actual),
                $"case {iteration} (seed {Seed}) disagreed.\n"
                + $"frame: {plan.Root.ToPlanText()}\n"
                + $"expected: {Describe(expected)}\nactual:   {Describe(actual)}");
        }
    }

    // ---- D59: the segment tree --------------------------------------------------------------------

    /// <summary>
    /// The general frame engine against the fast paths it does not replace. Every frame is run twice
    /// — once as the operator would take it, once with <see cref="WindowFrames.ForceSegmentTree"/> on
    /// — so the sliding accumulator, the monotonic deque and the tree are compared over the same
    /// thousands of random frames, in every <c>EXCLUDE</c> mode.
    /// </summary>
    /// <remarks>
    /// Both runs are pinned to the buffered operator, because those are the two implementations this
    /// test is about: since D258.4 a frame that ends at or before the current row would otherwise take
    /// the streaming path, and the sliding fast paths would go unread here. Streaming is compared
    /// against the buffered operator by its own property test in <c>StreamingWindowTests</c>.
    /// </remarks>
    [Fact]
    public async Task The_segment_tree_agrees_with_the_sliding_fast_paths()
    {
        var random = new Random(Seed);
        var exclusions = new HashSet<FrameExclusion>();

        for (var iteration = 0; iteration < Cases; iteration++)
        {
            var table = Values(random);
            var source = TestData.Source(table);
            var frame = Frame(random);
            exclusions.Add(frame.Exclusion);

            var plan = IrBuilder.Plan(IrBuilder.Window(
                IrBuilder.Collated(
                    IrBuilder.Read(table.Name, table.RowType()),
                    (Symbol, SortDirection.AscNullsLast),
                    (Ts, SortDirection.AscNullsLast)),
                [Symbol],
                [IrBuilder.Asc(Ts, IrBuilder.I64(nullable: true))],
                frame,
                [
                    ("total", IrBuilder.WinAgg(
                        AggregateFunctionId.Sum,
                        IrBuilder.I64(nullable: true),
                        IrBuilder.Ref(Value, IrBuilder.I64(nullable: true)))),
                    ("rows", IrBuilder.WinAgg(AggregateFunctionId.Count, IrBuilder.I64())),
                    ("values", IrBuilder.WinAgg(
                        AggregateFunctionId.Count,
                        IrBuilder.I64(),
                        IrBuilder.Ref(Value, IrBuilder.I64(nullable: true)))),
                    ("lo", IrBuilder.WinAgg(
                        AggregateFunctionId.Min,
                        IrBuilder.I64(nullable: true),
                        IrBuilder.Ref(Value, IrBuilder.I64(nullable: true)))),
                    ("hi", IrBuilder.WinAgg(
                        AggregateFunctionId.Max,
                        IrBuilder.I64(nullable: true),
                        IrBuilder.Ref(Value, IrBuilder.I64(nullable: true)))),
                ]));

            var fast = await Runner.RowsAsync(plan, source, 4096, bufferedWindows: true);

            List<object?[]> tree;
            WindowFrames.ForceSegmentTree = true;
            try
            {
                tree = await Runner.RowsAsync(plan, source, 4096, bufferedWindows: true);
            }
            finally
            {
                WindowFrames.ForceSegmentTree = false;
            }

            Assert.True(
                Same(fast, tree),
                $"case {iteration} (seed {Seed}) disagreed.\n"
                + $"frame: {plan.Root.ToPlanText()}\n"
                + $"fast paths:   {Describe(fast)}\nsegment tree: {Describe(tree)}");
        }

        Assert.Equal(4, exclusions.Count);
    }

    // ---- D57: MODE's tie rule ---------------------------------------------------------------------

    /// <summary>
    /// <c>MODE</c> returns the most frequent non-NULL value, and D57 breaks a tie with the smallest.
    /// The generator draws from a tiny alphabet so ties are the common case rather than the rare one.
    /// </summary>
    [Fact]
    public async Task Mode_breaks_a_tie_with_the_smallest_value()
    {
        var random = new Random(Seed);
        var ties = 0;

        for (var iteration = 0; iteration < 500; iteration++)
        {
            var table = Values(random, alphabet: 3);
            var source = TestData.Source(table);
            var row = table.RowType();

            var plan = IrBuilder.Plan(IrBuilder.HashAggregate(
                IrBuilder.Read(table.Name, row),
                [Symbol],
                [("busiest", IrBuilder.Agg(
                    AggregateFunctionId.Mode,
                    IrBuilder.I64(nullable: true),
                    IrBuilder.Ref(Value, IrBuilder.I64(nullable: true))))]));

            var expected = new List<object?[]>();
            foreach (var group in table.Rows.GroupBy(r => (string?)r[Symbol]))
            {
                var counts = group
                    .Select(r => r[Value])
                    .Where(v => v is not null)
                    .GroupBy(v => (long)v!)
                    .Select(g => (Value: g.Key, Count: g.Count()))
                    .ToList();
                if (counts.Count == 0)
                {
                    expected.Add([group.Key, null]);
                    continue;
                }

                var best = counts.Max(c => c.Count);
                ties += counts.Count(c => c.Count == best) > 1 ? 1 : 0;
                expected.Add([group.Key, counts.Where(c => c.Count == best).Min(c => c.Value)]);
            }

            var actual = await Runner.RowsAsync(plan, source, iteration % 7 == 0 ? 1 : 4096);
            Assert.True(
                Same(Order(expected), Order(actual)),
                $"case {iteration} (seed {Seed}) disagreed.\n"
                + $"expected: {Describe(expected)}\nactual:   {Describe(actual)}");
        }

        Assert.True(ties > 100, $"only {ties} cases had a tie; the generator is not reaching them");
    }

    // ---- generators -------------------------------------------------------------------------------

    /// <summary>A table with a partition key, a nanosecond timestamp and a value, already ordered.</summary>
    private static TestTable Times(Random random, out int nulls)
    {
        var rows = new List<object?[]>();
        nulls = 0;
        for (var p = 0; p < random.Next(1, 4); p++)
        {
            var ts = (long)random.Next(0, 4) * FiveMinutes;
            for (var i = 0; i < random.Next(1, 8); i++)
            {
                ts += (long)random.Next(1, 8) * (FiveMinutes / 5);
                rows.Add([$"p{p}", ts, (long)random.Next(0, 100)]);
            }

            // A NULL time belongs to no window and to no session, and sorts last in its partition.
            if (random.Next(3) == 0)
            {
                nulls++;
                rows.Add([$"p{p}", null, (long)random.Next(0, 100)]);
            }
        }

        return Table(rows, IrBuilder.Timestamp(9, nullable: true));
    }

    /// <summary>The same shape with a small integer "timestamp", for the frame tests.</summary>
    private static TestTable Values(Random random, int alphabet = 8)
    {
        var rows = new List<object?[]>();
        for (var p = 0; p < random.Next(1, 4); p++)
        {
            var ts = 0L;
            for (var i = 0; i < random.Next(1, 8); i++)
            {
                ts += random.Next(3) == 0 && i > 0 ? 0 : 1;
                object? value = random.Next(6) == 0 ? null : (long)random.Next(alphabet);
                rows.Add([$"p{p}", ts, value]);
            }
        }

        return Table(rows, IrBuilder.I64(nullable: true));
    }

    private static TestTable Table(List<object?[]> rows, Ir.Type timeType) => new()
    {
        Name = "generated",
        Columns =
        [
            ("symbol", ChalkType.String(nullable: true)),
            ("ts", ChalkType.FromProto(timeType)),
            ("value", ChalkType.Int64(nullable: true)),
        ],
        Rows = rows,
    };

    /// <summary>A random frame the IR accepts, in one of the four exclusion modes.</summary>
    private static WindowFrame Frame(Random random)
    {
        var exclusion = random.Next(4) switch
        {
            0 => FrameExclusion.CurrentRow,
            1 => FrameExclusion.Group,
            2 => FrameExclusion.Ties,
            _ => FrameExclusion.NoOthers,
        };

        while (true)
        {
            var lower = random.Next(4) switch
            {
                0 => FrameBoundKind.UnboundedPreceding,
                1 => FrameBoundKind.Preceding,
                2 => FrameBoundKind.CurrentRow,
                _ => FrameBoundKind.Following,
            };
            var upper = random.Next(4) switch
            {
                0 => FrameBoundKind.Preceding,
                1 => FrameBoundKind.CurrentRow,
                2 => FrameBoundKind.Following,
                _ => FrameBoundKind.UnboundedFollowing,
            };

            if (lower == FrameBoundKind.Following && upper == FrameBoundKind.CurrentRow)
            {
                continue;
            }

            return IrBuilder.Frame(
                random.Next(3) == 0 ? FrameMode.Range : FrameMode.Rows,
                lower,
                upper,
                lower is FrameBoundKind.Preceding or FrameBoundKind.Following
                    ? IrBuilder.Lit((long)random.Next(4))
                    : null,
                upper is FrameBoundKind.Preceding or FrameBoundKind.Following
                    ? IrBuilder.Lit((long)random.Next(4))
                    : null,
                exclusion);
        }
    }

    /// <summary>The IR carries an interval as microseconds; the tests count in nanoseconds.</summary>
    private static Expr Interval(long nanoseconds) => IrBuilder.LitIntervalDay(nanoseconds / 1_000);

    /// <summary>The largest multiple of <paramref name="step"/> at or below <paramref name="value"/>.</summary>
    private static long FloorTo(long value, long step)
    {
        var remainder = value % step;
        return remainder < 0 ? value - remainder - step : value - remainder;
    }

    private static List<object?[]> Order(List<object?[]> rows) =>
        [.. rows.OrderBy(r => (string?)r[0], StringComparer.Ordinal)];

    private static bool Same(List<object?[]> expected, List<object?[]> actual)
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
                if (!Equals(expected[r][c], actual[r][c]))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static string Describe(List<object?[]> rows) =>
        string.Join("; ", rows.Select(r => string.Join(",", r.Select(v => v?.ToString() ?? "null"))));
}

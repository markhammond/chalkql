using Chalk.Catalog;
using Chalk.Execution.Tests.Harness;
using Chalk.Execution.Windowing;
using Chalk.Ir;
using Chalk.TestKit;

namespace Chalk.Execution.Tests;

/// <summary>
/// §7's property test: the sliding accumulators against naive recomputation, over random arrays and
/// random frames.
///
/// <para>
/// The engine's window operator adds a row as it enters the frame and subtracts it as it leaves, and
/// keeps a monotonic deque for the extrema; the reference executor materialises each frame from
/// scratch and folds over it. Those are different algorithms with the same contract, so running both
/// over a thousand seeded shapes — partitions of one row and of twelve, ties on the order key, NULL
/// values, frames that name no rows at all, frames whose bounds are both behind the current row —
/// is the cheapest way to find the case a hand-written test would not have thought of.
/// </para>
/// </summary>
/// <remarks>
/// The three window classes share a collection because <see cref="WindowFrames.ForceSegmentTree"/>
/// is process-wide: one test flips it, and a window running concurrently in another class would take
/// the other code path while it is set.
/// </remarks>
[Collection("window-frames")]
public sealed class WindowPropertyTests
{
    /// <summary>The seed the failures are reproducible from. Changing it is changing the test.</summary>
    private const int Seed = 20260908;

    /// <summary>How many random shapes to run. Each one exercises six calls over one frame.</summary>
    private const int Cases = 3_000;

    private const int Symbol = 0;
    private const int Ts = 1;
    private const int Value = 2;

    [Fact]
    public async Task Sliding_accumulators_agree_with_naive_recomputation_over_random_frames()
    {
        var random = new Random(Seed);
        var empty = 0;
        var withNulls = 0;

        for (var iteration = 0; iteration < Cases; iteration++)
        {
            var table = Rows(random, out var nulls);
            withNulls += nulls > 0 ? 1 : 0;
            var source = TestData.Source(table);
            var frame = Frame(random, out var names);
            empty += names ? 0 : 1;

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
                    ("first", IrBuilder.WinFn(
                        WindowFunctionId.FirstValue,
                        IrBuilder.I64(nullable: true),
                        ignoreNulls: false,
                        IrBuilder.Ref(Value, IrBuilder.I64(nullable: true)))),
                ]));

            // Every twentieth case also runs one row per batch, which is where an emission that read
            // the wrong row would show.
            var batchSize = iteration % 20 == 0 ? 1 : 4096;
            var actual = await Runner.RowsAsync(plan, source, batchSize);
            var expected = await Runner.RowsAsync(plan, source, batchSize, reference: true);

            Assert.True(
                Same(expected, actual),
                $"case {iteration} (seed {Seed}) disagreed.\n"
                + $"frame: {PlanPrinter.Print(plan.Root)}\n"
                + $"rows: {Describe(table)}\n"
                + $"expected: {Describe(expected)}\n"
                + $"actual:   {Describe(actual)}");
        }

        // The generator is only useful if it reaches the interesting shapes; say so rather than
        // assuming it.
        Assert.True(empty > 0, "no case produced a frame that names no rows");
        Assert.True(withNulls > Cases / 4, $"only {withNulls} of {Cases} cases had a NULL value");
    }

    /// <summary>
    /// A random table, already in the order a window's input arrives in: partitions in key order,
    /// order keys ascending within each, sometimes tied.
    /// </summary>
    private static TestTable Rows(Random random, out int nulls)
    {
        var partitions = random.Next(1, 4);
        var rows = new List<object?[]>();
        nulls = 0;

        for (var p = 0; p < partitions; p++)
        {
            var length = random.Next(1, 6);
            var ts = 0L;
            for (var i = 0; i < length; i++)
            {
                // A third of the rows repeat the previous timestamp, which makes peer groups.
                ts += random.Next(3) == 0 && i > 0 ? 0 : 1;
                object? value = random.Next(5) == 0 ? null : (long)random.Next(-20, 21);
                nulls += value is null ? 1 : 0;
                rows.Add([$"p{p}", ts, value]);
            }
        }

        return new TestTable
        {
            Name = "generated",
            Columns =
            [
                ("symbol", ChalkType.String(nullable: true)),
                ("ts", ChalkType.Int64(nullable: true)),
                ("value", ChalkType.Int64(nullable: true)),
            ],
            Rows = rows,
        };
    }

    /// <summary>
    /// A random frame the IR accepts. <c>names</c> reports whether it can ever hold a row, so the
    /// test can check the generator reaches the empty case too.
    /// </summary>
    private static WindowFrame Frame(Random random, out bool names)
    {
        var mode = random.Next(3) == 0 ? FrameMode.Range : FrameMode.Rows;
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

            // The one pairing the IR refuses: a frame that starts after it ends, for every row.
            if (lower == FrameBoundKind.Following && upper == FrameBoundKind.CurrentRow)
            {
                continue;
            }

            var back = (long)random.Next(4);
            var forward = (long)random.Next(4);
            names = !(lower == FrameBoundKind.Following
                && upper == FrameBoundKind.Preceding);
            return IrBuilder.Frame(
                mode,
                lower,
                upper,
                lower is FrameBoundKind.Preceding or FrameBoundKind.Following
                    ? IrBuilder.Lit(back)
                    : null,
                upper is FrameBoundKind.Preceding or FrameBoundKind.Following
                    ? IrBuilder.Lit(forward)
                    : null,
                exclusion);
        }
    }

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

    private static string Describe(TestTable table) =>
        string.Join("; ", table.Rows.Select(r => string.Join(",", r.Select(v => v?.ToString() ?? "null"))));

    private static string Describe(List<object?[]> rows) =>
        string.Join("; ", rows.Select(r => string.Join(",", r.Select(v => v?.ToString() ?? "null"))));
}

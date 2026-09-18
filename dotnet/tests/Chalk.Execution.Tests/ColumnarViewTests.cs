using Chalk.Catalog;
using Chalk.Execution.Tests.Harness;
using Chalk.Ir;
using Chalk.Sources.Poco;
using Chalk.Sources;
using Chalk.TestKit;
using Chalk.Tests;
using CatalogContext = Chalk.Catalog.CatalogContext;

namespace Chalk.Execution.Tests;

/// <summary>
/// The internals of <c>15-zero-allocation-execution.md</c> §1 to §4: column views instead of Arrow
/// batches between operators (D61), selection vectors with a compaction threshold (D62), the
/// synchronous pull path (D63) and what a fresh operator tree costs (D64).
/// </summary>
public sealed class ColumnarViewTests
{
    private sealed record Bar(string Symbol, long Volume, double Close);

    private static readonly TestSource Source = TestData.Source(TestData.Numbers, TestData.Series(20));

    // ---- §1: views, and the lifetime check -------------------------------------------------

    /// <summary>
    /// A view is valid until its producer's next batch, and under
    /// <c>ExecutionOptions.ValidateBatchLifetimes</c> reading one after that fails rather than
    /// quietly returning the next batch's rows (D61).
    /// </summary>
    [Fact]
    public async Task A_view_kept_past_its_batch_fails_when_lifetimes_are_validated()
    {
        var table = TestData.Series(20);
        var plan = IrBuilder.Plan(IrBuilder.Read(table.Name, table.RowType()));
        var compiled = Runner.Compile(
            plan, TestData.Source(table), batchSize: 4, validateLifetimes: true);

        using var arena = new ExecutionArena();
        ColumnView retained = default;
        var seen = 0;
        await foreach (var batch in compiled.ExecuteColumnarAsync(
            [], new ExecutionStats(), arena, TestContext.Current.CancellationToken))
        {
            if (seen++ == 0)
            {
                retained = batch.Column(0);

                // Still this batch, so it still reads.
                Assert.Equal(4, retained.Length);
            }
        }

        Assert.True(seen > 1, "the query has to produce more than one batch for the check to mean anything.");
        var failure = Assert.Throws<BatchLifetimeException>(() => retained.Lanes<long>().Length);
        Assert.Equal(1, failure.CapturedGeneration);
        Assert.True(failure.CurrentGeneration > 1);
    }

    /// <summary>With the check off a stale view is not policed, which is what release builds pay for.</summary>
    [Fact]
    public async Task A_view_carries_no_owner_when_lifetimes_are_not_validated()
    {
        var table = TestData.Series(20);
        var plan = IrBuilder.Plan(IrBuilder.Read(table.Name, table.RowType()));
        var compiled = Runner.Compile(
            plan, TestData.Source(table), batchSize: 4, validateLifetimes: false);

        using var arena = new ExecutionArena();
        ColumnView retained = default;
        await foreach (var batch in compiled.ExecuteColumnarAsync(
            [], new ExecutionStats(), arena, TestContext.Current.CancellationToken))
        {
            retained = batch.Column(0);
        }

        Assert.Null(retained.Owner);
    }

    // ---- §2: selection vectors --------------------------------------------------------------

    /// <summary>
    /// Every threshold produces the same rows: the selection is an internal representation, not a
    /// semantic. Zero always forwards a selection, one always compacts, and 0.5 is the default.
    /// </summary>
    [Theory]
    [InlineData(1, 0.0)]
    [InlineData(1, 0.5)]
    [InlineData(1, 1.0)]
    [InlineData(7, 0.0)]
    [InlineData(7, 0.5)]
    [InlineData(7, 1.0)]
    [InlineData(4096, 0.0)]
    [InlineData(4096, 0.5)]
    [InlineData(4096, 1.0)]
    public async Task A_filter_answers_the_same_rows_at_every_threshold(int batchSize, double threshold)
    {
        var table = TestData.Series(37);
        var row = table.RowType();
        var plan = IrBuilder.Plan(IrBuilder.Filter(
            IrBuilder.Read(table.Name, row),
            IrBuilder.Call(
                FunctionId.Gt, IrBuilder.Bool(), IrBuilder.Ref(row, 0), IrBuilder.Lit(20L))));

        var rows = await Rows(plan, TestData.Source(table), batchSize, threshold);
        Assert.Equal(16, rows.Count);
        Assert.All(rows, r => Assert.True((long)r[0]! > 20));
    }

    /// <summary>
    /// A selection survives a project and stops at a blocking operator, which copies the selected
    /// rows into its store (§2). The aggregate below sees exactly the filtered rows.
    /// </summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    public async Task A_selection_reaches_an_aggregate_through_a_project(double threshold)
    {
        var table = TestData.Series(37);
        var row = table.RowType();
        var filter = IrBuilder.Filter(
            IrBuilder.Read(table.Name, row),
            IrBuilder.Call(
                FunctionId.Gt, IrBuilder.Bool(), IrBuilder.Ref(row, 0), IrBuilder.Lit(20L)));
        var project = IrBuilder.Project(filter, [("n", IrBuilder.Ref(row, 0))]);
        var projected = project.RowType;
        var plan = IrBuilder.Plan(IrBuilder.Aggregate(
            project,
            [],
            [("n", IrBuilder.Agg(AggregateFunctionId.Count, IrBuilder.I64(), IrBuilder.Ref(projected, 0)))]));

        var rows = await Rows(plan, TestData.Source(table), batchSize: 7, threshold);
        Assert.Equal(16L, Assert.Single(rows)[0]);
    }

    /// <summary>
    /// A sort is a blocking operator too, and the rows it holds are the selected ones in the input's
    /// order — the selection never leaks into the answer.
    /// </summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    public async Task A_sort_over_a_selected_batch_orders_only_the_selected_rows(double threshold)
    {
        var table = TestData.Series(37);
        var row = table.RowType();
        var filter = IrBuilder.Filter(
            IrBuilder.Read(table.Name, row),
            IrBuilder.Call(
                FunctionId.Gt, IrBuilder.Bool(), IrBuilder.Ref(row, 0), IrBuilder.Lit(20L)));
        var plan = IrBuilder.Plan(IrBuilder.Sort(filter, IrBuilder.Desc(0, row.Fields[0].Type)));

        var rows = await Rows(plan, TestData.Source(table), batchSize: 7, threshold);
        Assert.Equal(16, rows.Count);
        Assert.Equal(36L, rows[0][0]);
        Assert.Equal(21L, rows[^1][0]);
    }

    /// <summary>
    /// The hazard a forwarded selection creates, and the guard for it (§2). Kernels run every lane,
    /// so a lane the filter removed still reaches the division below — and that lane is exactly the
    /// one the query wrote <c>WHERE b &lt;&gt; 0</c> to exclude. An unselected lane is treated as
    /// invalid, and every guarded kernel already skips invalid lanes. Both thresholds are exercised:
    /// at 0 the filter always forwards a selection, at 1 it always compacts.
    /// </summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    public async Task A_forwarded_selection_does_not_expose_a_guarded_kernel_to_the_rows_it_removed(
        double threshold)
    {
        // Every third row has a zero divisor, so between a half and two thirds of the rows survive
        // — on both sides of the default threshold.
        var table = new TestTable
        {
            Name = "divisors",
            Columns = [("a", ChalkType.Int64()), ("b", ChalkType.Int64())],
            Rows = [.. Enumerable.Range(0, 30).Select(i => new object?[] { (long)(i + 1), (long)(i % 3) })],
        };

        var row = table.RowType();
        var filter = IrBuilder.Filter(
            IrBuilder.Read(table.Name, row),
            IrBuilder.Call(
                FunctionId.Ne, IrBuilder.Bool(), IrBuilder.Ref(row, 1), IrBuilder.Lit(0L)));
        var plan = IrBuilder.Plan(IrBuilder.Project(
            filter,
            [("q", IrBuilder.Call(
                FunctionId.Divide, IrBuilder.I64(), IrBuilder.Ref(row, 0), IrBuilder.Ref(row, 1)))]));

        var rows = await Rows(plan, TestData.Source(table), batchSize: 16, threshold);

        Assert.Equal(20, rows.Count);
        Assert.All(rows, r => Assert.NotNull(r[0]));
    }

    /// <summary>
    /// The same for a checked multiply, whose garbage lane would overflow rather than divide by
    /// zero: the filter keeps the rows that cannot overflow and the kernel must not see the others.
    /// </summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    public async Task A_forwarded_selection_protects_checked_arithmetic_too(double threshold)
    {
        var table = new TestTable
        {
            Name = "big",
            Columns = [("a", ChalkType.Int64())],
            Rows = [.. Enumerable.Range(0, 20).Select(
                i => new object?[] { i % 4 == 0 ? long.MaxValue : (long)i })],
        };

        var row = table.RowType();
        var filter = IrBuilder.Filter(
            IrBuilder.Read(table.Name, row),
            IrBuilder.Call(
                FunctionId.Lt, IrBuilder.Bool(), IrBuilder.Ref(row, 0), IrBuilder.Lit(1_000L)));
        var plan = IrBuilder.Plan(IrBuilder.Project(
            filter,
            [("doubled", IrBuilder.Call(
                FunctionId.Multiply, IrBuilder.I64(), IrBuilder.Ref(row, 0), IrBuilder.Lit(2L)))]));

        var rows = await Rows(plan, TestData.Source(table), batchSize: 8, threshold);

        Assert.Equal(15, rows.Count);
    }

    // ---- §3: the synchronous pull path -------------------------------------------------------

    /// <summary>
    /// The claim of §3 (D63): a POCO-only pipeline allocates <em>nothing</em> between its first and
    /// its last batch. Single-threaded, so <see cref="GC.GetAllocatedBytesForCurrentThread"/> is
    /// exact rather than indicative — for a drain nothing interrupted, which is what
    /// <see cref="AllocationProbe"/> is here to find.
    /// </summary>
    [Fact]
    public async Task A_poco_pipeline_allocates_nothing_between_its_first_and_last_batch()
    {
        const int rows = 20_000;
        var bars = new Bar[rows];
        for (var i = 0; i < rows; i++)
        {
            bars[i] = new Bar(i % 3 == 0 ? "AAA" : "BBB", i, i * 1.5d);
        }

        var source = new PocoSourceBuilder("mem").AddTable("bars", bars).Build();
        var schema = source.DescribeSchema();
        var compiled = PlanCompiler.Compile(
            Pipeline(schema.Tables[0]),
            new CatalogContext { ContextId = "test", Epoch = 1, Schemas = [schema] },
            new Dictionary<string, ISourceRuntime> { ["mem"] = source },
            new ExecutionSettings { BatchSize = 512 });

        using var arena = new ExecutionArena();

        // One run to warm the arena's pools and the tree, then the measured one.
        await Drain(compiled, arena);

        // The drain is repeated and the least-allocating run is the one that counts. This gate's
        // budget is zero, and a disturbance charges the measuring thread for up to 8 KB nobody
        // allocated, which would read here as the pipeline allocating; since a disturbance can only
        // ever add, the smallest of several drains is the pipeline on its own (AllocationProbe).
        var (batches, between) = await AllocationProbe.LeastAllocatingAsync(
            async () =>
            {
                var batches = 0;
                var between = 0L;
                var mark = 0L;
                await foreach (var batch in compiled.ExecuteColumnarAsync(
                    [], new ExecutionStats(), arena, TestContext.Current.CancellationToken))
                {
                    if (batches++ > 0)
                    {
                        between += GC.GetAllocatedBytesForCurrentThread() - mark;
                    }

                    Assert.True(batch.Count > 0);
                    mark = GC.GetAllocatedBytesForCurrentThread();
                }

                return (Batches: batches, Between: between);
            },
            static drain => drain.Between);

        Assert.True(batches > 10, $"the pipeline has to produce several batches; it produced {batches}.");
        Assert.Equal(0, between);
        Assert.Equal(0, arena.OutstandingBytes);
    }

    private static async Task<List<object?[]>> Rows(
        Plan plan, TestSource source, int batchSize, double threshold)
    {
        var batches = await Runner.RunAsync(
            plan, source, batchSize, compactionThreshold: threshold);
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
        }
    }

    private static async Task Drain(CompiledPlan compiled, ExecutionArena arena)
    {
        await foreach (var batch in compiled.ExecuteColumnarAsync(
            [], new ExecutionStats(), arena, TestContext.Current.CancellationToken))
        {
            _ = batch.Count;
        }
    }

    /// <summary>Read → Filter → Project, the three operators §5's gate names.</summary>
    private static Plan Pipeline(TableDescriptor table)
    {
        var row = new RowType();
        foreach (var column in table.Columns)
        {
            row.Fields.Add(new Field { Name = column.Name, Type = column.Type.ToProto() });
        }

        var read = IrBuilder.Read("bars", row);
        var filter = IrBuilder.Filter(
            read,
            IrBuilder.Call(
                FunctionId.Ne, IrBuilder.Bool(), IrBuilder.Ref(row, 0), IrBuilder.Lit("AAA")));
        var project = IrBuilder.Project(
            filter,
            [
                ("symbol", IrBuilder.Ref(row, 0)),
                ("volume", IrBuilder.Ref(row, 1)),
                ("scaled", IrBuilder.Call(
                    FunctionId.Multiply, IrBuilder.Fp64(), IrBuilder.Ref(row, 2), IrBuilder.Lit(2d))),
            ]);
        return IrBuilder.Plan(project);
    }
}

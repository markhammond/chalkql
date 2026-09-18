using Chalk.Catalog;
using Chalk.Execution.Tests.Harness;
using Chalk.Ir;
using Chalk.Sources;
using Chalk.TestKit;
using Chalk.Tests;

namespace Chalk.Execution.Tests;

/// <summary>
/// The four set operators of D69 (<c>15-zero-allocation-execution.md</c> §6), at the three batch
/// sizes every operator test runs at: SQL's semantics, NULL keys, empty inputs on either side,
/// duplicate-heavy inputs, and the budget failure naming the operator.
/// </summary>
public sealed class SetOperatorTests
{
    /// <summary>Three symbols with deliberate multiplicities, and a NULL that has to compare equal.</summary>
    private static readonly TestTable Left = new()
    {
        Name = "left",
        Columns = [("k", ChalkType.String(nullable: true)), ("n", ChalkType.Int64())],
        Rows =
        [
            ["a", 1L],
            ["a", 1L],
            ["a", 1L],
            ["b", 2L],
            ["b", 2L],
            [null, 9L],
            [null, 9L],
            ["c", 3L],
        ],
    };

    private static readonly TestTable Right = new()
    {
        Name = "right",
        Columns = [("k", ChalkType.String(nullable: true)), ("n", ChalkType.Int64())],
        Rows =
        [
            ["a", 1L],
            ["a", 1L],
            ["b", 2L],
            [null, 9L],
            ["d", 4L],
        ],
    };

    private static readonly TestTable Empty = new()
    {
        Name = "empty",
        Columns = [("k", ChalkType.String(nullable: true)), ("n", ChalkType.Int64())],
        Rows = [],
    };

    private static TestSource Source => TestData.Source(Left, Right, Empty);

    public static TheoryData<int> BatchSizes => TestData.BatchSizeData;

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Union_all_concatenates_in_input_order(int batchSize)
    {
        var rows = await Run(SetOpKind.UnionAll, batchSize, "left", "right");

        Assert.Equal(13, rows.Count);
        Assert.Equal(5, rows.Count(r => (string?)r[0] == "a"));
        Assert.Equal(3, rows.Count(r => r[0] is null));
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Union_distinct_keeps_one_of_each_row_including_the_null(int batchSize)
    {
        var rows = await Run(SetOpKind.UnionDistinct, batchSize, "left", "right");

        Assert.Equal(5, rows.Count);
        Assert.Equal("a|b|<null>|c|d", Keys(rows));
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Intersect_all_keeps_the_smaller_count(int batchSize)
    {
        var rows = await Run(SetOpKind.IntersectAll, batchSize, "left", "right");

        // a: min(3, 2) = 2; b: min(2, 1) = 1; NULL: min(2, 1) = 1; c and d: not in both.
        Assert.Equal(4, rows.Count);
        Assert.Equal(2, rows.Count(r => (string?)r[0] == "a"));
        Assert.Equal(1, rows.Count(r => (string?)r[0] == "b"));
        Assert.Equal(1, rows.Count(r => r[0] is null));
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Intersect_distinct_keeps_each_key_once(int batchSize)
    {
        var rows = await Run(SetOpKind.IntersectDistinct, batchSize, "left", "right");

        Assert.Equal("a|b|<null>", Keys(rows));
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Except_all_subtracts_counts(int batchSize)
    {
        var rows = await Run(SetOpKind.ExceptAll, batchSize, "left", "right");

        // a: 3 - 2 = 1; b: 2 - 1 = 1; NULL: 2 - 1 = 1; c: 1 - 0 = 1.
        Assert.Equal(4, rows.Count);
        Assert.Equal("a|b|<null>|c", Keys(rows));
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Except_distinct_removes_a_key_the_right_holds_at_all(int batchSize)
    {
        var rows = await Run(SetOpKind.ExceptDistinct, batchSize, "left", "right");

        Assert.Equal("c", Keys(rows));
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task An_empty_right_leaves_the_left_alone(int batchSize)
    {
        Assert.Empty(await Run(SetOpKind.IntersectAll, batchSize, "left", "empty"));
        Assert.Equal(8, (await Run(SetOpKind.ExceptAll, batchSize, "left", "empty")).Count);
        Assert.Equal(8, (await Run(SetOpKind.UnionAll, batchSize, "left", "empty")).Count);
        Assert.Equal(4, (await Run(SetOpKind.UnionDistinct, batchSize, "left", "empty")).Count);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task An_empty_left_produces_nothing_but_a_union(int batchSize)
    {
        Assert.Empty(await Run(SetOpKind.IntersectDistinct, batchSize, "empty", "right"));
        Assert.Empty(await Run(SetOpKind.ExceptDistinct, batchSize, "empty", "right"));
        Assert.Equal(5, (await Run(SetOpKind.UnionAll, batchSize, "empty", "right")).Count);
        Assert.Equal(4, (await Run(SetOpKind.UnionDistinct, batchSize, "empty", "right")).Count);
    }

    /// <summary>Three inputs: every one of them reduces, or in a union adds.</summary>
    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Three_inputs_fold_in_order(int batchSize)
    {
        var rows = await Run(SetOpKind.UnionDistinct, batchSize, "left", "right", "empty");
        Assert.Equal(5, rows.Count);

        var intersected = await Run(SetOpKind.IntersectDistinct, batchSize, "left", "right", "left");
        Assert.Equal("a|b|<null>", Keys(intersected));
    }

    /// <summary>
    /// Every kind agrees with the reference executor, row for row and count for count, which is what
    /// the corpus asserts end to end (I4).
    /// </summary>
    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Both_engines_agree_on_every_kind(int batchSize)
    {
        foreach (var kind in new[]
        {
            SetOpKind.UnionAll, SetOpKind.UnionDistinct, SetOpKind.IntersectAll,
            SetOpKind.IntersectDistinct, SetOpKind.ExceptAll, SetOpKind.ExceptDistinct,
        })
        {
            var vectorised = await Run(kind, batchSize, "left", "right");
            var reference = await Run(kind, batchSize, "left", "right", reference: true);
            Assert.Equal(
                Sorted(reference),
                Sorted(vectorised));
        }
    }

    /// <summary>ADR 0012 §2: a budget failure names the operator and strands nothing.</summary>
    [Fact]
    public async Task A_set_operation_that_breaks_MaxBytes_fails_naming_the_operator()
    {
        var wide = TestData.Series(5_000);
        var source = TestData.Source(wide);
        var row = wide.RowType();
        var plan = IrBuilder.Plan(IrBuilder.SetOp(
            SetOpKind.IntersectDistinct,
            IrBuilder.Read(wide.Name, row),
            IrBuilder.Read(wide.Name, row)));

        using var arena = new ExecutionArena(new ArenaOptions { MaxBytes = 100_000 });
        var compiled = Runner.Compile(plan, source, batchSize: 512);

        var failure = await Assert.ThrowsAsync<ExecutionException>(async () =>
        {
            await foreach (var batch in compiled.ExecuteAsync(
                [], new ExecutionStats(), arena, TestContext.Current.CancellationToken))
            {
                batch.Dispose();
            }
        });

        Assert.Contains("SetOp", failure.OperatorPath, StringComparison.Ordinal);
        Assert.IsType<ArenaBudgetExceededException>(failure.InnerException);
        Assert.Equal(0, arena.OutstandingBytes);
    }

    /// <summary>
    /// §6's own gate case: <c>UNION ALL</c> forwards its inputs' batches unchanged, so a POCO-only
    /// pipeline under one allocates nothing per batch. The one transition that does allocate is the
    /// switch from the first input to the second, which starts a scan — a per-input cost, not a
    /// per-batch one, and the benchmark's slope measurement (§5) sees through it for the same reason.
    /// </summary>
    [Fact]
    public async Task Union_all_allocates_nothing_between_its_batches()
    {
        const int rows = 8_000;
        var bars = new Bar[rows];
        for (var i = 0; i < rows; i++)
        {
            bars[i] = new Bar(i % 3 == 0 ? "AAA" : "BBB", i);
        }

        var source = new Chalk.Sources.Poco.PocoSourceBuilder("mem").AddTable("bars", bars).Build();
        var schema = source.DescribeSchema();
        var row = new RowType();
        foreach (var column in schema.Tables[0].Columns)
        {
            row.Fields.Add(new Field { Name = column.Name, Type = column.Type.ToProto() });
        }

        var plan = IrBuilder.Plan(IrBuilder.SetOp(
            SetOpKind.UnionAll,
            IrBuilder.Read("bars", row),
            IrBuilder.Read("bars", row)));
        var compiled = PlanCompiler.Compile(
            plan,
            new Chalk.Catalog.CatalogContext { ContextId = "test", Epoch = 1, Schemas = [schema] },
            new Dictionary<string, ISourceRuntime> { ["mem"] = source },
            new ExecutionSettings { BatchSize = 256 });

        using var arena = new ExecutionArena();
        await Drain(compiled, arena);

        // The drain is repeated and the least-allocating run is the one that counts: a disturbance
        // charges the measuring thread for up to 8 KB nobody allocated, which would read here as a
        // second allocating boundary. It can only ever add, so the smallest of several drains is the
        // one that saw the input switch and nothing else (AllocationProbe).
        var (batches, allocating, between) = await AllocationProbe.LeastAllocatingAsync(
            async () =>
            {
                var batches = 0;
                var allocating = 0;
                var between = 0L;
                var mark = 0L;
                await foreach (var batch in compiled.ExecuteColumnarAsync(
                    [], new ExecutionStats(), arena, TestContext.Current.CancellationToken))
                {
                    if (batches++ > 0)
                    {
                        var delta = GC.GetAllocatedBytesForCurrentThread() - mark;
                        between += delta;
                        allocating += delta > 0 ? 1 : 0;
                    }

                    Assert.True(batch.Count > 0);
                    mark = GC.GetAllocatedBytesForCurrentThread();
                }

                return (Batches: batches, Allocating: allocating, Between: between);
            },
            static drain => drain.Between);

        Assert.Equal(2 * ((rows + 255) / 256), batches);
        Assert.Equal(1, allocating);
        Assert.True(between < 4096, $"the input switch allocated {between} bytes.");
        Assert.Equal(0, arena.OutstandingBytes);
    }

    private sealed record Bar(string Symbol, long Volume);

    private static async Task Drain(CompiledPlan compiled, ExecutionArena arena)
    {
        await foreach (var batch in compiled.ExecuteColumnarAsync(
            [], new ExecutionStats(), arena, TestContext.Current.CancellationToken))
        {
            _ = batch.Count;
        }
    }

    /// <summary>The key column of every row, in order, with a NULL spelled out.</summary>
    private static string Keys(List<object?[]> rows) =>
        string.Join('|', rows.Select(r => (string?)r[0] ?? "<null>"));

    private static IEnumerable<string> Sorted(List<object?[]> rows) =>
        rows.Select(r => $"{r[0] ?? "<null>"}|{r[1]}").Order(StringComparer.Ordinal);

    private static async Task<List<object?[]>> Run(
        SetOpKind kind, int batchSize, params string[] tables) =>
        await Run(kind, batchSize, tables, reference: false);

    private static Task<List<object?[]>> Run(
        SetOpKind kind, int batchSize, string first, string second, bool reference) =>
        Run(kind, batchSize, [first, second], reference);

    private static async Task<List<object?[]>> Run(
        SetOpKind kind, int batchSize, string[] tables, bool reference)
    {
        var source = Source;
        var inputs = tables
            .Select(name => IrBuilder.Read(name, Table(name).RowType()))
            .ToArray();
        var plan = IrBuilder.Plan(IrBuilder.SetOp(kind, inputs));
        return await Runner.RowsAsync(plan, source, batchSize, reference);
    }

    private static TestTable Table(string name) => name switch
    {
        "left" => Left,
        "right" => Right,
        _ => Empty,
    };
}

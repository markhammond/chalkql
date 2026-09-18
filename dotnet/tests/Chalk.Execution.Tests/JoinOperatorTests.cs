using Chalk.Catalog;
using Chalk.Execution.Tests.Harness;
using Chalk.Ir;
using Chalk.Sources;
using Chalk.TestKit;

namespace Chalk.Execution.Tests;

/// <summary>
/// The four join operators of <c>12-joins.md</c> §4, each at <c>BatchSize</c> 1, 7 and 4096 so a
/// batch-boundary bug has nowhere to hide, and each compared with the reference executor — which
/// runs nested loops and a direct ASOF scan and shares no code with them (D45).
/// </summary>
public sealed class JoinOperatorTests
{
    /// <summary>Left rows: a key that matches twice, one that matches once, one NULL, one unmatched.</summary>
    private static readonly TestTable Left = new()
    {
        Name = "l",
        Columns = [("k", ChalkType.Int64(nullable: true)), ("lv", ChalkType.String(nullable: true))],
        Rows =
        [
            [1L, "a"],
            [2L, "b"],
            [null, "c"],
            [4L, "d"],
        ],
    };

    /// <summary>Right rows: a duplicate key, a NULL key, and a key nothing on the left has.</summary>
    private static readonly TestTable Right = new()
    {
        Name = "r",
        Columns = [("k", ChalkType.Int64(nullable: true)), ("rv", ChalkType.String(nullable: true))],
        Rows =
        [
            [1L, "x"],
            [1L, "y"],
            [2L, "z"],
            [null, "n"],
            [9L, "w"],
        ],
    };

    private static readonly TestTable Empty = new()
    {
        Name = "e",
        Columns = [("k", ChalkType.Int64(nullable: true)), ("ev", ChalkType.String(nullable: true))],
        Rows = [],
    };

    /// <summary>Left side of the ASOF tests: two symbols and a NULL time.</summary>
    private static readonly TestTable Quotes = new()
    {
        Name = "q",
        Columns =
        [
            ("sym", ChalkType.String(nullable: true)),
            ("ts", ChalkType.Int64(nullable: true)),
            ("px", ChalkType.Int64(nullable: true)),
        ],
        Rows =
        [
            ["A", 10L, 100L],
            ["A", 25L, 101L],
            ["A", 5L, 102L],
            ["B", 20L, 200L],
            ["A", null, 103L],
            ["C", 10L, 300L],
        ],
    };

    /// <summary>Right side: two rows tie at time 20 for symbol A, and one has a NULL time.</summary>
    private static readonly TestTable Rates = new()
    {
        Name = "f",
        Columns =
        [
            ("sym", ChalkType.String(nullable: true)),
            ("ts", ChalkType.Int64(nullable: true)),
            ("rate", ChalkType.Int64(nullable: true)),
        ],
        Rows =
        [
            ["A", 20L, 1L],
            ["A", 20L, 2L],
            ["A", 10L, 3L],
            ["A", null, 4L],
            ["B", 30L, 5L],
        ],
    };

    private static readonly TestSource Source = new("mem", "main", Left, Right, Empty, Quotes, Rates);

    public static TheoryData<int> BatchSizes() => TestData.BatchSizeData;

    public static TheoryData<int, JoinType> JoinTypes()
    {
        var data = new TheoryData<int, JoinType>();
        foreach (var size in Runner.BatchSizes)
        {
            foreach (var type in new[]
                     {
                         JoinType.Inner, JoinType.Left, JoinType.Right, JoinType.Full,
                         JoinType.Semi, JoinType.Anti,
                     })
            {
                data.Add(size, type);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(JoinTypes))]
    public async Task Hash_join_agrees_with_the_reference_executor(int batchSize, JoinType type)
    {
        var plan = IrBuilder.Plan(IrBuilder.HashJoin(Scan(Left), Scan(Right), [0], [0], type));

        await AssertAgreesAsync(plan, batchSize);
    }

    [Theory]
    [MemberData(nameof(JoinTypes))]
    public async Task Nested_loop_join_agrees_with_the_reference_executor(int batchSize, JoinType type)
    {
        var left = Scan(Left);
        var right = Scan(Right);
        var condition = IrBuilder.Call(
            FunctionId.Eq,
            IrBuilder.Bool(true),
            IrBuilder.Ref(0, IrBuilder.I64(true)),
            IrBuilder.Ref(2, IrBuilder.I64(true)));
        var plan = IrBuilder.Plan(IrBuilder.NestedLoopJoin(left, right, condition, type));

        await AssertAgreesAsync(plan, batchSize);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task A_cross_join_is_the_product_of_both_inputs(int batchSize)
    {
        var plan = IrBuilder.Plan(IrBuilder.NestedLoopJoin(Scan(Left), Scan(Right)));

        var rows = await Runner.RowsAsync(plan, Source, batchSize);

        Assert.Equal(Left.Rows.Count * Right.Rows.Count, rows.Count);
        await AssertAgreesAsync(plan, batchSize);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task A_null_key_never_matches(int batchSize)
    {
        var plan = IrBuilder.Plan(
            IrBuilder.HashJoin(Scan(Left), Scan(Right), [0], [0], JoinType.Left));

        var rows = await Runner.RowsAsync(plan, Source, batchSize);

        // The NULL-keyed left row is padded, not matched with the NULL-keyed right row.
        var nullKeyed = rows.Where(r => r[1] is "c").ToList();
        Assert.Single(nullKeyed);
        Assert.Null(nullKeyed[0][2]);
        Assert.Null(nullKeyed[0][3]);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Duplicate_keys_on_both_sides_produce_the_product_within_the_group(int batchSize)
    {
        var plan = IrBuilder.Plan(IrBuilder.HashJoin(Scan(Left), Scan(Left), [0], [0]));

        var rows = await Runner.RowsAsync(plan, Source, batchSize);

        // Three non-NULL keys, each matching itself once.
        Assert.Equal(3, rows.Count);
        await AssertAgreesAsync(plan, batchSize);
    }

    [Theory]
    [MemberData(nameof(JoinTypes))]
    public async Task An_empty_build_side_still_honours_the_join_type(int batchSize, JoinType type)
    {
        var plan = IrBuilder.Plan(IrBuilder.HashJoin(Scan(Left), Scan(Empty), [0], [0], type));

        await AssertAgreesAsync(plan, batchSize);
    }

    [Theory]
    [MemberData(nameof(JoinTypes))]
    public async Task An_empty_probe_side_still_honours_the_join_type(int batchSize, JoinType type)
    {
        var plan = IrBuilder.Plan(IrBuilder.HashJoin(Scan(Empty), Scan(Right), [0], [0], type));

        await AssertAgreesAsync(plan, batchSize);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task A_post_join_filter_is_part_of_the_condition_not_a_filter_above_it(int batchSize)
    {
        // ON l.k = r.k AND r.rv = 'y': the left row keyed 1 matches only one of its two candidates,
        // and the left row keyed 2 matches neither — so a LEFT join pads it rather than dropping it.
        var residual = IrBuilder.Call(
            FunctionId.Eq,
            IrBuilder.Bool(true),
            IrBuilder.Ref(3, IrBuilder.Str(true)),
            IrBuilder.Lit("y"));
        var plan = IrBuilder.Plan(
            IrBuilder.HashJoin(Scan(Left), Scan(Right), [0], [0], JoinType.Left, residual));

        var rows = await Runner.RowsAsync(plan, Source, batchSize);

        Assert.Equal(4, rows.Count);
        Assert.Equal("y", rows.Single(r => r[1] is "a")[3]);
        Assert.Null(rows.Single(r => r[1] is "b")[3]);
        await AssertAgreesAsync(plan, batchSize);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Merge_join_agrees_with_the_reference_executor(int batchSize)
    {
        var plan = IrBuilder.Plan(MergeJoinPlan(JoinType.Inner));

        await AssertAgreesAsync(plan, batchSize);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Merge_join_pads_an_unmatched_left_row(int batchSize)
    {
        var plan = IrBuilder.Plan(MergeJoinPlan(JoinType.Left));

        var rows = await Runner.RowsAsync(plan, Source, batchSize);

        Assert.Null(rows.Single(r => r[1] is "d")[3]);
        await AssertAgreesAsync(plan, batchSize);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Merge_join_emits_the_unmatched_right_rows_of_a_full_join(int batchSize)
    {
        var plan = IrBuilder.Plan(MergeJoinPlan(JoinType.Full));

        var rows = await Runner.RowsAsync(plan, Source, batchSize);

        Assert.Contains(rows, r => r[0] is null && r[3] is "w");
        await AssertAgreesAsync(plan, batchSize);
    }

    [Fact]
    public void Merge_join_needs_its_inputs_to_claim_the_key_order()
    {
        // The same join without the collation claims: the validator refuses the plan, because a
        // merge join whose inputs are not sorted is silently wrong rather than slow.
        var plan = IrBuilder.Plan(
            IrBuilder.MergeJoin(Scan(Left), Scan(Right), [0], [0]));

        var failure = Assert.Throws<InvalidPlanException>(() => Runner.Compile(plan, Source));
        Assert.Contains("MergeJoin", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task AsOf_join_takes_the_closest_earlier_row(int batchSize)
    {
        var plan = IrBuilder.Plan(AsOfPlan(AsOfMatch.Ge, JoinType.Left));

        var rows = await Runner.RowsAsync(plan, Source, batchSize);

        Assert.Equal(Quotes.Rows.Count, rows.Count);

        // A at 25 sees the tie at 20 and takes the first of it, rate 1 (D41).
        Assert.Equal(1L, rows.Single(r => r[2] is 101L)[5]);

        // A at 10 sees only the row at 10 itself.
        Assert.Equal(3L, rows.Single(r => r[2] is 100L)[5]);

        // A at 5 has nothing at or before it, and a LEFT ASOF pads.
        Assert.Null(rows.Single(r => r[2] is 102L)[5]);

        // A NULL left time matches nothing, whatever the right side holds.
        Assert.Null(rows.Single(r => r[2] is 103L)[5]);

        // A symbol with no partition at all.
        Assert.Null(rows.Single(r => r[2] is 300L)[5]);

        await AssertAgreesAsync(plan, batchSize);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task AsOf_join_takes_the_closest_later_row(int batchSize)
    {
        var plan = IrBuilder.Plan(AsOfPlan(AsOfMatch.Le, JoinType.Inner));

        var rows = await Runner.RowsAsync(plan, Source, batchSize);

        // A at 5 and A at 10 both reach the row at 10; A at 25 has nothing at or after it and an
        // INNER ASOF drops it.
        Assert.Equal(3L, rows.Single(r => r[2] is 102L)[5]);
        Assert.Equal(3L, rows.Single(r => r[2] is 100L)[5]);
        Assert.DoesNotContain(rows, r => r[2] is 101L);
        Assert.Equal(5L, rows.Single(r => r[2] is 200L)[5]);

        await AssertAgreesAsync(plan, batchSize);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task AsOf_join_with_a_strict_comparison_excludes_the_equal_time(int batchSize)
    {
        var plan = IrBuilder.Plan(AsOfPlan(AsOfMatch.Gt, JoinType.Left));

        var rows = await Runner.RowsAsync(plan, Source, batchSize);

        // A at 10 no longer matches the right row at 10.
        Assert.Null(rows.Single(r => r[2] is 100L)[5]);
        await AssertAgreesAsync(plan, batchSize);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task AsOf_join_without_keys_is_one_partition(int batchSize)
    {
        var plan = IrBuilder.Plan(
            IrBuilder.AsOfJoin(Scan(Quotes), Scan(Rates), [], [], 1, 1, AsOfMatch.Ge, JoinType.Left));

        await AssertAgreesAsync(plan, batchSize);
    }

    [Fact]
    public async Task A_build_side_over_the_arena_budget_names_the_operator()
    {
        var plan = IrBuilder.Plan(IrBuilder.HashJoin(Scan(Left), Scan(Right), [0], [0]));
        var compiled = Runner.Compile(plan, Source);
        using var arena = new ExecutionArena(new ArenaOptions { MaxBytes = 512 });

        var failure = await Assert.ThrowsAsync<ExecutionException>(async () =>
        {
            await foreach (var batch in compiled.ExecuteAsync(
                [], new ExecutionStats(), arena, CancellationToken.None))
            {
                batch.Dispose();
            }
        });

        Assert.Contains("HashJoin", failure.OperatorPath, StringComparison.Ordinal);
        Assert.IsType<ArenaBudgetExceededException>(failure.InnerException);

        // Not asserting OutstandingBytes here: an execution that is torn down mid-allocation leaves
        // the partially built column behind, which is true of every blocking operator since M1 and is
        // reclaimed by disposing the arena. The empty-after-success claim is the test below.
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task A_logical_join_runs_as_a_hash_join_when_it_has_an_equality(int batchSize)
    {
        var condition = IrBuilder.Call(
            FunctionId.Eq,
            IrBuilder.Bool(true),
            IrBuilder.Ref(0, IrBuilder.I64(true)),
            IrBuilder.Ref(2, IrBuilder.I64(true)));
        var plan = IrBuilder.Plan(IrBuilder.Join(Scan(Left), Scan(Right), condition, JoinType.Left));

        await AssertAgreesAsync(plan, batchSize);
    }

    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Every_join_leaves_the_arena_empty(int batchSize)
    {
        using var arena = new ExecutionArena();
        foreach (var plan in new[]
                 {
                     IrBuilder.Plan(IrBuilder.HashJoin(Scan(Left), Scan(Right), [0], [0], JoinType.Full)),
                     IrBuilder.Plan(MergeJoinPlan(JoinType.Full)),
                     IrBuilder.Plan(IrBuilder.NestedLoopJoin(Scan(Left), Scan(Right))),
                     IrBuilder.Plan(AsOfPlan(AsOfMatch.Ge, JoinType.Left)),
                 })
        {
            var compiled = Runner.Compile(plan, Source, batchSize);
            await foreach (var batch in compiled.ExecuteAsync(
                [], new ExecutionStats(), arena, CancellationToken.None))
            {
                batch.Dispose();
            }

            Assert.Equal(0, arena.OutstandingBytes);
        }
    }

    // ------------------------------------------------------------------ the coverage graft (D165)
    //
    // Query shapes grafted from ikvmnet/calcite-dotnet (Apache-2.0),
    // src/Apache.Calcite.Tests/ClrEnumerableDifferentialTests.cs; see the repository's NOTICE. No
    // expected value is taken (D163) -- every one of these is compared with the reference executor,
    // which runs nested loops and shares no code with the operators under test.
    //
    // What the corpus family cannot reach from SQL: the planner chooses which physical join runs, so
    // a corpus query cannot insist on a hash join over a composite key with one nullable member, or
    // on a merge join over a key that repeats. Hand-built IR can.

    /// <summary>
    /// `sales`, as two aliases of itself (D164): six rows, a duplicate amount, one NULL amount, two
    /// regions. Built here rather than taken from <c>Chalk.TestKit.Fixtures</c> because these tests
    /// want a <see cref="TestTable"/> and the fixture is a POCO source.
    /// </summary>
    private static readonly TestTable Sales = new()
    {
        Name = "sales",
        // Every column nullable, as the two tables above are: an outer join makes one side's
        // columns nullable in its output, and the IR's collation and condition claims are checked
        // against that. What matters here is that `amount` *holds* a NULL, not that its neighbours
        // could not.
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

    /// <summary>`sorted` (D164): declared ordered by k, which repeats at 2 and skips 3.</summary>
    private static readonly TestTable SortedRows = new()
    {
        Name = "sorted",
        Columns = [("k", ChalkType.Int64(nullable: true)), ("v", ChalkType.String(nullable: true))],
        Rows = [[1L, "A"], [2L, "B"], [2L, "C"], [4L, "D"]],
    };

    private static readonly TestSource GraftSource = new("mem", "main", Sales, SortedRows);

    public static TheoryData<int, JoinType> GraftJoinTypes()
    {
        var data = new TheoryData<int, JoinType>();
        foreach (var size in Runner.BatchSizes)
        {
            foreach (var type in new[]
                     {
                         JoinType.Inner, JoinType.Left, JoinType.Right, JoinType.Full,
                         JoinType.Semi, JoinType.Anti,
                     })
            {
                data.Add(size, type);
            }
        }

        return data;
    }

    /// <summary>
    /// A hash join on two key fields, one of them nullable — the case a null-aware key accessor
    /// nulls whole and a plain one leaves as a tuple holding a null that matches another one.
    /// </summary>
    [Theory]
    [MemberData(nameof(GraftJoinTypes))]
    public async Task Hash_join_on_a_composite_key_with_one_nullable_member(
        int batchSize, JoinType type)
    {
        var plan = IrBuilder.Plan(
            IrBuilder.HashJoin(Scan(Sales), Scan(Sales), [1, 2], [1, 2], type));
        await AssertGraftAgreesAsync(plan, batchSize);
    }

    /// <summary>
    /// The same, carrying an extra non-equi predicate the join tests on the pair it has already
    /// matched — which for an outer join is part of the <em>condition</em> and not a filter above
    /// it, so a row that fails it is still null-padded (ADR 0016).
    /// </summary>
    [Theory]
    [MemberData(nameof(GraftJoinTypes))]
    public async Task Hash_join_with_an_extra_predicate(int batchSize, JoinType type)
    {
        var plan = IrBuilder.Plan(IrBuilder.HashJoin(
            Scan(Sales),
            Scan(Sales),
            [1],
            [1],
            type,
            // Over left ++ right, which is what a post-join filter sees whatever the output row
            // type is: a semi join's output is the left row alone, and its condition is not.
            residual: IrBuilder.Call(
                FunctionId.Lt,
                IrBuilder.Bool(true),
                IrBuilder.Ref(0, IrBuilder.I64(nullable: true)),
                IrBuilder.Ref(3, IrBuilder.I64(nullable: true)))));
        await AssertGraftAgreesAsync(plan, batchSize);
    }

    /// <summary>A hash join whose only key is the nullable one: a NULL never matches a NULL.</summary>
    [Theory]
    [MemberData(nameof(GraftJoinTypes))]
    public async Task Hash_join_on_a_nullable_key(int batchSize, JoinType type)
    {
        var plan = IrBuilder.Plan(IrBuilder.HashJoin(Scan(Sales), Scan(Sales), [2], [2], type));
        await AssertGraftAgreesAsync(plan, batchSize);
    }

    /// <summary>
    /// A merge join over a key that repeats on both sides — two rows at 2 against two rows at 2 is
    /// four output rows, and a merge join that advanced one side too eagerly would find two.
    /// </summary>
    [Theory]
    [MemberData(nameof(GraftJoinTypes))]
    public async Task Merge_join_over_a_duplicate_key(int batchSize, JoinType type)
    {
        var plan = IrBuilder.Plan(
            IrBuilder.MergeJoin(Sorted(SortedRows), Sorted(SortedRows), [0], [0], type));
        await AssertGraftAgreesAsync(plan, batchSize);
    }

    /// <summary>
    /// The same with a predicate that only one of the tied pairs satisfies, so the duplicate group
    /// is entered and then partly rejected.
    /// </summary>
    [Theory]
    [MemberData(nameof(GraftJoinTypes))]
    public async Task Merge_join_over_a_duplicate_key_with_an_extra_predicate(
        int batchSize, JoinType type)
    {
        var plan = IrBuilder.Plan(IrBuilder.MergeJoin(
            Sorted(SortedRows),
            Sorted(SortedRows),
            [0],
            [0],
            type,
            residual: IrBuilder.Call(
                FunctionId.Lt,
                IrBuilder.Bool(true),
                IrBuilder.Ref(1, IrBuilder.Str(nullable: true)),
                IrBuilder.Ref(3, IrBuilder.Str(nullable: true)))));
        await AssertGraftAgreesAsync(plan, batchSize);
    }

    /// <summary>
    /// A null-safe key, as the nested-loop join carries it: two NULLs match, which no equi-join key
    /// in the IR can express (V50, ADR 0024).
    /// </summary>
    [Theory]
    [MemberData(nameof(GraftJoinTypes))]
    public async Task Nested_loop_join_on_a_null_safe_key(int batchSize, JoinType type)
    {
        var plan = IrBuilder.Plan(IrBuilder.NestedLoopJoin(
            Scan(Sales),
            Scan(Sales),
            IrBuilder.Call(
                FunctionId.IsNotDistinctFrom,
                IrBuilder.Bool(),
                IrBuilder.Ref(2, IrBuilder.I64(nullable: true)),
                IrBuilder.Ref(5, IrBuilder.I64(nullable: true))),
            type));
        await AssertGraftAgreesAsync(plan, batchSize);
    }

    private static async Task AssertGraftAgreesAsync(Plan plan, int batchSize)
    {
        var vectorised = await Runner.RowsAsync(plan, GraftSource, batchSize);
        var reference = await Runner.RowsAsync(plan, GraftSource, batchSize, reference: true);

        Assert.Equal(reference.Count, vectorised.Count);
        var expected = reference.Select(Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var actual = vectorised.Select(Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
        Assert.Equal(expected, actual);
    }

    /// <summary>A merge join over two inputs sorted on the key, with the claims the validator needs.</summary>
    private static Rel MergeJoinPlan(JoinType type) => IrBuilder.MergeJoin(
        Sorted(Left), Sorted(Right), [0], [0], type);

    private static Rel AsOfPlan(AsOfMatch match, JoinType type) =>
        IrBuilder.AsOfJoin(Scan(Quotes), Scan(Rates), [0], [0], 1, 1, match, type);

    private static Rel Scan(TestTable table) => IrBuilder.Read(table.Name, table.RowType());

    /// <summary>The table sorted on its key, which is what a merge join's input has to be.</summary>
    private static Rel Sorted(TestTable table) => IrBuilder.Sort(
        Scan(table),
        new SortField
        {
            Expr = IrBuilder.Ref(table.RowType(), 0),
            Direction = SortDirection.AscNullsLast,
        });

    /// <summary>Runs the plan on both engines and compares the rows in order.</summary>
    private static async Task AssertAgreesAsync(Plan plan, int batchSize)
    {
        var vectorised = await Runner.RowsAsync(plan, Source, batchSize);
        var reference = await Runner.RowsAsync(plan, Source, batchSize, reference: true);

        Assert.Equal(reference.Count, vectorised.Count);
        var expected = reference.Select(Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var actual = vectorised.Select(Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
        Assert.Equal(expected, actual);
    }

    private static string Key(object?[] row) =>
        string.Join('', row.Select(v => v?.ToString() ?? "<null>"));
}

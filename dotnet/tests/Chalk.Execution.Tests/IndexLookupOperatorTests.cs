using Chalk.Ir;
using Chalk.Sources;
using Chalk.Sources.Poco;
using Chalk.Execution.Operators;
using Chalk.TestKit;
using CatalogContext = Chalk.Catalog.CatalogContext;
using IrField = Chalk.Ir.Field;
using IrType = Chalk.Ir.Type;

namespace Chalk.Execution.Tests;

/// <summary>
/// The lookup operator on its own (§5, D37): what it asks the source for, and what it does with the
/// answer. The source here is the real POCO one, because the operator's job is the binding and the
/// batching and there is nothing to learn from a double that agrees with it by construction.
/// </summary>
public sealed class IndexLookupOperatorTests
{
    private sealed record Point(string Group, int Rank, double Value);

    private static readonly Point[] Points =
    [
        new("a", 1, 1.5),
        new("a", 3, 2.5),
        new("b", 2, 3.5),
        new("b", 5, 4.5),
        new("c", 4, 5.5),
        new("a", 2, 6.5),
        new("c", 6, 7.5),
    ];

    [Fact]
    public async Task An_equality_range_reads_only_the_rows_it_produces()
    {
        var plan = Plan([IrBuilder.Range([Text("a")], [Text("a")])]);
        var (rows, stats) = await RunAsync(plan);

        Assert.Equal(3, rows.Count);
        Assert.All(rows, row => Assert.Equal("a", row[0]));
        Assert.Equal(3, stats.RowsScanned);
        Assert.Equal(3, stats.RowsProduced);
    }

    [Fact]
    public async Task Two_ranges_are_a_union_with_no_row_counted_twice()
    {
        // The planner emits sorted, non-overlapping ranges; the executor de-duplicates anyway,
        // which is what this asks for by giving it the same range twice (§3).
        var plan = Plan(
        [
            IrBuilder.Range([Text("a")], [Text("a")]),
            IrBuilder.Range([Text("a")], [Text("a")]),
            IrBuilder.Range([Text("c")], [Text("c")]),
        ]);
        var (rows, stats) = await RunAsync(plan);

        Assert.Equal(5, rows.Count);
        Assert.Equal(["a", "a", "a", "c", "c"], rows.Select(r => (string)r[0]!));
        Assert.Equal(5, stats.RowsScanned);
    }

    [Fact]
    public async Task A_parameter_bound_to_null_empties_its_range()
    {
        // SQL: `x = NULL` matches nothing. The lookup does not even ask the source.
        var plan = Plan(
            [IrBuilder.Range([IrBuilder.Param(0, IrBuilder.Str(nullable: true))], [IrBuilder.Param(0, IrBuilder.Str(nullable: true))])],
            IrBuilder.Str(nullable: true));
        var (rows, stats) = await RunAsync(plan, [null]);

        Assert.Empty(rows);
        Assert.Equal(0, stats.RowsScanned);
    }

    [Fact]
    public async Task A_parameter_bound_to_a_value_finds_its_rows()
    {
        var plan = Plan(
            [IrBuilder.Range([IrBuilder.Param(0, IrBuilder.Str(nullable: true))], [IrBuilder.Param(0, IrBuilder.Str(nullable: true))])],
            IrBuilder.Str(nullable: true));
        var (rows, _) = await RunAsync(plan, ["b"]);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal("b", row[0]));
    }

    [Fact]
    public async Task A_lookup_with_no_ranges_produces_nothing()
    {
        var (rows, stats) = await RunAsync(Plan([]));

        Assert.Empty(rows);
        Assert.Equal(0, stats.RowsScanned);
    }

    [Fact]
    public async Task The_projection_is_the_row_the_lookup_emits()
    {
        // The planner widens the projection to cover everything the residual reads, so a lookup can
        // emit a column the query does not select; here that is `value`, at position 1 of the row.
        var source = Source();
        var row = Row(source, [0, 2]);
        var plan = IrBuilder.Plan(IrBuilder.IndexLookup(
            "points",
            "ix_points_Group",
            row,
            [IrBuilder.Range([Text("a")], [Text("a")])],
            projection: [0, 2]));

        var (rows, _) = await RunAsync(plan, source: source);

        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.Equal(2, r.Length));
        Assert.Equal([1.5, 2.5, 6.5], rows.Select(r => (double)r[1]!));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4096)]
    public async Task Batching_does_not_change_the_rows(int batchSize)
    {
        var plan = Plan([IrBuilder.Range([Text("a")], [Text("c")])]);
        var (rows, stats) = await RunAsync(plan, batchSize: batchSize);

        Assert.Equal(Points.Length, rows.Count);
        Assert.Equal(Points.Length, stats.RowsScanned);
    }

    [Fact]
    public async Task A_plan_naming_an_index_the_table_does_not_have_fails_at_compilation()
    {
        var source = Source();
        var plan = IrBuilder.Plan(IrBuilder.IndexLookup(
            "points", "ix_nope", Row(source, [0, 1, 2]), [IrBuilder.Range([Text("a")], [Text("a")])]));

        var error = await Assert.ThrowsAsync<InvalidPlanException>(() => RunAsync(plan, source: source));
        Assert.Contains("ix_nope", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_bound_that_is_not_a_constant_fails_at_compilation()
    {
        var source = Source();
        var row = Row(source, [0, 1, 2]);
        var bound = IrBuilder.Call(FunctionId.Upper, IrBuilder.Str(), Text("a"));
        var plan = IrBuilder.Plan(IrBuilder.IndexLookup(
            "points", "ix_points_Group", row, [IrBuilder.Range([bound], [bound])]));

        var error = await Assert.ThrowsAsync<InvalidPlanException>(() => RunAsync(plan, source: source));
        Assert.Contains("only a literal or a parameter", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// D282: an ordered index is sent the plain half-open range the prefix stands for, so the rows
    /// are exactly the ones a LIKE would keep and nothing is left to re-check.
    /// </summary>
    [Fact]
    public async Task A_prefix_range_on_an_ordered_index_becomes_the_range_it_stands_for()
    {
        var plan = Plan([IrBuilder.Range([Text("b%")], [], prefix: true)]);
        var (rows, stats) = await RunAsync(plan);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal("b", row[0]));
        Assert.Equal(2, stats.RowsScanned);
    }

    /// <summary>An empty prefix matches every text, and still excludes a NULL key.</summary>
    [Fact]
    public async Task An_empty_prefix_is_every_row()
    {
        var plan = Plan([IrBuilder.Range([Text("%")], [], prefix: true)]);
        var (rows, _) = await RunAsync(plan);

        Assert.Equal(Points.Length, rows.Count);
    }

    /// <summary>
    /// A parameter's text is the one thing about a prefix the plan could not know. The planner gives
    /// a parameter prefix a residual that decides the rest of the pattern (D314); a plan without one
    /// — which the planner never writes — refuses a value its ranges would over-answer, by name,
    /// rather than answering with the extra rows.
    /// </summary>
    [Fact]
    public async Task A_parameter_that_is_not_a_bare_prefix_needs_a_residual()
    {
        var plan = Plan(
            [IrBuilder.Range([IrBuilder.Param(0, IrBuilder.Str(nullable: true))], [], prefix: true)],
            IrBuilder.Str(nullable: true));

        var error = await Assert.ThrowsAsync<ExecutionException>(() => RunAsync(plan, ["a%b%"]));
        var refusal = Assert.IsType<UnsupportedFeatureException>(error.InnerException);

        Assert.Contains("ix_points_Group", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("ranges do not decide", refusal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("a%b%", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// D314: with its residual the same lookup answers any value — the range over the value's
    /// literal start, then the LIKE — and a bare prefix reads only the rows it produces, because the
    /// lookup switches the residual off when its ranges already decide.
    /// </summary>
    [Fact]
    public async Task A_parameter_prefix_with_its_residual_answers_any_value()
    {
        var plan = RechecksItsLike(escape: null);

        var (bare, bareStats) = await RunAsync(plan, ["b%"]);
        Assert.Equal(2, bare.Count);
        Assert.Equal(2, bareStats.RowsScanned);

        var (some, _) = await RunAsync(plan, ["%"]);
        Assert.Equal(Points.Length, some.Count);

        var (none, _) = await RunAsync(plan, ["_x%"]);
        Assert.Empty(none);

        var (nulls, nullStats) = await RunAsync(plan, [null]);
        Assert.Empty(nulls);
        Assert.Equal(0, nullStats.RowsScanned);
    }

    /// <summary>
    /// A prefix that ends at the largest code point there is has no text above it, so its range is
    /// open above and covers texts it does not match. With the residual that is answered; without
    /// one it is refused, as any range that does not decide is.
    /// </summary>
    [Fact]
    public async Task A_prefix_with_no_successor_is_answered_through_the_residual()
    {
        var bareRange = Plan(
            [IrBuilder.Range([IrBuilder.Param(0, IrBuilder.Str(nullable: true))], [], prefix: true)],
            IrBuilder.Str(nullable: true));
        var error = await Assert.ThrowsAsync<ExecutionException>(
            () => RunAsync(bareRange, ["\U0010FFFF%"]));
        Assert.IsType<UnsupportedFeatureException>(error.InnerException);

        var (rows, _) = await RunAsync(RechecksItsLike(escape: null), ["\U0010FFFF%"]);
        Assert.Empty(rows);
    }

    /// <summary>
    /// D313: a range carries its pattern's escape, and the client reads the pattern's literal start
    /// through it — here 'KB\_%' ESCAPE '\' is the prefix KB_, and only KB_1 starts with it.
    /// </summary>
    [Fact]
    public async Task A_prefix_range_reads_its_pattern_through_its_escape()
    {
        var range = IrBuilder.Range([Text("KB\\_%")], [], prefix: true);
        range.Escape = "\\";
        var (rows, stats) = await RunTagsAsync(TagsPlan([range]));

        Assert.Equal(["KB_1"], rows.Select(r => (string)r[0]!));
        Assert.Equal(1, stats.RowsScanned);
    }

    /// <summary>
    /// F153: an empty prefix after an equality prefix covers every key equal to it. Its upper side is
    /// the equality columns alone, and by the range contract — <see cref="IndexKeyRange.Contains"/>,
    /// which the conformance kit holds every index to — it has to be inclusive: exclusive, every key
    /// compared equal to it and fell outside, and a source that reads ranges as the contract says (an
    /// Akade tuple index does) answered nothing. POCO's own search happened to read it the intended
    /// way, which is why the end-to-end half of this passed before.
    /// </summary>
    [Fact]
    public async Task An_empty_prefix_after_an_equality_prefix_covers_the_equality()
    {
        var plan = new IndexLookupOperator.RangePlan
        {
            Lower = [IndexLookupOperator.BoundPlan.Constant("x"), IndexLookupOperator.BoundPlan.Constant("%")],
            LowerInclusive = true,
            Upper = [],
            UpperInclusive = false,
            Prefix = true,
        };

        var range = plan.Bind([], out var decided)!;

        Assert.True(decided);
        Assert.True(range.Contains(["x", "KB_1"], [SortDirection.AscNullsLast, SortDirection.AscNullsLast]));
        Assert.True(range.Contains(["x", string.Empty], [SortDirection.AscNullsLast, SortDirection.AscNullsLast]));
        Assert.False(range.Contains(["y", "100%"], [SortDirection.AscNullsLast, SortDirection.AscNullsLast]));
        Assert.False(range.Contains(["x", null], [SortDirection.AscNullsLast, SortDirection.AscNullsLast]));

        var (rows, _) = await RunTagsAsync(
            TagsPlan([IrBuilder.Range([Text("x"), Text("%")], [], prefix: true)], compound: true),
            compound: true);
        Assert.Equal(3, rows.Count);
    }

    private sealed record Tag(string Kind, string Name);

    private static readonly Tag[] Tags =
    [
        new("x", "KB_1"),
        new("x", "KBx1"),
        new("x", "KB\\x1"),
        new("y", "100%"),
        new("y", "1000"),
    ];

    private static PocoSource TagsSource(bool compound) =>
        new PocoSourceBuilder("mem")
            .AddTable("tags", Tags, t =>
            {
                if (compound)
                {
                    t.Index("ix_tags_kind_name", Chalk.Ir.IndexKind.Ordered, unique: false, x => x.Kind, x => x.Name);
                }
                else
                {
                    t.Index("ix_tags_name", Chalk.Ir.IndexKind.Ordered, unique: false, x => x.Name);
                }
            })
            .Build();

    private static Plan TagsPlan(IReadOnlyList<IndexRange> ranges, bool compound = false)
    {
        var source = TagsSource(compound);
        var table = source.DescribeSchema().FindTable("tags")!;
        var row = new RowType();
        foreach (var column in new[] { 1, 0 })
        {
            row.Fields.Add(new IrField { Name = table.Columns[column].Name, Type = table.Columns[column].Type.ToProto() });
        }

        return IrBuilder.Plan(IrBuilder.IndexLookup(
            "tags", compound ? "ix_tags_kind_name" : "ix_tags_name", row, ranges, projection: [1, 0]));
    }

    private static Task<(List<object?[]> Rows, ExecutionStats Stats)> RunTagsAsync(
        Plan plan, bool compound = false) =>
        RunAsync(plan, source: TagsSource(compound));

    /// <summary>A lookup on <c>Group</c> whose pattern is parameter 0, and whose residual is its LIKE.</summary>
    private static Plan RechecksItsLike(string? escape)
    {
        var row = Row(Source(), [0, 1, 2]);
        var range = IrBuilder.Range([IrBuilder.Param(0, IrBuilder.Str(nullable: true))], [], prefix: true);
        if (escape is not null)
        {
            range.Escape = escape;
        }

        var lookup = IrBuilder.IndexLookup("points", "ix_points_Group", row, [range]);
        lookup.IndexLookup.Residual = escape is null
            ? IrBuilder.Call(
                FunctionId.Like,
                IrBuilder.Bool(true),
                IrBuilder.Ref(row, 0),
                IrBuilder.Param(0, IrBuilder.Str(nullable: true)))
            : IrBuilder.Call(
                FunctionId.Like,
                IrBuilder.Bool(true),
                IrBuilder.Ref(row, 0),
                IrBuilder.Param(0, IrBuilder.Str(nullable: true)),
                IrBuilder.Lit(escape));
        return IrBuilder.Plan(lookup, parameterTypes: [IrBuilder.Str(nullable: true)]);
    }

    private static Expr Text(string value) => IrBuilder.Lit(value);

    private static PocoSource Source() =>
        new PocoSourceBuilder("mem").AddTable("points", Points, t => t.Index(p => p.Group)).Build();

    private static RowType Row(PocoSource source, IReadOnlyList<int> projection)
    {
        var table = source.DescribeSchema().FindTable("points")!;
        var row = new RowType();
        foreach (var column in projection)
        {
            row.Fields.Add(new IrField
            {
                Name = table.Columns[column].Name,
                Type = table.Columns[column].Type.ToProto(),
            });
        }

        return row;
    }

    private static Plan Plan(IReadOnlyList<IndexRange> ranges, params IrType[] parameterTypes)
    {
        var source = Source();
        return IrBuilder.Plan(
            IrBuilder.IndexLookup("points", "ix_points_Group", Row(source, [0, 1, 2]), ranges),
            parameterTypes: parameterTypes);
    }

    private static async Task<(List<object?[]> Rows, ExecutionStats Stats)> RunAsync(
        Plan plan,
        IReadOnlyList<object?>? parameters = null,
        int batchSize = 4096,
        PocoSource? source = null)
    {
        source ??= Source();
        var catalog = new CatalogContext
        {
            ContextId = "test",
            Epoch = 1,
            Schemas = [source.DescribeSchema()],
        };

        var compiled = PlanCompiler.Compile(
            plan,
            catalog,
            new Dictionary<string, ISourceRuntime> { ["mem"] = source },
            new ExecutionSettings { BatchSize = batchSize });

        var stats = new ExecutionStats();
        using var arena = new ExecutionArena();
        var rows = new List<object?[]>();
        await foreach (var batch in compiled.ExecuteAsync(
            parameters ?? [], stats, arena, TestContext.Current.CancellationToken))
        {
            using (batch)
            {
                rows.AddRange(BatchReader.ToStorageRows([batch]));
            }
        }

        return (rows, stats);
    }
}

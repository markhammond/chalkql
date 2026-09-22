using Chalk.Catalog;
using Chalk.Execution.Operators;
using Chalk.Execution.Tests.Harness;
using Chalk.Ir;
using Chalk.Sources;
using Chalk.TestKit;
using Chalk.Tests;
using CatalogContext = Chalk.Catalog.CatalogContext;
using IrType = Chalk.Ir.Type;

namespace Chalk.Execution.Tests;

/// <summary>
/// A pushed <c>LIMIT</c> or <c>OFFSET</c> that is a parameter: read when the execution starts,
/// written into the query text as a whole number, and never bound by the provider (D288).
/// </summary>
/// <remarks>
/// The plans here are hand-built, as the federation operator tests' are: what the planner produces
/// is pinned where the planner is, and what is left is the executor's own side of the boundary —
/// which placeholder it wrote into, what it left for the provider, and what it refuses.
/// </remarks>
public sealed class RenderedBoundTests
{
    private readonly Xunit.ITestOutputHelper _output;

    public RenderedBoundTests(Xunit.ITestOutputHelper output) => _output = output;

    private static readonly TestTable Facts = new()
    {
        Name = "facts",
        Columns = [("k", ChalkType.String()), ("v", ChalkType.Int64())],
        Rows =
        [
            ["a", 1L],
            ["b", 2L],
            ["c", 3L],
        ],
    };

    private static readonly TestTable Drivers = new()
    {
        Name = "drivers",
        Columns = [("k", ChalkType.String())],
        Rows = [["a"]],
    };

    // ------------------------------------------------------------------ the scanner

    /// <summary>The named placeholder is the one that changes, and it is counted over the text.</summary>
    [Fact]
    public void A_bound_is_written_into_the_placeholder_its_position_names()
    {
        Assert.Equal(
            "SELECT k FROM facts WHERE k > ? LIMIT 25",
            RenderedBounds.Substitute("SELECT k FROM facts WHERE k > ? LIMIT ?", [1], [25L]));

        Assert.Equal(
            "SELECT TOP (25) k FROM facts WHERE k > ?",
            RenderedBounds.Substitute("SELECT TOP (?) k FROM facts WHERE k > ?", [0], [25L]));
    }

    /// <summary>Two of them, in the order the text writes them.</summary>
    [Fact]
    public void Two_bounds_are_written_in_the_order_the_text_writes_them()
    {
        Assert.Equal(
            "SELECT k FROM facts ORDER BY k OFFSET 3 ROWS FETCH NEXT 7 ROWS ONLY",
            RenderedBounds.Substitute(
                "SELECT k FROM facts ORDER BY k OFFSET ? ROWS FETCH NEXT ? ROWS ONLY",
                [0, 1],
                [3L, 7L]));
    }

    /// <summary>
    /// A <c>?</c> inside a quoted identifier, a string literal or a comment is text: it is neither
    /// counted nor replaced. A scanner that did not know the difference would rewrite a predicate
    /// and return different rows, quietly — which is why the client's own substitution is a scanner
    /// too, under the same rules.
    /// </summary>
    [Fact]
    public void A_question_mark_in_a_quoted_run_or_a_comment_is_not_a_placeholder()
    {
        const string text =
            "SELECT \"we?rd\" FROM facts /* ? */ WHERE k = 'a?b' -- ?\n AND k <> ? LIMIT ?";

        Assert.Equal(
            "SELECT \"we?rd\" FROM facts /* ? */ WHERE k = 'a?b' -- ?\n AND k <> ? LIMIT 9",
            RenderedBounds.Substitute(text, [1], [9L]));
        Assert.Equal(
            "SELECT \"we?rd\" FROM facts /* ? */ WHERE k = 'a?b' -- ?\n AND k <> 4 LIMIT ?",
            RenderedBounds.Substitute(text, [0], [4L]));
    }

    /// <summary>A doubled quote is an escape and does not end the run.</summary>
    [Fact]
    public void A_doubled_quote_does_not_end_a_quoted_run()
    {
        Assert.Equal(
            "SELECT k FROM facts WHERE k = 'it''s ? here' LIMIT 2",
            RenderedBounds.Substitute(
                "SELECT k FROM facts WHERE k = 'it''s ? here' LIMIT ?", [0], [2L]));
    }

    /// <summary>A text with no placeholder where one was promised is a plan that cannot be run.</summary>
    [Fact]
    public void A_position_the_text_has_no_placeholder_for_is_refused()
    {
        var failure = Assert.Throws<InvalidPlanException>(
            () => RenderedBounds.Substitute("SELECT k FROM facts", [0], [5L]));

        Assert.Equal("I-IR-4", failure.Invariant);
    }

    // ------------------------------------------------------------------ at the source

    /// <summary>
    /// What the source is actually sent: the number written in, and one fewer value to bind. The
    /// same prepared plan run twice with two page sizes ships two different texts, which is the
    /// whole of what "rendered at execution" means.
    /// </summary>
    [Fact]
    public async Task The_source_is_sent_the_number_and_never_the_bound_as_a_parameter()
    {
        var (local, remote, catalog) = Fixture();
        var plan = BoundedPlan();

        await RunAsync(plan, local, remote, catalog, [Utf8String.FromString("a"), 25L]);
        await RunAsync(plan, local, remote, catalog, [Utf8String.FromString("a"), 100L]);

        Assert.Equal(
            [
                "SELECT k, v FROM facts WHERE k >= ? LIMIT 25",
                "SELECT k, v FROM facts WHERE k >= ? LIMIT 100",
            ],
            remote.Queries);
        // One value left for the provider, and it is the predicate's, not the bound's.
        Assert.All(remote.Calls, keys => Assert.Single(keys));
        Assert.All(remote.Calls, keys => Assert.Equal("a", keys[0]));
    }

    /// <summary>A bound at the front of the statement is the same case, by position.</summary>
    [Fact]
    public async Task A_bound_at_the_front_of_the_statement_is_rendered_by_position()
    {
        var (local, remote, catalog) = Fixture();

        await RunAsync(
            IrBuilder.Plan(
                IrBuilder.RemoteQuery(
                    "far",
                    "SELECT TOP (?) k, v FROM facts WHERE k >= ?",
                    Facts.RowType(),
                    parameters: [IrBuilder.Param(1, IrBuilder.I64()), IrBuilder.Param(0, IrBuilder.Str())],
                    renderedBounds: [0]),
                parameterTypes: [IrBuilder.Str(), IrBuilder.I64(nullable: true)]),
            local,
            remote,
            catalog,
            [Utf8String.FromString("a"), 25L]);

        Assert.Equal(["SELECT TOP (25) k, v FROM facts WHERE k >= ?"], remote.Queries);
        Assert.Equal(["a"], remote.Calls.Select(k => (string?)k[0]));
    }

    /// <summary>A NULL is not a count, and says so before the round trip is made.</summary>
    [Fact]
    public async Task A_null_bound_is_refused_by_name()
    {
        var (local, remote, catalog) = Fixture();

        var failure = await Assert.ThrowsAsync<ExecutionException>(
            () => RunAsync(BoundedPlan(), local, remote, catalog, [Utf8String.FromString("a"), null]));

        Assert.Contains(
            "NULL was bound to the LIMIT bound ?1; a LIMIT bound is a count, and NULL is not one.",
            failure.Message,
            StringComparison.Ordinal);
        Assert.Empty(remote.Queries);
    }

    /// <summary>Neither is a negative one.</summary>
    [Fact]
    public async Task A_negative_bound_is_refused_by_name()
    {
        var (local, remote, catalog) = Fixture();

        var failure = await Assert.ThrowsAsync<ExecutionException>(
            () => RunAsync(BoundedPlan(), local, remote, catalog, [Utf8String.FromString("a"), -3L]));

        Assert.Contains(
            "-3 was bound to the LIMIT bound ?1; a LIMIT bound must be zero or more.",
            failure.Message,
            StringComparison.Ordinal);
        Assert.Empty(remote.Queries);
    }

    /// <summary>Nor a fraction, which is rounded by nobody: a bound is a number of rows.</summary>
    [Fact]
    public async Task A_fractional_bound_is_refused_by_name()
    {
        var (local, remote, catalog) = Fixture();

        var failure = await Assert.ThrowsAsync<ExecutionException>(
            () => RunAsync(
                BoundedPlan(IrBuilder.Dec(38, 2)),
                local,
                remote,
                catalog,
                [Utf8String.FromString("a"), 2.5m]));

        Assert.Contains(
            "was bound to the LIMIT bound ?1", failure.Message, StringComparison.Ordinal);
        Assert.Contains(
            "a LIMIT bound is a whole number of rows, never a fraction or a share of them.",
            failure.Message,
            StringComparison.Ordinal);
        Assert.Empty(remote.Queries);
    }

    /// <summary>An offset is refused under its own name, which the pushed plan is what says.</summary>
    [Fact]
    public async Task An_offset_is_refused_under_its_own_name()
    {
        var (local, remote, catalog) = Fixture();
        var pushed = new Rel
        {
            RowType = Facts.RowType(),
            Fetch = new Fetch
            {
                Input = IrBuilder.Read(Facts.Name, Facts.RowType()),
                OffsetParam = new DynamicParam { Index = 0 },
                CountParam = new DynamicParam { Index = 1 },
            },
        };
        var plan = IrBuilder.Plan(
            IrBuilder.RemoteQuery(
                "far",
                "SELECT k, v FROM facts ORDER BY k OFFSET ? ROWS FETCH NEXT ? ROWS ONLY",
                Facts.RowType(),
                parameters: [IrBuilder.Param(0, IrBuilder.I64()), IrBuilder.Param(1, IrBuilder.I64())],
                renderedBounds: [0, 1],
                pushedPlan: pushed),
            parameterTypes: [IrBuilder.I64(nullable: true), IrBuilder.I64(nullable: true)]);

        var failure = await Assert.ThrowsAsync<ExecutionException>(
            () => RunAsync(plan, local, remote, catalog, [-1L, 5L]));

        Assert.Contains(
            "-1 was bound to the OFFSET bound ?0; a OFFSET bound must be zero or more.",
            failure.Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// One string per execution and nothing per row: the rendering happens once, before the round
    /// trip, into arrays the plan owns.
    /// </summary>
    /// <remarks>
    /// Measured as a <em>difference</em> against the same query with the number already in the
    /// text, over the same source and the same rows. That is the claim — rendering costs a constant
    /// and not a rate — and it is the only honest way to measure it here: a fake source builds its
    /// batches out of boxed rows, so its own per-row cost dwarfs anything this path could add and
    /// would swamp an absolute reading. It appears in both readings and cancels.
    /// </remarks>
    [Fact]
    public async Task Rendering_a_bound_costs_a_constant_and_not_a_rate()
    {
        const int rows = 20_000;
        var table = new TestTable
        {
            Name = "facts",
            Columns = [("k", ChalkType.String()), ("v", ChalkType.Int64())],
            Rows = [.. Enumerable.Range(0, rows).Select(i => new object?[] { "a", (long)i })],
        };

        var local = new TestSource("mem", "main", Drivers);
        var remote = new FakeRemoteSource("far", table);
        var catalog = FakeRemoteSource.Catalog(local, remote);

        var rendered = Compile(
            IrBuilder.Plan(
                IrBuilder.RemoteQuery(
                    "far",
                    "SELECT k, v FROM facts LIMIT ?",
                    table.RowType(),
                    parameters: [IrBuilder.Param(0, IrBuilder.I64())],
                    renderedBounds: [0]),
                parameterTypes: IrBuilder.I64(nullable: true)),
            catalog,
            local,
            remote);
        var literal = Compile(
            IrBuilder.Plan(
                IrBuilder.RemoteQuery("far", "SELECT k, v FROM facts LIMIT 25", table.RowType()),
                parameterTypes: IrBuilder.I64(nullable: true)),
            catalog,
            local,
            remote);

        using var arena = new ExecutionArena();
        Assert.Equal(rows, await ConsumeAsync(rendered, arena));
        Assert.Equal(rows, await ConsumeAsync(literal, arena));

        var (withBound, boundRows) = await AllocationProbe.SteadyStateAsync(
            () => ConsumeAsync(rendered, arena));
        var (withLiteral, literalRows) = await AllocationProbe.SteadyStateAsync(
            () => ConsumeAsync(literal, arena));

        Assert.Equal(rows, boundRows);
        Assert.Equal(rows, literalRows);

        var extra = withBound - withLiteral;
        var perRow = extra / (double)rows;
        _output.WriteLine(
            $"rendering a bound: {withBound} bytes against {withLiteral} for {rows} rows = "
            + $"{extra} extra, {perRow:0.####} bytes/row.");
        Assert.True(
            perRow < 1.0,
            $"rendering a bound cost {extra} extra bytes over {rows} rows ({perRow:0.####} per "
            + "row), which is a rate and not a constant.");
    }

    // ------------------------------------------------------------------ the fixture

    private static (TestSource Local, FakeRemoteSource Remote, CatalogContext Catalog) Fixture()
    {
        var local = new TestSource("mem", "main", Drivers);
        var remote = new FakeRemoteSource("far", Facts);
        return (local, remote, FakeRemoteSource.Catalog(local, remote));
    }

    private static Dictionary<string, ISourceRuntime> Sources(
        TestSource local, FakeRemoteSource remote) =>
        new(StringComparer.Ordinal) { [local.SourceId] = local, [remote.SourceId] = remote };

    private static CompiledPlan Compile(
        Plan plan, CatalogContext catalog, TestSource local, FakeRemoteSource remote) =>
        PlanCompiler.Compile(
            plan,
            catalog,
            Sources(local, remote),
            new ExecutionSettings { BatchSize = 4096, PooledOutput = true });

    /// <summary>
    /// A remote query whose text ends in a bound: the predicate's parameter first, the bound
    /// second, exactly as every dialect that spells it at the end of the statement writes them.
    /// </summary>
    private static Plan BoundedPlan(IrType? boundType = null) =>
        IrBuilder.Plan(
            IrBuilder.RemoteQuery(
                "far",
                "SELECT k, v FROM facts WHERE k >= ? LIMIT ?",
                Facts.RowType(),
                parameters:
                [
                    IrBuilder.Param(0, IrBuilder.Str()),
                    IrBuilder.Param(1, boundType ?? IrBuilder.I64()),
                ],
                renderedBounds: [1]),
            parameterTypes: [IrBuilder.Str(), boundType ?? IrBuilder.I64(nullable: true)]);

    private static async Task RunAsync(
        Plan plan,
        TestSource local,
        FakeRemoteSource remote,
        CatalogContext catalog,
        IReadOnlyList<object?> parameters)
    {
        var compiled = PlanCompiler.Compile(
            plan,
            catalog,
            Sources(local, remote),
            new ExecutionSettings { BatchSize = 4096, ValidateBatchLifetimes = true });

        await foreach (var batch in compiled.ExecuteAsync(
            parameters, new ExecutionStats(), arena: null, TestContext.Current.CancellationToken))
        {
            batch.Dispose();
        }
    }

    private static async Task<int> ConsumeAsync(CompiledPlan compiled, ExecutionArena arena)
    {
        var rows = 0;
        await foreach (var batch in compiled.ExecuteAsync(
            [25L], new ExecutionStats(), arena, TestContext.Current.CancellationToken))
        {
            rows += batch.Length;
            batch.Dispose();
        }

        return rows;
    }
}

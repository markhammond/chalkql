using Chalk.Catalog;
using Chalk.Ir;
using Chalk.Sources.Poco;
using Chalk.Sources;
using Chalk.TestKit;
using Chalk.Tests;
using CatalogContext = Chalk.Catalog.CatalogContext;

namespace Chalk.Execution.Tests;

/// <summary>
/// The milestone's allocation gate, as a test rather than as a reading of the code: a scan, filter
/// and project pipeline over 20 000 rows must allocate well under a byte per row once the scratch is
/// warm (<c>06-m1-workplan.md</c> §2, ADR 0007).
/// </summary>
/// <remarks>
/// <para>
/// Run under <c>OutputMemory.Pooled</c>, which is what the benchmark measures: the batch buffers come
/// from <c>ArrayPool</c> and go back on dispose, so what is left is the engine's own per-batch
/// bookkeeping. The managed-output default deliberately allocates the result, because the host may
/// keep it.
/// </para>
/// <para>
/// Every number here is taken through <see cref="AllocationProbe"/>: a collection landing inside a
/// measured window charges the measuring thread for up to 8 KB it never allocated, which on the
/// narrower of these budgets is the difference between passing and failing and has nothing to do
/// with the pipeline.
/// </para>
/// </remarks>
public sealed class AllocationTests
{
    private sealed record Bar(string Symbol, long Volume, double Close);

    private const int Rows = 20_000;

    private readonly Xunit.ITestOutputHelper _output;

    public AllocationTests(Xunit.ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Scan_filter_project_allocates_under_a_byte_per_row()
    {
        var bars = new Bar[Rows];
        for (var i = 0; i < Rows; i++)
        {
            bars[i] = new Bar(i % 3 == 0 ? "AAA" : "BBB", i, i * 1.5d);
        }

        var source = new PocoSourceBuilder("mem").AddTable("bars", bars).Build();
        var schema = source.DescribeSchema();
        var catalog = new CatalogContext { ContextId = "test", Epoch = 1, Schemas = [schema] };
        var compiled = PlanCompiler.Compile(
            Pipeline(schema.Tables[0]),
            catalog,
            new Dictionary<string, ISourceRuntime> { ["mem"] = source },
            new ExecutionSettings { BatchSize = 4096, PooledOutput = true });

        // One arena across every run, which is what the engine's arena pool gives an execution
        // (08-execution-arena.md §2.1). This run warms the expression scratch and the arena's pools
        // and checks the row count; the probe warms again and measures what a warm run costs.
        using var arena = new ExecutionArena();
        var first = await ConsumeAsync(compiled, arena);
        Assert.Equal(Rows - ((Rows + 2) / 3), first);

        var (allocated, second) = await AllocationProbe.SteadyStateAsync(
            () => ConsumeAsync(compiled, arena));

        Assert.Equal(first, second);
        Assert.Equal(0, arena.OutstandingBytes);
        var perRow = allocated / (double)Rows;
        _output.WriteLine(
            $"scan+filter+project: {allocated} bytes for {Rows} rows = {perRow:0.###} bytes/row.");
        Assert.True(
            perRow < 1.0,
            $"scan+filter+project allocated {allocated} bytes for {Rows} rows ({perRow:0.###} per row).");
    }

    /// <summary>
    /// The same gate on the index-lookup path (M2): a lookup gathers through the same compiled chunk
    /// writers a scan uses, so it has to stay under the same budget. The rows here are the ones the
    /// lookup reads, which is what makes the per-row number comparable with the scan's.
    /// </summary>
    [Fact]
    public async Task Index_lookup_filter_project_allocates_under_a_byte_per_row()
    {
        var bars = new Bar[Rows];
        for (var i = 0; i < Rows; i++)
        {
            bars[i] = new Bar(i % 3 == 0 ? "AAA" : "BBB", i, i * 1.5d);
        }

        var source = new PocoSourceBuilder("mem")
            .AddTable("bars", bars, t => t.Index(b => b.Symbol))
            .Build();
        var schema = source.DescribeSchema();
        var catalog = new CatalogContext { ContextId = "test", Epoch = 1, Schemas = [schema] };
        var compiled = PlanCompiler.Compile(
            LookupPipeline(schema.Tables[0]),
            catalog,
            new Dictionary<string, ISourceRuntime> { ["mem"] = source },
            new ExecutionSettings { BatchSize = 4096, PooledOutput = true });

        using var arena = new ExecutionArena();
        var matched = Rows - ((Rows + 2) / 3);
        var first = await ConsumeAsync(compiled, arena);
        Assert.Equal(matched, first);

        var (allocated, second) = await AllocationProbe.SteadyStateAsync(
            () => ConsumeAsync(compiled, arena));

        Assert.Equal(first, second);
        Assert.Equal(0, arena.OutstandingBytes);
        var perRow = allocated / (double)matched;
        _output.WriteLine(
            $"indexlookup+filter+project: {allocated} bytes for {matched} rows = {perRow:0.###} bytes/row.");
        Assert.True(
            perRow < 1.0,
            $"indexlookup+filter+project allocated {allocated} bytes for {matched} rows ({perRow:0.###} per row).");
    }

    /// <summary>
    /// The join gate (12-joins.md §6). A hash join is not held to the scan path's budget — it
    /// materialises a build side and assembles output batches column by column — so this one
    /// <em>reports</em> bytes per output row rather than asserting a byte. The bound it does assert
    /// is loose enough to catch a per-row allocation appearing where there was none, and the number
    /// in the output is what a reviewer reads.
    /// </summary>
    [Fact]
    public async Task Hash_join_reports_its_bytes_per_output_row()
    {
        var bars = new Bar[Rows];
        for (var i = 0; i < Rows; i++)
        {
            bars[i] = new Bar(i % 3 == 0 ? "AAA" : "BBB", i, i * 1.5d);
        }

        var symbols = new[] { new SymbolRow("AAA", "A"), new SymbolRow("BBB", "B") };
        var source = new PocoSourceBuilder("mem")
            .AddTable("bars", bars)
            .AddTable("symbols", symbols)
            .Build();
        var schema = source.DescribeSchema();
        var catalog = new CatalogContext { ContextId = "test", Epoch = 1, Schemas = [schema] };
        var compiled = PlanCompiler.Compile(
            JoinPipeline(schema.Tables[0], schema.Tables[1]),
            catalog,
            new Dictionary<string, ISourceRuntime> { ["mem"] = source },
            new ExecutionSettings { BatchSize = 4096, PooledOutput = true });

        using var arena = new ExecutionArena();
        var first = await ConsumeAsync(compiled, arena);
        Assert.Equal(Rows, first);

        var (allocated, second) = await AllocationProbe.SteadyStateAsync(
            () => ConsumeAsync(compiled, arena));

        Assert.Equal(first, second);
        Assert.Equal(0, arena.OutstandingBytes);
        var perRow = allocated / (double)Rows;
        _output.WriteLine(
            $"bars JOIN symbols: {allocated} bytes for {Rows} output rows = {perRow:0.###} bytes/row.");
        Assert.True(
            perRow < 16.0,
            $"bars JOIN symbols allocated {allocated} bytes for {Rows} rows ({perRow:0.###} per row).");
    }

    private sealed record SymbolRow(string Symbol, string Base);

    /// <summary>HashJoin(Read bars, Read symbols) ON symbol, projected to three columns.</summary>
    private static Plan JoinPipeline(TableDescriptor bars, TableDescriptor symbols)
    {
        var left = RowOf(bars);
        var right = RowOf(symbols);
        var join = IrBuilder.HashJoin(
            IrBuilder.Read("bars", left), IrBuilder.Read("symbols", right), [0], [0]);
        var joined = join.RowType;
        var project = IrBuilder.Project(
            join,
            [
                ("symbol", IrBuilder.Ref(joined, 0)),
                ("volume", IrBuilder.Ref(joined, 1)),
                ("base", IrBuilder.Ref(joined, left.Fields.Count + 1)),
            ]);
        return IrBuilder.Plan(project);
    }

    private static RowType RowOf(TableDescriptor table)
    {
        var row = new RowType();
        foreach (var column in table.Columns)
        {
            row.Fields.Add(new Field { Name = column.Name, Type = column.Type.ToProto() });
        }

        return row;
    }

    /// <summary>IndexLookup → Filter → Project: the M1 pipeline with an index in place of the scan.</summary>
    private static Plan LookupPipeline(TableDescriptor table)
    {
        var row = new RowType();
        foreach (var column in table.Columns)
        {
            row.Fields.Add(new Field { Name = column.Name, Type = column.Type.ToProto() });
        }

        var lookup = IrBuilder.IndexLookup(
            "bars",
            "ix_bars_Symbol",
            row,
            [IrBuilder.Range([IrBuilder.Lit("BBB")], [IrBuilder.Lit("BBB")])]);
        var filter = IrBuilder.Filter(
            lookup,
            IrBuilder.Call(
                FunctionId.Ne,
                IrBuilder.Bool(),
                IrBuilder.Ref(row, 0),
                IrBuilder.Lit("AAA")));
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

    /// <summary>Read → Filter → Project, the three operators the gate names.</summary>
    /// <summary>
    /// The gate on <c>FINGERPRINT</c> (step 26, 16-entitlements.md §4). A mask is evaluated at every
    /// entitled leaf, per row, for every principal that is not disclosed the column in full, so it
    /// has to be as free of allocation as a projection: the MAC is keyed once and reused, and the
    /// digest and its hex go into buffers the node owns.
    /// </summary>
    [Fact]
    public async Task Fingerprint_allocates_under_a_byte_per_row() =>
        await MaskGateAsync(
            "fingerprint",
            row => IrBuilder.Call(
                FunctionId.Fingerprint,
                IrBuilder.Str(),
                IrBuilder.Ref(row, 0),
                IrBuilder.Lit("k")));

    /// <summary>The same for <c>PRESENT</c>, which a masked column's consumer asks per row.</summary>
    [Fact]
    public async Task Present_allocates_under_a_byte_per_row() =>
        await MaskGateAsync(
            "present",
            row => IrBuilder.Call(FunctionId.Present, IrBuilder.Bool(), IrBuilder.Ref(row, 0)));

    /// <summary>A scan and a one-expression projection over 20 000 rows, measured warm.</summary>
    private async Task MaskGateAsync(string name, Func<RowType, Expr> mask)
    {
        var bars = new Bar[Rows];
        for (var i = 0; i < Rows; i++)
        {
            bars[i] = new Bar("SYM" + (i % 97), i, i * 1.5d);
        }

        var source = new PocoSourceBuilder("mem").AddTable("bars", bars).Build();
        var schema = source.DescribeSchema();
        var catalog = new CatalogContext { ContextId = "test", Epoch = 1, Schemas = [schema] };
        var compiled = PlanCompiler.Compile(
            MaskPipeline(schema.Tables[0], mask),
            catalog,
            new Dictionary<string, ISourceRuntime> { ["mem"] = source },
            new ExecutionSettings { BatchSize = 4096, PooledOutput = true });

        using var arena = new ExecutionArena();
        var first = await ConsumeAsync(compiled, arena);
        Assert.Equal(Rows, first);

        var (allocated, second) = await AllocationProbe.SteadyStateAsync(
            () => ConsumeAsync(compiled, arena));

        Assert.Equal(first, second);
        Assert.Equal(0, arena.OutstandingBytes);
        var perRow = allocated / (double)Rows;
        _output.WriteLine(
            $"scan+{name}: {allocated} bytes for {Rows} rows = {perRow:0.###} bytes/row.");
        Assert.True(
            perRow < 1.0,
            $"scan+{name} allocated {allocated} bytes for {Rows} rows ({perRow:0.###} per row).");
    }

    private static Plan MaskPipeline(TableDescriptor table, Func<RowType, Expr> mask)
    {
        var row = new RowType();
        foreach (var column in table.Columns)
        {
            row.Fields.Add(new Field { Name = column.Name, Type = column.Type.ToProto() });
        }

        var read = IrBuilder.Read("bars", row);
        return IrBuilder.Plan(
            IrBuilder.Project(read, [("masked", mask(row)), ("volume", IrBuilder.Ref(row, 1))]));
    }

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
                FunctionId.Ne,
                IrBuilder.Bool(),
                IrBuilder.Ref(row, 0),
                IrBuilder.Lit("AAA")));
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

    private static async Task<int> ConsumeAsync(CompiledPlan compiled, ExecutionArena arena)
    {
        var rows = 0;
        await foreach (var batch in compiled.ExecuteAsync(
            [], new ExecutionStats(), arena, CancellationToken.None))
        {
            rows += batch.Length;
            batch.Dispose();
        }

        return rows;
    }
}

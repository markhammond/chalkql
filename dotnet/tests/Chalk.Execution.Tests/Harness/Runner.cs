using Apache.Arrow;
using Chalk.Ir;
using Chalk.Sources;
using Chalk.TestKit;
using IrType = Chalk.Ir.Type;

namespace Chalk.Execution.Tests.Harness;

/// <summary>Compiles and runs a hand-built plan on either engine, and collects the batches.</summary>
internal static class Runner
{
    /// <summary>The three batch sizes every operator test runs at (<c>05-testing.md</c> §1, §5).</summary>
    public static readonly int[] BatchSizes = [1, 7, 4096];

    public static CompiledPlan Compile(
        Plan plan,
        TestSource source,
        int batchSize = 4096,
        bool reference = false,
        bool pooledOutput = false,
        TimeProvider? clock = null,
        double? compactionThreshold = null,
        bool? validateLifetimes = null,
        StringLayouts? outputStrings = null,
        bool bufferedWindows = false) =>
        PlanCompiler.Compile(
            plan,
            source.Catalog(),
            new Dictionary<string, ISourceRuntime> { [source.SourceId] = source },
            new ExecutionSettings
            {
                BatchSize = batchSize,
                UseReferenceEngine = reference,
                ForceBufferedWindows = bufferedWindows,
                PooledOutput = pooledOutput,
                TimeProvider = clock ?? TimeProvider.System,
                SelectionCompactionThreshold = compactionThreshold ?? 0.5,
                ValidateBatchLifetimes = validateLifetimes ?? true,
                OutputStrings = outputStrings ?? StringLayouts.Utf8View,
            });

    public static async Task<List<RecordBatch>> RunAsync(
        Plan plan,
        TestSource source,
        int batchSize = 4096,
        bool reference = false,
        object?[]? parameters = null,
        ExecutionStats? stats = null,
        bool pooledOutput = false,
        double? compactionThreshold = null,
        bool bufferedWindows = false)
    {
        var compiled = Compile(
            plan,
            source,
            batchSize,
            reference,
            pooledOutput,
            compactionThreshold: compactionThreshold,
            bufferedWindows: bufferedWindows);
        return await CollectAsync(compiled, parameters, stats).ConfigureAwait(false);
    }

    public static async Task<List<RecordBatch>> CollectAsync(
        CompiledPlan compiled, object?[]? parameters = null, ExecutionStats? stats = null)
    {
        var batches = new List<RecordBatch>();
        await foreach (var batch in compiled.ExecuteAsync(
            parameters ?? [], stats ?? new ExecutionStats(), arena: null, CancellationToken.None))
        {
            batches.Add(batch);
        }

        return batches;
    }

    /// <summary>
    /// The pipeline's own batches, without the Arrow output boundary — the shape
    /// <c>15-zero-allocation-execution.md</c> §1 is about. Each entry is the operator's reused slot
    /// as it stood, so only the summaries taken here survive the next batch.
    /// </summary>
    public static async Task<List<(int Rows, int[] Offsets, int[] Lengths)>> ColumnarShapeAsync(
        Plan plan, TestSource source, int batchSize, int column)
    {
        var compiled = Compile(plan, source, batchSize);
        using var arena = new ExecutionArena();
        var shapes = new List<(int, int[], int[])>();
        await foreach (var batch in compiled.ExecuteColumnarAsync(
            [], new ExecutionStats(), arena, CancellationToken.None))
        {
            var view = batch.Column(column);
            shapes.Add((batch.Count, [view.Offset], [view.Length]));
        }

        return shapes;
    }

    /// <summary>Every row of a run, as storage values — what the assertions read.</summary>
    public static async Task<List<object?[]>> RowsAsync(
        Plan plan,
        TestSource source,
        int batchSize = 4096,
        bool reference = false,
        object?[]? parameters = null,
        ExecutionStats? stats = null,
        bool bufferedWindows = false)
    {
        var batches = await RunAsync(
                plan, source, batchSize, reference, parameters, stats, bufferedWindows: bufferedWindows)
            .ConfigureAwait(false);
        var rows = ResultComparer.Rows(batches);
        foreach (var batch in batches)
        {
            batch.Dispose();
        }

        return rows;
    }

    /// <summary>The single-column result of projecting <paramref name="expr"/> over a table.</summary>
    public static async Task<List<object?>> ProjectAsync(
        Expr expr,
        TestSource source,
        TestTable table,
        int batchSize = 4096,
        bool reference = false,
        object?[]? parameters = null,
        IrType[]? parameterTypes = null)
    {
        var plan = ProjectionPlan(expr, table, parameterTypes);
        var rows = await RowsAsync(plan, source, batchSize, reference, parameters).ConfigureAwait(false);
        return [.. rows.Select(r => r[0])];
    }

    public static Plan ProjectionPlan(Expr expr, TestTable table, IrType[]? parameterTypes = null)
    {
        var row = table.RowType();
        var read = IrBuilder.Read(table.Name, row);
        var project = IrBuilder.Project(read, [("value", expr)]);
        return IrBuilder.Plan(project, parameterTypes: parameterTypes ?? []);
    }

    /// <summary>Runs one plan on both engines and asserts they agree (§5, invariant I4).</summary>
    public static async Task AssertEnginesAgreeAsync(
        Plan plan,
        TestSource source,
        ResultComparisonOptions? options = null,
        object?[]? parameters = null)
    {
        foreach (var batchSize in BatchSizes)
        {
            var vectorised = await RunAsync(plan, source, batchSize, reference: false, parameters)
                .ConfigureAwait(false);
            var expected = await RunAsync(plan, source, batchSize, reference: true, parameters)
                .ConfigureAwait(false);
            try
            {
                ResultComparer.AssertEquivalent(
                    expected, vectorised, options ?? ResultComparisonOptions.Ordered);
            }
            finally
            {
                foreach (var batch in vectorised.Concat(expected))
                {
                    batch.Dispose();
                }
            }
        }
    }
}

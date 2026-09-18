using Apache.Arrow;
using Chalk.Catalog;
using Chalk.Execution.Operators;
using Chalk.Execution.Reference;
using Chalk.Execution.Vectors;
using Chalk.Sources;
using ArrowSchema = Apache.Arrow.Schema;
using CatalogContext = Chalk.Catalog.CatalogContext;

namespace Chalk.Execution;

/// <summary>
/// A plan that has been validated, resolved, and turned into an operator factory. Every
/// execution builds its own operator tree; everything the tree works in — data-shaped and
/// plan-shaped alike — comes from that execution's <see cref="ExecutionArena"/>.
/// </summary>
internal sealed class CompiledPlan
{
    private readonly OperatorFactory _root;
    private readonly ExecutionSettings _settings;
    private readonly CatalogContext _catalog;
    private readonly IReadOnlyDictionary<string, ISourceRuntime> _sources;
    private readonly ChalkType[] _outputTypes;

    /// <summary>
    /// A concurrent execution that finds the slot empty simply builds its own, so there is no contention
    /// and no pooling policy to tune.
    /// </summary>
    private ExecutionTree? _spare;

    internal CompiledPlan(
        Ir.Plan plan,
        ArrowSchema outputSchema,
        IReadOnlyList<ChalkType> parameterTypes,
        IReadOnlyList<Ir.BoundScalar> boundScalars,
        IReadOnlyList<ChalkType> outputTypes,
        OperatorFactory root,
        CatalogContext catalog,
        IReadOnlyDictionary<string, ISourceRuntime> sources,
        ExecutionSettings settings)
    {
        Plan = plan;
        OutputSchema = outputSchema;
        ParameterTypes = parameterTypes;
        BoundScalars = boundScalars;
        _boundTypes = [.. parameterTypes, .. boundScalars.Select(s => ChalkType.FromProto(s.Type))];
        _outputTypes = [.. outputTypes];
        _root = root;
        _catalog = catalog;
        _sources = sources;
        _settings = settings;
    }

    private readonly ChalkType[] _boundTypes;

    /// <summary>
    /// The context scalars this plan reads at execution, in slot order after
    /// <see cref="ParameterTypes"/> Empty under prepare-time binding.
    /// </summary>
    public IReadOnlyList<Ir.BoundScalar> BoundScalars { get; }

    public Ir.Plan Plan { get; }

    public ArrowSchema OutputSchema { get; }

    /// <summary>One per <c>DynamicParam.index</c>, as the planner inferred them.</summary>
    public IReadOnlyList<ChalkType> ParameterTypes { get; }

    /// <summary>
    /// Runs the plan. Parameter binding happens before anything is enumerated, so an
    /// <see cref="ArgumentException"/> naming the parameter arrives at the call, not mid-stream. The
    /// returned sequence is enumerated once; calling this again starts a fresh execution.
    /// </summary>
    /// <param name="stats"></param>
    /// <param name="arena">
    /// Where this execution's memory comes from. Null means a private one, created and disposed
    /// around the run — correct, but it starts cold, so the engine pools arenas instead. It comes
    /// before the token so the token stays last, as it does on <c>ChalkEngine</c> (ADR 0012, F7).
    /// </param>
    /// <param name="parameters"></param>
    /// <param name="ct"></param>
    public IAsyncEnumerable<RecordBatch> ExecuteAsync(
        IReadOnlyList<object?> parameters,
        ExecutionStats stats,
        ExecutionArena? arena = null,
        CancellationToken ct = default) =>
        ExecuteAsync(parameters, stats, relations: null, arena, ct);

    /// <summary>
    /// The same, with the relations this execution binds by name — what a <c>ContextTable</c>
    /// materialises from (16-entitlements.md §2, §4). Null and empty mean the same thing and are
    /// what every statement over a catalog without entitlements passes.
    /// </summary>
    internal IAsyncEnumerable<RecordBatch> ExecuteAsync(
        IReadOnlyList<object?> parameters,
        ExecutionStats stats,
        IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyList<object?>>>? relations,
        ExecutionArena? arena = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(stats);
        // The statement's own parameters, then this plan's bound scalars: one array, one index each.
        var bound = ParameterBinder.Bind(parameters, _boundTypes);
        return Run(bound, stats, relations, arena, ct);
    }

    /// <summary>
    /// Holds the arena for the length of the execution — claimed before the first batch, released
    /// after the last one or after a failure, so a second concurrent execution on the same arena is
    /// refused rather than corrupting it.
    /// </summary>
    private async IAsyncEnumerable<RecordBatch> Run(
        ScalarValue[] parameters,
        ExecutionStats stats,
        IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyList<object?>>>? relations,
        ExecutionArena? supplied,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var arena = supplied ?? new ExecutionArena();
        arena.BeginExecution(stats);
        try
        {
            var batches = _settings.UseReferenceEngine
                ? new ReferenceExecutor(this, _catalog, _sources, _settings, relations)
                    .ExecuteAsync(parameters, stats, arena, ct)
                : Vectorised(parameters, stats, relations, arena, ct);

            await foreach (var batch in batches.WithCancellation(ct).ConfigureAwait(false))
            {
                yield return batch;
            }
        }
        finally
        {
            arena.EndExecution();
            if (supplied is null)
            {
                arena.Dispose();
            }
        }
    }

    /// <summary>
    /// The pipeline's own batches, without the Arrow output boundary — what
    /// <c>15-zero-allocation-execution.md</c> §5's steady-state gate measures ("anywhere between a
    /// source and the root"). The batches are the root operator's reused slot: valid until the next
    /// one, never handed to a host. Tests and the benchmark only.
    /// </summary>
    internal async IAsyncEnumerable<ColumnarBatch> ExecuteColumnarAsync(
        IReadOnlyList<object?> parameters,
        ExecutionStats stats,
        ExecutionArena arena,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(stats);
        var bound = ParameterBinder.Bind(parameters, ParameterTypes);
        arena.BeginExecution(stats);
        var tree = Rent();
        tree.Context.Begin(stats, arena, bound, _settings.TimeProvider.GetUtcNow());

        // As in Vectorised: the token has to reach the tree before the first pull creates the
        // operators' enumerators.
        _ = tree.Root.ExecuteAsync(ct);

        var completed = false;
        try
        {
            while (true)
            {
                if (tree.Root.TryNext(out var batch))
                {
                    stats.AddRowsProduced(batch!.Count);
                    stats.AddBatchesProduced(1);
                    yield return batch;
                    continue;
                }

                if (tree.Root.IsDone || !await tree.Root.WaitAsync(ct).ConfigureAwait(false))
                {
                    break;
                }

                stats.AddRowsProduced(tree.Root.Current!.Count);
                stats.AddBatchesProduced(1);
                yield return tree.Root.Current!;
            }

            completed = true;
        }
        finally
        {
            await tree.Root.DisposeAsync().ConfigureAwait(false);
            tree.Context.End();
            arena.EndExecution();
            if (completed)
            {
                Return(tree);
            }
        }
    }

    /// <summary>The output row's logical types, in order. The reference executor builds batches from these.</summary>
    internal IReadOnlyList<ChalkType> OutputTypes => _outputTypes;

    /// <summary>
    /// The same plan with a decorated output schema, for the per-column disclosure the entitlement
    /// report carries (step 26, <c>docs/design/16-entitlements.md</c> §3.12): every batch then holds
    /// the <c>chalk.disclosure</c> field metadata a typed consumer reads.
    /// </summary>
    /// <remarks>
    /// The schema is decorated here rather than in <see cref="PlanCompiler"/> because the disclosure
    /// is in the planner's report and not in the plan: the compiler has no way to know it, and
    /// threading it through would put a policy word in every operator's constructor for nothing.
    /// The field order, names, types, and nullability are the compiler's own and are unchanged.
    /// </remarks>
    public CompiledPlan WithOutputSchema(ArrowSchema outputSchema)
    {
        ArgumentNullException.ThrowIfNull(outputSchema);
        if (outputSchema.FieldsList.Count != OutputSchema.FieldsList.Count)
        {
            throw new ArgumentException(
                "a decorated output schema must have the compiler's own fields, in order",
                nameof(outputSchema));
        }

        return new CompiledPlan(
            Plan, outputSchema, ParameterTypes, BoundScalars, _outputTypes, _root, _catalog, _sources,
            _settings);
    }

    /// <summary>
    /// The same plan at another batch size, for the gate measurements of
    /// <c>15-zero-allocation-execution.md</c> §5, which vary the batch count over one query. The
    /// operator factories read the batch size from the execution's settings, so nothing is
    /// recompiled; the copy has its own warm-tree slot, which is what makes each measurement
    /// independent.
    /// </summary>
    internal CompiledPlan WithBatchSize(int batchSize) => new(
        Plan,
        OutputSchema,
        ParameterTypes,
        BoundScalars,
        _outputTypes,
        _root,
        _catalog,
        _sources,
        new ExecutionSettings
        {
            BatchSize = batchSize,
            UseReferenceEngine = _settings.UseReferenceEngine,
            PooledOutput = _settings.PooledOutput,
            TimeProvider = _settings.TimeProvider,
            ValidateBatchLifetimes = _settings.ValidateBatchLifetimes,
            SelectionCompactionThreshold = _settings.SelectionCompactionThreshold,
        });

    /// <summary>Builds the immutable half of an execution context.</summary>
    internal OperatorContext NewContext() => new()
    {
        Settings = _settings,
        Catalog = _catalog,
        Sources = _sources,
        PlanDigest = Plan.PlanDigest,
    };

    /// <summary>
    /// The vectorized path, driven through the synchronous pull: <c>TryNext</c> first, and
    /// <c>WaitAsync</c> only when the root says it is not ready — which an in-process pipeline
    /// never does. Awaiting an incomplete <see cref="ValueTask"/> is the one place the async
    /// machinery allocates, and this is where it is confined.
    /// </summary>
    private async IAsyncEnumerable<RecordBatch> Vectorised(
        ScalarValue[] parameters,
        ExecutionStats stats,
        IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyList<object?>>>? relations,
        ExecutionArena arena,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var tree = Rent();
        tree.Context.Begin(
            stats, arena, parameters, _settings.TimeProvider.GetUtcNow(), relations);

        // Points the tree at this execution's token *before* the first pull. `TryNext` comes first
        // by design and creates the body's enumerator, so an operator that genuinely blocks on
        // its first pull — a remote fetch, a fan-out — would otherwise run with `None` and never see
        // a cancellation at all. M1-M4 never noticed: every operator's first pull was synchronous
        // and `WaitAsync` had set the token by the time one of them waited (ADR 0022).
        _ = tree.Root.ExecuteAsync(ct);

        var started = _settings.TimeProvider.GetTimestamp();
        var completed = false;
        try
        {
            while (true)
            {
                if (!tree.Root.TryNext(out var batch))
                {
                    if (tree.Root.IsDone)
                    {
                        break;
                    }

                    if (!await tree.Root.WaitAsync(ct).ConfigureAwait(false))
                    {
                        break;
                    }

                    batch = tree.Root.Current;
                }

                ct.ThrowIfCancellationRequested();
                var arrow = tree.Output.Materialise(batch!);
                stats.AddRowsProduced(arrow.Length);
                stats.AddBatchesProduced(1);
                yield return arrow;
            }

            completed = true;
        }
        finally
        {
            stats.Elapsed = _settings.TimeProvider.GetElapsedTime(started);
            await tree.Root.DisposeAsync().ConfigureAwait(false);
            tree.Context.End();

            // Only a tree that ran to the end is known to be in a state the next execution can use.
            if (completed)
            {
                Return(tree);
            }
        }
    }

    /// <summary>
    /// A warm tree if one is spare, else a fresh one. ADR 0019 re-measured what D64 asked about: with
    /// the plan-shaped scratch moved into the arena a fresh tree costs 10 272 bytes for the gate's
    /// three-operator pipeline — 0.10 bytes per row on the 100 800-row benchmark — which is on the
    /// wrong side of the tenth of a byte D64 set as the line, and over §5's 4 096-byte fixed budget.
    /// So the slot stays; what it holds is now objects only, because every buffer behind them is the
    /// arena's for the length of one execution.
    /// </summary>
    private ExecutionTree Rent() => Interlocked.Exchange(ref _spare, null) ?? Build();

    private void Return(ExecutionTree tree) => Volatile.Write(ref _spare, tree);

    /// <summary>
    /// A fresh operator tree. Everything plan-shaped — each expression node's scratch and each
    /// operator's copiers — rents from the arena at <see cref="OperatorContext.Begin"/> and returns
    /// at <see cref="OperatorContext.End"/>, so a tree holds objects and nothing else.
    /// </summary>
    private ExecutionTree Build()
    {
        var context = NewContext();
        IBatchOperator root;
        OutputMaterialiser output;
        using (new ScratchScope(context))
        {
            root = _root(context);
            output = new OutputMaterialiser(context, OutputSchema, _outputTypes, _settings.PooledOutput);
        }

        return new ExecutionTree(root, output, context);
    }

    /// <summary>One operator tree and the context it reads its per-execution state from.</summary>
    private sealed class ExecutionTree
    {
        public ExecutionTree(IBatchOperator root, OutputMaterialiser output, OperatorContext context)
        {
            Root = root;
            Output = output;
            Context = context;
        }

        public IBatchOperator Root { get; }

        public OutputMaterialiser Output { get; }

        public OperatorContext Context { get; }
    }
}

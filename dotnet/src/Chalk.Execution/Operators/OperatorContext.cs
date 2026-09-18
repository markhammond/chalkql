using Apache.Arrow.Memory;
using Chalk.Execution.Expressions;
using Chalk.Execution.Vectors;
using Chalk.Sources;
using CatalogContext = Chalk.Catalog.CatalogContext;

namespace Chalk.Execution.Operators;

/// <summary>
/// Everything an operator tree needs: what the plan says, which never changes, and what one execution
/// brings — the counters, the arena its memory comes from, the bound parameters and the clock.
/// </summary>
/// <remarks>
/// One context per execution since step 20 (D64): the warm operator-tree slot of ADR 0011 §3 is
/// gone, because the plan-shaped scratch that made a fresh tree expensive now rents from
/// <see cref="Arena"/> here — acquired in <see cref="Begin"/>, released in <see cref="End"/> — and a
/// fresh tree is only its node objects. Data-shaped arrays are still rented at the top of an
/// operator's run and returned in its <c>finally</c> (ADR 0012).
/// </remarks>
internal sealed class OperatorContext
{
    public required ExecutionSettings Settings { get; init; }

    public required CatalogContext Catalog { get; init; }

    public required IReadOnlyDictionary<string, ISourceRuntime> Sources { get; init; }

    /// <summary>Carried into every <c>ExecutionException</c> so a failure names the plan (§6.7).</summary>
    public required ulong PlanDigest { get; init; }

    public ExecutionStats Stats { get; private set; } = new();

    /// <summary>
    /// This execution's memory: Arrow buffers through <see cref="Allocator"/>, scratch through
    /// <c>Rent</c> / <c>Return</c>. Only valid between <see cref="Begin"/> and the end of the run.
    /// </summary>
    public ExecutionArena Arena => _arena
        ?? throw new InvalidOperationException("no execution is running on this operator tree.");

    /// <summary>Where sources and operators build Arrow buffers. The arena's, always.</summary>
    public MemoryAllocator Allocator => _arena?.Allocator ?? ManagedMemoryAllocatorFallback;

    public IReadOnlyList<ScalarValue> Parameters { get; private set; } = [];

    /// <summary>
    /// The relations this execution bound by name, for a <c>ContextTable</c> to materialise from
    /// (16-entitlements.md §2, §4). Empty for every statement that binds no context, which is every
    /// statement over a catalog without entitlements.
    /// </summary>
    private IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyList<object?>>> _relations =
        NoRelations;

    /// <summary>The rows bound under <paramref name="name"/>, or null when nothing was.</summary>
    public IReadOnlyList<IReadOnlyList<object?>>? ContextRelation(string name) =>
        _relations.TryGetValue(name, out var rows) ? rows : null;

    internal static readonly IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyList<object?>>>
        NoRelations = new Dictionary<string, IReadOnlyList<IReadOnlyList<object?>>>(0, StringComparer.Ordinal);

    /// <summary>Read once per execution so two rows never disagree about "now" (§6).</summary>
    public DateTimeOffset Now { get; private set; }

    /// <summary>
    /// Bumped by every <see cref="Begin"/>. An expression that caches something derived from the
    /// parameters — the IN-list set — rebuilds when it sees a new generation.
    /// </summary>
    public int Generation { get; private set; }

    /// <summary>
    /// The plan-shaped scratch this tree holds — every expression node's <c>VectorScratch</c> and
    /// every operator's <c>ColumnCopier</c> — so it can be pointed at the execution's arena and
    /// released with it (D64).
    /// </summary>
    public void RegisterScratch(Vectors.IArenaScratch scratch) => _scratch.Add(scratch);

    /// <summary>Points the tree at the next execution.</summary>
    public void Begin(
        ExecutionStats stats,
        ExecutionArena arena,
        IReadOnlyList<ScalarValue> parameters,
        DateTimeOffset now,
        IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyList<object?>>>? relations = null)
    {
        Stats = stats;
        _arena = arena;
        Parameters = parameters;
        Now = now;
        _relations = relations ?? NoRelations;
        Generation++;
        foreach (var scratch in _scratch)
        {
            scratch.Acquire(arena);
        }
    }

    /// <summary>
    /// Detaches the tree from the execution's arena, so a stale rental cannot be made against it, and
    /// hands back everything the plan-shaped scratch rented (D64).
    /// </summary>
    public void End()
    {
        foreach (var scratch in _scratch)
        {
            scratch.Release();
        }

        _arena = null;
    }

    /// <summary>
    /// How many remote streams this execution may have open, per source and overall (D107). Built
    /// once per tree from the settings, because both ceilings are per execution rather than per
    /// operator: a fan-out and a lookup join running at the same time share them.
    /// </summary>
    public RemoteGate RemoteGate
    {
        get
        {
            // Not `??=`: a fan-out asks for this from several branch tasks at once, and two gates
            // would be two sets of permits — which is no ceiling at all.
            lock (_remoteGateLock)
            {
                return _remoteGate ??= new RemoteGate(
                    Settings.MaxRemoteConcurrency, Settings.MaxConcurrentQueries);
            }
        }
    }

    private readonly Lock _remoteGateLock = new();
    private RemoteGate? _remoteGate;

    /// <summary>A private evaluation context for one operator's expressions.</summary>
    public EvalContext NewEvalContext() => new(this);

    private readonly List<Vectors.IArenaScratch> _scratch = [];

    private ExecutionArena? _arena;

    private static readonly MemoryAllocator ManagedMemoryAllocatorFallback =
        Memory.ManagedMemoryAllocator.Instance;
}

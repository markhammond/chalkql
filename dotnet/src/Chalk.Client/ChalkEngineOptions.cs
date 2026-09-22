using Chalk.Catalog;
using Chalk.Sources;
using Microsoft.Extensions.Logging;

namespace Chalk.Client;

/// <summary>Which engine runs a query. <c>Reference</c> is invariant I4 as a runtime switch.</summary>
public enum ExecutionEngine
{
    /// <summary>The batch-at-a-time engine. What production uses.</summary>
    Vectorised,

    /// <summary>
    /// The independent row-at-a-time interpreter. Slower and obviously correct; useful when a
    /// wrong result is suspected in production, and the oracle the whole test suite hangs off.
    /// </summary>
    Reference,
}

/// <summary>
/// Which window operator a plan is compiled onto (D258.4,
/// <c>docs/design/13-window-functions.md</c> §4.1).
/// </summary>
public enum WindowExecution
{
    /// <summary>
    /// The frame's shape decides: one that ends at or before the current row, over calls that can all
    /// be answered from the rows still in hand, streams; everything else buffers. What production
    /// uses.
    /// </summary>
    Automatic,

    /// <summary>
    /// Every window reads its whole input first, as it did before the streaming path existed. The
    /// escape hatch when a streamed answer is in doubt, and how the test suite runs the corpus down
    /// both paths.
    /// </summary>
    Buffered,
}

/// <summary>Who owns the memory behind an output batch.</summary>
public enum OutputMemory
{
    /// <summary>Managed arrays: a host may keep a batch for as long as it likes. The default.</summary>
    Managed,

    /// <summary>Pooled buffers: cheaper, but the host must dispose every batch promptly.</summary>
    Pooled,
}

/// <summary>
/// What the Arrow batches a host receives look like (D244). One property per type family, so a
/// later decision about BINARY adds a property here rather than reinterpreting this one.
/// </summary>
public sealed class OutputOptions
{
    /// <summary>
    /// The Arrow layouts this host accepts for STRING columns. Views only by default — the
    /// executor's own representation, and the cheapest thing to hand over.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Whatever this says, a prepared query's <c>OutputSchema</c> names one layout per STRING column
    /// and every batch carries an array of exactly that type. Naming both
    /// (<see cref="StringLayouts.Any"/>) leaves the choice to the engine, which resolves it per
    /// column at prepare time and then never varies it.
    /// </para>
    /// <para>
    /// <see cref="StringLayouts.Utf8"/> is for a host that wants the classic <c>StringArray</c> —
    /// because it pattern-matches on it, or because whatever it hands the batch to does. It costs
    /// the one conversion this decision permits, paid by the host that asked for it.
    /// </para>
    /// </remarks>
    public StringLayouts Strings { get; init; } = StringLayouts.Utf8View;
}

/// <summary>Execution knobs. Tuning constants are configuration, never constants in operator code.</summary>
public sealed class ExecutionOptions
{
    /// <summary>Upper bound on rows per batch.</summary>
    public int BatchSize { get; init; } = 4096;

    public ExecutionEngine Engine { get; init; } = ExecutionEngine.Vectorised;

    /// <summary>
    /// Which window operator a plan is compiled onto (D258.4). <see cref="WindowExecution.Automatic"/>
    /// unless a workload has a reason to pin it.
    /// </summary>
    public WindowExecution WindowExecution { get; init; } = WindowExecution.Automatic;

    public OutputMemory OutputMemory { get; init; } = OutputMemory.Managed;

    /// <summary>
    /// How the arenas the engine pools are sized and bounded
    /// (<c>docs/design/08-execution-arena.md</c> §2.1). Ignored for an execution the host supplies its
    /// own arena for.
    /// </summary>
    public ArenaOptions Arena { get; init; } = new();

    /// <summary>
    /// How many arenas the engine keeps warm. Executions beyond this get a transient arena, which is
    /// correct but starts cold. The engine's worst-case retained memory is
    /// <c>ArenaPoolSize × Arena.RetainBytes</c>, which it says out loud when it starts.
    /// </summary>
    public int ArenaPoolSize { get; init; } = Environment.ProcessorCount;

    /// <summary>The clock <c>CURRENT_TIMESTAMP</c> reads, injectable so tests are not time-dependent.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>
    /// How often to run <see cref="ChalkEngine.RefreshCatalogAsync"/> by itself (D86). Null — the
    /// default — never refreshes, and the host calls it when it knows a schema changed.
    /// </summary>
    /// <remarks>
    /// A cadence is the right answer when nothing tells the host a table changed, and the wrong one
    /// when something does: every refresh bumps the epoch, and every prepared query from the epoch
    /// before it must be prepared again. A refresh that throws is logged and the old catalog is
    /// kept, because an unreachable database should not take the engine's catalog with it.
    /// </remarks>
    public TimeSpan? CatalogRefreshInterval { get; init; }

    /// <summary>
    /// Per-source timeouts and limits, by source id (D86). A source named here overrides whatever
    /// its own builder declared; one that is not gets what its builder said, or
    /// <see cref="Chalk.Sources.SourceOptions.Default"/>.
    /// </summary>
    /// <remarks>
    /// Both places exist because they answer to different people. The adapter author knows what the
    /// source can bear and sets it on the builder; the host running this particular engine knows
    /// what this particular query workload can wait for, and says so here without rebuilding a
    /// source it did not write.
    /// </remarks>
    public IReadOnlyDictionary<string, Chalk.Sources.SourceOptions> SourceOptions { get; init; } =
        new Dictionary<string, Chalk.Sources.SourceOptions>(StringComparer.Ordinal);

    /// <summary>
    /// The fraction of a batch's rows a filter must keep before it forwards a selection vector rather
    /// than compacting (<c>docs/design/15-zero-allocation-execution.md</c> §2, D62). Zero always
    /// forwards, one always compacts. Below the threshold the operator gathers the survivors, which
    /// costs one copy and saves the work everything downstream would do over the rows that were
    /// filtered away; above it the copy is the worse deal. 0.5 unless a workload says otherwise.
    /// </summary>
    public double SelectionCompactionThreshold { get; init; } = 0.5;

    /// <summary>
    /// Stamp every column of every batch with its producer's generation and check it on access
    /// (D61). A batch is valid until the next one; this turns a reference kept past that from a
    /// silently wrong answer into a <c>BatchLifetimeException</c>. On by default in Debug builds, and
    /// worth turning on in Release while hunting a wrong answer that only appears under load.
    /// </summary>
    public bool ValidateBatchLifetimes { get; init; } = DebugBuild;

    /// <summary>
    /// How many Arrow batches a remote stream may run ahead of the pipeline (D107, M5). Two by
    /// default: enough that a source is fetching the next batch while the engine is working on the
    /// last, and small enough that a fast source cannot pull an unbounded amount of a slow query's
    /// result into memory. Zero turns prefetching off, and the fetch happens on the pipeline's own
    /// thread exactly as it did before M5.
    /// </summary>
    public int RemotePrefetchDepth { get; init; } = 2;

    /// <summary>
    /// The most remote queries this engine runs at once, across every source (D107). Per-source
    /// limits are <see cref="Chalk.Sources.SourceOptions.MaxConcurrentQueries"/>; this is the
    /// ceiling over all of them, so a fan-out over eight partitions on a two-core box does not open
    /// eight connections. Zero means unlimited.
    /// </summary>
    public int MaxRemoteConcurrency { get; init; } = Environment.ProcessorCount;

    /// <summary>
    /// How long an execution waits for its in-flight fetches to notice they were cancelled before it
    /// gives up on them and returns (D108, D98). Five seconds by default.
    /// </summary>
    /// <remarks>
    /// A bound, not a benchmark: no test asserts how long cancellation <em>takes</em>, only that it
    /// completes within this. A task that has not observed the token when the period expires is
    /// abandoned and logged with the source it was reading — abandoned rather than aborted, because
    /// there is no safe way to stop a thread inside a driver, and left logged rather than silent,
    /// because a source that never notices a cancellation is a bug in that source.
    /// </remarks>
    public TimeSpan CancellationGracePeriod { get; init; } = TimeSpan.FromSeconds(5);

    private const bool DebugBuild =
#if DEBUG
        true;
#else
        false;
#endif
}

/// <summary>Everything a host supplies to create an engine.</summary>
public sealed class ChalkEngineOptions
{
    /// <summary>
    /// Names this catalog to the planner. Plans carry it and are refused against another.
    /// </summary>
    /// <remarks>
    /// Optional since D271 (a): an engine that is given no name mints an instance ULID for itself,
    /// unique across engines and across restarts, and the host never sees it unless it logs it. What
    /// a name is still for is <em>the same id in two processes</em> — a recorded corpus, a test that
    /// asserts on a plan's context id, a golden that must be byte-identical to the one before it —
    /// and that is the only reason to set one.
    /// </remarks>
    public string? ContextId { get; init; }

    public required IReadOnlyList<ISourceRuntime> Sources { get; init; }

    public required IQueryPlanner Planner { get; init; }

    public ExecutionOptions Execution { get; init; } = new();

    /// <summary>What the batches this engine produces look like (D244).</summary>
    public OutputOptions Output { get; init; } = new();

    public ILoggerFactory? LoggerFactory { get; init; }

    /// <summary>
    /// The client-bodied function implementations this engine serves (D79,
    /// <c>docs/design/17-user-defined-functions.md</c> §3). Called once during
    /// <c>ChalkEngine.CreateAsync</c> with a registry to fill in; every client-bodied function the
    /// catalog declares must be registered by the time it returns, or engine creation fails naming
    /// the function.
    /// </summary>
    public Action<IFunctionRegistry>? Functions { get; init; }

    /// <summary>
    /// What decides how a join across two sources is planned (D104, M5). Called at engine creation
    /// and again on every catalog refresh, and given the catalog it is about to describe. Null — the
    /// default — uses the shipped policy: adaptive everywhere, which measures rather than assumes.
    /// </summary>
    /// <remarks>
    /// This is rev 3's replaceable policy, and replacing it needs no fork: a host implements the
    /// interface, returns a descriptor, and the planner reads data. The engine never calls back into
    /// it while planning.
    /// </remarks>
    public ICrossSourceJoinPolicy? JoinPolicy { get; init; }

    /// <summary>
    /// Associations between columns of two tables of two sources, which no foreign key can state
    /// (D270 (c), <c>docs/design/45-typed-tenancy-surface.md</c> §3).
    /// </summary>
    /// <remarks>
    /// A source describes its own schema, so an association between two of them is the host's to
    /// declare: it is stated here, travels with the catalog, and survives a refresh, which a claim
    /// belonging to neither source could not do otherwise. Empty — the default — adds no bytes.
    /// </remarks>
    public IReadOnlyList<AssociationDescriptor> Associations { get; init; } = [];

    /// <summary>
    /// What may end a search early for every statement this engine prepares (D234). The default
    /// runs every search to completion; a prepare that sets <see cref="PrepareOptions.Planning"/>
    /// overrides this one whole.
    /// </summary>
    public PlanningOptions Planning { get; init; } = PlanningOptions.None;

    /// <summary>
    /// How this engine redacts a statement's literals so the text can be logged (D262,
    /// <c>docs/design/37-redacted-sql.md</c>). The default redacts nothing: an engine that never
    /// asks generates no salt, sends no redaction message and costs nothing at all.
    /// </summary>
    public RedactionOptions Redaction { get; init; } = new();
}

/// <summary>Per-statement planning options.</summary>
public sealed class PrepareOptions
{
    public PushdownLevel Pushdown { get; init; } = PushdownLevel.Full;

    /// <summary>Ask the planner for its plan text and the rules that fired. Diagnostics only.</summary>
    public bool IncludePlanText { get; init; }

    /// <summary>
    /// Ask the planner for this statement's literals as keyed pseudonyms, so the text can be logged
    /// (D262). Null — the default — uses <see cref="ChalkEngineOptions.Redaction"/>, whose own
    /// default is off.
    /// </summary>
    /// <remarks>
    /// Off, <see cref="PreparedQuery.RedactedSql"/> is null, the request carries no redaction
    /// message and the sidecar runs no visitor. On, it governs the redacted plan text and the
    /// redacted quoted query of a source failure too, so no log line about this statement is half
    /// safe.
    /// </remarks>
    public bool? IncludeRedactedSql { get; init; }

    /// <summary>Declare the parameter types instead of letting the planner infer them.</summary>
    public IReadOnlyList<ChalkType>? ParameterTypes { get; init; }

    /// <summary>
    /// What this statement's parameters are expected to be worth, so the planner can estimate a
    /// predicate against one instead of guessing at it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A list by ordinal, a dictionary or an anonymous object or POCO by name — the containers an
    /// execution binds values from — with one convention of its own: it is <b>sparse</b>, so
    /// <c>null</c> means "nothing said about this parameter" and <see cref="DBNull.Value"/> means
    /// "I expect SQL NULL here". At execution <c>null</c> binds SQL NULL; the two APIs read it
    /// differently because a planning API needs three states where a binding API needs two.
    /// </para>
    /// <para>
    /// A hint informs an estimate and never a truth. It changes which plan is chosen and nothing
    /// else: the same rows come back whatever is hinted, and the values bound at execution need not
    /// resemble the hints at all. A name the statement does not have is refused rather than ignored,
    /// so a misspelt hint cannot silently become no hint; so is a list, because the plan's shape
    /// depends on a list's length rather than on a representative value.
    /// </para>
    /// </remarks>
    public object? ParameterValueHints { get; init; }

    /// <summary>
    /// The SQL dialect this statement is written in (D34). <see cref="SqlConformance.Default"/> —
    /// standard SQL — unless a statement asks for another one.
    /// </summary>
    public SqlConformance Conformance { get; init; } = SqlConformance.Default;

    /// <summary>
    /// Which of Calcite's dialect function libraries this statement may call (D60). Empty — the
    /// default — is the standard operators alone; naming <see cref="SqlLibrary.Postgresql"/> is what
    /// makes <c>STRING_AGG</c> and <c>ARRAY_AGG</c> resolve.
    /// </summary>
    public IReadOnlyList<SqlLibrary> Libraries { get; init; } = [];

    /// <summary>
    /// Capabilities to plan as if no source had declared them (D87). Empty is the normal case:
    /// every descriptor is believed. See <see cref="DisabledCapability"/> for why the option exists.
    /// </summary>
    public IReadOnlyList<DisabledCapability> DisabledCapabilities { get; init; } = [];

    /// <summary>
    /// A cross-source join policy for this statement alone (D104, M5), merged over the catalog's:
    /// a field this one sets wins, a field it leaves at zero inherits, and its pair rules are
    /// consulted first. Null — the default — plans under the catalog's policy unchanged.
    /// </summary>
    /// <remarks>
    /// This is how a host says "not for this query": forbid lookups for one pair, cap what one
    /// report may pull into a local join, force a strategy while investigating a plan. It belongs in
    /// a plan-cache key, exactly as <see cref="Pushdown"/> and <see cref="Libraries"/> do.
    /// </remarks>
    public CrossSourceJoinPolicy? JoinPolicy { get; init; }

    /// <summary>
    /// The extensions this prepare attaches to the request, each a protobuf message the planner has
    /// a handler for (step 26c, D213). Empty — the default — is no bytes at all: a core client
    /// cannot construct an extension message, which is what makes zero cost structural. The
    /// entitlement decorator is what puts one here.
    /// </summary>
    public IReadOnlyList<Google.Protobuf.IMessage> Extensions { get; init; } = [];

    /// <summary>
    /// What may end this statement's optimiser search early (D234): a time budget, a convergence
    /// test, a stop token. Null — the default — uses
    /// <see cref="ChalkEngineOptions.Planning"/>, whose own default runs the search to completion.
    /// </summary>
    public PlanningOptions? Planning { get; init; }

    public bool RequireCurrentCatalog { get; init; } = true;
}

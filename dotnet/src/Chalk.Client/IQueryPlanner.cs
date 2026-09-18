using Chalk.Catalog;
using CatalogContext = Chalk.Catalog.CatalogContext;
using Chalk.Client.Rpc;
using Chalk.Ir;

namespace Chalk.Client;

/// <summary>
/// Planning is always out of process (invariant I1), and the transport is swappable (invariant I3):
/// production is gRPC to the sidecar, tests use a recorded-plan double so executor tests need no
/// sidecar running.
/// </summary>
public interface IQueryPlanner : IAsyncDisposable
{
    /// <summary>The planner's IR range, versions and config hash. Called once when an engine is created.</summary>
    ValueTask<PlannerInfo> GetInfoAsync(CancellationToken ct = default);

    /// <summary>
    /// Installs a whole catalog under its own context id and epoch, naming no version. The spelling
    /// a caller with nothing to version reaches for — a test, the corpus recorder — and the one every
    /// call site had before D271.
    /// </summary>
    ValueTask RegisterCatalogAsync(CatalogContext catalog, CancellationToken ct = default) =>
        RegisterCatalogAsync(new CatalogRegistration { Catalog = catalog }, ct);

    /// <summary>
    /// Installs one <em>shape version</em> of one engine instance's catalog (D271 (b),
    /// <c>docs/design/44-catalog-registration.md</c> §2), whole or as a delta over a version the
    /// planner still holds. Idempotent for a version already installed.
    /// </summary>
    ValueTask RegisterCatalogAsync(CatalogRegistration registration, CancellationToken ct = default);

    /// <summary>
    /// Installs one <em>statistics version</em> (D271 (c), §3): the tables whose numbers moved, and
    /// nothing else. Called only when a refresh moved something, so a steady state sends nothing.
    /// </summary>
    /// <remarks>
    /// Default-implemented as "this transport has nowhere to put them", because a transport that
    /// serves recorded plans has no cost model to feed: what a recorded plan was planned with is
    /// already in the recording.
    /// </remarks>
    ValueTask RegisterStatisticsAsync(
        StatisticsRegistration statistics, CancellationToken ct = default) =>
        ValueTask.CompletedTask;

    /// <summary>
    /// Whether this transport keeps versions to apply a delta to (D271 (d)). True for a planner with
    /// a registry behind it, which is every real one; false for a transport that only ever holds the
    /// catalog it was last handed — a directory of recorded plans — where a delta would install the
    /// changed tables and lose the rest.
    /// </summary>
    bool AcceptsCatalogDeltas => true;

    /// <summary>Plans one statement against the currently registered catalog.</summary>
    ValueTask<PlanResult> PlanAsync(PlanRequest request, CancellationToken ct = default);

    /// <summary>
    /// Asks the in-flight planning named by <paramref name="requestId"/> for the best complete plan
    /// it has (D236); false when nothing is in flight under that id, which means it has finished.
    /// </summary>
    /// <remarks>
    /// Default-implemented so a transport with no such notion — a recorded planner, say — needs no
    /// code: a plan that is already on disk cannot be stopped, and saying "not in flight" is the
    /// honest answer.
    /// </remarks>
    ValueTask<bool> StopPlanningAsync(string requestId, CancellationToken ct = default) =>
        ValueTask.FromResult(false);

    /// <summary>
    /// Redacts one statement's literals so the text can be logged (D262), for text that was never
    /// prepared — above all text that does not parse.
    /// </summary>
    /// <remarks>
    /// Default-implemented as "this transport cannot", because redacting needs a parser and only a
    /// real planner has one: a directory of recorded plans can serve what was recorded with them
    /// and nothing else, and saying so is better than inventing an answer.
    /// </remarks>
    ValueTask<RedactedSql> RedactSqlAsync(RedactSqlRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException(
            $"{GetType().Name} cannot redact a statement: redaction needs the planner's own parser "
            + "(D262, docs/design/37-redacted-sql.md §1).");
}

/// <summary>
/// One catalog version to install (D271 (b), (d)). A registration that names no version installs the
/// catalog under its own context id and epoch, which is what a caller with nothing to version means.
/// </summary>
public sealed class CatalogRegistration
{
    /// <summary>The catalog itself, or — under <see cref="BaseShapeVersion"/> — the changed tables.</summary>
    public required CatalogContext Catalog { get; init; }

    /// <summary>
    /// The engine instance this catalog belongs to. Empty falls back to the catalog's own context id.
    /// </summary>
    public string InstanceId { get; init; } = string.Empty;

    /// <summary>
    /// The version this registration installs. Empty falls back to the catalog's epoch, which is what
    /// a caller that names no version installs under.
    /// </summary>
    public string ShapeVersion { get; init; } = string.Empty;

    /// <summary>
    /// The version <see cref="Catalog"/>'s tables are applied <em>to</em>, making this a delta (§4).
    /// Empty — the default — is a full registration, and is also what a client falls back to when a
    /// delta is refused because the planner no longer holds the base.
    /// </summary>
    public string BaseShapeVersion { get; init; } = string.Empty;

    /// <summary>Tables the delta removes, as <c>schema.table</c>. Meaningless without a base.</summary>
    public IReadOnlyList<string> RemovedTables { get; init; } = [];
}

/// <summary>
/// One statistics version to install (D271 (c), §3): the tables whose numbers moved, each carrying
/// its row count, the kind of that count, and its columns' statistics.
/// </summary>
public sealed class StatisticsRegistration
{
    public required string InstanceId { get; init; }

    public required string StatisticsVersion { get; init; }

    public required IReadOnlyList<TableStatisticsUpdate> Tables { get; init; }
}

/// <summary>One table's numbers, at one statistics version.</summary>
public sealed class TableStatisticsUpdate
{
    /// <summary>The schema's federation-level name, as the catalog spells it.</summary>
    public required string Schema { get; init; }

    public required string Table { get; init; }

    /// <summary>Exact or estimated. Negative is unknown, as in the table descriptor.</summary>
    public required long RowCount { get; init; }

    public required Chalk.Ir.RowCountKind RowCountKind { get; init; }

    /// <summary>
    /// The columns that have statistics, by name rather than by position so a shape change that adds
    /// or reorders columns cannot attach one column's numbers to another. A column left out has none.
    /// </summary>
    public required IReadOnlyList<ColumnStatisticsUpdate> Columns { get; init; }
}

/// <summary>One column's statistics, as the wire carries them.</summary>
public sealed class ColumnStatisticsUpdate
{
    public required string Column { get; init; }

    public required Chalk.Ir.ColumnStatistics Statistics { get; init; }
}

/// <summary>One statement to plan, against one catalog version.</summary>
public sealed class PlanRequest
{
    /// <summary>The SQL the planner sees — already rewritten to positional <c>?</c> parameters (D27).</summary>
    public required string Sql { get; init; }

    /// <summary>
    /// The catalog's own name, which is also the engine instance's when the host named neither
    /// (D271 (a)). What a recorded plan is filed under, and what a plan carries back.
    /// </summary>
    public required string ContextId { get; init; }

    /// <summary>
    /// The engine's local refresh counter, carried for the plan's own diagnostics. No longer checked
    /// by the planner: <see cref="ShapeVersion"/> is what a plan is keyed to (D271 (b)).
    /// </summary>
    public required long CatalogEpoch { get; init; }

    /// <summary>Superseded by <see cref="StatisticsVersion"/> (D271 (c)); never set.</summary>
    public long StatsEpoch { get; init; }

    /// <summary>
    /// The engine instance this statement is planned for (D271 (a)). Empty falls back to
    /// <see cref="ContextId"/>.
    /// </summary>
    public string InstanceId { get; init; } = string.Empty;

    /// <summary>
    /// The shape version this statement is planned against (D271 (b)). One definite version and never
    /// a range: the plan's digest, every entitled read's descriptor hash and D260's staleness are all
    /// keyed to it. A version the planner does not hold is answered by name, and the client registers
    /// it and retries once. Empty asks for the newest the planner holds.
    /// </summary>
    public string ShapeVersion { get; init; } = string.Empty;

    /// <summary>
    /// The statistics version the client knows it has published (D271 (c)). Advisory: stale
    /// statistics change costs and never answers, so the planner plans with the newest it holds.
    /// </summary>
    public string StatisticsVersion { get; init; } = string.Empty;

    /// <summary>What the client can read. The planner declines to use nodes newer than this.</summary>
    public uint ClientIrVersion { get; init; } = IrVersion.Current;

    /// <summary>Optional. When empty the planner infers the types and reports them on the plan.</summary>
    public IReadOnlyList<ChalkType> ParameterTypes { get; init; } = [];

    public PlannerOptions Options { get; init; } = new();

    /// <summary>
    /// The execution context this statement is planned with (step 26, D152). Null under
    /// execute-time binding, and for every statement over a catalog without entitlements.
    /// </summary>
    public RequestContext? Context { get; init; }

    /// <summary>
    /// The digest of the plan this request's context is the <em>union</em> of, as a hint (D233).
    /// </summary>
    /// <remarks>
    /// Zero — the default — is every request that is not a narrowing. The context carries the whole
    /// union either way, so this cannot change the plan: it only tells the sidecar which plan's
    /// converted tree it may start from, and a sidecar that no longer holds one converts the
    /// statement again and produces the same plan.
    /// </remarks>
    public ulong NarrowFrom { get; init; }

    /// <summary>
    /// What may end this request's optimiser search early (D234). Null — the default — is a search
    /// that runs to completion, and sends no bytes for the option at all.
    /// </summary>
    public PlanningOptions? Planning { get; init; }

    /// <summary>
    /// Names this planning so <see cref="IQueryPlanner.StopPlanningAsync"/> can address it (D236).
    /// Empty — the default — is a planning nothing can stop by id.
    /// </summary>
    public string PlanningRequestId { get; init; } = string.Empty;

    /// <summary>
    /// Redact this statement's literals and carry the result on <see cref="PlanResult.RedactedSql"/>
    /// (D262). Null — the default, and every request that did not ask — sends no bytes for it, and
    /// the planner runs no visitor.
    /// </summary>
    public RedactionRequest? Redaction { get; init; }
}

/// <summary>How much freedom the planner has. <c>PUSHDOWN_LEVEL_NONE</c> is the I4 reference configuration.</summary>
public sealed class PlannerOptions
{
    public PushdownLevel Pushdown { get; init; } = PushdownLevel.Full;

    /// <summary>Ask for <c>RelOptUtil.toString</c> of both plans, and the list of rules that fired.</summary>
    public bool IncludePlanText { get; init; }

    /// <summary>M2+: plan as if no index existed. Plumbed end to end now; M1 ignores it.</summary>
    public bool DisableIndexLookup { get; init; }

    /// <summary>
    /// The SQL dialect this statement is written in (D34). A request option, like the pushdown
    /// level: it does not change <see cref="PlannerInfo.PlannerConfigHash"/>, and from M3 it is part
    /// of the plan-cache key alongside the pushdown level.
    /// </summary>
    public SqlConformance Conformance { get; init; } = SqlConformance.Default;

    /// <summary>
    /// Which of Calcite's dialect function libraries this statement may call (D60). Empty — the
    /// default — is the standard operators alone. Part of the plan-cache key, like the conformance
    /// level: two statements that differ only in their libraries can plan differently.
    /// </summary>
    public IReadOnlyList<SqlLibrary> Libraries { get; init; } = [];

    /// <summary>
    /// Capabilities the planner must ignore for this statement, whatever the catalog declares
    /// (D87). Part of the plan-cache key, like the level: the same SQL with a capability off is a
    /// different plan.
    /// </summary>
    public IReadOnlyList<DisabledCapability> DisabledCapabilities { get; init; } = [];

    /// <summary>
    /// A per-request cross-source join policy (D104, M5), merged over the catalog's field by field.
    /// Null plans under the catalog's unchanged. Like <see cref="Pushdown"/> it is a request option
    /// rather than part of the planner's config hash — but it does belong in a plan-cache key.
    /// </summary>
    public Chalk.Catalog.CrossSourceJoinPolicy? JoinPolicy { get; init; }

    /// <summary>
    /// The extensions this request carries, each a protobuf message the planner has a handler for
    /// (step 26c, D212). Empty — the default — is no bytes at all, and an extension the planner does
    /// not know is refused by the sidecar rather than ignored.
    /// </summary>
    public IReadOnlyList<Google.Protobuf.IMessage> Extensions { get; init; } = [];
}

/// <summary>What came back.</summary>
public sealed class PlanResult
{
    public required Plan Plan { get; init; }

    public ulong PlanDigest => Plan.PlanDigest;

    /// <summary>Diagnostics only; nothing parses it.</summary>
    public string? PlanText { get; init; }

    /// <summary>
    /// This statement's literals as keyed pseudonyms (D262), or null when the request asked for no
    /// redaction — which is every request that did not set <see cref="PlanRequest.Redaction"/>.
    /// </summary>
    public string? RedactedSql { get; init; }

    public PlanningStats Stats { get; init; } = new();

    /// <summary>How the optimiser ended, and what it cost (D237).</summary>
    public PlanningState PlanningState { get; init; } = PlanningState.Unknown;

    /// <summary>
    /// What the planner's extensions had to say, each a message addressed by its type URL (step 26c,
    /// D212). Empty for a plan no extension had anything to say about, which is what "no bytes when
    /// unused" means on this side of the call.
    /// </summary>
    public IReadOnlyList<Google.Protobuf.WellKnownTypes.Any> Extensions { get; init; } = [];
}

/// <summary>
/// One dialect preset the sidecar accepts as a source's <c>DialectProfile.Dialect</c> (D248, D249,
/// docs/design/31-dialect-discovery.md).
/// </summary>
public sealed class DialectInfo
{
    /// <summary>The preset name, lower case: <c>"sqlite"</c>, <c>"duckdb"</c>, <c>"oracle"</c>, …</summary>
    public required string Name { get; init; }

    /// <summary>Calcite's <c>SqlDialect.DatabaseProduct</c> enum name, e.g. <c>"POSTGRESQL"</c>; empty for <c>"ansi"</c>.</summary>
    public required string DatabaseProduct { get; init; }

    /// <summary>
    /// True for the four presets Chalk ships its own dialect subclass and a conformance-kit run
    /// for: <c>sqlite</c>, <c>duckdb</c>, <c>postgresql</c>, <c>ansi</c>. False for every other
    /// Calcite database product (D249) — the conformance kit, run by the host, is how it learns
    /// what holds for one of those.
    /// </summary>
    public required bool Tuned { get; init; }

    /// <summary>Other spellings the sidecar accepts for this same preset, e.g. <c>"postgres"</c>.</summary>
    public IReadOnlyList<string> Aliases { get; init; } = [];
}

/// <summary>The planner's identity. Part of the M3 plan-cache key.</summary>
public sealed class PlannerInfo
{
    public required uint MinIrVersion { get; init; }

    public required uint MaxIrVersion { get; init; }

    public required string PlannerVersion { get; init; }

    public required string CalciteVersion { get; init; }

    /// <summary>Hash of the rule set, cost model, Calcite version and IR version.</summary>
    public required uint PlannerConfigHash { get; init; }

    /// <summary>What this sidecar accepts as a source's dialect (D248, D249).</summary>
    public IReadOnlyList<DialectInfo> Dialects { get; init; } = [];

    /// <summary>Every <see cref="SqlConformance"/> level this sidecar's parser accepts (D248).</summary>
    public IReadOnlyList<SqlConformance> Conformances { get; init; } = [];

    /// <summary>Every <see cref="SqlLibrary"/> this sidecar can load (D248).</summary>
    public IReadOnlyList<SqlLibrary> Libraries { get; init; } = [];

    /// <summary>
    /// The planning scheduler's worker count in force (D243, D240): the sidecar's
    /// <c>--planning-workers</c>/<c>--planning-load-factor</c> arguments, resolved, or every
    /// available processor when neither was given.
    /// </summary>
    public uint PlanningWorkers { get; init; }

    /// <summary>True when this planner can serve a client speaking <paramref name="clientIrVersion"/>.</summary>
    public bool Serves(uint clientIrVersion) =>
        clientIrVersion >= MinIrVersion && clientIrVersion <= MaxIrVersion;
}

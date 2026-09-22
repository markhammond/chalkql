using System.Diagnostics;
using System.Runtime.CompilerServices;
using Apache.Arrow;
using Chalk.Catalog;
using Chalk.Client.Rpc;
using Chalk.Execution;
using Chalk.Ir;
using Chalk.Sources;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using CatalogContext = Chalk.Catalog.CatalogContext;

namespace Chalk.Client;

/// <summary>
/// SQL in, Arrow batches out. Assembles the catalog from its sources, hands statements to an
/// <see cref="IQueryPlanner"/>, and runs the plans it gets back.
/// </summary>
/// <remarks>
/// Thread-safe and reusable, as is every <see cref="PreparedQuery"/> it hands out; a
/// <see cref="QueryExecution"/> is single-use.
/// </remarks>
public sealed partial class ChalkEngine : IAsyncDisposable
{
    private readonly ChalkEngineOptions _options;
    private readonly Dictionary<string, ISourceRuntime> _sources;
    private readonly ExecutionSettings _settings;
    private readonly ArenaPool _arenas;
    private readonly ILogger _log;

    /// <summary>Stable identity handed to every source claim for this engine instance.</summary>
    private readonly object _identity;

    /// <summary>Each source's one-time sharing handshake, cached for the life of the engine.</summary>
    private readonly SourceRegistration[] _sourceRegistrations;
    private readonly Dictionary<string, SourceRegistration>? _sharedSourceRegistrationsById;
    private readonly SourceRegistration[] _sharedSourceRegistrations;
    private readonly SharedSourceState[] _orderedSharedSourceStates;

    /// <summary>
    /// Process-wide coordination exists only for sources that explicitly report Shared. Exclusive
    /// sources never enter this table, so their refresh path remains the engine-local path.
    /// </summary>
    private static readonly ConditionalWeakTable<ISourceRuntime, SharedSourceState> SharedSources = new();
    private static long _nextSharedSourceOrder;

    /// <summary>One refresh at a time; a second caller waits rather than racing the epoch.</summary>
    private readonly SemaphoreSlim _refreshing = new(1, 1);
    private readonly ITimer? _refreshTimer;

    private CatalogContext _catalog;
    private readonly Chalk.Sources.HostFunctionSet _functions;

    /// <summary>
    /// The shape of the catalog at <see cref="_shapeEpoch"/>, and the epoch that shape arrived at
    /// (D260 §2). Written only under the refresh lock; read through <see cref="ShapeEpoch"/>.
    /// </summary>
    private CatalogShape _shape;
    private long _shapeEpoch;

    /// <summary>
    /// The epoch at which each table's shape last changed (D271 (d)). Replaced wholesale under the
    /// refresh lock and never mutated in place, so a reader sees one consistent map; a table absent
    /// from it has never changed shape since the engine was created, and a table the catalog no
    /// longer declares is not in it at all — which <see cref="IsCurrent"/> reads as "stale".
    /// </summary>
    private IReadOnlyDictionary<TableKey, long> _tableShapeEpochs;

    /// <summary>
    /// The versions the engine mints and the wire keys on (D271 (a)). The instance is minted once and
    /// never changes; the shape version moves when some table's shape does and the statistics version
    /// when a refresh moves any table's numbers. The host never sees one unless it logs it.
    /// </summary>
    private readonly string _instanceId;
    private string _shapeVersion;
    private string _statisticsVersion;

    /// <summary>
    /// What this engine has actually registered on its planner connection (D271 (b)). A registration
    /// is sent only when a version is new here, and a plan answered with
    /// <see cref="PlanErrorKind.UnknownCatalogVersion"/> registers again and retries once. Guarded by
    /// <see cref="_registering"/> rather than by the refresh lock, because the recovery path runs
    /// from a prepare and must not wait behind a refresh it does not need.
    /// </summary>
    private readonly SemaphoreSlim _registering = new(1, 1);
    private string _registeredShapeVersion = string.Empty;
    private string _registeredStatisticsVersion = string.Empty;

    private ChalkEngine(
        ChalkEngineOptions options,
        CatalogContext catalog,
        Dictionary<string, ISourceRuntime> sources,
        PlannerInfo plannerInfo,
        Chalk.Sources.HostFunctionSet functions,
        CatalogVersionState versions,
        object identity,
        SourceRegistration[] sourceRegistrations,
        SharedSourceState[] orderedSharedSourceStates)
    {
        _options = options;
        _functions = functions;
        _sources = sources;
        _identity = identity;
        _sourceRegistrations = sourceRegistrations;
        _sharedSourceRegistrations = sourceRegistrations.Where(r => r.Shared is not null).ToArray();
        _sharedSourceRegistrationsById = _sharedSourceRegistrations.Length == 0
            ? null
            : _sharedSourceRegistrations.ToDictionary(r => r.Source.SourceId, StringComparer.Ordinal);
        
        _orderedSharedSourceStates = sourceRegistrations
            .Where(static registration => registration.Shared is not null)
            .Select(static registration => registration.Shared!)
            .Distinct<SharedSourceState>(ReferenceEqualityComparer.Instance)
            .OrderBy(static state => state.Order)
            .ToArray();
        
        _catalog = catalog;
        _shape = versions.Shape;
        _shapeEpoch = catalog.Epoch;
        _tableShapeEpochs = new Dictionary<TableKey, long>();
        _instanceId = versions.InstanceId;
        _shapeVersion = versions.ShapeVersion;
        _statisticsVersion = versions.StatisticsVersion;
        _registeredShapeVersion = versions.ShapeVersion;
        _registeredStatisticsVersion = versions.StatisticsVersion;
        PlannerInfo = plannerInfo;
        _log = (options.LoggerFactory ?? NullLoggerFactory.Instance).CreateLogger<ChalkEngine>();
        _settings = new ExecutionSettings
        {
            BatchSize = options.Execution.BatchSize,
            UseReferenceEngine = options.Execution.Engine == ExecutionEngine.Reference,
            ForceBufferedWindows =
                options.Execution.WindowExecution == WindowExecution.Buffered,
            PooledOutput = options.Execution.OutputMemory == OutputMemory.Pooled,
            OutputStrings = options.Output.Strings,
            SelectionCompactionThreshold = options.Execution.SelectionCompactionThreshold,
            ValidateBatchLifetimes = options.Execution.ValidateBatchLifetimes,
            TimeProvider = options.Execution.TimeProvider,
            SourceOptions = options.Execution.SourceOptions,
            RemotePrefetchDepth = options.Execution.RemotePrefetchDepth,
            MaxRemoteConcurrency = options.Execution.MaxRemoteConcurrency,
            CancellationGracePeriod = options.Execution.CancellationGracePeriod,
            Functions = functions,
        };
        _arenas = new ArenaPool(options.Execution.Arena, options.Execution.ArenaPoolSize);

        var arenaPoolSize = _arenas.Capacity;
        var retainBytes = options.Execution.Arena.RetainBytes;
        var worstCaseBytes = _arenas.WorstCaseBytes;
        var maxBytes = options.Execution.Arena.MaxBytes;

        LogChalkEngineReady(
            sources.Count,
            options.Execution.BatchSize,
            options.Execution.Engine,
            options.Execution.OutputMemory,
            arenaPoolSize,
            retainBytes,
            worstCaseBytes,
            maxBytes is { } max
                ? $"a budget of {max} bytes"
                : "no budget");
        
        if (options.Execution.CatalogRefreshInterval is { } interval)
        {
            if (interval <= TimeSpan.Zero)
            {
                throw new ArgumentException(
                    "CatalogRefreshInterval must be positive", nameof(options));
            }

            _refreshTimer = options.Execution.TimeProvider.CreateTimer(
                static state => ((ChalkEngine)state!).RefreshOnSchedule(), this, interval, interval);
        }
    }

    /// <summary>
    /// The catalog assembled from the sources, as of the current epoch. Replaced wholesale by
    /// <see cref="RefreshCatalogAsync"/>; a plan is only ever valid for the epoch it was planned against.
    /// </summary>
    public CatalogContext Catalog => Volatile.Read(ref _catalog);

    /// <summary>
    /// The epoch at which the catalog's <em>shape</em> last changed (D260 §2). A plan is stale when
    /// it was planned before this, and current otherwise — so a refresh that only moved rows or
    /// statistics leaves every prepared query valid, and one that added a column strands them as it
    /// always has (D86).
    /// </summary>
    public long ShapeEpoch => Volatile.Read(ref _shapeEpoch);

    /// <summary>What the planner said about itself when the engine was created.</summary>
    public PlannerInfo PlannerInfo { get; }

    /// <summary>
    /// Describes every source, assembles and validates the catalog, checks the planner speaks a
    /// compatible IR version, and registers the catalog. Any failure means no engine.
    /// </summary>
    public static async ValueTask<ChalkEngine> CreateAsync(
        ChalkEngineOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Sources.Count == 0)
        {
            throw new ArgumentException("an engine needs at least one source", nameof(options));
        }

        if (options.Execution.BatchSize < 1)
        {
            throw new ArgumentException("BatchSize must be at least 1", nameof(options));
        }

        // D244: a host that accepts no layout accepts no STRING column, which is not a query this
        // engine could ever answer. Refused here rather than at the first prepare that has one.
        if (options.Output.Strings is not (
            Chalk.Sources.StringLayouts.Utf8
            or Chalk.Sources.StringLayouts.Utf8View
            or Chalk.Sources.StringLayouts.Any))
        {
            throw new ArgumentException(
                $"Output.Strings names no acceptable layout ({(int)options.Output.Strings}); "
                + "name Utf8, Utf8View, or both (Any)",
                nameof(options));
        }

        var identity = new object();
        var claimed = new List<SourceRegistration>(options.Sources.Count);

        try
        {
            var sources = new Dictionary<string, ISourceRuntime>(StringComparer.Ordinal);
            foreach (var source in options.Sources)
            {
                if (!sources.TryAdd(source.SourceId, source))
                {
                    throw new ArgumentException(
                        $"two sources share the id '{source.SourceId}'; source ids identify a schema's runtime "
                        + "and must be unique",
                        nameof(options));
                }

                claimed.Add(ClaimSource(source, identity));
            }

            var sourceRegistrations = claimed.ToArray();
            var sharedSourceStates = sourceRegistrations
                .Where(r => r.Shared is not null)
                .Select(r => r.Shared!)
                .OrderBy(s => s.Order)
                .ToArray();

            using var sharedLease = await AcquireSharedSourcesAsync(sharedSourceStates, ct).ConfigureAwait(false);

            var schemas = new List<SchemaDescriptor>(sourceRegistrations.Length);
            foreach (var registration in sourceRegistrations)
            {
                schemas.Add(registration.Source.DescribeSchema());
            }

            // The engine's instance version (D271 (a)): the host's own name for its catalog when it gave
            // one — a test, a corpus recording, anything that needs the same id in two processes — and a
            // minted ULID otherwise, unique across engines and across restarts.
            var instanceId = string.IsNullOrEmpty(options.ContextId)
                ? CatalogVersions.Mint()
                : options.ContextId;

            var catalog = WithJoinPolicy(
                new CatalogContext
                {
                    ContextId = instanceId,
                    Epoch = 1,
                    Schemas = schemas,
                    Associations = options.Associations,
                },
                options.JoinPolicy);
            CatalogValidator.Validate(catalog);

            var functions = HostFunctions.Build(options.Functions);
            HostFunctions.CheckCatalog(catalog, functions);

            var info = await options.Planner.GetInfoAsync(ct).ConfigureAwait(false);
            if (!info.Serves(IrVersion.Current))
            {
                throw new IrVersionMismatchException(
                    info.MaxIrVersion,
                    IrVersion.Current,
                    $"The planner serves IR versions {info.MinIrVersion}..{info.MaxIrVersion}.");
            }

            // D256: a source's dialect the planner does not recognise is refused at RegisterCatalog
            // anyway (the sidecar remains the authority — this is not a second, independent policy),
            // but a name a host mistyped is cheaper to report here, naming the source, before the round
            // trip. Only when GetInfo reported a real list (D248): a recorded planner's is empty (D250)
            // because it never asked a sidecar anything, and there is nothing honest to check against.
            if (info.Dialects.Count > 0)
            {
                foreach (var schema in catalog.Schemas)
                {
                    var dialect = schema.DialectProfile.Dialect;
                    if (!string.IsNullOrEmpty(dialect) && !DialectIsAccepted(dialect, info.Dialects))
                    {
                        throw new CatalogValidationException(
                            $"schemas ({schema.SourceId})",
                            $"dialect '{dialect}' is not one of the presets this planner accepts: "
                            + string.Join(", ", info.Dialects.Select(d => d.Name))
                            + ". Leave it empty for ANSI.");
                    }
                }
            }

            // Registered once, under the versions this engine mints — not once per prepare, as it was
            // before D271 (b). The shape goes first and the numbers follow on their own version; a plan
            // names both, and the planner joins them.
            var shape = CatalogShape.Of(catalog);
            var versions = new CatalogVersionState(
                instanceId, shape, CatalogVersions.Mint(), CatalogVersions.Mint());
            await options.Planner.RegisterCatalogAsync(
                new CatalogRegistration
                {
                    Catalog = catalog,
                    InstanceId = versions.InstanceId,
                    ShapeVersion = versions.ShapeVersion,
                },
                ct).ConfigureAwait(false);
            await options.Planner.RegisterStatisticsAsync(
                StatisticsFor(catalog, versions.InstanceId, versions.StatisticsVersion, tables: null),
                ct).ConfigureAwait(false);

            var engine = new ChalkEngine(
                options,
                catalog,
                sources,
                info,
                functions,
                versions,
                identity,
                sourceRegistrations,
                sharedSourceStates);
            engine.CaptureSharedRevisions();
            return engine;
        }
        catch
        {
            ReleaseClaims(claimed, identity);
            throw;
        }
    }
    
    private static async ValueTask<SharedSourceLease?> AcquireSharedSourcesAsync(
        IEnumerable<SharedSourceState> sourceStates,
        CancellationToken ct)
    {
        var states = sourceStates
            .Distinct<SharedSourceState>(ReferenceEqualityComparer.Instance)
            .OrderBy(static state => state.Order)
            .ToArray();

        if (states.Length == 0)
        {
            return null;
        }

        var acquired = 0;

        try
        {
#if DEBUG
            for (var i = 1; i < states.Length; i++)
            {
                Debug.Assert(states[i - 1].Order < states[i].Order);
            }
#endif
            
            for (; acquired < states.Length; acquired++)
            {
                await states[acquired]
                    .Refresh
                    .WaitAsync(ct)
                    .ConfigureAwait(false);
            }

            return new SharedSourceLease(states);
        }
        catch
        {
            for (var i = acquired - 1; i >= 0; i--)
            {
                states[i].Refresh.Release();
            }

            throw;
        }
    }
    

    /// <summary>The versions an engine is created with, so the constructor takes one argument.</summary>
    private readonly record struct CatalogVersionState(
        string InstanceId, CatalogShape Shape, string ShapeVersion, string StatisticsVersion);

    private sealed class SourceRegistration
    {
        private long _observedRevision;
        private long _observedShapeRevision;

        internal SourceRegistration(ISourceRuntime source, SharedSourceState? shared)
        {
            Source = source;
            Shared = shared;
        }

        internal ISourceRuntime Source { get; }
        internal SharedSourceState? Shared { get; }
        internal long ObservedRevision => Volatile.Read(ref _observedRevision);
        internal long ObservedShapeRevision => Volatile.Read(ref _observedShapeRevision);

        internal void Observe()
        {
            if (Shared is not { } shared)
            {
                return;
            }

            Volatile.Write(ref _observedShapeRevision, shared.ShapeRevision);
            Volatile.Write(ref _observedRevision, shared.Revision);
        }
    }

    private sealed class SharedSourceState(long order)
    {
        private long _revision;
        private long _shapeRevision;

        internal long Order { get; } = order;
        internal SemaphoreSlim Refresh { get; } = new(1, 1);
        internal long Revision => Volatile.Read(ref _revision);
        internal long ShapeRevision => Volatile.Read(ref _shapeRevision);

        internal void Changed(bool shapeMayHaveChanged)
        {
            if (shapeMayHaveChanged)
            {
                Interlocked.Increment(ref _shapeRevision);
            }

            Interlocked.Increment(ref _revision);
        }
    }

    private sealed class SharedSourceLease : IDisposable
    {
        private SharedSourceState[]? _states;

        internal SharedSourceLease(SharedSourceState[] states)
        {
            _states = states;
        }

        public void Dispose()
        {
            var states = Interlocked.Exchange(ref _states, null);
            if (states is null)
            {
                return;
            }

            for (var i = states.Length - 1; i >= 0; i--)
            {
                states[i].Refresh.Release();
            }
        }
    }

    private static SourceRegistration ClaimSource(ISourceRuntime source, object identity)
    {
        if (!source.TryClaimEngine(identity, out var mode))
        {
            throw new InvalidOperationException(
                $"source '{source.SourceId}' is exclusive and is already claimed by another ChalkEngine.");
        }

        try
        {
            return mode switch
            {
                SourceSharing.Exclusive => new SourceRegistration(source, shared: null),
                SourceSharing.Shared => new SourceRegistration(
                    source,
                    SharedSources.GetValue(
                        source,
                        static _ => new SharedSourceState(
                            Interlocked.Increment(ref _nextSharedSourceOrder)))),
                _ => throw new InvalidOperationException(
                    $"source '{source.SourceId}' returned unsupported sharing mode '{mode}'."),
            };
        }
        catch
        {
            source.ReleaseEngine(identity);
            throw;
        }
    }

    private static void ReleaseClaims(IReadOnlyList<SourceRegistration> registrations, object identity)
    {
        for (var i = registrations.Count - 1; i >= 0; i--)
        {
            registrations[i].Source.ReleaseEngine(identity);
        }
    }

    private async ValueTask<SharedSourceLease?> AcquireSharedSourcesAsync(
        CancellationToken ct)
    {
        var states = _orderedSharedSourceStates;

        if (states.Length == 0)
        {
            return null;
        }

        var acquired = 0;
        try
        {
            for (; acquired < states.Length; acquired++)
            {
                await states[acquired].Refresh.WaitAsync(ct).ConfigureAwait(false);
            }

            return new SharedSourceLease(states);
        }
        catch
        {
            for (var i = acquired - 1; i >= 0; i--)
            {
                states[i].Refresh.Release();
            }

            throw;
        }
    }

    private void CaptureSharedRevisions()
    {
        for (var i = 0; i < _sharedSourceRegistrations.Length; i++)
        {
            _sharedSourceRegistrations[i].Observe();
        }
    }

    private bool SharedCatalogCurrent()
    {
        for (var i = 0; i < _sharedSourceRegistrations.Length; i++)
        {
            var registration = _sharedSourceRegistrations[i];
            if (registration.ObservedRevision != registration.Shared!.Revision)
            {
                return false;
            }
        }

        return true;
    }

    private bool SharedShapesCurrent()
    {
        for (var i = 0; i < _sharedSourceRegistrations.Length; i++)
        {
            var registration = _sharedSourceRegistrations[i];
            if (registration.ObservedShapeRevision != registration.Shared!.ShapeRevision)
            {
                return false;
            }
        }

        return true;
    }

    private bool SharedShapeCurrent(string sourceId) =>
        _sharedSourceRegistrationsById is null
        || !_sharedSourceRegistrationsById.TryGetValue(sourceId, out var registration)
        || registration.ObservedShapeRevision == registration.Shared!.ShapeRevision;

    private async ValueTask EnsureSharedCatalogCurrentAsync(CancellationToken ct)
    {
        // The exclusive-only fast path: no CWT access, no global semaphore and no catalogue work.
        if (_sharedSourceRegistrations.Length == 0 || SharedCatalogCurrent())
        {
            return;
        }

        await _refreshing.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Another prepare on this engine may have repaired the catalogue while this caller waited.
            if (SharedCatalogCurrent())
            {
                return;
            }

            using var shared = await AcquireSharedSourcesAsync(ct).ConfigureAwait(false);
            
            // The shared source is already current: another engine performed the source refresh.
            // Re-publish only this engine's catalogue; do not invoke RefreshAsync on the source again.
            await PublishAsync(ct).ConfigureAwait(false);
            CaptureSharedRevisions();
        }
        finally
        {
            _refreshing.Release();
        }
    }

    /// <summary>
    /// The statistics message for <paramref name="tables"/>, or for every table when it is null
    /// (D271 (c)). Row counts, their kind and every column's statistics travel here and nowhere else;
    /// what the shape registration carries is the shape.
    /// </summary>
    private static StatisticsRegistration StatisticsFor(
        CatalogContext catalog,
        string instanceId,
        string statisticsVersion,
        IReadOnlyCollection<TableKey>? tables)
    {
        var updates = new List<TableStatisticsUpdate>();
        foreach (var schema in catalog.Schemas)
        {
            foreach (var table in schema.Tables)
            {
                if (tables is not null
                    && !tables.Contains(new TableKey(schema.SourceId, schema.Name, table.Name)))
                {
                    continue;
                }

                var columns = new List<ColumnStatisticsUpdate>();
                foreach (var column in table.Columns)
                {
                    if (Chalk.Catalog.CatalogSerialization.ToProto(column.Statistics, column.Type)
                        is { } statistics)
                    {
                        columns.Add(new ColumnStatisticsUpdate
                        {
                            Column = column.Name,
                            Statistics = statistics,
                        });
                    }
                }

                updates.Add(new TableStatisticsUpdate
                {
                    Schema = schema.Name,
                    Table = table.Name,
                    RowCount = table.RowCount,
                    RowCountKind = table.RowCountKind,
                    Columns = columns,
                });
            }
        }

        return new StatisticsRegistration
        {
            InstanceId = instanceId,
            StatisticsVersion = statisticsVersion,
            Tables = updates,
        };
    }

    /// <summary>
    /// Whether <paramref name="name"/> is <paramref name="dialects"/>' own name for a preset or one
    /// of its aliases, matched the way <c>SourceDialects.of</c> matches a <c>DatabaseProduct</c>
    /// (D249): case-insensitively, <c>-</c> and <c>_</c> interchangeable.
    /// </summary>
    private static bool DialectIsAccepted(string name, IReadOnlyList<DialectInfo> dialects)
    {
        var normalized = Normalize(name);
        return dialects.Any(
            d => Normalize(d.Name) == normalized || d.Aliases.Any(alias => Normalize(alias) == normalized));

        static string Normalize(string value) => value.Replace('-', '_').ToLowerInvariant();
    }

    /// <summary>
    /// Plans and compiles a statement. Deliberately naive in M1: the catalog is re-registered on every
    /// call and nothing is cached (that is M3). Every "unsupported" surfaces here rather than
    /// mid-stream, because the plan is compiled eagerly.
    /// </summary>
    public ValueTask<PreparedQuery> PrepareAsync(
        string sql, PrepareOptions? options = null, CancellationToken ct = default) =>
        PrepareAsync(sql, context: null, options, ct);

    /// <summary>
    /// Plans and compiles a statement with an execution context, which is the primary binding mode:
    /// the planner folds every scalar and every small list into constants, so the plan is the one this
    /// principal gets and its digest differs from another principal's.
    /// </summary>
    public async ValueTask<IReadOnlyList<Google.Protobuf.WellKnownTypes.Any>> PlanExtensionsAsync(
        string sql,
        RequestContext? context = null,
        PrepareOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        options ??= new PrepareOptions();

        var rewriter = ParameterRewriter.Parse(sql);
        var rendered = rewriter.Render(rewriter.PrepareShape());
        var result = await PlanAsync(rendered.Sql, options, context, ct).ConfigureAwait(false);
        return result.Extensions;
    }

    public async ValueTask<PreparedQuery> PrepareAsync(
        string sql,
        RequestContext? context,
        PrepareOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        options ??= new PrepareOptions();
        if (context is not null && context.FoldMaxRows < 0)
        {
            throw new ArgumentException(
                "RequestContext.FoldMaxRows is negative; 0 means the planner's default of "
                + $"{RequestContext.DefaultFoldMaxRows}.",
                nameof(context));
        }

        var rewriter = ParameterRewriter.Parse(sql);
        var shape = rewriter.PrepareShape();
        var rendered = rewriter.Render(shape);

        var result = await PlanAsync(rendered.Sql, options, context, ct).ConfigureAwait(false);
        var compiled = await CompileAsync(result.Plan, options, context, ct).ConfigureAwait(false);
        
        var prepared =
            new PreparedQuery(this, sql, rewriter, options, result, compiled, shape, rendered, context);
        _log.LogDebug(
            "prepared {Digest:x16} ({Pushdown}) in {Micros}us: {Sql}",
            result.PlanDigest,
            options.Pushdown,
            result.Stats.TotalMicros,
            sql);
        return prepared;
    }

    /// <summary>Binds <c>?</c> or <c>$n</c> parameters and returns a ready-to-enumerate execution.</summary>
    /// <param name="parameters"></param>
    /// <param name="arena">
    /// The arena this execution's memory comes from. Null — the default — rents one from the engine's
    /// pool. A host that passes its own owns it, and must not run two executions on it at once.
    /// </param>
    /// <param name="query"></param>
    /// <param name="ct"></param>
    public ValueTask<QueryExecution> ExecuteAsync(
        PreparedQuery query,
        IReadOnlyList<object?>? parameters = null,
        ExecutionArena? arena = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var values = ParameterBinder.ResolvePositional(query.Parameters, parameters, query.ParameterStyle);
        return query.ExecuteAsync(values, arena, ct);
    }

    /// <summary>
    /// Binds this principal's context values, and the statement's own parameters, for a query
    /// prepared under execute-time binding (<c>docs/design/16-entitlements.md</c> §2, D209).
    /// </summary>
    /// <param name="context">
    /// The values, whose shape must be the one the query was prepared with — the same names, the same
    /// kinds, the same types. A shape mismatch is refused before anything runs, because the plan was
    /// built for the shape.
    /// </param>
    /// <inheritdoc cref="ExecuteAsync(PreparedQuery, IReadOnlyList{object?}, ExecutionArena, CancellationToken)"
    ///     path="/param[@name='arena']"/>
    public ValueTask<QueryExecution> ExecuteAsync(
        PreparedQuery query,
        RequestContext context,
        IReadOnlyList<object?>? parameters = null,
        ExecutionArena? arena = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var values = ParameterBinder.ResolvePositional(query.Parameters, parameters, query.ParameterStyle);
        return query.ExecuteAsync(values, context, arena, ct);
    }

    /// <summary>Binds <c>@name</c> parameters from a dictionary.</summary>
    /// <inheritdoc cref="ExecuteAsync(PreparedQuery, IReadOnlyList{object?}, ExecutionArena, CancellationToken)"
    ///     path="/param[@name='arena']"/>
    public ValueTask<QueryExecution> ExecuteAsync(
        PreparedQuery query,
        IReadOnlyDictionary<string, object?> parameters,
        ExecutionArena? arena = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        RequireNamed(query);
        return query.ExecuteAsync(ParameterBinder.ResolveNamed(query.Parameters, parameters), arena, ct);
    }

    /// <summary>Binds <c>@name</c> parameters from an anonymous object or POCO, Dapper-style.</summary>
    /// <inheritdoc cref="ExecuteAsync(PreparedQuery, IReadOnlyList{object?}, ExecutionArena, CancellationToken)"
    ///     path="/param[@name='arena']"/>
    public ValueTask<QueryExecution> ExecuteAsync(
        PreparedQuery query,
        object parameters,
        ExecutionArena? arena = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        RequireNamed(query);
        return query.ExecuteAsync(ParameterBinder.ResolveNamed(query.Parameters, parameters), arena, ct);
    }

    /// <summary>Prepare and execute in one call, for a statement run once.</summary>
    /// <inheritdoc cref="ExecuteAsync(PreparedQuery, IReadOnlyList{object?}, ExecutionArena, CancellationToken)"
    ///     path="/param[@name='arena']"/>
    public async IAsyncEnumerable<RecordBatch> QueryAsync(
        string sql,
        IReadOnlyList<object?>? parameters = null,
        ExecutionArena? arena = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var prepared = await PrepareAsync(sql, options: null, ct).ConfigureAwait(false);
        await using var execution = await ExecuteAsync(prepared, parameters, arena, ct).ConfigureAwait(false);
        await foreach (var batch in execution.Batches.WithCancellation(ct).ConfigureAwait(false))
        {
            yield return batch;
        }
    }

    /// <inheritdoc cref="QueryAsync(string, IReadOnlyList{object?}, ExecutionArena, CancellationToken)"/>
    public async IAsyncEnumerable<RecordBatch> QueryAsync(
        string sql,
        IReadOnlyDictionary<string, object?> parameters,
        ExecutionArena? arena = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var prepared = await PrepareAsync(sql, options: null, ct).ConfigureAwait(false);
        await using var execution = await ExecuteAsync(prepared, parameters, arena, ct).ConfigureAwait(false);
        await foreach (var batch in execution.Batches.WithCancellation(ct).ConfigureAwait(false))
        {
            yield return batch;
        }
    }

    /// <inheritdoc cref="QueryAsync(string, IReadOnlyList{object?}, ExecutionArena, CancellationToken)"/>
    public async IAsyncEnumerable<RecordBatch> QueryAsync(
        string sql,
        object parameters,
        ExecutionArena? arena = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var prepared = await PrepareAsync(sql, options: null, ct).ConfigureAwait(false);
        await using var execution = await ExecuteAsync(prepared, parameters, arena, ct).ConfigureAwait(false);
        await foreach (var batch in execution.Batches.WithCancellation(ct).ConfigureAwait(false))
        {
            yield return batch;
        }
    }

    /// <summary>
    /// The same execution, on an arena rented from <paramref name="pool"/> instead of from the
    /// engine's own (D258, <c>08-execution-arena.md</c> §2.1).
    /// </summary>
    /// <param name="pool">
    /// The host's pool. The execution rents an arena from it and hands it back when the run ends;
    /// beyond the pool's capacity it gets a transient arena, exactly as the engine's own pool
    /// behaves. The pool is the host's to dispose, and must outlive any pooled output batch taken
    /// through it.
    /// </param>
    /// <param name="query"></param>
    /// <param name="parameters"></param>
    /// <param name="ct"></param>
    public ValueTask<QueryExecution> ExecuteAsync(
        PreparedQuery query,
        IReadOnlyList<object?>? parameters,
        ArenaPool pool,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(pool);
        var values = ParameterBinder.ResolvePositional(query.Parameters, parameters, query.ParameterStyle);
        return query.ExecuteAsync(values, context: null, pool, ct);
    }

    /// <inheritdoc cref="ExecuteAsync(PreparedQuery, IReadOnlyList{object?}, ArenaPool, CancellationToken)"
    ///     path="/param[@name='pool']"/>
    public ValueTask<QueryExecution> ExecuteAsync(
        PreparedQuery query,
        RequestContext context,
        IReadOnlyList<object?>? parameters,
        ArenaPool pool,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(pool);
        var values = ParameterBinder.ResolvePositional(query.Parameters, parameters, query.ParameterStyle);
        return query.ExecuteAsync(values, context, pool, ct);
    }

    /// <inheritdoc cref="ExecuteAsync(PreparedQuery, IReadOnlyList{object?}, ArenaPool, CancellationToken)"
    ///     path="/param[@name='pool']"/>
    public ValueTask<QueryExecution> ExecuteAsync(
        PreparedQuery query,
        IReadOnlyDictionary<string, object?> parameters,
        ArenaPool pool,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(pool);
        RequireNamed(query);
        return query.ExecuteAsync(
            ParameterBinder.ResolveNamed(query.Parameters, parameters), context: null, pool, ct);
    }

    /// <inheritdoc cref="ExecuteAsync(PreparedQuery, IReadOnlyList{object?}, ArenaPool, CancellationToken)"
    ///     path="/param[@name='pool']"/>
    public ValueTask<QueryExecution> ExecuteAsync(
        PreparedQuery query,
        object parameters,
        ArenaPool pool,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(pool);
        RequireNamed(query);
        return query.ExecuteAsync(
            ParameterBinder.ResolveNamed(query.Parameters, parameters), context: null, pool, ct);
    }

    /// <inheritdoc cref="ExecuteAsync(PreparedQuery, IReadOnlyList{object?}, ArenaPool, CancellationToken)"
    ///     path="/param[@name='pool']"/>
    public async IAsyncEnumerable<RecordBatch> QueryAsync(
        string sql,
        IReadOnlyList<object?>? parameters,
        ArenaPool pool,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var prepared = await PrepareAsync(sql, options: null, ct).ConfigureAwait(false);
        await using var execution = await ExecuteAsync(prepared, parameters, pool, ct).ConfigureAwait(false);
        await foreach (var batch in execution.Batches.WithCancellation(ct).ConfigureAwait(false))
        {
            yield return batch;
        }
    }

    /// <inheritdoc cref="ExecuteAsync(PreparedQuery, IReadOnlyList{object?}, ArenaPool, CancellationToken)"
    ///     path="/param[@name='pool']"/>
    public async IAsyncEnumerable<RecordBatch> QueryAsync(
        string sql,
        IReadOnlyDictionary<string, object?> parameters,
        ArenaPool pool,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var prepared = await PrepareAsync(sql, options: null, ct).ConfigureAwait(false);
        await using var execution = await ExecuteAsync(prepared, parameters, pool, ct).ConfigureAwait(false);
        await foreach (var batch in execution.Batches.WithCancellation(ct).ConfigureAwait(false))
        {
            yield return batch;
        }
    }

    /// <inheritdoc cref="ExecuteAsync(PreparedQuery, IReadOnlyList{object?}, ArenaPool, CancellationToken)"
    ///     path="/param[@name='pool']"/>
    public async IAsyncEnumerable<RecordBatch> QueryAsync(
        string sql,
        object parameters,
        ArenaPool pool,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var prepared = await PrepareAsync(sql, options: null, ct).ConfigureAwait(false);
        await using var execution = await ExecuteAsync(prepared, parameters, pool, ct).ConfigureAwait(false);
        await foreach (var batch in execution.Batches.WithCancellation(ct).ConfigureAwait(false))
        {
            yield return batch;
        }
    }

    /// <summary>
    /// Re-describes every source, assembles a new catalog one epoch on, validates it and registers
    /// it with the planner (D86). Returns the new epoch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The order matters and is the conservative one: introspect, validate, register, and only then
    /// publish. A refresh that fails at any of those steps leaves the engine on the epoch it was
    /// already serving, so a database that is briefly unreachable or briefly wrong does not take
    /// the catalog with it.
    /// </para>
    /// <para>
    /// Executions already running are untouched: they hold their own compiled plan, their batches
    /// and their arena, and they finish against the catalog they started with. Everything after the
    /// swap plans against the new one, and every <see cref="PreparedQuery"/> from an earlier epoch
    /// is stale — <see cref="PreparedQuery.IsStale"/> says so, and executing one throws
    /// <see cref="Chalk.Sources.StalePlanException"/> rather than running a plan compiled for a
    /// shape the source may no longer have.
    /// </para>
    /// </remarks>
    public ValueTask<long> RefreshCatalogAsync(CancellationToken ct = default) => RefreshAsync(ct);

    /// <inheritdoc cref="RefreshCatalogAsync"/>
    /// <remarks>
    /// The D260 spelling of <see cref="RefreshCatalogAsync"/>, and the same call. Every source is
    /// asked to re-read whatever its descriptor came from first, which for a POCO source means every
    /// <c>Func</c> registration and the snapshots built from them
    /// (<c>docs/design/35-poco-refresh.md</c> §2).
    /// </remarks>
    public async ValueTask<long> RefreshAsync(CancellationToken ct = default)
    {
        await _refreshing.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Exclusive-only engines take exactly the pre-sharing path: the engine semaphore and
            // source refreshes, with no process-wide source coordination.
            if (_orderedSharedSourceStates.Length == 0)
            {
                foreach (var registration in _sourceRegistrations)
                {
                    await registration.Source.RefreshAsync(ct).ConfigureAwait(false);
                }

                return await PublishAsync(ct).ConfigureAwait(false);
            }

            using var shared = await AcquireSharedSourcesAsync(ct).ConfigureAwait(false);
            
            foreach (var registration in _sourceRegistrations)
            {
                await registration.Source.RefreshAsync(ct).ConfigureAwait(false);
                registration.Shared?.Changed(shapeMayHaveChanged: true);
            }

            var epoch = await PublishAsync(ct).ConfigureAwait(false);
            CaptureSharedRevisions();
            return epoch;
        }
        finally
        {
            _refreshing.Release();
        }
    }

    /// <summary>
    /// One refresh transaction (D260, <c>docs/design/35-poco-refresh.md</c> §2): the tables
    /// <paramref name="plan"/> names, across any number of sources, replaced under one catalog
    /// epoch. Returns the new epoch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// In order: every entry is validated — a source this engine holds, a source that can refresh,
    /// a table it has, the row type its table was built over — so a transaction with one bad entry
    /// in it changes nothing anywhere and moves no epoch. Then every source builds its new state off
    /// the execution path, while executions continue to read the state it will replace. Then all of
    /// it is published at once, the sources are described again, and the epoch moves once.
    /// </para>
    /// <para>
    /// The engine refresh lock is held for all of it; when this engine contains shared sources, their
    /// process-wide refresh gates are held too. A scheduled or second-engine refresh therefore waits
    /// rather than publishing between two of the swaps: an execution sees this transaction whole or not at all.
    /// Executions already running keep the state they captured, and a
    /// <see cref="PreparedQuery"/> survives — a replacement changes rows, and rows are not shape
    /// (<see cref="PreparedQuery.IsStale"/>).
    /// </para>
    /// </remarks>
    public async ValueTask<long> RefreshAsync(Action<RefreshBuilder> plan, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var builder = new RefreshBuilder();
        plan(builder);
        if (builder.Entries.Count == 0 && builder.Scopes.Count == 0)
        {
            return await RefreshAsync(ct).ConfigureAwait(false);
        }

        // Every scope is resolved to a source before the lock is taken, so a target this engine does
        // not serve is refused without holding anything up and without changing anything (D271 (f)).
        var scopes = new List<(ISourceRuntime Source, string Table)>(builder.Scopes.Count);
        foreach (var target in builder.Scopes)
        {
            scopes.Add((Resolve(target, nameof(plan)), target.Table));
        }

        // Grouped by source instance, in the order the entries were written, so a source hears about
        // all of its tables at once and the validation message names the entry the host wrote.
        var bySource =
            new List<(ISourceRuntime Runtime, IRefreshableSource Refreshable, List<SourceRefreshEntry> Entries)>();
        foreach (var (source, entry) in builder.Entries)
        {
            if (!_sources.TryGetValue(source.SourceId, out var held) || !ReferenceEquals(held, source))
            {
                throw new ArgumentException(
                    $"the source '{source.SourceId}' is not one this engine was built with; a refresh "
                    + "replaces the rows of a source the engine is already serving.",
                    nameof(plan));
            }

            if (source is not IRefreshableSource refreshable)
            {
                throw new ArgumentException(
                    $"the source '{source.SourceId}' is a {source.GetType().Name}, which does not "
                    + "implement IRefreshableSource, so its rows cannot be replaced from a refresh. "
                    + "An in-process POCO source can; a source over a database is refreshed by "
                    + "changing the database.",
                    nameof(plan));
            }

            var found = bySource.FindIndex(g => ReferenceEquals(g.Refreshable, refreshable));
            if (found < 0)
            {
                bySource.Add((source, refreshable, [entry]));
            }
            else
            {
                bySource[found].Entries.Add(entry);
            }
        }

        // Every source refuses what it cannot do before any of them builds anything.
        foreach (var (_, source, entries) in bySource)
        {
            source.ValidateRefresh(entries);
        }

        await _refreshing.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            SharedSourceLease? sharedLease = null;
            if (_orderedSharedSourceStates.Length != 0)
            {
                sharedLease = await AcquireSharedSourcesAsync(ct).ConfigureAwait(false);
            }

            using (sharedLease)
            {
                // The scopes first: a re-introspection is the source going to look again, and a
                // replacement in the same transaction is the host saying what the rows are. The host's
                // word is the one that survives, so it is applied last.
                foreach (var (source, table) in scopes)
                {
                    if (table.Length == 0)
                    {
                        await source.RefreshAsync(ct).ConfigureAwait(false);
                    }
                    else
                    {
                        await source.RefreshTableAsync(table, ct).ConfigureAwait(false);
                    }

                    if (_sharedSourceRegistrationsById is { } sharedById
                        && sharedById.TryGetValue(source.SourceId, out var sharedRegistration))
                    {
                        sharedRegistration.Shared!.Changed(shapeMayHaveChanged: true);
                    }
                }

                var commits =
                    new List<(ISourceRuntime Runtime, ISourceRefreshCommit Commit)>(bySource.Count);
                foreach (var (runtime, source, entries) in bySource)
                {
                    commits.Add((
                        runtime,
                        await source.PrepareRefreshAsync(entries, ct).ConfigureAwait(false)));
                }

                // The one moment the transaction is not atomic is this loop, and it is a handful of
                // reference writes with nothing between them that can fail: everything that could was
                // done above. Replace/Append cannot change table shape, so cross-engine shape invalidation
                // is not needed for this publication.
                foreach (var (runtime, commit) in commits)
                {
                    commit.Commit();
                    if (_sharedSourceRegistrationsById is { } sharedById
                        && sharedById.TryGetValue(runtime.SourceId, out var sharedRegistration))
                    {
                        sharedRegistration.Shared!.Changed(shapeMayHaveChanged: false);
                    }
                }

                var epoch = await PublishAsync(ct).ConfigureAwait(false);
                CaptureSharedRevisions();
                return epoch;
            }
        }
        finally
        {
            _refreshing.Release();
        }
    }

    /// <summary>
    /// The source a scoped refresh names (D271 (f)): by the runtime's id when the target carries one,
    /// and by the schema's federation-level name otherwise. A target this engine does not serve is
    /// refused here, naming what it looked for.
    /// </summary>
    private ISourceRuntime Resolve(IRefreshTarget target, string parameterName)
    {
        if (target.SourceId.Length > 0)
        {
            if (_sources.TryGetValue(target.SourceId, out var byId))
            {
                RequireTable(byId, target, parameterName);
                return byId;
            }

            throw new ArgumentException(
                $"the refresh names the source '{target.SourceId}', which is not one this engine was "
                + "built with. The sources it serves are: "
                + string.Join(", ", _sources.Keys.Select(id => $"'{id}'")) + ".",
                parameterName);
        }

        foreach (var schema in Catalog.Schemas)
        {
            if (string.Equals(schema.Name, target.Schema, StringComparison.OrdinalIgnoreCase)
                && _sources.TryGetValue(schema.SourceId, out var bySchema))
            {
                RequireTable(bySchema, target, parameterName);
                return bySchema;
            }
        }

        throw new ArgumentException(
            $"the refresh names the schema '{target.Schema}', which this engine's catalog does not "
            + "declare. The schemas it serves are: "
            + string.Join(", ", Catalog.Schemas.Select(s => $"'{s.Name}'")) + ".",
            parameterName);
    }

    /// <summary>
    /// Refuses a table target whose table that source does not have — the mistake the typed handles
    /// exist to make impossible, caught here for the string spellings that can still express it.
    /// </summary>
    private void RequireTable(ISourceRuntime source, IRefreshTarget target, string parameterName)
    {
        if (target.Table.Length == 0)
        {
            return;
        }

        foreach (var schema in Catalog.Schemas)
        {
            if (!string.Equals(schema.SourceId, source.SourceId, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var declared in schema.Tables)
            {
                if (string.Equals(declared.Name, target.Table, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
        }

        throw new ArgumentException(
            $"the refresh names the table '{target.Table}' on source '{source.SourceId}', which does "
            + "not declare it. A table is named by its schema and its own name together, and never by "
            + "its name alone (D271 (h)).",
            parameterName);
    }

    /// <summary>
    /// Describes every source, assembles the next catalog, validates and registers it, and publishes
    /// it one epoch on. The caller holds the engine refresh lock and, when shared sources exist, all
    /// of this engine's shared-source refresh gates.
    /// </summary>
    private async ValueTask<long> PublishAsync(CancellationToken ct)
    {
        var schemas = new List<SchemaDescriptor>(_sourceRegistrations.Length);
        foreach (var registration in _sourceRegistrations)
        {
            schemas.Add(registration.Source.DescribeSchema());
        }

        var current = Catalog;
        var next = WithJoinPolicy(
            new CatalogContext
            {
                ContextId = current.ContextId,
                Epoch = current.Epoch + 1,
                Schemas = schemas,
                Associations = _options.Associations,
            },
            _options.JoinPolicy);
        CatalogValidator.Validate(next);
        HostFunctions.CheckCatalog(next, _functions);

        // Per table since D271 (d): which tables changed shape, which changed only in their numbers,
        // and which did not change at all. A shape change registers a delta naming the changed
        // tables; a data-only change publishes statistics alone; a refresh that moved nothing sends
        // nothing at all.
        var shape = CatalogShape.Of(next);
        var changed = shape.ShapeChangesSince(_shape);
        var removed = shape.RemovedSince(_shape);
        var moved = shape.StatisticsChangesSince(_shape);
        var schemaChange = changed.Count > 0 || removed.Count > 0;

        var nextShapeVersion = schemaChange ? CatalogVersions.Mint() : _shapeVersion;
        var nextStatisticsVersion = moved.Count > 0 ? CatalogVersions.Mint() : _statisticsVersion;

        if (schemaChange)
        {
            await RegisterShapeAsync(next, nextShapeVersion, changed, removed, ct).ConfigureAwait(false);
        }

        if (moved.Count > 0)
        {
            await _options.Planner.RegisterStatisticsAsync(
                StatisticsFor(next, _instanceId, nextStatisticsVersion, moved), ct).ConfigureAwait(false);
            _registeredStatisticsVersion = nextStatisticsVersion;
        }

        // The shape first, so a reader that sees the new catalog has already seen the epoch its
        // shape belongs to. The other order would let a plan from the old epoch be thought current
        // for as long as the two writes are apart, which is the direction that runs a stale plan.
        if (schemaChange)
        {
            var epochs = new Dictionary<TableKey, long>(_tableShapeEpochs);
            foreach (var table in changed)
            {
                epochs[table] = next.Epoch;
            }

            foreach (var table in removed)
            {
                epochs.Remove(table);
            }

            _shape = shape;
            _shapeVersion = nextShapeVersion;
            Volatile.Write(ref _tableShapeEpochs, epochs);
            Volatile.Write(ref _shapeEpoch, next.Epoch);
        }
        else
        {
            // The numbers moved and the shape did not, so the per-table shapes are the ones already
            // held; what is replaced is the statistics half, which is what the next comparison reads.
            _shape = shape;
        }

        _statisticsVersion = nextStatisticsVersion;
        Volatile.Write(ref _catalog, next);
        _log.LogInformation(
            "catalog refreshed: epoch {Epoch} -> {NextEpoch}, {Sources} sources, {Tables} tables, "
            + "{Change} (shape epoch {ShapeEpoch}, {Moved} statistics published)",
            current.Epoch,
            next.Epoch,
            schemas.Count,
            schemas.Sum(s => s.Tables.Count),
            schemaChange
                ? $"shape changed in {changed.Count} table(s), {removed.Count} removed"
                : "data only",
            ShapeEpoch,
            moved.Count);
        return next.Epoch;
    }

    /// <summary>
    /// Installs one shape version (D271 (b), (d)): a delta naming the changed and removed tables when
    /// the planner still holds the version this engine last registered, and the whole catalog
    /// otherwise — which is also what a delta falls back to when the planner reports its base gone.
    /// </summary>
    private async ValueTask RegisterShapeAsync(
        CatalogContext next,
        string shapeVersion,
        IReadOnlyList<TableKey> changed,
        IReadOnlyList<TableKey> removed,
        CancellationToken ct)
    {
        var whole = new CatalogRegistration
        {
            Catalog = next,
            InstanceId = _instanceId,
            ShapeVersion = shapeVersion,
        };

        if (!_options.Planner.AcceptsCatalogDeltas || _registeredShapeVersion.Length == 0)
        {
            await _options.Planner.RegisterCatalogAsync(whole, ct).ConfigureAwait(false);
            _registeredShapeVersion = shapeVersion;
            return;
        }

        var delta = new CatalogRegistration
        {
            Catalog = Delta(next, changed),
            InstanceId = _instanceId,
            ShapeVersion = shapeVersion,
            BaseShapeVersion = _registeredShapeVersion,
            RemovedTables = [.. removed.Select(t => $"{t.Schema}.{t.Table}")],
        };

        try
        {
            await _options.Planner.RegisterCatalogAsync(delta, ct).ConfigureAwait(false);
        }
        catch (PlanningException failure) when (failure.Kind == Chalk.Client.Rpc.PlanErrorKind.UnknownCatalogVersion)
        {
            // The planner no longer holds the base — it evicted it, or it restarted. Memory policy,
            // recovered here: the whole catalog, and the numbers with it, because a planner that lost
            // the base lost those too.
            _log.LogDebug(
                "the planner no longer holds catalog version {Base}; registering the catalog whole",
                _registeredShapeVersion);
            await _options.Planner.RegisterCatalogAsync(whole, ct).ConfigureAwait(false);
            await _options.Planner.RegisterStatisticsAsync(
                StatisticsFor(next, _instanceId, _statisticsVersion, tables: null), ct).ConfigureAwait(false);
            _registeredStatisticsVersion = _statisticsVersion;
        }

        _registeredShapeVersion = shapeVersion;
    }

    /// <summary>
    /// The catalog reduced to the tables whose shape changed (D271 (d)), each carried whole, inside
    /// the schema it belongs to and with that schema's own properties. The catalog's own properties —
    /// the id, the epoch, the join policy, the associations — travel whole, because they are small
    /// and a delta that could leave one of them behind would be a second thing to reason about.
    /// </summary>
    private static CatalogContext Delta(CatalogContext next, IReadOnlyList<TableKey> changed)
    {
        var schemas = new List<SchemaDescriptor>();
        foreach (var schema in next.Schemas)
        {
            var tables = schema.Tables
                .Where(t => changed.Contains(new TableKey(schema.SourceId, schema.Name, t.Name)))
                .ToList();
            if (tables.Count == 0)
            {
                continue;
            }

            schemas.Add(new SchemaDescriptor
            {
                SourceId = schema.SourceId,
                Name = schema.Name,
                Kind = schema.Kind,
                Dialect = schema.Dialect,
                Capabilities = schema.Capabilities,
                DialectProfile = schema.DialectProfile,
                CostProfile = schema.CostProfile,
                Tables = tables,
                Functions = schema.Functions,
                TrustSourceRowSecurity = schema.TrustSourceRowSecurity,
            });
        }

        return new CatalogContext
        {
            ContextId = next.ContextId,
            Epoch = next.Epoch,
            Schemas = schemas,
            Associations = next.Associations,
            JoinPolicy = next.JoinPolicy,
        };
    }

    /// <summary>
    /// The cadence's callback. A scheduled refresh that fails is logged and dropped: the engine
    /// keeps the epoch it has, and the next tick tries again.
    /// </summary>
    private void RefreshOnSchedule() => _ = RefreshQuietlyAsync();

    private async Task RefreshQuietlyAsync()
    {
        try
        {
            await RefreshCatalogAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            _log.LogWarning(
                failure,
                "scheduled catalog refresh failed; staying on epoch {Epoch}",
                Catalog.Epoch);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_refreshTimer is not null)
        {
            await _refreshTimer.DisposeAsync().ConfigureAwait(false);
        }

        await _refreshing.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            ReleaseClaims(_sourceRegistrations, _identity);
        }
        finally
        {
            _refreshing.Release();
        }

        _arenas.Dispose();
        _refreshing.Dispose();
        await _options.Planner.DisposeAsync().ConfigureAwait(false);
    }

    internal ExecutionSettings Settings => _settings;

    /// <summary>
    /// The engine's own pool of arenas: one is rented for every execution that brings neither an
    /// arena nor a pool of its own (<c>08-execution-arena.md</c> §2.1). Read-only — it is built from
    /// <c>ExecutionOptions.Arena</c> and <c>ExecutionOptions.ArenaPoolSize</c>, it is unnamed, and
    /// disposing the engine disposes it. A host that wants memory kept apart from this builds its own
    /// <see cref="ArenaPool"/> and passes it to an execution (D258).
    /// </summary>
    public ArenaPool Arenas => _arenas;

    internal ILogger Log => _log;

    /// <summary>Plans one rendered statement against the version the engine currently holds.</summary>
    internal ValueTask<PlanResult> PlanAsync(string sql, PrepareOptions options, CancellationToken ct) =>
        PlanAsync(sql, options, context: null, ct);

    /// <summary>The same, with the values this statement binds by name (step 26).</summary>
    internal ValueTask<PlanResult> PlanAsync(
        string sql,
        PrepareOptions options,
        RequestContext? context,
        CancellationToken ct) =>
        PlanAsync(sql, options, context, narrowFrom: 0, ct);

    /// <summary>
    /// The same, naming the plan this one narrows. A hint: the context is already the union,
    /// so the plan is the same whether the sidecar still holds that one's tree or converts again.
    /// </summary>
    internal async ValueTask<PlanResult> PlanAsync(
        string sql,
        PrepareOptions options,
        RequestContext? context,
        ulong narrowFrom,
        CancellationToken ct)
    {
        await EnsureSharedCatalogCurrentAsync(ct).ConfigureAwait(false);

        // Nothing is registered here since D271 (b): the catalog crossed the wire when its version
        // was minted, and a prepare against a steady catalog carries no catalog bytes at all. What it
        // carries instead is the two versions, and a planner that does not hold the shape answers by
        // name — which is recovered below, not reported.

        // This statement's own options, or the engine's (D234). One whole or the other, because a
        // half-overridden budget is a budget nobody wrote.
        var planning = options.Planning ?? _options.Planning;
        // A planning a stop could address needs a name, and only such a planning gets one: a
        // prepare with no stop token sends no id and takes no entry in the sidecar's registry.
        var requestId = planning.StopToken.CanBeCanceled ? Guid.NewGuid().ToString("n") : string.Empty;

        var request = new PlanRequest
        {
            Sql = sql,
            ContextId = Catalog.ContextId,
            CatalogEpoch = Catalog.Epoch,
            InstanceId = _instanceId,
            ShapeVersion = _shapeVersion,
            StatisticsVersion = _statisticsVersion,
            ClientIrVersion = IrVersion.Current,
            Context = context,
            NarrowFrom = narrowFrom,
            ParameterTypes = options.ParameterTypes ?? [],
            Planning = planning,
            PlanningRequestId = requestId,
            Redaction = RedactionFor(options),
            Options = new Chalk.Client.PlannerOptions
            {
                Pushdown = options.Pushdown,
                IncludePlanText = options.IncludePlanText,
                Conformance = options.Conformance,
                Libraries = options.Libraries,
                DisabledCapabilities = options.DisabledCapabilities,
                JoinPolicy = options.JoinPolicy,
                Extensions = options.Extensions,
            },
        };

        // The stop token, wired to the second RPC for as long as this call is in flight. The
        // registration is disposed whatever happens, so a token a host keeps for the life of a page
        // does not accumulate one callback per prepare.
        using var stop = planning.StopToken.CanBeCanceled
            ? planning.StopToken.Register(
                static state =>
                {
                    var (planner, id, log) = ((IQueryPlanner, string, ILogger))state!;
                    // Fire and forget on purpose: a stop is advice, the plan comes back on the Plan
                    // call, and nothing here should be able to fail a prepare.
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await planner.StopPlanningAsync(id, CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (Exception e)
                        {
                            log.LogDebug(e, "stop for planning {RequestId} was not delivered", id);
                        }
                    });
                },
                (_options.Planner, requestId, _log))
            : default;

        PlanResult result;
        try
        {
            result = await _options.Planner.PlanAsync(request, ct).ConfigureAwait(false);
        }
        catch (PlanningException failure) when (failure.Kind == Chalk.Client.Rpc.PlanErrorKind.UnknownCatalogVersion)
        {
            // The planner evicted this version, or it restarted (D271 (b), (e)). Memory policy and
            // never correctness: register the version again and retry exactly once. A host sees
            // nothing — no callback, no re-prepare, no error — which is what makes the planner's
            // bounds and its lifetime its own business.
            _log.LogDebug(
                "the planner does not hold catalog version {Version}; registering it and retrying once",
                request.ShapeVersion);
            await ReregisterAsync(ct).ConfigureAwait(false);
            result = await _options.Planner.PlanAsync(request, ct).ConfigureAwait(false);
        }

        //The entitlement invariant, re-established from *this* catalog and never from the planner's claim,
        // before anything executes. It is given the client's own answer to "is this table entitled,
        // and how many columns has it", and it walks the physical plan to see whether that can be believed.
        // The clause that compares the plan against a *report* needs the report, so it is the entitlement
        // wrapper that asks for it; this clause is catalog data and stays here, where every prepare passes.
        PlanValidator.Validate(
            result.Plan, new PlanValidationOptions { EntitledTables = EntitledColumnCount });
        return result;
    }

    /// <summary>
    /// Registers the versions this engine currently holds, whole, after a planner reported one of
    /// them unknown (D271 (b)). Whole rather than as a delta, because a planner that lost a version
    /// has no base to apply one to; and both versions, because it lost the statistics with the shape.
    /// </summary>
    /// <remarks>
    /// Under its own lock and not the refresh lock: this runs from a prepare, and a prepare that
    /// waited behind a refresh it does not need would turn a recovery into a queue. Two prepares that
    /// race here register once and the second finds the version already registered, which the
    /// planner treats as a no-op anyway.
    /// </remarks>
    private async ValueTask ReregisterAsync(CancellationToken ct)
    {
        await _registering.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var catalog = Catalog;
            var shapeVersion = _shapeVersion;
            var statisticsVersion = _statisticsVersion;
            await _options.Planner.RegisterCatalogAsync(
                new CatalogRegistration
                {
                    Catalog = catalog,
                    InstanceId = _instanceId,
                    ShapeVersion = shapeVersion,
                },
                ct).ConfigureAwait(false);
            await _options.Planner.RegisterStatisticsAsync(
                StatisticsFor(catalog, _instanceId, statisticsVersion, tables: null),
                ct).ConfigureAwait(false);
            _registeredShapeVersion = shapeVersion;
            _registeredStatisticsVersion = statisticsVersion;
        }
        finally
        {
            _registering.Release();
        }
    }

    /// <summary>
    /// What this prepare asks to have redacted, or null when it asks for nothing — which is the
    /// default and every statement of an engine that never turns redaction on (D262).
    /// </summary>
    internal RedactionRequest? RedactionFor(PrepareOptions options) =>
        options.IncludeRedactedSql ?? _options.Redaction.IncludeRedactedSql
            ? new RedactionRequest
            {
                Salt = Salt(),
                Scope = _options.Redaction.Scope,
                KeepStructural = _options.Redaction.KeepStructural,
            }
            : null;

    /// <summary>
    /// The salt every redaction of this engine is keyed by: the host's, or a random 32-byte one
    /// generated <em>once, on first use</em> (D262, design 37 §2). No property returns it — a salt
    /// a host did not supply is the engine's own secret, and one it did supply is already the
    /// host's.
    /// </summary>
    /// <remarks>
    /// Lazily, so an engine that never redacts never asks the operating system for entropy; and
    /// under a lock, so two prepares racing on first use cannot key one process's log lines two
    /// different ways.
    /// </remarks>
    private ReadOnlyMemory<byte> Salt()
    {
        if (!_options.Redaction.Salt.IsEmpty)
        {
            return _options.Redaction.Salt;
        }

        var generated = Volatile.Read(ref _redactionSalt);
        if (generated is not null)
        {
            return generated;
        }

        lock (_redactionSaltLock)
        {
            _redactionSalt ??= System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
            return _redactionSalt;
        }
    }

    private readonly Lock _redactionSaltLock = new();
    private byte[]? _redactionSalt;

    /// <summary>
    /// Redacts a statement this engine never prepared (D262) — above all one that failed to parse,
    /// which is where a host most wants a text it can keep. Paid per call, and keyed by the same
    /// salt every prepared statement of this engine is.
    /// </summary>
    public ValueTask<RedactedSql> RedactSqlAsync(string sql, CancellationToken ct = default) =>
        RedactSqlAsync(sql, SqlConformance.Default, ct);

    /// <summary>
    /// The same, saying which dialect the text is written in (D34, D259), so the token fallback
    /// lexes it with the grammar a prepare would have used.
    /// </summary>
    public async ValueTask<RedactedSql> RedactSqlAsync(
        string sql, SqlConformance conformance, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sql);

        // A named parameter is rewritten to `?` exactly as a prepare rewrites it, and the name is
        // put back into the text that comes back, so the statement reads as the host wrote it
        // (D287). Any other style, and any text the rewrite cannot read, goes as it is: this call
        // exists above all for text that failed to parse, and is not the place to refuse one.
        var text = sql;
        IReadOnlyList<string>? names = null;
        try
        {
            var rewriter = ParameterRewriter.Parse(sql);
            if (rewriter.Style == ParameterStyle.Named)
            {
                var rendered = rewriter.Render(rewriter.PrepareShape());
                text = rendered.Sql;
                names = rewriter.SlotNames(rendered);
            }
        }
        catch (ArgumentException)
        {
            // Mixed styles, or a gap in the ordinals: not a statement a prepare would take.
        }

        var redacted = await _options.Planner.RedactSqlAsync(
            new RedactSqlRequest
            {
                Sql = text,
                Conformance = conformance,
                Redaction = new RedactionRequest
                {
                    Salt = Salt(),
                    Scope = _options.Redaction.Scope,
                    KeepStructural = _options.Redaction.KeepStructural,
                },
            },
            ct).ConfigureAwait(false);
        if (names is null)
        {
            return redacted;
        }

        return new RedactedSql
        {
            Sql = ParameterNames.Substitute(redacted.Sql, names, indexed: false),
            Parsed = redacted.Parsed,
            StructuralHash = redacted.StructuralHash,
        };
    }

    /// <summary>
    /// How many columns this table has, when this client's own catalog says it carries an
    /// entitlement, and null when it does not — the one question I-IR-E asks of the catalog.
    /// </summary>
    internal int? EntitledColumnCount(Chalk.Ir.TableRef table)
    {
        foreach (var schema in Catalog.Schemas)
        {
            if (!string.Equals(schema.SourceId, table.SourceId, StringComparison.Ordinal)
                || !string.Equals(schema.Name, table.Schema, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var declared in schema.Tables)
            {
                if (string.Equals(declared.Name, table.Table, StringComparison.OrdinalIgnoreCase))
                {
                    return declared.Entitlement is null ? null : declared.Columns.Count;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The catalog with the host's cross-source join policy in it (D104). Called at engine creation
    /// and on every refresh — and given the catalog it is about to describe, so a policy may read
    /// the sources it will be planning joins between. A null policy leaves the shipped default,
    /// which is adaptive everywhere.
    /// </summary>
    private static CatalogContext WithJoinPolicy(CatalogContext catalog, ICrossSourceJoinPolicy? policy)
    {
        if (policy is null)
        {
            return catalog;
        }

        var built = policy.Build(catalog)
            ?? throw new InvalidOperationException(
                $"{policy.GetType().Name}.Build returned null; a cross-source join policy must "
                + "return a descriptor, and CrossSourceJoinPolicy.Default is the shipped one.");
        return new CatalogContext
        {
            ContextId = catalog.ContextId,
            Epoch = catalog.Epoch,
            Schemas = catalog.Schemas,
            Associations = catalog.Associations,
            JoinPolicy = built,
        };
    }

    /// <summary>Compiles a plan against the live catalog and sources.</summary>
    internal CompiledPlan Compile(Plan plan)
    {
        RequireCurrentCatalog(plan);
        return PlanCompiler.Compile(plan, Catalog, _sources, _settings);
    }

    /// <summary>
    /// The same, with each pushed query's text redacted first when this prepare asked for that
    /// (D262). One round trip per distinct pushed query, at prepare and never at execution: the
    /// failure path quotes what was computed here and makes no call of its own.
    /// </summary>
    internal async ValueTask<CompiledPlan> CompileAsync(
        Plan plan, PrepareOptions options, RequestContext? context, CancellationToken ct)
    {
        RequireCurrentCatalog(plan);
        if (RedactionFor(options) is not { } redaction)
        {
            return PlanCompiler.Compile(plan, Catalog, _sources, _settings);
        }

        Dictionary<string, string>? redacted = null;
        foreach (var text in PushedQueryTexts(plan))
        {
            redacted ??= new Dictionary<string, string>(StringComparer.Ordinal);
            if (redacted.ContainsKey(text))
            {
                continue;
            }

            var result = await _options.Planner.RedactSqlAsync(
                new RedactSqlRequest
                {
                    Sql = text,
                    Conformance = options.Conformance,
                    Redaction = redaction,
                    // A folded value in the pushed text is labelled with the name it was bound
                    // under, which the sidecar can only do from the context that folded it (D286).
                    Context = context,
                },
                ct).ConfigureAwait(false);
            redacted[text] = result.Sql;
        }

        return PlanCompiler.Compile(plan, Catalog, _sources, _settings, redacted);
    }

    /// <summary>
    /// Every pushed query text in the plan, which is what a source failure quotes (D262). One case
    /// covers them all: a lookup join's own query is a <c>RemoteQuery</c> subtree, and
    /// <see cref="PlanWalker"/> descends into both its branches. Empty for a plan that pushes
    /// nothing, which is every plan over a single in-process source.
    /// </summary>
    private static IEnumerable<string> PushedQueryTexts(Plan plan)
    {
        foreach (var rel in PlanWalker.Rels(plan))
        {
            if (rel.KindCase == Rel.KindOneofCase.RemoteQuery
                && rel.RemoteQuery.QueryText.Length > 0)
            {
                yield return rel.RemoteQuery.QueryText;
            }
        }
    }

    /// <summary>
    /// A stale plan run against a changed schema is a wrong-answer bug, so this is checked before
    /// every compilation and every execution.
    /// </summary>
    internal void RequireCurrentCatalog(Plan plan) => RequireCurrentCatalog(plan, TablesRead(plan));

    /// <summary>
    /// The same, given the tables this plan reads, which a <see cref="PreparedQuery"/> collected once
    /// when it was prepared rather than on every execution.
    /// </summary>
    internal void RequireCurrentCatalog(Plan plan, IReadOnlyList<TableKey>? tables)
    {
        if (!string.Equals(plan.ContextId, Catalog.ContextId, StringComparison.Ordinal))
        {
            throw new StalePlanException(
                plan.ContextId, plan.CatalogEpoch, Catalog.ContextId, Catalog.Epoch);
        }

        if (tables is null)
        {
            // No honest list of tables (a pushed query whose subtree is absent): the whole catalog's
            // shape epoch decides, as it did before D271 (d). A shared source refreshed by another
            // engine is conservatively stale until this engine has re-described it.
            if (!SharedShapesCurrent() || plan.CatalogEpoch < ShapeEpoch)
            {
                throw new StalePlanException(
                    plan.ContextId, plan.CatalogEpoch, Catalog.ContextId, Catalog.Epoch);
            }

            return;
        }

        if (StaleTable(plan, tables) is { } stale)
        {
            throw new StalePlanException(
                plan.ContextId,
                plan.CatalogEpoch,
                Catalog.ContextId,
                Catalog.Epoch,
                $"{stale.Schema}.{stale.Table}");
        }
    }

    /// <summary>
    /// Whether a plan is still one this engine may run: the same catalog, and no table <em>it
    /// reads</em> changed shape since it was planned (D260 §2, per table since D271 (d)). A refresh
    /// that replaced rows or moved statistics leaves every shape where it was, and a refresh that
    /// changed one table's shape strands only the plans that read that table — the compiled plan
    /// holds column positions and source references for the tables it reads, and nothing else.
    /// </summary>
    internal bool IsCurrent(Plan plan) => IsCurrent(plan, TablesRead(plan));

    /// <inheritdoc cref="IsCurrent(Plan)"/>
    internal bool IsCurrent(Plan plan, IReadOnlyList<TableKey>? tables) =>
        string.Equals(plan.ContextId, Catalog.ContextId, StringComparison.Ordinal)
        && (tables is null
            ? SharedShapesCurrent() && plan.CatalogEpoch >= ShapeEpoch
            : StaleTable(plan, tables) is null);

    /// <summary>
    /// The first table this plan reads whose shape has moved since it was planned, or null when none
    /// has. A table the catalog no longer declares counts as moved: it cannot be scanned at all.
    /// </summary>
    private TableKey? StaleTable(Plan plan, IReadOnlyList<TableKey> tables)
    {
        var epochs = Volatile.Read(ref _tableShapeEpochs);
        var live = Catalog;
        for (var i = 0; i < tables.Count; i++)
        {
            var table = tables[i];
            if (!SharedShapeCurrent(table.SourceId))
            {
                return table;
            }

            if (epochs.TryGetValue(table, out var changedAt))
            {
                if (changedAt > plan.CatalogEpoch)
                {
                    return table;
                }

                continue;
            }

            // Not in the map: either it has never changed shape, or the catalog has dropped it.
            if (!Declares(live, table))
            {
                return table;
            }
        }

        return null;
    }

    private static bool Declares(CatalogContext catalog, TableKey table)
    {
        foreach (var schema in catalog.Schemas)
        {
            if (!string.Equals(schema.SourceId, table.SourceId, StringComparison.Ordinal)
                || !string.Equals(schema.Name, table.Schema, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var declared in schema.Tables)
            {
                if (string.Equals(declared.Name, table.Table, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Every table this plan reads, from its reads and index lookups and from the ones inside a
    /// pushed <c>RemoteQuery</c> — which <see cref="PlanWalker"/> stops at, because a pushed subtree
    /// is not an input. Collected once, when the statement is prepared, because it is what every
    /// staleness question then reads.
    /// </summary>
    /// <returns>
    /// Null when the plan holds a pushed query whose subtree it does not carry, and there is
    /// therefore no honest list of the tables it reads. Staleness then falls back to the whole
    /// catalog's shape epoch, which is what it was before D271 — conservative in the only direction
    /// that is safe.
    /// </returns>
    internal static IReadOnlyList<TableKey>? TablesRead(Plan plan)
    {
        List<TableKey> tables = [];
        return Collect(plan.Root, tables) ? tables : null;
    }

    private static bool Collect(Rel? root, List<TableKey> tables)
    {
        if (root is null)
        {
            return true;
        }

        // The inclusive walk: a table read only under a pushed query is read by this statement, and
        // its staleness is this statement's (F92, ADR 0057 §4). A remote query that carries no
        // pushed plan is one whose reads cannot be enumerated at all, and the answer is "unknown"
        // rather than "none" — which is what the null return means to the caller.
        foreach (var rel in PlanWalker.Rels(root))
        {
            switch (rel.KindCase)
            {
                case Rel.KindOneofCase.Read:
                    Add(tables, rel.Read.Table);
                    break;
                case Rel.KindOneofCase.IndexLookup:
                    Add(tables, rel.IndexLookup.Table);
                    break;
                case Rel.KindOneofCase.RemoteQuery:
                    if (rel.RemoteQuery.PushedPlan is null)
                    {
                        return false;
                    }

                    break;
                default:
                    break;
            }
        }

        return true;
    }

    private static void Add(List<TableKey> tables, TableRef? table)
    {
        if (table is null)
        {
            return;
        }

        var key = new TableKey(table.SourceId, table.Schema, table.Table);
        if (!tables.Contains(key))
        {
            tables.Add(key);
        }
    }

    private static void RequireNamed(PreparedQuery query)
    {
        if (query.ParameterStyle is not (ParameterStyle.Named or ParameterStyle.None))
        {
            throw new ArgumentException(
                $"this statement uses {query.ParameterStyle} parameters; bind them with a list, not by name");
        }
    }

    [LoggerMessage(
        LogLevel.Information,
        "chalk engine ready: {Sources} sources, batch size {BatchSize}, {Engine} engine, {Output} output; " +
        "{ArenaPoolSize} pooled arenas x {RetainBytes} retained bytes = at most {WorstCaseBytes} bytes " +
        "held between executions, {Budget} per execution")]
    partial void LogChalkEngineReady(
        int sources,
        int batchSize,
        ExecutionEngine engine,
        OutputMemory output,
        int arenaPoolSize,
        long retainBytes,
        long worstCaseBytes,
        string budget);
}

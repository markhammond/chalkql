using Chalk.Catalog;
using Chalk.Entitlements;
using CatalogContext = Chalk.Catalog.CatalogContext;
using Chalk.Client.Rpc;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Chalk.Client;

/// <summary>How to reach the planner sidecar.</summary>
public sealed class GrpcPlannerOptions
{
    /// <summary>
    /// <c>http://host:port</c> or <c>https://…</c> for a sidecar reached over TCP, or
    /// <c>unix:///absolute/path</c> for one on the same machine
    /// (docs/design/09-unix-socket-transport.md §4). A relative <c>unix:</c> path is rejected when
    /// the planner is constructed.
    /// </summary>
    public required Uri Address { get; init; }

    /// <summary>Applied to every call. Planning is tens of milliseconds; this is a backstop, not a budget.</summary>
    public TimeSpan Deadline { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Catalogs and plans can both be large.</summary>
    public int MaxReceiveMessageSizeMb { get; init; } = 64;

    /// <summary>
    /// How deep the plan message the sidecar returns may nest (F102). Protobuf's parser guards its
    /// own stack with a recursion limit of 100 levels, which an entirely unbound prepare over a wide
    /// policy — several tenancy kinds, a subject, many roles, D266's confinement pairs — can exceed
    /// in the predicate the pass writes. The limit is applied to the plan response alone; every
    /// other message keeps protobuf's default. Must be at least 1; unbounded is not offered, because
    /// the limit is what keeps a hostile message from overflowing the parser's stack.
    /// </summary>
    public int PlanNestingLimit { get; init; } = 1000;

    public ILoggerFactory? LoggerFactory { get; init; }
}

/// <summary>
/// The production transport (D8): plaintext HTTP/2 to a co-located sidecar, over TCP or over a Unix
/// domain socket (D33). Maps the <c>chalk-plan-error-bin</c> trailer back into
/// <see cref="PlanningException"/> so a host sees the planner's own message and SQL position rather
/// than a gRPC status code (D25).
/// </summary>
public sealed class GrpcQueryPlanner : IQueryPlanner
{
    private static readonly Metadata.Entry[] NoTrailers = [];

    /// <summary>
    /// The plan call with a response marshaller of this planner's own, so that the nesting limit is
    /// <see cref="GrpcPlannerOptions.PlanNestingLimit"/> rather than protobuf's default (F102). The
    /// request side is the generated marshaller's pattern verbatim.
    /// </summary>
    private readonly Method<Rpc.PlanRequest, Rpc.PlanResponse> _planMethod;
    private readonly CallInvoker _invoker;

    private readonly GrpcChannel _channel;
    private readonly SocketsHttpHandler? _handler;
    private readonly PlannerService.PlannerServiceClient _client;
    private readonly GrpcPlannerOptions _options;
    private readonly ILogger _log;

    public GrpcQueryPlanner(GrpcPlannerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _log = (options.LoggerFactory ?? NullLoggerFactory.Instance).CreateLogger<GrpcQueryPlanner>();

        var channelOptions = new GrpcChannelOptions
        {
            MaxReceiveMessageSize = options.MaxReceiveMessageSizeMb * 1024 * 1024,
            LoggerFactory = options.LoggerFactory,
        };

        if (PlannerAddress.IsUnix(options.Address))
        {
            // The socket path is checked here rather than at the first call: a relative path is a
            // configuration mistake, and it should not look like the sidecar is down.
            var path = PlannerAddress.RequireSocketPath(options.Address, nameof(options));
            _handler = PlannerAddress.UnixSocketHandler(path);
            channelOptions.HttpHandler = _handler;
            channelOptions.DisposeHttpClient = false;
            _channel = GrpcChannel.ForAddress(PlannerAddress.UnixChannelAuthority, channelOptions);
        }
        else
        {
            _channel = GrpcChannel.ForAddress(options.Address, channelOptions);
        }

        _client = new PlannerService.PlannerServiceClient(_channel);
        if (options.PlanNestingLimit < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), options.PlanNestingLimit, "PlanNestingLimit must be at least 1.");
        }

        var limit = options.PlanNestingLimit;
        _planMethod = new Method<Rpc.PlanRequest, Rpc.PlanResponse>(
            MethodType.Unary,
            "chalk.v1.PlannerService",
            "Plan",
            Marshallers.Create<Rpc.PlanRequest>(
                (message, context) =>
                {
                    context.SetPayloadLength(message.CalculateSize());
                    Google.Protobuf.MessageExtensions.WriteTo(message, context.GetBufferWriter());
                    context.Complete();
                },
                context => Rpc.PlanRequest.Parser.ParseFrom(context.PayloadAsReadOnlySequence())),
            Marshallers.Create<Rpc.PlanResponse>(
                (message, context) => context.Complete(
                    Google.Protobuf.MessageExtensions.ToByteArray(message)),
                context => ParsePlanResponse(context.PayloadAsNewBuffer(), limit)));
        _invoker = _channel.CreateCallInvoker();
    }

    /// <summary>
    /// Parses a plan response under a nesting limit of the caller's choosing (F102). Protobuf's
    /// <c>CodedInputStream</c> is the one reader that takes a limit; the generated marshaller reads
    /// through a parse context that has none to set.
    /// </summary>
    internal static Rpc.PlanResponse ParsePlanResponse(byte[] payload, int nestingLimit)
    {
        var response = new Rpc.PlanResponse();
        using var stream = new MemoryStream(payload, writable: false);
        var input = Google.Protobuf.CodedInputStream.CreateWithLimits(
            stream, sizeLimit: int.MaxValue, recursionLimit: nestingLimit);
        try
        {
            response.MergeFrom(input);
        }
        catch (Google.Protobuf.InvalidProtocolBufferException e)
            when (e.Message.Contains("nesting", StringComparison.OrdinalIgnoreCase))
        {
            throw new PlanningException(
                PlanErrorKind.Internal,
                $"the plan the planner returned nests deeper than this client's limit of "
                + $"{nestingLimit} levels (GrpcPlannerOptions.PlanNestingLimit). An entirely unbound "
                + "prepare over a wide policy writes a deep predicate: bind the axes every principal "
                + "binds alike with Shape(names), or raise the limit.",
                position: null,
                e);
        }

        return response;
    }

    /// <summary>The address this planner talks to, for diagnostics.</summary>
    public Uri Address => _options.Address;

    public async ValueTask<PlannerInfo> GetInfoAsync(CancellationToken ct = default)
    {
        // GetInfo and RegisterCatalog are idempotent, so one retry on UNAVAILABLE covers a sidecar
        // that is still binding its port. Plan is never retried in M1: it is not free and the caller
        // is better placed to decide.
        var response = await CallAsync(
            deadline => _client.GetInfoAsync(new GetInfoRequest(), deadline: deadline, cancellationToken: ct),
            retryOnUnavailable: true).ConfigureAwait(false);

        return new PlannerInfo
        {
            MinIrVersion = response.MinIrVersion,
            MaxIrVersion = response.MaxIrVersion,
            PlannerVersion = response.PlannerVersion,
            CalciteVersion = response.CalciteVersion,
            PlannerConfigHash = response.PlannerConfigHash,
            Dialects = [.. response.Dialects.Select(d => new DialectInfo
            {
                Name = d.Name,
                DatabaseProduct = d.DatabaseProduct,
                Tuned = d.Tuned,
                Aliases = [.. d.Aliases],
            })],
            // A wire value this build has never heard of — a newer sidecar's enum grew — is
            // skipped rather than thrown, so an older client survives it (D250). Cast first and
            // filter on the client's own enum, not the wire one: what "known" means here is
            // whether *this* SqlConformance/SqlLibrary has a member for it.
            Conformances = [.. response.Conformances.Select(c => (SqlConformance)c).Where(Enum.IsDefined)],
            Libraries = [.. response.Libraries.Select(l => (SqlLibrary)l).Where(Enum.IsDefined)],
            PlanningWorkers = response.PlanningWorkers,
        };
    }

    public async ValueTask RegisterCatalogAsync(
        CatalogRegistration registration, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(registration);
        var catalog = registration.Catalog;
        var request = new RegisterCatalogRequest
        {
            Catalog = catalog.ToProto(),
            InstanceId = registration.InstanceId,
            ShapeVersion = registration.ShapeVersion,
            BaseShapeVersion = registration.BaseShapeVersion,
        };
        request.RemovedTables.AddRange(registration.RemovedTables);

        // The shape and nothing but the shape (D271 (c)): the numbers travel in RegisterStatistics,
        // so a registration carries no row count and no column statistics and a data-only refresh
        // never sends this message at all. Stripped here rather than by the caller, because this is
        // where the wire form is built and the rule is a property of the wire.
        //
        // Only for a registration that names a version, which is every registration an engine makes.
        // A caller that names none is saying "here is the whole catalog" — a test, the corpus
        // recorder — and has no statistics message to follow it with, so its numbers stay inline and
        // the planner reads them from the descriptor exactly as it always did.
        if (registration.ShapeVersion.Length > 0)
        {
            StripStatistics(request.Catalog);
        }

        await CallAsync(
            deadline => _client.RegisterCatalogAsync(request, deadline: deadline, cancellationToken: ct),
            retryOnUnavailable: true).ConfigureAwait(false);

        _log.LogDebug(
            "registered catalog {ContextId} shape {ShapeVersion} ({Kind}) with {SchemaCount} schemas",
            catalog.ContextId,
            registration.ShapeVersion,
            registration.BaseShapeVersion.Length == 0
                ? "whole"
                : $"delta over {registration.BaseShapeVersion}",
            catalog.Schemas.Count);
    }

    public async ValueTask RegisterStatisticsAsync(
        StatisticsRegistration statistics, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(statistics);
        var request = new RegisterStatisticsRequest
        {
            InstanceId = statistics.InstanceId,
            StatisticsVersion = statistics.StatisticsVersion,
        };

        foreach (var table in statistics.Tables)
        {
            var message = new Rpc.TableStatistics
            {
                Schema = table.Schema,
                Table = table.Table,
                RowCount = table.RowCount,
                RowCountKind = table.RowCountKind,
            };
            foreach (var column in table.Columns)
            {
                message.Columns.Add(new ColumnStatisticsEntry
                {
                    Column = column.Column,
                    Statistics = column.Statistics,
                });
            }

            request.Tables.Add(message);
        }

        await CallAsync(
            deadline => _client.RegisterStatisticsAsync(request, deadline: deadline, cancellationToken: ct),
            retryOnUnavailable: true).ConfigureAwait(false);

        _log.LogDebug(
            "registered statistics {Version} for {Tables} tables",
            statistics.StatisticsVersion,
            statistics.Tables.Count);
    }

    /// <summary>
    /// Strikes every table's numbers out of the wire catalog (D271 (c)). What is left is the shape,
    /// which is what a shape version installs; the numbers arrive on their own version, and the
    /// planner joins the two.
    /// </summary>
    /// <remarks>
    /// The row count is struck out to <c>-1</c> and not to zero, because zero is a row count and
    /// <c>-1</c> is "unknown": a shape registered before its statistics arrived must cost as if
    /// nothing were known about it, not as if every table were empty.
    /// </remarks>
    private static void StripStatistics(Chalk.Ir.CatalogContext message)
    {
        foreach (var schema in message.Schemas)
        {
            foreach (var table in schema.Tables)
            {
                table.RowCount = -1;
                table.RowCountKind = Chalk.Ir.RowCountKind.Unspecified;
                foreach (var column in table.Columns)
                {
                    column.Statistics = null;
                }
            }
        }
    }

    public async ValueTask<PlanResult> PlanAsync(PlanRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The wire enum is this one's twin, value for value (D34), so the map is a cast — but a
        // value that is not one of ours would travel as a number the planner then refuses, and the
        // caller is better served by hearing about it here.
        if (!Enum.IsDefined(request.Options.Conformance))
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                request.Options.Conformance,
                "PlannerOptions.Conformance is not a SqlConformance value");
        }

        if (!Enum.IsDefined(request.Options.Pushdown))
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                request.Options.Pushdown,
                "PlannerOptions.Pushdown is not a PushdownLevel value");
        }

        foreach (var library in request.Options.Libraries)
        {
            if (!Enum.IsDefined(library))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(request), library, "PlannerOptions.Libraries holds a value that is not a SqlLibrary");
            }
        }

        foreach (var capability in request.Options.DisabledCapabilities)
        {
            if (!Enum.IsDefined(capability) || capability == DisabledCapability.Unspecified)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(request),
                    capability,
                    "PlannerOptions.DisabledCapabilities holds a value that is not a capability");
            }
        }

        if (request.Planning is { } declaredPriority && !Enum.IsDefined(declaredPriority.Priority))
        {
            throw new ArgumentOutOfRangeException(
                nameof(request), declaredPriority.Priority, "PlanningOptions.Priority is not a PlanningPriority value");
        }

        var message = new Rpc.PlanRequest
        {
            Sql = request.Sql,
            ContextId = request.ContextId,
            CatalogEpoch = request.CatalogEpoch,
            StatsEpoch = request.StatsEpoch,
            InstanceId = request.InstanceId,
            // A caller that names no version is addressing the catalog it registered under this
            // epoch, which the planner files under the epoch's own spelling — the pre-D271 contract,
            // preserved exactly. Sending the epoch rather than nothing matters when a sidecar is
            // shared: "nothing" means "the newest shape of this instance", and the newest may belong
            // to another client that named the same context id.
            ShapeVersion = request.ShapeVersion.Length > 0
                ? request.ShapeVersion
                : request.CatalogEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture),
            StatisticsVersion = request.StatisticsVersion,
            ClientIrVersion = request.ClientIrVersion,
            NarrowFrom = request.NarrowFrom,
            Options = new Rpc.PlannerOptions
            {
                Pushdown = (Rpc.PushdownLevel)request.Options.Pushdown,
                IncludePlanText = request.Options.IncludePlanText,
                DisableIndexLookup = request.Options.DisableIndexLookup,
                Conformance = (Chalk.Ir.SqlConformance)request.Options.Conformance,
            },
        };
        message.Options.Libraries.AddRange(
            request.Options.Libraries.Select(l => (Chalk.Ir.SqlLibrary)l));
        message.Options.DisabledCapabilities.AddRange(
            request.Options.DisabledCapabilities.Select(c => (Rpc.DisabledCapability)c));
        if (request.Options.JoinPolicy is { } joinPolicy)
        {
            message.Options.JoinPolicy = Chalk.Catalog.CatalogSerialization.ToProto(joinPolicy);
        }

        // Into the request's one extension slot (step 26c, D212). A client that attached none sends
        // no bytes here at all, which is what "zero cost when unused" means on this side of the
        // call; an extension the planner has no handler for is refused there rather than ignored.
        foreach (var extension in request.Options.Extensions)
        {
            message.Extensions.Add(Google.Protobuf.WellKnownTypes.Any.Pack(extension));
        }

        // Opt-in, and nothing at all when nobody asked (D262): a request with no redaction sends no
        // message here, and the sidecar runs no visitor and derives no seed.
        if (request.Redaction is { } redaction)
        {
            message.Redaction = ToProto(redaction);
        }

        message.ParameterTypes.AddRange(request.ParameterTypes.Select(t => t.ToProto()));
        if (request.Context?.ToProto() is { } context)
        {
            message.Context = context;
        }

        // Only for a request that can actually be ended early, or that names a priority or a
        // session, or that can be stopped by id (D235, D241, D245): a request that sets none of
        // these sends no bytes here, and the sidecar installs no listener and samples no cost —
        // beyond what D240's scheduler installs for every planning regardless of what the request
        // itself asked for.
        var planning = request.Planning;
        var stoppable = request.PlanningRequestId.Length > 0;
        if (planning is not null
            && (!planning.RunsToCompletion
                || stoppable
                || planning.Priority != PlanningPriority.Normal
                || planning.Session is not null))
        {
            message.PlanningOptions = new Rpc.PlanningOptions
            {
                TimeBudgetMs = planning.TimeBudget is { } budget && budget > TimeSpan.Zero
                    ? (ulong)Math.Max(1, (long)budget.TotalMilliseconds)
                    : 0UL,
                ConvergencePatience = (uint)Math.Max(0, planning.ConvergencePatience),
                ConvergenceRangeThreshold = Math.Max(0, planning.ConvergenceRangeThreshold),
                ConvergenceEvaluationInterval = (uint)Math.Max(0, planning.ConvergenceEvaluationInterval ?? 0),
                // 0 = the sidecar's own default (D239); ignored there whenever the interval above is
                // set, because the count is the reproducible mode and takes over.
                ConvergenceSamplePeriodMs = planning.ConvergenceSamplePeriod is { } period && period > TimeSpan.Zero
                    ? (ulong)Math.Max(1, (long)period.TotalMilliseconds)
                    : 0UL,
                // UNSPECIFIED reads as normal on the sidecar, so this is just the cast (D241).
                Priority = (Rpc.PlanningPriority)planning.Priority,
                // Empty runs uncapped and outside every session (D245); the sidecar ignores the cap
                // when the name is empty, so there is nothing to guard here.
                SessionName = planning.Session?.Name ?? string.Empty,
                SessionMaxConcurrency = (uint)Math.Max(0, planning.Session?.MaxConcurrency ?? 0),
                RequestId = request.PlanningRequestId,
                // The host cancelled the stop token before this prepare even began. A StopPlanning
                // call cannot reach a planning that does not exist yet, so it travels on the
                // request instead (D236).
                StopAtFirstPlan = planning.StopToken.IsCancellationRequested,
            };
        }

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        Rpc.PlanResponse response;
        try
        {
            response = await CallAsync(
                deadline => _invoker.AsyncUnaryCall(
                    _planMethod,
                    host: null,
                    new CallOptions(deadline: deadline, cancellationToken: ct),
                    message),
                retryOnUnavailable: false).ConfigureAwait(false);
        }
        catch (PlanningException e)
            when (ct.IsCancellationRequested
                && e.InnerException is RpcException { StatusCode: StatusCode.Cancelled })
        {
            // The host cancelled the prepare (D236). The sidecar sees the same cancellation and
            // stops planning; what it cannot do is answer, because the call is gone — so the state
            // the caller gets is the client's own.
            throw new PlanningCancelledException(
                new PlanningState
                {
                    TerminationReason = PlanningTerminationReason.CancelledByHost,
                    Elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started),
                },
                ct,
                e);
        }

        return new PlanResult
        {
            Plan = response.Plan,
            PlanText = response.PlanText.Length == 0 ? null : response.PlanText,
            RedactedSql = response.RedactedSql.Length == 0 ? null : response.RedactedSql,
            Stats = response.Stats ?? new PlanningStats(),
            PlanningState = PlanningStates.FromProto(response.PlanningState),
            Extensions = response.Extensions.Count == 0 ? [] : [.. response.Extensions],
        };
    }

    /// <summary>
    /// Redacts one statement (D262). Its own call, because the text it serves is text that was never
    /// prepared — above all text that failed to parse, which has no plan to carry a redaction on.
    /// </summary>
    public async ValueTask<RedactedSql> RedactSqlAsync(
        RedactSqlRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(request.Conformance))
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                request.Conformance,
                "RedactSqlRequest.Conformance is not a SqlConformance value");
        }

        var message = new Rpc.RedactSqlRequest
        {
            Sql = request.Sql,
            Conformance = (Chalk.Ir.SqlConformance)request.Conformance,
            Redaction = ToProto(request.Redaction),
        };
        if (request.Context?.ToProto() is { } context)
        {
            message.Context = context;
        }

        var response = await CallAsync(
            deadline => _client.RedactSqlAsync(message, deadline: deadline, cancellationToken: ct),
            retryOnUnavailable: true).ConfigureAwait(false);

        return new RedactedSql
        {
            Sql = response.RedactedSql,
            Parsed = response.Parsed,
            StructuralHash = Convert.ToHexStringLower(response.StructuralHash.Span),
        };
    }

    /// <summary>
    /// The redaction on the wire. The salt travels because the pseudonym must be the same whichever
    /// side computes it; the sidecar never logs it, and nothing here does either.
    /// </summary>
    private static Rpc.RedactionOptions ToProto(RedactionRequest redaction)
    {
        if (!Enum.IsDefined(redaction.Scope))
        {
            throw new ArgumentOutOfRangeException(
                nameof(redaction), redaction.Scope, "RedactionRequest.Scope is not a RedactionScope value");
        }

        return new Rpc.RedactionOptions
        {
            Salt = Google.Protobuf.ByteString.CopyFrom(redaction.Salt.Span),
            Scope = (Rpc.RedactionScope)redaction.Scope,
            KeepStructural = redaction.KeepStructural,
        };
    }

    /// <summary>
    /// Asks the sidecar for the best complete plan the planning named by <paramref name="requestId"/>
    /// has (D236). False when nothing is in flight under that id.
    /// </summary>
    /// <remarks>
    /// Its own call, on its own connection to the same channel, because <c>Plan</c> is unary and
    /// blocks its own stream: a stop has to arrive on a second one. Never retried, and never
    /// given the caller's own deadline — a stop that arrives late has simply lost a race with the
    /// planning it meant to shorten, and that is not an error.
    /// </remarks>
    public async ValueTask<bool> StopPlanningAsync(string requestId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(requestId);
        try
        {
            var response = await _client
                .StopPlanningAsync(
                    new StopPlanningRequest { RequestId = requestId },
                    deadline: DateTime.UtcNow.Add(_options.Deadline),
                    cancellationToken: ct)
                .ResponseAsync
                .ConfigureAwait(false);
            return response.Found;
        }
        catch (RpcException e)
        {
            _log.LogDebug("stop for planning {RequestId} did not reach the planner: {Status}", requestId, e.StatusCode);
            return false;
        }
    }

    /// <summary>
    /// The sidecar's evaluation count for a planning, live or last recorded — the diagnostic
    /// <c>StopPlanningResponse</c> carries beside <c>found</c>. Diagnostics only, and a stop as well
    /// as a read: use it on a planning that has already finished, where there is nothing to stop.
    /// </summary>
    internal async ValueTask<ulong> PlanningEvaluationsAsync(string requestId, CancellationToken ct = default)
    {
        var response = await _client
            .StopPlanningAsync(
                new StopPlanningRequest { RequestId = requestId },
                deadline: DateTime.UtcNow.Add(_options.Deadline),
                cancellationToken: ct)
            .ResponseAsync
            .ConfigureAwait(false);
        return response.EvaluationCount;
    }

    public async ValueTask DisposeAsync()
    {
        await _channel.ShutdownAsync().ConfigureAwait(false);
        _channel.Dispose();
        _handler?.Dispose();
    }

    private async Task<T> CallAsync<T>(Func<DateTime?, AsyncUnaryCall<T>> call, bool retryOnUnavailable)
    {
        try
        {
            using var first = call(DateTime.UtcNow.Add(_options.Deadline));
            return await first.ResponseAsync.ConfigureAwait(false);
        }
        catch (RpcException e) when (retryOnUnavailable && e.StatusCode == StatusCode.Unavailable)
        {
            _log.LogDebug("planner at {Address} was unavailable; retrying once", _options.Address);
            try
            {
                using var second = call(DateTime.UtcNow.Add(_options.Deadline));
                return await second.ResponseAsync.ConfigureAwait(false);
            }
            catch (RpcException retry)
            {
                throw Translate(retry);
            }
        }
        catch (RpcException e)
        {
            throw Translate(e);
        }
    }

    private Exception Translate(RpcException error)
    {
        // A plan the client could not read is a plan-shaped failure, never unavailability (F102):
        // the marshaller's own exception travels as the status's debug exception.
        if (error.Status.DebugException is PlanningException planning)
        {
            return planning;
        }

        if (error.Status.DebugException is Google.Protobuf.InvalidProtocolBufferException parse
            || error.Status.Detail.Contains("levels of nesting", StringComparison.OrdinalIgnoreCase))
        {
            return new PlanningException(
                PlanErrorKind.Internal,
                "the plan the planner returned could not be read by this client: "
                + (error.Status.DebugException?.Message ?? error.Status.Detail)
                + " (GrpcPlannerOptions.PlanNestingLimit bounds how deep a plan may nest)",
                position: null,
                error);
        }

        if (error.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded)
        {
            return new PlannerUnavailableException(
                _options.Address.ToString(),
                error.StatusCode == StatusCode.DeadlineExceeded
                    ? $"the call did not complete within {_options.Deadline}"
                    : error.Status.Detail,
                error);
        }

        var planError = DecodeTrailer(error);
        if (planError is null)
        {
            // No trailer: the failure came from the transport or from something in front of the
            // service, so there is nothing more specific to say than what gRPC reported.
            return new PlanningException(
                PlanErrorKind.Internal,
                $"{error.StatusCode}: {error.Status.Detail}",
                position: null,
                error);
        }

        SqlPosition? position = planError.Line > 0
            ? new SqlPosition(planError.Line, planError.Column, planError.EndLine, planError.EndColumn)
            : null;
        if (planError.Kind == PlanErrorKind.Policy)
        {
            return new EntitlementException(planError.Message, position, error);
        }

        // A search this request's own options ended before there was a plan (D235). The state is
        // the whole of what the caller can act on, so it travels on the exception rather than only
        // in the message.
        return planError.Kind == PlanErrorKind.PlanningAborted
            ? new PlanningException(
                planError.Kind,
                planError.Message,
                position,
                PlanningStates.FromProto(planError.PlanningState),
                error)
            : new PlanningException(planError.Kind, planError.Message, position, error);
    }

    /// <summary>Reads the <c>chalk-plan-error-bin</c> trailer, if the server sent one.</summary>
    internal static PlanError? DecodeTrailer(RpcException error)
    {
        var trailers = error.Trailers ?? [.. NoTrailers];
        var entry = trailers.FirstOrDefault(
            t => t.IsBinary && string.Equals(t.Key, "chalk-plan-error-bin", StringComparison.Ordinal));
        if (entry is null)
        {
            return null;
        }

        try
        {
            return PlanError.Parser.ParseFrom(entry.ValueBytes);
        }
        catch (Google.Protobuf.InvalidProtocolBufferException)
        {
            // A trailer we cannot parse is worse than none: fall back to the status text rather than
            // failing while reporting a failure.
            return null;
        }
    }
}

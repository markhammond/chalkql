using System.Globalization;
using Chalk.Client.Rpc;

namespace Chalk.Client;

/// <summary>A 1-based position in the submitted SQL. Zero line and column mean "not known".</summary>
public readonly record struct SqlPosition(int Line, int Column, int EndLine, int EndColumn)
{
    public override string ToString()
    {
        var start = string.Create(CultureInfo.InvariantCulture, $"line {Line}, column {Column}");
        return EndLine > 0 && (EndLine != Line || EndColumn != Column)
            ? start + string.Create(CultureInfo.InvariantCulture, $" to line {EndLine}, column {EndColumn}")
            : start;
    }
}

/// <summary>
/// The planner refused the statement: a parse error, a validation error, or valid SQL this build
/// cannot plan. The message is the planner's own, with the SQL position when it knew one, and each
/// violation carries the code and kind the planner raised it with.
/// </summary>
public sealed class PlanningException : ChalkException
{
    /// <summary>A planner's error that breaks the rule <paramref name="code"/>, of the kind <paramref name="kind"/>.</summary>
    public PlanningException(
        string code,
        string kind,
        string message,
        SqlPosition? position,
        PlanningState? planningState = null,
        Exception? innerException = null)
        : this([new ChalkViolation(code, message, kind)], message, position, planningState, innerException)
    {
    }

    /// <summary>
    /// A planner's error that breaks every rule in <paramref name="violations"/>, in the order the
    /// planner met them, worded by <paramref name="message"/>, the first one's. The state is what the
    /// optimiser had measured when it gave up (D235), set where the kind is
    /// <see cref="PlanErrorKinds.PlanningAborted"/> and the state is the whole of the answer.
    /// </summary>
    public PlanningException(
        IReadOnlyList<ChalkViolation> violations,
        string message,
        SqlPosition? position,
        PlanningState? planningState,
        Exception? innerException)
        : base(violations, Compose(violations.Count > 0 ? violations[0].Kind : null, message, position), innerException)
    {
        Position = position;
        PlanningState = planningState;
    }

    /// <summary>
    /// What kind of problem this is: the first violation's, one of <see cref="PlanErrorKinds"/>, or
    /// null when the planner named none.
    /// </summary>
    public string? Kind => Violations.Count > 0 ? Violations[0].Kind : null;

    /// <summary>Where in the SQL, when the planner reported it.</summary>
    public SqlPosition? Position { get; }

    /// <summary>
    /// How the optimiser ended, for a failure that is about the search rather than the statement
    /// (D237). Null for every other kind.
    /// </summary>
    public PlanningState? PlanningState { get; }

    private static string Compose(string? kind, string message, SqlPosition? position) =>
        position is { Line: > 0 }
            ? $"{Describe(kind)} at {position}: {message}"
            : $"{Describe(kind)}: {message}";

    private static string Describe(string? kind) => kind switch
    {
        PlanErrorKinds.Parse => "SQL parse error",
        PlanErrorKinds.Validation => "SQL validation error",
        PlanErrorKinds.Unsupported => "Unsupported query",
        PlanErrorKinds.UnknownContext => "Unknown catalog context",
        PlanErrorKinds.EpochMismatch => "Catalog epoch mismatch",
        PlanErrorKinds.IrVersion => "IR version mismatch",
        PlanErrorKinds.InvalidCatalog => "Invalid catalog",
        PlanErrorKinds.Internal => "Planner internal error",
        PlanErrorKinds.Policy => "Refused by the entitlements",
        PlanErrorKinds.PlanningAborted => "Planning ended before there was a plan",
        // D271 (b): the client recovers from this by registering the version and retrying, so a host
        // only ever sees it when the recovery itself failed.
        PlanErrorKinds.UnknownCatalogVersion => "The planner does not hold this catalog version",
        _ => "Planning failed",
    };
}

/// <summary>
/// The host cancelled a prepare (D236). An <see cref="OperationCanceledException"/> like any other,
/// so a <c>catch (OperationCanceledException)</c> already written for it still works; what it adds
/// is the state, so a host can say how far planning had got.
/// </summary>
/// <remarks>
/// <para>
/// The state is the client's own and not the sidecar's, because on a cancelled call there is
/// nothing for the sidecar to answer on: the call is torn down and no response and no trailer can
/// reach the caller. So <see cref="PlanningState.TerminationReason"/> is
/// <see cref="PlanningTerminationReason.CancelledByHost"/>, <see cref="PlanningState.Elapsed"/> is
/// what the client measured between sending the request and the cancellation landing, and
/// <see cref="PlanningState.EvaluationCount"/> is zero — the count the sidecar reached is knowable
/// only through the <c>StopPlanning</c> diagnostic, which a host may call with the same request id.
/// </para>
/// <para>
/// The sidecar does observe the cancellation, and stops: gRPC cancels the request context on a
/// client cancellation and on a lost session alike, and that raises the optimiser's cancel flag at
/// the next rule boundary. What cannot travel back is the news of it.
/// </para>
/// </remarks>
public sealed class PlanningCancelledException : OperationCanceledException
{
    public PlanningCancelledException(
        PlanningState planningState, CancellationToken token, Exception? innerException = null)
        : base(
            ErrorCodes.WithCode(
                ChalkErrorCodes.PlanningCancelled,
                $"Planning was cancelled by the host after {planningState?.Elapsed.TotalMilliseconds ?? 0:0} ms. "
                + "No plan was produced; PlanningOptions.StopToken asks for the best plan so far instead."),
            innerException!,
            token)
    {
        PlanningState = planningState!;
    }

    /// <summary>What the client knows about the planning it cancelled.</summary>
    public PlanningState PlanningState { get; }

    /// <summary>Always <see cref="ChalkErrorCodes.PlanningCancelled"/>, as a ChalkQL exception would carry it.</summary>
    public string Code => ChalkErrorCodes.PlanningCancelled;
}

/// <summary>
/// The planner could not be reached, or did not answer in time. Distinct from
/// <see cref="PlanningException"/> because it is an operational problem, not a problem with the SQL:
/// from M3, cached plans keep executing while this is happening and only cold queries fail.
/// </summary>
public sealed class PlannerUnavailableException : ChalkException
{
    public PlannerUnavailableException(string address, string detail, Exception? innerException = null)
        : base(
            ChalkErrorCodes.PlannerUnavailable,
            $"The Chalk planner at {address} is unavailable: {detail}",
            innerException)
    {
        Address = address;
    }

    public string Address { get; }
}

/// <summary>
/// A <c>RecordedPlanner</c> was asked for a plan that has not been recorded. The message carries the
/// exact command that would record it, because that is always the next thing to do.
/// </summary>
public sealed class RecordedPlanMissingException : ChalkException
{
    public RecordedPlanMissingException(string sql, PushdownLevel pushdown, string key, string directory)
        : base(
            ChalkErrorCodes.RecordedPlanMissing,
            $"No recorded plan for this query at pushdown level {pushdown} (key {key}) in {directory}.{Environment.NewLine}"
            + $"SQL: {sql}{Environment.NewLine}"
            + "Record it with: scripts/record-plans.sh")
    {
        Sql = sql;
        Pushdown = pushdown;
        Key = key;
    }

    public string Sql { get; }

    public PushdownLevel Pushdown { get; }

    /// <summary>The content hash the recorder files plans under.</summary>
    public string Key { get; }
}

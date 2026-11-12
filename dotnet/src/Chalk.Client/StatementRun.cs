namespace Chalk.Client;

/// <summary>
/// One run of a prepared statement, as the extension that decorated it hears of it before it starts
/// (<see cref="PreparedQuery.Decorate"/>): an execution, or an update of a standing view of it,
/// recomputed or maintained (D361).
/// </summary>
/// <param name="Context">
/// The context the run binds: the one an execution binds, for a statement prepared with shapes, or
/// the one it was prepared with, for a statement that folded its values (F166). Never the prepare's
/// shape, which holds no values and belongs to no request.
/// </param>
/// <param name="PlanDigest">
/// The digest of the plan the run is heard under: the statement's own for an execution, and for a
/// maintained update the plan the view keeps, which a plan made in the background may since have
/// replaced as the statement's (design 69 §4).
/// </param>
/// <param name="Version">
/// For an update of a standing view, the version of the epoch the update brings it to, as the host
/// numbered its refresh; null for any other execution, whose reads are not pinned to one epoch.
/// </param>
internal readonly record struct StatementRun(RequestContext? Context, ulong PlanDigest, long? Version);

/// <summary>How the quarantine settled a decision its policy could not reach (F302, design 82).</summary>
internal enum IndeterminateSettled
{
    RowsNotAdmitted,
    RuleNotApplied,
    EvaluatedHere,
    StatementWithheld,
}

/// <summary>
/// One kind of indeterminate decision in one execution, as the extension that decorated the statement
/// hears of it (F302): the tables, how it was settled, how many it covered, and the first one's reason
/// and cause, which may quote a value the policy hides.
/// </summary>
internal sealed record IndeterminateHeard(
    Guid CorrelationId,
    string Tables,
    IndeterminateSettled Settled,
    long Occurrences,
    string Reason,
    Exception Cause,
    bool AtSource,
    string? SqlState);


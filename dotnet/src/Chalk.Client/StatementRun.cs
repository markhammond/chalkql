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

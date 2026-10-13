namespace Chalk.Execution;

/// <summary>
/// The clock of an engine that names one (D341, <c>docs/design/66-view-checks-and-tuning.md</c> §2):
/// on such an engine every execution reads it, as a standing view does. The engine moves it in the
/// same hold of <see cref="Chalk.Sources.ExecutionReadSet.Gate"/> as a refresh's commits, and an
/// execution reads it in the same hold as its pins, so a query's tables and its time are of one
/// refresh.
/// </summary>
internal interface IEngineClock
{
    /// <summary>The clock as the last refresh to move it left it, or null while none has.</summary>
    DateTimeOffset? Committed { get; }
}

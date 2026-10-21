namespace Chalk.Execution;

/// <summary>
/// The clock of an engine that names one (D341, <c>docs/design/66-view-checks-and-tuning.md</c> §2):
/// on such an engine every execution reads it, as a standing view does. The engine moves it in the
/// same hold of <see cref="Chalk.Sources.ExecutionReadSet.Gate"/> as a refresh's commits, and an
/// execution reads it in the same hold as its pins, so a query's tables and its time are of one
/// refresh.
/// </summary>
internal interface IEngineCommits
{
    /// <summary>
    /// How many refreshes the engine has committed (design 78 §1): moved in the same hold of the gate as
    /// their commits, and read in the same hold as an execution's pins, so that what an execution reads
    /// can be named by the refresh it reads.
    /// </summary>
    long Committed { get; }
}

/// <summary>The clock of an engine that names one; see the remarks above.</summary>
internal interface IEngineClock
{
    /// <summary>The clock as the last refresh to move it left it, or null while none has.</summary>
    DateTimeOffset? Committed { get; }
}

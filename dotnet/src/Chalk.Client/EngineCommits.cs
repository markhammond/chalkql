namespace Chalk.Client;

/// <summary>
/// How many refreshes an engine has committed (design 78 §1): moved by a refresh in the same hold of the
/// gate as its commits, and read by an execution in the same hold as its pins. A published stamp records
/// the count it covers, so a view can tell whether what it read is the stamp it noted.
/// </summary>
internal sealed class EngineCommits : Chalk.Execution.IEngineCommits
{
    private long _committed;

    public long Committed => Volatile.Read(ref _committed);

    /// <summary>Counts a refresh's commits. The caller holds the gate, with them.</summary>
    public void Advance() => Volatile.Write(ref _committed, _committed + 1);
}

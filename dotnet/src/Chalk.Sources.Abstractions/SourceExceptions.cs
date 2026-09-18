namespace Chalk.Sources;

/// <summary>
/// Valid IR that this executor cannot run yet — a node kind, function or type combination outside
/// the milestone's scope. Distinct from <c>InvalidPlanException</c>, which means the plan is
/// malformed. Raised at plan compilation, never mid-stream.
/// </summary>
public sealed class UnsupportedFeatureException : ChalkException
{
    public UnsupportedFeatureException(string feature, string detail)
        : base($"Unsupported in this build: {feature}. {detail}")
    {
        Feature = feature;
    }

    /// <summary>What is unsupported, e.g. <c>HashJoin</c> or <c>LIKE(STRING, STRING) with a non-constant pattern</c>.</summary>
    public string Feature { get; }
}

/// <summary>
/// The plan was made against a different catalog than the one the engine holds. A stale plan run
/// against a changed schema is a wrong-answer bug, so this is checked before every execution.
/// </summary>
public sealed class StalePlanException : ChalkException
{
    public StalePlanException(string planContextId, long planEpoch, string liveContextId, long liveEpoch)
        : this(planContextId, planEpoch, liveContextId, liveEpoch, table: null)
    {
    }

    /// <summary>
    /// The same, naming the table whose shape moved (D271 (d)). Staleness is per table since D271:
    /// a plan is stale when a table <em>it reads</em> changed shape, and the one that did is what a
    /// host wants to see in the message.
    /// </summary>
    public StalePlanException(
        string planContextId, long planEpoch, string liveContextId, long liveEpoch, string? table)
        : base(
            table is null
                ? $"The plan was made against catalog ({planContextId}, epoch {planEpoch}) but the engine now holds "
                    + $"({liveContextId}, epoch {liveEpoch}). Re-prepare the query against the current catalog."
                : $"The plan reads '{table}', whose shape has changed since it was planned at epoch "
                    + $"{planEpoch} (the engine is now at epoch {liveEpoch}). Re-prepare the query "
                    + "against the current catalog.")
    {
        PlanContextId = planContextId;
        PlanEpoch = planEpoch;
        LiveContextId = liveContextId;
        LiveEpoch = liveEpoch;
        Table = table;
    }

    public string PlanContextId { get; }

    public long PlanEpoch { get; }

    public string LiveContextId { get; }

    public long LiveEpoch { get; }

    /// <summary>
    /// The table whose shape moved, as <c>schema.table</c>, or null when the plan belongs to another
    /// catalog altogether and no single table is to blame (D271 (d)).
    /// </summary>
    public string? Table { get; }
}

/// <summary>Something went wrong while running a plan. Carries the plan digest and the failing operator's path.</summary>
public sealed class ExecutionException : ChalkException
{
    public ExecutionException(ulong planDigest, string operatorPath, string detail, Exception? innerException = null)
        : base($"Execution failed at {operatorPath} (plan {planDigest:x16}): {detail}", innerException)
    {
        PlanDigest = planDigest;
        OperatorPath = operatorPath;
    }

    public ulong PlanDigest { get; }

    /// <summary>The operator's position in the plan, e.g. <c>root/HashAggregate/Filter/Read</c>.</summary>
    public string OperatorPath { get; }
}

/// <summary>
/// A source broke the batch protocol: a batch whose schema does not match the one it was asked for,
/// a pushed filter it never declared, or a projection it did not honour.
/// </summary>
public sealed class SourceContractException : ChalkException
{
    public SourceContractException(string sourceId, string table, string detail)
        : base($"Source '{sourceId}' broke the scan contract for table '{table}': {detail}")
    {
        SourceId = sourceId;
        Table = table;
    }

    public string SourceId { get; }

    public string Table { get; }
}

/// <summary>
/// A source failed while running a query or a scan (D86, §4). Every provider exception an adapter
/// meets is wrapped in this, so a failure names the source and what it was doing rather than
/// arriving as an <c>SqliteException</c> from somewhere in a pipeline.
/// </summary>
/// <remarks>
/// A faulted execution surfaces nothing partial: the enumerator throws instead of yielding, the
/// arena is returned, and <c>QueryExecution.Stats</c> records what had been fetched when the fault
/// happened.
/// </remarks>
public sealed class SourceExecutionException : ChalkException
{
    public SourceExecutionException(string sourceId, string subject, Exception? innerException = null)
        : base(
            innerException is null
                ? $"Source '{sourceId}' failed running {subject}."
                : $"Source '{sourceId}' failed running {subject}: {innerException.GetType().Name}: {innerException.Message}",
            innerException)
    {
        SourceId = sourceId;
        Subject = subject;
    }

    public string SourceId { get; }

    /// <summary>What it was doing: a table name for a scan, or the query text for a pushed query.</summary>
    public string Subject { get; }
}

/// <summary>
/// A source took longer than its <c>SourceOptions.QueryTimeout</c> (D86). Distinct from
/// <see cref="OperationCanceledException"/>, which is the caller changing their mind: this one is
/// the source being too slow, and it names how long it was given.
/// </summary>
public sealed class SourceTimeoutException : ChalkException
{
    public SourceTimeoutException(string sourceId, string subject, TimeSpan timeout, Exception? innerException = null)
        : base(
            $"Source '{sourceId}' did not answer within {timeout.TotalSeconds:0.###}s while running {subject}. "
            + "Raise SourceOptions.QueryTimeout, or Timeout.InfiniteTimeSpan to wait indefinitely.",
            innerException)
    {
        SourceId = sourceId;
        Subject = subject;
        Timeout = timeout;
    }

    public string SourceId { get; }

    public string Subject { get; }

    public TimeSpan Timeout { get; }
}

/// <summary>
/// Bytes that were about to become a STRING column's value are not valid UTF-8 (D144,
/// <c>docs/design/24-zero-gc.md</c> §2).
/// </summary>
/// <remarks>
/// <para>
/// Raised exactly where bytes <em>enter</em> a column — a POCO chunk writer reading a
/// <see cref="Utf8String"/> property, a Tier 1 delegate returning one — so an Arrow buffer never
/// holds invalid UTF-8 and every reader downstream may take validity for granted. A value coming out
/// of a column is valid by construction and is never re-checked.
/// </para>
/// <para>
/// A <see cref="Utf8String"/> is bytes: nothing validates when one is constructed, because a host
/// that already holds UTF-8 should not pay to prove it twice. This is the one boundary that does.
/// </para>
/// </remarks>
public sealed class InvalidUtf8Exception : ChalkException
{
    public InvalidUtf8Exception(string subject, long row)
        : base($"{subject} is not valid UTF-8 at row {row}. A Utf8String holds UTF-8 bytes with "
            + "ordinal semantics; bytes that are not UTF-8 are refused where they enter a column, so "
            + "that no reader downstream has to check. Encode the value, or declare the member "
            + "BINARY (byte[] or ReadOnlyMemory<byte>) if it is not text.")
    {
        Subject = subject;
        Row = row;
    }

    /// <summary>What was being written — a column, or a function's result.</summary>
    public string Subject { get; }

    /// <summary>The row within the chunk or batch being written.</summary>
    public long Row { get; }
}

namespace Chalk.Entitlements;

/// <summary>
/// A host's observer of the policy's indeterminate decisions (F302, design 82): where a condition or a
/// mask of the policy, or a source running one, could not be evaluated for a row, the policy reached no
/// decision for it, and ChalkQL resolved it closed. Registered through
/// <c>engine.WithEntitlements(options, audit, indeterminate)</c>; when none is registered, no reason is
/// built and nothing is kept.
/// </summary>
/// <remarks>
/// A decision's <see cref="IndeterminateDecision.Reason"/> may quote a value the policy hides: it is never
/// put in an exception, and is the host's to keep as it keeps a database's server log. Unlike
/// <see cref="IEntitlementsAudit"/>, whose promise is never a value, this observer exists to carry one,
/// which is why it is an interface of its own. An exception it throws does not propagate: it is counted on
/// <see cref="EntitledEngine.IndeterminateObserverFaults"/>, never surfaced, and the observer goes on
/// being called.
/// </remarks>
public interface IIndeterminateDecisions
{
    /// <summary>
    /// One kind of indeterminate decision in one execution: once per execution, per tables, per
    /// resolution, as the execution ends, or just before a withheld result's exception is thrown. On
    /// the caller's thread, or a standing view's update thread.
    /// </summary>
    void Resolved(IndeterminateDecision decision);
}

/// <summary>Where the policy's decision could not be reached.</summary>
public enum IndeterminateAt
{
    /// <summary>In ChalkQL's own evaluation of the policy.</summary>
    ChalkQL,

    /// <summary>In a source running a query that carries the policy.</summary>
    Source,
}

/// <summary>What "resolved closed" meant for an indeterminate decision.</summary>
public enum ClosedResolution
{
    /// <summary>A filter's condition raised for rows, and they were not admitted.</summary>
    RowsNotAdmitted,

    /// <summary>A rule's condition raised for rows, and the next rule decided them.</summary>
    RuleNotApplied,

    /// <summary>A source raised, and its part was evaluated in ChalkQL instead.</summary>
    EvaluatedHere,

    /// <summary>It could not be settled closed, and the statement's result was withheld.</summary>
    StatementWithheld,
}

/// <summary>
/// One kind of indeterminate decision in one execution, and how ChalkQL resolved it (F302, design 82).
/// </summary>
public sealed class IndeterminateDecision
{
    /// <summary>The execution's: shared by every decision it raised and by a withheld result's exception.</summary>
    public required Guid CorrelationId { get; init; }

    /// <summary><see cref="System.Diagnostics.Activity.Current"/>'s trace, where the host traces; else null.</summary>
    public string? TraceId { get; init; }

    /// <summary>The digest of the plan that ran.</summary>
    public required ulong PlanDigest { get; init; }

    /// <summary>The entitled tables whose policy reached no decision, <c>schema.table</c>.</summary>
    public required string Tables { get; init; }

    /// <summary>The descriptor hash of each of <see cref="Tables"/>, in its order.</summary>
    public required IReadOnlyList<string> DescriptorHashes { get; init; }

    /// <summary>Where the decision could not be reached.</summary>
    public required IndeterminateAt At { get; init; }

    /// <summary>The source's error class, where a source raised and its provider gives one; else null.</summary>
    public string? SqlState { get; init; }

    /// <summary>How ChalkQL resolved it.</summary>
    public required ClosedResolution Resolution { get; init; }

    /// <summary>How many it covered in this execution: rows, or a withheld result's one.</summary>
    public required long Occurrences { get; init; }

    /// <summary>What made the first of them indeterminate. It may quote a value the policy hides.</summary>
    public required string Reason { get; init; }

    /// <summary>The first one's exception, with its stack. It may quote a value the policy hides.</summary>
    public required Exception Cause { get; init; }

    /// <summary>Why the host says this execution is being run, as on the audit event.</summary>
    public required string Purpose { get; init; }

    /// <summary>Who the host says is running this execution, as on the audit event.</summary>
    public required string Actor { get; init; }

    /// <summary>For an update of a standing view, the version it brings the view to; else null.</summary>
    public long? Version { get; init; }
}

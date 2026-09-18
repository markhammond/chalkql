namespace Chalk.Client;

/// <summary>
/// One capability a statement asks the planner to ignore, whatever the source's descriptor claims
/// (D87). Each names a thing a source may declare it can do; naming it here plans the statement as
/// if no source had declared it.
/// </summary>
/// <remarks>
/// <para>
/// This is not a tuning knob. It is the handle the M4 property test pulls: with a capability off,
/// the planner must produce a plan that does less remote work and the <em>same answer</em>, because
/// a capability is an optimisation and an optimisation that changes the result is a bug. It is also
/// what an operator reaches for when a source turns out to be lying in production and the fix
/// cannot wait for a new descriptor.
/// </para>
/// <para>
/// The pushdown level and this list are independent: the level says how much of the plan shape may
/// travel, and this says which of the source's own claims to believe.
/// </para>
/// </remarks>
public enum DisabledCapability
{
    /// <summary>Never sent. Present so the default value is not a real capability.</summary>
    Unspecified = 0,

    /// <summary>Predicates stay local, whatever the source declares it can evaluate.</summary>
    Filter = 1,

    /// <summary>The source is read with every column, and the projection happens here.</summary>
    Project = 2,

    /// <summary>Aggregation stays local, including the split into partial and final.</summary>
    Aggregate = 3,

    /// <summary>No <c>ORDER BY</c> travels; the sort is local.</summary>
    Sort = 4,

    /// <summary>No <c>LIMIT</c> or <c>OFFSET</c> travels.</summary>
    Limit = 5,

    /// <summary>Joins stay local even when both sides are the same source.</summary>
    Join = 6,

    /// <summary><c>DISTINCT</c> stays local.</summary>
    Distinct = 7,

    /// <summary>An <c>IN</c> list is expanded into <c>OR</c>s or evaluated locally.</summary>
    InList = 8,

    /// <summary>
    /// Parameters are not bound remotely: a pushed query is generated with its values inlined as
    /// literals, or the predicate stays local when they cannot be.
    /// </summary>
    Parameters = 9,
}

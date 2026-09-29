namespace Chalk.Ir;

/// <summary>How deep a plan may nest before a client refuses to read it (F102, F161).</summary>
public static class PlanLimits
{
    /// <summary>
    /// The nesting limit every read of a plan applies unless its planner says otherwise, in protobuf
    /// message levels — a relation or an expression is about two of them.
    /// </summary>
    /// <remarks>
    /// The limit is what keeps a plan from exhausting the stack of the code that reads it: the
    /// parser, the validator and the compiler all recurse once per level. Measured on 2026-09-29, a
    /// plan 549 levels deep was read, validated, compiled and run, and one 609 deep overflowed the
    /// validator's stack — a crash no handler can catch — on the thread the planner's reply completed
    /// on. This is under half the depth that ran. No statement Chalk plans for an entitlement comes
    /// near it since a membership over a context list stopped being a join (F161); only a statement
    /// nested by the hundred does.
    /// </remarks>
    public const int DefaultNestingLimit = 256;
}

/// <summary>
/// The plan nests deeper than this client will read it (F102, F161): reading it could exhaust the
/// stack, which no handler can catch, so it is refused here, by name.
/// </summary>
public sealed class PlanTooDeepException : ChalkException
{
    public PlanTooDeepException(int limit, Exception? innerException = null)
        : base(
            $"the plan nests deeper than this client's limit of {limit} levels, and the client refuses "
            + "to read it rather than risk exhausting the stack. A plan that deep is a statement that "
            + "deep — sub-queries or joins nested by the hundred. Simplify the statement, or raise the "
            + "planner's PlanNestingLimit on a host whose threads have the stack to match.",
            innerException)
    {
        Limit = limit;
    }

    /// <summary>The limit the plan exceeded.</summary>
    public int Limit { get; }
}

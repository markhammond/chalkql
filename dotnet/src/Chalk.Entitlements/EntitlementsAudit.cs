namespace Chalk.Entitlements;

/// <summary>
/// One execution of a statement the entitlement rewrite touched
/// (<c>docs/design/16-entitlements.md</c> §3.12, D157, D205).
/// </summary>
/// <remarks>
/// <para>
/// It carries what identifies the policy and the request and <b>never a context value</b>: the plan
/// digest, the descriptor hash of every entitled table the plan reads, how many rows each bound
/// context list held, and the <see cref="Purpose"/> and <see cref="Actor"/> the host supplied as
/// audit metadata. A tenancy identifier is a value and is not here — an audit log is not a place to
/// put one, and the digest already tells two role sets apart.
/// </para>
/// </remarks>
public sealed class EntitlementsAuditEvent
{
    /// <summary>The digest of the plan that ran. Two role sets are two digests (D152).</summary>
    public required ulong PlanDigest { get; init; }

    /// <summary>The descriptor hash of each entitled table the plan reads, in the plan's order.</summary>
    public required IReadOnlyList<string> DescriptorHashes { get; init; }

    /// <summary>Each entitled table the plan reads, and how much of it this principal can see.</summary>
    /// <remarks>
    /// Each entry also carries the columns of that table the statement <b>tested</b> without reading
    /// and the shapes it tested them by (D261,
    /// <c>docs/design/36-test-verdict.md</c> §3) — empty until a host grants a <c>Test</c> verdict.
    /// Enumeration by repeated probes is inherent to any comparison oracle and is not defended by
    /// the planner; this is where a host sees what was asked, and rate limiting belongs to the host,
    /// where identity and quotas live. What is recorded is the column and the shape, never a
    /// parameter's value: a probe's guess is a context value and §3.12 keeps those out of here.
    /// </remarks>
    public required IReadOnlyList<EntitledTableReport> Tables { get; init; }

    /// <summary>How many rows each bound context list held, by name. Counts, never members.</summary>
    public required IReadOnlyDictionary<string, int> ContextListRowCounts { get; init; }

    /// <summary>Why the host says this statement is being run (D205). Empty when it said nothing.</summary>
    public required string Purpose { get; init; }

    /// <summary>Who the host says is running it (D205). Empty when it said nothing.</summary>
    public required string Actor { get; init; }
}

/// <summary>
/// A host's observer of entitled executions (D157). Registered through
/// <c>engine.WithEntitlements(options, audit)</c> (D213); when none is registered nothing is built
/// and no event is raised, which is §0's "no audit event unless subscribed".
/// </summary>
public interface IEntitlementsAudit
{
    /// <summary>
    /// Called once per execution of a statement whose plan the rewrite touched, before the first
    /// batch is produced. It runs on the caller's thread: a slow observer is a slow query.
    /// </summary>
    void Executed(EntitlementsAuditEvent audited);
}

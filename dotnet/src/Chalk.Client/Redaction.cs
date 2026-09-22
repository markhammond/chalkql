namespace Chalk.Client;

/// <summary>
/// Which literals a redaction replaces (D262, <c>docs/design/37-redacted-sql.md</c> §1).
/// </summary>
/// <remarks>
/// The structural hash — and therefore the seed — is taken over <em>every</em> literal whatever this
/// says, so turning the scope down never moves a pseudonym a host has already logged.
/// </remarks>
public enum RedactionScope
{
    /// <summary>Character and binary strings only; everything else stays as written.</summary>
    Strings = 1,

    /// <summary>Every literal. The default.</summary>
    All = 2,
}

/// <summary>
/// How this engine redacts a statement's literals so the text can be logged (D262,
/// <c>docs/design/37-redacted-sql.md</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Opt-in.</b> <see cref="IncludeRedactedSql"/> is false, and an engine that never asks pays
/// nothing: no salt is generated, no request carries a redaction message, and the sidecar runs no
/// visitor. A statement turns it on for itself with <see cref="PrepareOptions.IncludeRedactedSql"/>.
/// </para>
/// <para>
/// <b>A pseudonym is not anonymisation.</b> A low-entropy value — a boolean, a small integer, a
/// status code — is brute-forceable by anyone who knows the statement's shape and holds the salt.
/// The salt is the host's secret; keep it like one.
/// </para>
/// </remarks>
public sealed class RedactionOptions
{
    /// <summary>
    /// Whether a prepare redacts by default. False, and a prepare that says nothing pays nothing.
    /// </summary>
    public bool IncludeRedactedSql { get; init; }

    /// <summary>
    /// The host's salt, the secret the pseudonyms are keyed by.
    /// </summary>
    /// <remarks>
    /// Empty — the default — means a <em>random 32-byte salt generated once per engine</em>, on
    /// first use and never for an engine that never redacts. No property returns it, so two
    /// processes, or one process restarted, produce pseudonyms that cannot be compared: the
    /// conservative default. Supply one and equal values in equal shapes correlate across processes
    /// and restarts — and equal values in different shapes still do not, because the statement's
    /// structure is in the seed.
    /// </remarks>
    public ReadOnlyMemory<byte> Salt { get; init; }

    /// <summary>Which literals become pseudonyms. <see cref="RedactionScope.All"/> by default.</summary>
    public RedactionScope Scope { get; init; } = RedactionScope.All;

    /// <summary>
    /// Keep the literals that are a position rather than a value — <c>LIMIT</c>, <c>FETCH</c>,
    /// <c>OFFSET</c>, an integer window frame bound, an ordinal in <c>GROUP BY</c> or
    /// <c>ORDER BY</c> — so a redacted statement still reads as the shape it is. True by default,
    /// and never honoured by the token fallback, which has no positions to trust.
    /// </summary>
    public bool KeepStructural { get; init; } = true;
}

/// <summary>
/// What one request asks the planner to redact (D262). The transport-level shape of
/// <see cref="RedactionOptions"/> with the engine's decisions already made: the salt here is the
/// host's own or the engine's generated one, and it travels because the pseudonym must be the same
/// whichever side computes it.
/// </summary>
public sealed class RedactionRequest
{
    public required ReadOnlyMemory<byte> Salt { get; init; }

    public RedactionScope Scope { get; init; } = RedactionScope.All;

    public bool KeepStructural { get; init; } = true;
}

/// <summary>One statement to redact, outside any planning (D262).</summary>
public sealed class RedactSqlRequest
{
    public required string Sql { get; init; }

    /// <summary>
    /// Which dialect to parse it as, so the token fallback lexes it with the same grammar a prepare
    /// would (D34, D259).
    /// </summary>
    public SqlConformance Conformance { get; init; } = SqlConformance.Default;

    public required RedactionRequest Redaction { get; init; }

    /// <summary>
    /// The context the statement was planned with, when there was one (D286). A literal in the text
    /// that is, in type and value, one of its bound values is labelled with the name it was bound
    /// under — <c>/*REDACTED-3f9a1c2e:DECIMAL @ctx.tenant_id*/</c> — so a pushed query's redacted
    /// text says which context entry a folded value came from. Optional; the label is a rendering
    /// only and never part of the structural hash or the pseudonym.
    /// </summary>
    public RequestContext? Context { get; init; }
}

/// <summary>What a redaction produced (D262).</summary>
public sealed class RedactedSql
{
    /// <summary>The statement with every literal in scope replaced by its keyed pseudonym.</summary>
    public required string Sql { get; init; }

    /// <summary>
    /// False when the text did not parse and the token fallback served it — which is the case where
    /// nothing structural is kept, because without a tree there is no position to trust.
    /// </summary>
    public required bool Parsed { get; init; }

    /// <summary>
    /// The statement's structural form hashed: the canonical unparse with every literal replaced by
    /// its type marker, as lower-case hexadecimal SHA-256. Two statements that differ only in their
    /// literals, their spacing, their case or their quoting share it, which is what makes it a way
    /// to group log lines by shape. Stable across processes, machines and host builds; the one
    /// boundary is the planner build.
    /// </summary>
    public required string StructuralHash { get; init; }
}

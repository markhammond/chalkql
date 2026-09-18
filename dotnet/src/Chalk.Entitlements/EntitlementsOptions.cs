namespace Chalk.Entitlements;

/// <summary>
/// What one host wants done with entitled columns it cannot disclose, and with stars over entitled
/// tables (<c>docs/design/16-entitlements.md</c> §3, §6).
/// </summary>
/// <remarks>
/// None of them changes what the rewrite guarantees; each is about what the caller is handed. They
/// reach the planner as the one extension this package attaches to a request (step 26c, D212), so a
/// host that does not use this package cannot construct them and sends no bytes for them.
/// </remarks>
public sealed class EntitlementsOptions
{
    /// <summary>
    /// Whether a star may name an entitled table (§3.11, D160). Review discipline; the rewrite
    /// guarantees the same thing either way.
    /// </summary>
    public StarPolicy StarPolicy { get; init; } = StarPolicy.Allow;

    /// <summary>
    /// What a column no disclosure permits becomes, in the two places the question arises (D161,
    /// D217 as amended): what a star's expansion surfaces, and what the statement named. One option
    /// rather than two flat ones, because the two answers are one policy read together.
    /// </summary>
    public RedactionPolicy Redaction { get; init; } = new();

    /// <summary>What a placeholder holds; a per-column stand-in on the entitlement wins (D162).</summary>
    public PlaceholderPolicy PlaceholderPolicy { get; init; } = PlaceholderPolicy.PlaceholdersAsNull;

    /// <summary>
    /// The group-size floor every population-only column that declares none inherits (§3.6, D211).
    /// There is no shipped floor: zero — the default — means no guard, so a catalog with
    /// population-only columns and no small-cell rule pays nothing for them. A column's own
    /// <c>MinGroupSize</c> of 1 disables the guard for it whatever this says.
    /// </summary>
    public int DefaultMinGroupSize { get; init; }

    /// <summary>
    /// Turn "this principal holds no grant on the table" into an error at prepare rather than an
    /// empty result (D207). Off by default: zero rows is the honest answer and reveals nothing.
    /// </summary>
    public bool RefuseWhenNoVisibleRows { get; init; }

    /// <summary>
    /// Ask for the explanation on every prepare, so <c>EntitledQuery.ExplainAsync</c> is the only
    /// call a host needs (D207). Off by default; <c>EntitledEngine.ExplainAsync</c> asks for it per
    /// call whatever this says.
    /// </summary>
    public bool Explain { get; init; }

    /// <summary>
    /// Beside each output column with at least one entitled origin, a sibling <c>STRING</c> column
    /// holding what that column disclosed <em>per row</em> — <c>FULL</c>, <c>MASKED</c>,
    /// <c>AGGREGATE</c> or <c>REDACTED</c> (§3.12, D207, D218).
    /// </summary>
    /// <remarks>
    /// The per-column report says what a column discloses for the whole result; this says it row by
    /// row, which is what a grid needs to render a masked cell differently from a value. It is
    /// emitted even where the disclosure is constant, so a consumer has one code path; a column of
    /// an unentitled table has no sibling, and the report already says <c>Full</c> for it. A derived
    /// column's sibling is the per-row meet of its origins.
    /// </remarks>
    public bool IncludeDisclosureColumns { get; init; }

    /// <summary>
    /// What a sibling's name is its column's name plus. Empty — the default — is
    /// <c>__disclosure</c>, so <c>first_name</c> is joined by <c>first_name__disclosure</c>.
    /// </summary>
    /// <remarks>
    /// A suffixed name already among the statement's own output names is refused at prepare, naming
    /// the column and the suffix, and never uniquified: a consumer looks the sibling up by name, and
    /// a silent rename would be a silent failure.
    /// </remarks>
    public string DisclosureColumnSuffix { get; init; } = "";

    /// <summary>The wire message, with <paramref name="explain"/> OR-ed over <see cref="Explain"/>.</summary>
    internal Rpc.EntitlementsOptions ToProto(bool explain) => new()
    {
        StarPolicy = (Rpc.StarPolicy)StarPolicy,
        Redaction = new Rpc.Redaction
        {
            StarExpansion = (Rpc.StarExpansion)Redaction.StarExpansion,
            NamedColumns = (Rpc.NamedColumns)Redaction.NamedColumns,
        },
        PlaceholderPolicy = (Rpc.PlaceholderPolicy)PlaceholderPolicy,
        DefaultMinGroupSize = (uint)Math.Max(0, DefaultMinGroupSize),
        RefuseWhenNoVisibleRows = RefuseWhenNoVisibleRows,
        Explain = explain || Explain,
        IncludeDisclosureColumns = IncludeDisclosureColumns,
        DisclosureColumnSuffix = DisclosureColumnSuffix,
    };
}

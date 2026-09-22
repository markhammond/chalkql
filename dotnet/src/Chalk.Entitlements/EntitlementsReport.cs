namespace Chalk.Entitlements;

/// <summary>
/// Whether <c>SELECT *</c> may name an entitled table (<c>docs/design/16-entitlements.md</c> §3.11,
/// D160). Review discipline, not a safety mechanism: a star is expanded and resolved before the
/// rewrite either way, so each column is entitled individually. It is
/// <c>DefaultDisclosure = None</c> that closes the schema-evolution gap a star exposes.
/// </summary>
/// <remarks>
/// Hand-written rather than the generated wire enum, for the reason <c>PushdownLevel</c> is
/// (ADR 0014): the transport's types are an implementation detail. The two are twins, value for
/// value.
/// </remarks>
public enum StarPolicy
{
    /// <summary>The default.</summary>
    Allow = 1,

    /// <summary>A star whose expansion touches an entitled table is refused, naming the table.</summary>
    RefuseOverEntitled = 2,

    /// <summary>Any star in any query is refused while the catalog carries entitlements.</summary>
    RefuseWhenEntitled = 3,
}

/// <summary>
/// The redaction policy: what a column no disclosure permits becomes, in the two places the question
/// arises (<c>docs/design/16-entitlements.md</c> §3.11, D161, D217 as amended).
/// </summary>
/// <remarks>
/// Two members and not two options, because the two answers are one policy read together. They stay
/// two members because the questions differ: a star's width is the host's business, so its expansion
/// may drop a column it could not disclose; a column the statement <b>named</b> is the statement's
/// business, and dropping it silently would be a silent failure — which is why
/// <see cref="Entitlements.NamedColumns"/> has no <c>Omit</c>.
/// </remarks>
public sealed class RedactionPolicy
{
    /// <summary>The columns a <c>SELECT *</c> or <c>t.*</c> surfaced and no disclosure permits.</summary>
    public StarExpansion StarExpansion { get; init; } = StarExpansion.Placeholder;

    /// <summary>A redacted column the statement <b>named</b>, which §3.11 answers separately.</summary>
    public NamedColumns NamedColumns { get; init; } = NamedColumns.Placeholder;
}

/// <summary>
/// What happens to an output column a star's expansion surfaced and no disclosure permits (D161).
/// </summary>
public enum StarExpansion
{
    /// <summary>
    /// The default: the column stays in the result as a placeholder, so the row shape is the same
    /// for every principal, and its disclosure is reported <see cref="ReportedDisclosure.Redacted"/>.
    /// </summary>
    Placeholder = 1,

    /// <summary>
    /// Dropped from the projection, for a client that renders what it gets. Needs a constant nothing
    /// for the whole query — which prepare-time binding gives — and degrades to
    /// <see cref="Placeholder"/> otherwise, which the report says. A column the query named
    /// explicitly is never omitted.
    /// </summary>
    Omit = 2,

    /// <summary>
    /// An error naming the column, for a client that wants to be told. Over the columns a star
    /// surfaced, which is what this member is about (D217); <see cref="Entitlements.NamedColumns"/>
    /// is the answer for a column the statement named.
    /// </summary>
    Refuse = 3,
}

/// <summary>
/// What happens to a redacted column the statement <b>named</b> (D217,
/// <c>docs/design/16-entitlements.md</c> §3.11).
/// </summary>
/// <remarks>
/// <see cref="StarExpansion"/> is written as being about the columns a <c>SELECT *</c> surfaces,
/// and a named column is the case §3.11 answers separately: a <c>POLICY</c> error when no
/// disclosure could ever permit it, and otherwise a placeholder. This is the second member, for a
/// host that would rather be told than handed a stand-in.
/// </remarks>
public enum NamedColumns
{
    /// <summary>The default, and §3.11's own answer: a placeholder.</summary>
    Placeholder = 1,

    /// <summary>An error naming the column.</summary>
    Refuse = 2,
}

/// <summary>
/// What one output column discloses (D161, D202), for a typed consumer that must tell a redacted
/// value from a NULL without inspecting values.
/// </summary>
/// <remarks>
/// Also on every batch as the Arrow field metadata key <c>chalk.disclosure</c>, upper-cased.
/// </remarks>
public enum ReportedDisclosure
{
    /// <summary>The value, as it is. Every column of an unentitled table.</summary>
    Full = 1,

    /// <summary>The column's mask, for every row.</summary>
    Masked = 2,

    /// <summary>A placeholder, for every row: NULL, the type's empty value, or a stand-in.</summary>
    Redacted = 3,

    /// <summary>Mixed across rows, which is what a row-dependent disclosure gives.</summary>
    PerRow = 4,

    /// <summary>
    /// A population aggregate under the group-size guard (D202): the value is real, and a NULL is a
    /// group below the floor rather than "no rows". A grid that read it as data would be wrong twice.
    /// </summary>
    Aggregate = 5,

    /// <summary>
    /// The column the principal may test and not read (D261,
    /// <c>docs/design/36-test-verdict.md</c>): this output is the result of a comparison the policy
    /// permits, computed in the leaf over the raw value. One bit, three-valued — a NULL raw value
    /// compares to NULL — and never the value itself.
    /// </summary>
    Tested = 6,
}

/// <summary>
/// One output column of an entitled prepared statement, and what it discloses
/// (<c>docs/design/16-entitlements.md</c> §3.12).
/// </summary>
public sealed class EntitledColumn
{
    /// <summary>The column's name, as the statement asked for it.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// The meet over the column's origins: <see cref="ReportedDisclosure.Full"/> when every origin is
    /// full — and for every column of an unentitled table — and never full when any origin is
    /// redacted.
    /// </summary>
    public required ReportedDisclosure Disclosure { get; init; }
}

/// <summary>
/// What the entitlement rewrite did to one prepared statement
/// (<c>docs/design/16-entitlements.md</c> §3.12) — the report the response's extension slot carried
/// back, as this package's own types.
/// </summary>
public sealed class EntitlementsReport
{
    /// <summary>An empty report: what a plan over a catalog without entitlements has to say.</summary>
    public static EntitlementsReport Empty { get; } = new()
    {
        Columns = [],
        Tables = [],
        DescriptorHashes = [],
    };

    /// <summary>One per output column, in order, with what it discloses.</summary>
    public required IReadOnlyList<EntitledColumn> Columns { get; init; }

    /// <summary>
    /// One per entitled table this plan reads, in the order the rewrite met them: the descriptor
    /// hash, whether the row predicate reached the source, and how much of the table this principal
    /// holds any grant on. Empty for a plan over a catalog without entitlements.
    /// </summary>
    public required IReadOnlyList<EntitledTableReport> Tables { get; init; }

    /// <summary>The descriptor hash of each of those tables, in the same order.</summary>
    public required IReadOnlyList<string> DescriptorHashes { get; init; }

    /// <summary>
    /// The context scalars this plan reads at execution (§2, D209). Empty under prepare-time
    /// binding, where each of them was folded into a literal before the plan existed.
    /// </summary>
    public IReadOnlyList<string> RequiredScalars { get; init; } = [];

    /// <summary>
    /// The bound relations this plan materialises at execution: the names of its bound tables. Under
    /// prepare-time binding, only a list too large to fold leaves one behind.
    /// </summary>
    public IReadOnlyList<string> RequiredRelations { get; init; } = [];

    /// <summary>
    /// <see cref="StarExpansion.Omit"/> was asked for and could not be given — a row-dependent
    /// disclosure, or execute-time binding — so the caller got placeholders instead.
    /// </summary>
    public bool OmitDegradedToPlaceholder { get; init; }
}

/// <summary>
/// What the entitlement rewrite did to one table this plan reads (§3.12, D207).
/// </summary>
public sealed class EntitledTableReport
{
    public required string Schema { get; init; }

    public required string Table { get; init; }

    /// <summary>The descriptor this plan was built against — the content hash, in the digest.</summary>
    public required string DescriptorHash { get; init; }

    /// <summary>
    /// Whether the folded row predicate reached the source. Honestly false for a local table, which
    /// has no source to push into, for a <c>Local</c> table, for a source that trusts its own row
    /// security and for a shape the source did not declare.
    /// </summary>
    public required bool RowPredicatePushed { get; init; }

    /// <summary>
    /// How much of the table this principal holds any grant on, computed from the context and the
    /// statement alone and never from hidden rows (D207). Never a count.
    /// </summary>
    public required TableVisibility Visibility { get; init; }

    /// <summary>
    /// The statement's own predicate on this table and this principal's scope are disjoint —
    /// <c>WHERE org_id = 3</c> against a scope of {1, 2} (D207). The result is empty, and to a caller
    /// that reads the same as an empty table, so the report says which it was. Like
    /// <see cref="Visibility"/> it depends on the context and the statement and on no hidden row:
    /// what makes it true is that the statement <em>alone</em> is satisfiable and the statement with
    /// the policy is not.
    /// </summary>
    public bool Contradiction { get; init; }

    /// <summary>The column both sides speak about — the one a caller has to change.</summary>
    public string ContradictionColumn { get; init; } = "";

    /// <summary>
    /// The columns of this table the statement <b>tested</b> without reading, and by which shapes
    /// (D261, <c>docs/design/36-test-verdict.md</c> §3).
    /// </summary>
    /// <remarks>
    /// Empty for every statement that tests none, which is every statement until a host grants a
    /// <c>Test</c> verdict. Enumeration by repeated probes is inherent to any comparison oracle and
    /// is not defended by the planner: this is what the audit observer receives so that the host —
    /// where identity and quotas live — can see what was asked.
    /// </remarks>
    public IReadOnlyList<TestedColumnReport> Tested { get; init; } = [];
}

/// <summary>
/// One column a statement tested without reading (D261). The shapes are the ones the statement
/// actually used, not the ones the policy permits: an audit log records what was asked.
/// </summary>
public sealed class TestedColumnReport
{
    /// <summary>The column's name, as the table declares it.</summary>
    public required string Column { get; init; }

    /// <summary><c>EQUALS</c>, <c>NOT_EQUALS</c> or <c>IN</c>, upper-cased (D218).</summary>
    public required IReadOnlyList<string> Shapes { get; init; }
}

/// <summary>How much of an entitled table's rows a principal holds any grant on (D207).</summary>
public enum TableVisibility
{
    /// <summary>The folded row predicate is false: the principal holds no grant on the table.</summary>
    None = 1,

    /// <summary>Neither constant: some rows, decided per row.</summary>
    Some = 2,

    /// <summary>The folded row predicate is true: every row.</summary>
    All = 3,
}

/// <summary>
/// What this principal's policy resolved to for one statement, table by table and column by column
/// (<c>docs/design/16-entitlements.md</c> §3.12, D207) — the oracle a host reads when it wants to
/// know why a row is missing.
/// </summary>
/// <remarks>
/// Nothing executes to produce it and the caller needs no plan: it is computed by the same pass that
/// enforces the policy, from the same folded expressions, so it cannot drift from what runs. That is
/// the whole of its value — a second implementation that agreed by construction would say nothing,
/// and one that disagreed would be a third opinion nobody could adjudicate.
/// </remarks>
public sealed class EntitlementsExplanation
{
    /// <summary>One entry per entitled table the statement reads, in the order the pass met them.</summary>
    public required IReadOnlyList<ExplainedTable> Tables { get; init; }
}

/// <summary>One entitled table, as the policy resolved it for this principal.</summary>
public sealed class ExplainedTable
{
    public required string Schema { get; init; }

    public required string Table { get; init; }

    /// <summary>
    /// The folded row predicate as SQL text, or empty where it folded away and every row is visible.
    /// </summary>
    public required string RowPredicate { get; init; }

    /// <summary>Whether it reached the source (§3.7).</summary>
    public required bool RowPredicatePushed { get; init; }

    /// <summary>
    /// The conjuncts of that predicate this plan evaluates locally. Empty where it pushed whole, and
    /// the whole predicate where nothing pushed at all.
    /// </summary>
    public required IReadOnlyList<string> Residual { get; init; }

    public required IReadOnlyList<ExplainedColumn> Columns { get; init; }

    /// <summary>
    /// The parents this table's visibility derived through (§3.13, D230). Empty for a table that
    /// carries its own tenancy, which is every table until a host declares a <c>Through</c>.
    /// </summary>
    public IReadOnlyList<ExplainedParent> Parents { get; init; } = [];

    /// <summary>
    /// The declared paths this table holds a tenancy along (D265,
    /// <c>docs/design/38-existential-visibility.md</c> §6). Empty for a table that holds every
    /// perspective directly, which is every table until a host declares an <c>Inherited</c> or a
    /// <c>Related</c>.
    /// </summary>
    public IReadOnlyList<ExplainedPath> Paths { get; init; } = [];
}

/// <summary>
/// One declared path, as the policy resolved and folded it for this principal (D265 §6).
/// </summary>
/// <remarks>
/// What a host asking "why is this row missing" needs is which perspective answered for it, the
/// route it took, whether the chain was built at all, and whether the key it joins on is one this
/// principal can see.
/// </remarks>
public sealed class ExplainedPath
{
    /// <summary>The tenancy kind this path carries — the policy's own name for the perspective.</summary>
    public required string Kind { get; init; }

    /// <summary>The steps from the entitled table outward, in order.</summary>
    public IReadOnlyList<ExplainedStep> Steps { get; init; } = [];

    public required string EndpointSchema { get; init; }

    public required string EndpointTable { get; init; }

    /// <summary>
    /// The chain was left out of the plan: the endpoint predicate folded to TRUE over a route every
    /// row of this table follows, or this table's own predicate already granted every row, so the
    /// marker could not change the answer.
    /// </summary>
    public required bool Elided { get; init; }

    /// <summary>
    /// The endpoint predicate folded to FALSE, so nothing is reached this way and the joins went
    /// with it. The other extreme from <see cref="Elided"/>.
    /// </summary>
    public required bool Dropped { get; init; }

    /// <summary>The endpoint predicate as SQL text, folded, or empty where it folded away.</summary>
    public required string EndpointPredicate { get; init; }

    /// <summary>
    /// The path predicate as SQL text, folded, or empty where the path carries none and where it
    /// folded away. It is what a confinement conjoining this perspective with a kind the entitled
    /// table holds directly comes to, decided above the join over the two rows together.
    /// </summary>
    public string PathPredicate { get; init; } = "";

    /// <summary>
    /// What this principal sees of the column the entitled table joins on: <c>FULL</c>,
    /// <c>MASKED</c>, <c>REDACTED</c>, <c>AGGREGATE</c> or <c>PER_ROW</c>.
    /// </summary>
    /// <remarks>
    /// The mechanism never needs it disclosed — the key set's join reads it raw beneath the table's
    /// sanitiser — but the <em>statement's</em> own join does, so a role for which it is not
    /// <c>FULL</c> is a role that cannot correlate, and that is shown here rather than refused.
    /// </remarks>
    public required string KeyDisclosure { get; init; }
}

/// <summary>One step of an <see cref="ExplainedPath"/>, by the names the policy wrote.</summary>
public sealed class ExplainedStep
{
    public required string Schema { get; init; }

    public required string Table { get; init; }

    /// <summary>The column of the table this step starts from.</summary>
    public required string FromColumn { get; init; }

    /// <summary>The column of <see cref="Table"/> it joins.</summary>
    public required string ToColumn { get; init; }

    /// <summary>
    /// Up, to a table holding a foreign key to ours — the bridge, which contributes existence and
    /// nothing else. False is a step down to a parent.
    /// </summary>
    public required bool ToChild { get; init; }
}

/// <summary>
/// One parent a table's visibility derived through, as the policy resolved it for this principal
/// (§3.13, D230).
/// </summary>
/// <remarks>
/// What a host asking "why is this row missing" needs is which relationship answered for it, on
/// which key, and whether the key it joins on is one this principal can see at all.
/// </remarks>
public sealed class ExplainedParent
{
    public required string Schema { get; init; }

    public required string Table { get; init; }

    /// <summary>The correlation key on the entitled table.</summary>
    public required string Column { get; init; }

    /// <summary>The parent's key, a declared unique key of it.</summary>
    public required string ParentColumn { get; init; }

    /// <summary>
    /// The parent's own predicate folded to TRUE over a key every child row carries, so every row
    /// has a visible parent and the join was left out of the plan altogether (D226).
    /// </summary>
    public required bool Elided { get; init; }

    /// <summary>
    /// What this principal sees of <see cref="Column"/>: <c>FULL</c>, <c>MASKED</c>,
    /// <c>REDACTED</c>, <c>AGGREGATE</c> or <c>PER_ROW</c>.
    /// </summary>
    /// <remarks>
    /// The mechanism never needs it disclosed — the planner's own join reads it raw beneath the
    /// child's sanitiser — but the <em>statement's</em> own join does, so a role for which it is not
    /// <c>FULL</c> is a role that cannot correlate, and that is shown here rather than refused.
    /// </remarks>
    public required string KeyDisclosure { get; init; }
}

/// <summary>One column of one entitled table, as the policy resolved it.</summary>
public sealed class ExplainedColumn
{
    public required string Column { get; init; }

    /// <summary>
    /// The folded disclosure: a constant name where the fold settled it for every row this principal
    /// can see, and otherwise the folded rule conditions in order, as text.
    /// </summary>
    public required string Disclosure { get; init; }

    /// <summary>The mask in place of the value, as text, where one is reachable.</summary>
    public required string Mask { get; init; }

    /// <summary>The stand-in for a redacted value, as text, where the descriptor declares one.</summary>
    public required string Placeholder { get; init; }

    /// <summary>
    /// The group-size floor this column's population aggregates take, after the host's default has
    /// been applied (D211). Zero means no guard.
    /// </summary>
    public required int MinGroupSize { get; init; }
}

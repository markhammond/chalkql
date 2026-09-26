using System.Globalization;
using System.Security.Cryptography;
using System.Text;

// The descriptor vocabulary is entitlement vocabulary, so it carries the entitlement namespace
// (step 26c, D213) while staying in the Chalk.Catalog assembly: a policy is a property of a table,
// and a catalog is where a table is described. The catalog-wide *settings* that mention it —
// CatalogOptions and PlaceholderPolicy — stay in Chalk.Catalog, where the rest of a catalog's
// options are.
namespace Chalk.Entitlements;

using Chalk.Catalog;

/// <summary>
/// Row and column disclosure for one table — layer A of the entitlement design
/// (<c>docs/design/16-entitlements.md</c> §1, D141). The whole vocabulary the core knows: one row
/// predicate, per-column disclosures, one enforcement locus and one default. A policy model (the
/// shipped tenancy package, or a host's own) is a compiler down to this and nothing of its
/// vocabulary reaches here.
/// </summary>
/// <remarks>
/// The expressions are SQL text in Chalk's own dialect, validated and type-checked by the sidecar at
/// registration. <c>@ctx.&lt;name&gt;</c> names a context scalar; the same inside
/// <c>IN (SELECT … FROM @ctx.&lt;name&gt;)</c> or <c>EXISTS</c> names a context relation. SQL text is the
/// least opinionated interchange: any host language can produce it, and it is readable in the plan
/// text and in an audit log.
/// </remarks>
public sealed class TableEntitlementDescriptor
{
    /// <summary>
    /// A SQL boolean over the table's columns and the context. Empty — the default — means every row,
    /// which is what a table entitled only in its columns wants.
    /// </summary>
    public string RowPredicate { get; init; } = "";

    /// <summary>A column not listed here takes <see cref="DefaultDisclosure"/>.</summary>
    public IReadOnlyList<ColumnEntitlementDescriptor> Columns { get; init; } = [];

    /// <summary>
    /// Where the row predicate is evaluated. <see cref="Chalk.Catalog.Enforcement.Pushdown"/> lets it
    /// travel to a source that can take it; <see cref="Chalk.Catalog.Enforcement.Local"/> keeps the
    /// tenant set out of remote query text.
    /// </summary>
    public Enforcement Enforcement { get; init; } = Enforcement.Pushdown;

    /// <summary>
    /// What a column no <see cref="Columns"/> entry names gets (D159). <see cref="Disclosure.Full"/>
    /// is the default; <see cref="Disclosure.None"/> is deny-by-default, so a table whose columns
    /// grow never widens silently.
    /// </summary>
    public Disclosure DefaultDisclosure { get; init; } = Disclosure.Full;

    /// <summary>
    /// Whether this table's masks may be evaluated by its source, and then only where the source
    /// declares <see cref="SourceCapabilities.SupportsMaskPushdown"/> (D153). False by default: a
    /// pushed mask puts the raw value and the mask key in query text, which is the thing masking
    /// exists to prevent.
    /// </summary>
    public bool PushMasks { get; init; }

    /// <summary>
    /// The parents this table's visibility derives through (D225, §3.13): a row is visible when the
    /// parent row it names is visible to the same principal. A chat message reached through its
    /// thread, a line item through its order — a table that carries no tenancy column of its own.
    /// </summary>
    /// <remarks>
    /// Several entries are OR-ed, as every restriction is: any path grants. The same parent through
    /// two columns is two entries. <see cref="RowPredicate"/> may be set beside them and is OR-ed in
    /// too, and it keeps §2's vocabulary — this table's columns, context scalars, lists and
    /// relations, never another catalog table — so a sub-query over the parent is refused at
    /// registration naming this field as the way.
    /// </remarks>
    public IReadOnlyList<ParentVisibilityDescriptor> Through { get; init; } = [];

    /// <summary>
    /// The declared paths along which this table's rows hold a tenancy (D265,
    /// <c>docs/design/38-existential-visibility.md</c> §2): one entry per <c>Inherited</c> or
    /// <c>Related</c> restriction, flattened to its steps and its endpoint predicate.
    /// </summary>
    /// <remarks>
    /// <see cref="Through"/> carries every perspective of a parent down one foreign key. This carries
    /// one named perspective along a route that may begin by going <em>up</em>, and it consults no
    /// other table's entitlement: the compiler resolved the route and put the endpoint's own
    /// predicate here. Several entries are OR-ed, with <see cref="RowPredicate"/> and with
    /// <see cref="Through"/>: any path grants.
    /// </remarks>
    public IReadOnlyList<InheritedVisibilityDescriptor> Inherited { get; init; } = [];

    /// <summary>
    /// The content hash of this descriptor, in the plan digest for every plan that touches the table,
    /// so a changed policy is a new plan by construction and a cache can never serve a stale one.
    /// Computed on first read and stable for the life of the object, which is immutable.
    /// </summary>
    public string DescriptorHash => _hash ??= ComputeHash();

    private string? _hash;

    /// <summary>The entitlement for one column, or null when the column takes the default.</summary>
    public ColumnEntitlementDescriptor? FindColumn(int column)
    {
        for (var i = 0; i < Columns.Count; i++)
        {
            if (Columns[i].Column == column)
            {
                return Columns[i];
            }
        }

        return null;
    }

    /// <summary>
    /// SHA-256 over the canonical form — every field in declaration order, length-prefixed so no two
    /// distinct descriptors can spell the same bytes — as 32 lower-case hex characters. Truncated to
    /// 128 bits, which is a content hash rather than a signature: what it has to do is make a changed
    /// policy a different string, and it is not a secret.
    /// </summary>
    private string ComputeHash()
    {
        var text = new StringBuilder(256);
        Append(text, RowPredicate);
        Append(text, ((int)Enforcement).ToString(CultureInfo.InvariantCulture));
        Append(text, ((int)DefaultDisclosure).ToString(CultureInfo.InvariantCulture));
        Append(text, PushMasks ? "1" : "0");

        // The parents, in order — the order is part of the meaning, since it is the order the joins
        // go on and the order the verdicts are combined in (D225, D226). Appended only where there
        // are any, so a descriptor that derives its visibility through nothing hashes exactly as it
        // did before the field existed: zero-cost when unused is a property of the hash too.
        if (Through.Count > 0)
        {
            Append(text, "through");
            Append(text, Through.Count.ToString(CultureInfo.InvariantCulture));
            foreach (var parent in Through)
            {
                Append(text, parent.Column.ToString(CultureInfo.InvariantCulture));
                Append(text, parent.ParentSchema);
                Append(text, parent.ParentTable);
                Append(text, parent.ParentColumn.ToString(CultureInfo.InvariantCulture));
            }
        }

        // The declared paths, in order and in field order, immediately after the parents — appended
        // only where there are any, so a descriptor that declares none hashes exactly as it did
        // before the field existed, which is what keeps D265 additive in the plan digest too (§2).
        if (Inherited.Count > 0)
        {
            Append(text, "inherited");
            Append(text, Inherited.Count.ToString(CultureInfo.InvariantCulture));
            foreach (var path in Inherited)
            {
                Append(text, path.Kind);
                Append(text, path.Steps.Count.ToString(CultureInfo.InvariantCulture));
                foreach (var step in path.Steps)
                {
                    Append(text, step.Schema);
                    Append(text, step.Table);
                    Append(text, step.FromColumn.ToString(CultureInfo.InvariantCulture));
                    Append(text, step.ToColumn.ToString(CultureInfo.InvariantCulture));
                    Append(text, ((int)step.Direction).ToString(CultureInfo.InvariantCulture));
                }

                Append(text, path.EndpointPredicate);
                Append(text, path.EndpointSchema);
                Append(text, path.EndpointTable);

                // The path predicate after the endpoint's, in field order, and appended only where
                // the path carries one — so a path decided on the endpoint's row alone hashes
                // exactly as it did before the field existed (D279 §3). The marker before it is what
                // keeps the length-prefixed text unambiguous against the fields that follow.
                if (path.PathPredicate.Length > 0)
                {
                    Append(text, "path");
                    Append(text, path.PathPredicate);
                }
            }
        }

        Append(text, Columns.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var column in Columns)
        {
            Append(text, column.Column.ToString(CultureInfo.InvariantCulture));
            Append(text, column.Mask);
            Append(text, column.Placeholder);
            Append(text, column.MinGroupSize.ToString(CultureInfo.InvariantCulture));
            Append(text, column.AggregateOnlyFunctions.Count.ToString(CultureInfo.InvariantCulture));
            foreach (var function in column.AggregateOnlyFunctions)
            {
                Append(text, function);
            }

            // The rules in order — order is part of the meaning, since first match wins — then the
            // default and the statistical opt-in.
            Append(text, column.Rules.Count.ToString(CultureInfo.InvariantCulture));
            foreach (var rule in column.Rules)
            {
                Append(text, rule.When);
                Append(text, ((int)rule.Then).ToString(CultureInfo.InvariantCulture));
                Append(text, rule.Mask);
                Append(text, rule.Placeholder);

                // The permitted comparison shapes, in order and only where there are any (D261), so
                // a descriptor that permits no test hashes exactly as it did before the field
                // existed: zero-cost when unused is a property of the hash too.
                if (rule.Tests.Count > 0)
                {
                    Append(text, "tests");
                    Append(text, rule.Tests.Count.ToString(CultureInfo.InvariantCulture));
                    foreach (var shape in rule.Tests)
                    {
                        Append(text, ((int)shape).ToString(CultureInfo.InvariantCulture));
                    }
                }
            }

            Append(text, ((int)column.Otherwise).ToString(CultureInfo.InvariantCulture));
            Append(text, column.Statistical ? "1" : "0");
        }

        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()), hash);
        return Convert.ToHexStringLower(hash[..16]);
    }

    private static void Append(StringBuilder text, string value)
    {
        text.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append(';');
    }
}

/// <summary>
/// One parent a table's visibility derives through (D225, §3.13).
/// </summary>
/// <remarks>
/// The relationship is explicit rather than a sub-query in the row predicate, because the planner
/// has to compile it into a join and read the parent's own verdict at the parent's cardinality
/// (D226, D228), which it could not do from arbitrary SQL. Registration checks that the parent
/// exists, is entitled and is itself restricted — through an unrestricted parent restricts nothing
/// — that <see cref="ParentColumn"/> is a declared unique key of the parent, that
/// <see cref="Column"/> has the parent key's type kind, and that no chain of parents returns to a
/// table; a diamond is fine.
/// </remarks>
public sealed class ParentVisibilityDescriptor
{
    /// <summary>
    /// Index into this table's columns: the correlation key. It stays an ordinary column — the
    /// pass's own join reads it raw beneath the child's sanitiser — so the mechanism never needs it
    /// disclosed, while the statement's own join does.
    /// </summary>
    public required int Column { get; init; }

    /// <summary>The parent's schema. Empty means this table's own.</summary>
    public string ParentSchema { get; init; } = "";

    /// <summary>The parent table.</summary>
    public required string ParentTable { get; init; }

    /// <summary>
    /// Index into the parent's columns, and a declared unique key of it, so no join multiplies rows.
    /// </summary>
    public required int ParentColumn { get; init; }
}

/// <summary>
/// One declared path by which a table's rows hold a tenancy (D265,
/// <c>docs/design/38-existential-visibility.md</c> §2).
/// </summary>
/// <remarks>
/// <para>
/// Flattened: the steps from the entitled table outward, and at the far end the predicate that says
/// which of the endpoint's rows this principal holds the kind in. The pass joins along the steps,
/// folds the one predicate, and takes a key set back to the target — it never reads the endpoint's
/// own entitlement, which is what makes the marketplace shape acyclic where the bridge is itself
/// entitled through the target.
/// </para>
/// <para>
/// An <c>Inherited</c> path is down-steps alone and behaves as a <see cref="ParentVisibilityDescriptor"/>
/// does. A <c>Related</c> path begins with one up-step — the bridge, which contributes existence and
/// nothing else — and its semi-join is never elided: a total foreign key on the bridge says every
/// bridge row has a target row, not that every target row has a bridge row.
/// </para>
/// </remarks>
public sealed class InheritedVisibilityDescriptor
{
    /// <summary>The tenancy kind this path carries, for the report and for explain.</summary>
    public required string Kind { get; init; }

    /// <summary>The steps from this table outward, in order. Empty is refused at registration.</summary>
    public IReadOnlyList<VisibilityStepDescriptor> Steps { get; init; } = [];

    /// <summary>
    /// A SQL boolean over the <em>endpoint's</em> columns and the context, in §2's vocabulary — the
    /// same shape a direct dimension compiles to. Folded for the principal at the far end of the
    /// chain: FALSE drops the joins, TRUE drops the predicate.
    /// </summary>
    public string EndpointPredicate { get; init; } = "";

    /// <summary>
    /// A SQL boolean over the <b>target's own columns</b> and the endpoint's as
    /// <c>&lt;endpoint_table&gt;.&lt;column&gt;</c>, decided above the join where both rows are
    /// (D279 §2, §3). It is what a confinement conjoining this perspective with a kind the target
    /// holds directly comes to; the endpoint predicate then admits every endpoint row such a grant
    /// could reach, and this decides them.
    /// </summary>
    /// <remarks>
    /// Empty for a path that needs only the endpoint's own row, which is every path a policy could
    /// declare before this — so the field emits no bytes and an unchanged policy's descriptor and
    /// plan are byte-identical.
    /// </remarks>
    public string PathPredicate { get; init; } = "";

    /// <summary>The endpoint's schema. Empty means the entitled table's own.</summary>
    public string EndpointSchema { get; init; } = "";

    /// <summary>The endpoint, which holds the kind directly.</summary>
    public required string EndpointTable { get; init; }
}

/// <summary>One step of an <see cref="InheritedVisibilityDescriptor"/> path (D265 §2).</summary>
public sealed class VisibilityStepDescriptor
{
    /// <summary>The step's schema. Empty means the entitled table's own.</summary>
    public string Schema { get; init; } = "";

    /// <summary>The table this step arrives at.</summary>
    public required string Table { get; init; }

    /// <summary>Index into the columns of the table this step starts from.</summary>
    public required int FromColumn { get; init; }

    /// <summary>Index into <see cref="Table"/>'s columns.</summary>
    public required int ToColumn { get; init; }

    /// <summary>Which way the step goes over the declared foreign key.</summary>
    public required StepDirection Direction { get; init; }
}

/// <summary>Which way a step of a path goes over the declared foreign key (D265 §1).</summary>
public enum StepDirection
{
    Unspecified = 0,

    /// <summary>
    /// Down: the table the step starts from holds the foreign key, and the column stepped to is the
    /// parent's declared unique key. An <c>Inherited</c> path is these alone.
    /// </summary>
    ToParent = 1,

    /// <summary>
    /// Up: the table stepped to holds the foreign key, and the column stepped from is our declared
    /// unique key. Exactly one, and first, on a <c>Related</c> path.
    /// </summary>
    ToChild = 2,
}

/// <summary>
/// What one column discloses, per row: an ordered list of <see cref="Rules"/> with a default
/// (D196). First match wins. A constant disclosure is no rules and an <see cref="Otherwise"/>.
/// </summary>
/// <remarks>
/// Rules are structured rather than one SQL expression yielding a name, so the names a column can
/// take are read off the descriptor without parsing anything — which is what lets the planner decide
/// a refusal on the <em>folded</em> rules for this principal rather than on the text. The four names
/// are a closed codomain, so any function from a row and a context into them is at most three rules
/// and a default.
/// </remarks>
public sealed class ColumnEntitlementDescriptor
{
    /// <summary>Index into the table's columns.</summary>
    public required int Column { get; init; }

    /// <summary>
    /// The rules, in the order they are tried; the first whose <see cref="DisclosureRule.When"/>
    /// holds decides the row. Empty means the disclosure is the constant <see cref="Otherwise"/>.
    /// </summary>
    public IReadOnlyList<DisclosureRule> Rules { get; init; } = [];

    /// <summary>
    /// What a row no rule matched gets. <see cref="Chalk.Catalog.Disclosure.None"/> by default, and
    /// on a table with a non-empty <see cref="TableEntitlementDescriptor.RowPredicate"/> it must be
    /// <see cref="Chalk.Catalog.Disclosure.None"/> (D208): a row outside the predicate is evaluated
    /// too, by predicates the optimiser moves below the filter, so what it resolves to must not be
    /// the raw value.
    /// </summary>
    public Disclosure Otherwise { get; init; } = Disclosure.None;

    /// <summary>
    /// The value substituted where the disclosure is MASKED and the matching rule states no mask of
    /// its own: a SQL scalar expression of the column's type. Required where MASKED is reachable.
    /// </summary>
    public string Mask { get; init; } = "";

    /// <summary>
    /// The host accepts query-set-size control as this column's guarantee (D203). Under
    /// <see cref="Chalk.Catalog.Disclosure.Masked"/> or
    /// <see cref="Chalk.Catalog.Disclosure.AggregateOnly"/> the raw value may additionally reach
    /// predicates and grouping keys in a statement whose every output column is an aggregate or a
    /// group key, with group suppression and no individual pinning. Declared and shape-checked here;
    /// the planner enforces it from part 2b and ignores it until then.
    /// </summary>
    public bool Statistical { get; init; }

    /// <summary>
    /// The aggregates permitted over this column where the disclosure is AGGREGATE_ONLY, by their
    /// SQL names. A use that is not one of them is a <c>POLICY</c> error at planning.
    /// </summary>
    public IReadOnlyList<string> AggregateOnlyFunctions { get; init; } = [];

    /// <summary>
    /// The group-size floor those aggregates are guarded by (D211). 0 inherits the default the host
    /// gives at planning — <c>PrepareOptions.DefaultMinGroupSize</c>, with an engine-wide default on
    /// <c>ChalkEngineOptions</c> — which is itself 0 when the host says nothing; 1 disables the
    /// guard for this column whatever that default is; 2 or more is the floor. An effective floor of
    /// one or less emits no guard at all. The guard is query-set-size control, not differential
    /// privacy.
    /// </summary>
    public int MinGroupSize { get; init; }

    /// <summary>
    /// What stands in for this column where it is undisclosed: a SQL scalar expression of the
    /// column's type. Set, it wins over either <c>PlaceholderPolicy</c> (D162), so a host names a
    /// stand-in only where the policy's own value would mislead — an <c>amount</c> of 0 says more
    /// than it should.
    /// </summary>
    public string Placeholder { get; init; } = "";
}

/// <summary>
/// One rule of a column's disclosure (D196): a condition, the name it yields, and optionally the
/// mask that name uses. "Agents see the initial, auditors see the token" is two rules.
/// </summary>
public sealed class DisclosureRule
{
    /// <summary>A SQL boolean over the table's columns and the context.</summary>
    public required string When { get; init; }

    /// <summary>What this rule discloses when <see cref="When"/> holds.</summary>
    public required Disclosure Then { get; init; }

    /// <summary>
    /// The mask under this rule, over <see cref="ColumnEntitlementDescriptor.Mask"/>. Only
    /// meaningful with <see cref="Chalk.Catalog.Disclosure.Masked"/>.
    /// </summary>
    public string Mask { get; init; } = "";

    /// <summary>
    /// What stands in for the value where <em>this</em> rule redacts it (D224): a SQL scalar
    /// expression of the column's type, over
    /// <see cref="ColumnEntitlementDescriptor.Placeholder"/> and over either
    /// <c>PlaceholderPolicy</c>.
    /// </summary>
    /// <remarks>
    /// Only meaningful with <see cref="Chalk.Catalog.Disclosure.None"/>, and refused at registration
    /// under any other <see cref="Then"/>: a rule that discloses a value has nothing to stand in for
    /// it. It may not read the column itself — the outcome stays REDACTED, so a placeholder holding
    /// the withheld value would disclose exactly what it claims not to. The raw value reaches no
    /// use, and a predicate over the column compares the stand-in: that is what tells this from a
    /// MASKED rule with a constant mask.
    /// </remarks>
    public string Placeholder { get; init; } = "";

    /// <summary>
    /// The comparison shapes this rule permits over the column (D261,
    /// <c>docs/design/36-test-verdict.md</c>).
    /// </summary>
    /// <remarks>
    /// Read under <see cref="Chalk.Catalog.Disclosure.Test"/>, which is nothing but these shapes,
    /// and under <see cref="Chalk.Catalog.Disclosure.AggregateOnly"/>, where they permit a test as
    /// the <c>FILTER</c> of a permitted aggregate. Empty under <c>TEST</c> is refused at
    /// registration: a rule that discloses neither the value nor any comparison of it says nothing
    /// at all.
    /// </remarks>
    public IReadOnlyList<TestShape> Tests { get; init; } = [];
}

/// <summary>
/// One comparison a <see cref="Chalk.Catalog.Disclosure.Test"/> rule permits over the column (D261).
/// </summary>
/// <remarks>
/// The other operand must be a dynamic parameter, a literal or a context reference bound per
/// request — never another column, which is what keeps a <c>VALUES</c> list from turning one probe
/// into a thousand.
/// </remarks>
public enum TestShape
{
    Unspecified = 0,

    /// <summary><c>col = ?</c>.</summary>
    Equals = 1,

    /// <summary><c>col &lt;&gt; ?</c>.</summary>
    NotEquals = 2,

    /// <summary><c>col IN (a, b, …)</c>, every element an operand of the permitted kinds.</summary>
    In = 3,
}

/// <summary>
/// What a principal may see of one column in one row. Closed on purpose: the rewrite has to know
/// what each of them means.
/// </summary>
public enum Disclosure
{
    Unspecified = 0,

    /// <summary>The value, as it is.</summary>
    Full = 1,

    /// <summary>The column's mask in place of the value.</summary>
    Masked = 2,

    /// <summary>No value use; the listed aggregates only, guarded by the group-size floor.</summary>
    AggregateOnly = 3,

    /// <summary>
    /// Nothing: a placeholder in a projection. What leaf sanitisation guarantees is that the
    /// <em>raw</em> value reaches no use at all — a predicate the statement writes over the column
    /// compares the stand-in rather than being refused (§3.1, D195).
    /// </summary>
    None = 4,

    /// <summary>
    /// <see cref="None"/> to every use but one: a comparison of a shape the rule's
    /// <see cref="DisclosureRule.Tests"/> names, whose other operands are parameters, literals or
    /// context references, is computed in the leaf over the raw value and disclosed as one boolean
    /// (D261, <c>docs/design/36-test-verdict.md</c>).
    /// </summary>
    Test = 5,
}

/// <summary>Where a table's row predicate is evaluated.</summary>
public enum Enforcement
{
    Unspecified = 0,

    /// <summary>The predicate travels to a source that can take it, under the M4/M5 rules.</summary>
    Pushdown = 1,

    /// <summary>
    /// Rows are fetched and filtered in-process, so the tenant set never leaves it — what a shared or
    /// query-logging source wants.
    /// </summary>
    Local = 2,

    /// <summary>
    /// <see cref="Pushdown"/>, and a plan in which a remote table's row predicate would be evaluated
    /// locally is refused with <c>POLICY</c> naming the table and the shape the source cannot take
    /// (D199). For a source holding every tenancy's rows, a silent full fetch is the worse failure.
    /// It is a claim about a <em>source</em>, so declaring it on a table the client scans is refused
    /// at registration naming the table and the source kind: the constraint could there be neither
    /// met nor broken.
    /// </summary>
    PushdownRequired = 3,
}

/// <summary>
/// What a host asserts about a source before Chalk stops filtering its rows (D156, D310): the
/// pre-conditions under which trusting the source's own row-level security is sound. Chalk can check
/// none of them, and never tells a source who is asking; if any one is false, every row the source
/// returns is disclosed. A builder's <c>TrustSourceRowLevelSecurity</c> takes all three by name and
/// refuses fewer.
/// </summary>
[Flags]
public enum RowLevelSecurityPreconditions
{
    /// <summary>Nothing asserted: the value a host has not filled in, refused where trust is declared.</summary>
    None = 0,

    /// <summary>
    /// Every connection identifies the principal to the source — per-principal credentials, a session
    /// role, or a session variable the source's policies read — because Chalk itself never does.
    /// </summary>
    ConnectionIdentifiesPrincipal = 1,

    /// <summary>
    /// Row-level security is enabled, and forced for the table owner too, on every entitled table of
    /// the source.
    /// </summary>
    PoliciesEnabledAndForced = 2,

    /// <summary>The source's policies admit exactly the rows the entitlement's row predicate would.</summary>
    PoliciesMatchEntitlements = 4,
}

/// <summary>What <see cref="RowLevelSecurityPreconditions"/> leaves unasserted.</summary>
public static class RowLevelSecurityPreconditionsExtensions
{
    private const RowLevelSecurityPreconditions Every =
        RowLevelSecurityPreconditions.ConnectionIdentifiesPrincipal
        | RowLevelSecurityPreconditions.PoliciesEnabledAndForced
        | RowLevelSecurityPreconditions.PoliciesMatchEntitlements;

    /// <summary>The pre-conditions <paramref name="asserted"/> does not name; <see cref="RowLevelSecurityPreconditions.None"/> when it names them all.</summary>
    public static RowLevelSecurityPreconditions Missing(this RowLevelSecurityPreconditions asserted) => Every & ~asserted;

    /// <summary>
    /// Refuses an assertion short of every pre-condition, naming the ones left out: trusting a
    /// source's row-level security is a claim Chalk cannot check, so the host states all of it.
    /// </summary>
    public static void RequireAll(this RowLevelSecurityPreconditions asserted, string paramName)
    {
        var missing = asserted.Missing();
        if (missing != RowLevelSecurityPreconditions.None)
        {
            throw new ArgumentException(
                "trusting a source's row-level security requires every pre-condition asserted by name, "
                + $"and this call does not assert {missing}. Chalk cannot check any of them; if one is "
                + "false, every row the source returns is disclosed.",
                paramName);
        }
    }
}

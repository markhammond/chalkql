namespace Chalk.Entitlements.Tenancy;

/// <summary>
/// One way a restricted table's rows resolve to something the principal holds
/// (<c>docs/design/16-entitlements.md</c> §5; <c>29-entitlements-as-a-wrapper.md</c> §4, D215).
/// </summary>
/// <remarks>
/// <b>Every table reference here carries its schema</b> (D270): a table name alone is ambiguous
/// catalog-wide, because two sources may hold a table of one name. A <em>column</em> name does not
/// need one — <see cref="Column"/>, <see cref="ParentKey"/> and the realms' members are all scoped
/// by the declared table they were obtained from, and a <see cref="Column"/> handle carries that
/// table — so what is qualified is the explicit <see cref="Through"/> parent, every
/// <see cref="VisibilityStep"/>, and both ends of an association.
///
/// A restricted table carries one or more of these, <b>OR</b>-ed: a row is visible when any of them
/// reaches it. The visibility rules and the column rules then apply over the disjunction, exactly as
/// they did when a table had one mode. A table with none is <c>Unrestricted</c>: every row visible,
/// and its column rules still apply.
/// <para>
/// Internal since D270: this is what the declaration verbs of <see cref="TenancyRestrictions"/>
/// produce, in the strings the compiler reads, and the surface a host writes is the handles
/// (docs/design/45-typed-tenancy-surface.md §1).
/// </para>
/// </remarks>
internal sealed class Restriction
{
    /// <summary>Which of the three this is.</summary>
    public required RestrictionKind Kind { get; init; }

    /// <summary>The dimension, for <see cref="RestrictionKind.Dimension"/>.</summary>
    public GrantDimension? Dimension { get; init; }

    /// <summary>
    /// The host's own boolean SQL over the table and the context, for
    /// <see cref="RestrictionKind.Predicate"/>. Kept verbatim: the compiler neither parses it nor
    /// rewrites it.
    /// </summary>
    public string Predicate { get; init; } = "";

    /// <summary>
    /// The column holding the row's owner, for <see cref="RestrictionKind.ResourceOwner"/> — or, for
    /// the structured predicate <see cref="In"/>, the column the membership test reads.
    /// </summary>
    public string Column { get; init; } = "";

    /// <summary>
    /// The context list the structured predicate <see cref="In"/> tests against, without the
    /// <c>@ctx.</c> prefix. Empty for free text, which <c>Reconcile</c> then has to read (D215).
    /// </summary>
    public string List { get; init; } = "";

    /// <summary>What the resource owner sees of a row they own (D206, D215).</summary>
    public ResourceOwnerSees Sees { get; init; } = ResourceOwnerSees.Full;

    /// <summary>
    /// The parent table, for <see cref="RestrictionKind.Through"/>. Empty means "read it off the
    /// declared foreign key over <see cref="Column"/>".
    /// </summary>
    public string ParentTable { get; init; } = "";

    /// <summary>
    /// The parent's schema, for the explicit <see cref="RestrictionKind.Through"/> form. Empty means
    /// the entitled table's own — which is what the foreign-key form always resolves to, since a
    /// foreign key names a table of its own schema.
    /// </summary>
    public string ParentSchema { get; init; } = "";

    /// <summary>
    /// The parent's key column, for <see cref="RestrictionKind.Through"/>. Empty means the same.
    /// </summary>
    public string ParentKey { get; init; } = "";

    /// <summary>
    /// The tenancy kind this restriction carries, for <see cref="RestrictionKind.Direct"/>,
    /// <see cref="RestrictionKind.Inherited"/> and <see cref="RestrictionKind.Related"/> (D265 §1).
    /// A kind is declared once on the policy and a table then names the handle it got back.
    /// </summary>
    public string TenancyKind { get; init; } = "";

    /// <summary>
    /// The path's steps outward from this table, for <see cref="RestrictionKind.Inherited"/> and
    /// <see cref="RestrictionKind.Related"/> (D265 §1). Every step names a table; a step names a
    /// column only where the pair has more than one foreign key between them.
    /// </summary>
    public IReadOnlyList<VisibilityStep> Steps { get; init; } = [];

    /// <summary>A dimension the builder resolved from a kind handle.</summary>
    public static Restriction Of(GrantDimension dimension)
    {
        ArgumentNullException.ThrowIfNull(dimension);
        return new Restriction { Kind = RestrictionKind.Dimension, Dimension = dimension };
    }

    /// <summary>The host's own predicate over the context, kept verbatim.</summary>
    public static Restriction Text(string sql) =>
        new() { Kind = RestrictionKind.Predicate, Predicate = sql };

    /// <summary>
    /// The structured form of the commonest host predicate: <c>column IN (@ctx.list)</c>. The same
    /// SQL as <see cref="Text"/> would give, and reconcilable, because the package knows which
    /// column and which list rather than having to read them back out of the text (D215).
    /// </summary>
    public static Restriction In(string column, string list) =>
        new()
        {
            Kind = RestrictionKind.Predicate,
            Predicate = $"{column} IN (@ctx.{list})",
            Column = column,
            List = list,
        };

    /// <summary>
    /// The row's owner, and what they see of it (D206, D215): the designated column holds the
    /// principal who owns the row — often <c>created_by</c>, sometimes a transferable
    /// <c>owner_id</c> — and their implicit self-access is a fail-safe distinct from the
    /// grant-driven subject dimension.
    /// </summary>
    public static Restriction ResourceOwner(string column, ResourceOwnerSees sees = ResourceOwnerSees.Full) =>
        new() { Kind = RestrictionKind.ResourceOwner, Column = column, Sees = sees };

    /// <summary>
    /// A row is visible when the parent row <paramref name="column"/> names is visible to the same
    /// principal (§3.13, D227): a chat message reached through its thread, a line item through its
    /// order — a table that carries no tenancy column of its own.
    /// </summary>
    /// <remarks>
    /// The parent and its key are read off the <b>declared foreign key</b> over the column, which is
    /// the association the schema already states; a source that declares none names them with the
    /// other overload. The parent must itself be restricted — through an unrestricted parent
    /// restricts nothing. The correlation key stays an ordinary column: the planner's own join reads
    /// it raw beneath the child's sanitiser, so the mechanism never needs it disclosed, and the
    /// package leaves it in full unless the host puts it in a realm.
    /// </remarks>
    public static Restriction Through(string column) =>
        new() { Kind = RestrictionKind.Through, Column = column };

    /// <summary>The same, where the source declares no foreign key to read the parent off.</summary>
    public static Restriction Through(
        string column, string parentSchema, string parentTable, string parentKey) =>
        new()
        {
            Kind = RestrictionKind.Through,
            Column = column,
            ParentSchema = parentSchema,
            ParentTable = parentTable,
            ParentKey = parentKey,
        };

    /// <summary>
    /// The tenancy key of <paramref name="kind"/> physically exists on this table, in
    /// <paramref name="column"/> (D265 §0, §1): the first of the owner's three words.
    /// </summary>
    public static Restriction Direct(string kind, string column) =>
        new() { Kind = RestrictionKind.Direct, TenancyKind = kind, Column = column };
}

/// <summary>
/// One step of an <see cref="RestrictionKind.Inherited"/> or <see cref="RestrictionKind.Related"/>
/// path (D265 §1): the table stepped to, and — where the pair has more than one foreign key between
/// them, or a table references itself — the column the step joins on.
/// </summary>
/// <remarks>
/// <see cref="On"/> names the <em>bridge's</em> column for an up-step and <em>our</em> column for a
/// down-step, which is in both cases the column of the table holding the foreign key. Empty means
/// "the only one", and the compiler refuses an ambiguous step naming what would have made it
/// unambiguous.
/// </remarks>
internal sealed class VisibilityStep
{
    /// <summary>The schema the step's table belongs to (D270 §3). Empty means the entitled table's.</summary>
    public string Schema { get; init; } = "";

    /// <summary>The table this step arrives at.</summary>
    public required string Table { get; init; }

    /// <summary>The foreign-key column the step joins on, or empty for "the only one".</summary>
    public string On { get; init; } = "";
}

/// <summary>
/// One tenancy kind, declared once on the policy (D265 §1): an organisational container, or the
/// individual a row is about, confined to a container kind.
/// </summary>
internal sealed class TenancyKind
{
    /// <summary>The host's own name for it: <c>org</c>, <c>vendor</c>, <c>member</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Whether this resolves a row to an individual rather than to a container.</summary>
    public bool IsSubject { get; init; }

    /// <summary>
    /// For a subject kind, the tenancy kinds a subject grant of it may be confined by — one or
    /// several (D266 §2). Empty is not legal on a subject kind: a grant that reaches every tenancy
    /// says so with <c>Tenancy.Anywhere</c> at the grant, never at the declaration.
    /// </summary>
    public IReadOnlyList<string> WithinKinds { get; init; } = [];

    /// <summary>
    /// The first kind <see cref="WithinKinds"/> names, which is what the single-kind spelling
    /// declared and what <c>ForSubject(…, within: …)</c> resolves to (D266 §1).
    /// </summary>
    public string Within => WithinKinds.Count > 0 ? WithinKinds[0] : "";
}

/// <summary>Which of the three kinds a <see cref="Restriction"/> is (§5, D215).</summary>
internal enum RestrictionKind
{
    /// <summary>A dimension: the rows resolve through a column or a foreign-key path.</summary>
    Dimension = 1,

    /// <summary>A predicate the host writes itself, over the bound context.</summary>
    Predicate = 2,

    /// <summary>The row's owner sees it, wherever it is.</summary>
    ResourceOwner = 3,

    /// <summary>
    /// The rows resolve through a parent row: this one is visible when that one is (§3.13, D227).
    /// </summary>
    Through = 4,

    /// <summary>
    /// The tenancy key of a declared kind is physically on this table (D265 §0). The spelling
    /// <see cref="Restriction.Direct"/> writes; <see cref="Dimension"/> is what it compiles to.
    /// </summary>
    Direct = 5,

    /// <summary>
    /// The row derives its tenancy of a declared kind from an owning, parent relation, down a path of
    /// declared foreign keys (D265 §0).
    /// </summary>
    Inherited = 6,

    /// <summary>
    /// The row participates in a tenancy of a declared kind because some related rows belong to it:
    /// one step up to the bridge, then down to the endpoint (D265 §0).
    /// </summary>
    Related = 7,
}

/// <summary>What the owner of a row sees of it, wherever it is (§5, D206, D215).</summary>
public enum ResourceOwnerSees
{
    /// <summary>
    /// The default: the principal owns the values, so a row they own is disclosed in full whatever
    /// its tenancy.
    /// </summary>
    Full = 0,

    /// <summary>
    /// The tenancy rules unchanged, so on a tenancy they hold no grant in the owner sees that the
    /// row exists and nothing else.
    /// </summary>
    ByRules = 1,
}

/// <summary>
/// What one access rule discloses of a realm or a column (§5, D222).
/// </summary>
/// <remarks>
/// The four words are what a rule <em>grants</em>, and there are no exclusion words: precedence is
/// by <b>order</b> (D222). A rule that caps what a later rule would have given is simply an earlier
/// rule granting less — <see cref="None"/> for "nothing of this, whatever else says" — and the
/// verdict is the first matching stop-rule, else the last matching continue-rule, else
/// <see cref="None"/> again, because on a <b>protected</b> column — one in a realm, or one any rule
/// names — a role that says nothing grants nothing (D216). A column in no realm that no rule names
/// is not protected at all and keeps the table's default (D159), which is the host's declared choice
/// rather than silence.
/// </remarks>
public enum Verdict
{
    /// <summary>The value, as it is.</summary>
    Full = 1,

    /// <summary>The mask in place of the value.</summary>
    Mask = 2,

    /// <summary>The listed population aggregates only, under the group-size floor.</summary>
    AggregateOnly = 3,

    /// <summary>Nothing: a placeholder, and the raw value reaches no use at all.</summary>
    None = 4,

    /// <summary>
    /// <see cref="None"/> to every use but one: the comparisons <see cref="AccessRule.Tests"/> names
    /// are computed in the leaf over the raw value and disclosed as one boolean each (D261,
    /// <c>docs/design/36-test-verdict.md</c>) — a support desk confirming a national id, a host
    /// verifying a token.
    /// </summary>
    Test = 5,
}

/// <summary>
/// One comparison a <see cref="Verdict.Test"/> rule permits over the column (D261).
/// </summary>
/// <remarks>
/// The other operands must be dynamic parameters, literals or context references bound per request.
/// A comparison with a <em>column</em> is refused precisely so that a <c>VALUES</c> list cannot turn
/// one probe into a thousand, and every other shape — <c>LIKE</c>, a function of the column,
/// <c>BETWEEN</c>, <c>IS NULL</c> — sees the placeholder as under <see cref="Verdict.None"/>.
/// </remarks>
public enum Test
{
    /// <summary><c>col = ?</c>.</summary>
    Equals = 1,

    /// <summary><c>col &lt;&gt; ?</c>.</summary>
    NotEquals = 2,

    /// <summary><c>col IN (?, ?)</c>.</summary>
    In = 3,
}

/// <summary>
/// One way a table's rows resolve to a grant (§5, D204): a <b>tenancy</b> dimension resolves a row
/// to its organisational container, a <b>subject</b> dimension to the individual it is about.
/// </summary>
/// <remarks>
/// <para>
/// "Subject" is the privacy vocabulary's own word for the individual personal data concerns, and it
/// does not collide with the caller's identity the way "principal" would.
/// </para>
/// <para>
/// <see cref="Column"/> is a column of the table, or a dotted <em>path</em> over declared foreign
/// keys — <c>member.org</c> on <c>orders</c> means "the organization of the member this order is
/// about". A path is resolved at compile time: each segment but the last names a foreign key of the
/// table it starts from, and the last names a dimension of the table it arrives at. Because §2 lets
/// a compiled predicate hold only scalars and lists — never a relation — the path must end at a
/// column the entitled table itself carries; the compiler proves the association and refuses a path
/// whose target the table does not carry, naming what would be needed.
/// </para>
/// </remarks>
internal sealed class GrantDimension
{
    /// <summary>The host's own name for the dimension: <c>org</c>, <c>member</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>A column of the table, or a dotted path over declared foreign keys.</summary>
    public required string Column { get; init; }

    /// <summary>Whether this resolves a row to an individual rather than to a container.</summary>
    public bool IsSubject { get; init; }

    /// <summary>
    /// For a subject dimension, the <see cref="Kind"/>s of the tenancy dimensions a subject grant
    /// may be confined by — one or several at once (D266 §2). Empty is not legal on a subject
    /// dimension: a grant that reaches every tenancy says so with <see cref="Tenancy.Anywhere"/> at
    /// the grant, never at the declaration.
    /// </summary>
    public IReadOnlyList<string> WithinKinds { get; init; } = [];

    /// <summary>
    /// The first kind <see cref="WithinKinds"/> names: what the single-kind spelling declared, and
    /// what <c>ForSubject(…, within: …)</c> resolves its unnamed confinement to (D266 §1).
    /// </summary>
    public string Within => WithinKinds.Count > 0 ? WithinKinds[0] : "";

    /// <summary>A tenancy dimension: the row's organisational container.</summary>
    public static GrantDimension Tenancy(string kind, string column) =>
        new() { Kind = kind, Column = column };
}

/// <summary>
/// Which roles see a table's rows at all, and in which request scopes (§5).
/// </summary>
/// <remarks>
/// Without one, every declared role sees the rows of the tenancies it holds a grant in. With one,
/// only the roles it names do — and, where it names scopes, only in a request bound with one of
/// them. It restricts; it never widens.
/// </remarks>
public sealed class VisibilityRule
{
    /// <summary>The roles whose grants make this table's rows visible.</summary>
    public required IReadOnlyList<Role> Roles { get; init; }

    /// <summary>The request scopes this rule holds in; empty means every scope.</summary>
    public IReadOnlyList<Scope> Scopes { get; init; } = [];

    internal IReadOnlyList<string> ScopeNames => Names(Scopes);

    internal static IReadOnlyList<string> Names(IReadOnlyList<Scope> scopes)
    {
        if (scopes.Count == 0)
        {
            return [];
        }

        var names = new List<string>(scopes.Count);
        foreach (var scope in scopes)
        {
            names.Add(scope.Name);
        }

        return names;
    }
}

/// <summary>
/// What one set of roles may see of one realm, one column, or every protected column (§5, D222).
/// </summary>
/// <remarks>
/// <para>
/// At most one of <see cref="Realm"/> and <see cref="Column"/> is set. A rule on a realm reaches
/// every column of it, which is what realms are for: the policy names the sensitivity once and the
/// schema says which columns carry it. A rule naming <em>neither</em> is about every protected
/// column of the table — the explicit permissive form a host writes for a role that should see
/// everything the policy protects (D216), and an ordinary rule like any other, so where the host
/// puts it in the list is what it means.
/// </para>
/// <para>
/// <b>The list is the priority (D222).</b> The rules of a table are read in the order the host wrote
/// them; a rule matches when the row's tenancy holds one of its roles and its <see cref="When"/>
/// holds; a matching rule sets the tentative verdict; and <see cref="StopOnMatch"/> — true by
/// default — ends the reading there. So the answer is the first matching stop-rule if there is one,
/// otherwise the last matching continue-rule, otherwise nothing.
/// </para>
/// </remarks>
public sealed class AccessRule
{
    /// <summary>The roles this rule speaks for.</summary>
    public required IReadOnlyList<Role> Roles { get; init; }

    /// <summary>The realm this rule is about, or null when it names a column or neither.</summary>
    public Realm? Realm { get; init; }

    /// <summary>The column this rule is about, or null when it names a realm or neither.</summary>
    public Column? Column { get; init; }

    /// <summary>What these roles are granted here.</summary>
    public required Verdict Grants { get; init; }

    /// <summary>
    /// Whether a match here ends the evaluation (D222). True — the default — makes this rule the
    /// answer as soon as it matches, whatever later rules say; false makes it a tentative answer a
    /// later matching rule may replace, so that the <em>last</em> matching continue-rule wins where
    /// no stop-rule matched at all.
    /// </summary>
    public bool StopOnMatch { get; init; } = true;

    /// <summary>
    /// An optional further condition on the row, AND-ed onto the roles this rule speaks for (D221):
    /// boolean SQL over the table's own columns and the context vocabulary of §2.
    /// </summary>
    /// <remarks>
    /// Layer B's spelling of layer A's <c>DisclosureRule.when</c>, so that a rule can say "in full
    /// when the row is about the caller" — <c>When = Sql.Of("role = 'analyst'")</c> — without the
    /// host writing a descriptor. It may read any column of the row, protected or not: what a
    /// condition discloses is one bit, and it is the policy author's own choice (§1, D220). Null is
    /// what every rule was until now: the roles alone.
    /// </remarks>
    public Sql? When { get; init; }

    /// <summary>
    /// The mask these roles see under <see cref="Verdict.Mask"/>, over the column's own (D219).
    /// </summary>
    /// <remarks>
    /// A mask that covers more than one column is a <b>template</b> in lambda notation, whose
    /// parameter stands for the column being masked:
    /// <c>Sql.Of("lambda v: FINGERPRINT(v, @ctx.mask_key)")</c> masks every column of a realm, and
    /// the same template is reused across columns and tables. The compiler substitutes the parameter
    /// token-wise — an identifier token only, never text inside a string literal or a quoted
    /// identifier — and the wire carries the instantiated text. A mask without the <c>lambda</c>
    /// prefix is taken verbatim. Null leaves the column's default mask, which is
    /// <c>'********'</c> for a string and NULL for anything else.
    /// </remarks>
    public Sql? Mask { get; init; }

    /// <summary>
    /// What these roles see in place of the value under <see cref="Verdict.None"/> (D224): a SQL
    /// scalar expression of the column's type.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The redaction's counterpart to <see cref="Mask"/>, and it wins over the column's own
    /// placeholder and over the request's <c>PlaceholderPolicy</c> — so one role may be handed
    /// <c>'withheld'</c> where another gets the policy's NULL, without the host writing a
    /// descriptor. Under D222 a catch-all <see cref="Verdict.None"/> rule placed last gives a role
    /// one placeholder for every protected column it has no other rule for.
    /// </para>
    /// <para>
    /// It is refused on a rule whose <see cref="Grants"/> is anything but <see cref="Verdict.None"/>:
    /// a rule that discloses a value has nothing to stand in for it. The disclosure stays
    /// <c>REDACTED</c> and the raw value reaches no use, which is what tells this from a
    /// <see cref="Verdict.Mask"/> rule with a constant mask. Unlike a mask this is not a template:
    /// a placeholder that varied with the column would be a mask, and one that read the column at
    /// all is refused.
    /// </para>
    /// </remarks>
    public Sql? Placeholder { get; init; }

    /// <summary>
    /// The population aggregates permitted under <see cref="Verdict.AggregateOnly"/> (D190). Ignored
    /// for any other verdict, and required for that one.
    /// </summary>
    public IReadOnlyList<Aggregate> Aggregates { get; init; } = [];

    /// <summary>
    /// The group-size floor those aggregates are guarded by (D211): 0 inherits whatever the host
    /// gives at planning, 1 disables the guard for the column, 2 or more is the floor.
    /// </summary>
    public int MinGroupSize { get; init; }

    /// <summary>
    /// The comparisons these roles may make against the column without seeing it (D261,
    /// <c>docs/design/36-test-verdict.md</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Required under <see cref="Verdict.Test"/>, which is nothing but these shapes: each permitted
    /// comparison the statement writes over the column — its other operands parameters, literals or
    /// context references — is computed in the leaf over the raw value and disclosed as one boolean,
    /// and every other use sees the placeholder.
    /// </para>
    /// <para>
    /// Honoured on an <see cref="Verdict.AggregateOnly"/> rule too, where it permits a test of a
    /// permitted shape as the <c>FILTER</c> of a permitted aggregate —
    /// <c>COUNT(*) FILTER (WHERE national_id = ?)</c> — guarded by
    /// <see cref="MinGroupSize"/> exactly as any population aggregate is. It is refused on a rule
    /// granting anything else: a rule that discloses the value has nothing to test, and one that
    /// withholds it entirely says so.
    /// </para>
    /// </remarks>
    public IReadOnlyList<Test> Tests { get; init; } = [];

    // The strings the compiler reads. Every one of them comes from a handle, so the name is the one
    // that entered at the declaration and nothing else can have produced it (D270 §1).

    internal string RealmName => Realm?.Name ?? "";

    internal string ColumnName => Column?.Name ?? "";

    internal string WhenText => When?.Text ?? "";

    internal string MaskText => Mask?.Text ?? "";

    internal string PlaceholderText => Placeholder?.Text ?? "";

    internal IReadOnlyList<string> AggregateNames => Entitlements.Tenancy.Aggregates.Sql(Aggregates);
}

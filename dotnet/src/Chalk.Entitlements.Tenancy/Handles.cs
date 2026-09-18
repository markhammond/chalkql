using Chalk.Catalog;

namespace Chalk.Entitlements.Tenancy;

// The typed surface of the tenancy package (docs/design/45-typed-tenancy-surface.md §1, D270).
//
// Every name a policy speaks enters once, at its declaration, and comes back as a handle; every
// later mention is that handle. A handle is a runtime value obtained by name — from the policy, a
// source or a table — and never generic over a row type, because a host that layers its own API
// over Chalk builds policies from configuration at runtime and a `Table<T>` would shut that door
// (§0, as informed by the user).
//
// Each handle is a `readonly record struct` over the *declaration* the name entered as, so equality
// is identity of that declaration: two handles for one name are one, and a handle of another
// policy's is refused where it is used rather than silently matched by spelling. None of them
// converts from a string, implicitly or otherwise.

/// <summary>
/// A tenancy kind: the organisational container a row belongs to (§1, D265 §1). Obtained from
/// <see cref="TenancyPolicy.Tenancy(string)"/>.
/// </summary>
/// <remarks>
/// Distinct from <see cref="SubjectKind"/> so that a subject kind cannot be used where a container
/// belongs, and so that <c>within:</c> — which confines a subject grant to containers — accepts
/// only containers.
/// </remarks>
public readonly record struct Kind
{
    internal Kind(KindDeclaration declaration) => Declaration = declaration;

    internal KindDeclaration? Declaration { get; }

    /// <summary>The host's own name for it: <c>org</c>, <c>region</c>, <c>vendor</c>.</summary>
    public string Name => Declaration?.Name ?? "";

    public override string ToString() => Name;
}

/// <summary>
/// A subject kind: the individual a row is about, confined to one or more tenancy kinds (§1, D204,
/// D266 §2). Obtained from <see cref="TenancyPolicy.Subject(string, IEnumerable{Kind})"/>.
/// </summary>
public readonly record struct SubjectKind
{
    internal SubjectKind(KindDeclaration declaration) => Declaration = declaration;

    internal KindDeclaration? Declaration { get; }

    /// <summary>The host's own name for it: <c>member</c>.</summary>
    public string Name => Declaration?.Name ?? "";

    public override string ToString() => Name;
}

/// <summary>
/// A role a grant may be held in (§1). Obtained from <see cref="TenancyPolicy.Role(string)"/>, or as
/// one of the two instances <see cref="Roles.Owner"/> and <see cref="Roles.Visible"/>, which no
/// string produces (§6 (b), D269 (c)).
/// </summary>
public readonly record struct Role
{
    internal Role(RoleDeclaration declaration) => Declaration = declaration;

    internal RoleDeclaration? Declaration { get; }

    /// <summary>
    /// The role's name as a message writes it — the host's own for a declared role, and the
    /// constant's own spelling for one of the two markers.
    /// </summary>
    public string Name => Declaration?.Name ?? "";

    /// <summary>Whether this is one of the two markers rather than a role a grant can be held in.</summary>
    internal bool IsMarker => Declaration is { Marker: not RoleMarker.None };

    public override string ToString() => Name;
}

/// <summary>
/// A sensitivity label a table's columns carry and an access rule speaks about (§1). Obtained from
/// <see cref="TenancyPolicy.Realm(string)"/>.
/// </summary>
public readonly record struct Realm
{
    internal Realm(RealmDeclaration declaration) => Declaration = declaration;

    internal RealmDeclaration? Declaration { get; }

    public string Name => Declaration?.Name ?? "";

    public override string ToString() => Name;
}

/// <summary>
/// A request scope a grant or a visibility rule names (§1). Obtained from
/// <see cref="TenancyPolicy.Scope(string)"/>.
/// </summary>
public readonly record struct Scope
{
    internal Scope(ScopeDeclaration declaration) => Declaration = declaration;

    internal ScopeDeclaration? Declaration { get; }

    public string Name => Declaration?.Name ?? "";

    public override string ToString() => Name;
}

/// <summary>
/// The source a table belongs to (§1): one schema of the catalog the policy was declared over.
/// Obtained from <see cref="TenancyPolicy.Source(string)"/>.
/// </summary>
public readonly record struct Source : Chalk.Sources.IRefreshTarget
{
    internal Source(SourceDeclaration declaration) => Declaration = declaration;

    internal SourceDeclaration? Declaration { get; }

    /// <summary>The schema's name, which is how a table of it is addressed.</summary>
    public string Name => Declaration?.Schema.Name ?? "";

    /// <summary>The identifier of the runtime that serves it.</summary>
    public string SourceId => Declaration?.Schema.SourceId ?? "";

    /// <summary>
    /// This handle as a refresh target (D271 (f)): <c>refresh.Refresh(sqlite)</c> re-introspects this
    /// source, discovery included. A handle carries both the runtime's id and the schema's name, so
    /// the engine resolves it without either being a guess.
    /// </summary>
    string Chalk.Sources.IRefreshTarget.Schema => Name;

    /// <summary>Empty: a source handle names the whole source.</summary>
    string Chalk.Sources.IRefreshTarget.Table => "";

    /// <summary>
    /// One table of this source, by name. The policy speaks about it from here on, and the name is
    /// checked against the schema when the policy compiles.
    /// </summary>
    public Table Table(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return new Table(Required().TableNamed(name));
    }

    internal SourceDeclaration Required() =>
        Declaration ?? throw new InvalidOperationException(
            "a default Source handle names no source. Obtain one from TenancyPolicy.Source(id) "
            + "(docs/design/45-typed-tenancy-surface.md §1, D270).");

    public override string ToString() => Name;
}

/// <summary>
/// One table the policy speaks about (§1), and where that table's own declarations are written.
/// Obtained from <see cref="TenancyPolicy.Table(string)"/> or <see cref="Source.Table(string)"/>.
/// </summary>
/// <remarks>
/// Deliberately not generic. A host mapping its own configuration to a policy at runtime has no row
/// type to name, and a <c>Table&lt;T&gt;</c> would shut that door (§0, §6 (a), as informed by the
/// user). A host that does have row types may add a selector overload in its own POCO layer; nothing
/// in the core depends on one.
/// </remarks>
public readonly record struct Table : Chalk.Sources.IRefreshTarget
{
    internal Table(TableDeclaration declaration) => Declaration = declaration;

    internal TableDeclaration? Declaration { get; }

    public string Name => Declaration?.Name ?? "";

    /// <summary>The source this table belongs to.</summary>
    public Source Source => new(Required().Source);

    /// <summary>
    /// This handle as a refresh target (D271 (f)): <c>refresh.Refresh(orders)</c> re-describes this
    /// one table, statistics included, and discovers nothing. It carries the source it came from, so
    /// a table of one name in two sources is never the wrong one.
    /// </summary>
    string Chalk.Sources.IRefreshTarget.SourceId => Declaration?.Source.Schema.SourceId ?? "";

    string Chalk.Sources.IRefreshTarget.Schema => Declaration?.Source.Schema.Name ?? "";

    string Chalk.Sources.IRefreshTarget.Table => Name;

    /// <summary>
    /// One column of this table, by name — or a dotted path over declared foreign keys, which
    /// <c>Direct</c> resolves at compile time (<c>member.org</c> on <c>orders</c> means "the
    /// organisation of the member this order is about").
    /// </summary>
    /// <remarks>
    /// The handle carries the table it came from, so a column of another table used here is refused
    /// by name when the policy compiles, where a string would have been looked up and silently
    /// found or silently missed.
    /// </remarks>
    public Column Column(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return new Column(Required().ColumnNamed(name));
    }

    /// <summary>
    /// Restricts this table: its rows are visible through the restrictions declared here, OR-ed
    /// together (D215). The visibility rules and the column rules then apply over that disjunction.
    /// </summary>
    public Table Tenancy(Action<TenancyRestrictions> declare)
    {
        ArgumentNullException.ThrowIfNull(declare);
        var declaration = Required();
        var builder = new TenancyRestrictions(declaration);
        declare(builder);
        declaration.AddRestrictions(builder.Build());
        return this;
    }

    /// <summary>
    /// Every row of this table is visible (D215). Its column rules still apply, so a reference table
    /// with one sensitive column is declared here and not given a tenancy it does not have.
    /// </summary>
    public Table Unrestricted()
    {
        Required().ClearRestrictions();
        return this;
    }

    /// <summary>Which roles see this table's rows at all, and in which scopes.</summary>
    public Table Visible(VisibilityRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        Required().Visibility.Add(rule);
        return this;
    }

    /// <summary>Groups columns under one sensitivity, which the access rules then speak about.</summary>
    public Table Realm(Realm realm, params Column[] columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        Required().AddRealm(realm, columns);
        return this;
    }

    /// <summary>
    /// What one set of roles may see of one realm, one column, or — naming neither — of every
    /// protected column of the table. <b>Where it goes in the list is what it means</b> (D222).
    /// </summary>
    public Table Access(AccessRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        Required().Access.Add(rule);
        return this;
    }

    /// <summary>
    /// The commonest rule, written without an initialiser: what these roles see of one column, and
    /// what stands in for it where they see nothing (§2).
    /// </summary>
    /// <remarks>
    /// This is the spelling a host mapping its own configuration reaches for, where the parts arrive
    /// as separate values rather than as a literal. It is the same rule <see cref="Access(AccessRule)"/>
    /// takes, and it goes in the same ordered list.
    /// </remarks>
    public Table Access(
        Column column, IReadOnlyList<Role> roles, Verdict grants, Sql? placeholder = null)
    {
        ArgumentNullException.ThrowIfNull(roles);
        return Access(new AccessRule
        {
            Roles = roles,
            Column = column,
            Grants = grants,
            Placeholder = placeholder,
        });
    }

    internal TableDeclaration Required() =>
        Declaration ?? throw new InvalidOperationException(
            "a default Table handle names no table. Obtain one from TenancyPolicy.Table(name) or "
            + "Source.Table(name) (docs/design/45-typed-tenancy-surface.md §1, D270).");

    public override string ToString() => Name;
}

/// <summary>
/// One column of one table (§1), or a dotted path over declared foreign keys that resolves to one.
/// Obtained from <see cref="Table.Column(string)"/>.
/// </summary>
public readonly record struct Column
{
    internal Column(ColumnDeclaration declaration) => Declaration = declaration;

    internal ColumnDeclaration? Declaration { get; }

    /// <summary>The column's name, or the dotted path as it was written.</summary>
    public string Name => Declaration?.Name ?? "";

    /// <summary>The table this column was obtained from.</summary>
    public Table Table => new(Required().Table);

    /// <summary>
    /// Declares that this column names a row of <paramref name="other"/>'s table, keyed by
    /// <paramref name="other"/> — the cross-source counterpart of a foreign key (§3, D270 (c)).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A foreign key is one source's claim about a table of its own schema, so two tables in two
    /// sources have nothing to state the association with; this is that statement. A path step
    /// resolves through it exactly as through a foreign key — the far table's declared unique key
    /// and the two columns' type agreement checked the same way, the direction checked and never
    /// inferred.
    /// </para>
    /// <para>
    /// <b>It is declared, not verified.</b> Nothing reads either source to check that every value of
    /// this column occurs in the other's; the host is asserting it, exactly as a declared foreign
    /// key is the source's own assertion (ADR 0025's "verified is read as declared"). Across two
    /// sources there is no transaction in which it could be checked, which is why the word is
    /// <em>association</em> rather than <em>constraint</em>.
    /// </para>
    /// </remarks>
    /// <returns>
    /// The association, for the <see cref="CatalogContext.Associations"/> the host registers. It is
    /// also recorded on the policy these handles came from, which is what lets the compiler resolve
    /// a step through it.
    /// </returns>
    public AssociationDescriptor References(Column other) =>
        Required().Table.Policy.Associate(this, other);

    internal ColumnDeclaration Required() =>
        Declaration ?? throw new InvalidOperationException(
            "a default Column handle names no column. Obtain one from Table.Column(name) "
            + "(docs/design/45-typed-tenancy-surface.md §1, D270).");

    public override string ToString() => Name;
}

/// <summary>
/// SQL the host writes itself, in the three places a policy carries text rather than a name
/// (§1): <see cref="AccessRule.When"/>, <see cref="AccessRule.Mask"/>,
/// <see cref="AccessRule.Placeholder"/> — and the row predicate of
/// <see cref="TenancyRestrictions.Predicate(Sql)"/>, which is text for the same reason.
/// </summary>
/// <remarks>
/// It exists so that a column <em>name</em> cannot be passed where an expression belongs. The
/// package neither parses nor rewrites what it carries beyond the mask template's one substitution
/// (D219); it is the host's own SQL over the table's columns and the context vocabulary of §2.
/// </remarks>
public readonly record struct Sql
{
    private Sql(string text) => Text = text;

    /// <summary>The expression, verbatim.</summary>
    public string Text { get; }

    /// <summary>The expression <paramref name="text"/>, said out loud to be one.</summary>
    public static Sql Of(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new Sql(text);
    }

    public override string ToString() => Text ?? "";
}

/// <summary>
/// The population aggregates a <see cref="Verdict.AggregateOnly"/> rule may permit (§1, D190).
/// </summary>
/// <remarks>
/// The set is closed for the reason <see cref="Chalk.Entitlements.PopulationAggregates"/> gives: the
/// question is not "is this a function" but "does this report a population or an individual".
/// <c>Count</c> covers <c>COUNT(DISTINCT …)</c>, and <c>Sum0</c> is the form
/// <c>AGGREGATE_REDUCE_FUNCTIONS</c> rewrites <c>AVG</c> into, so permitting it is what keeps a
/// correct plan from being refused after the rewrite.
/// </remarks>
public enum Aggregate
{
    Count = 1,
    Sum = 2,
    Sum0 = 3,
    Avg = 4,
    StdDev = 5,
    StdDevPop = 6,
    StdDevSamp = 7,
    Variance = 8,
    VarPop = 9,
    VarSamp = 10,
    CovarPop = 11,
    CovarSamp = 12,
    Corr = 13,
    RegrCount = 14,
    RegrAvgX = 15,
    RegrAvgY = 16,
    RegrIntercept = 17,
    RegrR2 = 18,
    RegrSlope = 19,
    RegrSxx = 20,
    RegrSxy = 21,
    RegrSyy = 22,
    BoolAnd = 23,
    BoolOr = 24,
    Every = 25,
    Some = 26,
    ApproxCountDistinct = 27,
}

/// <summary>How an <see cref="Aggregate"/> is spelled in the SQL the compiler emits.</summary>
internal static class Aggregates
{
    internal static string Sql(Aggregate aggregate) => aggregate switch
    {
        Aggregate.Count => "COUNT",
        Aggregate.Sum => "SUM",
        Aggregate.Sum0 => "SUM0",
        Aggregate.Avg => "AVG",
        Aggregate.StdDev => "STDDEV",
        Aggregate.StdDevPop => "STDDEV_POP",
        Aggregate.StdDevSamp => "STDDEV_SAMP",
        Aggregate.Variance => "VARIANCE",
        Aggregate.VarPop => "VAR_POP",
        Aggregate.VarSamp => "VAR_SAMP",
        Aggregate.CovarPop => "COVAR_POP",
        Aggregate.CovarSamp => "COVAR_SAMP",
        Aggregate.Corr => "CORR",
        Aggregate.RegrCount => "REGR_COUNT",
        Aggregate.RegrAvgX => "REGR_AVGX",
        Aggregate.RegrAvgY => "REGR_AVGY",
        Aggregate.RegrIntercept => "REGR_INTERCEPT",
        Aggregate.RegrR2 => "REGR_R2",
        Aggregate.RegrSlope => "REGR_SLOPE",
        Aggregate.RegrSxx => "REGR_SXX",
        Aggregate.RegrSxy => "REGR_SXY",
        Aggregate.RegrSyy => "REGR_SYY",
        Aggregate.BoolAnd => "BOOL_AND",
        Aggregate.BoolOr => "BOOL_OR",
        Aggregate.Every => "EVERY",
        Aggregate.Some => "SOME",
        Aggregate.ApproxCountDistinct => "APPROX_COUNT_DISTINCT",
        _ => throw new ArgumentOutOfRangeException(
            nameof(aggregate), aggregate, "not a population aggregate"),
    };

    internal static IReadOnlyList<string> Sql(IReadOnlyList<Aggregate> aggregates)
    {
        if (aggregates.Count == 0)
        {
            return [];
        }

        var names = new List<string>(aggregates.Count);
        foreach (var aggregate in aggregates)
        {
            names.Add(Sql(aggregate));
        }

        return names;
    }
}

// ---------------------------------------------------------------------- the declarations

/// <summary>The declaration one tenancy or subject kind's name entered as (§1).</summary>
internal sealed class KindDeclaration
{
    internal required TenancyPolicy Policy { get; init; }

    internal required string Name { get; init; }

    internal bool IsSubject { get; init; }

    internal IReadOnlyList<Kind> WithinKinds { get; init; } = [];
}

/// <summary>Which of the two grantees a <see cref="Role"/> is, if either (D269 (c)).</summary>
internal enum RoleMarker
{
    /// <summary>An ordinary role, declared by the policy and holdable by a grant.</summary>
    None = 0,

    /// <summary>The row's resource owner.</summary>
    Owner = 1,

    /// <summary>Everyone the table's row predicate admits.</summary>
    Visible = 2,
}

/// <summary>The declaration one role's name entered as (§1).</summary>
internal sealed class RoleDeclaration
{
    internal TenancyPolicy? Policy { get; init; }

    internal required string Name { get; init; }

    internal RoleMarker Marker { get; init; }
}

/// <summary>The declaration one realm's name entered as (§1).</summary>
internal sealed class RealmDeclaration
{
    internal required TenancyPolicy Policy { get; init; }

    internal required string Name { get; init; }
}

/// <summary>The declaration one request scope's name entered as (§1).</summary>
internal sealed class ScopeDeclaration
{
    internal required TenancyPolicy Policy { get; init; }

    internal required string Name { get; init; }
}

/// <summary>The declaration one source entered as: the schema it describes (§1).</summary>
internal sealed class SourceDeclaration
{
    private readonly Dictionary<string, TableDeclaration> _tables =
        new(StringComparer.OrdinalIgnoreCase);

    internal required TenancyPolicy Policy { get; init; }

    internal required SchemaDescriptor Schema { get; init; }

    /// <summary>
    /// The declaration for <paramref name="name"/>, made once. Two handles for one name are one, so
    /// a table declared in two places accumulates into the same declaration rather than into two.
    /// </summary>
    internal TableDeclaration TableNamed(string name)
    {
        if (_tables.TryGetValue(name, out var known))
        {
            return known;
        }

        var declaration = new TableDeclaration { Policy = Policy, Source = this, Name = name };
        _tables[name] = declaration;
        Policy.Declared(declaration);
        return declaration;
    }
}

/// <summary>Everything one table declares, and the identity its <see cref="Table"/> handles share.</summary>
internal sealed class TableDeclaration
{
    private readonly Dictionary<string, ColumnDeclaration> _columns =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly List<Restriction> _restrictions = [];

    private readonly Dictionary<string, IReadOnlyList<string>> _realms =
        new(StringComparer.OrdinalIgnoreCase);

    internal required TenancyPolicy Policy { get; init; }

    internal required SourceDeclaration Source { get; init; }

    internal required string Name { get; init; }

    internal List<VisibilityRule> Visibility { get; } = [];

    internal List<AccessRule> Access { get; } = [];

    internal IReadOnlyList<Restriction> Restrictions => _restrictions;

    internal IReadOnlyDictionary<string, IReadOnlyList<string>> Realms => _realms;

    internal ColumnDeclaration ColumnNamed(string name)
    {
        if (_columns.TryGetValue(name, out var known))
        {
            return known;
        }

        var declaration = new ColumnDeclaration { Table = this, Name = name };
        _columns[name] = declaration;
        return declaration;
    }

    internal void AddRestrictions(IReadOnlyList<Restriction> restrictions) =>
        _restrictions.AddRange(restrictions);

    internal void ClearRestrictions() => _restrictions.Clear();

    internal void AddRealm(Realm realm, IReadOnlyList<Column> columns)
    {
        var declaration = Policy.Require(realm, $"tenancy.tables[{Name}].realm");
        var names = new List<string>(columns.Count);
        foreach (var column in columns)
        {
            names.Add(Policy.Require(column, this, $"tenancy.tables[{Name}].realm[{declaration.Name}]"));
        }

        _realms[declaration.Name] = names;
    }
}

/// <summary>The declaration one column name — or one dotted path — entered as (§1).</summary>
internal sealed class ColumnDeclaration
{
    internal required TableDeclaration Table { get; init; }

    internal required string Name { get; init; }
}

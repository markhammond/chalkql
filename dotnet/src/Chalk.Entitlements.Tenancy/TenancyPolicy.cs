using Chalk.Catalog;

namespace Chalk.Entitlements.Tenancy;

/// <summary>
/// Layer B: the tenancy model as C# declarations, and a compiler down to layer A's descriptors
/// (<c>docs/design/16-entitlements.md</c> §5, D204–D206; the typed surface of
/// <c>docs/design/45-typed-tenancy-surface.md</c>, D270).
/// </summary>
/// <remarks>
/// <para>
/// The core knows one small vocabulary — a row predicate and per-column disclosure rules — and
/// nothing of tenancies, roles, realms or scopes. This is a compiler into that vocabulary, and it is
/// the only place any of those words exist. A host with a different model writes its own compiler
/// and the core does not notice.
/// </para>
/// <para>
/// What the compiler may emit is bounded by §2: <b>only scalars and lists</b>, never a relation, so
/// every predicate and every rule condition folds natively into literals or into a semi-join against
/// a bound list. That is what keeps enforcement free of an N+1 round trip, and it is why a
/// foreign-key path has to resolve to a column the entitled table itself carries.
/// </para>
/// <para>
/// <b>Every name enters once.</b> A policy is declared over a schema or a catalog, and each name it
/// speaks — a kind, a role, a realm, a scope, a table, a column — comes back as a handle that every
/// later mention uses (D270). The handles are runtime values obtained by name, so a host mapping its
/// own configuration to a policy at runtime builds one exactly as a host with a static policy does:
/// nothing here is generic over a row type.
/// </para>
/// </remarks>
public sealed class TenancyPolicy
{
    private readonly List<SourceDeclaration> _sources = [];
    private readonly Dictionary<string, KindDeclaration> _kinds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RoleDeclaration> _roles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ScopeDeclaration> _scopes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RealmDeclaration> _realms = new(StringComparer.Ordinal);
    private readonly List<TableDeclaration> _tables = [];
    private readonly List<AssociationDescriptor> _associations = [];

    private TenancyPolicy(CatalogContext catalog)
    {
        Catalog = catalog;
        foreach (var schema in catalog.Schemas)
        {
            _sources.Add(new SourceDeclaration { Policy = this, Schema = schema });
        }
    }

    /// <summary>The catalog this policy was declared over, and is checked against when it compiles.</summary>
    internal CatalogContext Catalog { get; }

    /// <summary>
    /// Declares a policy over a catalog, whose <see cref="Source(string)"/> handles name the sources
    /// a table may belong to (§1, §2 as amended 2026-09-16).
    /// </summary>
    /// <remarks>
    /// Against the catalog rather than one schema, because a policy that spans sources — the
    /// marketplace's path from orders through the lines to the items, and the association of §3 —
    /// cannot tell two sources' <c>orders</c> apart by name. A <see cref="Table"/> is obtained from
    /// a <see cref="Source"/> and from nowhere else for the same reason.
    /// </remarks>
    public static TenancyPolicy Declare(CatalogContext catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return new TenancyPolicy(catalog);
    }

    // ------------------------------------------------------------------ the handles

    /// <summary>
    /// A tenancy kind: an organisational container a row belongs to (D265 §1). Declared once for the
    /// whole policy; a table then names it and nothing more about it.
    /// </summary>
    public Kind Tenancy(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return new Kind(KindNamed(name, isSubject: false, []));
    }

    /// <summary>
    /// A subject kind: the individual a row is about, confined to the tenancy kinds
    /// <paramref name="within"/> names — one, any subset of them, or all at once (D204, D266 §2).
    /// </summary>
    /// <remarks>
    /// A subject grant naming a kind outside this list is refused at binding, naming both. Which
    /// subsets actually reach a table is a separate question the table's own declarations answer: a
    /// confined grant applies where its kind <em>and every confining kind</em> resolve (D266 §3).
    /// The parameter takes <see cref="Kind"/> and nothing else, so a subject kind cannot confine a
    /// subject kind.
    /// </remarks>
    public SubjectKind Subject(string name, IEnumerable<Kind> within)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(within);
        var confining = new List<Kind>();
        foreach (var kind in within)
        {
            Require(kind, $"tenancy.kinds[{name}].within");
            confining.Add(kind);
        }

        return new SubjectKind(KindNamed(name, isSubject: true, confining));
    }

    /// <summary>
    /// A role a grant may be held in. A rule or a grant naming a role of another policy is refused
    /// where it is used, which is what the string form could only catch by spelling.
    /// </summary>
    public Role Role(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (_roles.TryGetValue(name, out var known))
        {
            return new Role(known);
        }

        var declaration = new RoleDeclaration { Policy = this, Name = name };
        _roles[name] = declaration;
        return new Role(declaration);
    }

    /// <summary>A request scope a grant or a visibility rule may name.</summary>
    public Scope Scope(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (_scopes.TryGetValue(name, out var known))
        {
            return new Scope(known);
        }

        var declaration = new ScopeDeclaration { Policy = this, Name = name };
        _scopes[name] = declaration;
        return new Scope(declaration);
    }

    /// <summary>
    /// A sensitivity a table's columns carry, which the access rules then speak about. The policy
    /// names the sensitivity once and each table says which of its columns carry it.
    /// </summary>
    public Realm Realm(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (_realms.TryGetValue(name, out var known))
        {
            return new Realm(known);
        }

        var declaration = new RealmDeclaration { Policy = this, Name = name };
        _realms[name] = declaration;
        return new Realm(declaration);
    }

    /// <summary>
    /// One source of the catalog, by its <b>schema name</b> — the federation-level name a statement
    /// qualifies a table with (A4), not the identifier of the runtime that serves it (§1, §2 as
    /// amended 2026-09-16). Every <see cref="Table"/> is obtained from one of these.
    /// </summary>
    public Source Source(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        foreach (var source in _sources)
        {
            if (string.Equals(source.Schema.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return new Source(source);
            }
        }

        throw new CatalogValidationException(
            "tenancy.sources",
            $"'{name}' names no source of this catalog, which holds "
            + $"{string.Join(", ", _sources.Select(s => s.Schema.Name))}. A source is named by its "
            + "schema name — what a statement qualifies a table with — rather than by the "
            + "identifier of the runtime that serves it "
            + "(docs/design/45-typed-tenancy-surface.md §1, D270).");
    }

    // A table handle is obtained from a Source and from nothing else: two sources may hold a table
    // of one name, so a bare `Table(name)` on the policy could not say which was meant. Deferred
    // pending the owner's decision on design 45 §1 (see ADR 0056).

    /// <summary>
    /// Permits global grants (D205). Off by default: a tier that may see everything is a decision a
    /// deployment makes once, not one a request makes.
    /// </summary>
    public TenancyPolicy AllowGlobalGrants(bool allow = true)
    {
        AllowsGlobalGrants = allow;
        return this;
    }

    /// <summary>
    /// Whether a global grant may be bound at all (D205).
    /// </summary>
    public bool AllowsGlobalGrants { get; private set; }

    /// <summary>
    /// The cross-source associations this policy's <see cref="Column.References(Column)"/> calls
    /// declared, for the <see cref="CatalogContext.Associations"/> the host registers (§3, D270 (c)).
    /// </summary>
    public IReadOnlyList<AssociationDescriptor> Associations => _associations;

    // ------------------------------------------------------------------ compiling

    /// <summary>
    /// Compiles this policy against the schema it was declared over, validating every declaration as
    /// it goes (§5): the columns exist and belong to the table that named them, a path resolves
    /// through declared foreign keys or declared associations and ends at a column of the declared
    /// kind, a subject kind's <c>within</c> names tenancy kinds, and every handle a rule names is one
    /// this policy declared.
    /// </summary>
    /// <exception cref="CatalogValidationException">
    /// The declarations and the schema disagree. The message names the table, the declaration and
    /// what would have made it resolve.
    /// </exception>
    public TenancyEntitlements Compile(IReadOnlyList<SchemaDescriptor> schemas)
    {
        ArgumentNullException.ThrowIfNull(schemas);
        Materialise();
        return TenancyCompiler.Compile(this, schemas, Associations);
    }

    /// <summary>
    /// The same, against a catalog's schemas and the associations it carries — the sugar a host that
    /// has already assembled its catalog reaches for.
    /// </summary>
    public TenancyEntitlements Compile(CatalogContext catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        Materialise();

        // The catalog a host registers usually carries this policy's own associations already, and
        // the same claim twice would read as two references between one pair of tables — which the
        // step resolver would call ambiguous. Declared twice is declared once, here as in `Associate`.
        var associations = new List<AssociationDescriptor>(_associations);
        foreach (var association in catalog.Associations)
        {
            var known = false;
            foreach (var seen in associations)
            {
                known |= AssociationDescriptor.Same(seen, association);
            }

            if (!known)
            {
                associations.Add(association);
            }
        }

        return TenancyCompiler.Compile(this, catalog.Schemas, associations);
    }

    // ------------------------------------------------------------------ what the compiler reads

    /// <summary>The tenancy kinds this policy declares, once for the whole policy (D265 §1).</summary>
    internal IReadOnlyList<TenancyKind> Kinds { get; private set; } = [];

    /// <summary>Every role a grant may name. A grant naming another is refused at bind.</summary>
    internal IReadOnlyList<string> Roles { get; private set; } = [];

    /// <summary>Every request scope a grant or a visibility rule may name.</summary>
    internal IReadOnlyList<string> Scopes { get; private set; } = [];

    /// <summary>The tables this policy speaks about, in declaration order.</summary>
    internal IReadOnlyList<TenancyTable> Tables { get; private set; } = [];

    internal TenancyTable? Table(TableDeclaration declaration)
    {
        foreach (var table in Tables)
        {
            if (ReferenceEquals(table.Declaration, declaration))
            {
                return table;
            }
        }

        return null;
    }

    /// <summary>
    /// The declared table of one source, by name. A table is looked up in the source it was declared
    /// against, so two sources may hold a table of one name without either being taken for the other.
    /// </summary>
    internal TenancyTable? TableOf(string schema, string name)
    {
        foreach (var table in Tables)
        {
            if (string.Equals(table.Name, name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(table.Schema, schema, StringComparison.OrdinalIgnoreCase))
            {
                return table;
            }
        }

        return null;
    }

    /// <summary>
    /// Freezes the declarations into the model the compiler reads. A path's steps are copied here,
    /// so nothing the caller still holds can change a declaration the policy has been compiled from.
    /// </summary>
    private void Materialise()
    {
        var kinds = new List<TenancyKind>(_kinds.Count);
        foreach (var declaration in _kinds.Values)
        {
            var within = new List<string>(declaration.WithinKinds.Count);
            foreach (var kind in declaration.WithinKinds)
            {
                within.Add(kind.Name);
            }

            kinds.Add(new TenancyKind
            {
                Name = declaration.Name,
                IsSubject = declaration.IsSubject,
                WithinKinds = within,
            });
        }

        Kinds = kinds;
        Roles = [.. _roles.Values.Select(r => r.Name)];
        Scopes = [.. _scopes.Values.Select(s => s.Name)];

        var tables = new List<TenancyTable>(_tables.Count);
        foreach (var declaration in _tables)
        {
            var restrictions = new List<Restriction>(declaration.Restrictions.Count);
            foreach (var restriction in declaration.Restrictions)
            {
                restrictions.Add(
                    restriction.Kind == RestrictionKind.Direct
                        ? Restriction.Of(Dimension(restriction))
                        : restriction.Steps.Count == 0
                            ? restriction
                            : new Restriction
                            {
                                Kind = restriction.Kind,
                                TenancyKind = restriction.TenancyKind,
                                Steps = [.. restriction.Steps],
                            });
            }

            tables.Add(new TenancyTable(
                declaration,
                restrictions,
                [.. declaration.Visibility],
                [.. declaration.Access],
                declaration.Realms));
        }

        Tables = tables;
    }

    /// <summary>What one <c>Direct</c> declaration means, read off the policy's declared kinds.</summary>
    /// <remarks>
    /// The kind is among them by construction: <c>Direct</c> takes a <see cref="Kind"/> handle,
    /// a handle comes from <c>Tenancy(name)</c> or <c>Subject(name, …)</c> on this policy, and a
    /// handle another policy declared is refused by identity where it is written (D270, ADR 0056
    /// §8). So the lookup cannot miss, and a miss would be a defect in this class rather than a
    /// policy a host could write.
    /// </remarks>
    private GrantDimension Dimension(Restriction restriction)
    {
        var kind = Kinds.First(
            declared => string.Equals(
                declared.Name, restriction.TenancyKind, StringComparison.Ordinal));

        return kind.IsSubject
            ? new GrantDimension
            {
                Kind = kind.Name,
                Column = restriction.Column,
                IsSubject = true,
                WithinKinds = kind.WithinKinds,
            }
            : GrantDimension.Tenancy(kind.Name, restriction.Column);
    }

    // ------------------------------------------------------------------ handle bookkeeping

    private KindDeclaration KindNamed(string name, bool isSubject, IReadOnlyList<Kind> within)
    {
        if (_kinds.TryGetValue(name, out var known))
        {
            if (known.IsSubject != isSubject)
            {
                throw new CatalogValidationException(
                    "tenancy.kinds",
                    $"'{name}' is declared both as a tenancy kind and as a subject kind. A kind is "
                    + "one or the other: a container a row belongs to, or the individual a row is "
                    + "about (docs/design/38-existential-visibility.md §1, D265).");
            }

            return known;
        }

        var declaration = new KindDeclaration
        {
            Policy = this,
            Name = name,
            IsSubject = isSubject,
            WithinKinds = within,
        };
        _kinds[name] = declaration;
        return declaration;
    }

    internal void Declared(TableDeclaration table) => _tables.Add(table);

    /// <summary>
    /// Records one cross-source association and hands it back as the descriptor the catalog carries
    /// (§3, D270 (c)). Declared twice is declared once.
    /// </summary>
    internal AssociationDescriptor Associate(Column from, Column to)
    {
        var here = from.Required();
        var there = to.Required();
        Require(from, here.Table, "tenancy.associations");
        Require(to, there.Table, "tenancy.associations");

        var descriptor = new AssociationDescriptor
        {
            FromSchema = here.Table.Source.Schema.Name,
            FromTable = here.Table.Name,
            FromColumn = here.Name,
            ToSchema = there.Table.Source.Schema.Name,
            ToTable = there.Table.Name,
            ToColumn = there.Name,
        };

        foreach (var known in _associations)
        {
            if (AssociationDescriptor.Same(known, descriptor))
            {
                return known;
            }
        }

        _associations.Add(descriptor);
        return descriptor;
    }

    // ------------------------------------------------------------------ handles of this policy

    internal KindDeclaration Require(Kind kind, string where) =>
        kind.Declaration is { } declaration && ReferenceEquals(declaration.Policy, this)
            ? declaration
            : throw Foreign("tenancy kind", kind.Name, where);

    internal KindDeclaration Require(SubjectKind kind, string where) =>
        kind.Declaration is { } declaration && ReferenceEquals(declaration.Policy, this)
            ? declaration
            : throw Foreign("subject kind", kind.Name, where);

    internal RealmDeclaration Require(Realm realm, string where) =>
        realm.Declaration is { } declaration && ReferenceEquals(declaration.Policy, this)
            ? declaration
            : throw Foreign("realm", realm.Name, where);

    internal ScopeDeclaration Require(Scope scope, string where) =>
        scope.Declaration is { } declaration && ReferenceEquals(declaration.Policy, this)
            ? declaration
            : throw Foreign("scope", scope.Name, where);

    internal RoleDeclaration Require(Role role, string where) =>
        role.Declaration is { } declaration
        && (declaration.Marker != RoleMarker.None || ReferenceEquals(declaration.Policy, this))
            ? declaration
            : throw Foreign("role", role.Name, where);

    /// <summary>
    /// The name of a column that belongs to <paramref name="table"/>. A handle of another table's is
    /// refused here, which is the misuse a string could not be checked for at all (§1, D270).
    /// </summary>
    internal string Require(Column column, TableDeclaration table, string where)
    {
        var declaration = column.Declaration
            ?? throw Foreign("column", column.Name, where);
        if (!ReferenceEquals(declaration.Table, table))
        {
            throw new CatalogValidationException(
                where,
                $"the column handle '{declaration.Table.Name}.{declaration.Name}' is used on "
                + $"'{table.Name}'. A column handle carries the table it was obtained from, so a "
                + "declaration reads a column of its own table and no other: write "
                + $"{table.Name}.Column(\"{declaration.Name}\") "
                + "(docs/design/45-typed-tenancy-surface.md §1, D270).");
        }

        return declaration.Name;
    }

    internal TableDeclaration Require(Table table, string where) =>
        table.Declaration is { } declaration && ReferenceEquals(declaration.Policy, this)
            ? declaration
            : throw Foreign("table", table.Name, where);

    private static CatalogValidationException Foreign(string what, string name, string where) =>
        new(where,
            $"the {what} handle '{name}' was not obtained from this policy. Every name enters once, "
            + "at its declaration on the policy this table belongs to, and every later mention is "
            + "the handle it got back (docs/design/45-typed-tenancy-surface.md §1, D270).");
}

/// <summary>One table's declarations (§5), frozen at compile.</summary>
internal sealed class TenancyTable
{
    internal TenancyTable(
        TableDeclaration declaration,
        IReadOnlyList<Restriction> restrictions,
        IReadOnlyList<VisibilityRule> visibility,
        IReadOnlyList<AccessRule> access,
        IReadOnlyDictionary<string, IReadOnlyList<string>> realms)
    {
        Declaration = declaration;
        Name = declaration.Name;
        Restrictions = restrictions;
        Visibility = visibility;
        Access = access;
        Realms = realms;

        var dimensions = new List<GrantDimension>();
        var owner = "";
        var sees = ResourceOwnerSees.Full;
        foreach (var restriction in restrictions)
        {
            if (restriction is { Kind: RestrictionKind.Dimension, Dimension: { } dimension })
            {
                dimensions.Add(dimension);
            }
            else if (restriction.Kind == RestrictionKind.ResourceOwner)
            {
                owner = restriction.Column;
                sees = restriction.Sees;
            }
        }

        Dimensions = dimensions;
        ResourceOwnerColumn = owner;
        ResourceOwnerSees = sees;
    }

    internal TableDeclaration Declaration { get; }

    internal string Name { get; }

    /// <summary>The name of the schema this table belongs to, which is how a statement qualifies it.</summary>
    internal string Schema => Declaration.Source.Schema.Name;

    /// <summary>
    /// The restrictions this table's rows are visible through, OR-ed (D215). Empty means
    /// unrestricted: every row is visible, and the column rules still apply.
    /// </summary>
    internal IReadOnlyList<Restriction> Restrictions { get; }

    /// <summary>Whether any restriction applies at all.</summary>
    internal bool IsRestricted => Restrictions.Count > 0;

    /// <summary>The dimensions among the restrictions, in declaration order.</summary>
    internal IReadOnlyList<GrantDimension> Dimensions { get; }

    internal IReadOnlyList<VisibilityRule> Visibility { get; }

    internal IReadOnlyList<AccessRule> Access { get; }

    /// <summary>Realm name to the columns that carry it.</summary>
    internal IReadOnlyDictionary<string, IReadOnlyList<string>> Realms { get; }

    /// <summary>The column holding the row's owner, or empty when the table has none (D206, D215).</summary>
    internal string ResourceOwnerColumn { get; }

    /// <summary>What that owner sees of a row they own (D206, D215).</summary>
    internal ResourceOwnerSees ResourceOwnerSees { get; }
}

/// <summary>Declares the restrictions of one restricted table (D215).</summary>
public sealed class TenancyRestrictions
{
    private readonly TableDeclaration _table;
    private readonly List<Restriction> _restrictions = [];

    internal TenancyRestrictions(TableDeclaration table) => _table = table;

    internal TableDeclaration Table => _table;

    /// <summary>
    /// The tenancy key of <paramref name="kind"/> physically exists on this table, in
    /// <paramref name="column"/> — the first of the three ways a table holds a tenancy (D265 §0).
    /// </summary>
    public TenancyRestrictions Direct(Kind kind, Column column)
    {
        _restrictions.Add(Restriction.Direct(Named(kind), Named(column, "direct")));
        return this;
    }

    /// <summary>
    /// The same for a subject kind: the individual the row is about is named by
    /// <paramref name="column"/> (D204, D265 §0).
    /// </summary>
    public TenancyRestrictions Direct(SubjectKind kind, Column column)
    {
        _restrictions.Add(Restriction.Direct(Named(kind), Named(column, "direct")));
        return this;
    }

    /// <summary>
    /// Each row derives its tenancy of <paramref name="kind"/> from an owning, parent relation
    /// (D265 §0): follow the path with <c>.Through(table)</c>, each step down a declared foreign key
    /// or a declared association.
    /// </summary>
    public TenancyPath Inherited(Kind kind) => Path(RestrictionKind.Inherited, Named(kind));

    /// <summary>The same, for a subject kind the parent relation holds (D265 §0).</summary>
    public TenancyPath Inherited(SubjectKind kind) => Path(RestrictionKind.Inherited, Named(kind));

    /// <summary>
    /// A row participates in a tenancy of <paramref name="kind"/> because some related rows belong to
    /// it (D265 §0): the first <c>.Through(table)</c> names the bridge and goes up, every later one
    /// goes down to the endpoint.
    /// </summary>
    public TenancyPath Related(Kind kind) => Path(RestrictionKind.Related, Named(kind));

    /// <summary>The same, for a subject kind the related rows belong to (D265 §0).</summary>
    public TenancyPath Related(SubjectKind kind) => Path(RestrictionKind.Related, Named(kind));

    /// <summary>The host's own row predicate over the bound context, kept verbatim.</summary>
    public TenancyRestrictions Predicate(Sql sql)
    {
        _restrictions.Add(Restriction.Text(sql.Text ?? ""));
        return this;
    }

    /// <summary>
    /// Every row of this table is readable by every principal, while the dimensions declared beside
    /// it still name the kind's column for paths and grants — a catalogue (D272). A supplier's own
    /// products reach the supplier through <c>Direct(supplier, …)</c> and are what a path to the
    /// supplier walks to, and anyone may read the list. Compiles to a <c>TRUE</c> disjunct of the
    /// row predicate — what <c>Predicate(Sql.Of("TRUE"))</c> wrote by hand before this verb existed —
    /// which the reconciler reads as every row. Not <see cref="Table.Unrestricted"/>, which drops the
    /// dimensions with the restriction.
    /// </summary>
    public TenancyRestrictions Public() => Predicate(Sql.Of("TRUE"));

    /// <summary>The structured predicate <c>column IN (@ctx.list)</c>, which reconciles (D215).</summary>
    public TenancyRestrictions In(Column column, string list)
    {
        ArgumentException.ThrowIfNullOrEmpty(list);
        _restrictions.Add(Restriction.In(Named(column, "in"), list));
        return this;
    }

    /// <summary>
    /// The column holding the row's owner: rows a principal owns are visible whatever their tenancy
    /// (D206, D215), and <paramref name="sees"/> says what they see of them.
    /// </summary>
    public TenancyRestrictions ResourceOwner(
        Column column, ResourceOwnerSees sees = ResourceOwnerSees.Full)
    {
        _restrictions.Add(Restriction.ResourceOwner(Named(column, "resourceOwner"), sees));
        return this;
    }

    /// <summary>
    /// A row is visible when the parent row <paramref name="column"/> names is visible to the same
    /// principal (§3.13, D227). The parent and its key come from the declared foreign key.
    /// </summary>
    public TenancyRestrictions Through(Column column)
    {
        _restrictions.Add(Restriction.Through(Named(column, "through")));
        return this;
    }

    /// <summary>The same, where the source declares no foreign key to read the parent off.</summary>
    public TenancyRestrictions Through(Column column, Table parentTable, Column parentKey)
    {
        var parent = _table.Policy.Require(parentTable, $"tenancy.tables[{_table.Name}].through");
        _restrictions.Add(Restriction.Through(
            Named(column, "through"),
            parent.Source.Schema.Name,
            parent.Name,
            _table.Policy.Require(parentKey, parent, $"tenancy.tables[{_table.Name}].through")));
        return this;
    }

    /// <summary>
    /// Records the restriction now, with a steps list the returned builder goes on filling: a path is
    /// declared left to right and its position among the other restrictions is where it began.
    /// </summary>
    private TenancyPath Path(RestrictionKind kind, string tenancyKind)
    {
        var steps = new List<VisibilityStep>();
        _restrictions.Add(new Restriction
        {
            Kind = kind,
            TenancyKind = tenancyKind,
            Steps = steps,
        });
        return new TenancyPath(this, steps);
    }

    private string Named(Kind kind) =>
        _table.Policy.Require(kind, $"tenancy.tables[{_table.Name}]").Name;

    private string Named(SubjectKind kind) =>
        _table.Policy.Require(kind, $"tenancy.tables[{_table.Name}]").Name;

    private string Named(Column column, string verb) =>
        _table.Policy.Require(column, _table, $"tenancy.tables[{_table.Name}].{verb}");

    internal IReadOnlyList<Restriction> Build() => _restrictions;
}

/// <summary>
/// One <c>Inherited</c> or <c>Related</c> path, as it is declared left to right (D265 §1).
/// </summary>
/// <remarks>
/// It carries the restriction builder's other verbs too, so a table declares several perspectives in
/// one chain. The kind-less <c>Through(column)</c> of §3.13 is <em>not</em> among them: on a path
/// <c>Through</c> means "step to this table", and a policy wanting both writes the kind-less one
/// first, which is what the fixture's <c>order_items</c> does.
/// </remarks>
public sealed class TenancyPath
{
    private readonly TenancyRestrictions _restrictions;
    private readonly List<VisibilityStep> _steps;

    internal TenancyPath(TenancyRestrictions restrictions, List<VisibilityStep> steps)
    {
        _restrictions = restrictions;
        _steps = steps;
    }

    /// <summary>
    /// One step of the path, to <paramref name="table"/>. The first step of a <c>Related</c> path
    /// goes up to the bridge; every other step goes down to a parent.
    /// </summary>
    public TenancyPath Through(Table table)
    {
        var declaration = Declared(table);
        _steps.Add(new VisibilityStep
        {
            Schema = declaration.Source.Schema.Name,
            Table = declaration.Name,
        });
        return this;
    }

    /// <summary>
    /// The same, naming the foreign-key column the step joins on — the bridge's for an up-step, ours
    /// for a down-step. Needed only where the pair has more than one foreign key between them, or a
    /// table references itself.
    /// </summary>
    public TenancyPath Through(Table table, Column on)
    {
        var declaration = Declared(table);
        _steps.Add(new VisibilityStep
        {
            Schema = declaration.Source.Schema.Name,
            Table = declaration.Name,
            On = on.Declaration?.Name ?? "",
        });
        return this;
    }

    /// <summary>
    /// Every step of the path, in order — the spelling a host mapping its own configuration reaches
    /// for, where the steps arrive as a sequence rather than as a chain of calls (§2).
    /// </summary>
    public TenancyPath Through(IEnumerable<Table> tables)
    {
        ArgumentNullException.ThrowIfNull(tables);
        foreach (var table in tables)
        {
            Through(table);
        }

        return this;
    }

    /// <summary>Another perspective held directly (D265 §0).</summary>
    public TenancyRestrictions Direct(Kind kind, Column column) => _restrictions.Direct(kind, column);

    /// <summary>Another perspective held directly, for a subject kind (D265 §0).</summary>
    public TenancyRestrictions Direct(SubjectKind kind, Column column) =>
        _restrictions.Direct(kind, column);

    /// <summary>Another perspective inherited down a path (D265 §0).</summary>
    public TenancyPath Inherited(Kind kind) => _restrictions.Inherited(kind);

    /// <summary>The same, for a subject kind (D265 §0).</summary>
    public TenancyPath Inherited(SubjectKind kind) => _restrictions.Inherited(kind);

    /// <summary>Another perspective related through the rows that belong to this one (D265 §0).</summary>
    public TenancyPath Related(Kind kind) => _restrictions.Related(kind);

    /// <summary>The same, for a subject kind (D265 §0).</summary>
    public TenancyPath Related(SubjectKind kind) => _restrictions.Related(kind);

    /// <summary>The host's own row predicate over the bound context, kept verbatim.</summary>
    public TenancyRestrictions Predicate(Sql sql) => _restrictions.Predicate(sql);

    /// <summary>Every row readable by every principal, the dimensions kept (D272).</summary>
    public TenancyRestrictions Public() => _restrictions.Public();

    /// <summary>The structured predicate <c>column IN (@ctx.list)</c>, which reconciles (D215).</summary>
    public TenancyRestrictions In(Column column, string list) => _restrictions.In(column, list);

    /// <summary>The column holding the row's owner (D206, D215).</summary>
    public TenancyRestrictions ResourceOwner(
        Column column, ResourceOwnerSees sees = ResourceOwnerSees.Full) =>
        _restrictions.ResourceOwner(column, sees);

    /// <summary>A row is visible when the parent row the column names is visible (§3.13, D227).</summary>
    public TenancyRestrictions Through(Column column) => _restrictions.Through(column);

    private TableDeclaration Declared(Table table) =>
        _restrictions.Table.Policy.Require(
            table, $"tenancy.tables[{_restrictions.Table.Name}].through");
}

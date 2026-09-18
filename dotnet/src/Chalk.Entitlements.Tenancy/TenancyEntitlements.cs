using Chalk.Catalog;
using Chalk.Client;

namespace Chalk.Entitlements.Tenancy;

/// <summary>
/// A <see cref="TenancyPolicy"/> compiled against one schema: the descriptors to register, the
/// binding of a principal's grants, and the reconciliation of a plan against them
/// (<c>docs/design/16-entitlements.md</c> §5).
/// </summary>
public sealed class TenancyEntitlements
{
    private readonly TenancyPolicy _policy;
    private readonly IReadOnlyList<SchemaDescriptor> _schemas;
    private readonly IReadOnlyDictionary<string, TableEntitlementDescriptor> _descriptors;
    private readonly IReadOnlyDictionary<string, TenancyCompiler.TableModel> _models;

    /// <summary>
    /// The same models under their bare table names, for the names that are unambiguous across the
    /// sources this policy speaks about. It answers a reference that names <em>no</em> schema — a
    /// hand-written plan, or a report from a planner that could not tell two sources apart — and it
    /// holds no name two sources both use.
    /// </summary>
    private readonly IReadOnlyDictionary<string, string> _byName;

    /// <summary>
    /// The bare names two or more sources do share, each with the tables that share it. A reference
    /// that names one of these and no schema is <b>refused by name</b> rather than resolved to
    /// whichever came first (F89).
    /// </summary>
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _shared;
    private readonly IReadOnlyList<TenancyCompiler.BoundList> _lists;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _confinable;
    private readonly bool _needsScope;

    internal TenancyEntitlements(
        TenancyPolicy policy,
        IReadOnlyList<SchemaDescriptor> schemas,
        IReadOnlyDictionary<string, TableEntitlementDescriptor> descriptors,
        IReadOnlyDictionary<string, TenancyCompiler.TableModel> models,
        IReadOnlyList<TenancyCompiler.BoundList> lists,
        IReadOnlyDictionary<string, IReadOnlyList<string>> confinable,
        bool needsScope)
    {
        _policy = policy;
        _schemas = schemas;
        _descriptors = descriptors;
        _models = models;
        _lists = lists;
        _confinable = confinable;
        _needsScope = needsScope;

        var shared = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in models.Keys)
        {
            var bare = key[(key.LastIndexOf('.') + 1)..];
            if (!byName.TryAdd(bare, key))
            {
                if (!shared.TryGetValue(bare, out var keys))
                {
                    keys = [byName[bare]];
                    shared[bare] = keys;
                }

                keys.Add(key);
            }
        }

        foreach (var (bare, keys) in shared)
        {
            byName.Remove(bare);
            keys.Sort(StringComparer.Ordinal);
        }

        _byName = byName;
        _shared = shared.ToDictionary(
            entry => entry.Key,
            entry => (IReadOnlyList<string>)entry.Value,
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The policy these descriptors were compiled from.</summary>
    public TenancyPolicy Policy => _policy;

    /// <summary>
    /// The descriptor per table, by <c>schema.table</c> — the name a statement qualifies it with
    /// (A4). Unrestricted tables have none.
    /// </summary>
    public IReadOnlyDictionary<string, TableEntitlementDescriptor> Tables => _descriptors;

    /// <summary>
    /// The descriptor for one table, or null when the policy leaves it unrestricted. The handle
    /// carries the source it came from, so this is the form that cannot name the wrong table (§1,
    /// D270).
    /// </summary>
    public TableEntitlementDescriptor? For(Table table) =>
        For(table.Source.Name, table.Name);

    /// <summary>The same, for a caller holding the two names rather than the handle.</summary>
    public TableEntitlementDescriptor? For(string source, string table)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(table);
        return _descriptors.TryGetValue(TenancyCompiler.Qualified(source, table), out var descriptor)
            ? descriptor
            : null;
    }

    /// <summary>
    /// The model for a table a plan, a report or a parent named — by the <c>schema.table</c> the
    /// reference carries, and by the bare-name index only where it carries none (F89).
    /// </summary>
    /// <remarks>
    /// A plan's <c>Read</c> and a report's <c>EntitledTable</c> both name the schema the statement
    /// resolved in, whether or not the statement itself qualified the table, so an unqualified
    /// statement over an ambiguous name is read here exactly as a qualified one is. What falls back
    /// to the bare name is a reference that names <em>no</em> schema at all — a hand-written plan,
    /// or a report from a planner that could not tell two sources apart — and a name two sources
    /// share is then refused rather than resolved to whichever came first.
    /// </remarks>
    /// <exception cref="CatalogValidationException">
    /// The reference names no schema and this policy speaks about that table name in more than one
    /// source.
    /// </exception>
    private bool TryModel(string schema, string table, out TenancyCompiler.TableModel model)
    {
        if (Resolve(schema, table) is { } key)
        {
            model = _models[key];
            return true;
        }

        model = null!;
        return false;
    }

    /// <summary>
    /// Which of this policy's tables a reference names, or null where it names none of them — which
    /// is every table the policy leaves unrestricted and every table of another policy's schema.
    /// </summary>
    /// <remarks>
    /// A <em>qualified</em> reference resolves in its own source and nowhere else. Falling back to
    /// the bare name would hand one source's model to another source's read wherever the two share a
    /// table name, and the model decides what the read must disclose — which is the shape ADR 0056
    /// §9 found and fixed in the client's own descriptor lookup and left standing here.
    /// </remarks>
    private string? Resolve(string schema, string table)
    {
        if (schema.Length > 0)
        {
            var qualified = TenancyCompiler.Qualified(schema, table);
            return _models.ContainsKey(qualified) ? qualified : null;
        }

        if (_byName.TryGetValue(table, out var key))
        {
            return key;
        }

        if (_shared.TryGetValue(table, out var shared))
        {
            throw new CatalogValidationException(
                "tenancy.reconcile",
                $"the reference names the table '{table}' and no schema, and this policy speaks "
                + $"about {string.Join(" and ", shared.Select(name => "'" + name + "'"))}. Which of "
                + "them a row of this plan came from decides what it must disclose, so answering "
                + "for one of them would be a guess: reconcile a plan whose reads name their schema "
                + "(docs/design/45-typed-tenancy-surface.md §1, ADR 0056 §7.7).");
        }

        return null;
    }

    /// <summary>The descriptor of the table this model was compiled for.</summary>
    private TableEntitlementDescriptor Descriptor(TenancyCompiler.TableModel model) =>
        _descriptors[TenancyCompiler.Qualified(model.Declared.Schema, model.Declared.Name)];

    /// <summary>The table descriptor of the table this model was compiled for, in its own source.</summary>
    private TableDescriptor? Described(TenancyCompiler.TableModel model)
    {
        foreach (var candidate in _schemas)
        {
            if (!string.Equals(
                    candidate.Name, model.Declared.Schema, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (TenancyCompiler.Find(candidate, model.Declared.Name) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    // ------------------------------------------------------------------ the context

    /// <summary>
    /// Binds a principal's grants as the <see cref="RequestContext"/> the descriptors read (§5).
    /// </summary>
    /// <remarks>
    /// One list per <em>group</em> — a dimension, a role and the ordered set of kinds confining it
    /// (D266 §4) — and the scalars <c>user</c>, <c>mask_key</c>, <c>global</c> and one boolean per
    /// role a global grant confers. A group of no confining kinds is the bare list, one is the pair
    /// list, several is the tuple of arity <c>1 + n</c>. Every list is typed from the schema, so an
    /// empty one still says what it would have held; every predicate is then a membership test or a
    /// scalar, which is what makes the fold of §2 native.
    /// </remarks>
    /// <exception cref="CatalogValidationException">
    /// A grant names a role, a scope or a dimension this policy does not declare, or holds a global
    /// grant the policy does not permit (D205).
    /// </exception>
    public RequestContext Bind(TenancyPrincipal principal) => Bind(principal, fold: null);

    /// <summary>
    /// The same, with only the named dimensions' grants folded into the plan and everything else
    /// left open for execution — <b>partial binding</b> (§2.1, D232).
    /// </summary>
    /// <param name="principal">Whose grants these are.</param>
    /// <param name="fold">
    /// The dimensions, by the kind the policy declares them under (<c>"org"</c>), whose lists become
    /// literals in the plan. Null — the default — folds everything, which is
    /// <see cref="Bind(TenancyPrincipal)"/>.
    /// </param>
    /// <remarks>
    /// <para>
    /// What is folded is in the leaf, in the plan text and in the digest, so two principals share the
    /// plan exactly when their folded half is the same. Naming the tenancy dimension is therefore one
    /// plan per tenant and role set, bound per execution to the principal: the motivating case, as
    /// informed by the user.
    /// </para>
    /// <para>
    /// Everything the named dimensions do not cover stays open, and deliberately so — the subject
    /// lists, the host's own custom lists, and every scalar, <c>user</c> and <c>mask_key</c> among
    /// them. Each of those is what tells one principal of a tenant from another, and folding one
    /// would make the plan that principal's rather than the tenant's.
    /// </para>
    /// </remarks>
    public RequestContext Bind(TenancyPrincipal principal, IReadOnlyCollection<string>? fold)
    {
        var whole = BindAll(principal);
        if (fold is null)
        {
            return whole;
        }

        foreach (var kind in fold)
        {
            if (!_lists.Any(list => string.Equals(list.Kind, kind, StringComparison.Ordinal)))
            {
                throw new CatalogValidationException(
                    "tenancy.grants",
                    $"'{kind}' is not a dimension this policy declares, so there is nothing of it to "
                    + "fold. Name one of "
                    + string.Join(
                        ", ",
                        _lists.Select(l => l.Kind).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
                    + " (docs/design/16-entitlements.md §2.1, D232).");
            }
        }

        var open = new List<string>();
        foreach (var list in _lists)
        {
            if (!fold.Contains(list.Kind, StringComparer.Ordinal))
            {
                open.Add(list.Name);
            }
        }

        open.AddRange(principal.CustomLists.Keys);
        open.AddRange(whole.Scalars.Keys);
        return whole.Shape(open);
    }

    /// <summary>
    /// What <see cref="Bind(TenancyPrincipal)"/> binds, group by group (D266 §5): the list, the
    /// dimension and role whose grants fill it, and <b>the kinds confining it</b>.
    /// </summary>
    /// <remarks>
    /// The planner's own <c>EntitlementsExplanation</c> says what a folded predicate came to for one
    /// table; this says what the principal's grants came to before any of it was folded, which is
    /// the question a host asks when a conjoined grant reaches fewer rows than it expected. A group
    /// with no rows is listed like any other: an empty list is what makes its term false, and seeing
    /// which group is empty is the whole point.
    /// </remarks>
    public IReadOnlyList<TenancyGrantGroup> Explain(TenancyPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        foreach (var grant in principal.Grants)
        {
            Check(grant, principal);
        }

        var groups = new List<TenancyGrantGroup>(_lists.Count);
        foreach (var list in _lists)
        {
            groups.Add(new TenancyGrantGroup
            {
                List = list.Name,
                Kind = list.Kind,
                Role = list.Role,
                IsSubject = list.IsSubject,
                Confining = list.Confining,
                Grants = Rows(list, principal).Rows.Count,
            });
        }

        return groups;
    }

    private RequestContext BindAll(TenancyPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        var globalRoles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var grant in principal.Grants)
        {
            Check(grant, principal);
            if (grant.IsGlobal && Admits(grant, principal))
            {
                globalRoles.Add(grant.Role);
            }
        }

        var scalars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["user"] = principal.User,
            ["mask_key"] = principal.MaskKey,
            ["global"] = globalRoles.Count > 0,
        };
        foreach (var role in _policy.Roles)
        {
            scalars["global_" + role] = globalRoles.Contains(role);
        }

        if (_needsScope)
        {
            scalars["scope"] = principal.ScopeName;
        }

        foreach (var (name, value) in principal.CustomScalars)
        {
            scalars[name] = value;
        }

        var lists = new Dictionary<string, ContextRelation>(StringComparer.Ordinal);
        foreach (var list in _lists)
        {
            lists[list.Name] = Rows(list, principal);
        }

        foreach (var (name, relation) in principal.CustomLists)
        {
            lists[name] = relation;
        }

        return new RequestContext
        {
            Scalars = scalars,
            Lists = lists,
            Purpose = principal.Purpose,
            Actor = principal.Actor,
            FoldMaxRows = principal.FoldMaxRows > 0
                ? principal.FoldMaxRows
                : RequestContext.DefaultFoldMaxRows,
        };
    }

    private ContextRelation Rows(TenancyCompiler.BoundList list, TenancyPrincipal principal)
    {
        var rows = new List<IReadOnlyList<object?>>();
        foreach (var grant in principal.Grants)
        {
            if (grant.IsGlobal
                || !Admits(grant, principal)
                || grant.IsSubject != list.IsSubject
                || !string.Equals(grant.Kind, list.Kind, StringComparison.Ordinal)
                || !string.Equals(grant.Role, list.Role, StringComparison.Ordinal))
            {
                continue;
            }

            // The group is the grant's own: its dimension, its role and the ordered set of kinds
            // confining it. A grant fills one list and never another, which is what makes an
            // unconfined grant and a confined one answer separately (D266 §4).
            var confinements = Confinements(grant);
            if (confinements.Count != list.Confining.Count)
            {
                continue;
            }

            var row = new List<object?>(1 + list.Confining.Count) { grant.Id };
            var matches = true;
            foreach (var kind in list.Confining)
            {
                if (!confinements.TryGetValue(kind, out var id))
                {
                    matches = false;
                    break;
                }

                row.Add(id);
            }

            if (matches)
            {
                rows.Add(row);
            }
        }

        return new ContextRelation
        {
            Columns = list.Columns,
            Rows = rows,
            ColumnTypes = list.Types,
        };
    }

    /// <summary>
    /// A grant's confinements with every kind resolved: the sugar's unnamed one becomes the first
    /// kind the declaration names for that dimension (D266 §1).
    /// </summary>
    private Dictionary<string, object> Confinements(Grant grant)
    {
        var resolved = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var confinement in grant.Confinements)
        {
            resolved[Resolve(grant, confinement)] = confinement.Id;
        }

        return resolved;
    }

    /// <summary>The kind one confinement names, or the first the declaration names for the grant.</summary>
    private string Resolve(Grant grant, Confinement confinement)
    {
        if (confinement.KindName.Length > 0)
        {
            return confinement.KindName;
        }

        var permitted = Permitted(grant.Kind);
        return permitted.Count > 0 ? permitted[0] : "";
    }

    /// <summary>The tenancy kinds this policy declares may confine one kind (D266 §2).</summary>
    private IReadOnlyList<string> Permitted(string kind) =>
        _confinable.TryGetValue(kind, out var kinds) ? kinds : [];

    /// <summary>Whether this grant applies in the principal's request scope.</summary>
    private static bool Admits(Grant grant, TenancyPrincipal principal) =>
        grant.ScopeName.Length == 0
        || string.Equals(grant.ScopeName, principal.ScopeName, StringComparison.Ordinal);

    private void Check(Grant grant, TenancyPrincipal principal)
    {
        if (!_policy.Roles.Contains(grant.Role, StringComparer.Ordinal))
        {
            throw new CatalogValidationException(
                "tenancy.grants",
                $"the grant names role '{grant.Role}', which this policy does not declare "
                + $"({string.Join(", ", _policy.Roles)})");
        }

        if (grant.ScopeName.Length > 0 && !_policy.Scopes.Contains(grant.ScopeName, StringComparer.Ordinal))
        {
            throw new CatalogValidationException(
                "tenancy.grants",
                $"the grant names scope '{grant.ScopeName}', which this policy does not declare");
        }

        if (grant.IsGlobal)
        {
            if (!_policy.AllowsGlobalGrants)
            {
                throw new CatalogValidationException(
                    "tenancy.grants",
                    "this principal holds a global grant and the policy does not permit them "
                    + "(AllowGlobalGrants is off, which is the default): a tier that may see every "
                    + "tenancy is a decision a deployment makes once, not one a request makes "
                    + "(docs/design/16-entitlements.md §5, D205).");
            }

            return;
        }

        if (!_lists.Any(list =>
                string.Equals(list.Kind, grant.Kind, StringComparison.Ordinal)
                && list.IsSubject == grant.IsSubject))
        {
            throw new CatalogValidationException(
                "tenancy.grants",
                $"the grant names {(grant.IsSubject ? "subject" : "tenancy")} dimension "
                + $"'{grant.Kind}', which no table of this policy declares");
        }

        // A subject grant reaching every tenancy is the widest thing a grant can do, and it is said
        // rather than implied by omission (§5, design 40 §1 as amended 2026-09-16). D266's run had
        // let `ForSubject(kind, id, role)` mean "anywhere", which turned a forgotten `within:` into
        // the widest grant in the vocabulary; the word is required again. `Within(kind, id)` after
        // the call is the confined form and satisfies this too.
        if (grant.IsSubject && grant.Confinements.Count == 0 && !grant.Unconfined)
        {
            throw new CatalogValidationException(
                "tenancy.grants",
                $"a subject grant must name the tenancy it is confined to, or Tenancy.Anywhere: the "
                + $"grant on '{grant.Kind}' names neither. Write "
                + $"Grant.ForSubject({grant.Kind}, …, within: <tenancy>) or .Within(kind, id) "
                + "for the confined grant, and "
                + $"Grant.ForSubject({grant.Kind}, …, within: Tenancy.Anywhere) for one that "
                + "reaches every tenancy (docs/design/16-entitlements.md §5, "
                + "docs/design/40-conjoined-confinement.md §1, D266).");
        }

        // Which kinds may confine which is the policy's own declaration (D266 §2): a subject kind
        // names them with `Subject(kind, within: …)`, and a tenancy kind is confinable by any other
        // declared tenancy kind. A grant naming one outside that list could never match a group, so
        // it would silently grant more than the host meant rather than less.
        var permitted = Permitted(grant.Kind);
        var named = new List<string>(grant.Confinements.Count);
        foreach (var confinement in grant.Confinements)
        {
            var kind = Resolve(grant, confinement);
            if (kind.Length == 0)
            {
                throw new CatalogValidationException(
                    "tenancy.grants",
                    $"the grant on '{grant.Kind}' is confined to a tenancy and this policy declares "
                    + $"no kind that may confine '{grant.Kind}'. Declare one — "
                    + $"Subject(\"{grant.Kind}\", within: [<kind>]) for a subject, a second tenancy "
                    + "kind for a container — or hold the grant unconfined "
                    + "(docs/design/40-conjoined-confinement.md §2, D266).");
            }

            if (!permitted.Contains(kind, StringComparer.Ordinal))
            {
                throw new CatalogValidationException(
                    "tenancy.grants",
                    $"the grant on '{grant.Kind}' is confined along '{kind}', which this policy does "
                    + $"not declare as a kind that may confine '{grant.Kind}'. It declares "
                    + $"{TenancyCompiler.Named(permitted)} (docs/design/40-conjoined-confinement.md "
                    + "§2, D266).");
            }

            if (named.Contains(kind, StringComparer.Ordinal))
            {
                throw new CatalogValidationException(
                    "tenancy.grants",
                    $"the grant on '{grant.Kind}' names the confining kind '{kind}' twice — once "
                    + "through Within and once through the within: sugar, which means the first "
                    + $"kind the declaration names. All the confinements of one grant must hold at "
                    + "once, so two of one kind would reach no row at all "
                    + "(docs/design/40-conjoined-confinement.md §1, D266).");
            }

            named.Add(kind);
        }

        // The role has to be one some list of this dimension carries. The first clause of this
        // method asks whether the *policy* declares the role at all; a role is declared once and
        // admitted per table, so a role declared for one dimension and named on another is a role
        // `Rows` matches against no list — the grant binds, reaches nothing and says nothing, which
        // is F80's silence reached by the other member of the same key. The owner's decision of
        // 2026-09-17: refuse, and fast, where the result is known to be terminal.
        if (!_lists.Any(list => Carries(grant, list)))
        {
            throw new CatalogValidationException(
                "tenancy.grants",
                $"the grant on {(grant.IsSubject ? "subject" : "tenancy")} dimension "
                + $"'{grant.Kind}' names role '{grant.Role}', and no table that resolves "
                + $"'{grant.Kind}' admits that role — so there is no membership the grant could fill "
                + "and it would reach nothing. The roles this policy holds a membership of "
                + $"'{grant.Kind}' for are {AnswerableRoles(grant)}. Name one of those, or admit "
                + $"'{grant.Role}' on a table that resolves '{grant.Kind}' "
                + "(docs/design/40-conjoined-confinement.md §3, D266, F99).");
        }

        // And the group the grant would fill has to exist. The clause above asks what the *policy*
        // permits to confine this kind; this asks what a *table* resolves, which is the other half
        // of design 40 §3: a confined grant applies to a table where its own kind and every
        // confining kind resolve on that table, so where no table resolves them together the
        // compiler emitted no list for the group and `Rows` fills none — the grant binds, reaches
        // nothing and says nothing. Fail-closed, but silent, which is what F80 measured.
        if (named.Count > 0 && !_lists.Any(list => Fills(grant, list, named)))
        {
            var kinds = TenancyCompiler.Named(named);
            throw new CatalogValidationException(
                "tenancy.grants",
                $"the grant on '{grant.Kind}' is confined along {kinds}, and no table of this policy "
                + $"resolves '{grant.Kind}' and {kinds} on one row — so there is no membership the "
                + "conjunction could be answered against and the grant would reach nothing. What "
                + $"'{grant.Kind}' can be confined along here for role '{grant.Role}' is: "
                + $"{AnswerableConfinements(grant)}. "
                + AlongAPath(grant, named)
                + "A confined grant applies to a table where its own kind and every confining kind "
                + "resolve on that table: hold the grant unconfined, or declare the dimension on a "
                + "table that has both (docs/design/40-conjoined-confinement.md §3, D266).");
        }

        _ = principal;
    }

    /// <summary>
    /// Whether this list is <em>a</em> list of the grant's dimension: its kind, its subject-ness and
    /// its role, which is the part of <see cref="Rows"/>'s key the confinement does not carry.
    /// </summary>
    private static bool Carries(Grant grant, TenancyCompiler.BoundList list) =>
        string.Equals(list.Kind, grant.Kind, StringComparison.Ordinal)
        && list.IsSubject == grant.IsSubject
        && string.Equals(list.Role, grant.Role, StringComparison.Ordinal);

    /// <summary>
    /// Whether this list is <em>the</em> group the grant fills: <see cref="Carries"/> and the
    /// ordered set of kinds confining it — together, exactly what <see cref="Rows"/> matches on.
    /// </summary>
    private static bool Fills(Grant grant, TenancyCompiler.BoundList list, IReadOnlyList<string> named) =>
        Carries(grant, list)
        && list.Confining.Count == named.Count
        && named.All(kind => list.Confining.Contains(kind, StringComparer.Ordinal));

    /// <summary>
    /// The near miss, named, or an empty string when there is none (<b>F101</b>). A table that holds
    /// one of the conjunction's kinds on its own row and reaches another along an <c>Inherited</c>
    /// path looks, to a host reading its declaration, exactly like a table that resolves both — and
    /// the generic refusal above, "no table of this policy resolves them on one row", reads as
    /// though that table did not exist.
    /// </summary>
    /// <remarks>
    /// It does exist, and what it does not have is a <em>row</em> carrying both. A conjoined term
    /// along a path is written over the <b>endpoint's</b> own row, because that is the row the
    /// endpoint predicate is evaluated on (design 40 §3, ADR 0049 §4) — so the confining kind has to
    /// be a dimension of the endpoint, and a kind the target holds directly is on the wrong row for
    /// it. Saying which table, which path and which endpoint turns the refusal from a denial into
    /// the two things a host can actually do about it.
    /// </remarks>
    private string AlongAPath(Grant grant, IReadOnlyList<string> named)
    {
        var conjunction = new List<string>(1 + named.Count) { grant.Kind };
        conjunction.AddRange(named);

        foreach (var key in _models.Keys.Order(StringComparer.Ordinal))
        {
            var model = _models[key];
            foreach (var path in model.Paths)
            {
                if (path.IsRelated || !conjunction.Contains(path.Kind, StringComparer.Ordinal))
                {
                    continue;
                }

                // The other half of the conjunction has to be on this table's own row for this to be
                // the near miss rather than a table with nothing to do with the grant.
                var direct = model.Dimensions
                    .Where(d => conjunction.Contains(d.Declared.Kind, StringComparer.Ordinal)
                        && !string.Equals(d.Declared.Kind, path.Kind, StringComparison.Ordinal))
                    .Select(d => d.Declared.Kind)
                    .ToList();
                if (direct.Count == 0)
                {
                    continue;
                }

                var carries = path.EndpointDimensions
                    .Where(d => !d.Declared.IsSubject)
                    .Select(d => d.Declared.Kind)
                    .ToList();

                return $"'{model.Declared.Name}' comes closest and is not close enough: it holds "
                    + $"{TenancyCompiler.Named(direct)} on its own row and reaches "
                    + $"'{path.Kind}' along an inherited path whose endpoint is "
                    + $"'{path.EndpointTable}'. A conjoined term along a path is written over the "
                    + "endpoint's own row, because that is the row the endpoint predicate is "
                    + $"evaluated on, and '{path.EndpointTable}' resolves "
                    + $"{TenancyCompiler.Named(carries)}. Either the confining kind resolves on "
                    + $"the endpoint's row, or '{path.Kind}' is declared directly on "
                    + $"'{model.Declared.Name}' (F101). ";
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// The confinements this policy can answer for the grant's dimension <em>in its role</em>, as a
    /// message names them: the <c>Confining</c> sets of the lists it holds for it, each set in the
    /// order the declaration names its kinds, or <c>nothing</c> where it holds only the bare list.
    /// </summary>
    private string AnswerableConfinements(Grant grant)
    {
        var sets = new List<string>();
        foreach (var list in _lists)
        {
            if (!Carries(grant, list) || list.Confining.Count == 0)
            {
                continue;
            }

            var named = TenancyCompiler.Named(list.Confining);
            if (!sets.Contains(named, StringComparer.Ordinal))
            {
                sets.Add(named);
            }
        }

        return sets.Count == 0 ? "nothing" : string.Join("; ", sets);
    }

    /// <summary>
    /// The roles this policy holds a membership of the grant's dimension for, as a message names
    /// them: the roles of the lists it emitted for that kind, in declaration order.
    /// </summary>
    private string AnswerableRoles(Grant grant)
    {
        var roles = new List<string>();
        foreach (var role in _policy.Roles)
        {
            if (_lists.Any(list =>
                    string.Equals(list.Kind, grant.Kind, StringComparison.Ordinal)
                    && list.IsSubject == grant.IsSubject
                    && string.Equals(list.Role, role, StringComparison.Ordinal)))
            {
                roles.Add(role);
            }
        }

        return TenancyCompiler.Named(roles);
    }

    // ------------------------------------------------------------------ reconciliation

    /// <summary>
    /// What the policy says every entitled read of a prepared plan must disclose, compared with what
    /// the planner said it did (§5, D201).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the misclassification half of the model. The planner's own taint check proves that no
    /// raw value <em>escapes</em>; it cannot prove that the pass classified a column the way the
    /// policy meant, because deriving the classification independently would be the pass written
    /// twice. Here it is written once more, from the other end — the grants and the declarations,
    /// with no plan involved — and the two answers are compared.
    /// </para>
    /// <para>
    /// The rule is the one the planner folds: one tenancy in scope gives a constant outcome, several
    /// that disagree give <c>PerRow</c>, a reachable population-only level dominates, and a table
    /// visible only through the created-by fail-safe gives what <c>CreatorSees</c> says.
    /// </para>
    /// </remarks>
    /// <returns>
    /// The per-column disagreements, and beside them a three-valued verdict per entitled table on
    /// how much of it the planner said this principal can see (D215).
    /// </returns>
    public TenancyReconciliation Reconcile(EntitledQuery prepared, TenancyPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(principal);

        var visibility = new List<TenancyVisibility>();
        foreach (var table in prepared.Entitlements.Tables)
        {
            if (!TryModel(table.Schema, table.Table, out var model))
            {
                continue;
            }

            var predicted = Predict(model, principal);
            visibility.Add(new TenancyVisibility
            {
                Table = table.Table,
                Predicted = predicted,
                Reported = table.Visibility,
                Verdict = predicted is null
                    ? ReconciledVisibility.Indeterminate
                    : predicted == table.Visibility
                        ? ReconciledVisibility.Agrees
                        : ReconciledVisibility.Disagrees,
            });
        }

        return new TenancyReconciliation
        {
            Differences = Reconcile(prepared.Plan, principal),
            Visibility = visibility,
        };
    }

    /// <summary>
    /// How much of this table the policy says this principal can see, or null where the package
    /// cannot say (D215).
    /// </summary>
    /// <remarks>
    /// Determinate for a dimension, for the resource-owner fail-safe and for a global grant, because
    /// each of those is a term the package wrote itself. For a host predicate it is determinate only
    /// in the recognised shapes — the structured <see cref="Restriction.In"/>, or free text that is a
    /// conjunction or a disjunction of <c>&lt;column&gt; IN (@ctx.&lt;list&gt;)</c> terms,
    /// <c>@ctx.&lt;bool&gt;</c> scalars and the literals <c>TRUE</c> and <c>FALSE</c> (D272).
    /// Anything else is <em>Indeterminate</em> and is reported as
    /// such rather than counted either way: a guess that happened to agree would be worse than no
    /// answer. The planner's own <c>visibility</c> stays determinate in every case, because it comes
    /// from the fold.
    /// </remarks>
    private TableVisibility? Predict(TenancyCompiler.TableModel model, TenancyPrincipal principal)
    {
        var declared = model.Declared;
        if (!declared.IsRestricted)
        {
            return TableVisibility.All;
        }

        // `@ctx.global` is OR-ed onto the predicate of a table with a grant-driven restriction, so
        // one global grant folds the whole predicate to TRUE. A table restricted only by host
        // predicates carries no such term — the host's text is its own answer — and a global grant
        // says nothing about it.
        // A `Through` is not a grant-driven route of *this* table: the escape sits on the parent's
        // own predicate, and whether a global grant reaches every child row depends on the parent
        // and on whether every child row has a parent at all (§3.13, D226, D230).
        var grantDriven = false;
        foreach (var restriction in declared.Restrictions)
        {
            grantDriven |=
                restriction.Kind != RestrictionKind.Predicate
                && restriction.Kind != RestrictionKind.Through
                && restriction.Kind != RestrictionKind.Inherited
                && restriction.Kind != RestrictionKind.Related;
        }

        if (grantDriven)
        {
            foreach (var grant in principal.Grants)
            {
                if (grant.IsGlobal && Admits(grant, principal))
                {
                    return TableVisibility.All;
                }
            }
        }

        var any = false;
        var all = false;
        var dimensionsDone = false;
        var throughDone = false;
        var pathsDone = false;
        foreach (var restriction in declared.Restrictions)
        {
            TableVisibility? term;
            switch (restriction.Kind)
            {
                case RestrictionKind.Dimension:
                    if (dimensionsDone)
                    {
                        continue;
                    }

                    dimensionsDone = true;
                    term = Dimensions(model, principal);
                    break;

                case RestrictionKind.Predicate:
                    term = Predicate(restriction, principal);
                    break;

                case RestrictionKind.Through:
                    if (throughDone)
                    {
                        continue;
                    }

                    throughDone = true;
                    term = Through(model, principal);
                    break;

                case RestrictionKind.Inherited:
                case RestrictionKind.Related:
                    if (pathsDone)
                    {
                        continue;
                    }

                    pathsDone = true;
                    term = Paths(model, principal);
                    break;

                default:
                    // A row the principal owns may exist, and no fold can rule it out.
                    term = TableVisibility.Some;
                    break;
            }

            if (term is null)
            {
                return null;
            }

            any |= term != TableVisibility.None;
            all |= term == TableVisibility.All;
        }

        return all ? TableVisibility.All : any ? TableVisibility.Some : TableVisibility.None;
    }

    /// <summary>
    /// How much of a child's rows its parents grant (§3.13, D230): NONE where every parent's own
    /// predicate folds to FALSE, ALL where one folds to TRUE over a key every child row carries —
    /// which is the join the planner elides — and SOME otherwise.
    /// </summary>
    /// <remarks>
    /// Determinate whenever the parents' own visibility is, which the compiler's refusal of a cycle
    /// makes a finite question: a <c>Through</c> never makes a table <em>Indeterminate</em> by
    /// itself.
    /// </remarks>
    private TableVisibility? Through(TenancyCompiler.TableModel model, TenancyPrincipal principal)
    {
        var any = false;
        var all = false;
        foreach (var parent in model.Through)
        {
            if (!TryModel(parent.ParentSchema, parent.ParentTable, out var parentModel))
            {
                return null;
            }

            var predicted = Predict(parentModel, principal);
            if (predicted is null)
            {
                return null;
            }

            var term = predicted switch
            {
                TableVisibility.None => TableVisibility.None,
                TableVisibility.All when parent.Total => TableVisibility.All,
                _ => TableVisibility.Some,
            };

            any |= term != TableVisibility.None;
            all |= term == TableVisibility.All;
        }

        return all ? TableVisibility.All : any ? TableVisibility.Some : TableVisibility.None;
    }

    /// <summary>
    /// How much of a table's rows its declared paths grant (D265 §6): NONE where no grant of the
    /// kind reaches the endpoint, ALL where an <c>Inherited</c> chain of total keys is reached by a
    /// grant that folds its endpoint predicate to TRUE, and SOME otherwise — never ALL for a
    /// <c>Related</c> path, because existence is per row.
    /// </summary>
    /// <remarks>
    /// The verdicts for the path's perspective are read off the <b>endpoint's</b> dimension of that
    /// kind, as a <c>Through</c> child's are read off its parent's — which is what makes this a
    /// finite question: a path consults no other table's entitlement, so there is nothing to recurse
    /// into and it never makes a table <em>Indeterminate</em> by itself.
    /// </remarks>
    private TableVisibility Paths(TenancyCompiler.TableModel model, TenancyPrincipal principal)
    {
        var any = false;
        var all = false;
        foreach (var declaredPath in model.Paths)
        {
            var reaches = false;
            var everywhere = false;
            foreach (var grant in principal.Grants)
            {
                if (!Admits(grant, principal)
                    || !model.AdmittedRoles.Contains(grant.Role, StringComparer.Ordinal))
                {
                    continue;
                }

                if (grant.IsGlobal)
                {
                    reaches = true;
                    everywhere = true;
                    continue;
                }

                reaches |= Reaches(
                    grant, declaredPath.EndpointDimension, declaredPath.EndpointDimensions);
            }

            var term = !reaches
                ? TableVisibility.None
                : everywhere && declaredPath.Total
                    ? TableVisibility.All
                    : TableVisibility.Some;

            any |= term != TableVisibility.None;
            all |= term == TableVisibility.All;
        }

        return all ? TableVisibility.All : any ? TableVisibility.Some : TableVisibility.None;
    }

    /// <summary>Whether this principal holds a grant that reaches this table's dimensions.</summary>
    private TableVisibility Dimensions(
        TenancyCompiler.TableModel model, TenancyPrincipal principal)
    {
        if (model.Scopes.Count > 0
            && !model.Scopes.Contains(principal.ScopeName, StringComparer.Ordinal))
        {
            return TableVisibility.None;
        }

        foreach (var grant in principal.Grants)
        {
            if (grant.IsGlobal
                || !Admits(grant, principal)
                || !model.AdmittedRoles.Contains(grant.Role, StringComparer.Ordinal))
            {
                continue;
            }

            foreach (var dimension in model.Dimensions)
            {
                if (Reaches(grant, dimension, model.Dimensions))
                {
                    return TableVisibility.Some;
                }
            }
        }

        return TableVisibility.None;
    }

    /// <summary>
    /// Whether one grant reaches one dimension on the row <paramref name="siblings"/> describes
    /// (D266 §3): its own kind resolves there, and so does <b>every kind confining it</b> — where one
    /// does not, the compiler wrote no term for the group and the grant reaches nothing of that
    /// table at all.
    /// </summary>
    private bool Reaches(
        Grant grant,
        TenancyCompiler.ResolvedDimension dimension,
        IReadOnlyList<TenancyCompiler.ResolvedDimension> siblings)
    {
        if (!string.Equals(dimension.Declared.Kind, grant.Kind, StringComparison.Ordinal)
            || dimension.Declared.IsSubject != grant.IsSubject)
        {
            return false;
        }

        foreach (var kind in Confinements(grant).Keys)
        {
            var resolves = false;
            foreach (var sibling in siblings)
            {
                resolves |= !sibling.Declared.IsSubject
                    && string.Equals(sibling.Declared.Kind, kind, StringComparison.Ordinal);
            }

            if (!resolves)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A host predicate, in the shapes the package can read (D215); null otherwise.</summary>
    private static TableVisibility? Predicate(Restriction restriction, TenancyPrincipal principal)
    {
        if (restriction.List.Length > 0)
        {
            return Membership(restriction.List, principal);
        }

        var text = restriction.Predicate.Trim();
        var disjunction = text.Contains(" OR ", StringComparison.Ordinal);
        var conjunction = text.Contains(" AND ", StringComparison.Ordinal);
        if (disjunction && conjunction)
        {
            // A mixed expression needs precedence and parentheses, which is a parser; the grammar
            // stops here on purpose.
            return null;
        }

        var terms = text.Split(
            disjunction ? " OR " : " AND ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var known = new List<TableVisibility>(terms.Length);
        foreach (var term in terms)
        {
            var one = Term(term, principal);
            if (one is null)
            {
                return null;
            }

            known.Add(one.Value);
        }

        if (known.Count == 0)
        {
            return null;
        }

        if (disjunction)
        {
            return known.Contains(TableVisibility.All)
                ? TableVisibility.All
                : known.TrueForAll(v => v == TableVisibility.None)
                    ? TableVisibility.None
                    : TableVisibility.Some;
        }

        return known.Contains(TableVisibility.None)
            ? TableVisibility.None
            : known.TrueForAll(v => v == TableVisibility.All)
                ? TableVisibility.All
                : TableVisibility.Some;
    }

    /// <summary>
    /// One term of the grammar: a membership test, a bound boolean, or the literal <c>TRUE</c> or
    /// <c>FALSE</c> — the first being what <c>Public()</c> compiles to (D272).
    /// </summary>
    private static TableVisibility? Term(string text, TenancyPrincipal principal)
    {
        var trimmed = Unwrap(text.Trim());
        if (trimmed.Equals("TRUE", StringComparison.OrdinalIgnoreCase))
        {
            return TableVisibility.All;
        }

        if (trimmed.Equals("FALSE", StringComparison.OrdinalIgnoreCase))
        {
            return TableVisibility.None;
        }

        if (trimmed.StartsWith("@ctx.", StringComparison.Ordinal))
        {
            var name = trimmed["@ctx.".Length..];
            return principal.CustomScalars.TryGetValue(name, out var value) && value is bool flag
                ? flag ? TableVisibility.All : TableVisibility.None
                : null;
        }

        var inAt = trimmed.IndexOf(" IN (@ctx.", StringComparison.Ordinal);
        if (inAt < 0 || !trimmed.EndsWith(')'))
        {
            return null;
        }

        var list = trimmed[(inAt + " IN (@ctx.".Length)..^1];
        return list.Length == 0 || list.Contains(' ', StringComparison.Ordinal)
            ? null
            : Membership(list, principal);
    }

    /// <summary>One term with its own wrapping parentheses removed, however many there are.</summary>
    private static string Unwrap(string text)
    {
        while (text.Length > 1 && text[0] == '(' && text[^1] == ')')
        {
            var depth = 0;
            var wraps = true;
            for (var i = 0; i < text.Length && wraps; i++)
            {
                depth += text[i] == '(' ? 1 : text[i] == ')' ? -1 : 0;
                wraps = depth > 0 || i == text.Length - 1;
            }

            if (!wraps)
            {
                return text;
            }

            text = text[1..^1].Trim();
        }

        return text;
    }

    /// <summary>What a membership test against one bound list can still yield.</summary>
    private static TableVisibility? Membership(string list, TenancyPrincipal principal) =>
        principal.CustomLists.TryGetValue(list, out var relation)
            ? relation.Rows.Count == 0 ? TableVisibility.None : TableVisibility.Some
            : null;

    /// <summary>The same, over a plan the caller already holds.</summary>
    public IReadOnlyList<TenancyDifference> Reconcile(Chalk.Ir.Plan plan, TenancyPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(principal);

        var differences = new List<TenancyDifference>();
        foreach (var read in Reads(plan))
        {
            if (read.Disclosures.Count == 0)
            {
                continue;
            }

            var name = read.Table.Table;
            if (!TryModel(read.Table.Schema, name, out var model))
            {
                continue;
            }

            if (Described(model) is not { } table)
            {
                continue;
            }

            var expected = Expected(table, model, principal);
            foreach (var disclosure in read.Disclosures)
            {
                var ordinal = (int)disclosure.Column;
                if (ordinal < 0 || ordinal >= expected.Count)
                {
                    continue;
                }

                if (expected[ordinal] != disclosure.Outcome)
                {
                    differences.Add(new TenancyDifference
                    {
                        Table = name,
                        Column = table.Columns[ordinal].Name,
                        Expected = expected[ordinal],
                        Reported = disclosure.Outcome,
                    });
                }
            }
        }

        return differences;
    }

    /// <summary>
    /// Every read in the plan, <b>the pushed subtrees included</b>: a table read only under a
    /// remote query is read by this statement, so what the policy says it must disclose is
    /// reconciled like any other's. This is the inclusive walk (ADR 0062 §6, F92).
    /// </summary>
    private static IEnumerable<Chalk.Ir.Read> Reads(Chalk.Ir.Plan plan) =>
        Chalk.Ir.PlanWalker.Rels(plan)
            .Where(rel => rel.KindCase == Chalk.Ir.Rel.KindOneofCase.Read)
            .Select(rel => rel.Read);

    /// <summary>What each column of this table must disclose for this principal, in ordinal order.</summary>
    private IReadOnlyList<Chalk.Ir.DisclosureOutcome> Expected(
        TableDescriptor table,
        TenancyCompiler.TableModel model,
        TenancyPrincipal principal)
    {
        var (classes, globalRoles) = RowClasses(model, principal);
        var routes = Routes(model, principal);
        var descriptor = Descriptor(model);
        var outcomes = new List<Chalk.Ir.DisclosureOutcome>(table.Columns.Count);
        for (var i = 0; i < table.Columns.Count; i++)
        {
            // A column in no realm and named by no rule carries no entitlement at all, so nothing is
            // ever consulted for it and it is disclosed under the table's default. That is what keeps
            // a policy about the columns that matter.
            outcomes.Add(
                descriptor.FindColumn(i) is null
                    ? descriptor.DefaultDisclosure == Disclosure.None
                        ? Chalk.Ir.DisclosureOutcome.Redacted
                        : Chalk.Ir.DisclosureOutcome.Full
                    : Outcome(model, table.Columns[i].Name, classes, globalRoles, routes));
        }

        return outcomes;
    }

    /// <summary>
    /// How the <b>routes</b> of a table entitled along declared paths reach this principal (D269 (a),
    /// <c>docs/design/38-existential-visibility.md</c> §4): how many of them reach it at all, and
    /// whether any of them reaches rows of more than one tenancy.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A route is one declared path. It reaches this principal when a grant they hold, in a role the
    /// table admits, resolves at the path's <em>endpoint</em> dimension — which is exactly the
    /// endpoint predicate the pass folds, and a route whose predicate folds to FALSE is dropped with
    /// its chain and takes no part in the meet.
    /// </para>
    /// <para>
    /// The tenancies are counted because the ordinal a route projects is decided on the endpoint's
    /// own row: one tenancy is one answer for every row that route reached, and several are several.
    /// A global grant is not a route and is not counted — it makes every endpoint predicate true and
    /// is answered by <see cref="Everywhere"/> instead.
    /// </para>
    /// </remarks>
    private ReachedRoutes Routes(TenancyCompiler.TableModel model, TenancyPrincipal principal)
    {
        var count = 0;
        var severalTenancies = false;
        foreach (var declaredPath in model.Paths)
        {
            var tenancies = new HashSet<string>(StringComparer.Ordinal);
            foreach (var grant in principal.Grants)
            {
                if (grant.IsGlobal
                    || !Admits(grant, principal)
                    || !model.AdmittedRoles.Contains(grant.Role, StringComparer.Ordinal))
                {
                    continue;
                }

                if (Reaches(grant, declaredPath.EndpointDimension, declaredPath.EndpointDimensions))
                {
                    tenancies.Add(Tenancy(grant));
                }
            }

            if (tenancies.Count == 0)
            {
                continue;
            }

            count++;
            severalTenancies |= tenancies.Count > 1;
        }

        return new ReachedRoutes(count, severalTenancies);
    }

    /// <summary>What <see cref="Routes"/> found, and the one question the prediction asks of it.</summary>
    private readonly record struct ReachedRoutes(int Count, bool SeveralTenancies)
    {
        /// <summary>
        /// Whether every row the target keeps carries one and the same verdict ordinal — so the
        /// column's verdict is a constant rather than a meet read per row.
        /// </summary>
        /// <remarks>
        /// True in exactly two cases. No route reaches, and no row is kept at all: the row predicate
        /// folds to FALSE and every column takes the table's <c>otherwise</c>. Or one route reaches
        /// and reaches one tenancy, so every row it reached matched the same rule at the endpoint
        /// and the ordinal is one value the pass folds away. Anywhere else — a second route, or a
        /// second tenancy along one — the ordinal arrives on the target as a column and neither the
        /// pass nor this package can say which value a given row carries.
        /// </remarks>
        internal bool OneOrdinal => Count <= 1 && !SeveralTenancies;
    }

    /// <summary>
    /// One tenancy this grant reaches, as the row classes and the routes both key it: the dimension
    /// and identifier it names, and every kind confining it, because a confined grant is a narrower
    /// tenancy than the same grant unconfined (D266).
    /// </summary>
    private string Tenancy(Grant grant)
    {
        var key = grant.Kind + "/" + grant.Id;
        foreach (var (kind, id) in Confinements(grant).OrderBy(c => c.Key, StringComparer.Ordinal))
        {
            key += "/" + kind + "=" + id;
        }

        return key;
    }

    /// <summary>
    /// The kinds of row this principal can see of this table, each as the set of roles they hold over
    /// it. A tenancy the grants name is one; a global grant is one that holds everywhere; the
    /// resource-owner fail-safe is one more, because a row the principal owns is visible whatever
    /// its tenancy and the planner cannot know whether any exists.
    /// </summary>
    private (List<RowClass> Classes, HashSet<string> GlobalRoles) RowClasses(
        TenancyCompiler.TableModel model, TenancyPrincipal principal)
    {
        var byTenancy = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        // Which of those tenancies the table's own row predicate names, as against one reached only
        // through a parent or along a declared path (D269 (c)).
        var direct = new HashSet<string>(StringComparer.Ordinal);
        var globalRoles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var grant in principal.Grants)
        {
            if (!Admits(grant, principal)
                || !model.AdmittedRoles.Contains(grant.Role, StringComparer.Ordinal))
            {
                continue;
            }

            if (grant.IsGlobal)
            {
                globalRoles.Add(grant.Role);
                continue;
            }

            var declares = false;
            var reachesADimensionOfItsOwn = false;
            foreach (var dimension in model.Dimensions)
            {
                reachesADimensionOfItsOwn |= Reaches(grant, dimension, model.Dimensions);
            }

            declares |= reachesADimensionOfItsOwn;

            // A table entitled through a parent has no dimension of its own: the roles held in the
            // row's tenancy are the roles held in the parent's, which is exactly what the compiler
            // wrote its rule conditions over (§3.13, D228).
            foreach (var parent in model.Through)
            {
                foreach (var dimension in parent.ParentDimensions)
                {
                    declares |= Reaches(grant, dimension, parent.ParentDimensions);
                }
            }

            // And a path's perspective, read off the endpoint's dimension of that kind, which is
            // exactly what the compiler wrote the target's rules for that perspective over (D265
            // §2, §6).
            foreach (var declaredPath in model.Paths)
            {
                declares |= Reaches(
                    grant, declaredPath.EndpointDimension, declaredPath.EndpointDimensions);
            }

            if (!declares)
            {
                continue;
            }

            // A row class is one tenancy the principal holds, and a confined grant is a narrower
            // tenancy than the same grant unconfined: the confinements are part of the key (D266).
            var key = Tenancy(grant);
            if (!byTenancy.TryGetValue(key, out var roles))
            {
                roles = new HashSet<string>(StringComparer.Ordinal);
                byTenancy[key] = roles;
            }

            roles.Add(grant.Role);
            if (reachesADimensionOfItsOwn)
            {
                direct.Add(key);
            }
        }

        var classes = new List<RowClass>();
        foreach (var (key, roles) in byTenancy)
        {
            classes.Add(new RowClass
            {
                Roles = roles,
                IsCreator = false,
                InTheRowPredicate = direct.Contains(key),
            });
        }

        if (globalRoles.Count > 0)
        {
            // `@ctx.global` is a disjunct of the row predicate itself (D205, D215).
            classes.Add(new RowClass
            {
                Roles = globalRoles,
                IsCreator = false,
                InTheRowPredicate = true,
            });
        }

        if (model.Declared.ResourceOwnerColumn.Length > 0)
        {
            // And so is the creator's fail-safe (D206, D215).
            classes.Add(new RowClass
            {
                Roles = new HashSet<string>(StringComparer.Ordinal),
                IsCreator = true,
                InTheRowPredicate = true,
            });
        }

        if (classes.Count == 0)
        {
            // No grant reaches this table. The row predicate folds to FALSE and no row is disclosed,
            // and what the planner reports for every column is the `otherwise`.
            classes.Add(new RowClass
            {
                Roles = new HashSet<string>(StringComparer.Ordinal),
                IsCreator = false,
                InTheRowPredicate = false,
            });
        }

        return (classes, globalRoles);
    }

    private sealed class RowClass
    {
        internal required HashSet<string> Roles { get; init; }

        internal required bool IsCreator { get; init; }

        /// <summary>
        /// Whether the table's <b>row predicate</b> admits this class — a tenancy the table holds a
        /// dimension of, the global grant, or the row's own creator — as against one reached only
        /// through a parent or along a declared path, whose marker the planner ORs into
        /// <c>Filter_R</c> and whose text is nowhere in the predicate (D269 (c), design 38 §4).
        /// </summary>
        /// <remarks>
        /// It is what <see cref="Roles.Visible"/> means: the compiler writes that predicate as the
        /// rule's condition, so the rule speaks for exactly the routes the predicate names and for
        /// no other. A principal who also reaches rows along a path meets the rule on some of its
        /// rows and not on others, which is why the column stays per-row for it — the same answer
        /// the fold reaches, from the other side.
        /// </remarks>
        internal required bool InTheRowPredicate { get; init; }
    }

    /// <summary>
    /// What the read must say this column discloses: the names its rules can still yield over the
    /// rows this principal can see, met as the pass meets them — one name is that name, a reachable
    /// population-only level dominates, and anything else is decided per row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The last name in that set is the one worth explaining. The core evaluates rules first-match-
    /// wins with a default, and it drops the default only when one rule holds for <em>every</em> row
    /// that reaches the leaf — which it can see when one rule's condition is the row predicate and
    /// not otherwise. So a table whose rows are visible through several routes — two tenancies, or a
    /// tenancy and the resource-owner fail-safe — keeps its default reachable even though no row can
    /// actually miss every rule, and the column is honestly reported as decided per row. The model
    /// says the same thing from the declarations: the default is reachable unless one rule covers
    /// every route this principal has to the table.
    /// </para>
    /// <para>
    /// Where the table is entitled along <b>declared paths</b> the same sentence is said of the
    /// routes (D269 (a), F87): the verdict is decided at each route's endpoint and read back as an
    /// ordinal, and it is a constant only where one route reaches the table in one tenancy.
    /// </para>
    /// </remarks>
    private Chalk.Ir.DisclosureOutcome Outcome(
        TenancyCompiler.TableModel model,
        string column,
        List<RowClass> classes,
        HashSet<string> globalRoles,
        ReachedRoutes routes)
    {
        var levels = new HashSet<Verdict>();
        foreach (var rowClass in classes)
        {
            levels.Add(Level(model, column, rowClass));
        }

        // A rule with a condition of its own holds for some rows and not others, and the package
        // knows no more about the row than the planner does: the level it grants is reachable and so
        // is the level below it, which makes the honest answer PerRow (D221).
        foreach (var rule in TenancyCompiler.ColumnPlan(model.Declared, model, column))
        {
            if (rule.When.Length > 0)
            {
                levels.Add(rule.Level);
                levels.Add(Verdict.None);
            }
        }

        if (!Covered(model, column, classes, globalRoles))
        {
            levels.Add(Verdict.None);
        }

        // The meet along the routes (D269 (a), design 38 §4, F87). A column of a table entitled
        // along declared paths is decided at each route's endpoint, at the endpoint's own
        // cardinality, and the target reads the least of the ordinals the routes project back. That
        // is one value for every row the target keeps only where a single route reaches it in a
        // single tenancy; with a second route, or a second tenancy along one route, the ordinal
        // arrives on the target as a column, and the level under every rule — the table's
        // `otherwise` — is reachable beside the levels the routes grant, because a row this
        // principal sees may be one the route at hand did not reach. That is the LEFT JOIN's NULL,
        // read from the declarations rather than from the plan.
        //
        // Not so for a rule speaking to a role held *everywhere*: its condition is unconditionally
        // true, the routes decide nothing, and the level it grants is the answer for every row.
        if (!routes.OneOrdinal && !Everywhere(model, column, globalRoles))
        {
            levels.Add(Verdict.None);
        }

        if (levels.Contains(Verdict.AggregateOnly))
        {
            return Chalk.Ir.DisclosureOutcome.Aggregate;
        }

        // Test dominates None the way AggregateOnly dominates everything: what the leaf emits for
        // the column is the placeholder either way, and the one thing that tells the two apart is
        // that a permitted comparison is computed there as well (D261). A principal who is a support
        // agent for some rows and nothing for the rest may test all the rows they can see, so the
        // determinate answer is Tested rather than PerRow.
        if (levels.Contains(Verdict.Test) && levels.IsSubsetOf([Verdict.Test, Verdict.None]))
        {
            return Chalk.Ir.DisclosureOutcome.Tested;
        }

        if (levels.Count != 1)
        {
            return Chalk.Ir.DisclosureOutcome.PerRow;
        }

        return levels.Single() switch
        {
            Verdict.Full => Chalk.Ir.DisclosureOutcome.Full,
            Verdict.Mask => Chalk.Ir.DisclosureOutcome.Masked,
            Verdict.Test => Chalk.Ir.DisclosureOutcome.Tested,
            _ => Chalk.Ir.DisclosureOutcome.Redacted,
        };
    }

    /// <summary>Whether one rule of this column covers every route this principal has to the table.</summary>
    /// <remarks>
    /// A global grant is the case worth naming, and <see cref="Everywhere"/> is it: a rule written
    /// for a role the principal holds <em>everywhere</em> has a condition that is unconditionally
    /// true, so it covers every row of the table — the created-by ones included — and the default
    /// becomes unreachable.
    /// </remarks>
    private static bool Covered(
        TenancyCompiler.TableModel model,
        string column,
        List<RowClass> classes,
        HashSet<string> globalRoles)
    {
        if (classes.Count == 0)
        {
            return false;
        }

        if (Everywhere(model, column, globalRoles))
        {
            return true;
        }

        foreach (var rule in TenancyCompiler.ColumnPlan(model.Declared, model, column))
        {
            // A rule that carries a condition of its own covers the rows that satisfy it and no
            // others, and nothing here can say which those are (D221).
            if (rule.When.Length > 0)
            {
                continue;
            }

            var all = true;
            foreach (var rowClass in classes)
            {
                all &= rowClass.IsCreator
                    ? rule.IsCreator || Holds(rule.Roles, rowClass)
                    : !rule.IsCreator && Holds(rule.Roles, rowClass);
            }

            if (all)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether one rule of this column speaks for a role this principal holds <em>everywhere</em>.
    /// </summary>
    /// <remarks>
    /// Such a rule's condition carries the global grant's own boolean as a disjunct, which binds
    /// true, so the rule holds for every row of the table by whatever route it was reached — and
    /// nothing under it, neither the table's <c>otherwise</c> nor a later rule, can be reached at
    /// all. The creator's fail-safe is not one of these: it speaks for the row's own creator and a
    /// global grant says nothing about who that is.
    /// </remarks>
    private static bool Everywhere(
        TenancyCompiler.TableModel model, string column, HashSet<string> globalRoles)
    {
        if (globalRoles.Count == 0)
        {
            return false;
        }

        foreach (var rule in TenancyCompiler.ColumnPlan(model.Declared, model, column))
        {
            if (rule.When.Length == 0
                && !rule.IsCreator
                && globalRoles.Overlaps(rule.Roles.Select(role => role.Name)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// What the host's own ordinal walk gives one row class: its rules read in order, a matching one
    /// setting the tentative verdict and a matching stop-rule ending the walk, and nothing at all
    /// meaning <see cref="Verdict.None"/> (D222, D216's inherit-is-deny).
    /// </summary>
    /// <remarks>
    /// This is the model, walked from the declarations; the compiler reaches the same verdict through
    /// layer A's first-match-wins list, and <c>Reconcile</c> is the two being compared. A rule
    /// carrying a <c>When</c> is skipped here and answered by the caller, which reports the column
    /// <c>PerRow</c>: nothing in this package can see the row (D221).
    /// </remarks>
    private static Verdict Level(TenancyCompiler.TableModel model, string column, RowClass rowClass)
    {
        var declared = model.Declared;
        if (rowClass.IsCreator && declared.ResourceOwnerSees == ResourceOwnerSees.Full)
        {
            return Verdict.Full;
        }

        var realm = Realm(declared, column);
        Verdict? tentative = null;
        foreach (var rule in declared.Access)
        {
            if (rule.WhenText.Length > 0 || !About(rule, column, realm) || !Holds(rule, rowClass))
            {
                continue;
            }

            tentative = rule.Grants;
            if (rule.StopOnMatch)
            {
                break;
            }
        }

        return tentative ?? Verdict.None;
    }

    /// <summary>Whether this row class holds one of the grantees the rule speaks for.</summary>
    /// <remarks>
    /// The two explicit ones are not roles a class holds (D269 (c)): <c>Roles.Visible</c> speaks for
    /// every class the row predicate admits and for no other, and <c>Roles.Owner</c> for the row's
    /// own creator.
    /// </remarks>
    private static bool Holds(AccessRule rule, RowClass rowClass) =>
        Holds(rule.Roles, rowClass);

    private static bool Holds(IReadOnlyList<Role> grantees, RowClass rowClass)
    {
        if (grantees.Contains(Roles.Visible))
        {
            return rowClass.InTheRowPredicate;
        }

        if (rowClass.IsCreator && grantees.Contains(Roles.Owner))
        {
            return true;
        }

        foreach (var role in grantees)
        {
            if (rowClass.Roles.Contains(role.Name))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a rule speaks about this column: by naming it, by naming its realm, or by naming
    /// neither, which is every protected column of the table (D222).
    /// </summary>
    private static bool About(AccessRule rule, string column, string realm) =>
        rule.ColumnName.Length > 0
            ? string.Equals(rule.ColumnName, column, StringComparison.OrdinalIgnoreCase)
            : rule.RealmName.Length == 0
                || (realm.Length > 0
                    && string.Equals(rule.RealmName, realm, StringComparison.OrdinalIgnoreCase));

    private static string Realm(TenancyTable declared, string column)
    {
        foreach (var (realm, members) in declared.Realms)
        {
            foreach (var member in members)
            {
                if (string.Equals(member, column, StringComparison.OrdinalIgnoreCase))
                {
                    return realm;
                }
            }
        }

        return "";
    }
}

/// <summary>
/// One group a principal's grants bind a context list for (D266 §4, §5): a dimension, a role, and
/// the ordered set of tenancy kinds confining it.
/// </summary>
/// <remarks>
/// The confining kinds are what the list's tuple is made of, in this order:
/// <c>(&lt;the dimension's column&gt;, &lt;c1&gt;, …, &lt;cn&gt;)</c>. A group with no confining
/// kinds is the bare membership test the package has always written.
/// </remarks>
public sealed class TenancyGrantGroup
{
    /// <summary>The context list this group binds, by the name the descriptor's SQL reads.</summary>
    public required string List { get; init; }

    /// <summary>The dimension's kind.</summary>
    public required string Kind { get; init; }

    /// <summary>The role a grant must carry to fill this list.</summary>
    public required string Role { get; init; }

    /// <summary>Whether the list holds subject identifiers rather than tenancy ones.</summary>
    public required bool IsSubject { get; init; }

    /// <summary>The kinds confining this group, in the order the declaration names them.</summary>
    public required IReadOnlyList<string> Confining { get; init; }

    /// <summary>How many of the principal's grants fell into it. Zero makes its term false.</summary>
    public required int Grants { get; init; }

    public override string ToString() =>
        $"{List}: {(IsSubject ? "subject" : "tenancy")} '{Kind}' in role '{Role}'"
        + (Confining.Count == 0 ? ", unconfined" : ", confined by " + string.Join(", ", Confining))
        + $" — {Grants} grant" + (Grants == 1 ? "" : "s");
}

/// <summary>
/// What the policy says about one prepared statement, compared with what the planner said (§5,
/// D215): the per-column disagreements, and a three-valued verdict per entitled table on how much of
/// it this principal can see.
/// </summary>
public sealed class TenancyReconciliation
{
    /// <summary>One entry per disagreement, empty when the plan says what the policy says.</summary>
    public required IReadOnlyList<TenancyDifference> Differences { get; init; }

    /// <summary>One entry per entitled table the plan reads.</summary>
    public required IReadOnlyList<TenancyVisibility> Visibility { get; init; }

    /// <summary>
    /// Whether nothing disagreed. An <see cref="ReconciledVisibility.Indeterminate"/> table is not a
    /// disagreement: the package said it could not tell, which is not the same as agreeing.
    /// </summary>
    public bool Agrees =>
        Differences.Count == 0
        && !Visibility.Any(v => v.Verdict == ReconciledVisibility.Disagrees);
}

/// <summary>What the policy and the planner say about how much of one table is visible (D215).</summary>
public sealed class TenancyVisibility
{
    public required string Table { get; init; }

    /// <summary>What the policy predicts from the grants alone, or null where it cannot say.</summary>
    public required TableVisibility? Predicted { get; init; }

    /// <summary>What the planner's report says, which is always determinate.</summary>
    public required TableVisibility Reported { get; init; }

    public required ReconciledVisibility Verdict { get; init; }

    public override string ToString() =>
        Verdict == ReconciledVisibility.Indeterminate
            ? $"{Table}: indeterminate (the plan says {Reported})"
            : $"{Table}: the policy says {Predicted}, the plan says {Reported}";
}

/// <summary>The three answers a visibility reconciliation can give (D215).</summary>
public enum ReconciledVisibility
{
    /// <summary>The policy predicted what the planner reported.</summary>
    Agrees = 1,

    /// <summary>It predicted something else. One of the two is wrong.</summary>
    Disagrees = 2,

    /// <summary>
    /// The policy cannot predict this table — a host predicate outside the recognised grammar — so
    /// the comparison is reported as unmade rather than counted either way.
    /// </summary>
    Indeterminate = 3,
}

/// <summary>
/// One disagreement between what the policy says a column discloses and what the planner said it
/// did (§5). The corpus asserts there are none.
/// </summary>
public sealed class TenancyDifference
{
    public required string Table { get; init; }

    public required string Column { get; init; }

    /// <summary>What the policy, read from the principal's grants alone, says.</summary>
    public required Chalk.Ir.DisclosureOutcome Expected { get; init; }

    /// <summary>What the plan's own read says the pass concluded.</summary>
    public required Chalk.Ir.DisclosureOutcome Reported { get; init; }

    public override string ToString() =>
        $"{Table}.{Column}: the policy says {Expected}, the plan says {Reported}";
}

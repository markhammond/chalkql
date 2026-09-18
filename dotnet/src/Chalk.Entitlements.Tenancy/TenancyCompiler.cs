using System.Globalization;
using System.Text;
using Chalk.Catalog;
using TypeKind = Chalk.Ir.TypeKind;

namespace Chalk.Entitlements.Tenancy;

/// <summary>
/// The compiler from a <see cref="TenancyPolicy"/> to layer A's descriptors
/// (<c>docs/design/16-entitlements.md</c> §5).
/// </summary>
/// <remarks>
/// <para>
/// Two things come out of it. The <b>row predicate</b>: a membership test per dimension and role,
/// OR-ed together, AND the visibility rules for the roles found, OR the created-by fail-safe, OR the
/// global wildcard. And per protected column an ordered list of <b>rules</b>, most permissive first,
/// each exclusion folded into the conditions it excludes, so that first-match-wins reproduces the
/// model's combination without the core knowing what a role is.
/// </para>
/// <para>
/// Every expression it emits reads a column, a context scalar or a context list, and nothing else.
/// A relation would be a join, a join would be a round trip, and §2 forbids it.
/// </para>
/// </remarks>
internal static class TenancyCompiler
{
    /// <summary>
    /// Which tenancy kinds may confine which, by the kind being confined (D266 §2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Subject(kind, within: …)</c> names them for a subject kind; a <b>tenancy</b> kind may be
    /// confined by any <em>other</em> declared tenancy kind, which is a declaration by omission and
    /// is read here rather than written by the host.
    /// </para>
    /// <para>
    /// Every kind a policy speaks about is declared, because a <see cref="Kind"/> comes from
    /// <c>Tenancy(name)</c> or <c>Subject(name, …)</c> and from nowhere else (D270): the kinds read
    /// here are all of them, and a dimension naming one the policy never declared cannot be written.
    /// </para>
    /// </remarks>
    internal static IReadOnlyDictionary<string, IReadOnlyList<string>> Confinable(TenancyPolicy policy)
    {
        var map = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var tenancies = new List<string>();
        foreach (var kind in policy.Kinds)
        {
            if (!kind.IsSubject)
            {
                tenancies.Add(kind.Name);
            }
        }

        foreach (var kind in policy.Kinds)
        {
            if (kind.IsSubject)
            {
                map[kind.Name] = kind.WithinKinds;
                continue;
            }

            var others = new List<string>();
            foreach (var other in tenancies)
            {
                if (!string.Equals(other, kind.Name, StringComparison.Ordinal))
                {
                    others.Add(other);
                }
            }

            map[kind.Name] = others;
        }

        return map;
    }

    /// <summary>
    /// The schemas a policy compiles against, and the associations that join two of them (D270 (c)).
    /// </summary>
    /// <remarks>
    /// A policy is declared over a catalog and compiled against its schemas, because a table handle
    /// carries the source it came from and a path step may arrive in another one. Everything but a
    /// path step stays inside one schema: a dimension reads this table's own row, a dotted path
    /// follows this schema's foreign keys, and a <c>Through</c> names a parent of this schema.
    /// </remarks>
    internal sealed class Sources
    {
        internal required IReadOnlyList<SchemaDescriptor> Schemas { get; init; }

        internal required IReadOnlyList<AssociationDescriptor> Associations { get; init; }

        internal SchemaDescriptor? Schema(string name)
        {
            foreach (var schema in Schemas)
            {
                if (string.Equals(schema.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return schema;
                }
            }

            return null;
        }

        internal SchemaDescriptor Require(string name, string path) =>
            Schema(name)
            ?? throw new CatalogValidationException(
                path,
                $"the policy speaks about the source '{name}', which is not among the schemas it is "
                + $"compiled against ({string.Join(", ", Schemas.Select(s => s.Name))}).");
    }

    internal static TenancyEntitlements Compile(
        TenancyPolicy policy,
        IReadOnlyList<SchemaDescriptor> schemas,
        IReadOnlyList<AssociationDescriptor> associations)
    {
        var roles = Distinct(policy.Roles, "role", "policy");
        var scopes = Distinct(policy.Scopes, "scope", "policy");
        var confinable = Confinable(policy);
        var sources = new Sources { Schemas = schemas, Associations = associations };

        var descriptors = new Dictionary<string, TableEntitlementDescriptor>(StringComparer.OrdinalIgnoreCase);
        var lists = new List<BoundList>();
        var listNames = new HashSet<string>(StringComparer.Ordinal);
        var needsScope = false;
        var models = new Dictionary<string, TableModel>(StringComparer.OrdinalIgnoreCase);

        foreach (var declared in policy.Tables)
        {
            var path = $"tenancy.tables[{declared.Name}]";
            var schema = sources.Require(declared.Schema, path);
            var table = Find(schema, declared.Name)
                ?? throw new CatalogValidationException(
                    path,
                    $"the policy declares '{declared.Schema}.{declared.Name}', which that source "
                    + "does not hold");

            var model = Model(policy, sources, schema, table, declared, roles, scopes, confinable, path);
            needsScope |= model.ScopeTest.Length > 0;

            foreach (var list in model.Lists)
            {
                if (listNames.Add(list.Name))
                {
                    lists.Add(list);
                }
            }

            // An unrestricted table with nothing to say about any column carries no entitlement at
            // all, which is what keeps reference data free (D215): unrestricted means every row is
            // visible, not that the table must be silent about its columns.
            var descriptor = Descriptor(table, declared, model, roles, schema.Name, path);
            if (!declared.IsRestricted && descriptor.Columns.Count == 0)
            {
                continue;
            }

            models[Qualified(declared.Schema, declared.Name)] = model;
            descriptors[Qualified(declared.Schema, declared.Name)] = descriptor;
        }

        return new TenancyEntitlements(
            policy, schemas, descriptors, models, lists, confinable, needsScope);
    }

    // ------------------------------------------------------------------ the model

    /// <summary>One dimension, resolved against the schema: the column it reads and how.</summary>
    internal sealed class ResolvedDimension
    {
        internal required GrantDimension Declared { get; init; }

        /// <summary>The column of <em>this</em> table the membership test reads.</summary>
        internal required string Column { get; init; }

        internal required ChalkType Type { get; init; }
    }

    /// <summary>
    /// One <b>group</b> a dimension and role bind a list for (D266 §4): the ordered set of kinds
    /// confining it, resolved to the columns of the row the membership test is written over.
    /// </summary>
    /// <remarks>
    /// The empty group is today's bare list; a group of one is today's pair list; a group of several
    /// is the tuple of arity <c>1 + n</c>. Which groups exist at all is the <em>declaration's</em>
    /// answer and not a principal's, because one descriptor serves every principal: every subset of
    /// the confining kinds the policy permits that <b>resolves on this row</b> gets a term, and a
    /// principal holding no grant of that group binds the list empty, which folds the term away.
    /// </remarks>
    private sealed class ConfinementGroup
    {
        internal required IReadOnlyList<ResolvedDimension> Confining { get; init; }

        internal required string Name { get; init; }

        internal required IReadOnlyList<string> Columns { get; init; }

        internal IReadOnlyList<string> Kinds =>
            [.. Confining.Select(dimension => dimension.Declared.Kind)];
    }

    /// <summary>One <c>Through</c> restriction, resolved against the schema (§3.13, D227).</summary>
    internal sealed class ResolvedThrough
    {
        /// <summary>The correlation column of the child.</summary>
        internal required string Column { get; init; }

        internal required int ColumnOrdinal { get; init; }

        internal required string ParentTable { get; init; }

        /// <summary>The parent's schema, which a §3.13 parent shares with the child.</summary>
        internal required string ParentSchema { get; init; }

        internal required string ParentKey { get; init; }

        internal required int ParentKeyOrdinal { get; init; }

        /// <summary>The parent's own dimensions, which the child's role conditions are written over.</summary>
        internal required IReadOnlyList<ResolvedDimension> ParentDimensions { get; init; }

        /// <summary>
        /// Whether every child row has a parent row: the column is NOT NULL and a declared foreign
        /// key over it names this parent's key. With the parent's own predicate folded to TRUE that
        /// is what lets the planner elide the join, so it is also what lets <c>Reconcile</c> predict
        /// <c>All</c> (D226, D230).
        /// </summary>
        internal required bool Total { get; init; }
    }

    /// <summary>
    /// One <c>Inherited</c> or <c>Related</c> path, resolved against the schema (D265,
    /// <c>docs/design/38-existential-visibility.md</c> §1).
    /// </summary>
    internal sealed class ResolvedPath
    {
        /// <summary>The tenancy kind this path carries.</summary>
        internal required string Kind { get; init; }

        /// <summary>Whether the first step goes up, to a bridge (a <c>Related</c> path).</summary>
        internal required bool IsRelated { get; init; }

        internal required IReadOnlyList<ResolvedStep> Steps { get; init; }

        internal required string EndpointTable { get; init; }

        /// <summary>The endpoint's schema, which a cross-source path's differs from the target's.</summary>
        internal required string EndpointSchema { get; init; }

        /// <summary>
        /// The endpoint's own dimension of this kind, which the endpoint predicate and the target's
        /// rules for this perspective are written over.
        /// </summary>
        internal required ResolvedDimension EndpointDimension { get; init; }

        /// <summary>
        /// Every dimension of the endpoint's own row, which is where a confinement of this
        /// perspective is written: the endpoint predicate is evaluated there, so both columns of a
        /// conjoined term have to be there too (D266 §3).
        /// </summary>
        internal required IReadOnlyList<ResolvedDimension> EndpointDimensions { get; init; }

        /// <summary>
        /// Whether every step is <b>total</b> — a NOT NULL foreign key down to a declared unique key
        /// — which for an <c>Inherited</c> path is what lets the planner elide the joins when the
        /// endpoint predicate folds to TRUE (§4). A <c>Related</c> path is never total: a total key
        /// on the bridge says every bridge row has a target row, not the other way about.
        /// </summary>
        internal required bool Total { get; init; }
    }

    /// <summary>One step of a path, resolved: the two columns and the direction (§2).</summary>
    internal sealed class ResolvedStep
    {
        internal required string FromTable { get; init; }

        internal required int FromOrdinal { get; init; }

        internal required string ToTable { get; init; }

        /// <summary>The schema the step arrives in, which a cross-source step's differs from ours.</summary>
        internal required string ToSchema { get; init; }

        internal required int ToOrdinal { get; init; }

        /// <summary>True for a down-step to a parent, false for the one up-step to a bridge.</summary>
        internal required bool Down { get; init; }
    }

    /// <summary>A context list this policy binds, with the types its values must have.</summary>
    internal sealed class BoundList
    {
        internal required string Name { get; init; }

        internal required IReadOnlyList<string> Columns { get; init; }

        internal required IReadOnlyList<ChalkType> Types { get; init; }

        /// <summary>The dimension and role whose grants fill it.</summary>
        internal required string Kind { get; init; }

        internal required string Role { get; init; }

        /// <summary>Whether this list holds subject identifiers rather than tenancy ones.</summary>
        internal required bool IsSubject { get; init; }

        /// <summary>
        /// The confining kinds of the group this list is bound for, in the order the declaration
        /// names them (D266 §4). Empty is the bare list; one is today's pair list; several is the
        /// tuple of arity <c>1 + n</c>.
        /// </summary>
        internal required IReadOnlyList<string> Confining { get; init; }

        /// <summary>Whether it holds tuples rather than bare identifiers.</summary>
        internal bool IsPair => Confining.Count > 0;
    }

    /// <summary>One table, resolved: what its predicate reads and which roles may see its rows.</summary>
    internal sealed class TableModel
    {
        internal required TenancyTable Declared { get; init; }

        internal required IReadOnlyList<ResolvedDimension> Dimensions { get; init; }

        /// <summary>The parents this table's visibility derives through, in declaration order.</summary>
        internal required IReadOnlyList<ResolvedThrough> Through { get; init; }

        /// <summary>The declared paths this table holds a tenancy along, in order (D265).</summary>
        internal required IReadOnlyList<ResolvedPath> Paths { get; init; }

        internal required IReadOnlyList<string> AdmittedRoles { get; init; }

        internal required IReadOnlyList<BoundList> Lists { get; init; }

        /// <summary>The scope test the visibility rules impose, or empty.</summary>
        internal required string ScopeTest { get; init; }

        /// <summary>The scopes those rules name, for the principal to check its own against.</summary>
        internal required IReadOnlyList<string> Scopes { get; init; }

        /// <summary>Which tenancy kinds this policy declares may confine which (D266 §2).</summary>
        internal required IReadOnlyDictionary<string, IReadOnlyList<string>> Confinable { get; init; }

        /// <summary>Where a refusal about this table says it is.</summary>
        internal required string Path { get; init; }
    }

    private static TableModel Model(
        TenancyPolicy policy,
        Sources sources,
        SchemaDescriptor schema,
        TableDescriptor table,
        TenancyTable declared,
        IReadOnlyList<string> roles,
        IReadOnlyList<string> scopes,
        IReadOnlyDictionary<string, IReadOnlyList<string>> confinable,
        string path)
    {
        var dimensions = new List<ResolvedDimension>();
        foreach (var dimension in declared.Dimensions)
        {
            dimensions.Add(Resolve(policy, schema, table, declared, dimension, path));
        }

        var through = new List<ResolvedThrough>();
        var paths = new List<ResolvedPath>();
        foreach (var restriction in declared.Restrictions)
        {
            if (restriction.Kind == RestrictionKind.Through)
            {
                through.Add(ResolveThrough(policy, sources, schema, table, restriction, path));
            }
            else if (restriction.Kind is RestrictionKind.Inherited or RestrictionKind.Related)
            {
                paths.Add(ResolvePath(policy, sources, schema, table, restriction, path));
            }
        }

        RefuseVisibilityCycle(policy, schema, declared, [], path);

        var admitted = new List<string>();
        var ruleScopes = new List<string>();
        foreach (var rule in declared.Visibility)
        {
            foreach (var role in rule.Roles)
            {
                Require(roles, role.Name, "role", $"{path}.visibility");
                if (!admitted.Contains(role.Name, StringComparer.Ordinal))
                {
                    admitted.Add(role.Name);
                }
            }

            foreach (var scope in rule.ScopeNames)
            {
                Require(scopes, scope, "scope", $"{path}.visibility");
                if (!ruleScopes.Contains(scope, StringComparer.Ordinal))
                {
                    ruleScopes.Add(scope);
                }
            }
        }

        if (declared.Visibility.Count == 0)
        {
            admitted.AddRange(roles);
        }

        var lists = new List<BoundList>();
        foreach (var role in admitted)
        {
            foreach (var dimension in dimensions)
            {
                lists.AddRange(ListsOf(dimension, role, dimensions, confinable, path));
            }

            // The child's rules are decided by the roles held in the *parent's* tenancy (§3.13,
            // D228), so the lists those memberships read are this table's to bind too — a role the
            // child admits and the parent does not would otherwise have no list at all.
            foreach (var parent in through)
            {
                foreach (var dimension in parent.ParentDimensions)
                {
                    lists.AddRange(
                        ListsOf(dimension, role, parent.ParentDimensions, confinable, path));
                }
            }

            // And a path's endpoint dimension, for the same reason: the endpoint predicate and the
            // target's rules for that perspective read it, so the lists those memberships read are
            // this table's to bind too (D265 §2). A confinement of that perspective is written over
            // the endpoint's own row, which is the row the predicate is evaluated on (D266 §3).
            foreach (var declaredPath in paths)
            {
                lists.AddRange(ListsOf(
                    declaredPath.EndpointDimension,
                    role,
                    declaredPath.EndpointDimensions,
                    confinable,
                    path));
            }
        }

        var scopeTest = new StringBuilder();
        if (ruleScopes.Count > 0)
        {
            scopeTest.Append('(');
            for (var i = 0; i < ruleScopes.Count; i++)
            {
                scopeTest.Append(i == 0 ? "" : " OR ")
                    .Append("@ctx.scope = ")
                    .Append(Quote(ruleScopes[i]));
            }

            scopeTest.Append(')');
        }

        if (declared.ResourceOwnerColumn.Length > 0
            && Column(table, declared.ResourceOwnerColumn) is null)
        {
            throw new CatalogValidationException(
                path,
                $"ResourceOwner names '{declared.ResourceOwnerColumn}', which is not a column of "
                + "the table");
        }

        foreach (var restriction in declared.Restrictions)
        {
            if (restriction.Kind == RestrictionKind.Predicate && restriction.Predicate.Length == 0)
            {
                throw new CatalogValidationException(path, "a Predicate restriction is empty");
            }
        }

        // A `Related` path's marker says that *some* related row belongs to the tenancy, and a
        // confinement of that perspective would have to be borne by the marker. D266 §3 refuses it
        // by name and leaves a marker-borne confinement to a later decision; an `Inherited` path is
        // flattened to a column of the endpoint's row and is built.
        foreach (var declaredPath in paths)
        {
            if (!declaredPath.IsRelated
                || !confinable.TryGetValue(declaredPath.Kind, out var permitted))
            {
                continue;
            }

            foreach (var dimension in declaredPath.EndpointDimensions)
            {
                if (dimension.Declared.IsSubject
                    || !permitted.Contains(dimension.Declared.Kind, StringComparer.Ordinal))
                {
                    continue;
                }

                throw new CatalogValidationException(
                    $"{path}.related",
                    $"Related(\"{declaredPath.Kind}\") on '{declared.Name}' reaches the endpoint "
                    + $"'{declaredPath.EndpointTable}', which also holds '{dimension.Declared.Kind}' "
                    + $"— a kind this policy declares may confine '{declaredPath.Kind}'. A grant so "
                    + "confined would have to be borne by the path's existence marker, and "
                    + "confinement along a Related kind is refused in this decision "
                    + "(docs/design/40-conjoined-confinement.md §3, §7, D266): reach the endpoint "
                    + "with an Inherited path, or take the confining kind off the endpoint.");
            }
        }

        return new TableModel
        {
            Declared = declared,
            Dimensions = dimensions,
            Through = through,
            Paths = paths,
            AdmittedRoles = admitted,
            Lists = lists,
            ScopeTest = scopeTest.ToString(),
            Scopes = ruleScopes,
            Confinable = confinable,
            Path = path,
        };
    }

    /// <summary>
    /// The most confining kinds one dimension may carry on one table. Each is a factor of two in the
    /// terms of a row predicate, and a policy that wanted more than this has said something the
    /// compiler would turn into a descriptor nobody could read.
    /// </summary>
    private const int MaxConfiningKinds = 4;

    /// <summary>
    /// Every group a dimension binds a list for, over the row <paramref name="siblings"/> describes
    /// (D266 §4): the ordered subsets of the confining kinds this policy permits that resolve there.
    /// </summary>
    /// <remarks>
    /// The non-empty subsets come first, in the order the declaration names their kinds, and the
    /// empty one last — which is exactly the order a subject dimension's pair list and identifier
    /// list were emitted in before D266, so a policy with one confining kind compiles to the text it
    /// compiled to.
    /// </remarks>
    private static IReadOnlyList<ConfinementGroup> Groups(
        ResolvedDimension dimension,
        string role,
        IReadOnlyList<ResolvedDimension> siblings,
        IReadOnlyDictionary<string, IReadOnlyList<string>> confinable,
        string path)
    {
        var declared = dimension.Declared;
        var available = new List<ResolvedDimension>();
        if (confinable.TryGetValue(declared.Kind, out var permitted))
        {
            foreach (var kind in permitted)
            {
                if (string.Equals(kind, declared.Kind, StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (var sibling in siblings)
                {
                    if (!sibling.Declared.IsSubject
                        && string.Equals(sibling.Declared.Kind, kind, StringComparison.Ordinal)
                        && !available.Contains(sibling))
                    {
                        available.Add(sibling);
                    }
                }
            }
        }

        if (available.Count > MaxConfiningKinds)
        {
            throw new CatalogValidationException(
                path,
                $"the dimension '{declared.Kind}' may be confined by {available.Count} tenancy kinds "
                + $"that this table resolves, and the compiler emits one membership test per subset "
                + $"of them — {1 << available.Count} per role. {MaxConfiningKinds} is the most it "
                + "will write. Narrow the kinds that may confine it "
                + "(docs/design/40-conjoined-confinement.md §2, §4, D266).");
        }

        var groups = new List<ConfinementGroup>();
        for (var mask = 1; mask < 1 << available.Count; mask++)
        {
            groups.Add(Group(dimension, role, Subset(available, mask)));
        }

        groups.Add(Group(dimension, role, []));
        return groups;
    }

    private static List<ResolvedDimension> Subset(IReadOnlyList<ResolvedDimension> of, int mask)
    {
        var chosen = new List<ResolvedDimension>();
        for (var i = 0; i < of.Count; i++)
        {
            if ((mask & (1 << i)) != 0)
            {
                chosen.Add(of[i]);
            }
        }

        return chosen;
    }

    /// <summary>
    /// One group's list name and column names (D266 §4). The bare list and the pair list keep the
    /// names they had — <c>&lt;kind&gt;_&lt;role&gt;</c>, <c>_ids</c> and <c>_pairs</c> — so a
    /// recorded context or a fixture that binds them still binds them; every other group is
    /// <c>&lt;kind&gt;_&lt;role&gt;_within_&lt;c1&gt;[_&lt;c2&gt;…]</c>.
    /// </summary>
    private static ConfinementGroup Group(
        ResolvedDimension dimension, string role, IReadOnlyList<ResolvedDimension> confining)
    {
        var declared = dimension.Declared;
        var kind = declared.Kind;
        var head = declared.IsSubject ? "subject" : "id";
        if (confining.Count == 0)
        {
            return new ConfinementGroup
            {
                Confining = confining,
                Name = declared.IsSubject ? $"{kind}_{role}_ids" : $"{kind}_{role}",
                Columns = [head],
            };
        }

        // The pair list of today is the one-confinement case along the dimension's *first* declared
        // confining kind, which is what a subject dimension had before D266 and all it could have.
        if (declared.IsSubject
            && confining.Count == 1
            && string.Equals(
                confining[0].Declared.Kind, declared.Within, StringComparison.Ordinal))
        {
            return new ConfinementGroup
            {
                Confining = confining,
                Name = $"{kind}_{role}_pairs",
                Columns = ["subject", "within"],
            };
        }

        return new ConfinementGroup
        {
            Confining = confining,
            Name = $"{kind}_{role}_within_"
                + string.Join("_", confining.Select(c => c.Declared.Kind)),
            Columns = [head, .. confining.Select(c => c.Declared.Kind)],
        };
    }

    /// <summary>The lists one dimension and role bind: one per group (D266 §4).</summary>
    private static IEnumerable<BoundList> ListsOf(
        ResolvedDimension dimension,
        string role,
        IReadOnlyList<ResolvedDimension> siblings,
        IReadOnlyDictionary<string, IReadOnlyList<string>> confinable,
        string path)
    {
        foreach (var group in Groups(dimension, role, siblings, confinable, path))
        {
            yield return new BoundList
            {
                Name = group.Name,
                Columns = group.Columns,
                Types = [dimension.Type, .. group.Confining.Select(c => c.Type)],
                Kind = dimension.Declared.Kind,
                Role = role,
                IsSubject = dimension.Declared.IsSubject,
                Confining = group.Kinds,
            };
        }
    }

    // ------------------------------------------------------------------ through a parent (§3.13)

    /// <summary>
    /// One <c>Through</c> restriction, resolved: the parent, its key, and whether every child row
    /// has one (D227).
    /// </summary>
    /// <remarks>
    /// The parent and its key come from the declared foreign key over the column, which is the
    /// association the schema already states — a source that declares none names them explicitly.
    /// The parent must be a table this policy declares and must itself be restricted: through an
    /// unrestricted parent restricts nothing, and a policy that said so would be saying nothing.
    /// </remarks>
    private static ResolvedThrough ResolveThrough(
        TenancyPolicy policy,
        Sources sources,
        SchemaDescriptor schema,
        TableDescriptor table,
        Restriction restriction,
        string path)
    {
        var column = Column(table, restriction.Column)
            ?? throw new CatalogValidationException(
                $"{path}.through",
                $"Through names '{restriction.Column}', which is not a column of '{table.Name}'");

        var parentName = restriction.ParentTable;
        var parentKey = restriction.ParentKey;

        // The parent's own source: the entitled table's where the foreign key resolved it, since a
        // foreign key names a table of its own schema, and the declared one where the host named it.
        var parentSchema = restriction.ParentSchema.Length == 0
            ? schema
            : sources.Require(restriction.ParentSchema, $"{path}.through");
        var total = !column.Type.Nullable;
        if (parentName.Length == 0)
        {
            var found = ForeignKeyOver(table, Ordinal(table, column.Name));
            if (found is null)
            {
                throw new CatalogValidationException(
                    $"{path}.through",
                    $"Through('{restriction.Column}') has no declared foreign key over that column "
                    + $"on '{table.Name}', so there is nothing to read the parent off. Declare the "
                    + "foreign key, or name the parent and its key: "
                    + $"Through({table.Name}.Column(\"{restriction.Column}\"), parent, "
                    + "parent.Column(\"<key>\")).");
            }

            parentName = found.ParentTable;
            parentSchema = schema;
            var parentTableForKey = Find(schema, parentName)
                ?? throw new CatalogValidationException(
                    $"{path}.through",
                    $"the foreign key over '{restriction.Column}' names parent '{parentName}', "
                    + "which the schema does not hold");
            parentKey = parentTableForKey.Columns[found.ParentColumns[0]].Name;
        }
        else
        {
            total = total && ForeignKeyOver(table, Ordinal(table, column.Name)) is not null;
        }

        var parent = Find(parentSchema, parentName)
            ?? throw new CatalogValidationException(
                $"{path}.through",
                $"Through('{restriction.Column}') names parent '{parentName}', which the schema "
                + "does not hold");

        var declaredParent = policy.TableOf(parentSchema.Name, parent.Name)
            ?? throw new CatalogValidationException(
                $"{path}.through",
                $"Through('{restriction.Column}') names parent '{parent.Name}', which this policy "
                + "does not declare. A row's visibility can only derive from a table the policy "
                + "speaks about (docs/design/16-entitlements.md §3.13, D227).");

        if (!declaredParent.IsRestricted)
        {
            throw new CatalogValidationException(
                $"{path}.through",
                $"Through('{restriction.Column}') names parent '{parent.Name}', which this policy "
                + "leaves unrestricted. Through an unrestricted parent restricts nothing; declare "
                + "the child unrestricted, or restrict the parent (§3.13, D227).");
        }

        var keyColumn = Column(parent, parentKey)
            ?? throw new CatalogValidationException(
                $"{path}.through",
                $"Through('{restriction.Column}') names the parent key '{parent.Name}.{parentKey}', "
                + "which is not a column of the parent");

        var keyOrdinal = Ordinal(parent, keyColumn.Name);
        if (!IsUniqueKey(parent, keyOrdinal))
        {
            throw new CatalogValidationException(
                $"{path}.through",
                $"Through('{restriction.Column}') resolves to '{parent.Name}.{keyColumn.Name}', "
                + "which is not a declared unique key of the parent. The join it compiles into must "
                + "not multiply rows, and a unique key is what makes that so (§3.13, D226). Declare "
                + "the key.");
        }

        var parentDimensions = new List<ResolvedDimension>();
        foreach (var dimension in declaredParent.Dimensions)
        {
            parentDimensions.Add(Resolve(policy, parentSchema, parent, declaredParent, dimension, path));
        }

        return new ResolvedThrough
        {
            Column = column.Name,
            ColumnOrdinal = Ordinal(table, column.Name),
            ParentTable = parent.Name,
            ParentSchema = parentSchema.Name,
            ParentKey = keyColumn.Name,
            ParentKeyOrdinal = keyOrdinal,
            ParentDimensions = parentDimensions,
            Total = total,
        };
    }

    // ------------------------------------------------------------ Direct, Inherited, Related (D265)

    /// <summary>
    /// One <c>Inherited</c> or <c>Related</c> path, resolved step by step through the declared
    /// foreign keys (D265 §1, §3).
    /// </summary>
    /// <remarks>
    /// <b>The direction is checked, not inferred.</b> A down-step wants a foreign key from the
    /// current table to the named one; the single up-step of a <c>Related</c> path wants one from the
    /// named table to this table's declared unique key. This run admits an <c>Inherited</c> path of
    /// any length and a <c>Related</c> path of one up-step followed by down-steps, and refuses every
    /// other shape by name. The endpoint must hold the kind directly — or inherit it, which is
    /// flattened here so the wire carries one chain.
    /// </remarks>
    private static ResolvedPath ResolvePath(
        TenancyPolicy policy,
        Sources sources,
        SchemaDescriptor schema,
        TableDescriptor table,
        Restriction restriction,
        string path)
    {
        var verb = restriction.Kind == RestrictionKind.Related ? "Related" : "Inherited";
        var where = $"{path}.{verb.ToLowerInvariant()}";
        if (restriction.Steps.Count == 0)
        {
            throw new CatalogValidationException(
                where,
                $"{verb}(\"{restriction.TenancyKind}\") on '{table.Name}' declares no step. A path "
                + "with no step is a Direct dimension written the long way round "
                + "(docs/design/38-existential-visibility.md §1, §3, D265).");
        }

        var steps = new List<ResolvedStep>(restriction.Steps.Count);
        var at = table;
        var atSchema = schema;
        var total = restriction.Kind == RestrictionKind.Inherited;
        for (var i = 0; i < restriction.Steps.Count; i++)
        {
            var step = restriction.Steps[i];
            var toSchema = sources.Require(
                step.Schema.Length == 0 ? schema.Name : step.Schema, where);
            var to = Find(toSchema, step.Table)
                ?? throw new CatalogValidationException(
                    where,
                    $"step {i} of {verb}(\"{restriction.TenancyKind}\") on '{table.Name}' names "
                    + $"'{toSchema.Name}.{step.Table}', which that source does not hold");

            if (string.Equals(to.Name, table.Name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(toSchema.Name, schema.Name, StringComparison.OrdinalIgnoreCase))
            {
                throw new CatalogValidationException(
                    where,
                    $"step {i} of {verb}(\"{restriction.TenancyKind}\") returns to '{table.Name}' "
                    + "itself. A row's visibility cannot derive from its own table's, so a bridge "
                    + "and an endpoint are both other tables (§3, D265).");
            }

            var up = restriction.Kind == RestrictionKind.Related && i == 0;
            var resolved = up
                ? ResolveUpStep(policy, sources, atSchema, at, toSchema, to, step, restriction, where, i)
                : ResolveDownStep(
                    policy, sources, atSchema, at, toSchema, to, step, restriction, verb, where, i,
                    fromIsBridge: restriction.Kind == RestrictionKind.Related && i == 1);
            steps.Add(resolved);
            total = total && !Column(at, at.Columns[resolved.FromOrdinal].Name)!.Type.Nullable;
            at = to;
            atSchema = toSchema;
        }

        // The endpoint holds the kind: directly, or inherited, which is flattened onto this chain so
        // the wire carries one route and the pass one set of joins (§1).
        var flattened = 0;
        while (true)
        {
            var declaredEndpoint = policy.TableOf(atSchema.Name, at.Name)
                ?? throw new CatalogValidationException(
                    where,
                    $"{verb}(\"{restriction.TenancyKind}\") on '{table.Name}' ends at "
                    + $"'{atSchema.Name}.{at.Name}', which this policy does not declare. A row's "
                    + "visibility can only derive from a table the policy speaks about (§3, D265).");

            if (DimensionOfKind(declaredEndpoint, restriction.TenancyKind) is { } held)
            {
                var endpointDimensions = new List<ResolvedDimension>();
                foreach (var dimension in declaredEndpoint.Dimensions)
                {
                    endpointDimensions.Add(
                        Resolve(policy, atSchema, at, declaredEndpoint, dimension, path));
                }

                return new ResolvedPath
                {
                    Kind = restriction.TenancyKind,
                    IsRelated = restriction.Kind == RestrictionKind.Related,
                    Steps = steps,
                    EndpointTable = at.Name,
                    EndpointSchema = atSchema.Name,
                    EndpointDimension =
                        Resolve(policy, atSchema, at, declaredEndpoint, held, path),
                    EndpointDimensions = endpointDimensions,
                    Total = total,
                };
            }

            var onward = InheritedOfKind(declaredEndpoint, restriction.TenancyKind);
            if (onward is null || ++flattened > 16)
            {
                throw new CatalogValidationException(
                    where,
                    $"{verb}(\"{restriction.TenancyKind}\") on '{table.Name}' ends at '{at.Name}', "
                    + $"which does not hold the kind '{restriction.TenancyKind}'. A path ends where "
                    + "the key lives: declare it there with "
                    + $"Direct(\"{restriction.TenancyKind}\", \"<column>\"), or carry the path on "
                    + "with another step (§1, §3, D265).");
            }

            for (var i = 0; i < onward.Steps.Count; i++)
            {
                var step = onward.Steps[i];
                var toSchema = sources.Require(
                    step.Schema.Length == 0 ? atSchema.Name : step.Schema, where);
                var to = Find(toSchema, step.Table)
                    ?? throw new CatalogValidationException(
                        where,
                        $"the endpoint '{at.Name}' inherits '{restriction.TenancyKind}' through "
                        + $"'{toSchema.Name}.{step.Table}', which that source does not hold");
                var resolved = ResolveDownStep(
                    policy, sources, atSchema, at, toSchema, to, step, onward, "Inherited", where, i,
                    fromIsBridge: false);
                steps.Add(resolved);
                total = total && !at.Columns[resolved.FromOrdinal].Type.Nullable;
                at = to;
                atSchema = toSchema;
            }
        }
    }

    /// <summary>
    /// The one up-step of a <c>Related</c> path: the named table holds a declared foreign key to this
    /// table's unique key, which is what makes it a bridge (§1).
    /// </summary>
    private static ResolvedStep ResolveUpStep(
        TenancyPolicy policy,
        Sources sources,
        SchemaDescriptor fromSchema,
        TableDescriptor from,
        SchemaDescriptor bridgeSchema,
        TableDescriptor bridge,
        VisibilityStep step,
        Restriction restriction,
        string where,
        int ordinal)
    {
        var keys = new List<(int Child, int Parent)>();
        if (string.Equals(bridgeSchema.Name, fromSchema.Name, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var key in bridge.ForeignKeys)
            {
                if (key.Columns.Count == 1
                    && key.ParentColumns.Count == 1
                    && string.Equals(key.ParentTable, from.Name, StringComparison.OrdinalIgnoreCase)
                    && (step.On.Length == 0
                        || string.Equals(
                            bridge.Columns[key.Columns[0]].Name, step.On, StringComparison.OrdinalIgnoreCase)))
                {
                    keys.Add((key.Columns[0], key.ParentColumns[0]));
                }
            }
        }

        // The bridge is in another source, so no foreign key of its own can name this table: a
        // declared association is what states the same thing across the two (§3, D270 (c)).
        foreach (var association in
                 Associations(sources, bridgeSchema, bridge, fromSchema, from, step.On))
        {
            keys.Add(association);
        }

        if (keys.Count == 0)
        {
            throw new CatalogValidationException(
                where,
                $"step {ordinal} of Related(\"{restriction.TenancyKind}\") goes up from "
                + $"'{from.Name}' to '{bridge.Name}', and '{bridge.Name}' declares no foreign key "
                + (step.On.Length == 0 ? "" : $"over '{step.On}' ")
                + $"naming '{from.Name}'"
                + (string.Equals(bridgeSchema.Name, fromSchema.Name, StringComparison.OrdinalIgnoreCase)
                    ? ""
                    : $", and the catalog declares no association from '{bridgeSchema.Name}."
                      + $"{bridge.Name}' to '{fromSchema.Name}.{from.Name}' either")
                + ". The first step of a Related path names the bridge — the "
                + "table that references us — and the direction is checked, not inferred (§1, §3, "
                + "D265; docs/design/45-typed-tenancy-surface.md §3, D270).");
        }

        if (keys.Count > 1)
        {
            throw new CatalogValidationException(
                where,
                $"step {ordinal} of Related(\"{restriction.TenancyKind}\") is ambiguous: "
                + $"'{bridge.Name}' declares {keys.Count} references naming '{from.Name}'. Name "
                + $"the bridge's column: Through(table, on: column) (§1, D265).");
        }

        var chosen = keys[0];
        RefuseProtectedBridgeKey(
            policy, bridgeSchema, bridge, bridge.Columns[chosen.Child].Name, where);
        var parentColumn = chosen.Parent;
        if (!IsUniqueKey(from, parentColumn))
        {
            throw new CatalogValidationException(
                where,
                $"step {ordinal} of Related(\"{restriction.TenancyKind}\") joins "
                + $"'{from.Name}.{from.Columns[parentColumn].Name}', which is not a declared unique "
                + "key of the table. The key set the planner joins back to must not multiply rows, "
                + "and a unique key is what makes that so (§3, D265). Declare the key.");
        }

        RefuseTypeDisagreement(
            fromSchema, from, parentColumn, bridgeSchema, bridge, chosen.Child, where, ordinal);

        return new ResolvedStep
        {
            FromTable = from.Name,
            FromOrdinal = parentColumn,
            ToTable = bridge.Name,
            ToSchema = bridgeSchema.Name,
            ToOrdinal = chosen.Child,
            Down = false,
        };
    }

    /// <summary>A down-step: this table holds a declared foreign key to the named parent (§1).</summary>
    private static ResolvedStep ResolveDownStep(
        TenancyPolicy policy,
        Sources sources,
        SchemaDescriptor fromSchema,
        TableDescriptor from,
        SchemaDescriptor parentSchema,
        TableDescriptor parent,
        VisibilityStep step,
        Restriction restriction,
        string verb,
        string where,
        int ordinal,
        bool fromIsBridge)
    {
        var keys = new List<(int Child, int Parent)>();
        if (string.Equals(parentSchema.Name, fromSchema.Name, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var key in from.ForeignKeys)
            {
                if (key.Columns.Count == 1
                    && key.ParentColumns.Count == 1
                    && string.Equals(key.ParentTable, parent.Name, StringComparison.OrdinalIgnoreCase)
                    && (step.On.Length == 0
                        || string.Equals(
                            from.Columns[key.Columns[0]].Name, step.On, StringComparison.OrdinalIgnoreCase)))
                {
                    keys.Add((key.Columns[0], key.ParentColumns[0]));
                }
            }
        }

        // Or a declared association from this table, which is the same statement for a pair a
        // foreign key cannot reach because the two tables are in two sources (§3, D270 (c)).
        foreach (var association in
                 Associations(sources, fromSchema, from, parentSchema, parent, step.On))
        {
            keys.Add(association);
        }

        if (keys.Count == 0)
        {
            throw new CatalogValidationException(
                where,
                $"step {ordinal} of {verb}(\"{restriction.TenancyKind}\") goes down from "
                + $"'{from.Name}' to '{parent.Name}', and '{from.Name}' declares no foreign key "
                + (step.On.Length == 0 ? "" : $"over '{step.On}' ")
                + $"naming '{parent.Name}'"
                + (string.Equals(parentSchema.Name, fromSchema.Name, StringComparison.OrdinalIgnoreCase)
                    ? ""
                    : $", and the catalog declares no association from '{fromSchema.Name}."
                      + $"{from.Name}' to '{parentSchema.Name}.{parent.Name}' either")
                + ". A down-step follows a declared foreign key of the "
                + "current table, or a declared association from it, and the direction is checked, "
                + "not inferred (§1, §3, D265; docs/design/45-typed-tenancy-surface.md §3, D270).");
        }

        if (keys.Count > 1)
        {
            throw new CatalogValidationException(
                where,
                $"step {ordinal} of {verb}(\"{restriction.TenancyKind}\") is ambiguous: "
                + $"'{from.Name}' declares {keys.Count} references naming '{parent.Name}'. Name "
                + $"our column: Through(table, on: column) (§1, D265).");
        }

        var chosen = keys[0];
        if (!IsUniqueKey(parent, chosen.Parent))
        {
            throw new CatalogValidationException(
                where,
                $"step {ordinal} of {verb}(\"{restriction.TenancyKind}\") joins "
                + $"'{parent.Name}.{parent.Columns[chosen.Parent].Name}', which is not a "
                + "declared unique key of that table. The joins a path compiles into must not "
                + "multiply rows, and a unique key is what makes that so (§3, D265). Declare the "
                + "key.");
        }

        // The step out of the bridge reads the bridge's second key column, which the mechanism reads
        // raw and discloses to nobody, exactly as D227 leaves a parent's key full (§3).
        if (fromIsBridge)
        {
            RefuseProtectedBridgeKey(
                policy, fromSchema, from, from.Columns[chosen.Child].Name, where);
        }

        RefuseTypeDisagreement(
            fromSchema, from, chosen.Child, parentSchema, parent, chosen.Parent, where, ordinal);

        return new ResolvedStep
        {
            FromTable = from.Name,
            FromOrdinal = chosen.Child,
            ToTable = parent.Name,
            ToSchema = parentSchema.Name,
            ToOrdinal = chosen.Parent,
            Down = true,
        };
    }

    /// <summary>
    /// The declared associations from one table to another, as the (child, parent) ordinals a
    /// foreign key would have given (§3, D270 (c)).
    /// </summary>
    /// <remarks>
    /// An association is the host's assertion and is <b>not verified</b> against either source; what
    /// is checked here is what a foreign key's shape is checked for — that both columns exist, and
    /// (by the caller) that the far one is a declared unique key and the two types agree.
    /// </remarks>
    private static IEnumerable<(int Child, int Parent)> Associations(
        Sources sources,
        SchemaDescriptor childSchema,
        TableDescriptor child,
        SchemaDescriptor parentSchema,
        TableDescriptor parent,
        string on)
    {
        foreach (var association in sources.Associations)
        {
            if (!string.Equals(association.FromSchema, childSchema.Name, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(association.FromTable, child.Name, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(association.ToSchema, parentSchema.Name, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(association.ToTable, parent.Name, StringComparison.OrdinalIgnoreCase)
                || (on.Length > 0
                    && !string.Equals(association.FromColumn, on, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var childOrdinal = child.IndexOfColumn(association.FromColumn);
            var parentOrdinal = parent.IndexOfColumn(association.ToColumn);
            if (childOrdinal >= 0 && parentOrdinal >= 0)
            {
                yield return (childOrdinal, parentOrdinal);
            }
        }
    }

    /// <summary>
    /// The two columns a step joins must agree in type, exactly as a foreign key's do (§3, D270 (c)).
    /// </summary>
    private static void RefuseTypeDisagreement(
        SchemaDescriptor childSchema,
        TableDescriptor child,
        int childColumn,
        SchemaDescriptor parentSchema,
        TableDescriptor parent,
        int parentColumn,
        string where,
        int ordinal)
    {
        var here = child.Columns[childColumn];
        var there = parent.Columns[parentColumn];
        if (here.Type.Kind != there.Type.Kind)
        {
            throw new CatalogValidationException(
                where,
                $"step {ordinal} joins '{childSchema.Name}.{child.Name}.{here.Name}' "
                + $"({here.Type.Kind}) to '{parentSchema.Name}.{parent.Name}.{there.Name}' "
                + $"({there.Type.Kind}), and the two types disagree. A step joins one key to "
                + "another, so the two columns have to be the same kind of value "
                + "(docs/design/45-typed-tenancy-surface.md §3, D270).");
        }
    }

    /// <summary>A realm or a rule over a bridge key is refused: the mechanism reads it raw (§3).</summary>
    private static void RefuseProtectedBridgeKey(
        TenancyPolicy policy,
        SchemaDescriptor bridgeSchema,
        TableDescriptor bridge,
        string column,
        string where)
    {
        if (policy.TableOf(bridgeSchema.Name, bridge.Name) is not { } declared)
        {
            return;
        }

        foreach (var realm in declared.Realms)
        {
            foreach (var member in realm.Value)
            {
                if (string.Equals(member, column, StringComparison.OrdinalIgnoreCase))
                {
                    throw new CatalogValidationException(
                        where,
                        $"the bridge key '{bridge.Name}.{column}' is in the realm '{realm.Key}'. The "
                        + "mechanism reads the bridge's two key columns raw and discloses neither, "
                        + "exactly as a parent's key is left full under D227, so a realm over one is "
                        + "a contradiction: take it out, or reach the endpoint another way "
                        + "(§3, D265).");
                }
            }
        }

        foreach (var rule in declared.Access)
        {
            if (string.Equals(rule.ColumnName, column, StringComparison.OrdinalIgnoreCase))
            {
                throw new CatalogValidationException(
                    where,
                    $"the bridge key '{bridge.Name}.{column}' is named by an access rule. The "
                    + "mechanism reads the bridge's two key columns raw and discloses neither, "
                    + "exactly as a parent's key is left full under D227, so a rule over one is a "
                    + "contradiction: take it out, or reach the endpoint another way (§3, D265).");
            }
        }
    }

    /// <summary>The table's own dimension of one kind, or null where it holds the kind no other way.</summary>
    private static GrantDimension? DimensionOfKind(TenancyTable declared, string kind)
    {
        foreach (var dimension in declared.Dimensions)
        {
            if (string.Equals(dimension.Kind, kind, StringComparison.Ordinal))
            {
                return dimension;
            }
        }

        return null;
    }

    /// <summary>The table's own <c>Inherited</c> path of one kind, which flattens onto ours (§1).</summary>
    private static Restriction? InheritedOfKind(TenancyTable declared, string kind)
    {
        foreach (var restriction in declared.Restrictions)
        {
            if (restriction.Kind == RestrictionKind.Inherited
                && string.Equals(restriction.TenancyKind, kind, StringComparison.Ordinal))
            {
                return restriction;
            }
        }

        return null;
    }

    /// <summary>
    /// A chain of derived visibility may not return to a table; a diamond is fine (D225, D265 §3).
    /// </summary>
    /// <remarks>
    /// The graph's edges are a <c>Through</c>'s child → parent and a path's target → endpoint. A
    /// bridge is <b>not</b> a node: its own visibility is never consulted, which is what keeps the
    /// marketplace shape acyclic where the bridge is itself entitled through the target.
    /// </remarks>
    private static void RefuseVisibilityCycle(
        TenancyPolicy policy,
        SchemaDescriptor schema,
        TenancyTable declared,
        List<string> visiting,
        string path)
    {
        if (visiting.Contains(declared.Name, StringComparer.OrdinalIgnoreCase))
        {
            visiting.Add(declared.Name);
            throw new CatalogValidationException(
                $"{path}.through",
                $"the chain of derived visibility returns to '{declared.Name}': "
                + string.Join(" -> ", visiting)
                + ". A row's visibility cannot derive from itself, so a cycle is refused where a "
                + "diamond is fine (docs/design/16-entitlements.md §3.13, D225; "
                + "docs/design/38-existential-visibility.md §3, D265).");
        }

        visiting.Add(declared.Name);
        foreach (var restriction in declared.Restrictions)
        {
            var next = "";
            var nextSchema = declared.Schema;
            if (restriction.Kind == RestrictionKind.Through)
            {
                next = restriction.ParentTable;
                if (restriction.ParentSchema.Length > 0)
                {
                    nextSchema = restriction.ParentSchema;
                }

                if (next.Length == 0 && Find(schema, declared.Name) is { } table)
                {
                    next = ForeignKeyOver(table, Ordinal(table, restriction.Column))?.ParentTable ?? "";
                }
            }
            else if (restriction.Kind is RestrictionKind.Inherited or RestrictionKind.Related
                && restriction.Steps.Count > 0)
            {
                // The endpoint is where the last step arrives; the bridge in between is not a node.
                next = restriction.Steps[^1].Table;
                if (restriction.Steps[^1].Schema.Length > 0)
                {
                    nextSchema = restriction.Steps[^1].Schema;
                }
            }

            if (next.Length > 0 && policy.TableOf(nextSchema, next) is { } onward)
            {
                RefuseVisibilityCycle(policy, schema, onward, visiting, path);
            }
        }

        visiting.RemoveAt(visiting.Count - 1);
    }

    private static ForeignKeyDescriptor? ForeignKeyOver(TableDescriptor table, int column)
    {
        foreach (var key in table.ForeignKeys)
        {
            if (key.Columns.Count == 1 && key.ParentColumns.Count == 1 && key.Columns[0] == column)
            {
                return key;
            }
        }

        return null;
    }

    private static bool IsUniqueKey(TableDescriptor table, int column)
    {
        foreach (var key in table.UniqueKeys)
        {
            if (key.Columns.Count == 1 && key.Columns[0] == column)
            {
                return true;
            }
        }

        return false;
    }

    // ------------------------------------------------------------------ paths

    /// <summary>
    /// Resolves a dimension's column, following a dotted path over declared foreign keys.
    /// </summary>
    /// <remarks>
    /// Each segment but the last names a foreign key of the table it starts from — by the key's own
    /// name, by its single child column read as <c>&lt;segment&gt;_id</c>, or by the parent table's
    /// name — and the last names a tenancy dimension of the table it arrives at. What comes back is
    /// the column that dimension reads <em>there</em>, which the entitled table must also carry:
    /// §2 lets a compiled predicate hold scalars and lists and nothing else, so a path that could
    /// only be answered by joining the parent is refused rather than compiled into a round trip.
    /// </remarks>
    private static ResolvedDimension Resolve(
        TenancyPolicy policy,
        SchemaDescriptor schema,
        TableDescriptor table,
        TenancyTable declared,
        GrantDimension dimension,
        string path)
    {
        var column = ResolveColumn(policy, schema, table, dimension.Column, $"{path}.{dimension.Kind}");
        if (!dimension.IsSubject)
        {
            return new ResolvedDimension { Declared = dimension, Column = column.Name, Type = column.Type };
        }

        if (dimension.WithinKinds.Count == 0)
        {
            throw new CatalogValidationException(
                $"{path}.{dimension.Kind}",
                "a subject dimension must name the tenancy dimension it is confined within; a grant "
                + "that reaches every tenancy says so with Tenancy.Anywhere at the grant");
        }

        // The first declared confining kind this table holds. A kind the table does not hold is not
        // an error since D266 — a confined grant simply does not reach a table that cannot resolve
        // every kind confining it (§3) — but a subject dimension none of whose declared confining
        // kinds resolves here is a declaration that could never confine anything, and is refused
        // where it was written.
        GrantDimension? within = null;
        foreach (var candidate in dimension.WithinKinds)
        {
            foreach (var other in declared.Dimensions)
            {
                if (within is null
                    && !other.IsSubject
                    && string.Equals(other.Kind, candidate, StringComparison.Ordinal))
                {
                    within = other;
                }
            }
        }

        if (within is null)
        {
            throw new CatalogValidationException(
                $"{path}.{dimension.Kind}",
                $"the subject dimension is confined within {Named(dimension.WithinKinds)}, "
                + (dimension.WithinKinds.Count == 1 ? "which is not a " : "none of which is a ")
                + "tenancy dimension of this table");
        }

        // The confining dimension's own column has to resolve on this table too, since a conjoined
        // term reads both columns off the one row (D266 §3). What the term reads is the sibling
        // dimension itself, which the groups of §4 pick up; this is the check that it is there.
        _ = ResolveColumn(policy, schema, table, within.Column, $"{path}.{within.Kind}");
        return new ResolvedDimension
        {
            Declared = dimension,
            Column = column.Name,
            Type = column.Type,
        };
    }

    private static ColumnDescriptor ResolveColumn(
        TenancyPolicy policy,
        SchemaDescriptor schema,
        TableDescriptor table,
        string columnOrPath,
        string path)
    {
        var segments = columnOrPath.Split('.');
        if (segments.Length == 1)
        {
            return Column(table, columnOrPath)
                ?? throw new CatalogValidationException(
                    path, $"'{columnOrPath}' is not a column of '{table.Name}'");
        }

        var at = table;
        var visited = new List<string> { table.Name };
        for (var i = 0; i < segments.Length - 1; i++)
        {
            at = Follow(schema, at, segments[i], path);
            if (visited.Contains(at.Name, StringComparer.OrdinalIgnoreCase))
            {
                throw new CatalogValidationException(
                    path,
                    $"the path '{columnOrPath}' returns to '{at.Name}', which it already visited: a "
                    + "foreign-key path may not cycle");
            }

            visited.Add(at.Name);
        }

        var last = segments[^1];
        var target = policy.TableOf(schema.Name, at.Name)
            ?? throw new CatalogValidationException(
                path,
                $"the path '{columnOrPath}' arrives at '{at.Name}', which the policy does not "
                + "declare, so it has no dimension to end at");

        GrantDimension? end = null;
        foreach (var other in target.Dimensions)
        {
            if (!other.IsSubject && string.Equals(other.Kind, last, StringComparison.Ordinal))
            {
                end = other;
            }
        }

        if (end is null)
        {
            throw new CatalogValidationException(
                path,
                $"the path '{columnOrPath}' ends at '{last}', which is not a tenancy dimension of "
                + $"'{at.Name}'");
        }

        var there = ResolveColumn(policy, schema, at, end.Column, path);

        // §2: only scalars and lists ever reach a compiled predicate, so the value has to be on this
        // table's own row. The association is what was proved above; the column is what carries it.
        var here = Column(table, there.Name);
        if (here is null || here.Type.Kind != there.Type.Kind)
        {
            throw new CatalogValidationException(
                path,
                $"the path '{columnOrPath}' resolves to '{at.Name}.{there.Name}' ({there.Type.Kind}), "
                + $"and '{table.Name}' carries no column '{there.Name}' of that kind. A compiled "
                + "predicate may read this table's own columns, a context scalar and a context list "
                + "and nothing else (docs/design/16-entitlements.md §2), so the tenancy has to be on "
                + "the row: carry the column, or declare the dimension on the column that does.");
        }

        return here;
    }

    /// <summary>The table one segment of a path leads to, by the ways a segment may name a key.</summary>
    private static TableDescriptor Follow(
        SchemaDescriptor schema, TableDescriptor from, string segment, string path)
    {
        var matches = new List<TableDescriptor>();
        foreach (var key in from.ForeignKeys)
        {
            var parent = Find(schema, key.ParentTable);
            if (parent is null)
            {
                continue;
            }

            var named = string.Equals(key.Name, segment, StringComparison.OrdinalIgnoreCase);
            var byColumn = key.Columns.Count == 1
                && string.Equals(
                    from.Columns[key.Columns[0]].Name, segment + "_id", StringComparison.OrdinalIgnoreCase);
            var byTable = string.Equals(parent.Name, segment, StringComparison.OrdinalIgnoreCase)
                || string.Equals(parent.Name, segment + "s", StringComparison.OrdinalIgnoreCase);
            if (named || byColumn || byTable)
            {
                matches.Add(parent);
            }
        }

        if (matches.Count == 1)
        {
            return matches[0];
        }

        throw new CatalogValidationException(
            path,
            matches.Count == 0
                ? $"'{segment}' names no declared foreign key of '{from.Name}'. A path resolves "
                  + "through foreign keys, so the association has to be declared before the policy "
                  + "can rely on it."
                : $"'{segment}' names {matches.Count} declared foreign keys of '{from.Name}'; name "
                  + "the key itself so the path is unambiguous.");
    }

    // ------------------------------------------------------------------ the descriptor

    private static TableEntitlementDescriptor Descriptor(
        TableDescriptor table,
        TenancyTable declared,
        TableModel model,
        IReadOnlyList<string> roles,
        string schema,
        string path)
    {
        var protectedColumns = ProtectedColumns(table, declared, model, roles, path);
        var columns = new List<ColumnEntitlementDescriptor>();
        foreach (var protectedColumn in protectedColumns)
        {
            columns.Add(ColumnRules(table, declared, model, protectedColumn, protectedColumns, path));
        }

        var through = new List<ParentVisibilityDescriptor>(model.Through.Count);
        foreach (var parent in model.Through)
        {
            through.Add(new ParentVisibilityDescriptor
            {
                Column = parent.ColumnOrdinal,
                ParentSchema =
                    string.Equals(parent.ParentSchema, schema, StringComparison.OrdinalIgnoreCase)
                        ? ""
                        : parent.ParentSchema,
                ParentTable = parent.ParentTable,
                ParentColumn = parent.ParentKeyOrdinal,
            });
        }

        var paths = new List<InheritedVisibilityDescriptor>(model.Paths.Count);
        foreach (var declaredPath in model.Paths)
        {
            var steps = new List<VisibilityStepDescriptor>(declaredPath.Steps.Count);
            foreach (var step in declaredPath.Steps)
            {
                steps.Add(new VisibilityStepDescriptor
                {
                    // Empty where the step stays in the entitled table's own schema, which is every
                    // step of every policy that does not cross a source: the wire is unchanged.
                    Schema = string.Equals(step.ToSchema, schema, StringComparison.OrdinalIgnoreCase)
                        ? ""
                        : step.ToSchema,
                    Table = step.ToTable,
                    FromColumn = step.FromOrdinal,
                    ToColumn = step.ToOrdinal,
                    Direction = step.Down ? StepDirection.ToParent : StepDirection.ToChild,
                });
            }

            paths.Add(new InheritedVisibilityDescriptor
            {
                Kind = declaredPath.Kind,
                Steps = steps,
                EndpointPredicate = EndpointPredicate(model, declaredPath),
                EndpointSchema =
                    string.Equals(declaredPath.EndpointSchema, schema, StringComparison.OrdinalIgnoreCase)
                        ? ""
                        : declaredPath.EndpointSchema,
                EndpointTable = declaredPath.EndpointTable,
            });
        }

        return new TableEntitlementDescriptor
        {
            RowPredicate = RowPredicate(declared, model),
            Through = through,
            Inherited = paths,
            Columns = columns,
        };
    }

    /// <summary>
    /// Which of the endpoint's rows this principal holds the kind in (D265 §2): the same membership
    /// test a <c>Direct</c> dimension compiles to, over the endpoint's own row, OR-ed across the
    /// roles this table admits — so the far end of the chain folds to one boolean the pass can read.
    /// </summary>
    private static string EndpointPredicate(TableModel model, ResolvedPath declaredPath)
    {
        var scopes = new List<string>();
        foreach (var role in model.AdmittedRoles)
        {
            var parts = new List<string>();
            Membership(
                parts,
                declaredPath.EndpointDimension,
                role,
                "",
                declaredPath.EndpointDimensions,
                model.Confinable,
                model.Path);
            parts.Add($"@ctx.global_{role}");
            scopes.Add(parts.Count == 1 ? parts[0] : "(" + string.Join(" OR ", parts) + ")");
        }

        return scopes.Count switch
        {
            0 => "FALSE",
            1 => scopes[0],
            _ => "(" + string.Join(" OR ", scopes) + ")",
        };
    }

    /// <summary>
    /// The columns that get rules at all: every column in a realm, and every column an access rule
    /// names. A column in neither takes the table's default, which is full disclosure — that is what
    /// keeps a policy about the columns that matter rather than about the whole row.
    /// </summary>
    private static List<string> ProtectedColumns(
        TableDescriptor table,
        TenancyTable declared,
        TableModel model,
        IReadOnlyList<string> roles,
        string path)
    {
        var names = new List<string>();
        foreach (var column in table.Columns)
        {
            var realm = RealmOf(declared, column.Name);
            var named = false;
            foreach (var rule in declared.Access)
            {
                if (rule.ColumnName.Length > 0
                    && string.Equals(rule.ColumnName, column.Name, StringComparison.OrdinalIgnoreCase))
                {
                    named = true;
                }
            }

            if (realm.Length > 0 || named)
            {
                names.Add(column.Name);
            }
        }

        foreach (var (realm, members) in declared.Realms)
        {
            foreach (var member in members)
            {
                if (Column(table, member) is null)
                {
                    throw new CatalogValidationException(
                        path, $"realm '{realm}' names '{member}', which is not a column of the table");
                }
            }
        }

        for (var r = 0; r < declared.Access.Count; r++)
        {
            var rule = declared.Access[r];

            // The two explicit grantees, checked where the rule was written (D269 (c)). Neither is
            // a role a grant can be held in, so neither is looked up among the policy's own.
            if (rule.Roles.Contains(Roles.Visible) && rule.Roles.Count > 1)
            {
                throw new CatalogValidationException(
                    $"{path}.access[{r}]",
                    $"the access rule for {About(rule)} on '{declared.Name}' names Roles.Visible "
                    + "beside other grantees. Roles.Visible is every way into the row — every "
                    + "declared role, the resource owner and the global grant — so a grantee beside "
                    + "it says nothing more and would only make the condition something other than "
                    + "the row predicate (docs/design/43-verdicts-along-paths.md, D269 (c)).");
            }

            if (rule.Roles.Contains(Roles.Visible)
                && RowPredicate(declared, model).Length == 0)
            {
                throw new CatalogValidationException(
                    $"{path}.access[{r}]",
                    $"the access rule for {About(rule)} on '{declared.Name}' names Roles.Visible, "
                    + "and this table restricts no row, so there is no predicate to write as the "
                    + "condition. Every row is visible here already; write the verdict for the "
                    + "roles it is meant for (docs/design/43-verdicts-along-paths.md, D269 (c)).");
            }

            if (rule.Roles.Contains(Roles.Owner)
                && declared.ResourceOwnerColumn.Length == 0)
            {
                throw new CatalogValidationException(
                    $"{path}.access[{r}]",
                    $"the access rule for {About(rule)} on '{declared.Name}' names Roles.Owner, and "
                    + "this table declares no ResourceOwner, so there is no column to compare the "
                    + "caller with. Declare one with `ResourceOwner(column)`, or drop the grantee "
                    + "(docs/design/43-verdicts-along-paths.md, D269 (c)).");
            }

            // A rule speaks for roles, and a role the policy does not list is a role no grant can
            // ever name — so the rule could never match and the host meant something else. It is
            // the check the visibility rules already get, and it names where to look.
            foreach (var role in rule.Roles)
            {
                if (role.IsMarker)
                {
                    continue;
                }

                if (!roles.Contains(role.Name, StringComparer.Ordinal))
                {
                    throw new CatalogValidationException(
                        $"{path}.access[{r}]",
                        $"the access rule for {About(rule)} on '{declared.Name}' speaks for role "
                        + $"'{role.Name}', which is not one of the roles this policy declares "
                        + $"({string.Join(", ", roles)}). A grant can never name it, so the rule "
                        + "could never match; declare it with Role(name) on this policy, or correct "
                        + "the rule.");
                }
            }

            // Every declared mask is checked here, whether or not a rule that carries one ever
            // wins for a principal: a retired `{column}`, a two-parameter template or a parameter
            // that shadows a column is a mistake in the declaration and is refused where it was
            // written (D219).
            MaskTemplate.Check(rule.MaskText, table, path);

            // A placeholder stands in for a value the rule withholds, so it means nothing beside a
            // verdict that discloses one (D224). Refused here, where it was written, rather than at
            // registration, where the message would be about a descriptor the host never wrote.
            if (rule.PlaceholderText.Length > 0 && rule.Grants != Verdict.None)
            {
                throw new CatalogValidationException(
                    path,
                    $"an access rule for {About(rule)} grants {rule.Grants} and states a "
                    + "placeholder. A placeholder is what stands in for a value the rule withholds, "
                    + "so it is meaningful only with Verdict.None (docs/design/16-entitlements.md "
                    + "§5, D224). A rule that means to show something derived from the value grants "
                    + "Verdict.Mask and states a Mask.");
            }

            // The shapes a test permits, checked where they were written (D261). What the column's
            // type admits is layer A's own check: the compiler is handed the schema and the
            // validator reads the same declaration, so saying it twice here would only duplicate the
            // message the host already gets.
            if (rule.Grants == Verdict.Test && rule.Tests.Count == 0)
            {
                throw new CatalogValidationException(
                    path,
                    $"an access rule for {About(rule)} grants Verdict.Test and names no comparison "
                    + "shape, so it discloses neither the value nor any comparison of it "
                    + "(docs/design/36-test-verdict.md §1). Name the shapes — Test.Equals, "
                    + "Test.NotEquals, Test.In — or grant Verdict.None and mean it.");
            }

            if (rule.Tests.Count > 0
                && rule.Grants is not (Verdict.Test or Verdict.AggregateOnly))
            {
                throw new CatalogValidationException(
                    path,
                    $"an access rule for {About(rule)} grants {rule.Grants} and names comparison "
                    + "shapes. A shape is what a principal may test without reading the value, so it "
                    + "is meaningful with Verdict.Test, and on a Verdict.AggregateOnly rule as the "
                    + "FILTER of a permitted aggregate (D261) — under any other verdict the value is "
                    + "either disclosed outright or withheld entirely.");
            }

            if (rule.Column is { } named)
            {
                declared.Declaration.Policy.Require(named, declared.Declaration, $"{path}.access[{r}]");
                if (Column(table, named.Name) is null)
                {
                    throw new CatalogValidationException(
                        path,
                        $"an access rule names column '{named.Name}', which the table does not hold");
                }
            }

            if (rule.Realm is { } about)
            {
                declared.Declaration.Policy.Require(about, $"{path}.access[{r}]");
                if (!declared.Realms.ContainsKey(about.Name))
                {
                    throw new CatalogValidationException(
                        path,
                        $"an access rule names realm '{about.Name}', which the table does not declare");
                }
            }

            if (rule.RealmName.Length > 0 && rule.ColumnName.Length > 0)
            {
                throw new CatalogValidationException(
                    path,
                    $"an access rule names both realm '{rule.RealmName}' and column "
                    + $"'{rule.ColumnName}'; a rule speaks about one or the other");
            }
        }

        return names;
    }

    private static string RealmOf(TenancyTable declared, string column)
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

    // ------------------------------------------------------------------ the row predicate

    /// <summary>
    /// The table's row predicate: its restrictions, OR-ed in declaration order (D215). An
    /// unrestricted table has none at all, so every row is visible and the descriptor carries an
    /// empty predicate — which is what the core reads as "no filter".
    /// </summary>
    private static string RowPredicate(TenancyTable declared, TableModel model)
    {
        if (!declared.IsRestricted)
        {
            return "";
        }

        var predicate = new StringBuilder();
        var dimensionsDone = false;
        foreach (var restriction in declared.Restrictions)
        {
            switch (restriction.Kind)
            {
                case RestrictionKind.Dimension:
                    // Every dimension at once, at the first one's position: a role's scope is one
                    // membership test per dimension, and the scope test applies over all of them.
                    if (dimensionsDone)
                    {
                        continue;
                    }

                    dimensionsDone = true;
                    var scopes = new List<string>();
                    foreach (var role in model.AdmittedRoles)
                    {
                        var scope = Scope(model, role, derived: false);
                        if (scope.Length > 0)
                        {
                            scopes.Add(scope);
                        }
                    }

                    if (scopes.Count == 0)
                    {
                        continue;
                    }

                    var term = "(" + string.Join(" OR ", scopes) + ")";
                    if (model.ScopeTest.Length > 0)
                    {
                        term = "(" + term + " AND " + model.ScopeTest + ")";
                    }

                    Or(predicate, term);
                    break;

                case RestrictionKind.Predicate:
                    Or(predicate, restriction.Predicate);
                    break;

                case RestrictionKind.Through:
                    // Nothing: the relationship is on the descriptor and the planner ORs its join
                    // in (§3.13, D226). Writing it as text would be writing a sub-query, which §2's
                    // vocabulary does not admit and D225 answers with `through`.
                    break;

                case RestrictionKind.Inherited:
                case RestrictionKind.Related:
                    // The same, for a declared path (D265 §4): the steps and the endpoint predicate
                    // are on the descriptor, and the key set's marker is the planner's to OR in. An
                    // `EXISTS` written here would be the sub-query §2 refuses.
                    break;

                default:
                    Or(predicate, $"{restriction.Column} = @ctx.user");
                    break;
            }
        }

        // The global escape belongs to the grant-driven routes. A table whose restrictions are all
        // host predicates is the host's own answer, kept verbatim (D215), and adding a term to it
        // would be the compiler rewriting text it was told not to read.
        // The global escape belongs to this table's own grant-driven routes. A `Through` is not one:
        // the parent's own predicate carries the escape, and a global grant folds *that* to TRUE —
        // which is what lets the planner elide the child's join altogether rather than filter it
        // away here (§3.13, D226).
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
            Or(predicate, "@ctx.global");
        }

        return predicate.ToString();
    }

    /// <summary>
    /// What "this row is in a tenancy where the principal holds <paramref name="role"/>" compiles to:
    /// a membership test per dimension, plus the boolean a global grant in that role binds.
    /// </summary>
    private static string Scope(TableModel model, string role, bool derived)
    {
        var parts = new List<string>();
        foreach (var dimension in model.Dimensions)
        {
            Membership(parts, dimension, role, "", model.Dimensions, model.Confinable, model.Path);
        }

        if (!derived)
        {
            // The row predicate is over this table's own row and is converted with the other tables
            // out of scope (§3.13's registration check, D265 §3), so a membership over a parent's or
            // an endpoint's column belongs to the rules and never to it.
            parts.Add($"@ctx.global_{role}");
            return parts.Count == 1 ? parts[0] : "(" + string.Join(" OR ", parts) + ")";
        }

        // A table entitled through a parent has no tenancy of its own: the roles held in the row's
        // tenancy are the roles held in the *parent's*, written over `<parent_table>.<column>` and
        // evaluated on the parent's side of the join, once per parent row (§3.13, D228).
        foreach (var parent in model.Through)
        {
            foreach (var dimension in parent.ParentDimensions)
            {
                Membership(
                    parts,
                    dimension,
                    role,
                    parent.ParentTable + ".",
                    parent.ParentDimensions,
                    model.Confinable,
                    model.Path);
            }
        }

        // And, for a declared path's perspective, the roles held at the *endpoint*, written over
        // `<endpoint_table>.<column>` and evaluated there, once per endpoint row (D265 §2, §4). The
        // `MIN` over the rows reaching one target key is what makes "first match wins across the
        // rows that reach it" of them.
        foreach (var declaredPath in model.Paths)
        {
            Membership(
                parts,
                declaredPath.EndpointDimension,
                role,
                declaredPath.EndpointTable + ".",
                declaredPath.EndpointDimensions,
                model.Confinable,
                model.Path);
        }

        parts.Add($"@ctx.global_{role}");
        return parts.Count == 1 ? parts[0] : "(" + string.Join(" OR ", parts) + ")";
    }

    /// <summary>
    /// One dimension's membership tests, over this table's row or over a parent's or an endpoint's:
    /// one term per group, the confining columns read off the very same row (D266 §3, §4).
    /// </summary>
    private static void Membership(
        List<string> parts,
        ResolvedDimension dimension,
        string role,
        string qualifier,
        IReadOnlyList<ResolvedDimension> siblings,
        IReadOnlyDictionary<string, IReadOnlyList<string>> confinable,
        string path)
    {
        foreach (var group in Groups(dimension, role, siblings, confinable, path))
        {
            if (group.Confining.Count == 0)
            {
                parts.Add($"{qualifier}{dimension.Column} IN (@ctx.{group.Name})");
                continue;
            }

            var columns = string.Join(
                ", ",
                new[] { dimension.Column }
                    .Concat(group.Confining.Select(c => c.Column))
                    .Select(column => qualifier + column));
            parts.Add($"({columns}) IN (@ctx.{group.Name})");
        }
    }

    private static void Or(StringBuilder text, string term)
    {
        if (text.Length > 0)
        {
            text.Append(" OR ");
        }

        text.Append(term);
    }

    // ------------------------------------------------------------------ the column rules

    /// <summary>
    /// One rule of a column, before it is written as SQL: what it discloses, to which roles, and
    /// whether it is the created-by fail-safe rather than a role's.
    /// </summary>
    /// <remarks>
    /// The <em>plan</em> both the emitter and the reconciliation read. Deriving the roles a second
    /// time from the emitted text would be reading the compiler's output as its own input, and the
    /// two would drift apart the first time the text changed.
    /// </remarks>
    internal sealed class ColumnRule
    {
        internal required Verdict Level { get; init; }

        internal required IReadOnlyList<Role> Roles { get; init; }

        internal required string Mask { get; init; }

        /// <summary>What stands in for the value under <c>None</c>, or empty (D224).</summary>
        internal string Placeholder { get; init; } = "";

        /// <summary>The rule's own further condition on the row, or empty (D221).</summary>
        internal string When { get; init; } = "";

        /// <summary>The population aggregates this rule permits, under <c>AggregateOnly</c>.</summary>
        internal IReadOnlyList<string> Aggregates { get; init; } = [];

        /// <summary>The group-size floor those aggregates are guarded by (D211).</summary>
        internal int MinGroupSize { get; init; }

        /// <summary>The comparisons this rule permits over the column without disclosing it (D261).</summary>
        internal IReadOnlyList<Test> Tests { get; init; } = [];

        internal bool IsCreator { get; init; }
    }

    /// <summary>
    /// One protected column's rules, in the order layer A must evaluate them so that first-match-wins
    /// gives the same verdict as the host's own ordinal walk (D222).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The host's list is the priority: rules are read in order, a matching rule sets the tentative
    /// verdict, and <c>StopOnMatch</c> ends the reading. So the answer is the <em>first</em> matching
    /// stop-rule if there is one — every continue-rule before it only set a verdict the stop-rule
    /// then replaced, and every rule after it was never read — and otherwise the <em>last</em>
    /// matching continue-rule, and otherwise nothing at all.
    /// </para>
    /// <para>
    /// Layer A knows only first-match-wins, and that is enough: emit the stop-rules in host order,
    /// then the continue-rules in <b>reverse</b> host order. A matching stop-rule is then met before
    /// any continue-rule and the earliest of them first, which is the first clause; with no stop-rule
    /// matching, the reversed tail is met in order and its first match is the last matching
    /// continue-rule, which is the second. Nothing matching leaves the <c>otherwise</c>, which is
    /// always <c>NONE</c> (D208, D216).
    /// </para>
    /// </remarks>
    internal static IReadOnlyList<ColumnRule> ColumnPlan(
        TenancyTable declared, TableModel model, string column)
    {
        var realm = RealmOf(declared, column);
        var plan = new List<ColumnRule>();

        // The resource-owner fail-safe, before every rule: the principal owns the values (D206,
        // D215). It is not one of the host's rules and takes no position among them.
        if (declared.ResourceOwnerColumn.Length > 0
            && declared.ResourceOwnerSees == ResourceOwnerSees.Full)
        {
            plan.Add(new ColumnRule
            {
                Level = Verdict.Full,
                Roles = [],
                Mask = "",
                IsCreator = true,
            });
        }

        var stops = new List<ColumnRule>();
        var continues = new List<ColumnRule>();
        foreach (var rule in declared.Access)
        {
            if (!About(rule, column, realm))
            {
                continue;
            }

            // A role this table does not admit holds no grant on its rows at all (§5's visibility
            // rules), so a rule that speaks only for such roles can never match and is not emitted.
            // A marker is not a role and is not held in a tenancy, so no visibility rule speaks to
            // it and it always stands (D269 (c)).
            var roles = new List<Role>();
            foreach (var role in rule.Roles)
            {
                if ((role.IsMarker || model.AdmittedRoles.Contains(role.Name, StringComparer.Ordinal))
                    && !roles.Contains(role))
                {
                    roles.Add(role);
                }
            }

            if (roles.Count == 0)
            {
                continue;
            }

            (rule.StopOnMatch ? stops : continues).Add(new ColumnRule
            {
                Level = rule.Grants,
                Roles = roles,
                Mask = rule.MaskText,
                Placeholder = rule.PlaceholderText,
                When = rule.WhenText,
                Aggregates = rule.AggregateNames,
                MinGroupSize = rule.MinGroupSize,
                Tests = rule.Tests,
            });
        }

        plan.AddRange(stops);
        for (var i = continues.Count - 1; i >= 0; i--)
        {
            plan.Add(continues[i]);
        }

        return plan;
    }

    /// <summary>
    /// Whether a rule speaks about this column: by naming it, by naming its realm, or by naming
    /// neither — which is a rule about <em>every</em> protected column of the table, the ordinary
    /// form of what used to be a role's declared default (D222).
    /// </summary>
    /// <summary>What a rule is about, as a message names it: a column, a realm, or every column.</summary>
    private static string About(AccessRule rule) =>
        rule.ColumnName.Length > 0 ? $"column '{rule.ColumnName}'"
            : rule.RealmName.Length > 0 ? $"realm '{rule.RealmName}'"
            : "every protected column";

    private static bool About(AccessRule rule, string column, string realm) =>
        rule.ColumnName.Length > 0
            ? string.Equals(rule.ColumnName, column, StringComparison.OrdinalIgnoreCase)
            : rule.RealmName.Length == 0
                || (realm.Length > 0
                    && string.Equals(rule.RealmName, realm, StringComparison.OrdinalIgnoreCase));

    private static ColumnEntitlementDescriptor ColumnRules(
        TableDescriptor table,
        TenancyTable declared,
        TableModel model,
        string column,
        IReadOnlyList<string> protectedColumns,
        string path)
    {
        var descriptor = Column(table, column)!;
        var realm = RealmOf(declared, column);
        var rules = new List<DisclosureRule>();
        var aggregates = new List<string>();
        var floor = 0;
        var masked = false;

        foreach (var planned in ColumnPlan(declared, model, column))
        {
            if (planned.IsCreator)
            {
                rules.Add(new DisclosureRule
                {
                    When = $"{declared.ResourceOwnerColumn} = @ctx.user",
                    Then = Disclosure.Full,
                });
                continue;
            }

            if (planned.Level == Verdict.AggregateOnly)
            {
                foreach (var name in planned.Aggregates)
                {
                    if (!aggregates.Contains(name, StringComparer.OrdinalIgnoreCase))
                    {
                        aggregates.Add(name);
                    }
                }

                floor = Math.Max(floor, planned.MinGroupSize);
                if (planned.Aggregates.Count == 0)
                {
                    throw new CatalogValidationException(
                        path,
                        $"column '{column}' is AggregateOnly for some role and the rule lists no "
                        + "population aggregate, so nothing could ever read it. List the aggregates "
                        + "the role may use.");
                }
            }

            var condition = Any(declared, model, planned.Roles);

            // The rule's own condition, AND-ed onto the roles it speaks for (D221). It is the
            // author's own SQL and is kept verbatim, as a host predicate is.
            if (planned.When.Length > 0)
            {
                condition = $"{Wrapped(condition)} AND ({planned.When})";
            }

            masked |= planned.Level == Verdict.Mask;
            rules.Add(new DisclosureRule
            {
                When = condition,
                Then = planned.Level switch
                {
                    Verdict.Full => Disclosure.Full,
                    Verdict.Mask => Disclosure.Masked,
                    Verdict.AggregateOnly => Disclosure.AggregateOnly,
                    Verdict.Test => Disclosure.Test,
                    _ => Disclosure.None,
                },
                Mask = planned.Level == Verdict.Mask
                    ? Mask(planned.Mask, column, descriptor, table, protectedColumns, path)
                    : "",
                // The rule's own placeholder travels only under None, which is the one verdict it
                // means anything under; the check that refused it elsewhere already ran (D224).
                Placeholder = planned.Level == Verdict.None ? planned.Placeholder : "",
                // The comparison shapes travel under the two verdicts that read them (D261): Test,
                // which is nothing but them, and AggregateOnly, where they are what a permitted
                // aggregate's FILTER may say. The check that refused them elsewhere already ran.
                Tests = planned.Level is Verdict.Test or Verdict.AggregateOnly
                    ? Shapes(planned.Tests)
                    : [],
            });
        }

        return new ColumnEntitlementDescriptor
        {
            Column = Ordinal(table, column),
            Rules = rules,
            Otherwise = Disclosure.None,
            Mask = masked ? DefaultMask(descriptor) : "",
            AggregateOnlyFunctions = aggregates,
            MinGroupSize = floor,
        };
    }

    /// <summary>
    /// Layer B's comparison shapes as layer A spells them (D261), deduplicated and in the order the
    /// host wrote them — the order is not meaning here, but a stable one keeps the descriptor hash
    /// stable for a declaration that did not change.
    /// </summary>
    private static IReadOnlyList<TestShape> Shapes(IReadOnlyList<Test> tests)
    {
        if (tests.Count == 0)
        {
            return [];
        }

        var shapes = new List<TestShape>(tests.Count);
        foreach (var test in tests)
        {
            var shape = test switch
            {
                Test.Equals => TestShape.Equals,
                Test.NotEquals => TestShape.NotEquals,
                Test.In => TestShape.In,
                _ => TestShape.Unspecified,
            };
            if (!shapes.Contains(shape))
            {
                shapes.Add(shape);
            }
        }

        return shapes;
    }

    /// <summary>Parenthesised, unless the text is already one parenthesised whole.</summary>
    private static string Wrapped(string text) => Wraps(text) ? text : "(" + text + ")";

    private static bool Wraps(string text)
    {
        if (text.Length < 2 || text[0] != '(' || text[^1] != ')')
        {
            return false;
        }

        var depth = 0;
        for (var i = 0; i < text.Length; i++)
        {
            depth += text[i] == '(' ? 1 : text[i] == ')' ? -1 : 0;
            if (depth == 0 && i < text.Length - 1)
            {
                return false;
            }
        }

        return depth == 0;
    }

    /// <summary>
    /// A rule's condition: what "this principal is one of the grantees this rule speaks for" comes
    /// to, OR-ed across them (D222, D269 (c)).
    /// </summary>
    /// <remarks>
    /// A role is its scope — the roles held in the row's tenancy, or in the parent's or the
    /// endpoint's for a derived perspective. <see cref="Roles.Owner"/> is the resource owner's own
    /// disjunct, which is a way into the row that no role's scope covers. And
    /// <see cref="Roles.Visible"/> is the table's row predicate, <b>textually</b>: it stands alone
    /// (the check above refuses a grantee beside it), so the condition is the very text
    /// <c>Filter_R</c> folds, which is what lets §3.3's <c>holds</c> read the column as a constant.
    /// </remarks>
    private static string Any(
        TenancyTable declared, TableModel model, IReadOnlyList<Role> roles)
    {
        if (roles.Contains(Roles.Visible))
        {
            return RowPredicate(declared, model);
        }

        var parts = new List<string>(roles.Count);
        foreach (var role in roles)
        {
            parts.Add(
                role == Roles.Owner
                    ? $"{declared.ResourceOwnerColumn} = @ctx.user"
                    : Scope(model, role.Name, derived: true));
        }

        return string.Join(" OR ", parts);
    }

    /// <summary>
    /// The mask this rule uses: its own — a lambda template instantiated for this column, or the
    /// text verbatim — or the type's default (D219).
    /// </summary>
    private static string Mask(
        string declared,
        string column,
        ColumnDescriptor descriptor,
        TableDescriptor table,
        IReadOnlyList<string> protectedColumns,
        string path)
    {
        if (declared.Length == 0)
        {
            return DefaultMask(descriptor);
        }

        var instantiated = MaskTemplate.Instantiate(declared, column, table, path);
        RefuseProtectedReferences(instantiated, column, protectedColumns, path);
        return instantiated;
    }

    /// <summary>
    /// A mask may read its own column, any <em>unprotected</em> column of the row and the context;
    /// a reference to another protected column is refused, naming both (D220).
    /// </summary>
    /// <remarks>
    /// The sanitiser is evaluated over the leaf's <b>raw</b> row — that is what makes masking
    /// possible at all — so a mask that read another protected column would embed that column's raw
    /// value in this column's disclosed one, and a principal who may see neither would be handed
    /// one. A rule <em>condition</em> is a different matter and may read anything: what it discloses
    /// is one bit, which the policy author chose (§1).
    /// </remarks>
    private static void RefuseProtectedReferences(
        string mask, string column, IReadOnlyList<string> protectedColumns, string path)
    {
        foreach (var identifier in MaskTemplate.Identifiers(mask))
        {
            if (string.Equals(identifier, column, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var other in protectedColumns)
            {
                if (!string.Equals(identifier, other, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                throw new CatalogValidationException(
                    path,
                    $"the mask of '{column}' reads '{other}', which is itself a protected column "
                    + "(it is in a realm, or a rule names it). A mask is evaluated over the raw "
                    + $"row, so this one would disclose '{other}' inside '{column}' to a principal "
                    + $"who may see neither (docs/design/16-entitlements.md §5, D220). A mask may "
                    + "read its own column, an unprotected column and the context; a rule condition "
                    + "may read anything.");
            }
        }
    }

    /// <summary>
    /// The per-type default (§5): <c>'********'</c> for a string, and NULL for anything else — a
    /// value of the column's own type, so every principal gets the same row shape (D161).
    /// </summary>
    private static string DefaultMask(ColumnDescriptor column) =>
        column.Type.Kind == TypeKind.String
            ? "'********'"
            : $"CAST(NULL AS {SqlType(column.Type)})";

    private static string SqlType(ChalkType type) => type.Kind switch
    {
        TypeKind.Bool => "BOOLEAN",
        TypeKind.I8 => "TINYINT",
        TypeKind.I16 => "SMALLINT",
        TypeKind.I32 => "INTEGER",
        TypeKind.I64 => "BIGINT",
        TypeKind.Fp32 => "REAL",
        TypeKind.Fp64 => "DOUBLE",
        TypeKind.Decimal => string.Create(
            CultureInfo.InvariantCulture,
            $"DECIMAL({type.Precision}, {type.Scale})"),
        TypeKind.Binary => "VARBINARY",
        TypeKind.Date => "DATE",
        TypeKind.Time => "TIME",
        TypeKind.Timestamp => "TIMESTAMP",
        TypeKind.TimestampTz => "TIMESTAMP WITH LOCAL TIME ZONE",
        _ => "VARCHAR",
    };

    // ------------------------------------------------------------------ small helpers

    /// <summary>A table's key in the compiled maps: its schema and its name, as a statement writes it.</summary>
    internal static string Qualified(string schema, string table) =>
        schema.Length == 0 ? table : schema + "." + table;

    internal static TableDescriptor? Find(SchemaDescriptor schema, string name)
    {
        foreach (var table in schema.Tables)
        {
            if (string.Equals(table.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return table;
            }
        }

        return null;
    }

    internal static ColumnDescriptor? Column(TableDescriptor table, string name)
    {
        foreach (var column in table.Columns)
        {
            if (string.Equals(column.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return column;
            }
        }

        return null;
    }

    internal static int Ordinal(TableDescriptor table, string name)
    {
        for (var i = 0; i < table.Columns.Count; i++)
        {
            if (string.Equals(table.Columns[i].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static IReadOnlyList<string> Distinct(IReadOnlyList<string> values, string what, string path)
    {
        var seen = new List<string>(values.Count);
        foreach (var value in values)
        {
            if (value.Length == 0)
            {
                throw new CatalogValidationException(path, $"a {what} is empty");
            }

            if (seen.Contains(value, StringComparer.Ordinal))
            {
                throw new CatalogValidationException(path, $"{what} '{value}' is listed twice");
            }

            seen.Add(value);
        }

        return seen;
    }

    private static void Require(
        IReadOnlyList<string> declared, string value, string what, string path)
    {
        if (!declared.Contains(value, StringComparer.Ordinal))
        {
            throw new CatalogValidationException(
                path,
                $"'{value}' is not one of the {what}s this policy declares ({string.Join(", ", declared)})");
        }
    }

    private static string Quote(string text) =>
        "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";

    /// <summary>A list of kind names as a message names them: <c>'org'</c>, <c>'org', 'region'</c>.</summary>
    internal static string Named(IReadOnlyList<string> kinds) =>
        kinds.Count == 0 ? "nothing" : string.Join(", ", kinds.Select(kind => "'" + kind + "'"));
}

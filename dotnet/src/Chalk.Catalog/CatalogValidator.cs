using System.Globalization;
using Chalk.Ir;

using Chalk.Entitlements;
// The descriptor vocabulary and the wire enums share three names; the model's are the ones this
// file means, and the wire ones are written out where it needs them.
using Disclosure = Chalk.Entitlements.Disclosure;
using Enforcement = Chalk.Entitlements.Enforcement;

namespace Chalk.Catalog;

/// <summary>
/// Applies the same rules as the planner's <c>CatalogValidator</c> (<c>docs/design/03-planner.md</c>
/// §3.1) so a bad descriptor fails in-process before any RPC: unique schema, table and column names
/// within their scope; key, collation and index column indexes in range; precision and scale rules;
/// <c>row_count &gt;= -1</c>.
/// </summary>
public static class CatalogValidator
{
    /// <summary>Validates the whole context, throwing on the first violation.</summary>
    public static void Validate(CatalogContext catalog) => Validate(catalog, CatalogOptions.Default);

    /// <summary>
    /// The same, under a host's catalog options (step 26). The only one that can refuse a catalog is
    /// <see cref="CatalogOptions.RequireEntitlements"/>, which is off by default — so this overload
    /// and the one above agree for every catalog until a host says otherwise.
    /// </summary>
    public static void Validate(CatalogContext catalog, CatalogOptions options)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(catalog.ContextId))
        {
            throw new CatalogValidationException("context_id", "the context id is empty");
        }

        if (catalog.Epoch < 0)
        {
            throw new CatalogValidationException("epoch", $"the epoch is {catalog.Epoch}; it must be >= 0");
        }

        if (catalog.Schemas.Count == 0)
        {
            throw new CatalogValidationException("schemas", "a catalog needs at least one schema");
        }

        var schemaNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var s = 0; s < catalog.Schemas.Count; s++)
        {
            var schema = catalog.Schemas[s];
            var path = $"schemas[{s}]";

            if (string.IsNullOrWhiteSpace(schema.Name))
            {
                throw new CatalogValidationException(path, "the schema name is empty");
            }

            if (string.IsNullOrWhiteSpace(schema.SourceId))
            {
                throw new CatalogValidationException($"{path} ({schema.Name})", "the source id is empty");
            }

            if (!schemaNames.Add(schema.Name))
            {
                throw new CatalogValidationException(
                    path, $"schema name '{schema.Name}' is used twice (names match case-insensitively)");
            }

            if (schema.Kind == SourceKind.Unspecified)
            {
                throw new CatalogValidationException($"{path} ({schema.Name})", "the source kind is unspecified");
            }

            if (schema.Kind == SourceKind.Remote && string.IsNullOrEmpty(schema.Dialect))
            {
                throw new CatalogValidationException(
                    $"{path} ({schema.Name})", "a remote schema must name the SQL dialect its source speaks");
            }

            Validate(schema, $"{path} ({schema.Name})");

            if (options.RequireEntitlements)
            {
                RequireEntitlements(schema, $"{path} ({schema.Name})");
            }
        }

        // M5: a partition names a table in another schema, so it can only be checked once every
        // schema has been read.
        for (var s = 0; s < catalog.Schemas.Count; s++)
        {
            var schema = catalog.Schemas[s];
            foreach (var table in schema.Tables)
            {
                ValidatePartitioning(catalog, table, $"schemas[{s}] ({schema.Name}).tables ({table.Name})");
            }
        }

        // D270 (c): an association's two ends are in two schemas, so it is checked here, where the
        // whole catalog is in hand, and before the paths that resolve through it.
        for (var a = 0; a < catalog.Associations.Count; a++)
        {
            ValidateAssociation(catalog, catalog.Associations[a], $"associations[{a}]");
        }

        // §3.13: a `Through` names a parent that may live in another schema, so it too can only be
        // checked once every schema has been read.
        for (var s = 0; s < catalog.Schemas.Count; s++)
        {
            var schema = catalog.Schemas[s];
            for (var t = 0; t < schema.Tables.Count; t++)
            {
                var where = $"schemas[{s}] ({schema.Name}).tables[{t}] ({schema.Tables[t].Name})";
                ValidateThrough(catalog, schema, schema.Tables[t], where);
                ValidateInherited(catalog, schema, schema.Tables[t], where);
            }
        }

        ValidateJoinPolicy(catalog.JoinPolicy, "join_policy");
    }

    /// <summary>
    /// A partitioned table is a claim about where rows live (D106): every partition names a table
    /// this catalog has, with the logical table's row type, and a value partition carries a value.
    /// </summary>
    private static void ValidatePartitioning(
        CatalogContext catalog, TableDescriptor table, string path)
    {
        if (table.Partitioning is not { } partitioning)
        {
            return;
        }

        if (partitioning.Partitions.Count == 0)
        {
            throw new CatalogValidationException(path, "a partitioned table declares no partitions");
        }

        if (partitioning.PartitionColumn < 0 || partitioning.PartitionColumn >= table.Columns.Count)
        {
            throw new CatalogValidationException(
                path,
                $"PartitionColumn is {partitioning.PartitionColumn} but the table has "
                + $"{table.Columns.Count} columns");
        }

        foreach (var partition in partitioning.Partitions)
        {
            if (string.IsNullOrWhiteSpace(partition.Schema) || string.IsNullOrWhiteSpace(partition.Table))
            {
                throw new CatalogValidationException(path, "every partition must name a schema and a table");
            }

            if (!partition.HasValue && partition.LowerBound is null && partition.UpperBound is null)
            {
                throw new CatalogValidationException(
                    path,
                    $"partition '{partition.Schema}.{partition.Table}' says neither which value nor "
                    + "which range it holds");
            }

            var owner = catalog.FindSchema(partition.Schema)
                ?? throw new CatalogValidationException(
                    path, $"partition schema '{partition.Schema}' is not in this catalog");
            var physical = owner.FindTable(partition.Table)
                ?? throw new CatalogValidationException(
                    path,
                    $"partition '{partition.Schema}.{partition.Table}' names a table schema "
                    + $"'{partition.Schema}' does not have");

            if (physical.Columns.Count != table.Columns.Count)
            {
                throw new CatalogValidationException(
                    path,
                    $"partition '{partition.Schema}.{partition.Table}' has {physical.Columns.Count} "
                    + $"columns and the partitioned table has {table.Columns.Count}; every partition "
                    + "must have the logical table's row type");
            }

            for (var c = 0; c < table.Columns.Count; c++)
            {
                if (physical.Columns[c].Type.Kind != table.Columns[c].Type.Kind)
                {
                    throw new CatalogValidationException(
                        path,
                        $"partition '{partition.Schema}.{partition.Table}' column "
                        + $"'{physical.Columns[c].Name}' is {physical.Columns[c].Type.Kind} and the "
                        + $"partitioned table's '{table.Columns[c].Name}' is {table.Columns[c].Type.Kind}");
                }
            }
        }
    }

    /// <summary>The cross-source join policy's limits (D104): every one is zero or positive.</summary>
    private static void ValidateJoinPolicy(CrossSourceJoinPolicy policy, string path)
    {
        if (policy.BroadcastMaxRows < 0
            || policy.LookupMaxCalls < 0
            || policy.LocalJoinMaxRows < 0
            || policy.UnknownRowCountAssumption < 0)
        {
            throw new CatalogValidationException(
                path,
                "every join-policy limit must be zero (the planner's default) or positive; "
                + $"BroadcastMaxRows={policy.BroadcastMaxRows}, LookupMaxCalls={policy.LookupMaxCalls}, "
                + $"LocalJoinMaxRows={policy.LocalJoinMaxRows}, "
                + $"UnknownRowCountAssumption={policy.UnknownRowCountAssumption}");
        }

        foreach (var pair in policy.Pairs)
        {
            if (pair.Allowed.Count > 0
                && pair.Preferred != JoinStrategy.Unspecified
                && !pair.Allowed.Contains(pair.Preferred))
            {
                throw new CatalogValidationException(
                    path,
                    $"pair rule '{pair.LeftSource}' -> '{pair.RightSource}' prefers {pair.Preferred}, "
                    + "which is not in its allowed list");
            }
        }
    }

    private static void Validate(SchemaDescriptor schema, string path)
    {
        ValidateCostProfile(schema.CostProfile, path);
        ValidateCapabilities(schema, path);
        ValidateFunctions(schema, path);
        var tableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var t = 0; t < schema.Tables.Count; t++)
        {
            var table = schema.Tables[t];
            var tablePath = $"{path}.tables[{t}]";

            if (string.IsNullOrWhiteSpace(table.Name))
            {
                throw new CatalogValidationException(tablePath, "the table name is empty");
            }

            tablePath = $"{path}.tables[{t}] ({table.Name})";

            if (!tableNames.Add(table.Name))
            {
                throw new CatalogValidationException(
                    tablePath, $"table name '{table.Name}' is used twice in schema '{schema.Name}'");
            }

            Validate(table, tablePath);
            ValidateEntitlementAgainstSource(schema, table, tablePath);
        }

        // Foreign keys name another table, so they can only be checked once every table is known.
        for (var t = 0; t < schema.Tables.Count; t++)
        {
            ValidateForeignKeys(schema, schema.Tables[t], $"{path}.tables[{t}] ({schema.Tables[t].Name})");
        }
    }

    /// <summary>
    /// The half of an entitlement that is a claim about the table's <em>source</em> rather than
    /// about the table (D199, <c>docs/design/16-entitlements.md</c> §3.7).
    /// </summary>
    /// <remarks>
    /// <see cref="Enforcement.PushdownRequired"/> says "refuse a plan that would evaluate this
    /// table's row predicate locally". A table the client scans has no source to push into, so the
    /// constraint could never be satisfied and never be violated: it would be a declaration that
    /// silently does nothing. It is refused here, at registration and naming the source kind, for the
    /// same reason mask pushdown on a source that takes no queries is — a descriptor that disagrees
    /// with its own source is a mistake, and the honest moment to say so is the moment it is made.
    /// </remarks>
    private static void ValidateEntitlementAgainstSource(
        SchemaDescriptor schema, TableDescriptor table, string path)
    {
        if (table.Entitlement is not { Enforcement: Enforcement.PushdownRequired })
        {
            return;
        }

        // The same question the planner asks (D82): a source is one a predicate can reach only when
        // it is remote *and* speaks a query language.
        if (schema.Kind == SourceKind.Remote
            && schema.Capabilities.QueryLanguage is QueryLanguage.Sql or QueryLanguage.Ir)
        {
            return;
        }

        throw new CatalogValidationException(
            $"{path}.entitlement",
            $"Enforcement.PushdownRequired on '{table.Name}', whose schema declares "
            + $"{schema.Kind} with {schema.Capabilities.QueryLanguage}: the client scans this "
            + "table, so there is no source for the row predicate to reach and the constraint "
            + "could neither be met nor broken. Use Enforcement.Pushdown — the default, under "
            + "which a local filter is honest and the report says row_predicate_pushed = false — "
            + "or declare a source that takes queries.");
    }

    /// <summary>
    /// Visibility derived through a parent, as much of it as this side can see (D225, §3.13): the
    /// parent exists and is entitled and restricted, its key is a declared unique key, the child's
    /// column has the parent key's type kind, and no chain of parents returns to a table.
    /// </summary>
    /// <remarks>
    /// The sidecar checks all of this again — it has the SQL parser and the row types — and adds
    /// what only it can see: that the child's rule conditions reference the parent's columns by the
    /// declared parent's name and nothing else. What is here is what a host can be told without a
    /// round trip, which is the same division of labour every other entitlement check follows.
    /// </remarks>
    private static void ValidateThrough(
        CatalogContext catalog, SchemaDescriptor schema, TableDescriptor table, string path)
    {
        if (table.Entitlement is not { } entitlement || entitlement.Through.Count == 0)
        {
            return;
        }

        for (var i = 0; i < entitlement.Through.Count; i++)
        {
            var parent = entitlement.Through[i];
            var parentPath = $"{path}.entitlement.through[{i}]";

            if (parent.Column < 0 || parent.Column >= table.Columns.Count)
            {
                throw new CatalogValidationException(
                    parentPath,
                    $"column index {parent.Column} is out of range for a table of "
                    + $"{table.Columns.Count} columns");
            }

            var (parentSchema, parentTable) = FindThroughParent(catalog, schema, parent);
            if (parentTable is null
                && parent.ParentSchema.Length > 0
                && !HoldsSchema(catalog, parent.ParentSchema))
            {
                // A parent in another source, seen from that source's own registration, which holds
                // one schema and cannot see the other. The check is the whole catalog's — the engine
                // runs it over every schema before it registers, and the sidecar runs it again — so
                // here there is nothing to say rather than something wrong to say.
                continue;
            }

            if (parentTable is null)
            {
                throw new CatalogValidationException(
                    parentPath,
                    $"'{table.Name}' derives its visibility through "
                    + $"'{Qualified(parent, schema)}', which this catalog does not hold. A Through "
                    + "names a table of the same catalog, so the planner can compile it into a join "
                    + "against that table's own entitled scan (docs/design/16-entitlements.md "
                    + "§3.13, D225).");
            }

            if (parentTable.Entitlement is not { } parentEntitlement)
            {
                throw new CatalogValidationException(
                    parentPath,
                    $"'{table.Name}' derives its visibility through "
                    + $"'{parentSchema!.Name}.{parentTable.Name}', which carries no entitlement. "
                    + "Through an unrestricted parent restricts nothing; declare the child "
                    + "unrestricted or restrict the parent (§3.13, D225).");
            }

            if (parentEntitlement.RowPredicate.Length == 0 && parentEntitlement.Through.Count == 0)
            {
                throw new CatalogValidationException(
                    parentPath,
                    $"'{table.Name}' derives its visibility through "
                    + $"'{parentSchema!.Name}.{parentTable.Name}', whose entitlement restricts no "
                    + "row — it has neither a row predicate nor a Through of its own. Through an "
                    + "unrestricted parent restricts nothing; declare the child unrestricted or "
                    + "restrict the parent (§3.13, D225).");
            }

            if (parent.ParentColumn < 0 || parent.ParentColumn >= parentTable.Columns.Count)
            {
                throw new CatalogValidationException(
                    parentPath,
                    $"parent column index {parent.ParentColumn} is out of range for "
                    + $"'{parentSchema!.Name}.{parentTable.Name}', which has "
                    + $"{parentTable.Columns.Count} columns");
            }

            if (!IsDeclaredUniqueKey(parentTable, parent.ParentColumn))
            {
                throw new CatalogValidationException(
                    parentPath,
                    $"'{parentSchema!.Name}.{parentTable.Name}.{parentTable.Columns[parent.ParentColumn].Name}' "
                    + "is not a declared unique key of the parent. The join the planner compiles a "
                    + "Through into must not multiply rows, and a unique key is what makes that so "
                    + "(§3.13, D226). Declare the key.");
            }

            var childType = table.Columns[parent.Column].Type;
            var parentType = parentTable.Columns[parent.ParentColumn].Type;
            if (childType.Kind != parentType.Kind)
            {
                throw new CatalogValidationException(
                    parentPath,
                    $"'{table.Name}.{table.Columns[parent.Column].Name}' is {childType.Kind} and "
                    + $"'{parentSchema!.Name}.{parentTable.Name}.{parentTable.Columns[parent.ParentColumn].Name}' "
                    + $"is {parentType.Kind}; the correlation key and the parent's key must have the "
                    + "same type kind, or the join could never match (§3.13, D225).");
            }
        }

        // A chain of parents may not return to a table (D225). A diamond is fine: what is refused is
        // a cycle, which is a join the planner could never finish building.
        var visiting = new List<string>();
        RefuseVisibilityCycle(catalog, schema, table, visiting, path);
    }

    /// <summary>
    /// The declared paths of one table (D265, <c>docs/design/38-existential-visibility.md</c> §3):
    /// every step arrives at a table the catalog holds, over a declared foreign key in the direction
    /// the step claims; the shape is one §1 admits; the bridge's two key columns carry no rule; and
    /// neither a bridge nor the endpoint is the target itself.
    /// </summary>
    /// <remarks>
    /// The sidecar checks all of this again, and adds what only it can see: that the endpoint
    /// predicate is boolean over the endpoint's row and that the target's rule conditions name the
    /// declared endpoint and nothing else. What is here is what a host can be told without a round
    /// trip, exactly as <see cref="ValidateThrough"/> is.
    /// </remarks>
    private static void ValidateInherited(
        CatalogContext catalog, SchemaDescriptor schema, TableDescriptor table, string path)
    {
        if (table.Entitlement is not { } entitlement || entitlement.Inherited.Count == 0)
        {
            return;
        }

        for (var i = 0; i < entitlement.Inherited.Count; i++)
        {
            var declared = entitlement.Inherited[i];
            var where = $"{path}.entitlement.inherited[{i}]";

            if (declared.Steps.Count == 0)
            {
                throw new CatalogValidationException(
                    where,
                    $"the path of kind '{declared.Kind}' on '{table.Name}' has no step. A path with "
                    + "no step is a direct dimension written the long way round "
                    + "(docs/design/38-existential-visibility.md §1, §3, D265).");
            }

            var fromSchema = schema;
            var fromTable = table;
            var ups = 0;
            for (var s = 0; s < declared.Steps.Count; s++)
            {
                var step = declared.Steps[s];
                var (toSchema, toTable) = FindTable(catalog, schema, step.Schema, step.Table);
                if (toTable is null
                    && step.Schema.Length > 0
                    && !HoldsSchema(catalog, step.Schema))
                {
                    // One source's own registration, which holds one schema and cannot see the
                    // other. The whole catalog's check is the engine's, and the sidecar runs it
                    // again over every schema at once.
                    fromTable = null;
                    break;
                }

                if (toTable is null)
                {
                    throw new CatalogValidationException(
                        where,
                        $"step {s} of the path of kind '{declared.Kind}' on '{table.Name}' arrives "
                        + $"at '{Qualified(step.Schema, step.Table, schema)}', which this catalog "
                        + "does not hold. A path names tables of the same catalog, so the pass can "
                        + "compile it into one key set (§3, D265).");
                }

                if (step.Direction == Chalk.Entitlements.StepDirection.ToChild)
                {
                    ups++;
                    if (s > 0)
                    {
                        throw new CatalogValidationException(
                            where,
                            $"step {s} of the path of kind '{declared.Kind}' on '{table.Name}' goes "
                            + "up. This run admits an inherited path of any length and a related "
                            + "path of one up-step followed by down-steps; a second up-step, and an "
                            + "up-step after a down-step, are refused rather than built (§1, §9, "
                            + "D265).");
                    }
                }

                ValidateStep(
                    catalog, fromSchema, fromTable!, toSchema!, toTable, step, declared, s, where);

                if (step.Direction == Chalk.Entitlements.StepDirection.ToChild)
                {
                    // The bridge's two key columns are correlation keys the mechanism reads raw,
                    // exactly as D227 leaves a parent's key FULL. A rule over either is a
                    // contradiction: the policy would be withholding a value the mechanism reads.
                    RefuseProtectedBridgeKey(toTable, toSchema!, step.ToColumn, where);
                    if (s + 1 < declared.Steps.Count)
                    {
                        RefuseProtectedBridgeKey(
                            toTable, toSchema!, declared.Steps[s + 1].FromColumn, where);
                    }
                }

                if (string.Equals(toTable.Name, table.Name, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(toSchema!.Name, schema.Name, StringComparison.OrdinalIgnoreCase))
                {
                    throw new CatalogValidationException(
                        where,
                        $"step {s} of the path of kind '{declared.Kind}' returns to '{table.Name}' "
                        + "itself. A row's visibility cannot derive from its own table's, so a "
                        + "bridge and an endpoint are both other tables (§3, D265).");
                }

                fromSchema = toSchema!;
                fromTable = toTable;
            }

            if (fromTable is null)
            {
                continue;
            }

            if (!string.Equals(fromTable.Name, declared.EndpointTable, StringComparison.OrdinalIgnoreCase))
            {
                throw new CatalogValidationException(
                    where,
                    $"the path of kind '{declared.Kind}' on '{table.Name}' says its endpoint is "
                    + $"'{declared.EndpointTable}', and its last step arrives at "
                    + $"'{fromTable.Name}'. The endpoint names where the kind is held, so it is the "
                    + "table the last step reaches (§2, D265).");
            }

            if (declared.EndpointPredicate.Length == 0)
            {
                throw new CatalogValidationException(
                    where,
                    $"the path of kind '{declared.Kind}' on '{table.Name}' carries no endpoint "
                    + $"predicate. The endpoint '{fromTable.Name}' is where the kind is held, and "
                    + "the predicate is what says which of its rows this principal holds it in; a "
                    + "path without one would grant every row (§2, §3, D265).");
            }

            // A path predicate is decided above the join, over the target's row and the endpoint's
            // together, which needs one endpoint row per target key. An `Inherited` path is one; a
            // `Related` path is many, and the verdict would have to be borne by the existence marker
            // (D279 §1, §4). Refused here, where the descriptor is read, rather than compiled into a
            // term evaluated at the wrong cardinality.
            if (declared.PathPredicate.Length > 0
                && declared.Steps.Count > 0
                && declared.Steps[0].Direction == Chalk.Entitlements.StepDirection.ToChild)
            {
                throw new CatalogValidationException(
                    where,
                    $"the path of kind '{declared.Kind}' on '{table.Name}' goes up to a bridge and "
                    + "carries a path predicate. A path predicate is decided above the join, over "
                    + "this table's own row and the endpoint's together, and that needs one endpoint "
                    + "row per key: an inherited path is one, a related path is many, so the verdict "
                    + "would have to be borne by the path's existence marker. Reach the endpoint "
                    + "with an inherited path, or drop the confinement (§1, §4, D279).");
            }
        }

        RefuseVisibilityCycle(catalog, schema, table, [], path);
    }

    /// <summary>
    /// A declared association: both ends are in this catalog, the referenced column is a declared
    /// unique key of its table, and the two columns' types agree (D270 (c),
    /// <c>docs/design/45-typed-tenancy-surface.md</c> §3).
    /// </summary>
    /// <remarks>
    /// What is <b>not</b> checked is whether the claim is true of the data: an association is the
    /// host's assertion, exactly as a declared foreign key is the source's own (ADR 0025's "verified
    /// is read as declared"), and across two sources there is no transaction in which it could be
    /// checked. Its shape is checked here because a path that resolves through it compiles into
    /// joins, and a join over a non-key or a mismatched type is a bug whatever the data says.
    /// </remarks>
    private static void ValidateAssociation(
        CatalogContext catalog, AssociationDescriptor association, string path)
    {
        var (fromSchema, fromTable) =
            FindTable(catalog, catalog.Schemas[0], association.FromSchema, association.FromTable);
        if (fromTable is null)
        {
            // One source's own registration, which holds one schema and cannot see the other. The
            // whole catalog's check is the engine's, exactly as a cross-schema `Through`'s is.
            if (!HoldsSchema(catalog, association.FromSchema))
            {
                return;
            }

            throw new CatalogValidationException(
                path,
                $"the association starts at '{association.FromSchema}.{association.FromTable}', "
                + "which this catalog does not hold.");
        }

        var (toSchema, toTable) =
            FindTable(catalog, catalog.Schemas[0], association.ToSchema, association.ToTable);
        if (toTable is null)
        {
            if (!HoldsSchema(catalog, association.ToSchema))
            {
                return;
            }

            throw new CatalogValidationException(
                path,
                $"the association arrives at '{association.ToSchema}.{association.ToTable}', which "
                + "this catalog does not hold.");
        }

        var fromColumn = fromTable.IndexOfColumn(association.FromColumn);
        if (fromColumn < 0)
        {
            throw new CatalogValidationException(
                path,
                $"'{association.FromColumn}' is not a column of '{fromSchema!.Name}.{fromTable.Name}'.");
        }

        var toColumn = toTable.IndexOfColumn(association.ToColumn);
        if (toColumn < 0)
        {
            throw new CatalogValidationException(
                path,
                $"'{association.ToColumn}' is not a column of '{toSchema!.Name}.{toTable.Name}'.");
        }

        if (!IsDeclaredUniqueKey(toTable, toColumn))
        {
            throw new CatalogValidationException(
                path,
                $"the association names '{toSchema!.Name}.{toTable.Name}.{association.ToColumn}', "
                + "which is not a declared unique key of that table. An association is read exactly "
                + "as a foreign key is, and the joins it compiles into must not multiply rows "
                + "(docs/design/45-typed-tenancy-surface.md §3, D270). Declare the key.");
        }

        if (fromTable.Columns[fromColumn].Type.Kind != toTable.Columns[toColumn].Type.Kind)
        {
            throw new CatalogValidationException(
                path,
                $"the association joins "
                + $"'{fromSchema!.Name}.{fromTable.Name}.{association.FromColumn}' "
                + $"({fromTable.Columns[fromColumn].Type.Kind}) to "
                + $"'{toSchema!.Name}.{toTable.Name}.{association.ToColumn}' "
                + $"({toTable.Columns[toColumn].Type.Kind}), and the two types disagree.");
        }
    }

    /// <summary>Whether a declared association states this step's join (D270 (c)).</summary>
    private static bool Associates(
        CatalogContext catalog,
        SchemaDescriptor childSchema,
        TableDescriptor child,
        int childColumn,
        SchemaDescriptor parentSchema,
        TableDescriptor parent,
        int parentColumn)
    {
        foreach (var association in catalog.Associations)
        {
            if (string.Equals(association.FromSchema, childSchema.Name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(association.FromTable, child.Name, StringComparison.OrdinalIgnoreCase)
                && child.IndexOfColumn(association.FromColumn) == childColumn
                && string.Equals(association.ToSchema, parentSchema.Name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(association.ToTable, parent.Name, StringComparison.OrdinalIgnoreCase)
                && parent.IndexOfColumn(association.ToColumn) == parentColumn)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>One step: the declared foreign key exists, in the direction the step claims.</summary>
    private static void ValidateStep(
        CatalogContext catalog,
        SchemaDescriptor fromSchema,
        TableDescriptor from,
        SchemaDescriptor toSchema,
        TableDescriptor to,
        VisibilityStepDescriptor step,
        InheritedVisibilityDescriptor declared,
        int ordinal,
        string where)
    {
        if (step.FromColumn < 0 || step.FromColumn >= from.Columns.Count)
        {
            throw new CatalogValidationException(
                where,
                $"step {ordinal} of the path of kind '{declared.Kind}' reads column "
                + $"{step.FromColumn} of '{fromSchema.Name}.{from.Name}', which has "
                + $"{from.Columns.Count} columns");
        }

        if (step.ToColumn < 0 || step.ToColumn >= to.Columns.Count)
        {
            throw new CatalogValidationException(
                where,
                $"step {ordinal} of the path of kind '{declared.Kind}' reads column {step.ToColumn} "
                + $"of '{toSchema.Name}.{to.Name}', which has {to.Columns.Count} columns");
        }

        var down = step.Direction == Chalk.Entitlements.StepDirection.ToParent;
        var child = down ? from : to;
        var childColumn = down ? step.FromColumn : step.ToColumn;
        var parent = down ? to : from;
        var parentColumn = down ? step.ToColumn : step.FromColumn;

        if (!IsDeclaredUniqueKey(parent, parentColumn))
        {
            throw new CatalogValidationException(
                where,
                $"step {ordinal} of the path of kind '{declared.Kind}' joins "
                + $"'{parent.Name}.{parent.Columns[parentColumn].Name}', which is not a declared "
                + "unique key of that table. The joins a path compiles into must not multiply rows, "
                + "and a unique key is what makes that so (§3, D265). Declare the key.");
        }

        var childSchema = down ? fromSchema : toSchema;
        var parentSchema = down ? toSchema : fromSchema;
        if (!ReferencesKey(child, childColumn, parent.Name, parentColumn)
            && !Associates(
                catalog, childSchema, child, childColumn, parentSchema, parent, parentColumn))
        {
            throw new CatalogValidationException(
                where,
                $"step {ordinal} of the path of kind '{declared.Kind}' goes "
                + (down ? "down" : "up")
                + $" from '{from.Name}' to '{to.Name}', and "
                + $"'{child.Name}.{child.Columns[childColumn].Name}' declares no foreign key naming "
                + $"'{parent.Name}.{parent.Columns[parentColumn].Name}', and this catalog declares "
                + "no association between them either. The direction is checked and never inferred, "
                + "so the association has to be declared before a path can rely on it (§1, §3, "
                + "D265; docs/design/45-typed-tenancy-surface.md §3, D270).");
        }

        if (child.Columns[childColumn].Type.Kind != parent.Columns[parentColumn].Type.Kind)
        {
            throw new CatalogValidationException(
                where,
                $"step {ordinal} of the path of kind '{declared.Kind}' joins "
                + $"'{childSchema.Name}.{child.Name}.{child.Columns[childColumn].Name}' "
                + $"({child.Columns[childColumn].Type.Kind}) to "
                + $"'{parentSchema.Name}.{parent.Name}.{parent.Columns[parentColumn].Name}' "
                + $"({parent.Columns[parentColumn].Type.Kind}), and the two types disagree.");
        }
    }

    private static void RefuseProtectedBridgeKey(
        TableDescriptor via, SchemaDescriptor viaSchema, int column, string where)
    {
        if (via.Entitlement is not { } entitlement || column < 0 || column >= via.Columns.Count)
        {
            return;
        }

        var denied = entitlement.FindColumn(column) is not null
            || (entitlement.DefaultDisclosure != Disclosure.Full
                && entitlement.DefaultDisclosure != Disclosure.Unspecified);
        if (denied)
        {
            throw new CatalogValidationException(
                where,
                $"the bridge key '{viaSchema.Name}.{via.Name}.{via.Columns[column].Name}' is "
                + "protected by a rule or by its table's default. The mechanism reads the bridge's "
                + "two key columns raw and discloses neither, exactly as a parent's key is left full "
                + "under D227, so a rule over one is a contradiction: leave it full, or reach the "
                + "target another way (§3, D265).");
        }
    }

    /// <summary>
    /// A cycle in the <b>visibility-dependency graph</b> (D225, D265 §3): its edges are a
    /// <c>through</c>'s child → parent and a path's target → endpoint. A bridge is not a node — its
    /// own visibility is never consulted, which is what keeps the marketplace shape acyclic where
    /// the bridge is itself entitled through the target.
    /// </summary>
    private static void RefuseVisibilityCycle(
        CatalogContext catalog,
        SchemaDescriptor schema,
        TableDescriptor table,
        List<string> visiting,
        string path)
    {
        var name = $"{schema.Name}.{table.Name}";
        if (visiting.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            visiting.Add(name);
            throw new CatalogValidationException(
                $"{path}.entitlement",
                $"the chain of derived visibility returns to '{name}': "
                + $"{string.Join(" -> ", visiting)}. A row's visibility cannot derive from itself, "
                + "so a cycle is refused where a diamond is fine (docs/design/16-entitlements.md "
                + "§3.13, D225; docs/design/38-existential-visibility.md §3, D265).");
        }

        if (table.Entitlement is not { } entitlement
            || (entitlement.Through.Count == 0 && entitlement.Inherited.Count == 0))
        {
            return;
        }

        visiting.Add(name);
        foreach (var parent in entitlement.Through)
        {
            var (parentSchema, parentTable) = FindThroughParent(catalog, schema, parent);
            if (parentTable is not null)
            {
                RefuseVisibilityCycle(catalog, parentSchema!, parentTable, visiting, path);
            }
        }

        foreach (var declared in entitlement.Inherited)
        {
            var (endpointSchema, endpoint) =
                FindTable(catalog, schema, declared.EndpointSchema, declared.EndpointTable);
            if (endpoint is not null)
            {
                RefuseVisibilityCycle(catalog, endpointSchema!, endpoint, visiting, path);
            }
        }

        visiting.RemoveAt(visiting.Count - 1);
    }

    /// <summary>Whether a single-column foreign key over <paramref name="column"/> names that key.</summary>
    private static bool ReferencesKey(
        TableDescriptor table, int column, string parentTable, int parentColumn)
    {
        foreach (var key in table.ForeignKeys)
        {
            if (key.Columns.Count == 1
                && key.ParentColumns.Count == 1
                && key.Columns[0] == column
                && key.ParentColumns[0] == parentColumn
                && string.Equals(key.ParentTable, parentTable, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static (SchemaDescriptor?, TableDescriptor?) FindTable(
        CatalogContext catalog, SchemaDescriptor schema, string schemaName, string tableName)
    {
        var wanted = schemaName.Length == 0 ? schema.Name : schemaName;
        foreach (var candidate in catalog.Schemas)
        {
            if (!string.Equals(candidate.Name, wanted, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var table in candidate.Tables)
            {
                if (string.Equals(table.Name, tableName, StringComparison.OrdinalIgnoreCase))
                {
                    return (candidate, table);
                }
            }
        }

        return (null, null);
    }

    private static string Qualified(string schemaName, string tableName, SchemaDescriptor schema) =>
        (schemaName.Length == 0 ? schema.Name : schemaName) + "." + tableName;

    private static bool HoldsSchema(CatalogContext catalog, string name)
    {
        foreach (var schema in catalog.Schemas)
        {
            if (string.Equals(schema.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static (SchemaDescriptor?, TableDescriptor?) FindThroughParent(
        CatalogContext catalog, SchemaDescriptor schema, ParentVisibilityDescriptor parent)
    {
        var wanted = parent.ParentSchema.Length == 0 ? schema.Name : parent.ParentSchema;
        foreach (var candidate in catalog.Schemas)
        {
            if (!string.Equals(candidate.Name, wanted, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var table in candidate.Tables)
            {
                if (string.Equals(table.Name, parent.ParentTable, StringComparison.OrdinalIgnoreCase))
                {
                    return (candidate, table);
                }
            }
        }

        return (null, null);
    }

    private static string Qualified(ParentVisibilityDescriptor parent, SchemaDescriptor schema) =>
        (parent.ParentSchema.Length == 0 ? schema.Name : parent.ParentSchema) + "." + parent.ParentTable;

    private static bool IsDeclaredUniqueKey(TableDescriptor table, int column)
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

    /// <summary>
    /// The functions a schema declares (D77, <c>docs/design/17-user-defined-functions.md</c> §1).
    /// The planner says the same things again over the wire, and additionally parses the SQL bodies,
    /// which needs a SQL parser this side does not have.
    /// </summary>
    private static void ValidateFunctions(SchemaDescriptor schema, string path)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var f = 0; f < schema.Functions.Count; f++)
        {
            var function = schema.Functions[f];
            var functionPath = $"{path}.functions[{f}]";
            if (string.IsNullOrWhiteSpace(function.Name))
            {
                throw new CatalogValidationException(functionPath, "the function name is empty");
            }

            functionPath = $"{functionPath} ({function.Name})";
            if (!names.Add(function.Name))
            {
                throw new CatalogValidationException(
                    functionPath,
                    $"function name '{function.Name}' is used twice in schema '{schema.Name}'");
            }

            if (function.Kind == FunctionKind.Unspecified)
            {
                throw new CatalogValidationException(functionPath, "the function kind is unspecified");
            }

            var isTable = function.Kind == FunctionKind.Table;
            if (isTable)
            {
                if (function.ReturnsTable.Count == 0)
                {
                    throw new CatalogValidationException(
                        functionPath, "a table function must declare the row type it returns");
                }

                if (function.ReturnType is not null)
                {
                    throw new CatalogValidationException(
                        functionPath, "a table function returns a table, not a scalar type");
                }

                if (function.Body is NativeFunctionBody)
                {
                    throw new CatalogValidationException(
                        functionPath,
                        "a native table function has nowhere to be evaluated: a pushed subtree is a "
                        + "query, not a table-valued call "
                        + "(docs/design/17-user-defined-functions.md §8)");
                }
            }
            else
            {
                if (function.ReturnType is not { } returnType)
                {
                    throw new CatalogValidationException(functionPath, "the function declares no return type");
                }
                else
                {
                    ValidateType(returnType, $"{functionPath}.return_type");

                    // D291: a composite comes from a function the host implements. A SQL body is
                    // inlined into algebra that has no way to build one, and a native body runs in a
                    // source that has no way to return one.
                    if (returnType.Kind == TypeKind.Composite && function.Body is not ClientFunctionBody)
                    {
                        throw new CatalogValidationException(
                            $"{functionPath}.return_type",
                            $"'{function.Name}' returns a COMPOSITE and is "
                            + (function.Body is SqlFunctionBody ? "SQL-bodied" : "native")
                            + "; only a client-bodied function returns a composite value, which the host "
                            + "implements and the engine takes apart by field");
                    }
                }

                if (function.ReturnsTable.Count > 0)
                {
                    throw new CatalogValidationException(
                        functionPath, "only a table function declares a returned row type");
                }

                if (function.Rows != 0)
                {
                    throw new CatalogValidationException(
                        functionPath, "`Rows` is a table function's output estimate; this one is not one");
                }
            }

            if (function.Kind != FunctionKind.Aggregate
                && (function.Window || function.Ordered || function.NullTreatment))
            {
                throw new CatalogValidationException(
                    functionPath, "`Window`, `Ordered` and `NullTreatment` describe an aggregate");
            }

            if (function.Rows < 0)
            {
                throw new CatalogValidationException(
                    functionPath, $"`Rows` is {function.Rows}; it must be >= 0");
            }

            if (function.Cost < 0 || double.IsNaN(function.Cost) || double.IsInfinity(function.Cost))
            {
                throw new CatalogValidationException(
                    functionPath,
                    $"`Cost` is {function.Cost.ToString(CultureInfo.InvariantCulture)}; it must be "
                    + "finite and >= 0 (zero means the planner's default)");
            }

            if (function.Monotonicity.Count != 0
                && function.Monotonicity.Count != function.Parameters.Count)
            {
                throw new CatalogValidationException(
                    functionPath,
                    $"monotonicity has {function.Monotonicity.Count} entries for "
                    + $"{function.Parameters.Count} parameters; declare one per parameter or none");
            }

            if (function.Body is SqlFunctionBody { Text: var text } && string.IsNullOrWhiteSpace(text))
            {
                throw new CatalogValidationException(functionPath, "the SQL body is empty");
            }

            if (function.Body is NativeFunctionBody
                && schema.Capabilities.QueryLanguage is not (QueryLanguage.Sql or QueryLanguage.Ir))
            {
                throw new CatalogValidationException(
                    functionPath,
                    "a native function is evaluated by its own source, so the schema must take "
                    + $"queries; this one declares {schema.Capabilities.QueryLanguage}");
            }

            var parameterNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var p = 0; p < function.Parameters.Count; p++)
            {
                var parameter = function.Parameters[p];
                var parameterPath = $"{functionPath}.parameters[{p}]";
                if (string.IsNullOrWhiteSpace(parameter.Name))
                {
                    throw new CatalogValidationException(parameterPath, "the parameter name is empty");
                }

                parameterPath = $"{parameterPath} ({parameter.Name})";
                if (!parameterNames.Add(parameter.Name))
                {
                    throw new CatalogValidationException(
                        parameterPath, $"parameter name '{parameter.Name}' is used twice");
                }

                ValidateType(parameter.Type, parameterPath);
                if (parameter.Type.Kind == TypeKind.List)
                {
                    throw new CatalogValidationException(
                        parameterPath, "a v1 parameter is a scalar; LIST parameters are not supported");
                }

                if (parameter.Type.Kind == TypeKind.Composite)
                {
                    throw new CatalogValidationException(
                        parameterPath,
                        "a parameter is a scalar; a COMPOSITE is only ever a function's result, so "
                        + "pass its fields as parameters of their own");
                }

                if (parameter.Optional && parameter.Default is null)
                {
                    throw new CatalogValidationException(
                        parameterPath, "an optional parameter must carry the default it stands for");
                }

                if (!parameter.Optional && parameter.Default is not null)
                {
                    throw new CatalogValidationException(
                        parameterPath, "a default is only meaningful on an optional parameter");
                }
            }

            for (var c = 0; c < function.ReturnsTable.Count; c++)
            {
                var column = function.ReturnsTable[c];
                var columnPath = $"{functionPath}.returns_table[{c}]";
                if (string.IsNullOrWhiteSpace(column.Name))
                {
                    throw new CatalogValidationException(columnPath, "the column name is empty");
                }

                ValidateType(column.Type, $"{columnPath} ({column.Name})");
                RefuseCompositeColumn(column.Type, $"{columnPath} ({column.Name})", "a table function's column");
            }
        }
    }

    /// <summary>
    /// D291: a COMPOSITE exists only between the function that returned it and the row that takes it apart
    /// or carries it out, so no table and no table function declares one as a column.
    /// </summary>
    private static void RefuseCompositeColumn(ChalkType type, string path, string what)
    {
        if (type.Kind == TypeKind.Composite)
        {
            throw new CatalogValidationException(
                path,
                $"a COMPOSITE cannot be {what}; a composite value is the result of a client-bodied function and "
                + "is never stored. Declare its fields as columns of their own");
        }
    }

    /// <summary>
    /// Contradictions between a descriptor and its dialect profile (D82). A capability model that
    /// disagrees with itself is a bug in the adapter, and finding it at registration is much better
    /// than finding it as a wrong answer: every rule here names a pair that cannot both be true.
    /// </summary>
    private static void ValidateCapabilities(SchemaDescriptor schema, string path)
    {
        var capabilities = schema.Capabilities;
        var profile = schema.DialectProfile;

        if (capabilities.QueryLanguage == QueryLanguage.None && Pushes(capabilities))
        {
            throw new CatalogValidationException(
                path,
                "the source declares pushable work but QueryLanguage.None, so it can only be "
                + "scanned and nothing would ever be pushed. Set QueryLanguage.Sql or "
                + "QueryLanguage.Ir, or declare no capabilities.");
        }

        if (capabilities.SupportsOffset && !capabilities.SupportsLimit)
        {
            throw new CatalogValidationException(
                path,
                "SupportsOffset without SupportsLimit: an OFFSET is pushed as part of a fetch, so a "
                + "source that takes one must take a LIMIT too.");
        }

        if (capabilities.SupportsHaving && !capabilities.SupportsGroupBy)
        {
            throw new CatalogValidationException(
                path, "SupportsHaving without SupportsGroupBy: a HAVING has nothing to filter.");
        }

        if (capabilities.MaxInList > 0
            && !capabilities.PushablePredicates.Contains(PredicateShape.In))
        {
            throw new CatalogValidationException(
                path,
                $"MaxInList is {capabilities.MaxInList} but PredicateShape.In is not pushable, so no "
                + "IN list is ever pushed. Declare the shape or leave MaxInList at zero.");
        }

        // M5: a broadcast ships rows into the source's own query, so a source that cannot take a
        // list of values certainly cannot take a VALUES relation of them.
        if (capabilities.SupportsValuesJoin && capabilities.MaxInList <= 0)
        {
            throw new CatalogValidationException(
                path,
                "SupportsValuesJoin with MaxInList 0: a broadcast join ships the small side's rows "
                + "into the source's query, so a source that accepts no list of values cannot do one.");
        }

        // F50: a row-constructor IN list is an IN list, and the same ceiling sizes it — in key rows
        // rather than in values.
        if (capabilities.SupportsRowValueInList && capabilities.MaxInList <= 0)
        {
            throw new CatalogValidationException(
                path,
                "SupportsRowValueInList with MaxInList 0: a row-constructor IN list is still an IN "
                + "list, and MaxInList is what sizes a call of them.");
        }

        // A mask pushed into a source puts the raw value and the mask key in its query text; a
        // source that cannot take a query at all could not evaluate one anyway.
        if (capabilities.SupportsMaskPushdown && capabilities.QueryLanguage == QueryLanguage.None)
        {
            throw new CatalogValidationException(
                path,
                "SupportsMaskPushdown with QueryLanguage.None: the source is only scanned, so it "
                + "never sees an expression to evaluate. Declare a query language, or leave mask "
                + "pushdown off — which is the default, and what masking is for.");
        }

        if (capabilities.MaxPushdownRows < 0)
        {
            throw new CatalogValidationException(
                path,
                $"MaxPushdownRows is {capabilities.MaxPushdownRows}; it must be zero (unlimited) or "
                + "positive.");
        }

        // D89's string rules are applied by the planner regardless of the descriptor, but a
        // descriptor that declares a string shape it can never use is a mistake worth naming rather
        // than silently ignoring.
        if (profile.StringCollation is StringCollation.CaseInsensitive or StringCollation.Locale)
        {
            foreach (var shape in capabilities.PushablePredicates)
            {
                if (shape is PredicateShape.Like or PredicateShape.LikePrefix)
                {
                    throw new CatalogValidationException(
                        path,
                        $"PredicateShape.{shape} is declared pushable but the dialect profile says "
                        + $"StringCollation.{profile.StringCollation}. A LIKE the source evaluates "
                        + "under a different collation than Chalk's can match different rows, so it "
                        + "is never pushed (D89). Drop the shape, or declare "
                        + "StringCollation.Binary if the source really compares by code point.");
                }
            }
        }

        if (capabilities.QueryLanguage == QueryLanguage.Sql
            && profile.Quoting == IdentifierQuoting.Unspecified)
        {
            throw new CatalogValidationException(
                path,
                "a SQL source must say how it quotes identifiers: set DialectProfile.Quoting, or "
                + "start from a DialectProfiles preset.");
        }
    }

    /// <summary>Whether a descriptor claims any work at all beyond a plain scan.</summary>
    private static bool Pushes(SourceCapabilities c) =>
        c.PushablePredicates.Count > 0
        || c.PushableFunctions.Count > 0
        || c.PushableAggregates.Count > 0
        || c.SupportsProject
        || c.SupportsSort
        || c.SupportsLimit
        || c.SupportsOffset
        || c.SupportsDistinct
        || c.SupportsGroupBy
        || c.SupportsHaving
        || c.SupportsInnerJoin
        || c.SupportsOuterJoin
        || c.SupportsSemiAntiJoin;

    /// <summary>
    /// A foreign key is a claim the cost model acts on (F14), so every part of it is checked: the
    /// child's columns exist, the parent exists in the same schema, the two key arities match, and
    /// each pair of columns has the same type kind — a key that compares an I32 to a STRING can
    /// never match a row and would silently bound a join at nothing.
    /// </summary>
    private static void ValidateForeignKeys(SchemaDescriptor schema, TableDescriptor table, string path)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var k = 0; k < table.ForeignKeys.Count; k++)
        {
            var key = table.ForeignKeys[k];
            var keyPath = $"{path}.foreign_keys[{k}]";

            if (key.Name.Length > 0 && !names.Add(key.Name))
            {
                throw new CatalogValidationException(
                    keyPath, $"foreign key name '{key.Name}' is used twice on table '{table.Name}'");
            }

            if (key.Columns.Count == 0)
            {
                throw new CatalogValidationException(keyPath, "a foreign key has no columns");
            }

            if (key.Columns.Count != key.ParentColumns.Count)
            {
                throw new CatalogValidationException(
                    keyPath,
                    $"the key has {key.Columns.Count} column(s) and the parent key has "
                    + $"{key.ParentColumns.Count}; a foreign key pairs them one for one");
            }

            var parent = schema.FindTable(key.ParentTable);
            if (parent is null)
            {
                throw new CatalogValidationException(
                    keyPath,
                    $"the parent table '{key.ParentTable}' is not in schema '{schema.Name}'");
            }

            var seen = new HashSet<int>();
            for (var c = 0; c < key.Columns.Count; c++)
            {
                RequireColumn(table, key.Columns[c], keyPath);
                RequireComparable(table, key.Columns[c], keyPath, "a foreign key");
                if (!seen.Add(key.Columns[c]))
                {
                    throw new CatalogValidationException(
                        keyPath, $"column {key.Columns[c]} appears twice in the same foreign key");
                }

                RequireColumn(parent, key.ParentColumns[c], $"{keyPath} (parent '{parent.Name}')");
                RequireComparable(
                    parent, key.ParentColumns[c], $"{keyPath} (parent '{parent.Name}')", "a foreign key");

                var childType = table.Columns[key.Columns[c]].Type;
                var parentType = parent.Columns[key.ParentColumns[c]].Type;
                if (childType.Kind != parentType.Kind)
                {
                    throw new CatalogValidationException(
                        keyPath,
                        $"column '{table.Columns[key.Columns[c]].Name}' is {childType.Kind} but "
                        + $"'{parent.Name}.{parent.Columns[key.ParentColumns[c]].Name}' is "
                        + $"{parentType.Kind}; a foreign key's columns must have the same kind");
                }
            }
        }
    }

    private static void Validate(TableDescriptor table, string path)
    {
        if (table.Columns.Count == 0)
        {
            throw new CatalogValidationException(path, "a table needs at least one column");
        }

        if (table.RowCount < -1)
        {
            throw new CatalogValidationException(
                path, $"row_count is {table.RowCount}; it must be >= -1 (-1 means unknown)");
        }

        ValidateCostProfile(table.CostProfile, path);

        var columnNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var c = 0; c < table.Columns.Count; c++)
        {
            var column = table.Columns[c];
            if (string.IsNullOrWhiteSpace(column.Name))
            {
                throw new CatalogValidationException($"{path}.columns[{c}]", "the column name is empty");
            }

            if (!columnNames.Add(column.Name))
            {
                throw new CatalogValidationException(
                    $"{path}.columns[{c}]",
                    $"column name '{column.Name}' is used twice in table '{table.Name}'");
            }

            ValidateType(column.Type, $"{path}.columns[{c}] ({column.Name})");
            RefuseCompositeColumn(column.Type, $"{path}.columns[{c}] ({column.Name})", "a table column");
        }

        for (var k = 0; k < table.UniqueKeys.Count; k++)
        {
            var key = table.UniqueKeys[k];
            if (key.Columns.Count == 0)
            {
                throw new CatalogValidationException($"{path}.unique_keys[{k}]", "a unique key has no columns");
            }

            var seen = new HashSet<int>();
            foreach (var column in key.Columns)
            {
                RequireColumn(table, column, $"{path}.unique_keys[{k}]");
                RequireComparable(table, column, $"{path}.unique_keys[{k}]", "a unique key");
                if (!seen.Add(column))
                {
                    throw new CatalogValidationException(
                        $"{path}.unique_keys[{k}]", $"column {column} appears twice in the same key");
                }
            }
        }

        for (var i = 0; i < table.Collations.Count; i++)
        {
            var collation = table.Collations[i];
            if (collation.Keys.Count == 0)
            {
                throw new CatalogValidationException($"{path}.collations[{i}]", "a collation has no keys");
            }

            var seen = new HashSet<int>();
            for (var j = 0; j < collation.Keys.Count; j++)
            {
                var key = collation.Keys[j];
                RequireColumn(table, key.Column, $"{path}.collations[{i}].keys[{j}]");
                RequireComparable(
                    table, key.Column, $"{path}.collations[{i}].keys[{j}]", "a collation");
                if (key.Direction == SortDirection.Unspecified)
                {
                    throw new CatalogValidationException(
                        $"{path}.collations[{i}].keys[{j}]",
                        "the sort direction is unspecified; Calcite compares collations including null "
                        + "direction, so an unspecified one never satisfies an ORDER BY");
                }

                if (!seen.Add(key.Column))
                {
                    throw new CatalogValidationException(
                        $"{path}.collations[{i}].keys[{j}]",
                        $"column {key.Column} appears twice in the same collation");
                }
            }
        }

        var indexNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < table.Indexes.Count; i++)
        {
            var index = table.Indexes[i];
            var indexPath = $"{path}.indexes[{i}]";
            if (string.IsNullOrWhiteSpace(index.Name))
            {
                throw new CatalogValidationException(indexPath, "the index name is empty");
            }

            if (!indexNames.Add(index.Name))
            {
                throw new CatalogValidationException(
                    indexPath, $"index name '{index.Name}' is used twice on table '{table.Name}'");
            }

            if (index.Kind == IndexKind.Unspecified)
            {
                throw new CatalogValidationException(indexPath, "the index kind is unspecified");
            }

            if (index.Columns.Count == 0)
            {
                throw new CatalogValidationException(indexPath, "an index has no columns");
            }

            var indexColumns = new HashSet<int>();
            foreach (var column in index.Columns)
            {
                RequireColumn(table, column, indexPath);
                RequireComparable(table, column, indexPath, "an index key");
                if (!indexColumns.Add(column))
                {
                    throw new CatalogValidationException(
                        indexPath, $"column {column} appears twice in the same index key");
                }
            }

            if (index.Directions.Count != 0 && index.Directions.Count != index.Columns.Count)
            {
                throw new CatalogValidationException(
                    indexPath,
                    $"the index declares {index.Directions.Count} key direction(s) for "
                    + $"{index.Columns.Count} key column(s); declare one per column or none at all");
            }

            for (var d = 0; d < index.Directions.Count; d++)
            {
                if (index.Directions[d] == SortDirection.Unspecified)
                {
                    throw new CatalogValidationException(
                        $"{indexPath}.directions[{d}]",
                        "the sort direction is unspecified; leave the directions empty for the "
                        + "ascending-nulls-last default rather than declaring an unspecified one");
                }
            }

            ValidateCovering(table, index, indexPath, indexColumns);
            ValidatePrefix(table, index, indexPath);
        }

        for (var c = 0; c < table.Columns.Count; c++)
        {
            ValidateStatistics(
                table.Columns[c].Statistics, $"{path}.columns[{c}] ({table.Columns[c].Name})");
        }

        ValidateEntitlement(table, path);
    }

    /// <summary>
    /// <see cref="CatalogOptions.RequireEntitlements"/> (D154): every table either carries an
    /// entitlement or has been marked <see cref="TableDescriptor.IsPublic"/>. For a host that wants
    /// forgetting to be impossible — the point being that a table nobody thought about is refused
    /// while a table somebody decided about is not, and the two are told apart by the marker rather
    /// than by their absence.
    /// </summary>
    private static void RequireEntitlements(SchemaDescriptor schema, string path)
    {
        for (var t = 0; t < schema.Tables.Count; t++)
        {
            var table = schema.Tables[t];
            if (table.Entitlement is not null || table.IsPublic)
            {
                continue;
            }

            throw new CatalogValidationException(
                $"{path}.tables[{t}] ({table.Name})",
                $"CatalogOptions.RequireEntitlements is on and table '{table.Name}' neither carries "
                + "an entitlement nor is marked Public(). Declare one, or say Public() and mean it.");
        }
    }

    /// <summary>
    /// The shape of an entitlement (step 26, <c>docs/design/16-entitlements.md</c> §1). What can be
    /// checked here is structure — column indexes, the expressions a disclosure implies, the
    /// group-size floor. The expressions themselves are SQL text and are parsed and type-checked by
    /// the sidecar at registration, which is the only side with a SQL parser.
    /// </summary>
    private static void ValidateEntitlement(TableDescriptor table, string path)
    {
        if (table.Entitlement is not { } entitlement)
        {
            return;
        }

        if (entitlement.DefaultDisclosure is Disclosure.Masked or Disclosure.AggregateOnly)
        {
            throw new CatalogValidationException(
                $"{path}.entitlement",
                $"default_disclosure is {entitlement.DefaultDisclosure}, which needs a mask or an "
                + "aggregate list this table cannot state for a column it does not name. The default "
                + "is Full or None; anything else is declared per column.");
        }

        var seen = new HashSet<int>();
        for (var c = 0; c < entitlement.Columns.Count; c++)
        {
            var column = entitlement.Columns[c];
            var columnPath = $"{path}.entitlement.columns[{c}]";

            if (column.Column < 0 || column.Column >= table.Columns.Count)
            {
                throw new CatalogValidationException(
                    columnPath,
                    $"column index {column.Column} is out of range for a table of "
                    + $"{table.Columns.Count} columns");
            }

            columnPath = $"{columnPath} ({table.Columns[column.Column].Name})";

            if (!seen.Add(column.Column))
            {
                throw new CatalogValidationException(
                    columnPath,
                    $"column '{table.Columns[column.Column].Name}' is entitled twice; one rule per "
                    + "column, and a rule that resolves several ways is one CASE expression");
            }

            if (column.MinGroupSize < 0)
            {
                throw new CatalogValidationException(
                    $"{columnPath}.min_group_size",
                    $"min_group_size is {column.MinGroupSize}; it must be >= 0 (0 inherits the "
                    + "default the host gives at planning, 1 disables the guard for this column)");
            }

            ValidateRules(entitlement, column, table.Columns[column.Column], columnPath);
        }
    }

    /// <summary>
    /// The rules of one column (D196): every reachable name is checked against what it needs, and
    /// the two whole-column obligations — D208's <c>Otherwise = None</c> on a tenanted table, and
    /// D203's shape check on <c>Statistical</c> — are checked over the set of reachable names.
    /// </summary>
    /// <remarks>
    /// The <c>When</c> conditions are SQL text and are parsed and type-checked by the sidecar, which
    /// is the only side with a parser. What is checkable here is a rule that could never be honoured
    /// whatever the text says.
    /// </remarks>
    private static void ValidateRules(
        TableEntitlementDescriptor entitlement,
        ColumnEntitlementDescriptor column,
        ColumnDescriptor declared,
        string columnPath)
    {
        // D208: on a table whose rows are already filtered, a row outside the predicate is still
        // evaluated by any conjunct the optimiser moves below the filter, so what such a row
        // resolves to must not be the raw value. A column that is FULL for every visible row is
        // written as a rule whose condition is the scope, which is what a compiler emits anyway.
        if (entitlement.RowPredicate.Length > 0 && column.Otherwise != Disclosure.None)
        {
            throw new CatalogValidationException(
                $"{columnPath}.otherwise",
                $"otherwise is {column.Otherwise} on a table that has a row predicate. Every column "
                + "of a table whose rows are filtered must default to None (D208): a row outside "
                + "the predicate is evaluated too, by whatever the optimiser moves below the "
                + "filter. Write the permissive case as a rule whose condition is the scope.");
        }

        var reachable = new HashSet<Disclosure>();
        for (var r = 0; r < column.Rules.Count; r++)
        {
            var rule = column.Rules[r];
            var rulePath = $"{columnPath}.rules[{r}]";

            if (string.IsNullOrWhiteSpace(rule.When))
            {
                throw new CatalogValidationException(
                    rulePath,
                    "the rule condition is empty. A rule that always holds is written 'TRUE'; a "
                    + "column with no condition at all is an otherwise and no rules.");
            }

            if (rule.Then is Disclosure.Unspecified)
            {
                throw new CatalogValidationException(
                    rulePath,
                    "the rule discloses Unspecified, which is not one of the names. Say Full, "
                    + "Masked, AggregateOnly, Test or None.");
            }

            ValidateTests(rule, declared, rulePath);

            // A rule mask only means anything under MASKED: under any other name it would never be
            // reached, and a host that wrote one meant something the descriptor cannot express.
            if (rule.Mask.Length > 0 && rule.Then != Disclosure.Masked)
            {
                throw new CatalogValidationException(
                    rulePath,
                    $"the rule declares a mask but discloses {rule.Then}, so the mask could never be "
                    + "reached. A per-rule mask belongs on a Masked rule.");
            }

            if (rule.Then == Disclosure.Masked && rule.Mask.Length == 0 && column.Mask.Length == 0)
            {
                throw new CatalogValidationException(
                    rulePath,
                    "the rule discloses Masked and neither it nor the column declares a mask, so "
                    + "there would be nothing to put in the value's place");
            }

            reachable.Add(rule.Then);
        }

        reachable.Add(column.Otherwise == Disclosure.Unspecified ? Disclosure.None : column.Otherwise);

        if (reachable.Contains(Disclosure.AggregateOnly) && column.AggregateOnlyFunctions.Count == 0)
        {
            throw new CatalogValidationException(
                columnPath,
                "AggregateOnly is reachable and no aggregate is permitted, so every use of the "
                + "column would be refused. List the aggregates, or say None and mean it.");
        }

        for (var f = 0; f < column.AggregateOnlyFunctions.Count; f++)
        {
            var function = column.AggregateOnlyFunctions[f].Trim().ToUpperInvariant();
            if (!PopulationAggregates.IsPermitted(function))
            {
                throw new CatalogValidationException(
                    $"{columnPath}.aggregate_only_functions[{f}]",
                    $"'{column.AggregateOnlyFunctions[f]}' is not a population aggregate (D190). "
                    + "MIN, MAX, ANY_VALUE, the positional and holistic aggregates, every string "
                    + "aggregate and any user-defined aggregate each report an individual row's "
                    + $"value. The permitted set is {PopulationAggregates.Listing}.");
            }
        }

        if (!reachable.Contains(Disclosure.Masked) && column.Mask.Length > 0)
        {
            var hasRuleMask = false;
            foreach (var rule in column.Rules)
            {
                hasRuleMask |= rule.Mask.Length > 0;
            }

            if (!hasRuleMask)
            {
                throw new CatalogValidationException(
                    columnPath,
                    "a mask is declared but no rule and no default discloses Masked, so the mask "
                    + "could never be reached");
            }
        }

        // D203: statistical is an opt-in *under* a withheld name. On a column that is Full for every
        // row it says nothing, and on one that is only ever None there is no raw value to relax to.
        if (column.Statistical
            && !reachable.Contains(Disclosure.Masked)
            && !reachable.Contains(Disclosure.AggregateOnly))
        {
            throw new CatalogValidationException(
                $"{columnPath}.statistical",
                "statistical is set but neither Masked nor AggregateOnly is reachable, so there is "
                + "no withheld value for it to relax. It is an opt-in under one of those two.");
        }
    }

    /// <summary>
    /// The shapes one rule permits over the column (D261,
    /// <c>docs/design/36-test-verdict.md</c> §1): a <c>Test</c> rule that names none, a shape that
    /// is not one of the three, and a test on a column whose type has no equality.
    /// </summary>
    /// <remarks>
    /// The third is a shape check and not a SQL one — the sidecar's registration validator makes the
    /// same refusal over the converted expressions — because a LIST is the one kind Chalk's own
    /// contract says comparison is not supported on (<c>types.proto</c>, D58), and a rule permitting
    /// a comparison that could never be compiled is a rule the host meant something else by.
    /// </remarks>
    private static void ValidateTests(
        Chalk.Entitlements.DisclosureRule rule, ColumnDescriptor declared, string rulePath)
    {
        if (rule.Then == Disclosure.Test && rule.Tests.Count == 0)
        {
            throw new CatalogValidationException(
                $"{rulePath}.tests",
                "the rule discloses Test and names no comparison shape, so it discloses neither the "
                + "value nor any comparison of it. Name the shapes — Equals, NotEquals, In — or say "
                + "None and mean it.");
        }

        if (rule.Tests.Count > 0 && rule.Then is not (Disclosure.Test or Disclosure.AggregateOnly))
        {
            throw new CatalogValidationException(
                $"{rulePath}.tests",
                $"the rule permits comparison shapes but discloses {rule.Then}, which either "
                + "discloses the value outright or withholds it entirely, so the shapes could never "
                + "be reached. They belong on a Test rule, or on an AggregateOnly rule as the "
                + "FILTER of a permitted aggregate.");
        }

        for (var t = 0; t < rule.Tests.Count; t++)
        {
            if (rule.Tests[t] == Chalk.Entitlements.TestShape.Unspecified)
            {
                throw new CatalogValidationException(
                    $"{rulePath}.tests[{t}]",
                    "the shape is Unspecified, which is not one of the three. Say Equals, NotEquals "
                    + "or In.");
            }
        }

        if (rule.Tests.Count > 0 && !HasEquality(declared.Type))
        {
            throw new CatalogValidationException(
                $"{rulePath}.tests",
                $"column '{declared.Name}' is {declared.Type.Kind}, a type Chalk states no equality "
                + "for (D58), so no comparison of it could be computed at the leaf. A test verdict "
                + "needs a column whose values compare.");
        }
    }

    /// <summary>Whether a value of this type compares for equality at all (<c>types.proto</c>, D58).</summary>
    private static bool HasEquality(ChalkType type) =>
        type.Kind is not (TypeKind.Unspecified or TypeKind.List);

    /// <summary>
    /// Statistics are claims the cost model acts on, so the shape is checked; the *values* are the
    /// source's word and are never second-guessed. -1 is the one legitimate "unknown" (D36).
    /// </summary>
    private static void ValidateStatistics(ColumnStatistics statistics, string path)
    {
        if (statistics.DistinctCount < -1)
        {
            throw new CatalogValidationException(
                path, $"distinct_count is {statistics.DistinctCount}; it must be >= -1 (-1 means unknown)");
        }

        if (statistics.NullCount < -1)
        {
            throw new CatalogValidationException(
                path, $"null_count is {statistics.NullCount}; it must be >= -1 (-1 means unknown)");
        }

        for (var b = 0; b < statistics.Histogram.Count; b++)
        {
            if (statistics.Histogram[b].Count < 0)
            {
                throw new CatalogValidationException(
                    $"{path}.histogram[{b}]", $"the bucket count is {statistics.Histogram[b].Count}; it must be >= 0");
            }

            if (statistics.Histogram[b].DistinctCount < -1)
            {
                throw new CatalogValidationException(
                    $"{path}.histogram[{b}]",
                    $"distinct_count is {statistics.Histogram[b].DistinctCount}; it must be >= -1");
            }
        }

        for (var v = 0; v < statistics.FrequentValues.Count; v++)
        {
            if (statistics.FrequentValues[v].Count < 0)
            {
                throw new CatalogValidationException(
                    $"{path}.frequent_values[{v}]",
                    $"the value count is {statistics.FrequentValues[v].Count}; it must be >= 0");
            }
        }
    }

    /// <summary>
    /// A cost may not be negative, and may not be infinite or NaN: Volcano compares costs and a NaN
    /// makes every comparison false, which is a plan chosen by accident (D38).
    /// </summary>
    private static void ValidateCostProfile(CostProfile profile, string path)
    {
        Check(profile.ScanRowCost, nameof(profile.ScanRowCost));
        Check(profile.LookupSeekCost, nameof(profile.LookupSeekCost));
        Check(profile.LookupRowCost, nameof(profile.LookupRowCost));
        Check(profile.RemoteCallCost, nameof(profile.RemoteCallCost));
        Check(profile.RemoteRowCost, nameof(profile.RemoteRowCost));

        void Check(double cost, string name)
        {
            if (cost < 0 || double.IsNaN(cost) || double.IsInfinity(cost))
            {
                throw new CatalogValidationException(
                    $"{path}.cost_profile",
                    $"{name} is {cost.ToString(System.Globalization.CultureInfo.InvariantCulture)}; "
                    + "a cost must be finite and >= 0 (zero means 'inherit')");
            }
        }
    }

    /// <summary>
    /// The covering set of a clustered index (D257, <c>docs/design/34-clustered-indexes.md</c> §2):
    /// real columns, no duplicates, ascending, and always a superset of the key, because a range seek
    /// binary-searches the key columns in the copy. Empty means every column. Only a clustered index
    /// has a copy to describe, so any other kind declaring one is a registration error rather than a
    /// field the planner would silently ignore.
    /// </summary>
    /// <summary>
    /// A PREFIX index answers <c>LIKE 'p%'</c> on one STRING key column and nothing else (D282). Two
    /// key columns or a key of another type is a shape the planner has no rule for, so it is refused
    /// at registration rather than declared and never matched.
    /// </summary>
    private static void ValidatePrefix(TableDescriptor table, IndexDescriptor index, string indexPath)
    {
        if (index.Kind != IndexKind.Prefix)
        {
            return;
        }

        if (index.Columns.Count != 1)
        {
            throw new CatalogValidationException(
                indexPath,
                $"index '{index.Name}' is PREFIX and declares {index.Columns.Count} key columns. A "
                + "prefix index answers prefix lookups on one STRING column and claims no ordering, "
                + "so it has exactly one key.");
        }

        if (table.Columns[index.Columns[0]].Type.Kind != TypeKind.String)
        {
            throw new CatalogValidationException(
                indexPath,
                $"index '{index.Name}' is PREFIX over column '{table.Columns[index.Columns[0]].Name}', "
                + $"which is {table.Columns[index.Columns[0]].Type.Kind}. A prefix is a question about "
                + "text, so a prefix index's key is a STRING column.");
        }
    }

    private static void ValidateCovering(
        TableDescriptor table, IndexDescriptor index, string indexPath, HashSet<int> keyColumns)
    {
        if (index.Covering.Count == 0)
        {
            return;
        }

        if (index.Kind != IndexKind.Clustered)
        {
            throw new CatalogValidationException(
                indexPath,
                $"index '{index.Name}' is {index.Kind} and declares a covering set, but only a "
                + "CLUSTERED index holds a copy of its columns. Declare the index clustered, or drop "
                + "the covering set.");
        }

        var seen = new HashSet<int>();
        var previous = -1;
        for (var c = 0; c < index.Covering.Count; c++)
        {
            var column = index.Covering[c];
            RequireColumn(table, column, $"{indexPath}.covering[{c}]");
            if (!seen.Add(column))
            {
                throw new CatalogValidationException(
                    $"{indexPath}.covering[{c}]",
                    $"column {column} appears twice in the covering set of index '{index.Name}'");
            }

            if (column <= previous)
            {
                throw new CatalogValidationException(
                    $"{indexPath}.covering[{c}]",
                    $"the covering set of index '{index.Name}' is not ascending: column {column} "
                    + $"follows column {previous}");
            }

            previous = column;
        }

        foreach (var key in keyColumns)
        {
            if (!seen.Contains(key))
            {
                throw new CatalogValidationException(
                    indexPath,
                    $"the covering set of index '{index.Name}' leaves out key column {key} "
                    + $"('{table.Columns[key].Name}'). A range seek binary-searches the key in the "
                    + "copy, so the key columns are always covered.");
            }
        }
    }

    private static void RequireColumn(TableDescriptor table, int column, string path)
    {
        if (column < 0 || column >= table.Columns.Count)
        {
            throw new CatalogValidationException(
                path, $"column index {column} is out of range for a table of {table.Columns.Count} columns");
        }
    }

    /// <summary>
    /// A key column has to be comparable, and a LIST is not (D58): it can be produced, projected and
    /// indexed into, never ordered or grouped. A collation, a unique key or an index over one would
    /// be a promise nothing can keep.
    /// </summary>
    private static void RequireComparable(TableDescriptor table, int column, string path, string what)
    {
        if (table.Columns[column].Type.Kind == TypeKind.List)
        {
            throw new CatalogValidationException(
                path,
                $"column '{table.Columns[column].Name}' is a LIST and cannot be part of {what}; "
                + "v1 lists have no ordering or equality (docs/design/14-windows-ii.md §5)");
        }
    }

    /// <summary>
    /// A composite value's fields (D291): at least one, each named, no two names equal ignoring case — SQL
    /// resolves a field that way — and each a scalar, so a composite value is exactly one level deep.
    /// </summary>
    private static void ValidateCompositeFields(ChalkType type, string path)
    {
        if (type.Fields.Count == 0)
        {
            throw new CatalogValidationException(path, "a COMPOSITE declares no fields; it has at least one");
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var f = 0; f < type.Fields.Count; f++)
        {
            var field = type.Fields[f];
            var fieldPath = $"{path}.fields[{f}]";
            if (string.IsNullOrWhiteSpace(field.Name))
            {
                throw new CatalogValidationException(fieldPath, "the field name is empty");
            }

            fieldPath = $"{fieldPath} ({field.Name})";
            if (!names.Add(field.Name))
            {
                throw new CatalogValidationException(
                    fieldPath,
                    $"field name '{field.Name}' is used twice ignoring case; SQL resolves a field by "
                    + "name ignoring case, so the two would be one field");
            }

            if (field.Type.Kind is TypeKind.List or TypeKind.Composite)
            {
                throw new CatalogValidationException(
                    fieldPath,
                    $"the field is a {field.Type.Kind.ToString().ToUpperInvariant()}; a COMPOSITE is one "
                    + "level deep and its fields are scalars");
            }

            ValidateType(field.Type, fieldPath);
        }
    }

    private static void ValidateType(ChalkType type, string path)
    {
        if (type.Kind == TypeKind.Unspecified)
        {
            throw new CatalogValidationException(path, "the column type is unspecified");
        }

        // D58: a LIST carries an element, exactly one level deep, and nothing else carries one.
        if (type.Kind == TypeKind.List)
        {
            if (type.Element is not { } element)
            {
                throw new CatalogValidationException(path, "a LIST column declares no element type");
            }

            if (element.Kind == TypeKind.List)
            {
                throw new CatalogValidationException(
                    path,
                    "a LIST's element is itself a LIST; v1 lists are exactly one level deep "
                    + "(docs/design/14-windows-ii.md §5)");
            }

            if (element.Kind == TypeKind.Composite)
            {
                throw new CatalogValidationException(
                    path,
                    "a LIST's element is a COMPOSITE; a list holds scalars and a composite is never "
                    + "nested in another");
            }

            ValidateType(element, $"{path}.element");
        }
        else if (type.Element is not null)
        {
            throw new CatalogValidationException(
                path, $"{type.Kind} declares an element type; only a LIST has one");
        }

        // D291: a COMPOSITE carries its fields, one level deep, and nothing else carries any.
        if (type.Kind == TypeKind.Composite)
        {
            ValidateCompositeFields(type, path);
        }
        else if (type.Fields.Count > 0)
        {
            throw new CatalogValidationException(
                path, $"{type.Kind} declares {type.Fields.Count} field(s); only a COMPOSITE has fields");
        }

        if (type.Precision < 0 || type.Scale < 0)
        {
            throw new CatalogValidationException(
                path, $"precision {type.Precision} / scale {type.Scale} must not be negative");
        }

        if (type.Kind == TypeKind.Decimal)
        {
            if (type.Precision is < 1 or > 38)
            {
                throw new CatalogValidationException(path, $"DECIMAL precision is {type.Precision}; it must be 1..38");
            }

            if (type.Scale > type.Precision)
            {
                throw new CatalogValidationException(
                    path, $"DECIMAL scale {type.Scale} exceeds precision {type.Precision}");
            }
        }
        else if (IrTypes.HasPrecision(type.Kind))
        {
            var max = type.Kind == TypeKind.Time ? 6 : 9;
            if (type.Precision > max)
            {
                throw new CatalogValidationException(
                    path, $"{type.Kind} precision is {type.Precision}; the maximum is {max}");
            }

            if (type.Scale != 0)
            {
                throw new CatalogValidationException(path, $"{type.Kind} carries a scale; scale is DECIMAL-only");
            }
        }
        else if (type.Precision != 0 || type.Scale != 0)
        {
            throw new CatalogValidationException(
                path,
                $"{type.Kind} carries precision {type.Precision} / scale {type.Scale}; both must be zero");
        }
    }
}

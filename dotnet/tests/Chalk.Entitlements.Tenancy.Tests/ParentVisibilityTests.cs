using Chalk.Catalog;
using Chalk.Ir;
using Chalk.Sources.Poco;
using CatalogContext = Chalk.Catalog.CatalogContext;

namespace Chalk.Entitlements.Tenancy.Tests;

/// <summary>
/// What a path takes from its parents (D358): the parent's visibility by default, so a role a
/// parent's rule leaves out reads nothing along the path, and the parent's value of the kind, as a
/// marking, for the roles the path names exempt.
/// </summary>
/// <remarks>
/// <para>
/// The schema is this file's own and has two families. In the first, an order holds its
/// organisation and is visible to a manager alone; a line takes its organisation from its order and
/// its vendor from its item, and a note its organisation from its line. In the second, an asset holds
/// a classification and a releasability and is visible to asset readers alone; a tasking holds its
/// mission and takes the other two from its asset, which is what a tasking reader is meant to read
/// it by.
/// </para>
/// <para>
/// A keeper is the role the order's rule leaves out, and a supplier the role the item's does.
/// </para>
/// </remarks>
public sealed class ParentVisibilityTests
{
    private sealed record Order(int Id, int OrgId);

    private sealed record Item(int Id, int VendorId);

    private sealed record Line(int Id, int OrderId, int ItemId, int Qty);

    private sealed record Note(int Id, int LineId);

    private sealed record Asset(int Id, int Classification, int Releasability);

    private sealed record Tasking(int Id, int MissionId, int AssetId);

    private static SchemaDescriptor Schema { get; } = Build();

    private static CatalogContext Catalog { get; } = new()
    {
        ContextId = "parent-visibility",
        Epoch = 1,
        Schemas = [Schema],
    };

    private static SchemaDescriptor Build()
    {
        var builder = new PocoSourceBuilder("mem").NamingPolicy(PocoNamingPolicy.SnakeCase);
        builder.AddTable("orders", Array.Empty<Order>(), t => t.OrderedBy(o => o.Id).UniqueKey(o => o.Id));
        builder.AddTable("items", Array.Empty<Item>(), t => t.OrderedBy(i => i.Id).UniqueKey(i => i.Id));
        builder.AddTable("lines", Array.Empty<Line>(), t =>
        {
            t.OrderedBy(l => l.Id).UniqueKey(l => l.Id);
            t.ForeignKey(l => l.OrderId).References<Order>(o => o.Id, verify: true);
            t.ForeignKey(l => l.ItemId).References<Item>(i => i.Id, verify: true);
        });
        builder.AddTable("notes", Array.Empty<Note>(), t =>
        {
            t.OrderedBy(n => n.Id).UniqueKey(n => n.Id);
            t.ForeignKey(n => n.LineId).References<Line>(l => l.Id, verify: true);
        });
        builder.AddTable("assets", Array.Empty<Asset>(), t => t.OrderedBy(a => a.Id).UniqueKey(a => a.Id));
        builder.AddTable("taskings", Array.Empty<Tasking>(), t =>
        {
            t.OrderedBy(x => x.Id).UniqueKey(x => x.Id);
            t.ForeignKey(x => x.AssetId).References<Asset>(a => a.Id, verify: true);
        });
        return builder.Build().DescribeSchema();
    }

    /// <summary>One policy over this file's schema, and the handles it is written with.</summary>
    private sealed class Declaration
    {
        internal Declaration()
        {
            Policy = TenancyPolicy.Declare(Catalog);
            var source = Policy.Source(Schema.Name);
            Manager = Policy.Role("manager");
            Keeper = Policy.Role("keeper");
            Supplier = Policy.Role("supplier");
            AssetReader = Policy.Role("asset_reader");
            TaskingReader = Policy.Role("tasking_reader");
            Org = Policy.Tenancy("org");
            Vendor = Policy.Tenancy("vendor");
            Classification = Policy.Tenancy("classification");
            Releasability = Policy.Tenancy("releasability");
            Mission = Policy.Tenancy("mission");
            Interactive = Policy.Scope("interactive");
            Batch = Policy.Scope("batch");
            Orders = source.Table("orders");
            Items = source.Table("items");
            Lines = source.Table("lines");
            Notes = source.Table("notes");
            Assets = source.Table("assets");
            Taskings = source.Table("taskings");
        }

        internal TenancyPolicy Policy { get; }

        internal Role Manager { get; }

        internal Role Keeper { get; }

        internal Role Supplier { get; }

        internal Role AssetReader { get; }

        internal Role TaskingReader { get; }

        internal Kind Org { get; }

        internal Kind Vendor { get; }

        internal Kind Classification { get; }

        internal Kind Releasability { get; }

        internal Kind Mission { get; }

        internal Scope Interactive { get; }

        internal Scope Batch { get; }

        internal Table Orders { get; }

        internal Table Items { get; }

        internal Table Lines { get; }

        internal Table Notes { get; }

        internal Table Assets { get; }

        internal Table Taskings { get; }

        internal TenancyEntitlements Compile() => Policy.Compile([Schema]);

        /// <summary>The orders: visible to a manager alone, in <paramref name="scopes"/> where any are named.</summary>
        internal void OrdersForManagers(params Scope[] scopes) =>
            Orders
                .Tenancy(t => t.Direct(Org, Orders.Column("org_id")))
                .Visible(new VisibilityRule { Roles = [Manager], Scopes = scopes });

        /// <summary>The items: every role reads them.</summary>
        internal void ItemsForAll() => Items.Tenancy(t => t.Direct(Vendor, Items.Column("vendor_id")));

        /// <summary>The assets: visible to asset readers alone.</summary>
        internal void AssetsForAssetReaders() =>
            Assets
                .Tenancy(t => t
                    .Direct(Classification, Assets.Column("classification"))
                    .Direct(Releasability, Assets.Column("releasability")))
                .Visible(new VisibilityRule { Roles = [AssetReader] });
    }

    private static string EndpointOf(TenancyEntitlements entitlements, string table, string kind) =>
        entitlements.For(Schema.Name, table)!.Inherited.Single(path => path.Kind == kind).EndpointPredicate;

    private static CatalogValidationException Refused(Action action) =>
        Assert.IsType<CatalogValidationException>(Record.Exception(action));

    // ------------------------------------------------------------------ the default

    /// <summary>
    /// A line admits every role, and takes its organisation from an order only a manager may read:
    /// along that path the keeper has no term, and along the item's, whose rule leaves nobody out,
    /// it has its own.
    /// </summary>
    [Fact]
    public void A_role_a_parent_leaves_out_has_no_term_along_the_path()
    {
        var d = new Declaration();
        d.OrdersForManagers();
        d.ItemsForAll();
        d.Lines.Tenancy(t => t
            .Inherited(d.Org).Through(d.Orders)
            .Inherited(d.Vendor).Through(d.Items));

        var compiled = d.Compile();

        var org = EndpointOf(compiled, "lines", "org");
        Assert.Contains("org_id IN (@ctx.org_manager)", org, StringComparison.Ordinal);
        Assert.DoesNotContain("keeper", org, StringComparison.Ordinal);
        Assert.DoesNotContain("supplier", org, StringComparison.Ordinal);
        Assert.Contains("vendor_id IN (@ctx.vendor_keeper)", EndpointOf(compiled, "lines", "vendor"), StringComparison.Ordinal);
        Assert.Empty(compiled.Exemptions);
    }

    /// <summary>
    /// Named exempt, the keeper takes the order's organisation as a marking: its term is back, and
    /// the policy lists the exemption for a review to read.
    /// </summary>
    [Fact]
    public void An_exempt_role_takes_the_parents_value_as_a_marking()
    {
        var d = new Declaration();
        d.OrdersForManagers();
        d.ItemsForAll();
        d.Lines.Tenancy(t => t
            .Inherited(d.Org).Through(d.Orders).ExemptFromParentVisibility(d.Keeper)
            .Inherited(d.Vendor).Through(d.Items));

        var compiled = d.Compile();

        Assert.Contains("org_id IN (@ctx.org_keeper)", EndpointOf(compiled, "lines", "org"), StringComparison.Ordinal);
        Assert.DoesNotContain("supplier", EndpointOf(compiled, "lines", "org"), StringComparison.Ordinal);
        var exemption = Assert.Single(compiled.Exemptions);
        Assert.Equal(("lines", "org", "orders"), (exemption.Table, exemption.Kind, exemption.Endpoint));
        Assert.Equal(["keeper"], exemption.Roles);
    }

    /// <summary>
    /// A line entitled along the order's path alone, with no rule of its own: the keeper it admits
    /// would read none of it ever, which is refused with the clause that would let it in.
    /// </summary>
    [Fact]
    public void A_role_left_with_no_way_in_is_refused_when_the_policy_compiles()
    {
        var d = new Declaration();
        d.OrdersForManagers();
        d.Lines.Tenancy(t => t.Inherited(d.Org).Through(d.Orders));

        var refused = Refused(() => d.Compile());

        Assert.Equal(ChalkErrorCodes.ParentVisibilityLeavesRoleOut, refused.Code);
        Assert.Contains(
            "'lines' admits 'keeper', which reaches it only through 'orders', whose visibility rule "
            + "leaves it out. Declare .ExemptFromParentVisibility(keeper) on the path, or give "
            + "'lines' a visibility rule without 'keeper': with none of its own, it admits every role.",
            refused.Message,
            StringComparison.Ordinal);
    }

    /// <summary>Admitting only the order's roles, the same line compiles: nobody is left out.</summary>
    [Fact]
    public void A_child_that_admits_its_parents_roles_compiles()
    {
        var d = new Declaration();
        d.OrdersForManagers();
        d.Lines
            .Tenancy(t => t.Inherited(d.Org).Through(d.Orders))
            .Visible(new VisibilityRule { Roles = [d.Manager] });

        Assert.Contains("org_manager", EndpointOf(d.Compile(), "lines", "org"), StringComparison.Ordinal);
    }

    /// <summary>
    /// A host predicate beside the path may admit any principal at all, so a table that has one is
    /// not judged to leave a role no way in.
    /// </summary>
    [Fact]
    public void A_table_with_a_host_predicate_is_not_judged()
    {
        var d = new Declaration();
        d.OrdersForManagers();
        d.Lines.Tenancy(t => t.Inherited(d.Org).Through(d.Orders).Predicate(Sql.Of("qty > 0")));

        Assert.DoesNotContain("keeper", EndpointOf(d.Compile(), "lines", "org"), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ the exemption and the binding

    /// <summary>
    /// Bound by the order's rule is what the keeper is anyway: the text is the default's, and the
    /// name is checked.
    /// </summary>
    [Fact]
    public void Bound_states_the_default()
    {
        var plain = new Declaration();
        plain.OrdersForManagers();
        plain.ItemsForAll();
        plain.Lines.Tenancy(t => t
            .Inherited(plain.Org).Through(plain.Orders)
            .Inherited(plain.Vendor).Through(plain.Items));

        var bound = new Declaration();
        bound.OrdersForManagers();
        bound.ItemsForAll();
        bound.Lines.Tenancy(t => t
            .Inherited(bound.Org).Through(bound.Orders).BoundByParentVisibility(bound.Keeper)
            .Inherited(bound.Vendor).Through(bound.Items));

        Assert.Equal(EndpointOf(plain.Compile(), "lines", "org"), EndpointOf(bound.Compile(), "lines", "org"));
    }

    /// <summary>
    /// A name that changes nothing is refused when the policy compiles: the manager, whom the order
    /// admits, is neither exempt from its rule nor bound by it, and a role the line does not admit
    /// reads nothing of it either way.
    /// </summary>
    [Fact]
    public void A_name_that_changes_nothing_is_refused()
    {
        var exempt = new Declaration();
        exempt.OrdersForManagers();
        exempt.ItemsForAll();
        exempt.Lines.Tenancy(t => t
            .Inherited(exempt.Org).Through(exempt.Orders).ExemptFromParentVisibility(exempt.Manager)
            .Inherited(exempt.Vendor).Through(exempt.Items));
        var notLeftOut = Refused(() => exempt.Compile());
        Assert.Equal(ChalkErrorCodes.InvalidPolicy, notLeftOut.Code);
        Assert.Contains(
            "ExemptFromParentVisibility(manager) on Inherited(\"org\") to 'orders' on 'lines' names a "
            + "role that no table the path reads leaves out, so it changes nothing: the path reaches "
            + "'manager' as it reaches any role its parents admit. Remove 'manager' from that "
            + "ExemptFromParentVisibility.",
            notLeftOut.Message,
            StringComparison.Ordinal);

        var bound = new Declaration();
        bound.OrdersForManagers();
        bound.ItemsForAll();
        bound.Lines.Tenancy(t => t
            .Inherited(bound.Org).Through(bound.Orders).BoundByParentVisibility(bound.Manager)
            .Inherited(bound.Vendor).Through(bound.Items));
        Assert.Contains(
            "BoundByParentVisibility(manager)",
            Refused(() => bound.Compile()).Message,
            StringComparison.Ordinal);

        var notAdmitted = new Declaration();
        notAdmitted.OrdersForManagers();
        notAdmitted.Lines
            .Tenancy(t => t.Inherited(notAdmitted.Org).Through(notAdmitted.Orders)
                .ExemptFromParentVisibility(notAdmitted.Keeper))
            .Visible(new VisibilityRule { Roles = [notAdmitted.Manager] });
        Assert.Contains(
            "ExemptFromParentVisibility(keeper) on Inherited(\"org\") to 'orders' on 'lines' names a "
            + "role 'lines' does not admit",
            Refused(() => notAdmitted.Compile()).Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// What no policy could mean is refused where it is written: no role, a role named twice, one
    /// role both ways, and a role that stands for a relation to a row.
    /// </summary>
    [Fact]
    public void A_name_no_policy_could_mean_is_refused_where_it_is_written()
    {
        static CatalogValidationException Writing(Func<Declaration, TenancyPath, TenancyPath> named)
        {
            var d = new Declaration();
            return Refused(() => d.Lines.Tenancy(t => named(d, t.Inherited(d.Org).Through(d.Orders))));
        }

        Assert.Contains(
            "names no role",
            Writing((_, path) => path.ExemptFromParentVisibility()).Message,
            StringComparison.Ordinal);
        Assert.Equal(
            ChalkErrorCodes.DuplicateName,
            Writing((d, path) => path.ExemptFromParentVisibility(d.Keeper, d.Keeper)).Code);
        Assert.Contains(
            "both in ExemptFromParentVisibility and in BoundByParentVisibility",
            Writing((d, path) => path.ExemptFromParentVisibility(d.Keeper).BoundByParentVisibility(d.Keeper)).Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "stands for a relation to a row",
            Writing((_, path) => path.ExemptFromParentVisibility(Roles.Owner)).Message,
            StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ explicit parent visibility

    /// <summary>
    /// Required to be explicit, the policy is refused until the line's path to the order names the
    /// keeper one way or the other — and compiles once it does, either way.
    /// </summary>
    [Fact]
    public void A_policy_that_requires_explicit_parent_visibility_has_every_role_named()
    {
        static Declaration Policy(Func<Declaration, TenancyPath, TenancyPath> named)
        {
            var d = new Declaration();
            d.Policy.RequireExplicitParentVisibility();
            d.OrdersForManagers();
            d.ItemsForAll();
            d.Lines.Tenancy(t => named(d, t.Inherited(d.Org).Through(d.Orders))
                .Inherited(d.Vendor).Through(d.Items));
            return d;
        }

        var unnamed = Refused(() => Policy((_, path) => path).Compile());
        Assert.Equal(ChalkErrorCodes.ParentVisibilityLeavesRoleOut, unnamed.Code);
        Assert.Contains(
            "'lines' admits 'keeper', which 'orders' leaves out. This policy requires explicit parent "
            + "visibility: declare .ExemptFromParentVisibility(keeper) or .BoundByParentVisibility(keeper) "
            + "on Inherited(\"org\") to 'orders' on 'lines', the path through 'orders'.",
            unnamed.Message,
            StringComparison.Ordinal);

        // Every other role the line admits is left out by the order too, and named with the keeper.
        static TenancyPath Every(Declaration d, TenancyPath path, bool exempt) =>
            exempt
                ? path.ExemptFromParentVisibility(d.Keeper).BoundByParentVisibility(d.Supplier, d.AssetReader, d.TaskingReader)
                : path.BoundByParentVisibility(d.Keeper, d.Supplier, d.AssetReader, d.TaskingReader);

        Assert.DoesNotContain(
            "keeper",
            EndpointOf(Policy((d, path) => Every(d, path, exempt: false)).Compile(), "lines", "org"),
            StringComparison.Ordinal);
        Assert.Contains(
            "org_keeper",
            EndpointOf(Policy((d, path) => Every(d, path, exempt: true)).Compile(), "lines", "org"),
            StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ scopes

    /// <summary>
    /// The order holds only in the interactive scope, and the line in every scope: along the path a
    /// manager's term carries the order's scope test. A role named exempt carries none of it.
    /// </summary>
    [Fact]
    public void A_parents_scope_test_holds_along_the_path()
    {
        var d = new Declaration();
        d.OrdersForManagers(d.Interactive);
        d.ItemsForAll();
        d.Lines.Tenancy(t => t
            .Inherited(d.Org).Through(d.Orders).ExemptFromParentVisibility(d.Keeper)
            .Inherited(d.Vendor).Through(d.Items));

        var org = EndpointOf(d.Compile(), "lines", "org");

        Assert.Contains(
            "((org_id IN (@ctx.org_manager) OR @ctx.global_manager) AND (@ctx.scope = 'interactive'))",
            org,
            StringComparison.Ordinal);
        Assert.Contains("(org_id IN (@ctx.org_keeper) OR @ctx.global_keeper)", org, StringComparison.Ordinal);
        Assert.DoesNotContain("keeper) AND (@ctx.scope", org, StringComparison.Ordinal);
    }

    /// <summary>
    /// A line visible in the batch scope alone holds there along its paths too, as it does on its own
    /// row: the endpoint predicate carries the line's own scope test.
    /// </summary>
    [Fact]
    public void A_tables_own_scope_test_holds_along_its_paths()
    {
        var d = new Declaration();
        d.OrdersForManagers();
        d.Lines
            .Tenancy(t => t.Inherited(d.Org).Through(d.Orders))
            .Visible(new VisibilityRule { Roles = [d.Manager], Scopes = [d.Batch] });

        Assert.Equal(
            "((org_id IN (@ctx.org_manager) OR @ctx.global_manager) AND (@ctx.scope = 'batch'))",
            EndpointOf(d.Compile(), "lines", "org"));
    }

    // ------------------------------------------------------------------ longer paths, and related ones

    /// <summary>
    /// A note takes its organisation from its line, which takes it from its order. Where the line's
    /// own path exempts the keeper, the note's reads the order's rule as the line's does, and the
    /// keeper reaches the note as it reaches the line; where it does not, the keeper has no term
    /// along the note's path either.
    /// </summary>
    [Fact]
    public void A_path_that_runs_on_through_a_parents_path_reads_the_rules_as_that_path_does()
    {
        var exempting = new Declaration();
        exempting.OrdersForManagers();
        exempting.ItemsForAll();
        exempting.Lines.Tenancy(t => t
            .Inherited(exempting.Org).Through(exempting.Orders).ExemptFromParentVisibility(exempting.Keeper)
            .Inherited(exempting.Vendor).Through(exempting.Items));
        exempting.Notes.Tenancy(t => t
            .Inherited(exempting.Org).Through(exempting.Lines)
            .Inherited(exempting.Vendor).Through(exempting.Lines));

        Assert.Contains("org_keeper", EndpointOf(exempting.Compile(), "notes", "org"), StringComparison.Ordinal);

        var binding = new Declaration();
        binding.OrdersForManagers();
        binding.ItemsForAll();
        binding.Lines.Tenancy(t => t
            .Inherited(binding.Org).Through(binding.Orders)
            .Inherited(binding.Vendor).Through(binding.Items));
        binding.Notes.Tenancy(t => t
            .Inherited(binding.Org).Through(binding.Lines)
            .Inherited(binding.Vendor).Through(binding.Lines));

        var compiled = binding.Compile();
        Assert.DoesNotContain("keeper", EndpointOf(compiled, "notes", "org"), StringComparison.Ordinal);
        Assert.Contains("vendor_keeper", EndpointOf(compiled, "notes", "vendor"), StringComparison.Ordinal);
    }

    /// <summary>
    /// Along a <c>Related</c> path the bridge is not consulted, and the endpoint is: an order related
    /// to the vendor of an item one of its lines carries reaches a supplier, whom the item admits and
    /// the line does not, and no keeper, whom the item leaves out.
    /// </summary>
    [Fact]
    public void Along_a_related_path_the_endpoints_rule_holds_and_the_bridges_is_not_read()
    {
        var d = new Declaration();
        d.Items
            .Tenancy(t => t.Direct(d.Vendor, d.Items.Column("vendor_id")))
            .Visible(new VisibilityRule { Roles = [d.Supplier] });
        d.Lines
            .Tenancy(t => t.Inherited(d.Vendor).Through(d.Items))
            .Visible(new VisibilityRule { Roles = [d.Supplier] });
        d.Orders.Tenancy(t => t
            .Direct(d.Org, d.Orders.Column("org_id"))
            .Related(d.Vendor).Through(d.Lines).Through(d.Items));

        var vendor = EndpointOf(d.Compile(), "orders", "vendor");

        Assert.Contains("vendor_id IN (@ctx.vendor_supplier)", vendor, StringComparison.Ordinal);
        Assert.DoesNotContain("keeper", vendor, StringComparison.Ordinal);
        Assert.DoesNotContain("manager", vendor, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ combinations and grants

    /// <summary>
    /// A tasking reader's grants hold a classification, a releasability and a mission together, and
    /// only the path through the asset answers them: declared, that is refused when the policy
    /// compiles, as the coalition model would be without its exemptions, and compiles with them.
    /// </summary>
    [Fact]
    public void A_combination_answered_only_through_a_parent_that_leaves_its_role_out_is_refused()
    {
        static Declaration Policy(bool exempt)
        {
            var d = new Declaration();
            d.AssetsForAssetReaders();
            d.Taskings
                .Tenancy(t =>
                {
                    var classification = t
                        .Direct(d.Mission, d.Taskings.Column("mission_id"))
                        .Inherited(d.Classification).Through(d.Assets);
                    var releasability = (exempt
                            ? classification.ExemptFromParentVisibility(d.TaskingReader)
                            : classification)
                        .Inherited(d.Releasability).Through(d.Assets);
                    if (exempt)
                    {
                        releasability.ExemptFromParentVisibility(d.TaskingReader);
                    }
                })
                .Visible(new VisibilityRule { Roles = [d.TaskingReader] });
            d.Policy
                .Combination([d.AssetReader], d.Classification, d.Releasability)
                .Combination([d.TaskingReader], d.Classification, d.Releasability, d.Mission);
            return d;
        }

        var refused = Refused(() => Policy(exempt: false).Compile());
        Assert.Equal(ChalkErrorCodes.ParentVisibilityLeavesRoleOut, refused.Code);
        Assert.Contains(
            "'taskings' admits 'tasking_reader', which holds the combination {classification, "
            + "releasability, mission} there only through 'assets', whose visibility rule leaves it out. "
            + "Declare .ExemptFromParentVisibility(tasking_reader) on the path, or remove "
            + "'tasking_reader' from the visibility rule of 'taskings'.",
            refused.Message,
            StringComparison.Ordinal);

        Assert.Equal(2, Policy(exempt: true).Compile().Exemptions.Count);
    }

    /// <summary>
    /// Without combinations, the compiler cannot see that a tasking reader's grants need the asset's
    /// kinds — the tasking holds its mission on its own row, so the role keeps a route — and the
    /// grant is refused when it is bound, naming the path and the clause.
    /// </summary>
    [Fact]
    public void Without_combinations_a_grant_left_with_no_way_in_is_refused_when_it_is_bound()
    {
        var d = new Declaration();
        d.AssetsForAssetReaders();
        d.Taskings
            .Tenancy(t => t
                .Direct(d.Mission, d.Taskings.Column("mission_id"))
                .Inherited(d.Classification).Through(d.Assets)
                .Inherited(d.Releasability).Through(d.Assets))
            .Visible(new VisibilityRule { Roles = [d.TaskingReader] });
        var compiled = d.Compile();

        var refused = Refused(() => compiled.Bind(new TenancyPrincipal
        {
            User = 1,
            Grants =
            [
                Grant.ForTenancy(d.Classification, 2, d.TaskingReader)
                    .Within(d.Releasability, 7)
                    .Within(d.Mission, 3),
            ],
        }));

        Assert.Equal(ChalkErrorCodes.InvalidGrant, refused.Code);
        Assert.Contains(
            "'taskings' takes 'classification' only through 'assets', whose visibility rule leaves "
            + "'tasking_reader' out, so it answers nothing there in that role: declare "
            + ".ExemptFromParentVisibility(tasking_reader) on that path",
            refused.Message,
            StringComparison.Ordinal);

        // A grant on the mission alone still reaches the taskings, by their own row.
        _ = compiled.Bind(new TenancyPrincipal
        {
            User = 1,
            Grants = [Grant.ForTenancy(d.Mission, 3, d.TaskingReader)],
        });
    }

    // ------------------------------------------------------------------ rules and the prediction

    /// <summary>
    /// A rule naming the keeper reads its vendor along the item's path, and nothing along the order's,
    /// whose rule leaves the keeper out: no term there, as there is no row.
    /// </summary>
    [Fact]
    public void A_rule_reads_nothing_along_a_path_its_role_is_kept_from()
    {
        var d = new Declaration();
        d.OrdersForManagers();
        d.ItemsForAll();
        d.Lines
            .Tenancy(t => t
                .Inherited(d.Org).Through(d.Orders)
                .Inherited(d.Vendor).Through(d.Items))
            .Access(new AccessRule { Roles = [d.Keeper], Column = d.Lines.Column("qty"), Grants = Verdict.Full });

        var rule = Assert.Single(d.Compile().For(Schema.Name, "lines")!.Columns).Rules.Single();

        Assert.Contains("items.vendor_id IN (@ctx.vendor_keeper)", rule.When, StringComparison.Ordinal);
        Assert.DoesNotContain("org_keeper", rule.When, StringComparison.Ordinal);
    }

    /// <summary>
    /// The prediction reads the order's rule as the compiler does: a keeper holding the organisation
    /// alone has no way to a line, and its rule is never met — unless the path names it exempt.
    /// </summary>
    [Fact]
    public void The_prediction_reads_the_parents_rule_as_the_compiler_does()
    {
        static TenancyEntitlements Policy(bool exempt)
        {
            var d = new Declaration();
            d.OrdersForManagers();
            d.ItemsForAll();
            d.Lines
                .Tenancy(t =>
                {
                    var org = t.Inherited(d.Org).Through(d.Orders);
                    (exempt ? org.ExemptFromParentVisibility(d.Keeper) : org)
                        .Inherited(d.Vendor).Through(d.Items);
                })
                .Access(new AccessRule { Roles = [d.Keeper], Column = d.Lines.Column("qty"), Grants = Verdict.Full });
            return d.Compile();
        }

        Assert.Equal(DisclosureOutcome.Redacted, QtyFor(Policy(exempt: false)));
        Assert.Equal(DisclosureOutcome.Full, QtyFor(Policy(exempt: true)));

        static DisclosureOutcome QtyFor(TenancyEntitlements entitlements)
        {
            var keeper = new TenancyPrincipal
            {
                User = 1,
                Grants = [Grant.ForTenancy(entitlements.Policy.Tenancy("org"), 1, entitlements.Policy.Role("keeper"))],
            };
            foreach (var difference in entitlements.Reconcile(LinesRead(), keeper))
            {
                if (difference.Column == "qty")
                {
                    return difference.Expected;
                }
            }

            return DisclosureOutcome.Full;
        }
    }

    /// <summary>A plan of one read of <c>lines</c>, claiming every column in full.</summary>
    private static Plan LinesRead()
    {
        var read = new Read { Table = new TableRef { SourceId = "", Schema = Schema.Name, Table = "lines" } };
        for (var i = 0; i < 4; i++)
        {
            read.Projection.Add((uint)i);
            read.Disclosures.Add(new ColumnDisclosure { Column = (uint)i, Outcome = DisclosureOutcome.Full });
        }

        return new Plan { Root = new Rel { Read = read } };
    }
}

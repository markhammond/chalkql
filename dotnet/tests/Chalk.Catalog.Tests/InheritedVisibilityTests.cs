using Chalk.Entitlements;
using Chalk.Ir;
using Disclosure = Chalk.Entitlements.Disclosure;
using StepDirection = Chalk.Entitlements.StepDirection;

namespace Chalk.Catalog.Tests;

/// <summary>
/// Visibility along a declared path, as much of it as the client can see
/// (<c>docs/design/38-existential-visibility.md</c> §2, §3, D265): the descriptor field, its place
/// in the content hash, and the shape checks that need no SQL parser.
/// </summary>
/// <remarks>
/// Three tables play three parts, on the tree's neutral domain: <c>orders</c> is the target,
/// <c>order_items</c> the bridge whose rows carry the relationship and contribute existence and
/// nothing else, and <c>vendors</c> the endpoint where the kind is held directly.
/// </remarks>
public sealed class InheritedVisibilityTests
{
    private static ColumnDescriptor Col(string name, ChalkType type) => new() { Name = name, Type = type };

    private static TableDescriptor Orders(TableEntitlementDescriptor? entitlement) => new()
    {
        Name = "orders",
        RowCount = 200,
        Entitlement = entitlement,
        UniqueKeys = [new UniqueKeyDescriptor { Columns = [0] }],
        Columns = [Col("id", ChalkType.Int32()), Col("org_id", ChalkType.Int32())],
    };

    private static TableDescriptor Vendors(TableEntitlementDescriptor? entitlement) => new()
    {
        Name = "vendors",
        RowCount = 20,
        Entitlement = entitlement,
        UniqueKeys = [new UniqueKeyDescriptor { Columns = [0] }],
        Columns = [Col("id", ChalkType.Int32()), Col("name", ChalkType.String())],
    };

    /// <summary>The bridge: a key to the target, a key to the endpoint, and a value column.</summary>
    private static TableDescriptor OrderItems(
        TableEntitlementDescriptor? entitlement = null, bool foreignKeys = true) => new()
    {
        Name = "order_items",
        RowCount = 900,
        Entitlement = entitlement,
        UniqueKeys = [new UniqueKeyDescriptor { Columns = [0] }],
        ForeignKeys = foreignKeys
            ?
            [
                new ForeignKeyDescriptor
                {
                    Name = "fk_item_order",
                    Columns = [1],
                    ParentTable = "orders",
                    ParentColumns = [0],
                },
                new ForeignKeyDescriptor
                {
                    Name = "fk_item_vendor",
                    Columns = [2],
                    ParentTable = "vendors",
                    ParentColumns = [0],
                },
            ]
            : [],
        Columns =
        [
            Col("id", ChalkType.Int32()),
            Col("order_id", ChalkType.Int32()),
            Col("vendor_id", ChalkType.Int32()),
            Col("quantity", ChalkType.Int32()),
        ],
    };

    private static TableEntitlementDescriptor Restricted(string predicate) =>
        new() { RowPredicate = predicate };

    private static VisibilityStepDescriptor Up(string table, int from, int to) =>
        new() { Table = table, FromColumn = from, ToColumn = to, Direction = StepDirection.ToChild };

    private static VisibilityStepDescriptor Down(string table, int from, int to) =>
        new() { Table = table, FromColumn = from, ToColumn = to, Direction = StepDirection.ToParent };

    /// <summary>The marketplace shape as layer A carries it: one up-step, then one down-step.</summary>
    private static TableEntitlementDescriptor Related(
        IReadOnlyList<VisibilityStepDescriptor>? steps = null,
        string endpoint = "vendors",
        string predicate = "id IN (@ctx.vendor_vendor)",
        string kind = "vendor") => new()
        {
            RowPredicate = "org_id IN (@ctx.org_manager)",
            Inherited =
            [
                new InheritedVisibilityDescriptor
                {
                    Kind = kind,
                    Steps = steps ?? [Up("order_items", 0, 1), Down("vendors", 2, 0)],
                    EndpointPredicate = predicate,
                    EndpointTable = endpoint,
                },
            ],
        };

    private static CatalogContext Catalog(params TableDescriptor[] tables) => new()
    {
        ContextId = "demo",
        Epoch = 1,
        Schemas =
        [
            new SchemaDescriptor
            {
                SourceId = "mem",
                Name = "main",
                Kind = SourceKind.Local,
                Tables = tables,
            },
        ],
    };

    private static CatalogContext Example(
        TableEntitlementDescriptor? orders = null,
        TableEntitlementDescriptor? vendors = null,
        TableDescriptor? bridge = null) =>
        Catalog(
            Orders(orders ?? Related()),
            Vendors(vendors ?? Restricted("id IN (@ctx.vendor_vendor)")),
            bridge ?? OrderItems());

    [Fact]
    public void The_example_validates()
    {
        CatalogValidator.Validate(Example());
    }

    // ------------------------------------------------------------------ the content hash

    /// <summary>
    /// The path is part of what the descriptor <em>is</em>, so it is in the hash: a policy that
    /// changed the route or the endpoint predicate would otherwise be the same plan.
    /// </summary>
    [Fact]
    public void The_path_is_in_the_content_hash()
    {
        var bare = Restricted("org_id IN (@ctx.org_manager)");
        var path = Related();

        Assert.NotEqual(bare.DescriptorHash, path.DescriptorHash);
        Assert.NotEqual(path.DescriptorHash, Related(kind: "supplier").DescriptorHash);
        Assert.NotEqual(path.DescriptorHash, Related(predicate: "TRUE").DescriptorHash);
        Assert.NotEqual(
            path.DescriptorHash,
            Related(steps: [Up("order_items", 0, 1), Down("vendors", 1, 0)]).DescriptorHash);
        Assert.Equal(path.DescriptorHash, Related().DescriptorHash);
    }

    /// <summary>
    /// A descriptor without one hashes exactly as it did before the field existed — the paths are
    /// appended only where there are any, so zero-cost when unused holds of the hash too.
    /// </summary>
    [Fact]
    public void A_descriptor_without_a_path_hashes_as_it_did()
    {
        Assert.Equal(
            "3f83699ed1e7be5d60576bf5a595350f",
            new TableEntitlementDescriptor { RowPredicate = "org_id IN (@ctx.manager_orgs)" }
                .DescriptorHash);
    }

    // ------------------------------------------------------------------ the wire

    [Fact]
    public void The_path_round_trips_through_the_wire()
    {
        var round = CatalogSerialization.FromProto(CatalogSerialization.ToProto(Example()));
        var paths = round.Schemas[0].Tables[0].Entitlement!.Inherited;

        var path = Assert.Single(paths);
        Assert.Equal("vendor", path.Kind);
        Assert.Equal("id IN (@ctx.vendor_vendor)", path.EndpointPredicate);
        Assert.Equal("", path.EndpointSchema);
        Assert.Equal("vendors", path.EndpointTable);
        Assert.Collection(
            path.Steps,
            up =>
            {
                Assert.Equal("order_items", up.Table);
                Assert.Equal(0, up.FromColumn);
                Assert.Equal(1, up.ToColumn);
                Assert.Equal(StepDirection.ToChild, up.Direction);
            },
            down =>
            {
                Assert.Equal("vendors", down.Table);
                Assert.Equal(2, down.FromColumn);
                Assert.Equal(0, down.ToColumn);
                Assert.Equal(StepDirection.ToParent, down.Direction);
            });
    }

    // ------------------------------------------------------------------ what the client refuses

    [Fact]
    public void A_path_with_no_step_is_refused()
    {
        var refusal = Assert.Throws<CatalogValidationException>(
            () => CatalogValidator.Validate(Example(orders: Related(steps: []))));

        Assert.Contains("has no step", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_step_to_a_table_the_catalog_does_not_hold_is_refused()
    {
        var refusal = Assert.Throws<CatalogValidationException>(
            () => CatalogValidator.Validate(
                Example(orders: Related(steps: [Up("order_lines", 0, 1)], endpoint: "order_lines"))));

        Assert.Contains("main.order_lines", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("does not hold", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>The direction is checked, not inferred (§1, §3).</summary>
    [Fact]
    public void A_step_whose_direction_the_foreign_keys_do_not_support_is_refused()
    {
        // The bridge references the target, so a *down*-step from the target to the bridge is a
        // relationship the schema does not state.
        var refusal = Assert.Throws<CatalogValidationException>(
            () => CatalogValidator.Validate(
                Example(orders: Related(steps: [Down("order_items", 0, 0), Down("vendors", 2, 0)]))));

        Assert.Contains("goes down from 'orders' to 'order_items'", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("declares no foreign key", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_bridge_with_no_declared_foreign_keys_is_refused()
    {
        var refusal = Assert.Throws<CatalogValidationException>(
            () => CatalogValidator.Validate(Example(bridge: OrderItems(foreignKeys: false))));

        Assert.Contains("declares no foreign key", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_join_onto_something_that_is_not_a_declared_unique_key_is_refused()
    {
        var refusal = Assert.Throws<CatalogValidationException>(
            () => CatalogValidator.Validate(
                Example(orders: Related(steps: [Up("order_items", 1, 1), Down("vendors", 2, 0)]))));

        Assert.Contains("is not a declared unique key", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// This run admits an inherited path of any length and a related path of one up-step followed by
    /// down-steps; every other shape is refused rather than built (§1, §9).
    /// </summary>
    [Fact]
    public void A_second_up_step_is_refused()
    {
        var refusal = Assert.Throws<CatalogValidationException>(
            () => CatalogValidator.Validate(
                Example(orders: Related(
                    steps: [Up("order_items", 0, 1), Up("order_items", 0, 1)],
                    endpoint: "order_items"))));

        Assert.Contains("goes up", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("refused rather than built", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_endpoint_the_last_step_does_not_reach_is_refused()
    {
        var refusal = Assert.Throws<CatalogValidationException>(
            () => CatalogValidator.Validate(
                Example(orders: Related(steps: [Up("order_items", 0, 1)], endpoint: "vendors"))));

        Assert.Contains("says its endpoint is 'vendors'", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("arrives at 'order_items'", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_path_with_no_endpoint_predicate_is_refused()
    {
        var refusal = Assert.Throws<CatalogValidationException>(
            () => CatalogValidator.Validate(Example(orders: Related(predicate: ""))));

        Assert.Contains("carries no endpoint predicate", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The bridge's two key columns are read raw and disclosed to nobody, exactly as a parent's key
    /// is left full under D227 — so a rule over one is a contradiction (§3).
    /// </summary>
    [Fact]
    public void A_protected_bridge_key_is_refused()
    {
        var bridge = OrderItems(new TableEntitlementDescriptor
        {
            RowPredicate = "order_id IN (@ctx.order_ids)",
            Columns =
            [
                new ColumnEntitlementDescriptor { Column = 2, Otherwise = Disclosure.None },
            ],
        });

        var refusal = Assert.Throws<CatalogValidationException>(
            () => CatalogValidator.Validate(Example(bridge: bridge)));

        Assert.Contains("main.order_items.vendor_id", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("is protected by a rule", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A bridge that is itself entitled is ordinary — its own policy governs a statement's own
    /// occurrence of it — as long as the two key columns are left alone (§0).
    /// </summary>
    [Fact]
    public void An_entitled_bridge_whose_keys_are_full_is_accepted()
    {
        var bridge = OrderItems(new TableEntitlementDescriptor
        {
            RowPredicate = "order_id IN (@ctx.order_ids)",
            Columns =
            [
                new ColumnEntitlementDescriptor { Column = 3, Otherwise = Disclosure.None },
            ],
        });

        CatalogValidator.Validate(Example(bridge: bridge));
    }

    [Fact]
    public void A_step_back_to_the_target_is_refused()
    {
        var refusal = Assert.Throws<CatalogValidationException>(
            () => CatalogValidator.Validate(
                Example(orders: Related(
                    steps: [Up("order_items", 0, 1), Down("orders", 1, 0)], endpoint: "orders"))));

        Assert.Contains("returns to 'orders' itself", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The cycle rule is over the visibility-dependency graph, whose edges are a <c>through</c>'s
    /// child → parent and a path's target → endpoint (§3).
    /// </summary>
    [Fact]
    public void A_chain_of_derived_visibility_that_returns_to_a_table_is_refused()
    {
        var vendorsThroughOrders = new TableEntitlementDescriptor
        {
            Through =
            [
                new ParentVisibilityDescriptor { Column = 0, ParentTable = "orders", ParentColumn = 0 },
            ],
        };

        var refusal = Assert.Throws<CatalogValidationException>(
            () => CatalogValidator.Validate(Example(vendors: vendorsThroughOrders)));

        Assert.Contains(
            "the chain of derived visibility returns to", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The bridge is <b>not</b> a node of that graph: a bridge entitled <em>through</em> the very
    /// table it makes visible is the marketplace shape, and it is acyclic because the bridge's own
    /// visibility is never consulted (§0, §3).
    /// </summary>
    [Fact]
    public void A_bridge_entitled_through_the_target_is_not_a_cycle()
    {
        var bridge = OrderItems(new TableEntitlementDescriptor
        {
            Through =
            [
                new ParentVisibilityDescriptor { Column = 1, ParentTable = "orders", ParentColumn = 0 },
            ],
        });

        CatalogValidator.Validate(Example(bridge: bridge));
    }
}

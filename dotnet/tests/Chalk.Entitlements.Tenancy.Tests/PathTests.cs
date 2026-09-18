using Chalk.Catalog;
using Chalk.Sources.Poco;

namespace Chalk.Entitlements.Tenancy.Tests;

/// <summary>
/// The three ways a table holds a tenancy — <c>Direct</c>, <c>Inherited</c>, <c>Related</c> — as the
/// package spells them and as the compiler resolves and refuses them
/// (<c>docs/design/38-existential-visibility.md</c> §0, §1, §3; D265).
/// </summary>
/// <remarks>
/// The schema is this file's own — an order that carries the organisation, an item that carries the
/// relationship and nothing else, and a vendor the item reaches — because it is the smallest shape
/// the decision has, and the corpus fixture's is the same one with rows in it.
/// </remarks>
public sealed class PathTests
{
    private sealed record Org(int Id, string Name);

    private sealed record Vendor(int Id, string Name);

    private sealed record Order(int Id, int OrgId, long Amount);

    private sealed record OrderItem(int Id, int OrderId, int VendorId, int Quantity, long UnitPrice);

    /// <summary>A bridge referencing one table twice, which is what makes a step ambiguous.</summary>
    private sealed record Transfer(int Id, int FromOrderId, int ToOrderId, int VendorId);

    private static SchemaDescriptor Schema { get; } = Build();

    /// <summary>
    /// The catalog the policies below are declared over. A policy is declared against a catalog and
    /// a table is obtained from one of its sources, because two sources may hold a table of one name
    /// (<c>docs/design/45-typed-tenancy-surface.md</c> §1, D270); this file has the one source.
    /// </summary>
    private static CatalogContext Catalog { get; } = new()
    {
        ContextId = "paths",
        Epoch = 1,
        Schemas = [Schema],
    };

    private static SchemaDescriptor Build()
    {
        var builder = new PocoSourceBuilder("mem").NamingPolicy(PocoNamingPolicy.SnakeCase);
        builder.AddTable("orgs", Array.Empty<Org>(), t => t.OrderedBy(o => o.Id).UniqueKey(o => o.Id));
        builder.AddTable("vendors", Array.Empty<Vendor>(), t =>
            t.OrderedBy(v => v.Id).UniqueKey(v => v.Id));
        builder.AddTable("orders", Array.Empty<Order>(), t =>
            t.OrderedBy(o => o.Id).UniqueKey(o => o.Id).ForeignKey(o => o.OrgId)
                .References<Org>(o => o.Id, verify: true));
        builder.AddTable("order_items", Array.Empty<OrderItem>(), t =>
        {
            t.OrderedBy(i => i.Id).UniqueKey(i => i.Id);
            t.ForeignKey(i => i.OrderId).References<Order>(o => o.Id, verify: true);
            t.ForeignKey(i => i.VendorId).References<Vendor>(v => v.Id, verify: true);
        });
        builder.AddTable("transfers", Array.Empty<Transfer>(), t =>
        {
            t.OrderedBy(x => x.Id).UniqueKey(x => x.Id);
            t.ForeignKey(x => x.FromOrderId).References<Order>(o => o.Id, verify: true);
            t.ForeignKey(x => x.ToOrderId).References<Order>(o => o.Id, verify: true);
            t.ForeignKey(x => x.VendorId).References<Vendor>(v => v.Id, verify: true);
        });
        return builder.Build().DescribeSchema();
    }

    /// <summary>
    /// One policy and the handles its declarations are written with. Every name enters once, here,
    /// and every later mention is the handle it came back as (§1, D270) — a table obtained from the
    /// source it belongs to, and a column from the table that carries it.
    /// </summary>
    private sealed class Declaration
    {
        private readonly Source _source;

        internal Declaration()
        {
            Policy = TenancyPolicy.Declare(Catalog);
            _source = Policy.Source(Schema.Name);
            Manager = Policy.Role("manager");
            VendorRole = Policy.Role("vendor");
            Org = Policy.Tenancy("org");
            Vendor = Policy.Tenancy("vendor");
            Vendors = _source.Table("vendors");
            Orders = _source.Table("orders");
            Items = _source.Table("order_items");
        }

        internal TenancyPolicy Policy { get; }

        internal Role Manager { get; }

        internal Role VendorRole { get; }

        internal Kind Org { get; }

        internal Kind Vendor { get; }

        internal Table Vendors { get; }

        internal Table Orders { get; }

        internal Table Items { get; }

        internal Realm Pii => Policy.Realm("pii");

        /// <summary>One more table of the same source, for a test that reaches beyond the three.</summary>
        internal Table Named(string name) => _source.Table(name);

        internal TenancyEntitlements Compile() => Policy.Compile([Schema]);
    }

    /// <summary>
    /// A policy of its own, for a kind no policy under test declares: a kind exists only where some
    /// policy declared one (§1, D270).
    /// </summary>
    private static TenancyPolicy Elsewhere { get; } = TenancyPolicy.Declare(Catalog);

    /// <summary>
    /// A kind is declared once, for the whole policy, and a table names the handle it got back. The
    /// string form could name a kind the policy never declared and was refused at compile; the typed
    /// form can only do it with <em>another</em> policy's handle, and is refused where it is
    /// written (§1, D265; docs/design/45-typed-tenancy-surface.md §1, D270).
    /// </summary>
    [Fact]
    public void A_kind_this_policy_does_not_declare_is_refused()
    {
        var declaration = new Declaration();

        var error = Assert.Throws<CatalogValidationException>(
            () => declaration.Orders.Tenancy(
                r => r.Direct(Elsewhere.Tenancy("tenant"), declaration.Orders.Column("org_id"))));

        Assert.Contains(
            "the tenancy kind handle 'tenant' was not obtained from this policy",
            error.Message,
            StringComparison.Ordinal);
    }

    /// <summary>§8's shape: the target direct in one kind and related in the other.</summary>
    private static Declaration Policy(
        Action<Declaration>? orders = null,
        Action<Declaration>? items = null,
        Action<Declaration>? vendors = null)
    {
        var declaration = new Declaration();
        Declare(vendors, DefaultVendors);
        Declare(orders, DefaultOrders);
        Declare(items, DefaultItems);
        return declaration;

        void Declare(Action<Declaration>? given, Action<Declaration> fallback) =>
            (given ?? fallback)(declaration);

        static void DefaultVendors(Declaration d) =>
            d.Vendors.Tenancy(r => r.Direct(d.Vendor, d.Vendors.Column("id")));

        static void DefaultOrders(Declaration d) =>
            d.Orders.Tenancy(r => r
                .Direct(d.Org, d.Orders.Column("org_id"))
                .Related(d.Vendor)
                    .Through(d.Items)
                    .Through(d.Vendors));

        static void DefaultItems(Declaration d) =>
            d.Items.Tenancy(r => r
                .Through(d.Items.Column("order_id"))
                .Inherited(d.Vendor).Through(d.Vendors));
    }

    private static CatalogValidationException Refused(Func<Declaration> declare)
    {
        var error = Record.Exception(() =>
        {
            declare().Compile();
        });

        return Assert.IsType<CatalogValidationException>(error);
    }

    // ------------------------------------------------------------------ what it compiles to

    /// <summary>
    /// A <c>Direct</c> declaration compiles to the <see cref="GrantDimension"/> the compiler reads,
    /// with the kind's own shape read off the policy (§1).
    /// </summary>
    [Fact]
    public void Direct_compiles_to_the_dimension_it_always_did()
    {
        var policy = TenancyPolicy.Declare(Catalog);
        policy.Role("manager");
        var orgKind = policy.Tenancy("org");
        var memberKind = policy.Subject("member", within: [orgKind]);
        var orders = policy.Source(Schema.Name).Table("orders");
        orders.Tenancy(r => r
            .Direct(orgKind, orders.Column("org_id"))
            .Direct(memberKind, orders.Column("id")));

        // The declarations are frozen into the model the compiler reads when the policy compiles
        // (§5, D270), so what they came to is read back from there and not from the handles.
        policy.Compile([Schema]);

        var table = Assert.Single(policy.Tables);
        Assert.Collection(
            table.Dimensions,
            org =>
            {
                Assert.Equal("org", org.Kind);
                Assert.Equal("org_id", org.Column);
                Assert.False(org.IsSubject);
            },
            member =>
            {
                Assert.Equal("member", member.Kind);
                Assert.Equal("id", member.Column);
                Assert.True(member.IsSubject);
                Assert.Equal("org", member.Within);
            });
    }

    /// <summary>
    /// A <c>Related</c> path is flattened onto the wire: one up-step to the bridge, then down-steps
    /// to the endpoint, with the endpoint's own predicate at the far end (§2).
    /// </summary>
    [Fact]
    public void A_related_path_flattens_to_its_steps_and_its_endpoint_predicate()
    {
        var declaration = Policy();
        var descriptor = declaration.Compile().For(declaration.Orders)!;

        var declaredPath = Assert.Single(descriptor.Inherited);
        Assert.Equal("vendor", declaredPath.Kind);
        Assert.Equal("vendors", declaredPath.EndpointTable);
        Assert.Collection(
            declaredPath.Steps,
            up =>
            {
                Assert.Equal("order_items", up.Table);
                Assert.Equal(StepDirection.ToChild, up.Direction);
                Assert.Equal(0, up.FromColumn);
                Assert.Equal(1, up.ToColumn);
            },
            down =>
            {
                Assert.Equal("vendors", down.Table);
                Assert.Equal(StepDirection.ToParent, down.Direction);
                Assert.Equal(2, down.FromColumn);
                Assert.Equal(0, down.ToColumn);
            });
        Assert.Contains("id IN (@ctx.vendor_vendor)", declaredPath.EndpointPredicate, StringComparison.Ordinal);
        Assert.DoesNotContain("orders", declaredPath.EndpointPredicate, StringComparison.Ordinal);
    }

    /// <summary>An <c>Inherited</c> path is the same chain without the bridge: down-steps alone (§4).</summary>
    [Fact]
    public void An_inherited_path_is_down_steps_alone()
    {
        var declaration = Policy();
        var descriptor = declaration.Compile().For(declaration.Items)!;

        var declaredPath = Assert.Single(descriptor.Inherited);
        Assert.Equal("vendor", declaredPath.Kind);
        var step = Assert.Single(declaredPath.Steps);
        Assert.Equal("vendors", step.Table);
        Assert.Equal(StepDirection.ToParent, step.Direction);
        Assert.Equal(2, step.FromColumn);
        Assert.Equal(0, step.ToColumn);
    }

    /// <summary>
    /// The path is on the descriptor and not in the text: an <c>EXISTS</c> written here would be the
    /// sub-query §2's vocabulary does not admit (§4).
    /// </summary>
    [Fact]
    public void A_path_writes_no_row_predicate_term()
    {
        var declaration = Policy();
        var predicate = declaration.Compile().For(declaration.Orders)!.RowPredicate;

        Assert.DoesNotContain("order_items", predicate, StringComparison.Ordinal);
        Assert.DoesNotContain("vendors.", predicate, StringComparison.Ordinal);
        Assert.Contains("org_id IN (@ctx.org_manager)", predicate, StringComparison.Ordinal);
    }

    /// <summary>
    /// The target's role conditions for a path's perspective read the <em>endpoint's</em> columns,
    /// written over <c>&lt;endpoint_table&gt;.&lt;column&gt;</c> so the pass can evaluate them there,
    /// once per endpoint row (§2, §4; D228's rule along a path).
    /// </summary>
    [Fact]
    public void The_targets_role_conditions_read_the_endpoints_columns()
    {
        var declaration = Policy(items: d => d.Items.Tenancy(r => r.Through(d.Items.Column("order_id"))));
        declaration.Orders.Access(new AccessRule
        {
            Roles = [declaration.VendorRole],
            Column = declaration.Orders.Column("amount"),
            Grants = Verdict.None,
        });

        var amount = declaration.Compile().For(declaration.Orders)!.FindColumn(2)!;

        Assert.Contains(
            amount.Rules,
            rule => rule.When.Contains("vendors.id IN (@ctx.vendor_vendor)", StringComparison.Ordinal));
    }

    /// <summary>The lists an endpoint's memberships read are the target's to bind too (§2).</summary>
    [Fact]
    public void The_endpoints_lists_are_bound_by_the_target()
    {
        var declaration = Policy();
        var context = declaration.Compile().Bind(new TenancyPrincipal
        {
            User = 12,
            Grants = [Grant.ForTenancy(declaration.Vendor, 1, declaration.VendorRole)],
        });

        Assert.True(context.Lists.ContainsKey("vendor_vendor"));
        Assert.Single(context.Lists["vendor_vendor"].Rows);
    }

    /// <summary>
    /// An endpoint that inherits the kind itself is flattened, so the wire carries one route and the
    /// pass one set of joins (§1).
    /// </summary>
    [Fact]
    public void An_endpoint_that_inherits_the_kind_is_flattened()
    {
        var declaration = new Declaration();
        declaration.Vendors.Tenancy(r =>
            r.Direct(declaration.Vendor, declaration.Vendors.Column("id")));
        declaration.Items.Tenancy(r =>
            r.Inherited(declaration.Vendor).Through(declaration.Vendors));
        declaration.Orders.Tenancy(r =>
            r.Related(declaration.Vendor).Through(declaration.Items));

        var declaredPath = Assert.Single(
            declaration.Compile().For(declaration.Orders)!.Inherited);
        Assert.Equal("vendors", declaredPath.EndpointTable);
        Assert.Collection(
            declaredPath.Steps,
            up => Assert.Equal("order_items", up.Table),
            down => Assert.Equal("vendors", down.Table));
    }

    // ------------------------------------------------------------------ what it refuses

    [Fact]
    public void A_step_to_a_table_the_schema_does_not_hold_is_refused()
    {
        var error = Refused(() => Policy(orders: d => d.Orders
            .Tenancy(r => r
                .Direct(d.Org, d.Orders.Column("org_id"))
                .Related(d.Vendor)
                    .Through(d.Named("order_lines"))
                    .Through(d.Vendors))));

        Assert.Contains("order_lines", error.Message, StringComparison.Ordinal);
        Assert.Contains("that source does not hold", error.Message, StringComparison.Ordinal);
    }

    /// <summary>The direction is checked, not inferred: an up-step wants a bridge (§1).</summary>
    [Fact]
    public void A_related_first_step_to_a_table_that_does_not_reference_us_is_refused()
    {
        var error = Refused(() => Policy(orders: d => d.Orders
            .Tenancy(r => r
                .Direct(d.Org, d.Orders.Column("org_id"))
                .Related(d.Vendor).Through(d.Vendors))));

        Assert.Contains("goes up from 'orders' to 'vendors'", error.Message, StringComparison.Ordinal);
        Assert.Contains("declares no foreign key", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_inherited_step_with_no_foreign_key_to_the_named_table_is_refused()
    {
        var error = Refused(() => Policy(items: d => d.Items
            .Tenancy(r => r.Inherited(d.Org).Through(d.Named("orgs")))));

        Assert.Contains(
            "goes down from 'order_items' to 'orgs'", error.Message, StringComparison.Ordinal);
        Assert.Contains("declares no foreign key", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Where a pair of tables has more than one foreign key between them the step names the column,
    /// and a step that does not is refused naming what would have made it unambiguous (§1).
    /// </summary>
    [Fact]
    public void An_ambiguous_up_step_is_refused()
    {
        var error = Refused(() => Policy(orders: d => d.Orders
            .Tenancy(r => r
                .Direct(d.Org, d.Orders.Column("org_id"))
                .Related(d.Vendor)
                    .Through(d.Named("transfers"))
                    .Through(d.Vendors))));

        Assert.Contains("is ambiguous", error.Message, StringComparison.Ordinal);
        Assert.Contains("2 references naming 'orders'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_ambiguous_up_step_that_names_its_column_resolves()
    {
        var declaration = new Declaration();
        var transfers = declaration.Named("transfers");
        declaration.Vendors.Tenancy(r =>
            r.Direct(declaration.Vendor, declaration.Vendors.Column("id")));
        declaration.Orders.Tenancy(r => r
            .Direct(declaration.Org, declaration.Orders.Column("org_id"))
            .Related(declaration.Vendor)
                .Through(transfers, on: transfers.Column("to_order_id"))
                .Through(declaration.Vendors));

        var declaredPath = Assert.Single(
            declaration.Compile().For(declaration.Orders)!.Inherited);
        Assert.Equal(2, declaredPath.Steps[0].ToColumn);
    }

    [Fact]
    public void An_endpoint_that_does_not_hold_the_kind_is_refused()
    {
        var error = Refused(() => Policy(vendors: d =>
            d.Vendors.Tenancy(r => r.Direct(d.Org, d.Vendors.Column("id")))));

        Assert.Contains("does not hold the kind 'vendor'", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The bridge's two key columns are read raw and disclosed to nobody, exactly as a parent's key
    /// is left full under D227 — so a realm or a rule over one is a contradiction (§3).
    /// </summary>
    [Fact]
    public void A_bridge_key_in_a_realm_is_refused()
    {
        var error = Refused(() => Policy(items: d => d.Items
            .Tenancy(r => r.Through(d.Items.Column("order_id")))
            .Realm(d.Pii, d.Items.Column("vendor_id"))));

        Assert.Contains("order_items.vendor_id", error.Message, StringComparison.Ordinal);
        Assert.Contains("is in the realm 'pii'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_bridge_key_named_by_an_access_rule_is_refused()
    {
        var error = Refused(() => Policy(items: d => d.Items
            .Tenancy(r => r.Through(d.Items.Column("order_id")))
            .Access(new AccessRule
            {
                Roles = [d.Manager],
                Column = d.Items.Column("order_id"),
                Grants = Verdict.Mask,
            })));

        Assert.Contains("order_items.order_id", error.Message, StringComparison.Ordinal);
        Assert.Contains("is named by an access rule", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The bridge is <b>not</b> a node of the visibility-dependency graph: a bridge entitled
    /// <em>through</em> the very table it makes visible is the marketplace shape, and it is acyclic
    /// because the bridge's own visibility is never consulted (§0, §3).
    /// </summary>
    [Fact]
    public void A_bridge_entitled_through_the_target_is_not_a_cycle()
    {
        var declaration = Policy();
        var declaredPath = Assert.Single(
            declaration.Compile().For(declaration.Orders)!.Inherited);
        Assert.Equal("order_items", declaredPath.Steps[0].Table);
    }

    /// <summary>
    /// The graph's edge is target → endpoint, so an endpoint that derives its own visibility back
    /// through the target is a cycle (§3).
    /// </summary>
    [Fact]
    public void A_chain_of_derived_visibility_that_returns_to_a_table_is_refused()
    {
        var error = Refused(() => Policy(vendors: d => d.Vendors
            .Tenancy(r => r
                .Direct(d.Vendor, d.Vendors.Column("id"))
                .Related(d.Org).Through(d.Items).Through(d.Orders))));

        Assert.Contains(
            "the chain of derived visibility returns to", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_path_that_steps_back_to_the_target_is_refused()
    {
        var error = Refused(() => Policy(orders: d => d.Orders
            .Tenancy(r => r
                .Direct(d.Org, d.Orders.Column("org_id"))
                .Related(d.Vendor).Through(d.Items).Through(d.Orders))));

        Assert.Contains("returns to 'orders' itself", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_path_with_no_step_is_refused()
    {
        var error = Refused(() => Policy(orders: d => d.Orders
            .Tenancy(r => r.Direct(d.Org, d.Orders.Column("org_id")).Related(d.Vendor))));

        Assert.Contains("declares no step", error.Message, StringComparison.Ordinal);
    }
}

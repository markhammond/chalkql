using Chalk.Catalog;
using Chalk.Client;
using Chalk.Entitlements;
using Chalk.Entitlements.Tenancy;
using Chalk.Sources.Poco;
using Chalk.TestKit;
using CatalogContext = Chalk.Catalog.CatalogContext;

namespace Chalk.Integration.Tests;

/// <summary>
/// What a path takes from a parent (D358): the parent's visibility by default, so a role the
/// parent's rule leaves out reads nothing along the path, and the parent's value of the kind, as a
/// marking, for the roles the path names exempt.
/// </summary>
/// <remarks>
/// <para>
/// The smallest policies that show it, run through the engine as a host runs them. <c>orders</c> is
/// visible to a manager and nobody else; a line takes its organisation from its order and its vendor
/// from its item, which every role may read. A keeper is the role the order's rule leaves out.
/// </para>
/// <para>
/// The same declaration once read the other way, as a marking for every role: a keeper in
/// organisation 1 read no order and the line of one. A policy that meant that says so now, with
/// <c>ExemptFromParentVisibility</c>; one that did not keeps its children with its parent.
/// </para>
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class InheritedAdmissionTests(SharedSidecar sidecar)
{
    private sealed record Order(int Id, int OrgId);

    private sealed record Line(int Id, int OrderId, int ItemId, int Qty);

    private sealed record Item(int Id, int VendorId);

    private static readonly Order[] Orders = [new(1, 1), new(2, 2)];

    private static readonly Line[] Lines = [new(1, 1, 1, 5), new(2, 2, 2, 7)];

    private static readonly Item[] Items = [new(1, 1), new(2, 2)];

    private const string OrdersSql = "SELECT id FROM shop.orders ORDER BY id";

    private const string LinesSql = "SELECT id, order_id, qty FROM shop.lines ORDER BY id";

    /// <summary>
    /// A manager in organisation 1 reads its order and the order's line. A keeper there would read
    /// neither — the order's rule holds along the line's path to it — and since no other table answers
    /// an organisation for a keeper here, its grant would reach nothing at all, and is refused when it
    /// is bound, naming the path.
    /// </summary>
    [Fact]
    public async Task A_role_a_parent_leaves_out_holds_nothing_along_the_path()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var entitlements = Compile(s => s.LinesByOrderAndItem());
        await using var engine = await EngineAsync(entitlements);

        var manager = Bind(entitlements, "org", "manager");
        Assert.Equal(["1"], await RowsAsync(engine, OrdersSql, manager));
        Assert.Equal(["1|1|5"], await RowsAsync(engine, LinesSql, manager));

        var refused = Assert.Throws<CatalogValidationException>(() => Bind(entitlements, "org", "keeper"));
        Assert.Equal(ChalkErrorCodes.InvalidGrant, refused.Code);
        Assert.Contains(
            "'lines' takes 'org' only through 'orders', whose visibility rule leaves 'keeper' out, so "
            + "it answers nothing there in that role: declare .ExemptFromParentVisibility(keeper) on "
            + "that path",
            refused.Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Named exempt, the keeper takes the order's organisation as a marking: it reads the line of
    /// organisation 1's order, and still no order.
    /// </summary>
    [Fact]
    public async Task An_exempt_role_reads_a_child_by_its_parents_marking()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var entitlements = Compile(s => s.LinesByOrderAndItem(path => path.ExemptFromParentVisibility(s.Keeper)));
        await using var engine = await EngineAsync(entitlements);

        var keeper = Bind(entitlements, "org", "keeper");
        Assert.Empty(await RowsAsync(engine, OrdersSql, keeper));
        Assert.Equal(["1|1|5"], await RowsAsync(engine, LinesSql, keeper));
    }

    /// <summary>
    /// Entitled through the whole parent, as before: a line is visible only when its order is, so the
    /// keeper reads none.
    /// </summary>
    [Fact]
    public async Task Through_the_whole_parent_a_child_is_visible_only_when_its_parent_is()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var entitlements = Compile(s => s.Lines.Tenancy(t => t.Through(s.Lines.Column("order_id"))));
        await using var engine = await EngineAsync(entitlements);
        var keeper = Bind(entitlements, "org", "keeper");

        Assert.Empty(await RowsAsync(engine, OrdersSql, keeper));
        Assert.Empty(await RowsAsync(engine, LinesSql, keeper));
    }

    /// <summary>
    /// A line entitled along its order's path alone, admitting every role: the keeper would read
    /// none of it ever, and the policy is refused when it compiles, saying what to declare.
    /// </summary>
    [Fact]
    public void A_role_left_with_no_way_in_is_refused_when_the_policy_compiles()
    {
        var refused = Assert.Throws<CatalogValidationException>(
            () => Compile(s => s.Lines.Tenancy(t => t.Inherited(s.Org).Through(s.Orders))));

        Assert.Equal(ChalkErrorCodes.ParentVisibilityLeavesRoleOut, refused.Code);
        Assert.Contains(
            "'lines' admits 'keeper', which reaches it only through 'orders', whose visibility rule "
            + "leaves it out. Declare .ExemptFromParentVisibility(keeper) on the path",
            refused.Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Required to be explicit, the policy names the keeper on the line's path to the order, and
    /// reads as it says: bound, the keeper's grant reaches nothing and is refused when it is bound;
    /// exempt, it reads the line of organisation 1's order.
    /// </summary>
    [Fact]
    public async Task A_policy_that_requires_explicit_parent_visibility_reads_as_it_names()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        var unnamed = Assert.Throws<CatalogValidationException>(() => Compile(s =>
        {
            s.Policy.RequireExplicitParentVisibility();
            s.LinesByOrderAndItem();
        }));
        Assert.Contains(
            "declare .ExemptFromParentVisibility(keeper) or .BoundByParentVisibility(keeper)",
            unnamed.Message,
            StringComparison.Ordinal);

        var bound = Compile(s =>
        {
            s.Policy.RequireExplicitParentVisibility();
            s.LinesByOrderAndItem(path => path.BoundByParentVisibility(s.Keeper, s.Supplier));
        });
        Assert.Throws<CatalogValidationException>(() => Bind(bound, "org", "keeper"));

        var exempt = Compile(s =>
        {
            s.Policy.RequireExplicitParentVisibility();
            s.LinesByOrderAndItem(path => path
                .ExemptFromParentVisibility(s.Keeper)
                .BoundByParentVisibility(s.Supplier));
        });
        await using (var engine = await EngineAsync(exempt))
        {
            Assert.Equal(["1|1|5"], await RowsAsync(engine, LinesSql, Bind(exempt, "org", "keeper")));
        }
    }

    /// <summary>
    /// Along a <c>Related</c> path the endpoint's rule holds: <c>items</c> admits the supplier alone,
    /// so a supplier holding the vendor reaches the order its line carries, and a keeper's vendor
    /// grant reaches nothing at all — refused when it is bound, naming the path.
    /// </summary>
    [Fact]
    public async Task Along_a_related_path_the_endpoints_rule_holds()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var entitlements = Compile(
            s =>
            {
                s.Items
                    .Tenancy(t => t.Direct(s.Vendor, s.Items.Column("vendor_id")))
                    .Visible(new VisibilityRule { Roles = [s.Supplier] });
                s.Orders.Tenancy(t => t
                    .Direct(s.Org, s.Orders.Column("org_id"))
                    .Related(s.Vendor).Through(s.Lines).Through(s.Items));
            },
            ordersForManagers: false);
        await using var engine = await EngineAsync(entitlements);

        Assert.Equal(["1"], await RowsAsync(engine, OrdersSql, Bind(entitlements, "vendor", "supplier")));

        var refused = Assert.Throws<CatalogValidationException>(() => Bind(entitlements, "vendor", "keeper"));
        Assert.Contains(
            "'orders' takes 'vendor' only through 'items', whose visibility rule leaves 'keeper' out",
            refused.Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The order holds in the interactive scope alone, and its rule's scope holds along the line's
    /// path: a manager reads the line there, and not in the batch scope.
    /// </summary>
    [Fact]
    public async Task A_parents_scope_holds_along_the_path()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var entitlements = Compile(s => s.LinesByOrderAndItem(), orderScopes: s => [s.Interactive]);
        await using var engine = await EngineAsync(entitlements);

        Assert.Equal(
            ["1|1|5"],
            await RowsAsync(engine, LinesSql, Bind(entitlements, "org", "manager", "interactive")));
        Assert.Empty(await RowsAsync(engine, LinesSql, Bind(entitlements, "org", "manager", "batch")));
    }

    /// <summary>
    /// A line visible in the batch scope alone holds there along its path, as on its own row: a
    /// manager reads it in the batch scope, and not in the interactive one.
    /// </summary>
    [Fact]
    public async Task A_tables_own_scope_holds_along_its_paths()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var entitlements = Compile(s => s.Lines
            .Tenancy(t => t.Inherited(s.Org).Through(s.Orders))
            .Visible(new VisibilityRule { Roles = [s.Manager], Scopes = [s.Batch] }));
        await using var engine = await EngineAsync(entitlements);

        Assert.Equal(
            ["1|1|5"],
            await RowsAsync(engine, LinesSql, Bind(entitlements, "org", "manager", "batch")));
        Assert.Empty(await RowsAsync(engine, LinesSql, Bind(entitlements, "org", "manager", "interactive")));
    }

    // ---------------------------------------------------------------- the policies

    /// <summary>One policy over the shop, and the handles it is written with.</summary>
    private sealed class Shop
    {
        internal Shop(TenancyPolicy policy)
        {
            Policy = policy;
            var shop = policy.Source("shop");
            Org = policy.Tenancy("org");
            Vendor = policy.Tenancy("vendor");
            Manager = policy.Role("manager");
            Keeper = policy.Role("keeper");
            Supplier = policy.Role("supplier");
            Interactive = policy.Scope("interactive");
            Batch = policy.Scope("batch");
            Orders = shop.Table("orders");
            Lines = shop.Table("lines");
            Items = shop.Table("items");
        }

        internal TenancyPolicy Policy { get; }

        internal Kind Org { get; }

        internal Kind Vendor { get; }

        internal Role Manager { get; }

        internal Role Keeper { get; }

        internal Role Supplier { get; }

        internal Scope Interactive { get; }

        internal Scope Batch { get; }

        internal Table Orders { get; }

        internal Table Lines { get; }

        internal Table Items { get; }

        /// <summary>
        /// A line takes its organisation from its order, the path named as <paramref name="named"/>
        /// says, and its vendor from its item, which every role reads.
        /// </summary>
        internal void LinesByOrderAndItem(Func<TenancyPath, TenancyPath>? named = null)
        {
            Items.Tenancy(t => t.Direct(Vendor, Items.Column("vendor_id")));
            Lines.Tenancy(t => (named ?? (path => path))(t.Inherited(Org).Through(Orders))
                .Inherited(Vendor).Through(Items));
        }
    }

    /// <summary>
    /// The shop's policy: <c>orders</c> visible to a manager alone, in the scopes
    /// <paramref name="orderScopes"/> names, unless <paramref name="ordersForManagers"/> leaves it to
    /// <paramref name="declare"/>; and whatever else <paramref name="declare"/> says.
    /// </summary>
    private static TenancyEntitlements Compile(
        Action<Shop> declare, bool ordersForManagers = true, Func<Shop, Scope[]>? orderScopes = null)
    {
        var bare = Catalog(null);
        var shop = new Shop(TenancyPolicy.Declare(bare));
        if (ordersForManagers)
        {
            shop.Orders
                .Tenancy(t => t.Direct(shop.Org, shop.Orders.Column("org_id")))
                .Visible(new VisibilityRule { Roles = [shop.Manager], Scopes = orderScopes?.Invoke(shop) ?? [] });
        }

        declare(shop);
        return shop.Policy.Compile(bare);
    }

    // ---------------------------------------------------------------- the parts

    private static PocoSource ShopSource(TenancyEntitlements? entitlements)
    {
        var builder = new PocoSourceBuilder("mem", "shop").NamingPolicy(PocoNamingPolicy.SnakeCase);
        builder.AddTable("orders", Orders, t =>
        {
            t.OrderedBy(o => o.Id).UniqueKey(o => o.Id);
            Attach(t, entitlements, "orders");
        });
        builder.AddTable("items", Items, t =>
        {
            t.OrderedBy(i => i.Id).UniqueKey(i => i.Id);
            Attach(t, entitlements, "items");
        });
        builder.AddTable("lines", Lines, t =>
        {
            t.OrderedBy(l => l.Id).UniqueKey(l => l.Id)
                .ForeignKey(l => l.OrderId).References<Order>(o => o.Id, verify: true);
            t.ForeignKey(l => l.ItemId).References<Item>(i => i.Id, verify: true);
            Attach(t, entitlements, "lines");
        });
        return builder.Build();
    }

    private static void Attach<T>(PocoTableBuilder<T> table, TenancyEntitlements? entitlements, string name)
        where T : class
    {
        if (entitlements?.For("shop", name) is { } descriptor)
        {
            table.Entitlement(descriptor);
        }
    }

    private static CatalogContext Catalog(TenancyEntitlements? entitlements) => new()
    {
        ContextId = "inherited-admission",
        Epoch = 1,
        Schemas = [ShopSource(entitlements).DescribeSchema()],
    };

    private async ValueTask<ChalkEngine> EngineAsync(TenancyEntitlements entitlements) =>
        await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "inherited-admission",
            Sources = [ShopSource(entitlements)],
            Planner = sidecar.CreatePlanner(),
        });

    /// <summary>
    /// A principal holding one grant — identifier 1 of <paramref name="kind"/>, in
    /// <paramref name="role"/> — asking in the request scope <paramref name="scope"/>.
    /// </summary>
    private static RequestContext Bind(
        TenancyEntitlements entitlements, string kind, string role, string scope = "interactive") =>
        entitlements.Bind(new TenancyPrincipal
        {
            User = 9,
            Scope = entitlements.Policy.Scope(scope),
            Grants =
            [
                Grant.ForTenancy(entitlements.Policy.Tenancy(kind), 1, entitlements.Policy.Role(role)),
            ],
        });

    private static async Task<string[]> RowsAsync(ChalkEngine engine, string sql, RequestContext context)
    {
        var prepared = await engine.WithEntitlements().PrepareAsync(sql, context);
        var rows = new List<string>();
        await using var execution = await engine.ExecuteAsync(prepared);
        await foreach (var batch in execution.Batches)
        {
            using (batch)
            {
                rows.AddRange(BatchReader.ToRows(batch).Select(
                    r => string.Join("|", r.Select(v => v?.ToString() ?? "<null>"))));
            }
        }

        return [.. rows];
    }
}

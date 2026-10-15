using Chalk.Client;
using Chalk.Entitlements;
using Chalk.Entitlements.Tenancy;
using Chalk.Sources.Poco;
using Chalk.TestKit;
using CatalogContext = Chalk.Catalog.CatalogContext;

namespace Chalk.Integration.Tests;

/// <summary>
/// What a path takes from a parent (F208): the parent's value of the kind, as a marking, and not
/// the parent's visibility — which the kind-less <c>Through</c> does take.
/// </summary>
/// <remarks>
/// <para>
/// A tasking can carry its asset's classification and be read by roles that may not read the asset:
/// one marking, held once, and each table's visibility rule saying who reads that table. The same
/// declaration reads differently where an author meant a parent's rule to hide its children too, so
/// such a policy gives the children a rule of their own, or entitles them through the parent with
/// <c>Through</c>. Whether the stricter reading should be the default is the owner's to decide.
/// </para>
/// <para>
/// The smallest policies that show it. <c>orders</c> is visible to a manager and to nobody else, and a
/// line takes its organisation from its order: a keeper in organisation 1 reads no order, and reads
/// the line of one.
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

    /// <summary>The line's path to its order admits the roles the line admits, the keeper among them.</summary>
    [Fact]
    public void A_path_admits_the_roles_its_table_admits()
    {
        var entitlements = Inherited(kindless: false);

        var path = Assert.Single(entitlements.For("shop", "lines")!.Inherited);

        Assert.Contains("org_id IN (@ctx.org_manager)", path.EndpointPredicate, StringComparison.Ordinal);
        Assert.Contains("org_id IN (@ctx.org_keeper)", path.EndpointPredicate, StringComparison.Ordinal);
        Assert.DoesNotContain("keeper", entitlements.For("shop", "orders")!.RowPredicate, StringComparison.Ordinal);
    }

    /// <summary>
    /// A keeper in organisation 1 reads no order and the line of one: the line takes the order's
    /// organisation as a marking, and admits the keeper itself. A manager there reads both.
    /// </summary>
    [Fact]
    public async Task A_child_takes_its_parents_marking_and_keeps_its_own_visibility()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var entitlements = Inherited(kindless: false);
        await using var engine = await EngineAsync(entitlements);

        var keeper = Bind(entitlements, "org", "keeper");
        Assert.Empty(await RowsAsync(engine, "SELECT id FROM shop.orders ORDER BY id", keeper));
        Assert.Equal(
            ["1|1|5"],
            await RowsAsync(engine, "SELECT id, order_id, qty FROM shop.lines ORDER BY id", keeper));

        var manager = Bind(entitlements, "org", "manager");
        Assert.Equal(["1"], await RowsAsync(engine, "SELECT id FROM shop.orders ORDER BY id", manager));
        Assert.Equal(
            ["1|1|5"],
            await RowsAsync(engine, "SELECT id, order_id, qty FROM shop.lines ORDER BY id", manager));
    }

    /// <summary>Through the whole parent the line is visible only when its order is, so the keeper reads none.</summary>
    [Fact]
    public async Task Through_the_whole_parent_a_child_is_visible_only_when_its_parent_is()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var entitlements = Inherited(kindless: true);
        await using var engine = await EngineAsync(entitlements);
        var keeper = Bind(entitlements, "org", "keeper");

        Assert.Empty(await RowsAsync(engine, "SELECT id FROM shop.orders ORDER BY id", keeper));
        Assert.Empty(await RowsAsync(engine, "SELECT id, order_id, qty FROM shop.lines ORDER BY id", keeper));
    }

    /// <summary>
    /// Along a <c>Related</c> path, likewise: <c>items</c> admits the supplier alone, and a keeper
    /// holding the vendor reaches the order its line carries, as the supplier does. The item's vendor
    /// is the marking; the item's rule is not read.
    /// </summary>
    [Fact]
    public async Task Along_a_related_path_the_endpoints_rule_is_not_read()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var entitlements = Related();
        await using var engine = await EngineAsync(entitlements);

        var keeper = Bind(entitlements, "vendor", "keeper");
        Assert.Equal(["1"], await RowsAsync(engine, "SELECT id FROM shop.orders ORDER BY id", keeper));

        var supplier = Bind(entitlements, "vendor", "supplier");
        Assert.Equal(["1"], await RowsAsync(engine, "SELECT id FROM shop.orders ORDER BY id", supplier));
    }

    // ---------------------------------------------------------------- the policies

    /// <summary>
    /// <c>orders</c> admits the manager alone; <c>lines</c> admits every role and takes its
    /// organisation from its order, by kind or, with <paramref name="kindless"/>, as a whole.
    /// </summary>
    private static TenancyEntitlements Inherited(bool kindless)
    {
        var bare = Catalog(null);
        var policy = TenancyPolicy.Declare(bare);
        var shop = policy.Source("shop");
        var org = policy.Tenancy("org");
        var manager = policy.Role("manager");
        policy.Role("keeper");

        var orders = shop.Table("orders");
        var lines = shop.Table("lines");
        orders
            .Tenancy(t => t.Direct(org, orders.Column("org_id")))
            .Visible(new VisibilityRule { Roles = [manager] });
        if (kindless)
        {
            lines.Tenancy(t => t.Through(lines.Column("order_id")));
        }
        else
        {
            lines.Tenancy(t => t.Inherited(org).Through(orders));
        }

        return policy.Compile(bare);
    }

    /// <summary>
    /// <c>items</c> admits the supplier alone; an order is related to the vendor of an item one of its
    /// lines carries.
    /// </summary>
    private static TenancyEntitlements Related()
    {
        var bare = Catalog(null);
        var policy = TenancyPolicy.Declare(bare);
        var shop = policy.Source("shop");
        var org = policy.Tenancy("org");
        var vendor = policy.Tenancy("vendor");
        var supplier = policy.Role("supplier");
        policy.Role("keeper");

        var orders = shop.Table("orders");
        var lines = shop.Table("lines");
        var items = shop.Table("items");
        items
            .Tenancy(t => t.Direct(vendor, items.Column("vendor_id")))
            .Visible(new VisibilityRule { Roles = [supplier] });
        orders.Tenancy(t => t
            .Direct(org, orders.Column("org_id"))
            .Related(vendor).Through(lines).Through(items));

        return policy.Compile(bare);
    }

    // ---------------------------------------------------------------- the parts

    private static PocoSource Shop(TenancyEntitlements? entitlements)
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
        Schemas = [Shop(entitlements).DescribeSchema()],
    };

    private async ValueTask<ChalkEngine> EngineAsync(TenancyEntitlements entitlements) =>
        await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "inherited-admission",
            Sources = [Shop(entitlements)],
            Planner = sidecar.CreatePlanner(),
        });

    /// <summary>A principal holding one grant: identifier 1 of <paramref name="kind"/>, in <paramref name="role"/>.</summary>
    private static RequestContext Bind(TenancyEntitlements entitlements, string kind, string role) =>
        entitlements.Bind(new TenancyPrincipal
        {
            User = 9,
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

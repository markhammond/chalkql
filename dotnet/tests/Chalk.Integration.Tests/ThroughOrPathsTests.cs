using Chalk.Catalog;
using Chalk.Client;
using Chalk.Entitlements;
using Chalk.Entitlements.Tenancy;
using Chalk.Sources.Poco;
using Chalk.TestKit;
using CatalogContext = Chalk.Catalog.CatalogContext;

namespace Chalk.Integration.Tests;

/// <summary>
/// A child entitled <c>Through</c> its parent, or along paths by kind (D358, D359): what each gives,
/// run through the engine as a host runs them.
/// </summary>
/// <remarks>
/// <para>
/// One schema. <c>orders</c> holds its organisation and the customer who placed it, is related to
/// the vendor of an item one of its lines carries, and admits managers, agents and suppliers. O1, in
/// organisation 1 and placed by customer 100, carries L1, an item of vendor 1's, and L2, one of
/// vendor 2's; O2, in organisation 2, carries L3, one of vendor 2's.
/// </para>
/// <para>
/// <c>Through</c> is for a child that is part of its parent: it is read by every way into the parent,
/// the customer's included, and is all or nothing on the parent's row. Paths are for a child whose
/// readers differ from its parent's: each kind is its own route, the child's rule narrows it, and a
/// path carries kinds and nothing else. A rule that would narrow a <c>Through</c> child is refused,
/// since the route cannot hold it.
/// </para>
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class ThroughOrPathsTests(SharedSidecar sidecar)
{
    private sealed record Order(int Id, int OrgId, int CreatedBy);

    private sealed record Item(int Id, int VendorId);

    private sealed record Line(int Id, int OrderId, int ItemId);

    private static readonly Order[] Orders = [new(1, 1, 100), new(2, 2, 200)];

    private static readonly Item[] Items = [new(1, 1), new(2, 2)];

    private static readonly Line[] Lines = [new(1, 1, 1), new(2, 1, 2), new(3, 2, 2)];

    private enum Declared
    {
        Through,
        ThroughWithAManagersRule,
        Paths,
        PathsWithAManagersRule,
    }

    /// <summary>
    /// Through its order, a line is read by whoever reads the order, however: the customer who placed
    /// O1 reads its lines, and the supplier of vendor 2, which reads O1 because L2 carries its item,
    /// reads L1, vendor 1's line, too.
    /// </summary>
    [Fact]
    public async Task Through_its_parent_a_child_is_read_by_every_way_into_the_parent()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        var lines = await LinesReadAsync(Declared.Through);

        Assert.Equal(["1", "2"], lines["manager"]);
        Assert.Equal(["1", "2"], lines["agent"]);
        Assert.Equal(["1", "2"], lines["customer"]);
        Assert.Equal(["1", "2", "3"], lines["supplier"]);
    }

    /// <summary>
    /// Along paths, each kind is its own route: the supplier reads only its own items' lines, and the
    /// customer, whom no kind names, reads none of O1's.
    /// </summary>
    [Fact]
    public async Task Along_paths_a_child_is_read_by_kind()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        var lines = await LinesReadAsync(Declared.Paths);

        Assert.Equal(["1", "2"], lines["manager"]);
        Assert.Equal(["1", "2"], lines["agent"]);
        Assert.Empty(lines["customer"]);
        Assert.Equal(["2", "3"], lines["supplier"]);
    }

    /// <summary>
    /// A rule for managers alone narrows a child along paths: the agent and the supplier read no line.
    /// </summary>
    [Fact]
    public async Task A_rule_narrows_a_child_along_paths()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        var lines = await LinesReadAsync(Declared.PathsWithAManagersRule);

        Assert.Equal(["1", "2"], lines["manager"]);
        Assert.Empty(lines["agent"]);
        Assert.Empty(lines["customer"]);
        Assert.Empty(lines["supplier"]);
    }

    /// <summary>
    /// The same rule on a child entitled through its order could not narrow it — the line would be
    /// read whenever the order is — so the policy is refused when it compiles, saying what to do.
    /// </summary>
    [Fact]
    public void A_rule_narrowing_a_child_entitled_through_its_parent_is_refused()
    {
        var refused = Assert.Throws<CatalogValidationException>(() => Compile(Declared.ThroughWithAManagersRule));

        Assert.Equal(ChalkErrorCodes.InvalidPolicy, refused.Code);
        Assert.Contains(
            "'lines' is entitled through 'orders', and its visibility rule leaves out 'agent' and "
            + "'supplier', which 'orders' admits. A row entitled through its parent is visible whenever "
            + "the parent row is, so the rule cannot keep them from it. Admit them on 'lines', or "
            + "entitle 'lines' with Inherited paths, which read both rules.",
            refused.Message,
            StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- the harness

    /// <summary>The policy, with <c>lines</c> entitled as <paramref name="declared"/> says.</summary>
    private static TenancyEntitlements Compile(Declared declared)
    {
        var bare = Catalog(null);
        var p = TenancyPolicy.Declare(bare);
        var shop = p.Source("shop");
        var org = p.Tenancy("org");
        var vendor = p.Tenancy("vendor");
        var manager = p.Role("manager");
        var agent = p.Role("agent");
        var supplier = p.Role("supplier");
        var orders = shop.Table("orders");
        var lines = shop.Table("lines");
        var items = shop.Table("items");

        items.Tenancy(t => t.Direct(vendor, items.Column("vendor_id")));
        orders
            .Tenancy(t => t
                .Direct(org, orders.Column("org_id"))
                .ResourceOwner(orders.Column("created_by"))
                .Related(vendor).Through(lines).Through(items))
            .Visible(new VisibilityRule { Roles = [manager, agent, supplier] });

        var managersAlone = new VisibilityRule { Roles = [manager] };
        switch (declared)
        {
            case Declared.Through:
                lines.Tenancy(t => t.Through(lines.Column("order_id")));
                break;
            case Declared.ThroughWithAManagersRule:
                lines.Tenancy(t => t.Through(lines.Column("order_id"))).Visible(managersAlone);
                break;
            case Declared.Paths:
                lines.Tenancy(t => t.Inherited(org).Through(orders).Inherited(vendor).Through(items));
                break;
            case Declared.PathsWithAManagersRule:
                lines
                    .Tenancy(t => t.Inherited(org).Through(orders).Inherited(vendor).Through(items))
                    .Visible(managersAlone);
                break;
        }

        return p.Compile(bare);
    }

    /// <summary>The lines each of the four principals reads, by name.</summary>
    private async Task<Dictionary<string, string[]>> LinesReadAsync(Declared declared)
    {
        var entitlements = Compile(declared);
        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "through-or-paths",
            Sources = [Source(entitlements)],
            Planner = sidecar.CreatePlanner(),
        });

        var policy = entitlements.Policy;
        var org = policy.Tenancy("org");
        var principals = new Dictionary<string, TenancyPrincipal>
        {
            ["manager"] = new() { User = 1, Grants = [Grant.ForTenancy(org, 1, policy.Role("manager"))] },
            ["agent"] = new() { User = 2, Grants = [Grant.ForTenancy(org, 1, policy.Role("agent"))] },
            ["customer"] = new() { User = 100 },
            ["supplier"] = new()
            {
                User = 3,
                Grants = [Grant.ForTenancy(policy.Tenancy("vendor"), 2, policy.Role("supplier"))],
            },
        };

        var read = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var (name, principal) in principals)
        {
            read[name] = await RowsAsync(engine, entitlements.Bind(principal));
        }

        return read;
    }

    private static async Task<string[]> RowsAsync(ChalkEngine engine, RequestContext context)
    {
        var prepared = await engine.WithEntitlements().PrepareAsync("SELECT id FROM shop.lines ORDER BY id", context);
        var rows = new List<string>();
        await using var execution = await engine.ExecuteAsync(prepared);
        await foreach (var batch in execution.Batches)
        {
            using (batch)
            {
                rows.AddRange(BatchReader.ToRows(batch).Select(r => string.Join("|", r)));
            }
        }

        return [.. rows];
    }

    private static PocoSource Source(TenancyEntitlements? entitlements)
    {
        var builder = new PocoSourceBuilder("mem", "shop").NamingPolicy(PocoNamingPolicy.SnakeCase);
        builder.AddTable("orders", Orders, t =>
        {
            t.OrderedBy(o => o.Id).UniqueKey(o => o.Id);
            if (entitlements?.For("shop", "orders") is { } d)
            {
                t.Entitlement(d);
            }
        });
        builder.AddTable("items", Items, t =>
        {
            t.OrderedBy(i => i.Id).UniqueKey(i => i.Id);
            if (entitlements?.For("shop", "items") is { } d)
            {
                t.Entitlement(d);
            }
        });
        builder.AddTable("lines", Lines, t =>
        {
            t.OrderedBy(l => l.Id).UniqueKey(l => l.Id)
                .ForeignKey(l => l.OrderId).References<Order>(o => o.Id, verify: true);
            t.ForeignKey(l => l.ItemId).References<Item>(i => i.Id, verify: true);
            if (entitlements?.For("shop", "lines") is { } d)
            {
                t.Entitlement(d);
            }
        });
        return builder.Build();
    }

    private static CatalogContext Catalog(TenancyEntitlements? entitlements) => new()
    {
        ContextId = "through-or-paths",
        Epoch = 1,
        Schemas = [Source(entitlements).DescribeSchema()],
    };
}

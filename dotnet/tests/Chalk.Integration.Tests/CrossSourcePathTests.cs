using Chalk.Catalog;
using Chalk.Client;
using Chalk.Entitlements;
using Chalk.Entitlements.Tenancy;
using Chalk.Ir;
using Chalk.Sources.Poco;
using Chalk.TestKit;
using CatalogContext = Chalk.Catalog.CatalogContext;

namespace Chalk.Integration.Tests;

/// <summary>
/// A declared path whose steps cross a source: it compiles, it registers, and the pass builds its
/// chain across the two sources with the exchange carrying the keys (F84,
/// <c>docs/design/38-existential-visibility.md</c> §5;
/// <c>docs/design/45-typed-tenancy-surface.md</c> §3, D270 (c)).
/// </summary>
/// <remarks>
/// <para>
/// F84 was that design 38 §8's two-source layout <b>could not be declared at all</b>: every step
/// resolves through a declared foreign key, and a foreign key is one source's claim about a table of
/// its own schema. D270 supplied the missing declaration — the association — and left the exchange
/// to this run, which is where planning used to refuse by name.
/// </para>
/// <para>
/// The refusal that remains is the one registration makes, and it is the same one it always made: a
/// step across two sources that <em>nothing</em> declares. That is
/// <c>TenancyRemoteBindingTests.The_endpoint_in_another_source_is_refused_at_registration</c>, which
/// builds this very layout with no association and is refused before a statement is written.
/// </para>
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class CrossSourcePathTests(SharedSidecar sidecar)
{
    private sealed record Vendor(int Id, string Name);

    private sealed record Item(int Id, int VendorId, string Name);

    private sealed record Order(int Id, int OrgId, long Amount);

    private sealed record OrderItem(int Id, int OrderId, int ItemId);

    /// <summary>The target and the bridge, in one source.</summary>
    private static PocoSource Warehouse(TenancyEntitlements? entitlements)
    {
        var builder = new PocoSourceBuilder("duckdb", "warehouse")
            .NamingPolicy(PocoNamingPolicy.SnakeCase);
        builder.AddTable(
            "orders",
            new[] { new Order(1, 1, 500), new Order(2, 2, 900) },
            t =>
            {
                t.OrderedBy(o => o.Id).UniqueKey(o => o.Id);
                if (entitlements?.For("warehouse", "orders") is { } descriptor)
                {
                    t.Entitlement(descriptor);
                }
            });
        builder.AddTable(
            "order_items",
            new[] { new OrderItem(1, 1, 10), new OrderItem(2, 2, 11) },
            t => t.OrderedBy(i => i.Id).UniqueKey(i => i.Id)
                .ForeignKey(i => i.OrderId).References<Order>(o => o.Id, verify: true));
        return builder.Build();
    }

    /// <summary>The endpoint, in the other one. Its items name a vendor; nothing names an order.</summary>
    private static PocoSource Catalogue(TenancyEntitlements? entitlements)
    {
        var builder = new PocoSourceBuilder("sqlite", "catalogue")
            .NamingPolicy(PocoNamingPolicy.SnakeCase);
        builder.AddTable(
            "vendors",
            new[] { new Vendor(1, "north"), new Vendor(2, "south") },
            t => t.OrderedBy(v => v.Id).UniqueKey(v => v.Id));
        builder.AddTable(
            "items",
            new[] { new Item(10, 1, "bolt"), new Item(11, 2, "nut") },
            t =>
            {
                t.OrderedBy(i => i.Id).UniqueKey(i => i.Id)
                    .ForeignKey(i => i.VendorId).References<Vendor>(v => v.Id, verify: true);
                if (entitlements?.For("catalogue", "items") is { } descriptor)
                {
                    t.Entitlement(descriptor);
                }
            });
        return builder.Build();
    }

    private static CatalogContext Catalog(
        TenancyEntitlements? entitlements, IReadOnlyList<AssociationDescriptor> associations) => new()
        {
            ContextId = "f84-cross-source",
            Epoch = 1,
            Schemas = [Warehouse(entitlements).DescribeSchema(), Catalogue(entitlements).DescribeSchema()],
            Associations = associations,
        };

    /// <summary>
    /// §8's two-source layout, declared: an order is visible to a vendor some line of it carries an
    /// item of, and the line and the item are in two sources.
    /// </summary>
    private static (TenancyEntitlements Entitlements, IReadOnlyList<AssociationDescriptor> Associations)
        Declare()
    {
        var bare = Catalog(entitlements: null, associations: []);
        var policy = TenancyPolicy.Declare(bare);

        var warehouse = policy.Source("warehouse");
        var catalogue = policy.Source("catalogue");
        var org = policy.Tenancy("org");
        var vendor = policy.Tenancy("vendor");
        var manager = policy.Role("manager");
        var vendorRole = policy.Role("vendor");

        var orders = warehouse.Table("orders");
        var orderItems = warehouse.Table("order_items");
        var items = catalogue.Table("items");

        // The declaration F84 lacked: the line names an item of the other source, and no foreign key
        // of `order_items` could say so, because a foreign key names a table of its own schema.
        orderItems.Column("item_id").References(items.Column("id"));

        items.Tenancy(t => t.Direct(vendor, items.Column("vendor_id")));
        orders.Tenancy(t => t
            .Direct(org, orders.Column("org_id"))
            .Related(vendor).Through(orderItems).Through(items));

        Assert.Equal("manager", manager.Name);
        Assert.Equal("vendor", vendorRole.Name);

        var associations = policy.Associations;
        return (policy.Compile(Catalog(entitlements: null, associations)), associations);
    }

    /// <summary>
    /// The policy compiles: the step from the bridge to the endpoint resolves through the declared
    /// association exactly as it would through a foreign key, and the wire carries the endpoint's
    /// schema beside its table.
    /// </summary>
    [Fact]
    public void A_path_across_two_sources_compiles_through_the_association()
    {
        var (entitlements, associations) = Declare();

        var association = Assert.Single(associations);
        Assert.Equal("warehouse", association.FromSchema);
        Assert.Equal("order_items", association.FromTable);
        Assert.Equal("item_id", association.FromColumn);
        Assert.Equal("catalogue", association.ToSchema);
        Assert.Equal("items", association.ToTable);
        Assert.Equal("id", association.ToColumn);

        var path = Assert.Single(entitlements.For("warehouse", "orders")!.Inherited);
        Assert.Equal("vendor", path.Kind);
        Assert.Equal("catalogue", path.EndpointSchema);
        Assert.Equal("items", path.EndpointTable);
        Assert.Collection(
            path.Steps,
            up =>
            {
                Assert.Equal("order_items", up.Table);
                Assert.Equal("", up.Schema);
            },
            down =>
            {
                Assert.Equal("items", down.Table);
                Assert.Equal("catalogue", down.Schema);
            });
    }

    /// <summary>And the catalog carrying it registers: the association is accepted, not refused.</summary>
    [Fact]
    public void The_catalog_registers()
    {
        var (entitlements, associations) = Declare();
        var catalog = Catalog(entitlements, associations);

        CatalogValidator.Validate(catalog);
    }

    /// <summary>
    /// And the statement plans and answers: the chain is built across the two sources and an order
    /// is visible to the vendor whose item one of its lines carries — which is the whole of what
    /// F84 stood in the way of.
    /// </summary>
    [Fact]
    public async Task A_statement_over_the_target_reads_the_orders_the_path_reaches()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        await using var engine = await EngineAsync();
        var entitled = engine.WithEntitlements();
        var prepared = await entitled.PrepareAsync(
            "SELECT id FROM warehouse.orders ORDER BY id", VendorContext());

        var orders = Assert.Single(prepared.Entitlements.Tables, t => t.Table == "orders");
        Assert.Equal(TableVisibility.Some, orders.Visibility);

        // Order 1 carries the line that carries vendor 1's item; order 2 carries vendor 2's.
        Assert.Equal(["1"], await RowsAsync(engine, prepared));
    }

    /// <summary>
    /// The report names the schema of every step, so a host reads which source each hop ran in
    /// (design 38 §6, D265 (f)).
    /// </summary>
    [Fact]
    public async Task The_report_names_each_hops_source()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        await using var engine = await EngineAsync();
        var prepared = await engine.WithEntitlements().PrepareAsync(
            "SELECT id FROM warehouse.orders ORDER BY id", VendorContext());

        var explained = await prepared.ExplainAsync();
        var orders = Assert.Single(explained.Tables, t => t.Table == "orders");
        Assert.Equal("warehouse", orders.Schema);
        var path = Assert.Single(orders.Paths);
        Assert.Equal("vendor", path.Kind);
        Assert.Equal("catalogue", path.EndpointSchema);
        Assert.Equal("items", path.EndpointTable);
        Assert.Collection(
            path.Steps,
            up =>
            {
                Assert.Equal("warehouse", up.Schema);
                Assert.Equal("order_items", up.Table);
                Assert.True(up.ToChild);
            },
            down =>
            {
                Assert.Equal("catalogue", down.Schema);
                Assert.Equal("items", down.Table);
                Assert.False(down.ToChild);
            });

        // Honest: a POCO pair takes no query text at all, so no part of the marker went with one.
        Assert.False(orders.RowPredicatePushed);
    }

    /// <summary>
    /// The chain's reads are the mechanism's own on the wire, in <b>both</b> sources, and the
    /// client's invariant holds each of them as disclosing nothing and unreachable from the
    /// statement (D265 §7, <c>Read.correlation</c>).
    /// </summary>
    /// <remarks>
    /// The plan reached the client through <c>PlanValidator</c> already — an engine refuses a plan
    /// its own I-IR-E check rejects — so what this adds is the positive half: the two reads are
    /// there, they are marked, and they are of the two sources the chain crossed.
    /// </remarks>
    [Fact]
    public async Task The_chains_reads_are_marked_as_correlations_in_both_sources()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        await using var engine = await EngineAsync();
        var prepared = await engine.WithEntitlements().PrepareAsync(
            "SELECT id FROM warehouse.orders ORDER BY id", VendorContext());

        var correlations = new HashSet<string>(StringComparer.Ordinal);
        Collect(prepared.Plan.Root, correlations);

        Assert.Equal(
            ["catalogue.items", "warehouse.order_items"],
            correlations.Order(StringComparer.Ordinal));
        return;

        // Into a pushed subtree too, which is where a read that went to a source lives.
        static void Collect(Rel rel, HashSet<string> into)
        {
            if (rel.KindCase == Rel.KindOneofCase.Read && rel.Read.Correlation)
            {
                into.Add($"{rel.Read.Table.Schema}.{rel.Read.Table.Table}");
            }

            if (rel.KindCase == Rel.KindOneofCase.RemoteQuery && rel.RemoteQuery.PushedPlan is { } pushed)
            {
                Collect(pushed, into);
            }

            foreach (var input in PlanWalker.Inputs(rel))
            {
                Collect(input, into);
            }
        }
    }

    // ---------------------------------------------------------------- the engine

    private async ValueTask<ChalkEngine> EngineAsync()
    {
        var (entitlements, associations) = Declare();
        return await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "f84-cross-source",
            Sources = [Warehouse(entitlements), Catalogue(entitlements)],
            Associations = associations,
            Planner = sidecar.CreatePlanner(),
        });
    }

    private static RequestContext VendorContext()
    {
        var (entitlements, _) = Declare();
        return entitlements.Bind(new TenancyPrincipal
        {
            User = 1,
            Grants =
            [
                Grant.ForTenancy(
                    entitlements.Policy.Tenancy("vendor"), 1, entitlements.Policy.Role("vendor")),
            ],
        });
    }

    private static async Task<string[]> RowsAsync(ChalkEngine engine, PreparedQuery prepared)
    {
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

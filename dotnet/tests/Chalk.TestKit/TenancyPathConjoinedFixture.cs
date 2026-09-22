using Chalk.Catalog;
using Chalk.Client;
using Chalk.Entitlements.Tenancy;
using Chalk.Sources.Poco;
using CatalogContext = Chalk.Catalog.CatalogContext;

namespace Chalk.TestKit;

/// <summary>
/// The inventory shape a conjoined confinement runs across a path in (design 47 §6, D279): a table
/// that holds one tenancy kind on its own row and reaches the other along an <c>Inherited</c> path.
/// </summary>
/// <remarks>
/// <para>
/// <c>positions</c> holds its warehouse <b>directly</b> and inherits its supplier through
/// <c>products</c>. A grant confined along both — a reviewer for supplier A, only in Singapore —
/// therefore reads one column off the target's row and one off the endpoint's, which is the
/// cross-row group the compiler forms and the path predicate decides above the join.
/// </para>
/// <para>
/// <c>movements</c> is the same thing one step further out: it inherits the supplier through
/// <c>positions</c>, which inherits it through <c>products</c>, so the path the compiler flattens
/// has two steps and the chain two joins. It holds its own warehouse directly as well, so the
/// conjunction is answerable there too.
/// </para>
/// <para>
/// The data is chosen so that each half of the conjunction fails on its own somewhere: supplier A
/// has stock in Tokyo as well as Singapore, and Singapore holds supplier B's stock as well as A's.
/// A reviewer confined to A-in-Singapore must see neither, which is what the canaries assert.
/// </para>
/// </remarks>
public sealed class TenancyPathConjoinedFixture
{
    private TenancyPathConjoinedFixture(PocoSource source, TenancyEntitlements entitlements)
    {
        Source = source;
        Entitlements = entitlements;
        Catalog = new CatalogContext
        {
            ContextId = TenancyFixture.ContextId,
            Epoch = TenancyFixture.Epoch,
            Schemas = [source.DescribeSchema()],
        };
        CatalogValidator.Validate(Catalog);
    }

    /// <summary>
    /// The name of the one source this fixture's tables live in — the <b>schema</b> name, which is
    /// what a statement qualifies a table with (D270 §1).
    /// </summary>
    public const string SourceName = "stock";

    /// <summary>The identifier of the runtime that serves it, which is a different thing.</summary>
    private const string RuntimeId = "stock-mem";

    public PocoSource Source { get; }

    public TenancyEntitlements Entitlements { get; }

    public CatalogContext Catalog { get; }

    public static TenancyPathConjoinedFixture Shared => LazyShared.Value;

    private static readonly Lazy<TenancyPathConjoinedFixture> LazyShared = new(() => Create());

    public static TenancyPathConjoinedFixture Create()
    {
        var schema = Tables(null).DescribeSchema();
        var entitlements = Policy(schema).Compile([schema]);
        return new TenancyPathConjoinedFixture(Tables(entitlements), entitlements);
    }

    // ---------------------------------------------------------------- the rows

    /// <summary>A supplier: the kind a position holds along the path, held here on the row.</summary>
    public sealed record Supplier(int Id, string Name);

    /// <summary>A warehouse: the kind a position holds directly, keyed by its code.</summary>
    public sealed record Warehouse(string Code, string City);

    /// <summary>
    /// A product, supplied by exactly one supplier: the <b>endpoint</b> of the path, where the
    /// supplier key is on the row.
    /// </summary>
    public sealed record Product(int Id, int SupplierId, string Name);

    /// <summary>
    /// A stock position: the target. Its warehouse is on its own row and its supplier is one join
    /// away, which is the shape the conjunction has to span.
    /// </summary>
    public sealed record Position(int Id, int ProductId, string WarehouseCode, int Quantity, long Cost);

    /// <summary>A movement of a position: the same shape with the path one step longer.</summary>
    public sealed record Movement(int Id, int PositionId, string WarehouseCode, int Delta, string Note);

    /// <summary>Two suppliers. A has stock in both warehouses; B has stock in Singapore too.</summary>
    public static IReadOnlyList<Supplier> Suppliers { get; } =
    [
        new(1, "Acme"),
        new(2, "Brightwork"),
    ];

    /// <summary>Two warehouses, so a confinement to one leaves rows in the other to be missed.</summary>
    public static IReadOnlyList<Warehouse> Warehouses { get; } =
    [
        new("SIN", "Singapore"),
        new("TYO", "Tokyo"),
    ];

    public static IReadOnlyList<Product> Products { get; } =
    [
        new(1, 1, "Anvil"),
        new(2, 1, "Bellows"),
        new(3, 2, "Caliper"),
    ];

    /// <summary>
    /// Five positions. Supplier A in Singapore (1, 5), supplier A in Tokyo (2) — the canary a
    /// warehouse-confined reviewer must never reach — and supplier B in Singapore (3) and Tokyo (4),
    /// the canary a supplier-confined one must never reach.
    /// </summary>
    public static IReadOnlyList<Position> Positions { get; } =
    [
        new(1, 1, "SIN", 10, 1000),
        new(2, 1, "TYO", 20, 2000),
        new(3, 3, "SIN", 30, 3000),
        new(4, 3, "TYO", 40, 4000),
        new(5, 2, "SIN", 50, 5000),
    ];

    /// <summary>One movement per position, so the two-step path answers the same question.</summary>
    public static IReadOnlyList<Movement> Movements { get; } =
    [
        new(1, 1, "SIN", 5, "a-sin"),
        new(2, 2, "TYO", 6, "a-tyo"),
        new(3, 3, "SIN", 7, "b-sin"),
        new(4, 4, "TYO", 8, "b-tyo"),
        new(5, 5, "SIN", 9, "a-sin-two"),
    ];

    // ---------------------------------------------------------------- the declaration

    /// <summary>
    /// The model, as declarations. Two tenancy kinds, either of which may confine the other, and a
    /// target that resolves one on its row and the other along a path.
    /// </summary>
    public static TenancyPolicy Policy(SchemaDescriptor schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var catalog = new CatalogContext
        {
            ContextId = TenancyFixture.ContextId,
            Epoch = TenancyFixture.Epoch,
            Schemas = [schema],
        };

        var p = TenancyPolicy.Declare(catalog);
        var source = p.Source(SourceName);
        p.AllowGlobalGrants();

        var reviewer = p.Role("reviewer");
        var keeper = p.Role("keeper");
        var finance = p.Role("finance");

        var supplier = p.Tenancy("supplier");
        var warehouse = p.Tenancy("warehouse");
        var confidential = p.Realm("confidential");

        var suppliers = source.Table("suppliers");
        var warehouses = source.Table("warehouses");
        var products = source.Table("products");
        var positions = source.Table("positions");
        var movements = source.Table("movements");

        suppliers.Tenancy(t => t.Direct(supplier, suppliers.Column("id")));
        warehouses.Tenancy(t => t.Direct(warehouse, warehouses.Column("code")));
        products.Tenancy(t => t.Direct(supplier, products.Column("supplier_id")));

        // The target. The warehouse is on the row; the supplier is at the end of the path, and a
        // grant confining one by the other spans the two rows.
        positions
            .Tenancy(t => t
                .Direct(warehouse, positions.Column("warehouse_code"))
                .Inherited(supplier).Through(products))
            // A column the supplier's own people may read and the warehouse's may not: its verdict
            // is decided by the same conjunction the row is, so it reads on an A-in-Singapore row
            // and is withheld on every other.
            .Realm(confidential, positions.Column("cost"))
            .Access(new AccessRule
            {
                Roles = [reviewer],
                Realm = confidential,
                Grants = Verdict.Full,
            });

        // The same, one step further out: the path to `products` is flattened through `positions`,
        // so the chain carries two joins rather than one.
        movements
            .Tenancy(t => t
                .Direct(warehouse, movements.Column("warehouse_code"))
                .Inherited(supplier).Through(positions));

        _ = keeper;
        _ = finance;
        return p;
    }

    /// <summary>The handles the principals below hold their grants in (D270 §1).</summary>
    public static TenancyPolicy Handles { get; } = Policy(Tables(null).DescribeSchema());

    public static Kind Supplier1 => Handles.Tenancy("supplier");

    public static Kind WarehouseKind => Handles.Tenancy("warehouse");

    public static Role Reviewer => Handles.Role("reviewer");

    public static Role Keeper => Handles.Role("keeper");

    public static Role Finance => Handles.Role("finance");

    // ---------------------------------------------------------------- the principals

    /// <summary>
    /// The five of design 47 §6. The first is the grant the whole design exists for: a reviewer for
    /// supplier A, only in Singapore.
    /// </summary>
    public static IReadOnlyList<(string Name, TenancyPrincipal Principal)> Principals { get; } =
    [
        ("a-reviewer-in-sin", new TenancyPrincipal
        {
            User = 1,
            MaskKey = "k",
            Grants = [Grant.ForTenancy(Supplier1, 1, Reviewer).Within(WarehouseKind, "SIN")],
        }),
        ("a-reviewer", new TenancyPrincipal
        {
            User = 2,
            MaskKey = "k",
            Grants = [Grant.ForTenancy(Supplier1, 1, Reviewer)],
        }),
        ("b-reviewer-in-sin", new TenancyPrincipal
        {
            User = 3,
            MaskKey = "k",
            Grants = [Grant.ForTenancy(Supplier1, 2, Reviewer).Within(WarehouseKind, "SIN")],
        }),
        ("sin-keeper", new TenancyPrincipal
        {
            User = 4,
            MaskKey = "k",
            Grants = [Grant.ForTenancy(WarehouseKind, "SIN", Keeper)],
        }),
        ("finance", new TenancyPrincipal
        {
            User = 5,
            MaskKey = "k",
            Grants = [Grant.Global(Finance)],
        }),
    ];

    /// <summary>Every principal, bound against this fixture's own compilation.</summary>
    public IReadOnlyList<(string Name, RequestContext Context)> Bound =>
        [.. Principals.Select(p => (p.Name, Entitlements.Bind(p.Principal)))];

    // ---------------------------------------------------------------- the catalog

    private static PocoSource Tables(TenancyEntitlements? entitlements)
    {
        var builder = new PocoSourceBuilder(RuntimeId, SourceName)
            .NamingPolicy(PocoNamingPolicy.SnakeCase);

        builder.AddTable("suppliers", Suppliers, t =>
        {
            t.OrderedBy(s => s.Id).UniqueKey(s => s.Id);
            Attach(t, entitlements, "suppliers");
        });

        builder.AddTable("warehouses", Warehouses, t =>
        {
            t.OrderedBy(w => w.Code).UniqueKey(w => w.Code);
            Attach(t, entitlements, "warehouses");
        });

        builder.AddTable("products", Products, t =>
        {
            t.OrderedBy(x => x.Id).UniqueKey(x => x.Id).ForeignKey(x => x.SupplierId)
                .References<Supplier>(s => s.Id, verify: true);
            Attach(t, entitlements, "products");
        });

        builder.AddTable("positions", Positions, t =>
        {
            t.OrderedBy(x => x.Id).UniqueKey(x => x.Id)
                .ForeignKey(x => x.ProductId).References<Product>(p => p.Id, verify: true)
                .ForeignKey(x => x.WarehouseCode).References<Warehouse>(w => w.Code, verify: true);
            Attach(t, entitlements, "positions");
        });

        builder.AddTable("movements", Movements, t =>
        {
            t.OrderedBy(x => x.Id).UniqueKey(x => x.Id)
                .ForeignKey(x => x.PositionId).References<Position>(p => p.Id, verify: true)
                .ForeignKey(x => x.WarehouseCode).References<Warehouse>(w => w.Code, verify: true);
            Attach(t, entitlements, "movements");
        });

        return builder.Build();
    }

    private static void Attach<T>(
        PocoTableBuilder<T> table, TenancyEntitlements? entitlements, string name)
        where T : class
    {
        var descriptor = entitlements?.For(SourceName, name);
        if (descriptor is not null)
        {
            table.Entitlement(descriptor);
        }
    }
}

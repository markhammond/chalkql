using Chalk.Catalog;
using Chalk.Entitlements;
using Chalk.Entitlements.Tenancy;
using Chalk.Sources.Ado;
using Chalk.Sources.DuckDb;
using Chalk.Sources.Poco;

namespace Chalk.Sample.Tutorial;

/// <summary>
/// The marketplace as a catalog: the same rows in three places, and the one place they are
/// described.
/// </summary>
/// <remarks>
/// <para>
/// The POCO source is the shape of everything: the columns come from the records, the keys and the
/// foreign keys are declared on the builder, and the two databases take their DDL from the
/// descriptors that source produces. One definition, three copies, and a chapter that wants the
/// data in memory and a chapter that wants it behind a driver are reading the same marketplace.
/// </para>
/// <para>
/// The reference tables — customers, suppliers, products, employees, regions, warehouses — live in
/// SQLite, standing in for an OLTP store. The transactional facts, orders and order_details, live
/// together in DuckDB: an order and its lines belong together, and a supplier's existential hop then
/// has its target and its bridge in one source.
/// </para>
/// </remarks>
public static class Marketplace
{
    /// <summary>The schema the in-process copy is registered under — the default, so SQL need not qualify it.</summary>
    public const string InProcess = "main";

    /// <summary>The schema the SQLite reference tables are registered under.</summary>
    public const string Oltp = "oltp";

    /// <summary>The schema the DuckDB facts are registered under.</summary>
    public const string Facts = "facts";

    /// <summary>Which schema each half of the marketplace is in, for a policy that spans them.</summary>
    /// <param name="Reference">customers, suppliers, products, employees, regions, warehouses.</param>
    /// <param name="Transactional">orders and order_details.</param>
    /// <param name="Live">inventory_positions, inventory_movements, market_prices.</param>
    public sealed record Placement(string Reference, string Transactional, string Live)
    {
        /// <summary>Everything in memory, which is what most chapters want.</summary>
        public static Placement Memory { get; } = new(InProcess, InProcess, InProcess);

        /// <summary>The reference tables in SQLite, the facts in DuckDB.</summary>
        public static Placement Federated { get; } = new(Oltp, Facts, InProcess);
    }

    /// <summary>
    /// The whole marketplace in one in-process source. Every key and every foreign key is declared
    /// and verified — including <c>employees.manager_id</c>, which names another row of the table it
    /// is on, and is this tree's first self-referential foreign key.
    /// </summary>
    public static PocoSource Poco(
        TenancyEntitlements? entitlements = null,
        IReadOnlyList<Order>? orders = null,
        IReadOnlyList<OrderDetail>? lines = null,
        string sourceId = "shop") =>
        Poco(out _, out _, out _, entitlements, orders, lines, sourceId: sourceId);

    /// <summary>
    /// The same, handing back the typed handles chapters 15 to 17 refresh through. A refresh names a
    /// table rather than describing one, and the builder is where the name is.
    /// </summary>
    public static PocoSource Poco(
        out PocoTable<InventoryPosition> positions,
        out PocoTable<InventoryMovement> movements,
        out PocoTable<MarketPrice> prices,
        TenancyEntitlements? entitlements = null,
        IReadOnlyList<Order>? orders = null,
        IReadOnlyList<OrderDetail>? lines = null,
        IReadOnlyList<InventoryPosition>? openingPositions = null,
        IReadOnlyList<InventoryMovement>? ledger = null,
        string sourceId = "shop")
    {
        orders ??= Data.OrderRows();
        lines ??= Data.OrderDetails(orders);

        var builder = new PocoSourceBuilder(sourceId, InProcess)
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .DefaultDecimalScale(2);

        builder.AddTable("customers", Data.Customers, t => Entitle(t
            .OrderedBy(c => c.CustomerId)
            .UniqueKey(c => c.CustomerId), entitlements, "customers"));
        builder.AddTable("suppliers", Data.Suppliers, t => Entitle(t
            .OrderedBy(s => s.SupplierId)
            .UniqueKey(s => s.SupplierId), entitlements, "suppliers"));
        builder.AddTable("regions", Data.Regions, t => Entitle(t
            .OrderedBy(r => r.RegionId)
            .UniqueKey(r => r.RegionId), entitlements, "regions"));
        builder.AddTable("warehouses", Data.Warehouses, t => Entitle(t
            .OrderedBy(w => w.WarehouseId)
            .UniqueKey(w => w.WarehouseId), entitlements, "warehouses"));

        // The self-referential one. `manager_id` is nullable because somebody has to be at the top,
        // and verification checks every non-NULL key against this same table.
        builder.AddTable("employees", Data.Employees, t => Entitle(t
            .OrderedBy(e => e.EmployeeId)
            .UniqueKey(e => e.EmployeeId)
            .ForeignKey(e => e.ManagerId).References<Employee>(e => e.EmployeeId, verify: true),
            entitlements, "employees"));

        builder.AddTable("products", Data.Products, t => Entitle(t
            .OrderedBy(p => p.ProductId)
            .UniqueKey(p => p.ProductId)
            .Index(p => p.SupplierId)
            .ForeignKey(p => p.SupplierId).References<Supplier>(s => s.SupplierId, verify: true),
            entitlements, "products"));

        builder.AddTable("orders", orders, t => Entitle(t
            .OrderedBy(o => o.OrderId)
            .UniqueKey(o => o.OrderId)
            .ForeignKey(o => o.CustomerId).References<Customer>(c => c.CustomerId, verify: true)
            .ForeignKey(o => o.EmployeeId).References<Employee>(e => e.EmployeeId, verify: true)
            .ForeignKey(o => o.RegionId).References<Region>(r => r.RegionId, verify: true),
            entitlements, "orders"));

        builder.AddTable("usd_rates", Data.UsdRates(orders), t => Entitle(t
            .OrderedBy(r => r.Currency).ThenBy(r => r.Ts)
            .UniqueKey(r => r.Currency, r => r.Ts), entitlements, "usd_rates"));

        builder.AddTable("order_details", lines, t => Entitle(t
            .OrderedBy(d => d.OrderId).ThenBy(d => d.ProductId)
            .UniqueKey(d => d.OrderId, d => d.ProductId)
            .ForeignKey(d => d.OrderId).References<Order>(o => o.OrderId, verify: true)
            .ForeignKey(d => d.ProductId).References<Product>(p => p.ProductId, verify: true),
            entitlements, "order_details"));

        // The four live tables. They are in the same source as everything else, so the policy
        // that spans them is one declaration rather than two.
        builder.AddTable(
            "inventory_positions",
            openingPositions ?? Data.Positions(),
            out positions,
            t => Entitle(t
                .OrderedBy(p => p.WarehouseId).ThenBy(p => p.ProductId)
                .UniqueKey(p => p.WarehouseId, p => p.ProductId)
                .ForeignKey(p => p.ProductId).References<Product>(x => x.ProductId, verify: true)
                .ForeignKey(p => p.SupplierId).References<Supplier>(x => x.SupplierId, verify: true)
                .ForeignKey(p => p.WarehouseId).References<Warehouse>(w => w.WarehouseId, verify: true),
                entitlements, "inventory_positions"));

        builder.AddTable(
            "inventory_movements",
            ledger ?? Data.Movements(),
            out movements,
            t => Entitle(t
                .OrderedBy(m => m.MovementId)
                .UniqueKey(m => m.MovementId)
                .ForeignKey(m => m.ProductId).References<Product>(x => x.ProductId, verify: true)
                .ForeignKey(m => m.SupplierId).References<Supplier>(x => x.SupplierId, verify: true)
                .ForeignKey(m => m.WarehouseId).References<Warehouse>(w => w.WarehouseId, verify: true),
                entitlements, "inventory_movements"));

        builder.AddTable(
            "market_prices",
            Data.Prices(),
            out prices,
            t => Entitle(t
                // Ordered by the feed's own sequence, because that is the order it appends in; the
                // index over (product_id, ts) is what an ASOF join reads (chapter 17).
                .OrderedBy(p => p.PriceId)
                .UniqueKey(p => p.PriceId)
                .UniqueIndex(p => p.ProductId, p => p.Ts)
                .ForeignKey(p => p.ProductId).References<Product>(x => x.ProductId, verify: true),
                entitlements, "market_prices"));

        return builder.Build();
    }

    /// <summary>
    /// The reference half in SQLite: the DDL generated from the descriptors above, the rows loaded
    /// through ADO parameters, and the keys declared here because a SQL schema collection reports
    /// columns and types and Chalk acts on constraints.
    /// </summary>
    public static AdoSource Reference(Database sqlite, PocoSource shape, TenancyEntitlements? entitlements)
    {
        ArgumentNullException.ThrowIfNull(sqlite);
        ArgumentNullException.ThrowIfNull(shape);

        var schema = shape.DescribeSchema();
        sqlite.Load(Table(schema, "customers"), Data.Customers,
            c => [c.CustomerId, c.CompanyName, c.ContactName, c.Country]);
        sqlite.Load(Table(schema, "suppliers"), Data.Suppliers,
            s => [s.SupplierId, s.CompanyName, s.Country]);
        sqlite.Load(Table(schema, "regions"), Data.Regions, r => [r.RegionId, r.Name]);
        sqlite.Load(Table(schema, "warehouses"), Data.Warehouses,
            w => [w.WarehouseId, w.Name, w.Country]);
        sqlite.Load(Table(schema, "employees"), Data.Employees,
            e => [e.EmployeeId, e.ManagerId, e.Name, e.Title]);
        sqlite.Load(Table(schema, "products"), Data.Products,
            p => [p.ProductId, p.SupplierId, p.ProductName, p.UnitPrice]);

        return ReferenceBuilder(sqlite, shape, entitlements).Build();
    }

    /// <summary>
    /// The registration on its own, without loading any rows — for a second build with a policy on
    /// it, or for a chapter that wants to declare a function beside the tables.
    /// </summary>
    public static AdoSourceBuilder ReferenceBuilder(
        Database sqlite, PocoSource shape, TenancyEntitlements? entitlements)
    {
        ArgumentNullException.ThrowIfNull(sqlite);
        ArgumentNullException.ThrowIfNull(shape);

        var schema = shape.DescribeSchema();
        var builder = new AdoSourceBuilder(sqlite.SourceId, () => Connect(sqlite), Oltp)
            .Dialect(sqlite.Dialect)
            .Capabilities(AdoCapabilities.For(sqlite.Dialect))
            .RowCounts(AdoSourceBuilder.RowCountMode.Exact)
            .AddTable("customers", Table(schema, "customers").Columns, configure: t => t.UniqueKey("customer_id"))
            .AddTable("suppliers", Table(schema, "suppliers").Columns, configure: t => t.UniqueKey("supplier_id"))
            .AddTable("regions", Table(schema, "regions").Columns, configure: t => t.UniqueKey("region_id"))
            .AddTable("warehouses", Table(schema, "warehouses").Columns, configure: t => t.UniqueKey("warehouse_id"))
            .AddTable(
                "employees",
                Table(schema, "employees").Columns,
                configure: t => t
                    .UniqueKey("employee_id")
                    .ForeignKey("employees_manager", ["manager_id"], "employees", ["employee_id"]))
            .AddTable(
                "products",
                Table(schema, "products").Columns,
                configure: t => t
                    .UniqueKey("product_id")
                    .Index("ix_products_supplier", unique: false, "supplier_id")
                    .ForeignKey("products_supplier", ["supplier_id"], "suppliers", ["supplier_id"]));

        Attach(builder, entitlements, Oltp);
        return builder;
    }

    /// <summary>The transactional half in DuckDB: the orders and the lines, together.</summary>
    public static AdoSource Transactional(
        Database duck,
        PocoSource shape,
        IReadOnlyList<Order> orders,
        IReadOnlyList<OrderDetail> lines,
        TenancyEntitlements? entitlements)
    {
        ArgumentNullException.ThrowIfNull(duck);
        ArgumentNullException.ThrowIfNull(shape);

        var schema = shape.DescribeSchema();
        duck.Load(Table(schema, "orders"), orders,
            o => [o.OrderId, o.CustomerId, o.EmployeeId, o.RegionId, o.OrderDate, o.Freight, o.Currency]);
        duck.Load(Table(schema, "order_details"), lines,
            d => [d.OrderId, d.ProductId, d.UnitPrice, d.Quantity, d.Discount]);
        duck.Load(Table(schema, "usd_rates"), Data.UsdRates(orders), r => [r.Currency, r.Ts, r.Rate]);

        return TransactionalBuilder(duck, shape, entitlements).Build();
    }

    /// <summary>The same, for the transactional half.</summary>
    public static AdoSourceBuilder TransactionalBuilder(
        Database duck, PocoSource shape, TenancyEntitlements? entitlements)
    {
        ArgumentNullException.ThrowIfNull(duck);
        ArgumentNullException.ThrowIfNull(shape);

        var schema = shape.DescribeSchema();
        var builder = new AdoSourceBuilder(duck.SourceId, () => Connect(duck), Facts)
            .Dialect(duck.Dialect)
            .Capabilities(AdoCapabilities.For(duck.Dialect))
            .RowCounts(AdoSourceBuilder.RowCountMode.Exact)
            .AddTable("orders", Table(schema, "orders").Columns, configure: t => t.UniqueKey("order_id"))
            .AddTable("usd_rates", Table(schema, "usd_rates").Columns, configure: t => t.UniqueKey("currency", "ts"))
            .AddTable(
                "order_details",
                Table(schema, "order_details").Columns,
                configure: t => t
                    .UniqueKey("order_id", "product_id")
                    .ForeignKey("details_order", ["order_id"], "orders", ["order_id"]));

        Attach(builder, entitlements, Facts);
        return builder;
    }

    /// <summary>
    /// The whole marketplace in the DuckDB database, reference tables and all. Chapters 13 and 14
    /// want every table on the far side of one boundary: what they are about is what a fold does to
    /// generated SQL, and a second source would only add a join the chapter is not about.
    /// </summary>
    public static AdoSource Everything(
        Database duck,
        PocoSource shape,
        IReadOnlyList<Order> orders,
        IReadOnlyList<OrderDetail> lines,
        TenancyEntitlements? entitlements)
    {
        ArgumentNullException.ThrowIfNull(duck);
        ArgumentNullException.ThrowIfNull(shape);

        var schema = shape.DescribeSchema();
        duck.Load(Table(schema, "customers"), Data.Customers,
            c => [c.CustomerId, c.CompanyName, c.ContactName, c.Country]);
        duck.Load(Table(schema, "suppliers"), Data.Suppliers,
            s => [s.SupplierId, s.CompanyName, s.Country]);
        duck.Load(Table(schema, "regions"), Data.Regions, r => [r.RegionId, r.Name]);
        duck.Load(Table(schema, "warehouses"), Data.Warehouses, w => [w.WarehouseId, w.Name, w.Country]);
        duck.Load(Table(schema, "employees"), Data.Employees,
            e => [e.EmployeeId, e.ManagerId, e.Name, e.Title]);
        duck.Load(Table(schema, "products"), Data.Products,
            p => [p.ProductId, p.SupplierId, p.ProductName, p.UnitPrice]);
        duck.Load(Table(schema, "orders"), orders,
            o => [o.OrderId, o.CustomerId, o.EmployeeId, o.RegionId, o.OrderDate, o.Freight, o.Currency]);
        duck.Load(Table(schema, "order_details"), lines,
            d => [d.OrderId, d.ProductId, d.UnitPrice, d.Quantity, d.Discount]);
        duck.Load(Table(schema, "usd_rates"), Data.UsdRates(orders), r => [r.Currency, r.Ts, r.Rate]);

        return EverythingBuilder(duck, shape, entitlements).Build();
    }

    /// <summary>The registration of <see cref="Everything"/>, without loading any rows.</summary>
    public static AdoSourceBuilder EverythingBuilder(
        Database duck, PocoSource shape, TenancyEntitlements? entitlements)
    {
        ArgumentNullException.ThrowIfNull(duck);
        ArgumentNullException.ThrowIfNull(shape);

        var schema = shape.DescribeSchema();
        var builder = new AdoSourceBuilder(duck.SourceId, () => Connect(duck), Facts)
            .Dialect(duck.Dialect)
            .Capabilities(AdoCapabilities.For(duck.Dialect))
            .RowCounts(AdoSourceBuilder.RowCountMode.Exact)
            .AddTable("customers", Table(schema, "customers").Columns, configure: t => t.UniqueKey("customer_id"))
            .AddTable("suppliers", Table(schema, "suppliers").Columns, configure: t => t.UniqueKey("supplier_id"))
            .AddTable("regions", Table(schema, "regions").Columns, configure: t => t.UniqueKey("region_id"))
            .AddTable("warehouses", Table(schema, "warehouses").Columns, configure: t => t.UniqueKey("warehouse_id"))
            .AddTable(
                "employees",
                Table(schema, "employees").Columns,
                configure: t => t
                    .UniqueKey("employee_id")
                    .ForeignKey("employees_manager", ["manager_id"], "employees", ["employee_id"]))
            .AddTable(
                "products",
                Table(schema, "products").Columns,
                configure: t => t
                    .UniqueKey("product_id")
                    .ForeignKey("products_supplier", ["supplier_id"], "suppliers", ["supplier_id"]))
            .AddTable(
                "orders",
                Table(schema, "orders").Columns,
                configure: t => t
                    .UniqueKey("order_id")
                    .ForeignKey("orders_customer", ["customer_id"], "customers", ["customer_id"])
                    .ForeignKey("orders_employee", ["employee_id"], "employees", ["employee_id"])
                    .ForeignKey("orders_region", ["region_id"], "regions", ["region_id"]))
            .AddTable("usd_rates", Table(schema, "usd_rates").Columns, configure: t => t.UniqueKey("currency", "ts"))
            .AddTable(
                "order_details",
                Table(schema, "order_details").Columns,
                configure: t => t
                    .UniqueKey("order_id", "product_id")
                    .ForeignKey("details_order", ["order_id"], "orders", ["order_id"])
                    .ForeignKey("details_product", ["product_id"], "products", ["product_id"]));

        Attach(builder, entitlements, Facts);
        return builder;
    }

    /// <summary>
    /// One database holding one physical table and nothing else: a partition of chapter 7's logical
    /// table, registered under its own schema so a statement could name it directly if it wanted to.
    /// </summary>
    public static AdoSource Partition(Database database, TableDescriptor table)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(table);
        return new AdoSourceBuilder(database.SourceId, () => Connect(database), database.SourceId)
            .Dialect(database.Dialect)
            .Capabilities(AdoCapabilities.For(database.Dialect))
            .RowCounts(AdoSourceBuilder.RowCountMode.Exact)
            .AddTable(table.Name, table.Columns)
            .Build();
    }

    /// <summary>One table's descriptor, by name.</summary>
    public static TableDescriptor Table(SchemaDescriptor schema, string name)
    {
        ArgumentNullException.ThrowIfNull(schema);
        return schema.FindTable(name)
            ?? throw new InvalidOperationException($"the marketplace has no table '{name}'.");
    }

    private static System.Data.Common.DbConnection Connect(Database database)
    {
        var connection = database.Open();
        return connection;
    }

    /// <summary>
    /// Attaches whatever the policy compiled for one table. A table the policy leaves unrestricted
    /// has no descriptor and gets none, which is why this is a lookup rather than a requirement.
    /// </summary>
    private static void Entitle<T>(
        PocoTableBuilder<T> table, TenancyEntitlements? entitlements, string name)
        where T : class
    {
        if (entitlements?.For(InProcess, name) is { } descriptor)
        {
            table.Entitlement(descriptor);
        }
    }

    private static void Attach(
        AdoSourceBuilder builder, TenancyEntitlements? entitlements, string schema)
    {
        if (entitlements is null)
        {
            return;
        }

        foreach (var (name, descriptor) in Descriptors(entitlements, schema))
        {
            builder.Entitlement(name, descriptor);
        }
    }

    /// <summary>
    /// The descriptors of one schema, by bare table name. <c>TenancyEntitlements.Tables</c> is keyed
    /// by <c>schema.table</c> — which is the whole reason a policy over two sources can carry two
    /// tables of the same name.
    /// </summary>
    private static IEnumerable<(string Name, TableEntitlementDescriptor Descriptor)> Descriptors(
        TenancyEntitlements entitlements, string schema)
    {
        var prefix = schema + ".";
        foreach (var (qualified, descriptor) in entitlements.Tables)
        {
            if (qualified.StartsWith(prefix, StringComparison.Ordinal))
            {
                yield return (qualified[prefix.Length..], descriptor);
            }
        }
    }
}

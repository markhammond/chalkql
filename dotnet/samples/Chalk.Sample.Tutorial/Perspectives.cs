using Chalk.Catalog;
using Chalk.Entitlements.Tenancy;

namespace Chalk.Sample.Tutorial;

/// <summary>
/// The marketplace's perspectives, declared once.
/// Customers and suppliers are two views of one relational graph; regions and warehouses are two
/// more axes across it; an employee is a <em>subject</em> rather than a tenancy, because the
/// merchant's staff serve every customer and belong to none.
/// </summary>
/// <remarks>
/// <para>
/// Every name enters once and comes back as a handle — a kind, a role, a realm, a source, a table, a
/// column. A misspelling is caught where it is written rather than where it is used, and a
/// handle of the wrong sort does not fit the parameter at all.
/// </para>
/// <para>
/// One declaration serves every chapter that has a policy in it, and the chapters differ only in
/// which schema the tables are in — which is what <see cref="Marketplace.Placement"/> carries.
/// </para>
/// </remarks>
public sealed class Perspectives
{
    private Perspectives(TenancyPolicy policy)
    {
        Policy = policy;
    }

    /// <summary>The policy itself, for a chapter that wants to compile it against a second catalog.</summary>
    public TenancyPolicy Policy { get; }

    // ---- the kinds -------------------------------------------------------------------------

    /// <summary>Who bought. An order holds one directly; a line inherits it from its order.</summary>
    public Kind Customer { get; private set; }

    /// <summary>Who supplies. A product holds one directly; an order is only <em>related</em> to one.</summary>
    public Kind Supplier { get; private set; }

    /// <summary>Where it was sold. A third axis, on <c>orders</c> and nothing else.</summary>
    public Kind Region { get; private set; }

    /// <summary>Where the stock is. The fourth kind, and chapters 15 to 17's second axis.</summary>
    public Kind Warehouse { get; private set; }

    /// <summary>The merchant's own staff — a subject, confinable within a customer.</summary>
    public SubjectKind Employee { get; private set; }

    // ---- the roles -------------------------------------------------------------------------

    /// <summary>A customer's own staff.</summary>
    public Role Buyer { get; private set; }

    /// <summary>A region's sales representative.</summary>
    public Role Sales { get; private set; }

    /// <summary>A supplier's representative.</summary>
    public Role Representative { get; private set; }

    /// <summary>A supplier's reviewer, confined to one warehouse.</summary>
    public Role Reviewer { get; private set; }

    /// <summary>A warehouse operator.</summary>
    public Role Operator { get; private set; }

    /// <summary>An employee acting as themselves.</summary>
    public Role Self { get; private set; }

    /// <summary>A customer's auditor, watching one employee on that customer's account.</summary>
    public Role Auditor { get; private set; }

    /// <summary>Finance, who holds the global grant this deployment had to switch on.</summary>
    public Role Finance { get; private set; }

    // ---- the tables ------------------------------------------------------------------------

    public Table Customers { get; private set; }

    public Table Suppliers { get; private set; }

    public Table Products { get; private set; }

    public Table Employees { get; private set; }

    public Table Orders { get; private set; }

    public Table OrderDetails { get; private set; }

    public Table Positions { get; private set; }

    public Table Movements { get; private set; }

    public Table Prices { get; private set; }

    /// <summary>
    /// Declares the marketplace's perspectives over <paramref name="catalog"/>. Every table is
    /// obtained from its source, so a policy that spans two of them never has to tell their tables
    /// apart by name.
    /// </summary>
    /// <param name="catalog">The catalog the policy is written against.</param>
    /// <param name="placement">Which schema each half of the marketplace is in.</param>
    /// <param name="live">
    /// Whether the four live tables are in this catalog. Chapters 1 to 14 do not register
    /// them, and a policy may not name a table its catalog does not hold.
    /// </param>
    public static Perspectives Declare(
        CatalogContext catalog, Marketplace.Placement placement, bool live = false)
    {
        ArgumentNullException.ThrowIfNull(placement);

        var p = TenancyPolicy.Declare(catalog);
        var self = new Perspectives(p);

        // Finance's grant reaches everywhere, and a grant that reaches everywhere is refused unless
        // the deployment says otherwise — which is what this line is.
        p.AllowGlobalGrants();

        self.Customer = p.Tenancy("customer");
        self.Supplier = p.Tenancy("supplier");
        self.Region = p.Tenancy("region");
        self.Warehouse = p.Tenancy("warehouse");
        self.Employee = p.Subject("employee", within: [self.Customer]);

        self.Buyer = p.Role("buyer");
        self.Sales = p.Role("sales");
        self.Representative = p.Role("representative");
        self.Reviewer = p.Role("reviewer");
        self.Operator = p.Role("operator");
        self.Self = p.Role("self");
        self.Auditor = p.Role("auditor");
        self.Finance = p.Role("finance");

        var reference = p.Source(placement.Reference);
        var transactional = p.Source(placement.Transactional);

        var customers = reference.Table("customers");
        var suppliers = reference.Table("suppliers");
        var products = reference.Table("products");
        var employees = reference.Table("employees");
        var orders = transactional.Table("orders");
        var orderDetails = transactional.Table("order_details");

        self.Customers = customers;
        self.Suppliers = suppliers;
        self.Products = products;
        self.Employees = employees;
        self.Orders = orders;
        self.OrderDetails = orderDetails;

        // Reference data, and it stays that way: a customer may read the catalogue of products and
        // the list of regions, because neither says anything about anybody's trade.
        customers.Unrestricted();
        suppliers.Unrestricted();
        reference.Table("regions").Unrestricted();

        // A rate is a fact about the market and belongs to nobody. What is confidential is the
        // amount it converts, and that restriction arrives through the order it is on.
        transactional.Table("usd_rates").Unrestricted();

        // The perspectives, spelled over the typed surface. The second restriction is what makes the catalogue a
        // catalogue: `Direct` names the column every supplier path walks to, and the predicate says
        // that reading the catalogue itself is not restricted — a marketplace whose customers
        // cannot see what is for sale has nothing to sell. What is confidential is who bought what,
        // and that is on the tables below.
        products.Tenancy(t => t
            .Direct(self.Supplier, products.Column("supplier_id"))
            .Public());

        orders.Tenancy(t => t
            .Direct(self.Customer, orders.Column("customer_id"))
            .Direct(self.Employee, orders.Column("employee_id"))
            .Direct(self.Region, orders.Column("region_id"))
            .Related(self.Supplier).Through(orderDetails).Through(products));

        // An employee sees their own row wherever it turns up; `title` is masked for everybody else.
        employees.Tenancy(t => t.ResourceOwner(employees.Column("employee_id")));
        employees
            .Access(new AccessRule
            {
                Roles = [Roles.Owner],
                Column = employees.Column("title"),
                Grants = Verdict.Full,
            })
            .Access(new AccessRule
            {
                Roles = [Roles.Visible],
                Column = employees.Column("title"),
                Grants = Verdict.Mask,
                Mask = Sql.Of("lambda v: SUBSTRING(v, 1, 1)"),
            });

        orderDetails.Tenancy(t => t
            .Inherited(self.Customer).Through(orders)
            .Inherited(self.Supplier).Through(products));

        // What a supplier may read of a line. `order_id`, `product_id` and `quantity` in full, by
        // saying nothing about them; `unit_price` is the *customer's* negotiated price, which is
        // withheld outright; `discount` is a population statistic and nothing else.
        orderDetails
            .Access(new AccessRule
            {
                Roles = [self.Representative, self.Reviewer],
                Column = orderDetails.Column("unit_price"),
                Grants = Verdict.None,
                Placeholder = Sql.Of("CAST(NULL AS DECIMAL(19,2))"),
            })
            .Access(new AccessRule
            {
                Roles = [self.Representative, self.Reviewer],
                Column = orderDetails.Column("discount"),
                Grants = Verdict.AggregateOnly,
                Aggregates = [Aggregate.Avg],
                MinGroupSize = 3,
            })

            // Read in order, which is what the order means. A column with any rule on it is
            // a protected column, and a principal no rule speaks for sees nothing — so the last
            // rule says who else may read it. `order_details` restricts no row of its own (its
            // visibility comes through its parents), so the roles are named rather than marked.
            .Access(new AccessRule
            {
                Roles = CustomerSide(self),
                Column = orderDetails.Column("unit_price"),
                Grants = Verdict.Full,
            })
            .Access(new AccessRule
            {
                Roles = CustomerSide(self),
                Column = orderDetails.Column("discount"),
                Grants = Verdict.Full,
            });

        // And what it may read of an order: the shipping cost is the merchant's, and the customer's
        // identity may be *tested* without being read.
        orders
            .Access(new AccessRule
            {
                Roles = [self.Representative, self.Reviewer],
                Column = orders.Column("freight"),
                Grants = Verdict.None,
                Placeholder = Sql.Of("CAST(NULL AS DECIMAL(19,2))"),
            })
            .Access(new AccessRule
            {
                Roles = [self.Representative, self.Reviewer],
                Column = orders.Column("customer_id"),
                Grants = Verdict.Test,
                Tests = [Test.Equals],
            })
            .Access(new AccessRule
            {
                Roles = CustomerSide(self),
                Column = orders.Column("freight"),
                Grants = Verdict.Full,
            })
            .Access(new AccessRule
            {
                Roles = CustomerSide(self),
                Column = orders.Column("customer_id"),
                Grants = Verdict.Full,
            });

        // The cross-source association: `order_details.product_id` names a product, and
        // when the two tables are in two sources a declared foreign key cannot say it, because a
        // foreign key names a table of its own schema. This can. Where they are in one source the
        // foreign key already says it, and declaring both would leave the step ambiguous.
        if (!string.Equals(placement.Transactional, placement.Reference, StringComparison.Ordinal))
        {
            orderDetails.Column("product_id").References(products.Column("product_id"));
        }

        if (live)
        {
            var liveSource = p.Source(placement.Live);
            var positions = liveSource.Table("inventory_positions");
            var movements = liveSource.Table("inventory_movements");
            var prices = liveSource.Table("market_prices");

            self.Positions = positions;
            self.Movements = movements;
            self.Prices = prices;

            liveSource.Table("warehouses").Unrestricted();

            // Both axes on the row. A position's supplier is its product's — the column is
            // denormalised from it and the foreign key checks that — and it is *on the row* because
            // a conjoined grant is an intersection of two dimensions, which both have to be
            // resolvable where the row is: supplier A's rows in Singapore, and nothing else.
            positions.Tenancy(t => t
                .Direct(self.Warehouse, positions.Column("warehouse_id"))
                .Direct(self.Supplier, positions.Column("supplier_id")));
            movements.Tenancy(t => t
                .Direct(self.Warehouse, movements.Column("warehouse_id"))
                .Direct(self.Supplier, movements.Column("supplier_id")));

            // A price is public. What is confidential is the quantity, and a valuation inherits that
            // restriction through the join rather than from the price.
            prices.Unrestricted();
        }

        return self;
    }

    /// <summary>
    /// Everyone who is not on the supplier's side of the marketplace. A column with a rule on it is
    /// protected, so the catch-all that follows the supplier's rules has to name who else may read
    /// it — and on a table whose rows are visible only through a parent there is no row predicate
    /// for <c>Roles.Visible</c> to stand for.
    /// </summary>
    private static IReadOnlyList<Role> CustomerSide(Perspectives self) =>
        [self.Buyer, self.Sales, self.Self, self.Auditor, self.Operator, self.Finance];

    /// <summary>
    /// Compiles the policy against the catalog it was declared over, with whatever associations the
    /// declaration added: an association is a catalog fact, and the compiler resolves a path step
    /// through one exactly as it does through a foreign key.
    /// </summary>
    public TenancyEntitlements Compile(CatalogContext catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return Policy.Compile(new CatalogContext
        {
            ContextId = catalog.ContextId,
            Epoch = catalog.Epoch,
            Schemas = catalog.Schemas,
            Associations = Policy.Associations,
        });
    }

    // ---- the principals --------------------------------------------------------------------

    /// <summary>One principal: who they are, what they hold, and the key their masks are keyed by.</summary>
    public static TenancyPrincipal Principal(object user, params Grant[] grants) => new()
    {
        User = user,
        Grants = grants,
        MaskKey = "tutorial",
    };

    /// <summary>Everything supplier A's representative may see, in every warehouse.</summary>
    public TenancyPrincipal SupplierRepresentative(string supplier = "A") =>
        Principal("rep:" + supplier, Grant.ForTenancy(Supplier, supplier, Representative));

    /// <summary>Everything in one warehouse, whoever supplies it.</summary>
    public TenancyPrincipal WarehouseOperator(string warehouse = "SIN") =>
        Principal("ops:" + warehouse, Grant.ForTenancy(Warehouse, warehouse, Operator));

    /// <summary>
    /// The intersection, not the union: supplier A's rows <em>in</em> Singapore and nothing else.
    /// Two separate grants would give the union; <c>Within</c> conjoins them.
    /// </summary>
    public TenancyPrincipal SupplierWarehouseReviewer(string supplier = "A", string warehouse = "SIN") =>
        Principal(
            "rev:" + supplier + "@" + warehouse,
            Grant.ForTenancy(Supplier, supplier, Reviewer).Within(Warehouse, warehouse));

    /// <summary>Everything, anywhere — which this deployment had to permit before it could be held.</summary>
    public TenancyPrincipal FinanceDesk() => Principal("finance", Grant.Global(Finance));
}

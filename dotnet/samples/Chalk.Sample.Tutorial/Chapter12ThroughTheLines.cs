using Chalk.Catalog;
using Chalk.Client;
using Chalk.Entitlements;
using Chalk.Entitlements.Tenancy;

namespace Chalk.Sample.Tutorial;

/// <summary>
/// Chapter 12 — through the lines. An order is not a supplier's; it is a customer's. But an order
/// <em>reaches</em> a supplier through the lines that carry that supplier's products, which is a
/// different kind of relationship — one-to-many and existential — and it compiles to a semi-join
/// rather than to a chain of parents.
/// </summary>
public static class Chapter12ThroughTheLines
{
    private const string Statement = """
        SELECT o.order_id, o.order_date, o.employee_id
        FROM facts.orders o
        WHERE o.order_id <= 40
        ORDER BY o.order_id
        """;

    public static async Task RunAsync(Func<IQueryPlanner> planner)
    {
        Tutorial.Chapter(
            12,
            "Through the lines",
            "Related, not Inherited. A line inherits its supplier from its product: exactly one,\n"
            + "always. An order is related to a supplier through the lines that carry its products:\n"
            + "zero, one or many. The declaration is one clause; the plan is a semi-join, and the\n"
            + "reached keys come from another source.");

        using var oltp = Database.Sqlite("oltp");
        using var facts = Database.DuckDb("facts");
        var shape = Marketplace.Poco();
        var orders = Data.OrderRows();
        var lines = Data.OrderDetails(orders);

        Marketplace.Reference(oltp, shape, entitlements: null);
        Marketplace.Transactional(facts, shape, orders, lines, entitlements: null);

        var catalog = new CatalogContext
        {
            ContextId = "tutorial-12",
            Epoch = 1,
            Schemas =
            [
                Marketplace.ReferenceBuilder(oltp, shape, entitlements: null).Build().DescribeSchema(),
                Marketplace.TransactionalBuilder(facts, shape, entitlements: null).Build().DescribeSchema(),
            ],
        };

        var perspectives = Perspectives.Declare(catalog, Marketplace.Placement.Federated);
        var entitlements = perspectives.Compile(catalog);

        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "tutorial-12",
            Sources =
            [
                Marketplace.ReferenceBuilder(oltp, shape, entitlements).Build(),
                Marketplace.TransactionalBuilder(facts, shape, entitlements).Build(),
            ],

            // The association travels with the catalog, because that is what it is: a fact about
            // the shape of the data, not about this query.
            Associations = perspectives.Policy.Associations,
            Planner = planner(),
        });

        var entitled = engine.WithEntitlements();
        Tutorial.Sql(Statement);

        Console.WriteLine("    The clause, in the middle of the orders declaration:");
        Console.WriteLine();
        Console.WriteLine("        .Related(supplier).Through(orderDetails).Through(products)");
        Console.WriteLine();
        Console.WriteLine("    The two sources are why the path needs one more thing. A declared step");
        Console.WriteLine("    resolves through a foreign key, and a foreign key names a table of its");
        Console.WriteLine("    own schema — so `order_details.product_id` in DuckDB could not reach");
        Console.WriteLine("    `products` in SQLite. An association says across two sources what a");
        Console.WriteLine("    foreign key says within one:");
        Console.WriteLine();
        Console.WriteLine("        orderDetails.Column(\"product_id\").References(products.Column(\"product_id\"))");

        var representative = perspectives.SupplierRepresentative("A");
        var buyer = Perspectives.Principal(
            "buyer:3", Grant.ForTenancy(perspectives.Customer, 3, perspectives.Buyer));

        Tutorial.Step("supplier A's representative asks for orders");
        var asSupplier = await entitled.PrepareAsync(Statement, entitlements.Bind(representative));
        Tutorial.Plan("plan", asSupplier.Plan);
        Console.WriteLine();
        Chapter10WhosAsking.Report(asSupplier);
        Console.WriteLine();
        Console.WriteLine("what each source was asked to run:");
        Tutorial.RemoteQueries(asSupplier.Plan);
        Console.WriteLine();
        await Chapter10WhosAsking.PrintAsync(engine, asSupplier);

        Console.WriteLine();
        Console.WriteLine("    `freight` is the merchant's cost of shipping and a supplier does not");
        Console.WriteLine("    read it; `customer_id` may be tested for equality without being read,");
        Console.WriteLine("    which is a different thing from being masked — a supplier chasing a");
        Console.WriteLine("    dispute can confirm the account they were already given, and learn");
        Console.WriteLine("    nothing by guessing.");

        Tutorial.Step("and the customer, who reaches the same table the other way");
        var asBuyer = await entitled.PrepareAsync(Statement, entitlements.Bind(buyer));
        Tutorial.Plan("plan", asBuyer.Plan);
        Console.WriteLine();
        Console.WriteLine("what each source was asked to run:");
        Tutorial.RemoteQueries(asBuyer.Plan);
        Console.WriteLine();
        await Chapter10WhosAsking.PrintAsync(engine, asBuyer);

        Console.WriteLine();
        Console.WriteLine("    One table, two shapes of reach. The customer's is a column on the row");
        Console.WriteLine("    and folds to a literal the database can be asked. The supplier's is an");
        Console.WriteLine("    existence question over the lines, and it stays a join.");
    }
}

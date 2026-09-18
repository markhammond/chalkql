using Chalk.Client;
using Chalk.Entitlements;
using Chalk.Entitlements.Tenancy;

namespace Chalk.Sample.Tutorial;

/// <summary>
/// Chapter 11 — through the parent. <c>order_details</c> carries no customer and no supplier:
/// nothing on the row says who may read it. Its order does, and its product does. <c>Through</c>
/// declares those two relationships once and the planner compiles each into a join against the
/// parent's own entitled scan.
/// </summary>
public static class Chapter11ThroughTheParent
{
    private const string Statement = """
        SELECT d.order_id, d.product_id, d.quantity, o.order_date
        FROM order_details d
        JOIN orders o ON o.order_id = d.order_id
        ORDER BY d.order_id, d.product_id
        """;

    public static async Task RunAsync(Func<IQueryPlanner> planner)
    {
        Tutorial.Chapter(
            11,
            "Through the parent",
            "Look at the columns of order_details: an order, a product, a quantity, a price and a\n"
            + "discount. Not a tenancy column among them. Which principal may read a line is decided\n"
            + "entirely by the order it is on and the product it names — one declaration each, and\n"
            + "the same statement returns different rows to different callers.");

        var shape = Marketplace.Poco();
        var catalog = Chapter10WhosAsking.Catalog(shape);
        var perspectives = Perspectives.Declare(catalog, Marketplace.Placement.Memory);
        var entitlements = perspectives.Compile(catalog);

        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "tutorial-11",
            Sources = [Marketplace.Poco(entitlements)],
            Planner = planner(),
        });

        var entitled = engine.WithEntitlements();
        Tutorial.Sql(Statement);

        Console.WriteLine("    The whole of the declaration is two lines:");
        Console.WriteLine();
        Console.WriteLine("        orderDetails.Tenancy(t => t");
        Console.WriteLine("            .Inherited(customer).Through(orders)");
        Console.WriteLine("            .Inherited(supplier).Through(products));");

        var buyer = Perspectives.Principal(
            "buyer:3", Grant.ForTenancy(perspectives.Customer, 3, perspectives.Buyer));
        var representative = perspectives.SupplierRepresentative("A");
        var finance = perspectives.FinanceDesk();

        Tutorial.Step("the customer's hop: a line is visible when its order is");
        var asBuyer = await entitled.PrepareAsync(Statement, entitlements.Bind(buyer));
        Tutorial.Plan("plan", asBuyer.Plan);
        Console.WriteLine();
        Chapter10WhosAsking.Report(asBuyer);
        Console.WriteLine();
        await Chapter10WhosAsking.PrintAsync(engine, asBuyer);

        Console.WriteLine();
        Console.WriteLine("    Read the leaf on order_details: it has no filter of its own, because it");
        Console.WriteLine("    has nothing to filter on. What restricts it is the join above it, to a");
        Console.WriteLine("    scan of orders carrying `customer_id = 3` — the order's policy, reached");
        Console.WriteLine("    through a relationship the statement never wrote.");

        Tutorial.Step("the supplier's hop: the same line, visible because of its product");
        var asSupplier = await entitled.PrepareAsync(Statement, entitlements.Bind(representative));
        Tutorial.Plan("plan", asSupplier.Plan);
        Console.WriteLine();
        Chapter10WhosAsking.Report(asSupplier);
        Console.WriteLine();
        await Chapter10WhosAsking.PrintAsync(engine, asSupplier);

        Console.WriteLine();
        Console.WriteLine("    The same two lines of policy, the other hop — and note that the");
        Console.WriteLine("    statement never mentions `products`. The policy brought it: `products`");
        Console.WriteLine("    holds its supplier directly, that is the foot of the path, and the");
        Console.WriteLine("    line reaches it through `product_id`, so the scan the policy added");
        Console.WriteLine("    carries `supplier_id = ''A''` and the join above it does the rest.");

        Tutorial.Step("Finance reaches everywhere — and the joins disappear");
        var asFinance = await entitled.PrepareAsync(Statement, entitlements.Bind(finance));
        Tutorial.Plan("plan", asFinance.Plan);
        Console.WriteLine();
        Chapter10WhosAsking.Report(asFinance);
        Console.WriteLine();
        await Chapter10WhosAsking.PrintAsync(engine, asFinance);

        Console.WriteLine();
        Console.WriteLine("    For Finance each parent's predicate folds to TRUE, the foreign keys to");
        Console.WriteLine("    orders and to products are declared and neither key is NULL — so every");
        Console.WriteLine("    line has a visible parent and the policy's joins are left out");
        Console.WriteLine("    altogether. Count the reads: the entitled plans above have the");
        Console.WriteLine("    statement's own and the ones the policy added; this one has the");
        Console.WriteLine("    statement's own. The report says ALL rows for every table, which is");
        Console.WriteLine("    what the elision was read off.");
    }
}

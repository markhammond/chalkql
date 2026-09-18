using Chalk.Catalog;
using Chalk.Client;
using Chalk.Entitlements;
using Chalk.Entitlements.Tenancy;

namespace Chalk.Sample.Tutorial;

/// <summary>
/// Chapter 10 — who's asking. One statement, several principals, several answers: the policy is a
/// property of the table and the caller, and the query does not mention it.
/// </summary>
public static class Chapter10WhosAsking
{
    private const string Lines = """
        SELECT order_id, product_id, quantity, unit_price
        FROM order_details
        ORDER BY order_id, product_id
        """;

    private const string Discounts = """
        SELECT product_id, AVG(discount) AS average_discount, COUNT(*) AS lines
        FROM order_details
        GROUP BY product_id
        ORDER BY product_id
        """;

    private const string Orders = """
        SELECT order_id, customer_id, employee_id, region_id, order_date
        FROM orders
        ORDER BY order_id
        """;

    public static async Task RunAsync(Func<IQueryPlanner> planner)
    {
        Tutorial.Chapter(
            10,
            "Who's asking",
            "A line of an order belongs to two tenancies at once: the customer who bought it, and\n"
            + "the supplier whose product it is. They are two perspectives over one row, and the\n"
            + "policy says what each may read of it. The statements below say nothing about either.");

        var shape = Marketplace.Poco();
        var catalog = Catalog(shape);
        var perspectives = Perspectives.Declare(catalog, Marketplace.Placement.Memory);
        var entitlements = perspectives.Compile(catalog);

        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "tutorial-10",
            Sources = [Marketplace.Poco(entitlements)],
            Planner = planner(),
        });

        // The decorator is the whole of the call site: `engine` knows nothing about policies, and
        // `engine.WithEntitlements()` is what attaches one and hands the report back.
        var entitled = engine.WithEntitlements();

        var buyer = Perspectives.Principal(
            "buyer:3", Grant.ForTenancy(perspectives.Customer, 3, perspectives.Buyer));
        var representative = perspectives.SupplierRepresentative("A");

        // ---------------------------------------------------------------- the customer

        Tutorial.Sql(Lines);

        Tutorial.Step("Harbour Partners' own buyer — their lines, every column of them");
        var asBuyer = await entitled.PrepareAsync(Lines, entitlements.Bind(buyer));
        Tutorial.Plan("plan", asBuyer.Plan);
        Console.WriteLine();
        Report(asBuyer);
        Console.WriteLine();
        await PrintAsync(engine, asBuyer);

        // ---------------------------------------------------------------- the supplier

        Tutorial.Step("Supplier A's representative — the same statement, the other perspective");
        var asSupplier = await entitled.PrepareAsync(Lines, entitlements.Bind(representative));
        Tutorial.Plan("plan", asSupplier.Plan);
        Console.WriteLine();
        Report(asSupplier);
        Console.WriteLine();
        await PrintAsync(engine, asSupplier);

        Console.WriteLine();
        Console.WriteLine("    Different rows and a different column. The customer's leaf reaches its");
        Console.WriteLine("    tenancy through the order; the supplier's reaches its own through the");
        Console.WriteLine("    product. And `unit_price` on a line is the price this customer");
        Console.WriteLine("    negotiated, which is not the supplier's to read — so it is withheld");
        Console.WriteLine("    outright rather than masked, and the report says REDACTED.");

        // ---------------------------------------------------------------- aggregate only

        Tutorial.Step("The discount a supplier may ask about, and the one it may not");
        Tutorial.Sql(Discounts);
        var averages = await entitled.PrepareAsync(Discounts, entitlements.Bind(representative));
        Report(averages);
        Console.WriteLine();
        await PrintAsync(engine, averages);

        Console.WriteLine();
        Console.WriteLine("    `discount` is AggregateOnly for a supplier under a group-size floor of");
        Console.WriteLine("    three: the average over a large enough group is a fact about the market,");
        Console.WriteLine("    and one row's discount is a fact about one customer. Asking for the");
        Console.WriteLine("    column itself is the second thing:");

        try
        {
            await entitled.PrepareAsync(
                "SELECT order_id, discount FROM order_details", entitlements.Bind(representative));
            Console.WriteLine("    (no refusal, which would be a bug)");
        }
        catch (EntitlementException refused)
        {
            Console.WriteLine("    " + FirstSentence(refused.Message));
        }

        // ---------------------------------------------------------------- the subject

        Tutorial.Step("The merchant's own staff: a subject rather than a tenancy");
        Tutorial.Sql(Orders);
        Console.WriteLine("    `employee` is declared `within: [customer]`, which names the tenancy a");
        Console.WriteLine("    subject grant may be confined to — never where the employee belongs.");
        Console.WriteLine("    So the same subject reads two ways, and the tutorial shows both.");

        var anywhere = Perspectives.Principal(
            5,
            Grant.ForSubject(perspectives.Employee, 5, perspectives.Self, within: Tenancy.Anywhere));
        var confined = Perspectives.Principal(
            "audit:7",
            Grant.ForSubject(perspectives.Employee, 5, perspectives.Auditor, within: 7));

        Tutorial.Step("employee 5, everywhere: the orders they handled, whoever bought");
        var asSelf = await entitled.PrepareAsync(Orders, entitlements.Bind(anywhere));
        Tutorial.Plan("plan", asSelf.Plan);
        Console.WriteLine();
        await PrintAsync(engine, asSelf);

        Tutorial.Step("employee 5, within customer 7: one auditor, one account");
        var asAuditor = await entitled.PrepareAsync(Orders, entitlements.Bind(confined));
        Tutorial.Plan("plan", asAuditor.Plan);
        Console.WriteLine();
        await PrintAsync(engine, asAuditor);

        Console.WriteLine();
        Console.WriteLine("    Read the two leaves. `employee_id = 5` alone, and `employee_id = 5 AND");
        Console.WriteLine("    customer_id = 7`: a grant written in terms of a subject, a role and a");
        Console.WriteLine("    customer, compiled into an ordinary relational predicate that a source");
        Console.WriteLine("    can push and an index can answer. A confined subject grant can only");
        Console.WriteLine("    apply where both can be resolved, and `orders` resolves both.");

        // ---------------------------------------------------------------- the third axis

        Tutorial.Step("And the third axis: a region, on orders and on nothing else");
        var sales = Perspectives.Principal(
            "sales:7", Grant.ForTenancy(perspectives.Region, 7, perspectives.Sales));
        var asSales = await entitled.PrepareAsync(Orders, entitlements.Bind(sales));
        Tutorial.Plan("plan", asSales.Plan);
        Console.WriteLine();
        await PrintAsync(engine, asSales);

        Console.WriteLine();
        Console.WriteLine("    Three dimensions on one table, OR-ed: a row is visible when the caller");
        Console.WriteLine("    holds a role in its customer, or a grant for the employee who handled");
        Console.WriteLine("    it, or a role in its region. One declaration serves all three.");
    }

    // ---------------------------------------------------------------- the parts

    internal static CatalogContext Catalog(Chalk.Sources.Poco.PocoSource shape) => new()
    {
        ContextId = "tutorial-policy",
        Epoch = 1,
        Schemas = [shape.DescribeSchema()],
    };

    /// <summary>What the policy did, per output column and per table it read.</summary>
    internal static void Report(EntitledQuery prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        Console.WriteLine("report:");
        Console.WriteLine(
            "    columns  "
            + string.Join(", ", prepared.Columns.Select(c => $"{c.Name}:{c.Disclosure}")));
        Console.WriteLine(
            "    tables   "
            + string.Join(
                ", ",
                prepared.Entitlements.Tables.Select(t => $"{t.Table}: {t.Visibility} rows")));
    }

    internal static async Task PrintAsync(ChalkEngine engine, EntitledQuery prepared, int maxRows = 8)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(prepared);
        await using var execution = await engine.ExecuteAsync(prepared.Query);
        await Tutorial.PrintAsync(execution, maxRows);
    }

    internal static string FirstSentence(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var stop = message.IndexOf(". ", StringComparison.Ordinal);
        return stop < 0 ? message : message[..(stop + 1)];
    }
}

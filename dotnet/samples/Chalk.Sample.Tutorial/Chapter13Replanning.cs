using Chalk.Catalog;
using Chalk.Client;
using Chalk.Entitlements;
using Chalk.Entitlements.Tenancy;

namespace Chalk.Sample.Tutorial;

/// <summary>
/// Chapter 13 — replanning. One statement, three plans: what the host binds when decides how much
/// of the policy is in the plan, and a plan can be told more later without being written again.
/// </summary>
/// <remarks>
/// The tables are in DuckDB rather than in memory, because the point of folding a tenant's grants is
/// what the fold does on the other side of a boundary: the tenant plan's <c>customer_id = 3</c> is
/// in the SQL the source is asked to run, and the base plan's marker joins are not.
/// </remarks>
public static class Chapter13Replanning
{
    private const string Statement = """
        SELECT order_id, product_id, quantity
        FROM facts.order_details
        ORDER BY order_id, product_id
        """;

    /// <summary>
    /// The names that are the <em>person's</em> rather than the customer's. Everything else — the
    /// grants of the customer dimension, and whether this deployment permits a grant that reaches
    /// everywhere — every member of a customer's staff binds identically, and is therefore what a
    /// plan may fold and still be shared by all of them.
    /// </summary>
    private static readonly string[] PersonNames = ["user", "mask_key"];

    /// <summary>
    /// What this chapter leaves open in the base plan: the customer's grants, the employee subject's
    /// and who is asking. Everything else — the supplier, region and warehouse axes, and whether
    /// this deployment permits a grant that reaches everywhere — every principal on the customer's
    /// side of the marketplace binds identically, so a plan may fold it and still be everybody's.
    /// </summary>
    private static bool Open(string name) =>
        name.StartsWith("customer", StringComparison.Ordinal)
        || name.StartsWith("employee", StringComparison.Ordinal)
        || PersonNames.Contains(name, StringComparer.Ordinal);

    /// <summary>Those names, for this principal's context.</summary>
    private static IReadOnlyCollection<string> OpenNames(
        TenancyEntitlements entitlements, TenancyPrincipal principal)
    {
        var whole = entitlements.Bind(principal);
        return
        [
            .. whole.Scalars.Keys.Where(Open),
            .. whole.Lists.Keys.Where(Open),
        ];
    }

    public static async Task RunAsync(Func<IQueryPlanner> planner)
    {
        Tutorial.Chapter(
            13,
            "Replanning",
            "One statement, planned three times. Bind everything and the tenancy is a literal in\n"
            + "the leaf and the plan is that principal's alone. Leave the customer and the caller\n"
            + "open and one plan serves everybody, with every membership decided per row. Between\n"
            + "them: fold the customer's grants and leave the person open, and the plan is the\n"
            + "tenant's — shared by all its staff, bound to one of them when it runs. Each is the\n"
            + "one before it, told more — and the tables are in a database, so each tier is also a\n"
            + "different question asked of the source.");

        using var facts = Database.DuckDb("facts");
        var shape = Marketplace.Poco();
        var orders = Data.OrderRows();
        Marketplace.Everything(facts, shape, orders, Data.OrderDetails(orders), entitlements: null);

        var placement = new Marketplace.Placement(Marketplace.Facts, Marketplace.Facts, Marketplace.Facts);
        var catalog = new CatalogContext
        {
            ContextId = "tutorial-13",
            Epoch = 1,
            Schemas = [Marketplace.EverythingBuilder(facts, shape, entitlements: null).Build().DescribeSchema()],
        };

        var perspectives = Perspectives.Declare(catalog, placement);
        var entitlements = perspectives.Compile(catalog);

        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "tutorial-13",
            Sources = [Marketplace.EverythingBuilder(facts, shape, entitlements).Build()],
            Planner = planner(),
        });

        var entitled = engine.WithEntitlements();
        Tutorial.Sql(Statement);

        var harbour = Perspectives.Principal(
            5,
            Grant.ForTenancy(perspectives.Customer, 3, perspectives.Buyer),
            Grant.ForSubject(perspectives.Employee, 5, perspectives.Self, within: Tenancy.Anywhere));
        var solvang = Perspectives.Principal(
            4,
            Grant.ForTenancy(perspectives.Customer, 8, perspectives.Buyer),
            Grant.ForSubject(perspectives.Employee, 4, perspectives.Self, within: Tenancy.Anywhere));

        // ------------------------------------------------------------ the base plan

        Tutorial.Step("the base plan: the customer and the caller left open, one plan for everybody");
        var basePlan = await entitled.PrepareAsync(Statement, entitlements.Bind(harbour).Shape(OpenNames(entitlements, harbour)));
        Tutorial.Plan("plan", basePlan.Plan);
        Console.WriteLine();
        Required(basePlan);
        Console.WriteLine();
        Chapter10WhosAsking.Report(basePlan);
        Console.WriteLine();
        Console.WriteLine("remote queries:");
        Tutorial.RemoteQueries(basePlan.Plan);
        await PrintAsync(engine, basePlan, entitlements.Bind(harbour), "as Harbour Partners' buyer");
        await PrintAsync(engine, basePlan, entitlements.Bind(solvang), "as Solvang Retail's");

        Console.WriteLine();
        Console.WriteLine("    One plan, one digest, two principals. Nothing about either customer");
        Console.WriteLine("    is in it: every membership is a join to a table the executor");
        Console.WriteLine("    materialises from the binding, and no customer is named in the SQL.");
        Console.WriteLine();
        Console.WriteLine("    `Shape(names)` is what chose the line: the supplier, region and");
        Console.WriteLine("    warehouse axes are bound, because every principal on the customer's");
        Console.WriteLine("    side of this marketplace binds them identically, and what is left open");
        Console.WriteLine("    is the customer, the employee and the caller. A host draws that line");
        Console.WriteLine("    where its own principals differ.");

        // ------------------------------------------------------------ the tenant plan

        Tutorial.Step("the tenant plan: the customer's grants folded, the person left open");
        var tenantPlan = await basePlan.NarrowAsync(Customer(entitlements, harbour));
        Tutorial.Plan("plan", tenantPlan.Plan);
        Console.WriteLine();
        Required(tenantPlan);
        Console.WriteLine();
        Chapter10WhosAsking.Report(tenantPlan);
        Console.WriteLine();
        Console.WriteLine("remote queries:");
        Tutorial.RemoteQueries(tenantPlan.Plan);
        Console.WriteLine();
        Stages(tenantPlan);
        await PrintAsync(engine, tenantPlan, entitlements.Bind(harbour), "as Harbour Partners' buyer");

        Console.WriteLine();
        Console.WriteLine("    `customer_id = 3` is in the SQL now, and the marker joins that carried");
        Console.WriteLine("    it are gone: the customer is decided, so the predicate is something the");
        Console.WriteLine("    database can be asked, and the source returns the lines this binding");
        Console.WriteLine("    reaches rather than all of them. That is what a fold is for, reached by");
        Console.WriteLine("    a plan built before the tenant was known. What is still open is who is");
        Console.WriteLine("    asking, which is what lets every one of that customer's staff share");
        Console.WriteLine("    this plan.");

        // ------------------------------------------------------------ the person's plan

        Tutorial.Step("the person's plan: everything folded, nothing left to bind");
        var personPlan = await tenantPlan.NarrowAsync(Person(entitlements, harbour));
        Tutorial.Plan("plan", personPlan.Plan);
        Console.WriteLine();
        Required(personPlan);
        Console.WriteLine();
        Chapter10WhosAsking.Report(personPlan);
        Console.WriteLine();
        Console.WriteLine("remote queries:");
        Tutorial.RemoteQueries(personPlan.Plan);
        Console.WriteLine();
        Stages(personPlan);
        await PrintAsync(engine, personPlan, context: null, "as employee 5 of Harbour Partners");

        Console.WriteLine();
        Console.WriteLine("    Nothing is required at execution and nothing of the context is left in");
        Console.WriteLine("    the plan as a parameter. This is the plan preparing with the whole");
        Console.WriteLine("    binding would have given — digest included — reached by narrowing");
        Console.WriteLine("    rather than by writing the statement out again.");
        Console.WriteLine();
        Console.WriteLine("    Three tiers of specialisation from one statement, chosen by what the");
        Console.WriteLine("    host binds when — and a tenant's plan shared by all its staff.");
    }

    // ---------------------------------------------------------------- the parts

    /// <summary>
    /// The context every customer-side principal shares: the axes this chapter is not about, bound
    /// to their (empty) values, and the customer, the employee and the caller as shapes.
    /// </summary>
    private static RequestContext Everybodys(
        TenancyEntitlements entitlements, TenancyPrincipal principal)
    {
        var whole = entitlements.Bind(principal);
        var shaped = whole.Shape();
        var scalars = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (name, value) in whole.Scalars)
        {
            if (!Open(name))
            {
                scalars[name] = value;
            }
            else if (shaped.Scalars.TryGetValue(name, out var shape))
            {
                scalars[name] = shape;
            }
        }

        var lists = new Dictionary<string, ContextRelation>(StringComparer.Ordinal);
        foreach (var (name, value) in whole.Lists)
        {
            if (!Open(name))
            {
                lists[name] = value;
            }
            else if (shaped.Lists.TryGetValue(name, out var shape))
            {
                lists[name] = shape;
            }
        }

        return new RequestContext { Scalars = scalars, Lists = lists };
    }

    /// <summary>
    /// Exactly what this plan says it still needs, bound from this principal. A plan that folded
    /// half its context is executed with the other half, and the plan is the thing that knows which
    /// half that is.
    /// </summary>
    private static RequestContext Required(
        TenancyEntitlements entitlements, TenancyPrincipal principal, EntitledQuery prepared) =>
        Only(
            entitlements.Bind(principal),
            name => prepared.Query.RequiredContext.Contains(name, StringComparer.Ordinal));

    /// <summary>This principal's customer half.</summary>
    private static RequestContext Customer(
        TenancyEntitlements entitlements, TenancyPrincipal principal) =>
        Only(
            entitlements.Bind(principal),
            name => Open(name) && !PersonNames.Contains(name, StringComparer.Ordinal)
                && !name.StartsWith("employee", StringComparison.Ordinal));

    /// <summary>And the rest of it: who is asking.</summary>
    private static RequestContext Person(
        TenancyEntitlements entitlements, TenancyPrincipal principal) =>
        Only(
            entitlements.Bind(principal),
            name => PersonNames.Contains(name, StringComparer.Ordinal)
                || name.StartsWith("employee", StringComparison.Ordinal));

    private static RequestContext Only(RequestContext whole, Func<string, bool> take) => new()
    {
        Scalars = whole.Scalars.Where(s => take(s.Key))
            .ToDictionary(s => s.Key, s => s.Value, StringComparer.Ordinal),
        Lists = whole.Lists.Where(l => take(l.Key))
            .ToDictionary(l => l.Key, l => l.Value, StringComparer.Ordinal),
    };

    /// <summary>What this plan still needs bound when it runs, and what it already folded.</summary>
    private static void Required(EntitledQuery prepared)
    {
        Console.WriteLine("context:");
        Console.WriteLine("    folded    " + Names(prepared.Query.FoldedContext));
        Console.WriteLine("    required  " + Names(prepared.Query.RequiredContext));
    }

    /// <summary>
    /// How many names, and which kinds they are about. The policy has four tenancy kinds, a subject
    /// and eight roles, so the context has sixty-odd names in it and printing them all would say
    /// less than counting them does.
    /// </summary>
    private static string Names(IReadOnlyList<string> names)
    {
        if (names.Count == 0)
        {
            return "(none)";
        }

        var kinds = names
            .Select(n => n.Split('_')[0])
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal);
        return $"{names.Count} names, over {string.Join(", ", kinds)}";
    }

    /// <summary>Which plan this one started from, when the sidecar still held that one's tree.</summary>
    private static void Stages(EntitledQuery prepared)
    {
        Console.WriteLine("stages:");
        Console.WriteLine("    " + string.Join(" -> ", prepared.Query.PlanningStats.Stages));
    }

    private static async Task PrintAsync(
        ChalkEngine engine, EntitledQuery prepared, RequestContext? context, string who)
    {
        Console.WriteLine();
        Console.WriteLine("    " + who + ":");
        await using var execution = context is null
            ? await engine.ExecuteAsync(prepared.Query)
            : await engine.ExecuteAsync(prepared.Query, context);
        await Tutorial.PrintAsync(execution, maxRows: 8);
    }
}

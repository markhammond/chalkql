using Chalk.Client;

namespace Chalk.Sample.Tutorial;

/// <summary>
/// Chapter 8 — parameters and prepared statements. One statement planned once and run three times,
/// then the two other parameter styles: named, and a list expanded into an <c>IN</c> clause.
/// </summary>
public static class Chapter08Parameters
{
    private const string Positional = """
        SELECT c.company_name AS customer, COUNT(*) AS orders, SUM(o.freight) AS freight
        FROM customers c JOIN orders o ON o.customer_id = c.customer_id
        WHERE c.country = ? AND o.region_id = ?
        GROUP BY c.company_name
        ORDER BY c.company_name
        """;

    private const string Named = """
        SELECT s.supplier_id, COUNT(*) AS products
        FROM products p JOIN suppliers s ON s.supplier_id = p.supplier_id
        WHERE s.country IN @countries AND p.unit_price > @floor
        GROUP BY s.supplier_id
        ORDER BY s.supplier_id
        """;

    public static async Task RunAsync(Func<IQueryPlanner> planner)
    {
        Tutorial.Chapter(
            8,
            "Parameters and prepared statements",
            "PrepareAsync plans and compiles; ExecuteAsync only binds. A PreparedQuery is\n"
            + "thread-safe and reusable, so the planning round trip happens once however many\n"
            + "times the statement runs.");

        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "tutorial-08",
            Sources = [Marketplace.Poco()],
            Planner = planner(),
        });

        Tutorial.Step("prepared once");
        Tutorial.Sql(Positional);

        var query = await engine.PrepareAsync(Positional);
        Console.WriteLine($"    style {query.ParameterStyle}, {query.Parameters.Count} parameters "
            + $"({string.Join(", ", query.ParameterTypes.Select(t => t.Kind))}), plan {query.PlanDigest:x16}");

        object?[][] bindings = [["GB", 3], ["SE", 1], ["JP", 8]];
        foreach (var values in bindings)
        {
            Console.WriteLine();
            Console.WriteLine($"    country = '{values[0]}', region_id = {values[1]}");
            await using var execution = await engine.ExecuteAsync(query, values);
            await Tutorial.PrintAsync(execution);
            Console.WriteLine($"    plan {execution.Plan.PlanDigest:x16} — the same plan every time");
        }

        Tutorial.Step("named parameters, and a list expanded into the IN clause");
        Tutorial.Sql(Named);

        var byName = await engine.PrepareAsync(Named);
        foreach (var countries in new[] { new[] { "SE", "FR" }, ["DE", "TW", "IT"] })
        {
            await using var execution = await engine.ExecuteAsync(byName, new { countries, floor = 10m });
            Console.WriteLine();
            Console.WriteLine($"    countries = [{string.Join(", ", countries)}], plan {execution.Plan.PlanDigest:x16}");
            await Tutorial.PrintAsync(execution);
        }

        Console.WriteLine();
        Console.WriteLine("    A list parameter is planned per list length: n literals make an");
        Console.WriteLine("    n-dependent plan, so the two runs above have different digests and");
        Console.WriteLine("    each length is compiled once and cached on the PreparedQuery.");
    }
}

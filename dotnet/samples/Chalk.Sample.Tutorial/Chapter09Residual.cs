using Chalk.Client;

namespace Chalk.Sample.Tutorial;

/// <summary>
/// Chapter 9 — when the source cannot. A <c>WHERE</c> with two conjuncts: one DuckDB can evaluate,
/// one it has never heard of. The plan is split at the boundary, and the counters show exactly what
/// that cost.
/// </summary>
public static class Chapter09Residual
{
    private const string Sql = """
        SELECT d.order_id, d.product_id, d.quantity, d.unit_price
        FROM facts.order_details d
        WHERE d.order_id <= 40 AND size_band(d.quantity) = 'large'
        ORDER BY d.order_id, d.product_id
        """;

    public static async Task RunAsync(Func<IQueryPlanner> planner)
    {
        Tutorial.Chapter(
            9,
            "When the source cannot",
            "size_band is a C# delegate in this process. DuckDB cannot run it and Chalk does not\n"
            + "pretend otherwise: the conjunct it can run goes into the generated query, the one it\n"
            + "cannot stays above the boundary as a residual Filter, and the answer is the same.");

        using var facts = Database.DuckDb("facts");
        var shape = Marketplace.Poco();
        var orders = Data.OrderRows(Data.WarehouseRows);
        Marketplace.Transactional(facts, shape, orders, Data.OrderDetails(orders), entitlements: null);

        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "tutorial-09",
            Sources =
            [
                Chapter06Functions.Declare(
                    Marketplace.TransactionalBuilder(facts, shape, entitlements: null)).Build(),
            ],
            Planner = planner(),
            Functions = Chapter06Functions.Register,
        });

        Tutorial.Sql(Sql);

        var query = await engine.PrepareAsync(Sql);
        Tutorial.Plan("plan", query.Plan);
        Console.WriteLine();
        Console.WriteLine("what the database was asked to run:");
        Tutorial.RemoteQueries(query.Plan);
        Console.WriteLine();
        Console.WriteLine($"plan shape: {Tutorial.Kinds(query.Plan)}");
        Console.WriteLine();

        await using var execution = await engine.ExecuteAsync(query);
        await Tutorial.PrintAsync(execution, maxRows: 6);
        Console.WriteLine();
        Tutorial.Counters(execution);
        Console.WriteLine();
        Console.WriteLine("    Rows fetched is what the pushed conjunct left; rows produced is what");
        Console.WriteLine("    survived the residual. The difference is the price of a predicate the");
        Console.WriteLine("    source could not evaluate — visible, rather than guessed at.");
    }
}

using Chalk.Client;

namespace Chalk.Sample.Tutorial;

/// <summary>
/// Chapter 3 — two sources, one query. The reference tables in SQLite and the facts in DuckDB, in
/// one catalog, joined by a statement that says nothing about where either side lives.
/// </summary>
public static class Chapter03TwoSources
{
    private const string Sql = """
        SELECT s.company_name AS supplier, p.product_name, d.order_id, d.quantity
        FROM oltp.products p
        JOIN oltp.suppliers s ON s.supplier_id = p.supplier_id
        JOIN facts.order_details d ON d.product_id = p.product_id
        WHERE p.supplier_id = 'A' AND d.quantity > 22
        ORDER BY d.order_id, p.product_id
        """;

    /// <summary>The same question with the customer side narrowed, which is what a tenant's query looks like.</summary>
    private const string Narrowed = """
        SELECT c.company_name AS customer, o.order_id, o.order_date, o.freight
        FROM oltp.customers c
        JOIN facts.orders o ON o.customer_id = c.customer_id
        WHERE c.country IN ('GB', 'JP') AND o.freight > 60
        ORDER BY o.order_id
        """;

    public static async Task RunAsync(Func<IQueryPlanner> planner)
    {
        Tutorial.Chapter(
            3,
            "Two sources, one query",
            "Six suppliers and twenty products in SQLite; twenty thousand orders and fifty thousand\n"
            + "lines in DuckDB. One statement names both. What travels to each database is SQL Chalk\n"
            + "generated in that database's own dialect, carrying every predicate the source declared\n"
            + "it can evaluate.");

        using var oltp = Database.Sqlite("oltp");
        using var facts = Database.DuckDb("facts");
        var shape = Marketplace.Poco();
        var orders = Data.OrderRows(Data.WarehouseRows);

        Tutorial.Step("what each side actually holds");
        Console.WriteLine("    oltp (sqlite):");
        foreach (var line in oltp.Ddl(Marketplace.Table(shape.DescribeSchema(), "products")).Split('\n'))
        {
            Console.WriteLine("      " + line);
        }

        Console.WriteLine("    facts (duckdb):");
        foreach (var line in facts.Ddl(Marketplace.Table(shape.DescribeSchema(), "order_details")).Split('\n'))
        {
            Console.WriteLine("      " + line);
        }

        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "tutorial-03",
            Sources =
            [
                Marketplace.Reference(oltp, shape, entitlements: null),
                Marketplace.Transactional(facts, shape, orders, Data.OrderDetails(orders), entitlements: null),
            ],
            Planner = planner(),
        });

        await RunAtAsync(engine, "the keys travel, the rows do not", Sql, PushdownLevel.Full);
        await RunAtAsync(engine, "a customer-side predicate, in the other dialect", Narrowed, PushdownLevel.Full);
        await RunAtAsync(
            engine, "PushdownLevel.None — bare scans, which is the I4 oracle's view", Sql, PushdownLevel.None);
    }

    private static async Task RunAtAsync(
        ChalkEngine engine, string label, string sql, PushdownLevel level)
    {
        Tutorial.Step(label);
        Tutorial.Sql(sql);

        var query = await engine.PrepareAsync(sql, new PrepareOptions { Pushdown = level });
        Tutorial.Plan("plan", query.Plan);
        Console.WriteLine();
        Console.WriteLine("what each source was asked to run:");
        Tutorial.RemoteQueries(query.Plan);
        Console.WriteLine();

        await using var execution = await engine.ExecuteAsync(query);
        await Tutorial.PrintAsync(execution, maxRows: 6);
        Console.WriteLine();
        Tutorial.Counters(execution);
    }
}

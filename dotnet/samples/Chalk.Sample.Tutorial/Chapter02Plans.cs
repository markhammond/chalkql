using Chalk.Client;
using Chalk.Sources.Poco;

namespace Chalk.Sample.Tutorial;

/// <summary>
/// Chapter 2 — what the planner did. An order joined to its customer under a filter, with the plan
/// text asked for; then the same filter over twenty thousand orders, planned differently because the
/// host declared an index.
/// </summary>
public static class Chapter02Plans
{
    private const string Joined = """
        SELECT c.company_name AS customer, o.order_id, o.order_date, o.freight
        FROM orders o
        JOIN customers c ON c.customer_id = o.customer_id
        WHERE c.country = 'GB'
        ORDER BY o.order_id
        """;

    private const string Filtered =
        "SELECT order_id, customer_id, region_id, freight FROM orders WHERE customer_id = 3 AND region_id = 5";

    public static async Task RunAsync(Func<IQueryPlanner> planner)
    {
        Tutorial.Chapter(
            2,
            "What the planner did",
            "Ask for the plan text and Calcite says what it made of the statement, twice: the\n"
            + "logical algebra, then the physical operators it chose and what it thought they\n"
            + "would cost. Declare an index and the same filter is planned differently.");

        await using (var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "tutorial-02a",
            Sources = [Marketplace.Poco()],
            Planner = planner(),
        }))
        {
            var query = await engine.PrepareAsync(Joined, new PrepareOptions { IncludePlanText = true });
            Tutorial.Step("an order joined to its customer, planned with IncludePlanText");
            Tutorial.Sql(Joined);
            Tutorial.Block("planner", query.PlanText ?? "(none)");
            Tutorial.Plan("the IR Chalk executes", query.Plan);
        }

        // The same twenty thousand orders twice: once as a plain list, once with an index over
        // `customer_id` declared on it. Nothing about the query changes.
        var orders = Data.OrderRows(Data.WarehouseRows);
        await RunFilterAsync(planner, "tutorial-02b", "no index declared", Plain(orders));
        await RunFilterAsync(planner, "tutorial-02c", "an index on (customer_id)", Indexed(orders));
    }

    private static PocoSource Plain(IReadOnlyList<Order> orders) =>
        new PocoSourceBuilder("shop")
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .DefaultDecimalScale(2)
            .AddTable("orders", orders, t => t.OrderedBy(o => o.OrderId).UniqueKey(o => o.OrderId))
            .Build();

    private static PocoSource Indexed(IReadOnlyList<Order> orders) =>
        new PocoSourceBuilder("shop")
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .DefaultDecimalScale(2)
            .AddTable("orders", orders, t => t
                .OrderedBy(o => o.OrderId)
                .UniqueKey(o => o.OrderId)
                .Index(o => o.CustomerId))
            .Build();

    private static async Task RunFilterAsync(
        Func<IQueryPlanner> planner, string contextId, string label, PocoSource source)
    {
        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = contextId,
            Sources = [source],
            Planner = planner(),
        });

        Tutorial.Step(label);
        Tutorial.Sql(Filtered);

        var query = await engine.PrepareAsync(Filtered);
        Tutorial.Plan("plan", query.Plan);

        await using var execution = await engine.ExecuteAsync(query);
        var rows = await Tutorial.DrainAsync(execution);
        Console.WriteLine($"    {rows} rows produced, {execution.Stats.RowsScanned} scanned of "
            + $"{source.DescribeSchema().Tables[0].RowCount}");
    }
}

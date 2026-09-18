using Chalk.Client;

namespace Chalk.Sample.Tutorial;

/// <summary>
/// Chapter 5 — time series. A running revenue per customer over <c>order_date</c>, a moving average
/// over a month of EUR/USD, and a tumbling bucket, on tables whose order the host declared.
/// </summary>
public static class Chapter05TimeSeries
{
    private const string Running = """
        SELECT c.company_name AS customer, o.order_date,
               SUM(d.unit_price * d.quantity) AS order_value,
               SUM(SUM(d.unit_price * d.quantity))
                 OVER (PARTITION BY o.customer_id ORDER BY o.order_date) AS running_revenue
        FROM orders o
        JOIN customers c ON c.customer_id = o.customer_id
        JOIN order_details d ON d.order_id = o.order_id
        WHERE o.customer_id IN (3, 7)
        GROUP BY c.company_name, o.customer_id, o.order_date
        ORDER BY c.company_name, o.order_date
        """;

    private const string Moving = """
        SELECT currency, ts, rate,
               AVG(rate) OVER (PARTITION BY currency ORDER BY ts ROWS 4 PRECEDING) AS moving_5
        FROM usd_rates
        WHERE currency = 'EUR'
        """;

    private const string Tumbling = """
        SELECT window_start, COUNT(*) AS orders, SUM(freight) AS freight
        FROM TABLE(TUMBLE(TABLE orders, DESCRIPTOR(order_date), INTERVAL '6' HOUR))
        GROUP BY window_start
        ORDER BY window_start
        """;

    public static async Task RunAsync(Func<IQueryPlanner> planner)
    {
        Tutorial.Chapter(
            5,
            "Time series",
            "Sixty orders, forty-five minutes apart, and a month of daily rates. A running revenue\n"
            + "per customer, a five-day moving average of the euro rate, and six-hour buckets: three\n"
            + "questions about time, over one marketplace, and none of them a subquery.");

        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "tutorial-05",
            Sources = [Marketplace.Poco()],
            Planner = planner(),
        });

        Tutorial.Step("running revenue per customer");
        Tutorial.Sql(Running);
        var running = await engine.PrepareAsync(Running);
        Tutorial.Plan("plan", running.Plan);
        Console.WriteLine();
        await using (var execution = await engine.ExecuteAsync(running))
        {
            var rows = await Tutorial.PrintAsync(execution, maxRows: 8);
            Console.WriteLine($"    {rows} rows, {execution.Stats.RowsScanned} scanned");
        }

        Console.WriteLine();
        Console.WriteLine("    A window over an aggregate: the inner SUM adds a line up per order,");
        Console.WriteLine("    the outer one runs along that customer's orders in date order. One");
        Console.WriteLine("    statement, two levels, and no subquery.");

        // usd_rates is collated by (currency, ts) and the host said so, which is what the window
        // below reads: one currency, its days in order.
        Tutorial.Step("a five-day moving average of the euro rate");
        Tutorial.Sql(Moving);
        var moving = await engine.PrepareAsync(Moving);
        Tutorial.Plan("plan", moving.Plan);
        Console.WriteLine();
        await using (var execution = await engine.ExecuteAsync(moving))
        {
            await Tutorial.PrintAsync(execution, maxRows: 8);
        }

        Console.WriteLine();
        Console.WriteLine("    `usd_rates` holds USD per one unit of a currency, already the right");
        Console.WriteLine("    way up — a feed a host owns usually is — so a euro amount times this");
        Console.WriteLine("    number is dollars, and chapter 6 is that multiplication in a function.");

        Tutorial.Step("six-hour tumbling buckets");
        Tutorial.Sql(Tumbling);
        await using (var execution = await engine.ExecuteAsync(await engine.PrepareAsync(Tumbling)))
        {
            await Tutorial.PrintAsync(execution, maxRows: 6);
        }
    }
}

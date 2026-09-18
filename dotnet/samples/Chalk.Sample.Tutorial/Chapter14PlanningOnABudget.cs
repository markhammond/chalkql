using Chalk.Client;

namespace Chalk.Sample.Tutorial;

/// <summary>
/// Chapter 14 — planning on a budget. The same statement prepared three ways: run to completion,
/// stopped when the optimiser stopped improving, and stopped because the host asked.
/// </summary>
/// <remarks>
/// Nothing printed here is a duration or a count that could differ between machines. The reason a
/// search ended and the cost ratio it reached are functions of the rule-evaluation sequence, which
/// is the same everywhere; the stopped run prints its reason and nothing else, because when a stop
/// lands is the one thing about it that is not.
/// </remarks>
public static class Chapter14PlanningOnABudget
{
    /// <summary>
    /// Four joins across two sources — the most Volcano enumerates before the heuristic pass takes
    /// over, with a local and a remote alternative for every side. It is the longest search in this
    /// tutorial, which is what makes there be anything to watch.
    /// </summary>
    private const string Sql = """
        SELECT c.company_name AS customer, s.company_name AS supplier, COUNT(*) AS lines
        FROM customers c
        JOIN facts.orders o ON o.customer_id = c.customer_id
        JOIN facts.order_details d ON d.order_id = o.order_id
        JOIN facts.products p ON p.product_id = d.product_id
        JOIN facts.suppliers s ON s.supplier_id = p.supplier_id
        GROUP BY c.company_name, s.company_name
        ORDER BY lines DESC, customer, supplier
        """;

    public static async Task RunAsync(Func<IQueryPlanner> planner)
    {
        Tutorial.Chapter(
            14,
            "Planning on a budget",
            "By default the optimiser searches until it has nothing left to try. A host that would\n"
            + "rather have a good plan now than the best plan later says so, and the prepared query\n"
            + "reports how planning ended: converged, budget spent, or stopped because you asked.\n"
            + "Convergence looks by default on a wall-clock cadence (20 ms) rather than a count of rule\n"
            + "evaluations, so it costs nothing to leave on and needs nothing measured up front; a host\n"
            + "that needs the same plan on every machine asks for a count explicitly, which is what the\n"
            + "example below does, so its numbers below are the same wherever this runs.");

        using var facts = Database.DuckDb("facts");
        var shape = Marketplace.Poco();
        var orders = Data.OrderRows(Data.WarehouseRows);
        var everything = Marketplace.Everything(facts, shape, orders, Data.OrderDetails(orders), entitlements: null);

        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "tutorial-14",
            Sources = [Marketplace.Poco(), everything],
            Planner = planner(),
        });

        Tutorial.Sql(Sql);

        // 1. The default: no options at all.
        Tutorial.Step("prepared with no options — the search runs to completion");
        var full = await engine.PrepareAsync(Sql);
        Console.WriteLine($"    reason:      {full.PlanningState.TerminationReason}");
        Console.WriteLine(
            "    sampled:     nothing — with no option able to end the search early, no listener is");
        Console.WriteLine("                 installed and no cost is read");

        // 2. Convergence: stop when the best plan has stopped improving. The interval is a count of
        //    rule evaluations and never a duration, so this is the same everywhere. Twice, because
        //    the two outcomes are exactly what a host is choosing between.
        Tutorial.Step("prepared with a convergence test — stop when the best plan stops improving");
        Console.WriteLine();
        Console.WriteLine("    Sample the root's best cost every N rule evaluations, and stop when");
        Console.WriteLine("    three samples in a row show no improvement:");
        await ConvergedAsync(engine, full, patience: 3, interval: 5);
        await ConvergedAsync(engine, full, patience: 3, interval: 20);
        Console.WriteLine();
        Console.WriteLine("    Sampling sooner stops sooner and settles for the plan it had. The count");
        Console.WriteLine("    is where an interval comes from: measure the statement once, then pick");
        Console.WriteLine("    an interval that samples the part of the search worth watching.");

        // 3. A stop. The token is the host's "that will do"; the prepare still returns a plan.
        Tutorial.Step("prepared with a stop token the host had already cancelled");
        using var stop = new CancellationTokenSource();
        await stop.CancelAsync();
        var stopped = await engine.PrepareAsync(
            Sql,
            new PrepareOptions { Planning = new PlanningOptions { StopToken = stop.Token } });
        Console.WriteLine($"    reason:      {stopped.PlanningState.TerminationReason}");
        Console.WriteLine("    plan:        yes — a stop returns the best complete plan there is, and");
        Console.WriteLine("                 waits for the first one when there is none yet");

        // The stopped plan is a plan like any other: every physical check ran on it, and it executes.
        Tutorial.Step("the stopped plan runs");
        await using var execution = await engine.ExecuteAsync(stopped);
        await Tutorial.PrintAsync(execution, maxRows: 5);
    }

    /// <summary>One convergence setting, and what it reached.</summary>
    private static async Task ConvergedAsync(
        ChalkEngine engine, PreparedQuery full, int patience, int interval)
    {
        var converged = await engine.PrepareAsync(
            Sql,
            new PrepareOptions
            {
                Planning = new PlanningOptions
                {
                    ConvergencePatience = patience,
                    ConvergenceEvaluationInterval = interval,
                },
            });

        var state = converged.PlanningState;
        Console.WriteLine();
        Console.WriteLine(
            $"    patience {patience}, interval {interval}: {state.TerminationReason} after "
            + $"{state.EvaluationCount} rule matches, cost ratio {state.CostRatio:0.0000}, "
            + $"{(converged.PlanDigest == full.PlanDigest ? "the plan the full search found" : "a plan the full search improved on")}");
    }
}

using Chalk.Client;
using Chalk.Entitlements;

namespace Chalk.Sample.Tutorial;

/// <summary>
/// Chapter 17 — temporal streaming live pivot. A second stream, on a cadence of its own, and no
/// common timestamp between the two. The chapter-16 ledger is what gets valued: each movement at the
/// price that was in force when it happened, summed and pivoted by supplier and warehouse.
/// </summary>
public static class Chapter17Temporal
{
    private const string Prices = """
        SELECT product_id, ts, price
        FROM market_prices
        WHERE product_id = 1
        ORDER BY ts
        """;

    public static async Task RunAsync(Func<IQueryPlanner> planner)
    {
        Tutorial.Chapter(
            17,
            "Temporal streaming live pivot",
            "Keep the pivot live when both quantities and prices move independently. Movements land\n"
            + "every ten minutes; prices land every forty. There is no instant at which both are\n"
            + "known, and waiting for one would be waiting forever — so the question changes shape:\n"
            + "not \"the price now\" but \"the price that was in force when this happened\".");

        var shape = Marketplace.Poco();
        var catalog = Live.Catalog("tutorial-17", shape);
        var perspectives = Perspectives.Declare(catalog, Marketplace.Placement.Memory, live: true);
        var entitlements = perspectives.Compile(catalog);

        var source = Marketplace.Poco(out _, out var movements, out var prices, entitlements);
        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "tutorial-17",
            Sources = [source],
            Planner = planner(),
        });

        var entitled = engine.WithEntitlements();
        var finance = perspectives.FinanceDesk();

        Tutorial.Step("the second stream: prices, on nobody's cadence but their own");
        Tutorial.Sql(Prices);
        var quoted = await entitled.PrepareAsync(Prices, entitlements.Bind(finance));
        await Chapter10WhosAsking.PrintAsync(engine, quoted, maxRows: 8);

        Console.WriteLine();
        Console.WriteLine("    `market_prices` is declared Unrestricted: a price is public, and there");
        Console.WriteLine("    is nothing on the row to restrict. What is confidential is how much of");
        Console.WriteLine("    a thing somebody holds — so a valuation is restricted exactly as the");
        Console.WriteLine("    movements are, through the join, and not at all by the price.");

        Tutorial.Step("the join a question like this needs");
        Tutorial.Sql(Live.Valuation);
        Console.WriteLine("    The pivot clause is chapter 15's, character for character:");
        Console.WriteLine();
        Console.WriteLine("        " + Live.Pivot);
        Console.WriteLine();
        Console.WriteLine("    What changed is underneath it. An ASOF join takes, for each movement,");
        Console.WriteLine("    the last price at or before the movement's own timestamp — one pass");
        Console.WriteLine("    down two ordered streams rather than a range join and a window over");
        Console.WriteLine("    its output.");

        var valuation = await entitled.PrepareAsync(Live.Valuation, entitlements.Bind(finance));
        Tutorial.Plan("plan as Finance", valuation.Plan);

        Tutorial.Step("the valuation, four principals");
        await Live.EachPrincipalAsync(engine, entitled, entitlements, perspectives, Live.Valuation);

        Console.WriteLine();
        Console.WriteLine("    The reviewer's slice is empty, and that is the intersection being");
        Console.WriteLine("    honest: supplier A has moved nothing through Singapore yet. Watch it");
        Console.WriteLine("    when the next batch lands.");

        // ------------------------------------------------------------ both streams advance

        Tutorial.Step("both streams advance, independently, under one epoch each");
        var batch = Data.MovementBatch();
        var ticks = NextPrices();

        var first = await engine.RefreshAsync(refresh => refresh.Append(movements, batch));
        Console.WriteLine($"    {batch.Count} movements appended, catalog epoch {first}");
        var second = await engine.RefreshAsync(refresh => refresh.Append(prices, ticks));
        Console.WriteLine($"    {ticks.Count} prices appended, catalog epoch {second}");
        Console.WriteLine();
        Console.WriteLine("    Two refreshes, not one: the two feeds are independent and neither waits");
        Console.WriteLine("    for the other. That is exactly the situation an ASOF join is for — each");
        Console.WriteLine("    execution values whatever movements it can see at whatever prices were");
        Console.WriteLine("    in force, and nothing is ever valued at a price from its own future.");

        Tutorial.Step("and the valuation again");
        await Live.EachPrincipalAsync(engine, entitled, entitlements, perspectives, Live.Valuation);

        Console.WriteLine();
        Console.WriteLine("    Three chapters, one pivot clause. In §15 it summed a position the host");
        Console.WriteLine("    replaced; in §16 a position the host derived from a batch it appended;");
        Console.WriteLine("    here a valuation of that same ledger at the prices a second feed");
        Console.WriteLine("    delivered. The four principals never changed, and neither did what");
        Console.WriteLine("    each of them is allowed to see.");
    }

    /// <summary>The prices the second feed delivers: one more tick per product, after the last.</summary>
    private static IReadOnlyList<MarketPrice> NextPrices()
    {
        var current = Data.Prices();
        var lastId = current[^1].PriceId;
        var lastTs = current.Max(p => p.Ts);
        var rows = new List<MarketPrice>();
        foreach (var group in current.GroupBy(p => p.ProductId).OrderBy(g => g.Key))
        {
            var latest = group.OrderBy(p => p.Ts).Last();
            rows.Add(new MarketPrice(
                PriceId: lastId + rows.Count + 1,
                Ts: lastTs.AddMinutes(40),
                ProductId: group.Key,
                Price: decimal.Round(latest.Price * 1.1m, 2)));
        }

        return rows;
    }
}

using Chalk.Catalog;
using Chalk.Client;
using Chalk.Sources.Ado;

namespace Chalk.Sample.Tutorial;

/// <summary>
/// Chapter 6 — your functions. An order is invoiced in the customer's own money, so what a line came
/// to in USD is a question about four columns and a rate. One SQL-bodied function says it once; the
/// planner inlines it before validation, and the whole thing — the function and the join that
/// supplies the rate — travels into DuckDB's own query.
/// </summary>
public static class Chapter06Functions
{
    private const string Converted = """
        SELECT d.order_id, d.product_id, o.currency, d.quantity, r.rate,
               usd_line_total(d.unit_price, d.quantity, d.discount, r.rate) AS usd
        FROM facts.order_details d
        JOIN facts.orders o ON o.order_id = d.order_id
        JOIN facts.usd_rates r
          ON r.currency = o.currency AND r.ts = CAST(o.order_date AS DATE)
        WHERE d.order_id <= 8
        ORDER BY d.order_id, d.product_id
        """;

    private const string Opaque = """
        SELECT d.order_id, d.product_id, d.quantity,
               size_band(d.quantity) AS band
        FROM facts.order_details d
        WHERE d.order_id <= 4
        ORDER BY d.order_id, d.product_id
        """;

    /// <summary>
    /// The declarations. A function is a catalog object like a table: it belongs to a schema,
    /// travels with the catalog and moves the epoch, and a name a built-in already has is refused
    /// at registration rather than quietly shadowed.
    /// </summary>
    public static AdoSourceBuilder Declare(AdoSourceBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder
            // What a line came to, in dollars. A SQL body, so the planner inlines it before
            // validation and the call is gone by the time anything asks who can run it.
            .AddFunction("usd_line_total", f => f
                .Scalar()
                .Parameter("unit_price", ChalkType.Decimal(28, 2))
                .Parameter<int>("quantity")
                .Parameter("discount", ChalkType.Decimal(28, 2))
                .Parameter<double>("rate")
                .Returns(ChalkType.Decimal(18, 2))
                .Strict()
                .Sql("CAST(unit_price * quantity * (1 - discount) * rate AS DECIMAL(18, 2))"))

            // And one with a C# body, which is opaque: it travels by name, is never pushed and
            // never folded, and runs here.
            .AddFunction("size_band", f => f
                .Scalar<int, string>("quantity")
                .Strict()
                .Client());
    }

    /// <summary>The C# side of the client-bodied one. Tier 1: an ordinary delegate.</summary>
    public static void Register(IFunctionRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.AddScalar<int, string>(
            "size_band", static quantity => quantity < 10 ? "small" : quantity < 18 ? "medium" : "large");
    }

    public static async Task RunAsync(Func<IQueryPlanner> planner)
    {
        Tutorial.Chapter(
            6,
            "Your functions",
            "Every order is invoiced in the customer's own money, and `usd_rates` holds USD per one\n"
            + "unit of each currency for every day. What a line came to in dollars is therefore the\n"
            + "same arithmetic every time — which is exactly the thing to write once, declare on the\n"
            + "catalog, and never write again.");

        using var facts = Database.DuckDb("facts");
        var shape = Marketplace.Poco();
        var orders = Data.OrderRows(Data.WarehouseRows);
        Marketplace.Transactional(facts, shape, orders, Data.OrderDetails(orders), entitlements: null);

        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "tutorial-06",
            Sources = [Declare(Marketplace.TransactionalBuilder(facts, shape, entitlements: null)).Build()],
            Planner = planner(),

            // Checked here, not at the first call: a client-bodied function the catalog declares
            // and nobody implemented fails engine creation, naming the function.
            Functions = Register,
        });

        Tutorial.Step("the declaration");
        Console.WriteLine("    .AddFunction(\"usd_line_total\", f => f");
        Console.WriteLine("        .Scalar()");
        Console.WriteLine("        .Parameter(\"unit_price\", ChalkType.Decimal(28, 2))");
        Console.WriteLine("        .Parameter<int>(\"quantity\")");
        Console.WriteLine("        .Parameter(\"discount\", ChalkType.Decimal(28, 2))");
        Console.WriteLine("        .Parameter<double>(\"rate\")");
        Console.WriteLine("        .Returns(ChalkType.Decimal(18, 2))");
        Console.WriteLine("        .Strict()");
        Console.WriteLine("        .Sql(\"CAST(unit_price * quantity * (1 - discount) * rate AS DECIMAL(18, 2))\"))");
        Console.WriteLine();
        Console.WriteLine("    The types are the catalog's, so they are written out rather than");
        Console.WriteLine("    guessed from CLR types that have more than one SQL spelling. `Strict`");
        Console.WriteLine("    says NULL in, NULL out, which is a fact the planner uses.");
        Console.WriteLine();
        Console.WriteLine("    The rate is a double and the money is exact, and this function is");
        Console.WriteLine("    where the two meet: the IR harmonises an arithmetic call's operands by");
        Console.WriteLine("    kind, so the decimal price times the double rate is computed in");
        Console.WriteLine("    double, and the cast brings the product back to money, rounding");
        Console.WriteLine("    half-even to the scale.");

        await RunAtAsync(engine, "what every line came to, in dollars", Converted);

        Console.WriteLine();
        Console.WriteLine("    Read the generated SQL. There is no `usd_line_total` in it: the body");
        Console.WriteLine("    was inlined before validation, so what the planner had was arithmetic");
        Console.WriteLine("    over four columns, and arithmetic is something DuckDB can do. The two");
        Console.WriteLine("    joins went with it — the line to its order for the currency and the");
        Console.WriteLine("    date, the order to the rate in force that day — and the whole");
        Console.WriteLine("    statement is one remote query.");

        await AgreeAsync(engine);

        Tutorial.Step("and one the source cannot run");
        Console.WriteLine("    `size_band` is a C# delegate in this process. Watch what happens to it.");
        await RunAtAsync(engine, "a C# body in the select list", Opaque);

        Console.WriteLine();
        Console.WriteLine("    `size_band` is still there by name in the plan and nowhere in the SQL,");
        Console.WriteLine("    because DuckDB has never heard of it. It travels by name, is never");
        Console.WriteLine("    pushed and never folded, and runs here over columns that arrived —");
        Console.WriteLine("    which is chapter 9's whole subject.");
    }

    /// <summary>
    /// The same statement twice — pushed, and with nothing pushed at all — and the money compared
    /// cent for cent. The rate is a double either way; what the cast fixes is where the rounding
    /// happens, and that it happens the same way on both sides of the boundary.
    /// </summary>
    private static async Task AgreeAsync(ChalkEngine engine)
    {
        Tutorial.Step("the pushed answer and the local one, compared");

        var pushed = await ReadAsync(engine, PushdownLevel.Full);
        var local = await ReadAsync(engine, PushdownLevel.None);

        var same = pushed.Count == local.Count;
        for (var i = 0; same && i < pushed.Count; i++)
        {
            same = pushed[i] == local[i];
        }

        Console.WriteLine($"    {pushed.Count} rows pushed into DuckDB, {local.Count} computed here");
        Console.WriteLine($"    every value equal to the cent: {same}");
        if (!same)
        {
            throw new InvalidOperationException(
                "the pushed and local answers disagree, which is the one thing this chapter claims.");
        }
    }

    /// <summary>The <c>usd</c> column of <see cref="Converted"/>, at one pushdown level.</summary>
    private static async Task<IReadOnlyList<decimal>> ReadAsync(ChalkEngine engine, PushdownLevel level)
    {
        var query = await engine.PrepareAsync(Converted, new PrepareOptions { Pushdown = level });
        await using var execution = await engine.ExecuteAsync(query);
        var values = new List<decimal>();
        await foreach (var batch in execution.Batches)
        {
            var column = (Apache.Arrow.Decimal128Array)batch.Column(5);
            for (var row = 0; row < batch.Length; row++)
            {
                values.Add(column.GetValue(row) ?? 0m);
            }

            batch.Dispose();
        }

        return values;
    }

    private static async Task RunAtAsync(ChalkEngine engine, string label, string sql)
    {
        Tutorial.Step(label);
        Tutorial.Sql(sql);

        var query = await engine.PrepareAsync(sql);
        Tutorial.Plan("plan", query.Plan);
        Console.WriteLine();
        Console.WriteLine("what the database was asked to run:");
        Tutorial.RemoteQueries(query.Plan);
        Console.WriteLine();

        await using var execution = await engine.ExecuteAsync(query);
        await Tutorial.PrintAsync(execution, maxRows: 9);
        Console.WriteLine();
        Tutorial.Counters(execution);
    }
}

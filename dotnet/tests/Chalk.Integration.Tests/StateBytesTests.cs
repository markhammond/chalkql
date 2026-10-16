using Chalk.Client;
using Chalk.Execution.Maintenance;
using Chalk.Sources.Poco;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// A maintained statement's <c>StateBytes</c> is what its state holds (F223). Each state's estimate,
/// made from counts it keeps as rows come and go, comes within a few percent of what weighing the
/// state's objects finds: a join's buckets, the result's key index, an aggregate's groups and the sums
/// and extremes it keeps for each, an as-of join's runs, a window's orders, a LIMIT's rows. It does so
/// after the update from nothing and after the updates that follow.
/// </summary>
/// <remarks>
/// Before, a state counted its arrays but not the objects it keeps for each key, such as a list and a
/// key array per bucket and per key of the result, nor some of the buffers it keeps between updates.
/// A join of many small keys counted about two thirds of what it held.
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class StateBytesTests(SharedSidecar sidecar, ITestOutputHelper output)
{
    private sealed record Order(int Id, int Customer, long Amount, double Price, DateTime Ts);

    private sealed record Customer(int Id, string Name, string Region);

    private sealed record Quote(int Customer, DateTime Ts, double Px);

    private const int Customers = 4_000;

    private static readonly DateTime Open = new(2026, 10, 16, 9, 0, 0);

    /// <summary>The statements, each with its key columns.</summary>
    public static TheoryData<string, string, int[]> Statements() => new()
    {
        { "a join of many keys", "SELECT o.id, c.name FROM orders o JOIN customers c ON c.id = o.customer", [0] },
        {
            "sums, counts and extremes by key",
            "SELECT customer, COUNT(*) AS n, SUM(amount) AS q, SUM(price) AS notional, MIN(price) AS lo, MAX(price) AS hi FROM orders GROUP BY customer",
            [0]
        },
        {
            "an as-of join",
            "SELECT o.id, q.px FROM orders o LEFT ASOF JOIN quotes q MATCH_CONDITION q.ts <= o.ts ON q.customer = o.customer",
            [0]
        },
        {
            "a semi join",
            "SELECT c.id, c.name FROM customers c WHERE EXISTS (SELECT 1 FROM orders o WHERE o.customer = c.id AND o.amount > 500)",
            [0]
        },
        {
            "a window over the clock",
            "SELECT id, amount FROM orders WHERE ts > CAST(@now AS TIMESTAMP(9)) - INTERVAL '30' MINUTE",
            [0]
        },
        { "the first rows in an order", "SELECT id, amount FROM orders ORDER BY amount DESC, id LIMIT 200", [0] },
    };

    /// <summary>
    /// After the update from nothing and after three updates of new orders, renamed customers, new
    /// quotes and a moving clock, every state, and the result, counts what weighing it finds, within 2%
    /// and a few kilobytes of the objects a state is made of; and the statement's <c>StateBytes</c> is
    /// their sum.
    /// </summary>
    [Theory]
    [MemberData(nameof(Statements))]
    public async Task Each_state_counts_what_it_holds(string name, string sql, int[] key)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var shop = await OpenAsync(sql, key);
        var (engine, source, prepared, maintained, random) = (shop.Engine, shop.Source, shop.Prepared, shop.Maintained, shop.Random);
        var (orders, customers, quotes, now) = (shop.Orders, shop.Customers, shop.Quotes, shop.Now);

        _ = await maintained.UpdateAsync(Values(prepared, now), default, engine.Version, whole: false, Ct);
        AssertCounted(maintained, $"{name}, after the update from nothing");

        var nextId = orders.Count;
        for (var round = 0; round < 3; round++)
        {
            now = now.AddMinutes(5);
            var latest = orders[^1].Ts;
            List<Order> appended = [.. Enumerable.Range(0, 500)
                .Select(i => new Order(++nextId, 1 + random.Next(Customers), random.Next(1_000), Math.Round(random.NextDouble() * 100, 2), latest.AddMilliseconds((i + 1) * 100)))];
            orders.AddRange(appended);
            List<Customer> renamed = [.. Enumerable.Range(0, 100)
                .Select(_ => customers[random.Next(Customers)])
                .DistinctBy(c => c.Id)
                .Select(c => c with { Name = $"{c.Name}-{round}" })];
            var lastQuote = quotes[^1].Ts;
            List<Quote> quoted = [.. Enumerable.Range(0, 200)
                .Select(i => new Quote(1 + random.Next(Customers), lastQuote.AddMilliseconds((i + 1) * 250), Math.Round(random.NextDouble() * 100, 3)))];
            quotes.AddRange(quoted);
            await engine.RefreshAsync(
                r => r.Append(source, "orders", (IReadOnlyList<Order>)appended)
                    .Upsert(source, "customers", (IReadOnlyList<Customer>)renamed)
                    .Append(source, "quotes", (IReadOnlyList<Quote>)quoted)
                    .AdvanceClock(new DateTimeOffset(now, TimeSpan.Zero)),
                Ct);
            _ = await maintained.UpdateAsync(Values(prepared, now), default, engine.Version, whole: false, Ct);
        }

        AssertCounted(maintained, $"{name}, after three updates");
    }

    /// <summary>
    /// After the update from nothing, each operator that keeps scratch between updates holds no more
    /// than a small update needs (F236): the arrays and maps that update grew to every row, and that
    /// the next one clears, are let go, as the states' buffers are. Before, a join's candidate pairs,
    /// a left join's counts and a read's weight column kept the size the first update grew them to,
    /// for the life of the view.
    /// </summary>
    [Theory]
    [MemberData(nameof(Statements))]
    public async Task Each_operator_lets_go_of_the_scratch_its_first_update_grew(string name, string sql, int[] key)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var shop = await OpenAsync(sql, key);
        _ = await shop.Maintained.UpdateAsync(Values(shop.Prepared, shop.Now), default, shop.Engine.Version, whole: false, Ct);

        Assert.NotEmpty(shop.Maintained.Scratch);
        foreach (var keeper in shop.Maintained.Scratch)
        {
            // The operator and what it keeps for itself: not its state, its inputs, its expressions,
            // nor the batches it gives, which the next update replaces.
            var weight = HeapWeight.Of(
                [keeper],
                other => other is IMaintainedState or Chalk.Execution.Operators.IBatchOperator or Chalk.Execution.Operators.OperatorContext
                    or Chalk.Execution.Expressions.EvalContext or Chalk.Execution.Expressions.IVectorExpr or Chalk.Sources.ColumnarBatch);
            output.WriteLine($"{name}: {keeper.GetType().Name} keeps {weight:N0} bytes after the update from nothing");
            Assert.True(weight < 16 * 1024, $"{name}: {keeper.GetType().Name} keeps {weight:N0} bytes after the update from nothing");
        }
    }

    /// <summary>The shop's tables in an engine with a clock, and the statement maintained over them, before any update.</summary>
    private async Task<Shop> OpenAsync(string sql, int[] key)
    {
        var random = new Random(23);
        var now = Open.AddHours(1);
        var orders = Enumerable.Range(1, 20_000)
            .Select(i => new Order(i, 1 + random.Next(Customers), random.Next(1_000), Math.Round(random.NextDouble() * 100, 2), Open.AddMilliseconds(i * 180)))
            .ToList();
        var customers = Enumerable.Range(1, Customers).Select(i => new Customer(i, $"customer-{i}", i % 3 == 0 ? "apac" : "emea")).ToList();
        var quotes = Enumerable.Range(0, 8_000)
            .Select(i => new Quote(1 + random.Next(Customers), Open.AddMilliseconds(i * 450), Math.Round(random.NextDouble() * 100, 3)))
            .ToList();
        var source = new PocoSourceBuilder("shop")
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .AddTable("orders", (IReadOnlyList<Order>)orders, t => t.OrderedBy(x => x.Ts))
            .AddTable("customers", (IReadOnlyList<Customer>)customers, t => t.Keyed(x => x.Id))
            .AddTable("quotes", (IReadOnlyList<Quote>)quotes, t =>
            {
                t.OrderedBy(x => x.Ts);
                t.Index(x => x.Customer, x => x.Ts);
            })
            .Build();
        var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            Sources = [source],
            Planner = sidecar.CreatePlanner(),
            Clock = "now",
        }, Ct);
        await engine.RefreshAsync(r => r.AdvanceClock(new DateTimeOffset(now, TimeSpan.Zero)), Ct);
        var prepared = await engine.PrepareAsync(sql, ct: Ct);
        var clock = Enumerable.Range(0, prepared.Parameters.Count)
            .FirstOrDefault(i => string.Equals(prepared.Parameters[i].Name?.TrimStart('@'), "now", StringComparison.OrdinalIgnoreCase), -1);
        var maintained = await prepared.MaintainAsync(Values(prepared, now), clock, key, Ct);
        return new Shop(engine, source, prepared, maintained, random, orders, customers, quotes, now);
    }

    /// <summary>The tables, the engine and the maintained statement a test reads and changes.</summary>
    private sealed record Shop(
        ChalkEngine Engine,
        PocoSource Source,
        PreparedQuery Prepared,
        MaintainedQuery Maintained,
        Random Random,
        List<Order> Orders,
        List<Customer> Customers,
        List<Quote> Quotes,
        DateTime Now) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Maintained.DisposeAsync();
            await Engine.DisposeAsync();
        }
    }

    /// <summary>Each state, and the result, against its weight; each weighed apart from the others.</summary>
    private void AssertCounted(MaintainedQuery maintained, string when)
    {
        List<(object Part, long Counted)> parts = [.. maintained.States.Select(s => ((object)s, s.Bytes)), (maintained.Result, maintained.Result.Bytes)];
        var others = new HashSet<object>(parts.Select(p => p.Part), ReferenceEqualityComparer.Instance);
        long counted = 0;
        foreach (var (part, bytes) in parts)
        {
            var weight = HeapWeight.Of([part], others.Contains);
            output.WriteLine($"{when}: {part.GetType().Name} counts {bytes:N0} bytes, holds {weight:N0}");
            Assert.True(
                Math.Abs(bytes - weight) <= (weight / 50) + 4096,
                $"{when}: {part.GetType().Name} counts {bytes:N0} bytes, and holds {weight:N0}");
            counted += bytes;
        }

        Assert.Equal(counted, maintained.StateBytes);
    }

    private static object?[] Values(PreparedQuery prepared, DateTime now) =>
        [.. prepared.Parameters.Select(_ => (object?)DateTime.SpecifyKind(now, DateTimeKind.Unspecified))];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}

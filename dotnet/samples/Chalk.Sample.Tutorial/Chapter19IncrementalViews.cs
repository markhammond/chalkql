using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Chalk.Client;
using Chalk.Incremental;
using Chalk.Sources.Poco;

namespace Chalk.Sample.Tutorial;

/// <summary>
/// Chapter 19 — incremental views. A statement kept current as one row per key, and a reader sent
/// only what changed since it last read: the latest row of each key, never a queue of every change.
/// A view recomputes its statement at each update, or, experimentally, maintains its answer from the
/// rows that changed, with its planning left to the background and new plans built beside it.
/// </summary>
public static class Chapter19IncrementalViews
{
    private sealed record Movement(int Id, string Warehouse, string Product, int Delta, DateTime Ts);

    [InlineArray(16)]
    private struct Label
    {
        private byte _first;
    }

    /// <summary>The host's key: two short labels, inline, so a drain copies them and allocates nothing.</summary>
    private struct StockKey
    {
        public Label Warehouse;
        public Label Product;
        public byte WarehouseLength;
        public byte ProductLength;

        public readonly override string ToString() =>
            Encoding.UTF8.GetString(((ReadOnlySpan<byte>)Warehouse)[..WarehouseLength]) + " · "
            + Encoding.UTF8.GetString(((ReadOnlySpan<byte>)Product)[..ProductLength]);
    }

    private struct Stock
    {
        public long OnHand;
    }

    private const string OnHand = """
        SELECT warehouse, product, SUM(delta) AS on_hand
        FROM movements
        WHERE ts <= CAST(@now AS TIMESTAMP(9))
        GROUP BY warehouse, product
        HAVING SUM(delta) <> 0
        """;

    private static readonly DateTime Morning = new(2026, 10, 12, 9, 0, 0);

    public static async Task RunAsync(Func<IQueryPlanner> planner)
    {
        Tutorial.Chapter(
            19,
            "Incremental views",
            "Keep a statement's answer current as one row per key, and send each reader only what\n"
            + "changed since it last read: the latest row of each key, never a queue of every change.\n"
            + "Recompute the statement at each update, or maintain its answer from what changed.");

        IReadOnlyList<Movement> ledger =
        [
            new(1, "North", "widget", 12, Morning),
            new(2, "North", "gadget", 5, Morning.AddMinutes(1)),
            new(3, "South", "widget", 7, Morning.AddMinutes(2)),
        ];

        var stock = new PocoSourceBuilder("stock")
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .AddTable("movements", ledger, t => t.AppendOnly(m => m.Ts))
            .Build();

        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "tutorial-19",
            Sources = [stock],
            Planner = planner(),
            Clock = "now",
        });

        Tutorial.Step("the ledger is append-only, by its time; the engine's clock is @now");
        await engine.RefreshAsync(epoch => epoch.Version(1).AdvanceClock(Utc(Morning.AddMinutes(5))));

        Tutorial.Step("a view over it, keyed by warehouse and product");
        Tutorial.Sql(OnHand);
        await using var view = await engine.MaterializeAsync(OnHand, new ViewOptions<StockKey, Stock>
        {
            Key = ["warehouse", "product"],
            LocalJoinMaxRows = StandingViews.DefaultLocalJoinMaxRows,
            Map = Map,
            RowHash = Hash,
        });

        Tutorial.Step("a reader's first drain is a reset: every row, then a commit");
        var shop = new Printer();
        var shopCursor = ViewCursor.Start;
        view.Drain(ref shopCursor, shop);
        var audit = new Printer();
        var auditCursor = ViewCursor.Start;
        view.Drain(ref auditCursor, new Quiet());

        Tutorial.Step("a batch of movements lands in one epoch, numbered by the host");
        await engine.RefreshAsync(epoch => epoch
            .Version(2)
            .Append(stock, "movements", (IReadOnlyList<Movement>)
            [
                new(4, "North", "widget", -12, Morning.AddMinutes(6)),
                new(5, "South", "gadget", 3, Morning.AddMinutes(6)),
            ])
            .AdvanceClock(Utc(Morning.AddMinutes(7))));
        await view.UpdateAsync();

        Tutorial.Step("the next drain carries only what moved: North's widgets are gone, South has gadgets");
        view.Drain(ref shopCursor, shop);

        Tutorial.Step("one more epoch; a reader that skipped one has as many changes waiting as rows,\n"
            + "   so it is sent the whole result again, as a reset");
        await engine.RefreshAsync(epoch => epoch
            .Version(3)
            .Append(stock, "movements", (IReadOnlyList<Movement>)[new(6, "North", "gadget", 1, Morning.AddMinutes(8))])
            .AdvanceClock(Utc(Morning.AddMinutes(9))));
        await view.UpdateAsync();
        view.Drain(ref auditCursor, audit);

        Tutorial.Step("a movement older than the ledger's frontier refuses the whole refresh");
        try
        {
            await engine.RefreshAsync(epoch => epoch.Append(
                stock, "movements", (IReadOnlyList<Movement>)[new(7, "South", "widget", 1, Morning.AddMinutes(3))]));
        }
        catch (ChalkException refused)
        {
            Console.WriteLine(Wrap(refused.Message));
        }

        Tutorial.Step("a view refuses what it could not keep, such as an order its rows do not have");
        try
        {
            await engine.MaterializeAsync(
                OnHand + "\nORDER BY on_hand",
                new ViewOptions<StockKey, Stock>
                {
                    Key = ["warehouse", "product"],
                    LocalJoinMaxRows = StandingViews.DefaultLocalJoinMaxRows,
                    Map = Map,
                });
        }
        catch (ChalkException refused)
        {
            Console.WriteLine(Wrap(refused.Message));
        }

        Tutorial.Step("the same statement, maintained: an update computes only what changed,\n"
            + "   and a new plan is built beside the one it runs before it swaps in");
#pragma warning disable CHALK003 // incremental maintenance is experimental
        await using var maintained = await engine.MaterializeAsync(OnHand, new ViewOptions<StockKey, Stock>
        {
            Key = ["warehouse", "product"],
            LocalJoinMaxRows = StandingViews.DefaultLocalJoinMaxRows,
            Map = Map,
            RowHash = Hash,
            Maintenance = ViewMaintenance.Incremental,
            PlanSwap = ViewPlanSwap.LowJitter,
        });
#pragma warning restore CHALK003
        var desk = new Printer();
        var deskCursor = ViewCursor.Start;
        maintained.Drain(ref deskCursor, desk);

        // A reader of the recomputing view that starts beside the maintained view's, so the two drains
        // below send the same changes.
        var screen = new Printer();
        var screenCursor = ViewCursor.Start;
        view.Drain(ref screenCursor, new Quiet());

        Tutorial.Step("refreshes leave statistics and plans to the background; both views take the epoch");
        await engine.RefreshAsync(epoch => epoch
            .Version(4)
            .Append(stock, "movements", (IReadOnlyList<Movement>)
            [
                new(8, "South", "widget", -7, Morning.AddMinutes(10)),
                new(9, "North", "widget", 4, Morning.AddMinutes(10)),
            ])
            .AdvanceClock(Utc(Morning.AddMinutes(11)))
            .DeferPlanning());
        await view.UpdateAsync();
        await maintained.UpdateAsync();
        Console.WriteLine("    recomputed:");
        view.Drain(ref screenCursor, screen);
        Console.WriteLine("    maintained:");
        maintained.Drain(ref deskCursor, desk);

        Tutorial.Step("PlanAsync waits for the background's plans, which a host awaits only before it serves;\n"
            + "   a view takes a new plan at its next update, and under LowJitter builds it first");
        await engine.PlanAsync();
        await engine.RefreshAsync(epoch => epoch
            .Version(5)
            .Append(stock, "movements", (IReadOnlyList<Movement>)[new(10, "South", "gadget", 2, Morning.AddMinutes(12))])
            .AdvanceClock(Utc(Morning.AddMinutes(13)))
            .DeferPlanning());
        await view.UpdateAsync();
        await maintained.UpdateAsync();
        Console.WriteLine("    recomputed:");
        view.Drain(ref screenCursor, screen);
        Console.WriteLine("    maintained:");
        maintained.Drain(ref deskCursor, desk);
    }

    private static DateTimeOffset Utc(DateTime time) => new(time, TimeSpan.Zero);

    private static void Map(in ViewRow row, out StockKey key, out Stock value)
    {
        key = default;
        var warehouse = row.GetUtf8(0);
        var product = row.GetUtf8(1);
        warehouse.CopyTo(key.Warehouse);
        product.CopyTo(key.Product);
        key.WarehouseLength = (byte)warehouse.Length;
        key.ProductLength = (byte)product.Length;
        value = new Stock { OnHand = row.GetInt64(2) };
    }

    private static uint Hash(ReadOnlySpan<byte> key, ReadOnlySpan<byte> row)
    {
        var hash = 2166136261u;
        foreach (var b in key)
        {
            hash = (hash ^ b) * 16777619u;
        }

        foreach (var b in row)
        {
            hash = (hash ^ b) * 16777619u;
        }

        return hash;
    }

    private static string Wrap(string text)
    {
        var lines = new StringBuilder();
        var line = new StringBuilder("    ");
        foreach (var word in text.Split(' '))
        {
            if (line.Length + word.Length > 92 && line.Length > 4)
            {
                lines.AppendLine(line.ToString().TrimEnd());
                line.Clear().Append("    ");
            }

            line.Append(word).Append(' ');
        }

        return lines.Append(line.ToString().TrimEnd()).ToString();
    }

    /// <summary>A sink that prints what it is sent, as a host's sink would apply it.</summary>
    private sealed class Printer : IViewSink<StockKey, Stock>
    {
        public void Reset() => Console.WriteLine("    reset");

        public void Upsert(in StockKey key, in Stock row) =>
            Console.WriteLine($"    upsert {key} = {row.OnHand.ToString(CultureInfo.InvariantCulture)}");

        public void Delete(in StockKey key) => Console.WriteLine($"    delete {key}");

        public void Commit(long version, uint checksum) =>
            Console.WriteLine($"    commit version {version}, checksum {checksum:x8}");
    }

    private sealed class Quiet : IViewSink<StockKey, Stock>
    {
        public void Reset()
        {
        }

        public void Upsert(in StockKey key, in Stock row)
        {
        }

        public void Delete(in StockKey key)
        {
        }

        public void Commit(long version, uint checksum)
        {
        }
    }
}

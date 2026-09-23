using Akade.IndexedSet;
using Chalk.Client;
using Chalk.Ir;
using Chalk.Sources;
using Chalk.Sources.Akade;
using Chalk.Sources.Poco;

namespace Chalk.Sample.AkadeIndexedSet;

/// <summary>
/// Akade.IndexedSet as a first-class Chalk source.
///
/// The physical index definitions intentionally mirror Akade's README overview. The source itself is
/// one IndexedSet -> one source -> one logical table; no AkadeRows wrapper or manually registered
/// IPocoIndex instances are required.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var rows = AkadeReadmeExamples.PlanningPurchases();

        var purchases = AkadeReadmeExamples.BuildPurchases(rows);

        var source = AkadeSource
            .From("purchases", purchases)
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            // Optional. This is required only for RefreshBuilder.Replace/Append, not ordinary
            // host mutation or Refresh(source/table).
            .RebuildWith(AkadeReadmeExamples.BuildPurchases)
            .Build();

        PrintCatalog(source);

        Console.WriteLine();
        Console.WriteLine("Akade-native README examples:");
        RunAkadeExamples(purchases);

        await using var sidecar = await PlannerProcess.StartAsync();
        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "akade",
            Sources = [source],
            Planner = sidecar.CreatePlanner(),
        });

        Console.WriteLine();
        Console.WriteLine("Chalk SQL:");

        await ShowQueryAsync(
            engine,
            "non-unique ProductId",
            "SELECT id, product_id, amount, unit_price FROM purchases WHERE product_id = ?",
            [4]);

        await ShowQueryAsync(
            engine,
            "range Amount",
            "SELECT id, product_id, amount, unit_price FROM purchases "
            + "WHERE amount BETWEEN ? AND ?",
            [1, 3]);

        await ShowQueryAsync(
            engine,
            "range UnitPrice",
            "SELECT id, product_id, amount, unit_price FROM purchases WHERE unit_price >= ?",
            [10]);

        await ShowQueryAsync(
            engine,
            "compound equality",
            "SELECT id, product_id, amount, unit_price FROM purchases "
            + "WHERE product_id = ? AND unit_price = ?",
            [4, 10]);

        // Live mode: mutate the host-owned set directly. Query data changes immediately, but the
        // catalog metadata still describes the last source snapshot until it is explicitly refreshed.
        Console.WriteLine();
        Console.WriteLine("live host mutation:");
        var before = source.DescribeSchema().FindTable("purchases")!.RowCount;

        purchases.Add(new Purchase(Id: 8, ProductId: 4, Amount: 1, UnitPrice: 11));

        var live = await CountAsync(
            engine,
            "SELECT id FROM purchases WHERE product_id = ?",
            [4]);

        var stale = source.DescribeSchema().FindTable("purchases")!.RowCount;
        Console.WriteLine(
            $"query sees {live.Rows} ProductId=4 rows; catalog row count is still {stale} (was {before})");

        // Table-scoped refresh does not rebuild the IndexedSet and does not discover new indexes. It
        // simply re-describes the table/statistics against the current supported topology.
        await engine.RefreshAsync(refresh => refresh.Refresh(source.Table));

        var refreshed = source.DescribeSchema().FindTable("purchases")!.RowCount;
        Console.WriteLine($"after Refresh(table): catalog row count = {refreshed}");

        // Strong path: Append builds a fresh IndexedSet off the execution path and atomically publishes
        // it. RebuildWith above is what makes this operation available.
        await engine.RefreshAsync(refresh =>
            refresh.Append(
                source.Table,
                [new Purchase(Id: 9, ProductId: 4, Amount: 9, UnitPrice: 9)],
                StatisticsRefresh.Defer));

        var appended = await CountAsync(
            engine,
            "SELECT id FROM purchases WHERE product_id = ?",
            [4]);

        var product4Query = await engine.PrepareAsync(
            "SELECT id FROM purchases WHERE product_id = ?");
        
        // Transactional Append has published the six-row successor.
        var afterAppend = await CountProduct4Async(engine, product4Query);
        Console.WriteLine(
            $"after transactional Append: {afterAppend.Rows} ProductId=4 rows; " +
            $"{afterAppend.Scanned} rows scanned");

        // Table refresh preserves that published successor.
        await engine.RefreshAsync(refresh =>
        {
            refresh.Refresh(source.Table);
        });

        var afterTableRefresh = await CountProduct4Async(engine, product4Query);
        var afterTableDescriptor =
            source.DescribeSchema().FindTable("purchases")!;

        Console.WriteLine(
            $"after Refresh(table) post-Append: " +
            $"{afterTableRefresh.Rows} ProductId=4 rows; " +
            $"catalog row count = {afterTableDescriptor.RowCount}");

        // Source refresh re-reads the external registration.
        // In this example it still refers to the five-row host set.
        await engine.RefreshAsync();

        var afterSourceRefresh = await CountProduct4Async(engine, product4Query);
        var afterSourceDescriptor =
            source.DescribeSchema().FindTable("purchases")!;

        Console.WriteLine(
            $"after Refresh(source): " +
            $"{afterSourceRefresh.Rows} ProductId=4 rows; " +
            $"catalog row count = {afterSourceDescriptor.RowCount}");
        
        return 0;
    }
    
    private static async Task<(int Rows, long Scanned)> CountProduct4Async(
        ChalkEngine engine,
        PreparedQuery query)
    {
        await using var execution =
            await engine.ExecuteAsync(query, [4]);

        var rows = 0;

        await foreach (var batch in execution.Batches)
        {
            rows += batch.Length;
            batch.Dispose();
        }

        return (
            rows,
            (long)execution.Stats.RowsScanned);
    }

    private static void PrintCatalog(IndexedSetSource<Purchase> source)
    {
        var table = source.DescribeSchema().FindTable("purchases")!;

        Console.WriteLine($"purchases: {table.RowCount} rows");
        Console.WriteLine(
            "indexes: "
            + (table.Indexes.Count == 0
                ? "(none advertised)"
                : string.Join(
                    ", ",
                    table.Indexes.Select(index =>
                        $"{index.Name} [{index.Kind}] ({string.Join(",", index.Columns)})"))));
    }

    private static void RunAkadeExamples(IndexedSet<int, Purchase> purchases)
    {
        Console.WriteLine(
            "  product=4: "
            + string.Join(", ", purchases.Where(x => x.ProductId, 4).Select(x => x.Id)));

        Console.WriteLine(
            "  amount 1..3: "
            + string.Join(
                ", ",
                purchases.Range(
                        x => x.Amount,
                        1,
                        3,
                        inclusiveStart: true,
                        inclusiveEnd: true)
                    .Select(x => x.Id)));

        Console.WriteLine(
            "  unit_price >= 10: "
            + string.Join(", ", purchases.GreaterThanOrEqual(x => x.UnitPrice, 10).Select(x => x.Id)));

        Console.WriteLine(
            "  max total: " 
            + string.Join(", ", AkadeReadmeExamples.MaxTotal(purchases).Select(x => x.Id)));
        
        Console.WriteLine(
            "  (product,price)=(4,10): "
            + string.Join(
                ", ",
                AkadeReadmeExamples
                    .ProductAndPrice(purchases, 4, 10).Select(x => x.Id)));
    }

    private static async Task ShowQueryAsync(
        ChalkEngine engine,
        string label,
        string sql,
        IReadOnlyList<object?> parameters)
    {
        var query = await engine.PrepareAsync(sql);
        Console.WriteLine($"{label} plan:");
        Console.WriteLine(query.Plan.ToPlanText().TrimEnd());

        var (rows, scanned) = await CountAsync(engine, query, parameters);
        Console.WriteLine($"  {rows} row(s), {scanned} scanned");
    }

    private static async Task<(int Rows, long Scanned)> CountAsync(
        ChalkEngine engine,
        string sql,
        IReadOnlyList<object?> parameters)
    {
        var query = await engine.PrepareAsync(sql);
        return await CountAsync(engine, query, parameters);
    }

    private static async Task<(int Rows, long Scanned)> CountAsync(
        ChalkEngine engine,
        PreparedQuery query,
        IReadOnlyList<object?> parameters)
    {
        await using var execution = await engine.ExecuteAsync(query, parameters);

        var rows = 0;
        await foreach (var batch in execution.Batches)
        {
            rows += batch.Length;
            batch.Dispose();
        }

        return (rows, (long)execution.Stats.RowsScanned);
    }
}

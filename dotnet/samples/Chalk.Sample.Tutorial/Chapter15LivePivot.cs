using Chalk.Client;
using Chalk.Entitlements;
using Chalk.Entitlements.Tenancy;
using Chalk.Sources.Poco;

namespace Chalk.Sample.Tutorial;

/// <summary>
/// Chapter 15 — live pivot. One analytical question — <em>what is inventory by supplier and
/// warehouse right now?</em> — asked over a coherent snapshot while a refresh replaces the positions
/// underneath it. What this chapter establishes: <c>PIVOT</c>, snapshot consistency, and warehouse
/// and supplier tenancy.
/// </summary>
public static class Chapter15LivePivot
{
    private const string Scan = """
        SELECT warehouse_id, product_id, quantity
        FROM inventory_positions
        ORDER BY warehouse_id, product_id
        """;

    public static async Task RunAsync(Func<IQueryPlanner> planner)
    {
        Tutorial.Chapter(
            15,
            "Live pivot",
            "What is inventory by supplier and warehouse right now? One statement, asked four times\n"
            + "by four principals, while a refresh replaces every position underneath it. The pivot\n"
            + "is ordinary SQL — Calcite rewrites it to filtered aggregates and the executor runs\n"
            + "them — and the entitlement is applied at the leaf, before anything is added up.");

        var shape = Marketplace.Poco();
        var catalog = Live.Catalog("tutorial-15", shape);
        var perspectives = Perspectives.Declare(
            catalog, Marketplace.Placement.Memory, live: true);
        var entitlements = perspectives.Compile(catalog);

        var source = Marketplace.Poco(out var positions, out _, out _, entitlements);
        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "tutorial-15",
            Sources = [source],

            // Four rows a batch, so a read can be stopped half way through on purpose.
            Execution = new ExecutionOptions { BatchSize = 4 },
            Planner = planner(),
        });

        var entitled = engine.WithEntitlements();

        Tutorial.Step("the question");
        Tutorial.Sql(Live.Positions);

        var finance = perspectives.FinanceDesk();
        var shown = await entitled.PrepareAsync(Live.Positions, entitlements.Bind(finance));
        Tutorial.Plan("plan as Finance", shown.Plan);
        Console.WriteLine();
        Console.WriteLine("    The PIVOT is gone by the time the IR sees it: it is one aggregate whose");
        Console.WriteLine("    measures carry FILTER, and a projection that puts NULL back where a");
        Console.WriteLine("    group had no rows at all. Nothing in the executor knows the word.");

        Tutorial.Step("the same statement, four principals");
        await Live.EachPrincipalAsync(engine, entitled, entitlements, perspectives, Live.Positions);

        Console.WriteLine();
        Console.WriteLine("    Read the four. The operator sees Singapore, whoever supplies it. The");
        Console.WriteLine("    representative sees supplier A everywhere. The reviewer sees supplier A");
        Console.WriteLine("    in Singapore and nothing else — the *intersection*, which is what");
        Console.WriteLine("    `.Within(warehouse, \"SIN\")` conjoins; two separate grants would have");
        Console.WriteLine("    given the union of the first two. And Finance sees everything, because");
        Console.WriteLine("    the policy says AllowGlobalGrants() — off by default, because a grant");
        Console.WriteLine("    that reaches everywhere should be a thing a deployment decided.");

        // ------------------------------------------------------------ the snapshot

        Tutorial.Step("start reading the positions, and stop after one batch");
        Tutorial.Sql(Scan);
        var scan = await entitled.PrepareAsync(Scan, entitlements.Bind(finance));
        var execution = await engine.ExecuteAsync(scan.Query);
        var batches = execution.Batches.GetAsyncEnumerator();
        await batches.MoveNextAsync();
        Print(batches.Current);
        batches.Current.Dispose();

        Tutorial.Step("replace every position, under one epoch, while that read is open");
        var next = Restocked(Data.Positions());
        var epoch = await engine.RefreshAsync(refresh => refresh.Replace(positions, next));

        Console.WriteLine($"    catalog epoch {epoch}, shape epoch {engine.ShapeEpoch}");
        Console.WriteLine($"    the prepared statement is stale: {scan.Query.IsStale}");
        Console.WriteLine();
        Console.WriteLine("    `refresh.Replace(positions, next)` names the table by the handle the");
        Console.WriteLine("    builder handed back — a PocoTable<InventoryPosition>, so the rows it");
        Console.WriteLine("    takes are that table's row type and a misnamed table is not a runtime");
        Console.WriteLine("    error but a compile one. (A host whose tables arrive at run time has a");
        Console.WriteLine("    string form of the same call; this tutorial uses the handle.)");

        Tutorial.Step("finish the read that was already open");
        var rest = 0;
        while (await batches.MoveNextAsync())
        {
            rest += batches.Current.Length;
            batches.Current.Dispose();
        }

        await batches.DisposeAsync();
        await execution.DisposeAsync();

        Console.WriteLine($"    {rest} more rows, and every one of them from the snapshot that read");
        Console.WriteLine("    started with. It was never shown a row of the replacement.");

        Tutorial.Step("and the pivot again, on the next epoch");
        await Live.EachPrincipalAsync(engine, entitled, entitlements, perspectives, Live.Positions);

        Console.WriteLine();
        Console.WriteLine("    Same statement, same four principals, same plan — different numbers,");
        Console.WriteLine("    because the epoch underneath moved. That is the whole of what \"live\"");
        Console.WriteLine("    means here: nothing is mutated, a new snapshot is built beside the old");
        Console.WriteLine("    one and swapped, and a reader that started before the swap finishes");
        Console.WriteLine("    the answer it started.");
    }

    /// <summary>The next positions: the same shape, restocked, so the pivot's numbers move.</summary>
    private static IReadOnlyList<InventoryPosition> Restocked(IReadOnlyList<InventoryPosition> current) =>
        [.. current.Select(p => p with { Quantity = p.Quantity + ((p.ProductId * 7) % 40) - 10 })];

    private static void Print(Apache.Arrow.RecordBatch batch)
    {
        var warehouse = batch.Column(0);
        var product = (Apache.Arrow.Int32Array)batch.Column(1);
        var quantity = (Apache.Arrow.Int32Array)batch.Column(2);
        for (var row = 0; row < batch.Length; row++)
        {
            Console.WriteLine(
                $"    {Chalk.Arrow.RecordBatchExtensions.GetUtf8String(warehouse, row),-4} "
                + $"{product.GetValue(row),3}  {quantity.GetValue(row)}");
        }
    }
}

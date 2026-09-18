using Chalk.Catalog;
using Chalk.Client;
using Chalk.Entitlements;
using Chalk.Ir;
using Chalk.Sources.Poco;

namespace Chalk.Sample.Tutorial;

/// <summary>
/// Chapter 16 — streaming live pivot. Chapter 15's question, kept live as stock moves: a batch of
/// movements is appended to the ledger and the positions the host derived from it are replaced,
/// under one epoch. One concept is added — append-only facts — and the pivot text does not change.
/// </summary>
public static class Chapter16Streaming
{
    private const string Ledger = """
        SELECT movement_id, ts, warehouse_id, product_id, delta
        FROM inventory_movements
        ORDER BY movement_id DESC
        """;

    private const string Frontier = """
        SELECT warehouse_id, stage, COUNT(*) AS entries, MAX(movement_id) AS frontier
        FROM federated.ledger
        GROUP BY warehouse_id, stage
        ORDER BY warehouse_id, stage
        """;

    public static async Task RunAsync(Func<IQueryPlanner> planner)
    {
        Tutorial.Chapter(
            16,
            "Streaming live pivot",
            "Keep that same pivot live as stock moves. One concept added: append-only facts. A batch\n"
            + "of movements arrives, the host applies it to get the next positions, and both land\n"
            + "under one epoch — so the next execution of the same statement sees the next coherent\n"
            + "answer and never a half-applied one.");

        var shape = Marketplace.Poco();
        var catalog = Live.Catalog("tutorial-16", shape);
        var perspectives = Perspectives.Declare(catalog, Marketplace.Placement.Memory, live: true);
        var entitlements = perspectives.Compile(catalog);

        var source = Marketplace.Poco(out var positions, out var movements, out _, entitlements);
        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "tutorial-16",
            Sources = [source],
            Planner = planner(),
        });

        var entitled = engine.WithEntitlements();
        var finance = perspectives.FinanceDesk();

        Tutorial.Step("the ledger as it stands, newest first");
        var before = await entitled.PrepareAsync(Ledger, entitlements.Bind(finance));
        await Chapter10WhosAsking.PrintAsync(engine, before, maxRows: 4);

        Tutorial.Step("the pivot, before the batch");
        Tutorial.Sql(Live.Positions);
        await Live.EachPrincipalAsync(engine, entitled, entitlements, perspectives, Live.Positions);

        // ------------------------------------------------------------ the batch

        Tutorial.Step("a batch of movements, appended — and the positions it implies, replaced");
        var batch = Data.MovementBatch();
        foreach (var movement in batch)
        {
            Console.WriteLine(
                $"    {movement.Ts:HH:mm}  {movement.WarehouseId}  product {movement.ProductId,2}  "
                + $"{movement.Delta,+4}");
        }

        var next = Apply(Data.Positions(), batch);
        var epoch = await engine.RefreshAsync(refresh =>
        {
            refresh.Append(movements, batch);
            refresh.Replace(positions, next);
        });

        Console.WriteLine();
        Console.WriteLine($"    catalog epoch {epoch} — one epoch, two tables");
        Console.WriteLine();
        Console.WriteLine("        await engine.RefreshAsync(refresh =>");
        Console.WriteLine("        {");
        Console.WriteLine("            refresh.Append(movements, batch);");
        Console.WriteLine("            refresh.Replace(positions, next);");
        Console.WriteLine("        });");
        Console.WriteLine();
        Console.WriteLine("    An append extends the table in place: the columns grow, the declared");
        Console.WriteLine("    order is checked where the new rows join the old, and the index over");
        Console.WriteLine("    movement_id extends without sorting anything, because the appended");
        Console.WriteLine("    keys were already in order. The replacement is a whole new snapshot");
        Console.WriteLine("    beside the old one. Both are visible at the same instant or neither");
        Console.WriteLine("    is, which is what one epoch buys: no execution can see a movement");
        Console.WriteLine("    that its positions have not accounted for.");

        Tutorial.Step("the ledger again");
        var after = await entitled.PrepareAsync(Ledger, entitlements.Bind(finance));
        await Chapter10WhosAsking.PrintAsync(engine, after, maxRows: 4);

        Tutorial.Step("and the pivot — the same text, the next epoch");
        await Live.EachPrincipalAsync(engine, entitled, entitlements, perspectives, Live.Positions);

        Console.WriteLine();
        Console.WriteLine("    Singapore moved, because that is where two of the four movements");
        Console.WriteLine("    landed, and the reviewer's slice moved with the representative's where");
        Console.WriteLine("    the two overlap. Nobody's statement changed.");

        await BootstrappingAsync(planner);
    }

    // ---------------------------------------------------------------- bootstrapping a stream

    /// <summary>
    /// <b>Bootstrapping a stream.</b> A live pivot that starts from nothing is a live pivot that is
    /// wrong for as long as the history takes to arrive. The usual answer is two tables and a union
    /// in every query; the answer here is one table whose partitions live in two different kinds of
    /// source — the history in a warehouse, the tail in memory.
    /// </summary>
    private static async Task BootstrappingAsync(Func<IQueryPlanner> planner)
    {
        Tutorial.Step("Bootstrapping a stream");
        Console.WriteLine("    The ledger so far, split where the feed took over: everything up to");
        Console.WriteLine("    movement §16 was loaded from the warehouse, and everything after it");
        Console.WriteLine("    arrived on the wire. Two physical tables, in two kinds of source, and");
        Console.WriteLine("    one logical table over them.");

        var history = Data.Movements().Where(m => m.MovementId <= 16).Select(Entry("history")).ToList();
        var tail = Data.Movements().Where(m => m.MovementId > 16).Select(Entry("live")).ToList();

        using var warehouse = Database.DuckDb("warehouse");
        warehouse.Load(LedgerTable("ledger_history"), history, Values);

        var historySource = Marketplace.Partition(warehouse, LedgerTable("ledger_history"));

        // The tail, in memory, with a clustered index on the ledger's own sequence: the feed appends
        // in order, so the index extends rather than being rebuilt, and a lookup by movement_id
        // reads the rows themselves rather than a permutation into them.
        var buffer = tail;
        var tailSource = new PocoSourceBuilder("tail", "tail")
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .AddTable(
                "ledger_live",
                () => buffer,
                out var tailTable,
                t => t
                    .OrderedBy(m => m.MovementId)
                    .UniqueKey(m => m.MovementId)
                    .UniqueClusteredIndex(m => m.MovementId))
            .Build();

        var federated = new PartitionCatalog(
            "federated",
            [
                PartitionCatalog.Table(
                    "ledger",
                    LedgerTable("ledger").Columns,
                    partitionColumn: 5,
                    [("warehouse", "history"), ("tail", "live")],
                    stage => stage == "history" ? "ledger_history" : "ledger_live",
                    rowsPerPartition: 12),
            ]);

        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "tutorial-16b",
            Sources = [historySource, tailSource, federated],
            Planner = planner(),
        });

        Tutorial.Sql(Frontier);
        var query = await engine.PrepareAsync(Frontier);
        Tutorial.Plan("plan", query.Plan);
        Console.WriteLine();
        Console.WriteLine("what each partition was asked to run:");
        Tutorial.RemoteQueries(query.Plan);
        Console.WriteLine();
        await using (var execution = await engine.ExecuteAsync(query))
        {
            await Tutorial.PrintAsync(execution);
            Console.WriteLine();
            Tutorial.Counters(execution);
        }

        Console.WriteLine();
        Console.WriteLine("    One partition became SQL and the other did not, because the other has");
        Console.WriteLine("    no dialect: an in-process list is read where it stands. The frontier a");
        Console.WriteLine("    feed processor needs — the last movement it has per warehouse — is");
        Console.WriteLine("    read off the bootstrap's own snapshot by the same statement, which is");
        Console.WriteLine("    what makes \"where do I resume?\" a query rather than a protocol.");

        Tutorial.Step("the tail-buffer swap, which is the whole of a feed processor");
        var arrived = Data.MovementBatch().Select(Entry("live")).ToList();
        buffer = [.. buffer, .. arrived];
        var epoch = await engine.RefreshAsync(refresh => refresh.Replace(tailTable, buffer));
        Console.WriteLine($"    {arrived.Count} entries buffered and swapped in, catalog epoch {epoch}");

        await using (var execution = await engine.ExecuteAsync(await engine.PrepareAsync(Frontier)))
        {
            await Tutorial.PrintAsync(execution);
        }

        Console.WriteLine();
        Console.WriteLine("    The feed accumulates into a list the host owns and swaps the whole list");
        Console.WriteLine("    under one epoch; readers in flight finish on the list they started");
        Console.WriteLine("    with. No row is ever mutated, so there is no lock anywhere in this and");
        Console.WriteLine("    no reader is ever blocked by a writer.");
    }

    /// <summary>The ledger entry a partitioned table needs: a movement, and which side it came from.</summary>
    private static Func<InventoryMovement, LedgerEntry> Entry(string stage) =>
        m => new LedgerEntry(m.MovementId, m.Ts, m.WarehouseId, m.ProductId, m.Delta, stage);

    private static object?[] Values(LedgerEntry e) =>
        [e.MovementId, e.Ts, e.WarehouseId, e.ProductId, e.Delta, e.Stage];

    // (the ledger entry carries only what the partitioned table declares)

    private static TableDescriptor LedgerTable(string name) => new()
    {
        Name = name,
        Columns =
        [
            new ColumnDescriptor { Name = "movement_id", Type = ChalkType.Int32() },
            new ColumnDescriptor { Name = "ts", Type = ChalkType.Timestamp() },
            new ColumnDescriptor { Name = "warehouse_id", Type = ChalkType.String() },
            new ColumnDescriptor { Name = "product_id", Type = ChalkType.Int32() },
            new ColumnDescriptor { Name = "delta", Type = ChalkType.Int32() },
            new ColumnDescriptor { Name = "stage", Type = ChalkType.String() },
        ],
        RowCount = -1,
        RowCountKind = RowCountKind.Unknown,
    };

    /// <summary>
    /// The host's own materialisation: <c>next = Apply(current, batch)</c>. This is ordinary C# over
    /// the rows the host already has, and it is the reason <c>inventory_positions</c> exists at all.
    /// </summary>
    private static IReadOnlyList<InventoryPosition> Apply(
        IReadOnlyList<InventoryPosition> current, IReadOnlyList<InventoryMovement> batch)
    {
        var by = current.ToDictionary(p => (p.WarehouseId, p.ProductId));
        foreach (var movement in batch)
        {
            var key = (movement.WarehouseId, movement.ProductId);
            by[key] = by.TryGetValue(key, out var position)
                ? position with { Quantity = position.Quantity + movement.Delta }
                : new InventoryPosition(
                    movement.WarehouseId, movement.ProductId, movement.SupplierId, movement.Delta);
        }

        return [.. by.Values
            .OrderBy(p => p.WarehouseId, StringComparer.Ordinal)
            .ThenBy(p => p.ProductId)];
    }
}

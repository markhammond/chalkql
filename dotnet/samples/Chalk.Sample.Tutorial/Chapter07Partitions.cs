using Chalk.Client;

namespace Chalk.Sample.Tutorial;

/// <summary>
/// Chapter 7 — one table, many places. <c>federated.orders_by_year</c> is one table to whoever
/// queries it and two physical tables in two DuckDB databases underneath. A predicate on the
/// partition key removes the partitions that cannot hold a matching row, at planning.
/// </summary>
public static class Chapter07Partitions
{
    private const string Pruned = """
        SELECT order_year, COUNT(*) AS orders, SUM(freight) AS freight
        FROM federated.orders_by_year
        WHERE order_year = 2026
        GROUP BY order_year
        ORDER BY order_year
        """;

    private const string FanOut = """
        SELECT order_year, COUNT(*) AS orders, SUM(freight) AS freight
        FROM federated.orders_by_year
        GROUP BY order_year
        ORDER BY order_year
        """;

    public static async Task RunAsync(Func<IQueryPlanner> planner)
    {
        Tutorial.Chapter(
            7,
            "One table, many places",
            "Two databases, two physical tables, one logical table. A warehouse that partitions by\n"
            + "year carries the year as a column, and that column is what the partitions are keyed\n"
            + "by: nothing about the statement says where a row lives, the descriptor does, and the\n"
            + "planner reads it.");

        var orders = Data.OrderRows(Data.WarehouseRows);
        var years = orders.Select(Data.Year).Distinct().OrderBy(y => y).ToArray();
        using var current = Database.DuckDb("current");
        using var archive = Database.DuckDb("archive");

        var placement = years
            .Select((year, i) => (Schema: i == years.Length - 1 ? "current" : "archive", Value: year.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            .ToArray();

        foreach (var (schema, value) in placement)
        {
            var database = schema == "current" ? current : archive;
            var year = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
            database.Load(
                YearlyTable(Physical(value)),
                [.. orders.Where(o => Data.Year(o) == year)],
                o => [o.OrderId, o.CustomerId, o.EmployeeId, o.RegionId, o.OrderDate, o.Freight, o.Currency, Data.Year(o)]);
        }

        var currentSource = Marketplace.Partition(current, YearlyTable(Physical(placement[^1].Value)));
        var archiveSource = Marketplace.Partition(archive, YearlyTable(Physical(placement[0].Value)));
        var columns = YearlyTable("orders_by_year").Columns;

        // `order_year` is the eighth column of the physical tables, and it is what they are keyed by.
        var federated = new PartitionCatalog(
            "federated",
            [
                PartitionCatalog.Table(
                    "orders_by_year",
                    columns,
                    partitionColumn: 7,
                    placement,
                    Physical,
                    orders.Count / Math.Max(1, placement.Length)),
            ]);

        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "tutorial-07",
            Sources = [currentSource, archiveSource, federated],
            Planner = planner(),
        });

        Console.WriteLine();
        foreach (var (schema, value) in placement)
        {
            Console.WriteLine($"    {schema}.{Physical(value)} holds order_year = {value}");
        }

        await RunAtAsync(engine, "a predicate on the partition key", Pruned);
        await RunAtAsync(engine, "and one without it", FanOut);
    }

    private static string Physical(string year) => "orders_" + year;

    /// <summary>
    /// The <c>orders</c> row type with the partition key beside it. A logical table is keyed by a
    /// column of its own rows, so a warehouse that splits by year carries the year.
    /// </summary>
    private static Chalk.Catalog.TableDescriptor YearlyTable(string name)
    {
        var orders = Marketplace.Table(Marketplace.Poco().DescribeSchema(), "orders");
        return new Chalk.Catalog.TableDescriptor
        {
            Name = name,
            Columns =
            [
                .. orders.Columns,
                new Chalk.Catalog.ColumnDescriptor
                {
                    Name = "order_year",
                    Type = Chalk.Catalog.ChalkType.Int32(),
                },
            ],
            RowCount = -1,
            RowCountKind = Chalk.Ir.RowCountKind.Unknown,
        };
    }

    private static async Task RunAtAsync(ChalkEngine engine, string label, string sql)
    {
        Tutorial.Step(label);
        Tutorial.Sql(sql);

        var query = await engine.PrepareAsync(sql);
        Tutorial.Plan("plan", query.Plan);
        Console.WriteLine();
        Console.WriteLine("what each partition was asked to run:");
        Tutorial.RemoteQueries(query.Plan);
        Console.WriteLine();

        await using var execution = await engine.ExecuteAsync(query);
        await Tutorial.PrintAsync(execution);
        Console.WriteLine();
        Tutorial.Counters(execution);
    }
}

using Apache.Arrow;
using Chalk.Client;

namespace Chalk.Sample.Tutorial;

/// <summary>
/// Chapter 1 — a list is a table. The marketplace's own <c>IReadOnlyList&lt;T&gt;</c>s become catalog
/// tables, and a join with a <c>GROUP BY</c> and an <c>ORDER BY</c> runs over them.
/// </summary>
public static class Chapter01Tables
{
    private const string Sql = """
        SELECT s.company_name AS supplier, s.country, COUNT(*) AS lines
        FROM suppliers s
        JOIN products p ON p.supplier_id = s.supplier_id
        WHERE p.unit_price > 20
        GROUP BY s.company_name, s.country
        ORDER BY lines DESC, supplier
        """;

    public static async Task RunAsync(Func<IQueryPlanner> planner)
    {
        Tutorial.Chapter(
            1,
            "A list is a table",
            "A marketplace the host already had in memory, registered as tables. No ORM, no mapping\n"
            + "file, no copy of the rows: columns are the record's properties, and the engine reads\n"
            + "the list itself.");

        var shop = Marketplace.Poco();
        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "tutorial-01",
            Sources = [shop],
            Planner = planner(),
        });

        Tutorial.Step("the whole marketplace, as the catalog sees it");
        foreach (var table in engine.Catalog.Schemas[0].Tables)
        {
            Console.WriteLine($"    {table.Name}: {table.RowCount} rows, columns "
                + string.Join(
                    ", ",
                    table.Columns.Select(c => $"{c.Name} {c.Type.Kind}{(c.Type.Nullable ? "?" : string.Empty)}")));
        }

        Console.WriteLine();
        Console.WriteLine("    Every key and every foreign key on those tables was declared and checked,");
        Console.WriteLine("    including employees.manager_id — which names another row of employees, and");
        Console.WriteLine("    is verified against it like any other.");

        Tutorial.Step("one statement over two of them");
        Tutorial.Sql(Sql);

        var query = await engine.PrepareAsync(Sql);
        await using var execution = await engine.ExecuteAsync(query);

        // The result is Arrow. Read it column by column: a STRING column is a StringViewArray —
        // sixteen-byte views over UTF-8, which is what the schema declares — the counts
        // are one buffer of int64, and nothing is boxed into an object[] on the way past. A batch is
        // yours until you dispose it.
        var rows = 0;
        await foreach (var batch in execution.Batches)
        {
            var supplier = (StringViewArray)batch.Column(0);
            var country = (StringViewArray)batch.Column(1);
            var count = (Int64Array)batch.Column(2);
            for (var row = 0; row < batch.Length; row++)
            {
                Console.WriteLine($"    {supplier.GetString(row),-18} {country.GetString(row),-3} {count.GetValue(row)}");
                rows++;
            }

            batch.Dispose();
        }

        Console.WriteLine();
        Console.WriteLine($"{rows} rows produced, {execution.Stats.RowsScanned} scanned, "
            + $"{execution.Stats.BatchesProduced} batch(es), {execution.Stats.BytesFetched} bytes fetched");
    }
}

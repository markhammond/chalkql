using System.Text;
using Apache.Arrow;
using Chalk.Client;
using Chalk.Sources.Poco;

namespace Chalk.Sample.Tutorial;

/// <summary>
/// Chapter 4 — strings without allocation. A <c>Utf8String</c> property is a STRING column whose
/// bytes are copied rather than transcoded, and a result column is read as bytes unless the host
/// asks for a <see cref="string"/>. The chapter measures what a row costs on the managed heap.
/// </summary>
public static class Chapter04Utf8
{
    private const string Sql = "SELECT product_name, supplier_id FROM products WHERE unit_price > 10";

    public static async Task RunAsync(Func<IQueryPlanner> planner)
    {
        Tutorial.Chapter(
            4,
            "Strings without allocation",
            "The marketplace's supplier and product names are not ASCII. A POCO property of type\n"
            + "Utf8String maps to STRING and its bytes are copied straight into the Arrow buffer, so\n"
            + "nothing is transcoded on the way in and nothing is decoded on the way out. The only\n"
            + ".NET string in this chapter is the one printed at the end, and the host asked for it.");

        Tutorial.Step("product names, read back as bytes");
        Tutorial.Sql(Sql);

        await using (var engine = await EngineAsync(planner, "tutorial-04a", Data.Products, 4096))
        {
            await using var execution = await engine.ExecuteAsync(await engine.PrepareAsync(Sql));
            await foreach (var batch in execution.Batches)
            {
                // A STRING column is a StringViewArray unless the host asks for the classic layout
                // with Output.Strings, and the schema says which before a row is read.
                var name = (StringViewArray)batch.Column(0);
                var supplier = (StringViewArray)batch.Column(1);
                for (var row = 0; row < Math.Min(batch.Length, 8); row++)
                {
                    // GetUtf8 hands back a Utf8String over the batch's own buffer: no copy, no
                    // decode, valid until the batch is disposed. Encoding.UTF8.GetString below is
                    // the host deciding to make a string, on the last line, to print it.
                    ReadOnlySpan<byte> bytes = name.GetUtf8(row).AsSpan();
                    Console.WriteLine(
                        $"    {Encoding.UTF8.GetString(bytes),-18} {bytes.Length,2} bytes  "
                        + $"supplier={supplier.GetString(row)}");
                }

                batch.Dispose();
            }
        }

        Console.WriteLine();
        Console.WriteLine("    Count the bytes against the characters: 凍頂烏龍 is four characters and");
        Console.WriteLine("    twelve bytes, and nothing in the path between the list and the print");
        Console.WriteLine("    above had to know that.");

        Tutorial.Step("what a row costs on the managed heap");
        var small = await MeasureAsync(planner, "tutorial-04b", 20_000);
        var large = await MeasureAsync(planner, "tutorial-04c", 40_000);
        var slope = (large.Bytes - small.Bytes) / (double)(large.Rows - small.Rows);

        Console.WriteLine($"    {small.Rows} rows in one batch: {small.Bytes} bytes allocated");
        Console.WriteLine($"    {large.Rows} rows in one batch: {large.Bytes} bytes allocated");
        Console.WriteLine($"    slope: {slope:0.0000} bytes per row");
        Console.WriteLine(
            "    The allocation gates hold a POCO Utf8String column at 0.0000 bytes per row.");
        Console.WriteLine(
            "    Two sizes read in one batch each, because a slope taken with the batch count");
        Console.WriteLine("    growing is a per-batch cost in disguise.");
    }

    private static async Task<(int Rows, long Bytes)> MeasureAsync(
        Func<IQueryPlanner> planner, string contextId, int count)
    {
        await using var engine = await EngineAsync(planner, contextId, Data.ProductRepeat(count), 1 << 17);
        var query = await engine.PrepareAsync(Sql);

        // The first run warms the compiled chunk writers, the JIT and the arena's pools; the second
        // is the one that is counted. Process-wide and precise, so a thread hop cannot flatter it.
        await Tutorial.DrainAsync(await engine.ExecuteAsync(query));
        var before = GC.GetTotalAllocatedBytes(precise: true);
        var rows = await Tutorial.DrainAsync(await engine.ExecuteAsync(query));
        return (rows, GC.GetTotalAllocatedBytes(precise: true) - before);
    }

    private static ValueTask<ChalkEngine> EngineAsync(
        Func<IQueryPlanner> planner, string contextId, IReadOnlyList<Product> rows, int batchSize) =>
        ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = contextId,
            Sources =
            [
                new PocoSourceBuilder("shop")
                    .NamingPolicy(PocoNamingPolicy.SnakeCase)
                    .DefaultDecimalScale(2)
                    .AddTable("products", rows)
                    .Build(),
            ],
            Planner = planner(),

            // Pooled output: the batch's buffers come from the execution's arena rather than from
            // managed arrays, so a row of the result costs nothing on the heap — and the host must
            // dispose every batch promptly, which is what the loops above do.
            Execution = new ExecutionOptions { BatchSize = batchSize, OutputMemory = OutputMemory.Pooled },
        });
}

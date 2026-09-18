using Apache.Arrow;
using Chalk.Tests;

namespace Chalk.Sources.Poco.Tests;

/// <summary>
/// The milestone's scan-path gate (work plan §2): allocated bytes per scanned row below one. This is
/// a counter assertion, not a timing one, so it is stable enough to run in CI — but only measured as
/// <see cref="AllocationProbe"/> measures it. A collection landing inside the measured window charges
/// the measuring thread for up to 8 KB nobody allocated, and the margin this gate has over its budget
/// is narrower than that, so a single one of them would decide it.
/// </summary>
public sealed class PocoAllocationTests
{
    private const int Rows = 20_000;

    [Fact]
    public async Task A_scan_does_not_allocate_per_row()
    {
        var rows = Bars(Rows);
        var source = new PocoSourceBuilder("mem").AddTable("bars", rows).Build();
        using var arena = new ExecutionArena();

        // The probe warms the compiled loops, the JIT and the arena's pools first; what a warm run
        // then rents back out of the arena (§5.3, ADR 0012) is what is being measured.
        var (allocated, scanned) = await AllocationProbe.SteadyStateAsync(
            () => ScanAsync(source, arena));

        Assert.Equal(Rows, scanned);
        Assert.Equal(0, arena.OutstandingBytes);
        var perRow = (double)allocated / Rows;
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"List<BarRow>: {allocated} bytes for {Rows} rows = {perRow:0.0000} bytes/row");
        Assert.True(perRow < 1.0, $"scan allocated {allocated} bytes for {Rows} rows ({perRow:0.0000} per row)");
    }

    [Fact]
    public async Task A_scan_of_an_array_does_not_allocate_per_row_either()
    {
        var source = new PocoSourceBuilder("mem").AddTable("bars", Bars(Rows).ToArray()).Build();
        using var arena = new ExecutionArena();

        var (allocated, _) = await AllocationProbe.SteadyStateAsync(
            () => ScanAsync(source, arena));

        var perRow = (double)allocated / Rows;
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"BarRow[]: {allocated} bytes for {Rows} rows = {perRow:0.0000} bytes/row");
        Assert.True(perRow < 1.0, $"scan allocated {allocated} bytes for {Rows} rows ({perRow:0.0000} per row)");
    }

    /// <summary>
    /// ADR 0012, replacing ADR 0007's per-column writer slot: a scanned column keeps nothing. All the
    /// staging goes back to the arena when the scan ends, the arena's balance returns to zero, and
    /// disposing the arena releases even what it kept warm.
    /// </summary>
    [Fact]
    public async Task A_scanned_column_retains_nothing_after_the_scan()
    {
        var source = new PocoSourceBuilder("mem").AddTable("bars", Bars(1000)).Build();
        var arena = new ExecutionArena();

        var batches = await PocoTestSupport.ScanAsync(source, "bars", 256, arena: arena);
        Assert.True(arena.OutstandingBytes > 0, "the batches a scan yields are the arena's memory");
        PocoTestSupport.Dispose(batches);

        Assert.Equal(0, arena.OutstandingBytes);
        Assert.True(arena.RetainedBytes > 0, "the scan's staging should have come back to the arena");

        arena.Dispose();
        Assert.Equal(0, arena.RetainedBytes);

        // Structural, so that no later change quietly turns the pool back into a cache of scan data.
        // Since ADR 0020 §1 a column may keep the writer *object* between scans — that is what makes
        // a second scan of the same table cost nothing per execution — but the writer must have
        // released everything: no arena, no capacity, and therefore no array of anyone's.
        Assert.All(ReleasedWriters(source), writer =>
        {
            Assert.Equal(0, writer.Capacity);
            Assert.Throws<InvalidOperationException>(() => ArenaOf(writer));
        });
    }

    /// <summary>Every writer the source's columns are holding in their pools, as objects.</summary>
    private static IReadOnlyList<PocoChunkWriter<BarRow>> ReleasedWriters(PocoSource source)
    {
        const System.Reflection.BindingFlags All =
            System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.NonPublic;

        var tables = (System.Array)typeof(PocoSource)
            .GetField("_tables", All)!.GetValue(source)!;
        var writers = new List<PocoChunkWriter<BarRow>>();
        foreach (var table in tables)
        {
            var columns = (System.Collections.IEnumerable)table!.GetType()
                .GetProperty("Columns", All)!.GetValue(table)!;
            foreach (var column in columns)
            {
                if (column!.GetType().BaseType!.GetField("_spare", All)?.GetValue(column)
                    is PocoChunkWriter<BarRow> spare)
                {
                    writers.Add(spare);
                }
            }
        }

        return writers;
    }

    private static object ArenaOf(PocoChunkWriter<BarRow> writer)
    {
        try
        {
            return typeof(PocoChunkWriter<BarRow>)
                .GetProperty("Arena", System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(writer)!;
        }
        catch (System.Reflection.TargetInvocationException wrapped)
            when (wrapped.InnerException is not null)
        {
            throw wrapped.InnerException;
        }
    }

    private static async Task<long> ScanAsync(PocoSource source, ExecutionArena arena)
    {
        var stats = new ExecutionStats();
        var request = PocoTestSupport.Request(source, "bars", 4096);
        var context = PocoTestSupport.Context(stats, arena);

        await foreach (var batch in source.ScanAsync(request, context, TestContext.Current.CancellationToken))
        {
            Consume(batch);
            batch.Dispose();
        }

        return stats.RowsScanned;
    }

    /// <summary>Touches every column so the batch cannot be optimised away.</summary>
    private static void Consume(RecordBatch batch)
    {
        foreach (var array in batch.Arrays)
        {
            if (array.Length == 0)
            {
                throw new InvalidOperationException("empty batch");
            }
        }
    }

    private static List<BarRow> Bars(int count) => Enumerable
        .Range(0, count)
        .Select(i => new BarRow(
            new DateTime(2026, 1, 3, 9, 0, 0, DateTimeKind.Unspecified).AddMinutes(i),
            i % 2 == 0 ? "BTCUSDT" : "ETHUSDT",
            1000.5m + i,
            i % 4 == 0 ? null : i * 1.25,
            (Colour)(i % 3)))
        .ToList();
}

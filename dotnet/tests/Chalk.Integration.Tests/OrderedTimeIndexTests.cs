using System.Diagnostics;
using Chalk.Client;
using Chalk.Ir;
using Chalk.Sources.Poco;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// A log kept in time order, with an index on its time beside one on its key and time. The index on
/// time is backed by the table's own order, so it holds no positions, and a range on time seeks it.
/// An append used to read the key of every row in the table again for that index, which over a long
/// log cost each append milliseconds; it now reads only the appended rows.
/// </summary>
public sealed class OrderedTimeIndexTests(SharedSidecar sidecar, ITestOutputHelper output) : IClassFixture<SharedSidecar>
{
    private static readonly DateTime Day = new(2026, 10, 12);

    /// <summary>
    /// A range on time, given as parameters, seeks the index on time and reads only the rows in it,
    /// alone and under a join to the keys.
    /// </summary>
    [Theory]
    [InlineData("SELECT COUNT(*) FROM marks WHERE ts >= @from AND ts < @to")]
    [InlineData(
        "SELECT COUNT(*) FROM instrument i JOIN marks m ON m.instrument_id = i.instrument_id "
        + "WHERE m.ts >= @from AND m.ts < @to")]
    public async Task A_range_on_time_seeks_the_index_the_order_backs(string sql)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var (source, _) = Log(rows: 80_000);
        await using var engine = await CreateEngineAsync(source);

        var prepared = await engine.PrepareAsync(sql);
        Assert.True(PlanWalker.Has(prepared.Plan, Rel.KindOneofCase.IndexLookup), "the range seeks the index on time");

        // An hour of a log with eight marks a second.
        await using var execution = await engine.ExecuteAsync(
            prepared, (IReadOnlyList<object?>)[Day.AddHours(1), Day.AddHours(2)]);
        long count = 0;
        await foreach (var batch in execution.Batches)
        {
            count = ((Apache.Arrow.Int64Array)batch.Column(0)).GetValue(0)!.Value;
            batch.Dispose();
        }

        Assert.Equal(3_600 * 8, count);
        Assert.InRange(execution.Stats.RowsScanned, count, count + 8);
    }

    /// <summary>
    /// An append to a log of 1.6 million rows costs what one to a log of 1 600 does. Before, the index
    /// on time read every row's time at each append: 8 rows took 11.51 ms on the long log against
    /// 0.04 ms on the short one; now 0.07 ms against 0.02 ms.
    /// </summary>
    [Fact]
    public async Task An_append_reads_only_the_appended_rows_for_the_index_the_order_backs()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var longLog = await AppendMillisecondsAsync(rows: 1_600_000);
        var shortLog = await AppendMillisecondsAsync(rows: 1_600);

        output.WriteLine($"an append of 8 rows took {longLog:0.00} ms on 1.6 million rows, {shortLog:0.00} ms on 1 600");
        Assert.True(
            longLog <= (3 * shortLog) + 1,
            $"an append of 8 rows took {longLog:0.00} ms on 1.6 million rows against {shortLog:0.00} ms on 1 600");
    }

    /// <summary>The median of 40 appends of 8 rows each, after 10 to warm up, in milliseconds.</summary>
    private async Task<double> AppendMillisecondsAsync(int rows)
    {
        var (source, marks) = Log(rows);
        await using var engine = await CreateEngineAsync(source);
        var next = rows;
        var times = new List<double>();
        for (var step = 0; step < 50; step++)
        {
            Mark[] batch = [.. Enumerable.Range(next, 8).Select(At)];
            next += 8;
            var clock = Stopwatch.StartNew();
            await engine.RefreshAsync(r => r.Append(marks, batch), TestContext.Current.CancellationToken);
            if (step >= 10)
            {
                times.Add(clock.Elapsed.TotalMilliseconds);
            }
        }

        times.Sort();
        return times[times.Count / 2];
    }

    /// <summary>One mark every eighth of a second, dealt to the eight instruments in turn.</summary>
    private static Mark At(int i) => new(i % 8, Day.AddTicks(i * (TimeSpan.TicksPerSecond / 8)), i);

    private static (PocoSource Source, PocoTable<Mark> Marks) Log(int rows)
    {
        var source = new PocoSourceBuilder("mem")
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .AddTable("instrument", (IReadOnlyList<Instrument>)[.. Enumerable.Range(0, 8).Select(i => new Instrument(i))])
            .AddTable(
                "marks",
                (IReadOnlyList<Mark>)[.. Enumerable.Range(0, rows).Select(At)],
                out var marks,
                t => t.OrderedBy(m => m.Ts).Index(m => m.InstrumentId, m => m.Ts).Index(m => m.Ts))
            .Build();
        return (source, marks);
    }

    private async Task<ChalkEngine> CreateEngineAsync(PocoSource source) =>
        await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = $"ordered-time-{Guid.NewGuid():n}",
            Sources = [source],
            Planner = sidecar.CreatePlanner(),
        });

    public sealed record Instrument(int InstrumentId);

    public sealed record Mark(int InstrumentId, DateTime Ts, double Price);
}

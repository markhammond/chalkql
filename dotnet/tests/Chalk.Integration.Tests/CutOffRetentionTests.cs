#pragma warning disable CHALK003
using Chalk.Client;
using Chalk.Incremental;
using Chalk.Sources.Poco;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// A maintained filter whose cut-off rises with the clock lets go of the rows behind it. The clock moves
/// only forward, so a row behind a lower bound of the whole condition, such as <c>ts &gt;= @now -
/// INTERVAL '2' HOUR</c>, can never pass again: the filter holds the rows of its window, not the log. A
/// change behind the cut-off, appended late, changed or deleted, gives nothing. A bound that can pass a
/// row again, beside an OR or read from a parameter a view binds again, holds every row, as before.
/// </summary>
public sealed class CutOffRetentionTests(SharedSidecar sidecar, ITestOutputHelper output) : IClassFixture<SharedSidecar>
{
    private sealed record Mark(long Seq, int Instrument, DateTime Ts, long Px);

    private struct RowKey
    {
        public long Key;
    }

    private struct RowValue
    {
        public double V;
    }

    private static readonly DateTime Open = new(2026, 10, 20, 0, 0, 0);

    /// <summary>A day of marks, one every two seconds: 43,200 rows, of which two hours are 3,600.</summary>
    private const int Day = 43_200;

    /// <summary>
    /// Over a day of marks the two-hour window holds about a sixth of what the whole log does (1.7 MB against
    /// 9.6 MB), and gives the
    /// statement's rows through clock moves, appends at the end, late appends, and rows changed and
    /// deleted behind the cut-off and inside it.
    /// </summary>
    [Theory]
    [InlineData("WHERE ts >= @now - INTERVAL '2' HOUR", true)]
    [InlineData("WHERE ts >= CURRENT_TIMESTAMP - INTERVAL '2' HOUR AND px > 3", true)]
    [InlineData("WHERE (ts >= @now - INTERVAL '2' HOUR OR px > 98)", false)]
    public async Task A_cut_off_that_rises_with_the_clock_lets_go_of_the_rows_behind_it(string where, bool letsGo)
    {
        var sql = $"SELECT seq AS k, CAST(px AS DOUBLE) AS v FROM marks {where}";
        var (engine, marks) = await DeskAsync();
        await using var _ = engine;
        await using var view = await engine.MaterializeAsync(sql, Options(), Ct);
        var (replica, cursor) = Drained(view);
        Same(await StatementAsync(engine, sql), replica.Rows);
        var held = view.StateBytes;
        output.WriteLine($"{where}: {held:N0} B held");
        if (letsGo)
        {
            Assert.True(held < 3_000_000, $"{where}: {held:N0} B held, for two hours of a day");
        }
        else
        {
            Assert.True(held > 6_000_000, $"{where}: {held:N0} B held, though a row behind the cut-off can pass again");
        }

        var now = Open.AddDays(1);
        for (var step = 1; step <= 4; step++)
        {
            now = now.AddMinutes(20);
            var at = now;
            await engine.RefreshAsync(
                r => r.AdvanceClock(new DateTimeOffset(at, TimeSpan.Zero))
                    .Upsert(marks, [
                        .. Marks(Day + (step * 1_000), 600),                                   // the newest, at the end
                        .. Marks(5_000 + (step * 10), 5),                                       // late, behind the cut-off
                        .. Marks(Day - 200 + step, 3).Select(m => m with { Px = m.Px + 50 }),   // changed inside the window
                        .. Marks(2_000 + step, 3).Select(m => m with { Px = m.Px + 50 }),       // changed behind it
                    ])
                    .Delete(marks, [.. Marks(3_000 + step, 2), .. Marks(Day - 100 + step, 2)]),
                Ct);
            await view.UpdateAsync(Ct);
            view.Drain(ref cursor, replica);
            Same(await StatementAsync(engine, sql), replica.Rows);
        }

        output.WriteLine($"{where}: {view.StateBytes:N0} B held after the clock moved");
    }

    /// <summary>Marks from <paramref name="from"/> on, two seconds apart, four instruments in turn.</summary>
    private static Mark[] Marks(int from, int count) =>
        [.. Enumerable.Range(from, count).Select(i => new Mark(i, i % 4, Open.AddSeconds(2 * i), (i * 37) % 100))];

    private async Task<(ChalkEngine Engine, PocoTable<Mark> Marks)> DeskAsync()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var source = new PocoSourceBuilder("desk")
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .AddTable("marks", (IReadOnlyList<Mark>)Marks(0, Day), out var marks, t => t.Keyed(x => x.Seq))
            .Build();
        var engine = await ChalkEngine.CreateAsync(
            new ChalkEngineOptions { Sources = [source], Planner = sidecar.CreatePlanner(), Clock = "now" }, Ct);
        await engine.RefreshAsync(r => r.AdvanceClock(new DateTimeOffset(Open.AddDays(1), TimeSpan.Zero)), Ct);
        return (engine, marks);
    }

    private static ViewOptions<RowKey, RowValue> Options() => new()
    {
        Key = ["k"],
        LocalJoinMaxRows = StandingViews.DefaultLocalJoinMaxRows,
        Maintenance = ViewMaintenance.Incremental,
        Map = static (in ViewRow row, out RowKey key, out RowValue value) =>
        {
            key = new RowKey { Key = row.GetInt64(0) };
            value = new RowValue { V = row.GetDouble(1) };
        },
    };

    private static async Task<Dictionary<long, double>> StatementAsync(ChalkEngine engine, string sql)
    {
        var rows = new Dictionary<long, double>();
        await foreach (var batch in engine.QueryAsync(sql, new Dictionary<string, object?>(), ct: Ct))
        {
            using (batch)
            {
                for (var i = 0; i < batch.Length; i++)
                {
                    rows[Convert.ToInt64(Chalk.Arrow.RecordBatchExtensions.ValueAt(batch.Column(0), i))] =
                        Convert.ToDouble(Chalk.Arrow.RecordBatchExtensions.ValueAt(batch.Column(1), i));
                }
            }
        }

        return rows;
    }

    private static void Same(Dictionary<long, double> expected, Dictionary<long, double> actual)
    {
        Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
        foreach (var (key, value) in expected)
        {
            Assert.True(value.Equals(actual[key]), $"{key}: {value} against {actual[key]}");
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (Replica Sink, ViewCursor Cursor) Drained(StandingView<RowKey, RowValue> view)
    {
        var sink = new Replica();
        var cursor = ViewCursor.Start;
        Assert.Equal(DrainOutcome.Complete, view.Drain(ref cursor, sink));
        return (sink, cursor);
    }

    private sealed class Replica : IViewSink<RowKey, RowValue>
    {
        public Dictionary<long, double> Rows { get; } = [];

        public void Reset() => Rows.Clear();

        public void Upsert(in RowKey key, in RowValue row) => Rows[key.Key] = row.V;

        public void Delete(in RowKey key) => Rows.Remove(key.Key);

        public void Commit(long version, uint checksum)
        {
        }
    }
}

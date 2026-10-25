#pragma warning disable CHALK003
using Chalk.Client;
using Chalk.Incremental;
using Chalk.Sources.Poco;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// A refresh that moves only the clock: no table changes, yet a view whose condition reads the clock
/// must give the statement's rows at the new time. Rows leave a lower bound that rises with the clock,
/// and enter an upper bound that rises with it, such as <c>since &lt; @now - INTERVAL '1' SECOND</c>:
/// how long something has waited. Each view is held to a run of its statement after every move, and
/// the statement must give rows by the end, so that agreeing is not agreeing on nothing.
/// </summary>
public sealed class ClockOnlyRefreshTests(SharedSidecar sidecar) : IClassFixture<SharedSidecar>
{
    private sealed record Mark(long Seq, DateTime Ts, long Qty);

    // A far-future "never" needs microseconds: nanoseconds reach only 2262, and a later instant is refused.
    private sealed record Wait(
        long Seq, [property: ChalkColumn(Precision = 6)] DateTime Since, [property: ChalkColumn(Precision = 6)] DateTime Until);

    private struct RowKey
    {
        public long Key;
    }

    private struct RowValue
    {
        public double V;
    }

    private static readonly DateTime Open = new(2026, 10, 25, 9, 0, 0);

    private static readonly DateTime Never = new(9999, 12, 31);

    public static TheoryData<string, string> Conditions() => new()
    {
        { "rows enter an upper bound", "SELECT seq AS k, CAST(qty AS DOUBLE) AS v FROM marks WHERE ts < @now - INTERVAL '1' SECOND" },
        { "rows enter an inclusive upper bound", "SELECT seq AS k, CAST(qty AS DOUBLE) AS v FROM marks WHERE ts <= @now - INTERVAL '1' SECOND" },
        { "rows leave a lower bound", "SELECT seq AS k, CAST(qty AS DOUBLE) AS v FROM marks WHERE ts >= @now - INTERVAL '1' SECOND" },
        {
            "rows pass through a band", "SELECT seq AS k, CAST(qty AS DOUBLE) AS v FROM marks "
            + "WHERE ts < @now - INTERVAL '1' SECOND AND ts >= @now - INTERVAL '3' SECOND"
        },
        { "the clock on the column's side", "SELECT seq AS k, CAST(qty AS DOUBLE) AS v FROM marks WHERE ts + INTERVAL '1' SECOND < @now" },
        {
            "a wait past a bound, joined", "SELECT m.seq AS k, CAST(m.qty AS DOUBLE) AS v FROM marks AS m JOIN waits AS w ON w.seq = m.seq "
            + "WHERE w.since < @now - INTERVAL '1' SECOND AND w.since < w.until - INTERVAL '1' SECOND"
        },
        { "a count under an upper bound", "SELECT 1 AS k, CAST(COUNT(*) AS DOUBLE) AS v FROM marks WHERE ts < @now - INTERVAL '1' SECOND" },
    };

    /// <summary>
    /// Moved by the clock alone, past a row's bound, onto one, and far past every one, then changed: the view
    /// gives the statement's rows each time, incrementally kept as recomputed.
    /// </summary>
    [Theory]
    [MemberData(nameof(Conditions))]
    public async Task A_view_reading_the_clock_follows_it_through_refreshes_that_move_only_the_clock(string what, string sql)
    {
        _ = what;
        foreach (var maintenance in new[] { ViewMaintenance.Incremental, ViewMaintenance.Recompute })
        {
            var (engine, marks, waits) = await DeskAsync();
            await using var _ = engine;
            await using var view = await engine.MaterializeAsync(sql, Options(maintenance), Ct);
            var replica = new Replica();
            var cursor = ViewCursor.Start;
            view.Drain(ref cursor, replica);
            Same(maintenance, Open, await StatementAsync(engine, sql), replica.Rows);

            // Half a second on, onto a row's bound to the microsecond, a microsecond past it, and far past all.
            foreach (var at in new[] { 0.5, 1.5, 2.0, 2.000001, 3.25, 9.0 })
            {
                var now = Open.AddSeconds(at);
                await engine.RefreshAsync(r => r.AdvanceClock(new DateTimeOffset(now, TimeSpan.Zero)), Ct);
                await view.UpdateAsync(Ct);
                view.Drain(ref cursor, replica);
                Same(maintenance, now, await StatementAsync(engine, sql), replica.Rows);
            }

            // A change after the clock-only moves, a mark either side of the bound: the view still agrees.
            var later = Open.AddSeconds(10);
            await engine.RefreshAsync(
                r => r.AdvanceClock(new DateTimeOffset(later, TimeSpan.Zero))
                    .Upsert(marks, [new Mark(100, later.AddSeconds(-1.5), 100), new Mark(101, later.AddSeconds(-0.5), 101)])
                    .Upsert(waits, [new Wait(100, later.AddSeconds(-1.5), Never), new Wait(101, later.AddSeconds(-0.5), Never)]),
                Ct);
            await view.UpdateAsync(Ct);
            view.Drain(ref cursor, replica);
            var end = await StatementAsync(engine, sql);
            Assert.True(end.Count > 0, $"{maintenance}: the statement gives no rows, so the view's agreeing proves nothing");
            Same(maintenance, later, end, replica.Rows);
        }
    }

    /// <summary>Ten marks half a second apart from the opening, each waiting since its time, for good.</summary>
    private async Task<(ChalkEngine Engine, PocoTable<Mark> Marks, PocoTable<Wait> Waits)> DeskAsync()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        Mark[] marks = [.. Enumerable.Range(0, 10).Select(i => new Mark(i, Open.AddSeconds(i * 0.5), i))];
        var source = new PocoSourceBuilder("desk")
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .AddTable("marks", (IReadOnlyList<Mark>)marks, out var table, t => t.Keyed(x => x.Seq))
            .AddTable("waits", (IReadOnlyList<Wait>)[.. marks.Select(m => new Wait(m.Seq, m.Ts, Never))], out var waits, t => t.Keyed(x => x.Seq))
            .Build();
        var engine = await ChalkEngine.CreateAsync(
            new ChalkEngineOptions { Sources = [source], Planner = sidecar.CreatePlanner(), Clock = "now" }, Ct);
        await engine.RefreshAsync(r => r.AdvanceClock(new DateTimeOffset(Open, TimeSpan.Zero)), Ct);
        return (engine, table, waits);
    }

    private static ViewOptions<RowKey, RowValue> Options(ViewMaintenance maintenance, double? resetWhen = null, long? budget = null) => new()
    {
        ResetWhen = resetWhen ?? 1,
        StateBudget = budget ?? long.MaxValue,
        Key = ["k"],
        LocalJoinMaxRows = StandingViews.DefaultLocalJoinMaxRows,
        Maintenance = maintenance,
        Map = static (in ViewRow row, out RowKey key, out RowValue value) =>
        {
            key = new RowKey { Key = row.GetInt64(0) };
            value = new RowValue { V = row.GetDouble(1) };
        },
    };

    /// <summary>The statement's rows now: a plain query reads the engine's clock, as the view does.</summary>
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

    private static void Same(ViewMaintenance maintenance, DateTime at, Dictionary<long, double> expected, Dictionary<long, double> actual)
    {
        var said = $"{maintenance} at {at:HH:mm:ss.ffffff}";
        Assert.True(
            expected.Keys.Order().SequenceEqual(actual.Keys.Order()),
            $"{said}: the statement gives [{string.Join(",", expected.Keys.Order())}], the view [{string.Join(",", actual.Keys.Order())}]");
        foreach (var (key, value) in expected)
        {
            Assert.True(value.Equals(actual[key]), $"{said}: {key} is {value}, the view has {actual[key]}");
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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

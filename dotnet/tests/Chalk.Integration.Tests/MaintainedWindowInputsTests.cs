#pragma warning disable CHALK003
using Chalk.Client;
using Chalk.Incremental;
using Chalk.Ir;
using Chalk.Sources.Poco;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// What a maintained window's input may be. An index lookup over a range of keys, which a planner writes
/// for a range on an indexed column or for an index read in its order, is kept as a filter over the table:
/// a bound that reads the clock follows the clock, as a cut-off does. And a unique key is carried through
/// joins, so a window that a tie would decide is admitted over a join that keeps its order's key: an
/// equi-join with a side unique on its keys, a join with one row, and a semi join.
/// </summary>
public sealed class MaintainedWindowInputsTests(SharedSidecar sidecar, ITestOutputHelper output) : IClassFixture<SharedSidecar>
{
    private sealed record Position(long Seq, int Account, int Channel, DateTime UpdateTime, long Qty);

    private sealed record Book(int Account, string Name);

    private struct RowKey
    {
        public long Key;
    }

    private struct RowValue
    {
        public double V;
    }

    private static readonly DateTime Open = new(2026, 10, 20, 9, 0, 0);

    public static TheoryData<string, string, bool> Statements() => new()
    {
        {
            "a range on an indexed time",
            "SELECT seq AS k, CAST(qty AS DOUBLE) AS v FROM positions WHERE update_time >= TIMESTAMP '2026-10-20 10:00:00' AND update_time < TIMESTAMP '2026-10-20 11:00:00'",
            true
        },
        {
            "a clock cut-off on an indexed time",
            "SELECT seq AS k, CAST(qty AS DOUBLE) AS v FROM positions WHERE update_time >= CURRENT_TIMESTAMP - INTERVAL '2' HOUR AND update_time < CURRENT_TIMESTAMP + INTERVAL '1' HOUR",
            true
        },
        {
            "a lag over an index's own order (W1)",
            "SELECT seq AS k, CAST(qty - LAG(qty, 1, 0) OVER (PARTITION BY account, channel ORDER BY update_time) AS DOUBLE) AS v FROM positions",
            false
        },
        {
            "a lag over a left join to a table unique on its key",
            "SELECT p.seq AS k, CAST(p.qty - LAG(p.qty, 1, 0) OVER (PARTITION BY p.account ORDER BY p.seq) AS DOUBLE) AS v "
                + "FROM positions p LEFT JOIN books b ON b.account = p.account",
            false
        },
        {
            "a lag over a semi join",
            "SELECT seq AS k, CAST(qty - LAG(qty, 1, 0) OVER (PARTITION BY account ORDER BY seq) AS DOUBLE) AS v "
                + "FROM positions WHERE account IN (SELECT account FROM books WHERE name <> 'house')",
            false
        },
        {
            "a lag over a join with a total",
            "SELECT p.seq AS k, CAST(p.qty - LAG(p.qty, 1, 0) OVER (PARTITION BY p.account ORDER BY p.seq) + t.n AS DOUBLE) AS v "
                + "FROM positions p CROSS JOIN (SELECT COUNT(*) AS n FROM books) t",
            false
        },
    };

    /// <summary>
    /// Each statement is maintained and gives a run's rows through appends, changes and deletes in the past,
    /// and the clock moving an hour; where it says so, its plan reads the table through an index range.
    /// </summary>
    [Theory]
    [MemberData(nameof(Statements))]
    public async Task A_window_input_is_maintained(string what, string sql, bool ranged)
    {
        var (engine, positions) = await DeskAsync();
        await using var _ = engine;
        if (ranged)
        {
            var prepared = await engine.PrepareAsync(sql, ct: Ct);
            Assert.True(PlanWalker.Has(prepared.Plan, Rel.KindOneofCase.IndexLookup), $"{what}: the plan reads an index range");
        }

        await using var view = await engine.MaterializeAsync(sql, Options(), Ct);
        Assert.Equal(ViewMaintenance.Incremental, view.Maintenance);
        var (replica, cursor) = Drained(view);
        Same(await StatementAsync(engine, sql), replica.Rows);

        await engine.RefreshAsync(r => r.Upsert(positions, Positions(2_000, 200)), Ct);
        await Check(engine, view, sql, replica, cursor);
        await engine.RefreshAsync(
            r => r.Upsert(positions, [.. Positions(400, 50).Select(p => p with { Qty = p.Qty + 5 })]).Delete(positions, Positions(800, 40)),
            Ct);
        await Check(engine, view, sql, replica, cursor);
        await engine.RefreshAsync(r => r.AdvanceClock(new DateTimeOffset(Open.AddHours(13), TimeSpan.Zero)), Ct);
        await Check(engine, view, sql, replica, cursor);
        output.WriteLine($"{what}: maintained, {view.StateBytes:N0} B held");
    }

    private static async Task Check(
        ChalkEngine engine, StandingView<RowKey, RowValue> view, string sql, Replica replica, ViewCursor cursor)
    {
        await view.UpdateAsync(Ct);
        view.Drain(ref cursor, replica);
        Same(await StatementAsync(engine, sql), replica.Rows);
    }

    /// <summary>Positions a quarter of a minute apart, dealt to four accounts of two channels, unique on (account, channel, time).</summary>
    private static Position[] Positions(int from, int count) =>
        [.. Enumerable.Range(from, count).Select(i => new Position(i, i % 4, (i / 4) % 2, Open.AddSeconds(15 * i), (i * 37) % 11))];

    private async Task<(ChalkEngine Engine, PocoTable<Position> Positions)> DeskAsync()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var source = new PocoSourceBuilder("desk")
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .AddTable(
                "positions",
                (IReadOnlyList<Position>)Positions(0, 2_000),
                out var positions,
                t => t.Keyed(x => x.Seq).UniqueIndex(x => x.Account, x => x.Channel, x => x.UpdateTime).Index(x => x.UpdateTime))
            .AddTable(
                "books",
                (IReadOnlyList<Book>)[new Book(0, "house"), new Book(1, "a"), new Book(2, "b"), new Book(3, "c")],
                t => t.UniqueKey(x => x.Account))
            .Build();
        var engine = await ChalkEngine.CreateAsync(
            new ChalkEngineOptions { Sources = [source], Planner = sidecar.CreatePlanner(), Clock = "now" }, Ct);
        await engine.RefreshAsync(r => r.AdvanceClock(new DateTimeOffset(Open.AddHours(12), TimeSpan.Zero)), Ct);
        return (engine, positions);
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

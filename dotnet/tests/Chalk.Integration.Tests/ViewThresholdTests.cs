#pragma warning disable CHALK003
using Chalk.Client;
using Chalk.Incremental;
using Chalk.Sources.Poco;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// Two ways a view's threshold moves without a view operation for rebinding a parameter. Remade from
/// the statement the host prepared, with the new value bound, the view asks the planner for nothing.
/// Held as the one row of a keyed table and cross-joined, the threshold is data: a move is an
/// ordinary update, which asks the planner for nothing and sends the host only the rows whose flag
/// flipped. Both are held to a run of the statement with the parameter bound.
/// </summary>
public sealed class ViewThresholdTests(SharedSidecar sidecar) : IClassFixture<SharedSidecar>
{
    private sealed record Mark(long Seq, DateTime Ts, double Price);

    private sealed record Threshold(int Id, double Level);

    private struct PointKey
    {
        public long Bucket;
    }

    private struct PointRow
    {
        public double Equity;
        public long Breached;
    }

    private static readonly DateTime Open = new(2026, 10, 13, 9, 0, 0);

    private const string ByParameter =
        "SELECT seq / 100 AS bucket, SUM(price) AS equity, CASE WHEN SUM(price) < @level THEN 1 ELSE 0 END AS breached "
        + "FROM marks GROUP BY seq / 100";

    private const string ByData =
        "SELECT g.bucket, g.equity, CASE WHEN g.equity < t.level THEN 1 ELSE 0 END AS breached "
        + "FROM (SELECT seq / 100 AS bucket, SUM(price) AS equity FROM marks GROUP BY seq / 100) g "
        + "CROSS JOIN thresholds t";

    /// <summary>A view remade from the host's prepared statement with a new value asks the planner for nothing.</summary>
    [Theory]
    [InlineData(ViewMaintenance.Incremental)]
    [InlineData(ViewMaintenance.Recompute)]
    public async Task A_view_remade_with_a_new_value_plans_nothing(ViewMaintenance maintenance)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var (engine, planner, marks, _) = await DeskAsync();
        await using var _engine = engine;
        var prepared = await engine.PrepareAsync(ByParameter);
        var plans = planner.Plans;

        await using var first = await engine.MaterializeAsync(prepared, Options(maintenance, Level(5_500)), Ct);
        await engine.RefreshAsync(r => r.Append(marks, Marks(20_000, 50)), Ct);
        await first.UpdateAsync(Ct);
        await using var second = await engine.MaterializeAsync(prepared, Options(maintenance, Level(5_520)), Ct);

        Assert.Equal(plans, planner.Plans);
        Same(await StatementAsync(engine, 5_500), Drained(first).Sink.Rows);
        Same(await StatementAsync(engine, 5_520), Drained(second).Sink.Rows);
    }

    /// <summary>
    /// A threshold held as data moves by an update: no plan, and the host is sent each point whose flag
    /// flipped and no other.
    /// </summary>
    [Theory]
    [InlineData(ViewMaintenance.Incremental)]
    [InlineData(ViewMaintenance.Recompute)]
    public async Task A_threshold_held_as_data_moves_by_an_update(ViewMaintenance maintenance)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var (engine, planner, marks, thresholds) = await DeskAsync();
        await using var _engine = engine;
        await using var view = await engine.MaterializeAsync(ByData, Options(maintenance, null), Ct);
        var (replica, cursor) = Drained(view);
        var before = replica.Rows.ToDictionary();
        var plans = planner.Plans;

        await engine.RefreshAsync(r => r.Upsert(thresholds, (IReadOnlyList<Threshold>)[new Threshold(1, 5_520)]), Ct);
        await view.UpdateAsync(Ct);
        replica.Upserted.Clear();
        view.Drain(ref cursor, replica);

        Assert.Equal(plans, planner.Plans);
        var expected = await StatementAsync(engine, 5_520);
        Same(expected, replica.Rows);
        var flipped = expected.Where(p => p.Value.Breached != before[p.Key].Breached).Select(p => p.Key).Order();
        Assert.NotEmpty(flipped);
        Assert.Equal(flipped, replica.Upserted.Order());

        await engine.RefreshAsync(r => r.Append(marks, Marks(20_000, 50)), Ct);
        await view.UpdateAsync(Ct);
        view.Drain(ref cursor, replica);
        Same(await StatementAsync(engine, 5_520), replica.Rows);
    }

    private static IReadOnlyList<Mark> Marks(int from, int count) =>
        [.. Enumerable.Range(from, count).Select(i => new Mark(i, Open.AddMilliseconds(i * 125), 50 + ((i * 7919) % 101) / 10.0))];

    private static IReadOnlyDictionary<string, object?> Level(double value) => new Dictionary<string, object?> { ["level"] = value };

    private async Task<(ChalkEngine Engine, CountingPlanner Planner, PocoTable<Mark> Marks, PocoTable<Threshold> Thresholds)> DeskAsync()
    {
        var planner = new CountingPlanner(sidecar.CreatePlanner());
        var source = new PocoSourceBuilder("desk")
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .AddTable("marks", Marks(0, 20_000), out var marks, t => t.AppendOnly(x => x.Ts))
            .AddTable("thresholds", (IReadOnlyList<Threshold>)[new Threshold(1, 5_500)], out var thresholds, t => t.Keyed(x => x.Id))
            .Build();
        var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions { Sources = [source], Planner = planner }, Ct);
        return (engine, planner, marks, thresholds);
    }

    private static void Same(Dictionary<long, PointRow> expected, Dictionary<long, PointRow> actual)
    {
        Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
        foreach (var (key, row) in expected)
        {
            Assert.True(Math.Abs(row.Equity - actual[key].Equity) < 1e-6, $"{key}: {row.Equity} against {actual[key].Equity}");
            Assert.Equal(row.Breached, actual[key].Breached);
        }
    }

    private static async Task<Dictionary<long, PointRow>> StatementAsync(ChalkEngine engine, double level)
    {
        var rows = new Dictionary<long, PointRow>();
        await foreach (var batch in engine.QueryAsync(ByParameter, Level(level), ct: Ct))
        {
            using (batch)
            {
                for (var i = 0; i < batch.Length; i++)
                {
                    rows[Convert.ToInt64(Chalk.Arrow.RecordBatchExtensions.ValueAt(batch.Column(0), i))] = new PointRow
                    {
                        Equity = Convert.ToDouble(Chalk.Arrow.RecordBatchExtensions.ValueAt(batch.Column(1), i)),
                        Breached = Convert.ToInt64(Chalk.Arrow.RecordBatchExtensions.ValueAt(batch.Column(2), i)),
                    };
                }
            }
        }

        return rows;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ViewOptions<PointKey, PointRow> Options(ViewMaintenance maintenance, object? parameters) => new()
    {
        Key = ["bucket"],
        Parameters = parameters,
        LocalJoinMaxRows = StandingViews.DefaultLocalJoinMaxRows,
        Maintenance = maintenance,
        Exactness = ViewExactness.EqualUpToRounding,
        Map = static (in ViewRow row, out PointKey key, out PointRow value) =>
        {
            key = new PointKey { Bucket = row.GetInt64(0) };
            value = new PointRow { Equity = row.GetDouble(1), Breached = row.GetInt64(2) };
        },
    };

    private static (Replica Sink, ViewCursor Cursor) Drained(StandingView<PointKey, PointRow> view)
    {
        var sink = new Replica();
        var cursor = ViewCursor.Start;
        Assert.Equal(DrainOutcome.Complete, view.Drain(ref cursor, sink));
        return (sink, cursor);
    }

    private sealed class Replica : IViewSink<PointKey, PointRow>
    {
        public Dictionary<long, PointRow> Rows { get; } = [];

        public List<long> Upserted { get; } = [];

        public void Reset() => Rows.Clear();

        public void Upsert(in PointKey key, in PointRow row)
        {
            Upserted.Add(key.Bucket);
            Rows[key.Bucket] = row;
        }

        public void Delete(in PointKey key) => Rows.Remove(key.Bucket);

        public void Commit(long version, uint checksum)
        {
        }
    }

    private sealed class CountingPlanner(IQueryPlanner inner) : IQueryPlanner
    {
        private int _plans;

        internal int Plans => Volatile.Read(ref _plans);

        public ValueTask<PlannerInfo> GetInfoAsync(CancellationToken ct = default) => inner.GetInfoAsync(ct);

        public bool AcceptsCatalogDeltas => inner.AcceptsCatalogDeltas;

        public ValueTask RegisterCatalogAsync(CatalogRegistration registration, CancellationToken ct = default) =>
            inner.RegisterCatalogAsync(registration, ct);

        public ValueTask RegisterStatisticsAsync(StatisticsRegistration statistics, CancellationToken ct = default) =>
            inner.RegisterStatisticsAsync(statistics, ct);

        public ValueTask<PlanResult> PlanAsync(PlanRequest request, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _plans);
            return inner.PlanAsync(request, ct);
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}

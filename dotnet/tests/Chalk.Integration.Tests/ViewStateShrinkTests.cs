#pragma warning disable CHALK003
using Chalk.Client;
using Chalk.Incremental;
using Chalk.Sources.Poco;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// A maintained view's states keep the room their fullest moment needed, so a view whose inputs shrink —
/// a log compacted, a window passed — would hold memory for rows it no longer has. Once its states hold a
/// quarter of the rows they have room for or fewer, the view builds its statement afresh, as it moves to a
/// new plan: at its next update, or beside it under <c>LowJitter</c>. A view that lived through a log's
/// compaction then holds about what a fresh view over the same rows holds, with the same rows.
/// </summary>
[Collection(SidecarCollection.Name)]
public sealed class ViewStateShrinkTests(SharedSidecar sidecar)
{
    private sealed record Mark(long Seq, long Inst, DateTime Ts, double Px);

    private struct K { public long Inst; }

    private struct V { public double Px; }

    /// <summary>Each instrument's latest mark within the last two hours, by a join back to its latest time.</summary>
    private const string Latest =
        "SELECT m.inst, m.px FROM marks m JOIN (SELECT inst, MAX(ts) AS ts FROM marks "
        + "WHERE ts >= CURRENT_TIMESTAMP - INTERVAL '2' HOUR GROUP BY inst) l "
        + "ON m.inst = l.inst AND m.ts = l.ts WHERE m.ts >= CURRENT_TIMESTAMP - INTERVAL '2' HOUR";

    private const int Instruments = 20;

    /// <summary>
    /// Three hours of marks, a view over them, then a compaction to each half minute's last mark and a few
    /// hundred ticks of appends with compactions every 15: the view's state comes down to within twice a
    /// fresh view's, from about twenty times it before, and it holds the fresh view's rows.
    /// </summary>
    [Theory]
    [InlineData(ViewPlanSwap.AtNextUpdate)]
    [InlineData(ViewPlanSwap.LowJitter)]
    public async Task A_view_whose_log_was_compacted_holds_what_a_fresh_view_holds(ViewPlanSwap swap)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var ct = TestContext.Current.CancellationToken;
        var start = new DateTime(2026, 10, 22);
        var rows = new List<Mark>();
        var seq = 0L;
        var now = start;
        for (var t = 0; t < 3 * 3600; t += 2)
        {
            now = start.AddSeconds(t);
            for (var i = 0; i < Instruments; i++)
            {
                rows.Add(new Mark(seq++, i, now, seq % 1000 / 10.0));
            }
        }

        var source = new PocoSourceBuilder("desk").NamingPolicy(PocoNamingPolicy.SnakeCase)
            .AddTable("marks", (IReadOnlyList<Mark>)rows.ToList(), out var marks).Build();
        await using var engine = await ChalkEngine.CreateAsync(
            new ChalkEngineOptions { Sources = [source], Planner = sidecar.CreatePlanner(), Clock = "now" }, ct);
        await engine.RefreshAsync(r => r.AdvanceClock(new DateTimeOffset(now, TimeSpan.Zero)), ct);
        await using var view = await MakeAsync(engine, swap, ct);
        var made = view.StateBytes;

        for (var tick = 1; tick <= 300; tick++)
        {
            now = now.AddSeconds(2);
            var appended = Enumerable.Range(0, Instruments).Select(i => new Mark(seq++, i, now, seq % 1000 / 10.0)).ToArray();
            rows.AddRange(appended);
            var at = new DateTimeOffset(now, TimeSpan.Zero);
            if (tick % 15 == 1)
            {
                // The same marks, thinned: each half minute's last within 2 h 10 min, hourly before that.
                var cut = now.AddMinutes(-130);
                rows = [.. rows.Where(r => r.Ts >= cut ? r.Ts.Second % 30 >= 28 || r.Ts >= now.AddSeconds(-30) : r.Ts is { Minute: 0, Second: 0 })];
                var kept = rows.ToList();
                await engine.RefreshAsync(r => r.Replace(marks, kept).AdvanceClock(at), ct);
            }
            else
            {
                await engine.RefreshAsync(r => r.Append(marks, appended).AdvanceClock(at), ct);
            }

            await view.UpdateAsync(ct);
        }

        await using var fresh = await MakeAsync(engine, swap, ct);
        Assert.True(made > 10 * fresh.StateBytes, $"the view was made over the long log: {made} bytes against {fresh.StateBytes}");
        Assert.True(view.StateBytes <= 2 * fresh.StateBytes, $"the view holds {view.StateBytes} bytes; a fresh view {fresh.StateBytes}");
        Assert.Equal(Rows(fresh), Rows(view));
    }

    private static async Task<StandingView<K, V>> MakeAsync(ChalkEngine engine, ViewPlanSwap swap, CancellationToken ct) =>
        await engine.MaterializeAsync(Latest, new ViewOptions<K, V>
        {
            Key = ["inst"],
            LocalJoinMaxRows = StandingViews.DefaultLocalJoinMaxRows,
            Maintenance = ViewMaintenance.Incremental,
            PlanSwap = swap,
            StateBudget = 1L << 30,
            Map = static (in ViewRow row, out K key, out V value) =>
            {
                key = new K { Inst = row.GetInt64(0) };
                value = new V { Px = row.GetDouble(1) };
            },
        }, ct);

    private static SortedDictionary<long, double> Rows(StandingView<K, V> view)
    {
        var sink = new Sink();
        var cursor = ViewCursor.Start;
        view.Drain(ref cursor, sink);
        return sink.Rows;
    }

    private sealed class Sink : IViewSink<K, V>
    {
        public SortedDictionary<long, double> Rows { get; } = [];

        public void Reset() => Rows.Clear();

        public void Upsert(in K key, in V row) => Rows[key.Inst] = row.Px;

        public void Delete(in K key) => Rows.Remove(key.Inst);

        public void Commit(long version, uint checksum)
        {
        }
    }
}

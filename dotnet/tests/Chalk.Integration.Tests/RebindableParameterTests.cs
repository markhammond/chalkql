#pragma warning disable CHALK003
using Chalk.Client;
using Chalk.Incremental;
using Chalk.Sources.Poco;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// A standing view binds again the parameters it declares rebindable (design 77 §5), and keeps its plan.
/// Kept incrementally, each operator that reads one holds what it reads and runs again over it when one
/// moves, as one that reads the clock does when the clock moves, wherever the parameter is read: in a
/// projection or a filter over the groups, in a filter on the log, as a cut-off on time, or as the one
/// row of a <c>WITH</c>, which the planner reads as the parameter itself (D348). A list keeps its length,
/// or binds into a declared capacity, padded with its first element. Every answer is held to a run of
/// the statement with the new values, a rebind asks the planner for nothing, and a maintained view's
/// readers are sent only the keys whose rows changed.
/// </summary>
public sealed class RebindableParameterTests(SharedSidecar sidecar, ITestOutputHelper output) : IClassFixture<SharedSidecar>
{
    private sealed record Mark(long Seq, int Instrument, DateTime Ts, double Price);

    private struct PointKey
    {
        public long Bucket;
    }

    private struct PointRow
    {
        public double Equity;
        public long N;
    }

    private static readonly DateTime Open = new(2026, 10, 20, 9, 0, 0);

    private const string Groups = "SELECT seq / 100 AS bucket, SUM(price) AS equity, ";

    public static TheoryData<string, string, object, object> Placements() => new()
    {
        {
            "a CASE over the groups",
            Groups + "CASE WHEN SUM(price) < @level THEN 1 ELSE 0 END AS n FROM marks GROUP BY seq / 100",
            Values(("level", 5_500.0)),
            Values(("level", 5_520.0))
        },
        {
            "the one row of a WITH, read in a CASE",
            "WITH t AS (SELECT CAST(@level AS DOUBLE) AS level) SELECT g.bucket, g.equity, "
                + "CASE WHEN g.equity < t.level THEN 1 ELSE 0 END AS n FROM "
                + "(SELECT seq / 100 AS bucket, SUM(price) AS equity FROM marks GROUP BY seq / 100) g CROSS JOIN t",
            Values(("level", 5_500.0)),
            Values(("level", 5_520.0))
        },
        {
            "two parameters in one WITH row",
            "WITH t AS (SELECT CAST(@level AS DOUBLE) AS level, CAST(@floor AS DOUBLE) AS lo) SELECT g.bucket, g.equity, "
                + "CASE WHEN g.equity < t.level AND g.equity > t.lo THEN 1 ELSE 0 END AS n FROM "
                + "(SELECT seq / 100 AS bucket, SUM(price) AS equity FROM marks GROUP BY seq / 100) g CROSS JOIN t",
            Values(("level", 5_520.0), ("floor", 5_480.0)),
            Values(("level", 5_530.0), ("floor", 5_500.0))
        },
        {
            "a filter over the groups",
            Groups + "COUNT(*) AS n FROM marks GROUP BY seq / 100 HAVING SUM(price) < @level",
            Values(("level", 5_500.0)),
            Values(("level", 5_520.0))
        },
        {
            "a filter on the log",
            Groups + "COUNT(*) AS n FROM marks WHERE price < @level GROUP BY seq / 100",
            Values(("level", 55.0)),
            Values(("level", 57.5))
        },
        {
            "a cut-off on time",
            Groups + "COUNT(*) AS n FROM marks WHERE ts >= @from GROUP BY seq / 100",
            Values(("from", Open.AddMinutes(10))),
            Values(("from", Open.AddMinutes(25)))
        },
        {
            "a list of the same length",
            Groups + "COUNT(*) AS n FROM marks WHERE instrument IN @picks GROUP BY seq / 100",
            Values(("picks", new[] { 1, 2 })),
            Values(("picks", new[] { 3, 5 }))
        },
    };

    /// <summary>
    /// Wherever the parameter is read, a rebind gives the statement's rows with the new values, asks the
    /// planner for nothing, and, kept incrementally, sends the keys whose rows changed and no others. An
    /// append after it is kept with the new values.
    /// </summary>
    [Theory]
    [MemberData(nameof(Placements))]
    public async Task A_rebind_gives_the_statements_rows_with_the_new_values(string where, string sql, object first, object then)
    {
        foreach (var maintenance in new[] { ViewMaintenance.Incremental, ViewMaintenance.Recompute })
        {
            await RebindsAsync(where, sql, first, then, maintenance, Declared(first));
        }
    }

    /// <summary>
    /// A list with a capacity binds a list of any length up to it into the same plan, padded with its first
    /// element, under <c>IN</c> and under <c>NOT IN</c>, which a repeated element changes neither of.
    /// </summary>
    [Theory]
    [InlineData("IN")]
    [InlineData("NOT IN")]
    public async Task A_list_with_a_capacity_binds_any_length_up_to_it(string membership)
    {
        var sql = Groups + $"COUNT(*) AS n FROM marks WHERE instrument {membership} @picks GROUP BY seq / 100";
        RebindableParameter[] declared = [new("picks", Capacity: 4)];
        foreach (var maintenance in new[] { ViewMaintenance.Incremental, ViewMaintenance.Recompute })
        {
            var desk = await DeskAsync();
            await using var _ = desk.Engine;
            await using var view = await desk.Engine.MaterializeAsync(sql, Options(maintenance, Values(("picks", new[] { 1 })), declared), Ct);
            var (replica, cursor) = Drained(view);
            foreach (var picks in new[] { new[] { 2, 3, 5 }, new[] { 7 }, new[] { 0, 1, 2, 3 } })
            {
                var plans = desk.Planner.Plans;
                await view.RebindAsync(Values(("picks", picks)), Ct);
                view.Drain(ref cursor, replica);
                Assert.Equal(plans, desk.Planner.Plans);
                Same(await StatementAsync(desk.Engine, sql, Values(("picks", picks))), replica.Rows);
            }
        }
    }

    /// <summary>
    /// Kept incrementally, a parameter that a <c>LIMIT</c> reads, or that bounds an index seek, is fixed when
    /// the maintained plan starts, and is refused when the view is made, by name. Recomputed, the same
    /// <c>LIMIT</c> rebinds.
    /// </summary>
    [Fact]
    public async Task A_parameter_fixed_when_the_maintained_plan_starts_is_refused()
    {
        var desk = await DeskAsync();
        await using var _ = desk.Engine;
        const string limit = "SELECT seq AS bucket, price AS equity, CAST(instrument AS BIGINT) AS n FROM marks ORDER BY ts DESC, seq DESC LIMIT @n";
        var refused = await Assert.ThrowsAsync<StandingViewException>(() => desk.Engine.MaterializeAsync(
            limit, Options(ViewMaintenance.Incremental, Values(("n", 2)), [new("n")]), Ct).AsTask());
        Assert.Contains("@n is rebindable, and the statement's LIMIT reads it", refused.Message, StringComparison.Ordinal);

        const string seek = Groups + "COUNT(*) AS n FROM indexed WHERE seq = @seq GROUP BY seq / 100";
        var sought = await Assert.ThrowsAsync<StandingViewException>(() => desk.Engine.MaterializeAsync(
            seek, Options(ViewMaintenance.Incremental, Values(("seq", 5L)), [new("seq")]), Ct).AsTask());
        Assert.Contains("@seq is rebindable, and it bounds the seek of index", sought.Message, StringComparison.Ordinal);

        await using var recomputed = await desk.Engine.MaterializeAsync(
            limit, Options(ViewMaintenance.Recompute, Values(("n", 2)), [new("n")]), Ct);
        var (replica, cursor) = Drained(recomputed);
        Assert.Equal(2, replica.Rows.Count);
        await recomputed.RebindAsync(Values(("n", 3)), Ct);
        recomputed.Drain(ref cursor, replica);
        Assert.Equal(3, replica.Rows.Count);
    }

    /// <summary>A declaration that cannot hold is refused when the view is made, naming what is wrong.</summary>
    [Theory]
    [InlineData("level", null, "has no such parameter")]
    [InlineData("now", null, "is the engine's clock")]
    [InlineData("picks", 0, "one or more")]
    [InlineData("floor", 4, "takes no list")]
    [InlineData("picks", 1, "beyond its capacity of 1")]
    public async Task A_declaration_that_cannot_hold_is_refused(string name, int? capacity, string reason)
    {
        var desk = await DeskAsync();
        await using var _ = desk.Engine;
        const string sql = Groups + "COUNT(*) AS n FROM marks WHERE instrument IN @picks AND price > @floor GROUP BY seq / 100";
        var refusal = await Assert.ThrowsAnyAsync<ArgumentException>(() => desk.Engine.MaterializeAsync(
            sql, Options(ViewMaintenance.Incremental, Values(("picks", new[] { 1, 2 }), ("floor", 0.0)), [new(name, capacity)]), Ct).AsTask());
        Assert.Contains(reason, refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>A statement whose parameters have no names cannot name them rebindable.</summary>
    [Fact]
    public async Task Positional_parameters_cannot_be_declared_rebindable()
    {
        var desk = await DeskAsync();
        await using var _ = desk.Engine;
        var refusal = await Assert.ThrowsAsync<ArgumentException>(() => desk.Engine.MaterializeAsync(
            Groups + "COUNT(*) AS n FROM marks WHERE price < ? GROUP BY seq / 100",
            Options(ViewMaintenance.Incremental, new object?[] { 55.0 }, [new("level")]), Ct).AsTask());
        Assert.Contains("have no names: write them as @name", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A rebind the view cannot take is refused before anything moves, and the view keeps its values: a
    /// name it did not declare, a list of another length, one beyond the capacity, an empty one for a
    /// capacity, and a list for a parameter bound to one value.
    /// </summary>
    [Fact]
    public async Task A_rebind_the_view_cannot_take_moves_nothing()
    {
        var desk = await DeskAsync();
        await using var _ = desk.Engine;
        const string sql = Groups + "COUNT(*) AS n FROM marks WHERE instrument IN @picks AND instrument NOT IN @skip AND price > @floor GROUP BY seq / 100";
        var first = Values(("picks", new[] { 1, 2, 3 }), ("skip", new[] { 2 }), ("floor", 0.0));
        await using var view = await desk.Engine.MaterializeAsync(
            sql, Options(ViewMaintenance.Incremental, first, [new("picks"), new("skip", Capacity: 2), new("floor")]), Ct);
        var (replica, cursor) = Drained(view);

        (object Values, string Reason)[] refused =
        [
            (Values(("level", 1.0)), "@level is not rebindable"),
            (Values(("picks", new[] { 1, 2 })), "keeps its length"),
            (Values(("skip", new[] { 1, 2, 3 })), "beyond its capacity of 2"),
            (Values(("skip", Array.Empty<int>())), "an empty list"),
            (Values(("floor", new[] { 1.0, 2.0 })), "gives it a list"),
        ];
        foreach (var (values, reason) in refused)
        {
            var refusal = await Assert.ThrowsAsync<ArgumentException>(() => view.RebindAsync(values, Ct).AsTask());
            Assert.Contains(reason, refusal.Message, StringComparison.Ordinal);
        }

        await desk.Engine.RefreshAsync(r => r.Append(desk.Marks, Marks(20_000, 300)), Ct);
        await view.UpdateAsync(Ct);
        view.Drain(ref cursor, replica);
        Same(await StatementAsync(desk.Engine, sql, first), replica.Rows);
    }

    /// <summary>
    /// A rebind whose update fails leaves the view with the values and the rows it held: a divisor of zero
    /// fails the update, and the next update is kept with the divisor before it.
    /// </summary>
    [Theory]
    [InlineData(ViewMaintenance.Incremental)]
    [InlineData(ViewMaintenance.Recompute)]
    public async Task A_rebind_that_fails_keeps_the_values_the_view_held(ViewMaintenance maintenance)
    {
        var desk = await DeskAsync();
        await using var _ = desk.Engine;
        const string sql = "SELECT seq / 100 AS bucket, SUM(price) AS equity, CAST(SUM(price) AS BIGINT) / @div AS n FROM marks GROUP BY seq / 100";
        await using var view = await desk.Engine.MaterializeAsync(sql, Options(maintenance, Values(("div", 2L)), [new("div")]), Ct);
        var (replica, cursor) = Drained(view);

        await Assert.ThrowsAnyAsync<Exception>(() => view.RebindAsync(Values(("div", 0L)), Ct).AsTask());
        await desk.Engine.RefreshAsync(r => r.Append(desk.Marks, Marks(20_000, 300)), Ct);
        await view.UpdateAsync(Ct);
        view.Drain(ref cursor, replica);
        Same(await StatementAsync(desk.Engine, sql, Values(("div", 2L))), replica.Rows);
    }

    /// <summary>
    /// A parameter that nothing beside it gives a type, as the one column of a <c>WITH</c> row, is refused
    /// naming it as the statement writes it, and saying how to type it.
    /// </summary>
    [Fact]
    public async Task A_parameter_nothing_types_is_named()
    {
        var desk = await DeskAsync();
        await using var _ = desk.Engine;
        var refusal = await Assert.ThrowsAsync<PlanningException>(() => desk.Engine.PrepareAsync(
            "WITH t AS (SELECT @level AS level) SELECT g.equity FROM (SELECT SUM(price) AS equity FROM marks) g CROSS JOIN t "
            + "WHERE g.equity < t.level", ct: Ct).AsTask());
        Assert.Contains("@level has nothing beside it to give it a type. Write CAST(@level AS DOUBLE)", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// What a capacity costs: a rebind of a list of three, and an update after 300 marks, with capacities of
    /// 8, 32 and 128, over 200,000 marks, and the state the view holds, beside the same view with the list
    /// bound once. Reported, not held to a bound; the answers are.
    /// </summary>
    [Fact]
    public async Task What_a_capacity_costs()
    {
        {
            var desk = await DeskAsync(rows: 200_000);
            await using var _ = desk.Engine;
            await using var fixedOnce = await desk.Engine.MaterializeAsync(
                Groups + "COUNT(*) AS n FROM marks WHERE instrument IN @picks GROUP BY seq / 100",
                Options(ViewMaintenance.Incremental, Values(("picks", new[] { 1 })), []), Ct);
            output.WriteLine($"bound once: state {fixedOnce.StateBytes:N0} B");
        }

        foreach (var capacity in new[] { 8, 32, 128 })
        {
            var desk = await DeskAsync(rows: 200_000);
            await using var _ = desk.Engine;
            var sql = Groups + "COUNT(*) AS n FROM marks WHERE instrument IN @picks GROUP BY seq / 100";
            await using var view = await desk.Engine.MaterializeAsync(
                sql, Options(ViewMaintenance.Incremental, Values(("picks", new[] { 1 })), [new("picks", capacity)]), Ct);
            var (replica, cursor) = Drained(view);
            var rebind = await TimeAsync(() => view.RebindAsync(Values(("picks", new[] { 2, 3, 5 })), Ct).AsTask());
            await desk.Engine.RefreshAsync(r => r.Append(desk.Marks, Marks(200_000, 300)), Ct);
            var update = await TimeAsync(() => view.UpdateAsync(Ct).AsTask());
            view.Drain(ref cursor, replica);
            Same(await StatementAsync(desk.Engine, sql, Values(("picks", new[] { 2, 3, 5 }))), replica.Rows);
            output.WriteLine($"capacity {capacity}: a rebind {rebind:0.0} ms, an update of 300 marks {update:0.0} ms, state {view.StateBytes:N0} B");
        }
    }

    // ---- the scaffolding ----

    private async Task RebindsAsync(
        string where, string sql, object first, object then, ViewMaintenance maintenance, RebindableParameter[] declared)
    {
        var desk = await DeskAsync();
        await using var _ = desk.Engine;
        await using var view = await desk.Engine.MaterializeAsync(sql, Options(maintenance, first, declared), Ct);
        var (replica, cursor) = Drained(view);
        var before = await StatementAsync(desk.Engine, sql, first);
        Same(before, replica.Rows);

        var plans = desk.Planner.Plans;
        replica.Touched.Clear();
        await view.RebindAsync(then, Ct);
        view.Drain(ref cursor, replica);
        Assert.Equal(plans, desk.Planner.Plans);
        var after = await StatementAsync(desk.Engine, sql, then);
        Same(after, replica.Rows);

        var moved = after.Keys.Union(before.Keys)
            .Where(k => !before.TryGetValue(k, out var a) || !after.TryGetValue(k, out var b) || !Close(a, b))
            .Order()
            .ToList();
        Assert.True(moved.Count > 0, $"{where}: the rebind moved no row, so it tests nothing");
        if (maintenance == ViewMaintenance.Incremental)
        {
            Assert.Equal(moved, replica.Touched.Distinct().Order());
        }

        await desk.Engine.RefreshAsync(r => r.Append(desk.Marks, Marks(20_000, 300)), Ct);
        await view.UpdateAsync(Ct);
        view.Drain(ref cursor, replica);
        Same(await StatementAsync(desk.Engine, sql, then), replica.Rows);
        output.WriteLine($"{where}, {maintenance}: {moved.Count} of {after.Count} rows moved");
    }

    private static RebindableParameter[] Declared(object values) =>
        [.. ((IReadOnlyDictionary<string, object?>)values).Keys.Select(name => new RebindableParameter(name))];

    private static IReadOnlyDictionary<string, object?> Values(params (string Name, object? Value)[] values) =>
        values.ToDictionary(v => v.Name, v => v.Value);

    private static IReadOnlyList<Mark> Marks(int from, int count) =>
        [.. Enumerable.Range(from, count).Select(i => new Mark(
            i, i % 8, Open.AddMilliseconds(i * 125), 50 + ((i * 7919) % 101) / 10.0))];

    private sealed record Desk(ChalkEngine Engine, CountingPlanner Planner, PocoTable<Mark> Marks);

    private async Task<Desk> DeskAsync(int rows = 20_000)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var planner = new CountingPlanner(sidecar.CreatePlanner());
        var source = new PocoSourceBuilder("desk")
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .AddTable("marks", Marks(0, rows), out var marks, t => t.AppendOnly(x => x.Ts))
            .AddTable("indexed", Marks(0, 2_000), t => t.UniqueIndex(x => x.Seq))
            .Build();
        var engine = await ChalkEngine.CreateAsync(
            new ChalkEngineOptions { Sources = [source], Planner = planner, Clock = "now" }, Ct);
        await engine.RefreshAsync(r => r.AdvanceClock(new DateTimeOffset(Open, TimeSpan.Zero)), Ct);
        return new Desk(engine, planner, marks);
    }

    private static ViewOptions<PointKey, PointRow> Options(
        ViewMaintenance maintenance, object? parameters, IReadOnlyList<RebindableParameter> rebindable) => new()
    {
        Key = ["bucket"],
        Parameters = parameters,
        Rebindable = rebindable,
        LocalJoinMaxRows = StandingViews.DefaultLocalJoinMaxRows,
        Maintenance = maintenance,
        Exactness = ViewExactness.EqualUpToRounding,
        Map = static (in ViewRow row, out PointKey key, out PointRow value) =>
        {
            key = new PointKey { Bucket = row.GetInt64(0) };
            value = new PointRow { Equity = row.GetDouble(1), N = row.GetInt64(2) };
        },
    };

    private static async Task<Dictionary<long, PointRow>> StatementAsync(ChalkEngine engine, string sql, object values)
    {
        var rows = new Dictionary<long, PointRow>();
        await foreach (var batch in engine.QueryAsync(sql, (IReadOnlyDictionary<string, object?>)values, ct: Ct))
        {
            using (batch)
            {
                for (var i = 0; i < batch.Length; i++)
                {
                    rows[Convert.ToInt64(Chalk.Arrow.RecordBatchExtensions.ValueAt(batch.Column(0), i))] = new PointRow
                    {
                        Equity = Convert.ToDouble(Chalk.Arrow.RecordBatchExtensions.ValueAt(batch.Column(1), i)),
                        N = Convert.ToInt64(Chalk.Arrow.RecordBatchExtensions.ValueAt(batch.Column(2), i)),
                    };
                }
            }
        }

        return rows;
    }

    private static bool Close(PointRow a, PointRow b) => Math.Abs(a.Equity - b.Equity) < 1e-6 && a.N == b.N;

    private static void Same(Dictionary<long, PointRow> expected, Dictionary<long, PointRow> actual)
    {
        Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
        foreach (var (key, row) in expected)
        {
            Assert.True(Close(row, actual[key]), $"{key}: ({row.Equity}, {row.N}) against ({actual[key].Equity}, {actual[key].N})");
        }
    }

    private static async Task<double> TimeAsync(Func<Task> run)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await run();
        return clock.Elapsed.TotalMilliseconds;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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

        public List<long> Touched { get; } = [];

        public void Reset() => Rows.Clear();

        public void Upsert(in PointKey key, in PointRow row)
        {
            Touched.Add(key.Bucket);
            Rows[key.Bucket] = row;
        }

        public void Delete(in PointKey key)
        {
            Touched.Add(key.Bucket);
            Rows.Remove(key.Bucket);
        }

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

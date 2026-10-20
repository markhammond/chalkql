#pragma warning disable CHALK003
using Chalk.Client;
using Chalk.Incremental;
using Chalk.Sources.Poco;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// Window functions kept by incremental maintenance (design 77, phase 1). Each partition a change touches
/// is computed again, whole, by the batch window over its rows in order, and the view is sent the rows
/// whose values moved. Every answer is held to a run of the statement, through appends at a partition's
/// end, rows changed and deleted in the past, and appends that touch one partition of many, which send
/// only that partition's moved rows. A window whose one change could move every row of a partition is
/// refused, and so is a call that reads which of two tied rows comes first where the order can tie.
/// </summary>
public sealed class MaintainedWindowTests(SharedSidecar sidecar, ITestOutputHelper output) : IClassFixture<SharedSidecar>
{
    private sealed record Fill(long Seq, int Account, DateTime Ts, long Qty);

    private sealed record Unkeyed(long Seq, int Account, DateTime Ts, long Qty);

    private struct RowKey
    {
        public long Key;
    }

    private struct RowValue
    {
        public double V;
    }

    private static readonly DateTime Open = new(2026, 10, 20, 9, 0, 0);

    private const int Accounts = 8;

    public static TheoryData<string, string> Windows() => new()
    {
        {
            "each fill's change from the one before it in its account (W1)",
            "SELECT seq AS k, CAST(qty - LAG(qty, 1, 0) OVER (PARTITION BY account ORDER BY seq) AS DOUBLE) AS v FROM fills"
        },
        {
            "what an account holds at each fill, peers included (W2)",
            "SELECT seq AS k, CAST(SUM(qty) OVER (PARTITION BY account ORDER BY ts RANGE UNBOUNDED PRECEDING) AS DOUBLE) AS v FROM fills"
        },
        {
            "the high over the hour before each fill (W3)",
            "SELECT seq AS k, CAST(MAX(qty) OVER (ORDER BY ts RANGE BETWEEN INTERVAL '1' HOUR PRECEDING AND CURRENT ROW) AS DOUBLE) AS v FROM fills"
        },
        {
            "a running maximum and a lag over groups (W4)",
            "SELECT b AS k, CAST(MAX(total) OVER (ORDER BY b ROWS UNBOUNDED PRECEDING) - LAG(total, 1, 0) OVER (ORDER BY b) AS DOUBLE) AS v "
                + "FROM (SELECT seq / 10 AS b, SUM(qty) AS total FROM fills GROUP BY seq / 10) g"
        },
        {
            "a row number in each account",
            "SELECT seq AS k, CAST(ROW_NUMBER() OVER (PARTITION BY account ORDER BY seq) AS DOUBLE) AS v FROM fills"
        },
        {
            "a rank and a dense rank by quantity, which tie",
            "SELECT seq AS k, CAST(RANK() OVER (PARTITION BY account ORDER BY qty) * 1000 + DENSE_RANK() OVER (PARTITION BY account ORDER BY qty) AS DOUBLE) AS v FROM fills"
        },
        {
            "the first quantity in each account, and the next",
            "SELECT seq AS k, CAST(FIRST_VALUE(qty) OVER (PARTITION BY account ORDER BY seq ROWS UNBOUNDED PRECEDING) * 1000 + "
                + "LEAD(qty, 1, -1) OVER (PARTITION BY account ORDER BY seq) AS DOUBLE) AS v FROM fills"
        },
        {
            "a sum over the three rows either side",
            "SELECT seq AS k, CAST(SUM(qty) OVER (PARTITION BY account ORDER BY seq ROWS BETWEEN 3 PRECEDING AND 3 FOLLOWING) AS DOUBLE) AS v FROM fills"
        },
    };

    /// <summary>
    /// Kept incrementally, each window gives the statement's rows after appends, after rows changed and
    /// deleted in the past, and after a clean-up that deletes the oldest rows of every account.
    /// </summary>
    [Theory]
    [MemberData(nameof(Windows))]
    public async Task A_maintained_window_gives_the_statements_rows(string what, string sql)
    {
        foreach (var maintenance in new[] { ViewMaintenance.Incremental, ViewMaintenance.Recompute })
        {
            var (engine, fills) = await DeskAsync();
            await using var _ = engine;
            await using var view = await engine.MaterializeAsync(sql, Options(maintenance), Ct);
            Assert.Equal(maintenance, view.Maintenance);
            var (replica, cursor) = Drained(view);
            Same(await StatementAsync(engine, sql), replica.Rows);

            await engine.RefreshAsync(r => r.Upsert(fills, Fills(2_000, 120)), Ct);
            await CheckAsync(engine, view, sql, replica, cursor);

            // Changed and deleted in the past: in the middle of every account.
            await engine.RefreshAsync(
                r => r.Upsert(fills, [.. Fills(500, 40).Select(f => f with { Qty = f.Qty + 7 })]).Delete(fills, Fills(900, 30)),
                Ct);
            await CheckAsync(engine, view, sql, replica, cursor);

            // The oldest rows of every account go, as an hourly clean-up takes them.
            await engine.RefreshAsync(r => r.Delete(fills, Fills(0, 200)), Ct);
            await CheckAsync(engine, view, sql, replica, cursor);
            output.WriteLine($"{what}, {maintenance}: kept, {view.StateBytes:N0} B held");
        }
    }

    /// <summary>
    /// A fill appended to one account of eight moves only that account's rows: a running sum sends the new
    /// fill's row and no other, and a lag the new row alone, while a recompute runs every account.
    /// </summary>
    [Theory]
    [InlineData("SELECT seq AS k, CAST(SUM(qty) OVER (PARTITION BY account ORDER BY ts RANGE UNBOUNDED PRECEDING) AS DOUBLE) AS v FROM fills")]
    [InlineData("SELECT seq AS k, CAST(qty - LAG(qty, 1, 0) OVER (PARTITION BY account ORDER BY seq) AS DOUBLE) AS v FROM fills")]
    public async Task An_append_to_one_partition_sends_that_partitions_moved_rows(string sql)
    {
        var (engine, fills) = await DeskAsync();
        await using var _ = engine;
        await using var view = await engine.MaterializeAsync(sql, Options(ViewMaintenance.Incremental), Ct);
        var (replica, cursor) = Drained(view);

        await engine.RefreshAsync(r => r.Upsert(fills, [new Fill(5_000, 3, Open.AddHours(10), 9)]), Ct);
        replica.Touched.Clear();
        await view.UpdateAsync(Ct);
        view.Drain(ref cursor, replica);
        Assert.Equal([5_000L], replica.Touched);
        Same(await StatementAsync(engine, sql), replica.Rows);
    }

    /// <summary>
    /// A window over the rows of the last two hours, by the engine's clock: as the clock moves, the oldest
    /// rows leave every account, and each account is computed again from what it holds.
    /// </summary>
    [Fact]
    public async Task A_window_over_a_clocked_cut_off_follows_the_clock()
    {
        const string sql =
            "SELECT seq AS k, CAST(SUM(qty) OVER (PARTITION BY account ORDER BY ts RANGE UNBOUNDED PRECEDING) AS DOUBLE) AS v "
            + "FROM fills WHERE ts >= @now - INTERVAL '2' HOUR";
        foreach (var maintenance in new[] { ViewMaintenance.Incremental, ViewMaintenance.Recompute })
        {
            var (engine, fills) = await DeskAsync(clock: Open.AddHours(3));
            await using var _ = engine;
            await using var view = await engine.MaterializeAsync(sql, Options(maintenance), Ct);
            var (replica, cursor) = Drained(view);
            Same(await StatementAsync(engine, sql, Open.AddHours(3)), replica.Rows);
            foreach (var minutes in new[] { 20, 45, 90 })
            {
                var now = Open.AddHours(3).AddMinutes(minutes);
                await engine.RefreshAsync(
                    r => r.AdvanceClock(new DateTimeOffset(now, TimeSpan.Zero)).Upsert(fills, Fills(1_000 + minutes, 8)), Ct);
                await view.UpdateAsync(Ct);
                view.Drain(ref cursor, replica);
                Same(await StatementAsync(engine, sql, now), replica.Rows);
            }
        }
    }

    /// <summary>
    /// A call that reads a parameter the view binds again: a rebind computes every partition again, and the
    /// view is sent the rows whose values moved.
    /// </summary>
    [Fact]
    public async Task A_window_reading_a_rebindable_parameter_follows_a_rebind()
    {
        const string sql = "SELECT seq AS k, CAST(LAG(qty, 1, CAST(@fallback AS BIGINT)) OVER (PARTITION BY account ORDER BY seq) AS DOUBLE) AS v FROM fills";
        var (engine, _) = await DeskAsync();
        await using var __ = engine;
        // The default is computed beneath the window, in the projection that feeds it, and read there (F259).
        var prepared = await engine.PrepareAsync(sql, ct: Ct);
        var options = Options(
            ViewMaintenance.Incremental, new Dictionary<string, object?> { ["fallback"] = -1L }, [new RebindableParameter("fallback")]);
        await using var view = await engine.MaterializeAsync(prepared, options, Ct);
        var (replica, cursor) = Drained(view);
        replica.Touched.Clear();
        await view.RebindAsync(new { fallback = -2L }, Ct);
        view.Drain(ref cursor, replica);
        var expected = new Dictionary<long, double>();
        await using (var execution = await engine.ExecuteAsync(prepared, new Dictionary<string, object?> { ["fallback"] = -2L }, ct: Ct))
        {
            await foreach (var batch in execution.Batches)
            {
                using (batch)
                {
                    for (var i = 0; i < batch.Length; i++)
                    {
                        expected[Convert.ToInt64(Chalk.Arrow.RecordBatchExtensions.ValueAt(batch.Column(0), i))] =
                            Convert.ToDouble(Chalk.Arrow.RecordBatchExtensions.ValueAt(batch.Column(1), i));
                    }
                }
            }
        }

        Same(expected, replica.Rows);
        Assert.Equal(Accounts, replica.Touched.Count);
    }

    /// <summary>
    /// What phase 1 costs: an update after a fill appended to each of eight accounts, against a run of the
    /// statement, over 8,000 to 128,000 fills. Each touched account is computed again whole, so the update
    /// follows the accounts' size. Reported, not held to a bound; the answers are.
    /// </summary>
    [Fact]
    public async Task What_a_maintained_window_costs()
    {
        const string sql = "SELECT seq AS k, CAST(SUM(qty) OVER (PARTITION BY account ORDER BY ts RANGE UNBOUNDED PRECEDING) AS DOUBLE) AS v FROM fills";
        foreach (var rows in new[] { 8_000, 32_000, 128_000 })
        {
            var (engine, fills) = await DeskAsync(rows: rows);
            await using var _ = engine;
            var options = Options(ViewMaintenance.Incremental);
            options = new ViewOptions<RowKey, RowValue>
            {
                Key = options.Key,
                LocalJoinMaxRows = options.LocalJoinMaxRows,
                Maintenance = options.Maintenance,
                Map = options.Map,
                StateBudget = 1L << 30,
            };
            await using var view = await engine.MaterializeAsync(sql, options, Ct);
            var (replica, cursor) = Drained(view);
            var update = 0.0;
            for (var step = 0; step < 5; step++)
            {
                await engine.RefreshAsync(r => r.Upsert(fills, Fills(rows + (8 * step), 8)), Ct);
                var clock = System.Diagnostics.Stopwatch.StartNew();
                await view.UpdateAsync(Ct);
                update = clock.Elapsed.TotalMilliseconds;
            }

            view.Drain(ref cursor, replica);
            var run = System.Diagnostics.Stopwatch.StartNew();
            var expected = await StatementAsync(engine, sql);
            var ran = run.Elapsed.TotalMilliseconds;
            Same(expected, replica.Rows);
            output.WriteLine($"{rows:N0} fills: an update of 8 appends {update:0.0} ms, a run {ran:0.0} ms, {view.StateBytes:N0} B held");
        }
    }

    /// <summary>
    /// A <c>LAG</c> default given as a parameter (F259): Calcite computes it in the projection beneath the
    /// window, and the plan reads it there, where it used to leave the call a reference the plan's check
    /// refused. Each account's first fill takes the default.
    /// </summary>
    [Theory]
    [InlineData("LAG(qty, 1, CAST(@fallback AS BIGINT))")]
    [InlineData("LEAD(qty, 1, CAST(@fallback AS BIGINT))")]
    public async Task A_lag_default_may_be_a_parameter(string call)
    {
        var (engine, _) = await DeskAsync();
        await using var __ = engine;
        var sql = $"SELECT seq AS k, CAST({call} OVER (PARTITION BY account ORDER BY seq) AS DOUBLE) AS v FROM fills";
        var rows = await StatementAsync(engine, sql, fallback: -5L);
        var ends = call.StartsWith("LAG", StringComparison.Ordinal) ? Enumerable.Range(0, Accounts) : Enumerable.Range(1_000 - Accounts, Accounts);
        Assert.All(ends, seq => Assert.Equal(-5.0, rows[seq]));
        Assert.Equal(Accounts, rows.Values.Count(v => v == -5.0));
    }

    /// <summary>A window that one change could move every row of is refused, naming the call and why.</summary>
    [Theory]
    [InlineData("SELECT seq AS k, CAST(SUM(qty) OVER (PARTITION BY account) AS DOUBLE) AS v FROM fills", "reaches the end of its partition")]
    [InlineData("SELECT seq AS k, CAST(NTILE(4) OVER (PARTITION BY account ORDER BY seq) AS DOUBLE) AS v FROM fills", "reads the partition's size")]
    [InlineData("SELECT seq AS k, CAST(CUME_DIST() OVER (PARTITION BY account ORDER BY seq) AS DOUBLE) AS v FROM fills", "reads the partition's size")]
    [InlineData(
        "SELECT seq AS k, CAST(qty - LAG(qty, 1, 0) OVER (PARTITION BY account ORDER BY ts) AS DOUBLE) AS v FROM unkeyed",
        "depends on which of two rows that tie in its order comes first")]
    public async Task A_window_whose_churn_is_unbounded_is_refused(string sql, string reason)
    {
        var (engine, _) = await DeskAsync();
        await using var __ = engine;
        var refused = await Assert.ThrowsAsync<StandingViewException>(
            () => engine.MaterializeAsync(sql, Options(ViewMaintenance.Incremental), Ct).AsTask());
        Assert.Contains(reason, refused.Message, StringComparison.Ordinal);

        await using var recomputed = await engine.MaterializeAsync(sql, Options(ViewMaintenance.Recompute), Ct);
        Assert.Equal(ViewMaintenance.Recompute, recomputed.Maintenance);
    }

    // ---- the scaffolding ----

    private static async Task CheckAsync(
        ChalkEngine engine, StandingView<RowKey, RowValue> view, string sql, Replica replica, ViewCursor cursor)
    {
        await view.UpdateAsync(Ct);
        view.Drain(ref cursor, replica);
        Same(await StatementAsync(engine, sql), replica.Rows);
    }

    /// <summary>Fills from <paramref name="from"/> on, dealt to the accounts in turn, a minute apart, with quantities that tie.</summary>
    private static Fill[] Fills(int from, int count) =>
        [.. Enumerable.Range(from, count).Select(i => new Fill(i, i % Accounts, Open.AddMinutes(i / 2), (i * 37) % 11))];

    private async Task<(ChalkEngine Engine, PocoTable<Fill> Fills)> DeskAsync(int rows = 1_000, DateTime? clock = null)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var source = new PocoSourceBuilder("desk")
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .AddTable("fills", (IReadOnlyList<Fill>)Fills(0, rows), out var fills, t => t.Keyed(x => x.Seq))
            .AddTable("unkeyed", (IReadOnlyList<Unkeyed>)[.. Fills(0, 100).Select(f => new Unkeyed(f.Seq, f.Account, f.Ts, f.Qty))])
            .Build();
        var engine = await ChalkEngine.CreateAsync(
            new ChalkEngineOptions { Sources = [source], Planner = sidecar.CreatePlanner(), Clock = "now" }, Ct);
        await engine.RefreshAsync(r => r.AdvanceClock(new DateTimeOffset(clock ?? Open, TimeSpan.Zero)), Ct);
        return (engine, fills);
    }

    private static ViewOptions<RowKey, RowValue> Options(
        ViewMaintenance maintenance, object? parameters = null, IReadOnlyList<RebindableParameter>? rebindable = null) => new()
    {
        Key = ["k"],
        Parameters = parameters,
        Rebindable = rebindable,
        LocalJoinMaxRows = StandingViews.DefaultLocalJoinMaxRows,
        Maintenance = maintenance,
        Map = static (in ViewRow row, out RowKey key, out RowValue value) =>
        {
            key = new RowKey { Key = row.GetInt64(0) };
            value = new RowValue { V = row.GetDouble(1) };
        },
    };

    private static async Task<Dictionary<long, double>> StatementAsync(
        ChalkEngine engine, string sql, DateTime? now = null, long? fallback = null)
    {
        var rows = new Dictionary<long, double>();
        var parameters = new Dictionary<string, object?>();
        if (fallback is { } value)
        {
            parameters["fallback"] = value;
        }

        // A plain query reads the engine's clock, as the view does; @now is that clock.
        _ = now;
        await foreach (var batch in engine.QueryAsync(sql, parameters, ct: Ct))
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

        public List<long> Touched { get; } = [];

        public void Reset() => Rows.Clear();

        public void Upsert(in RowKey key, in RowValue row)
        {
            Touched.Add(key.Key);
            Rows[key.Key] = row.V;
        }

        public void Delete(in RowKey key)
        {
            Touched.Add(key.Key);
            Rows.Remove(key.Key);
        }

        public void Commit(long version, uint checksum)
        {
        }
    }
}

using Chalk.Catalog;
using Chalk.Client;
using Chalk.Entitlements;
using Chalk.Execution.Maintenance;
using Chalk.Sources.Poco;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// A filter over the clock whose every read of it is a cut-off a column is compared with (design 68 §6):
/// when the clock moves, maintenance reads again only the rows between the old cut-offs and the new,
/// and gives what the statement gives. A filter that reads the clock any other way, and a projection
/// that reads it, read every row they hold, as before.
/// </summary>
[Collection(SidecarCollection.Name)]
public sealed class WindowOverTheClockTests(SharedSidecar sidecar)
{
    private sealed record Tick(int Seq, int Book, long Qty, DateTime Ts);

    private static readonly DateTime Start = new(2026, 10, 16, 9, 0, 0);

    private const string LastTenMinutes =
        "SELECT seq, qty FROM ticks WHERE ts > CAST(@now AS TIMESTAMP(9)) - INTERVAL '600' SECOND";

    /// <summary>
    /// Two thousand ticks a second apart, and a ten-minute window over them. Each move of the clock by
    /// ten seconds reads again the ten or so ticks the cut-off passed, not the six hundred held, and
    /// the view is the statement after every move.
    /// </summary>
    [Fact]
    public async Task A_window_reads_again_only_the_rows_the_clock_moved_past()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var desk = await DeskAsync(Ticks(2_000));
        var now = Start.AddSeconds(1_000);
        var prepared = await desk.Engine.PrepareAsync(LastTenMinutes, ct: Ct);
        await using var maintained = await prepared.MaintainAsync([now], 0, [0], Ct);
        var window = Assert.Single(maintained.States.OfType<WindowState>());
        var held = new Dictionary<long, long>();

        // The first update reads the ticks after the cut-off and lets the rest go: the clock moves only
        // forward, so a tick behind a cut-off that rises with it can never pass again.
        Apply(await maintained.UpdateAsync([now], At(now), desk.Engine.Version, whole: false, Ct), held);
        Assert.Equal(1_599, window.Read);
        Assert.Equal(await StatementAsync(desk.Engine, prepared, now), held);

        for (var step = 0; step < 20; step++)
        {
            now = now.AddSeconds(10);
            await desk.Engine.RefreshAsync(r => r.AdvanceClock(At(now)), Ct);
            Apply(await maintained.UpdateAsync([now], At(now), desk.Engine.Version, whole: false, Ct), held);

            Assert.InRange(window.Read, 10, 11);
            Assert.Equal(await StatementAsync(desk.Engine, prepared, now), held);
        }
    }

    /// <summary>
    /// The window and the ticks appended to it in one update: the appended ticks are read once, and
    /// the held ones the cut-off passed, again.
    /// </summary>
    [Fact]
    public async Task Ticks_appended_as_the_clock_moves_are_read_once()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var desk = await DeskAsync(Ticks(1_000));
        var now = Start.AddSeconds(1_000);
        var prepared = await desk.Engine.PrepareAsync(LastTenMinutes, ct: Ct);
        await using var maintained = await prepared.MaintainAsync([now], 0, [0], Ct);
        var window = Assert.Single(maintained.States.OfType<WindowState>());
        var held = new Dictionary<long, long>();
        Apply(await maintained.UpdateAsync([now], At(now), desk.Engine.Version, whole: false, Ct), held);

        var seq = 1_000;
        for (var step = 0; step < 10; step++)
        {
            now = now.AddSeconds(5);
            var appended = Enumerable.Range(0, 5).Select(_ => new Tick(++seq, seq % 3, seq % 7 - 3, Start.AddSeconds(seq))).ToList();
            await desk.Engine.RefreshAsync(r => r.AdvanceClock(At(now)).Append(desk.Source, "ticks", (IReadOnlyList<Tick>)appended), Ct);
            Apply(await maintained.UpdateAsync([now], At(now), desk.Engine.Version, whole: false, Ct), held);

            Assert.InRange(window.Read, 10, 11);
            Assert.Equal(await StatementAsync(desk.Engine, prepared, now), held);
        }
    }

    /// <summary>
    /// Where the window applies and where it does not: a cut-off under an OR or a NOT, or on either
    /// side of its comparison, is a window; a cut-off compared with a computed column, and a projection
    /// that reads the clock, read every row they hold.
    /// </summary>
    [Theory]
    [InlineData("SELECT seq FROM ticks WHERE ts > CURRENT_TIMESTAMP - INTERVAL '60' SECOND", true)]
    [InlineData("SELECT seq FROM ticks WHERE ts > CAST(@now AS TIMESTAMP(9)) - INTERVAL '60' SECOND OR qty < 0", true)]
    [InlineData("SELECT seq FROM ticks WHERE NOT (ts <= CAST(@now AS TIMESTAMP(9)) - INTERVAL '60' SECOND)", true)]
    [InlineData("SELECT seq FROM ticks WHERE CAST(@now AS TIMESTAMP(9)) - INTERVAL '60' SECOND < ts AND qty > 0", true)]
    [InlineData("SELECT seq FROM ticks WHERE ts + INTERVAL '60' SECOND > CAST(@now AS TIMESTAMP(9))", false)]
    [InlineData("SELECT seq, ts > CAST(@now AS TIMESTAMP(9)) - INTERVAL '60' SECOND AS recent FROM ticks", false)]
    public async Task A_window_is_taken_only_where_every_read_of_the_clock_is_a_cut_off(string sql, bool windowed)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var desk = await DeskAsync(Ticks(300));
        var now = Start.AddSeconds(200);
        await desk.Engine.RefreshAsync(r => r.AdvanceClock(At(now)), Ct);
        var prepared = await desk.Engine.PrepareAsync(sql, ct: Ct);
        await using var maintained = await prepared.MaintainAsync(Values(prepared, now), prepared.Parameters.Count - 1, [0], Ct);
        Assert.Equal(windowed, maintained.States.OfType<WindowState>().Any());
        Assert.Equal(!windowed, maintained.States.OfType<ClockedState>().Any());

        var held = new Dictionary<long, long>();
        for (var step = 0; step < 6; step++)
        {
            Apply(await maintained.UpdateAsync(Values(prepared, now), At(now), desk.Engine.Version, whole: false, Ct), held);
            Assert.Equal(await StatementAsync(desk.Engine, prepared, now), held);
            now = now.AddSeconds(15);
            var clock = now;
            await desk.Engine.RefreshAsync(r => r.AdvanceClock(At(clock)), Ct);
        }
    }

    /// <summary>
    /// A window over a table under an access policy: the policy's row filter stays below it, the
    /// window reads again only what the clock moved past, and the view is the statement after every
    /// move (D361).
    /// </summary>
    [Fact]
    public async Task A_window_over_a_table_under_a_policy_is_the_statement()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var desk = await DeskAsync(
            Ticks(600), new TableEntitlementDescriptor { RowPredicate = "book IN (@ctx.books)" });
        var entitled = desk.Engine.WithEntitlements(new EntitlementsOptions());
        var context = new RequestContext
        {
            Lists = new Dictionary<string, ContextRelation>(StringComparer.Ordinal)
            {
                ["books"] = new() { Columns = ["id"], Rows = [[1], [2]], ColumnTypes = [ChalkType.Int32()] },
            },
        };
        var statement = await entitled.PrepareAsync(LastTenMinutes, context, ct: Ct);
        var now = Start.AddSeconds(400);
        await using var maintained = await statement.Query.MaintainAsync([now], 0, [0], Ct);
        var window = Assert.Single(maintained.States.OfType<WindowState>());
        var held = new Dictionary<long, long>();
        for (var step = 0; step < 10; step++)
        {
            Apply(await maintained.UpdateAsync([now], At(now), desk.Engine.Version, whole: false, Ct), held);
            Assert.Equal(await StatementAsync(desk.Engine, statement.Query, now), held);
            Assert.All(held.Keys, seq => Assert.NotEqual(0, seq % 3));
            if (step > 0)
            {
                Assert.InRange(window.Read, 0, 21);
            }

            now = now.AddSeconds(20);
            var clock = now;
            await desk.Engine.RefreshAsync(r => r.AdvanceClock(At(clock)), Ct);
        }
    }

    private static IReadOnlyList<Tick> Ticks(int count) =>
        [.. Enumerable.Range(0, count).Select(i => new Tick(i, i % 3, i % 7 - 3, Start.AddSeconds(i)))];

    private static DateTimeOffset At(DateTime time) => new(time, TimeSpan.Zero);

    /// <summary>The clock's parameter, where the statement names it, and nothing where it reads the current time.</summary>
    private static object?[] Values(PreparedQuery prepared, DateTime now) =>
        [.. prepared.Parameters.Select(_ => (object?)DateTime.SpecifyKind(now, DateTimeKind.Unspecified))];

    /// <summary>What an update changed, applied to the rows held: each by its key, the first column.</summary>
    private static void Apply(MaintainedUpdate update, Dictionary<long, long> held)
    {
        if (update.Whole)
        {
            held.Clear();
        }

        foreach (var (key, _) in Rows(update.Before))
        {
            held.Remove(key);
        }

        foreach (var (key, value) in Rows(update.Now))
        {
            held[key] = value;
        }
    }

    /// <summary>Each row as its key and a number standing for its second column, if it has one.</summary>
    private static IEnumerable<(long Key, long Value)> Rows(Apache.Arrow.RecordBatch batch)
    {
        for (var i = 0; i < batch.Length; i++)
        {
            var key = Convert.ToInt64(Chalk.Arrow.RecordBatchExtensions.ValueAt(batch.Column(0), i));
            var second = batch.ColumnCount > 1 ? Chalk.Arrow.RecordBatchExtensions.ValueAt(batch.Column(1), i) : null;
            yield return (key, second switch
            {
                null => 0,
                long number => number,
                _ => second.GetHashCode(),
            });
        }
    }

    private static async Task<Dictionary<long, long>> StatementAsync(ChalkEngine engine, PreparedQuery prepared, DateTime now)
    {
        var rows = new Dictionary<long, long>();
        await using var execution = await engine.ExecuteAsync(prepared, Values(prepared, now), ct: Ct);
        await foreach (var batch in execution.Batches)
        {
            using (batch)
            {
                foreach (var (key, value) in Rows(batch))
                {
                    rows[key] = value;
                }
            }
        }

        return rows;
    }

    private sealed class Desk : IAsyncDisposable
    {
        public required ChalkEngine Engine { get; init; }

        public required PocoSource Source { get; init; }

        public ValueTask DisposeAsync() => Engine.DisposeAsync();
    }

    private async Task<Desk> DeskAsync(IReadOnlyList<Tick> ticks, TableEntitlementDescriptor? entitlement = null)
    {
        var source = new PocoSourceBuilder("desk")
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .AddTable("ticks", ticks, t =>
            {
                t.OrderedBy(x => x.Ts);
                if (entitlement is not null)
                {
                    t.Entitlement(entitlement);
                }
            })
            .Build();
        var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            Sources = [source],
            Planner = sidecar.CreatePlanner(),
            Clock = "now",
        }, Ct);
        await engine.RefreshAsync(r => r.AdvanceClock(At(Start)), Ct);
        return new Desk { Engine = engine, Source = source };
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}

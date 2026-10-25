using System.Diagnostics;
using System.Reflection;
using Chalk.Client;
using Chalk.Incremental;
using Chalk.Sources.Poco;
using Chalk.TestKit;
using Chalk.Tests;

namespace Chalk.Integration.Tests;

/// <summary>
/// What a maintained view's update allocates beside its statement's (F244): the view keeps the staging
/// it maps an update's rows into, and the values it binds, from one update to the next, so an update
/// adds next to nothing to what its statement allocates; and the room a large update grew is let go
/// after it, so a whole result is not kept twice.
/// </summary>
[Collection(SidecarCollection.Name)]
public sealed class ViewAllocationTests(SharedSidecar sidecar, ITestOutputHelper output)
{
    private sealed record Fill(int Seq, int Book, long Qty);

    private sealed record Book(int Id, int Desk);

    private struct One
    {
        public long Value;
    }

    private const string ByDesk =
        "SELECT b.desk, SUM(f.qty) AS qty FROM fills AS f JOIN books AS b ON b.id = f.book GROUP BY b.desk";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The view's update and its statement's, maintained beside it, take the same changes: what the
    /// view adds is a few hundred bytes, where staging the changed keys anew cost kilobytes.
    /// </summary>
    [Fact]
    public async Task A_maintained_views_update_allocates_little_beside_its_statements()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        SkipUnoptimised();
        var (viewBytes, statementBytes) = await SteadyUpdatesAsync();

        Assert.True(
            viewBytes - statementBytes <= 512,
            $"the view's update allocated {viewBytes} bytes, its statement's {statementBytes}");
    }

    /// <summary>
    /// The statement's update of five keys allocates under 2 KB: its result keeps a key's entry for the
    /// row that replaces another, and records what each key held without copying it; its operators
    /// keep their iterations; and binding its values makes no strings. It allocated 3 KB before.
    /// </summary>
    [Fact]
    public async Task A_maintained_statements_update_of_five_keys_allocates_under_two_kilobytes()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        SkipUnoptimised();
        var (_, statementBytes) = await SteadyUpdatesAsync();

        Assert.True(statementBytes <= 2_048, $"the statement's update allocated {statementBytes} bytes");
    }

    /// <summary>
    /// A view and its statement maintained beside it, warmed on five fills a step over five desks, then
    /// what each update allocates, the least of several steps; the view's rows checked against a run.
    /// </summary>
    private async Task<(long View, long Statement)> SteadyUpdatesAsync()
    {
        await using var desk = await DeskAsync(Fills(3_000, books: 300), Books(300, desks: 30));
        await using var view = await desk.Engine.MaterializeAsync(ByDesk, Options(), Ct);
        var statement = await desk.Engine.PrepareAsync(ByDesk, ct: Ct);
        await using var maintained = await statement.MaintainAsync([], -1, [0], Ct);
        await maintained.UpdateAsync([], default, desk.Engine.Version, whole: false, Ct);
        var seq = 3_000;
        for (var warm = 0; warm < 20; warm++)
        {
            seq = await StepAsync(desk, seq);
            await view.UpdateAsync(Ct);
            await maintained.UpdateAsync([], default, desk.Engine.Version, whole: false, Ct);
        }

        var (viewBytes, statementBytes) = await AllocationProbe.LeastAllocatingAsync(
            async () =>
            {
                seq = await StepAsync(desk, seq);
                var window = AllocationWindow.Open();
                await view.UpdateAsync(Ct);
                window.TryClose(out var viewUpdate, out _);
                window = AllocationWindow.Open();
                await maintained.UpdateAsync([], default, desk.Engine.Version, whole: false, Ct);
                window.TryClose(out var statementUpdate, out _);
                return (viewUpdate, statementUpdate);
            },
            measured => measured.viewUpdate - measured.statementUpdate);

        output.WriteLine($"the view's update allocated {viewBytes} bytes, its statement's {statementBytes}");
        Assert.Equal(await RowsAsync(desk.Engine, statement), await ViewRowsAsync(view));
        return (viewBytes, statementBytes);
    }

    /// <summary>
    /// A maintained statement's update gives its changes in its arena's memory, and the next update gives
    /// that memory back before it writes its own: once warm, the arena serves every update from what it
    /// keeps, with no new array, and holds one update's batches between updates. Disposing the statement
    /// gives the last of them back. A whole result, the first update's, is the GC's, as every update's
    /// batches were before.
    /// </summary>
    [Fact]
    public async Task A_maintained_statements_update_writes_its_changes_in_the_last_ones_memory()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var desk = await DeskAsync(Fills(3_000, books: 300), Books(300, desks: 30));
        var statement = await desk.Engine.PrepareAsync(ByDesk, ct: Ct);
        var maintained = await statement.MaintainAsync([], -1, [0], Ct);
        var arena = maintained.Arena;
        var fresh = new List<long>();
        var held = new List<long>();
        try
        {
            var first = await maintained.UpdateAsync([], default, desk.Engine.Version, whole: false, Ct);
            Assert.Equal(30, first.Now.Length);
            Assert.Equal(0, arena.OutstandingBytes);
            var seq = 3_000;
            for (var step = 0; step < 70; step++)
            {
                seq = await StepAsync(desk, seq);
                var fallbacks = arena.HeapFallbacks;
                var update = await maintained.UpdateAsync([], default, desk.Engine.Version, whole: false, Ct);
                Assert.Equal((5, 5), (update.Before.Length, update.Now.Length));
                if (step >= 20)
                {
                    // Warm: twenty updates have rented what a step of five keys needs.
                    fresh.Add(arena.HeapFallbacks - fallbacks);
                    held.Add(arena.OutstandingBytes);
                }
            }
        }
        finally
        {
            await maintained.DisposeAsync();
        }

        Assert.All(fresh, arrays => Assert.Equal(0, arrays));
        Assert.True(held[0] > 0, "an update's batches are the arena's memory");
        Assert.All(held, bytes => Assert.Equal(held[0], bytes));
        Assert.Equal(0, arena.OutstandingBytes);
    }

    /// <summary>
    /// A view's first update stages its whole result, 5,000 keys. The next, which changes one, lets
    /// that room go: the staging keeps what small updates use, not a second copy of the view's rows.
    /// </summary>
    [Fact]
    public async Task A_views_staging_lets_go_of_the_room_a_whole_result_grew()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var desk = await DeskAsync(Fills(20_000, books: 5_000), Books(5_000, desks: 5_000));
        await using var view = await desk.Engine.MaterializeAsync(ByDesk, Options(), Ct);
        Assert.Equal(5_000, view.Count);
        Assert.True(view.StagingRoom >= 5_000, $"the staging kept room for {view.StagingRoom} keys");

        await desk.Engine.RefreshAsync(r => r.Append(desk.Source, "fills", [new Fill(20_001, 7, 3)]), Ct);
        await view.UpdateAsync(Ct);

        Assert.True(view.StagingRoom <= 256, $"the staging kept room for {view.StagingRoom} keys");
        Assert.Equal(await RowsAsync(desk.Engine, view.Statement), await ViewRowsAsync(view));
    }

    // ---- the parts ----

    /// <summary>
    /// The byte counts hold for code built with optimisation. A Debug build makes each async method's
    /// state machine a class, allocated at every call, even one that completes at once: some 2.8 KB more
    /// an update here.
    /// </summary>
    private static void SkipUnoptimised() => Assert.SkipWhen(
        typeof(ChalkEngine).Assembly.GetCustomAttribute<DebuggableAttribute>()?.IsJITOptimizerDisabled == true,
        "a Debug build allocates each async method's state machine at every call");

    private static ViewOptions<One, One> Options() => new()
    {
        Key = ["desk"],
        Maintenance = ViewMaintenance.Incremental,
        LocalJoinMaxRows = StandingViews.DefaultLocalJoinMaxRows,
        Map = static (in ViewRow row, out One key, out One value) =>
        {
            key = new One { Value = row.GetInt64(0) };
            value = new One { Value = row.GetInt64(1) };
        },
        ResetWhen = double.PositiveInfinity,
    };

    /// <summary>Five fills, on five books of five desks: the update after them changes five keys.</summary>
    private static async Task<int> StepAsync(Desk desk, int seq)
    {
        var fills = new Fill[5];
        for (var i = 0; i < fills.Length; i++)
        {
            seq++;
            fills[i] = new Fill(seq, (seq * 7) % 300, 1 + (seq % 5));
        }

        await desk.Engine.RefreshAsync(r => r.Append(desk.Source, "fills", fills), Ct);
        return seq;
    }

    private static IReadOnlyList<Fill> Fills(int count, int books) =>
        [.. Enumerable.Range(1, count).Select(i => new Fill(i, i % books, (i % 7) + 1))];

    private static IReadOnlyList<Book> Books(int count, int desks) =>
        [.. Enumerable.Range(0, count).Select(i => new Book(i, i % desks))];

    /// <summary>The view's rows, as a reader starting now is sent them.</summary>
    private static Task<Dictionary<long, long>> ViewRowsAsync(StandingView<One, One> view)
    {
        var sink = new Recorder();
        var cursor = ViewCursor.Start;
        view.Drain(ref cursor, sink);
        return Task.FromResult(sink.Rows);
    }

    private static async Task<Dictionary<long, long>> RowsAsync(ChalkEngine engine, PreparedQuery query)
    {
        var rows = new Dictionary<long, long>();
        await using var execution = await engine.ExecuteAsync(query, (IReadOnlyList<object?>?)null, ct: Ct);
        await foreach (var batch in execution.Batches)
        {
            using (batch)
            {
                for (var i = 0; i < batch.Length; i++)
                {
                    rows[Convert.ToInt64(Chalk.Arrow.RecordBatchExtensions.ValueAt(batch.Column(0), i))] =
                        Convert.ToInt64(Chalk.Arrow.RecordBatchExtensions.ValueAt(batch.Column(1), i));
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

    private async Task<Desk> DeskAsync(IReadOnlyList<Fill> fills, IReadOnlyList<Book> books)
    {
        var source = new PocoSourceBuilder("trading")
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .AddTable("fills", fills)
            .AddTable("books", books)
            .Build();
        var engine = await ChalkEngine.CreateAsync(
            new ChalkEngineOptions { Sources = [source], Planner = sidecar.CreatePlanner() },
            Ct);
        return new Desk { Engine = engine, Source = source };
    }

    /// <summary>A sink that keeps the rows it was sent.</summary>
    private sealed class Recorder : IViewSink<One, One>
    {
        public Dictionary<long, long> Rows { get; } = [];

        public void Reset() => Rows.Clear();

        public void Upsert(in One key, in One row) => Rows[key.Value] = row.Value;

        public void Delete(in One key) => Rows.Remove(key.Value);

        public void Commit(long version, uint checksum)
        {
        }
    }
}

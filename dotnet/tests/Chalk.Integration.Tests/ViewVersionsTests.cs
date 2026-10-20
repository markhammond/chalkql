#pragma warning disable CHALK003
using Chalk.Client;
using Chalk.Incremental;
using Chalk.Ir;
using Chalk.Sources.Poco;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// Refreshes a view publishes itself (design 78 §4, §5). A barrier is published by every view, rather than
/// gone past: an update that reaches one stops there. A view that publishes every version stops at each
/// refresh that changes what it reads, in order, so a reader that drains after each update is sent a row
/// added in one refresh and taken out in the next — the same on every run — and goes past the refreshes
/// that change nothing it reads. One that falls further behind than its MaxBacklog is faulted by name.
/// </summary>
[Collection(SidecarCollection.Name)]
public sealed class ViewVersionsTests(SharedSidecar sidecar)
{
    private sealed record Mark(long Seq, long Px);

    private sealed record Note(long Id, string Body);

    private struct RowKey
    {
        public long K;
    }

    private struct RowValue
    {
        public long V;
    }

    private static readonly Mark[] Seed = [new Mark(1, 1), new Mark(2, 2)];

    /// <summary>
    /// A barrier and a refresh after it land before the view updates: the update stops at the barrier, with
    /// its rows, and a reader is sent its version; the next update goes on to the latest.
    /// </summary>
    [Theory]
    [InlineData(ViewMaintenance.Incremental)]
    [InlineData(ViewMaintenance.Recompute)]
    public async Task An_update_stops_at_a_barrier(ViewMaintenance maintenance)
    {
        var (engine, marks, _) = await DeskAsync();
        await using var _ = engine;
        await using var view = await engine.MaterializeAsync("SELECT 1 AS k, SUM(px) AS v FROM marks", Options(maintenance), Ct);
        var sink = new Sink();
        var cursor = ViewCursor.Start;
        view.Drain(ref cursor, sink);

        await engine.RefreshAsync(r => r.Version(10).Barrier().Append(marks, [new Mark(3, 10)]), Ct);
        await engine.RefreshAsync(r => r.Version(11).Append(marks, [new Mark(4, 100)]), Ct);

        Assert.Equal(10, await view.UpdateAsync(Ct));
        view.Drain(ref cursor, sink);
        Assert.Equal((10L, 13L), sink.Commits[^1]);

        Assert.Equal(11, await view.UpdateAsync(Ct));
        view.Drain(ref cursor, sink);
        Assert.Equal((11L, 113L), sink.Commits[^1]);
    }

    /// <summary>A barrier that writes nothing the view reads is published as a relabel, and the update stops at it.</summary>
    [Fact]
    public async Task A_barrier_the_view_does_not_read_is_published_as_a_relabel()
    {
        var (engine, marks, notes) = await DeskAsync();
        await using var _ = engine;
        await using var view = await engine.MaterializeAsync(
            "SELECT 1 AS k, SUM(px) AS v FROM marks", Options(ViewMaintenance.Incremental), Ct);

        await engine.RefreshAsync(r => r.Version(10).Barrier().Append(notes, [new Note(2, "close")]), Ct);
        await engine.RefreshAsync(r => r.Version(11).Append(marks, [new Mark(3, 10)]), Ct);

        Assert.Equal(10, await view.UpdateAsync(Ct));
        Assert.Equal(1, view.Relabels);
        Assert.Equal(11, await view.UpdateAsync(Ct));
    }

    /// <summary>
    /// A row added in one refresh and taken out in the next, with a refresh of another table between: a view
    /// that publishes every version sends a reader that drains after each update the add, then the removal,
    /// at their versions, and the same on a second run; a view that publishes the latest sends neither.
    /// </summary>
    [Theory]
    [InlineData(ViewMaintenance.Incremental)]
    [InlineData(ViewMaintenance.Recompute)]
    public async Task A_view_of_every_version_sends_a_row_added_and_taken_out(ViewMaintenance maintenance)
    {
        var first = await RunAsync(maintenance, ViewVersions.Every);
        var second = await RunAsync(maintenance, ViewVersions.Every);
        Assert.Equal(["10: +3", "12: -3"], first);
        Assert.Equal(first, second);

        Assert.Empty(await RunAsync(maintenance, ViewVersions.Latest));
    }

    /// <summary>One refresh more than MaxBacklog while the view is not updated faults it by name.</summary>
    [Fact]
    public async Task A_view_past_its_backlog_is_faulted()
    {
        var (engine, marks, _) = await DeskAsync();
        await using var _ = engine;
        await using var view = await engine.MaterializeAsync(
            "SELECT seq AS k, px AS v FROM marks",
            Options(ViewMaintenance.Incremental, ViewVersions.Every, maxBacklog: 2),
            Ct);

        for (var version = 10L; version < 13; version++)
        {
            var at = version;
            await engine.RefreshAsync(r => r.Version(at).Append(marks, [new Mark(at, at)]), Ct);
        }

        var fault = await Assert.ThrowsAsync<StandingViewException>(() => view.UpdateAsync(Ct).AsTask());
        Assert.Equal(ChalkErrorCodes.MemoryBudgetExceeded, fault.Code);
        Assert.Contains("MaxBacklog", fault.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<StandingViewException>(() => view.UpdateAsync(Ct).AsTask());
    }

    /// <summary>A view that would publish every version is refused where it reads a database, and with no backlog.</summary>
    [Fact]
    public async Task Every_is_refused_where_nothing_can_be_held()
    {
        var (engine, _, _) = await DeskAsync();
        await using var _ = engine;
        var database = Options(ViewMaintenance.Recompute, ViewVersions.Every);
        await Assert.ThrowsAsync<ArgumentException>(() => engine.MaterializeAsync(
            "SELECT seq AS k, px AS v FROM marks",
            new ViewOptions<RowKey, RowValue>
            {
                Key = database.Key,
                Map = database.Map,
                LocalJoinMaxRows = database.LocalJoinMaxRows,
                Versions = ViewVersions.Every,
                DatabaseReads = ViewDatabaseReads.WhenRefreshed,
            },
            Ct).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => engine.MaterializeAsync(
            "SELECT seq AS k, px AS v FROM marks",
            Options(ViewMaintenance.Recompute, ViewVersions.Every, maxBacklog: 0),
            Ct).AsTask());
    }

    /// <summary>
    /// The add-then-remove run: the changes a reader that drains after each update is sent, as "version: +key"
    /// and "version: -key", once the view has reached the engine.
    /// </summary>
    private async Task<List<string>> RunAsync(ViewMaintenance maintenance, ViewVersions versions)
    {
        var (engine, marks, notes) = await DeskAsync();
        await using var _ = engine;
        await using var view = await engine.MaterializeAsync(
            "SELECT seq AS k, px AS v FROM marks", Options(maintenance, versions), Ct);
        var sink = new Sink();
        var cursor = ViewCursor.Start;
        view.Drain(ref cursor, sink);
        sink.Changes.Clear();

        await engine.RefreshAsync(r => r.Version(10).Append(marks, [new Mark(3, 30)]), Ct);
        await engine.RefreshAsync(r => r.Version(11).Append(notes, [new Note(2, "b")]), Ct);
        await engine.RefreshAsync(r => r.Version(12).Replace(marks, Seed), Ct);

        while (view.Version < engine.Version)
        {
            await view.UpdateAsync(Ct);
            view.Drain(ref cursor, sink);
        }

        return sink.Changes;
    }

    private async Task<(ChalkEngine Engine, PocoTable<Mark> Marks, PocoTable<Note> Notes)> DeskAsync()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var source = new PocoSourceBuilder("desk")
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .AddTable("marks", (IReadOnlyList<Mark>)Seed, out var marks)
            .AddTable("notes", (IReadOnlyList<Note>)[new Note(1, "a")], out var notes)
            .Build();
        var engine = await ChalkEngine.CreateAsync(
            new ChalkEngineOptions { Sources = [source], Planner = sidecar.CreatePlanner() }, Ct);
        await engine.RefreshAsync(r => r.Version(1), Ct);
        return (engine, marks, notes);
    }

    private static ViewOptions<RowKey, RowValue> Options(
        ViewMaintenance maintenance, ViewVersions versions = ViewVersions.Latest, int maxBacklog = 1024) => new()
    {
        Key = ["k"],
        LocalJoinMaxRows = StandingViews.DefaultLocalJoinMaxRows,
        Maintenance = maintenance,
        Versions = versions,
        MaxBacklog = maxBacklog,
        Map = static (in ViewRow row, out RowKey key, out RowValue value) =>
        {
            key = new RowKey { K = row.GetInt64(0) };
            value = new RowValue { V = row.IsNull(1) ? 0 : row.GetInt64(1) };
        },
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Each version committed with the last row's value, and each key sent, as "version: ±key".</summary>
    private sealed class Sink : IViewSink<RowKey, RowValue>
    {
        private readonly List<string> _pending = [];
        private long _row;

        public List<(long Version, long Value)> Commits { get; } = [];

        public List<string> Changes { get; } = [];

        public void Reset() => _pending.Clear();

        public void Upsert(in RowKey key, in RowValue row)
        {
            _row = row.V;
            _pending.Add($"+{key.K}");
        }

        public void Delete(in RowKey key) => _pending.Add($"-{key.K}");

        public void Commit(long version, uint checksum)
        {
            Commits.Add((version, _row));
            foreach (var change in _pending)
            {
                Changes.Add($"{version}: {change}");
            }

            _pending.Clear();
        }
    }
}

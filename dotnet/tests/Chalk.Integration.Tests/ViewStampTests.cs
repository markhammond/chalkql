#pragma warning disable CHALK003
using Chalk.Client;
using Chalk.Incremental;
using Chalk.Sources.Poco;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// A view's stamp and the tokens a host waits on (design 78 §2, §3). A refresh that writes nothing a view
/// reads moves the view's version without an update, and its readers are sent a commit at the new
/// version with the same checksum; one that writes what it reads, or moves the clock it reads, updates
/// it. A rebind moves the stamp's generation without moving the version. A host that changed something
/// waits for the view to show it with <see cref="StandingView{TKey, TRow}.UpdateToAsync"/>, even for a
/// refresh still being published.
/// </summary>
[Collection(SidecarCollection.Name)]
public sealed class ViewStampTests(SharedSidecar sidecar)
{
    private sealed record Mark(long Seq, long Px, DateTime Ts);

    private sealed record Note(long Id, string Body);

    private struct RowKey
    {
        public long K;
    }

    private struct RowValue
    {
        public long V;
    }

    private static readonly DateTime Open = new(2026, 10, 21, 9, 0, 0);

    /// <summary>
    /// A refresh of a table the view does not read relabels it: no update, the same rows, a commit at the
    /// new version with the same checksum. A refresh of its own table updates it.
    /// </summary>
    [Theory]
    [InlineData(ViewMaintenance.Incremental)]
    [InlineData(ViewMaintenance.Recompute)]
    public async Task A_refresh_of_another_table_relabels_the_view(ViewMaintenance maintenance)
    {
        var (engine, marks, notes) = await DeskAsync();
        await using var _ = engine;
        await using var view = await engine.MaterializeAsync("SELECT 1 AS k, SUM(px) AS v FROM marks", Options(maintenance), Ct);
        var sink = new Sink();
        var cursor = ViewCursor.Start;
        view.Drain(ref cursor, sink);
        var first = sink.Stamps[^1];

        await engine.RefreshAsync(r => r.Version(10).Append(notes, [new Note(2, "b")]), Ct);
        await view.UpdateAsync(Ct);
        view.Drain(ref cursor, sink);
        Assert.Equal(1, view.Relabels);
        Assert.Equal(new ViewStamp(10, 0), sink.Stamps[^1].Stamp);
        Assert.Equal(first.Checksum, sink.Stamps[^1].Checksum);
        Assert.Equal(0, sink.Upserts);

        await engine.RefreshAsync(r => r.Version(11).Append(marks, [new Mark(3, 5, Open)]), Ct);
        await view.UpdateAsync(Ct);
        view.Drain(ref cursor, sink);
        Assert.Equal(1, view.Relabels);
        Assert.Equal(new ViewStamp(11, 0), sink.Stamps[^1].Stamp);
        Assert.Equal(1, sink.Upserts);
        Assert.Equal(8L, sink.Row.V);
    }

    /// <summary>A clock move relabels a view that does not read the clock, and updates one that does.</summary>
    [Fact]
    public async Task A_clock_move_relabels_only_a_view_that_does_not_read_it()
    {
        var (engine, _, _) = await DeskAsync();
        await using var _engine = engine;
        await using var timeless = await engine.MaterializeAsync("SELECT 1 AS k, SUM(px) AS v FROM marks", Options(ViewMaintenance.Incremental), Ct);
        await using var clocked = await engine.MaterializeAsync(
            "SELECT 1 AS k, COUNT(*) AS v FROM marks WHERE ts >= @now - INTERVAL '1' HOUR", Options(ViewMaintenance.Incremental), Ct);

        await engine.RefreshAsync(r => r.Version(20).AdvanceClock(new DateTimeOffset(Open.AddHours(2), TimeSpan.Zero)), Ct);
        await timeless.UpdateAsync(Ct);
        await clocked.UpdateAsync(Ct);
        Assert.Equal(1, timeless.Relabels);
        Assert.Equal(0, clocked.Relabels);
        Assert.Equal(new ViewStamp(20, 0), timeless.Stamp);
        Assert.Equal(new ViewStamp(20, 0), clocked.Stamp);
    }

    /// <summary>
    /// A rebind moves the generation at an unchanged version, and readers are committed to it; a host waits
    /// for a refresh it made, still being published, with <c>UpdateToAsync</c>; and a generation the view
    /// has not reached is refused.
    /// </summary>
    [Fact]
    public async Task A_host_waits_for_its_change_by_stamp()
    {
        var (engine, marks, _) = await DeskAsync();
        await using var _engine = engine;
        await using var view = await engine.MaterializeAsync(
            "SELECT 1 AS k, SUM(px) AS v FROM marks WHERE px > @floor",
            Options(ViewMaintenance.Incremental, rebindable: true),
            Ct);
        var sink = new Sink();
        var cursor = ViewCursor.Start;
        view.Drain(ref cursor, sink);

        var rebound = await view.RebindAsync(new { floor = 1L }, Ct);
        view.Drain(ref cursor, sink);
        Assert.Equal(new ViewStamp(view.Stamp.Version, 1), rebound);
        Assert.Equal(rebound, sink.Stamps[^1].Stamp);

        var refreshing = engine.RefreshAsync(r => r.Version(30).Append(marks, [new Mark(9, 100, Open)]), Ct).AsTask();
        var stood = await view.UpdateToAsync(new ViewStamp(30, 0), Ct);
        await refreshing;
        Assert.True(stood >= new ViewStamp(30, 1), $"the view stood at {stood}");
        view.Drain(ref cursor, sink);
        Assert.Equal(102L, sink.Row.V);

        await Assert.ThrowsAsync<ArgumentException>(() => view.UpdateToAsync(new ViewStamp(30, 5), Ct).AsTask());
    }

    private async Task<(ChalkEngine Engine, PocoTable<Mark> Marks, PocoTable<Note> Notes)> DeskAsync()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var source = new PocoSourceBuilder("desk")
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .AddTable("marks", (IReadOnlyList<Mark>)[new Mark(1, 1, Open), new Mark(2, 2, Open)], out var marks)
            .AddTable("notes", (IReadOnlyList<Note>)[new Note(1, "a")], out var notes)
            .Build();
        var engine = await ChalkEngine.CreateAsync(
            new ChalkEngineOptions { Sources = [source], Planner = sidecar.CreatePlanner(), Clock = "now" }, Ct);
        await engine.RefreshAsync(r => r.Version(1).AdvanceClock(new DateTimeOffset(Open, TimeSpan.Zero)), Ct);
        return (engine, marks, notes);
    }

    private static ViewOptions<RowKey, RowValue> Options(ViewMaintenance maintenance, bool rebindable = false) => new()
    {
        Key = ["k"],
        LocalJoinMaxRows = StandingViews.DefaultLocalJoinMaxRows,
        Maintenance = maintenance,
        Parameters = rebindable ? new { floor = 0L } : null,
        Rebindable = rebindable ? [new RebindableParameter("floor")] : null,
        Map = static (in ViewRow row, out RowKey key, out RowValue value) =>
        {
            key = new RowKey { K = row.GetInt64(0) };
            value = new RowValue { V = row.IsNull(1) ? 0 : row.GetInt64(1) };
        },
        RowHash = static (key, row) => (uint)(row.Length > 0 ? row[0] + 31 : 0),
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The stamps committed, with their checksums, the last row, and how many rows were sent.</summary>
    private sealed class Sink : IViewSink<RowKey, RowValue>
    {
        public List<(ViewStamp Stamp, uint Checksum)> Stamps { get; } = [];

        public RowValue Row { get; private set; }

        public int Upserts { get; private set; }

        public void Reset()
        {
        }

        public void Upsert(in RowKey key, in RowValue row)
        {
            Row = row;
            if (Stamps.Count > 0)
            {
                Upserts++;
            }
        }

        public void Delete(in RowKey key)
        {
        }

        public void Commit(long version, uint checksum) => throw new InvalidOperationException("the stamped commit is the one called");

        public void Commit(in ViewStamp stamp, uint checksum) => Stamps.Add((stamp, checksum));
    }
}

#pragma warning disable CHALK003
using System.Diagnostics;
using Chalk.Client;
using Chalk.Incremental;
using Chalk.Sources.Poco;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// A view's update that refreshes overtake is taken at the epoch it read, named by its pin (design 78
/// §1), not thrown away and staged again with refreshes held off. Refreshes numbered by their version
/// append rows that carry it, so a result says which refresh it read: every commit a view publishes
/// names exactly that version, no refresh waits for a view's update, and with refreshes stopped the view
/// settles to the latest at its next update.
/// </summary>
[Collection(SidecarCollection.Name)]
public sealed class ViewLinearisationTests(SharedSidecar sidecar, ITestOutputHelper output)
{
    private sealed record Tick(long Seq, long V);

    private struct RowKey
    {
        public long K;
    }

    private struct RowValue
    {
        public long V;
        public long N;
    }

    private const int PerRefresh = 3;

    /// <summary>
    /// Refreshes every 10 ms against a view whose update takes 50 ms: each commit names the version its
    /// rows came from, with exactly the rows that version holds; no update holds refreshes off (before,
    /// the longest refresh waited 100 ms, against 8 ms with no view); and once the refreshes stop, one
    /// update brings the view to the last of them.
    /// </summary>
    [Theory]
    [InlineData(ViewMaintenance.Incremental)]
    [InlineData(ViewMaintenance.Recompute)]
    public async Task An_overtaken_update_is_taken_at_the_version_it_read(ViewMaintenance maintenance)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var source = new PocoSourceBuilder("desk")
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .AddTable("ticks", (IReadOnlyList<Tick>)[new Tick(0, 0)], out var ticks)
            .AddFunction("slow", f => f.Scalar<long, long>("n").Strict().Client())
            .Build();
        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            Sources = [source],
            Planner = sidecar.CreatePlanner(),
            Functions = registry => registry.AddScalar<long, long>("slow", n =>
            {
                Thread.Sleep(50);
                return n;
            }),
        }, Ct);

        // One row: the last version read, and the rows read, through a function that takes 50 ms.
        await using var view = await engine.MaterializeAsync(
            "SELECT 1 AS k, MAX(v) AS v, slow(COUNT(*)) AS n FROM ticks",
            new ViewOptions<RowKey, RowValue>
            {
                Key = ["k"],
                LocalJoinMaxRows = StandingViews.DefaultLocalJoinMaxRows,
                Maintenance = maintenance,
                Map = static (in ViewRow row, out RowKey key, out RowValue value) =>
                {
                    key = new RowKey { K = row.GetInt64(0) };
                    value = new RowValue { V = row.GetInt64(1), N = row.GetInt64(2) };
                },
            },
            Ct);
        var sink = new Sink();
        var cursor = ViewCursor.Start;
        view.Drain(ref cursor, sink);

        // Refreshes numbered by version, each appending three rows that carry it, every 10 ms.
        using var stop = new CancellationTokenSource();
        var longest = TimeSpan.Zero;
        var last = 0L;
        var refreshing = Task.Run(async () =>
        {
            for (var version = 1L; !stop.IsCancellationRequested; version++)
            {
                var rows = Enumerable.Range(0, PerRefresh).Select(i => new Tick((version * PerRefresh) + i, version)).ToArray();
                var clock = Stopwatch.StartNew();
                var at = version;
                await engine.RefreshAsync(r => r.Version(at).Append(ticks, rows), CancellationToken.None);
                longest = clock.Elapsed > longest ? clock.Elapsed : longest;
                Volatile.Write(ref last, version);
                await Task.Delay(10, CancellationToken.None);
            }
        }, CancellationToken.None);

        var updates = 0;
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(3))
        {
            await view.UpdateAsync(Ct);
            view.Drain(ref cursor, sink);
            updates++;
        }

        await stop.CancelAsync();
        await refreshing;
        await view.UpdateAsync(Ct);
        view.Drain(ref cursor, sink);

        output.WriteLine(
            $"{maintenance}: {updates} updates, {sink.Commits.Count} commits over {Volatile.Read(ref last)} refreshes, "
            + $"the longest refresh {longest.TotalMilliseconds:0} ms");
        Assert.NotEmpty(sink.Commits);
        foreach (var (version, value) in sink.Commits)
        {
            Assert.Equal(version, value.V);
            Assert.Equal(1 + (PerRefresh * value.V), value.N);
        }

        Assert.Equal(sink.Commits.Select(c => c.Version).Order(), sink.Commits.Select(c => c.Version));
        Assert.Equal(Volatile.Read(ref last), sink.Commits[^1].Version);
        Assert.Equal(0, view.HeldRefreshes);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Each commit's version with the row it published.</summary>
    private sealed class Sink : IViewSink<RowKey, RowValue>
    {
        private RowValue _row;

        public List<(long Version, RowValue Value)> Commits { get; } = [];

        public void Reset()
        {
        }

        public void Upsert(in RowKey key, in RowValue row) => _row = row;

        public void Delete(in RowKey key)
        {
        }

        public void Commit(long version, uint checksum)
        {
            if (Commits.Count == 0 || Commits[^1].Version != version)
            {
                Commits.Add((version, _row));
            }
        }
    }
}

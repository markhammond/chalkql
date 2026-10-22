using Chalk.Client;
using Chalk.Sources;
using Chalk.Sources.Poco;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// A statement run again and again on one arena falls back to the heap on every run when the arena
/// retains its peak but not its working set, and on none once it retains the working set the arena
/// reports.
/// </summary>
[Collection(SidecarCollection.Name)]
public sealed class ArenaWorkingSetTests(SharedSidecar sidecar)
{
    private sealed record Tick(long Seq, long Inst, DateTime Ts, double Px);

    private const string Buckets =
        "SELECT inst, FLOOR(ts TO MINUTE) AS b, MAX(px) AS m FROM ticks GROUP BY inst, FLOOR(ts TO MINUTE)";

    [Fact]
    public async Task Retaining_the_working_set_keeps_repeats_off_the_heap()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var ct = TestContext.Current.CancellationToken;
        var start = new DateTime(2026, 10, 21);
        var source = new PocoSourceBuilder("desk").NamingPolicy(PocoNamingPolicy.SnakeCase)
            .AddTable("ticks", (IReadOnlyList<Tick>)[.. Enumerable.Range(0, 300_000).Select(i => new Tick(i, i % 40, start.AddSeconds(i), i % 1000 / 10.0))])
            .Build();
        await using var engine = await ChalkEngine.CreateAsync(
            new ChalkEngineOptions { Sources = [source], Planner = sidecar.CreatePlanner() }, ct);
        var prepared = await engine.PrepareAsync(Buckets);

        // Measured on an arena that keeps everything: the working set is above the peak.
        using var roomy = new ExecutionArena(new ArenaOptions { RetainBytes = 1L << 30 });
        await RunAsync(engine, prepared, roomy, ct);
        Assert.True(roomy.WorkingSetBytes > roomy.PeakBytes, $"working set {roomy.WorkingSetBytes}, peak {roomy.PeakBytes}");

        using var atPeak = new ExecutionArena(new ArenaOptions { RetainBytes = roomy.PeakBytes });
        Assert.True(await WarmFallbacksAsync(engine, prepared, atPeak, ct) > 0, "retaining the peak should fall back");

        using var atWorkingSet = new ExecutionArena(new ArenaOptions { RetainBytes = roomy.WorkingSetBytes });
        Assert.Equal(0, await WarmFallbacksAsync(engine, prepared, atWorkingSet, ct));
    }

    /// <summary>The bytes the second and third runs rent from the heap, the first having warmed the arena.</summary>
    private static async Task<long> WarmFallbacksAsync(ChalkEngine engine, PreparedQuery prepared, ExecutionArena arena, CancellationToken ct)
    {
        await RunAsync(engine, prepared, arena, ct);
        var before = arena.HeapFallbackBytes;
        await RunAsync(engine, prepared, arena, ct);
        await RunAsync(engine, prepared, arena, ct);
        return arena.HeapFallbackBytes - before;
    }

    private static async Task RunAsync(ChalkEngine engine, PreparedQuery prepared, ExecutionArena arena, CancellationToken ct)
    {
        await using var execution = await engine.ExecuteAsync(prepared, (IReadOnlyList<object?>?)null, arena, ct);
        await foreach (var batch in execution.Batches.WithCancellation(ct))
        {
            batch.Dispose();
        }
    }
}

using Apache.Arrow;
using Chalk.Client;
using Chalk.Sources;
using Chalk.Sources.Poco;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// Managed output, the default, is the host's to keep: a projection's, a filter's or a join's batches
/// are copied out of the arena's scratch at their exact sizes, and the scratch stays pooled. So a
/// statement run again on one arena rents nothing from the heap after its first run, and a batch the
/// host kept from an earlier run still holds its own rows after later runs reuse the scratch.
/// </summary>
[Collection(SidecarCollection.Name)]
public sealed class ManagedOutputCopyTests(SharedSidecar sidecar)
{
    private sealed record Tick(long Seq, long Inst, double Px, string? Note);

    private sealed record Inst(long Id, string Name);

    [Theory]
    [InlineData("SELECT seq, px, note FROM ticks WHERE px > 10")]
    [InlineData("SELECT t.seq, i.name FROM ticks t JOIN insts i ON t.inst = i.id")]
    public async Task Repeats_rent_nothing_from_the_heap_and_kept_batches_keep_their_rows(string sql)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var ct = TestContext.Current.CancellationToken;
        var source = new PocoSourceBuilder("desk").NamingPolicy(PocoNamingPolicy.SnakeCase)
            .AddTable("ticks", (IReadOnlyList<Tick>)[.. Enumerable.Range(0, 200_000).Select(i => new Tick(i, i % 40, i % 1000 / 10.0, i % 7 == 0 ? null : $"n{i % 97}"))])
            .AddTable("insts", (IReadOnlyList<Inst>)[.. Enumerable.Range(0, 40).Select(i => new Inst(i, $"inst-{i}"))])
            .Build();
        await using var engine = await ChalkEngine.CreateAsync(
            new ChalkEngineOptions { Sources = [source], Planner = sidecar.CreatePlanner() }, ct);
        var prepared = await engine.PrepareAsync(sql);
        using var arena = new ExecutionArena(new ArenaOptions { RetainBytes = 256L << 20 });

        var kept = await RunAsync(engine, prepared, arena, ct);
        var first = Text(kept);
        var fallen = arena.HeapFallbackBytes;
        for (var run = 0; run < 3; run++)
        {
            foreach (var batch in await RunAsync(engine, prepared, arena, ct))
            {
                batch.Dispose();
            }
        }

        Assert.Equal(0, arena.HeapFallbackBytes - fallen);
        Assert.Equal(first, Text(kept));
        foreach (var batch in kept)
        {
            batch.Dispose();
        }
    }

    private static async Task<List<RecordBatch>> RunAsync(ChalkEngine engine, PreparedQuery prepared, ExecutionArena arena, CancellationToken ct)
    {
        var batches = new List<RecordBatch>();
        await using var execution = await engine.ExecuteAsync(prepared, (IReadOnlyList<object?>?)null, arena, ct);
        await foreach (var batch in execution.Batches.WithCancellation(ct))
        {
            batches.Add(batch);
        }

        return batches;
    }

    /// <summary>Every row of the batches as text, so two readings can be compared whole.</summary>
    private static string Text(List<RecordBatch> batches)
    {
        var text = new System.Text.StringBuilder();
        foreach (var batch in batches)
        {
            for (var row = 0; row < batch.Length; row++)
            {
                for (var column = 0; column < batch.ColumnCount; column++)
                {
                    text.Append(Chalk.Arrow.RecordBatchExtensions.ValueAt(batch.Column(column), row)).Append('|');
                }

                text.Append('\n');
            }
        }

        return text.ToString();
    }
}

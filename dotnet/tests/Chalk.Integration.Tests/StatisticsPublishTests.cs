using Chalk.Client;
using Chalk.Sources.Poco;
using Chalk.TestKit;
using Chalk.Tests;

namespace Chalk.Integration.Tests;

/// <summary>
/// A refresh that brings a table new statistics validates the tables it moved, not the whole catalog
/// (F243): what it costs does not grow with the tables it did not touch.
/// </summary>
[Collection(SidecarCollection.Name)]
public sealed class StatisticsPublishTests(SharedSidecar sidecar, ITestOutputHelper output)
{
    private sealed record Note(long Id, int Team, string Body);

    private sealed record Other(long Id, string Name, string Kind, long Amount, double Rate, DateTime At);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// A keyed table whose statistics are computed again at every refresh, beside one other table and
    /// beside forty: a refresh of one row, up to the task it returns, allocates under 128 bytes more for
    /// each table it did not touch. Before, such a refresh validated the whole catalog, at about
    /// 2.8 KB a table.
    /// </summary>
    [Fact]
    public async Task A_refresh_that_moves_one_tables_statistics_validates_only_that_table()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var few = await RefreshBytesAsync(others: 1);
        var many = await RefreshBytesAsync(others: 40);

        output.WriteLine($"beside one table, a refresh allocated {few} bytes; beside forty, {many}");
        Assert.True(
            (many - few) / 39.0 < 128,
            $"beside forty tables a refresh allocated {many} bytes, beside one {few}: "
            + $"{(many - few) / 39.0:0} bytes more for each table it did not touch");
    }

    /// <summary>
    /// The least a refresh of one new note allocates on the calling thread up to the task it returns,
    /// over several, warm, with <paramref name="others"/> tables beside the notes.
    /// </summary>
    private async Task<long> RefreshBytesAsync(int others)
    {
        var builder = new PocoSourceBuilder("feed")
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .AddTable(
                "notes",
                (IReadOnlyList<Note>)[.. Enumerable.Range(1, 300).Select(i => new Note(i, i % 5, $"note-{i}"))],
                t => t.Keyed(x => x.Id).Statistics(Chalk.Ir.StatisticsLevel.Basic, refreshWhenGrownBy: 0));
        for (var i = 0; i < others; i++)
        {
            builder.AddTable($"other_{i}", (IReadOnlyList<Other>)[new Other(1, "one", "plain", 1, 1.5, DateTime.UnixEpoch)]);
        }

        var source = builder.Build();
        await using var engine = await ChalkEngine.CreateAsync(
            new ChalkEngineOptions { Sources = [source], Planner = sidecar.CreatePlanner() }, Ct);
        var next = 300L;
        var least = long.MaxValue;
        for (var step = 0; step < 30; step++)
        {
            next++;
            var window = AllocationWindow.Open();
            var refresh = engine.RefreshAsync(
                (source, note: new Note(next, 1, "new")),
                static (r, s) => r.Upsert(s.source, "notes", (IReadOnlyList<Note>)[s.note]),
                Ct);
            if (window.TryClose(out var bytes, out _) && step >= 10)
            {
                least = Math.Min(least, bytes);
            }

            await refresh;
        }

        Assert.NotEqual(long.MaxValue, least);
        return least;
    }
}

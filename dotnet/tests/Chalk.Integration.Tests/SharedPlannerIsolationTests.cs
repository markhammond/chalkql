using Chalk.Client;
using Chalk.Sources.Poco;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// Engines that share a planner keep their catalogs apart, however they are named. Two instances of one
/// service, each an engine named "practice" over a table of the same name, plan against one planner: each
/// statement is planned on its own engine's numbers, and each engine registers under an instance of its
/// own, so neither replaces the other's statistics nor evicts its shapes.
/// </summary>
[Collection(SidecarCollection.Name)]
public sealed class SharedPlannerIsolationTests(SharedSidecar sidecar)
{
    private sealed record Mark(long Seq, long Px);

    /// <summary>
    /// The first engine's table holds 10 rows and the second's 50,000. Planned after the second has
    /// registered, the first's statement still estimates its own 10 rows, and the second its 50,000;
    /// a refresh of the second moves its own numbers and not the first's.
    /// </summary>
    [Fact]
    public async Task Engines_with_one_name_plan_on_their_own_numbers()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var ct = TestContext.Current.CancellationToken;
        await using var blue = await EngineAsync(10, ct);
        await using var green = await EngineAsync(50_000, ct);

        Assert.Equal("practice", blue.Catalog.ContextId);
        Assert.Equal("practice", green.Catalog.ContextId);
        Assert.NotEqual(blue.InstanceId, green.InstanceId);

        const string Sql = "SELECT seq, px FROM marks";
        Assert.InRange(await EstimateAsync(blue, Sql), 1, 100);
        Assert.InRange(await EstimateAsync(green, Sql), 10_000, 100_000);

        // A refresh of the green engine's table registers its numbers, under its own instance only.
        var (marks, _) = Tables[green];
        await green.RefreshAsync(r => r.Replace(marks, Rows(200_000)), ct);
        Assert.InRange(await EstimateAsync(blue, Sql), 1, 100);
        Assert.InRange(await EstimateAsync(green, Sql), 100_000, 400_000);
    }

    private readonly Dictionary<ChalkEngine, (PocoTable<Mark> Marks, int Rows)> Tables = [];

    private async Task<ChalkEngine> EngineAsync(int rows, CancellationToken ct)
    {
        var source = new PocoSourceBuilder("desk")
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .AddTable("marks", Rows(rows), out var marks)
            .Build();
        var engine = await ChalkEngine.CreateAsync(
            new ChalkEngineOptions { ContextId = "practice", Sources = [source], Planner = sidecar.CreatePlanner() }, ct);
        Tables[engine] = (marks, rows);
        return engine;
    }

    private static IReadOnlyList<Mark> Rows(int count) => [.. Enumerable.Range(0, count).Select(i => new Mark(i, i))];

    /// <summary>The planner's estimate of the statement's rows, read off its plan's root.</summary>
    private static async Task<double> EstimateAsync(ChalkEngine engine, string sql)
    {
        var prepared = await engine.PrepareAsync(sql);
        return prepared.Plan.Root.EstRowCount;
    }
}

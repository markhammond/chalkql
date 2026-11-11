using Chalk.Arrow;
using Chalk.Client;
using Chalk.Sources.Poco;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// Engines may share a planner client, which is disposed with the last of them (F290).
/// </summary>
/// <remarks>
/// Disposing an engine disposed the client it was handed, whatever else used it: a second engine
/// on the same client went on running its prepared statements and clock-only refreshes, then failed
/// at the first refresh that replaced a table, with <c>ObjectDisposedException</c> from the client's
/// channel, and no third engine could be made on it. A host that builds a new engine for a changed
/// rule set and disposes the old one while both exist met exactly that.
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class PlannerSharingTests(SharedSidecar sidecar)
{
    private sealed record Track(string TrackId, string Callsign);

    private static readonly Track[] Tracks = [new("T1", "TEAK 11"), new("T2", "PETREL 22")];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// With two engines on one client, disposing the first leaves the second planning: a refresh that
    /// replaces a table, and a statement prepared afterwards, both run.
    /// </summary>
    [Fact]
    public async Task Disposing_one_engine_leaves_another_on_the_same_client_planning()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var planner = sidecar.CreatePlanner();
        var (first, _) = await EngineAsync(planner, "first");
        var (second, source) = await EngineAsync(planner, "second");

        await first.DisposeAsync();

        await using (second)
        {
            Track[] replaced = [new("T3", "BANYAN 41")];
            await second.RefreshAsync(r => r.Replace(source, "tracks", replaced), Ct);
            Assert.Equal(["T3"], await IdsAsync(second, "SELECT TrackId FROM s.tracks"));
        }
    }

    /// <summary>
    /// Once the last engine on a client is disposed, the client is too, and an engine made on it
    /// afterwards is refused when it is made, naming the client.
    /// </summary>
    [Fact]
    public async Task A_client_disposed_with_its_last_engine_is_refused_to_a_new_one()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var planner = sidecar.CreatePlanner();
        var (first, _) = await EngineAsync(planner, "first");
        var (second, _) = await EngineAsync(planner, "second");
        await first.DisposeAsync();
        await second.DisposeAsync();

        var refused = await Assert.ThrowsAsync<ArgumentException>(async () => await EngineAsync(planner, "third"));

        Assert.Contains("ChalkEngineOptions.Planner was disposed with the last engine", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>An engine with a client of its own disposes it as it always has.</summary>
    [Fact]
    public async Task An_engines_own_client_goes_with_it()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var planner = sidecar.CreatePlanner();
        var (engine, _) = await EngineAsync(planner, "only");

        await engine.DisposeAsync();

        await Assert.ThrowsAnyAsync<ObjectDisposedException>(async () => await planner.GetInfoAsync(Ct));
    }

    /// <summary>An engine that fails to be made takes no share of the client and does not dispose it.</summary>
    [Fact]
    public async Task An_engine_that_fails_to_be_made_leaves_the_client_alone()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var planner = sidecar.CreatePlanner();
        var duplicate = Source();

        await Assert.ThrowsAsync<ArgumentException>(async () => await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "refused",
            Sources = [duplicate, Source()],
            Planner = planner,
        }, Ct));

        var (engine, _) = await EngineAsync(planner, "after");
        await using (engine)
        {
            Assert.Equal(["T1", "T2"], await IdsAsync(engine, "SELECT TrackId FROM s.tracks ORDER BY TrackId"));
        }
    }

    private static PocoSource Source() =>
        new PocoSourceBuilder("s", "s").AddTable("tracks", Tracks, t => t.UniqueKey(x => x.TrackId)).Build();

    private static async Task<(ChalkEngine Engine, PocoSource Source)> EngineAsync(IQueryPlanner planner, string name)
    {
        var source = Source();
        var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = name,
            Sources = [source],
            Planner = planner,
        }, Ct);
        return (engine, source);
    }

    private static async Task<List<string>> IdsAsync(ChalkEngine engine, string sql)
    {
        var ids = new List<string>();
        await using var execution = await engine.ExecuteAsync(await engine.PrepareAsync(sql, ct: Ct), ct: Ct);
        await foreach (var batch in execution.Batches.WithCancellation(Ct))
        {
            using (batch)
            {
                ids.AddRange(batch.ToRows().Select(r => (string)r[0]!));
            }
        }

        return ids;
    }
}

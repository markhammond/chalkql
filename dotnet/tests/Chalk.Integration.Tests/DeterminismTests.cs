using Chalk.Client;
using Chalk.Ir;
using Chalk.TestKit;
using PlanRequest = Chalk.Client.PlanRequest;

namespace Chalk.Integration.Tests;

/// <summary>
/// Volcano nondeterminism is the risk rev 3 calls out for M1, and a golden-plan suite that flakes is
/// worse than none. This is the .NET half (docs/design/05-testing.md §6): every corpus query planned
/// 100 times over RPC, and again across a sidecar restart. The planner's own <c>DigestTest</c> does
/// the in-JVM half, so a regression is caught without needing both toolchains.
/// </summary>
/// <remarks>Owns a private sidecar because it kills and restarts the process.</remarks>
public sealed class DeterminismTests : IAsyncLifetime
{
    private const int Runs = 100;

    private static readonly CorpusFixture Fixture = CorpusFixture.Shared;

    private SidecarFixture? _sidecar;

    public async ValueTask InitializeAsync() => _sidecar = await SidecarFixture.StartAsync();

    public async ValueTask DisposeAsync()
    {
        if (_sidecar is not null)
        {
            await _sidecar.DisposeAsync();
        }
    }

    [Fact]
    public async Task One_hundred_plans_of_every_corpus_query_produce_one_digest_each()
    {
        Assert.SkipWhen(!_sidecar!.IsAvailable, _sidecar.SkipReason ?? string.Empty);

        await using var planner = _sidecar.CreatePlanner();
        await planner.RegisterCatalogAsync(Fixture.Catalog);

        foreach (var query in CorpusQueries.Load())
        {
            var digests = new HashSet<ulong>();
            for (var run = 0; run < Runs; run++)
            {
                digests.Add((await PlanAsync(planner, query)).PlanDigest);
            }

            Assert.True(
                digests.Count == 1,
                $"{query.Name} produced {digests.Count} different digests over {Runs} runs: "
                + string.Join(", ", digests.Select(PlanDigest.Format)));
        }
    }

    [Fact]
    public async Task Digests_survive_a_sidecar_restart()
    {
        Assert.SkipWhen(!_sidecar!.IsAvailable, _sidecar.SkipReason ?? string.Empty);

        var before = new Dictionary<string, ulong>(StringComparer.Ordinal);
        await using (var planner = _sidecar.CreatePlanner())
        {
            await planner.RegisterCatalogAsync(Fixture.Catalog);
            foreach (var query in CorpusQueries.Load())
            {
                before[query.Name] = (await PlanAsync(planner, query)).PlanDigest;
            }
        }

        // kill -9 in all but name: the fixture kills the process tree and starts a fresh JVM.
        await _sidecar.DisposeAsync();
        _sidecar = await SidecarFixture.StartAsync();
        Assert.SkipWhen(!_sidecar.IsAvailable, _sidecar.SkipReason ?? string.Empty);

        await using var restarted = _sidecar.CreatePlanner();
        await restarted.RegisterCatalogAsync(Fixture.Catalog);
        foreach (var query in CorpusQueries.Load())
        {
            var after = (await PlanAsync(restarted, query)).PlanDigest;

            Assert.Equal(PlanDigest.Format(before[query.Name]), PlanDigest.Format(after));
        }
    }

    /// <summary>The config hash must be stable too, or M3's plan cache would invalidate on restart.</summary>
    [Fact]
    public async Task The_planner_config_hash_survives_a_restart()
    {
        Assert.SkipWhen(!_sidecar!.IsAvailable, _sidecar.SkipReason ?? string.Empty);

        uint before;
        await using (var planner = _sidecar.CreatePlanner())
        {
            before = (await planner.GetInfoAsync()).PlannerConfigHash;
        }

        await _sidecar.DisposeAsync();
        _sidecar = await SidecarFixture.StartAsync();
        Assert.SkipWhen(!_sidecar.IsAvailable, _sidecar.SkipReason ?? string.Empty);

        await using var restarted = _sidecar.CreatePlanner();

        Assert.Equal(before, (await restarted.GetInfoAsync()).PlannerConfigHash);
        Assert.NotEqual(0u, before);
    }

    private static async Task<Plan> PlanAsync(IQueryPlanner planner, CorpusQuery query)
    {
        var result = await planner.PlanAsync(new PlanRequest
        {
            Sql = query.PlannerSql,
            ContextId = Fixture.Catalog.ContextId,
            CatalogEpoch = Fixture.Catalog.Epoch,
            Options = new Chalk.Client.PlannerOptions
            {
                Pushdown = PushdownLevel.Full,
                Conformance = query.Conformance,
            },
        });
        return result.Plan;
    }
}

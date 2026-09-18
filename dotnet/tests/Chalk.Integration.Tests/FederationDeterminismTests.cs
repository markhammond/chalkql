using Chalk.Client;
using Chalk.Ir;
using Chalk.TestKit;
using PlanRequest = Chalk.Client.PlanRequest;

namespace Chalk.Integration.Tests;

/// <summary>
/// <b>Exit criterion:</b> the three-source plan's digest is stable across restarts (§8, §6 corpus
/// 04). A hundred plans of every federation query produce one digest each, and the same digests come
/// back after the sidecar has been killed and restarted.
/// </summary>
/// <remarks>
/// The M1 suite already asserts this for the local corpus; the federation one is where it could
/// plausibly break, because a cross-source plan's shape depends on which of several alternatives
/// cost chose and on the order the sources were registered in. Owns a private sidecar, because it
/// restarts the process.
/// </remarks>
public sealed class FederationDeterminismTests : IAsyncLifetime
{
    private const int Runs = 100;

    private SidecarFixture? _sidecar;

    public async ValueTask InitializeAsync() => _sidecar = await SidecarFixture.StartAsync();

    public async ValueTask DisposeAsync()
    {
        if (_sidecar is not null)
        {
            await _sidecar.DisposeAsync();
        }
    }

    private static RemoteFixture Fixture => RemoteFixture.Shared;

    [Fact]
    public async Task One_hundred_plans_of_every_federation_query_produce_one_digest_each()
    {
        Assert.SkipWhen(RemoteFixture.SkipReason is not null, RemoteFixture.SkipReason ?? string.Empty);
        Assert.SkipWhen(!_sidecar!.IsAvailable, _sidecar.SkipReason ?? string.Empty);

        var catalog = Fixture.PartitionedCatalog();
        await using var planner = _sidecar.CreatePlanner();
        await planner.RegisterCatalogAsync(catalog);

        foreach (var query in CorpusQueries.LoadM9())
        {
            var digests = new HashSet<ulong>();
            for (var run = 0; run < Runs; run++)
            {
                digests.Add((await PlanAsync(planner, catalog, query)).PlanDigest);
            }

            Assert.True(
                digests.Count == 1,
                $"{query.Name} produced {digests.Count} different digests over {Runs} runs: "
                + string.Join(", ", digests.Select(PlanDigest.Format)));
        }
    }

    [Fact]
    public async Task Federation_digests_survive_a_sidecar_restart()
    {
        Assert.SkipWhen(RemoteFixture.SkipReason is not null, RemoteFixture.SkipReason ?? string.Empty);
        Assert.SkipWhen(!_sidecar!.IsAvailable, _sidecar.SkipReason ?? string.Empty);

        var catalog = Fixture.PartitionedCatalog();
        var before = new Dictionary<string, ulong>(StringComparer.Ordinal);
        await using (var planner = _sidecar.CreatePlanner())
        {
            await planner.RegisterCatalogAsync(catalog);
            foreach (var query in CorpusQueries.LoadM9())
            {
                before[query.Name] = (await PlanAsync(planner, catalog, query)).PlanDigest;
            }
        }

        await _sidecar.DisposeAsync();
        _sidecar = await SidecarFixture.StartAsync();
        Assert.SkipWhen(!_sidecar.IsAvailable, _sidecar.SkipReason ?? string.Empty);

        await using var restarted = _sidecar.CreatePlanner();
        await restarted.RegisterCatalogAsync(catalog);
        foreach (var query in CorpusQueries.LoadM9())
        {
            var after = (await PlanAsync(restarted, catalog, query)).PlanDigest;

            Assert.Equal(PlanDigest.Format(before[query.Name]), PlanDigest.Format(after));
        }
    }

    /// <summary>
    /// §6 corpus 08: a policy is part of what a plan is. The same statement under a policy that
    /// forbids the strategy the planner would have chosen is a different plan, with a different
    /// digest — and the same rows, which the corpus differential asserts.
    /// </summary>
    [Fact]
    public async Task A_policy_override_changes_the_digest()
    {
        Assert.SkipWhen(RemoteFixture.SkipReason is not null, RemoteFixture.SkipReason ?? string.Empty);
        Assert.SkipWhen(!_sidecar!.IsAvailable, _sidecar.SkipReason ?? string.Empty);

        var catalog = Fixture.PartitionedCatalog();
        await using var planner = _sidecar.CreatePlanner();
        await planner.RegisterCatalogAsync(catalog);

        var lookup = CorpusQueries.LoadM9().Single(q => q.Name == "01_lookup_local_dimension");
        var forced = CorpusQueries.LoadM9().Single(q => q.Name == "08_policy_forbids_lookup");
        Assert.Equal(lookup.Sql, forced.Sql);

        var withLookup = await PlanAsync(planner, catalog, lookup);
        var withoutLookup = await PlanAsync(planner, catalog, forced);

        Assert.NotEqual(withLookup.PlanDigest, withoutLookup.PlanDigest);
        Assert.Contains(
            PlanWalker.Rels(withLookup.Root), r => r.KindCase == Rel.KindOneofCase.LookupJoin);
        Assert.DoesNotContain(
            PlanWalker.Rels(withoutLookup.Root), r => r.KindCase == Rel.KindOneofCase.LookupJoin);
    }

    private static async Task<Plan> PlanAsync(
        IQueryPlanner planner, Chalk.Catalog.CatalogContext catalog, CorpusQuery query)
    {
        var result = await planner.PlanAsync(new PlanRequest
        {
            Sql = query.PlannerSql,
            ContextId = catalog.ContextId,
            CatalogEpoch = catalog.Epoch,
            Options = new Chalk.Client.PlannerOptions
            {
                Pushdown = PushdownLevel.Full,
                Conformance = query.Conformance,
                JoinPolicy = query.JoinPolicy,
            },
        });
        return result.Plan;
    }
}

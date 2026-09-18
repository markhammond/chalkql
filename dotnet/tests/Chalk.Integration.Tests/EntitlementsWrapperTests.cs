using Chalk.Client;
using Chalk.Entitlements;
using Chalk.TestKit;
using Google.Protobuf;
using CatalogContext = Chalk.Catalog.CatalogContext;

namespace Chalk.Integration.Tests;

/// <summary>
/// The wrapper as the one way in (step 26c, <c>docs/design/29-entitlements-as-a-wrapper.md</c> §5,
/// D212, D213): what a core client puts on the wire, what the decorator adds, and that adding it
/// changes nothing about the plan.
/// </summary>
[Collection(SidecarCollection.Name)]
public sealed class EntitlementsWrapperTests(SharedSidecar sidecar)
{
    private const string Sql = "SELECT id, first_name FROM members ORDER BY id";

    private static TenancyFixture Fixture => TenancyFixture.Shared;

    /// <summary>
    /// A request built by <c>Chalk.Client</c> alone carries an empty extension slot — over an
    /// <em>entitled</em> catalog, which is the case worth checking: there is nothing about the
    /// catalog that makes a core client attach anything, because it has nothing to attach.
    /// </summary>
    [Fact]
    public async Task A_request_built_by_the_core_client_carries_an_empty_slot()
    {
        var spy = new RecordingPlanner(sidecar.CreatePlanner());
        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = TenancyFixture.ContextId,
            Sources = [Fixture.Source],
            Planner = spy,
        });

        _ = await engine.PrepareAsync(Sql, TenancyFixture.U1);

        Assert.Empty(Assert.Single(spy.Requests).Options.Extensions);
    }

    /// <summary>And the decorator's request carries exactly one, which is its options message.</summary>
    [Fact]
    public async Task The_decorator_attaches_exactly_one_extension()
    {
        var spy = new RecordingPlanner(sidecar.CreatePlanner());
        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = TenancyFixture.ContextId,
            Sources = [Fixture.Source],
            Planner = spy,
        });

        _ = await engine.WithEntitlements().PrepareAsync(Sql, TenancyFixture.U1);

        var attached = Assert.Single(Assert.Single(spy.Requests).Options.Extensions);
        Assert.Equal("chalk.v1.EntitlementsOptions", attached.Descriptor.FullName);
    }

    /// <summary>
    /// The decorator changes what the caller is <em>handed</em> and nothing about the plan: the same
    /// statement with and without it gives the same plan bytes, the same digest and the same report.
    /// The corpus is the same claim query by query and principal by principal; this is it stated
    /// once, directly.
    /// </summary>
    [Theory]
    [InlineData("u1")]
    [InlineData("u2")]
    [InlineData("u6")]
    public async Task The_decorator_gives_the_same_plan_the_same_digest_and_the_same_report(string principal)
    {
        await using var engine = await EngineAsync();
        var context = TenancyFixture.Principals.Single(p => p.Name == principal).Context;

        var bare = await engine.PrepareAsync(Sql, context);
        var entitled = await engine.WithEntitlements().PrepareAsync(Sql, context);

        Assert.Equal(bare.PlanDigest, entitled.PlanDigest);
        Assert.Equal(bare.Plan.ToByteArray(), entitled.Plan.ToByteArray());
        Assert.Equal(bare.Extensions, entitled.Query.Extensions);
    }

    /// <summary>
    /// A prepare <em>without</em> the decorator over an entitled catalog is not refused for the
    /// missing options: the design requires a <b>context</b>, not options
    /// (<c>16-entitlements.md</c> §2, D152; <c>29-entitlements-as-a-wrapper.md</c> §5). The pass is
    /// installed by the catalog and runs with every default, so the report comes back in the
    /// response's slot for anyone who can read it — and a prepare with no context at all is the
    /// refusal, because that asks for execute-time binding, which this planner does not serve.
    /// </summary>
    [Fact]
    public async Task Without_the_decorator_the_pass_still_runs_and_it_is_the_context_that_is_required()
    {
        await using var engine = await EngineAsync();

        var prepared = await engine.PrepareAsync(Sql, TenancyFixture.U1);
        var report = prepared.Extension<Chalk.Entitlements.Rpc.EntitlementsReport>();
        Assert.NotNull(report);
        Assert.Equal("members", Assert.Single(report.Tables).Table);

        var refusal = await Assert.ThrowsAsync<EntitlementException>(
            () => engine.PrepareAsync(Sql).AsTask());
        Assert.Contains("binds no execution context", refusal.Message, StringComparison.Ordinal);
    }

    private async Task<ChalkEngine> EngineAsync() =>
        await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = TenancyFixture.ContextId,
            Sources = [Fixture.Source],
            Planner = sidecar.CreatePlanner(),
        });

    /// <summary>A planner that keeps every request it forwarded, so a test can read the wire.</summary>
    private sealed class RecordingPlanner(IQueryPlanner inner) : IQueryPlanner
    {
        private readonly List<PlanRequest> _requests = [];

        internal IReadOnlyList<PlanRequest> Requests => _requests;

        public ValueTask<PlannerInfo> GetInfoAsync(CancellationToken ct = default) =>
            inner.GetInfoAsync(ct);

        public bool AcceptsCatalogDeltas => inner.AcceptsCatalogDeltas;

        public ValueTask RegisterCatalogAsync(
            CatalogRegistration registration, CancellationToken ct = default) =>
            inner.RegisterCatalogAsync(registration, ct);

        public ValueTask RegisterStatisticsAsync(
            StatisticsRegistration statistics, CancellationToken ct = default) =>
            inner.RegisterStatisticsAsync(statistics, ct);

        public ValueTask<PlanResult> PlanAsync(PlanRequest request, CancellationToken ct = default)
        {
            _requests.Add(request);
            return inner.PlanAsync(request, ct);
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}

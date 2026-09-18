using Chalk.Client;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// Both transports of D33 against a real sidecar: the same plan comes back over a Unix domain socket
/// and over TCP, and the two failure modes a <c>unix:</c> address adds — a path nothing is listening
/// on, and a relative path — are told apart (docs/design/09-unix-socket-transport.md §7).
/// </summary>
public sealed class TransportTests
{
    private static readonly CorpusFixture Fixture = CorpusFixture.Create(barMinutes: 2);

    [Theory]
    [InlineData(PlannerTransport.UnixSocket)]
    [InlineData(PlannerTransport.Tcp)]
    public async Task A_plan_comes_back_over_either_transport(PlannerTransport transport)
    {
        var jar = RepoLayout.PlannerJar;
        Assert.SkipWhen(jar is null, "no planner jar; run 'planner/gradlew -p planner shadowJar'");

        await using var sidecar = await PlannerProcess.StartAsync(
            new PlannerProcessOptions { JarPath = jar!.FullName, Transport = transport },
            TestContext.Current.CancellationToken);

        Assert.Equal(
            transport == PlannerTransport.UnixSocket ? "unix" : "http",
            sidecar.Address.Scheme);

        await using var planner = sidecar.CreatePlanner();
        var info = await planner.GetInfoAsync(TestContext.Current.CancellationToken);
        Assert.Equal(Chalk.Ir.IrVersion.Current, info.MaxIrVersion);

        await using var engine = await ChalkEngine.CreateAsync(
            new ChalkEngineOptions
            {
                ContextId = CorpusFixture.ContextId,
                Functions = CorpusFunctions.Register,
                Sources = Fixture.Sources,
                Planner = sidecar.CreatePlanner(),
            },
            TestContext.Current.CancellationToken);

        var prepared = await engine.PrepareAsync(
            "SELECT symbol, ts FROM bars ORDER BY ts", options: null, TestContext.Current.CancellationToken);
        Assert.NotEqual(0UL, prepared.PlanDigest);
    }

    /// <summary>
    /// The two transports are two ways to reach the same planner, so they must agree exactly: the
    /// digest is the plan's identity, and a transport that changed it would be a bug nothing else
    /// would catch.
    /// </summary>
    [Fact]
    public async Task The_transport_does_not_change_the_plan()
    {
        var jar = RepoLayout.PlannerJar;
        Assert.SkipWhen(jar is null, "no planner jar; run 'planner/gradlew -p planner shadowJar'");

        var digests = new List<ulong>();
        foreach (var transport in new[] { PlannerTransport.UnixSocket, PlannerTransport.Tcp })
        {
            await using var sidecar = await PlannerProcess.StartAsync(
                new PlannerProcessOptions { JarPath = jar!.FullName, Transport = transport },
                TestContext.Current.CancellationToken);
            await using var engine = await ChalkEngine.CreateAsync(
                new ChalkEngineOptions
                {
                    ContextId = CorpusFixture.ContextId,
                    Functions = CorpusFunctions.Register,
                    Sources = Fixture.Sources,
                    Planner = sidecar.CreatePlanner(),
                },
                TestContext.Current.CancellationToken);

            var prepared = await engine.PrepareAsync(
                "SELECT symbol, COUNT(*) FROM bars GROUP BY symbol",
                options: null,
                TestContext.Current.CancellationToken);
            digests.Add(prepared.PlanDigest);
        }

        Assert.Equal(digests[0], digests[1]);
    }

    [Fact]
    public async Task A_socket_nothing_is_listening_on_is_reported_as_unavailable_by_path()
    {
        var missing = Path.Combine("/tmp", $"chalk-absent-{Environment.ProcessId}.sock");
        File.Delete(missing);

        await using var planner = new GrpcQueryPlanner(
            new GrpcPlannerOptions { Address = new Uri("unix://" + missing) });

        var error = await Assert.ThrowsAsync<PlannerUnavailableException>(
            async () => await planner.GetInfoAsync(TestContext.Current.CancellationToken));

        Assert.Contains(missing, error.Message, StringComparison.Ordinal);
        Assert.Contains(missing, error.Address, StringComparison.Ordinal);
    }

    /// <summary>
    /// A relative path would be resolved against whichever working directory the host happens to
    /// have, so it is a configuration mistake, not a connection failure — and it is refused where the
    /// mistake was made.
    /// </summary>
    [Theory]
    [InlineData("unix://relative/chalk.sock")]
    [InlineData("unix:relative/chalk.sock")]
    [InlineData("unix://./chalk.sock")]
    public void A_relative_unix_address_is_rejected_at_construction(string address)
    {
        var error = Assert.Throws<ArgumentException>(
            () => new GrpcQueryPlanner(new GrpcPlannerOptions { Address = new Uri(address) }));

        Assert.Contains("absolute path", error.Message, StringComparison.Ordinal);
    }
}

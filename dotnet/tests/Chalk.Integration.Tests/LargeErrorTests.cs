using Chalk.Client;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// An error longer than the response headers a client reads by default (F174, D337). Before, it
/// aborted the HTTP/2 connection: the host saw <c>PlannerUnavailable</c> with the message lost,
/// and every call in flight beside it failed too. Now the planner cuts the message to its bound,
/// so the host sees the planning error itself, and the calls beside it are untouched, over either
/// transport.
/// </summary>
public sealed class LargeErrorTests
{
    private static readonly CorpusFixture Fixture = CorpusFixture.Create(barMinutes: 2);

    [Theory]
    [InlineData(PlannerTransport.UnixSocket)]
    [InlineData(PlannerTransport.Tcp)]
    public async Task An_error_over_64_KB_is_the_planning_error_and_harms_no_other_call(
        PlannerTransport transport)
    {
        var jar = RepoLayout.PlannerJar;
        Assert.SkipWhen(jar is null, "no planner jar; run 'planner/gradlew -p planner shadowJar'");
        var ct = TestContext.Current.CancellationToken;

        await using var sidecar = await PlannerProcess.StartAsync(
            new PlannerProcessOptions { JarPath = jar!.FullName, Transport = transport }, ct);
        await using var engine = await ChalkEngine.CreateAsync(
            new ChalkEngineOptions
            {
                ContextId = CorpusFixture.ContextId,
                Functions = CorpusFunctions.Register,
                Sources = Fixture.Sources,
                Planner = sidecar.CreatePlanner(),
            },
            ct);

        // Calcite names every argument's type when no function matches: about 88 KB here.
        var huge = "SELECT nosuch(" + string.Join(", ", Enumerable.Repeat("1", 8000)) + ") FROM bars";
        var neighbours = Enumerable.Range(0, 30)
            .Select(i => engine.PrepareAsync($"SELECT symbol, ts, {i} AS n FROM bars", options: null, ct).AsTask())
            .ToList();

        var error = await Assert.ThrowsAsync<PlanningException>(
            async () => await engine.PrepareAsync(huge, options: null, ct));

        Assert.Equal(ChalkErrorCodes.SqlValidation, error.Code);
        Assert.Contains("No match found for function signature", error.Message, StringComparison.Ordinal);
        Assert.Matches(@"… \(\d+ more characters\) \[SqlValidation\]$", error.Message);
        Assert.InRange(error.Message.Length, 4096, 4096 + 256);
        foreach (var neighbour in neighbours)
        {
            Assert.NotEqual(0UL, (await neighbour).PlanDigest);
        }
    }
}

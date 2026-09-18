using Chalk.Catalog;
using Chalk.Client;
using Chalk.Client.Rpc;
using Chalk.Sources;
using Chalk.Sources.Ado;
using Chalk.TestKit;
using DuckDB.NET.Data;

namespace Chalk.Integration.Tests;

/// <summary>
/// What a federated query does when it cannot answer (§5, §6 corpus 09 and 11): the local-join
/// guardrail refuses the plan, and a source that is not there produces one attributable error and no
/// rows at all.
/// </summary>
[Collection(SidecarCollection.Name)]
public sealed class FederationFailureTests(SharedSidecar sidecar)
{
    private static RemoteFixture Fixture => RemoteFixture.Shared;

    public static TheoryData<string> ErrorQueries()
    {
        var data = new TheoryData<string>();
        foreach (var query in CorpusQueries.LoadM9Errors())
        {
            data.Add(query.Name);
        }

        return data;
    }

    /// <summary>
    /// §6 corpus 09: a policy that caps what a local join may fetch refuses the plan at planning,
    /// naming the join and the estimate. Not at execution, and not after a minute of fetching.
    /// </summary>
    [Theory]
    [MemberData(nameof(ErrorQueries))]
    public async Task A_query_the_policy_forbids_fails_at_planning(string name)
    {
        Assert.SkipWhen(RemoteFixture.SkipReason is not null, RemoteFixture.SkipReason ?? string.Empty);

        var query = CorpusQueries.LoadM9Errors().Single(q => q.Name == name);
        await using var engine = await FederationCorpusTests.CreateEngineAsync(
            sidecar, ExecutionEngine.Vectorised);

        var failure = await Assert.ThrowsAsync<PlanningException>(
            () => engine.PrepareAsync(query.Sql, query.PrepareOptions()).AsTask());

        Assert.Equal(PlanErrorKind.Unsupported, failure.Kind);
        Assert.Contains("local_join_max_rows", failure.Message, StringComparison.Ordinal);
        Assert.Contains("fetched rows", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Exit criterion:</b> one source failing produces a clean, attributable error and never a
    /// partial result (§8, §6 corpus 11). The source here is a real DuckDB registration pointed at a
    /// path that cannot be opened, so the failure comes from the driver rather than from a stub.
    /// </summary>
    [Fact]
    public async Task A_source_that_is_not_there_faults_the_query_naming_it()
    {
        Assert.SkipWhen(RemoteFixture.SkipReason is not null, RemoteFixture.SkipReason ?? string.Empty);
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        var broken = BrokenSource();
        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = CorpusFixture.ContextId,
            Functions = CorpusFunctions.Register,
            Sources = [Fixture.Poco.Source, Fixture.Duck, broken],
            Planner = sidecar.CreatePlanner(),
        });

        var prepared = await engine.PrepareAsync(
            "SELECT s.symbol, b.v FROM symbols s JOIN down.bars_down b ON b.symbol = s.symbol",
            new PrepareOptions());

        await using var execution = await engine.ExecuteAsync(prepared, (IReadOnlyList<object?>?)null);
        var produced = 0;
        var failure = await Assert.ThrowsAnyAsync<ChalkException>(async () =>
        {
            await foreach (var batch in execution.Batches.WithCancellation(
                TestContext.Current.CancellationToken))
            {
                produced += batch.Length;
                batch.Dispose();
            }
        });

        Assert.Equal(0, produced);
        var attributed = Find<SourceExecutionException>(failure);
        Assert.NotNull(attributed);
        Assert.Equal("down", attributed.SourceId);
    }

    /// <summary>
    /// D98: there is no cross-source snapshot, so a host is told which moment each source was read
    /// at rather than being left to assume they agree.
    /// </summary>
    [Fact]
    public async Task Every_source_a_federated_query_read_is_in_the_fetch_stats()
    {
        Assert.SkipWhen(RemoteFixture.SkipReason is not null, RemoteFixture.SkipReason ?? string.Empty);

        var query = CorpusQueries.LoadM9().Single(q => q.Name == "04_three_sources");
        await using var engine = await FederationCorpusTests.CreateEngineAsync(
            sidecar, ExecutionEngine.Vectorised);
        var prepared = await engine.PrepareAsync(query.Sql, query.PrepareOptions());

        await using var execution = await engine.ExecuteAsync(prepared, (IReadOnlyList<object?>?)null);
        await foreach (var batch in execution.Batches.WithCancellation(
            TestContext.Current.CancellationToken))
        {
            batch.Dispose();
        }

        Assert.Contains("duck", execution.Stats.SourceFetches);
        Assert.Contains("sqlite", execution.Stats.SourceFetches);
        foreach (var entry in execution.Stats.SourceFetches)
        {
            Assert.True(entry.Value.Calls > 0);
            Assert.True(entry.Value.FirstFetch <= entry.Value.LastFetch);
        }
    }

    /// <summary>
    /// A DuckDB source whose file cannot be opened, registered by hand because discovery would fail
    /// first — the point is a source that is *declared* and then not there, which is what a database
    /// going down looks like to a plan that was made before it did.
    /// </summary>
    private static AdoSource BrokenSource()
    {
        var profile = DialectProfiles.DuckDb;
        var builder = new AdoSourceBuilder(
                "down",
                () => new DuckDBConnection("DataSource=/dev/null/nowhere/chalk-down.duckdb"),
                "down")
            .Dialect(profile)
            .Capabilities(AdoCapabilities.For(profile));

        builder.AddTable(
            "bars_down",
            [
                new ColumnDescriptor { Name = "symbol", Type = ChalkType.String() },
                new ColumnDescriptor { Name = "v", Type = ChalkType.Int64() },
            ]);

        return builder.Build();
    }

    private static T? Find<T>(Exception? failure)
        where T : Exception
    {
        for (var e = failure; e is not null; e = e.InnerException)
        {
            if (e is T found)
            {
                return found;
            }
        }

        return null;
    }
}

using Apache.Arrow;
using Chalk.Client;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// Interval arithmetic is sent only to a source whose dialect has an interval type (F238). SQLite has
/// none: before, a comparison over <c>ts + INTERVAL '6' HOUR</c> at a precision SQLite holds was sent
/// there as written, and failed with a syntax error. Now Chalk evaluates it on the rows SQLite gives,
/// and DuckDB, which has intervals, is still sent it.
/// </summary>
/// <remarks>
/// The <c>funding</c> rows are in SQLite and in DuckDB, a rate every eight hours from 1 January 2026;
/// two of them have no time.
/// </remarks>
public sealed class IntervalPushdownTests(SharedSidecar sidecar) : IClassFixture<SharedSidecar>
{
    private static readonly DateTime Now = new(2026, 1, 8, 0, 0, 0);

    /// <summary>
    /// The cut-off written with a cast, as a host wrote it to work around F192, and an interval added to
    /// a column: SQLite is sent neither and DuckDB both, and both keep the rows a cut-off six hours
    /// before the parameter keeps.
    /// </summary>
    [Fact]
    public async Task Interval_arithmetic_is_sent_only_where_the_dialect_has_intervals()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        Assert.SkipWhen(RemoteFixture.SkipReason is not null, RemoteFixture.SkipReason ?? string.Empty);
        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = CorpusFixture.ContextId,
            Functions = CorpusFunctions.Register,
            Sources = RemoteFixture.Shared.Sources,
            Planner = sidecar.CreatePlanner(),
        });
        var expected = Fixtures.FundingRates().Count(f => f.Ts >= Now.AddHours(-6));

        foreach (var condition in new[]
        {
            "CAST(ts AS TIMESTAMP(3)) >= CAST(? AS TIMESTAMP(3)) - INTERVAL '6' HOUR",
            "CAST(ts AS TIMESTAMP(0)) + INTERVAL '6' HOUR >= ?",
        })
        {
            var sqlite = await engine.PrepareAsync($"SELECT COUNT(*) AS n FROM sqlite.funding WHERE {condition}");
            var duck = await engine.PrepareAsync($"SELECT COUNT(*) AS n FROM duck.funding WHERE {condition}");

            Assert.DoesNotContain("INTERVAL", Assert.Single(TenancyRemoteBindingTests.RemoteSql(sqlite.Plan)), StringComparison.Ordinal);
            Assert.Contains("INTERVAL", Assert.Single(TenancyRemoteBindingTests.RemoteSql(duck.Plan)), StringComparison.Ordinal);
            Assert.Equal(expected, await CountAsync(engine, sqlite));
            Assert.Equal(expected, await CountAsync(engine, duck));
        }
    }

    private static async Task<long> CountAsync(ChalkEngine engine, PreparedQuery prepared)
    {
        long count = 0;
        await using var execution = await engine.ExecuteAsync(prepared, (IReadOnlyList<object?>)[Now]);
        await foreach (var batch in execution.Batches)
        {
            count += ((Int64Array)batch.Column(0)).GetValue(0)!.Value;
            batch.Dispose();
        }

        return count;
    }
}

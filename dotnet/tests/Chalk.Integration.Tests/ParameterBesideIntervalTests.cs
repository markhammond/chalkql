using Apache.Arrow;
using Chalk.Client;
using Chalk.Ir;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// A parameter beside an interval takes the timestamp its arithmetic is compared with (F192), and the
/// statement answers alike over every source. Before, <c>ts &gt;= @now - INTERVAL '6' HOUR</c> was
/// refused, because the parameter was typed as the interval, and a host wrote
/// <c>CAST(@now AS TIMESTAMP(9))</c> instead.
/// </summary>
/// <remarks>
/// The <c>funding</c> rows are in memory, in SQLite and in DuckDB, a rate every eight hours from
/// 1 January 2026; two of them have no time.
/// </remarks>
public sealed class ParameterBesideIntervalTests(SharedSidecar sidecar) : IClassFixture<SharedSidecar>
{
    /// <summary>A week into the rates: the window keeps the rates at and after this midnight.</summary>
    private static readonly DateTime Now = new(2026, 1, 8, 0, 0, 0);

    public static TheoryData<string> Tables() => new() { "funding", "sqlite.funding", "duck.funding" };

    /// <summary>
    /// The window F192 names, either way round, nested, and as the lower bound of a <c>BETWEEN</c>,
    /// over each source: the rows a cut-off six hours before the parameter keeps, which are those the
    /// <c>CAST</c> that worked around it keeps.
    /// </summary>
    [Theory]
    [MemberData(nameof(Tables))]
    public async Task A_window_on_a_parameter_keeps_the_same_rows_over_every_source(string table)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        Assert.SkipWhen(RemoteFixture.SkipReason is not null, RemoteFixture.SkipReason ?? string.Empty);
        await using var engine = await RemoteEngineAsync();
        var cutOff = Now.AddHours(-6);
        var expected = Fixtures.FundingRates().Count(f => f.Ts >= cutOff);

        string[] conditions =
        [
            "ts >= CAST(? AS TIMESTAMP(9)) - INTERVAL '6' HOUR",
            "ts >= ? - INTERVAL '6' HOUR",
            "? - INTERVAL '6' HOUR <= ts",
            "ts >= (? - INTERVAL '1' DAY) + INTERVAL '18' HOUR",
            "ts BETWEEN ? - INTERVAL '6' HOUR AND TIMESTAMP '2027-01-01 00:00:00'",
        ];
        foreach (var condition in conditions)
        {
            Assert.Equal(expected, await CountAsync(engine, $"SELECT COUNT(*) AS n FROM {table} WHERE {condition}"));
        }
    }

    /// <summary>
    /// A named parameter, as a host writes one, is the timestamp of the column its window is on, and
    /// both executors keep the same rows with it.
    /// </summary>
    [Theory]
    [InlineData(ExecutionEngine.Vectorised)]
    [InlineData(ExecutionEngine.Reference)]
    public async Task A_named_parameter_beside_an_interval_is_a_timestamp(ExecutionEngine executor)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        Assert.SkipWhen(RemoteFixture.SkipReason is not null, RemoteFixture.SkipReason ?? string.Empty);
        await using var engine = await RemoteEngineAsync(executor);

        var prepared = await engine.PrepareAsync("SELECT COUNT(*) AS n FROM funding WHERE ts >= @now - INTERVAL '6' HOUR");

        var parameter = Assert.Single(prepared.ParameterTypes);
        Assert.Equal((TypeKind.Timestamp, 9), (parameter.Kind, parameter.Precision));
        Assert.Equal(Fixtures.FundingRates().Count(f => f.Ts >= Now.AddHours(-6)), await CountAsync(engine, prepared));
    }

    /// <summary>
    /// Where the column's precision lets the comparison be sent, the parameter's arithmetic goes to
    /// DuckDB and never to SQLite, which has no interval (F238): both keep the window's rows.
    /// </summary>
    [Fact]
    public async Task A_parameter_beside_an_interval_is_never_sent_to_sqlite()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        Assert.SkipWhen(RemoteFixture.SkipReason is not null, RemoteFixture.SkipReason ?? string.Empty);
        await using var engine = await RemoteEngineAsync();
        const string Condition = "CAST(ts AS TIMESTAMP(3)) >= ? - INTERVAL '6' HOUR";

        var sqlite = await engine.PrepareAsync($"SELECT COUNT(*) AS n FROM sqlite.funding WHERE {Condition}");
        var duck = await engine.PrepareAsync($"SELECT COUNT(*) AS n FROM duck.funding WHERE {Condition}");

        Assert.DoesNotContain("INTERVAL", Assert.Single(TenancyRemoteBindingTests.RemoteSql(sqlite.Plan)), StringComparison.Ordinal);
        Assert.Contains("? - INTERVAL '6' HOUR", Assert.Single(TenancyRemoteBindingTests.RemoteSql(duck.Plan)), StringComparison.Ordinal);
        var expected = Fixtures.FundingRates().Count(f => f.Ts >= Now.AddHours(-6));
        Assert.Equal(expected, await CountAsync(engine, sqlite));
        Assert.Equal(expected, await CountAsync(engine, duck));
    }

    private async Task<ChalkEngine> RemoteEngineAsync(ExecutionEngine executor = ExecutionEngine.Vectorised) =>
        await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = CorpusFixture.ContextId,
            Functions = CorpusFunctions.Register,
            Sources = RemoteFixture.Shared.Sources,
            Planner = sidecar.CreatePlanner(),
            Execution = new ExecutionOptions { Engine = executor },
        });

    private static async Task<long> CountAsync(ChalkEngine engine, string sql) =>
        await CountAsync(engine, await engine.PrepareAsync(sql));

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

using Chalk.Client;
using Chalk.Sources.Poco;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// Text that writes a zone, cast to a timestamp, is read in no machine's time zone (F240), as DuckDB
/// and PostgreSQL read it. A <c>TIMESTAMP</c> is the wall clock as written, the zone dropped, and a
/// timestamp with a time zone is the instant the zone names. Text that writes no zone is read as
/// written, as UTC. Before, both executors turned a written zone into the machine's own wall clock:
/// <c>12:00+02:00</c> was 10:00 on a machine set to UTC and 19:00 on one set to Tokyo.
/// </summary>
public sealed class TimestampOffsetTests(SharedSidecar sidecar) : IClassFixture<SharedSidecar>
{
    public sealed record Written(int Id, string Txt);

    /// <summary>No zone; an offset east of UTC; a Z; an offset west of it; and a fraction at UTC.</summary>
    private static readonly IReadOnlyList<Written> Texts =
    [
        new(1, "2026-01-08 12:00:00"),
        new(2, "2026-01-08 12:00:00+02:00"),
        new(3, "2026-01-08T12:00:00Z"),
        new(4, "2026-01-08 12:00:00-05:30"),
        new(5, "2026-01-08 12:00:00.5+00"),
    ];

    /// <summary>A <c>TIMESTAMP</c> is the wall clock each text writes, whatever zone it names.</summary>
    [Theory]
    [InlineData(ExecutionEngine.Vectorised)]
    [InlineData(ExecutionEngine.Reference)]
    public async Task A_timestamp_is_the_wall_clock_as_written(ExecutionEngine executor)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await EngineAsync(executor);

        Assert.Equal(
            [
                "1|2026-01-08 12:00:00",
                "2|2026-01-08 12:00:00",
                "3|2026-01-08 12:00:00",
                "4|2026-01-08 12:00:00",
                "5|2026-01-08 12:00:00.5",
            ],
            await RowsAsync(engine, "SELECT id, CAST(CAST(txt AS TIMESTAMP) AS VARCHAR) FROM texts ORDER BY id"));
    }

    /// <summary>
    /// A timestamp with a time zone is the instant each text names, written here as its wall clock in
    /// UTC: the zone applied where one is written, and the text as written where none is.
    /// </summary>
    [Theory]
    [InlineData(ExecutionEngine.Vectorised)]
    [InlineData(ExecutionEngine.Reference)]
    public async Task A_timestamp_with_a_time_zone_is_the_instant_written(ExecutionEngine executor)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await EngineAsync(executor);

        Assert.Equal(
            [
                "1|2026-01-08 12:00:00",
                "2|2026-01-08 10:00:00",
                "3|2026-01-08 12:00:00",
                "4|2026-01-08 17:30:00",
                "5|2026-01-08 12:00:00.5",
            ],
            await RowsAsync(
                engine,
                "SELECT id, CAST(CAST(CAST(txt AS TIMESTAMP WITH LOCAL TIME ZONE) AS TIMESTAMP) AS VARCHAR) "
                + "FROM texts ORDER BY id"));
    }

    /// <summary>
    /// A timestamp with a time zone is written as its wall clock in UTC and the zone, as DuckDB and
    /// PostgreSQL write one in a session at UTC, and its text reads back as the same instant.
    /// </summary>
    [Theory]
    [InlineData(ExecutionEngine.Vectorised)]
    [InlineData(ExecutionEngine.Reference)]
    public async Task A_timestamp_with_a_time_zone_is_written_with_its_zone(ExecutionEngine executor)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await EngineAsync(executor);

        Assert.Equal(
            [
                "1|2026-01-08 12:00:00+00|True",
                "2|2026-01-08 10:00:00+00|True",
                "3|2026-01-08 12:00:00+00|True",
                "4|2026-01-08 17:30:00+00|True",
                "5|2026-01-08 12:00:00.5+00|True",
            ],
            await RowsAsync(
                engine,
                "SELECT id, CAST(CAST(txt AS TIMESTAMP WITH LOCAL TIME ZONE) AS VARCHAR), "
                + "CAST(CAST(CAST(txt AS TIMESTAMP WITH LOCAL TIME ZONE) AS VARCHAR) AS TIMESTAMP WITH LOCAL TIME ZONE) "
                + "= CAST(txt AS TIMESTAMP WITH LOCAL TIME ZONE) FROM texts ORDER BY id"));
    }

    /// <summary>
    /// Text written in the statement, and text bound to a parameter as text, are read the same way. A
    /// parameter cast straight to a timestamp is a timestamp, which a string does not bind to.
    /// </summary>
    [Theory]
    [InlineData(ExecutionEngine.Vectorised)]
    [InlineData(ExecutionEngine.Reference)]
    public async Task Text_in_the_statement_or_bound_to_a_parameter_is_read_the_same(ExecutionEngine executor)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await EngineAsync(executor);

        Assert.Equal(
            ["True|True"],
            await RowsAsync(
                engine,
                "SELECT CAST('2026-01-08 12:00:00+02:00' AS TIMESTAMP) = TIMESTAMP '2026-01-08 12:00:00', "
                + "CAST(CAST(CAST(? AS VARCHAR) AS TIMESTAMP WITH LOCAL TIME ZONE) AS TIMESTAMP) = TIMESTAMP '2026-01-08 10:00:00' "
                + "FROM texts WHERE id = 1",
                ["2026-01-08 12:00:00+02:00"]));
    }

    /// <summary>
    /// Over SQLite and DuckDB the text is read by Chalk, as in memory, and neither database is sent the
    /// cast. SQLite has no timestamp type and would read the text as a number.
    /// </summary>
    [Fact]
    public async Task Text_is_read_the_same_over_every_source()
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
        const string Text =
            "SELECT CAST(CASE WHEN symbol = 'BTCUSDT' THEN '2026-01-08 12:00:00+02:00' END AS TIMESTAMP) "
            + "= TIMESTAMP '2026-01-08 12:00:00' AS wall, "
            + "CAST(CAST(CASE WHEN symbol = 'BTCUSDT' THEN '2026-01-08 12:00:00+02:00' END "
            + "AS TIMESTAMP WITH LOCAL TIME ZONE) AS TIMESTAMP) = TIMESTAMP '2026-01-08 10:00:00' AS instant "
            + "FROM {0} WHERE symbol = 'BTCUSDT' ORDER BY ts LIMIT 1";

        Assert.Equal(
            ["True|True"],
            await RowsAsync(engine, string.Format(System.Globalization.CultureInfo.InvariantCulture, Text, "funding")));
        foreach (var table in new[] { "sqlite.funding", "duck.funding" })
        {
            var sql = string.Format(System.Globalization.CultureInfo.InvariantCulture, Text, table);
            var prepared = await engine.PrepareAsync(sql);

            Assert.DoesNotContain("CAST", Assert.Single(TenancyRemoteBindingTests.RemoteSql(prepared.Plan)), StringComparison.Ordinal);
            Assert.Equal(["True|True"], await RowsAsync(engine, sql));
        }
    }

    private async Task<ChalkEngine> EngineAsync(ExecutionEngine executor) =>
        await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "timestamp-offsets",
            Sources =
            [
                new PocoSourceBuilder("mem").NamingPolicy(PocoNamingPolicy.SnakeCase).AddTable("texts", Texts).Build(),
            ],
            Planner = sidecar.CreatePlanner(),
            Execution = new ExecutionOptions { Engine = executor },
        });

    private static async Task<List<string>> RowsAsync(
        ChalkEngine engine, string sql, IReadOnlyList<object?>? parameters = null)
    {
        var rows = new List<string>();
        await using var execution = await engine.ExecuteAsync(await engine.PrepareAsync(sql), parameters ?? []);
        await foreach (var batch in execution.Batches)
        {
            rows.AddRange(BatchReader.ToRows(batch).Select(row => string.Join("|", row)));
            batch.Dispose();
        }

        return rows;
    }
}

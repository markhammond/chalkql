using Chalk.Client;
using Chalk.Sources.Poco;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// A date, a time or a timestamp cast to text is written as PostgreSQL and DuckDB write one (F193):
/// <c>2026-01-08</c>, <c>12:34:56</c>, <c>2026-01-08 12:34:56</c>, a fraction of a second after a
/// point with its trailing zeros trimmed, and none for a whole second. Chalk writes it whichever
/// source holds the value, so the cast is never sent to one. Before, the cast was refused in memory
/// and sent to SQLite and DuckDB, which each wrote their own text. A timestamp cast to <c>TIME</c> is
/// its time of day (F191).
/// </summary>
public sealed class TemporalTextTests(SharedSidecar sidecar) : IClassFixture<SharedSidecar>
{
    public sealed record Moment(int Id, DateOnly OnDay, TimeOnly Clock, DateTime Stamp, DateTime? Missing);

    /// <summary>A whole second; a half and a hundredth; and a microsecond's fraction just before 1970.</summary>
    private static readonly IReadOnlyList<Moment> Moments =
    [
        new(1, new DateOnly(2026, 1, 8), new TimeOnly(12, 34, 56), new DateTime(2026, 1, 8, 0, 0, 0), null),
        new(2, new DateOnly(99, 1, 8), new TimeOnly(12, 34, 56, 500), new DateTime(2026, 1, 8, 0, 0, 0, 120), null),
        new(3, new DateOnly(2026, 12, 31), new TimeOnly(23, 59, 59).Add(TimeSpan.FromTicks(1_234_560)), new DateTime(1969, 12, 31, 23, 59, 59).AddTicks(1_234_567), null),
    ];

    /// <summary>
    /// The texts DuckDB writes for the same values, from either executor: the date's four-digit year,
    /// the fraction trimmed, the timestamp's own precision, and a NULL left NULL.
    /// </summary>
    [Theory]
    [InlineData(ExecutionEngine.Vectorised)]
    [InlineData(ExecutionEngine.Reference)]
    public async Task A_date_a_time_and_a_timestamp_are_written_as_iso_text(ExecutionEngine executor)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await EngineAsync(executor);

        Assert.Equal(
            [
                "1|2026-01-08|12:34:56|2026-01-08 00:00:00|2026-01-08 00:00:00|",
                "2|0099-01-08|12:34:56.5|2026-01-08 00:00:00.12|2026-01-08 00:00:00.12|",
                "3|2026-12-31|23:59:59.123456|1969-12-31 23:59:59.1234567|1969-12-31 23:59:59.123|",
            ],
            await RowsAsync(
                engine,
                "SELECT id, CAST(on_day AS VARCHAR), CAST(clock AS VARCHAR), CAST(stamp AS VARCHAR), "
                + "CAST(CAST(stamp AS TIMESTAMP(3)) AS VARCHAR), CAST(missing AS VARCHAR) FROM moments ORDER BY id"));
    }

    /// <summary>The text read back is the value it was written from, to the last digit.</summary>
    [Theory]
    [InlineData(ExecutionEngine.Vectorised)]
    [InlineData(ExecutionEngine.Reference)]
    public async Task The_text_reads_back_as_the_same_value(ExecutionEngine executor)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await EngineAsync(executor);

        Assert.Equal(
            ["1|True|True|True", "2|True|True|True", "3|True|True|True"],
            await RowsAsync(
                engine,
                "SELECT id, CAST(CAST(on_day AS VARCHAR) AS DATE) = on_day, "
                + "CAST(CAST(clock AS VARCHAR) AS TIME(6)) = clock, "
                + "CAST(CAST(stamp AS VARCHAR) AS TIMESTAMP(9)) = stamp FROM moments ORDER BY id"));
    }

    /// <summary>
    /// A timestamp cast to <c>TIME</c> is its time of day, cut to the time's precision: a <c>TIME</c>
    /// with none written has none, so it is a time to the second (F191). A time cast to a coarser one is
    /// cut the same way.
    /// </summary>
    [Theory]
    [InlineData(ExecutionEngine.Vectorised)]
    [InlineData(ExecutionEngine.Reference)]
    public async Task A_timestamp_cast_to_time_is_its_time_of_day(ExecutionEngine executor)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await EngineAsync(executor);

        Assert.Equal(
            [
                "1|00:00:00|00:00:00|12:34:56",
                "2|00:00:00|00:00:00.12|12:34:56",
                "3|23:59:59|23:59:59.123456|23:59:59",
            ],
            await RowsAsync(
                engine,
                "SELECT id, CAST(CAST(stamp AS TIME) AS VARCHAR), CAST(CAST(stamp AS TIME(6)) AS VARCHAR), "
                + "CAST(CAST(clock AS TIME(0)) AS VARCHAR) FROM moments ORDER BY id"));
    }

    /// <summary>
    /// Over SQLite and DuckDB the text is Chalk's, the same as the in-memory table's, and neither
    /// database is sent the cast. Before, each was sent it and wrote its own.
    /// </summary>
    [Fact]
    public async Task A_timestamp_is_the_same_text_over_every_source()
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
        const string Text = "SELECT CAST(ts AS VARCHAR) AS t FROM {0} WHERE symbol = 'BTCUSDT' AND ts IS NOT NULL ORDER BY ts LIMIT 3";

        var memory = await RowsAsync(engine, string.Format(System.Globalization.CultureInfo.InvariantCulture, Text, "funding"));
        Assert.Equal(["2026-01-01 00:00:00", "2026-01-01 08:00:00", "2026-01-01 16:00:00"], memory);
        foreach (var table in new[] { "sqlite.funding", "duck.funding" })
        {
            var sql = string.Format(System.Globalization.CultureInfo.InvariantCulture, Text, table);
            var prepared = await engine.PrepareAsync(sql);

            Assert.DoesNotContain("CAST", Assert.Single(TenancyRemoteBindingTests.RemoteSql(prepared.Plan)), StringComparison.Ordinal);
            Assert.Equal(memory, await RowsAsync(engine, sql));
        }
    }

    private async Task<ChalkEngine> EngineAsync(ExecutionEngine executor) =>
        await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "temporal-text",
            Sources =
            [
                new PocoSourceBuilder("mem").NamingPolicy(PocoNamingPolicy.SnakeCase).AddTable("moments", Moments).Build(),
            ],
            Planner = sidecar.CreatePlanner(),
            Execution = new ExecutionOptions { Engine = executor },
        });

    private static async Task<List<string>> RowsAsync(ChalkEngine engine, string sql)
    {
        var rows = new List<string>();
        await using var execution = await engine.ExecuteAsync(await engine.PrepareAsync(sql), (IReadOnlyList<object?>)[]);
        await foreach (var batch in execution.Batches)
        {
            rows.AddRange(BatchReader.ToRows(batch).Select(row => string.Join("|", row)));
            batch.Dispose();
        }

        return rows;
    }
}

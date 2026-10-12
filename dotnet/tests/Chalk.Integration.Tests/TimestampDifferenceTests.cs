using Apache.Arrow;
using Chalk.Client;
using Chalk.Sources.Poco;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// The difference between two timestamps (D337, F175): <c>TIMESTAMPDIFF(unit, from, to)</c>, the
/// standard <c>(to - from) unit</c>, and an interval compared with an interval. Calcite accepted all
/// of them and the executor ran none. A count is of whole units, truncated toward zero, and months
/// count only once the later day and time of month has reached the earlier's. Both engines answer.
/// </summary>
public sealed class TimestampDifferenceTests(SharedSidecar sidecar) : IClassFixture<SharedSidecar>
{
    [Theory]
    [MemberData(nameof(Counts))]
    public async Task Timestampdiff_counts_whole_units_truncated_toward_zero(
        ExecutionEngine executor, string unit, int pair, long expected)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await CreateEngineAsync(executor);

        var value = await ScalarAsync(
            engine, $"SELECT TIMESTAMPDIFF({unit}, a, b) AS n FROM pairs WHERE id = {pair}");

        Assert.Equal(expected, Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture));
    }

    public static TheoryData<ExecutionEngine, string, int, long> Counts()
    {
        var data = new TheoryData<ExecutionEngine, string, int, long>();
        foreach (var executor in new[] { ExecutionEngine.Vectorised, ExecutionEngine.Reference })
        {
            // Pair 1: 1 day, 2 hours, 3 minutes, 4.5 seconds later.
            data.Add(executor, "MICROSECOND", 1, 93_784_500_000L);
            data.Add(executor, "MILLISECOND", 1, 93_784_500L);
            data.Add(executor, "SECOND", 1, 93_784L);
            data.Add(executor, "MINUTE", 1, 1_563L);
            data.Add(executor, "HOUR", 1, 26L);
            data.Add(executor, "DAY", 1, 1L);
            data.Add(executor, "WEEK", 1, 0L);

            // Pair 2: 90 seconds earlier, which truncates toward zero.
            data.Add(executor, "SECOND", 2, -90L);
            data.Add(executor, "MINUTE", 2, -1L);

            // Pair 3: 31 January to 28 February is no month; pair 4, to 1 March, is one.
            data.Add(executor, "MONTH", 3, 0L);
            data.Add(executor, "MONTH", 4, 1L);

            // Pair 5: two years and seven months later, and back again for pair 6.
            data.Add(executor, "MONTH", 5, 31L);
            data.Add(executor, "QUARTER", 5, 10L);
            data.Add(executor, "YEAR", 5, 2L);
            data.Add(executor, "YEAR", 6, -2L);
        }

        return data;
    }

    [Theory]
    [InlineData(ExecutionEngine.Vectorised)]
    [InlineData(ExecutionEngine.Reference)]
    public async Task A_date_counts_against_a_timestamp_as_its_midnight(ExecutionEngine executor)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await CreateEngineAsync(executor);

        var hours = await ScalarAsync(engine, "SELECT TIMESTAMPDIFF(HOUR, d, b) AS n FROM pairs WHERE id = 1");

        // From midnight on the 1st to 02:03:04.5 on the 2nd.
        Assert.Equal(26L, Convert.ToInt64(hours, System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>The standard form: an interval, and the same count when it is cast to an integer.</summary>
    [Theory]
    [InlineData(ExecutionEngine.Vectorised)]
    [InlineData(ExecutionEngine.Reference)]
    public async Task The_standard_form_is_an_interval_and_its_cast_a_count(ExecutionEngine executor)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await CreateEngineAsync(executor);

        var interval = await ScalarAsync(engine, "SELECT (b - a) DAY TO SECOND AS i FROM pairs WHERE id = 1");
        var minutes = await ScalarAsync(engine, "SELECT CAST((b - a) MINUTE AS BIGINT) AS n FROM pairs WHERE id = 1");
        var months = await ScalarAsync(engine, "SELECT CAST((b - a) MONTH AS INTEGER) AS n FROM pairs WHERE id = 5");

        Assert.Equal(new TimeSpan(1, 2, 3, 4, 500), interval);
        Assert.Equal(1_563L, Convert.ToInt64(minutes, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(31L, Convert.ToInt64(months, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData(ExecutionEngine.Vectorised)]
    [InlineData(ExecutionEngine.Reference)]
    public async Task An_interval_compares_with_an_interval(ExecutionEngine executor)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await CreateEngineAsync(executor);

        var dayTime = await IdsAsync(engine, "SELECT id FROM pairs WHERE (b - a) SECOND < INTERVAL '1' HOUR ORDER BY id");
        var yearMonth = await IdsAsync(engine, "SELECT id FROM pairs WHERE (b - a) MONTH >= INTERVAL '1' MONTH ORDER BY id");

        Assert.Equal([2, 6], dayTime);
        Assert.Equal([4, 5], yearMonth);
    }

    private static readonly Pair[] Pairs =
    [
        new(1, new DateTime(2026, 10, 1), new DateTime(2026, 10, 2, 2, 3, 4, 500), new DateOnly(2026, 10, 1)),
        new(2, new DateTime(2026, 10, 1, 12, 0, 0), new DateTime(2026, 10, 1, 11, 58, 30), new DateOnly(2026, 10, 1)),
        new(3, new DateTime(2026, 1, 31), new DateTime(2026, 2, 28), new DateOnly(2026, 1, 31)),
        new(4, new DateTime(2026, 1, 31), new DateTime(2026, 3, 1), new DateOnly(2026, 1, 31)),
        new(5, new DateTime(2024, 3, 15, 8, 0, 0), new DateTime(2026, 10, 15, 9, 0, 0), new DateOnly(2024, 3, 15)),
        new(6, new DateTime(2026, 10, 15, 9, 0, 0), new DateTime(2024, 3, 15, 8, 0, 0), new DateOnly(2026, 10, 15)),
    ];

    private async Task<ChalkEngine> CreateEngineAsync(ExecutionEngine executor) =>
        await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "timestamp-differences",
            Sources =
            [
                new PocoSourceBuilder("mem")
                    .NamingPolicy(PocoNamingPolicy.SnakeCase)
                    .AddTable("pairs", Pairs)
                    .Build(),
            ],
            Planner = sidecar.CreatePlanner(),
            Execution = new ExecutionOptions { Engine = executor },
        });

    private static async Task<object?> ScalarAsync(ChalkEngine engine, string sql)
    {
        object? value = null;
        var rows = 0;
        var prepared = await engine.PrepareAsync(sql);
        await using var execution = await engine.ExecuteAsync(prepared, (IReadOnlyList<object?>)[]);
        await foreach (var batch in execution.Batches)
        {
            for (var i = 0; i < batch.Length; i++)
            {
                rows++;
                value = batch.Column(0) switch
                {
                    Int32Array ints => ints.GetValue(i),
                    Int64Array longs => longs.GetValue(i),
                    DurationArray durations => durations.GetTimeSpan(i),
                    var other => throw new InvalidOperationException($"unexpected {other.GetType().Name}"),
                };
            }

            batch.Dispose();
        }

        Assert.Equal(1, rows);
        return value;
    }

    private static async Task<List<int>> IdsAsync(ChalkEngine engine, string sql)
    {
        var ids = new List<int>();
        var prepared = await engine.PrepareAsync(sql);
        await using var execution = await engine.ExecuteAsync(prepared, (IReadOnlyList<object?>)[]);
        await foreach (var batch in execution.Batches)
        {
            var column = (Int32Array)batch.Column(0);
            for (var i = 0; i < batch.Length; i++)
            {
                ids.Add(column.GetValue(i)!.Value);
            }

            batch.Dispose();
        }

        return ids;
    }

    public sealed record Pair(int Id, DateTime A, DateTime B, DateOnly D);
}

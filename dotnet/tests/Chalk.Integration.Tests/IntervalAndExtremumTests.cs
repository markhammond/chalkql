using System.Globalization;
using Apache.Arrow;
using Chalk.Catalog;
using Chalk.Client;
using Chalk.Sources.Poco;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// Interval arithmetic, and <c>LEAST</c> and <c>GREATEST</c> (D338, design 63 §6, F185, F186). A
/// local cut-off converted at a zone's offset is <c>opens_local - utc_offset_minutes * INTERVAL '1'
/// MINUTE</c>, and today's close is <c>LEAST(closes, @now)</c>. Neither ran: the executor did no
/// arithmetic on intervals, and Calcite's standard table has no <c>LEAST</c>. Both engines answer.
/// </summary>
public sealed class IntervalAndExtremumTests(SharedSidecar sidecar) : IClassFixture<SharedSidecar>
{
    private static readonly DateTime Local = new(2026, 10, 11, 17, 0, 0);

    [Theory]
    [InlineData(ExecutionEngine.Vectorised)]
    [InlineData(ExecutionEngine.Reference)]
    public async Task A_number_times_an_interval_moves_a_timestamp(ExecutionEngine executor)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await CreateEngineAsync(executor);

        var utc = await TimestampsAsync(
            engine, "SELECT opens_local - utc_offset_minutes * INTERVAL '1' MINUTE AS t FROM zones ORDER BY id");

        // New York at -240 minutes, Tokyo at +540.
        Assert.Equal([Local.AddHours(4), Local.AddHours(-9)], utc);
    }

    [Theory]
    [InlineData(ExecutionEngine.Vectorised)]
    [InlineData(ExecutionEngine.Reference)]
    public async Task An_interval_scales_divides_adds_and_negates(ExecutionEngine executor)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await CreateEngineAsync(executor);

        var spans = await SpansAsync(
            engine,
            "SELECT (closes_local - opens_local) MINUTE * 1.5 AS a, (closes_local - opens_local) MINUTE / 7 AS b, "
            + "(closes_local - opens_local) MINUTE + utc_offset_minutes * INTERVAL '1' MINUTE AS c, "
            + "-((closes_local - opens_local) MINUTE) AS d FROM zones WHERE id = 1");

        Assert.Equal(TimeSpan.FromHours(36), spans[0]);
        // 86 400 000 000 µs / 7 = 12 342 857 142.857…, rounded half-even to the microsecond.
        Assert.Equal(TimeSpan.FromTicks(12_342_857_143L * 10), spans[1]);
        Assert.Equal(TimeSpan.FromHours(20), spans[2]);
        Assert.Equal(TimeSpan.FromHours(-24), spans[3]);
    }

    [Theory]
    [InlineData(ExecutionEngine.Vectorised)]
    [InlineData(ExecutionEngine.Reference)]
    public async Task Least_and_greatest_skip_nulls(ExecutionEngine executor)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await CreateEngineAsync(executor);
        var now = new DateTime(2026, 10, 12, 9, 30, 0);
        var prepared = await engine.PrepareAsync(
            "SELECT LEAST(closes_local, CAST(? AS TIMESTAMP(9))) AS l, GREATEST(closes_local, CAST(? AS TIMESTAMP(9))) AS g, "
            + "LEAST(name, 'M', NULL) AS t, GREATEST(utc_offset_minutes, NULL, 0) AS n FROM zones ORDER BY id",
            new PrepareOptions { ParameterTypes = [ChalkType.Timestamp(9, nullable: true), ChalkType.Timestamp(9, nullable: true)] });

        var rows = await RowsAsync(engine, prepared, [now, null]);

        Assert.Equal(
            [
                $"{Stamp(now)}|{Stamp(Local.AddHours(24))}|M|0",
                $"{Stamp(now)}|{Stamp(Local.AddHours(24))}|M|540",
            ],
            rows);
    }

    private static readonly Zone[] Zones =
    [
        new(1, "New York", Local, Local.AddHours(24), -240),
        new(2, "Tokyo", Local, Local.AddHours(24), 540),
    ];

    private async Task<ChalkEngine> CreateEngineAsync(ExecutionEngine executor) =>
        await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "interval-and-extremum",
            Sources = [new PocoSourceBuilder("mem").NamingPolicy(PocoNamingPolicy.SnakeCase).AddTable("zones", Zones).Build()],
            Planner = sidecar.CreatePlanner(),
            Execution = new ExecutionOptions { Engine = executor },
        });

    private static async Task<List<DateTime>> TimestampsAsync(ChalkEngine engine, string sql)
    {
        var values = new List<DateTime>();
        var prepared = await engine.PrepareAsync(sql);
        await using var execution = await engine.ExecuteAsync(prepared, (IReadOnlyList<object?>)[]);
        await foreach (var batch in execution.Batches)
        {
            var column = (TimestampArray)batch.Column(0);
            for (var i = 0; i < batch.Length; i++)
            {
                values.Add(column.GetTimestamp(i)!.Value.UtcDateTime);
            }

            batch.Dispose();
        }

        return values;
    }

    private static async Task<List<TimeSpan>> SpansAsync(ChalkEngine engine, string sql)
    {
        var values = new List<TimeSpan>();
        var prepared = await engine.PrepareAsync(sql);
        await using var execution = await engine.ExecuteAsync(prepared, (IReadOnlyList<object?>)[]);
        await foreach (var batch in execution.Batches)
        {
            for (var c = 0; c < batch.ColumnCount; c++)
            {
                values.Add(((DurationArray)batch.Column(c)).GetTimeSpan(0)!.Value);
            }

            batch.Dispose();
        }

        return values;
    }

    private static async Task<List<string>> RowsAsync(ChalkEngine engine, PreparedQuery prepared, object?[] values)
    {
        var rows = new List<string>();
        await using var execution = await engine.ExecuteAsync(prepared, (IReadOnlyList<object?>)values);
        await foreach (var batch in execution.Batches)
        {
            for (var i = 0; i < batch.Length; i++)
            {
                rows.Add(string.Join(
                    '|',
                    Stamp(((TimestampArray)batch.Column(0)).GetTimestamp(i)!.Value.UtcDateTime),
                    Stamp(((TimestampArray)batch.Column(1)).GetTimestamp(i)!.Value.UtcDateTime),
                    batch.Column(2) switch
                    {
                        StringArray text => text.GetString(i),
                        StringViewArray view => view.GetString(i),
                        var other => other.GetType().Name,
                    },
                    batch.Column(3) switch
                    {
                        Int32Array ints => ints.GetValue(i)!.Value.ToString(CultureInfo.InvariantCulture),
                        Int64Array longs => longs.GetValue(i)!.Value.ToString(CultureInfo.InvariantCulture),
                        var other => other.GetType().Name,
                    }));
            }

            batch.Dispose();
        }

        return rows;
    }

    private static string Stamp(DateTime value) =>
        value.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture);

    public sealed record Zone(int Id, string Name, DateTime OpensLocal, DateTime ClosesLocal, int UtcOffsetMinutes);
}

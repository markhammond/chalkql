using Apache.Arrow;
using Apache.Arrow.Types;
using Chalk.Client;
using Chalk.Sources.Poco;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// <c>CURRENT_TIMESTAMP</c>, <c>LOCALTIMESTAMP</c> and <c>CURRENT_DATE</c> (D337, F173). Each reads
/// the execution's one instant, taken from <see cref="ExecutionOptions.TimeProvider"/> as the
/// execution starts, as a UTC wall clock. A timestamp's precision is 6 unless one is written. Before,
/// the planner accepted the first and last, the executor ran neither, and the planner refused
/// <c>LOCALTIMESTAMP</c>.
/// </summary>
public sealed class CurrentTimeTests(SharedSidecar sidecar) : IClassFixture<SharedSidecar>
{
    /// <summary>
    /// 22:34:56.1234567 on 12 October in UTC, written in a zone ten hours ahead, where it is already
    /// the 13th.
    /// </summary>
    private static readonly DateTimeOffset Instant =
        new DateTimeOffset(2026, 10, 12, 22, 34, 56, TimeSpan.Zero).AddTicks(1_234_567).ToOffset(TimeSpan.FromHours(10));

    /// <summary>The instant as a UTC wall clock, the way a TIMESTAMP column holds one.</summary>
    private static readonly DateTime Wall = new DateTime(2026, 10, 12, 22, 34, 56).AddTicks(1_234_567);

    [Theory]
    [InlineData(ExecutionEngine.Vectorised)]
    [InlineData(ExecutionEngine.Reference)]
    public async Task Each_function_reads_the_instant_as_a_UTC_wall_clock(ExecutionEngine executor)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await CreateEngineAsync(new Clock(Instant), executor);
        var prepared = await engine.PrepareAsync(
            "SELECT CURRENT_TIMESTAMP AS standard, LOCALTIMESTAMP AS wall, CURRENT_TIMESTAMP(9) AS fine, "
            + "CURRENT_TIMESTAMP(0) AS coarse, CURRENT_DATE AS today FROM trades WHERE id = 0");

        Assert.Equal(TimeUnit.Microsecond, ((TimestampType)prepared.OutputSchema.GetFieldByName("standard").DataType).Unit);
        Assert.Equal(TimeUnit.Nanosecond, ((TimestampType)prepared.OutputSchema.GetFieldByName("fine").DataType).Unit);
        Assert.IsType<Date32Type>(prepared.OutputSchema.GetFieldByName("today").DataType);

        var row = await SingleRowAsync(engine, prepared);
        var wall = new DateTime(2026, 10, 12, 22, 34, 56);
        Assert.Equal(wall.AddTicks(1_234_560), row[0]);
        Assert.Equal(wall.AddTicks(1_234_560), row[1]);
        Assert.Equal(wall.AddTicks(1_234_567), row[2]);
        Assert.Equal(wall, row[3]);
        Assert.Equal(new DateOnly(2026, 10, 12), row[4]);
    }

    /// <summary>
    /// <c>CURRENT_TIME</c> and <c>LOCALTIME</c> are the instant's time of day, as a UTC wall clock: a
    /// time of precision 6 unless one is written, and the time of day of <c>CURRENT_TIMESTAMP</c>
    /// within one execution (F191). Before, both were refused.
    /// </summary>
    [Theory]
    [InlineData(ExecutionEngine.Vectorised)]
    [InlineData(ExecutionEngine.Reference)]
    public async Task The_time_of_day_is_the_instants_as_a_UTC_wall_clock(ExecutionEngine executor)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await CreateEngineAsync(new Clock(Instant), executor);
        var prepared = await engine.PrepareAsync(
            "SELECT CURRENT_TIME AS standard, LOCALTIME AS wall, CURRENT_TIME(0) AS coarse, "
            + "CAST(CURRENT_TIME AS VARCHAR) AS text, CURRENT_TIME = CAST(CURRENT_TIMESTAMP AS TIME(6)) AS same "
            + "FROM trades WHERE id = 0");

        Assert.Equal(TimeUnit.Microsecond, ((Time64Type)prepared.OutputSchema.GetFieldByName("standard").DataType).Unit);
        await using var execution = await engine.ExecuteAsync(prepared, (IReadOnlyList<object?>)[]);
        var rows = new List<object?[]>();
        await foreach (var batch in execution.Batches)
        {
            rows.AddRange(BatchReader.ToRows(batch));
            batch.Dispose();
        }

        var row = Assert.Single(rows);
        var micros = new TimeOnly(22, 34, 56).Ticks / TimeSpan.TicksPerMicrosecond;
        Assert.Equal(new TimeOnly((micros + 123_456) * 10), row[0]);
        Assert.Equal(new TimeOnly((micros + 123_456) * 10), row[1]);
        Assert.Equal(new TimeOnly(22, 34, 56), row[2]);
        Assert.Equal("22:34:56.123456", row[3]);
        Assert.Equal(true, row[4]);
    }

    /// <summary>
    /// A clock that moves on every reading: every row of one execution, across batches and
    /// operators, still reads one value, and the next execution reads a later one.
    /// </summary>
    [Theory]
    [InlineData(ExecutionEngine.Vectorised)]
    [InlineData(ExecutionEngine.Reference)]
    public async Task One_execution_reads_one_instant_and_the_next_reads_its_own(ExecutionEngine executor)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var clock = new Clock(Instant) { Step = TimeSpan.FromSeconds(1) };
        await using var engine = await CreateEngineAsync(clock, executor, batchSize: 2);
        var prepared = await engine.PrepareAsync(
            "SELECT t.id, CURRENT_TIMESTAMP(9) AS seen, u.joined FROM trades t "
            + "JOIN (SELECT id, CURRENT_TIMESTAMP(9) AS joined FROM trades) u ON t.id = u.id");

        var first = await ColumnAsync(engine, prepared);
        var second = await ColumnAsync(engine, prepared);

        Assert.Equal(40, first.Count);
        Assert.Single(first.Distinct());
        Assert.Single(second.Distinct());
        Assert.True(second[0] > first[0], $"the second execution read {second[0]:O}, the first {first[0]:O}");
    }

    [Fact]
    public async Task A_window_on_the_current_time_keeps_the_rows_inside_it()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await CreateEngineAsync(new Clock(Instant), ExecutionEngine.Vectorised);
        var prepared = await engine.PrepareAsync(
            "SELECT id FROM trades WHERE ts >= CURRENT_TIMESTAMP - INTERVAL '60' SECOND ORDER BY id");

        var ids = new List<int>();
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

        Assert.Equal([0, 1, 2], ids);
    }

    /// <summary>
    /// A remote source is never asked for its own "now": the comparison stays in the host, so one
    /// instant serves the whole statement.
    /// </summary>
    [Fact]
    public async Task A_remote_source_is_never_sent_the_current_time()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        Assert.SkipWhen(RemoteFixture.SkipReason is not null, RemoteFixture.SkipReason ?? string.Empty);
        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = CorpusFixture.ContextId,
            Functions = CorpusFunctions.Register,
            Sources = RemoteFixture.Shared.Sources,
            Planner = sidecar.CreatePlanner(),
            Execution = new ExecutionOptions { TimeProvider = new Clock(Instant) },
        });

        var dated = await engine.PrepareAsync(
            "SELECT COUNT(*) AS n FROM duck.lineitem WHERE l_shipdate <= CURRENT_DATE");
        var all = await engine.PrepareAsync("SELECT COUNT(*) AS n FROM duck.lineitem");

        var sql = Assert.Single(TenancyRemoteBindingTests.RemoteSql(dated.Plan));
        Assert.DoesNotContain("CURRENT", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(await CountAsync(engine, all), await CountAsync(engine, dated));
    }

    private async Task<ChalkEngine> CreateEngineAsync(Clock clock, ExecutionEngine executor, int batchSize = 4096) =>
        await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "current-time",
            Sources =
            [
                new PocoSourceBuilder("mem")
                    .NamingPolicy(PocoNamingPolicy.SnakeCase)
                    .AddTable(
                        "trades",
                        Enumerable.Range(0, 20)
                            .Select(i => new Trade(i, Wall.AddSeconds(-25 * i)))
                            .ToArray())
                    .Build(),
            ],
            Planner = sidecar.CreatePlanner(),
            Execution = new ExecutionOptions { Engine = executor, BatchSize = batchSize, TimeProvider = clock },
        });

    /// <summary>The one row's four timestamps and its date.</summary>
    private static async Task<object[]> SingleRowAsync(ChalkEngine engine, PreparedQuery prepared)
    {
        object[]? row = null;
        await using var execution = await engine.ExecuteAsync(prepared, (IReadOnlyList<object?>)[]);
        await foreach (var batch in execution.Batches)
        {
            Assert.Null(row);
            Assert.Equal(1, batch.Length);
            row =
            [
                Timestamp(batch, 0),
                Timestamp(batch, 1),
                Timestamp(batch, 2),
                Timestamp(batch, 3),
                ((Date32Array)batch.Column(4)).GetDateOnly(0)!.Value,
            ];
            batch.Dispose();
        }

        Assert.NotNull(row);
        return row;
    }

    private static async Task<List<DateTime>> ColumnAsync(ChalkEngine engine, PreparedQuery prepared)
    {
        var seen = new List<DateTime>();
        await using var execution = await engine.ExecuteAsync(prepared, (IReadOnlyList<object?>)[]);
        await foreach (var batch in execution.Batches)
        {
            for (var i = 0; i < batch.Length; i++)
            {
                seen.Add(Timestamp(batch, 1, i));
                seen.Add(Timestamp(batch, 2, i));
            }

            batch.Dispose();
        }

        return seen;
    }

    private static async Task<long> CountAsync(ChalkEngine engine, PreparedQuery prepared)
    {
        long count = 0;
        await using var execution = await engine.ExecuteAsync(prepared, (IReadOnlyList<object?>)[]);
        await foreach (var batch in execution.Batches)
        {
            count += ((Int64Array)batch.Column(0)).GetValue(0)!.Value;
            batch.Dispose();
        }

        return count;
    }

    private static DateTime Timestamp(RecordBatch batch, int column, int row = 0) =>
        ((TimestampArray)batch.Column(column)).GetTimestamp(row)!.Value.UtcDateTime;

    public sealed record Trade(int Id, DateTime Ts);

    /// <summary>A clock that reads <see cref="Now"/>, and moves on by <see cref="Step"/> each time it is read.</summary>
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; private set; } = now;

        public TimeSpan Step { get; init; }

        public override DateTimeOffset GetUtcNow()
        {
            var read = Now;
            Now += Step;
            return read;
        }
    }
}

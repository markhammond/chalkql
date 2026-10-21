using Apache.Arrow;
using Chalk.Client;
using Chalk.Sources.Poco;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// Two statements the planner typed wrongly. A <c>UNION ALL</c> of timestamps of two precisions was
/// planned with the coarser input left as it was, which the plan's check refused as the planner's
/// fault; that input is now cast to the set's type. A parameter in a division that is a <c>CASE</c>
/// branch had no type when the branch was derived, and the statement was refused; it now takes the
/// type the division gives it, as it does outside a <c>CASE</c>.
/// </summary>
public sealed class SetAndBranchTypingTests(SharedSidecar sidecar) : IClassFixture<SharedSidecar>
{
    private static readonly DateTime Day = new(2026, 10, 12);

    /// <summary>
    /// The half minutes the marks fall in, each labelled by its end, and the time given, cut to the
    /// second by its <c>CAST</c>: a <c>TIMESTAMP(9)</c> input beside a <c>TIMESTAMP(0)</c> one, alone
    /// and as a <c>WITH</c> block read twice.
    /// </summary>
    [Theory]
    [InlineData(
        "SELECT TIME_BUCKET(INTERVAL '30' SECOND, ts) + INTERVAL '30' SECOND AS ts FROM marks "
        + "UNION ALL SELECT CAST(@now AS TIMESTAMP) AS ts")]
    [InlineData(
        "WITH points AS (SELECT TIME_BUCKET(INTERVAL '30' SECOND, ts) + INTERVAL '30' SECOND AS ts FROM marks "
        + "UNION ALL SELECT CAST(@now AS TIMESTAMP) AS ts) "
        + "SELECT a.ts FROM points a JOIN (SELECT MAX(ts) AS ts FROM points) b ON a.ts <= b.ts")]
    public async Task A_union_of_two_timestamp_precisions_takes_the_finer(string sql)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await CreateEngineAsync();

        var now = Day.AddMinutes(5).AddTicks(1_234_567);
        var times = await ValuesAsync<DateTimeOffset?>(engine, sql, now, column => ((TimestampArray)column).GetTimestamp);

        DateTime[] expected = [Day.AddSeconds(30), Day.AddSeconds(60), Day.AddSeconds(90), Day.AddMinutes(5)];
        Assert.Equal(expected, times.Select(t => t!.Value.UtcDateTime).Order());
    }

    /// <summary>
    /// <c>CASE WHEN @trigger &lt; 0e0 THEN price / @trigger END</c>, the parameter a DOUBLE in the
    /// division as in the comparison: each price over a threshold of -2.5, and NULL where the
    /// threshold holds. (Beside the integer literal <c>0</c> the comparison's occurrence would be an
    /// INTEGER, and a named parameter takes each occurrence's type, so no value would bind to both.)
    /// </summary>
    [Fact]
    public async Task A_parameter_in_a_division_in_a_branch_takes_the_divisions_type()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await CreateEngineAsync();

        var prepared = await engine.PrepareAsync("SELECT CASE WHEN @trigger < 0e0 THEN price / @trigger END FROM marks");

        var negative = await ValuesAsync<double?>(engine, prepared, -2.5, column => ((DoubleArray)column).GetValue);
        Assert.Equal([-1.2, -0.8, -0.4], negative.Order());

        var positive = await ValuesAsync<double?>(engine, prepared, 2.5, column => ((DoubleArray)column).GetValue);
        Assert.All(positive, value => Assert.Null(value));
    }

    private static async Task<List<T>> ValuesAsync<T>(
        ChalkEngine engine, string sql, object? parameter, Func<IArrowArray, Func<int, T>> read) =>
        await ValuesAsync(engine, await engine.PrepareAsync(sql), parameter, read);

    private static async Task<List<T>> ValuesAsync<T>(
        ChalkEngine engine, PreparedQuery prepared, object? parameter, Func<IArrowArray, Func<int, T>> read)
    {
        var values = new List<T>();
        await using var execution = await engine.ExecuteAsync(prepared, (IReadOnlyList<object?>)[parameter]);
        await foreach (var batch in execution.Batches)
        {
            var at = read(batch.Column(0));
            for (var i = 0; i < batch.Length; i++)
            {
                values.Add(at(i));
            }

            batch.Dispose();
        }

        return values;
    }

    private async Task<ChalkEngine> CreateEngineAsync() =>
        await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "set-and-branch-typing",
            Sources =
            [
                new PocoSourceBuilder("mem")
                    .NamingPolicy(PocoNamingPolicy.SnakeCase)
                    .AddTable("marks", (IReadOnlyList<Mark>)
                    [
                        new(Day.AddSeconds(10.5), 1.0), new(Day.AddSeconds(40.25), 2.0), new(Day.AddSeconds(75), 3.0),
                    ])
                    .Build(),
            ],
            Planner = sidecar.CreatePlanner(),
        });

    public sealed record Mark(DateTime Ts, double Price);
}

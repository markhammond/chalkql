using Apache.Arrow;
using Chalk.Client;
using Chalk.Sources.Poco;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// A timestamp keeps the digits its precision declares. <c>TIMESTAMP(0)</c> to <c>TIMESTAMP(3)</c>
/// are counted in milliseconds, and a cast to <c>TIMESTAMP(0)</c> at run time, or a value bound to a
/// <c>TIMESTAMP(0)</c> parameter, kept the milliseconds: <c>CAST(@now AS TIMESTAMP)</c> gave
/// 00:05:00.123 for 00:05:00.1234567. Both now keep whole seconds, as a literal cast the planner folds
/// always did; an instant before 1970 is cut towards the past.
/// </summary>
public sealed class TimestampPrecisionCutTests(SharedSidecar sidecar) : IClassFixture<SharedSidecar>
{
    private static readonly DateTime Now = new DateTime(2026, 10, 12, 0, 5, 0).AddTicks(1_234_567);

    [Theory]
    [InlineData(ExecutionEngine.Vectorised, "SELECT CAST(@now AS TIMESTAMP) FROM single", "2026-10-12T00:05:00.0000000")]
    [InlineData(ExecutionEngine.Vectorised, "SELECT CAST(CAST(@now AS TIMESTAMP(9)) AS TIMESTAMP(0)) FROM single", "2026-10-12T00:05:00.0000000")]
    [InlineData(ExecutionEngine.Vectorised, "SELECT CAST(CAST(@now AS TIMESTAMP(9)) AS TIMESTAMP(2)) FROM single", "2026-10-12T00:05:00.1200000")]
    [InlineData(ExecutionEngine.Vectorised, "SELECT CAST(CAST(@now AS TIMESTAMP(9)) AS TIMESTAMP(5)) FROM single", "2026-10-12T00:05:00.1234500")]
    [InlineData(ExecutionEngine.Vectorised, "SELECT CAST(early AS TIMESTAMP(0)) FROM single", "1969-12-31T23:59:58.0000000")]
    [InlineData(ExecutionEngine.Reference, "SELECT CAST(CAST(@now AS TIMESTAMP(9)) AS TIMESTAMP(0)) FROM single", "2026-10-12T00:05:00.0000000")]
    [InlineData(ExecutionEngine.Reference, "SELECT CAST(early AS TIMESTAMP(0)) FROM single", "1969-12-31T23:59:58.0000000")]
    public async Task A_timestamp_keeps_the_digits_its_precision_declares(ExecutionEngine executor, string sql, string expected)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "timestamp-precision-cut",
            Sources =
            [
                new PocoSourceBuilder("mem")
                    .NamingPolicy(PocoNamingPolicy.SnakeCase)
                    .AddTable("single", (IReadOnlyList<Single>)[new Single(DateTime.UnixEpoch.AddSeconds(-1.5))])
                    .Build(),
            ],
            Planner = sidecar.CreatePlanner(),
            Execution = new ExecutionOptions { Engine = executor },
        });

        var prepared = await engine.PrepareAsync(sql);
        object?[] parameters = prepared.Parameters.Count > 0 ? [Now] : [];
        await using var execution = await engine.ExecuteAsync(prepared, (IReadOnlyList<object?>)parameters);
        var values = new List<string>();
        await foreach (var batch in execution.Batches)
        {
            values.Add(((TimestampArray)batch.Column(0)).GetTimestamp(0)!.Value.UtcDateTime.ToString("O")[..27]);
            batch.Dispose();
        }

        Assert.Equal([expected], values);
    }

    public sealed record Single(DateTime Early);
}

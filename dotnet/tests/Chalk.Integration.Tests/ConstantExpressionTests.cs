using Apache.Arrow;
using Chalk.Catalog;
using Chalk.Client;
using Chalk.Sources;
using Chalk.Sources.Poco;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// An expression that reads no column is computed once for each execution, not once for each row or
/// batch (D337, F170). A host function that counts its calls shows it: called with a parameter, it
/// answers once per execution, and again for the next one with its new value.
/// </summary>
public sealed class ConstantExpressionTests(SharedSidecar sidecar) : IClassFixture<SharedSidecar>
{
    private int _calls;

    [Fact]
    public async Task An_expression_of_constants_is_computed_once_per_execution()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await CreateEngineAsync();
        var prepared = await engine.PrepareAsync(
            "SELECT id FROM items WHERE id >= counted(? + 1) ORDER BY id",
            new PrepareOptions { ParameterTypes = [ChalkType.Int32()] });

        var first = await IdsAsync(engine, prepared, 5);
        Assert.Equal(1, _calls);
        var second = await IdsAsync(engine, prepared, 7);

        Assert.Equal([6, 7, 8, 9], first);
        Assert.Equal([8, 9], second);
        Assert.Equal(2, _calls);
    }

    /// <summary>
    /// A batch with no row to answer for never computes it, so a value that would fail fails only
    /// where a row reaches it, as it did when it was computed row by row.
    /// </summary>
    [Fact]
    public async Task An_expression_no_row_reaches_is_never_computed()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await CreateEngineAsync();
        var none = await engine.PrepareAsync("SELECT id, counted(?) AS c FROM items WHERE id < 0");
        var some = await engine.PrepareAsync("SELECT id, counted(?) AS c FROM items WHERE id >= 0");

        Assert.Empty(await IdsAsync(engine, none, 13));
        Assert.Equal(0, _calls);
        var failure = await Assert.ThrowsAsync<ExecutionException>(() => IdsAsync(engine, some, 13));
        Assert.Equal("13 is refused", failure.InnerException?.Message);
        Assert.Equal(1, _calls);
    }

    private async Task<ChalkEngine> CreateEngineAsync() =>
        await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "constant-expressions",
            Sources =
            [
                new PocoSourceBuilder("mem")
                    .NamingPolicy(PocoNamingPolicy.SnakeCase)
                    .AddTable("items", Enumerable.Range(0, 10).Select(i => new Item(i)).ToArray())
                    .AddFunction("counted", f => f.Scalar<int, int>("x").Strict().Client())
                    .Build(),
            ],
            Functions = registry => registry.AddScalar<int, int>("counted", Count),
            Planner = sidecar.CreatePlanner(),
            Execution = new ExecutionOptions { BatchSize = 2 },
        });

    private int Count(int x)
    {
        Interlocked.Increment(ref _calls);
        return x == 13 ? throw new InvalidOperationException("13 is refused") : x;
    }

    private static async Task<List<int>> IdsAsync(ChalkEngine engine, PreparedQuery prepared, int value)
    {
        var ids = new List<int>();
        await using var execution = await engine.ExecuteAsync(prepared, (IReadOnlyList<object?>)[value]);
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

    public sealed record Item(int Id);
}

using Akade.IndexedSet;
using Chalk.Catalog;
using Chalk.Client;
using Chalk.Ir;
using Chalk.Sources.Poco;

namespace Chalk.Sources.Akade.Tests;

/// <summary>
/// D337 — an Akade range index serves a range whose bounds are computed once per execution, as it
/// serves one bounded by literals or bare parameters: <c>id &gt;= ? * 2</c> reads the index and
/// touches only the rows in the range.
/// </summary>
public sealed class AkadeComputedBoundTests
{
    private sealed record Item(int Id, string Tag);

    [Fact]
    public async Task A_range_computed_from_a_parameter_reads_the_range_index()
    {
        var sidecar = await PlannerProcess.StartAsync();
        await using var _ = sidecar;
        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "akade-computed-bounds",
            Sources =
            [
                AkadeSource
                    .From(
                        "items",
                        Enumerable.Range(0, 10_000)
                            .Select(i => new Item(i, $"r{i}"))
                            .ToIndexedSet()
                            .WithRangeIndex(x => x.Id)
                            .Build())
                    .TableName("items")
                    .NamingPolicy(PocoNamingPolicy.SnakeCase)
                    .Build(),
            ],
            Planner = sidecar.CreatePlanner(),
        });

        var query = await engine.PrepareAsync(
            "SELECT id FROM items WHERE id >= ? * 2 AND id <= ? * 2 + 2 ORDER BY id",
            new PrepareOptions { ParameterTypes = [ChalkType.Int32(), ChalkType.Int32()] });
        Assert.True(PlanWalker.Has(query.Plan, Rel.KindOneofCase.IndexLookup), "the range reads the index");

        await using var execution = await engine.ExecuteAsync(query, [3, 3]);
        var ids = new List<int>();
        await foreach (var batch in execution.Batches)
        {
            var column = (Apache.Arrow.Int32Array)batch.Column(0);
            for (var i = 0; i < batch.Length; i++)
            {
                ids.Add(column.GetValue(i)!.Value);
            }

            batch.Dispose();
        }

        Assert.Equal([6, 7, 8], ids);
        Assert.Equal(3, (long)execution.Stats.RowsScanned);
    }
}

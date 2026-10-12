using Apache.Arrow;
using Chalk.Catalog;
using Chalk.Client;
using Chalk.Ir;
using Chalk.Sources.Poco;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// A range bound computed once per execution serves an index (D337, F172):
/// <c>CURRENT_TIMESTAMP - INTERVAL '60' SECOND</c>, or a parameter less an interval. Before, a bound
/// had to be a literal or a parameter, so such a window filtered a full scan whatever index the table
/// declared. Each index here answers the same rows a table with no index does.
/// </summary>
public sealed class ComputedIndexBoundTests(SharedSidecar sidecar) : IClassFixture<SharedSidecar>
{
    private const string Window =
        "SELECT id FROM {0} WHERE ts >= CURRENT_TIMESTAMP - INTERVAL '60' SECOND "
        + "AND ts <= CURRENT_TIMESTAMP ORDER BY id";

    private static readonly DateTime Now = new(2026, 10, 12, 12, 0, 0);

    [Theory]
    [InlineData("ordered")]
    [InlineData("clustered")]
    public async Task A_window_on_the_current_time_reads_the_index(string table)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await CreateEngineAsync();

        var indexed = await engine.PrepareAsync(string.Format(null, Window, table));
        var scanned = await engine.PrepareAsync(string.Format(null, Window, "plain"));

        Assert.True(PlanWalker.Has(indexed.Plan, Rel.KindOneofCase.IndexLookup), "the window reads the index");
        Assert.False(PlanWalker.Has(scanned.Plan, Rel.KindOneofCase.IndexLookup));
        Assert.Equal([0, 1, 2], await IdsAsync(engine, indexed, []));
        Assert.Equal(await IdsAsync(engine, scanned, []), await IdsAsync(engine, indexed, []));
    }

    /// <summary>A bound computed from a parameter, and a NULL bound, which gives no rows, as a NULL parameter does.</summary>
    [Fact]
    public async Task A_bound_computed_from_a_parameter_reads_the_index_and_NULL_reads_nothing()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await CreateEngineAsync();
        var prepared = await engine.PrepareAsync(
            "SELECT id FROM ordered WHERE ts >= ? - INTERVAL '60' SECOND AND ts <= ? ORDER BY id",
            new PrepareOptions
            {
                ParameterTypes = [ChalkType.Timestamp(9, nullable: true), ChalkType.Timestamp(9, nullable: true)],
            });

        Assert.True(PlanWalker.Has(prepared.Plan, Rel.KindOneofCase.IndexLookup), "the window reads the index");
        Assert.Equal([2, 3, 4], await IdsAsync(engine, prepared, [Now.AddSeconds(-50), Now.AddSeconds(-50)]));
        Assert.Empty(await IdsAsync(engine, prepared, [null, Now]));
    }

    private async Task<ChalkEngine> CreateEngineAsync()
    {
        var rows = Enumerable.Range(0, 20).Select(i => new Trade(i, Now.AddSeconds(-25 * i))).ToArray();
        return await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "computed-bounds",
            Sources =
            [
                new PocoSourceBuilder("mem")
                    .NamingPolicy(PocoNamingPolicy.SnakeCase)
                    .AddTable("ordered", rows, t => t.Index(r => r.Ts))
                    .AddTable("clustered", rows, t => t.ClusteredIndex(r => r.Ts))
                    .AddTable("plain", rows)
                    .Build(),
            ],
            Planner = sidecar.CreatePlanner(),
            Execution = new ExecutionOptions { TimeProvider = new Fixed(new DateTimeOffset(Now, TimeSpan.Zero)) },
        });
    }

    private static async Task<List<int>> IdsAsync(ChalkEngine engine, PreparedQuery prepared, object?[] values)
    {
        var ids = new List<int>();
        await using var execution = await engine.ExecuteAsync(prepared, (IReadOnlyList<object?>)values);
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

    public sealed record Trade(int Id, DateTime Ts);

    private sealed class Fixed(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

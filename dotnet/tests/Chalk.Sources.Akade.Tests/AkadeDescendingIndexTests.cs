using Akade.IndexedSet;
using Chalk.Client;
using Chalk.Ir;
using Chalk.Sources.Poco;

namespace Chalk.Sources.Akade.Tests;

/// <summary>
/// D281 — an Akade index built with a descending comparer is a descending Chalk index, and the
/// planner serves the commonest ordered query there is from it without a sort.
/// </summary>
public sealed class AkadeDescendingIndexTests
{
    private sealed record Tick(int Id, long Ts);

    /// <summary>
    /// A lambda accessor names its own column in the text Akade files the index under, which is both
    /// how the index is discovered and how the comparer declaration below is matched to it.
    /// </summary>
    private const string IndexName = "x => x.Ts";

    private static Tick[] Rows()
    {
        var rows = new Tick[10_000];
        for (var i = 0; i < rows.Length; i++)
        {
            rows[i] = new Tick(i, i);
        }

        return rows;
    }

    private static IndexedSetSource<Tick> Source() =>
        AkadeSource
            .From(
                "ticks",
                Rows().ToIndexedSet()
                    .WithRangeIndex(x => x.Ts, ChalkComparers.For<long>(descending: true))
                    .Build())
            .TableName("ticks")
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .Comparer(x => x.Ts, ChalkComparers.For<long>(descending: true))
            .Build();

    /// <summary>
    /// The latest row: one seek and one row, where an ascending index would have sorted ten
    /// thousand. The goal is what makes the lookup the cheapest plan, and the descending direction
    /// is what makes it deliver the order without a sort above it.
    /// </summary>
    [Fact]
    public async Task Order_by_descending_with_a_limit_reads_one_row()
    {
        await using var fixture = await Fixture.CreateAsync();

        var plan = await fixture.PlanTextAsync("SELECT id FROM ticks ORDER BY ts DESC LIMIT 1");

        Assert.Contains("IndexLookup", plan, StringComparison.Ordinal);
        Assert.Contains(IndexName, plan, StringComparison.Ordinal);
        Assert.Contains("goal=1", plan, StringComparison.Ordinal);
        Assert.DoesNotContain("Sort", plan, StringComparison.Ordinal);
        Assert.DoesNotContain("TopN", plan, StringComparison.Ordinal);

        var result = await fixture.RunAsync("SELECT id FROM ticks ORDER BY ts DESC LIMIT 1");

        Assert.Equal(1, result.Rows);
        Assert.Equal(1, result.Scanned);
    }

    /// <summary>
    /// And the rows really are the last ones, in the index's order — the plan deleted a sort on the
    /// strength of the declaration, so this is what makes the declaration true.
    /// </summary>
    [Fact]
    public async Task The_descending_index_hands_back_the_latest_rows_in_order()
    {
        await using var fixture = await Fixture.CreateAsync();

        var ids = await fixture.IdsAsync("SELECT id FROM ticks ORDER BY ts DESC LIMIT 3");

        Assert.Equal([9_999, 9_998, 9_997], ids);
    }

    /// <summary>
    /// A bound on a descending key: the range's bounds are stated in the index's own key order, so
    /// <c>ts &gt;= ?</c> is the range's <em>upper</em> bound. The planner swaps them and the adapter
    /// reads the index forwards.
    /// </summary>
    [Fact]
    public async Task A_bound_on_a_descending_key_matches_the_rows_a_scan_would()
    {
        await using var fixture = await Fixture.CreateAsync();

        var ids = await fixture.IdsAsync(
            "SELECT id FROM ticks WHERE ts >= 9997 ORDER BY ts DESC");

        Assert.Equal([9_999, 9_998, 9_997], ids);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly PlannerProcess _sidecar;

        private Fixture(PlannerProcess sidecar, ChalkEngine engine)
        {
            _sidecar = sidecar;
            Engine = engine;
        }

        public ChalkEngine Engine { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var sidecar = await PlannerProcess.StartAsync();

            try
            {
                var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
                {
                    ContextId = "akade-descending",
                    Sources = [Source()],
                    Planner = sidecar.CreatePlanner(),
                });

                return new Fixture(sidecar, engine);
            }
            catch
            {
                await sidecar.DisposeAsync();
                throw;
            }
        }

        public async Task<string> PlanTextAsync(string sql)
        {
            var query = await Engine.PrepareAsync(sql);
            return query.Plan.ToPlanText();
        }

        public async Task<(int Rows, long Scanned)> RunAsync(string sql)
        {
            var query = await Engine.PrepareAsync(sql);
            await using var execution = await Engine.ExecuteAsync(query, []);

            var rows = 0;
            await foreach (var batch in execution.Batches)
            {
                rows += batch.Length;
                batch.Dispose();
            }

            return (rows, (long)execution.Stats.RowsScanned);
        }

        public async Task<int[]> IdsAsync(string sql)
        {
            var query = await Engine.PrepareAsync(sql);
            await using var execution = await Engine.ExecuteAsync(query, []);

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

            return [.. ids];
        }

        public async ValueTask DisposeAsync()
        {
            await Engine.DisposeAsync();
            await _sidecar.DisposeAsync();
        }
    }
}

using Chalk.Client;
using Chalk.Sources.Poco;
using Chalk.Sample.AkadeIndexedSet;

namespace Chalk.Sources.Akade.Tests;

/// <summary>
/// End-to-end planner/executor coverage for the README-style index set.
///
/// These are deliberately separate tests: when one access-path mapping regresses, the failing SQL
/// says exactly which physical Akade index Chalk failed to exploit.
/// </summary>
public sealed class IndexedSetSourcePlanningTests
{
    [Fact]
    public async Task Unique_primary_key_lookup()
    {
        await using var fixture = await Fixture.CreateAsync();

        var result = await fixture.CountAsync(
            "SELECT id FROM purchases WHERE id = ?",
            [6]);

        Assert.Equal(1, result.Rows);
        Assert.True(result.Scanned < AkadeReadmeExamples.Purchases.Length);
    }

    [Fact]
    public async Task Non_unique_equality_lookup()
    {
        await using var fixture = await Fixture.CreateAsync();

        var result = await fixture.CountAsync(
            "SELECT id FROM purchases WHERE product_id = ?",
            [4]);

        Assert.Equal(2, result.Rows);
        Assert.True(result.Scanned < AkadeReadmeExamples.Purchases.Length);
    }

    [Fact]
    public async Task Range_lookup()
    {
        await using var fixture = await Fixture.CreateAsync();

        var result = await fixture.CountAsync(
            "SELECT id FROM purchases WHERE amount BETWEEN ? AND ?",
            [1, 3]);

        Assert.Equal(3, result.Rows);
        Assert.True(result.Scanned < AkadeReadmeExamples.Purchases.Length);
    }

    [Fact]
    public async Task Greater_than_or_equal_lookup()
    {
        await using var fixture = await Fixture.CreateAsync();

        var result = await fixture.CountAsync(
            "SELECT id FROM purchases WHERE amount >= ? ORDER BY amount asc LIMIT 1",
            [10000]);

        Assert.Equal(1, result.Rows);
        Assert.True(result.Scanned < AkadeReadmeExamples.PlanningPurchases().Length);
    }

    [Fact]
    public async Task Compound_hash_lookup_requires_full_key()
    {
        await using var fixture = await Fixture.CreateAsync();

        var result = await fixture.CountAsync(
            "SELECT id FROM purchases WHERE product_id = ? AND unit_price = ?",
            [4, 10]);

        Assert.Equal(1, result.Rows);
        Assert.True(result.Scanned < AkadeReadmeExamples.Purchases.Length);
    }

    /*
     * Keep the computed-expression case separate from the base-column cases. It should be enabled
     * once IndexDescriptor keys are expression-valued and PurchaseKeys.Total is bound to the same
     * scalar expression/function that the SQL planner sees.
     *
     * [Fact]
     * public async Task Computed_range_lookup()
     * {
     *     await using var fixture = await Fixture.CreateAsync();
     *
     *     var result = await fixture.CountAsync(
     *         "SELECT id FROM purchases WHERE amount * unit_price >= ?",
     *         [36]);
     *
     *     Assert.Equal(2, result.Rows);
     *     Assert.True(result.Scanned < AkadeReadmeExamples.Purchases.Length);
     * }
     */

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly PlannerProcess _sidecar;

        private Fixture(
            PlannerProcess sidecar,
            ChalkEngine engine)
        {
            _sidecar = sidecar;
            Engine = engine;
        }

        public ChalkEngine Engine { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var rows = AkadeReadmeExamples.PlanningPurchases();
            var set = AkadeReadmeExamples.BuildPurchases(rows);

            var source = AkadeSource
                .From("purchases", set)
                .TableName("purchases")
                .NamingPolicy(PocoNamingPolicy.SnakeCase)
                .Build();

            var sidecar = await PlannerProcess.StartAsync();

            try
            {
                var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
                {
                    ContextId = "akade-tests",
                    Sources = [source],
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

        public async Task<(int Rows, long Scanned)> CountAsync(
            string sql,
            IReadOnlyList<object?> parameters)
        {
            var query = await Engine.PrepareAsync(sql);
            await using var execution = await Engine.ExecuteAsync(query, parameters);

            var rows = 0;
            await foreach (var batch in execution.Batches)
            {
                rows += batch.Length;
                batch.Dispose();
            }

            return (rows, (long)execution.Stats.RowsScanned);
        }

        public async ValueTask DisposeAsync()
        {
            await Engine.DisposeAsync();
            await _sidecar.DisposeAsync();
        }
    }
}

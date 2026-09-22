using Chalk.Client;
using Chalk.Ir;
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
        Assert.True(result.Scanned < AkadeReadmeExamples.PlanningPurchases().Length);
    }

    [Fact]
    public async Task Non_unique_equality_lookup()
    {
        await using var fixture = await Fixture.CreateAsync();

        var result = await fixture.CountAsync(
            "SELECT id FROM purchases WHERE product_id = ?",
            [4]);

        Assert.Equal(2, result.Rows);
        Assert.True(result.Scanned < AkadeReadmeExamples.PlanningPurchases().Length);
    }

    [Fact]
    public async Task Range_lookup()
    {
        await using var fixture = await Fixture.CreateAsync();

        var result = await fixture.CountAsync(
            "SELECT id FROM purchases WHERE amount BETWEEN ? AND ?",
            [1, 3]);

        Assert.Equal(3, result.Rows);
        Assert.True(result.Scanned < AkadeReadmeExamples.PlanningPurchases().Length);
    }

    /// <summary>
    /// D276's worked example, end to end. The limit states a row goal of one, the goal makes the
    /// ordered lookup on <c>amount</c> the cheapest plan although the parameter's selectivity is
    /// only guessed, and the source is told to size its first batch for one row — so the seek lands
    /// on the first matching row and exactly that row is read.
    /// </summary>
    [Fact]
    public async Task Greater_than_or_equal_lookup()
    {
        await using var fixture = await Fixture.CreateAsync();

        var result = await fixture.CountAsync(
            "SELECT id FROM purchases WHERE amount >= ? ORDER BY amount asc LIMIT 1",
            [10000]);

        Assert.Equal(1, result.Rows);
        Assert.Equal(1, result.Scanned);
    }

    /// <summary>
    /// The same bound without the ordering is a goaled <em>scan</em>, and that is the right choice
    /// rather than a missed one: any matching row answers the query, a parameter's selectivity is
    /// guessed at one half, so the first match is expected two rows in and two rows at a unit each
    /// beat a seek. Asserted as a scan so that a change of mind here is a change somebody made.
    /// </summary>
    [Fact]
    public async Task A_parameterised_bound_without_an_ordering_stays_a_scan()
    {
        await using var fixture = await Fixture.CreateAsync();

        var plan = await fixture.PlanTextAsync("SELECT id FROM purchases WHERE amount >= ? LIMIT 1");

        Assert.Contains("Read ", plan, StringComparison.Ordinal);
        Assert.DoesNotContain("IndexLookup", plan, StringComparison.Ordinal);

        // And the scan is goaled rather than left whole: the leaf is what the limit reaches.
        Assert.Contains("goal=", plan, StringComparison.Ordinal);
    }

    /// <summary>
    /// And the goal reaches the source: the leaf is read for the rows the limit will pull rather
    /// than for a full batch. Two rows is the goal a filter guessed at one half inflates a limit of
    /// one to; the batches that follow double, so a goal that under-estimated costs a few small
    /// batches and never the table.
    /// </summary>
    [Fact]
    public async Task A_limit_over_the_filtered_scan_reads_a_ramp_and_not_a_batch()
    {
        await using var fixture = await Fixture.CreateAsync();

        var result = await fixture.CountAsync(
            "SELECT id FROM purchases WHERE amount >= ? LIMIT 1",
            [1]);

        Assert.Equal(1, result.Rows);
        Assert.True(
            result.Scanned <= 8,
            $"scanned {result.Scanned}: the first ramp steps are 2, 4 and 8 rows, and the first row "
            + "of the set already matches.");
    }

    /// <summary>
    /// With a <em>literal</em> bound the statistics are real rather than guessed: one percent of the
    /// rows match, the goaled scan would have to examine a hundred of them, and the seek wins.
    /// </summary>
    [Fact]
    public async Task A_literal_bound_under_a_limit_is_a_lookup()
    {
        await using var fixture = await Fixture.CreateAsync();

        var plan = await fixture.PlanTextAsync(
            "SELECT id FROM purchases WHERE amount >= 10000 LIMIT 1");

        Assert.Contains("IndexLookup", plan, StringComparison.Ordinal);
    }

    /// <summary>
    /// D280: the whole key of the compound hash index, and nothing left over. Asserted by the
    /// index's name, because "fewer rows than the table" was also true when the compound index was
    /// undisclosed and the single-column hash on <c>product_id</c> served this with a residual.
    /// </summary>
    [Fact]
    public async Task Compound_hash_lookup_requires_full_key()
    {
        await using var fixture = await Fixture.CreateAsync();

        var plan = await fixture.PlanTextAsync(
            "SELECT id FROM purchases WHERE product_id = ? AND unit_price = ?");

        Assert.Contains("IndexLookup", plan, StringComparison.Ordinal);
        Assert.Contains(CompoundIndexName, plan, StringComparison.Ordinal);
        Assert.DoesNotContain("Filter", plan, StringComparison.Ordinal);

        var result = await fixture.CountAsync(
            "SELECT id FROM purchases WHERE product_id = ? AND unit_price = ?",
            [4, 10]);

        Assert.Equal(1, result.Rows);
        Assert.True(result.Scanned < AkadeReadmeExamples.PlanningPurchases().Length);
    }

    /// <summary>
    /// And half the key is no key at all: a hash bucket is named by the whole key, so the planner
    /// leaves <c>product_id = ?</c> alone to the single-column hash index beside it.
    /// </summary>
    [Fact]
    public async Task A_compound_hash_index_does_not_serve_a_prefix()
    {
        await using var fixture = await Fixture.CreateAsync();

        var plan = await fixture.PlanTextAsync("SELECT id FROM purchases WHERE product_id = ?");

        Assert.DoesNotContain(CompoundIndexName, plan, StringComparison.Ordinal);
    }

    /// <summary>
    /// Akade files an index under the source text of its accessor, so this is the name the catalog
    /// publishes for the compound index the fixture declares.
    /// </summary>
    private const string CompoundIndexName = "PurchaseKeys.ProductAndUnitPrice";

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
     *     Assert.True(result.Scanned < AkadeReadmeExamples.PlanningPurchases().Length);
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
                // The compound key is a method, so its recorded text names no columns; the host
                // states them once and the index becomes an access path (D280).
                .CompoundIndex(
                    AkadeReadmeExamples.PurchaseKeys.ProductAndUnitPrice,
                    x => x.ProductId,
                    x => x.UnitPrice)
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

        public async Task<string> PlanTextAsync(string sql)
        {
            var query = await Engine.PrepareAsync(sql);
            return query.Plan.ToPlanText();
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

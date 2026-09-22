using System.Globalization;
using Chalk.Client;
using Chalk.Sources.Poco;
using Chalk.Sample.AkadeIndexedSet;

namespace Chalk.Sources.Akade.Tests;

/// <summary>
/// One saved filter, four preparations, three different plans (D284).
/// </summary>
/// <remarks>
/// <para>
/// "Purchases within a quantity band, cheapest unit price first" is the statement a host writes once
/// and serves twice: an interactive grid wants the first 25 rows, a report export wants up to a
/// hundred thousand. Two access paths compete for it, because <c>purchases</c> carries a range index
/// on <c>amount</c> — the predicate's column — and another on <c>unit_price</c> — the ordering's.
/// </para>
/// <list type="bullet">
///   <item><b>Predicate-first</b>: read the amount band from its own index, then order what comes
///   back. Pays for every matching row and for ordering them.</item>
///   <item><b>Ordering-first</b>: read the unit-price index in order, filter the band above it, and
///   stop when the limit is met. Pays for the rows it examines before it has enough, which is the
///   limit divided by the band's selectivity.</item>
/// </list>
/// <para>
/// Neither is right in general, and the statement alone cannot say which: both bounds and the limit
/// are parameters, so without hints the planner guesses the band at a quarter and cannot see the
/// limit at all. What the hints buy is the ability to choose — and the choice flips twice below,
/// once on the limit and once on the predicate.
/// </para>
/// <para>
/// The numbers here are the cost model's, not a clock's: nothing in this class is timed.
/// </para>
/// </remarks>
public sealed class AmountBandExemplarTests : IAsyncLifetime
{
    /// <summary>The saved filter. Every case below prepares this exact text.</summary>
    private const string Sql = """
        SELECT id, product_id, amount, unit_price
        FROM purchases
        WHERE amount BETWEEN ? AND ?
        ORDER BY unit_price ASC
        LIMIT ?
        """;

    /// <summary>The same without a bound, where selectivity alone decides.</summary>
    private const string Unbounded = """
        SELECT id, product_id, amount, unit_price
        FROM purchases
        WHERE amount BETWEEN ? AND ?
        ORDER BY unit_price ASC
        """;

    private const int Rows = 50_000;

    /// <summary>A band of a thousand rows in fifty thousand: just under two percent.</summary>
    private const int BandLow = 20_000;
    private const int BandHigh = 20_999;

    private const string AmountIndex = "x => x.Amount";
    private const string UnitPriceIndex = "x => x.UnitPrice";

    private PlannerProcess _sidecar = null!;
    private ChalkEngine _engine = null!;

    public async ValueTask InitializeAsync()
    {
        var source = AkadeSource
            .From("purchases", AkadeReadmeExamples.BuildPurchases(BandRows(Rows)))
            .TableName("purchases")
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .Build();

        _sidecar = await PlannerProcess.StartAsync();
        try
        {
            _engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
            {
                ContextId = "akade-band-exemplar",
                Sources = [source],
                Planner = _sidecar.CreatePlanner(),
            });
        }
        catch
        {
            await _sidecar.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _engine.DisposeAsync();
        await _sidecar.DisposeAsync();
    }

    /// <summary>
    /// The baseline: nothing hinted. The band is Calcite's guessed quarter and the bound is a number
    /// the planner cannot see, so it reads the amount index for what it guesses are twelve and a
    /// half thousand rows and keeps a heap over them.
    /// </summary>
    [Fact]
    public async Task Generic_prepares_predicate_first_on_a_guess()
    {
        var plan = await PlanAsync(Sql, null);

        Assert.Contains("ChalkTopN", plan, StringComparison.Ordinal);
        Assert.Contains(AmountIndex, plan, StringComparison.Ordinal);
        Assert.Contains("sel=[guess(0.2500)]", plan, StringComparison.Ordinal);
        Assert.DoesNotContain(UnitPriceIndex, plan, StringComparison.Ordinal);
        Assert.DoesNotContain("goal=", plan, StringComparison.Ordinal);
    }

    /// <summary>
    /// The interactive grid: the same band, twenty-five rows wanted. Ordering-first now, because
    /// twenty-five rows out of a band of one in fifty are expected about twelve hundred rows into
    /// the unit-price index — and twelve hundred rows read in order beat a thousand read and sorted.
    /// The goal is what says so: <c>ceil(25 / 0.02)</c>.
    /// </summary>
    [Fact]
    public async Task Interactive_flips_to_ordering_first_and_goals_the_leaf()
    {
        var plan = await PlanAsync(Sql, Hints(BandLow, BandHigh, 25));

        Assert.Contains(UnitPriceIndex, plan, StringComparison.Ordinal);
        Assert.Contains("goal=[1252]", plan, StringComparison.Ordinal);
        Assert.Contains("sel=[hint(0.0200)]", plan, StringComparison.Ordinal);
        Assert.DoesNotContain(AmountIndex, plan, StringComparison.Ordinal);

        // Limit over filter over the ordered leaf, in that order.
        Assert.True(
            plan.IndexOf("ChalkLimit", StringComparison.Ordinal)
            < plan.IndexOf("ChalkFilter", StringComparison.Ordinal),
            "the limit sits above the filter");
        Assert.True(
            plan.IndexOf("ChalkFilter", StringComparison.Ordinal)
            < plan.IndexOf("ChalkIndexLookup", StringComparison.Ordinal),
            "the filter sits above the ordered leaf");
    }

    /// <summary>
    /// The report export: the <em>same band</em>, a hundred thousand rows wanted. Predicate-first
    /// again — the limit no longer bounds anything, since the band holds fewer rows than it asks
    /// for, so there is nothing to stop early for and the cheaper path is to read the band and order
    /// it. This is the limit hint alone flipping the plan.
    /// </summary>
    [Fact]
    public async Task Analytical_flips_back_to_predicate_first_on_the_limit_alone()
    {
        var plan = await PlanAsync(Sql, Hints(BandLow, BandHigh, 100_000));

        Assert.Contains(AmountIndex, plan, StringComparison.Ordinal);
        Assert.Contains("sel=[hint(0.0200)]", plan, StringComparison.Ordinal);
        Assert.DoesNotContain(UnitPriceIndex, plan, StringComparison.Ordinal);
        Assert.DoesNotContain("goal=", plan, StringComparison.Ordinal);

        // A heap that would hold more than its input is a sort, and is costed as one.
        Assert.Contains("ChalkSort", plan, StringComparison.Ordinal);
        Assert.DoesNotContain("ChalkTopN", plan, StringComparison.Ordinal);
    }

    /// <summary>
    /// The wide report: the same limit of a hundred thousand, a band that covers the table.
    /// Ordering-first again, and now it is the <em>predicate</em> hint alone that flipped it — at a
    /// selectivity of one there is nothing for the amount index to narrow, so reading the unit-price
    /// index in order and filtering above it beats reading everything and sorting it.
    /// </summary>
    [Fact]
    public async Task Wide_report_flips_on_the_predicate_hint_at_the_same_limit()
    {
        var plan = await PlanAsync(Sql, Hints(0, Rows - 1, 100_000));

        Assert.Contains(UnitPriceIndex, plan, StringComparison.Ordinal);
        Assert.Contains("sel=[hint(1.0000)]", plan, StringComparison.Ordinal);
        Assert.DoesNotContain(AmountIndex, plan, StringComparison.Ordinal);
        Assert.DoesNotContain("ChalkSort", plan, StringComparison.Ordinal);

        // No goal: a hundred thousand rows wanted out of fifty thousand is not a goal but the whole
        // table, and the rule declines rather than offer a leaf that costs what the plain one costs
        // (the cap of design 46 §2.1).
        Assert.DoesNotContain("goal=", plan, StringComparison.Ordinal);
    }

    /// <summary>
    /// The two flips, as the numbers the optimiser actually compared. Relative, never absolute:
    /// what is being claimed is that each hinted preparation is cheaper than it would have been on
    /// the other path, not that any of them costs a particular number of units.
    /// </summary>
    [Fact]
    public async Task Each_preparation_is_the_cheaper_of_the_two_paths()
    {
        var generic = await CostAsync(Sql, null);
        var interactive = await CostAsync(Sql, Hints(BandLow, BandHigh, 25));
        var analytical = await CostAsync(Sql, Hints(BandLow, BandHigh, 100_000));
        var wide = await CostAsync(Sql, Hints(0, Rows - 1, 100_000));

        // Both hinted narrow-band preparations beat the guess they would have been planned on.
        Assert.True(interactive < generic, $"interactive {interactive} < generic {generic}");
        Assert.True(analytical < generic, $"analytical {analytical} < generic {generic}");

        // The interactive grid is the cheapest of all of them: it is the one that can stop early.
        Assert.True(interactive < analytical, $"interactive {interactive} < analytical {analytical}");

        // And the wide report is the dearest, because nothing about it is selective — which is the
        // honest answer rather than a flattering one.
        Assert.True(wide > analytical, $"wide {wide} > analytical {analytical}");
    }

    /// <summary>
    /// Without a bound at all, the predicate hint alone decides — and at a band covering the table
    /// the ordering-first plan does not merely win, it removes the sort entirely.
    /// </summary>
    [Fact]
    public async Task Selectivity_alone_decides_when_nothing_is_bounded()
    {
        var narrow = await PlanAsync(Unbounded, Hints(BandLow, BandHigh));
        Assert.Contains(AmountIndex, narrow, StringComparison.Ordinal);
        Assert.Contains("ChalkSort", narrow, StringComparison.Ordinal);

        var wide = await PlanAsync(Unbounded, Hints(0, Rows - 1));
        Assert.Contains(UnitPriceIndex, wide, StringComparison.Ordinal);
        Assert.DoesNotContain("ChalkSort", wide, StringComparison.Ordinal);
    }

    /// <summary>
    /// Invariant 1 of design 49 §5, on the exemplar: whatever is hinted, the tree the pass hands to
    /// the optimiser is the same tree and the parameters stay parameters. A hint moves a cost.
    /// </summary>
    [Fact]
    public async Task The_parameters_stay_parameters_whatever_is_hinted()
    {
        foreach (var hints in new object?[]?[]
                 {
                     null,
                     Hints(BandLow, BandHigh, 25),
                     Hints(BandLow, BandHigh, 100_000),
                     Hints(0, Rows - 1, 100_000),
                 })
        {
            var query = await _engine.PrepareAsync(
                Sql, hints, new PrepareOptions { IncludePlanText = true });

            Assert.Equal(3, query.ParameterTypes.Count);
            var plan = query.PlanText!;

            // The logical tree is the same tree in every preparation.
            Assert.Contains(
                "LogicalFilter(condition=[AND(>=($2, ?0), <=($2, ?1))])", plan, StringComparison.Ordinal);

            // And the bound is still the parameter the statement wrote, never the hint.
            Assert.Contains("?0", plan, StringComparison.Ordinal);
            Assert.Contains("?1", plan, StringComparison.Ordinal);
            Assert.Contains("fetch=[?2]", plan, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Invariant 2, on the exemplar: the interactive and analytical preparations are two plans over
    /// two different indexes, and executing both with the very same bound values answers the very
    /// same rows. <c>unit_price</c> is distinct across the set, so the ordering is total and the
    /// comparison is row for row rather than as a multiset.
    /// </summary>
    [Fact]
    public async Task The_two_plans_answer_the_same_rows_for_the_same_values()
    {
        var interactive = await _engine.PrepareAsync(Sql, Hints(BandLow, BandHigh, 25));
        var analytical = await _engine.PrepareAsync(Sql, Hints(BandLow, BandHigh, 100_000));

        Assert.NotEqual(interactive.PlanDigest, analytical.PlanDigest);

        // Neither hint is what is bound.
        object?[] values = [BandLow, BandHigh, 40];

        var fromInteractive = await RowsAsync(interactive, values);
        var fromAnalytical = await RowsAsync(analytical, values);

        Assert.Equal(40, fromInteractive.Count);
        Assert.Equal(fromAnalytical.Count, fromInteractive.Count);
        Assert.Equal(fromAnalytical, fromInteractive);
    }

    // ---- support ----

    private static object?[] Hints(int low, int high) => [low, high];

    private static object?[] Hints(int low, int high, int limit) => [low, high, limit];

    private async Task<string> PlanAsync(string sql, object?[]? hints)
    {
        var query = await _engine.PrepareAsync(
            sql, hints, new PrepareOptions { IncludePlanText = true });
        return Physical(query.PlanText!);
    }

    /// <summary>What the whole plan costs, in the slot the optimiser compares.</summary>
    private async Task<double> CostAsync(string sql, object?[]? hints)
    {
        var physical = await PlanAsync(sql, hints);
        var marker = physical.IndexOf("cumulative cost = {", StringComparison.Ordinal);
        Assert.True(marker >= 0, "the plan text carries a cumulative cost");

        var from = marker + "cumulative cost = {".Length;
        var to = physical.IndexOf(" rows", from, StringComparison.Ordinal);
        return double.Parse(physical[from..to], CultureInfo.InvariantCulture);
    }

    /// <summary>The physical half of the planner's plan text, where the costs are.</summary>
    private static string Physical(string planText)
    {
        var marker = planText.IndexOf("-- physical", StringComparison.Ordinal);
        return marker < 0 ? planText : planText[marker..];
    }

    private async Task<List<(int Id, int UnitPrice)>> RowsAsync(PreparedQuery query, object?[] values)
    {
        var rows = new List<(int, int)>();
        await using var execution = await _engine.ExecuteAsync(query, values);
        await foreach (var batch in execution.Batches)
        {
            var ids = (Apache.Arrow.Int32Array)batch.Column(0);
            var prices = (Apache.Arrow.Int32Array)batch.Column(3);
            for (var i = 0; i < batch.Length; i++)
            {
                rows.Add((ids.GetValue(i)!.Value, prices.GetValue(i)!.Value));
            }

            batch.Dispose();
        }

        return rows;
    }

    /// <summary>
    /// A set with a usable distribution on both indexed columns: <c>amount</c> spread evenly over
    /// the whole set, so a band is a controllable share of it, and <c>unit_price</c> a permutation
    /// of the same range, so the ordering is total and worth serving from its own index.
    /// </summary>
    private static Purchase[] BandRows(int count)
    {
        var rows = new Purchase[count];
        for (var i = 0; i < count; i++)
        {
            // 7919 is coprime with 50 000, so the unit prices are a permutation and no two rows tie.
            rows[i] = new Purchase(
                Id: i,
                ProductId: i % 500,
                Amount: i,
                UnitPrice: (int)((long)i * 7919 % count));
        }

        return rows;
    }
}

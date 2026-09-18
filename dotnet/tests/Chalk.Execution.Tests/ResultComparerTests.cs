using Apache.Arrow;
using Chalk.Catalog;
using Chalk.Execution.Tests.Harness;
using Chalk.TestKit;

namespace Chalk.Execution.Tests;

/// <summary>
/// The comparison rules of <c>05-testing.md</c> §5, checked on the comparer itself: it is the thing
/// every differential test trusts, so a comparer that quietly agrees would hide every bug at once.
/// </summary>
public sealed class ResultComparerTests
{
    [Fact]
    public async Task Identical_results_are_equivalent()
    {
        var expected = await Batches(ChalkType.Int64(nullable: true), [1L, null, 3L]);
        var actual = await Batches(ChalkType.Int64(nullable: true), [1L, null, 3L]);

        ResultComparer.AssertEquivalent(expected, actual, ResultComparisonOptions.Ordered);
    }

    [Fact]
    public async Task A_different_value_is_caught()
    {
        var expected = await Batches(ChalkType.Int64(), [1L, 2L]);
        var actual = await Batches(ChalkType.Int64(), [1L, 3L]);

        Assert.ThrowsAny<Exception>(
            () => ResultComparer.AssertEquivalent(expected, actual, ResultComparisonOptions.Ordered));
    }

    [Fact]
    public async Task A_different_null_is_caught()
    {
        var expected = await Batches(ChalkType.Int64(nullable: true), [1L, null]);
        var actual = await Batches(ChalkType.Int64(nullable: true), [1L, 0L]);

        Assert.ThrowsAny<Exception>(
            () => ResultComparer.AssertEquivalent(expected, actual, ResultComparisonOptions.Ordered));
    }

    [Fact]
    public async Task A_different_row_count_is_caught()
    {
        var expected = await Batches(ChalkType.Int64(), [1L, 2L]);
        var actual = await Batches(ChalkType.Int64(), [1L]);

        Assert.ThrowsAny<Exception>(
            () => ResultComparer.AssertEquivalent(expected, actual, ResultComparisonOptions.Ordered));
    }

    [Fact]
    public async Task A_different_type_is_caught()
    {
        var expected = await Batches(ChalkType.Int64(), [1L]);
        var actual = await Batches(ChalkType.Int32(), [1L]);

        Assert.ThrowsAny<Exception>(
            () => ResultComparer.AssertEquivalent(expected, actual, ResultComparisonOptions.Ordered));
    }

    [Fact]
    public async Task NaN_equals_NaN_so_that_a_NaN_result_is_comparable()
    {
        var expected = await Batches(ChalkType.Float64(), [double.NaN, 1.0d]);
        var actual = await Batches(ChalkType.Float64(), [double.NaN, 1.0d]);

        ResultComparer.AssertEquivalent(expected, actual, ResultComparisonOptions.Ordered);
    }

    [Fact]
    public async Task Floating_point_is_exact_unless_the_column_came_from_an_aggregation()
    {
        var expected = await Batches(ChalkType.Float64(), [1.0d]);
        var actual = await Batches(ChalkType.Float64(), [Math.BitIncrement(Math.BitIncrement(1.0d))]);

        Assert.ThrowsAny<Exception>(
            () => ResultComparer.AssertEquivalent(expected, actual, ResultComparisonOptions.Ordered));

        ResultComparer.AssertEquivalent(
            expected, actual, new ResultComparisonOptions { AggregatedFloatColumns = [0] });
    }

    [Fact]
    public async Task A_difference_beyond_four_ulps_is_caught_even_for_an_aggregated_column()
    {
        var expected = await Batches(ChalkType.Float64(), [1.0d]);
        var actual = await Batches(ChalkType.Float64(), [1.0000000000001d]);

        Assert.ThrowsAny<Exception>(() => ResultComparer.AssertEquivalent(
            expected, actual, new ResultComparisonOptions { AggregatedFloatColumns = [0] }));
    }

    [Fact]
    public async Task A_reordered_result_is_a_difference_unless_the_comparison_is_a_multiset()
    {
        var expected = await Batches(ChalkType.Int64(), [1L, 2L, 3L]);
        var actual = await Batches(ChalkType.Int64(), [3L, 1L, 2L]);

        Assert.ThrowsAny<Exception>(
            () => ResultComparer.AssertEquivalent(expected, actual, ResultComparisonOptions.Ordered));

        ResultComparer.AssertEquivalent(expected, actual, ResultComparisonOptions.Multiset);
    }

    [Fact]
    public async Task An_order_by_the_output_does_not_honour_is_caught()
    {
        var expected = await Batches(ChalkType.Int64(), [1L, 2L, 3L]);
        var actual = await Batches(ChalkType.Int64(), [3L, 1L, 2L]);
        var options = new ResultComparisonOptions
        {
            CompareAsMultiset = true,
            OrderKeys = [new OrderKeyExpectation(0, Descending: false, NullsFirst: false)],
        };

        Assert.ThrowsAny<Exception>(
            () => ResultComparer.AssertEquivalent(expected, actual, options));
    }

    [Fact]
    public async Task Strings_compare_by_code_point_not_by_culture()
    {
        var expected = await Strings(["Delta", "alpha"]);
        var actual = await Strings(["Delta", "alpha"]);

        ResultComparer.AssertEquivalent(expected, actual, ResultComparisonOptions.Ordered);
        ResultComparer.AssertEquivalent(expected, actual, ResultComparisonOptions.Multiset);
    }

    [Fact]
    public void Two_empty_results_are_equivalent()
    {
        ResultComparer.AssertEquivalent([], [], ResultComparisonOptions.Ordered);
    }

    // ------------------------------------------------------------------ D166: TopKUnderTies
    //
    // The comparer's own test (25-coverage-graft.md §3). Every differential run of a query whose
    // LIMIT lands inside a tie trusts this mode, and a mode that quietly agreed would hide exactly
    // the bugs the `edge` family exists to find.
    //
    // The fixture is four ordered rows, (1,A) (2,B) (2,C) (4,D): a duplicate key and a gap, which
    // is `sorted`. `OFFSET 1 FETCH 2` returns two rows out of the three that follow the first --
    // the two tied at 2, of which either may be dropped by an executor with a different eviction
    // order, and (4,D). Both boundary keys are the interesting case at once.

    private static readonly object?[][] SortedFour =
    [
        [1L, "A"], [2L, "B"], [2L, "C"], [4L, "D"],
    ];

    private static ResultComparisonOptions TopK(long offset, long? count) => new()
    {
        Mode = ResultComparisonMode.TopKUnderTies,
        Boundary = new TopKBoundary { Offset = offset, Count = count },
        OrderKeys = [new OrderKeyExpectation(0, Descending: false, NullsFirst: false)],
    };

    private static void TopKCompare(object?[][] actual, long offset, long? count) =>
        ResultComparer.AssertTopKUnderTies(
            [.. SortedFour],
            [.. actual],
            [ChalkType.Int64(), ChalkType.String()],
            TopK(offset, count));

    [Fact]
    public void A_valid_top_k_under_ties_passes()
    {
        // OFFSET 1 FETCH 2: the reference's own answer, (2,B) (2,C).
        TopKCompare([[2L, "B"], [2L, "C"]], offset: 1, count: 2);

        // FETCH 2 from the top: (1,A) and either of the two tied at 2.
        TopKCompare([[1L, "A"], [2L, "B"]], offset: 0, count: 2);
        TopKCompare([[1L, "A"], [2L, "C"]], offset: 0, count: 2);

        // OFFSET 2 FETCH 2 -- the offset itself lands inside the tie, so which of the two rows at 2
        // was skipped is the executor's business. Both answers are right.
        TopKCompare([[2L, "B"], [4L, "D"]], offset: 2, count: 2);
        TopKCompare([[2L, "C"], [4L, "D"]], offset: 2, count: 2);

        // No fetch at all: every row from the offset on, and then nothing is ambiguous.
        TopKCompare([[2L, "B"], [2L, "C"], [4L, "D"]], offset: 1, count: null);
    }

    [Fact]
    public void A_wrong_count_is_caught()
    {
        var failure = Assert.ThrowsAny<Exception>(
            () => TopKCompare([[1L, "A"]], offset: 0, count: 2));
        Assert.Contains("Row counts differ", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_wrong_order_is_caught()
    {
        // The right two rows, handed back the wrong way round.
        var failure = Assert.ThrowsAny<Exception>(
            () => TopKCompare([[4L, "D"], [2L, "B"]], offset: 2, count: 2));
        Assert.Contains("break the ORDER BY", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_row_before_the_boundary_is_caught()
    {
        // FETCH 4 takes everything, so the boundary keys are 1 and 4 and both rows at 2 are
        // strictly inside them: determined, and not a tie the executor may choose within. Handing
        // (2,B) back twice keeps the count and the order and still loses (2,C).
        var failure = Assert.ThrowsAny<Exception>(
            () => TopKCompare([[1L, "A"], [2L, "B"], [2L, "B"], [4L, "D"]], offset: 0, count: 4));
        Assert.Contains(
            "sorts strictly between the two boundary keys",
            failure.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_row_past_the_boundary_is_caught()
    {
        // FETCH 2 from the top: 4 is past the last key the query would return.
        var failure = Assert.ThrowsAny<Exception>(
            () => TopKCompare([[1L, "A"], [4L, "D"]], offset: 0, count: 2));
        Assert.Contains(
            "sorts after the last row the plan would return",
            failure.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_row_before_the_offset_is_caught()
    {
        // OFFSET 1 FETCH 2 may not hand back the row the offset skipped.
        var failure = Assert.ThrowsAny<Exception>(
            () => TopKCompare([[1L, "A"], [2L, "B"]], offset: 1, count: 2));
        Assert.Contains(
            "sorts before the first row the plan would return",
            failure.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_row_that_is_not_in_the_tied_group_is_caught()
    {
        // The right key, a value the reference never had at it.
        var failure = Assert.ThrowsAny<Exception>(
            () => TopKCompare([[1L, "A"], [2L, "Z"]], offset: 0, count: 2));
        Assert.Contains("is not one of the reference's rows tied there", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_mode_needs_the_plans_boundary_and_ordering()
    {
        Assert.Throws<ArgumentException>(
            () => ResultComparer.AssertTopKUnderTies(
                [.. SortedFour], [], [], new ResultComparisonOptions
                {
                    Mode = ResultComparisonMode.TopKUnderTies,
                    OrderKeys = [new OrderKeyExpectation(0, false, false)],
                }));

        Assert.ThrowsAny<Exception>(
            () => ResultComparer.AssertTopKUnderTies(
                [.. SortedFour], [], [], new ResultComparisonOptions
                {
                    Mode = ResultComparisonMode.TopKUnderTies,
                    Boundary = new TopKBoundary { Offset = 0, Count = 2 },
                }));
    }

    private static async Task<List<RecordBatch>> Batches(ChalkType type, object?[] values)
    {
        var table = new TestTable
        {
            Name = "t",
            Columns = [("v", type)],
            Rows = [.. values.Select(v => new[] { v })],
        };
        var source = TestData.Source(table);
        var plan = IrBuilder.Plan(IrBuilder.Read(table.Name, table.RowType()));
        return await Runner.RunAsync(plan, source);
    }

    private static Task<List<RecordBatch>> Strings(string[] values) =>
        Batches(ChalkType.String(), [.. values.Cast<object?>()]);
}

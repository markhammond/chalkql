using Akade.IndexedSet;
using Chalk.Catalog;
using Chalk.Tests;
using IndexKind = Chalk.Ir.IndexKind;
using SortDirection = Chalk.Ir.SortDirection;

namespace Chalk.Sources.Akade.Tests;

/// <summary>
/// D277 — the ordered Akade lookup enumerates lazily and checks as it yields.
/// </summary>
/// <remarks>
/// The adapter used to sort the matched rows before returning any of them, which made a consumer
/// that stops after one row pay for the whole range — the one thing a row goal exists to avoid.
/// It now yields Akade's enumeration as it comes and verifies the key order per row, so these tests
/// are about two things at once: that the rows really do arrive in key order from input that was
/// not in key order, and that nothing is read before it is asked for.
/// </remarks>
public sealed class AkadeOrderedLookupTests
{
    private sealed record Row(int Key, string Tag);

    /// <summary>
    /// The name Akade files this index under is the caller expression of the accessor, so the
    /// builder below and this constant have to spell the lambda the same way. They are next to each
    /// other for that reason; a mismatch fails loudly with "index not found" rather than quietly.
    /// </summary>
    private const string AkadeIndexName = "x => x.Key";

    private static readonly IndexDescriptor Descriptor = new()
    {
        Name = "ix_rows_key",
        Kind = IndexKind.Ordered,
        Columns = [0],
        Directions = [SortDirection.AscNullsLast],
    };

    /// <summary>Ten rows, added in an order that is not their key order.</summary>
    private static IndexedSet<Row> Shuffled() =>
        Build([7, 1, 9, 3, 5, 2, 8, 4, 6, 0]);

    private static IndexedSet<Row> Build(IEnumerable<int> keys)
    {
        var rows = new List<Row>();
        foreach (var key in keys)
        {
            rows.Add(new Row(key, $"r{key}"));
        }

        return rows.ToIndexedSet().WithRangeIndex(x => x.Key).Build();
    }

    private static AkadeScalarIndex<Row, int> Index(IndexedSet<Row>? set = null) =>
        Index(x => x.Key, set);

    private static AkadeScalarIndex<Row, int> Index(Func<Row, int> key, IndexedSet<Row>? set = null) =>
        new(Descriptor, set ?? Shuffled(), key, AkadeIndexName, "akade", "rows");

    private static IndexKeyRange Range(
        int? lower, int? upper, bool lowerInclusive = true, bool upperInclusive = true) =>
        new()
        {
            Lower = lower is null ? [] : [lower.Value],
            Upper = upper is null ? [] : [upper.Value],
            LowerInclusive = lowerInclusive,
            UpperInclusive = upperInclusive,
        };

    private static int[] Keys(IEnumerable<Row> rows)
    {
        var keys = new List<int>();
        foreach (var row in rows)
        {
            keys.Add(row.Key);
        }

        return [.. keys];
    }

    [Fact]
    public void Greater_than_or_equal_yields_in_key_order()
    {
        Assert.Equal([6, 7, 8, 9], Keys(Index().Lookup(Range(6, null))));
    }

    [Fact]
    public void Greater_than_yields_in_key_order()
    {
        Assert.Equal([7, 8, 9], Keys(Index().Lookup(Range(6, null, lowerInclusive: false))));
    }

    [Fact]
    public void Less_than_or_equal_yields_in_key_order()
    {
        Assert.Equal([0, 1, 2, 3], Keys(Index().Lookup(Range(null, 3))));
    }

    [Fact]
    public void Less_than_yields_in_key_order()
    {
        Assert.Equal([0, 1, 2], Keys(Index().Lookup(Range(null, 3, upperInclusive: false))));
    }

    [Fact]
    public void A_two_sided_range_yields_in_key_order()
    {
        Assert.Equal([3, 4, 5, 6], Keys(Index().Lookup(Range(3, 6))));
    }

    [Fact]
    public void A_half_open_range_honours_both_bounds()
    {
        Assert.Equal(
            [4, 5],
            Keys(Index().Lookup(Range(3, 6, lowerInclusive: false, upperInclusive: false))));
    }

    /// <summary>The unbounded case: the whole-range ordered scan of D50, in the index's order.</summary>
    [Fact]
    public void An_unbounded_ordered_lookup_yields_the_whole_set_in_key_order()
    {
        Assert.Equal([0, 1, 2, 3, 4, 5, 6, 7, 8, 9], Keys(Index().Lookup(IndexKeyRange.All)));
    }

    /// <summary>
    /// The guard: a row that arrives out of order fails by name rather than being sorted around.
    /// Fed straight to the guard, because a working Akade index has no way to produce one — which is
    /// the point: this is what happens the day it does.
    /// </summary>
    [Fact]
    public void The_guard_names_the_akade_index_when_a_row_arrives_out_of_order()
    {
        var misordered = new[] { new Row(1, "a"), new Row(5, "b"), new Row(3, "c") };

        var failure = Assert.Throws<SourceContractException>(
            () => Keys(Index().InKeyOrder(misordered)));

        Assert.Equal("akade", failure.SourceId);
        Assert.Equal("rows", failure.Table);
        Assert.Contains(AkadeIndexName, failure.Message, StringComparison.Ordinal);
        Assert.Contains(Descriptor.Name, failure.Message, StringComparison.Ordinal);
        Assert.Contains("'3' after '5'", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>And it fails on the <em>first</em> violation, with the rows before it already out.</summary>
    [Fact]
    public void The_guard_yields_what_came_before_the_violation()
    {
        var misordered = new[] { new Row(1, "a"), new Row(5, "b"), new Row(3, "c") };
        var seen = new List<int>();

        Assert.Throws<SourceContractException>(() =>
        {
            foreach (var row in Index().InKeyOrder(misordered))
            {
                seen.Add(row.Key);
            }
        });

        Assert.Equal([1, 5], seen);
    }

    /// <summary>
    /// Nothing is read past what the consumer asked for. The key accessor counts the rows that went
    /// through the guard, and a consumer that stops after one row costs exactly one.
    /// </summary>
    [Fact]
    public void A_consumer_that_stops_after_one_row_pulls_exactly_one()
    {
        var pulled = 0;
        var index = Index(row =>
        {
            pulled++;
            return row.Key;
        });

        var first = System.Linq.Enumerable.First(index.Lookup(Range(0, null)));

        Assert.Equal(0, first.Key);
        Assert.Equal(1, pulled);
    }

    /// <summary>
    /// The gate: the ordered path allocates for the enumeration and not for the rows. Measured as a
    /// difference rather than a budget — a thousand rows and eight rows through the same code path,
    /// whose readings must not differ by anything that scales.
    /// </summary>
    [Fact]
    public async Task The_ordered_enumeration_allocates_nothing_per_row()
    {
        var set = Build(System.Linq.Enumerable.Range(0, 1024));
        var index = Index(set);

        var (whole, wholeRows) = await AllocationProbe.SteadyStateAsync(
            () => Task.FromResult(Count(index.Lookup(Range(0, null)))));
        var (few, fewRows) = await AllocationProbe.SteadyStateAsync(
            () => Task.FromResult(Count(index.Lookup(Range(1016, null)))));

        Assert.Equal(1024, wholeRows);
        Assert.Equal(8, fewRows);
        Assert.True(
            whole <= few + 256,
            $"{whole} bytes for {wholeRows} rows against {few} for {fewRows}: the difference is "
            + "per-row allocation.");
    }

    private static int Count(IEnumerable<Row> rows)
    {
        var seen = 0;
        foreach (var row in rows)
        {
            seen += row.Key == int.MinValue ? 0 : 1;
        }

        return seen;
    }
}

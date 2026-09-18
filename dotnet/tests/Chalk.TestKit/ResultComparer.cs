using System.Globalization;
using System.Text;
using Apache.Arrow;
using Chalk.Catalog;
using Chalk.Sources;
using Xunit;
using ArrowSchema = Apache.Arrow.Schema;

namespace Chalk.TestKit;

/// <summary>
/// Which of the three comparisons a query asks for (D166, <c>25-coverage-graft.md</c> §3). Only
/// <see cref="TopKUnderTies"/> is a query author's to choose; the other two are read off the plan.
/// </summary>
public enum ResultComparisonMode
{
    /// <summary>The rule of <c>05-testing.md</c> §5: row by row where the plan orders totally, else a multiset.</summary>
    Default = 0,

    /// <summary>The tie-consistent prefix check of D166, for a top-k whose boundary falls inside a tie.</summary>
    TopKUnderTies = 1,
}

/// <summary>
/// What a <see cref="ResultComparisonMode.TopKUnderTies"/> comparison needs from the plan (D166):
/// which rows the query would have returned out of a fully ordered result.
/// </summary>
/// <remarks>
/// The expected side of such a comparison is the reference executor's <b>untruncated</b> ordered
/// rows — the same plan with its <c>Fetch</c> or <c>TopN</c> limit lifted — because the tied group
/// at a boundary is exactly what a truncated result no longer shows.
/// </remarks>
public sealed class TopKBoundary
{
    /// <summary>Rows the query skips before returning any.</summary>
    public required long Offset { get; init; }

    /// <summary>Rows the query returns, or null for "all that are left".</summary>
    public required long? Count { get; init; }
}

/// <summary>How two result sets are allowed to differ (<c>docs/design/05-testing.md</c> §5).</summary>
public sealed class ResultComparisonOptions
{
    /// <summary>Row-by-row and exact everywhere: the strictest reading, for a query with a total order.</summary>
    public static ResultComparisonOptions Ordered { get; } = new();

    /// <summary>Multiset comparison, for a query whose output order is not determined.</summary>
    public static ResultComparisonOptions Multiset { get; } = new() { CompareAsMultiset = true };

    /// <summary>
    /// Compare as multisets rather than row by row. Set when the query has no total order — the
    /// ordering the executor happens to produce is then not part of the contract (A14).
    /// </summary>
    public bool CompareAsMultiset { get; init; }

    /// <summary>
    /// Indexes of FP columns produced by aggregation. Those compare within
    /// <see cref="UlpTolerance"/>; every other column, FP included, compares exactly (D13, A6).
    /// </summary>
    public IReadOnlyList<int> AggregatedFloatColumns { get; init; } = [];

    public int UlpTolerance { get; init; } = 4;

    /// <summary>
    /// The ORDER BY the actual output must honour even when the comparison is a multiset: a
    /// non-decreasing key sequence with the right null placement (§5).
    /// </summary>
    public IReadOnlyList<OrderKeyExpectation> OrderKeys { get; init; } = [];

    /// <summary>Which comparison to make (D166). <see cref="ResultComparisonMode.Default"/> unless a query says.</summary>
    public ResultComparisonMode Mode { get; init; }

    /// <summary>
    /// The plan's fetch and offset, required by <see cref="ResultComparisonMode.TopKUnderTies"/> and
    /// meaningless otherwise.
    /// </summary>
    public TopKBoundary? Boundary { get; init; }
}

/// <param name="Column">Index into the output row.</param>
/// <param name="Descending">Whether the key is DESC.</param>
/// <param name="NullsFirst">Where NULLs go.</param>
public readonly record struct OrderKeyExpectation(int Column, bool Descending, bool NullsFirst);

/// <summary>
/// The differential comparison of <c>docs/design/05-testing.md</c> §5, shared by the executor's own
/// tests and the corpus tests: schemas must be identical, values exact for every kind but the
/// floating-point results of an aggregation, and unordered queries compare as multisets.
/// </summary>
public static class ResultComparer
{
    /// <summary>Asserts that two result sets agree under <paramref name="options"/>.</summary>
    public static void AssertEquivalent(
        IReadOnlyList<RecordBatch> expected,
        IReadOnlyList<RecordBatch> actual,
        ResultComparisonOptions options)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        ArgumentNullException.ThrowIfNull(options);

        var expectedSchema = SchemaOf(expected);
        var actualSchema = SchemaOf(actual);
        if (expectedSchema is not null && actualSchema is not null)
        {
            Assert.True(
                ArrowTypeMapping.AreEquivalent(expectedSchema, actualSchema),
                $"Schemas differ.\n  expected {ArrowTypeMapping.DescribeArrow(expectedSchema)}"
                + $"\n  actual   {ArrowTypeMapping.DescribeArrow(actualSchema)}");
        }

        var types = expectedSchema is null ? [] : BatchReader.TypesOf(expectedSchema);
        var expectedRows = BatchReader.ToStorageRows(expected);
        var actualRows = BatchReader.ToStorageRows(actual);

        if (options.Mode == ResultComparisonMode.TopKUnderTies)
        {
            AssertTopKUnderTies(expectedRows, actualRows, types, options);
            return;
        }

        Assert.True(
            expectedRows.Count == actualRows.Count,
            $"Row counts differ: expected {expectedRows.Count}, actual {actualRows.Count}.");

        AssertOrderKeys(actualRows, types, options);

        if (options.CompareAsMultiset)
        {
            expectedRows.Sort(CanonicalOrder);
            actualRows.Sort(CanonicalOrder);
        }

        var tolerant = new HashSet<int>(options.AggregatedFloatColumns);
        for (var row = 0; row < expectedRows.Count; row++)
        {
            for (var column = 0; column < expectedRows[row].Length; column++)
            {
                var left = expectedRows[row][column];
                var right = actualRows[row][column];
                var equal = tolerant.Contains(column)
                    ? WithinUlps(left, right, options.UlpTolerance)
                    : Same(left, right);

                Assert.True(
                    equal,
                    $"Row {row}, column {column}"
                    + (types.Count > column ? $" ({types[column]})" : string.Empty)
                    + $": expected {Render(left)}, actual {Render(right)}.");
            }
        }
    }

    /// <summary>
    /// The tie-consistent prefix check (D166, <c>25-coverage-graft.md</c> §3).
    /// <paramref name="expectedRows"/> is the reference executor's <b>untruncated</b> ordered
    /// result; <paramref name="actualRows"/> is what the query returned.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <c>LIMIT</c> whose boundary falls inside a tie has more than one right answer, and the
    /// unique-key rule of <c>10-conformance-and-fixes.md</c> §1 avoids the case rather than testing
    /// it. This mode tests it: the answer is pinned everywhere it is determined and only counted
    /// where it is not.
    /// </para>
    /// <para>
    /// §3 names one boundary, the key of the last row the query would return. There are two: an
    /// <c>OFFSET</c> can land inside a tie as well, and §2's A1 asks for exactly that case, so the
    /// first returned row's key is a boundary on the same terms (ADR 0024). Between the two, every
    /// row is determined and must be present; at either, only how many rows come from the tied
    /// group is.
    /// </para>
    /// </remarks>
    public static void AssertTopKUnderTies(
        List<object?[]> expectedRows,
        List<object?[]> actualRows,
        IReadOnlyList<ChalkType> types,
        ResultComparisonOptions options)
    {
        ArgumentNullException.ThrowIfNull(expectedRows);
        ArgumentNullException.ThrowIfNull(actualRows);
        ArgumentNullException.ThrowIfNull(options);
        var boundary = options.Boundary
            ?? throw new ArgumentException(
                "a top-k-under-ties comparison needs the plan's fetch and offset", nameof(options));
        Assert.True(
            options.OrderKeys.Count > 0,
            "a top-k-under-ties comparison needs the plan's ordering; this plan claims none.");

        var offset = (int)Math.Min(boundary.Offset, expectedRows.Count);
        var count = boundary.Count is { } wanted
            ? (int)Math.Min(wanted, expectedRows.Count - offset)
            : expectedRows.Count - offset;

        // 1. The cardinality is determined even when the rows are not.
        Assert.True(
            actualRows.Count == count,
            $"Row counts differ: the plan takes {count} of {expectedRows.Count} ordered rows "
            + $"(offset {boundary.Offset}, fetch {boundary.Count?.ToString(CultureInfo.InvariantCulture) ?? "all"}), "
            + $"actual {actualRows.Count}.");

        // 2. Whatever it returned, it returned in order.
        AssertOrderKeys(actualRows, types, options);

        if (count == 0)
        {
            return;
        }

        var low = expectedRows[offset];
        var high = expectedRows[offset + count - 1];

        // 3. Every row is inside the window the two boundary keys mark out.
        for (var i = 0; i < actualRows.Count; i++)
        {
            Assert.True(
                CompareKeys(low, actualRows[i], options, types) <= 0,
                $"Row {i} sorts before the first row the plan would return: "
                + $"{RenderKeys(actualRows[i], options)} against {RenderKeys(low, options)}.");
            Assert.True(
                CompareKeys(actualRows[i], high, options, types) <= 0,
                $"Row {i} sorts after the last row the plan would return: "
                + $"{RenderKeys(actualRows[i], options)} against {RenderKeys(high, options)}.");
        }

        // 4. Every row strictly between the two boundary keys is determined and must be there,
        //    value for value; 5. and at a boundary key, the rows must come from that key's tied
        //    group in the reference and there must be the right number of them.
        var lowGroup = expectedRows.Where(r => CompareKeys(r, low, options, types) == 0).ToList();
        var highGroup = expectedRows.Where(r => CompareKeys(r, high, options, types) == 0).ToList();
        var interior = expectedRows
            .Where(r => CompareKeys(low, r, options, types) < 0 && CompareKeys(r, high, options, types) < 0)
            .ToList();

        var remaining = new List<object?[]>(actualRows);
        foreach (var required in interior)
        {
            var found = remaining.FindIndex(r => SameRow(required, r));
            Assert.True(
                found >= 0,
                $"The row {RenderRow(required)} sorts strictly between the two boundary keys, so it "
                + "is not a tie and had to be returned, and it was not.");
            remaining.RemoveAt(found);
        }

        // What is left must be boundary rows, each one a row the reference has at that key.
        var pool = new List<object?[]>(lowGroup);
        if (!ReferenceEquals(low, high) && CompareKeys(low, high, options, types) != 0)
        {
            pool.AddRange(highGroup);
        }

        foreach (var row in remaining)
        {
            var found = pool.FindIndex(r => SameRow(row, r));
            Assert.True(
                found >= 0,
                $"The row {RenderRow(row)} is at a boundary key but is not one of the reference's "
                + "rows tied there.");
            pool.RemoveAt(found);
        }
    }

    /// <summary>Two rows compared on the order keys alone, in the plan's directions.</summary>
    private static int CompareKeys(
        object?[] left,
        object?[] right,
        ResultComparisonOptions options,
        IReadOnlyList<ChalkType> types)
    {
        foreach (var key in options.OrderKeys)
        {
            var comparison = CompareKey(
                left[key.Column], right[key.Column], key,
                types.Count > key.Column ? types[key.Column] : default);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return 0;
    }

    private static bool SameRow(object?[] left, object?[] right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        for (var i = 0; i < left.Length; i++)
        {
            if (!Same(left[i], right[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static string RenderKeys(object?[] row, ResultComparisonOptions options) =>
        "(" + string.Join(", ", options.OrderKeys.Select(k => Render(row[k.Column]))) + ")";

    private static string RenderRow(object?[] row) =>
        "(" + string.Join(", ", row.Select(Render)) + ")";

    /// <summary>Every row, as storage values — handy when a test wants to look at the data itself.</summary>
    public static List<object?[]> Rows(IReadOnlyList<RecordBatch> batches) => BatchReader.ToStorageRows(batches);

    /// <summary>
    /// Asserts that these batches are in the given key order, on their own. The differential
    /// comparison does this as part of a comparison; M2's monotone-key check does it to one
    /// operator's output, where there is nothing to compare against (11-m2-index-support.md §5).
    /// </summary>
    public static void AssertOrdered(
        IReadOnlyList<RecordBatch> batches, IReadOnlyList<OrderKeyExpectation> keys)
    {
        ArgumentNullException.ThrowIfNull(batches);
        ArgumentNullException.ThrowIfNull(keys);
        AssertOrderKeys(
            BatchReader.ToStorageRows(batches),
            batches.Count == 0 ? [] : BatchReader.TypesOf(batches[0].Schema),
            new ResultComparisonOptions { OrderKeys = keys });
    }

    private static ArrowSchema? SchemaOf(IReadOnlyList<RecordBatch> batches) =>
        batches.Count == 0 ? null : batches[0].Schema;

    private static void AssertOrderKeys(
        List<object?[]> rows, IReadOnlyList<ChalkType> types, ResultComparisonOptions options)
    {
        if (options.OrderKeys.Count == 0)
        {
            return;
        }

        for (var i = 1; i < rows.Count; i++)
        {
            foreach (var key in options.OrderKeys)
            {
                var comparison = CompareKey(
                    rows[i - 1][key.Column], rows[i][key.Column], key, types.Count > key.Column ? types[key.Column] : default);
                Assert.True(
                    comparison <= 0,
                    $"Rows {i - 1} and {i} break the ORDER BY on column {key.Column}: "
                    + $"{Render(rows[i - 1][key.Column])} then {Render(rows[i][key.Column])}.");
                if (comparison < 0)
                {
                    break;
                }
            }
        }
    }

    private static int CompareKey(object? left, object? right, OrderKeyExpectation key, ChalkType type)
    {
        _ = type;
        if (left is null || right is null)
        {
            if (left is null && right is null)
            {
                return 0;
            }

            return (left is null) == key.NullsFirst ? -1 : 1;
        }

        var comparison = CompareValues(left, right);
        return key.Descending ? -comparison : comparison;
    }

    private static int CanonicalOrder(object?[] left, object?[] right)
    {
        for (var i = 0; i < left.Length; i++)
        {
            if (left[i] is null || right[i] is null)
            {
                if (left[i] is null && right[i] is null)
                {
                    continue;
                }

                return left[i] is null ? -1 : 1;
            }

            var comparison = CompareValues(left[i], right[i]);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return 0;
    }

    private static int CompareValues(object? left, object? right) => (left, right) switch
    {
        (bool a, bool b) => a.CompareTo(b),
        (long a, long b) => a.CompareTo(b),
        (double a, double b) => Real(a, b),
        (float a, float b) => Real(a, b),
        (decimal a, decimal b) => a.CompareTo(b),
        (string a, string b) => Utf8(a, b),
        (byte[] a, byte[] b) => a.AsSpan().SequenceCompareTo(b),

        // A LIST has no SQL ordering (D58); the multiset comparison still needs a total order to
        // sort by, so lists are ordered by their rendered form, which is stable and total.
        _ => string.CompareOrdinal(Render(left), Render(right)),
    };

    private static int Real(double a, double b)
    {
        if (double.IsNaN(a))
        {
            return double.IsNaN(b) ? 0 : 1;
        }

        return double.IsNaN(b) ? -1 : a < b ? -1 : a > b ? 1 : 0;
    }

    private static int Utf8(string a, string b) =>
        Encoding.UTF8.GetBytes(a).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(b));

    /// <summary>Exact equality, with NaN equal to NaN so that a NaN result is comparable at all.</summary>
    private static bool Same(object? left, object? right) => (left, right) switch
    {
        (null, null) => true,
        (null, _) or (_, null) => false,
        (double a, double b) => a.Equals(b),
        (float a, float b) => a.Equals(b),
        (byte[] a, byte[] b) => a.AsSpan().SequenceEqual(b),

        // A LIST cell is its elements, compared elementwise (D58). Two lists are equal when they
        // hold the same values in the same order; nothing about a list is order-insensitive.
        (object?[] a, object?[] b) =>
            a.Length == b.Length && a.Zip(b).All(pair => Same(pair.First, pair.Second)),
        _ => left.Equals(right),
    };

    private static bool WithinUlps(object? left, object? right, int tolerance) => (left, right) switch
    {
        (double a, double b) => Ulps(a, b) <= tolerance,
        (float a, float b) => Ulps(a, b) <= tolerance,
        (object?[] a, object?[] b) =>
            a.Length == b.Length
            && a.Zip(b).All(pair => WithinUlps(pair.First, pair.Second, tolerance)),
        _ => Same(left, right),
    };

    private static long Ulps(double a, double b)
    {
        if (a.Equals(b))
        {
            return 0;
        }

        if (double.IsNaN(a) || double.IsNaN(b) || double.IsInfinity(a) || double.IsInfinity(b))
        {
            return long.MaxValue;
        }

        var x = Monotonic(BitConverter.DoubleToInt64Bits(a));
        var y = Monotonic(BitConverter.DoubleToInt64Bits(b));
        return Math.Abs(x - y);

        static long Monotonic(long bits) => bits < 0 ? long.MinValue - bits : bits;
    }

    private static string Render(object? value) => value switch
    {
        null => "NULL",
        bool flag => flag ? "true" : "false",
        byte[] bytes => "0x" + Convert.ToHexStringLower(bytes),
        object?[] list => "[" + string.Join(", ", list.Select(Render)) + "]",
        double real => real.ToString("R", CultureInfo.InvariantCulture),
        float single => single.ToString("R", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "NULL",
    };
}

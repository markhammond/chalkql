using System.Globalization;
using Chalk.Sources;
using Chalk.Sources.Poco;
using SortDirection = Chalk.Ir.SortDirection;

namespace Chalk.TestKit;

/// <summary>
/// Proves an <see cref="IPocoIndex{T}"/> against the full scan it replaces (D35).
///
/// <para>
/// A host-supplied index's descriptor is trusted as declared: the planner deletes sorts and chooses
/// lookups on the strength of it, and Chalk never checks the structure's answers at query time. So
/// an adapter author runs this once, over their own rows, and the claim becomes a tested one. It is
/// the same idea as M4's conformance kit for sources, and the same shape: a battery of ranges, each
/// answered twice.
/// </para>
///
/// <para>
/// What it checks: the rows a range matches are exactly the rows a filter would keep; an
/// <see cref="Chalk.Ir.IndexKind.Ordered"/> index hands them back in key order; a unique index has
/// no two rows with the same key; a positional index's positions name the same rows as its rows; and
/// any <c>DistinctCount</c> the structure offers is the truth rather than an estimate.
/// </para>
/// </summary>
public static class PocoIndexConformance
{
    /// <summary>How many distinct values of each key column the generated battery samples.</summary>
    private const int SampledValues = 8;

    /// <summary>
    /// Runs the conformance battery, throwing <see cref="PocoIndexConformanceException"/> on the
    /// first disagreement.
    /// </summary>
    /// <param name="index">The index under test.</param>
    /// <param name="rows">The rows it was built over, in the order a scan would produce them.</param>
    /// <param name="keys">
    /// One accessor per key column, in key order, returning the value the column holds — the same
    /// shape Chalk compares, which for a DATE column is days since the epoch and for a TIMESTAMP
    /// column is <c>Type.Precision</c> units (<c>04-client.md</c> §7.2).
    /// </param>
    /// <param name="ranges">
    /// Ranges to check, or null to generate a battery from the data: equality on sampled values of
    /// every key prefix, open and closed ranges around them, the unbounded range, and a range no row
    /// can match.
    /// </param>
    /// <returns>What was checked, for a test that wants to assert the battery was not empty.</returns>
    public static PocoIndexConformanceReport Verify<T>(
        IPocoIndex<T> index,
        IReadOnlyCollection<T> rows,
        IReadOnlyList<Func<T, object?>> keys,
        IEnumerable<IndexKeyRange>? ranges = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(keys);

        var descriptor = index.Descriptor;
        if (keys.Count != descriptor.Columns.Count)
        {
            throw new PocoIndexConformanceException(
                descriptor.Name,
                $"the index declares {descriptor.Columns.Count} key column(s) but {keys.Count} "
                + "accessor(s) were given; they must correspond, in key order");
        }

        var directions = descriptor.Key().Select(k => k.Direction).ToArray();
        var material = rows as IReadOnlyList<T> ?? [.. rows];
        var battery = (ranges ?? Battery(index, material, keys, directions)).ToArray();

        VerifyUnique(index, material, keys, directions);

        var checkedRanges = 0;
        foreach (var range in battery)
        {
            Compare(index, material, keys, directions, range);
            checkedRanges++;
        }

        VerifyDistinctCounts(index, material, keys, directions);

        return new PocoIndexConformanceReport
        {
            Index = descriptor.Name,
            Rows = material.Count,
            Ranges = checkedRanges,
        };
    }

    /// <summary>One range, answered by the index and by a full scan, compared as sequences.</summary>
    private static void Compare<T>(
        IPocoIndex<T> index,
        IReadOnlyList<T> rows,
        IReadOnlyList<Func<T, object?>> keys,
        IReadOnlyList<SortDirection> directions,
        IndexKeyRange range)
    {
        var expected = rows.Where(row => range.Contains(KeyOf(row, keys), directions)).ToList();
        List<T> actual;
        try
        {
            actual = [.. index.Lookup(range)];
        }
        catch (SourceContractException) when (index.Descriptor.Kind == Chalk.Ir.IndexKind.Hash)
        {
            // A hash index is allowed to refuse a non-equality range; that is the contract, not a
            // failure. It is not allowed to refuse an equality one.
            if (IsEquality(range, index.Descriptor.Columns.Count))
            {
                throw;
            }

            return;
        }

        if (expected.Count != actual.Count)
        {
            throw new PocoIndexConformanceException(
                index.Descriptor.Name,
                $"the range {range} matches {expected.Count} row(s) by full scan but the index "
                + $"returned {actual.Count}");
        }

        if (index.Descriptor.Kind == Chalk.Ir.IndexKind.Ordered)
        {
            // An ordered index promises key order, which for equal keys need not be the scan's
            // order — so the rows are compared by key, and the key order is asserted separately.
            AssertKeyOrder(index.Descriptor.Name, actual, keys, directions, range);
        }

        var expectedKeys = expected.Select(row => KeyOf(row, keys)).ToList();
        var actualKeys = actual.Select(row => KeyOf(row, keys)).ToList();
        expectedKeys.Sort((left, right) => Compare(left, right, directions));
        actualKeys.Sort((left, right) => Compare(left, right, directions));

        for (var i = 0; i < expectedKeys.Count; i++)
        {
            if (Compare(expectedKeys[i], actualKeys[i], directions) != 0)
            {
                throw new PocoIndexConformanceException(
                    index.Descriptor.Name,
                    $"the range {range} returned a row with key ({Render(actualKeys[i])}) where the "
                    + $"full scan has ({Render(expectedKeys[i])})");
            }
        }

        if (index is IPositionalPocoIndex<T> positional)
        {
            var positions = positional.LookupPositions(range).ToList();
            if (positions.Count != actual.Count)
            {
                throw new PocoIndexConformanceException(
                    index.Descriptor.Name,
                    $"the range {range} returned {actual.Count} row(s) but {positions.Count} position(s)");
            }

            for (var i = 0; i < positions.Count; i++)
            {
                if (positions[i] < 0 || positions[i] >= rows.Count)
                {
                    throw new PocoIndexConformanceException(
                        index.Descriptor.Name,
                        $"position {positions[i]} is out of range for {rows.Count} rows");
                }

                if (Compare(KeyOf(rows[positions[i]], keys), KeyOf(actual[i], keys), directions) != 0)
                {
                    throw new PocoIndexConformanceException(
                        index.Descriptor.Name,
                        $"position {positions[i]} names a row whose key is not the one Lookup "
                        + $"returned at the same offset, for the range {range}");
                }
            }
        }
    }

    private static void AssertKeyOrder<T>(
        string name,
        IReadOnlyList<T> rows,
        IReadOnlyList<Func<T, object?>> keys,
        IReadOnlyList<SortDirection> directions,
        IndexKeyRange range)
    {
        for (var i = 1; i < rows.Count; i++)
        {
            if (Compare(KeyOf(rows[i - 1], keys), KeyOf(rows[i], keys), directions) > 0)
            {
                throw new PocoIndexConformanceException(
                    name,
                    $"an ORDERED index must hand back rows in key order, but rows {i - 1} and {i} of "
                    + $"the range {range} go backwards");
            }
        }
    }

    private static void VerifyUnique<T>(
        IPocoIndex<T> index,
        IReadOnlyList<T> rows,
        IReadOnlyList<Func<T, object?>> keys,
        IReadOnlyList<SortDirection> directions)
    {
        if (!index.Descriptor.Unique)
        {
            return;
        }

        var sorted = rows.Select(row => KeyOf(row, keys)).ToList();
        sorted.Sort((left, right) => Compare(left, right, directions));
        for (var i = 1; i < sorted.Count; i++)
        {
            if (Compare(sorted[i - 1], sorted[i], directions) == 0)
            {
                throw new PocoIndexConformanceException(
                    index.Descriptor.Name,
                    $"the index is declared unique but two rows share the key ({Render(sorted[i])})");
            }
        }
    }

    private static void VerifyDistinctCounts<T>(
        IPocoIndex<T> index,
        IReadOnlyList<T> rows,
        IReadOnlyList<Func<T, object?>> keys,
        IReadOnlyList<SortDirection> directions)
    {
        for (var prefix = 1; prefix <= keys.Count; prefix++)
        {
            var claimed = index.DistinctCount(prefix);
            if (claimed is null)
            {
                continue;
            }

            var sorted = rows
                .Select(row => KeyOf(row, keys).Take(prefix).ToArray())
                .ToList();
            sorted.Sort((left, right) => Compare(left, right, directions));

            long distinct = sorted.Count == 0 ? 0 : 1;
            for (var i = 1; i < sorted.Count; i++)
            {
                if (Compare(sorted[i - 1], sorted[i], directions) != 0)
                {
                    distinct++;
                }
            }

            if (claimed != distinct)
            {
                throw new PocoIndexConformanceException(
                    index.Descriptor.Name,
                    $"DistinctCount({prefix}) claims {claimed} but the rows hold {distinct}. "
                    + "Return null rather than an estimate: the planner divides by this.");
            }
        }
    }

    /// <summary>
    /// The generated battery: equality on sampled values of every key prefix, half-open and closed
    /// ranges around the sampled values of the first column, the unbounded range, and one range
    /// nothing can match.
    /// </summary>
    private static IEnumerable<IndexKeyRange> Battery<T>(
        IPocoIndex<T> index,
        IReadOnlyList<T> rows,
        IReadOnlyList<Func<T, object?>> keys,
        IReadOnlyList<SortDirection> directions)
    {
        yield return IndexKeyRange.All;

        var ordered = index.Descriptor.Kind == Chalk.Ir.IndexKind.Ordered;
        for (var prefix = 1; prefix <= keys.Count; prefix++)
        {
            foreach (var value in Sample(rows, keys, prefix, directions))
            {
                yield return IndexKeyRange.Equality(value);

                if (!ordered || prefix != 1)
                {
                    continue;
                }

                yield return new IndexKeyRange { Lower = value, LowerInclusive = true, Upper = [] };
                yield return new IndexKeyRange { Lower = value, LowerInclusive = false, Upper = [] };
                yield return new IndexKeyRange { Lower = [], Upper = value, UpperInclusive = true };
                yield return new IndexKeyRange { Lower = [], Upper = value, UpperInclusive = false };
            }
        }

        if (!ordered)
        {
            yield break;
        }

        // Closed ranges between consecutive sampled values of the first key column.
        var points = Sample(rows, keys, 1, directions).ToList();
        for (var i = 1; i < points.Count; i++)
        {
            yield return new IndexKeyRange
            {
                Lower = points[i - 1], LowerInclusive = true, Upper = points[i], UpperInclusive = true,
            };
            yield return new IndexKeyRange
            {
                Lower = points[i - 1], LowerInclusive = false, Upper = points[i], UpperInclusive = false,
            };

            // Reversed, which matches nothing: a range whose lower bound is above its upper is not
            // an error, it is empty, and an index that returns rows for it is wrong.
            yield return new IndexKeyRange
            {
                Lower = points[i], LowerInclusive = true, Upper = points[i - 1], UpperInclusive = true,
            };
        }
    }

    /// <summary>Distinct key prefixes present in the rows, in key order, evenly sampled.</summary>
    private static IEnumerable<object?[]> Sample<T>(
        IReadOnlyList<T> rows,
        IReadOnlyList<Func<T, object?>> keys,
        int prefix,
        IReadOnlyList<SortDirection> directions)
    {
        var values = rows
            .Select(row => KeyOf(row, keys).Take(prefix).ToArray())
            .Where(key => key.All(value => value is not null))
            .ToList();
        if (values.Count == 0)
        {
            yield break;
        }

        values.Sort((left, right) => Compare(left, right, directions));

        var distinct = new List<object?[]> { values[0] };
        for (var i = 1; i < values.Count; i++)
        {
            if (Compare(values[i - 1], values[i], directions) != 0)
            {
                distinct.Add(values[i]);
            }
        }

        var stride = Math.Max(1, distinct.Count / SampledValues);
        for (var i = 0; i < distinct.Count; i += stride)
        {
            yield return distinct[i];
        }
    }

    private static object?[] KeyOf<T>(T row, IReadOnlyList<Func<T, object?>> keys)
    {
        var key = new object?[keys.Count];
        for (var i = 0; i < key.Length; i++)
        {
            key[i] = keys[i](row);
        }

        return key;
    }

    private static int Compare(
        IReadOnlyList<object?> left, IReadOnlyList<object?> right, IReadOnlyList<SortDirection> directions)
    {
        var columns = Math.Min(left.Count, right.Count);
        for (var i = 0; i < columns; i++)
        {
            var comparison = SourceValueOrder.Compare(
                left[i], right[i], i < directions.Count ? directions[i] : SortDirection.AscNullsLast);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return left.Count.CompareTo(right.Count);
    }

    private static bool IsEquality(IndexKeyRange range, int keyColumns) =>
        range.LowerInclusive
        && range.UpperInclusive
        && range.Lower.Count == keyColumns
        && range.Upper.Count == keyColumns
        && range.Lower.Zip(range.Upper).All(pair => Equals(pair.First, pair.Second));

    private static string Render(IReadOnlyList<object?> key) =>
        string.Join(", ", key.Select(v => v switch
        {
            null => "NULL",
            string text => $"'{text}'",
            _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? string.Empty,
        }));
}

/// <summary>What <see cref="PocoIndexConformance.Verify{T}"/> checked.</summary>
public sealed class PocoIndexConformanceReport
{
    public required string Index { get; init; }

    public required int Rows { get; init; }

    /// <summary>How many ranges were answered twice and compared.</summary>
    public required int Ranges { get; init; }

    public override string ToString() =>
        $"{Index}: {Ranges} range(s) over {Rows} row(s) agree with a full scan";
}

/// <summary>An index disagreed with the full scan it replaces.</summary>
public sealed class PocoIndexConformanceException : Exception
{
    public PocoIndexConformanceException(string index, string detail)
        : base($"Index '{index}' does not conform: {detail}.")
    {
        Index = index;
    }

    public string Index { get; }
}

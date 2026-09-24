using Chalk.Catalog;
using StatisticsLevel = Chalk.Ir.StatisticsLevel;

namespace Chalk.Sources.Poco;

/// <summary>
/// Statistics a host supplies instead of, or on top of, the ones Chalk computes (D36). Columns are
/// named the way the catalog names them, not the way the CLR member is spelled.
/// </summary>
public sealed class TableStatistics
{
    /// <summary>Rows in the table. -1 = unknown.</summary>
    public long RowCount { get; init; } = -1;

    /// <summary>How much <see cref="RowCount"/> is worth.</summary>
    public Chalk.Ir.RowCountKind RowCountKind { get; init; } = Chalk.Ir.RowCountKind.Unspecified;

    /// <summary>Per-column statistics, by SQL column name (case-insensitively). Columns may be missing.</summary>
    public IReadOnlyDictionary<string, ColumnStatistics> Columns { get; init; } =
        new Dictionary<string, ColumnStatistics>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>What a table was told to do about statistics, decided at <c>Build()</c>.</summary>
internal sealed class PocoStatisticsPlan
{
    public required StatisticsLevel Level { get; init; }

    public required int HistogramBuckets { get; init; }

    /// <summary>Host overrides, by table column index. Applied over whatever was computed.</summary>
    public required IReadOnlyDictionary<int, ColumnStatistics> Overrides { get; init; }

    /// <summary>A host-supplied replacement for the whole table's statistics.</summary>
    public Func<TableStatistics>? Supplier { get; init; }

    /// <summary>
    /// How much the table must have grown since its column statistics were last computed before they
    /// are computed again (D271 (g)): a fraction of the row count they were taken over. Zero — the
    /// default — recomputes every time, which is what every table did before D271.
    /// </summary>
    public double RefreshWhenGrownBy { get; init; }
}

/// <summary>
/// Computes the statistics a POCO table reports (§2). BASIC is the default and costs one pass over
/// the rows: <c>null_count</c>, <c>min</c> and <c>max</c> for every column, and an exact
/// <c>distinct_count</c> for every column an index or a collation names as a key. HISTOGRAM adds
/// equi-height buckets and a most-common-value list per column, from a deterministic sample when the
/// table is large.
/// </summary>
/// <remarks>
/// This runs at registration — <c>Build()</c> and every <c>DescribeSchema()</c> where the collection
/// has changed — never on the scan path, so it is allowed to box.
/// </remarks>
internal static class PocoStatistics
{
    private const int MaxStatisticsSample = 65_536;
    
    /// <summary>Above this many rows the histogram pass samples rather than reading every row (§2).</summary>
    private const int ExactHistogramLimit = 1_000_000;

    /// <summary>How many most-common values a HISTOGRAM column reports.</summary>
    private const int FrequentValueCount = 16;
    

    public static ColumnStatistics[] Compute<T>(
        PocoColumn<T>[] columns,
        IReadOnlyCollection<T> rows,
        IReadOnlyList<CollationDescriptor> collations,
        IReadOnlyList<IPocoIndex<T>> indexes,
        PocoStatisticsPlan plan)
    {
        var columnCount = columns.Length;
        var result = new ColumnStatistics[columnCount];

        if (plan.Level == StatisticsLevel.Unknown)
        {
            Array.Fill(result, ColumnStatistics.Unknown);
            return Apply(result, columns, plan);
        }

        if (rows.Count == 0)
        {
            for (var c = 0; c < columnCount; c++)
            {
                if (IsComposite(columns[c]))
                {
                    result[c] = ColumnStatistics.Unknown;
                    continue;
                }

                result[c] = new ColumnStatistics
                {
                    Level = plan.Level,
                    DistinctCount = -1,
                    NullCount = 0,
                    Min = null,
                    Max = null,
                    Histogram = [],
                    FrequentValues = [],
                };
            }

            return Apply(result, columns, plan);
        }

        var counted = new bool[columnCount];

        foreach (var collation in collations)
        {
            foreach (var key in collation.Keys)
            {
                counted[key.Column] = true;
            }
        }

        foreach (var index in indexes)
        {
            foreach (var column in index.Descriptor.Columns)
            {
                counted[column] = true;
            }
        }

        //
        // If a column is the first key of a built-in/host index and that
        // index can give us exact prefix cardinality, don't recompute it.
        //
        var exactDistinct = new long[columnCount];
        Array.Fill(exactDistinct, -1);

        foreach (var index in indexes)
        {
            var descriptor = index.Descriptor;

            if (descriptor.Columns.Count == 0)
            {
                continue;
            }

            var column = descriptor.Columns[0];
            var indexDistinct = index.DistinctCount(1);

            if (indexDistinct is { } value)
            {
                exactDistinct[column] = value;
            }
        }

        var accessors = new Func<T, object?>[columnCount];
        var ordered = new bool[columnCount];

        for (var c = 0; c < columnCount; c++)
        {
            // D302: a composite column carries no statistics at all, so its records are never read here.
            accessors[c] = IsComposite(columns[c]) ? static _ => null : columns[c].CompileLogicalAccessor();
            ordered[c] = columns[c].Type.Kind != Chalk.Ir.TypeKind.List && !IsComposite(columns[c]);
        }

        var nulls = new int[columnCount];
        var minimum = new object?[columnCount];
        var maximum = new object?[columnCount];

        var comparers =
            new SourceValueOrder.ISourceValueComparer?[columnCount];

        var distinct =
            new HashSet<object>?[columnCount];

        var histogram =
            plan.Level == StatisticsLevel.Histogram;

        var samples =
            histogram
                ? new List<object>?[columnCount]
                : null;

        for (var c = 0; c < columnCount; c++)
        {
            if (!ordered[c])
            {
                continue;
            }

            if (counted[c] && exactDistinct[c] < 0)
            {
                distinct[c] =
                    new HashSet<object>(PocoValueEquality.Instance);
            }

            if (histogram)
            {
                samples![c] =
                    new List<object>(
                        Math.Min(rows.Count, MaxStatisticsSample));
            }
        }

        var target =
            Math.Min(rows.Count, MaxStatisticsSample);

        var stride =
            Math.Max(
                1,
                (rows.Count + target - 1) / target);

        var row = 0;
        var sampledRows = 0;

        foreach (var item in rows)
        {
            if (row % stride != 0)
            {
                row++;
                continue;
            }

            sampledRows++;

            for (var c = 0; c < columnCount; c++)
            {
                var value = accessors[c](item);

                if (value is null)
                {
                    nulls[c]++;
                    continue;
                }

                if (!ordered[c])
                {
                    continue;
                }

                var comparer = comparers[c];

                if (comparer is null)
                {
                    // Statistics want ascending value ordering. NULL position
                    // is immaterial because only non-NULLs reach here.
                    comparer =
                        SourceValueOrder.CreateComparer(
                            value,
                            Chalk.Ir.SortDirection.AscNullsFirst);

                    comparers[c] = comparer;
                    minimum[c] = value;
                    maximum[c] = value;
                }
                else
                {
                    if (comparer.Compare(value, minimum[c]) < 0)
                    {
                        minimum[c] = value;
                    }

                    if (comparer.Compare(value, maximum[c]) > 0)
                    {
                        maximum[c] = value;
                    }
                }

                distinct[c]?.Add(value);
                samples?[c]?.Add(value);
            }

            row++;
        }

        var scale =
            sampledRows == 0
                ? 0d
                : (double)rows.Count / sampledRows;

        for (var c = 0; c < columnCount; c++)
        {
            var estimatedNulls =
                Math.Min(
                    rows.Count,
                    (long)Math.Round(
                        nulls[c] * scale,
                        MidpointRounding.AwayFromZero));

            long distinctCount;

            if (exactDistinct[c] >= 0)
            {
                distinctCount = exactDistinct[c];
            }
            else if (distinct[c] is { } set)
            {
                if (sampledRows == rows.Count)
                {
                    distinctCount = set.Count;
                }
                else
                {
                    // Deliberately simple planner estimate. It is bounded by
                    // the estimated number of non-NULL rows.
                    var nonNullRows =
                        Math.Max(0L, rows.Count - estimatedNulls);

                    distinctCount =
                        Math.Min(
                            nonNullRows,
                            (long)Math.Ceiling(set.Count * scale));
                }
            }
            else
            {
                distinctCount = -1;
            }

            HistogramBucket[] buckets = [];
            FrequentValue[] frequent = [];

            if (samples?[c] is { Count: > 0 } sample)
            {
                var comparer = comparers[c]!;

                sample.Sort(
                    (left, right) =>
                        comparer.Compare(left, right));

                buckets =
                    Buckets(
                        sample,
                        plan.HistogramBuckets,
                        rows.Count,
                        comparer);

                frequent =
                    Frequent(
                        sample,
                        rows.Count,
                        comparer);
            }

            if (IsComposite(columns[c]))
            {
                result[c] = ColumnStatistics.Unknown;
                continue;
            }

            result[c] = new ColumnStatistics
            {
                Level = plan.Level,
                DistinctCount = distinctCount,
                NullCount = estimatedNulls,
                Min = minimum[c],
                Max = maximum[c],
                Histogram = buckets,
                FrequentValues = frequent,
            };
        }

        return Apply(result, columns, plan);
    }

    /// <summary>A composite column carries no statistics (D302): none of its values compares with another.</summary>
    private static bool IsComposite<T>(PocoColumn<T> column) => column.Type.Kind == Chalk.Ir.TypeKind.Composite;

    /// <summary>Host overrides win over anything computed; a supplier replaces the lot.</summary>
    private static ColumnStatistics[] Apply<T>(
        ColumnStatistics[] computed, PocoColumn<T>[] columns, PocoStatisticsPlan plan)
    {
        if (plan.Supplier is { } supplier)
        {
            var supplied = supplier()
                ?? throw new CatalogValidationException(
                    "statistics", "the Statistics(Func<TableStatistics>) delegate returned null");
            for (var c = 0; c < columns.Length; c++)
            {
                if (supplied.Columns.TryGetValue(columns[c].Name, out var statistics))
                {
                    computed[c] = statistics;
                }
            }
        }

        foreach (var (column, statistics) in plan.Overrides)
        {
            computed[column] = statistics;
        }

        return computed;
    }

    /// <summary>
    /// Equi-height buckets over an already-sorted sample, scaled back to the table's row count. Each
    /// bucket holds roughly the same number of rows, which is what makes range selectivity
    /// interpolable.
    /// </summary>
    private static HistogramBucket[] Buckets(
        List<object> sample,
        int buckets,
        int rowCount,
        SourceValueOrder.ISourceValueComparer comparer)
    {
        var wanted = Math.Min(buckets, sample.Count);
        var result = new List<HistogramBucket>(wanted);
        var scale = (double)rowCount / sample.Count;

        var start = 0;

        for (var b = 0; b < wanted && start < sample.Count; b++)
        {
            var end =
                (int)Math.Round(
                    (double)(b + 1) * sample.Count / wanted,
                    MidpointRounding.AwayFromZero);

            end =
                Math.Min(
                    Math.Max(end, start + 1),
                    sample.Count);

            while (end < sample.Count
                   && comparer.Compare(
                       sample[end - 1],
                       sample[end]) == 0)
            {
                end++;
            }

            var distinct = 1L;

            for (var i = start + 1; i < end; i++)
            {
                if (comparer.Compare(
                        sample[i - 1],
                        sample[i]) != 0)
                {
                    distinct++;
                }
            }

            result.Add(
                new HistogramBucket
                {
                    Upper = sample[end - 1],
                    Count =
                        (long)Math.Round(
                            (end - start) * scale,
                            MidpointRounding.AwayFromZero),
                    DistinctCount = distinct,
                });

            start = end;
        }

        return [.. result];
    }

    /// <summary>The most common values of an already-sorted sample, scaled to the table's row count.</summary>
    private static FrequentValue[] Frequent(
        List<object> sample,
        int rowCount,
        SourceValueOrder.ISourceValueComparer comparer)
    {
        var scale = (double)rowCount / sample.Count;

        // Number of runs is generally far smaller than number of sampled rows,
        // but allocate lazily rather than capacity == sample.Count.
        var best = new List<(object Value, int Count)>();

        var runStart = 0;

        for (var i = 1; i <= sample.Count; i++)
        {
            if (i != sample.Count
                && comparer.Compare(
                    sample[i - 1],
                    sample[i]) == 0)
            {
                continue;
            }

            best.Add(
                (sample[runStart], i - runStart));

            runStart = i;
        }

        best.Sort(
            (left, right) =>
            {
                var comparison =
                    right.Count.CompareTo(left.Count);

                return comparison != 0
                    ? comparison
                    : comparer.Compare(left.Value, right.Value);
            });

        var count =
            Math.Min(FrequentValueCount, best.Count);

        var result =
            new FrequentValue[count];

        for (var i = 0; i < count; i++)
        {
            var run = best[i];

            result[i] =
                new FrequentValue
                {
                    Value = run.Value,
                    Count =
                        (long)Math.Round(
                            run.Count * scale,
                            MidpointRounding.AwayFromZero),
                };
        }

        return result;
    }

    /// <summary>Equality that agrees with <see cref="SourceValueOrder"/>: BINARY compares by content.</summary>
    private sealed class PocoValueEquality : IEqualityComparer<object>
    {
        public static PocoValueEquality Instance { get; } = new();

        public new bool Equals(object? x, object? y)
        {
            if (ReferenceEquals(x, y))
            {
                return true;
            }

            return (x, y) switch
            {
                (ReadOnlyMemory<byte> a, ReadOnlyMemory<byte> b) =>
                    a.Span.SequenceEqual(b.Span),

                (byte[] a, byte[] b) =>
                    a.AsSpan().SequenceEqual(b),

                (ReadOnlyMemory<byte> a, byte[] b) =>
                    a.Span.SequenceEqual(b),

                (byte[] a, ReadOnlyMemory<byte> b) =>
                    a.AsSpan().SequenceEqual(b.Span),

                _ => object.Equals(x, y),
            };
        }

        public int GetHashCode(object obj) => obj switch
        {
            ReadOnlyMemory<byte> bytes => Hash(bytes.Span),
            byte[] bytes => Hash(bytes),
            _ => obj.GetHashCode(),
        };

        private static int Hash(ReadOnlySpan<byte> bytes)
        {
            var hash = new HashCode();
            hash.AddBytes(bytes);
            return hash.ToHashCode();
        }
    }
}

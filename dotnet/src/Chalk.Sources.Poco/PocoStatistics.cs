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
/// has changed — never on the scan path. Each column is read through its own typed reader, so a value
/// is boxed only when a statistic reports it (design 69 §5).
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

        var histogram =
            plan.Level == StatisticsLevel.Histogram;

        var target =
            Math.Min(rows.Count, MaxStatisticsSample);

        var stride =
            Math.Max(
                1,
                (rows.Count + target - 1) / target);

        // One pass per column, through the column's own reader. D302: a composite column carries no
        // statistics at all, so its records are never read here.
        var passes = new PocoStatisticsPass<T>?[columnCount];

        for (var c = 0; c < columnCount; c++)
        {
            if (IsComposite(columns[c]))
            {
                continue;
            }

            passes[c] =
                columns[c].Statistics.Begin(
                    countDistinct: counted[c] && exactDistinct[c] < 0,
                    sample: histogram,
                    sampleCapacity: target);
        }

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
                passes[c]?.Add(item);
            }

            row++;
        }

        var scale =
            sampledRows == 0
                ? 0d
                : (double)rows.Count / sampledRows;

        for (var c = 0; c < columnCount; c++)
        {
            if (passes[c] is not { } pass)
            {
                result[c] = ColumnStatistics.Unknown;
                continue;
            }

            var estimatedNulls =
                Math.Min(
                    rows.Count,
                    (long)Math.Round(
                        pass.Nulls * scale,
                        MidpointRounding.AwayFromZero));

            long distinctCount;

            if (exactDistinct[c] >= 0)
            {
                distinctCount = exactDistinct[c];
            }
            else if (pass.Distinct is var seen and >= 0)
            {
                if (sampledRows == rows.Count)
                {
                    distinctCount = seen;
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
                            (long)Math.Ceiling(seen * scale));
                }
            }
            else
            {
                distinctCount = -1;
            }

            var (buckets, frequent) =
                histogram
                    ? pass.Histogram(plan.HistogramBuckets, rows.Count)
                    : ([], []);

            result[c] = new ColumnStatistics
            {
                Level = plan.Level,
                DistinctCount = distinctCount,
                NullCount = estimatedNulls,
                Min = pass.Min,
                Max = pass.Max,
                Histogram = buckets,
                FrequentValues = frequent,
            };

            pass.End();
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
                    ChalkErrorCodes.IncompleteDeclaration,
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
    internal static HistogramBucket[] Buckets<TValue>(
        List<TValue> sample,
        int buckets,
        int rowCount,
        IComparer<TValue> comparer)
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
    internal static FrequentValue[] Frequent<TValue>(
        List<TValue> sample,
        int rowCount,
        IComparer<TValue> comparer)
    {
        var scale = (double)rowCount / sample.Count;

        // Number of runs is generally far smaller than number of sampled rows,
        // but allocate lazily rather than capacity == sample.Count.
        var best = new List<(TValue Value, int Count)>();

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
}

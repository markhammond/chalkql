using Chalk.Catalog;

namespace Chalk.Sources.Poco;

/// <summary>
/// How one column's statistics are gathered, compiled once per column (design 69 §5). A pass reads
/// each row's value through the column's own typed reader, so no value is boxed to be counted,
/// compared or sampled: only the few values a statistic reports are.
/// </summary>
/// <remarks>
/// A column keeps one spare pass, its distinct set and sample emptied but not shrunk, as it keeps a
/// spare chunk writer (ADR 0020 §1): the next refresh's statistics reuse its storage. A pass taken
/// while the spare is out builds its own.
/// </remarks>
internal abstract class PocoStatisticsColumn<T>
{
    /// <summary>Starts a pass over the column's values.</summary>
    /// <param name="countDistinct">Whether the pass counts distinct values, which costs a set.</param>
    /// <param name="sample">Whether it keeps every value it sees, for a histogram.</param>
    /// <param name="sampleCapacity">How many values a sample will hold at most.</param>
    public abstract PocoStatisticsPass<T> Begin(bool countDistinct, bool sample, int sampleCapacity);

    /// <summary>
    /// Orders two rows by this column in <paramref name="direction"/>, NULLs placed as it says, so a
    /// positive result means the two are out of order: what a declared collation is checked with
    /// (D17), without boxing either value.
    /// </summary>
    public abstract int CompareRows(T left, T right, Chalk.Ir.SortDirection direction);

    /// <summary>Where a NULL against a value goes: -1 when the NULL comes first.</summary>
    protected static int NullOrder(bool leftIsNull, Chalk.Ir.SortDirection direction) =>
        leftIsNull == (direction is Chalk.Ir.SortDirection.AscNullsFirst or Chalk.Ir.SortDirection.DescNullsFirst)
            ? -1
            : 1;

    protected static bool Descending(Chalk.Ir.SortDirection direction) =>
        direction is Chalk.Ir.SortDirection.DescNullsFirst or Chalk.Ir.SortDirection.DescNullsLast;
}

/// <summary>One pass of statistics over one column.</summary>
internal abstract class PocoStatisticsPass<T>
{
    /// <summary>The rows whose value was NULL.</summary>
    public int Nulls { get; protected set; }

    public abstract void Add(T row);

    /// <summary>The smallest value seen, as the catalog carries it, or null when every value was NULL.</summary>
    public abstract object? Min { get; }

    /// <summary>The largest value seen, as the catalog carries it, or null when every value was NULL.</summary>
    public abstract object? Max { get; }

    /// <summary>The distinct non-NULL values seen, or -1 when the pass was not asked to count them.</summary>
    public abstract int Distinct { get; }

    /// <summary>Equi-height buckets and most common values over the sample, scaled to the table.</summary>
    public abstract (HistogramBucket[] Buckets, FrequentValue[] Frequent) Histogram(int buckets, int rowCount);

    /// <summary>Hands the pass back to its column, emptied, for the next pass to reuse.</summary>
    public abstract void End();
}

/// <summary>A column read in its storage type <typeparamref name="TValue"/>.</summary>
internal sealed class PocoStatisticsColumn<T, TValue> : PocoStatisticsColumn<T>
{
    /// <summary>A distinct set or sample grown past this many values is not kept between passes.</summary>
    private const int MaxKept = 1 << 16;

    private readonly PocoValueReader<T, TValue> _read;
    private readonly IComparer<TValue> _order;
    private readonly IEqualityComparer<TValue> _equality;
    private Pass? _spare;

    public PocoStatisticsColumn(PocoValueBinding binding)
    {
        _read = PocoChunkCompiler.CompileReader<T, TValue>(binding);
        _order = PocoStatisticsOrder.Order<TValue>();
        _equality = PocoStatisticsOrder.Equality<TValue>();
    }

    /// <summary>The column's typed reader, which an index's keys are read with too.</summary>
    public PocoValueReader<T, TValue> Read => _read;

    public override int CompareRows(T left, T right, Chalk.Ir.SortDirection direction)
    {
        var hasLeft = _read(left, out var leftValue);
        var hasRight = _read(right, out var rightValue);
        if (!hasLeft || !hasRight)
        {
            return hasLeft == hasRight ? 0 : NullOrder(!hasLeft, direction);
        }

        var comparison = _order.Compare(leftValue, rightValue);
        return Descending(direction) ? -comparison : comparison;
    }

    public override PocoStatisticsPass<T> Begin(bool countDistinct, bool sample, int sampleCapacity)
    {
        var pass = Interlocked.Exchange(ref _spare, null) ?? new Pass(this);
        pass.Start(countDistinct, sample, sampleCapacity);
        return pass;
    }

    private sealed class Pass(PocoStatisticsColumn<T, TValue> column) : PocoStatisticsPass<T>
    {
        private readonly HashSet<TValue> _distinct = new(column._equality);
        private readonly List<TValue> _sample = [];
        private bool _counting;
        private bool _sampling;
        private bool _any;
        private TValue _min = default!;
        private TValue _max = default!;

        public void Start(bool countDistinct, bool sample, int sampleCapacity)
        {
            Nulls = 0;
            _any = false;
            _counting = countDistinct;
            _sampling = sample;
            if (sample)
            {
                _sample.EnsureCapacity(sampleCapacity);
            }
        }

        public override void Add(T row)
        {
            if (!column._read(row, out var value))
            {
                Nulls++;
                return;
            }

            if (!_any)
            {
                _min = value;
                _max = value;
                _any = true;
            }
            else
            {
                if (column._order.Compare(value, _min) < 0)
                {
                    _min = value;
                }

                if (column._order.Compare(value, _max) > 0)
                {
                    _max = value;
                }
            }

            if (_counting)
            {
                _distinct.Add(value);
            }

            if (_sampling)
            {
                _sample.Add(value);
            }
        }

        public override object? Min => _any ? (object?)_min : null;

        public override object? Max => _any ? (object?)_max : null;

        public override int Distinct => _counting ? _distinct.Count : -1;

        public override (HistogramBucket[] Buckets, FrequentValue[] Frequent) Histogram(int buckets, int rowCount)
        {
            if (_sample.Count == 0)
            {
                return ([], []);
            }

            _sample.Sort(column._order);
            return (
                PocoStatistics.Buckets(_sample, buckets, rowCount, column._order),
                PocoStatistics.Frequent(_sample, rowCount, column._order));
        }

        public override void End()
        {
            // Nothing a pass saw outlives it: a kept value could pin a row's string or bytes.
            var oversized = _distinct.Count > MaxKept || _sample.Count > MaxKept;
            _distinct.Clear();
            _sample.Clear();
            _min = default!;
            _max = default!;
            if (!oversized)
            {
                Volatile.Write(ref column._spare, this);
            }
        }
    }
}

/// <summary>
/// A column with no typed reader, read boxed: what statistics did for every column before design 69,
/// and what a column whose values have no order (a list) still needs, to count its NULLs.
/// </summary>
internal sealed class PocoBoxedStatisticsColumn<T>(Func<T, object?> read, bool ordered) : PocoStatisticsColumn<T>
{
    public override PocoStatisticsPass<T> Begin(bool countDistinct, bool sample, int sampleCapacity) =>
        new Pass(read, ordered, countDistinct, sample);

    public override int CompareRows(T left, T right, Chalk.Ir.SortDirection direction) =>
        PocoVerification.Compare(read(left), read(right), direction);

    private sealed class Pass(Func<T, object?> read, bool ordered, bool counting, bool sampling) : PocoStatisticsPass<T>
    {
        private readonly HashSet<object>? _distinct = ordered && counting ? new(PocoStatisticsOrder.BoxedEquality.Instance) : null;
        private readonly List<object>? _sample = ordered && sampling ? [] : null;
        private SourceValueOrder.ISourceValueComparer? _comparer;
        private object? _min;
        private object? _max;

        public override void Add(T row)
        {
            var value = read(row);
            if (value is null)
            {
                Nulls++;
                return;
            }

            if (!ordered)
            {
                return;
            }

            if (_comparer is null)
            {
                // Statistics want ascending order; where NULLs go is immaterial, as none reaches here.
                _comparer = SourceValueOrder.CreateComparer(value, Chalk.Ir.SortDirection.AscNullsFirst);
                _min = value;
                _max = value;
            }
            else
            {
                if (_comparer.Compare(value, _min) < 0)
                {
                    _min = value;
                }

                if (_comparer.Compare(value, _max) > 0)
                {
                    _max = value;
                }
            }

            _distinct?.Add(value);
            _sample?.Add(value);
        }

        public override object? Min => _min;

        public override object? Max => _max;

        public override int Distinct => _distinct?.Count ?? -1;

        public override (HistogramBucket[] Buckets, FrequentValue[] Frequent) Histogram(int buckets, int rowCount)
        {
            if (_sample is not { Count: > 0 } sample || _comparer is not { } comparer)
            {
                return ([], []);
            }

            var order = Comparer<object>.Create(comparer.Compare);
            sample.Sort(order);
            return (
                PocoStatistics.Buckets(sample, buckets, rowCount, order),
                PocoStatistics.Frequent(sample, rowCount, order));
        }

        public override void End()
        {
        }
    }
}

/// <summary>
/// Statistics' order and equality for each storage type, as <see cref="SourceValueOrder"/> orders the
/// same values boxed: text by code point, binary by content, NaN after every number, and every other
/// type its own order. Min, max and the histogram must come out as they did when the values were
/// boxed.
/// </summary>
internal static class PocoStatisticsOrder
{
    public static IComparer<TValue> Order<TValue>() =>
        typeof(TValue) == typeof(double) ? (IComparer<TValue>)(object)Real.Instance
        : typeof(TValue) == typeof(float) ? (IComparer<TValue>)(object)RealSingle.Instance
        : typeof(TValue) == typeof(string) ? (IComparer<TValue>)(object)TextOrder.Comparer
        : typeof(TValue) == typeof(ReadOnlyMemory<byte>) ? (IComparer<TValue>)(object)Bytes.Instance
        : typeof(TValue) == typeof(Utf8String) ? (IComparer<TValue>)(object)Utf8.Instance
        : Comparer<TValue>.Default;

    public static IEqualityComparer<TValue> Equality<TValue>() =>
        typeof(TValue) == typeof(string) ? (IEqualityComparer<TValue>)(object)StringComparer.Ordinal
        : typeof(TValue) == typeof(ReadOnlyMemory<byte>) ? (IEqualityComparer<TValue>)(object)Bytes.Instance
        : EqualityComparer<TValue>.Default;

    private sealed class Real : IComparer<double>
    {
        public static Real Instance { get; } = new();

        public int Compare(double left, double right) =>
            double.IsNaN(left) ? (double.IsNaN(right) ? 0 : 1)
            : double.IsNaN(right) ? -1
            : left < right ? -1 : left > right ? 1 : 0;
    }

    private sealed class RealSingle : IComparer<float>
    {
        public static RealSingle Instance { get; } = new();

        public int Compare(float left, float right) =>
            float.IsNaN(left) ? (float.IsNaN(right) ? 0 : 1)
            : float.IsNaN(right) ? -1
            : left < right ? -1 : left > right ? 1 : 0;
    }

    private sealed class Utf8 : IComparer<Utf8String>
    {
        public static Utf8 Instance { get; } = new();

        public int Compare(Utf8String left, Utf8String right) => left.CompareTo(right);
    }

    private sealed class Bytes : IComparer<ReadOnlyMemory<byte>>, IEqualityComparer<ReadOnlyMemory<byte>>
    {
        public static Bytes Instance { get; } = new();

        public int Compare(ReadOnlyMemory<byte> left, ReadOnlyMemory<byte> right) =>
            left.Span.SequenceCompareTo(right.Span);

        public bool Equals(ReadOnlyMemory<byte> left, ReadOnlyMemory<byte> right) =>
            left.Span.SequenceEqual(right.Span);

        public int GetHashCode(ReadOnlyMemory<byte> value)
        {
            var hash = new HashCode();
            hash.AddBytes(value.Span);
            return hash.ToHashCode();
        }
    }

    /// <summary>Equality that agrees with <see cref="SourceValueOrder"/>: BINARY compares by content.</summary>
    internal sealed class BoxedEquality : IEqualityComparer<object>
    {
        public static BoxedEquality Instance { get; } = new();

        public new bool Equals(object? x, object? y)
        {
            if (ReferenceEquals(x, y))
            {
                return true;
            }

            return (x, y) switch
            {
                (ReadOnlyMemory<byte> a, ReadOnlyMemory<byte> b) => a.Span.SequenceEqual(b.Span),
                (byte[] a, byte[] b) => a.AsSpan().SequenceEqual(b),
                (ReadOnlyMemory<byte> a, byte[] b) => a.Span.SequenceEqual(b),
                (byte[] a, ReadOnlyMemory<byte> b) => a.AsSpan().SequenceEqual(b.Span),
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

using System.Runtime.CompilerServices;
using Chalk.Catalog;
using Chalk.Sources.Keys;
using System.Numerics;
using System.Runtime.InteropServices;
using SortDirection = Chalk.Ir.SortDirection;

namespace Chalk.Sources.Poco;

/// <summary>
/// Ordered index implemented as a permutation of row positions.
///
/// Construction materialises each key into a strongly-typed temporary column.
/// The typed columns are used by sorting, prefix-cardinality calculation and
/// uniqueness verification, then discarded. Consequently the persistent index
/// still costs one int per row, or nothing when backed by a declared collation.
/// </summary>
public sealed class PermutationIndex<T> : IPositionalPocoIndex<T>
{
    private readonly IReadOnlyList<T> _rows;
    private readonly ISearchKey[] _searchKeys;
    private readonly SortDirection[] _directions;

    /// <summary>
    /// Row positions in key order, or null when the collection is already in that order.
    /// </summary>
    private readonly int[]? _permutation;

    /// <summary>Exact distinct count for each key prefix.</summary>
    private readonly long[] _prefixDistinct;

    private readonly int _rowCount;

    internal PermutationIndex(IndexDescriptor descriptor,
        IReadOnlyList<T> rows,
        Func<T, object?>[] keys,
        bool collationBacked,
        string table,
        IReadOnlyList<string> keyColumnNames,
        PermutationIndexScratch scratch)
        : this(descriptor, rows, keys, collationBacked, table, keyColumnNames, scratch,
            previous: null, appendedFrom: 0)
    {
    }

    /// <summary>
    /// The same, extending <paramref name="previous"/> rather than sorting from scratch (D260 §3):
    /// <paramref name="rows"/> is what that index was built over followed by
    /// <c>rows.Count - appendedFrom</c> new rows, whose positions are therefore the ones from
    /// <paramref name="appendedFrom"/> on.
    /// </summary>
    internal PermutationIndex(IndexDescriptor descriptor,
        IReadOnlyList<T> rows,
        Func<T, object?>[] keys,
        bool collationBacked,
        string table,
        IReadOnlyList<string> keyColumnNames,
        PermutationIndexScratch scratch,
        PermutationIndex<T>? previous,
        int appendedFrom)
    {
        Descriptor = descriptor;
        _rows = rows;
        _rowCount = rows.Count;

        var keyCount = keys.Length;

        _directions = new SortDirection[keyCount];
        _searchKeys = new ISearchKey[keyCount];

        var materialised = new IMaterialisedKey[keyCount];

        for (var k = 0; k < keyCount; k++)
        {
            var direction = descriptor.DirectionAt(k);

            _directions[k] = direction;

            materialised[k] =
                MaterialiseKey(
                    rows,
                    keys[k],
                    direction,
                    k,
                    scratch,
                    out _searchKeys[k]);
        }

        if (collationBacked)
        {
            // The collection is already in key order, so appending rows that keep it appends to the
            // index too — which is what the D17 verification has just been asked to confirm.
            _permutation = null;
            ExtendedInOrder = true;
        }
        else if (previous?._permutation is { } existing
            && appendedFrom > 0
            && appendedFrom == previous._rowCount
            && appendedFrom <= _rowCount)
        {
            _permutation = MergeAppended(
                existing, appendedFrom, _rowCount, materialised, out var inOrder);
            ExtendedInOrder = inOrder;
        }
        else
        {
            var permutation =
                GC.AllocateUninitializedArray<int>(_rowCount);

            for (var i = 0; i < permutation.Length; i++)
                permutation[i] = i;

            Sort(permutation, materialised, scratch);
            _permutation = permutation;
            ExtendedInOrder = previous is null && appendedFrom == 0;
        }

        _prefixDistinct =
            CountPrefixDistinct(materialised);

        if (descriptor.Unique)
        {
            VerifyUnique(
                materialised,
                keys,
                table,
                keyColumnNames);
        }
    }

    /// <summary>
    /// The permutation of an appended index, from the one it extends (D260 §3). Keys that arrived in
    /// order — a time-series table appended in timestamp order — are already where they belong, so
    /// the two runs are copied end to end and nothing is sorted; otherwise the appended tail alone
    /// is sorted and the two runs are merged.
    /// </summary>
    private static int[] MergeAppended(
        int[] existing,
        int from,
        int total,
        IMaterialisedKey[] keys,
        out bool inOrder)
    {
        var comparer = new CompositeComparer(keys);
        var added = total - from;

        var tail = GC.AllocateUninitializedArray<int>(added);
        for (var i = 0; i < added; i++)
        {
            tail[i] = from + i;
        }

        inOrder = true;
        for (var i = 1; i < added && inOrder; i++)
        {
            inOrder = comparer.Compare(tail[i - 1], tail[i]) <= 0;
        }

        if (inOrder && added > 0 && existing.Length > 0)
        {
            inOrder = comparer.Compare(existing[^1], tail[0]) <= 0;
        }

        var merged = GC.AllocateUninitializedArray<int>(total);

        if (inOrder)
        {
            existing.AsSpan().CopyTo(merged);
            tail.AsSpan().CopyTo(merged.AsSpan(existing.Length));
            return merged;
        }

        tail.AsSpan().Sort(comparer);

        var left = 0;
        var right = 0;
        var at = 0;

        while (left < existing.Length && right < added)
        {
            merged[at++] = comparer.Compare(existing[left], tail[right]) <= 0
                ? existing[left++]
                : tail[right++];
        }

        while (left < existing.Length)
        {
            merged[at++] = existing[left++];
        }

        while (right < added)
        {
            merged[at++] = tail[right++];
        }

        return merged;
    }

    private static void Sort(
        int[] permutation,
        IMaterialisedKey[] keys,
        PermutationIndexScratch scratch)
    {
        switch (keys.Length)
        {
            case 0:
                return;

            case 1:
                keys[0].SortSingle(
                    permutation,
                    0,
                    permutation.Length);
                return;

            case 2:
            {
                var first = keys[0];
                var second = keys[1];

                if (ReferenceEquals(
                        first,
                        AllNullKey.Instance))
                {
                    second.SortSingle(
                        permutation,
                        0,
                        permutation.Length);
                    return;
                }

                if (ReferenceEquals(
                        second,
                        AllNullKey.Instance))
                {
                    first.SortSingle(
                        permutation,
                        0,
                        permutation.Length);
                    return;
                }

                if (first is ISymbolKey symbols)
                {
                    SortByLeadingSymbols(
                        permutation,
                        symbols,
                        second,
                        scratch);
                    return;
                }

                first.SortPair(
                    permutation,
                    second);
                return;
            }

            default:
                permutation.AsSpan().Sort(
                    new CompositeComparer(keys));
                return;
        }
    }

    private static void SortByLeadingSymbols(
        int[] permutation,
        ISymbolKey symbols,
        IMaterialisedKey second,
        PermutationIndexScratch scratch)
    {
        var bucketCount =
            symbols.BucketCount;

        var metaLength =
            (bucketCount * 3) + 1;

        if (bucketCount <= 256)
        {
            Span<int> meta =
                stackalloc int[metaLength];

            SortByLeadingSymbolsCore(
                permutation,
                symbols,
                second,
                scratch,
                meta);

            return;
        }

        SortByLeadingSymbolsCore(
            permutation,
            symbols,
            second,
            scratch,
            scratch.GetBucketMeta(
                metaLength));
    }
    
    private static void SortByLeadingSymbolsCore(
        int[] permutation,
        ISymbolKey symbols,
        IMaterialisedKey second,
        PermutationIndexScratch scratch,
        Span<int> meta)
    {
        var bucketCount =
            symbols.BucketCount;

        var counts =
            meta.Slice(
                0,
                bucketCount);

        var offsets =
            meta.Slice(
                bucketCount,
                bucketCount + 1);

        var next =
            meta.Slice(
                (bucketCount * 2) + 1,
                bucketCount);

        counts.Clear();

        for (var i = 0;
             i < permutation.Length;
             i++)
        {
            var row =
                permutation[i];

            counts[
                symbols.CodeAt(row)]++;
        }

        offsets[0] = 0;

        for (var bucket = 0;
             bucket < bucketCount;
             bucket++)
        {
            offsets[bucket + 1] =
                offsets[bucket] +
                counts[bucket];
        }

        offsets
            .Slice(0, bucketCount)
            .CopyTo(next);

        var rows =
            scratch.GetBucketRows(
                permutation.Length);

        for (var i = 0;
             i < permutation.Length;
             i++)
        {
            var row =
                permutation[i];

            rows[
                    next[symbols.CodeAt(row)]++] =
                row;
        }

        rows.CopyTo(permutation);

        for (var bucket = 0;
             bucket < bucketCount;
             bucket++)
        {
            var offset =
                offsets[bucket];

            var length =
                offsets[bucket + 1] -
                offset;

            if (length > 1)
            {
                second.SortSingle(
                    permutation,
                    offset,
                    length);
            }
        }
    }

    public IndexDescriptor Descriptor { get; }

    public bool IsCollationBacked =>
        _permutation is null;

    public long BytesPerRow =>
        _permutation is null
            ? 0
            : sizeof(int);

    public long? DistinctCount(int keyPositions) =>
        keyPositions >= 1 &&
        keyPositions <= _prefixDistinct.Length
            ? _prefixDistinct[keyPositions - 1]
            : null;

    public IEnumerable<T> Lookup(IndexKeyRange range)
    {
        GetWindow(
            range,
            out var from,
            out var to);

        for (var ordinal = from;
             ordinal < to;
             ordinal++)
        {
            yield return _rows[
                GetPosition(ordinal)];
        }
    }

    public IEnumerable<int> LookupPositions(
        IndexKeyRange range)
    {
        GetWindow(
            range,
            out var from,
            out var to);

        for (var ordinal = from;
             ordinal < to;
             ordinal++)
        {
            yield return GetPosition(ordinal);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetPosition(int ordinal)
    {
        var permutation = _permutation;

        return permutation is null
            ? ordinal
            : permutation[ordinal];
    }

    internal bool Describes(
        IReadOnlyList<T> rows) =>
        ReferenceEquals(rows, _rows) &&
        rows.Count == _rowCount;

    /// <summary>
    /// Row positions in key order, or null when the collection is already in that order. Read by
    /// <see cref="ClusteredIndex{T}"/>, which extracts its copy through exactly this permutation
    /// (D257); nobody may write to it.
    /// </summary>
    internal int[]? Permutation =>
        _permutation;

    /// <summary>Rows the index was built over.</summary>
    internal int RowCount =>
        _rowCount;

    /// <summary>
    /// Whether this index's order is the order of the index it extends, followed by the appended
    /// rows in the order they were appended (D260 §3). True when the appended keys arrived in key
    /// order, which is when a clustered copy can be extended rather than extracted again.
    /// </summary>
    internal bool ExtendedInOrder { get; }

    public void GetWindow(
        IndexKeyRange range,
        out int from,
        out int to)
    {
        ArgumentNullException.ThrowIfNull(range);

        var bounded = range.BoundedColumns;

        if ((uint)bounded >
            (uint)_searchKeys.Length)
        {
            throw new ArgumentException(
                $"the range bounds {bounded} key column(s) but index "
                + $"'{Descriptor.Name}' has {_searchKeys.Length}",
                nameof(range));
        }

        if (bounded == 0)
        {
            from = 0;
            to = _rowCount;
            return;
        }

        var lower =
            new BoundView(range.Lower);

        var upper =
            new BoundView(range.Upper);

        var lowerInclusive =
            range.LowerInclusive;

        var upperInclusive =
            range.UpperInclusive;

        var direction =
            _directions[bounded - 1];

        var nullsLast =
            direction is
                SortDirection.AscNullsLast or
                SortDirection.DescNullsLast;

        if (lower.Count < bounded)
        {
            lower =
                Prefix(
                    range,
                    bounded - 1);

            if (nullsLast)
            {
                lowerInclusive = true;
            }
            else
            {
                lower =
                    lower.AppendNull();

                lowerInclusive = false;
            }
        }

        if (upper.Count < bounded)
        {
            upper =
                Prefix(
                    range,
                    bounded - 1);

            if (nullsLast)
            {
                upper =
                    upper.AppendNull();

                upperInclusive = false;
            }
            else
            {
                upperInclusive = true;
            }
        }

        from =
            Search(
                in lower,
                lowerInclusive ? 0 : 1);

        to =
            Search(
                in upper,
                upperInclusive ? 1 : 0);

        if (from >= to)
        {
            from = 0;
            to = 0;
        }
    }

    private static BoundView Prefix(
        IndexKeyRange range,
        int count)
    {
        if (count == 0)
            return default;

        IReadOnlyList<object?> source =
            range.Lower.Count >= count
                ? range.Lower
                : range.Upper;

        return new BoundView(
            source,
            count,
            appendNull: false);
    }

    private int Search(
        in BoundView bound,
        int atLeast)
    {
        if (bound.Count == 0)
        {
            return atLeast == 0
                ? 0
                : _rowCount;
        }

        var low = 0;
        var high = _rowCount;

        while (low < high)
        {
            var mid =
                low + ((high - low) >> 1);

            if (CompareAt(
                    mid,
                    in bound) < atLeast)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    private int CompareAt(
        int ordinal,
        in BoundView bound)
    {
        var row =
            _rows[GetPosition(ordinal)];

        var count = bound.Count;

        for (var k = 0; k < count; k++)
        {
            var comparison =
                _searchKeys[k].Compare(
                    row,
                    bound[k]);

            if (comparison != 0)
                return comparison;
        }

        return 0;
    }

    private long[] CountPrefixDistinct(
        IMaterialisedKey[] keys)
    {
        var distinct =
            new long[keys.Length];

        if (_rowCount == 0)
            return distinct;

        Array.Fill(
            distinct,
            1L);

        var permutation = _permutation;

        for (var ordinal = 1;
             ordinal < _rowCount;
             ordinal++)
        {
            var previous =
                permutation is null
                    ? ordinal - 1
                    : permutation[ordinal - 1];

            var current =
                permutation is null
                    ? ordinal
                    : permutation[ordinal];

            for (var k = 0;
                 k < keys.Length;
                 k++)
            {
                if (keys[k].EqualRows(
                        previous,
                        current))
                {
                    continue;
                }

                for (var longer = k;
                     longer < distinct.Length;
                     longer++)
                {
                    distinct[longer]++;
                }

                break;
            }
        }

        return distinct;
    }

    private void VerifyUnique(
        IMaterialisedKey[] keys,
        Func<T, object?>[] accessors,
        string table,
        IReadOnlyList<string> keyColumnNames)
    {
        var declaration =
            "unique index '" +
            Descriptor.Name +
            "' (" +
            string.Join(", ", keyColumnNames) +
            ")";

        var permutation = _permutation;

        for (var ordinal = 1;
             ordinal < _rowCount;
             ordinal++)
        {
            var previous =
                permutation is null
                    ? ordinal - 1
                    : permutation[ordinal - 1];

            var current =
                permutation is null
                    ? ordinal
                    : permutation[ordinal];

            var equal = true;

            for (var k = 0; k < keys.Length; k++)
            {
                if (!keys[k].EqualRows(previous, current))
                {
                    equal = false;
                    break;
                }
            }

            if (!equal)
                continue;

            var rendered = new string[keys.Length];
            var row = _rows[current];

            for (var k = 0; k < keys.Length; k++)
                rendered[k] = Render(accessors[k](row));

            var first = Math.Min(previous, current);
            var second = Math.Max(previous, current);

            throw new CatalogVerificationException(
                table,
                declaration,
                second,
                $"rows {first} and {second} share the key "
                + $"({string.Join(", ", rendered)})");
        }
    }

    // ------------------------------------------------------------------
    // Materialisation
    // ------------------------------------------------------------------

    private static IMaterialisedKey MaterialiseKey(
        IReadOnlyList<T> rows,
        Func<T, object?> accessor,
        SortDirection direction,
        int keyOrdinal,
        PermutationIndexScratch scratch,
        out ISearchKey searchKey)
    {
        var firstNonNull = -1;
        object? exemplar = null;

        for (var i = 0;
             i < rows.Count;
             i++)
        {
            exemplar =
                accessor(rows[i]);

            if (exemplar is not null)
            {
                firstNonNull = i;
                break;
            }
        }

        if (firstNonNull < 0)
        {
            searchKey =
                new AllNullSearchKey(
                    accessor,
                    direction);

            return AllNullKey.Instance;
        }

        return exemplar switch
        {
            sbyte value =>
                MaterialiseEncoded<
                    sbyte,
                    byte,
                    SByteEncoding,
                    ComparableOrder<sbyte>>(
                    rows, accessor, direction,
                    firstNonNull, value,
                    keyOrdinal, scratch,
                    out searchKey),

            byte value =>
                MaterialiseEncoded<
                    byte,
                    byte,
                    ByteEncoding,
                    ComparableOrder<byte>>(
                    rows, accessor, direction,
                    firstNonNull, value,
                    keyOrdinal, scratch,
                    out searchKey),

            short value =>
                MaterialiseEncoded<
                    short,
                    ushort,
                    Int16Encoding,
                    ComparableOrder<short>>(
                    rows, accessor, direction,
                    firstNonNull, value,
                    keyOrdinal, scratch,
                    out searchKey),

            ushort value =>
                MaterialiseEncoded<
                    ushort,
                    ushort,
                    UInt16Encoding,
                    ComparableOrder<ushort>>(
                    rows, accessor, direction,
                    firstNonNull, value,
                    keyOrdinal, scratch,
                    out searchKey),

            int value =>
                MaterialiseEncoded<
                    int,
                    uint,
                    Int32Encoding,
                    ComparableOrder<int>>(
                    rows, accessor, direction,
                    firstNonNull, value,
                    keyOrdinal, scratch,
                    out searchKey),

            uint value =>
                MaterialiseEncoded<
                    uint,
                    uint,
                    UInt32Encoding,
                    ComparableOrder<uint>>(
                    rows, accessor, direction,
                    firstNonNull, value,
                    keyOrdinal, scratch,
                    out searchKey),

            long value =>
                MaterialiseEncoded<
                    long,
                    ulong,
                    Int64Encoding,
                    ComparableOrder<long>>(
                    rows, accessor, direction,
                    firstNonNull, value,
                    keyOrdinal, scratch,
                    out searchKey),

            ulong value =>
                MaterialiseEncoded<
                    ulong,
                    ulong,
                    UInt64Encoding,
                    ComparableOrder<ulong>>(
                    rows, accessor, direction,
                    firstNonNull, value,
                    keyOrdinal, scratch,
                    out searchKey),

            float value =>
                MaterialiseEncoded<
                    float,
                    uint,
                    SingleEncoding,
                    FloatOrder>(
                    rows, accessor, direction,
                    firstNonNull, value,
                    keyOrdinal, scratch,
                    out searchKey),

            double value =>
                MaterialiseEncoded<
                    double,
                    ulong,
                    DoubleEncoding,
                    DoubleOrder>(
                    rows, accessor, direction,
                    firstNonNull, value,
                    keyOrdinal, scratch,
                    out searchKey),

            DateTime value =>
                MaterialiseEncoded<
                    DateTime,
                    ulong,
                    DateTimeEncoding,
                    ComparableOrder<DateTime>>(
                    rows, accessor, direction,
                    firstNonNull, value,
                    keyOrdinal, scratch,
                    out searchKey),

            DateTimeOffset value =>
                MaterialiseEncoded<
                    DateTimeOffset,
                    ulong,
                    DateTimeOffsetEncoding,
                    ComparableOrder<DateTimeOffset>>(
                    rows, accessor, direction,
                    firstNonNull, value,
                    keyOrdinal, scratch,
                    out searchKey),

            string value =>
                MaterialiseSymbols<
                    string,
                    StringOrder>(
                    rows,
                    accessor,
                    direction,
                    firstNonNull,
                    value,
                    keyOrdinal, scratch,
                    out searchKey),

            Utf8String value =>
                MaterialiseSymbols<
                    Utf8String,
                    Utf8Order>(
                    rows,
                    accessor,
                    direction,
                    firstNonNull,
                    value,
                    keyOrdinal, scratch,
                    out searchKey),

            // Leave these as logical representations for this iteration.
            decimal value =>
                Materialise<decimal, ComparableOrder<decimal>>(
                    rows, accessor, direction,
                    firstNonNull, value, out searchKey),

            Guid value =>
                Materialise<Guid, ComparableOrder<Guid>>(
                    rows, accessor, direction,
                    firstNonNull, value, out searchKey),

            byte[] value =>
                Materialise<byte[], ByteArrayOrder>(
                    rows, accessor, direction,
                    firstNonNull, value, out searchKey),

            ReadOnlyMemory<byte> value =>
                Materialise<
                    ReadOnlyMemory<byte>,
                    MemoryByteOrder>(
                    rows, accessor, direction,
                    firstNonNull, value, out searchKey),

            _ =>
                MaterialiseFallback(
                    rows,
                    accessor,
                    direction,
                    firstNonNull,
                    exemplar,
                    out searchKey),
        };
    }

    private static IMaterialisedKey MaterialiseEncoded<
        TValue,
        TCode,
        TEncoding,
        TLogicalOrder>(
        IReadOnlyList<T> rows,
        Func<T, object?> accessor,
        SortDirection direction,
        int firstNonNull,
        TValue firstValue,
        int keyOrdinal,
        PermutationIndexScratch scratch,
        out ISearchKey searchKey)
        where TCode :
            unmanaged,
            IBinaryInteger<TCode>,
            IUnsignedNumber<TCode>
        where TEncoding :
            struct,
            ISortEncoding<TValue, TCode>
        where TLogicalOrder :
            struct,
            IValueOrder<TValue>
    {
        var count =
            rows.Count;

        var codes =
            scratch.GetCodes<TCode>(
                keyOrdinal,
                count);

        bool[]? nulls = null;

        if (firstNonNull != 0)
        {
            nulls =
                scratch.GetNulls(
                    keyOrdinal,
                    count);

            nulls
                .AsSpan(0, firstNonNull)
                .Fill(true);
        }

        var descending =
            IsDescending(direction);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static TCode Encode(
            TValue value,
            bool descending)
        {
            var code =
                TEncoding.EncodeAscending(value);

            return descending
                ? ~code
                : code;
        }

        codes[firstNonNull] =
            Encode(
                firstValue,
                descending);

        for (var i = firstNonNull + 1;
             i < count;
             i++)
        {
            var boxed =
                accessor(rows[i]);

            if (boxed is null)
            {
                if (nulls is null)
                {
                    nulls =
                        scratch.GetNulls(
                            keyOrdinal,
                            count);
                }

                nulls[i] = true;
                continue;
            }

            if (boxed is not TValue value)
            {
                throw new InvalidCastException(
                    $"index key produced both "
                    + $"{typeof(TValue).Name} and "
                    + $"{boxed.GetType().Name}");
            }

            codes[i] =
                Encode(
                    value,
                    descending);
        }

        searchKey =
            SearchKeyFactory<
                    TValue,
                    TLogicalOrder>
                .Create(
                    accessor,
                    direction);

        return CreateEncodedKey(
            codes,
            nulls,
            direction,
            scratch);
    }

    private interface IEncodedKey
    {
        bool TrySortEncodedPairSecond<TFirstCode>(
            int[] permutation,
            TFirstCode[] first)
            where TFirstCode :
            unmanaged,
            IBinaryInteger<TFirstCode>,
            IUnsignedNumber<TFirstCode>;
    }

    private readonly struct EncodedKey<TCode> :
        IMaterialisedKey,
        IRowKey<EncodedKey<TCode>>,
        IEncodedKey
        where TCode :
        unmanaged,
        IBinaryInteger<TCode>,
        IUnsignedNumber<TCode>
    {
        private readonly TCode[] _codes;
        private readonly PermutationIndexScratch _scratch;

        public EncodedKey(
            TCode[] codes,
            PermutationIndexScratch scratch)
        {
            _codes = codes;
            _scratch = scratch;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Compare(
            in EncodedKey<TCode> key,
            int left,
            int right)
        {
            var a = key._codes[left];
            var b = key._codes[right];

            return a < b
                ? -1
                : a > b
                    ? 1
                    : 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int CompareRows(
            int left,
            int right) =>
            Compare(
                in this,
                left,
                right);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool EqualRows(
            int left,
            int right) =>
            _codes[left] == _codes[right];

        public void SortSingle(
            int[] permutation,
            int offset,
            int length)
        {
            //
            // Packed single sorting currently assumes the entire
            // row set. Don't use it for a bucket slice.
            //
            if (offset == 0 &&
                length == permutation.Length &&
                TrySortPackedSingle(
                    _codes,
                    permutation,
                    _scratch))
            {
                return;
            }

            permutation
                .AsSpan(offset, length)
                .Sort(
                    new SingleComparer<
                        EncodedKey<TCode>>(this));
        }

        public void SortPair(
            int[] permutation,
            IMaterialisedKey second)
        {
            if (second is IEncodedKey encoded &&
                encoded.TrySortEncodedPairSecond(
                    permutation,
                    _codes))
            {
                return;
            }

            second.SortPairSecond(
                permutation,
                this);
        }

        public void SortPairSecond<TFirst>(
            int[] permutation,
            TFirst first)
            where TFirst :
            struct,
            IRowKey<TFirst>
        {
            permutation.AsSpan().Sort(
                new PairComparer<
                    TFirst,
                    EncodedKey<TCode>>(
                    first,
                    this));
        }

        public bool TrySortEncodedPairSecond<TFirstCode>(
            int[] permutation,
            TFirstCode[] first)
            where TFirstCode :
            unmanaged,
            IBinaryInteger<TFirstCode>,
            IUnsignedNumber<TFirstCode> =>
            TrySortPackedPair(
                first,
                _codes,
                permutation,
                _scratch);
    }

    private static bool TrySortPackedSingle<TCode>(
        TCode[] codes,
        int[] permutation,
        PermutationIndexScratch scratch)
        where TCode :
        unmanaged,
        IBinaryInteger<TCode>,
        IUnsignedNumber<TCode>
    {
        var codeBits =
            Unsafe.SizeOf<TCode>() * 8;

        if (codeBits <= 32)
        {
            SortPacked64(
                codes,
                permutation,
                scratch);

            return true;
        }

        if (codeBits <= 64)
        {
            SortPacked128(
                codes,
                permutation,
                scratch);

            return true;
        }

        return false;
    }
    
    private static void SortPacked64<TCode>(
        TCode[] codes,
        int[] permutation,
        PermutationIndexScratch scratch)
        where TCode :
        unmanaged,
        IBinaryInteger<TCode>,
        IUnsignedNumber<TCode>
    {
        var length = permutation.Length;

        var packed = scratch.GetPacked64(length);
        
        for (var row = 0;
             row < length;
             row++)
        {
            var code = ulong.CreateTruncating(
                codes[row]);

            packed[row] = (code << 32) | (uint)row;
        }

        packed.Sort();

        for (var i = 0; i < length; i++)
        {
            permutation[i] = unchecked((int)(uint)packed[i]);
        }
    }

    private static void SortPacked128<TCode>(
        TCode[] codes,
        int[] permutation,
        PermutationIndexScratch scratch)
        where TCode :
        unmanaged,
        IBinaryInteger<TCode>,
        IUnsignedNumber<TCode>
    {
        var length = permutation.Length;

        var packed = scratch.GetPacked128(length);
        
        for (var row = 0;
             row < length;
             row++)
        {
            var code = UInt128.CreateTruncating(codes[row]);

            packed[row] = (code << 32) | (uint)row;
        }

        packed.Sort();

        for (var i = 0; i < length; i++)
        {
            permutation[i] = (int)(uint)packed[i];
        }
    }

    private static bool TrySortPackedPair<
        TFirst,
        TSecond>(
        TFirst[] first,
        TSecond[] second,
        int[] permutation,
        PermutationIndexScratch scratch)
        where TFirst :
        unmanaged,
        IBinaryInteger<TFirst>,
        IUnsignedNumber<TFirst>
        where TSecond :
        unmanaged,
        IBinaryInteger<TSecond>,
        IUnsignedNumber<TSecond>
    {
        var firstBits =
            Unsafe.SizeOf<TFirst>() * 8;

        var secondBits =
            Unsafe.SizeOf<TSecond>() * 8;

        var keyBits =
            firstBits + secondBits;

        if (keyBits <= 32)
        {
            SortPackedPair64(
                first,
                second,
                permutation,
                secondBits,
                scratch);

            return true;
        }

        if (keyBits <= 96)
        {
            SortPackedPair128(
                first,
                second,
                permutation,
                secondBits,
                scratch);

            return true;
        }

        return false;
    }

    private static void SortPackedPair64<
        TFirst,
        TSecond>(
        TFirst[] first,
        TSecond[] second,
        int[] permutation,
        int secondBits,
        PermutationIndexScratch scratch)
        where TFirst :
        unmanaged,
        IBinaryInteger<TFirst>,
        IUnsignedNumber<TFirst>
        where TSecond :
        unmanaged,
        IBinaryInteger<TSecond>,
        IUnsignedNumber<TSecond>
    {
        var length =
            permutation.Length;

        var span = scratch.GetPacked64(length);
        
        for (var row = 0;
             row < length;
             row++)
        {
            var a =
                ulong.CreateTruncating(
                    first[row]);

            var b =
                ulong.CreateTruncating(
                    second[row]);

            var key =
                (a << secondBits) |
                b;

            span[row] =
                (key << 32) |
                (uint)row;
        }

        span.Sort();

        for (var i = 0;
             i < length;
             i++)
        {
            permutation[i] =
                (int)(uint)span[i];
        }
    }

    private static void SortPackedPair128<
        TFirst,
        TSecond>(
        TFirst[] first,
        TSecond[] second,
        int[] permutation,
        int secondBits,
        PermutationIndexScratch scratch)
        where TFirst :
        unmanaged,
        IBinaryInteger<TFirst>,
        IUnsignedNumber<TFirst>
        where TSecond :
        unmanaged,
        IBinaryInteger<TSecond>,
        IUnsignedNumber<TSecond>
    {
        var length =
            permutation.Length;

        var span =
            scratch.GetPacked128(
                length);

        for (var row = 0;
             row < length;
             row++)
        {
            var a =
                UInt128.CreateTruncating(
                    first[row]);

            var b =
                UInt128.CreateTruncating(
                    second[row]);

            var key =
                (a << secondBits) |
                b;

            span[row] =
                (key << 32) |
                (uint)row;
        }

        span.Sort();

        for (var i = 0;
             i < length;
             i++)
        {
            permutation[i] =
                (int)(uint)span[i];
        }
    }

    private static IMaterialisedKey CreateEncodedKey<TCode>(
        TCode[] codes,
        bool[]? nulls,
        SortDirection direction,
        PermutationIndexScratch scratch)
        where TCode :
        unmanaged,
        IBinaryInteger<TCode>,
        IUnsignedNumber<TCode>
    {
        if (nulls is null)
        {
            return new EncodedKey<TCode>(codes, scratch);
        }

        // Nullable numeric representation can remain on the existing
        // generic path for this iteration.
        return IsNullsFirst(direction)
            ? new NullableKey<
                TCode,
                PrimitiveOrder<TCode>,
                Ascending,
                NullsFirst>(
                codes,
                nulls)
            : new NullableKey<
                TCode,
                PrimitiveOrder<TCode>,
                Ascending,
                NullsLast>(
                codes,
                nulls);
    }

    private interface ISymbolKey
    {
        int BucketCount { get; }

        int CodeAt(int row);
    }

    private readonly struct SymbolKey<TCode> :
        IMaterialisedKey,
        IRowKey<SymbolKey<TCode>>,
        ISymbolKey,
        IEncodedKey
        where TCode :
        unmanaged,
        IBinaryInteger<TCode>,
        IUnsignedNumber<TCode>
    {
        private readonly TCode[] _codes;
        private readonly int _bucketCount;
        private readonly PermutationIndexScratch _scratch;

        public SymbolKey(
            TCode[] codes,
            int bucketCount,
            PermutationIndexScratch scratch)
        {
            _codes = codes;
            _bucketCount = bucketCount;
            _scratch = scratch;
        }

        public int BucketCount =>
            _bucketCount;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int CodeAt(int row) =>
            int.CreateTruncating(_codes[row]);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Compare(
            in SymbolKey<TCode> key,
            int left,
            int right)
        {
            var a = key._codes[left];
            var b = key._codes[right];

            return a < b
                ? -1
                : a > b
                    ? 1
                    : 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int CompareRows(
            int left,
            int right) =>
            Compare(in this, left, right);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool EqualRows(
            int left,
            int right) =>
            _codes[left] == _codes[right];

        public void SortSingle(
            int[] permutation,
            int offset,
            int length)
        {
            permutation
                .AsSpan(offset, length)
                .Sort(
                    new SingleComparer<
                        SymbolKey<TCode>>(this));
        }

        public void SortPair(
            int[] permutation,
            IMaterialisedKey second)
        {
            if (second is IEncodedKey encoded &&
                encoded.TrySortEncodedPairSecond(
                    permutation,
                    _codes))
            {
                return;
            }

            second.SortPairSecond(
                permutation,
                this);
        }

        public void SortPairSecond<TFirst>(
            int[] permutation,
            TFirst first)
            where TFirst :
            struct,
            IRowKey<TFirst> =>
            permutation.AsSpan().Sort(
                new PairComparer<
                    TFirst,
                    SymbolKey<TCode>>(
                    first,
                    this));

        public bool TrySortEncodedPairSecond<TFirstCode>(
            int[] permutation,
            TFirstCode[] first)
            where TFirstCode :
            unmanaged,
            IBinaryInteger<TFirstCode>,
            IUnsignedNumber<TFirstCode> =>
            TrySortPackedPair(
                first,
                _codes,
                permutation,
                _scratch);
    }

    private static IMaterialisedKey MaterialiseSymbols<
        TSymbol,
        TOrder>(
        IReadOnlyList<T> rows,
        Func<T, object?> accessor,
        SortDirection direction,
        int firstNonNull,
        TSymbol firstValue,
        int keyOrdinal,
        PermutationIndexScratch scratch,
        out ISearchKey searchKey)
        where TSymbol : notnull
        where TOrder :
            struct,
            IValueOrder<TSymbol>
    {
        var rowCount =
            rows.Count;

        //
        // Required only if symbol encoding is abandoned because
        // cardinality becomes too high.
        //
        var values =
            scratch.GetSymbolValues<TSymbol>(
                keyOrdinal,
                rowCount);

        if (RuntimeHelpers
            .IsReferenceOrContainsReferences<TSymbol>())
        {
            values
                .AsSpan(0, rowCount)
                .Clear();
        }

        bool[]? nulls = null;

        if (firstNonNull != 0)
        {
            nulls =
                scratch.GetNulls(
                    keyOrdinal,
                    rowCount);

            nulls
                .AsSpan(0, firstNonNull)
                .Fill(true);
        }

        values[firstNonNull] =
            firstValue;

        var symbolIds =
            scratch.GetSymbolIds(
                keyOrdinal,
                rowCount);

        var symbolLimit =
            SortSymbolRanks.Limit(
                rowCount);

        //
        // Don't eagerly size a dictionary to potentially 65K
        // symbols when most actual cardinalities are small.
        //
        var initialCapacity =
            Math.Min(
                symbolLimit,
                256);

        var bySymbol =
            scratch.BeginSymbols<TSymbol>(
                initialCapacity);

        var uniqueSymbols =
            scratch.GetUniqueSymbols<TSymbol>(
                initialCapacity);

        var symbolCount = 1;

        uniqueSymbols[0] =
            firstValue;

        bySymbol.Add(
            firstValue,
            0);

        symbolIds[firstNonNull] =
            0;

        var symbolCandidate =
            true;

        for (var i = firstNonNull + 1;
             i < rowCount;
             i++)
        {
            var boxed =
                accessor(rows[i]);

            if (boxed is null)
            {
                if (nulls is null)
                {
                    nulls =
                        scratch.GetNulls(
                            keyOrdinal,
                            rowCount);
                }

                nulls[i] = true;
                continue;
            }

            if (boxed is not TSymbol value)
            {
                throw new InvalidCastException(
                    $"index key produced both "
                    + $"{typeof(TSymbol).Name} and "
                    + $"{boxed.GetType().Name}");
            }

            values[i] =
                value;

            if (!symbolCandidate)
                continue;

            ref var symbolId =
                ref CollectionsMarshal
                    .GetValueRefOrAddDefault(
                        bySymbol,
                        value,
                        out var exists);

            if (!exists)
            {
                //
                // The new entry has already been inserted into
                // bySymbol. Once the limit is exceeded we no longer
                // care about the symbol table because the typed
                // fallback will be returned.
                //
                symbolId =
                    symbolCount;

                if (symbolCount >= symbolLimit)
                {
                    symbolCandidate = false;
                    continue;
                }

                if (symbolCount ==
                    uniqueSymbols.Length)
                {
                    var newCapacity =
                        Math.Min(
                            symbolLimit,
                            checked(
                                uniqueSymbols.Length * 2));

                    uniqueSymbols =
                        scratch
                            .GetUniqueSymbols<TSymbol>(
                                newCapacity);
                }

                uniqueSymbols[symbolCount] =
                    value;

                symbolCount++;
            }

            symbolIds[i] =
                symbolId;
        }

        searchKey =
            SearchKeyFactory<
                    TSymbol,
                    TOrder>
                .Create(
                    accessor,
                    direction);

        if (!symbolCandidate)
        {
            return KeyColumnFactory<
                    TSymbol,
                    TOrder>
                .Create(
                    values,
                    nulls,
                    direction);
        }

        return CreateSymbolKey<
            TSymbol,
            TOrder>(
            symbolIds,
            nulls,
            uniqueSymbols,
            symbolCount,
            direction,
            rowCount,
            keyOrdinal,
            scratch);
    }

    private readonly struct SymbolIdComparer<
        TSymbol,
        TOrder>(
        TSymbol[] symbols)
        : IComparer<int>
        where TOrder :
        struct,
        IValueOrder<TSymbol>
    {
        private readonly TSymbol[] _symbols =
            symbols;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Compare(
            int left,
            int right) =>
            TOrder.Compare(
                _symbols[left],
                _symbols[right]);
    }

    private static IMaterialisedKey CreateSymbolKey<
        TSymbol,
        TOrder>(
        int[] symbolIds,
        bool[]? nulls,
        TSymbol[] symbols,
        int cardinality,
        SortDirection direction,
        int rowCount,
        int keyOrdinal,
        PermutationIndexScratch scratch)
        where TOrder :
        struct,
        IValueOrder<TSymbol>
    {
        var order =
            scratch.GetSymbolOrder(
                cardinality);

        var rankById =
            scratch.GetSymbolRanks(
                cardinality);

        SortSymbolRanks.RankBySortedValues(
            order,
            rankById,
            new SymbolIdComparer<
                TSymbol,
                TOrder>(symbols));

        if (cardinality <=
            byte.MaxValue)
        {
            return CreateSymbolCodes<byte>(
                symbolIds,
                nulls,
                rankById,
                cardinality,
                direction,
                rowCount,
                keyOrdinal,
                scratch);
        }

        return CreateSymbolCodes<ushort>(
            symbolIds,
            nulls,
            rankById,
            cardinality,
            direction,
            rowCount,
            keyOrdinal,
            scratch);
    }

    private static IMaterialisedKey CreateSymbolCodes<TCode>(
        int[] symbolIds,
        bool[]? nulls,
        ReadOnlySpan<int> rankById,
        int cardinality,
        SortDirection direction,
        int rowCount,
        int keyOrdinal,
        PermutationIndexScratch scratch)
        where TCode :
        unmanaged,
        IBinaryInteger<TCode>,
        IUnsignedNumber<TCode>
    {
        var codes =
            scratch.GetCodes<TCode>(
                keyOrdinal,
                rowCount);

        var descending =
            IsDescending(direction);

        var nullsFirst =
            IsNullsFirst(direction);

        for (var i = 0;
             i < rowCount;
             i++)
        {
            uint code;

            if (nulls is not null &&
                nulls[i])
            {
                code =
                    SortSymbolRanks.NullCode(
                        cardinality,
                        nullsFirst);
            }
            else
            {
                code =
                    SortSymbolRanks.Code(
                        rankById[
                            symbolIds[i]],
                        cardinality,
                        descending,
                        nullsFirst);
            }

            codes[i] =
                TCode.CreateChecked(code);
        }

        var bucketCount =
            cardinality +
            (nulls is null ? 0 : 1);

        return new SymbolKey<TCode>(
            codes,
            bucketCount,
            scratch);
    }

    private static IMaterialisedKey Materialise<
        TValue,
        TOrder>(
        IReadOnlyList<T> rows,
        Func<T, object?> accessor,
        SortDirection direction,
        int firstNonNull,
        TValue firstValue,
        out ISearchKey searchKey)
        where TOrder : struct, IValueOrder<TValue>
    {
        var values =
            GC.AllocateUninitializedArray<TValue>(
                rows.Count);

        bool[]? nulls = null;

        if (firstNonNull != 0)
        {
            nulls =
                new bool[rows.Count];

            Array.Fill(
                nulls,
                true,
                0,
                firstNonNull);
        }

        values[firstNonNull] =
            firstValue;

        for (var i = firstNonNull + 1;
             i < rows.Count;
             i++)
        {
            var boxed =
                accessor(rows[i]);

            if (boxed is null)
            {
                nulls ??=
                    new bool[rows.Count];

                nulls[i] = true;
                continue;
            }

            if (boxed is not TValue value)
            {
                throw new InvalidCastException(
                    $"index key produced both "
                    + $"{typeof(TValue).Name} and "
                    + $"{boxed.GetType().Name}");
            }

            values[i] = value;
        }

        searchKey =
            SearchKeyFactory<
                    TValue,
                    TOrder>
                .Create(
                    accessor,
                    direction);

        return KeyColumnFactory<
                TValue,
                TOrder>
            .Create(
                values,
                nulls,
                direction);
    }

    private static IMaterialisedKey MaterialiseFallback(
        IReadOnlyList<T> rows,
        Func<T, object?> accessor,
        SortDirection direction,
        int firstNonNull,
        object? firstValue,
        out ISearchKey searchKey)
    {
        var values =
            new object?[rows.Count];

        values[firstNonNull] =
            firstValue;

        for (var i = firstNonNull + 1;
             i < rows.Count;
             i++)
        {
            values[i] =
                accessor(rows[i]);
        }

        searchKey =
            new ObjectSearchKey(
                accessor,
                direction);

        return new ObjectKey(
            values,
            direction);
    }

    // ------------------------------------------------------------------
    // Typed row-key contracts
    // ------------------------------------------------------------------

    /// <summary>
    /// CRTP contract used only by the hot generic sort paths.
    ///
    /// A comparer parameterised by TKey can invoke TKey.Compare directly,
    /// rather than making an instance interface call through IRowKey.
    /// </summary>
    private interface IRowKey<TSelf>
        where TSelf :
        struct,
        IRowKey<TSelf>
    {
        static abstract int Compare(
            in TSelf key,
            int left,
            int right);
    }

    /// <summary>
    /// Heterogeneous/cold-path contract.
    ///
    /// Values implementing this interface are boxed once while the temporary
    /// key array is assembled. The arity-1/2 hot sort path escapes back into
    /// concrete struct types through SortSingle/SortPairSecond.
    /// </summary>
    private interface IMaterialisedKey
    {
        int CompareRows(int left, int right);

        bool EqualRows(int left, int right);

        void SortSingle(
            int[] permutation, int offset, int length);

        void SortPair(
            int[] permutation,
            IMaterialisedKey second);

        void SortPairSecond<TFirst>(int[] permutation, TFirst first)
            where TFirst : struct, IRowKey<TFirst>;
    }

    private static class KeyColumnFactory<
        TValue,
        TOrder>
        where TOrder :
        struct,
        IValueOrder<TValue>
    {
        public static IMaterialisedKey Create(
            TValue[] values,
            bool[]? nulls,
            SortDirection direction)
        {
            return direction switch
            {
                SortDirection.AscNullsFirst =>
                    nulls is null
                        ? new NonNullKey<
                            TValue,
                            TOrder,
                            Ascending>(values)
                        : new NullableKey<
                            TValue,
                            TOrder,
                            Ascending,
                            NullsFirst>(
                            values,
                            nulls),

                SortDirection.AscNullsLast =>
                    nulls is null
                        ? new NonNullKey<
                            TValue,
                            TOrder,
                            Ascending>(values)
                        : new NullableKey<
                            TValue,
                            TOrder,
                            Ascending,
                            NullsLast>(
                            values,
                            nulls),

                SortDirection.DescNullsFirst =>
                    nulls is null
                        ? new NonNullKey<
                            TValue,
                            TOrder,
                            Descending>(values)
                        : new NullableKey<
                            TValue,
                            TOrder,
                            Descending,
                            NullsFirst>(
                            values,
                            nulls),

                SortDirection.DescNullsLast =>
                    nulls is null
                        ? new NonNullKey<
                            TValue,
                            TOrder,
                            Descending>(values)
                        : new NullableKey<
                            TValue,
                            TOrder,
                            Descending,
                            NullsLast>(
                            values,
                            nulls),

                _ =>
                    throw new ArgumentOutOfRangeException(
                        nameof(direction),
                        direction,
                        "index direction must be explicit"),
            };
        }
    }

    private readonly struct NonNullKey<
        TValue,
        TOrder,
        TDirection> :
        IMaterialisedKey,
        IRowKey<
            NonNullKey<
                TValue,
                TOrder,
                TDirection>>
        where TOrder :
        struct,
        IValueOrder<TValue>
        where TDirection :
        struct,
        IDirection
    {
        private readonly TValue[] _values;

        public NonNullKey(
            TValue[] values)
        {
            _values = values;
        }

        /// <summary>
        /// Hot sort operation. Called statically through the CRTP constraint.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Compare(
            in NonNullKey<
                TValue,
                TOrder,
                TDirection> key,
            int left,
            int right)
        {
            var values = key._values;

            var comparison =
                TOrder.Compare(
                    values[left],
                    values[right]);

            return TDirection.Apply(
                comparison);
        }

        /// <summary>
        /// Heterogeneous fallback for CompositeComparer.
        /// Arity-1/2 sorting does not call this method.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int CompareRows(
            int left,
            int right) =>
            Compare(
                in this,
                left,
                right);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool EqualRows(
            int left,
            int right)
        {
            var values = _values;

            return TOrder.Compare(
                values[left],
                values[right]) == 0;
        }

        public void SortSingle(
            int[] permutation,
            int offset,
            int length) =>
            permutation
                .AsSpan(offset, length)
                .Sort(
                    new SingleComparer<
                        NonNullKey<
                            TValue,
                            TOrder,
                            TDirection>>(this));

        public object? BoxedValue(int row) =>
            _values[row];

        public void SortPair(
            int[] permutation,
            IMaterialisedKey second) =>
            second.SortPairSecond(
                permutation,
                this);

        public void SortPairSecond<TFirst>(
            int[] permutation,
            TFirst first)
            where TFirst :
            struct,
            IRowKey<TFirst> =>
            permutation.AsSpan().Sort(
                new PairComparer<
                    TFirst,
                    NonNullKey<
                        TValue,
                        TOrder,
                        TDirection>>(
                    first,
                    this));
    }

    private readonly struct NullableKey<
        TValue,
        TOrder,
        TDirection,
        TNullOrder> :
        IMaterialisedKey,
        IRowKey<
            NullableKey<
                TValue,
                TOrder,
                TDirection,
                TNullOrder>>
        where TOrder :
        struct,
        IValueOrder<TValue>
        where TDirection :
        struct,
        IDirection
        where TNullOrder :
        struct,
        INullOrder
    {
        private readonly TValue[] _values;
        private readonly bool[] _nulls;

        public NullableKey(
            TValue[] values,
            bool[] nulls)
        {
            _values = values;
            _nulls = nulls;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Compare(
            in NullableKey<
                TValue,
                TOrder,
                TDirection,
                TNullOrder> key,
            int left,
            int right)
        {
            var nulls = key._nulls;

            var leftNull =
                nulls[left];

            var rightNull =
                nulls[right];

            if (leftNull | rightNull)
            {
                if (leftNull == rightNull)
                    return 0;

                return TNullOrder.Compare(
                    leftNull,
                    rightNull);
            }

            var values = key._values;

            return TDirection.Apply(
                TOrder.Compare(
                    values[left],
                    values[right]));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int CompareRows(
            int left,
            int right) =>
            Compare(
                in this,
                left,
                right);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool EqualRows(
            int left,
            int right)
        {
            var leftNull =
                _nulls[left];

            var rightNull =
                _nulls[right];

            if (leftNull | rightNull)
                return leftNull == rightNull;

            return TOrder.Compare(
                _values[left],
                _values[right]) == 0;
        }

        public void SortSingle(
            int[] permutation,
            int offset,
            int length) =>
            permutation
                .AsSpan(offset, length)
                .Sort(
                    new SingleComparer<
                        NullableKey<
                            TValue,
                            TOrder,
                            TDirection, TNullOrder>>(this));

        public object? BoxedValue(int row) =>
            _nulls[row]
                ? null
                : _values[row];

        public void SortPair(
            int[] permutation,
            IMaterialisedKey second) =>
            second.SortPairSecond(
                permutation,
                this);

        public void SortPairSecond<TFirst>(
            int[] permutation,
            TFirst first)
            where TFirst :
            struct,
            IRowKey<TFirst> =>
            permutation.AsSpan().Sort(
                new PairComparer<
                    TFirst,
                    NullableKey<
                        TValue,
                        TOrder,
                        TDirection,
                        TNullOrder>>(
                    first,
                    this));
    }

    // ------------------------------------------------------------------
    // CRTP sort comparers
    // ------------------------------------------------------------------

    private readonly struct SingleComparer<TKey>(
        TKey key)
        : IComparer<int>
        where TKey :
        struct,
        IRowKey<TKey>
    {
        private readonly TKey _key = key;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Compare(
            int left,
            int right)
        {
            var comparison =
                TKey.Compare(
                    in _key,
                    left,
                    right);

            return comparison != 0
                ? comparison
                : left.CompareTo(right);
        }
    }

    private readonly struct PairComparer<
        TFirst,
        TSecond>(
        TFirst first,
        TSecond second)
        : IComparer<int>
        where TFirst :
        struct,
        IRowKey<TFirst>
        where TSecond :
        struct,
        IRowKey<TSecond>
    {
        private readonly TFirst _first = first;
        private readonly TSecond _second = second;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Compare(
            int left,
            int right)
        {
            var comparison =
                TFirst.Compare(
                    in _first,
                    left,
                    right);

            if (comparison != 0)
                return comparison;

            comparison =
                TSecond.Compare(
                    in _second,
                    left,
                    right);

            return comparison != 0
                ? comparison
                : left.CompareTo(right);
        }
    }

    /// <summary>
    /// General 3+-column fallback. This deliberately remains heterogeneous;
    /// optimise additional fixed arities only if profiling justifies it.
    /// </summary>
    private readonly struct CompositeComparer(
        IMaterialisedKey[] keys)
        : IComparer<int>
    {
        private readonly IMaterialisedKey[] _keys =
            keys;

        public int Compare(
            int left,
            int right)
        {
            foreach (var key in _keys)
            {
                var comparison =
                    key.CompareRows(
                        left,
                        right);

                if (comparison != 0)
                    return comparison;
            }

            return left.CompareTo(right);
        }
    }

    // ------------------------------------------------------------------
    // Value ordering policies
    // ------------------------------------------------------------------

    private interface IValueOrder<TValue>
    {
        static abstract int Compare(
            TValue left,
            TValue right);
    }

    private readonly struct PrimitiveOrder<TValue>
        : IValueOrder<TValue>
        where TValue :
        IComparisonOperators<
            TValue,
            TValue,
            bool>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Compare(
            TValue left,
            TValue right) =>
            left < right
                ? -1
                : left > right
                    ? 1
                    : 0;
    }

    private static bool IsDescending(
        SortDirection direction) =>
        direction is
            SortDirection.DescNullsFirst or
            SortDirection.DescNullsLast;

    private static bool IsNullsFirst(
        SortDirection direction) =>
        direction is
            SortDirection.AscNullsFirst or
            SortDirection.DescNullsFirst;

    private readonly struct ComparableOrder<TValue>
        : IValueOrder<TValue>
        where TValue :
        IComparable<TValue>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Compare(
            TValue left,
            TValue right) =>
            left.CompareTo(right);
    }

    private readonly struct StringOrder
        : IValueOrder<string>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Compare(
            string left,
            string right) =>
            string.CompareOrdinal(
                left,
                right);
    }

    private readonly struct Utf8Order
        : IValueOrder<Utf8String>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Compare(
            Utf8String left,
            Utf8String right) =>
            left.CompareTo(right);
    }

    private readonly struct ByteArrayOrder
        : IValueOrder<byte[]>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Compare(
            byte[] left,
            byte[] right) =>
            left.AsSpan()
                .SequenceCompareTo(right);
    }

    private readonly struct MemoryByteOrder
        : IValueOrder<ReadOnlyMemory<byte>>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Compare(
            ReadOnlyMemory<byte> left,
            ReadOnlyMemory<byte> right) =>
            left.Span.SequenceCompareTo(
                right.Span);
    }

    private readonly struct FloatOrder
        : IValueOrder<float>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Compare(
            float left,
            float right)
        {
            if (float.IsNaN(left))
            {
                return float.IsNaN(right)
                    ? 0
                    : 1;
            }

            if (float.IsNaN(right))
                return -1;

            return left < right
                ? -1
                : left > right
                    ? 1
                    : 0;
        }
    }

    private readonly struct DoubleOrder
        : IValueOrder<double>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Compare(
            double left,
            double right)
        {
            if (double.IsNaN(left))
            {
                return double.IsNaN(right)
                    ? 0
                    : 1;
            }

            if (double.IsNaN(right))
                return -1;

            return left < right
                ? -1
                : left > right
                    ? 1
                    : 0;
        }
    }

    // ------------------------------------------------------------------
    // Direction / NULL policies
    // ------------------------------------------------------------------

    private interface IDirection
    {
        static abstract int Apply(
            int comparison);
    }

    private readonly struct Ascending
        : IDirection
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Apply(
            int comparison) =>
            comparison;
    }

    private readonly struct Descending
        : IDirection
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Apply(
            int comparison) =>
            -comparison;
    }

    private interface INullOrder
    {
        static abstract int Compare(
            bool leftNull,
            bool rightNull);
    }

    private readonly struct NullsFirst
        : INullOrder
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Compare(
            bool leftNull,
            bool rightNull) =>
            leftNull
                ? -1
                : 1;
    }

    private readonly struct NullsLast
        : INullOrder
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Compare(
            bool leftNull,
            bool rightNull) =>
            leftNull
                ? 1
                : -1;
    }

    // ------------------------------------------------------------------
    // Search
    // ------------------------------------------------------------------

    private interface ISearchKey
    {
        int Compare(
            T row,
            object? bound);
    }

    private static class SearchKeyFactory<
        TValue,
        TOrder>
        where TOrder :
        struct,
        IValueOrder<TValue>
    {
        public static ISearchKey Create(
            Func<T, object?> accessor,
            SortDirection direction) =>
            new SearchKey<
                TValue,
                TOrder>(
                accessor,
                direction);
    }

    private sealed class SearchKey<TValue, TOrder>(
        Func<T, object?> accessor,
        SortDirection direction)
        : ISearchKey
        where TOrder :
        struct,
        IValueOrder<TValue>
    {
        private readonly SortDirection _direction =
            direction;

        private readonly bool _descending =
            direction is
                SortDirection.DescNullsFirst or
                SortDirection.DescNullsLast;

        private readonly bool _nullsFirst =
            direction is
                SortDirection.AscNullsFirst or
                SortDirection.DescNullsFirst;

        public int Compare(
            T row,
            object? bound)
        {
            var boxed =
                accessor(row);

            if (boxed is null)
            {
                if (bound is null)
                    return 0;

                return _nullsFirst
                    ? -1
                    : 1;
            }

            if (bound is null)
            {
                return _nullsFirst
                    ? 1
                    : -1;
            }

            //
            // Normal hot path. The representation matches the one
            // observed when the index was constructed.
            //
            if (boxed is TValue value)
            {
                var comparison =
                    CompareBound<TValue, TOrder>(
                        value,
                        bound);

                return _descending
                    ? -comparison
                    : comparison;
            }

            //
            // Cold path: representation changed or is an allowed
            // equivalent representation such as string/Utf8String.
            //
            return SourceValueOrder.Compare(
                boxed,
                bound,
                _direction);
        }
    }

    private sealed class AllNullSearchKey(
        Func<T, object?> accessor,
        SortDirection direction)
        : ISearchKey
    {
        private readonly bool _nullsFirst =
            direction is
                SortDirection.AscNullsFirst or
                SortDirection.DescNullsFirst;

        public int Compare(
            T row,
            object? bound)
        {
            var value =
                accessor(row);

            if (value is not null)
            {
                return SourceValueOrder.Compare(
                    value,
                    bound,
                    direction);
            }

            if (bound is null)
                return 0;

            return _nullsFirst
                ? -1
                : 1;
        }
    }

    private static int CompareBound<
        TValue,
        TOrder>(
        TValue left,
        object right)
        where TOrder :
        struct,
        IValueOrder<TValue>
    {
        if (typeof(TValue) == typeof(string))
        {
            var text =
                (string)(object)left!;

            return right switch
            {
                string other =>
                    string.CompareOrdinal(
                        text,
                        other),

                Utf8String other =>
                    -other.CompareTo(
                        Utf8String.FromString(
                            text)),

                _ =>
                    throw Incompatible<TValue>(
                        right),
            };
        }

        if (typeof(TValue) ==
            typeof(Utf8String))
        {
            var text =
                (Utf8String)(object)left!;

            return right switch
            {
                Utf8String other =>
                    text.CompareTo(other),

                string other =>
                    text.CompareTo(
                        Utf8String.FromString(
                            other)),

                _ =>
                    throw Incompatible<TValue>(
                        right),
            };
        }

        if (typeof(TValue) == typeof(float))
        {
            var value =
                (float)(object)left!;

            var other =
                right switch
                {
                    float f => (double)f,
                    double d => d,
                    _ => throw Incompatible<TValue>(
                        right),
                };

            return CompareReal(
                value,
                other);
        }

        if (typeof(TValue) == typeof(double))
        {
            var value =
                (double)(object)left!;

            var other =
                right switch
                {
                    float f => (double)f,
                    double d => d,
                    _ => throw Incompatible<TValue>(
                        right),
                };

            return CompareReal(
                value,
                other);
        }

        if (typeof(TValue) == typeof(byte[]))
        {
            var value =
                (byte[])(object)left!;

            return right switch
            {
                byte[] bytes =>
                    value.AsSpan()
                        .SequenceCompareTo(bytes),

                ReadOnlyMemory<byte> memory =>
                    value.AsSpan()
                        .SequenceCompareTo(
                            memory.Span),

                _ =>
                    throw Incompatible<TValue>(
                        right),
            };
        }

        if (typeof(TValue) ==
            typeof(ReadOnlyMemory<byte>))
        {
            var value =
                (ReadOnlyMemory<byte>)(object)left!;

            return right switch
            {
                byte[] bytes =>
                    value.Span
                        .SequenceCompareTo(bytes),

                ReadOnlyMemory<byte> memory =>
                    value.Span
                        .SequenceCompareTo(
                            memory.Span),

                _ =>
                    throw Incompatible<TValue>(
                        right),
            };
        }

        if (right is not TValue typed)
        {
            throw Incompatible<TValue>(
                right);
        }

        return TOrder.Compare(
            left,
            typed);
    }

    private static InvalidCastException Incompatible<TValue>(
        object right) =>
        new(
            $"cannot compare {typeof(TValue).Name} "
            + $"with {right.GetType().Name}");

    private static int CompareReal(
        double left,
        double right)
    {
        if (double.IsNaN(left))
        {
            return double.IsNaN(right)
                ? 0
                : 1;
        }

        if (double.IsNaN(right))
            return -1;

        return left < right
            ? -1
            : left > right
                ? 1
                : 0;
    }

    // ------------------------------------------------------------------
    // Rare object fallback
    // ------------------------------------------------------------------

    private readonly struct ObjectKey :
        IMaterialisedKey,
        IRowKey<ObjectKey>
    {
        private readonly object?[] _values;
        private readonly SortDirection _direction;

        public ObjectKey(
            object?[] values,
            SortDirection direction)
        {
            _values = values;
            _direction = direction;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Compare(
            in ObjectKey key,
            int left,
            int right) =>
            SourceValueOrder.Compare(
                key._values[left],
                key._values[right],
                key._direction);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int CompareRows(
            int left,
            int right) =>
            Compare(
                in this,
                left,
                right);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool EqualRows(
            int left,
            int right) =>
            SourceValueOrder.Compare(
                _values[left],
                _values[right],
                _direction) == 0;

        public void SortSingle(
            int[] permutation,
            int offset,
            int length) =>
            permutation
                .AsSpan(offset, length)
                .Sort(
                    new SingleComparer<
                        ObjectKey>(this));

        public object? BoxedValue(int row) =>
            _values[row];

        public void SortPair(
            int[] permutation,
            IMaterialisedKey second) =>
            second.SortPairSecond(
                permutation,
                this);

        public void SortPairSecond<TFirst>(
            int[] permutation,
            TFirst first)
            where TFirst :
            struct,
            IRowKey<TFirst> =>
            permutation.AsSpan().Sort(
                new PairComparer<
                    TFirst,
                    ObjectKey>(
                    first,
                    this));
    }

    private sealed class ObjectSearchKey(
        Func<T, object?> accessor,
        SortDirection direction)
        : ISearchKey
    {
        public int Compare(
            T row,
            object? bound) =>
            SourceValueOrder.Compare(
                accessor(row),
                bound,
                direction);
    }

    // ------------------------------------------------------------------
    // All-NULL special case
    // ------------------------------------------------------------------

    private sealed class AllNullKey :
        IMaterialisedKey
    {
        public static AllNullKey Instance { get; } =
            new();

        private AllNullKey()
        {
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int CompareRows(
            int left,
            int right) =>
            0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool EqualRows(
            int left,
            int right) =>
            true;

        public void SortSingle(int[] permutation, int offset, int length)
        {
            // Identity order already supplies the deterministic
            // row-position tie-break.
        }
        
        public void SortPair(
            int[] permutation,
            IMaterialisedKey second)
        {
            // Constant first key: ordering is entirely the second key.
            second.SortSingle(
                permutation, 0, 0);
        }

        public void SortPairSecond<TFirst>(
            int[] permutation,
            TFirst first)
            where TFirst :
            struct,
            IRowKey<TFirst>
        {
            // Constant second key: ordering is entirely the first key.
            permutation.AsSpan().Sort(
                new SingleComparer<TFirst>(
                    first));
        }
    }

    // ------------------------------------------------------------------
    // Allocation-free range-bound view
    // ------------------------------------------------------------------

    private readonly struct BoundView
    {
        private readonly IReadOnlyList<object?>? _source;
        private readonly int _sourceCount;
        private readonly bool _appendNull;

        public BoundView(
            IReadOnlyList<object?> source)
            : this(
                source,
                source.Count,
                appendNull: false)
        {
        }

        public BoundView(
            IReadOnlyList<object?>? source,
            int sourceCount,
            bool appendNull)
        {
            _source = source;
            _sourceCount = sourceCount;
            _appendNull = appendNull;
        }

        public int Count =>
            _sourceCount +
            (_appendNull ? 1 : 0);

        public object? this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get =>
                index < _sourceCount
                    ? _source![index]
                    : null;
        }

        public BoundView AppendNull() =>
            new(
                _source,
                _sourceCount,
                appendNull: true);
    }

    private static string Render(
        object? value) =>
        value switch
        {
            null =>
                "NULL",

            string text =>
                $"'{text}'",

            ReadOnlyMemory<byte> bytes =>
                $"0x{Convert.ToHexString(bytes.Span)}",

            _ =>
                value.ToString() ??
                string.Empty,
        };
}
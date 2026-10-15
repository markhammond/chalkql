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
/// still costs about one int per row, or nothing when backed by a declared collation.
///
/// The permutation is held in leaves (F202), so an append reads only the rows it
/// appends, places them by a search of the order before, and rewrites only the
/// leaves they land in.
/// </summary>
public sealed class PermutationIndex<T> : IPositionalPocoIndex<T>, IReversiblePocoIndex<T>
{
    private readonly IReadOnlyList<T> _rows;
    private readonly ISearchKey[] _searchKeys;
    private readonly SortDirection[] _directions;

    /// <summary>
    /// Row positions in key order, or null when the collection is already in that order: in leaves, so
    /// that an append extends them by what it appends (F202).
    /// </summary>
    private readonly PositionLeaves? _positions;

    /// <summary>
    /// Exact distinct count for each key prefix: counted when the index is built whole, and when first
    /// asked for after an append, since an append no longer reads every row (F202).
    /// </summary>
    private long[]? _prefixDistinct;

    private readonly Lock _distinctGate = new();

    private readonly int _rowCount;

    /// <summary>The key accessors and columns, kept to count distinct prefixes when asked.</summary>
    private readonly Func<T, object?>[] _accessors;

    private readonly PocoColumn<T>[]? _keyColumns;

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
        int appendedFrom,
        PocoColumn<T>[]? keyColumns = null)
    {
        Descriptor = descriptor;
        _rows = rows;
        _rowCount = rows.Count;
        _accessors = keys;
        _keyColumns = keyColumns;

        // An append extends the order of the index it follows (F202): only the appended rows are read,
        // put in order among themselves, and placed by a search of that index, so the work is what
        // was appended rather than the table.
        if (!collationBacked
            && previous?._positions is not null
            && appendedFrom > 0
            && appendedFrom == previous._rowCount
            && appendedFrom <= _rowCount
            && previous.Extended(rows, keys, keyColumns, appendedFrom, scratch, table, keyColumnNames) is { } extended)
        {
            _directions = previous._directions;
            _searchKeys = previous._searchKeys;
            _positions = extended.Positions;
            ExtendedInOrder = extended.InOrder;
            return;
        }

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
                    keyColumns?[k],
                    direction,
                    k,
                    scratch,
                    out _searchKeys[k]);
        }

        int[]? order = null;
        if (collationBacked)
        {
            // The collection is already in key order, so appending rows that keep it appends to the
            // index too — which is what the D17 verification has just been asked to confirm.
            _positions = null;
            ExtendedInOrder = true;
        }
        else
        {
            var permutation =
                GC.AllocateUninitializedArray<int>(_rowCount);

            for (var i = 0; i < permutation.Length; i++)
                permutation[i] = i;

            Sort(permutation, materialised, scratch);
            order = permutation;
            _positions = PositionLeaves.FromSorted(permutation);
            ExtendedInOrder = previous is null && appendedFrom == 0;
        }

        _prefixDistinct =
            CountPrefixDistinct(materialised, order);

        if (descriptor.Unique)
        {
            VerifyUnique(
                materialised,
                keys,
                table,
                keyColumnNames,
                order);
        }
    }

    /// <summary>
    /// This index's order with the rows from <paramref name="appendedFrom"/> on put in it (F202), or
    /// null when they must be indexed whole: when they bring the first value to a key that held only
    /// NULLs, whose search reads no type. The appended rows' keys alone are read and put in order;
    /// each is placed after the last row whose key is not greater, which is where a whole sort puts it,
    /// since equal keys are in row order and the appended rows come after every row before them. A
    /// unique index refuses an appended key equal to its neighbour there, as a whole check would.
    /// </summary>
    private (PositionLeaves Positions, bool InOrder)? Extended(
        IReadOnlyList<T> rows,
        Func<T, object?>[] keys,
        PocoColumn<T>[]? keyColumns,
        int appendedFrom,
        PermutationIndexScratch scratch,
        string table,
        IReadOnlyList<string> keyColumnNames)
    {
        var added = rows.Count - appendedFrom;
        var appended = new Appended(rows, appendedFrom, added);
        var materialised = new IMaterialisedKey[keys.Length];
        for (var k = 0; k < keys.Length; k++)
        {
            materialised[k] = MaterialiseKey(
                appended, keys[k], keyColumns?[k], _directions[k], k, scratch, out _);
            if (_searchKeys[k] is AllNullSearchKey && !ReferenceEquals(materialised[k], AllNullKey.Instance))
            {
                return null;
            }
        }

        var order = GC.AllocateUninitializedArray<int>(added);
        for (var i = 0; i < added; i++)
        {
            order[i] = i;
        }

        Sort(order, materialised, scratch);

        var ordinals = GC.AllocateUninitializedArray<int>(added);
        var positions = GC.AllocateUninitializedArray<int>(added);
        var values = new object?[keys.Length];
        var low = 0;
        var inOrder = true;
        for (var i = 0; i < added; i++)
        {
            var at = order[i];
            var row = rows[appendedFrom + at];
            for (var k = 0; k < keys.Length; k++)
            {
                values[k] = keys[k](row);
            }

            var bound = new BoundView(values);
            var ordinal = Search(in bound, atLeast: 1, low);
            if (Descriptor.Unique)
            {
                if (i > 0 && ordinal == ordinals[i - 1])
                {
                    if (Equal(materialised, order[i - 1], at))
                    {
                        throw Duplicate(
                            rows, positions[i - 1], appendedFrom + at, keys, table, keyColumnNames);
                    }
                }
                else if (ordinal > 0 && CompareAt(ordinal - 1, in bound) == 0)
                {
                    throw Duplicate(
                        rows, GetPosition(ordinal - 1), appendedFrom + at, keys, table, keyColumnNames);
                }
            }

            ordinals[i] = ordinal;
            positions[i] = appendedFrom + at;
            inOrder &= ordinal == appendedFrom && at == i;
            low = ordinal;
        }

        return (_positions!.Insert(ordinals, positions), inOrder);
    }

    /// <summary>Whether two of the materialised rows have equal keys in every column.</summary>
    private static bool Equal(IMaterialisedKey[] keys, int first, int second)
    {
        for (var k = 0; k < keys.Length; k++)
        {
            if (!keys[k].EqualRows(first, second))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The rows from <c>from</c> on, as a list of their own: what an append's keys are read from.</summary>
    private sealed class Appended(IReadOnlyList<T> rows, int from, int count) : IReadOnlyList<T>
    {
        public T this[int index] => rows[from + index];

        public int Count => count;

        public IEnumerator<T> GetEnumerator()
        {
            for (var i = 0; i < count; i++)
            {
                yield return rows[from + i];
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
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
        _positions is null;

    public long BytesPerRow =>
        _positions is not { Count: > 0 } positions
            ? 0
            : (positions.Bytes + positions.Count - 1) / positions.Count;

    public long? DistinctCount(int keyPositions)
    {
        var distinct = Volatile.Read(ref _prefixDistinct) ?? CountPrefixDistinctNow();
        return keyPositions >= 1 &&
            keyPositions <= distinct.Length
                ? distinct[keyPositions - 1]
                : null;
    }

    /// <summary>
    /// The distinct prefixes of an index an append extended, counted the first time they are asked for
    /// (F202): its keys are read over every row once, as a build reads them, with buffers of its own,
    /// since an append may be building the next index meanwhile.
    /// </summary>
    private long[] CountPrefixDistinctNow()
    {
        lock (_distinctGate)
        {
            if (_prefixDistinct is { } counted)
            {
                return counted;
            }

            var scratch = new PermutationIndexScratch();
            var materialised = new IMaterialisedKey[_accessors.Length];
            for (var k = 0; k < _accessors.Length; k++)
            {
                materialised[k] = MaterialiseKey(
                    _rows, _accessors[k], _keyColumns?[k], _directions[k], k, scratch, out _);
            }

            counted = CountPrefixDistinct(materialised, _positions?.ToArray());
            Volatile.Write(ref _prefixDistinct, counted);
            return counted;
        }
    }

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

    /// <summary>
    /// Any range at all (D283): the window a range resolves to is a contiguous slice of the
    /// permutation, and walking it from its end costs exactly what walking it from its start does.
    /// </summary>
    public Chalk.Ir.IndexReversal Reversal =>
        Chalk.Ir.IndexReversal.Any;

    /// <inheritdoc />
    public IEnumerable<T> LookupReversed(
        IndexKeyRange range)
    {
        GetWindow(
            range,
            out var from,
            out var to);

        for (var ordinal = to - 1;
             ordinal >= from;
             ordinal--)
        {
            yield return _rows[
                GetPosition(ordinal)];
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetPosition(int ordinal)
    {
        var positions = _positions;

        return positions is null
            ? ordinal
            : positions[ordinal];
    }

    /// <summary>
    /// The positions of the ordinals from <paramref name="from"/> to <paramref name="to"/>, in key
    /// order or, <paramref name="reverse"/>, from the last: walked leaf by leaf (F202), where
    /// <see cref="GetPosition"/> finds each ordinal's leaf.
    /// </summary>
    internal IEnumerable<int> PositionsBetween(int from, int to, bool reverse)
    {
        if (from >= to)
        {
            yield break;
        }

        if (_positions is not { } leaves)
        {
            if (reverse)
            {
                for (var ordinal = to - 1; ordinal >= from; ordinal--)
                {
                    yield return ordinal;
                }
            }
            else
            {
                for (var ordinal = from; ordinal < to; ordinal++)
                {
                    yield return ordinal;
                }
            }

            yield break;
        }

        if (reverse)
        {
            for (var leaf = leaves.LeafOf(to - 1); leaf >= 0 && leaves.StartOf(leaf + 1) > from; leaf--)
            {
                var array = leaves.ArrayOf(leaf);
                var start = leaves.StartOf(leaf);
                var offset = leaves.OffsetOf(leaf) - start;
                var last = Math.Max(from, start);
                for (var ordinal = Math.Min(to, leaves.StartOf(leaf + 1)) - 1; ordinal >= last; ordinal--)
                {
                    yield return array[offset + ordinal];
                }
            }

            yield break;
        }

        for (var leaf = leaves.LeafOf(from); leaf < leaves.LeafCount && leaves.StartOf(leaf) < to; leaf++)
        {
            var array = leaves.ArrayOf(leaf);
            var start = leaves.StartOf(leaf);
            var offset = leaves.OffsetOf(leaf) - start;
            var end = Math.Min(to, leaves.StartOf(leaf + 1));
            for (var ordinal = Math.Max(from, start); ordinal < end; ordinal++)
            {
                yield return array[offset + ordinal];
            }
        }
    }

    internal bool Describes(
        IReadOnlyList<T> rows) =>
        ReferenceEquals(rows, _rows) &&
        rows.Count == _rowCount;

    /// <summary>
    /// Row positions in key order, or null when the collection is already in that order. Read by
    /// <see cref="ClusteredIndex{T}"/>, which extracts its copy through exactly this order (D257);
    /// nobody may write to it.
    /// </summary>
    internal PositionLeaves? Positions =>
        _positions;

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

        Window(
            new BoundView(range.Lower),
            range.LowerInclusive,
            new BoundView(range.Upper),
            range.UpperInclusive,
            bounded,
            out from,
            out to);
    }

    /// <summary>
    /// Whether an as-of seek's bounds — the leading key columns' then the time's — can be compared with
    /// this index's keys straight from their columns, which every key with a typed reader can.
    /// </summary>
    internal bool CanSeek(
        SeekBound[] bounds)
    {
        if (bounds.Length == 0 || bounds.Length > _searchKeys.Length)
            return false;

        for (var k = 0; k < bounds.Length; k++)
        {
            if (!_searchKeys[k].CanSeek(in bounds[k]))
                return false;
        }

        return true;
    }

    /// <summary>
    /// The one row an as-of seek finds for the left row <paramref name="row"/>, or -1: the range its keys
    /// and time bound, as an <see cref="IndexKeyRange"/> of them would, read from its closest end.
    /// </summary>
    internal int Seek(
        SeekBound[] bounds,
        int row,
        bool backwards,
        bool inclusive)
    {
        var keys =
            new BoundView(bounds, row, bounds.Length - 1);

        var withTime =
            new BoundView(bounds, row, bounds.Length);

        // Backwards: from the keys, inclusive, to the keys and time; forwards: from the keys and time to
        // the keys, inclusive. The time's own end is inclusive as the match is.
        Window(
            backwards ? keys : withTime,
            backwards || inclusive,
            backwards ? withTime : keys,
            !backwards || inclusive,
            bounds.Length,
            out var from,
            out var to);

        if (from >= to)
            return -1;

        return GetPosition(
            backwards
                ? to - 1
                : from);
    }

    /// <summary>The ordinals between two bounds over the first <paramref name="bounded"/> key columns.</summary>
    private void Window(
        BoundView lower,
        bool lowerInclusive,
        BoundView upper,
        bool upperInclusive,
        int bounded,
        out int from,
        out int to)
    {
        var direction =
            _directions[bounded - 1];

        var nullsLast =
            direction is
                SortDirection.AscNullsLast or
                SortDirection.DescNullsLast;

        var prefix =
            (lower.Count >= upper.Count ? lower : upper)
                .Prefix(bounded - 1);

        if (lower.Count < bounded)
        {
            lower =
                prefix;

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
                prefix;

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

    private int Search(
        in BoundView bound,
        int atLeast,
        int low = 0)
    {
        if (bound.Count == 0)
        {
            return atLeast == 0
                ? 0
                : _rowCount;
        }

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
                bound.IsTypedAt(k)
                    ? _searchKeys[k].Compare(
                        row,
                        in bound.Seek(k),
                        bound.Row)
                    : _searchKeys[k].Compare(
                        row,
                        bound[k]);

            if (comparison != 0)
                return comparison;
        }

        return 0;
    }

    private long[] CountPrefixDistinct(
        IMaterialisedKey[] keys,
        int[]? permutation)
    {
        var distinct =
            new long[keys.Length];

        if (_rowCount == 0)
            return distinct;

        Array.Fill(
            distinct,
            1L);

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
        IReadOnlyList<string> keyColumnNames,
        int[]? permutation)
    {
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

            if (Equal(keys, previous, current))
            {
                throw Duplicate(_rows, previous, current, accessors, table, keyColumnNames);
            }
        }
    }

    /// <summary>The refusal of two rows of a unique index that share its key, naming both and the key.</summary>
    private CatalogVerificationException Duplicate(
        IReadOnlyList<T> rows,
        int previous,
        int current,
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

        var rendered = new string[accessors.Length];
        var row = rows[current];

        for (var k = 0; k < accessors.Length; k++)
            rendered[k] = Render(accessors[k](row));

        var first = Math.Min(previous, current);
        var second = Math.Max(previous, current);

        return new CatalogVerificationException(
            table,
            declaration,
            second,
            $"rows {first} and {second} share the key "
            + $"({string.Join(", ", rendered)})");
    }

    // ------------------------------------------------------------------
    // Materialisation
    // ------------------------------------------------------------------

    /// <summary>
    /// Row <paramref name="i"/>'s key, through the column's typed reader when it has one (design 69
    /// §5) and boxed otherwise; false for NULL.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ReadKey<TValue>(
        IReadOnlyList<T> rows,
        int i,
        PocoValueReader<T, TValue>? typed,
        Func<T, object?> accessor,
        out TValue value)
    {
        if (typed is not null)
        {
            return typed(rows[i], out value);
        }

        var boxed =
            accessor(rows[i]);

        if (boxed is null)
        {
            value = default!;
            return false;
        }

        if (boxed is not TValue cast)
        {
            throw new InvalidCastException(
                $"index key produced both "
                + $"{typeof(TValue).Name} and "
                + $"{boxed.GetType().Name}");
        }

        value = cast;
        return true;
    }

    private static IMaterialisedKey MaterialiseKey(
        IReadOnlyList<T> rows,
        Func<T, object?> accessor,
        PocoColumn<T>? column,
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
                    rows, accessor, column, direction,
                    firstNonNull, value,
                    keyOrdinal, scratch,
                    out searchKey),

            byte value =>
                MaterialiseEncoded<
                    byte,
                    byte,
                    ByteEncoding,
                    ComparableOrder<byte>>(
                    rows, accessor, column, direction,
                    firstNonNull, value,
                    keyOrdinal, scratch,
                    out searchKey),

            short value =>
                MaterialiseEncoded<
                    short,
                    ushort,
                    Int16Encoding,
                    ComparableOrder<short>>(
                    rows, accessor, column, direction,
                    firstNonNull, value,
                    keyOrdinal, scratch,
                    out searchKey),

            ushort value =>
                MaterialiseEncoded<
                    ushort,
                    ushort,
                    UInt16Encoding,
                    ComparableOrder<ushort>>(
                    rows, accessor, column, direction,
                    firstNonNull, value,
                    keyOrdinal, scratch,
                    out searchKey),

            int value =>
                MaterialiseEncoded<
                    int,
                    uint,
                    Int32Encoding,
                    ComparableOrder<int>>(
                    rows, accessor, column, direction,
                    firstNonNull, value,
                    keyOrdinal, scratch,
                    out searchKey),

            uint value =>
                MaterialiseEncoded<
                    uint,
                    uint,
                    UInt32Encoding,
                    ComparableOrder<uint>>(
                    rows, accessor, column, direction,
                    firstNonNull, value,
                    keyOrdinal, scratch,
                    out searchKey),

            long value =>
                MaterialiseEncoded<
                    long,
                    ulong,
                    Int64Encoding,
                    ComparableOrder<long>>(
                    rows, accessor, column, direction,
                    firstNonNull, value,
                    keyOrdinal, scratch,
                    out searchKey),

            ulong value =>
                MaterialiseEncoded<
                    ulong,
                    ulong,
                    UInt64Encoding,
                    ComparableOrder<ulong>>(
                    rows, accessor, column, direction,
                    firstNonNull, value,
                    keyOrdinal, scratch,
                    out searchKey),

            float value =>
                MaterialiseEncoded<
                    float,
                    uint,
                    SingleEncoding,
                    FloatOrder>(
                    rows, accessor, column, direction,
                    firstNonNull, value,
                    keyOrdinal, scratch,
                    out searchKey),

            double value =>
                MaterialiseEncoded<
                    double,
                    ulong,
                    DoubleEncoding,
                    DoubleOrder>(
                    rows, accessor, column, direction,
                    firstNonNull, value,
                    keyOrdinal, scratch,
                    out searchKey),

            DateTime value =>
                MaterialiseEncoded<
                    DateTime,
                    ulong,
                    DateTimeEncoding,
                    ComparableOrder<DateTime>>(
                    rows, accessor, column, direction,
                    firstNonNull, value,
                    keyOrdinal, scratch,
                    out searchKey),

            DateTimeOffset value =>
                MaterialiseEncoded<
                    DateTimeOffset,
                    ulong,
                    DateTimeOffsetEncoding,
                    ComparableOrder<DateTimeOffset>>(
                    rows, accessor, column, direction,
                    firstNonNull, value,
                    keyOrdinal, scratch,
                    out searchKey),

            string value =>
                MaterialiseSymbols<
                    string,
                    StringOrder>(
                    rows,
                    accessor,
                    column,
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
                    column,
                    direction,
                    firstNonNull,
                    value,
                    keyOrdinal, scratch,
                    out searchKey),

            // Leave these as logical representations for this iteration.
            decimal value =>
                Materialise<decimal, ComparableOrder<decimal>>(
                    rows, accessor, column, direction,
                    firstNonNull, value, out searchKey),

            Guid value =>
                Materialise<Guid, ComparableOrder<Guid>>(
                    rows, accessor, column, direction,
                    firstNonNull, value, out searchKey),

            byte[] value =>
                Materialise<byte[], ByteArrayOrder>(
                    rows, accessor, column, direction,
                    firstNonNull, value, out searchKey),

            ReadOnlyMemory<byte> value =>
                Materialise<
                    ReadOnlyMemory<byte>,
                    MemoryByteOrder>(
                    rows, accessor, column, direction,
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
        PocoColumn<T>? column,
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

        // Every row's key, on every build: read unboxed where the column can (design 69 §5).
        var typed = column?.Reader<TValue>();

        for (var i = firstNonNull + 1;
             i < count;
             i++)
        {
            if (!ReadKey(rows, i, typed, accessor, out TValue value))
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
                    typed,
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
        PocoColumn<T>? column,
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

        // Every row's key, on every build: read unboxed where the column can (design 69 §5).
        var typed = column?.Reader<TSymbol>();

        for (var i = firstNonNull + 1;
             i < rowCount;
             i++)
        {
            if (!ReadKey(rows, i, typed, accessor, out TSymbol value))
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
                    typed,
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
        PocoColumn<T>? column,
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

        // Every row's key, on every build: read unboxed where the column can (design 69 §5).
        var typed = column?.Reader<TValue>();

        for (var i = firstNonNull + 1;
             i < rows.Count;
             i++)
        {
            if (!ReadKey(rows, i, typed, accessor, out TValue value))
            {
                nulls ??=
                    new bool[rows.Count];

                nulls[i] = true;
                continue;
            }

            values[i] = value;
        }

        searchKey =
            SearchKeyFactory<
                    TValue,
                    TOrder>
                .Create(
                    accessor,
                    typed,
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
            TextOrder.Compare(
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

        /// <summary>Whether a seek's bound is compared without boxing: a fixed value always is, as it was boxed once.</summary>
        bool CanSeek(in SeekBound bound) =>
            bound.IsFixed;

        /// <summary>The row's key compared with a seek's bound for the left row <paramref name="laneRow"/>.</summary>
        int Compare(
            T row,
            in SeekBound bound,
            int laneRow) =>
            bound.IsFixed
                ? Compare(
                    row,
                    bound.Value)
                : throw new InvalidOperationException(
                    "a key with no typed reader was asked to compare a column's value.");
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
            PocoValueReader<T, TValue>? typed,
            SortDirection direction) =>
            new SearchKey<
                TValue,
                TOrder>(
                accessor,
                typed,
                direction);
    }

    /// <summary>
    /// One key column compared with a bound. A column with a typed reader is read through it, as the
    /// index's keys were built (design 69 §5), so a search boxes nothing; any other is read boxed.
    /// </summary>
    private sealed class SearchKey<TValue, TOrder>(
        Func<T, object?> accessor,
        PocoValueReader<T, TValue>? typed,
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
            if (typed is not null)
            {
                return CompareTyped(
                    row,
                    bound);
            }

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

        private int CompareTyped(
            T row,
            object? bound)
        {
            if (!typed!(row, out var value))
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

            var comparison =
                CompareBound<TValue, TOrder>(
                    value,
                    bound);

            return _descending
                ? -comparison
                : comparison;
        }

        public bool CanSeek(in SeekBound bound) =>
            bound.IsFixed ||
            (typed is not null &&
             LaneHolds<TValue>(bound.Type));

        public int Compare(
            T row,
            in SeekBound bound,
            int laneRow)
        {
            if (bound.IsFixed)
            {
                return Compare(
                    row,
                    bound.Value);
            }

            var given =
                bound.Column.IsValid(laneRow);

            if (!typed!(row, out var value))
            {
                if (!given)
                    return 0;

                return _nullsFirst
                    ? -1
                    : 1;
            }

            if (!given)
            {
                return _nullsFirst
                    ? 1
                    : -1;
            }

            var comparison =
                CompareLane<TValue, TOrder>(
                    value,
                    bound.Column,
                    laneRow);

            return _descending
                ? -comparison
                : comparison;
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

    /// <summary>
    /// Whether a column of <paramref name="type"/> holds <typeparamref name="TValue"/> as its lanes, as a
    /// range bound read from it would: the types a seek compares straight from the column.
    /// </summary>
    private static bool LaneHolds<TValue>(
        ChalkType type) =>
        type.Kind switch
        {
            Chalk.Ir.TypeKind.I64 or Chalk.Ir.TypeKind.Time or Chalk.Ir.TypeKind.Timestamp
                or Chalk.Ir.TypeKind.TimestampTz or Chalk.Ir.TypeKind.IntervalDay =>
                typeof(TValue) == typeof(long),
            Chalk.Ir.TypeKind.I32 or Chalk.Ir.TypeKind.Date or Chalk.Ir.TypeKind.IntervalYear =>
                typeof(TValue) == typeof(int),
            Chalk.Ir.TypeKind.I16 => typeof(TValue) == typeof(short),
            Chalk.Ir.TypeKind.I8 => typeof(TValue) == typeof(sbyte),
            Chalk.Ir.TypeKind.Fp64 => typeof(TValue) == typeof(double),
            Chalk.Ir.TypeKind.Fp32 => typeof(TValue) == typeof(float),
            Chalk.Ir.TypeKind.String => typeof(TValue) == typeof(string),
            _ => false,
        };

    /// <summary>
    /// A key compared with a column's lane as <see cref="CompareBound{TValue,TOrder}"/> compares it with
    /// the value read from the lane, for the types <see cref="LaneHolds{TValue}"/> accepts.
    /// </summary>
    private static int CompareLane<
        TValue,
        TOrder>(
        TValue left,
        in ColumnView lane,
        int row)
        where TOrder :
        struct,
        IValueOrder<TValue>
    {
        if (typeof(TValue) == typeof(string))
        {
            return CompareText(
                (string)(object)left!,
                lane.VarValue(row));
        }

        if (typeof(TValue) == typeof(double))
        {
            return CompareReal(
                (double)(object)left!,
                lane.Lanes<double>()[row]);
        }

        if (typeof(TValue) == typeof(float))
        {
            return CompareReal(
                (float)(object)left!,
                lane.Lanes<float>()[row]);
        }

        if (typeof(TValue) == typeof(long))
        {
            var other = lane.Lanes<long>()[row];
            return TOrder.Compare(left, Unsafe.As<long, TValue>(ref other));
        }

        if (typeof(TValue) == typeof(int))
        {
            var other = lane.Lanes<int>()[row];
            return TOrder.Compare(left, Unsafe.As<int, TValue>(ref other));
        }

        if (typeof(TValue) == typeof(short))
        {
            var other = lane.Lanes<short>()[row];
            return TOrder.Compare(left, Unsafe.As<short, TValue>(ref other));
        }

        if (typeof(TValue) == typeof(sbyte))
        {
            var other = lane.Lanes<sbyte>()[row];
            return TOrder.Compare(left, Unsafe.As<sbyte, TValue>(ref other));
        }

        throw new InvalidOperationException(
            $"a {typeof(TValue).Name} key was asked to compare a column's lane.");
    }

    /// <summary>
    /// <see cref="TextOrder.Compare(string, string)"/> of a string and the text whose UTF-8 bytes are
    /// given, by sign, without making a string of them: the bytes are read as UTF-16 code units, and
    /// the first two that differ are ranked as the index ranks them, by code point. Comparing the units
    /// as they are would put a supplementary code point below U+E000, out of the index's order.
    /// </summary>
    private static int CompareText(
        string text,
        ReadOnlySpan<byte> utf8)
    {
        Span<char> units = stackalloc char[2];
        var at = 0;
        while (!utf8.IsEmpty)
        {
            System.Text.Rune.DecodeFromUtf8(
                utf8,
                out var rune,
                out var consumed);

            var count =
                rune.EncodeToUtf16(units);

            for (var u = 0; u < count; u++)
            {
                if (at == text.Length)
                    return -1;

                var difference =
                    TextOrder.Rank(text[at]) - TextOrder.Rank(units[u]);

                if (difference != 0)
                    return difference;

                at++;
            }

            utf8 = utf8[consumed..];
        }

        return at == text.Length
            ? 0
            : 1;
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
                    TextOrder.Compare(
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

    /// <summary>
    /// A range's bound over the first <see cref="Count"/> key columns: values a range holds, or a seek's
    /// bounds read from their columns at one left row; with a NULL after them, where a prefix needs one.
    /// </summary>
    private readonly struct BoundView
    {
        private readonly IReadOnlyList<object?>? _source;
        private readonly SeekBound[]? _seek;
        private readonly int _sourceCount;
        private readonly bool _appendNull;

        public BoundView(
            IReadOnlyList<object?> source)
            : this(
                source,
                null,
                0,
                source.Count,
                appendNull: false)
        {
        }

        public BoundView(
            SeekBound[] seek,
            int row,
            int count)
            : this(
                null,
                seek,
                row,
                count,
                appendNull: false)
        {
        }

        private BoundView(
            IReadOnlyList<object?>? source,
            SeekBound[]? seek,
            int row,
            int sourceCount,
            bool appendNull)
        {
            _source = source;
            _seek = seek;
            Row = row;
            _sourceCount = sourceCount;
            _appendNull = appendNull;
        }

        public int Count =>
            _sourceCount +
            (_appendNull ? 1 : 0);

        /// <summary>The left row a seek's bounds are read at.</summary>
        public int Row { get; }

        public object? this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get =>
                index < _sourceCount && _source is not null
                    ? _source[index]
                    : null;
        }

        /// <summary>Whether key column <paramref name="index"/> is bounded by a seek's bound rather than a value.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsTypedAt(int index) =>
            _seek is not null &&
            index < _sourceCount;

        public ref readonly SeekBound Seek(int index) =>
            ref _seek![index];

        public BoundView AppendNull() =>
            new(
                _source,
                _seek,
                Row,
                _sourceCount,
                appendNull: true);

        /// <summary>The bound over its first <paramref name="count"/> columns only.</summary>
        public BoundView Prefix(int count) =>
            count == 0
                ? default
                : new(
                    _source,
                    _seek,
                    Row,
                    count,
                    appendNull: false);
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
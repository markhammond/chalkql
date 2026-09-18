using Chalk.Catalog;

namespace Chalk.Sources.Poco;

/// <summary>
/// An ordered index that also holds its own copy of the table's columns, materialised in the index's
/// key order at <c>Build()</c> (D257, <c>docs/design/34-clustered-indexes.md</c>).
/// </summary>
/// <remarks>
/// <para>
/// The key half is a <see cref="PermutationIndex{T}"/> — the same sort, the same binary search, the
/// same uniqueness check and the same exact prefix cardinalities — so every existing path works
/// unchanged: this is an <see cref="IPositionalPocoIndex{T}"/> and a lookup that reaches outside the
/// covering set gathers through it exactly as it does for a permutation index.
/// </para>
/// <para>
/// What is new is the copy. Every covered column is extracted a second time, in permutation order,
/// through the same compiled chunk writers the base table's columns go through, and the result is
/// held in Chalk-owned arrays for the life of the index. A lookup whose projection lies inside the
/// covering set is then a <em>slice</em> of those arrays — no gather, no per-row allocation, and the
/// same zero-copy publish a scan of the base table makes.
/// </para>
/// <para>
/// The copy costs what it says it costs: <see cref="CopyBytes"/> is the whole of it, for a host that
/// budgets memory. It is built wherever the permutation is built, which for a POCO table is
/// <c>Build()</c> — the permutation is never rebuilt in place, and a collection swapped behind a
/// <c>Func</c> registration is refused by the same staleness check that has always refused it.
/// </para>
/// </remarks>
public sealed class ClusteredIndex<T> : IPositionalPocoIndex<T>
{
    private readonly PermutationIndex<T> _keys;
    private readonly ClusteredColumnCopy _copy;

    internal ClusteredIndex(
        IndexDescriptor descriptor,
        IReadOnlyList<T> rows,
        Func<T, object?>[] keys,
        bool collationBacked,
        string table,
        IReadOnlyList<string> keyColumnNames,
        PermutationIndexScratch scratch,
        PocoColumn<T>[] columns)
        : this(descriptor, rows, keys, collationBacked, table, keyColumnNames, scratch, columns,
            previous: null, appendedFrom: 0)
    {
    }

    /// <summary>
    /// The same, extending <paramref name="previous"/> (D260 §3). The key half extends exactly as a
    /// permutation index does; the copy extends with it when the appended keys arrived in key order,
    /// because the rows already in it are then still where they were, and is extracted again when
    /// they did not, because then they are not.
    /// </summary>
    internal ClusteredIndex(
        IndexDescriptor descriptor,
        IReadOnlyList<T> rows,
        Func<T, object?>[] keys,
        bool collationBacked,
        string table,
        IReadOnlyList<string> keyColumnNames,
        PermutationIndexScratch scratch,
        PocoColumn<T>[] columns,
        ClusteredIndex<T>? previous,
        int appendedFrom)
    {
        _keys = new PermutationIndex<T>(
            descriptor, rows, keys, collationBacked, table, keyColumnNames, scratch,
            previous?._keys, appendedFrom);

        var covered = CoveredColumns(descriptor, columns.Length);
        _copy = previous is not null && appendedFrom > 0 && _keys.ExtendedInOrder
            ? ClusteredColumnCopy.Append(
                columns, covered, rows, _keys.Permutation, previous._copy, appendedFrom)
            : ClusteredColumnCopy.Extract(columns, covered, rows, _keys.Permutation);
    }

    /// <inheritdoc />
    public IndexDescriptor Descriptor =>
        _keys.Descriptor;

    /// <summary>
    /// Every byte the second column set holds: the values, the offsets and the validity bitmaps of
    /// every covered column, over every row. Reported for a host that budgets memory; nothing in
    /// Chalk reads it.
    /// </summary>
    public long CopyBytes =>
        _copy.Bytes;

    /// <summary>The table columns the copy carries, ascending. Always a superset of the key.</summary>
    public IReadOnlyList<int> Covering =>
        _copy.Columns;

    /// <inheritdoc />
    /// <remarks>
    /// The permutation's one int per row, plus the copy spread over the rows it holds. A host reading
    /// this for a build report gets what the index really costs, not what the key half of it costs.
    /// </remarks>
    public long BytesPerRow =>
        _keys.RowCount == 0
            ? _keys.BytesPerRow
            : _keys.BytesPerRow + (_copy.Bytes / _keys.RowCount);

    /// <inheritdoc />
    public long? DistinctCount(int keyPositions) =>
        _keys.DistinctCount(keyPositions);

    /// <inheritdoc />
    public IEnumerable<T> Lookup(IndexKeyRange range) =>
        _keys.Lookup(range);

    /// <inheritdoc />
    public IEnumerable<int> LookupPositions(IndexKeyRange range) =>
        _keys.LookupPositions(range);

    /// <inheritdoc />
    public void GetWindow(IndexKeyRange range, out int from, out int to) =>
        _keys.GetWindow(range, out from, out to);

    /// <inheritdoc />
    /// <remarks>
    /// Which is also how a copy position becomes a row of the registered collection: the copy is in
    /// index order, so its ordinal <em>is</em> the index ordinal, and anything that needs the row
    /// itself maps through here.
    /// </remarks>
    public int GetPosition(int ordinal) =>
        _keys.GetPosition(ordinal);

    /// <summary>Whether the rows this index was built over are still the ones the table reports.</summary>
    internal bool Describes(IReadOnlyList<T> rows) =>
        _keys.Describes(rows);

    /// <inheritdoc cref="PermutationIndex{T}.ExtendedInOrder"/>
    internal bool ExtendedInOrder =>
        _keys.ExtendedInOrder;

    /// <summary>
    /// Whether the copy can answer <paramref name="projection"/> by itself, which is the same
    /// question the planner priced the lookup on.
    /// </summary>
    internal bool Covers(IReadOnlyList<int> projection) =>
        Descriptor.Covers(projection);

    /// <summary>
    /// One covered column over the half-open window <c>[from, from + count)</c> of the copy, as a
    /// view that borrows the copy's arrays. Nothing is copied and nothing is gathered.
    /// </summary>
    internal ColumnView Slice(int tableColumn, int from, int count) =>
        _copy.Slice(tableColumn, from, count);

    /// <summary>
    /// The descriptor's covering set as column indexes, with the empty set — "every column" — spelled
    /// out, because the copy has to know which columns to extract.
    /// </summary>
    private static int[] CoveredColumns(IndexDescriptor descriptor, int columnCount)
    {
        if (descriptor.Covering.Count > 0)
        {
            return [.. descriptor.Covering];
        }

        var all = new int[columnCount];
        for (var i = 0; i < all.Length; i++)
        {
            all[i] = i;
        }

        return all;
    }
}

/// <summary>
/// The second column set: every covered column of a table, materialised once in an index's key order
/// and held in arrays Chalk owns (D257, <c>34-clustered-indexes.md</c> §1).
/// </summary>
/// <remarks>
/// <para>
/// Extraction runs the table's own compiled chunk writers over the permutation — the same code, the
/// same encodings, the same Arrow layout the base columns are published in — and then takes the
/// buffers the writer staged and copies them into arrays of this object's own. The writer's staging
/// goes back to a build-time arena that is disposed the moment the last column is out, so the only
/// memory that survives is the copy itself.
/// </para>
/// <para>
/// A slice is a <see cref="ColumnView"/> over those arrays with an offset: every kind Chalk stores is
/// sliceable that way — a fixed-width column by its lanes, a variable-length one through its offsets,
/// a validity bitmap by the bit index the view's offset already carries. The views carry no generation
/// and no owner, because nothing ever reuses these arrays: they are valid for the life of the index.
/// </para>
/// </remarks>
internal sealed class ClusteredColumnCopy
{
    private readonly int[] _slotByColumn;
    private readonly ColumnView[] _views;

    private ClusteredColumnCopy(int[] slotByColumn, int[] columns, ColumnView[] views, long bytes)
    {
        _slotByColumn = slotByColumn;
        _views = views;
        Columns = columns;
        Bytes = bytes;
    }

    /// <summary>The table columns the copy carries, ascending.</summary>
    public IReadOnlyList<int> Columns { get; }

    /// <summary>Every byte the copy holds.</summary>
    public long Bytes { get; }

    /// <summary>
    /// Extracts <paramref name="covered"/> in the order <paramref name="permutation"/> gives, or in
    /// the collection's own order when the index is collation-backed and there is no permutation.
    /// </summary>
    public static ClusteredColumnCopy Extract<T>(
        PocoColumn<T>[] columns,
        int[] covered,
        IReadOnlyList<T> rows,
        int[]? permutation)
    {
        var slotByColumn = new int[columns.Length];
        Array.Fill(slotByColumn, -1);

        var views = new ColumnView[covered.Length];
        var rowCount = rows.Count;
        long bytes = 0;

        // One arena for the whole extraction, disposed when the last column is out: the writers'
        // staging is rented from it and the copy is taken out of it, so nothing of the build
        // survives but the copy. A writer is released before the next is acquired, so the transient
        // peak is one column's staging rather than every column's.
        using var arena = new ExecutionArena();
        for (var i = 0; i < covered.Length; i++)
        {
            var column = covered[i];
            slotByColumn[column] = i;

            if (rowCount == 0)
            {
                views[i] = ColumnView.Empty(columns[column].Type);
                continue;
            }

            var writer = columns[column].AcquireWriter(arena, rowCount);
            try
            {
                var staged = permutation is null
                    ? writer.WriteView(rows, 0, rowCount)
                    : writer.WriteView(rows, permutation, 0, rowCount);
                views[i] = Own(staged, ref bytes);
            }
            finally
            {
                columns[column].ReleaseWriter(writer);
            }
        }

        return new ClusteredColumnCopy(slotByColumn, covered, views, bytes);
    }

    /// <summary>
    /// <paramref name="previous"/> with the rows from <paramref name="appendedFrom"/> on added to
    /// the end of every covered column (D260 §3). Only the appended rows go through the writers; the
    /// columns already in the copy are concatenated with what they produce, which is a copy of two
    /// buffers rather than an extraction of every row again.
    /// </summary>
    /// <remarks>
    /// The caller has established that the appended keys arrived in key order, so the copy's
    /// existing rows are still at the ordinals they were at and the new ones belong after them. A
    /// column whose layout cannot be concatenated — a LIST, whose element column would have to be
    /// concatenated and its offsets rebased through two levels — falls back to extracting that
    /// column again, which is correct however the rows arrived.
    /// </remarks>
    public static ClusteredColumnCopy Append<T>(
        PocoColumn<T>[] columns,
        int[] covered,
        IReadOnlyList<T> rows,
        int[]? permutation,
        ClusteredColumnCopy previous,
        int appendedFrom)
    {
        var slotByColumn = new int[columns.Length];
        Array.Fill(slotByColumn, -1);

        var views = new ColumnView[covered.Length];
        var added = rows.Count - appendedFrom;
        long bytes = 0;

        using var arena = new ExecutionArena();
        for (var i = 0; i < covered.Length; i++)
        {
            var column = covered[i];
            slotByColumn[column] = i;

            var writer = columns[column].AcquireWriter(arena, Math.Max(added, 1));
            try
            {
                var staged = permutation is null
                    ? writer.WriteView(rows, appendedFrom, added)
                    : writer.WriteView(rows, permutation, appendedFrom, added);

                if (TryConcat(previous._views[i], staged, ref bytes, out views[i]))
                {
                    continue;
                }
            }
            finally
            {
                columns[column].ReleaseWriter(writer);
            }

            // The layout does not concatenate: extract this column over every row instead, which is
            // what a copy built from scratch would have held anyway.
            views[i] = ExtractOne(columns[column], rows, permutation, arena, ref bytes);
        }

        return new ClusteredColumnCopy(slotByColumn, covered, views, bytes);
    }

    /// <summary>One covered column over every row, as <see cref="Extract{T}"/> would have built it.</summary>
    private static ColumnView ExtractOne<T>(
        PocoColumn<T> column,
        IReadOnlyList<T> rows,
        int[]? permutation,
        ExecutionArena arena,
        ref long bytes)
    {
        var rowCount = rows.Count;
        if (rowCount == 0)
        {
            return ColumnView.Empty(column.Type);
        }

        var writer = column.AcquireWriter(arena, rowCount);
        try
        {
            var staged = permutation is null
                ? writer.WriteView(rows, 0, rowCount)
                : writer.WriteView(rows, permutation, 0, rowCount);
            return Own(staged, ref bytes);
        }
        finally
        {
            column.ReleaseWriter(writer);
        }
    }

    /// <summary>
    /// <paramref name="left"/> — a column this copy already owns — followed by <paramref name="right"/>,
    /// the same column over the appended rows as the writer just staged it. False when the layout is
    /// one this does not join, and the caller extracts the column again instead.
    /// </summary>
    private static bool TryConcat(
        in ColumnView left, in ColumnView right, ref long bytes, out ColumnView result)
    {
        result = default;

        // A LIST's element column would have to be concatenated too and both levels of offsets
        // rebased; a sliced view would have to be normalised first. Neither is built.
        if (left.Children is { Length: > 0 } || right.Children is { Length: > 0 }
            || left.Offset != 0 || right.Offset != 0
            || left.IsUtf8View != right.IsUtf8View
            || left.BitPacked != right.BitPacked
            || left.Offsets.IsEmpty != right.Offsets.IsEmpty)
        {
            return false;
        }

        var n = left.Length;
        var m = right.Length;

        if (m == 0)
        {
            bytes += Held(left);
            result = left;
            return true;
        }

        if (n == 0)
        {
            result = Own(right, ref bytes);
            return true;
        }

        var total = n + m;
        ReadOnlyMemory<byte> values;
        ReadOnlyMemory<byte> offsets = default;
        ReadOnlyMemory<byte>[]? viewBuffers = null;
        var viewBufferCount = 0;

        if (left.IsUtf8View)
        {
            if (!TryConcatViews(left, right, n, m, ref bytes, out values, out viewBuffers, out viewBufferCount))
            {
                return false;
            }
        }
        else if (!left.Offsets.IsEmpty)
        {
            if (!TryConcatVariable(left, right, n, m, ref bytes, out values, out offsets))
            {
                return false;
            }
        }
        else if (left.BitPacked)
        {
            var packed = new byte[BitmapBytes(total)];
            CopyBits(left.Values.Span, packed, 0, n);
            CopyBits(right.Values.Span, packed, n, m);
            bytes += packed.Length;
            values = packed;
        }
        else
        {
            var width = left.Values.Length / n;
            if (width == 0 || left.Values.Length != n * width || right.Values.Length != m * width)
            {
                return false;
            }

            var lanes = new byte[total * width];
            left.Values.Span.CopyTo(lanes);
            right.Values.Span.CopyTo(lanes.AsSpan(n * width));
            bytes += lanes.Length;
            values = lanes;
        }

        ReadOnlyMemory<byte> validity = default;
        var nullCount = 0;
        if (!left.Validity.IsEmpty || !right.Validity.IsEmpty)
        {
            var bits = new byte[BitmapBytes(total)];
            Fill(left.Validity.Span, bits, 0, n);
            Fill(right.Validity.Span, bits, n, m);
            bytes += bits.Length;
            validity = bits;
            nullCount = left.NullCount >= 0 && right.NullCount >= 0
                ? left.NullCount + right.NullCount
                : -1;
        }

        result = new ColumnView
        {
            Type = left.Type,
            Length = total,
            Values = values,
            Validity = validity,
            Offsets = offsets,
            ViewBuffers = viewBuffers,
            ViewBufferCount = viewBufferCount,
            NullCount = nullCount,
            Offset = 0,
            BitPacked = left.BitPacked,
        };

        return true;
    }

    /// <summary>
    /// STRING_VIEW: the 16-byte lanes end to end, and the variadic buffers likewise — a lane that is
    /// not inline names one by index, so the appended lanes' indexes shift by however many buffers
    /// the existing column has.
    /// </summary>
    private static bool TryConcatViews(
        in ColumnView left,
        in ColumnView right,
        int n,
        int m,
        ref long bytes,
        out ReadOnlyMemory<byte> values,
        out ReadOnlyMemory<byte>[]? viewBuffers,
        out int viewBufferCount)
    {
        values = default;
        viewBuffers = null;
        viewBufferCount = 0;

        const int Width = StringViewLayout.Width;
        const int InlineLimit = 12;

        if (left.Values.Length < n * Width || right.Values.Length < m * Width)
        {
            return false;
        }

        var lanes = new byte[(n + m) * Width];
        left.Values.Span[..(n * Width)].CopyTo(lanes);
        right.Values.Span[..(m * Width)].CopyTo(lanes.AsSpan(n * Width));

        viewBufferCount = left.ViewBufferCount + right.ViewBufferCount;
        viewBuffers = new ReadOnlyMemory<byte>[Math.Max(viewBufferCount, 1)];
        for (var i = 0; i < left.ViewBufferCount; i++)
        {
            viewBuffers[i] = left.ViewBuffers![i];
            bytes += viewBuffers[i].Length;
        }

        for (var i = 0; i < right.ViewBufferCount; i++)
        {
            viewBuffers[left.ViewBufferCount + i] = Copy(right.ViewBuffers![i], ref bytes);
        }

        if (left.ViewBufferCount > 0)
        {
            var lanesAsInts = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, int>(lanes.AsSpan());
            for (var i = 0; i < m; i++)
            {
                var lane = ((n + i) * Width) / sizeof(int);
                if (lanesAsInts[lane] > InlineLimit)
                {
                    lanesAsInts[lane + 2] += left.ViewBufferCount;
                }
            }
        }

        bytes += lanes.Length;
        values = lanes;
        return true;
    }

    /// <summary>
    /// A classic variable-length column: the data buffers end to end, and the appended offsets
    /// rebased by how many bytes the existing data holds.
    /// </summary>
    private static bool TryConcatVariable(
        in ColumnView left,
        in ColumnView right,
        int n,
        int m,
        ref long bytes,
        out ReadOnlyMemory<byte> values,
        out ReadOnlyMemory<byte> offsets)
    {
        values = default;
        offsets = default;

        if (left.Offsets.Length < (n + 1) * sizeof(int) || right.Offsets.Length < (m + 1) * sizeof(int))
        {
            return false;
        }

        var leftOffsets = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, int>(left.Offsets.Span);
        var rightOffsets = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, int>(right.Offsets.Span);
        var leftData = leftOffsets[n];
        var rightData = rightOffsets[m];

        if (left.Values.Length < leftData || right.Values.Length < rightData)
        {
            return false;
        }

        var merged = new byte[(n + m + 1) * sizeof(int)];
        var lanes = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, int>(merged.AsSpan());
        leftOffsets[..(n + 1)].CopyTo(lanes);
        for (var i = 1; i <= m; i++)
        {
            lanes[n + i] = leftData + rightOffsets[i];
        }

        var data = new byte[leftData + rightData];
        left.Values.Span[..leftData].CopyTo(data);
        right.Values.Span[..rightData].CopyTo(data.AsSpan(leftData));

        bytes += merged.Length + data.Length;
        offsets = merged;
        values = data;
        return true;
    }

    private static int BitmapBytes(int rows) => ((rows + 63) / 64) * 8;

    /// <summary>
    /// <paramref name="count"/> bits of <paramref name="source"/> into <paramref name="target"/> at
    /// <paramref name="at"/>. Whole bytes are copied whole when the two are aligned, which is the
    /// case for the existing column: only the appended tail is ever written bit by bit.
    /// </summary>
    private static void CopyBits(ReadOnlySpan<byte> source, byte[] target, int at, int count)
    {
        if (at % 8 == 0)
        {
            var whole = count / 8;
            source[..whole].CopyTo(target.AsSpan(at / 8));
            for (var i = whole * 8; i < count; i++)
            {
                if (Apache.Arrow.BitUtility.GetBit(source, i))
                {
                    Apache.Arrow.BitUtility.SetBit(target, at + i);
                }
            }

            return;
        }

        for (var i = 0; i < count; i++)
        {
            if (Apache.Arrow.BitUtility.GetBit(source, i))
            {
                Apache.Arrow.BitUtility.SetBit(target, at + i);
            }
        }
    }

    /// <summary>The same, where an absent bitmap means every one of those rows holds a value.</summary>
    private static void Fill(ReadOnlySpan<byte> source, byte[] target, int at, int count)
    {
        if (!source.IsEmpty)
        {
            CopyBits(source, target, at, count);
            return;
        }

        for (var i = 0; i < count; i++)
        {
            Apache.Arrow.BitUtility.SetBit(target, at + i);
        }
    }

    /// <summary>Every byte a view already owns, for the running total of what the copy holds.</summary>
    private static long Held(in ColumnView view)
    {
        long held = view.Values.Length + view.Validity.Length + view.Offsets.Length;
        for (var i = 0; i < view.ViewBufferCount; i++)
        {
            held += view.ViewBuffers![i].Length;
        }

        return held;
    }

    /// <summary>
    /// One covered column over <c>[from, from + count)</c>. A slice of the whole-column view: the
    /// buffers are shared and the offset does the work, which is exactly how Arrow slices an array.
    /// </summary>
    public ColumnView Slice(int tableColumn, int from, int count)
    {
        var slot = _slotByColumn[tableColumn];
        if (slot < 0)
        {
            throw new InvalidOperationException(
                $"column {tableColumn} is not in this clustered index's covering set.");
        }

        var whole = _views[slot];
        return new ColumnView
        {
            Type = whole.Type,
            Length = count,
            Values = whole.Values,
            Validity = whole.Validity,
            Offsets = whole.Offsets,
            ViewBuffers = whole.ViewBuffers,
            ViewBufferCount = whole.ViewBufferCount,

            // A column with no NULL at all has no bitmap, so every slice of it has none either;
            // otherwise nobody counted this window's and -1 is the honest answer.
            NullCount = whole.NullCount == 0 ? 0 : -1,
            Offset = from,
            BitPacked = whole.BitPacked,
            Children = whole.Children,
        };
    }

    /// <summary>
    /// The staged view with every buffer copied into an array this object owns. The writer's staging
    /// is an arena rental that is about to go back; a view of the copy has to outlive the arena.
    /// </summary>
    /// <remarks>
    /// A staged view always starts at offset zero and its buffers are exactly as long as the rows
    /// they hold, so the copy is the buffers as they stand — no length arithmetic per kind, and
    /// therefore nothing to get wrong for a kind this code has never met. A LIST's element column is
    /// copied the same way; slicing the parent leaves the child whole, which is what a sliced Arrow
    /// list array is.
    /// </remarks>
    private static ColumnView Own(in ColumnView staged, ref long bytes)
    {
        var values = Copy(staged.Values, ref bytes);
        var validity = Copy(staged.Validity, ref bytes);
        var offsets = Copy(staged.Offsets, ref bytes);

        ReadOnlyMemory<byte>[]? viewBuffers = null;
        if (staged.ViewBuffers is { } buffers)
        {
            viewBuffers = new ReadOnlyMemory<byte>[buffers.Length];
            for (var i = 0; i < buffers.Length; i++)
            {
                viewBuffers[i] = i < staged.ViewBufferCount
                    ? Copy(buffers[i], ref bytes)
                    : default;
            }
        }

        ColumnView[]? children = null;
        if (staged.Children is { Length: > 0 } elements)
        {
            children = new ColumnView[elements.Length];
            for (var i = 0; i < elements.Length; i++)
            {
                children[i] = Own(elements[i], ref bytes);
            }
        }

        return new ColumnView
        {
            Type = staged.Type,
            Length = staged.Length,
            Values = values,
            Validity = validity,
            Offsets = offsets,
            ViewBuffers = viewBuffers,
            ViewBufferCount = staged.ViewBufferCount,
            NullCount = staged.NullCount,
            Offset = staged.Offset,
            BitPacked = staged.BitPacked,
            Children = children,

            // Deliberately absent: Generation, Owner and ManagedPublisher. Nothing reuses these
            // arrays and nothing may publish them as its own, so a view of the copy never goes stale.
        };
    }

    private static ReadOnlyMemory<byte> Copy(ReadOnlyMemory<byte> source, ref long bytes)
    {
        if (source.Length == 0)
        {
            return default;
        }

        bytes += source.Length;
        return source.ToArray();
    }
}

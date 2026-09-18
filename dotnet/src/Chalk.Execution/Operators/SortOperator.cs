using System.Numerics;
using Chalk.Catalog;
using Chalk.Execution.Vectors;
using Chalk.Sources;
using Array = System.Array;
using ArrowSchema = Apache.Arrow.Schema;

namespace Chalk.Execution.Operators;

/// <summary>
/// A blocking sort (§6.3): the input is concatenated into one table, an <c>int[]</c> permutation is
/// sorted by an encoded composite key, and the rows are gathered back out in batches. Not stable —
/// <c>02-ir.md</c> §4 does not promise stability, and the key paths below could not keep it anyway.
/// </summary>
/// <remarks>
/// <para>
/// The ordering reaches the sort as one fixed-width word per row rather than as a comparer
/// (<see cref="SortKeyComposite"/>, design 41 §2): the keys are encoded most-significant first and
/// the permutation sorts against those words with no call per comparison. The comparer is still
/// here, and still correct, for the two cases the encoding cannot serve: an ordering whose first key
/// has no order-preserving code, and a run of rows whose composite is equal because it is a prefix
/// rather than the whole ordering.
/// </para>
/// <para>
/// The concatenation is not one buffer per column but a <b>chunk</b> of rows at a time (design 41
/// §3, D267 c), each chunk's rental small enough for the arena to keep between executions: one
/// buffer for a whole input is above any sane retention budget, so it is a fresh heap array every
/// time and its size class is never reused. A row's chunk and offset are a shift apart, and a sort
/// whose input fits one chunk is exactly what it was before.
/// </para>
/// </remarks>
internal sealed class SortOperator : OperatorBase
{
    /// <summary>
    /// What one column of one chunk may cost. Small enough that the arena pools and retains it — the
    /// default retention holds sixteen of these — and large enough that the per-chunk bookkeeping is
    /// nothing against the rows it covers.
    /// </summary>
    private const int DefaultChunkBytes = 4 << 20;

    private static readonly AsyncLocal<int?> ChunkBytesOverride = new();

    /// <summary>A variable-length column's assumed width when the chunk is sized: its view lane.</summary>
    private const int VariableWidth = 16;

    private readonly IBatchOperator _input;
    private readonly SortOrdering _ordering;
    private readonly ChalkType[] _columnTypes;
    private readonly ColumnCopier[] _copiers;
    private readonly ColumnCopier[] _gathers;
    private readonly ColumnarBatch _output;
    private readonly SortKeyComposite _composite;

    /// <summary>One view per column per chunk, by <c>chunk * columns + column</c>.</summary>
    private ColumnView[] _chunkViews;

    /// <summary>The copiers of chunks after the first, which this operator acquires and releases itself.</summary>
    private ColumnCopier[][] _extraChunks = [];

    /// <summary>A batch's rows regrouped by chunk, and where each one landed (§3).</summary>
    private int[] _localRows = [];
    private int[] _scratchRows = [];
    private int[] _chunkCursor = [];
    private ColumnCopier[] _scratch = [];

    private int _chunkCount;
    private int _chunkShift;

    /// <summary>The plan's estimate of the input's rows, which is what this sort concatenates (D258.3).</summary>
    private readonly double _estimatedRows;

    public SortOperator(
        OperatorContext context,
        string path,
        ArrowSchema schema,
        IReadOnlyList<ChalkType> columnTypes,
        IBatchOperator input,
        SortOrdering ordering,
        double estimatedRows = 0)
        : base(context, schema, columnTypes, path)
    {
        _input = input;
        _ordering = ordering;
        _estimatedRows = estimatedRows;
        _columnTypes = [.. columnTypes];
        _copiers = [.. columnTypes.Select(t => new ColumnCopier(t))];
        _gathers = [.. columnTypes.Select(t => new ColumnCopier(t))];
        _output = NewOutput();
        _chunkViews = new ColumnView[columnTypes.Count];
        _composite = new SortKeyComposite(ordering, _columnTypes);
    }

    /// <summary>
    /// A test hook: what one column of one chunk may cost, so that a test can reach the chunked
    /// path over a few hundred rows instead of a few hundred thousand. <see cref="AsyncLocal{T}"/>
    /// for the reason <c>SortKeyComposite.MaxBits</c> is one.
    /// </summary>
    internal static int ChunkBytes
    {
        get => ChunkBytesOverride.Value ?? DefaultChunkBytes;
        set => ChunkBytesOverride.Value = value;
    }

    private int Columns => _columnTypes.Length;

    protected override ValueTask DisposeCoreAsync() => _input.DisposeAsync();

    protected override async IAsyncEnumerable<ColumnarBatch> RunAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var arena = Context.Arena;
        var permutation = System.Array.Empty<int>();
        try
        {
            // Inside the try: concatenation is where a MaxBytes budget usually runs out, and the
            // columns it did build are arena memory that has to go back (§2).
            var rows = await ConcatenateAsync(ct).ConfigureAwait(false);
            if (rows == 0)
            {
                yield break;
            }

            var table = SortTable.Of(_chunkViews, Columns, _chunkShift, _chunkCount, rows);

            // The permutation is the one array that scales with the whole input rather than with a
            // batch, so it is the one a MaxBytes budget is most likely to refuse (ADR 0012).
            permutation = arena.Rent<int>(rows);
            for (var i = 0; i < rows; i++)
            {
                permutation[i] = i;
            }

            Sort(in table, permutation, arena);

            var batchSize = Context.Settings.BatchSize;
            for (var start = 0; start < rows; start += batchSize)
            {
                ct.ThrowIfCancellationRequested();
                var count = Math.Min(batchSize, rows - start);
                _output.Begin(count);
                if (_chunkCount == 1)
                {
                    for (var c = 0; c < Columns; c++)
                    {
                        _output.Set(
                            c, _gathers[c].Gather(_chunkViews[c], permutation.AsSpan(start, count)));
                    }
                }
                else
                {
                    GatherChunked(permutation.AsSpan(start, count), arena);
                }

                yield return _output;
            }
        }
        finally
        {
            arena.Return(permutation);
            _composite.Release(arena);
            ReleaseChunks(arena);
        }
    }

    /// <summary>
    /// Reads the whole input into chunks of <see cref="ChunkBytes"/> a column and returns how many
    /// rows they hold. Every chunk but the last is exactly <c>1 &lt;&lt; _chunkShift</c> rows, which
    /// is what makes a row's chunk a shift of its index.
    /// </summary>
    private async ValueTask<int> ConcatenateAsync(CancellationToken ct)
    {
        // The plan says how many rows are coming, so the first chunk starts there rather than
        // doubling a batch at a time into it (D258.3). A wrong estimate costs one more chunk.
        var capacity = PlanCapacity.Rows(Context, _estimatedRows, _columnTypes);
        _chunkShift = ChunkShift();
        var chunkRows = 1 << _chunkShift;
        _chunkCount = 0;

        // The estimate is what the first chunk's buffers are rented at, so a sort that fits one
        // chunk rents each buffer once and a sort that does not still starts at the chunk's size
        // (D258.3). No estimate leaves the growth exactly where it was: a batch, then doubling.
        var copiers = BeginChunk(capacity > 0 ? Math.Min(capacity, chunkRows) : 0);

        var rows = 0;
        var inChunk = 0;
        await foreach (var batch in _input.ExecuteAsync(ct))
        {
            var taken = 0;
            while (taken < batch.Count)
            {
                if (inChunk == chunkRows)
                {
                    FinishChunk(copiers);
                    copiers = BeginChunk(chunkRows);
                    inChunk = 0;
                }

                var take = Math.Min(chunkRows - inChunk, batch.Count - taken);
                for (var c = 0; c < copiers.Length; c++)
                {
                    AppendRange(copiers[c], batch, c, taken, take);
                }

                inChunk += take;
                taken += take;
                rows += take;
            }
        }

        FinishChunk(copiers);
        return rows;
    }

    /// <summary>Appends <paramref name="count"/> of a batch's rows from <paramref name="from"/>.</summary>
    private static void AppendRange(
        ColumnCopier copier, ColumnarBatch batch, int column, int from, int count)
    {
        var view = batch.Column(column);
        if (batch.HasSelection)
        {
            copier.AppendGather(view, batch.Selection.Slice(from, count));
        }
        else if (from == 0 && count == batch.RowCount)
        {
            copier.Append(view, count);
        }
        else
        {
            copier.Append(view.Slice(from, count), count);
        }
    }

    /// <summary>
    /// Rows per chunk: as many as <see cref="ChunkBytes"/> buys of the widest column, as a power of
    /// two and never fewer than a batch. Deliberately not the plan's estimate — every chunk but the
    /// last is the same size, which is what makes a row's chunk a shift of its index, and an
    /// estimate that sized them would make a wrong estimate a different addressing scheme rather
    /// than one more chunk.
    /// </summary>
    private int ChunkShift()
    {
        var widest = 1;
        foreach (var type in _columnTypes)
        {
            var width = ColumnKinds.Width(ColumnKinds.Of(type));
            widest = Math.Max(widest, width == 0 ? VariableWidth : width);
        }

        var rows = Math.Max(Context.Settings.BatchSize, ChunkBytes / widest);
        return BitOperations.Log2(BitOperations.RoundUpToPowerOf2((uint)rows));
    }

    /// <summary>Starts the next chunk and returns the copiers that fill it.</summary>
    private ColumnCopier[] BeginChunk(int capacity)
    {
        var copiers = ChunkCopiers(_chunkCount);
        foreach (var copier in copiers)
        {
            copier.Begin(capacity);
        }

        return copiers;
    }

    private void FinishChunk(ColumnCopier[] copiers)
    {
        var at = _chunkCount * Columns;
        if (_chunkViews.Length < at + Columns)
        {
            System.Array.Resize(ref _chunkViews, Math.Max(at + Columns, _chunkViews.Length * 2));
        }

        for (var c = 0; c < Columns; c++)
        {
            _chunkViews[at + c] = copiers[c].FinishView();
        }

        _chunkCount++;
    }

    /// <summary>
    /// The copiers of one chunk. The first chunk's are the operator's own, built with the tree and
    /// released with it; the rest are made here, which is why nothing but a chunked sort pays for
    /// them.
    /// </summary>
    private ColumnCopier[] ChunkCopiers(int chunk)
    {
        if (chunk == 0)
        {
            return _copiers;
        }

        var index = chunk - 1;
        if (_extraChunks.Length <= index)
        {
            System.Array.Resize(ref _extraChunks, Math.Max(index + 1, _extraChunks.Length * 2));
        }

        if (_extraChunks[index] is { } existing)
        {
            return existing;
        }

        var copiers = new ColumnCopier[Columns];
        for (var c = 0; c < Columns; c++)
        {
            copiers[c] = new ColumnCopier(_columnTypes[c]);
            copiers[c].Acquire(Context.Arena);
        }

        _extraChunks[index] = copiers;
        return copiers;
    }

    private void ReleaseChunks(ExecutionArena arena)
    {
        foreach (var chunk in _extraChunks)
        {
            if (chunk is null)
            {
                continue;
            }

            foreach (var copier in chunk)
            {
                copier.Release();
            }
        }

        _extraChunks = [];

        foreach (var copier in _scratch)
        {
            copier.Release();
        }

        _scratch = [];

        if (_localRows.Length != 0)
        {
            arena.Return(_localRows);
            arena.Return(_scratchRows);
            arena.Return(_chunkCursor);
            _localRows = [];
            _scratchRows = [];
            _chunkCursor = [];
        }
    }

    /// <summary>
    /// One output batch gathered from several chunks. The batch's rows are regrouped by chunk first,
    /// so each chunk is read by one bulk gather rather than a row at a time, and a second gather puts
    /// them back into the permutation's order.
    /// </summary>
    private void GatherChunked(ReadOnlySpan<int> rows, ExecutionArena arena)
    {
        EnsureGatherScratch(rows.Length, arena);

        // Where each chunk's rows start, from a counting pass over the batch.
        var cursor = _chunkCursor.AsSpan(0, _chunkCount + 1);
        cursor.Clear();
        foreach (var row in rows)
        {
            cursor[(row >> _chunkShift) + 1]++;
        }

        for (var chunk = 1; chunk <= _chunkCount; chunk++)
        {
            cursor[chunk] += cursor[chunk - 1];
        }

        // Ascending over the batch, so a chunk's rows keep the order the permutation gave them.
        for (var i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            var chunk = row >> _chunkShift;
            var at = cursor[chunk]++;
            _localRows[at] = row - (chunk << _chunkShift);
            _scratchRows[i] = at;
        }

        for (var c = 0; c < Columns; c++)
        {
            var scratch = _scratch[c];
            scratch.Begin(rows.Length);
            var from = 0;
            for (var chunk = 0; chunk < _chunkCount; chunk++)
            {
                var to = cursor[chunk];
                if (to > from)
                {
                    scratch.AppendGather(
                        _chunkViews[(chunk * Columns) + c], _localRows.AsSpan(from, to - from));
                    from = to;
                }
            }

            _output.Set(
                c, _gathers[c].Gather(scratch.FinishView(), _scratchRows.AsSpan(0, rows.Length)));
        }
    }

    private void EnsureGatherScratch(int batchSize, ExecutionArena arena)
    {
        if (_localRows.Length < batchSize || _chunkCursor.Length < _chunkCount + 1)
        {
            if (_localRows.Length != 0)
            {
                arena.Return(_localRows);
                arena.Return(_scratchRows);
                arena.Return(_chunkCursor);
            }

            _localRows = arena.Rent<int>(batchSize);
            _scratchRows = arena.Rent<int>(batchSize);
            _chunkCursor = arena.Rent<int>(_chunkCount + 1);
        }

        if (_scratch.Length != 0)
        {
            return;
        }

        _scratch = new ColumnCopier[Columns];
        for (var c = 0; c < Columns; c++)
        {
            _scratch[c] = new ColumnCopier(_columnTypes[c]);
            _scratch[c].Acquire(arena);
        }
    }

    /// <summary>
    /// Sorts the first <c>table.Rows</c> entries only: the permutation comes from the arena and may
    /// be longer than the input, so every call below is the range-limited overload.
    /// </summary>
    private void Sort(in SortTable table, int[] permutation, ExecutionArena arena)
    {
        var rows = table.Rows;
        var layout = _composite.Plan(in table, arena);
        Context.Stats.RecordSortPath(
            layout.Path switch
            {
                SortKeyPath.Word64 => SortPath.Encoded64,
                SortKeyPath.Word128 => SortPath.Encoded128,
                _ => SortPath.Comparer,
            },
            layout.Path != SortKeyPath.Comparer && layout.NeedsTieBreak);

        if (layout.Path == SortKeyPath.Comparer)
        {
            RowSort.Sort(permutation.AsSpan(0, rows), new RowOrder(table, _ordering));
            return;
        }

        var high = arena.Rent<ulong>(rows);
        var low = layout.Path == SortKeyPath.Word128 ? arena.Rent<ulong>(rows) : [];
        try
        {
            _composite.Encode(in table, in layout, high, low);
            var lowIsSorted = _composite.Sort(in layout, high, low, permutation, rows, arena);
            if (layout.NeedsTieBreak)
            {
                TieBreak(in table, in layout, high, low, permutation, rows, lowIsSorted);
            }
        }
        finally
        {
            if (low.Length != 0)
            {
                arena.Return(low);
            }

            arena.Return(high);
        }
    }

    /// <summary>
    /// Settles the rows an encoded prefix left equal, and only those: one comparer, one pass, and a
    /// sort of each run of equal composites (§2). A composite that is the whole ordering has no runs
    /// to settle and this is never called.
    /// </summary>
    private void TieBreak(
        in SortTable table,
        in SortKeyLayout layout,
        ulong[] high,
        ulong[] low,
        int[] permutation,
        int rows,
        bool lowIsSorted)
    {
        var wide = layout.Path == SortKeyPath.Word128;
        var order = new RowOrder(table, _ordering);
        var start = 0;
        while (start < rows)
        {
            var end = start + 1;
            while (end < rows
                && high[end] == high[start]
                && (!wide || LowAt(low, permutation, end, lowIsSorted)
                    == LowAt(low, permutation, start, lowIsSorted)))
            {
                end++;
            }

            if (end - start > 1)
            {
                RowSort.Sort(permutation.AsSpan(start, end - start), order);
            }

            start = end;
        }
    }

    /// <summary>
    /// One sorted position's low word. A radix sort leaves the low words in the permutation's order;
    /// a high-word sort leaves them where the encoder wrote them, by row.
    /// </summary>
    private static ulong LowAt(ulong[] low, int[] permutation, int at, bool lowIsSorted) =>
        lowIsSorted ? low[at] : low[permutation[at]];

    /// <summary>
    /// The multi-key comparer over two rows of the concatenated table, by global row index. A struct
    /// rather than a <c>Comparer&lt;int&gt;.Create</c> delegate, because every framework sort that
    /// takes an <c>IComparer</c> boxes it and builds a <c>Comparison</c> per call, and a tie-break
    /// makes one call per run (<see cref="RowSort"/>).
    /// </summary>
    private readonly struct RowOrder(SortTable table, SortOrdering ordering) : IComparer<int>
    {
        public int Compare(int left, int right) => ordering.Compare(
            table.ChunkOfRow(left),
            table.OffsetOf(left),
            table.ChunkOfRow(right),
            table.OffsetOf(right));
    }
}

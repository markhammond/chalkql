using Chalk.Catalog;
using Chalk.Execution.Vectors;
using Chalk.Sources;
using Array = System.Array;
using ArrowSchema = Apache.Arrow.Schema;

namespace Chalk.Execution.Operators;

/// <summary>
/// <c>Sort</c> and <c>Fetch</c> fused (§6.3): a bounded heap of <c>offset + count</c> row references
/// over the sort comparer, above a store that holds only the rows the heap still points at.
/// </summary>
/// <remarks>
/// Before step 20 the operator retained the input <em>batches</em> an entry pointed at. A batch is
/// now a reused slot, so a candidate row is copied into this operator's own arena-backed store as it
/// is accepted (§1: "anything that must outlive a batch copies"). The store is compacted back to the
/// live rows whenever it grows past twice what the heap can hold, so <c>ORDER BY … LIMIT 10</c>
/// still never holds the whole input.
/// </remarks>
internal sealed class TopNOperator : OperatorBase
{
    private readonly IBatchOperator _input;
    private readonly SortOrdering _ordering;
    private readonly ColumnCopier[] _emit;
    private readonly ColumnarBatch _output;

    /// <summary>The two halves of the store, swapped when it is compacted.</summary>
    private readonly ColumnCopier[] _store;
    private readonly ColumnCopier[] _spare;
    private readonly ColumnView[] _storeViews;
    private readonly ColumnView[] _spareViews;

    private readonly RowBound _offset;
    private readonly RowBound _count;
    private long[] _heap = [];
    private int _heapCount;
    private int _storeRows;
    private bool _swapped;

    public TopNOperator(
        OperatorContext context,
        string path,
        ArrowSchema schema,
        IReadOnlyList<ChalkType> columnTypes,
        IBatchOperator input,
        SortOrdering ordering,
        RowBound offset,
        RowBound count)
        : base(context, schema, columnTypes, path)
    {
        _input = input;
        _ordering = ordering;
        _offset = offset;
        _count = count;
        _emit = [.. columnTypes.Select(t => new ColumnCopier(t))];
        _store = [.. columnTypes.Select(t => new ColumnCopier(t))];
        _spare = [.. columnTypes.Select(t => new ColumnCopier(t))];
        _storeViews = new ColumnView[columnTypes.Count];
        _spareViews = new ColumnView[columnTypes.Count];
        _output = NewOutput();
    }

    protected override ValueTask DisposeCoreAsync() => _input.DisposeAsync();

    protected override async IAsyncEnumerable<ColumnarBatch> RunAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        // The bounds this execution runs under, read before any row moves: a parameterised one is
        // read from its slot here, and a negative or NULL value is refused by name (D285).
        var offset = _offset.Resolve(Context.Parameters, "OFFSET");
        var count = _count.Resolve(Context.Parameters, "LIMIT");
        var capacity = offset + count;
        if (capacity <= 0 || capacity > int.MaxValue)
        {
            if (capacity > int.MaxValue)
            {
                throw new InvalidOperationException(
                    $"TopN was asked for {capacity} rows, which does not fit in memory as row references.");
            }

            yield break;
        }

        var wanted = (int)capacity;

        // A reused tree starts each execution from empty, and the heap is the arena's for the length
        // of this run rather than the operator's for ever (ADR 0012).
        _heapCount = 0;
        _storeRows = 0;
        _swapped = false;
        foreach (var copier in _store)
        {
            copier.Begin();
        }

        var arena = Context.Arena;
        _heap = arena.Rent<long>(wanted);
        var entries = System.Array.Empty<long>();
        try
        {
            var limit = Math.Max(2L * wanted, wanted + 1024L);
            await foreach (var batch in _input.ExecuteAsync(ct))
            {
                var rows = batch.Count;
                for (var i = 0; i < rows; i++)
                {
                    Offer(batch, batch.RowAt(i), wanted);
                }

                if (_storeRows > limit)
                {
                    Compact();
                }
            }

            var kept = _heapCount;
            entries = arena.Rent<long>(Math.Max(kept, 1));
            Array.Copy(_heap, entries, kept);
            Array.Sort(entries, 0, kept, Comparer<long>.Create(Compare));

            var batchSize = Context.Settings.BatchSize;
            for (var start = (int)offset; start < kept; start += batchSize)
            {
                ct.ThrowIfCancellationRequested();
                var rows = Math.Min(batchSize, kept - start);
                _output.Begin(rows);
                for (var c = 0; c < _emit.Length; c++)
                {
                    _emit[c].Begin();
                    for (var i = 0; i < rows; i++)
                    {
                        _emit[c].AppendRow(StoreViews[c], (int)entries[start + i]);
                    }

                    _output.Set(c, _emit[c].FinishView());
                }

                yield return _output;
            }
        }
        finally
        {
            arena.Return(entries);
            arena.Return(_heap);
            _heap = [];
            _heapCount = 0;
        }
    }

    /// <summary>The store's views as they stand, refreshed after every append.</summary>
    private ColumnView[] StoreViews => _swapped ? _spareViews : _storeViews;

    private ColumnCopier[] Store => _swapped ? _spare : _store;

    private void Offer(ColumnarBatch batch, int row, int wanted)
    {
        if (_heapCount >= wanted && !Improves(batch, row))
        {
            return;
        }

        var entry = Append(batch, row);
        if (_heapCount < wanted)
        {
            _heap[_heapCount] = entry;
            SiftUp(_heapCount++);
            return;
        }

        _heap[0] = entry;
        SiftDown(0);
    }

    /// <summary>Whether a candidate beats the worst row the heap is holding.</summary>
    private bool Improves(ColumnarBatch batch, int row)
    {
        var comparison = _ordering.Compare(batch.Columns, row, StoreViews, (int)_heap[0]);

        // Ties break on arrival order and the candidate arrived later, so a tie does not displace.
        return comparison < 0;
    }

    /// <summary>Copies one input row into the store and returns its entry.</summary>
    private long Append(ColumnarBatch batch, int row)
    {
        var store = Store;
        for (var c = 0; c < store.Length; c++)
        {
            store[c].AppendRow(batch.Column(c), row);
        }

        Refresh();
        return _storeRows++;
    }

    private void Refresh()
    {
        var views = StoreViews;
        var store = Store;
        for (var c = 0; c < store.Length; c++)
        {
            views[c] = store[c].FinishView();
        }
    }

    /// <summary>
    /// Rebuilds the store from the rows the heap still points at, in their arrival order, and remaps
    /// the heap onto the new positions. Arrival order is preserved, so the tie rule is unchanged.
    /// </summary>
    private void Compact()
    {
        var live = Context.Arena.Rent<long>(Math.Max(_heapCount, 1));
        try
        {
            CompactInto(live);
        }
        finally
        {
            Context.Arena.Return(live);
        }
    }

    private void CompactInto(long[] live)
    {
        Array.Copy(_heap, live, _heapCount);
        Array.Sort(live, 0, _heapCount);

        var target = _swapped ? _store : _spare;
        var targetViews = _swapped ? _storeViews : _spareViews;
        foreach (var copier in target)
        {
            copier.Begin();
        }

        var source = StoreViews;
        for (var i = 0; i < _heapCount; i++)
        {
            for (var c = 0; c < target.Length; c++)
            {
                target[c].AppendRow(source[c], (int)live[i]);
            }
        }

        for (var c = 0; c < target.Length; c++)
        {
            targetViews[c] = target[c].FinishView();
        }

        // The heap holds the old positions; each becomes its rank in the sorted live list.
        for (var h = 0; h < _heapCount; h++)
        {
            _heap[h] = Array.BinarySearch(live, 0, _heapCount, _heap[h]);
        }

        _swapped = !_swapped;
        _storeRows = _heapCount;
    }

    /// <summary>A max-heap: the root is the worst row kept, so it is the one an improvement replaces.</summary>
    private void SiftUp(int index)
    {
        while (index > 0)
        {
            var parent = (index - 1) / 2;
            if (Compare(_heap[index], _heap[parent]) <= 0)
            {
                return;
            }

            (_heap[index], _heap[parent]) = (_heap[parent], _heap[index]);
            index = parent;
        }
    }

    private void SiftDown(int index)
    {
        while (true)
        {
            var left = (2 * index) + 1;
            if (left >= _heapCount)
            {
                return;
            }

            var largest = left;
            var right = left + 1;
            if (right < _heapCount && Compare(_heap[right], _heap[left]) > 0)
            {
                largest = right;
            }

            if (Compare(_heap[largest], _heap[index]) <= 0)
            {
                return;
            }

            (_heap[index], _heap[largest]) = (_heap[largest], _heap[index]);
            index = largest;
        }
    }

    private int Compare(long left, long right)
    {
        var views = StoreViews;
        var comparison = _ordering.Compare(views, (int)left, views, (int)right);

        // Ties break on arrival order, so a heap that is at capacity keeps the earlier row. Without
        // this the heap's own array order would decide, and two runs could differ.
        return comparison != 0 ? comparison : left.CompareTo(right);
    }
}

using Chalk.Catalog;

namespace Chalk.Sources;

/// <summary>
/// One operator's output slot: the columns of the batch it is producing right now, reused for every
/// batch it produces (<c>docs/design/15-zero-allocation-execution.md</c> §1, D61).
/// </summary>
/// <remarks>
/// <para>
/// The slot is the unit of ownership. An operator owns its <see cref="ColumnarBatch"/> and the
/// arena-rented buffers behind the views in it; producing the next batch overwrites those buffers.
/// The consumer's contract is the one the design already had for Arrow batches, now enforced by
/// structure: a batch is valid until the next <c>MoveNext</c>, and anything that must outlive it
/// copies.
/// </para>
/// <para>
/// <see cref="Selection"/> is the selection vector of §2: empty means every row, otherwise the
/// selected row indexes into the columns. A batch with a selection never leaves the pipeline — the
/// root and every boundary compact.
/// </para>
/// </remarks>
public sealed class ColumnarBatch
{
    private ColumnView[] _columns;
    private int[] _selection = [];
    private int _selected;

    public ColumnarBatch(IReadOnlyList<ChalkType> types)
    {
        ArgumentNullException.ThrowIfNull(types);
        Types = types;
        _columns = new ColumnView[types.Count];
        for (var i = 0; i < _columns.Length; i++)
        {
            _columns[i] = ColumnView.Empty(types[i]);
        }
    }

    /// <summary>The column types, fixed by the operator's schema.</summary>
    public IReadOnlyList<ChalkType> Types { get; }

    /// <summary>Rows in the columns. <see cref="Count"/> is what a consumer iterates.</summary>
    public int RowCount { get; private set; }

    /// <summary>
    /// Rows a consumer sees: <see cref="RowCount"/> when there is no selection, else how many rows
    /// the selection names.
    /// </summary>
    public int Count => HasSelection ? _selected : RowCount;

    /// <summary>Bumped on every reuse; the views stamped with an older one are stale (D61).</summary>
    public int Generation { get; private set; }

    /// <summary>Whether views this slot hands out carry a back-reference and check their generation.</summary>
    public bool ValidateLifetimes { get; init; }

    /// <summary>True when only some of the rows are selected.</summary>
    public bool HasSelection { get; private set; }

    /// <summary>The selected row indexes, or empty when every row is selected (D62).</summary>
    public ReadOnlySpan<int> Selection => HasSelection ? _selection.AsSpan(0, _selected) : default;

    public int ColumnCount => _columns.Length;

    /// <summary>One column, as it stands.</summary>
    public ColumnView Column(int index) => _columns[index];

    /// <summary>Every column, for the operators that forward a whole row.</summary>
    public ReadOnlySpan<ColumnView> Columns => _columns.AsSpan();

    /// <summary>
    /// Starts the next batch: bumps the generation, clears the selection, and fixes the row count.
    /// Every view handed out before this call is stale from now on.
    /// </summary>
    public void Begin(int rowCount)
    {
        Generation++;
        RowCount = rowCount;
        HasSelection = false;
        _selected = 0;
    }

    /// <summary>Places one column of the batch under construction.</summary>
    public void Set(int index, in ColumnView column) => _columns[index] = Stamp(column);

    /// <summary>Places every column at once, for a pass-through.</summary>
    public void SetAll(ReadOnlySpan<ColumnView> columns)
    {
        for (var i = 0; i < columns.Length && i < _columns.Length; i++)
        {
            _columns[i] = Stamp(columns[i]);
        }
    }

    /// <summary>
    /// Adopts <paramref name="source"/>'s columns, row count and selection unchanged — a pass-through
    /// that copies nothing, which is what makes <c>UNION ALL</c> free (§6).
    /// </summary>
    public void Adopt(ColumnarBatch source)
    {
        ArgumentNullException.ThrowIfNull(source);
        Begin(source.RowCount);
        for (var i = 0; i < _columns.Length && i < source._columns.Length; i++)
        {
            _columns[i] = source._columns[i];
        }

        if (source.HasSelection)
        {
            // The source's rental, borrowed rather than copied: it outlives this batch by
            // construction — its owner returns it only when its own run ends.
            _selection = source._selection;
            _selected = source._selected;
            HasSelection = true;
        }
    }

    /// <summary>
    /// Gives this batch a selection over rows already placed. The array is the caller's — an
    /// arena rental it returns at the end of its run — and is not copied.
    /// </summary>
    public void Select(int[] selection, int count)
    {
        ArgumentNullException.ThrowIfNull(selection);
        _selection = selection;
        _selected = count;
        HasSelection = true;
    }

    /// <summary>
    /// Takes <paramref name="source"/>'s selection, sharing its array rather than copying it: the
    /// array is the producer's arena rental and outlives this batch by construction.
    /// </summary>
    public void ShareSelection(ColumnarBatch source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.HasSelection)
        {
            SelectAll();
            return;
        }

        _selection = source._selection;
        _selected = source._selected;
        HasSelection = true;
    }

    /// <summary>Declares that every row is selected, which is the common case.</summary>
    public void SelectAll()
    {
        HasSelection = false;
        _selected = 0;
    }

    /// <summary>The row index of the <paramref name="i"/>th selected row.</summary>
    public int RowAt(int i) => HasSelection ? _selection[i] : i;

    /// <summary>Resizes the slot when a schema is not known until the first batch.</summary>
    public void EnsureColumns(int count)
    {
        if (_columns.Length >= count)
        {
            return;
        }

        var grown = new ColumnView[count];
        _columns.CopyTo(grown.AsSpan());
        _columns = grown;
    }

    private ColumnView Stamp(in ColumnView column) => ValidateLifetimes
        ? column with { Generation = Generation, Owner = this }
        : column;
}

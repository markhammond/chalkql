using Chalk.Catalog;
using Chalk.Execution.Vectors;
using Chalk.Sources;

namespace Chalk.Execution.Windowing;

/// <summary>
/// The rows the streaming window operator is still holding: a contiguous run of the input, addressed
/// by the row's position in the whole input rather than by its position in the store
/// (<c>13-window-functions.md</c> §4.1, D258.4).
///
/// <para>
/// The operator reads batches in, appends them here, and releases everything below the oldest row any
/// call can still ask for. Releasing does not copy: the dead prefix is simply left alone until it is
/// at least as long as the live suffix, at which point the live rows are copied into the spare store
/// and the two are swapped. Each row is therefore copied at most once more than it would have been,
/// and the store never holds more than twice the live rows plus the batch that was last appended.
/// </para>
/// <para>
/// Everything is a <see cref="ColumnCopier"/>, which is what makes the variable-length kinds work:
/// a copier owns the bytes it is given — a long string is copied into its own payload buffer and its
/// descriptor rewritten — so a retained row survives the batch it arrived in and survives compaction.
/// The copiers are created with the operator, so the execution's arena backs them through
/// <c>ScratchScope</c> and gets them back when the execution ends (D64).
/// </para>
/// </summary>
internal sealed class WindowHold
{
    private readonly ChalkType[] _types;
    private readonly WindowColumn[] _columns;
    private readonly int _capacityHint;

    private ColumnCopier[] _store;
    private ColumnCopier[] _spare;

    /// <summary>Where row zero of the store sits in the whole input.</summary>
    private long _base;

    private int _rows;

    /// <summary>
    /// Dead rows a compaction is worth: below this the copy costs more than the bytes it reclaims,
    /// and a batch size of one would otherwise compact on every row.
    /// </summary>
    private const int CompactionFloor = 16;

    public WindowHold(IReadOnlyList<ChalkType> types, int capacityHint)
    {
        _types = [.. types];
        _capacityHint = capacityHint;
        _store = [.. _types.Select(t => new ColumnCopier(t))];
        _spare = [.. _types.Select(t => new ColumnCopier(t))];
        _columns = [.. _types.Select(t => new WindowColumn(ColumnView.Empty(t), 0, t))];
    }

    /// <summary>The first row still held, in the input's own numbering.</summary>
    public long Start => _base;

    /// <summary>One past the last row held — the rows read so far.</summary>
    public long End => _base + _rows;

    /// <summary>How many rows the store is carrying, live and dead together.</summary>
    public int Count => _rows;

    /// <summary>Where <paramref name="row"/> sits in the store. The caller has checked it is held.</summary>
    public int Local(long row) => (int)(row - _base);

    public WindowColumn Column(int column) => _columns[column];

    /// <summary>Points the hold at a new execution — no rows, both stores rewound.</summary>
    public void Begin()
    {
        _base = 0;
        _rows = 0;
        foreach (var copier in _store)
        {
            copier.Begin(_capacityHint);
        }

        Refresh();
    }

    /// <summary>Appends one input batch, honouring its selection.</summary>
    public void Append(ColumnarBatch batch)
    {
        for (var c = 0; c < _store.Length; c++)
        {
            _store[c].AppendBatch(batch, c);
        }

        _rows = _store.Length == 0 ? _rows + batch.Count : _store[0].Rows;
        Refresh();
    }

    /// <summary>
    /// Says that nothing below <paramref name="keepFrom"/> will be read again. The rows are dropped
    /// when the dead prefix has grown to at least the live suffix, which bounds the store and keeps
    /// the copying amortised at one row per row.
    /// </summary>
    public void Release(long keepFrom)
    {
        if (keepFrom <= _base)
        {
            return;
        }

        var dead = (int)Math.Min(keepFrom - _base, _rows);
        var live = _rows - dead;
        if (dead < CompactionFloor || dead < live)
        {
            return;
        }

        for (var c = 0; c < _store.Length; c++)
        {
            _spare[c].Begin(_capacityHint);
            if (live > 0)
            {
                _spare[c].Append(_store[c].FinishView().Slice(dead, live), live);
            }
        }

        (_store, _spare) = (_spare, _store);
        _base += dead;
        _rows = live;
        Refresh();
    }

    /// <summary>Appends one held row to an output column.</summary>
    public void AppendRowTo(ColumnCopier copier, int column, long row) =>
        copier.AppendRow(_columns[column].View, Local(row));

    /// <summary>
    /// Re-points the accessors at the store, which has just grown or moved. Once a batch, so the
    /// views themselves cost nothing per row.
    /// </summary>
    private void Refresh()
    {
        for (var c = 0; c < _store.Length; c++)
        {
            _columns[c].Retarget(_store[c].FinishView(), _rows);
        }
    }
}

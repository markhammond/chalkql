using Chalk.Catalog;
using Chalk.Sources;

namespace Chalk.Execution.Maintenance;

/// <summary>
/// One input's changes in one update, added up (D343, design 68 §2): each distinct row once, in the
/// order it first arrived, with the sum of its weights. A row taken out and put back in the same update
/// adds up to nothing, so the operator reading it does no work for it.
/// </summary>
internal sealed class ChangeSet
{
    private readonly ChangeColumn[] _columns;
    private readonly ColumnView[] _views;
    private readonly KeyColumns _identity;
    private readonly KeyEncoder _encoder = new();
    private readonly ByteMap _index = new();
    private long[] _weights = new long[16];

    public ChangeSet(IReadOnlyList<ChalkType> types)
    {
        Types = [.. types];
        _columns = [.. types.Select(t => new ChangeColumn(t))];
        _views = new ColumnView[types.Count];
        _identity = KeyColumns.All(Types);
    }

    public IReadOnlyList<ChalkType> Types { get; }

    /// <summary>Distinct rows, those that add up to nothing included.</summary>
    public int Count { get; private set; }

    /// <summary>The rows' columns, valid after <see cref="Seal"/> until the next <see cref="Clear"/>.</summary>
    public ReadOnlySpan<ColumnView> Columns => _views;

    public long Bytes
    {
        get
        {
            var bytes = ((long)_weights.Length * sizeof(long)) + _index.Bytes;
            foreach (var column in _columns)
            {
                bytes += column.Bytes;
            }

            return bytes;
        }
    }

    public long Weight(int row) => _weights[row];

    /// <summary>
    /// Row <paramref name="row"/>'s identity, as <see cref="KeyColumns.Encode(KeyEncoder, ReadOnlySpan{ColumnView}, int, bool)"/>
    /// encodes every column, not normalised: an operator keyed by whole rows reads it here rather
    /// than encoding the row again.
    /// </summary>
    public ReadOnlySpan<byte> Identity(int row) => _index.Key(row);

    public void Clear()
    {
        foreach (var column in _columns)
        {
            column.Clear();
        }

        _index.Clear();
        Count = 0;
    }

    /// <summary>Adds a change batch, whose last column is the weight, honouring its selection.</summary>
    public void Add(ColumnarBatch batch)
    {
        var weights = batch.Column(batch.ColumnCount - 1).Lanes<long>();
        for (var i = 0; i < batch.Count; i++)
        {
            var row = batch.RowAt(i);
            Add(batch.Columns, row, weights[row]);
        }
    }

    /// <summary>Adds every row of a batch of plain rows, each with <paramref name="weight"/>.</summary>
    public void AddRows(ColumnarBatch batch, long weight)
    {
        for (var i = 0; i < batch.Count; i++)
        {
            Add(batch.Columns, batch.RowAt(i), weight);
        }
    }

    /// <summary>Adds one row of <paramref name="columns"/>, the set's own types first, with <paramref name="weight"/>.</summary>
    public void Add(ReadOnlySpan<ColumnView> columns, int row, long weight)
    {
        if (weight == 0)
        {
            return;
        }

        _identity.Encode(_encoder, columns, row, normalise: false);
        ref var at = ref _index.GetOrAdd(_encoder.Span, out var added);
        if (!added)
        {
            _weights[at] += weight;
            return;
        }

        at = Count;
        for (var c = 0; c < _columns.Length; c++)
        {
            _columns[c].Append(columns[c], row);
        }

        if (Count == _weights.Length)
        {
            Array.Resize(ref _weights, _weights.Length * 2);
        }

        _weights[Count++] = weight;
    }

    /// <summary>Publishes the rows' columns, once every batch of the update has been added.</summary>
    public void Seal()
    {
        for (var c = 0; c < _columns.Length; c++)
        {
            _views[c] = _columns[c].View();
        }
    }
}

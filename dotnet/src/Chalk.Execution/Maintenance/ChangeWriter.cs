using Chalk.Catalog;
using Chalk.Sources;

namespace Chalk.Execution.Maintenance;

/// <summary>
/// The changes a maintained operator gives in an update (D343, design 68 §1): rows, each with its
/// weight, +1 for a row that arrives and -1 for one that leaves, published as batches whose last
/// column is the weight. Written row by row, in batches of at most the execution's batch size.
/// </summary>
/// <remarks>
/// A row is written by <see cref="Begin"/>, an append to each <see cref="Column"/>, and
/// <see cref="End"/> with its weight. The batches are valid until the next <see cref="Clear"/>, which
/// is the next update: every reader of an update's changes reads them within it.
/// </remarks>
internal sealed class ChangeWriter
{
    private readonly ChalkType[] _types;
    private readonly ChalkType[] _batchTypes;
    private readonly int _batchSize;
    private readonly List<Chunk> _chunks = [];
    private int _current = -1;

    public ChangeWriter(IReadOnlyList<ChalkType> types, int batchSize)
    {
        _types = [.. types];
        _batchTypes = [.. types, Weights.Type];
        _batchSize = Math.Max(1, batchSize);
    }

    /// <summary>The rows' types, without the weight.</summary>
    public IReadOnlyList<ChalkType> Types => _types;

    /// <summary>Rows written since <see cref="Clear"/>.</summary>
    public int Rows { get; private set; }

    /// <summary>The batches written, each with the weight last.</summary>
    public int BatchCount => _current + 1;

    /// <summary>Bytes the writer holds on to between updates.</summary>
    public long Bytes
    {
        get
        {
            var bytes = 0L;
            foreach (var chunk in _chunks)
            {
                foreach (var column in chunk.Columns)
                {
                    bytes += column.Bytes;
                }

                bytes += chunk.Weights.Bytes;
            }

            return bytes;
        }
    }

    public void Clear()
    {
        foreach (var chunk in _chunks)
        {
            chunk.Clear();
        }

        _current = -1;
        Rows = 0;
    }

    /// <summary>Starts a row, in a fresh batch when the current one is full.</summary>
    public void Begin()
    {
        if (_current < 0 || _chunks[_current].Weights.Count >= _batchSize)
        {
            _current++;
            if (_current == _chunks.Count)
            {
                _chunks.Add(new Chunk(_types, _batchTypes));
            }
        }
    }

    /// <summary>Column <paramref name="index"/> of the row being written.</summary>
    public ChangeColumn Column(int index) => _chunks[_current].Columns[index];

    /// <summary>Ends the row with its weight.</summary>
    public void End(long weight)
    {
        _chunks[_current].Weights.AppendInt64(weight);
        Rows++;
    }

    /// <summary>
    /// Writes a whole row of <paramref name="columns"/>, the writer's own types, with
    /// <paramref name="weight"/>.
    /// </summary>
    public void Write(ReadOnlySpan<ColumnView> columns, int row, long weight)
    {
        Begin();
        for (var c = 0; c < _types.Length; c++)
        {
            Column(c).Append(columns[c], row);
        }

        End(weight);
    }

    /// <summary>Writes a whole stored row with <paramref name="weight"/>.</summary>
    public void Write(RowStore store, int slot, long weight)
    {
        Begin();
        for (var c = 0; c < _types.Length; c++)
        {
            Column(c).Append(store.Column(c), slot);
        }

        End(weight);
    }

    /// <summary>Batch <paramref name="index"/>, published in its slot: the rows, then the weight.</summary>
    public ColumnarBatch Batch(int index) => _chunks[index].Publish();

    private sealed class Chunk
    {
        private readonly ColumnarBatch _slot;

        public Chunk(ChalkType[] types, ChalkType[] batchTypes)
        {
            Columns = [.. types.Select(t => new ChangeColumn(t))];
            Weights = new ChangeColumn(Maintenance.Weights.Type);
            _slot = new ColumnarBatch(batchTypes);
        }

        public ChangeColumn[] Columns { get; }

        public ChangeColumn Weights { get; }

        public void Clear()
        {
            foreach (var column in Columns)
            {
                column.Clear();
            }

            Weights.Clear();
        }

        public ColumnarBatch Publish()
        {
            _slot.Begin(Weights.Count);
            for (var c = 0; c < Columns.Length; c++)
            {
                _slot.Set(c, Columns[c].View());
            }

            _slot.Set(Columns.Length, Weights.View());
            return _slot;
        }
    }
}

/// <summary>The weight every change carries, as the last column of its batch (D343).</summary>
internal static class Weights
{
    /// <summary>The column's name in a rewritten plan's row types.</summary>
    public const string Name = "$weight";

    public static readonly ChalkType Type = ChalkType.Int64();

    /// <summary>The weight of row <paramref name="row"/> of a change batch, whose last column is the weight.</summary>
    public static long Of(ColumnarBatch batch, int row) =>
        batch.Column(batch.ColumnCount - 1).Lanes<long>()[row];
}

/// <summary>A statement a maintained view cannot keep, and why, refused when the view is made (§8).</summary>
internal sealed class NotMaintainedException : Exception
{
    public NotMaintainedException(string reason)
        : base(reason)
    {
    }

    /// <summary>Where in the plan, as the executor names an operator: <c>root/HashAggregate/…</c>.</summary>
    public string? Path { get; init; }
}

using Chalk.Catalog;
using Chalk.Execution.Operators;
using Chalk.Execution.Vectors;
using Chalk.Sources;

namespace Chalk.Execution.Joins;

/// <summary>
/// One join input read all the way in and held as this operator's own columns — the build side of a
/// hash or ASOF join, the buffered side of a nested loop, the right run of a merge.
/// </summary>
/// <remarks>
/// Every byte comes from the execution's arena, so a build side larger than <c>MaxBytes</c> fails
/// with an <c>ArenaBudgetExceededException</c> that the operator's path names (D45: no spilling —
/// Chalk is an in-memory engine and the budget is the safety net).
/// </remarks>
internal sealed class JoinRows
{
    private readonly ColumnCopier[] _copiers;
    private readonly ColumnView[] _columns;

    public JoinRows(IReadOnlyList<ChalkType> columnTypes)
    {
        _copiers = [.. columnTypes.Select(t => new ColumnCopier(t))];
        _columns = new ColumnView[columnTypes.Count];
    }

    /// <summary>Rows held. Zero until <see cref="FillAsync"/> has run.</summary>
    public int Count { get; private set; }

    public int Width => _copiers.Length;

    public ColumnView Column(int index) => _columns[index];

    /// <summary>The columns, for a gather.</summary>
    public ColumnView[] Columns => _columns;

    /// <summary>True when the input had rows to hold.</summary>
    public bool HasRows => Count > 0;

    /// <summary>
    /// Starts a fresh buffer that is about to be given roughly <paramref name="capacityHint"/> rows,
    /// from the plan's estimate, or zero where there is none (D258.3). Called by an operator that
    /// fills it row by row rather than by draining a whole input — a lookup join, which buffers only
    /// as many driving rows as one call's worth of distinct keys (M5, §4), and an adaptive join,
    /// which materialises its whole small side.
    /// </summary>
    public void Begin(int capacityHint = 0)
    {
        foreach (var copier in _copiers)
        {
            copier.Begin(capacityHint);
        }

        System.Array.Clear(_columns);
        Count = 0;
    }

    /// <summary>Copies one row of <paramref name="batch"/> into the buffer.</summary>
    public void AppendRow(ColumnarBatch batch, int row)
    {
        for (var c = 0; c < _copiers.Length; c++)
        {
            _copiers[c].AppendRow(batch.Column(c), row);
        }

        Count++;
    }

    /// <summary>Publishes what has been appended as this side's columns.</summary>
    public void Finish()
    {
        for (var c = 0; c < _copiers.Length; c++)
        {
            _columns[c] = _copiers[c].FinishView();
        }
    }

    /// <summary>
    /// Reads the whole input into this side's own buffers, honouring any selection (§2), starting at
    /// <paramref name="capacityHint"/> rows where the plan has an estimate for this side (D258.3).
    /// </summary>
    public async ValueTask FillAsync(
        IBatchOperator input, CancellationToken ct, int capacityHint = 0)
    {
        foreach (var copier in _copiers)
        {
            copier.Begin(capacityHint);
        }

        var rows = 0;
        await foreach (var batch in input.ExecuteAsync(ct).ConfigureAwait(false))
        {
            for (var c = 0; c < _copiers.Length; c++)
            {
                _copiers[c].AppendBatch(batch, c);
            }

            rows += batch.Count;
        }

        for (var c = 0; c < _copiers.Length; c++)
        {
            _columns[c] = _copiers[c].FinishView();
        }

        Count = rows;
    }

    /// <summary>Forgets the buffered rows. The arena gets its memory back when the copiers release.</summary>
    public void Release()
    {
        System.Array.Clear(_columns);
        Count = 0;
    }
}

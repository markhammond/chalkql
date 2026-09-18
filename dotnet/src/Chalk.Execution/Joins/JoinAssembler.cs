using Chalk.Catalog;
using Chalk.Execution.Vectors;
using Chalk.Sources;

namespace Chalk.Execution.Joins;

/// <summary>
/// Turns two index lists — one into the left rows, one into the right — into an output batch. A
/// negative index is the null padding an outer join owes that side.
/// </summary>
/// <remarks>
/// One copier per output column, reused for every batch, so a join's output costs one gather per
/// column rather than anything per row (ADR 0007). SEMI and ANTI joins output the left row only, so
/// the assembler is built with no right columns at all and never looks at the right indexes.
/// </remarks>
internal sealed class JoinAssembler
{
    private readonly ColumnCopier[] _copiers;
    private readonly int _leftWidth;

    /// <param name="outputTypes">The join's output row: left fields then right fields.</param>
    /// <param name="leftWidth">How many of them come from the left input.</param>
    public JoinAssembler(IReadOnlyList<ChalkType> outputTypes, int leftWidth)
    {
        _copiers = [.. outputTypes.Select(t => new ColumnCopier(t))];
        _leftWidth = leftWidth;
    }

    public int Width => _copiers.Length;

    /// <summary>The output row's types, which is the shape of the slot this assembler fills.</summary>
    public IReadOnlyList<ChalkType> Types => [.. _copiers.Select(c => c.Type)];

    /// <summary>
    /// Fills <paramref name="slot"/> with one output batch. An empty <paramref name="leftColumns"/>
    /// means every left field is NULL — the unmatched-build pass of a RIGHT or FULL join.
    /// </summary>
    public ColumnarBatch Emit(
        ColumnarBatch slot,
        ReadOnlySpan<ColumnView> leftColumns,
        ReadOnlySpan<int> leftIndexes,
        ReadOnlySpan<ColumnView> rightColumns,
        ReadOnlySpan<int> rightIndexes)
    {
        var rows = leftIndexes.Length;
        slot.Begin(rows);
        for (var c = 0; c < _copiers.Length; c++)
        {
            var copier = _copiers[c];
            copier.Begin();
            if (c < _leftWidth)
            {
                Append(copier, leftColumns, c, leftIndexes, rows);
            }
            else
            {
                Append(copier, rightColumns, c - _leftWidth, rightIndexes, rows);
            }

            slot.Set(c, copier.FinishView());
        }

        return slot;
    }

    private static void Append(
        ColumnCopier copier,
        ReadOnlySpan<ColumnView> columns,
        int column,
        ReadOnlySpan<int> indexes,
        int rows)
    {
        if (columns.Length == 0)
        {
            copier.AppendNulls(rows);
            return;
        }

        copier.AppendGatherOrNull(columns[column], indexes);
    }
}

using Chalk.Catalog;
using Chalk.Execution.Operators;
using Chalk.Execution.Vectors;
using Chalk.Sources;
using ArrowSchema = Apache.Arrow.Schema;

namespace Chalk.Execution.Aggregation;

/// <summary>
/// One grouping set: its table, and where each of the aggregate's output columns comes from in its
/// rows.
/// </summary>
internal sealed class GroupingSet
{
    /// <summary>
    /// <see cref="Columns"/>'s mark for a key the set does not name, which is NULL in its rows.
    /// </summary>
    public const int NullKey = -1;

    /// <summary><see cref="Columns"/>'s mark for a <c>GROUPING</c> measure, whose value is the set's.</summary>
    public const int Grouping = -2;

    /// <summary>The set's own keys, then the measures other than <c>GROUPING</c>.</summary>
    public required HashAggregateOperator Table { get; init; }

    /// <summary>
    /// For each output column, the table's column it is, or <see cref="NullKey"/>, or
    /// <see cref="Grouping"/>.
    /// </summary>
    public required int[] Columns { get; init; }

    /// <summary>For each output column marked <see cref="Grouping"/>, the value it has in this set's rows.</summary>
    public required long[] Groupings { get; init; }
}

/// <summary>
/// <c>GROUPING SETS</c>, <c>ROLLUP</c> and <c>CUBE</c> in one pass over the input (D338, design 63
/// §1). Each set has a hash table of its own; every input batch is folded into each in turn, so the
/// input is read once however many sets there are, and the sets' groups then come out one set after
/// another. A key a set does not name is NULL in that set's rows, and <c>GROUPING</c> is the set's
/// own constant, so every row has the aggregate's whole output layout.
/// </summary>
/// <remarks>
/// Each table rents its state from the arena as a single aggregate does, so the sets' memory is the
/// sum of their tables, held to the same budget.
/// </remarks>
internal sealed class GroupingSetsOperator : OperatorBase
{
    private readonly IBatchOperator _input;
    private readonly GroupingSet[] _sets;
    private readonly ColumnCopier?[] _constants;
    private readonly ColumnarBatch _slot;

    public GroupingSetsOperator(
        OperatorContext context,
        string path,
        ArrowSchema schema,
        IReadOnlyList<ChalkType> outputTypes,
        IBatchOperator input,
        GroupingSet[] sets)
        : base(context, schema, outputTypes, path)
    {
        _input = input;
        _sets = sets;
        _slot = NewOutput();

        // A copier for each column some set fills itself, with NULLs or its GROUPING value.
        _constants = new ColumnCopier?[outputTypes.Count];
        for (var c = 0; c < outputTypes.Count; c++)
        {
            if (sets.Any(s => s.Columns[c] < 0))
            {
                _constants[c] = new ColumnCopier(outputTypes[c]);
            }
        }
    }

    protected override ValueTask DisposeCoreAsync() => _input.DisposeAsync();

    protected override async IAsyncEnumerable<ColumnarBatch> RunAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        try
        {
            foreach (var set in _sets)
            {
                set.Table.Restart();
            }

            await foreach (var batch in _input.ExecuteAsync(ct))
            {
                foreach (var set in _sets)
                {
                    set.Table.Consume(batch);
                }
            }

            foreach (var set in _sets)
            {
                foreach (var groups in set.Table.Emit(ct))
                {
                    var count = groups.RowCount;
                    _slot.Begin(count);
                    for (var c = 0; c < set.Columns.Length; c++)
                    {
                        var column = set.Columns[c];
                        if (column >= 0)
                        {
                            _slot.Set(c, groups.Column(column));
                            continue;
                        }

                        var constant = _constants[c]!;
                        constant.Begin();
                        if (column == GroupingSet.NullKey)
                        {
                            constant.AppendNulls(count);
                        }
                        else
                        {
                            constant.AppendConstant(
                                new ScalarValue { Type = ColumnTypes[c], Integer = set.Groupings[c] }, count);
                        }

                        _slot.Set(c, constant.FinishView());
                    }

                    yield return _slot;
                }
            }
        }
        finally
        {
            foreach (var set in _sets)
            {
                set.Table.ReleaseScratch();
            }
        }
    }
}

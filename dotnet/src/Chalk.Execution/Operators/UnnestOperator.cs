using System.Runtime.InteropServices;
using Chalk.Catalog;
using Chalk.Execution.Vectors;
using Chalk.Sources;
using ArrowSchema = Apache.Arrow.Schema;

namespace Chalk.Execution.Operators;

/// <summary>
/// <c>UNNEST</c> (D66, <c>14-windows-ii.md</c> §7): one input row becomes one output row per element
/// of its list, with the element — and optionally its 1-based position — appended.
/// </summary>
/// <remarks>
/// <para>
/// Streaming, one input batch at a time, and a row's elements come out together, so the input's
/// ordering survives and the node claims it. <c>keepEmpty</c> is the difference between
/// <c>CROSS JOIN UNNEST</c>, where an empty or NULL list contributes nothing, and
/// <c>LEFT JOIN LATERAL UNNEST … ON TRUE</c>, where it contributes one NULL-padded row.
/// </para>
/// <para>
/// The element column is normally a <em>slice</em> of the list column's own child view: a batch's
/// rows are consecutive, so their elements are a contiguous range of it and nothing is copied.
/// Padding breaks that — a NULL element is not in the child at all — so a batch that pads builds its
/// element column through a copier instead.
/// </para>
/// </remarks>
internal sealed class UnnestOperator : OperatorBase
{
    /// <summary>A padded row's element index: not a position in the child array.</summary>
    private const int Padding = -1;

    private readonly IBatchOperator _input;
    private readonly int _listColumn;
    private readonly bool _withOrdinality;
    private readonly bool _keepEmpty;
    private readonly ColumnCopier[] _output;
    private readonly ColumnarBatch _slot;

    public UnnestOperator(
        OperatorContext context,
        string path,
        ArrowSchema schema,
        IReadOnlyList<ChalkType> outputTypes,
        IBatchOperator input,
        int listColumn,
        bool withOrdinality,
        bool keepEmpty)
        : base(context, schema, outputTypes, path)
    {
        _input = input;
        _listColumn = listColumn;
        _withOrdinality = withOrdinality;
        _keepEmpty = keepEmpty;
        _output = [.. outputTypes.Select(t => new ColumnCopier(t))];
        _slot = NewOutput();
    }

    protected override ValueTask DisposeCoreAsync() => _input.DisposeAsync();

    /// <summary>How many columns come straight from the input.</summary>
    private int InputColumns => _output.Length - (_withOrdinality ? 2 : 1);

    protected override async IAsyncEnumerable<ColumnarBatch> RunAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var batchSize = Context.Settings.BatchSize;
        var arena = Context.Arena;
        var positions = arena.Rent<int>(batchSize);
        var elements = arena.Rent<int>(batchSize);
        var ordinals = arena.Rent<int>(batchSize);

        try
        {
            await foreach (var batch in _input.ExecuteAsync(ct))
            {
                {
                    var filled = 0;
                    for (var r = 0; r < batch.Count; r++)
                    {
                        var row = batch.RowAt(r);
                        ct.ThrowIfCancellationRequested();
                        var list = batch.Column(_listColumn);
                        var valid = list.IsValid(row);
                        var offsets = list.OffsetLanes();
                        var start = valid ? offsets[row] : 0;
                        var count = valid ? offsets[row + 1] - start : 0;

                        if (count == 0)
                        {
                            if (!_keepEmpty)
                            {
                                continue;
                            }

                            positions[filled] = row;
                            elements[filled] = Padding;
                            ordinals[filled] = 0;
                            filled++;
                            if (filled == batchSize)
                            {
                                yield return Emit(batch, positions, elements, ordinals, filled);
                                filled = 0;
                            }

                            continue;
                        }

                        for (var e = 0; e < count; e++)
                        {
                            positions[filled] = row;
                            elements[filled] = start + e;
                            ordinals[filled] = e + 1;
                            filled++;
                            if (filled == batchSize)
                            {
                                yield return Emit(batch, positions, elements, ordinals, filled);
                                filled = 0;
                            }
                        }
                    }

                    if (filled > 0)
                    {
                        yield return Emit(batch, positions, elements, ordinals, filled);
                    }
                }
            }
        }
        finally
        {
            arena.Return(positions);
            arena.Return(elements);
            arena.Return(ordinals);
        }
    }

    private ColumnarBatch Emit(
        ColumnarBatch batch, int[] positions, int[] elements, int[] ordinals, int count)
    {
        var inputColumns = InputColumns;
        _slot.Begin(count);
        var built = 0;
        for (; built < inputColumns; built++)
        {
            _slot.Set(built, _output[built].Gather(batch.Column(built), positions.AsSpan(0, count)));
        }

        _slot.Set(built, Elements(batch.Column(_listColumn), elements, count));
        built++;
        if (_withOrdinality)
        {
            _slot.Set(built, Ordinality(_output[built], ordinals, count));
        }

        return _slot;
    }

    /// <summary>
    /// The element column: a shared slice of the list's child array when the batch's elements are one
    /// contiguous range of it, and a copy when a padded row interrupts them.
    /// </summary>
    private ColumnView Elements(in ColumnView list, int[] elements, int count)
    {
        var child = list.Child;
        var contiguous = elements[0] != Padding;
        for (var i = 1; i < count && contiguous; i++)
        {
            contiguous = elements[i] == elements[i - 1] + 1;
        }

        if (contiguous)
        {
            return child.Slice(elements[0], count);
        }

        var copier = _output[InputColumns];
        copier.Begin();
        for (var i = 0; i < count; i++)
        {
            if (elements[i] == Padding)
            {
                copier.AppendNulls(1);
            }
            else
            {
                copier.AppendRow(child, elements[i]);
            }
        }

        return copier.FinishView();
    }

    /// <summary>The 1-based position column, which is I32 and NULL exactly where a row was padded.</summary>
    private static ColumnView Ordinality(ColumnCopier copier, int[] ordinals, int count)
    {
        copier.Begin();
        Span<byte> lane = stackalloc byte[4];
        for (var i = 0; i < count; i++)
        {
            if (ordinals[i] == 0)
            {
                copier.AppendNulls(1);
                continue;
            }

            MemoryMarshal.Write(lane, ordinals[i]);
            copier.AppendRaw(lane, valid: true);
        }

        return copier.FinishView();
    }
}

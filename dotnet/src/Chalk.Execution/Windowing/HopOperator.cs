using System.Runtime.InteropServices;
using Chalk.Catalog;
using Chalk.Execution.Operators;
using Chalk.Execution.Vectors;
using Chalk.Sources;
using ArrowSchema = Apache.Arrow.Schema;

namespace Chalk.Execution.Windowing;

/// <summary>
/// The hopping window of D55 (<c>14-windows-ii.md</c> §1): one output row per (input row, window it
/// falls in), with <c>window_start</c> and <c>window_end</c> appended.
/// </summary>
/// <remarks>
/// Streaming, one input batch at a time. A row's copies come out together and in ascending
/// <c>window_start</c>, which is what lets the node claim its input's ordering — and it is why the
/// window starts are counted down from the floor and then emitted upwards rather than the other way
/// round. A row whose time is NULL, and a row that falls in no window at all (which happens exactly
/// when {@code size &lt; slide}), produce nothing.
/// </remarks>
internal sealed class HopOperator : OperatorBase
{
    private readonly IBatchOperator _input;
    private readonly int _timeColumn;
    private readonly ChalkType _timeType;
    private readonly ColumnKind _timeKind;
    private readonly ScalarValue _slide;
    private readonly ScalarValue _size;
    private readonly ColumnCopier[] _output;
    private readonly ColumnarBatch _slot;

    public HopOperator(
        OperatorContext context,
        string path,
        ArrowSchema schema,
        IReadOnlyList<ChalkType> outputTypes,
        IBatchOperator input,
        int timeColumn,
        ChalkType timeType,
        ScalarValue slide,
        ScalarValue size)
        : base(context, schema, outputTypes, path)
    {
        _input = input;
        _timeColumn = timeColumn;
        _timeType = timeType;
        _timeKind = ColumnKinds.Of(timeType);
        _slide = slide;
        _size = size;
        _output = [.. outputTypes.Select(t => new ColumnCopier(t))];
        _slot = NewOutput();
    }

    protected override ValueTask DisposeCoreAsync() => _input.DisposeAsync();

    protected override async IAsyncEnumerable<ColumnarBatch> RunAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var slide = WindowBoundsMath.IntervalInUnits(_slide.Integer, _timeType, "HOP's slide");
        var size = WindowBoundsMath.IntervalInUnits(_size.Integer, _timeType, "HOP's size");
        var batchSize = Context.Settings.BatchSize;
        var arena = Context.Arena;

        var positions = arena.Rent<int>(batchSize);
        var starts = arena.Rent<long>(batchSize);
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
                        var time = batch.Column(_timeColumn);
                        if (!time.IsValid(row))
                        {
                            continue;
                        }

                        var value = ReadTime(time, row);
                        var windows = WindowBoundsMath.WindowCount(value, slide, size);
                        var start = WindowBoundsMath.FirstWindowStart(value, slide, windows);
                        for (var w = 0; w < windows; w++)
                        {
                            positions[filled] = row;
                            starts[filled] = start + (w * slide);
                            filled++;
                            if (filled == batchSize)
                            {
                                yield return Emit(batch, positions, starts, filled, size);
                                filled = 0;
                            }
                        }
                    }

                    if (filled > 0)
                    {
                        yield return Emit(batch, positions, starts, filled, size);
                    }
                }
            }
        }
        finally
        {
            arena.Return(positions);
            arena.Return(starts);
        }
    }

    /// <summary>One output batch: the input's columns gathered, then the two window bounds.</summary>
    private ColumnarBatch Emit(
        ColumnarBatch batch, int[] positions, long[] starts, int count, long size)
    {
        var inputColumns = _output.Length - 2;
        _slot.Begin(count);
        var built = 0;
        for (; built < inputColumns; built++)
        {
            _slot.Set(built, _output[built].Gather(batch.Column(built), positions.AsSpan(0, count)));
        }

        _slot.Set(built, Bounds(_output[built], starts.AsSpan(0, count), 0, count));
        built++;
        _slot.Set(built, Bounds(_output[built], starts.AsSpan(0, count), size, count));
        return _slot;
    }

    /// <summary>A bounds column: every start, plus <paramref name="offset"/> units.</summary>
    private ColumnView Bounds(ColumnCopier copier, ReadOnlySpan<long> starts, long offset, int count)
    {
        copier.Begin();
        Span<byte> lane = stackalloc byte[8];
        for (var i = 0; i < count; i++)
        {
            var value = starts[i] + offset;
            if (_timeKind == ColumnKind.Int32)
            {
                MemoryMarshal.Write(lane, (int)value);
            }
            else
            {
                MemoryMarshal.Write(lane, value);
            }

            copier.AppendRaw(lane, valid: true);
        }

        return copier.FinishView();
    }

    private long ReadTime(in ColumnView column, int row) => _timeKind switch
    {
        ColumnKind.Int32 => column.Lanes<int>()[row],
        ColumnKind.Int64 => column.Lanes<long>()[row],
        _ => throw new UnsupportedFeatureException(
            $"HOP over a {_timeType} column",
            "A window's time column is DATE, TIMESTAMP or TIMESTAMP_TZ."),
    };
}

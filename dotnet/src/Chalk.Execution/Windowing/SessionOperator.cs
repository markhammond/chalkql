using System.Runtime.InteropServices;
using Chalk.Catalog;
using Chalk.Execution.Operators;
using Chalk.Execution.Vectors;
using Chalk.Sources;
using ArrowSchema = Apache.Arrow.Schema;

namespace Chalk.Execution.Windowing;

/// <summary>
/// The session window of D55 (<c>14-windows-ii.md</c> §1): one output row per input row, with the
/// bounds of the session it belongs to appended.
/// </summary>
/// <remarks>
/// <para>
/// A session is a run of rows whose consecutive gaps are all smaller than <c>gap</c>. Its
/// <c>window_start</c> is its first time and its <c>window_end</c> its last time plus the gap, which
/// is Calcite's own definition: each row opens the window <c>[t, t + gap)</c> and overlapping windows
/// merge, so a row at or after the previous end starts a new session (V24).
/// </para>
/// <para>
/// The input arrives ordered by (partition keys, time) — the plan requires it as a trait — so
/// partitions are contiguous runs and NULL times come last inside each. Bounds are only known once a
/// session has ended, so the operator buffers its input the way <c>WindowOperator</c> does, walks it
/// once to find the sessions and once to write the bounds, and emits in batches. Every array is
/// rented from the arena, so <c>MaxBytes</c> applies and an aborted run leaves it empty.
/// </para>
/// </remarks>
internal sealed class SessionOperator : OperatorBase
{
    private readonly IBatchOperator _input;
    private readonly int[] _partitionKeys;
    private readonly int _timeColumn;
    private readonly ChalkType _timeType;
    private readonly ColumnKind _timeKind;
    private readonly ScalarValue _gap;
    private readonly ChalkType[] _inputTypes;
    private readonly ColumnCopier[] _buffer;
    private readonly ColumnCopier[] _output;
    private readonly ColumnarBatch _slot;
    private readonly ColumnView[] _table;

    public SessionOperator(
        OperatorContext context,
        string path,
        ArrowSchema schema,
        IReadOnlyList<ChalkType> outputTypes,
        IReadOnlyList<ChalkType> inputTypes,
        IBatchOperator input,
        IReadOnlyList<int> partitionKeys,
        int timeColumn,
        ScalarValue gap)
        : base(context, schema, outputTypes, path)
    {
        _input = input;
        _partitionKeys = [.. partitionKeys];
        _timeColumn = timeColumn;
        _timeType = inputTypes[timeColumn];
        _timeKind = ColumnKinds.Of(_timeType);
        _gap = gap;
        _inputTypes = [.. inputTypes];
        _buffer = [.. inputTypes.Select(t => new ColumnCopier(t))];
        _output = [.. outputTypes.Select(t => new ColumnCopier(t))];
        _slot = NewOutput();
        _table = new ColumnView[inputTypes.Count];
    }

    protected override ValueTask DisposeCoreAsync() => _input.DisposeAsync();

    protected override async IAsyncEnumerable<ColumnarBatch> RunAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var gap = WindowBoundsMath.IntervalInUnits(_gap.Integer, _timeType, "SESSION's gap");
        var arena = Context.Arena;
        var table = _table;
        var starts = System.Array.Empty<long>();
        var ends = System.Array.Empty<long>();
        var assigned = System.Array.Empty<byte>();

        try
        {
            var rows = await ConcatenateAsync(ct).ConfigureAwait(false);
            if (rows == 0)
            {
                yield break;
            }

            starts = arena.Rent<long>(rows);
            ends = arena.Rent<long>(rows);
            assigned = arena.Rent<byte>(rows);

            Sessionise(table, rows, gap, starts, ends, assigned);

            var batchSize = Context.Settings.BatchSize;
            for (var start = 0; start < rows; start += batchSize)
            {
                ct.ThrowIfCancellationRequested();
                var count = Math.Min(batchSize, rows - start);
                yield return Emit(table, starts, ends, assigned, start, count);
            }
        }
        finally
        {
            arena.Return(starts);
            arena.Return(ends);
            arena.Return(assigned);
        }
    }

    /// <summary>
    /// Walks the contiguous partition runs the plan guarantees and numbers the sessions inside each.
    /// A row whose time is NULL belongs to no session and gets NULL bounds; those rows are last in
    /// their run, so the scan stops at the first one.
    /// </summary>
    private void Sessionise(
        ColumnView[] table, int rows, long gap, long[] starts, long[] ends, byte[] assigned)
    {
        var keys = new WindowColumn[_partitionKeys.Length];
        for (var k = 0; k < keys.Length; k++)
        {
            keys[k] = new WindowColumn(table[_partitionKeys[k]], rows, _inputTypes[_partitionKeys[k]]);
        }

        var partitions = new WindowRowComparer(keys);
        var time = table[_timeColumn];

        var runStart = 0;
        while (runStart < rows)
        {
            var runEnd = runStart + 1;
            while (runEnd < rows && partitions.SameGroup(runStart, runEnd))
            {
                runEnd++;
            }

            var i = runStart;
            while (i < runEnd && time.IsValid(i))
            {
                var first = ReadTime(time, i);
                var last = first;
                var j = i;
                while (j + 1 < runEnd
                    && time.IsValid(j + 1)
                    && ReadTime(time, j + 1) < last + gap)
                {
                    j++;
                    last = ReadTime(time, j);
                }

                for (var k = i; k <= j; k++)
                {
                    starts[k] = first;
                    ends[k] = last + gap;
                    assigned[k] = 1;
                }

                i = j + 1;
            }

            for (; i < runEnd; i++)
            {
                assigned[i] = 0;
            }

            runStart = runEnd;
        }
    }

    private ColumnarBatch Emit(
        ColumnView[] table, long[] starts, long[] ends, byte[] assigned, int start, int count)
    {
        var inputColumns = table.Length;
        _slot.Begin(count);
        var built = 0;
        for (; built < inputColumns; built++)
        {
            _slot.Set(built, table[built].Slice(start, count));
        }

        _slot.Set(built, Bounds(_output[built], starts, assigned, start, count));
        built++;
        _slot.Set(built, Bounds(_output[built], ends, assigned, start, count));
        return _slot;
    }

    private ColumnView Bounds(
        ColumnCopier copier, long[] values, byte[] assigned, int start, int count)
    {
        copier.Begin();
        Span<byte> lane = stackalloc byte[8];
        for (var i = 0; i < count; i++)
        {
            var row = start + i;
            if (assigned[row] == 0)
            {
                copier.AppendNulls(1);
                continue;
            }

            if (_timeKind == ColumnKind.Int32)
            {
                MemoryMarshal.Write(lane, (int)values[row]);
            }
            else
            {
                MemoryMarshal.Write(lane, values[row]);
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
            $"SESSION over a {_timeType} column",
            "A window's time column is DATE, TIMESTAMP or TIMESTAMP_TZ."),
    };

    /// <summary>Reads the whole input into this operator's own columns and returns how many rows.</summary>
    private async ValueTask<int> ConcatenateAsync(CancellationToken ct)
    {
        foreach (var copier in _buffer)
        {
            copier.Begin();
        }

        var rows = 0;
        await foreach (var batch in _input.ExecuteAsync(ct))
        {
            rows += batch.Count;
            for (var c = 0; c < _buffer.Length; c++)
            {
                _buffer[c].AppendBatch(batch, c);
            }
        }

        for (var c = 0; c < _buffer.Length; c++)
        {
            _table[c] = _buffer[c].FinishView();
        }

        return rows;
    }
}

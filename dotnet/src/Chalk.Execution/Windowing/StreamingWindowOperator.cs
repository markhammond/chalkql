using Chalk.Catalog;
using Chalk.Execution.Aggregation;
using Chalk.Execution.Operators;
using Chalk.Execution.Vectors;
using Chalk.Sources;
using ArrowSchema = Apache.Arrow.Schema;

namespace Chalk.Execution.Windowing;

/// <summary>
/// The streaming window operator of D258.4 (<c>13-window-functions.md</c> §4.1): the path taken when
/// every frame ends at or before the current row and every call over it can be answered from the rows
/// still in hand.
///
/// <para>
/// Nothing is concatenated. Batches arrive, their rows go into a <see cref="WindowHold"/>, and a row's
/// answer is written the moment its frame is complete — immediately for a <c>ROWS</c> frame, at the
/// end of its peer group for a <c>RANGE</c> one. Everything below the oldest row any call can still
/// read is released, so the operator's memory is the frame, the open peer group and the output batch
/// being assembled, whatever the input's size.
/// </para>
/// <para>
/// One hold serves every call, which is the second half of D258.4: the planner reduces <c>AVG(x)</c>
/// to <c>COUNT(x)</c> and <c>SUM(x)</c> before the window, and both read the same retained column
/// rather than each renting state over the whole input.
/// </para>
/// </summary>
internal sealed class StreamingWindowOperator : OperatorBase
{
    private readonly IBatchOperator _input;
    private readonly WindowSpec _spec;
    private readonly StreamingWindowCall[] _calls;
    private readonly StreamingWindowArgument[] _arguments;
    private readonly ChalkType[] _inputTypes;
    private readonly WindowHold _hold;
    private readonly ColumnCopier[] _output;
    private readonly ColumnarBatch _slot;
    private readonly StreamingWindowFrame _frame;
    private readonly StreamingWindowRun _run;
    private readonly StreamingWindowPeers _peers;
    private readonly WindowRowComparer _partitions;
    private readonly WindowRowComparer _order;
    private readonly bool _tracksPeers;
    private readonly bool _readsFrameRows;
    private readonly long _rowsBehind;

    private long _computed;
    private long _partitionStart;
    private long _partitionEnd;
    private long _scanned;
    private bool _inputDone;
    private HashSet<string>? _seen;

    public StreamingWindowOperator(
        OperatorContext context,
        string path,
        ArrowSchema schema,
        IReadOnlyList<ChalkType> outputTypes,
        IReadOnlyList<ChalkType> inputTypes,
        IBatchOperator input,
        WindowSpec spec,
        StreamingWindowPlan plan)
        : base(context, schema, outputTypes, path)
    {
        var calls = plan.Calls;
        _input = input;
        _spec = spec;
        _calls = calls;
        _arguments = plan.Arguments;
        _inputTypes = [.. inputTypes];
        _hold = new WindowHold(_inputTypes, context.Settings.BatchSize);
        _output = [.. outputTypes.Select(t => new ColumnCopier(t))];
        _slot = NewOutput();
        _frame = new StreamingWindowFrame(spec);
        _run = new StreamingWindowRun { Hold = _hold };
        _partitions = new WindowRowComparer([.. spec.PartitionKeys.Select(k => _hold.Column(k))]);
        _order = new WindowRowComparer([.. spec.Order.Select(k => _hold.Column(k.Column))]);
        _peers = new StreamingWindowPeers(_order, _hold);
        _tracksPeers = spec.Mode == Ir.FrameMode.Range || calls.Any(c => c.NeedsPeers);
        _readsFrameRows = calls.Any(c => c.LookBack.Frame);
        _rowsBehind = calls.Length == 0 ? 0 : calls.Max(c => c.LookBack.Rows);
    }

    protected override ValueTask DisposeCoreAsync() => _input.DisposeAsync();

    protected override async IAsyncEnumerable<ColumnarBatch> RunAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var arena = Context.Arena;
        var batchSize = Context.Settings.BatchSize;
        var started = false;
        var pending = 0;

        _hold.Begin();
        _run.Parameters = Context.Parameters;
        _run.ReadEnd = 0;
        _computed = 0;
        _scanned = 0;
        _inputDone = false;
        _frame.Begin(Context.Parameters);
        Context.Stats.RecordWindowPath(WindowPath.Streaming);

        try
        {
            foreach (var call in _calls)
            {
                call.Begin(arena, Context.Parameters);
            }

            started = true;
            StartPartition(0);
            BeginOutput(batchSize);

            await foreach (var batch in _input.ExecuteAsync(ct))
            {
                ct.ThrowIfCancellationRequested();
                if (batch.Count > 0)
                {
                    var before = _hold.End;
                    _hold.Append(batch);
                    _run.ReadEnd = _hold.End;
                    if (WindowOperator.VerifyInputOrder)
                    {
                        Verify(before);
                    }

                    ScanPartitions();
                }

                while (true)
                {
                    var first = _computed;
                    var settled = SettleAndEmit(batchSize - pending);
                    if (settled > 0)
                    {
                        AppendHeldRows(first, settled);
                        pending += settled;
                    }

                    if (pending < batchSize)
                    {
                        break;
                    }

                    _slot.Begin(pending);
                    for (var i = 0; i < _output.Length; i++)
                    {
                        _slot.Set(i, _output[i].FinishView());
                    }

                    yield return _slot;
                    pending = 0;
                    BeginOutput(batchSize);
                }

                ReleaseHold();
            }

            _inputDone = true;
            ScanPartitions();

            while (true)
            {
                var first = _computed;
                var settled = SettleAndEmit(batchSize - pending);
                if (settled > 0)
                {
                    AppendHeldRows(first, settled);
                    pending += settled;
                }

                if (pending < batchSize)
                {
                    break;
                }

                _slot.Begin(pending);
                for (var i = 0; i < _output.Length; i++)
                {
                    _slot.Set(i, _output[i].FinishView());
                }

                yield return _slot;
                pending = 0;
                BeginOutput(batchSize);

                if (settled == 0)
                {
                    break;
                }
            }

            if (pending > 0)
            {
                _slot.Begin(pending);
                for (var i = 0; i < _output.Length; i++)
                {
                    _slot.Set(i, _output[i].FinishView());
                }

                yield return _slot;
            }
        }
        finally
        {
            if (started)
            {
                foreach (var call in _calls)
                {
                    call.Release(arena);
                }
            }
        }
    }

    /// <summary>
    /// Settles and answers up to <paramref name="cap"/> rows, stopping early at the first row whose
    /// frame the input has not closed yet. Each call appends straight into its output column, which
    /// is why no per-row result storage exists on this path.
    /// </summary>
    private int SettleAndEmit(int cap)
    {
        var emitted = 0;
        while (emitted < cap)
        {
            if (!TryNextRow(out var row, out var lo, out var hi))
            {
                break;
            }

            // The shared frames first: a call's answer is a formatting of state the argument it
            // reads has just moved (D258.4).
            for (var a = 0; a < _arguments.Length; a++)
            {
                _arguments[a].Advance(_run, lo, hi);
            }

            for (var k = 0; k < _calls.Length; k++)
            {
                _calls[k].Emit(_output[_inputTypes.Length + k], _run, row, lo, hi);
            }

            emitted++;
        }

        return emitted;
    }

    /// <summary>The next row whose frame is complete, with that frame.</summary>
    private bool TryNextRow(out long row, out long lo, out long hi)
    {
        row = _computed;
        lo = 0;
        hi = 0;
        if (_computed >= _run.ReadEnd)
        {
            return false;
        }

        if (_computed >= _partitionEnd)
        {
            StartPartition(_computed);
            ScanPartitions();
        }

        _run.PartitionStart = _partitionStart;
        _run.PartitionEnd = _partitionEnd;
        if (_tracksPeers)
        {
            _run.PeerEnd = _peers.Advance(_computed, _run.ReadEnd, _partitionEnd);
            _run.PeerStart = _peers.Start;
        }
        else
        {
            _run.PeerStart = _computed;
            _run.PeerEnd = _computed + 1;
        }

        if (!_frame.TryBounds(_run, _computed, out lo, out hi))
        {
            return false;
        }

        _computed++;
        return true;
    }

    /// <summary>Copies a settled run of input rows into the output columns, in one go per column.</summary>
    private void AppendHeldRows(long first, int count)
    {
        var start = _hold.Local(first);
        for (var c = 0; c < _inputTypes.Length; c++)
        {
            _output[c].Append(_hold.Column(c).View.Slice(start, count), count);
        }
    }

    /// <summary>
    /// Starts the next output batch. The slot is rewound first, so the batch just handed on is
    /// already a generation behind before any copier underneath it is overwritten (D61).
    /// </summary>
    private void BeginOutput(int batchSize)
    {
        _slot.Begin(0);
        foreach (var copier in _output)
        {
            copier.Begin(batchSize);
        }
    }

    private void StartPartition(long start)
    {
        _partitionStart = start;
        _partitionEnd = StreamingWindowRun.Open;
        _run.PartitionStart = start;
        _run.PartitionEnd = StreamingWindowRun.Open;
        _peers.StartPartition(start);
        _frame.StartPartition(start);
        foreach (var argument in _arguments)
        {
            argument.StartPartition(start);
        }

        foreach (var call in _calls)
        {
            call.StartPartition(start);
        }
    }

    /// <summary>
    /// Finds where the current partition ends, reading each row once over the whole run. A partition
    /// that has not ended within what has been read stays open, and an open partition is what makes a
    /// <c>RANGE</c> frame's last peer group wait.
    /// </summary>
    private void ScanPartitions()
    {
        if (_partitionEnd != StreamingWindowRun.Open)
        {
            return;
        }

        if (_partitions.ColumnCount != 0)
        {
            var cursor = Math.Max(_scanned, _partitionStart + 1);
            while (cursor < _run.ReadEnd)
            {
                if (!_partitions.SameGroup(_hold.Local(cursor - 1), _hold.Local(cursor)))
                {
                    _scanned = cursor;
                    _partitionEnd = cursor;
                    return;
                }

                cursor++;
            }

            _scanned = cursor;
        }
        else
        {
            _scanned = _run.ReadEnd;
        }

        if (_inputDone)
        {
            _partitionEnd = _run.ReadEnd;
        }
    }

    /// <summary>
    /// Releases everything below the oldest row anything can still read: the frame's lower bound, the
    /// furthest <c>LAG</c>, the open peer group, the partition scan's predecessor and the last row
    /// read, which the next batch is compared against.
    /// </summary>
    private void ReleaseHold()
    {
        var keep = _computed;
        if (_readsFrameRows)
        {
            keep = Math.Min(keep, _frame.WaterMark(_run, _computed - 1));
        }

        if (_rowsBehind > 0)
        {
            keep = Math.Min(keep, _computed - _rowsBehind);
        }

        if (_tracksPeers)
        {
            keep = Math.Min(keep, _peers.Retain);
        }

        // Rows already settled have been copied into the output columns, so the hold owes the output
        // nothing: what it still owes is the frame, the peer group and the two scans below.
        keep = Math.Min(keep, _run.ReadEnd - 1);
        keep = Math.Min(keep, _scanned - 1);
        if (keep > _hold.Start)
        {
            _hold.Release(keep);
        }
    }

    /// <summary>
    /// The promise the plan made, checked as the rows arrive rather than over a buffered copy: rows
    /// are grouped into contiguous partitions and ordered within each one. A violation is a planner
    /// bug that would otherwise produce a wrong answer quietly.
    /// </summary>
    private void Verify(long from)
    {
        Span<byte> left = stackalloc byte[16];
        Span<byte> right = stackalloc byte[16];
        _seen ??= new HashSet<string>(StringComparer.Ordinal);
        if (from == 0 && _spec.PartitionKeys.Length > 0)
        {
            _seen.Clear();
            _seen.Add(PartitionKey(0));
        }

        for (var i = Math.Max(from, 1); i < _run.ReadEnd; i++)
        {
            var previous = _hold.Local(i - 1);
            var current = _hold.Local(i);
            if (!_partitions.SameGroup(previous, current))
            {
                if (_spec.PartitionKeys.Length > 0 && !_seen.Add(PartitionKey(i)))
                {
                    throw new InvalidOperationException(
                        $"the window's input is not partitioned: the run starting at row {i} repeats "
                        + "a partition key that has already ended");
                }

                continue;
            }

            foreach (var key in _spec.Order)
            {
                var column = _hold.Column(key.Column);
                var previousValid = column.IsValid(previous);
                var currentValid = column.IsValid(current);
                if (previousValid != currentValid)
                {
                    if (previousValid == key.NullsFirst)
                    {
                        throw new InvalidOperationException(
                            $"the window's input is not ordered: row {i} puts a "
                            + (currentValid ? "value" : "NULL")
                            + $" after a {(previousValid ? "value" : "NULL")} on column {key.Column}");
                    }

                    break;
                }

                if (!currentValid)
                {
                    break;
                }

                var comparison = LaneComparer.Compare(
                    column.Kind, column.Lane(previous, left), column.Lane(current, right));
                if (key.Descending)
                {
                    comparison = -comparison;
                }

                if (comparison > 0)
                {
                    throw new InvalidOperationException(
                        $"the window's input is not ordered on column {key.Column} at row {i}");
                }

                if (comparison < 0)
                {
                    break;
                }
            }
        }
    }

    private string PartitionKey(long row)
    {
        Span<byte> scratch = stackalloc byte[16];
        var local = _hold.Local(row);
        var text = new System.Text.StringBuilder();
        foreach (var key in _spec.PartitionKeys)
        {
            var column = _hold.Column(key);
            text.Append(column.IsValid(local)
                ? Convert.ToHexStringLower(column.Lane(local, scratch))
                : "null");
            text.Append('|');
        }

        return text.ToString();
    }
}

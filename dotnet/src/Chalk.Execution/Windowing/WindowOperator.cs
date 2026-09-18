using Chalk.Catalog;
using Chalk.Execution.Aggregation;
using Chalk.Execution.Operators;
using Chalk.Execution.Vectors;
using Chalk.Sources;
using ArrowSchema = Apache.Arrow.Schema;

namespace Chalk.Execution.Windowing;

/// <summary>
/// The window operator of §4: blocking, single-threaded and arena-backed.
///
/// <para>
/// It reads its whole input into owned columns, exactly as <c>SortOperator</c> does, because a frame
/// can reach forwards as well as backwards. The input arrives ordered by (partition keys, order
/// keys) — the plan guarantees it, and <see cref="VerifyInputOrder"/> checks it in Debug builds and
/// under an <c>AppContext</c> switch — so partitions are contiguous runs and the whole computation is
/// a walk from one run to the next.
/// </para>
/// <para>
/// Per partition the operator computes the peer groups and each row's frame <em>once</em>, and every
/// call reads them; then it emits the input's columns and the call columns together in batches of
/// <c>BatchSize</c>. Every array that scales with the data is rented from the execution's arena, so
/// <c>MaxBytes</c> applies and the arena is empty again when the query ends (ADR 0012).
/// </para>
/// </summary>
internal sealed class WindowOperator : OperatorBase
{
    /// <summary>
    /// Whether to check that the input really arrives in the order the plan promised. On in Debug
    /// builds, and switchable with <c>AppContext.SetSwitch("Chalk.Execution.VerifyWindowInputOrder")</c>
    /// — a wrong order is a silent wrong answer, so it is worth being able to turn on in the field.
    /// </summary>
    internal static bool VerifyInputOrder { get; set; } =
        AppContext.TryGetSwitch("Chalk.Execution.VerifyWindowInputOrder", out var on) ? on : DebugBuild;

    private const bool DebugBuild =
#if DEBUG
        true;
#else
        false;
#endif

    private readonly IBatchOperator _input;
    private readonly WindowSpec _spec;
    private readonly WindowCallEvaluator[] _calls;
    private readonly ColumnCopier[] _buffer;
    private readonly ColumnCopier[] _output;
    private readonly ChalkType[] _inputTypes;
    private readonly ColumnarBatch _slot;
    private readonly ColumnView[] _table;

    /// <summary>The plan's estimate of the input's rows, which is what this window buffers (D258.3).</summary>
    private readonly double _estimatedRows;

    public WindowOperator(
        OperatorContext context,
        string path,
        ArrowSchema schema,
        IReadOnlyList<ChalkType> outputTypes,
        IReadOnlyList<ChalkType> inputTypes,
        IBatchOperator input,
        WindowSpec spec,
        WindowCallEvaluator[] calls,
        double estimatedRows = 0)
        : base(context, schema, outputTypes, path)
    {
        _input = input;
        _spec = spec;
        _calls = calls;
        _estimatedRows = estimatedRows;
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
        var arena = Context.Arena;
        Context.Stats.RecordWindowPath(WindowPath.Buffered);
        var rows = await ConcatenateAsync(ct).ConfigureAwait(false);
        var table = _table;

        var peerStart = System.Array.Empty<int>();
        var peerEnd = System.Array.Empty<int>();
        var frameLo = System.Array.Empty<int>();
        var frameHi = System.Array.Empty<int>();
        var started = false;

        try
        {
            if (rows == 0)
            {
                yield break;
            }

            peerStart = arena.Rent<int>(rows);
            peerEnd = arena.Rent<int>(rows);
            frameLo = arena.Rent<int>(rows);
            frameHi = arena.Rent<int>(rows);
            var columns = new WindowColumn[table.Length];
            for (var c = 0; c < table.Length; c++)
            {
                columns[c] = new WindowColumn(table[c], rows, _inputTypes[c]);
            }

            var partitions = new WindowRowComparer([.. _spec.PartitionKeys.Select(k => columns[k])]);
            var order = new WindowRowComparer([.. _spec.Order.Select(k => columns[k.Column])]);

            if (VerifyInputOrder)
            {
                Verify(columns, partitions, rows);
            }

            var run = new WindowRun
            {
                Columns = columns,
                RowCount = rows,
                PeerStart = peerStart,
                PeerEnd = peerEnd,
                FrameLo = frameLo,
                FrameHi = frameHi,
                Exclusion = _spec.Exclusion,
                Parameters = Context.Parameters,
            };

            foreach (var call in _calls)
            {
                call.Begin(arena, rows);
            }

            started = true;
            var frames = new WindowFrameBuilder(_spec, run, arena, rows);
            try
            {
                var start = 0;
                while (start < rows)
                {
                    ct.ThrowIfCancellationRequested();
                    var end = start + 1;
                    while (end < rows && partitions.SameGroup(end - 1, end))
                    {
                        end++;
                    }

                    WindowFrames.Peers(order, start, end, peerStart, peerEnd);
                    frames.Build(start, end);
                    foreach (var call in _calls)
                    {
                        call.Compute(run, start, end);
                    }

                    start = end;
                }
            }
            finally
            {
                frames.Release(arena);
            }

            var batchSize = Context.Settings.BatchSize;
            for (var offset = 0; offset < rows; offset += batchSize)
            {
                ct.ThrowIfCancellationRequested();
                var count = Math.Min(batchSize, rows - offset);
                _slot.Begin(count);
                for (var c = 0; c < table.Length; c++)
                {
                    // A contiguous run of the concatenated input: one slice appended in one copy, not a
                    // gather through an index array the size of the whole input (design 33 §3.8).
                    var input = _output[c];
                    input.Begin();
                    input.Append(table[c].Slice(offset, count), count);
                    _slot.Set(c, input.FinishView());
                }

                for (var k = 0; k < _calls.Length; k++)
                {
                    var copier = _output[table.Length + k];
                    copier.Begin();
                    for (var i = offset; i < offset + count; i++)
                    {
                        _calls[k].Emit(copier, run, i);
                    }

                    _slot.Set(table.Length + k, copier.FinishView());
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

            arena.Return(frameHi);
            arena.Return(frameLo);
            arena.Return(peerEnd);
            arena.Return(peerStart);
        }
    }

    /// <summary>Reads the whole input into this operator's own columns and returns how many rows.</summary>
    private async ValueTask<int> ConcatenateAsync(CancellationToken ct)
    {
        // The whole input is what this operator holds, and the plan says how big it is: the buffers
        // start at the estimate instead of doubling a batch at a time into it (D258.3).
        var capacity = PlanCapacity.Rows(Context, _estimatedRows, _inputTypes);
        foreach (var copier in _buffer)
        {
            copier.Begin(capacity);
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

    /// <summary>
    /// Checks the promise the plan made: rows are grouped into contiguous partitions and ordered
    /// within each one. A violation is a planner bug that would otherwise produce a wrong answer
    /// quietly, so it names the window rather than the row.
    /// </summary>
    private void Verify(WindowColumn[] columns, WindowRowComparer partitions, int rows)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        Span<byte> left = stackalloc byte[16];
        Span<byte> right = stackalloc byte[16];

        for (var i = 1; i < rows; i++)
        {
            if (!partitions.SameGroup(i - 1, i))
            {
                continue;
            }

            foreach (var key in _spec.Order)
            {
                var column = columns[key.Column];
                var previousValid = column.IsValid(i - 1);
                var currentValid = column.IsValid(i);
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
                    column.Kind, column.Lane(i - 1, left), column.Lane(i, right));
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

        // Partition runs must not repeat: a key that reappears after another one would split a
        // partition in two and give every row the wrong frame.
        if (_spec.PartitionKeys.Length > 0)
        {
            for (var i = 0; i < rows; i++)
            {
                if (i > 0 && partitions.SameGroup(i - 1, i))
                {
                    continue;
                }

                if (!seen.Add(PartitionKey(columns, i)))
                {
                    throw new InvalidOperationException(
                        $"the window's input is not partitioned: the run starting at row {i} repeats "
                        + "a partition key that has already ended");
                }
            }
        }
    }

    private string PartitionKey(WindowColumn[] columns, int row)
    {
        Span<byte> scratch = stackalloc byte[16];
        var text = new System.Text.StringBuilder();
        foreach (var key in _spec.PartitionKeys)
        {
            var column = columns[key];
            text.Append(column.IsValid(row)
                ? Convert.ToHexStringLower(column.Lane(row, scratch))
                : "null");
            text.Append('|');
        }

        return text.ToString();
    }
}

using Chalk.Catalog;
using Chalk.Execution.Operators;
using Chalk.Sources;
using ArrowSchema = Apache.Arrow.Schema;

namespace Chalk.Execution.Maintenance;

/// <summary>
/// What a maintained read keeps (D343, design 68 §3): the table state it read at the last update,
/// held across updates, and whether this update found the table rewritten.
/// </summary>
internal sealed class ReadState : IMaintainedState
{
    private object? _held;

    public ReadState(ITrackedSource source, string sourceId, string table, ScanRequest request)
    {
        Source = source;
        SourceId = sourceId;
        Table = table;
        Request = request;
    }

    public ITrackedSource Source { get; }

    public string SourceId { get; }

    public string Table { get; }

    public ScanRequest Request { get; }

    /// <summary>
    /// Whether the update now running found the table rewritten, so that its changes are every old
    /// row out and every new row in: an as-of join then places its right rows afresh (§5). A read of
    /// appends only says so too when it reads the table for the first time.
    /// </summary>
    public bool Rewritten { get; set; }

    /// <summary>The state the last update read, or null before the first.</summary>
    public object? Held => _held;

    public long Bytes => 0;

    /// <summary>Holds <paramref name="pinned"/>, this update's state, until the next update, letting the old one go.</summary>
    public void Hold(object pinned)
    {
        if (ReferenceEquals(pinned, _held))
        {
            return;
        }

        var retained = Source.Retain(Table, pinned);
        if (_held is { } old)
        {
            Source.Unpin(Table, old);
        }

        _held = retained;
    }

    public void Discard()
    {
        if (_held is { } old)
        {
            _held = null;
            Source.Unpin(Table, old);
        }

        Rewritten = false;
    }
}

/// <summary>
/// The changes of one table read in one update: at the first, every row in; after an append, the
/// rows it added; after anything else, every old row out and every new row in (design 68 §3).
/// </summary>
/// <remarks>
/// A read of appends only, as a maintained as-of seek reads its table's keys, gives the rows an
/// append added and nothing else: at the first read and after a rewrite it gives no rows and says
/// <see cref="ReadState.Rewritten"/>.
/// </remarks>
internal sealed class ReadChanges : OperatorBase
{
    private readonly ReadState _state;
    private readonly bool _appendsOnly;
    private readonly ColumnarBatch _slot;
    private readonly int _weightColumn;
    private byte[] _plus = [];
    private byte[] _minus = [];

    public ReadChanges(
        OperatorContext context,
        string path,
        ArrowSchema schema,
        IReadOnlyList<ChalkType> types,
        ReadState state,
        bool appendsOnly = false)
        : base(context, schema, types, path)
    {
        _state = state;
        _appendsOnly = appendsOnly;
        _slot = NewOutput();
        _weightColumn = types.Count - 1;
    }

    protected override ValueTask DisposeCoreAsync() => default;

    protected override async IAsyncEnumerable<ColumnarBatch> RunAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var now = Context.ReadSet?.Pinned(_state.SourceId, _state.Table)
            ?? throw new InvalidOperationException(
                $"a maintained read of '{_state.Table}' found no state pinned for it in this update.");
        var held = _state.Held;
        _state.Rewritten = false;

        if (held is null)
        {
            _state.Rewritten = _appendsOnly;
            if (!_appendsOnly)
            {
                await foreach (var batch in Scan(now, 0, +1, ct))
                {
                    yield return batch;
                }
            }
        }
        else if (!ReferenceEquals(held, now))
        {
            var from = _state.Source.AppendedFrom(_state.Table, held, now);
            if (from >= 0)
            {
                await foreach (var batch in Scan(now, from, +1, ct))
                {
                    yield return batch;
                }
            }
            else
            {
                _state.Rewritten = true;
                if (!_appendsOnly)
                {
                    await foreach (var batch in Scan(held, 0, -1, ct))
                    {
                        yield return batch;
                    }

                    await foreach (var batch in Scan(now, 0, +1, ct))
                    {
                        yield return batch;
                    }
                }
            }
        }

        _state.Hold(now);
    }

    private async IAsyncEnumerable<ColumnarBatch> Scan(
        object pinned, long from, long weight, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var scan = _state.Source.ScanFrom(_state.Table, pinned, from, _state.Request, ScanContextFor(Context));
        await using (scan.ConfigureAwait(false))
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (!scan.TryNext(_slot) && !await scan.WaitAsync(_slot, ct).ConfigureAwait(false))
                {
                    yield break;
                }

                _slot.Set(_weightColumn, Weight(weight, _slot.RowCount));
                yield return _slot;
            }
        }
    }

    /// <summary>A weight column of <paramref name="rows"/> rows, every one <paramref name="weight"/>.</summary>
    private ColumnView Weight(long weight, int rows)
    {
        ref var lanes = ref weight > 0 ? ref _plus : ref _minus;
        if (lanes.Length < rows * sizeof(long))
        {
            var count = Math.Max(rows, lanes.Length / sizeof(long) * 2);
            lanes = new byte[count * sizeof(long)];
            System.Runtime.InteropServices.MemoryMarshal.Cast<byte, long>(lanes.AsSpan()).Fill(weight);
        }

        return new ColumnView
        {
            Type = Weights.Type,
            Length = rows,
            Values = lanes.AsMemory(0, rows * sizeof(long)),
            NullCount = 0,
        };
    }
}

/// <summary>What a maintained operator keeps between updates, and how it is let go after a failure.</summary>
internal interface IMaintainedState
{
    /// <summary>Bytes held, against the view's state budget.</summary>
    long Bytes { get; }

    /// <summary>Forgets everything: after an update that failed part way, the next starts from nothing (§7).</summary>
    void Discard();
}

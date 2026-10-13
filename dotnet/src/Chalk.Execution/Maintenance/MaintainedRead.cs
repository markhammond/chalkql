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

    public ReadState(ITrackedSource source, string sourceId, string table, ScanRequest request, TableDiffs diffs)
    {
        Source = source;
        SourceId = sourceId;
        Table = table;
        Request = request;
        Diffs = diffs;
    }

    /// <summary>What replacements changed, found once in an update for every read of a table (§17).</summary>
    public TableDiffs Diffs { get; }

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

    /// <summary>
    /// Whether a rewrite gives the new rows only, without every old row out: set when what reads the
    /// table builds its state again from them, as an aggregate over the table does (design 68 §14).
    /// </summary>
    public bool NewRowsOnRewrite { get; set; }

    /// <summary>
    /// Whether any change but an append is read as a rewrite, never as the rows a replacement took out
    /// and appended (§15): set when what reads the table places its rows by their order in it, as an
    /// as-of join reading its right side whole does.
    /// </summary>
    public bool WholeOnRewrite { get; set; }

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
/// rows it added; after a replacement that kept the rest of the rows in their order, the rows it took
/// out and the rows it appended (§15); after anything else, every old row out and every new row in
/// (design 68 §3).
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
            else if (!_state.WholeOnRewrite && _state.Diffs.DiffFrom(_state, held, now) is { } diff)
            {
                // A replacement that kept the rest of the rows in their order, as a compaction does: the
                // rows it took out, and the rows it appended.
                await foreach (var batch in ScanAt(held, diff.Removed, ct))
                {
                    yield return batch;
                }

                await foreach (var batch in Scan(now, diff.AppendedFrom, +1, ct))
                {
                    yield return batch;
                }
            }
            else
            {
                _state.Rewritten = true;
                if (!_appendsOnly)
                {
                    if (!_state.NewRowsOnRewrite)
                    {
                        await foreach (var batch in Scan(held, 0, -1, ct))
                        {
                            yield return batch;
                        }
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

    /// <summary>The rows of <paramref name="pinned"/> at <paramref name="positions"/>, taken out.</summary>
    private async IAsyncEnumerable<ColumnarBatch> ScanAt(
        object pinned, int[] positions, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        if (positions.Length == 0)
        {
            yield break;
        }

        var scan = _state.Source.ScanAt(_state.Table, pinned, positions, _state.Request, ScanContextFor(Context));
        await using (scan.ConfigureAwait(false))
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (!scan.TryNext(_slot) && !await scan.WaitAsync(_slot, ct).ConfigureAwait(false))
                {
                    yield break;
                }

                _slot.Set(_weightColumn, Weight(-1, _slot.RowCount));
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

/// <summary>
/// What replacements changed in an update's tables (design 68 §17): a table read by several operators
/// is walked once, and the first read's answer is every other read's. Kept for one update only, so no
/// state a replacement left behind is held past it.
/// </summary>
internal sealed class TableDiffs
{
    private readonly Dictionary<(string Source, string Table), (object Earlier, object Later, TableDiff? Diff)> _found = [];
    private readonly Lock _gate = new();

    /// <summary>What a replacement changed between two of a table's states, as its source says.</summary>
    public TableDiff? DiffFrom(ReadState read, object earlier, object later)
    {
        lock (_gate)
        {
            if (_found.TryGetValue((read.SourceId, read.Table), out var known)
                && ReferenceEquals(known.Earlier, earlier)
                && ReferenceEquals(known.Later, later))
            {
                return known.Diff;
            }

            var diff = read.Source.DiffFrom(read.Table, earlier, later);
            _found[(read.SourceId, read.Table)] = (earlier, later, diff);
            return diff;
        }
    }

    /// <summary>Forgets what this update found, and the states it named.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _found.Clear();
        }
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

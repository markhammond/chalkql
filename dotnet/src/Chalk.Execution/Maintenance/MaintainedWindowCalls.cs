using Chalk.Catalog;
using Chalk.Execution.Operators;
using Chalk.Sources;
using ArrowSchema = Apache.Arrow.Schema;

namespace Chalk.Execution.Maintenance;

/// <summary>
/// A window rel's calls, maintained (design 77 §2, phase 1): its input rows by partition, and the rows it
/// gave. An update computes again each partition its input's changes touch, whole, with the batch
/// window's own calls and frames over the partition's rows in order, and gives what differs from what it
/// gave for those partitions. A value is the one a recompute gives by construction.
/// </summary>
/// <remarks>
/// <para>
/// Only windows whose churn is bounded are admitted (§1): every frame ends at or before the current row,
/// or a bounded number of rows or a bounded range after it. A call whose value depends on which of two
/// rows that tie in the order comes first is admitted only where the order cannot tie (§4).
/// </para>
/// <para>
/// A call whose arguments or frame read the clock, or a parameter a view binds again, computes every
/// partition again when one moves.
/// </para>
/// </remarks>
internal sealed class WindowCallsState : IMaintainedState
{
    private readonly KeyColumns _inputPartition;
    private readonly int[] _partitionColumns;
    private readonly KeyEncoder _encoder = new();
    private readonly Dictionary<byte[], HashSet<int>> _inputByPartition = new(ByteKeys.Instance);
    private readonly Dictionary<byte[], HashSet<int>> _givenByPartition = new(ByteKeys.Instance);

    /// <param name="inputTypes">The window's input columns.</param>
    /// <param name="outputTypes">Its output columns: the input's, then each call's.</param>
    /// <param name="partitionKeys">The input columns it partitions by, which the output keeps at the same places.</param>
    /// <param name="readsMoving">Whether a call reads the clock or a rebindable parameter.</param>
    /// <param name="batchSize">How many rows a batch of changes holds.</param>
    public WindowCallsState(
        IReadOnlyList<ChalkType> inputTypes,
        IReadOnlyList<ChalkType> outputTypes,
        IReadOnlyList<int> partitionKeys,
        bool readsMoving,
        int batchSize)
    {
        _partitionColumns = [.. partitionKeys];
        _inputPartition = new KeyColumns(inputTypes, _partitionColumns);
        ReadsMoving = readsMoving;
        Input = new HeldRows(inputTypes) { Changed = (slot, added) => Index(_inputByPartition, Input!.Rows, slot, added) };
        Given = new HeldRows(outputTypes) { Changed = (slot, added) => Index(_givenByPartition, Given!.Rows, slot, added) };
        InputChanges = new ChangeSet(inputTypes);
        Replay = new ChangeWriter(inputTypes, batchSize);
        Now = new ChangeSet(outputTypes);
        Output = new ChangeWriter(outputTypes, batchSize);
    }

    /// <summary>Every input row held, with its count.</summary>
    public HeldRows Input { get; }

    /// <summary>Every row the window gave, with its count.</summary>
    public HeldRows Given { get; }

    public ChangeSet InputChanges { get; }

    /// <summary>The rows of the partitions this update computes again, each copy once.</summary>
    public ChangeWriter Replay { get; }

    /// <summary>What the window gave for those partitions in this update, added up.</summary>
    public ChangeSet Now { get; }

    public ChangeWriter Output { get; }

    /// <summary>Whether a call reads a value that moves between updates.</summary>
    public bool ReadsMoving { get; }

    /// <summary>The clock and the bindings the window last ran at, or null before its first update.</summary>
    public Moment? RanAt { get; set; }

    /// <summary>The partitions held, each by its key.</summary>
    public IEnumerable<byte[]> Partitions => _inputByPartition.Keys;

    public long Bytes =>
        Input.Bytes + Given.Bytes + InputChanges.Bytes + Replay.Bytes + Now.Bytes + Output.Bytes
        + Indexed(_inputByPartition) + Indexed(_givenByPartition);

    /// <summary>The key of the partition a batch row falls in. NULLs partition together.</summary>
    public byte[] PartitionOf(ReadOnlySpan<ColumnView> columns, int row)
    {
        _inputPartition.Encode(_encoder, columns, row, normalise: false);
        return _encoder.Span.ToArray();
    }

    /// <summary>The input rows held in a partition, by slot; none when it holds none.</summary>
    public IReadOnlyCollection<int> InputOf(byte[] partition) =>
        _inputByPartition.TryGetValue(partition, out var slots) ? slots : [];

    /// <summary>The rows given for a partition, by slot; none when it holds none.</summary>
    public IReadOnlyCollection<int> GivenOf(byte[] partition) =>
        _givenByPartition.TryGetValue(partition, out var slots) ? slots : [];

    public void Discard()
    {
        Input.Clear();
        Given.Clear();
        _inputByPartition.Clear();
        _givenByPartition.Clear();
        InputChanges.Clear();
        Replay.Clear();
        Now.Clear();
        Output.Clear();
        RanAt = null;
    }

    public void Trim()
    {
        InputChanges.Trim();
        Replay.Trim();
        Now.Trim();
        Output.Trim();
    }

    /// <summary>Keeps a partition's slots in step with the rows a held multiset holds.</summary>
    private void Index(Dictionary<byte[], HashSet<int>> index, RowStore rows, int slot, bool added)
    {
        _inputPartition.Encode(_encoder, rows, _partitionColumns, slot, normalise: false);
        var key = _encoder.Span;
        var lookup = index.GetAlternateLookup<ReadOnlySpan<byte>>();
        if (added)
        {
            if (!lookup.TryGetValue(key, out var slots))
            {
                slots = [];
                index[key.ToArray()] = slots;
            }

            slots.Add(slot);
            return;
        }

        if (lookup.TryGetValue(key, out var held))
        {
            held.Remove(slot);
            if (held.Count == 0)
            {
                lookup.Remove(key);
            }
        }
    }

    /// <summary>What an index of partitions holds: a key, a set and a slot for each entry.</summary>
    private static long Indexed(Dictionary<byte[], HashSet<int>> index)
    {
        var bytes = HeapSizes.Array(index.Count, 24);
        foreach (var (key, slots) in index)
        {
            bytes += HeapSizes.Array(key.Length, 1) + HeapSizes.Array(slots.Count, 12) + 64;
        }

        return bytes;
    }
}

/// <summary>Replays the rows a maintained window hands the batch window in this update.</summary>
internal sealed class WindowReplay : OperatorBase, IBatchesInHand
{
    private readonly WindowCallsState _state;
    private readonly BatchesInHand _run;

    public WindowReplay(OperatorContext context, string path, ArrowSchema schema, IReadOnlyList<ChalkType> types, WindowCallsState state)
        : base(context, schema, types, path)
    {
        _state = state;
        _run = new BatchesInHand(this);
    }

    protected override ValueTask DisposeCoreAsync() => default;

    protected override IAsyncEnumerator<ColumnarBatch> Begin(CancellationToken ct) => _run.Begin(ct);

    ValueTask<int> IBatchesInHand.PrepareAsync(CancellationToken ct) => new(_state.Replay.BatchCount);

    ColumnarBatch IBatchesInHand.Batch(int index) => _state.Replay.Batch(index);
}

/// <summary>A maintained window's update: the partitions its input's changes touch, computed again.</summary>
internal sealed class WindowCallChanges : MaintainedOperator, IKeepsScratch
{
    private readonly WindowCallsState _state;
    private readonly IBatchOperator _input;
    private readonly IBatchOperator _body;
    private readonly HashSet<byte[]> _touched = new(ByteKeys.Instance);
    private readonly HashSet<int> _matched = [];
    private readonly List<int> _gone = [];

    public WindowCallChanges(
        OperatorContext context,
        string path,
        ArrowSchema schema,
        IReadOnlyList<ChalkType> types,
        WindowCallsState state,
        IBatchOperator input,
        IBatchOperator body)
        : base(context, path, schema, types)
    {
        _state = state;
        _input = input;
        _body = body;
        context.Scratch.Add(this);
    }

    /// <summary>The partitions and rows the last update touched, back to none.</summary>
    public void Trim()
    {
        _touched.Clear();
        _touched.TrimExcess();
        _matched.Clear();
        _matched.TrimExcess();
        _gone.Clear();
        _gone.TrimExcess();
    }

    protected override async ValueTask DisposeCoreAsync()
    {
        await _input.DisposeAsync().ConfigureAwait(false);
        await _body.DisposeAsync().ConfigureAwait(false);
    }

    protected override async ValueTask<ChangeWriter> ComputeAsync(CancellationToken ct)
    {
        var state = _state;
        await DrainAsync(_input, state.InputChanges, ct).ConfigureAwait(false);
        var changes = state.InputChanges;
        _touched.Clear();
        for (var row = 0; row < changes.Count; row++)
        {
            var weight = changes.Weight(row);
            if (weight != 0)
            {
                _touched.Add(state.PartitionOf(changes.Columns, row));
                state.Input.Add(changes.Columns, row, weight);
            }
        }

        // A value a call reads moved: every partition, and every partition it gave rows for, again.
        var now = Moment.Of(Context);
        if (state.ReadsMoving && state.RanAt != now)
        {
            _touched.UnionWith(state.Partitions);
        }

        state.RanAt = now;
        var output = state.Output;
        output.Clear();
        if (_touched.Count == 0)
        {
            return output;
        }

        // Each touched partition's rows, a row held twice given twice: the window counts rows, not values.
        state.Replay.Clear();
        foreach (var partition in _touched)
        {
            foreach (var slot in state.InputOf(partition))
            {
                for (var copy = state.Input.Count(slot); copy > 0; copy--)
                {
                    state.Replay.Write(state.Input.Rows, slot, 1);
                }
            }
        }

        var given = state.Now;
        given.Clear();
        await foreach (var batch in _body.ExecuteAsync(ct).ConfigureAwait(false))
        {
            given.Add(batch);
        }

        given.Seal();

        // What the touched partitions give now, against what they gave: the rows that differ.
        _matched.Clear();
        for (var row = 0; row < given.Count; row++)
        {
            var weight = given.Weight(row);
            var held = state.Given.Find(given.Columns, row);
            if (held >= 0)
            {
                _matched.Add(held);
                weight -= state.Given.Count(held);
            }

            if (weight != 0)
            {
                output.Write(given.Columns, row, weight);
            }
        }

        _gone.Clear();
        foreach (var partition in _touched)
        {
            foreach (var slot in state.GivenOf(partition))
            {
                if (!_matched.Contains(slot))
                {
                    _gone.Add(slot);
                }
            }
        }

        foreach (var slot in _gone)
        {
            output.Write(state.Given.Rows, slot, -state.Given.Count(slot));
        }

        // The rows given now take the place of those they differ from: those gone first, so that a slot
        // they free can hold a row given now.
        foreach (var slot in _gone)
        {
            state.Given.Take(slot);
        }

        for (var row = 0; row < given.Count; row++)
        {
            var held = state.Given.Find(given.Columns, row);
            var weight = given.Weight(row) - (held >= 0 ? state.Given.Count(held) : 0);
            state.Given.Add(given.Columns, row, weight);
        }

        return output;
    }
}

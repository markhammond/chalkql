using Chalk.Catalog;
using Chalk.Execution.Operators;
using Chalk.Sources;
using ArrowSchema = Apache.Arrow.Schema;

namespace Chalk.Execution.Maintenance;

/// <summary>
/// A filter or a projection that reads the clock over a table (D343, design 68 §6): it keeps its input
/// rows and what it gave. While the clock stands, it is linear and runs on its input's changes; when
/// the clock moves, it runs on every input row again and gives what differs from what it gave.
/// </summary>
/// <remarks>
/// The node's own operator runs as it is, over a replay of the rows this state hands it: the changes,
/// or every row held, each with its count as its weight.
/// </remarks>
internal sealed class ClockedState : IMaintainedState
{
    public ClockedState(IReadOnlyList<ChalkType> inputTypes, IReadOnlyList<ChalkType> outputTypes, int batchSize)
    {
        Input = new HeldRows(inputTypes);
        Given = new HeldRows(outputTypes);
        InputChanges = new ChangeSet(inputTypes);
        Replay = new ChangeWriter(inputTypes, batchSize);
        Now = new ChangeSet(outputTypes);
        Output = new ChangeWriter(outputTypes, batchSize);
    }

    /// <summary>Every input row held, with its count.</summary>
    public HeldRows Input { get; }

    /// <summary>Every row the node gave, with its count.</summary>
    public HeldRows Given { get; }

    public ChangeSet InputChanges { get; }

    /// <summary>What the node's operator reads in this update.</summary>
    public ChangeWriter Replay { get; }

    /// <summary>What the node's operator gave in this update, added up.</summary>
    public ChangeSet Now { get; }

    public ChangeWriter Output { get; }

    /// <summary>The clock and the bindings the node last ran at, or null before its first update.</summary>
    public Moment? RanAt { get; set; }

    public long Bytes => Input.Bytes + Given.Bytes + InputChanges.Bytes + Replay.Bytes + Now.Bytes + Output.Bytes;

    public void Discard()
    {
        Input.Clear();
        Given.Clear();
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
}

/// <summary>Distinct rows with how many times each is held: a multiset kept between updates.</summary>
internal sealed class HeldRows
{
    private readonly KeyColumns _identity;
    private readonly KeyEncoder _encoder = new();
    private readonly ByteMap _slots = new();
    private long[] _counts = [];

    public HeldRows(IReadOnlyList<ChalkType> types)
    {
        Rows = new RowStore(types);
        _identity = KeyColumns.All(types);
    }

    public RowStore Rows { get; }

    /// <summary>
    /// Told of each slot as it comes to hold a row, after it is filled, and as it lets one go, before
    /// it is emptied: what keeps an index over the rows in step with them.
    /// </summary>
    public Action<int, bool>? Changed { get; set; }

    /// <summary>The rows, their counts and identities, at their size (F223).</summary>
    public long Bytes => Rows.Bytes + HeapSizes.Array(_counts.Length, sizeof(long)) + _slots.Bytes;

    public long Count(int slot) => _counts[slot];

    /// <summary>The slots held now, and their counts.</summary>
    public IEnumerable<(int Slot, long Count)> All()
    {
        for (var entry = 0; entry < _slots.Entries; entry++)
        {
            if (_slots.IsLive(entry))
            {
                var slot = _slots.Value(entry);
                yield return (slot, _counts[slot]);
            }
        }
    }

    /// <summary>The slot holding a row equal to <paramref name="row"/>, bit for bit, or -1.</summary>
    public int Find(ReadOnlySpan<ColumnView> columns, int row)
    {
        _identity.Encode(_encoder, columns, row, normalise: false);
        return _slots.TryGet(_encoder.Span, out var slot) ? slot : -1;
    }

    /// <summary>Adds <paramref name="weight"/> copies of a row, or takes them out.</summary>
    public void Add(ReadOnlySpan<ColumnView> columns, int row, long weight)
    {
        if (weight == 0)
        {
            return;
        }

        _identity.Encode(_encoder, columns, row, normalise: false);
        ref var held = ref _slots.GetOrAdd(_encoder.Span, out var added);
        if (!added)
        {
            var at = held;
            _counts[at] += weight;
            if (_counts[at] < 0)
            {
                throw new InvalidOperationException("a maintained operator took out more copies of a row than it held.");
            }

            if (_counts[at] == 0)
            {
                Changed?.Invoke(at, false);
                _slots.Remove(_encoder.Span, out _);
                Rows.Remove(at);
            }

            return;
        }

        if (weight < 0)
        {
            throw new InvalidOperationException("a maintained operator was asked to take out a row it does not hold.");
        }

        var slot = Rows.Add(columns, row);
        held = slot;
        if (_counts.Length < Rows.Slots)
        {
            Array.Resize(ref _counts, Math.Max(Rows.Slots, Math.Max(16, _counts.Length * 2)));
        }

        _counts[slot] = weight;
        Changed?.Invoke(slot, true);
    }

    /// <summary>Takes out every copy of the row <paramref name="slot"/> holds.</summary>
    public void Take(int slot)
    {
        _identity.Encode(_encoder, Rows, _identity.Columns, slot, normalise: false);
        Changed?.Invoke(slot, false);
        _slots.Remove(_encoder.Span, out _);
        Rows.Remove(slot);
        _counts[slot] = 0;
    }

    public void Clear()
    {
        Rows.Clear();
        _slots.Clear();
    }
}

/// <summary>Replays the rows a clocked node hands its own operator in this update.</summary>
internal sealed class ReplayChanges : OperatorBase, IBatchesInHand
{
    private readonly ClockedState _state;
    private readonly BatchesInHand _run;

    public ReplayChanges(OperatorContext context, string path, ArrowSchema schema, IReadOnlyList<ChalkType> types, ClockedState state)
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

internal sealed class ClockedChanges : MaintainedOperator, IKeepsScratch
{
    private readonly ClockedState _state;
    private readonly IBatchOperator _input;
    private readonly IBatchOperator _body;
    private readonly HashSet<int> _matched = [];

    public ClockedChanges(
        OperatorContext context,
        string path,
        ArrowSchema schema,
        IReadOnlyList<ChalkType> types,
        ClockedState state,
        IBatchOperator input,
        IBatchOperator body)
        : base(context, path, schema, types)
    {
        _state = state;
        _input = input;
        _body = body;
        context.Scratch.Add(this);
    }

    /// <summary>The rows matched in the last update, back to none.</summary>
    public void Trim()
    {
        _matched.Clear();
        _matched.TrimExcess();
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
        for (var row = 0; row < changes.Count; row++)
        {
            state.Input.Add(changes.Columns, row, changes.Weight(row));
        }

        var moved = state.RanAt != Moment.Of(Context);
        state.Replay.Clear();
        if (moved)
        {
            // Every input row held, each once with its count.
            foreach (var (slot, count) in state.Input.All())
            {
                state.Replay.Write(state.Input.Rows, slot, count);
            }
        }
        else
        {
            for (var row = 0; row < changes.Count; row++)
            {
                if (changes.Weight(row) != 0)
                {
                    state.Replay.Write(changes.Columns, row, changes.Weight(row));
                }
            }
        }

        var now = state.Now;
        now.Clear();
        await foreach (var batch in _body.ExecuteAsync(ct).ConfigureAwait(false))
        {
            now.Add(batch);
        }

        now.Seal();

        var output = state.Output;
        output.Clear();
        if (!moved)
        {
            for (var row = 0; row < now.Count; row++)
            {
                var weight = now.Weight(row);
                if (weight != 0)
                {
                    output.Write(now.Columns, row, weight);
                    state.Given.Add(now.Columns, row, weight);
                }
            }

            return output;
        }

        // What the node gives now is the whole of it: give what differs from what it gave.
        _matched.Clear();
        for (var row = 0; row < now.Count; row++)
        {
            var weight = now.Weight(row);
            var held = state.Given.Find(now.Columns, row);
            if (held >= 0)
            {
                _matched.Add(held);
                weight -= state.Given.Count(held);
            }

            if (weight != 0)
            {
                output.Write(now.Columns, row, weight);
            }
        }

        foreach (var (slot, count) in state.Given.All())
        {
            if (!_matched.Contains(slot))
            {
                output.Write(state.Given.Rows, slot, -count);
            }
        }

        state.Given.Clear();
        for (var row = 0; row < now.Count; row++)
        {
            state.Given.Add(now.Columns, row, now.Weight(row));
        }

        state.RanAt = Moment.Of(Context);
        return output;
    }
}

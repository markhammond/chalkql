using System.Runtime.InteropServices;
using Chalk.Catalog;
using Chalk.Execution.Aggregation;
using Chalk.Execution.Operators;
using Chalk.Execution.Vectors;
using Chalk.Sources;
using ArrowSchema = Apache.Arrow.Schema;

namespace Chalk.Execution.Maintenance;

/// <summary>How a call's value at a row is had from its value at the row before a span and its value over the span alone (design 77 §2).</summary>
internal enum WindowSeed
{
    /// <summary>It cannot be: the span is read from the partition's start.</summary>
    None,

    /// <summary>The two added: SUM, SUM0 and COUNT over a running frame. NULL is no value.</summary>
    Add,

    /// <summary>The lesser: MIN, and BOOL_AND, over a running frame. NULL is no value.</summary>
    Least,

    /// <summary>The greater: MAX, and BOOL_OR, over a running frame. NULL is no value.</summary>
    Greatest,

    /// <summary>The span's value plus the rows before it: ROW_NUMBER and RANK.</summary>
    Rows,

    /// <summary>The span's value plus the value before it: DENSE_RANK.</summary>
    Plus,

    /// <summary>The value before it: FIRST_VALUE over a frame from the partition's start.</summary>
    First,

    /// <summary>The value before it, or the span's where that is NULL: FIRST_VALUE IGNORE NULLS from the start.</summary>
    FirstIgnoringNulls,
}

/// <summary>
/// What one change can reach in a maintained window, in rows, from the compiler (design 77 §2). Ahead:
/// how many rows after a changed row may give another value; behind: how many before it. Lookback and
/// lookahead: how many rows before and after a row its value reads. <see cref="Unbounded"/> where a
/// call reaches the partition's end, or reads by an offset this does not count, such as a RANGE.
/// </summary>
internal sealed record WindowReach(int Ahead, int Behind, int Lookback, int Lookahead, WindowSeed[] Seeds)
{
    public const int Unbounded = int.MaxValue;
}

/// <summary>
/// A window rel's calls, maintained (design 77 §2): its input rows, each partition in its order, and each
/// row's call values. An update computes again, by the batch window, only the rows of each touched
/// partition whose values a change can reach, reading the rows their values read; a running call is
/// resumed from the value held at the row before them. It gives what differs.
/// </summary>
/// <remarks>
/// <para>
/// Where a change is in its partition is found by its order key, so a row taken out is placed as a row
/// put in is: at the first row of its peers. From there the compiler's reach bounds the rows whose values
/// may move and the rows those values read, each widened to whole peer groups. An append in order then
/// computes its own rows and their lookback, and gives them; a change in the past computes from it on,
/// where a call runs to the partition's end.
/// </para>
/// <para>
/// A row held twice is given twice; its copies are peers, which every call the window admits gives
/// alike, so each held row keeps one value per call.
/// </para>
/// </remarks>
internal sealed class WindowCallsState : IMaintainedState
{
    private readonly KeyColumns _partitionKey;
    private readonly int[] _partitionColumns;
    private readonly KeyEncoder _encoder = new();
    private readonly Dictionary<byte[], List<int>> _partitions = new(ByteKeys.Instance);

    public WindowCallsState(
        IReadOnlyList<ChalkType> inputTypes,
        IReadOnlyList<ChalkType> callTypes,
        IReadOnlyList<int> partitionKeys,
        SortOrdering order,
        SortOrdering partitionOrder,
        WindowReach reach,
        bool readsMoving,
        int batchSize)
    {
        _partitionColumns = [.. partitionKeys];
        _partitionKey = new KeyColumns(inputTypes, _partitionColumns);
        Order = order;
        PartitionOrder = partitionOrder;
        Reach = reach;
        ReadsMoving = readsMoving;
        Input = new HeldRows(inputTypes) { Changed = Arrive };
        Values = new RowStore(callTypes);
        InputChanges = new ChangeSet(inputTypes);
        Replay = new ChangeWriter(inputTypes, batchSize);
        Fresh = new ChangeWriter(callTypes, batchSize);
        Output = new ChangeWriter([.. inputTypes, .. callTypes], batchSize);
        CallCount = callTypes.Count;
    }

    public HeldRows Input { get; }

    /// <summary>Each held row's call values, at the slot <see cref="ValueOf"/> says.</summary>
    public RowStore Values { get; }

    public ChangeSet InputChanges { get; }

    /// <summary>The rows the batch window reads in this update, each copy once, partition by partition.</summary>
    public ChangeWriter Replay { get; }

    /// <summary>The values computed in this update, one row for each row whose value is computed.</summary>
    public ChangeWriter Fresh { get; }

    public ChangeWriter Output { get; }

    public SortOrdering Order { get; }

    /// <summary>The partition keys, ascending with NULLs last, by which the replay hands partitions over.</summary>
    public SortOrdering PartitionOrder { get; }

    public WindowReach Reach { get; }

    public bool ReadsMoving { get; }

    public int CallCount { get; }

    public Moment? RanAt { get; set; }

    /// <summary>For each input slot, the slot of its values, or -1 before any.</summary>
    public int[] ValueOf { get; private set; } = [];

    /// <summary>For each input slot, how many copies of it the window last gave.</summary>
    public long[] GivenCount { get; private set; } = [];

    /// <summary>Told of a held row as it goes, before it is emptied: so its values can be taken back.</summary>
    public Action<int>? Leaving { get; set; }

    public IEnumerable<byte[]> PartitionKeys => _partitions.Keys;

    public long Bytes =>
        Input.Bytes + Values.Bytes + InputChanges.Bytes + Replay.Bytes + Fresh.Bytes + Output.Bytes
        + HeapSizes.Array(ValueOf.Length, sizeof(int)) + HeapSizes.Array(GivenCount.Length, sizeof(long))
        + _partitions.Sum(p => HeapSizes.Array(p.Key.Length, 1) + HeapSizes.Array(p.Value.Capacity, sizeof(int)) + 64);

    public byte[] PartitionOf(ReadOnlySpan<ColumnView> columns, int row)
    {
        _partitionKey.Encode(_encoder, columns, row, normalise: false);
        return _encoder.Span.ToArray();
    }

    /// <summary>A partition's held rows, by slot, in its order; empty when it holds none.</summary>
    public List<int> Partition(byte[] key) => _partitions.TryGetValue(key, out var slots) ? slots : [];

    /// <summary>The first position whose row is not before a batch row's order key: the first of its peers.</summary>
    public int LowerBound(List<int> slots, ReadOnlySpan<ColumnView> columns, int row)
    {
        int low = 0, high = slots.Count;
        while (low < high)
        {
            var middle = (low + high) >>> 1;
            if (HeldOrder.Compare(Order, Input.Rows, slots[middle], columns, row) < 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    /// <summary>The first position whose row is after a batch row's order key: past the last of its peers.</summary>
    public int UpperBound(List<int> slots, ReadOnlySpan<ColumnView> columns, int row)
    {
        int low = 0, high = slots.Count;
        while (low < high)
        {
            var middle = (low + high) >>> 1;
            if (HeldOrder.Compare(Order, Input.Rows, slots[middle], columns, row) <= 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    /// <summary>Whether two held rows are peers: equal in every order key.</summary>
    public bool Peers(int a, int b) => HeldOrder.Compare(Order, Input.Rows, a, Input.Rows, b) == 0;

    public void Discard()
    {
        Input.Clear();
        Values.Clear();
        _partitions.Clear();
        Array.Fill(ValueOf, -1);
        Array.Clear(GivenCount);
        InputChanges.Clear();
        Replay.Clear();
        Fresh.Clear();
        Output.Clear();
        RanAt = null;
    }

    public void Trim()
    {
        InputChanges.Trim();
        Replay.Trim();
        Fresh.Trim();
        Output.Trim();
    }

    /// <summary>Keeps each partition's rows in order as rows arrive and go.</summary>
    private void Arrive(int slot, bool added)
    {
        _partitionKey.Encode(_encoder, Input.Rows, _partitionColumns, slot, normalise: false);
        var lookup = _partitions.GetAlternateLookup<ReadOnlySpan<byte>>();
        if (added)
        {
            if (!lookup.TryGetValue(_encoder.Span, out var slots))
            {
                slots = [];
                _partitions[_encoder.Span.ToArray()] = slots;
            }

            // After its peers, by an order key's search over the held rows.
            int low = 0, high = slots.Count;
            while (low < high)
            {
                var middle = (low + high) >>> 1;
                if (HeldOrder.Compare(Order, Input.Rows, slots[middle], Input.Rows, slot) <= 0)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }

            slots.Insert(low, slot);
            if (ValueOf.Length < Input.Rows.Slots)
            {
                var size = Math.Max(Input.Rows.Slots, Math.Max(16, ValueOf.Length * 2));
                var values = ValueOf;
                Array.Resize(ref values, size);
                Array.Fill(values, -1, ValueOf.Length, size - ValueOf.Length);
                ValueOf = values;
                var given = GivenCount;
                Array.Resize(ref given, size);
                GivenCount = given;
            }

            return;
        }

        Leaving?.Invoke(slot);
        if (lookup.TryGetValue(_encoder.Span, out var held))
        {
            var at = held.IndexOf(slot);
            held.RemoveAt(at);
            if (held.Count == 0)
            {
                lookup.Remove(_encoder.Span);
            }
        }

        if (ValueOf[slot] >= 0)
        {
            Values.Remove(ValueOf[slot]);
            ValueOf[slot] = -1;
        }

        GivenCount[slot] = 0;
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

/// <summary>A maintained window's update: the rows of each touched partition a change can reach, computed again.</summary>
internal sealed class WindowCallChanges : MaintainedOperator, IKeepsScratch
{
    private readonly WindowCallsState _state;
    private readonly IBatchOperator _input;
    private readonly IBatchOperator _body;
    private readonly int _inputWidth;

    // The touched partitions and the positions their changes reach, from the first of their peers to the last.
    private readonly Dictionary<byte[], (int First, int Last)> _touched = new(ByteKeys.Instance);

    // For each partition computed: its rows, the span whose values are taken, and the rows read before it.
    private readonly List<Span> _spans = [];

    // The slot each replayed row is a copy of, in replay order, and whether it is the first copy.
    private readonly List<int> _replayed = [];

    private readonly byte[] _scratch = new byte[16];

    public WindowCallChanges(
        OperatorContext context,
        string path,
        ArrowSchema schema,
        IReadOnlyList<ChalkType> types,
        WindowCallsState state,
        IBatchOperator input,
        IBatchOperator body,
        int inputWidth)
        : base(context, path, schema, types)
    {
        _state = state;
        _input = input;
        _body = body;
        _inputWidth = inputWidth;
        state.Leaving = Leave;
        context.Scratch.Add(this);
    }

    /// <summary>What the last update touched, back to nothing.</summary>
    public void Trim()
    {
        _touched.Clear();
        _touched.TrimExcess();
        _spans.Clear();
        _spans.TrimExcess();
        _replayed.Clear();
        _replayed.TrimExcess();
    }

    protected override async ValueTask DisposeCoreAsync()
    {
        await _input.DisposeAsync().ConfigureAwait(false);
        await _body.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>A row going: what the window gave for it is taken back, from the values it held.</summary>
    private void Leave(int slot)
    {
        var state = _state;
        if (state.GivenCount[slot] == 0 || state.ValueOf[slot] < 0)
        {
            return;
        }

        WriteGiven(slot, -state.GivenCount[slot]);
    }

    private void WriteGiven(int slot, long weight)
    {
        var state = _state;
        var output = state.Output;
        output.Begin();
        for (var c = 0; c < _inputWidth; c++)
        {
            output.Column(c).Append(state.Input.Rows.Column(c), slot);
        }

        for (var c = 0; c < state.CallCount; c++)
        {
            output.Column(_inputWidth + c).Append(state.Values.Column(c), state.ValueOf[slot]);
        }

        output.End(weight);
    }

    protected override async ValueTask<ChangeWriter> ComputeAsync(CancellationToken ct)
    {
        var state = _state;
        var output = state.Output;
        output.Clear();
        await DrainAsync(_input, state.InputChanges, ct).ConfigureAwait(false);
        var changes = state.InputChanges;

        // The rows first, each partition kept in order and a row that goes taken back as it goes.
        for (var row = 0; row < changes.Count; row++)
        {
            if (changes.Weight(row) != 0)
            {
                state.Input.Add(changes.Columns, row, changes.Weight(row));
            }
        }

        // Then where each change falls, among its peers in the partition as it is now.
        _touched.Clear();
        var now = Moment.Of(Context);
        var everything = state.ReadsMoving && state.RanAt != now;
        state.RanAt = now;
        if (everything)
        {
            foreach (var key in state.PartitionKeys)
            {
                _touched[key] = (0, int.MaxValue);
            }
        }
        else
        {
            for (var row = 0; row < changes.Count; row++)
            {
                if (changes.Weight(row) == 0)
                {
                    continue;
                }

                var key = state.PartitionOf(changes.Columns, row);
                var slots = state.Partition(key);
                var first = state.LowerBound(slots, changes.Columns, row);
                var last = state.UpperBound(slots, changes.Columns, row) - 1;
                _touched[key] = _touched.TryGetValue(key, out var was)
                    ? (Math.Min(was.First, first), Math.Max(was.Last, last))
                    : (first, last);
            }
        }

        if (_touched.Count == 0)
        {
            return output;
        }

        Plan();
        if (_spans.Count == 0)
        {
            return output;
        }

        // The batch window over the rows read, and each computed row's values, resumed where a span does
        // not begin its partition.
        state.Fresh.Clear();
        var span = 0;
        var at = 0;
        var position = _spans[0].ReadFrom;
        long[] seeded = new long[state.CallCount];
        await foreach (var batch in _body.ExecuteAsync(ct).ConfigureAwait(false))
        {
            for (var row = 0; row < batch.RowCount; row++, at++)
            {
                var slot = _replayed[at];
                if (slot < 0)
                {
                    continue; // a later copy of a row: its values are its first copy's
                }

                while (position > _spans[span].ReadTo)
                {
                    span++;
                    position = _spans[span].ReadFrom;
                }

                var current = _spans[span];
                if (position >= current.From && position <= current.To)
                {
                    Resume(batch, row, current);
                }

                position++;
            }
        }

        Give();
        return output;
    }

    /// <summary>
    /// For each touched partition, the span whose values a change can move and the rows they read, widened
    /// to whole peer groups; and the replay of those rows, partition by partition in their keys' order.
    /// </summary>
    private void Plan()
    {
        var state = _state;
        var reach = state.Reach;
        _spans.Clear();
        foreach (var (key, (first, last)) in _touched)
        {
            var slots = state.Partition(key);
            if (slots.Count == 0)
            {
                continue;
            }

            var end = slots.Count - 1;
            var from = PeerStart(slots, Clamp(Back(first, reach.Behind), end));
            var to = reach.Ahead == WindowReach.Unbounded || last >= end ? end : PeerEnd(slots, Clamp(Forward(last, reach.Ahead), end));
            if (to < from)
            {
                continue; // the changes took out the partition's last rows, and nothing after them moves
            }

            var readFrom = PeerStart(slots, Clamp(Back(from, reach.Lookback), end));
            var readTo = PeerEnd(slots, Clamp(Forward(to, reach.Lookahead), end));
            _spans.Add(new Span(key, slots, from, to, readFrom, readTo));
        }

        // The batch window wants its partitions in their keys' order, and their rows in theirs.
        _spans.Sort((a, b) => HeldOrder.Compare(state.PartitionOrder, state.Input.Rows, a.Slots[0], state.Input.Rows, b.Slots[0]));
        state.Replay.Clear();
        _replayed.Clear();
        foreach (var span in _spans)
        {
            for (var position = span.ReadFrom; position <= span.ReadTo; position++)
            {
                var slot = span.Slots[position];
                for (var copy = state.Input.Count(slot); copy > 0; copy--)
                {
                    state.Replay.Write(state.Input.Rows, slot, 1);
                    _replayed.Add(copy == state.Input.Count(slot) ? slot : -1);
                }
            }
        }
    }

    private static int Back(int position, int rows) => rows == WindowReach.Unbounded ? 0 : Math.Max(0, position - rows);

    private static int Forward(int position, int rows) => rows == WindowReach.Unbounded ? int.MaxValue : position + rows;

    private static int Clamp(int position, int end) => Math.Min(Math.Max(position, 0), end);

    private int PeerStart(List<int> slots, int position)
    {
        while (position > 0 && _state.Peers(slots[position - 1], slots[position]))
        {
            position--;
        }

        return position;
    }

    private int PeerEnd(List<int> slots, int position)
    {
        while (position < slots.Count - 1 && _state.Peers(slots[position + 1], slots[position]))
        {
            position++;
        }

        return position;
    }

    /// <summary>
    /// One computed row's values, as the batch window gave them over the rows read or, for a running call
    /// whose span does not begin its partition, resumed from the value held at the row before the span.
    /// </summary>
    private void Resume(ColumnarBatch batch, int row, Span span)
    {
        var state = _state;
        var fresh = state.Fresh;
        var before = span.ReadFrom > 0 ? state.ValueOf[span.Slots[span.ReadFrom - 1]] : -1;
        fresh.Begin();
        for (var c = 0; c < state.CallCount; c++)
        {
            var column = fresh.Column(c);
            ref readonly var value = ref batch.Columns[_inputWidth + 1 + c];
            var seed = state.Reach.Seeds[c];
            if (before < 0 || seed == WindowSeed.None)
            {
                column.Append(value, row);
                continue;
            }

            var held = state.Values.Column(c);
            switch (seed)
            {
                case WindowSeed.First:
                    column.Append(held, before);
                    break;
                case WindowSeed.FirstIgnoringNulls:
                    if (held.IsValid(before))
                    {
                        column.Append(held, before);
                    }
                    else
                    {
                        column.Append(value, row);
                    }

                    break;
                case WindowSeed.Rows:
                    column.AppendInt64(value.Lanes<long>()[row] + RowsBefore(span));
                    break;
                case WindowSeed.Plus:
                    column.AppendInt64(value.Lanes<long>()[row] + MemoryMarshal.Read<long>(held.Lane(before)));
                    break;
                default:
                    Combine(column, seed, held, before, value, row);
                    break;
            }
        }

        fresh.End(0);
        _computed.Add((span.Slots, span.ReadFrom, fresh.Rows - 1));
    }

    // Each computed row: its partition's rows and the position in them, by the order rows were computed.
    private readonly List<(List<int> Slots, int ReadFrom, int Fresh)> _computed = [];

    /// <summary>How many rows the partition holds before the rows read, copies counted.</summary>
    private long RowsBefore(Span span)
    {
        if (span.RowsBefore < 0)
        {
            long rows = 0;
            for (var position = 0; position < span.ReadFrom; position++)
            {
                rows += _state.Input.Count(span.Slots[position]);
            }

            span.RowsBefore = rows;
        }

        return span.RowsBefore;
    }

    /// <summary>A running aggregate resumed: the value before the span with the span's own, NULL being no value.</summary>
    private void Combine(ChangeColumn column, WindowSeed seed, StoredColumn held, int before, in ColumnView value, int row)
    {
        var heldValid = held.IsValid(before);
        var valueValid = value.IsValid(row);
        if (!heldValid || !valueValid)
        {
            if (valueValid)
            {
                column.Append(value, row);
            }
            else
            {
                column.Append(held, before);
            }

            return;
        }

        var kind = held.Kind;
        var lane = value.RawLanes(held.Width).Slice(row * held.Width, held.Width);
        if (seed is WindowSeed.Least or WindowSeed.Greatest)
        {
            var order = LaneComparer.Compare(kind, held.Lane(before), lane);
            var keepHeld = seed == WindowSeed.Least ? order <= 0 : order >= 0;
            if (keepHeld)
            {
                column.Append(held, before);
            }
            else
            {
                column.Append(value, row);
            }

            return;
        }

        switch (kind)
        {
            case ColumnKind.Int64:
                column.AppendInt64(checked(MemoryMarshal.Read<long>(held.Lane(before)) + MemoryMarshal.Read<long>(lane)));
                break;
            case ColumnKind.Int32:
            {
                var sum = checked(MemoryMarshal.Read<int>(held.Lane(before)) + MemoryMarshal.Read<int>(lane));
                MemoryMarshal.Write(_scratch, in sum);
                column.AppendLane(_scratch.AsSpan(0, 4));
                break;
            }

            case ColumnKind.Double:
            {
                var sum = MemoryMarshal.Read<double>(held.Lane(before)) + MemoryMarshal.Read<double>(lane);
                MemoryMarshal.Write(_scratch, in sum);
                column.AppendLane(_scratch.AsSpan(0, 8));
                break;
            }

            case ColumnKind.Float:
            {
                var sum = MemoryMarshal.Read<float>(held.Lane(before)) + MemoryMarshal.Read<float>(lane);
                MemoryMarshal.Write(_scratch, in sum);
                column.AppendLane(_scratch.AsSpan(0, 4));
                break;
            }

            default:
                throw new InvalidOperationException($"a window's running sum of {kind} was taken as resumable.");
        }
    }

    /// <summary>
    /// Gives each computed row whose values moved, or whose copies did: what was given taken back, and
    /// the row given now; then keeps its values.
    /// </summary>
    private void Give()
    {
        var state = _state;
        var computed = 0;
        foreach (var span in _spans)
        {
            for (var position = span.From; position <= span.To; position++, computed++)
            {
                var slot = span.Slots[position];
                var fresh = _computed[computed].Fresh;
                var batch = state.Fresh.Batch(fresh / Context.Settings.BatchSize);
                var row = fresh % Context.Settings.BatchSize;
                var count = state.Input.Count(slot);
                var valueSlot = state.ValueOf[slot];
                var same = valueSlot >= 0 && SameValues(valueSlot, batch, row);
                if (same && state.GivenCount[slot] == count)
                {
                    continue;
                }

                if (same)
                {
                    WriteGiven(slot, count - state.GivenCount[slot]);
                }
                else
                {
                    if (valueSlot >= 0 && state.GivenCount[slot] > 0)
                    {
                        WriteGiven(slot, -state.GivenCount[slot]);
                    }

                    if (valueSlot >= 0)
                    {
                        state.Values.Remove(valueSlot);
                    }

                    state.ValueOf[slot] = state.Values.Add(batch.Columns[..state.CallCount], row);
                    WriteGiven(slot, count);
                }

                state.GivenCount[slot] = count;
            }
        }

        _computed.Clear();
    }

    private bool SameValues(int valueSlot, ColumnarBatch batch, int row)
    {
        var values = _state.Values;
        for (var c = 0; c < _state.CallCount; c++)
        {
            var held = values.Column(c);
            ref readonly var fresh = ref batch.Columns[c];
            if (held.IsValid(valueSlot) != fresh.IsValid(row))
            {
                return false;
            }

            if (!held.IsValid(valueSlot))
            {
                continue;
            }

            var equal = held.Variable
                ? held.Bytes(valueSlot).SequenceEqual(fresh.VarValue(row))
                : held.Kind == ColumnKind.Boolean
                    ? (held.Lane(valueSlot)[0] != 0) == fresh.BoolAt(row)
                    : held.Lane(valueSlot).SequenceEqual(fresh.RawLanes(held.Width).Slice(row * held.Width, held.Width));
            if (!equal)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A touched partition's rows, the positions whose values are computed, and the positions read.</summary>
    private sealed class Span(byte[] key, List<int> slots, int from, int to, int readFrom, int readTo)
    {
        public byte[] Key { get; } = key;

        public List<int> Slots { get; } = slots;

        public int From { get; } = from;

        public int To { get; } = to;

        public int ReadFrom { get; } = readFrom;

        public int ReadTo { get; } = readTo;

        public long RowsBefore { get; set; } = -1;
    }
}

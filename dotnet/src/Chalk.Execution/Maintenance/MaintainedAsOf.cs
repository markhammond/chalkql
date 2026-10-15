using Chalk.Catalog;
using Chalk.Execution.Aggregation;
using Chalk.Execution.Operators;
using Chalk.Execution.Vectors;
using Chalk.Ir;
using Chalk.Sources;
using ArrowSchema = Apache.Arrow.Schema;

namespace Chalk.Execution.Maintenance;

/// <summary>
/// A maintained as-of join (D343, design 68 §2, §5): the left rows by key, each with the right row it
/// matches now; the right rows by key, ordered by time and then by their place in the table.
/// </summary>
/// <remarks>
/// <para>
/// The right input is a table read, or a lookup of one key whose ties are the table's (§19), through
/// filters and projections only, so its changes are rows appended, in the table's order, or, after
/// the table was rewritten, every row afresh: an append re-matches the left rows of the keys it
/// reached, and a rewrite places every right row again and re-matches every left row. Either way a
/// left row's output changes only when its match's values do.
/// </para>
/// <para>
/// Ties break as the executor's as-of join breaks them: the first of the closest rows in the right
/// input's order. An as-of seek, which breaks them by its index, is <see cref="SeekState"/>.
/// </para>
/// </remarks>
internal sealed class AsOfState : IMaintainedState
{
    public AsOfState(
        IReadOnlyList<ChalkType> leftTypes,
        IReadOnlyList<ChalkType> rightTypes,
        IReadOnlyList<ChalkType> outputTypes,
        int[] leftKeys,
        int[] rightKeys,
        int leftTime,
        int rightTime,
        AsOfMatch match,
        bool leftJoin,
        ReadState rightRead,
        int batchSize)
    {
        LeftWidth = leftTypes.Count;
        RightWidth = rightTypes.Count;
        LeftKeys = new KeyColumns(leftTypes, leftKeys);
        RightKeys = new KeyColumns(rightTypes, rightKeys);
        LeftIdentity = KeyColumns.All(leftTypes);
        RightIdentity = KeyColumns.All(rightTypes);
        LeftTime = leftTime;
        RightTime = rightTime;
        Match = match;
        LeftJoin = leftJoin;
        RightRead = rightRead;
        Times = new TimeOrder(leftTypes[leftTime]);
        Left = new RowStore(leftTypes);
        Right = new RowStore(rightTypes);
        Spare = new RowStore(rightTypes);
        LeftChanges = new ChangeSet(leftTypes);
        Output = new ChangeWriter(outputTypes, batchSize);
    }

    public int LeftWidth { get; }

    public int RightWidth { get; }

    public KeyColumns LeftKeys { get; }

    public KeyColumns RightKeys { get; }

    public KeyColumns LeftIdentity { get; }

    public KeyColumns RightIdentity { get; }

    public int LeftTime { get; }

    public int RightTime { get; }

    public AsOfMatch Match { get; }

    public bool LeftJoin { get; }

    /// <summary>The read under the right input, which says when the table was rewritten.</summary>
    public ReadState RightRead { get; }

    public TimeOrder Times { get; }

    public RowStore Left { get; }

    public RowStore Right { get; set; }

    /// <summary>The store a rewrite builds the right rows into, then <see cref="Right"/>; empty between rewrites.</summary>
    public RowStore Spare { get; set; }

    public ChangeSet LeftChanges { get; }

    public ChangeWriter Output { get; }

    public KeyEncoder Encoder { get; } = new();

    /// <summary>The left rows held: how many times, by key, and the right slot each matches, or -1.</summary>
    public long[] LeftCounts { get; set; } = [];

    public int[] LeftMatches { get; set; } = [];

    /// <summary>Each held left row's slot, by its identity.</summary>
    public ByteMap LeftSlots { get; } = new();

    public Buckets LeftBuckets { get; } = new();

    /// <summary>The right rows by key, ordered by time and then by arrival.</summary>
    public Dictionary<byte[], Run> Runs { get; set; } = new(ByteKeys.Instance);

    public Dictionary<byte[], Run> SpareRuns { get; set; } = new(ByteKeys.Instance);

    /// <summary>The arrival number the next right row takes.</summary>
    public long NextArrival { get; set; }

    public long Bytes =>
        Left.Bytes + Right.Bytes + Spare.Bytes + LeftChanges.Bytes + Output.Bytes
        + ((LeftCounts.Length * sizeof(long)) + (LeftMatches.Length * sizeof(int)))
        + LeftSlots.Bytes + LeftBuckets.Bytes + (Runs.Count * 64L) + (Right.Count * 16L);

    public void Discard()
    {
        Left.Clear();
        Right.Clear();
        Spare.Clear();
        LeftChanges.Clear();
        Output.Clear();
        LeftSlots.Clear();
        LeftBuckets.Clear();
        Runs.Clear();
        SpareRuns.Clear();
        NextArrival = 0;
    }

    public void Trim()
    {
        LeftChanges.Trim();
        Output.Trim();
    }

    /// <summary>One key's right rows, ordered by time and then by arrival.</summary>
    internal sealed class Run(byte[] key)
    {
        /// <summary>The key, as the dictionary of runs holds it.</summary>
        public byte[] Key { get; } = key;

        public List<(int Slot, long Arrival)> Rows { get; } = [];
    }
}

/// <summary>Orders time lanes as the executor's as-of join does: floating point by value, NaN last.</summary>
internal sealed class TimeOrder
{
    private readonly byte[] _a = new byte[16];
    private readonly byte[] _b = new byte[16];

    public TimeOrder(ChalkType type)
    {
        Kind = Kinds.Of(type, "an as-of join's time");
        Width = ColumnKinds.Width(Kind);
    }

    public ColumnKind Kind { get; }

    public int Width { get; }

    public int Compare(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (Kind is ColumnKind.Float or ColumnKind.Double)
        {
            a[..Width].CopyTo(_a);
            b[..Width].CopyTo(_b);
            return LaneComparer.Compare(
                Kind, LaneAccess.Normalise(Kind, _a.AsSpan(0, Width)), LaneAccess.Normalise(Kind, _b.AsSpan(0, Width)));
        }

        return LaneComparer.Compare(Kind, a[..Width], b[..Width]);
    }
}

internal sealed class AsOfChanges : MaintainedOperator
{
    private readonly AsOfState _state;
    private readonly IBatchOperator _left;
    private readonly IBatchOperator _right;
    private readonly ChangeWriter _arrived;
    private readonly HashSet<byte[]> _reached = new(ByteKeys.Instance);
    private readonly KeyEncoder _first = new();
    private readonly KeyEncoder _second = new();
    private readonly int[] _rightColumns;

    /// <summary>How many slots the last rebuild's store reached, to make room for at once the next time.</summary>
    private int _rebuiltSize;

    public AsOfChanges(
        OperatorContext context,
        string path,
        ArrowSchema schema,
        IReadOnlyList<ChalkType> types,
        AsOfState state,
        IBatchOperator left,
        IBatchOperator right)
        : base(context, path, schema, types)
    {
        _state = state;
        _left = left;
        _right = right;
        _arrived = new ChangeWriter(state.Right.Types, context.Settings.BatchSize);
        _rightColumns = [.. Enumerable.Range(0, state.RightWidth)];
    }

    protected override async ValueTask DisposeCoreAsync()
    {
        await _left.DisposeAsync().ConfigureAwait(false);
        await _right.DisposeAsync().ConfigureAwait(false);
    }

    protected override async ValueTask<ChangeWriter> ComputeAsync(CancellationToken ct)
    {
        var state = _state;
        await DrainAsync(_left, state.LeftChanges, ct).ConfigureAwait(false);

        // The right changes in the order they arrived: never added up, since a rewrite gives every row
        // again and its order is the table's.
        _arrived.Clear();
        await foreach (var batch in _right.ExecuteAsync(ct).ConfigureAwait(false))
        {
            var columns = batch.Columns;
            for (var i = 0; i < batch.Count; i++)
            {
                var row = batch.RowAt(i);
                _arrived.Write(columns, row, Weights.Of(batch, row));
            }
        }

        state.Output.Clear();
        if (state.RightRead.Rewritten)
        {
            Rebuild();
        }
        else
        {
            Append();
        }

        ApplyLeft();
        return state.Output;
    }

    /// <summary>Right rows appended: placed after the rows of their key, and the left rows of each key they reached matched again.</summary>
    private void Append()
    {
        var state = _state;
        _reached.Clear();
        for (var b = 0; b < _arrived.BatchCount; b++)
        {
            var batch = _arrived.Batch(b);
            var columns = batch.Columns;
            for (var row = 0; row < batch.RowCount; row++)
            {
                var weight = Weights.Of(batch, row);
                if (weight < 0)
                {
                    throw new InvalidOperationException(
                        "a maintained as-of join's right input took a row out without its table being rewritten.");
                }

                for (var copy = 0; copy < weight; copy++)
                {
                    if (Place(state.Right, state.Runs, columns, row) is { } key)
                    {
                        _reached.Add(key);
                    }
                }
            }
        }

        foreach (var key in _reached)
        {
            if (state.LeftBuckets.Find(key) is { } bucket && state.Runs.TryGetValue(key, out var run))
            {
                foreach (var slot in bucket)
                {
                    Rematch(slot, run, state.Right, state.Right);
                }
            }
        }
    }

    /// <summary>The table rewritten: every right row placed afresh, in its new order, and every left row matched again.</summary>
    private void Rebuild()
    {
        var state = _state;
        var fresh = state.Spare.Slots == 0 && state.Right.Slots > 0
            ? state.Spare = new RowStore(state.Right.Types, Math.Max(state.Right.Count, _rebuiltSize))
            : state.Spare;
        var runs = state.SpareRuns;
        fresh.Clear();
        runs.Clear();
        state.NextArrival = 0;
        for (var b = 0; b < _arrived.BatchCount; b++)
        {
            var batch = _arrived.Batch(b);
            var columns = batch.Columns;
            for (var row = 0; row < batch.RowCount; row++)
            {
                var weight = Weights.Of(batch, row);
                for (var copy = 0; copy < weight; copy++)
                {
                    Place(fresh, runs, columns, row);
                }
            }
        }

        var old = state.Right;
        foreach (var (key, bucket) in state.LeftBuckets.All())
        {
            runs.TryGetValue(key, out var run);
            foreach (var slot in bucket)
            {
                Rematch(slot, run, old, fresh);
            }
        }

        // The old rows go with their store: a rewrite is rare, and a store as large as the table kept
        // empty for the next one would hold its memory against the view's budget meanwhile.
        state.Spare = new RowStore(old.Types);
        _rebuiltSize = fresh.Slots;
        state.Right = fresh;
        state.SpareRuns = state.Runs;
        state.SpareRuns.Clear();
        state.Runs = runs;
    }

    /// <summary>Keeps a right row, unless its key or time is NULL, and returns its key; null for a row kept nowhere.</summary>
    private byte[]? Place(RowStore store, Dictionary<byte[], AsOfState.Run> runs, ReadOnlySpan<ColumnView> columns, int row)
    {
        var state = _state;
        if (!columns[state.RightTime].IsValid(row) || !state.RightKeys.Encode(state.Encoder, columns, row, normalise: true))
        {
            return null;
        }

        if (!runs.GetAlternateLookup<ReadOnlySpan<byte>>().TryGetValue(state.Encoder.Span, out var run))
        {
            var key = state.Encoder.Span.ToArray();
            run = new AsOfState.Run(key);
            runs[key] = run;
        }

        var slot = store.Add(columns[..state.RightWidth], row);
        var arrival = state.NextArrival++;
        var time = store.Column(state.RightTime).Lane(slot);
        var rows = run.Rows;

        // Rows usually arrive in time order, and then their place is the end. Otherwise it is after
        // every row of its time or earlier: the latest arrival of its time.
        var at = rows.Count > 0 && state.Times.Compare(store.Column(state.RightTime).Lane(rows[^1].Slot), time) > 0
            ? Upper(rows, store, time)
            : rows.Count;
        rows.Insert(at, (slot, arrival));
        return run.Key;
    }

    /// <summary>The first place in <paramref name="rows"/> whose time is after <paramref name="time"/>.</summary>
    private int Upper(List<(int Slot, long Arrival)> rows, RowStore store, ReadOnlySpan<byte> time)
    {
        var low = 0;
        var high = rows.Count;
        while (low < high)
        {
            var middle = low + ((high - low) >> 1);
            if (_state.Times.Compare(store.Column(_state.RightTime).Lane(rows[middle].Slot), time) <= 0)
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

    /// <summary>
    /// The right slot a left time matches in <paramref name="run"/>, or -1: the closest time under the
    /// comparison, and among rows of that time the one the executor's operator takes.
    /// </summary>
    private int Find(AsOfState.Run? run, RowStore store, ReadOnlySpan<byte> time)
    {
        if (run is null || run.Rows.Count == 0)
        {
            return -1;
        }

        var state = _state;
        var rows = run.Rows;
        var strict = state.Match is AsOfMatch.Gt or AsOfMatch.Lt;
        var low = 0;
        var high = rows.Count;
        if (state.Match is AsOfMatch.Ge or AsOfMatch.Gt)
        {
            // The last right row at or before the time (strictly before for >).
            while (low < high)
            {
                var middle = low + ((high - low) >> 1);
                var order = state.Times.Compare(store.Column(state.RightTime).Lane(rows[middle].Slot), time);
                if (order < 0 || (order == 0 && !strict))
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }

            if (low == 0)
            {
                return -1;
            }

            // Among rows of the closest time, the first.
            var found = low - 1;
            var closest = store.Column(state.RightTime).Lane(rows[found].Slot);
            while (found > 0 && state.Times.Compare(store.Column(state.RightTime).Lane(rows[found - 1].Slot), closest) == 0)
            {
                found--;
            }

            return rows[found].Slot;
        }

        // The first right row at or after the time (strictly after for <).
        while (low < high)
        {
            var middle = low + ((high - low) >> 1);
            var order = state.Times.Compare(store.Column(state.RightTime).Lane(rows[middle].Slot), time);
            if (order < 0 || (order == 0 && strict))
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low == rows.Count ? -1 : rows[low].Slot;
    }

    /// <summary>
    /// Matches a held left row again, and when its match's values move, takes its old output out and
    /// puts its new one in. <paramref name="before"/> holds the old match, <paramref name="after"/> the new.
    /// </summary>
    private void Rematch(int slot, AsOfState.Run? run, RowStore before, RowStore after)
    {
        var state = _state;
        var old = state.LeftMatches[slot];
        var now = Find(run, after, state.Left.Column(state.LeftTime).Lane(slot));
        if (old < 0 && now < 0)
        {
            return;
        }

        if (old >= 0 && now >= 0 && SameRow(before, old, after, now))
        {
            state.LeftMatches[slot] = now;
            return;
        }

        var count = state.LeftCounts[slot];
        WriteHeld(slot, before, old, -count);
        WriteHeld(slot, after, now, count);
        state.LeftMatches[slot] = now;
    }

    /// <summary>Whether two held right rows have the same values, bit for bit.</summary>
    private bool SameRow(RowStore a, int aSlot, RowStore b, int bSlot)
    {
        if (ReferenceEquals(a, b) && aSlot == bSlot)
        {
            return true;
        }

        _state.RightIdentity.Encode(_first, a, _rightColumns, aSlot, normalise: false);
        _state.RightIdentity.Encode(_second, b, _rightColumns, bSlot, normalise: false);
        return _first.Span.SequenceEqual(_second.Span);
    }

    /// <summary>The left changes: a row taken out leaves with the match it had; a row put in is matched now.</summary>
    private void ApplyLeft()
    {
        var state = _state;
        var changes = state.LeftChanges;
        var columns = changes.Columns;
        for (var row = 0; row < changes.Count; row++)
        {
            var weight = changes.Weight(row);
            if (weight == 0)
            {
                continue;
            }

            if (!columns[state.LeftTime].IsValid(row) || !state.LeftKeys.Encode(state.Encoder, columns, row, normalise: true))
            {
                // A NULL key or time matches nothing: a left join pads the row, an inner join drops it.
                if (state.LeftJoin)
                {
                    WriteChange(row, null, -1, weight);
                }

                continue;
            }

            ref var held = ref state.LeftSlots.GetOrAdd(changes.Identity(row), out var added);
            if (!added)
            {
                var slot = held;
                WriteHeld(slot, state.Right, state.LeftMatches[slot], weight);
                state.LeftCounts[slot] += weight;
                if (state.LeftCounts[slot] < 0)
                {
                    throw new InvalidOperationException("a maintained as-of join took out more copies of a row than it held.");
                }

                if (state.LeftCounts[slot] == 0)
                {
                    state.LeftSlots.Remove(changes.Identity(row), out _);
                    state.LeftBuckets.Remove(slot);
                    state.Left.Remove(slot);
                }

                continue;
            }

            if (weight < 0)
            {
                throw new InvalidOperationException("a maintained as-of join was asked to take out a row it does not hold.");
            }

            state.Runs.GetAlternateLookup<ReadOnlySpan<byte>>().TryGetValue(state.Encoder.Span, out var run);
            var time = columns[state.LeftTime].RawLanes(state.Times.Width).Slice(row * state.Times.Width, state.Times.Width);
            var match = Find(run, state.Right, time);
            WriteChange(row, state.Right, match, weight);

            var fresh = state.Left.Add(columns, row);
            held = fresh;
            if (state.LeftCounts.Length < state.Left.Slots)
            {
                var size = Math.Max(state.Left.Slots, Math.Max(16, state.LeftCounts.Length * 2));
                var counts = state.LeftCounts;
                var matches = state.LeftMatches;
                Array.Resize(ref counts, size);
                Array.Resize(ref matches, size);
                state.LeftCounts = counts;
                state.LeftMatches = matches;
            }

            state.LeftCounts[fresh] = weight;
            state.LeftMatches[fresh] = match;
            state.LeftBuckets.Add(state.Encoder.Span, fresh);
        }
    }

    /// <summary>A held left row with its match, or NULLs for a left join's unmatched row; nothing for an inner join's.</summary>
    private void WriteHeld(int slot, RowStore right, int match, long weight)
    {
        var state = _state;
        if (match < 0 && !state.LeftJoin)
        {
            return;
        }

        var into = state.Output;
        into.Begin();
        for (var c = 0; c < state.LeftWidth; c++)
        {
            into.Column(c).Append(state.Left.Column(c), slot);
        }

        WriteRight(into, right, match);
        into.End(weight);
    }

    /// <summary>A row of the left changes with its match, as <see cref="WriteHeld"/> writes a held one.</summary>
    private void WriteChange(int row, RowStore? right, int match, long weight)
    {
        var state = _state;
        if (match < 0 && !state.LeftJoin)
        {
            return;
        }

        var into = state.Output;
        into.Begin();
        var columns = state.LeftChanges.Columns;
        for (var c = 0; c < state.LeftWidth; c++)
        {
            into.Column(c).Append(columns[c], row);
        }

        WriteRight(into, right, match);
        into.End(weight);
    }

    private void WriteRight(ChangeWriter into, RowStore? right, int match)
    {
        var state = _state;
        for (var c = 0; c < state.RightWidth; c++)
        {
            if (match < 0 || right is null)
            {
                into.Column(state.LeftWidth + c).AppendNull();
            }
            else
            {
                into.Column(state.LeftWidth + c).Append(right.Column(c), match);
            }
        }
    }
}

using Chalk.Catalog;
using Chalk.Execution.Operators;
using Chalk.Sources;
using ArrowSchema = Apache.Arrow.Schema;

namespace Chalk.Execution.Maintenance;

/// <summary>
/// A maintained as-of seek (D343, design 68 §13): an as-of join that seeks an in-process table's
/// index, kept without the table's rows. It holds its left rows by key, each with the values of the
/// right row it matches now, and seeks the index again only for the left rows whose match may have
/// moved.
/// </summary>
/// <remarks>
/// <para>
/// The seeking is the executor's own: the statement's as-of seek, compiled as it is but over a replay
/// of the left rows to match, and as a left join, so that every one of them comes back. Its matches,
/// ties and NULLs are therefore a recompute's. A row is replayed as all the seek reads of it, its keys
/// and its time, and its place in the replay, by which what the seek found comes back to it.
/// </para>
/// <para>
/// A left row is matched when it arrives; again when a row is appended to the table under its key;
/// and, after the table is rewritten, again in every case. Its output changes only when its match's
/// values do. All the operator reads of the table itself is the keys of the rows appended.
/// </para>
/// </remarks>
internal sealed class SeekState : IMaintainedState
{
    public SeekState(
        IReadOnlyList<ChalkType> leftTypes,
        IReadOnlyList<ChalkType> rightTypes,
        IReadOnlyList<ChalkType> outputTypes,
        IReadOnlyList<ChalkType> keyTypes,
        int[] leftKeys,
        int leftTime,
        int rightTime,
        bool leftJoin,
        ReadState keys,
        int batchSize)
    {
        LeftWidth = leftTypes.Count;
        RightWidth = rightTypes.Count;
        Replayed = [.. leftKeys, leftTime];
        LeftKeys = new KeyColumns(leftTypes, leftKeys);
        TableKeys = new KeyColumns(keyTypes, [.. Enumerable.Range(0, leftKeys.Length)]);
        RightIdentity = KeyColumns.All(rightTypes);
        LeftTime = leftTime;
        RightTime = rightTime;
        LeftJoin = leftJoin;
        Keys = keys;
        Left = new RowStore(leftTypes);
        Matches = new RowStore(rightTypes);
        Found = new RowStore(rightTypes);
        LeftChanges = new ChangeSet(leftTypes);
        Pending = new ChangeWriter([.. Replayed.Select(c => leftTypes[c]), Place], batchSize);
        Output = new ChangeWriter(outputTypes, batchSize);
    }

    /// <summary>The type of a replayed row's place among the rows replayed, after its own columns.</summary>
    public static ChalkType Place { get; } = ChalkType.Int64();

    public int LeftWidth { get; }

    public int RightWidth { get; }

    /// <summary>The left columns a row is replayed with, its keys and then its time; its place follows them.</summary>
    public int[] Replayed { get; }

    /// <summary>A left row's equality keys, encoded as a table row's are.</summary>
    public KeyColumns LeftKeys { get; }

    /// <summary>A table row's keys: the columns of the read of its keys.</summary>
    public KeyColumns TableKeys { get; }

    public KeyColumns RightIdentity { get; }

    public int LeftTime { get; }

    /// <summary>The right time's place among the right columns: never NULL in a match, so it marks one.</summary>
    public int RightTime { get; }

    public bool LeftJoin { get; }

    /// <summary>The read of the table's keys, which says too when the table was rewritten.</summary>
    public ReadState Keys { get; }

    public RowStore Left { get; }

    /// <summary>The right values each held left row matches, at <see cref="LeftMatches"/>.</summary>
    public RowStore Matches { get; }

    /// <summary>What this update's seek found for the left rows it matched.</summary>
    public RowStore Found { get; }

    /// <summary>The left rows held: how many times, by key, and the slot of <see cref="Matches"/> each matches, or -1.</summary>
    public long[] LeftCounts { get; set; } = [];

    public int[] LeftMatches { get; set; } = [];

    /// <summary>Each held left row's slot, by its identity.</summary>
    public ByteMap LeftSlots { get; } = new();

    public Buckets LeftBuckets { get; } = new();

    public ChangeSet LeftChanges { get; }

    /// <summary>The left rows this update matches, as they are replayed to the seek.</summary>
    public ChangeWriter Pending { get; }

    public ChangeWriter Output { get; }

    public KeyEncoder Encoder { get; } = new();

    public long Bytes =>
        Left.Bytes + Matches.Bytes + Found.Bytes + LeftChanges.Bytes + Pending.Bytes + Output.Bytes
        + ((LeftCounts.Length * sizeof(long)) + (LeftMatches.Length * sizeof(int)))
        + LeftSlots.Bytes + LeftBuckets.Bytes;

    public void Discard()
    {
        Left.Clear();
        Matches.Clear();
        Found.Clear();
        LeftChanges.Clear();
        Pending.Clear();
        Output.Clear();
        LeftSlots.Clear();
        LeftBuckets.Clear();
    }
}

/// <summary>The left rows a maintained seek matches in this update: their keys, time and place, without weights.</summary>
internal sealed class PendingRows : OperatorBase
{
    private readonly SeekState _state;
    private readonly ColumnarBatch _slot;

    public PendingRows(OperatorContext context, string path, ArrowSchema schema, IReadOnlyList<ChalkType> types, SeekState state)
        : base(context, schema, types, path)
    {
        _state = state;
        _slot = NewOutput();
    }

    protected override ValueTask DisposeCoreAsync() => default;

    protected override async IAsyncEnumerable<ColumnarBatch> RunAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        var pending = _state.Pending;
        for (var i = 0; i < pending.BatchCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            var batch = pending.Batch(i);
            _slot.Begin(batch.RowCount);
            for (var c = 0; c <= _state.Replayed.Length; c++)
            {
                _slot.Set(c, batch.Column(c));
            }

            yield return _slot;
        }
    }
}

internal sealed class SeekChanges : MaintainedOperator
{
    private readonly SeekState _state;
    private readonly IBatchOperator _left;
    private readonly IBatchOperator _keys;
    private readonly IBatchOperator _seek;
    private readonly int[] _rightColumns;
    private readonly ByteMap _reached = new();
    private readonly KeyEncoder _first = new();
    private readonly KeyEncoder _second = new();

    /// <summary>Each replayed row's owner: a held slot, or the complement of a row of the left changes.</summary>
    private readonly List<int> _owners = [];

    /// <summary>What the seek found for each replayed row, a slot of <see cref="SeekState.Found"/>, or -1.</summary>
    private int[] _found = [];

    /// <summary>Each row of the left changes' place among the rows replayed, or -1.</summary>
    private int[] _arriving = [];

    public SeekChanges(
        OperatorContext context,
        string path,
        ArrowSchema schema,
        IReadOnlyList<ChalkType> types,
        SeekState state,
        IBatchOperator left,
        IBatchOperator keys,
        IBatchOperator seek)
        : base(context, path, schema, types)
    {
        _state = state;
        _left = left;
        _keys = keys;
        _seek = seek;
        _rightColumns = [.. Enumerable.Range(0, state.RightWidth)];
    }

    protected override async ValueTask DisposeCoreAsync()
    {
        await _left.DisposeAsync().ConfigureAwait(false);
        await _keys.DisposeAsync().ConfigureAwait(false);
        await _seek.DisposeAsync().ConfigureAwait(false);
    }

    protected override async ValueTask<ChangeWriter> ComputeAsync(CancellationToken ct)
    {
        var state = _state;
        await DrainAsync(_left, state.LeftChanges, ct).ConfigureAwait(false);
        await ReachAsync(ct).ConfigureAwait(false);
        state.Output.Clear();
        state.Pending.Clear();
        state.Found.Clear();
        _owners.Clear();

        // The rows leaving first, with the matches they had: a row that leaves is not matched again.
        Leave();

        // The held left rows whose match may have moved: every one after a rewrite, and otherwise those
        // under a key a row was appended to.
        if (state.Keys.Rewritten)
        {
            for (var entry = 0; entry < state.LeftSlots.Entries; entry++)
            {
                if (state.LeftSlots.IsLive(entry))
                {
                    Pend(state.LeftSlots.Value(entry));
                }
            }
        }
        else
        {
            for (var entry = 0; entry < _reached.Entries; entry++)
            {
                if (_reached.IsLive(entry) && state.LeftBuckets.Find(_reached.Key(entry)) is { } bucket)
                {
                    foreach (var slot in bucket)
                    {
                        Pend(slot);
                    }
                }
            }
        }

        // And the left rows arriving that are not held yet.
        var changes = state.LeftChanges;
        if (_arriving.Length < changes.Count)
        {
            _arriving = new int[Math.Max(changes.Count, _arriving.Length * 2)];
        }

        for (var row = 0; row < changes.Count; row++)
        {
            _arriving[row] = changes.Weight(row) > 0 && Matchable(row) && !state.LeftSlots.TryGet(changes.Identity(row), out _)
                ? Pend(~row)
                : -1;
        }

        await SeekAsync(ct).ConfigureAwait(false);

        // The held rows matched again: their old output out and their new in, where the match's values moved.
        for (var place = 0; place < _owners.Count; place++)
        {
            var slot = _owners[place];
            if (slot < 0)
            {
                continue;
            }

            var old = state.LeftMatches[slot];
            var found = _found[place];
            if (Same(old, found))
            {
                continue;
            }

            var count = state.LeftCounts[slot];
            WriteHeld(slot, -count);
            if (old >= 0)
            {
                state.Matches.Remove(old);
            }

            state.LeftMatches[slot] = found < 0 ? -1 : state.Matches.Add(state.Found, found);
            WriteHeld(slot, count);
        }

        Arrive();
        return state.Output;
    }

    /// <summary>The keys of the rows appended to the table, unless it was rewritten or read for the first time.</summary>
    private async ValueTask ReachAsync(CancellationToken ct)
    {
        var state = _state;
        _reached.Clear();
        await foreach (var batch in _keys.ExecuteAsync(ct).ConfigureAwait(false))
        {
            var columns = batch.Columns;
            for (var i = 0; i < batch.Count; i++)
            {
                // A row with a NULL key matches nothing.
                if (state.TableKeys.Encode(state.Encoder, columns, batch.RowAt(i), normalise: true))
                {
                    _reached.GetOrAdd(state.Encoder.Span, out _);
                }
            }
        }
    }

    /// <summary>Whether a row of the left changes can match at all: its keys and its time not NULL.</summary>
    private bool Matchable(int row)
    {
        var state = _state;
        var columns = state.LeftChanges.Columns;
        return columns[state.LeftTime].IsValid(row) && state.LeftKeys.Encode(state.Encoder, columns, row, normalise: true);
    }

    /// <summary>Replays a left row to the seek, a held slot or the complement of a row of the left changes, and returns its place.</summary>
    private int Pend(int owner)
    {
        var state = _state;
        var place = _owners.Count;
        _owners.Add(owner);
        var into = state.Pending;
        var replayed = state.Replayed;
        into.Begin();
        if (owner >= 0)
        {
            for (var c = 0; c < replayed.Length; c++)
            {
                into.Column(c).Append(state.Left.Column(replayed[c]), owner);
            }
        }
        else
        {
            var columns = state.LeftChanges.Columns;
            for (var c = 0; c < replayed.Length; c++)
            {
                into.Column(c).Append(columns[replayed[c]], ~owner);
            }
        }

        into.Column(replayed.Length).AppendInt64(place);
        into.End(1);
        return place;
    }

    /// <summary>
    /// Runs the statement's own seek over the rows replayed, and keeps what it found for each: the right
    /// row's values, or nothing.
    /// </summary>
    private async ValueTask SeekAsync(CancellationToken ct)
    {
        var state = _state;
        var count = _owners.Count;
        if (_found.Length < count)
        {
            _found = new int[Math.Max(count, _found.Length * 2)];
        }

        Array.Fill(_found, -1, 0, count);
        if (count == 0)
        {
            return;
        }

        var right = state.Replayed.Length + 1;
        var time = right + state.RightTime;
        await foreach (var batch in _seek.ExecuteAsync(ct).ConfigureAwait(false))
        {
            var columns = batch.Columns;
            var places = columns[right - 1].Lanes<long>();
            for (var i = 0; i < batch.Count; i++)
            {
                var row = batch.RowAt(i);
                var place = places[row];
                if ((ulong)place >= (ulong)count)
                {
                    throw new InvalidOperationException("a maintained as-of seek found a row for a left row it did not seek.");
                }

                // A match's time is never NULL: the seek's left join pads only the rows it matched to nothing.
                if (columns[time].IsValid(row))
                {
                    _found[place] = state.Found.Add(columns[right..], row);
                }
            }
        }
    }

    /// <summary>Whether a held match and a match found have the same values, bit for bit; both none is the same.</summary>
    private bool Same(int held, int found)
    {
        if (held < 0 || found < 0)
        {
            return held < 0 && found < 0;
        }

        _state.RightIdentity.Encode(_first, _state.Matches, _rightColumns, held, normalise: false);
        _state.RightIdentity.Encode(_second, _state.Found, _rightColumns, found, normalise: false);
        return _first.Span.SequenceEqual(_second.Span);
    }

    /// <summary>The left rows taken out, each with the match it had; a held row none of whose copies are left goes.</summary>
    private void Leave()
    {
        var state = _state;
        var changes = state.LeftChanges;
        for (var row = 0; row < changes.Count; row++)
        {
            var weight = changes.Weight(row);
            if (weight >= 0)
            {
                continue;
            }

            if (!Matchable(row))
            {
                // A NULL key or time matches nothing: a left join padded the row, an inner join dropped it.
                if (state.LeftJoin)
                {
                    WriteChange(row, -1, weight);
                }

                continue;
            }

            if (!state.LeftSlots.TryGet(changes.Identity(row), out var slot))
            {
                throw new InvalidOperationException("a maintained as-of seek was asked to take out a row it does not hold.");
            }

            WriteHeld(slot, weight);
            state.LeftCounts[slot] += weight;
            if (state.LeftCounts[slot] < 0)
            {
                throw new InvalidOperationException("a maintained as-of seek took out more copies of a row than it held.");
            }

            if (state.LeftCounts[slot] == 0)
            {
                state.LeftSlots.Remove(changes.Identity(row), out _);
                state.LeftBuckets.Remove(slot);
                if (state.LeftMatches[slot] >= 0)
                {
                    state.Matches.Remove(state.LeftMatches[slot]);
                }

                state.Left.Remove(slot);
            }
        }
    }

    /// <summary>The left rows put in: more copies of a held row, with its match now, or a row arriving, with the match found.</summary>
    private void Arrive()
    {
        var state = _state;
        var changes = state.LeftChanges;
        var columns = changes.Columns;
        for (var row = 0; row < changes.Count; row++)
        {
            var weight = changes.Weight(row);
            if (weight <= 0)
            {
                continue;
            }

            if (_arriving[row] < 0 && !Matchable(row))
            {
                if (state.LeftJoin)
                {
                    WriteChange(row, -1, weight);
                }

                continue;
            }

            if (_arriving[row] < 0)
            {
                var slot = state.LeftSlots.TryGet(changes.Identity(row), out var held)
                    ? held
                    : throw new InvalidOperationException("a maintained as-of seek lost a left row it holds.");
                WriteHeld(slot, weight);
                state.LeftCounts[slot] += weight;
                continue;
            }

            var found = _found[_arriving[row]];
            WriteChange(row, found, weight);
            var fresh = state.Left.Add(columns, row);
            state.LeftSlots.Set(changes.Identity(row), fresh);
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
            state.LeftMatches[fresh] = found < 0 ? -1 : state.Matches.Add(state.Found, found);
            state.LeftKeys.Encode(state.Encoder, columns, row, normalise: true);
            state.LeftBuckets.Add(state.Encoder.Span, fresh);
        }
    }

    /// <summary>A held left row with its match, or NULLs for a left join's unmatched row; nothing for an inner join's.</summary>
    private void WriteHeld(int slot, long weight)
    {
        var state = _state;
        var match = state.LeftMatches[slot];
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

        WriteRight(into, state.Matches, match);
        into.End(weight);
    }

    /// <summary>A row of the left changes with what the seek found for it, as <see cref="WriteHeld"/> writes a held one.</summary>
    private void WriteChange(int row, int found, long weight)
    {
        var state = _state;
        if (found < 0 && !state.LeftJoin)
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

        WriteRight(into, state.Found, found);
        into.End(weight);
    }

    private void WriteRight(ChangeWriter into, RowStore right, int match)
    {
        var state = _state;
        for (var c = 0; c < state.RightWidth; c++)
        {
            if (match < 0)
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

using Chalk.Catalog;
using Chalk.Execution.Operators;
using Chalk.Execution.Vectors;
using Chalk.Ir;
using Chalk.Sources;
using ArrowSchema = Apache.Arrow.Schema;

namespace Chalk.Execution.Maintenance;

/// <summary>
/// A maintained as-of seek (D343, design 68 §13–§14): an as-of join that seeks an in-process table's
/// index, kept without the table's rows. It holds its left rows by key, each with the values of the
/// right row it matches now, and matches again only the left rows whose match may have moved.
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
/// <para>
/// Most matches need no seek (§14). A row that leaves hands its match to a row that arrives with the
/// same key and time, as when only columns the seek does not read moved. And a seek back from a time
/// keeps each key's last row: a left row at or after it — at the clock, in the usual case — matches it.
/// The last row is what the seek finds from the largest time the time's layout holds, and it holds
/// until a row is appended under its key.
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
        AsOfMatch match,
        bool leftJoin,
        ReadState keys,
        int batchSize)
    {
        LeftWidth = leftTypes.Count;
        RightWidth = rightTypes.Count;
        Replayed = [.. leftKeys, leftTime];
        LeftKeys = new KeyColumns(leftTypes, leftKeys);
        Handed = new KeyColumns(leftTypes, Replayed);
        TableKeys = new KeyColumns(keyTypes, [.. Enumerable.Range(0, leftKeys.Length)]);
        RightIdentity = KeyColumns.All(rightTypes);
        LeftTime = leftTime;
        RightTime = rightTime;
        LeftJoin = leftJoin;
        Keys = keys;
        Left = new RowStore(leftTypes);
        Matches = new RowStore(rightTypes);
        Found = new RowStore(rightTypes);
        Lasts = new RowStore(rightTypes);
        LeftChanges = new ChangeSet(leftTypes);
        Pending = new ChangeWriter([.. Replayed.Select(c => leftTypes[c]), Place], batchSize);
        Output = new ChangeWriter(outputTypes, batchSize);

        // A key's last row stands in for a seek back from a later time, when the two times are one type
        // laid out as integers, which the index compares as the lanes do.
        var leftTimeType = leftTypes[leftTime];
        if (match is AsOfMatch.Ge or AsOfMatch.Gt
            && leftTimeType.WithNullable(false) == rightTypes[rightTime].WithNullable(false)
            && Largest(ColumnKinds.Of(leftTimeType)) is { } largest)
        {
            LargestTime = largest;
            Strict = match == AsOfMatch.Gt;
            Times = new TimeOrder(leftTimeType);
        }
    }

    /// <summary>The type of a replayed row's place among the rows replayed, after its own columns.</summary>
    public static ChalkType Place { get; } = ChalkType.Int64();

    public int LeftWidth { get; }

    public int RightWidth { get; }

    /// <summary>The left columns a row is replayed with, its keys and then its time; its place follows them.</summary>
    public int[] Replayed { get; }

    /// <summary>A left row's equality keys, encoded as a table row's are.</summary>
    public KeyColumns LeftKeys { get; }

    /// <summary>A left row's keys and time: what a match handed over is known by.</summary>
    public KeyColumns Handed { get; }

    /// <summary>A table row's keys: the columns of the read of its keys.</summary>
    public KeyColumns TableKeys { get; }

    public KeyColumns RightIdentity { get; }

    public int LeftTime { get; }

    /// <summary>The right time's place among the right columns: never NULL in a match, so it marks one.</summary>
    public int RightTime { get; }

    public bool LeftJoin { get; }

    /// <summary>The read of the table's keys, which says too when the table was rewritten.</summary>
    public ReadState Keys { get; }

    /// <summary>The largest time the time's layout holds, to seek a key's last row from; null when last rows are not kept.</summary>
    public byte[]? LargestTime { get; }

    /// <summary>Whether the seek takes rows strictly before the left time.</summary>
    public bool Strict { get; }

    public TimeOrder? Times { get; }

    public RowStore Left { get; }

    /// <summary>The right values each held left row matches, at <see cref="LeftMatches"/>.</summary>
    public RowStore Matches { get; }

    /// <summary>What this update found for the left rows it matched, and the matches handed over.</summary>
    public RowStore Found { get; }

    /// <summary>Each key's last row, at <see cref="LastIndex"/>.</summary>
    public RowStore Lasts { get; }

    /// <summary>Each key's slot of <see cref="Lasts"/>, or -1 for a key with no row; a key not here is not known.</summary>
    public ByteMap LastIndex { get; } = new();

    /// <summary>The matches of the rows leaving in this update, by key and time: slots of <see cref="Found"/>, or -1.</summary>
    public ByteMap HandedOver { get; } = new();

    /// <summary>The left rows held: how many times, by key, and the slot of <see cref="Matches"/> each matches, or -1.</summary>
    public long[] LeftCounts { get; set; } = [];

    public int[] LeftMatches { get; set; } = [];

    /// <summary>Each held left row's slot, by its identity.</summary>
    public ByteMap LeftSlots { get; } = new();

    public Buckets LeftBuckets { get; } = new();

    public ChangeSet LeftChanges { get; }

    /// <summary>The left rows this update seeks for, as they are replayed to the seek.</summary>
    public ChangeWriter Pending { get; }

    public ChangeWriter Output { get; }

    public KeyEncoder Encoder { get; } = new();

    public long Bytes =>
        Left.Bytes + Matches.Bytes + Found.Bytes + Lasts.Bytes + LeftChanges.Bytes + Pending.Bytes + Output.Bytes
        + ((LeftCounts.Length * sizeof(long)) + (LeftMatches.Length * sizeof(int)))
        + LeftSlots.Bytes + LeftBuckets.Bytes + LastIndex.Bytes + HandedOver.Bytes;

    public void Discard()
    {
        Left.Clear();
        Matches.Clear();
        Found.Clear();
        Lasts.Clear();
        LastIndex.Clear();
        HandedOver.Clear();
        LeftChanges.Clear();
        Pending.Clear();
        Output.Clear();
        LeftSlots.Clear();
        LeftBuckets.Clear();
    }

    /// <summary>The largest value of an integer layout, as its lane; null for any other layout.</summary>
    private static byte[]? Largest(ColumnKind kind) => kind switch
    {
        ColumnKind.Int8 => [0x7F],
        ColumnKind.Int16 => BitConverter.GetBytes(short.MaxValue),
        ColumnKind.Int32 => BitConverter.GetBytes(int.MaxValue),
        ColumnKind.Int64 => BitConverter.GetBytes(long.MaxValue),
        _ => null,
    };
}

/// <summary>The left rows a maintained seek seeks for in this update: their keys, time and place, without weights.</summary>
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
    /// <summary>
    /// A match not known yet: the row waits for its key's last row, or for a seek. Below every slot of
    /// <see cref="SeekState.Lasts"/> as <see cref="_found"/> encodes one.
    /// </summary>
    private const int Probing = int.MinValue;

    private const int Seeking = int.MinValue + 1;

    private readonly SeekState _state;
    private readonly IBatchOperator _left;
    private readonly IBatchOperator _keys;
    private readonly IBatchOperator _seek;
    private readonly IBatchOperator? _probe;
    private readonly int[] _rightColumns;
    private readonly ByteMap _reached = new();
    private readonly ByteMap _probing = new();
    private readonly KeyEncoder _first = new();
    private readonly KeyEncoder _second = new();
    private readonly KeyEncoder _key = new();
    private readonly KeyEncoder _handed = new();

    /// <summary>Each row to match's owner: a held slot, or the complement of a row of the left changes.</summary>
    private readonly List<int> _owners = [];

    /// <summary>Each key sought from the largest time: the row to match it was first needed for.</summary>
    private readonly List<int> _probes = [];

    /// <summary>
    /// What each row to match matches: -1 for nothing; a slot of <see cref="SeekState.Found"/>; a slot
    /// <c>s</c> of <see cref="SeekState.Lasts"/> as <c>-(s + 2)</c>; or not known yet.
    /// </summary>
    private int[] _found = [];

    /// <summary>Each row of the left changes' place among the rows to match, or -1.</summary>
    private int[] _arriving = [];

    /// <summary>What each key sought from the largest time found: a slot of <see cref="SeekState.Lasts"/>, or -1.</summary>
    private int[] _lasts = [];

    public SeekChanges(
        OperatorContext context,
        string path,
        ArrowSchema schema,
        IReadOnlyList<ChalkType> types,
        SeekState state,
        IBatchOperator left,
        IBatchOperator keys,
        IBatchOperator seek,
        IBatchOperator? probe)
        : base(context, path, schema, types)
    {
        _state = state;
        _left = left;
        _keys = keys;
        _seek = seek;
        _probe = probe;
        _rightColumns = [.. Enumerable.Range(0, state.RightWidth)];
    }

    protected override async ValueTask DisposeCoreAsync()
    {
        await _left.DisposeAsync().ConfigureAwait(false);
        await _keys.DisposeAsync().ConfigureAwait(false);
        await _seek.DisposeAsync().ConfigureAwait(false);
        if (_probe is not null)
        {
            await _probe.DisposeAsync().ConfigureAwait(false);
        }
    }

    protected override async ValueTask<ChangeWriter> ComputeAsync(CancellationToken ct)
    {
        var state = _state;
        await DrainAsync(_left, state.LeftChanges, ct).ConfigureAwait(false);
        await ReachAsync(ct).ConfigureAwait(false);
        state.Output.Clear();
        state.Found.Clear();
        state.HandedOver.Clear();
        _owners.Clear();
        _probes.Clear();
        _probing.Clear();
        Forget();

        // The rows leaving first, with the matches they had: a row that leaves is not matched again,
        // and its match is handed to a row arriving with its key and time.
        Leave();

        // The held left rows whose match may have moved: every one after a rewrite, and otherwise those
        // under a key a row was appended to.
        if (state.Keys.Rewritten)
        {
            for (var entry = 0; entry < state.LeftSlots.Entries; entry++)
            {
                if (state.LeftSlots.IsLive(entry))
                {
                    Owe(state.LeftSlots.Value(entry));
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
                        Owe(slot);
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
                ? Owe(~row)
                : -1;
        }

        // The keys whose last row is not known, sought from the largest time; then what their rows match.
        if (_probes.Count > 0)
        {
            await ProbeAsync(ct).ConfigureAwait(false);
            for (var i = 0; i < _owners.Count; i++)
            {
                if (_found[i] == Probing)
                {
                    _found[i] = Resolve(_owners[i], probe: false);
                }
            }
        }

        await SeekAsync(ct).ConfigureAwait(false);

        // The held rows matched again: their old output out and their new in, where the match's values moved.
        for (var i = 0; i < _owners.Count; i++)
        {
            var slot = _owners[i];
            if (slot < 0)
            {
                continue;
            }

            var old = state.LeftMatches[slot];
            var found = _found[i];
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

            state.LeftMatches[slot] = Keep(found);
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

    /// <summary>
    /// Forgets the last rows a rewrite or an append may have moved; and all of them once they outnumber
    /// the rows held, so that keys no row holds any more are not kept for ever.
    /// </summary>
    private void Forget()
    {
        var state = _state;
        if (state.LargestTime is null)
        {
            return;
        }

        if (state.Keys.Rewritten || state.LastIndex.Count > Math.Max(1024, 4 * state.Left.Count))
        {
            state.LastIndex.Clear();
            state.Lasts.Clear();
            return;
        }

        for (var entry = 0; entry < _reached.Entries; entry++)
        {
            if (_reached.IsLive(entry) && state.LastIndex.Remove(_reached.Key(entry), out var last) && last >= 0)
            {
                state.Lasts.Remove(last);
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

    /// <summary>A row to match, a held slot or the complement of a row of the left changes: its place among them.</summary>
    private int Owe(int owner)
    {
        var place = _owners.Count;
        _owners.Add(owner);
        if (_found.Length <= place)
        {
            Array.Resize(ref _found, Math.Max(16, _found.Length * 2));
        }

        _found[place] = Resolve(owner, probe: true);
        return place;
    }

    /// <summary>
    /// What a row matches, if that is known without a seek: a match handed over with its key and time,
    /// or its key's last row when the row's time is at or after it. Otherwise <see cref="Probing"/> when
    /// its key's last row is not known and <paramref name="probe"/> asks for it, or <see cref="Seeking"/>.
    /// </summary>
    private int Resolve(int owner, bool probe)
    {
        var state = _state;
        var time = TimeOf(owner);
        if (owner < 0 && state.HandedOver.Count > 0)
        {
            state.Handed.Encode(_handed, state.LeftChanges.Columns, ~owner, normalise: true);
            if (state.HandedOver.TryGet(_handed.Span, out var handed))
            {
                return handed;
            }
        }

        if (state.LargestTime is null)
        {
            return Seeking;
        }

        KeyOf(owner);
        if (!state.LastIndex.TryGet(_key.Span, out var last))
        {
            if (!probe)
            {
                return Seeking;
            }

            ref var at = ref _probing.GetOrAdd(_key.Span, out var added);
            if (added)
            {
                at = _probes.Count;
                _probes.Add(owner);
            }

            return Probing;
        }

        if (last < 0)
        {
            return -1;
        }

        var order = state.Times!.Compare(time, state.Lasts.Column(state.RightTime).Lane(last));
        return order > 0 || (order == 0 && !state.Strict) ? -(last + 2) : Seeking;
    }

    /// <summary>A row to match's keys, normalised, into <see cref="_key"/>.</summary>
    private void KeyOf(int owner)
    {
        var state = _state;
        if (owner >= 0)
        {
            state.LeftKeys.Encode(_key, state.Left, state.LeftKeys.Columns, owner, normalise: true);
        }
        else
        {
            state.LeftKeys.Encode(_key, state.LeftChanges.Columns, ~owner, normalise: true);
        }
    }

    private ReadOnlySpan<byte> TimeOf(int owner)
    {
        var state = _state;
        if (owner >= 0)
        {
            return state.Left.Column(state.LeftTime).Lane(owner);
        }

        var column = state.LeftChanges.Columns[state.LeftTime];
        var width = state.Left.Column(state.LeftTime).Width;
        return column.RawLanes(width).Slice(~owner * width, width);
    }

    /// <summary>Seeks each key whose last row is not known from the largest time, and keeps what it finds.</summary>
    private async ValueTask ProbeAsync(CancellationToken ct)
    {
        var state = _state;
        state.Pending.Clear();
        var keys = state.Replayed.Length - 1;
        for (var p = 0; p < _probes.Count; p++)
        {
            var owner = _probes[p];
            var into = state.Pending;
            into.Begin();
            Replay(into, owner, keys);
            into.Column(keys).AppendLane(state.LargestTime!);
            into.Column(keys + 1).AppendInt64(p);
            into.End(1);
        }

        if (_lasts.Length < _probes.Count)
        {
            _lasts = new int[Math.Max(_probes.Count, _lasts.Length * 2)];
        }

        Array.Fill(_lasts, -1, 0, _probes.Count);
        await RunAsync(_probe!, _lasts, state.Lasts, ct).ConfigureAwait(false);
        for (var p = 0; p < _probes.Count; p++)
        {
            KeyOf(_probes[p]);
            state.LastIndex.Set(_key.Span, _lasts[p]);
        }
    }

    /// <summary>Seeks for the rows whose match is still not known, each from its own time.</summary>
    private async ValueTask SeekAsync(CancellationToken ct)
    {
        var state = _state;
        state.Pending.Clear();
        var seeking = false;
        for (var i = 0; i < _owners.Count; i++)
        {
            if (_found[i] != Seeking)
            {
                continue;
            }

            seeking = true;
            var into = state.Pending;
            into.Begin();
            Replay(into, _owners[i], state.Replayed.Length);
            into.Column(state.Replayed.Length).AppendInt64(i);
            into.End(1);
            _found[i] = -1;
        }

        if (seeking)
        {
            await RunAsync(_seek, _found, state.Found, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Writes the first <paramref name="columns"/> replayed columns of a row to match.</summary>
    private void Replay(ChangeWriter into, int owner, int columns)
    {
        var state = _state;
        var replayed = state.Replayed;
        if (owner >= 0)
        {
            for (var c = 0; c < columns; c++)
            {
                into.Column(c).Append(state.Left.Column(replayed[c]), owner);
            }

            return;
        }

        var changes = state.LeftChanges.Columns;
        for (var c = 0; c < columns; c++)
        {
            into.Column(c).Append(changes[replayed[c]], ~owner);
        }
    }

    /// <summary>
    /// Runs the statement's own seek over the rows replayed, and keeps what it found for each in
    /// <paramref name="store"/>, at <paramref name="found"/>[place]; a row it matched to nothing keeps its entry.
    /// </summary>
    private async ValueTask RunAsync(IBatchOperator matcher, int[] found, RowStore store, CancellationToken ct)
    {
        var state = _state;
        var right = state.Replayed.Length + 1;
        var time = right + state.RightTime;
        await foreach (var batch in matcher.ExecuteAsync(ct).ConfigureAwait(false))
        {
            var columns = batch.Columns;
            var places = columns[right - 1].Lanes<long>();
            for (var i = 0; i < batch.Count; i++)
            {
                var row = batch.RowAt(i);
                var place = places[row];
                if ((ulong)place >= (ulong)found.Length)
                {
                    throw new InvalidOperationException("a maintained as-of seek found a row for a left row it did not seek.");
                }

                // A match's time is never NULL: the seek's left join pads only the rows it matched to nothing.
                if (columns[time].IsValid(row))
                {
                    found[place] = store.Add(columns[right..], row);
                }
            }
        }
    }

    /// <summary>The store and slot a match is held at; <paramref name="found"/> is not -1.</summary>
    private RowStore At(int found, out int slot)
    {
        if (found >= 0)
        {
            slot = found;
            return _state.Found;
        }

        slot = -found - 2;
        return _state.Lasts;
    }

    /// <summary>A copy of a match to hold, or -1 for none.</summary>
    private int Keep(int found)
    {
        if (found == -1)
        {
            return -1;
        }

        var store = At(found, out var slot);
        return _state.Matches.Add(store, slot);
    }

    /// <summary>Whether a held match and a match found have the same values, bit for bit; both none is the same.</summary>
    private bool Same(int held, int found)
    {
        if (held < 0 || found == -1)
        {
            return held < 0 && found == -1;
        }

        var store = At(found, out var slot);
        _state.RightIdentity.Encode(_first, _state.Matches, _rightColumns, held, normalise: false);
        _state.RightIdentity.Encode(_second, store, _rightColumns, slot, normalise: false);
        return _first.Span.SequenceEqual(_second.Span);
    }

    /// <summary>
    /// The left rows taken out, each with the match it had; a held row none of whose copies are left goes.
    /// The match of a row whose key nothing was appended under is handed over by its key and time.
    /// </summary>
    private void Leave()
    {
        var state = _state;
        var changes = state.LeftChanges;
        var columns = changes.Columns;
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

            var match = state.LeftMatches[slot];
            if (!state.Keys.Rewritten && !_reached.TryGet(state.Encoder.Span, out _))
            {
                state.Handed.Encode(_handed, columns, row, normalise: true);
                ref var handed = ref state.HandedOver.GetOrAdd(_handed.Span, out var added);
                if (added)
                {
                    handed = match < 0 ? -1 : state.Found.Add(state.Matches, match);
                }
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
                if (match >= 0)
                {
                    state.Matches.Remove(match);
                }

                state.Left.Remove(slot);
            }
        }
    }

    /// <summary>The left rows put in: more copies of a held row, with its match now, or a row arriving, with its match.</summary>
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
            state.LeftMatches[fresh] = Keep(found);
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

        WriteRight(into, match < 0 ? null : state.Matches, match);
        into.End(weight);
    }

    /// <summary>A row of the left changes with what it matches, as <see cref="WriteHeld"/> writes a held one.</summary>
    private void WriteChange(int row, int found, long weight)
    {
        var state = _state;
        if (found == -1 && !state.LeftJoin)
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

        if (found == -1)
        {
            WriteRight(into, null, -1);
        }
        else
        {
            var store = At(found, out var slot);
            WriteRight(into, store, slot);
        }

        into.End(weight);
    }

    private void WriteRight(ChangeWriter into, RowStore? right, int slot)
    {
        var state = _state;
        for (var c = 0; c < state.RightWidth; c++)
        {
            if (right is null)
            {
                into.Column(state.LeftWidth + c).AppendNull();
            }
            else
            {
                into.Column(state.LeftWidth + c).Append(right.Column(c), slot);
            }
        }
    }
}

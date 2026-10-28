using RecordBatch = Apache.Arrow.RecordBatch;
using Chalk.Catalog;
using Chalk.Execution.Operators;
using Chalk.Execution.Vectors;
using Chalk.Sources;

namespace Chalk.Execution.Maintenance;

/// <summary>
/// A statement kept by incremental maintenance (D343, <c>docs/design/68-incremental-maintenance.md</c>):
/// its plan rewritten to run on changes, the state its operators keep, and the rows of its result by
/// key. Each update brings the state to one epoch and says which keys' rows changed.
/// </summary>
/// <remarks>
/// <para>
/// Updates are taken one at a time, by the view that owns the plan. An update pins every table it reads
/// at one epoch, as any execution does (F188), and reads the time it is given, the epoch's clock.
/// </para>
/// <para>
/// An update that fails part way leaves the operators' state behind: it is discarded, and the next
/// update computes the statement from nothing, as the first did, and gives the whole result (§7).
/// </para>
/// <para>
/// The batches an update gives are valid until the next update or until the plan is disposed. The
/// view that owns the plan reads them before it asks for the next update. An update's changes are
/// written in its arena's memory, which the next update gives back for its own, so that steady updates
/// reuse the same buffers; a whole result is written in the GC's, as a one-off.
/// </para>
/// </remarks>
internal sealed class MaintainedPlan : IAsyncDisposable
{
    private readonly CompiledPlan _compiled;
    private readonly OperatorFactory _root;
    private readonly List<IMaintainedState> _states;
    private readonly TableDiffs _diffs;
    private readonly ResultState _result;
    private readonly IReadOnlyList<ChalkType> _outputTypes;
    private Tree? _tree;
    private bool _restart = true;

    // Whether the states held, after the last update, a quarter of the rows they have room for or fewer.
    private bool _sparse;

    /// <summary>The fewest slots, across the states, for emptiness to count: below it a rebuild saves little.</summary>
    private const long SparseSlots = 1 << 16;

    // The batches the last update gave, valid until the next update lets go of them.
    private MaintainedChanges? _given;

    // The rebindable slots, the values the last update bound them to, and how many times they have moved.
    private int[] _rebindable = [];
    private object?[]? _rebound;
    private long _bindings;

    private MaintainedPlan(CompiledPlan compiled, OperatorFactory root, List<IMaintainedState> states, TableDiffs diffs)
    {
        _compiled = compiled;
        _root = root;
        _states = states;
        _diffs = diffs;
        _outputTypes = compiled.OutputTypes;
        _result = new ResultState(_outputTypes);
    }

    /// <summary>
    /// The plan of <paramref name="compiled"/> rewritten to be maintained, or a refusal naming the first
    /// thing in it that cannot be (§8). <paramref name="clockSlots"/> are the parameters bound to the clock,
    /// and <paramref name="rebindableSlots"/> those a view may bind again between updates (design 77 §5).
    /// Both move: a node that reads one holds what it reads, and runs over it again when one moves.
    /// </summary>
    public static MaintainedPlan Create(
        CompiledPlan compiled, IReadOnlyCollection<int> clockSlots, IReadOnlyCollection<int>? rebindableSlots = null)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        int[] rebindable = [.. (rebindableSlots ?? []).Distinct().Order()];
        var compiler = new MaintainedCompiler(
            compiled.Plan, compiled.Catalog, compiled.Sources, compiled.Settings, [.. clockSlots.Union(rebindable)], clockSlots);
        OperatorFactory root;
        try
        {
            root = compiler.Compile();
        }
        catch (NotMaintainedException refusal) when (rebindable.Length > 0 && refusal.Message.Contains("clock", StringComparison.Ordinal))
        {
            // A rebindable parameter is kept as the clock is, so a refusal of a read of the clock may be of one.
            throw new NotMaintainedException(
                refusal.Message + " A rebindable parameter is kept as the clock is, and is refused where the clock is.")
            {
                Path = refusal.Path,
            };
        }

        var plan = new MaintainedPlan(compiled, root, compiler.States, compiler.Diffs) { _rebindable = rebindable };

        // Build a tree once, so that a kernel or a type the operators cannot take is refused now.
        plan._tree = plan.Build();
        return plan;
    }

    /// <summary>Bytes the operators and the result hold, against the view's state budget.</summary>
    public long StateBytes => _states.Sum(s => s.Bytes) + _result.Bytes;

    /// <summary>Every state the plan's operators keep, for a test to look into.</summary>
    internal IReadOnlyList<IMaintainedState> States => _states;

    /// <summary>
    /// Whether the states, after the last update, hold a quarter of the rows they have room for or fewer,
    /// over at least <see cref="SparseSlots"/> slots (F270). Their stores keep the room their fullest
    /// moment needed, so a statement whose inputs shrank — a log compacted, a window passed — holds memory
    /// for rows it no longer has, and a fresh statement over the same rows would hold a fraction of it.
    /// Measured from counts the stores keep, so it costs nothing.
    /// </summary>
    internal bool IsSparse => _sparse;

    private bool Sparse()
    {
        var (live, slots) = (0L, 0L);
        foreach (var state in _states)
        {
            var (held, room) = state.Occupancy;
            live += held;
            slots += room;
        }

        return slots >= SparseSlots && live * 4 <= slots;
    }

    /// <summary>The result's rows by key, for a test to weigh.</summary>
    internal ResultState Result => _result;

    /// <summary>The operators of the tree the last update ran that keep scratch, for a test to weigh.</summary>
    internal IReadOnlyList<IKeepsScratch> Scratch => _tree?.Context.Scratch ?? [];

    /// <summary>The result's key columns: positions in the output.</summary>
    public int[] Key
    {
        get => _result.Key;
        set => _result.Key = value;
    }

    /// <summary>
    /// Brings the plan to the epoch its tables are pinned at now, with <paramref name="parameters"/>
    /// bound and <paramref name="now"/> as the current time, and returns which keys' rows changed — or,
    /// when <paramref name="whole"/> or after a restart, every row the result holds.
    /// </summary>
    /// <param name="relations">
    /// The relations the statement's context binds by name, as an execution binds them: a list too
    /// long to fold into the plan, read where the plan reads it (D361). Null for a statement that binds
    /// none.
    /// </param>
    public async ValueTask<MaintainedChanges> UpdateAsync(
        IReadOnlyList<object?> parameters,
        DateTimeOffset now,
        bool whole,
        ExecutionArena arena,
        ExecutionStats stats,
        CancellationToken ct,
        IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyList<object?>>>? relations = null)
    {
        try
        {
            return await AttemptAsync(parameters, now, whole, arena, stats, relations, ct).ConfigureAwait(false);
        }
        catch (TopNRefillException)
        {
            // A bounded LIMIT ran short of rows it no longer holds (D360). The failed attempt discarded
            // every state, so this one computes from nothing, as a restart does.
            return await AttemptAsync(parameters, now, whole, arena, stats, relations, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// One attempt, with what an operator raises for the plan as a whole taken out of the execution's
    /// own failure: a key the rows before a LIMIT give twice, or a LIMIT that must read again (D360).
    /// </summary>
    private async ValueTask<MaintainedChanges> AttemptAsync(
        IReadOnlyList<object?> parameters,
        DateTimeOffset now,
        bool whole,
        ExecutionArena arena,
        ExecutionStats stats,
        IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyList<object?>>>? relations,
        CancellationToken ct)
    {
        try
        {
            return await UpdateOnceAsync(parameters, now, whole, arena, stats, relations, ct).ConfigureAwait(false);
        }
        catch (ExecutionException wrapped) when (wrapped.InnerException is MaintainedKeyException or TopNRefillException)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(wrapped.InnerException);
            throw;
        }
    }

    /// <summary>
    /// How many times the rebindable slots have moved, counting this update's values: one more than
    /// before when any differs from the last update's. A value that cannot be told equal, such as a byte
    /// array, counts as moved, which only runs the nodes that read it again.
    /// </summary>
    private long Bindings(IReadOnlyList<object?> parameters)
    {
        if (_rebindable.Length == 0)
        {
            return 0;
        }

        var last = _rebound ??= new object?[_rebindable.Length];
        var moved = false;
        for (var i = 0; i < _rebindable.Length; i++)
        {
            var value = parameters[_rebindable[i]];
            if (!Equals(value, last[i]) || value is Array)
            {
                moved = true;
                last[i] = value;
            }
        }

        return moved ? ++_bindings : _bindings;
    }

    private async ValueTask<MaintainedChanges> UpdateOnceAsync(
        IReadOnlyList<object?> parameters,
        DateTimeOffset now,
        bool whole,
        ExecutionArena arena,
        ExecutionStats stats,
        IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyList<object?>>>? relations,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(arena);
        var bound = ParameterBinder.Bind(parameters, _compiled.BoundTypes);
        var bindings = Bindings(parameters);
        var tree = _tree ??= Build();
        var completed = false;

        // The last update's batches have been read: the memory of its changes goes back for this update's.
        LetGoOfGiven();
        arena.BeginExecution(stats);
        try
        {
            tree.Context.ReadSet ??= _compiled.NewReadSet();
            CompiledPlan.Pin(tree.Context.ReadSet, arena, _compiled.Settings.EngineCommits);
            tree.Context.Begin(stats, arena, bound, now, relations, bindings);
            _ = tree.Root.ExecuteAsync(ct);
            try
            {
                if (_restart)
                {
                    foreach (var state in _states)
                    {
                        state.Discard();
                    }

                    _result.Restart();
                }

                _result.Begin();
                while (true)
                {
                    if (!tree.Root.TryNext(out var batch))
                    {
                        if (tree.Root.IsDone || !await tree.Root.WaitAsync(ct).ConfigureAwait(false))
                        {
                            break;
                        }

                        batch = tree.Root.Current;
                    }

                    ct.ThrowIfCancellationRequested();
                    _result.Add(batch!);
                }

                var changes = _given = _result.End(_restart || whole, _restart, tree.Output);
                changes.Commit = arena.PinnedCommit;
                if (_restart)
                {
                    // An update from nothing read every row of its tables, and the buffers its changes
                    // passed through grew to hold them. Later updates read what changed: the buffers are
                    // let go now rather than held for the life of the view.
                    foreach (var state in _states)
                    {
                        state.Trim();
                    }

                    _result.Trim();
                    foreach (var keeper in tree.Context.Scratch)
                    {
                        keeper.Trim();
                    }
                }

                _restart = false;
                _sparse = Sparse();
                completed = true;
                return changes;
            }
            finally
            {
                await tree.Root.DisposeAsync().ConfigureAwait(false);
                CompiledPlan.Unpin(tree.Context.ReadSet, arena);
                tree.Context.End();
            }
        }
        finally
        {
            _diffs.Clear();
            arena.EndExecution();
            if (!completed)
            {
                // The operators may hold half an update: the next starts from nothing, on a fresh tree.
                _restart = true;
                _tree = null;
                foreach (var state in _states)
                {
                    state.Discard();
                }
            }
        }
    }

    /// <summary>Lets go of every table state the plan's reads hold, and of the last update's batches.</summary>
    public ValueTask DisposeAsync()
    {
        LetGoOfGiven();
        foreach (var state in _states)
        {
            state.Discard();
        }

        _tree = null;
        return default;
    }

    /// <summary>Lets go of the batches the last update gave: the memory of its changes goes back to its arena.</summary>
    private void LetGoOfGiven()
    {
        if (_given is { } given)
        {
            _given = null;
            given.Now.Dispose();
            given.Before.Dispose();
        }
    }

    private Tree Build()
    {
        var context = _compiled.NewContext();
        IBatchOperator root;
        OutputMaterialiser output;
        using (new ScratchScope(context))
        {
            root = _root(context);
            output = new OutputMaterialiser(context, _compiled.OutputSchema, _outputTypes, pooled: true);
        }

        return new Tree(root, output, context);
    }

    private sealed record Tree(IBatchOperator Root, OutputMaterialiser Output, OperatorContext Context);
}

/// <summary>
/// What an update of a maintained plan changed (D343): for each key whose row changed, the row it held
/// before and the row it holds now, either absent. After a restart, every row the result holds, to be
/// taken as the whole result.
/// </summary>
internal sealed class MaintainedChanges
{
    /// <summary>True when <see cref="Now"/> is the whole result, as after the first update or a restart.</summary>
    public required bool Whole { get; init; }

    /// <summary>
    /// True when the update computed the result from nothing: the first, and the first after one that
    /// failed. An update asked for the whole result otherwise catches up, and only writes it whole.
    /// </summary>
    public required bool Restarted { get; init; }

    /// <summary>
    /// The rows keys hold now: those that changed, or every row when <see cref="Whole"/>. Valid until
    /// the plan's next update.
    /// </summary>
    public required RecordBatch Now { get; init; }

    /// <summary>
    /// The rows the changed keys held before, of keys that hold no row now or hold another key's. Valid
    /// until the plan's next update.
    /// </summary>
    public required RecordBatch Before { get; init; }

    /// <summary>How many refreshes the engine had committed when the update pinned its tables (design 78 §1), or -1.</summary>
    public long Commit { get; set; } = -1;
}

/// <summary>
/// The result of a maintained plan, by key (D343): its rows, and the keys an update changed. A key that
/// holds two rows faults the view, as a recompute that finds one twice does (design 64 §6 a).
/// </summary>
internal sealed class ResultState
{
    private readonly IReadOnlyList<ChalkType> _types;
    private readonly RowStore _rows;
    private readonly KeyColumns _identity;
    private readonly KeyEncoder _encoder = new();
    private readonly KeyEncoder _keyEncoder = new();
    private readonly ByteMap _slots = new();

    // Each key's row (F283): its slot when the key holds one row, or ~i for the slots in _several[i]
    // when it holds more, which faults the view at the update's end. Null when the key is every column
    // of the result: a key is then its row, and _slots finds it.
    private ByteMap? _keySlots = new();
    private readonly List<List<int>> _several = [];
    private readonly Stack<int> _freeSeveral = new();

    // The row each key the update touches held before it, or -1 for none or more than one: all an
    // update's end reads of what the key held. Kept between updates, its storage reused (F244).
    private readonly ByteMap _before = new();
    private readonly List<int> _removed = [];
    private long[] _counts = [];
    private int[] _key = [];
    private KeyColumns _keyColumns;
    private int[] _stored = [];
    private ChangeWriter? _now;
    private ChangeWriter? _was;
    private ColumnarBatch? _nowColumns;
    private ColumnarBatch? _wasColumns;

    public ResultState(IReadOnlyList<ChalkType> types)
    {
        _types = types;
        _rows = new RowStore(types);
        _identity = KeyColumns.All(types);
        _keyColumns = new KeyColumns(types, []);
    }

    public int[] Key
    {
        get => _key;
        set
        {
            _key = value;

            // A key of every column is the row's identity: encoded alike, so _slots is its index.
            var whole = value.Distinct().Count() == _types.Count;
            _keyColumns = whole ? _identity : new KeyColumns(_types, value);
            _stored = whole ? _identity.Columns : value;
            _keySlots = whole ? null : new ByteMap();
        }
    }

    /// <summary>
    /// The rows, their counts and identities, the key index, and the buffers kept from one update to
    /// the next: what the last update wrote, and room for the keys it touched (F223).
    /// </summary>
    public long Bytes =>
        _rows.Bytes + HeapSizes.Array(_counts.Length, sizeof(long)) + _slots.Bytes
        + (_keySlots?.Bytes ?? 0) + SeveralBytes
        + _before.Bytes + HeapSizes.Of(_removed, sizeof(int))
        + (_now?.Bytes ?? 0) + (_was?.Bytes ?? 0)
        + (_nowColumns is null ? 0 : HeapSizes.Batch(_types.Count)) + (_wasColumns is null ? 0 : HeapSizes.Batch(_types.Count));

    public void Restart()
    {
        _rows.Clear();
        _slots.Clear();
        _keySlots?.Clear();
        _freeSeveral.Clear();
        for (var i = _several.Count - 1; i >= 0; i--)
        {
            _several[i].Clear();
            _freeSeveral.Push(i);
        }

        _before.Clear();
        _removed.Clear();
    }

    public void Begin()
    {
        _before.Clear();
        _removed.Clear();
    }

    /// <summary>
    /// Lets go of what an update from nothing grew: room for every key it touched, and the whole
    /// result it wrote, which the batches it gave were copied out of.
    /// </summary>
    public void Trim()
    {
        _before.Trim();
        _removed.TrimExcess();
        _now?.Trim();
        _was?.Trim();

        // The batches view the writers' old buffers: they go with them.
        _nowColumns = null;
        _wasColumns = null;
    }

    /// <summary>Applies one batch of the root's changes: rows, then the weight.</summary>
    public void Add(ColumnarBatch batch)
    {
        var columns = batch.Columns;
        var weights = batch.Column(batch.ColumnCount - 1).Lanes<long>();
        for (var i = 0; i < batch.Count; i++)
        {
            var row = batch.RowAt(i);
            var weight = weights[row];
            if (weight == 0)
            {
                continue;
            }

            _identity.Encode(_encoder, columns, row, normalise: false);
            if (_slots.TryGet(_encoder.Span, out var slot))
            {
                Touch(slot);
                _counts[slot] += weight;
                if (_counts[slot] < 0)
                {
                    throw new InvalidOperationException("a maintained result took out more copies of a row than it held.");
                }

                if (_counts[slot] == 0)
                {
                    _slots.Remove(_encoder.Span, out _);
                    _keyColumns.Encode(_keyEncoder, _rows, _stored, slot, normalise: false);
                    Unindex(_keyEncoder.Span, slot);

                    // Kept until the update ends: the view reads the row the key held before.
                    _removed.Add(slot);
                }

                continue;
            }

            if (weight < 0)
            {
                throw new InvalidOperationException("a maintained result was asked to take out a row it does not hold.");
            }

            _keyColumns.Encode(_keyEncoder, columns, row, normalise: false);
            BeforeOf(_keyEncoder.Span);
            slot = _rows.Add(columns[.._types.Count], row);
            _slots.Set(_encoder.Span, slot);
            if (_counts.Length < _rows.Slots)
            {
                Array.Resize(ref _counts, StateGrowth.Next(_counts.Length, _rows.Slots));
            }

            _counts[slot] = weight;
            Index(_keyEncoder.Span, slot);
        }
    }

    /// <summary>Records that <paramref name="key"/> holds the row in <paramref name="slot"/>, beside any it holds.</summary>
    private void Index(ReadOnlySpan<byte> key, int slot)
    {
        if (_keySlots is null)
        {
            return;
        }

        ref var held = ref _keySlots.GetOrAdd(key, out var added);
        if (added)
        {
            held = slot;
        }
        else if (held >= 0)
        {
            // A second row: an upsert that adds before it takes out, or a key the view faults on. Its
            // list is reused from key to key, so that an upsert allocates nothing.
            var index = _freeSeveral.Count > 0 ? _freeSeveral.Pop() : Several();
            _several[index].Add(held);
            _several[index].Add(slot);
            held = ~index;
        }
        else
        {
            _several[~held].Add(slot);
        }
    }

    /// <summary>Records that <paramref name="key"/> no longer holds the row in <paramref name="slot"/>.</summary>
    private void Unindex(ReadOnlySpan<byte> key, int slot)
    {
        if (_keySlots is null)
        {
            return;
        }

        _keySlots.TryGet(key, out var held);
        if (held >= 0)
        {
            _keySlots.Remove(key, out _);
            return;
        }

        var several = _several[~held];
        several.Remove(slot);
        if (several.Count == 1)
        {
            _keySlots.Set(key, several[0]);
            several.Clear();
            _freeSeveral.Push(~held);
        }
    }

    private int Several()
    {
        _several.Add(new List<int>(2));
        return _several.Count - 1;
    }

    /// <summary>The lists of keys that hold several rows, kept for reuse: almost always none.</summary>
    private long SeveralBytes
    {
        get
        {
            var bytes = HeapSizes.Of(_several, HeapSizes.Reference) + HeapSizes.Of(_freeSeveral, sizeof(int));
            foreach (var several in _several)
            {
                bytes += HeapSizes.Of(several, sizeof(int));
            }

            return bytes;
        }
    }

    /// <summary>
    /// Ends the update: checks each changed key holds at most one row, once, and writes the changes —
    /// or, when <paramref name="whole"/>, every row — as Arrow batches of the output's layout.
    /// <paramref name="restarted"/> says whether the update computed the result from nothing.
    /// </summary>
    public MaintainedChanges End(bool whole, bool restarted, OutputMaterialiser output)
    {
        // The writers are kept from update to update: the batches the last one gave were copied out.
        var now = _now ??= new ChangeWriter(_types, int.MaxValue);
        var before = _was ??= new ChangeWriter(_types, int.MaxValue);
        now.Clear();
        before.Clear();
        if (whole)
        {
            var keys = _keySlots ?? _slots;
            for (var entry = 0; entry < keys.Entries; entry++)
            {
                if (keys.IsLive(entry))
                {
                    now.Write(_rows, Checked(keys.Value(entry)), 1);
                }
            }
        }
        else
        {
            for (var entry = 0; entry < _before.Entries; entry++)
            {
                if (!_before.IsLive(entry))
                {
                    continue;
                }

                var after = (_keySlots ?? _slots).TryGet(_before.Key(entry), out var held) ? Checked(held) : -1;
                var was = _before.Value(entry);
                if (was == after)
                {
                    continue;
                }

                if (was >= 0)
                {
                    before.Write(_rows, was, 1);
                }

                if (after >= 0)
                {
                    now.Write(_rows, after, 1);
                }
            }
        }

        foreach (var slot in _removed)
        {
            _rows.Remove(slot);
        }

        _removed.Clear();
        _before.Clear();

        // Changes are written in the arena's memory, which the next update reuses. A whole result is
        // written in the GC's, so that the arena does not keep room for every row once updates are small.
        var pooled = !whole;
        var rows = Arrow(now, _nowColumns ??= new ColumnarBatch(_types), output, pooled);
        try
        {
            return new MaintainedChanges
            {
                Whole = whole,
                Restarted = restarted,
                Now = rows,
                Before = Arrow(before, _wasColumns ??= new ColumnarBatch(_types), output, pooled),
            };
        }
        catch
        {
            // A batch in the arena's memory gives it back only when it is disposed.
            rows.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The row a key's index entry names, which must be one row held once (design 64 §6 a): an entry
    /// of several rows, or a row held twice, faults the view.
    /// </summary>
    private int Checked(int held)
    {
        var slot = held >= 0 ? held : _several[~held][0];
        if (held < 0 || _counts[slot] > 1)
        {
            throw new MaintainedKeyException(Render(slot));
        }

        return slot;
    }

    private string Render(int slot)
    {
        var parts = new List<string>();
        foreach (var column in _key)
        {
            var stored = _rows.Column(column);
            parts.Add(!stored.IsValid(slot)
                ? "NULL"
                : stored.Variable
                    ? System.Text.Encoding.UTF8.GetString(stored.Bytes(slot))
                    : Convert.ToHexString(stored.Lane(slot)));
        }

        return string.Join(", ", parts);
    }

    /// <summary>
    /// Records the row a key held before this update, the first time the update touches it: -1 when it
    /// held none, or more than one, which its end reads alike.
    /// </summary>
    private void BeforeOf(ReadOnlySpan<byte> key)
    {
        ref var held = ref _before.GetOrAdd(key, out var added);
        if (!added)
        {
            return;
        }

        held = (_keySlots ?? _slots).TryGet(key, out var slot) && slot >= 0 ? slot : -1;
    }

    private void Touch(int slot)
    {
        _keyColumns.Encode(_keyEncoder, _rows, _stored, slot, normalise: false);
        BeforeOf(_keyEncoder.Span);
    }

    private static RecordBatch Arrow(ChangeWriter rows, ColumnarBatch columns, OutputMaterialiser output, bool pooled)
    {
        if (rows.Rows == 0)
        {
            columns.Begin(0);
            for (var c = 0; c < rows.Types.Count; c++)
            {
                columns.Set(c, ColumnView.Empty(rows.Types[c]));
            }

            return output.Materialise(columns, pooled);
        }

        // One batch: the writer was made with no row limit.
        var batch = rows.Batch(0);
        columns.Begin(batch.RowCount);
        for (var c = 0; c < rows.Types.Count; c++)
        {
            columns.Set(c, batch.Column(c));
        }

        return output.Materialise(columns, pooled);
    }
}

/// <summary>
/// A maintained result whose key holds two rows (design 64 §6 a): the key, rendered, and where the two
/// rows were — of the result, or before the statement's <c>LIMIT</c>, whose order they tie under (D360).
/// </summary>
internal sealed class MaintainedKeyException(string key, string where = "of the result")
    : InvalidOperationException($"the key ({key}) holds two rows {where}.")
{
    public string Key { get; } = key;

    public string Where { get; } = where;
}

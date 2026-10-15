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
    /// thing in it that cannot be (§8). <paramref name="clockSlots"/> are the parameters bound to the clock.
    /// </summary>
    public static MaintainedPlan Create(CompiledPlan compiled, IReadOnlyCollection<int> clockSlots)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        var compiler = new MaintainedCompiler(
            compiled.Plan, compiled.Catalog, compiled.Sources, compiled.Settings, clockSlots);
        var root = compiler.Compile();
        var plan = new MaintainedPlan(compiled, root, compiler.States, compiler.Diffs);

        // Build a tree once, so that a kernel or a type the operators cannot take is refused now.
        plan._tree = plan.Build();
        return plan;
    }

    /// <summary>Bytes the operators and the result hold, against the view's state budget.</summary>
    public long StateBytes => _states.Sum(s => s.Bytes) + _result.Bytes;

    /// <summary>Every state the plan's operators keep, for a test to look into.</summary>
    internal IReadOnlyList<IMaintainedState> States => _states;

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
        var tree = _tree ??= Build();
        var completed = false;
        arena.BeginExecution(stats);
        try
        {
            tree.Context.ReadSet ??= _compiled.NewReadSet();
            CompiledPlan.Pin(tree.Context.ReadSet, arena);
            tree.Context.Begin(stats, arena, bound, now, relations);
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

                var changes = _result.End(_restart || whole, _restart, tree.Output);
                if (_restart)
                {
                    // An update from nothing read every row of its tables, and the buffers its changes
                    // passed through grew to hold them. Later updates read what changed: the buffers are
                    // let go now rather than held for the life of the view.
                    foreach (var state in _states)
                    {
                        state.Trim();
                    }
                }

                _restart = false;
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

    /// <summary>Lets go of every table state the plan's reads hold.</summary>
    public ValueTask DisposeAsync()
    {
        foreach (var state in _states)
        {
            state.Discard();
        }

        _tree = null;
        return default;
    }

    private Tree Build()
    {
        var context = _compiled.NewContext();
        IBatchOperator root;
        OutputMaterialiser output;
        using (new ScratchScope(context))
        {
            root = _root(context);
            output = new OutputMaterialiser(context, _compiled.OutputSchema, _outputTypes, pooled: false);
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

    /// <summary>The rows keys hold now: those that changed, or every row when <see cref="Whole"/>.</summary>
    public required RecordBatch Now { get; init; }

    /// <summary>The rows the changed keys held before, of keys that hold no row now or hold another key's.</summary>
    public required RecordBatch Before { get; init; }
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
    private readonly Dictionary<byte[], List<int>> _byKey = new(ByteKeys.Instance);
    private readonly Dictionary<byte[], List<int>> _before = new(ByteKeys.Instance);
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
            _keyColumns = new KeyColumns(_types, value);
            _stored = value;
        }
    }

    public long Bytes => _rows.Bytes + (_counts.Length * sizeof(long)) + _slots.Bytes + (_byKey.Count * 64L);

    public void Restart()
    {
        _rows.Clear();
        _slots.Clear();
        _byKey.Clear();
        _before.Clear();
        _removed.Clear();
    }

    public void Begin()
    {
        _before.Clear();
        _removed.Clear();
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
                    var members = _byKey.GetAlternateLookup<ReadOnlySpan<byte>>();
                    members.TryGetValue(_keyEncoder.Span, out var list);
                    list!.Remove(slot);
                    if (list.Count == 0)
                    {
                        members.Remove(_keyEncoder.Span);
                    }

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
                Array.Resize(ref _counts, Math.Max(_rows.Slots, Math.Max(16, _counts.Length * 2)));
            }

            _counts[slot] = weight;
            var byKey = _byKey.GetAlternateLookup<ReadOnlySpan<byte>>();
            if (!byKey.TryGetValue(_keyEncoder.Span, out var held))
            {
                held = [];
                byKey[_keyEncoder.Span] = held;
            }

            held.Add(slot);
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
            foreach (var (_, slots) in _byKey)
            {
                Check(slots);
                now.Write(_rows, slots[0], 1);
            }
        }
        else
        {
            foreach (var (key, previous) in _before)
            {
                var current = _byKey.TryGetValue(key, out var slots) ? slots : null;
                if (current is not null)
                {
                    Check(current);
                }

                var after = current is { Count: 1 } ? current[0] : -1;
                var was = previous.Count == 1 ? previous[0] : -1;
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
        return new MaintainedChanges
        {
            Whole = whole,
            Restarted = restarted,
            Now = Arrow(now, _nowColumns ??= new ColumnarBatch(_types), output),
            Before = Arrow(before, _wasColumns ??= new ColumnarBatch(_types), output),
        };
    }

    /// <summary>One row per key, held once (design 64 §6 a).</summary>
    private void Check(List<int> slots)
    {
        if (slots.Count > 1 || (slots.Count == 1 && _counts[slots[0]] > 1))
        {
            throw new MaintainedKeyException(Render(slots[0]));
        }
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

    /// <summary>Records the rows a key held before this update, the first time the update touches it.</summary>
    private void BeforeOf(ReadOnlySpan<byte> key)
    {
        var before = _before.GetAlternateLookup<ReadOnlySpan<byte>>();
        if (before.ContainsKey(key))
        {
            return;
        }

        var held = _byKey.GetAlternateLookup<ReadOnlySpan<byte>>().TryGetValue(key, out var slots)
            ? new List<int>(slots)
            : [];
        before[key] = held;
    }

    private void Touch(int slot)
    {
        _keyColumns.Encode(_keyEncoder, _rows, _stored, slot, normalise: false);
        BeforeOf(_keyEncoder.Span);
    }

    private static RecordBatch Arrow(ChangeWriter rows, ColumnarBatch columns, OutputMaterialiser output)
    {
        if (rows.Rows == 0)
        {
            columns.Begin(0);
            for (var c = 0; c < rows.Types.Count; c++)
            {
                columns.Set(c, ColumnView.Empty(rows.Types[c]));
            }

            return output.Materialise(columns);
        }

        // One batch: the writer was made with no row limit.
        var batch = rows.Batch(0);
        columns.Begin(batch.RowCount);
        for (var c = 0; c < rows.Types.Count; c++)
        {
            columns.Set(c, batch.Column(c));
        }

        return output.Materialise(columns);
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

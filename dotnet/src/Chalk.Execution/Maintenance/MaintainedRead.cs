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

    /// <summary>A read keeps no buffer of its own: its changes are the source's batches.</summary>
    public void Trim()
    {
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
internal sealed class ReadChanges : OperatorBase, IKeepsScratch
{
    private readonly ReadState _state;
    private readonly bool _appendsOnly;
    private readonly ColumnarBatch _slot;
    private readonly int _weightColumn;
    private readonly Reading _run;
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
        _run = new Reading(this);
        context.Scratch.Add(this);
    }

    /// <summary>The weight columns, which a batch of every row grew to a batch's length.</summary>
    public void Trim()
    {
        _plus = [];
        _minus = [];
    }

    protected override ValueTask DisposeCoreAsync() => default;

    protected override IAsyncEnumerator<ColumnarBatch> Begin(CancellationToken ct) => _run.Begin(ct);

    /// <summary>
    /// The read of one update, kept from one update to the next (F244): the scans the table's change
    /// calls for, worked out at the first move, then run one after another, each batch given with its
    /// weight. Only a read that runs to its end holds the table's new state.
    /// </summary>
    private sealed class Reading(ReadChanges owner) : KeptIteration
    {
        /// <summary>The scans of this update: at most a diff's three.</summary>
        private readonly Step[] _steps = new Step[3];
        private int _count;
        private int _next;
        private bool _planned;
        private object? _now;
        private IColumnarScan? _scan;
        private long _weight;

        protected override void Started()
        {
            _count = 0;
            _next = 0;
            _planned = false;
            _now = null;
            _scan = null;
        }

        public override ValueTask<bool> MoveNextAsync()
        {
            if (!_planned)
            {
                Plan();
                _planned = true;
            }

            var slot = owner._slot;
            while (true)
            {
                if (_scan is null)
                {
                    if (_next >= _count)
                    {
                        Finish();
                        return new ValueTask<bool>(false);
                    }

                    Open(_steps[_next++]);
                    if (_scan is null)
                    {
                        continue;
                    }
                }

                Token.ThrowIfCancellationRequested();
                if (_scan.TryNext(slot))
                {
                    return new ValueTask<bool>(Weighed(slot));
                }

                var waiting = _scan.WaitAsync(slot, Token);
                if (!waiting.IsCompletedSuccessfully)
                {
                    return WaitedAsync(waiting);
                }

                if (waiting.Result)
                {
                    return new ValueTask<bool>(Weighed(slot));
                }

                var closing = Close();
                if (!closing.IsCompletedSuccessfully)
                {
                    return ClosedAsync(closing);
                }
            }
        }

        /// <summary>The scan could not answer at once: waited for, and the read carried on.</summary>
        private async ValueTask<bool> WaitedAsync(ValueTask<bool> waiting)
        {
            if (await waiting.ConfigureAwait(false))
            {
                return Weighed(owner._slot);
            }

            await Close().ConfigureAwait(false);
            return await MoveNextAsync().ConfigureAwait(false);
        }

        private async ValueTask<bool> ClosedAsync(ValueTask closing)
        {
            await closing.ConfigureAwait(false);
            return await MoveNextAsync().ConfigureAwait(false);
        }

        /// <summary>The scans the table's change calls for: what was appended, a diff's three, or the whole table.</summary>
        private void Plan()
        {
            var state = owner._state;
            var now = owner.Context.ReadSet?.Pinned(state.SourceId, state.Table)
                ?? throw new InvalidOperationException(
                    $"a maintained read of '{state.Table}' found no state pinned for it in this update.");
            _now = now;
            var held = state.Held;
            state.Rewritten = false;

            if (held is null)
            {
                state.Rewritten = owner._appendsOnly;
                if (!owner._appendsOnly)
                {
                    Add(new Step(now, 0, null, +1));
                }
            }
            else if (!ReferenceEquals(held, now))
            {
                var from = state.Source.AppendedFrom(state.Table, held, now);
                if (from >= 0)
                {
                    Add(new Step(now, from, null, +1));
                }
                else if (!state.WholeOnRewrite && state.Diffs.DiffFrom(state, held, now) is { } diff)
                {
                    // A replacement that kept the rest of the rows in their order, as a compaction does:
                    // the rows it took out, the rows a keyed refresh put in their places (D367), and the
                    // rows it appended.
                    Add(new Step(held, 0, diff.Removed, -1));
                    Add(new Step(now, 0, diff.Added, +1));
                    Add(new Step(now, diff.AppendedFrom, null, +1));
                }
                else
                {
                    state.Rewritten = true;
                    if (!owner._appendsOnly)
                    {
                        if (!state.NewRowsOnRewrite)
                        {
                            Add(new Step(held, 0, null, -1));
                        }

                        Add(new Step(now, 0, null, +1));
                    }
                }
            }
        }

        private void Add(Step step) => _steps[_count++] = step;

        /// <summary>Opens <paramref name="step"/>'s scan; none for positions there are none of.</summary>
        private void Open(Step step)
        {
            var state = owner._state;
            _weight = step.Weight;
            if (step.At is { } positions)
            {
                _scan = positions.Length == 0
                    ? null
                    : state.Source.ScanAt(state.Table, step.Pinned, positions, state.Request, ScanContextFor(owner.Context));
                return;
            }

            _scan = state.Source.ScanFrom(state.Table, step.Pinned, step.From, state.Request, ScanContextFor(owner.Context));
        }

        private bool Weighed(ColumnarBatch slot)
        {
            slot.Set(owner._weightColumn, owner.Weight(_weight, slot.RowCount));
            Current = slot;
            return true;
        }

        private ValueTask Close()
        {
            var scan = _scan!;
            _scan = null;
            return scan.DisposeAsync();
        }

        /// <summary>The read ran to its end: the table's new state is held, and the steps' states let go.</summary>
        private void Finish()
        {
            if (_now is { } now)
            {
                owner._state.Hold(now);
            }

            LetGo();
        }

        /// <summary>No state of the table is kept past the update, so nothing here holds an old one alive.</summary>
        private void LetGo()
        {
            System.Array.Clear(_steps);
            _count = 0;
            _now = null;
        }

        public override async ValueTask DisposeAsync()
        {
            if (_scan is not null)
            {
                await Close().ConfigureAwait(false);
            }

            LetGo();
        }

        /// <summary>One scan of the read: of a pinned state from a position, or at positions, with a weight.</summary>
        private readonly record struct Step(object Pinned, long From, int[]? At, long Weight);
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

    /// <summary>
    /// The rows the state holds, and the slots it has room for: what its stores reached at their fullest,
    /// which removals free for reuse but never give back. A state far emptier than its slots holds memory
    /// for rows it no longer has. Zero and zero for a state that keeps no rows of its own.
    /// </summary>
    (long Live, long Slots) Occupancy => default;

    /// <summary>Forgets everything: after an update that failed part way, the next starts from nothing (§7).</summary>
    void Discard();

    /// <summary>
    /// Lets go of the buffers an update's changes passed through, keeping all that is held between
    /// updates: after an update from nothing, whose changes were every row it read.
    /// </summary>
    void Trim();
}

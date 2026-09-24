using Chalk.Catalog;
using Chalk.Execution.Aggregation;
using Chalk.Execution.Numeric;
using Chalk.Execution.Vectors;
using Chalk.Sources;
using Array = System.Array;
using ArrowSchema = Apache.Arrow.Schema;

namespace Chalk.Execution.Operators;

/// <summary>
/// <c>UNION ALL</c> (D69, <c>15-zero-allocation-execution.md</c> §6): each input in turn, forwarded
/// <em>unchanged</em>.
/// </summary>
/// <remarks>
/// The first operator whose steady-state cost is exactly zero: with column views there is nothing to
/// copy, so a batch is adopted — its columns, its row count and its selection — and handed on. That
/// is why it is this step's own gate case (§5).
/// </remarks>
internal sealed class UnionAllOperator : OperatorBase
{
    private readonly IBatchOperator[] _inputs;
    private readonly ColumnarBatch _output;

    public UnionAllOperator(
        OperatorContext context,
        string path,
        ArrowSchema schema,
        IReadOnlyList<ChalkType> columnTypes,
        IBatchOperator[] inputs)
        : base(context, schema, columnTypes, path)
    {
        _inputs = inputs;
        _output = NewOutput();
    }

    protected override async ValueTask DisposeCoreAsync()
    {
        foreach (var input in _inputs)
        {
            await input.DisposeAsync().ConfigureAwait(false);
        }
    }

    protected override async IAsyncEnumerable<ColumnarBatch> RunAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var input in _inputs)
        {
            await foreach (var batch in input.ExecuteAsync(ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                _output.Adopt(batch);
                yield return _output;
            }
        }
    }
}

/// <summary>
/// The three hashing set operations (D69, §6): <c>UNION</c>, <c>INTERSECT [ALL]</c> and
/// <c>EXCEPT [ALL]</c>.
/// </summary>
/// <remarks>
/// One arena-backed hash table over whole rows, with NULLs equal to each other — a set operation
/// compares values, not predicates, so a NULL is a value like any other. The first input fills the
/// table with per-key counts; each further input is probed and the counts are reduced, to the
/// minimum for an intersection and by subtraction for a difference. A <c>UNION</c> never reduces:
/// every input adds keys, and each is emitted the first time it is seen.
/// <para>
/// Rows come out in the order their keys first arrived, which is the reference executor's order too.
/// Nothing about a set operation's result is ordered, and the plan says so — a <c>Sort</c> above one
/// is a real sort — so the corpus asserts what it needs by sorting.
/// </para>
/// </remarks>
internal sealed class HashSetOperator : OperatorBase
{
    private const int InitialSlots = 1024;

    /// <summary>The largest probe table there is: one more doubling would not fit in an <c>int</c>.</summary>
    private const int MaximumSlots = 1 << 30;

    private readonly IBatchOperator[] _inputs;
    private readonly Ir.SetOpKind _kind;
    private readonly ColumnCopier[] _store;
    private readonly ColumnCopier[] _emit;
    private readonly ColumnView[] _keys;
    private readonly ColumnKind[] _kinds;
    private readonly int[] _widths;
    private readonly byte[] _laneScratch = new byte[16];
    private readonly ColumnarBatch _output;
    private readonly ChalkType[] _columnTypes;

    /// <summary>
    /// The plan's estimate of this node's own output rows, which is what the store holds: a set
    /// operation keeps one entry per distinct row it has seen (D258.3).
    /// </summary>
    private readonly double _estimatedRows;

    private int[] _slots = [];
    private ulong[] _hashes = [];
    private int[] _counts = [];
    private int[] _probe = [];
    private int _rows;
    private int _slotCount;
    private int _mask;

    public HashSetOperator(
        OperatorContext context,
        string path,
        ArrowSchema schema,
        IReadOnlyList<ChalkType> columnTypes,
        IBatchOperator[] inputs,
        Ir.SetOpKind kind,
        double estimatedRows = 0)
        : base(context, schema, columnTypes, path)
    {
        // D297: every form but UNION ALL hashes and compares whole rows, so every column is a key.
        // A v1 LIST has no equality (D58) and neither has a composite (D291); UNION ALL, which
        // compares nothing, is not built on this operator and carries either.
        for (var c = 0; c < columnTypes.Count; c++)
        {
            ColumnKinds.RequireComparable(
                columnTypes[c], $"the column '{schema.GetFieldByIndex(c).Name}' of a row {Named(kind)} compares");
        }

        _inputs = inputs;
        _kind = kind;
        _columnTypes = [.. columnTypes];
        _estimatedRows = estimatedRows;
        _store = [.. columnTypes.Select(t => new ColumnCopier(t))];
        _emit = [.. columnTypes.Select(t => new ColumnCopier(t))];
        _keys = new ColumnView[columnTypes.Count];
        _kinds = [.. columnTypes.Select(ColumnKinds.Of)];
        _widths = [.. _kinds.Select(ColumnKinds.Width)];
        _output = NewOutput();
    }

    protected override async ValueTask DisposeCoreAsync()
    {
        foreach (var input in _inputs)
        {
            await input.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>The set operation as SQL spells it, for a refusal.</summary>
    private static string Named(Ir.SetOpKind kind) => kind switch
    {
        Ir.SetOpKind.UnionDistinct => "UNION",
        Ir.SetOpKind.IntersectAll => "INTERSECT ALL",
        Ir.SetOpKind.IntersectDistinct => "INTERSECT",
        Ir.SetOpKind.ExceptAll => "EXCEPT ALL",
        Ir.SetOpKind.ExceptDistinct => "EXCEPT",
        _ => kind.ToString(),
    };

    private bool IsUnion => _kind == Ir.SetOpKind.UnionDistinct;

    private bool IsIntersect =>
        _kind is Ir.SetOpKind.IntersectAll or Ir.SetOpKind.IntersectDistinct;

    private bool IsDistinct => _kind is Ir.SetOpKind.UnionDistinct
        or Ir.SetOpKind.IntersectDistinct or Ir.SetOpKind.ExceptDistinct;

    /// <summary>
    /// <c>EXCEPT DISTINCT</c> removes a key outright rather than one occurrence of it: SQL's
    /// difference of two <em>sets</em> keeps a left row only when the right holds none like it.
    /// </summary>
    private bool RemovesOutright => _kind == Ir.SetOpKind.ExceptDistinct;

    protected override async IAsyncEnumerable<ColumnarBatch> RunAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var arena = Context.Arena;
        try
        {
            Restart(arena);

            // The first input builds the table; every further one reduces it, except for a UNION,
            // where every input adds to it.
            await ConsumeAsync(_inputs[0], first: true, ct).ConfigureAwait(false);
            for (var i = 1; i < _inputs.Length; i++)
            {
                await ConsumeAsync(_inputs[i], first: false, ct).ConfigureAwait(false);
            }

            var batchSize = Context.Settings.BatchSize;
            var emitted = 0;
            for (var row = 0; row < _rows; row++)
            {
                ct.ThrowIfCancellationRequested();
                var copies = IsDistinct ? Math.Min(_counts[row], 1) : _counts[row];
                for (var n = 0; n < copies; n++)
                {
                    for (var c = 0; c < _emit.Length; c++)
                    {
                        if (emitted == 0)
                        {
                            _emit[c].Begin();
                        }

                        _emit[c].AppendRow(_keys[c], row);
                    }


                    if (++emitted == batchSize)
                    {
                        yield return Emit(emitted);
                        emitted = 0;
                    }
                }
            }

            if (emitted > 0)
            {
                yield return Emit(emitted);
            }
        }
        finally
        {
            arena.Return(_slots);
            arena.Return(_hashes);
            arena.Return(_counts);
            _slots = [];
            _hashes = [];
            _counts = [];
            _rows = 0;
            _slotCount = 0;
            _mask = 0;
        }
    }

    private ColumnarBatch Emit(int count)
    {
        _output.Begin(count);
        for (var c = 0; c < _emit.Length; c++)
        {
            _output.Set(c, _emit[c].FinishView());
        }

        return _output;
    }

    private void Restart(ExecutionArena arena)
    {
        // Every form but UNION ALL holds one entry per distinct row, and the plan's estimate of this
        // node's output is an estimate of how many that is (D258.3).
        var capacity = Operators.PlanCapacity.Rows(Context, _estimatedRows, _columnTypes);
        foreach (var copier in _store)
        {
            copier.Begin(capacity);
        }

        _rows = 0;
        _counts = arena.Rent<int>(Math.Max(capacity, 16));
        _hashes = arena.Rent<ulong>(Math.Max(capacity, 16));
        Resize(SlotsFor(capacity));
        RefreshStore();
    }

    /// <summary>
    /// The probe table's first size for <paramref name="rows"/> distinct rows, under the 0.7 load
    /// factor <c>Insert</c> keeps, and never fewer than <see cref="InitialSlots"/>.
    /// </summary>
    private static int SlotsFor(int rows)
    {
        if (rows <= 0)
        {
            return InitialSlots;
        }

        var wanted = ((long)rows * 10 / 7) + 1;
        var slots = InitialSlots;
        while (slots < wanted && slots < MaximumSlots)
        {
            slots <<= 1;
        }

        return slots;
    }

    /// <summary>
    /// Folds one input into the table. The first input inserts; a further one inserts too for a
    /// <c>UNION</c>, and otherwise only probes — an intersection cannot gain a key it did not have,
    /// and neither can a difference.
    /// </summary>
    private async ValueTask ConsumeAsync(IBatchOperator input, bool first, CancellationToken ct)
    {
        var inserts = first || IsUnion;
        var arena = Context.Arena;
        var counting = !first && IsIntersect;
        var touched = System.Array.Empty<byte>();
        try
        {
            if (counting)
            {
                // An intersection keeps min(mine, theirs), so it needs this input's own counts
                // before it can reduce; they go into `_probe`, against the stored rows.
                touched = arena.Rent<byte>(Math.Max(_rows, 1));
                _probe = arena.Rent<int>(Math.Max(_rows, 1));
                Array.Clear(touched, 0, Math.Max(_rows, 1));
                Array.Clear(_probe, 0, Math.Max(_rows, 1));
            }

            await foreach (var batch in input.ExecuteAsync(ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                var rows = batch.Count;
                for (var i = 0; i < rows; i++)
                {
                    var row = batch.RowAt(i);
                    var hash = Hash(batch, row);
                    var found = Probe(batch, row, hash, inserts);
                    if (found < 0)
                    {
                        continue;
                    }

                    if (first || IsUnion)
                    {
                        _counts[found]++;
                    }
                    else if (counting)
                    {
                        _probe[found]++;
                        touched[found] = 1;
                    }
                    else if (RemovesOutright)
                    {
                        _counts[found] = 0;
                    }
                    else
                    {
                        _counts[found]--;
                    }
                }
            }

            if (counting)
            {
                for (var row = 0; row < _rows; row++)
                {
                    _counts[row] = touched[row] == 0 ? 0 : Math.Min(_counts[row], _probe[row]);
                }
            }
        }
        finally
        {
            arena.Return(touched);
            if (counting)
            {
                arena.Return(_probe);
                _probe = [];
            }
        }
    }

    /// <summary>Points the key views back at the operator's own store after a batch is done with.</summary>
    private void RefreshStore()
    {
        for (var c = 0; c < _store.Length; c++)
        {
            _keys[c] = _store[c].FinishView();
        }
    }

    private ulong Hash(ColumnarBatch batch, int row)
    {
        Span<byte> scratch = _laneScratch;
        ulong hash = 0;
        for (var c = 0; c < _keys.Length; c++)
        {
            var view = batch.Column(c);
            var valid = view.IsValid(row);
            var lane = valid ? Lane(view, row, c, scratch) : default;
            var value = GroupKeyStore.Hash(lane, valid);
            hash = c == 0 ? Hashing.Mix(value) : Hashing.Combine(hash, value);
        }

        return hash;
    }

    /// <summary>
    /// The stored row this one equals, or -1 when it is not there and <paramref name="insert"/> is
    /// false. Insertion appends the row to the store, which is where a key lives after its batch has
    /// gone.
    /// </summary>
    private int Probe(ColumnarBatch batch, int row, ulong hash, bool insert)
    {
        var slot = (int)(hash & (ulong)_mask);
        while (true)
        {
            var stored = _slots[slot];
            if (stored < 0)
            {
                if (!insert)
                {
                    return -1;
                }

                return Insert(batch, row, hash, slot);
            }

            if (_hashes[stored] == hash && Matches(batch, row, stored))
            {
                return stored;
            }

            slot = (slot + 1) & _mask;
        }
    }

    private bool Matches(ColumnarBatch batch, int row, int stored)
    {
        Span<byte> mine = _laneScratch;
        Span<byte> theirs = stackalloc byte[16];
        for (var c = 0; c < _keys.Length; c++)
        {
            var view = batch.Column(c);
            var storedView = _keys[c];
            var leftValid = view.IsValid(row);
            var rightValid = storedView.IsValid(stored);
            if (leftValid != rightValid)
            {
                return false;
            }

            if (!leftValid)
            {
                continue;
            }

            if (!Lane(view, row, c, mine).SequenceEqual(Lane(storedView, stored, c, theirs)))
            {
                return false;
            }
        }

        return true;
    }

    private ReadOnlySpan<byte> Lane(in ColumnView view, int row, int column, Span<byte> scratch)
    {
        switch (_kinds[column])
        {
            case ColumnKind.Utf8:
            case ColumnKind.Binary:
                return view.VarValue(row);

            case ColumnKind.Boolean:
                scratch[0] = (byte)(view.BoolAt(row) ? 1 : 0);
                return scratch[..1];

            case ColumnKind.Float:
            case ColumnKind.Double:
            {
                var width = _widths[column];
                view.RawLanes(width).Slice(row * width, width).CopyTo(scratch);
                return Normalise(_kinds[column], scratch[..width]);
            }

            default:
            {
                var width = _widths[column];
                return view.RawLanes(width).Slice(row * width, width);
            }
        }
    }

    /// <summary>Every NaN is one value and -0.0 is 0.0, exactly as the hash aggregate reads them.</summary>
    private static ReadOnlySpan<byte> Normalise(ColumnKind kind, Span<byte> lane)
    {
        if (kind == ColumnKind.Float)
        {
            var value = System.Runtime.InteropServices.MemoryMarshal.Read<float>(lane);
            if (float.IsNaN(value))
            {
                System.Runtime.InteropServices.MemoryMarshal.Write(lane, float.NaN);
            }
            else if (value == 0f)
            {
                System.Runtime.InteropServices.MemoryMarshal.Write(lane, 0f);
            }
        }
        else
        {
            var value = System.Runtime.InteropServices.MemoryMarshal.Read<double>(lane);
            if (double.IsNaN(value))
            {
                System.Runtime.InteropServices.MemoryMarshal.Write(lane, double.NaN);
            }
            else if (value == 0d)
            {
                System.Runtime.InteropServices.MemoryMarshal.Write(lane, 0d);
            }
        }

        return lane;
    }

    private int Insert(ColumnarBatch batch, int row, ulong hash, int slot)
    {
        var stored = _rows++;
        EnsureCapacity(_rows);
        for (var c = 0; c < _store.Length; c++)
        {
            _store[c].AppendRow(batch.Column(c), row);
        }

        _hashes[stored] = hash;
        _counts[stored] = 0;
        _slots[slot] = stored;
        RefreshStore();

        // Load factor 0.7, as the hash aggregate uses (§6.6).
        if (_rows * 10 >= _slotCount * 7)
        {
            Resize(_slotCount * 2);
        }

        return stored;
    }

    private void EnsureCapacity(int rows)
    {
        if (_hashes.Length >= rows)
        {
            return;
        }

        var arena = Context.Arena;
        var size = Math.Max(rows, _hashes.Length * 2);
        var hashes = arena.Rent<ulong>(size);
        var counts = arena.Rent<int>(size);
        Array.Copy(_hashes, hashes, _hashes.Length);
        Array.Copy(_counts, counts, _counts.Length);
        arena.Return(_hashes);
        arena.Return(_counts);
        _hashes = hashes;
        _counts = counts;
    }

    private void Resize(int size)
    {
        var slots = Context.Arena.Rent<int>(size);
        Context.Arena.Return(_slots);
        _slots = slots;
        _slotCount = size;
        _mask = size - 1;
        Array.Fill(_slots, -1, 0, size);
        for (var row = 0; row < _rows; row++)
        {
            var slot = (int)(_hashes[row] & (ulong)_mask);
            while (_slots[slot] >= 0)
            {
                slot = (slot + 1) & _mask;
            }

            _slots[slot] = row;
        }
    }
}

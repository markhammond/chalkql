using Chalk.Catalog;
using Chalk.Execution.Expressions;
using Chalk.Execution.Numeric;
using Chalk.Execution.Operators;
using Chalk.Execution.Vectors;
using Chalk.Sources;
using Array = System.Array;
using ArrowSchema = Apache.Arrow.Schema;

namespace Chalk.Execution.Aggregation;

/// <summary>What one measure needs at run time: its argument, its filter, and its state.</summary>
internal sealed class MeasurePlan
{
    public required MeasureAccumulator Accumulator { get; init; }

    /// <summary>Null for <c>COUNT(*)</c>.</summary>
    public IVectorExpr? Argument { get; init; }

    /// <summary>The measure's <c>FILTER (WHERE …)</c>, evaluated to a mask before the fold (§6.6).</summary>
    public IVectorExpr? Filter { get; init; }

    public bool Distinct { get; init; }

    public ColumnKind ArgumentKind { get; init; }

    public int ArgumentWidth { get; init; }

    /// <summary>
    /// The {@code WITHIN GROUP (ORDER BY …)} key of a holistic aggregate (D57), and null for every
    /// other measure. One key: the executor refuses more, because none of the aggregates the IR
    /// carries needs a compound ordering and a wrong one is a silently wrong answer.
    /// </summary>
    public IVectorExpr? OrderKey { get; init; }

    public ColumnKind OrderKeyKind { get; init; }

    public int OrderKeyWidth { get; init; }

    /// <summary>One set per group, created lazily; only a <c>distinct</c> measure has any.</summary>
    public List<ByteSet?> Seen { get; } = [];
}

/// <summary>
/// The hash aggregate of §6.6, which also serves a logical <c>Aggregate</c> because
/// <c>02-ir.md</c> §4 says logical nodes are always executable. Groups come out in first-seen order.
/// </summary>
internal sealed class HashAggregateOperator : OperatorBase
{
    private const int InitialSlots = 64;

    /// <summary>The largest probe table there is: one more doubling would not fit in an <c>int</c>.</summary>
    private const int MaximumSlots = 1 << 30;

    private readonly IBatchOperator _input;
    private readonly IVectorExpr[] _keys;
    private readonly GroupKeyStore[] _keyStores;
    private readonly ColumnKind[] _keyKinds;
    private readonly int[] _keyWidths;
    private readonly MeasurePlan[] _measures;
    private readonly ColumnCopier[] _output;
    private readonly ColumnarBatch _slot;
    private readonly EvalContext _eval;

    private int[] _slots = [];
    private ulong[] _groupHashes = [];
    private int _slotCount;
    private int _mask;
    private int _groupCount;
    private ulong[] _rowHashes = [];
    private int[] _rowGroups = [];
    private byte[] _rowMask = [];
    private readonly byte[] _laneScratch = new byte[16];
    private readonly Vector[] _keyVectors;
    private readonly Vector[] _arguments;
    private readonly Vector[] _filters;
    private readonly Vector[] _orderKeys;
    private readonly byte[] _keyScratch = new byte[16];
    private readonly ChalkType[] _outputTypes;

    /// <summary>
    /// The plan's estimate of this node's own output rows, which for an aggregate is a count of
    /// groups — the thing all of its per-group state scales with (D258.3).
    /// </summary>
    private readonly double _estimatedGroups;

    /// <summary>
    /// True when the one grouping key has a layout the image path of D255 covers, so a row's key is
    /// two words and a length rather than a lane read, a byte-at-a-time hash and a
    /// <c>SequenceEqual</c>. BOOL is out because a bit-packed column is not a lane, and LIST has no
    /// equality at all.
    /// </summary>
    private readonly bool _singleKey;

    /// <summary>Each group's key as <see cref="KeyImage"/> words, parallel to the dense group ids.</summary>
    private ulong[] _groupLow = [];
    private ulong[] _groupHigh = [];
    private int[] _groupLengths = [];

    /// <summary>
    /// The group the row before this one landed in, or -1 at the start of a run (D255). Carried
    /// across batches, because a run of one key does not stop at a batch boundary.
    /// </summary>
    private int _previousGroup = -1;

    /// <summary>True when the one key is STRING or BINARY, which changes what the selector is.</summary>
    private readonly bool _variableKey;

    /// <summary>
    /// The exact distinct count the source declared for the grouping key, or -1 (D255). A source that
    /// counts exactly — a POCO table whose index covers the column — makes this the size of the key
    /// set; anything else is an estimate, and this is read as a hint either way, because a key the
    /// set turns out not to contain is a miss that the general table answers correctly.
    /// </summary>
    private readonly long _declaredDistinct;

    /// <summary>The perfect table: a group id per slot, or empty when there is not one.</summary>
    private int[] _perfectSlots = [];
    private int _perfectSlotCount;
    private PerfectKeyFunction _perfect;

    /// <summary>Groups whose key is not NULL, which is what the declared count counts.</summary>
    private int _perfectKeys;

    /// <summary>Whether the build has already been tried, or given up on, for this run.</summary>
    private bool _perfectSettled;

    public HashAggregateOperator(
        OperatorContext context,
        string path,
        ArrowSchema schema,
        IReadOnlyList<ChalkType> outputTypes,
        IBatchOperator input,
        IVectorExpr[] keys,
        MeasurePlan[] measures,
        double estimatedGroups = 0,
        long declaredDistinct = -1)
        : base(context, schema, outputTypes, path)
    {
        _input = input;
        _keys = keys;
        _measures = measures;
        _outputTypes = [.. outputTypes];
        _estimatedGroups = estimatedGroups;
        _eval = context.NewEvalContext();
        _keyKinds = [.. keys.Select(k => ColumnKinds.Of(k.Type))];
        _keyWidths = [.. _keyKinds.Select(ColumnKinds.Width)];
        _keyStores = [.. _keyKinds.Select(GroupKeyStore.Create)];
        _output = [.. outputTypes.Select(t => new ColumnCopier(t))];
        _slot = NewOutput();
        _keyVectors = new Vector[keys.Length];
        _arguments = new Vector[measures.Length];
        _filters = new Vector[measures.Length];
        _orderKeys = new Vector[measures.Length];
        _singleKey = keys.Length == 1
            && _keyKinds[0] is not (ColumnKind.Boolean or ColumnKind.List)
            && (ColumnKinds.IsVariableLength(_keyKinds[0]) || _keyWidths[0] is 1 or 2 or 4 or 8 or 16);
        _variableKey = _singleKey && ColumnKinds.IsVariableLength(_keyKinds[0]);
        _declaredDistinct = _singleKey && declaredDistinct is >= 1 and <= PerfectKeys.MaxDistinct
            ? declaredDistinct
            : -1;
    }

    protected override ValueTask DisposeCoreAsync() => _input.DisposeAsync();

    protected override async IAsyncEnumerable<ColumnarBatch> RunAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        try
        {
            Restart();
            await foreach (var batch in _input.ExecuteAsync(ct))
            {
                Consume(batch);
            }

            var batchSize = Context.Settings.BatchSize;
            for (var start = 0; start < _groupCount; start += batchSize)
            {
                ct.ThrowIfCancellationRequested();
                var count = Math.Min(batchSize, _groupCount - start);
                _slot.Begin(count);
                for (var c = 0; c < _output.Length; c++)
                {
                    _output[c].Begin();
                    for (var g = start; g < start + count; g++)
                    {
                        if (c < _keyStores.Length)
                        {
                            _keyStores[c].Emit(_output[c], g);
                        }
                        else
                        {
                            _measures[c - _keyStores.Length].Accumulator.Emit(_output[c], g);
                        }
                    }

                    _slot.Set(c, _output[c].FinishView());
                }

                yield return _slot;
            }
        }
        finally
        {
            ReleaseScratch();
        }
    }

    /// <summary>
    /// Starts this execution's hash table. The probe table, the per-group hashes and the per-row
    /// working arrays are rented from the arena here and returned by <see cref="ReleaseScratch"/>, so
    /// nothing that scales with the data outlives the run (ADR 0012).
    /// </summary>
    private void Restart()
    {
        var arena = Context.Arena;
        foreach (var measure in _measures)
        {
            measure.Accumulator.Begin(arena);
            measure.Seen.Clear();
        }

        foreach (var store in _keyStores)
        {
            store.Begin(arena);
        }

        _groupCount = 0;
        _previousGroup = -1;
        _perfectKeys = 0;
        _perfectSettled = _declaredDistinct < 0;

        // The plan's estimate of this node's output is an estimate of the group count, so the probe
        // table and every per-group store start sized for it rather than doubling their way there
        // (D258.3). An estimate that is wrong is a wrong first size: a group past it grows the state
        // exactly as it did before.
        var groups = PlanCapacity.Rows(Context, _estimatedGroups, _outputTypes);
        Resize(SlotsFor(groups));
        if (groups > 0)
        {
            EnsureGroupCapacity(groups);
            foreach (var store in _keyStores)
            {
                store.Reserve(groups);
            }
        }

        // With no grouping keys the output is exactly one row even for empty input (§4), so the single
        // group exists before a single row has been read.
        if (_keys.Length == 0)
        {
            EnsureGroupCapacity(1);
            _groupCount = 1;
        }
    }

    /// <summary>
    /// The probe table's first size for <paramref name="groups"/> groups: the smallest power of two
    /// that holds them under the 0.7 load factor <c>Insert</c> keeps, and never fewer than
    /// <see cref="InitialSlots"/>.
    /// </summary>
    private static int SlotsFor(int groups)
    {
        if (groups <= 0)
        {
            return InitialSlots;
        }

        var wanted = ((long)groups * 10 / 7) + 1;
        var slots = InitialSlots;
        while (slots < wanted && slots < MaximumSlots)
        {
            slots <<= 1;
        }

        return slots;
    }

    /// <summary>Hands every rented array back, in the run's <c>finally</c>.</summary>
    private void ReleaseScratch()
    {
        var arena = Context.Arena;
        arena.Return(_slots);
        arena.Return(_groupHashes);
        arena.Return(_rowHashes);
        arena.Return(_rowGroups);
        arena.Return(_rowMask);
        arena.Return(_groupLow);
        arena.Return(_groupHigh);
        arena.Return(_groupLengths);
        arena.Return(_perfectSlots);
        _perfectSlots = [];
        _perfectSlotCount = 0;
        _perfectKeys = 0;
        _perfectSettled = false;
        _slots = [];
        _groupHashes = [];
        _rowHashes = [];
        _rowGroups = [];
        _rowMask = [];
        _groupLow = [];
        _groupHigh = [];
        _groupLengths = [];
        _slotCount = 0;
        _mask = 0;
        _previousGroup = -1;

        foreach (var store in _keyStores)
        {
            store.Release();
        }

        foreach (var measure in _measures)
        {
            measure.Accumulator.Release();
            measure.Seen.Clear();
        }
    }

    private void Consume(ColumnarBatch batch)
    {
        var length = batch.RowCount;
        if (batch.Count == 0)
        {
            return;
        }

        _eval.SetBatch(batch, length);

        // Evaluating every key, argument and filter up front is what makes this vectorised: one pass
        // per expression over the batch, then one pass over the rows to probe and fold.
        for (var k = 0; k < _keys.Length; k++)
        {
            _keyVectors[k] = _keys[k].Evaluate(_eval);
        }

        for (var m = 0; m < _measures.Length; m++)
        {
            if (_measures[m].Argument is { } argument)
            {
                _arguments[m] = argument.Evaluate(_eval);
            }

            if (_measures[m].Filter is { } filter)
            {
                _filters[m] = filter.Evaluate(_eval);
            }

            if (_measures[m].OrderKey is { } orderKey)
            {
                _orderKeys[m] = orderKey.Evaluate(_eval);
            }
        }

        // The kernels ran over every lane; folding visits only the selected rows, which is where a
        // selection vector stops (§2).
        var groups = ResolveGroups(_keyVectors, batch);
        var selected = batch.Count;
        for (var m = 0; m < _measures.Length; m++)
        {
            var plan = _measures[m];

            // A DISTINCT measure keeps a set per group and an ordered one reads a second column per
            // row; neither is a scatter, so both stay on the generic fold (D255).
            if (!plan.Distinct && plan.OrderKey is null
                && FoldBatch(plan, groups, selected, _arguments[m], _filters[m], batch))
            {
                continue;
            }

            Fold(plan, groups, _arguments[m], _filters[m], _orderKeys[m], batch);
        }
    }

    /// <summary>
    /// Offers the batch to this measure's accumulate kernel, with its <c>FILTER</c> resolved to a
    /// mask first (D255). False means there is no kernel for this argument's layout and the caller
    /// folds row by row instead.
    /// </summary>
    private bool FoldBatch(
        MeasurePlan plan,
        int[] groups,
        int selected,
        in Vector argument,
        in Vector filter,
        ColumnarBatch batch)
    {
        var hasArgument = plan.Argument is not null;
        var reads = hasArgument && !argument.IsScalar;
        var grouped = new GroupedBatch(
            groups.AsSpan(0, selected),
            batch.Selection,
            plan.Filter is null ? default : FilterMask(filter, batch, selected),
            reads ? argument.View.ValidityBits() : default,
            reads ? argument.View.Offset : 0);

        return plan.Accumulator.TryAddBatch(grouped, argument, hasArgument);
    }

    /// <summary>
    /// A measure's <c>FILTER (WHERE …)</c> as one byte per selected row, computed before the fold so
    /// that the kernel's loop carries no expression evaluation (§6.6, D255).
    /// </summary>
    private ReadOnlySpan<byte> FilterMask(in Vector filter, ColumnarBatch batch, int selected)
    {
        var lanes = Lanes<byte>.From(filter);
        var selection = batch.Selection;
        var mask = _rowMask;
        for (var i = 0; i < selected; i++)
        {
            var row = selection.Length == 0 ? i : selection[i];
            mask[i] = (byte)(lanes.IsValid(row) && lanes[row] != 0 ? 1 : 0);
        }

        return mask.AsSpan(0, selected);
    }

    /// <summary>Maps each row of the batch to its dense group id, creating groups as they first appear.</summary>
    private int[] ResolveGroups(Vector[] keyVectors, ColumnarBatch batch)
    {
        var length = batch.Count;
        EnsureRowCapacity(length);

        if (_keys.Length == 0)
        {
            Array.Clear(_rowGroups, 0, length);
            return _rowGroups;
        }

        // One key of a layout the image path covers is the case worth specialising, and it is the
        // shape nearly every GROUP BY has (D255). Everything else goes down the two-pass path below.
        if (_singleKey && !keyVectors[0].IsScalar)
        {
            if (ColumnKinds.IsVariableLength(_keyKinds[0]))
            {
                ResolveVariableKey(keyVectors[0].View, batch, length);
            }
            else
            {
                ResolveFixedKey(keyVectors[0], batch, length);
            }

            return _rowGroups;
        }

        for (var i = 0; i < length; i++)
        {
            _rowHashes[i] = 0;
        }

        Span<byte> scratch = _laneScratch;
        for (var k = 0; k < _keys.Length; k++)
        {
            for (var i = 0; i < length; i++)
            {
                var row = batch.RowAt(i);
                var valid = LaneAccess.IsValid(keyVectors[k], row);
                var lane = LaneAccess.Read(keyVectors[k], row, _keyKinds[k], _keyWidths[k], scratch);
                // The first key's hash is the image's own — already a finalised mix — so that a run
                // that takes both paths for one key (a scalar key vector in one batch, a column in
                // the next) records the same hash for a group either way.
                var hash = Image(lane, valid, _keyKinds[k], _keyWidths[k]).Hash();
                _rowHashes[i] = k == 0 ? hash : Hashing.Combine(_rowHashes[i], hash);
            }
        }

        for (var i = 0; i < length; i++)
        {
            _rowGroups[i] = Probe(keyVectors, batch.RowAt(i), _rowHashes[i]);
        }

        return _rowGroups;
    }

    /// <summary>The image of one lane, whichever layout it has.</summary>
    private static KeyImage Image(ReadOnlySpan<byte> lane, bool valid, ColumnKind kind, int width) =>
        !valid ? KeyImage.Null
        : ColumnKinds.IsVariableLength(kind) ? KeyImage.OfBytes(lane)
        : KeyImage.OfFixed(lane, width);

    /// <summary>Rents this batch's per-row working arrays, which scale with the batch and not the data.</summary>
    private void EnsureRowCapacity(int length)
    {
        if (_rowGroups.Length >= length && _rowHashes.Length >= length && _rowMask.Length >= length)
        {
            return;
        }

        var arena = Context.Arena;
        var hashes = arena.Rent<ulong>(length);
        var groups = arena.Rent<int>(length);
        var mask = arena.Rent<byte>(length);
        arena.Return(_rowHashes);
        arena.Return(_rowGroups);
        arena.Return(_rowMask);
        _rowHashes = hashes;
        _rowGroups = groups;
        _rowMask = mask;
    }

    /// <summary>
    /// The single-key path over a fixed-width column: the lane is one or two words, the previous
    /// row's group is tried first, and the probe compares images rather than reading the lane again
    /// (D255).
    /// </summary>
    private void ResolveFixedKey(in Vector keyVector, ColumnarBatch batch, int length)
    {
        var kind = _keyKinds[0];
        var width = _keyWidths[0];
        var lanes = RawLanes.From(keyVector, width);
        var selection = batch.Selection;
        var groups = _rowGroups;
        var real = kind is ColumnKind.Float or ColumnKind.Double;
        Span<byte> scratch = _laneScratch;
        var previous = _previousGroup;

        for (var i = 0; i < length; i++)
        {
            var row = selection.Length == 0 ? i : selection[i];
            scoped ReadOnlySpan<byte> lane;
            KeyImage image;
            if (lanes.IsValid(row))
            {
                lane = lanes[row];
                if (real)
                {
                    // Grouping compares reals by value: one NaN, one zero (LaneAccess.Normalise).
                    lane.CopyTo(scratch);
                    lane = LaneAccess.Normalise(kind, scratch[..width]);
                }

                image = KeyImage.OfFixed(lane, width);
            }
            else
            {
                lane = default;
                image = KeyImage.Null;
            }

            previous = Lookup(previous, image, lane);
            groups[i] = previous;
        }

        _previousGroup = previous;
    }

    /// <summary>The same for a STRING or BINARY column, whose image is its first sixteen bytes.</summary>
    private void ResolveVariableKey(in ColumnView view, ColumnarBatch batch, int length)
    {
        var lanes = VarLanes.FromView(view);
        var selection = batch.Selection;
        var groups = _rowGroups;
        var previous = _previousGroup;

        for (var i = 0; i < length; i++)
        {
            var row = selection.Length == 0 ? i : selection[i];
            scoped ReadOnlySpan<byte> lane;
            KeyImage image;
            if (lanes.IsValid(row))
            {
                lane = lanes[row];
                image = KeyImage.OfBytes(lane);
            }
            else
            {
                lane = default;
                image = KeyImage.Null;
            }

            previous = Lookup(previous, image, lane);
            groups[i] = previous;
        }

        _previousGroup = previous;
    }

    /// <summary>
    /// One row's group on the single-key path: the previous row's group, then the probe table,
    /// creating the group if it is new.
    /// </summary>
    private int Lookup(int previous, in KeyImage image, ReadOnlySpan<byte> lane)
    {
        if (previous >= 0 && SameGroup(previous, image, lane))
        {
            return previous;
        }

        // The perfect table, when the key set was declared and the function was found: a selector
        // built from one or two loads, a multiply, an index, and the compare that validates the slot
        // (D255). A key the declared set did not contain misses here and the general table answers,
        // so a stale statistic costs one wasted lookup and never an answer.
        if (_perfectSlotCount != 0 && !image.IsNull)
        {
            var selector = _variableKey
                ? PerfectKeys.Selector(lane, _perfect.Position1, _perfect.Position2)
                : PerfectKeys.Selector(image.Low, image.High);
            var candidate = _perfectSlots[PerfectKeys.Slot(selector, _perfect.Seed, _perfect.Shift)];
            if (candidate >= 0 && SameGroup(candidate, image, lane))
            {
                return candidate;
            }
        }

        var hash = image.Hash();
        var slot = (int)(hash & (ulong)_mask);
        while (true)
        {
            var group = _slots[slot];
            if (group < 0)
            {
                return InsertSingle(image, lane, hash, slot);
            }

            if (_groupHashes[group] == hash && SameGroup(group, image, lane))
            {
                return group;
            }

            slot = (slot + 1) & _mask;
        }
    }

    /// <summary>
    /// Whether a group holds this key. The length is compared first, so a NULL — whose length is a
    /// value no key can have — equals only another NULL, and a key too long for its image can only
    /// be compared with another of the same length, which is where the key store's full compare is
    /// the answer.
    /// </summary>
    private bool SameGroup(int group, in KeyImage image, ReadOnlySpan<byte> lane)
    {
        var length = _groupLengths[group];
        if (length != image.Length)
        {
            return false;
        }

        return length <= KeyImage.MaxInline
            ? _groupLow[group] == image.Low && _groupHigh[group] == image.High
            : _keyStores[0].Matches(group, lane, valid: true);
    }

    /// <summary>Creates the group this key starts, on the single-key path.</summary>
    private int InsertSingle(in KeyImage image, ReadOnlySpan<byte> lane, ulong hash, int slot)
    {
        var group = _groupCount++;
        EnsureGroupCapacity(_groupCount);
        _keyStores[0].Append(lane, valid: !image.IsNull);
        _groupHashes[group] = hash;
        _groupLow[group] = image.Low;
        _groupHigh[group] = image.High;
        _groupLengths[group] = image.Length;
        _slots[slot] = group;

        if (_groupCount * 10 >= _slotCount * 7)
        {
            Resize(_slotCount * 2);
        }

        if (!image.IsNull)
        {
            _perfectKeys++;
            if (_perfectSlotCount != 0)
            {
                // A key the perfect table does not contain: the declared count was not the size of
                // the key set, so the table no longer answers for every group and is retired rather
                // than consulted once per row for nothing.
                RetirePerfect();
            }
            else if (!_perfectSettled && _perfectKeys == _declaredDistinct)
            {
                BuildPerfect();
            }
        }

        return group;
    }

    /// <summary>
    /// Builds the perfect hash over the declared key set, once per run and inside its own budget
    /// (D255). A search that does not find a function inside it leaves the aggregate on its general
    /// table, which is the same answer at the same speed as before.
    /// </summary>
    private void BuildPerfect()
    {
        _perfectSettled = true;
        var size = 2;
        while (size < _perfectKeys * 2)
        {
            size <<= 1;
        }

        var slots = Context.Arena.Rent<int>(size);
        if (PerfectKeys.TryBuild(
            _groupLow,
            _groupHigh,
            _groupLengths,
            _groupCount,
            _keyStores[0],
            _variableKey,
            slots,
            size,
            out var function))
        {
            _perfectSlots = slots;
            _perfectSlotCount = size;
            _perfect = function;
            return;
        }

        Context.Arena.Return(slots);
    }

    /// <summary>Hands the perfect table back and stops consulting it, for the rest of the run.</summary>
    private void RetirePerfect()
    {
        Context.Arena.Return(_perfectSlots);
        _perfectSlots = [];
        _perfectSlotCount = 0;
    }

    private int Probe(Vector[] keyVectors, int row, ulong hash)
    {
        var slot = (int)(hash & (ulong)_mask);
        while (true)
        {
            var group = _slots[slot];
            if (group < 0)
            {
                return Insert(keyVectors, row, hash, slot);
            }

            if (_groupHashes[group] == hash && Matches(keyVectors, row, group))
            {
                return group;
            }

            slot = (slot + 1) & _mask;
        }
    }

    private bool Matches(Vector[] keyVectors, int row, int group)
    {
        Span<byte> scratch = _laneScratch;
        for (var k = 0; k < _keys.Length; k++)
        {
            var valid = LaneAccess.IsValid(keyVectors[k], row);
            var lane = LaneAccess.Read(keyVectors[k], row, _keyKinds[k], _keyWidths[k], scratch);
            if (!_keyStores[k].Matches(group, lane, valid))
            {
                return false;
            }
        }

        return true;
    }

    private int Insert(Vector[] keyVectors, int row, ulong hash, int slot)
    {
        var group = _groupCount++;
        EnsureGroupCapacity(_groupCount);
        Span<byte> scratch = _laneScratch;
        for (var k = 0; k < _keys.Length; k++)
        {
            var valid = LaneAccess.IsValid(keyVectors[k], row);
            var lane = LaneAccess.Read(keyVectors[k], row, _keyKinds[k], _keyWidths[k], scratch);
            _keyStores[k].Append(lane, valid);

            // A single-key run may come through here — a scalar key vector has no lanes — and the
            // image arrays have to hold for every group however it was created.
            if (_singleKey)
            {
                var image = Image(lane, valid, _keyKinds[k], _keyWidths[k]);
                _groupLow[group] = image.Low;
                _groupHigh[group] = image.High;
                _groupLengths[group] = image.Length;
                if (valid)
                {
                    _perfectKeys++;
                    if (_perfectSlotCount != 0)
                    {
                        RetirePerfect();
                    }
                }
            }
        }

        _groupHashes[group] = hash;
        _slots[slot] = group;

        // Load factor 0.7 (§6.6); growing by doubling keeps the dense group ids untouched.
        if (_groupCount * 10 >= _slotCount * 7)
        {
            Resize(_slotCount * 2);
        }

        return group;
    }

    private void Fold(
        MeasurePlan plan,
        int[] groups,
        in Vector argument,
        in Vector filter,
        in Vector orderKey,
        ColumnarBatch batch)
    {
        var length = batch.Count;
        var hasArgument = plan.Argument is not null;
        var hasFilter = plan.Filter is not null;
        var hasOrderKey = plan.OrderKey is not null;
        var filterLanes = hasFilter ? Lanes<byte>.From(filter) : default;
        Span<byte> scratch = _laneScratch;
        Span<byte> keyScratch = _keyScratch;

        for (var i = 0; i < length; i++)
        {
            var row = batch.RowAt(i);
            if (hasFilter && !(filterLanes.IsValid(row) && filterLanes[row] != 0))
            {
                continue;
            }

            var group = groups[i];
            var valid = !hasArgument || LaneAccess.IsValid(argument, row);
            var lane = hasArgument && valid
                ? LaneAccess.Read(argument, row, plan.ArgumentKind, plan.ArgumentWidth, scratch)
                : default;

            if (plan.Distinct && valid)
            {
                while (plan.Seen.Count <= group)
                {
                    plan.Seen.Add(null);
                }

                var seen = plan.Seen[group] ??= new ByteSet();
                if (!seen.Add(lane))
                {
                    continue;
                }
            }

            if (hasOrderKey)
            {
                var keyValid = LaneAccess.IsValid(orderKey, row);
                var key = keyValid
                    ? LaneAccess.Read(orderKey, row, plan.OrderKeyKind, plan.OrderKeyWidth, keyScratch)
                    : default;
                plan.Accumulator.AddOrdered(group, lane, valid, key, keyValid);
                continue;
            }

            plan.Accumulator.Add(group, lane, valid);
        }
    }

    private void EnsureGroupCapacity(int groups)
    {
        // The image arrays are parallel to the dense group ids, so they grow with them and a group id
        // is an index into all four. A rental may come back longer than it was asked for and the four
        // pools need not round the same way, so the shortest of them is what the capacity is.
        var held = _groupHashes.Length;
        if (_singleKey)
        {
            held = Math.Min(held, Math.Min(_groupLow.Length, Math.Min(_groupHigh.Length, _groupLengths.Length)));
        }

        if (held < groups)
        {
            var size = Math.Max(groups, Math.Max(16, held * 2));
            _groupHashes = GrowGroups(_groupHashes, size);
            if (_singleKey)
            {
                _groupLow = GrowGroups(_groupLow, size);
                _groupHigh = GrowGroups(_groupHigh, size);
                _groupLengths = GrowGroups(_groupLengths, size);
            }
        }

        foreach (var measure in _measures)
        {
            measure.Accumulator.EnsureCapacity(Math.Max(groups, 16));
        }
    }

    /// <summary>Grows one per-group rental, keeping what is in it (ADR 0012).</summary>
    private T[] GrowGroups<T>(T[] array, int size)
        where T : struct
    {
        var grown = Context.Arena.Rent<T>(size);
        Array.Copy(array, grown, array.Length);
        Context.Arena.Return(array);
        return grown;
    }

    /// <summary>
    /// Rebuilds the probe table at <paramref name="size"/> slots. The rented array may be longer than
    /// asked for, so the mask is derived from the size this table actually uses, not from its length.
    /// </summary>
    private void Resize(int size)
    {
        var slots = Context.Arena.Rent<int>(size);
        Context.Arena.Return(_slots);
        _slots = slots;
        _slotCount = size;
        _mask = size - 1;
        Array.Fill(_slots, -1, 0, size);
        for (var group = 0; group < _groupCount; group++)
        {
            var slot = (int)(_groupHashes[group] & (ulong)_mask);
            while (_slots[slot] >= 0)
            {
                slot = (slot + 1) & _mask;
            }

            _slots[slot] = group;
        }
    }
}

using Chalk.Catalog;
using Chalk.Execution.Operators;
using Chalk.Execution.Vectors;
using Chalk.Sources;
using ArrowSchema = Apache.Arrow.Schema;

namespace Chalk.Execution.Maintenance;

/// <summary>
/// A maintained join that seeks its left side rather than holding it (D361, design 74 §6): the left
/// side is a table under an access policy read through filters and projections, and an index of the
/// table begins with the join's left keys. The join holds its right side alone.
/// </summary>
/// <remarks>
/// <para>
/// A join's change is the left changes against the right rows as they were, and the left rows as they
/// are now against the right changes. The first needs no left row held. The second needs the left rows
/// of only the keys the right changes name, and the join seeks them through the index, at the update's
/// epoch, through the statement's own filters and projections. So the state follows the right side, as
/// a reader's key set under a policy, and not the table, which a view would otherwise hold a copy of.
/// </para>
/// <para>
/// The join has no condition but its keys, so a left row meets as many right rows as its key holds. A
/// left, a semi or an anti join therefore turns on each key's count alone: when a key's count reaches or
/// leaves zero, every left row of the key is padded or unpadded, given or taken back, found by the same
/// lookup.
/// </para>
/// </remarks>
internal sealed class JoinSeekState : IMaintainedState
{
    /// <summary>The right key columns in the index's order: the values a lookup of a right row's key is made of.</summary>
    private readonly int[] _sought;
    private readonly ChalkType[] _soughtTypes;

    /// <param name="order">For each of the index's leading columns, the join key that holds its value.</param>
    public JoinSeekState(
        IReadOnlyList<ChalkType> leftTypes,
        IReadOnlyList<ChalkType> rightTypes,
        IReadOnlyList<ChalkType> outputTypes,
        int[] leftKeys,
        int[] rightKeys,
        int[] order,
        JoinMode mode,
        int batchSize)
    {
        LeftWidth = leftTypes.Count;
        RightWidth = rightTypes.Count;
        Mode = mode;
        LeftKeys = new KeyColumns(leftTypes, leftKeys);
        Right = new JoinSide(rightTypes, rightKeys);
        _sought = [.. order.Select(k => rightKeys[k])];
        _soughtTypes = [.. _sought.Select(c => rightTypes[c])];
        LeftChanges = new ChangeSet(leftTypes);
        RightChanges = new ChangeSet(rightTypes);
        Found = new ChangeSet(leftTypes);
        Output = new ChangeWriter(outputTypes, batchSize);
    }

    public int LeftWidth { get; }

    public int RightWidth { get; }

    public JoinMode Mode { get; }

    /// <summary>Whether the join gives pairs: an inner or a left join. A semi or an anti join gives left rows.</summary>
    public bool Pairs => Mode is JoinMode.Inner or JoinMode.Left;

    public KeyColumns LeftKeys { get; }

    /// <summary>The right side: its rows, how many times each is held, and the rows by key.</summary>
    public JoinSide Right { get; }

    public ChangeSet LeftChanges { get; }

    public ChangeSet RightChanges { get; }

    /// <summary>The left rows this update's lookup found, each with how many times the table holds it.</summary>
    public ChangeSet Found { get; }

    public ChangeWriter Output { get; }

    public KeyEncoder Encoder { get; } = new();

    /// <summary>Each key the right changes name in this update, by its encoding: its place in the lookup.</summary>
    public ByteMap Touched { get; } = new();

    /// <summary>One range for each key the right changes name: what the lookup asks the index for.</summary>
    public List<IndexKeyRange> Ranges { get; } = [];

    /// <summary>
    /// Whether an update has run since the join was made or its state discarded. Until one has, neither
    /// side holds anything, and the left changes are the whole of the left side.
    /// </summary>
    public bool Started { get; set; }

    /// <summary>How many keys the last update looked up.</summary>
    public int Sought { get; set; }

    /// <summary>How many left rows the last update's lookup found.</summary>
    public long Read { get; set; }

    public long Bytes =>
        Right.Bytes + LeftChanges.Bytes + RightChanges.Bytes + Found.Bytes + Output.Bytes + Touched.Bytes;

    /// <summary>The lookup of the key of row <paramref name="row"/> of the right changes: one value per index column.</summary>
    public IndexKeyRange Range(ReadOnlySpan<ColumnView> columns, int row)
    {
        var key = new object?[_sought.Length];
        for (var i = 0; i < key.Length; i++)
        {
            key[i] = ScalarValue.ClrAt(columns[_sought[i]], row, _soughtTypes[i]);
        }

        return IndexKeyRange.Equality(key);
    }

    public void Discard()
    {
        Right.Clear();
        LeftChanges.Clear();
        RightChanges.Clear();
        Found.Clear();
        Output.Clear();
        Touched.Clear();
        Ranges.Clear();
        Started = false;
        Sought = 0;
        Read = 0;
    }

    public void Trim()
    {
        LeftChanges.Trim();
        RightChanges.Trim();
        Found.Trim();
        Output.Trim();
        Touched.Trim();
        Ranges.Clear();
        Ranges.TrimExcess();
    }
}

/// <summary>A seeking join's update: the left changes against the right as it was, then the right changes against the left rows of their keys as they are.</summary>
internal sealed class JoinSeekChanges : MaintainedOperator
{
    private readonly JoinSeekState _state;
    private readonly IBatchOperator _left;
    private readonly IBatchOperator _right;
    private readonly IBatchOperator _lookup;

    // Per key the right changes name: the right rows its key held before them, how far they move it,
    // and the first of them; and per right change, the next of the same key.
    private long[] _before = new long[16];
    private long[] _moved = new long[16];
    private int[] _first = new int[16];
    private int[] _next = new int[64];

    public JoinSeekChanges(
        OperatorContext context,
        string path,
        ArrowSchema schema,
        IReadOnlyList<ChalkType> types,
        JoinSeekState state,
        IBatchOperator left,
        IBatchOperator right,
        IBatchOperator lookup)
        : base(context, path, schema, types)
    {
        _state = state;
        _left = left;
        _right = right;
        _lookup = lookup;
    }

    protected override async ValueTask DisposeCoreAsync()
    {
        await _left.DisposeAsync().ConfigureAwait(false);
        await _right.DisposeAsync().ConfigureAwait(false);
        await _lookup.DisposeAsync().ConfigureAwait(false);
    }

    protected override async ValueTask<ChangeWriter> ComputeAsync(CancellationToken ct)
    {
        var state = _state;
        await DrainAsync(_left, state.LeftChanges, ct).ConfigureAwait(false);
        await DrainAsync(_right, state.RightChanges, ct).ConfigureAwait(false);
        state.Output.Clear();
        state.Sought = 0;
        state.Read = 0;
        if (!state.Started)
        {
            // Nothing is held on either side, so the result is the left rows against the right as it is:
            // no lookup is needed.
            ApplyRight();
            LeftAgainstRight();
            state.Started = true;
            return state.Output;
        }

        LeftAgainstRight();
        await RightAgainstLeftAsync(ct).ConfigureAwait(false);
        ApplyRight();
        return state.Output;
    }

    /// <summary>The left changes against the right rows as they are held.</summary>
    private void LeftAgainstRight()
    {
        var state = _state;
        var changes = state.LeftChanges;
        var right = state.Right;
        var encoder = state.Encoder;
        for (var l = 0; l < changes.Count; l++)
        {
            var weight = changes.Weight(l);
            if (weight == 0)
            {
                continue;
            }

            // A NULL key meets nothing.
            var count = 0L;
            if (state.LeftKeys.Encode(encoder, changes.Columns, l, normalise: true) && right.Bucket(encoder) is { } bucket)
            {
                foreach (var slot in bucket)
                {
                    count += right.Counts[slot];
                    if (state.Pairs)
                    {
                        Pair(changes.Columns, l, right.Rows, slot, weight * right.Counts[slot]);
                    }
                }
            }

            if (count == 0)
            {
                Unmatched(changes.Columns, l, weight);
            }
            else
            {
                Matched(changes.Columns, l, weight);
            }
        }
    }

    /// <summary>The right changes against the left rows of their keys as they are now, sought through the index.</summary>
    private async ValueTask RightAgainstLeftAsync(CancellationToken ct)
    {
        var state = _state;
        var changes = state.RightChanges;
        var right = state.Right;
        var encoder = state.Encoder;
        var touched = state.Touched;
        touched.Clear();
        state.Ranges.Clear();
        if (_next.Length < changes.Count)
        {
            _next = new int[Math.Max(changes.Count, _next.Length * 2)];
        }

        var keys = 0;
        for (var r = 0; r < changes.Count; r++)
        {
            var weight = changes.Weight(r);
            if (weight == 0 || !right.Keys.Encode(encoder, changes.Columns, r, normalise: true))
            {
                continue;
            }

            ref var entry = ref touched.GetOrAdd(encoder.Span, out var added);
            if (added)
            {
                entry = keys;
                if (keys == _before.Length)
                {
                    Array.Resize(ref _before, keys * 2);
                    Array.Resize(ref _moved, keys * 2);
                    Array.Resize(ref _first, keys * 2);
                }

                var before = 0L;
                foreach (var slot in right.Bucket(encoder) ?? [])
                {
                    before += right.Counts[slot];
                }

                _before[keys] = before;
                _moved[keys] = 0;
                _first[keys] = -1;
                state.Ranges.Add(state.Range(changes.Columns, r));
                keys++;
            }

            var key = entry;
            _moved[key] += weight;
            _next[r] = _first[key];
            _first[key] = r;
        }

        state.Sought = keys;
        if (keys == 0)
        {
            return;
        }

        // The left rows of those keys at the update's epoch, through the statement's own filters and
        // projections.
        var found = state.Found;
        found.Clear();
        await foreach (var batch in _lookup.ExecuteAsync(ct).ConfigureAwait(false))
        {
            found.AddRows(batch, 1);
        }

        found.Seal();
        for (var l = 0; l < found.Count; l++)
        {
            var copies = found.Weight(l);
            state.Read += copies;

            // An index may give more than the join's keys hold equal, as one that ignores case would: such
            // a row is not this key's.
            if (!state.LeftKeys.Encode(encoder, found.Columns, l, normalise: true) || !touched.TryGet(encoder.Span, out var key))
            {
                continue;
            }

            if (state.Pairs)
            {
                for (var r = _first[key]; r >= 0; r = _next[r])
                {
                    Pair(found.Columns, l, changes.Columns, r, copies * changes.Weight(r));
                }
            }

            // A key that starts or stops meeting the right side: its rows were unmatched and are matched
            // now, or the other way round.
            var (before, after) = (_before[key], _before[key] + _moved[key]);
            if (before == 0 && after > 0)
            {
                Unmatched(found.Columns, l, -copies);
                Matched(found.Columns, l, copies);
            }
            else if (before > 0 && after == 0)
            {
                Unmatched(found.Columns, l, copies);
                Matched(found.Columns, l, -copies);
            }
        }
    }

    private void ApplyRight()
    {
        var state = _state;
        var changes = state.RightChanges;
        for (var r = 0; r < changes.Count; r++)
        {
            var weight = changes.Weight(r);
            if (weight != 0 && state.Right.Keys.Encode(state.Encoder, changes.Columns, r, normalise: true))
            {
                state.Right.Apply(changes, r, weight, matches: 0, state.Encoder);
            }
        }
    }

    /// <summary>A left row that meets no right row: a left join pads it, an anti join gives it.</summary>
    private void Unmatched(ReadOnlySpan<ColumnView> left, int row, long weight)
    {
        switch (_state.Mode)
        {
            case JoinMode.Left:
                Padded(left, row, weight);
                break;
            case JoinMode.Anti:
                Alone(left, row, weight);
                break;
        }
    }

    /// <summary>A left row that meets a right row: a semi join gives it, once.</summary>
    private void Matched(ReadOnlySpan<ColumnView> left, int row, long weight)
    {
        if (_state.Mode == JoinMode.Semi)
        {
            Alone(left, row, weight);
        }
    }

    private void Pair(ReadOnlySpan<ColumnView> left, int row, RowStore right, int slot, long weight)
    {
        var state = _state;
        var into = state.Output;
        into.Begin();
        for (var c = 0; c < state.LeftWidth; c++)
        {
            into.Column(c).Append(left[c], row);
        }

        for (var c = 0; c < state.RightWidth; c++)
        {
            into.Column(state.LeftWidth + c).Append(right.Column(c), slot);
        }

        into.End(weight);
    }

    private void Pair(ReadOnlySpan<ColumnView> left, int row, ReadOnlySpan<ColumnView> right, int rightRow, long weight)
    {
        var state = _state;
        var into = state.Output;
        into.Begin();
        for (var c = 0; c < state.LeftWidth; c++)
        {
            into.Column(c).Append(left[c], row);
        }

        for (var c = 0; c < state.RightWidth; c++)
        {
            into.Column(state.LeftWidth + c).Append(right[c], rightRow);
        }

        into.End(weight);
    }

    /// <summary>A left row with every right column NULL.</summary>
    private void Padded(ReadOnlySpan<ColumnView> left, int row, long weight)
    {
        var state = _state;
        var into = state.Output;
        into.Begin();
        for (var c = 0; c < state.LeftWidth; c++)
        {
            into.Column(c).Append(left[c], row);
        }

        for (var c = 0; c < state.RightWidth; c++)
        {
            into.Column(state.LeftWidth + c).AppendNull();
        }

        into.End(weight);
    }

    /// <summary>A left row alone, as a semi or an anti join gives it.</summary>
    private void Alone(ReadOnlySpan<ColumnView> left, int row, long weight)
    {
        var into = _state.Output;
        into.Begin();
        for (var c = 0; c < _state.LeftWidth; c++)
        {
            into.Column(c).Append(left[c], row);
        }

        into.End(weight);
    }
}

/// <summary>
/// The read under a seeking join's lookup: the rows of the table whose index keys are those the
/// update asks for, at the update's epoch, the columns the statement's read takes.
/// </summary>
internal sealed class KeyLookup : OperatorBase
{
    private readonly JoinSeekState _state;
    private readonly ISourceRuntime _source;
    private readonly string _table;
    private readonly string _index;
    private readonly IReadOnlyList<int> _projection;
    private readonly ColumnarBatch _output;
    private readonly ColumnView[]?[] _children;

    public KeyLookup(
        OperatorContext context,
        string path,
        ArrowSchema schema,
        IReadOnlyList<ChalkType> types,
        JoinSeekState state,
        ISourceRuntime source,
        string table,
        string index,
        IReadOnlyList<int> projection)
        : base(context, schema, types, path)
    {
        _state = state;
        _source = source;
        _table = table;
        _index = index;
        _projection = projection;
        _output = NewOutput();
        _children = ArrowBatchViews.ChildHolders(types);
    }

    protected override ValueTask DisposeCoreAsync() => default;

    protected override async IAsyncEnumerable<ColumnarBatch> RunAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        if (_state.Ranges.Count == 0)
        {
            yield break;
        }

        var request = new IndexLookupRequest
        {
            Table = _table,
            Index = _index,
            Ranges = _state.Ranges,
            Projection = _projection,
            OutputSchema = Schema,
            BatchSize = Context.Settings.BatchSize,
        };

        // The source reads the state the update pinned, as every read of the update does.
        var scanContext = ScanContextFor(Context);
        if (_source is IColumnarBatchSource columnar && columnar.ColumnarLookup(request, scanContext) is { } direct)
        {
            await using (direct.ConfigureAwait(false))
            {
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!direct.TryNext(_output) && !await direct.WaitAsync(_output, ct).ConfigureAwait(false))
                    {
                        yield break;
                    }

                    yield return _output;
                }
            }
        }

        await foreach (var batch in _source.IndexLookupAsync(request, scanContext, ct).WithCancellation(ct))
        {
            try
            {
                if (!ArrowTypeMapping.AreEquivalent(Schema, batch.Schema))
                {
                    throw new SourceContractException(
                        ChalkErrorCodes.SourceContract,
                        _source.SourceId,
                        _table,
                        $"a lookup's batch has schema {ArrowTypeMapping.DescribeArrow(batch.Schema)}, "
                        + $"not the requested {ArrowTypeMapping.DescribeArrow(Schema)}.");
                }

                ArrowBatchViews.Fill(_output, batch, ColumnTypes, _children);
                yield return _output;
            }
            finally
            {
                batch.Dispose();
            }
        }
    }
}

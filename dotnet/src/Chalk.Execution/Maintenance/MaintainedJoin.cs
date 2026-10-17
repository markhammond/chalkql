using Chalk.Catalog;
using Chalk.Execution.Expressions;
using Chalk.Execution.Operators;
using Chalk.Sources;
using ArrowSchema = Apache.Arrow.Schema;

namespace Chalk.Execution.Maintenance;

/// <summary>
/// One side of a maintained join (D343, design 68 §2): its distinct rows, how many times each is
/// held, and the rows by join key. A row with a NULL key joins nothing, so neither side keeps one.
/// </summary>
internal sealed class JoinSide
{
    public JoinSide(IReadOnlyList<ChalkType> types, int[] keys)
    {
        Rows = new RowStore(types);
        Keys = new KeyColumns(types, keys);
    }

    public RowStore Rows { get; }

    public KeyColumns Keys { get; }

    /// <summary>How many times each slot's row is held.</summary>
    public long[] Counts { get; private set; } = [];

    /// <summary>For the left side of a left join: how many right rows each slot's row meets.</summary>
    public long[] Matches { get; private set; } = [];

    /// <summary>Each held row's slot, by its identity.</summary>
    public ByteMap Slots { get; } = new();

    public Buckets Buckets { get; } = new();

    /// <summary>The side's rows, their counts and matches, and the rows by identity and by key, at their size (F223).</summary>
    public long Bytes =>
        Rows.Bytes + HeapSizes.Array(Counts.Length, sizeof(long)) + HeapSizes.Array(Matches.Length, sizeof(long))
        + Slots.Bytes + Buckets.Bytes;

    /// <summary>The slots whose rows have the key now in <paramref name="encoder"/>, or null.</summary>
    public List<int>? Bucket(KeyEncoder encoder) => Buckets.Find(encoder.Span);

    /// <summary>
    /// Puts <paramref name="weight"/> copies of a row of <paramref name="changes"/> in, or takes them
    /// out, its key already checked not NULL. Returns the row's slot, or -1 when it is no longer held.
    /// </summary>
    public int Apply(ChangeSet changes, int row, long weight, long matches, KeyEncoder encoder)
    {
        ref var held = ref Slots.GetOrAdd(changes.Identity(row), out var added);
        if (!added)
        {
            var slot = held;
            Counts[slot] += weight;
            if (Counts[slot] > 0)
            {
                return slot;
            }

            if (Counts[slot] < 0)
            {
                throw new InvalidOperationException("a maintained join took out more copies of a row than it held.");
            }

            Slots.Remove(changes.Identity(row), out _);
            Buckets.Remove(slot);
            Rows.Remove(slot);
            return -1;
        }

        if (weight < 0)
        {
            throw new InvalidOperationException("a maintained join was asked to take out a row it does not hold.");
        }

        var fresh = Rows.Add(changes.Columns, row);
        held = fresh;
        if (Counts.Length < Rows.Slots)
        {
            var size = Math.Max(Rows.Slots, Math.Max(16, Counts.Length * 2));
            var counts = Counts;
            var meets = Matches;
            Array.Resize(ref counts, size);
            Array.Resize(ref meets, size);
            Counts = counts;
            Matches = meets;
        }

        Counts[fresh] = weight;
        Matches[fresh] = matches;
        Keys.Encode(encoder, changes.Columns, row, normalise: true);
        Buckets.Add(encoder.Span, fresh);
        return fresh;
    }

    public void Clear()
    {
        Rows.Clear();
        Slots.Clear();
        Buckets.Clear();
        Array.Clear(Counts);
        Array.Clear(Matches);
    }
}

/// <summary>
/// A maintained inner or left join: hash, merge or nested loop (design 68 §2). The left changes meet
/// the right rows as they are after this update, and the right changes the left rows as they were
/// before it, which together are the join's change. A left join counts each left row's matches, and
/// pads the row with NULLs while the count is zero.
/// </summary>
/// <summary>What a maintained join gives (D343; D361 for the last two).</summary>
internal enum JoinMode
{
    /// <summary>Every pair that matches.</summary>
    Inner,

    /// <summary>Every pair, and each left row that meets nothing with its right columns NULL.</summary>
    Left,

    /// <summary>Each left row that meets at least one right row, once, its left columns alone.</summary>
    Semi,

    /// <summary>Each left row that meets no right row, its left columns alone.</summary>
    Anti,
}

internal sealed class JoinState : IMaintainedState
{
    public JoinState(
        IReadOnlyList<ChalkType> leftTypes,
        IReadOnlyList<ChalkType> rightTypes,
        IReadOnlyList<ChalkType> outputTypes,
        int[] leftKeys,
        int[] rightKeys,
        JoinMode mode,
        int batchSize)
    {
        LeftWidth = leftTypes.Count;
        RightWidth = rightTypes.Count;
        Mode = mode;
        Left = new JoinSide(leftTypes, leftKeys);
        Right = new JoinSide(rightTypes, rightKeys);
        LeftChanges = new ChangeSet(leftTypes);
        RightChanges = new ChangeSet(rightTypes);
        Output = new ChangeWriter(outputTypes, batchSize);

        // A pair a condition has still to be judged on carries both sides, whatever the join gives.
        Candidates = new ChangeWriter([.. leftTypes, .. rightTypes], batchSize);
    }

    public int LeftWidth { get; }

    public int RightWidth { get; }

    public JoinMode Mode { get; }

    public bool LeftJoin => Mode == JoinMode.Left;

    /// <summary>Whether the join gives pairs: an inner or a left join. A semi or an anti join gives left rows.</summary>
    public bool Pairs => Mode is JoinMode.Inner or JoinMode.Left;

    /// <summary>Whether each left row's matches are counted: every join but an inner one turns on them.</summary>
    public bool Counted => Mode != JoinMode.Inner;

    public JoinSide Left { get; }

    public JoinSide Right { get; }

    public ChangeSet LeftChanges { get; }

    public ChangeSet RightChanges { get; }

    public ChangeWriter Output { get; }

    /// <summary>Pairs a condition has still to be evaluated on.</summary>
    public ChangeWriter Candidates { get; }

    public KeyEncoder Encoder { get; } = new();

    public long Bytes =>
        Left.Bytes + Right.Bytes + LeftChanges.Bytes + RightChanges.Bytes + Output.Bytes + Candidates.Bytes;

    public void Discard()
    {
        Left.Clear();
        Right.Clear();
        LeftChanges.Clear();
        RightChanges.Clear();
        Output.Clear();
        Candidates.Clear();
    }

    public void Trim()
    {
        LeftChanges.Trim();
        RightChanges.Trim();
        Output.Trim();
        Candidates.Trim();
    }
}

internal sealed class JoinChanges : MaintainedOperator, IKeepsScratch
{
    private readonly JoinState _state;
    private readonly IBatchOperator _left;
    private readonly IBatchOperator _right;
    private readonly IVectorExpr? _condition;
    private readonly EvalContext? _eval;

    // Per candidate pair: where its left row is (a left slot, or ~row of the left changes), the units
    // of match it adds, and the right slot (B) or the right change row (A).
    private int[] _candidateLeft = new int[64];
    private long[] _candidateUnits = new long[64];
    private int _candidates;
    private bool[] _pass = new bool[64];

    // The left join's per-update bookkeeping.
    private readonly Dictionary<int, long> _touched = [];
    private long[] _changeMatches = new long[64];

    // The two ways a candidate is counted, made once rather than as a delegate at every update (F244).
    private readonly Action<int> _countA;
    private readonly Action<int> _countB;

    public JoinChanges(
        OperatorContext context,
        string path,
        ArrowSchema schema,
        IReadOnlyList<ChalkType> types,
        JoinState state,
        IBatchOperator left,
        IBatchOperator right,
        IVectorExpr? condition)
        : base(context, path, schema, types)
    {
        _state = state;
        _left = left;
        _right = right;
        _condition = condition;
        _eval = condition is null ? null : context.NewEvalContext();
        _countA = CountA;
        _countB = CountB;
        context.Scratch.Add(this);
    }

    /// <summary>The candidate pairs, the pass marks and the left join's bookkeeping, back to their first size.</summary>
    public void Trim()
    {
        _candidateLeft = new int[64];
        _candidateUnits = new long[64];
        _pass = new bool[64];
        _touched.Clear();
        _touched.TrimExcess();
        _changeMatches = new long[64];
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
        await DrainAsync(_right, state.RightChanges, ct).ConfigureAwait(false);
        state.Output.Clear();
        var encoder = state.Encoder;
        var left = state.Left;
        var right = state.Right;
        var leftChanges = state.LeftChanges;
        var rightChanges = state.RightChanges;

        // A: the right changes against the left rows as they were.
        _touched.Clear();
        BeginCandidates();
        for (var r = 0; r < rightChanges.Count; r++)
        {
            var weight = rightChanges.Weight(r);
            if (weight == 0 || !right.Keys.Encode(encoder, rightChanges.Columns, r, normalise: true))
            {
                continue;
            }

            if (left.Bucket(encoder) is not { } bucket)
            {
                continue;
            }

            foreach (var slot in bucket)
            {
                AddCandidate(
                    leftSlot: slot, leftChange: -1, rightSlot: -1, rightChange: r,
                    weight: left.Counts[slot] * weight, units: weight);
            }
        }

        FlushCandidates(countInto: _countA);

        // The left rows that stopped or started meeting the right side, as they were held: a left join
        // pads or unpads them, a semi join gives or takes them back, an anti join the other way round.
        if (state.Counted)
        {
            foreach (var (slot, before) in _touched)
            {
                var after = left.Matches[slot];
                var sign = before == 0 && after > 0 ? 1 : before > 0 && after == 0 ? -1 : 0;
                switch (state.Mode)
                {
                    case JoinMode.Left when sign != 0:
                        WritePadded(slot, -1, -sign * left.Counts[slot]);
                        break;
                    case JoinMode.Semi when sign != 0:
                        WriteLeft(slot, -1, sign * left.Counts[slot]);
                        break;
                    case JoinMode.Anti when sign != 0:
                        WriteLeft(slot, -1, -sign * left.Counts[slot]);
                        break;
                }
            }
        }

        for (var r = 0; r < rightChanges.Count; r++)
        {
            var weight = rightChanges.Weight(r);
            if (weight != 0 && right.Keys.Encode(encoder, rightChanges.Columns, r, normalise: true))
            {
                right.Apply(rightChanges, r, weight, matches: 0, encoder);
            }
        }

        // B: the left changes against the right rows as they are.
        if (_changeMatches.Length < leftChanges.Count)
        {
            _changeMatches = new long[Math.Max(leftChanges.Count, _changeMatches.Length * 2)];
        }

        Array.Clear(_changeMatches, 0, leftChanges.Count);
        BeginCandidates();
        for (var l = 0; l < leftChanges.Count; l++)
        {
            var weight = leftChanges.Weight(l);
            if (weight == 0)
            {
                continue;
            }

            if (!left.Keys.Encode(encoder, leftChanges.Columns, l, normalise: true))
            {
                // A NULL key meets nothing: a left join pads the row, an anti join gives it, and an inner
                // or a semi join drops it.
                if (state.Mode == JoinMode.Left)
                {
                    WritePadded(-1, l, weight);
                }
                else if (state.Mode == JoinMode.Anti)
                {
                    WriteLeft(-1, l, weight);
                }

                continue;
            }

            if (right.Bucket(encoder) is not { } bucket)
            {
                continue;
            }

            foreach (var slot in bucket)
            {
                AddCandidate(
                    leftSlot: -1, leftChange: l, rightSlot: slot, rightChange: -1,
                    weight: weight * right.Counts[slot], units: right.Counts[slot]);
            }
        }

        FlushCandidates(countInto: _countB);

        for (var l = 0; l < leftChanges.Count; l++)
        {
            var weight = leftChanges.Weight(l);
            if (weight == 0 || !left.Keys.Encode(encoder, leftChanges.Columns, l, normalise: true))
            {
                continue;
            }

            switch (state.Mode)
            {
                case JoinMode.Left when _changeMatches[l] == 0:
                    WritePadded(-1, l, weight);
                    break;
                case JoinMode.Semi when _changeMatches[l] > 0:
                case JoinMode.Anti when _changeMatches[l] == 0:
                    WriteLeft(-1, l, weight);
                    break;
            }

            left.Apply(leftChanges, l, weight, _changeMatches[l], encoder);
        }

        return state.Output;
    }

    private void CountA(int candidate)
    {
        var slot = _candidateLeft[candidate];
        if (!_touched.ContainsKey(slot))
        {
            _touched[slot] = _state.Left.Matches[slot];
        }

        _state.Left.Matches[slot] += _candidateUnits[candidate];
    }

    private void CountB(int candidate) => _changeMatches[~_candidateLeft[candidate]] += _candidateUnits[candidate];

    private void BeginCandidates()
    {
        _candidates = 0;
        _state.Candidates.Clear();
    }

    /// <summary>
    /// A pair whose keys match: written straight to the output when the join has no condition, and
    /// kept to be evaluated otherwise. Its left row is a held slot or a change row, and so is its right.
    /// </summary>
    private void AddCandidate(int leftSlot, int leftChange, int rightSlot, int rightChange, long weight, long units)
    {
        var state = _state;
        if (_condition is not null || state.Pairs)
        {
            WritePair(_condition is null ? state.Output : state.Candidates, leftSlot, leftChange, rightSlot, rightChange, weight);
        }

        if (_candidates == _candidateLeft.Length)
        {
            Array.Resize(ref _candidateLeft, _candidates * 2);
            Array.Resize(ref _candidateUnits, _candidates * 2);
        }

        _candidateLeft[_candidates] = leftSlot >= 0 ? leftSlot : ~leftChange;
        _candidateUnits[_candidates] = units;
        _candidates++;
    }

    private void WritePair(ChangeWriter into, int leftSlot, int leftChange, int rightSlot, int rightChange, long weight)
    {
        into.Begin();
        var state = _state;
        for (var c = 0; c < state.LeftWidth; c++)
        {
            if (leftSlot >= 0)
            {
                into.Column(c).Append(state.Left.Rows.Column(c), leftSlot);
            }
            else
            {
                into.Column(c).Append(state.LeftChanges.Columns[c], leftChange);
            }
        }

        for (var c = 0; c < state.RightWidth; c++)
        {
            if (rightSlot >= 0)
            {
                into.Column(state.LeftWidth + c).Append(state.Right.Rows.Column(c), rightSlot);
            }
            else
            {
                into.Column(state.LeftWidth + c).Append(state.RightChanges.Columns[c], rightChange);
            }
        }

        into.End(weight);
    }

    /// <summary>
    /// Keeps the pairs the condition holds for, and counts each kept pair's matches. Without a
    /// condition every pair is kept, and is already in the output.
    /// </summary>
    private void FlushCandidates(Action<int> countInto)
    {
        if (_condition is null)
        {
            if (_state.Counted)
            {
                for (var i = 0; i < _candidates; i++)
                {
                    countInto(i);
                }
            }

            return;
        }

        var candidates = _state.Candidates;
        var index = 0;
        for (var b = 0; b < candidates.BatchCount; b++)
        {
            var batch = candidates.Batch(b);
            if (_pass.Length < batch.RowCount)
            {
                _pass = new bool[Math.Max(batch.RowCount, _pass.Length * 2)];
            }

            Evaluate(_condition, _eval!, batch, _pass);
            var columns = batch.Columns;
            for (var row = 0; row < batch.RowCount; row++, index++)
            {
                if (!_pass[row])
                {
                    continue;
                }

                if (_state.Pairs)
                {
                    _state.Output.Write(columns, row, Weights.Of(batch, row));
                }

                if (_state.Counted)
                {
                    countInto(index);
                }
            }
        }
    }

    /// <summary>A left row alone, as a semi or an anti join gives it: a held slot, or a row of the left changes.</summary>
    private void WriteLeft(int leftSlot, int leftChange, long weight)
    {
        var state = _state;
        var into = state.Output;
        into.Begin();
        for (var c = 0; c < state.LeftWidth; c++)
        {
            if (leftSlot >= 0)
            {
                into.Column(c).Append(state.Left.Rows.Column(c), leftSlot);
            }
            else
            {
                into.Column(c).Append(state.LeftChanges.Columns[c], leftChange);
            }
        }

        into.End(weight);
    }

    /// <summary>A left row with every right column NULL: a held slot, or a row of the left changes.</summary>
    private void WritePadded(int leftSlot, int leftChange, long weight)
    {
        var state = _state;
        var into = state.Output;
        into.Begin();
        for (var c = 0; c < state.LeftWidth; c++)
        {
            if (leftSlot >= 0)
            {
                into.Column(c).Append(state.Left.Rows.Column(c), leftSlot);
            }
            else
            {
                into.Column(c).Append(state.LeftChanges.Columns[c], leftChange);
            }
        }

        for (var c = 0; c < state.RightWidth; c++)
        {
            into.Column(state.LeftWidth + c).AppendNull();
        }

        into.End(weight);
    }
}

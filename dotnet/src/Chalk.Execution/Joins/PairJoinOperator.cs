using Chalk.Catalog;
using Chalk.Execution.Expressions;
using Chalk.Execution.Operators;
using Chalk.Ir;
using Chalk.Sources;
using Array = System.Array;
using ArrowSchema = Apache.Arrow.Schema;

namespace Chalk.Execution.Joins;

/// <summary>
/// What a hash join and a nested loop join have in common: buffer the right input, stream the left
/// one, and for each left row walk the right rows that could match.
/// </summary>
/// <remarks>
/// <para>
/// The two differ only in <em>which</em> right rows are candidates — a hash bucket, or all of them —
/// so that is the one thing a subclass supplies.
/// </para>
/// <para>
/// Rows come out in left-input order, which is the ordering the IR lets these joins claim (D42), so a
/// left row's matches and its null padding are emitted before the next left row is looked at. The
/// condition that is not an equality is evaluated <em>vectorised over a batch of candidate pairs</em>
/// and before the row counts as matched, which is what makes an outer join right: a left row whose
/// only candidate fails the remainder is unmatched, not dropped.
/// </para>
/// <para>
/// Since step 20 the emit walk is an explicit cursor rather than a list of finished batches: the
/// output slot is reused, so a batch has to leave the operator before the next one is built (D61).
/// </para>
/// </remarks>
internal abstract class PairJoinOperator : OperatorBase
{
    private readonly IBatchOperator _left;
    private readonly IBatchOperator _right;
    private readonly JoinType _joinType;
    private readonly IVectorExpr? _condition;
    private readonly JoinAssembler _output;
    private readonly JoinAssembler? _candidates;
    private readonly ColumnarBatch _slot;
    private readonly ColumnarBatch? _candidateSlot;
    private readonly EvalContext _eval;
    private readonly int _leftWidth;
    private readonly ChalkType[] _rightTypes;

    /// <summary>
    /// The plan's estimate of the right input's rows — the side this join buffers whole — or zero
    /// where the plan has none (D258.3).
    /// </summary>
    private readonly double _estimatedRightRows;

    private int[] _outLeft = [];
    private int[] _outRight = [];
    private int[] _candLeft = [];
    private int[] _candRight = [];
    private byte[] _keep = [];
    private byte[] _buildMatched = [];
    private int _outCount;

    protected PairJoinOperator(
        OperatorContext context,
        string path,
        ArrowSchema schema,
        IReadOnlyList<ChalkType> outputTypes,
        IReadOnlyList<ChalkType> leftTypes,
        IReadOnlyList<ChalkType> rightTypes,
        IBatchOperator left,
        IBatchOperator right,
        JoinType joinType,
        IVectorExpr? condition,
        double estimatedRightRows = 0)
        : base(context, schema, outputTypes, path)
    {
        _left = left;
        _right = right;
        _joinType = joinType;
        _condition = condition;
        _leftWidth = leftTypes.Count;
        _rightTypes = [.. rightTypes];
        _estimatedRightRows = estimatedRightRows;
        _eval = context.NewEvalContext();
        _output = new JoinAssembler(outputTypes, _leftWidth);
        _slot = NewOutput();
        Right = new JoinRows(rightTypes);

        if (condition is not null)
        {
            // The remainder reads the joined row, so it needs a batch of left ++ right whatever the
            // join's own output row is — a SEMI join outputs the left row only.
            var joined = new List<ChalkType>(leftTypes.Count + rightTypes.Count);
            joined.AddRange(leftTypes);
            joined.AddRange(rightTypes);
            _candidates = new JoinAssembler(joined, _leftWidth);
            _candidateSlot = NewOutput(joined);
        }
    }

    /// <summary>The buffered right input. Valid between <see cref="JoinRows.FillAsync"/> and the run's end.</summary>
    protected JoinRows Right { get; }

    protected JoinType JoinType => _joinType;

    /// <summary>The first candidate right row for this probe row, or -1.</summary>
    protected abstract int FirstCandidate(ColumnarBatch probe, int probeRow);

    /// <summary>The next candidate after <paramref name="rightRow"/>, or -1.</summary>
    protected abstract int NextCandidate(int rightRow);

    /// <summary>Indexes the buffered right input, once, before the first probe batch.</summary>
    protected abstract void OnBuilt(ExecutionArena arena);

    /// <summary>Called before each probe batch, so a subclass can prepare its key vectors.</summary>
    protected abstract void OnProbeBatch(ColumnarBatch probe);

    /// <summary>Releases whatever <see cref="OnBuilt"/> rented.</summary>
    protected abstract void ReleaseBuild(ExecutionArena arena);

    protected override async ValueTask DisposeCoreAsync()
    {
        await _left.DisposeAsync().ConfigureAwait(false);
        await _right.DisposeAsync().ConfigureAwait(false);
    }

    protected override async IAsyncEnumerable<ColumnarBatch> RunAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var arena = Context.Arena;
        var batchSize = Context.Settings.BatchSize;
        try
        {
            // The build side is read whole, so the plan's estimate for it is what its copiers start
            // at rather than a batch (D258.3).
            await Right
                .FillAsync(_right, ct, PlanCapacity.Rows(Context, _estimatedRightRows, _rightTypes))
                .ConfigureAwait(false);
            OnBuilt(arena);

            _outLeft = arena.Rent<int>(batchSize);
            _outRight = arena.Rent<int>(batchSize);
            var candidateCap = Math.Max(batchSize, 1);
            _candLeft = arena.Rent<int>(candidateCap * 2);
            _candRight = arena.Rent<int>(candidateCap * 2);
            _keep = arena.Rent<byte>(candidateCap * 2);
            if (NeedsBuildMatches)
            {
                _buildMatched = arena.Rent<byte>(Math.Max(Right.Count, 1));
                Array.Clear(_buildMatched, 0, Right.Count);
            }

            _outCount = 0;
            await foreach (var probe in _left.ExecuteAsync(ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                OnProbeBatch(probe);

                var rows = probe.Count;
                var start = 0;
                while (start < rows)
                {
                    var count = 0;
                    var windowEnd = start;
                    while (windowEnd < rows)
                    {
                        var probeRow = probe.RowAt(windowEnd);
                        for (var right = FirstCandidate(probe, probeRow); right >= 0; right = NextCandidate(right))
                        {
                            if (count == _candLeft.Length)
                            {
                                Grow();
                            }

                            _candLeft[count] = probeRow;
                            _candRight[count] = right;
                            count++;
                        }

                        windowEnd++;
                        if (count >= candidateCap)
                        {
                            break;
                        }
                    }

                    ApplyCondition(probe, count);

                    // Walk the window one output row at a time so a full batch can leave before the
                    // next one is built into the same slot.
                    var pointer = 0;
                    for (var i = start; i < windowEnd; i++)
                    {
                        var probeRow = probe.RowAt(i);
                        var matched = false;
                        while (pointer < count && _candLeft[pointer] == probeRow)
                        {
                            if (_keep[pointer] != 0)
                            {
                                matched = true;
                                if (NeedsBuildMatches)
                                {
                                    _buildMatched[_candRight[pointer]] = 1;
                                }

                                if (ProjectsRight)
                                {
                                    _outLeft[_outCount] = probeRow;
                                    _outRight[_outCount] = _candRight[pointer];
                                    _outCount++;
                                    if (_outCount == batchSize)
                                    {
                                        yield return Flush(probe);
                                    }
                                }
                            }

                            pointer++;
                        }

                        var pad = _joinType switch
                        {
                            JoinType.Semi => matched,
                            JoinType.Anti => !matched,
                            JoinType.Left or JoinType.Full => !matched,
                            _ => false,
                        };

                        if (pad)
                        {
                            _outLeft[_outCount] = probeRow;
                            _outRight[_outCount] = -1;
                            _outCount++;
                            if (_outCount == batchSize)
                            {
                                yield return Flush(probe);
                            }
                        }
                    }

                    start = windowEnd;
                }

                // Every pending row names this batch's columns, so the flush belongs here.
                if (_outCount > 0)
                {
                    yield return Flush(probe);
                }
            }

            if (NeedsBuildMatches)
            {
                var pending = 0;
                for (var row = 0; row < Right.Count; row++)
                {
                    if (_buildMatched[row] != 0)
                    {
                        continue;
                    }

                    _outLeft[pending] = -1;
                    _outRight[pending] = row;
                    pending++;
                    if (pending == batchSize)
                    {
                        ct.ThrowIfCancellationRequested();
                        yield return _output.Emit(
                            _slot, [], _outLeft.AsSpan(0, pending), Right.Columns,
                            _outRight.AsSpan(0, pending));
                        pending = 0;
                    }
                }

                if (pending > 0)
                {
                    yield return _output.Emit(
                        _slot, [], _outLeft.AsSpan(0, pending), Right.Columns,
                        _outRight.AsSpan(0, pending));
                }
            }
        }
        finally
        {
            ReleaseBuild(arena);
            arena.Return(_outLeft);
            arena.Return(_outRight);
            arena.Return(_candLeft);
            arena.Return(_candRight);
            arena.Return(_keep);
            arena.Return(_buildMatched);
            _outLeft = [];
            _outRight = [];
            _candLeft = [];
            _candRight = [];
            _keep = [];
            _buildMatched = [];
            Right.Release();
        }
    }

    /// <summary>RIGHT and FULL owe every unmatched build row a row of its own.</summary>
    private bool NeedsBuildMatches => _joinType is JoinType.Right or JoinType.Full;

    private bool ProjectsRight => _joinType is not (JoinType.Semi or JoinType.Anti);

    /// <summary>Evaluates the non-equality remainder over the candidate pairs, in batch-sized chunks.</summary>
    private void ApplyCondition(ColumnarBatch probe, int count)
    {
        if (_condition is null || _candidates is null || _candidateSlot is null)
        {
            Array.Fill(_keep, (byte)1, 0, count);
            return;
        }

        var chunk = Math.Max(Context.Settings.BatchSize, 1);
        for (var start = 0; start < count; start += chunk)
        {
            var length = Math.Min(chunk, count - start);
            var batch = _candidates.Emit(
                _candidateSlot,
                probe.Columns,
                _candLeft.AsSpan(start, length),
                Right.Columns,
                _candRight.AsSpan(start, length));

            _eval.SetBatch(batch, length);
            var mask = _condition.Evaluate(_eval);
            if (mask.IsScalar)
            {
                var pass = !mask.Scalar.IsNull && mask.Scalar.Integer != 0;
                Array.Fill(_keep, (byte)(pass ? 1 : 0), start, length);
            }
            else
            {
                var lanes = Lanes<byte>.From(mask);
                for (var i = 0; i < length; i++)
                {
                    _keep[start + i] = (byte)(lanes.IsValid(i) && lanes[i] != 0 ? 1 : 0);
                }
            }
        }
    }

    private ColumnarBatch Flush(ColumnarBatch probe)
    {
        var batch = _output.Emit(
            _slot,
            probe.Columns,
            _outLeft.AsSpan(0, _outCount),
            ProjectsRight ? Right.Columns : [],
            _outRight.AsSpan(0, _outCount));
        _outCount = 0;
        return batch;
    }

    private void Grow()
    {
        var arena = Context.Arena;
        var size = _candLeft.Length * 2;
        var left = arena.Rent<int>(size);
        var right = arena.Rent<int>(size);
        var keep = arena.Rent<byte>(size);
        _candLeft.AsSpan(0, _candLeft.Length).CopyTo(left);
        _candRight.AsSpan(0, _candRight.Length).CopyTo(right);
        arena.Return(_candLeft);
        arena.Return(_candRight);
        arena.Return(_keep);
        _candLeft = left;
        _candRight = right;
        _keep = keep;
    }
}

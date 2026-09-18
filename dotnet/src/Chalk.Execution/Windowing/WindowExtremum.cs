using Chalk.Catalog;
using Chalk.Execution.Aggregation;
using Chalk.Ir;
using Chalk.Sources;

namespace Chalk.Execution.Windowing;

/// <summary>
/// <c>MIN</c> and <c>MAX</c> over a frame — and therefore <c>EVERY</c> and <c>SOME</c>, which Calcite
/// spells as MIN and MAX over a boolean.
///
/// <para>
/// Neither is invertible, so a sliding accumulator cannot remove a row that leaves the frame. Two
/// shapes cover every frame (§4): when the lower bound is UNBOUNDED PRECEDING nothing ever leaves, so
/// a running extremum is enough; otherwise a <b>monotonic deque</b> of candidate rows, each better
/// than everything after it, whose front is the answer. Both bounds only move forward, so the deque
/// is amortised O(1) per row.
/// </para>
/// <para>
/// An <c>EXCLUDE</c> other than NO OTHERS drops the sliding structure and recomputes the frame,
/// which is O(frame) per row and is what §4 says it costs.
/// </para>
/// </summary>
internal sealed class WindowExtremumEvaluator : WindowRowEvaluator
{
    private readonly bool _max;
    private readonly WindowExtremumTree _tree;
    private int[] _deque = [];
    private ExecutionArena? _arena;

    public WindowExtremumEvaluator(ChalkType resultType, int valueColumn, bool max)
        : base(resultType, valueColumn, defaultValue: null)
    {
        _max = max;
        _tree = new WindowExtremumTree(max);
    }

    public override void Begin(ExecutionArena arena, int rows)
    {
        base.Begin(arena, rows);
        _deque = arena.Rent<int>(Math.Max(rows, 1));
        _arena = arena;
    }

    public override void Release(ExecutionArena arena)
    {
        _tree.Release(arena);
        arena.Return(_deque);
        _deque = [];
        _arena = null;
        base.Release(arena);
    }

    public override void Compute(WindowRun run, int start, int end)
    {
        var column = run.Columns[ValueColumn];
        if (!run.NoExclusion || WindowFrames.ForceSegmentTree)
        {
            // D59: the general frame engine, in place of step 18's O(frame) recomputation.
            ThroughTree(run, column, start, end);
            return;
        }

        var head = 0;
        var tail = 0;
        var filled = start - 1;

        for (var i = start; i < end; i++)
        {
            var lo = run.FrameLo[i];
            var hi = run.FrameHi[i];
            if (lo > hi)
            {
                Source[i] = NullRow;
                continue;
            }

            if (lo > filled)
            {
                // The frame jumped clear of everything the deque holds.
                head = 0;
                tail = 0;
                filled = lo - 1;
            }

            while (filled < hi)
            {
                filled++;
                if (!column.IsValid(filled))
                {
                    continue;
                }

                while (tail > head && !Better(column, _deque[tail - 1], filled))
                {
                    tail--;
                }

                _deque[tail++] = filled;
            }

            while (head < tail && _deque[head] < lo)
            {
                head++;
            }

            Source[i] = head < tail ? _deque[head] : NullRow;
        }
    }

    /// <summary>The O(frame) path an EXCLUDE forces, and the only one that has to skip rows.</summary>
    /// <summary>Every frame answered through the extremum segment tree of D59.</summary>
    private void ThroughTree(WindowRun run, WindowColumn column, int start, int end)
    {
        var arena = _arena
            ?? throw new InvalidOperationException("the window extremum is not attached to an arena.");
        _tree.Attach(column);
        _tree.Build(arena, start, end);

        for (var i = start; i < end; i++)
        {
            var lo = run.FrameLo[i];
            var hi = run.FrameHi[i];
            _tree.Reset();
            if (hi >= lo)
            {
                switch (run.Exclusion)
                {
                    case FrameExclusion.CurrentRow:
                        _tree.QueryExcluding(lo, hi, i, i);
                        break;
                    case FrameExclusion.Group:
                        _tree.QueryExcluding(lo, hi, run.PeerStart[i], run.PeerEnd[i] - 1);
                        break;
                    case FrameExclusion.Ties:
                        _tree.QueryExcluding(lo, hi, run.PeerStart[i], run.PeerEnd[i] - 1);
                        if (i >= lo && i <= hi)
                        {
                            _tree.Query(i, i);
                        }

                        break;
                    default:
                        _tree.Query(lo, hi);
                        break;
                }
            }

            Source[i] = _tree.Winner < 0 ? NullRow : _tree.Winner;
        }
    }

    private void Recompute(WindowRun run, WindowColumn column, int start, int end)
    {
        Span<byte> best = stackalloc byte[16];
        Span<byte> candidate = stackalloc byte[16];
        for (var i = start; i < end; i++)
        {
            var winner = NullRow;
            for (var r = run.FrameLo[i]; r <= run.FrameHi[i]; r++)
            {
                if (run.Excluded(i, r) || !column.IsValid(r))
                {
                    continue;
                }

                if (winner < 0)
                {
                    winner = r;
                    continue;
                }

                var comparison = LaneComparer.Compare(
                    column.Kind, column.Lane(r, candidate), column.Lane(winner, best));
                if (_max ? comparison > 0 : comparison < 0)
                {
                    winner = r;
                }
            }

            Source[i] = winner;
        }
    }

    /// <summary>
    /// Whether <paramref name="held"/> should stay in the deque when <paramref name="arriving"/>
    /// joins it: only if it is strictly better. An equal row is dropped, which keeps the deque short;
    /// which of two equal rows the front names cannot change the value the call reports.
    /// </summary>
    private bool Better(WindowColumn column, int held, int arriving)
    {
        Span<byte> left = stackalloc byte[16];
        Span<byte> right = stackalloc byte[16];
        var comparison = LaneComparer.Compare(
            column.Kind, column.Lane(held, left), column.Lane(arriving, right));
        return _max ? comparison > 0 : comparison < 0;
    }
}

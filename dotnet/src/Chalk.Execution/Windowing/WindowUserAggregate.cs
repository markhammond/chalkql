using Chalk.Catalog;
using Chalk.Execution.Expressions;
using Chalk.Sources;

namespace Chalk.Execution.Windowing;

/// <summary>
/// A client-bodied aggregate over a frame (D80). Which algorithm runs is decided by what the host
/// declared: with <c>Remove</c> the state slides, and without it every frame is recomputed.
/// </summary>
/// <remarks>
/// <para>
/// The sliding path is the one <c>WindowAggregateEvaluator</c> uses for the built-ins: both bounds
/// only move forward, so a row that enters is added once and a row that leaves is removed once.
/// It applies when the host gave an inverse <em>and</em> the frame excludes nothing — an
/// <c>EXCLUDE</c> cuts a hole that a sliding state cannot express, so those frames are recomputed,
/// which is slower and gives the same answer (§5's recorded positive).
/// </para>
/// <para>
/// <c>Merge</c> is not used here. The segment tree of step 19 is built over the built-ins' own sum
/// state, and putting a host's <typeparamref name="TState"/> in its nodes is a larger change than
/// step 22 needs — recomputation already covers every frame the tree would. ADR 0021 records it.
/// </para>
/// </remarks>
internal sealed class WindowUserAggregateEvaluator<TState, TIn, TOut> : WindowValueEvaluator
    where TState : struct
{
    private readonly AggregateSpec<TState, TIn, TOut> _spec;
    private readonly int _valueColumn;

    public WindowUserAggregateEvaluator(
        ChalkType resultType, AggregateSpec<TState, TIn, TOut> spec, int valueColumn)
        : base(resultType)
    {
        _spec = spec;
        _valueColumn = valueColumn;
    }

    public override void Compute(WindowRun run, int start, int end)
    {
        var column = run.Columns[_valueColumn];
        if (_spec.Remove is null || !run.NoExclusion)
        {
            Recompute(run, column, start, end);
            return;
        }

        var state = _spec.Init();
        var low = start;
        var high = start - 1;
        for (var i = start; i < end; i++)
        {
            var lo = run.FrameLo[i];
            var hi = run.FrameHi[i];
            if (lo > hi)
            {
                state = _spec.Init();
                low = lo;
                high = lo - 1;
                Write(i, state);
                continue;
            }

            if (low > high || lo > high || hi < low)
            {
                state = _spec.Init();
                for (var r = lo; r <= hi; r++)
                {
                    AddRow(ref state, column, r);
                }
            }
            else
            {
                for (var r = low; r < lo; r++)
                {
                    RemoveRow(ref state, column, r);
                }

                for (var r = high + 1; r <= hi; r++)
                {
                    AddRow(ref state, column, r);
                }
            }

            low = lo;
            high = hi;
            Write(i, state);
        }
    }

    private void Recompute(WindowRun run, WindowColumn column, int start, int end)
    {
        for (var i = start; i < end; i++)
        {
            var state = _spec.Init();
            var lo = run.FrameLo[i];
            var hi = run.FrameHi[i];
            for (var r = lo; r <= hi; r++)
            {
                if (!run.Excluded(i, r))
                {
                    AddRow(ref state, column, r);
                }
            }

            Write(i, state);
        }
    }

    private void AddRow(ref TState state, WindowColumn column, int row)
    {
        if (column.IsValid(row))
        {
            _spec.Add(ref state, LaneCodec.Read<TIn>(column.View, row));
        }
    }

    private void RemoveRow(ref TState state, WindowColumn column, int row)
    {
        if (column.IsValid(row))
        {
            _spec.Remove!(ref state, LaneCodec.Read<TIn>(column.View, row));
        }
    }

    private void Write(int row, TState state) =>
        Valid[row] = LaneCodec.WriteRaw(Lane(row), _spec.Finish(state));
}

/// <summary>Builds the frame evaluator for one registered aggregate, types known statically.</summary>
internal sealed class WindowUserAggregateFactory : IHostAggregateVisitor<WindowCallEvaluator>
{
    private readonly ChalkType _resultType;
    private readonly int _valueColumn;

    public WindowUserAggregateFactory(ChalkType resultType, int valueColumn)
    {
        _resultType = resultType;
        _valueColumn = valueColumn;
    }

    public WindowCallEvaluator Visit<TState, TIn, TOut>(AggregateSpec<TState, TIn, TOut> spec)
        where TState : struct =>
        new WindowUserAggregateEvaluator<TState, TIn, TOut>(_resultType, spec, _valueColumn);
}

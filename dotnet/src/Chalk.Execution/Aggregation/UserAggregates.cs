using Chalk.Catalog;
using Chalk.Execution.Expressions;
using Chalk.Execution.Vectors;
using Chalk.Sources;

namespace Chalk.Execution.Aggregation;

/// <summary>
/// A client-bodied aggregate over groups (D80): the host's state machine, with one
/// <typeparamref name="TState"/> per group in an arena-owned array.
/// </summary>
/// <remarks>
/// NULL arguments never reach <c>Add</c>, which is what every built-in aggregate does and what makes
/// <c>Finish</c>'s answer for a group of nothing but NULLs the same as for an empty one. The state
/// array is grown through <see cref="ArenaScratch.Grow{T}"/>, so it lives in the execution's arena
/// and accumulation allocates nothing.
/// </remarks>
internal sealed class UserAggregateAccumulator<TState, TIn, TOut> : MeasureAccumulator
    where TState : struct
{
    private readonly AggregateSpec<TState, TIn, TOut> _spec;
    private TState[] _states = [];
    private int _initialised;

    public UserAggregateAccumulator(ChalkType resultType, AggregateSpec<TState, TIn, TOut> spec)
        : base(resultType) => _spec = spec;

    public override void EnsureCapacity(int groups)
    {
        if (_states.Length < groups)
        {
            _states = Grow(_states, groups);
        }

        // `Grow` zeroes what it adds, and the host's identity need not be zero, so every new slot is
        // seeded from Init rather than assumed.
        for (; _initialised < groups; _initialised++)
        {
            _states[_initialised] = _spec.Init();
        }
    }

    protected override void ReleaseCore()
    {
        Give(ref _states);
        _initialised = 0;
    }

    public override void Add(int group, ReadOnlySpan<byte> lane, bool valid)
    {
        if (!valid)
        {
            return;
        }

        _spec.Add(ref _states[group], LaneCodec.ReadRaw<TIn>(lane));
    }

    public override void Emit(ColumnCopier copier, int group)
    {
        var state = group < _initialised ? _states[group] : _spec.Init();
        Span<byte> lane = stackalloc byte[ResultWidth];
        var valid = LaneCodec.WriteRaw(lane, _spec.Finish(state));
        copier.AppendRaw(lane, valid);
    }
}

/// <summary>Builds the accumulator for one registered aggregate, with its types known statically.</summary>
internal sealed class UserAggregateFactory : IHostAggregateVisitor<MeasureAccumulator>
{
    private readonly ChalkType _resultType;

    public UserAggregateFactory(ChalkType resultType) => _resultType = resultType;

    public MeasureAccumulator Visit<TState, TIn, TOut>(AggregateSpec<TState, TIn, TOut> spec)
        where TState : struct =>
        new UserAggregateAccumulator<TState, TIn, TOut>(_resultType, spec);
}

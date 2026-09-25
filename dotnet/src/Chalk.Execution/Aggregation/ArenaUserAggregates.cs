using System.Runtime.CompilerServices;
using Chalk.Catalog;
using Chalk.Execution.Expressions;
using Chalk.Execution.Vectors;
using Chalk.Sources;

namespace Chalk.Execution.Aggregation;

/// <summary>
/// A client-bodied aggregate with an arena state over groups (D305): one <typeparamref name="TState"/>
/// per group in an arena-owned array, as <see cref="UserAggregateAccumulator{TState, TIn, TOut}"/> keeps
/// it, plus one <see cref="ArenaStore"/> over the execution arena's pool that the states rent
/// variable-length data from. A span
/// answered is copied into the measure column as the group is emitted.
/// </summary>
internal sealed class ArenaUserAggregateAccumulator<TState, TIn, TOut> : MeasureAccumulator
    where TState : struct
    where TIn : allows ref struct
    where TOut : allows ref struct
{
    private readonly ArenaAggregateSpec<TState, TIn, TOut> _spec;
    private readonly ArenaStore _store = new();
    private readonly CompositeEmitter<TOut>? _composite;
    private readonly LaneFormat _input;
    private readonly LaneFormat _result;
    private readonly string _name;
    private readonly bool _text;
    private TState[] _states = [];
    private int _initialised;

    public ArenaUserAggregateAccumulator(
        ChalkType inputType, ChalkType resultType, string name, ArenaAggregateSpec<TState, TIn, TOut> spec)
        : base(resultType)
    {
        _spec = spec;
        _name = name;
        _input = new LaneFormat(inputType, $"the input of '{name}'");
        _result = new LaneFormat(resultType, $"the result of '{name}'");
        _text = resultType.Kind == Ir.TypeKind.String;
        _composite = resultType.Kind == Ir.TypeKind.Composite
            ? CompositeEmitters.For<TOut>(resultType, $"the result of '{name}'")
            : null;
    }

    protected override void BeginCore(ExecutionArena arena) => _store.Begin(arena.Pool);

    public override void EnsureCapacity(int groups)
    {
        if (_states.Length < groups)
        {
            _states = Grow(_states, groups);
        }

        for (; _initialised < groups; _initialised++)
        {
            var scope = new ArenaScope(_store);
            _states[_initialised] = _spec.Init(ref scope);
        }
    }

    protected override void ReleaseCore()
    {
        Give(ref _states);
        _initialised = 0;
        _store.Release();
    }

    public override void Add(int group, ReadOnlySpan<byte> lane, bool valid)
    {
        if (!valid)
        {
            return;
        }

        var scope = new ArenaScope(_store);
        _spec.Add(ref _states[group], LaneCodec.ReadRaw<TIn>(lane, in _input), ref scope);
    }

    public override void Emit(ColumnCopier copier, int group)
    {
        var scope = new ArenaScope(_store);
        var state = group < _initialised ? _states[group] : _spec.Init(ref scope);
        if (_spec.HasValue is { } hasValue && !hasValue(in state))
        {
            EmitNull(copier);
            return;
        }

        if (typeof(TOut) == typeof(ReadOnlySpan<byte>))
        {
            // A span over the scope, or over the input: copied into the column here, and validated
            // where the column is text, as a Tier 1 scalar's answer is.
            var answer = _spec.Finish(in state, ref scope);
            var bytes = Unsafe.As<TOut, ReadOnlySpan<byte>>(ref answer);
            if (_text && !Utf8String.IsValidUtf8(bytes))
            {
                throw new InvalidUtf8Exception($"the result of '{_name}'", group);
            }

            copier.AppendRaw(bytes, valid: true);
            return;
        }

        if (_composite is not null)
        {
            _composite.Emit(copier, _spec.Finish(in state, ref scope));
            return;
        }

        Span<byte> lane = stackalloc byte[ResultWidth];
        var written = LaneCodec.WriteRaw(lane, _spec.Finish(in state, ref scope), in _result);
        copier.AppendRaw(lane, written);
    }

    private void EmitNull(ColumnCopier copier)
    {
        if (_composite is not null)
        {
            copier.AppendNulls(1);
            return;
        }

        Span<byte> empty = stackalloc byte[ColumnKinds.IsVariableLength(ResultKind) ? 0 : ResultWidth];
        copier.AppendRaw(empty, valid: false);
    }
}

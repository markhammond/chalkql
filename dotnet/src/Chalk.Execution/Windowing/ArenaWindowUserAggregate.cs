using System.Runtime.CompilerServices;
using Chalk.Catalog;
using Chalk.Execution.Aggregation;
using Chalk.Execution.Expressions;
using Chalk.Execution.Vectors;
using Chalk.Sources;

namespace Chalk.Execution.Windowing;

/// <summary>
/// A client-bodied aggregate with an arena state over a frame (D305), the shape of
/// <see cref="WindowUserAggregateEvaluator{TState, TIn, TOut}"/>: a sliding state where the host
/// declares <c>Remove</c>, a recomputation otherwise, each recomputed frame starting from an empty
/// scope since the previous frame's answer has already been written out. A span or record answer is
/// appended to a column of its own in row order and emitted from it.
/// </summary>
internal sealed class ArenaWindowUserAggregateEvaluator<TState, TIn, TOut> : WindowValueEvaluator
    where TState : struct
    where TIn : allows ref struct
    where TOut : allows ref struct
{
    private readonly ArenaAggregateSpec<TState, TIn, TOut> _spec;
    private readonly ExecutionArenaStore _store = new();
    private readonly int _valueColumn;
    private readonly CompositeEmitter<TOut>? _composite;
    private readonly ColumnCopier? _appended;
    private readonly LaneFormat _input;
    private readonly LaneFormat _result;
    private readonly string _name;
    private readonly bool _text;
    private ColumnView _appendedView;
    private bool _appendedFinished;
    private int _written;

    public ArenaWindowUserAggregateEvaluator(
        ChalkType inputType,
        ChalkType resultType,
        string name,
        ArenaAggregateSpec<TState, TIn, TOut> spec,
        int valueColumn)
        : base(resultType)
    {
        _spec = spec;
        _name = name;
        _valueColumn = valueColumn;
        _input = new LaneFormat(inputType, $"the input of '{name}'");
        _result = new LaneFormat(resultType, $"the result of '{name}'");
        _text = resultType.Kind == Ir.TypeKind.String;
        if (resultType.Kind == Ir.TypeKind.Composite)
        {
            _composite = CompositeEmitters.For<TOut>(resultType, $"the result of '{name}'");
        }

        if (resultType.Kind == Ir.TypeKind.Composite || typeof(TOut) == typeof(ReadOnlySpan<byte>))
        {
            _appended = new ColumnCopier(resultType);
        }
    }

    public override void Begin(ExecutionArena arena, int rows)
    {
        base.Begin(arena, rows);
        _store.Begin(arena);
        if (_appended is not null)
        {
            _appended.Begin(rows);
            _appendedFinished = false;
            _written = 0;
        }
    }

    public override void Release(ExecutionArena arena)
    {
        _store.Release();
        base.Release(arena);
    }

    public override void Emit(ColumnCopier copier, WindowRun run, int row)
    {
        if (_appended is null)
        {
            base.Emit(copier, run, row);
            return;
        }

        if (!_appendedFinished)
        {
            _appendedView = _appended.FinishView();
            _appendedFinished = true;
        }

        copier.AppendRow(_appendedView, row);
    }

    public override void Compute(WindowRun run, int start, int end)
    {
        var column = run.Columns[_valueColumn];
        if (_spec.Remove is null || !run.NoExclusion)
        {
            Recompute(run, column, start, end);
            return;
        }

        var scope = new ArenaScope(_store);
        var state = _spec.Init(ref scope);
        var low = start;
        var high = start - 1;
        for (var i = start; i < end; i++)
        {
            var lo = run.FrameLo[i];
            var hi = run.FrameHi[i];
            if (lo > hi)
            {
                state = _spec.Init(ref scope);
                low = lo;
                high = lo - 1;
                Write(i, in state);
                continue;
            }

            if (low > high || lo > high || hi < low)
            {
                state = _spec.Init(ref scope);
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
            Write(i, in state);
        }
    }

    private void Recompute(WindowRun run, WindowColumn column, int start, int end)
    {
        for (var i = start; i < end; i++)
        {
            // The previous frame's answer is written; nothing references its buffers any more.
            _store.Reset();
            var scope = new ArenaScope(_store);
            var state = _spec.Init(ref scope);
            var lo = run.FrameLo[i];
            var hi = run.FrameHi[i];
            for (var r = lo; r <= hi; r++)
            {
                if (!run.Excluded(i, r))
                {
                    AddRow(ref state, column, r);
                }
            }

            Write(i, in state);
        }
    }

    private void AddRow(ref TState state, WindowColumn column, int row)
    {
        if (column.IsValid(row))
        {
            // The view in a local, and the scope declared `scoped` to the same block: a span read from
            // the view may then be passed beside the scope, which the compiler otherwise takes for a
            // way to smuggle a block-scoped span into a method-scoped ref struct.
            var view = column.View;
            scoped var scope = new ArenaScope(_store);
            _spec.Add(ref state, LaneCodec.Read<TIn>(in view, row, in _input), ref scope);
        }
    }

    private void RemoveRow(ref TState state, WindowColumn column, int row)
    {
        if (column.IsValid(row))
        {
            var view = column.View;
            scoped var scope = new ArenaScope(_store);
            _spec.Remove!(ref state, LaneCodec.Read<TIn>(in view, row, in _input), ref scope);
        }
    }

    private void Write(int row, in TState state)
    {
        var scope = new ArenaScope(_store);
        var hasValue = _spec.HasValue is not { } test || test(in state);
        if (_appended is null)
        {
            if (!hasValue)
            {
                Valid[row] = false;
                return;
            }

            Valid[row] = LaneCodec.WriteRaw(Lane(row), _spec.Finish(in state, ref scope), in _result);
            return;
        }

        // The appended column is only right if rows arrive in order; the operator computes its
        // partitions front to back and each one's rows in order, and this says so.
        if (row != _written)
        {
            throw new InvalidOperationException(
                $"a window aggregate with an arena state computed row {row} after {_written} rows; its "
                + "frames are written in row order.");
        }

        _written++;
        if (!hasValue)
        {
            if (_composite is not null)
            {
                _appended.AppendNulls(1);
            }
            else
            {
                _appended.AppendRaw(default, valid: false);
            }

            return;
        }

        if (_composite is not null)
        {
            _composite.Emit(_appended, _spec.Finish(in state, ref scope));
            return;
        }

        var answer = _spec.Finish(in state, ref scope);
        var bytes = Unsafe.As<TOut, ReadOnlySpan<byte>>(ref answer);
        if (_text && !Utf8String.IsValidUtf8(bytes))
        {
            throw new InvalidUtf8Exception($"the result of '{_name}'", row);
        }

        _appended.AppendRaw(bytes, valid: true);
    }
}

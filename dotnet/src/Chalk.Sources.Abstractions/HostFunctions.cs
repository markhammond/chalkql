namespace Chalk.Sources;

/// <summary>
/// What a host registered for one client-bodied function, in the shape the engine consumes
/// (D79, D80). Internal: hosts declare through <c>IFunctionRegistry</c> in <c>Chalk.Client</c>, and
/// the engine reads these; neither is part of the public surface.
/// </summary>
/// <remarks>
/// Each kind carries its CLR type arguments as real generic parameters rather than
/// <see cref="Type"/> objects, and hands them to the engine through a visitor. That is what lets the
/// lane loops be generic — and therefore allocation-free — without a line of reflection.
/// </remarks>
internal abstract class HostFunction
{
    protected HostFunction(string name) => Name = name;

    /// <summary>The registration key: <c>ClientBody.registration</c>, or the function's own name.</summary>
    public string Name { get; }
}

/// <summary>A Tier 1 scalar: a delegate of one to six arguments.</summary>
internal abstract class HostScalar : HostFunction
{
    protected HostScalar(string name)
        : base(name)
    {
    }

    /// <summary>How many arguments the delegate takes.</summary>
    public abstract int Arity { get; }

    /// <summary>The delegate's parameter and return CLR types, for the signature check.</summary>
    public abstract IReadOnlyList<Type> ParameterTypes { get; }

    public abstract Type ReturnType { get; }

    /// <summary>Hands the delegate to <paramref name="visitor"/> with its types known statically.</summary>
    public abstract TResult Accept<TResult>(IHostScalarVisitor<TResult> visitor);

    /// <summary>
    /// Calls the delegate with boxed arguments. This is the reference executor's path, which works in
    /// boxed values throughout (D13): the <em>same</em> delegate runs under both engines, so the
    /// differential test compares Chalk's plumbing rather than the host's arithmetic.
    /// </summary>
    public abstract object? InvokeBoxed(object?[] args);

    /// <summary>One boxed argument as the delegate's own type; see <see cref="BoxedLanes.Cast{T}"/>.</summary>
    private protected static T Cast<T>(object? value)
        where T : allows ref struct
        => BoxedLanes.Cast<T>(value);

    /// <summary>The delegate's answer boxed; see <see cref="BoxedLanes.Box{TOut}"/>.</summary>
    private protected static object? Box<TOut>(TOut value)
        where TOut : allows ref struct
        => BoxedLanes.Box(value);
}

/// <summary>Receives a registered delegate with its CLR types intact.</summary>
internal interface IHostScalarVisitor<out TResult>
{
    TResult Visit<TOut>(Func<TOut> f)
        where TOut : allows ref struct;

    TResult Visit<T1, TOut>(Func<T1, TOut> f)
        where T1 : allows ref struct
        where TOut : allows ref struct;

    TResult Visit<T1, T2, TOut>(Func<T1, T2, TOut> f)
        where T1 : allows ref struct
        where T2 : allows ref struct
        where TOut : allows ref struct;

    TResult Visit<T1, T2, T3, TOut>(Func<T1, T2, T3, TOut> f)
        where T1 : allows ref struct
        where T2 : allows ref struct
        where T3 : allows ref struct
        where TOut : allows ref struct;

    TResult Visit<T1, T2, T3, T4, TOut>(Func<T1, T2, T3, T4, TOut> f)
        where T1 : allows ref struct
        where T2 : allows ref struct
        where T3 : allows ref struct
        where T4 : allows ref struct
        where TOut : allows ref struct;

    TResult Visit<T1, T2, T3, T4, T5, TOut>(Func<T1, T2, T3, T4, T5, TOut> f)
        where T1 : allows ref struct
        where T2 : allows ref struct
        where T3 : allows ref struct
        where T4 : allows ref struct
        where T5 : allows ref struct
        where TOut : allows ref struct;

    TResult Visit<T1, T2, T3, T4, T5, T6, TOut>(Func<T1, T2, T3, T4, T5, T6, TOut> f)
        where T1 : allows ref struct
        where T2 : allows ref struct
        where T3 : allows ref struct
        where T4 : allows ref struct
        where T5 : allows ref struct
        where T6 : allows ref struct
        where TOut : allows ref struct;
}

internal sealed class HostScalar0<TOut> : HostScalar
    where TOut : allows ref struct
{
    private readonly Func<TOut> _f;

    public HostScalar0(string name, Func<TOut> f)
        : base(name) => _f = f;

    public override int Arity => 0;

    public override IReadOnlyList<Type> ParameterTypes => [];

    public override Type ReturnType => typeof(TOut);

    public override TResult Accept<TResult>(IHostScalarVisitor<TResult> visitor) => visitor.Visit(_f);

    public override object? InvokeBoxed(object?[] args) => Box(_f());
}

internal sealed class HostScalar1<T1, TOut> : HostScalar
    where T1 : allows ref struct
    where TOut : allows ref struct
{
    private readonly Func<T1, TOut> _f;

    public HostScalar1(string name, Func<T1, TOut> f)
        : base(name) => _f = f;

    public override int Arity => 1;

    public override IReadOnlyList<Type> ParameterTypes => [typeof(T1)];

    public override Type ReturnType => typeof(TOut);

    public override TResult Accept<TResult>(IHostScalarVisitor<TResult> visitor) => visitor.Visit(_f);

    public override object? InvokeBoxed(object?[] args) => Box(_f(Cast<T1>(args[0])));
}

internal sealed class HostScalar2<T1, T2, TOut> : HostScalar
    where T1 : allows ref struct
    where T2 : allows ref struct
    where TOut : allows ref struct
{
    private readonly Func<T1, T2, TOut> _f;

    public HostScalar2(string name, Func<T1, T2, TOut> f)
        : base(name) => _f = f;

    public override int Arity => 2;

    public override IReadOnlyList<Type> ParameterTypes => [typeof(T1), typeof(T2)];

    public override Type ReturnType => typeof(TOut);

    public override TResult Accept<TResult>(IHostScalarVisitor<TResult> visitor) => visitor.Visit(_f);

    public override object? InvokeBoxed(object?[] args) => Box(_f(Cast<T1>(args[0]), Cast<T2>(args[1])));
}

internal sealed class HostScalar3<T1, T2, T3, TOut> : HostScalar
    where T1 : allows ref struct
    where T2 : allows ref struct
    where T3 : allows ref struct
    where TOut : allows ref struct
{
    private readonly Func<T1, T2, T3, TOut> _f;

    public HostScalar3(string name, Func<T1, T2, T3, TOut> f)
        : base(name) => _f = f;

    public override int Arity => 3;

    public override IReadOnlyList<Type> ParameterTypes => [typeof(T1), typeof(T2), typeof(T3)];

    public override Type ReturnType => typeof(TOut);

    public override TResult Accept<TResult>(IHostScalarVisitor<TResult> visitor) => visitor.Visit(_f);

    public override object? InvokeBoxed(object?[] args) => Box(_f(Cast<T1>(args[0]), Cast<T2>(args[1]), Cast<T3>(args[2])));
}

internal sealed class HostScalar4<T1, T2, T3, T4, TOut> : HostScalar
    where T1 : allows ref struct
    where T2 : allows ref struct
    where T3 : allows ref struct
    where T4 : allows ref struct
    where TOut : allows ref struct
{
    private readonly Func<T1, T2, T3, T4, TOut> _f;

    public HostScalar4(string name, Func<T1, T2, T3, T4, TOut> f)
        : base(name) => _f = f;

    public override int Arity => 4;

    public override IReadOnlyList<Type> ParameterTypes => [typeof(T1), typeof(T2), typeof(T3), typeof(T4)];

    public override Type ReturnType => typeof(TOut);

    public override TResult Accept<TResult>(IHostScalarVisitor<TResult> visitor) => visitor.Visit(_f);

    public override object? InvokeBoxed(object?[] args) => Box(_f(Cast<T1>(args[0]), Cast<T2>(args[1]), Cast<T3>(args[2]), Cast<T4>(args[3])));
}

internal sealed class HostScalar5<T1, T2, T3, T4, T5, TOut> : HostScalar
    where T1 : allows ref struct
    where T2 : allows ref struct
    where T3 : allows ref struct
    where T4 : allows ref struct
    where T5 : allows ref struct
    where TOut : allows ref struct
{
    private readonly Func<T1, T2, T3, T4, T5, TOut> _f;

    public HostScalar5(string name, Func<T1, T2, T3, T4, T5, TOut> f)
        : base(name) => _f = f;

    public override int Arity => 5;

    public override IReadOnlyList<Type> ParameterTypes =>
        [typeof(T1), typeof(T2), typeof(T3), typeof(T4), typeof(T5)];

    public override Type ReturnType => typeof(TOut);

    public override TResult Accept<TResult>(IHostScalarVisitor<TResult> visitor) => visitor.Visit(_f);

    public override object? InvokeBoxed(object?[] args) => Box(_f(Cast<T1>(args[0]), Cast<T2>(args[1]), Cast<T3>(args[2]), Cast<T4>(args[3]), Cast<T5>(args[4])));
}

internal sealed class HostScalar6<T1, T2, T3, T4, T5, T6, TOut> : HostScalar
    where T1 : allows ref struct
    where T2 : allows ref struct
    where T3 : allows ref struct
    where T4 : allows ref struct
    where T5 : allows ref struct
    where T6 : allows ref struct
    where TOut : allows ref struct
{
    private readonly Func<T1, T2, T3, T4, T5, T6, TOut> _f;

    public HostScalar6(string name, Func<T1, T2, T3, T4, T5, T6, TOut> f)
        : base(name) => _f = f;

    public override int Arity => 6;

    public override IReadOnlyList<Type> ParameterTypes =>
        [typeof(T1), typeof(T2), typeof(T3), typeof(T4), typeof(T5), typeof(T6)];

    public override Type ReturnType => typeof(TOut);

    public override TResult Accept<TResult>(IHostScalarVisitor<TResult> visitor) => visitor.Visit(_f);

    public override object? InvokeBoxed(object?[] args) => Box(_f(Cast<T1>(args[0]), Cast<T2>(args[1]), Cast<T3>(args[2]), Cast<T4>(args[3]), Cast<T5>(args[4]), Cast<T6>(args[5])));
}

/// <summary>A Tier 1 aggregate: the five-step protocol of D80.</summary>
internal abstract class HostAggregate : HostFunction
{
    protected HostAggregate(string name)
        : base(name)
    {
    }

    /// <summary>Whether a sliding frame can be maintained rather than recomputed.</summary>
    public abstract bool HasRemove { get; }

    /// <summary>Whether the segment tree and partial aggregation apply.</summary>
    public abstract bool HasMerge { get; }

    /// <summary>The CLR type of the value being aggregated, for the signature check.</summary>
    public abstract Type InputType { get; }

    public abstract Type ResultType { get; }

    public abstract TResult Accept<TResult>(IHostAggregateVisitor<TResult> visitor);

    /// <summary>A fresh accumulator working in boxed values, for the reference executor (D13).</summary>
    public abstract IBoxedAggregate NewBoxed();
}

/// <summary>
/// The reference executor's view of an aggregate: add boxed values, ask for the answer. The state
/// machine behind it is the host's own, so both engines fold the same arithmetic.
/// </summary>
internal interface IBoxedAggregate
{
    void Add(object? value);

    object? Finish();
}

internal interface IHostAggregateVisitor<out TResult>
{
    TResult Visit<TState, TIn, TOut>(AggregateSpec<TState, TIn, TOut> spec)
        where TState : struct
        where TIn : allows ref struct;

    TResult Visit<TState, TIn, TOut>(ArenaAggregateSpec<TState, TIn, TOut> spec)
        where TState : struct
        where TIn : allows ref struct
        where TOut : allows ref struct;
}

internal sealed class HostAggregate<TState, TIn, TOut> : HostAggregate
    where TState : struct
    where TIn : allows ref struct
{
    private readonly AggregateSpec<TState, TIn, TOut> _spec;

    public HostAggregate(string name, AggregateSpec<TState, TIn, TOut> spec)
        : base(name) => _spec = spec;

    public override bool HasRemove => _spec.Remove is not null;

    public override bool HasMerge => _spec.Merge is not null;

    public override Type InputType => typeof(TIn);

    public override Type ResultType => typeof(TOut);

    public override TResult Accept<TResult>(IHostAggregateVisitor<TResult> visitor) => visitor.Visit(_spec);

    public override IBoxedAggregate NewBoxed() => new Boxed(_spec);

    private sealed class Boxed : IBoxedAggregate
    {
        private readonly AggregateSpec<TState, TIn, TOut> _spec;
        private TState _state;

        public Boxed(AggregateSpec<TState, TIn, TOut> spec)
        {
            _spec = spec;
            _state = spec.Init();
        }

        public void Add(object? value)
        {
            if (value is not null)
            {
                _spec.Add(ref _state, BoxedLanes.Cast<TIn>(value));
            }
        }

        public object? Finish() => _spec.Finish(_state);
    }
}

/// <summary>An aggregate with an arena state (D305): the same engine shape, over the arena spec.</summary>
internal sealed class HostArenaAggregate<TState, TIn, TOut> : HostAggregate
    where TState : struct
    where TIn : allows ref struct
    where TOut : allows ref struct
{
    private readonly ArenaAggregateSpec<TState, TIn, TOut> _spec;

    public HostArenaAggregate(string name, ArenaAggregateSpec<TState, TIn, TOut> spec)
        : base(name) => _spec = spec;

    public override bool HasRemove => _spec.Remove is not null;

    public override bool HasMerge => _spec.Merge is not null;

    public override Type InputType => typeof(TIn);

    public override Type ResultType => typeof(TOut);

    public override TResult Accept<TResult>(IHostAggregateVisitor<TResult> visitor) => visitor.Visit(_spec);

    public override IBoxedAggregate NewBoxed() => new Boxed(_spec);

    /// <summary>The reference executor's accumulator: the same delegates over a heap-backed scope.</summary>
    private sealed class Boxed : IBoxedAggregate
    {
        private readonly ArenaAggregateSpec<TState, TIn, TOut> _spec;
        private readonly HeapArenaStore _store = new();
        private TState _state;

        public Boxed(ArenaAggregateSpec<TState, TIn, TOut> spec)
        {
            _spec = spec;
            var scope = new ArenaScope(_store);
            _state = spec.Init(ref scope);
        }

        public void Add(object? value)
        {
            if (value is not null)
            {
                var scope = new ArenaScope(_store);
                _spec.Add(ref _state, BoxedLanes.Cast<TIn>(value), ref scope);
            }
        }

        public object? Finish()
        {
            if (_spec.HasValue is { } hasValue && !hasValue(in _state))
            {
                return null;
            }

            var scope = new ArenaScope(_store);
            return BoxedLanes.Box(_spec.Finish(in _state, ref scope));
        }
    }
}

/// <summary>A Tier 1 table function: a producer of rows of a POCO type.</summary>
internal abstract class HostTable : HostFunction
{
    protected HostTable(string name)
        : base(name)
    {
    }

    /// <summary>The row type, inferred exactly as a POCO table's is.</summary>
    public abstract Type RowType { get; }

    /// <summary>The producer's own parameter types, for the signature check.</summary>
    public abstract IReadOnlyList<Type> ParameterTypes { get; }

    public abstract TResult Accept<TResult>(IHostTableVisitor<TResult> visitor);
}

internal interface IHostTableVisitor<out TResult>
{
    TResult Visit<TRow>(Delegate producer);
}

internal sealed class HostTable<TRow> : HostTable
{
    private readonly Delegate _producer;

    public HostTable(string name, Delegate producer)
        : base(name) => _producer = producer;

    public override Type RowType => typeof(TRow);

    public override IReadOnlyList<Type> ParameterTypes =>
        [.. _producer.Method.GetParameters().Select(p => p.ParameterType)];

    public override TResult Accept<TResult>(IHostTableVisitor<TResult> visitor) => visitor.Visit<TRow>(_producer);
}

/// <summary>A Tier 2 kernel, registered as it is.</summary>
internal sealed class HostKernel : HostFunction
{
#pragma warning disable CHALK001
    public HostKernel(string name, IVectorFunction kernel)
        : base(name) => Kernel = kernel;

    public IVectorFunction Kernel { get; }
#pragma warning restore CHALK001
}

/// <summary>
/// Every implementation a host registered, keyed by registration name. Built once per engine and
/// read by the executor; empty for an engine that declares no client-bodied function, which is
/// every engine before step 22.
/// </summary>
internal sealed class HostFunctionSet
{
    public static readonly HostFunctionSet Empty = new(new Dictionary<string, HostFunction>());

    private readonly IReadOnlyDictionary<string, HostFunction> _byName;

    public HostFunctionSet(IReadOnlyDictionary<string, HostFunction> byName) => _byName = byName;

    public int Count => _byName.Count;

    public HostFunction? Find(string name) =>
        _byName.TryGetValue(name, out var function) ? function : null;
}

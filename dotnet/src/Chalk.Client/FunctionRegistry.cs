using System.Diagnostics.CodeAnalysis;
using Chalk.Sources;

namespace Chalk.Client;

/// <summary>
/// Tier 1 of the public kernel interface (D79,
/// <c>docs/design/17-user-defined-functions.md</c> §3): the host writes ordinary delegates and Chalk
/// generates the lane loop.
/// </summary>
/// <remarks>
/// <para>
/// Registration is keyed by the catalog's <c>ClientBody.Registration</c> — the function's own name
/// unless the descriptor says otherwise — and every signature is checked against the descriptor when
/// the engine is created, so a delegate whose CLR types do not match fails before any query runs.
/// </para>
/// <para>
/// Nothing here allocates per row: Chalk reads the lanes through column views, calls the delegate,
/// and writes the result and its validity. A <c>STRICT</c> function's NULL lanes are skipped before
/// the call. If the delegate itself allocates, so does the query — which is the one thing the host
/// controls and Chalk cannot.
/// </para>
/// <para>
/// <b>Text and bytes.</b> A STRING parameter is spelled <c>ReadOnlySpan&lt;byte&gt;</c>, a span over
/// the lane itself that the compiler keeps inside the call, or <c>string</c>, which is decoded and
/// allocated per row; a BINARY parameter <c>ReadOnlySpan&lt;byte&gt;</c> or <c>byte[]</c>. A delegate
/// never receives a <see cref="Utf8String"/> or a <c>ReadOnlyMemory&lt;byte&gt;</c> over a lane —
/// a storable value over memory the engine reuses — and registering one for a parameter is refused
/// when the engine is created. A result may be a <c>ReadOnlySpan&lt;byte&gt;</c> — a slice of the
/// input, or of a buffer the host owns — copied into the result column before the next call, a
/// <see cref="Utf8String"/> or <c>ReadOnlyMemory&lt;byte&gt;</c> over the host's own memory, or a
/// <c>string</c> or <c>byte[]</c>. A span has no NULL, so a non-strict function, which sees every
/// NULL, spells a STRING parameter <c>string?</c>.
/// </para>
/// </remarks>
public interface IFunctionRegistry
{
    /// <summary>
    /// A scalar function of no arguments — a clock, a sequence, a session value. Only a
    /// <c>STABLE</c> or <c>VOLATILE</c> one is useful: an immutable function of nothing is a
    /// constant, and the planner would fold it if it could.
    /// </summary>
    void AddScalar<TResult>(string name, Func<TResult> f)
        where TResult : allows ref struct;

    /// <summary>A one-argument scalar function.</summary>
    void AddScalar<T1, TResult>(string name, Func<T1, TResult> f)
        where T1 : allows ref struct
        where TResult : allows ref struct;

    /// <summary>A two-argument scalar function.</summary>
    void AddScalar<T1, T2, TResult>(string name, Func<T1, T2, TResult> f)
        where T1 : allows ref struct
        where T2 : allows ref struct
        where TResult : allows ref struct;

    /// <summary>A three-argument scalar function.</summary>
    void AddScalar<T1, T2, T3, TResult>(string name, Func<T1, T2, T3, TResult> f)
        where T1 : allows ref struct
        where T2 : allows ref struct
        where T3 : allows ref struct
        where TResult : allows ref struct;

    /// <summary>A four-argument scalar function.</summary>
    void AddScalar<T1, T2, T3, T4, TResult>(string name, Func<T1, T2, T3, T4, TResult> f)
        where T1 : allows ref struct
        where T2 : allows ref struct
        where T3 : allows ref struct
        where T4 : allows ref struct
        where TResult : allows ref struct;

    /// <summary>A five-argument scalar function.</summary>
    void AddScalar<T1, T2, T3, T4, T5, TResult>(string name, Func<T1, T2, T3, T4, T5, TResult> f)
        where T1 : allows ref struct
        where T2 : allows ref struct
        where T3 : allows ref struct
        where T4 : allows ref struct
        where T5 : allows ref struct
        where TResult : allows ref struct;

    /// <summary>A six-argument scalar function, which is as many as Tier 1 takes.</summary>
    void AddScalar<T1, T2, T3, T4, T5, T6, TResult>(string name, Func<T1, T2, T3, T4, T5, T6, TResult> f)
        where T1 : allows ref struct
        where T2 : allows ref struct
        where T3 : allows ref struct
        where T4 : allows ref struct
        where T5 : allows ref struct
        where T6 : allows ref struct
        where TResult : allows ref struct;

    /// <summary>An aggregate, as the state machine of D80.</summary>
    void AddAggregate<TState, TIn, TOut>(string name, AggregateSpec<TState, TIn, TOut> spec)
        where TState : struct;

    /// <summary>
    /// A table function: a delegate <c>(args…) =&gt; IEnumerable&lt;TRow&gt;</c> whose row type is
    /// inferred exactly as a POCO table's is.
    /// </summary>
    void AddTable<TRow>(string name, Delegate producer);

    /// <summary>A Tier 2 kernel, for a host that wants the whole batch.</summary>
    [Experimental("CHALK001")]
    void AddKernel(string name, IVectorFunction kernel);
}

/// <summary>
/// The registry <c>ChalkEngineOptions.Functions</c> is handed. One instance per engine; nothing is
/// mutable once <c>CreateAsync</c> has read it.
/// </summary>
public sealed class FunctionRegistry : IFunctionRegistry
{
    private readonly Dictionary<string, HostFunction> _functions =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The names registered so far, for a host that wants to check its own wiring.</summary>
    public IReadOnlyCollection<string> Names => _functions.Keys;

    /// <summary>Whether something is registered under <paramref name="name"/>.</summary>
    public bool Contains(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _functions.ContainsKey(name);
    }

    public void AddScalar<TResult>(string name, Func<TResult> f)
        where TResult : allows ref struct =>
        Add(new HostScalar0<TResult>(Key(name), Required(f)));

    public void AddScalar<T1, TResult>(string name, Func<T1, TResult> f)
        where T1 : allows ref struct
        where TResult : allows ref struct =>
        Add(new HostScalar1<T1, TResult>(Key(name), Required(f)));

    public void AddScalar<T1, T2, TResult>(string name, Func<T1, T2, TResult> f)
        where T1 : allows ref struct
        where T2 : allows ref struct
        where TResult : allows ref struct =>
        Add(new HostScalar2<T1, T2, TResult>(Key(name), Required(f)));

    public void AddScalar<T1, T2, T3, TResult>(string name, Func<T1, T2, T3, TResult> f)
        where T1 : allows ref struct
        where T2 : allows ref struct
        where T3 : allows ref struct
        where TResult : allows ref struct =>
        Add(new HostScalar3<T1, T2, T3, TResult>(Key(name), Required(f)));

    public void AddScalar<T1, T2, T3, T4, TResult>(string name, Func<T1, T2, T3, T4, TResult> f)
        where T1 : allows ref struct
        where T2 : allows ref struct
        where T3 : allows ref struct
        where T4 : allows ref struct
        where TResult : allows ref struct =>
        Add(new HostScalar4<T1, T2, T3, T4, TResult>(Key(name), Required(f)));

    public void AddScalar<T1, T2, T3, T4, T5, TResult>(
        string name, Func<T1, T2, T3, T4, T5, TResult> f)
        where T1 : allows ref struct
        where T2 : allows ref struct
        where T3 : allows ref struct
        where T4 : allows ref struct
        where T5 : allows ref struct
        where TResult : allows ref struct =>
        Add(new HostScalar5<T1, T2, T3, T4, T5, TResult>(Key(name), Required(f)));

    public void AddScalar<T1, T2, T3, T4, T5, T6, TResult>(
        string name, Func<T1, T2, T3, T4, T5, T6, TResult> f)
        where T1 : allows ref struct
        where T2 : allows ref struct
        where T3 : allows ref struct
        where T4 : allows ref struct
        where T5 : allows ref struct
        where T6 : allows ref struct
        where TResult : allows ref struct =>
        Add(new HostScalar6<T1, T2, T3, T4, T5, T6, TResult>(Key(name), Required(f)));

    public void AddAggregate<TState, TIn, TOut>(string name, AggregateSpec<TState, TIn, TOut> spec)
        where TState : struct =>
        Add(new HostAggregate<TState, TIn, TOut>(Key(name), Required(spec)));

    public void AddTable<TRow>(string name, Delegate producer) =>
        Add(new HostTable<TRow>(Key(name), Required(producer)));

    [Experimental("CHALK001")]
    public void AddKernel(string name, IVectorFunction kernel) =>
        Add(new HostKernel(Key(name), Required(kernel)));

    /// <summary>What the engine reads. Internal: the shapes are the engine's, not the host's.</summary>
    internal HostFunctionSet Freeze() => new(_functions);

    private void Add(HostFunction function)
    {
        if (!_functions.TryAdd(function.Name, function))
        {
            throw new ArgumentException(
                $"A function is already registered as '{function.Name}'. Registration keys are "
                + "case-insensitive and one key means one implementation.",
                nameof(function));
        }
    }

    private static string Key(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return name;
    }

    private static T Required<T>(T value)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(value);
        return value;
    }
}

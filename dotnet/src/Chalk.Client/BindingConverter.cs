using System.Diagnostics.CodeAnalysis;
using Chalk.Catalog;

namespace Chalk.Client;

/// <summary>
/// A host's conversion of one of its own CLR types into a value Chalk binds (D323), registered on
/// <see cref="ChalkEngineOptions.BindingConverters"/>: a strongly typed identifier as the
/// <see cref="long"/> it wraps, a money type as its <see cref="decimal"/>, a 36-character string as the
/// <see cref="Guid"/> a UUID parameter takes.
/// </summary>
/// <remarks>
/// <para>
/// A converter is chosen by the bound value's exact runtime type, and asked only where the target
/// type is known: a statement's parameter at <c>ExecuteAsync</c>, and a context scalar or a context
/// binding's column whose type the host stated, or which a plan prepared with a shape declares. What
/// it returns is then held to the same rule as any other value (D317) — it binds only when the
/// target type holds it exactly — so a converter can widen what a host may bind but can never make a
/// lossy conversion silent. A converter that declines leaves the value to that rule as it stands.
/// </para>
/// <para>
/// A value whose type is read off the value itself — a parameter value hint, a context value with no
/// stated type — is not converted: there is no target to convert to. A host type there is refused,
/// asking for the type to be stated.
/// </para>
/// </remarks>
public abstract class BindingConverter
{
    /// <summary>
    /// For <see cref="BindingConverter{T}"/>, which is what a host derives from; what the engine calls
    /// is internal to this class, so nothing else can implement it.
    /// </summary>
    protected BindingConverter()
    {
    }

    /// <summary>The CLR type this converter is chosen for.</summary>
    public abstract Type ValueType { get; }

    /// <summary>
    /// A converter that turns every <typeparamref name="T"/> into what <paramref name="convert"/>
    /// returns, whatever the target — the shape of a strongly typed identifier.
    /// </summary>
    public static BindingConverter<T> From<T>(Func<T, object?> convert)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(convert);
        return new DelegateConverter<T>(convert);
    }

    internal abstract bool TryConvertValue(object value, ChalkType target, out object? converted);

    /// <summary>The converter's own type name, which a refusal names.</summary>
    public override string ToString() => GetType().Name;

    private sealed class DelegateConverter<T>(Func<T, object?> convert) : BindingConverter<T>
        where T : notnull
    {
        public override bool TryConvert(T value, ChalkType target, out object? converted)
        {
            converted = convert(value);
            return true;
        }

        public override string ToString() => $"BindingConverter.From<{typeof(T).Name}>";
    }
}

/// <summary>A conversion of <typeparamref name="T"/> into a value Chalk binds (D323).</summary>
/// <typeparam name="T">
/// The CLR type converted: a concrete type, matched exactly, so neither an interface, an abstract
/// class nor a <see cref="Nullable{T}"/> — a boxed nullable is its underlying type.
/// </typeparam>
public abstract class BindingConverter<T> : BindingConverter
    where T : notnull
{
    /// <inheritdoc />
    public sealed override Type ValueType => typeof(T);

    /// <summary>
    /// <paramref name="value"/> as a value of <paramref name="target"/>, or false to leave it as it is.
    /// </summary>
    /// <param name="value">The value bound.</param>
    /// <param name="target">The type it is bound to.</param>
    /// <param name="converted">
    /// The value to bind instead: one of the CLR types <paramref name="target"/> binds from, or null
    /// for SQL NULL, which a target that is not nullable refuses.
    /// </param>
    public abstract bool TryConvert(T value, ChalkType target, out object? converted);

    internal sealed override bool TryConvertValue(object value, ChalkType target, out object? converted) =>
        TryConvert((T)value, target, out converted);
}

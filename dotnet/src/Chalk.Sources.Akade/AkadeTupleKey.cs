using System.Linq.Expressions;
using System.Reflection;

namespace Chalk.Sources.Akade;

/// <summary>What counts as a compound key's shape, independently of the key type itself.</summary>
internal static class AkadeTupleKey
{
    /// <summary>
    /// The component types of a <c>ValueTuple</c> of two to four components, or null for anything
    /// else. Longer tuples nest their rest element, which is a key shape this adapter does not
    /// claim.
    /// </summary>
    public static Type[]? ComponentTypes(Type key)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (!key.IsValueType || !key.IsGenericType)
        {
            return null;
        }

        var definition = key.GetGenericTypeDefinition();
        var arguments = key.GetGenericArguments();

        var isTuple = (definition == typeof(ValueTuple<,>) && arguments.Length == 2)
            || (definition == typeof(ValueTuple<,,>) && arguments.Length == 3)
            || (definition == typeof(ValueTuple<,,,>) && arguments.Length == 4);

        return isTuple ? arguments : null;
    }
}

/// <summary>
/// What a compound Akade key can do for Chalk: turn a range's bounds into a tuple, and compare two
/// tuples in the index's own order (D280).
/// </summary>
/// <remarks>
/// Both are compiled once, when the index is built, and both are strongly typed all the way down:
/// the comparison the ordered guard makes per row reads the tuple's fields and calls each
/// component's own <see cref="IComparer{T}"/>, so nothing is boxed and nothing is allocated on the
/// row path.
/// </remarks>
internal sealed class AkadeTupleKey<TKey>
    where TKey : notnull
{
    private AkadeTupleKey(
        Func<IReadOnlyList<object?>, bool, TKey> fill,
        IComparer<TKey> comparer,
        int components)
    {
        _fill = fill;
        Comparer = comparer;
        Components = components;
    }

    private readonly Func<IReadOnlyList<object?>, bool, TKey> _fill;

    /// <summary>Bound values, and which extreme fills what they do not reach, to a key.</summary>
    public TKey Fill(IReadOnlyList<object?> bounds, bool useMaximum) => _fill(bounds, useMaximum);

    /// <summary>The key order: the components' Chalk orders, in key order.</summary>
    public IComparer<TKey> Comparer { get; }

    public int Components { get; }

    /// <summary>
    /// Builds the two for a <c>ValueTuple</c> key whose components are ordered by
    /// <paramref name="componentComparers"/> — one per component, in key order, each an
    /// <c>IComparer&lt;TComponent&gt;</c>.
    /// </summary>
    /// <param name="descending">
    /// Whether the index reads from the largest key down (D281). The extremes that fill the
    /// components a range does not reach are the extremes of the <em>index's</em> order, so a
    /// descending key fills below everything with its type's maximum.
    /// </param>
    public static AkadeTupleKey<TKey> Create(
        IReadOnlyList<object> componentComparers,
        bool descending = false)
    {
        ArgumentNullException.ThrowIfNull(componentComparers);

        var types = AkadeTupleKey.ComponentTypes(typeof(TKey))
            ?? throw new ArgumentException(
                $"'{typeof(TKey).FullName}' is not a ValueTuple of two to four components.",
                nameof(componentComparers));

        if (types.Length != componentComparers.Count)
        {
            throw new ArgumentException(
                $"{componentComparers.Count} comparer(s) were given for {types.Length} key "
                + "component(s); give one per component, in key order.",
                nameof(componentComparers));
        }

        return new AkadeTupleKey<TKey>(
            CompileFill(types, descending),
            CompileComparer(types, componentComparers),
            types.Length);
    }

    private static Func<IReadOnlyList<object?>, bool, TKey> CompileFill(Type[] types, bool descending)
    {
        var bounds = Expression.Parameter(typeof(IReadOnlyList<object?>), "bounds");
        var useMaximum = Expression.Parameter(typeof(bool), "useMaximum");

        var component = typeof(AkadeBound)
            .GetMethod(nameof(AkadeBound.Component), BindingFlags.Public | BindingFlags.Static)!;

        var arguments = new Expression[types.Length];
        for (var i = 0; i < types.Length; i++)
        {
            var type = types[i];

            // A component type with no extremes — GUID, whose CLR order is not the byte order Chalk
            // sorts UUIDs by — is admitted for a HASH key only, and a hash lookup binds every
            // component, so the extremes are never read. Default rather than absent, so the compiled
            // fill is one shape.
            arguments[i] = Expression.Call(
                component.MakeGenericMethod(type),
                bounds,
                Expression.Constant(i),
                useMaximum,
                Extreme(descending ? AkadeKeyOrder.Maximum(type) : AkadeKeyOrder.Minimum(type), type),
                Extreme(descending ? AkadeKeyOrder.Minimum(type) : AkadeKeyOrder.Maximum(type), type));
        }

        var constructor = typeof(TKey).GetConstructor(types)
            ?? throw new InvalidOperationException(
                $"'{typeof(TKey).FullName}' has no constructor over its own component types.");

        return Expression
            .Lambda<Func<IReadOnlyList<object?>, bool, TKey>>(
                Expression.New(constructor, arguments),
                bounds,
                useMaximum)
            .Compile();
    }

    private static Expression Extreme(object? value, Type type) =>
        value is null ? Expression.Default(type) : Expression.Constant(value, type);

    private static IComparer<TKey> CompileComparer(Type[] types, IReadOnlyList<object> comparers)
    {
        var left = Expression.Parameter(typeof(TKey), "x");
        var right = Expression.Parameter(typeof(TKey), "y");
        var result = Expression.Variable(typeof(int), "c");
        var done = Expression.Label(typeof(int), "done");

        var body = new List<Expression>(types.Length + 2);
        for (var i = 0; i < types.Length; i++)
        {
            var comparer = comparers[i];
            var compare = typeof(IComparer<>).MakeGenericType(types[i]).GetMethod("Compare")!;
            var field = typeof(TKey).GetField("Item" + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture))!;

            body.Add(
                Expression.Assign(
                    result,
                    Expression.Call(
                        Expression.Constant(comparer, typeof(IComparer<>).MakeGenericType(types[i])),
                        compare,
                        Expression.Field(left, field),
                        Expression.Field(right, field))));

            body.Add(
                Expression.IfThen(
                    Expression.NotEqual(result, Expression.Constant(0)),
                    Expression.Return(done, result)));
        }

        body.Add(Expression.Label(done, Expression.Constant(0)));

        var comparison = Expression
            .Lambda<Comparison<TKey>>(
                Expression.Block(typeof(int), [result], body),
                left,
                right)
            .Compile();

        return Comparer<TKey>.Create(comparison);
    }
}

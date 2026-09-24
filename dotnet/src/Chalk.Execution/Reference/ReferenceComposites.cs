using System.Collections.Concurrent;
using System.Linq.Expressions;
using Chalk.Catalog;
using Chalk.Sources;

namespace Chalk.Execution.Reference;

/// <summary>
/// How the reference executor holds a COMPOSITE (D291, ADR 0077): an <c>object?[]</c> of its fields in
/// order, each in the reference's own representation — <see cref="long"/> for every integer,
/// <see cref="string"/> for a STRING — and null for a NULL composite. It is also what a host reads a
/// composite cell as.
/// </summary>
/// <remarks>
/// A delegate's record reaches it boxed, which the reference path is allowed (D13); the fields are
/// read through accessors compiled once per record type from the same properties the declaration and
/// the vectorised engine read, so the two engines cannot disagree about which property is which field.
/// </remarks>
internal static class ReferenceComposites
{
    private static readonly ConcurrentDictionary<Type, Func<object, object?>[]> Accessors = new();

    /// <summary>A boxed record, or null, as the reference's composite value.</summary>
    public static object?[]? FromRecord(object? boxed)
    {
        if (boxed is null)
        {
            return null;
        }

        var accessors = Accessors.GetOrAdd(boxed.GetType(), Compile);
        var fields = new object?[accessors.Length];
        for (var i = 0; i < fields.Length; i++)
        {
            fields[i] = Normalise(accessors[i](boxed));
        }

        return fields;
    }

    /// <summary>One field's boxed CLR value in the reference's representation.</summary>
    private static object? Normalise(object? value) => value switch
    {
        null => null,
        Utf8String text => text.ToString(),
        sbyte v => (long)v,
        short v => (long)v,
        int v => (long)v,
        _ => value,
    };

    private static Func<object, object?>[] Compile(Type record)
    {
        var properties = CompositeInference.Properties(record);
        var accessors = new Func<object, object?>[properties.Count];
        for (var i = 0; i < accessors.Length; i++)
        {
            var boxed = Expression.Parameter(typeof(object), "boxed");
            accessors[i] = Expression.Lambda<Func<object, object?>>(
                    Expression.Convert(
                        Expression.Property(Expression.Convert(boxed, record), properties[i]),
                        typeof(object)),
                    boxed)
                .Compile();
        }

        return accessors;
    }

    /// <summary>A boxed aggregate whose <c>Finish</c> answers a record, answering the composite value instead.</summary>
    public sealed class Aggregate(IBoxedAggregate inner) : IBoxedAggregate
    {
        public void Add(object? value) => inner.Add(value);

        public object? Finish() => FromRecord(inner.Finish());
    }
}

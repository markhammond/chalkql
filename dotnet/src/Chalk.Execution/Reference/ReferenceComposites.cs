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

    /// <summary>
    /// A boxed record, or null, as the reference's composite value: each field in the storage
    /// vocabulary, converted — and refused — as the vectorised engine writes the field's lane (D298).
    /// </summary>
    public static object?[]? FromRecord(object? boxed, ChalkType declared, string owner)
    {
        if (boxed is null)
        {
            return null;
        }

        var accessors = Accessors.GetOrAdd(boxed.GetType(), Compile);
        var fields = new object?[accessors.Length];
        for (var i = 0; i < fields.Length; i++)
        {
            var field = declared.Fields[i];
            fields[i] = Expressions.ClrBoxes.ToStorage(
                accessors[i](boxed),
                new Expressions.LaneFormat(field.Type, $"field '{field.Name}' of {owner}"));
        }

        return fields;
    }

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

    /// <summary>
    /// A boxed aggregate in the storage vocabulary: its input handed to <c>Add</c> in the delegate's
    /// own CLR spelling (D298), and <c>Finish</c>'s answer converted back — a record into the
    /// reference's composite value (D291), a scalar as the vectorised engine writes its lane.
    /// </summary>
    public sealed class Converted(
        IBoxedAggregate inner, ChalkType inputType, Type inputClr, ChalkType resultType, string name)
        : IBoxedAggregate
    {
        public void Add(object? value) =>
            inner.Add(Expressions.ClrBoxes.ToClr(value, inputType, inputClr));

        public object? Finish() => resultType.Kind == Ir.TypeKind.Composite
            ? FromRecord(inner.Finish(), resultType, $"the result of '{name}'")
            : Expressions.ClrBoxes.ToStorage(
                inner.Finish(), new Expressions.LaneFormat(resultType, $"the result of '{name}'"));
    }
}

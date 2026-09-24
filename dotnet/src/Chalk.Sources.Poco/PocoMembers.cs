using System.Linq.Expressions;
using System.Reflection;
using Chalk.Catalog;

namespace Chalk.Sources.Poco;

/// <summary>One candidate column found on the row type, before naming and type mapping.</summary>
internal sealed class PocoMember
{
    public required MemberInfo Member { get; init; }

    public required Type ClrType { get; init; }

    /// <summary>Whether a value read through this member can be null (§5.2).</summary>
    public required bool Nullable { get; init; }

    public required ChalkColumnAttribute? Attribute { get; init; }

    public required bool Ignored { get; init; }

    public string Name => Member.Name;
}

/// <summary>
/// Member discovery for §5.2: public instance properties with a getter and public instance fields,
/// in declaration order. Static members, indexers and anything marked <c>[ChalkIgnore]</c> or
/// <c>[ChalkColumn(Ignore = true)]</c> are skipped.
/// </summary>
internal static class PocoMembers
{
    public static IReadOnlyList<PocoMember> Discover(Type rowType)
    {
        // Reflection does not promise declaration order, but metadata tokens increase with it inside a
        // type, and base types are laid out before derived ones. Properties and fields live in
        // different metadata tables, though, so their tokens cannot be interleaved: §5.2's
        // "declaration order" is honoured within each kind, with every property before every field.
        var nullability = new NullabilityInfoContext();
        var members = new List<(int Depth, int Kind, int Token, PocoMember Member)>();

        foreach (var property in rowType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0 || property.GetMethod is not { IsPublic: true, IsStatic: false })
            {
                continue;
            }

            members.Add((
                Depth(property.DeclaringType),
                0,
                property.MetadataToken,
                Describe(property, property.PropertyType, nullability.Create(property).ReadState)));
        }

        foreach (var field in rowType.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            members.Add((
                Depth(field.DeclaringType),
                1,
                field.MetadataToken,
                Describe(field, field.FieldType, nullability.Create(field).ReadState)));
        }

        return members
            .OrderBy(m => m.Depth)
            .ThenBy(m => m.Kind)
            .ThenBy(m => m.Token)
            .Select(m => m.Member)
            .ToArray();
    }

    /// <summary>
    /// Resolves the member a builder lambda names. A boxing conversion is unwrapped so that
    /// <c>UniqueKey(r =&gt; r.Id)</c>, whose lambda returns <c>object?</c>, reaches the same member as
    /// <c>Column(r =&gt; r.Id)</c>.
    /// </summary>
    public static MemberInfo Resolve(LambdaExpression lambda, string table, string what)
    {
        var body = lambda.Body;
        while (body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert)
        {
            body = convert.Operand;
        }

        if (body is MemberExpression { Expression: ParameterExpression } member
            && member.Member is PropertyInfo or FieldInfo)
        {
            return member.Member;
        }

        // D302: a field of a composite member is reached in SQL and is not a column of its own, so
        // a declaration naming one is refused as naming it, not as a lambda this cannot read.
        if (body is MemberExpression { Expression: MemberExpression { Expression: ParameterExpression } outer } field
            && outer.Member is PropertyInfo or FieldInfo
            && CompositeInference.IsCandidate(outer.Type))
        {
            throw new CatalogValidationException(
                $"table '{table}'",
                $"{what} names '{field.Member.Name}' of '{outer.Member.Name}', which is a composite "
                + "column; a field of a composite column is not a column of its own, and nothing is "
                + "keyed, indexed or ordered on one. Declare the field as a member of its own.");
        }

        throw new CatalogValidationException(
            $"table '{table}'",
            $"{what} must name a property or field of {lambda.Parameters[0].Type.Name} directly, "
            + "for example row => row.Symbol. Use Column(name, projection) for a computed column.");
    }

    private static PocoMember Describe(MemberInfo member, Type clrType, NullabilityState state)
    {
        var attribute = member.GetCustomAttribute<ChalkColumnAttribute>();
        var ignored = attribute?.Ignore == true || member.GetCustomAttribute<ChalkIgnoreAttribute>() is not null;

        // Nullable<T> answers for itself; a reference type is nullable unless the compiler annotated it
        // otherwise, and an unannotated assembly reports Unknown, which §5.2 reads as nullable.
        var nullable = Nullable.GetUnderlyingType(clrType) is not null
            || (!clrType.IsValueType && state != NullabilityState.NotNull);

        return new PocoMember
        {
            Member = member,
            ClrType = clrType,
            Nullable = nullable,
            Attribute = attribute,
            Ignored = ignored,
        };
    }

    private static int Depth(Type? declaringType)
    {
        var depth = 0;
        for (var t = declaringType?.BaseType; t is not null; t = t.BaseType)
        {
            depth++;
        }

        return depth;
    }
}

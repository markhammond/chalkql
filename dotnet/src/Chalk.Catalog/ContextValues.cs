using Chalk.Ir;

namespace Chalk.Catalog;

/// <summary>
/// CLR values as IR literals for the execution context (<c>docs/design/16-entitlements.md</c> §2).
/// </summary>
/// <remarks>
/// A host binds the values it thinks in — an <see cref="int"/> tenancy identifier, a
/// <see cref="string"/> user name, a <see cref="DateOnly"/> — and the type is read off the value
/// rather than declared, because a context binding has no column to take its type from. Where the
/// inference would be wrong (a <see cref="long"/> that must compare as an <c>I32</c>, a decimal of a
/// particular scale) a host states the type instead.
/// </remarks>
public static class ContextValues
{
    /// <summary>The IR type a bound value has, by its CLR type.</summary>
    /// <exception cref="ArgumentException">for a type the IR has no kind for.</exception>
    public static ChalkType InferType(object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value switch
        {
            bool => ChalkType.Bool(),
            sbyte => ChalkType.Int8(),
            short => ChalkType.Int16(),
            int => ChalkType.Int32(),
            long => ChalkType.Int64(),
            float => ChalkType.Float32(),
            double => ChalkType.Float64(),
            string => ChalkType.String(),
            byte[] => ChalkType.Binary(),
            DateOnly => ChalkType.Date(),
            TimeOnly => ChalkType.Time(),
            DateTime => ChalkType.Timestamp(),
            DateTimeOffset => ChalkType.TimestampTz(),
            Guid => ChalkType.Uuid(),
            decimal d => ChalkType.Decimal(38, ScaleOf(d)),
            _ => throw new ArgumentException(
                $"a context value of type {value.GetType().Name} has no IR type. Bind bool, the "
                + "integer and floating kinds, decimal, string, byte[], DateOnly, TimeOnly, "
                + "DateTime, DateTimeOffset or Guid, or state the type yourself.",
                nameof(value)),
        };
    }

    /// <summary>The value as an IR literal expression of <paramref name="type"/>.</summary>
    public static Expr ToLiteral(object? value, ChalkType type, string what)
    {
        var literal = CatalogLiterals.ToProto(value, type, what) ?? new Literal { IsNull = true };
        return new Expr { Type = type.ToProto(), Literal = literal };
    }

    /// <summary>The same, with the type inferred from the value. A null value needs a type.</summary>
    public static Expr ToLiteral(object? value, string what)
    {
        if (value is null or DBNull)
        {
            throw new ArgumentException(
                $"{what} is null and no type was stated, so the planner would not know what NULL it "
                + "is. State the type, or leave the binding out.",
                nameof(value));
        }

        return ToLiteral(value, InferType(value), what);
    }

    /// <summary>The scale a decimal carries, which is part of its own representation.</summary>
    private static int ScaleOf(decimal value) =>
        (decimal.GetBits(value)[3] >> 16) & 0xFF;
}

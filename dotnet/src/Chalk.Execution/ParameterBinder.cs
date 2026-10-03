using System.Globalization;
using Chalk.Catalog;
using Chalk.Execution.Expressions;
using Chalk.Execution.Numeric;
using Chalk.Execution.Vectors;
using Chalk.Ir;

namespace Chalk.Execution;

/// <summary>
/// Binds the CLR values a host passed to <c>ExecuteAsync</c> into the scalars the engine evaluates
/// against — the reverse of the output mapping in <c>04-client.md</c> §7.2 — holding each to
/// <see cref="ParameterValues.Exact"/>: a value binds only when the parameter's type holds it
/// exactly (F155, D317).
/// </summary>
/// <remarks>
/// Both engines bind through this, which is one of the two things D13 lets the reference executor
/// share. Every mismatch is an <see cref="ArgumentException"/> naming the parameter, raised before a
/// single row is read. The client has already applied the same rule, naming the parameter as the
/// statement wrote it; this is the backstop for anything that reaches a plan without it.
/// </remarks>
internal static class ParameterBinder
{
    public static ScalarValue[] Bind(IReadOnlyList<object?> values, IReadOnlyList<ChalkType> types)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(types);
        if (values.Count != types.Count)
        {
            throw new ParameterBindingException(
                "Parameters",
                $"The plan declares {types.Count} parameter(s) but {values.Count} were supplied. Bind one "
                + "value per parameter, in order.");
        }

        var bound = new ScalarValue[types.Count];
        for (var i = 0; i < types.Count; i++)
        {
            bound[i] = Bind(values[i], types[i], i);
        }

        return bound;
    }

    private static ScalarValue Bind(object? value, ChalkType type, int index)
    {
        if (value is null or DBNull)
        {
            return type.Nullable
                ? ScalarValue.Null(type)
                : throw new ParameterBindingException(
                    $"Parameter {index.ToString(CultureInfo.InvariantCulture)}",
                    $"Parameter {index} is declared {type} but NULL was bound: the parameter is declared NOT NULL.");
        }

        return Scalar(ParameterValues.Exact(value, type, $"Parameter {index.ToString(CultureInfo.InvariantCulture)}"), type);
    }

    /// <summary>A value <see cref="ParameterValues.Exact"/> has already made the type's own CLR type.</summary>
    private static ScalarValue Scalar(object value, ChalkType type) => type.Kind switch
    {
        TypeKind.Bool => new ScalarValue { Type = type, Integer = (bool)value ? 1 : 0 },
        TypeKind.I8 or TypeKind.I16 or TypeKind.I32 or TypeKind.I64 =>
            new ScalarValue { Type = type, Integer = (long)value },
        TypeKind.Fp32 => new ScalarValue { Type = type, Single = (float)value },
        TypeKind.Fp64 => new ScalarValue { Type = type, Double = (double)value },
        TypeKind.String => value is Utf8String text8
            // D145: a Utf8String parameter travels as a STRING literal exactly as a string does. The
            // decode is once per execution, not once per row, which is why it is allowed here.
            ? Literals.Text(type, text8.ToString())
            : Literals.Text(type, (string)value),
        TypeKind.Binary => new ScalarValue { Type = type, Bytes = (byte[])value },
        TypeKind.Date => new ScalarValue { Type = type, Integer = ((DateOnly)value).DayNumber - EpochDayNumber },
        // Finer than a microsecond is truncated: the one loss D317 tolerates.
        TypeKind.Time => new ScalarValue { Type = type, Integer = ((TimeOnly)value).Ticks / TimeSpan.TicksPerMicrosecond },
        TypeKind.Timestamp => new ScalarValue { Type = type, Integer = Units((DateTime)value, type) },
        TypeKind.TimestampTz => new ScalarValue { Type = type, Integer = Units(((DateTimeOffset)value).UtcDateTime, type) },
        TypeKind.Decimal => new ScalarValue { Type = type, Bytes = DecimalBytes((decimal)value, type) },
        TypeKind.Uuid => new ScalarValue { Type = type, Bytes = ((Guid)value).ToByteArray(bigEndian: true) },
        TypeKind.IntervalDay => new ScalarValue { Type = type, Integer = ((TimeSpan)value).Ticks / TimeSpan.TicksPerMicrosecond },
        TypeKind.IntervalYear => new ScalarValue { Type = type, Integer = (int)value },
        _ => throw new ArgumentException($"the engine has no binding for {type}", nameof(type)),
    };

    /// <summary>Days from 0001-01-01 to 1970-01-01, which is what <see cref="DateOnly.DayNumber"/> counts from.</summary>
    private const int EpochDayNumber = 719_162;

    /// <summary>A decimal the parameter's precision and scale already hold, so writing it rounds nothing.</summary>
    private static byte[] DecimalBytes(decimal value, ChalkType type)
    {
        var bytes = new byte[16];
        Decimals.Write(bytes, value, type.Precision, type.Scale);
        return bytes;
    }

    /// <summary>
    /// A wall-clock <see cref="DateTime"/> in the storage units the IR type declares (§3), truncated
    /// below them — the one loss D317 tolerates.
    /// </summary>
    private static long Units(DateTime value, ChalkType type)
    {
        var unitsPerSecond = IrTypes.TimestampUnitsPerSecond((uint)type.Precision);
        var days = CivilTime.FromCivil(value.Year, value.Month, value.Day);
        var secondOfDay = (value.Hour * 3600L) + (value.Minute * 60L) + value.Second;
        var subSecond = value.Ticks % TimeSpan.TicksPerSecond * unitsPerSecond / TimeSpan.TicksPerSecond;
        return (((days * CivilTime.SecondsPerDay) + secondOfDay) * unitsPerSecond) + subSecond;
    }
}

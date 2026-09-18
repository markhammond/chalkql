using System.Globalization;
using Chalk.Catalog;
using Chalk.Execution.Expressions;
using Chalk.Execution.Numeric;
using Chalk.Execution.Vectors;
using Chalk.Ir;

namespace Chalk.Execution;

/// <summary>
/// Binds the CLR values a host passed to <c>ExecuteAsync</c> into the scalars the engine evaluates
/// against — the reverse of the output mapping in <c>04-client.md</c> §7.2, plus the safe widenings
/// §7.3 allows (<c>int</c> → I64, <c>float</c> → FP64, <c>DateTime</c> → TIMESTAMP).
/// </summary>
/// <remarks>
/// Both engines bind through this, which is one of the two things D13 lets the reference executor
/// share. Every mismatch is an <see cref="ArgumentException"/> naming the parameter, raised before a
/// single row is read.
/// </remarks>
internal static class ParameterBinder
{
    public static ScalarValue[] Bind(IReadOnlyList<object?> values, IReadOnlyList<ChalkType> types)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(types);
        if (values.Count != types.Count)
        {
            throw new ArgumentException(
                $"The plan declares {types.Count} parameter(s) but {values.Count} were supplied.",
                nameof(values));
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
                : throw Mismatch(index, type, "NULL", "the parameter is declared NOT NULL");
        }

        try
        {
            return Convert(value, type, index);
        }
        catch (Exception exception) when (exception is OverflowException or FormatException or InvalidCastException)
        {
            throw Mismatch(index, type, Describe(value), exception.Message);
        }
    }

    private static ScalarValue Convert(object value, ChalkType type, int index) => type.Kind switch
    {
        TypeKind.Bool => value is bool flag
            ? new ScalarValue { Type = type, Integer = flag ? 1 : 0 }
            : throw Mismatch(index, type, Describe(value), "expected a bool"),

        TypeKind.I8 or TypeKind.I16 or TypeKind.I32 or TypeKind.I64 =>
            new ScalarValue { Type = type, Integer = Integer(value, type, index) },

        TypeKind.Fp32 => new ScalarValue { Type = type, Single = (float)Real(value, type, index) },
        TypeKind.Fp64 => new ScalarValue { Type = type, Double = Real(value, type, index) },

        TypeKind.String => value switch
        {
            string text => Literals.Text(type, text),
            // D145: a Utf8String parameter travels as a STRING literal exactly as a string does. The
            // decode is once per execution, not once per row, which is why it is allowed here.
            Utf8String text8 => Literals.Text(type, text8.ToString()),
            char character => Literals.Text(type, character.ToString()),
            _ => throw Mismatch(index, type, Describe(value), "expected a string"),
        },

        TypeKind.Binary => value switch
        {
            byte[] bytes => new ScalarValue { Type = type, Bytes = bytes },
            ReadOnlyMemory<byte> memory => new ScalarValue { Type = type, Bytes = memory.ToArray() },
            _ => throw Mismatch(index, type, Describe(value), "expected a byte[]"),
        },

        TypeKind.Date => value switch
        {
            DateOnly date => new ScalarValue { Type = type, Integer = date.DayNumber - EpochDayNumber },
            DateTime timestamp => new ScalarValue
            {
                Type = type,
                Integer = DateOnly.FromDateTime(timestamp).DayNumber - EpochDayNumber,
            },
            _ => throw Mismatch(index, type, Describe(value), "expected a DateOnly"),
        },

        TypeKind.Time => value is TimeOnly time
            ? new ScalarValue { Type = type, Integer = time.Ticks / TimeSpan.TicksPerMicrosecond }
            : throw Mismatch(index, type, Describe(value), "expected a TimeOnly"),

        TypeKind.Timestamp => value switch
        {
            DateTime timestamp => new ScalarValue { Type = type, Integer = Units(timestamp, type) },
            DateTimeOffset offset => new ScalarValue { Type = type, Integer = Units(offset.UtcDateTime, type) },
            _ => throw Mismatch(index, type, Describe(value), "expected a DateTime"),
        },

        TypeKind.TimestampTz => value switch
        {
            DateTimeOffset offset => new ScalarValue { Type = type, Integer = Units(offset.UtcDateTime, type) },
            DateTime timestamp => new ScalarValue { Type = type, Integer = Units(timestamp, type) },
            _ => throw Mismatch(index, type, Describe(value), "expected a DateTimeOffset"),
        },

        TypeKind.Decimal => new ScalarValue { Type = type, Bytes = DecimalBytes(value, type, index) },

        TypeKind.Uuid => value is Guid uuid
            ? new ScalarValue { Type = type, Bytes = uuid.ToByteArray(bigEndian: true) }
            : throw Mismatch(index, type, Describe(value), "expected a Guid"),

        TypeKind.IntervalDay => value is TimeSpan span
            ? new ScalarValue { Type = type, Integer = span.Ticks / TimeSpan.TicksPerMicrosecond }
            : throw Mismatch(index, type, Describe(value), "expected a TimeSpan"),

        TypeKind.IntervalYear => value is int months
            ? new ScalarValue { Type = type, Integer = months }
            : throw Mismatch(index, type, Describe(value), "expected an int month count"),

        _ => throw Mismatch(index, type, Describe(value), "the engine has no binding for this type"),
    };

    /// <summary>Days from 0001-01-01 to 1970-01-01, which is what <see cref="DateOnly.DayNumber"/> counts from.</summary>
    private const int EpochDayNumber = 719_162;

    private static long Integer(object value, ChalkType type, int index)
    {
        var raw = value switch
        {
            sbyte v => v,
            byte v => v,
            short v => v,
            ushort v => v,
            int v => v,
            uint v => v,
            long v => v,
            ulong v => checked((long)v),
            _ => throw Mismatch(index, type, Describe(value), "expected an integer"),
        };

        var (min, max) = type.Kind switch
        {
            TypeKind.I8 => ((long)sbyte.MinValue, (long)sbyte.MaxValue),
            TypeKind.I16 => (short.MinValue, short.MaxValue),
            TypeKind.I32 => (int.MinValue, int.MaxValue),
            _ => (long.MinValue, long.MaxValue),
        };

        return raw >= min && raw <= max
            ? raw
            : throw Mismatch(index, type, Describe(value), $"the value is outside {min}..{max}");
    }

    private static double Real(object value, ChalkType type, int index) => value switch
    {
        float v => v,
        double v => v,
        sbyte or byte or short or ushort or int or uint or long => System.Convert.ToDouble(
            value, CultureInfo.InvariantCulture),
        decimal v => (double)v,
        _ => throw Mismatch(index, type, Describe(value), "expected a floating-point number"),
    };

    private static byte[] DecimalBytes(object value, ChalkType type, int index)
    {
        var number = value switch
        {
            decimal v => v,
            sbyte or byte or short or ushort or int or uint or long or ulong =>
                System.Convert.ToDecimal(value, CultureInfo.InvariantCulture),
            double v => (decimal)v,
            float v => (decimal)v,
            _ => throw Mismatch(index, type, Describe(value), "expected a decimal"),
        };

        var bytes = new byte[16];
        Decimals.Write(bytes, number, type.Precision, type.Scale);
        return bytes;
    }

    /// <summary>A wall-clock <see cref="DateTime"/> in the storage units the IR type declares (§3).</summary>
    private static long Units(DateTime value, ChalkType type)
    {
        var unitsPerSecond = IrTypes.TimestampUnitsPerSecond((uint)type.Precision);
        var days = CivilTime.FromCivil(value.Year, value.Month, value.Day);
        var secondOfDay = (value.Hour * 3600L) + (value.Minute * 60L) + value.Second;
        var subSecond = value.Ticks % TimeSpan.TicksPerSecond * unitsPerSecond / TimeSpan.TicksPerSecond;
        return (((days * CivilTime.SecondsPerDay) + secondOfDay) * unitsPerSecond) + subSecond;
    }

    private static string Describe(object value) =>
        $"{value.GetType().Name} '{System.Convert.ToString(value, CultureInfo.InvariantCulture)}'";

    private static ArgumentException Mismatch(int index, ChalkType type, string value, string detail) =>
        new($"Parameter {index} is declared {type} but {value} was bound: {detail}.", $"parameters[{index}]");
}

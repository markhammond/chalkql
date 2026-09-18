using System.Globalization;
using System.Numerics;
using Chalk.Ir;
using Google.Protobuf;

namespace Chalk.Catalog;

/// <summary>
/// CLR values ↔ IR <see cref="Literal"/>s, for the statistics a catalog carries (D36): a column's
/// <c>min</c>, <c>max</c>, histogram bounds and most-common values.
/// </summary>
/// <remarks>
/// The public catalog model states these as plain CLR values, so a host never builds a proto message
/// by hand. For the temporal kinds either shape is accepted: the value a host thinks in
/// (<see cref="DateTime"/>, <see cref="DateOnly"/>, …) or the encoded one a source's own extractor
/// produces — days since the epoch for DATE, microseconds for TIME, <c>Type.precision</c> units for
/// TIMESTAMP (<c>04-client.md</c> §7.2). Reading one back always gives the first.
/// </remarks>
internal static class CatalogLiterals
{
    private const int EpochDayNumber = 719_162; // DateOnly(1970, 1, 1).DayNumber

    /// <summary>Encodes a CLR value as a literal of <paramref name="type"/>, or null for "unknown".</summary>
    public static Literal? ToProto(object? value, ChalkType type, string what)
    {
        if (value is null or DBNull)
        {
            return null;
        }

        try
        {
            return type.Kind switch
            {
                TypeKind.Bool => new Literal { BoolValue = Convert.ToBoolean(value, CultureInfo.InvariantCulture) },
                TypeKind.I8 => new Literal { I8Value = Convert.ToSByte(value, CultureInfo.InvariantCulture) },
                TypeKind.I16 => new Literal { I16Value = Convert.ToInt16(value, CultureInfo.InvariantCulture) },
                TypeKind.I32 => new Literal { I32Value = Convert.ToInt32(value, CultureInfo.InvariantCulture) },
                TypeKind.I64 => new Literal { I64Value = Convert.ToInt64(value, CultureInfo.InvariantCulture) },
                TypeKind.Fp32 => new Literal { Fp32Value = Convert.ToSingle(value, CultureInfo.InvariantCulture) },
                TypeKind.Fp64 => new Literal { Fp64Value = Convert.ToDouble(value, CultureInfo.InvariantCulture) },
                TypeKind.String => new Literal
                {
                    StringValue = value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
                },
                TypeKind.Binary => new Literal { BinaryValue = ByteString.CopyFrom(Bytes(value)) },
                TypeKind.Date => new Literal { DateValue = Days(value) },
                TypeKind.Time => new Literal { TimeValue = Microseconds(value) },
                TypeKind.Timestamp => new Literal { TimestampValue = TimestampUnits(value, type) },
                TypeKind.TimestampTz => new Literal { TimestampTzValue = TimestampUnits(value, type) },
                TypeKind.Decimal => new Literal
                {
                    DecimalValue = new DecimalValue
                    {
                        Unscaled = ByteString.CopyFrom(
                            Unscaled(Convert.ToDecimal(value, CultureInfo.InvariantCulture), type)),
                    },
                },
                TypeKind.Uuid => new Literal
                {
                    UuidValue = ByteString.CopyFrom(((Guid)value).ToByteArray(bigEndian: true)),
                },
                TypeKind.IntervalDay => new Literal { IntervalDayValue = Microseconds(value) },
                TypeKind.IntervalYear => new Literal
                {
                    IntervalYearValue = Convert.ToInt32(value, CultureInfo.InvariantCulture),
                },
                _ => throw new CatalogValidationException(
                    what, $"a statistic of type {type} cannot be expressed as a literal"),
            };
        }
        catch (Exception e) when (e is InvalidCastException or FormatException or OverflowException)
        {
            throw new CatalogValidationException(
                what,
                $"the statistic value ({value.GetType().Name}) does not fit the column's type {type}: {e.Message}");
        }
    }

    /// <summary>Reads a literal back as the CLR value the column produces.</summary>
    public static object? FromProto(Literal? literal, ChalkType type)
    {
        if (literal is null || literal.ValueCase == Literal.ValueOneofCase.None || literal.HasIsNull)
        {
            return null;
        }

        return literal.ValueCase switch
        {
            Literal.ValueOneofCase.BoolValue => literal.BoolValue,
            Literal.ValueOneofCase.I8Value => (sbyte)literal.I8Value,
            Literal.ValueOneofCase.I16Value => (short)literal.I16Value,
            Literal.ValueOneofCase.I32Value => literal.I32Value,
            Literal.ValueOneofCase.I64Value => literal.I64Value,
            Literal.ValueOneofCase.Fp32Value => literal.Fp32Value,
            Literal.ValueOneofCase.Fp64Value => literal.Fp64Value,
            Literal.ValueOneofCase.StringValue => literal.StringValue,
            Literal.ValueOneofCase.BinaryValue => literal.BinaryValue.ToByteArray(),
            Literal.ValueOneofCase.DateValue => DateOnly.FromDayNumber(literal.DateValue + EpochDayNumber),
            Literal.ValueOneofCase.TimeValue =>
                new TimeOnly(literal.TimeValue * TimeSpan.TicksPerMicrosecond),
            Literal.ValueOneofCase.TimestampValue => Timestamp(literal.TimestampValue, type),
            Literal.ValueOneofCase.TimestampTzValue =>
                new DateTimeOffset(Timestamp(literal.TimestampTzValue, type), TimeSpan.Zero),
            Literal.ValueOneofCase.DecimalValue => Scaled(literal.DecimalValue.Unscaled.Span, type),
            Literal.ValueOneofCase.UuidValue => new Guid(literal.UuidValue.Span, bigEndian: true),
            Literal.ValueOneofCase.IntervalDayValue =>
                TimeSpan.FromTicks(literal.IntervalDayValue * TimeSpan.TicksPerMicrosecond),
            Literal.ValueOneofCase.IntervalYearValue => literal.IntervalYearValue,
            _ => null,
        };
    }

    private static byte[] Bytes(object value) => value switch
    {
        byte[] bytes => bytes,
        ReadOnlyMemory<byte> memory => memory.ToArray(),
        _ => throw new InvalidCastException($"cannot express a {value.GetType().Name} as BINARY"),
    };

    private static int Days(object value) => value switch
    {
        DateOnly date => date.DayNumber - EpochDayNumber,
        DateTime timestamp => DateOnly.FromDateTime(timestamp).DayNumber - EpochDayNumber,
        // Already encoded: what a source's own column extractor produces.
        int days => days,
        long days => checked((int)days),
        _ => throw new InvalidCastException($"cannot express a {value.GetType().Name} as DATE"),
    };

    private static long Microseconds(object value) => value switch
    {
        TimeOnly time => time.Ticks / TimeSpan.TicksPerMicrosecond,
        TimeSpan span => span.Ticks / TimeSpan.TicksPerMicrosecond,
        long microseconds => microseconds,
        int microseconds => microseconds,
        _ => throw new InvalidCastException($"cannot express a {value.GetType().Name} as TIME"),
    };

    private static long TimestampUnits(object value, ChalkType type)
    {
        if (value is long units)
        {
            return units;
        }

        var timestamp = value switch
        {
            DateTime dateTime => dateTime,
            DateTimeOffset offset => offset.UtcDateTime,
            DateOnly date => date.ToDateTime(TimeOnly.MinValue),
            _ => throw new InvalidCastException($"cannot express a {value.GetType().Name} as {type.Kind}"),
        };

        var ticks = timestamp.Ticks - DateTime.UnixEpoch.Ticks;
        return type.Precision switch
        {
            0 => ticks / TimeSpan.TicksPerSecond,
            3 => ticks / TimeSpan.TicksPerMillisecond,
            6 => ticks / TimeSpan.TicksPerMicrosecond,
            _ => ticks * 100,
        };
    }

    private static DateTime Timestamp(long units, ChalkType type)
    {
        var ticks = type.Precision switch
        {
            0 => units * TimeSpan.TicksPerSecond,
            3 => units * TimeSpan.TicksPerMillisecond,
            6 => units * TimeSpan.TicksPerMicrosecond,
            _ => units / 100,
        };
        return new DateTime(DateTime.UnixEpoch.Ticks + ticks, DateTimeKind.Unspecified);
    }

    /// <summary>The decimal as a 16-byte little-endian two's-complement unscaled integer (02-ir.md §3).</summary>
    private static byte[] Unscaled(decimal value, ChalkType type)
    {
        var scaled = decimal.Round(value, type.Scale, MidpointRounding.ToEven);
        var unscaled = new BigInteger(scaled * Pow10(type.Scale));
        var bytes = new byte[16];
        if (!unscaled.TryWriteBytes(bytes, out _, isUnsigned: false, isBigEndian: false))
        {
            throw new OverflowException($"{value} does not fit DECIMAL({type.Precision},{type.Scale})");
        }

        if (unscaled.Sign < 0)
        {
            // TryWriteBytes writes the shortest two's-complement form; sign-extend the rest.
            var written = unscaled.GetByteCount(isUnsigned: false);
            for (var i = written; i < bytes.Length; i++)
            {
                bytes[i] = 0xFF;
            }
        }

        return bytes;
    }

    private static decimal Scaled(ReadOnlySpan<byte> unscaled, ChalkType type)
    {
        var value = new BigInteger(unscaled, isUnsigned: false, isBigEndian: false);
        return (decimal)value / Pow10(type.Scale);
    }

    private static decimal Pow10(int scale)
    {
        var result = 1m;
        for (var i = 0; i < scale; i++)
        {
            result *= 10m;
        }

        return result;
    }
}

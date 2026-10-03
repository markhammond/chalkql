using System.Globalization;
using Chalk.Catalog;
using Chalk.Ir;

namespace Chalk.Execution;

/// <summary>
/// What a bound CLR value becomes as a value of a given type: exactly that value, or a refusal (F155,
/// F160, D317). The one place the rule lives — the client applies it to a statement's parameters and
/// to every context value whose type is known, naming each as the host wrote it, after the host's
/// <c>BindingConverter</c>s (D323); the engine's binder applies it again for anything that reaches a
/// plan without the client.
/// </summary>
/// <remarks>
/// <para>
/// The rule is that nothing is lost. An integer binds to any integer type that holds its value, and
/// an <see cref="Enum"/> as its number, as Dapper sends one; a fraction never binds to an integer,
/// not even 3.0. A DECIMAL takes an integer, a decimal, or a <see cref="float"/> or
/// <see cref="double"/> read as its shortest round-trip decimal — 0.1 is 0.1 — when the value fits
/// the precision and scale without rounding. A DOUBLE is approximate, and takes an integer or a
/// decimal at its nearest double, as SQL converts an exact number to an approximate one; a REAL
/// takes anything it holds exactly. Text binds only to STRING, and nothing is parsed: not a number,
/// not a date, not a GUID.
/// </para>
/// <para>
/// Time is the one place a loss is tolerated: a value finer than the parameter's precision is
/// truncated to it, as Npgsql does, because the column cannot hold the extra digits and refusing
/// would refuse <c>DateTime.UtcNow</c> against every microsecond source. Which CLR value is which
/// kind of time follows Npgsql 6: TIMESTAMP is a wall clock — a <see cref="DateTime"/> of kind Local
/// or Unspecified, or a <see cref="DateOnly"/> — and TIMESTAMP WITH TIME ZONE is an instant — a
/// <see cref="DateTimeOffset"/>, or a <see cref="DateTime"/> of kind Utc.
/// </para>
/// <para>
/// A refusal names what was bound — a parameter, a context scalar, a context binding's column — and
/// the CLR type bound to it, never the value: a value is the caller's data, and a refusal is a log
/// line.
/// </para>
/// </remarks>
internal static class ParameterValues
{
    /// <summary>
    /// The value as the CLR type the engine binds <paramref name="type"/> from: <see cref="long"/>
    /// for every integer type, <see cref="float"/>, <see cref="double"/>, <see cref="decimal"/>,
    /// <see cref="string"/> or <see cref="Utf8String"/>, <see cref="byte"/>[], <see cref="DateOnly"/>,
    /// <see cref="TimeOnly"/>, <see cref="DateTime"/> (a wall clock), <see cref="DateTimeOffset"/>
    /// (UTC), <see cref="Guid"/>, <see cref="TimeSpan"/>, <see cref="int"/> (months) or
    /// <see cref="bool"/>. Throws a <see cref="ParameterBindingException"/> naming <paramref name="what"/> —
    /// <c>Parameter @amount</c>, <c>Context scalar 'tenant'</c> — for anything the type does not hold
    /// exactly; <paramref name="via"/> names the converter the value came from, when it did.
    /// </summary>
    public static object Exact(object value, ChalkType type, string what, string? via = null) =>
        Exact(value, type, new Subject(what, via));

    private static object Exact(object value, ChalkType type, Subject parameter) => type.Kind switch
    {
        TypeKind.Bool => value is bool flag ? flag : throw Refused(parameter, type, value, "a BOOL takes a bool"),

        TypeKind.I8 or TypeKind.I16 or TypeKind.I32 or TypeKind.I64 => Integer(value, type, parameter),

        TypeKind.Fp32 => Single(value, type, parameter),
        TypeKind.Fp64 => Double(value, type, parameter),
        TypeKind.Decimal => Decimal(value, type, parameter),

        TypeKind.String => value switch
        {
            string text => text,
            Utf8String text => text,
            char character => character.ToString(),
            _ => throw Refused(parameter, type, value, "text binds from a string, a Utf8String or a char, and nothing is converted to text"),
        },

        TypeKind.Binary => value switch
        {
            byte[] bytes => bytes,
            ReadOnlyMemory<byte> memory => memory.ToArray(),
            _ => throw Refused(parameter, type, value, "BINARY takes a byte[] or a ReadOnlyMemory<byte>"),
        },

        TypeKind.Date => value switch
        {
            DateOnly date => date,
            DateTime timestamp when timestamp.TimeOfDay == TimeSpan.Zero => DateOnly.FromDateTime(timestamp),
            DateTime => throw Refused(parameter, type, value, "a DATE holds no time of day, and this one has one"),
            _ => throw Refused(parameter, type, value, "a DATE takes a DateOnly, or a DateTime at midnight"),
        },

        TypeKind.Time => value switch
        {
            TimeOnly time => time,
            TimeSpan span when span >= TimeSpan.Zero && span < TimeSpan.FromDays(1) => TimeOnly.FromTimeSpan(span),
            TimeSpan => throw Refused(parameter, type, value, "a TIME is a time of day, and this span is not within one"),
            _ => throw Refused(parameter, type, value, "a TIME takes a TimeOnly, or a TimeSpan within a day"),
        },

        TypeKind.Timestamp => value switch
        {
            DateTime { Kind: DateTimeKind.Utc } => throw Refused(
                parameter, type, value,
                "a TIMESTAMP is a wall clock, and a DateTime of kind Utc is an instant; bind it to a "
                + "TIMESTAMP WITH TIME ZONE, or give it kind Unspecified"),
            DateTime timestamp => DateTime.SpecifyKind(timestamp, DateTimeKind.Unspecified),
            DateOnly date => date.ToDateTime(TimeOnly.MinValue),
            DateTimeOffset => throw Refused(
                parameter, type, value,
                "a TIMESTAMP is a wall clock, and a DateTimeOffset is an instant; bind it to a "
                + "TIMESTAMP WITH TIME ZONE, or bind its DateTime"),
            _ => throw Refused(parameter, type, value, "a TIMESTAMP takes a DateTime or a DateOnly"),
        },

        TypeKind.TimestampTz => value switch
        {
            DateTimeOffset offset => offset.ToUniversalTime(),
            DateTime { Kind: DateTimeKind.Utc } timestamp => new DateTimeOffset(timestamp),
            DateTime => throw Refused(
                parameter, type, value,
                "a TIMESTAMP WITH TIME ZONE is an instant, and a DateTime of kind Local or Unspecified "
                + "is a wall clock; bind a DateTimeOffset, or a DateTime of kind Utc"),
            _ => throw Refused(parameter, type, value, "a TIMESTAMP WITH TIME ZONE takes a DateTimeOffset or a UTC DateTime"),
        },

        TypeKind.Uuid => value is Guid uuid ? uuid : throw Refused(parameter, type, value, "a UUID takes a Guid, and nothing is parsed"),

        TypeKind.IntervalDay => value is TimeSpan span ? span : throw Refused(parameter, type, value, "an INTERVAL DAY takes a TimeSpan"),

        TypeKind.IntervalYear => IsInteger(value, out var months) && months >= int.MinValue && months <= int.MaxValue
            ? (int)months
            : throw Refused(parameter, type, value, "an INTERVAL YEAR takes a whole number of months"),

        _ => throw Refused(parameter, type, value, "the engine binds no value to this type"),
    };

    /// <summary>An integer or an enum, and its value; false for anything else, a fraction included.</summary>
    private static bool IsInteger(object value, out Int128 number)
    {
        if (value is Enum)
        {
            value = System.Convert.ChangeType(
                value, Enum.GetUnderlyingType(value.GetType()), CultureInfo.InvariantCulture);
        }

        switch (value)
        {
            case sbyte v: number = v; return true;
            case byte v: number = v; return true;
            case short v: number = v; return true;
            case ushort v: number = v; return true;
            case int v: number = v; return true;
            case uint v: number = v; return true;
            case long v: number = v; return true;
            case ulong v: number = v; return true;
            case Int128 v: number = v; return true;
            case UInt128 v when v <= (UInt128)Int128.MaxValue: number = (Int128)v; return true;
            default: number = 0; return false;
        }
    }

    private static long Integer(object value, ChalkType type, Subject parameter)
    {
        if (!IsInteger(value, out var number))
        {
            throw Refused(
                parameter, type, value,
                value is float or double or decimal or Half
                    ? "an integer type holds no fraction, and a fractional type is not bound to one even when its value is whole"
                    : "an integer type takes an integer or an enum");
        }

        var (min, max) = type.Kind switch
        {
            TypeKind.I8 => ((long)sbyte.MinValue, (long)sbyte.MaxValue),
            TypeKind.I16 => (short.MinValue, short.MaxValue),
            TypeKind.I32 => (int.MinValue, int.MaxValue),
            _ => (long.MinValue, long.MaxValue),
        };

        return number >= min && number <= max
            ? (long)number
            : throw Refused(parameter, type, value, $"its value is outside {type.Kind}'s range");
    }

    private static double Double(object value, ChalkType type, Subject parameter)
    {
        if (value is double real)
        {
            return real;
        }

        if (value is float single)
        {
            return single;
        }

        if (value is decimal exact)
        {
            return (double)exact;
        }

        if (IsInteger(value, out var number))
        {
            return (double)number;
        }

        throw Refused(parameter, type, value, "a DOUBLE takes a number");
    }

    private static float Single(object value, ChalkType type, Subject parameter)
    {
        switch (value)
        {
            case float single:
                return single;
            case double real when double.IsNaN(real):
                return float.NaN;
            case double real when (double)(float)real == real:
                return (float)real;
            case decimal exact when TryExactSingle(exact, out var fromDecimal):
                return fromDecimal;
        }

        if (IsInteger(value, out var number) && (Int128)(float)number == number)
        {
            return (float)number;
        }

        throw Refused(
            parameter, type, value,
            value is double or decimal || IsInteger(value, out _)
                ? "a REAL does not hold this value exactly; bind a float, or CAST the parameter to DOUBLE"
                : "a REAL takes a number");
    }

    private static bool TryExactSingle(decimal value, out float single)
    {
        single = (float)value;
        try
        {
            return (decimal)single == value;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static decimal Decimal(object value, ChalkType type, Subject parameter)
    {
        decimal number;
        switch (value)
        {
            case decimal exact:
                number = exact;
                break;
            case double real when double.IsFinite(real) && TryShortest(real.ToString(CultureInfo.InvariantCulture), out var fromDouble):
                number = fromDouble;
                break;
            case float single when float.IsFinite(single) && TryShortest(single.ToString(CultureInfo.InvariantCulture), out var fromSingle):
                number = fromSingle;
                break;
            case double or float:
                throw Refused(parameter, type, value, "a DECIMAL holds no infinity, NaN, or value this large");
            default:
                if (!IsInteger(value, out var integer) || integer > (Int128)decimal.MaxValue || integer < (Int128)decimal.MinValue)
                {
                    throw Refused(parameter, type, value, "a DECIMAL takes a number");
                }

                number = (decimal)integer;
                break;
        }

        var scale = type.Scale;
        if (scale < 28 && decimal.Round(number, scale) != number)
        {
            throw Refused(
                parameter, type, value,
                $"its value has more fractional digits than {type}'s scale of {scale}, and would be rounded");
        }

        var integral = decimal.Truncate(decimal.Abs(number));
        var digits = type.Precision - scale;
        if (digits < 29 && integral >= Pow10(digits))
        {
            throw Refused(parameter, type, value, $"its value has more integer digits than {type} holds");
        }

        return number;
    }

    /// <summary>A float's or double's shortest round-trip text as a decimal, which is what it was written as.</summary>
    private static bool TryShortest(string text, out decimal number)
    {
        try
        {
            number = decimal.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
            return true;
        }
        catch (OverflowException)
        {
            number = 0;
            return false;
        }
    }

    private static decimal Pow10(int exponent)
    {
        var result = 1m;
        for (var i = 0; i < exponent; i++)
        {
            result *= 10m;
        }

        return result;
    }

    private static ParameterBindingException Refused(Subject subject, ChalkType type, object value, string reason) =>
        new(
            subject.What,
            $"{subject.What} is {type}, and a {Describe(value)}"
            + (subject.Via is { } via ? $" returned by {via}" : string.Empty)
            + $" was bound to it: {reason}. A bound value is never converted at a loss; bind one the "
            + "type holds exactly.");

    /// <summary>What is being bound, and the converter its value came from.</summary>
    private readonly record struct Subject(string What, string? Via);

    /// <summary>The CLR type bound, never the value — a value is the caller's data.</summary>
    private static string Describe(object value) => value switch
    {
        DateTime { Kind: var kind } => $"DateTime of kind {kind}",
        _ => value.GetType().Name,
    };
}

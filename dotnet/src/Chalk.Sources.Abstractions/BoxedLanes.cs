using System.Runtime.CompilerServices;
using System.Text;

namespace Chalk.Sources;

/// <summary>
/// The reference executor's crossings between a boxed value and a Tier 1 delegate's own CLR type,
/// written so that the type may be a <c>ref struct</c>: a <c>ReadOnlySpan&lt;byte&gt;</c> parameter or
/// result cannot be boxed or unboxed by a cast, and a type parameter that <c>allows ref struct</c>
/// cannot be cast from or to <c>object</c> at all. Every crossing is therefore a reinterpretation
/// through <see cref="Unsafe.As{TFrom, TTo}(ref TFrom)"/> of a value of the one concrete type the
/// spelling is, and the set of spellings is the closed Tier 1 set <c>LaneCodec</c> accepts.
/// </summary>
/// <remarks>
/// This is the boxed path only (D13): per call, and allowed to allocate. The vectorised engine reads a
/// lane straight into the delegate's type through <c>LaneCodec</c> and never comes here.
/// </remarks>
internal static class BoxedLanes
{
    /// <summary>
    /// One boxed argument as the delegate's own type. A STRING arrives as a .NET <c>string</c> and a
    /// BINARY as a <c>byte[]</c>; a delegate written in <c>ReadOnlySpan&lt;byte&gt;</c> is handed a span
    /// over the encoded bytes, and one written in <see cref="Utf8String"/> — a result's spelling, and
    /// a table function's parameter's — a copy of its own.
    /// </summary>
    public static T Cast<T>(object? value)
        where T : allows ref struct
    {
        if (typeof(T) == typeof(ReadOnlySpan<byte>))
        {
            var span = Bytes(value);
            return Unsafe.As<ReadOnlySpan<byte>, T>(ref span);
        }

        if (typeof(T) == typeof(Utf8String))
        {
            var text = value is Utf8String already ? already : Utf8String.FromString((string?)value);
            return Unsafe.As<Utf8String, T>(ref text);
        }

        if (typeof(T) == typeof(Utf8String?))
        {
            // Written out for the reason V43 gives: a null literal beside a Utf8String binds to the
            // implicit byte[] conversion and produces the empty value, not a null Nullable.
            Utf8String? text = default;
            if (value is Utf8String already)
            {
                text = already;
            }
            else if (value is not null)
            {
                text = Utf8String.FromString((string)value);
            }

            return Unsafe.As<Utf8String?, T>(ref text);
        }

        if (!typeof(T).IsValueType)
        {
            // string, byte[], a record class: the reference itself, reinterpreted as the type it is.
            return Unsafe.As<object?, T>(ref value);
        }

        if (typeof(T) == typeof(bool)) return Value<bool, T>(value);
        if (typeof(T) == typeof(bool?)) return NullableValue<bool, T>(value);
        if (typeof(T) == typeof(sbyte)) return Value<sbyte, T>(value);
        if (typeof(T) == typeof(sbyte?)) return NullableValue<sbyte, T>(value);
        if (typeof(T) == typeof(short)) return Value<short, T>(value);
        if (typeof(T) == typeof(short?)) return NullableValue<short, T>(value);
        if (typeof(T) == typeof(int)) return Value<int, T>(value);
        if (typeof(T) == typeof(int?)) return NullableValue<int, T>(value);
        if (typeof(T) == typeof(long)) return Value<long, T>(value);
        if (typeof(T) == typeof(long?)) return NullableValue<long, T>(value);
        if (typeof(T) == typeof(float)) return Value<float, T>(value);
        if (typeof(T) == typeof(float?)) return NullableValue<float, T>(value);
        if (typeof(T) == typeof(double)) return Value<double, T>(value);
        if (typeof(T) == typeof(double?)) return NullableValue<double, T>(value);
        if (typeof(T) == typeof(decimal)) return Value<decimal, T>(value);
        if (typeof(T) == typeof(decimal?)) return NullableValue<decimal, T>(value);
        if (typeof(T) == typeof(DateOnly)) return Value<DateOnly, T>(value);
        if (typeof(T) == typeof(DateOnly?)) return NullableValue<DateOnly, T>(value);
        if (typeof(T) == typeof(TimeOnly)) return Value<TimeOnly, T>(value);
        if (typeof(T) == typeof(TimeOnly?)) return NullableValue<TimeOnly, T>(value);
        if (typeof(T) == typeof(DateTime)) return Value<DateTime, T>(value);
        if (typeof(T) == typeof(DateTime?)) return NullableValue<DateTime, T>(value);
        if (typeof(T) == typeof(DateTimeOffset)) return Value<DateTimeOffset, T>(value);
        if (typeof(T) == typeof(DateTimeOffset?)) return NullableValue<DateTimeOffset, T>(value);
        if (typeof(T) == typeof(TimeSpan)) return Value<TimeSpan, T>(value);
        if (typeof(T) == typeof(TimeSpan?)) return NullableValue<TimeSpan, T>(value);
        if (typeof(T) == typeof(Guid)) return Value<Guid, T>(value);
        if (typeof(T) == typeof(Guid?)) return NullableValue<Guid, T>(value);
        if (typeof(T) == typeof(ReadOnlyMemory<byte>)) return Value<ReadOnlyMemory<byte>, T>(value);
        if (typeof(T) == typeof(ReadOnlyMemory<byte>?)) return NullableValue<ReadOnlyMemory<byte>, T>(value);

        throw new InvalidOperationException(
            $"{typeof(T).Name} is not a Tier 1 spelling, and the binding check should have refused it.");
    }

    /// <summary>
    /// The delegate's answer as the boxed value the reference executor compares in. Text leaves as a
    /// <c>string</c> — decoded, and therefore also copied out of whatever buffer the delegate lent,
    /// which the lifetime rule requires of anything kept — and a <c>ReadOnlySpan&lt;byte&gt;</c> as a
    /// <c>byte[]</c> of its own, which <c>ClrBoxes.ToStorage</c> decodes for a STRING and keeps for a
    /// BINARY.
    /// </summary>
    public static object? Box<TOut>(TOut value)
        where TOut : allows ref struct
    {
        if (typeof(TOut) == typeof(ReadOnlySpan<byte>))
        {
            return Unsafe.As<TOut, ReadOnlySpan<byte>>(ref value).ToArray();
        }

        if (typeof(TOut) == typeof(Utf8String))
        {
            return Unsafe.As<TOut, Utf8String>(ref value).ToString();
        }

        if (typeof(TOut) == typeof(Utf8String?))
        {
            var text = Unsafe.As<TOut, Utf8String?>(ref value);
            return text is { } present ? present.ToString() : null;
        }

        if (!typeof(TOut).IsValueType)
        {
            return Unsafe.As<TOut, object?>(ref value);
        }

        // A struct, a record struct or a Nullable of either, boxed as the runtime boxes it — a
        // Nullable without a value is null — without a cast the anti-constraint forbids.
        return RuntimeHelpers.Box(ref Unsafe.As<TOut, byte>(ref value), typeof(TOut).TypeHandle);
    }

    /// <summary>The bytes a boxed text or binary value stands for, encoded where it was a string.</summary>
    private static ReadOnlySpan<byte> Bytes(object? value) => value switch
    {
        null => default,
        byte[] bytes => bytes,
        string text => Encoding.UTF8.GetBytes(text),
        Utf8String text => text.AsSpan(),
        ReadOnlyMemory<byte> memory => memory.Span,
        _ => throw new InvalidCastException(
            $"a {value.GetType().Name} cannot be handed to a delegate as ReadOnlySpan<byte>"),
    };

    private static T Value<TValue, T>(object? value)
        where TValue : struct
        where T : allows ref struct
    {
        var unboxed = (TValue)value!;
        return Unsafe.As<TValue, T>(ref unboxed);
    }

    private static T NullableValue<TValue, T>(object? value)
        where TValue : struct
        where T : allows ref struct
    {
        TValue? unboxed = value is null ? null : (TValue)value;
        return Unsafe.As<TValue?, T>(ref unboxed);
    }
}

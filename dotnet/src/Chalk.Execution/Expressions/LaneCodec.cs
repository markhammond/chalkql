using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Chalk.Catalog;
using Chalk.Ir;
using Chalk.Sources;
using Type = System.Type;

namespace Chalk.Execution.Expressions;

/// <summary>
/// What a lane of a declared type needs besides its CLR spelling (D298): a DECIMAL's precision and
/// scale, a TIMESTAMP's precision — which decide the lane's unit — and whose lane it is, for a
/// refusal. Built once when a kernel, a composite writer or an aggregate is bound; read per lane.
/// </summary>
internal readonly struct LaneFormat
{
    public LaneFormat(ChalkType type, string owner)
    {
        Type = type;
        Owner = owner;
    }

    /// <summary>The declared type.</summary>
    public ChalkType Type { get; }

    /// <summary>Whose lane this is, as a refusal names it: "the result of 'f'", "argument 'x' of 'f'".</summary>
    public string Owner { get; }

    public int Precision => Type.Precision;

    public int Scale => Type.Scale;
}

/// <summary>
/// The bridge between a declared <see cref="ChalkType"/> and the CLR type a Tier 1 delegate is
/// written in (D79, <c>docs/design/17-user-defined-functions.md</c> §3).
/// </summary>
/// <remarks>
/// <para>
/// Every method is generic and dispatches on <c>typeof(T)</c>, which the JIT resolves at
/// specialisation time for a value type: the branches are gone in the compiled code and reading a
/// lane is one indexed load. That is what makes the lane loop allocation-free without any codegen of
/// Chalk's own.
/// </para>
/// <para>
/// The CLR spellings are the ones whose in-memory shape <em>is</em> the column's — <c>bool</c> (a
/// byte per lane), <c>sbyte</c>, <c>short</c>, <c>int</c>, <c>long</c>, <c>float</c>, <c>double</c>
/// — and, since D298, the ones the POCO source maps a member from, converted per lane through the
/// same <see cref="ClrStorage"/> the POCO chunk writer uses: <c>decimal</c> for a DECIMAL of up to 28
/// digits, <c>DateOnly</c>, <c>TimeOnly</c>, <c>DateTime</c> (a TIMESTAMP, kind unspecified),
/// <c>DateTimeOffset</c> (a TIMESTAMP_TZ, in UTC), <c>TimeSpan</c> (an INTERVAL_DAY), <c>Guid</c>
/// and <c>ReadOnlyMemory&lt;byte&gt;</c> for BINARY — all allocation-free — plus <c>string</c> and
/// <c>byte[]</c>, which cost an allocation per row as a .NET string always has. Each value type may
/// also be spelled nullable, for a non-strict function. A temporal may still be spelled as its raw
/// count (<c>int</c> days, <c>long</c> units), as it could before D298. A LIST, an INTERVAL_YEAR
/// beyond its raw <c>int</c>, and a DECIMAL of more than 28 digits have no Tier 1 spelling.
/// </para>
/// <para>
/// A CLR temporal counts 100-nanosecond ticks. Read out of a lane, a nanosecond TIMESTAMP is floored
/// to the tick; written into one, a value the lane's unit cannot hold exactly — a sub-microsecond
/// TIME or INTERVAL_DAY, a TIMESTAMP(3) with sub-millisecond ticks — is refused, never dropped.
/// </para>
/// </remarks>
internal static class LaneCodec
{
    /// <summary>The most digits a CLR <c>decimal</c> holds, and so the widest DECIMAL it may spell.</summary>
    public const int DecimalDigits = 28;

    /// <summary>
    /// The CLR type a delegate uses for <paramref name="type"/>, or null when it has none. A STRING is
    /// named as <c>string</c>, which is what <see cref="Describe"/> prints; <see cref="Accepts{T}"/> also
    /// takes <see cref="Utf8String"/> for it, which is the allocation-free spelling (D146), and the
    /// other alternatives <see cref="Describe"/> lists.
    /// </summary>
    public static Type? ClrTypeOf(ChalkType type) => type.Kind switch
    {
        TypeKind.Bool => typeof(bool),
        TypeKind.I8 => typeof(sbyte),
        TypeKind.I16 => typeof(short),
        TypeKind.I32 or TypeKind.IntervalYear => typeof(int),
        TypeKind.I64 => typeof(long),
        TypeKind.Fp32 => typeof(float),
        TypeKind.Fp64 => typeof(double),
        TypeKind.String => typeof(string),
        TypeKind.Date => typeof(DateOnly),
        TypeKind.Time => typeof(TimeOnly),
        TypeKind.Timestamp => typeof(DateTime),
        TypeKind.TimestampTz => typeof(DateTimeOffset),
        TypeKind.IntervalDay => typeof(TimeSpan),
        TypeKind.Uuid => typeof(Guid),
        TypeKind.Binary => typeof(ReadOnlyMemory<byte>),
        TypeKind.Decimal when type.Precision <= DecimalDigits => typeof(decimal),
        _ => null,
    };

    /// <summary>
    /// The raw count a temporal may also be spelled as — <c>int</c> days, <c>long</c> units — which is
    /// how a delegate read one before D298, and still may.
    /// </summary>
    private static Type? RawTypeOf(ChalkType type) => type.Kind switch
    {
        TypeKind.Date => typeof(int),
        TypeKind.Time or TypeKind.Timestamp or TypeKind.TimestampTz or TypeKind.IntervalDay => typeof(long),
        _ => null,
    };

    /// <summary>
    /// Whether <typeparamref name="T"/> is how a delegate may spell <paramref name="type"/>. A value
    /// type may also be spelled nullable, which is what a non-strict function needs in order to see a
    /// NULL at all.
    /// </summary>
    public static bool Accepts<T>(ChalkType type) => Accepts(typeof(T), type);

    /// <summary>The same question asked of a <see cref="Type"/>, for the signature check.</summary>
    public static bool Accepts(Type actual, ChalkType type)
    {
        var required = ClrTypeOf(type);
        if (required is null)
        {
            return false;
        }

        if (actual == typeof(ReadOnlySpan<byte>))
        {
            // D304: the bytes of a STRING or a BINARY lane as a span — the borrowed spelling, which the
            // compiler keeps inside the call. Inferred as a STRING; a BINARY span is declared explicitly.
            return type.Kind is TypeKind.String or TypeKind.Binary;
        }

        var underlying = Nullable.GetUnderlyingType(actual);
        if (required == typeof(string)
            && (actual == typeof(Utf8String) || underlying == typeof(Utf8String)))
        {
            // D146: a STRING may be spelled Utf8String, which is a memory-backed slice of the lane
            // and allocates nothing. `string` stays supported and stays the allocating lane.
            return true;
        }

        if (type.Kind == TypeKind.Binary && actual == typeof(byte[]))
        {
            // D298: byte[] is BINARY's allocating spelling, as string is STRING's.
            return true;
        }

        if (RawTypeOf(type) is { } raw && (actual == raw || underlying == raw))
        {
            return true;
        }

        return actual == required || underlying == required;
    }

    /// <summary>
    /// Explains the mismatch, naming both sides. Used by the engine-creation check, which is where a
    /// wrong delegate should be caught (§5's negative).
    /// </summary>
    public static string Describe(ChalkType type)
    {
        var described = IrTypes.Describe(type.ToProto());
        if (type.Kind == TypeKind.Decimal && type.Precision > DecimalDigits)
        {
            return $"{described} (no Tier 1 CLR type: a CLR decimal holds {DecimalDigits} digits and this "
                + $"DECIMAL {type.Precision}; use a Tier 2 kernel, which reads the 16-byte lane)";
        }

        return ClrTypeOf(type) is { } clr
            ? type.Kind switch
            {
                TypeKind.String => $"{described} (CLR ReadOnlySpan<Byte> or String; a result may also be Utf8String)",
                TypeKind.Binary => $"{described} (CLR ReadOnlySpan<Byte>, ReadOnlyMemory<Byte> or Byte[])",
                TypeKind.Date => $"{described} (CLR DateOnly, or Int32 days since 1970-01-01)",
                TypeKind.Time => $"{described} (CLR TimeOnly, or Int64 microseconds since midnight)",
                TypeKind.Timestamp =>
                    $"{described} (CLR DateTime, or Int64 {Unit(type.Precision)} since 1970-01-01)",
                TypeKind.TimestampTz =>
                    $"{described} (CLR DateTimeOffset, or Int64 {Unit(type.Precision)} since 1970-01-01 UTC)",
                TypeKind.IntervalDay => $"{described} (CLR TimeSpan, or Int64 microseconds)",
                TypeKind.IntervalYear => $"{described} (CLR Int32 months)",
                _ => $"{described} (CLR {clr.Name})",
            }
            : $"{described} (no Tier 1 CLR type; use a Tier 2 kernel)";
    }

    /// <summary>What a TIMESTAMP lane of <paramref name="precision"/> counts.</summary>
    private static string Unit(int precision) => precision switch
    {
        <= 3 => "milliseconds",
        <= 6 => "microseconds",
        _ => "nanoseconds",
    };

    /// <summary>Reads one lane. NULL becomes <c>default</c> for a value type and null for the rest.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T Read<T>(in ColumnView view, int row, in LaneFormat format)
        where T : allows ref struct
    {
        if (typeof(T) == typeof(ReadOnlySpan<byte>))
        {
            // D304: the lane's own bytes, borrowed for the call and unable to outlive it — a STRING's
            // or a BINARY's. The one borrowed spelling a delegate is handed.
            var value = view.VarValue(row);
            return Unsafe.As<ReadOnlySpan<byte>, T>(ref value);
        }

        if (typeof(T) == typeof(double))
        {
            var value = view.Lanes<double>()[row];
            return Unsafe.As<double, T>(ref value);
        }

        if (typeof(T) == typeof(long))
        {
            var value = view.Lanes<long>()[row];
            return Unsafe.As<long, T>(ref value);
        }

        if (typeof(T) == typeof(int))
        {
            var value = view.Lanes<int>()[row];
            return Unsafe.As<int, T>(ref value);
        }

        if (typeof(T) == typeof(float))
        {
            var value = view.Lanes<float>()[row];
            return Unsafe.As<float, T>(ref value);
        }

        if (typeof(T) == typeof(short))
        {
            var value = view.Lanes<short>()[row];
            return Unsafe.As<short, T>(ref value);
        }

        if (typeof(T) == typeof(sbyte))
        {
            var value = view.Lanes<sbyte>()[row];
            return Unsafe.As<sbyte, T>(ref value);
        }

        if (typeof(T) == typeof(bool))
        {
            var value = view.BoolAt(row);
            return Unsafe.As<bool, T>(ref value);
        }

        if (typeof(T) == typeof(Utf8String))
        {
            var value = view.Utf8(row);
            return Unsafe.As<Utf8String, T>(ref value);
        }

        // D298: the POCO source's CLR spellings, through the conversions its chunk writer uses.
        if (typeof(T) == typeof(decimal))
        {
            var value = ClrStorage.DecimalOf(view.RawLanes(16).Slice(row * 16, 16), format.Scale);
            return Unsafe.As<decimal, T>(ref value);
        }

        if (typeof(T) == typeof(DateOnly))
        {
            var value = ClrStorage.DateOf(view.Lanes<int>()[row]);
            return Unsafe.As<DateOnly, T>(ref value);
        }

        if (typeof(T) == typeof(TimeOnly))
        {
            var value = ClrStorage.TimeOf(view.Lanes<long>()[row]);
            return Unsafe.As<TimeOnly, T>(ref value);
        }

        if (typeof(T) == typeof(DateTime))
        {
            var value = ClrStorage.DateTimeOf(view.Lanes<long>()[row], format.Precision);
            return Unsafe.As<DateTime, T>(ref value);
        }

        if (typeof(T) == typeof(DateTimeOffset))
        {
            var value = ClrStorage.InstantOf(view.Lanes<long>()[row], format.Precision);
            return Unsafe.As<DateTimeOffset, T>(ref value);
        }

        if (typeof(T) == typeof(TimeSpan))
        {
            var value = ClrStorage.IntervalOf(view.Lanes<long>()[row]);
            return Unsafe.As<TimeSpan, T>(ref value);
        }

        if (typeof(T) == typeof(Guid))
        {
            var value = ClrStorage.UuidOf(view.RawLanes(16).Slice(row * 16, 16));
            return Unsafe.As<Guid, T>(ref value);
        }

        if (typeof(T) == typeof(ReadOnlyMemory<byte>))
        {
            // A slice of the lane's own buffer, borrowed for the call as a Utf8String is (D146).
            var value = view.VarValueMemory(row);
            return Unsafe.As<ReadOnlyMemory<byte>, T>(ref value);
        }

        if (typeof(T) == typeof(string))
        {
            // F133: a NULL is null, not the empty string — a non-strict delegate spelled string? is
            // the one that reads a NULL row, and used to be handed "" for it.
            var value = view.IsValid(row) ? Encoding.UTF8.GetString(view.VarValue(row)) : null;
            return Unsafe.As<string?, T>(ref value);
        }

        if (typeof(T) == typeof(byte[]))
        {
            // BINARY's allocating spelling, as string is STRING's: a copy the delegate may keep.
            var value = view.IsValid(row) ? view.VarValue(row).ToArray() : null;
            return Unsafe.As<byte[]?, T>(ref value);
        }

        return ReadNullable<T>(view, row, format);
    }

    /// <summary>The nullable forms, which is what a non-strict delegate is written in.</summary>
    private static T ReadNullable<T>(in ColumnView view, int row, in LaneFormat format)
        where T : allows ref struct
    {
        var valid = view.IsValid(row);
        if (typeof(T) == typeof(double?))
        {
            double? value = valid ? view.Lanes<double>()[row] : null;
            return Unsafe.As<double?, T>(ref value);
        }

        if (typeof(T) == typeof(long?))
        {
            long? value = valid ? view.Lanes<long>()[row] : null;
            return Unsafe.As<long?, T>(ref value);
        }

        if (typeof(T) == typeof(int?))
        {
            int? value = valid ? view.Lanes<int>()[row] : null;
            return Unsafe.As<int?, T>(ref value);
        }

        if (typeof(T) == typeof(float?))
        {
            float? value = valid ? view.Lanes<float>()[row] : null;
            return Unsafe.As<float?, T>(ref value);
        }

        if (typeof(T) == typeof(short?))
        {
            short? value = valid ? view.Lanes<short>()[row] : null;
            return Unsafe.As<short?, T>(ref value);
        }

        if (typeof(T) == typeof(sbyte?))
        {
            sbyte? value = valid ? view.Lanes<sbyte>()[row] : null;
            return Unsafe.As<sbyte?, T>(ref value);
        }

        if (typeof(T) == typeof(bool?))
        {
            bool? value = valid ? view.BoolAt(row) : null;
            return Unsafe.As<bool?, T>(ref value);
        }

        if (typeof(T) == typeof(Utf8String?))
        {
            Utf8String? value = default;

            if (valid)
            {
                value = view.Utf8(row);
            }

            return Unsafe.As<Utf8String?, T>(
                ref value);
        }

        if (typeof(T) == typeof(decimal?))
        {
            decimal? value = valid
                ? ClrStorage.DecimalOf(view.RawLanes(16).Slice(row * 16, 16), format.Scale)
                : null;
            return Unsafe.As<decimal?, T>(ref value);
        }

        if (typeof(T) == typeof(DateOnly?))
        {
            DateOnly? value = valid ? ClrStorage.DateOf(view.Lanes<int>()[row]) : null;
            return Unsafe.As<DateOnly?, T>(ref value);
        }

        if (typeof(T) == typeof(TimeOnly?))
        {
            TimeOnly? value = valid ? ClrStorage.TimeOf(view.Lanes<long>()[row]) : null;
            return Unsafe.As<TimeOnly?, T>(ref value);
        }

        if (typeof(T) == typeof(DateTime?))
        {
            DateTime? value = valid
                ? ClrStorage.DateTimeOf(view.Lanes<long>()[row], format.Precision)
                : null;
            return Unsafe.As<DateTime?, T>(ref value);
        }

        if (typeof(T) == typeof(DateTimeOffset?))
        {
            DateTimeOffset? value = valid
                ? ClrStorage.InstantOf(view.Lanes<long>()[row], format.Precision)
                : null;
            return Unsafe.As<DateTimeOffset?, T>(ref value);
        }

        if (typeof(T) == typeof(TimeSpan?))
        {
            TimeSpan? value = valid ? ClrStorage.IntervalOf(view.Lanes<long>()[row]) : null;
            return Unsafe.As<TimeSpan?, T>(ref value);
        }

        if (typeof(T) == typeof(Guid?))
        {
            Guid? value = valid ? ClrStorage.UuidOf(view.RawLanes(16).Slice(row * 16, 16)) : null;
            return Unsafe.As<Guid?, T>(ref value);
        }

        if (typeof(T) == typeof(ReadOnlyMemory<byte>?))
        {
            ReadOnlyMemory<byte>? value = valid ? view.VarValueMemory(row) : null;
            return Unsafe.As<ReadOnlyMemory<byte>?, T>(ref value);
        }

        throw new UnsupportedFeatureException(
            $"a Tier 1 argument of CLR type {typeof(T).Name}",
            "Tier 1 delegates take " + Spellings + " and their nullable forms; anything else needs a "
            + "Tier 2 kernel");
    }

    /// <summary>The CLR types a Tier 1 lane may be spelled in, as a refusal lists them.</summary>
    internal const string Spellings =
        "bool, sbyte, short, int, long, float, double, decimal, DateOnly, TimeOnly, DateTime, "
        + "DateTimeOffset, TimeSpan, Guid, ReadOnlySpan<byte>, Utf8String, string, ReadOnlyMemory<byte> "
        + "and byte[]";

    /// <summary>
    /// Whether a value a delegate returned is a NULL.
    /// </summary>
    /// <remarks>
    /// Written out per form rather than as <c>value is null</c>, which in a generic method boxes: for
    /// <c>T = double?</c> the compiler emits a box before the null test, and a box per row is exactly
    /// what the allocation gate exists to catch. <c>default(T) is not null</c> would box too — the
    /// optimiser folds it in Release and does not in Debug, which is how that one was found — so the
    /// non-nullable forms are named by <c>typeof</c>, which is a handle comparison either way.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsNull<T>(T value)
        where T : allows ref struct
    {
        if (typeof(T) == typeof(ReadOnlySpan<byte>)
            || typeof(T) == typeof(double)
            || typeof(T) == typeof(long)
            || typeof(T) == typeof(int)
            || typeof(T) == typeof(float)
            || typeof(T) == typeof(short)
            || typeof(T) == typeof(sbyte)
            || typeof(T) == typeof(bool)
            || typeof(T) == typeof(Utf8String)
            || typeof(T) == typeof(decimal)
            || typeof(T) == typeof(DateOnly)
            || typeof(T) == typeof(TimeOnly)
            || typeof(T) == typeof(DateTime)
            || typeof(T) == typeof(DateTimeOffset)
            || typeof(T) == typeof(TimeSpan)
            || typeof(T) == typeof(Guid)
            || typeof(T) == typeof(ReadOnlyMemory<byte>))
        {
            return false;
        }

        if (typeof(T) == typeof(Utf8String?))
        {
            return !Unsafe.As<T, Utf8String?>(ref value).HasValue;
        }

        if (typeof(T) == typeof(double?))
        {
            return !Unsafe.As<T, double?>(ref value).HasValue;
        }

        if (typeof(T) == typeof(long?))
        {
            return !Unsafe.As<T, long?>(ref value).HasValue;
        }

        if (typeof(T) == typeof(int?))
        {
            return !Unsafe.As<T, int?>(ref value).HasValue;
        }

        if (typeof(T) == typeof(float?))
        {
            return !Unsafe.As<T, float?>(ref value).HasValue;
        }

        if (typeof(T) == typeof(short?))
        {
            return !Unsafe.As<T, short?>(ref value).HasValue;
        }

        if (typeof(T) == typeof(sbyte?))
        {
            return !Unsafe.As<T, sbyte?>(ref value).HasValue;
        }

        if (typeof(T) == typeof(bool?))
        {
            return !Unsafe.As<T, bool?>(ref value).HasValue;
        }

        if (typeof(T) == typeof(decimal?))
        {
            return !Unsafe.As<T, decimal?>(ref value).HasValue;
        }

        if (typeof(T) == typeof(DateOnly?))
        {
            return !Unsafe.As<T, DateOnly?>(ref value).HasValue;
        }

        if (typeof(T) == typeof(TimeOnly?))
        {
            return !Unsafe.As<T, TimeOnly?>(ref value).HasValue;
        }

        if (typeof(T) == typeof(DateTime?))
        {
            return !Unsafe.As<T, DateTime?>(ref value).HasValue;
        }

        if (typeof(T) == typeof(DateTimeOffset?))
        {
            return !Unsafe.As<T, DateTimeOffset?>(ref value).HasValue;
        }

        if (typeof(T) == typeof(TimeSpan?))
        {
            return !Unsafe.As<T, TimeSpan?>(ref value).HasValue;
        }

        if (typeof(T) == typeof(Guid?))
        {
            return !Unsafe.As<T, Guid?>(ref value).HasValue;
        }

        if (typeof(T) == typeof(ReadOnlyMemory<byte>?))
        {
            return !Unsafe.As<T, ReadOnlyMemory<byte>?>(ref value).HasValue;
        }

        return value is null;
    }

    /// <summary>
    /// Writes one lane of a fixed-width result. A NULL is left as a zeroed lane with its validity bit
    /// clear, which is what the caller has already arranged.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Write<T>(ColumnWriter writer, int length, int row, T value, in LaneFormat format)
        where T : allows ref struct
    {
        if (typeof(T) == typeof(double))
        {
            writer.Values<double>(length)[row] = Unsafe.As<T, double>(ref value);
            return;
        }

        if (typeof(T) == typeof(long))
        {
            writer.Values<long>(length)[row] = Unsafe.As<T, long>(ref value);
            return;
        }

        if (typeof(T) == typeof(int))
        {
            writer.Values<int>(length)[row] = Unsafe.As<T, int>(ref value);
            return;
        }

        if (typeof(T) == typeof(float))
        {
            writer.Values<float>(length)[row] = Unsafe.As<T, float>(ref value);
            return;
        }

        if (typeof(T) == typeof(short))
        {
            writer.Values<short>(length)[row] = Unsafe.As<T, short>(ref value);
            return;
        }

        if (typeof(T) == typeof(sbyte))
        {
            writer.Values<sbyte>(length)[row] = Unsafe.As<T, sbyte>(ref value);
            return;
        }

        if (typeof(T) == typeof(bool))
        {
            writer.Values<byte>(length)[row] = (byte)(Unsafe.As<T, bool>(ref value) ? 1 : 0);
            return;
        }

        if (typeof(T) == typeof(double?))
        {
            writer.Values<double>(length)[row] = Unsafe.As<T, double?>(ref value)!.Value;
            return;
        }

        if (typeof(T) == typeof(long?))
        {
            writer.Values<long>(length)[row] = Unsafe.As<T, long?>(ref value)!.Value;
            return;
        }

        if (typeof(T) == typeof(int?))
        {
            writer.Values<int>(length)[row] = Unsafe.As<T, int?>(ref value)!.Value;
            return;
        }

        if (typeof(T) == typeof(float?))
        {
            writer.Values<float>(length)[row] = Unsafe.As<T, float?>(ref value)!.Value;
            return;
        }

        if (typeof(T) == typeof(short?))
        {
            writer.Values<short>(length)[row] = Unsafe.As<T, short?>(ref value)!.Value;
            return;
        }

        if (typeof(T) == typeof(sbyte?))
        {
            writer.Values<sbyte>(length)[row] = Unsafe.As<T, sbyte?>(ref value)!.Value;
            return;
        }

        if (typeof(T) == typeof(bool?))
        {
            writer.Values<byte>(length)[row] = (byte)(Unsafe.As<T, bool?>(ref value)!.Value ? 1 : 0);
            return;
        }

        // D298: every widened spelling is one fixed-width lane, written through WriteLane's
        // conversions — the same as a composite field's or an aggregate's answer. Each is named as
        // the concrete type it is before the call, so WriteLane need not admit a ref struct.

        if (typeof(T) == typeof(decimal))
        {
            _ = WriteLane(
                System.Runtime.InteropServices.MemoryMarshal.AsBytes(writer.Values<Int128>(length))
                    .Slice(row * 16, 16),
                Unsafe.As<T, decimal>(ref value),
                format);
            return;
        }

        if (typeof(T) == typeof(decimal?))
        {
            _ = WriteLane(
                System.Runtime.InteropServices.MemoryMarshal.AsBytes(writer.Values<Int128>(length))
                    .Slice(row * 16, 16),
                Unsafe.As<T, decimal?>(ref value),
                format);
            return;
        }

        if (typeof(T) == typeof(Guid))
        {
            _ = WriteLane(
                System.Runtime.InteropServices.MemoryMarshal.AsBytes(writer.Values<Int128>(length))
                    .Slice(row * 16, 16),
                Unsafe.As<T, Guid>(ref value),
                format);
            return;
        }

        if (typeof(T) == typeof(Guid?))
        {
            _ = WriteLane(
                System.Runtime.InteropServices.MemoryMarshal.AsBytes(writer.Values<Int128>(length))
                    .Slice(row * 16, 16),
                Unsafe.As<T, Guid?>(ref value),
                format);
            return;
        }

        if (typeof(T) == typeof(DateOnly))
        {
            _ = WriteLane(
                System.Runtime.InteropServices.MemoryMarshal.AsBytes(writer.Values<int>(length))
                    .Slice(row * 4, 4),
                Unsafe.As<T, DateOnly>(ref value),
                format);
            return;
        }

        if (typeof(T) == typeof(DateOnly?))
        {
            _ = WriteLane(
                System.Runtime.InteropServices.MemoryMarshal.AsBytes(writer.Values<int>(length))
                    .Slice(row * 4, 4),
                Unsafe.As<T, DateOnly?>(ref value),
                format);
            return;
        }

        if (typeof(T) == typeof(TimeOnly))
        {
            _ = WriteLane(
                System.Runtime.InteropServices.MemoryMarshal.AsBytes(writer.Values<long>(length))
                    .Slice(row * 8, 8),
                Unsafe.As<T, TimeOnly>(ref value),
                format);
            return;
        }

        if (typeof(T) == typeof(TimeOnly?))
        {
            _ = WriteLane(
                System.Runtime.InteropServices.MemoryMarshal.AsBytes(writer.Values<long>(length))
                    .Slice(row * 8, 8),
                Unsafe.As<T, TimeOnly?>(ref value),
                format);
            return;
        }

        if (typeof(T) == typeof(DateTime))
        {
            _ = WriteLane(
                System.Runtime.InteropServices.MemoryMarshal.AsBytes(writer.Values<long>(length))
                    .Slice(row * 8, 8),
                Unsafe.As<T, DateTime>(ref value),
                format);
            return;
        }

        if (typeof(T) == typeof(DateTime?))
        {
            _ = WriteLane(
                System.Runtime.InteropServices.MemoryMarshal.AsBytes(writer.Values<long>(length))
                    .Slice(row * 8, 8),
                Unsafe.As<T, DateTime?>(ref value),
                format);
            return;
        }

        if (typeof(T) == typeof(DateTimeOffset))
        {
            _ = WriteLane(
                System.Runtime.InteropServices.MemoryMarshal.AsBytes(writer.Values<long>(length))
                    .Slice(row * 8, 8),
                Unsafe.As<T, DateTimeOffset>(ref value),
                format);
            return;
        }

        if (typeof(T) == typeof(DateTimeOffset?))
        {
            _ = WriteLane(
                System.Runtime.InteropServices.MemoryMarshal.AsBytes(writer.Values<long>(length))
                    .Slice(row * 8, 8),
                Unsafe.As<T, DateTimeOffset?>(ref value),
                format);
            return;
        }

        if (typeof(T) == typeof(TimeSpan))
        {
            _ = WriteLane(
                System.Runtime.InteropServices.MemoryMarshal.AsBytes(writer.Values<long>(length))
                    .Slice(row * 8, 8),
                Unsafe.As<T, TimeSpan>(ref value),
                format);
            return;
        }

        if (typeof(T) == typeof(TimeSpan?))
        {
            _ = WriteLane(
                System.Runtime.InteropServices.MemoryMarshal.AsBytes(writer.Values<long>(length))
                    .Slice(row * 8, 8),
                Unsafe.As<T, TimeSpan?>(ref value),
                format);
            return;
        }

        throw new UnsupportedFeatureException(
            $"a Tier 1 result of CLR type {typeof(T).Name}",
            "Tier 1 delegates return " + Spellings + " and their nullable forms "
            + "(docs/design/17-user-defined-functions.md §3).");
    }

    /// <summary>
    /// Zeroes one lane of a fixed-width result: a NULL composite's non-nullable field, which holds its
    /// storage default — never garbage from the previous batch, and never a NULL its column may not
    /// have. Zero is every layout's default, and it needs no conversion that could refuse.
    /// </summary>
    public static void WriteZero(ColumnWriter writer, int length, int row, ChalkType type)
    {
        var width = Vectors.ColumnKinds.Width(Vectors.ColumnKinds.Of(type));
        switch (width)
        {
            case 1:
                writer.Values<byte>(length)[row] = 0;
                return;
            case 2:
                writer.Values<short>(length)[row] = 0;
                return;
            case 4:
                writer.Values<int>(length)[row] = 0;
                return;
            case 8:
                writer.Values<long>(length)[row] = 0;
                return;
            case 16:
                writer.Values<Int128>(length)[row] = Int128.Zero;
                return;
            default:
                throw new UnsupportedFeatureException(
                    $"a zero lane of {type}", "Only fixed-width lanes are zeroed.");
        }
    }

    /// <summary>
    /// Reads a value out of a raw lane, which is how the aggregate path sees one: the hash aggregate
    /// hands every accumulator the argument's bytes rather than a column view.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T ReadRaw<T>(ReadOnlySpan<byte> lane, in LaneFormat format)
        where T : allows ref struct
    {
        if (typeof(T) == typeof(ReadOnlySpan<byte>))
        {
            // D304: a STRING or BINARY input is the value's own bytes — the hash aggregate hands a
            // variable-length measure's bytes as the lane — lent for the call and unable to outlive it.
            return Unsafe.As<ReadOnlySpan<byte>, T>(ref lane);
        }

        if (typeof(T) == typeof(double))
        {
            var value = System.Runtime.InteropServices.MemoryMarshal.Read<double>(lane);
            return Unsafe.As<double, T>(ref value);
        }

        if (typeof(T) == typeof(long))
        {
            var value = System.Runtime.InteropServices.MemoryMarshal.Read<long>(lane);
            return Unsafe.As<long, T>(ref value);
        }

        if (typeof(T) == typeof(int))
        {
            var value = System.Runtime.InteropServices.MemoryMarshal.Read<int>(lane);
            return Unsafe.As<int, T>(ref value);
        }

        if (typeof(T) == typeof(float))
        {
            var value = System.Runtime.InteropServices.MemoryMarshal.Read<float>(lane);
            return Unsafe.As<float, T>(ref value);
        }

        if (typeof(T) == typeof(short))
        {
            var value = System.Runtime.InteropServices.MemoryMarshal.Read<short>(lane);
            return Unsafe.As<short, T>(ref value);
        }

        if (typeof(T) == typeof(sbyte))
        {
            var value = System.Runtime.InteropServices.MemoryMarshal.Read<sbyte>(lane);
            return Unsafe.As<sbyte, T>(ref value);
        }

        if (typeof(T) == typeof(bool))
        {
            var value = lane[0] != 0;
            return Unsafe.As<bool, T>(ref value);
        }

        // D298: the fixed-width widened spellings. An aggregate's input never reaches here NULL: a
        // NULL lane is skipped before Add, as every built-in aggregate skips it.
        if (typeof(T) == typeof(decimal))
        {
            var value = ClrStorage.DecimalOf(lane, format.Scale);
            return Unsafe.As<decimal, T>(ref value);
        }

        if (typeof(T) == typeof(DateOnly))
        {
            var value = ClrStorage.DateOf(System.Runtime.InteropServices.MemoryMarshal.Read<int>(lane));
            return Unsafe.As<DateOnly, T>(ref value);
        }

        if (typeof(T) == typeof(TimeOnly))
        {
            var value = ClrStorage.TimeOf(System.Runtime.InteropServices.MemoryMarshal.Read<long>(lane));
            return Unsafe.As<TimeOnly, T>(ref value);
        }

        if (typeof(T) == typeof(DateTime))
        {
            var value = ClrStorage.DateTimeOf(
                System.Runtime.InteropServices.MemoryMarshal.Read<long>(lane), format.Precision);
            return Unsafe.As<DateTime, T>(ref value);
        }

        if (typeof(T) == typeof(DateTimeOffset))
        {
            var value = ClrStorage.InstantOf(
                System.Runtime.InteropServices.MemoryMarshal.Read<long>(lane), format.Precision);
            return Unsafe.As<DateTimeOffset, T>(ref value);
        }

        if (typeof(T) == typeof(TimeSpan))
        {
            var value = ClrStorage.IntervalOf(System.Runtime.InteropServices.MemoryMarshal.Read<long>(lane));
            return Unsafe.As<TimeSpan, T>(ref value);
        }

        if (typeof(T) == typeof(Guid))
        {
            var value = ClrStorage.UuidOf(lane);
            return Unsafe.As<Guid, T>(ref value);
        }

        throw new UnsupportedFeatureException(
            $"a Tier 1 aggregate over CLR type {typeof(T).Name}",
            "A Tier 1 aggregate takes bool, sbyte, short, int, long, float, double, decimal, "
            + "DateOnly, TimeOnly, DateTime, DateTimeOffset, TimeSpan or Guid "
            + "(docs/design/17-user-defined-functions.md §3).");
    }

    /// <summary>
    /// Writes a value into a raw lane, and says whether it was a value at all. A null answer leaves
    /// the lane zeroed, which is what an aggregate with no rows produces.
    /// </summary>
    public static bool WriteRaw<T>(Span<byte> lane, T value, in LaneFormat format)
    {
        switch (value)
        {
            case double d:
                System.Runtime.InteropServices.MemoryMarshal.Write(lane, in d);
                return true;
            case long l:
                System.Runtime.InteropServices.MemoryMarshal.Write(lane, in l);
                return true;
            case int i:
                System.Runtime.InteropServices.MemoryMarshal.Write(lane, in i);
                return true;
            case float f:
                System.Runtime.InteropServices.MemoryMarshal.Write(lane, in f);
                return true;
            case short s:
                System.Runtime.InteropServices.MemoryMarshal.Write(lane, in s);
                return true;
            case sbyte b:
                System.Runtime.InteropServices.MemoryMarshal.Write(lane, in b);
                return true;
            case bool o:
                lane[0] = (byte)(o ? 1 : 0);
                return true;
            case null:
                lane.Clear();
                return false;
            case decimal or DateOnly or TimeOnly or DateTime or DateTimeOffset or TimeSpan or Guid:
                // D298: the widened spellings, through the same conversions as a scalar's answer.
                return WriteLane(lane, value, format);
            default:
                throw new UnsupportedFeatureException(
                    $"a Tier 1 aggregate returning CLR type {typeof(T).Name}",
                    "A Tier 1 aggregate returns bool, sbyte, short, int, long, float, double, decimal, "
                    + "DateOnly, TimeOnly, DateTime, DateTimeOffset, TimeSpan or Guid, or a nullable "
                    + "form of one (docs/design/17-user-defined-functions.md §3).");
        }
    }

    /// <summary>
    /// Writes one fixed-width value into a raw lane without boxing it, and says whether it was a value
    /// at all — how a composite field reaches an aggregate's output copier (D294), and how every
    /// widened spelling reaches a lane (D298). A NULL clears the lane. Every branch is a
    /// <c>typeof</c> test the JIT folds away per instantiation.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool WriteLane<T>(Span<byte> lane, T value, in LaneFormat format)
    {
        if (typeof(T) == typeof(double))
        {
            System.Runtime.InteropServices.MemoryMarshal.Write(lane, in Unsafe.As<T, double>(ref value));
            return true;
        }

        if (typeof(T) == typeof(long))
        {
            System.Runtime.InteropServices.MemoryMarshal.Write(lane, in Unsafe.As<T, long>(ref value));
            return true;
        }

        if (typeof(T) == typeof(int))
        {
            System.Runtime.InteropServices.MemoryMarshal.Write(lane, in Unsafe.As<T, int>(ref value));
            return true;
        }

        if (typeof(T) == typeof(float))
        {
            System.Runtime.InteropServices.MemoryMarshal.Write(lane, in Unsafe.As<T, float>(ref value));
            return true;
        }

        if (typeof(T) == typeof(short))
        {
            System.Runtime.InteropServices.MemoryMarshal.Write(lane, in Unsafe.As<T, short>(ref value));
            return true;
        }

        if (typeof(T) == typeof(sbyte))
        {
            System.Runtime.InteropServices.MemoryMarshal.Write(lane, in Unsafe.As<T, sbyte>(ref value));
            return true;
        }

        if (typeof(T) == typeof(bool))
        {
            lane[0] = (byte)(Unsafe.As<T, bool>(ref value) ? 1 : 0);
            return true;
        }

        if (typeof(T) == typeof(decimal))
        {
            var refusal = ClrStorage.TryWriteDecimal(
                Unsafe.As<T, decimal>(ref value), format.Precision, format.Scale, lane);
            if (refusal is not null)
            {
                throw Unrepresentable(format, Unsafe.As<T, decimal>(ref value).ToString(CultureInfo.InvariantCulture), refusal);
            }

            return true;
        }

        if (typeof(T) == typeof(DateOnly))
        {
            var days = ClrStorage.DaysOf(Unsafe.As<T, DateOnly>(ref value));
            System.Runtime.InteropServices.MemoryMarshal.Write(lane, in days);
            return true;
        }

        if (typeof(T) == typeof(TimeOnly))
        {
            var time = Unsafe.As<T, TimeOnly>(ref value);
            if (time.Ticks % 10 != 0)
            {
                throw Unrepresentable(
                    format, time.ToString("O", CultureInfo.InvariantCulture), "which TIME cannot hold: it counts whole microseconds");
            }

            var micros = ClrStorage.MicrosOf(time);
            System.Runtime.InteropServices.MemoryMarshal.Write(lane, in micros);
            return true;
        }

        if (typeof(T) == typeof(DateTime))
        {
            var ticks = Unsafe.As<T, DateTime>(ref value).Ticks;
            var units = Units(ticks, format, Unsafe.As<T, DateTime>(ref value).ToString("O", CultureInfo.InvariantCulture));
            System.Runtime.InteropServices.MemoryMarshal.Write(lane, in units);
            return true;
        }

        if (typeof(T) == typeof(DateTimeOffset))
        {
            var instant = Unsafe.As<T, DateTimeOffset>(ref value);
            var units = Units(instant.UtcTicks, format, instant.ToString("O", CultureInfo.InvariantCulture));
            System.Runtime.InteropServices.MemoryMarshal.Write(lane, in units);
            return true;
        }

        if (typeof(T) == typeof(TimeSpan))
        {
            var interval = Unsafe.As<T, TimeSpan>(ref value);
            if (interval.Ticks % 10 != 0)
            {
                throw Unrepresentable(
                    format, interval.ToString("c", CultureInfo.InvariantCulture), "which INTERVAL_DAY cannot hold: it counts whole microseconds");
            }

            var micros = ClrStorage.MicrosOf(interval);
            System.Runtime.InteropServices.MemoryMarshal.Write(lane, in micros);
            return true;
        }

        if (typeof(T) == typeof(Guid))
        {
            SourceEncoding.WriteUuid(Unsafe.As<T, Guid>(ref value), lane);
            return true;
        }

        if (typeof(T) == typeof(double?))
        {
            return WriteLane(lane, Unsafe.As<T, double?>(ref value), format);
        }

        if (typeof(T) == typeof(long?))
        {
            return WriteLane(lane, Unsafe.As<T, long?>(ref value), format);
        }

        if (typeof(T) == typeof(int?))
        {
            return WriteLane(lane, Unsafe.As<T, int?>(ref value), format);
        }

        if (typeof(T) == typeof(float?))
        {
            return WriteLane(lane, Unsafe.As<T, float?>(ref value), format);
        }

        if (typeof(T) == typeof(short?))
        {
            return WriteLane(lane, Unsafe.As<T, short?>(ref value), format);
        }

        if (typeof(T) == typeof(sbyte?))
        {
            return WriteLane(lane, Unsafe.As<T, sbyte?>(ref value), format);
        }

        if (typeof(T) == typeof(bool?))
        {
            return WriteLane(lane, Unsafe.As<T, bool?>(ref value), format);
        }

        if (typeof(T) == typeof(decimal?))
        {
            return WriteLane(lane, Unsafe.As<T, decimal?>(ref value), format);
        }

        if (typeof(T) == typeof(DateOnly?))
        {
            return WriteLane(lane, Unsafe.As<T, DateOnly?>(ref value), format);
        }

        if (typeof(T) == typeof(TimeOnly?))
        {
            return WriteLane(lane, Unsafe.As<T, TimeOnly?>(ref value), format);
        }

        if (typeof(T) == typeof(DateTime?))
        {
            return WriteLane(lane, Unsafe.As<T, DateTime?>(ref value), format);
        }

        if (typeof(T) == typeof(DateTimeOffset?))
        {
            return WriteLane(lane, Unsafe.As<T, DateTimeOffset?>(ref value), format);
        }

        if (typeof(T) == typeof(TimeSpan?))
        {
            return WriteLane(lane, Unsafe.As<T, TimeSpan?>(ref value), format);
        }

        if (typeof(T) == typeof(Guid?))
        {
            return WriteLane(lane, Unsafe.As<T, Guid?>(ref value), format);
        }

        throw new UnsupportedFeatureException(
            $"a composite field of CLR type {typeof(T).Name}",
            "A composite value's fields are " + Spellings + ", or a nullable form of one "
            + "(docs/design/51-structured-function-results.md §1).");
    }

    /// <summary>The nullable forms: no value clears the lane.</summary>
    private static bool WriteLane<T>(Span<byte> lane, T? value, in LaneFormat format)
        where T : struct
    {
        if (!value.HasValue)
        {
            lane.Clear();
            return false;
        }

        return WriteLane(lane, value.GetValueOrDefault(), format);
    }

    /// <summary>
    /// A CLR tick count as a TIMESTAMP lane's units, refused when the declared precision cannot hold
    /// it exactly: a sub-millisecond tick under TIMESTAMP(3), a sub-microsecond one under
    /// TIMESTAMP(6), or an instant a nanosecond count cannot reach.
    /// </summary>
    private static long Units(long ticks, in LaneFormat format, string shown)
    {
        if (!ClrStorage.FitsUnit(ticks, format.Precision))
        {
            throw Unrepresentable(
                format,
                shown,
                $"which {IrTypes.Describe(format.Type.ToProto())} cannot hold: it counts whole "
                + $"{Unit(format.Precision)} since 1970-01-01, within what a 64-bit count of them reaches");
        }

        return ClrStorage.UnitsOf(ticks, format.Precision);
    }

    /// <summary>A value a delegate answered that its declared type cannot hold exactly (D298).</summary>
    private static InvalidOperationException Unrepresentable(in LaneFormat format, string value, string why) =>
        new($"{format.Owner} is {IrTypes.Describe(format.Type.ToProto())} and the delegate answered {value}, "
            + $"{why}. Round the value in the function, or declare a type that holds it.");

    /// <summary>Whether <typeparamref name="T"/> is written by appending rather than by lane.</summary>
    public static bool IsVariableLength<T>()
        where T : allows ref struct
        =>
        typeof(T) == typeof(ReadOnlySpan<byte>)
        || typeof(T) == typeof(string)
        || typeof(T) == typeof(Utf8String)
        || typeof(T) == typeof(Utf8String?)
        || typeof(T) == typeof(byte[])
        || typeof(T) == typeof(ReadOnlyMemory<byte>)
        || typeof(T) == typeof(ReadOnlyMemory<byte>?);

    /// <summary>Appends one variable-length value.</summary>
    /// <param name="writer">The result column.</param>
    /// <param name="length">Rows in this batch, for the message a refusal names.</param>
    /// <param name="row">The row being written, for the same reason.</param>
    /// <param name="value">What the delegate returned.</param>
    /// <param name="format">The result's declared type, which says whether a span's bytes are text.</param>
    public static void Append<T>(ColumnWriter writer, int length, int row, T value, in LaneFormat format)
        where T : allows ref struct
    {
        _ = length;
        if (typeof(T) == typeof(ReadOnlySpan<byte>))
        {
            // D304: the delegate's own bytes — a slice of its input, or of a buffer it owns — copied
            // into the result's data buffer before the next call, and validated where they are text.
            var bytes = Unsafe.As<T, ReadOnlySpan<byte>>(ref value);
            if (format.Type.Kind == TypeKind.String)
            {
                AppendUtf8(writer, bytes, row);
            }
            else
            {
                writer.AppendValue(bytes);
            }

            return;
        }

        if (typeof(T) == typeof(Utf8String))
        {
            // D146: the delegate's own bytes, copied straight into the result's data buffer. This is
            // the write half of the allocation-free lane, and the one place a Tier 1 result's bytes
            // are validated (§2) — an Arrow buffer never holds invalid UTF-8.
            AppendUtf8(writer, Unsafe.As<T, Utf8String>(ref value).AsSpan(), row);
            return;
        }

        if (typeof(T) == typeof(Utf8String?))
        {
            var nullable = Unsafe.As<T, Utf8String?>(ref value);
            if (nullable is { } text8)
            {
                AppendUtf8(writer, text8.AsSpan(), row);
            }
            else
            {
                writer.AppendNull();
            }

            return;
        }

        if (typeof(T) == typeof(ReadOnlyMemory<byte>))
        {
            // D298: BINARY's allocation-free spelling, copied into the result's data buffer.
            writer.AppendValue(Unsafe.As<T, ReadOnlyMemory<byte>>(ref value).Span);
            return;
        }

        if (typeof(T) == typeof(ReadOnlyMemory<byte>?))
        {
            var bytes = Unsafe.As<T, ReadOnlyMemory<byte>?>(ref value);
            if (bytes is { } present)
            {
                writer.AppendValue(present.Span);
            }
            else
            {
                writer.AppendNull();
            }

            return;
        }

        if (typeof(T) == typeof(byte[]))
        {
            var array = Unsafe.As<T, byte[]?>(ref value);
            if (array is null)
            {
                writer.AppendNull();
            }
            else
            {
                writer.AppendValue(array);
            }

            return;
        }

        if (typeof(T) == typeof(string))
        {
            // The second allocating step, and the reason a STRING-returning Tier 1 function written
            // in `string` is not on the zero-allocation path: a .NET string has to be encoded to
            // reach a UTF-8 buffer. A delegate answering a span or a Utf8String takes a branch above
            // and allocates nothing.
            var text = Unsafe.As<T, string?>(ref value);
            if (text is null)
            {
                writer.AppendNull();
            }
            else
            {
                writer.AppendValue(Encoding.UTF8.GetBytes(text));
            }

            return;
        }

        writer.AppendNull();
    }

    /// <summary>
    /// Appends validated UTF-8. The check is <c>Utf8.IsValid</c> — vectorised, allocation-free — and
    /// it is here rather than in <see cref="Utf8String"/>'s constructor because this is where bytes
    /// enter a column (§2).
    /// </summary>
    private static void AppendUtf8(ColumnWriter writer, ReadOnlySpan<byte> bytes, int row)
    {
        if (!Utf8String.IsValidUtf8(bytes))
        {
            throw new InvalidUtf8Exception("the text a Tier 1 function returned", row);
        }

        writer.AppendValue(bytes);
    }
}

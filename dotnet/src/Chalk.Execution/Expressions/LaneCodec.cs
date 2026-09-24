using System.Runtime.CompilerServices;
using System.Text;
using Chalk.Catalog;
using Chalk.Ir;
using Chalk.Sources;
using Type = System.Type;

namespace Chalk.Execution.Expressions;

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
/// The supported CLR types are the ones whose in-memory shape <em>is</em> the column's:
/// <c>bool</c> (a byte per lane), <c>sbyte</c>, <c>short</c>, <c>int</c>, <c>long</c>,
/// <c>float</c>, <c>double</c>, and their <c>Nullable</c> forms for a non-strict function; plus
/// <c>string</c>, which is the one that costs an allocation per row because a UTF-8 lane is not a
/// .NET string. A DECIMAL, a UUID or a LIST argument is refused rather than approximated.
/// </para>
/// </remarks>
internal static class LaneCodec
{
    /// <summary>
    /// The CLR type a delegate must use for <paramref name="type"/>, or null. A STRING is named as
    /// <c>string</c>, which is what <see cref="Describe"/> prints; <see cref="Accepts{T}"/> also
    /// takes <see cref="Utf8String"/> for it, which is the allocation-free spelling (D146).
    /// </summary>
    public static Type? ClrTypeOf(ChalkType type) => type.Kind switch
    {
        TypeKind.Bool => typeof(bool),
        TypeKind.I8 => typeof(sbyte),
        TypeKind.I16 => typeof(short),
        TypeKind.I32 or TypeKind.Date or TypeKind.IntervalYear => typeof(int),
        TypeKind.I64 or TypeKind.Time or TypeKind.Timestamp or TypeKind.TimestampTz
            or TypeKind.IntervalDay => typeof(long),
        TypeKind.Fp32 => typeof(float),
        TypeKind.Fp64 => typeof(double),
        TypeKind.String => typeof(string),
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

        var underlying = Nullable.GetUnderlyingType(actual);
        if (required == typeof(string)
            && (actual == typeof(Utf8String) || underlying == typeof(Utf8String)))
        {
            // D146: a STRING may be spelled Utf8String, which is a memory-backed slice of the lane
            // and allocates nothing. `string` stays supported and stays the allocating lane.
            return true;
        }

        return actual == required || underlying == required;
    }

    /// <summary>
    /// Explains the mismatch, naming both sides. Used by the engine-creation check, which is where a
    /// wrong delegate should be caught (§5's negative).
    /// </summary>
    public static string Describe(ChalkType type) =>
        ClrTypeOf(type) is { } clr
            ? clr == typeof(string)
                ? $"{IrTypes.Describe(type.ToProto())} (CLR Utf8String, or string)"
                : $"{IrTypes.Describe(type.ToProto())} (CLR {clr.Name})"
            : $"{IrTypes.Describe(type.ToProto())} (no Tier 1 CLR type; use a Tier 2 kernel)";

    /// <summary>Reads one lane. NULL becomes <c>default</c> for a value type and null for the rest.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T Read<T>(in ColumnView view, int row)
    {
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
        
        if (typeof(T) == typeof(string))
        {
            object value = Encoding.UTF8.GetString(view.VarValue(row));
            return (T)value;
        }

        return ReadNullable<T>(view, row);
    }

    /// <summary>The nullable forms, which is what a non-strict delegate is written in.</summary>
    private static T ReadNullable<T>(in ColumnView view, int row)
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

        throw new UnsupportedFeatureException(
            $"a Tier 1 argument of CLR type {typeof(T).Name}",
            "Tier 1 delegates take bool, sbyte, short, int, long, float, double, Utf8String, string "
            + "and their nullable forms; anything else needs a Tier 2 kernel");
    }

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
    {
        if (typeof(T) == typeof(double)
            || typeof(T) == typeof(long)
            || typeof(T) == typeof(int)
            || typeof(T) == typeof(float)
            || typeof(T) == typeof(short)
            || typeof(T) == typeof(sbyte)
            || typeof(T) == typeof(bool)
            || typeof(T) == typeof(Utf8String))
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

        return value is null;
    }

    /// <summary>
    /// Writes one lane of a fixed-width result. A NULL is left as a zeroed lane with its validity bit
    /// clear, which is what the caller has already arranged.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Write<T>(ColumnWriter writer, int length, int row, T value)
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

        WriteNullable(writer, length, row, value);
    }

    /// <summary>The forms the generic fast paths above do not cover, which is the error path.</summary>
    private static void WriteNullable<T>(ColumnWriter writer, int length, int row, T value)
    {
        switch (value)
        {
            case double d:
                writer.Values<double>(length)[row] = d;
                return;
            case long l:
                writer.Values<long>(length)[row] = l;
                return;
            case int i:
                writer.Values<int>(length)[row] = i;
                return;
            case float f:
                writer.Values<float>(length)[row] = f;
                return;
            case short s:
                writer.Values<short>(length)[row] = s;
                return;
            case sbyte b:
                writer.Values<sbyte>(length)[row] = b;
                return;
            case bool o:
                writer.Values<byte>(length)[row] = (byte)(o ? 1 : 0);
                return;
            case null:
                return;
            default:
                throw new UnsupportedFeatureException(
                    $"a Tier 1 result of CLR type {typeof(T).Name}",
                    "Tier 1 delegates return bool, sbyte, short, int, long, float, double, "
                    + "Utf8String, string and their nullable forms "
                    + "(docs/design/17-user-defined-functions.md §3).");
        }
    }

    /// <summary>
    /// Reads a value out of a raw lane, which is how the aggregate path sees one: the hash aggregate
    /// hands every accumulator the argument's bytes rather than a column view.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T ReadRaw<T>(ReadOnlySpan<byte> lane)
    {
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

        throw new UnsupportedFeatureException(
            $"a Tier 1 aggregate over CLR type {typeof(T).Name}",
            "A Tier 1 aggregate takes bool, sbyte, short, int, long, float or double "
            + "(docs/design/17-user-defined-functions.md §3).");
    }

    /// <summary>
    /// Writes a value into a raw lane, and says whether it was a value at all. A null answer leaves
    /// the lane zeroed, which is what an aggregate with no rows produces.
    /// </summary>
    public static bool WriteRaw<T>(Span<byte> lane, T value)
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
            default:
                throw new UnsupportedFeatureException(
                    $"a Tier 1 aggregate returning CLR type {typeof(T).Name}",
                    "A Tier 1 aggregate returns bool, sbyte, short, int, long, float or double, or "
                    + "a nullable form of one (docs/design/17-user-defined-functions.md §3).");
        }
    }

    /// <summary>
    /// Writes one fixed-width value into a raw lane without boxing it, and says whether it was a value
    /// at all — how a struct field reaches an aggregate's output copier (D294). A NULL clears the lane.
    /// Every branch is a <c>typeof</c> test the JIT folds away per instantiation.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool WriteLane<T>(Span<byte> lane, T value)
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

        if (typeof(T) == typeof(double?))
        {
            return WriteLane(lane, Unsafe.As<T, double?>(ref value));
        }

        if (typeof(T) == typeof(long?))
        {
            return WriteLane(lane, Unsafe.As<T, long?>(ref value));
        }

        if (typeof(T) == typeof(int?))
        {
            return WriteLane(lane, Unsafe.As<T, int?>(ref value));
        }

        if (typeof(T) == typeof(float?))
        {
            return WriteLane(lane, Unsafe.As<T, float?>(ref value));
        }

        if (typeof(T) == typeof(short?))
        {
            return WriteLane(lane, Unsafe.As<T, short?>(ref value));
        }

        if (typeof(T) == typeof(sbyte?))
        {
            return WriteLane(lane, Unsafe.As<T, sbyte?>(ref value));
        }

        if (typeof(T) == typeof(bool?))
        {
            return WriteLane(lane, Unsafe.As<T, bool?>(ref value));
        }

        throw new UnsupportedFeatureException(
            $"a struct field of CLR type {typeof(T).Name}",
            "A struct's fields are bool, sbyte, short, int, long, float, double, Utf8String or "
            + "string, or a nullable form of one (docs/design/51-structured-function-results.md §1).");
    }

    /// <summary>The nullable forms: no value clears the lane.</summary>
    private static bool WriteLane<T>(Span<byte> lane, T? value)
        where T : struct
    {
        if (!value.HasValue)
        {
            lane.Clear();
            return false;
        }

        return WriteLane(lane, value.GetValueOrDefault());
    }

    /// <summary>Whether <typeparamref name="T"/> is written by appending rather than by lane.</summary>
    public static bool IsVariableLength<T>() =>
        typeof(T) == typeof(string)
        || typeof(T) == typeof(Utf8String)
        || typeof(T) == typeof(Utf8String?);

    /// <summary>Appends one variable-length value.</summary>
    /// <param name="writer">The result column.</param>
    /// <param name="length">Rows in this batch, for the message a refusal names.</param>
    /// <param name="row">The row being written, for the same reason.</param>
    /// <param name="value">What the delegate returned.</param>
    public static void Append<T>(ColumnWriter writer, int length, int row, T value)
    {
        _ = length;
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

        if (value is string text)
        {
            // The second allocating step, and the reason a STRING-returning Tier 1 function written
            // in `string` is not on the zero-allocation path: a .NET string has to be encoded to
            // reach a UTF-8 buffer. A delegate declared in Utf8String takes the branch above and
            // allocates nothing.
            writer.AppendValue(Encoding.UTF8.GetBytes(text));
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
            throw new InvalidUtf8Exception("the Utf8String a Tier 1 function returned", row);
        }

        writer.AppendValue(bytes);
    }
}

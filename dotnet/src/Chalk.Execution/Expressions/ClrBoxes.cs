using System.Text;
using System.Runtime.InteropServices;
using Chalk.Catalog;
using Chalk.Sources;
using Type = System.Type;

namespace Chalk.Execution.Expressions;

/// <summary>
/// The widened Tier 1 spellings (D298) for the paths that work in boxed values: the reference
/// executor, which calls a delegate with boxed arguments and compares boxed answers (D13), and a
/// table function, whose producer is invoked and whose rows are read by reflection.
/// </summary>
/// <remarks>
/// <para>
/// Both paths hold a value in the <em>storage</em> vocabulary: an exact integer or a temporal as a
/// <c>long</c> count (a <see cref="ScalarValue"/> gives an <c>int</c> for a DATE, which is read as
/// well), a DECIMAL as a <c>decimal</c>, a UUID as its sixteen bytes and a BINARY as a byte array.
/// A delegate is written in its own CLR spelling, so a value crosses here on the way in and on the
/// way out.
/// </para>
/// <para>
/// The way out is <see cref="LaneCodec.WriteLane{T}"/> itself, into a lane on the stack and read
/// back: a value the vectorised engine refuses is refused here in the same words, and one it
/// accepts reads back exactly as its lane holds it. Boxing is what these paths are allowed.
/// </para>
/// </remarks>
internal static class ClrBoxes
{
    /// <summary>
    /// A boxed storage value as the CLR type <paramref name="clr"/> a delegate or a producer takes
    /// for a value of <paramref name="type"/>. Null stays null; a type with nothing to convert is
    /// handed over as it is.
    /// </summary>
    public static object? ToClr(object? storage, ChalkType type, Type clr)
    {
        if (storage is null)
        {
            return null;
        }

        var target = Nullable.GetUnderlyingType(clr) ?? clr;
        if (target == typeof(DateOnly))
        {
            return storage is DateOnly ? storage : ClrStorage.DateOf(Convert.ToInt32(storage, null));
        }

        if (target == typeof(TimeOnly))
        {
            return storage is TimeOnly ? storage : ClrStorage.TimeOf(Convert.ToInt64(storage, null));
        }

        if (target == typeof(DateTime))
        {
            return storage is DateTime
                ? storage
                : ClrStorage.DateTimeOf(Convert.ToInt64(storage, null), type.Precision);
        }

        if (target == typeof(DateTimeOffset))
        {
            return storage is DateTimeOffset
                ? storage
                : ClrStorage.InstantOf(Convert.ToInt64(storage, null), type.Precision);
        }

        if (target == typeof(TimeSpan))
        {
            return storage is TimeSpan ? storage : ClrStorage.IntervalOf(Convert.ToInt64(storage, null));
        }

        if (target == typeof(Guid))
        {
            return storage switch
            {
                Guid => storage,
                byte[] bytes => ClrStorage.UuidOf(bytes),
                ReadOnlyMemory<byte> memory => ClrStorage.UuidOf(memory.Span),
                _ => storage,
            };
        }

        if (target == typeof(ReadOnlySpan<byte>))
        {
            // D304: a span cannot be boxed, so the reference path hands the delegate the bytes as an
            // array and BoxedLanes.Cast makes the span over them — a per-call allocation this path
            // is allowed and the vectorised one never makes.
            return storage switch
            {
                string text => Encoding.UTF8.GetBytes(text),
                ReadOnlyMemory<byte> memory => memory.ToArray(),
                Utf8String text => text.ToArray(),
                _ => storage,
            };
        }

        if (target == typeof(ReadOnlyMemory<byte>))
        {
            return storage is byte[] bytes ? new ReadOnlyMemory<byte>(bytes) : storage;
        }

        if (target == typeof(byte[]))
        {
            return storage is ReadOnlyMemory<byte> memory ? memory.ToArray() : storage;
        }

        if (target == typeof(bool) && storage is byte flag)
        {
            // A ScalarValue holds a BOOL as its lane's byte.
            return flag != 0;
        }

        // The exact integers a delegate may be written in, from the long the reference holds them as.
        if (target == typeof(int) && storage is long or short or sbyte)
        {
            return Convert.ToInt32(storage, null);
        }

        if (target == typeof(short) && storage is long or int or sbyte)
        {
            return Convert.ToInt16(storage, null);
        }

        if (target == typeof(sbyte) && storage is long or int or short)
        {
            return Convert.ToSByte(storage, null);
        }

        return storage;
    }

    /// <summary>
    /// A delegate's boxed answer in the storage vocabulary, refused exactly as the vectorised engine
    /// refuses it when <paramref name="format"/>'s type cannot hold it.
    /// </summary>
    public static object? ToStorage(object? value, in LaneFormat format)
    {
        Span<byte> lane = stackalloc byte[16];
        lane.Clear();
        switch (value)
        {
            case null:
                return null;
            case Utf8String text:
                return text.ToString();
            case byte[] bytes when format.Type.Kind == Ir.TypeKind.String:
                // D304: a span a delegate answered for a STRING, boxed as its bytes.
                return Encoding.UTF8.GetString(bytes);
            case sbyte v:
                return (long)v;
            case short v:
                return (long)v;
            case int v:
                return (long)v;
            case DateOnly date:
                _ = LaneCodec.WriteLane(lane, date, format);
                return (long)MemoryMarshal.Read<int>(lane);
            case TimeOnly time:
                _ = LaneCodec.WriteLane(lane, time, format);
                return MemoryMarshal.Read<long>(lane);
            case DateTime stamp:
                _ = LaneCodec.WriteLane(lane, stamp, format);
                return MemoryMarshal.Read<long>(lane);
            case DateTimeOffset instant:
                _ = LaneCodec.WriteLane(lane, instant, format);
                return MemoryMarshal.Read<long>(lane);
            case TimeSpan interval:
                _ = LaneCodec.WriteLane(lane, interval, format);
                return MemoryMarshal.Read<long>(lane);
            case decimal number:
                _ = LaneCodec.WriteLane(lane, number, format);
                return ClrStorage.DecimalOf(lane, format.Scale);
            case Guid uuid:
                _ = LaneCodec.WriteLane(lane, uuid, format);
                return lane.ToArray();
            case ReadOnlyMemory<byte> bytes:
                return bytes.ToArray();
            default:
                return value;
        }
    }

    /// <summary>
    /// One boxed CLR value into a fixed-width lane of <paramref name="format"/>'s type, through the
    /// same conversions as a delegate's answer. False when the value was none of the widened
    /// spellings, which the caller writes itself.
    /// </summary>
    public static bool TryWriteLane(Span<byte> lane, object value, in LaneFormat format)
    {
        switch (value)
        {
            case decimal number:
                _ = LaneCodec.WriteLane(lane, number, format);
                return true;
            case DateOnly date:
                _ = LaneCodec.WriteLane(lane, date, format);
                return true;
            case TimeOnly time:
                _ = LaneCodec.WriteLane(lane, time, format);
                return true;
            case DateTime stamp:
                _ = LaneCodec.WriteLane(lane, stamp, format);
                return true;
            case DateTimeOffset instant:
                _ = LaneCodec.WriteLane(lane, instant, format);
                return true;
            case TimeSpan interval:
                _ = LaneCodec.WriteLane(lane, interval, format);
                return true;
            case Guid uuid:
                _ = LaneCodec.WriteLane(lane, uuid, format);
                return true;
            default:
                return false;
        }
    }
}

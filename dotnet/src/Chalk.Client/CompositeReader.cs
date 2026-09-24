using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Apache.Arrow;
using Apache.Arrow.Types;
using Chalk.Catalog;
using Chalk.Execution.Expressions;
using Chalk.Ir;
using Chalk.Sources;
using ChalkType = Chalk.Catalog.ChalkType;
using Type = System.Type;

namespace Chalk.Arrow;

/// <summary>
/// The typed read back of one composite cell (D301): a row of an Arrow <see cref="StructArray"/> as
/// the host's own record <typeparamref name="T"/>, bound once per (<typeparamref name="T"/>, struct
/// type) and cached.
/// </summary>
/// <remarks>
/// <para>
/// <typeparamref name="T"/> is read as the registration check reads a function's record (D294): its
/// public readable instance properties, in a positional record's constructor order. A positional
/// record is constructed through that constructor; any other type through its public parameterless
/// constructor and settable properties. Each of those members is matched to the struct's child field
/// of the same name, ignoring case, and must read that field's type as a delegate's parameter would
/// (D298's Tier 1, <see cref="LaneCodec.Accepts(Type, ChalkType)"/>): a <c>decimal</c> reads any DECIMAL
/// of up to 28 digits, a <c>DateTime</c> any TIMESTAMP, a <c>Utf8String</c> or a <c>string</c> a STRING.
/// A nullable field is read only into a member that can hold its NULL. A field the record does not
/// name is left unread. Anything else is refused on the first call, naming both sides.
/// </para>
/// <para>
/// The binding is compiled — one expression tree per (<typeparamref name="T"/>, struct type) — and
/// cached per process: an extension method has no engine in hand, so the cache is the client
/// library's. It is keyed weakly by the Arrow type object, so a host's schemas are never held, and by
/// the struct's shape behind that, so the next batch's equal type binds nothing new.
/// </para>
/// <para>
/// A read allocates nothing but what <typeparamref name="T"/> itself costs: the cache is asked by
/// reference, the fields are read from the child buffers directly, a <c>Utf8String</c> or a
/// <c>ReadOnlyMemory&lt;byte&gt;</c> is a slice of the batch's own memory (valid while the batch is),
/// and a record struct is built on the stack. A <c>string</c> or a <c>byte[]</c> field allocates, as it
/// does everywhere else, and so does a record class.
/// </para>
/// </remarks>
internal sealed class CompositeReader<T>
{
    private static readonly ConditionalWeakTable<StructType, CompositeReader<T>> ByType = new();

    private static readonly ConcurrentDictionary<string, Lazy<CompositeReader<T>>> ByShape =
        new(StringComparer.Ordinal);

    private static int _bindings;

    private readonly Func<ArrayData[], int, T> _read;
    private readonly bool _holdsNull;

    private CompositeReader(Func<ArrayData[], int, T> read, bool holdsNull)
    {
        _read = read;
        _holdsNull = holdsNull;
    }

    /// <summary>How many bindings have been compiled for <typeparamref name="T"/>: one per struct shape. Tests read it.</summary>
    internal static int Bindings => Volatile.Read(ref _bindings);

    /// <summary>The reader for <paramref name="composite"/>'s type, bound on the first call and cached.</summary>
    public static CompositeReader<T> For(StructArray composite)
    {
        var type = (StructType)composite.Data.DataType;
        if (ByType.TryGetValue(type, out var reader))
        {
            return reader;
        }

        // A type object not seen before: its shape may be — the next batch's equal type is.
        reader = ByShape
            .GetOrAdd(Shape(type), static (_, t) => new Lazy<CompositeReader<T>>(() => Bind(t)), type)
            .Value;
        ByType.AddOrUpdate(type, reader);
        return reader;
    }

    /// <summary>The value in row <paramref name="index"/>, which holds a composite.</summary>
    public T Read(StructArray composite, int index) =>
        _read(composite.Data.Children, composite.Data.Offset + index);

    /// <summary>What a NULL composite reads as: null, when <typeparamref name="T"/> can hold one.</summary>
    public T Null(int index) => _holdsNull
        ? default!
        : throw new InvalidOperationException(
            $"Row {index} holds a NULL composite, which {CompositeInference.Describe(typeof(T))} cannot "
            + $"hold. Read it with TryGetComposite, or as {typeof(T).Name}?.");

    // ---------------------------------------------------------------- binding

    private static CompositeReader<T> Bind(StructType type)
    {
        Interlocked.Increment(ref _bindings);

        var target = typeof(T);
        var underlying = Nullable.GetUnderlyingType(target);
        var record = underlying ?? target;
        var holdsNull = underlying is not null || !target.IsValueType;

        // Two fields one name ignoring case are one field to SQL, and a member could not say which.
        for (var f = 0; f < type.Fields.Count; f++)
        {
            for (var g = 0; g < f; g++)
            {
                if (string.Equals(type.Fields[f].Name, type.Fields[g].Name, StringComparison.OrdinalIgnoreCase))
                {
                    throw Refused(
                        record,
                        $"struct<{string.Join(", ", type.Fields.Select(field => field.Name))}>",
                        $"its fields '{type.Fields[g].Name}' and '{type.Fields[f].Name}' are one name ignoring "
                        + "case, which is how a field is matched, so no member can say which it reads");
                }
            }
        }

        ChalkType declared;
        try
        {
            // The composite's own nullability is the cell's, which the read checks; the fields' is theirs.
            declared = ArrowTypeMapping.FromArrow(type, nullable: false);
        }
        catch (UnsupportedFeatureException unsupported)
        {
            throw Refused(record, type.Name, $"a field of it has no Chalk type ({unsupported.Message})");
        }

        var described = IrTypes.Describe(declared.ToProto());
        if (!CompositeInference.IsCandidate(record))
        {
            throw Refused(
                record,
                described,
                $"{CompositeInference.Describe(record)} is not a record a composite value can be read into");
        }

        var children = Expression.Parameter(typeof(ArrayData[]), "children");
        var row = Expression.Parameter(typeof(int), "row");
        Expression Member(string name, Type clr) => Read(type, declared, described, record, name, clr, children, row);

        Expression built;
        var properties = CompositeInference.Properties(record);
        if (CompositeInference.PositionalConstructor(record) is { } constructor)
        {
            built = Expression.New(
                constructor,
                constructor.GetParameters().Select(p => Member(p.Name!, p.ParameterType)));
        }
        else
        {
            var settable = properties.Where(p => p.SetMethod is { IsPublic: true }).ToArray();
            if (settable.Length == 0 || (!record.IsValueType && record.GetConstructor(Type.EmptyTypes) is null))
            {
                throw Refused(
                    record,
                    described,
                    $"{CompositeInference.Describe(record)} can be built neither through a constructor whose "
                    + "parameters are its properties nor through a public parameterless constructor and "
                    + "settable properties");
            }

            built = Expression.MemberInit(
                Expression.New(record),
                settable.Select(p => (MemberBinding)Expression.Bind(p, Member(p.Name, p.PropertyType))));
        }

        var body = built.Type == target ? built : Expression.Convert(built, target);
        var read = Expression.Lambda<Func<ArrayData[], int, T>>(body, children, row).Compile();
        return new CompositeReader<T>(read, holdsNull);
    }

    /// <summary>The expression that reads the field <paramref name="name"/> names as <paramref name="clr"/>.</summary>
    private static Expression Read(
        StructType type,
        ChalkType declared,
        string described,
        Type record,
        string name,
        Type clr,
        ParameterExpression children,
        ParameterExpression row)
    {
        // No two fields share a name ignoring case (refused above), so at most one matches.
        var index = -1;
        for (var f = 0; f < type.Fields.Count && index < 0; f++)
        {
            if (string.Equals(type.Fields[f].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                index = f;
            }
        }

        if (index < 0)
        {
            throw Refused(
                record,
                described,
                $"'{name}' of {CompositeInference.Describe(record)} names none of its fields");
        }

        var field = declared.Fields[index];
        if (field.Type.Kind == TypeKind.Decimal && field.Type.Precision > LaneCodec.DecimalDigits)
        {
            throw Refused(
                record,
                described,
                $"field '{field.Name}' is {IrTypes.Describe(field.Type.ToProto())}, which a CLR decimal cannot "
                + $"hold (it holds {LaneCodec.DecimalDigits} digits), and '{name}' of "
                + $"{CompositeInference.Describe(record)} is {CompositeInference.Describe(clr)}; read this field "
                + "from its own Arrow array");
        }

        if (!LaneCodec.Accepts(clr, field.Type))
        {
            throw Refused(
                record,
                described,
                $"field '{field.Name}' is {LaneCodec.Describe(field.Type)} and '{name}' of "
                + $"{CompositeInference.Describe(record)} is {CompositeInference.Describe(clr)}, which does not read it");
        }

        if (field.Type.Nullable && clr.IsValueType && Nullable.GetUnderlyingType(clr) is null)
        {
            throw Refused(
                record,
                described,
                $"field '{field.Name}' is nullable and '{name}' of {CompositeInference.Describe(record)} is "
                + $"{CompositeInference.Describe(clr)}, which cannot hold its NULL; declare it {clr.Name}?");
        }

        var reader = FieldReaders.Create(clr, field.Type, type.Fields[index].DataType);
        return Expression.Call(
            Expression.Constant(reader),
            reader.GetType().GetMethod(nameof(FieldReader<int>.Read))!,
            Expression.ArrayIndex(children, Expression.Constant(index)),
            row);
    }

    private static InvalidOperationException Refused(Type record, string described, string why) =>
        new($"{CompositeInference.Describe(typeof(T))} cannot be read from the composite {described}: {why}.");

    /// <summary>
    /// The struct's shape, as far as a binding depends on it: each field's name, physical Arrow type
    /// and nullability. Two type objects of one shape share a binding.
    /// </summary>
    private static string Shape(StructType type)
    {
        var shape = new StringBuilder();
        foreach (var field in type.Fields)
        {
            shape.Append(field.Name).Append(':').Append(Physical(field.DataType))
                .Append(field.IsNullable ? "?" : string.Empty).Append('|');
        }

        return shape.ToString();
    }

    private static string Physical(IArrowType type) => type switch
    {
        Decimal128Type d => $"decimal128({d.Precision},{d.Scale})",
        TimestampType t => $"timestamp({t.Unit},{t.Timezone})",
        Time64Type t => $"time64({t.Unit})",
        Time32Type t => $"time32({t.Unit})",
        DurationType d => $"duration({d.Unit})",
        FixedSizeBinaryType b => $"fixed({b.ByteWidth})",
        StructType s => $"struct<{Shape(s)}>",
        ListType l => $"list<{Physical(l.ValueDataType)}>",
        _ => type.Name,
    };
}

/// <summary>One field of a composite, read from its child array's buffers as <typeparamref name="TValue"/>.</summary>
/// <remarks>
/// <paramref name="row"/> in <see cref="Read"/> is the child's own logical row — the composite's
/// offset added, as Arrow applies a struct's offset to its children — and the child's own offset is
/// added here.
/// </remarks>
internal abstract class FieldReader<TValue>
{
    public abstract TValue Read(ArrayData data, int row);

    protected static bool IsNull(ArrayData data, int row)
    {
        if (data.NullCount == 0)
        {
            return false;
        }

        var bits = data.Buffers[0].Span;
        return !bits.IsEmpty && !BitUtility.GetBit(bits, data.Offset + row);
    }

    protected static ReadOnlySpan<byte> Lane(ArrayData data, int row, int width) =>
        data.Buffers[1].Span.Slice((data.Offset + row) * width, width);

    /// <summary>A variable-length value's bytes, in the classic layout or the view layout.</summary>
    protected static ReadOnlyMemory<byte> Bytes(ArrayData data, int row, bool views)
    {
        var position = data.Offset + row;
        if (!views)
        {
            var offsets = MemoryMarshal.Cast<byte, int>(data.Buffers[1].Span);
            var start = offsets[position];
            return data.Buffers[2].Memory.Slice(start, offsets[position + 1] - start);
        }

        // Arrow's view layout: a 16-byte view per row, the length first; twelve bytes or fewer are
        // inline after it, and a longer value is (prefix, buffer index, offset) into a data buffer.
        var viewAt = position * 16;
        var view = data.Buffers[1].Span.Slice(viewAt, 16);
        var length = BinaryPrimitives.ReadInt32LittleEndian(view);
        if (length <= 12)
        {
            return data.Buffers[1].Memory.Slice(viewAt + 4, length);
        }

        var buffer = BinaryPrimitives.ReadInt32LittleEndian(view[8..]);
        var offset = BinaryPrimitives.ReadInt32LittleEndian(view[12..]);
        return data.Buffers[2 + buffer].Memory.Slice(offset, length);
    }
}

/// <summary>The readers, one per (CLR spelling, Chalk type) pair <see cref="LaneCodec.Accepts(Type, ChalkType)"/> allows.</summary>
internal static class FieldReaders
{
    public static object Create(Type clr, ChalkType field, IArrowType arrow)
    {
        var underlying = Nullable.GetUnderlyingType(clr);
        var value = underlying ?? clr;
        object reader = field.Kind switch
        {
            TypeKind.Bool => new BoolReader(),
            TypeKind.I8 => new FixedReader<sbyte>(),
            TypeKind.I16 => new FixedReader<short>(),
            TypeKind.I32 => new FixedReader<int>(),
            TypeKind.I64 => new FixedReader<long>(),
            TypeKind.Fp32 => new FixedReader<float>(),
            TypeKind.Fp64 => new FixedReader<double>(),
            TypeKind.Decimal => new DecimalReader((int)field.Scale),
            TypeKind.String when value == typeof(Utf8String) => new Utf8Reader(arrow is StringViewType),
            TypeKind.String => new StringReader(arrow is StringViewType),
            TypeKind.Binary when value == typeof(ReadOnlyMemory<byte>) => new BinaryMemoryReader(),
            TypeKind.Binary => new BinaryArrayReader(),
            TypeKind.Date when value == typeof(DateOnly) => new DateReader(),
            TypeKind.Date => new FixedReader<int>(),
            TypeKind.Time when value == typeof(TimeOnly) => new TimeReader(MicrosDivisor(arrow)),
            TypeKind.Time => new MicrosReader(MicrosDivisor(arrow), 1),
            TypeKind.Timestamp when value == typeof(DateTime) => new DateTimeReader((int)field.Precision),
            TypeKind.TimestampTz when value == typeof(DateTimeOffset) => new InstantReader((int)field.Precision),
            TypeKind.Timestamp or TypeKind.TimestampTz => new FixedReader<long>(),
            TypeKind.IntervalDay when value == typeof(TimeSpan) =>
                new IntervalReader(MicrosDivisor(arrow), MicrosMultiplier(arrow)),
            TypeKind.IntervalDay => new MicrosReader(MicrosDivisor(arrow), MicrosMultiplier(arrow)),
            TypeKind.IntervalYear => new FixedReader<int>(),
            TypeKind.Uuid => new UuidReader(),
            _ => throw new InvalidOperationException($"no reader for a {field.Kind} field."),
        };

        return underlying is null
            ? reader
            : Activator.CreateInstance(typeof(NullableReader<>).MakeGenericType(underlying), reader)!;
    }

    /// <summary>What divides a TIME's or a DURATION's count into microseconds: 1000 for nanoseconds.</summary>
    private static long MicrosDivisor(IArrowType arrow) => arrow switch
    {
        Time64Type { Unit: TimeUnit.Nanosecond } => 1000,
        DurationType { Unit: TimeUnit.Nanosecond } => 1000,
        _ => 1,
    };

    /// <summary>What multiplies a DURATION's count into microseconds: seconds and milliseconds.</summary>
    private static long MicrosMultiplier(IArrowType arrow) => arrow switch
    {
        DurationType { Unit: TimeUnit.Second } => 1_000_000,
        DurationType { Unit: TimeUnit.Millisecond } => 1000,
        _ => 1,
    };
}

internal sealed class NullableReader<TValue>(FieldReader<TValue> inner) : FieldReader<TValue?>
    where TValue : struct
{
    public override TValue? Read(ArrayData data, int row) => IsNull(data, row) ? null : inner.Read(data, row);
}

internal sealed class FixedReader<TValue> : FieldReader<TValue>
    where TValue : unmanaged
{
    public override TValue Read(ArrayData data, int row) =>
        MemoryMarshal.Read<TValue>(Lane(data, row, Unsafe.SizeOf<TValue>()));
}

internal sealed class BoolReader : FieldReader<bool>
{
    public override bool Read(ArrayData data, int row) =>
        BitUtility.GetBit(data.Buffers[1].Span, data.Offset + row);
}

internal sealed class DecimalReader(int scale) : FieldReader<decimal>
{
    public override decimal Read(ArrayData data, int row) => ClrStorage.DecimalOf(Lane(data, row, 16), scale);
}

internal sealed class UuidReader : FieldReader<Guid>
{
    public override Guid Read(ArrayData data, int row) => ClrStorage.UuidOf(Lane(data, row, 16));
}

internal sealed class DateReader : FieldReader<DateOnly>
{
    public override DateOnly Read(ArrayData data, int row) =>
        ClrStorage.DateOf(MemoryMarshal.Read<int>(Lane(data, row, 4)));
}

/// <summary>A count in the Arrow type's unit, as the microseconds a TIME or an INTERVAL_DAY holds.</summary>
internal sealed class MicrosReader(long divisor, long multiplier) : FieldReader<long>
{
    public override long Read(ArrayData data, int row) =>
        Micros(MemoryMarshal.Read<long>(Lane(data, row, 8)), divisor, multiplier);

    /// <summary>
    /// A count as microseconds: multiplied up from a coarser unit, or divided down from nanoseconds
    /// to the microsecond at or before it, as a nanosecond instant floors to its tick.
    /// </summary>
    internal static long Micros(long count, long divisor, long multiplier)
    {
        if (divisor == 1)
        {
            return count * multiplier;
        }

        var (quotient, remainder) = Math.DivRem(count, divisor);
        return remainder < 0 ? quotient - 1 : quotient;
    }
}

internal sealed class TimeReader(long divisor) : FieldReader<TimeOnly>
{
    public override TimeOnly Read(ArrayData data, int row) =>
        ClrStorage.TimeOf(MicrosReader.Micros(MemoryMarshal.Read<long>(Lane(data, row, 8)), divisor, 1));
}

internal sealed class IntervalReader(long divisor, long multiplier) : FieldReader<TimeSpan>
{
    public override TimeSpan Read(ArrayData data, int row) =>
        ClrStorage.IntervalOf(
            MicrosReader.Micros(MemoryMarshal.Read<long>(Lane(data, row, 8)), divisor, multiplier));
}

internal sealed class DateTimeReader(int precision) : FieldReader<DateTime>
{
    public override DateTime Read(ArrayData data, int row) =>
        ClrStorage.DateTimeOf(MemoryMarshal.Read<long>(Lane(data, row, 8)), precision);
}

internal sealed class InstantReader(int precision) : FieldReader<DateTimeOffset>
{
    public override DateTimeOffset Read(ArrayData data, int row) =>
        ClrStorage.InstantOf(MemoryMarshal.Read<long>(Lane(data, row, 8)), precision);
}

internal sealed class Utf8Reader(bool views) : FieldReader<Utf8String>
{
    public override Utf8String Read(ArrayData data, int row) => new(Bytes(data, row, views));
}

internal sealed class StringReader(bool views) : FieldReader<string?>
{
    public override string? Read(ArrayData data, int row) =>
        IsNull(data, row) ? null : Encoding.UTF8.GetString(Bytes(data, row, views).Span);
}

internal sealed class BinaryMemoryReader : FieldReader<ReadOnlyMemory<byte>>
{
    public override ReadOnlyMemory<byte> Read(ArrayData data, int row) => Bytes(data, row, views: false);
}

internal sealed class BinaryArrayReader : FieldReader<byte[]?>
{
    public override byte[]? Read(ArrayData data, int row) =>
        IsNull(data, row) ? null : Bytes(data, row, views: false).ToArray();
}

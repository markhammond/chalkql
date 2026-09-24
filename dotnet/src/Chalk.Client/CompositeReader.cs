using System.Buffers;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
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
/// The typed read back of composite cells (D301): rows of an Arrow <see cref="StructArray"/> as the
/// host's own record <typeparamref name="T"/>, bound once per (<typeparamref name="T"/>, struct type),
/// cached, and read a batch at a time from the child arrays' own buffers.
/// </summary>
/// <remarks>
/// <para>
/// <b>What <typeparamref name="T"/> is.</b> <typeparamref name="T"/> is read as the registration
/// check reads a function's record (D294): its public readable instance properties, in a positional
/// record's constructor order. A positional record is constructed through that constructor; any
/// other type through its public parameterless constructor and settable properties. Each of those
/// members is matched to the struct's child field of the same name, ignoring case, and must read that
/// field's type as a delegate's parameter would (D298's Tier 1, <see cref="LaneCodec.Accepts(Type,
/// ChalkType)"/>). A nullable field is read only into a member that can hold its NULL. A field the
/// record does not name is left unread. Anything else is refused on the first call, naming both sides.
/// </para>
/// <para>
/// <b>The reader.</b> A read attaches one cursor per member to its child array: the cursor pins the
/// child's validity bitmap and its values (or offsets, or views) for the length of the read, applies
/// the child's offset and the struct's own — Arrow applies a struct's offset to its children — and
/// tests validity by bit. A cursor is a small sealed class the compiled binder calls; the struct's
/// own validity is one more, and a row whose composite is NULL is never constructed. The binder is an
/// expression tree compiled once per binding: it takes the cursors as an array, casts each to its own
/// type once per call, and then loops over the rows constructing <typeparamref name="T"/> from the
/// cursors' reads. It writes into an array because an expression tree cannot hold a
/// <see cref="Span{T}"/> — a ref struct, which neither a local nor a parameter of one can be — nor a
/// pointer, which is also why the cursors are classes the tree calls rather than structs it would copy
/// on every call. The array is the batch state's own, reused for every read, and one
/// <see cref="Span{T}.CopyTo"/> moves each chunk into the caller's span; so the delegate call, the
/// casts and the pinning are paid once per chunk, never per row.
/// </para>
/// <para>
/// <b>The seam.</b> The cursors and the batch state do not depend on how the binder was made. A host
/// compiled ahead of time, where expression trees are interpreted, can be given a binder a source
/// generator wrote — the same <see cref="Fill"/> shape over the same cursors — without anything else
/// changing.
/// </para>
/// <para>
/// <b>The cache.</b> An extension method has no engine in hand, so the cache is the client library's,
/// per process: weakly by the Arrow type object, so a host's schemas are never held, and by the
/// struct's shape behind that, so the next batch's equal type binds nothing new. The batch state — the
/// cursors and the array — is pooled one deep per binding, taken atomically, so two concurrent reads
/// never share one: whoever loses the exchange builds its own.
/// </para>
/// </remarks>
internal sealed class CompositeReader<T>
{
    /// <summary>Rows one call of the binder writes: the batch state's own array, reused.</summary>
    internal const int Chunk = 1024;

    private static readonly ConditionalWeakTable<StructType, CompositeReader<T>> ByType = new();

    private static readonly ConcurrentDictionary<string, Lazy<CompositeReader<T>>> ByShape =
        new(StringComparer.Ordinal);

    private static int _bindings;

    private readonly Func<FieldCursor>[] _cursors;
    private readonly int[] _fields;
    private readonly Fill _fill;
    private readonly bool _holdsNull;
    private Batch? _spare;

    /// <summary>
    /// The binder: <paramref name="count"/> rows from <paramref name="first"/>, each constructed from the
    /// attached <paramref name="cursors"/> where <paramref name="composite"/> says the row holds a
    /// composite, and the default where it does not, written into <paramref name="into"/> from 0.
    /// </summary>
    internal delegate void Fill(FieldCursor[] cursors, ValidityCursor composite, T[] into, int first, int count);

    private CompositeReader(Func<FieldCursor>[] cursors, int[] fields, Fill fill, bool holdsNull)
    {
        _cursors = cursors;
        _fields = fields;
        _fill = fill;
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

    /// <summary>
    /// Rows <paramref name="first"/> onwards into <paramref name="into"/>, one per element. A NULL
    /// composite reads as the default — null for a reference type or a <c>Nullable</c> — and is refused
    /// for any other <typeparamref name="T"/>, naming the row.
    /// </summary>
    public void Read(StructArray composite, int first, Span<T> into)
    {
        var batch = Interlocked.Exchange(ref _spare, null) ?? new Batch(_cursors);
        try
        {
            batch.Attach(composite, _fields);
            if (!_holdsNull && composite.NullCount != 0)
            {
                // Refused before anything is written: the first NULL composite in the range, by bit.
                for (var row = first; row < first + into.Length; row++)
                {
                    if (!batch.Composite.IsValid(row))
                    {
                        _ = Null(row);
                    }
                }
            }

            for (var done = 0; done < into.Length;)
            {
                var count = Math.Min(into.Length - done, Chunk);
                _fill(batch.Cursors, batch.Composite, batch.Rows, first + done, count);
                batch.Rows.AsSpan(0, count).CopyTo(into[done..]);
                done += count;
            }
        }
        finally
        {
            batch.Detach();
            Volatile.Write(ref _spare, batch);
        }
    }

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
            throw Refused(type.Name, $"a field of it has no Chalk type ({unsupported.Message})");
        }

        var described = IrTypes.Describe(declared.ToProto());
        if (!CompositeInference.IsCandidate(record))
        {
            throw Refused(
                described,
                $"{CompositeInference.Describe(record)} is not a record a composite value can be read into");
        }

        var cursorsParameter = Expression.Parameter(typeof(FieldCursor[]), "cursors");
        var composite = Expression.Parameter(typeof(ValidityCursor), "composite");
        var into = Expression.Parameter(typeof(T[]), "into");
        var first = Expression.Parameter(typeof(int), "first");
        var count = Expression.Parameter(typeof(int), "count");
        var row = Expression.Variable(typeof(int), "row");
        var i = Expression.Variable(typeof(int), "i");

        var factories = new List<Func<FieldCursor>>();
        var fields = new List<int>();
        var locals = new List<ParameterExpression>();
        var casts = new List<Expression>();

        Expression Member(string name, Type clr)
        {
            var (field, cursor, read) = Plan(type, declared, described, record, name, clr);
            var local = Expression.Variable(cursor.Type, $"c{locals.Count}");
            casts.Add(Expression.Assign(
                local,
                Expression.Convert(
                    Expression.ArrayIndex(cursorsParameter, Expression.Constant(locals.Count)), cursor.Type)));
            locals.Add(local);
            factories.Add(cursor.Create);
            fields.Add(field);
            return Expression.Call(local, read, row);
        }

        Expression built;
        if (CompositeInference.PositionalConstructor(record) is { } constructor)
        {
            built = Expression.New(
                constructor,
                constructor.GetParameters().Select(p => Member(p.Name!, p.ParameterType)).ToArray());
        }
        else
        {
            var settable = CompositeInference.Properties(record)
                .Where(p => p.SetMethod is { IsPublic: true })
                .ToArray();
            if (settable.Length == 0 || (!record.IsValueType && record.GetConstructor(Type.EmptyTypes) is null))
            {
                throw Refused(
                    described,
                    $"{CompositeInference.Describe(record)} can be built neither through a constructor whose "
                    + "parameters are its properties nor through a public parameterless constructor and "
                    + "settable properties");
            }

            built = Expression.MemberInit(
                Expression.New(record),
                settable.Select(p => (MemberBinding)Expression.Bind(p, Member(p.Name, p.PropertyType))).ToArray());
        }

        if (built.Type != target)
        {
            built = Expression.Convert(built, target);
        }

        // for (i = 0; i < count; i++) { row = first + i; into[i] = composite.IsValid(row) ? new T(…) : default; }
        var exit = Expression.Label("exit");
        var loop = Expression.Loop(
            Expression.IfThenElse(
                Expression.LessThan(i, count),
                Expression.Block(
                    Expression.Assign(row, Expression.Add(first, i)),
                    Expression.Assign(
                        Expression.ArrayAccess(into, i),
                        Expression.Condition(
                            Expression.Call(composite, ValidityCursor.IsValidMethod, row),
                            built,
                            Expression.Default(target))),
                    Expression.PostIncrementAssign(i)),
                Expression.Break(exit)),
            exit);

        var body = Expression.Block(
            [row, i, .. locals],
            [.. casts, Expression.Assign(i, Expression.Constant(0)), loop]);
        var fill = Expression.Lambda<Fill>(body, cursorsParameter, composite, into, first, count).Compile();
        return new CompositeReader<T>([.. factories], [.. fields], fill, holdsNull);
    }

    /// <summary>
    /// Which child the member <paramref name="name"/> reads, the cursor that reads it, and the cursor's
    /// method that answers <paramref name="clr"/> — refused, naming both sides, where none can.
    /// </summary>
    private static (int Field, CursorKind Cursor, MethodInfo Read) Plan(
        StructType type, ChalkType declared, string described, Type record, string name, Type clr)
    {
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
            throw Refused(described, $"'{name}' of {CompositeInference.Describe(record)} names none of its fields");
        }

        var field = declared.Fields[index];
        if (field.Type.Kind == Ir.TypeKind.Decimal && field.Type.Precision > LaneCodec.DecimalDigits)
        {
            throw Refused(
                described,
                $"field '{field.Name}' is {IrTypes.Describe(field.Type.ToProto())}, which a CLR decimal cannot "
                + $"hold (it holds {LaneCodec.DecimalDigits} digits), and '{name}' of "
                + $"{CompositeInference.Describe(record)} is {CompositeInference.Describe(clr)}; read this field "
                + "from its own Arrow array");
        }

        if (!LaneCodec.Accepts(clr, field.Type))
        {
            throw Refused(
                described,
                $"field '{field.Name}' is {LaneCodec.Describe(field.Type)} and '{name}' of "
                + $"{CompositeInference.Describe(record)} is {CompositeInference.Describe(clr)}, which does not read it");
        }

        if (field.Type.Nullable && clr.IsValueType && Nullable.GetUnderlyingType(clr) is null)
        {
            throw Refused(
                described,
                $"field '{field.Name}' is nullable and '{name}' of {CompositeInference.Describe(record)} is "
                + $"{CompositeInference.Describe(clr)}, which cannot hold its NULL; declare it {clr.Name}?");
        }

        var cursor = CursorKind.For(clr, field.Type, type.Fields[index].DataType);
        var nullable = Nullable.GetUnderlyingType(clr) is not null;
        var read = cursor.Type.GetMethod(nullable ? nameof(FixedCursor<int>.ReadNullable) : nameof(FixedCursor<int>.Read))
            ?? throw new InvalidOperationException($"{cursor.Type.Name} has no reader for {clr.Name}.");
        if (read.ReturnType != clr)
        {
            throw new InvalidOperationException(
                $"{cursor.Type.Name} reads {read.ReturnType.Name}, not {clr.Name}; the cursor table and the "
                + "lane codec disagree.");
        }

        return (index, cursor, read);
    }

    private static InvalidOperationException Refused(string described, string why) =>
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

    /// <summary>
    /// One read's state: a cursor per member and the array the binder writes into. Pooled one deep per
    /// binding; attached to a struct array for the length of a read and detached after it, so a pooled
    /// batch pins nothing and holds no row of the host's.
    /// </summary>
    private sealed class Batch
    {
        public Batch(Func<FieldCursor>[] cursors)
        {
            Cursors = new FieldCursor[cursors.Length];
            for (var c = 0; c < cursors.Length; c++)
            {
                Cursors[c] = cursors[c]();
            }
        }

        public FieldCursor[] Cursors { get; }

        public ValidityCursor Composite { get; } = new();

        public T[] Rows { get; } = new T[Chunk];

        public void Attach(StructArray composite, int[] fields)
        {
            var data = composite.Data;
            Composite.Attach(data, 0);
            for (var c = 0; c < Cursors.Length; c++)
            {
                Cursors[c].Attach(data.Children[fields[c]], data.Offset);
            }
        }

        public void Detach()
        {
            Composite.Detach();
            foreach (var cursor in Cursors)
            {
                cursor.Detach();
            }

            if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            {
                System.Array.Clear(Rows);
            }
        }
    }
}

/// <summary>Which cursor reads a field for a CLR spelling, and how to make one.</summary>
internal sealed record CursorKind(Type Type, Func<FieldCursor> Create)
{
    /// <summary>The cursor for one (CLR spelling, Chalk type, Arrow type), the lane codec's pairs.</summary>
    public static CursorKind For(Type clr, ChalkType field, IArrowType arrow)
    {
        var value = Nullable.GetUnderlyingType(clr) ?? clr;
        var views = arrow is StringViewType or BinaryViewType;
        return field.Kind switch
        {
            Ir.TypeKind.Bool => Of(() => new BoolCursor()),
            Ir.TypeKind.I8 => Of(() => new FixedCursor<sbyte>()),
            Ir.TypeKind.I16 => Of(() => new FixedCursor<short>()),
            Ir.TypeKind.I32 => Of(() => new FixedCursor<int>()),
            Ir.TypeKind.I64 => Of(() => new FixedCursor<long>()),
            Ir.TypeKind.Fp32 => Of(() => new FixedCursor<float>()),
            Ir.TypeKind.Fp64 => Of(() => new FixedCursor<double>()),
            Ir.TypeKind.Decimal => Of(() => new DecimalCursor(field.Scale)),
            Ir.TypeKind.String when value == typeof(Utf8String) => Of(() => new Utf8Cursor(views)),
            Ir.TypeKind.String => Of(() => new StringCursor(views)),
            Ir.TypeKind.Binary when value == typeof(ReadOnlyMemory<byte>) => Of(() => new BinaryMemoryCursor(views)),
            Ir.TypeKind.Binary => Of(() => new BinaryArrayCursor(views)),
            Ir.TypeKind.Date when value == typeof(DateOnly) => Of(() => new DateCursor()),
            Ir.TypeKind.Date => Of(() => new FixedCursor<int>()),
            Ir.TypeKind.Time when value == typeof(TimeOnly) => Of(() => new TimeCursor(MicrosDivisor(arrow))),
            Ir.TypeKind.Time => Of(() => new MicrosCursor(MicrosDivisor(arrow), 1)),
            Ir.TypeKind.Timestamp when value == typeof(DateTime) => Of(() => new DateTimeCursor(field.Precision)),
            Ir.TypeKind.TimestampTz when value == typeof(DateTimeOffset) => Of(() => new InstantCursor(field.Precision)),
            Ir.TypeKind.Timestamp or Ir.TypeKind.TimestampTz => Of(() => new FixedCursor<long>()),
            Ir.TypeKind.IntervalDay when value == typeof(TimeSpan) =>
                Of(() => new IntervalCursor(MicrosDivisor(arrow), MicrosMultiplier(arrow))),
            Ir.TypeKind.IntervalDay => Of(() => new MicrosCursor(MicrosDivisor(arrow), MicrosMultiplier(arrow))),
            Ir.TypeKind.IntervalYear => Of(() => new FixedCursor<int>()),
            Ir.TypeKind.Uuid => Of(() => new UuidCursor()),
            _ => throw new InvalidOperationException($"no cursor reads a {field.Kind} field."),
        };
    }

    private static CursorKind Of<TCursor>(Func<TCursor> create)
        where TCursor : FieldCursor => new(typeof(TCursor), create);

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

/// <summary>
/// A struct array's own validity for one read: its bitmap pinned, its offset applied, tested by bit.
/// </summary>
internal sealed unsafe class ValidityCursor
{
    internal static readonly MethodInfo IsValidMethod = typeof(ValidityCursor).GetMethod(nameof(IsValid))!;

    private MemoryHandle _handle;
    private byte* _bits;
    private int _offset;

    public void Attach(ArrayData data, int offset)
    {
        _offset = data.Offset + offset;
        _bits = null;
        if (data.NullCount != 0 && data.Buffers.Length > 0 && data.Buffers[0].Length > 0)
        {
            _handle = data.Buffers[0].Memory.Pin();
            _bits = (byte*)_handle.Pointer;
        }
    }

    public void Detach()
    {
        _handle.Dispose();
        _handle = default;
        _bits = null;
    }

    public bool IsValid(int row)
    {
        if (_bits == null)
        {
            return true;
        }

        var position = _offset + row;
        return (_bits[position >> 3] & (1 << (position & 7))) != 0;
    }
}

/// <summary>
/// One child of a composite for one read: its validity pinned and tested by bit, the child's offset
/// and the struct's applied. The derived cursors pin what they read the values from.
/// </summary>
internal abstract unsafe class FieldCursor
{
    private MemoryHandle _validityHandle;
    private byte* _validity;

    /// <summary>The child's own offset and the struct's: where the read's row 0 is in the buffers.</summary>
    protected int Offset { get; private set; }

    public virtual void Attach(ArrayData child, int compositeOffset)
    {
        Offset = child.Offset + compositeOffset;
        _validity = null;
        if (child.NullCount != 0 && child.Buffers.Length > 0 && child.Buffers[0].Length > 0)
        {
            _validityHandle = child.Buffers[0].Memory.Pin();
            _validity = (byte*)_validityHandle.Pointer;
        }
    }

    public virtual void Detach()
    {
        _validityHandle.Dispose();
        _validityHandle = default;
        _validity = null;
    }

    protected bool IsValid(int row)
    {
        if (_validity == null)
        {
            return true;
        }

        var position = Offset + row;
        return (_validity[position >> 3] & (1 << (position & 7))) != 0;
    }

    protected static byte* Pin(in ArrowBuffer buffer, ref MemoryHandle handle)
    {
        handle = buffer.Memory.Pin();
        return (byte*)handle.Pointer;
    }
}

/// <summary>A fixed-width child read through its pinned lanes: <typeparamref name="TLane"/> per row.</summary>
internal abstract unsafe class LaneCursor<TLane> : FieldCursor
    where TLane : unmanaged
{
    private MemoryHandle _handle;
    private byte* _lanes;

    public override void Attach(ArrayData child, int compositeOffset)
    {
        base.Attach(child, compositeOffset);
        _lanes = Pin(child.Buffers[1], ref _handle);
    }

    public override void Detach()
    {
        _handle.Dispose();
        _handle = default;
        _lanes = null;
        base.Detach();
    }

    protected TLane Lane(int row) => Unsafe.ReadUnaligned<TLane>(_lanes + ((long)(Offset + row) * sizeof(TLane)));

    protected byte* LaneAddress(int row, int width) => _lanes + ((long)(Offset + row) * width);
}

internal sealed class FixedCursor<TValue> : LaneCursor<TValue>
    where TValue : unmanaged
{
    public TValue Read(int row) => Lane(row);

    public TValue? ReadNullable(int row) => IsValid(row) ? Lane(row) : default(TValue?);
}

internal sealed unsafe class BoolCursor : FieldCursor
{
    private MemoryHandle _handle;
    private byte* _bits;

    public override void Attach(ArrayData child, int compositeOffset)
    {
        base.Attach(child, compositeOffset);
        _bits = Pin(child.Buffers[1], ref _handle);
    }

    public override void Detach()
    {
        _handle.Dispose();
        _handle = default;
        _bits = null;
        base.Detach();
    }

    public bool Read(int row)
    {
        var position = Offset + row;
        return (_bits[position >> 3] & (1 << (position & 7))) != 0;
    }

    public bool? ReadNullable(int row) => IsValid(row) ? Read(row) : default(bool?);
}

internal sealed unsafe class DecimalCursor(int scale) : LaneCursor<byte>
{
    public decimal Read(int row) => ClrStorage.DecimalOf(new ReadOnlySpan<byte>(LaneAddress(row, 16), 16), scale);

    public decimal? ReadNullable(int row) => IsValid(row) ? Read(row) : default(decimal?);
}

internal sealed unsafe class UuidCursor : LaneCursor<byte>
{
    public Guid Read(int row) => ClrStorage.UuidOf(new ReadOnlySpan<byte>(LaneAddress(row, 16), 16));

    public Guid? ReadNullable(int row) => IsValid(row) ? Read(row) : default(Guid?);
}

internal sealed class DateCursor : LaneCursor<int>
{
    public DateOnly Read(int row) => ClrStorage.DateOf(Lane(row));

    public DateOnly? ReadNullable(int row) => IsValid(row) ? Read(row) : default(DateOnly?);
}

/// <summary>A count in the Arrow type's unit, as the microseconds a TIME or an INTERVAL_DAY holds.</summary>
internal sealed class MicrosCursor(long divisor, long multiplier) : LaneCursor<long>
{
    public long Read(int row) => Micros(Lane(row), divisor, multiplier);

    public long? ReadNullable(int row) => IsValid(row) ? Read(row) : default(long?);

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

internal sealed class TimeCursor(long divisor) : LaneCursor<long>
{
    public TimeOnly Read(int row) => ClrStorage.TimeOf(MicrosCursor.Micros(Lane(row), divisor, 1));

    public TimeOnly? ReadNullable(int row) => IsValid(row) ? Read(row) : default(TimeOnly?);
}

internal sealed class IntervalCursor(long divisor, long multiplier) : LaneCursor<long>
{
    public TimeSpan Read(int row) => ClrStorage.IntervalOf(MicrosCursor.Micros(Lane(row), divisor, multiplier));

    public TimeSpan? ReadNullable(int row) => IsValid(row) ? Read(row) : default(TimeSpan?);
}

internal sealed class DateTimeCursor(int precision) : LaneCursor<long>
{
    public DateTime Read(int row) => ClrStorage.DateTimeOf(Lane(row), precision);

    public DateTime? ReadNullable(int row) => IsValid(row) ? Read(row) : default(DateTime?);
}

internal sealed class InstantCursor(int precision) : LaneCursor<long>
{
    public DateTimeOffset Read(int row) => ClrStorage.InstantOf(Lane(row), precision);

    public DateTimeOffset? ReadNullable(int row) => IsValid(row) ? Read(row) : default(DateTimeOffset?);
}

/// <summary>
/// A variable-length child — STRING or BINARY — in the classic layout (offsets, then the bytes) or
/// the view layout (a 16-byte view per row, the bytes inline when twelve or fewer, otherwise in one
/// of the data buffers). Its offsets or views are pinned; a value is a slice of the buffer's own
/// memory, never a copy.
/// </summary>
internal abstract unsafe class BytesCursor(bool views) : FieldCursor
{
    private MemoryHandle _handle;
    private byte* _index;
    private ArrowBuffer[] _buffers = [];

    public override void Attach(ArrayData child, int compositeOffset)
    {
        base.Attach(child, compositeOffset);
        _index = Pin(child.Buffers[1], ref _handle);
        _buffers = child.Buffers;
    }

    public override void Detach()
    {
        _handle.Dispose();
        _handle = default;
        _index = null;
        _buffers = [];
        base.Detach();
    }

    protected ReadOnlyMemory<byte> Bytes(int row)
    {
        var position = Offset + row;
        if (!views)
        {
            var offsets = (int*)_index;
            var start = offsets[position];
            return _buffers[2].Memory.Slice(start, offsets[position + 1] - start);
        }

        var view = _index + ((long)position * 16);
        var length = *(int*)view;
        if (length <= 12)
        {
            return _buffers[1].Memory.Slice((position * 16) + 4, length);
        }

        return _buffers[2 + *(int*)(view + 8)].Memory.Slice(*(int*)(view + 12), length);
    }
}

/// <summary>
/// STRING as a <see cref="Utf8String"/>: a slice of the batch's memory, valid while the batch is. Its
/// nullable read answers a typed NULL, never a bare <c>null</c>, which would convert through
/// <see cref="Utf8String"/>'s conversion from <c>byte[]</c> into an empty value — as it would through
/// <see cref="ReadOnlyMemory{T}"/>'s below.
/// </summary>
internal sealed class Utf8Cursor(bool views) : BytesCursor(views)
{
    public Utf8String Read(int row) => new(Bytes(row));

    public Utf8String? ReadNullable(int row) => IsValid(row) ? Read(row) : default(Utf8String?);
}

/// <summary>STRING as a <see cref="string"/>: decoded, and so allocated, on every read.</summary>
internal sealed class StringCursor(bool views) : BytesCursor(views)
{
    public string? Read(int row) => IsValid(row) ? Encoding.UTF8.GetString(Bytes(row).Span) : null;
}

/// <summary>BINARY as a <see cref="ReadOnlyMemory{T}"/>: a slice of the batch's memory.</summary>
internal sealed class BinaryMemoryCursor(bool views) : BytesCursor(views)
{
    public ReadOnlyMemory<byte> Read(int row) => Bytes(row);

    public ReadOnlyMemory<byte>? ReadNullable(int row) => IsValid(row) ? Read(row) : default(ReadOnlyMemory<byte>?);
}

/// <summary>BINARY as a <c>byte[]</c>: copied, and so allocated, on every read.</summary>
internal sealed class BinaryArrayCursor(bool views) : BytesCursor(views)
{
    public byte[]? Read(int row) => IsValid(row) ? Bytes(row).ToArray() : null;
}

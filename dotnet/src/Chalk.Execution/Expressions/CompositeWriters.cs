using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using System.Reflection;
using Chalk.Catalog;
using Chalk.Sources;
using Type = System.Type;

namespace Chalk.Execution.Expressions;

/// <summary>
/// Writes a record a Tier 1 delegate returned into a COMPOSITE result's field columns (D294, ADR 0077):
/// one accessor per field, compiled once when the kernel is bound, and per lane one read and one write
/// per field — no boxing, no reflection and no allocation per row.
/// </summary>
/// <remarks>
/// <para>
/// The accessors are expression trees over the record's properties, compiled into
/// <c>Func&lt;TRecord, TField&gt;</c> delegates, which is how <c>PocoChunkCompiler</c> reads a member;
/// the field writers are generic on the field's CLR type, so each write is the same specialised
/// <see cref="LaneCodec"/> call a scalar result makes.
/// </para>
/// <para>
/// Every field column gets exactly one row per composite row, whether the composite is NULL or not, so the
/// fields stay aligned with the composite's own rows. A NULL composite's fields are undefined (D291): a
/// nullable field is written NULL, a non-nullable one its type's default, so a non-nullable field
/// column never holds a NULL.
/// </para>
/// </remarks>
/// <typeparam name="TOut">What the delegate returns: the record, or a <c>Nullable</c> of it.</typeparam>
internal abstract class CompositeWriter<TOut>
{
    /// <summary>Starts one batch of <paramref name="length"/> rows in <paramref name="result"/>'s fields.</summary>
    public abstract void Begin(ColumnWriter result, int length);

    /// <summary>
    /// Writes one row's answer into the fields, and says whether it was a value at all — false for a
    /// NULL record, whose fields have then been written as a NULL composite's are.
    /// </summary>
    public abstract bool Write(TOut value, int row);

    /// <summary>Writes a NULL composite's row: a strict lane the delegate never saw.</summary>
    public abstract void WriteNull(int row);
}

/// <summary>Builds a <see cref="CompositeWriter{TOut}"/> for a declared composite, once, at binding.</summary>
internal static class CompositeWriters
{
    /// <summary>
    /// The writer for a delegate returning <typeparamref name="TOut"/> into <paramref name="declared"/>.
    /// The binding check has already compared the record with the declaration field by field, so the
    /// properties read here are the fields, in the declaration's order.
    /// </summary>
    public static CompositeWriter<TOut> For<TOut>(ChalkType declared)
    {
        var underlying = Nullable.GetUnderlyingType(typeof(TOut));
        var record = underlying ?? typeof(TOut);
        var recordWriter = typeof(CompositeWriters)
            .GetMethod(nameof(RecordWriter), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(record)
            .Invoke(null, [declared])!;

        if (underlying is null)
        {
            return (CompositeWriter<TOut>)recordWriter;
        }

        return (CompositeWriter<TOut>)Activator.CreateInstance(
            typeof(NullableRecordWriter<>).MakeGenericType(record), recordWriter)!;
    }

    private static RecordWriter<TRecord> RecordWriter<TRecord>(ChalkType declared)
    {
        var properties = CompositeInference.Properties(typeof(TRecord));
        if (properties.Count != declared.Fields.Count)
        {
            throw new InvalidOperationException(
                $"{typeof(TRecord).Name} has {properties.Count} fields and the declared composite "
                + $"{declared.Fields.Count}; the binding check should have refused this.");
        }

        var fields = new FieldWriter<TRecord>[properties.Count];
        for (var i = 0; i < fields.Length; i++)
        {
            fields[i] = Field<TRecord>(properties[i], declared.Fields[i].Type.Nullable);
        }

        return new RecordWriter<TRecord>(fields);
    }

    /// <summary>One field's writer: the property's compiled getter, and the field's nullability.</summary>
    private static FieldWriter<TRecord> Field<TRecord>(PropertyInfo property, bool nullable)
    {
        var parameter = Expression.Parameter(typeof(TRecord), "record");
        var getter = Expression.Lambda(
                typeof(Func<,>).MakeGenericType(typeof(TRecord), property.PropertyType),
                Expression.Property(parameter, property),
                parameter)
            .Compile();

        return (FieldWriter<TRecord>)Activator.CreateInstance(
            typeof(FieldWriter<,>).MakeGenericType(typeof(TRecord), property.PropertyType),
            getter,
            nullable)!;
    }
}

/// <summary>The record itself: a value type or a class, a class's null being a NULL composite.</summary>
internal sealed class RecordWriter<TRecord> : CompositeWriter<TRecord>
{
    private readonly FieldWriter<TRecord>[] _fields;

    public RecordWriter(FieldWriter<TRecord>[] fields) => _fields = fields;

    public override void Begin(ColumnWriter result, int length)
    {
        for (var i = 0; i < _fields.Length; i++)
        {
            _fields[i].Begin(result.Child(i), length);
        }
    }

    public override bool Write(TRecord value, int row)
    {
        // A value-type record is never null, and the test folds away; a class's null is a NULL composite.
        if (!typeof(TRecord).IsValueType && value is null)
        {
            WriteNull(row);
            return false;
        }

        foreach (var field in _fields)
        {
            field.Write(value, row);
        }

        return true;
    }

    public override void WriteNull(int row)
    {
        foreach (var field in _fields)
        {
            field.WriteNull(row);
        }
    }
}

/// <summary><c>Nullable&lt;TRecord&gt;</c>: no value is a NULL composite.</summary>
internal sealed class NullableRecordWriter<TRecord> : CompositeWriter<TRecord?>
    where TRecord : struct
{
    private readonly RecordWriter<TRecord> _record;

    public NullableRecordWriter(RecordWriter<TRecord> record) => _record = record;

    public override void Begin(ColumnWriter result, int length) => _record.Begin(result, length);

    public override bool Write(TRecord? value, int row)
    {
        if (value.HasValue)
        {
            return _record.Write(value.GetValueOrDefault(), row);
        }

        _record.WriteNull(row);
        return false;
    }

    public override void WriteNull(int row) => _record.WriteNull(row);
}

/// <summary>One field of a record, written into its field column.</summary>
internal abstract class FieldWriter<TRecord>
{
    public abstract void Begin(ColumnWriter field, int length);

    public abstract void Write(TRecord record, int row);

    public abstract void WriteNull(int row);
}

/// <summary>A field of CLR type <typeparamref name="TField"/>, read through its compiled getter.</summary>
internal sealed class FieldWriter<TRecord, TField> : FieldWriter<TRecord>
{
    private static readonly bool Variable = LaneCodec.IsVariableLength<TField>();

    private readonly Func<TRecord, TField> _get;
    private readonly bool _nullable;
    private ColumnWriter? _column;
    private int _length;

    public FieldWriter(Func<TRecord, TField> get, bool nullable)
    {
        _get = get;
        _nullable = nullable;
    }

    public override void Begin(ColumnWriter field, int length)
    {
        _column = field;
        _length = length;
        if (Variable)
        {
            field.BeginVarLength(length, _nullable);
        }
        else if (_nullable)
        {
            _ = field.BeginValidity(length);
        }
        else
        {
            field.NoValidity();
        }
    }

    public override void Write(TRecord record, int row)
    {
        var value = _get(record);
        var column = _column!;
        if (Variable)
        {
            LaneCodec.Append(column, _length, row, value);
            return;
        }

        if (LaneCodec.IsNull(value))
        {
            // The bit Begin cleared stays clear. Only a nullable field's CLR type can answer NULL,
            // which the binding check made sure of.
            return;
        }

        LaneCodec.Write(column, _length, row, value);
        if (_nullable)
        {
            column.SetValid(row);
        }
    }

    public override void WriteNull(int row)
    {
        var column = _column!;
        if (Variable)
        {
            if (_nullable)
            {
                column.AppendNull();
            }
            else
            {
                column.AppendValue(default);
            }

            return;
        }

        if (!_nullable)
        {
            // A non-nullable field under a NULL composite holds its type's default, never garbage from
            // the previous batch — and never a NULL, which its column may not have.
            LaneCodec.Write(column, _length, row, default(TField)!);
        }
    }
}

/// <summary>
/// Appends a record an aggregate's <c>Finish</c> returned to a COMPOSITE column's copier (D294): the
/// grouped aggregate's emit and the window aggregate's frames. The same compiled accessors as
/// <see cref="CompositeWriter{TOut}"/>, writing into <see cref="Vectors.ColumnCopier"/> fields rather than
/// a kernel's writer — one read and one append per field, and the composite row closed.
/// </summary>
internal abstract class CompositeEmitter<TOut>
{
    /// <summary>Appends one composite row: <paramref name="value"/>'s fields, or a NULL composite's.</summary>
    public abstract void Emit(Vectors.ColumnCopier copier, TOut value);
}

/// <summary>Builds a <see cref="CompositeEmitter{TOut}"/>, once, when the aggregate is bound.</summary>
internal static class CompositeEmitters
{
    public static CompositeEmitter<TOut> For<TOut>(ChalkType declared)
    {
        var underlying = Nullable.GetUnderlyingType(typeof(TOut));
        var record = underlying ?? typeof(TOut);
        var properties = CompositeInference.Properties(record);
        if (properties.Count != declared.Fields.Count)
        {
            throw new InvalidOperationException(
                $"{record.Name} has {properties.Count} fields and the declared composite "
                + $"{declared.Fields.Count}; the binding check should have refused this.");
        }

        var fields = Array.CreateInstance(
            typeof(FieldEmitter<>).MakeGenericType(record), properties.Count);
        for (var i = 0; i < properties.Count; i++)
        {
            var parameter = Expression.Parameter(record, "record");
            var getter = Expression.Lambda(
                    typeof(Func<,>).MakeGenericType(record, properties[i].PropertyType),
                    Expression.Property(parameter, properties[i]),
                    parameter)
                .Compile();
            fields.SetValue(
                Activator.CreateInstance(
                    typeof(FieldEmitter<,>).MakeGenericType(record, properties[i].PropertyType),
                    getter),
                i);
        }

        var emitter = Activator.CreateInstance(
            typeof(RecordEmitter<>).MakeGenericType(record), fields)!;
        return underlying is null
            ? (CompositeEmitter<TOut>)emitter
            : (CompositeEmitter<TOut>)Activator.CreateInstance(
                typeof(NullableRecordEmitter<>).MakeGenericType(record), emitter)!;
    }
}

/// <summary>The record itself; a class's null is a NULL composite.</summary>
internal sealed class RecordEmitter<TRecord> : CompositeEmitter<TRecord>
{
    private readonly FieldEmitter<TRecord>[] _fields;

    public RecordEmitter(FieldEmitter<TRecord>[] fields) => _fields = fields;

    public override void Emit(Vectors.ColumnCopier copier, TRecord value)
    {
        if (!typeof(TRecord).IsValueType && value is null)
        {
            copier.AppendNulls(1);
            return;
        }

        for (var i = 0; i < _fields.Length; i++)
        {
            _fields[i].Emit(copier.Field(i), value);
        }

        copier.EndComposite(valid: true);
    }
}

/// <summary><c>Nullable&lt;TRecord&gt;</c>: no value is a NULL composite.</summary>
internal sealed class NullableRecordEmitter<TRecord> : CompositeEmitter<TRecord?>
    where TRecord : struct
{
    private readonly RecordEmitter<TRecord> _record;

    public NullableRecordEmitter(RecordEmitter<TRecord> record) => _record = record;

    public override void Emit(Vectors.ColumnCopier copier, TRecord? value)
    {
        if (value.HasValue)
        {
            _record.Emit(copier, value.GetValueOrDefault());
        }
        else
        {
            copier.AppendNulls(1);
        }
    }
}

/// <summary>One field, appended to its field copier.</summary>
internal abstract class FieldEmitter<TRecord>
{
    public abstract void Emit(Vectors.ColumnCopier field, TRecord record);
}

/// <summary>A field of CLR type <typeparamref name="TField"/>, read through its compiled getter.</summary>
internal sealed class FieldEmitter<TRecord, TField> : FieldEmitter<TRecord>
{
    private readonly Func<TRecord, TField> _get;

    public FieldEmitter(Func<TRecord, TField> get) => _get = get;

    public override void Emit(Vectors.ColumnCopier field, TRecord record)
    {
        var value = _get(record);
        if (typeof(TField) == typeof(Utf8String))
        {
            Text(field, Unsafe.As<TField, Utf8String>(ref value).AsSpan());
            return;
        }

        if (typeof(TField) == typeof(Utf8String?))
        {
            var text = Unsafe.As<TField, Utf8String?>(ref value);
            if (text is { } present)
            {
                Text(field, present.AsSpan());
            }
            else
            {
                field.AppendRaw(default, valid: false);
            }

            return;
        }

        if (typeof(TField) == typeof(string))
        {
            // The allocating spelling of a STRING, as it is for a scalar result (D146): a .NET
            // string has to be encoded to reach a UTF-8 column. Utf8String is the one that is not.
            var text = Unsafe.As<TField, string?>(ref value);
            if (text is null)
            {
                field.AppendRaw(default, valid: false);
            }
            else
            {
                field.AppendRaw(System.Text.Encoding.UTF8.GetBytes(text), valid: true);
            }

            return;
        }

        Span<byte> lane = stackalloc byte[16];
        var valid = LaneCodec.WriteLane(lane, value);
        field.AppendRaw(lane, valid);
    }

    /// <summary>A STRING field's bytes, checked where they enter a column as every Tier 1 result's are.</summary>
    private static void Text(Vectors.ColumnCopier field, ReadOnlySpan<byte> bytes)
    {
        if (!Utf8String.IsValidUtf8(bytes))
        {
            throw new InvalidUtf8Exception("the Utf8String field an aggregate returned", -1);
        }

        field.AppendRaw(bytes, valid: true);
    }
}

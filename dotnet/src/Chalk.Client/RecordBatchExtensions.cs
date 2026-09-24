using System.Text;
using Apache.Arrow;
using Apache.Arrow.Types;

namespace Chalk.Arrow;

/// <summary>
/// Arrow batches as CLR values. <see cref="ToRows(RecordBatch)"/> and <see cref="ValueAt"/> are the
/// boxed rows of the <b>test kit and samples only</b>: they box every value, which is precisely what
/// the engine exists to avoid, and hosts read Arrow. <see cref="GetComposite{T}"/> and
/// <see cref="TryGetComposite{T}"/> are a host's own: a composite cell read as the host's record,
/// allocating nothing a record struct does not.
/// </summary>
public static class RecordBatchExtensions
{
    public static bool IsNullAt(
        this IArrowArray array,
        int index)
    {
        ArgumentNullException.ThrowIfNull(array);

        if ((uint)index >= (uint)array.Length)
            throw new ArgumentOutOfRangeException(nameof(index));

        var validity =
            array.Data.Buffers[0].Span;

        return !validity.IsEmpty &&
               !BitUtility.GetBit(
                   validity,
                   array.Offset + index);
    }

    public static ReadOnlySpan<byte> GetUtf8(
        this IArrowArray array,
        int index)
    {
        ArgumentNullException.ThrowIfNull(array);

        return array switch
        {
            StringArray strings =>
                strings.GetBytes(index),

            StringViewArray strings =>
                strings.GetBytes(index),

            _ =>
                throw new ArgumentException(
                    $"Expected Arrow UTF-8 array, got "
                    + $"{array.Data.DataType}.",
                    nameof(array)),
        };
    }

    public static string? GetUtf8String(
        this IArrowArray array,
        int index)
    {
        if (array.IsNullAt(index))
            return null;

        return Encoding.UTF8.GetString(
            array.GetUtf8(index));
    }
    
    /// <summary>
    /// One composite cell of a struct column — what a function answering a record returns, or a
    /// composite column of an in-process table — as the host's own <typeparamref name="T"/>: a record
    /// struct, a record class, or any class or struct with a public parameterless constructor and
    /// settable properties. A NULL composite reads as null for a reference type or a
    /// <c>Nullable&lt;T&gt;</c>; for any other struct it is refused, and
    /// <see cref="TryGetComposite{T}"/> is the way to read it. A batch is read at once with
    /// <see cref="ReadComposites{T}(IArrowArray, int, Span{T})"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <typeparamref name="T"/>'s properties — or a positional record's constructor parameters — are
    /// matched to the composite's fields by name, ignoring case, and each must read its field's type
    /// as a function's parameter would: <c>Utf8String</c> or <c>string</c> for a STRING, <c>decimal</c>
    /// for a DECIMAL of up to 28 digits, <c>DateOnly</c>, <c>DateTime</c>, <c>Guid</c> and the rest of
    /// the Tier 1 table. A nullable field needs a member that can hold its NULL. A field
    /// <typeparamref name="T"/> does not name is not read. The binding is built on the first call for a
    /// given <typeparamref name="T"/> and struct type and cached; a mismatch is refused there, naming
    /// both sides.
    /// </para>
    /// <para>
    /// The fields are read from the child arrays' own buffers, and nothing is allocated but what
    /// <typeparamref name="T"/> itself costs: a <c>Utf8String</c> or a <c>ReadOnlyMemory&lt;byte&gt;</c>
    /// field is a slice of the batch's memory, valid while the batch is. A <c>string</c> or a
    /// <c>byte[]</c> field is a copy, allocated on every read, and so is a record class.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">The array is not a struct array.</exception>
    /// <exception cref="InvalidOperationException">
    /// <typeparamref name="T"/> cannot be read from the composite, or the cell is a NULL composite and
    /// <typeparamref name="T"/> cannot hold one.
    /// </exception>
    public static T GetComposite<T>(this IArrowArray array, int index)
    {
        var composite = CompositeAt(array, index, 1);
        var reader = CompositeReader<T>.For(composite);
        if (composite.IsNull(index))
        {
            return reader.Null(index);
        }

        T value = default!;
        reader.Read(composite, index, System.Runtime.InteropServices.MemoryMarshal.CreateSpan(ref value, 1));
        return value;
    }

    /// <summary>
    /// The same read, saying whether the cell held a composite: false, with <paramref name="value"/>
    /// the default, for a NULL composite.
    /// </summary>
    /// <inheritdoc cref="GetComposite{T}(IArrowArray, int)" path="/remarks"/>
    public static bool TryGetComposite<T>(
        this IArrowArray array, int index, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out T value)
    {
        var composite = CompositeAt(array, index, 1);
        var reader = CompositeReader<T>.For(composite);
        value = default!;
        if (composite.IsNull(index))
        {
            return false;
        }

        reader.Read(composite, index, System.Runtime.InteropServices.MemoryMarshal.CreateSpan(ref value, 1));
        return true;
    }

    /// <summary>
    /// The first <c>into.Length</c> composite cells of a struct column, one per element of
    /// <paramref name="into"/>: the batch form of <see cref="GetComposite{T}(IArrowArray, int)"/>, which
    /// sets the read up once and loops inside the compiled binding rather than once per row.
    /// </summary>
    /// <inheritdoc cref="ReadComposites{T}(IArrowArray, int, Span{T})" path="/remarks"/>
    public static void ReadComposites<T>(this IArrowArray array, Span<T> into) =>
        ReadComposites(array, 0, into);

    /// <summary>
    /// The composite cells from row <paramref name="start"/>, one per element of
    /// <paramref name="into"/>, read as <see cref="GetComposite{T}(IArrowArray, int)"/> reads one: a
    /// NULL composite is null for a reference type or a <c>Nullable&lt;T&gt;</c>, and refused, naming
    /// the row, for any other <typeparamref name="T"/>.
    /// </summary>
    /// <remarks>
    /// The read attaches to the child arrays once — their buffers pinned for its length, the struct's
    /// offset applied to every child — and the compiled binding then constructs each row from them, a
    /// chunk at a time, into the span. Nothing is allocated per row for value fields and
    /// <c>Utf8String</c> or <c>ReadOnlyMemory&lt;byte&gt;</c> fields, which are slices of the batch's
    /// memory and valid while the batch is. A <c>string</c> or a <c>byte[]</c> field allocates a copy
    /// per row, and so does a record class.
    /// </remarks>
    /// <exception cref="ArgumentException">The array is not a struct array.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The rows run past the array's end.</exception>
    /// <exception cref="InvalidOperationException">
    /// <typeparamref name="T"/> cannot be read from the composite, or a cell is a NULL composite and
    /// <typeparamref name="T"/> cannot hold one.
    /// </exception>
    public static void ReadComposites<T>(this IArrowArray array, int start, Span<T> into)
    {
        var composite = CompositeAt(array, start, into.Length);
        CompositeReader<T>.For(composite).Read(composite, start, into);
    }

    private static StructArray CompositeAt(IArrowArray array, int start, int rows)
    {
        ArgumentNullException.ThrowIfNull(array);
        if (array is not StructArray composite)
        {
            throw new ArgumentException(
                $"Expected an Arrow struct array — a composite column — and got {array.Data.DataType.Name}.",
                nameof(array));
        }

        if (start < 0 || rows < 0 || (long)start + rows > composite.Length || (rows == 1 && start >= composite.Length))
        {
            throw new ArgumentOutOfRangeException(
                nameof(start), start, $"rows {start} to {(long)start + rows - 1} of an array of {composite.Length}");
        }

        return composite;
    }

    /// <summary>Every row of one batch, as <c>object?[]</c> in column order.</summary>
    public static List<object?[]> ToRows(this RecordBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);

        var rows = new List<object?[]>(batch.Length);
        var columns = new IArrowArray[batch.ColumnCount];
        for (var c = 0; c < batch.ColumnCount; c++)
        {
            columns[c] = batch.Column(c);
        }

        for (var r = 0; r < batch.Length; r++)
        {
            var row = new object?[batch.ColumnCount];
            for (var c = 0; c < batch.ColumnCount; c++)
            {
                row[c] = ValueAt(columns[c], r);
            }

            rows.Add(row);
        }

        return rows;
    }

    /// <summary>Every row of a sequence of batches.</summary>
    public static List<object?[]> ToRows(this IEnumerable<RecordBatch> batches)
    {
        ArgumentNullException.ThrowIfNull(batches);
        var rows = new List<object?[]>();
        foreach (var batch in batches)
        {
            rows.AddRange(batch.ToRows());
        }

        return rows;
    }

    /// <summary>One value. Null for a null slot.</summary>
    public static object? ValueAt(IArrowArray array, int index)
    {
        ArgumentNullException.ThrowIfNull(array);
        if (array.IsNull(index))
        {
            return null;
        }

        return array switch
        {
            BooleanArray a => a.GetValue(index),
            Int8Array a => a.GetValue(index),
            Int16Array a => a.GetValue(index),
            Int32Array a => a.GetValue(index),
            Int64Array a => a.GetValue(index),
            FloatArray a => a.GetValue(index),
            DoubleArray a => a.GetValue(index),
            StringArray a => a.GetString(index),
            StringViewArray a => a.GetString(index),
            Date32Array a => a.GetDateOnly(index),
            // Arrow 23's Time64Array exposes the raw unit count, not a TimeOnly.
            Time64Array a => TimeOnly.FromTimeSpan(TimeSpan.FromTicks(a.GetValue(index)!.Value * 10)),
            TimestampArray a => Timestamp(a, index),
            Decimal128Array a => a.GetValue(index),
            DurationArray a => a.GetValue(index),
            Apache.Arrow.Arrays.FixedSizeBinaryArray a => Uuid(a, index),
            BinaryArray a => a.GetBytes(index).ToArray(),
            StructArray a => Fields(a, index),
            _ => throw new NotSupportedException(
                $"no CLR mapping for Arrow array {array.GetType().Name}; see 04-client.md §7.2"),
        };
    }

    /// <summary>
    /// A COMPOSITE — what a function answering a record returns — as the <c>object?[]</c> of its fields
    /// in declared order, each read as a column of the field's own type would be. The fields are
    /// aligned with the composite's rows, so the composite's offset applies to them too.
    /// </summary>
    private static object?[] Fields(StructArray array, int index)
    {
        var fields = new object?[array.Data.Children.Length];
        for (var i = 0; i < fields.Length; i++)
        {
            // A view of the composite's own child data, which the composite owns: not disposed here.
            var field = ArrowArrayFactory.BuildArray(array.Data.Children[i]);
            fields[i] = ValueAt(field, array.Offset + index);
        }

        return fields;
    }

    /// <summary>
    /// A zone-less TIMESTAMP is a <see cref="DateTime"/> with <see cref="DateTimeKind.Unspecified"/>;
    /// a TIMESTAMP_TZ is a UTC <see cref="DateTimeOffset"/>.
    /// </summary>
    private static object Timestamp(TimestampArray array, int index)
    {
        var value = array.GetTimestamp(index)!.Value;
        var type = (TimestampType)array.Data.DataType;
        return string.IsNullOrEmpty(type.Timezone)
            ? DateTime.SpecifyKind(value.UtcDateTime, DateTimeKind.Unspecified)
            : value.ToUniversalTime();
    }

    /// <summary>UUID is a 16-byte fixed-size binary in RFC 4122 order (02-ir.md §3).</summary>
    private static object Uuid(Apache.Arrow.Arrays.FixedSizeBinaryArray array, int index)
    {
        var bytes = array.GetBytes(index);
        return bytes.Length == 16 ? new Guid(bytes, bigEndian: true) : bytes.ToArray();
    }
}
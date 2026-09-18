using System.Text;
using Apache.Arrow;
using Apache.Arrow.Types;

namespace Chalk.Arrow;

/// <summary>
/// Arrow batches as boxed CLR rows, with the value mapping of <b>Test kit and samples only</b>:
/// it boxes every value, which is precisely what the engine exists to avoid. Hosts read Arrow.
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
            _ => throw new NotSupportedException(
                $"no CLR mapping for Arrow array {array.GetType().Name}; see 04-client.md §7.2"),
        };
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
using System.Runtime.InteropServices;
using System.Text;
using Apache.Arrow;
using Chalk.Catalog;
using Chalk.Ir;
using Chalk.Sources;
using ArrowSchema = Apache.Arrow.Schema;

namespace Chalk.TestKit;

/// <summary>
/// Reads Arrow batches back into CLR values for tests and samples. Two views of the same data:
/// <see cref="ToRows"/> gives the host-facing types of <c>04-client.md</c> §7.2, and
/// <see cref="ToStorage"/> gives the exact storage a comparison has to use — a TIMESTAMP(9) does not
/// survive a round trip through <see cref="DateTime"/>'s 100-nanosecond ticks.
/// </summary>
public static class BatchReader
{
    /// <summary>The logical types of a batch's columns, read back from its Arrow schema.</summary>
    public static IReadOnlyList<ChalkType> TypesOf(ArrowSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        return [.. schema.FieldsList.Select(f => ArrowTypeMapping.FromArrow(f.DataType, f.IsNullable))];
    }

    /// <summary>Host-facing rows: the CLR types <c>04-client.md</c> §7.2 promises.</summary>
    public static List<object?[]> ToRows(RecordBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var types = TypesOf(batch.Schema);
        var rows = new List<object?[]>(batch.Length);
        for (var row = 0; row < batch.Length; row++)
        {
            var values = new object?[types.Count];
            for (var column = 0; column < types.Count; column++)
            {
                values[column] = Host(ToStorage(batch.Column(column), row, types[column]), types[column]);
            }

            rows.Add(values);
        }

        return rows;
    }

    /// <summary>Every row of every batch, as storage values.</summary>
    public static List<object?[]> ToStorageRows(IReadOnlyList<RecordBatch> batches)
    {
        ArgumentNullException.ThrowIfNull(batches);
        var rows = new List<object?[]>();
        foreach (var batch in batches)
        {
            var types = TypesOf(batch.Schema);
            for (var row = 0; row < batch.Length; row++)
            {
                var values = new object?[types.Count];
                for (var column = 0; column < types.Count; column++)
                {
                    values[column] = ToStorage(batch.Column(column), row, types[column]);
                }

                rows.Add(values);
            }
        }

        return rows;
    }

    /// <summary>
    /// One cell as its storage value: <c>bool</c>, <c>long</c> for every exact and temporal kind,
    /// <c>float</c>/<c>double</c>, <c>string</c>, <c>byte[]</c> or <c>decimal</c>.
    /// </summary>
    public static object? ToStorage(
        IArrowArray array,
        int row,
        ChalkType type)
    {
        ArgumentNullException.ThrowIfNull(array);

        var physicalIndex = array.Offset + row;
        var validity = array.Data.Buffers[0].Span;
        if (!validity.IsEmpty && !BitUtility.GetBit(validity, physicalIndex))
        {
            return null;
        }

        // A composite value has no value buffer of its own, only its fields (D291).
        if (type.Kind == TypeKind.Composite)
        {
            return Composite(array, physicalIndex, type);
        }

        var values = array.Data.Buffers[1].Span;
        return type.Kind switch
        {
            TypeKind.Bool => BitUtility.GetBit(values, physicalIndex),
            TypeKind.I8 => (long)(sbyte)values[physicalIndex],
            TypeKind.I16 => (long)MemoryMarshal.Cast<byte, short>(values)[physicalIndex],
            TypeKind.I32 or TypeKind.Date or TypeKind.IntervalYear => 
                (long)MemoryMarshal.Cast<byte, int>(values)[physicalIndex],
            TypeKind.Fp32 => MemoryMarshal.Cast<byte, float>(values)[physicalIndex],
            TypeKind.Fp64 => MemoryMarshal.Cast<byte, double>(values)[physicalIndex],

            //
            // Array-level accessors take the logical row.
            //
            TypeKind.String => Encoding.UTF8.GetString(VarBytes(array, row)),
            TypeKind.Binary => VarBytes(array, row).ToArray(),
            TypeKind.Uuid => values.Slice(physicalIndex * 16, 16).ToArray(),
            TypeKind.Decimal => Decimal(values.Slice(physicalIndex * 16, 16), type.Scale),
            TypeKind.List => List(array, physicalIndex, type.Element!.Value),

            _ =>
                MemoryMarshal
                    .Cast<byte, long>(
                        values)[physicalIndex],
        };
    }
    // public static object? ToStorage(IArrowArray array, int row, ChalkType type)
    // {
    //     ArgumentNullException.ThrowIfNull(array);
    //     var validity = array.Data.Buffers[0].Span;
    //     if (!validity.IsEmpty && !BitUtility.GetBit(validity, array.Offset + row))
    //     {
    //         return null;
    //     }
    //
    //     var values = array.Data.Buffers[1].Span;
    //     var index = array.Offset + row;
    //     return type.Kind switch
    //     {
    //         TypeKind.Bool => BitUtility.GetBit(values, index),
    //         TypeKind.I8 => (long)(sbyte)values[index],
    //         TypeKind.I16 => (long)MemoryMarshal.Cast<byte, short>(values)[index],
    //         TypeKind.I32 or TypeKind.Date or TypeKind.IntervalYear =>
    //             (long)MemoryMarshal.Cast<byte, int>(values)[index],
    //         TypeKind.Fp32 => MemoryMarshal.Cast<byte, float>(values)[index],
    //         TypeKind.Fp64 => MemoryMarshal.Cast<byte, double>(values)[index],
    //         TypeKind.String => Encoding.UTF8.GetString(VarBytes(array, index)),
    //         TypeKind.Binary => VarBytes(array, index).ToArray(),
    //         TypeKind.Uuid => values.Slice(index * 16, 16).ToArray(),
    //         TypeKind.Decimal => Decimal(values.Slice(index * 16, 16), type.Scale),
    //         TypeKind.List => List(array, index, type.Element!.Value),
    //         _ => MemoryMarshal.Cast<byte, long>(values)[index],
    //     };
    // }

    /// <summary>
    /// A COMPOSITE cell, as the <c>object?[]</c> of its fields in order (D291) — what the reference
    /// executor holds one as. The composite's physical row is each field's logical row, as Arrow aligns a
    /// composite's children.
    /// </summary>
    private static object?[] Composite(IArrowArray array, int index, ChalkType type)
    {
        var values = new object?[type.Fields.Count];
        for (var i = 0; i < values.Length; i++)
        {
            var child = ArrowArrayFactory.BuildArray(array.Data.Children[i]);
            values[i] = ToStorage(child, index, type.Fields[i].Type);
        }

        return values;
    }

    /// <summary>A LIST cell, as the <c>object?[]</c> of its elements (D58).</summary>
    private static object?[] List(IArrowArray array, int index, ChalkType element)
    {
        var offsets = MemoryMarshal.Cast<byte, int>(array.Data.Buffers[1].Span);
        var child = ArrowArrayFactory.BuildArray(array.Data.Children[0]);
        var start = offsets[index];
        var values = new object?[offsets[index + 1] - start];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = ToStorage(child, start + i, element);
        }

        return values;
    }

    // private static ReadOnlySpan<byte> VarBytes(IArrowArray array, int index)
    // {
    //     var offsets = MemoryMarshal.Cast<byte, int>(array.Data.Buffers[1].Span);
    //     return array.Data.Buffers[2].Span[offsets[index]..offsets[index + 1]];
    // }

    public static ReadOnlySpan<byte> VarBytes(IArrowArray array, int logicalIndex)
    {
        switch (array)
        {
            case BinaryViewArray view:
                return view.GetBytes(logicalIndex);

            case BinaryArray binary:
                return binary.GetBytes(logicalIndex);

            default:
                throw new ArgumentException(
                    $"Array type {array.GetType().Name} "
                    + "is not a variable-byte array.",
                    nameof(array));
        }
    }

    /// <summary>
    /// A 16-byte little-endian two's-complement unscaled value as a <see cref="decimal"/> — the
    /// storage form Arrow and the IR both use.
    /// </summary>
    public static decimal Decimal(ReadOnlySpan<byte> lane, int scale)
    {
        var negative = (lane[15] & 0x80) != 0;
        Span<byte> work = stackalloc byte[16];
        lane.CopyTo(work);
        if (negative)
        {
            for (var i = 0; i < 16; i++)
            {
                work[i] = (byte)~work[i];
            }

            var carry = 1;
            for (var i = 0; i < 16 && carry != 0; i++)
            {
                var sum = work[i] + carry;
                work[i] = (byte)sum;
                carry = sum >> 8;
            }
        }

        var magnitude = System.Numerics.BigInteger.Zero;
        for (var i = 15; i >= 0; i--)
        {
            magnitude = (magnitude * 256) + work[i];
        }

        var result = (decimal)magnitude;
        for (var i = 0; i < scale; i++)
        {
            result /= 10m;
        }

        return negative ? -result : result;
    }

    /// <summary>Storage → the host-facing CLR type of §7.2.</summary>
    private static object? Host(object? storage, ChalkType type) => storage switch
    {
        null => null,
        long value => type.Kind switch
        {
            TypeKind.I8 => (sbyte)value,
            TypeKind.I16 => (short)value,
            TypeKind.I32 => (int)value,
            TypeKind.Date => DateOnly.FromDayNumber((int)value + 719_162),
            TypeKind.Time => new TimeOnly(value * TimeSpan.TicksPerMicrosecond),
            TypeKind.Timestamp => Epoch(value, type),
            TypeKind.TimestampTz => new DateTimeOffset(Epoch(value, type), TimeSpan.Zero),
            TypeKind.IntervalDay => new TimeSpan(value * TimeSpan.TicksPerMicrosecond),
            TypeKind.IntervalYear => (int)value,
            _ => value,
        },
        byte[] bytes when type.Kind == TypeKind.Uuid => new Guid(bytes, bigEndian: true),
        object?[] fields when type.Kind == TypeKind.Composite =>
            fields.Select((field, i) => Host(field, type.Fields[i].Type)).ToArray(),
        _ => storage,
    };

    private static DateTime Epoch(long units, ChalkType type)
    {
        var perSecond = IrTypes.TimestampUnitsPerSecond((uint)type.Precision);
        var ticks = units * (TimeSpan.TicksPerSecond / (double)perSecond);
        return new DateTime(DateTime.UnixEpoch.Ticks + (long)ticks, DateTimeKind.Unspecified);
    }
}
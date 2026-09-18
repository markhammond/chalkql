using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Apache.Arrow;
using Apache.Arrow.Memory;
using Apache.Arrow.Types;
using Chalk.Catalog;
using Chalk.Ir;
using Chalk.Sources;
using ArrowSchema = Apache.Arrow.Schema;

namespace Chalk.Execution.Reference;

/// <summary>
/// How the reference executor holds a value: boxed, one per cell, with the storage integer for the
/// temporal kinds so nothing is lost to a CLR calendar type (D13, §8).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>BOOL → <see cref="bool"/></item>
/// <item>I8 … I64, DATE, TIME, TIMESTAMP, TIMESTAMP_TZ, INTERVAL_* → <see cref="long"/></item>
/// <item>FP32 → <see cref="float"/>, FP64 → <see cref="double"/></item>
/// <item>STRING → <see cref="string"/>; BINARY and UUID → <c>byte[]</c>; DECIMAL → <see cref="decimal"/></item>
/// </list>
/// This is deliberately not the engine's layout: the point of I4 is that the two implementations
/// disagree loudly rather than share a misunderstanding.
/// </remarks>
internal static class ReferenceValues
{
    /// <summary>Reads one Arrow batch into boxed rows.</summary>
    public static void ReadBatch(RecordBatch batch, IReadOnlyList<ChalkType> types, List<object?[]> rows)
    {
        for (var row = 0; row < batch.Length; row++)
        {
            var values = new object?[types.Count];
            for (var column = 0; column < types.Count; column++)
            {
                values[column] = Read(batch.Column(column), row, types[column]);
            }

            rows.Add(values);
        }
    }

    /// <summary>Reads one cell of an Arrow array.</summary>
    public static object? Read(
        IArrowArray array,
        int row,
        ChalkType type)
    {
        var validity =
            array.Data.Buffers[0].Span;

        var index =
            array.Offset + row;

        if (!validity.IsEmpty &&
            !BitUtility.GetBit(
                validity,
                index))
        {
            return null;
        }

        var values =
            array.Data.Buffers[1].Span;

        switch (type.Kind)
        {
            case TypeKind.Bool:
                return BitUtility.GetBit(
                    values,
                    index);

            case TypeKind.I8:
                return (long)(sbyte)
                    values[index];

            case TypeKind.I16:
                return (long)
                    MemoryMarshal
                        .Cast<byte, short>(
                            values)[index];

            case TypeKind.I32:
            case TypeKind.Date:
            case TypeKind.IntervalYear:
                return (long)
                    MemoryMarshal
                        .Cast<byte, int>(
                            values)[index];

            case TypeKind.I64:
            case TypeKind.Time:
            case TypeKind.Timestamp:
            case TypeKind.TimestampTz:
            case TypeKind.IntervalDay:
                return MemoryMarshal
                    .Cast<byte, long>(
                        values)[index];

            case TypeKind.Fp32:
                return MemoryMarshal
                    .Cast<byte, float>(
                        values)[index];

            case TypeKind.Fp64:
                return MemoryMarshal
                    .Cast<byte, double>(
                        values)[index];

            case TypeKind.String:
                return Encoding.UTF8.GetString(
                    VarBytes(
                        array,
                        row));

            case TypeKind.Binary:
                return VarBytes(
                        array,
                        row)
                    .ToArray();

            case TypeKind.Uuid:
                return values
                    .Slice(
                        index * 16,
                        16)
                    .ToArray();

            case TypeKind.List:
            {
                var offsets =
                    MemoryMarshal.Cast<byte, int>(
                        values);

                var elements =
                    ArrowArrayFactory.BuildArray(
                        array.Data.Children[0]);

                var element =
                    type.Element!.Value;

                var start =
                    offsets[index];

                var list =
                    new object?[
                        offsets[index + 1] -
                        start];

                for (var i = 0;
                     i < list.Length;
                     i++)
                {
                    list[i] =
                        Read(
                            elements,
                            start + i,
                            element);
                }

                return list;
            }

            case TypeKind.Decimal:
                return ReadDecimal(
                    values.Slice(
                        index * 16,
                        16),
                    type.Scale);

            default:
                throw new NotSupportedException(
                    $"ReferenceValues cannot read {type.Kind}.");
        }
    }

    /// <summary>Decodes a DECIMAL lane. Written out longhand rather than shared with the engine (D13).</summary>
    public static decimal ReadDecimal(ReadOnlySpan<byte> lane, int scale)
    {
        var magnitude = System.Numerics.BigInteger.Zero;
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
    
    private static ReadOnlySpan<byte> VarBytes(
        IArrowArray array,
        int row)
    {
        return array switch
        {
            BinaryViewArray view =>
                view.GetBytes(row),

            BinaryArray binary =>
                binary.GetBytes(row),

            _ =>
                throw new InvalidOperationException(
                    $"Arrow array {array.GetType().Name} "
                    + "does not have variable-byte storage."),
        };
    }

    /// <summary>Encodes a DECIMAL lane, rounding half-even to the target scale.</summary>
    public static void WriteDecimal(Span<byte> lane, decimal value, int precision, int scale)
    {
        var rounded = Math.Round(value, scale, MidpointRounding.ToEven);
        var scaled = rounded;
        for (var i = 0; i < scale; i++)
        {
            scaled *= 10m;
        }

        var magnitude = new System.Numerics.BigInteger(decimal.Truncate(scaled));
        var limit = System.Numerics.BigInteger.Pow(10, precision);
        if (System.Numerics.BigInteger.Abs(magnitude) >= limit)
        {
            throw new OverflowException(
                $"{value.ToString(CultureInfo.InvariantCulture)} does not fit in DECIMAL({precision},{scale}).");
        }

        lane.Clear();
        if (magnitude.Sign < 0)
        {
            lane.Fill(0xFF);
        }

        magnitude.TryWriteBytes(lane, out _, isUnsigned: false, isBigEndian: false);
    }

    /// <summary>Builds Arrow batches from boxed rows, using the shared type mapping (§8).</summary>
    public static IEnumerable<RecordBatch> ToBatches(
        IReadOnlyList<object?[]> rows,
        ArrowSchema schema,
        IReadOnlyList<ChalkType> types,
        int batchSize,
        MemoryAllocator allocator)
    {
        for (var start = 0; start < rows.Count; start += batchSize)
        {
            var count = Math.Min(batchSize, rows.Count - start);
            var columns = new IArrowArray[types.Count];
            for (var c = 0; c < types.Count; c++)
            {
                columns[c] =
                    BuildColumn(
                        rows,
                        start,
                        count,
                        c,
                        types[c],
                        schema.GetFieldByIndex(c).DataType,
                        allocator);
            }

            yield return new RecordBatch(schema, columns, count);
        }
    }

    private static IArrowArray BuildColumn(
        IReadOnlyList<object?[]> rows,
        int start,
        int count,
        int column,
        ChalkType type,
        IArrowType arrowType,
        MemoryAllocator allocator)
    {
        var validity = new ArrowBuffer.BitmapBuilder(count);
        var nulls = 0;
        for (var i = 0; i < count; i++)
        {
            var present = rows[start + i][column] is not null;
            validity.Append(present);
            if (!present)
            {
                nulls++;
            }
        }

        var nullBuffer = nulls == 0 ? ArrowBuffer.Empty : validity.Build(allocator);

        if (type.Kind == TypeKind.List)
        {
            // The child column is built the same way, over the elements of every list end to end.
            var element = type.Element!.Value;
            var flattened = new List<object?[]>();
            var listOffsets = new ArrowBuffer.Builder<int>(count + 1);
            listOffsets.Append(0);
            for (var i = 0; i < count; i++)
            {
                if (rows[start + i][column] is object?[] list)
                {
                    foreach (var value in list)
                    {
                        flattened.Add([value]);
                    }
                }

                listOffsets.Append(flattened.Count);
            }

            var listType =
                (ListType)arrowType;

            var child =
                BuildColumn(
                    flattened,
                    0,
                    flattened.Count,
                    0,
                    element,
                    listType.Fields[0].DataType,
                    allocator);

            return new ListArray(new ArrayData(
                arrowType,
                count,
                nulls,
                0,
                [nullBuffer, listOffsets.Build(allocator)],
                [child.Data]));
        }

        if (type.Kind == TypeKind.String)
        {
            return arrowType switch
            {
                StringViewType =>
                    BuildStringView(
                        rows,
                        start,
                        count,
                        column,
                        allocator),

                StringType =>
                    BuildClassicString(
                        rows,
                        start,
                        count,
                        column,
                        nullBuffer,
                        nulls,
                        allocator),

                _ =>
                    throw new InvalidOperationException(
                        $"STRING cannot be represented as Arrow {arrowType.Name}."),
            };
        }
        if (type.Kind is TypeKind.Binary)
        {
            var offsets = new ArrowBuffer.Builder<int>(count + 1);
            var data = new ArrowBuffer.Builder<byte>(count * 8);
            var used = 0;
            offsets.Append(0);
            for (var i = 0; i < count; i++)
            {
                var value = rows[start + i][column];
                if (value is not null)
                {
                    var bytes = type.Kind == TypeKind.String
                        ? Encoding.UTF8.GetBytes((string)value)
                        : (byte[])value;
                    data.Append(bytes);
                    used += bytes.Length;
                }

                offsets.Append(used);
            }

            return ArrowArrayFactory.BuildArray(new ArrayData(
                arrowType, count, nulls, 0, [nullBuffer, offsets.Build(allocator), data.Build(allocator)]));
        }

        if (type.Kind == TypeKind.Bool)
        {
            var bits = new ArrowBuffer.BitmapBuilder(count);
            for (var i = 0; i < count; i++)
            {
                bits.Append(rows[start + i][column] is true);
            }

            return ArrowArrayFactory.BuildArray(new ArrayData(
                arrowType, count, nulls, 0, [nullBuffer, bits.Build(allocator)]));
        }

        var width = Width(type.Kind);
        var lanes = new byte[count * width];
        for (var i = 0; i < count; i++)
        {
            var value = rows[start + i][column];
            if (value is null)
            {
                continue;
            }

            var lane = lanes.AsSpan(i * width, width);
            switch (type.Kind)
            {
                case TypeKind.I8:
                    lane[0] = (byte)(sbyte)(long)value;
                    break;
                case TypeKind.I16:
                    MemoryMarshal.Write(lane, (short)(long)value);
                    break;
                case TypeKind.I32:
                case TypeKind.Date:
                case TypeKind.IntervalYear:
                    MemoryMarshal.Write(lane, (int)(long)value);
                    break;
                case TypeKind.Fp32:
                    MemoryMarshal.Write(lane, (float)value);
                    break;
                case TypeKind.Fp64:
                    MemoryMarshal.Write(lane, (double)value);
                    break;
                case TypeKind.Decimal:
                    WriteDecimal(lane, (decimal)value, type.Precision, type.Scale);
                    break;
                case TypeKind.Uuid:
                    ((byte[])value).CopyTo(lane);
                    break;
                default:
                    MemoryMarshal.Write(lane, (long)value);
                    break;
            }
        }

        var values = new ArrowBuffer.Builder<byte>(lanes.Length);
        values.Append(lanes);
        return ArrowArrayFactory.BuildArray(new ArrayData(
            arrowType, count, nulls, 0, [nullBuffer, values.Build(allocator)]));
    }
    
    private static StringViewArray BuildStringView(
        IReadOnlyList<object?[]> rows,
        int start,
        int count,
        int column,
        MemoryAllocator allocator)
    {
        var builder =
            new StringViewArray.Builder();

        builder.Reserve(count);

        for (var i = 0; i < count; i++)
        {
            if (rows[start + i][column] is string value)
            {
                builder.Append(value);
            }
            else
            {
                builder.AppendNull();
            }
        }

        return builder.Build(allocator);
    }
    
    private static IArrowArray BuildClassicString(
        IReadOnlyList<object?[]> rows,
        int start,
        int count,
        int column,
        ArrowBuffer nullBuffer,
        int nulls,
        MemoryAllocator allocator)
    {
        var offsets =
            new ArrowBuffer.Builder<int>(
                count + 1);

        var data =
            new ArrowBuffer.Builder<byte>(
                count * 8);

        var used = 0;

        offsets.Append(0);

        for (var i = 0; i < count; i++)
        {
            if (rows[start + i][column] is string value)
            {
                var bytes =
                    Encoding.UTF8.GetBytes(
                        value);

                data.Append(
                    bytes);

                used =
                    checked(
                        used +
                        bytes.Length);
            }

            offsets.Append(
                used);
        }

        return new StringArray(
            count,
            offsets.Build(allocator),
            data.Build(allocator),
            nullBuffer,
            nulls,
            offset: 0);
    }

    private static int Width(TypeKind kind) => kind switch
    {
        TypeKind.I8 => 1,
        TypeKind.I16 => 2,
        TypeKind.I32 or TypeKind.Fp32 or TypeKind.Date or TypeKind.IntervalYear => 4,
        TypeKind.Decimal or TypeKind.Uuid => 16,
        _ => 8,
    };

    /// <summary>
    /// The reference executor's total order: NULLs are handled by the caller, NaN is the largest
    /// value, and strings compare by UTF-8 bytes, which is code point order (<c>02-ir.md</c> §3).
    /// </summary>
    public static int Compare(object? left, object? right)
    {
        return (left, right) switch
        {
            (bool a, bool b) => a.CompareTo(b),
            (long a, long b) => a.CompareTo(b),
            (double a, double b) => CompareReal(a, b),
            (float a, float b) => CompareReal(a, b),
            (decimal a, decimal b) => a.CompareTo(b),
            (string a, string b) => CompareUtf8(a, b),
            (byte[] a, byte[] b) => a.AsSpan().SequenceCompareTo(b),
            _ => throw new InvalidOperationException(
                $"The reference executor cannot order {left?.GetType().Name} against {right?.GetType().Name}."),
        };

        static int CompareReal(double a, double b)
        {
            if (double.IsNaN(a))
            {
                return double.IsNaN(b) ? 0 : 1;
            }

            return double.IsNaN(b) ? -1 : a < b ? -1 : a > b ? 1 : 0;
        }
    }

    /// <summary>Strings order by their UTF-8 bytes, not by the CLR's default ordinal char order.</summary>
    public static int CompareUtf8(string left, string right)
    {
        var a = Encoding.UTF8.GetBytes(left);
        var b = Encoding.UTF8.GetBytes(right);
        return a.AsSpan().SequenceCompareTo(b);
    }

    /// <summary>Equality for grouping and DISTINCT: NULLs are equal, NaNs are equal, -0.0 equals 0.0.</summary>
    public static bool GroupEquals(object? left, object? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return (left, right) switch
        {
            (double a, double b) => double.IsNaN(a) ? double.IsNaN(b) : a == b,
            (float a, float b) => float.IsNaN(a) ? float.IsNaN(b) : a == b,
            (byte[] a, byte[] b) => a.AsSpan().SequenceEqual(b),
            _ => left.Equals(right),
        };
    }

    /// <summary>The hash that goes with <see cref="GroupEquals"/>.</summary>
    public static int GroupHash(object? value) => value switch
    {
        null => 0,
        double d => double.IsNaN(d) ? -1 : d == 0d ? 0 : d.GetHashCode(),
        float f => float.IsNaN(f) ? -1 : f == 0f ? 0 : ((double)f).GetHashCode(),
        byte[] bytes => Structural(bytes),
        _ => value.GetHashCode(),
    };

    private static int Structural(byte[] bytes)
    {
        var hash = 17;
        foreach (var b in bytes)
        {
            hash = (hash * 31) + b;
        }

        return hash;
    }

    /// <summary>A group key: the tuple of grouping values, compared with SQL grouping semantics.</summary>
    public sealed class RowKey : IEquatable<RowKey>
    {
        private readonly object?[] _values;
        private readonly int _hash;

        public RowKey(object?[] values)
        {
            _values = values;
            var hash = 19;
            foreach (var value in values)
            {
                hash = (hash * 31) + GroupHash(value);
            }

            _hash = hash;
        }

        public object?[] Values => _values;

        public bool Equals(RowKey? other)
        {
            if (other is null || other._values.Length != _values.Length)
            {
                return false;
            }

            for (var i = 0; i < _values.Length; i++)
            {
                if (!GroupEquals(_values[i], other._values[i]))
                {
                    return false;
                }
            }

            return true;
        }

        public override bool Equals(object? obj) => Equals(obj as RowKey);

        public override int GetHashCode() => _hash;
    }
}
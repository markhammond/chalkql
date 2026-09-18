using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Apache.Arrow;
using Chalk.Catalog;
using Chalk.Ir;
using ArrowField = Apache.Arrow.Field;
using ArrowSchema = Apache.Arrow.Schema;

namespace Chalk.TestKit;

/// <summary>
/// The inverse of <see cref="BatchReader"/>: builds a <see cref="RecordBatch"/> from rows of the
/// storage values <see cref="BatchReader.ToStorage"/> produces — <c>bool</c>, <c>long</c> for every
/// exact and temporal kind, <c>float</c>/<c>double</c>, <c>string</c>, <c>byte[]</c>,
/// <c>decimal</c>.
/// </summary>
/// <remarks>
/// This exists so a third oracle can be compared with the same <see cref="ResultComparer"/> as the
/// two executors (D28). Buffers are written by hand rather than through Arrow's builders because the
/// storage form is the point: a TIMESTAMP(9) does not survive a round trip through
/// <see cref="DateTime"/>, so it has to be laid down as the int64 it is.
/// </remarks>
public static class BatchBuilder
{
    /// <summary>One batch holding every row, matching <paramref name="schema"/> exactly.</summary>
    public static RecordBatch FromStorageRows(ArrowSchema schema, IReadOnlyList<object?[]> rows)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(rows);

        var types = BatchReader.TypesOf(schema);
        var arrays = new IArrowArray[types.Count];
        for (var column = 0; column < types.Count; column++)
        {
            arrays[column] = Column(schema.FieldsList[column], types[column], rows, column);
        }

        return new RecordBatch(schema, arrays, rows.Count);
    }

    private static IArrowArray Column(
        ArrowField field, ChalkType type, IReadOnlyList<object?[]> rows, int column)
    {
        var length = rows.Count;
        var validity = new byte[(length + 7) / 8];
        var nulls = 0;
        for (var row = 0; row < length; row++)
        {
            if (rows[row][column] is null)
            {
                nulls++;
            }
            else
            {
                BitUtility.SetBit(validity, row);
            }
        }

        // A LIST is the one column whose payload is another column (D58): its offsets, and a child
        // built by this same method over the elements flattened end to end.
        if (type.Kind == TypeKind.List)
        {
            var offsets = new byte[(length + 1) * sizeof(int)];
            var lanes = MemoryMarshal.Cast<byte, int>(offsets.AsSpan());
            var flattened = new List<object?[]>();
            for (var row = 0; row < length; row++)
            {
                if (rows[row][column] is object?[] list)
                {
                    foreach (var element in list)
                    {
                        flattened.Add([element]);
                    }
                }

                lanes[row + 1] = flattened.Count;
            }

            var elementField = ((Apache.Arrow.Types.ListType)field.DataType).ValueField;
            var child = Column(elementField, type.Element!.Value, flattened, 0);
            return ArrowArrayFactory.BuildArray(new ArrayData(
                field.DataType,
                length,
                nulls,
                0,
                [new ArrowBuffer(validity), new ArrowBuffer(offsets)],
                [child.Data]));
        }

        var buffers = field.DataType switch
        {
            // D244: a STRING column is declared in one Arrow layout or the other, and the buffers
            // have to be that layout's. Views are built from the classic ones this method already
            // lays down, which is also what makes the two spellings the same bytes.
            Apache.Arrow.Types.StringViewType =>
                StringViews(VarLength(type, rows, column, validity), length),
            _ => type.Kind switch
            {
                TypeKind.String or TypeKind.Binary => VarLength(type, rows, column, validity),
                _ => [new ArrowBuffer(validity), new ArrowBuffer(Fixed(type, rows, column))],
            },
        };

        return ArrowArrayFactory.BuildArray(
            new ArrayData(field.DataType, length, nulls, 0, buffers));
    }

    /// <summary>
    /// Classic validity/offsets/payload as a string view's validity, sixteen-byte lanes and variadic
    /// buffer zero (D244): twelve bytes or fewer inline, anything longer as prefix, buffer index and
    /// offset into the payload this builder already wrote.
    /// </summary>
    private static ArrowBuffer[] StringViews(ArrowBuffer[] classic, int length)
    {
        const int Width = 16;
        const int InlineLength = 12;

        var offsets = MemoryMarshal.Cast<byte, int>(classic[1].Span);
        var payload = classic[2].Span;
        var lanes = new byte[length * Width];
        var views = MemoryMarshal.Cast<byte, int>(lanes.AsSpan());

        var external = false;
        for (var row = 0; row < length; row++)
        {
            var start = offsets[row];
            var size = offsets[row + 1] - start;
            views[row * 4] = size;
            if (size <= InlineLength)
            {
                if (size != 0)
                {
                    payload.Slice(start, size).CopyTo(lanes.AsSpan((row * Width) + sizeof(int), size));
                }

                continue;
            }

            views[(row * 4) + 1] = MemoryMarshal.Read<int>(payload.Slice(start, sizeof(int)));
            views[(row * 4) + 3] = start;
            external = true;
        }

        return external
            ? [classic[0], new ArrowBuffer(lanes), classic[2]]
            : [classic[0], new ArrowBuffer(lanes)];
    }

    private static ArrowBuffer[] VarLength(
        ChalkType type, IReadOnlyList<object?[]> rows, int column, byte[] validity)
    {
        var offsets = new byte[(rows.Count + 1) * sizeof(int)];
        var lanes = MemoryMarshal.Cast<byte, int>(offsets.AsSpan());
        var data = new List<byte>(rows.Count * 8);
        for (var row = 0; row < rows.Count; row++)
        {
            var value = rows[row][column];
            if (value is not null)
            {
                data.AddRange(type.Kind == TypeKind.String
                    ? Encoding.UTF8.GetBytes((string)value)
                    : (byte[])value);
            }

            lanes[row + 1] = data.Count;
        }

        return [new ArrowBuffer(validity), new ArrowBuffer(offsets), new ArrowBuffer(data.ToArray())];
    }

    private static byte[] Fixed(ChalkType type, IReadOnlyList<object?[]> rows, int column)
    {
        if (type.Kind == TypeKind.Bool)
        {
            var bits = new byte[(rows.Count + 7) / 8];
            for (var row = 0; row < rows.Count; row++)
            {
                if (rows[row][column] is true)
                {
                    BitUtility.SetBit(bits, row);
                }
            }

            return bits;
        }

        var width = Width(type.Kind);
        var values = new byte[rows.Count * width];
        for (var row = 0; row < rows.Count; row++)
        {
            var value = rows[row][column];
            if (value is null)
            {
                continue;
            }

            var lane = values.AsSpan(row * width, width);
            switch (type.Kind)
            {
                case TypeKind.I8:
                    lane[0] = (byte)(sbyte)(long)value;
                    break;
                case TypeKind.I16:
                    MemoryMarshal.Write(lane, (short)(long)value);
                    break;
                case TypeKind.I32 or TypeKind.Date or TypeKind.IntervalYear:
                    MemoryMarshal.Write(lane, (int)(long)value);
                    break;
                case TypeKind.Fp32:
                    MemoryMarshal.Write(lane, (float)value);
                    break;
                case TypeKind.Fp64:
                    MemoryMarshal.Write(lane, (double)value);
                    break;
                case TypeKind.Uuid:
                    ((byte[])value).CopyTo(lane);
                    break;
                case TypeKind.Decimal:
                    WriteDecimal(lane, (decimal)value, type.Scale);
                    break;
                default:
                    MemoryMarshal.Write(lane, (long)value);
                    break;
            }
        }

        return values;
    }

    private static int Width(TypeKind kind) => kind switch
    {
        TypeKind.I8 => 1,
        TypeKind.I16 => 2,
        TypeKind.I32 or TypeKind.Fp32 or TypeKind.Date or TypeKind.IntervalYear => 4,
        TypeKind.Decimal or TypeKind.Uuid => 16,
        _ => 8,
    };

    /// <summary>128-bit two's complement, little-endian, at the column's scale.</summary>
    /// <remarks>
    /// The unscaled integer is taken from <see cref="decimal.GetBits(decimal)"/> and rescaled in
    /// <see cref="BigInteger"/>, not by multiplying the <see cref="decimal"/> by a power of ten:
    /// a 128-bit column can hold values a <see cref="decimal"/> cannot be scaled to. The largest
    /// value at scale 28 is 7.9228162514264337593543950335, whose unscaled form is
    /// 79228162514264337593543950335 — comfortably inside 128 bits, and an overflow the moment you
    /// try to reach it by multiplying by 10^28 (§5 C, ADR 0024).
    /// </remarks>
    private static void WriteDecimal(Span<byte> lane, decimal value, int scale)
    {
        var rounded = decimal.Round(value, scale, MidpointRounding.ToEven);
        Span<int> bits = stackalloc int[4];
        _ = decimal.GetBits(rounded, bits);

        // bits[0..2] are the 96-bit mantissa, low word first; bits[3] carries the sign in its top
        // bit and the scale in bits 16-23. decimal.Round has already brought the scale down to at
        // most the column's, so the rescale below never divides.
        var mantissa = (new BigInteger((uint)bits[2]) << 64)
            | (new BigInteger((uint)bits[1]) << 32)
            | new BigInteger((uint)bits[0]);
        var unscaled = mantissa * BigInteger.Pow(10, scale - ((bits[3] >> 16) & 0xFF));
        if (bits[3] < 0)
        {
            unscaled = -unscaled;
        }

        if (!unscaled.TryWriteBytes(lane, out _, isUnsigned: false, isBigEndian: false))
        {
            throw new OverflowException($"{value} does not fit a 128-bit decimal at scale {scale}");
        }

        if (unscaled.Sign < 0)
        {
            // TryWriteBytes stops at the last significant byte; a negative value's remaining bytes
            // are its sign extension.
            for (var i = unscaled.GetByteCount(); i < lane.Length; i++)
            {
                lane[i] = 0xFF;
            }
        }
    }
}

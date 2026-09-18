using System.Runtime.InteropServices;
using Chalk.Execution.Numeric;
using Chalk.Execution.Vectors;

namespace Chalk.Execution.Aggregation;

/// <summary>
/// Reads one row of a vector as raw lane bytes. The aggregate stores keys and MIN/MAX states as
/// bytes, so hashing, equality and comparison are all one shape whatever the column's type (§6.6).
/// </summary>
internal static class LaneAccess
{
    /// <summary>
    /// The lane's bytes. Fixed-width lanes are returned as a view into the source where possible;
    /// booleans and floating point are normalised into <paramref name="scratch"/>.
    /// </summary>
    public static ReadOnlySpan<byte> Read(
        in Vector vector, int row, ColumnKind kind, int width, Span<byte> scratch)
    {
        if (vector.IsScalar)
        {
            if (ColumnKinds.IsVariableLength(kind))
            {
                return vector.Scalar.ReadBytes();
            }

            ScalarLanes.Write(kind, vector.Scalar, scratch[..width]);
            return Normalise(kind, scratch[..width]);
        }

        var view = vector.View;
        switch (kind)
        {
            case ColumnKind.Utf8:
            case ColumnKind.Binary:
                return view.VarValue(row);

            case ColumnKind.Boolean:
                scratch[0] = (byte)(view.BoolAt(row) ? 1 : 0);
                return scratch[..1];

            case ColumnKind.Float:
            case ColumnKind.Double:
                view.RawLanes(width).Slice(row * width, width).CopyTo(scratch);
                return Normalise(kind, scratch[..width]);

            default:
                return view.RawLanes(width).Slice(row * width, width);
        }
    }

    public static bool IsValid(in Vector vector, int row) => vector.IsScalar
        ? !vector.Scalar.IsNull
        : vector.View.IsValid(row);

    /// <summary>
    /// Grouping compares floating point by value, not by bits: every NaN is one group and -0.0 groups
    /// with 0.0. Both executors normalise the same way, so a differential run cannot disagree here.
    /// </summary>
    internal static ReadOnlySpan<byte> Normalise(ColumnKind kind, Span<byte> lane)
    {
        switch (kind)
        {
            case ColumnKind.Float:
            {
                var value = MemoryMarshal.Read<float>(lane);
                if (float.IsNaN(value))
                {
                    MemoryMarshal.Write(lane, float.NaN);
                }
                else if (value == 0f)
                {
                    MemoryMarshal.Write(lane, 0f);
                }

                break;
            }

            case ColumnKind.Double:
            {
                var value = MemoryMarshal.Read<double>(lane);
                if (double.IsNaN(value))
                {
                    MemoryMarshal.Write(lane, double.NaN);
                }
                else if (value == 0d)
                {
                    MemoryMarshal.Write(lane, 0d);
                }

                break;
            }

            default:
                break;
        }

        return lane;
    }
}

/// <summary>
/// Orders two lanes of the same layout, for MIN and MAX. The rules are those of <c>02-ir.md</c> §3:
/// bytes for STRING, BINARY and UUID, unscaled magnitude for DECIMAL, IEEE with NaN largest for the
/// floating-point kinds.
/// </summary>
internal static class LaneComparer
{
    public static int Compare(ColumnKind kind, ReadOnlySpan<byte> left, ReadOnlySpan<byte> right) => kind switch
    {
        ColumnKind.Boolean => left[0].CompareTo(right[0]),
        ColumnKind.Int8 => ((sbyte)left[0]).CompareTo((sbyte)right[0]),
        ColumnKind.Int16 => MemoryMarshal.Read<short>(left).CompareTo(MemoryMarshal.Read<short>(right)),
        ColumnKind.Int32 => MemoryMarshal.Read<int>(left).CompareTo(MemoryMarshal.Read<int>(right)),
        ColumnKind.Int64 => MemoryMarshal.Read<long>(left).CompareTo(MemoryMarshal.Read<long>(right)),
        ColumnKind.Float => Operators.DoubleComparer.Compare(
            MemoryMarshal.Read<float>(left), MemoryMarshal.Read<float>(right)),
        ColumnKind.Double => Operators.DoubleComparer.Compare(
            MemoryMarshal.Read<double>(left), MemoryMarshal.Read<double>(right)),
        ColumnKind.Decimal128 => Decimals.Compare(left, right),
        _ => left.SequenceCompareTo(right),
    };
}

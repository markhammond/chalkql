using System.Runtime.InteropServices;
using Chalk.Sources;

namespace Chalk.Execution.Vectors;

/// <summary>Writes a broadcast constant into a fixed-width lane, in the layout <c>02-ir.md</c> §3 fixes.</summary>
internal static class ScalarLanes
{
    public static void Write(ColumnKind kind, ScalarValue value, Span<byte> lane)
    {
        switch (kind)
        {
            case ColumnKind.Boolean:
                lane[0] = (byte)(value.Integer != 0 ? 1 : 0);
                break;
            case ColumnKind.Int8:
                lane[0] = (byte)(sbyte)value.Integer;
                break;
            case ColumnKind.Int16:
                MemoryMarshal.Write(lane, (short)value.Integer);
                break;
            case ColumnKind.Int32:
                MemoryMarshal.Write(lane, (int)value.Integer);
                break;
            case ColumnKind.Int64:
                MemoryMarshal.Write(lane, value.Integer);
                break;
            case ColumnKind.Float:
                MemoryMarshal.Write(lane, value.Single);
                break;
            case ColumnKind.Double:
                MemoryMarshal.Write(lane, value.Double);
                break;
            case ColumnKind.Decimal128:
            case ColumnKind.Bytes16:
                lane.Clear();
                value.ReadBytes().CopyTo(lane);
                break;
            default:
                throw new UnsupportedFeatureException(
                    $"constant of layout {kind}",
                    "It has no fixed-width lane; variable-length constants are appended, not written.");
        }
    }
}

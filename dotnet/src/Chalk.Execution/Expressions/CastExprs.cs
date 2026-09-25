using System.Globalization;
using System.Text;
using Apache.Arrow;
using Chalk.Catalog;
using Chalk.Execution.Numeric;
using Chalk.Execution.Vectors;
using Chalk.Ir;

namespace Chalk.Execution.Expressions;

/// <summary>
/// Shared shape of the cast nodes: read a lane, convert it, and let
/// <c>CAST_FAILURE_NULL</c> turn a conversion failure into a NULL instead of an error
/// (<c>expr.proto</c>, <c>02-ir.md</c> §6).
/// </summary>
internal abstract class CastExprBase : VectorExprBase
{
    protected CastExprBase(ChalkType type, IVectorExpr operand, CastFailure onFailure)
        : base(type)
    {
        Operand = operand;
        Source = operand.Type;
        OnFailure = onFailure;
    }

    protected IVectorExpr Operand { get; }

    protected ChalkType Source { get; }

    protected CastFailure OnFailure { get; }

    /// <summary>True when the caller should emit NULL rather than rethrow.</summary>
    protected bool Recover(Exception exception) => OnFailure == CastFailure.Null
        && exception is OverflowException or FormatException or ArgumentException;
}

/// <summary>
/// A cast between two fixed-width layouts: every numeric pair, the temporal rescalings, and
/// DATE ↔ TIMESTAMP. The conversion goes through <see cref="decimal"/> when either side is DECIMAL and
/// through <see cref="double"/> when either side is approximate, which keeps the matrix to one method.
/// </summary>
internal sealed class FixedCastExpr : CastExprBase
{
    public FixedCastExpr(ChalkType type, IVectorExpr operand, CastFailure onFailure)
        : base(type, operand, onFailure)
    {
    }

    public override Vector Evaluate(EvalContext context)
    {
        var operand = Operand.Evaluate(context);
        var length = context.Length;
        var bits = BeginInheritedValidity(length, operand, context.SelectionMask);
        var result = Scratch.RawValues(length);
        for (var i = 0; i < length; i++)
        {
            var lane = result.Slice(i * Width, Width);
            if (!BitUtility.GetBit(bits, i))
            {
                lane.Clear();
                continue;
            }

            try
            {
                Convert(operand, i, lane);
            }
            catch (Exception exception) when (Recover(exception))
            {
                BitUtility.ClearBit(bits, i);
                lane.Clear();
            }
        }

        var nulls = Validity.CountNulls(bits, length);
        if (nulls == 0)
        {
            Scratch.NoValidity();
        }

        return Scratch.Finish(length, nulls);
    }

    private void Convert(in Vector operand, int row, Span<byte> lane)
    {
        if (Source.Kind == TypeKind.Decimal && ColumnKinds.IsFloatingPoint(Type.Kind))
        {
            // F137: the nearest double or float of the exact value, wider than System.Decimal or not.
            var unscaled = Decimals.ReadUnscaled(RawLanes.From(operand, 16)[row]);
            if (Kind == ColumnKind.Double)
            {
                System.Runtime.InteropServices.MemoryMarshal.Write(lane, Decimals.ToDouble(unscaled, Source.Scale));
            }
            else
            {
                System.Runtime.InteropServices.MemoryMarshal.Write(lane, Decimals.ToSingle(unscaled, Source.Scale));
            }

            return;
        }

        if (ColumnKinds.IsFloatingPoint(Source.Kind) && Type.Kind == TypeKind.Decimal)
        {
            // F135: the double's exact value, rounded half away from zero to the declared scale.
            Decimals.WriteUnscaled(
                lane,
                Decimals.FromDouble(NumberLanes.ReadDouble(operand, row, Source), Type.Precision, Type.Scale));
            return;
        }

        if (Source.Kind == TypeKind.Decimal || Type.Kind == TypeKind.Decimal)
        {
            var value = Source.Kind == TypeKind.Decimal
                ? Decimals.Read(RawLanes.From(operand, 16)[row], Source.Scale)
                : NumberLanes.ReadDecimal(operand, row, Source);
            NumberLanes.WriteDecimal(Kind, Type, value, lane);
            return;
        }

        if (ColumnKinds.IsFloatingPoint(Source.Kind) || ColumnKinds.IsFloatingPoint(Type.Kind))
        {
            NumberLanes.WriteDouble(Kind, Type, NumberLanes.ReadDouble(operand, row, Source), lane);
            return;
        }

        NumberLanes.WriteInteger(Kind, Type, ReadInteger(operand, row), lane);
    }

    /// <summary>
    /// Reads an exact lane as an int64. DATE ↔ TIMESTAMP and the timestamp rescalings happen here:
    /// they are integer conversions on the storage, exactly as <c>02-ir.md</c> §6 documents.
    /// </summary>
    private long ReadInteger(in Vector operand, int row)
    {
        var raw = NumberLanes.ReadInteger(operand, row, Source);
        if (!IrTypes.IsTemporal(Source.Kind) && !IrTypes.IsTemporal(Type.Kind))
        {
            return raw;
        }

        return TemporalConversions.Convert(raw, Source, Type);
    }
}

/// <summary>A cast whose target is STRING: the numeric and boolean spellings of <c>02-ir.md</c> §6.</summary>
internal sealed class CastToStringExpr : CastExprBase
{
    private byte[] _buffer = new byte[64];

    public CastToStringExpr(ChalkType type, IVectorExpr operand, CastFailure onFailure)
        : base(type, operand, onFailure)
    {
    }

    public override Vector Evaluate(EvalContext context)
    {
        var operand = Operand.Evaluate(context);
        var length = context.Length;
        Scratch.BeginVarLen(length, nullable: true);
        for (var i = 0; i < length; i++)
        {
            if (!IsValidAt(operand, i))
            {
                Scratch.AppendNull();
                continue;
            }

            var written = Format(operand, i);
            Scratch.AppendValue(_buffer.AsSpan(0, written));
        }

        return Scratch.FinishVarLen();
    }

    private int Format(in Vector operand, int row)
    {
        while (true)
        {
            var span = _buffer.AsSpan();
            int written;
            var formatted = Source.Kind switch
            {
                TypeKind.Bool => TryLiteral(span, Lanes<byte>.From(operand)[row] != 0, out written),
                TypeKind.Fp32 => Lanes<float>.From(operand)[row]
                    .TryFormat(span, out written, default, CultureInfo.InvariantCulture),
                TypeKind.Fp64 => Lanes<double>.From(operand)[row]
                    .TryFormat(span, out written, default, CultureInfo.InvariantCulture),
                TypeKind.Decimal => Decimals.Read(RawLanes.From(operand, 16)[row], Source.Scale)
                    .TryFormat(span, out written, default, CultureInfo.InvariantCulture),
                _ => NumberLanes.ReadInteger(operand, row, Source)
                    .TryFormat(span, out written, default, CultureInfo.InvariantCulture),
            };

            if (formatted)
            {
                return written;
            }

            _buffer = new byte[_buffer.Length * 2];
        }
    }

    private static bool TryLiteral(Span<byte> span, bool value, out int written)
    {
        var text = value ? "true"u8 : "false"u8;
        written = text.Length;
        if (span.Length < written)
        {
            return false;
        }

        text.CopyTo(span);
        return true;
    }
}

/// <summary>
/// A cast whose source is STRING: SQL literal syntax with leading and trailing spaces allowed, and
/// ISO 8601 / SQL forms for the temporal targets (<c>02-ir.md</c> §6).
/// </summary>
internal sealed class CastFromStringExpr : CastExprBase
{
    private char[] _chars = new char[64];

    public CastFromStringExpr(ChalkType type, IVectorExpr operand, CastFailure onFailure)
        : base(type, operand, onFailure)
    {
    }

    public override Vector Evaluate(EvalContext context)
    {
        var operand = Operand.Evaluate(context);
        var length = context.Length;
        var bits = BeginInheritedValidity(length, operand, context.SelectionMask);
        var result = Scratch.RawValues(length);
        var lanes = VarOperand.From(operand);
        for (var i = 0; i < length; i++)
        {
            var lane = result.Slice(i * Width, Width);
            if (!BitUtility.GetBit(bits, i))
            {
                lane.Clear();
                continue;
            }

            try
            {
                Parse(Decode(lanes[i]), lane);
            }
            catch (Exception exception) when (Recover(exception))
            {
                BitUtility.ClearBit(bits, i);
                lane.Clear();
            }
        }

        var nulls = Validity.CountNulls(bits, length);
        if (nulls == 0)
        {
            Scratch.NoValidity();
        }

        return Scratch.Finish(length, nulls);
    }

    private ReadOnlySpan<char> Decode(ReadOnlySpan<byte> utf8)
    {
        var count = Encoding.UTF8.GetCharCount(utf8);
        if (_chars.Length < count)
        {
            _chars = new char[count];
        }

        return _chars.AsSpan(0, Encoding.UTF8.GetChars(utf8, _chars));
    }

    private void Parse(ReadOnlySpan<char> text, Span<byte> lane)
    {
        var trimmed = text.Trim();
        switch (Type.Kind)
        {
            case TypeKind.Bool:
                lane[0] = trimmed.Equals("true", StringComparison.OrdinalIgnoreCase) ? (byte)1
                    : trimmed.Equals("false", StringComparison.OrdinalIgnoreCase) ? (byte)0
                    : throw new FormatException($"'{new string(trimmed)}' is not a BOOL literal.");
                return;

            case TypeKind.Fp32:
            case TypeKind.Fp64:
                NumberLanes.WriteDouble(
                    Kind,
                    Type,
                    double.Parse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture),
                    lane);
                return;

            case TypeKind.Decimal:
                NumberLanes.WriteDecimal(
                    Kind,
                    Type,
                    decimal.Parse(trimmed, NumberStyles.Number, CultureInfo.InvariantCulture),
                    lane);
                return;

            case TypeKind.Date:
                NumberLanes.WriteInteger(
                    Kind, Type, DateOnly.Parse(trimmed, CultureInfo.InvariantCulture).DayNumber - 719_162, lane);
                return;

            case TypeKind.Time:
                NumberLanes.WriteInteger(
                    Kind,
                    Type,
                    TimeOnly.Parse(trimmed, CultureInfo.InvariantCulture).Ticks / TimeSpan.TicksPerMicrosecond,
                    lane);
                return;

            case TypeKind.Timestamp:
            case TypeKind.TimestampTz:
            {
                var parsed = DateTime.Parse(
                    trimmed, CultureInfo.InvariantCulture, DateTimeStyles.NoCurrentDateDefault);
                var days = CivilTime.FromCivil(parsed.Year, parsed.Month, parsed.Day);
                var secondOfDay = (parsed.Hour * 3600L) + (parsed.Minute * 60L) + parsed.Second;
                var unitsPerSecond = IrTypes.TimestampUnitsPerSecond((uint)Type.Precision);
                var subSecond = parsed.Ticks % TimeSpan.TicksPerSecond * unitsPerSecond / TimeSpan.TicksPerSecond;
                NumberLanes.WriteInteger(
                    Kind,
                    Type,
                    (((days * CivilTime.SecondsPerDay) + secondOfDay) * unitsPerSecond) + subSecond,
                    lane);
                return;
            }

            default:
                NumberLanes.WriteInteger(
                    Kind, Type, long.Parse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture), lane);
                return;
        }
    }
}

/// <summary>
/// A cast that changes nothing but the declared nullability, which lives on the type rather than in
/// the data. Forwarding the operand's array keeps a redundant CAST free.
/// </summary>
internal sealed class IdentityCastExpr : IVectorExpr
{
    private readonly IVectorExpr _operand;

    public IdentityCastExpr(ChalkType type, IVectorExpr operand)
    {
        Type = type;
        _operand = operand;
    }

    public ChalkType Type { get; }

    public Vector Evaluate(EvalContext context) => _operand.Evaluate(context);
}

/// <summary>Reading and writing the numeric lanes a cast moves between.</summary>
internal static class NumberLanes
{
    public static long ReadInteger(in Vector vector, int row, ChalkType type) => ColumnKinds.Of(type) switch
    {
        ColumnKind.Boolean => Lanes<byte>.From(vector)[row],
        ColumnKind.Int8 => Lanes<sbyte>.From(vector)[row],
        ColumnKind.Int16 => Lanes<short>.From(vector)[row],
        ColumnKind.Int32 => Lanes<int>.From(vector)[row],
        _ => Lanes<long>.From(vector)[row],
    };

    public static double ReadDouble(in Vector vector, int row, ChalkType type) => ColumnKinds.Of(type) switch
    {
        ColumnKind.Float => Lanes<float>.From(vector)[row],
        ColumnKind.Double => Lanes<double>.From(vector)[row],
        _ => ReadInteger(vector, row, type),
    };

    public static decimal ReadDecimal(in Vector vector, int row, ChalkType type) => ColumnKinds.Of(type) switch
    {
        ColumnKind.Float => (decimal)Lanes<float>.From(vector)[row],
        ColumnKind.Double => (decimal)Lanes<double>.From(vector)[row],
        _ => ReadInteger(vector, row, type),
    };

    public static void WriteInteger(ColumnKind kind, ChalkType type, long value, Span<byte> lane)
    {
        switch (kind)
        {
            case ColumnKind.Boolean:
                lane[0] = (byte)(value != 0 ? 1 : 0);
                return;
            case ColumnKind.Int8:
                lane[0] = (byte)checked((sbyte)value);
                return;
            case ColumnKind.Int16:
                System.Runtime.InteropServices.MemoryMarshal.Write(lane, checked((short)value));
                return;
            case ColumnKind.Int32:
                System.Runtime.InteropServices.MemoryMarshal.Write(lane, checked((int)value));
                return;
            case ColumnKind.Int64:
                System.Runtime.InteropServices.MemoryMarshal.Write(lane, value);
                return;
            case ColumnKind.Float:
                System.Runtime.InteropServices.MemoryMarshal.Write(lane, (float)value);
                return;
            case ColumnKind.Double:
                System.Runtime.InteropServices.MemoryMarshal.Write(lane, (double)value);
                return;
            default:
                Decimals.Write(lane, value, type.Precision, type.Scale);
                return;
        }
    }

    /// <summary>FP → integer truncates towards zero, and out of range is an overflow (§6).</summary>
    public static void WriteDouble(ColumnKind kind, ChalkType type, double value, Span<byte> lane)
    {
        switch (kind)
        {
            case ColumnKind.Float:
                System.Runtime.InteropServices.MemoryMarshal.Write(lane, (float)value);
                return;
            case ColumnKind.Double:
                System.Runtime.InteropServices.MemoryMarshal.Write(lane, value);
                return;
            case ColumnKind.Decimal128:
                Decimals.Write(lane, (decimal)value, type.Precision, type.Scale);
                return;
            default:
                WriteInteger(kind, type, checked((long)Math.Truncate(value)), lane);
                return;
        }
    }

    public static void WriteDecimal(ColumnKind kind, ChalkType type, decimal value, Span<byte> lane)
    {
        switch (kind)
        {
            case ColumnKind.Decimal128:
                Decimals.Write(lane, value, type.Precision, type.Scale);
                return;
            case ColumnKind.Float:
                System.Runtime.InteropServices.MemoryMarshal.Write(lane, (float)value);
                return;
            case ColumnKind.Double:
                System.Runtime.InteropServices.MemoryMarshal.Write(lane, (double)value);
                return;
            default:
                WriteInteger(kind, type, checked((long)decimal.Truncate(value)), lane);
                return;
        }
    }
}

/// <summary>
/// DATE ↔ TIMESTAMP and the timestamp rescalings. TIMESTAMP ↔ TIMESTAMP_TZ is the identity on the
/// integer — the wall clock is taken as UTC, documented rather than clever (<c>02-ir.md</c> §6).
/// </summary>
internal static class TemporalConversions
{
    public static long Convert(long raw, ChalkType source, ChalkType target)
    {
        if (source.Kind == TypeKind.Date && target.Kind != TypeKind.Date)
        {
            return raw * CivilTime.SecondsPerDay * IrTypes.TimestampUnitsPerSecond((uint)target.Precision);
        }

        if (source.Kind != TypeKind.Date && target.Kind == TypeKind.Date)
        {
            var seconds = CivilTime.FloorDiv(raw, IrTypes.TimestampUnitsPerSecond((uint)source.Precision));
            return CivilTime.FloorDiv(seconds, CivilTime.SecondsPerDay);
        }

        if (source.Kind == TypeKind.Date)
        {
            return raw;
        }

        var from = IrTypes.TimestampUnitsPerSecond((uint)source.Precision);
        var to = IrTypes.TimestampUnitsPerSecond((uint)target.Precision);
        if (from == to)
        {
            return raw;
        }

        // Rescaling truncates, which for a negative instant means towards the past, not towards zero.
        return to > from ? raw * (to / from) : CivilTime.FloorDiv(raw, from / to);
    }
}

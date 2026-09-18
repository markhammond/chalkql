using Apache.Arrow;
using Chalk.Catalog;
using Chalk.Execution.Numeric;
using Chalk.Execution.Vectors;
using Chalk.Ir;
using Chalk.Sources;

namespace Chalk.Execution.Expressions;

/// <summary>The temporal units <c>EXTRACT</c> and <c>FLOOR … TO</c> accept in M1 (<c>02-ir.md</c> §6).</summary>
internal enum TemporalUnit
{
    Year,
    Quarter,
    Month,
    Week,
    Day,
    DayOfWeek,
    DayOfYear,
    Hour,
    Minute,
    Second,
    Millisecond,
    Microsecond,
    Epoch,
}

/// <summary>Reads the <c>EnumArg</c> spelling of a unit.</summary>
internal static class TemporalUnits
{
    public static TemporalUnit Parse(string value, string function) => value.ToUpperInvariant() switch
    {
        "YEAR" => TemporalUnit.Year,
        "QUARTER" => TemporalUnit.Quarter,
        "MONTH" => TemporalUnit.Month,
        "WEEK" => TemporalUnit.Week,
        "DAY" => TemporalUnit.Day,
        "DOW" or "DAYOFWEEK" => TemporalUnit.DayOfWeek,
        "DOY" or "DAYOFYEAR" => TemporalUnit.DayOfYear,
        "HOUR" => TemporalUnit.Hour,
        "MINUTE" => TemporalUnit.Minute,
        "SECOND" => TemporalUnit.Second,
        "MILLISECOND" => TemporalUnit.Millisecond,
        "MICROSECOND" => TemporalUnit.Microsecond,
        "EPOCH" => TemporalUnit.Epoch,
        _ => throw new UnsupportedFeatureException(
            $"{function} unit {value}",
            "M1 supports YEAR MONTH DAY HOUR MINUTE SECOND DOW DOY EPOCH (docs/design/02-ir.md §6)."),
    };
}

/// <summary>
/// A temporal value split into the fields a kernel needs, without constructing a
/// <see cref="DateTime"/> per lane (§6.4).
/// </summary>
internal readonly struct CivilInstant
{
    public CivilInstant(long days, long secondOfDay, long subSecondUnits, long unitsPerSecond)
    {
        Days = days;
        SecondOfDay = secondOfDay;
        SubSecondUnits = subSecondUnits;
        UnitsPerSecond = unitsPerSecond;
    }

    public long Days { get; }

    public long SecondOfDay { get; }

    public long SubSecondUnits { get; }

    public long UnitsPerSecond { get; }

    /// <summary>Seconds since the epoch, floored — what EXTRACT(EPOCH) reports.</summary>
    public long EpochSeconds => (Days * CivilTime.SecondsPerDay) + SecondOfDay;
}

/// <summary>Splits DATE and TIMESTAMP storage into civil fields.</summary>
internal static class TemporalLanes
{
    /// <summary>How many storage units make one second for this type; 1 for DATE, which counts days.</summary>
    public static long UnitsPerSecond(ChalkType type) => type.Kind == TypeKind.Date
        ? 1
        : IrTypes.TimestampUnitsPerSecond((uint)type.Precision);

    public static CivilInstant Split(long value, ChalkType type)
    {
        if (type.Kind == TypeKind.Date)
        {
            return new CivilInstant(value, 0, 0, 1);
        }

        var unitsPerSecond = IrTypes.TimestampUnitsPerSecond((uint)type.Precision);
        var seconds = CivilTime.FloorDiv(value, unitsPerSecond);
        var subSecond = value - (seconds * unitsPerSecond);
        var days = CivilTime.FloorDiv(seconds, CivilTime.SecondsPerDay);
        return new CivilInstant(days, seconds - (days * CivilTime.SecondsPerDay), subSecond, unitsPerSecond);
    }

    /// <summary>The inverse of <see cref="Split"/>, for FLOOR … TO.</summary>
    public static long Combine(long days, long secondOfDay, long subSecondUnits, ChalkType type) =>
        type.Kind == TypeKind.Date
            ? days
            : (((days * CivilTime.SecondsPerDay) + secondOfDay) * UnitsPerSecond(type)) + subSecondUnits;
}

/// <summary>
/// <c>EXTRACT(unit FROM temporal)</c> → I64. TIMESTAMP_TZ fields are read in UTC (A5), and DOW counts
/// Sunday as 0 (<c>02-ir.md</c> §6).
/// </summary>
internal sealed class ExtractExpr : VectorExprBase
{
    private readonly IVectorExpr _operand;
    private readonly TemporalUnit _unit;
    private readonly ChalkType _sourceType;
    private readonly bool _wideSource;

    public ExtractExpr(ChalkType type, TemporalUnit unit, IVectorExpr operand)
        : base(type)
    {
        _unit = unit;
        _operand = operand;
        _sourceType = operand.Type;
        _wideSource = ColumnKinds.Of(_sourceType) == ColumnKind.Int64;
    }

    public override Vector Evaluate(EvalContext context)
    {
        var operand = _operand.Evaluate(context);
        var length = context.Length;
        var nulls = InheritValidity(length, operand, context.SelectionMask);
        var valid = Scratch.CurrentValidity(length);
        var result = Scratch.Values<long>(length);
        var wide = _wideSource ? Lanes<long>.From(operand) : default;
        var narrow = _wideSource ? default : Lanes<int>.From(operand);

        for (var i = 0; i < length; i++)
        {
            if (!valid.IsEmpty && !BitUtility.GetBit(valid, i))
            {
                result[i] = 0;
                continue;
            }

            var raw = _wideSource ? wide[i] : narrow[i];
            result[i] = Field(TemporalLanes.Split(raw, _sourceType));
        }

        return Scratch.Finish(length, nulls);
    }

    private long Field(CivilInstant instant)
    {
        switch (_unit)
        {
            case TemporalUnit.Hour:
                return instant.SecondOfDay / 3600;
            case TemporalUnit.Minute:
                return instant.SecondOfDay / 60 % 60;
            case TemporalUnit.Second:
                return instant.SecondOfDay % 60;
            case TemporalUnit.Millisecond:
                return (instant.SecondOfDay % 60 * 1000)
                    + (instant.SubSecondUnits * 1000 / instant.UnitsPerSecond);
            case TemporalUnit.Microsecond:
                return (instant.SecondOfDay % 60 * 1_000_000)
                    + (instant.SubSecondUnits * 1_000_000 / instant.UnitsPerSecond);
            case TemporalUnit.Epoch:
                return instant.EpochSeconds;
            case TemporalUnit.DayOfWeek:
                return CivilTime.DayOfWeek((int)instant.Days);
            case TemporalUnit.DayOfYear:
                return CivilTime.DayOfYear((int)instant.Days);
            default:
                break;
        }

        CivilTime.ToCivil((int)instant.Days, out var year, out var month, out var day);
        return _unit switch
        {
            TemporalUnit.Year => year,
            TemporalUnit.Quarter => ((month - 1) / 3) + 1,
            TemporalUnit.Month => month,
            TemporalUnit.Week => (CivilTime.DayOfYear((int)instant.Days) + 6) / 7,
            _ => day,
        };
    }
}

/// <summary>
/// <c>TIME_BUCKET(INTERVAL size, value [, origin])</c> — the start of the fixed-width bucket that
/// contains <c>value</c>, PostgreSQL's <c>date_bin</c> (D52, <c>13-window-functions.md</c> §2).
///
/// <para>
/// All the arithmetic is integer, in the value's own storage units: the interval arrives in
/// microseconds and is converted once per row into days for a DATE or into the timestamp's precision
/// units, and the division floors, so a value before the origin lands on the boundary below it rather
/// than being truncated towards the origin.
/// </para>
/// </summary>
internal sealed class TimeBucketExpr : VectorExprBase
{
    private readonly IVectorExpr _size;
    private readonly IVectorExpr _value;
    private readonly IVectorExpr? _origin;
    private readonly bool _wide;

    public TimeBucketExpr(ChalkType type, IVectorExpr size, IVectorExpr value, IVectorExpr? origin)
        : base(type)
    {
        _size = size;
        _value = value;
        _origin = origin;
        _wide = ColumnKinds.Of(type) == ColumnKind.Int64;
    }

    public override Vector Evaluate(EvalContext context)
    {
        var size = _size.Evaluate(context);
        var value = _value.Evaluate(context);
        var origin = _origin?.Evaluate(context) ?? default;
        var length = context.Length;

        var operands = _origin is null
            ? new[] { size, value }
            : [size, value, origin];
        var nulls = IntersectValidity(length, operands, context.SelectionMask);
        var valid = Scratch.CurrentValidity(length);

        var wideResult = _wide ? Scratch.Values<long>(length) : default;
        var narrowResult = _wide ? default : Scratch.Values<int>(length);
        var sizes = Lanes<long>.From(size);
        var values = _wide ? Lanes<long>.From(value) : default;
        var narrowValues = _wide ? default : Lanes<int>.From(value);
        var origins = _origin is null
            ? default
            : _wide ? Lanes<long>.From(origin) : default;
        var narrowOrigins = _origin is null || _wide ? default : Lanes<int>.From(origin);

        for (var i = 0; i < length; i++)
        {
            if (!valid.IsEmpty && !BitUtility.GetBit(valid, i))
            {
                if (_wide)
                {
                    wideResult[i] = 0;
                }
                else
                {
                    narrowResult[i] = 0;
                }

                continue;
            }

            var width = BucketWidth(sizes[i]);
            var current = _wide ? values[i] : narrowValues[i];
            long start = _origin is null ? 0 : _wide ? origins[i] : narrowOrigins[i];
            var bucket = start + (CivilTime.FloorDiv(current - start, width) * width);
            if (_wide)
            {
                wideResult[i] = bucket;
            }
            else
            {
                narrowResult[i] = (int)bucket;
            }
        }

        return Scratch.Finish(length, nulls);
    }

    /// <summary>The bucket width in the value's units; microseconds in, storage units out.</summary>
    private long BucketWidth(long microseconds)
    {
        if (microseconds <= 0)
        {
            throw new InvalidOperationException(
                $"TIME_BUCKET needs a positive interval; it was given {microseconds} microseconds.");
        }

        var width = Type.Kind == TypeKind.Date
            ? microseconds / 86_400_000_000L
            : Rescale(microseconds, IrTypes.TimestampUnitsPerSecond((uint)Type.Precision));
        return width > 0
            ? width
            : throw new InvalidOperationException(
                $"TIME_BUCKET's interval of {microseconds} microseconds is shorter than one unit of {Type}.");
    }

    private static long Rescale(long microseconds, long unitsPerSecond) => unitsPerSecond >= 1_000_000L
        ? microseconds * (unitsPerSecond / 1_000_000L)
        : microseconds / (1_000_000L / unitsPerSecond);
}

/// <summary>
/// <c>temporal ± INTERVAL_DAY</c>, the one heterogeneous arithmetic <c>02-ir.md</c> §6 defines: the
/// interval's microseconds are rescaled into the temporal's own units and added. This is what the
/// <c>TUMBLE</c> rewrite's <c>window_end = window_start + size</c> needs (D52).
/// </summary>
internal sealed class TemporalIntervalExpr : VectorExprBase
{
    private readonly IVectorExpr _temporal;
    private readonly IVectorExpr _interval;
    private readonly bool _subtract;
    private readonly bool _wide;

    public TemporalIntervalExpr(
        ChalkType type, IVectorExpr temporal, IVectorExpr interval, bool subtract)
        : base(type)
    {
        _temporal = temporal;
        _interval = interval;
        _subtract = subtract;
        _wide = ColumnKinds.Of(type) == ColumnKind.Int64;
    }

    public override Vector Evaluate(EvalContext context)
    {
        var temporal = _temporal.Evaluate(context);
        var interval = _interval.Evaluate(context);
        var length = context.Length;
        var nulls = IntersectValidity(length, temporal, interval, context.SelectionMask);
        var valid = Scratch.CurrentValidity(length);

        var wideResult = _wide ? Scratch.Values<long>(length) : default;
        var narrowResult = _wide ? default : Scratch.Values<int>(length);
        var values = _wide ? Lanes<long>.From(temporal) : default;
        var narrowValues = _wide ? default : Lanes<int>.From(temporal);
        var intervals = Lanes<long>.From(interval);

        for (var i = 0; i < length; i++)
        {
            if (!valid.IsEmpty && !BitUtility.GetBit(valid, i))
            {
                if (_wide)
                {
                    wideResult[i] = 0;
                }
                else
                {
                    narrowResult[i] = 0;
                }

                continue;
            }

            var units = Units(intervals[i]);
            var current = _wide ? values[i] : narrowValues[i];
            var moved = _subtract ? checked(current - units) : checked(current + units);
            if (_wide)
            {
                wideResult[i] = moved;
            }
            else
            {
                narrowResult[i] = checked((int)moved);
            }
        }

        return Scratch.Finish(length, nulls);
    }

    private long Units(long microseconds)
    {
        if (Type.Kind == TypeKind.Date)
        {
            return CivilTime.FloorDiv(microseconds, 86_400_000_000L);
        }

        var perSecond = IrTypes.TimestampUnitsPerSecond((uint)Type.Precision);
        return perSecond >= 1_000_000L
            ? microseconds * ((long)perSecond / 1_000_000L)
            : microseconds / (1_000_000L / (long)perSecond);
    }
}

/// <summary>
/// <c>FLOOR(temporal TO unit)</c>: truncates towards the past, which for a pre-1970 timestamp means
/// towards more negative storage. The result keeps the operand's type.
/// </summary>
internal sealed class FloorTemporalExpr : VectorExprBase
{
    private readonly IVectorExpr _operand;
    private readonly TemporalUnit _unit;
    private readonly bool _wide;

    public FloorTemporalExpr(ChalkType type, TemporalUnit unit, IVectorExpr operand)
        : base(type)
    {
        _unit = unit;
        _operand = operand;
        _wide = ColumnKinds.Of(type) == ColumnKind.Int64;
    }

    public override Vector Evaluate(EvalContext context)
    {
        var operand = _operand.Evaluate(context);
        var length = context.Length;
        var nulls = InheritValidity(length, operand, context.SelectionMask);
        var valid = Scratch.CurrentValidity(length);
        var wideResult = _wide ? Scratch.Values<long>(length) : default;
        var narrowResult = _wide ? default : Scratch.Values<int>(length);
        var wide = _wide ? Lanes<long>.From(operand) : default;
        var narrow = _wide ? default : Lanes<int>.From(operand);

        for (var i = 0; i < length; i++)
        {
            if (!valid.IsEmpty && !BitUtility.GetBit(valid, i))
            {
                if (_wide)
                {
                    wideResult[i] = 0;
                }
                else
                {
                    narrowResult[i] = 0;
                }

                continue;
            }

            var truncated = Truncate(_wide ? wide[i] : narrow[i]);
            if (_wide)
            {
                wideResult[i] = truncated;
            }
            else
            {
                narrowResult[i] = (int)truncated;
            }
        }

        return Scratch.Finish(length, nulls);
    }

    private long Truncate(long raw)
    {
        var instant = TemporalLanes.Split(raw, Type);
        var days = instant.Days;
        var secondOfDay = instant.SecondOfDay;
        var subSecond = instant.SubSecondUnits;

        switch (_unit)
        {
            case TemporalUnit.Second:
                subSecond = 0;
                break;
            case TemporalUnit.Minute:
                subSecond = 0;
                secondOfDay -= secondOfDay % 60;
                break;
            case TemporalUnit.Hour:
                subSecond = 0;
                secondOfDay -= secondOfDay % 3600;
                break;
            case TemporalUnit.Day:
                subSecond = 0;
                secondOfDay = 0;
                break;
            default:
            {
                subSecond = 0;
                secondOfDay = 0;
                CivilTime.ToCivil((int)days, out var year, out var month, out _);
                var startMonth = _unit switch
                {
                    TemporalUnit.Year => 1,
                    TemporalUnit.Quarter => (((month - 1) / 3) * 3) + 1,
                    _ => month,
                };
                days = CivilTime.FromCivil(year, startMonth, 1);
                break;
            }
        }

        return TemporalLanes.Combine(days, secondOfDay, subSecond, Type);
    }
}

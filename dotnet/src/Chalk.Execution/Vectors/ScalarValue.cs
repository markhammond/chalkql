using System.Globalization;
using System.Runtime.CompilerServices;
using Chalk.Catalog;
using Chalk.Ir;
using Chalk.Sources;

namespace Chalk.Execution.Vectors;

/// <summary>
/// A constant that broadcasts over a whole batch: a literal, a bound parameter, or an IN-list option.
/// Literals and parameters never materialise into an array (§6.4), so a comparison against a constant
/// reads one value per batch instead of one per row.
/// </summary>
/// <remarks>
/// The payload is a small union rather than an <c>object</c> so that reading it in a kernel costs no
/// unboxing. Instances are built at plan compilation or at parameter binding; nothing allocates one
/// per batch.
/// </remarks>
internal sealed class ScalarValue
{
    /// <summary>The value's logical type, which fixes which member below is meaningful.</summary>
    public required ChalkType Type { get; init; }

    public bool IsNull { get; init; }

    /// <summary>BOOL (0/1), I8–I64, DATE, TIME, TIMESTAMP, TIMESTAMP_TZ, INTERVAL_DAY, INTERVAL_YEAR.</summary>
    public long Integer { get; init; }

    public double Double { get; init; }

    public float Single { get; init; }

    public string? Text { get; init; }

    /// <summary>BINARY payload, the 16 UUID bytes, or the 16 unscaled DECIMAL bytes.</summary>
    public byte[]? Bytes { get; init; }

    /// <summary>A LIST constant's elements, in order (D58). Empty for every other kind.</summary>
    public IReadOnlyList<ScalarValue> Elements { get; init; } = [];

    /// <summary>A typed NULL.</summary>
    public static ScalarValue Null(ChalkType type) => new() { Type = type, IsNull = true };

    /// <summary>
    /// One lane of a column, as the constant a remote query would bind (M5). This is the boundary a
    /// lookup join's keys cross: they are read out of a driving batch here and handed to a source
    /// through <see cref="ToHost"/>, which is the same path a dynamic parameter takes.
    /// </summary>
    public static ScalarValue FromLane(in ColumnView column, int row, ChalkType type)
    {
        if (!column.IsValid(row))
        {
            return Null(type);
        }

        return type.Kind switch
        {
            TypeKind.Bool => new ScalarValue { Type = type, Integer = column.BoolAt(row) ? 1 : 0 },
            TypeKind.I8 => new ScalarValue { Type = type, Integer = column.Lanes<sbyte>()[row] },
            TypeKind.I16 => new ScalarValue { Type = type, Integer = column.Lanes<short>()[row] },
            TypeKind.I32 or TypeKind.Date or TypeKind.IntervalYear =>
                new ScalarValue { Type = type, Integer = column.Lanes<int>()[row] },
            TypeKind.I64 or TypeKind.Time or TypeKind.Timestamp or TypeKind.TimestampTz
                or TypeKind.IntervalDay =>
                new ScalarValue { Type = type, Integer = column.Lanes<long>()[row] },
            TypeKind.Fp32 => new ScalarValue { Type = type, Single = column.Lanes<float>()[row] },
            TypeKind.Fp64 => new ScalarValue { Type = type, Double = column.Lanes<double>()[row] },
            TypeKind.String => new ScalarValue
            {
                Type = type,
                Text = System.Text.Encoding.UTF8.GetString(column.VarValue(row)),
            },
            TypeKind.Binary or TypeKind.Uuid or TypeKind.Decimal =>
                new ScalarValue { Type = type, Bytes = BytesOf(column, row, type) },
            _ => throw new UnsupportedFeatureException(
                $"a lookup key of type {IrTypes.Describe(type.ToProto())}",
                "A lookup join binds its keys into a source's query, and this type has no value the "
                + "boundary can carry. Plan the join as LOCAL, or narrow the key column."),
        };
    }

    private static byte[] BytesOf(in ColumnView column, int row, ChalkType type) =>
        type.Kind == TypeKind.Binary
            ? column.VarValue(row).ToArray()
            : column.RawLanes(type.Kind == TypeKind.Uuid ? 16 : 16).Slice(row * 16, 16).ToArray();

    /// <summary>Reads the payload as the layout's CLR storage type. The branches fold away per instantiation.</summary>
    public T Read<T>()
        where T : unmanaged
    {
        if (typeof(T) == typeof(byte))
        {
            var v = (byte)(Integer != 0 ? 1 : 0);
            return Unsafe.As<byte, T>(ref v);
        }

        if (typeof(T) == typeof(sbyte))
        {
            var v = (sbyte)Integer;
            return Unsafe.As<sbyte, T>(ref v);
        }

        if (typeof(T) == typeof(short))
        {
            var v = (short)Integer;
            return Unsafe.As<short, T>(ref v);
        }

        if (typeof(T) == typeof(int))
        {
            var v = (int)Integer;
            return Unsafe.As<int, T>(ref v);
        }

        if (typeof(T) == typeof(long))
        {
            var v = Integer;
            return Unsafe.As<long, T>(ref v);
        }

        if (typeof(T) == typeof(float))
        {
            var v = Single;
            return Unsafe.As<float, T>(ref v);
        }

        if (typeof(T) == typeof(double))
        {
            var v = Double;
            return Unsafe.As<double, T>(ref v);
        }

        throw new InvalidOperationException($"No scalar payload of CLR type {typeof(T).Name}.");
    }

    /// <summary>The raw bytes of a BINARY, UUID or DECIMAL constant.</summary>
    public ReadOnlySpan<byte> ReadBytes() => Bytes ?? [];

    /// <summary>
    /// The value as the CLR object a source's own rows hold, for the one place a constant has to
    /// leave the engine: an index lookup's key bounds (M2, §5). The shapes are the <em>storage</em>
    /// ones a POCO column extracts — days for DATE, <c>Type.Precision</c> units for TIMESTAMP — so a
    /// bound and a key compare without either side converting.
    /// </summary>
    public object? ToClr() => IsNull ? null : Type.Kind switch
    {
        TypeKind.Bool => (byte)(Integer != 0 ? 1 : 0),
        TypeKind.I8 => (sbyte)Integer,
        TypeKind.I16 => (short)Integer,
        TypeKind.I32 => (int)Integer,
        TypeKind.I64 => Integer,
        TypeKind.Fp32 => Single,
        TypeKind.Fp64 => Double,
        TypeKind.String => Text ?? string.Empty,
        TypeKind.Binary => new ReadOnlyMemory<byte>(Bytes ?? []),
        TypeKind.Uuid => new Guid(ReadBytes(), bigEndian: true),
        TypeKind.Decimal => Numeric.Decimals.Read(ReadBytes(), Type.Scale),
        TypeKind.Date => (int)Integer,
        TypeKind.Time or TypeKind.Timestamp or TypeKind.TimestampTz
            or TypeKind.IntervalDay => Integer,
        TypeKind.IntervalYear => (int)Integer,
        TypeKind.List => Elements.Select(e => e.ToClr()).ToArray(),
        _ => throw new UnsupportedFeatureException(
            $"index key bound of type {Type}",
            "There is no CLR shape for it; the index would compare the wrong values."),
    };

    /// <summary>
    /// The value as the CLR object a <em>host</em> holds (<c>04-client.md</c> §7.2): a
    /// <c>DateOnly</c> for a DATE, a <c>DateTime</c> for a TIMESTAMP, a <c>TimeOnly</c> for a TIME.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="ToClr"/>, and the distinction matters at exactly one boundary. An
    /// index key bound is compared against a POCO column's <em>storage</em> — days, microseconds —
    /// so <see cref="ToClr"/> gives it those. A parameter bound into a remote query is handed to a
    /// database driver, which knows nothing about Chalk's storage and wants the ordinary .NET shape
    /// its own type mapping is written against (M4 §3).
    /// </remarks>
    public object? ToHost() => IsNull ? null : Type.Kind switch
    {
        TypeKind.Date => DateOnly.FromDayNumber((int)Integer + 719_162),
        TypeKind.Time => new TimeOnly(Integer * TimeSpan.TicksPerMicrosecond),
        TypeKind.Timestamp => Epoch(Integer, Type),
        TypeKind.TimestampTz => new DateTimeOffset(Epoch(Integer, Type), TimeSpan.Zero),
        TypeKind.IntervalDay => new TimeSpan(Integer * TimeSpan.TicksPerMicrosecond),
        TypeKind.List => Elements.Select(e => e.ToHost()).ToArray(),
        _ => ToClr(),
    };

    private static DateTime Epoch(long units, ChalkType type)
    {
        var perSecond = Ir.IrTypes.TimestampUnitsPerSecond((uint)type.Precision);
        var ticks = units * (TimeSpan.TicksPerSecond / (double)perSecond);
        return new DateTime(DateTime.UnixEpoch.Ticks + (long)ticks, DateTimeKind.Unspecified);
    }

    public override string ToString() => IsNull
        ? "NULL"
        : Type.Kind switch
        {
            TypeKind.String => Text ?? string.Empty,
            TypeKind.Fp32 => Single.ToString("R", CultureInfo.InvariantCulture),
            TypeKind.Fp64 => Double.ToString("R", CultureInfo.InvariantCulture),
            TypeKind.Binary or TypeKind.Uuid or TypeKind.Decimal =>
                Convert.ToHexStringLower(Bytes ?? []),
            TypeKind.List => "[" + string.Join(", ", Elements) + "]",
            _ => Integer.ToString(CultureInfo.InvariantCulture),
        };
}

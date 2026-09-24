using Apache.Arrow.Types;
using Chalk.Catalog;
using Chalk.Ir;
using ArrowField = Apache.Arrow.Field;
using ArrowSchema = Apache.Arrow.Schema;

namespace Chalk.Sources;

/// <summary>
/// The physical mapping between Chalk's logical types and Arrow, per <c>docs/design/02-ir.md</c> §3.
/// Sources and the engine share it, so a batch a source produces is exactly what the engine expects.
/// </summary>
public static class ArrowTypeMapping
{
    /// <summary>What Arrow's own list builders call a list's child field.</summary>
    public const string ListElementName = "item";

    /// <summary>
    /// The Arrow type for a Chalk type, with STRING carried in <paramref name="strings"/> (D244).
    /// Used where an <em>output</em> schema is built and nowhere else: the schemas handed to sources
    /// keep declaring <see cref="StringType"/>, which is what the whole source boundary is written
    /// against.
    /// </summary>
    /// <remarks>
    /// The layout reaches every STRING inside the type, a LIST's element and a STRUCT's fields
    /// included, so a declared field and the array under it agree at every level.
    /// </remarks>
    public static IArrowType ToArrow(ChalkType type, StringLayouts strings)
    {
        if (strings is not (StringLayouts.Utf8 or StringLayouts.Utf8View))
        {
            throw new ArgumentOutOfRangeException(
                nameof(strings),
                strings,
                "a schema is built from one resolved layout; Any is resolved before it gets here.");
        }

        return type.Kind switch
        {
            TypeKind.String when strings == StringLayouts.Utf8View => StringViewType.Default,
            TypeKind.List => new ListType(ToArrowField(
                ListElementName,
                type.Element ?? throw new UnsupportedFeatureException(
                    "LIST with no element type",
                    "docs/design/02-ir.md §3 requires Type.element on a LIST."),
                strings)),
            TypeKind.Struct => new StructType(
                [.. type.Fields.Select(f => ToArrowField(f.Name, f.Type, strings))]),
            _ => ToArrow(type),
        };
    }

    /// <summary>The Arrow type for a Chalk type. Nullability is carried on the Arrow <see cref="ArrowField"/>, not here.</summary>
    public static IArrowType ToArrow(ChalkType type) => type.Kind switch
    {
        TypeKind.Bool => BooleanType.Default,
        TypeKind.I8 => Int8Type.Default,
        TypeKind.I16 => Int16Type.Default,
        TypeKind.I32 => Int32Type.Default,
        TypeKind.I64 => Int64Type.Default,
        TypeKind.Fp32 => FloatType.Default,
        TypeKind.Fp64 => DoubleType.Default,
        TypeKind.String => StringType.Default,
        TypeKind.Binary => BinaryType.Default,
        TypeKind.Date => Date32Type.Default,
        TypeKind.Time => new Time64Type(TimeUnit.Microsecond),
        TypeKind.Timestamp => new TimestampType(TimestampUnit(type.Precision), timezone: (string?)null),
        TypeKind.TimestampTz => new TimestampType(TimestampUnit(type.Precision), "UTC"),
        TypeKind.Decimal => new Decimal128Type(type.Precision, type.Scale),
        TypeKind.Uuid => new FixedSizeBinaryType(16),
        TypeKind.IntervalDay => DurationType.Microsecond,
        TypeKind.IntervalYear => IntervalType.YearMonth,
        // 32-bit offsets, and the child field is named "item" as Arrow's own list builders name it
        // (D58). The element's nullability rides on the child field, the list's on the list's own.
        TypeKind.List => new ListType(ToArrowField(
            ListElementName,
            type.Element ?? throw new UnsupportedFeatureException(
                "LIST with no element type",
                "docs/design/02-ir.md §3 requires Type.element on a LIST."))),

        // D291: one child field per struct field, named as declared and carrying the field's own
        // nullability; the struct's own is on the column's field.
        TypeKind.Struct => new StructType([.. type.Fields.Select(f => ToArrowField(f.Name, f.Type))]),
        _ => throw new UnsupportedFeatureException(
            $"type kind {type.Kind}",
            "There is no Arrow mapping for it; the planner should never have produced it."),
    };

    /// <summary>The Arrow field for a named, typed column.</summary>
    public static ArrowField ToArrowField(string name, ChalkType type) =>
        new(name, ToArrow(type), type.Nullable);

    /// <summary>The Arrow field for a named, typed column, with STRING in a chosen layout (D244).</summary>
    public static ArrowField ToArrowField(string name, ChalkType type, StringLayouts strings) =>
        new(name, ToArrow(type, strings), type.Nullable);

    /// <summary>The Arrow schema for an IR row type — field names verbatim, duplicates allowed.</summary>
    public static ArrowSchema ToArrowSchema(RowType rowType)
    {
        ArgumentNullException.ThrowIfNull(rowType);

        return new ArrowSchema(
            rowType.Fields.Select(f => ToArrowField(f.Name, ChalkType.FromProto(f.Type))), 
            metadata: null);
    }

    /// <summary>
    /// The Arrow schema for an IR row type with a layout chosen per column (D244): the output
    /// schema of a prepared query, and the only schema built this way.
    /// </summary>
    public static ArrowSchema ToArrowSchema(RowType rowType, IReadOnlyList<StringLayouts> strings)
    {
        ArgumentNullException.ThrowIfNull(rowType);
        ArgumentNullException.ThrowIfNull(strings);
        if (strings.Count != rowType.Fields.Count)
        {
            throw new ArgumentException(
                $"the row type has {rowType.Fields.Count} fields and {strings.Count} layouts were "
                + "given; there is exactly one layout per column",
                nameof(strings));
        }

        var fields = new ArrowField[rowType.Fields.Count];
        for (var i = 0; i < fields.Length; i++)
        {
            var field = rowType.Fields[i];
            fields[i] = ToArrowField(field.Name, ChalkType.FromProto(field.Type), strings[i]);
        }

        return new ArrowSchema(fields, metadata: null);
    }

    /// <summary>The Arrow schema for a list of catalog columns, in order.</summary>
    public static ArrowSchema ToArrowSchema(IReadOnlyList<ColumnDescriptor> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        return new ArrowSchema(columns.Select(c => ToArrowField(c.Name, c.Type)), metadata: null);
    }

    /// <summary>The Arrow schema for a projection of a table's columns, in output order.</summary>
    public static ArrowSchema ToArrowSchema(TableDescriptor table, IReadOnlyList<int> projection)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(projection);
        return new ArrowSchema(
            projection.Select(i => ToArrowField(table.Columns[i].Name, table.Columns[i].Type)),
            metadata: null);
    }

    /// <summary>
    /// The Chalk type an Arrow type maps back to. Round-trips every kind in §3; anything else is
    /// unsupported rather than silently approximated.
    /// </summary>
    public static ChalkType FromArrow(IArrowType type, bool nullable)
    {
        ArgumentNullException.ThrowIfNull(type);
        return type switch
        {
            BooleanType => ChalkType.Bool(nullable),
            Int8Type => ChalkType.Int8(nullable),
            Int16Type => ChalkType.Int16(nullable),
            Int32Type => ChalkType.Int32(nullable),
            Int64Type => ChalkType.Int64(nullable),
            FloatType => ChalkType.Float32(nullable),
            DoubleType => ChalkType.Float64(nullable),
            StringType => ChalkType.String(nullable),

            // Both Arrow UTF-8 layouts are the one Chalk type (D244). StringViewType does not
            // derive from StringType, so it needs its own arm rather than falling into the one above.
            StringViewType => ChalkType.String(nullable),
            BinaryType => ChalkType.Binary(nullable),
            Date32Type => ChalkType.Date(nullable),
            Time64Type => ChalkType.Time(6, nullable),
            TimestampType timestamp => string.IsNullOrEmpty(timestamp.Timezone)
                ? ChalkType.Timestamp(PrecisionOf(timestamp.Unit), nullable)
                : ChalkType.TimestampTz(PrecisionOf(timestamp.Unit), nullable),
            Decimal128Type dec => ChalkType.Decimal(dec.Precision, dec.Scale, nullable),
            FixedSizeBinaryType fixedSize when fixedSize.ByteWidth == 16 => ChalkType.Uuid(nullable),
            DurationType => ChalkType.IntervalDay(nullable),
            IntervalType { Unit: IntervalUnit.YearMonth } => ChalkType.IntervalYear(nullable),
            ListType list => ChalkType.List(
                FromArrow(list.ValueDataType, list.ValueField.IsNullable), nullable),
            StructType record => ChalkType.Struct(
                record.Fields.Select(f => new ChalkField(f.Name, FromArrow(f.DataType, f.IsNullable))),
                nullable),
            _ => throw new UnsupportedFeatureException(
                $"Arrow type {type.Name}",
                "It has no Chalk logical type; see docs/design/02-ir.md §3 for the supported set."),
        };
    }

    /// <summary>The Chalk row type an Arrow schema maps back to.</summary>
    public static IReadOnlyList<ColumnDescriptor> FromArrow(ArrowSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        return schema.FieldsList
            .Select(f => new ColumnDescriptor { Name = f.Name, Type = FromArrow(f.DataType, f.IsNullable) })
            .ToArray();
    }

    /// <summary>
    /// The Arrow time unit a TIMESTAMP precision selects: 0–3 milliseconds, 4–6 microseconds,
    /// 7–9 nanoseconds (§3). Arrow has no second-resolution row in the mapping table, so precision 0
    /// still counts milliseconds.
    /// </summary>
    public static TimeUnit TimestampUnit(int precision) => precision switch
    {
        <= 3 => TimeUnit.Millisecond,
        <= 6 => TimeUnit.Microsecond,
        _ => TimeUnit.Nanosecond,
    };

    /// <summary>The number of units in one second at this Arrow time unit.</summary>
    public static long UnitsPerSecond(TimeUnit unit) => unit switch
    {
        TimeUnit.Second => 1L,
        TimeUnit.Millisecond => 1_000L,
        TimeUnit.Microsecond => 1_000_000L,
        TimeUnit.Nanosecond => 1_000_000_000L,
        _ => throw new UnsupportedFeatureException($"Arrow time unit {unit}", "Chalk maps ms, us and ns only."),
    };

    /// <summary>Two schemas are the same when names, types, nullability and order all agree.</summary>
    public static bool AreEquivalent(ArrowSchema left, ArrowSchema right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        if (left.FieldsList.Count != right.FieldsList.Count)
        {
            return false;
        }

        for (var i = 0; i < left.FieldsList.Count; i++)
        {
            if (!AreEquivalent(left.FieldsList[i], right.FieldsList[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Two fields are the same when name, nullability and type all agree. The same question as
    /// <see cref="AreEquivalent(ArrowSchema, ArrowSchema)"/> asks of a whole schema, one field at a
    /// time, so a source can check a projection against an output schema without building one.
    /// </summary>
    public static bool AreEquivalent(ArrowField left, ArrowField right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return string.Equals(left.Name, right.Name, StringComparison.Ordinal)
            && left.IsNullable == right.IsNullable
            && AreEquivalent(left.DataType, right.DataType);
    }

    /// <summary>
    /// Whether two Arrow types are the same one. Exactly the question
    /// <c>DescribeArrow(left) == DescribeArrow(right)</c> answers — case for case, including the
    /// parameters each printed form carries and the ones it drops — without the two strings. That
    /// matters because this runs once per column per scan on the hot path (ADR 0020 §1).
    /// </summary>
    public static bool AreEquivalent(IArrowType left, IArrowType right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        return left switch
        {
            TimestampType a => right is TimestampType b
                && a.Unit == b.Unit
                && string.Equals(Zone(a.Timezone), Zone(b.Timezone), StringComparison.Ordinal),
            Time64Type a => right is Time64Type b && a.Unit == b.Unit,
            Decimal128Type a => right is Decimal128Type b && a.Precision == b.Precision && a.Scale == b.Scale,

            // Decimal128Type derives from FixedSizeBinaryType and DescribeArrow prints it first, so
            // a decimal is never the same type as a plain fixed-width binary of the same width.
            FixedSizeBinaryType a => right is FixedSizeBinaryType b and not Decimal128Type
                && a.ByteWidth == b.ByteWidth,
            DurationType a => right is DurationType b && a.Unit == b.Unit,
            IntervalType a => right is IntervalType b && a.Unit == b.Unit,
            _ => right is not (TimestampType or Time64Type or FixedSizeBinaryType
                    or DurationType or IntervalType)
                && string.Equals(left.Name, right.Name, StringComparison.Ordinal),
        };

        static string Zone(string? zone) => string.IsNullOrEmpty(zone) ? "none" : zone;
    }

    /// <summary>A stable printable form of an Arrow type, for schema-mismatch messages.</summary>
    public static string DescribeArrow(IArrowType type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return type switch
        {
            TimestampType t => $"timestamp[{t.Unit}, tz={(string.IsNullOrEmpty(t.Timezone) ? "none" : t.Timezone)}]",
            Time64Type t => $"time64[{t.Unit}]",
            Decimal128Type d => $"decimal128({d.Precision},{d.Scale})",
            FixedSizeBinaryType f => $"fixed_size_binary[{f.ByteWidth}]",
            DurationType d => $"duration[{d.Unit}]",
            IntervalType i => $"interval[{i.Unit}]",
            _ => type.Name,
        };
    }

    /// <summary>A printable schema, for mismatch messages.</summary>
    public static string DescribeArrow(ArrowSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        return "[" + string.Join(
            ", ",
            schema.FieldsList.Select(f => $"{f.Name}:{DescribeArrow(f.DataType)}{(f.IsNullable ? "?" : string.Empty)}"))
            + "]";
    }

    private static int PrecisionOf(TimeUnit unit) => unit switch
    {
        TimeUnit.Second => 0,
        TimeUnit.Millisecond => 3,
        TimeUnit.Microsecond => 6,
        _ => 9,
    };
}

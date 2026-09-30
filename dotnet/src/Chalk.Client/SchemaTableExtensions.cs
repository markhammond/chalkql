using System.Data;
using Chalk.Catalog;
using Chalk.Sources;
using ArrowSchema = Apache.Arrow.Schema;
using TypeKind = Chalk.Ir.TypeKind;

namespace Chalk.Client;

/// <summary>
/// A result's table model as an ADO.NET <see cref="DataTable"/>: the equivalent of
/// <c>DbDataAdapter.FillSchema</c>, for a host that speaks <c>System.Data</c> (D331).
/// </summary>
public static class SchemaTableExtensions
{
    /// <summary>
    /// The extended property a composite column's fields are under: one detached
    /// <see cref="DataColumn"/> per field, in declared order, each with its name, type and nullability.
    /// </summary>
    public const string CompositeFieldsProperty = "chalk.fields";

    /// <summary>
    /// The extended property a column's disclosure is under, where the schema carries one: the word
    /// its <c>chalk.disclosure</c> metadata says — <c>FULL</c>, <c>MASKED</c>, <c>REDACTED</c>,
    /// <c>PER_ROW</c>, <c>AGGREGATE</c> or <c>TESTED</c>.
    /// </summary>
    public const string DisclosureProperty = DisclosureLabels.MetadataKey;

    /// <summary>
    /// An empty <see cref="DataTable"/> with one column per field of <paramref name="schema"/>: a
    /// <see cref="PreparedQuery.OutputSchema"/>, or a refusal's
    /// <see cref="Chalk.Entitlements.EntitlementRefusal.OutputSchema"/>, which are the same form.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each column is typed as Chalk reads the value: <see cref="DateOnly"/> for a date,
    /// <see cref="TimeOnly"/> for a time, <see cref="DateTime"/> for a timestamp and
    /// <see cref="DateTimeOffset"/> for one with a time zone, <see cref="TimeSpan"/> for a day-time
    /// interval and <see cref="int"/> months for a year-month one, <see cref="Guid"/> for a UUID.
    /// <see cref="DataColumn.AllowDBNull"/> is the field's nullability.
    /// </para>
    /// <para>
    /// A <see cref="DataTable"/> has no nested columns, so a composite is one column typed
    /// <see cref="object"/>, with its fields under <see cref="CompositeFieldsProperty"/>; a list is one
    /// column typed <see cref="object"/> too. A <see cref="DataTable"/> refuses two columns of one
    /// name where a result may have them, so a repeated name is numbered as <c>FillSchema</c> numbers
    /// it: <c>id</c>, <c>id1</c>, <c>id2</c>.
    /// </para>
    /// </remarks>
    public static DataTable ToDataTable(this ArrowSchema schema, string tableName = "")
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(tableName);

        var table = new DataTable(tableName);
        foreach (var field in schema.FieldsList)
        {
            var type = ArrowTypeMapping.FromArrow(field.DataType, field.IsNullable);
            var column = new DataColumn(Unique(table, field.Name), ClrType(type))
            {
                AllowDBNull = field.IsNullable,
            };
            if (field.Metadata is { } metadata
                && metadata.TryGetValue(DisclosureLabels.MetadataKey, out var disclosure))
            {
                column.ExtendedProperties[DisclosureProperty] = disclosure;
            }

            if (type.Kind == TypeKind.Composite)
            {
                column.ExtendedProperties[CompositeFieldsProperty] = type.Fields
                    .Select(f => new DataColumn(f.Name, ClrType(f.Type)) { AllowDBNull = f.Type.Nullable })
                    .ToArray();
            }

            table.Columns.Add(column);
        }

        return table;
    }

    /// <summary>The name, numbered past any column of <paramref name="table"/> already called it.</summary>
    private static string Unique(DataTable table, string name)
    {
        if (!table.Columns.Contains(name))
        {
            return name;
        }

        for (var n = 1; ; n++)
        {
            var candidate = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{name}{n}");
            if (!table.Columns.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    private static Type ClrType(ChalkType type) => type.Kind switch
    {
        TypeKind.Bool => typeof(bool),
        TypeKind.I8 => typeof(sbyte),
        TypeKind.I16 => typeof(short),
        TypeKind.I32 => typeof(int),
        TypeKind.I64 => typeof(long),
        TypeKind.Fp32 => typeof(float),
        TypeKind.Fp64 => typeof(double),
        TypeKind.String => typeof(string),
        TypeKind.Binary => typeof(byte[]),
        TypeKind.Date => typeof(DateOnly),
        TypeKind.Time => typeof(TimeOnly),
        TypeKind.Timestamp => typeof(DateTime),
        TypeKind.TimestampTz => typeof(DateTimeOffset),
        TypeKind.Decimal => typeof(decimal),
        TypeKind.Uuid => typeof(Guid),
        TypeKind.IntervalDay => typeof(TimeSpan),
        TypeKind.IntervalYear => typeof(int),
        _ => typeof(object),
    };
}

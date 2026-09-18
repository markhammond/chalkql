using System.Globalization;
using System.Text;
using Apache.Arrow;
using Chalk.Catalog;
using Chalk.Ir;
using ArrowSchema = Apache.Arrow.Schema;

namespace Chalk.Sources.Conformance;

/// <summary>
/// The rows the kit needs inside the source it is checking (D88). Every value here exists to make
/// one probe answer a question that cannot be answered any other way.
/// </summary>
/// <remarks>
/// <para>
/// The kit cannot invent data: to find out how a source compares <c>'a'</c> with <c>'A'</c> it has
/// to have both in a column of that source. So an adapter author either supplies a
/// <see cref="ConformanceOptions.Seed"/> callback that loads these rows — the kit hands them over
/// both as a <see cref="RecordBatch"/> and as SQL <c>INSERT</c> text, so whichever the adapter finds
/// easier — or points the kit at existing tables that match this schema.
/// </para>
/// <para>
/// One table, deliberately. Two would double the seeding an author has to write, and every probe
/// here is about one column's values or one pair of columns.
/// </para>
/// </remarks>
public static class ConformanceDataset
{
    /// <summary>The table the kit reads. Override with <see cref="ConformanceOptions.Table"/>.</summary>
    public const string DefaultTable = "chalk_conformance";

    /// <summary>The columns, in the order a scan must produce them.</summary>
    public static IReadOnlyList<ColumnDescriptor> Columns { get; } =
    [
        // The key: unique, non-null, and what the uniqueness probe (F16(e)) is run against.
        new() { Name = "id", Type = ChalkType.Int64() },

        // Strings that differ only in case, only in accent, and only in trailing space — the three
        // ways a collation can disagree with code-point order.
        new() { Name = "text_value", Type = ChalkType.String(nullable: true) },

        // A second string column so a two-column uniqueness claim can be checked, and so the
        // empty-string-versus-NULL probe has somewhere to look.
        new() { Name = "text_other", Type = ChalkType.String(nullable: true) },

        // Signed, so integer division and modulus of negatives are observable.
        new() { Name = "int_value", Type = ChalkType.Int64(nullable: true) },

        // Enough scale to see whether the source rounds or truncates.
        new() { Name = "decimal_value", Type = ChalkType.Decimal(18, 6, nullable: true) },

        // Sub-millisecond, so a source that stores milliseconds is caught truncating.
        new() { Name = "ts_value", Type = ChalkType.Timestamp(9, nullable: true) },

        new() { Name = "date_value", Type = ChalkType.Date(nullable: true) },
    ];

    /// <summary>The Arrow schema a seeding adapter receives.</summary>
    public static ArrowSchema Schema { get; } = ArrowTypeMapping.ToArrowSchema(Columns);

    /// <summary>
    /// The rows, as CLR values in column order — the shape a host holds
    /// (<c>04-client.md</c> §7.2). Sixteen rows: small enough to insert anywhere, wide enough that
    /// every probe has both a matching and a non-matching row to tell apart.
    /// </summary>
    public static IReadOnlyList<object?[]> Rows { get; } = Build();

    private static IReadOnlyList<object?[]> Build()
    {
        var epoch = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
        return
        [
            //        id  text        other      int    decimal          ts                          date
            Row(1, "alpha", "x", 10L, 1.500000m, epoch.AddTicks(1_234_567), new DateOnly(2026, 1, 1)),
            Row(2, "ALPHA", "y", -10L, -1.500000m, epoch.AddTicks(7_654_321), new DateOnly(2026, 1, 2)),
            Row(3, "Alpha", "z", 7L, 0.000001m, epoch.AddTicks(1), new DateOnly(2026, 1, 3)),
            Row(4, "beta", "x", -7L, 1234567890.123456m, epoch.AddTicks(999_999_9), new DateOnly(2026, 2, 1)),
            Row(5, "BETA", "y", 3L, -0.500000m, epoch.AddSeconds(1), new DateOnly(2026, 3, 1)),
            Row(6, "café", "z", -3L, 0.500000m, epoch.AddSeconds(2), new DateOnly(2026, 4, 1)),
            Row(7, "cafe", "x", 1L, 2.250000m, epoch.AddSeconds(3), new DateOnly(2026, 5, 1)),
            Row(8, "cafz", "y", -1L, -2.250000m, epoch.AddSeconds(4), new DateOnly(2026, 6, 1)),

            // The percent and underscore a LIKE pattern would otherwise treat as wildcards, so the
            // escape probe can tell a source that honours ESCAPE from one that does not.
            Row(9, "100%", "z", 0L, 0m, epoch.AddSeconds(5), new DateOnly(2026, 7, 1)),
            Row(10, "a_b", "x", 100L, 100.000000m, epoch.AddSeconds(6), new DateOnly(2026, 8, 1)),
            Row(11, "axb", "y", -100L, -100.000000m, epoch.AddSeconds(7), new DateOnly(2026, 9, 1)),

            // The empty string and the NULL, next to each other, which is the whole of that probe.
            Row(12, string.Empty, "z", 5L, 5.000000m, epoch.AddSeconds(8), new DateOnly(2026, 10, 1)),
            Row(13, null, null, null, null, null, null),

            // Trailing space: a source that pads or trims CHAR would sort or match these together.
            Row(14, "pad ", "x", 2L, 0.250000m, epoch.AddSeconds(9), new DateOnly(2026, 11, 1)),
            Row(15, "pad", "y", -2L, -0.250000m, epoch.AddSeconds(10), new DateOnly(2026, 12, 1)),

            // A second NULL in the string column alone, so a sort has two NULLs to place and a
            // one-NULL accident cannot look like a rule.
            Row(16, null, "z", 42L, 42.000000m, epoch.AddSeconds(11), new DateOnly(2027, 1, 1)),
        ];
    }

    private static object?[] Row(
        long id,
        string? text,
        string? other,
        long? number,
        decimal? value,
        DateTime? timestamp,
        DateOnly? date) => [id, text, other, number, value, timestamp, date];

    /// <summary>
    /// The rows as SQL <c>INSERT</c> statements in <paramref name="profile"/>'s dialect, for an
    /// adapter that finds text easier to load than Arrow. Values are literals, not parameters, so
    /// the statements can be pasted into a console.
    /// </summary>
    public static IReadOnlyList<string> InsertStatements(
        DialectProfileDescriptor profile, string table = DefaultTable)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var statements = new List<string>(Rows.Count);
        foreach (var row in Rows)
        {
            var sql = new StringBuilder("INSERT INTO ");
            Identifier(sql, table, profile);
            sql.Append(" (");
            for (var c = 0; c < Columns.Count; c++)
            {
                if (c > 0)
                {
                    sql.Append(", ");
                }

                Identifier(sql, Columns[c].Name, profile);
            }

            sql.Append(") VALUES (");
            for (var c = 0; c < Columns.Count; c++)
            {
                if (c > 0)
                {
                    sql.Append(", ");
                }

                sql.Append(Literal(row[c], Columns[c].Type, profile));
            }

            sql.Append(')');
            statements.Add(sql.ToString());
        }

        return statements;
    }

    /// <summary>The <c>CREATE TABLE</c> the dataset needs, in this dialect.</summary>
    public static string CreateTableStatement(
        DialectProfileDescriptor profile, string table = DefaultTable)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var sql = new StringBuilder("CREATE TABLE ");
        Identifier(sql, table, profile);
        sql.Append(" (");
        for (var c = 0; c < Columns.Count; c++)
        {
            if (c > 0)
            {
                sql.Append(", ");
            }

            Identifier(sql, Columns[c].Name, profile);
            sql.Append(' ').Append(SqlType(Columns[c].Type));
            if (!Columns[c].Type.Nullable)
            {
                sql.Append(" NOT NULL");
            }
        }

        return sql.Append(')').ToString();
    }

    /// <summary>The rows as one Arrow batch, for an adapter that would rather write them that way.</summary>
    public static RecordBatch ToBatch()
    {
        var arrays = new IArrowArray[Columns.Count];
        for (var c = 0; c < Columns.Count; c++)
        {
            arrays[c] = BuildColumn(c);
        }

        return new RecordBatch(Schema, arrays, Rows.Count);
    }

    private static IArrowArray BuildColumn(int column) => Columns[column].Type.Kind switch
    {
        TypeKind.I64 => BuildInt64(column),
        TypeKind.String => BuildString(column),
        TypeKind.Decimal => BuildDecimal(column),
        TypeKind.Timestamp => BuildTimestamp(column),
        TypeKind.Date => BuildDate(column),
        _ => throw new NotSupportedException($"the dataset has no builder for {Columns[column].Type}"),
    };

    private static IArrowArray BuildInt64(int column)
    {
        var builder = new Int64Array.Builder();
        foreach (var row in Rows)
        {
            if (row[column] is long value)
            {
                builder.Append(value);
            }
            else
            {
                builder.AppendNull();
            }
        }

        return builder.Build();
    }

    private static IArrowArray BuildString(int column)
    {
        var builder = new StringArray.Builder();
        foreach (var row in Rows)
        {
            if (row[column] is string value)
            {
                builder.Append(value);
            }
            else
            {
                builder.AppendNull();
            }
        }

        return builder.Build();
    }

    private static IArrowArray BuildDecimal(int column)
    {
        var type = (Apache.Arrow.Types.Decimal128Type)ArrowTypeMapping.ToArrow(Columns[column].Type);
        var builder = new Decimal128Array.Builder(type);
        foreach (var row in Rows)
        {
            if (row[column] is decimal value)
            {
                builder.Append(value);
            }
            else
            {
                builder.AppendNull();
            }
        }

        return builder.Build();
    }

    private static IArrowArray BuildTimestamp(int column)
    {
        var type = (Apache.Arrow.Types.TimestampType)ArrowTypeMapping.ToArrow(Columns[column].Type);
        var builder = new TimestampArray.Builder(type);
        foreach (var row in Rows)
        {
            if (row[column] is DateTime value)
            {
                builder.Append(new DateTimeOffset(value, TimeSpan.Zero));
            }
            else
            {
                builder.AppendNull();
            }
        }

        return builder.Build();
    }

    private static IArrowArray BuildDate(int column)
    {
        var builder = new Date32Array.Builder();
        foreach (var row in Rows)
        {
            if (row[column] is DateOnly value)
            {
                builder.Append(value);
            }
            else
            {
                builder.AppendNull();
            }
        }

        return builder.Build();
    }

    private static string SqlType(ChalkType type) => type.Kind switch
    {
        TypeKind.I64 => "BIGINT",
        TypeKind.String => "VARCHAR",
        TypeKind.Decimal => $"DECIMAL({type.Precision},{type.Scale})",
        TypeKind.Timestamp => "TIMESTAMP",
        TypeKind.Date => "DATE",
        _ => throw new NotSupportedException($"the dataset has no SQL spelling for {type}"),
    };

    private static string Literal(object? value, ChalkType type, DialectProfileDescriptor profile) =>
        value switch
        {
            null => "NULL",
            long v => v.ToString(CultureInfo.InvariantCulture),
            decimal v => v.ToString(CultureInfo.InvariantCulture),
            string v => "'" + v.Replace("'", "''", StringComparison.Ordinal) + "'",
            DateOnly v => TemporalLiteral("DATE", v.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), profile),
            DateTime v => TemporalLiteral(
                "TIMESTAMP", v.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture), profile),
            _ => throw new NotSupportedException($"no literal for {value.GetType().Name}"),
        };

    /// <summary>
    /// A temporal literal. SQLite has no temporal literal syntax at all and compares ISO-8601 text,
    /// which is the same assumption Chalk's SQLite dialect makes when it generates SQL.
    /// </summary>
    private static string TemporalLiteral(string keyword, string text, DialectProfileDescriptor profile) =>
        profile.Dialect.Equals("sqlite", StringComparison.OrdinalIgnoreCase)
            ? "'" + text + "'"
            : keyword + " '" + text + "'";

    internal static void Identifier(StringBuilder sql, string name, DialectProfileDescriptor profile)
    {
        var (open, close) = profile.Quoting switch
        {
            IdentifierQuoting.None => ('\0', '\0'),
            IdentifierQuoting.BackTick => ('`', '`'),
            IdentifierQuoting.Bracket => ('[', ']'),
            _ => ('"', '"'),
        };

        if (open == '\0')
        {
            sql.Append(name);
            return;
        }

        sql.Append(open);
        foreach (var c in name)
        {
            if (c == close)
            {
                sql.Append(close);
            }

            sql.Append(c);
        }

        sql.Append(close);
    }
}

using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using Chalk.Catalog;

namespace Chalk.Sources.Ado;

/// <summary>
/// Reading a database's shape out of the provider (§3): tables, columns, primary keys, indexes and
/// foreign keys.
/// </summary>
/// <remarks>
/// <para>
/// Three routes, tried in order, because no one route works everywhere — a fact worth stating
/// rather than discovering at a customer's:
/// </para>
/// <list type="number">
/// <item>
/// <b>ADO.NET's <c>GetSchema</c> collections.</b> The portable route where a provider implements
/// it. DuckDB.NET, Npgsql and Microsoft.Data.SqlClient all do; the column names differ between them
/// and are matched case-insensitively against a list of the spellings in use.
/// </item>
/// <item>
/// <b><c>information_schema</c>.</b> The SQL standard's own answer, and what a provider with no
/// schema collections usually still has.
/// </item>
/// <item>
/// <b>SQLite's pragmas.</b> Microsoft.Data.Sqlite implements neither of the above — it reports
/// exactly two collections, <c>MetaDataCollections</c> and <c>ReservedWords</c> — and SQLite has no
/// <c>information_schema</c>. <c>sqlite_schema</c> plus <c>PRAGMA table_info</c>,
/// <c>index_list</c>, <c>index_info</c> and <c>foreign_key_list</c> is the route that works, and it
/// is the one the corpus exercises every run.
/// </item>
/// </list>
/// <para>
/// Nothing is invented. No collation is declared, because a <c>SELECT</c> without <c>ORDER BY</c>
/// promises no order; no row count unless the host asked for one; and a column whose declared type
/// does not map is a registration error naming the column and the type, because guessing at it is
/// how a wrong answer starts.
/// </para>
/// </remarks>
internal static class AdoDiscovery
{
    /// <summary>Every table the provider reports, in <paramref name="schema"/> when it has schemas.</summary>
    public static IReadOnlyList<TableRegistration> Tables(
        DbConnection connection, string schema, DialectProfileDescriptor profile)
    {
        var tables = FromSchemaCollections(connection, schema);
        if (tables.Count > 0)
        {
            return tables;
        }

        tables = FromInformationSchema(connection, schema);
        if (tables.Count > 0)
        {
            return tables;
        }

        return FromSqlitePragmas(connection);
    }

    /// <summary>One <c>COUNT(*)</c>. Opt-in, because on a large table it is not free.</summary>
    public static long Count(DbConnection connection, string remoteName, DialectProfileDescriptor profile)
    {
        var sql = new StringBuilder("SELECT COUNT(*) FROM ");
        AdoQuoting.AppendQualifiedName(sql, remoteName, profile);
        using var command = connection.CreateCommand();
        command.CommandText = sql.ToString();
        try
        {
            var value = command.ExecuteScalar();
            return value is null or DBNull
                ? -1
                : Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
        catch (Exception failure)
        {
            throw new SourceExecutionException(connection.Database, command.CommandText, failure);
        }
    }

    // ---------------------------------------------------------------- 1. GetSchema

    private static List<TableRegistration> FromSchemaCollections(DbConnection connection, string schema)
    {
        var tableRows = TryGetSchema(connection, "Tables", schema);
        var columnRows = TryGetSchema(connection, "Columns", schema);
        if (tableRows is null || columnRows is null)
        {
            return [];
        }

        var columns = new Dictionary<string, List<ColumnDescriptor>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Ordered(columnRows))
        {
            var table = Text(row, "TABLE_NAME");
            var name = Text(row, "COLUMN_NAME");
            var tableSchema = Text(row, "TABLE_SCHEMA", "TABLE_SCHEM", "SCHEMA_NAME");
            if (table.Length == 0 || name.Length == 0 || IsSystem(tableSchema, table))
            {
                continue;
            }

            Add(columns, Key(tableSchema, table), Column(
                table,
                name,
                Text(row, "DATA_TYPE", "TYPE_NAME", "COLUMN_TYPE"),
                Flag(row, "IS_NULLABLE", "ALLOWDBNULL") ?? true,
                (int)Number(row, "NUMERIC_PRECISION", "PRECISION", "COLUMN_PRECISION"),
                (int)Number(row, "NUMERIC_SCALE", "SCALE", "COLUMN_SCALE")));
        }

        var tables = new List<TableRegistration>();
        foreach (DataRow row in tableRows.Rows)
        {
            var type = Text(row, "TABLE_TYPE");
            if (type.Length > 0
                && !type.Contains("TABLE", StringComparison.OrdinalIgnoreCase)
                && !type.Contains("VIEW", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var tableSchema = Text(row, "TABLE_SCHEMA", "TABLE_SCHEM", "SCHEMA_NAME");
            var name = Text(row, "TABLE_NAME");
            if (name.Length == 0
                || IsSystem(tableSchema, name)
                || !columns.TryGetValue(Key(tableSchema, name), out var tableColumns))
            {
                continue;
            }

            tables.Add(new AdoTableBuilder(name, tableColumns, Qualified(tableSchema, name)).Build());
        }

        return tables;
    }

    // ---------------------------------------------------------------- 2. information_schema

    private static List<TableRegistration> FromInformationSchema(DbConnection connection, string schema)
    {
        var filter = schema.Length == 0 ? string.Empty : " AND table_schema = '" + schema.Replace("'", "''", StringComparison.Ordinal) + "'";
        var columns = new Dictionary<string, List<ColumnDescriptor>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT table_schema, table_name, column_name, data_type, is_nullable, "
                + "numeric_precision, numeric_scale FROM information_schema.columns"
                + " WHERE 1 = 1" + filter + " ORDER BY table_schema, table_name, ordinal_position";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var tableSchema = Str(reader, 0);
                var table = Str(reader, 1);
                if (IsSystem(tableSchema, table))
                {
                    continue;
                }

                Add(columns, Key(tableSchema, table), Column(
                    table,
                    Str(reader, 2),
                    Str(reader, 3),
                    !Str(reader, 4).Equals("NO", StringComparison.OrdinalIgnoreCase),
                    Int(reader, 5),
                    Int(reader, 6)));
            }
        }
        catch (Exception failure) when (failure is DbException or InvalidOperationException or NotSupportedException)
        {
            return [];
        }

        var tables = new List<TableRegistration>();
        foreach (var (key, tableColumns) in columns)
        {
            var split = key.IndexOf(' ', StringComparison.Ordinal);
            var tableSchema = key[..split];
            var name = key[(split + 1)..];
            tables.Add(new AdoTableBuilder(name, tableColumns, Qualified(tableSchema, name)).Build());
        }

        return tables;
    }

    // ---------------------------------------------------------------- 3. SQLite's pragmas

    /// <summary>
    /// SQLite, whose driver implements no schema collections and whose engine has no
    /// <c>information_schema</c>. Unlike the other two routes this one <em>can</em> read indexes and
    /// foreign keys, so a SQLite source arrives with everything the planner can use.
    /// </summary>
    private static List<TableRegistration> FromSqlitePragmas(DbConnection connection)
    {
        List<string> names = [];
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT name FROM sqlite_schema WHERE type IN ('table', 'view') "
                + "AND name NOT LIKE 'sqlite_%' ORDER BY name";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                names.Add(Str(reader, 0));
            }
        }
        catch (Exception failure) when (failure is DbException or InvalidOperationException or NotSupportedException)
        {
            return [];
        }

        var tables = new List<TableRegistration>(names.Count);
        foreach (var name in names)
        {
            List<ColumnDescriptor> columns = [];
            List<string> primaryKey = [];
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA table_info(" + Literal(name) + ")";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var column = Str(reader, 1);
                    var declared = Str(reader, 2);
                    var notNull = Int(reader, 3) != 0;
                    var pk = Int(reader, 5);
                    columns.Add(Column(name, column, declared, !notNull, 0, 0));
                    if (pk > 0)
                    {
                        primaryKey.Add(column);
                    }
                }
            }

            if (columns.Count == 0)
            {
                continue;
            }

            var builder = new AdoTableBuilder(name, columns, name);
            if (primaryKey.Count > 0)
            {
                builder.UniqueKey([.. primaryKey]);
            }

            foreach (var (index, unique) in SqliteIndexes(connection, name))
            {
                var indexColumns = SqliteIndexColumns(connection, index);
                if (indexColumns.Count > 0)
                {
                    builder.Index(index, unique, [.. indexColumns]);
                }
            }

            tables.Add(builder.Build());
        }

        return tables;
    }

    private static List<(string Name, bool Unique)> SqliteIndexes(DbConnection connection, string table)
    {
        List<(string, bool)> indexes = [];
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA index_list(" + Literal(table) + ")";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var name = Str(reader, 1);

            // `origin` is 'c' for a CREATE INDEX, 'pk' for the implicit primary-key index and 'u'
            // for a UNIQUE constraint's. The first two are already declared as the unique key, and
            // declaring them twice would just make the planner consider the same fact twice.
            var origin = reader.FieldCount > 3 ? Str(reader, 3) : "c";
            if (!origin.Equals("c", StringComparison.OrdinalIgnoreCase)
                && !origin.Equals("u", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            indexes.Add((name, Int(reader, 2) != 0));
        }

        return indexes;
    }

    private static List<string> SqliteIndexColumns(DbConnection connection, string index)
    {
        List<string> columns = [];
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA index_info(" + Literal(index) + ")";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.IsDBNull(2) ? string.Empty : Str(reader, 2);
            if (name.Length == 0)
            {
                // An expression index has no column name; the planner can do nothing with it.
                return [];
            }

            columns.Add(name);
        }

        return columns;
    }

    // ---------------------------------------------------------------- shared

    private static ColumnDescriptor Column(
        string table, string name, string declared, bool nullable, int precision, int scale)
    {
        var type = AdoTypeMapping.FromSqlType(declared, nullable, precision, scale)
            ?? throw new CatalogValidationException(
                $"table '{table}'",
                $"column '{name}' is declared '{declared}', which Chalk has no type for. Register "
                + "the table explicitly with AddTable and give the column a type, or cast it in a "
                + "view.");
        return new ColumnDescriptor { Name = name, Type = type };
    }

    private static void Add(
        Dictionary<string, List<ColumnDescriptor>> columns, string key, ColumnDescriptor column)
    {
        if (!columns.TryGetValue(key, out var list))
        {
            list = [];
            columns[key] = list;
        }

        list.Add(column);
    }

    /// <summary>
    /// Ordinal position is what makes a scan's column order match the descriptor's; a provider that
    /// does not report it is read in the order the rows came, which is what that means.
    /// </summary>
    private static IEnumerable<DataRow> Ordered(DataTable rows) => rows.Rows
        .Cast<DataRow>()
        .OrderBy(r => Text(r, "TABLE_SCHEMA", "TABLE_SCHEM", "SCHEMA_NAME"), StringComparer.Ordinal)
        .ThenBy(r => Text(r, "TABLE_NAME"), StringComparer.Ordinal)
        .ThenBy(r => Number(r, "ORDINAL_POSITION", "COLUMN_ORDINAL", "COLUMN_ID"));

    /// <summary>
    /// A schema collection, or null when the provider does not have it. Providers differ in which
    /// collections they implement and in whether they accept restrictions; "does not have it" is a
    /// perfectly good answer and never a failure.
    /// </summary>
    private static DataTable? TryGetSchema(DbConnection connection, string collection, string schema)
    {
        try
        {
            return schema.Length == 0
                ? connection.GetSchema(collection)
                : connection.GetSchema(collection, [null, schema, null, null]);
        }
        catch (Exception first) when (Recoverable(first))
        {
            try
            {
                return connection.GetSchema(collection);
            }
            catch (Exception second) when (Recoverable(second))
            {
                return null;
            }
        }
    }

    private static bool Recoverable(Exception failure) =>
        failure is ArgumentException or NotSupportedException or DbException
            or InvalidOperationException or IndexOutOfRangeException;

    private static bool IsSystem(string schema, string table) =>
        table.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase)
        || schema.Equals("information_schema", StringComparison.OrdinalIgnoreCase)
        || schema.Equals("pg_catalog", StringComparison.OrdinalIgnoreCase)
        || schema.Equals("system", StringComparison.OrdinalIgnoreCase)
        || schema.Equals("temp", StringComparison.OrdinalIgnoreCase);

    private static string Key(string schema, string table) => schema + " " + table;

    /// <summary>
    /// The name the source knows a table by. The schema is left off for the schemas every engine
    /// resolves without help — SQLite's <c>main</c>, DuckDB's and PostgreSQL's <c>public</c> — so
    /// the generated SQL reads the way a person would write it.
    /// </summary>
    private static string Qualified(string schema, string table) =>
        schema.Length == 0
        || schema.Equals("main", StringComparison.OrdinalIgnoreCase)
        || schema.Equals("public", StringComparison.OrdinalIgnoreCase)
            ? table
            : schema + "." + table;

    private static string Literal(string value) =>
        "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string Str(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? string.Empty
            : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture) ?? string.Empty;

    private static int Int(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return 0;
        }

        try
        {
            return Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
        }
        catch (Exception failure) when (failure is FormatException or InvalidCastException or OverflowException)
        {
            return 0;
        }
    }

    private static string Text(DataRow row, params string[] columns)
    {
        foreach (var column in columns)
        {
            if (row.Table.Columns.Contains(column) && row[column] is not (null or DBNull))
            {
                return Convert.ToString(row[column], CultureInfo.InvariantCulture) ?? string.Empty;
            }
        }

        return string.Empty;
    }

    private static long Number(DataRow row, params string[] columns)
    {
        foreach (var column in columns)
        {
            if (row.Table.Columns.Contains(column) && row[column] is not (null or DBNull))
            {
                try
                {
                    return Convert.ToInt64(row[column], CultureInfo.InvariantCulture);
                }
                catch (Exception failure) when (failure is FormatException or InvalidCastException or OverflowException)
                {
                    return 0;
                }
            }
        }

        return 0;
    }

    private static bool? Flag(DataRow row, params string[] columns)
    {
        foreach (var column in columns)
        {
            if (!row.Table.Columns.Contains(column) || row[column] is null or DBNull)
            {
                continue;
            }

            var value = row[column];
            return value switch
            {
                bool b => b,
                string s => s.Equals("YES", StringComparison.OrdinalIgnoreCase)
                    || s.Equals("TRUE", StringComparison.OrdinalIgnoreCase)
                    || s == "1",
                _ => Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0,
            };
        }

        return null;
    }
}

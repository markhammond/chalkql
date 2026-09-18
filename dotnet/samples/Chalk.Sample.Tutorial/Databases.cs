using System.Data.Common;
using System.Text;
using Chalk.Catalog;
using Chalk.Ir;
using DuckDB.NET.Data;
using Microsoft.Data.Sqlite;

namespace Chalk.Sample.Tutorial;

/// <summary>
/// A real database the tutorial creates, fills and deletes: a real file, a real driver, a real
/// dialect. Two of them — SQLite for the reference tables and DuckDB for the facts — because
/// the marketplace puts them there, and because a federated engine with
/// one database in it is not federated.
/// </summary>
/// <remarks>
/// The DDL is generated from the same <see cref="TableDescriptor"/> the POCO source describes, so
/// the three copies of the marketplace cannot drift: change a property on a record and all three
/// change together. The rows travel as ADO parameters rather than as SQL literals, which is what a
/// host would do and what keeps a name with an apostrophe in it from being a quoting problem.
/// </remarks>
public sealed class Database : IDisposable
{
    private readonly Func<DbConnection> _connect;
    private readonly DbConnection? _keepAlive;

    private Database(
        string sourceId,
        DialectProfileDescriptor dialect,
        Func<DbConnection> connect,
        string? path,
        DbConnection? keepAlive)
    {
        SourceId = sourceId;
        Dialect = dialect;
        _connect = connect;
        Path = path;
        _keepAlive = keepAlive;
    }

    /// <summary>The source id, which is also the schema name — so SQL says <c>oltp.customers</c>.</summary>
    public string SourceId { get; }

    /// <summary>The dialect profile this database's SQL is generated in.</summary>
    public DialectProfileDescriptor Dialect { get; }

    /// <summary>The database file, when there is one. DuckDB has one; SQLite here is in memory.</summary>
    public string? Path { get; }

    /// <summary>A new, empty DuckDB database under the machine's temp root.</summary>
    public static Database DuckDb(string sourceId)
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "chalk-tutorial");
        Directory.CreateDirectory(directory);
        var path = System.IO.Path.Combine(directory, $"{sourceId}-{Guid.NewGuid():N}.duckdb");
        var connectionString = "DataSource=" + path;
        return new Database(
            sourceId,
            DialectProfiles.DuckDb,
            () => new DuckDBConnection(connectionString),
            path,
            keepAlive: null);
    }

    /// <summary>
    /// A new, empty SQLite database. In memory, with a shared cache, so it lives exactly as long as
    /// the connection this object holds open — which is what makes it a real SQLite database with a
    /// real dialect and no file to clean up.
    /// </summary>
    public static Database Sqlite(string sourceId)
    {
        var connectionString = $"Data Source=chalk-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        var keepAlive = new SqliteConnection(connectionString);
        keepAlive.Open();
        return new Database(
            sourceId,
            DialectProfiles.Sqlite,
            () => new SqliteConnection(connectionString),
            path: null,
            keepAlive);
    }

    /// <summary>A new connection to this database. The caller — usually the source — disposes it.</summary>
    public DbConnection Open() => _connect();

    /// <summary>The <c>CREATE TABLE</c> for one table, in this database's dialect.</summary>
    public string Ddl(TableDescriptor table)
    {
        ArgumentNullException.ThrowIfNull(table);
        var sql = new StringBuilder("CREATE TABLE \"").Append(table.Name).Append("\" (\n");
        for (var i = 0; i < table.Columns.Count; i++)
        {
            var column = table.Columns[i];
            sql.Append("  \"").Append(column.Name).Append("\" ").Append(SqlType(column.Type));
            if (!column.Type.Nullable)
            {
                sql.Append(" NOT NULL");
            }

            sql.Append(i == table.Columns.Count - 1 ? "\n" : ",\n");
        }

        return sql.Append(')').ToString();
    }

    /// <summary>Creates one table and fills it from the rows the host already has.</summary>
    public void Load<T>(TableDescriptor table, IReadOnlyList<T> rows, Func<T, object?[]> values)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(values);

        using var connection = _connect();
        connection.Open();
        Execute(connection, Ddl(table));

        // DuckDB has an appender, and it is an order of magnitude faster than any INSERT. The
        // chapters that want a cost model to have an opinion load twenty thousand orders and their
        // lines, which is the difference between a second and a minute.
        if (connection is DuckDBConnection duck)
        {
            using var appender = duck.CreateAppender(table.Name);
            foreach (var row in rows)
            {
                var appended = appender.CreateRow();
                foreach (var cell in values(row))
                {
                    appended = Append(appended, cell);
                }

                appended.EndRow();
            }

            return;
        }

        var names = string.Join(", ", table.Columns.Select(c => '"' + c.Name + '"'));
        var marks = string.Join(", ", table.Columns.Select((_, i) => Parameter(i)));
        using var command = connection.CreateCommand();
        command.CommandText = $"INSERT INTO \"{table.Name}\" ({names}) VALUES ({marks})";
        for (var i = 0; i < table.Columns.Count; i++)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = Parameter(i);
            command.Parameters.Add(parameter);
        }

        command.Prepare();
        using var transaction = connection.BeginTransaction();
        command.Transaction = transaction;
        foreach (var row in rows)
        {
            var cells = values(row);
            for (var i = 0; i < cells.Length; i++)
            {
                command.Parameters[i].Value = Cell(cells[i]);
            }

            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public void Dispose()
    {
        _keepAlive?.Dispose();
        if (Path is not null)
        {
            Delete(Path);
            Delete(Path + ".wal");
        }
    }

    private string Parameter(int ordinal) =>
        Dialect.Dialect == "duckdb" ? "$p" + ordinal : "@p" + ordinal;

    /// <summary>
    /// Chalk's type, in this dialect's spelling. Generated rather than written out, because the
    /// marketplace is declared once — on the records — and a second spelling of it would be a
    /// second thing to keep true.
    /// </summary>
    private string SqlType(ChalkType type) => type.Kind switch
    {
        TypeKind.Bool => "BOOLEAN",
        TypeKind.I8 or TypeKind.I16 or TypeKind.I32 => "INTEGER",
        TypeKind.I64 => "BIGINT",
        TypeKind.Fp32 => "REAL",
        TypeKind.Fp64 => "DOUBLE",
        TypeKind.Decimal => $"DECIMAL({type.Precision},{type.Scale})",
        TypeKind.String => "VARCHAR",
        TypeKind.Date => "DATE",
        TypeKind.Timestamp => "TIMESTAMP",
        _ => throw new NotSupportedException($"the tutorial has no SQL spelling for {type.Kind}."),
    };

    private static IDuckDBAppenderRow Append(IDuckDBAppenderRow row, object? value) => value switch
    {
        null => row.AppendNullValue(),
        bool v => row.AppendValue(v),
        int v => row.AppendValue(v),
        long v => row.AppendValue(v),
        double v => row.AppendValue(v),
        decimal v => row.AppendValue(v),
        string v => row.AppendValue(v),
        Utf8String v => row.AppendValue(v.ToString()),
        DateTime v => row.AppendValue(v),
        DateOnly v => row.AppendValue(v),
        _ => throw new NotSupportedException($"the tutorial cannot append a {value.GetType().Name}."),
    };

    private static object Cell(object? value) => value switch
    {
        null => DBNull.Value,
        Utf8String utf8 => utf8.ToString(),

        // Microsoft.Data.Sqlite has no DateOnly binding: a date is text in SQLite, and ISO-8601 is
        // what the provider reads back into one.
        DateOnly date => date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        _ => value,
    };

    private static void Execute(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best effort: the file is under the machine's temp root, which is swept anyway.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

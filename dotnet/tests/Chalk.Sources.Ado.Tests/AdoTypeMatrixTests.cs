using System.Data.Common;
using System.Globalization;
using Apache.Arrow;
using Apache.Arrow.Types;
using Chalk.Catalog;
using Chalk.Sources.DuckDb;
using DuckDB.NET.Data;
using Chalk.TestKit;
using Microsoft.Data.Sqlite;
using Npgsql;
using ArrowSchema = Apache.Arrow.Schema;

namespace Chalk.Sources.Ado.Tests;

/// <summary>
/// The reader type matrix (D167, <c>docs/design/25-coverage-graft.md</c> §4): every Chalk type a
/// remote source can read, against every real dialect and every read path, with a value row and a
/// NULL row.
/// </summary>
/// <remarks>
/// <para>
/// <b>Provenance.</b> The checklist is grafted from <c>ikvmnet/calcite-dotnet</c> (Apache-2.0) —
/// <c>src/Apache.Calcite.Data.Tests/CalciteTypeCoverageTests.cs</c> and
/// <c>src/Apache.Calcite.Adapter.AdoNet.Tests/AdoReaderUtilTests.cs</c>, whose
/// <c>DateIsReadAsDaysSinceTheEpoch</c>, <c>TheEpochItselfIsDayZero</c>,
/// <c>ADateBeforeTheEpochIsNegative</c>, <c>MidnightDoesNotDependOnTheMachineTimeZone</c>,
/// <c>AnEmptyStringIsNotNull</c>, <c>EveryTypeReadsADatabaseNullAsNull</c> and
/// <c>AnUnmappedTypeIsRefusedByName</c> name the cases. See the repository's <c>NOTICE</c>. The
/// checklist is taken; the values are Chalk's own contract (<c>02-ir.md</c> §3), never theirs
/// (D163).
/// </para>
/// <para>
/// <b>Every path, per dialect.</b> A path is a (driver, <see cref="IRemoteFetch"/>) pair, and the
/// five of them cover what §4 asks for: SQLite is dynamically typed, so its reader takes the
/// <b>boxed</b> path and its text takes a strategy of its own (D149); DuckDB and PostgreSQL report
/// their field types, so theirs take the <b>typed</b> path; and DuckDB is read three ways — through
/// the provider's <c>DbDataReader</c>, through the vector copier (D148) and through the Arrow
/// export (D148a).
/// </para>
/// <para>
/// PostgreSQL is skipped unless <c>CHALK_TEST_POSTGRES</c> names a server, and required when
/// <c>CHALK_TEST_POSTGRES_REQUIRED</c> is set — the same rule the rest of the suite follows.
/// </para>
/// </remarks>
[Collection(TypeMatrixCollection.Name)]
public sealed class AdoTypeMatrixTests(SharedTypeMatrixPostgres postgres)
{
    // ------------------------------------------------------------------ the type table

    /// <summary>
    /// One column of the matrix: what the catalog declares, how each dialect spells it, the value
    /// the first row holds, and what reading it back must produce.
    /// </summary>
    private sealed record TypeCase(
        string Column,
        ChalkType Type,
        IArrowType Arrow,
        string Ddl,
        string Literal,
        object? Expected)
    {
        /// <summary>Dialects that cannot store this type at all, by name.</summary>
        public string[] NotOn { get; init; } = [];

        /// <summary>A per-dialect override of <see cref="Ddl"/> and <see cref="Literal"/>.</summary>
        public Dictionary<string, (string Ddl, string Literal)> Override { get; init; } = [];

        public override string ToString() => Column;
    }

    /// <summary>Days from 1970-01-01, which is what a DATE is on the wire (02-ir.md §3).</summary>
    private static int Days(int year, int month, int day) =>
        new DateOnly(year, month, day).DayNumber - new DateOnly(1970, 1, 1).DayNumber;

    private static readonly TypeCase[] Matrix =
    [
        new("c_bool", ChalkType.Bool(nullable: true), BooleanType.Default,
            "BOOLEAN", "TRUE", true)
        {
            // SQLite has no BOOLEAN; a 1 in an INTEGER column is what it stores and what it hands
            // back, and IsCompatible accepts an integer for a BOOL for exactly that reason.
            Override = { ["sqlite"] = ("INTEGER", "1") },
        },
        new("c_i8", ChalkType.Int8(nullable: true), Int8Type.Default, "SMALLINT", "-3", (sbyte)-3)
        {
            Override = { ["sqlite"] = ("INTEGER", "-3"), ["duckdb"] = ("TINYINT", "-3") },
        },
        new("c_i16", ChalkType.Int16(nullable: true), Int16Type.Default,
            "SMALLINT", "-300", (short)-300)
        {
            Override = { ["sqlite"] = ("INTEGER", "-300") },
        },
        new("c_i32", ChalkType.Int32(nullable: true), Int32Type.Default,
            "INTEGER", "-70000", -70000),
        new("c_i64", ChalkType.Int64(nullable: true), Int64Type.Default,
            "BIGINT", "-5000000000", -5000000000L),

        // An unsigned value that still fits the declared signed width: DuckDB is the only dialect
        // here with unsigned integers, and the reader widens rather than reinterpreting.
        new("c_u8", ChalkType.Int16(nullable: true), Int16Type.Default,
            "SMALLINT", "200", (short)200)
        {
            Override = { ["sqlite"] = ("INTEGER", "200"), ["duckdb"] = ("UTINYINT", "200") },

            // The Arrow export hands a UTINYINT over as uint8 and cannot widen it into the declared
            // INT16, so it falls back for the whole query (V51). Asserted on its own below rather
            // than by putting a column here that would send the rest of the table down the other
            // path with it.
            NotOn = ["duckdb-arrow"],
        },
        new("c_u16", ChalkType.Int32(nullable: true), Int32Type.Default,
            "INTEGER", "60000", 60000)
        {
            Override = { ["sqlite"] = ("INTEGER", "60000"), ["duckdb"] = ("USMALLINT", "60000") },
            NotOn = ["duckdb-arrow"],
        },

        new("c_fp32", ChalkType.Float32(nullable: true), FloatType.Default, "REAL", "1.5", 1.5f),
        new("c_fp64", ChalkType.Float64(nullable: true), DoubleType.Default,
            "DOUBLE", "2.5", 2.5d)
        {
            Override = { ["postgresql"] = ("DOUBLE PRECISION", "2.5") },
        },
        new("c_decimal", ChalkType.Decimal(18, 4, nullable: true), new Decimal128Type(18, 4),
            "DECIMAL(18,4)", "12.3456", 12.3456m),
        new("c_string", ChalkType.String(nullable: true), StringType.Default,
            "VARCHAR", "'a value longer than twelve bytes'", "a value longer than twelve bytes"),

        // An empty string is a value, not a NULL. AnEmptyStringIsNotNull, and the one every
        // dynamically typed driver gets wrong first.
        new("c_empty", ChalkType.String(nullable: true), StringType.Default, "VARCHAR", "''", ""),

        new("c_binary", ChalkType.Binary(nullable: true), BinaryType.Default,
            "BLOB", "'ab'::BLOB", "ab"u8.ToArray())
        {
            Override =
            {
                ["sqlite"] = ("BLOB", "x'6162'"),
                ["postgresql"] = ("BYTEA", "'\\x6162'::bytea"),
            },

            // V45, F24: DuckDB.NET hands a BLOB back as an UnmanagedMemoryStream, which
            // AdoTypeMapping has never accepted for BINARY. The copier and the Arrow export read
            // the bytes themselves; the provider's own reader cannot.
            NotOn = ["duckdb-provider"],
        },

        // DATE is days since the epoch, not a millisecond count. Three rows say so: a date after
        // the epoch, the epoch itself as day zero, and one before it as a negative.
        new("c_date", ChalkType.Date(nullable: true), Date32Type.Default,
            "DATE", "DATE '2020-03-04'", Days(2020, 3, 4))
        {
            // SQLite has no DATE literal and no DATE storage class: a host writes the ISO text and
            // the reader parses it, which is why IsCompatible accepts a string for a DATE.
            Override = { ["sqlite"] = ("DATE", "'2020-03-04'") },
        },
        new("c_epoch", ChalkType.Date(nullable: true), Date32Type.Default,
            "DATE", "DATE '1970-01-01'", 0)
        {
            Override = { ["sqlite"] = ("DATE", "'1970-01-01'") },
        },
        new("c_pre_epoch", ChalkType.Date(nullable: true), Date32Type.Default,
            "DATE", "DATE '1969-12-31'", -1)
        {
            Override = { ["sqlite"] = ("DATE", "'1969-12-31'") },
        },

        // Midnight is midnight wherever the machine stands: a TIME of 00:00:00 is zero
        // microseconds, and a timestamp at midnight is a whole number of days.
        new("c_midnight", ChalkType.Time(6, nullable: true), new Time64Type(TimeUnit.Microsecond),
            "TIME", "TIME '00:00:00'", 0L)
        {
            Override = { ["sqlite"] = ("TIME", "'00:00:00'") },
        },
        new("c_time", ChalkType.Time(6, nullable: true), new Time64Type(TimeUnit.Microsecond),
            "TIME", "TIME '12:34:56.789012'",
            (((((12L * 60) + 34) * 60) + 56) * 1_000_000L) + 789_012L)
        {
            Override = { ["sqlite"] = ("TIME", "'12:34:56.789012'") },
        },
        new("c_timestamp", ChalkType.Timestamp(6, nullable: true),
            new TimestampType(TimeUnit.Microsecond, timezone: (string?)null),
            "TIMESTAMP", "TIMESTAMP '2020-03-04 12:34:56.789012'",
            (new DateTime(2020, 3, 4, 12, 34, 56, DateTimeKind.Unspecified)
                .AddTicks(7_890_120) - DateTime.UnixEpoch).Ticks / 10L)
        {
            Override = { ["sqlite"] = ("TIMESTAMP", "'2020-03-04 12:34:56.789012'") },
        },
        new("c_midnight_ts", ChalkType.Timestamp(6, nullable: true),
            new TimestampType(TimeUnit.Microsecond, timezone: (string?)null),
            "TIMESTAMP", "TIMESTAMP '2020-03-04 00:00:00'",
            (long)(new DateOnly(2020, 3, 4).DayNumber - new DateOnly(1970, 1, 1).DayNumber)
                * 86_400_000_000L)
        {
            Override = { ["sqlite"] = ("TIMESTAMP", "'2020-03-04 00:00:00'") },
        },

        new("c_uuid", ChalkType.Uuid(nullable: true), new FixedSizeBinaryType(16),
            "UUID", "'550e8400-e29b-41d4-a716-446655440000'::UUID",
            new Guid("550e8400-e29b-41d4-a716-446655440000").ToByteArray(bigEndian: true))
        {
            // SQLite has no UUID; a text column holding the canonical spelling is what a host does,
            // and IsCompatible accepts a string for a UUID.
            Override =
            {
                ["sqlite"] = ("TEXT", "'550e8400-e29b-41d4-a716-446655440000'"),
                ["postgresql"] = ("UUID", "'550e8400-e29b-41d4-a716-446655440000'"),
            },

            // The Arrow export hands a UUID over as utf8, not as the sixteen bytes Chalk's
            // FIXED_SIZE_BINARY(16) holds, so that path falls back for it (V51).
            NotOn = ["duckdb-arrow"],
        },
    ];

    // ------------------------------------------------------------------ the paths

    /// <summary>One row of the matrix's other axis: a driver and the fetch that reads it.</summary>
    private sealed record ReadPathCase(string Name, string Dialect)
    {
        public override string ToString() => Name;
    }

    private static readonly ReadPathCase[] Paths =
    [
        new("sqlite", "sqlite"),
        new("duckdb-provider", "duckdb"),
        new("duckdb-native", "duckdb"),
        new("duckdb-arrow", "duckdb"),
        new("postgresql", "postgresql"),
    ];

    public static TheoryData<string, string> Cells()
    {
        var data = new TheoryData<string, string>();
        foreach (var path in Paths)
        {
            foreach (var type in Matrix)
            {
                if (!type.NotOn.Contains(path.Name, StringComparer.Ordinal))
                {
                    data.Add(path.Name, type.Column);
                }
            }
        }

        return data;
    }

    public static TheoryData<string> PathNames()
    {
        var data = new TheoryData<string>();
        foreach (var path in Paths)
        {
            data.Add(path.Name);
        }

        return data;
    }

    /// <summary>
    /// One cell: the Arrow array is the declared type's, the value is the one that went in, and the
    /// second row is NULL. That last clause is <c>EveryTypeReadsADatabaseNullAsNull</c>, asserted
    /// per cell rather than once.
    /// </summary>
    [Theory]
    [MemberData(nameof(Cells))]
    public async Task Every_type_reads_back_as_itself_on_every_path(string pathName, string column)
    {
        using var fixture = TypeFixture.Create(Path(pathName), postgres);
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? string.Empty);

        var type = Matrix.Single(t => t.Column == column);
        var rows = await fixture.ReadAsync([type]).ConfigureAwait(true);

        Assert.Equal(2, rows.Batches.Sum(b => b.Length));
        var array = rows.Batches[0].Column(0);
        Assert.True(
            ArrowTypeMapping.AreEquivalent(type.Arrow, array.Data.DataType),
            $"{pathName}.{column}: expected Arrow {ArrowTypeMapping.DescribeArrow(type.Arrow)}, "
            + $"got {ArrowTypeMapping.DescribeArrow(array.Data.DataType)}");

        Assert.Equal(Render(type.Expected), Render(rows.Values[0][0]));
        Assert.Null(rows.Values[1][0]);
    }

    /// <summary>
    /// The whole matrix in one table on one path: every readable type side by side, which is the
    /// shape a real query has and the one where a column's reader can trample its neighbour's.
    /// </summary>
    [Theory]
    [MemberData(nameof(PathNames))]
    public async Task The_whole_matrix_reads_in_one_row(string pathName)
    {
        var path = Path(pathName);
        using var fixture = TypeFixture.Create(path, postgres);
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? string.Empty);

        var types = Matrix.Where(t => !t.NotOn.Contains(path.Name, StringComparer.Ordinal)).ToArray();
        var rows = await fixture.ReadAsync(types).ConfigureAwait(true);

        for (var i = 0; i < types.Length; i++)
        {
            Assert.Equal(Render(types[i].Expected), Render(rows.Values[0][i]));
            Assert.Null(rows.Values[1][i]);
        }
    }

    /// <summary>
    /// A declared type the column cannot hold is a <see cref="SourceContractException"/> naming the
    /// source, the table, the column and both types — <c>AnUnmappedTypeIsRefusedByName</c>, and the
    /// contract of <c>18-m4-capabilities-and-pushdown.md</c> §3.
    /// </summary>
    [Theory]
    [MemberData(nameof(PathNames))]
    public async Task A_declared_type_the_column_cannot_hold_is_refused_by_name(string pathName)
    {
        var path = Path(pathName);
        using var fixture = TypeFixture.Create(path, postgres);
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? string.Empty);

        // A text column read as a BOOLEAN. Nothing coerces a string to a boolean on any path.
        var lie = Matrix.Single(t => t.Column == "c_string") with
        {
            Type = ChalkType.Bool(nullable: true),
            Arrow = BooleanType.Default,
        };

        var failure = await Assert.ThrowsAsync<SourceContractException>(
            () => fixture.ReadAsync([lie])).ConfigureAwait(true);

        Assert.Contains("c_string", failure.Message, StringComparison.Ordinal);
        Assert.Contains("BOOL", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(fixture.SourceId, failure.SourceId);
    }

    /// <summary>
    /// The path each dialect actually took, so the matrix is on the record as having covered the
    /// typed reader, the boxed reader and both native DuckDB readers rather than one of them four
    /// times.
    /// </summary>
    [Theory]
    [MemberData(nameof(PathNames))]
    public async Task The_path_is_the_one_the_dialect_should_take(string pathName)
    {
        var path = Path(pathName);
        using var fixture = TypeFixture.Create(path, postgres);
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? string.Empty);

        var rows = await fixture.ReadAsync([Matrix.Single(t => t.Column == "c_string")])
            .ConfigureAwait(true);

        Assert.Equal(
            pathName switch
            {
                "duckdb-native" => "duckdb-native",
                "duckdb-arrow" => "duckdb-arrow",
                _ => "DbDataReader",
            },
            rows.Stats.SourcePaths[fixture.SourceId]);
    }

    /// <summary>
    /// V51 (ADR 0024): DuckDB's Arrow export hands over what DuckDB has, not what the catalog
    /// declared. A UTINYINT comes over as <c>uint8</c> and a UUID as <c>utf8</c>, neither of which
    /// is the declared Chalk type's Arrow form, so the whole query goes to the provider's reader
    /// and <c>Stats.SourcePaths</c> names both. The vector copier widens and reinterprets, so it
    /// reads the same columns natively — which is the one thing it can still do that the export
    /// cannot.
    /// </summary>
    [Theory]
    [InlineData("c_u8")]
    [InlineData("c_u16")]
    [InlineData("c_uuid")]
    public async Task The_Arrow_export_falls_back_where_DuckDB_hands_over_another_type(string column)
    {
        var type = Matrix.Single(t => t.Column == column);

        using var arrow = TypeFixture.Create(Path("duckdb-arrow"), postgres);
        Assert.SkipWhen(arrow.SkipReason is not null, arrow.SkipReason ?? string.Empty);
        var viaArrow = await arrow.ReadAsync([type]).ConfigureAwait(true);
        Assert.Equal("duckdb-arrow+DbDataReader", viaArrow.Stats.SourcePaths[arrow.SourceId]);

        using var copier = TypeFixture.Create(Path("duckdb-native"), postgres);
        var viaCopier = await copier.ReadAsync([type]).ConfigureAwait(true);
        Assert.Equal("duckdb-native", viaCopier.Stats.SourcePaths[copier.SourceId]);

        // Both still answer, and answer the same thing: the fallback is a slower path, not a
        // different one.
        Assert.Equal(Render(type.Expected), Render(viaArrow.Values[0][0]));
        Assert.Equal(Render(type.Expected), Render(viaCopier.Values[0][0]));
    }

    private static ReadPathCase Path(string name) => Paths.Single(p => p.Name == name);

    private static string Render(object? value) => value switch
    {
        null => "NULL",
        byte[] bytes => "0x" + Convert.ToHexStringLower(bytes),
        ReadOnlyMemory<byte> memory => "0x" + Convert.ToHexStringLower(memory.Span),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "NULL",
    };

    // ------------------------------------------------------------------ the fixture

    /// <summary>
    /// One database holding a <c>types</c> table with the requested columns, a value row and a NULL
    /// row, read back through the path under test.
    /// </summary>
    private sealed class TypeFixture : IDisposable
    {
        private readonly ReadPathCase _path;
        private readonly Func<DbConnection> _connect;
        private readonly DbConnection? _keepAlive;
        private readonly IDisposable? _owned;

        private TypeFixture(
            ReadPathCase path,
            Func<DbConnection>? connect,
            DbConnection? keepAlive,
            IDisposable? owned,
            string? skipReason)
        {
            _path = path;
            _connect = connect ?? (() => throw new InvalidOperationException("unavailable"));
            _keepAlive = keepAlive;
            _owned = owned;
            SkipReason = skipReason;
        }

        public string? SkipReason { get; }

        public string SourceId => _path.Name;

        public static TypeFixture Create(ReadPathCase path, SharedTypeMatrixPostgres postgres)
        {
            switch (path.Dialect)
            {
                case "sqlite":
                {
                    var connectionString =
                        $"Data Source=chalk-types-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
                    var keepAlive = new SqliteConnection(connectionString);
                    keepAlive.Open();
                    return new TypeFixture(
                        path, () => new SqliteConnection(connectionString), keepAlive, null, null);
                }

                case "duckdb":
                {
                    if (DuckDbProbe.SkipReason is { } why)
                    {
                        return new TypeFixture(path, null, null, null, why);
                    }

                    var file = new TempFile("types");
                    var connectionString = $"Data Source={file.FullPath}";
                    var keepAlive = new DuckDBConnection(connectionString);
                    keepAlive.Open();
                    return new TypeFixture(
                        path, () => new DuckDBConnection(connectionString), keepAlive, file, null);
                }

                default:
                {
                    var server = postgres.Fixture;
                    if (server.SkipReason is { } why)
                    {
                        return new TypeFixture(path, null, null, null, why);
                    }

                    var connectionString = postgres.Database;
                    return new TypeFixture(
                        path, () => new NpgsqlConnection(connectionString), null, null, null);
                }
            }
        }

        public void Dispose()
        {
            _keepAlive?.Dispose();
            _owned?.Dispose();
        }

        /// <summary>Creates the table, loads the two rows, and reads them back through the path.</summary>
        public async Task<(IReadOnlyList<RecordBatch> Batches, List<object?[]> Values, ExecutionStats Stats)>
            ReadAsync(IReadOnlyList<TypeCase> types)
        {
            using var connection = _connect();
            await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            var table = "types_" + Guid.NewGuid().ToString("N")[..8];
            await ExecuteAsync(connection, Create(table, types)).ConfigureAwait(true);
            await ExecuteAsync(connection, Insert(table, types)).ConfigureAwait(true);

            var source = Build(table, types);
            using var arena = new ExecutionArena();
            var stats = new ExecutionStats();
            var batches = new List<RecordBatch>();
            var values = new List<object?[]>();
            await foreach (var batch in source.ScanAsync(
                new ScanRequest
                {
                    Table = table,
                    Projection = [.. Enumerable.Range(0, types.Count)],
                    OutputSchema = Schema(types),
                    BatchSize = 4096,
                },
                new ScanContext { Stats = stats, Arena = arena },
                TestContext.Current.CancellationToken))
            {
                batches.Add(batch);
                for (var row = 0; row < batch.Length; row++)
                {
                    var cells = new object?[types.Count];
                    for (var c = 0; c < types.Count; c++)
                    {
                        cells[c] = Cell(batch.Column(c), row);
                    }

                    values.Add(cells);
                }
            }

            foreach (var batch in batches)
            {
                batch.Dispose();
            }

            return (batches, values, stats);
        }

        /// <summary>
        /// The storage value of one Arrow cell: the number or the bytes the IR says the type is,
        /// never a CLR date. That is the point of the DATE and TIME rows — a reader that produced a
        /// millisecond count would pass a <c>DateTime</c> comparison and fail this one.
        /// </summary>
        private static object? Cell(IArrowArray array, int row) => array switch
        {
            BooleanArray a => a.GetValue(row),
            Int8Array a => a.GetValue(row),
            Int16Array a => a.GetValue(row),
            Int32Array a => a.GetValue(row),
            Int64Array a => a.GetValue(row),
            FloatArray a => a.GetValue(row),
            DoubleArray a => a.GetValue(row),
            Decimal128Array a => a.GetValue(row),
            Date32Array a => a.IsNull(row) ? null : a.Values[row],
            Time64Array a => a.GetValue(row),
            TimestampArray a => a.GetValue(row),
            StringArray a => a.GetString(row),
            BinaryArray a => a.IsNull(row) ? null : a.GetBytes(row).ToArray(),
            Apache.Arrow.Arrays.FixedSizeBinaryArray a =>
                a.IsNull(row) ? null : a.GetBytes(row).ToArray(),
            _ => throw new NotSupportedException($"no cell reader for {array.GetType().Name}"),
        };

        private AdoSource Build(string table, IReadOnlyList<TypeCase> types)
        {
            var profile = _path.Dialect switch
            {
                "sqlite" => DialectProfiles.Sqlite,
                "duckdb" => DialectProfiles.DuckDb,
                _ => DialectProfiles.PostgreSql,
            };

            var builder = new AdoSourceBuilder(SourceId, _connect, "main")
                .Dialect(profile)
                .AddTable(
                    table,
                    [.. types.Select(t => new ColumnDescriptor { Name = t.Column, Type = t.Type })],
                    remoteName: table);

            return _path.Name switch
            {
                "duckdb-native" => builder.UseNativeReader().Build(),
                "duckdb-arrow" => builder.UseArrowReader().Build(),
                _ => builder.Build(),
            };
        }

        private ArrowSchema Schema(IReadOnlyList<TypeCase> types)
        {
            var builder = new ArrowSchema.Builder();
            foreach (var type in types)
            {
                builder.Field(ArrowTypeMapping.ToArrowField(type.Column, type.Type));
            }

            return builder.Build();
        }

        private string Create(string table, IReadOnlyList<TypeCase> types) =>
            $"CREATE TABLE \"{table}\" ("
            + string.Join(", ", types.Select(t => $"\"{t.Column}\" {Ddl(t)}"))
            + ")";

        private string Insert(string table, IReadOnlyList<TypeCase> types) =>
            $"INSERT INTO \"{table}\" VALUES ("
            + string.Join(", ", types.Select(Literal))
            + "), ("
            + string.Join(", ", types.Select(_ => "NULL"))
            + ")";

        private string Ddl(TypeCase type) =>
            type.Override.TryGetValue(_path.Dialect, out var over) ? over.Ddl : type.Ddl;

        private string Literal(TypeCase type) =>
            type.Override.TryGetValue(_path.Dialect, out var over) ? over.Literal : type.Literal;

        private static async Task ExecuteAsync(DbConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
        }
    }

    /// <summary>A DuckDB database file under the test temp root, removed with its side files.</summary>
    private sealed class TempFile : IDisposable
    {
        public TempFile(string name) =>
            FullPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"chalk-{name}-{Guid.NewGuid():N}.duckdb");

        public string FullPath { get; }

        public void Dispose()
        {
            foreach (var suffix in new[] { string.Empty, ".wal", ".tmp" })
            {
                try
                {
                    if (File.Exists(FullPath + suffix))
                    {
                        File.Delete(FullPath + suffix);
                    }
                }
                catch (IOException)
                {
                    // A file DuckDB still holds is the operating system's to reclaim.
                }
            }
        }
    }

    /// <summary>Whether DuckDB's native library is loadable here, asked once.</summary>
    private static class DuckDbProbe
    {
        public static string? SkipReason { get; } = Probe();

        private static string? Probe()
        {
            try
            {
                using var connection = new DuckDBConnection("Data Source=:memory:");
                connection.Open();
                return null;
            }
            catch (Exception failure)
            {
                return $"DuckDB is unavailable: {failure.Message}";
            }
        }
    }
}

/// <summary>
/// The PostgreSQL server the matrix's third dialect runs on: the ephemeral one of D133 §0b, started
/// once and shared, exactly as the integration suite shares it.
/// </summary>
public sealed class SharedTypeMatrixPostgres : IDisposable
{
    private readonly Lock _gate = new();
    private PostgresFixture? _server;
    private string? _database;
    private bool _disposed;

    /// <summary>The server, started on first access. Its <c>SkipReason</c> says why if there is none.</summary>
    public PostgresFixture Fixture
    {
        get
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _server ??= PostgresFixture.Start();
            }
        }
    }

    /// <summary>A database of this matrix's own, so its tables cannot collide with anything else's.</summary>
    public string Database
    {
        get
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var server = _server ??= PostgresFixture.Start();
                if (_database is not null)
                {
                    return _database;
                }

                try
                {
                    return _database = server.CreateDatabase("chalk_type_matrix");
                }
                catch (InvalidOperationException)
                {
                    // CHALK_TEST_POSTGRES named a server this fixture did not start, so the
                    // database is the developer's to provide and its own connection string is it.
                    return _database = server.ConnectionString!;
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _server?.Dispose();
            _server = null;
        }
    }
}

[CollectionDefinition(Name)]
public sealed class TypeMatrixCollection : ICollectionFixture<SharedTypeMatrixPostgres>
{
    public const string Name = "type-matrix";
}

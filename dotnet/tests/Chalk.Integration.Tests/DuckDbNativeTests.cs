using Chalk.Catalog;
using Chalk.Ir;
using Chalk.Sources;
using Chalk.Sources.Ado;
using Chalk.Sources.DuckDb;
using Chalk.TestKit;
using DuckDB.NET.Data;
using ArrowSchema = Apache.Arrow.Schema;

namespace Chalk.Integration.Tests;

/// <summary>
/// The native DuckDB reader (D148, <c>docs/design/24-zero-gc.md</c> §6 and §9): every type of the
/// table, NULLs, strings on both sides of the twelve inlined bytes, every decimal storage width,
/// every timestamp unit, the fallback for a type outside the table, cancellation through
/// <c>duckdb_interrupt</c>, and error attribution.
/// </summary>
/// <remarks>
/// Driven through <c>AdoSource.ExecuteQueryAsync</c> rather than through the engine, because what is
/// under test is the reader and not the plan. The corpora run over the same reader — the fixture's
/// DuckDB sources use it — which is where the answers are compared against the other two copies.
/// </remarks>
public sealed class DuckDbNativeTests : IDisposable
{
    private readonly DuckDbFile _file = DuckDbFile.Create("native");
    private readonly DuckDBConnection _keepAlive;

    public DuckDbNativeTests()
    {
        Assert.SkipWhen(RemoteFixture.SkipReason is not null, RemoteFixture.SkipReason ?? string.Empty);
        _keepAlive = new DuckDBConnection(_file.ConnectionString);
        _keepAlive.Open();
    }

    public void Dispose()
    {
        _keepAlive.Dispose();
        _file.Dispose();
    }

    private void Execute(string sql)
    {
        using var command = _keepAlive.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Every DuckDB type §6 names, with a value row and a NULL row, read natively and compared
    /// against the same query on the <c>DbDataReader</c> path — the two must agree exactly.
    /// </summary>
    [Fact]
    public async Task Every_type_reads_the_same_natively_as_through_the_provider()
    {
        Execute("""
            CREATE TABLE m AS SELECT * FROM (VALUES
              (true, CAST(-3 AS TINYINT), CAST(-300 AS SMALLINT), CAST(-70000 AS INTEGER),
               CAST(-5000000000 AS BIGINT), CAST(3 AS UTINYINT), CAST(300 AS USMALLINT),
               CAST(70000 AS UINTEGER), CAST(5000000000 AS UBIGINT),
               CAST(1.5 AS FLOAT), CAST(2.5 AS DOUBLE),
               DATE '2020-03-04', TIME '12:34:56.789012',
               TIMESTAMP '2020-03-04 12:34:56.789012',
               'short', 'a string longer than twelve bytes',
               BLOB 'ab', CAST('550e8400-e29b-41d4-a716-446655440000' AS UUID)),
              (NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL,
               NULL, NULL, NULL, NULL, NULL, NULL, NULL)
            ) t(b, t1, s2, i4, i8, u1, u2, u4, u8, f4, f8, d, tm, ts, v1, v2, bl, uu)
            """);

        await AssertPathsAgreeAsync(
            "SELECT b, t1, s2, i4, i8, u1, u2, u4, u8, f4, f8, d, tm, ts, v1, v2, uu FROM m",
            [
                Column("b", ChalkType.Bool(nullable: true)),
                Column("t1", ChalkType.Int8(nullable: true)),
                Column("s2", ChalkType.Int16(nullable: true)),
                Column("i4", ChalkType.Int32(nullable: true)),
                Column("i8", ChalkType.Int64(nullable: true)),
                Column("u1", ChalkType.Int16(nullable: true)),
                Column("u2", ChalkType.Int32(nullable: true)),
                Column("u4", ChalkType.Int64(nullable: true)),
                Column("u8", ChalkType.Int64(nullable: true)),
                Column("f4", ChalkType.Float32(nullable: true)),
                Column("f8", ChalkType.Float64(nullable: true)),
                Column("d", ChalkType.Date(nullable: true)),
                Column("tm", ChalkType.Time(6, nullable: true)),
                Column("ts", ChalkType.Timestamp(6, nullable: true)),
                Column("v1", ChalkType.String(nullable: true)),
                Column("v2", ChalkType.String(nullable: true)),
                Column("uu", ChalkType.Uuid(nullable: true)),
            ]).ConfigureAwait(true);

        // BLOB is not in the comparison because the provider's own reader cannot read one: DuckDB.NET
        // hands a BLOB back as an UnmanagedMemoryStream, which AdoTypeMapping has never accepted for
        // BINARY (V45). The native reader copies the bytes, so it is asserted on its own.
        var blobs = await ReadAsync(
            "SELECT bl FROM m",
            [Column("bl", ChalkType.Binary(nullable: true))],
            native: true).ConfigureAwait(true);
        Assert.Equal("ab"u8.ToArray(), (byte[]?)blobs[0][0]);
        Assert.Null(blobs[1][0]);
    }

    /// <summary>
    /// A <c>duckdb_string_t</c> is inlined at twelve bytes or fewer and a pointer beyond, which is
    /// the one layout fact the reader has to get right. Both sides of the boundary, the boundary
    /// itself, the empty string, and a multi-byte value whose bytes cross it.
    /// </summary>
    [Fact]
    public async Task Strings_on_both_sides_of_the_twelve_byte_inline_boundary()
    {
        Execute("""
            CREATE TABLE s AS SELECT * FROM (VALUES
              (1, ''), (2, 'a'), (3, 'exactlytwelv'), (4, 'thirteenchars'),
              (5, 'ÉÉÉÉÉÉ'), (6, 'ÉÉÉÉÉÉÉ'), (7, NULL), (8, '日経平均株価')
            ) t(k, v)
            """);

        var rows = await ReadAsync(
            "SELECT k, v FROM s ORDER BY k",
            [Column("k", ChalkType.Int32()), Column("v", ChalkType.String(nullable: true))],
            native: true).ConfigureAwait(true);

        Assert.Equal(
            ["", "a", "exactlytwelv", "thirteenchars", "ÉÉÉÉÉÉ", "ÉÉÉÉÉÉÉ", null, "日経平均株価"],
            rows.Select(r => (string?)r[1]));

        // Twelve bytes inlined, thirteen not; six É are twelve bytes and seven are fourteen.
        Assert.Equal(12, System.Text.Encoding.UTF8.GetByteCount("exactlytwelv"));
        Assert.Equal(12, System.Text.Encoding.UTF8.GetByteCount("ÉÉÉÉÉÉ"));
        Assert.Equal(14, System.Text.Encoding.UTF8.GetByteCount("ÉÉÉÉÉÉÉ"));
    }

    /// <summary>
    /// A DECIMAL's unscaled value lives in an INT16, INT32, INT64 or HUGEINT by its declared width,
    /// and each has to be sign-extended into Arrow's sixteen bytes. Negative values included,
    /// because sign extension is the half that is easy to get wrong.
    /// </summary>
    [Fact]
    public async Task Every_decimal_storage_width_including_hugeint()
    {
        Execute("""
            CREATE TABLE d AS SELECT * FROM (VALUES
              (1, CAST(1.23 AS DECIMAL(4,2)), CAST(1.23 AS DECIMAL(9,2)),
                  CAST(1.23 AS DECIMAL(18,2)), CAST(1.23 AS DECIMAL(30,2))),
              (2, CAST(-1.23 AS DECIMAL(4,2)), CAST(-1.23 AS DECIMAL(9,2)),
                  CAST(-1.23 AS DECIMAL(18,2)), CAST(-1.23 AS DECIMAL(30,2))),
              (3, NULL, NULL, NULL, NULL)
            ) t(k, d4, d9, d18, d30)
            """);

        await AssertPathsAgreeAsync(
            "SELECT k, d4, d9, d18, d30 FROM d ORDER BY k",
            [
                Column("k", ChalkType.Int32()),
                Column("d4", ChalkType.Decimal(4, 2, nullable: true)),
                Column("d9", ChalkType.Decimal(9, 2, nullable: true)),
                Column("d18", ChalkType.Decimal(18, 2, nullable: true)),
                Column("d30", ChalkType.Decimal(30, 2, nullable: true)),
            ]).ConfigureAwait(true);
    }

    /// <summary>
    /// The four timestamp types, each in its own unit, read into a column declared at a different
    /// one: seconds, milliseconds, microseconds and nanoseconds all have to land on the same instant.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(9)]
    public async Task Every_timestamp_unit_lands_on_the_same_instant(int precision)
    {
        Execute("""
            CREATE TABLE ts AS SELECT * FROM (VALUES
              (1, CAST(TIMESTAMP '2020-03-04 12:34:56' AS TIMESTAMP_S),
                  CAST(TIMESTAMP '2020-03-04 12:34:56' AS TIMESTAMP_MS),
                  CAST(TIMESTAMP '2020-03-04 12:34:56' AS TIMESTAMP),
                  CAST(TIMESTAMP '2020-03-04 12:34:56' AS TIMESTAMP_NS),
                  TIMESTAMPTZ '2020-03-04 12:34:56+00')
            ) t(k, s, ms, us, ns, tz)
            """);

        var columns = new[]
        {
            Column("k", ChalkType.Int32()),
            Column("s", ChalkType.Timestamp(precision, nullable: true)),
            Column("ms", ChalkType.Timestamp(precision, nullable: true)),
            Column("us", ChalkType.Timestamp(precision, nullable: true)),
            Column("ns", ChalkType.Timestamp(precision, nullable: true)),
            Column("tz", ChalkType.TimestampTz(precision, nullable: true)),
        };

        // The storage lanes rather than the host types: a TIMESTAMP is a DateTime and a TIMESTAMPTZ
        // a DateTimeOffset, and what is under test is that all five carry the same number of the
        // declared unit.
        var rows = await ReadAsync(
            "SELECT k, s, ms, us, ns, tz FROM ts", columns, native: true, storage: true)
            .ConfigureAwait(true);
        var row = rows.Single();
        var expected = row[1];
        Assert.All(row[1..], value => Assert.Equal(expected, value));

        await AssertPathsAgreeAsync("SELECT k, s, ms, us, ns, tz FROM ts", columns)
            .ConfigureAwait(true);
    }

    /// <summary>
    /// A column whose logical type is outside §6's table sends the whole query to the provider's own
    /// reader, and <c>Stats.SourcePaths</c> names both — decided before the first chunk, so the rows
    /// are read once, not twice.
    /// </summary>
    [Fact]
    public async Task A_LIST_column_falls_back_to_the_provider_and_says_so()
    {
        Execute("CREATE TABLE l AS SELECT 1 AS k, [1, 2, 3] AS xs");

        var stats = new ExecutionStats();
        var rows = await ReadAsync(
            "SELECT k FROM l",
            [Column("k", ChalkType.Int32())],
            native: true,
            stats: stats).ConfigureAwait(true);
        Assert.Single(rows);
        Assert.Equal("duckdb-native", stats.SourcePaths["duck"]);

        // A LIST in the *result* is what the fallback is about, not one in the table. The query
        // then runs on the provider's own reader — which is where it meets that reader's own limit,
        // since AdoTypeMapping has never accepted a List<> for a LIST column (V45). The refusal is
        // the proof the fallback happened: the native reader would not have produced that message.
        var listStats = new ExecutionStats();
        var failure = await Assert.ThrowsAsync<SourceContractException>(
            () => ReadAsync(
                "SELECT k, xs FROM l",
                [
                    Column("k", ChalkType.Int32()),
                    Column("xs", ChalkType.List(ChalkType.Int32(nullable: true), nullable: true)),
                ],
                native: true,
                stats: listStats)).ConfigureAwait(true);

        Assert.Equal("duckdb-native+DbDataReader", listStats.SourcePaths["duck"]);
        Assert.Contains("column 'xs'", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An unsigned value that does not fit the declared width is refused rather than truncated —
    /// the copy is <c>checked</c>, and the failure is attributed to the source like any other.
    /// </summary>
    [Fact]
    public async Task A_value_too_wide_for_the_declared_type_is_refused()
    {
        Execute("CREATE TABLE w AS SELECT CAST(70000 AS UINTEGER) AS u");

        var failure = await Assert.ThrowsAsync<SourceExecutionException>(
            () => ReadAsync(
                "SELECT u FROM w",
                [Column("u", ChalkType.Int16())],
                native: true)).ConfigureAwait(true);
        Assert.Equal("duck", failure.SourceId);
        Assert.IsType<OverflowException>(failure.InnerException);
    }

    /// <summary>
    /// A query DuckDB refuses is one attributable error naming the source and the query, exactly as
    /// the <c>DbDataReader</c> path attributes one (§6).
    /// </summary>
    [Fact]
    public async Task A_query_DuckDB_refuses_is_attributed_to_the_source()
    {
        var failure = await Assert.ThrowsAsync<SourceExecutionException>(
            () => ReadAsync(
                "SELECT * FROM no_such_table",
                [Column("k", ChalkType.Int32())],
                native: true)).ConfigureAwait(true);

        Assert.Equal("duck", failure.SourceId);
        Assert.Contains("DuckDB could not prepare", failure.InnerException?.Message ?? string.Empty,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Cancellation reaches a fetch already inside the native call: <c>duckdb_interrupt</c> is
    /// registered on the token, and a query that would otherwise run for a while stops.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Enumerated on a task, because the native fetch is synchronous: <c>duckdb_stream_fetch_chunk</c>
    /// blocks until DuckDB has a chunk, so a caller that pulls it on its own thread blocks with it.
    /// That is what the engine's prefetch task is for (D107), and this is the engine's shape.
    /// </para>
    /// <para>
    /// No timing assertion: what is asserted is that the enumeration ended in a cancellation rather
    /// than in rows. The wait has a bound only so that a broken interrupt fails the run instead of
    /// hanging it, and the bound is far longer than the interrupt needs.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task An_interrupt_stops_a_query_that_is_already_running()
    {
        using var cancellation = new CancellationTokenSource();
        var source = Source(native: true);
        using var arena = new ExecutionArena();
        var columns = new[] { Column("n", ChalkType.Int64()) };

        // Uninterrupted this counts ten million md5s, which takes long enough that the cancellation
        // lands while the fetch is inside the native call.
        var batches = source.ExecuteQueryAsync(
            Request(
                "SELECT count(*) AS n FROM range(200000) a, range(50) b "
                + "WHERE md5(CAST(a.range * b.range AS VARCHAR)) LIKE '%abcdef%'",
                columns),
            new ScanContext { Stats = new ExecutionStats(), Arena = arena },
            cancellation.Token).GetAsyncEnumerator(cancellation.Token);

        var pending = Task.Run(async () => await batches.MoveNextAsync().ConfigureAwait(false));
        await cancellation.CancelAsync().ConfigureAwait(true);

        var failure = await Record.ExceptionAsync(
            () => pending.WaitAsync(TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken))
            .ConfigureAwait(true);

        Assert.NotNull(failure);
        Assert.True(
            failure is OperationCanceledException or SourceExecutionException,
            $"an interrupted query ended in {failure.GetType().Name}: {failure.Message}");
        await batches.DisposeAsync().ConfigureAwait(true);
    }

    /// <summary>Both paths, the same query, the same answers — cell for cell.</summary>
    private async Task AssertPathsAgreeAsync(string sql, IReadOnlyList<ColumnDescriptor> columns)
    {
        var nativeStats = new ExecutionStats();
        var providerStats = new ExecutionStats();
        var native = await ReadAsync(sql, columns, native: true, stats: nativeStats)
            .ConfigureAwait(true);
        var provider = await ReadAsync(sql, columns, native: false, stats: providerStats)
            .ConfigureAwait(true);

        Assert.Equal("duckdb-native", nativeStats.SourcePaths["duck"]);
        Assert.Equal("DbDataReader", providerStats.SourcePaths["duck"]);
        Assert.Equal(provider.Count, native.Count);
        for (var row = 0; row < provider.Count; row++)
        {
            for (var column = 0; column < columns.Count; column++)
            {
                Assert.Equal(
                    Describe(provider[row][column]),
                    Describe(native[row][column]));
            }
        }
    }

    private static string Describe(object? value) => value switch
    {
        null => "NULL",
        byte[] bytes => Convert.ToHexString(bytes),
        IEnumerable<object?> list => "[" + string.Join(", ", list.Select(Describe)) + "]",
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
    };

    private async Task<List<object?[]>> ReadAsync(
        string sql,
        IReadOnlyList<ColumnDescriptor> columns,
        bool native,
        ExecutionStats? stats = null,
        bool storage = false)
    {
        var source = Source(native);
        using var arena = new ExecutionArena();
        var context = new ScanContext { Stats = stats ?? new ExecutionStats(), Arena = arena };

        var rows = new List<object?[]>();
        await foreach (var batch in source.ExecuteQueryAsync(
            Request(sql, columns), context, TestContext.Current.CancellationToken))
        {
            using (batch)
            {
                rows.AddRange(storage
                    ? BatchReader.ToStorageRows([batch])
                    : BatchReader.ToRows(batch));
            }
        }

        return rows;
    }

    private AdoSource Source(bool native)
    {
        var builder = new AdoSourceBuilder(
                "duck", () => new DuckDBConnection(_file.ConnectionString), "duck")
            .Dialect(DialectProfiles.DuckDb)
            .Capabilities(AdoCapabilities.For(DialectProfiles.DuckDb, maxInList: 1000))
            .AddTable("probe", [Column("k", ChalkType.Int32())], remoteName: "probe");
        if (native)
        {
            builder.UseNativeReader();
        }

        return builder.Build();
    }

    private static RemoteQueryRequest Request(string sql, IReadOnlyList<ColumnDescriptor> columns) =>
        new()
        {
            QueryText = sql,
            PushedPlan = new Rel(),
            Parameters = [],
            ParameterTypes = [],
            OutputSchema = Schema(columns),
            BatchSize = 1024,
            Dialect = "duckdb",
        };

    private static ArrowSchema Schema(IReadOnlyList<ColumnDescriptor> columns)
    {
        var builder = new ArrowSchema.Builder();
        foreach (var column in columns)
        {
            builder.Field(new Apache.Arrow.Field(
                column.Name, ArrowTypeMapping.ToArrow(column.Type), column.Type.Nullable));
        }

        return builder.Build();
    }

    private static ColumnDescriptor Column(string name, ChalkType type) =>
        new() { Name = name, Type = type };
}

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
/// The Arrow-export DuckDB reader (D148a, <c>docs/design/25-coverage-graft.md</c> §0a): the same
/// answers as the provider's own reader, the two types the copier cannot read read natively, the
/// fallback when DuckDB's exported type is not the declared one, parameters, and cancellation.
/// </summary>
/// <remarks>
/// The sibling of <see cref="DuckDbNativeTests"/> and deliberately its shape: the same fixture, the
/// same helpers, the same comparison against <c>DbDataReaderFetch</c>. What differs is which reader
/// is under test and what each can do — the copier converts widths and units and refuses composite
/// types; the export converts nothing and reads everything DuckDB can export.
/// </remarks>
public sealed class DuckDbArrowTests : IDisposable
{
    private readonly DuckDbFile _file = DuckDbFile.Create("arrow");
    private readonly DuckDBConnection _keepAlive;

    public DuckDbArrowTests()
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
    /// Every type whose exported Arrow form <em>is</em> the declared one, with a value row and a NULL
    /// row, read through the export and compared against the same query on the <c>DbDataReader</c>
    /// path.
    /// </summary>
    [Fact]
    public async Task Every_exported_type_reads_the_same_as_through_the_provider()
    {
        Execute("""
            CREATE TABLE m AS SELECT * FROM (VALUES
              (true, CAST(-3 AS TINYINT), CAST(-300 AS SMALLINT), CAST(-70000 AS INTEGER),
               CAST(-5000000000 AS BIGINT), CAST(1.5 AS FLOAT), CAST(2.5 AS DOUBLE),
               DATE '2020-03-04', TIME '12:34:56.789012',
               TIMESTAMP '2020-03-04 12:34:56.789012',
               'short', 'a string longer than twelve bytes',
               CAST(12.3456 AS DECIMAL(18, 4))),
              (NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL)
            ) t(b, t1, s2, i4, i8, f4, f8, d, tm, ts, v1, v2, dec)
            """);

        await AssertPathsAgreeAsync(
            "SELECT b, t1, s2, i4, i8, f4, f8, d, tm, ts, v1, v2, dec FROM m",
            [
                Column("b", ChalkType.Bool(nullable: true)),
                Column("t1", ChalkType.Int8(nullable: true)),
                Column("s2", ChalkType.Int16(nullable: true)),
                Column("i4", ChalkType.Int32(nullable: true)),
                Column("i8", ChalkType.Int64(nullable: true)),
                Column("f4", ChalkType.Float32(nullable: true)),
                Column("f8", ChalkType.Float64(nullable: true)),
                Column("d", ChalkType.Date(nullable: true)),
                Column("tm", ChalkType.Time(6, nullable: true)),
                Column("ts", ChalkType.Timestamp(6, nullable: true)),
                Column("v1", ChalkType.String(nullable: true)),
                Column("v2", ChalkType.String(nullable: true)),
                Column("dec", ChalkType.Decimal(18, 4, nullable: true)),
            ]).ConfigureAwait(true);
    }

    /// <summary>
    /// The two the copier has to hand to the provider — a LIST, and a BLOB the provider cannot read
    /// either (V45, F24) — come back on the export with no fallback at all.
    /// </summary>
    [Fact]
    public async Task A_LIST_and_a_BLOB_read_natively_with_no_fallback()
    {
        Execute("CREATE TABLE l AS SELECT 1 AS k, [10, 20, 30] AS xs, BLOB 'ab' AS bl");

        var stats = new ExecutionStats();
        var rows = await ReadAsync(
            "SELECT k, xs, bl FROM l",
            [
                Column("k", ChalkType.Int32()),
                Column("xs", ChalkType.List(ChalkType.Int32(nullable: true), nullable: true)),
                Column("bl", ChalkType.Binary(nullable: true)),
            ],
            stats: stats).ConfigureAwait(true);

        Assert.Equal("duckdb-arrow", stats.SourcePaths["duck"]);
        Assert.Single(rows);
        Assert.Equal(1, rows[0][0]);
        Assert.Equal("[10, 20, 30]", Describe(rows[0][1]));
        Assert.Equal("ab"u8.ToArray(), (byte[]?)rows[0][2]);
    }

    /// <summary>
    /// A declared type the export does not produce sends the whole query to the provider's reader and
    /// <c>Stats.SourcePaths</c> names both. DuckDB exports a TIMESTAMP in microseconds; a plan that
    /// declares TIMESTAMP(9) wants nanoseconds, which is exactly what discovery declares for a
    /// DuckDB TIMESTAMP today, so this is the common case and not a contrived one.
    /// </summary>
    [Fact]
    public async Task A_unit_the_export_does_not_produce_falls_back_and_says_so()
    {
        Execute("CREATE TABLE u AS SELECT TIMESTAMP '2020-03-04 12:34:56.789012' AS ts");

        var stats = new ExecutionStats();
        var rows = await ReadAsync(
            "SELECT ts FROM u",
            [Column("ts", ChalkType.Timestamp(9, nullable: true))],
            stats: stats).ConfigureAwait(true);

        Assert.Equal("duckdb-arrow+DbDataReader", stats.SourcePaths["duck"]);
        Assert.Single(rows);
        Assert.Equal(
            new DateTime(2020, 3, 4, 12, 34, 56, DateTimeKind.Unspecified).AddTicks(7890120),
            rows[0][0]);
    }

    /// <summary>
    /// Parameters go through the same prepared command the <c>DbDataReader</c> path uses, so the
    /// query means one thing on both paths.
    /// </summary>
    [Fact]
    public async Task A_parameter_binds_through_the_same_prepared_command()
    {
        Execute("CREATE TABLE p AS SELECT * FROM (VALUES (1, 'a'), (2, 'b'), (3, 'c')) t(k, v)");

        var source = Source();
        using var arena = new ExecutionArena();
        var columns = new[] { Column("k", ChalkType.Int32()), Column("v", ChalkType.String()) };
        var request = new RemoteQueryRequest
        {
            QueryText = "SELECT k, v FROM p WHERE k >= ? ORDER BY k",
            PushedPlan = new Rel(),
            Parameters = [2],
            ParameterTypes = [ChalkType.Int32()],
            OutputSchema = Schema(columns),
            BatchSize = 1024,
            Dialect = "duckdb",
        };

        var rows = new List<object?[]>();
        var stats = new ExecutionStats();
        await foreach (var batch in source.ExecuteQueryAsync(
            request,
            new ScanContext { Stats = stats, Arena = arena },
            TestContext.Current.CancellationToken))
        {
            using (batch)
            {
                rows.AddRange(BatchReader.ToRows(batch));
            }
        }

        Assert.Equal("duckdb-arrow", stats.SourcePaths["duck"]);
        Assert.Equal(2, rows.Count);
        Assert.Equal(2, rows[0][0]);
        Assert.Equal(3, rows[1][0]);
    }

    /// <summary>A query DuckDB refuses is one attributable error naming the source.</summary>
    [Fact]
    public async Task A_query_DuckDB_refuses_is_attributed_to_the_source()
    {
        var failure = await Assert.ThrowsAsync<SourceExecutionException>(
            () => ReadAsync("SELECT * FROM no_such_table", [Column("k", ChalkType.Int32())]))
            .ConfigureAwait(true);

        Assert.Equal("duck", failure.SourceId);
    }

    /// <summary>
    /// Cancellation reaches a query already running: the token is registered on the command, and the
    /// enumeration ends in a cancellation rather than in rows.
    /// </summary>
    /// <remarks>
    /// No timing assertion (the handoff rule): the bound on the wait exists only so a broken
    /// cancellation fails the run instead of hanging it, and it is far longer than the cancel needs.
    /// </remarks>
    [Fact]
    public async Task Cancellation_stops_a_query_that_is_already_running()
    {
        using var cancellation = new CancellationTokenSource();
        var source = Source();
        using var arena = new ExecutionArena();
        var columns = new[] { Column("n", ChalkType.Int64()) };

        var batches = source.ExecuteQueryAsync(
            new RemoteQueryRequest
            {
                QueryText = "SELECT count(*) AS n FROM range(200000) a, range(50) b "
                    + "WHERE md5(CAST(a.range * b.range AS VARCHAR)) LIKE '%abcdef%'",
                PushedPlan = new Rel(),
                Parameters = [],
                ParameterTypes = [],
                OutputSchema = Schema(columns),
                BatchSize = 1024,
                Dialect = "duckdb",
            },
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
            $"a cancelled query ended in {failure.GetType().Name}: {failure.Message}");
        await batches.DisposeAsync().ConfigureAwait(true);
    }

    /// <summary>Both paths, the same query, the same answers — cell for cell.</summary>
    private async Task AssertPathsAgreeAsync(string sql, IReadOnlyList<ColumnDescriptor> columns)
    {
        var arrowStats = new ExecutionStats();
        var providerStats = new ExecutionStats();
        var arrow = await ReadAsync(sql, columns, stats: arrowStats).ConfigureAwait(true);
        var provider = await ReadAsync(sql, columns, arrow: false, stats: providerStats)
            .ConfigureAwait(true);

        Assert.Equal("duckdb-arrow", arrowStats.SourcePaths["duck"]);
        Assert.Equal("DbDataReader", providerStats.SourcePaths["duck"]);
        Assert.Equal(provider.Count, arrow.Count);
        for (var row = 0; row < provider.Count; row++)
        {
            for (var column = 0; column < columns.Count; column++)
            {
                Assert.Equal(Describe(provider[row][column]), Describe(arrow[row][column]));
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
        bool arrow = true,
        ExecutionStats? stats = null)
    {
        var source = Source(arrow);
        using var arena = new ExecutionArena();
        var context = new ScanContext { Stats = stats ?? new ExecutionStats(), Arena = arena };

        var rows = new List<object?[]>();
        await foreach (var batch in source.ExecuteQueryAsync(
            Request(sql, columns), context, TestContext.Current.CancellationToken))
        {
            using (batch)
            {
                rows.AddRange(BatchReader.ToRows(batch));
            }
        }

        return rows;
    }

    private AdoSource Source(bool arrow = true)
    {
        var builder = new AdoSourceBuilder(
                "duck", () => new DuckDBConnection(_file.ConnectionString), "duck")
            .Dialect(DialectProfiles.DuckDb)
            .Capabilities(AdoCapabilities.For(DialectProfiles.DuckDb, maxInList: 1000))
            .AddTable("probe", [Column("k", ChalkType.Int32())], remoteName: "probe");
        if (arrow)
        {
            builder.UseArrowReader();
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

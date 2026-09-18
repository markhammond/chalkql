using Apache.Arrow;
using Chalk.Catalog;
using Chalk.Sources.DuckDb;
using Chalk.TestKit;
using DuckDB.NET.Data;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace Chalk.Sources.Ado.Tests;

/// <summary>
/// D263 (ADR 0046): the real drivers behind the reflection-based null check and the declared-versus-
/// measured text strategy, so the fake-provider tests are not the only evidence either mechanism
/// behaves as the design intends against a real driver.
/// </summary>
/// <remarks>
/// Shares <see cref="TypeMatrixCollection"/> with <see cref="AdoTypeMatrixTests"/> rather than
/// starting a second PostgreSQL server: both are the ephemeral one of D133 §0b, and a run that never
/// asks for one still pays nothing.
/// </remarks>
[Collection(TypeMatrixCollection.Name)]
public sealed class ProviderTraitsTests(SharedTypeMatrixPostgres postgres)
{
    /// <summary>
    /// SQLite is in-process and its row is fully materialised by the time <c>ReadAsync</c> returns
    /// (V44), which the reflection now discovers rather than assumes: <c>SqliteDataReader</c> does
    /// not override <c>IsDBNullAsync</c>, so it resolves to <see cref="System.Data.Common.DbDataReader"/>'s own.
    /// </summary>
    [Fact]
    public async Task SQLite_resolves_to_the_synchronous_null_check()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";
        await using var reader = await command
            .ExecuteReaderAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.True(AdoBatchReader.NullCheckIsSynchronous(reader.GetType()));
    }

    /// <summary>
    /// DuckDB is in-process too, and its provider's reader — as distinct from the native chunk and
    /// Arrow readers (D148, D148a) — answers the same way: no override, so the synchronous path.
    /// </summary>
    [Fact]
    public async Task DuckDB_resolves_to_the_synchronous_null_check()
    {
        await using var connection = new DuckDBConnection("DataSource=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";
        await using var reader = await command
            .ExecuteReaderAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.True(AdoBatchReader.NullCheckIsSynchronous(reader.GetType()));
    }

    /// <summary>
    /// Npgsql is the networked provider the mechanism exists for: under <c>SequentialAccess</c> a
    /// column may still be on the wire when it is asked, so <c>NpgsqlDataReader</c> overrides
    /// <c>IsDBNullAsync</c> with its own, and the reflection must see that override and keep the
    /// asynchronous call. Skipped, not failed, where <c>docs/handoff-m1.md</c>'s environment has no
    /// server — required instead when <c>CHALK_TEST_POSTGRES_REQUIRED=1</c> (D133 §0b).
    /// </summary>
    [Fact]
    public async Task PostgreSQL_resolves_to_the_asynchronous_null_check_when_the_fixture_is_available()
    {
        var fixture = postgres.Fixture;
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? string.Empty);

        var connectionString = fixture.CreateDatabase("chalk_d263_nullcheck");
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";
        await using var reader = await command
            .ExecuteReaderAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.False(AdoBatchReader.NullCheckIsSynchronous(reader.GetType()));
    }

    private static readonly ColumnDescriptor[] TextColumn =
        [new() { Name = "v", Type = ChalkType.String(nullable: true) }];

    /// <summary>
    /// D263 (ADR 0046 §2, §4): <c>Chalk.Sources.DuckDb</c> declares its trait on the underlying
    /// <see cref="AdoSourceBuilder"/>, and it reaches the reader even when the fetch is forced onto
    /// the plain <c>DbDataReader</c> path — the one both <c>UseNativeReader</c>'s and
    /// <c>UseArrowReader</c>'s own fallback describe, over a real <c>DuckDBDataReader</c> — so the
    /// declared answer is used and no measurement runs at all.
    /// </summary>
    [Fact]
    public async Task DuckDBs_declared_trait_reaches_the_reader_over_the_DbDataReader_path()
    {
        using var file = DuckDbFile.Create("d263-traits");
        await using (var keepAlive = new DuckDBConnection(file.ConnectionString))
        {
            await keepAlive.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            await using var seed = keepAlive.CreateCommand();
            seed.CommandText = "CREATE TABLE t(v VARCHAR); INSERT INTO t VALUES ('alpha');";
            await seed.ExecuteNonQueryAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        var source = DuckDbSources
            .AddDuckDbSource("duck", () => new DuckDBConnection(file.ConnectionString), "main")
            // Forces the exact fallback shape: a real DuckDBDataReader read through AdoBatchReader,
            // never through the native chunk copier or the Arrow export.
            .Fetch(DbDataReaderFetch.Instance)
            .AddTable("t", TextColumn)
            .Build();

        AdoBatchReader.ForgetMeasuredStrategies();
        var before = AdoBatchReader.MeasurementRuns;

        using var arena = new ExecutionArena();
        var request = new ScanRequest
        {
            Table = "t",
            Projection = [0],
            OutputSchema = ArrowTypeMapping.ToArrowSchema(TextColumn),
            BatchSize = 8,
        };
        var context = new ScanContext { Stats = new ExecutionStats(), Arena = arena };

        var values = new List<string?>();
        await foreach (var batch in source.ScanAsync(
            request, context, TestContext.Current.CancellationToken))
        {
            using (batch)
            {
                var column = (StringArray)batch.Column(0);
                for (var i = 0; i < batch.Length; i++)
                {
                    values.Add(column.GetString(i));
                }
            }
        }

        Assert.Equal(["alpha"], values);
        Assert.Equal(before, AdoBatchReader.MeasurementRuns);
    }

    /// <summary>
    /// D263 (ADR 0046 §3): the conformance kit's report line for the one column the kit's own
    /// dataset has (<c>ConformanceDataset</c> has two, but this source only registers one, which is
    /// enough to prove the mechanism), against real SQLite — undeclared, so it is measured — and
    /// real DuckDB — declared by its package, so it is not.
    /// </summary>
    [Fact]
    public async Task The_conformance_kit_reports_the_declared_and_the_measured_strategy()
    {
        // SQLite: undeclared, so the report names a measurement. A named, shared-cache in-memory
        // database, kept alive by one connection, so the source's own connection (opened inside
        // ProbeTextStrategiesAsync) sees the same table the seed just wrote.
        const string ConnectionString = "Data Source=chalk_d263_report;Mode=Memory;Cache=Shared";
        await using var sqliteKeepAlive = new SqliteConnection(ConnectionString);
        await sqliteKeepAlive.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        await using (var seed = sqliteKeepAlive.CreateCommand())
        {
            seed.CommandText = "CREATE TABLE t(v TEXT); INSERT INTO t VALUES ('alpha');";
            await seed.ExecuteNonQueryAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        var sqliteSource = new AdoSourceBuilder("sqlite", () => new SqliteConnection(ConnectionString), "main")
            .Dialect(DialectProfiles.Sqlite)
            .AddTable("t", TextColumn)
            .Build();

        var sqliteReports = await sqliteSource
            .ProbeTextStrategiesAsync("t", TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        var sqliteReport = Assert.Single(sqliteReports);
        Assert.False(sqliteReport.Declared);
        Assert.Equal(TextStrategy.String, sqliteReport.Strategy);

        // DuckDB: declared by its own package.
        using var file = DuckDbFile.Create("d263-report");
        await using (var keepAlive = new DuckDBConnection(file.ConnectionString))
        {
            await keepAlive.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            await using var seed = keepAlive.CreateCommand();
            seed.CommandText = "CREATE TABLE t(v VARCHAR); INSERT INTO t VALUES ('alpha');";
            await seed.ExecuteNonQueryAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        var duckSource = DuckDbSources
            .AddDuckDbSource("duck", () => new DuckDBConnection(file.ConnectionString), "main")
            .Fetch(DbDataReaderFetch.Instance)
            .AddTable("t", TextColumn)
            .Build();

        var duckReports = await duckSource
            .ProbeTextStrategiesAsync("t", TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        var duckReport = Assert.Single(duckReports);
        Assert.True(duckReport.Declared);
        Assert.Equal(TextStrategy.String, duckReport.Strategy);
    }
}

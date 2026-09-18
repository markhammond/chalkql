using System.Data.Common;
using Chalk.Catalog;
using Chalk.Ir;
using Chalk.Sources;
using Chalk.Sources.Ado;
using Chalk.Sources.Conformance;
using Chalk.TestKit;
using DuckDB.NET.Data;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace Chalk.Integration.Tests;

/// <summary>
/// The conformance kit against the in-box source, on all three dialects, and against a descriptor
/// that lies (D88, rev 3's M4 exit criterion).
/// </summary>
/// <remarks>
/// The reports are checked in under <c>corpus/conformance/</c>, one per dialect. They are the
/// evidence for the exit criterion, and they are a diff a reviewer can read: a probe that starts
/// observing something different shows up as a changed line rather than as a passing test — which is
/// why the report names the server version it ran against (D133).
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class ConformanceKitTests : IDisposable
{
    private readonly SharedPostgres _postgres;
    private readonly List<DuckDbFile> _files = [];

    public ConformanceKitTests(SharedPostgres postgres) => _postgres = postgres;

    private static DirectoryInfo Reports =>
        new(Path.Combine(RepoLayout.Corpus.FullName, "conformance"));

    /// <summary>
    /// The in-box ADO.NET source passes the kit on SQLite, on DuckDB and on PostgreSQL. The third is
    /// an ephemeral server this run starts itself (D133): no container, no service, no network. When
    /// no PostgreSQL binaries can be found the case skips with a reason that names what to install,
    /// and <c>CHALK_TEST_POSTGRES_REQUIRED=1</c> — which CI sets — makes that skip a failure.
    /// </summary>
    [Theory]
    [MemberData(nameof(Dialects))]
    public async Task The_in_box_source_passes_the_kit(string dialect)
    {
        Assert.SkipWhen(RemoteFixture.SkipReason is not null, RemoteFixture.SkipReason ?? string.Empty);
        SkipWithoutPostgres(dialect);

        var (source, _) = Build(dialect, honest: true);
        var report = await SourceConformance.RunAsync(
            source, SeedingOptions(dialect), TestContext.Current.CancellationToken);

        await RecordAsync(dialect, report, TestContext.Current.CancellationToken);
        Assert.True(report.Passed, report.ToString());

        // A run that skipped everything would also "pass"; the point is that the checks ran.
        Assert.True(
            report.Findings.Count(f => f.Outcome == ConformanceOutcome.Pass) >= 15,
            $"only {report.Findings.Count} checks ran:\n{report}");
    }

    /// <summary>
    /// Rev 3's deliberately-lying adapter, exactly as the design describes it: the SQLite preset —
    /// which claims <c>StringCollation.Binary</c> — over a table whose text column is declared
    /// <c>COLLATE NOCASE</c>. The descriptor is honest about the engine and wrong about this
    /// database, which is the failure mode a manual cannot catch.
    /// </summary>
    [Fact]
    public async Task The_kit_catches_a_descriptor_that_claims_a_binary_collation_it_does_not_have()
    {
        Assert.SkipWhen(RemoteFixture.SkipReason is not null, RemoteFixture.SkipReason ?? string.Empty);

        var (source, _) = Build("sqlite", honest: false);
        var failure = await Assert.ThrowsAsync<ConformanceException>(
            () => SourceConformance.VerifyAsync(
                source, SeedingOptions("sqlite", noCase: true), TestContext.Current.CancellationToken));

        Assert.False(failure.Report.Passed);
        var collation = failure.Report.Findings.Single(f => f.Subject == "string collation");
        Assert.Equal(ConformanceOutcome.Fail, collation.Outcome);
        Assert.Contains("StringCollation.Binary", collation.Declared, StringComparison.Ordinal);
        Assert.Contains("ignores case", collation.Observed, StringComparison.Ordinal);
    }

    /// <summary>
    /// F27: a source whose division of two integers is real division is caught, and it is caught by
    /// the type as well as the value.
    /// </summary>
    /// <remarks>
    /// The database is DuckDB and the profile is DuckDB's in every respect but its <em>name</em>,
    /// which is what makes the probe write <c>/</c> rather than the <c>//</c> Chalk's DuckDB
    /// dialect generates (ADR 0027) — the position every source outside the three presets is in.
    /// This is the exact run that used to pass: the old probe cast the result to DOUBLE PRECISION
    /// and accepted any text beginning <c>-2</c>, so <c>-2.3333333333333335</c> read as
    /// "truncates towards zero".
    /// </remarks>
    [Fact]
    public async Task The_kit_catches_a_source_whose_division_of_two_integers_is_real_division()
    {
        Assert.SkipWhen(RemoteFixture.SkipReason is not null, RemoteFixture.SkipReason ?? string.Empty);

        var (source, _) = Build("duckdb", honest: true, profileOverride: NotNamedDuckDb);
        var report = await SourceConformance.RunAsync(
            source,
            SeedingOptions("duckdb", checks: ConformanceChecks.Dialect),
            TestContext.Current.CancellationToken);

        Assert.False(report.Passed, report.ToString());
        var finding = report.Findings.Single(f => f.Subject == "integer division of a negative");
        Assert.Equal(ConformanceOutcome.Fail, finding.Outcome);
        Assert.Contains("-7 / 3 = -2.3333333333333335", finding.Observed, StringComparison.Ordinal);
        Assert.Contains("not in an integer type", finding.Observed, StringComparison.Ordinal);

        // The provider's own field type, which is what the old probe cast away.
        Assert.Contains("Double", finding.Observed, StringComparison.Ordinal);
        Assert.Contains("real division", finding.Advice, StringComparison.Ordinal);
    }

    /// <summary>
    /// DuckDB's profile under another name. Everything the kit reads from a profile is DuckDB's —
    /// quoting, casing, collation, NULL placement, precisions — except the preset name, which is
    /// what selects the dialect the planner generates with and therefore the operator the kit
    /// probes.
    /// </summary>
    private static DialectProfileDescriptor NotNamedDuckDb { get; } = new()
    {
        Dialect = "ansi",
        Quoting = DialectProfiles.DuckDb.Quoting,
        QuotedCasing = DialectProfiles.DuckDb.QuotedCasing,
        UnquotedCasing = DialectProfiles.DuckDb.UnquotedCasing,
        CaseSensitiveIdentifiers = DialectProfiles.DuckDb.CaseSensitiveIdentifiers,
        Conformance = DialectProfiles.DuckDb.Conformance,
        MaxNumericPrecision = DialectProfiles.DuckDb.MaxNumericPrecision,
        MaxTimestampPrecision = DialectProfiles.DuckDb.MaxTimestampPrecision,
        HasBoolean = DialectProfiles.DuckDb.HasBoolean,
        DefaultNullCollation = DialectProfiles.DuckDb.DefaultNullCollation,
        SupportsNullOrderingClause = DialectProfiles.DuckDb.SupportsNullOrderingClause,
        StringCollation = DialectProfiles.DuckDb.StringCollation,
        ParameterPlaceholder = DialectProfiles.DuckDb.ParameterPlaceholder,
    };

    /// <summary>
    /// F16(e): a unique key that is not unique is caught. Declared by hand, because no discovery
    /// would invent one — which is the point: this is the claim a host makes.
    /// </summary>
    [Fact]
    public async Task The_kit_catches_a_unique_key_that_is_not_unique()
    {
        Assert.SkipWhen(RemoteFixture.SkipReason is not null, RemoteFixture.SkipReason ?? string.Empty);

        // `text_other` holds four distinct values over sixteen rows, so declaring it unique is a lie.
        var (source, _) = Build("sqlite", honest: true, falseUniqueKey: "text_other");
        var report = await SourceConformance.RunAsync(
            source,
            SeedingOptions("sqlite", checks: ConformanceChecks.Uniqueness),
            TestContext.Current.CancellationToken);

        Assert.False(report.Passed);
        var finding = report.Findings.Single(f => f.Outcome == ConformanceOutcome.Fail);
        Assert.Contains("text_other", finding.Subject, StringComparison.Ordinal);
        Assert.Contains("share the key", finding.Observed, StringComparison.Ordinal);
    }

    /// <summary>
    /// All three dialects, always. PostgreSQL is a real case now rather than one that appears only
    /// when an environment variable is set: the fixture starts a server from binaries on the machine,
    /// and says what to install when it cannot.
    /// </summary>
    public static TheoryData<string> Dialects() => ["sqlite", "duckdb", "postgresql"];

    private void SkipWithoutPostgres(string dialect)
    {
        if (dialect != "postgresql")
        {
            return;
        }

        Assert.SkipWhen(
            _postgres.Fixture.SkipReason is not null, _postgres.Fixture.SkipReason ?? string.Empty);
    }

    /// <summary>What the report records for this run: a server version, or the engine's own build.</summary>
    private string ServerOf(string dialect) => dialect switch
    {
        "postgresql" => _postgres.Fixture.ServerVersion.Length > 0
            ? _postgres.Fixture.ServerVersion
            : "an existing server named by CHALK_TEST_POSTGRES",
        "sqlite" => "SQLite through Microsoft.Data.Sqlite, in-process",
        _ => "DuckDB through DuckDB.NET, in-process",
    };

    private static DialectProfileDescriptor Profile(string dialect) => dialect switch
    {
        "sqlite" => DialectProfiles.Sqlite,
        "duckdb" => DialectProfiles.DuckDb,
        "postgresql" => DialectProfiles.PostgreSql,
        _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "no preset"),
    };

    /// <summary>
    /// A source over a private in-memory database, with the kit's table created but not filled —
    /// the kit's own seed callback does that, which is the path an adapter author takes.
    /// </summary>
    private (AdoSource Source, DbConnection? KeepAlive) Build(
        string dialect,
        bool honest,
        string? falseUniqueKey = null,
        DialectProfileDescriptor? profileOverride = null)
    {
        var profile = profileOverride ?? Profile(dialect);
        var connectionString = dialect switch
        {
            "sqlite" => $"Data Source=chalk-kit-{Guid.NewGuid():N};Mode=Memory;Cache=Shared",

            // A file under the test temp root, not the working tree: ":memory:name" is a path to
            // DuckDB.NET 1.5.5, and it was writing a real database wherever the test process stood
            // (D133 §0c).
            "duckdb" => NewDuckDbFile().ConnectionString,
            _ => _postgres.Fixture.ConnectionString!,
        };
        DbConnection Connect() => dialect switch
        {
            "sqlite" => new SqliteConnection(connectionString),
            "duckdb" => new DuckDBConnection(connectionString),
            _ => new NpgsqlConnection(connectionString),
        };

        // The two in-memory engines drop the database when the last connection closes, so one is
        // held open for the life of the process. A real PostgreSQL needs no such thing.
        DbConnection? keepAlive = null;
        if (dialect is "sqlite" or "duckdb")
        {
            keepAlive = Connect();
            keepAlive.Open();
            Alive.Add(keepAlive);
        }

        var builder = new AdoSourceBuilder("kit", Connect, dialect)
            .Dialect(honest
                ? profile
                : profile.With(stringCollation: StringCollation.Binary))
            .Capabilities(AdoCapabilities.For(profile));

        // Registered by hand rather than discovered: the table does not exist yet — the kit's seed
        // callback creates it — and a false unique key is a claim no discovery would make.
        builder.AddTable(
            ConformanceDataset.DefaultTable,
            ConformanceDataset.Columns,
            configure: t =>
            {
                t.UniqueKey("id");
                if (falseUniqueKey is not null)
                {
                    t.UniqueKey(falseUniqueKey);
                }
            });

        return (builder.Build(), keepAlive);
    }

    /// <summary>
    /// The seed: create the table and insert the kit's rows, through the source's own connection.
    /// The <c>NOCASE</c> variant is the lying adapter's database — the same preset over a table
    /// whose text column compares case-insensitively.
    /// </summary>
    private ConformanceOptions SeedingOptions(
        string dialect, bool noCase = false, ConformanceChecks checks = ConformanceChecks.All) => new()
    {
        Checks = checks,
        Server = ServerOf(dialect),
        Seed = async (seed, ct) =>
        {
            var source = (AdoSource)seed.Source;

            // The in-memory engines start empty; a real PostgreSQL holds last run's table.
            await ExecuteAsync(source, $"DROP TABLE IF EXISTS \"{seed.Table}\"", ct)
                .ConfigureAwait(false);

            var create = noCase
                ? seed.CreateTable.Replace(
                    "\"text_value\" VARCHAR", "\"text_value\" VARCHAR COLLATE NOCASE", StringComparison.Ordinal)
                : seed.CreateTable;

            await ExecuteAsync(source, create, ct).ConfigureAwait(false);
            foreach (var insert in seed.Inserts)
            {
                await ExecuteAsync(source, insert, ct).ConfigureAwait(false);
            }
        },
    };

    /// <summary>
    /// One statement through the source itself. The kit hands an adapter the SQL and lets it decide
    /// how to run it; the in-box source's own query path is the obvious way, and it proves that path
    /// works for DDL too.
    /// </summary>
    private static async Task ExecuteAsync(AdoSource source, string sql, CancellationToken ct)
    {
        using var arena = new ExecutionArena();
        var request = new RemoteQueryRequest
        {
            QueryText = sql,
            PushedPlan = new Rel(),
            Parameters = [],
            ParameterTypes = [],
            OutputSchema = new Apache.Arrow.Schema([], null),
            BatchSize = 1,
        };

        await foreach (var batch in source.ExecuteQueryAsync(
            request, new ScanContext { Stats = new ExecutionStats(), Arena = arena }, ct))
        {
            batch.Dispose();
        }
    }

    private static async Task RecordAsync(string dialect, ConformanceReport report, CancellationToken ct)
    {
        Reports.Create();
        await File.WriteAllTextAsync(
            Path.Combine(Reports.FullName, dialect + ".txt"), report.ToString(), ct);
    }

    /// <summary>
    /// The connections that keep each private database alive for the life of the process. A test
    /// class cannot dispose them without closing the database the assertions still read.
    /// </summary>
    private static readonly List<DbConnection> Alive = [];

    private DuckDbFile NewDuckDbFile()
    {
        var file = DuckDbFile.Create("kit");
        _files.Add(file);
        return file;
    }

    /// <summary>
    /// Deletes this test's DuckDB files. The keep-alive connections outlive the class on purpose, so
    /// the file is unlinked while it is still open — which is what an operating system is for, and
    /// what keeps the working tree clean whichever order things shut down in.
    /// </summary>
    public void Dispose()
    {
        foreach (var file in _files)
        {
            file.Dispose();
        }

        _files.Clear();
    }
}

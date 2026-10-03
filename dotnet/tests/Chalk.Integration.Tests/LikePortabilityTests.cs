using System.Text;
using System.Text.RegularExpressions;
using Chalk.Catalog;
using Chalk.Client;
using Chalk.Client.Rpc;
using Chalk.Ir;
using Chalk.Sources;
using Chalk.Sources.Ado;
using Chalk.Sources.DuckDb;
using Chalk.Sources.Poco;
using Chalk.TestKit;
using DuckDB.NET.Data;
using Npgsql;

namespace Chalk.Integration.Tests;

/// <summary>
/// D312–D315: one LIKE, the same rows, wherever the table lives. The same eleven rows as a POCO
/// table, a POCO table with an ordered index on the text, a DuckDB table and a PostgreSQL one —
/// the last under the preset, which now pushes LIKE and writes <c>ESCAPE ''</c> on it — and every
/// statement below is held to the SQL standard's LIKE, read off the rows by a regular expression.
/// </summary>
/// <remarks>
/// The backslash case is the one that used to differ: PostgreSQL's LIKE takes a backslash as its
/// escape when no ESCAPE clause names one, and Chalk's has none. The malformed cases are refused the
/// same way everywhere — a literal at prepare, with its position; a parameter's value at execute,
/// before anything reaches a source.
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class LikePortabilityTests(SharedSidecar sidecar, SharedPostgres postgres)
{
    private sealed record Item(int Id, string Sku);

    private static readonly Item[] Rows =
    [
        new(1, "KB_1"), new(2, "KBx1"), new(3, "KB\\x1"), new(4, "100%"), new(5, "1000"),
        new(6, "é"), new(7, "😀"), new(8, "kb_1"), new(9, "a!b"), new(10, "ab"), new(11, "ab!"),
    ];

    private static readonly (string Pattern, string? Escape)[] Cases =
    [
        ("KB%", null),
        ("KB\\_%", null),
        ("KB\\_%", "\\"),
        ("KB!_%", "!"),
        ("100!%", "!"),
        ("100%", null),
        ("_", null),
        ("kb%", null),
        ("a!!b", "!"),
        ("%!%", "!"),
        ("", null),
    ];

    public static TheoryData<string, string?> Literals()
    {
        var data = new TheoryData<string, string?>();
        foreach (var (pattern, escape) in Cases)
        {
            data.Add(pattern, escape);
        }

        return data;
    }

    public static TheoryData<string> Backings() => ["poco", "poco-indexed", "duckdb", "postgresql"];

    [Theory]
    [MemberData(nameof(Literals))]
    public async Task A_literal_pattern_matches_the_same_rows_everywhere(string pattern, string? escape)
    {
        var sql = $"SELECT id FROM items WHERE sku LIKE '{pattern}'"
            + (escape is null ? string.Empty : $" ESCAPE '{escape}'");
        await foreach (var (name, engine) in EnginesAsync())
        {
            await using (engine)
            {
                Assert.True(
                    Expected(pattern, escape).SequenceEqual(await IdsAsync(engine, sql, [])),
                    $"{name}: {sql}");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Literals))]
    public async Task A_parameter_pattern_matches_the_same_rows_everywhere(string pattern, string? escape)
    {
        var sql = "SELECT id FROM items WHERE sku LIKE ?"
            + (escape is null ? string.Empty : $" ESCAPE '{escape}'");
        await foreach (var (name, engine) in EnginesAsync())
        {
            await using (engine)
            {
                Assert.True(
                    Expected(pattern, escape).SequenceEqual(await IdsAsync(engine, sql, [pattern])),
                    $"{name}: {sql} with '{pattern}'");
            }
        }
    }

    [Fact]
    public async Task A_negated_pattern_and_a_null_one_match_the_same_rows_everywhere()
    {
        await foreach (var (name, engine) in EnginesAsync())
        {
            await using (engine)
            {
                Assert.Equal(
                    [.. Rows.Select(r => r.Id).Where(id => id != 1)],
                    await IdsAsync(engine, "SELECT id FROM items WHERE sku NOT LIKE 'KB\\_%' ESCAPE '\\'", []));
                Assert.True(
                    (await IdsAsync(engine, "SELECT id FROM items WHERE sku LIKE ?", [null])).Count == 0,
                    $"{name}: LIKE NULL");
            }
        }
    }

    [Theory]
    [InlineData("SELECT id FROM items WHERE sku LIKE 'ab!' ESCAPE '!'")]
    [InlineData("SELECT id FROM items WHERE sku LIKE 'a!b' ESCAPE '!'")]
    [InlineData("SELECT id FROM items WHERE sku LIKE 'ab%' ESCAPE '!!'")]
    [InlineData("SELECT id FROM items WHERE sku LIKE 'ab%' ESCAPE ''")]
    public async Task A_malformed_literal_is_refused_at_prepare_everywhere(string sql)
    {
        await foreach (var (name, engine) in EnginesAsync())
        {
            await using (engine)
            {
                var error = await Assert.ThrowsAsync<PlanningException>(async () => await engine.PrepareAsync(sql));
                Assert.True(error.Kind == PlanErrorKinds.Validation, $"{name}: {error.Kind}");
                Assert.NotNull(error.Position);
            }
        }
    }

    [Theory]
    [InlineData("ab!")]
    [InlineData("a!b")]
    public async Task A_malformed_parameter_value_is_refused_at_execute_everywhere(string pattern)
    {
        await foreach (var (name, engine) in EnginesAsync())
        {
            await using (engine)
            {
                var query = await engine.PrepareAsync("SELECT id FROM items WHERE sku LIKE ? ESCAPE '!'");
                var error = await Assert.ThrowsAsync<ParameterBindingException>(
                    async () => await engine.ExecuteAsync(query, [pattern]));
                Assert.True(error.Code == ChalkErrorCodes.ParameterBinding, $"{name}: {error.Message}");
            }
        }
    }

    /// <summary>
    /// D316: ILIKE folds as LOWER does — the same rows everywhere, literal or parameter — and is never
    /// sent to a source: DuckDB folds a Turkish capital LOWER leaves alone, and PostgreSQL folds by its
    /// database's locale.
    /// </summary>
    [Theory]
    [InlineData("kb%", null)]
    [InlineData("KB!_%", "!")]
    [InlineData("\u00c9", null)]
    [InlineData("_", null)]
    [InlineData("AB", null)]
    [InlineData("A!!B", "!")]
    public async Task Ilike_matches_the_same_rows_everywhere_and_stays_here(string pattern, string? escape)
    {
        var suffix = escape is null ? string.Empty : $" ESCAPE '{escape}'";
        var expected = ExpectedIlike(pattern, escape);
        await foreach (var (name, engine) in EnginesAsync())
        {
            await using (engine)
            {
                // ILIKE is Chalk's own (D316): no library needed — and naming PostgreSQL's changes nothing.
                var literal = $"SELECT id FROM items WHERE sku ILIKE '{pattern}'{suffix}";
                Assert.True(expected.SequenceEqual(await IdsAsync(engine, literal, [])), $"{name}: {literal}");

                var parameter = $"SELECT id FROM items WHERE sku ILIKE ?{suffix}";
                Assert.True(
                    expected.SequenceEqual(await IdsAsync(engine, parameter, [pattern], Postgresql)),
                    $"{name}: {parameter} with '{pattern}'");

                var query = await engine.PrepareAsync(literal);
                if (PlanWalker.Has(query.Plan, Rel.KindOneofCase.RemoteQuery))
                {
                    Assert.True(PlanWalker.Has(query.Plan, Rel.KindOneofCase.Filter), $"{name}: ILIKE was pushed");
                }
            }
        }
    }

    private static readonly PrepareOptions Postgresql = new() { Libraries = [Chalk.Catalog.SqlLibrary.Postgresql] };

    /// <summary>
    /// D315: under the preset a LIKE is pushed to PostgreSQL — with <c>ESCAPE ''</c> when it names no
    /// escape — while a string equality, which the collation does decide, stays here.
    /// </summary>
    [Fact]
    public async Task Postgresql_is_sent_the_like_and_told_it_has_no_escape_character()
    {
        var engine = await PostgresEngineAsync(DatabaseAsync);
        await using (engine)
        {
            var like = await engine.PrepareAsync("SELECT id FROM items WHERE sku LIKE 'KB\\_%'");
            Assert.True(PlanWalker.Has(like.Plan, Rel.KindOneofCase.RemoteQuery));
            Assert.False(PlanWalker.Has(like.Plan, Rel.KindOneofCase.Filter));
            var sql = PlanWalker.Rels(like.Plan).Single(r => r.KindCase == Rel.KindOneofCase.RemoteQuery)
                .RemoteQuery.QueryText;
            Assert.Contains("ESCAPE ''", sql, StringComparison.Ordinal);

            var equality = await engine.PrepareAsync("SELECT id FROM items WHERE sku = 'KB_1'");
            Assert.True(PlanWalker.Has(equality.Plan, Rel.KindOneofCase.Filter));
        }
    }

    /// <summary>
    /// D315's claim itself: under a locale collation — which orders 'a' before 'B', as C does not —
    /// PostgreSQL's LIKE still matches code point by code point and case-sensitively. Skipped where
    /// the server has no en_US.UTF-8 locale to create the database with.
    /// </summary>
    [Fact]
    public async Task Postgresql_like_under_a_locale_collation_is_chalks()
    {
        var fixture = RequirePostgres();
        string connection;
        try
        {
            connection = await LocaleDatabaseAsync(fixture);
        }
        catch (PostgresException e)
        {
            Assert.Skip("no en_US.UTF-8 locale on this server: " + e.MessageText);
            return;
        }

        var engine = await PostgresEngineAsync(Task.FromResult(connection));
        await using (engine)
        {
            foreach (var (pattern, escape) in Cases)
            {
                var sql = $"SELECT id FROM items WHERE sku LIKE '{pattern}'"
                    + (escape is null ? string.Empty : $" ESCAPE '{escape}'");
                Assert.True(
                    Expected(pattern, escape).SequenceEqual(await IdsAsync(engine, sql, [])),
                    $"en_US.UTF-8: {sql}");
            }
        }
    }

    private async IAsyncEnumerable<(string Name, ChalkEngine Engine)> EnginesAsync()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        yield return ("poco", await EngineAsync(
            "poco", new PocoSourceBuilder("poco").NamingPolicy(PocoNamingPolicy.SnakeCase).AddTable("items", Rows).Build()));
        yield return ("poco-indexed", await EngineAsync(
            "pocoix",
            new PocoSourceBuilder("pocoix")
                .NamingPolicy(PocoNamingPolicy.SnakeCase)
                .AddTable("items", Rows, t => t.Index("ix_items_sku", IndexKind.Ordered, unique: false, x => x.Sku))
                .Build()));
        yield return ("duckdb", await EngineAsync(
            "duck",
            DuckDbSources.AddDuckDbSource("duck", "DataSource=" + await DuckFileAsync())
                .Capabilities(AdoCapabilities.For(DialectProfiles.DuckDb))
                .DiscoverTables()
                .Build()));
        if (postgres.Fixture.SkipReason is null)
        {
            yield return ("postgresql", await PostgresEngineAsync(DatabaseAsync));
        }
    }

    private async Task<ChalkEngine> EngineAsync(string id, ISourceRuntime source) =>
        await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "like-" + id,
            Sources = [source],
            Planner = sidecar.CreatePlanner(),
        });

    private async Task<ChalkEngine> PostgresEngineAsync(Task<string> connection) =>
        await EngineAsync(
            "pg",
            new AdoSourceBuilder("pg", NpgsqlFactory.Instance, await connection)
                .Dialect(DialectProfiles.PostgreSql)
                .Capabilities(AdoCapabilities.For(DialectProfiles.PostgreSql))
                .DiscoverTables("public")
                .Build());

    private static async Task<List<int>> IdsAsync(
        ChalkEngine engine, string sql, IReadOnlyList<object?> parameters, PrepareOptions? options = null)
    {
        var query = options is null ? await engine.PrepareAsync(sql) : await engine.PrepareAsync(sql, options);
        await using var execution = await engine.ExecuteAsync(query, parameters);
        var ids = new List<int>();
        await foreach (var batch in execution.Batches)
        {
            using (batch)
            {
                ids.AddRange(Chalk.Arrow.RecordBatchExtensions.ToRows(batch).Select(r => Convert.ToInt32(r[0])));
            }
        }

        ids.Sort();
        return ids;
    }

    /// <summary>The SQL standard's LIKE over <see cref="Rows"/>: no escape unless one is named.</summary>
    private static List<int> Expected(string pattern, string? escape)
    {
        var regex = new StringBuilder("^");
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (escape is not null && c == escape[0] && i + 1 < pattern.Length)
            {
                regex.Append(Regex.Escape(pattern[++i].ToString()));
            }
            else if (c == '%')
            {
                regex.Append(".*");
            }
            else if (c == '_')
            {
                regex.Append("(?:[\\uD800-\\uDBFF][\\uDC00-\\uDFFF]|.)");
            }
            else
            {
                regex.Append(Regex.Escape(c.ToString()));
            }
        }

        var matcher = new Regex(regex.Append('$').ToString(), RegexOptions.Singleline | RegexOptions.CultureInvariant);
        return [.. Rows.Where(r => matcher.IsMatch(r.Sku)).Select(r => r.Id).Order()];
    }

    /// <summary>ILIKE as D316 defines it: the standard LIKE over the rows and the pattern, both lowered as LOWER lowers.</summary>
    private static List<int> ExpectedIlike(string pattern, string? escape)
    {
        var regex = new StringBuilder("^");
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (escape is not null && c == escape[0] && i + 1 < pattern.Length)
            {
                regex.Append(Regex.Escape(pattern[++i].ToString().ToLowerInvariant()));
            }
            else if (c == '%')
            {
                regex.Append(".*");
            }
            else if (c == '_')
            {
                regex.Append("(?:[\\uD800-\\uDBFF][\\uDC00-\\uDFFF]|.)");
            }
            else
            {
                regex.Append(Regex.Escape(c.ToString().ToLowerInvariant()));
            }
        }

        var matcher = new Regex(regex.Append('$').ToString(), RegexOptions.Singleline | RegexOptions.CultureInvariant);
        return [.. Rows.Where(r => matcher.IsMatch(r.Sku.ToLowerInvariant())).Select(r => r.Id).Order()];
    }

    private PostgresFixture RequirePostgres()
    {
        var fixture = postgres.Fixture;
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? string.Empty);
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        return fixture;
    }

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static string? _duckFile;
    private static string? _database;

    private static async Task<string> DuckFileAsync()
    {
        await Gate.WaitAsync();
        try
        {
            if (_duckFile is not null)
            {
                return _duckFile;
            }

            var path = Path.Combine(Path.GetTempPath(), $"chalk-like-{Environment.ProcessId}.duckdb");
            File.Delete(path);
            await using var connection = new DuckDBConnection("DataSource=" + path);
            await connection.OpenAsync();
            await using (var create = connection.CreateCommand())
            {
                create.CommandText = "CREATE TABLE items(id INTEGER NOT NULL, sku VARCHAR NOT NULL)";
                await create.ExecuteNonQueryAsync();
            }

            foreach (var row in Rows)
            {
                await using var insert = connection.CreateCommand();
                insert.CommandText = "INSERT INTO items VALUES ($1, $2)";
                insert.Parameters.Add(new DuckDBParameter(row.Id));
                insert.Parameters.Add(new DuckDBParameter(row.Sku));
                await insert.ExecuteNonQueryAsync();
            }

            return _duckFile = path;
        }
        finally
        {
            Gate.Release();
        }
    }

    private Task<string> DatabaseAsync => CreateOnceAsync();

    private async Task<string> CreateOnceAsync()
    {
        var fixture = RequirePostgres();
        await Gate.WaitAsync();
        try
        {
            if (_database is not null)
            {
                return _database;
            }

            var connection = fixture.CreateDatabase("like_portability_" + Environment.ProcessId);
            await LoadAsync(connection);
            return _database = connection;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<string> LocaleDatabaseAsync(PostgresFixture fixture)
    {
        var name = "like_locale_" + Environment.ProcessId;
        await using (var admin = new NpgsqlConnection(fixture.ConnectionString))
        {
            await admin.OpenAsync();
            await using var exists = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @n", admin);
            exists.Parameters.AddWithValue("n", name);
            if (await exists.ExecuteScalarAsync() is null)
            {
                await using var create = new NpgsqlCommand(
                    $"CREATE DATABASE {name} TEMPLATE template0 ENCODING 'UTF8' LOCALE 'en_US.UTF-8'", admin);
                await create.ExecuteNonQueryAsync();
            }
        }

        var connection = fixture.ConnectionString!.Replace(
            "Database=postgres", "Database=" + name, StringComparison.Ordinal);
        await using (var check = new NpgsqlConnection(connection))
        {
            await check.OpenAsync();
            await using var ordered = new NpgsqlCommand("SELECT 'a' < 'B'", check);
            Assert.True((bool)(await ordered.ExecuteScalarAsync())!, "the database's collation is not a locale's");
        }

        await LoadAsync(connection);
        return connection;
    }

    private static async Task LoadAsync(string connection)
    {
        await using var db = new NpgsqlConnection(connection);
        await db.OpenAsync();
        await using (var create = new NpgsqlCommand(
            "DROP TABLE IF EXISTS items; CREATE TABLE items(id integer NOT NULL, sku text NOT NULL)", db))
        {
            await create.ExecuteNonQueryAsync();
        }

        foreach (var row in Rows)
        {
            await using var insert = new NpgsqlCommand("INSERT INTO items VALUES (@id, @sku)", db);
            insert.Parameters.AddWithValue("id", row.Id);
            insert.Parameters.AddWithValue("sku", row.Sku);
            await insert.ExecuteNonQueryAsync();
        }
    }
}

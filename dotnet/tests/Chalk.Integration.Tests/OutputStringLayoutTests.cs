using Apache.Arrow;
using Apache.Arrow.Types;
using Chalk.Catalog;
using Chalk.Client;
using Chalk.Sources;
using Chalk.Sources.Ado;
using Chalk.Sources.DuckDb;
using Chalk.Sources.Poco;
using Chalk.TestKit;
using DuckDB.NET.Data;

namespace Chalk.Integration.Tests;

/// <summary>One row of the in-process half: a plain <c>string</c> beside a <c>Utf8String</c>.</summary>
public sealed record NoteRow(long Id, string Text, Utf8String Tag);

/// <summary>
/// The string layout of a prepared query's output (D244): what the schema declares under each of
/// the three settings, and — from the second half of the decision — what the arrays actually are.
/// </summary>
/// <remarks>
/// <para>
/// Two sources on purpose, because the <c>Any</c> rule reads what a source says about itself. The
/// warehouse is a real DuckDB database behind the in-box ADO.NET source on its Arrow-export reader,
/// which declares classic strings; the in-process source declares views, which is what the POCO
/// writers produce for a <c>Utf8String</c> column and what a <c>string</c> column converts into
/// without moving a payload byte.
/// </para>
/// <para>
/// The statements are addressed at an explicit pushdown level wherever the level is what decides the
/// plan's shape: at <c>Full</c> a sort over a remote table <em>is</em> the source's own query and
/// the column still arrives untouched, and at <c>None</c> the same statement sorts in process and
/// the column is rebuilt. The rule is about the plan, so the test says which plan it means.
/// </para>
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class OutputStringLayoutTests : IDisposable
{
    private const string Warehouse = "warehouse";
    private const string InProcess = "inproc";

    private readonly SharedSidecar _sidecar;
    private readonly DuckDbFile _file = DuckDbFile.Create("d244");
    private readonly DuckDBConnection _keepAlive;
    private readonly AdoSource _remote;
    private readonly PocoSource _localSource;

    public OutputStringLayoutTests(SharedSidecar sidecar)
    {
        _sidecar = sidecar;
        Assert.SkipWhen(RemoteFixture.SkipReason is not null, RemoteFixture.SkipReason ?? string.Empty);

        _keepAlive = new DuckDBConnection(_file.ConnectionString);
        _keepAlive.Open();
        Execute(
            "CREATE TABLE \"messages\" (\"id\" INTEGER NOT NULL, \"content\" VARCHAR NOT NULL, "
            + "\"note\" VARCHAR)");
        Execute(
            """
            INSERT INTO "messages" VALUES
              (1, 'short', 'a note longer than twelve bytes'),
              (2, 'a message body that is comfortably longer than twelve bytes', NULL),
              (3, '', 'twelve bytes'),
              (4, 'exactly12345', NULL)
            """);

        _remote = new AdoSourceBuilder(
                Warehouse, () => new DuckDBConnection(_file.ConnectionString), Warehouse)
            .Dialect(DialectProfiles.DuckDb)
            .Options(TestTimeouts.SourceOptions)
            .Capabilities(AdoCapabilities.For(DialectProfiles.DuckDb, maxInList: 1000))
            .RowCounts(AdoSourceBuilder.RowCountMode.Exact)
            .DiscoverTables()
            .UseArrowReader()
            .Build();

        _localSource = new PocoSourceBuilder(InProcess, InProcess)
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .AddTable("notes", Notes)
            .Build();
    }

    private static IReadOnlyList<NoteRow> Notes { get; } =
    [
        new(1, "short", Utf8String.FromString("t1")),
        new(2, "a note body that is comfortably longer than twelve bytes",
            Utf8String.FromString("a tag longer than twelve bytes")),
        new(3, string.Empty, Utf8String.FromString(string.Empty)),
        new(4, "exactly12345", Utf8String.FromString("exactly12345")),
    ];

    public void Dispose()
    {
        _keepAlive.Dispose();
        _file.Dispose();
    }

    /// <summary>
    /// Every statement, at every setting: the schema declares what the setting says, and under
    /// <c>Any</c> it declares what the resolution rule predicts.
    /// </summary>
    [Theory]
    [MemberData(nameof(Statements))]
    public async Task The_schema_declares_the_layout_the_setting_asks_for(
        string sql, PushdownLevel pushdown, string expectedUnderAny)
    {
        Assert.SkipWhen(!_sidecar.Sidecar.IsAvailable, _sidecar.SkipReason ?? string.Empty);

        await using (var views = await EngineAsync(StringLayouts.Utf8View))
        {
            AssertStringColumns(
                (await Prepare(views, sql, pushdown)).OutputSchema, "utf8view");
        }

        await using (var classic = await EngineAsync(StringLayouts.Utf8))
        {
            AssertStringColumns((await Prepare(classic, sql, pushdown)).OutputSchema, "utf8");
        }

        await using (var any = await EngineAsync(StringLayouts.Any))
        {
            AssertStringColumns(
                (await Prepare(any, sql, pushdown)).OutputSchema, expectedUnderAny);
        }
    }

    /// <summary>A setting that names no layout is refused at engine creation, naming the option.</summary>
    [Fact]
    public async Task A_setting_that_names_no_layout_is_refused()
    {
        var failure = await Assert.ThrowsAsync<ArgumentException>(
            async () => await ChalkEngine.CreateAsync(
                new ChalkEngineOptions
                {
                    ContextId = "d244",
                    Sources = [_localSource],
                    Planner = new RecordedPlanner(RepoLayout.Plans.FullName),
                    Output = new OutputOptions { Strings = 0 },
                },
                TestContext.Current.CancellationToken));

        Assert.Contains("Output.Strings", failure.Message, StringComparison.Ordinal);
        Assert.Contains("names no acceptable layout", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The statements, with the pushdown level that fixes each one's plan and the layout the
    /// <c>Any</c> rule predicts for its STRING columns.
    /// </summary>
    public static TheoryData<string, PushdownLevel, string> Statements()
    {
        var data = new TheoryData<string, PushdownLevel, string>
        {
            // The DuckDB Arrow export, straight through: one RemoteQuery and no node above it.
            { "SELECT content FROM messages ORDER BY id", PushdownLevel.Full, "utf8" },

            // The same column with NULLs in it, and the same answer.
            { "SELECT note FROM messages ORDER BY id", PushdownLevel.Full, "utf8" },

            // Scanned rather than pushed: still a leaf, still untouched.
            { "SELECT content FROM messages", PushdownLevel.None, "utf8" },

            // Sorted in process, so the column is rebuilt by the sort's own copier.
            { "SELECT content FROM messages ORDER BY content", PushdownLevel.None, "utf8view" },

            // Filtered in process: whether the filter compacts is a per-batch decision.
            { "SELECT content FROM messages WHERE id > 1", PushdownLevel.None, "utf8view" },

            // Computed by an expression.
            { "SELECT UPPER(content) AS u FROM messages", PushdownLevel.None, "utf8view" },

            // Grouped in process.
            {
                "SELECT content, COUNT(*) AS n FROM messages GROUP BY content",
                PushdownLevel.None,
                "utf8view"
            },

            // A POCO Utf8String column: the writer produces views.
            { "SELECT tag FROM inproc.notes ORDER BY id", PushdownLevel.Full, "utf8view" },

            // A POCO string column: physically classic, and declared views because that is the
            // layout that spares this source work over both its column kinds.
            { "SELECT text FROM inproc.notes ORDER BY id", PushdownLevel.Full, "utf8view" },

            // Across the two sources, joined in process.
            {
                "SELECT m.content, n.text FROM messages m JOIN inproc.notes n ON m.id = n.id "
                + "ORDER BY m.id",
                PushdownLevel.Full,
                "utf8view"
            },
        };

        return data;
    }

    private static void AssertStringColumns(Apache.Arrow.Schema schema, string expected)
    {
        var strings = schema.FieldsList
            .Where(f => f.DataType is StringType or StringViewType)
            .ToArray();

        Assert.NotEmpty(strings);
        foreach (var field in strings)
        {
            Assert.Equal(expected, field.DataType.Name);
        }
    }

    private static ValueTask<PreparedQuery> Prepare(
        ChalkEngine engine, string sql, PushdownLevel pushdown) =>
        engine.PrepareAsync(
            sql,
            new PrepareOptions { Pushdown = pushdown },
            TestContext.Current.CancellationToken);

    private ValueTask<ChalkEngine> EngineAsync(StringLayouts strings) =>
        ChalkEngine.CreateAsync(
            new ChalkEngineOptions
            {
                ContextId = "d244",
                Sources = [_remote, _localSource],
                Planner = _sidecar.Sidecar.CreatePlanner(),
                Execution = new ExecutionOptions { BatchSize = 3 },
                Output = new OutputOptions { Strings = strings },
            },
            TestContext.Current.CancellationToken);

    private void Execute(string sql)
    {
        using var command = _keepAlive.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}

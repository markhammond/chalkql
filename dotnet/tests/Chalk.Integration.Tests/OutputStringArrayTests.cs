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

/// <summary>
/// The other half of D244, through the engine: whatever the schema declared, that is the array in
/// every batch, and the values are the same however the layout was chosen.
/// </summary>
/// <remarks>
/// The <see cref="OutputStringLayoutTests"/> fixture, again — a DuckDB warehouse behind the in-box
/// ADO.NET source on its Arrow-export reader, and an in-process POCO source with a <c>string</c>
/// column beside a <c>Utf8String</c> one — because what is under test here is what comes out of the
/// same statements rather than a second set of them. Every run is compared against the default
/// setting's, so a layout that lost a byte shows up as a different value and not as a different type.
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class OutputStringArrayTests : IDisposable
{
    private const string Warehouse = "warehouse";
    private const string InProcess = "inproc";

    private readonly SharedSidecar _sidecar;
    private readonly DuckDbFile _file = DuckDbFile.Create("d244-arrays");
    private readonly DuckDBConnection _keepAlive;
    private readonly AdoSource _remote;
    private readonly PocoSource _local;

    public OutputStringArrayTests(SharedSidecar sidecar)
    {
        _sidecar = sidecar;
        Assert.SkipWhen(RemoteFixture.SkipReason is not null, RemoteFixture.SkipReason ?? string.Empty);

        _keepAlive = new DuckDBConnection(_file.ConnectionString);
        _keepAlive.Open();
        Execute(
            "CREATE TABLE \"messages\" (\"id\" INTEGER NOT NULL, \"content\" VARCHAR NOT NULL, "
            + "\"note\" VARCHAR, \"sent_at\" TIMESTAMP NOT NULL)");
        Execute(
            """
            INSERT INTO "messages" VALUES
              (1, 'short', 'a note longer than twelve bytes', TIMESTAMP '2020-01-01 00:00:01'),
              (2, 'a message body that is comfortably longer than twelve bytes', NULL,
               TIMESTAMP '2020-01-01 00:00:02'),
              (3, '', 'twelve bytes', TIMESTAMP '2020-01-01 00:00:03'),
              (4, 'exactly12345', NULL, TIMESTAMP '2020-01-01 00:00:04'),
              (5, 'épée', 'exactly123456', TIMESTAMP '2020-01-01 00:00:05')
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

        _local = new PocoSourceBuilder(InProcess, InProcess)
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
        new(4, "exactly12345", Utf8String.FromString("exactly123456")),
        new(5, "épée", Utf8String.FromString("épée")),
    ];

    public void Dispose()
    {
        _keepAlive.Dispose();
        _file.Dispose();
    }

    /// <summary>
    /// The schema's declared type is the runtime array type of every STRING column of every batch,
    /// at every setting and under both memory modes — and the rows are the default setting's rows.
    /// </summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Every_batch_carries_the_declared_array(
        string sql, PushdownLevel pushdown, string expectedUnderAny)
    {
        Assert.SkipWhen(!_sidecar.Sidecar.IsAvailable, _sidecar.SkipReason ?? string.Empty);

        var expected = await ReadAsync(sql, pushdown, StringLayouts.Utf8View, OutputMemory.Managed);

        foreach (var strings in new[] { StringLayouts.Utf8View, StringLayouts.Utf8, StringLayouts.Any })
        {
            foreach (var memory in new[] { OutputMemory.Managed, OutputMemory.Pooled })
            {
                var actual = await ReadAsync(sql, pushdown, strings, memory);

                Assert.Equal(expected.Rows, actual.Rows);
                Assert.Equal(
                    strings == StringLayouts.Any
                        ? expectedUnderAny
                        : strings == StringLayouts.Utf8 ? "utf8" : "utf8view",
                    Assert.Single(actual.Declared.Distinct()));
            }
        }
    }

    /// <summary>
    /// <c>Any</c> elides the view-building pass for a column that reaches the output untouched from
    /// the DuckDB Arrow export: the batch carries a classic <c>StringArray</c>, which is what the
    /// export produced, and the sixteen-byte lane buffer is never built.
    /// </summary>
    [Theory]
    [InlineData(OutputMemory.Managed)]
    [InlineData(OutputMemory.Pooled)]
    public async Task Any_hands_the_DuckDB_export_straight_through(OutputMemory memory)
    {
        Assert.SkipWhen(!_sidecar.Sidecar.IsAvailable, _sidecar.SkipReason ?? string.Empty);

        const string Sql = "SELECT content FROM messages ORDER BY id";
        var any = await ReadAsync(Sql, PushdownLevel.Full, StringLayouts.Any, memory);
        var views = await ReadAsync(Sql, PushdownLevel.Full, StringLayouts.Utf8View, memory);

        Assert.All(any.Kinds, kind => Assert.Equal(nameof(StringArray), kind));
        Assert.All(views.Kinds, kind => Assert.Equal(nameof(StringViewArray), kind));
        Assert.Equal(views.Rows, any.Rows);
        Assert.Equal("duckdb-arrow", any.SourcePath);
    }

    /// <summary>
    /// The column the export cannot produce falls back to the provider's reader, and the declared
    /// layout is still what comes out — of both readers, in the same query.
    /// </summary>
    [Theory]
    [InlineData(StringLayouts.Utf8View, nameof(StringViewArray))]
    [InlineData(StringLayouts.Utf8, nameof(StringArray))]
    [InlineData(StringLayouts.Any, nameof(StringArray))]
    public async Task A_query_that_falls_back_still_carries_the_declared_array(
        StringLayouts strings, string expected)
    {
        Assert.SkipWhen(!_sidecar.Sidecar.IsAvailable, _sidecar.SkipReason ?? string.Empty);

        // DuckDB exports a TIMESTAMP in microseconds; discovery declares a DuckDB TIMESTAMP as
        // TIMESTAMP(9), so the exported type is not the declared one, the whole query goes to
        // DbDataReaderFetch, and the text column comes back with it.
        const string Sql = "SELECT content, sent_at FROM messages ORDER BY id";

        var actual = await ReadAsync(Sql, PushdownLevel.Full, strings, OutputMemory.Managed);
        var views = await ReadAsync(Sql, PushdownLevel.Full, StringLayouts.Utf8View, OutputMemory.Managed);

        // The fallback is the point of the case, so it is asserted rather than assumed.
        Assert.Contains("DbDataReader", actual.SourcePath ?? string.Empty, StringComparison.Ordinal);
        Assert.All(actual.Kinds, kind => Assert.Equal(expected, kind));
        Assert.Equal(views.Rows, actual.Rows);
    }

    public static TheoryData<string, PushdownLevel, string> Cases()
    {
        var data = new TheoryData<string, PushdownLevel, string>
        {
            { "SELECT content FROM messages ORDER BY id", PushdownLevel.Full, "utf8" },
            { "SELECT note FROM messages ORDER BY id", PushdownLevel.Full, "utf8" },
            { "SELECT content FROM messages", PushdownLevel.None, "utf8" },
            { "SELECT content FROM messages ORDER BY content", PushdownLevel.None, "utf8view" },
            { "SELECT content FROM messages WHERE id > 1", PushdownLevel.None, "utf8view" },
            { "SELECT UPPER(content) AS u FROM messages", PushdownLevel.None, "utf8view" },
            {
                "SELECT content, COUNT(*) AS n FROM messages GROUP BY content",
                PushdownLevel.None,
                "utf8view"
            },
            { "SELECT tag FROM inproc.notes ORDER BY id", PushdownLevel.Full, "utf8view" },
            { "SELECT text FROM inproc.notes ORDER BY id", PushdownLevel.Full, "utf8view" },
            {
                "SELECT m.content, n.text FROM messages m JOIN inproc.notes n ON m.id = n.id "
                + "ORDER BY m.id",
                PushdownLevel.Full,
                "utf8view"
            },
        };

        return data;
    }

    /// <summary>What one run produced: the rows, the declared types, and the runtime array types.</summary>
    private sealed record Run(
        List<string> Rows,
        List<string> Declared,
        List<string> Kinds,
        string? SourcePath);

    private async Task<Run> ReadAsync(
        string sql, PushdownLevel pushdown, StringLayouts strings, OutputMemory memory)
    {
        await using var engine = await ChalkEngine.CreateAsync(
            new ChalkEngineOptions
            {
                ContextId = "d244",
                Sources = [_remote, _local],
                Planner = _sidecar.Sidecar.CreatePlanner(),
                Execution = new ExecutionOptions { BatchSize = 2, OutputMemory = memory },
                Output = new OutputOptions { Strings = strings },
            },
            TestContext.Current.CancellationToken);

        var query = await engine.PrepareAsync(
            sql,
            new PrepareOptions { Pushdown = pushdown },
            TestContext.Current.CancellationToken);

        var columns = query.OutputSchema.FieldsList
            .Select((f, i) => (Field: f, Index: i))
            .Where(c => c.Field.DataType is StringType or StringViewType)
            .ToArray();

        Assert.NotEmpty(columns);

        var declared = columns.Select(c => c.Field.DataType.Name).ToList();
        var kinds = new List<string>();
        var rows = new List<string>();

        await using var execution = await engine.ExecuteAsync(
            query, ct: TestContext.Current.CancellationToken);

        await foreach (var batch in execution.Batches.WithCancellation(
            TestContext.Current.CancellationToken))
        {
            using (batch)
            {
                // "The schema equals the arrays" is the whole decision: the batch's own schema is the
                // declared one, and the array under each field has to be that field's type.
                foreach (var (field, index) in columns)
                {
                    var array = batch.Column(index);
                    kinds.Add(array.GetType().Name);
                    Assert.Equal(
                        field.DataType.Name,
                        array switch
                        {
                            StringArray => "utf8",
                            StringViewArray => "utf8view",
                            _ => array.Data.DataType.Name,
                        });
                }

                foreach (var row in BatchReader.ToStorageRows([batch]))
                {
                    rows.Add(string.Join(
                        '', row.Select(v => v is null ? "∅" : v.ToString())));
                }
            }
        }

        execution.Stats.SourcePaths.TryGetValue(Warehouse, out var path);
        return new Run(rows, declared, kinds, path);
    }

    private void Execute(string sql)
    {
        using var command = _keepAlive.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}

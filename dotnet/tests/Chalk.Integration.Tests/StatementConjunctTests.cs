using System.Data.Common;
using Chalk.Catalog;
using Chalk.Client;
using Chalk.Entitlements;
using Chalk.Sources;
using Chalk.Sources.Ado;
using Chalk.Sources.Poco;
using Chalk.TestKit;
using DuckDB.NET.Data;

namespace Chalk.Integration.Tests;

/// <summary>
/// A statement's own filter over a column the principal sees masked says what the mask gives, not
/// what the row holds, and decides no disclosure rule over that column (F163).
/// </summary>
/// <remarks>
/// <para>
/// <c>last_name</c> is masked to its initial for everyone but a manager, and <c>note</c> is
/// disclosed only where the last name is exactly S. <c>WHERE last_name = 'S'</c> holds for Smith —
/// his initial is S — so the statement reads his row, and his note stays withheld, because his name
/// is not S. <c>org_id</c> is disclosed raw, so a filter over it still settles <c>memo</c>'s rule.
/// So does a filter no NULL passes over a column withheld as NULL — every row it keeps holds the
/// raw value — and not one over a column withheld as <c>0</c>, which the placeholder passes.
/// </para>
/// <para>
/// Every case is run the three ways a host runs a statement, and each gives the same answer: over
/// rows in the process with the principal's values bound as the statement is prepared, over the
/// same rows prepared for the context's shape and bound as it runs, and over the same rows in a
/// DuckDB database.
/// </para>
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class StatementConjunctTests(SharedSidecar sidecar) : IDisposable
{
    private readonly List<DbConnection> _connections = [];
    private readonly List<DuckDbFile> _files = [];

    public void Dispose()
    {
        foreach (var connection in _connections)
        {
            connection.Dispose();
        }

        foreach (var file in _files)
        {
            file.Dispose();
        }
    }

    /// <summary>Where the rows are, and when the principal's values are bound.</summary>
    public enum Run
    {
        /// <summary>Rows in the process, the values bound as the statement is prepared.</summary>
        InProcess,

        /// <summary>Rows in the process, prepared for the context's shape and bound as it runs.</summary>
        BoundAtExecute,

        /// <summary>The same rows in a DuckDB database, reached through the ADO.NET source.</summary>
        FromDatabase,
    }

    public static TheoryData<Run> Runs => new() { Run.InProcess, Run.BoundAtExecute, Run.FromDatabase };

    public sealed record Card(int Id, string LastName, string? Note, string? Memo, int OrgId);

    public sealed record Account(int Id, int OrgId, string? Memo);

    private static readonly Account[] Accounts =
    [
        new(1, 1, "the first organisation's memo"),
        new(2, 2, "the second organisation's memo"),
    ];

    private static readonly Card[] Cards =
    [
        new(1, "S", "the note of S", "the memo of S", 1),
        new(2, "Smith", "Smith's note", "Smith's memo", 1),
        new(3, "Stone", "Stone's note", "Stone's memo", 2),
    ];

    /// <summary>A manager of no organisation: every last name is its initial.</summary>
    private static readonly RequestContext Agent = new()
    {
        Lists = new Dictionary<string, ContextRelation>(StringComparer.Ordinal)
        {
            ["managers"] = new() { Columns = ["id"], Rows = [], ColumnTypes = [ChalkType.Int32()] },
            ["orgs"] = new() { Columns = ["id"], Rows = [[1]], ColumnTypes = [ChalkType.Int32()] },
        },
    };

    private static readonly EntitlementsOptions Siblings = new() { IncludeDisclosureColumns = true };

    [Theory]
    [MemberData(nameof(Runs))]
    public async Task A_filter_on_a_masked_column_does_not_disclose_what_the_rule_withholds(Run run)
    {
        var answer = await AnswerAsync(
            run, "SELECT id, note FROM cards WHERE last_name = 'S' ORDER BY id");

        // Smith and Stone share the initial; only the row named S discloses its note.
        Assert.Equal(["1|the note of S", "2|<null>", "3|<null>"], answer.Rows);
        Assert.Equal(ReportedDisclosure.PerRow, answer.Disclosures[1]);
    }

    [Theory]
    [MemberData(nameof(Runs))]
    public async Task The_per_row_label_says_which_notes_were_withheld(Run run)
    {
        var answer = await AnswerAsync(
            run, "SELECT id, note FROM cards WHERE last_name = 'S' ORDER BY id", Siblings);

        // id, its label, note, its label: the note is disclosed on the row named S alone.
        Assert.Equal(
            ["1|FULL|the note of S|FULL", "2|FULL|<null>|REDACTED", "3|FULL|<null>|REDACTED"],
            answer.Rows);
    }

    [Theory]
    [MemberData(nameof(Runs))]
    public async Task A_filter_on_a_raw_column_still_settles_a_rule_over_it(Run run)
    {
        var answer = await AnswerAsync(
            run, "SELECT id, memo FROM cards WHERE org_id = 1 ORDER BY id");

        Assert.Equal(["1|the memo of S", "2|Smith's memo"], answer.Rows);
        Assert.Equal(ReportedDisclosure.Full, answer.Disclosures[1]);
    }

    [Theory]
    [MemberData(nameof(Runs))]
    public async Task A_filter_no_null_passes_keeps_a_column_withheld_as_null_whole(Run run)
    {
        // Where org_id is not disclosed it is NULL, which fails org_id = 1: every row kept is the
        // first organisation's, and the report says org_id is whole. Bound at execute, the report
        // is made before the organisations are known and says only that it varies.
        var answer = await AnswerAsync(
            run, "SELECT id, org_id FROM accounts WHERE org_id = 1 ORDER BY id");

        Assert.Equal(["1|1"], answer.Rows);
        Assert.Equal(
            run == Run.BoundAtExecute ? ReportedDisclosure.PerRow : ReportedDisclosure.Full,
            answer.Disclosures[1]);
    }

    [Theory]
    [MemberData(nameof(Runs))]
    public async Task A_filter_a_placeholder_passes_does_not_disclose_what_the_rule_withholds(Run run)
    {
        // Withheld as 0, the second organisation's org_id passes org_id <> 2, and its memo — which
        // the rule gives only where the raw organisation is not 2 — stays withheld.
        var answer = await AnswerAsync(
            run, "SELECT id, memo FROM zeroed WHERE org_id <> 2 ORDER BY id");

        Assert.Equal(["1|the first organisation's memo", "2|<null>"], answer.Rows);
        Assert.Equal(ReportedDisclosure.PerRow, answer.Disclosures[1]);
    }

    // ---------------------------------------------------------------- the harness

    /// <summary>The rows, each its values joined by <c>|</c>, and the report's label per column.</summary>
    private sealed record Answer(string[] Rows, ReportedDisclosure[] Disclosures);

    private static readonly TableEntitlementDescriptor CardsEntitlement = new()
    {
        Columns =
        [
            new ColumnEntitlementDescriptor
            {
                Column = 1,
                Rules = [new DisclosureRule { When = "org_id IN (@ctx.managers)", Then = Disclosure.Full }],
                Otherwise = Disclosure.Masked,
                Mask = "SUBSTRING(last_name, 1, 1)",
            },
            new ColumnEntitlementDescriptor
            {
                Column = 2,
                Rules = [new DisclosureRule { When = "last_name = 'S'", Then = Disclosure.Full }],
                Otherwise = Disclosure.None,
            },
            new ColumnEntitlementDescriptor
            {
                Column = 3,
                Rules = [new DisclosureRule { When = "org_id = 1", Then = Disclosure.Full }],
                Otherwise = Disclosure.None,
            },
        ],
    };

    /// <summary>
    /// <c>org_id</c> disclosed in the principal's organisations and withheld elsewhere — as NULL, or
    /// as <paramref name="placeholder"/> where one is given — with <c>memo</c> disclosed where the
    /// organisation is not 2.
    /// </summary>
    private static TableEntitlementDescriptor Withheld(string placeholder) => new()
    {
        Columns =
        [
            new ColumnEntitlementDescriptor
            {
                Column = 1,
                Rules = [new DisclosureRule { When = "org_id IN (@ctx.orgs)", Then = Disclosure.Full }],
                Otherwise = Disclosure.None,
                Placeholder = placeholder,
            },
            new ColumnEntitlementDescriptor
            {
                Column = 2,
                Rules = [new DisclosureRule { When = "org_id <> 2", Then = Disclosure.Full }],
                Otherwise = Disclosure.None,
            },
        ],
    };

    private async Task<Answer> AnswerAsync(Run run, string sql, EntitlementsOptions? options = null)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "statement-conjuncts",
            Sources = [run == Run.FromDatabase ? DatabaseSource() : InProcessSource()],
            Planner = sidecar.CreatePlanner(),
        });

        var prepared = await engine
            .WithEntitlements(options)
            .PrepareAsync(sql, run == Run.BoundAtExecute ? Agent.Shape() : Agent);

        var rows = new List<string>();
        await using var execution = run == Run.BoundAtExecute
            ? await engine.ExecuteAsync(prepared.Query, Agent)
            : await engine.ExecuteAsync(prepared.Query, (IReadOnlyList<object?>?)null);
        await foreach (var batch in execution.Batches)
        {
            using (batch)
            {
                rows.AddRange(BatchReader.ToRows(batch).Select(
                    r => string.Join("|", r.Select(v => v?.ToString() ?? "<null>"))));
            }
        }

        return new Answer([.. rows], [.. prepared.Columns.Select(c => c.Disclosure)]);
    }

    private static ISourceRuntime InProcessSource() =>
        new PocoSourceBuilder("mem")
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .AddTable("accounts", Accounts, t => t.UniqueKey(a => a.Id).Entitlement(Withheld("")))
            .AddTable("zeroed", Accounts, t => t.UniqueKey(a => a.Id).Entitlement(Withheld("0")))
            .AddTable("cards", Cards, t => t.UniqueKey(c => c.Id).Entitlement(CardsEntitlement))
            .Build();

    /// <summary>The same tables, rows and policies in a DuckDB database.</summary>
    private ISourceRuntime DatabaseSource()
    {
        Assert.SkipWhen(RemoteFixture.SkipReason is not null, RemoteFixture.SkipReason ?? string.Empty);

        var file = DuckDbFile.Create("statement-conjuncts");
        _files.Add(file);
        var connection = new DuckDBConnection(file.ConnectionString);
        connection.Open();
        _connections.Add(connection);

        Execute(
            connection,
            "CREATE TABLE cards (id INTEGER NOT NULL, last_name VARCHAR NOT NULL, note VARCHAR, "
            + "memo VARCHAR, org_id INTEGER NOT NULL)");
        foreach (var card in Cards)
        {
            Execute(
                connection,
                "INSERT INTO cards VALUES ($1, $2, $3, $4, $5)",
                card.Id, card.LastName, card.Note, card.Memo, card.OrgId);
        }

        foreach (var table in new[] { "accounts", "zeroed" })
        {
            Execute(
                connection,
                $"CREATE TABLE {table} (id INTEGER NOT NULL, org_id INTEGER NOT NULL, memo VARCHAR)");
            foreach (var account in Accounts)
            {
                Execute(
                    connection,
                    $"INSERT INTO {table} VALUES ($1, $2, $3)",
                    account.Id, account.OrgId, account.Memo);
            }
        }

        return new AdoSourceBuilder("duck", () => new DuckDBConnection(file.ConnectionString), "duck")
            .Dialect(DialectProfiles.DuckDb)
            .Options(TestTimeouts.SourceOptions)
            .Capabilities(AdoCapabilities.For(DialectProfiles.DuckDb))
            .RowCounts(AdoSourceBuilder.RowCountMode.Exact)
            .DiscoverTables()
            .Entitlement("accounts", Withheld(""))
            .Entitlement("zeroed", Withheld("0"))
            .Entitlement("cards", CardsEntitlement)
            .Build();
    }

    private static void Execute(DbConnection connection, string sql, params object?[] values)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var value in values)
        {
            command.Parameters.Add(new DuckDBParameter(value ?? DBNull.Value));
        }

        command.ExecuteNonQuery();
    }
}

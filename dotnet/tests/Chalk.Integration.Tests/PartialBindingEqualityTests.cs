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
/// A statement's own equality settles the disclosure rules it decides, beside a list the principal
/// is bound to at execution (F162).
/// </summary>
/// <remarks>
/// <para>
/// A principal sees the members of organisations 1 and 2, and one person of organisation 3.
/// <c>first_name</c> is masked to its initial in the organisations and withheld elsewhere;
/// <c>org_id</c> of <c>accounts</c> is disclosed in the organisations and withheld elsewhere. Bound
/// partially — the people left to execution, as a host leaves a subject list — <c>WHERE org_id = 3</c>
/// says every row the statement keeps is outside the organisations, so <c>first_name</c> is withheld
/// on every one and the report says so, where it said the answer varied row by row.
/// </para>
/// <para>
/// The rows never change. Each case runs the ways a host runs a statement: bound as it is prepared,
/// bound partially, bound as it runs — where the organisations are not known when the report is made,
/// and it says only that the answer varies — and partially over the same rows in a DuckDB database.
/// </para>
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class PartialBindingEqualityTests(SharedSidecar sidecar) : IDisposable
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
        /// <summary>Rows in the process, every list bound as the statement is prepared.</summary>
        BoundAtPrepare,

        /// <summary>Rows in the process, the people left to execution.</summary>
        PartiallyBound,

        /// <summary>Rows in the process, every list left to execution.</summary>
        BoundAtExecute,

        /// <summary>The same rows in a DuckDB database, the people left to execution.</summary>
        FromDatabase,
    }

    public static TheoryData<Run> Runs =>
        new() { Run.BoundAtPrepare, Run.PartiallyBound, Run.BoundAtExecute, Run.FromDatabase };

    public sealed record Member(int Id, int OrgId, string FirstName);

    public sealed record Account(int Id, int OrgId);

    private static readonly Member[] Members =
    [
        new(1, 1, "Ann"),
        new(2, 2, "Ben"),
        new(3, 3, "Cat"),
        new(4, 3, "Dan"),
    ];

    private static readonly Account[] Accounts = [new(1, 1), new(2, 2), new(3, 3), new(4, 3)];

    /// <summary>Organisations 1 and 2, and person 3 — who is in organisation 3.</summary>
    private static readonly RequestContext Principal = new()
    {
        Lists = new Dictionary<string, ContextRelation>(StringComparer.Ordinal)
        {
            ["orgs"] = new() { Columns = ["id"], Rows = [[1], [2]], ColumnTypes = [ChalkType.Int32()] },
            ["people"] = new() { Columns = ["id"], Rows = [[3]], ColumnTypes = [ChalkType.Int32()] },
        },
    };

    [Theory]
    [MemberData(nameof(Runs))]
    public async Task Without_an_equality_the_name_varies_row_by_row(Run run)
    {
        var answer = await AnswerAsync(run, "SELECT id, first_name FROM members ORDER BY id");

        Assert.Equal(["1|A", "2|B", "3|<null>"], answer.Rows);
        Assert.Equal(ReportedDisclosure.PerRow, answer.Disclosures[1]);
    }

    [Theory]
    [MemberData(nameof(Runs))]
    public async Task An_equality_outside_the_organisations_withholds_the_name_on_every_row(Run run)
    {
        var answer = await AnswerAsync(
            run, "SELECT id, first_name FROM members WHERE org_id = 3 ORDER BY id");

        // Person 3 is the one row of organisation 3 the principal sees, and the name is withheld.
        Assert.Equal(["3|<null>"], answer.Rows);
        Assert.Equal(Settled(run, ReportedDisclosure.Redacted), answer.Disclosures[1]);
    }

    [Theory]
    [MemberData(nameof(Runs))]
    public async Task An_equality_inside_the_organisations_masks_the_name_on_every_row(Run run)
    {
        var answer = await AnswerAsync(
            run, "SELECT id, first_name FROM members WHERE org_id = 1 ORDER BY id");

        Assert.Equal(["1|A"], answer.Rows);
        Assert.Equal(Settled(run, ReportedDisclosure.Masked), answer.Disclosures[1]);
    }

    [Theory]
    [MemberData(nameof(Runs))]
    public async Task The_statements_filter_reads_the_value_disclosed(Run run)
    {
        // Organisation 3's org_id is withheld as NULL, which fails org_id = 3: no row is kept, though
        // the principal sees account 3, whose raw org_id is 3.
        var outside = await AnswerAsync(
            run, "SELECT id, org_id FROM accounts WHERE org_id = 3 ORDER BY id");
        Assert.Empty(outside.Rows);
        Assert.Equal(Settled(run, ReportedDisclosure.Redacted), outside.Disclosures[1]);

        var inside = await AnswerAsync(
            run, "SELECT id, org_id FROM accounts WHERE org_id = 1 ORDER BY id");
        Assert.Equal(["1|1"], inside.Rows);
        Assert.Equal(Settled(run, ReportedDisclosure.Full), inside.Disclosures[1]);
    }

    // ---------------------------------------------------------------- the harness

    /// <summary>The rows, each its values joined by <c>|</c>, and the report's label per column.</summary>
    private sealed record Answer(string[] Rows, ReportedDisclosure[] Disclosures);

    /// <summary>
    /// The label a settled rule gives, where the organisations are known as the statement is
    /// prepared; bound as it runs they are not, and the report says the answer varies.
    /// </summary>
    private static ReportedDisclosure Settled(Run run, ReportedDisclosure settled) =>
        run == Run.BoundAtExecute ? ReportedDisclosure.PerRow : settled;

    private static readonly TableEntitlementDescriptor MembersEntitlement = new()
    {
        RowPredicate = "org_id IN (@ctx.orgs) OR id IN (@ctx.people)",
        Columns =
        [
            new ColumnEntitlementDescriptor
            {
                Column = 2,
                Rules = [new DisclosureRule { When = "org_id IN (@ctx.orgs)", Then = Disclosure.Masked }],
                Otherwise = Disclosure.None,
                Mask = "SUBSTRING(first_name, 1, 1)",
            },
        ],
    };

    private static readonly TableEntitlementDescriptor AccountsEntitlement = new()
    {
        RowPredicate = "org_id IN (@ctx.orgs) OR id IN (@ctx.people)",
        Columns =
        [
            new ColumnEntitlementDescriptor
            {
                Column = 1,
                Rules = [new DisclosureRule { When = "org_id IN (@ctx.orgs)", Then = Disclosure.Full }],
                Otherwise = Disclosure.None,
            },
        ],
    };

    private async Task<Answer> AnswerAsync(Run run, string sql)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "partial-binding-equalities",
            Sources = [run == Run.FromDatabase ? DatabaseSource() : InProcessSource()],
            Planner = sidecar.CreatePlanner(),
        });

        var prepareWith = run switch
        {
            Run.BoundAtPrepare => Principal,
            Run.BoundAtExecute => Principal.Shape(),
            _ => Principal.Shape(["people"]),
        };
        var prepared = await engine.WithEntitlements().PrepareAsync(sql, prepareWith);

        var rows = new List<string>();
        await using var execution = run == Run.BoundAtPrepare
            ? await engine.ExecuteAsync(prepared.Query, (IReadOnlyList<object?>?)null)
            : await engine.ExecuteAsync(prepared.Query, Principal);
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
            .AddTable("members", Members, t => t.UniqueKey(m => m.Id).Entitlement(MembersEntitlement))
            .AddTable("accounts", Accounts, t => t.UniqueKey(a => a.Id).Entitlement(AccountsEntitlement))
            .Build();

    /// <summary>The same tables, rows and policies in a DuckDB database.</summary>
    private ISourceRuntime DatabaseSource()
    {
        Assert.SkipWhen(RemoteFixture.SkipReason is not null, RemoteFixture.SkipReason ?? string.Empty);

        var file = DuckDbFile.Create("partial-binding-equalities");
        _files.Add(file);
        var connection = new DuckDBConnection(file.ConnectionString);
        connection.Open();
        _connections.Add(connection);

        Execute(
            connection,
            "CREATE TABLE members (id INTEGER NOT NULL, org_id INTEGER NOT NULL, first_name VARCHAR NOT NULL)");
        foreach (var member in Members)
        {
            Execute(
                connection, "INSERT INTO members VALUES ($1, $2, $3)", member.Id, member.OrgId, member.FirstName);
        }

        Execute(connection, "CREATE TABLE accounts (id INTEGER NOT NULL, org_id INTEGER NOT NULL)");
        foreach (var account in Accounts)
        {
            Execute(connection, "INSERT INTO accounts VALUES ($1, $2)", account.Id, account.OrgId);
        }

        return new AdoSourceBuilder("duck", () => new DuckDBConnection(file.ConnectionString), "duck")
            .Dialect(DialectProfiles.DuckDb)
            .Options(TestTimeouts.SourceOptions)
            .Capabilities(AdoCapabilities.For(DialectProfiles.DuckDb))
            .RowCounts(AdoSourceBuilder.RowCountMode.Exact)
            .DiscoverTables()
            .Entitlement("members", MembersEntitlement)
            .Entitlement("accounts", AccountsEntitlement)
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

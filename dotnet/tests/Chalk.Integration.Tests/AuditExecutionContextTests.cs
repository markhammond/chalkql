using System.Data.Common;
using Chalk.Catalog;
using Chalk.Client;
using Chalk.Entitlements;
using Chalk.Entitlements.Tenancy;
using Chalk.Sources;
using Chalk.Sources.Ado;
using Chalk.Sources.Poco;
using Chalk.TestKit;
using DuckDB.NET.Data;
using CatalogContext = Chalk.Catalog.CatalogContext;

namespace Chalk.Integration.Tests;

/// <summary>
/// The audit event describes the execution: the purpose, actor and list counts of the context the
/// execution runs with, however the statement was prepared (F166).
/// </summary>
/// <remarks>
/// <para>
/// Alice reads organisation 1 for billing; Bob reads organisations 2 and 3 for a fraud review. A
/// plan prepared once for a shape and run for both was audited as whoever prepared it — Bob's run
/// said purpose "billing", actor "alice" — and every list bound at execution was counted as empty,
/// though each got their own rows.
/// </para>
/// <para>
/// Each case runs the ways a host runs a statement: bound as it is prepared, once per principal;
/// bound partially, the organisation list left to execution, once per principal, since the user is
/// folded; prepared once for a shape and bound per execution; and the last over the same rows in
/// DuckDB.
/// </para>
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class AuditExecutionContextTests(SharedSidecar sidecar) : IDisposable
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
        /// <summary>Rows in the process, prepared with each principal's values.</summary>
        BoundAtPrepare,

        /// <summary>Rows in the process, prepared per principal with the organisation list left open.</summary>
        PartiallyBound,

        /// <summary>Rows in the process, one plan prepared for a shape and bound per execution.</summary>
        BoundAtExecute,

        /// <summary>The same rows in DuckDB, one plan prepared for a shape and bound per execution.</summary>
        FromDatabase,
    }

    public static TheoryData<Run> Runs =>
        new() { Run.BoundAtPrepare, Run.PartiallyBound, Run.BoundAtExecute, Run.FromDatabase };

    private sealed record Order(int Id, int Org);

    private static readonly Order[] Orders = [new(1, 1), new(2, 2), new(3, 3), new(4, 3)];

    [Theory]
    [MemberData(nameof(Runs))]
    public async Task Each_execution_is_audited_as_the_principal_it_runs_for(Run run)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        var fromDatabase = run == Run.FromDatabase;
        var catalog = new CatalogContext
        {
            ContextId = "audit-context",
            Schemas = [(fromDatabase ? OrdersInDatabase(null) : OrdersInProcess(null)).DescribeSchema()],
        };
        var policy = TenancyPolicy.Declare(catalog);
        var org = policy.Tenancy("org");
        var reader = policy.Role("reader");
        var table = policy.Source("s").Table("orders");
        table.Tenancy(t => t.Direct(org, table.Column("Org")));
        var entitlements = policy.Compile(catalog);

        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "audit-context",
            Sources = [fromDatabase ? OrdersInDatabase(entitlements) : OrdersInProcess(entitlements)],
            Planner = sidecar.CreatePlanner(),
        });
        var audit = new RecordingAudit();
        var entitled = engine.WithEntitlements(audit: audit);

        var alice = new TenancyPrincipal
        {
            User = "alice", Purpose = "billing", Actor = "alice",
            Grants = [Grant.ForTenancy(org, 1, reader)],
        };
        var bob = new TenancyPrincipal
        {
            User = "bob", Purpose = "fraud review", Actor = "bob",
            Grants = [Grant.ForTenancy(org, 2, reader), Grant.ForTenancy(org, 3, reader)],
        };

        const string Sql = "SELECT Id FROM s.orders ORDER BY Id";
        var shared = run is Run.BoundAtExecute or Run.FromDatabase
            ? await entitled.PrepareAsync(Sql, entitlements.Bind(alice).Shape())
            : null;

        async Task<string[]> RowsFor(TenancyPrincipal who)
        {
            var context = entitlements.Bind(who);
            return run switch
            {
                Run.BoundAtPrepare => await ConfinementDirectionTests.RowsAsync(
                    engine, await entitled.PrepareAsync(Sql, context)),
                Run.PartiallyBound => await ConfinementDirectionTests.RowsAsync(
                    engine, await entitled.PrepareAsync(Sql, context.Shape(["org_reader"])), context),
                _ => await ConfinementDirectionTests.RowsAsync(engine, shared!, context),
            };
        }

        Assert.Equal(["1"], await RowsFor(alice));
        Assert.Equal(["2", "3", "4"], await RowsFor(bob));

        var (forAlice, forBob) = (audit.Events[0], audit.Events[1]);
        Assert.Equal(("billing", "alice", 1), (forAlice.Purpose, forAlice.Actor, forAlice.ContextListRowCounts["org_reader"]));
        Assert.Equal(("fraud review", "bob", 2), (forBob.Purpose, forBob.Actor, forBob.ContextListRowCounts["org_reader"]));
        if (shared is not null)
        {
            // One plan served both: the digest says so, and the events still tell them apart.
            Assert.Equal(forAlice.PlanDigest, forBob.PlanDigest);
        }
    }

    private sealed class RecordingAudit : IEntitlementsAudit
    {
        public List<EntitlementsAuditEvent> Events { get; } = [];

        public void Executed(EntitlementsAuditEvent audited) => Events.Add(audited);
    }

    private static ISourceRuntime OrdersInProcess(TenancyEntitlements? entitlements) =>
        new PocoSourceBuilder("s", "s")
            .AddTable("orders", Orders, t =>
            {
                t.UniqueKey(x => x.Id);
                if (entitlements?.For("s", "orders") is { } d) t.Entitlement(d);
            })
            .Build();

    /// <summary>The same table and rows in DuckDB, declared rather than discovered.</summary>
    private ISourceRuntime OrdersInDatabase(TenancyEntitlements? entitlements)
    {
        Assert.SkipWhen(RemoteFixture.SkipReason is not null, RemoteFixture.SkipReason ?? string.Empty);

        var file = DuckDbFile.Create("audit-context");
        _files.Add(file);
        var connection = new DuckDBConnection(file.ConnectionString);
        connection.Open();
        _connections.Add(connection);
        Execute(connection, "CREATE TABLE \"orders\" (\"Id\" INTEGER NOT NULL PRIMARY KEY, \"Org\" INTEGER NOT NULL)");
        foreach (var order in Orders)
        {
            Execute(connection, $"INSERT INTO \"orders\" VALUES ({order.Id}, {order.Org})");
        }

        var builder = new AdoSourceBuilder("s-db", () => new DuckDBConnection(file.ConnectionString), "s")
            .Dialect(DialectProfiles.DuckDb)
            .Options(TestTimeouts.SourceOptions)
            .Capabilities(AdoCapabilities.For(DialectProfiles.DuckDb))
            .RowCounts(AdoSourceBuilder.RowCountMode.Exact)
            .AddTable(
                "orders",
                [
                    new ColumnDescriptor { Name = "Id", Type = ChalkType.Int32() },
                    new ColumnDescriptor { Name = "Org", Type = ChalkType.Int32() },
                ],
                configure: t => t.UniqueKey("Id"));
        if (entitlements?.For("s", "orders") is { } descriptor)
        {
            builder.Entitlement("orders", descriptor);
        }

        return builder.Build();
    }

    private static void Execute(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}

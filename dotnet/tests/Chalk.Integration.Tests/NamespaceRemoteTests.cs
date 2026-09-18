using System.Data.Common;
using Chalk.Catalog;
using Chalk.Client;
using Chalk.Entitlements;
using Chalk.Entitlements.Tenancy;
using Chalk.Ir;
using Chalk.Sources.Ado;
using Chalk.Sources.Poco;
using Chalk.TestKit;
using DuckDB.NET.Data;
using CatalogContext = Chalk.Catalog.CatalogContext;

namespace Chalk.Integration.Tests;

/// <summary>
/// The third source of the namespace family: a table called <c>orders</c> in a real database beside
/// the one in process, under one policy, so the <b>SQL that leaves the process</b> can be read
/// (D270, <c>docs/design/45-typed-tenancy-surface.md</c> §1 as amended 2026-09-16).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="NamespaceConfusionTests"/> reads the plan and the report; a POCO table has no source to
/// push into, so neither can say what a source is <em>sent</em>. This can. What a remote query
/// carries is the one channel where a policy's predicate leaves Chalk entirely, so it is the one
/// place where serving the wrong source's rules would be invisible to every check the client makes
/// of its own plan — and it is therefore the one worth reading by hand.
/// </para>
/// <para>
/// The claim is narrow and complete: the query sent to the database names that database's table and
/// carries that table's own row predicate, and nothing of the in-process source's policy appears in
/// it. Both binding modes, because a shape-only bind is the plan shared across principals and is
/// where a predicate that resolved by name rather than by source would be shared too.
/// </para>
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class NamespaceRemoteTests(SharedSidecar sidecar) : IDisposable
{
    /// <summary>The in-process source, tenanted by organisation.</summary>
    private const string Ledger = "ledger";

    /// <summary>The database, tenanted by region — a different axis over the same column names.</summary>
    private const string Vault = "vault";

    private sealed record Order(int Id, int OrgId, int RegionId, long Amount, string Note);

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

    // ---------------------------------------------------------------- the two sources

    /// <summary>
    /// The ledger's four orders. Its amounts are the 1000s, so a 3000 arriving here would have come
    /// from the other source and a 1000 arriving there from this one.
    /// </summary>
    private static PocoSource LedgerSource(TenancyEntitlements? entitlements)
    {
        var builder = new PocoSourceBuilder("ledger-mem", Ledger)
            .NamingPolicy(PocoNamingPolicy.SnakeCase);
        builder.AddTable(
            "orders",
            new[]
            {
                new Order(1, 1, 7, 1001, "LEDGER-NOTE-01"),
                new Order(2, 1, 8, 1002, "LEDGER-NOTE-02"),
                new Order(3, 2, 7, 1003, "LEDGER-NOTE-03"),
                new Order(4, 2, 8, 1004, "LEDGER-NOTE-04"),
            },
            t =>
            {
                t.OrderedBy(o => o.Id).UniqueKey(o => o.Id);
                if (entitlements?.For(Ledger, "orders") is { } descriptor)
                {
                    t.Entitlement(descriptor);
                }
            });
        return builder.Build();
    }

    /// <summary>The same table, the same columns, in DuckDB — and the 3000s.</summary>
    private AdoSource VaultSource(TenancyEntitlements? entitlements)
    {
        var file = DuckDbFile.Create("namespace");
        _files.Add(file);

        var keepAlive = new DuckDBConnection(file.ConnectionString);
        keepAlive.Open();
        _connections.Add(keepAlive);

        Execute(keepAlive, "DROP TABLE IF EXISTS \"orders\"");
        Execute(
            keepAlive,
            "CREATE TABLE \"orders\" (\"id\" INTEGER NOT NULL PRIMARY KEY, "
            + "\"org_id\" INTEGER NOT NULL, \"region_id\" INTEGER NOT NULL, "
            + "\"amount\" BIGINT NOT NULL, \"note\" VARCHAR NOT NULL)");
        Execute(
            keepAlive,
            "INSERT INTO \"orders\" VALUES (1, 1, 7, 3001, 'VAULT-NOTE-01'), "
            + "(2, 1, 8, 3002, 'VAULT-NOTE-02'), (3, 2, 7, 3003, 'VAULT-NOTE-03'), "
            + "(4, 2, 8, 3004, 'VAULT-NOTE-04')");

        var builder = new AdoSourceBuilder(
                "vault-db", () => new DuckDBConnection(file.ConnectionString), Vault)
            .Dialect(DialectProfiles.DuckDb)
            .Options(TestTimeouts.SourceOptions)
            .Capabilities(AdoCapabilities.For(DialectProfiles.DuckDb))
            .RowCounts(AdoSourceBuilder.RowCountMode.Exact)
            .DiscoverTables();

        if (entitlements?.For(Vault, "orders") is { } descriptor)
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

    // ---------------------------------------------------------------- the one policy

    /// <summary>
    /// One policy over both sources, with a different axis for each: the ledger's rows resolve by
    /// organisation and the vault's by region, over columns of the same name. A policy that resolved
    /// a table by name would compile one of these twice and the answers would be indistinguishable
    /// from correct until a row arrived.
    /// </summary>
    private static (TenancyEntitlements Entitlements, TenancyPolicy Policy) Declare(
        CatalogContext catalog)
    {
        var policy = TenancyPolicy.Declare(catalog);
        var ledger = policy.Source(Ledger);
        var vault = policy.Source(Vault);
        var org = policy.Tenancy("org");
        var region = policy.Tenancy("region");
        var manager = policy.Role("manager");
        var keeper = policy.Role("keeper");

        var ledgerOrders = ledger.Table("orders");
        ledgerOrders
            .Tenancy(t => t.Direct(org, ledgerOrders.Column("org_id")))
            .Access(ledgerOrders.Column("amount"), [manager], Verdict.Full)
            .Access(ledgerOrders.Column("amount"), [keeper], Verdict.None);

        var vaultOrders = vault.Table("orders");
        vaultOrders
            .Tenancy(t => t.Direct(region, vaultOrders.Column("region_id")))
            .Access(vaultOrders.Column("amount"), [keeper], Verdict.Full)
            .Access(vaultOrders.Column("amount"), [manager], Verdict.None);

        return (policy.Compile(catalog), policy);
    }

    private async Task<(ChalkEngine Engine, TenancyEntitlements Entitlements, TenancyPolicy Policy)>
        EngineAsync()
    {
        var bare = new CatalogContext
        {
            ContextId = "namespace-remote",
            Epoch = 1,
            Schemas = [LedgerSource(null).DescribeSchema(), VaultSource(null).DescribeSchema()],
        };

        var (entitlements, policy) = Declare(bare);
        var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "namespace-remote",
            Sources = [LedgerSource(entitlements), VaultSource(entitlements)],
            Planner = sidecar.CreatePlanner(),
        });

        return (engine, entitlements, policy);
    }

    private static TenancyPrincipal Principal(TenancyPolicy policy, string role, int id) => new()
    {
        User = 1,
        MaskKey = "k",
        Grants =
        [
            string.Equals(role, "manager", StringComparison.Ordinal)
                ? Grant.ForTenancy(policy.Tenancy("org"), id, policy.Role("manager"))
                : Grant.ForTenancy(policy.Tenancy("region"), id, policy.Role("keeper")),
        ],
    };

    /// <summary>Every remote query text the plan carries — the SQL a source is actually sent.</summary>
    private static IReadOnlyList<string> RemoteSql(Plan plan)
    {
        var texts = new List<string>();
        foreach (var rel in PlanWalker.Rels(plan.Root))
        {
            if (rel.KindCase == Rel.KindOneofCase.RemoteQuery)
            {
                texts.Add(rel.RemoteQuery.QueryText);
            }
        }

        return texts;
    }

    // ---------------------------------------------------------------- what the source is sent

    /// <summary>
    /// The query sent to the database carries the <b>vault's</b> row predicate — its region — and
    /// nothing of the ledger's organisation, for a principal who holds a grant in both axes.
    /// </summary>
    [Fact]
    public async Task The_pushed_query_carries_only_its_own_sources_predicate()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        var (engine, entitlements, policy) = await EngineAsync();
        await using (engine)
        {
            var keeper = Principal(policy, "keeper", 7);
            var prepared = await engine.WithEntitlements().PrepareAsync(
                "SELECT id, amount FROM vault.orders ORDER BY id", entitlements.Bind(keeper));

            var sql = Assert.Single(RemoteSql(prepared.Plan));
            Assert.Contains("region_id", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("org_id", sql, StringComparison.OrdinalIgnoreCase);

            // And no value of the in-process source reached the database's query text.
            Assert.DoesNotContain("LEDGER-NOTE", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("100", sql, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The same under a <b>shape-only</b> bind, which is the plan shared across principals: what is
    /// pushed is still the vault's own predicate, with the membership left open rather than folded.
    /// </summary>
    [Fact]
    public async Task The_pushed_query_names_its_own_source_under_a_shape_bind_too()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        var (engine, entitlements, policy) = await EngineAsync();
        await using (engine)
        {
            var keeper = Principal(policy, "keeper", 7);
            var prepared = await engine.WithEntitlements().PrepareAsync(
                "SELECT id, amount FROM vault.orders ORDER BY id",
                entitlements.Bind(keeper).Shape());

            var sql = Assert.Single(RemoteSql(prepared.Plan));
            Assert.Contains("region_id", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("org_id", sql, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// <b>The reconciler reads a table that is only under a pushed query</b> (F92). The vault's
    /// <c>orders</c> is read by the database and by nothing this client executes, so it appears in
    /// the plan only inside the remote query's pushed subtree — and the policy's own prediction is
    /// still made for it, which is what says the reconciler is on the inclusive walk.
    /// </summary>
    /// <remarks>
    /// The prediction is taken twice: once for the principal the plan was compiled for, which
    /// agrees, and once for a principal of the <em>other</em> role, for whom this table's amount is
    /// <c>None</c> where the plan discloses it in full. A reconciler that stopped at the source
    /// boundary would report no difference at all for the second, which is the silence ADR 0057 §4
    /// names load-bearing one caller along.
    /// </remarks>
    [Fact]
    public async Task A_read_only_under_a_pushed_query_is_reconciled()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        var (engine, entitlements, policy) = await EngineAsync();
        await using (engine)
        {
            var keeper = Principal(policy, "keeper", 7);
            var prepared = await engine.WithEntitlements().PrepareAsync(
                "SELECT id, amount FROM vault.orders ORDER BY id", entitlements.Bind(keeper));

            // The read is the pushed subtree's and nothing else's: the executed walk finds none.
            Assert.DoesNotContain(
                PlanWalker.ExecutedRels(prepared.Plan), r => r.KindCase == Rel.KindOneofCase.Read);
            var read = Assert.Single(
                PlanWalker.Rels(prepared.Plan), r => r.KindCase == Rel.KindOneofCase.Read).Read;
            Assert.Equal("orders", read.Table.Table);
            Assert.NotEmpty(read.Disclosures);

            // The plan is the keeper's, and the keeper's prediction agrees with it.
            Assert.Empty(entitlements.Reconcile(prepared.Plan, keeper));

            // The manager's does not: this table's amount is None for that role, and the plan
            // discloses it. The difference is reported only because the read was reached.
            var difference = Assert.Single(
                entitlements.Reconcile(prepared.Plan, Principal(policy, "manager", 1)));
            Assert.Equal("orders", difference.Table);
            Assert.Equal("amount", difference.Column);
            Assert.Equal(DisclosureOutcome.Redacted, difference.Expected);
            Assert.Equal(DisclosureOutcome.Full, difference.Reported);
        }
    }

    /// <summary>
    /// A statement naming both: one remote query for the database's <c>orders</c> and an in-process
    /// leaf for the other, each under its own rules. The keeper reads the vault's amount and meets
    /// the ledger's redaction, which is the two policies meeting in one result.
    /// </summary>
    [Fact]
    public async Task A_statement_naming_both_pushes_one_and_leaves_the_other_in_process()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        var (engine, entitlements, policy) = await EngineAsync();
        await using (engine)
        {
            var both = new TenancyPrincipal
            {
                User = 1,
                MaskKey = "k",
                Grants =
                [
                    Grant.ForTenancy(policy.Tenancy("org"), 1, policy.Role("manager")),
                    Grant.ForTenancy(policy.Tenancy("region"), 7, policy.Role("keeper")),
                ],
            };

            var prepared = await engine.WithEntitlements().PrepareAsync(
                "SELECT l.id, l.amount, v.amount FROM ledger.orders l"
                + " JOIN vault.orders v ON v.id = l.id ORDER BY l.id",
                entitlements.Bind(both));

            // Exactly one query left the process, and it was the database's own table.
            var sql = Assert.Single(RemoteSql(prepared.Plan));
            Assert.Contains("region_id", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("org_id", sql, StringComparison.OrdinalIgnoreCase);

            // Two tables in the report, each named by its schema, each with its own descriptor.
            Assert.Equal(2, prepared.Entitlements.Tables.Count);
            var ledgerTable = Assert.Single(prepared.Entitlements.Tables, t => t.Schema == Ledger);
            var vaultTable = Assert.Single(prepared.Entitlements.Tables, t => t.Schema == Vault);
            Assert.Equal("orders", ledgerTable.Table);
            Assert.Equal("orders", vaultTable.Table);
            Assert.NotEqual(ledgerTable.DescriptorHash, vaultTable.DescriptorHash);
            Assert.Equal(
                entitlements.For(Ledger, "orders")!.DescriptorHash, ledgerTable.DescriptorHash);
            Assert.Equal(
                entitlements.For(Vault, "orders")!.DescriptorHash, vaultTable.DescriptorHash);
        }
    }
}

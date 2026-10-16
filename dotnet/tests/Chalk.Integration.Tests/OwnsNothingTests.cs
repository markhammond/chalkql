using Chalk.Catalog;
using Chalk.Client;
using Chalk.Entitlements;
using Chalk.Entitlements.Tenancy;
using Chalk.Sources.Poco;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// A principal that owns no rows (F229): a class principal, one identity for many people, says so
/// with a <see cref="TenancyPrincipal.User"/> of null, and the owner tests the policy writes come
/// out of its plans, its labels and its explanation.
/// </summary>
/// <remarks>
/// Before, such a principal had to carry some id no owner column holds. Each owner test then stayed
/// in every plan, a test no row could pass: an <c>OR</c> beside the row predicate's tenancy terms,
/// and a creator's rule that kept a column the principal's roles show in full decided row by row.
/// The rows were right either way; what the policy could say about them was not.
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class OwnsNothingTests(SharedSidecar sidecar)
{
    private sealed record Org(int Id, string Name);

    private sealed record Order(int Id, int OrgId, int CreatedBy, long Amount);

    private const string ContextId = "owns-nothing";

    private const string SourceName = "main";

    private const string Statement = "SELECT amount FROM orders ORDER BY id";

    private static readonly IReadOnlyList<Org> Orgs = [new(1, "Northwind"), new(2, "Southerly")];

    /// <summary>Two orders in O1 and one in O2, written by three people, none of them the desk.</summary>
    private static readonly IReadOnlyList<Order> Orders =
        [new(1, 1, 7, 100), new(2, 1, 8, 200), new(3, 2, 4, 300)];

    private static readonly TenancyEntitlements Policy = Compile();

    /// <summary>The O1 desk as a class principal that owns nothing.</summary>
    private static TenancyPrincipal Desk => new()
    {
        User = null,
        MaskKey = "k",
        Grants = [Grant.ForTenancy(Policy.Policy.Tenancy("org"), 1, Policy.Policy.Role("manager"))],
    };

    /// <summary>The same desk carrying an id of its own, which no order's writer holds.</summary>
    private static TenancyPrincipal DeskById => new()
    {
        User = 999,
        MaskKey = "k",
        Grants = Desk.Grants,
    };

    /// <summary>
    /// The desk reads its role's rows and its role's verdict, and the policy can now say so: the
    /// column the manager's rule shows in full is <c>Full</c>, where with an id of its own it was
    /// decided per row. The rows are the same two either way.
    /// </summary>
    [Fact]
    public async Task A_principal_that_owns_nothing_reads_its_roles_alone()
    {
        var (byId, byIdRows) = await RunAsync(DeskById);
        Assert.Equal("amount:PerRow", byId);
        Assert.Equal(["100", "200"], byIdRows);

        var (owner, ownerRows) = await RunAsync(Desk);
        Assert.Equal("amount:Full", owner);
        Assert.Equal(["100", "200"], ownerRows);
    }

    /// <summary>
    /// The explanation is the plan's: the row predicate folds to the desk's organisation alone, and
    /// the column's verdict is a constant, with no owner's branch beside it.
    /// </summary>
    [Fact]
    public async Task A_principal_that_owns_nothing_is_explained_without_an_owner()
    {
        await using var engine = await EngineAsync();

        var byId = Assert.Single(
            (await engine.WithEntitlements().ExplainAsync(Statement, Policy.Bind(DeskById))).Tables,
            t => t.Table == "orders");
        Assert.Contains("999", byId.RowPredicate, StringComparison.Ordinal);
        Assert.Contains("WHEN", Assert.Single(byId.Columns, c => c.Column == "amount").Disclosure, StringComparison.Ordinal);

        var orders = Assert.Single(
            (await engine.WithEntitlements().ExplainAsync(Statement, Policy.Bind(Desk))).Tables,
            t => t.Table == "orders");
        Assert.DoesNotContain("$2", orders.RowPredicate, StringComparison.Ordinal);
        Assert.Equal("FULL", Assert.Single(orders.Columns, c => c.Column == "amount").Disclosure);
    }

    /// <summary>
    /// Bound for a tenant's plan, folding only the organisations (D232), the desk's NULL user is
    /// folded all the same: it is every such principal's. So the plan has no owner test, and run
    /// with the desk's whole binding it reads the same two rows.
    /// </summary>
    [Fact]
    public async Task A_tenant_plan_folds_a_principal_that_owns_nothing()
    {
        await using var engine = await EngineAsync();
        var partial = Policy.Bind(Desk, fold: ["org"]);
        Assert.False(partial.IsShape("user"));
        Assert.True(partial.IsShape("mask_key"));

        var orders = Assert.Single(
            (await engine.WithEntitlements().ExplainAsync(Statement, partial)).Tables,
            t => t.Table == "orders");
        Assert.DoesNotContain("$2", orders.RowPredicate, StringComparison.Ordinal);

        var prepared = await engine.WithEntitlements().PrepareAsync(Statement, partial);
        Assert.Equal(["100", "200"], await RowsAsync(engine, prepared.Query, Policy.Bind(Desk)));
    }

    /// <summary>A principal with an id still owns what it wrote: order 3, in an organisation it holds no grant in.</summary>
    [Fact]
    public async Task A_writer_still_reads_what_it_wrote()
    {
        var (report, rows) = await RunAsync(new TenancyPrincipal { User = 4, MaskKey = "k" });

        Assert.Equal("amount:Full", report);
        Assert.Equal(["300"], rows);
    }

    // ------------------------------------------------------------------ the catalog

    /// <summary>Orders kept by organisation, each owned by its writer, whose amount a manager reads in full.</summary>
    private static TenancyEntitlements Compile()
    {
        var schema = Tables(null).DescribeSchema();
        var policy = TenancyPolicy.Declare(new CatalogContext { ContextId = ContextId, Epoch = 1, Schemas = [schema] });
        var manager = policy.Role("manager");
        var org = policy.Tenancy("org");
        var orders = policy.Source(SourceName).Table("orders");
        orders
            .Tenancy(t => t
                .Direct(org, orders.Column("org_id"))
                .ResourceOwner(orders.Column("created_by")))
            .Access(new AccessRule { Roles = [manager], Column = orders.Column("amount"), Grants = Verdict.Full });
        return policy.Compile([schema]);
    }

    private static PocoSource Tables(TenancyEntitlements? entitlements)
    {
        var builder = new PocoSourceBuilder("mem").NamingPolicy(PocoNamingPolicy.SnakeCase);
        builder.AddTable("orgs", Orgs, t => t.OrderedBy(o => o.Id).UniqueKey(o => o.Id));
        builder.AddTable("orders", Orders, t =>
        {
            t.OrderedBy(o => o.Id).UniqueKey(o => o.Id).ForeignKey(o => o.OrgId)
                .References<Org>(o => o.Id, verify: true);
            if (entitlements?.For(SourceName, "orders") is { } descriptor)
            {
                t.Entitlement(descriptor);
            }
        });
        return builder.Build();
    }

    private async Task<ChalkEngine> EngineAsync() =>
        await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = ContextId,
            Sources = [Tables(Policy)],
            Planner = sidecar.CreatePlanner(),
        });

    private async Task<(string Report, string[] Rows)> RunAsync(TenancyPrincipal principal)
    {
        await using var engine = await EngineAsync();
        var prepared = await engine.WithEntitlements().PrepareAsync(Statement, Policy.Bind(principal));
        var report = string.Join(", ", prepared.Columns.Select(c => $"{c.Name}:{c.Disclosure}"));
        return (report, await RowsAsync(engine, prepared.Query, context: null));
    }

    private static async Task<string[]> RowsAsync(ChalkEngine engine, PreparedQuery query, RequestContext? context)
    {
        var rows = new List<string>();
        await using var execution = context is null
            ? await engine.ExecuteAsync(query)
            : await engine.ExecuteAsync(query, context);
        await foreach (var batch in execution.Batches)
        {
            using (batch)
            {
                rows.AddRange(BatchReader.ToRows(batch).Select(r => string.Join("|", r)));
            }
        }

        return [.. rows];
    }
}

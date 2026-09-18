using Chalk.Catalog;
using Chalk.Client;
using Chalk.Entitlements;
using Chalk.Entitlements.Tenancy;
using Chalk.Sources.Poco;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// What the two explicit grantees buy at the fold (F75, D269 (c), design 43): a rule that means
/// "everyone who may see this row" is read as a <b>constant</b> verdict rather than a per-row one,
/// because its condition is the very text <c>Filter_R</c> carries.
/// </summary>
/// <remarks>
/// <para>
/// §3.3 reads a column as constantly full only where a rule's condition <em>is</em> a conjunct of
/// the filter below it — textual identity, the narrow sound case. A role-scoped union never is: the
/// row predicate is wider, by the resource owner's fail-safe and by the global grant. So layer B,
/// which writes role scopes, read per-row where layer A, which wrote the predicate by hand, read a
/// constant, and the client's own invariant refused the pair
/// (<c>I-IR-E: output column 'total' is reported Aggregate and the reads this plan walks say
/// PerRow</c>).
/// </para>
/// <para>
/// The two declarations below differ in exactly that and in nothing else, and the difference is
/// visible in the report: the roles alone leave the column <c>PerRow</c>, and
/// <see cref="Roles.Visible"/> makes it <c>Full</c> — the same rows either way, which is what the
/// second assertion of each test is for.
/// </para>
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class ExplicitGranteeTests(SharedSidecar sidecar)
{
    private sealed record Org(int Id, string Name);

    private sealed record Order(int Id, int OrgId, int CreatedBy, long Amount);

    private const string ContextId = "grantee";

    private static readonly IReadOnlyList<Org> Orgs = [new(1, "Northwind"), new(2, "Southerly")];

    /// <summary>
    /// Order 3 is O2's and was written by the principal below, so it is a row the fail-safe admits
    /// and no role's scope does — the very row that makes the two conditions different things.
    /// </summary>
    private static readonly IReadOnlyList<Order> Orders =
        [new(1, 1, 7, 100), new(2, 1, 8, 200), new(3, 2, 4, 300)];

    /// <summary>A manager in O1 who also wrote order 3, which lies in O2.</summary>
    private static TenancyPrincipal Manager => new()
    {
        User = 4,
        MaskKey = "k",
        Grants = [Grant.ForTenancy(OrgKind, 1, ManagerRole)],
    };

    /// <summary>
    /// The same person with an agent's grant in O2 beside it: a principal who can see rows a rule
    /// naming the manager role alone says nothing about, which is what keeps such a rule per-row.
    /// </summary>
    private static TenancyPrincipal ManagerAndAgent => new()
    {
        User = 4,
        MaskKey = "k",
        Grants =
        [
            Grant.ForTenancy(OrgKind, 1, ManagerRole),
            Grant.ForTenancy(OrgKind, 2, AgentRole),
        ],
    };

    // ------------------------------------------------------------------ the claim

    /// <summary>
    /// The roles alone: correct, and per-row. A row admitted by the fail-safe alone is within no
    /// role's scope, so <c>Full</c> for the roles does not cover it and the verdict has to be
    /// decided row by row.
    /// </summary>
    [Fact]
    public async Task A_rule_for_the_roles_alone_leaves_the_column_per_row()
    {
        var (report, rows) = await RunAsync(Roles: p => [p.Role("manager")]);

        Assert.Equal("amount:PerRow", report);
        Assert.Equal(["100", "200", "300"], rows);
    }

    /// <summary>
    /// <see cref="Roles.Visible"/>: the condition is the row predicate, so §3.3's <c>holds</c> fires
    /// and the column is a constant. The rows are the same three — the grant did not widen, only
    /// what the policy could say about it did.
    /// </summary>
    [Fact]
    public async Task Roles_visible_makes_the_column_a_constant_the_fold_can_read()
    {
        var (report, rows) = await RunAsync(Roles: _ => [Chalk.Entitlements.Tenancy.Roles.Visible]);

        Assert.Equal("amount:Full", report);
        Assert.Equal(["100", "200", "300"], rows);
    }

    /// <summary>
    /// The owner beside the roles reaches the fail-safe's row, which is what it is for — and it is
    /// still only the ways in the rule <em>names</em>. A principal who also holds an agent's grant
    /// can see rows this rule says nothing about, so the verdict stays per-row and that is the truth
    /// of the rule as written.
    /// </summary>
    [Fact]
    public async Task The_owner_beside_the_roles_is_still_only_the_ways_in_the_rule_names()
    {
        var (report, rows) = await RunAsync(
            Roles: p => [p.Role("manager"), Chalk.Entitlements.Tenancy.Roles.Owner],
            principal: ManagerAndAgent);

        Assert.Equal("amount:PerRow", report);
        Assert.Equal(["100", "200", "300"], rows);
    }

    /// <summary>
    /// The same principal, the same rows, and the declaration that says "every way in": the column
    /// is a constant. This pair is the whole of F75 — the rule the host meant, said in a form the
    /// fold recognises.
    /// </summary>
    [Fact]
    public async Task Roles_visible_covers_the_ways_in_a_role_list_would_have_to_enumerate()
    {
        var (report, rows) = await RunAsync(
            Roles: _ => [Chalk.Entitlements.Tenancy.Roles.Visible],
            principal: ManagerAndAgent);

        Assert.Equal("amount:Full", report);
        Assert.Equal(["100", "200", "300"], rows);
    }

    /// <summary>
    /// And for a principal whose grants the rule <em>does</em> enumerate, the roles plus the owner
    /// already are every way in, so the two spellings agree: the marker adds nothing where the list
    /// was complete, which is what makes it a shorthand rather than a widening.
    /// </summary>
    [Fact]
    public async Task The_owner_beside_every_role_the_principal_holds_is_already_every_way_in()
    {
        var (report, rows) = await RunAsync(
            Roles: p => [p.Role("manager"), Chalk.Entitlements.Tenancy.Roles.Owner]);

        Assert.Equal("amount:Full", report);
        Assert.Equal(["100", "200", "300"], rows);
    }

    /// <summary>
    /// And a principal the rule does not reach at all still sees the placeholder: the constant is a
    /// constant for the principal it was folded for, never for everyone.
    /// </summary>
    [Fact]
    public async Task A_principal_outside_every_way_in_still_sees_nothing()
    {
        var (report, rows) = await RunAsync(
            Roles: _ => [Chalk.Entitlements.Tenancy.Roles.Visible],
            principal: new TenancyPrincipal { User = 99, MaskKey = "k" });

        // No grant, no ownership: the row predicate folds to FALSE and the leaf is empty, so the
        // column's constant is never read.
        Assert.Equal("amount:Full", report);
        Assert.Empty(rows);
    }

    // ------------------------------------------------------------------ the catalog

    /// <summary>The schema this file's tables live in, which names their source (D270).</summary>
    private const string SourceName = "main";

    /// <summary>
    /// The model, with the rule's grantees left to the test: every name enters once here and the
    /// rule names the handles it got back, so a role a rule speaks for is one this policy declared
    /// and the two markers are the only grantees no declaration produced (D270 §1, §6 (b)).
    /// </summary>
    private static TenancyPolicy Policy(
        SchemaDescriptor schema, Func<TenancyPolicy, IReadOnlyList<Role>> roles)
    {
        var policy = TenancyPolicy.Declare(new CatalogContext
        {
            ContextId = ContextId,
            Epoch = 1,
            Schemas = [schema],
        });
        policy.Role("manager");
        policy.Role("agent");
        policy.AllowGlobalGrants();
        var org = policy.Tenancy("org");
        var orders = policy.Source(SourceName).Table("orders");
        orders
            .Tenancy(t => t
                .Direct(org, orders.Column("org_id"))
                .ResourceOwner(orders.Column("created_by")))
            .Access(new AccessRule
            {
                Roles = roles(policy),
                Column = orders.Column("amount"),
                Grants = Verdict.Full,
            });
        return policy;
    }

    /// <summary>
    /// The handles the two principals hold their grants in. A grant carries the kind and the role it
    /// was declared as (D270) and binding matches them by the name they entered under, so one policy
    /// over this file's model serves every compilation of it — which is what lets the principals be
    /// written once while each test compiles the model with its own rule. Nothing compiles this one,
    /// so its rule names no grantee at all: what is wanted of it is the names.
    /// </summary>
    private static readonly Lazy<TenancyPolicy> Handles =
        new(() => Policy(Tables(null).DescribeSchema(), _ => []));

    private static Kind OrgKind => Handles.Value.Tenancy("org");

    private static Role ManagerRole => Handles.Value.Role("manager");

    private static Role AgentRole => Handles.Value.Role("agent");

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

    private async Task<(string Report, string[] Rows)> RunAsync(
        Func<TenancyPolicy, IReadOnlyList<Role>> Roles, TenancyPrincipal? principal = null)
    {
        var schema = Tables(null).DescribeSchema();
        var entitlements = Policy(schema, Roles).Compile([schema]);
        var source = Tables(entitlements);
        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = ContextId,
            Sources = [source],
            Planner = sidecar.CreatePlanner(),
        });

        var prepared = await engine.WithEntitlements().PrepareAsync(
            "SELECT amount FROM orders ORDER BY id",
            entitlements.Bind(principal ?? Manager));
        var report = string.Join(
            ", ", prepared.Columns.Select(c => $"{c.Name}:{c.Disclosure}"));

        var rows = new List<string>();
        await using var execution = await engine.ExecuteAsync(prepared.Query);
        await foreach (var batch in execution.Batches)
        {
            using (batch)
            {
                rows.AddRange(BatchReader.ToRows(batch).Select(r => string.Join("|", r)));
            }
        }

        return (report, rows.ToArray());
    }
}

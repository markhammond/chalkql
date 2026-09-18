using Chalk.Catalog;
using Chalk.Client;
using Chalk.Entitlements;
using Chalk.Entitlements.Tenancy;
using Chalk.Sources.Poco;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// The shape F66 stopped, end to end: a target related to a vendor through its lines, the vendor's
/// rules on the target's own columns, and every statement design 38 §8 asks of it, as every
/// principal (ADR 0050).
/// </summary>
/// <remarks>
/// <para>
/// The fixture is this file's own — the tables, the policy, the rows and the principals — because
/// the corpus fixture and its goldens belong to D265's clause (h) and nothing here may move them.
/// What it reproduces is the fixture's <em>shape</em>: <c>orders</c> holding its organisation and
/// its member directly and its vendor along a <c>Related</c> path, a vendor's rule on
/// <c>member_id</c> — which is what makes that column nullable for every principal (D216, D161) and
/// what put the Hep pre-pass in reach of F66 — and a second entitled table joined to it on that very
/// column.
/// </para>
/// <para>
/// Every claim is checked against a <b>naive oracle</b> written here in C#, which states design 38
/// §8's rule directly over the rows and consults no descriptor, no planner and no pass: an order is
/// visible when its organisation or its member grants it, or its creator is the caller, or some line
/// of it carries a vendor the principal's vendor grants reach.
/// </para>
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class PathBudgetTests(SharedSidecar sidecar)
{
    private const string ContextId = "path-budget";

    // ------------------------------------------------------------------ the rows

    private sealed record Member(int Id, int OrgId, string LastName);

    private sealed record Order(int Id, int MemberId, int OrgId, long Amount, string Note, int CreatedBy);

    private sealed record Vendor(int Id, string Name);

    private sealed record OrderItem(int Id, int OrderId, int VendorId, int Quantity, long UnitPrice);

    private static readonly IReadOnlyList<Member> Members =
    [
        new(1, 1, "Ng"),
        new(2, 1, "Oyelaran"),
        new(3, 2, "Persson"),
        new(4, 2, "Quill"),
    ];

    /// <summary>
    /// Order 4 is the one nothing but its lines reaches: no grant of the fixture names organisation
    /// 3, so a vendor that supplies it is the only way in — which is what makes "a row related to a
    /// tenant" an assertion rather than a coincidence. Order 3 carries no line at all, so a vendor
    /// reaches it by no path (§4: a row related to zero tenants is invisible along that axis).
    /// </summary>
    private static readonly IReadOnlyList<Order> Orders =
    [
        new(1, 1, 1, 100, "first", 1),
        new(2, 3, 2, 200, "second", 3),
        new(3, 4, 2, 300, "third", 9),
        new(4, 2, 3, 400, "fourth", 9),
    ];

    private static readonly IReadOnlyList<Vendor> Vendors = [new(1, "Acme"), new(2, "Borealis")];

    private static readonly IReadOnlyList<OrderItem> OrderItems =
    [
        new(1, 1, 1, 2, 50),
        new(2, 2, 2, 1, 200),
        new(3, 4, 1, 4, 100),
    ];

    // ------------------------------------------------------------------ the principals

    /// <summary>
    /// The handles the grants below are held in: the very policy the descriptors were compiled
    /// from, so a kind and a role enter once, at their declaration, and a grant names what it got
    /// back (D270 §1).
    /// </summary>
    private static TenancyPolicy Handles => Compiled.Value.Entitlements.Policy;

    private static Kind OrgKind => Handles.Tenancy("org");

    private static Kind VendorKind => Handles.Tenancy("vendor");

    private static SubjectKind MemberKind => Handles.Subject("member", within: [OrgKind]);

    private static Role ManagerRole => Handles.Role("manager");

    private static Role AgentRole => Handles.Role("agent");

    private static Role SelfRole => Handles.Role("self");

    private static Role VendorRole => Handles.Role("vendor");

    /// <summary>
    /// Which kind one of this file's grants resolves against. The oracle below used to read that
    /// back off the grant itself; a grant carries the handle the policy declared its kind as and
    /// says nothing else about it (D270), so the reading is written here instead — beside the
    /// grants it is the source of, which is what keeps the two from drifting apart.
    /// </summary>
    private enum Held
    {
        Org,
        Vendor,
        Member,
    }

    /// <summary>
    /// One grant as this file declares it: the kind it resolves against, the tenancy or the
    /// individual it names, the role it is held in, and — for a subject grant — the organisation it
    /// is confined to.
    /// </summary>
    private sealed record Holding(Held Kind, int Id, Role Role, int Within = 0);

    /// <summary>A caller: their identifier, and the grants they hold.</summary>
    private sealed record Caller(int User, IReadOnlyList<Holding> Holdings)
    {
        /// <summary>The same caller as the package reads them, in the order declared.</summary>
        internal TenancyPrincipal Principal => new()
        {
            User = User,
            MaskKey = "k",
            Grants = [.. Holdings.Select(Granted)],
        };

        private static Grant Granted(Holding holding) => holding.Kind switch
        {
            Held.Org => Grant.ForTenancy(OrgKind, holding.Id, holding.Role),
            Held.Vendor => Grant.ForTenancy(VendorKind, holding.Id, holding.Role),
            _ => Grant.ForSubject(MemberKind, holding.Id, holding.Role, within: holding.Within),
        };
    }

    /// <summary>A manager in organisation 1.</summary>
    private static Caller Manager => new(1, [new Holding(Held.Org, 1, ManagerRole)]);

    /// <summary>
    /// The principal F66 was found through: one subject grant, confined within an organisation.
    /// Its row predicate folds to <c>member_id = 3 AND org_id = 2</c>, which pins the join key of
    /// statement 08 to a constant — the other half of what the Hep pre-pass could not finish.
    /// </summary>
    private static Caller Participant =>
        new(3, [new Holding(Held.Member, 3, SelfRole, Within: 2)]);

    /// <summary>No grant at all; the creator of orders 3 and 4.</summary>
    private static Caller Creator => new(9, []);

    /// <summary>A vendor grant alone: what it sees of an order comes only through its lines.</summary>
    private static Caller Supplier => new(12, [new Holding(Held.Vendor, 1, VendorRole)]);

    /// <summary>The same vendor grant and an agent grant, so both perspectives meet on order 1.</summary>
    private static Caller SupplierAndAgent => new(
        13, [new Holding(Held.Vendor, 1, VendorRole), new Holding(Held.Org, 1, AgentRole)]);

    public static TheoryData<string> Principals() =>
        new("manager", "participant", "creator", "supplier", "supplier-and-agent");

    private static Caller By(string name) => name switch
    {
        "manager" => Manager,
        "participant" => Participant,
        "creator" => Creator,
        "supplier" => Supplier,
        "supplier-and-agent" => SupplierAndAgent,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "no such principal"),
    };

    // ------------------------------------------------------------------ the oracle

    /// <summary>
    /// Design 38 §8's rule, stated naively over the rows: an order is visible when its organisation
    /// or its member grants it, or its creator is the caller, or some line of it carries a vendor the
    /// principal's vendor grants reach. Nothing here reads a descriptor or a plan.
    /// </summary>
    private static IReadOnlyList<int> VisibleOrders(Caller caller)
    {
        var orgs = Ids(caller, Held.Org);
        var vendors = Ids(caller, Held.Vendor);
        var subjects = Subjects(caller);

        return
        [
            .. Orders
                .Where(o =>
                    orgs.Contains(o.OrgId)
                    || subjects.Contains((o.MemberId, o.OrgId))
                    || o.CreatedBy == caller.User
                    || OrderItems.Any(i => i.OrderId == o.Id && vendors.Contains(i.VendorId)))
                .Select(o => o.Id)
                .Order(),
        ];
    }

    /// <summary>A line is visible along a kind when its parent along that kind is (§8).</summary>
    private static IReadOnlyList<int> VisibleItems(Caller caller)
    {
        var orgs = Ids(caller, Held.Org);
        var vendors = Ids(caller, Held.Vendor);
        var subjects = Subjects(caller);

        return
        [
            .. OrderItems
                .Where(i =>
                {
                    var order = Orders.Single(o => o.Id == i.OrderId);
                    // The kind-less `Through` carries every perspective of the order; the vendor
                    // perspective comes down the line's own vendor instead.
                    return orgs.Contains(order.OrgId)
                        || subjects.Contains((order.MemberId, order.OrgId))
                        || order.CreatedBy == caller.User
                        || vendors.Contains(i.VendorId);
                })
                .Select(i => i.Id)
                .Order(),
        ];
    }

    private static HashSet<int> Ids(Caller caller, Held kind) =>
        [.. caller.Holdings.Where(h => h.Kind == kind).Select(h => h.Id)];

    /// <summary>The individuals this caller holds a grant on, each with its confining org.</summary>
    private static HashSet<(int Member, int Org)> Subjects(Caller caller) =>
        [
            .. caller.Holdings
                .Where(h => h.Kind == Held.Member)
                .Select(h => (Member: h.Id, Org: h.Within)),
        ];

    // ------------------------------------------------------------------ the statements

    /// <summary>
    /// The corpus statement F66 stopped: two entitled tables in one statement, joined on a column a
    /// rule protects. Before the fix this did not finish planning for <c>participant</c>; the planner
    /// suite's <c>HepBudgetTest</c> holds the rule-firing count that says why.
    /// </summary>
    [Theory]
    [MemberData(nameof(Principals))]
    public async Task Statement_08_plans_and_runs_for_every_principal(string name)
    {
        var caller = By(name);
        await using var engine = await EngineAsync();
        var rows = await RunAsync(
            engine,
            "SELECT m.id AS mid, o.id AS oid FROM members m JOIN orders o ON o.member_id = m.id"
                + " ORDER BY m.id, o.id",
            caller);

        // The join is on `orders.member_id`, which the vendor's rule protects: a principal who sees
        // the order but not that column meets the placeholder and matches no member, so what the
        // statement returns is the orders whose member column this principal may read.
        var expected = VisibleOrders(caller)
            .Select(id => Orders.Single(o => o.Id == id))
            .Where(o => ReadsMemberId(caller, o) && VisibleMembers(caller).Contains(o.MemberId))
            .Select(o => $"{o.MemberId}|{o.Id}")
            .Order(StringComparer.Ordinal);

        Assert.Equal(expected, rows.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// §8's first statement: every column of the target, as every principal. The rows are the
    /// oracle's, and what each principal reads of the column the vendor's rule protects is the rule
    /// order §8 asks for — the vendor's rule is written first and stops on match (D222), so a
    /// principal reaching the row along the path meets the placeholder even where an organisation
    /// grant would have shown the value.
    /// </summary>
    [Theory]
    [MemberData(nameof(Principals))]
    public async Task Select_star_from_orders_matches_the_oracle(string name)
    {
        var caller = By(name);
        await using var engine = await EngineAsync();
        var rows = await RunAsync(engine, "SELECT * FROM orders ORDER BY id", caller);

        var fields = rows.Select(r => r.Split('|')).ToArray();
        Assert.Equal(
            VisibleOrders(caller).Select(id => id.ToString()),
            fields.Select(f => f[0]));

        foreach (var row in fields)
        {
            var order = Orders.Single(o => o.Id == int.Parse(row[0]));
            Assert.Equal(
                ReadsMemberId(caller, order) ? order.MemberId.ToString() : "<null>",
                row[1]);
        }
    }

    /// <summary>§8's second: the count over the same rows.</summary>
    [Theory]
    [MemberData(nameof(Principals))]
    public async Task Count_of_orders_matches_the_oracle(string name)
    {
        var caller = By(name);
        await using var engine = await EngineAsync();
        var rows = await RunAsync(engine, "SELECT COUNT(*) FROM orders", caller);

        Assert.Equal([VisibleOrders(caller).Count.ToString()], rows);
    }

    /// <summary>
    /// §8's third: the join of the target to the bridge on the key. A principal that reaches no
    /// vendor at all reads the lines through their order alone, which is §3.13's own shape, and gets
    /// what the oracle says; one that <em>does</em> reach a vendor reads its own lines, which is
    /// what F67 refused until clause 3 learned the folded form of its own key-set join (ADR 0052).
    /// </summary>
    [Theory]
    [MemberData(nameof(Principals))]
    public async Task The_join_to_the_lines_matches_the_oracle(string name)
    {
        var caller = By(name);
        await using var engine = await EngineAsync();
        var rows = await RunAsync(engine, Lines, caller);

        var orders = VisibleOrders(caller).ToHashSet();
        var expected = VisibleItems(caller)
            .Select(id => OrderItems.Single(i => i.Id == id))
            .Where(i => orders.Contains(i.OrderId))
            .Select(i => $"{i.OrderId}|{i.Id}");

        Assert.Equal(expected, rows);
    }

    /// <summary>
    /// The statement F67 refused, as the principals it refused it for. <c>order_items</c> declares
    /// an <c>Inherited</c> path of its own, and for a principal holding one vendor grant the
    /// endpoint predicate pins <c>vendors.id</c> — the endpoint's unique key — to a constant, so the
    /// key set is one row and the optimiser projects that constant in place of the column. The join
    /// is still there and still restricts the lines; what was missing was clause 3's ability to
    /// recognise its own join wearing that shape (ADR 0052). Both principals now plan and answer the
    /// oracle.
    /// </summary>
    [Theory]
    [InlineData("supplier")]
    [InlineData("supplier-and-agent")]
    public async Task The_hand_written_exists_over_the_bridge_matches_the_oracle(string name)
    {
        var caller = By(name);
        await using var engine = await EngineAsync();
        var rows = await RunAsync(engine, Exists, caller);

        Assert.Equal(ExistsOverTheBridge(caller).Select(id => id.ToString()), rows);
    }

    /// <summary>
    /// The oracle for §0's hand-written semi-join, stated over the rows: an order the principal can
    /// see, some line of which the principal can see, carrying a vendor the principal can see.
    /// Every one of the three is the policy's own answer for its own table — the statement joins
    /// three entitled tables and the engine must agree with all three at once.
    /// </summary>
    private static IReadOnlyList<int> ExistsOverTheBridge(Caller caller)
    {
        var lines = VisibleItems(caller).ToHashSet();
        var vendors = Ids(caller, Held.Vendor);
        return
        [
            .. VisibleOrders(caller)
                .Where(id => OrderItems.Any(
                    i => i.OrderId == id && lines.Contains(i.Id) && vendors.Contains(i.VendorId)))
                .Order(),
        ];
    }

    /// <summary>
    /// The same statement as a principal holding no vendor grant. The endpoint leaf folds to
    /// nothing, the sub-query's inner side is pruned with it, and what was left was an INNER
    /// correlate over an empty input that nothing pruned — so planning refused with <i>could not be
    /// decorrelated</i> rather than answering the no rows that are plainly right (F70, ADR 0054).
    /// The two <c>PruneEmptyRules</c> correlate instances the joins run never listed are in the Hep
    /// pre-pass now, and this answers the oracle as every other statement here does.
    /// </summary>
    [Theory]
    [InlineData("manager")]
    [InlineData("participant")]
    [InlineData("creator")]
    public async Task The_hand_written_exists_answers_no_rows_where_the_endpoint_folds_away(
        string name)
    {
        var caller = By(name);
        await using var engine = await EngineAsync();
        var rows = await RunAsync(engine, Exists, caller);

        Assert.Empty(ExistsOverTheBridge(caller));
        Assert.Equal(ExistsOverTheBridge(caller).Select(id => id.ToString()), rows);
    }

    /// <summary>§0's own statement, written by hand over the bridge and the endpoint.</summary>
    private const string Exists =
        "SELECT o.id FROM orders o WHERE EXISTS ("
        + "SELECT 1 FROM order_items i JOIN vendors v ON v.id = i.vendor_id"
        + " WHERE i.order_id = o.id) ORDER BY o.id";

    /// <summary>§8's third statement: the target joined to the bridge on the key.</summary>
    private const string Lines =
        "SELECT o.id, i.id FROM orders o JOIN order_items i ON i.order_id = o.id"
        + " ORDER BY o.id, i.id";

    /// <summary>
    /// The mechanism's own reading, per principal: the target is SOME along the path whenever the
    /// endpoint predicate does not fold to FALSE, and never ALL (§6).
    /// </summary>
    [Fact]
    public async Task A_vendor_reads_the_target_along_the_path_and_never_the_whole_table()
    {
        await using var engine = await EngineAsync();
        var query = await engine
            .WithEntitlements()
            .PrepareAsync("SELECT id FROM orders ORDER BY id", Bind(Supplier.Principal));

        var orders = query.Entitlements.Tables.Single(t => t.Table == "orders");
        Assert.Equal(TableVisibility.Some, orders.Visibility);
    }

    // ------------------------------------------------------------------ what a principal reads

    /// <summary>
    /// Whether this principal reads <c>orders.member_id</c> as the value. The vendor's rule is
    /// written first and grants a comparison and nothing else (D222, D261), so a principal reaching
    /// the row along the path alone meets the placeholder; every other role reaches the rule after
    /// it, which grants the value.
    /// </summary>
    private static bool ReadsMemberId(Caller caller, Order order)
    {
        var vendors = Ids(caller, Held.Vendor);
        var reachedByAVendor =
            OrderItems.Any(i => i.OrderId == order.Id && vendors.Contains(i.VendorId));
        return !reachedByAVendor;
    }

    private static HashSet<int> VisibleMembers(Caller caller)
    {
        var orgs = Ids(caller, Held.Org);
        var subjects = Subjects(caller);
        return
        [
            .. Members
                .Where(m => orgs.Contains(m.OrgId) || subjects.Contains((m.Id, m.OrgId)))
                .Select(m => m.Id),
        ];
    }

    // ------------------------------------------------------------------ the policy

    /// <summary>The schema this file's tables live in, which names their source (D270).</summary>
    private const string SourceName = "main";

    /// <summary>
    /// The fixture's shape, declared over the catalog: every name enters once, here, and every later
    /// mention is the handle it came back as (D270 §1).
    /// </summary>
    private static TenancyPolicy Policy(SchemaDescriptor schema)
    {
        var policy = TenancyPolicy.Declare(new CatalogContext
        {
            ContextId = ContextId,
            Epoch = 1,
            Schemas = [schema],
        });

        var manager = policy.Role("manager");
        var agent = policy.Role("agent");
        var self = policy.Role("self");
        var vendorRole = policy.Role("vendor");
        policy.AllowGlobalGrants();

        var org = policy.Tenancy("org");
        var vendor = policy.Tenancy("vendor");
        var member = policy.Subject("member", within: [org]);

        var source = policy.Source(SourceName);
        var members = source.Table("members");
        var vendors = source.Table("vendors");
        var orderItems = source.Table("order_items");
        var orders = source.Table("orders");

        members.Tenancy(t => t
            .Direct(org, members.Column("org_id"))
            .Direct(member, members.Column("id")));

        vendors.Tenancy(t => t.Direct(vendor, vendors.Column("id")));

        orderItems.Tenancy(t => t
            // For a customer the line inherits its order's every perspective; for a vendor it holds
            // the kind one step down, so a vendor sees the lines that carry its own goods (§8).
            .Through(orderItems.Column("order_id"))
            .Inherited(vendor).Through(vendors));

        orders
            .Tenancy(t => t
                .Direct(org, orders.Column("org_id"))
                .Direct(member, orders.Column("member_id"))
                .ResourceOwner(orders.Column("created_by"))
                .Related(vendor).Through(orderItems).Through(vendors))
            // D216: the moment the vendor's rule names `member_id` the column is protected for every
            // role, so the roles that reach the row any other way say so after it. First match wins
            // and where the host puts a rule is what it means (D222).
            .Access(new AccessRule
            {
                Roles = [vendorRole],
                Column = orders.Column("member_id"),
                Grants = Verdict.Test,
                Tests = [Test.Equals],
            })
            .Access(new AccessRule
            {
                Roles = [manager, agent, self],
                Column = orders.Column("member_id"),
                Grants = Verdict.Full,
            });

        return policy;
    }

    // ------------------------------------------------------------------ the catalog

    private static PocoSource Tables(TenancyEntitlements? entitlements)
    {
        var builder = new PocoSourceBuilder("mem").NamingPolicy(PocoNamingPolicy.SnakeCase);
        builder.AddTable("members", Members, t =>
        {
            t.OrderedBy(m => m.Id).UniqueKey(m => m.Id);
            Attach(t, entitlements, "members");
        });
        builder.AddTable("vendors", Vendors, t =>
        {
            t.OrderedBy(v => v.Id).UniqueKey(v => v.Id);
            Attach(t, entitlements, "vendors");
        });
        builder.AddTable("orders", Orders, t =>
        {
            t.OrderedBy(o => o.Id).UniqueKey(o => o.Id);
            t.ForeignKey(o => o.MemberId).References<Member>(m => m.Id, verify: true);
            Attach(t, entitlements, "orders");
        });
        builder.AddTable("order_items", OrderItems, t =>
        {
            t.OrderedBy(i => i.Id).UniqueKey(i => i.Id);
            t.ForeignKey(i => i.OrderId).References<Order>(o => o.Id, verify: true);
            t.ForeignKey(i => i.VendorId).References<Vendor>(v => v.Id, verify: true);
            Attach(t, entitlements, "order_items");
        });
        return builder.Build();
    }

    private static void Attach<T>(
        PocoTableBuilder<T> table, TenancyEntitlements? entitlements, string name)
        where T : class
    {
        if (entitlements?.For(SourceName, name) is { } descriptor)
        {
            table.Entitlement(descriptor);
        }
    }

    private static readonly Lazy<(PocoSource Source, TenancyEntitlements Entitlements)> Compiled =
        new(() =>
        {
            var schema = Tables(null).DescribeSchema();
            var entitlements = Policy(schema).Compile([schema]);
            return (Tables(entitlements), entitlements);
        });

    private async Task<ChalkEngine> EngineAsync() =>
        await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = ContextId,
            Sources = [Compiled.Value.Source],
            Planner = sidecar.CreatePlanner(),
        });

    private static RequestContext Bind(TenancyPrincipal principal) =>
        Compiled.Value.Entitlements.Bind(principal);

    private static async Task<string[]> RunAsync(
        ChalkEngine engine, string sql, Caller caller)
    {
        var query = await engine.WithEntitlements().PrepareAsync(sql, Bind(caller.Principal));
        var rows = new List<string>();
        await using var execution = await engine.ExecuteAsync(
            query.Query, (IReadOnlyList<object?>?)null);
        await foreach (var batch in execution.Batches)
        {
            using (batch)
            {
                rows.AddRange(BatchReader.ToRows(batch).Select(
                    r => string.Join("|", r.Select(v => v?.ToString() ?? "<null>"))));
            }
        }

        return [.. rows];
    }
}

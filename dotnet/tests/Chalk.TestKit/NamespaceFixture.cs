using Chalk.Catalog;
using Chalk.Entitlements.Tenancy;
using Chalk.Sources.Poco;
using CatalogContext = Chalk.Catalog.CatalogContext;

namespace Chalk.TestKit;

/// <summary>
/// Two sources holding a table called <c>orders</c> and a table called <c>order_items</c> each, under
/// <b>one</b> policy, so that the amended reading of D270 has something to be true of
/// (<c>docs/design/45-typed-tenancy-surface.md</c> §1–§2 as amended 2026-09-16).
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="Table"/> handle is obtained from a <see cref="Source"/> and from nowhere else, and
/// this fixture is the reason: the two <c>orders</c> are told apart by the source they were obtained
/// from and never by their name, so the policy can give each leaf its own rules and neither leaf can
/// be served the other's. The whole family exists to catch the failure where it cannot — where a
/// lookup by bare name hands the first source's descriptor to the second source's read, which looks
/// exactly like a correct plan until a value from the wrong source appears in a result.
/// </para>
/// <para>
/// The two sources are therefore deliberately confusable: the same table names, the same column
/// names, the same identifiers, the same organisations, members, regions and creators. What differs
/// is what a principal is entitled to — the ledger is tenanted by organisation and by member, the
/// archive by region — and what each row carries: the amounts and the notes. Those are the canaries.
/// A number from one source, or a token from one source, appearing anywhere a principal entitled only
/// in the other can see it is the leak this fixture exists to make visible, and it cannot be produced
/// by reading the right source's rows, because no row of one source carries a value of the other.
/// </para>
/// <para>
/// It is a fixture of its own rather than two more tables on <see cref="TenancyFixture"/> or
/// <see cref="TenancyPolicyFixture"/>: those two are held to goldens byte for byte, and a second
/// schema beside them would move every one of them for a property neither family is about.
/// </para>
/// </remarks>
public sealed class NamespaceFixture
{
    /// <summary>Names this catalog to the planner; plans carry it and are refused against another.</summary>
    public const string ContextId = "namespace";

    public const long Epoch = 1;

    /// <summary>
    /// The first source's schema name — what a statement qualifies its tables with, and what
    /// <see cref="TenancyPolicy.Source(string)"/> takes. Being first, it is also the default schema
    /// an unqualified table name resolves in (<c>docs/design/03-planner.md</c> §2).
    /// </summary>
    public const string Ledger = "ledger";

    /// <summary>The second source's schema name, whose tables are reachable only when qualified.</summary>
    public const string Archive = "archive";

    /// <summary>
    /// The identifier of the runtime serving <see cref="Ledger"/>. Distinct from the schema name on
    /// purpose: D270 makes a source handle read by schema name, and a fixture whose two names were
    /// the same string could not tell a mistake there from a success.
    /// </summary>
    public const string LedgerSourceId = "ledger-mem";

    /// <summary>The same for <see cref="Archive"/>.</summary>
    public const string ArchiveSourceId = "archive-mem";

    // ---------------------------------------------------------------- the rows

    /// <summary>
    /// An order, in whichever of the two sources holds it. One row type for both, because two row
    /// types would give the two tables different column sets and the confusion this fixture is about
    /// would be impossible by construction rather than refused by the policy.
    /// </summary>
    public sealed record Order(
        int Id, int OrgId, int MemberId, int RegionId, long Amount, int CreatedBy, string Note);

    /// <summary>A line of an order, which holds no tenancy of its own and inherits its order's.</summary>
    public sealed record OrderItem(int Id, int OrderId, int ItemId, int Quantity, long UnitPrice);

    /// <summary>
    /// The ledger's orders. Two organisations, four members, two regions, four creators — and the
    /// amounts and notes that say which source a value came from.
    /// </summary>
    public static IReadOnlyList<Order> LedgerOrders { get; } =
    [
        new(1, 1, 11, 7, 1001, 100, "LEDGER-NOTE-01"),
        new(2, 1, 12, 8, 1002, 101, "LEDGER-NOTE-02"),
        new(3, 2, 13, 7, 1003, 102, "LEDGER-NOTE-03"),
        new(4, 2, 14, 8, 1004, 103, "LEDGER-NOTE-04"),
    ];

    /// <summary>
    /// The archive's orders: the same identifiers, organisations, members, regions and creators, so
    /// that nothing but the amount and the note can say which table a row was read from.
    /// </summary>
    public static IReadOnlyList<Order> ArchiveOrders { get; } =
    [
        new(1, 1, 11, 7, 2001, 100, "ARCHIVE-NOTE-01"),
        new(2, 1, 12, 8, 2002, 101, "ARCHIVE-NOTE-02"),
        new(3, 2, 13, 7, 2003, 102, "ARCHIVE-NOTE-03"),
        new(4, 2, 14, 8, 2004, 103, "ARCHIVE-NOTE-04"),
    ];

    /// <summary>The ledger's lines, one per order, keyed into the ledger's own orders.</summary>
    public static IReadOnlyList<OrderItem> LedgerOrderItems { get; } =
    [
        new(10, 1, 1, 2, 11),
        new(11, 2, 2, 3, 12),
        new(12, 3, 3, 4, 13),
        new(13, 4, 4, 5, 14),
    ];

    /// <summary>
    /// The archive's lines, over the same order identifiers and keyed into the archive's own orders.
    /// Their own identifiers and unit prices differ, so a line read from the wrong source is visible
    /// in the result rather than only in the plan.
    /// </summary>
    public static IReadOnlyList<OrderItem> ArchiveOrderItems { get; } =
    [
        new(20, 1, 1, 2, 21),
        new(21, 2, 2, 3, 22),
        new(22, 3, 3, 4, 23),
        new(23, 4, 4, 5, 24),
    ];

    /// <summary>
    /// The ledger's note tokens: what a principal entitled only in the archive may never receive, by
    /// any channel.
    /// </summary>
    public static IReadOnlySet<string> LedgerCanaries { get; } =
        LedgerOrders.Select(o => o.Note).ToHashSet(StringComparer.Ordinal);

    /// <summary>The archive's, and the mirror obligation.</summary>
    public static IReadOnlySet<string> ArchiveCanaries { get; } =
        ArchiveOrders.Select(o => o.Note).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// The ledger's amounts, checked by value rather than by output position: an amount a principal
    /// may not read is one the leaf replaced with a placeholder, so the number appearing anywhere at
    /// all is a raw value that escaped, whatever the statement did with it.
    /// </summary>
    public static IReadOnlyList<long> LedgerAmounts { get; } = [.. LedgerOrders.Select(o => o.Amount)];

    /// <summary>The archive's, ranged well clear of the ledger's so neither can be mistaken for it.</summary>
    public static IReadOnlyList<long> ArchiveAmounts { get; } = [.. ArchiveOrders.Select(o => o.Amount)];

    // ---------------------------------------------------------------- the declarations

    /// <summary>
    /// The ledger's schema described without entitlements, which is what the policy is declared and
    /// compiled against before there is anything to attach.
    /// </summary>
    public static SchemaDescriptor LedgerSchema { get; } = LedgerTables(null).DescribeSchema();

    /// <summary>The archive's, the same way.</summary>
    public static SchemaDescriptor ArchiveSchema { get; } = ArchiveTables(null).DescribeSchema();

    /// <summary>
    /// A canonical policy over the two bare schemas, from which the kinds and roles the grants name
    /// are taken. A grant carries the kind and the role it was declared as (D270) and binds by that
    /// name, so principals written against this one policy are bound by any compilation of the same
    /// model — which is what lets <see cref="Principals"/> be static while the fixture is built twice.
    /// </summary>
    public static TenancyPolicy Handles { get; } = Policy(LedgerSchema, ArchiveSchema);

    /// <summary>The organisation a ledger order belongs to. No archive table holds it.</summary>
    public static Kind Org => Handles.Tenancy("org");

    /// <summary>The region an archive order belongs to. No ledger table is restricted by it.</summary>
    public static Kind Region => Handles.Tenancy("region");

    /// <summary>The individual a ledger order is about, confined by either container kind.</summary>
    public static SubjectKind Member => Handles.Subject("member", within: [Org, Region]);

    public static Role Manager => Handles.Role("manager");

    public static Role Auditor => Handles.Role("auditor");

    public static Role Keeper => Handles.Role("keeper");

    public static Role Self => Handles.Role("self");

    /// <summary>
    /// The principals, in the order the goldens record them: one entitled in each source alone, one
    /// in both, one everywhere, one who owns a row of each source and holds no grant at all, one
    /// subject grant, and one auditor.
    /// </summary>
    /// <remarks>
    /// The first two are the family's point. A principal entitled in the ledger alone must receive
    /// nothing of the archive through any channel, and the archive's must receive nothing of the
    /// ledger; every other principal is here so that the two negatives are not the only thing the
    /// goldens record, and so a reviewer can see what a principal entitled in both actually gets.
    /// </remarks>
    public static IReadOnlyList<(string Name, TenancyPrincipal Principal)> Principals { get; } =
    [
        ("ledger-only", Principal(1, Grant.ForTenancy(Org, 1, Manager))),
        ("archive-only", Principal(2, Grant.ForTenancy(Region, 7, Manager))),
        ("both", Principal(
            3, Grant.ForTenancy(Org, 1, Manager), Grant.ForTenancy(Region, 7, Manager))),
        ("global", Principal(4, Grant.Global(Manager))),
        // 100 is the creator of order 1 in *both* sources and holds no grant of any kind, so the
        // resource-owner fail-safe is the only thing that reaches either row.
        ("owner", Principal(100)),
        ("subject", Principal(5, Grant.ForSubject(Member, 11, Self, within: 1))),
        ("auditor-in-ledger", Principal(6, Grant.ForTenancy(Org, 1, Auditor))),
    ];

    /// <summary>One principal: their identifier, their grants, and the key their masks are keyed by.</summary>
    public static TenancyPrincipal Principal(int user, params Grant[] grants) => new()
    {
        User = user,
        Grants = grants,
        MaskKey = "k",
    };

    /// <summary>
    /// One policy over a catalog holding both schemas, the ledger first. Every table is obtained from
    /// the source it belongs to, so the two <c>orders</c> are two declarations and the two
    /// <c>order_items</c> are two more (D270 §1–§2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two <c>orders</c> are given deliberately different rules, and not merely different data.
    /// The ledger's is restricted by organisation, by member and by its creator, and grants a manager
    /// the amount in full while granting an auditor the population aggregates alone; the archive's is
    /// restricted by region and by its creator, and grants the amount in full to the row's owner and a
    /// mask to everybody else. So no assertion about either leaf can pass by reading the other's
    /// rules: the answers differ for every principal that holds anything at all.
    /// </para>
    /// <para>
    /// The archive's two rules are written owner-first because <b>where a rule goes in the list is
    /// what it means</b> (D222): the owner's grant is the one that must survive, and a mask written
    /// ahead of it would take it away.
    /// </para>
    /// <para>
    /// <c>note</c> is in no realm and named by no rule, so it is not a protected column and keeps the
    /// table's default of <c>Full</c> (D159, D216). That is deliberate: a canary a principal is told
    /// they may see is a canary that reports where they actually looked, and one that every rule
    /// redacted would report nothing at all.
    /// </para>
    /// </remarks>
    private static TenancyPolicy Policy(SchemaDescriptor ledger, SchemaDescriptor archive)
    {
        var policy = TenancyPolicy.Declare(new CatalogContext
        {
            ContextId = ContextId,
            Epoch = Epoch,
            Schemas = [ledger, archive],
        });

        var ledgerSource = policy.Source(Ledger);
        var archiveSource = policy.Source(Archive);

        // Every name enters once, here, and every later mention is the handle it came back as.
        var org = policy.Tenancy("org");
        var region = policy.Tenancy("region");
        var member = policy.Subject("member", within: [org, region]);

        var manager = policy.Role("manager");
        var auditor = policy.Role("auditor");
        var keeper = policy.Role("keeper");
        var self = policy.Role("self");
        policy.AllowGlobalGrants();

        var ledgerOrders = ledgerSource.Table("orders");
        var ledgerOrderItems = ledgerSource.Table("order_items");
        var archiveOrders = archiveSource.Table("orders");
        var archiveOrderItems = archiveSource.Table("order_items");

        ledgerOrders
            .Tenancy(t => t
                .Direct(org, ledgerOrders.Column("org_id"))
                .Direct(member, ledgerOrders.Column("member_id"))
                .ResourceOwner(ledgerOrders.Column("created_by")))
            .Access(new AccessRule
            {
                Roles = [manager],
                Column = ledgerOrders.Column("amount"),
                Grants = Verdict.Full,
            })
            .Access(new AccessRule
            {
                Roles = [auditor],
                Column = ledgerOrders.Column("amount"),
                Grants = Verdict.AggregateOnly,
                Aggregates = [Aggregate.Count, Aggregate.Sum],
                MinGroupSize = 2,
            });

        archiveOrders
            .Tenancy(t => t
                .Direct(region, archiveOrders.Column("region_id"))
                .ResourceOwner(archiveOrders.Column("created_by")))
            .Access(new AccessRule
            {
                Roles = [Roles.Owner],
                Column = archiveOrders.Column("amount"),
                Grants = Verdict.Full,
            })
            .Access(new AccessRule
            {
                Roles = [manager, auditor, keeper, self],
                Column = archiveOrders.Column("amount"),
                Grants = Verdict.Mask,
                Mask = Sql.Of("-1"),
            });

        // A line holds no tenancy of its own and inherits its order's — its *own* source's order,
        // which is the whole of what the handle says and what a name alone could not have.
        ledgerOrderItems.Tenancy(t => t.Inherited(org).Through(ledgerOrders));
        archiveOrderItems.Tenancy(t => t.Inherited(region).Through(archiveOrders));

        return policy;
    }

    // ---------------------------------------------------------------- the fixture

    private NamespaceFixture(
        PocoSource ledger, PocoSource archive, TenancyEntitlements entitlements)
    {
        LedgerSource = ledger;
        ArchiveSource = archive;
        Entitlements = entitlements;
        Catalog = new CatalogContext
        {
            ContextId = ContextId,
            Epoch = Epoch,
            Schemas = [ledger.DescribeSchema(), archive.DescribeSchema()],
        };
        CatalogValidator.Validate(Catalog);
    }

    /// <summary>The first source, whose schema is also the catalog's default.</summary>
    public PocoSource LedgerSource { get; }

    /// <summary>The second, reachable from a statement only when its tables are qualified.</summary>
    public PocoSource ArchiveSource { get; }

    /// <summary>The descriptors the policy compiled, one pair per source.</summary>
    public TenancyEntitlements Entitlements { get; }

    /// <summary>The catalog those descriptors are attached to, ledger first.</summary>
    public CatalogContext Catalog { get; }

    /// <summary>One fixture for the whole run; nothing here is mutated by reading it.</summary>
    public static NamespaceFixture Shared => LazyShared.Value;

    private static readonly Lazy<NamespaceFixture> LazyShared = new(() => Create());

    /// <summary>
    /// Builds the fixture: both sources once <b>without</b> entitlements, so the policy has schemas to
    /// be compiled and validated against, then both again with the descriptors the compiler produced
    /// attached to the tables they belong to.
    /// </summary>
    /// <remarks>
    /// The two passes are what makes the attachment provable rather than assumed: a descriptor is
    /// looked up by <c>For(schema, table)</c>, so a table of the wrong source would have to be asked
    /// for by the wrong schema name, and the schema name is the one the source itself describes.
    /// </remarks>
    public static NamespaceFixture Create()
    {
        var ledger = LedgerTables(null).DescribeSchema();
        var archive = ArchiveTables(null).DescribeSchema();
        var entitlements = Policy(ledger, archive).Compile([ledger, archive]);
        return new NamespaceFixture(
            LedgerTables(entitlements), ArchiveTables(entitlements), entitlements);
    }

    private static PocoSource LedgerTables(TenancyEntitlements? entitlements)
    {
        var builder = new PocoSourceBuilder(LedgerSourceId, Ledger)
            .NamingPolicy(PocoNamingPolicy.SnakeCase);

        builder.AddTable("orders", LedgerOrders, t =>
        {
            t.OrderedBy(o => o.Id).UniqueKey(o => o.Id);
            Attach(t, entitlements, Ledger, "orders");
        });

        builder.AddTable("order_items", LedgerOrderItems, t =>
        {
            // Verified, and within this source: a foreign key is one source's claim about a table of
            // its own schema, so this one could not have named the archive's orders even by mistake.
            t.OrderedBy(i => i.Id).UniqueKey(i => i.Id)
                .ForeignKey(i => i.OrderId).References<Order>(o => o.Id, verify: true);
            Attach(t, entitlements, Ledger, "order_items");
        });

        return builder.Build();
    }

    private static PocoSource ArchiveTables(TenancyEntitlements? entitlements)
    {
        var builder = new PocoSourceBuilder(ArchiveSourceId, Archive)
            .NamingPolicy(PocoNamingPolicy.SnakeCase);

        builder.AddTable("orders", ArchiveOrders, t =>
        {
            t.OrderedBy(o => o.Id).UniqueKey(o => o.Id);
            Attach(t, entitlements, Archive, "orders");
        });

        builder.AddTable("order_items", ArchiveOrderItems, t =>
        {
            t.OrderedBy(i => i.Id).UniqueKey(i => i.Id)
                .ForeignKey(i => i.OrderId).References<Order>(o => o.Id, verify: true);
            Attach(t, entitlements, Archive, "order_items");
        });

        return builder.Build();
    }

    /// <summary>
    /// Attaches the descriptor the policy compiled for <paramref name="schema"/>'s
    /// <paramref name="name"/>, and nothing else. The schema name is half the key, which is the whole
    /// of D270's amendment in one call.
    /// </summary>
    private static void Attach<T>(
        PocoTableBuilder<T> table, TenancyEntitlements? entitlements, string schema, string name)
        where T : class
    {
        if (entitlements?.For(schema, name) is { } descriptor)
        {
            table.Entitlement(descriptor);
        }
    }
}

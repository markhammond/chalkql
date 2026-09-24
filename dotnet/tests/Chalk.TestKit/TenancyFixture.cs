using Chalk.Catalog;
using Chalk.Entitlements;
using Chalk.Client;
using Chalk.Sources.Poco;
using CatalogContext = Chalk.Catalog.CatalogContext;

namespace Chalk.TestKit;

/// <summary>
/// The <c>tenancy</c> fixture of <c>docs/design/16-entitlements.md</c> §8, on the neutral domain and
/// over POCO collections, with the entitlements written <b>directly as descriptors</b>.
/// </summary>
/// <remarks>
/// <para>
/// This is §8's "layer A alone": a test host that never references a policy package and writes the
/// row predicate and the disclosure rules itself, which is the only way to exercise the core's own
/// vocabulary without the compiler that will eventually produce it. Its Java twin is
/// <c>chalk.planner.TenancyCatalogs</c>, table for table and rule for rule.
/// </para>
/// <para>
/// Six principals, as §8 names them: <c>u1</c> a manager in O1 and an agent in O2, <c>u2</c> an
/// agent in O1 only, <c>u3</c> a subject grant on their own member id confined to O1, <c>u4</c> a
/// global grant, <c>u5</c> no grants at all, <c>u6</c> an auditor in O1. Each is a
/// <see cref="RequestContext"/> the test host builds by hand — lists of organization identifiers,
/// pairs for a confined subject, and three scalars.
/// </para>
/// </remarks>
public sealed class TenancyFixture
{
    public const string ContextId = "tenancy";

    public const long Epoch = 1;

    /// <summary>The default group-size floor the fixture's aggregates are guarded by.</summary>
    public const int MinGroupSize = 3;

    private TenancyFixture(PocoSource source)
    {
        Source = source;
        Catalog = new CatalogContext
        {
            ContextId = ContextId,
            Epoch = Epoch,
            Schemas = [source.DescribeSchema()],
        };
        CatalogValidator.Validate(Catalog);
    }

    public PocoSource Source { get; }

    public CatalogContext Catalog { get; }

    /// <summary>The same tables with no entitlement at all, for the zero-cost comparison (§0).</summary>
    /// <remarks>
    /// Lazily, because a static field initialiser runs in declaration order and the rows this builds
    /// from are declared below it; a reader should be able to put the tables where they read best.
    /// </remarks>
    public static TenancyFixture Unentitled => LazyUnentitled.Value;

    public static TenancyFixture Shared => LazyShared.Value;

    /// <summary>
    /// The same tables and the same descriptors with <see cref="Functions"/> declared beside them:
    /// the fixture the adversarial family runs against (D251 class 4).
    /// </summary>
    /// <remarks>
    /// A fixture of its own rather than two more declarations on <see cref="Shared"/>, because a
    /// client-bodied function must have a body registered on every engine over the catalog that
    /// declares it — and §0's zero-cost property is measured against <see cref="Unentitled"/>'s
    /// catalog byte for byte, which a wider schema would move.
    /// </remarks>
    public static TenancyFixture Subversion => LazySubversion.Value;

    private static readonly Lazy<TenancyFixture> LazyUnentitled =
        new(() => Create(entitled: false));

    private static readonly Lazy<TenancyFixture> LazyShared = new(() => Create(entitled: true));

    private static readonly Lazy<TenancyFixture> LazySubversion =
        new(() => Create(
            entitled: true,
            subversionFunctions: true,
            amountAggregates: SubversionAmountAggregates,
            compositeTable: true));

    /// <summary>
    /// What <c>amount</c> permits beyond the four built-ins on the fixtures that declare
    /// <see cref="Functions"/>: <c>population_summary</c>, which its host declares population-safe
    /// (D295), so that the adversarial family's statement 66 is the permitted twin of statement 64.
    /// A fixture that does not declare the function cannot name it, and its catalog would be refused.
    /// </summary>
    public static readonly IReadOnlyList<string> SubversionAmountAggregates = ["POPULATION_SUMMARY"];

    // ---------------------------------------------------------------- the rows

    public sealed record Org(int Id, string Name);

    public sealed record Member(
        int Id, int OrgId, string FirstName, string LastName, string NationalId, string Postcode);

    public sealed record Order(
        int Id, int MemberId, int OrgId, long Amount, string Note, int CreatedBy, int RegionId);

    /// <summary>
    /// A geographic region: the second tenancy kind, and the one a grant is <em>confined</em> along
    /// rather than held on (D266 §6). It is on an order's row and on nothing else, which is what
    /// makes a confined grant reach <c>orders</c> and never a member's own row (D266 §3).
    /// </summary>
    public sealed record Region(int Id, string Name);

    public sealed record Note(int Id, int OrgId, int CreatedBy, string Body);

    public sealed record Invite(int Id, int OrgId, string Target);

    public sealed record Symbol(string Name, string Base);

    /// <summary>A conversation: it carries the tenancy, and the member it is about (§3.13).</summary>
    public sealed record Thread(int Id, int OrgId, int MemberId);

    /// <summary>
    /// A message in a thread. It carries <b>no</b> tenancy column at all: which principal may see it
    /// derives entirely from its thread, which is what <c>Through</c> is for.
    /// </summary>
    public sealed record Message(int Id, int ThreadId, string Content, DateTime? FirstViewedAt);

    /// <summary>An attachment on a message: two hops from a tenancy column, which is the chain.</summary>
    public sealed record Attachment(int Id, int MessageId, string Name);

    /// <summary>
    /// A vendor: the second <em>tenancy</em> kind of design 38 §8, and the one a row of
    /// <c>orders</c> holds along a <c>Related</c> path rather than on its own row. The key is the
    /// row's own identifier, so <c>vendors</c> is where the kind lives <c>Direct</c>ly.
    /// </summary>
    public sealed record Vendor(int Id, string Name);

    /// <summary>
    /// A catalogue item, supplied by exactly one vendor: the <b>endpoint</b> of the path, where the
    /// tenancy key is on the row (<c>Direct("vendor", "vendor_id")</c>).
    /// </summary>
    public sealed record Item(int Id, int VendorId, string Name);

    /// <summary>
    /// An order line: the <b>bridge</b>. It contributes existence and nothing else to an order's
    /// vendor perspective (design 38 §0), while carrying its own organisation and member down from
    /// its order and its vendor down from its item — kind by kind, which is what makes a vendor see
    /// the lines that carry its own goods and no other line of an order it can see (§8).
    /// </summary>
    public sealed record OrderItem(int Id, int OrderId, int ItemId, int Quantity, long UnitPrice);

    /// <summary>
    /// Three regions. O1's orders lie in R1 and R2, so an organisation grant confined to one region
    /// reaches half of them and the adversarial family can ask for the other half (D266 §6).
    /// </summary>
    public static IReadOnlyList<Region> Regions { get; } =
    [
        new(1, "North"),
        new(2, "South"),
        new(3, "East"),
    ];

    /// <summary>Three organisations; O3 is the one nobody in the fixture holds a grant on.</summary>
    public static IReadOnlyList<Org> Orgs { get; } =
    [
        new(1, "Northwind"),
        new(2, "Southerly"),
        new(3, "Eastward"),
    ];

    /// <summary>
    /// Two members per organization, so a group of two is below the floor of three — and one more in
    /// O2 who shares a first name with a member of O1, which is §8's query 19: an auditor's
    /// fingerprint mask is keyed and stable, so an equality join on the token matches those two rows
    /// across the tenancy boundary without either name being disclosed. Without a shared name that
    /// query would assert nothing but reflexivity.
    /// </summary>
    /// <remarks>
    /// Every protected value carries a canary (D252, <c>32-adversarial-entitlements.md</c> §2),
    /// placed after the initial the mask discloses. The two <c>Tara</c>s carry the <em>same</em>
    /// token, because the token stands for the value: giving each row its own would have made the
    /// fingerprint join above assert nothing but reflexivity, which is what the shared name is for.
    /// </remarks>
    public static IReadOnlyList<Member> Members { get; } =
    [
        new(1, 1, FirstName("Tara", 1), LastName("Ng", 1), NationalId("AA-1", 1), "2000"),
        new(2, 1, FirstName("Bo", 2), LastName("Ito", 2), NationalId("AA-2", 2), "2001"),
        new(3, 2, FirstName("Tomas", 3), LastName("Ek", 3), NationalId("BB-1", 3), "3000"),
        new(4, 2, FirstName("Ada", 4), LastName("Roy", 4), NationalId("BB-2", 4), "3001"),
        new(5, 3, FirstName("Kim", 5), LastName("Zhao", 5), NationalId("CC-1", 5), "4000"),
        new(6, 3, FirstName("Lea", 6), LastName("Fox", 6), NationalId("CC-2", 6), "4001"),
        new(7, 2, FirstName("Tara", 1), LastName("Vo", 7), NationalId("BB-3", 7), "3002"),
    ];

    /// <summary>
    /// A member's contact card: the composite member of <c>profiles</c> (D302), a record whose email and
    /// phone carry canaries, and whose tier a rule condition reads as a field.
    /// </summary>
    public sealed record ContactCard(string Email, string? Phone, int Tier);

    /// <summary>A profile: one contact card per row, or none.</summary>
    public sealed record Profile(int Id, int OrgId, ContactCard? Contact);

    /// <summary>
    /// The composite-column table of the adversarial family (D302): two profiles per organisation,
    /// the tiers spread so that an agent's rule over the <c>tier</c> field discloses some cards of an
    /// organisation and withholds others, and one profile with no card at all.
    /// </summary>
    public static IReadOnlyList<Profile> Profiles { get; } =
    [
        new(1, 1, new ContactCard(Email("tara@o1", 1), Phone("+64 1", 1), 1)),
        new(2, 1, new ContactCard(Email("bo@o1", 2), Phone("+64 2", 2), 3)),
        new(3, 2, new ContactCard(Email("tomas@o2", 3), null, 2)),
        new(4, 2, null),
        new(5, 3, new ContactCard(Email("kim@o3", 5), Phone("+64 5", 5), 1)),
        new(6, 3, new ContactCard(Email("lea@o3", 6), Phone("+64 6", 6), 2)),
    ];

    private static string Email(string value, int ordinal) => TenancyCanaries.Mark(value, "EMAIL", ordinal);

    private static string Phone(string value, int ordinal) => TenancyCanaries.Mark(value, "PHONE", ordinal);

    private static string FirstName(string value, int ordinal) =>
        TenancyCanaries.Mark(value, "FIRST", ordinal);

    private static string LastName(string value, int ordinal) =>
        TenancyCanaries.Mark(value, "LAST", ordinal);

    private static string NationalId(string value, int ordinal) =>
        TenancyCanaries.Mark(value, "NID", ordinal);

    /// <summary>
    /// Four orders in O1 and two in O2, so O1's group clears the floor of three and O2's does not —
    /// which is §8's query 13. Order 7 is in O3 and was created by <c>u1</c>, which is the
    /// created-by fail-safe.
    /// </summary>
    /// <remarks>
    /// The amount is the numeric canary of D252 and the note the string one: the amounts are far
    /// apart and deliberately not an arithmetic progression, so that a mean of two a principal may
    /// see never lands on a third they may not (<see cref="TenancyCanaries.Amounts"/>).
    /// </remarks>
    public static IReadOnlyList<Order> Orders { get; } =
    [
        new(1, 1, 1, TenancyCanaries.Amounts[0], OrderNote("n1", 1), 1, 1),
        new(2, 1, 1, TenancyCanaries.Amounts[1], OrderNote("n2", 2), 1, 2),
        new(3, 2, 1, TenancyCanaries.Amounts[2], OrderNote("n3", 3), 1, 1),
        new(4, 2, 1, TenancyCanaries.Amounts[3], OrderNote("n4", 4), 2, 2),
        new(5, 3, 2, TenancyCanaries.Amounts[4], OrderNote("n5", 5), 3, 1),
        new(6, 4, 2, TenancyCanaries.Amounts[5], OrderNote("n6", 6), 3, 3),
        new(7, 5, 3, TenancyCanaries.Amounts[6], OrderNote("n7", 7), 1, 3),
    ];

    private static string OrderNote(string value, int ordinal) =>
        TenancyCanaries.Mark(value, "NOTE", ordinal);

    /// <summary>Two notes in O1 created by <c>u5</c>, who holds no grant — §8's query 25.</summary>
    public static IReadOnlyList<Note> Notes { get; } =
    [
        new(1, 1, 5, NoteBody("first", 1)),
        new(2, 1, 5, NoteBody("second", 2)),
        new(3, 2, 1, NoteBody("third", 3)),
    ];

    private static string NoteBody(string value, int ordinal) =>
        TenancyCanaries.Mark(value, "BODY", ordinal);

    /// <summary>The <c>Custom</c>-mode table, whose predicate reads a host-computed list.</summary>
    public static IReadOnlyList<Invite> Invites { get; } =
    [
        new(1, 1, "a@example.test"),
        new(2, 2, "b@example.test"),
    ];

    /// <summary>Unrestricted reference data: visible with no grants at all (§8's query 15).</summary>
    public static IReadOnlyList<Symbol> Symbols { get; } =
    [
        new("EURUSD", "EUR"),
        new("GBPUSD", "GBP"),
    ];

    /// <summary>
    /// Two threads in O1, one in O2 about member 3 — which is the one a subject grant reaches — and
    /// one in O3, which nobody but a global grant sees.
    /// </summary>
    public static IReadOnlyList<Thread> Threads { get; } =
    [
        new(1, 1, 1),
        new(2, 1, 2),
        new(3, 2, 3),
        new(4, 3, 5),
    ];

    /// <summary>
    /// Five messages over those four threads. Nothing on the row says which organization it belongs
    /// to: the thread does, and the join is the whole of the enforcement (§3.13).
    /// </summary>
    /// <remarks>
    /// The canary sits past the ninth character, which is what the excerpt mask discloses, so the
    /// three O1 messages still share the excerpt <c>northwind</c> they shared before.
    /// </remarks>
    public static IReadOnlyList<Message> Messages { get; } =
    [
        new(1, 1, MessageContent("northwind opening", 1), new DateTime(2024, 1, 1, 9, 0, 0, DateTimeKind.Unspecified)),
        new(2, 1, MessageContent("northwind reply", 2), null),
        new(3, 2, MessageContent("northwind second thread", 3), null),
        new(4, 3, MessageContent("southerly opening", 4), new DateTime(2024, 2, 2, 10, 0, 0, DateTimeKind.Unspecified)),
        new(5, 4, MessageContent("eastward opening", 5), null),
    ];

    private static string MessageContent(string value, int ordinal) =>
        TenancyCanaries.Mark(value, "CONTENT", ordinal);

    /// <summary>Three attachments: a message's own visibility, one hop further out.</summary>
    public static IReadOnlyList<Attachment> Attachments { get; } =
    [
        new(1, 1, "northwind-a"),
        new(2, 3, "northwind-b"),
        new(3, 4, "southerly-a"),
    ];

    /// <summary>Two vendors, so that one order can carry the goods of both (design 38 §8).</summary>
    public static IReadOnlyList<Vendor> Vendors { get; } =
    [
        new(1, "Acme"),
        new(2, "Borealis"),
    ];

    /// <summary>
    /// Three catalogue items over the two vendors: two of V1 and one of V2, so a line tells one
    /// vendor's perspective from the other's.
    /// </summary>
    public static IReadOnlyList<Item> Items { get; } =
    [
        new(1, 1, "widget"),
        new(2, 1, "gadget"),
        new(3, 2, "sprocket"),
    ];

    /// <summary>
    /// Five order lines over the fixture's own orders, chosen so that every claim design 38 §8
    /// makes is a row rather than a coincidence.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Order 1 carries a line of each vendor, so <b>both</b> vendors reach it and a principal
    /// holding an organisation grant on O1 meets it from both sides at once. Order 5 is in O2 and
    /// order 7 in O3 — <b>no grant of the fixture reaches O3</b> except the global one — so a V1
    /// line on order 7 is the assertion that a row becomes visible through the rows that belong to
    /// it and by no other route. Orders 3, 4 and 6 carry no line at all, so a vendor reaches them by
    /// no path: a row related to zero tenants is invisible along that axis (§4).
    /// </para>
    /// <para>
    /// <c>unit_price</c> is what a vendor sees as a placeholder and <c>quantity</c> what it sees in
    /// full, so one statement over the lines shows both halves of §8's column rules.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<OrderItem> OrderItems { get; } =
    [
        new(1, 1, 1, 2, 50),
        new(2, 1, 3, 4, 30),
        new(3, 2, 3, 1, 200),
        new(4, 5, 2, 3, 70),
        new(5, 7, 1, 1, 90),
    ];

    // ---------------------------------------------------------------- the descriptors

    /// <summary>
    /// A member's row predicate: any organization this principal holds a grant in, the subject
    /// grants confined to one, and the global wildcard.
    /// </summary>
    private const string MemberRows =
        "org_id IN (@ctx.manager_orgs) OR org_id IN (@ctx.agent_orgs) "
        + "OR org_id IN (@ctx.auditor_orgs) OR org_id IN (@ctx.support_orgs) "
        + "OR org_id IN (@ctx.counter_orgs) OR (id, org_id) IN (@ctx.subject_pairs) OR @ctx.global";

    /// <summary>
    /// An order's rows, and the two <b>conjoined</b> groups of D266 beside them: an organisation
    /// grant confined to one region is a pair over <c>(org_id, region_id)</c>, and a subject grant
    /// confined to an organisation <em>and</em> a region is a triple. All of a group's columns must
    /// match at once, which is the whole of the conjunction (design 40 §1, §4).
    /// </summary>
    private const string OrderRows =
        "org_id IN (@ctx.manager_orgs) OR org_id IN (@ctx.agent_orgs) "
        + "OR org_id IN (@ctx.auditor_orgs) OR org_id IN (@ctx.support_orgs) "
        + "OR org_id IN (@ctx.counter_orgs) OR (member_id, org_id) IN (@ctx.subject_pairs) "
        + "OR (org_id, region_id) IN (@ctx.auditor_org_regions) "
        + "OR (member_id, org_id, region_id) IN (@ctx.subject_org_regions) "
        + "OR @ctx.global OR created_by = @ctx.user";

    /// <summary>
    /// The <b>organisation kind's</b> reach over an order's rows: one membership per role, the
    /// region-confined auditor's group among them, and the global grant. It is the endpoint
    /// predicate of the line's <c>Inherited("org")</c> path, and §2 is exact about what that is —
    /// "in §2's vocabulary over the endpoint table's columns … the same shape a <c>Direct</c>
    /// dimension compiles to", which is the <em>kind's</em> membership and nothing else.
    /// </summary>
    /// <remarks>
    /// So the creator's fail-safe is deliberately not here, and the subject's grants are in
    /// <see cref="OrderSubject"/> beside it as the <c>member</c> kind's own path: "a line is
    /// visible along a kind when its parent along that kind is" (§8) is a statement about kinds,
    /// and a row admitted by <c>created_by</c> is admitted by no kind at all. It is what the
    /// compiled policy writes for the same declaration, and the two layers are compared on it.
    /// </remarks>
    private const string OrderOrgRows =
        "org_id IN (@ctx.manager_orgs) OR org_id IN (@ctx.agent_orgs) "
        + "OR org_id IN (@ctx.auditor_orgs) OR org_id IN (@ctx.support_orgs) "
        + "OR org_id IN (@ctx.counter_orgs) "
        + "OR (org_id, region_id) IN (@ctx.auditor_org_regions) "
        + "OR @ctx.global";

    /// <summary>The auditor's scope on an order, including the region-confined half (D266).</summary>
    private const string OrderAuditor =
        "org_id IN (@ctx.auditor_orgs) OR (org_id, region_id) IN (@ctx.auditor_org_regions)";

    /// <summary>The subject's, the same way: confined to an organisation, or to both at once.</summary>
    private const string OrderSubject =
        "(member_id, org_id) IN (@ctx.subject_pairs) "
        + "OR (member_id, org_id, region_id) IN (@ctx.subject_org_regions)";

    /// <summary>
    /// The endpoint predicate of every path that ends at a vendor's own catalogue: the kind is on
    /// <c>items.vendor_id</c>, which is where design 38 §8 puts it <c>Direct</c>ly.
    /// </summary>
    private const string ItemRows = "vendor_id IN (@ctx.vendor_vendor) OR @ctx.global";

    /// <summary>The same for the endpoint of a one-step path: a vendor's own row.</summary>
    private const string VendorRows = "id IN (@ctx.vendor_vendor) OR @ctx.global";

    /// <summary>
    /// A vendor's reach on an order, decided at the <b>endpoint</b> of the <c>Related</c> path
    /// (design 38 §2, §4): a rule condition on the target may name the endpoint's columns exactly as
    /// a child's may name its parent's, and the ordinal it produces is evaluated at the endpoint's
    /// cardinality and <c>MIN</c>-ed over the rows that reach one order.
    /// </summary>
    private const string OrderVendor = "items.vendor_id IN (@ctx.vendor_vendor)";

    /// <summary>
    /// The other route into an order line: through the order itself, whose columns a rule on the
    /// line may name because the order is the endpoint of the line's own <c>org</c> and
    /// <c>member</c> paths (§2). It is <see cref="OrderOrgRows"/> and <see cref="OrderSubject"/>
    /// written over the endpoint's row.
    /// </summary>
    private const string LineThroughItsOrder =
        "orders.org_id IN (@ctx.manager_orgs) OR orders.org_id IN (@ctx.agent_orgs) "
        + "OR orders.org_id IN (@ctx.auditor_orgs) OR orders.org_id IN (@ctx.support_orgs) "
        + "OR orders.org_id IN (@ctx.counter_orgs) "
        + "OR (orders.org_id, orders.region_id) IN (@ctx.auditor_org_regions) "
        + "OR @ctx.global "
        + "OR (orders.member_id, orders.org_id) IN (@ctx.subject_pairs) "
        + "OR (orders.member_id, orders.org_id, orders.region_id) IN (@ctx.subject_org_regions)";

    private const string NoteRows =
        "org_id IN (@ctx.manager_orgs) OR org_id IN (@ctx.agent_orgs) OR @ctx.global "
        + "OR created_by = @ctx.user";

    /// <summary>
    /// A protected column: full where the principal manages, masked where they act as an agent, the
    /// fingerprint token where they audit, and nothing anywhere else (D196, first match wins).
    /// </summary>
    private static ColumnEntitlementDescriptor Protected(
        int column, string name, bool statistical = false, int floor = 0, string? mask = null) => new()
    {
        Column = column,
        Mask = mask ?? $"SUBSTRING({name}, 1, 1)",
        Statistical = statistical,
        MinGroupSize = floor,
        Rules =
        [
            new DisclosureRule
            {
                When = "org_id IN (@ctx.manager_orgs) OR @ctx.global",
                Then = Disclosure.Full,
            },
            new DisclosureRule
            {
                When = "org_id IN (@ctx.agent_orgs)",
                Then = Disclosure.Masked,
            },
            new DisclosureRule
            {
                When = "org_id IN (@ctx.auditor_orgs)",
                Then = Disclosure.Masked,
                // The per-rule mask (D196): an agent sees the initial, an auditor sees a token.
                // FINGERPRINT is keyed and stable, so an auditor's equality join on the token joins
                // masked rows across tenancies without either name being disclosed (§8's query 19).
                Mask = $"FINGERPRINT({name}, @ctx.mask_key)",
            },
        ],
        Otherwise = Disclosure.None,
    };

/// <summary>The descriptor, so the ADO variant of the fixture attaches exactly the same one.</summary>
    /// <param name="firstNameMask">
    /// The agent's mask for <c>first_name</c>, in place of the initial written as <c>SUBSTRING</c>.
    /// The adversarial battery writes the same initial as a field of a composite-valued function
    /// (ADR 0077), which is a mask a consumer can only see if the leaf applied it.
    /// </param>
    public static TableEntitlementDescriptor MembersEntitlement(
        bool statisticalFirstName = false, int floor = 0, string? firstNameMask = null) => new()
    {
        RowPredicate = MemberRows,
        Columns =
        [
            Protected(2, "first_name", statisticalFirstName, floor, firstNameMask),
            Protected(3, "last_name"),
            // `restricted`: NOT NULL in the catalog and disclosed to nobody — which is what makes
            // the placeholder widen an output type — and, for two roles, testable without being
            // readable (D261, docs/design/36-test-verdict.md §1). A support desk may confirm an
            // identifier it was told; a counter may count how many of the rows it can see match one,
            // guarded by the floor. Every other principal's `support_orgs` and `counter_orgs` are
            // empty, so both conditions fold to FALSE and the column is the placeholder it was.
            new ColumnEntitlementDescriptor
            {
                Column = 4,
                AggregateOnlyFunctions = ["COUNT"],
                MinGroupSize = MinGroupSize,
                Rules =
                [
                    new DisclosureRule
                    {
                        When = "org_id IN (@ctx.support_orgs)",
                        Then = Disclosure.Test,
                        Tests = [TestShape.Equals, TestShape.NotEquals, TestShape.In],
                    },
                    new DisclosureRule
                    {
                        When = "org_id IN (@ctx.counter_orgs)",
                        Then = Disclosure.AggregateOnly,
                        Tests = [TestShape.Equals],
                    },
                ],
                Otherwise = Disclosure.None,
            },
        ],
    };

    /// <summary>A profile's rows: any organisation this principal manages, acts in or audits, and the global grant.</summary>
    private const string ProfileRows =
        "org_id IN (@ctx.manager_orgs) OR org_id IN (@ctx.agent_orgs) "
        + "OR org_id IN (@ctx.auditor_orgs) OR @ctx.global";

    /// <summary>
    /// The composite column's policy (D302): disclosed whole or withheld whole. A manager — and the
    /// global grant — sees the card; an agent sees it only where the card's own <c>tier</c> field is 2
    /// or more, a condition over a field of the column it decides; everyone else gets the NULL composite.
    /// </summary>
    public static TableEntitlementDescriptor ProfilesEntitlement() => new()
    {
        RowPredicate = ProfileRows,
        Columns =
        [
            new ColumnEntitlementDescriptor
            {
                Column = 2,
                Rules =
                [
                    new DisclosureRule { When = "org_id IN (@ctx.manager_orgs) OR @ctx.global", Then = Disclosure.Full },
                    new DisclosureRule
                    {
                        When = "org_id IN (@ctx.agent_orgs) AND (contact).tier >= 2",
                        Then = Disclosure.Full,
                    },
                ],
                Otherwise = Disclosure.None,
            },
        ],
    };

/// <summary>The same, for orders — the population-only column and the constant mask.</summary>
    /// <remarks>
    /// D265 clause (h) adds the <c>Related</c> path: an order is also visible to a vendor some line
    /// of it carries an item of — one step <b>up</b> to the bridge, one <b>down</b> to the endpoint,
    /// and the endpoint's own predicate at the far end (design 38 §8). The two columns the vendor's
    /// rules name are protected for every role from that moment (D216), so each carries the three
    /// rules §8 asks for in the order it asks for them: the organisation's own reach first, so a
    /// principal holding both grants sees the value; the vendor's second, so one reaching the row
    /// along the path alone does not; and a catch-all last.
    /// </remarks>
    /// <param name="itemSchema">
    /// Where <c>items</c> lives. Empty is this table's own schema, which is the ordinary case;
    /// naming one is §8's <b>two-source</b> layout — the target and the bridge in the database and
    /// the endpoint in process — and the planner then reaches the endpoint's keys through M5's
    /// strategies rather than through one remote query (§5, D229).
    /// </param>
    /// <param name="amountAggregates">
    /// Population aggregates <c>amount</c> would permit beyond the four built-ins. Empty but for the
    /// fixtures that declare <see cref="Functions"/>, which name <c>population_summary</c>
    /// (<see cref="SubversionAmountAggregates"/>, D295); the adversarial battery names
    /// <c>amount_summary</c> here to show that registration refuses a user-defined aggregate its host
    /// has not declared population-safe (D190).
    /// </param>
    public static TableEntitlementDescriptor OrdersEntitlement(
        string itemSchema = "", IReadOnlyList<string>? amountAggregates = null) => new()
    {
        RowPredicate = OrderRows,
        Inherited =
        [
            new InheritedVisibilityDescriptor
            {
                Kind = "vendor",
                Steps =
                [
                    // Up, to the bridge: the lines that name this order.
                    new VisibilityStepDescriptor
                    {
                        Table = "order_items",
                        FromColumn = 0,
                        ToColumn = 1,
                        Direction = StepDirection.ToChild,
                    },
                    // Down, to the endpoint: the item each line carries.
                    ToTheItem(itemSchema),
                ],
                EndpointPredicate = ItemRows,
                EndpointSchema = itemSchema,
                EndpointTable = "items",
            },
        ],
        Columns =
        [
            // D265 clause (h), design 38 §8. What a vendor reaching an order along the path may
            // have of the two columns that say whose order it is: the member as a column it may
            // **test** for equality and never read (D261 in its natural home), and the organisation
            // as a placeholder. Reaching a row through the rows that belong to it discloses the
            // row's existence and whatever its own rules say, never more.
            //
            // The order is what keeps every other principal byte-identical (D216, D222). The first
            // rule's condition is the table's own row predicate — every way into an order that is
            // not the path's, the creator's fail-safe included — so for a principal holding no
            // vendor grant it is exactly what `Filter_R` folds to and the fold reads the column as
            // FULL outright, as it did before these rules existed. The vendor's rule is second, and
            // its condition is the *endpoint's*: evaluated at the endpoint's cardinality and MIN-ed
            // over the rows that reach one order (§4, D228). A principal holding both grants meets
            // the first rule on an order its organisation reaches and the second only on the orders
            // it reaches along the path alone.
            new ColumnEntitlementDescriptor
            {
                Column = 1,
                Rules =
                [
                    new DisclosureRule { When = OrderRows, Then = Disclosure.Full },
                    new DisclosureRule
                    {
                        When = OrderVendor,
                        Then = Disclosure.Test,
                        Tests = [TestShape.Equals],
                    },
                ],
                Otherwise = Disclosure.None,
            },
            new ColumnEntitlementDescriptor
            {
                Column = 2,
                Rules =
                [
                    new DisclosureRule { When = OrderRows, Then = Disclosure.Full },
                    new DisclosureRule { When = OrderVendor, Then = Disclosure.None },
                ],
                Otherwise = Disclosure.None,
            },
            new ColumnEntitlementDescriptor
            {
                Column = 3,
                AggregateOnlyFunctions = ["COUNT", "SUM", "SUM0", "AVG", .. amountAggregates ?? []],
                MinGroupSize = MinGroupSize,
                // D216: `amount` is protected — a rule names it — so a role that says nothing
                // about it grants nothing. The owner sees what they wrote, the auditor gets the
                // population aggregates, and everybody else gets the placeholder.
                Rules =
                [
                    new DisclosureRule
                    {
                        When = "created_by = @ctx.user",
                        Then = Disclosure.Full,
                    },
                    new DisclosureRule
                    {
                        When = OrderAuditor,
                        Then = Disclosure.AggregateOnly,
                    },
                ],
                Otherwise = Disclosure.None,
            },
            new ColumnEntitlementDescriptor
            {
                Column = 4,
                Mask = "'********'",
                Rules =
                [
                    new DisclosureRule
                    {
                        When = "org_id IN (@ctx.manager_orgs) OR @ctx.global "
                            + "OR created_by = @ctx.user "
                            + "OR " + OrderSubject,
                        Then = Disclosure.Full,
                    },
                    new DisclosureRule
                    {
                        When = "org_id IN (@ctx.agent_orgs) OR " + OrderAuditor,
                        Then = Disclosure.Masked,
                    },
                ],
                Otherwise = Disclosure.None,
            },
        ],
    };

    /// <summary>A vendor's own row, and nothing else: the kind is the identifier (design 38 §8).</summary>
    public static TableEntitlementDescriptor VendorsEntitlement() => new()
    {
        RowPredicate = VendorRows,
    };

    /// <summary>
    /// The endpoint of every vendor path: the key is on the row, so <c>items</c> is where the kind
    /// lives <c>Direct</c>ly and what an order's path and a line's path both end at.
    /// </summary>
    public static TableEntitlementDescriptor ItemsEntitlement() => new()
    {
        RowPredicate = ItemRows,
    };

    /// <summary>
    /// The bridge, entitled <b>by kind</b> (design 38 §8): a line inherits its organisation and its
    /// member from its order and its vendor from its item, so a vendor sees the lines that carry its
    /// own goods and no other line of an order it can see. The kind-less <c>Through</c> would have
    /// given it every line of that order, and stays exercised by messages and attachments.
    /// </summary>
    /// <remarks>
    /// Three restrictions, OR-ed as restrictions always are. <c>unit_price</c> is the one column the
    /// vendor does not read: its rules name the two routes in the order §8 gives them, so a
    /// principal reaching the line through its order sees the value and one reaching it through its
    /// item does not. <c>quantity</c> carries no rule at all and is therefore full for everyone that
    /// can see the row.
    /// </remarks>
    /// <param name="itemSchema">
    /// Where <c>items</c> lives. Empty is this table's own schema, which is the ordinary case;
    /// naming one is §8's <b>two-source</b> layout — the target and the bridge in the database and
    /// the endpoint in process — and the planner then reaches the endpoint's keys through M5's
    /// strategies rather than through one remote query (§5, D229).
    /// </param>
    public static TableEntitlementDescriptor OrderItemsEntitlement(string itemSchema = "") => new()
    {
        Inherited =
        [
            new InheritedVisibilityDescriptor
            {
                Kind = "org",
                Steps = [ToTheOrder],
                EndpointPredicate = OrderOrgRows,
                EndpointTable = "orders",
            },
            new InheritedVisibilityDescriptor
            {
                Kind = "member",
                Steps = [ToTheOrder],
                EndpointPredicate = OrderSubject,
                EndpointTable = "orders",
            },
            new InheritedVisibilityDescriptor
            {
                Kind = "vendor",
                Steps = [ToTheItem(itemSchema)],
                EndpointPredicate = ItemRows,
                EndpointSchema = itemSchema,
                EndpointTable = "items",
            },
        ],
        Columns =
        [
            new ColumnEntitlementDescriptor
            {
                Column = 4,
                Rules =
                [
                    new DisclosureRule { When = LineThroughItsOrder, Then = Disclosure.Full },
                    new DisclosureRule { When = OrderVendor, Then = Disclosure.None },
                ],
                Otherwise = Disclosure.None,
            },
        ],
    };

    /// <summary>The down-step every line takes to its order: the kind-scoped <c>Through</c>.</summary>
    private static VisibilityStepDescriptor ToTheOrder => new()
    {
        Table = "orders",
        FromColumn = 1,
        ToColumn = 0,
        Direction = StepDirection.ToParent,
    };

    /// <summary>And the one it takes to the item that carries the vendor's key.</summary>
    private static VisibilityStepDescriptor ToTheItem(string schema = "") => new()
    {
        Schema = schema,
        Table = "items",
        FromColumn = 2,
        ToColumn = 0,
        Direction = StepDirection.ToParent,
    };

    /// <summary>
    /// A thread's rows resolve exactly as a member's do — the tenancy, the subject grants confined to
    /// one, and the global wildcard — which is what makes it a parent worth deriving from (§3.13).
    /// </summary>
    public static TableEntitlementDescriptor ThreadsEntitlement() => new()
    {
        RowPredicate =
            "org_id IN (@ctx.manager_orgs) OR org_id IN (@ctx.agent_orgs) "
            + "OR org_id IN (@ctx.auditor_orgs) OR org_id IN (@ctx.support_orgs) "
            + "OR org_id IN (@ctx.counter_orgs) OR (member_id, org_id) IN (@ctx.subject_pairs) "
            + "OR @ctx.global",
    };

    /// <summary>
    /// A message is visible when its thread is (D225), and its <c>content</c> is disclosed by the
    /// roles held in the <em>thread's</em> organization: a manager or the member the thread is about
    /// sees it in full, an agent an excerpt, everybody else nothing (D228).
    /// </summary>
    /// <param name="parentSchema">
    /// Where the parent lives. Empty is this table's own schema, which is the ordinary case; naming
    /// one is what a <b>cross-source</b> parent needs, and the planner then reaches it through M5's
    /// strategies rather than through one remote query (§3.13, D229).
    /// </param>
    public static TableEntitlementDescriptor MessagesEntitlement(string parentSchema = "") => new()
    {
        Through =
        [
            new ParentVisibilityDescriptor
            {
                Column = 1,
                ParentSchema = parentSchema,
                ParentTable = "threads",
                ParentColumn = 0,
            },
        ],
        Columns =
        [
            new ColumnEntitlementDescriptor
            {
                Column = 2,
                Mask = "SUBSTRING(content, 1, 9)",
                Rules =
                [
                    new DisclosureRule
                    {
                        When = "threads.org_id IN (@ctx.manager_orgs) OR @ctx.global",
                        Then = Disclosure.Full,
                    },
                    new DisclosureRule
                    {
                        When = "(threads.member_id, threads.org_id) IN (@ctx.subject_pairs)",
                        Then = Disclosure.Full,
                    },
                    new DisclosureRule
                    {
                        When = "threads.org_id IN (@ctx.agent_orgs)",
                        Then = Disclosure.Masked,
                    },
                ],
                Otherwise = Disclosure.None,
            },
        ],
    };

    /// <summary>An attachment through its message, which is itself through its thread: the chain.</summary>
    public static TableEntitlementDescriptor AttachmentsEntitlement() => new()
    {
        Through =
        [
            new ParentVisibilityDescriptor { Column = 1, ParentTable = "messages", ParentColumn = 0 },
        ],
    };

    /// <summary>The creator sees their own rows in full (D206, <c>CreatorSees = Full</c>).</summary>
    private static TableEntitlementDescriptor NotesEntitlement(bool creatorSeesFull) => new()
    {
        RowPredicate = NoteRows,
        Columns =
        [
            new ColumnEntitlementDescriptor
            {
                Column = 3,
                Rules = creatorSeesFull
                    ?
                    [
                        new DisclosureRule { When = "created_by = @ctx.user", Then = Disclosure.Full },
                        new DisclosureRule
                        {
                            When = "org_id IN (@ctx.manager_orgs) OR @ctx.global",
                            Then = Disclosure.Full,
                        },
                    ]
                    :
                    [
                        new DisclosureRule
                        {
                            When = "org_id IN (@ctx.manager_orgs) OR @ctx.global",
                            Then = Disclosure.Full,
                        },
                    ],
                Otherwise = Disclosure.None,
            },
        ],
    };

    /// <summary>The <c>Custom</c> predicate: a host-computed list and nothing else (§8's 15b).</summary>
    private static TableEntitlementDescriptor InvitesEntitlement() => new()
    {
        RowPredicate = "org_id IN (@ctx.allowed_orgs)",
    };

    // ---------------------------------------------------------------- the catalog

    /// <summary>
    /// Builds the fixture. With <paramref name="entitled"/> false the same tables carry no
    /// entitlement at all, which is the baseline the zero-cost class measures against.
    /// </summary>
    /// <summary>
    /// The same tables with <c>members.first_name</c> declaring <c>statistical</c> and a floor
    /// (D203): under an aggregate-only statement the raw name reaches predicates and grouping keys,
    /// and a group below the floor is dropped rather than NULLed.
    /// </summary>
    /// <summary>
    /// The same fixture with one more rule on <c>members.first_name</c>, for a role <c>u1</c> does
    /// not hold — so its condition folds to FALSE and the plan is otherwise identical, node for
    /// node, which is what makes it the test of D231's claim that a changed descriptor is a
    /// different plan.
    /// </summary>
    public static TenancyFixture WithAnUnreachableRule()
    {
        var builder = new PocoSourceBuilder("mem").NamingPolicy(PocoNamingPolicy.SnakeCase);
        builder.AddTable("orgs", Orgs, t => t.OrderedBy(o => o.Id).UniqueKey(o => o.Id));
        builder.AddTable("regions", Regions, t => t.OrderedBy(r => r.Id).UniqueKey(r => r.Id));
        builder.AddTable("members", Members, t =>
        {
            t.OrderedBy(m => m.Id).UniqueKey(m => m.Id).ForeignKey(m => m.OrgId)
                .References<Org>(o => o.Id, verify: true);
            var declared = MembersEntitlement();
            var first = declared.Columns[0];
            t.Entitlement(new TableEntitlementDescriptor
            {
                RowPredicate = declared.RowPredicate,
                Columns =
                [
                    new ColumnEntitlementDescriptor
                    {
                        Column = first.Column,
                        Mask = first.Mask,
                        Otherwise = first.Otherwise,
                        Rules =
                        [
                            .. first.Rules,
                            new DisclosureRule
                            {
                                When = "org_id IN (@ctx.auditor_orgs)",
                                Then = Disclosure.Masked,
                            },
                        ],
                    },
                    declared.Columns[1],
                    declared.Columns[2],
                ],
            });
        });
        builder.AddTable("orders", Orders, t =>
        {
            t.OrderedBy(o => o.Id).UniqueKey(o => o.Id).ForeignKey(o => o.MemberId)
                .References<Member>(m => m.Id, verify: true)
                .ForeignKey(o => o.RegionId).References<Region>(r => r.Id, verify: true);
            t.Entitlement(OrdersEntitlement());
        });
        Marketplace(builder, entitled: true);
        builder.AddTable("symbols", Symbols, t => t.OrderedBy(s => s.Name).UniqueKey(s => s.Name));
        return new TenancyFixture(builder.Build());
    }

    public static TenancyFixture Statistical(int floor)
    {
        var builder = new PocoSourceBuilder("mem").NamingPolicy(PocoNamingPolicy.SnakeCase);
        builder.AddTable("orgs", Orgs, t => t.OrderedBy(o => o.Id).UniqueKey(o => o.Id));
        builder.AddTable("regions", Regions, t => t.OrderedBy(r => r.Id).UniqueKey(r => r.Id));
        builder.AddTable("members", Members, t =>
        {
            t.OrderedBy(m => m.Id).UniqueKey(m => m.Id).ForeignKey(m => m.OrgId)
                .References<Org>(o => o.Id, verify: true);
            t.Entitlement(MembersEntitlement(statisticalFirstName: true, floor: floor));
        });
        builder.AddTable("orders", Orders, t =>
        {
            t.OrderedBy(o => o.Id).UniqueKey(o => o.Id).ForeignKey(o => o.MemberId)
                .References<Member>(m => m.Id, verify: true)
                .ForeignKey(o => o.RegionId).References<Region>(r => r.Id, verify: true);
            t.Entitlement(OrdersEntitlement());
        });
        Marketplace(builder, entitled: true);
        builder.AddTable("symbols", Symbols, t => t.OrderedBy(s => s.Name).UniqueKey(s => s.Name));
        return new TenancyFixture(builder.Build());
    }

    /// <summary>
    /// The three tables <c>orders</c>' <c>Related</c> path runs through (design 38 §8). Every
    /// catalog that carries <see cref="OrdersEntitlement"/> must carry them, because registration
    /// resolves the whole route and refuses a step to a table the catalog does not hold (§3).
    /// </summary>
    private static void Marketplace(PocoSourceBuilder builder, bool entitled) =>
        Marketplace(builder, entitled, itemSchema: string.Empty);

    /// <param name="itemSchema">
    /// Where <c>items</c> and <c>vendors</c> live. Empty is this source, which is the co-located
    /// layout; a schema name is design 38 §8's <b>two-source</b> layout (F84), and then they are not
    /// here at all — <see cref="MarketplaceSource"/> is that source — and <c>order_items</c> declares
    /// no foreign key to <c>items</c>, because a foreign key names a table of its own schema. What
    /// stands in its place is the association the catalog carries (D270 (c)).
    /// </param>
    private static void Marketplace(PocoSourceBuilder builder, bool entitled, string itemSchema)
    {
        var split = !string.IsNullOrEmpty(itemSchema);
        if (!split)
        {
            builder.AddTable("vendors", Vendors, t =>
            {
                t.OrderedBy(v => v.Id).UniqueKey(v => v.Id);
                if (entitled)
                {
                    t.Entitlement(VendorsEntitlement());
                }
            });

            builder.AddTable("items", Items, t =>
            {
                t.OrderedBy(i => i.Id).UniqueKey(i => i.Id).ForeignKey(i => i.VendorId)
                    .References<Vendor>(v => v.Id, verify: true);
                if (entitled)
                {
                    t.Entitlement(ItemsEntitlement());
                }
            });
        }

        builder.AddTable("order_items", OrderItems, t =>
        {
            t.OrderedBy(i => i.Id).UniqueKey(i => i.Id).ForeignKey(i => i.OrderId)
                .References<Order>(o => o.Id, verify: true);
            if (!split)
            {
                t.ForeignKey(i => i.ItemId).References<Item>(x => x.Id, verify: true);
            }

            if (entitled)
            {
                t.Entitlement(OrderItemsEntitlement(itemSchema));
            }
        });
    }

    /// <summary>
    /// The marketplace's two tables in a source of their own: design 38 §8's two-source layout,
    /// where the last step of an order's path crosses a source (F84).
    /// </summary>
    public static PocoSource MarketplaceSource(bool entitled = true)
    {
        var builder = new PocoSourceBuilder("mem-catalogue", MarketplaceSchema)
            .NamingPolicy(PocoNamingPolicy.SnakeCase);
        builder.AddTable("vendors", Vendors, t =>
        {
            t.OrderedBy(v => v.Id).UniqueKey(v => v.Id);
            if (entitled)
            {
                t.Entitlement(VendorsEntitlement());
            }
        });
        builder.AddTable("items", Items, t =>
        {
            t.OrderedBy(i => i.Id).UniqueKey(i => i.Id).ForeignKey(i => i.VendorId)
                .References<Vendor>(v => v.Id, verify: true);
            if (entitled)
            {
                t.Entitlement(ItemsEntitlement());
            }
        });
        return builder.Build();
    }

    /// <summary>The second source's schema name in the two-source layout (F84).</summary>
    public const string MarketplaceSchema = "catalogue";

    /// <summary>
    /// The same fixture with <c>vendors</c> and <c>items</c> one source away (F84): everything but
    /// those two, entitled exactly as <see cref="Create"/> entitles them, with the path's last step
    /// naming the other schema.
    /// </summary>
    public static PocoSource SplitSource(bool entitled = true) =>
        Create(entitled, creatorSeesFull: true, subversionFunctions: false, itemSchema: MarketplaceSchema)
            .Source;

    public static TenancyFixture Create(
        bool entitled = true,
        bool creatorSeesFull = true,
        bool subversionFunctions = false,
        string itemSchema = "",
        IReadOnlyList<string>? amountAggregates = null,
        string? firstNameMask = null,
        bool compositeTable = false)
    {
        var builder = new PocoSourceBuilder("mem").NamingPolicy(PocoNamingPolicy.SnakeCase);

        builder.AddTable("orgs", Orgs, t => t.OrderedBy(o => o.Id).UniqueKey(o => o.Id));

        builder.AddTable("regions", Regions, t => t.OrderedBy(r => r.Id).UniqueKey(r => r.Id));

        builder.AddTable("members", Members, t =>
        {
            t.OrderedBy(m => m.Id).UniqueKey(m => m.Id).ForeignKey(m => m.OrgId)
                .References<Org>(o => o.Id, verify: true);
            if (entitled)
            {
                t.Entitlement(MembersEntitlement(firstNameMask: firstNameMask));
            }
        });

        builder.AddTable("orders", Orders, t =>
        {
            t.OrderedBy(o => o.Id).UniqueKey(o => o.Id).ForeignKey(o => o.MemberId)
                .References<Member>(m => m.Id, verify: true)
                .ForeignKey(o => o.RegionId).References<Region>(r => r.Id, verify: true);
            if (entitled)
            {
                t.Entitlement(OrdersEntitlement(itemSchema, amountAggregates));
            }
        });

        builder.AddTable("notes", Notes, t =>
        {
            t.OrderedBy(n => n.Id).UniqueKey(n => n.Id);
            if (entitled)
            {
                t.Entitlement(NotesEntitlement(creatorSeesFull));
            }
        });

        builder.AddTable("invites", Invites, t =>
        {
            t.OrderedBy(i => i.Id).UniqueKey(i => i.Id);
            if (entitled)
            {
                t.Entitlement(InvitesEntitlement());
            }
        });

        builder.AddTable("threads", Threads, t =>
        {
            t.OrderedBy(x => x.Id).UniqueKey(x => x.Id).ForeignKey(x => x.OrgId)
                .References<Org>(o => o.Id, verify: true);
            if (entitled)
            {
                t.Entitlement(ThreadsEntitlement());
            }
        });

        builder.AddTable("messages", Messages, t =>
        {
            t.OrderedBy(m => m.Id).UniqueKey(m => m.Id).ForeignKey(m => m.ThreadId)
                .References<Thread>(x => x.Id, verify: true);
            if (entitled)
            {
                t.Entitlement(MessagesEntitlement());
            }
        });

        builder.AddTable("attachments", Attachments, t =>
        {
            t.OrderedBy(a => a.Id).UniqueKey(a => a.Id).ForeignKey(a => a.MessageId)
                .References<Message>(m => m.Id, verify: true);
            if (entitled)
            {
                t.Entitlement(AttachmentsEntitlement());
            }
        });

        Marketplace(builder, entitled, itemSchema);

        builder.AddTable("symbols", Symbols, t => t.OrderedBy(s => s.Name).UniqueKey(s => s.Name));
        if (subversionFunctions)
        {
            Functions(builder);
        }

        if (compositeTable)
        {
            AddProfiles(builder, entitled);
        }

        return new TenancyFixture(builder.Build());
    }

    /// <summary>
    /// The composite-column table (D302), in schema <c>main</c> of every fixture the adversarial family
    /// runs against: this one, and the in-process source beside the database, since a composite column
    /// is read only from an in-process source. The family's statements name it <c>main.profiles</c>,
    /// so they resolve over the database too, where the database's schema is the default one.
    /// </summary>
    public static PocoSourceBuilder AddProfiles(PocoSourceBuilder builder, bool entitled = true)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddTable("profiles", Profiles, t =>
        {
            t.OrderedBy(p => p.Id).UniqueKey(p => p.Id);
            if (entitled)
            {
                t.Entitlement(ProfilesEntitlement());
            }
        });
    }

    /// <summary>
    /// The two functions the adversarial corpus reaches an entitled table through (D251 class 4,
    /// <c>16-entitlements.md</c> §7): a SQL body whose statement names <c>members</c>, inlined
    /// before the rewrite so the reference is entitled like any other, and a client body that is
    /// handed a protected column and can therefore say what it was given. Class 8 adds three that
    /// answer a composite value (ADR 0077): the same client body as a record, and an aggregate twice
    /// over — <c>amount_summary</c>, which is not declared population-safe, and
    /// <c>population_summary</c>, the same aggregate declared so (D295).
    /// </summary>
    /// <remarks>
    /// Declared on every fixture the family runs against — this one, the oracle's disclosed source
    /// and the ADO fixture's — because a statement is only comparable across them if the same names
    /// resolve in all three. <c>first_name</c> is declared nullable on the function's own row,
    /// because a principal with no rule for it receives the placeholder and a placeholder widens.
    /// </remarks>
    public static PocoSourceBuilder Functions(PocoSourceBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder
            .AddFunction("members_in", f => f
                .TableFunction()
                .Parameter<int>("org")
                .Column("id", ChalkType.Int32())
                .Column("first_name", ChalkType.String(nullable: true))
                .Sql("SELECT id, first_name FROM members WHERE org_id = org"))
            .AddFunction("echo", f => f
                .Scalar<string, string>("s")
                .Strict()
                .Client())
            // Composite results (ADR 0077): a function whose fields say what it was handed, and a
            // population aggregate that answers a record, so a masked column and a population-only
            // column's allow-list each meet a composite value.
            .AddFunction("echo_with_length", f => f
                .Scalar<string, Echoed>("s")
                .Strict()
                .Client())
            .AddFunction("amount_summary", f => f
                .Aggregate<long, AmountSummary>("x")
                .Client())
            // D295: the same aggregate, which its host declares population-safe — a total and a
            // count report the group and never one row — so an allow-list may name it.
            .AddFunction("population_summary", f => f
                .Aggregate<long, AmountSummary>("x")
                .Population()
                .Client());
    }

    /// <summary>
    /// What <c>echo_with_length</c> answers: the string it was handed and its length. <c>Echo</c> is a
    /// nullable field, because a <c>string</c> property is one.
    /// </summary>
    public sealed record Echoed(string Echo, long Len);

    /// <summary>
    /// What <c>amount_summary</c> and <c>population_summary</c> answer: a population's total and how
    /// many values made it — the two things <c>SUM</c> and <c>COUNT</c> answer apart, and nothing a
    /// single row could be read back out of.
    /// </summary>
    public readonly record struct AmountSummary(long Total, long Tally);

    /// <summary><c>amount_summary</c>'s and <c>population_summary</c>'s state.</summary>
    public struct AmountSummaryState
    {
        public long Total;
        public long Tally;
    }

    // ---------------------------------------------------------------- the principals

    /// <summary>A manager in O1 and an agent in O2 — the mixed-rights principal of §8.</summary>
    public static RequestContext U1 { get; } = Principal(
        user: 1, managerOrgs: [1], agentOrgs: [2], auditorOrgs: [], subjectPairs: []);

    /// <summary>An agent in O1 only: rows visible, protected columns initial-masked.</summary>
    public static RequestContext U2 { get; } = Principal(
        user: 2, managerOrgs: [], agentOrgs: [1], auditorOrgs: [], subjectPairs: []);

    /// <summary>A subject grant on member 3, confined to O2.</summary>
    public static RequestContext U3 { get; } = Principal(
        user: 3, managerOrgs: [], agentOrgs: [], auditorOrgs: [], subjectPairs: [[3, 2]]);

    /// <summary>The same subject grant, confined to O1 instead — §8's query 24.</summary>
    public static RequestContext U3InO1 { get; } = Principal(
        user: 3, managerOrgs: [], agentOrgs: [], auditorOrgs: [], subjectPairs: [[3, 1]]);

    /// <summary>A global grant: every row, raw (§8's query 14).</summary>
    public static RequestContext U4 { get; } = Principal(
        user: 4, managerOrgs: [], agentOrgs: [], auditorOrgs: [], subjectPairs: [], global: true);

    /// <summary>No grants at all: the row predicate folds to FALSE.</summary>
    public static RequestContext U5 { get; } = Principal(
        user: 5, managerOrgs: [], agentOrgs: [], auditorOrgs: [], subjectPairs: []);

    /// <summary>An auditor in O1: <c>amount</c> population-only, <c>note</c> token-masked.</summary>
    public static RequestContext U6 { get; } = Principal(
        user: 6, managerOrgs: [], agentOrgs: [], auditorOrgs: [1], subjectPairs: []);

    /// <summary>
    /// An auditor in both O1 and O2 — §8's query 13. O2 holds two orders and the floor is three, so
    /// this is the principal for whom a group really is below the floor and its aggregate is NULLed.
    /// </summary>
    public static RequestContext U6Both { get; } = Principal(
        user: 6, managerOrgs: [], agentOrgs: [], auditorOrgs: [1, 2], subjectPairs: []);

    /// <summary>The host-computed list of §8's 15b, and nothing else: the Custom-mode principal.</summary>
    public static RequestContext U7 { get; } = Principal(
        user: 7,
        managerOrgs: [],
        agentOrgs: [],
        auditorOrgs: [],
        subjectPairs: [],
        allowedOrgs: [1]);

    /// <summary>
    /// A support desk in O1: <c>national_id</c> may be <b>tested</b> and never read (D261). The two
    /// members of O1 are the rows it can see, and what it may learn of either is whether the
    /// identifier it was handed is theirs.
    /// </summary>
    public static RequestContext U8 { get; } = Principal(
        user: 8,
        managerOrgs: [],
        agentOrgs: [],
        auditorOrgs: [],
        subjectPairs: [],
        supportOrgs: [1]);

    /// <summary>
    /// A counter in O1 and O2: <c>national_id</c> is population-only, countable, and a match count
    /// is guarded by the floor of three (D261 §2). O1 holds two members and O2 three, so one
    /// statement grouped by organisation shows the guard withholding and disclosing at once.
    /// </summary>
    public static RequestContext U9 { get; } = Principal(
        user: 9,
        managerOrgs: [],
        agentOrgs: [],
        auditorOrgs: [],
        subjectPairs: [],
        counterOrgs: [1, 2]);

    /// <summary>
    /// An auditor in O1 <b>confined to region 2</b> (D266 §6): the conjunction is inside the one
    /// grant, so this principal sees the O1 orders that lie in R2 and neither the O1 orders in R1
    /// nor any order of another organisation in R2. A member's row carries no region at all, so the
    /// grant resolves nothing there and reaches no member — which is §3's own rule.
    /// </summary>
    public static RequestContext U10 { get; } = Principal(
        user: 10,
        managerOrgs: [],
        agentOrgs: [],
        auditorOrgs: [],
        subjectPairs: [],
        auditorOrgRegions: [[1, 2]]);

    /// <summary>
    /// A subject grant on member 1 confined to O1 <b>and</b> region 1 at once (D266 §6): of member
    /// 1's two orders it reaches the one in R1, and of <c>members</c> it reaches nothing.
    /// </summary>
    public static RequestContext U11 { get; } = Principal(
        user: 11,
        managerOrgs: [],
        agentOrgs: [],
        auditorOrgs: [],
        subjectPairs: [],
        subjectOrgRegions: [[1, 1, 1]]);

    /// <summary>
    /// A <b>vendor grant alone</b> (design 38 §8): what this principal sees of an order comes only
    /// through the lines of it that carry one of its own items, and of the order it sees the
    /// identifier, the region and the amount's placeholder — <c>org_id</c> is the placeholder its
    /// own rule gives it and <c>member_id</c> it may only test for equality.
    /// </summary>
    /// <remarks>
    /// V1 supplies items 1 and 2, which appear on the lines of orders 1, 5 and 7. Order 7 is in O3,
    /// which no grant of this fixture reaches, so it is visible to this principal and to the global
    /// grant and to nobody else in the fixture: related visibility is what puts it there.
    /// </remarks>
    public static RequestContext U14 { get; } = Principal(
        user: 14,
        managerOrgs: [],
        agentOrgs: [],
        auditorOrgs: [],
        subjectPairs: [],
        vendors: [1]);

    /// <summary>
    /// The same vendor grant <b>and</b> a manager grant in O1, so both perspectives meet on order 1
    /// — which carries a line of V1's item 1 and is itself an O1 order. The rule order §8 asks for
    /// is what decides: the organisation's rule is written first and stops on match, so this
    /// principal reads <c>org_id</c> and <c>member_id</c> of an O1 order in full and meets the
    /// vendor's placeholder only on the orders it reaches along the path alone (5 and 7).
    /// </summary>
    public static RequestContext U15 { get; } = Principal(
        user: 15,
        managerOrgs: [1],
        agentOrgs: [],
        auditorOrgs: [],
        subjectPairs: [],
        vendors: [1]);

    /// <summary>Every principal §8 names, in its order, for a test that runs the lot.</summary>
    /// <remarks>
    /// <c>u3'</c> is §8's query 24 — the same subject grant confined to the other organization, so
    /// that "the confined grant does not reach O1's rows" is a principal rather than a special case —
    /// and <c>u7</c> is 15b's, the only one that binds <c>allowed_orgs</c>. <c>u5</c> holds no grant
    /// and is also the creator of the two notes of query 25.
    /// </remarks>
    public static IReadOnlyList<(string Name, RequestContext Context)> Principals { get; } =
    [
        ("u1", U1),
        ("u2", U2),
        ("u3", U3),
        ("u3-in-o1", U3InO1),
        ("u4", U4),
        ("u5", U5),
        ("u6", U6),
        ("u6-two-orgs", U6Both),
        ("u7", U7),
        ("u8", U8),
        ("u9", U9),
        ("u10-in-a-region", U10),
        ("u11-subject-in-a-region", U11),
        // D265 clause (h): the two the related path adds. Every statement of the corpus runs as
        // these too, which is what makes the path's answer a diff a reviewer reads.
        ("u14-a-vendor", U14),
        ("u15-a-vendor-and-an-org", U15),
    ];

    /// <summary>One principal's bindings, as a host's own compiler would produce them.</summary>
    public static RequestContext Principal(
        int user,
        int[] managerOrgs,
        int[] agentOrgs,
        int[] auditorOrgs,
        int[][] subjectPairs,
        bool global = false,
        int[]? allowedOrgs = null,
        int[]? supportOrgs = null,
        int[]? counterOrgs = null,
        int[][]? auditorOrgRegions = null,
        int[][]? subjectOrgRegions = null,
        int[]? vendors = null,
        string purpose = "",
        string actor = "")
    {
        var lists = new Dictionary<string, ContextRelation>(StringComparer.Ordinal)
        {
            ["manager_orgs"] = Ids(managerOrgs),
            ["agent_orgs"] = Ids(agentOrgs),
            ["auditor_orgs"] = Ids(auditorOrgs),
            ["allowed_orgs"] = Ids(allowedOrgs ?? []),
            // The two roles D261 adds. Empty for every principal that holds neither, which folds
            // both of `national_id`'s rules to FALSE and leaves the column exactly as it was.
            ["support_orgs"] = Ids(supportOrgs ?? []),
            ["counter_orgs"] = Ids(counterOrgs ?? []),
            // D265 clause (h): the vendor kind's own list, held in the `vendor` role. Empty for
            // every principal that holds no vendor grant, which folds the endpoint predicate of
            // every path that ends at a vendor's catalogue to FALSE — and design 38 §4 then drops
            // the chain, the marker and the verdict ordinals, leaving the plan the plan it was.
            ["vendor_vendor"] = Ids(vendors ?? []),
            ["subject_pairs"] = new ContextRelation
            {
                Columns = ["subject", "within"],
                Rows = subjectPairs
                    .Select(p => (IReadOnlyList<object?>)[p[0], p[1]])
                    .ToArray(),
                ColumnTypes = [ChalkType.Int32(), ChalkType.Int32()],
            },
            // D266's two conjoined groups. A tuple list of arity `1 + n` is a context relation like
            // any other — nothing on the wire changes shape — and a principal holding no grant of
            // the group binds it empty, which folds its term to FALSE and leaves every other
            // principal's answer exactly what it was.
            ["auditor_org_regions"] = new ContextRelation
            {
                Columns = ["org", "region"],
                Rows = (auditorOrgRegions ?? [])
                    .Select(p => (IReadOnlyList<object?>)[p[0], p[1]])
                    .ToArray(),
                ColumnTypes = [ChalkType.Int32(), ChalkType.Int32()],
            },
            ["subject_org_regions"] = new ContextRelation
            {
                Columns = ["subject", "org", "region"],
                Rows = (subjectOrgRegions ?? [])
                    .Select(p => (IReadOnlyList<object?>)[p[0], p[1], p[2]])
                    .ToArray(),
                ColumnTypes = [ChalkType.Int32(), ChalkType.Int32(), ChalkType.Int32()],
            },
        };

        return new RequestContext
        {
            Scalars = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["user"] = user,
                ["global"] = global,
                ["mask_key"] = "k",
            },
            Lists = lists,
            Purpose = purpose,
            Actor = actor,
        };
    }

    /// <summary>
    /// The names §2.1's partial binding leaves open for this fixture: the subject dimension and the
    /// principal's own identity (D232).
    /// </summary>
    /// <remarks>
    /// The tenancy half folds — its grants are the tenant's and every principal of that tenant
    /// shares them — and the subject half does not, because it is what tells one of them from
    /// another. <c>subject_pairs</c> becomes a bound table with a membership marker and <c>user</c> a
    /// parameter, beside the tenancy's own literal IN lists in the very same leaf.
    /// </remarks>
    public static readonly string[] SubjectNames = ["subject_pairs", "user"];

    /// <summary>
    /// The names that are the <b>person's</b> rather than the organization's, which is the split a
    /// tenant's shared plan folds along (D232): every principal of an organization binds the same
    /// grants, and what tells one of them from another is who they are and what key their masks are
    /// computed under.
    /// </summary>
    public static readonly string[] PersonNames = ["user", "mask_key"];

    /// <summary>This principal's bindings with <see cref="SubjectNames"/> left open (D232).</summary>
    public static RequestContext PartiallyBound(RequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Shape(SubjectNames);
    }

    /// <summary>
    /// The same principal with a different fold ceiling. A list of more rows than this stays a
    /// relation the executor materialises as a <c>ContextTable</c> rather than becoming literals in
    /// the plan (§2), which is what a host with a large tenancy gets and what lowering the ceiling
    /// lets a test exercise on a small one.
    /// </summary>
    public static RequestContext WithFoldCeiling(RequestContext context, int rows)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new RequestContext
        {
            Scalars = context.Scalars,
            Lists = context.Lists,
            Relations = context.Relations,
            FoldMaxRows = rows,
            Purpose = context.Purpose,
            Actor = context.Actor,
        };
    }

    private static ContextRelation Ids(int[] ids) => new()
    {
        Columns = ["id"],
        Rows = ids.Select(i => (IReadOnlyList<object?>)[i]).ToArray(),
        ColumnTypes = [ChalkType.Int32()],
    };
}

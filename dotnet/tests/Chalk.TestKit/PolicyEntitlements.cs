using Chalk.Entitlements;

namespace Chalk.TestKit;

/// <summary>
/// The battery's entitlements, in layer A's own vocabulary
/// (<c>docs/design/16-entitlements.md</c> §1; <c>corpus/policy/README.md</c>).
/// </summary>
/// <remarks>
/// <para>
/// A row predicate, per column an ordered list of <see cref="DisclosureRule"/>s with
/// <c>Otherwise = None</c> (D208), masks, placeholders, an aggregate allow-list and a group-size
/// floor. Nothing of the tenancy package's vocabulary appears — a role is visible here only as the
/// membership test on the context list a compiler would bind for it — which is what lets the
/// battery run the whole corpus through layer A alone.
/// </para>
/// <para>
/// The variants are whole entitlements: the corpus wrote them as inheritance and this writes them
/// out, with each distinct column declared once and shared by every variant that says the same
/// thing. A case names one by the constant, so a variant that goes away is a compiler error.
/// </para>
/// </remarks>
public static class PolicyEntitlements
{
    /// <summary>The scope predicates, written once and named by the rules that need them.</summary>
    public static class Predicates
    {
        /// <summary>The corpus's <c>members_masking_roles</c>.</summary>
        public const string MembersMaskingRoles =
            "org_id IN (@ctx.agent_orgs) OR org_id IN (@ctx.auditor_orgs) OR (id, org_id) IN "
                + "(@ctx.subject_pairs) OR id IN (@ctx.subject_ids)";

        /// <summary>The corpus's <c>members_scope</c>.</summary>
        public const string MembersScope =
            "org_id IN (@ctx.manager_orgs) OR org_id IN (@ctx.agent_orgs) OR org_id IN "
                + "(@ctx.auditor_orgs) OR (id, org_id) IN (@ctx.subject_pairs) OR id IN "
                + "(@ctx.subject_ids) OR @ctx.global";

        /// <summary>The corpus's <c>members_subject</c>.</summary>
        public const string MembersSubject =
            "(id, org_id) IN (@ctx.subject_pairs) OR id IN (@ctx.subject_ids)";

        /// <summary>The corpus's <c>notes_creator</c>.</summary>
        public const string NotesCreator =
            "created_by = @ctx.user";

        /// <summary>The corpus's <c>notes_scope</c>.</summary>
        public const string NotesScope =
            "org_id IN (@ctx.manager_orgs) OR org_id IN (@ctx.agent_orgs) OR org_id IN "
                + "(@ctx.auditor_orgs) OR created_by = @ctx.user OR @ctx.global";

        /// <summary>The corpus's <c>orders_agent</c>.</summary>
        public const string OrdersAgent =
            "org_id IN (@ctx.agent_orgs)";

        /// <summary>The corpus's <c>orders_auditor</c>.</summary>
        public const string OrdersAuditor =
            "org_id IN (@ctx.auditor_orgs)";

        /// <summary>The corpus's <c>orders_manager</c>.</summary>
        public const string OrdersManager =
            "org_id IN (@ctx.manager_orgs)";

        /// <summary>The corpus's <c>orders_scope</c>.</summary>
        public const string OrdersScope =
            "org_id IN (@ctx.manager_orgs) OR org_id IN (@ctx.agent_orgs) OR org_id IN "
                + "(@ctx.auditor_orgs) OR (member_id, org_id) IN (@ctx.subject_pairs) OR member_id "
                + "IN (@ctx.subject_ids) OR created_by = @ctx.user OR @ctx.global";

        /// <summary>
        /// The <b>organisation kind's</b> reach over an order, and nothing else (D265 §2, group U):
        /// the endpoint predicate of a line's <c>Inherited("org")</c> path is the same shape a
        /// <c>Direct</c> dimension compiles to, so the creator's fail-safe — a way into a row and
        /// not a tenancy of any kind — is deliberately not in it.
        /// </summary>
        public const string OrdersOrgKind =
            "org_id IN (@ctx.manager_orgs) OR org_id IN (@ctx.agent_orgs) OR org_id IN "
                + "(@ctx.auditor_orgs) OR @ctx.global";

        /// <summary>The endpoint predicate of every vendor path: the key is on the item's row.</summary>
        public const string ItemsScope =
            "vendor_id IN (@ctx.vendor_ids) OR @ctx.global";

        /// <summary>The same for a vendor's own row, where the key is the identifier.</summary>
        public const string VendorsScope =
            "id IN (@ctx.vendor_ids) OR @ctx.global";

        /// <summary>
        /// A vendor's reach on the target, decided at the <b>endpoint</b> of the path (D265 §2,
        /// §4): a rule on the target may name the endpoint's columns exactly as a child's rule may
        /// name its parent's, and the ordinal it produces is evaluated at the endpoint's
        /// cardinality and <c>MIN</c>-ed over the rows that reach one order.
        /// </summary>
        public const string TheVendorsItem = "items.vendor_id IN (@ctx.vendor_ids)";

        /// <summary>
        /// The other route into a line: through its order, whose columns a rule on the line may name
        /// because the order is the endpoint of the line's own <c>org</c> and <c>member</c> paths.
        /// It is <see cref="OrdersOrgKind"/> and <see cref="OrdersSubject"/> over that row.
        /// </summary>
        public const string LineThroughItsOrder =
            "orders.org_id IN (@ctx.manager_orgs) OR orders.org_id IN (@ctx.agent_orgs) OR "
                + "orders.org_id IN (@ctx.auditor_orgs) OR @ctx.global OR (orders.member_id, "
                + "orders.org_id) IN (@ctx.subject_pairs) OR orders.member_id IN "
                + "(@ctx.subject_ids)";

        /// <summary>The corpus's <c>orders_subject</c>.</summary>
        public const string OrdersSubject =
            "(member_id, org_id) IN (@ctx.subject_pairs) OR member_id IN (@ctx.subject_ids)";

        /// <summary>
        /// The corpus's <c>orders_confined</c> (D266): a subject grant confined along <b>two</b>
        /// tenancy kinds at once, which layer A spells as one membership over a tuple of arity
        /// three. All three columns must match on the same row — that is the whole of the
        /// conjunction, and it is why this is not the union of two memberships.
        /// </summary>
        public const string OrdersConfined =
            "(member_id, org_id, created_by) IN (@ctx.subject_org_desks)";

        /// <summary>The same as a whole table scope, beside the manager's and the wildcard.</summary>
        public const string OrdersConfinedScope =
            "org_id IN (@ctx.manager_orgs) OR " + OrdersConfined + " OR @ctx.global";

    }

    /// <summary>The entitlement <c>members</c> on <c>members</c>.</summary>
    public const string Members = "members";

    /// <summary>The entitlement <c>members_none</c> on <c>members</c>.</summary>
    public const string MembersNone = "members_none";

    /// <summary>The entitlement <c>members_none_placeholder</c> on <c>members</c>.</summary>
    public const string MembersNonePlaceholder = "members_none_placeholder";

    /// <summary>The entitlement <c>members_pushdown_required</c> on <c>members</c>.</summary>
    public const string MembersPushdownRequired = "members_pushdown_required";

    /// <summary>The entitlement <c>members_local</c> on <c>members</c>.</summary>
    public const string MembersLocal = "members_local";

    /// <summary>The entitlement <c>members_blocked</c> on <c>members</c>.</summary>
    public const string MembersBlocked = "members_blocked";

    /// <summary>The entitlement <c>members_statistical</c> on <c>members</c>.</summary>
    public const string MembersStatistical = "members_statistical";

    /// <summary>The entitlement <c>members_tested</c> on <c>members</c> (D261).</summary>
    public const string MembersTested = "members_tested";

    /// <summary>The entitlement <c>members_counted</c> on <c>members</c> (D261 §2).</summary>
    public const string MembersCounted = "members_counted";

    /// <summary>The entitlement <c>orders</c> on <c>orders</c>.</summary>
    public const string Orders = "orders";

    /// <summary>The entitlement <c>orders_byrules</c> on <c>orders</c>.</summary>
    public const string OrdersByrules = "orders_byrules";

    /// <summary>The entitlement <c>orders_confined</c> on <c>orders</c> (D266).</summary>
    public const string OrdersConfined = "orders_confined";

    /// <summary>The entitlement <c>orders_none</c> on <c>orders</c>.</summary>
    public const string OrdersNone = "orders_none";

    /// <summary>The entitlement <c>orders_floor1</c> on <c>orders</c>.</summary>
    public const string OrdersFloor1 = "orders_floor1";

    /// <summary>The entitlement <c>orders_floor_off</c> on <c>orders</c>.</summary>
    public const string OrdersFloorOff = "orders_floor_off";

    /// <summary>The entitlement <c>orders_statistical</c> on <c>orders</c>.</summary>
    public const string OrdersStatistical = "orders_statistical";

    /// <summary>The entitlement <c>orders_ctx_path</c> on <c>orders</c>.</summary>
    public const string OrdersCtxPath = "orders_ctx_path";

    /// <summary>The entitlement <c>notes</c> on <c>notes</c>.</summary>
    public const string Notes = "notes";

    /// <summary>The entitlement <c>notes_byrules</c> on <c>notes</c>.</summary>
    public const string NotesByrules = "notes_byrules";

    /// <summary>The entitlement <c>invites</c> on <c>invites</c>.</summary>
    public const string Invites = "invites";

    /// <summary>The entitlement <c>symbols_unentitled</c> on <c>symbols</c>.</summary>
    public const string SymbolsUnentitled = "symbols_unentitled";

    /// <summary>The entitlement <c>bad_otherwise_full</c> on <c>members</c>.</summary>
    public const string BadOtherwiseFull = "bad_otherwise_full";

    /// <summary>The entitlement <c>bad_aggregate_max</c> on <c>orders</c>.</summary>
    public const string BadAggregateMax = "bad_aggregate_max";

    /// <summary>The entitlement <c>bad_when_not_boolean</c> on <c>members</c>.</summary>
    public const string BadWhenNotBoolean = "bad_when_not_boolean";

    /// <summary>The entitlement <c>bad_rule_mask_type</c> on <c>members</c>.</summary>
    public const string BadRuleMaskType = "bad_rule_mask_type";

    /// <summary>The entitlement <c>bad_mask_reads_protected</c> on <c>members</c> (D220).</summary>
    public const string BadMaskReadsProtected = "bad_mask_reads_protected";

    /// <summary>The entitlement <c>members_rule_placeholder</c> on <c>members</c> (D224).</summary>
    public const string MembersRulePlaceholder = "members_rule_placeholder";

    /// <summary>The entitlement <c>vendors</c> (D265 §8, group U).</summary>
    public const string Vendors = "vendors";

    /// <summary>The entitlement <c>items</c>: the endpoint of every vendor path.</summary>
    public const string Items = "items";

    /// <summary>The entitlement <c>order_items</c>: the bridge, entitled by kind.</summary>
    public const string OrderItems = "order_items";

    /// <summary>
    /// The entitlement <c>orders_related</c>: the default <c>orders</c> with D265's <c>Related</c>
    /// path beside its own restrictions, and the two rules §8 gives the column that says whose
    /// order it is.
    /// </summary>
    public const string OrdersRelated = "orders_related";

    private static ColumnEntitlementDescriptor MembersFirstName { get; } =
    new ColumnEntitlementDescriptor
    {
        Column = PolicyColumns.Members.FirstName,
        Rules =
        [
            new DisclosureRule { When = Predicates.OrdersManager, Then = Disclosure.Full },
            new DisclosureRule { When = Predicates.MembersSubject, Then = Disclosure.Full },
            new DisclosureRule { When = "@ctx.global", Then = Disclosure.Full },
            new DisclosureRule { When = Predicates.OrdersAgent, Then = Disclosure.Masked, Mask = "SUBSTRING(first_name FROM 1 FOR 1)" },
            new DisclosureRule { When = Predicates.OrdersAuditor, Then = Disclosure.Masked, Mask = "FINGERPRINT(first_name, @ctx.mask_key)" },
        ],
    };

    private static ColumnEntitlementDescriptor MembersLastName { get; } =
    new ColumnEntitlementDescriptor
    {
        Column = PolicyColumns.Members.LastName,
        Rules =
        [
            new DisclosureRule { When = Predicates.OrdersManager, Then = Disclosure.Full },
            new DisclosureRule { When = Predicates.MembersSubject, Then = Disclosure.Full },
            new DisclosureRule { When = "@ctx.global", Then = Disclosure.Full },
            new DisclosureRule { When = Predicates.OrdersAgent, Then = Disclosure.Masked, Mask = "SUBSTRING(last_name FROM 1 FOR 1)" },
            new DisclosureRule { When = Predicates.OrdersAuditor, Then = Disclosure.Masked, Mask = "FINGERPRINT(last_name, @ctx.mask_key)" },
        ],
    };

    private static ColumnEntitlementDescriptor MembersDob { get; } =
    new ColumnEntitlementDescriptor
    {
        Column = PolicyColumns.Members.Dob,
        Rules =
        [
            new DisclosureRule { When = Predicates.OrdersManager, Then = Disclosure.Full },
            new DisclosureRule { When = Predicates.MembersSubject, Then = Disclosure.Full },
            new DisclosureRule { When = "@ctx.global", Then = Disclosure.Full },
            new DisclosureRule { When = Predicates.OrdersAgent, Then = Disclosure.Masked, Mask = "FLOOR(dob TO YEAR)" },
            new DisclosureRule { When = Predicates.OrdersAuditor, Then = Disclosure.Masked, Mask = "CAST(NULL AS DATE)" },
        ],
    };

    private static ColumnEntitlementDescriptor MembersNationalId { get; } =
    new ColumnEntitlementDescriptor
    {
        Column = PolicyColumns.Members.NationalId,
        Rules =
        [
            new DisclosureRule { When = Predicates.OrdersManager, Then = Disclosure.Full },
            new DisclosureRule { When = "@ctx.global", Then = Disclosure.Full },
            new DisclosureRule { When = Predicates.MembersMaskingRoles, Then = Disclosure.Masked, Mask = "CAST(NULL AS VARCHAR)" },
        ],
    };

    private static ColumnEntitlementDescriptor MembersId { get; } =
    new ColumnEntitlementDescriptor
    {
        Column = PolicyColumns.Members.Id,
        Rules =
        [
            new DisclosureRule { When = Predicates.MembersScope, Then = Disclosure.Full },
        ],
    };

    private static ColumnEntitlementDescriptor MembersOrgId { get; } =
    new ColumnEntitlementDescriptor
    {
        Column = PolicyColumns.Members.OrgId,
        Rules =
        [
            new DisclosureRule { When = Predicates.MembersScope, Then = Disclosure.Full },
        ],
    };

    private static ColumnEntitlementDescriptor MembersPostcode { get; } =
    new ColumnEntitlementDescriptor
    {
        Column = PolicyColumns.Members.Postcode,
        Placeholder = "'0000'",
    };

    /// <summary>
    /// <c>first_name</c> with the agent's own stand-in (D224): a manager sees the value, an agent is
    /// told in words that there is one and does not see it, and every other principal takes the
    /// column's placeholder. Three answers over one column, and only the middle one is the rule's.
    /// </summary>
    private static ColumnEntitlementDescriptor MembersFirstNameRulePlaceholder { get; } =
    new ColumnEntitlementDescriptor
    {
        Column = PolicyColumns.Members.FirstName,
        Placeholder = "'(none)'",
        Rules =
        [
            new DisclosureRule { When = Predicates.OrdersManager, Then = Disclosure.Full },
            new DisclosureRule
            {
                When = Predicates.OrdersAgent,
                Then = Disclosure.None,
                Placeholder = "'withheld from agents'",
            },
        ],
    };

    /// <summary>
    /// The same on a DATE column, where the point is sharper: a date has no spelling an empty value
    /// could take, so a stand-in for it has to be a date the host chose (D224, F47).
    /// </summary>
    private static ColumnEntitlementDescriptor MembersDobRulePlaceholder { get; } =
    new ColumnEntitlementDescriptor
    {
        Column = PolicyColumns.Members.Dob,
        Rules =
        [
            new DisclosureRule { When = Predicates.OrdersManager, Then = Disclosure.Full },
            new DisclosureRule
            {
                When = Predicates.OrdersAgent,
                Then = Disclosure.None,
                Placeholder = "DATE '1900-01-01'",
            },
        ],
    };

    private static ColumnEntitlementDescriptor MembersFirstName2 { get; } =
    new ColumnEntitlementDescriptor
    {
        Column = PolicyColumns.Members.FirstName,
        Rules =
        [
            new DisclosureRule { When = Predicates.OrdersManager, Then = Disclosure.Full },
            new DisclosureRule { When = Predicates.MembersSubject, Then = Disclosure.Full },
            new DisclosureRule { When = "@ctx.global", Then = Disclosure.Full },
            new DisclosureRule { When = Predicates.OrdersAgent, Then = Disclosure.Masked, Mask = "SUBSTRING(first_name FROM 1 FOR 1)" },
            new DisclosureRule { When = Predicates.OrdersAuditor, Then = Disclosure.Masked, Mask = "FINGERPRINT(first_name, @ctx.mask_key)" },
        ],
        Statistical = true,
        MinGroupSize = 2,
    };

    private static ColumnEntitlementDescriptor OrdersNote { get; } =
    new ColumnEntitlementDescriptor
    {
        Column = PolicyColumns.Orders.Note,
        Rules =
        [
            new DisclosureRule { When = Predicates.NotesCreator, Then = Disclosure.Full },
            new DisclosureRule { When = Predicates.OrdersManager, Then = Disclosure.Full },
            new DisclosureRule { When = Predicates.OrdersSubject, Then = Disclosure.Full },
            new DisclosureRule { When = "@ctx.global", Then = Disclosure.Full },
            new DisclosureRule { When = Predicates.OrdersAgent, Then = Disclosure.Masked, Mask = "'********'" },
            new DisclosureRule { When = Predicates.OrdersAuditor, Then = Disclosure.Masked, Mask = "FINGERPRINT(note, @ctx.mask_key)" },
        ],
    };

    /// <summary>
    /// The note, as a <b>conjoined</b> subject grant discloses it (D266): the same first-match-wins
    /// list, with the confined group's membership where the unconfined subject's stands. It is one
    /// term over a tuple of arity three and nothing else about layer A changes.
    /// </summary>
    private static ColumnEntitlementDescriptor OrdersNoteConfined { get; } =
    new ColumnEntitlementDescriptor
    {
        Column = PolicyColumns.Orders.Note,
        Rules =
        [
            new DisclosureRule { When = Predicates.OrdersManager, Then = Disclosure.Full },
            new DisclosureRule { When = Predicates.OrdersConfined, Then = Disclosure.Full },
            new DisclosureRule { When = "@ctx.global", Then = Disclosure.Full },
        ],
    };

    private static ColumnEntitlementDescriptor OrdersAmount { get; } =
    new ColumnEntitlementDescriptor
    {
        Column = PolicyColumns.Orders.Amount,
        Rules =
        [
            new DisclosureRule { When = Predicates.NotesCreator, Then = Disclosure.Full },
            new DisclosureRule { When = Predicates.OrdersManager, Then = Disclosure.Full },
            new DisclosureRule { When = Predicates.OrdersAgent, Then = Disclosure.Full },
            new DisclosureRule { When = Predicates.OrdersSubject, Then = Disclosure.Full },
            new DisclosureRule { When = "@ctx.global", Then = Disclosure.Full },
            new DisclosureRule { When = Predicates.OrdersAuditor, Then = Disclosure.AggregateOnly },
        ],
        AggregateOnlyFunctions = ["COUNT", "SUM", "SUM0", "AVG"],
        MinGroupSize = 5,
    };

    private static ColumnEntitlementDescriptor OrdersNote2 { get; } =
    new ColumnEntitlementDescriptor
    {
        Column = PolicyColumns.Orders.Note,
        Rules =
        [
            new DisclosureRule { When = Predicates.OrdersManager, Then = Disclosure.Full },
            new DisclosureRule { When = Predicates.OrdersSubject, Then = Disclosure.Full },
            new DisclosureRule { When = "@ctx.global", Then = Disclosure.Full },
            new DisclosureRule { When = Predicates.OrdersAgent, Then = Disclosure.Masked, Mask = "'********'" },
            new DisclosureRule { When = Predicates.OrdersAuditor, Then = Disclosure.Masked, Mask = "FINGERPRINT(note, @ctx.mask_key)" },
        ],
    };

    private static ColumnEntitlementDescriptor OrdersAmount2 { get; } =
    new ColumnEntitlementDescriptor
    {
        Column = PolicyColumns.Orders.Amount,
        Rules =
        [
            new DisclosureRule { When = Predicates.OrdersManager, Then = Disclosure.Full },
            new DisclosureRule { When = Predicates.OrdersAgent, Then = Disclosure.Full },
            new DisclosureRule { When = Predicates.OrdersSubject, Then = Disclosure.Full },
            new DisclosureRule { When = "@ctx.global", Then = Disclosure.Full },
            new DisclosureRule { When = Predicates.OrdersAuditor, Then = Disclosure.AggregateOnly },
        ],
        AggregateOnlyFunctions = ["COUNT", "SUM", "SUM0", "AVG"],
        MinGroupSize = 5,
    };

    private static ColumnEntitlementDescriptor OrdersId { get; } =
    new ColumnEntitlementDescriptor
    {
        Column = PolicyColumns.Orders.Id,
        Rules =
        [
            new DisclosureRule { When = Predicates.OrdersScope, Then = Disclosure.Full },
        ],
    };

    private static ColumnEntitlementDescriptor OrdersMemberId { get; } =
    new ColumnEntitlementDescriptor
    {
        Column = PolicyColumns.Orders.MemberId,
        Rules =
        [
            new DisclosureRule { When = Predicates.OrdersScope, Then = Disclosure.Full },
        ],
    };

    private static ColumnEntitlementDescriptor OrdersOrgId { get; } =
    new ColumnEntitlementDescriptor
    {
        Column = PolicyColumns.Orders.OrgId,
        Rules =
        [
            new DisclosureRule { When = Predicates.OrdersScope, Then = Disclosure.Full },
        ],
    };

    private static ColumnEntitlementDescriptor OrdersAmount3 { get; } =
    new ColumnEntitlementDescriptor
    {
        Column = PolicyColumns.Orders.Amount,
        Rules =
        [
            new DisclosureRule { When = Predicates.OrdersManager, Then = Disclosure.Full },
            new DisclosureRule { When = Predicates.OrdersAgent, Then = Disclosure.Full },
            new DisclosureRule { When = Predicates.OrdersSubject, Then = Disclosure.Full },
            new DisclosureRule { When = "@ctx.global", Then = Disclosure.Full },
            new DisclosureRule { When = Predicates.OrdersAuditor, Then = Disclosure.AggregateOnly },
        ],
        Placeholder = "CAST(-1 AS DECIMAL(10,2))",
        AggregateOnlyFunctions = ["COUNT", "SUM", "SUM0", "AVG"],
        MinGroupSize = 5,
    };

    private static ColumnEntitlementDescriptor OrdersStatus { get; } =
    new ColumnEntitlementDescriptor
    {
        Column = PolicyColumns.Orders.Status,
        Rules =
        [
            new DisclosureRule { When = Predicates.OrdersScope, Then = Disclosure.Full },
        ],
    };

    private static ColumnEntitlementDescriptor OrdersCreatedBy { get; } =
    new ColumnEntitlementDescriptor
    {
        Column = PolicyColumns.Orders.CreatedBy,
        Rules =
        [
            new DisclosureRule { When = Predicates.OrdersScope, Then = Disclosure.Full },
        ],
    };

    private static ColumnEntitlementDescriptor OrdersAmount4 { get; } =
    new ColumnEntitlementDescriptor
    {
        Column = PolicyColumns.Orders.Amount,
        Rules =
        [
            new DisclosureRule { When = Predicates.NotesCreator, Then = Disclosure.Full },
            new DisclosureRule { When = Predicates.OrdersManager, Then = Disclosure.Full },
            new DisclosureRule { When = Predicates.OrdersAgent, Then = Disclosure.Full },
            new DisclosureRule { When = Predicates.OrdersSubject, Then = Disclosure.Full },
            new DisclosureRule { When = "@ctx.global", Then = Disclosure.Full },
            new DisclosureRule { When = Predicates.OrdersAuditor, Then = Disclosure.AggregateOnly },
        ],
        AggregateOnlyFunctions = ["COUNT", "SUM", "SUM0", "AVG"],
        MinGroupSize = 1,
    };

    private static ColumnEntitlementDescriptor OrdersAmount5 { get; } =
    new ColumnEntitlementDescriptor
    {
        Column = PolicyColumns.Orders.Amount,
        Rules =
        [
            new DisclosureRule { When = Predicates.NotesCreator, Then = Disclosure.Full },
            new DisclosureRule { When = Predicates.OrdersManager, Then = Disclosure.Full },
            new DisclosureRule { When = Predicates.OrdersAgent, Then = Disclosure.Full },
            new DisclosureRule { When = Predicates.OrdersSubject, Then = Disclosure.Full },
            new DisclosureRule { When = "@ctx.global", Then = Disclosure.Full },
            new DisclosureRule { When = Predicates.OrdersAuditor, Then = Disclosure.AggregateOnly },
        ],
        AggregateOnlyFunctions = ["COUNT", "SUM", "SUM0", "AVG"],
    };

    private static ColumnEntitlementDescriptor OrdersAmount6 { get; } =
    new ColumnEntitlementDescriptor
    {
        Column = PolicyColumns.Orders.Amount,
        Rules =
        [
            new DisclosureRule { When = Predicates.NotesCreator, Then = Disclosure.Full },
            new DisclosureRule { When = Predicates.OrdersManager, Then = Disclosure.Full },
            new DisclosureRule { When = Predicates.OrdersAgent, Then = Disclosure.Full },
            new DisclosureRule { When = Predicates.OrdersSubject, Then = Disclosure.Full },
            new DisclosureRule { When = "@ctx.global", Then = Disclosure.Full },
            new DisclosureRule { When = Predicates.OrdersAuditor, Then = Disclosure.AggregateOnly },
        ],
        Statistical = true,
        AggregateOnlyFunctions = ["COUNT", "SUM", "SUM0", "AVG"],
        MinGroupSize = 5,
    };

    private static ColumnEntitlementDescriptor NotesBody { get; } =
    new ColumnEntitlementDescriptor
    {
        Column = PolicyColumns.Notes.Body,
        Rules =
        [
            new DisclosureRule { When = Predicates.NotesCreator, Then = Disclosure.Full },
            new DisclosureRule { When = Predicates.OrdersManager, Then = Disclosure.Full },
            new DisclosureRule { When = "@ctx.global", Then = Disclosure.Full },
            new DisclosureRule { When = Predicates.OrdersAgent, Then = Disclosure.Masked, Mask = "'********'" },
            new DisclosureRule { When = Predicates.OrdersAuditor, Then = Disclosure.Masked, Mask = "'********'" },
        ],
    };

    private static ColumnEntitlementDescriptor NotesBody2 { get; } =
    new ColumnEntitlementDescriptor
    {
        Column = PolicyColumns.Notes.Body,
        Rules =
        [
            new DisclosureRule { When = Predicates.OrdersManager, Then = Disclosure.Full },
            new DisclosureRule { When = "@ctx.global", Then = Disclosure.Full },
            new DisclosureRule { When = Predicates.OrdersAgent, Then = Disclosure.Masked, Mask = "'********'" },
            new DisclosureRule { When = Predicates.OrdersAuditor, Then = Disclosure.Masked, Mask = "'********'" },
        ],
    };

    private static ColumnEntitlementDescriptor MembersPostcode2 { get; } =
    new ColumnEntitlementDescriptor
    {
        Column = PolicyColumns.Members.Postcode,
        Otherwise = Disclosure.Full,
    };

    private static ColumnEntitlementDescriptor OrdersAmount7 { get; } =
    new ColumnEntitlementDescriptor
    {
        Column = PolicyColumns.Orders.Amount,
        Rules =
        [
            new DisclosureRule { When = Predicates.OrdersAuditor, Then = Disclosure.AggregateOnly },
        ],
        AggregateOnlyFunctions = ["SUM", "MAX"],
    };

    private static ColumnEntitlementDescriptor MembersFirstName3 { get; } =
    new ColumnEntitlementDescriptor
    {
        Column = PolicyColumns.Members.FirstName,
        Rules =
        [
            new DisclosureRule { When = "first_name", Then = Disclosure.Full },
        ],
    };

    private static ColumnEntitlementDescriptor MembersDob2 { get; } =
    new ColumnEntitlementDescriptor
    {
        Column = PolicyColumns.Members.Dob,
        Rules =
        [
            new DisclosureRule { When = Predicates.OrdersAgent, Then = Disclosure.Masked, Mask = "'****'" },
        ],
    };

    /// <summary>
    /// A mask that reads another <em>protected</em> column: <c>first_name</c> masked by
    /// <c>last_name</c>, which carries an entitlement of its own (D220).
    /// </summary>
    private static ColumnEntitlementDescriptor MembersFirstNameReadsLastName { get; } =
    new ColumnEntitlementDescriptor
    {
        Column = PolicyColumns.Members.FirstName,
        Rules =
        [
            new DisclosureRule
            {
                When = Predicates.OrdersAgent,
                Then = Disclosure.Masked,
                Mask = "SUBSTRING(last_name FROM 1 FOR 1)",
            },
        ],
    };

    /// <summary>
    /// <c>national_id</c> as a column the masking roles may <b>test and never read</b> (D261): the
    /// three shapes, and the placeholder for everything else.
    /// </summary>
    private static ColumnEntitlementDescriptor MembersNationalIdTested { get; } =
    new ColumnEntitlementDescriptor
    {
        Column = PolicyColumns.Members.NationalId,
        Rules =
        [
            new DisclosureRule { When = Predicates.OrdersManager, Then = Disclosure.Full },
            new DisclosureRule
            {
                When = Predicates.MembersMaskingRoles,
                Then = Disclosure.Test,
                Tests = [TestShape.Equals, TestShape.NotEquals, TestShape.In],
            },
        ],
    };

    /// <summary>
    /// The same column as one the masking roles may <b>count matches of</b>, guarded by a floor of
    /// three — the aggregate form (D261 §2), which is the one place a population-only column may be
    /// compared at all.
    /// </summary>
    private static ColumnEntitlementDescriptor MembersNationalIdCounted { get; } =
    new ColumnEntitlementDescriptor
    {
        Column = PolicyColumns.Members.NationalId,
        AggregateOnlyFunctions = ["COUNT"],
        MinGroupSize = 3,
        Rules =
        [
            new DisclosureRule
            {
                When = Predicates.MembersMaskingRoles,
                Then = Disclosure.AggregateOnly,
                Tests = [TestShape.Equals],
            },
        ],
    };

    /// <summary>
    /// What a principal reaching an order along the path may have of the column that says whose
    /// order it is (D265 §8): the organisation's own reach first, so a principal holding both
    /// grants reads the value, and the vendor's placeholder second, so one reaching the row along
    /// the path alone does not. The order is the whole of D222.
    /// </summary>
    private static ColumnEntitlementDescriptor OrdersOrgIdRelated { get; } =
    new ColumnEntitlementDescriptor
    {
        Column = PolicyColumns.Orders.OrgId,
        Rules =
        [
            new DisclosureRule { When = Predicates.OrdersScope, Then = Disclosure.Full },
            new DisclosureRule { When = Predicates.TheVendorsItem, Then = Disclosure.None },
        ],
    };

    /// <summary>
    /// The one column of a line a vendor does not read: its rules name the two routes in §8's own
    /// order, so a principal reaching the line through its order sees the value and one reaching it
    /// through its item does not. <c>quantity</c> carries no rule and is full for everyone that can
    /// see the row.
    /// </summary>
    private static ColumnEntitlementDescriptor OrderItemsUnitPrice { get; } =
    new ColumnEntitlementDescriptor
    {
        Column = PolicyColumns.OrderItems.UnitPrice,
        Rules =
        [
            new DisclosureRule { When = Predicates.LineThroughItsOrder, Then = Disclosure.Full },
            new DisclosureRule { When = Predicates.TheVendorsItem, Then = Disclosure.None },
        ],
    };

    /// <summary>The down-step every line takes to its order: the kind-scoped <c>Through</c>.</summary>
    private static VisibilityStepDescriptor ToTheOrder { get; } = new()
    {
        Table = "orders",
        FromColumn = PolicyColumns.OrderItems.OrderId,
        ToColumn = PolicyColumns.Orders.Id,
        Direction = StepDirection.ToParent,
    };

    /// <summary>And the one it takes to the item that carries the vendor's key.</summary>
    private static VisibilityStepDescriptor ToTheItem { get; } = new()
    {
        Table = "items",
        FromColumn = PolicyColumns.OrderItems.ItemId,
        ToColumn = PolicyColumns.Items.Id,
        Direction = StepDirection.ToParent,
    };

    /// <summary>Every entitlement the battery declares, by the name a case's catalog uses.</summary>
    public static IReadOnlyDictionary<string, PolicyEntitlement> All { get; } =
        new Dictionary<string, PolicyEntitlement>(StringComparer.Ordinal)
    {
        [Members] = new(Members, "members", new TableEntitlementDescriptor
        {
            RowPredicate = Predicates.MembersScope,
            Columns =
            [
                MembersFirstName,
                MembersLastName,
                MembersDob,
                MembersNationalId,
            ],
        }),
        [MembersTested] = new(MembersTested, "members", new TableEntitlementDescriptor
        {
            RowPredicate = Predicates.MembersScope,
            Columns =
            [
                MembersFirstName,
                MembersLastName,
                MembersDob,
                MembersNationalIdTested,
            ],
        }),
        [MembersCounted] = new(MembersCounted, "members", new TableEntitlementDescriptor
        {
            RowPredicate = Predicates.MembersScope,
            Columns =
            [
                MembersFirstName,
                MembersLastName,
                MembersDob,
                MembersNationalIdCounted,
            ],
        }),
        [MembersNone] = new(MembersNone, "members", new TableEntitlementDescriptor
        {
            RowPredicate = Predicates.MembersScope,
            DefaultDisclosure = Disclosure.None,
            Columns =
            [
                MembersId,
                MembersOrgId,
                MembersFirstName,
                MembersLastName,
                MembersDob,
                MembersNationalId,
            ],
        }),
        [MembersNonePlaceholder] = new(MembersNonePlaceholder, "members", new TableEntitlementDescriptor
        {
            RowPredicate = Predicates.MembersScope,
            DefaultDisclosure = Disclosure.None,
            Columns =
            [
                MembersId,
                MembersOrgId,
                MembersFirstName,
                MembersLastName,
                MembersDob,
                MembersNationalId,
                MembersPostcode,
            ],
        }),
        [MembersRulePlaceholder] = new(MembersRulePlaceholder, "members", new TableEntitlementDescriptor
        {
            RowPredicate = Predicates.MembersScope,
            Columns =
            [
                MembersFirstNameRulePlaceholder,
                MembersDobRulePlaceholder,
                MembersLastName,
                MembersNationalId,
            ],
        }),
        [MembersPushdownRequired] = new(MembersPushdownRequired, "members", new TableEntitlementDescriptor
        {
            RowPredicate = Predicates.MembersScope,
            Enforcement = Enforcement.PushdownRequired,
            Columns =
            [
                MembersFirstName,
                MembersLastName,
                MembersDob,
                MembersNationalId,
            ],
        }),
        [MembersLocal] = new(MembersLocal, "members", new TableEntitlementDescriptor
        {
            RowPredicate = Predicates.MembersScope,
            Enforcement = Enforcement.Local,
            Columns =
            [
                MembersFirstName,
                MembersLastName,
                MembersDob,
                MembersNationalId,
            ],
        }),
        [MembersBlocked] = new(MembersBlocked, "members", new TableEntitlementDescriptor
        {
            RowPredicate = "(org_id IN (@ctx.manager_orgs)\n OR org_id IN "
                                + "(@ctx.agent_orgs)\n OR org_id IN (@ctx.auditor_orgs)\n OR "
                                + "@ctx.global)\nAND org_id NOT IN (@ctx.blocked_orgs)",
            Columns =
            [
                MembersFirstName,
                MembersLastName,
                MembersDob,
                MembersNationalId,
            ],
        }),
        [MembersStatistical] = new(MembersStatistical, "members", new TableEntitlementDescriptor
        {
            RowPredicate = Predicates.MembersScope,
            Columns =
            [
                MembersFirstName2,
                MembersLastName,
                MembersDob,
                MembersNationalId,
            ],
        }),
        [Orders] = new(Orders, "orders", new TableEntitlementDescriptor
        {
            RowPredicate = Predicates.OrdersScope,
            Columns = [OrdersNote, OrdersAmount],
        }),
        // D265 clause (h), group U. The same orders with the `Related` path beside their own
        // restrictions: one step **up** to the lines, one **down** to the items, and the endpoint's
        // own predicate at the far end. Restrictions OR, so any path grants.
        [OrdersRelated] = new(OrdersRelated, "orders", new TableEntitlementDescriptor
        {
            RowPredicate = Predicates.OrdersScope,
            Inherited =
            [
                new InheritedVisibilityDescriptor
                {
                    Kind = "vendor",
                    Steps =
                    [
                        new VisibilityStepDescriptor
                        {
                            Table = "order_items",
                            FromColumn = PolicyColumns.Orders.Id,
                            ToColumn = PolicyColumns.OrderItems.OrderId,
                            Direction = StepDirection.ToChild,
                        },
                        ToTheItem,
                    ],
                    EndpointPredicate = Predicates.ItemsScope,
                    EndpointTable = "items",
                },
            ],
            Columns = [OrdersNote, OrdersAmount, OrdersOrgIdRelated],
        }),
        [Vendors] = new(Vendors, "vendors", new TableEntitlementDescriptor
        {
            RowPredicate = Predicates.VendorsScope,
        }),
        [Items] = new(Items, "items", new TableEntitlementDescriptor
        {
            RowPredicate = Predicates.ItemsScope,
        }),
        // The bridge, entitled **by kind**: a line inherits its organisation and its member from
        // its order and its vendor from its item, so a vendor sees the lines that carry its own
        // goods and no other line of an order it can see. The kind-less `Through` of §3.13 would
        // have given it every line of that order.
        [OrderItems] = new(OrderItems, "order_items", new TableEntitlementDescriptor
        {
            Inherited =
            [
                new InheritedVisibilityDescriptor
                {
                    Kind = "org",
                    Steps = [ToTheOrder],
                    EndpointPredicate = Predicates.OrdersOrgKind,
                    EndpointTable = "orders",
                },
                new InheritedVisibilityDescriptor
                {
                    Kind = "member",
                    Steps = [ToTheOrder],
                    EndpointPredicate = Predicates.OrdersSubject,
                    EndpointTable = "orders",
                },
                new InheritedVisibilityDescriptor
                {
                    Kind = "vendor",
                    Steps = [ToTheItem],
                    EndpointPredicate = Predicates.ItemsScope,
                    EndpointTable = "items",
                },
            ],
            Columns = [OrderItemsUnitPrice],
        }),
        [OrdersByrules] = new(OrdersByrules, "orders", new TableEntitlementDescriptor
        {
            RowPredicate = Predicates.OrdersScope,
            Columns = [OrdersNote2, OrdersAmount2],
        }),
        [OrdersConfined] = new(OrdersConfined, "orders", new TableEntitlementDescriptor
        {
            RowPredicate = Predicates.OrdersConfinedScope,
            Columns = [OrdersNoteConfined, OrdersAmount],
        }),
        [OrdersNone] = new(OrdersNone, "orders", new TableEntitlementDescriptor
        {
            RowPredicate = Predicates.OrdersScope,
            DefaultDisclosure = Disclosure.None,
            Columns =
            [
                OrdersId,
                OrdersMemberId,
                OrdersOrgId,
                OrdersNote2,
                OrdersAmount3,
                OrdersStatus,
                OrdersCreatedBy,
            ],
        }),
        [OrdersFloor1] = new(OrdersFloor1, "orders", new TableEntitlementDescriptor
        {
            RowPredicate = Predicates.OrdersScope,
            Columns = [OrdersNote, OrdersAmount4],
        }),
        [OrdersFloorOff] = new(OrdersFloorOff, "orders", new TableEntitlementDescriptor
        {
            RowPredicate = Predicates.OrdersScope,
            Columns = [OrdersNote, OrdersAmount5],
        }),
        [OrdersStatistical] = new(OrdersStatistical, "orders", new TableEntitlementDescriptor
        {
            RowPredicate = Predicates.OrdersScope,
            Columns = [OrdersNote, OrdersAmount6],
        }),
        [OrdersCtxPath] = new(OrdersCtxPath, "orders", new TableEntitlementDescriptor
        {
            RowPredicate = "member_id IN (@ctx.scope_member_ids) OR created_by = @ctx.user "
                                + "OR @ctx.global",
            Columns = [OrdersNote, OrdersAmount],
        }),
        [Notes] = new(Notes, "notes", new TableEntitlementDescriptor
        {
            RowPredicate = Predicates.NotesScope,
            Columns = [NotesBody],
        }),
        [NotesByrules] = new(NotesByrules, "notes", new TableEntitlementDescriptor
        {
            RowPredicate = Predicates.NotesScope,
            Columns = [NotesBody2],
        }),
        [Invites] = new(Invites, "invites", new TableEntitlementDescriptor
        {
            RowPredicate = "org_id IN (@ctx.allowed_orgs)",
        }),
        [SymbolsUnentitled] = new(SymbolsUnentitled, "symbols", new TableEntitlementDescriptor
        {
        }),
        [BadOtherwiseFull] = new(BadOtherwiseFull, "members", new TableEntitlementDescriptor
        {
            RowPredicate = Predicates.MembersScope,
            Columns = [MembersPostcode2],
        }),
        [BadAggregateMax] = new(BadAggregateMax, "orders", new TableEntitlementDescriptor
        {
            RowPredicate = Predicates.OrdersScope,
            Columns = [OrdersAmount7],
        }),
        [BadWhenNotBoolean] = new(BadWhenNotBoolean, "members", new TableEntitlementDescriptor
        {
            RowPredicate = Predicates.MembersScope,
            Columns = [MembersFirstName3],
        }),
        [BadRuleMaskType] = new(BadRuleMaskType, "members", new TableEntitlementDescriptor
        {
            RowPredicate = Predicates.MembersScope,
            Columns = [MembersDob2],
        }),
        [BadMaskReadsProtected] = new(
            BadMaskReadsProtected, "members", new TableEntitlementDescriptor
            {
                RowPredicate = Predicates.MembersScope,
                Columns = [MembersFirstNameReadsLastName, MembersLastName],
            }),
    };

    /// <summary>
    /// The entitlement of that name, or null for a table this case leaves unentitled — which is what
    /// a name ending in <c>_unentitled</c> says, and what no name at all says.
    /// </summary>
    public static PolicyEntitlement? Find(string name)
    {
        if (string.IsNullOrEmpty(name) || name.EndsWith("_unentitled", StringComparison.Ordinal))
        {
            return null;
        }

        return All.TryGetValue(name, out var declared)
            ? declared
            : throw new InvalidOperationException(
                $"a case names the entitlement '{name}' and nothing declares it.");
    }
}

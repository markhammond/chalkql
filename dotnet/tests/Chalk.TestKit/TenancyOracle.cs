using System.Security.Cryptography;
using System.Text;
using Chalk.Client;
using Chalk.Sources.Poco;

namespace Chalk.TestKit;

/// <summary>
/// The oracle for the entitlement corpus (<c>docs/design/16-entitlements.md</c> §4, §9): the policy
/// of §8, evaluated naively row by row, in C#.
/// </summary>
/// <remarks>
/// <para>
/// It states the <em>model</em> — who manages, who acts as an agent, who audits, who is the subject,
/// who created the row — and derives each row's disclosure from it directly, with no descriptor, no
/// planner and no pass. The engine reaches the same answers by compiling that model into a
/// <c>TableEntitlement</c> and rewriting the statement; the two are independent statements of one
/// intent, so a disagreement is a real defect in one of them rather than a test agreeing with itself.
/// This is what §9's first exit criterion asks for, and what row-by-row assertions in a test could
/// not give: those catch a misclassification the author thought of.
/// </para>
/// <para>
/// The disclosed rows go into an ordinary POCO source that carries <b>no entitlement at all</b>, and
/// the statement runs over that. So the oracle's query path is the plain one every other corpus
/// family uses, and the only thing under test is what the pass did.
/// </para>
/// <para>
/// What it does not do is the group-size guard: suppressing a population aggregate below the floor
/// is a property of the <em>statement's</em> shape rather than of a row, and an oracle that took the
/// statement apart to find the guarded calls would be the pass again. The three corpus queries with
/// a guarded aggregate are recorded goldens instead, and say so.
/// </para>
/// </remarks>
public static class TenancyOracle
{
    /// <summary>A principal's grants, read back out of the context both sides are given.</summary>
    private sealed record Grants(
        int User,
        bool Global,
        string MaskKey,
        IReadOnlySet<int> ManagerOrgs,
        IReadOnlySet<int> AgentOrgs,
        IReadOnlySet<int> AuditorOrgs,
        IReadOnlySet<int> AllowedOrgs,
        IReadOnlySet<int> SupportOrgs,
        IReadOnlySet<int> CounterOrgs,
        IReadOnlySet<(int Subject, int Within)> SubjectPairs,
        IReadOnlySet<(int Org, int Region)> AuditorOrgRegions,
        IReadOnlySet<(int Subject, int Org, int Region)> SubjectOrgRegions,
        IReadOnlySet<int> Vendors);

    /// <summary>Members, as this principal may see them.</summary>
    public sealed record Member(
        int Id, int OrgId, string? FirstName, string? LastName, string? NationalId, string? Postcode);

    /// <summary>Orders, as this principal may see them.</summary>
    /// <remarks>
    /// <c>member_id</c> and <c>org_id</c> are nullable from D265 clause (h) onwards: a vendor's rule
    /// names both, so both are protected for every role (D216) and a principal that reached the row
    /// along the <c>Related</c> path alone receives the placeholder — the value for <c>org_id</c>,
    /// and for <c>member_id</c> a column it may test for equality and never read (D261).
    /// </remarks>
    public sealed record Order(
        int Id, int? MemberId, int? OrgId, long? Amount, string? Note, int CreatedBy, int RegionId);

    /// <summary>Notes, as this principal may see them.</summary>
    public sealed record Note(int Id, int OrgId, int CreatedBy, string? Body);

    /// <summary>Threads, as this principal may see them.</summary>
    public sealed record Thread(int Id, int OrgId, int MemberId);

    /// <summary>Messages, as this principal may see them — decided by the thread, never by the row.</summary>
    public sealed record Message(int Id, int ThreadId, string? Content, DateTime? FirstViewedAt);

    /// <summary>Attachments, decided by the message, which is decided by the thread.</summary>
    public sealed record Attachment(int Id, int MessageId, string Name);

    /// <summary>A vendor, as this principal may see them: its own row and no other.</summary>
    public sealed record Vendor(int Id, string Name);

    /// <summary>An item, likewise: the endpoint is where the key is, so the rule is one membership.</summary>
    public sealed record Item(int Id, int VendorId, string Name);

    /// <summary>
    /// A profile, as this principal may see it (D302): the contact card whole, or the NULL composite
    /// where the principal's rules withhold it.
    /// </summary>
    public sealed record Profile(int Id, int OrgId, TenancyFixture.ContactCard? Contact);

    /// <summary>An order line, as this principal may see it — and what it may read of the price.</summary>
    public sealed record OrderItem(
        int Id, int OrderId, int ItemId, int Quantity, long? UnitPrice);

    /// <summary>
    /// A source holding exactly the rows and values this principal is entitled to, and carrying no
    /// entitlement of its own — so a statement over it is an ordinary statement.
    /// </summary>
    /// <param name="context">The principal's bindings, the same object the engine is given.</param>
    /// <param name="creatorSeesFull">D206: what the creator of a note sees of their own row.</param>
    /// <param name="subversionFunctions">
    /// D251 class 4's two functions, for the family whose statements reach an entitled table
    /// through one. Off by default, so every other suite's oracle catalog is the one it was.
    /// </param>
    /// <param name="compositeTable">
    /// The adversarial family's composite-column table, <c>profiles</c> (D302). Off by default, so
    /// every other suite's oracle catalog is the one it was.
    /// </param>
    /// <param name="split">
    /// F84: leave <c>vendors</c> and <c>items</c> out, because design 38 §8's two-source layout puts
    /// them in a source of their own and <see cref="DiscloseMarketplace"/> is that source. The
    /// oracle has to wear the layout too, or the statement a family runs over the fixture would not
    /// resolve over the oracle.
    /// </param>
    public static PocoSource Disclose(
        RequestContext context,
        bool creatorSeesFull = true,
        bool subversionFunctions = false,
        bool split = false,
        bool compositeTable = false)
    {
        ArgumentNullException.ThrowIfNull(context);
        var grants = Read(context);

        var builder = new PocoSourceBuilder("mem")
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .AddTable("orgs", TenancyFixture.Orgs, t => t.OrderedBy(o => o.Id).UniqueKey(o => o.Id))
            .AddTable("regions", TenancyFixture.Regions, t => t.OrderedBy(r => r.Id).UniqueKey(r => r.Id))
            .AddTable("members", Members(grants), t => t.OrderedBy(m => m.Id).UniqueKey(m => m.Id))
            .AddTable("orders", Orders(grants), t => t.OrderedBy(o => o.Id).UniqueKey(o => o.Id))
            .AddTable(
                "notes", Notes(grants, creatorSeesFull), t => t.OrderedBy(n => n.Id).UniqueKey(n => n.Id))
            .AddTable("invites", Invites(grants), t => t.OrderedBy(i => i.Id).UniqueKey(i => i.Id))
            .AddTable("threads", Threads(grants), t => t.OrderedBy(x => x.Id).UniqueKey(x => x.Id))
            .AddTable("messages", Messages(grants), t => t.OrderedBy(m => m.Id).UniqueKey(m => m.Id))
            .AddTable(
                "attachments", Attachments(grants), t => t.OrderedBy(a => a.Id).UniqueKey(a => a.Id))
            .AddTable(
                "order_items", OrderItems(grants), t => t.OrderedBy(i => i.Id).UniqueKey(i => i.Id))
            .AddTable("symbols", TenancyFixture.Symbols, t => t.OrderedBy(s => s.Name).UniqueKey(s => s.Name))
            .AddFunction("is_vip", f => f.Scalar<int, bool>("id").Strict().Client());
        if (!split)
        {
            builder
                .AddTable("vendors", Vendors(grants), t => t.OrderedBy(v => v.Id).UniqueKey(v => v.Id))
                .AddTable("items", Items(grants), t => t.OrderedBy(i => i.Id).UniqueKey(i => i.Id));
        }

        if (compositeTable)
        {
            builder.AddTable(
                "profiles", Profiles(grants), t => t.OrderedBy(p => p.Id).UniqueKey(p => p.Id));
        }

        return (subversionFunctions ? TenancyFixture.Functions(builder) : builder).Build();
    }

    /// <summary>
    /// The marketplace's two tables as the second source of design 38 §8's two-source layout (F84),
    /// disclosing exactly what this principal is entitled to and carrying no entitlement of its own.
    /// </summary>
    public static PocoSource DiscloseMarketplace(RequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var grants = Read(context);
        return new PocoSourceBuilder("mem-catalogue", "catalogue")
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .AddTable("vendors", Vendors(grants), t => t.OrderedBy(v => v.Id).UniqueKey(v => v.Id))
            .AddTable("items", Items(grants), t => t.OrderedBy(i => i.Id).UniqueKey(i => i.Id))
            .Build();
    }

    /// <summary>What the oracle discloses to a principal, as values rather than as a source.</summary>
    /// <param name="Strings">Every string the principal's own rows hold, masks included.</param>
    /// <param name="Amounts">Every <c>orders.amount</c> the principal's own rows hold.</param>
    public sealed record Disclosed(IReadOnlyList<string> Strings, IReadOnlyList<long> Amounts);

    /// <summary>
    /// The same disclosure, read as values: D252's allowed set is the canaries these hold, and
    /// every other canary in the fixture is one this principal may not receive through any channel.
    /// </summary>
    /// <remarks>
    /// It is the very disclosure <see cref="Disclose"/> builds a source from, so the detector and
    /// the row comparison are two readings of one model rather than two models.
    /// </remarks>
    public static Disclosed DiscloseValues(RequestContext context, bool creatorSeesFull = true)
    {
        ArgumentNullException.ThrowIfNull(context);
        var grants = Read(context);

        var strings = new List<string>();
        var amounts = new List<long>();
        foreach (var member in Members(grants))
        {
            Add(strings, member.FirstName, member.LastName, member.NationalId, member.Postcode);
        }

        foreach (var order in Orders(grants))
        {
            Add(strings, order.Note);
            if (order.Amount is { } amount)
            {
                amounts.Add(amount);
            }
        }

        foreach (var note in Notes(grants, creatorSeesFull))
        {
            Add(strings, note.Body);
        }

        foreach (var message in Messages(grants))
        {
            Add(strings, message.Content);
        }

        foreach (var attachment in Attachments(grants))
        {
            Add(strings, attachment.Name);
        }

        foreach (var invite in Invites(grants))
        {
            Add(strings, invite.Target);
        }

        foreach (var profile in Profiles(grants))
        {
            Add(strings, profile.Contact?.Email, profile.Contact?.Phone);
        }

        // D265 clause (h)'s three tables carry no canary of their own — nothing on them is masked
        // or hidden by a rule that could leak a value from elsewhere — but they are what a vendor
        // receives, so the detector reads them with the rest.
        foreach (var vendor in Vendors(grants))
        {
            Add(strings, vendor.Name);
        }

        foreach (var item in Items(grants))
        {
            Add(strings, item.Name);
        }

        return new Disclosed(strings, amounts);
    }

    private static void Add(List<string> into, params string?[] values)
    {
        foreach (var value in values)
        {
            if (value is not null)
            {
                into.Add(value);
            }
        }
    }

    // ---------------------------------------------------------------- the model, row by row

    /// <summary>
    /// A member's row is visible where the principal holds any grant in its organization, holds a
    /// subject grant on it confined to that organization, or holds the global grant.
    /// </summary>
    private static IReadOnlyList<Member> Members(Grants grants)
    {
        var rows = new List<Member>();
        foreach (var member in TenancyFixture.Members)
        {
            var visible = grants.Global
                || grants.ManagerOrgs.Contains(member.OrgId)
                || grants.AgentOrgs.Contains(member.OrgId)
                || grants.AuditorOrgs.Contains(member.OrgId)
                || grants.SupportOrgs.Contains(member.OrgId)
                || grants.CounterOrgs.Contains(member.OrgId)
                || grants.SubjectPairs.Contains((member.Id, member.OrgId));
            if (!visible)
            {
                continue;
            }

            rows.Add(new Member(
                member.Id,
                member.OrgId,
                Protected(member.FirstName, member.OrgId, grants),
                Protected(member.LastName, member.OrgId, grants),
                // `national_id` is disclosed to nobody, so it is the placeholder for every
                // principal — the global grant included, and the support desk of D261 too: what a
                // test verdict grants is a comparison, never the value, and the comparison is
                // modelled below rather than here, because a source cannot hold a column that is
                // testable and not readable (ADR 0042).
                null,
                member.Postcode));
        }

        return rows;
    }

    /// <summary>
    /// A protected column, in the order the model combines its rules: the most permissive grant the
    /// principal holds in that organization wins, and nothing anywhere else discloses it.
    /// </summary>
    private static string? Protected(string value, int orgId, Grants grants)
    {
        if (grants.Global || grants.ManagerOrgs.Contains(orgId))
        {
            return value;
        }

        if (grants.AgentOrgs.Contains(orgId))
        {
            return value.Length == 0 ? value : value[..1];
        }

        if (grants.AuditorOrgs.Contains(orgId))
        {
            return Fingerprint(value, grants.MaskKey);
        }

        return null;
    }

    /// <summary>
    /// A profile's row is visible where the principal manages, acts in or audits its organisation, or
    /// holds the global grant (D302). Its card is disclosed whole to a manager and the global grant,
    /// to an agent only where the card's own tier is 2 or more, and to nobody else: the NULL composite.
    /// </summary>
    private static IReadOnlyList<Profile> Profiles(Grants grants)
    {
        var profiles = new List<Profile>();
        foreach (var profile in TenancyFixture.Profiles)
        {
            var org = profile.OrgId;
            if (!grants.ManagerOrgs.Contains(org)
                && !grants.AgentOrgs.Contains(org)
                && !grants.AuditorOrgs.Contains(org)
                && !grants.Global)
            {
                continue;
            }

            var disclosed = grants.ManagerOrgs.Contains(org)
                || grants.Global
                || (grants.AgentOrgs.Contains(org) && profile.Contact is { Tier: >= 2 });
            profiles.Add(new Profile(profile.Id, org, disclosed ? profile.Contact : null));
        }

        return profiles;
    }

    /// <summary>
    /// An order's row adds two things to the member's rule: the subject grant is on the *member* the
    /// order is about, and the created-by fail-safe makes a row the principal wrote visible wherever
    /// it is.
    /// </summary>
    private static IReadOnlyList<Order> Orders(Grants grants)
    {
        var rows = new List<Order>();
        foreach (var order in TenancyFixture.Orders)
        {
            // D266 §5, stated naively: a confined grant reaches this row when the row's dimension
            // of the grant's own kind is the grant's id *and*, for every confinement, the row's
            // dimension of that kind is the confinement's id. Both columns of the tuple at once —
            // which is why neither of these is the union of two grants.
            var regionalAuditor = Confined(grants.AuditorOrgRegions, order.OrgId, order.RegionId);
            var regionalSubject = Confined(
                grants.SubjectOrgRegions, order.MemberId, order.OrgId, order.RegionId);
            var subject = grants.SubjectPairs.Contains((order.MemberId, order.OrgId))
                || regionalSubject;
            var creator = order.CreatedBy == grants.User;
            // Every way in that is not the path's — D265 clause (h) calls this the organisation's
            // own reach, and §8's rule order puts it first, so a principal holding both grants sees
            // the value where a vendor alone meets the placeholder.
            var reached = grants.Global
                || grants.ManagerOrgs.Contains(order.OrgId)
                || grants.AgentOrgs.Contains(order.OrgId)
                || grants.AuditorOrgs.Contains(order.OrgId)
                // The two roles D261 adds hold no rule on this table, so they see its rows and
                // nothing protected on them — which is D216's inherit-is-deny, and what layer B's
                // compiler writes from the roles alone.
                || grants.SupportOrgs.Contains(order.OrgId)
                || grants.CounterOrgs.Contains(order.OrgId)
                || subject
                || regionalAuditor
                || creator;
            // And the path's: some line of this order carries an item of a vendor this principal's
            // vendor grants reach (design 38 §8). Zero related rows is invisible along that axis.
            var visible = reached || ReachedByAVendor(grants, order);
            if (!visible)
            {
                continue;
            }

            var full = grants.Global || grants.ManagerOrgs.Contains(order.OrgId) || creator;
            // `amount` is a *protected* column: a rule names it, so under D216 a role that says
            // nothing about it grants nothing. Only the auditor grants it — population-only, which
            // means the leaf emits it raw and tainted and only an allow-listed aggregate may consume
            // it — and the owner sees what they wrote. A manager, an agent and a subject say nothing
            // about `amount`, so they see none of it.
            var auditor = grants.AuditorOrgs.Contains(order.OrgId) || regionalAuditor;
            var amount = creator || auditor ? order.Amount : (long?)null;
            var note = full || subject
                ? order.Note
                : grants.AgentOrgs.Contains(order.OrgId) || auditor
                    ? "********"
                    : null;
            rows.Add(new Order(
                order.Id,
                reached ? order.MemberId : null,
                reached ? order.OrgId : null,
                amount,
                note,
                order.CreatedBy,
                order.RegionId));
        }

        return rows;
    }

    /// <summary>
    /// A thread's row resolves exactly as a member's does: the tenancy, the subject grant on the
    /// member it is about, and the global wildcard.
    /// </summary>
    private static IReadOnlyList<Thread> Threads(Grants grants)
    {
        var rows = new List<Thread>();
        foreach (var thread in TenancyFixture.Threads)
        {
            if (Sees(thread, grants))
            {
                rows.Add(new Thread(thread.Id, thread.OrgId, thread.MemberId));
            }
        }

        return rows;
    }

    private static bool Sees(TenancyFixture.Thread thread, Grants grants) =>
        grants.Global
        || grants.ManagerOrgs.Contains(thread.OrgId)
        || grants.AgentOrgs.Contains(thread.OrgId)
        || grants.AuditorOrgs.Contains(thread.OrgId)
        || grants.SupportOrgs.Contains(thread.OrgId)
        || grants.CounterOrgs.Contains(thread.OrgId)
        || grants.SubjectPairs.Contains((thread.MemberId, thread.OrgId));

    /// <summary>
    /// A message, evaluated <b>through its thread</b> and never off its own row (§3.13): the row is
    /// visible when the thread is, and <c>content</c> is disclosed by the roles held in the thread's
    /// organization — full for a manager or for the member the thread is about, an excerpt for an
    /// agent, and nothing for anybody else, an auditor included.
    /// </summary>
    private static IReadOnlyList<Message> Messages(Grants grants)
    {
        var rows = new List<Message>();
        foreach (var message in TenancyFixture.Messages)
        {
            var thread = Parent(message.ThreadId);
            if (thread is null || !Sees(thread, grants))
            {
                continue;
            }

            var full = grants.Global
                || grants.ManagerOrgs.Contains(thread.OrgId)
                || grants.SubjectPairs.Contains((thread.MemberId, thread.OrgId));
            var content = full
                ? message.Content
                : grants.AgentOrgs.Contains(thread.OrgId)
                    ? Excerpt(message.Content)
                    : null;
            rows.Add(new Message(message.Id, message.ThreadId, content, message.FirstViewedAt));
        }

        return rows;
    }

    /// <summary>An attachment is visible when its message is, which is a second hop (D226).</summary>
    private static IReadOnlyList<Attachment> Attachments(Grants grants)
    {
        var visible = new HashSet<int>();
        foreach (var message in Messages(grants))
        {
            visible.Add(message.Id);
        }

        var rows = new List<Attachment>();
        foreach (var attachment in TenancyFixture.Attachments)
        {
            if (visible.Contains(attachment.MessageId))
            {
                rows.Add(new Attachment(attachment.Id, attachment.MessageId, attachment.Name));
            }
        }

        return rows;
    }

    // ------------------------------------------------ related visibility (design 38 §8, D265 h)

    /// <summary>
    /// The vendors this principal may see: its own, which is what <c>Direct("vendor", "id")</c>
    /// says, plus every one under the global grant.
    /// </summary>
    private static IReadOnlyList<Vendor> Vendors(Grants grants) =>
    [
        .. TenancyFixture.Vendors
            .Where(v => grants.Global || grants.Vendors.Contains(v.Id))
            .Select(v => new Vendor(v.Id, v.Name)),
    ];

    /// <summary>The items likewise, by the key on their own row — and everything of them (§8).</summary>
    private static IReadOnlyList<Item> Items(Grants grants) =>
    [
        .. TenancyFixture.Items
            .Where(i => grants.Global || grants.Vendors.Contains(i.VendorId))
            .Select(i => new Item(i.Id, i.VendorId, i.Name)),
    ];

    /// <summary>
    /// <b>A line is visible along a kind when its parent along that kind is</b> (design 38 §8),
    /// stated naively: its order for the organisation and for the member, and its <em>item</em> for
    /// the vendor. The kind-scoped inheritance is the whole of the difference from §3.13's kind-less
    /// <c>Through</c>, which would have given a vendor every line of an order it can see.
    /// </summary>
    /// <remarks>
    /// <c>unit_price</c> follows the route: the value to a principal that reached the line through
    /// its order, the placeholder to one that reached it through its item alone. <c>quantity</c>
    /// carries no rule and is full for everyone that can see the row.
    /// </remarks>
    private static IReadOnlyList<OrderItem> OrderItems(Grants grants)
    {
        var rows = new List<OrderItem>();
        foreach (var line in TenancyFixture.OrderItems)
        {
            var order = TenancyFixture.Orders.Single(o => o.Id == line.OrderId);
            var item = TenancyFixture.Items.Single(i => i.Id == line.ItemId);
            var throughItsOrder = ReachesTheOrder(grants, order);
            var throughItsItem = grants.Global || grants.Vendors.Contains(item.VendorId);
            if (!throughItsOrder && !throughItsItem)
            {
                continue;
            }

            rows.Add(new OrderItem(
                line.Id,
                line.OrderId,
                line.ItemId,
                line.Quantity,
                throughItsOrder ? line.UnitPrice : null));
        }

        return rows;
    }

    /// <summary>
    /// Every way into an order <b>by kind</b>: the organisation's roles, the subject's grants
    /// confined or not, and the global grant. It is what a line inherits for <c>org</c> and for
    /// <c>member</c>, and what §8's rule order puts first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The creator's fail-safe is deliberately absent. It admits an order to the person who wrote
    /// it, and it is a way into <em>that row</em> rather than a tenancy of any kind — so a line,
    /// which inherits its order's perspectives kind by kind (§8), does not inherit it. The compiled
    /// policy writes exactly the kind's membership as the endpoint predicate (§2), and the two
    /// layers are held to each other on it.
    /// </para>
    /// </remarks>
    private static bool ReachesTheOrder(Grants grants, TenancyFixture.Order order) =>
        grants.Global
        || grants.ManagerOrgs.Contains(order.OrgId)
        || grants.AgentOrgs.Contains(order.OrgId)
        || grants.AuditorOrgs.Contains(order.OrgId)
        || grants.SupportOrgs.Contains(order.OrgId)
        || grants.CounterOrgs.Contains(order.OrgId)
        || Confined(grants.AuditorOrgRegions, order.OrgId, order.RegionId)
        || grants.SubjectPairs.Contains((order.MemberId, order.OrgId))
        || Confined(grants.SubjectOrgRegions, order.MemberId, order.OrgId, order.RegionId);

    /// <summary>
    /// <b>An order is visible when some line of it carries an item of a vendor the principal's
    /// vendor grants reach</b> (design 38 §8), which is the other half of the rule <c>Orders</c>
    /// states. Zero related rows is invisible along that axis: existence is per row (§4).
    /// </summary>
    private static bool ReachedByAVendor(Grants grants, TenancyFixture.Order order)
    {
        foreach (var line in TenancyFixture.OrderItems)
        {
            if (line.OrderId != order.Id)
            {
                continue;
            }

            var item = TenancyFixture.Items.Single(i => i.Id == line.ItemId);
            if (grants.Vendors.Contains(item.VendorId))
            {
                return true;
            }
        }

        return false;
    }

    private static TenancyFixture.Thread? Parent(int id)
    {
        foreach (var thread in TenancyFixture.Threads)
        {
            if (thread.Id == id)
            {
                return thread;
            }
        }

        return null;
    }

    /// <summary>The excerpt an agent sees of a message: the descriptor's own mask, in C#.</summary>
    private static string Excerpt(string content) =>
        content.Length <= 9 ? content : content[..9];

    /// <summary>Notes carry the created-by fail-safe and D206's choice about what the creator sees.</summary>
    private static IReadOnlyList<Note> Notes(Grants grants, bool creatorSeesFull)
    {
        var rows = new List<Note>();
        foreach (var note in TenancyFixture.Notes)
        {
            var creator = note.CreatedBy == grants.User;
            var visible = grants.Global
                || grants.ManagerOrgs.Contains(note.OrgId)
                || grants.AgentOrgs.Contains(note.OrgId)
                || creator;
            if (!visible)
            {
                continue;
            }

            var body = (creatorSeesFull && creator)
                || grants.Global
                || grants.ManagerOrgs.Contains(note.OrgId)
                    ? note.Body
                    : null;
            rows.Add(new Note(note.Id, note.OrgId, note.CreatedBy, body));
        }

        return rows;
    }

    /// <summary>The Custom-mode table: a host-computed list and nothing else.</summary>
    private static IReadOnlyList<TenancyFixture.Invite> Invites(Grants grants) =>
        [.. TenancyFixture.Invites.Where(i => grants.AllowedOrgs.Contains(i.OrgId))];

    /// <summary>HMAC-SHA-256 of the value's UTF-8 bytes, keyed, first sixteen bytes as hex.</summary>
    private static string Fingerprint(string value, string key)
    {
        var digest = HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(digest.AsSpan(0, 16)).ToLowerInvariant();
    }

    // ---------------------------------------------------------------- reading the context

    // ---------------------------------------------------------------- the test verdict (D261)

    /// <summary>The members this principal can see at all, raw — the naive model's own rows.</summary>
    /// <remarks>
    /// The disclosed source above cannot express a column that is <em>testable and not readable</em>:
    /// it holds values, and the answer to "is this the value?" is not one of them. So the test
    /// verdict's model is stated here, over the fixture's own rows, and the statements that test are
    /// held to it by <c>TestVerdictTests</c> and to a recorded golden by the corpus, exactly as the
    /// three statements with a guarded aggregate already are (ADR 0042,
    /// <c>docs/design/36-test-verdict.md</c> §4).
    /// </remarks>
    public static IReadOnlyList<TenancyFixture.Member> VisibleMembers(RequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var grants = Read(context);
        var rows = new List<TenancyFixture.Member>();
        foreach (var member in TenancyFixture.Members)
        {
            if (grants.Global
                || grants.ManagerOrgs.Contains(member.OrgId)
                || grants.AgentOrgs.Contains(member.OrgId)
                || grants.AuditorOrgs.Contains(member.OrgId)
                || grants.SupportOrgs.Contains(member.OrgId)
                || grants.CounterOrgs.Contains(member.OrgId)
                || grants.SubjectPairs.Contains((member.Id, member.OrgId)))
            {
                rows.Add(member);
            }
        }

        return rows;
    }

    /// <summary>
    /// What a permitted comparison of <c>national_id</c> answers for one visible row, three-valued
    /// (D261 §3): the raw value compared with the parameter where the principal may test that row,
    /// and unknown where it may not — which is what the placeholder gives and what a predicate, a
    /// <c>FILTER</c> and a select list each read as "not this row".
    /// </summary>
    public static bool? Tested(RequestContext context, TenancyFixture.Member member, string? parameter)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(member);
        if (!Read(context).SupportOrgs.Contains(member.OrgId))
        {
            return null;
        }

        return parameter is null
            ? null
            : string.Equals(member.NationalId, parameter, StringComparison.Ordinal);
    }

    /// <summary>The same for the counting grant, which is the aggregate form's own verdict.</summary>
    public static bool? Counted(RequestContext context, TenancyFixture.Member member, string? parameter)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(member);
        if (!Read(context).CounterOrgs.Contains(member.OrgId))
        {
            return null;
        }

        return parameter is null
            ? null
            : string.Equals(member.NationalId, parameter, StringComparison.Ordinal);
    }

    private static Grants Read(RequestContext context) => new(
        Scalar(context, "user") is int user ? user : 0,
        Scalar(context, "global") is true,
        Scalar(context, "mask_key") as string ?? string.Empty,
        Ids(context, "manager_orgs"),
        Ids(context, "agent_orgs"),
        Ids(context, "auditor_orgs"),
        Ids(context, "allowed_orgs"),
        Ids(context, "support_orgs"),
        Ids(context, "counter_orgs"),
        Pairs(context, "subject_pairs"),
        // The two conjoined groups of D266: an auditor confined to one region of one organisation,
        // and a subject confined to both at once. A principal holding neither binds them empty,
        // which is what makes their terms false and every other principal's answer what it was.
        Pairs(context, "auditor_org_regions"),
        Triples(context, "subject_org_regions"),
        // D265 clause (h): the vendor kind, held in the `vendor` role.
        Ids(context, "vendor_vendor"));

    private static object? Scalar(RequestContext context, string name) =>
        context.Scalars.TryGetValue(name, out var value) ? value : null;

    private static IReadOnlySet<int> Ids(RequestContext context, string name) =>
        context.Lists.TryGetValue(name, out var relation)
            ? relation.Rows.Select(r => Convert.ToInt32(r[0], System.Globalization.CultureInfo.InvariantCulture)).ToHashSet()
            : new HashSet<int>();

    /// <summary>
    /// D266 §5's rule, stated the naive way: a confined grant reaches a row when the row's dimension
    /// of the grant's own kind is the grant's id <b>and</b>, for every confinement, the row's
    /// dimension of that kind is the confinement's id. It is one membership test over the tuple,
    /// which is exactly what the compiler writes and what this states independently.
    /// </summary>
    public static bool Confined(IReadOnlySet<(int Org, int Region)> grants, int org, int region) =>
        grants.Contains((org, region));

    /// <summary>The same for a subject grant confined along both kinds at once (D266 §5).</summary>
    public static bool Confined(
        IReadOnlySet<(int Subject, int Org, int Region)> grants, int subject, int org, int region) =>
        grants.Contains((subject, org, region));

    private static IReadOnlySet<(int, int, int)> Triples(RequestContext context, string name) =>
        context.Lists.TryGetValue(name, out var relation)
            ? relation.Rows
                .Select(r => (
                    Convert.ToInt32(r[0], System.Globalization.CultureInfo.InvariantCulture),
                    Convert.ToInt32(r[1], System.Globalization.CultureInfo.InvariantCulture),
                    Convert.ToInt32(r[2], System.Globalization.CultureInfo.InvariantCulture)))
                .ToHashSet()
            : new HashSet<(int, int, int)>();

    private static IReadOnlySet<(int, int)> Pairs(RequestContext context, string name) =>
        context.Lists.TryGetValue(name, out var relation)
            ? relation.Rows
                .Select(r => (
                    Convert.ToInt32(r[0], System.Globalization.CultureInfo.InvariantCulture),
                    Convert.ToInt32(r[1], System.Globalization.CultureInfo.InvariantCulture)))
                .ToHashSet()
            : new HashSet<(int, int)>();
}

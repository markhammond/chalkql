using Chalk.Catalog;
using Chalk.Client;
using Chalk.Entitlements.Tenancy;
using Chalk.Sources.Poco;
using CatalogContext = Chalk.Catalog.CatalogContext;

namespace Chalk.TestKit;

/// <summary>
/// The same <c>tenancy</c> fixture as <see cref="TenancyFixture"/>, with the entitlements
/// <b>produced by the tenancy package</b> from the declarations of
/// <c>docs/design/16-entitlements.md</c> §8 rather than written out as descriptors.
/// </summary>
/// <remarks>
/// <para>
/// This is layer B where <see cref="TenancyFixture"/> is layer A alone, and the two are held to the
/// same answer: the corpus runs through both and must agree query for query and principal for
/// principal. That is the only check that says the compiler compiles the model rather than something
/// near it — a descriptor that reads plausibly and discloses one row too many looks exactly like a
/// correct one until the two paths are compared.
/// </para>
/// <para>
/// The declarations are §8's, transcribed: a tenancy dimension <c>org</c>, a subject dimension
/// <c>member</c> confined within it, <c>orders</c> tenanted through the path <c>member.org</c> over
/// the declared foreign key, the realms <c>pii</c> and <c>restricted</c>, and the three roles' access
/// rules. The fourth role, <c>self</c>, is what a subject grant is held in: a grant carries a role
/// (D204), and it is the role that decides what the subject sees of their own row — nothing of a
/// member's name, and their own order's note in full, which is what §8's fixture says.
/// </para>
/// </remarks>
public sealed class TenancyPolicyFixture
{
    private TenancyPolicyFixture(PocoSource source, TenancyEntitlements entitlements)
    {
        Source = source;
        Entitlements = entitlements;
        Catalog = new CatalogContext
        {
            ContextId = TenancyFixture.ContextId,
            Epoch = TenancyFixture.Epoch,
            Schemas = [source.DescribeSchema()],
        };
        CatalogValidator.Validate(Catalog);
    }

    public PocoSource Source { get; }

    public TenancyEntitlements Entitlements { get; }

    public CatalogContext Catalog { get; }

    public static TenancyPolicyFixture Shared => LazyShared.Value;

    private static readonly Lazy<TenancyPolicyFixture> LazyShared = new(() => Create());

    // ---------------------------------------------------------------- the declarations

    /// <summary>
    /// §8's model, as declarations. Nothing here is SQL; the compiler writes that.
    /// </summary>
    /// <remarks>
    /// The policy is declared over a catalog and every table is obtained from the source it belongs
    /// to, which is what lets a policy speak about two sources without their tables being told apart
    /// by name alone (D270, <c>docs/design/45-typed-tenancy-surface.md</c> §1–§2 as amended
    /// 2026-09-16). This fixture has one source, so it names it once.
    /// </remarks>
    public static TenancyPolicy Policy(
        SchemaDescriptor schema, ResourceOwnerSees ownerSees = ResourceOwnerSees.Full) =>
        Policy(schema, ownerSees, explicitManager: false);

    /// <summary>
    /// The same model with the manager role saying what it sees of a protected column no rule of its
    /// own names — D216's explicit form.
    /// </summary>
    /// <remarks>
    /// §8 gives <c>orders.amount</c> one rule, the auditor's, and <c>amount</c> is therefore a
    /// protected column: under D216 a role that says nothing about it grants nothing, so a manager's
    /// <c>amount</c> is redacted. That is the ratified reading and the corpus's goldens record it.
    /// This is the other half of the same decision — a role that <em>should</em> see everything the
    /// policy protects says so — and it is a variant rather than a change to §8's fixture, because
    /// both readings are things a host writes and the difference between them is the point. Under
    /// D222 it is an ordinary rule naming neither realm nor column, written last, which is where a
    /// fallback belongs.
    /// </remarks>
    public static TenancyPolicy ExplicitPolicy(SchemaDescriptor schema) =>
        Policy(schema, ResourceOwnerSees.Full, explicitManager: true);

    private static TenancyPolicy Policy(
        SchemaDescriptor schema, ResourceOwnerSees ownerSees, bool explicitManager)
    {
        var policy = TenancyPolicy.Declare(new CatalogContext
        {
            ContextId = TenancyFixture.ContextId,
            Epoch = TenancyFixture.Epoch,
            Schemas = [schema],
        });
        Body(policy, policy.Source(SourceName), policy.Source(SourceName), ownerSees, explicitManager);
        return policy;
    }

    /// <summary>
    /// The very same model over design 38 §8's <b>two-source</b> layout (F84): the marketplace's two
    /// tables are obtained from a second source, and the step from the line to the item resolves
    /// through the declared association rather than through a foreign key
    /// (<c>docs/design/45-typed-tenancy-surface.md</c> §3, D270 (c)).
    /// </summary>
    /// <remarks>
    /// One <see cref="Body"/> and two layouts, so "the same policy as the co-located fixture" is a
    /// fact about the code rather than a claim about two transcriptions of it. Everything the
    /// declarations say is identical; what differs is which source each handle came from, and the
    /// one line that states across two sources what a foreign key states within one.
    /// </remarks>
    public static TenancyPolicy SplitPolicy(CatalogContext catalog)
    {
        var policy = TenancyPolicy.Declare(catalog);
        Body(
            policy,
            policy.Source(SourceName),
            policy.Source(MarketplaceSourceName),
            ResourceOwnerSees.Full,
            explicitManager: false);
        return policy;
    }

    /// <summary>The name of the one source this fixture's tables live in.</summary>
    public const string SourceName = "main";

    /// <summary>
    /// Where <c>vendors</c> and <c>items</c> live in the two-source layout of design 38 §8 — the
    /// endpoint of the path and the table its kind is held on, one source away from the target and
    /// the bridge (F84).
    /// </summary>
    public const string MarketplaceSourceName = "catalogue";

    /// <summary>
    /// The manager's explicit grant over every protected column no rule of its own names (D216), as
    /// D222 makes it: an ordinary rule naming neither realm nor column, placed <b>last</b>, so that
    /// it is what a manager falls through to and never what overrides an earlier rule.
    /// </summary>
    private static void ExplicitManagerRule(Table table, Role manager, bool explicitManager)
    {
        if (explicitManager)
        {
            table.Access(new AccessRule { Roles = [manager], Grants = Verdict.Full });
        }
    }

    /// <param name="source">Where every table but the marketplace's two lives.</param>
    /// <param name="marketplace">
    /// Where <c>vendors</c> and <c>items</c> live: the same source in the co-located layout, and a
    /// second one in design 38 §8's two-source layout (F84). The only other thing that follows from
    /// it is the association below, which states across two sources what a foreign key states within
    /// one.
    /// </param>
    private static void Body(
        TenancyPolicy p,
        Source source,
        Source marketplace,
        ResourceOwnerSees ownerSees,
        bool explicitManager)
    {
        // Every name enters once, here, and every later mention is the handle it came back as
        // (D270 §1). A misspelling is a name this policy does not declare rather than a string that
        // silently matches nothing.

        var manager = p.Role("manager");
        var agent = p.Role("agent");
        var auditor = p.Role("auditor");
        var self = p.Role("self");
        var support = p.Role("support");
        var counter = p.Role("counter");
        var vendorRole = p.Role("vendor");

        p.Scope("interactive");
        p.Scope("batch");
        p.AllowGlobalGrants();

        // The kinds, declared once for the whole policy (D265 §1, §8): a table then names one and
        // nothing more about it.
        var org = p.Tenancy("org");

        // The second tenancy kind (D266 §6). A tenancy kind may be confined by any other declared
        // tenancy kind, and a subject kind names the ones it admits — here both, so a member grant
        // may be confined to an organisation, to a region, or to both at once.
        var region = p.Tenancy("region");

        // The third tenancy kind (D265 clause (h), design 38 §8). A row of `orders` holds it along a
        // `Related` path rather than on its own row, which is the whole of what D265 builds;
        // `vendors` and `items` hold it `Direct`ly.
        var vendor = p.Tenancy("vendor");
        var member = p.Subject("member", within: [org, region]);

        var pii = p.Realm("pii");
        var restricted = p.Realm("restricted");

        var members = source.Table("members");
        var orders = source.Table("orders");
        var notes = source.Table("notes");
        var threads = source.Table("threads");
        var messages = source.Table("messages");
        var attachments = source.Table("attachments");
        var vendors = marketplace.Table("vendors");
        var items = marketplace.Table("items");
        var orderItems = source.Table("order_items");
        var invites = source.Table("invites");

        // The declaration a path step across two sources resolves through (D270 (c), design 45 §3):
        // a foreign key is one source's claim about a table of its own schema, and this is the
        // host's claim across two. Declared and not verified, as a foreign key already is. In the
        // co-located layout the two handles are one source's and `order_items` declares the ordinary
        // foreign key instead, so nothing is stated twice.
        if (!marketplace.Equals(source))
        {
            orderItems.Column("item_id").References(items.Column("id"));
        }

        members
            .Tenancy(t => t
                .Direct(org, members.Column("org_id"))
                .Direct(member, members.Column("id")))
            .Realm(pii, members.Column("first_name"), members.Column("last_name"))
            .Realm(restricted, members.Column("national_id"))
            .Access(new AccessRule { Roles = [manager], Realm = pii, Grants = Verdict.Full })
            .Access(new AccessRule
            {
                Roles = [agent],
                Realm = pii,
                Grants = Verdict.Mask,
                Mask = Sql.Of("lambda v: SUBSTRING(v, 1, 1)"),
            })
            .Access(new AccessRule
            {
                // The per-rule mask: an agent sees the initial, an auditor sees a token. The
                // fingerprint is keyed and stable, so an equality join on it matches rows across
                // tenancies with neither name disclosed.
                Roles = [auditor],
                Realm = pii,
                Grants = Verdict.Mask,
                Mask = Sql.Of("lambda v: FINGERPRINT(v, @ctx.mask_key)"),
            })
            // D261. A support desk may confirm a member's national id and never see it: each
            // permitted comparison is computed in the leaf over the raw value and disclosed as one
            // boolean, and every other use sees the placeholder.
            .Access(new AccessRule
            {
                Roles = [support],
                Column = members.Column("national_id"),
                Grants = Verdict.Test,
                Tests = [Test.Equals, Test.NotEquals, Test.In],
            })
            // And a role that may count matches rather than confirm one, guarded by k: the aggregate
            // form, where the shape is what a permitted aggregate's FILTER may say.
            .Access(new AccessRule
            {
                Roles = [counter],
                Column = members.Column("national_id"),
                Grants = Verdict.AggregateOnly,
                Aggregates = [Aggregate.Count],
                Tests = [Test.Equals],
                MinGroupSize = TenancyFixture.MinGroupSize,
            });
        ExplicitManagerRule(members, manager, explicitManager);

        orders
            .Tenancy(t => t
                // Through the association rather than by naming a column: the compiler proves the
                // foreign key and the kind, and refuses the path if either moves.
                .Direct(org, orders.Column("member.org"))
                .Direct(member, orders.Column("member_id"))
                // The region is on an order's row and on nothing else, which is what makes a
                // confined grant reach `orders` and never a member's own row (D266 §3).
                .Direct(region, orders.Column("region_id"))
                .ResourceOwner(orders.Column("created_by"))
                // D265 clause (h): an order is also visible to a vendor some line of it carries an
                // item of — one step up to the bridge, one down to the endpoint. The bridge's own
                // entitlement is never consulted, which is what keeps this acyclic where the line is
                // itself entitled through the order.
                .Related(vendor).Through(orderItems).Through(items))
            .Realm(pii, orders.Column("note"))
            .Access(new AccessRule { Roles = [manager, self], Realm = pii, Grants = Verdict.Full })
            .Access(new AccessRule { Roles = [agent, auditor], Realm = pii, Grants = Verdict.Mask })
            .Access(new AccessRule
            {
                Roles = [auditor],
                Column = orders.Column("amount"),
                Grants = Verdict.AggregateOnly,
                Aggregates = [Aggregate.Count, Aggregate.Sum, Aggregate.Sum0, Aggregate.Avg],
                MinGroupSize = TenancyFixture.MinGroupSize,
            })
            // D265 clause (h), design 38 §8. What a vendor reading an order along the path may have
            // of the two columns that say whose order it is: the organisation as a placeholder, and
            // the member as a column it may **test** for equality and never read (D261 in its
            // natural home). Reaching a row through the rows that belong to it discloses the row's
            // existence and whatever its own rules say, never more.
            //
            // The order is what keeps every other principal byte-identical (D216, D222). The
            // organisation's roles are written first and stop on match, so a principal holding both
            // grants reads the value on an order its organisation reaches and meets the placeholder
            // only on the orders it reaches along the path alone. The vendor's rule is second, and
            // its condition is the *endpoint's* — the compiler writes a path perspective's role
            // scope over `items.vendor_id`, evaluated once per endpoint row (§4, D228). The
            // catch-all §8 names last is the resource-owner fail-safe the compiler already emits
            // ahead of every rule (D206, D215): a principal that reaches an order by having created
            // it reads its columns as it always did, which is what a role-scoped rule alone would
            // have taken away.
            // `Roles.Visible` is what layer A writes by hand as the table's own row predicate
            // (D269 (c), F75): every way into an order that is not the path's — every declared
            // role's scope, the creator's fail-safe and the global grant — said once. Written as the
            // six roles instead, the condition would be narrower than the predicate by exactly those
            // two disjuncts, §3.3's fold could not read the column as a constant, and the two layers
            // would report different labels for the same rows.
            .Access(new AccessRule
            {
                Roles = [Roles.Visible],
                Column = orders.Column("org_id"),
                Grants = Verdict.Full,
            })
            .Access(new AccessRule
            {
                Roles = [vendorRole],
                Column = orders.Column("org_id"),
                Grants = Verdict.None,
            })
            .Access(new AccessRule
            {
                Roles = [Roles.Visible],
                Column = orders.Column("member_id"),
                Grants = Verdict.Full,
            })
            .Access(new AccessRule
            {
                Roles = [vendorRole],
                Column = orders.Column("member_id"),
                Grants = Verdict.Test,
                Tests = [Test.Equals],
            });
        ExplicitManagerRule(orders, manager, explicitManager);

        notes
            .Tenancy(t => t
                .Direct(org, notes.Column("org_id"))
                .ResourceOwner(notes.Column("created_by"), ownerSees))
            // Notes are an operational table: an auditor holds no view of them at all.
            .Visible(new VisibilityRule { Roles = [manager, agent] })
            .Realm(pii, notes.Column("body"))
            .Access(new AccessRule { Roles = [manager], Realm = pii, Grants = Verdict.Full });
        ExplicitManagerRule(notes, manager, explicitManager);

        // The parent: an ordinary tenanted table, and the subject dimension is what lets a grant on
        // one member reach the thread that member is about (§3.13).
        threads.Tenancy(t => t
            .Direct(org, threads.Column("org_id"))
            .Direct(member, threads.Column("member_id")));

        // No tenancy column of its own: a message is visible when its thread is, and its content is
        // disclosed by the roles held in the thread's organization (D227, D228).
        messages
            .Tenancy(t => t.Through(messages.Column("thread_id")))
            .Realm(pii, messages.Column("content"))
            .Access(new AccessRule { Roles = [manager, self], Realm = pii, Grants = Verdict.Full })
            .Access(new AccessRule
            {
                Roles = [agent],
                Realm = pii,
                Grants = Verdict.Mask,
                Mask = Sql.Of("lambda v: SUBSTRING(v, 1, 9)"),
            });

        attachments.Tenancy(t => t.Through(attachments.Column("message_id")));

        // D265 clause (h)'s three tables. The key is on the row for both ends of the path, and the
        // bridge inherits each of its order's perspectives *by kind* — so a vendor sees the lines
        // that carry its own goods and no other line of an order it can see, which the kind-less
        // `Through` of §3.13 would not have given.
        vendors.Tenancy(t => t.Direct(vendor, vendors.Column("id")));
        items.Tenancy(t => t.Direct(vendor, items.Column("vendor_id")));
        orderItems
            .Tenancy(t => t
                .Inherited(org).Through(orders)
                .Inherited(member).Through(orders)
                .Inherited(vendor).Through(items))
            .Access(new AccessRule
            {
                Roles = [manager, agent, auditor, self, support, counter],
                Column = orderItems.Column("unit_price"),
                Grants = Verdict.Full,
            })
            .Access(new AccessRule
            {
                Roles = [vendorRole],
                Column = orderItems.Column("unit_price"),
                Grants = Verdict.None,
            });

        invites.Tenancy(t => t.Predicate(Sql.Of("org_id IN (@ctx.allowed_orgs)")));

        source.Table("regions").Unrestricted();
        source.Table("symbols").Unrestricted();
    }

    /// <summary>
    /// The same model over two sources, and the association between them (F84): the catalog this
    /// compiles against is the two schemas together, because a policy that spans sources cannot be
    /// declared against one of them (D270, design 45 §1 as amended).
    /// </summary>
    public static (TenancyEntitlements Entitlements, IReadOnlyList<AssociationDescriptor> Associations)
        SplitEntitlements(SchemaDescriptor main, SchemaDescriptor marketplace)
    {
        var bare = new CatalogContext
        {
            ContextId = TenancyFixture.ContextId,
            Epoch = TenancyFixture.Epoch,
            Schemas = [main, marketplace],
        };
        var policy = SplitPolicy(bare);
        var associations = policy.Associations;
        return (
            policy.Compile(new CatalogContext
            {
                ContextId = TenancyFixture.ContextId,
                Epoch = TenancyFixture.Epoch,
                Schemas = [main, marketplace],
                Associations = associations,
            }),
            associations);
    }

    // ---------------------------------------------------------------- the catalog


    /// <summary>
    /// Builds the fixture: the tables once without entitlements to get a schema the compiler can be
    /// validated against, then again with what it produced.
    /// </summary>
    public static TenancyPolicyFixture Create(ResourceOwnerSees ownerSees = ResourceOwnerSees.Full)
    {
        var schema = Tables(null).DescribeSchema();
        var entitlements = Policy(schema, ownerSees).Compile([schema]);
        return new TenancyPolicyFixture(Tables(entitlements), entitlements);
    }

    /// <summary>The same fixture with the manager role's explicit default (D216).</summary>
    public static TenancyPolicyFixture Explicit => LazyExplicit.Value;

    private static readonly Lazy<TenancyPolicyFixture> LazyExplicit = new(() =>
    {
        var schema = Tables(null).DescribeSchema();
        var entitlements = ExplicitPolicy(schema).Compile([schema]);
        return new TenancyPolicyFixture(Tables(entitlements), entitlements);
    });

    /// <summary>
    /// The schema this fixture's tables describe, for a test that has to declare a policy of its own
    /// against it.
    /// </summary>
    public static SchemaDescriptor Schema { get; } = Tables(null).DescribeSchema();

    /// <summary>
    /// The handles §8's principals hold their grants in. A grant carries the kind and the role it
    /// was declared as (D270), so the principals are written against one canonical policy over
    /// <see cref="Schema"/> and bound by any compilation of the same model.
    /// </summary>
    public static TenancyPolicy Handles { get; } = Policy(Schema);

    /// <summary>
    /// The principal the explicit variant exists for: a manager in O1 and nothing else, whose role
    /// declares what it sees of a protected column no rule names.
    /// </summary>
    public static TenancyPrincipal ExplicitManager { get; } =
        Principal(1, Grant.ForTenancy(Org, 1, Manager));

    /// <summary>The kinds and roles §8's grants name, as the handles they were declared as.</summary>
    public static Kind Org => Handles.Tenancy("org");

    public static Kind Region => Handles.Tenancy("region");

    public static Kind Vendor => Handles.Tenancy("vendor");

    public static SubjectKind Member => Handles.Subject("member", within: [Org, Region]);

    public static Role Manager => Handles.Role("manager");

    public static Role Agent => Handles.Role("agent");

    public static Role Auditor => Handles.Role("auditor");

    public static Role Self => Handles.Role("self");

    public static Role Support => Handles.Role("support");

    public static Role Counter => Handles.Role("counter");

    public static Role VendorRole => Handles.Role("vendor");

    /// <summary>
    /// The two-source layout of design 38 §8 (F84): everything but the marketplace's endpoint and
    /// the table its kind is held on, which are in <see cref="MarketplaceTables"/>.
    /// </summary>
    /// <remarks>
    /// The one difference from <see cref="Tables"/> beyond which tables are here: <c>order_items</c>
    /// declares no foreign key to <c>items</c>, because a foreign key names a table of its own
    /// schema. What stands in its place is the association the policy declares, which is what a step
    /// across two sources resolves through (D270 (c)).
    /// </remarks>
    public static PocoSource MainTables(TenancyEntitlements? entitlements) =>
        Tables(entitlements, split: true);

    /// <summary>The marketplace's two tables, in a source of their own (F84).</summary>
    public static PocoSource MarketplaceTables(TenancyEntitlements? entitlements)
    {
        var builder = new PocoSourceBuilder("mem-catalogue", MarketplaceSourceName)
            .NamingPolicy(PocoNamingPolicy.SnakeCase);

        builder.AddTable("vendors", TenancyFixture.Vendors, t =>
        {
            t.OrderedBy(v => v.Id).UniqueKey(v => v.Id);
            Attach(t, entitlements, MarketplaceSourceName, "vendors");
        });

        builder.AddTable("items", TenancyFixture.Items, t =>
        {
            t.OrderedBy(i => i.Id).UniqueKey(i => i.Id).ForeignKey(i => i.VendorId)
                .References<TenancyFixture.Vendor>(v => v.Id, verify: true);
            Attach(t, entitlements, MarketplaceSourceName, "items");
        });

        return builder.Build();
    }

    private static PocoSource Tables(TenancyEntitlements? entitlements) =>
        Tables(entitlements, split: false);

    private static PocoSource Tables(TenancyEntitlements? entitlements, bool split)
    {
        var builder = new PocoSourceBuilder("mem").NamingPolicy(PocoNamingPolicy.SnakeCase);

        builder.AddTable("orgs", TenancyFixture.Orgs, t => t.OrderedBy(o => o.Id).UniqueKey(o => o.Id));

        builder.AddTable(
            "regions", TenancyFixture.Regions, t => t.OrderedBy(r => r.Id).UniqueKey(r => r.Id));

        builder.AddTable("members", TenancyFixture.Members, t =>
        {
            t.OrderedBy(m => m.Id).UniqueKey(m => m.Id).ForeignKey(m => m.OrgId)
                .References<TenancyFixture.Org>(o => o.Id, verify: true);
            Attach(t, entitlements, "members");
        });

        builder.AddTable("orders", TenancyFixture.Orders, t =>
        {
            t.OrderedBy(o => o.Id).UniqueKey(o => o.Id).ForeignKey(o => o.MemberId)
                .References<TenancyFixture.Member>(m => m.Id, verify: true)
                .ForeignKey(o => o.RegionId)
                .References<TenancyFixture.Region>(r => r.Id, verify: true);
            Attach(t, entitlements, "orders");
        });

        builder.AddTable("notes", TenancyFixture.Notes, t =>
        {
            t.OrderedBy(n => n.Id).UniqueKey(n => n.Id);
            Attach(t, entitlements, "notes");
        });

        builder.AddTable("invites", TenancyFixture.Invites, t =>
        {
            t.OrderedBy(i => i.Id).UniqueKey(i => i.Id);
            Attach(t, entitlements, "invites");
        });

        builder.AddTable("threads", TenancyFixture.Threads, t =>
        {
            t.OrderedBy(x => x.Id).UniqueKey(x => x.Id).ForeignKey(x => x.OrgId)
                .References<TenancyFixture.Org>(o => o.Id, verify: true);
            Attach(t, entitlements, "threads");
        });

        builder.AddTable("messages", TenancyFixture.Messages, t =>
        {
            t.OrderedBy(m => m.Id).UniqueKey(m => m.Id).ForeignKey(m => m.ThreadId)
                .References<TenancyFixture.Thread>(x => x.Id, verify: true);
            Attach(t, entitlements, "messages");
        });

        builder.AddTable("attachments", TenancyFixture.Attachments, t =>
        {
            t.OrderedBy(a => a.Id).UniqueKey(a => a.Id).ForeignKey(a => a.MessageId)
                .References<TenancyFixture.Message>(m => m.Id, verify: true);
            Attach(t, entitlements, "attachments");
        });

        if (!split)
        {
            builder.AddTable("vendors", TenancyFixture.Vendors, t =>
            {
                t.OrderedBy(v => v.Id).UniqueKey(v => v.Id);
                Attach(t, entitlements, "vendors");
            });

            builder.AddTable("items", TenancyFixture.Items, t =>
            {
                t.OrderedBy(i => i.Id).UniqueKey(i => i.Id).ForeignKey(i => i.VendorId)
                    .References<TenancyFixture.Vendor>(v => v.Id, verify: true);
                Attach(t, entitlements, "items");
            });
        }

        builder.AddTable("order_items", TenancyFixture.OrderItems, t =>
        {
            t.OrderedBy(i => i.Id).UniqueKey(i => i.Id).ForeignKey(i => i.OrderId)
                .References<TenancyFixture.Order>(o => o.Id, verify: true);
            if (!split)
            {
                t.ForeignKey(i => i.ItemId)
                    .References<TenancyFixture.Item>(x => x.Id, verify: true);
            }

            Attach(t, entitlements, "order_items");
        });

        builder.AddTable("symbols", TenancyFixture.Symbols, t =>
            t.OrderedBy(s => s.Name).UniqueKey(s => s.Name));

        return builder.Build();
    }

    private static void Attach<T>(PocoTableBuilder<T> table, TenancyEntitlements? entitlements, string name)
        where T : class =>
        Attach(table, entitlements, SourceName, name);

    private static void Attach<T>(
        PocoTableBuilder<T> table, TenancyEntitlements? entitlements, string source, string name)
        where T : class
    {
        var descriptor = entitlements?.For(source, name);
        if (descriptor is not null)
        {
            table.Entitlement(descriptor);
        }
    }

    // ---------------------------------------------------------------- the principals

    /// <summary>§8's principals as grants, in the order <see cref="TenancyFixture.Principals"/> has them.</summary>
    public static IReadOnlyList<(string Name, TenancyPrincipal Principal)> Principals { get; } =
        PrincipalsOf(Handles);

    /// <summary>
    /// The same fifteen principals, in the same order, as the handles of <paramref name="handles"/>
    /// — a grant carries the kind and the role it was declared as, and a handle of another policy is
    /// refused by identity rather than matched by spelling (D270 §1). A second layout of the same
    /// model is a second policy, so it declares its principals from its own handles and the two
    /// lists are one list of code.
    /// </summary>
    public static IReadOnlyList<(string Name, TenancyPrincipal Principal)> PrincipalsOf(
        TenancyPolicy handles)
    {
        ArgumentNullException.ThrowIfNull(handles);
        var org = handles.Tenancy("org");
        var region = handles.Tenancy("region");
        var vendor = handles.Tenancy("vendor");
        var member = handles.Subject("member", within: [org, region]);
        var manager = handles.Role("manager");
        var agent = handles.Role("agent");
        var auditor = handles.Role("auditor");
        var self = handles.Role("self");
        var support = handles.Role("support");
        var counter = handles.Role("counter");
        var vendorRole = handles.Role("vendor");

        return
        [
            ("u1", Principal(1, Grant.ForTenancy(org, 1, manager), Grant.ForTenancy(org, 2, agent))),
            ("u2", Principal(2, Grant.ForTenancy(org, 1, agent))),
            ("u3", Principal(3, Grant.ForSubject(member, 3, self, within: 2))),
            ("u3-in-o1", Principal(3, Grant.ForSubject(member, 3, self, within: 1))),
            ("u4", Principal(4, Grant.Global(manager))),
            ("u5", Principal(5)),
            ("u6", Principal(6, Grant.ForTenancy(org, 1, auditor))),
            ("u6-two-orgs", Principal(
                6, Grant.ForTenancy(org, 1, auditor), Grant.ForTenancy(org, 2, auditor))),
            ("u7", HostList(7, 1)),
            ("u8", Principal(8, Grant.ForTenancy(org, 1, support))),
            ("u9", Principal(
                9, Grant.ForTenancy(org, 1, counter), Grant.ForTenancy(org, 2, counter))),
            // D266's two: the conjunction is inside the one grant, so neither of these is the union
            // of an organisation grant and a region grant — which is what OR-ing two grants would
            // give.
            ("u10-in-a-region", Principal(
                10, Grant.ForTenancy(org, 1, auditor).Within(region, 2))),
            ("u11-subject-in-a-region", Principal(
                11, Grant.ForSubject(member, 1, self, within: 1).Within(region, 1))),
            // D265 clause (h): a vendor grant alone, and the same grant beside an organisation's, so
            // that both perspectives meet on one order.
            ("u14-a-vendor", Principal(14, Grant.ForTenancy(vendor, 1, vendorRole))),
            ("u15-a-vendor-and-an-org", Principal(
                15,
                Grant.ForTenancy(vendor, 1, vendorRole),
                Grant.ForTenancy(org, 1, manager))),
        ];
    }

    /// <summary>One principal: their identifier, their grants and the key their masks are keyed by.</summary>
    /// <remarks>
    /// Every principal binds <c>allowed_orgs</c>, empty unless they hold one: it is the list the
    /// predicate-restricted table's own predicate reads, and a name a host wrote into a predicate is
    /// a name the host binds for every request, bound to nothing where it grants nothing.
    /// </remarks>
    public static TenancyPrincipal Principal(int user, params Grant[] grants) => new()
    {
        User = user,
        Grants = grants,
        MaskKey = "k",
        CustomLists = HostLists(),
    };

    private static Dictionary<string, ContextRelation> HostLists(params int[] allowedOrgs) =>
        new(StringComparer.Ordinal)
        {
            ["allowed_orgs"] = new ContextRelation
            {
                Columns = ["id"],
                Rows = allowedOrgs.Select(i => (IReadOnlyList<object?>)[i]).ToArray(),
                ColumnTypes = [ChalkType.Int32()],
            },
        };

    /// <summary>
    /// A principal who holds no grant and carries a list the host computed itself, for the
    /// predicate-restricted table whose predicate reads it (§8's 15b).
    /// </summary>
    public static TenancyPrincipal HostList(int user, params int[] allowedOrgs) => new()
    {
        User = user,
        MaskKey = "k",
        CustomLists = HostLists(allowedOrgs),
    };

    /// <summary>The bound context for one of <see cref="Principals"/>, by name.</summary>
    public RequestContext Context(string name)
    {
        foreach (var (declared, principal) in Principals)
        {
            if (string.Equals(declared, name, StringComparison.Ordinal))
            {
                return Entitlements.Bind(principal);
            }
        }

        throw new ArgumentOutOfRangeException(nameof(name), name, "no such principal");
    }
}

using Chalk.Catalog;
using Chalk.Sources.Poco;

namespace Chalk.Entitlements.Tenancy.Tests;

/// <summary>
/// <c>Restriction.Through</c>: what the compiler resolves off the declared foreign key, what it
/// refuses, and what the child's rules come out as
/// (<c>docs/design/16-entitlements.md</c> §3.13, D227, D228).
/// </summary>
/// <remarks>
/// The schema is this file's own — a thread that carries the tenancy and a message that does not —
/// because it is the smallest shape the relationship has and the corpus fixture's is the same one
/// with rows in it.
/// </remarks>
public sealed class ThroughTests
{
    private sealed record Org(int Id, string Name);

    private sealed record Thread(int Id, int OrgId, int MemberId);

    private sealed record Message(int Id, int ThreadId, string Content, DateTime? FirstViewedAt);

    /// <summary>A message whose thread may be absent: the nullable key that keeps the join.</summary>
    private sealed record Draft(int Id, int? ThreadId, string Content);

    private static SchemaDescriptor Schema { get; } = Build();

    /// <summary>
    /// The catalog the policies below are declared over. A policy is declared against a catalog and
    /// a table is obtained from one of its sources, because two sources may hold a table of one name
    /// (<c>docs/design/45-typed-tenancy-surface.md</c> §1, D270); this file has the one source.
    /// </summary>
    private static CatalogContext Catalog { get; } = new()
    {
        ContextId = "through",
        Epoch = 1,
        Schemas = [Schema],
    };

    private static SchemaDescriptor Build()
    {
        var builder = new PocoSourceBuilder("mem").NamingPolicy(PocoNamingPolicy.SnakeCase);
        builder.AddTable("orgs", Array.Empty<Org>(), t => t.OrderedBy(o => o.Id).UniqueKey(o => o.Id));
        builder.AddTable("threads", Array.Empty<Thread>(), t =>
            t.OrderedBy(x => x.Id).UniqueKey(x => x.Id).ForeignKey(x => x.OrgId)
                .References<Org>(o => o.Id, verify: true));
        builder.AddTable("messages", Array.Empty<Message>(), t =>
            t.OrderedBy(m => m.Id).UniqueKey(m => m.Id).ForeignKey(m => m.ThreadId)
                .References<Thread>(x => x.Id, verify: true));
        builder.AddTable("drafts", Array.Empty<Draft>(), t =>
            t.OrderedBy(d => d.Id).UniqueKey(d => d.Id).ForeignKey(d => d.ThreadId)
                .References<Thread>(x => x.Id, verify: true));
        return builder.Build().DescribeSchema();
    }

    /// <summary>
    /// One policy and the handles its declarations are written with. Every name enters once, here,
    /// and every later mention is the handle it came back as (§1, D270) — a table obtained from the
    /// source it belongs to, and a column from the table that carries it.
    /// </summary>
    private sealed class Declaration
    {
        private readonly Source _source;

        internal Declaration()
        {
            Policy = TenancyPolicy.Declare(Catalog);
            _source = Policy.Source(Schema.Name);
            Manager = Policy.Role("manager");
            Agent = Policy.Role("agent");
            Org = Policy.Tenancy("org");
            Pii = Policy.Realm("pii");
            Threads = _source.Table("threads");
            Messages = _source.Table("messages");
        }

        internal TenancyPolicy Policy { get; }

        internal Role Manager { get; }

        internal Role Agent { get; }

        internal Kind Org { get; }

        internal Realm Pii { get; }

        internal Table Threads { get; }

        internal Table Messages { get; }

        internal TenancyEntitlements Compile() => Policy.Compile([Schema]);
    }

    /// <summary>
    /// The thread that carries the tenancy, and the message whose own restriction the caller writes
    /// — the default being §3.13's, that a message is visible when its thread is.
    /// </summary>
    private static Declaration Policy(Action<Declaration>? messages = null)
    {
        var declaration = new Declaration();
        declaration.Threads
            .Tenancy(r => r.Direct(declaration.Org, declaration.Threads.Column("org_id")))
            .Realm(declaration.Pii, declaration.Threads.Column("member_id"));
        (messages ?? DefaultMessages)(declaration);
        return declaration;

        static void DefaultMessages(Declaration d) =>
            d.Messages.Tenancy(r => r.Through(d.Messages.Column("thread_id")));
    }

    private static CatalogValidationException Refused(Declaration declaration) =>
        Assert.Throws<CatalogValidationException>(() => declaration.Compile());

    // ------------------------------------------------------------------ what it compiles to

    [Fact]
    public void The_parent_and_its_key_come_off_the_declared_foreign_key()
    {
        var declaration = Policy();
        var descriptor = declaration.Compile().For(declaration.Messages)!;

        var parent = Assert.Single(descriptor.Through);
        Assert.Equal(1, parent.Column);
        Assert.Equal("threads", parent.ParentTable);
        Assert.Equal(0, parent.ParentColumn);
    }

    /// <summary>
    /// The relationship is on the descriptor and not in the text: writing it as a predicate would be
    /// writing a sub-query, which §2's vocabulary does not admit and D225 answers with
    /// <c>through</c>.
    /// </summary>
    [Fact]
    public void A_through_writes_no_row_predicate()
    {
        var declaration = Policy();

        Assert.Equal("", declaration.Compile().For(declaration.Messages)!.RowPredicate);
    }

    /// <summary>
    /// The child's role conditions are the parent's dimensions, written over
    /// <c>&lt;parent_table&gt;.&lt;column&gt;</c> so the planner can evaluate them on the parent's
    /// side of the join, once per parent row (D228).
    /// </summary>
    [Fact]
    public void The_childs_role_conditions_are_written_over_the_parents_columns()
    {
        var declaration = Policy(d => d.Messages
            .Tenancy(r => r.Through(d.Messages.Column("thread_id")))
            .Realm(d.Pii, d.Messages.Column("content"))
            .Access(new AccessRule { Roles = [d.Manager], Realm = d.Pii, Grants = Verdict.Full })
            .Access(new AccessRule { Roles = [d.Agent], Realm = d.Pii, Grants = Verdict.Mask }));

        var descriptor = declaration.Compile().For(declaration.Messages)!;

        var content = Assert.Single(descriptor.Columns);
        Assert.Equal(2, content.Column);
        Assert.Equal(
            "(threads.org_id IN (@ctx.org_manager) OR @ctx.global_manager)",
            content.Rules[0].When);
        Assert.Equal(
            "(threads.org_id IN (@ctx.org_agent) OR @ctx.global_agent)",
            content.Rules[1].When);
    }

    /// <summary>An explicit parent, for a source that declares no foreign key.</summary>
    [Fact]
    public void An_explicit_parent_is_taken_as_declared()
    {
        var declaration = Policy(d => d.Messages.Tenancy(r =>
            r.Through(d.Messages.Column("thread_id"), d.Threads, d.Threads.Column("id"))));

        var descriptor = declaration.Compile().For(declaration.Messages)!;

        var parent = Assert.Single(descriptor.Through);
        Assert.Equal("threads", parent.ParentTable);
        Assert.Equal(0, parent.ParentColumn);
    }

    // ------------------------------------------------------------------ what it refuses

    [Fact]
    public void A_column_the_table_does_not_hold_is_refused()
    {
        var error = Refused(Policy(d =>
            d.Messages.Tenancy(r => r.Through(d.Messages.Column("chat_id")))));

        Assert.Contains("'chat_id', which is not a column", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A column of the <em>other</em> table, which a string could not have been checked for at all:
    /// the handle carries the table it was obtained from, so the declaration is refused where it was
    /// written rather than looked up on the wrong table and silently found (§1, D270).
    /// </summary>
    [Fact]
    public void A_column_handle_of_another_table_is_refused()
    {
        var declaration = new Declaration();

        var error = Assert.Throws<CatalogValidationException>(() =>
            declaration.Messages.Tenancy(r => r.Through(declaration.Threads.Column("org_id"))));

        Assert.Contains(
            "the column handle 'threads.org_id' is used on 'messages'",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_column_with_no_foreign_key_and_no_explicit_parent_is_refused()
    {
        var error = Refused(Policy(d =>
            d.Messages.Tenancy(r => r.Through(d.Messages.Column("content")))));

        Assert.Contains(
            "has no declared foreign key over that column", error.Message, StringComparison.Ordinal);
        Assert.Contains(
            "Through(messages.Column(\"content\"), parent, parent.Column(\"<key>\"))",
            error.Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A rule speaks for roles, and a role this policy does not declare is a role no grant can ever
    /// name, so the rule could never match. Since D270 the only way to write it is with a role
    /// <em>another</em> policy declared, which is the shape a host running two policies has.
    /// </summary>
    [Fact]
    public void An_access_rule_naming_an_unlisted_role_is_refused()
    {
        var declaration = Policy(d => d.Messages
            .Tenancy(r => r.Through(d.Messages.Column("thread_id")))
            .Realm(d.Pii, d.Messages.Column("content"))
            .Access(new AccessRule { Roles = [d.Manager], Realm = d.Pii, Grants = Verdict.Full })
            .Access(new AccessRule
            {
                Roles = [Elsewhere.Role("auditor")],
                Realm = d.Pii,
                Grants = Verdict.Mask,
            }));

        var error = Refused(declaration);

        Assert.Contains("tenancy.tables[messages].access[1]", error.Message, StringComparison.Ordinal);
        Assert.Contains("realm 'pii'", error.Message, StringComparison.Ordinal);
        Assert.Contains("speaks for role 'auditor'", error.Message, StringComparison.Ordinal);
        Assert.Contains("not one of the roles this policy declares", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A policy of its own, for the names no policy under test declares: a role exists only where
    /// some policy declared one (§1, D270).
    /// </summary>
    private static TenancyPolicy Elsewhere { get; } = TenancyPolicy.Declare(Catalog);

    [Fact]
    public void A_parent_the_policy_does_not_declare_is_refused()
    {
        var policy = TenancyPolicy.Declare(Catalog);
        policy.Role("manager");
        var messages = policy.Source(Schema.Name).Table("messages");
        messages.Tenancy(r => r.Through(messages.Column("thread_id")));

        var error = Assert.Throws<CatalogValidationException>(() => policy.Compile([Schema]));

        Assert.Contains("which this policy does not declare", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_parent_the_policy_leaves_unrestricted_is_refused()
    {
        var policy = TenancyPolicy.Declare(Catalog);
        policy.Role("manager");
        var source = policy.Source(Schema.Name);
        source.Table("threads").Unrestricted();
        var messages = source.Table("messages");
        messages.Tenancy(r => r.Through(messages.Column("thread_id")));

        var error = Assert.Throws<CatalogValidationException>(() => policy.Compile([Schema]));

        Assert.Contains(
            "Through an unrestricted parent restricts nothing", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_parent_key_that_is_not_a_declared_unique_key_is_refused()
    {
        var error = Refused(Policy(d => d.Messages.Tenancy(r =>
            r.Through(d.Messages.Column("thread_id"), d.Threads, d.Threads.Column("org_id")))));

        Assert.Contains(
            "is not a declared unique key of the parent", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cycle_is_refused_and_a_diamond_is_not()
    {
        var cyclic = Policy();
        cyclic.Threads.Tenancy(r => r.Through(
            cyclic.Threads.Column("id"), cyclic.Messages, cyclic.Messages.Column("id")));

        var error = Assert.Throws<CatalogValidationException>(() => cyclic.Compile());

        Assert.Contains("the chain of derived visibility returns to", error.Message, StringComparison.Ordinal);

        // Two routes to one parent is a diamond, and a diamond is fine.
        Policy(d => d.Messages.Tenancy(r => r
            .Through(d.Messages.Column("thread_id"))
            .Through(d.Messages.Column("id"), d.Threads, d.Threads.Column("id"))))
            .Compile();
    }
}

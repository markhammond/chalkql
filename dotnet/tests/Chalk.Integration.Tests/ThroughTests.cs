using Chalk.Catalog;
using Chalk.Client;
using Chalk.Entitlements;
using Chalk.Entitlements.Tenancy;
using Chalk.Sources.Poco;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// The shapes of <c>Through</c> the corpus cannot record as rows
/// (<c>docs/design/16-entitlements.md</c> §3.13, D225–D230): two parents OR-ed, one parent through
/// two columns, a nullable correlation key, and execute-time binding over a child.
/// </summary>
/// <remarks>
/// <para>
/// The corpus family <c>m7-tenancy</c> covers the ordinary case — a manager, an agent, a participant
/// by subject grant, a global grant whose join is elided, and a principal with no grant at all —
/// against the oracle and a recorded golden, per principal. What is here is the rest of the algebra,
/// where the claim is about the <em>plan</em> and the report rather than about which rows came back:
/// any path grants, one parent through two columns is two joins, and a key that may be NULL keeps
/// the join and reports SOME however wide the principal's grant is.
/// </para>
/// <para>
/// The tables are this file's own and the policy is the package's, so the same run exercises
/// <c>Tenancy(t =&gt; t.Through(column))</c>, the descriptor it compiles to, the pass, the report and
/// <c>Reconcile</c> — which is where a disagreement between the package's prediction and the
/// planner's fold would show.
/// </para>
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class ThroughTests(SharedSidecar sidecar)
{
    private sealed record Org(int Id, string Name);

    private sealed record Thread(int Id, int OrgId, int MemberId);

    private sealed record Message(int Id, int ThreadId, string Content);

    /// <summary>Two parents: a link is visible through its message <em>or</em> through its thread.</summary>
    private sealed record Link(int Id, int MessageId, int ThreadId, string Label);

    /// <summary>One parent through two columns: two joins to two occurrences of one set of rules.</summary>
    private sealed record Pairing(int Id, int LeftThread, int RightThread, string Label);

    /// <summary>A correlation key that may be NULL, which is what keeps the join (D226).</summary>
    private sealed record Draft(int Id, int? ThreadId, string Content);

    private static readonly IReadOnlyList<Org> Orgs =
        [new(1, "Northwind"), new(2, "Southerly"), new(3, "Eastward")];

    private static readonly IReadOnlyList<Thread> Threads =
        [new(1, 1, 1), new(2, 2, 3), new(3, 3, 5)];

    private static readonly IReadOnlyList<Message> Messages =
        [new(1, 1, "northwind"), new(2, 2, "southerly"), new(3, 3, "eastward")];

    /// <summary>
    /// Link 1 is reachable both ways, link 2 only through its thread, link 3 neither way — which is
    /// what makes "any path grants" an assertion rather than a coincidence.
    /// </summary>
    private static readonly IReadOnlyList<Link> Links =
        [new(1, 1, 1, "both"), new(2, 3, 1, "by thread"), new(3, 3, 3, "neither")];

    private static readonly IReadOnlyList<Pairing> Pairings =
        [new(1, 1, 2, "left"), new(2, 2, 1, "right"), new(3, 2, 3, "neither")];

    private static readonly IReadOnlyList<Draft> Drafts =
        [new(1, 1, "kept"), new(2, null, "orphan"), new(3, 3, "elsewhere")];

    private const string ContextId = "through";

    /// <summary>
    /// The handles the grants below are held in: the very policy the descriptors were compiled
    /// from, so a kind and a role enter once, at their declaration, and a grant names what it got
    /// back (D270 §1).
    /// </summary>
    private static TenancyPolicy Handles => Compiled.Value.Entitlements.Policy;

    private static Kind OrgKind => Handles.Tenancy("org");

    private static Role ManagerRole => Handles.Role("manager");

    /// <summary>A manager in Northwind and nothing else.</summary>
    private static TenancyPrincipal Manager => new()
    {
        User = 1,
        MaskKey = "k",
        Grants = [Grant.ForTenancy(OrgKind, 1, ManagerRole)],
    };

    /// <summary>A grant that reaches every organization, for whom a total join is elided.</summary>
    private static TenancyPrincipal Everywhere => new()
    {
        User = 9,
        MaskKey = "k",
        Grants = [Grant.Global(ManagerRole)],
    };

    private static TenancyPrincipal Nobody => new() { User = 5, MaskKey = "k" };

    // ------------------------------------------------------------------ the catalog

    /// <summary>The schema this file's tables live in, which names their source (D270).</summary>
    private const string SourceName = "main";

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
        policy.AllowGlobalGrants();

        var org = policy.Tenancy("org");
        var member = policy.Subject("member", within: [org]);
        var pii = policy.Realm("pii");

        var source = policy.Source(SourceName);
        var threads = source.Table("threads");
        var messages = source.Table("messages");
        var links = source.Table("links");
        var pairings = source.Table("pairings");
        var drafts = source.Table("drafts");

        threads.Tenancy(t => t
            .Direct(org, threads.Column("org_id"))
            .Direct(member, threads.Column("member_id")));

        messages
            .Tenancy(t => t.Through(messages.Column("thread_id")))
            .Realm(pii, messages.Column("content"))
            .Access(new AccessRule { Roles = [manager], Realm = pii, Grants = Verdict.Full })
            .Access(new AccessRule
            {
                Roles = [agent],
                Realm = pii,
                Grants = Verdict.Mask,
                Mask = Sql.Of("lambda v: SUBSTRING(v, 1, 5)"),
            });

        // Any path grants: a link reached through either parent is visible (D226).
        links.Tenancy(t => t
            .Through(links.Column("message_id"))
            .Through(links.Column("thread_id")));

        // The same parent through two columns, which is two joins to two occurrences.
        pairings.Tenancy(t => t
            .Through(pairings.Column("left_thread"))
            .Through(pairings.Column("right_thread")));

        drafts.Tenancy(t => t.Through(drafts.Column("thread_id")));
        return policy;
    }

    private static PocoSource Tables(TenancyEntitlements? entitlements)
    {
        var builder = new PocoSourceBuilder("mem").NamingPolicy(PocoNamingPolicy.SnakeCase);
        builder.AddTable("orgs", Orgs, t => t.OrderedBy(o => o.Id).UniqueKey(o => o.Id));
        builder.AddTable("threads", Threads, t =>
        {
            t.OrderedBy(x => x.Id).UniqueKey(x => x.Id).ForeignKey(x => x.OrgId)
                .References<Org>(o => o.Id, verify: true);
            Attach(t, entitlements, "threads");
        });
        builder.AddTable("messages", Messages, t =>
        {
            t.OrderedBy(m => m.Id).UniqueKey(m => m.Id).ForeignKey(m => m.ThreadId)
                .References<Thread>(x => x.Id, verify: true);
            Attach(t, entitlements, "messages");
        });
        builder.AddTable("links", Links, t =>
        {
            t.OrderedBy(l => l.Id).UniqueKey(l => l.Id);
            t.ForeignKey(l => l.MessageId).References<Message>(m => m.Id, verify: true);
            t.ForeignKey(l => l.ThreadId).References<Thread>(x => x.Id, verify: true);
            Attach(t, entitlements, "links");
        });
        builder.AddTable("pairings", Pairings, t =>
        {
            t.OrderedBy(p => p.Id).UniqueKey(p => p.Id);
            t.ForeignKey(p => p.LeftThread).References<Thread>(x => x.Id, verify: true);
            t.ForeignKey(p => p.RightThread).References<Thread>(x => x.Id, verify: true);
            Attach(t, entitlements, "pairings");
        });
        builder.AddTable("drafts", Drafts, t =>
        {
            t.OrderedBy(d => d.Id).UniqueKey(d => d.Id).ForeignKey(d => d.ThreadId)
                .References<Thread>(x => x.Id, verify: true);
            Attach(t, entitlements, "drafts");
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

    private async Task<(EntitledQuery Query, string[] Rows)> RunAsync(
        ChalkEngine engine, string sql, TenancyPrincipal principal)
    {
        var query = await engine.WithEntitlements().PrepareAsync(sql, Bind(principal));
        return (query, await RowsAsync(engine, query.Query, null));
    }

    private static async Task<string[]> RowsAsync(
        ChalkEngine engine, PreparedQuery prepared, RequestContext? executeWith)
    {
        var rows = new List<string>();
        await using var execution = executeWith is null
            ? await engine.ExecuteAsync(prepared, (IReadOnlyList<object?>?)null)
            : await engine.ExecuteAsync(prepared, executeWith);
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

    // ------------------------------------------------------------------ two parents, OR-ed

    /// <summary>
    /// Any path grants (D226): link 1 is reachable through its message and through its thread, link
    /// 2 only through its thread, link 3 through neither — and the manager sees the first two.
    /// </summary>
    [Fact]
    public async Task Two_parents_are_or_ed_and_either_one_grants()
    {
        await using var engine = await EngineAsync();
        var (query, rows) = await RunAsync(engine, "SELECT id, label FROM links ORDER BY id", Manager);

        Assert.Equal(["1|both", "2|by thread"], rows);

        var links = query.Entitlements.Tables.Single(t => t.Table == "links");
        Assert.Equal(TableVisibility.Some, links.Visibility);

        // And the package predicts the same, from the parents alone.
        var reconciled = Compiled.Value.Entitlements.Reconcile(query, Manager);
        var predicted = reconciled.Visibility.Single(v => v.Table == "links");
        Assert.Equal(ReconciledVisibility.Agrees, predicted.Verdict);
        Assert.True(reconciled.Agrees);
    }

    /// <summary>
    /// A principal who holds no grant at all: every parent's own predicate folds to FALSE, so the
    /// derived visibility is NONE and the package says so too.
    /// </summary>
    [Fact]
    public async Task Two_parents_that_both_grant_nothing_derive_none()
    {
        await using var engine = await EngineAsync();
        var (query, rows) = await RunAsync(engine, "SELECT id, label FROM links ORDER BY id", Nobody);

        Assert.Empty(rows);
        Assert.Equal(
            TableVisibility.None,
            query.Entitlements.Tables.Single(t => t.Table == "links").Visibility);

        var reconciled = Compiled.Value.Entitlements.Reconcile(query, Nobody);
        Assert.Equal(
            ReconciledVisibility.Agrees,
            reconciled.Visibility.Single(v => v.Table == "links").Verdict);
    }

    // ------------------------------------------------------------------ one parent, two columns

    /// <summary>
    /// The same parent through two columns is two joins to two occurrences (D226): a pairing is
    /// visible when <em>either</em> of the threads it names is.
    /// </summary>
    [Fact]
    public async Task One_parent_through_two_columns_is_two_joins()
    {
        await using var engine = await EngineAsync();
        var (query, rows) =
            await RunAsync(engine, "SELECT id, label FROM pairings ORDER BY id", Manager);

        Assert.Equal(["1|left", "2|right"], rows);
        Assert.Equal(
            TableVisibility.Some,
            query.Entitlements.Tables.Single(t => t.Table == "pairings").Visibility);

        // Two occurrences of one parent, so the explanation names it twice, on its two keys.
        var explained = await engine.WithEntitlements()
            .ExplainAsync("SELECT id FROM pairings", Bind(Manager));
        var parents = explained.Tables.Single(t => t.Table == "pairings").Parents;
        Assert.Equal(["left_thread", "right_thread"], parents.Select(p => p.Column));
        Assert.All(parents, p => Assert.Equal("threads", p.Table));
        Assert.All(parents, p => Assert.Equal("id", p.ParentColumn));
        Assert.All(parents, p => Assert.False(p.Elided));
        Assert.All(parents, p => Assert.Equal("FULL", p.KeyDisclosure));
    }

    // ------------------------------------------------------------------ the nullable key

    /// <summary>
    /// A grant that reaches every organization folds the parent's own predicate to TRUE, so the join
    /// to a NOT NULL key is elided and the visibility is ALL — but a key that may be NULL keeps the
    /// join, because a row with no parent has no visible parent either (D226).
    /// </summary>
    [Fact]
    public async Task A_nullable_key_keeps_the_join_and_reports_some()
    {
        await using var engine = await EngineAsync();

        var (wide, wideRows) =
            await RunAsync(engine, "SELECT id, content FROM drafts ORDER BY id", Everywhere);
        Assert.Equal(["1|kept", "3|elsewhere"], wideRows);
        var drafts = wide.Entitlements.Tables.Single(t => t.Table == "drafts");
        Assert.Equal(TableVisibility.Some, drafts.Visibility);
        // The parent is still read, which is what "the join was kept" comes to in the report.
        Assert.Contains(wide.Entitlements.Tables, t => t.Table == "threads");

        var reconciled = Compiled.Value.Entitlements.Reconcile(wide, Everywhere);
        Assert.Equal(
            ReconciledVisibility.Agrees,
            reconciled.Visibility.Single(v => v.Table == "drafts").Verdict);

        // Beside it, the NOT NULL key of `messages` under the same grant: elided, and ALL.
        var (elided, _) =
            await RunAsync(engine, "SELECT id, content FROM messages ORDER BY id", Everywhere);
        var messages = elided.Entitlements.Tables.Single(t => t.Table == "messages");
        Assert.Equal(TableVisibility.All, messages.Visibility);
        Assert.DoesNotContain(elided.Entitlements.Tables, t => t.Table == "threads");

        var explained = await engine.WithEntitlements()
            .ExplainAsync("SELECT id FROM messages", Bind(Everywhere));
        Assert.True(explained.Tables.Single(t => t.Table == "messages").Parents.Single().Elided);
    }

    // ------------------------------------------------------------------ execute-time binding

    /// <summary>
    /// One plan for every principal of a shape (D209), over a table entitled through a parent: the
    /// memberships the parent's verdicts read are the parent's own bound-list markers, computed once
    /// per parent row beside its predicate.
    /// </summary>
    [Fact]
    public async Task Execute_time_binding_works_through_a_parent()
    {
        await using var engine = await EngineAsync();
        var query = await engine.WithEntitlements()
            .PrepareAsync("SELECT id, content FROM messages ORDER BY id", Bind(Manager).Shape());

        Assert.Equal(["1|northwind"], await RowsAsync(engine, query.Query, Bind(Manager)));
        Assert.Equal(
            ["1|northwind", "2|southerly", "3|eastward"],
            await RowsAsync(engine, query.Query, Bind(Everywhere)));
    }
}

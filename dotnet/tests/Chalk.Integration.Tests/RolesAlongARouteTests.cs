using Chalk.Catalog;
using Chalk.Client;
using Chalk.Entitlements;
using Chalk.Entitlements.Tenancy;
using Chalk.Sources.Poco;
using Chalk.TestKit;
using CatalogContext = Chalk.Catalog.CatalogContext;

namespace Chalk.Integration.Tests;

/// <summary>
/// A column rule for a role held along a route — a declared path, or a <c>Through</c> parent —
/// decides only the rows that route reaches. A row the reader reaches by another of the table's
/// restrictions, a direct dimension or a resource owner, takes the rule that holds there, even where
/// the reader's grants make the role constant along the route: one grant on the reader's own id, as
/// a consent model has, is what makes it so. Each case is read twice, on rows and on the disclosure
/// each row carries.
/// </summary>
[Collection(SidecarCollection.Name)]
public sealed class RolesAlongARouteTests(SharedSidecar sidecar)
{
    private const string ContextId = "roles-along-a-route";

    private sealed record Team(int Id);

    private sealed record Reader(int Id);

    private sealed record Note(long Seq, int TeamId, int AuthorId, string Body);

    private sealed record NoteGrant(int Id, long NoteSeq, int ReaderId);

    private sealed record Doc(long Seq, int TeamId, string Body);

    private sealed record DocGrant(int Id, long DocSeq, int ReaderId);

    private sealed record Share(int ShareKey, int TeamId);

    private sealed record Section(long Seq, int ShareKey, int AttendingId, string Content);

    private static readonly Team[] Teams = [new(1), new(2), new(3)];

    private static readonly Reader[] Readers = [new(7), new(8)];

    private static readonly Note[] Notes =
    [
        new(1, 1, 5, "body-1"),
        new(2, 3, 5, "body-2"),
        new(3, 1, 5, "body-3"),
        new(4, 2, 7, "body-4"),
        new(5, 3, 5, "body-5"),
    ];

    private static readonly NoteGrant[] NoteGrants = [new(1, 2, 7), new(2, 3, 7), new(3, 5, 8)];

    private static readonly Doc[] Docs = [new(1, 1, "doc-1"), new(2, 1, "doc-2"), new(3, 3, "doc-3"), new(4, 3, "doc-4")];

    private static readonly DocGrant[] DocGrants = [new(1, 2, 7), new(2, 3, 7), new(3, 4, 8)];

    private static readonly Share[] Shares = [new(10, 1), new(20, 2)];

    private static readonly Section[] Sections =
    [
        new(1, 10, 5, "content-1"),
        new(2, 20, 7, "content-2"),
        new(3, 20, 5, "content-3"),
    ];

    private static readonly TenancyEntitlements Policy = Compile();

    /// <summary>A member of team 1, and reader 7, who has consented to what a grant row shares with it.</summary>
    private static TenancyPrincipal Seven => new()
    {
        User = 7,
        Grants =
        [
            Grant.ForTenancy(Policy.Policy.Tenancy("team"), 1, Policy.Policy.Role("member")),
            Grant.ForTenancy(Policy.Policy.Tenancy("reader"), 7, Policy.Policy.Role("consented")),
        ],
    };

    /// <summary>
    /// Notes kept by team, by author and along a grant table: a note shared with the reader is read in
    /// full, but one it reaches only as a member of its team keeps the member's mask, and one it
    /// authored is read in full as its owner. The rules mix the note's own columns with the path's, so
    /// the path's verdict is one boolean per rule.
    /// </summary>
    [Fact]
    public async Task A_role_held_along_a_path_decides_only_the_rows_the_path_reaches()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var rows = await ReadAsync("SELECT seq, body FROM desk.notes", Seven);

        Assert.Equal(
            new Dictionary<long, (string?, string)>
            {
                [1] = ("bod", "MASKED"),
                [2] = ("body-2", "FULL"),
                [3] = ("body-3", "FULL"),
                [4] = ("body-4", "FULL"),
            },
            rows);
    }

    /// <summary>
    /// Documents kept by team and along a grant table, with one rule, for the role held along the
    /// path: every rule is decided on the path's side, so its verdict is one ordinal. A document the
    /// reader reaches only by its team is withheld, as no rule grants it there.
    /// </summary>
    [Fact]
    public async Task A_path_role_decided_by_ordinal_holds_only_where_the_path_matched()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var rows = await ReadAsync("SELECT seq, body FROM desk.docs", Seven);

        Assert.Equal(
            new Dictionary<long, (string?, string)>
            {
                [1] = (null, "REDACTED"),
                [2] = ("doc-2", "FULL"),
                [3] = ("doc-3", "FULL"),
            },
            rows);
    }

    /// <summary>
    /// Sections kept through their share and by their attending reader, who sees what the rules give
    /// it. A member of the share's team reads a section in full; a section the reader reaches only as
    /// its attending, under a share it holds no role on, takes the owner's mask, not the member's
    /// verdict.
    /// </summary>
    [Fact]
    public async Task A_role_held_through_a_parent_decides_only_the_rows_the_parent_reaches()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var rows = await ReadAsync("SELECT seq, content FROM desk.sections", Seven);

        Assert.Equal(
            new Dictionary<long, (string?, string)>
            {
                [1] = ("content-1", "FULL"),
                [2] = ("co", "MASKED"),
            },
            rows);
    }

    /// <summary>
    /// The same statements prepared for the context's shape, bound at execution: the verdict is decided
    /// by the same route, and the rows and disclosures are the same.
    /// </summary>
    [Theory]
    [InlineData("SELECT seq, body FROM desk.notes")]
    [InlineData("SELECT seq, body FROM desk.docs")]
    [InlineData("SELECT seq, content FROM desk.sections")]
    public async Task A_statement_bound_at_execution_reads_the_same(string sql)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        Assert.Equal(await ReadAsync(sql, Seven), await ReadAsync(sql, Seven, shaped: true));
    }

    /// <summary>Each row's value and disclosure, by its key in the first column.</summary>
    private async Task<Dictionary<long, (string?, string)>> ReadAsync(string sql, TenancyPrincipal who, bool shaped = false)
    {
        await using var engine = await ChalkEngine.CreateAsync(
            new ChalkEngineOptions { ContextId = ContextId, Sources = [Desk(Policy)], Planner = sidecar.CreatePlanner() },
            TestContext.Current.CancellationToken);
        var entitled = engine.WithEntitlements(new EntitlementsOptions { IncludeDisclosureColumns = true });
        var context = Policy.Bind(who);
        var statement = await entitled.PrepareAsync(
            sql, shaped ? context.Shape() : context, ct: TestContext.Current.CancellationToken);
        await using var execution = shaped
            ? await engine.ExecuteAsync(statement, context, ct: TestContext.Current.CancellationToken)
            : await engine.ExecuteAsync(statement, ct: TestContext.Current.CancellationToken);
        var rows = new Dictionary<long, (string?, string)>();
        await foreach (var batch in execution.Batches)
        {
            using (batch)
            {
                // Each column is followed by its disclosure: the key, then the value read and its label.
                var key = batch.Schema.GetFieldIndex("seq");
                var value = batch.Schema.GetFieldIndex(sql.Split(' ')[2]);
                var label = batch.Schema.GetFieldIndex(sql.Split(' ')[2] + "__disclosure");
                for (var i = 0; i < batch.Length; i++)
                {
                    rows[Convert.ToInt64(Chalk.Arrow.RecordBatchExtensions.ValueAt(batch.Column(key), i))] = (
                        (string?)Chalk.Arrow.RecordBatchExtensions.ValueAt(batch.Column(value), i),
                        (string)Chalk.Arrow.RecordBatchExtensions.ValueAt(batch.Column(label), i)!);
                }
            }
        }

        return rows;
    }

    private static TenancyEntitlements Compile()
    {
        var bare = new CatalogContext { ContextId = ContextId, Epoch = 1, Schemas = [Desk(null).DescribeSchema()] };
        var policy = TenancyPolicy.Declare(bare);
        var desk = policy.Source("desk");
        var team = policy.Tenancy("team");
        var reader = policy.Tenancy("reader");
        var member = policy.Role("member");
        var consented = policy.Role("consented");
        var pii = policy.Realm("pii");
        var readers = desk.Table("readers");
        var notes = desk.Table("notes");
        var noteGrants = desk.Table("note_grants");
        var docs = desk.Table("docs");
        var docGrants = desk.Table("doc_grants");
        var shares = desk.Table("shares");
        var sections = desk.Table("sections");

        readers.Tenancy(t => t.Direct(reader, readers.Column("id")));
        noteGrants.Tenancy(t => t.Direct(reader, noteGrants.Column("reader_id")));
        docGrants.Tenancy(t => t.Direct(reader, docGrants.Column("reader_id")));

        notes
            .Tenancy(t => t
                .Direct(team, notes.Column("team_id"))
                .ResourceOwner(notes.Column("author_id"))
                .Related(reader).Through(noteGrants).Through(readers))
            .Realm(pii, notes.Column("body"))
            .Access(new AccessRule { Roles = [Roles.Owner], Grants = Verdict.Full })
            .Access(new AccessRule { Roles = [consented], Realm = pii, Grants = Verdict.Full })
            .Access(new AccessRule
            {
                Roles = [member], Realm = pii, Grants = Verdict.Mask, Mask = Sql.Of("lambda v: SUBSTRING(v, 1, 3)"),
            });

        docs
            .Tenancy(t => t
                .Direct(team, docs.Column("team_id"))
                .Related(reader).Through(docGrants).Through(readers))
            .Realm(pii, docs.Column("body"))
            .Access(new AccessRule { Roles = [consented], Realm = pii, Grants = Verdict.Full });

        shares.Tenancy(t => t.Direct(team, shares.Column("team_id")));
        sections
            .Tenancy(t => t
                .ResourceOwner(sections.Column("attending_id"), ResourceOwnerSees.ByRules)
                .Through(sections.Column("share_key")))
            .Realm(pii, sections.Column("content"))
            .Access(new AccessRule { Roles = [member], Realm = pii, Grants = Verdict.Full })
            .Access(new AccessRule
            {
                Roles = [Roles.Owner], Realm = pii, Grants = Verdict.Mask, Mask = Sql.Of("lambda v: SUBSTRING(v, 1, 2)"),
            });
        return policy.Compile(bare);
    }

    private static PocoSource Desk(TenancyEntitlements? entitlements)
    {
        var builder = new PocoSourceBuilder("mem", "desk").NamingPolicy(PocoNamingPolicy.SnakeCase);
        builder.AddTable("teams", Teams, t => t.OrderedBy(x => x.Id).UniqueKey(x => x.Id));
        builder.AddTable("readers", Readers, t =>
        {
            t.OrderedBy(x => x.Id).UniqueKey(x => x.Id);
            Entitle(t, entitlements, "readers");
        });
        builder.AddTable("notes", Notes, t =>
        {
            t.OrderedBy(x => x.Seq).UniqueKey(x => x.Seq);
            t.ForeignKey(x => x.TeamId).References<Team>(x => x.Id, verify: true);
            Entitle(t, entitlements, "notes");
        });
        builder.AddTable("note_grants", NoteGrants, t =>
        {
            t.OrderedBy(x => x.Id).UniqueKey(x => x.Id);
            t.ForeignKey(x => x.NoteSeq).References<Note>(x => x.Seq, verify: true);
            t.ForeignKey(x => x.ReaderId).References<Reader>(x => x.Id, verify: true);
            Entitle(t, entitlements, "note_grants");
        });
        builder.AddTable("docs", Docs, t =>
        {
            t.OrderedBy(x => x.Seq).UniqueKey(x => x.Seq);
            t.ForeignKey(x => x.TeamId).References<Team>(x => x.Id, verify: true);
            Entitle(t, entitlements, "docs");
        });
        builder.AddTable("doc_grants", DocGrants, t =>
        {
            t.OrderedBy(x => x.Id).UniqueKey(x => x.Id);
            t.ForeignKey(x => x.DocSeq).References<Doc>(x => x.Seq, verify: true);
            t.ForeignKey(x => x.ReaderId).References<Reader>(x => x.Id, verify: true);
            Entitle(t, entitlements, "doc_grants");
        });
        builder.AddTable("shares", Shares, t =>
        {
            t.OrderedBy(x => x.ShareKey).UniqueKey(x => x.ShareKey);
            t.ForeignKey(x => x.TeamId).References<Team>(x => x.Id, verify: true);
            Entitle(t, entitlements, "shares");
        });
        builder.AddTable("sections", Sections, t =>
        {
            t.OrderedBy(x => x.Seq).UniqueKey(x => x.Seq);
            t.ForeignKey(x => x.ShareKey).References<Share>(x => x.ShareKey, verify: true);
            Entitle(t, entitlements, "sections");
        });
        return builder.Build();
    }

    private static void Entitle<T>(PocoTableBuilder<T> table, TenancyEntitlements? entitlements, string name)
    {
        if (entitlements?.For("desk", name) is { } descriptor)
        {
            table.Entitlement(descriptor);
        }
    }
}

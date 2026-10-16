using System.Data.Common;
using Chalk.Catalog;
using Chalk.Client;
using Chalk.Entitlements;
using Chalk.Entitlements.Tenancy;
using Chalk.Execution.Maintenance;
using Chalk.Incremental;
using Chalk.Ir;
using Chalk.Sources.Ado;
using Chalk.Sources.Poco;
using Chalk.TestKit;
using Microsoft.Data.Sqlite;
using Aggregate = Chalk.Entitlements.Tenancy.Aggregate;
using CatalogContext = Chalk.Catalog.CatalogContext;
using CrossSourceJoinPolicy = Chalk.Catalog.CrossSourceJoinPolicy;

namespace Chalk.Integration.Tests;

/// <summary>
/// Standing views under an access policy (D361, <c>docs/design/74-views-under-a-policy.md</c>). A
/// statement prepared through <see cref="EntitledEngine"/> is kept by incremental maintenance while
/// random epochs append, change and delete its rows and add and withdraw grants, and after every
/// epoch the result it holds is what the statement returns: a direct restriction with a resource
/// owner, masks and placeholders, a path through a grant table, a child through its parent, a list too
/// long to fold, and a group-size floor. A view of one, recomputed or maintained, follows its grants,
/// raises an audit event at each update, and keeps its policy when a table it reads changes shape.
/// </summary>
[Collection(SidecarCollection.Name)]
public sealed class ViewsUnderPolicyTests(SharedSidecar sidecar) : IDisposable
{
    private const string ContextId = "views-under-a-policy";

    private sealed record Team(int Id);

    private sealed record Reader(int Id);

    private sealed record Note(long Seq, int TeamId, int PatientId, int AuthorId, string Title, string Body, string Phone, int Score);

    private sealed record NoteGrant(int Id, long NoteSeq, int ReaderId);

    private sealed record Reply(long Seq, long NoteSeq, string Text);

    private sealed record Visit(long Seq, int TeamId, int PatientId, string Name);

    private sealed record Event(long Seq, int SiteId, int ClientId, int StaffId, string ClientUser, DateTime OccurredAt);

    private sealed record Mark(
        long Seq, long EventSeq, string MarkKind, bool IsFirst, string PersonRole, string PersonName, DateTime OccurredAt);

    private struct NoteKey
    {
        public long Seq;
    }

    private struct NoteRow
    {
        public byte Body;
    }

    private static readonly Team[] Teams = [new(1), new(2), new(3), new(4)];

    private static readonly Reader[] Readers = [new(7), new(8)];

    private static readonly TenancyEntitlements Policy = Compile();

    private readonly List<DbConnection> _open = [];

    /// <summary>
    /// A member of team 1, a viewer of team 2, and reader 7, who reads what a grant row shares with it
    /// in full: its own notes in full too, as their author.
    /// </summary>
    private static TenancyPrincipal Seven(string purpose = "", string actor = "") => new()
    {
        User = 7,
        Grants =
        [
            Grant.ForTenancy(Policy.Policy.Tenancy("team"), 1, Policy.Policy.Role("member")),
            Grant.ForTenancy(Policy.Policy.Tenancy("team"), 2, Policy.Policy.Role("viewer")),
            Grant.ForTenancy(Policy.Policy.Tenancy("reader"), 7, Policy.Policy.Role("consented")),
        ],
        Purpose = purpose,
        Actor = actor,
    };

    /// <summary>
    /// Reader 8: a member of teams 1 and 2, and the carer of three patients in teams 3 and 4. Past a
    /// fold ceiling of one, every list stays a relation bound at each run, read by the row predicate
    /// as a membership (F161).
    /// </summary>
    private static TenancyPrincipal Eight => new()
    {
        User = 8,
        Grants =
        [
            Grant.ForTenancy(Policy.Policy.Tenancy("team"), 1, Policy.Policy.Role("member")),
            Grant.ForTenancy(Policy.Policy.Tenancy("team"), 2, Policy.Policy.Role("member")),
            Grant.ForSubject(Patient, 101, Policy.Policy.Role("carer"), within: 3),
            Grant.ForSubject(Patient, 102, Policy.Policy.Role("carer"), within: 3),
            Grant.ForSubject(Patient, 103, Policy.Policy.Role("carer"), within: 4),
            Grant.ForTenancy(Policy.Policy.Tenancy("reader"), 8, Policy.Policy.Role("consented")),
        ],
        FoldMaxRows = 1,
    };

    /// <summary>
    /// A member of team 1 and the carer of three patients in teams 1 and 2. Past a fold ceiling of one,
    /// the patients stay a relation bound at each run, and the visits' row predicate is split over it
    /// into a semi and an anti join (F36).
    /// </summary>
    private static TenancyPrincipal Carer => new()
    {
        User = 9,
        Grants =
        [
            Grant.ForTenancy(Policy.Policy.Tenancy("team"), 1, Policy.Policy.Role("member")),
            Grant.ForSubject(Patient, 101, Policy.Policy.Role("carer"), within: 1),
            Grant.ForSubject(Patient, 102, Policy.Policy.Role("carer"), within: 2),
            Grant.ForSubject(Patient, 103, Policy.Policy.Role("carer"), within: 2),
        ],
        FoldMaxRows = 1,
    };

    /// <summary>The policy's patients, each confined to a team.</summary>
    private static SubjectKind Patient => Policy.Policy.Subject("patient", [Policy.Policy.Tenancy("team")]);

    /// <summary>Reader 7, who reads what a grant row shares with it, and its own notes as their author.</summary>
    private static TenancyPrincipal Sharer => ReaderOf(7);

    /// <summary>
    /// A reader who reads what a grant row shares with it, and its own notes as their author: every
    /// such reader binds a context of one shape, whatever its id.
    /// </summary>
    private static TenancyPrincipal ReaderOf(int id, string purpose = "", string actor = "") => new()
    {
        User = id,
        Grants = [Grant.ForTenancy(Policy.Policy.Tenancy("reader"), id, Policy.Policy.Role("consented"))],
        Purpose = purpose,
        Actor = actor,
    };

    /// <summary>A counter of teams 1 and 2, who may see their scores only as counts and sums of three or more.</summary>
    private static TenancyPrincipal Counter => new()
    {
        User = 99,
        Grants =
        [
            Grant.ForTenancy(Policy.Policy.Tenancy("team"), 1, Policy.Policy.Role("counter")),
            Grant.ForTenancy(Policy.Policy.Tenancy("team"), 2, Policy.Policy.Role("counter")),
        ],
    };

    /// <summary>
    /// The statements, each with its principal, what its plan must have — the joins it means to test,
    /// or a membership over a list bound at each run — and how many of its joins seek a governed table
    /// when the tables have indexes on their keys (design 74 §6).
    /// </summary>
    public static TheoryData<string, string, string, string?, int> Statements() => new()
    {
        {
            "a direct restriction, an owner, a path, masks and placeholders",
            "SELECT seq, team_id, title, body, phone FROM desk.notes",
            nameof(Seven),
            "Left",
            1
        },
        {
            // The anti join's left side reads the team as the constant the policy folded it to: held.
            "a list too long to fold, split into a semi and an anti join",
            "SELECT seq, team_id, patient_id, name FROM desk.visits",
            nameof(Carer),
            "Semi,Anti",
            1
        },
        {
            "a child through its parent",
            "SELECT seq, note_seq, text FROM desk.replies",
            nameof(Seven),
            null,
            2
        },
        {
            "a join of two tables under the policy",
            "SELECT r.seq, n.title, n.body, r.text FROM desk.replies r JOIN desk.notes n ON n.seq = r.note_seq",
            nameof(Seven),
            null,
            3
        },
        {
            "lists too long to fold, read as memberships",
            "SELECT seq, team_id, patient_id, title, body, phone FROM desk.notes",
            nameof(Eight),
            "@ctx",
            1
        },
        {
            "a group-size floor",
            "SELECT team_id, COUNT(score) AS n, SUM(score) AS total FROM desk.notes GROUP BY team_id",
            nameof(Counter),
            null,
            0
        },
    };

    /// <summary>
    /// Each statement under the policy, kept by maintenance over 30 random epochs under two seeds, is
    /// what the statement returns after every one; each plan is checked to have what it means to test.
    /// Each is kept twice: over tables with no index, every join holding both its sides, and over
    /// tables with indexes on their keys, the joins that read a governed table seeking it.
    /// </summary>
    [Theory]
    [MemberData(nameof(Statements))]
    public async Task Every_epoch_a_statement_under_a_policy_is_kept(
        string name, string sql, string principal, string? planned, int seeking)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        _ = name;
        var who = principal switch
        {
            nameof(Seven) => Seven(),
            nameof(Eight) => Eight,
            nameof(Carer) => Carer,
            _ => Counter,
        };

        foreach (var (indexed, seed) in new[] { (false, 11), (false, 23), (true, 11), (true, 23) })
        {
            var epochs = await RunAsync(sql, who, seed, planned, indexed ? seeking : 0, indexed);
            if (principal == nameof(Counter))
            {
                // Team 1's group is pushed over the floor, under it and over it again: its total is
                // given, withheld and given.
                var given = epochs.Select(rows => rows.TryGetValue("1", out var row) && row[2] is not null).ToList();
                Assert.True(given[2], $"seed {seed}: team 1's total is withheld at epoch 2");
                Assert.False(given[8], $"seed {seed}: team 1's total is given at epoch 8");
                Assert.True(given[14], $"seed {seed}: team 1's total is withheld at epoch 14");
            }
        }
    }

    /// <summary>
    /// A statement of left joins to a child visible through its parent, which the policy reads as
    /// left joins to the parent joined with the child. Over five thousand events the planner builds
    /// one of them on the other side, as a right join, and maintenance refused the plan, so a view
    /// of it recomputed at every update (F230). Kept now as the left join it mirrors, through twelve
    /// epochs that add events and marks, take marks out, change them, and take an event out with its
    /// marks, and after each it is what the statement returns.
    /// </summary>
    [Fact]
    public async Task A_statement_planned_with_a_right_join_is_kept()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var start = new DateTime(2026, 10, 16, 8, 0, 0);
        var events = new Dictionary<long, Event>();
        var marks = new Dictionary<long, Mark>();
        var markSeq = 0L;
        void Add(Event added)
        {
            events[added.Seq] = added;
            if (added.Seq % 3 == 0)
            {
                marks[++markSeq] = new Mark(markSeq, added.Seq, "acknowledged", false, "client", "client", added.OccurredAt);
            }

            for (var s = 0; s < 2; s++)
            {
                marks[++markSeq] = new Mark(markSeq, added.Seq, "seen", s == 0, "desk", $"person {s}", added.OccurredAt.AddSeconds(s + 1));
            }
        }

        Event NewEvent(long seq) => new(seq, 1 + (int)(seq % 2), 1000 + (int)(seq % 200), 201 + (int)(seq % 3), $"client-{1000 + (seq % 200)}", start.AddSeconds(seq));
        for (var i = 1L; i <= 5000; i++)
        {
            Add(NewEvent(i));
        }

        var (source, policy) = Events([.. events.Values], [.. marks.Values]);
        await using var engine = await EngineAsync(source);
        var desk = new TenancyPrincipal
        {
            User = "class:desk@1",
            Grants = [Grant.ForTenancy(policy.Policy.Tenancy("site"), 1, policy.Policy.Role("desk"))],
        };
        var statement = await engine.WithEntitlements(new EntitlementsOptions()).PrepareAsync(
            """
            SELECT e.seq, a.occurred_at AS acked_at, a.person_name AS acked_by, k.read_at, f.person_name AS read_by, k.readers
            FROM desk.events e
            LEFT JOIN desk.marks a ON a.event_seq = e.seq AND a.mark_kind = 'acknowledged'
            LEFT JOIN desk.marks f ON f.event_seq = e.seq AND f.mark_kind = 'seen' AND f.is_first
            LEFT JOIN (SELECT event_seq, MIN(occurred_at) AS read_at,
                              COUNT(CASE WHEN person_role = 'desk' THEN 1 END) AS readers
                       FROM desk.marks
                       WHERE mark_kind = 'seen' AND person_role <> 'client'
                       GROUP BY event_seq) k ON k.event_seq = e.seq
            ORDER BY e.seq DESC LIMIT 300
            """,
            policy.Bind(desk),
            ct: Ct);
        Assert.True(
            JoinTypes(statement.Plan).Contains(JoinType.Right),
            $"the statement is not planned with a right join: {statement.Plan.ToPlanText()}");

        await using var maintained = await statement.Query.MaintainAsync([], -1, [0], Ct);
        var held = new Dictionary<string, object?[]>(StringComparer.Ordinal);
        var random = new Random(5);
        var seq = events.Keys.Max();
        for (var epoch = 0; epoch < 12; epoch++)
        {
            if (epoch > 0)
            {
                var before = marks.Keys.ToHashSet();
                var added = new List<Event>();
                for (var i = random.Next(1, 4); i > 0; i--)
                {
                    var next = NewEvent(++seq);
                    Add(next);
                    added.Add(next);
                }

                // Marks taken out and marks changed, each event keeping one acknowledgement and one
                // first sighting at most, so the statement keeps one row an event.
                var changed = new List<Mark>();
                var gone = new List<Mark>();
                var lost = new List<Event>();
                foreach (var key in before.Where(_ => random.Next(400) == 0).ToList())
                {
                    var mark = marks[key];
                    if (random.Next(2) == 0)
                    {
                        marks.Remove(key);
                        gone.Add(mark);
                    }
                    else
                    {
                        changed.Add(marks[key] = mark with
                        {
                            PersonRole = mark.PersonRole == "desk" ? "client" : "desk",
                            PersonName = $"{mark.PersonName} again",
                            OccurredAt = mark.OccurredAt.AddMinutes(1),
                        });
                    }
                }

                var acknowledged = marks.Values.Where(m => m.MarkKind == "acknowledged").Select(m => m.EventSeq).ToHashSet();
                foreach (var waiting in events.Keys.Where(key => !acknowledged.Contains(key)).OrderBy(_ => random.Next()).Take(2).ToList())
                {
                    marks[++markSeq] = new Mark(markSeq, waiting, "acknowledged", false, "desk", "person 9", start.AddDays(1));
                }

                if (epoch % 4 == 0)
                {
                    // The newest event the view showed leaves, with its marks.
                    var last = events[events.Keys.Where(key => added.All(a => a.Seq != key)).Max()];
                    events.Remove(last.Seq);
                    lost.Add(last);
                    foreach (var mark in marks.Values.Where(m => m.EventSeq == last.Seq).ToList())
                    {
                        marks.Remove(mark.Seq);
                        changed.Remove(mark);
                        gone.Add(mark);
                    }
                }

                var appended = marks.Values.Where(m => !before.Contains(m.Seq)).ToList();
                await engine.RefreshAsync(
                    r =>
                    {
                        if (gone.Count > 0)
                        {
                            r.Delete(source, "marks", (IReadOnlyList<Mark>)gone);
                        }

                        if (lost.Count > 0)
                        {
                            r.Delete(source, "events", (IReadOnlyList<Event>)lost);
                        }

                        r.Upsert(source, "events", (IReadOnlyList<Event>)added);
                        r.Upsert(source, "marks", (IReadOnlyList<Mark>)[.. appended.Where(m => events.ContainsKey(m.EventSeq)), .. changed]);
                    },
                    Ct);
            }

            var update = await maintained.UpdateAsync([], default, engine.Version, false, Ct);

            Apply(update, held);
            Compare(held, await StatementAsync(engine, statement), 5, epoch);
        }
    }

    /// <summary>
    /// A grant kept in a table is a refresh away: sharing a note with the reader puts it in the view in
    /// full at the next update, and withdrawing the share takes it out, whether the view recomputes or
    /// is maintained.
    /// </summary>
    [Theory]
    [InlineData(ViewMaintenance.Recompute)]
    [InlineData(ViewMaintenance.Incremental)]
    public async Task A_view_under_a_policy_follows_its_grants(ViewMaintenance maintenance)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        Note[] notes =
        [
            new(1, 3, 101, 5, "one", "body-1", "555-0101", 1),
            new(2, 1, 102, 5, "two", "body-2", "555-0102", 2),
            new(3, 2, 103, 7, "three", "body-3", "555-0103", 3),
        ];
        var source = Desk(Policy, notes, [], []);
        await using var engine = await EngineAsync(source);
        var entitled = engine.WithEntitlements(new EntitlementsOptions());
        var feed = await entitled.PrepareAsync("SELECT seq, body FROM desk.notes", Policy.Bind(Sharer), ct: Ct);
        await using var view = await engine.MaterializeAsync(feed.Query, Options(maintenance), Ct);
        var replica = new Replica();
        var cursor = ViewCursor.Start;
        view.Drain(ref cursor, replica);

        // Its own note, as the author; nothing else is shared with it yet.
        Assert.Equal(new Dictionary<long, byte> { [3] = Full }, replica.Rows);
        await AssertIsTheStatementAsync(engine, feed, replica);

        await engine.RefreshAsync(r => r.Upsert(source, "note_grants", Shares(new NoteGrant(1, 1, 7), new NoteGrant(2, 2, 7))), Ct);
        await view.UpdateAsync(Ct);
        view.Drain(ref cursor, replica);
        Assert.Equal(new Dictionary<long, byte> { [1] = Full, [2] = Full, [3] = Full }, replica.Rows);
        await AssertIsTheStatementAsync(engine, feed, replica);

        await engine.RefreshAsync(r => r.Delete(source, "note_grants", Shares(new NoteGrant(1, 1, 7))), Ct);
        await view.UpdateAsync(Ct);
        view.Drain(ref cursor, replica);
        Assert.Equal(new Dictionary<long, byte> { [2] = Full, [3] = Full }, replica.Rows);

        // A share of a note to another reader is not this reader's.
        await engine.RefreshAsync(
            r => r.Delete(source, "note_grants", Shares(new NoteGrant(2, 2, 7))).Upsert(source, "note_grants", Shares(new NoteGrant(3, 2, 8))),
            Ct);
        await view.UpdateAsync(Ct);
        view.Drain(ref cursor, replica);
        Assert.Equal(new Dictionary<long, byte> { [3] = Full }, replica.Rows);
        await AssertIsTheStatementAsync(engine, feed, replica);
    }

    /// <summary>
    /// A reader's view of two thousand notes, ten of them shared with it along the path. Over notes with
    /// an index on their key, maintenance holds the reader's shares and the rows it sees, and not the
    /// notes; without one, every note (design 74 §6). A share added later is one key sought, one note
    /// read.
    /// </summary>
    [Fact]
    public async Task A_view_under_a_policy_holds_its_readers_share_of_an_indexed_table()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var notes = Enumerable.Range(1, 2_000)
            .Select(i => new Note(i, 3, 104, 5, $"title-{i}", $"body-{i}", $"555-{i:0000}", i % 10))
            .ToList();
        var grants = Enumerable.Range(1, 10).Select(i => new NoteGrant(i, i * 100, 7)).ToList();
        var bytes = new Dictionary<bool, long>();
        foreach (var indexed in new[] { true, false })
        {
            var source = Desk(Policy, notes, grants, [], indexed: indexed);
            await using var engine = await EngineAsync(source);
            var entitled = engine.WithEntitlements(new EntitlementsOptions());
            var feed = await entitled.PrepareAsync("SELECT seq, body FROM desk.notes", Policy.Bind(Sharer), ct: Ct);
            await using var maintained = await feed.Query.MaintainAsync([], -1, [0], Ct);
            var first = await maintained.UpdateAsync([], default, engine.Version, whole: false, Ct);
            Assert.Equal(10, first.Now.Length);
            bytes[indexed] = maintained.StateBytes;
            if (!indexed)
            {
                Assert.Empty(maintained.States.OfType<JoinSeekState>());
                continue;
            }

            var join = Assert.Single(maintained.States.OfType<JoinSeekState>());
            await engine.RefreshAsync(r => r.Upsert(source, "note_grants", Shares(new NoteGrant(11, 1_500, 7))), Ct);
            var shared = await maintained.UpdateAsync([], default, engine.Version, whole: false, Ct);
            Assert.Equal(1, shared.Now.Length);
            Assert.Equal((1, 1L), (join.Sought, join.Read));
        }

        Assert.True(bytes[true] * 20 < bytes[false], $"seeking, the view holds {bytes[true]} bytes; holding, {bytes[false]}");
    }

    /// <summary>
    /// Every update that runs is audited, recomputed or maintained (D361): one event each, before it
    /// runs, with the purpose and actor of the context the statement was prepared with, the version the
    /// update brings the view to, and the digest of the plan it ran. An update at the epoch the view
    /// already holds runs nothing and raises nothing.
    /// </summary>
    [Theory]
    [InlineData(ViewMaintenance.Recompute)]
    [InlineData(ViewMaintenance.Incremental)]
    public async Task Each_update_of_a_view_under_a_policy_is_audited(ViewMaintenance maintenance)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var source = Desk(Policy, [new(1, 1, 101, 5, "one", "body-1", "555-0101", 1)], [], []);
        await using var engine = await EngineAsync(source);
        await engine.RefreshAsync(r => r.Version(10), Ct);
        var audit = new Audit();
        var entitled = engine.WithEntitlements(new EntitlementsOptions(), audit);
        var feed = await entitled.PrepareAsync(
            "SELECT seq, body FROM desk.notes", Policy.Bind(Seven("care", "reader 7")), ct: Ct);
        Assert.Empty(audit.Events);

        await using var view = await engine.MaterializeAsync(feed.Query, Options(maintenance), Ct);
        await engine.RefreshAsync(r => r.Version(20).Upsert(source, "note_grants", Shares(new NoteGrant(1, 1, 7))), Ct);
        await view.UpdateAsync(Ct);
        await view.UpdateAsync(Ct);
        await engine.RefreshAsync(r => r.Version(30).Delete(source, "note_grants", Shares(new NoteGrant(1, 1, 7))), Ct);
        await view.UpdateAsync(Ct);

        Assert.Equal([10L, 20L, 30L], audit.Events.Select(e => e.Version));
        Assert.All(audit.Events, e =>
        {
            Assert.Equal("care", e.Purpose);
            Assert.Equal("reader 7", e.Actor);
            Assert.Equal(feed.PlanDigest, e.PlanDigest);
            Assert.Equal(feed.Entitlements.DescriptorHashes, e.DescriptorHashes);
            Assert.Contains(e.Tables, t => t.Table == "notes");
        });

        // An execution of the statement is audited as before, with no version: it reads no one epoch.
        await using (var execution = await engine.ExecuteAsync(feed, ct: Ct))
        {
            await foreach (var batch in execution.Batches)
            {
                batch.Dispose();
            }
        }

        Assert.Equal(4, audit.Events.Count);
        Assert.Null(audit.Events[^1].Version);
        Assert.Equal("care", audit.Events[^1].Purpose);
    }

    /// <summary>
    /// A view whose database table changes shape prepares its statement again (design 70): through the
    /// policy, with the context it was prepared with, so the new statement reads the same rows of the
    /// table, and its updates are audited as the old one's were.
    /// </summary>
    [Fact]
    public async Task A_view_under_a_policy_keeps_its_policy_when_a_table_changes_shape()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var (source, connection) = Database();
        await using var engine = await ChalkEngine.CreateAsync(
            new ChalkEngineOptions { ContextId = ContextId, Sources = [source], Planner = sidecar.CreatePlanner() }, Ct);
        var audit = new Audit();
        var entitled = engine.WithEntitlements(new EntitlementsOptions(), audit);
        var context = new RequestContext
        {
            Lists = new Dictionary<string, ContextRelation>(StringComparer.Ordinal)
            {
                ["teams"] = new() { Columns = ["id"], Rows = [[1L]], ColumnTypes = [ChalkType.Int64()] },
            },
            Purpose = "care",
            Actor = "reader 7",
        };
        var feed = await entitled.PrepareAsync(
            "SELECT seq, body FROM db.notes",
            context,
            new PrepareOptions
            {
                JoinPolicy = new CrossSourceJoinPolicy { LocalJoinMaxRows = StandingViews.DefaultLocalJoinMaxRows },
            },
            Ct);
        await using var view = await engine.MaterializeAsync(
            feed.Query, Options(ViewMaintenance.Recompute, ViewDatabaseReads.WhenRefreshed), Ct);
        var replica = new Replica();
        var cursor = ViewCursor.Start;
        view.Drain(ref cursor, replica);
        Assert.Equal([1L, 3L], replica.Rows.Keys.Order());

        Execute(connection, "ALTER TABLE notes ADD COLUMN extra REAL");
        Execute(connection, "INSERT INTO notes (seq, team_id, body) VALUES (4, 1, 'four'), (5, 2, 'five')");
        await engine.RefreshAsync(Ct);
        await view.UpdateAsync(Ct);

        Assert.NotSame(feed.Query, view.Statement);
        Assert.True(feed.Query.ChangedShape);
        view.Drain(ref cursor, replica);
        Assert.Equal([1L, 3L, 4L], replica.Rows.Keys.Order());
        Assert.Equal(2, audit.Events.Count);
        Assert.All(audit.Events, e => Assert.Equal(("care", "reader 7"), (e.Purpose, e.Actor)));
        Assert.Equal(view.Statement.PlanDigest, audit.Events[^1].PlanDigest);
    }

    /// <summary>
    /// A view of a statement prepared for a context's shape binds a principal's values, and is refused
    /// when made without them, recomputed or maintained (design 74 §3 c).
    /// </summary>
    [Theory]
    [InlineData(ViewMaintenance.Recompute)]
    [InlineData(ViewMaintenance.Incremental)]
    public async Task A_view_of_a_statement_prepared_for_a_shape_needs_its_values(ViewMaintenance maintenance)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var source = Desk(Policy, [new(1, 1, 101, 5, "one", "body-1", "555-0101", 1)], [], []);
        await using var engine = await EngineAsync(source);
        var entitled = engine.WithEntitlements(new EntitlementsOptions());
        var shaped = await entitled.PrepareAsync("SELECT seq, body FROM desk.notes", Policy.Bind(Seven()).Shape(), ct: Ct);

        var refused = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await engine.MaterializeAsync(shaped.Query, Options(maintenance), Ct));
        Assert.Contains("give the view them in ViewOptions.Context", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// One statement, prepared once for the shape every reader binds, serves two readers' views, each
    /// binding its own values for its life (design 74 §3 c). Each view is what the statement returns
    /// for its reader, through shares made and withdrawn, and both run the one statement. Each reports
    /// its reader's binding as the context it was made with (§3 a).
    /// </summary>
    [Theory]
    [InlineData(ViewMaintenance.Recompute)]
    [InlineData(ViewMaintenance.Incremental)]
    public async Task One_statement_prepared_for_a_shape_serves_every_readers_view(ViewMaintenance maintenance)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        Note[] notes =
        [
            new(1, 3, 101, 5, "one", "body-1", "555-0101", 1),
            new(2, 1, 102, 5, "two", "body-2", "555-0102", 2),
            new(3, 2, 103, 7, "three", "body-3", "555-0103", 3),
        ];
        var source = Desk(Policy, notes, [], []);
        await using var engine = await EngineAsync(source);
        var entitled = engine.WithEntitlements(new EntitlementsOptions());
        var shaped = await entitled.PrepareAsync("SELECT seq, body FROM desk.notes", Policy.Bind(ReaderOf(7)).Shape(), ct: Ct);
        var seven = Policy.Bind(ReaderOf(7));
        var eight = Policy.Bind(ReaderOf(8));
        await using var sevens = await engine.MaterializeAsync(shaped.Query, Options(maintenance, context: seven), Ct);
        await using var eights = await engine.MaterializeAsync(shaped.Query, Options(maintenance, context: eight), Ct);
        Assert.Same(sevens.Statement, eights.Statement);
        Assert.Equal(seven.CanonicalHash, sevens.ContextDigest);
        Assert.Equal(eight.CanonicalHash, eights.ContextDigest);
        Assert.NotEqual(sevens.ContextDigest, eights.ContextDigest);

        var (sevenRows, eightRows) = (new Replica(), new Replica());
        var (sevenCursor, eightCursor) = (ViewCursor.Start, ViewCursor.Start);
        async Task CheckAsync(Dictionary<long, byte> forSeven, Dictionary<long, byte> forEight)
        {
            sevens.Drain(ref sevenCursor, sevenRows);
            eights.Drain(ref eightCursor, eightRows);
            Assert.Equal(forSeven, sevenRows.Rows);
            Assert.Equal(forEight, eightRows.Rows);
            await AssertIsTheStatementAsync(engine, shaped, sevenRows, seven);
            await AssertIsTheStatementAsync(engine, shaped, eightRows, eight);
        }

        // Each its own note, as the author: reader 7 wrote note 3, and reader 8 none.
        await CheckAsync(new() { [3] = Full }, []);

        await engine.RefreshAsync(r => r.Upsert(source, "note_grants", Shares(new NoteGrant(1, 1, 7), new NoteGrant(2, 2, 8))), Ct);
        await sevens.UpdateAsync(Ct);
        await eights.UpdateAsync(Ct);
        await CheckAsync(new() { [1] = Full, [3] = Full }, new() { [2] = Full });

        await engine.RefreshAsync(
            r => r.Delete(source, "note_grants", Shares(new NoteGrant(1, 1, 7))).Upsert(source, "note_grants", Shares(new NoteGrant(3, 3, 8))),
            Ct);
        await sevens.UpdateAsync(Ct);
        await eights.UpdateAsync(Ct);
        await CheckAsync(new() { [3] = Full }, new() { [2] = Full, [3] = Full });
    }

    /// <summary>
    /// Each reader's view of one shared statement is audited as that reader: every update's event
    /// carries the purpose and the actor of the context the view binds, and the one plan's digest
    /// (design 74 §3 b, c).
    /// </summary>
    [Theory]
    [InlineData(ViewMaintenance.Recompute)]
    [InlineData(ViewMaintenance.Incremental)]
    public async Task Each_readers_view_of_a_shared_statement_is_audited_as_that_reader(ViewMaintenance maintenance)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var source = Desk(Policy, [new(1, 1, 101, 7, "one", "body-1", "555-0101", 1)], [], []);
        await using var engine = await EngineAsync(source);
        await engine.RefreshAsync(r => r.Version(10), Ct);
        var audit = new Audit();
        var entitled = engine.WithEntitlements(new EntitlementsOptions(), audit);
        var shaped = await entitled.PrepareAsync("SELECT seq, body FROM desk.notes", Policy.Bind(ReaderOf(7)).Shape(), ct: Ct);

        await using var sevens = await engine.MaterializeAsync(
            shaped.Query, Options(maintenance, context: Policy.Bind(ReaderOf(7, "care", "reader 7"))), Ct);
        await using var eights = await engine.MaterializeAsync(
            shaped.Query, Options(maintenance, context: Policy.Bind(ReaderOf(8, "billing", "reader 8"))), Ct);
        await engine.RefreshAsync(r => r.Version(20).Upsert(source, "note_grants", Shares(new NoteGrant(1, 1, 8))), Ct);
        await sevens.UpdateAsync(Ct);
        await eights.UpdateAsync(Ct);

        var events = audit.Events;
        Assert.Equal([10L, 20L], events.Where(e => e.Actor == "reader 7").Select(e => e.Version));
        Assert.Equal([10L, 20L], events.Where(e => e.Actor == "reader 8").Select(e => e.Version));
        Assert.All(events, e =>
        {
            Assert.Equal(e.Actor == "reader 7" ? "care" : "billing", e.Purpose);
            Assert.Equal(shaped.PlanDigest, e.PlanDigest);
        });
    }

    /// <summary>
    /// Values that do not fit the statement are refused when the view is made, as an execution
    /// refuses them: values for a statement that carries its own, a shape, and values for what a
    /// tenant's plan folded that are not the ones it folded. A view made from a statement's text
    /// prepares it with no context, and refuses one.
    /// </summary>
    [Fact]
    public async Task A_view_whose_values_do_not_fit_its_statement_is_refused()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var source = Desk(Policy, [new(1, 1, 101, 5, "one", "body-1", "555-0101", 1)], [], []);
        await using var engine = await EngineAsync(source);
        var entitled = engine.WithEntitlements(new EntitlementsOptions());
        const string Sql = "SELECT seq, body FROM desk.notes";
        var folded = await entitled.PrepareAsync(Sql, Policy.Bind(Seven()), ct: Ct);
        var shaped = await entitled.PrepareAsync(Sql, Policy.Bind(Seven()).Shape(), ct: Ct);
        var tenant = await entitled.PrepareAsync(Sql, Policy.Bind(Seven(), fold: ["team"]), ct: Ct);

        async Task<string> RefusedAsync(PreparedQuery statement, RequestContext context) =>
            (await Assert.ThrowsAsync<ArgumentException>(async () =>
                await engine.MaterializeAsync(statement, Options(ViewMaintenance.Recompute, context: context), Ct))).Message;

        Assert.Contains("prepared with its context values and carries them", await RefusedAsync(folded.Query, Policy.Bind(Seven())), StringComparison.Ordinal);
        Assert.Contains("as shapes again", await RefusedAsync(shaped.Query, Policy.Bind(Seven()).Shape()), StringComparison.Ordinal);
        Assert.Contains("folded", await RefusedAsync(tenant.Query, Policy.Bind(ReaderOf(8))), StringComparison.Ordinal);

        var text = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await engine.MaterializeAsync(Sql, Options(ViewMaintenance.Recompute, context: Policy.Bind(Seven())), Ct));
        Assert.Contains("statement's text", text.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A tenant's plan folds the team grants and leaves the rest open (D232): it serves the views of
    /// two principals with those grants, each binding its own reader and user, each what the statement
    /// returns for it.
    /// </summary>
    [Fact]
    public async Task A_tenant_plan_serves_the_views_of_its_principals()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        Note[] notes =
        [
            new(1, 1, 101, 5, "one", "body-1", "555-0101", 1),
            new(2, 2, 102, 7, "two", "body-2", "555-0102", 2),
            new(3, 3, 103, 8, "three", "body-3", "555-0103", 3),
        ];
        var source = Desk(Policy, notes, [new NoteGrant(1, 3, 7)], []);
        await using var engine = await EngineAsync(source);
        var entitled = engine.WithEntitlements(new EntitlementsOptions());
        var tenant = await entitled.PrepareAsync("SELECT seq, body FROM desk.notes", Policy.Bind(Seven(), fold: ["team"]), ct: Ct);
        var twin = new TenancyPrincipal { User = 8, Grants = [.. Seven().Grants.Take(2), Grant.ForTenancy(Policy.Policy.Tenancy("reader"), 8, Policy.Policy.Role("consented"))] };

        foreach (var principal in new[] { Seven(), twin })
        {
            var context = Policy.Bind(principal);
            await using var view = await engine.MaterializeAsync(tenant.Query, Options(ViewMaintenance.Incremental, context: context), Ct);
            var replica = new Replica();
            var cursor = ViewCursor.Start;
            view.Drain(ref cursor, replica);
            Assert.NotEmpty(replica.Rows);
            await AssertIsTheStatementAsync(engine, tenant, replica, context);
            Assert.Equal(context.CanonicalHash, view.ContextDigest);
        }
    }

    /// <summary>
    /// A view of a statement prepared with its principal's values reports the digest of that context,
    /// so a host whose principal's grants change finds the view, which stands on the old grants, and
    /// makes it again (design 74 §3 a). Purpose and actor are not grants: the digest does not move
    /// with them.
    /// </summary>
    [Fact]
    public async Task A_view_reports_the_digest_of_the_grants_it_was_made_with()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var source = Desk(Policy, [new(1, 1, 101, 5, "one", "body-1", "555-0101", 1)], [], []);
        await using var engine = await EngineAsync(source);
        var entitled = engine.WithEntitlements(new EntitlementsOptions());
        var folded = await entitled.PrepareAsync("SELECT seq, body FROM desk.notes", Policy.Bind(Seven()), ct: Ct);
        await using var view = await engine.MaterializeAsync(folded.Query, Options(ViewMaintenance.Incremental), Ct);

        Assert.Equal(Policy.Bind(Seven()).CanonicalHash, view.ContextDigest);
        Assert.Equal(Policy.Bind(Seven("audit", "someone else")).CanonicalHash, view.ContextDigest);
        var widened = new TenancyPrincipal
        {
            User = 7,
            Grants = [.. Seven().Grants, Grant.ForTenancy(Policy.Policy.Tenancy("team"), 3, Policy.Policy.Role("member"))],
        };
        Assert.NotEqual(Policy.Bind(widened).CanonicalHash, view.ContextDigest);
    }

    public void Dispose()
    {
        foreach (var connection in _open)
        {
            connection.Dispose();
        }
    }

    // ------------------------------------------------------------------ the random epochs

    /// <summary>Keeps the statement through 30 epochs, comparing it after each; returns what it gave at each.</summary>
    private async Task<List<Dictionary<string, object?[]>>> RunAsync(
        string sql, TenancyPrincipal who, int seed, string? planned, int seeking, bool indexed)
    {
        var epochs = new List<Dictionary<string, object?[]>>();
        var random = new Random(seed);
        var seq = 0L;
        var grantId = 0;
        var notes = new Dictionary<long, Note>();
        var grants = new Dictionary<int, NoteGrant>();
        var replies = new Dictionary<long, Reply>();
        for (var i = 0; i < 14; i++)
        {
            var note = NewNote(random, ++seq);
            notes[note.Seq] = note;
        }

        for (var i = 0; i < 4; i++)
        {
            var grant = new NoteGrant(++grantId, 1 + random.Next((int)seq), 7 + random.Next(2));
            grants[grant.Id] = grant;
        }

        var replySeq = 0L;
        for (var i = 0; i < 6; i++)
        {
            var reply = new Reply(++replySeq, 1 + random.Next((int)seq), $"reply-{replySeq}");
            replies[reply.Seq] = reply;
        }

        var visitSeq = 0L;
        var visits = new Dictionary<long, Visit>();
        for (var i = 0; i < 12; i++)
        {
            var visit = NewVisit(random, ++visitSeq);
            visits[visit.Seq] = visit;
        }

        var source = Desk(Policy, [.. notes.Values], [.. grants.Values], [.. replies.Values], [.. visits.Values], indexed);
        await using var engine = await EngineAsync(source);
        var entitled = engine.WithEntitlements(new EntitlementsOptions());
        var statement = await entitled.PrepareAsync(sql, Policy.Bind(who), ct: Ct);
        if (planned is not null)
        {
            var text = statement.Plan.ToPlanText();
            var has = planned == "@ctx"
                ? text.Contains(" IN @ctx.", StringComparison.Ordinal)
                : planned.Split(',').All(join => JoinTypes(statement.Plan).Contains(Enum.Parse<JoinType>(join)));
            Assert.True(has, $"the statement is not planned with {planned}: {text}");
        }

        await using var maintained = await statement.Query.MaintainAsync([], -1, [0], Ct);
        Assert.Equal(seeking, maintained.States.OfType<JoinSeekState>().Count());
        var held = new Dictionary<string, object?[]>(StringComparer.Ordinal);
        for (var epoch = 0; epoch < 30; epoch++)
        {
            if (epoch > 0)
            {
                // What each changed note becomes, once per key.
                var upserted = new Dictionary<long, Note>();
                var deleted = new List<Note>();
                var shared = new List<NoteGrant>();
                var withdrawn = new List<NoteGrant>();
                var replied = new List<Reply>();
                var unreplied = new List<Reply>();
                var visited = new List<Visit>();
                var unvisited = new List<Visit>();
                switch (epoch)
                {
                    case 2:
                        // Team 1 over the floor of three: three notes of other teams move to it.
                        foreach (var note in notes.Values.Where(n => n.TeamId != 1).Take(3).ToList())
                        {
                            upserted[note.Seq] = notes[note.Seq] = note with { TeamId = 1 };
                        }

                        break;
                    case 8:
                        // Team 1 below the floor of three: all but two of its notes move to team 3.
                        foreach (var note in notes.Values.Where(n => n.TeamId == 1).Skip(2).ToList())
                        {
                            upserted[note.Seq] = notes[note.Seq] = note with { TeamId = 3 };
                        }

                        break;
                    case 14:
                        // And back above it.
                        for (var i = 0; i < 3; i++)
                        {
                            upserted[++seq] = notes[seq] = NewNote(random, seq) with { TeamId = 1 };
                        }

                        break;
                    default:
                        for (var i = random.Next(3); i > 0; i--)
                        {
                            upserted[++seq] = notes[seq] = NewNote(random, seq);
                        }

                        for (var i = random.Next(3); i > 0 && notes.Count > 0; i--)
                        {
                            var note = notes.Values.ElementAt(random.Next(notes.Count));
                            upserted[note.Seq] = notes[note.Seq] = note with
                            {
                                TeamId = 1 + random.Next(4),
                                PatientId = 101 + random.Next(4),
                                AuthorId = 5 + random.Next(5),
                                Body = $"body-{note.Seq}-{epoch}",
                                Score = random.Next(10),
                            };
                        }

                        if (random.Next(3) == 0 && notes.Count > 4)
                        {
                            // A note leaves with its shares and replies, so no key names a note that is gone.
                            var note = notes.Values.ElementAt(random.Next(notes.Count));
                            notes.Remove(note.Seq);
                            upserted.Remove(note.Seq);
                            deleted.Add(note);
                            foreach (var grant in grants.Values.Where(g => g.NoteSeq == note.Seq).ToList())
                            {
                                grants.Remove(grant.Id);
                                withdrawn.Add(grant);
                            }

                            foreach (var reply in replies.Values.Where(r => r.NoteSeq == note.Seq).ToList())
                            {
                                replies.Remove(reply.Seq);
                                unreplied.Add(reply);
                            }
                        }

                        // What is withdrawn is withdrawn before anything is added, so no key is both.
                        if (random.Next(2) == 0 && grants.Count > 0)
                        {
                            var grant = grants.Values.ElementAt(random.Next(grants.Count));
                            grants.Remove(grant.Id);
                            withdrawn.Add(grant);
                        }

                        if (random.Next(3) == 0 && replies.Count > 0)
                        {
                            var reply = replies.Values.ElementAt(random.Next(replies.Count));
                            replies.Remove(reply.Seq);
                            unreplied.Add(reply);
                        }

                        if (random.Next(3) == 0 && visits.Count > 0)
                        {
                            var visit = visits.Values.ElementAt(random.Next(visits.Count));
                            visits.Remove(visit.Seq);
                            unvisited.Add(visit);
                        }

                        for (var i = random.Next(3); i > 0; i--)
                        {
                            visited.Add(visits[++visitSeq] = NewVisit(random, visitSeq));
                        }

                        if (random.Next(2) == 0 && visits.Count > 0)
                        {
                            var visit = visits.Values.ElementAt(random.Next(visits.Count));
                            if (!unvisited.Contains(visit) && !visited.Contains(visit))
                            {
                                visited.Add(visits[visit.Seq] = visit with { TeamId = 1 + random.Next(4), PatientId = 101 + random.Next(4) });
                            }
                        }

                        for (var i = random.Next(3); i > 0; i--)
                        {
                            var grant = new NoteGrant(++grantId, notes.Keys.ElementAt(random.Next(notes.Count)), 7 + random.Next(2));
                            grants[grant.Id] = grant;
                            shared.Add(grant);
                        }

                        for (var i = random.Next(3); i > 0; i--)
                        {
                            var reply = new Reply(++replySeq, notes.Keys.ElementAt(random.Next(notes.Count)), $"reply-{replySeq}");
                            replies[reply.Seq] = reply;
                            replied.Add(reply);
                        }

                        break;
                }

                await engine.RefreshAsync(
                    r =>
                    {
                        if (upserted.Count > 0)
                        {
                            r.Upsert(source, "notes", (IReadOnlyList<Note>)[.. upserted.Values]);
                        }

                        if (shared.Count > 0)
                        {
                            r.Upsert(source, "note_grants", (IReadOnlyList<NoteGrant>)shared);
                        }

                        if (withdrawn.Count > 0)
                        {
                            r.Delete(source, "note_grants", (IReadOnlyList<NoteGrant>)withdrawn);
                        }

                        if (replied.Count > 0)
                        {
                            r.Upsert(source, "replies", (IReadOnlyList<Reply>)replied);
                        }

                        if (unreplied.Count > 0)
                        {
                            r.Delete(source, "replies", (IReadOnlyList<Reply>)unreplied);
                        }

                        if (deleted.Count > 0)
                        {
                            r.Delete(source, "notes", (IReadOnlyList<Note>)deleted);
                        }

                        if (visited.Count > 0)
                        {
                            r.Upsert(source, "visits", (IReadOnlyList<Visit>)visited);
                        }

                        if (unvisited.Count > 0)
                        {
                            r.Delete(source, "visits", (IReadOnlyList<Visit>)unvisited);
                        }
                    },
                    Ct);
            }

            var whole = random.Next(10) == 0;
            var update = await maintained.UpdateAsync([], default, engine.Version, whole, Ct);
            Assert.Equal(epoch == 0, update.Restarted);
            Apply(update, held);
            var expected = await StatementAsync(engine, statement);
            Compare(held, expected, seed, epoch);
            epochs.Add(expected);
        }

        return epochs;
    }

    private static Note NewNote(Random random, long seq) =>
        new(seq, 1 + random.Next(4), 101 + random.Next(4), 5 + random.Next(5), $"title-{seq}", $"body-{seq}", $"555-{seq:0000}", random.Next(10));

    private static Visit NewVisit(Random random, long seq) => new(seq, 1 + random.Next(4), 101 + random.Next(4), $"name-{seq}");

    private static HashSet<JoinType> JoinTypes(Plan plan)
    {
        var found = new HashSet<JoinType>();
        foreach (var rel in PlanWalker.Rels(plan))
        {
            JoinType? type = rel.KindCase switch
            {
                Rel.KindOneofCase.HashJoin => rel.HashJoin.Type,
                Rel.KindOneofCase.MergeJoin => rel.MergeJoin.Type,
                Rel.KindOneofCase.NestedLoopJoin => rel.NestedLoopJoin.Type,
                Rel.KindOneofCase.Join => rel.Join.Type,
                _ => null,
            };
            if (type is { } join)
            {
                found.Add(join);
            }
        }

        return found;
    }

    private static void Apply(MaintainedUpdate update, Dictionary<string, object?[]> held)
    {
        if (update.Whole)
        {
            held.Clear();
        }

        foreach (var row in Rows(update.Before))
        {
            held.Remove(KeyOf(row));
        }

        foreach (var row in Rows(update.Now))
        {
            held[KeyOf(row)] = row;
        }
    }

    private static string KeyOf(object?[] row) => row[0]?.ToString() ?? "∅";

    private static IEnumerable<object?[]> Rows(Apache.Arrow.RecordBatch batch)
    {
        for (var i = 0; i < batch.Length; i++)
        {
            yield return [.. Enumerable.Range(0, batch.ColumnCount).Select(c => Chalk.Arrow.RecordBatchExtensions.ValueAt(batch.Column(c), i))];
        }
    }

    private static async Task<Dictionary<string, object?[]>> StatementAsync(
        ChalkEngine engine, EntitledQuery statement, RequestContext? context = null)
    {
        var rows = new Dictionary<string, object?[]>(StringComparer.Ordinal);
        await using var execution = context is null
            ? await engine.ExecuteAsync(statement, ct: Ct)
            : await engine.ExecuteAsync(statement.Query, context, ct: Ct);
        await foreach (var batch in execution.Batches)
        {
            using (batch)
            {
                foreach (var row in Rows(batch))
                {
                    rows.Add(KeyOf(row), row);
                }
            }
        }

        return rows;
    }

    private static void Compare(Dictionary<string, object?[]> held, Dictionary<string, object?[]> expected, int seed, int epoch)
    {
        var where = $"seed {seed}, epoch {epoch}";
        Assert.True(
            expected.Count == held.Count,
            $"{where}: {held.Count} rows held, {expected.Count} expected; extra [{string.Join("; ", held.Keys.Except(expected.Keys))}], missing [{string.Join("; ", expected.Keys.Except(held.Keys))}]");
        foreach (var (key, row) in expected)
        {
            Assert.True(held.TryGetValue(key, out var mine), $"{where}: key {key} missing");
            for (var c = 0; c < row.Length; c++)
            {
                Assert.True(Equals(row[c], mine![c]), $"{where}: key {key}, column {c}: {mine[c] ?? "NULL"} held, {row[c] ?? "NULL"} expected");
            }
        }
    }

    // ------------------------------------------------------------------ views

    private const byte Null = 0;
    private const byte Full = 1;
    private const byte Masked = 2;
    private const byte Withheld = 3;

    /// <summary>What a reader was given of a note's body.</summary>
    private static byte Shown(string? body) => body switch
    {
        null => Null,
        "withheld" => Withheld,
        { Length: 3 } => Masked,
        _ => Full,
    };

    private static ViewOptions<NoteKey, NoteRow> Options(
        ViewMaintenance maintenance, ViewDatabaseReads reads = ViewDatabaseReads.None, RequestContext? context = null) => new()
    {
        Key = ["seq"],
        LocalJoinMaxRows = StandingViews.DefaultLocalJoinMaxRows,
        Maintenance = maintenance,
        DatabaseReads = reads,
        Context = context,
        Map = static (in ViewRow row, out NoteKey key, out NoteRow value) =>
        {
            key = new NoteKey { Seq = row.GetInt64(0) };
            value = new NoteRow { Body = Shown(row.GetString(1)) };
        },
    };

    private static async Task AssertIsTheStatementAsync(
        ChalkEngine engine, EntitledQuery statement, Replica replica, RequestContext? context = null)
    {
        var expected = (await StatementAsync(engine, statement, context)).Values
            .ToDictionary(row => Convert.ToInt64(row[0]), row => Shown((string?)row[1]));
        Assert.Equal(expected, replica.Rows);
    }

    private static IReadOnlyList<NoteGrant> Shares(params NoteGrant[] grants) => grants;

    /// <summary>A sink that keeps a replica of the rows, as a host's would.</summary>
    private sealed class Replica : IViewSink<NoteKey, NoteRow>
    {
        public Dictionary<long, byte> Rows { get; } = [];

        public void Reset() => Rows.Clear();

        public void Upsert(in NoteKey key, in NoteRow row) => Rows[key.Seq] = row.Body;

        public void Delete(in NoteKey key) => Rows.Remove(key.Seq);

        public void Commit(long version, uint checksum)
        {
        }
    }

    /// <summary>An observer that keeps every event, as a host's audit log would.</summary>
    private sealed class Audit : IEntitlementsAudit
    {
        private readonly List<EntitlementsAuditEvent> _events = [];

        public IReadOnlyList<EntitlementsAuditEvent> Events
        {
            get
            {
                lock (_events)
                {
                    return [.. _events];
                }
            }
        }

        public void Executed(EntitlementsAuditEvent audited)
        {
            lock (_events)
            {
                _events.Add(audited);
            }
        }
    }

    // ------------------------------------------------------------------ the policy and its tables

    /// <summary>
    /// Events kept by site, by client and by member of staff within a site, and by the client who
    /// owns each, with the client's identifiers in a realm; marks, through their event, with the name
    /// of whoever made each in a realm of its own. The desk and the doctor read both in full.
    /// </summary>
    private static (PocoSource Source, TenancyEntitlements Policy) Events(IReadOnlyList<Event> events, IReadOnlyList<Mark> marks)
    {
        PocoSource Source(TenancyEntitlements? policy) => new PocoSourceBuilder("mem", "desk")
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .AddTable("events", events, t =>
            {
                t.Keyed(e => e.Seq);
                if (policy?.For("desk", "events") is { } descriptor)
                {
                    t.Entitlement(descriptor);
                }
            })
            .AddTable("marks", marks, t =>
            {
                t.Keyed(m => m.Seq);
                t.ForeignKey(m => m.EventSeq).References<Event>(e => e.Seq, verify: true);
                if (policy?.For("desk", "marks") is { } descriptor)
                {
                    t.Entitlement(descriptor);
                }
            })
            .Build();

        var bare = new CatalogContext { ContextId = ContextId, Epoch = 1, Schemas = [Source(null).DescribeSchema()] };
        var declared = TenancyPolicy.Declare(bare);
        var site = declared.Tenancy("site");
        var client = declared.Subject("client", [site]);
        var staff = declared.Subject("staff", [site]);
        var contact = declared.Realm("contact");
        var people = declared.Realm("people");
        Role[] reading = [declared.Role("desk"), declared.Role("doctor")];
        var store = declared.Source("desk");
        var eventTable = store.Table("events");
        var markTable = store.Table("marks");
        eventTable
            .Tenancy(t => t
                .Direct(site, eventTable.Column("site_id"))
                .Direct(client, eventTable.Column("client_id"))
                .Direct(staff, eventTable.Column("staff_id"))
                .ResourceOwner(eventTable.Column("client_user")))
            .Realm(contact, eventTable.Column("client_id"), eventTable.Column("client_user"))
            .Access(new AccessRule { Roles = [Roles.Owner], Grants = Verdict.Full })
            .Access(new AccessRule { Roles = reading, Grants = Verdict.Full });
        markTable
            .Tenancy(t => t.Through(markTable.Column("event_seq")))
            .Realm(people, markTable.Column("person_name"))
            .Access(new AccessRule { Roles = reading, Realm = people, Grants = Verdict.Full });
        var policy = declared.Compile(bare);
        return (Source(policy), policy);
    }

    private async Task<ChalkEngine> EngineAsync(PocoSource source) =>
        await ChalkEngine.CreateAsync(
            new ChalkEngineOptions { ContextId = ContextId, Sources = [source], Planner = sidecar.CreatePlanner() }, Ct);

    /// <summary>
    /// Notes, kept by team, by patient within a team, by author and by a grant table along a path;
    /// replies, through their note.
    /// Every role reads the realm its own way, and a counter reads scores only in numbers.
    /// </summary>
    private static TenancyEntitlements Compile()
    {
        var bare = new CatalogContext { ContextId = ContextId, Epoch = 1, Schemas = [Desk(null, [], [], []).DescribeSchema()] };
        var policy = TenancyPolicy.Declare(bare);
        var desk = policy.Source("desk");
        var team = policy.Tenancy("team");
        var reader = policy.Tenancy("reader");
        var patient = policy.Subject("patient", [team]);
        var member = policy.Role("member");
        var viewer = policy.Role("viewer");
        var consented = policy.Role("consented");
        var counter = policy.Role("counter");
        var carer = policy.Role("carer");
        var pii = policy.Realm("pii");
        var teams = desk.Table("teams");
        var readers = desk.Table("readers");
        var notes = desk.Table("notes");
        var grants = desk.Table("note_grants");
        var replies = desk.Table("replies");
        var visits = desk.Table("visits");

        teams.Tenancy(t => t.Direct(team, teams.Column("id")));
        readers.Tenancy(t => t.Direct(reader, readers.Column("id")));
        grants.Tenancy(t => t.Direct(reader, grants.Column("reader_id")));
        notes
            .Tenancy(t => t
                .Direct(team, notes.Column("team_id"))
                .Direct(patient, notes.Column("patient_id"))
                .ResourceOwner(notes.Column("author_id"))
                .Related(reader).Through(grants).Through(readers))
            .Realm(pii, notes.Column("body"), notes.Column("phone"))
            .Access(new AccessRule { Roles = [Roles.Owner], Grants = Verdict.Full })
            .Access(new AccessRule { Roles = [consented], Realm = pii, Grants = Verdict.Full })
            .Access(new AccessRule
            {
                Roles = [member], Realm = pii, Grants = Verdict.Mask, Mask = Sql.Of("lambda v: SUBSTRING(v, 1, 3)"),
            })
            .Access(new AccessRule { Roles = [viewer], Realm = pii, Grants = Verdict.None, Placeholder = Sql.Of("'withheld'") })
            .Access(new AccessRule
            {
                Roles = [carer], Realm = pii, Grants = Verdict.Mask, Mask = Sql.Of("lambda v: SUBSTRING(v, 1, 4)"),
            })
            .Access(new AccessRule
            {
                Roles = [counter],
                Column = notes.Column("score"),
                Grants = Verdict.AggregateOnly,
                Aggregates = [Aggregate.Count, Aggregate.Sum],
                MinGroupSize = 3,
            });
        replies
            .Tenancy(t => t.Through(replies.Column("note_seq")))
            .Realm(pii, replies.Column("text"))
            .Access(new AccessRule
            {
                Roles = [member], Realm = pii, Grants = Verdict.Mask, Mask = Sql.Of("lambda v: SUBSTRING(v, 1, 2)"),
            })
            .Access(new AccessRule { Roles = [viewer], Realm = pii, Grants = Verdict.None, Placeholder = Sql.Of("'withheld'") });
        visits
            .Tenancy(t => t.Direct(team, visits.Column("team_id")).Direct(patient, visits.Column("patient_id")))
            .Realm(pii, visits.Column("name"))
            .Access(new AccessRule { Roles = [carer], Realm = pii, Grants = Verdict.Full })
            .Access(new AccessRule { Roles = [member], Realm = pii, Grants = Verdict.None });
        return policy.Compile(bare);
    }

    private static PocoSource Desk(
        TenancyEntitlements? entitlements,
        IReadOnlyList<Note> notes,
        IReadOnlyList<NoteGrant> grants,
        IReadOnlyList<Reply> replies,
        IReadOnlyList<Visit>? visits = null,
        bool indexed = false)
    {
        var builder = new PocoSourceBuilder("mem", "desk").NamingPolicy(PocoNamingPolicy.SnakeCase);
        builder.AddTable("teams", Teams, t =>
        {
            t.Keyed(x => x.Id);
            Entitle(t, entitlements, "teams");
        });
        builder.AddTable("readers", Readers, t =>
        {
            t.Keyed(x => x.Id);
            Entitle(t, entitlements, "readers");
        });
        builder.AddTable("notes", notes, t =>
        {
            t.Keyed(x => x.Seq);
            if (indexed)
            {
                t.UniqueIndex(x => x.Seq);
            }

            t.ForeignKey(x => x.TeamId).References<Team>(x => x.Id, verify: true);
            Entitle(t, entitlements, "notes");
        });
        builder.AddTable("note_grants", grants, t =>
        {
            t.Keyed(x => x.Id);
            t.ForeignKey(x => x.NoteSeq).References<Note>(x => x.Seq, verify: true);
            t.ForeignKey(x => x.ReaderId).References<Reader>(x => x.Id, verify: true);
            Entitle(t, entitlements, "note_grants");
        });
        builder.AddTable("replies", replies, t =>
        {
            t.Keyed(x => x.Seq);
            if (indexed)
            {
                t.Index(x => x.NoteSeq);
            }

            t.ForeignKey(x => x.NoteSeq).References<Note>(x => x.Seq, verify: true);
            Entitle(t, entitlements, "replies");
        });
        builder.AddTable("visits", visits ?? [], t =>
        {
            t.Keyed(x => x.Seq);
            if (indexed)
            {
                t.Index(x => x.PatientId, x => x.TeamId);
            }

            t.ForeignKey(x => x.TeamId).References<Team>(x => x.Id, verify: true);
            Entitle(t, entitlements, "visits");
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

    /// <summary>A private in-memory SQLite with one table of notes, team 1's readable through a context list.</summary>
    private (AdoSource Source, DbConnection KeepAlive) Database()
    {
        var connectionString = $"Data Source=chalk-views-under-a-policy-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        DbConnection Connect() => new SqliteConnection(connectionString);
        var keepAlive = Connect();
        keepAlive.Open();
        _open.Add(keepAlive);
        Execute(keepAlive, "CREATE TABLE notes (seq INTEGER NOT NULL, team_id INTEGER NOT NULL, body TEXT)");
        Execute(keepAlive, "INSERT INTO notes VALUES (1, 1, 'one'), (2, 2, 'two'), (3, 1, 'three')");
        var source = new AdoSourceBuilder("db", Connect, "db")
            .Dialect(DialectProfiles.Sqlite)
            .Capabilities(AdoCapabilities.For(DialectProfiles.Sqlite))
            .DiscoverTables()
            .Entitlement("notes", new TableEntitlementDescriptor { RowPredicate = "team_id IN (@ctx.teams)" })
            .Build();
        return (source, keepAlive);
    }

    private static void Execute(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}

using System.Data.Common;
using Chalk.Catalog;
using Chalk.Client;
using Chalk.Entitlements;
using Chalk.Entitlements.Tenancy;
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
    private static TenancyPrincipal Sharer => new()
    {
        User = 7,
        Grants = [Grant.ForTenancy(Policy.Policy.Tenancy("reader"), 7, Policy.Policy.Role("consented"))],
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
    /// The statements, each with its principal and what its plan must have: the joins it means to
    /// test, or a membership over a list bound at each run.
    /// </summary>
    public static TheoryData<string, string, string, string?> Statements() => new()
    {
        {
            "a direct restriction, an owner, a path, masks and placeholders",
            "SELECT seq, team_id, title, body, phone FROM desk.notes",
            nameof(Seven),
            "Left"
        },
        {
            "a list too long to fold, split into a semi and an anti join",
            "SELECT seq, team_id, patient_id, name FROM desk.visits",
            nameof(Carer),
            "Semi,Anti"
        },
        {
            "a child through its parent",
            "SELECT seq, note_seq, text FROM desk.replies",
            nameof(Seven),
            null
        },
        {
            "a join of two tables under the policy",
            "SELECT r.seq, n.title, n.body, r.text FROM desk.replies r JOIN desk.notes n ON n.seq = r.note_seq",
            nameof(Seven),
            null
        },
        {
            "lists too long to fold, read as memberships",
            "SELECT seq, team_id, patient_id, title, body, phone FROM desk.notes",
            nameof(Eight),
            "@ctx"
        },
        {
            "a group-size floor",
            "SELECT team_id, COUNT(score) AS n, SUM(score) AS total FROM desk.notes GROUP BY team_id",
            nameof(Counter),
            null
        },
    };

    /// <summary>
    /// Each statement under the policy, kept by maintenance over 30 random epochs under two seeds, is
    /// what the statement returns after every one; each plan is checked to have what it means to test.
    /// </summary>
    [Theory]
    [MemberData(nameof(Statements))]
    public async Task Every_epoch_a_statement_under_a_policy_is_kept(string name, string sql, string principal, string? planned)
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

        foreach (var seed in new[] { 11, 23 })
        {
            var epochs = await RunAsync(sql, who, seed, planned);
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
    /// Every update that runs is audited, recomputed or maintained (D361): one event each, before it
    /// runs, with the purpose and actor of the context the statement was prepared with, the version the
    /// update brings the view to, and the digest of the plan it ran. An update with nothing new to read
    /// runs nothing and raises nothing.
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
    /// A statement prepared for a context's shape binds its values at execution, which a view does
    /// not: maintenance refuses it when the view is made, naming the context.
    /// </summary>
    [Fact]
    public async Task A_maintained_view_of_a_statement_prepared_for_a_shape_is_refused()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var source = Desk(Policy, [new(1, 1, 101, 5, "one", "body-1", "555-0101", 1)], [], []);
        await using var engine = await EngineAsync(source);
        var entitled = engine.WithEntitlements(new EntitlementsOptions());
        var shaped = await entitled.PrepareAsync("SELECT seq, body FROM desk.notes", Policy.Bind(Seven()).Shape(), ct: Ct);

        var refused = await Assert.ThrowsAsync<StandingViewException>(async () =>
            await engine.MaterializeAsync(shaped.Query, Options(ViewMaintenance.Incremental), Ct));
        Assert.Equal(ChalkErrorCodes.UnsupportedInView, refused.Code);
        Assert.Contains("reads a request's context", refused.Message, StringComparison.Ordinal);
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
    private async Task<List<Dictionary<string, object?[]>>> RunAsync(string sql, TenancyPrincipal who, int seed, string? planned)
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

        var source = Desk(Policy, [.. notes.Values], [.. grants.Values], [.. replies.Values], [.. visits.Values]);
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

    private static async Task<Dictionary<string, object?[]>> StatementAsync(ChalkEngine engine, EntitledQuery statement)
    {
        var rows = new Dictionary<string, object?[]>(StringComparer.Ordinal);
        await using var execution = await engine.ExecuteAsync(statement, ct: Ct);
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
        ViewMaintenance maintenance, ViewDatabaseReads reads = ViewDatabaseReads.None) => new()
    {
        Key = ["seq"],
        LocalJoinMaxRows = StandingViews.DefaultLocalJoinMaxRows,
        Maintenance = maintenance,
        DatabaseReads = reads,
        Map = static (in ViewRow row, out NoteKey key, out NoteRow value) =>
        {
            key = new NoteKey { Seq = row.GetInt64(0) };
            value = new NoteRow { Body = Shown(row.GetString(1)) };
        },
    };

    private static async Task AssertIsTheStatementAsync(ChalkEngine engine, EntitledQuery statement, Replica replica)
    {
        var expected = (await StatementAsync(engine, statement)).Values
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
        IReadOnlyList<Visit>? visits = null)
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
            t.ForeignKey(x => x.NoteSeq).References<Note>(x => x.Seq, verify: true);
            Entitle(t, entitlements, "replies");
        });
        builder.AddTable("visits", visits ?? [], t =>
        {
            t.Keyed(x => x.Seq);
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

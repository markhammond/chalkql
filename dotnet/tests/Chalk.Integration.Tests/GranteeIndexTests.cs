using Chalk.Catalog;
using Chalk.Client;
using Chalk.Entitlements;
using Chalk.Entitlements.Tenancy;
using Chalk.Sources.Poco;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// A path through a grant table whose grantee column carries an index (F231). With the index, the
/// planner reads the reader's grants through it, and the path's join then stood over an index lookup
/// of a scan that reads some of the table's columns: the lookup named its columns' origins in the
/// table's own numbering on the scan's projected view, the taint check mapped them a second time,
/// missed the join, and refused every statement over the governed table as a bug.
/// </summary>
[Collection(SidecarCollection.Name)]
public sealed class GranteeIndexTests(SharedSidecar sidecar)
{
    private const string ContextId = "grantee-index";

    private sealed record Reader(int Id);

    private sealed record Note(long Seq, int TeamId, string Body);

    private sealed record NoteGrant(int Id, long NoteSeq, int ReaderId);

    private sealed record Team(int Id);

    /// <summary>
    /// Notes shared with reader 7 along the path, the newest 300 of them: the same rows with an index
    /// on the grantee column and without, at three rows and at five thousand, and with an index on the
    /// notes' key too.
    /// </summary>
    [Theory]
    [InlineData(false, false, 3)]
    [InlineData(true, false, 3)]
    [InlineData(false, false, 5000)]
    [InlineData(true, false, 5000)]
    [InlineData(true, true, 5000)]
    public async Task A_path_over_an_indexed_grant_table_reads_the_rows_it_shares(
        bool granteeIndex, bool keyIndex, int count)
    {
        var (source, policy, shared) = Desk(granteeIndex, keyIndex, count);
        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = ContextId,
            Sources = [source],
            Planner = sidecar.CreatePlanner(),
        });
        var reader = new TenancyPrincipal
        {
            User = 7,
            Grants = [Grant.ForTenancy(policy.Policy.Tenancy("reader"), 7, policy.Policy.Role("consented"))],
        };

        var prepared = await engine.WithEntitlements().PrepareAsync(
            "SELECT seq, body FROM desk.notes ORDER BY seq DESC LIMIT 300", policy.Bind(reader));
        var rows = new List<string>();
        await using var execution = await engine.ExecuteAsync(prepared, ct: TestContext.Current.CancellationToken);
        await foreach (var batch in execution.Batches)
        {
            using (batch)
            {
                rows.AddRange(BatchReader.ToRows(batch).Select(r => string.Join("|", r)));
            }
        }

        Assert.Equal(
            [.. shared.OrderByDescending(seq => seq).Take(300).Select(seq => $"{seq}|note-{seq}")],
            rows);
    }

    /// <summary>Notes of one team, each shared with readers by grant rows; and which notes reader 7 holds a grant to.</summary>
    private static (PocoSource Source, TenancyEntitlements Policy, IReadOnlySet<long> Shared) Desk(
        bool granteeIndex, bool keyIndex, int count)
    {
        IReadOnlyList<Note> noteRows = [.. Enumerable.Range(1, count).Select(i => new Note(i, 1, $"note-{i}"))];
        IReadOnlyList<Reader> readerRows = [.. Enumerable.Range(1, 400).Select(i => new Reader(i))];
        IReadOnlyList<NoteGrant> grantRows = [.. Enumerable.Range(1, count * 4).Select(i => new NoteGrant(i, 1 + (i % count), 1 + (i % 400)))];
        PocoSource Source(TenancyEntitlements? policy)
        {
            void Entitle<T>(PocoTableBuilder<T> t, string name)
            {
                if (policy?.For("desk", name) is { } descriptor)
                {
                    t.Entitlement(descriptor);
                }
            }

            return new PocoSourceBuilder("mem", "desk")
                .NamingPolicy(PocoNamingPolicy.SnakeCase)
                .AddTable("teams", (IReadOnlyList<Team>)[new(1)], t =>
                {
                    t.Keyed(x => x.Id);
                    Entitle(t, "teams");
                })
                .AddTable("readers", readerRows, t =>
                {
                    t.Keyed(x => x.Id);
                    Entitle(t, "readers");
                })
                .AddTable("notes", noteRows, t =>
                {
                    t.Keyed(x => x.Seq);
                    if (keyIndex)
                    {
                        t.UniqueIndex(x => x.Seq);
                    }

                    t.ForeignKey(x => x.TeamId).References<Team>(x => x.Id, verify: true);
                    Entitle(t, "notes");
                })
                .AddTable("note_grants", grantRows, t =>
                {
                    t.Keyed(x => x.Id);
                    if (granteeIndex)
                    {
                        t.Index(x => x.ReaderId);
                    }

                    t.ForeignKey(x => x.NoteSeq).References<Note>(x => x.Seq, verify: true);
                    t.ForeignKey(x => x.ReaderId).References<Reader>(x => x.Id, verify: true);
                    Entitle(t, "note_grants");
                })
                .Build();
        }

        var bare = new CatalogContext { ContextId = ContextId, Epoch = 1, Schemas = [Source(null).DescribeSchema()] };
        var declared = TenancyPolicy.Declare(bare);
        var desk = declared.Source("desk");
        var team = declared.Tenancy("team");
        var readerKind = declared.Tenancy("reader");
        var consented = declared.Role("consented");
        var teams = desk.Table("teams");
        var readers = desk.Table("readers");
        var notes = desk.Table("notes");
        var grants = desk.Table("note_grants");
        teams.Tenancy(t => t.Direct(team, teams.Column("id")));
        readers.Tenancy(t => t.Direct(readerKind, readers.Column("id")));
        grants.Tenancy(t => t.Direct(readerKind, grants.Column("reader_id")));
        notes
            .Tenancy(t => t.Direct(team, notes.Column("team_id")).Related(readerKind).Through(grants).Through(readers))
            .Access(new AccessRule { Roles = [consented], Grants = Verdict.Full });
        var policy = declared.Compile(bare);
        return (Source(policy), policy, grantRows.Where(g => g.ReaderId == 7).Select(g => g.NoteSeq).ToHashSet());
    }
}

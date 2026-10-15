using Chalk.Catalog;
using Chalk.Client;
using Chalk.Entitlements;
using Chalk.Entitlements.Tenancy;
using Chalk.Sources.Poco;
using Chalk.TestKit;
using CatalogContext = Chalk.Catalog.CatalogContext;

namespace Chalk.Integration.Tests;

/// <summary>
/// Grants kept in a table rather than in the principal: a share is read by the readers a grant
/// table names, along a <c>Related</c> path, and a document is entitled <c>Through</c> its share. A
/// principal then holds one grant, its own reader id, and sharing and withdrawing are refreshes of
/// the grant table, with no new plan.
/// </summary>
/// <remarks>
/// <para>
/// Three readers, 7, 8 and 9. Share 1 is granted to reader 7 and share 2 to reader 8; share 3 to
/// nobody. Document 1, in share 1, was written by reader 8; document 2, in share 2, by reader 7;
/// document 3, in share 3, by reader 9. Its author reads a document in full; a reader it is shared
/// with reads that it exists, and not its body.
/// </para>
/// <para>
/// A parent whose rows only a path restricts used to be refused as "through an unrestricted
/// parent": the checks counted a row predicate and a <c>Through</c>, and not a path.
/// </para>
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class GrantTableTests(SharedSidecar sidecar)
{
    private sealed record Reader(int Id);

    private sealed record Share(int ShareKey);

    private sealed record ShareGrant(int Id, int ShareKey, int ReaderId);

    private sealed record Document(long Seq, int ShareKey, int AuthorId, string Body);

    private static readonly Reader[] Readers = [new(7), new(8), new(9)];

    private static readonly Share[] Shares = [new(1), new(2), new(3)];

    private static readonly ShareGrant[] Granted = [new(1, 1, 7), new(2, 2, 8)];

    private static readonly ShareGrant[] SharedAgain = [new(1, 1, 7), new(2, 2, 8), new(3, 3, 7)];

    private static readonly ShareGrant[] Withdrawn = [new(2, 2, 8), new(3, 3, 7)];

    private static readonly Document[] Documents =
        [new(1, 1, 8, "first"), new(2, 2, 7, "second"), new(3, 3, 9, "third")];

    private const string Statement = "SELECT seq, body FROM library.documents";

    /// <summary>
    /// Each reader reads what it wrote, in full, and what is shared with it, without its body: the
    /// disclosure is decided row by row.
    /// </summary>
    [Fact]
    public async Task A_document_is_read_by_its_author_and_by_the_readers_its_share_is_granted_to()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var entitlements = Compile();
        await using var engine = await EngineAsync(entitlements);

        Assert.Equal(
            ["1||REDACTED", "2|second|FULL"],
            await RowsAsync(engine, await PrepareAsync(engine, entitlements, 7)));
        Assert.Equal(
            ["1|first|FULL", "2||REDACTED"],
            await RowsAsync(engine, await PrepareAsync(engine, entitlements, 8)));
        Assert.Equal(["3|third|FULL"], await RowsAsync(engine, await PrepareAsync(engine, entitlements, 9)));
    }

    /// <summary>
    /// Sharing and withdrawing are refreshes of the grant table. The statement prepared before them
    /// reads the change at its next run, and preparing it again gives the same plan.
    /// </summary>
    [Fact]
    public async Task Sharing_and_withdrawing_are_refreshes_of_the_grant_table()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        var entitlements = Compile();
        await using var engine = await EngineAsync(entitlements, out var grants);
        var prepared = await PrepareAsync(engine, entitlements, 7);

        await engine.RefreshAsync(r => r.Replace(grants, SharedAgain));
        Assert.Equal(["1||REDACTED", "2|second|FULL", "3||REDACTED"], await RowsAsync(engine, prepared));

        await engine.RefreshAsync(r => r.Replace(grants, Withdrawn));
        Assert.Equal(["2|second|FULL", "3||REDACTED"], await RowsAsync(engine, prepared));

        Assert.Equal(prepared.PlanDigest, (await PrepareAsync(engine, entitlements, 7)).PlanDigest);
    }

    // ---------------------------------------------------------------- the harness

    /// <summary>
    /// The policy: a share is read by the readers its grants name, a document through its share or
    /// by its author, and a document's body only by its author.
    /// </summary>
    private static TenancyEntitlements Compile()
    {
        var bare = Catalog(null);
        var p = TenancyPolicy.Declare(bare);
        var library = p.Source("library");
        var reader = p.Tenancy("reader");
        p.Role("granted");
        var content = p.Realm("content");
        var readers = library.Table("readers");
        var shares = library.Table("shares");
        var grants = library.Table("share_grants");
        var documents = library.Table("documents");

        readers.Tenancy(t => t.Direct(reader, readers.Column("id")));
        grants.Tenancy(t => t.Direct(reader, grants.Column("reader_id")));
        shares.Tenancy(t => t.Related(reader).Through(grants).Through(readers));
        documents
            .Tenancy(t => t.ResourceOwner(documents.Column("author_id")).Through(documents.Column("share_key")))
            .Realm(content, documents.Column("body"))
            .Access(new AccessRule { Roles = [Roles.Owner], Grants = Verdict.Full });
        return p.Compile(bare);
    }

    private ValueTask<ChalkEngine> EngineAsync(TenancyEntitlements entitlements) =>
        ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "grant-table",
            Sources = [Source(entitlements, out _)],
            Planner = sidecar.CreatePlanner(),
        });

    private ValueTask<ChalkEngine> EngineAsync(TenancyEntitlements entitlements, out PocoTable<ShareGrant> grants) =>
        ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "grant-table",
            Sources = [Source(entitlements, out grants)],
            Planner = sidecar.CreatePlanner(),
        });

    /// <summary>The statement, for a reader holding one grant: its own id, in the role the grant table gives.</summary>
    private static async Task<EntitledQuery> PrepareAsync(ChalkEngine engine, TenancyEntitlements entitlements, int id)
    {
        var policy = entitlements.Policy;
        var principal = new TenancyPrincipal
        {
            User = id,
            Grants = [Grant.ForTenancy(policy.Tenancy("reader"), id, policy.Role("granted"))],
        };
        return await engine
            .WithEntitlements(new EntitlementsOptions { IncludeDisclosureColumns = true })
            .PrepareAsync(Statement, entitlements.Bind(principal));
    }

    /// <summary>Each row as seq, body and the body's disclosure, in order.</summary>
    private static async Task<string[]> RowsAsync(ChalkEngine engine, EntitledQuery prepared)
    {
        var rows = new List<string>();
        await using var execution = await engine.ExecuteAsync(prepared);
        await foreach (var batch in execution.Batches)
        {
            using (batch)
            {
                var seq = batch.Schema.GetFieldIndex("seq");
                var body = batch.Schema.GetFieldIndex("body");
                var disclosure = batch.Schema.GetFieldIndex("body__disclosure");
                rows.AddRange(BatchReader.ToRows(batch).Select(r => $"{r[seq]}|{r[body]}|{r[disclosure]}"));
            }
        }

        rows.Sort(StringComparer.Ordinal);
        return [.. rows];
    }

    private static PocoSource Source(TenancyEntitlements? entitlements, out PocoTable<ShareGrant> grants)
    {
        var builder = new PocoSourceBuilder("mem", "library").NamingPolicy(PocoNamingPolicy.SnakeCase);
        builder.AddTable("readers", Readers, t =>
        {
            t.OrderedBy(r => r.Id).UniqueKey(r => r.Id);
            Entitle(t, entitlements, "readers");
        });
        builder.AddTable("shares", Shares, t =>
        {
            t.OrderedBy(s => s.ShareKey).UniqueKey(s => s.ShareKey);
            Entitle(t, entitlements, "shares");
        });
        builder.AddTable("share_grants", Granted, out grants, t =>
        {
            t.OrderedBy(g => g.Id).UniqueKey(g => g.Id)
                .ForeignKey(g => g.ShareKey).References<Share>(s => s.ShareKey, verify: true);
            t.ForeignKey(g => g.ReaderId).References<Reader>(r => r.Id, verify: true);
            Entitle(t, entitlements, "share_grants");
        });
        builder.AddTable("documents", Documents, t =>
        {
            t.OrderedBy(d => d.Seq).UniqueKey(d => d.Seq)
                .ForeignKey(d => d.ShareKey).References<Share>(s => s.ShareKey, verify: true);
            Entitle(t, entitlements, "documents");
        });
        return builder.Build();
    }

    private static void Entitle<T>(PocoTableBuilder<T> table, TenancyEntitlements? entitlements, string name)
    {
        if (entitlements?.For("library", name) is { } descriptor)
        {
            table.Entitlement(descriptor);
        }
    }

    private static CatalogContext Catalog(TenancyEntitlements? entitlements) => new()
    {
        ContextId = "grant-table",
        Epoch = 1,
        Schemas = [Source(entitlements, out _).DescribeSchema()],
    };
}

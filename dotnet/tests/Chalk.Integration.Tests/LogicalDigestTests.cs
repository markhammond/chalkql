using Chalk.Client;
using Chalk.Sources.Poco;
using Chalk.TestKit;

namespace Chalk.Integration.Tests;

/// <summary>
/// A statement's logical digest is its identity in a catalog (D342): the same on every engine and every
/// planner that holds the catalog, whatever the tables' sizes and whichever plan each chose. It covers
/// what the statement reads, every column of every table it reads, and nothing else; and it does not
/// name the catalog itself, so a key that spans catalogs adds one.
/// </summary>
/// <remarks>
/// One engine plans with the assembly's shared sidecar and the other with a sidecar of this class's
/// own, so the digests come from two planner processes.
/// </remarks>
[Collection(SidecarCollection.Name)]
public sealed class LogicalDigestTests(SharedSidecar sidecar) : IAsyncLifetime
{
    private sealed record Fill(int Seq, int Book, long Qty);

    private sealed record Book(int Id, int Desk);

    private sealed record NamedBook(int Id, int Desk, string Name);

    private sealed record Note(int Id, string Text);

    private const string ByDesk =
        "SELECT b.desk, SUM(f.qty) AS qty FROM fills AS f JOIN books AS b ON b.id = f.book GROUP BY b.desk";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private SidecarFixture? _other;

    public async ValueTask InitializeAsync() => _other = await SidecarFixture.StartAsync();

    public async ValueTask DisposeAsync()
    {
        if (_other is not null)
        {
            await _other.DisposeAsync();
        }
    }

    /// <summary>
    /// Two engines, each with its own planner, one over empty tables and one over 5,000 fills: the
    /// sizes give them different plans, and the statement one logical digest. Spaced otherwise, with
    /// its keywords in another case, it is the same statement.
    /// </summary>
    [Fact]
    public async Task One_statement_has_one_digest_on_two_engines_at_different_sizes()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        Assert.SkipWhen(!_other!.IsAvailable, _other.SkipReason ?? string.Empty);
        await using var empty = await EngineAsync(sidecar.CreatePlanner(), [], Books(0));
        await using var loaded = await EngineAsync(
            _other.CreatePlanner(),
            [.. Enumerable.Range(1, 5_000).Select(i => new Fill(i, i % 50, (i % 7) + 1))],
            Books(50));

        var small = await empty.PrepareAsync(ByDesk, ct: Ct);
        var large = await loaded.PrepareAsync(ByDesk, ct: Ct);
        var respaced = await loaded.PrepareAsync(
            "select b.desk, sum(f.qty) as qty\n  from fills as f\n  join books as b on b.id = f.book\n group by b.desk", ct: Ct);

        Assert.NotEqual(small.PlanDigest, large.PlanDigest);
        Assert.NotEqual(0UL, small.LogicalDigest);
        Assert.Equal(small.LogicalDigest, large.LogicalDigest);
        Assert.Equal(large.LogicalDigest, respaced.LogicalDigest);
    }

    /// <summary>
    /// A column added to a table the statement reads moves its digest, though the statement does not
    /// read the column; a table it does not read leaves the digest as it was.
    /// </summary>
    [Fact]
    public async Task A_digest_moves_with_the_tables_it_reads_and_no_others()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var plain = await EngineAsync(sidecar.CreatePlanner(), [], Books(0));
        await using var named = await EngineAsync(sidecar.CreatePlanner(), [], [], withNames: true);
        await using var noted = await EngineAsync(sidecar.CreatePlanner(), [], Books(0), withNotes: true);

        var digest = (await plain.PrepareAsync(ByDesk, ct: Ct)).LogicalDigest;

        Assert.NotEqual(digest, (await named.PrepareAsync(ByDesk, ct: Ct)).LogicalDigest);
        Assert.Equal(digest, (await noted.PrepareAsync(ByDesk, ct: Ct)).LogicalDigest);
    }

    /// <summary>
    /// Two catalogs whose tables share names and columns give the statement the same digest, however
    /// their engines are named and whatever rows they hold: the digest does not name the catalog.
    /// </summary>
    [Fact]
    public async Task A_digest_does_not_name_the_catalog()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);
        await using var first = await EngineAsync(sidecar.CreatePlanner(), [new Fill(1, 0, 3)], Books(1), contextId: "desk-a");
        await using var second = await EngineAsync(sidecar.CreatePlanner(), [new Fill(1, 0, 9)], Books(1), contextId: "desk-b");

        Assert.Equal(
            (await first.PrepareAsync(ByDesk, ct: Ct)).LogicalDigest,
            (await second.PrepareAsync(ByDesk, ct: Ct)).LogicalDigest);
    }

    private static IReadOnlyList<Book> Books(int count) =>
        [.. Enumerable.Range(0, count).Select(i => new Book(i, i % 3))];

    private static async Task<ChalkEngine> EngineAsync(
        IQueryPlanner planner,
        IReadOnlyList<Fill> fills,
        IReadOnlyList<Book> books,
        bool withNames = false,
        bool withNotes = false,
        string contextId = "desk")
    {
        var source = new PocoSourceBuilder("trading").NamingPolicy(PocoNamingPolicy.SnakeCase).AddTable("fills", fills);
        source = withNames
            ? source.AddTable("books", (IReadOnlyList<NamedBook>)[.. books.Select(b => new NamedBook(b.Id, b.Desk, "book"))])
            : source.AddTable("books", books);
        if (withNotes)
        {
            source = source.AddTable("notes", (IReadOnlyList<Note>)[new Note(1, "unread")]);
        }

        return await ChalkEngine.CreateAsync(
            new ChalkEngineOptions { ContextId = contextId, Sources = [source.Build()], Planner = planner },
            Ct);
    }
}

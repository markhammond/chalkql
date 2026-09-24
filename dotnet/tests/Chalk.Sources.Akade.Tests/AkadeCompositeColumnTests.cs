using Akade.IndexedSet;
using Chalk.Arrow;
using Chalk.Client;
using Chalk.Ir;
using Chalk.Sources.Poco;

namespace Chalk.Sources.Akade.Tests;

/// <summary>
/// A composite column from an Akade set (D302): a member whose type is a record is a COMPOSITE of the
/// record's properties, exactly as on any in-process table. Discovery describes no index over it — an
/// Akade index keyed by the record is a physical detail Chalk cannot key on, as a computed key is — so
/// the column is read through the full scan like any unindexed member, whole and by field, while the
/// set's other indexes still serve their lookups.
/// </summary>
public sealed class AkadeCompositeColumnTests
{
    /// <summary>A member's contact card: a record struct, so the column is never NULL.</summary>
    public readonly record struct Card(string Email, int Tier);

    /// <summary>A member, with the card it had before, which some members have not.</summary>
    public sealed record Member(int Id, DateOnly Joined, Card Contact, Card? Previous);

    private const int Rows = 40;

    private static Member[] Members() =>
    [
        .. Enumerable.Range(1, Rows).Select(i => new Member(
            i,
            new DateOnly(2026, 1, 1).AddDays(i),
            new Card($"m{i}@example.org", i % 3),
            i % 4 == 0 ? null : new Card($"old{i}@example.org", i % 2))),
    ];

    /// <summary>
    /// The set, keyed by id, with a range index on the join date and a hash index Akade keys by the
    /// card itself — which Akade can answer and Chalk cannot describe.
    /// </summary>
    private static IndexedSet<int, Member> Set(IEnumerable<Member> rows) =>
        rows.ToIndexedSet(x => x.Id)
            .WithRangeIndex(x => x.Joined)
            .WithIndex(x => x.Contact)
            .Build();

    private static IndexedSetSource<Member> Source() =>
        AkadeSource
            .From("members", Set(Members()))
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .Build();

    [Fact]
    public void Discovery_describes_no_index_over_a_composite_member()
    {
        var table = Source().DescribeSchema().Tables[0];

        Assert.Equal(
            ["id:I32", "joined:Date", "contact:Composite", "previous:Composite"],
            table.Columns.Select(c => $"{c.Name}:{c.Type.Kind}"));
        Assert.False(table.Columns[2].Type.Nullable);
        Assert.True(table.Columns[3].Type.Nullable);
        Assert.Equal(["Email", "Tier"], table.Columns[2].Type.Fields.Select(f => f.Name));

        // The id and the join date are access paths; the card's own index is not, and nothing else
        // names the composite column.
        Assert.NotEmpty(table.Indexes);
        Assert.All(table.Indexes, index => Assert.DoesNotContain(2, index.Columns));
        Assert.Contains(table.Indexes, index => index.Columns.SequenceEqual([0]));
        Assert.Contains(table.Indexes, index => index.Columns.SequenceEqual([1]));
        Assert.All(table.Columns.Skip(2), column => Assert.Same(Chalk.Catalog.ColumnStatistics.Unknown, column.Statistics));
    }

    [Fact]
    public async Task A_composite_column_reads_through_the_full_scan_whole_and_by_field()
    {
        await using var sidecar = await PlannerProcess.StartAsync();
        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "akade-composite-columns",
            Sources = [Source()],
            Planner = sidecar.CreatePlanner(),
        });

        var (whole, wholeScanned) = await RowsAsync(engine, "SELECT id, contact, previous FROM members ORDER BY id");
        Assert.Equal(Rows, whole.Count);
        Assert.Equal("4|[m4@example.org, 1]|NULL", whole[3]);
        Assert.Equal("5|[m5@example.org, 2]|[old5@example.org, 1]", whole[4]);
        Assert.Equal(Rows, wholeScanned);

        // A field filters over the full scan: no index is over the card, whatever Akade holds.
        var (tiered, tieredScanned) = await RowsAsync(
            engine, "SELECT m.id, m.contact.email AS email FROM members m WHERE m.contact.tier = 2 ORDER BY m.id");
        Assert.Equal(
            Members().Where(m => m.Contact.Tier == 2).Select(m => $"{m.Id}|{m.Contact.Email}"),
            tiered);
        Assert.Equal(Rows, tieredScanned);

        // The id is still a lookup, and the composite column comes with the row it finds.
        var (found, foundScanned) = await RowsAsync(engine, "SELECT id, contact FROM members WHERE id = 7");
        Assert.Equal(["7|[m7@example.org, 1]"], found);
        Assert.True(foundScanned < Rows, $"the id lookup read {foundScanned} rows of {Rows}");
    }

    private static async Task<(List<string> Rows, long Scanned)> RowsAsync(ChalkEngine engine, string sql)
    {
        var prepared = await engine.PrepareAsync(sql);
        await using var execution = await engine.ExecuteAsync(prepared, (IReadOnlyList<object?>?)null);
        var rows = new List<string>();
        await foreach (var batch in execution.Batches)
        {
            using (batch)
            {
                rows.AddRange(batch.ToRows().Select(row => string.Join("|", row.Select(Render))));
            }
        }

        return (rows, (long)execution.Stats.RowsScanned);
    }

    private static string Render(object? value) => value switch
    {
        null => "NULL",
        object?[] fields => "[" + string.Join(", ", fields.Select(Render)) + "]",
        _ => value.ToString() ?? "?",
    };
}

namespace Chalk.Sources.Poco.Tests;

/// <summary>
/// A table describes as the descriptor it was when nothing moved, and as a new row count over the
/// parts it had when only its rows did (F205): what lets the engine publish what a refresh changed
/// rather than the whole catalog.
/// </summary>
public sealed class PocoDescribeTests
{
    private sealed record Tick(int Seq, string Symbol);

    private static Tick[] Ticks(int from, int count) =>
        [.. Enumerable.Range(from, count).Select(i => new Tick(i, i % 2 == 0 ? "A" : "B"))];

    [Fact]
    public void A_source_nothing_moved_in_describes_as_the_schema_it_was()
    {
        var source = Source(out _);

        Assert.Same(source.DescribeSchema(), source.DescribeSchema());
    }

    [Fact]
    public async Task An_append_moves_the_row_count_over_the_parts_the_table_had()
    {
        var source = Source(out var ticks);
        var before = source.DescribeSchema();

        await AppendAsync(ticks, Ticks(101, 5));
        var after = source.DescribeSchema();

        Assert.NotSame(before, after);
        var (was, now) = (before.FindTable("ticks")!, after.FindTable("ticks")!);
        Assert.Equal(105, now.RowCount);
        Assert.Same(was.Columns, now.Columns);
        Assert.Same(was.Indexes, now.Indexes);
        Assert.Same(was.UniqueKeys, now.UniqueKeys);
        Assert.Same(before.FindTable("venues"), after.FindTable("venues"));
        Assert.Same(before.Functions, after.Functions);
    }

    /// <summary>Statistics computed again are new columns: the engine reads that table again.</summary>
    [Fact]
    public async Task Statistics_computed_again_describe_as_new_columns()
    {
        var source = Source(out var ticks);
        var before = source.DescribeSchema().FindTable("ticks")!;

        await AppendAsync(ticks, Ticks(101, 20));
        var after = source.DescribeSchema().FindTable("ticks")!;

        Assert.NotSame(before.Columns, after.Columns);
        Assert.Same(before.Indexes, after.Indexes);
        Assert.Equal(120, Convert.ToInt32(after.Columns.Single(c => c.Name == "Seq").Statistics.Max));
    }

    private static PocoSource Source(out PocoTable<Tick> ticks) =>
        new PocoSourceBuilder("mem")
            .AddTable("ticks", Ticks(1, 100), out ticks, t => t.Index(x => x.Seq))
            .AddTable("venues", (IReadOnlyList<Tick>)[new Tick(1, "X")])
            .Build();

    private static async Task AppendAsync(PocoTable<Tick> table, IReadOnlyList<Tick> rows)
    {
        var source = (IRefreshableSource)table.Runtime;
        var commit = await source.PrepareRefreshAsync(
            [
                new SourceRefreshEntry
                {
                    Table = table.Name,
                    Kind = SourceRefreshKind.Append,
                    RowType = typeof(Tick),
                    Rows = rows,
                },
            ],
            TestContext.Current.CancellationToken);
        commit.Commit();
    }
}

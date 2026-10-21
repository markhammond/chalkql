using Chalk.Catalog;

namespace Chalk.Sources.Poco.Tests;

/// <summary>
/// An append to a table with a declared unique key checks the rows it brings, not every row the table
/// holds, where what the table declares besides vouches for the rest (F235): a unique index on the
/// key's columns, which checks its own rows as it is extended, or a collation that puts equal keys
/// side by side. A key with neither is checked over every row, as before.
/// </summary>
public sealed class PocoUniqueAppendTests
{
    private sealed record Row(int Seq, string Code);

    /// <summary>
    /// A key its collation keeps in order: a row appended with the last row's key is refused, naming
    /// the row and the key, and the table keeps the rows it had; a row with a new key is taken.
    /// </summary>
    [Fact]
    public async Task An_appended_row_that_repeats_a_key_its_order_keeps_is_refused()
    {
        var source = Rows(t => t.OrderedBy(x => x.Seq).UniqueKey(x => x.Seq));

        var refused = await Assert.ThrowsAsync<CatalogVerificationException>(
            () => AppendAsync(source, new Row(4, "d"), new Row(4, "e")));
        Assert.Contains("unique key (Seq)", refused.Message, StringComparison.Ordinal);
        Assert.Contains("key (4) already occurred", refused.Message, StringComparison.Ordinal);

        var refusedAtTheJoin = await Assert.ThrowsAsync<CatalogVerificationException>(
            () => AppendAsync(source, new Row(3, "again")));
        Assert.Contains("already occurred", refusedAtTheJoin.Message, StringComparison.Ordinal);

        await AppendAsync(source, new Row(4, "d"), new Row(5, "e"));
        Assert.Equal(["a", "b", "c", "d", "e"], await CodesAsync(source));
    }

    /// <summary>A key a unique index covers is refused by the index, as the index is extended.</summary>
    [Fact]
    public async Task An_appended_row_that_repeats_a_key_its_unique_index_holds_is_refused()
    {
        var source = Rows(t => t.UniqueKey(x => x.Code).UniqueIndex(x => x.Code));

        var refused = await Assert.ThrowsAsync<CatalogVerificationException>(
            () => AppendAsync(source, new Row(9, "a")));
        Assert.Contains("unique index", refused.Message, StringComparison.Ordinal);
        Assert.Equal(["a", "b", "c"], await CodesAsync(source));
    }

    /// <summary>
    /// A unique index the table's order backs holds no positions, and an append extends it by reading
    /// only the appended rows and the last row before them: a repeat among them, or of the last row,
    /// is refused by the index, and the table keeps the rows it had.
    /// </summary>
    [Fact]
    public async Task An_appended_row_that_repeats_a_key_its_ordered_unique_index_holds_is_refused()
    {
        var source = Rows(t => t.OrderedBy(x => x.Seq).UniqueIndex(x => x.Seq));

        var among = await Assert.ThrowsAsync<CatalogVerificationException>(
            () => AppendAsync(source, new Row(4, "d"), new Row(4, "e")));
        Assert.Contains("unique index", among.Message, StringComparison.Ordinal);

        var atTheJoin = await Assert.ThrowsAsync<CatalogVerificationException>(
            () => AppendAsync(source, new Row(3, "again")));
        Assert.Contains("unique index", atTheJoin.Message, StringComparison.Ordinal);

        await AppendAsync(source, new Row(4, "d"));
        await AppendAsync(source, new Row(5, "e"));
        await Assert.ThrowsAsync<CatalogVerificationException>(() => AppendAsync(source, new Row(5, "again")));
        Assert.Equal(["a", "b", "c", "d", "e"], await CodesAsync(source));
    }

    /// <summary>A key with neither is still checked over every row: a repeat of the first row's key is found.</summary>
    [Fact]
    public async Task An_appended_row_that_repeats_any_earlier_key_is_still_refused()
    {
        var source = Rows(t => t.UniqueKey(x => x.Code));

        var refused = await Assert.ThrowsAsync<CatalogVerificationException>(
            () => AppendAsync(source, new Row(9, "a")));
        Assert.Contains("key ('a') already occurred", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A replacement is checked whole, through the same paths: a key its collation keeps is compared
    /// row by row with the one before it, without a set of every key.
    /// </summary>
    [Fact]
    public async Task A_replacement_that_repeats_a_key_its_order_keeps_is_refused()
    {
        var source = Rows(t => t.OrderedBy(x => x.Seq).UniqueKey(x => x.Seq));

        var refused = await Assert.ThrowsAsync<CatalogVerificationException>(
            () => CommitAsync(source, SourceRefreshKind.Replace, [new Row(1, "a"), new Row(2, "b"), new Row(2, "c")]));
        Assert.Contains("key (2) already occurred", refused.Message, StringComparison.Ordinal);
    }

    // ---- the scaffolding ----

    private static PocoSource Rows(Action<PocoTableBuilder<Row>> configure) =>
        new PocoSourceBuilder("mem")
            .AddTable("rows", (IReadOnlyList<Row>)[new Row(1, "a"), new Row(2, "b"), new Row(3, "c")], configure)
            .Build();

    private static Task AppendAsync(PocoSource source, params Row[] rows) =>
        CommitAsync(source, SourceRefreshKind.Append, rows);

    private static async Task CommitAsync(PocoSource source, SourceRefreshKind kind, IReadOnlyList<Row> rows)
    {
        var commit = await source.PrepareRefreshAsync(
            [new SourceRefreshEntry { Table = "rows", Kind = kind, RowType = typeof(Row), Rows = rows }],
            TestContext.Current.CancellationToken);
        commit.Commit();
    }

    private static async Task<string[]> CodesAsync(PocoSource source)
    {
        var column = PocoTestSupport.Describe(source, "rows").Columns.ToList().FindIndex(c => c.Name == "Code");
        var values = await PocoTestSupport.ColumnAsync(source, "rows", column, batchSize: 64);
        return [.. values.Select(v => (string)v!)];
    }
}

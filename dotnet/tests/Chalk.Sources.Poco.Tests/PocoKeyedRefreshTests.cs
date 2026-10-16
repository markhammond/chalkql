using Chalk.Catalog;

namespace Chalk.Sources.Poco.Tests;

/// <summary>
/// A keyed table's refresh of upserts and deletes, built from the table's last snapshot (D367): its
/// rows found by key, copied in bulk, its indexes extended where the change kept them valid, only what
/// it touched checked, and what it changed recorded for the views that read it.
/// </summary>
public sealed class PocoKeyedRefreshTests
{
    private sealed record Row(int Id, int Group, int Rank, string Name);

    /// <summary>
    /// Over 300 random refreshes of upserts, new keys and deletes, the table holds exactly the rows a
    /// refresh that rebuilt it would: each upserted key's row in the place of the one it replaced, each
    /// deleted key's row gone, and the rows of new keys after the rest, in the order given. An index on
    /// another column answers as a filter of those rows does, after every refresh.
    /// </summary>
    [Theory]
    [InlineData(3)]
    [InlineData(17)]
    public async Task A_keyed_refresh_keeps_the_rows_a_rebuild_would(int seed)
    {
        var random = new Random(seed);
        var model = Enumerable.Range(1, 200).Select(i => new Row(i, i % 7, i, $"row-{i}")).ToList();
        var source = Rows(model, t => t.Index(x => x.Group));
        var next = 201;
        for (var round = 0; round < 300; round++)
        {
            var upserts = new List<Row>();
            var deletes = new List<Row>();
            var touched = new HashSet<int>();
            for (var i = random.Next(4); i > 0 && model.Count > 0; i--)
            {
                var old = model[random.Next(model.Count)];
                if (touched.Add(old.Id))
                {
                    upserts.Add(old with { Group = random.Next(7), Name = $"row-{old.Id}-{round}" });
                }
            }

            for (var i = random.Next(3); i > 0; i--)
            {
                upserts.Add(new Row(next, random.Next(7), next, $"row-{next}"));
                touched.Add(next++);
            }

            for (var i = random.Next(3); i > 0 && model.Count > 0; i--)
            {
                var gone = model[random.Next(model.Count)];
                if (touched.Add(gone.Id))
                {
                    deletes.Add(gone);
                }
            }

            await CommitAsync(source, Entry(SourceRefreshKind.Upsert, upserts), Entry(SourceRefreshKind.Delete, deletes));
            model = Rebuilt(model, upserts, deletes);

            var (rows, release) = Pinned(source);
            try
            {
                Assert.Equal(model, rows.Rows!.Take(rows.RowCount));
                var group = (IPositionalPocoIndex<Row>)rows.Indexes[0];
                for (var g = 0; g < 7; g++)
                {
                    group.GetWindow(IndexKeyRange.Equality(g), out var from, out var to);
                    var found = Enumerable.Range(from, to - from).Select(o => rows.Rows![group.GetPosition(o)].Id).Order();
                    Assert.Equal(model.Where(r => r.Group == g).Select(r => r.Id).Order(), found);
                }
            }
            finally
            {
                release();
            }
        }
    }

    /// <summary>
    /// A refresh records what it changed against the snapshot it was made from: the rows it took out,
    /// where the rows it put in their places now stand, and where the rows it appended begin. Applied to
    /// the earlier rows, that record gives the later ones. Between snapshots a refresh apart, the source
    /// finds the change as it did before, by walking them.
    /// </summary>
    [Fact]
    public async Task A_keyed_refresh_records_what_it_changed()
    {
        var source = Rows([.. Enumerable.Range(1, 10).Select(i => new Row(i, 0, i, $"row-{i}"))]);
        var tracked = (ITrackedSource)source;
        var (first, releaseFirst) = Pinned(source);

        await CommitAsync(
            source,
            Entry(SourceRefreshKind.Upsert, [new Row(2, 0, 2, "two"), new Row(7, 0, 7, "seven"), new Row(11, 0, 11, "eleven")]),
            Entry(SourceRefreshKind.Delete, [new Row(4, 0, 4, "")]));
        var (second, releaseSecond) = Pinned(source);

        Assert.Equal(-1, tracked.AppendedFrom("rows", first, second));
        var diff = tracked.DiffFrom("rows", first, second)!;
        Assert.Equal([1, 3, 6], diff.Removed);
        Assert.Equal([1, 5], diff.Added);
        Assert.Equal(9, diff.AppendedFrom);
        Assert.Equal(
            ["row-1", "two", "row-3", "row-5", "row-6", "seven", "row-8", "row-9", "row-10", "eleven"],
            second.Rows!.Take(second.RowCount).Select(r => r.Name));

        await CommitAsync(source, Entry(SourceRefreshKind.Upsert, [new Row(1, 0, 1, "one")]));
        var (third, releaseThird) = Pinned(source);
        Assert.NotNull(tracked.DiffFrom("rows", second, third));
        var skipped = tracked.DiffFrom("rows", first, third);
        Assert.True(skipped is null || skipped.Added.Length == 0, "a change a refresh apart is not taken from the record");

        releaseFirst();
        releaseSecond();
        releaseThird();
    }

    /// <summary>A refresh that only upserts keys the table does not hold is an append: every earlier row stays where it was.</summary>
    [Fact]
    public async Task An_upsert_of_new_keys_is_an_append()
    {
        var source = Rows([new Row(1, 0, 1, "one"), new Row(2, 0, 2, "two")]);
        var tracked = (ITrackedSource)source;
        var (before, releaseBefore) = Pinned(source);

        await CommitAsync(source, Entry(SourceRefreshKind.Upsert, [new Row(3, 0, 3, "three"), new Row(4, 0, 4, "four")]));
        var (after, releaseAfter) = Pinned(source);

        Assert.Equal(2, tracked.AppendedFrom("rows", before, after));
        Assert.Equal(["one", "two", "three", "four"], after.Rows!.Take(after.RowCount).Select(r => r.Name));
        releaseBefore();
        releaseAfter();
    }

    /// <summary>
    /// What the key does not settle is still checked on what changed: a replaced row out of the declared
    /// order, and a row that repeats another unique key, are refused, and the table is left as it was,
    /// its next refresh finding its rows by key as before.
    /// </summary>
    [Fact]
    public async Task What_a_keyed_refresh_touches_is_checked_against_the_declarations()
    {
        var source = Rows(
            [.. Enumerable.Range(1, 5).Select(i => new Row(i, 0, i * 10, $"row-{i}"))],
            t => t.OrderedBy(x => x.Rank).UniqueKey(x => x.Name));

        var order = await Assert.ThrowsAsync<CatalogVerificationException>(
            () => CommitAsync(source, Entry(SourceRefreshKind.Upsert, [new Row(2, 0, 45, "row-2")])));
        Assert.Contains("goes backwards", order.Message, StringComparison.Ordinal);

        var unique = await Assert.ThrowsAsync<CatalogVerificationException>(
            () => CommitAsync(source, Entry(SourceRefreshKind.Upsert, [new Row(3, 0, 30, "row-4")])));
        Assert.Contains("already occurred", unique.Message, StringComparison.Ordinal);

        await CommitAsync(source, Entry(SourceRefreshKind.Upsert, [new Row(3, 0, 31, "three")]));
        var (rows, release) = Pinned(source);
        Assert.Equal(["row-1", "row-2", "three", "row-4", "row-5"], rows.Rows!.Take(rows.RowCount).Select(r => r.Name));
        release();
    }

    /// <summary>A replacement of the whole table, then a keyed refresh: the keys are found in the rows the replacement put there.</summary>
    [Fact]
    public async Task A_keyed_refresh_after_a_replacement_finds_the_replacements_rows()
    {
        var source = Rows([new Row(1, 0, 1, "one"), new Row(2, 0, 2, "two")]);
        await CommitAsync(source, Entry(SourceRefreshKind.Upsert, [new Row(2, 0, 2, "TWO")]));
        await CommitAsync(source, Entry(SourceRefreshKind.Replace, [new Row(5, 0, 5, "five"), new Row(2, 0, 2, "deux")]));

        await CommitAsync(
            source,
            Entry(SourceRefreshKind.Upsert, [new Row(2, 0, 2, "zwei")]),
            Entry(SourceRefreshKind.Delete, [new Row(5, 0, 5, "")]));

        var (rows, release) = Pinned(source);
        Assert.Equal(["zwei"], rows.Rows!.Take(rows.RowCount).Select(r => r.Name));
        release();
    }

    // ---- the scaffolding ----

    /// <summary>The rows a refresh that rebuilt the table would give, as a keyed refresh is specified to.</summary>
    private static List<Row> Rebuilt(List<Row> rows, List<Row> upserts, List<Row> deletes)
    {
        var replacing = upserts.ToDictionary(r => r.Id);
        var gone = deletes.Select(r => r.Id).ToHashSet();
        var next = new List<Row>();
        foreach (var row in rows)
        {
            if (gone.Contains(row.Id))
            {
                continue;
            }

            next.Add(replacing.Remove(row.Id, out var replacement) ? replacement : row);
        }

        next.AddRange(upserts.Where(r => replacing.ContainsKey(r.Id)));
        return next;
    }

    private static PocoSource Rows(IReadOnlyList<Row> rows, Action<PocoTableBuilder<Row>>? configure = null) =>
        new PocoSourceBuilder("mem")
            .AddTable("rows", rows, t =>
            {
                t.Keyed(x => x.Id);
                configure?.Invoke(t);
            })
            .Build();

    private static (PocoTableSnapshot<Row> Snapshot, Action Release) Pinned(PocoSource source)
    {
        var pinnable = (IPinnableSource)source;
        var pinned = pinnable.Pin("rows")!;
        return ((PocoTableSnapshot<Row>)pinned, () => pinnable.Unpin("rows", pinned));
    }

    private static SourceRefreshEntry Entry(SourceRefreshKind kind, IReadOnlyList<Row> rows) => new()
    {
        Table = "rows",
        Kind = kind,
        RowType = typeof(Row),
        Rows = rows,
    };

    private static async Task CommitAsync(PocoSource source, params SourceRefreshEntry[] entries)
    {
        var commit = await source.PrepareRefreshAsync(
            [.. entries.Where(e => e.Kind is SourceRefreshKind.Replace || ((IReadOnlyList<Row>)e.Rows).Count > 0)],
            TestContext.Current.CancellationToken);
        commit.Commit();
    }
}

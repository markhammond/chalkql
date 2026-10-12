using Chalk.Catalog;

namespace Chalk.Sources.Poco.Tests;

/// <summary>
/// How a table says it changes (D339, <c>docs/design/64-incremental-views.md</c> §2): a log appended
/// to in time order, with a lateness behind its frontier; a log read by key and time; and a table
/// whose rows change by key. Each refusal changes nothing and says what to do.
/// </summary>
public sealed class PocoChangeKindTests
{
    private sealed record Tick(int Seq, string Symbol, DateTime Ts);

    private sealed record Untimed(int Seq, DateTime? Ts);

    private sealed record Position(string Book, string Asset, decimal Qty);

    private static readonly DateTime Open = new(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);

    private static Tick At(int seq, int second, string symbol = "ACME") =>
        new(seq, symbol, Open.AddSeconds(second));

    // ---- append-only ----

    /// <summary>
    /// With zero lateness a refresh's rows may come in any order among themselves: the table sorts
    /// them by time before it appends, so its declared time order holds.
    /// </summary>
    [Fact]
    public async Task An_append_only_table_sorts_the_rows_a_refresh_appends()
    {
        var source = Ticks([At(1, 0), At(2, 10)], t => t.AppendOnly(x => x.Ts));

        await AppendAsync(source, [At(5, 40), At(3, 20), At(4, 20)]);

        await AssertSeqsAsync(source, 1, 2, 3, 4, 5);
    }

    /// <summary>
    /// A row older than the frontier refuses the whole refresh, as a declaration the data does not
    /// hold, naming the row, its time and the frontier; nothing in it is applied.
    /// </summary>
    [Fact]
    public async Task A_row_older_than_the_frontier_refuses_the_refresh()
    {
        var source = Ticks([At(1, 0), At(2, 10)], t => t.AppendOnly(x => x.Ts));

        var refused = await Assert.ThrowsAsync<CatalogVerificationException>(
            () => AppendAsync(source, [At(3, 15), At(4, 5)]));

        Assert.Equal(ChalkErrorCodes.DeclarationNotHeld, refused.Code);
        Assert.Equal(1, refused.RowIndex);
        Assert.Contains("row 1 of the rows appended has Ts 2026-10-12T09:00:05", refused.Message, StringComparison.Ordinal);
        Assert.Contains("older than the table's frontier, 2026-10-12T09:00:10", refused.Message, StringComparison.Ordinal);
        Assert.Contains("Declare a lateness on the table", refused.Message, StringComparison.Ordinal);
        await AssertSeqsAsync(source, 1, 2);

        // The frontier did not move either: a row at the old frontier is still in time.
        await AppendAsync(source, [At(5, 10)]);
        await AssertSeqsAsync(source, 1, 2, 5);
    }

    /// <summary>A row at the frontier is not late: lateness is "older than", not "not newer than".</summary>
    [Fact]
    public async Task A_row_at_the_frontier_is_in_time()
    {
        var source = Ticks([At(1, 10)], t => t.AppendOnly(x => x.Ts));

        await AppendAsync(source, [At(2, 10)]);

        await AssertSeqsAsync(source, 1, 2);
    }

    /// <summary>
    /// With a window, a row late by no more than it is taken and lands in time order through the
    /// time index; a row later than the window is refused.
    /// </summary>
    [Fact]
    public async Task A_window_takes_a_row_within_it_and_refuses_one_beyond_it()
    {
        var source = Ticks([At(1, 0), At(2, 30)], t => t.AppendOnly(x => x.Ts, TimeSpan.FromSeconds(10)));

        await AppendAsync(source, [At(3, 25)]);
        await AssertSeqsAsync(source, 1, 2, 3);
        Assert.NotNull(source.FindIndex<Tick>("ticks", "ix_ticks_Ts"));

        var refused = await Assert.ThrowsAsync<CatalogVerificationException>(
            () => AppendAsync(source, [At(4, 19)]));
        Assert.Contains("less its lateness of 00:00:10", refused.Message, StringComparison.Ordinal);
        await AssertSeqsAsync(source, 1, 2, 3);
    }

    /// <summary>A row of a table with a time needs one: a NULL time is refused, naming the row.</summary>
    [Fact]
    public async Task A_row_without_a_time_is_refused()
    {
        var source = new PocoSourceBuilder("mem")
            .AddTable("untimed", (IReadOnlyList<Untimed>)[new(1, Open)], t => t.AppendOnly(x => x.Ts))
            .Build();

        var refused = await Assert.ThrowsAsync<CatalogVerificationException>(
            () => RefreshAsync(source, "untimed", SourceRefreshKind.Append, (IReadOnlyList<Untimed>)[new(2, null)]));

        Assert.Contains("row 0 of the rows appended has no Ts", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An advance moves the frontier without a row, so a row older than it is then late; it may
    /// stand beside the same table's rows, and it may not move the frontier back.
    /// </summary>
    [Fact]
    public async Task An_advance_moves_the_frontier_without_a_row()
    {
        var source = Ticks([At(1, 0)], t => t.AppendOnly(x => x.Ts));

        await CommitAsync(source, Advance("ticks", Open.AddSeconds(30)));
        await Assert.ThrowsAsync<CatalogVerificationException>(() => AppendAsync(source, [At(2, 20)]));

        await CommitAsync(
            source,
            Entry("ticks", SourceRefreshKind.Append, (IReadOnlyList<Tick>)[At(2, 35)]),
            Advance("ticks", Open.AddSeconds(60)));
        await Assert.ThrowsAsync<CatalogVerificationException>(() => AppendAsync(source, [At(3, 50)]));

        var back = await Assert.ThrowsAsync<SourceContractException>(
            () => CommitAsync(source, Advance("ticks", Open.AddSeconds(45))));
        Assert.Equal(ChalkErrorCodes.SourceRefresh, back.Code);
        Assert.Contains("would move the table's frontier back", back.Message, StringComparison.Ordinal);
        await AssertSeqsAsync(source, 1, 2);
    }

    /// <summary>An advance names a time of the type the table declares, on a table that has one.</summary>
    [Fact]
    public async Task An_advance_is_refused_without_a_time_or_with_the_wrong_one()
    {
        var timed = Ticks([At(1, 0)], t => t.AppendOnly(x => x.Ts));
        var wrong = Assert.Throws<SourceContractException>(
            () => timed.ValidateRefresh([Advance("ticks", new DateTimeOffset(Open))]));
        Assert.Contains("the table's time, Ts, is a DateTime", wrong.Message, StringComparison.Ordinal);

        var untimed = Ticks([At(1, 0)]);
        var none = Assert.Throws<SourceContractException>(
            () => untimed.ValidateRefresh([Advance("ticks", Open)]));
        Assert.Contains("only a table that declares a time has one", none.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A replacement rewrites a log, which is how a host trims it, and the frontier follows the rows
    /// it is given — back, if the host says so.
    /// </summary>
    [Fact]
    public async Task A_replacement_rewrites_the_log_and_its_frontier()
    {
        var source = Ticks([At(1, 0), At(2, 50)], t => t.AppendOnly(x => x.Ts));

        await CommitAsync(source, Entry("ticks", SourceRefreshKind.Replace, (IReadOnlyList<Tick>)[At(3, 20)]));
        await AppendAsync(source, [At(4, 30)]);

        await AssertSeqsAsync(source, 3, 4);
    }

    // ---- as-of ----

    /// <summary>
    /// An as-of table keeps its time order and is indexed on its keys then its time, which is the
    /// index an ASOF join seeks.
    /// </summary>
    [Fact]
    public void An_as_of_table_is_indexed_on_its_keys_then_its_time()
    {
        var source = Ticks([At(1, 0), At(2, 10, "BRIX")], t => t.AsOf(x => x.Ts, x => x.Symbol));

        var table = PocoTestSupport.Describe(source, "ticks");
        var index = Assert.Single(table.Indexes, i => i.Name == "ix_ticks_Symbol_Ts");
        Assert.Equal(
            [table.Columns.ToList().FindIndex(c => c.Name == "Symbol"), table.Columns.ToList().FindIndex(c => c.Name == "Ts")],
            index.Columns);
        Assert.Contains(table.Collations, c => c.Keys[0].Column == table.Columns.ToList().FindIndex(x => x.Name == "Ts"));
    }

    // ---- keyed ----

    /// <summary>
    /// Upserts replace a key's row where it stands and add new keys after the rest; deletes remove
    /// a key's row, and a key the table does not hold removes nothing.
    /// </summary>
    [Fact]
    public async Task A_keyed_table_takes_upserts_and_deletes_by_key()
    {
        var source = Positions([new("b1", "ACME", 1), new("b1", "BRIX", 2), new("b2", "ACME", 3)]);

        await CommitAsync(
            source,
            Entry("positions", SourceRefreshKind.Upsert, (IReadOnlyList<Position>)[new("b1", "BRIX", 20), new("b3", "CIRRUS", 4)]),
            Entry("positions", SourceRefreshKind.Delete, (IReadOnlyList<Position>)[new("b2", "ACME", 0), new("b9", "NONE", 0)]));

        await AssertQtysAsync(source, 1m, 20m, 4m);
    }

    /// <summary>A key upserted twice in one refresh is refused, naming both rows.</summary>
    [Fact]
    public async Task A_key_upserted_twice_in_one_refresh_is_refused()
    {
        var source = Positions([new("b1", "ACME", 1)]);

        var refused = await Assert.ThrowsAsync<CatalogVerificationException>(
            () => CommitAsync(
                source,
                Entry("positions", SourceRefreshKind.Upsert, (IReadOnlyList<Position>)[new("b2", "ACME", 1), new("b2", "ACME", 2)])));

        Assert.Equal(ChalkErrorCodes.DeclarationNotHeld, refused.Code);
        Assert.Contains("rows 0 and 1 of the upserts share the key (b2, ACME)", refused.Message, StringComparison.Ordinal);
        await AssertQtysAsync(source, 1m);
    }

    /// <summary>A key both upserted and deleted in one refresh is refused: it says twice what the key becomes.</summary>
    [Fact]
    public async Task A_key_upserted_and_deleted_in_one_refresh_is_refused()
    {
        var source = Positions([new("b1", "ACME", 1)]);

        var refused = await Assert.ThrowsAsync<SourceContractException>(
            () => CommitAsync(
                source,
                Entry("positions", SourceRefreshKind.Upsert, (IReadOnlyList<Position>)[new("b1", "ACME", 5)]),
                Entry("positions", SourceRefreshKind.Delete, (IReadOnlyList<Position>)[new("b1", "ACME", 0)])));

        Assert.Equal(ChalkErrorCodes.SourceRefresh, refused.Code);
        Assert.Contains("both upserted and deleted", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Each kind takes the changes it declares and refuses the others before anything is built: an
    /// append to a keyed table, an upsert to a log, an upsert to a table that declares no key.
    /// </summary>
    [Fact]
    public void Each_kind_refuses_the_changes_it_does_not_declare()
    {
        var keyed = Positions([new("b1", "ACME", 1)]);
        var append = Assert.Throws<SourceContractException>(
            () => keyed.ValidateRefresh([Entry("positions", SourceRefreshKind.Append, (IReadOnlyList<Position>)[new("b2", "ACME", 1)])]));
        Assert.Contains("Upsert the new rows rather than appending them", append.Message, StringComparison.Ordinal);

        var log = Ticks([At(1, 0)], t => t.AppendOnly(x => x.Ts));
        var upsert = Assert.Throws<SourceContractException>(
            () => log.ValidateRefresh([Entry("ticks", SourceRefreshKind.Upsert, (IReadOnlyList<Tick>)[At(2, 5)])]));
        Assert.Contains("its rows are only appended", upsert.Message, StringComparison.Ordinal);

        var plain = Ticks([At(1, 0)]);
        var noKey = Assert.Throws<SourceContractException>(
            () => plain.ValidateRefresh([Entry("ticks", SourceRefreshKind.Delete, (IReadOnlyList<Tick>)[At(1, 0)])]));
        Assert.Contains("declares no key to change them by. Declare Keyed(…)", noKey.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// One refresh says once what a table becomes: upserts and deletes together, or rows and an
    /// advance, but not rows given whole and changed by key.
    /// </summary>
    [Fact]
    public void A_table_takes_one_change_and_an_advance_in_a_refresh()
    {
        var keyed = Positions([new("b1", "ACME", 1)]);
        var twice = Assert.Throws<SourceContractException>(
            () => keyed.ValidateRefresh(
            [
                Entry("positions", SourceRefreshKind.Replace, (IReadOnlyList<Position>)[]),
                Entry("positions", SourceRefreshKind.Upsert, (IReadOnlyList<Position>)[]),
            ]));
        Assert.Contains("names this table twice", twice.Message, StringComparison.Ordinal);

        keyed.ValidateRefresh(
        [
            Entry("positions", SourceRefreshKind.Upsert, (IReadOnlyList<Position>)[]),
            Entry("positions", SourceRefreshKind.Delete, (IReadOnlyList<Position>)[]),
        ]);
    }

    // ---- declarations ----

    /// <summary>A table declares one change kind, a time that is a time, and a lateness that is a window.</summary>
    [Fact]
    public void A_declaration_says_one_change_kind_with_a_time_and_a_lateness()
    {
        var twice = Assert.Throws<InvalidOperationException>(
            () => new PocoSourceBuilder("mem").AddTable(
                "ticks", (IReadOnlyList<Tick>)[], t => t.AppendOnly(x => x.Ts).Keyed(x => x.Seq)));
        Assert.Contains("already declares AppendOnly(Ts)", twice.Message, StringComparison.Ordinal);

        var notATime = Assert.Throws<ArgumentException>(
            () => new PocoSourceBuilder("mem").AddTable(
                "ticks", (IReadOnlyList<Tick>)[], t => t.AppendOnly(x => x.Seq)));
        Assert.Contains("a time is a DateTime or a DateTimeOffset", notATime.Message, StringComparison.Ordinal);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new PocoSourceBuilder("mem").AddTable(
                "ticks", (IReadOnlyList<Tick>)[], t => t.AppendOnly(x => x.Ts, TimeSpan.FromSeconds(-1))));

        var keyless = Assert.Throws<ArgumentException>(
            () => new PocoSourceBuilder("mem").AddTable(
                "ticks", (IReadOnlyList<Tick>)[], t => t.AsOf(x => x.Ts)));
        Assert.Contains("A log read by time alone is AppendOnly", keyless.Message, StringComparison.Ordinal);
    }

    /// <summary>A keyed table's key is a unique key, which the registered rows must hold.</summary>
    [Fact]
    public void A_keyed_table_declares_its_key_unique()
    {
        var source = Positions([new("b1", "ACME", 1)]);
        Assert.Single(PocoTestSupport.Describe(source, "positions").UniqueKeys);

        Assert.Throws<CatalogVerificationException>(
            () => Positions([new("b1", "ACME", 1), new("b1", "ACME", 2)]));
    }

    /// <summary>
    /// A table whose rows change by position or by key needs positions: one registered as a
    /// collection that only enumerates is refused at Build.
    /// </summary>
    [Fact]
    public void A_change_kind_needs_a_list()
    {
        IReadOnlyCollection<Tick> enumerated = new HashSet<Tick> { At(1, 0) };
        var refused = Assert.Throws<CatalogValidationException>(
            () => new PocoSourceBuilder("mem").AddTable("ticks", enumerated, t => t.AppendOnly(x => x.Ts)).Build());
        Assert.Contains("only enumerates", refused.Message, StringComparison.Ordinal);
    }

    // ---- the scaffolding ----

    private static PocoSource Ticks(IReadOnlyList<Tick> rows, Action<PocoTableBuilder<Tick>>? configure = null) =>
        new PocoSourceBuilder("mem").AddTable("ticks", rows, configure).Build();

    private static PocoSource Positions(IReadOnlyList<Position> rows) =>
        new PocoSourceBuilder("mem")
            .AddTable("positions", rows, t => t.Keyed(x => x.Book, x => x.Asset))
            .Build();

    private static SourceRefreshEntry Entry<T>(string table, SourceRefreshKind kind, IReadOnlyList<T> rows) => new()
    {
        Table = table,
        Kind = kind,
        RowType = typeof(T),
        Rows = rows,
    };

    private static SourceRefreshEntry Advance(string table, object time) => new()
    {
        Table = table,
        Kind = SourceRefreshKind.Advance,
        RowType = typeof(object),
        Rows = Array.Empty<object>(),
        Frontier = time,
    };

    private static Task AppendAsync(PocoSource source, IReadOnlyList<Tick> rows) =>
        CommitAsync(source, Entry("ticks", SourceRefreshKind.Append, rows));

    private static Task RefreshAsync<T>(PocoSource source, string table, SourceRefreshKind kind, IReadOnlyList<T> rows) =>
        CommitAsync(source, Entry(table, kind, rows));

    private static async Task CommitAsync(PocoSource source, params SourceRefreshEntry[] entries)
    {
        var commit = await source.PrepareRefreshAsync(entries, TestContext.Current.CancellationToken);
        commit.Commit();
    }

    private static async Task AssertSeqsAsync(PocoSource source, params int[] expected) =>
        Assert.Equal(expected, await SeqsAsync(source));

    private static async Task AssertQtysAsync(PocoSource source, params decimal[] expected) =>
        Assert.Equal(expected, await QtysAsync(source));

    private static async Task<int[]> SeqsAsync(PocoSource source)
    {
        var column = PocoTestSupport.Describe(source, "ticks").Columns.ToList().FindIndex(c => c.Name == "Seq");
        var values = await PocoTestSupport.ColumnAsync(source, "ticks", column, batchSize: 64);
        return [.. values.Select(v => (int)v!)];
    }

    private static async Task<decimal[]> QtysAsync(PocoSource source)
    {
        var column = PocoTestSupport.Describe(source, "positions").Columns.ToList().FindIndex(c => c.Name == "Qty");
        var values = await PocoTestSupport.ColumnAsync(source, "positions", column, batchSize: 64);
        return [.. values.Select(v => Convert.ToDecimal(v, System.Globalization.CultureInfo.InvariantCulture))];
    }
}

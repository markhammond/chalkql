using Apache.Arrow;
using Apache.Arrow.Types;
using Chalk.Catalog;
using Chalk.Tests;

namespace Chalk.Sources.Poco.Tests;

/// <summary>
/// Composite columns from the POCO source (D302): a member whose type is a record reads as a
/// COMPOSITE of its properties, exactly as a function's record result does (D294); its values reach
/// both scan paths as a struct of child columns, the composite NULL where the member is; and every
/// declaration that would key, index, order or describe one is refused at build, naming it.
/// </summary>
public sealed class PocoCompositeColumnTests
{
    /// <summary>A contact, a record class: the composite may be NULL.</summary>
    public sealed record Contact(Utf8String Email, string? Phone, int Tier);

    /// <summary>A position, a record struct: never NULL as a member, NULL only as a <c>Point?</c>.</summary>
    public readonly record struct Point(double X, double Y);

    public sealed record Account(long Id, Contact? Contact, Point Where, Point? Previous);

    private static readonly ChalkType ContactType = ChalkType.Composite(
        [
            new CompositeField("Email", ChalkType.String()),
            new CompositeField("Phone", ChalkType.String(nullable: true)),
            new CompositeField("Tier", ChalkType.Int32()),
        ],
        nullable: true);

    private static Account[] Accounts(int count) =>
    [
        .. Enumerable.Range(0, count).Select(i => new Account(
            i,
            i % 3 == 2 ? null : new Contact(System.Text.Encoding.UTF8.GetBytes($"a{i}@x"), i % 2 == 0 ? null : $"+{i}", i % 4),
            new Point(i, -i),
            i % 5 == 0 ? null : new Point(i * 0.5, i * 0.25))),
    ];

    [Fact]
    public void A_record_member_is_a_composite_column_of_its_properties()
    {
        var source = new PocoSourceBuilder("mem").AddTable("accounts", Accounts(3)).Build();
        var columns = PocoTestSupport.Describe(source, "accounts").Columns;

        Assert.Equal(["Id", "Contact", "Where", "Previous"], columns.Select(c => c.Name));
        Assert.Equal(ContactType, columns[1].Type);
        Assert.Equal(
            ChalkType.Composite([new CompositeField("X", ChalkType.Float64()), new CompositeField("Y", ChalkType.Float64())]),
            columns[2].Type);
        Assert.True(columns[3].Type.Nullable);
        Assert.Equal(columns[2].Type.Fields, columns[3].Type.Fields);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(4096)]
    public async Task The_arrow_scan_writes_a_struct_whose_children_are_the_fields(int batchSize)
    {
        var rows = Accounts(50);
        var source = new PocoSourceBuilder("mem").AddTable("accounts", rows).Build();
        var batches = await PocoTestSupport.ScanAsync(source, "accounts", batchSize);
        try
        {
            var row = 0;
            foreach (var batch in batches)
            {
                var contact = Assert.IsType<StructArray>(batch.Column(1));
                Assert.Equal(["Email", "Phone", "Tier"], ((StructType)contact.Data.DataType).Fields.Select(f => f.Name));
                var where = Assert.IsType<StructArray>(batch.Column(2));
                var previous = Assert.IsType<StructArray>(batch.Column(3));
                Assert.Equal(0, where.NullCount);
                for (var i = 0; i < batch.Length; i++, row++)
                {
                    var expected = rows[row];
                    Assert.Equal(expected.Contact is null, contact.IsNull(i));
                    if (expected.Contact is { } c)
                    {
                        Assert.Equal(c.Email.ToString(), ((StringArray)contact.Fields[0]).GetString(i));
                        Assert.Equal(c.Phone, ((StringArray)contact.Fields[1]).GetString(i));
                        Assert.Equal(c.Tier, ((Int32Array)contact.Fields[2]).GetValue(i));
                    }

                    Assert.Equal(expected.Where.X, ((DoubleArray)where.Fields[0]).GetValue(i));
                    Assert.Equal(expected.Where.Y, ((DoubleArray)where.Fields[1]).GetValue(i));
                    Assert.Equal(expected.Previous is null, previous.IsNull(i));
                    if (expected.Previous is { } p)
                    {
                        Assert.Equal(p.X, ((DoubleArray)previous.Fields[0]).GetValue(i));
                    }
                }

                // A NULL composite's non-nullable fields hold a value; only the nullable one is NULL.
                Assert.Equal(0, contact.Fields[0].NullCount);
                Assert.Equal(0, contact.Fields[2].NullCount);
                Assert.Equal(0, previous.Fields[0].NullCount);
            }

            Assert.Equal(rows.Length, row);
        }
        finally
        {
            PocoTestSupport.Dispose(batches);
        }
    }

    [Fact]
    public async Task The_columnar_scan_writes_a_view_whose_children_are_the_fields()
    {
        var rows = Accounts(50);
        var source = new PocoSourceBuilder("mem").AddTable("accounts", rows).Build();
        var request = PocoTestSupport.Request(source, "accounts", 16);
        var scan = ((IColumnarBatchSource)source).ColumnarScan(request, PocoTestSupport.Context())
            ?? throw new InvalidOperationException("the source declined to serve a columnar scan");
        var types = PocoTestSupport.Describe(source, "accounts").Columns.Select(c => c.Type).ToArray();

        var row = 0;
        await using (scan.ConfigureAwait(false))
        {
            var batch = new ColumnarBatch(types);
            while (scan.TryNext(batch))
            {
                var contact = batch.Column(1);
                var tier = contact.FieldView(2).Lanes<int>();
                var x = batch.Column(2).FieldView(0).Lanes<double>();
                for (var i = 0; i < batch.Count; i++, row++)
                {
                    Assert.Equal(rows[row].Contact is not null, contact.IsValid(i));
                    if (rows[row].Contact is { } c)
                    {
                        Assert.Equal(c.Tier, tier[i]);
                        Assert.Equal(c.Email.ToString(), contact.FieldView(0).Utf8(i).ToString());
                    }

                    Assert.Equal(rows[row].Where.X, x[i]);
                }
            }
        }

        Assert.Equal(rows.Length, row);
    }

    /// <summary>
    /// Refresh and append are unchanged by a composite column: a replaced table reads its new
    /// records, and an appended one the old and the new, every composite and every NULL where a table
    /// built from the whole list has them — across a batch boundary, and a validity bitmap joined at a
    /// bit that is not a byte's.
    /// </summary>
    [Fact]
    public async Task A_replace_and_an_append_read_composite_columns_as_a_rebuilt_table_does()
    {
        var source = new PocoSourceBuilder("mem").AddTable("accounts", Accounts(30)).Build();

        Account[] replaced = [.. Accounts(45).Skip(15)];
        await RefreshAsync(source, SourceRefreshKind.Replace, replaced);
        Assert.Equal(await RenderAsync(Built(replaced)), await RenderAsync(source));

        Account[] appended = [.. Accounts(61).Skip(45)];
        await RefreshAsync(source, SourceRefreshKind.Append, appended);
        Assert.Equal(await RenderAsync(Built([.. replaced, .. appended])), await RenderAsync(source));
        Assert.Equal(46, PocoTestSupport.Describe(source, "accounts").RowCount);
    }

    private static PocoSource Built(IReadOnlyList<Account> rows) =>
        new PocoSourceBuilder("mem").AddTable("accounts", rows).Build();

    private static async Task RefreshAsync(PocoSource source, SourceRefreshKind kind, IReadOnlyList<Account> rows)
    {
        var commit = await source.PrepareRefreshAsync(
            [new SourceRefreshEntry { Table = "accounts", Kind = kind, RowType = typeof(Account), Rows = rows }],
            TestContext.Current.CancellationToken);
        commit.Commit();
    }

    /// <summary>Every row of an Arrow scan in batches of seven, a composite rendered field by field.</summary>
    private static async Task<List<string>> RenderAsync(PocoSource source)
    {
        var batches = await PocoTestSupport.ScanAsync(source, "accounts", 7);
        try
        {
            var rows = new List<string>();
            foreach (var batch in batches)
            {
                for (var i = 0; i < batch.Length; i++)
                {
                    rows.Add(string.Join("|", Enumerable.Range(0, batch.ColumnCount).Select(c => Render(batch.Column(c), i))));
                }
            }

            return rows;
        }
        finally
        {
            PocoTestSupport.Dispose(batches);
        }
    }

    private static string Render(IArrowArray array, int i) => array.IsNull(i) ? "NULL" : array switch
    {
        StructArray composite => "[" + string.Join(", ", composite.Fields.Select(f => Render(f, i))) + "]",
        StringArray text => text.GetString(i),
        Int32Array int32 => int32.GetValue(i)!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Int64Array int64 => int64.GetValue(i)!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        DoubleArray real => real.GetValue(i)!.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        _ => throw new InvalidOperationException($"no rendering for {array.GetType().Name}"),
    };

    [Fact]
    public void A_composite_column_carries_no_statistics_at_any_level()
    {
        foreach (var level in new[] { Chalk.Ir.StatisticsLevel.Basic, Chalk.Ir.StatisticsLevel.Histogram })
        {
            var source = new PocoSourceBuilder("mem")
                .AddTable("accounts", Accounts(40), t => t.Statistics(level))
                .Build();
            var columns = PocoTestSupport.Describe(source, "accounts").Columns;

            Assert.Same(ColumnStatistics.Unknown, columns[1].Statistics);
            Assert.Same(ColumnStatistics.Unknown, columns[2].Statistics);
            Assert.Equal(level, columns[0].Statistics.Level);
        }
    }

    public sealed record Linked(Uri Link, int Rank);

    public sealed record WithLink(long Id, Linked Link);

    public sealed record ContactArray(long Id, Contact[] Contacts);

    [Fact]
    public void A_record_with_a_property_no_field_can_be_is_refused_naming_it()
    {
        var ex = Assert.Throws<CatalogValidationException>(
            () => new PocoSourceBuilder("mem").AddTable("links", new[] { new WithLink(1, new Linked(new Uri("https://x"), 1)) }).Build());

        Assert.Contains("table 'links' member 'Link'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("property 'Link' of Linked is Uri, which no composite field can be", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_list_of_records_is_refused_as_nested()
    {
        var ex = Assert.Throws<CatalogValidationException>(
            () => new PocoSourceBuilder("mem").AddTable("lists", new[] { new ContactArray(1, []) }).Build());

        Assert.Contains(
            "Contact[] is a list of Contact records; a LIST holds scalars, and a composite column is never "
            + "nested in another column.",
            ex.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Every_declaration_that_would_key_index_order_or_describe_a_composite_is_refused_at_build()
    {
        static CatalogValidationException Refused(Action<PocoTableBuilder<Account>> configure) =>
            Assert.Throws<CatalogValidationException>(
                () => new PocoSourceBuilder("mem").AddTable("accounts", Accounts(4), configure).Build());

        Assert.Equal(
            "Invalid catalog at table 'accounts' index 'ix_contact': 'Contact' is a composite column and cannot be an index key: a "
            + "composite value has no ordering or equality, so nothing is keyed, indexed or ordered on one. "
            + "Declare the field to key on as a member of its own.",
            Refused(t => t.Index("ix_contact", Chalk.Ir.IndexKind.Ordered, unique: false, a => a.Contact)).Message);
        // An index the host did not name is refused by the name it would have had.
        Assert.StartsWith(
            "Invalid catalog at table 'accounts' index 'ix_accounts_Contact': 'Contact' is a composite column",
            Refused(t => t.Index(a => a.Contact)).Message,
            StringComparison.Ordinal);
        Assert.StartsWith(
            "Invalid catalog at table 'accounts' index 'ix_accounts_Id': a clustered index with no covering set",
            Refused(t => t.ClusteredIndex(a => a.Id)).Message,
            StringComparison.Ordinal);
        Assert.Equal(
            "Invalid catalog at table 'accounts': Index(keys) names 'Tier' of 'Contact', which is a composite column; a field of "
            + "a composite column is not a column of its own, and nothing is keyed, indexed or ordered on "
            + "one. Declare the field as a member of its own.",
            Refused(t => t.Index(a => a.Contact!.Tier)).Message);
        Assert.Contains(
            "'Where' is a composite column and cannot be a unique key",
            Refused(t => t.UniqueKey(a => a.Where)).Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "'Where' is a composite column and cannot be a collation",
            Refused(t => t.OrderedBy(a => a.Where)).Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "a clustered index with no covering set copies every column, and 'Contact' is a composite column",
            Refused(t => t.ClusteredIndex(a => a.Id)).Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "'Where' is a composite column and cannot be a clustered index's covering set",
            Refused(t => t.ClusteredIndex(a => a.Id).Covering(a => a.Where)).Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "'Contact' is a composite column, which carries no statistics",
            Refused(t => t.Statistics(a => a.Contact, new ColumnStatistics { NullCount = 1 })).Message,
            StringComparison.Ordinal);

        // A clustered index that names its covering set, leaving the composite out, is fine.
        var fine = new PocoSourceBuilder("mem")
            .AddTable("accounts", Accounts(4), t => t.ClusteredIndex(a => a.Id).Covering(a => a.Id))
            .Build();
        Assert.Single(PocoTestSupport.Describe(fine, "accounts").Indexes);
    }

    /// <summary>The same rows with every field a column of its own: the floor a composite column is held to.</summary>
    public sealed record FlatAccount(
        long Id, Utf8String Email, string? Phone, int Tier, double X, double Y, double? PreviousX, double? PreviousY);

    private static FlatAccount[] Flattened(Account[] accounts) =>
    [
        .. accounts.Select(a => new FlatAccount(
            a.Id, a.Contact?.Email ?? default, a.Contact?.Phone, a.Contact?.Tier ?? 0,
            a.Where.X, a.Where.Y, a.Previous?.X, a.Previous?.Y)),
    ];

    /// <summary>
    /// The engine's scan path — the columnar one, views over the staging and no Arrow object — costs
    /// nothing for a composite column over its fields as columns of their own: the records are staged
    /// in an array reused for every batch, and the field views are the fields' own.
    /// </summary>
    [Fact]
    public async Task A_columnar_scan_of_composite_columns_costs_nothing_over_their_fields()
    {
        const int rows = 20_000;
        var accounts = Accounts(rows);
        var composite = new PocoSourceBuilder("mem").AddTable("t", accounts).Build();
        var flat = new PocoSourceBuilder("mem").AddTable("t", Flattened(accounts)).Build();
        using var arena = new ExecutionArena();

        var (floor, flatRows) = await AllocationProbe.SteadyStateAsync(() => ColumnarAsync(flat, arena));
        var (measured, scanned) = await AllocationProbe.SteadyStateAsync(() => ColumnarAsync(composite, arena));

        Assert.Equal(rows, scanned);
        Assert.Equal(rows, flatRows);
        Assert.Equal(0, arena.OutstandingBytes);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"columnar scan of {rows} rows: three composite columns {measured} bytes, their fields as columns {floor} bytes");
        Assert.True(
            measured <= floor,
            $"a columnar scan of composite columns allocated {measured} bytes against {floor} for their fields as columns");
    }

    /// <summary>
    /// The Arrow scan — a host's and a remote source's path — pays per batch for Arrow objects, and a
    /// composite column adds one struct array to its fields' arrays: under 400 bytes a batch (the
    /// array, its data and its child list), and nothing per row.
    /// </summary>
    [Fact]
    public async Task An_arrow_scan_of_composite_columns_costs_one_struct_a_batch_over_their_fields()
    {
        const int rows = 20_000;
        const int batchSize = 4096;
        var accounts = Accounts(rows);
        var composite = new PocoSourceBuilder("mem").AddTable("t", accounts).Build();
        var flat = new PocoSourceBuilder("mem").AddTable("t", Flattened(accounts)).Build();
        using var arena = new ExecutionArena();

        var (floor, _) = await AllocationProbe.SteadyStateAsync(() => ArrowAsync(flat, arena, batchSize));
        var (measured, scanned) = await AllocationProbe.SteadyStateAsync(() => ArrowAsync(composite, arena, batchSize));

        Assert.Equal(rows, scanned);
        Assert.Equal(0, arena.OutstandingBytes);
        var batches = (rows + batchSize - 1) / batchSize;
        var wrappers = 3 * batches;
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"Arrow scan of {rows} rows in {batches} batches: three composite columns {measured} bytes, their fields "
            + $"as columns {floor} bytes = {(measured - floor) / (double)wrappers:0.#} bytes per struct array");
        Assert.True(
            measured <= floor + (wrappers * 400L),
            $"an Arrow scan of composite columns allocated {measured} bytes against {floor} for their fields as "
            + $"columns, more than 400 bytes for each of its {wrappers} struct arrays");
    }

    private static async Task<long> ColumnarAsync(PocoSource source, ExecutionArena arena)
    {
        var request = PocoTestSupport.Request(source, "t", 4096);
        var types = PocoTestSupport.Describe(source, "t").Columns.Select(c => c.Type).ToArray();
        var scan = ((IColumnarBatchSource)source).ColumnarScan(request, PocoTestSupport.Context(arena: arena))
            ?? throw new InvalidOperationException("the source declined to serve a columnar scan");
        long rows = 0;
        await using (scan.ConfigureAwait(false))
        {
            var batch = new ColumnarBatch(types);
            while (scan.TryNext(batch))
            {
                rows += batch.Count;
            }
        }

        return rows;
    }

    private static async Task<long> ArrowAsync(PocoSource source, ExecutionArena arena, int batchSize)
    {
        var stats = new ExecutionStats();
        var request = PocoTestSupport.Request(source, "t", batchSize);
        await foreach (var batch in source.ScanAsync(
            request, PocoTestSupport.Context(stats, arena), TestContext.Current.CancellationToken))
        {
            batch.Dispose();
        }

        return stats.RowsScanned;
    }
}

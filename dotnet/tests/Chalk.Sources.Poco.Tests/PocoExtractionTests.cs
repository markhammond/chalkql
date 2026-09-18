using System.Collections;
using Apache.Arrow;
using Chalk.Catalog;
using TypeKind = Chalk.Ir.TypeKind;

namespace Chalk.Sources.Poco.Tests;

/// <summary>
/// §5.3: the compiled chunk writers, cell by cell, for class rows and struct rows, at the batch sizes
/// the milestone gate names.
/// </summary>
public sealed class PocoExtractionTests
{
    private const long UnixEpochTicks = 621_355_968_000_000_000L;

    private const int UnixEpochDayNumber = 719_162;

    private const int RowCount = 20;

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(4096)]
    public async Task Class_rows_extract_every_supported_clr_type(int batchSize)
    {
        var rows = Rows();
        var source = new PocoSourceBuilder("mem").AddTable("t", rows).Build();

        await AssertColumnsAsync(source, rows, batchSize);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(4096)]
    public async Task Nullable_members_extract_values_and_null_flags(int batchSize)
    {
        var rows = NullableRows();
        var source = new PocoSourceBuilder("mem").AddTable("t", rows).Build();
        var table = PocoTestSupport.Describe(source, "t");
        var batches = await PocoTestSupport.ScanAsync(source, "t", batchSize);

        try
        {
            for (var c = 0; c < table.Columns.Count; c++)
            {
                var name = table.Columns[c].Name;
                var actual = Flatten(batches, c);
                Assert.Equal(rows.Count, actual.Count);
                for (var r = 0; r < rows.Count; r++)
                {
                    var expected = IsNullRow(r) ? null : Expected(Rows()[r], name);
                    AssertCell(expected, actual[r], $"{name}[{r}]");
                }
            }
        }
        finally
        {
            PocoTestSupport.Dispose(batches);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(4096)]
    public async Task Struct_rows_extract_the_same_way_class_rows_do(int batchSize)
    {
        var rows = Bars();
        var source = new PocoSourceBuilder("mem").AddTable("bars", rows).Build();
        var batches = await PocoTestSupport.ScanAsync(source, "bars", batchSize);

        try
        {
            var ts = Flatten(batches, 0);
            var symbol = Flatten(batches, 1);
            var close = Flatten(batches, 2);
            var volume = Flatten(batches, 3);
            var shade = Flatten(batches, 4);

            for (var r = 0; r < rows.Count; r++)
            {
                AssertCell(Nanoseconds(rows[r].Ts), ts[r], $"Ts[{r}]");
                AssertCell(rows[r].Symbol, symbol[r], $"Symbol[{r}]");
                AssertCell(rows[r].Close, close[r], $"Close[{r}]");
                AssertCell(rows[r].Volume, volume[r], $"Volume[{r}]");
                AssertCell(rows[r].Shade.ToString(), shade[r], $"Shade[{r}]");
            }
        }
        finally
        {
            PocoTestSupport.Dispose(batches);
        }
    }

    [Theory]
    [InlineData(1, new[] { 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 })]
    [InlineData(7, new[] { 7, 7, 6 })]
    [InlineData(4096, new[] { 20 })]
    public async Task Batch_boundaries_land_where_the_batch_size_puts_them(int batchSize, int[] expected)
    {
        var source = new PocoSourceBuilder("mem").AddTable("t", Rows()).Build();
        var batches = await PocoTestSupport.ScanAsync(source, "t", batchSize);

        try
        {
            Assert.Equal(expected, batches.Select(b => b.Length));
            Assert.All(batches, b => Assert.True(
                ArrowTypeMapping.AreEquivalent(
                    b.Schema, PocoTestSupport.Request(source, "t", batchSize).OutputSchema)));
        }
        finally
        {
            PocoTestSupport.Dispose(batches);
        }
    }

    /// <summary>
    /// A2: <c>T[]</c> and <c>List&lt;T&gt;</c> get their own compiled loop, and everything else goes
    /// through the interface. All three must agree, cell for cell.
    /// </summary>
    [Fact]
    public async Task Arrays_lists_and_plain_read_only_lists_all_produce_the_same_batches()
    {
        var rows = Bars();

        var fromArray = await ScanBarsAsync(rows.ToArray());
        var fromList = await ScanBarsAsync(rows);
        var fromWrapper = await ScanBarsAsync(new OpaqueList<BarRow>(rows));

        Assert.Equal(fromArray, fromList);
        Assert.Equal(fromArray, fromWrapper);
    }

    [Fact]
    public async Task An_empty_collection_produces_no_batches()
    {
        var source = new PocoSourceBuilder("mem").AddTable("t", new List<BarRow>()).Build();

        var batches = await PocoTestSupport.ScanAsync(source, "t", 4096);

        Assert.Empty(batches);
    }

    [Fact]
    public async Task An_enum_with_no_declared_name_falls_back_to_its_number()
    {
        var rows = new List<BarRow> { Bar(0) with { Shade = (Colour)42 } };
        var source = new PocoSourceBuilder("mem").AddTable("bars", rows).Build();

        var values = await PocoTestSupport.ColumnAsync(source, "bars", 0, 4096, projection: [4]);

        Assert.Equal("42", Assert.Single(values));
    }

    [Fact]
    public async Task An_enum_marked_as_integer_extracts_its_underlying_value()
    {
        var rows = new List<AttributedRow> { new() { Shade = Colour.Blue } };
        var source = new PocoSourceBuilder("mem").AddTable("t", rows).Build();

        var values = await PocoTestSupport.ColumnAsync(source, "t", 0, 4096, projection: [2]);

        Assert.Equal(2, Assert.Single(values));
    }

    [Fact]
    public async Task Decimal_trailing_zeros_beyond_the_scale_are_not_an_error()
    {
        var rows = new List<AttributedRow> { new() { Fee = 1.500000m } };
        var source = new PocoSourceBuilder("mem").AddTable("t", rows).Build();

        var values = await PocoTestSupport.ColumnAsync(source, "t", 0, 4096, projection: [1]);

        Assert.Equal(1.5000m, Assert.Single(values));
    }

    [Fact]
    public async Task A_decimal_with_more_fractional_digits_than_the_scale_is_loud()
    {
        var rows = new List<AttributedRow> { new() { Fee = 1.00001m } };
        var source = new PocoSourceBuilder("mem").AddTable("t", rows).Build();

        var error = await Assert.ThrowsAsync<SourceContractException>(
            () => PocoTestSupport.ColumnAsync(source, "t", 0, 4096, projection: [1]));

        Assert.Contains("DECIMAL(18,4)", error.Message, StringComparison.Ordinal);
        Assert.Contains("Fee", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_decimal_too_wide_for_its_precision_is_loud()
    {
        var rows = new List<AttributedRow> { new() { Fee = 123_456_789_012_345.5m } };
        var source = new PocoSourceBuilder("mem").AddTable("t", rows).Build();

        var error = await Assert.ThrowsAsync<SourceContractException>(
            () => PocoTestSupport.ColumnAsync(source, "t", 0, 4096, projection: [1]));

        Assert.Contains("more than 18 digits", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_null_in_a_column_declared_not_null_is_loud()
    {
        var rows = new List<NullableTypesRow> { new() { Text = null } };
        var source = new PocoSourceBuilder("mem")
            .AddTable("t", rows, t => t.Column(r => r.Text, type: ChalkType.String()))
            .Build();

        var column = PocoTestSupport.Describe(source, "t").IndexOfColumn("Text");

        var error = await Assert.ThrowsAsync<SourceContractException>(
            () => PocoTestSupport.ColumnAsync(source, "t", 0, 4096, projection: [column]));

        Assert.Contains("declared NOT NULL", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A coarser declared precision selects a coarser Arrow unit (<c>02-ir.md</c> §3).</summary>
    [Fact]
    public async Task A_timestamp_precision_override_changes_the_unit_the_value_is_written_in()
    {
        var rows = Bars();
        var source = new PocoSourceBuilder("mem")
            .AddTable("bars", rows, t => t.Column(r => r.Ts, type: ChalkType.Timestamp(3)))
            .Build();

        var batches = await PocoTestSupport.ScanAsync(source, "bars", 4096, projection: [0]);

        try
        {
            Assert.Equal(
                "timestamp[Millisecond, tz=none]",
                ArrowTypeMapping.DescribeArrow(batches[0].Schema.FieldsList[0].DataType));
            Assert.Equal(
                rows.Select(r => (object?)((r.Ts.Ticks - UnixEpochTicks) / 10_000L)),
                Flatten(batches, 0));
        }
        finally
        {
            PocoTestSupport.Dispose(batches);
        }
    }

    [Fact]
    public async Task A_computed_column_is_extracted_by_the_same_compiled_loop()
    {
        var rows = Bars();
        var source = new PocoSourceBuilder("mem")
            .AddTable("bars", rows, t => t.Column("minute", r => r.Ts.Minute))
            .Build();
        var column = PocoTestSupport.Describe(source, "bars").IndexOfColumn("minute");

        var values = await PocoTestSupport.ColumnAsync(source, "bars", 0, 7, projection: [column]);

        Assert.Equal(rows.Select(r => (object?)r.Ts.Minute), values);
    }

    [Fact]
    public void A_pushed_filter_this_source_never_declared_is_refused()
    {
        var source = new PocoSourceBuilder("mem").AddTable("bars", Bars()).Build();
        var request = new ScanRequest
        {
            Table = "bars",
            Projection = [0],
            OutputSchema = PocoTestSupport.Request(source, "bars", 16, [0]).OutputSchema,
            BatchSize = 16,
            PushedFilter = new Chalk.Ir.Expr(),
        };

        var error = Assert.Throws<SourceContractException>(
            () => source.ScanAsync(request, PocoTestSupport.Context(), CancellationToken.None));

        Assert.Contains("SourceCapabilities.None", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_output_schema_this_projection_cannot_produce_is_refused()
    {
        var source = new PocoSourceBuilder("mem").AddTable("bars", Bars()).Build();
        var request = new ScanRequest
        {
            Table = "bars",
            Projection = [0],
            OutputSchema = ArrowTypeMapping.ToArrowSchema(
                new[] { new ColumnDescriptor { Name = "Ts", Type = ChalkType.Int64() } }),
            BatchSize = 16,
        };

        var error = Assert.Throws<SourceContractException>(
            () => source.ScanAsync(request, PocoTestSupport.Context(), CancellationToken.None));

        Assert.Contains("output schema", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_table_is_refused_and_lists_the_ones_that_exist()
    {
        var source = new PocoSourceBuilder("mem").AddTable("bars", Bars()).Build();
        var request = new ScanRequest
        {
            Table = "quotes",
            Projection = [0],
            OutputSchema = PocoTestSupport.Request(source, "bars", 16, [0]).OutputSchema,
            BatchSize = 16,
        };

        var error = Assert.Throws<SourceContractException>(
            () => source.ScanAsync(request, PocoTestSupport.Context(), CancellationToken.None));

        Assert.Contains("bars", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_projection_index_outside_the_table_is_refused()
    {
        var source = new PocoSourceBuilder("mem").AddTable("bars", Bars()).Build();
        var request = new ScanRequest
        {
            Table = "bars",
            Projection = [99],
            OutputSchema = PocoTestSupport.Request(source, "bars", 16, [0]).OutputSchema,
            BatchSize = 16,
        };

        Assert.Throws<SourceContractException>(
            () => source.ScanAsync(request, PocoTestSupport.Context(), CancellationToken.None));
    }

    [Fact]
    public async Task A_cancelled_scan_stops_at_the_next_batch()
    {
        var source = new PocoSourceBuilder("mem").AddTable("bars", Bars()).Build();
        var request = PocoTestSupport.Request(source, "bars", 4, [0]);
        using var cts = new CancellationTokenSource();

        var produced = 0;
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await foreach (var batch in source.ScanAsync(request, PocoTestSupport.Context(), cts.Token))
            {
                batch.Dispose();
                produced++;
                await cts.CancelAsync();
            }
        });

        Assert.Equal(1, produced);
    }

    [Fact]
    public async Task A_table_is_found_case_insensitively()
    {
        var source = new PocoSourceBuilder("mem").AddTable("Bars", Bars()).Build();

        var batches = await PocoTestSupport.ScanAsync(source, "BARS", 4096);

        try
        {
            Assert.Single(batches);
        }
        finally
        {
            PocoTestSupport.Dispose(batches);
        }
    }

    private static async Task AssertColumnsAsync(PocoSource source, List<AllTypesRow> rows, int batchSize)
    {
        var table = PocoTestSupport.Describe(source, "t");
        var batches = await PocoTestSupport.ScanAsync(source, "t", batchSize);

        try
        {
            for (var c = 0; c < table.Columns.Count; c++)
            {
                var name = table.Columns[c].Name;
                var actual = Flatten(batches, c);
                Assert.Equal(rows.Count, actual.Count);
                for (var r = 0; r < rows.Count; r++)
                {
                    AssertCell(Expected(rows[r], name), actual[r], $"{name}[{r}]");
                }
            }
        }
        finally
        {
            PocoTestSupport.Dispose(batches);
        }
    }

    private static async Task<List<object?>> ScanBarsAsync(IReadOnlyList<BarRow> rows)
    {
        var source = new PocoSourceBuilder("mem").AddTable("bars", rows).Build();
        var batches = await PocoTestSupport.ScanAsync(source, "bars", 7);

        try
        {
            var values = new List<object?>();
            for (var c = 0; c < 5; c++)
            {
                values.AddRange(Flatten(batches, c));
            }

            return values;
        }
        finally
        {
            PocoTestSupport.Dispose(batches);
        }
    }

    private static List<object?> Flatten(List<RecordBatch> batches, int column)
    {
        var values = new List<object?>();
        foreach (var batch in batches)
        {
            var array = batch.Column(column);
            for (var i = 0; i < array.Length; i++)
            {
                values.Add(PocoTestSupport.Read(array, i));
            }
        }

        return values;
    }

    private static void AssertCell(object? expected, object? actual, string where)
    {
        if (expected is byte[] expectedBytes)
        {
            Assert.True(
                actual is byte[] actualBytes && expectedBytes.SequenceEqual(actualBytes),
                $"{where}: expected {Convert.ToHexString(expectedBytes)}, got {actual}");
            return;
        }

        Assert.True(Equals(expected, actual), $"{where}: expected {expected ?? "null"}, got {actual ?? "null"}");
    }

    private static object? Expected(AllTypesRow r, string column) => column switch
    {
        "Flag" => r.Flag,
        "Tiny" => r.Tiny,
        "Small" => r.Small,
        "Medium" => r.Medium,
        "Big" => r.Big,
        "UnsignedTiny" => (short)r.UnsignedTiny,
        "UnsignedSmall" => (int)r.UnsignedSmall,
        "UnsignedMedium" => (long)r.UnsignedMedium,
        "UnsignedBig" => (decimal)r.UnsignedBig,
        "Single" => r.Single,
        "Real" => r.Real,
        "Money" => r.Money,
        "Text" => r.Text,
        "Blob" => r.Blob,
        "Memory" => r.Memory.ToArray(),
        "Moment" => Nanoseconds(r.Moment),
        "Instant" => (r.Instant.UtcTicks - UnixEpochTicks) * 100L,
        "Day" => r.Day.DayNumber - UnixEpochDayNumber,
        "Clock" => r.Clock.Ticks / 10L,
        "Span" => r.Span.Ticks / 10L,
        "Key" => r.Key,
        "Shade" => r.Shade.ToString(),
        "Letter" => r.Letter.ToString(),
        _ => throw new InvalidOperationException($"no expectation for {column}"),
    };

    private static long Nanoseconds(DateTime value) => (value.Ticks - UnixEpochTicks) * 100L;

    private static bool IsNullRow(int i) => i % 3 == 0;

    private static List<AllTypesRow> Rows() => Enumerable.Range(0, RowCount).Select(MakeRow).ToList();

    private static List<NullableTypesRow> NullableRows() =>
        Enumerable.Range(0, RowCount).Select(MakeNullableRow).ToList();

    private static List<BarRow> Bars() => Enumerable.Range(0, RowCount).Select(Bar).ToList();

    private static BarRow Bar(int i) => new(
        new DateTime(2026, 1, 3, 9, 0, 0, DateTimeKind.Unspecified).AddMinutes(i),
        i % 2 == 0 ? "BTCUSDT" : "ETHUSDT",
        1000.5m + i,
        i % 4 == 0 ? null : i * 1.25,
        (Colour)(i % 3));

    private static AllTypesRow MakeRow(int i) => new()
    {
        Flag = i % 2 == 0,
        Tiny = (sbyte)(i - 10),
        Small = (short)(i * 100),
        Medium = i * 100_000,
        Big = i * 10_000_000_000L,
        UnsignedTiny = (byte)(200 + i),
        UnsignedSmall = (ushort)(60_000 + i),
        UnsignedMedium = (uint)(4_000_000_000L + i),
        UnsignedBig = ulong.MaxValue - (ulong)i,
        Single = i * 1.5f,
        Real = i * 2.25,
        Money = i + 0.0000000001m,
        Text = i % 5 == 0 ? string.Empty : $"row-{i}-é中",
        Blob = [(byte)i, (byte)(i + 1)],
        Memory = new byte[] { (byte)(i + 2), (byte)(i + 3) },
        Moment = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified).AddTicks(i * 1_234_567L),
        Instant = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.FromHours(2)).AddMinutes(i),
        Day = new DateOnly(2026, 1, 1).AddDays(i),
        Clock = new TimeOnly(1, 2, 3).Add(TimeSpan.FromTicks(i * 10L)),
        Span = TimeSpan.FromTicks(i * 12_345L),
        Key = new Guid($"00000000-0000-0000-0000-{i:D12}"),
        Shade = (Colour)(i % 3),
        Letter = (char)('a' + (i % 26)),
    };

    private static NullableTypesRow MakeNullableRow(int i)
    {
        if (IsNullRow(i))
        {
            return new NullableTypesRow();
        }

        var row = MakeRow(i);
        return new NullableTypesRow
        {
            Flag = row.Flag,
            Tiny = row.Tiny,
            Small = row.Small,
            Medium = row.Medium,
            Big = row.Big,
            UnsignedTiny = row.UnsignedTiny,
            UnsignedSmall = row.UnsignedSmall,
            UnsignedMedium = row.UnsignedMedium,
            UnsignedBig = row.UnsignedBig,
            Single = row.Single,
            Real = row.Real,
            Money = row.Money,
            Text = row.Text,
            Blob = row.Blob,
            Memory = row.Memory,
            Moment = row.Moment,
            Instant = row.Instant,
            Day = row.Day,
            Clock = row.Clock,
            Span = row.Span,
            Key = row.Key,
            Shade = row.Shade,
            Letter = row.Letter,
        };
    }

    /// <summary>An <see cref="IReadOnlyList{T}"/> that is neither an array nor a list, to exercise the general loop.</summary>
    private sealed class OpaqueList<TRow> : IReadOnlyList<TRow>
    {
        private readonly IReadOnlyList<TRow> _inner;

        public OpaqueList(IReadOnlyList<TRow> inner) => _inner = inner;

        public int Count => _inner.Count;

        public TRow this[int index] => _inner[index];

        public IEnumerator<TRow> GetEnumerator() => _inner.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    // ---- LIST columns (D58, 14-windows-ii.md §5) --------------------------------------------------

    private sealed record ListRow(
        int Id,
        string[]? Names,
        List<int>? Counts,
        IReadOnlyList<double?>? Prices,
        System.Collections.Immutable.ImmutableArray<decimal> Rates);

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(4096)]
    public async Task List_members_extract_as_arrow_lists(int batchSize)
    {
        var rows = new List<ListRow>
        {
            new(1, ["a", "bb"], [1, 2, 3], [1.5, null], [1.25m, 2.5m]),
            new(2, [], [], [], System.Collections.Immutable.ImmutableArray<decimal>.Empty),
            new(3, null, null, null, default),
            new(4, ["only"], [7], [null], [9.75m]),
        };
        var source = new PocoSourceBuilder("mem").AddTable("t", rows).Build();
        var table = PocoTestSupport.Describe(source, "t");

        Assert.Equal(TypeKind.List, table.Columns[1].Type.Kind);
        Assert.Equal(TypeKind.String, table.Columns[1].Type.Element!.Value.Kind);
        Assert.Equal(TypeKind.I32, table.Columns[2].Type.Element!.Value.Kind);
        Assert.True(table.Columns[3].Type.Element!.Value.Nullable);
        Assert.Equal(TypeKind.Decimal, table.Columns[4].Type.Element!.Value.Kind);

        var names = await PocoTestSupport.ColumnAsync(source, "t", 1, batchSize);
        var counts = await PocoTestSupport.ColumnAsync(source, "t", 2, batchSize);
        var prices = await PocoTestSupport.ColumnAsync(source, "t", 3, batchSize);
        var rates = await PocoTestSupport.ColumnAsync(source, "t", 4, batchSize);

        Assert.Equal(["a", "bb"], (List<object?>)names[0]!);
        Assert.Empty((List<object?>)names[1]!);
        Assert.Null(names[2]);
        Assert.Equal([1, 2, 3], ((List<object?>)counts[0]!).Select(v => (int)v!));
        Assert.Equal([1.5, null], (List<object?>)prices[0]!);
        Assert.Equal([null], (List<object?>)prices[3]!);
        Assert.Equal([1.25m, 2.5m], ((List<object?>)rates[0]!).Select(v => (decimal)v!));

        // A default ImmutableArray is a NULL list, not an empty one.
        Assert.Null(rates[2]);
        Assert.Empty((List<object?>)rates[1]!);
    }

    /// <summary>v1 lists are one level deep, and inference says so rather than the planner (D58).</summary>
    [Fact]
    public void A_list_of_lists_is_refused_at_inference()
    {
        var error = Assert.Throws<CatalogValidationException>(
            () => new PocoSourceBuilder("mem").AddTable("t", new[] { new NestedRow(1, [[1]]) }).Build());

        Assert.Contains("one level deep", error.Message, StringComparison.Ordinal);
    }

    private sealed record NestedRow(int Id, int[][] Nested);
}

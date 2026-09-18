namespace Chalk.Sources.Poco.Tests;

/// <summary>§5.3: only the requested columns are extracted, in the requested order, and rows are counted.</summary>
public sealed class PocoProjectionTests
{
    [Fact]
    public async Task Only_the_requested_columns_are_produced_in_the_requested_order()
    {
        var rows = Rows(10);
        var source = new PocoSourceBuilder("mem").AddTable("bars", rows).Build();

        // Ts is column 0 and Close is column 2; ask for them the other way round.
        var batches = await PocoTestSupport.ScanAsync(source, "bars", 4, projection: [2, 0]);

        try
        {
            Assert.All(batches, b => Assert.Equal(2, b.ColumnCount));
            Assert.All(batches, b => Assert.Equal(["Close", "Ts"], b.Schema.FieldsList.Select(f => f.Name)));

            var close = batches.SelectMany(b => Values(b.Column(0))).ToList();
            Assert.Equal(rows.Select(r => (object?)r.Close), close);
        }
        finally
        {
            PocoTestSupport.Dispose(batches);
        }
    }

    [Fact]
    public async Task The_same_column_may_be_projected_twice()
    {
        var rows = Rows(5);
        var source = new PocoSourceBuilder("mem").AddTable("bars", rows).Build();

        var batches = await PocoTestSupport.ScanAsync(source, "bars", 4096, projection: [1, 1]);

        try
        {
            var batch = Assert.Single(batches);
            Assert.Equal(Values(batch.Column(0)), Values(batch.Column(1)));
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
    public async Task Rows_scanned_counts_the_rows_the_scan_read(int batchSize)
    {
        var stats = new ExecutionStats();
        var source = new PocoSourceBuilder("mem").AddTable("bars", Rows(37)).Build();

        var batches = await PocoTestSupport.ScanAsync(source, "bars", batchSize, projection: [0], stats: stats);

        try
        {
            Assert.Equal(37, stats.RowsScanned);
            Assert.Equal(37, batches.Sum(b => b.Length));
        }
        finally
        {
            PocoTestSupport.Dispose(batches);
        }
    }

    [Fact]
    public async Task A_projection_of_one_column_does_not_touch_the_others()
    {
        // Close is DECIMAL(28,10) and would throw on this value; asking only for Ts must not extract it.
        var rows = new List<BarRow>
        {
            new(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified), "BTC", 1.5m, null, Colour.Red),
        };
        var source = new PocoSourceBuilder("mem")
            .AddTable("bars", rows, t => t.Column(r => r.Close, type: Catalog.ChalkType.Decimal(4, 0)))
            .Build();

        var values = await PocoTestSupport.ColumnAsync(source, "bars", 0, 4096, projection: [1]);

        Assert.Equal("BTC", Assert.Single(values));
    }

    [Fact]
    public async Task Scanning_twice_produces_independent_batches()
    {
        var source = new PocoSourceBuilder("mem").AddTable("bars", Rows(9)).Build();

        var first = await PocoTestSupport.ScanAsync(source, "bars", 4, projection: [0]);
        var second = await PocoTestSupport.ScanAsync(source, "bars", 4, projection: [0]);

        try
        {
            Assert.Equal(
                first.SelectMany(b => Values(b.Column(0))),
                second.SelectMany(b => Values(b.Column(0))));
        }
        finally
        {
            PocoTestSupport.Dispose(first);
            PocoTestSupport.Dispose(second);
        }
    }

    private static List<object?> Values(Apache.Arrow.IArrowArray array)
    {
        var values = new List<object?>(array.Length);
        for (var i = 0; i < array.Length; i++)
        {
            values.Add(PocoTestSupport.Read(array, i));
        }

        return values;
    }

    private static List<BarRow> Rows(int count) => Enumerable
        .Range(0, count)
        .Select(i => new BarRow(
            new DateTime(2026, 1, 3, 9, 0, 0, DateTimeKind.Unspecified).AddMinutes(i),
            i % 2 == 0 ? "BTCUSDT" : "ETHUSDT",
            1000.5m + i,
            i * 1.25,
            (Colour)(i % 3)))
        .ToList();
}

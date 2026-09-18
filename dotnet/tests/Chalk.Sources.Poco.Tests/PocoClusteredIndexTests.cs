using Chalk.Catalog;
using IndexKind = Chalk.Ir.IndexKind;

namespace Chalk.Sources.Poco.Tests;

/// <summary>
/// The clustered index (D257, <c>docs/design/34-clustered-indexes.md</c>): what the builder accepts,
/// what the copy holds, and — the claim that matters — that a lookup served from the copy answers
/// exactly what the same lookup over a permutation index answers, row for row and value for value.
/// </summary>
public sealed class PocoClusteredIndexTests
{
    private sealed record Point(string Group, int? Rank, double Value);

    private static readonly Point[] Points =
    [
        new("a", 1, 1.5),
        new("a", 3, 2.5),
        new("b", 2, double.NaN),
        new("b", 5, 4.5),
        new("c", null, 5.5),
        new("a", 2, 6.5),
        new("c", 4, 7.5),
    ];

    /// <summary>Every range shape design 34 §5 names, over the (Group, Rank) key.</summary>
    public static TheoryData<string> Ranges() =>
        ["equality-prefix", "lower-bound", "upper-bound", "both-bounds", "unbounded", "two-columns"];

    private static IndexKeyRange Range(string shape) => shape switch
    {
        "equality-prefix" => IndexKeyRange.Equality("a"),
        "lower-bound" => new IndexKeyRange { Lower = ["b"], LowerInclusive = true, Upper = [] },
        "upper-bound" => new IndexKeyRange { Lower = [], Upper = ["b"], UpperInclusive = false },
        "both-bounds" => new IndexKeyRange
        {
            Lower = ["a"], LowerInclusive = false, Upper = ["c"], UpperInclusive = true,
        },
        "unbounded" => IndexKeyRange.All,
        "two-columns" => new IndexKeyRange
        {
            Lower = ["a", 2], LowerInclusive = true, Upper = ["a"], UpperInclusive = true,
        },
        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "no such range shape"),
    };

    // ---- the builder ----

    [Fact]
    public void A_clustered_index_declares_the_clustered_kind_and_its_covering_set()
    {
        var source = Source(t => t
            .ClusteredIndex(p => p.Group, p => p.Rank)
            .Covering(p => p.Value));

        var index = Descriptor(source, "ix_points_Group_Rank");

        Assert.Equal(IndexKind.Clustered, index.Kind);
        Assert.Equal([0, 1], index.Columns);

        // The key columns are always covered, and the set is ascending without duplicates.
        Assert.Equal([0, 1, 2], index.Covering);
        Assert.True(index.Covers([2, 0]));
    }

    [Fact]
    public void A_clustered_index_with_no_covering_set_covers_every_column()
    {
        var source = Source(t => t.ClusteredIndex(p => p.Group));
        var index = Descriptor(source, "ix_points_Group");

        Assert.Empty(index.Covering);
        Assert.True(index.Covers([0, 1, 2]));
        Assert.Equal([0, 1, 2], Clustered(source, "ix_points_Group").Covering);
    }

    [Fact]
    public void UniqueClusteredIndex_enforces_its_key_exactly_as_UniqueIndex_does()
    {
        var error = Assert.Throws<CatalogVerificationException>(
            () => Source(t => t.UniqueClusteredIndex(p => p.Group)));

        Assert.Contains("unique index", error.Message, StringComparison.Ordinal);
        Assert.Contains("share the key", error.Message, StringComparison.Ordinal);

        var source = Source(t => t.UniqueClusteredIndex(p => p.Group, p => p.Value));
        Assert.True(Descriptor(source, "ix_points_Group_Value").Unique);
    }

    [Fact]
    public void A_clustered_index_over_a_key_that_is_not_a_column_is_refused()
    {
        var error = Assert.Throws<CatalogValidationException>(() =>
            new PocoSourceBuilder("mem")
                .AddTable("points", Points, t => t
                    .Ignore(p => p.Rank)
                    .ClusteredIndex(p => p.Group, p => p.Rank))
                .Build());

        Assert.Contains("'Rank'", error.Message, StringComparison.Ordinal);
        Assert.Contains("not one of the table's columns", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_covering_column_that_is_not_a_column_is_refused()
    {
        var error = Assert.Throws<CatalogValidationException>(() =>
            new PocoSourceBuilder("mem")
                .AddTable("points", Points, t => t
                    .Ignore(p => p.Value)
                    .ClusteredIndex(p => p.Group)
                    .Covering(p => p.Value))
                .Build());

        Assert.Contains("'Value'", error.Message, StringComparison.Ordinal);
        Assert.Contains("not one of the table's columns", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Covering_without_a_clustered_index_names_the_rule()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => Source(t => t.Index(p => p.Group).Covering(p => p.Value)));

        Assert.Contains("applies to the clustered index declared last", error.Message, StringComparison.Ordinal);
        Assert.Contains("ClusteredIndex", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Covering_before_any_index_names_the_rule()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => Source(t => t.Covering(p => p.Value)));

        Assert.Contains("no index has been declared", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Covering_applies_to_the_clustered_index_declared_last()
    {
        var source = Source(t => t
            .ClusteredIndex("first", unique: false, p => p.Value)
            .ClusteredIndex("second", unique: false, p => p.Group)
            .Covering(p => p.Rank));

        Assert.Empty(Descriptor(source, "first").Covering);
        Assert.Equal([0, 1], Descriptor(source, "second").Covering);
    }

    // ---- the copy ----

    [Fact]
    public void The_copy_is_in_the_index_key_order()
    {
        var source = Source(t => t.ClusteredIndex(p => p.Value));
        var index = Clustered(source, "ix_points_Value");

        // NaN sorts last, as 02-ir.md §3 requires; everything else ascends. The copy is the column
        // in that order, read straight out of it rather than through the rows.
        Assert.Equal(
            [1.5, 2.5, 4.5, 5.5, 6.5, 7.5, double.NaN],
            Doubles(index, column: 2, from: 0, count: Points.Length));
    }

    [Fact]
    public void The_copy_carries_only_the_covered_columns()
    {
        var source = Source(t => t.ClusteredIndex(p => p.Group).Covering(p => p.Value));
        var index = Clustered(source, "ix_points_Group");

        Assert.Equal([0, 2], index.Covering);
        Assert.False(index.Descriptor.Covers([1]));

        var error = Assert.Throws<InvalidOperationException>(() => index.Slice(1, 0, 1));
        Assert.Contains("covering set", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_copy_reports_what_it_costs()
    {
        var source = Source(t => t.ClusteredIndex(p => p.Group).Covering(p => p.Value));
        var index = Clustered(source, "ix_points_Group");

        // Seven doubles, seven group strings and their eight offsets. The numbers are the layout's,
        // not an estimate: values, offsets and validity, counted as they are held.
        const long values = 7 * sizeof(double);
        const long offsets = 8 * sizeof(int);
        var strings = Points.Sum(p => p.Group.Length);

        Assert.Equal(values + offsets + strings, index.CopyBytes);
        Assert.Equal(sizeof(int) + (index.CopyBytes / Points.Length), index.BytesPerRow);
    }

    [Fact]
    public void A_collation_backed_clustered_index_still_holds_its_copy()
    {
        var ordered = Points.Where(p => !double.IsNaN(p.Value)).OrderBy(p => p.Value).ToArray();
        var source = new PocoSourceBuilder("mem")
            .AddTable("points", ordered, t => t
                .OrderedBy(p => p.Value)
                .ClusteredIndex(p => p.Value))
            .Build();

        var index = (ClusteredIndex<Point>)Index(source, "points", "ix_points_Value");

        // No permutation to pay for — the list is already in key order — and the copy regardless.
        Assert.Equal(index.CopyBytes / ordered.Length, index.BytesPerRow);
        Assert.Equal(
            ordered.Select(p => p.Value).ToArray(),
            Doubles(index, column: 2, from: 0, count: ordered.Length));
    }

    /// <summary>
    /// The copy is rebuilt exactly where the permutation is — over whatever collection the snapshot
    /// was built from (D260 §1).
    /// </summary>
    [Fact]
    public async Task A_refresh_rebuilds_the_copy()
    {
        var rows = new List<Point>(Points);
        var before = new PocoSourceBuilder("mem")
            .AddTable("points", rows, t => t.ClusteredIndex(p => p.Value))
            .Build();

        Assert.Equal(
            [1.5, 2.5, 4.5, 5.5, 6.5, 7.5, double.NaN],
            Doubles(Clustered(before, "ix_points_Value"), column: 2, from: 0, count: rows.Count));

        rows.Add(new Point("d", 9, 0.5));
        var after = new PocoSourceBuilder("mem")
            .AddTable("points", rows, t => t.ClusteredIndex(p => p.Value))
            .Build();

        Assert.Equal(
            [0.5, 1.5, 2.5, 4.5, 5.5, 6.5, 7.5, double.NaN],
            Doubles(Clustered(after, "ix_points_Value"), column: 2, from: 0, count: rows.Count));

        // And the rebuilt source answers the rebuilt copy, through the scan rather than the index.
        var scanned = await SliceLookupAsync(after, "ix_points_Value", [IndexKeyRange.All], 3, [2]);
        Assert.Equal([0.5, 1.5, 2.5, 4.5, 5.5, 6.5, 7.5, double.NaN], scanned.Select(r => (double)r[0]!));
    }

    /// <summary>
    /// A collection swapped behind a <c>Func</c> registration changes nothing until a refresh reads
    /// it: the copy a lookup answers from is the one its snapshot was built with, which is what
    /// makes the answer consistent rather than a race between the swap and the read (D260 §1).
    /// </summary>
    [Fact]
    public async Task A_collection_swapped_behind_a_func_is_answered_from_the_snapshot()
    {
        var rows = Points.ToList();
        IReadOnlyList<Point> current = rows;
        var source = new PocoSourceBuilder("mem")
            .AddTable("points", () => current, t => t.ClusteredIndex(p => p.Value))
            .Build();

        current = Points.Take(3).ToArray();

        var scanned = await SliceLookupAsync(source, "ix_points_Value", [IndexKeyRange.All], 3, [2]);
        Assert.Equal(
            [1.5, 2.5, 4.5, 5.5, 6.5, 7.5, double.NaN],
            scanned.Select(r => (double)r[0]!));
    }

    // ---- the lookups ----

    /// <summary>
    /// The claim design 34 §5 makes: for every range shape, the copy scan answers exactly what a
    /// permutation index answers over the same rows — the same rows, in the same order, to the value.
    /// </summary>
    [Theory]
    [MemberData(nameof(Ranges))]
    public async Task A_covered_lookup_answers_what_the_permutation_index_answers(string shape)
    {
        var permutation = Source(t => t.Index(p => p.Group, p => p.Rank));
        var clustered = Source(t => t.ClusteredIndex(p => p.Group, p => p.Rank));

        var expected = await SliceLookupAsync(
            permutation, "ix_points_Group_Rank", [Range(shape)], 3, [0, 1, 2]);
        var actual = await SliceLookupAsync(
            clustered, "ix_points_Group_Rank", [Range(shape)], 3, [0, 1, 2]);

        Assert.Equal(Render(expected), Render(actual));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4096)]
    public async Task The_batch_size_does_not_change_the_answer(int batchSize)
    {
        var clustered = Source(t => t.ClusteredIndex(p => p.Group, p => p.Rank));
        var rows = await SliceLookupAsync(
            clustered, "ix_points_Group_Rank", [IndexKeyRange.All], batchSize, [0, 1, 2]);

        Assert.Equal(
            ["a|1|1.5", "a|2|6.5", "a|3|2.5", "b|2|NaN", "b|5|4.5", "c|4|7.5", "c||5.5"],
            Render(rows));
    }

    [Fact]
    public async Task Several_ranges_never_yield_a_row_twice()
    {
        var clustered = Source(t => t.ClusteredIndex(p => p.Group));
        var rows = await SliceLookupAsync(
            clustered,
            "ix_points_Group",
            [IndexKeyRange.Equality("a"), IndexKeyRange.Equality("a"), IndexKeyRange.Equality("c")],
            2,
            [0]);

        Assert.Equal(["a", "a", "a", "c", "c"], rows.Select(r => (string)r[0]!));
    }

    [Fact]
    public async Task A_lookup_reads_exactly_what_it_produces()
    {
        var clustered = Source(t => t.ClusteredIndex(p => p.Group));
        var stats = new ExecutionStats();
        await SliceLookupAsync(
            clustered, "ix_points_Group", [IndexKeyRange.Equality("a")], 2, [0], stats);

        Assert.Equal(3, stats.RowsScanned);
    }

    [Fact]
    public async Task A_projection_outside_the_covering_set_takes_the_positional_path()
    {
        var clustered = Source(t => t.ClusteredIndex(p => p.Group).Covering(p => p.Value));
        var permutation = Source(t => t.Index(p => p.Group));

        // Rank is not in the covering set, so every column is gathered — the positional path, as it
        // is for a permutation index. A projection that is covered takes the copy.
        Assert.StartsWith("PocoIndexScan", ScanKind(clustered, [0, 1, 2]), StringComparison.Ordinal);
        Assert.StartsWith("PocoClusteredScan", ScanKind(clustered, [0, 2]), StringComparison.Ordinal);

        var expected = await LookupAsync(permutation, "ix_points_Group", [IndexKeyRange.All], 3, [0, 1, 2]);
        var actual = await LookupAsync(clustered, "ix_points_Group", [IndexKeyRange.All], 3, [0, 1, 2]);
        Assert.Equal(Render(expected), Render(actual));
    }

    /// <summary>
    /// §4's claim, as a counter rather than a stopwatch: a scan of the copy allocates nothing per
    /// row. The batches are slices of arrays that were built once, so a lookup over twenty thousand
    /// rows costs the same handful of bytes a lookup over one does.
    /// </summary>
    [Fact]
    public async Task A_copy_scan_does_not_allocate_per_row()
    {
        const int rows = 20_000;
        var points = new Point[rows];
        for (var i = 0; i < rows; i++)
        {
            points[i] = new Point($"g{i % 7}", i, i * 0.5);
        }

        var source = new PocoSourceBuilder("mem")
            .AddTable("points", points, t => t.ClusteredIndex(p => p.Value))
            .Build();

        // The first run warms the JIT and the slot; the second is what is measured.
        Assert.Equal(rows, await DrainAsync(source, rows));

        var before = GC.GetAllocatedBytesForCurrentThread();
        var scanned = await DrainAsync(source, rows);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(rows, scanned);
        var perRow = (double)allocated / rows;
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"clustered copy scan: {allocated} bytes for {rows} rows = {perRow:0.0000} bytes/row");
        Assert.True(
            perRow < 1.0,
            $"the copy scan allocated {allocated} bytes for {rows} rows ({perRow:0.0000} per row)");
    }

    // ---- support ----

    private static PocoSource Source(Action<PocoTableBuilder<Point>> configure) =>
        new PocoSourceBuilder("mem").AddTable("points", Points, configure).Build();

    private static IndexDescriptor Descriptor(PocoSource source, string index) =>
        PocoTestSupport.Describe(source, "points").Indexes.Single(
            i => string.Equals(i.Name, index, StringComparison.Ordinal));

    private static ClusteredIndex<Point> Clustered(PocoSource source, string name) =>
        (ClusteredIndex<Point>)Index(source, "points", name);

    private static IPocoIndex<Point> Index(PocoSource source, string table, string name) =>
        source.FindIndex<Point>(table, name)
        ?? throw new InvalidOperationException($"no index '{name}' on '{table}'");

    /// <summary>Which scan the source serves a lookup with, named by the type that owns it.</summary>
    private static string ScanKind(PocoSource source, IReadOnlyList<int> projection)
    {
        var request = LookupRequest(source, "ix_points_Group", [IndexKeyRange.All], 3, projection);
        var scan = ((IColumnarBatchSource)source).ColumnarLookup(request, PocoTestSupport.Context())
            ?? throw new InvalidOperationException("the source declined to serve a columnar lookup");
        return scan.GetType().DeclaringType?.Name ?? scan.GetType().Name;
    }

    /// <summary>One covered column of the copy, straight out of it.</summary>
    private static double[] Doubles(ClusteredIndex<Point> index, int column, int from, int count)
    {
        var view = index.Slice(column, from, count);
        return [.. view.Lanes<double>()];
    }

    private static IndexLookupRequest LookupRequest(
        PocoSource source,
        string index,
        IReadOnlyList<IndexKeyRange> ranges,
        int batchSize,
        IReadOnlyList<int> projection)
    {
        var descriptor = PocoTestSupport.Describe(source, "points");
        return new IndexLookupRequest
        {
            Table = "points",
            Index = index,
            Ranges = ranges,
            Projection = projection,
            OutputSchema = ArrowTypeMapping.ToArrowSchema(descriptor, projection),
            BatchSize = batchSize,
        };
    }

    /// <summary>The columnar path — the one the engine takes, and the one the copy serves.</summary>
    private static async Task<List<object?[]>> SliceLookupAsync(
        PocoSource source,
        string index,
        IReadOnlyList<IndexKeyRange> ranges,
        int batchSize,
        IReadOnlyList<int> projection,
        ExecutionStats? stats = null)
    {
        var request = LookupRequest(source, index, ranges, batchSize, projection);
        var descriptor = PocoTestSupport.Describe(source, "points");
        var types = projection.Select(c => descriptor.Columns[c].Type).ToArray();

        var scan = ((IColumnarBatchSource)source).ColumnarLookup(request, PocoTestSupport.Context(stats))
            ?? throw new InvalidOperationException("the source declined to serve a columnar lookup");

        var rows = new List<object?[]>();
        await using (scan.ConfigureAwait(false))
        {
            var batch = new ColumnarBatch(types);
            while (scan.TryNext(batch))
            {
                for (var r = 0; r < batch.Count; r++)
                {
                    var row = new object?[projection.Count];
                    for (var c = 0; c < projection.Count; c++)
                    {
                        row[c] = Cell(batch.Column(c), r);
                    }

                    rows.Add(row);
                }
            }
        }

        return rows;
    }

    /// <summary>The Arrow path, for the comparison that has to hold between the two.</summary>
    private static async Task<List<object?[]>> LookupAsync(
        PocoSource source,
        string index,
        IReadOnlyList<IndexKeyRange> ranges,
        int batchSize,
        IReadOnlyList<int> projection)
    {
        var request = LookupRequest(source, index, ranges, batchSize, projection);
        var rows = new List<object?[]>();
        await foreach (var batch in source.IndexLookupAsync(
            request, PocoTestSupport.Context(), TestContext.Current.CancellationToken))
        {
            using (batch)
            {
                for (var i = 0; i < batch.Length; i++)
                {
                    rows.Add([.. Enumerable.Range(0, batch.ColumnCount)
                        .Select(c => PocoTestSupport.Read(batch.Column(c), i))]);
                }
            }
        }

        return rows;
    }

    /// <summary>Drains a copy scan into the same slot over and over, counting the rows.</summary>
    private static async Task<int> DrainAsync(PocoSource source, int batchSize)
    {
        var descriptor = PocoTestSupport.Describe(source, "points");
        var request = LookupRequest(source, "ix_points_Value", [IndexKeyRange.All], batchSize, [0, 2]);
        var scan = ((IColumnarBatchSource)source).ColumnarLookup(request, PocoTestSupport.Context())
            ?? throw new InvalidOperationException("the source declined to serve a columnar lookup");

        var batch = new ColumnarBatch([descriptor.Columns[0].Type, descriptor.Columns[2].Type]);
        var scanned = 0;
        await using (scan.ConfigureAwait(false))
        {
            while (scan.TryNext(batch))
            {
                scanned += batch.Count;
            }
        }

        return scanned;
    }

    private static object? Cell(in ColumnView view, int row)
    {
        if (!view.IsValid(row))
        {
            return null;
        }

        return view.Type.Kind switch
        {
            Chalk.Ir.TypeKind.String => System.Text.Encoding.UTF8.GetString(view.VarValue(row)),
            Chalk.Ir.TypeKind.I32 => view.Lanes<int>()[row],
            Chalk.Ir.TypeKind.Fp64 => view.Lanes<double>()[row],
            _ => throw new InvalidOperationException($"unhandled kind {view.Type.Kind}"),
        };
    }

    private static string[] Render(IEnumerable<object?[]> rows) =>
        [.. rows.Select(r => string.Join("|", r.Select(Text)))];

    private static string Text(object? value) => value switch
    {
        null => string.Empty,
        double d => d.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

}

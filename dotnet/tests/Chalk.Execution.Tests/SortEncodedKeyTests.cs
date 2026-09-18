using Apache.Arrow;
using Chalk.Catalog;
using Chalk.Execution.Operators;
using Chalk.Execution.Tests.Harness;
using Chalk.Ir;
using Chalk.Sources;
using Chalk.TestKit;

namespace Chalk.Execution.Tests;

/// <summary>
/// The blocking sort's encoded composite key against the reference executor (D267 b,
/// <c>docs/design/41-blocking-sort.md</c> §2 and §4): randomised rows of every encodable kind, every
/// direction and NULL placement, and each of the three key paths forced through the test hook and
/// asked for the same answer as the comparer that used to give it.
/// </summary>
/// <remarks>
/// <para>
/// The orderings here are deliberately full of ties, so two correct sorts may still return tied rows
/// in different orders — neither sort is stable (§6.3). The comparison is therefore the one
/// <c>05-testing.md</c> §5 makes for a query with no total order: the same multiset of rows, and the
/// <c>ORDER BY</c> honoured by the rows actually returned. Where an ordering ends in the unique
/// <c>id</c> column the order is total, and the strict row-by-row comparison is used instead.
/// </para>
/// <para>
/// No timing is asserted anywhere: the paths are told apart by <see cref="ExecutionStats"/>'s
/// counters, not by how long they took.
/// </para>
/// </remarks>
public sealed class SortEncodedKeyTests
{
    /// <summary>The seed every table here is built from; printed with any failure.</summary>
    private const int Seed = 20260916;

    private const int Rows = 600;

    /// <summary>Column indexes of <see cref="Keys"/>, in the order it declares them.</summary>
    private const int Bool = 0;
    private const int Int8 = 1;
    private const int Int16 = 2;
    private const int Int32 = 3;
    private const int Int64 = 4;
    private const int Float32 = 5;
    private const int Float64 = 6;
    private const int Date = 7;
    private const int Stamp = 8;
    private const int Few = 9;
    private const int Many = 10;
    private const int Binary = 11;
    private const int Decimal = 12;
    private const int Widest = 13;
    private const int Id = 14;

    private static readonly TestTable Keys = BuildKeys(Rows, Seed);

    private static readonly TestTable OneRow = new()
    {
        Name = "one",
        Columns = Keys.Columns,
        Rows = [Keys.Rows[0]],
    };

    private static readonly TestTable NoRows = new()
    {
        Name = "none",
        Columns = Keys.Columns,
        Rows = [],
    };

    private static readonly TestSource Source = TestData.Source(Keys, OneRow, NoRows);

    /// <summary>The row type every table here shares, which is where a key's IR type comes from.</summary>
    private static readonly RowType KeysRow = Keys.RowType();

    public static TheoryData<int> BatchSizes() => new(1, 7, 4096);

    /// <summary>Every kind the composite can encode, and the two it cannot, one key at a time.</summary>
    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task Every_kind_agrees_in_every_direction(int batchSize)
    {
        int[] columns =
        [
            Bool, Int8, Int16, Int32, Int64, Float32, Float64, Date, Stamp, Few, Many, Binary,
            Decimal, Widest, Id,
        ];

        foreach (var column in columns)
        {
            foreach (var (descending, nullsFirst) in Directions)
            {
                await AgreeAsync(
                    Keys,
                    batchSize,
                    $"column {column}, descending {descending}, nulls first {nullsFirst}",
                    [Field(column, descending, nullsFirst)]);
            }
        }
    }

    /// <summary>
    /// The shapes that decide the path: a narrow pair in one word, a wide ordering in two, a long
    /// shared string prefix that only the tie-break can settle, and a DECIMAL first key that nothing
    /// can encode.
    /// </summary>
    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task The_path_follows_the_data(int batchSize)
    {
        // Two small ranges and a boolean: a handful of bits.
        var narrow = await AgreeAsync(
            Keys, batchSize, "narrow", [Field(Int8), Field(Bool), Field(Int16)]);
        Assert.Equal(1, narrow.SortsEncoded64);
        Assert.Equal(0, narrow.SortsTieBroken);

        // A wide range, an instant and a double: more than one word, and nothing given up.
        var wide = await AgreeAsync(
            Keys, batchSize, "wide", [Field(Int64), Field(Stamp), Field(Float64)]);
        Assert.Equal(1, wide.SortsEncoded128);
        Assert.Equal(0, wide.SortsTieBroken);

        // A nullable key whose codes span every bit gives up its lowest one, which ends the
        // composite: the key below it is not packed, and the tie-break settles both.
        var squeezed = await AgreeAsync(
            Keys, batchSize, "squeezed", [Field(Widest), Field(Int32)]);
        Assert.Equal(1, squeezed.SortsEncoded64);
        Assert.Equal(1, squeezed.SortsTieBroken);

        // Distinct values beyond the rank cap, every one of them sharing its first eight bytes.
        var prefix = await AgreeAsync(Keys, batchSize, "prefix", [Field(Many)]);
        Assert.Equal(1, prefix.SortsEncoded64);
        Assert.Equal(1, prefix.SortsTieBroken);

        // A DECIMAL first key has no code at all, so the composite is empty.
        var undecodable = await AgreeAsync(Keys, batchSize, "decimal", [Field(Decimal)]);
        Assert.Equal(1, undecodable.SortsCompared);

        // A DECIMAL below an encodable key is a tie-break, not a comparer sort.
        var tail = await AgreeAsync(
            Keys, batchSize, "int32 then decimal", [Field(Int32), Field(Decimal)]);
        Assert.Equal(1, tail.SortsEncoded64);
        Assert.Equal(1, tail.SortsTieBroken);

        // Few enough short distinct values: the symbol rank, exactly and with no tie-break.
        var ranked = await AgreeAsync(Keys, batchSize, "ranked", [Field(Few), Field(Int32)]);
        Assert.Equal(1, ranked.SortsEncoded64);
        Assert.Equal(0, ranked.SortsTieBroken);
    }

    /// <summary>
    /// The same ordering forced onto each path in turn. Every one of them must return what the
    /// comparer returns, which is what makes the encoding a performance change and nothing else.
    /// </summary>
    [Theory]
    [InlineData(0, 1)]
    [InlineData(0, 4096)]
    [InlineData(6, 7)]
    [InlineData(6, 4096)]
    [InlineData(64, 7)]
    [InlineData(64, 4096)]
    [InlineData(128, 1)]
    [InlineData(128, 4096)]
    public async Task A_forced_path_answers_as_the_comparer_does(int maxBits, int batchSize)
    {
        SortField[] ordering =
        [
            Field(Few), Field(Stamp, descending: true), Field(Float64), Field(Int64), Field(Id),
        ];

        var previous = SortKeyComposite.MaxBits;
        try
        {
            SortKeyComposite.MaxBits = maxBits;
            var stats = await AgreeAsync(
                Keys, batchSize, $"max bits {maxBits}", ordering, totalOrder: true);

            // The ordering ends in `id`, so the encoded answer is the comparer's answer row for row.
            Assert.Equal(
                maxBits == 0 ? 1 : 0,
                stats.SortsCompared);
            Assert.Equal(maxBits is > 0 and <= 64 ? 1 : 0, stats.SortsEncoded64);
            Assert.Equal(maxBits > 64 ? 1 : 0, stats.SortsEncoded128);
            Assert.Equal(maxBits is 6 or 64 ? 1 : 0, stats.SortsTieBroken);
        }
        finally
        {
            SortKeyComposite.MaxBits = previous;
        }
    }

    /// <summary>Both ways of sorting a two-word composite must agree, so the faster one may be kept.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Both_two_word_sorts_agree(bool radix)
    {
        var mode = radix ? SortWide128.Radix : SortWide128.RunsByLowWord;
        var previous = SortKeyComposite.Wide128;
        try
        {
            SortKeyComposite.Wide128 = mode;
            var stats = await AgreeAsync(
                Keys,
                4096,
                $"{mode}",
                [Field(Int64), Field(Stamp), Field(Float64), Field(Id)],
                totalOrder: true);
            Assert.Equal(1, stats.SortsEncoded128);
        }
        finally
        {
            SortKeyComposite.Wide128 = previous;
        }
    }

    /// <summary>No rows, one row, and a batch larger than the input.</summary>
    [Theory]
    [MemberData(nameof(BatchSizes))]
    public async Task The_edges_agree(int batchSize)
    {
        SortField[] ordering = [Field(Few), Field(Int64, descending: true), Field(Id)];
        await AgreeAsync(NoRows, batchSize, "no rows", ordering, totalOrder: true);
        await AgreeAsync(OneRow, batchSize, "one row", ordering, totalOrder: true);
        await AgreeAsync(Keys, Rows * 2, "one batch", ordering, totalOrder: true);
    }

    /// <summary>
    /// The same orderings over a concatenation split into chunks (design 41 §3). A chunk is as many
    /// rows as its byte budget buys, so the budget is lowered here to reach over the input's six
    /// hundred rows what a real one reaches over a few hundred thousand: every key is encoded across
    /// chunk boundaries and every gather crosses them.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    public async Task A_chunked_concatenation_agrees(int batchSize)
    {
        var previous = SortOperator.ChunkBytes;
        SortOperator.ChunkBytes = 512;
        try
        {
            (string What, SortField[] Ordering)[] cases =
            [
                ("ranked", [Field(Few), Field(Int32)]),
                ("prefix", [Field(Many)]),
                ("two words", [Field(Int64), Field(Stamp), Field(Float64), Field(Id)]),
                ("comparer", [Field(Decimal)]),
                ("descending, nulls first", [Field(Few, true, true), Field(Binary, true, true)]),
            ];

            foreach (var (what, ordering) in cases)
            {
                var stats = await AgreeAsync(
                    Keys, batchSize, $"chunked {what}", ordering, estimate: 8);
                Assert.Equal(1, stats.SortsCompared + stats.SortsEncoded64 + stats.SortsEncoded128);
            }
        }
        finally
        {
            SortOperator.ChunkBytes = previous;
        }
    }

    private static IEnumerable<(bool Descending, bool NullsFirst)> Directions =>
        [(false, false), (false, true), (true, false), (true, true)];

    private static SortField Field(int column, bool descending = false, bool nullsFirst = false) =>
        descending
            ? IrBuilder.Desc(column, KeysRow.Fields[column].Type, nullsFirst)
            : IrBuilder.Asc(column, KeysRow.Fields[column].Type, nullsFirst);

    private static async Task<ExecutionStats> AgreeAsync(
        TestTable table,
        int batchSize,
        string what,
        SortField[] ordering,
        bool totalOrder = false,
        double estimate = 0)
    {
        var row = table.RowType();
        var plan = IrBuilder.Plan(
            IrBuilder.Sort(IrBuilder.Read(table.Name, row, rows: estimate), ordering));

        var stats = new ExecutionStats();
        var vectorised = await Runner.RunAsync(plan, Source, batchSize, stats: stats);
        var expected = await Runner.RunAsync(plan, Source, batchSize, reference: true);
        try
        {
            ResultComparer.AssertEquivalent(
                expected,
                vectorised,
                new ResultComparisonOptions
                {
                    CompareAsMultiset = !totalOrder,
                    OrderKeys =
                    [
                        .. ordering.Select(f => new OrderKeyExpectation(
                            (int)f.Expr.FieldRef.Index,
                            f.Direction is SortDirection.DescNullsFirst or SortDirection.DescNullsLast,
                            f.Direction is SortDirection.AscNullsFirst or SortDirection.DescNullsFirst)),
                    ],
                });
        }
        catch (Exception failure)
        {
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"{what} disagreed (seed {Seed}, {table.Rows.Count} rows, batch {batchSize}): "
                + failure.Message);
            throw;
        }
        finally
        {
            foreach (var batch in vectorised.Concat(expected))
            {
                batch.Dispose();
            }
        }

        return stats;
    }

    /// <summary>
    /// One randomised table of every kind a key can be. Small value domains on purpose: the point is
    /// ties, NULLs on both sides of every comparison, and the values an encoding is most likely to
    /// get wrong — the minimum of each integer, both zeroes and NaN, the empty string.
    /// </summary>
    private static TestTable BuildKeys(int rows, int seed)
    {
        var random = new Random(seed);
        string[] few = ["AAA", "BBB", "CCC", "aaa", ""];
        long[] extremes =
            [long.MinValue, long.MaxValue, 0, -1, 1, int.MinValue, int.MaxValue];
        double[] doubles =
            [0d, -0d, 1.5d, -1.5d, double.NaN, double.PositiveInfinity, double.NegativeInfinity, 1e308];
        float[] floats =
            [0f, -0f, 1.5f, -1.5f, float.NaN, float.PositiveInfinity, float.NegativeInfinity, 1e38f];

        var data = new List<object?[]>(rows);
        for (var i = 0; i < rows; i++)
        {
            data.Add(
            [
                Maybe(random, () => random.Next(2) == 0),
                Maybe(random, () => (long)random.Next(-3, 4)),
                Maybe(random, () => (long)random.Next(-300, 301)),
                Maybe(random, () => (long)random.Next(-70_000, 70_001)),
                Maybe(random, () => random.NextInt64(-5_000_000_000L, 5_000_000_000L)),
                Maybe(random, () => floats[random.Next(floats.Length)]),
                Maybe(random, () => doubles[random.Next(doubles.Length)]),
                Maybe(random, () => (long)random.Next(19_000, 20_001)),
                Maybe(random, () => 1_767_413_106_000_007L + random.Next(-1_000, 1_001)),
                Maybe(random, () => few[random.Next(few.Length)]),

                // Every value shares its first eight bytes and is longer than an image, so the
                // prefix cannot separate any two of them and the rank cannot hold them.
                Maybe(random, () => "prefix--shared--" + random.Next(rows).ToString("D6", null)),
                Maybe(random, () => new byte[] { (byte)random.Next(4), (byte)random.Next(4) }),
                Maybe(random, () => Math.Round((decimal)random.NextDouble() * 100m, 4)),

                // Both ends of the range, so the codes span every bit there is and a NULL has no
                // code of its own left: the one shape that gives up a bit of the value.
                Maybe(random, () => random.Next(3) == 0
                    ? extremes[random.Next(extremes.Length)]
                    : random.NextInt64()),
                (long)i,
            ]);
        }

        return new TestTable
        {
            Name = "keys",
            Columns =
            [
                ("b", ChalkType.Bool(nullable: true)),
                ("i8", ChalkType.Int8(nullable: true)),
                ("i16", ChalkType.Int16(nullable: true)),
                ("i32", ChalkType.Int32(nullable: true)),
                ("i64", ChalkType.Int64(nullable: true)),
                ("f32", ChalkType.Float32(nullable: true)),
                ("f64", ChalkType.Float64(nullable: true)),
                ("d", ChalkType.Date(nullable: true)),
                ("ts", ChalkType.Timestamp(6, nullable: true)),
                ("few", ChalkType.String(nullable: true)),
                ("many", ChalkType.String(nullable: true)),
                ("bin", ChalkType.Binary(nullable: true)),
                ("dec", ChalkType.Decimal(18, 4, nullable: true)),
                ("widest", ChalkType.Int64(nullable: true)),
                ("id", ChalkType.Int64()),
            ],
            Rows = data,
        };
    }

    /// <summary>One value in seven is NULL, so every key has NULLs on both sides of a comparison.</summary>
    private static object? Maybe<T>(Random random, Func<T> value) =>
        random.Next(7) == 0 ? null : value();
}

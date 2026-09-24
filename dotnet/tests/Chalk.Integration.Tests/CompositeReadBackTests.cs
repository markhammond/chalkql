using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Apache.Arrow;
using Apache.Arrow.Types;
using Chalk.Arrow;
using Chalk.Catalog;
using Chalk.Client;
using Chalk.Sources;
using Chalk.Sources.Poco;
using Chalk.TestKit;
using Chalk.Tests;
using ArrowField = Apache.Arrow.Field;

namespace Chalk.Integration.Tests;

/// <summary>
/// The typed read back of a composite cell (D301): <c>GetComposite&lt;T&gt;</c> and
/// <c>TryGetComposite&lt;T&gt;</c> over what the engine hands back, in both string layouts; the
/// widened Tier 1 spellings a field is read in; the binding compiled once per (T, struct type); the
/// refusals, each naming both sides; and what a read costs, which for a record struct of value and
/// <c>Utf8String</c> fields is nothing.
/// </summary>
[Collection(SidecarCollection.Name)]
public sealed class CompositeReadBackTests(SharedSidecar sidecar)
{
    /// <summary>The owner's record.</summary>
    public readonly record struct Classification(Utf8String Category, double Confidence);

    /// <summary>The same fields as a class a host fills through setters, names in another case.</summary>
    public sealed class ClassificationView
    {
        public string? CATEGORY { get; set; }

        public double confidence { get; set; }
    }

    /// <summary>Read only by the cache test, so its binding count is that test's alone.</summary>
    public readonly record struct Counted(Utf8String Category, double Confidence);

    private sealed record Transaction(long Id, Utf8String Description, double? Amount);

    private static readonly Utf8String Large = "large"u8.ToArray();
    private static readonly Utf8String Small = "small"u8.ToArray();

    private static readonly Transaction[] Transactions =
    [
        .. Enumerable.Range(0, 50).Select(i => new Transaction(
            i, i % 2 == 0 ? Large : Small, i % 5 == 4 ? null : i * 7.5)),
    ];

    private static Classification Classify(double amount) =>
        new(amount >= 100 ? Large : Small, amount / 1000);

    // ---------------------------------------------------------------- through the engine

    [Theory]
    [InlineData(StringLayouts.Utf8View)]
    [InlineData(StringLayouts.Utf8)]
    public async Task The_owner_s_record_reads_back_whole_in_either_string_layout(StringLayouts strings)
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        await using var engine = await ClassifyingAsync(strings, batchSize: 4096);
        var seen = 0;
        await ForEachRowAsync(engine, (batch, row) =>
        {
            var id = ((Int64Array)batch.Column(0)).GetValue(row)!.Value;
            var c = batch.Column(1);
            var category = ((StructType)c.Data.DataType).Fields[0].DataType;
            Assert.IsType(strings == StringLayouts.Utf8View ? typeof(StringViewType) : typeof(StringType), category);

            if (Transactions[id].Amount is not { } amount)
            {
                Assert.False(c.TryGetComposite<Classification>(row, out var none));
                Assert.Equal(default, none);
                Assert.Null(c.GetComposite<Classification?>(row));
                Assert.Null(c.GetComposite<ClassificationView>(row));
                var refusal = Assert.Throws<InvalidOperationException>(() => c.GetComposite<Classification>(row));
                Assert.Equal(
                    $"Row {row} holds a NULL composite, which Classification cannot hold. Read it with "
                    + "TryGetComposite, or as Classification?.",
                    refusal.Message);
            }
            else
            {
                var expected = Classify(amount);
                Assert.True(c.TryGetComposite<Classification>(row, out var read));
                Assert.Equal(expected, read);
                Assert.Equal(expected, c.GetComposite<Classification>(row));
                Assert.Equal(expected, c.GetComposite<Classification?>(row));
                var view = c.GetComposite<ClassificationView>(row)!;
                Assert.Equal(expected.Category.ToString(), view.CATEGORY);
                Assert.Equal(expected.Confidence, view.confidence);
            }

            seen++;
        });

        Assert.Equal(Transactions.Length, seen);
    }

    /// <summary>A price and the day and instant it was set, declared narrower than the record reads.</summary>
    public readonly record struct Priced(decimal Amount, DateOnly Day, DateTime At);

    /// <summary>The same fields in their raw spellings: days, and microseconds since 1970.</summary>
    public readonly record struct PricedRaw(decimal Amount, int Day, long At);

    private sealed record Posting(
        long Id,
        [property: ChalkColumn(Precision = 18, Scale = 2)] decimal Amount,
        DateOnly Effective);

    private static Priced Price(decimal amount, DateOnly day) =>
        new(amount * 2, day.AddDays(1), day.ToDateTime(new TimeOnly(12, 30, 15, 250, 125)));

    /// <summary>
    /// A composite declared as <c>COMPOSITE(Amount DECIMAL(18,2), Day DATE, At TIMESTAMP(6))</c> reads
    /// back as the delegate wrote it: <c>decimal</c>, <c>DateOnly</c> and <c>DateTime</c>, or the raw
    /// counts, as a delegate's parameters may be spelled.
    /// </summary>
    [Fact]
    public async Task A_narrower_decimal_a_date_and_a_timestamp_read_back_as_the_host_spells_them()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        Posting[] postings =
        [
            .. Enumerable.Range(0, 40).Select(i => new Posting(i, i * 1.25m, new DateOnly(2024, 2, 1).AddDays(i))),
        ];
        var priced = ChalkType.Composite(
        [
            new CompositeField("Amount", ChalkType.Decimal(18, 2)),
            new CompositeField("Day", ChalkType.Date()),
            new CompositeField("At", ChalkType.Timestamp(6)),
        ]);
        var source = new PocoSourceBuilder("mem")
            .AddTable("postings", postings)
            .AddFunction("price_on", f => f
                .Scalar()
                .Parameter("amount", ChalkType.Decimal(18, 2))
                .Parameter<DateOnly>("effective")
                .Returns(priced)
                .Strict()
                .Client())
            .Build();
        await using var engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "read-back-widened",
            Sources = [source],
            Functions = registry => registry.AddScalar<decimal, DateOnly, Priced>("price_on", Price),
            Planner = sidecar.CreatePlanner(),
        });

        var prepared = await engine.PrepareAsync(
            "SELECT id, price_on(amount, effective) AS p FROM postings ORDER BY id");
        var seen = 0;
        await using var execution = await engine.ExecuteAsync(prepared, (IReadOnlyList<object?>?)null);
        await foreach (var batch in execution.Batches)
        {
            using (batch)
            {
                var p = batch.Column(1);
                for (var row = 0; row < batch.Length; row++)
                {
                    var posting = postings[((Int64Array)batch.Column(0)).GetValue(row)!.Value];
                    var expected = Price(posting.Amount, posting.Effective);
                    Assert.Equal(expected, p.GetComposite<Priced>(row));

                    var raw = p.GetComposite<PricedRaw>(row);
                    Assert.Equal(expected.Amount, raw.Amount);
                    Assert.Equal(expected.Day.DayNumber - DateOnly.FromDateTime(DateTime.UnixEpoch).DayNumber, raw.Day);
                    Assert.Equal((expected.At.Ticks - DateTime.UnixEpoch.Ticks) / 10, raw.At);
                    seen++;
                }
            }
        }

        Assert.Equal(postings.Length, seen);
    }

    /// <summary>
    /// The binding is compiled once per (T, struct type): every batch of an execution, a second
    /// execution and a second prepare of the same shape reuse it, and only another shape — the other
    /// string layout — compiles another.
    /// </summary>
    [Fact]
    public async Task The_binding_is_compiled_once_per_type_and_shape()
    {
        Assert.SkipWhen(!sidecar.Sidecar.IsAvailable, sidecar.SkipReason ?? string.Empty);

        var before = CompositeReader<Counted>.Bindings;
        var batches = 0;
        foreach (var strings in new[] { StringLayouts.Utf8View, StringLayouts.Utf8View, StringLayouts.Utf8 })
        {
            // Eight rows a batch: seven batches an execution, each with a struct type of its own.
            await using var engine = await ClassifyingAsync(strings, batchSize: 8);
            for (var execution = 0; execution < 2; execution++)
            {
                await ForEachRowAsync(engine, (batch, row) =>
                {
                    batches += row == 0 ? 1 : 0;
                    batch.Column(1).TryGetComposite<Counted>(row, out _);
                });
            }
        }

        Assert.Equal(3 * 2 * 7, batches);
        Assert.Equal(2, CompositeReader<Counted>.Bindings - before);
    }

    private async ValueTask<ChalkEngine> ClassifyingAsync(StringLayouts strings, int batchSize)
    {
        var source = new PocoSourceBuilder("mem")
            .AddTable("transactions", Transactions)
            .AddFunction("classify_transaction", f => f
                .Scalar<Utf8String, double, Classification>("description", "amount")
                .Strict()
                .Client())
            .Build();

        return await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "read-back",
            Sources = [source],
            Functions = registry => registry.AddScalar<Utf8String, double, Classification>(
                "classify_transaction", static (_, amount) => Classify(amount)),
            Output = new OutputOptions { Strings = strings },
            Execution = new ExecutionOptions { BatchSize = batchSize },
            Planner = sidecar.CreatePlanner(),
        });
    }

    private static async Task ForEachRowAsync(ChalkEngine engine, Action<RecordBatch, int> visit)
    {
        var prepared = await engine.PrepareAsync(
            "SELECT id, classify_transaction(description, amount) AS c FROM transactions ORDER BY id");
        await using var execution = await engine.ExecuteAsync(prepared, (IReadOnlyList<object?>?)null);
        await foreach (var batch in execution.Batches)
        {
            using (batch)
            {
                for (var row = 0; row < batch.Length; row++)
                {
                    visit(batch, row);
                }
            }
        }
    }

    // ---------------------------------------------------------------- over hand-built arrays

    /// <summary>Every widened Tier 1 spelling a field reads back in, and its nullable form over a NULL.</summary>
    public sealed record Everything(
        bool Flag,
        sbyte Tiny,
        short Small,
        int Medium,
        long Large,
        float Single,
        double Double,
        decimal Amount,
        Utf8String Name,
        string? Label,
        ReadOnlyMemory<byte> Blob,
        byte[]? Bytes,
        DateOnly Day,
        TimeOnly At,
        DateTime Stamp,
        DateTimeOffset Instant,
        TimeSpan Span,
        Guid Key,
        int? MaybeMedium,
        Utf8String? MaybeName,
        DateOnly? MaybeDay);

    [Fact]
    public void Every_widened_spelling_reads_back_from_a_hand_built_composite()
    {
        var day = new DateOnly(2024, 3, 9);
        var stamp = new DateTime(2024, 3, 9, 8, 7, 6, DateTimeKind.Unspecified).AddTicks(5_430);
        var instant = new DateTimeOffset(2024, 3, 9, 8, 7, 6, TimeSpan.Zero).AddTicks(123);
        var key = Guid.Parse("0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0");
        var epochDays = DateOnly.FromDateTime(DateTime.UnixEpoch).DayNumber;

        var composite = Struct(
            2,
            validity: null,
            ("Flag", BooleanType.Default, false, new BooleanArray.Builder().Append(true).Append(false).Build()),
            ("Tiny", Int8Type.Default, false, Fixed<sbyte>(Int8Type.Default, [-7, 7])),
            ("Small", Int16Type.Default, false, Fixed<short>(Int16Type.Default, [-300, 300])),
            ("Medium", Int32Type.Default, false, Fixed<int>(Int32Type.Default, [70_000, -70_000])),
            ("Large", Int64Type.Default, false, Fixed<long>(Int64Type.Default, [1L << 40, -(1L << 40)])),
            ("Single", FloatType.Default, false, Fixed<float>(FloatType.Default, [1.5f, -1.5f])),
            ("Double", DoubleType.Default, false, Fixed<double>(DoubleType.Default, [2.25, -2.25])),
            ("Amount", new Decimal128Type(18, 2), false, Decimals(new Decimal128Type(18, 2), [12.34m, -0.05m])),
            ("Name", StringViewType.Default, false, new StringViewArray.Builder().Append("a name longer than twelve").Append("short").Build()),
            ("Label", StringType.Default, true, new StringArray.Builder().Append("label").AppendNull().Build()),
            ("Blob", BinaryType.Default, false, new BinaryArray.Builder().Append((ReadOnlySpan<byte>)[1, 2, 3]).Append((ReadOnlySpan<byte>)[]).Build()),
            ("Bytes", BinaryType.Default, true, new BinaryArray.Builder().AppendNull().Append((ReadOnlySpan<byte>)[9]).Build()),
            ("Day", Date32Type.Default, false, Fixed<int>(Date32Type.Default, [day.DayNumber - epochDays, 0])),
            ("At", new Time64Type(TimeUnit.Microsecond), false, Fixed<long>(new Time64Type(TimeUnit.Microsecond), [3_600_000_001L, 0])),
            ("Stamp", new TimestampType(TimeUnit.Microsecond, (string?)null), false,
                Fixed<long>(new TimestampType(TimeUnit.Microsecond, (string?)null), [(stamp.Ticks - DateTime.UnixEpoch.Ticks) / 10, 0])),
            ("Instant", new TimestampType(TimeUnit.Nanosecond, "UTC"), false,
                Fixed<long>(new TimestampType(TimeUnit.Nanosecond, "UTC"), [(instant.UtcTicks - DateTime.UnixEpoch.Ticks) * 100 + 7, 0])),
            ("Span", DurationType.Microsecond, false, Fixed<long>(DurationType.Microsecond, [90_000_000L, -1L])),
            ("Key", new FixedSizeBinaryType(16), false, Uuids([key, Guid.Empty])),
            ("MaybeMedium", Int32Type.Default, true, Fixed<int>(Int32Type.Default, [5, null])),
            ("MaybeName", StringType.Default, true, new StringArray.Builder().AppendNull().Append("named").Build()),
            ("MaybeDay", Date32Type.Default, true, Fixed<int>(Date32Type.Default, [null, 1])));

        var first = composite.GetComposite<Everything>(0);
        Assert.True(first.Flag);
        Assert.Equal(-7, first.Tiny);
        Assert.Equal(-300, first.Small);
        Assert.Equal(70_000, first.Medium);
        Assert.Equal(1L << 40, first.Large);
        Assert.Equal(1.5f, first.Single);
        Assert.Equal(2.25, first.Double);
        Assert.Equal(12.34m, first.Amount);
        Assert.Equal("a name longer than twelve", first.Name.ToString());
        Assert.Equal("label", first.Label);
        Assert.Equal([1, 2, 3], first.Blob.ToArray());
        Assert.Null(first.Bytes);
        Assert.Equal(day, first.Day);
        Assert.Equal(new TimeOnly(1, 0, 0).Add(TimeSpan.FromTicks(10)), first.At);
        Assert.Equal(stamp, first.Stamp);
        Assert.Equal(DateTimeKind.Unspecified, first.Stamp.Kind);
        Assert.Equal(instant, first.Instant);
        Assert.Equal(TimeSpan.Zero, first.Instant.Offset);
        Assert.Equal(TimeSpan.FromSeconds(90), first.Span);
        Assert.Equal(key, first.Key);
        Assert.Equal(5, first.MaybeMedium);
        Assert.Null(first.MaybeName);
        Assert.Null(first.MaybeDay);

        var second = composite.GetComposite<Everything>(1);
        Assert.False(second.Flag);
        Assert.Equal(-0.05m, second.Amount);
        Assert.Equal("short", second.Name.ToString());
        Assert.Null(second.Label);
        Assert.Empty(second.Blob.ToArray());
        Assert.Equal([9], second.Bytes);
        Assert.Equal(DateOnly.FromDateTime(DateTime.UnixEpoch), second.Day);
        Assert.Equal(TimeSpan.FromTicks(-10), second.Span);
        Assert.Equal(Guid.Empty, second.Key);
        Assert.Null(second.MaybeMedium);
        Assert.Equal("named", second.MaybeName!.Value.ToString());
        Assert.Equal(DateOnly.FromDateTime(DateTime.UnixEpoch).AddDays(1), second.MaybeDay);
    }

    [Fact]
    public void A_slice_reads_the_rows_of_its_slice_and_a_null_slot_is_null()
    {
        var composite = Classifications(10, nullEvery: 4);
        var slice = composite.Slice(3, 5);

        for (var row = 0; row < slice.Length; row++)
        {
            var original = row + 3;
            if (original % 4 == 0)
            {
                Assert.False(slice.TryGetComposite<Classification>(row, out _));
                continue;
            }

            Assert.Equal(
                new Classification(Encoded($"c{original}"), original / 10.0),
                slice.GetComposite<Classification>(row));
        }
    }

    public readonly record struct Missing(Utf8String Category, double Score);

    public readonly record struct WrongType(Utf8String Category, long Confidence);

    public readonly record struct Wide(decimal Amount);

    public sealed class Unbuildable
    {
        public Unbuildable(int unrelated) => _ = unrelated;

        public double Confidence { get; }
    }

    [Fact]
    public void A_mismatch_is_refused_on_the_first_call_naming_both_sides()
    {
        var composite = Classifications(3, nullEvery: 0);
        const string described = "COMPOSITE(Category STRING, Confidence FP64)";

        Assert.Equal(
            $"Missing cannot be read from the composite {described}: 'Score' of Missing names none of its fields.",
            Assert.Throws<InvalidOperationException>(() => composite.GetComposite<Missing>(0)).Message);
        Assert.Equal(
            $"WrongType cannot be read from the composite {described}: field 'Confidence' is FP64 (CLR Double) "
            + "and 'Confidence' of WrongType is Int64, which does not read it.",
            Assert.Throws<InvalidOperationException>(() => composite.GetComposite<WrongType>(0)).Message);
        Assert.Equal(
            $"Int32 cannot be read from the composite {described}: Int32 is not a record a composite value "
            + "can be read into.",
            Assert.Throws<InvalidOperationException>(() => composite.GetComposite<int>(0)).Message);
        Assert.Equal(
            $"Unbuildable cannot be read from the composite {described}: Unbuildable can be built neither "
            + "through a constructor whose parameters are its properties nor through a public parameterless "
            + "constructor and settable properties.",
            Assert.Throws<InvalidOperationException>(() => composite.GetComposite<Unbuildable>(0)).Message);

        // Refused the same way on every call after the first, not only on the first.
        Assert.Throws<InvalidOperationException>(() => composite.GetComposite<Missing>(1));

        var nullable = Struct(
            1,
            validity: null,
            ("Category", StringViewType.Default, false, new StringViewArray.Builder().Append("x").Build()),
            ("Confidence", DoubleType.Default, true, Fixed<double>(DoubleType.Default, [null])));
        Assert.Equal(
            "Classification cannot be read from the composite COMPOSITE(Category STRING, Confidence FP64?): "
            + "field 'Confidence' is nullable and 'Confidence' of Classification is Double, which cannot "
            + "hold its NULL; declare it Double?.",
            Assert.Throws<InvalidOperationException>(() => nullable.GetComposite<Classification>(0)).Message);

        var wide = Struct(
            1,
            validity: null,
            ("Amount", new Decimal128Type(38, 4), false, Decimals(new Decimal128Type(38, 4), [1m])));
        Assert.Equal(
            "Wide cannot be read from the composite COMPOSITE(Amount DECIMAL(38,4)): field 'Amount' is "
            + "DECIMAL(38,4), which a CLR decimal cannot hold (it holds 28 digits), and 'Amount' of Wide is "
            + "Decimal; read this field from its own Arrow array.",
            Assert.Throws<InvalidOperationException>(() => wide.GetComposite<Wide>(0)).Message);

        var twice = Struct(
            1,
            validity: null,
            ("category", StringViewType.Default, false, new StringViewArray.Builder().Append("x").Build()),
            ("CATEGORY", StringViewType.Default, false, new StringViewArray.Builder().Append("y").Build()),
            ("Confidence", DoubleType.Default, false, Fixed<double>(DoubleType.Default, [1.0])));
        Assert.Equal(
            "Classification cannot be read from the composite struct<category, CATEGORY, Confidence>: its "
            + "fields 'category' and 'CATEGORY' are one name ignoring case, which is how a field is matched, "
            + "so no member can say which it reads.",
            Assert.Throws<InvalidOperationException>(() => twice.GetComposite<Classification>(0)).Message);

        var notComposite = Fixed<int>(Int32Type.Default, [1]);
        Assert.Equal(
            "Expected an Arrow struct array — a composite column — and got int32. (Parameter 'array')",
            Assert.Throws<ArgumentException>(() => notComposite.GetComposite<Classification>(0)).Message);
        Assert.Throws<ArgumentOutOfRangeException>(() => composite.GetComposite<Classification>(3));
    }

    /// <summary>
    /// A record struct of value and <c>Utf8String</c> fields costs nothing to read: the binding is
    /// asked by reference, the fields are read from the child buffers, and the string is a slice.
    /// </summary>
    [Fact]
    public async Task Reading_a_record_struct_allocates_nothing_per_row()
    {
        const int rows = 4096;
        var composite = Classifications(rows, nullEvery: 7);
        double Read()
        {
            var total = 0.0;
            for (var row = 0; row < rows; row++)
            {
                if (composite.TryGetComposite<Classification>(row, out var value))
                {
                    total += value.Confidence + value.Category.Length;
                }
            }

            return total;
        }

        // A Task<bool> is one the runtime caches, so the window holds the reads and nothing else.
        var total = 0.0;
        var (bytes, read) = await AllocationProbe.SteadyStateAsync(() =>
        {
            total = Read();
            return Task.FromResult(total > 0);
        });

        Assert.True(read);
        Assert.Equal(0, bytes);

        // And the probe sees what a read does allocate: a record class is an object a row.
        var (classBytes, _) = await AllocationProbe.SteadyStateAsync(() =>
        {
            var count = 0;
            for (var row = 0; row < rows; row++)
            {
                count += composite.GetComposite<ClassificationView>(row) is null ? 0 : 1;
            }

            return Task.FromResult(count > 0);
        });
        Assert.True(classBytes >= rows / 7 * 6 * 24, $"{classBytes} bytes for {rows} reads of a class");
    }

    // ---------------------------------------------------------------- the batch read

    /// <summary>A record struct of a <c>Utf8String</c> and value fields: what a batch reads without allocating.</summary>
    public readonly record struct Priced2(Utf8String Category, double Confidence, long Rank);

    /// <summary>The same with a <c>string</c> field, which allocates a copy per row and is measured apart.</summary>
    public readonly record struct Labelled(string? Category, double Confidence);

    [Fact]
    public void A_batch_reads_what_each_cell_reads_nulls_included()
    {
        var composite = Classifications(40, nullEvery: 6);

        var nullable = new Classification?[composite.Length];
        composite.ReadComposites<Classification?>(nullable);
        var views = new ClassificationView?[composite.Length];
        composite.ReadComposites<ClassificationView?>(views);

        for (var row = 0; row < composite.Length; row++)
        {
            Assert.Equal(composite.GetComposite<Classification?>(row), nullable[row]);
            Assert.Equal(row % 6 == 0, nullable[row] is null);
            Assert.Equal(nullable[row]?.Category.ToString(), views[row]?.CATEGORY);
        }

        // From a row other than the first, into a span shorter than the rest.
        var middle = new Classification?[5];
        composite.ReadComposites<Classification?>(13, middle);
        Assert.Equal(nullable.AsSpan(13, 5).ToArray(), middle);
    }

    [Fact]
    public void A_batch_read_of_a_slice_reads_the_slice_s_rows_in_both_string_layouts()
    {
        foreach (var views in new[] { true, false })
        {
            var composite = Classifications(2500, nullEvery: 0, views);
            var slice = (StructArray)composite.Slice(1031, 1200);
            Assert.Equal(1031, slice.Offset);

            var read = new Classification[slice.Length];
            slice.ReadComposites<Classification>(read);
            for (var row = 0; row < read.Length; row++)
            {
                var original = row + 1031;
                Assert.Equal(new Classification(Encoded($"c{original}"), original / 10.0), read[row]);
            }

            // And a later start inside the slice, across a chunk boundary.
            var tail = new Classification[100];
            slice.ReadComposites<Classification>(1000, tail);
            Assert.Equal(new Classification(Encoded("c2031"), 203.1), tail[0]);
        }
    }

    [Fact]
    public void A_batch_into_a_struct_that_cannot_hold_null_is_refused_at_its_first_null_row()
    {
        var composite = Classifications(10, nullEvery: 4);
        var into = new Classification[6];

        var refusal = Assert.Throws<InvalidOperationException>(() => composite.ReadComposites<Classification>(1, into));
        Assert.Equal(
            "Row 4 holds a NULL composite, which Classification cannot hold. Read it with TryGetComposite, or "
            + "as Classification?.",
            refusal.Message);
        Assert.Throws<ArgumentOutOfRangeException>(() => composite.ReadComposites<Classification?>(5, new Classification?[6]));
    }

    /// <summary>
    /// The batch gate: 4096 rows of a record struct of a <c>Utf8String</c> and value fields read into a
    /// span cost nothing, the attach, the binder call and the copy included. A <c>string</c> field is
    /// the one allowed exception — a copy per row — and is measured apart, so the probe is seen to see.
    /// </summary>
    [Fact]
    public async Task Reading_a_batch_into_a_span_allocates_nothing_per_row()
    {
        const int rows = 4096;
        var composite = Ranked(rows);
        var into = new Priced2[rows];
        var labelled = new Labelled[rows];

        var (bytes, read) = await AllocationProbe.SteadyStateAsync(() =>
        {
            composite.ReadComposites<Priced2>(into);
            return Task.FromResult(into[rows - 1].Rank == rows - 1);
        });
        var (stringBytes, _) = await AllocationProbe.SteadyStateAsync(() =>
        {
            composite.ReadComposites<Labelled>(labelled);
            return Task.FromResult(labelled[0].Category is not null);
        });

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"a batch of {rows}: {bytes} bytes with a Utf8String field, {stringBytes} bytes "
            + $"({stringBytes / (double)rows:0.#} a row) with a string field");
        Assert.True(read);
        Assert.Equal(0, bytes);
        Assert.True(stringBytes >= rows * 24L, $"{stringBytes} bytes for {rows} strings");
    }

    // ---------------------------------------------------------------- building arrays

    private static Utf8String Encoded(string text) => System.Text.Encoding.UTF8.GetBytes(text);

    /// <summary>
    /// A COMPOSITE(Category STRING, Confidence FP64) of <paramref name="rows"/> rows, a NULL composite
    /// every <paramref name="nullEvery"/>, the strings in the view layout or the classic one.
    /// </summary>
    private static StructArray Classifications(int rows, int nullEvery, bool views = true)
    {
        var confidences = new double?[rows];
        bool[]? valid = nullEvery > 0 ? new bool[rows] : null;
        var names = new string[rows];
        for (var row = 0; row < rows; row++)
        {
            names[row] = $"c{row}";
            confidences[row] = row / 10.0;
            if (valid is not null)
            {
                valid[row] = row % nullEvery != 0;
            }
        }

        IArrowArray categories = views
            ? new StringViewArray.Builder().AppendRange(names).Build()
            : new StringArray.Builder().AppendRange(names).Build();
        return Struct(
            rows,
            valid,
            ("Category", views ? StringViewType.Default : StringType.Default, false, categories),
            ("Confidence", DoubleType.Default, false, Fixed<double>(DoubleType.Default, confidences)));
    }

    /// <summary>COMPOSITE(Category STRING, Confidence FP64, Rank I64), every row valid.</summary>
    private static StructArray Ranked(int rows)
    {
        var names = new string[rows];
        var confidences = new double?[rows];
        var ranks = new long?[rows];
        for (var row = 0; row < rows; row++)
        {
            names[row] = row % 2 == 0 ? $"category {row}, long enough to leave the view" : $"c{row}";
            confidences[row] = row / 7.0;
            ranks[row] = row;
        }

        return Struct(
            rows,
            null,
            ("Category", StringViewType.Default, false, new StringViewArray.Builder().AppendRange(names).Build()),
            ("Confidence", DoubleType.Default, false, Fixed<double>(DoubleType.Default, confidences)),
            ("Rank", Int64Type.Default, false, Fixed<long>(Int64Type.Default, ranks)));
    }

    private static StructArray Struct(
        int length, bool[]? validity, params (string Name, IArrowType Type, bool Nullable, IArrowArray Array)[] fields)
    {
        var type = new StructType([.. fields.Select(f => new ArrowField(f.Name, f.Type, f.Nullable))]);
        var (bits, nulls) = Bits(validity, length);
        return new StructArray(type, length, fields.Select(f => f.Array), bits, nulls);
    }

    private static (ArrowBuffer Bits, int Nulls) Bits(bool[]? validity, int length)
    {
        if (validity is null)
        {
            return (ArrowBuffer.Empty, 0);
        }

        var bits = new byte[(length + 7) / 8];
        var nulls = 0;
        for (var i = 0; i < length; i++)
        {
            if (validity[i])
            {
                BitUtility.SetBit(bits, i);
            }
            else
            {
                nulls++;
            }
        }

        return (new ArrowBuffer(bits), nulls);
    }

    private static IArrowArray Fixed<TValue>(IArrowType type, TValue?[] values)
        where TValue : unmanaged
    {
        var width = Unsafe.SizeOf<TValue>();
        var bytes = new byte[values.Length * width];
        var valid = new bool[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            if (values[i] is { } value)
            {
                MemoryMarshal.Write(bytes.AsSpan(i * width), in value);
                valid[i] = true;
            }
        }

        var (bits, nulls) = Bits(valid.All(v => v) ? null : valid, values.Length);
        return ArrowArrayFactory.BuildArray(
            new ArrayData(type, values.Length, nulls, 0, [bits, new ArrowBuffer(bytes)]));
    }

    private static IArrowArray Decimals(Decimal128Type type, decimal[] values)
    {
        var bytes = new byte[values.Length * 16];
        for (var i = 0; i < values.Length; i++)
        {
            var unscaled = (Int128)(values[i] * (decimal)Math.Pow(10, type.Scale));
            BinaryPrimitives.WriteInt128LittleEndian(bytes.AsSpan(i * 16), unscaled);
        }

        return ArrowArrayFactory.BuildArray(
            new ArrayData(type, values.Length, 0, 0, [ArrowBuffer.Empty, new ArrowBuffer(bytes)]));
    }

    private static IArrowArray Uuids(Guid[] values)
    {
        var bytes = new byte[values.Length * 16];
        for (var i = 0; i < values.Length; i++)
        {
            values[i].TryWriteBytes(bytes.AsSpan(i * 16), bigEndian: true, out _);
        }

        return ArrowArrayFactory.BuildArray(
            new ArrayData(new FixedSizeBinaryType(16), values.Length, 0, 0, [ArrowBuffer.Empty, new ArrowBuffer(bytes)]));
    }
}

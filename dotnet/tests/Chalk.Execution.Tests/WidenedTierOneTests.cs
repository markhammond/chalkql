using System.Diagnostics.CodeAnalysis;
using Apache.Arrow;
using Chalk.Catalog;
using Chalk.Execution.Expressions;
using Chalk.Ir;
using Chalk.Sources;
using Chalk.Sources.Poco;
using Chalk.TestKit;
using Chalk.Tests;
using static Chalk.TestKit.IrBuilder;
using CatalogContext = Chalk.Catalog.CatalogContext;
using FunctionDescriptor = Chalk.Catalog.FunctionDescriptor;
using IrType = Chalk.Ir.Type;

namespace Chalk.Execution.Tests;

/// <summary>
/// Tier 1 widened to the CLR spellings the POCO source already maps (D298): each kind read by a
/// delegate and written back, on both engines; the nullable forms; a composite's fields; an
/// aggregate's input and result; the refusals — at registration and per value — and what a
/// <c>decimal</c> and a <c>DateOnly</c> lane cost per row, which is nothing.
/// </summary>
[Experimental("CHALK001")]
public sealed class WidenedTierOneTests
{
    /// <summary>One row of every widened kind, and a nullable one of two of them.</summary>
    public sealed record Entry(
        long Id,
        [property: ChalkColumn(Precision = 18, Scale = 2)] decimal Amount,
        decimal Total,
        DateOnly Day,
        TimeOnly At,
        DateTime Stamp,
        DateTimeOffset Instant,
        TimeSpan Span,
        Guid Key,
        byte[]? Blob,
        DateOnly? MaybeDay,
        [property: ChalkColumn(Precision = 18, Scale = 2)] decimal? MaybeAmount);

    /// <summary>A composite whose fields are widened kinds.</summary>
    public readonly record struct Stamped(DateOnly Day, decimal Amount, Guid Key, TimeSpan Span);

    public struct SumState
    {
        public decimal Total;
        public long Count;
    }

    public struct LatestState
    {
        public DateOnly Latest;
        public bool Any;
    }

    private const int Rows = 4096;

    private readonly Xunit.ITestOutputHelper _output;

    public WidenedTierOneTests(Xunit.ITestOutputHelper output) => _output = output;

    private static Entry[] Entries()
    {
        var rows = new Entry[Rows];
        var epoch = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
        for (var i = 0; i < Rows; i++)
        {
            rows[i] = new Entry(
                i,
                (i % 1000) + 0.25m,
                i * 1.5m,
                new DateOnly(2024, 1, 1).AddDays(i % 400),
                new TimeOnly(0, 0).Add(TimeSpan.FromSeconds(i * 7)),
                epoch.AddTicks(i * 12_345L),
                new DateTimeOffset(epoch.AddMinutes(i), TimeSpan.Zero),
                TimeSpan.FromMilliseconds(i * 3),
                new Guid(i, 7, 9, 1, 2, 3, 4, 5, 6, 7, 8),
                i % 5 == 0 ? null : [(byte)i, (byte)(i >> 8), 42],
                i % 3 == 0 ? null : new DateOnly(2000, 1, 1).AddDays(i),
                i % 4 == 0 ? null : i / 4m);
        }

        return rows;
    }

    private static readonly Lazy<(PocoSource Source, RowType Row)> Table = new(() =>
    {
        var source = new PocoSourceBuilder("mem").AddTable("entries", Entries()).Build();
        var columns = source.DescribeSchema().Tables[0].Columns;
        return (source, Row([.. columns.Select(c => F(c.Name, c.Type.ToProto()))]));
    });

    private static RowType EntryRow => Table.Value.Row;

    private static Rel EntryRead() => Read("entries", EntryRow, rows: Rows);

    private static int Column(string name) =>
        EntryRow.Fields.Select((f, i) => (f, i)).Single(p => p.f.Name == name).i;

    private static Expr Col(string name) => Ref(EntryRow, Column(name));

    private static IrType TypeOf(string name) => EntryRow.Fields[Column(name)].Type;

    private static IrType Nullable(IrType type)
    {
        var copy = type.Clone();
        copy.Nullable = true;
        return copy;
    }

    private static CompiledPlan Compile(
        Plan plan,
        IReadOnlyList<FunctionDescriptor> functions,
        IReadOnlyList<HostFunction> hosts,
        bool reference = false,
        int batchSize = Rows)
    {
        var source = new PocoSourceBuilder("mem").AddTable("entries", Entries()).Build();
        var schema = source.DescribeSchema();
        var withFunctions = new SchemaDescriptor
        {
            SourceId = schema.SourceId,
            Name = schema.Name,
            Kind = schema.Kind,
            Capabilities = schema.Capabilities,
            Tables = schema.Tables,
            Functions = functions,
        };
        var catalog = new CatalogContext { ContextId = "test", Epoch = 1, Schemas = [withFunctions] };
        foreach (var descriptor in functions)
        {
            UserFunctionBinding.Check(
                descriptor, hosts.Single(h => h.Name == descriptor.Name), "engine creation");
        }

        return PlanCompiler.Compile(
            plan,
            catalog,
            new Dictionary<string, ISourceRuntime> { ["mem"] = source },
            new ExecutionSettings
            {
                BatchSize = batchSize,
                PooledOutput = true,
                UseReferenceEngine = reference,
                Functions = new HostFunctionSet(
                    hosts.ToDictionary(h => h.Name, h => h, StringComparer.OrdinalIgnoreCase)),
            });
    }

    private static async Task<List<object?[]>> RunAsync(CompiledPlan compiled)
    {
        using var arena = new ExecutionArena();
        var batches = new List<RecordBatch>();
        try
        {
            await foreach (var batch in compiled.ExecuteAsync([], new ExecutionStats(), arena, CancellationToken.None))
            {
                batches.Add(batch);
            }

            return BatchReader.ToStorageRows(batches);
        }
        finally
        {
            foreach (var batch in batches)
            {
                batch.Dispose();
            }
        }
    }

    /// <summary>The same plan through both engines, which must agree row for row (I4).</summary>
    private static async Task<List<object?[]>> BothAsync(
        Plan plan, IReadOnlyList<FunctionDescriptor> functions, IReadOnlyList<HostFunction> hosts)
    {
        var vectorised = await RunAsync(Compile(plan, functions, hosts));
        var reference = await RunAsync(Compile(plan, functions, hosts, reference: true));
        Assert.Equal(reference.Count, vectorised.Count);
        for (var i = 0; i < reference.Count; i++)
        {
            Assert.Equal(Render(reference[i]), Render(vectorised[i]));
        }

        return vectorised;
    }

    private static string Render(object? value) => value switch
    {
        null => "NULL",
        object?[] values => "(" + string.Join(", ", values.Select(Render)) + ")",
        byte[] bytes => Convert.ToHexString(bytes),
        decimal d => d.ToString("G29", System.Globalization.CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "?",
    };

    private static Expr Fn(string name, IrType type, params Expr[] args) => UserCall($"main.{name}", type, args);

    // ---- each kind, in and out, on both engines ----

    [Fact]
    public async Task Every_widened_kind_reaches_a_delegate_and_its_answer_reaches_the_column()
    {
        FunctionDescriptor[] declarations =
        [
            new FunctionBuilder("dec_up").Scalar()
                .Parameter("x", ChalkType.Decimal(18, 2, nullable: true)).Returns(ChalkType.Decimal(18, 2)).Strict().Client().Build(),
            new FunctionBuilder("next_day").Scalar<DateOnly, DateOnly>("d").Strict().Client().Build(),
            new FunctionBuilder("minute_later").Scalar<TimeOnly, TimeOnly>("t").Strict().Client().Build(),
            new FunctionBuilder("second_later").Scalar<DateTime, DateTime>("t").Strict().Client().Build(),
            new FunctionBuilder("hour_later").Scalar<DateTimeOffset, DateTimeOffset>("t").Strict().Client().Build(),
            new FunctionBuilder("doubled").Scalar<TimeSpan, TimeSpan>("s").Strict().Client().Build(),
            new FunctionBuilder("same_key").Scalar<Guid, Guid>("k").Strict().Client().Build(),
            new FunctionBuilder("same_blob").Scalar<ReadOnlyMemory<byte>, ReadOnlyMemory<byte>>("b").Strict().Client().Build(),
            new FunctionBuilder("reversed_blob").Scalar<byte[], byte[]>("b").Strict().Client().Build(),
        ];
        HostFunction[] hosts =
        [
            new HostScalar1<decimal, decimal>("dec_up", static x => x + 1.25m),
            new HostScalar1<DateOnly, DateOnly>("next_day", static d => d.AddDays(1)),
            new HostScalar1<TimeOnly, TimeOnly>("minute_later", static t => t.AddMinutes(1)),
            new HostScalar1<DateTime, DateTime>("second_later", static t => t.AddSeconds(1)),
            new HostScalar1<DateTimeOffset, DateTimeOffset>("hour_later", static t => t.AddHours(1)),
            new HostScalar1<TimeSpan, TimeSpan>("doubled", static s => s + s),
            new HostScalar1<Guid, Guid>("same_key", static k => k),
            new HostScalar1<ReadOnlyMemory<byte>, ReadOnlyMemory<byte>>("same_blob", static b => b),
            new HostScalar1<byte[], byte[]>("reversed_blob", static b => [.. b.Reverse()]),
        ];

        var plan = IrBuilder.Plan(Project(
            EntryRead(),
            [
                ("id", Col("Id")),
                ("amount", Fn("dec_up", Dec(18, 2, nullable: false), Col("Amount"))),
                ("day", Fn("next_day", Date(), Col("Day"))),
                ("at", Fn("minute_later", Time(6), Col("At"))),
                ("stamp", Fn("second_later", Timestamp(9), Col("Stamp"))),
                ("instant", Fn("hour_later", TimestampTz(9), Col("Instant"))),
                ("span", Fn("doubled", IntervalDay(), Col("Span"))),
                ("key", Fn("same_key", Uuid(), Col("Key"))),
                ("blob", Fn("same_blob", Binary(nullable: true), Col("Blob"))),
                ("reversed", Fn("reversed_blob", Binary(nullable: true), Col("Blob"))),
            ]));

        var rows = await BothAsync(plan, declarations, hosts);

        var entries = Entries();
        Assert.Equal(Rows, rows.Count);
        foreach (var row in rows)
        {
            var entry = entries[(int)(long)row[0]!];
            Assert.Equal(entry.Amount + 1.25m, (decimal)row[1]!);
            Assert.Equal((long)(entry.Day.AddDays(1).DayNumber - 719_162), Convert.ToInt64(row[2], null));
            Assert.Equal(entry.At.AddMinutes(1).Ticks / 10, Convert.ToInt64(row[3], null));
            Assert.Equal((entry.Stamp.AddSeconds(1).Ticks - ClrStorageEpoch) * 100, Convert.ToInt64(row[4], null));
            Assert.Equal((entry.Instant.AddHours(1).UtcTicks - ClrStorageEpoch) * 100, Convert.ToInt64(row[5], null));
            Assert.Equal((entry.Span + entry.Span).Ticks / 10, Convert.ToInt64(row[6], null));
            Assert.Equal(entry.Key, new Guid((byte[])row[7]!, bigEndian: true));
            if (entry.Blob is null)
            {
                // STRICT over a NULL BINARY: the delegate is never called.
                Assert.Null(row[8]);
                Assert.Null(row[9]);
            }
            else
            {
                Assert.Equal(entry.Blob, (byte[])row[8]!);
                Assert.Equal(entry.Blob.Reverse(), (byte[])row[9]!);
            }
        }
    }

    private const long ClrStorageEpoch = 621_355_968_000_000_000L;

    [Fact]
    public async Task A_non_strict_delegate_sees_null_through_the_nullable_forms()
    {
        FunctionDescriptor[] declarations =
        [
            new FunctionBuilder("day_or_epoch").Scalar<DateOnly?, DateOnly>("d").Client().Build(),
            new FunctionBuilder("amount_or_zero").Scalar()
                .Parameter("x", ChalkType.Decimal(18, 2, nullable: true)).Returns(ChalkType.Decimal(18, 2)).Client().Build(),
        ];
        HostFunction[] hosts =
        [
            new HostScalar1<DateOnly?, DateOnly>("day_or_epoch", static d => d ?? new DateOnly(1970, 1, 1)),
            new HostScalar1<decimal?, decimal>("amount_or_zero", static x => x ?? 0m),
        ];
        var plan = IrBuilder.Plan(Project(
            EntryRead(),
            [
                ("id", Col("Id")),
                ("day", Fn("day_or_epoch", Date(), Col("MaybeDay"))),
                ("amount", Fn("amount_or_zero", Dec(18, 2), Col("MaybeAmount"))),
            ]));

        var rows = await BothAsync(plan, declarations, hosts);

        var entries = Entries();
        foreach (var row in rows)
        {
            var entry = entries[(int)(long)row[0]!];
            var expectedDay = entry.MaybeDay ?? new DateOnly(1970, 1, 1);
            Assert.Equal((long)(expectedDay.DayNumber - 719_162), Convert.ToInt64(row[1], null));
            Assert.Equal(entry.MaybeAmount ?? 0m, (decimal)row[2]!);
        }
    }

    [Fact]
    public async Task A_composite_s_fields_may_be_widened_kinds()
    {
        var declaration = new FunctionBuilder("stamp_of").Scalar()
            .Parameter<DateOnly>("d")
            .Parameter("x", ChalkType.Decimal(18, 2, nullable: true))
            .Parameter<Guid>("k")
            .Parameter<TimeSpan>("s")
            .Returns<Stamped>()
            .Strict()
            .Client()
            .Build();
        Assert.Equal(
            "COMPOSITE(Day DATE, Amount DECIMAL(28,10), Key UUID, Span INTERVAL_DAY)",
            IrTypes.Describe(declaration.ReturnType!.Value.ToProto()));

        var host = new HostScalar4<DateOnly, decimal, Guid, TimeSpan, Stamped>(
            "stamp_of", static (d, x, k, s) => new Stamped(d, x * 2, k, s));
        var stampType = declaration.ReturnType!.Value.ToProto();
        var call = Fn("stamp_of", stampType, Col("Day"), Col("Amount"), Col("Key"), Col("Span"));
        var plan = IrBuilder.Plan(Project(
            EntryRead(),
            [
                ("id", Col("Id")),
                ("day", FieldAccess(call, 0)),
                ("amount", FieldAccess(call, 1)),
                ("whole", call),
            ]));

        var rows = await BothAsync(plan, [declaration], [host]);

        var entries = Entries();
        foreach (var row in rows)
        {
            var entry = entries[(int)(long)row[0]!];
            Assert.Equal((long)(entry.Day.DayNumber - 719_162), Convert.ToInt64(row[1], null));
            Assert.Equal(entry.Amount * 2, (decimal)row[2]!);
            var whole = Assert.IsType<object?[]>(row[3]);
            Assert.Equal(entry.Key, new Guid((byte[])whole[2]!, bigEndian: true));
            Assert.Equal(entry.Span.Ticks / 10, Convert.ToInt64(whole[3], null));
        }
    }

    [Fact]
    public async Task An_aggregate_s_input_and_result_may_be_widened_kinds()
    {
        FunctionDescriptor[] declarations =
        [
            new FunctionBuilder("dsum").Aggregate<decimal, decimal>("x").Client().Build(),
            new FunctionBuilder("latest").Aggregate<DateOnly, DateOnly>("d").Window().Client().Build(),
        ];
        HostFunction[] hosts =
        [
            new HostAggregate<SumState, decimal, decimal?>("dsum", new AggregateSpec<SumState, decimal, decimal?>
            {
                Init = static () => default,
                Add = static (ref s, x) =>
                {
                    s.Total += x;
                    s.Count++;
                },
                Finish = static s => s.Count == 0 ? null : s.Total,
            }),
            new HostAggregate<LatestState, DateOnly, DateOnly?>("latest", new AggregateSpec<LatestState, DateOnly, DateOnly?>
            {
                Init = static () => default,
                Add = static (ref s, d) =>
                {
                    s.Latest = !s.Any || d > s.Latest ? d : s.Latest;
                    s.Any = true;
                },
                Finish = static s => s.Any ? s.Latest : null,
            }),
        ];

        var bucket = Project(
            EntryRead(),
            [
                ("b", Call(FunctionId.Modulus, I64(), Col("Id"), Lit(8L))),
                ("total", Col("Total")),
                ("day", Col("Day")),
            ]);
        var grouped = HashAggregate(
            bucket,
            [0],
            [
                ("s", UserAgg("main.dsum", Dec(28, 10, nullable: true), Ref(bucket.RowType, 1))),
                ("l", UserAgg("main.latest", Date(nullable: true), Ref(bucket.RowType, 2))),
            ]);
        var rows = await BothAsync(IrBuilder.Plan(grouped), declarations, hosts);

        var entries = Entries();
        Assert.Equal(8, rows.Count);
        foreach (var row in rows)
        {
            var b = (long)row[0]!;
            var members = entries.Where(e => e.Id % 8 == b).ToArray();
            Assert.Equal(members.Sum(e => e.Total), (decimal)row[1]!);
            Assert.Equal(
                (long)(members.Max(e => e.Day).DayNumber - 719_162), Convert.ToInt64(row[2], null));
        }

        // The same DateOnly aggregate over a frame: the window path reads its lanes too.
        var window = Window(
            Project(EntryRead(), [("id", Col("Id")), ("day", Col("Day"))]),
            [],
            [Asc(0, I64())],
            RowsFrame(2, 0),
            [("l", WinUserAgg("main.latest", Date(nullable: true), Ref(1, Date())))]);
        var framed = await BothAsync(IrBuilder.Plan(window), declarations, hosts);
        Assert.Equal(Rows, framed.Count);
    }

    private static WindowCall WinUserAgg(string name, IrType type, Expr arg)
    {
        var call = new WindowCall { UserFunction = name, Type = type };
        call.Args.Add(arg);
        return call;
    }

    // ---- a table function's arguments and columns ----

    /// <summary>A table function's row, its columns widened kinds.</summary>
    public sealed record Dated(DateOnly Day, decimal Amount, Guid Key);

    private static IEnumerable<Dated> DaysFrom(DateOnly start, long count)
    {
        for (var i = 0; i < count; i++)
        {
            yield return new Dated(start.AddDays(i), (i * 2.5m) + 0.1m, new Guid(i, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10));
        }
    }

    [Fact]
    public async Task A_table_function_takes_and_yields_widened_kinds()
    {
        var declaration = new FunctionBuilder("days_from")
            .TableFunction()
            .Parameter<DateOnly>("start")
            .Parameter<long>("count")
            .Column<DateOnly>("day")
            .Column<decimal>("amount")
            .Column<Guid>("key")
            .Client()
            .Build();
        var host = new HostTable<Dated>("days_from", (Func<DateOnly, long, IEnumerable<Dated>>)DaysFrom);
        var start = new DateOnly(2024, 2, 27);
        var row = Row(F("day", Date()), F("amount", Dec(28, 10)), F("key", Uuid()));
        var scan = new Rel
        {
            RowType = row,
            TableFunctionScan = new TableFunctionScan { Function = "main.days_from" },
        };
        scan.TableFunctionScan.Args.Add(LitDate(start.DayNumber - 719_162));
        scan.TableFunctionScan.Args.Add(Lit(4L));

        var rows = await BothAsync(IrBuilder.Plan(scan), [declaration], [host]);

        var expected = DaysFrom(start, 4).ToArray();
        Assert.Equal(4, rows.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            Assert.Equal((long)(expected[i].Day.DayNumber - 719_162), Convert.ToInt64(rows[i][0], null));
            Assert.Equal(expected[i].Amount, (decimal)rows[i][1]!);
            Assert.Equal(expected[i].Key, new Guid((byte[])rows[i][2]!, bigEndian: true));
        }
    }

    // ---- read and write precision ----

    [Fact]
    public async Task A_nanosecond_timestamp_is_floored_to_the_tick_on_the_way_in()
    {
        var declaration = new FunctionBuilder("ticks_of").Scalar<DateTime, long>("t").Strict().Client().Build();
        var host = new HostScalar1<DateTime, long>("ticks_of", static t => t.Ticks - ClrStorageEpoch);
        var plan = IrBuilder.Plan(Project(
            EntryRead(),
            [
                ("after", Fn("ticks_of", I64(), LitTimestamp(1_000_000_123, precision: 9))),
                ("before", Fn("ticks_of", I64(), LitTimestamp(-123, precision: 9))),
            ]));

        var rows = await BothAsync(plan, [declaration], [host]);

        // 1 000 000 123 ns is 10 000 001 ticks and 23 ns; −123 ns floors to −2 ticks, the tick at or
        // before it — never the later instant a division toward zero would give.
        Assert.Equal(10_000_001L, (long)rows[0][0]!);
        Assert.Equal(-2L, (long)rows[0][1]!);
    }

    [Fact]
    public async Task A_value_the_declared_precision_cannot_hold_is_refused_on_the_way_out()
    {
        (FunctionDescriptor Declaration, HostFunction Host, IrType Type, string Expected)[] cases =
        [
            (
                new FunctionBuilder("at_ms").Scalar().Returns(ChalkType.Timestamp(3)).Client().Build(),
                new HostScalar0<DateTime>("at_ms", static () => new DateTime(2024, 1, 1).AddTicks(1)),
                Timestamp(3),
                "the result of 'at_ms' is TIMESTAMP(3) and the delegate answered 2024-01-01T00:00:00.0000001"),
            (
                new FunctionBuilder("at_time").Scalar().Returns(ChalkType.Time(6)).Client().Build(),
                new HostScalar0<TimeOnly>("at_time", static () => new TimeOnly(1)),
                Time(6),
                "which TIME cannot hold: it counts whole microseconds"),
            (
                new FunctionBuilder("lasts").Scalar().Returns(ChalkType.IntervalDay()).Client().Build(),
                new HostScalar0<TimeSpan>("lasts", static () => new TimeSpan(15)),
                IntervalDay(),
                "which INTERVAL_DAY cannot hold: it counts whole microseconds"),
            (
                new FunctionBuilder("cents").Scalar().Returns(ChalkType.Decimal(18, 2)).Client().Build(),
                new HostScalar0<decimal>("cents", static () => 1.005m),
                Dec(18, 2),
                "which needs scale 3"),
        ];

        foreach (var (declaration, host, type, expected) in cases)
        {
            var plan = IrBuilder.Plan(Project(EntryRead(), [("v", Fn(declaration.Name, type))]));
            foreach (var reference in new[] { false, true })
            {
                var error = await Record.ExceptionAsync(
                    () => RunAsync(Compile(plan, [declaration], [host], reference)));
                Assert.NotNull(error);
                var innermost = error!;
                while (innermost.InnerException is not null)
                {
                    innermost = innermost.InnerException;
                }

                Assert.IsType<InvalidOperationException>(innermost);
                Assert.Contains(expected, innermost.Message, StringComparison.Ordinal);
            }
        }
    }

    // ---- refusals at registration ----

    [Fact]
    public void A_decimal_wider_than_28_digits_is_refused_at_registration_naming_the_tier_2_path()
    {
        var declaration = new FunctionBuilder("wide").Scalar()
            .Parameter("x", ChalkType.Decimal(38, 4, nullable: true)).Returns(ChalkType.Decimal(18, 2)).Client().Build();

        var error = Assert.Throws<InvalidOperationException>(() => UserFunctionBinding.Check(
            declaration, new HostScalar1<decimal, decimal>("wide", static x => x), "engine creation"));

        Assert.Contains("parameter 'x' is DECIMAL(38,4)?, which has no Tier 1 spelling", error.Message, StringComparison.Ordinal);
        Assert.Contains("a CLR decimal holds 28 digits", error.Message, StringComparison.Ordinal);
        Assert.Contains("Tier 2 kernel (IVectorFunction)", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_binary_aggregate_is_refused_at_registration()
    {
        var declaration = new FunctionBuilder("blobs").Aggregate<ReadOnlyMemory<byte>, long>("b").Client().Build();
        var host = new HostAggregate<long, ReadOnlyMemory<byte>, long>(
            "blobs",
            new AggregateSpec<long, ReadOnlyMemory<byte>, long>
            {
                Init = static () => 0,
                Add = static (ref s, b) => s += b.Length,
                Finish = static s => s,
            });

        var error = Assert.Throws<InvalidOperationException>(
            () => UserFunctionBinding.Check(declaration, host, "engine creation"));

        Assert.Contains("folds fixed-width values only", error.Message, StringComparison.Ordinal);
    }

    // ---- what a lane costs ----

    /// <summary>
    /// A <c>decimal</c> and a <c>DateOnly</c> lane cost nothing per row (D298): the call's result is
    /// written into arena-backed lanes through <see cref="ClrStorage"/>, exactly as a scalar result
    /// is, so a warm projection through a delegate allocates what the same projection without one
    /// does. <c>ReadOnlyMemory&lt;byte&gt;</c>, BINARY's allocation-free spelling, is held to it too.
    /// </summary>
    [Theory]
    [InlineData("decimal")]
    [InlineData("DateOnly")]
    [InlineData("ReadOnlyMemory<byte>")]
    public async Task A_widened_lane_allocates_nothing_per_row(string spelling)
    {
        var (declaration, host, column, type) = spelling switch
        {
            "decimal" => (
                new FunctionBuilder("keep").Scalar()
                    .Parameter("x", ChalkType.Decimal(18, 2, nullable: true)).Returns(ChalkType.Decimal(18, 2)).Strict().Client().Build(),
                (HostFunction)new HostScalar1<decimal, decimal>("keep", static x => x + 1m),
                "Amount",
                Dec(18, 2)),
            "DateOnly" => (
                new FunctionBuilder("keep").Scalar<DateOnly, DateOnly>("d").Strict().Client().Build(),
                new HostScalar1<DateOnly, DateOnly>("keep", static d => d.AddDays(1)),
                "Day",
                Date()),
            _ => (
                new FunctionBuilder("keep").Scalar<ReadOnlyMemory<byte>, ReadOnlyMemory<byte>>("b").Strict().Client().Build(),
                new HostScalar1<ReadOnlyMemory<byte>, ReadOnlyMemory<byte>>("keep", static b => b),
                "Blob",
                Binary(nullable: true)),
        };

        var withCall = IrBuilder.Plan(Project(EntryRead(), [("v", Fn("keep", type, Col(column)))]));
        var floor = IrBuilder.Plan(Project(EntryRead(), [("v", Col(column))]));

        var measuredFloor = await Measure(floor, [], []);
        var measured = await Measure(withCall, [declaration], [host]);

        _output.WriteLine(
            $"{spelling} lane: {measured} bytes over 8 batches against the bare column's {measuredFloor} = "
            + $"{(measured - measuredFloor) / 8.0:0.###} bytes/batch.");
        Assert.True(
            measured <= measuredFloor,
            $"a warm {spelling} call cost {(measured - measuredFloor) / 8.0:0.###} bytes per batch more "
            + $"than the bare column ({measured} against {measuredFloor}).");
    }

    private static async Task<long> Measure(
        Plan plan, IReadOnlyList<FunctionDescriptor> functions, IReadOnlyList<HostFunction> hosts)
    {
        var compiled = Compile(plan, functions, hosts, batchSize: Rows / 8);
        using var arena = new ExecutionArena();
        var (allocated, _) = await AllocationProbe.SteadyStateAsync(() => ConsumeAsync(compiled, arena));
        Assert.Equal(0, arena.OutstandingBytes);
        return allocated;
    }

    private static async Task<int> ConsumeAsync(CompiledPlan compiled, ExecutionArena arena)
    {
        var rows = 0;
        await foreach (var batch in compiled.ExecuteAsync([], new ExecutionStats(), arena, CancellationToken.None))
        {
            rows += batch.Length;
            batch.Dispose();
        }

        return rows;
    }
}

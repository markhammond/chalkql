using System.Diagnostics.CodeAnalysis;
using Apache.Arrow;
using Apache.Arrow.Types;
using Chalk.Catalog;
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
/// The executor's half of structured results (D291, D294; ADR 0077): a struct result written by a
/// Tier 1 delegate and taken apart by field access, nullable and strict; a struct-valued aggregate
/// grouped and over a frame; how often a call runs; what a struct costs per batch; and the
/// reference executor agreeing with all of it.
/// </summary>
[Experimental("CHALK001")]
public sealed class StructExecutionTests
{
    /// <summary>A transaction: every fourth amount is missing, which is what exercises STRICT.</summary>
    public sealed record Txn(long Id, Utf8String Description, double? Amount);

    /// <summary>The owner's record.</summary>
    public readonly record struct Classification(Utf8String Category, double Confidence);

    /// <summary>A struct aggregate's answer.</summary>
    public readonly record struct Summary(double Total, long Count);

    public struct SummaryState
    {
        public double Total;
        public long Count;
    }

    private const int Rows = 4096;

    private static readonly Utf8String Large = "large"u8.ToArray();
    private static readonly Utf8String Small = "small"u8.ToArray();

    private static readonly IrType ClassificationType = Struct(
        nullable: true, F("Category", Str()), F("Confidence", Fp64()));

    private static readonly RowType TxnRow = Row(
        F("Id", I64()), F("Description", Str()), F("Amount", Fp64(nullable: true)));

    private readonly Xunit.ITestOutputHelper _output;

    public StructExecutionTests(Xunit.ITestOutputHelper output) => _output = output;

    /// <summary>Counts every call a registered delegate answers, per test.</summary>
    private sealed class Counter
    {
        public long Calls;
    }

    private static Classification Classify(double amount) =>
        new(amount >= 100 ? Large : Small, amount / 1000);

    private static Txn[] Transactions()
    {
        var rows = new Txn[Rows];
        for (var i = 0; i < Rows; i++)
        {
            rows[i] = new Txn(i, i % 2 == 0 ? Large : Small, i % 4 == 3 ? null : i % 250);
        }

        return rows;
    }

    // ---- declarations ----

    private static FunctionDescriptor ClassifyDeclaration(Volatility volatility = Volatility.Immutable)
    {
        var builder = new FunctionBuilder("classify").Scalar<double, Classification>("amount").Strict();
        builder = volatility switch
        {
            Volatility.Volatile => builder.Volatile(),
            Volatility.Stable => builder.Stable(),
            _ => builder.Immutable(),
        };
        return builder.Client().Build();
    }

    private static FunctionDescriptor MaybeDeclaration() =>
        new FunctionBuilder("maybe_classify").Scalar<double?, Classification?>("amount").Client().Build();

    private static FunctionDescriptor TodayDeclaration() =>
        new FunctionBuilder("today").Scalar<Classification>().Stable().Client().Build();

    private static FunctionDescriptor SummarizeDeclaration() =>
        new FunctionBuilder("summarize").Aggregate<double, Summary>("x").Window().Client().Build();

    private static HostFunction ClassifyHost(Counter counter) =>
        new HostScalar1<double, Classification>("classify", amount =>
        {
            counter.Calls++;
            return Classify(amount);
        });

    private static HostFunction MaybeHost() =>
        new HostScalar1<double?, Classification?>("maybe_classify", static amount =>
        {
            Classification? answer = default;
            if (amount is { } value && value >= 10)
            {
                answer = Classify(value);
            }

            return answer;
        });

    private static HostFunction TodayHost(Counter counter) =>
        new HostScalar0<Classification>("today", () =>
        {
            counter.Calls++;
            return new Classification(Large, 1.0);
        });

    private static HostFunction SummarizeHost() =>
        new HostAggregate<SummaryState, double, Summary?>(
            "summarize",
            new AggregateSpec<SummaryState, double, Summary?>
            {
                Init = static () => default,
                Add = static (ref s, x) =>
                {
                    s.Total += x;
                    s.Count++;
                },
                Finish = static s => s.Count == 0 ? null : new Summary(s.Total, s.Count),
            });

    // ---- plans ----

    private static Rel TxnRead() => Read("txn", TxnRow, rows: Rows);

    private static Expr Fn(string name, IrType type, params Expr[] args) =>
        UserCall($"main.{name}", type, args);

    private static Expr ClassifyCall() => Fn("classify", ClassificationType, Ref(TxnRow, 2));

    private static CompiledPlan Compile(
        Plan plan,
        IReadOnlyList<FunctionDescriptor> functions,
        IReadOnlyList<HostFunction> hosts,
        bool reference = false,
        int batchSize = Rows)
    {
        var source = new PocoSourceBuilder("mem").AddTable("txn", Transactions()).Build();
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
        double d => d.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "?",
    };

    // ---- field access ----

    [Fact]
    public async Task Two_fields_of_one_call_are_read_through_field_access()
    {
        var counter = new Counter();
        var plan = IrBuilder.Plan(Project(
            TxnRead(),
            [("id", Ref(TxnRow, 0)), ("category", FieldAccess(ClassifyCall(), 0)), ("confidence", FieldAccess(ClassifyCall(), 1))]));

        var rows = await BothAsync(plan, [ClassifyDeclaration()], [ClassifyHost(counter)]);

        Assert.Equal(Rows, rows.Count);
        foreach (var row in rows)
        {
            var id = (long)row[0]!;
            if (id % 4 == 3)
            {
                // STRICT over a NULL amount: the whole struct is NULL, and so is each field.
                Assert.Null(row[1]);
                Assert.Null(row[2]);
                continue;
            }

            var amount = (double)(id % 250);
            Assert.Equal(amount >= 100 ? "large" : "small", row[1]);
            Assert.Equal(amount / 1000, (double)row[2]!);
        }
    }

    [Fact]
    public async Task A_field_of_a_struct_column_is_read_through_its_reference()
    {
        var inner = Project(TxnRead(), [("id", Ref(TxnRow, 0)), ("c", ClassifyCall())]);
        var plan = IrBuilder.Plan(Project(
            inner,
            [("id", Ref(inner.RowType, 0)), ("confidence", FieldAccess(Ref(inner.RowType, 1), 1))]));

        var rows = await BothAsync(plan, [ClassifyDeclaration()], [ClassifyHost(new Counter())]);

        Assert.Equal(Rows, rows.Count);
        Assert.All(rows.Where(r => (long)r[0]! % 4 == 3), r => Assert.Null(r[1]));
        Assert.All(
            rows.Where(r => (long)r[0]! % 4 != 3),
            r => Assert.Equal((double)((long)r[0]! % 250) / 1000, (double)r[1]!));
    }

    [Fact]
    public async Task The_whole_value_is_an_arrow_struct_whose_children_are_the_fields()
    {
        var plan = IrBuilder.Plan(Project(TxnRead(), [("id", Ref(TxnRow, 0)), ("c", ClassifyCall())]));
        var compiled = Compile(plan, [ClassifyDeclaration()], [ClassifyHost(new Counter())]);

        using var arena = new ExecutionArena();
        var seen = 0;
        await foreach (var batch in compiled.ExecuteAsync([], new ExecutionStats(), arena, CancellationToken.None))
        {
            using (batch)
            {
                var column = Assert.IsType<StructArray>(batch.Column(1));
                var type = Assert.IsType<StructType>(column.Data.DataType);
                Assert.Equal(["Category", "Confidence"], type.Fields.Select(f => f.Name));
                Assert.True(batch.Schema.GetFieldByIndex(1).IsNullable);
                for (var row = 0; row < batch.Length; row++)
                {
                    Assert.Equal(row % 4 == 3, column.IsNull(row));
                }

                seen += batch.Length;
            }
        }

        Assert.Equal(Rows, seen);

        // And read back the way a host reads it: one object?[] per struct, in field order.
        var rows = await BothAsync(plan, [ClassifyDeclaration()], [ClassifyHost(new Counter())]);
        var first = Assert.IsType<object?[]>(rows[1][1]);
        Assert.Equal(["small", 0.001], first);
    }

    [Fact]
    public async Task A_nullable_record_answers_null_structs_and_null_fields()
    {
        var maybe = Fn("maybe_classify", ClassificationType, Ref(TxnRow, 2));
        var plan = IrBuilder.Plan(Project(
            TxnRead(),
            [("id", Ref(TxnRow, 0)), ("c", maybe), ("category", FieldAccess(maybe, 0))]));

        var rows = await BothAsync(plan, [MaybeDeclaration()], [MaybeHost()]);

        foreach (var row in rows)
        {
            var id = (long)row[0]!;
            var answered = id % 4 != 3 && id % 250 >= 10;
            Assert.Equal(answered, row[1] is not null);
            Assert.Equal(answered, row[2] is not null);
        }
    }

    [Fact]
    public async Task A_strict_delegate_never_sees_a_null_lane()
    {
        var counter = new Counter();
        var plan = IrBuilder.Plan(Project(TxnRead(), [("confidence", FieldAccess(ClassifyCall(), 1))]));

        await RunAsync(Compile(plan, [ClassifyDeclaration()], [ClassifyHost(counter)]));

        Assert.Equal(Rows - (Rows / 4), counter.Calls);
    }

    [Fact]
    public async Task A_null_test_reads_the_struct_s_own_validity()
    {
        var maybe = Fn("maybe_classify", ClassificationType, Ref(TxnRow, 2));
        var plan = IrBuilder.Plan(Project(
            TxnRead(),
            [("id", Ref(TxnRow, 0)), ("present", Call(FunctionId.IsNotNull, Bool(), maybe))]));

        var rows = await BothAsync(plan, [MaybeDeclaration()], [MaybeHost()]);

        Assert.All(rows, row =>
        {
            var id = (long)row[0]!;
            Assert.Equal(id % 4 != 3 && id % 250 >= 10, (bool)row[1]!);
        });
    }

    [Fact]
    public void A_struct_is_refused_where_the_engine_would_compare_order_or_hash_it()
    {
        var error = Assert.Throws<UnsupportedFeatureException>(
            () => Vectors.ColumnKinds.RequireComparable(ChalkType.FromProto(ClassificationType), "a sort key"));

        Assert.Contains("a sort key on a STRUCT", error.Message);
        Assert.Contains("no ordering or equality", error.Message);
    }

    // ---- aggregates ----

    [Fact]
    public async Task A_struct_aggregate_grouped_writes_finish_into_the_measure_column()
    {
        var summary = Struct(nullable: true, F("Total", Fp64()), F("Count", I64()));
        var bucket = Project(
            TxnRead(),
            [("b", Call(FunctionId.Modulus, I64(), Ref(TxnRow, 0), Lit(4L))), ("amount", Ref(TxnRow, 2))]);
        var grouped = HashAggregate(
            bucket, [0], [("s", UserAgg("main.summarize", summary, Ref(bucket.RowType, 1)))]);
        var plan = IrBuilder.Plan(Project(
            grouped,
            [
                ("b", Ref(grouped.RowType, 0)),
                ("total", FieldAccess(Ref(grouped.RowType, 1), 0)),
                ("n", FieldAccess(Ref(grouped.RowType, 1), 1)),
                ("s", Ref(grouped.RowType, 1)),
            ]));

        var rows = await BothAsync(plan, [SummarizeDeclaration()], [SummarizeHost()]);

        Assert.Equal(4, rows.Count);
        foreach (var row in rows)
        {
            var b = (long)row[0]!;
            var ids = Enumerable.Range(0, Rows).Where(i => i % 4 == b).ToArray();
            if (b == 3)
            {
                // Every amount in bucket 3 is NULL, so Finish saw nothing and answered NULL.
                Assert.Null(row[1]);
                Assert.Null(row[3]);
                continue;
            }

            Assert.Equal(ids.Sum(i => (double)(i % 250)), (double)row[1]!);
            Assert.Equal((long)ids.Length, (long)row[2]!);
        }
    }

    [Fact]
    public async Task A_struct_aggregate_over_a_frame_writes_each_frame_s_record()
    {
        var summary = Struct(nullable: true, F("Total", Fp64()), F("Count", I64()));
        var read = TxnRead();
        var window = Window(
            read,
            [],
            [Asc(0, I64())],
            RowsFrame(2, 0),
            [("s", new WindowCall { UserFunction = "main.summarize", Type = summary, Args = { Ref(TxnRow, 2) } })]);
        var sorted = Collated(window, (0, SortDirection.AscNullsLast));
        var plan = IrBuilder.Plan(Project(
            sorted,
            [("id", Ref(window.RowType, 0)), ("total", FieldAccess(Ref(window.RowType, 3), 0)), ("n", FieldAccess(Ref(window.RowType, 3), 1))]));

        var rows = await BothAsync(plan, [SummarizeDeclaration()], [SummarizeHost()]);

        Assert.Equal(Rows, rows.Count);
        foreach (var row in rows)
        {
            var id = (long)row[0]!;
            var frame = Enumerable.Range((int)Math.Max(0, id - 2), (int)Math.Min(3, id + 1))
                .Where(i => i % 4 != 3)
                .ToArray();
            if (frame.Length == 0)
            {
                Assert.Null(row[1]);
                continue;
            }

            Assert.Equal(frame.Sum(i => (double)(i % 250)), (double)row[1]!);
            Assert.Equal((long)frame.Length, (long)row[2]!);
        }
    }

    // ---- how often a call runs ----

    [Fact]
    public async Task A_volatile_call_runs_once_per_occurrence()
    {
        var counter = new Counter();
        var plan = IrBuilder.Plan(Project(
            TxnRead(),
            [("category", FieldAccess(ClassifyCall(), 0)), ("confidence", FieldAccess(ClassifyCall(), 1))]));

        await RunAsync(Compile(plan, [ClassifyDeclaration(Volatility.Volatile)], [ClassifyHost(counter)]));

        Assert.Equal(2 * (Rows - (Rows / 4)), counter.Calls);
    }

    [Fact]
    public async Task A_stable_call_of_constants_runs_once_per_execution()
    {
        var counter = new Counter();
        var today = Fn("today", Struct(F("Category", Str()), F("Confidence", Fp64())));
        var plan = IrBuilder.Plan(Project(
            TxnRead(),
            [("category", FieldAccess(today, 0)), ("confidence", FieldAccess(today, 1)), ("c", today)]));

        var rows = await RunAsync(Compile(plan, [TodayDeclaration()], [TodayHost(counter)], batchSize: 512));

        // Once per execution for each of the three occurrences, over eight batches: each is broadcast
        // child by child from the one row it answered.
        Assert.Equal(3, counter.Calls);
        Assert.Equal(Rows, rows.Count);
        Assert.All(rows, r => Assert.Equal("large", r[0]));
        Assert.All(rows, r => Assert.Equal(["large", 1.0], (object?[])r[2]!));
    }

    // ---- allocation gates ----

    /// <summary>
    /// D294's claim, as the corpus functions' gates make theirs: a struct call and its two fields add
    /// nothing per batch over the engine's own floor, measured against the same two-column projection
    /// written with built-ins. Both sides read and write the same column kinds.
    /// </summary>
    [Fact]
    public async Task A_struct_result_and_its_field_accesses_allocate_nothing_per_batch()
    {
        var withStruct = IrBuilder.Plan(Project(
            TxnRead(),
            [("category", FieldAccess(ClassifyCall(), 0)), ("confidence", FieldAccess(ClassifyCall(), 1))]));
        var floor = IrBuilder.Plan(Project(
            TxnRead(),
            [
                ("category", Ref(TxnRow, 1)),
                ("confidence", Call(FunctionId.Divide, Fp64(nullable: true), Ref(TxnRow, 2), Lit(1000.0))),
            ]));

        var measuredFloor = await Measure(floor, [], []);
        var measured = await Measure(withStruct, [ClassifyDeclaration()], [ClassifyHost(new Counter())]);

        _output.WriteLine(
            $"struct scalar: {measured} bytes over 8 batches against built-ins' {measuredFloor} = "
            + $"{(measured - measuredFloor) / 8.0:0.###} bytes/batch.");
        Assert.True(
            measured <= measuredFloor,
            $"a warm struct call cost {(measured - measuredFloor) / 8.0:0.###} bytes per batch more "
            + $"than the same projection written with built-ins ({measured} against {measuredFloor}).");
    }

    /// <summary>
    /// The same of an aggregate: a struct-valued measure emitted per group allocates nothing over a
    /// scalar measure's emit, the fields written straight into the measure column's children.
    /// </summary>
    [Fact]
    public async Task A_struct_aggregate_s_emit_allocates_nothing_per_group()
    {
        var summary = Struct(nullable: true, F("Total", Fp64()), F("Count", I64()));
        var grouped = HashAggregate(
            TxnRead(), [0], [("s", UserAgg("main.summarize", summary, Ref(TxnRow, 2)))]);
        var withStruct = IrBuilder.Plan(Project(
            grouped,
            [("total", FieldAccess(Ref(grouped.RowType, 1), 0)), ("n", FieldAccess(Ref(grouped.RowType, 1), 1))]));
        var scalar = HashAggregate(
            TxnRead(),
            [0],
            [("t", Agg(AggregateFunctionId.Sum, Fp64(nullable: true), Ref(TxnRow, 2))), ("n", Agg(AggregateFunctionId.Count, I64(), Ref(TxnRow, 2)))]);
        var floor = IrBuilder.Plan(Project(
            scalar,
            [("total", Ref(scalar.RowType, 1)), ("n", Ref(scalar.RowType, 2))]));

        var measuredFloor = await Measure(floor, [], []);
        var measured = await Measure(withStruct, [SummarizeDeclaration()], [SummarizeHost()]);

        _output.WriteLine(
            $"struct aggregate: {measured} bytes over {Rows} groups against built-ins' {measuredFloor}.");
        Assert.True(
            measured <= measuredFloor,
            $"a warm struct aggregate cost {measured - measuredFloor} bytes more over {Rows} groups "
            + $"than SUM and COUNT did ({measured} against {measuredFloor}).");
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

using System.Diagnostics.CodeAnalysis;
using Apache.Arrow;
using Chalk.Catalog;
using Chalk.Ir;
using Chalk.Sources;
using Chalk.Sources.Poco;
using Chalk.TestKit;
using static Chalk.TestKit.IrBuilder;
using CatalogContext = Chalk.Catalog.CatalogContext;
using FunctionDescriptor = Chalk.Catalog.FunctionDescriptor;
using IrType = Chalk.Ir.Type;

namespace Chalk.Execution.Tests;

/// <summary>
/// Sharing widened to every repeated subtree within an operator (D299): a built-in call, a cast, a
/// <c>CASE</c>, an <c>IN</c> and a field access named twice compile to one node that runs once per
/// batch; a field reference, a literal or a parameter is never shared; and a subtree holding a
/// <c>VOLATILE</c> call is never shared, so the call still runs once per occurrence. The counts come
/// from the compiled plan's per-operator tally; the answers are checked on both engines.
/// </summary>
[Experimental("CHALK001")]
public sealed class SharedSubtreeTests
{
    public sealed record Person(long Id, string Name, double Score, int Tier);

    /// <summary>A composite a function answers, to take apart twice.</summary>
    public readonly record struct Grade(Utf8String Band, double Score);

    private const int Rows = 4096;
    private const int Batches = 8;

    private static readonly string[] Names = ["x", "ada", "X", "grace", "linus"];

    private static readonly RowType PersonRow = Row(
        F("Id", I64()), F("Name", Str()), F("Score", Fp64()), F("Tier", I32()));

    private sealed class Counter
    {
        public long Calls;
    }

    private static Person[] People()
    {
        var rows = new Person[Rows];
        for (var i = 0; i < Rows; i++)
        {
            rows[i] = new Person(i, Names[i % Names.Length], i % 100, i % 7);
        }

        return rows;
    }

    private static Rel PeopleRead() => Read("people", PersonRow, rows: Rows);

    private static Expr Name() => Ref(PersonRow, 1);

    private static Expr Upper(Expr text) => Call(FunctionId.Upper, Str(text.Type.Nullable), text);

    private static FunctionDescriptor NoiseDeclaration() =>
        new FunctionBuilder("noise").Scalar<string, string>("s").Volatile().Client().Build();

    private static HostFunction NoiseHost(Counter counter) =>
        new HostScalar1<string, string>("noise", s =>
        {
            counter.Calls++;
            return s;
        });

    private static FunctionDescriptor GradeDeclaration() =>
        new FunctionBuilder("grade").Scalar<double, Grade>("score").Strict().Client().Build();

    private static readonly Utf8String High = "high"u8.ToArray();
    private static readonly Utf8String Low = "low"u8.ToArray();

    private static HostFunction GradeHost(Counter counter) =>
        new HostScalar1<double, Grade>("grade", score =>
        {
            counter.Calls++;
            return new Grade(score >= 50 ? High : Low, score);
        });

    private static CompiledPlan Compile(
        Plan plan,
        IReadOnlyList<FunctionDescriptor> functions,
        IReadOnlyList<HostFunction> hosts,
        bool reference = false)
    {
        var source = new PocoSourceBuilder("mem").AddTable("people", People()).Build();
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
                BatchSize = Rows / Batches,
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

    /// <summary>Runs the plan vectorised, checks the reference executor agrees, and hands back the tally.</summary>
    private static async Task<(List<object?[]> Rows, Expressions.SharingTally Tally)> RunSharedAsync(
        Plan plan,
        string path,
        IReadOnlyList<FunctionDescriptor>? functions = null,
        IReadOnlyList<HostFunction>? hosts = null)
    {
        var compiled = Compile(plan, functions ?? [], hosts ?? []);
        var rows = await RunAsync(compiled);
        var reference = await RunAsync(Compile(plan, functions ?? [], hosts ?? [], reference: true));
        Assert.Equal(reference.Count, rows.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            Assert.Equal(Render(reference[i]), Render(rows[i]));
        }

        return (rows, compiled.Sharing[path]);
    }

    private static string Render(object?[] row) =>
        string.Join("|", row.Select(v => v switch
        {
            null => "NULL",
            object?[] fields => "(" + Render(fields) + ")",
            double d => d.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            _ => v.ToString(),
        }));

    [Fact]
    public async Task A_built_in_call_named_twice_runs_once_per_batch()
    {
        // The design's measurement: CASE WHEN UPPER(name) = 'X' THEN UPPER(name) END.
        var upperIsX = Call(FunctionId.Eq, Bool(), Upper(Name()), Lit("X"));
        var plan = IrBuilder.Plan(Project(
            PeopleRead(),
            [("id", Ref(PersonRow, 0)), ("shout", Case(Str(nullable: true), Null(Str(nullable: true)), (upperIsX, Upper(Name()))))]));

        var (rows, tally) = await RunSharedAsync(plan, "root/Project");

        Assert.Equal(1, tally.SharedNodes);
        Assert.Equal(Batches, tally.Computed);
        Assert.Equal(Batches, tally.Reused);
        Assert.All(rows, row => Assert.Equal(
            Names[(int)((long)row[0]! % Names.Length)] is "x" or "X" ? "X" : null, row[1]));
    }

    [Fact]
    public async Task A_cast_an_in_and_a_case_named_twice_are_each_one_node()
    {
        var asText = Cast(Ref(PersonRow, 3), Str());
        var inTop = In(Ref(PersonRow, 3), Bool(), Lit(1), Lit(2), Lit(3));
        var band = Case(Str(), Lit("rest"), (inTop, Lit("top")));
        var plan = IrBuilder.Plan(Project(
            PeopleRead(),
            [
                ("id", Ref(PersonRow, 0)),
                ("text", asText),
                ("text_again", Call(FunctionId.Concat, Str(), asText, Lit("!"))),
                ("top", inTop),
                ("band", band),
                ("band_again", band),
            ]));

        var (rows, tally) = await RunSharedAsync(plan, "root/Project");

        // CAST, IN and CASE are each named twice; the CASE's own IN is the same node as the one
        // projected, so it is not a fourth.
        Assert.Equal(3, tally.SharedNodes);
        Assert.Equal(3 * Batches, tally.Computed);
        Assert.All(rows, row =>
        {
            var tier = (int)((long)row[0]! % 7);
            Assert.Equal(tier.ToString(System.Globalization.CultureInfo.InvariantCulture), row[1]);
            Assert.Equal(row[1] + "!", row[2]);
            Assert.Equal(tier is 1 or 2 or 3, row[3]);
            Assert.Equal(tier is 1 or 2 or 3 ? "top" : "rest", row[4]);
            Assert.Equal(row[4], row[5]);
        });
    }

    [Fact]
    public async Task A_field_access_named_twice_is_one_node_over_one_call()
    {
        var counter = new Counter();
        var gradeType = GradeDeclaration().ReturnType!.Value.ToProto();
        var grade = UserCall("main.grade", gradeType, Ref(PersonRow, 2));
        var band = FieldAccess(grade, 0);
        var plan = IrBuilder.Plan(Project(
            PeopleRead(),
            [
                ("id", Ref(PersonRow, 0)),
                ("band", band),
                ("loud", Upper(band)),
                ("score", FieldAccess(grade, 1)),
            ]));

        var (rows, tally) = await RunSharedAsync(plan, "root/Project", [GradeDeclaration()], [GradeHost(new Counter())]);

        // The call is under both field accesses, and the band access under UPPER as well.
        Assert.Equal(2, tally.SharedNodes);
        Assert.All(rows, row => Assert.Equal(((string)row[1]!).ToUpperInvariant(), row[2]));

        // The vectorised engine calls the function once per row for all three uses of it; the
        // reference executor, which shares nothing, is only there for the answers.
        _ = await RunAsync(Compile(plan, [GradeDeclaration()], [GradeHost(counter)]));
        Assert.Equal(Rows, counter.Calls);
    }

    [Fact]
    public async Task A_filter_and_an_aggregate_share_within_themselves()
    {
        var upper = Upper(Name());
        var filter = Filter(
            PeopleRead(),
            Call(
                FunctionId.Or,
                Bool(),
                Call(FunctionId.Eq, Bool(), upper, Lit("ADA")),
                Call(FunctionId.Eq, Bool(), upper, Lit("GRACE"))));
        var doubled = Call(FunctionId.Multiply, Fp64(), Ref(PersonRow, 2), Lit(2.0));
        var grouped = HashAggregate(
            filter,
            [3],
            [
                ("total", Agg(AggregateFunctionId.Sum, Fp64(nullable: true), doubled)),
                ("most", Agg(AggregateFunctionId.Max, Fp64(nullable: true), doubled)),
            ]);
        var plan = IrBuilder.Plan(grouped);

        var compiled = Compile(plan, [], []);
        var rows = await RunAsync(compiled);

        Assert.Equal(1, compiled.Sharing["root/HashAggregate/Filter"].SharedNodes);
        Assert.Equal(1, compiled.Sharing["root/HashAggregate"].SharedNodes);
        var expected = People()
            .Where(p => p.Name is "ada" or "grace")
            .GroupBy(p => p.Tier)
            .ToDictionary(g => (long)g.Key, g => (g.Sum(p => p.Score * 2), g.Max(p => p.Score * 2)));
        Assert.Equal(expected.Count, rows.Count);
        foreach (var row in rows)
        {
            var (total, most) = expected[(long)row[0]!];
            Assert.Equal(total, (double)row[1]!);
            Assert.Equal(most, (double)row[2]!);
        }
    }

    [Fact]
    public async Task A_volatile_call_under_a_built_in_runs_once_per_occurrence()
    {
        var counter = new Counter();
        var noisy = Upper(UserCall("main.noise", Str(nullable: true), Name()));
        var plan = IrBuilder.Plan(Project(
            PeopleRead(),
            [
                ("id", Ref(PersonRow, 0)),
                ("shout", Case(
                    Str(nullable: true),
                    Null(Str(nullable: true)),
                    (Call(FunctionId.Eq, Bool(nullable: true), noisy, Lit("X")), noisy))),
            ]));

        var compiled = Compile(plan, [NoiseDeclaration()], [NoiseHost(counter)]);
        var rows = await RunAsync(compiled);

        // Neither UPPER(noise(name)) nor noise(name) is shared, so the delegate runs twice a row.
        Assert.Equal(0, compiled.Sharing["root/Project"].SharedNodes);
        Assert.Equal(2L * Rows, counter.Calls);
        Assert.Equal(Rows, rows.Count);
    }

    [Fact]
    public async Task Field_references_literals_and_parameters_are_never_shared()
    {
        var plan = IrBuilder.Plan(
            Project(
                PeopleRead(),
                [
                    ("a", Ref(PersonRow, 2)),
                    ("b", Ref(PersonRow, 2)),
                    ("c", Lit(7L)),
                    ("d", Lit(7L)),
                    ("e", Param(0, I64(nullable: true))),
                    ("f", Param(0, I64(nullable: true))),
                ]),
            parameterTypes: [I64(nullable: true)]);

        var compiled = Compile(plan, [], []);
        await foreach (var batch in compiled.ExecuteAsync([5L], new ExecutionStats(), arena: null, CancellationToken.None))
        {
            batch.Dispose();
        }

        Assert.Equal(0, compiled.Sharing["root/Project"].SharedNodes);
    }
}

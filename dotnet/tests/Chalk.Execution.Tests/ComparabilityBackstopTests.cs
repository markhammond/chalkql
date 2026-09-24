using System.Diagnostics.CodeAnalysis;
using Chalk.Catalog;
using Chalk.Execution.Tests.Harness;
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
/// The executor's own refusal of a key it cannot compare (D297, F127): every place an operator binds
/// a key calls <c>ColumnKinds.RequireComparable</c>, which refuses a LIST or a COMPOSITE by name.
/// </summary>
/// <remarks>
/// Each plan is compiled twice. Validated, the IR validator refuses it first (I-IR-23 for a
/// composite, I-IR-12 for a list), which is what every host's plan meets. Compiled without the
/// validator, the executor refuses it where the key is bound, naming the key — the backstop that had
/// no caller before this, and that a plan built by something other than Chalk's planner and not
/// validated would otherwise run into as a wrong answer or an unrelated failure.
/// </remarks>
[Experimental("CHALK001")]
public sealed class ComparabilityBackstopTests
{
    /// <summary>A transaction.</summary>
    public sealed record Txn(long Id, double? Amount);

    /// <summary>A composite result to key on.</summary>
    public readonly record struct Classification(Utf8String Category, double Confidence);

    private static readonly IrType ClassificationType = Composite(
        nullable: true, F("Category", Str()), F("Confidence", Fp64()));

    private static readonly RowType TxnRow = Row(F("Id", I64()), F("Amount", Fp64(nullable: true)));

    private static readonly TestTable Facts = new()
    {
        Name = "facts",
        Columns = [("k", ChalkType.Int64()), ("v", ChalkType.Int64())],
        Rows = [[1L, 2L]],
    };

    private static FunctionDescriptor ClassifyDeclaration() =>
        new FunctionBuilder("classify").Scalar<double, Classification>("amount").Strict().Client().Build();

    private static HostFunction ClassifyHost() =>
        new HostScalar1<double, Classification>(
            "classify", static amount => new Classification("x"u8.ToArray(), amount));

    /// <summary><c>id, classify(amount) AS c, amount</c> over the transactions: a composite column.</summary>
    private static Rel Composites() => Project(
        Read("txn", TxnRow, rows: 4),
        [
            ("id", Ref(TxnRow, 0)),
            ("c", UserCall("main.classify", ClassificationType, Ref(TxnRow, 1))),
            ("amount", Ref(TxnRow, 1)),
        ]);

    /// <summary><c>id, [id] AS l</c>: a LIST column.</summary>
    private static Rel Lists() => Project(
        Read("txn", TxnRow, rows: 4),
        [("id", Ref(TxnRow, 0)), ("l", LitList(I64(), Lit(1L), Lit(2L)))]);

    private static Rel Plan(string site)
    {
        var composites = Composites();
        var row = composites.RowType;
        return site switch
        {
            "sort" => Sort(composites, Asc(1, ClassificationType)),
            "top-n" => TopN(composites, 0, 5, Asc(1, ClassificationType)),
            "grouping" => HashAggregate(composites, [1], [("n", Agg(AggregateFunctionId.Count, I64()))]),
            "select-distinct" => HashAggregate(composites, [1], []),
            "distinct-measure" => HashAggregate(
                composites, [0], [("n", Agg(AggregateFunctionId.Count, I64(), Ref(row, 1), distinct: true))]),
            "ordered-measure" => HashAggregate(
                composites,
                [],
                [("ids", Agg(AggregateFunctionId.ArrayAgg, List(I64()), Ref(row, 0), orderBy: Asc(1, ClassificationType)))]),
            "hash-join" => HashJoin(composites, Composites(), [1], [1]),
            "merge-join" => MergeJoin(
                Collated(composites, (1, SortDirection.AscNullsLast)),
                Collated(Composites(), (1, SortDirection.AscNullsLast)),
                [1],
                [1]),
            "as-of-join-key" => AsOfJoin(composites, Composites(), [1], [1], 0, 0, AsOfMatch.Ge),
            "as-of-join-time" => AsOfJoin(composites, Composites(), [0], [0], 1, 1, AsOfMatch.Ge),
            "lookup-join" => LookupJoin(composites, Lookup(), 1, 0),
            "adaptive-join" => AdaptiveJoin(
                composites,
                LookupJoin(MaterialisedInput(composites), Lookup(), 1, 0),
                HashJoin(MaterialisedInput(composites), Lookup(), [1], [0]),
                key: 1,
                maxKeys: 10),
            "intersect" => SetOp(SetOpKind.IntersectDistinct, composites, Composites()),
            "window-partition" => Window(
                composites, [1], [], WholePartitionFrame(), [("n", WinAgg(AggregateFunctionId.Count, I64()))]),
            "window-order" => Window(
                composites,
                [],
                [Asc(1, ClassificationType)],
                RunningFrame(),
                [("n", WinAgg(AggregateFunctionId.Count, I64()))]),
            "window-distinct" => Window(
                composites,
                [0],
                [],
                WholePartitionFrame(),
                [("n", WinAggDistinct(AggregateFunctionId.Count, I64(), Ref(row, 1)))]),
            "session" => Session(composites, [1], 0, LitIntervalDay(1_000_000)),
            "sort-list" => Sort(Lists(), Asc(1, List(I64()))),
            _ => throw new ArgumentOutOfRangeException(nameof(site), site, "no such key site"),
        };
    }

    /// <summary>The lookup side: a remote query whose key column is a composite as well.</summary>
    private static Rel Lookup() => RemoteQuery(
        "far",
        "SELECT c, v FROM facts WHERE c IN (?)",
        Row(F("c", ClassificationType), F("v", I64())),
        parameters: [KeySet(ClassificationType)]);

    private static CompiledPlan Compile(Rel root, bool validate)
    {
        var source = new PocoSourceBuilder("mem")
            .AddTable("txn", new[] { new Txn(1, 10), new Txn(2, null), new Txn(3, 30), new Txn(4, 40) })
            .Build();
        var schema = source.DescribeSchema();
        var withFunctions = new SchemaDescriptor
        {
            SourceId = schema.SourceId,
            Name = schema.Name,
            Kind = schema.Kind,
            Capabilities = schema.Capabilities,
            Tables = schema.Tables,
            Functions = [ClassifyDeclaration()],
        };
        var catalog = new CatalogContext { ContextId = "test", Epoch = 1, Schemas = [withFunctions] };
        var sources = new Dictionary<string, ISourceRuntime>
        {
            ["mem"] = source,
            ["far"] = new FakeRemoteSource("far", Facts),
        };
        var settings = new ExecutionSettings
        {
            Functions = new HostFunctionSet(
                new Dictionary<string, HostFunction>(StringComparer.OrdinalIgnoreCase)
                {
                    ["classify"] = ClassifyHost(),
                }),
        };
        var plan = IrBuilder.Plan(root);
        return validate
            ? PlanCompiler.Compile(plan, catalog, sources, settings)
            : PlanCompiler.CompileUnvalidatedForTests(plan, catalog, sources, settings);
    }

    [Theory]
    [InlineData("sort", "a sort key", "COMPOSITE")]
    [InlineData("top-n", "a sort key", "COMPOSITE")]
    [InlineData("grouping", "a grouping key", "COMPOSITE")]
    [InlineData("select-distinct", "a grouping key", "COMPOSITE")]
    [InlineData("distinct-measure", "a DISTINCT aggregate's argument", "COMPOSITE")]
    [InlineData("ordered-measure", "an aggregate's ORDER BY key", "COMPOSITE")]
    [InlineData("hash-join", "a join key", "COMPOSITE")]
    [InlineData("merge-join", "a join key", "COMPOSITE")]
    [InlineData("as-of-join-key", "a join key", "COMPOSITE")]
    [InlineData("as-of-join-time", "an as-of join's time column", "COMPOSITE")]
    [InlineData("lookup-join", "a join key", "COMPOSITE")]
    [InlineData("adaptive-join", "an adaptive join's key", "COMPOSITE")]
    [InlineData("intersect", "the column 'c' of a row INTERSECT compares", "COMPOSITE")]
    [InlineData("window-partition", "a window partition key", "COMPOSITE")]
    [InlineData("window-order", "a window order key", "COMPOSITE")]
    [InlineData("window-distinct", "a DISTINCT window aggregate's argument", "COMPOSITE")]
    [InlineData("session", "a session partition key", "COMPOSITE")]
    [InlineData("sort-list", "a sort key", "LIST")]
    public void A_key_the_engine_cannot_compare_is_refused_where_it_is_bound(
        string site, string key, string kind)
    {
        // The validator is first: this is what a host's plan meets.
        var validated = Assert.Throws<InvalidPlanException>(() => Compile(Plan(site), validate: true));
        Assert.Equal(kind == "LIST" ? "I-IR-12" : "I-IR-23", validated.Invariant);

        // Unvalidated, the executor refuses it itself, naming the key and the type.
        var refused = Assert.Throws<UnsupportedFeatureException>(() => Compile(Plan(site), validate: false));
        Assert.Equal($"{key} on a {kind}", refused.Feature);
        Assert.Contains("no ordering or equality", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_distinct_aggregate_over_a_list_is_refused_where_the_validator_lets_it_through()
    {
        // The validator refuses a composite argument of a built-in aggregate and not a list, so this
        // plan validates. The executor read a list's lane as zero bytes wide, which made every list
        // hash and compare equal: COUNT(DISTINCT l) would have counted one. It is refused instead.
        var lists = Lists();
        var plan = HashAggregate(
            lists, [0], [("n", Agg(AggregateFunctionId.Count, I64(), Ref(lists.RowType, 1), distinct: true))]);

        var refused = Assert.Throws<UnsupportedFeatureException>(() => Compile(plan, validate: true));

        Assert.Equal("a DISTINCT aggregate's argument on a LIST", refused.Feature);
    }

    [Fact]
    public void The_same_plans_with_a_scalar_key_compile()
    {
        // The backstop refuses the type and nothing else: the shapes above over the scalar column
        // they sit beside compile, validated or not.
        var composites = Composites();
        Rel[] plans =
        [
            Sort(composites, Asc(0, I64())),
            HashAggregate(composites, [0], [("n", Agg(AggregateFunctionId.Count, I64(), Ref(composites.RowType, 2), distinct: true))]),
            HashJoin(composites, Composites(), [0], [0]),
            SetOp(SetOpKind.UnionAll, composites, Composites()),
            Window(composites, [0], [Asc(2, Fp64(nullable: true))], RunningFrame(), [("n", WinAgg(AggregateFunctionId.Count, I64()))]),
        ];

        foreach (var root in plans)
        {
            _ = Compile(root, validate: true);
            _ = Compile(root, validate: false);
        }
    }
}

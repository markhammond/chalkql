using Chalk.Catalog;
using Chalk.Execution.Tests.Harness;
using Chalk.Ir;
using Chalk.Sources;
using Chalk.TestKit;

namespace Chalk.Execution.Tests;

/// <summary>
/// What <c>PlanCompiler</c> promises (§6.8): the plan is validated first, every node kind goes through
/// the registry, and everything M1 cannot run is refused here rather than mid-stream.
/// </summary>
public sealed class PlanCompilerTests
{
    private static readonly TestTable Table = TestData.Series(5);
    private static readonly TestSource Source = TestData.Source(Table, TestData.Numbers);

    [Fact]
    public void An_invalid_plan_is_rejected_before_anything_is_resolved()
    {
        var row = Table.RowType();
        var plan = IrBuilder.Plan(IrBuilder.Read(Table.Name, row));
        plan.PlanDigest = 1;

        Assert.Throws<InvalidPlanException>(() => Runner.Compile(plan, Source));
    }

    [Fact]
    public void Compilation_exposes_the_output_schema_and_the_parameter_types()
    {
        var row = TestData.Numbers.RowType();
        var filter = IrBuilder.Filter(
            IrBuilder.Read(TestData.Numbers.Name, row),
            IrBuilder.Call(
                FunctionId.Eq,
                IrBuilder.Bool(true),
                IrBuilder.Ref(row, 0),
                IrBuilder.Param(0, IrBuilder.I32(true))));
        var plan = IrBuilder.Plan(filter, parameterTypes: [IrBuilder.I32(true)]);

        var compiled = Runner.Compile(plan, Source);

        Assert.Equal(7, compiled.OutputSchema.FieldsList.Count);
        Assert.Equal("i32", compiled.OutputSchema.FieldsList[0].Name);
        Assert.Equal([ChalkType.Int32(nullable: true)], compiled.ParameterTypes);
        Assert.Same(plan, compiled.Plan);
    }

    public static TheoryData<Rel.KindOneofCase> UnsupportedKinds()
    {
        var data = new TheoryData<Rel.KindOneofCase>();
        foreach (var kind in new[]
                 {
                     // IndexLookup left the list in M2: the compiler runs it, and a plan naming an
                     // index the table does not declare is an InvalidPlanException rather than an
                     // unsupported one (IndexLookupOperatorTests covers both). The four joins and the
                     // logical Join left it in step 17 (12-joins.md §4), Window in step 18
                     // (13-window-functions.md §4) — WindowOperatorTests covers what it still
                     // refuses: a GROUPS frame, a DISTINCT call and a function the IR has no id for
                     // — and SetOp in step 20 (15-zero-allocation-execution.md §6, D69).
                     // RemoteQuery left it in step 21 (18-m4-capabilities-and-pushdown.md §2, D83):
                     // the compiler builds a RemoteQueryOperator, and what it still refuses — a
                     // source id the engine does not hold, a parameter that is not a DynamicParam —
                     // is covered where the reason is legible.
                     Rel.KindOneofCase.StreamAggregate,
                 })
        {
            data.Add(kind);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(UnsupportedKinds))]
    public void Every_node_kind_out_of_M1_is_refused_at_compilation(Rel.KindOneofCase kind)
    {
        var plan = IrBuilder.Plan(NodeOfKind(kind));

        var failure = Assert.Throws<UnsupportedFeatureException>(
            () => PlanCompiler.Compile(
                plan,
                Source.Catalog(),
                new Dictionary<string, ISourceRuntime> { [Source.SourceId] = Source },
                new ExecutionSettings()));

        Assert.Equal(kind.ToString(), failure.Feature);
    }

    [Fact]
    public void A_read_of_an_unknown_source_is_refused()
    {
        var row = Table.RowType();
        var plan = IrBuilder.Plan(IrBuilder.Read(Table.Name, row, sourceId: "elsewhere"));

        var failure = Assert.Throws<UnsupportedFeatureException>(
            () => PlanCompiler.Compile(
                plan,
                Source.Catalog(),
                new Dictionary<string, ISourceRuntime> { [Source.SourceId] = Source },
                new ExecutionSettings()));

        Assert.Contains("elsewhere", failure.Feature, StringComparison.Ordinal);
    }

    [Fact]
    public void A_pushed_read_filter_is_refused_until_M4()
    {
        var row = Table.RowType();
        var read = IrBuilder.Read(Table.Name, row);
        read.Read.Filter = IrBuilder.Lit(true);
        var plan = IrBuilder.Plan(read);

        // The validator refuses it first; with that check relaxed the compiler refuses it too.
        Assert.Throws<InvalidPlanException>(() => Runner.Compile(plan, Source));
    }

    [Fact]
    public void A_batch_size_below_one_is_refused()
    {
        var plan = IrBuilder.Plan(IrBuilder.Read(Table.Name, Table.RowType()));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => PlanCompiler.Compile(
                plan,
                Source.Catalog(),
                new Dictionary<string, ISourceRuntime> { [Source.SourceId] = Source },
                new ExecutionSettings { BatchSize = 0 }));
    }

    [Fact]
    public async Task A_parameter_of_the_wrong_type_is_named_in_the_exception()
    {
        var row = TestData.Numbers.RowType();
        var filter = IrBuilder.Filter(
            IrBuilder.Read(TestData.Numbers.Name, row),
            IrBuilder.Call(
                FunctionId.Eq,
                IrBuilder.Bool(true),
                IrBuilder.Ref(row, 5),
                IrBuilder.Param(0, IrBuilder.Str(true))));
        var compiled = Runner.Compile(
            IrBuilder.Plan(filter, parameterTypes: [IrBuilder.Str(true)]), Source);

        var failure = Assert.Throws<ArgumentException>(
            () => compiled.ExecuteAsync([42], new ExecutionStats(), arena: null, CancellationToken.None));

        Assert.Contains("Parameter 0", failure.Message, StringComparison.Ordinal);
        await Task.CompletedTask;
    }

    [Fact]
    public void A_missing_parameter_is_refused_before_execution()
    {
        var row = TestData.Numbers.RowType();
        var filter = IrBuilder.Filter(
            IrBuilder.Read(TestData.Numbers.Name, row),
            IrBuilder.Call(
                FunctionId.Eq,
                IrBuilder.Bool(true),
                IrBuilder.Ref(row, 0),
                IrBuilder.Param(0, IrBuilder.I32(true))));
        var compiled = Runner.Compile(
            IrBuilder.Plan(filter, parameterTypes: [IrBuilder.I32(true)]), Source);

        Assert.Throws<ArgumentException>(
            () => compiled.ExecuteAsync([], new ExecutionStats(), arena: null, CancellationToken.None));
    }

    [Fact]
    public void A_null_bound_to_a_non_nullable_parameter_is_refused()
    {
        var row = TestData.Numbers.RowType();
        var filter = IrBuilder.Filter(
            IrBuilder.Read(TestData.Numbers.Name, row),
            IrBuilder.Call(
                FunctionId.Eq,
                IrBuilder.Bool(true),
                IrBuilder.Ref(row, 0),
                IrBuilder.Param(0, IrBuilder.I32())));
        var compiled = Runner.Compile(IrBuilder.Plan(filter, parameterTypes: [IrBuilder.I32()]), Source);

        Assert.Throws<ArgumentException>(
            () => compiled.ExecuteAsync([null], new ExecutionStats(), arena: null, CancellationToken.None));
    }

    [Fact]
    public async Task A_failure_carries_the_plan_digest_and_the_operator_path()
    {
        var table = TestData.Extremes;
        var source = TestData.Source(table);
        var row = table.RowType();
        var project = IrBuilder.Project(
            IrBuilder.Read(table.Name, row),
            [("boom", IrBuilder.Call(
                FunctionId.Add, IrBuilder.I64(true), IrBuilder.Ref(row, 0), IrBuilder.Lit(1L)))]);
        var plan = IrBuilder.Plan(project);

        var failure = await Assert.ThrowsAsync<ExecutionException>(
            () => Runner.RowsAsync(plan, source));

        Assert.Equal(plan.PlanDigest, failure.PlanDigest);
        Assert.Equal("root/Project", failure.OperatorPath);
        Assert.Contains("root/Project", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_path_names_the_operator_that_broke_not_the_root()
    {
        var table = TestData.Extremes;
        var source = TestData.Source(table);
        var row = table.RowType();
        var filter = IrBuilder.Filter(
            IrBuilder.Read(table.Name, row),
            IrBuilder.Call(
                FunctionId.Gt,
                IrBuilder.Bool(true),
                IrBuilder.Call(
                    FunctionId.Multiply, IrBuilder.I64(true), IrBuilder.Ref(row, 0), IrBuilder.Lit(2L)),
                IrBuilder.Lit(0L)));
        var sort = IrBuilder.Sort(filter, IrBuilder.Asc(0, row.Fields[0].Type));

        var failure = await Assert.ThrowsAsync<ExecutionException>(
            () => Runner.RowsAsync(IrBuilder.Plan(sort), source));

        Assert.Equal("root/Sort/Filter", failure.OperatorPath);
    }

    [Fact]
    public async Task Cancellation_surfaces_as_cancellation_not_as_an_execution_failure()
    {
        var plan = IrBuilder.Plan(IrBuilder.Read(Table.Name, Table.RowType()));
        var compiled = Runner.Compile(plan, Source, batchSize: 1);
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var batch in compiled.ExecuteAsync([], new ExecutionStats(), arena: null, cancellation.Token))
            {
                batch.Dispose();
                await cancellation.CancelAsync();
            }
        });
    }

    [Fact]
    public async Task The_reference_engine_is_reached_through_the_same_compiled_plan()
    {
        var plan = IrBuilder.Plan(IrBuilder.Read(Table.Name, Table.RowType()));
        var compiled = Runner.Compile(plan, Source, reference: true);

        var batches = await Runner.CollectAsync(compiled);
        try
        {
            Assert.Equal(5, batches.Sum(b => b.Length));
        }
        finally
        {
            foreach (var batch in batches)
            {
                batch.Dispose();
            }
        }
    }

    /// <summary>A structurally valid node of a kind M1 does not execute.</summary>
    private static Rel NodeOfKind(Rel.KindOneofCase kind)
    {
        var row = Table.RowType();
        var input = IrBuilder.Read(Table.Name, row);
        var joined = IrBuilder.Row([.. row.Fields, .. row.Fields]);
        var rel = new Rel { RowType = kind is Rel.KindOneofCase.Join or Rel.KindOneofCase.HashJoin
            or Rel.KindOneofCase.MergeJoin or Rel.KindOneofCase.NestedLoopJoin ? joined : row };

        switch (kind)
        {
            case Rel.KindOneofCase.Join:
                rel.Join = new Join { Left = input, Right = input, Type = JoinType.Inner };
                break;
            case Rel.KindOneofCase.SetOp:
                rel.SetOp = new SetOp { Kind = SetOpKind.UnionAll, Inputs = { input, input } };
                break;
            case Rel.KindOneofCase.Window:
                rel.Window = new Window { Input = input };
                break;
            case Rel.KindOneofCase.IndexLookup:
                rel.IndexLookup = new IndexLookup
                {
                    Table = new TableRef { SourceId = "mem", Schema = "main", Table = Table.Name },
                    Index = "ix",
                    Projection = { 0u, 1u, 2u },
                };
                break;
            case Rel.KindOneofCase.RemoteQuery:
                rel.RemoteQuery = new RemoteQuery
                {
                    SourceId = "mem", Dialect = "ansi", QueryText = "SELECT 1",
                };
                break;
            case Rel.KindOneofCase.HashJoin:
                rel.HashJoin = new HashJoin { Left = input, Right = input, Type = JoinType.Inner };
                break;
            case Rel.KindOneofCase.MergeJoin:
                rel.MergeJoin = new MergeJoin { Left = input, Right = input, Type = JoinType.Inner };
                break;
            case Rel.KindOneofCase.NestedLoopJoin:
                rel.NestedLoopJoin = new NestedLoopJoin { Left = input, Right = input, Type = JoinType.Inner };
                break;
            default:
            {
                var aggregate = new Aggregate { Input = input };
                aggregate.Groupings.Add(new Grouping { Keys = { 0u } });
                rel.RowType = IrBuilder.Row(row.Fields[0]);
                rel.StreamAggregate = new StreamAggregate { Aggregate = aggregate };
                break;
            }
        }

        return rel;
    }
}

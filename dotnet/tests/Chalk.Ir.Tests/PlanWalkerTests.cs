using Chalk.TestKit;
using static Chalk.TestKit.IrBuilder;

namespace Chalk.Ir.Tests;

public sealed class PlanWalkerTests
{
    private static readonly RowType Bars = Row(
        F("symbol", Str()),
        F("ts", Timestamp()),
        F("close", Fp64()),
        F("volume", I64()));

    private static Plan Sample()
    {
        var read = Read("bars", Bars, rows: 100_800);
        var filter = Filter(read, Call(FunctionId.Eq, Bool(), Ref(Bars, 0), Lit("BTCUSDT")), rows: 20_160);
        var project = Project(
            filter,
            [("symbol", Ref(Bars, 0)), ("close", Ref(Bars, 2))],
            rows: 20_160);
        return IrBuilder.Plan(project);
    }

    [Fact]
    public void Rels_are_enumerated_root_first()
    {
        var kinds = PlanWalker.Rels(Sample()).Select(r => r.KindCase).ToArray();

        Assert.Equal(
            [Rel.KindOneofCase.Project, Rel.KindOneofCase.Filter, Rel.KindOneofCase.Read],
            kinds);
    }

    [Fact]
    public void Own_exprs_are_the_node_s_own_and_not_its_input_s()
    {
        var project = PlanWalker.Rels(Sample()).First();

        var own = PlanWalker.OwnExprs(project).ToArray();

        Assert.Equal(2, own.Length);
        Assert.All(own, e => Assert.Equal(Expr.KindOneofCase.FieldRef, e.KindCase));
    }

    [Fact]
    public void Exprs_walks_call_arguments_in_order()
    {
        var condition = Call(
            FunctionId.And,
            Bool(),
            Call(FunctionId.Eq, Bool(), Ref(Bars, 0), Lit("BTCUSDT")),
            Call(FunctionId.Gt, Bool(), Ref(Bars, 3), Lit(5000L)));

        var printed = PlanWalker.Exprs(condition).Select(PlanPrinter.Print).ToArray();

        Assert.Equal(
            [
                "AND(EQ($0, 'BTCUSDT'), GT($3, 5000))",
                "EQ($0, 'BTCUSDT')",
                "$0",
                "'BTCUSDT'",
                "GT($3, 5000)",
                "$3",
                "5000",
            ],
            printed);
    }

    [Fact]
    public void Has_and_count_answer_the_corpus_expectations()
    {
        var plan = Sample();

        Assert.True(PlanWalker.Has(plan, Rel.KindOneofCase.Read));
        Assert.False(PlanWalker.Has(plan, Rel.KindOneofCase.Sort));
        Assert.Equal(1, PlanWalker.Count(plan, Rel.KindOneofCase.Filter));
        Assert.Equal(0, PlanWalker.Count(plan, Rel.KindOneofCase.HashAggregate));
    }

    [Fact]
    public void All_exprs_covers_every_relation()
    {
        var plan = Sample();

        var count = PlanWalker.AllExprs(plan).Count();

        // project: 2 field refs; filter: EQ + 2 args; read: nothing.
        Assert.Equal(5, count);
    }

    [Fact]
    public void Inputs_of_a_leaf_are_empty()
    {
        var read = Read("bars", Bars);

        Assert.Empty(PlanWalker.Inputs(read));
    }

    // ---------------------------------------------------------------- the two walks (F92)

    /// <summary>
    /// A plan whose only read of <c>bars</c> is inside a pushed query: the source runs it, and
    /// nothing this client executes mentions the table.
    /// </summary>
    private static Plan Pushed()
    {
        var remote = IrBuilder.RemoteQuery("warehouse", "SELECT \"symbol\" FROM \"bars\"", Bars);
        remote.RemoteQuery.PushedPlan = Read("bars", Bars, rows: 100_800);
        return IrBuilder.Plan(Project(remote, [("symbol", Ref(Bars, 0))], rows: 100_800));
    }

    /// <summary>
    /// The inclusive walk answers about the statement: a table read only under a pushed query is a
    /// table the statement reads, which is what staleness, the digest and the policy checks are
    /// about.
    /// </summary>
    [Fact]
    public void The_inclusive_walk_sees_a_read_only_a_pushed_query_makes()
    {
        var kinds = PlanWalker.Rels(Pushed()).Select(r => r.KindCase).ToArray();

        Assert.Equal(
            [Rel.KindOneofCase.Project, Rel.KindOneofCase.RemoteQuery, Rel.KindOneofCase.Read],
            kinds);
    }

    /// <summary>
    /// And <c>Has</c> and <c>Count</c> are the executed walk's, because they are the corpus's
    /// <c>has(…)</c> and <c>not(…)</c> vocabulary: a pushdown query's <c>not(Filter)</c> says the
    /// filter reached the source, so counting what went with it would make the expectation
    /// unsatisfiable by the very plan it describes.
    /// </summary>
    [Fact]
    public void Has_and_count_are_about_the_plan_as_executed()
    {
        Assert.False(PlanWalker.Has(Pushed(), Rel.KindOneofCase.Read));
        Assert.Equal(0, PlanWalker.Count(Pushed(), Rel.KindOneofCase.Read));
        Assert.True(PlanWalker.Has(Pushed(), Rel.KindOneofCase.RemoteQuery));
    }

    /// <summary>
    /// The executed walk answers about this client: the boundary is a leaf, because below it is the
    /// source's work — which is what parameter binding, the printer and any counting of the
    /// operators this executor will run are about.
    /// </summary>
    [Fact]
    public void The_executed_walk_stops_at_the_boundary()
    {
        var kinds = PlanWalker.ExecutedRels(Pushed()).Select(r => r.KindCase).ToArray();

        Assert.Equal([Rel.KindOneofCase.Project, Rel.KindOneofCase.RemoteQuery], kinds);
    }

    /// <summary>A pushed plan is not an input, and <c>Pushed</c> is where it lives.</summary>
    [Fact]
    public void A_pushed_plan_is_not_an_input()
    {
        var remote = PlanWalker.ExecutedRels(Pushed())
            .Single(r => r.KindCase == Rel.KindOneofCase.RemoteQuery);

        Assert.Empty(PlanWalker.Inputs(remote));
        Assert.Equal(Rel.KindOneofCase.Read, Assert.Single(PlanWalker.Pushed(remote)).KindCase);
        Assert.Empty(PlanWalker.Pushed(Read("bars", Bars)));
    }
}

using Chalk.Catalog;
using Chalk.Execution.Maintenance;
using Chalk.Ir;

namespace Chalk.Execution.Tests.Maintenance;

/// <summary>
/// A maintained plan under an access policy keeps every filter and projection its statement's plan
/// puts over a governed read (D361): the check passes a plan that keeps them all, and refuses one that
/// drops either, or holds an equal copy in its place, naming the node, where it is and the table.
/// </summary>
public sealed class PolicyNodesTests
{
    private static readonly Expr Condition = Greater(0, 5);

    private static readonly Expr[] Columns = [Field(0), Field(1)];

    [Fact]
    public void A_plan_that_keeps_every_node_over_a_governed_read_passes()
    {
        var plan = Statement(Project(Filter(Read("notes", governed: true), Condition), Columns));

        PolicyNodes.Check(plan, Governed, [Project(Filter(Placeholder(), Condition), Columns)]);
    }

    [Fact]
    public void A_plan_that_drops_a_filter_over_a_governed_read_is_refused()
    {
        var plan = Statement(Project(Filter(Read("notes", governed: true), Condition), Columns));

        var refused = Assert.Throws<NotMaintainedException>(
            () => PolicyNodes.Check(plan, Governed, [Project(Placeholder(), Columns)]));
        Assert.Equal(
            "a filter over the table 'notes', which carries an access policy, would not be kept as the statement's "
            + "plan has it.",
            refused.Message);
        Assert.Equal("root/Project/Filter", refused.Path);
    }

    [Fact]
    public void A_plan_that_drops_a_projection_over_a_governed_read_is_refused()
    {
        var plan = Statement(Project(Filter(Read("notes", governed: true), Condition), Columns));

        var refused = Assert.Throws<NotMaintainedException>(
            () => PolicyNodes.Check(plan, Governed, [Filter(Placeholder(), Condition)]));
        Assert.StartsWith("a projection over the table 'notes'", refused.Message, StringComparison.Ordinal);
        Assert.Equal("root/Project", refused.Path);
    }

    /// <summary>
    /// The rewrite carries the statement's own condition, so a copy, however equal, is not it: an
    /// equal filter elsewhere in the plan must not stand in for one that was dropped.
    /// </summary>
    [Fact]
    public void An_equal_condition_does_not_stand_in_for_the_statements_own()
    {
        var plan = Statement(Project(Filter(Read("notes", governed: true), Condition), Columns));
        var copy = Condition.Clone();
        Assert.Equal(Condition, copy);

        var refused = Assert.Throws<NotMaintainedException>(
            () => PolicyNodes.Check(plan, Governed, [Project(Filter(Placeholder(), copy), Columns)]));
        Assert.Equal("root/Project/Filter", refused.Path);
    }

    /// <summary>A node over a join is over both its sides: one governed side is enough to hold it.</summary>
    [Fact]
    public void A_filter_over_a_join_with_one_governed_side_is_held()
    {
        var join = new Rel
        {
            RowType = Row(4),
            HashJoin = new HashJoin
            {
                Left = Read("books", governed: false),
                Right = Read("notes", governed: true),
                Type = JoinType.Inner,
                LeftKeys = { 0 },
                RightKeys = { 0 },
            },
        };
        var plan = Statement(Filter(join, Condition));

        var refused = Assert.Throws<NotMaintainedException>(() => PolicyNodes.Check(plan, Governed, []));
        Assert.Equal("root/Filter", refused.Path);
        Assert.Contains("'notes'", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>A plan that reads nothing under a policy holds nothing, whatever the maintained plan keeps.</summary>
    [Fact]
    public void Nodes_over_reads_under_no_policy_are_not_held()
    {
        var plan = Statement(Project(Filter(Read("books", governed: false), Condition), Columns));

        PolicyNodes.Check(plan, Governed, []);
    }

    /// <summary>A subtree the plan shares is held once, where the plan lists it, and named by it.</summary>
    [Fact]
    public void A_node_in_a_shared_subtree_is_held_where_the_plan_lists_it()
    {
        var plan = new Plan
        {
            Root = Project(new Rel { RowType = Row(2), SharedRef = new SharedRef { Id = 1 } }, Columns),
            Shared = { new SharedPlan { Id = 1, Rel = Filter(Read("notes", governed: true), Condition) } },
        };

        var refused = Assert.Throws<NotMaintainedException>(
            () => PolicyNodes.Check(plan, Governed, [Project(Placeholder(), Columns)]));
        Assert.Equal("shared#1/Filter", refused.Path);

        PolicyNodes.Check(plan, Governed, [Project(Placeholder(), Columns), Filter(Placeholder(), Condition)]);
    }

    private static string? Governed(Rel rel) =>
        rel.KindCase == Rel.KindOneofCase.Read && rel.PolicyInjected ? rel.Read.Table.Table : null;

    private static Plan Statement(Rel root) => new() { Root = root };

    private static Rel Read(string table, bool governed)
    {
        var read = new Rel
        {
            RowType = Row(2),
            PolicyInjected = governed,
            Read = new Ir.Read { Table = new TableRef { SourceId = "desk", Schema = "desk", Table = table } },
        };
        read.Read.Projection.Add([0u, 1u]);
        return read;
    }

    private static Rel Filter(Rel input, Expr condition) => new()
    {
        RowType = input.RowType,
        Filter = new Ir.Filter { Input = input, Condition = condition },
    };

    private static Rel Project(Rel input, Expr[] exprs)
    {
        var project = new Ir.Project { Input = input };
        project.Exprs.AddRange(exprs);
        return new Rel { RowType = Row(exprs.Length), Project = project };
    }

    /// <summary>What the maintained plan puts in place of a step that keeps state.</summary>
    private static Rel Placeholder() => new() { RowType = Row(2), BoundTable = new BoundTable { Name = "$maintained#0" } };

    private static RowType Row(int width)
    {
        var row = new RowType();
        for (var i = 0; i < width; i++)
        {
            row.Fields.Add(new Ir.Field { Name = $"c{i}", Type = ChalkType.Int64().ToProto() });
        }

        return row;
    }

    private static Expr Field(int index) => new()
    {
        Type = ChalkType.Int64().ToProto(),
        FieldRef = new FieldRef { Index = (uint)index },
    };

    private static Expr Greater(int index, long value)
    {
        var greater = new Expr { Type = ChalkType.Bool(nullable: true).ToProto(), Call = new ScalarCall { Function = FunctionId.Gt } };
        greater.Call.Args.Add(Field(index));
        greater.Call.Args.Add(new Expr { Type = ChalkType.Int64().ToProto(), Literal = new Literal { I64Value = value } });
        return greater;
    }
}

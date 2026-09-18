using Chalk.Client;
using Chalk.Ir;
using Xunit;

namespace Chalk.Integration.Tests;

/// <summary>
/// The plan response is read under <see cref="GrpcPlannerOptions.PlanNestingLimit"/> rather than
/// protobuf's default of a hundred levels (F102), and a plan that nests deeper than the limit is a
/// plan-shaped refusal that names the limit, never an unavailability.
/// </summary>
public sealed class PlanNestingLimitTests
{
    /// <summary>A plan whose relations nest this many filters deep over one read.</summary>
    private static byte[] DeepPlan(int depth)
    {
        var condition = new Expr
        {
            Literal = new Literal { BoolValue = true },
            Type = new Chalk.Ir.Type { Kind = TypeKind.Bool },
        };
        var rel = new Rel { Read = new Read() };
        for (var i = 0; i < depth; i++)
        {
            rel = new Rel { Filter = new Filter { Condition = condition, Input = rel } };
        }

        var response = new Chalk.Client.Rpc.PlanResponse { Plan = new Plan { Root = rel } };
        return Google.Protobuf.MessageExtensions.ToByteArray(response);
    }

    [Fact]
    public void A_plan_deeper_than_the_limit_is_a_planning_exception_naming_the_limit()
    {
        var payload = DeepPlan(300);

        var refused = Assert.Throws<PlanningException>(() => GrpcQueryPlanner.ParsePlanResponse(payload, 100));

        Assert.Contains("100 levels", refused.Message, StringComparison.Ordinal);
        Assert.Contains("PlanNestingLimit", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_default_limit_reads_a_plan_protobufs_default_would_refuse()
    {
        var payload = DeepPlan(300);

        var parsed = GrpcQueryPlanner.ParsePlanResponse(payload, new GrpcPlannerOptions { Address = new Uri("http://localhost:1") }.PlanNestingLimit);

        Assert.Equal(Rel.KindOneofCase.Filter, parsed.Plan.Root.KindCase);
    }

    [Fact]
    public void A_limit_below_one_is_refused_at_construction()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GrpcQueryPlanner(
            new GrpcPlannerOptions { Address = new Uri("http://localhost:1"), PlanNestingLimit = 0 }));
    }
}

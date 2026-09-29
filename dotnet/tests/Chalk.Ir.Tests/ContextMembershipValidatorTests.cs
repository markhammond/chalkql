using Chalk.TestKit;
using static Chalk.TestKit.IrBuilder;

namespace Chalk.Ir.Tests;

/// <summary>
/// A membership over a context list the executor holds (F161): what makes one well formed, where it
/// may stand (I-IR-24), and the one nesting limit every read of a plan applies (F102, F161).
/// </summary>
public sealed class ContextMembershipValidatorTests
{
    private static readonly RowType Bars = Row(F("symbol", Str()), F("close", Fp64()));

    private static Expr Membership(string list, Chalk.Ir.Type type, params Expr[] columns)
    {
        var membership = new ContextMembership { List = list };
        membership.Columns.AddRange(columns);
        return new Expr { Type = type, ContextMembership = membership };
    }

    private static Expr Symbols => Membership("symbols", Bool(), Ref(Bars, 0));

    private static Plan Restamp(Plan plan)
    {
        plan.PlanDigest = 0;
        plan.PlanDigest = PlanDigest.Compute(plan);
        return plan;
    }

    [Fact]
    public void A_membership_in_the_executors_own_filter_validates_and_prints_its_list()
    {
        var plan = IrBuilder.Plan(Filter(Read("bars", Bars), Symbols));

        PlanValidator.Validate(plan);
        Assert.Contains("$0 IN @ctx.symbols", plan.ToPlanText(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_membership_names_its_list_and_compares_a_column_as_a_predicate()
    {
        foreach (var (expr, invariant) in new (Expr, string)[]
        {
            (Membership("", Bool(), Ref(Bars, 0)), "I-IR-24"),
            (Membership("symbols", Bool()), "I-IR-24"),
            (Membership("symbols", I32(), Ref(Bars, 0)), "I-IR-2"),
        })
        {
            var error = Assert.Throws<InvalidPlanException>(
                () => PlanValidator.Validate(Restamp(IrBuilder.Plan(Filter(Read("bars", Bars), expr)))));
            Assert.Equal(invariant, error.Invariant);
        }
    }

    [Fact]
    public void A_membership_a_source_would_evaluate_is_refused()
    {
        var read = Read("bars", Bars);
        read.Read.Filter = Symbols;
        var inRead = Assert.Throws<InvalidPlanException>(() => PlanValidator.Validate(Restamp(IrBuilder.Plan(read))));
        Assert.Equal("I-IR-24", inRead.Invariant);

        var pushed = RemoteQuery(
            "pg", "SELECT symbol, close FROM bars", Bars, pushedPlan: Filter(Read("bars", Bars), Symbols));
        var inPushed = Assert.Throws<InvalidPlanException>(() => PlanValidator.Validate(Restamp(IrBuilder.Plan(pushed))));
        Assert.Equal("I-IR-24", inPushed.Invariant);
    }

    // ---- the one nesting limit ----

    /// <summary>A chain of filters, two message levels deep each.</summary>
    private static Plan Deep(int filters)
    {
        var rel = Read("bars", Bars);
        for (var i = 0; i < filters; i++)
        {
            rel = Filter(rel, Call(FunctionId.Eq, Bool(), Ref(Bars, 0), Lit("BTCUSDT")));
        }

        return IrBuilder.Plan(rel);
    }

    [Fact]
    public void A_plan_protobufs_own_default_refused_reads_under_the_limit()
    {
        // About 120 levels: past the 100 protobuf's reader defaults to, which the validator's
        // re-read applied whatever limit the host set (F161), and inside the default.
        PlanValidator.Validate(Deep(60));
    }

    [Fact]
    public void A_plan_past_the_limit_is_refused_by_name_before_anything_recurses_over_it()
    {
        var error = Assert.Throws<PlanTooDeepException>(() => PlanValidator.Validate(Deep(200)));

        Assert.Equal(PlanLimits.DefaultNestingLimit, error.Limit);
        Assert.Contains($"limit of {PlanLimits.DefaultNestingLimit} levels", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_limit_is_the_callers()
    {
        PlanValidator.Validate(Deep(200), new PlanValidationOptions { NestingLimit = 1000 });

        var error = Assert.Throws<PlanTooDeepException>(
            () => PlanValidator.Validate(Deep(60), new PlanValidationOptions { NestingLimit = 64 }));
        Assert.Equal(64, error.Limit);
    }
}

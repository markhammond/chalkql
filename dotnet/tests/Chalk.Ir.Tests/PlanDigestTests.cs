using Chalk.TestKit;
using Google.Protobuf;
using static Chalk.TestKit.IrBuilder;

namespace Chalk.Ir.Tests;

public sealed class PlanDigestTests
{
    private static readonly RowType Bars = Row(
        F("symbol", Str()),
        F("ts", Timestamp()),
        F("close", Fp64()),
        F("volume", I64()));

    private static Plan Sample() => IrBuilder.Plan(
        Project(
            Filter(
                Read("bars", Bars, rows: 100_800),
                Call(FunctionId.Eq, Bool(), Ref(Bars, 0), Lit("BTCUSDT")),
                rows: 20_160),
            [("symbol", Ref(Bars, 0)), ("close", Ref(Bars, 2))],
            rows: 20_160));

    [Fact]
    public void The_digest_is_stable_across_repeated_computation()
    {
        var plan = Sample();
        var first = PlanDigest.Compute(plan);

        for (var i = 0; i < 100; i++)
        {
            Assert.Equal(first, PlanDigest.Compute(plan));
        }
    }

    [Fact]
    public void The_digest_survives_a_wire_round_trip()
    {
        var plan = Sample();

        var reparsed = Plan.Parser.ParseFrom(plan.ToByteArray());

        Assert.Equal(PlanDigest.Compute(plan), PlanDigest.Compute(reparsed));
    }

    [Fact]
    public void Estimates_context_and_epoch_are_excluded()
    {
        var plan = Sample();
        var baseline = PlanDigest.Compute(plan);

        var changed = plan.Clone();
        changed.ContextId = "somewhere-else";
        changed.CatalogEpoch = 99;
        foreach (var rel in PlanWalker.Rels(changed))
        {
            rel.EstRowCount *= 3.5;
        }

        Assert.Equal(baseline, PlanDigest.Compute(changed));
    }

    [Fact]
    public void The_digest_field_itself_is_excluded()
    {
        var plan = Sample();
        var baseline = PlanDigest.Compute(plan);

        var changed = plan.Clone();
        changed.PlanDigest = 0xDEADBEEFDEADBEEF;

        Assert.Equal(baseline, PlanDigest.Compute(changed));
    }

    [Fact]
    public void A_change_of_plan_shape_changes_the_digest()
    {
        var baseline = PlanDigest.Compute(Sample());

        var noFilter = IrBuilder.Plan(
            Project(
                Read("bars", Bars, rows: 100_800),
                [("symbol", Ref(Bars, 0)), ("close", Ref(Bars, 2))],
                rows: 100_800));

        Assert.NotEqual(baseline, PlanDigest.Compute(noFilter));
    }

    [Fact]
    public void A_change_of_literal_changes_the_digest()
    {
        var baseline = PlanDigest.Compute(Sample());

        var other = Sample();
        other.Root.Project.Input.Filter.Condition.Call.Args[1].Literal.StringValue = "ETHUSDT";

        Assert.NotEqual(baseline, PlanDigest.Compute(other));
    }

    [Fact]
    public void A_change_of_row_type_name_changes_the_digest()
    {
        var baseline = PlanDigest.Compute(Sample());

        var other = Sample();
        other.OutputType.Fields[0].Name = "sym";
        other.Root.RowType.Fields[0].Name = "sym";

        Assert.NotEqual(baseline, PlanDigest.Compute(other));
    }

    [Fact]
    public void Canonicalise_does_not_mutate_the_original()
    {
        var plan = Sample();
        var before = plan.Clone();

        PlanDigest.Canonicalise(plan);

        Assert.Equal(before, plan);
    }

    [Theory]
    [InlineData(0UL, "0000000000000000")]
    [InlineData(1UL, "0000000000000001")]
    [InlineData(0xE0415C39AA6B7E8CUL, "e0415c39aa6b7e8c")]
    public void Format_and_parse_round_trip(ulong digest, string text)
    {
        Assert.Equal(text, PlanDigest.Format(digest));
        Assert.Equal(digest, PlanDigest.Parse(text));
        Assert.Equal(digest, PlanDigest.Parse("  " + text + "\n"));
    }
}

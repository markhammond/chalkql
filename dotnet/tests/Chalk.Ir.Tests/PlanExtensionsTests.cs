using Chalk.TestKit;
using static Chalk.TestKit.IrBuilder;

namespace Chalk.Ir.Tests;

public sealed class PlanExtensionsTests
{
    private static readonly RowType Bars = Row(
        F("symbol", Str()),
        F("ts", Timestamp()),
        F("close", Fp64()),
        F("volume", I64()),
        F("vwap", Dec(28, 10, nullable: true)));

    [Fact]
    public void A_plan_prints_one_relation_per_line_indented_by_depth()
    {
        var plan = IrBuilder.Plan(
            Project(
                Filter(
                    Read("bars", Bars, rows: 100_800),
                    Call(FunctionId.Eq, Bool(), Ref(Bars, 0), Lit("BTCUSDT")),
                    rows: 20_160),
                [("symbol", Ref(Bars, 0)), ("close", Ref(Bars, 2))],
                rows: 20_160));

        var lines = plan.ToPlanText().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(4, lines.Length);
        Assert.StartsWith("Plan ir_version=1 digest=", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("  Project [$0, $2]", lines[1], StringComparison.Ordinal);
        Assert.StartsWith("    Filter EQ($0, 'BTCUSDT')", lines[2], StringComparison.Ordinal);
        Assert.StartsWith("      Read mem.main.bars projection=[0,1,2,3,4]", lines[3], StringComparison.Ordinal);
    }

    [Fact]
    public void Row_types_and_estimates_are_on_every_line()
    {
        var read = Read("bars", Bars, projection: [0, 2], rows: 100_800);
        read.RowType = Row(F("symbol", Str()), F("close", Fp64()));

        var text = read.ToPlanText();

        Assert.Contains("rows=100800", text, StringComparison.Ordinal);
        Assert.Contains("out=[symbol:STRING, close:FP64]", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Collations_print_direction_and_null_placement()
    {
        var read = Read(
            "bars",
            Bars,
            rows: 100_800,
            collations: Collation(Asc(1, Timestamp()), Asc(0, Str())));

        var text = read.ToPlanText();

        Assert.Contains("collations=[($1 ASC NULLS LAST, $0 ASC NULLS LAST)]", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("field ref", "$3")]
    [InlineData("literal", "'BTCUSDT'")]
    public void Expressions_print_compactly(string what, string expected)
    {
        Expr expr = what == "field ref" ? Ref(3, I64()) : Lit("BTCUSDT");

        Assert.Equal(expected, expr.ToPlanText());
    }

    [Fact]
    public void Case_prints_as_sql()
    {
        var expr = Case(
            Str(),
            Lit("flat"),
            (Call(FunctionId.Gt, Bool(), Ref(Bars, 2), Lit(0d)), Lit("up")));

        Assert.Equal("CASE WHEN GT($2, 0) THEN 'up' ELSE 'flat' END", expr.ToPlanText());
    }

    [Fact]
    public void In_lists_and_casts_print_readably()
    {
        Assert.Equal(
            "$0 IN ('BTCUSDT', 'ETHUSDT')",
            In(Ref(Bars, 0), Bool(), Lit("BTCUSDT"), Lit("ETHUSDT")).ToPlanText());

        Assert.Equal(
            "CAST($3 AS FP64)",
            Cast(Ref(Bars, 3), Fp64()).ToPlanText());

        Assert.Equal(
            "CAST($3 AS FP64 ON FAILURE NULL)",
            Cast(Ref(Bars, 3), Fp64(), CastFailure.Null).ToPlanText());
    }

    [Fact]
    public void Decimal_literals_print_with_their_scale()
    {
        Assert.Equal("1.2500000000", LitDecimal(1.25m).ToPlanText());
        Assert.Equal("-1.2500000000", LitDecimal(-1.25m).ToPlanText());
        Assert.Equal("125", LitDecimal(125m, 10, 0).ToPlanText());
    }

    [Fact]
    public void Null_literals_carry_their_type()
    {
        Assert.Equal("NULL:I64?", Null(I64()).ToPlanText());
    }

    [Fact]
    public void Parameters_and_enum_arguments_are_distinguishable()
    {
        Assert.Equal("?0", Param(0, Str(nullable: true)).ToPlanText());
        Assert.Equal(
            "EXTRACT(#HOUR, $1)",
            Call(FunctionId.Extract, I64(), EnumArg("HOUR"), Ref(Bars, 1)).ToPlanText());
    }

    [Fact]
    public void Aggregates_print_their_measures()
    {
        var rel = HashAggregate(
            Read("bars", Bars, rows: 100_800),
            keys: [0],
            measures:
            [
                ("n", Agg(AggregateFunctionId.Count, I64())),
                ("vol", Agg(AggregateFunctionId.Sum, I64(nullable: true), Ref(Bars, 3), distinct: true)),
            ],
            rows: 5);

        var text = rel.ToPlanText();

        Assert.Contains(
            "HashAggregate keys=[0] measures=[COUNT()->I64, SUM(DISTINCT $3)->I64?]",
            text,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Fetch_prints_an_absent_count_as_all()
    {
        Assert.Contains(
            "Fetch offset=100 count=all",
            Fetch(Read("bars", Bars), 100, null).ToPlanText(),
            StringComparison.Ordinal);
        Assert.Contains(
            "Fetch offset=100 count=50",
            Fetch(Read("bars", Bars), 100, 50).ToPlanText(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Parameter_types_appear_in_the_header()
    {
        var plan = IrBuilder.Plan(
            Filter(Read("bars", Bars), Call(FunctionId.Eq, Bool(), Ref(Bars, 0), Param(0, Str(nullable: true)))),
            parameterTypes: [Str(nullable: true), Timestamp(9, nullable: true)]);

        Assert.Contains("params=[STRING?, TIMESTAMP(9)?]", plan.ToPlanText(), StringComparison.Ordinal);
    }
}

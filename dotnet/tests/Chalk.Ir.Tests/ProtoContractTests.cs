using Google.Protobuf;

namespace Chalk.Ir.Tests;

/// <summary>
/// The C# half of "both builds compile proto/ and agree on it" (work plan step 1). Semantics are
/// PlanValidator's and PlanDigest's job; this only proves the generated code exists and round-trips.
/// </summary>
public sealed class ProtoContractTests
{
    [Fact]
    public void Plan_round_trips_through_the_wire_format()
    {
        var plan = SamplePlan();

        var parsed = Plan.Parser.ParseFrom(plan.ToByteArray());

        Assert.Equal(plan, parsed);
        Assert.Equal(Rel.KindOneofCase.Read, parsed.Root.KindCase);
        Assert.Equal("bars", parsed.Root.Read.Table.Table);
    }

    [Fact]
    public void Unset_oneof_is_distinguishable_from_a_set_one()
    {
        Assert.Equal(Rel.KindOneofCase.None, new Rel().KindCase);
        Assert.NotEqual(Rel.KindOneofCase.None, SamplePlan().Root.KindCase);
    }

    [Fact]
    public void Generated_messages_live_in_the_namespaces_the_design_assigns()
    {
        Assert.Equal("Chalk.Ir", typeof(Plan).Namespace);
        Assert.Equal("Chalk.Ir", typeof(CatalogContext).Namespace);
    }

    internal static Plan SamplePlan()
    {
        var rowType = new RowType
        {
            Fields = { new Field { Name = "symbol", Type = new Type { Kind = TypeKind.String } } },
        };
        var read = new Rel
        {
            RowType = rowType,
            EstRowCount = 100_800,
            Read = new Read
            {
                Table = new TableRef { SourceId = "mem", Schema = "main", Table = "bars" },
                Projection = { 0u },
            },
        };
        return new Plan
        {
            IrVersion = 1,
            ContextId = "demo",
            CatalogEpoch = 1,
            OutputType = rowType,
            Root = read,
        };
    }
}

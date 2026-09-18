using Apache.Arrow.Types;
using Chalk.Catalog;
using Chalk.Execution.Tests.Harness;
using Chalk.Ir;
using Chalk.Sources;
using Chalk.TestKit;
using ArrowSchema = Apache.Arrow.Schema;
using ClrType = System.Type;

namespace Chalk.Execution.Tests;

/// <summary>
/// What a prepared query <em>declares</em> for its STRING columns (D244): one layout per column,
/// fixed at compile time, and under <see cref="StringLayouts.Any"/> the one the resolution rule
/// predicts. The arrays that come out of the materialiser are
/// <see cref="OutputStringArrayTests"/>'s subject; this is only the schema.
/// </summary>
public sealed class OutputStringLayoutTests
{
    private static readonly TestTable Numbers = TestData.Numbers;

    /// <summary>A source whose rows arrive as classic Arrow strings — an ADO.NET reader's shape.</summary>
    private static TestSource Classic(params TestTable[] tables) =>
        new("mem", "main", tables.Length == 0 ? [Numbers] : tables)
        {
            NativeStringLayout = StringLayouts.Utf8,
        };

    /// <summary>A source whose rows arrive as string views — the in-box default.</summary>
    private static TestSource Views(params TestTable[] tables) =>
        new("mem", "main", tables.Length == 0 ? [Numbers] : tables)
        {
            NativeStringLayout = StringLayouts.Utf8View,
        };

    [Fact]
    public void Utf8View_declares_every_STRING_column_as_a_view()
    {
        var schema = CompileRead(Classic(), StringLayouts.Utf8View);

        Assert.IsType<StringViewType>(StringField(schema).DataType);
    }

    [Fact]
    public void Utf8_declares_every_STRING_column_as_classic()
    {
        var schema = CompileRead(Views(), StringLayouts.Utf8);

        Assert.IsType<StringType>(StringField(schema).DataType);
    }

    [Fact]
    public void Any_declares_classic_for_a_column_read_straight_from_a_classic_source()
    {
        var schema = CompileRead(Classic(), StringLayouts.Any);

        Assert.IsType<StringType>(StringField(schema).DataType);
    }

    [Fact]
    public void Any_declares_a_view_for_a_column_read_from_a_source_that_produces_views()
    {
        var schema = CompileRead(Views(), StringLayouts.Any);

        Assert.IsType<StringViewType>(StringField(schema).DataType);
    }

    /// <summary>A bare field reference is a reference; anything computed is built by an expression.</summary>
    [Fact]
    public void Any_follows_a_projection_through_a_field_reference_and_no_further()
    {
        var row = Numbers.RowType();
        var read = IrBuilder.Read(Numbers.Name, row);
        var project = IrBuilder.Project(
            read,
            [
                ("passed", IrBuilder.Ref(row, 5)),
                ("computed", IrBuilder.Call(FunctionId.Upper, IrBuilder.Str(true), IrBuilder.Ref(row, 5))),
            ]);

        var schema = Runner
            .Compile(IrBuilder.Plan(project), Classic(), outputStrings: StringLayouts.Any)
            .OutputSchema;

        Assert.IsType<StringType>(schema.FieldsList[0].DataType);
        Assert.IsType<StringViewType>(schema.FieldsList[1].DataType);
    }

    /// <summary>
    /// OFFSET/FETCH narrows a batch; it does not rebuild a column, so the reference is still the
    /// source's.
    /// </summary>
    [Fact]
    public void Any_follows_a_fetch_through_to_the_leaf()
    {
        var row = Numbers.RowType();
        var fetch = IrBuilder.Fetch(IrBuilder.Read(Numbers.Name, row), offset: 1, count: 3);

        var schema = Runner
            .Compile(IrBuilder.Plan(fetch), Classic(), outputStrings: StringLayouts.Any)
            .OutputSchema;

        Assert.IsType<StringType>(StringField(schema).DataType);
    }

    /// <summary>
    /// A node that rebuilds the column declares views, because that is what the copier and every
    /// expression's scratch produce. A filter is in the list: whether it compacts or forwards a
    /// selection is a per-batch decision, and a declaration has to hold for every batch.
    /// </summary>
    [Theory]
    [InlineData("filter")]
    [InlineData("sort")]
    public void Any_declares_a_view_for_a_column_a_node_re_materialises(string shape)
    {
        var row = Numbers.RowType();
        var read = IrBuilder.Read(Numbers.Name, row);
        var rel = shape switch
        {
            "filter" => IrBuilder.Filter(
                read,
                IrBuilder.Call(
                    FunctionId.Gt, IrBuilder.Bool(true), IrBuilder.Ref(row, 0), IrBuilder.Lit(0))),
            _ => IrBuilder.Sort(
                read,
                new SortField { Expr = IrBuilder.Ref(row, 5), Direction = SortDirection.AscNullsLast }),
        };

        var schema = Runner
            .Compile(IrBuilder.Plan(rel), Classic(), outputStrings: StringLayouts.Any)
            .OutputSchema;

        Assert.IsType<StringViewType>(StringField(schema).DataType);
    }

    /// <summary>The layout reaches a LIST's elements too, so the field and the array agree at every level.</summary>
    [Theory]
    [InlineData(StringLayouts.Utf8View, typeof(StringViewType))]
    [InlineData(StringLayouts.Utf8, typeof(StringType))]
    public void A_LIST_of_STRING_declares_its_element_in_the_same_layout(
        StringLayouts accepted, ClrType expected)
    {
        var table = new TestTable
        {
            Name = "tagged",
            Columns =
            [
                ("n", ChalkType.Int64()),
                ("tags", ChalkType.List(ChalkType.String(nullable: true), nullable: true)),
            ],
            Rows = [[1L, new object?[] { "a" }]],
        };

        var schema = Runner
            .Compile(
                IrBuilder.Plan(IrBuilder.Read(table.Name, table.RowType())),
                Views(table),
                outputStrings: accepted)
            .OutputSchema;

        var list = Assert.IsType<ListType>(schema.FieldsList[1].DataType);
        Assert.Equal(ArrowTypeMapping.ListElementName, list.ValueField.Name);
        Assert.IsType(expected, list.ValueDataType);
    }

    /// <summary>A schema is built from one resolved layout; <c>Any</c> is resolved before it gets there.</summary>
    [Theory]
    [InlineData(StringLayouts.Any)]
    [InlineData((StringLayouts)0)]
    public void An_unresolved_layout_cannot_build_a_type(StringLayouts layout)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ArrowTypeMapping.ToArrow(ChalkType.String(nullable: true), layout));
    }

    /// <summary>Both Arrow UTF-8 layouts map back to the one Chalk type.</summary>
    [Fact]
    public void A_view_type_maps_back_to_STRING()
    {
        Assert.Equal(
            ChalkType.String(nullable: true),
            ArrowTypeMapping.FromArrow(StringViewType.Default, nullable: true));

        // And the two are not the same Arrow type, which is what makes declaring one of them mean
        // something.
        Assert.False(ArrowTypeMapping.AreEquivalent(StringType.Default, StringViewType.Default));
    }

    private static ArrowSchema CompileRead(TestSource source, StringLayouts accepted) =>
        Runner
            .Compile(
                IrBuilder.Plan(IrBuilder.Read(Numbers.Name, Numbers.RowType())),
                source,
                outputStrings: accepted)
            .OutputSchema;

    private static Apache.Arrow.Field StringField(ArrowSchema schema) =>
        schema.FieldsList.Single(f => f.Name == "s");
}

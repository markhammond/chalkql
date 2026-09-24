using Chalk.Ir;

namespace Chalk.Catalog.Tests;

/// <summary>
/// The catalog half of D291 (ADR 0077): <see cref="ChalkType.Struct(IEnumerable{ChalkField}, bool)"/>
/// is a value with its fields in it, it round-trips through the IR's <c>Type</c>, and the catalog
/// refuses a struct everywhere but as a client-bodied function's result.
/// </summary>
public sealed class StructTypeTests
{
    private static readonly ChalkType Classification = ChalkType.Struct(
        new ChalkField("Category", ChalkType.String()),
        new ChalkField("Confidence", ChalkType.Float64()));

    private static CatalogContext Catalog(
        IReadOnlyList<ColumnDescriptor>? columns = null,
        params FunctionDescriptor[] functions) => new()
    {
        ContextId = "demo",
        Epoch = 1,
        Schemas =
        [
            new SchemaDescriptor
            {
                SourceId = "mem",
                Name = "main",
                Kind = SourceKind.Local,
                Tables =
                [
                    new TableDescriptor
                    {
                        Name = "transactions",
                        RowCount = 10,
                        Columns = columns ??
                        [
                            new ColumnDescriptor { Name = "description", Type = ChalkType.String() },
                            new ColumnDescriptor { Name = "amount", Type = ChalkType.Float64() },
                        ],
                    },
                ],
                Functions = functions,
            },
        ],
    };

    private static FunctionDescriptor Classify(FunctionBody body, ChalkType? returns = null) => new()
    {
        Name = "classify_transaction",
        Kind = FunctionKind.Scalar,
        Parameters =
        [
            new ParameterDescriptor { Name = "description", Type = ChalkType.String(nullable: true) },
            new ParameterDescriptor { Name = "amount", Type = ChalkType.Float64(nullable: true) },
        ],
        ReturnType = returns ?? Classification,
        Body = body,
    };

    private static CatalogValidationException AssertInvalid(CatalogContext catalog) =>
        Assert.Throws<CatalogValidationException>(() => CatalogValidator.Validate(catalog));

    // ---- the value ----

    [Fact]
    public void A_struct_holds_its_fields_in_declared_order()
    {
        Assert.Equal(TypeKind.Struct, Classification.Kind);
        Assert.Equal(["Category", "Confidence"], Classification.Fields.Select(f => f.Name));
        Assert.Equal(ChalkType.String(), Classification.Fields[0].Type);
        Assert.False(Classification.Nullable);
        Assert.Empty(ChalkType.String().Fields);
    }

    [Fact]
    public void Two_structs_of_the_same_fields_are_equal_and_hash_alike()
    {
        var again = ChalkType.Struct(
            [new ChalkField("Category", ChalkType.String()), new ChalkField("Confidence", ChalkType.Float64())]);

        Assert.Equal(Classification, again);
        Assert.Equal(Classification.GetHashCode(), again.GetHashCode());
        Assert.NotEqual(Classification, Classification.WithNullable(true));
        Assert.NotEqual(
            Classification,
            ChalkType.Struct(
                new ChalkField("Category", ChalkType.String(nullable: true)),
                new ChalkField("Confidence", ChalkType.Float64())));
    }

    [Fact]
    public void A_struct_round_trips_through_the_ir_type()
    {
        var nullable = ChalkType.Struct(
            [new ChalkField("Category", ChalkType.String()), new ChalkField("Score", ChalkType.Int32(nullable: true))],
            nullable: true);

        var proto = nullable.ToProto();

        Assert.Equal(TypeKind.Struct, proto.Kind);
        Assert.True(proto.Nullable);
        Assert.Equal(["Category", "Score"], proto.Fields.Select(f => f.Name));
        Assert.True(proto.Fields[1].Type.Nullable);
        Assert.Equal(nullable, ChalkType.FromProto(proto));
        Assert.Equal("STRUCT<Category:STRING, Score:I32?>?", nullable.ToString());
    }

    [Fact]
    public void A_struct_with_no_field_is_refused()
    {
        var ex = Assert.Throws<ArgumentException>(() => ChalkType.Struct());

        Assert.Contains("at least one field", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_field_names_equal_ignoring_case_are_refused()
    {
        var ex = Assert.Throws<ArgumentException>(() => ChalkType.Struct(
            new ChalkField("Category", ChalkType.String()), new ChalkField("CATEGORY", ChalkType.String())));

        Assert.Contains("'CATEGORY' is used twice ignoring case", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_composite_field_is_refused_as_one_level_deep()
    {
        var ex = Assert.Throws<ArgumentException>(() => ChalkType.Struct(
            new ChalkField("Inner", Classification)));

        Assert.Contains("one level deep", ex.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => ChalkType.Struct(
            new ChalkField("Tags", ChalkType.List(ChalkType.String()))));
    }

    // ---- the catalog ----

    [Fact]
    public void A_client_bodied_function_may_return_a_struct()
    {
        CatalogValidator.Validate(Catalog(functions: Classify(new ClientFunctionBody())));
    }

    [Fact]
    public void A_sql_bodied_function_returning_a_struct_is_refused()
    {
        var ex = AssertInvalid(Catalog(
            functions: Classify(new SqlFunctionBody { Text = "description" })));

        Assert.Contains("returns a STRUCT and is SQL-bodied", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_native_function_returning_a_struct_is_refused()
    {
        // Refused at the declaration itself, before anything asks whether the schema takes queries.
        var ex = AssertInvalid(Catalog(
            functions: Classify(new NativeFunctionBody { DialectName = "classify" })));

        Assert.Contains("returns a STRUCT and is native", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_struct_parameter_is_refused()
    {
        var function = new FunctionDescriptor
        {
            Name = "explain",
            Kind = FunctionKind.Scalar,
            Parameters = [new ParameterDescriptor { Name = "c", Type = Classification }],
            ReturnType = ChalkType.String(),
            Body = new ClientFunctionBody(),
        };

        var ex = AssertInvalid(Catalog(functions: function));

        Assert.Contains("a parameter is a scalar", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_struct_table_column_is_refused()
    {
        var ex = AssertInvalid(Catalog(
            [new ColumnDescriptor { Name = "c", Type = Classification }]));

        Assert.Contains("a STRUCT cannot be a table column", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_struct_table_function_column_is_refused()
    {
        var function = new FunctionDescriptor
        {
            Name = "classified",
            Kind = FunctionKind.Table,
            ReturnsTable = [new ColumnDescriptor { Name = "c", Type = Classification }],
            Body = new ClientFunctionBody(),
        };

        var ex = AssertInvalid(Catalog(functions: function));

        Assert.Contains("a table function's column", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_nested_struct_declared_as_a_result_is_refused_as_one_level_deep()
    {
        // Built past the factory, which refuses it too: what a catalog read off the wire could say.
        var nested = new ChalkType(TypeKind.Struct, Nullable: false)
        {
            Fields = [new ChalkField("Inner", Classification)],
        };

        var ex = AssertInvalid(Catalog(functions: Classify(new ClientFunctionBody(), nested)));

        Assert.Contains("one level deep", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_scalar_type_carrying_fields_is_refused()
    {
        var odd = ChalkType.String() with { Fields = [new ChalkField("a", ChalkType.Int32())] };

        var ex = AssertInvalid(Catalog(functions: Classify(new ClientFunctionBody(), odd)));

        Assert.Contains("only a STRUCT has fields", ex.Message, StringComparison.Ordinal);
    }
}

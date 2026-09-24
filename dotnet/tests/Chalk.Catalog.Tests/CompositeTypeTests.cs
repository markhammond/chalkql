using Chalk.Entitlements;
using Chalk.Ir;
using Disclosure = Chalk.Entitlements.Disclosure;
using DisclosureRule = Chalk.Entitlements.DisclosureRule;

namespace Chalk.Catalog.Tests;

/// <summary>
/// The catalog half of D291 (ADR 0077): <see cref="ChalkType.Composite(IEnumerable{CompositeField}, bool)"/>
/// is a value with its fields in it, it round-trips through the IR's <c>Type</c>, and the catalog
/// refuses a composite value everywhere but as a client-bodied function's result — and, since D302, as
/// a column of an in-process source's table, which is keyed, indexed and summarised on nothing and is
/// disclosed whole or withheld whole.
/// </summary>
public sealed class CompositeTypeTests
{
    private static readonly ChalkType Classification = ChalkType.Composite(
        new CompositeField("Category", ChalkType.String()),
        new CompositeField("Confidence", ChalkType.Float64()));

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
    public void A_composite_holds_its_fields_in_declared_order()
    {
        Assert.Equal(TypeKind.Composite, Classification.Kind);
        Assert.Equal(["Category", "Confidence"], Classification.Fields.Select(f => f.Name));
        Assert.Equal(ChalkType.String(), Classification.Fields[0].Type);
        Assert.False(Classification.Nullable);
        Assert.Empty(ChalkType.String().Fields);
    }

    [Fact]
    public void Two_composites_of_the_same_fields_are_equal_and_hash_alike()
    {
        var again = ChalkType.Composite(
            [new CompositeField("Category", ChalkType.String()), new CompositeField("Confidence", ChalkType.Float64())]);

        Assert.Equal(Classification, again);
        Assert.Equal(Classification.GetHashCode(), again.GetHashCode());
        Assert.NotEqual(Classification, Classification.WithNullable(true));
        Assert.NotEqual(
            Classification,
            ChalkType.Composite(
                new CompositeField("Category", ChalkType.String(nullable: true)),
                new CompositeField("Confidence", ChalkType.Float64())));
    }

    [Fact]
    public void A_composite_round_trips_through_the_ir_type()
    {
        var nullable = ChalkType.Composite(
            [new CompositeField("Category", ChalkType.String()), new CompositeField("Score", ChalkType.Int32(nullable: true))],
            nullable: true);

        var proto = nullable.ToProto();

        Assert.Equal(TypeKind.Composite, proto.Kind);
        Assert.True(proto.Nullable);
        Assert.Equal(["Category", "Score"], proto.Fields.Select(f => f.Name));
        Assert.True(proto.Fields[1].Type.Nullable);
        Assert.Equal(nullable, ChalkType.FromProto(proto));
        Assert.Equal("COMPOSITE(Category STRING, Score I32?)?", nullable.ToString());
    }

    [Fact]
    public void A_composite_with_no_field_is_refused()
    {
        var ex = Assert.Throws<ArgumentException>(() => ChalkType.Composite());

        Assert.Contains("at least one field", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_field_names_equal_ignoring_case_are_refused()
    {
        var ex = Assert.Throws<ArgumentException>(() => ChalkType.Composite(
            new CompositeField("Category", ChalkType.String()), new CompositeField("CATEGORY", ChalkType.String())));

        Assert.Contains("'CATEGORY' is used twice ignoring case", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_composite_field_is_refused_as_one_level_deep()
    {
        var ex = Assert.Throws<ArgumentException>(() => ChalkType.Composite(
            new CompositeField("Inner", Classification)));

        Assert.Contains("one level deep", ex.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => ChalkType.Composite(
            new CompositeField("Tags", ChalkType.List(ChalkType.String()))));
    }

    // ---- the catalog ----

    [Fact]
    public void A_client_bodied_function_may_return_a_composite()
    {
        CatalogValidator.Validate(Catalog(functions: Classify(new ClientFunctionBody())));
    }

    [Fact]
    public void A_sql_bodied_function_returning_a_composite_is_refused()
    {
        var ex = AssertInvalid(Catalog(
            functions: Classify(new SqlFunctionBody { Text = "description" })));

        Assert.Contains("returns a COMPOSITE and is SQL-bodied", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_native_function_returning_a_composite_is_refused()
    {
        // Refused at the declaration itself, before anything asks whether the schema takes queries.
        var ex = AssertInvalid(Catalog(
            functions: Classify(new NativeFunctionBody { DialectName = "classify" })));

        Assert.Contains("returns a COMPOSITE and is native", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_composite_parameter_is_refused()
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

    // ---- a composite column (D302) ----

    private static readonly ChalkType Card = ChalkType.Composite(
        [new CompositeField("Email", ChalkType.String()), new CompositeField("Tier", ChalkType.Int32())],
        nullable: true);

    /// <summary>
    /// A table of an id and a composite <c>contact</c> card, on a source of the kind given, with the
    /// keys, statistics and policy a test declares over it.
    /// </summary>
    private static CatalogContext Profiles(
        SourceKind kind = SourceKind.Local,
        IReadOnlyList<UniqueKeyDescriptor>? uniqueKeys = null,
        IReadOnlyList<CollationDescriptor>? collations = null,
        IReadOnlyList<IndexDescriptor>? indexes = null,
        ColumnStatistics? statistics = null,
        ColumnEntitlementDescriptor? contact = null) => new()
    {
        ContextId = "demo",
        Epoch = 1,
        Schemas =
        [
            new SchemaDescriptor
            {
                SourceId = "crm",
                Name = "people",
                Kind = kind,
                Dialect = kind == SourceKind.Remote ? "sqlite" : null,
                Tables =
                [
                    new TableDescriptor
                    {
                        Name = "profiles",
                        RowCount = 6,
                        Columns =
                        [
                            new ColumnDescriptor { Name = "id", Type = ChalkType.Int32() },
                            new ColumnDescriptor
                            {
                                Name = "contact",
                                Type = Card,
                                Statistics = statistics ?? ColumnStatistics.Unknown,
                            },
                        ],
                        UniqueKeys = uniqueKeys ?? [],
                        Collations = collations ?? [],
                        Indexes = indexes ?? [],
                        Entitlement = contact is null
                            ? null
                            : new TableEntitlementDescriptor { Columns = [contact] },
                    },
                ],
            },
        ],
    };

    [Fact]
    public void A_composite_column_is_declared_on_an_in_process_source()
    {
        CatalogValidator.Validate(Profiles());
        CatalogValidator.Validate(Profiles(
            uniqueKeys: [new UniqueKeyDescriptor { Columns = [0] }],
            collations: [new CollationDescriptor { Keys = [new KeyOrder(0, SortDirection.AscNullsLast)] }],
            indexes:
            [
                new IndexDescriptor
                {
                    Name = "cx_id", Kind = IndexKind.Clustered, Columns = [0], Covering = [0],
                },
            ]));
    }

    [Fact]
    public void A_composite_column_on_a_remote_source_is_refused_naming_the_table_and_the_column()
    {
        var ex = AssertInvalid(Profiles(SourceKind.Remote));

        Assert.Equal(
            "Invalid catalog at schemas[0] (people).tables[0] (profiles).columns[1] (contact): column "
            + "'contact' of table "
            + "'profiles' is a COMPOSITE, and schema 'people' is a REMOTE source; a composite column is "
            + "read only from an in-process (LOCAL) source. Declare its fields as columns of their own",
            ex.Message);
    }

    [Fact]
    public void No_key_collation_or_index_is_over_a_composite_column()
    {
        const string Why = "is a COMPOSITE and cannot be part of";

        Assert.Contains(
            $"column 'contact' {Why} a unique key",
            AssertInvalid(Profiles(uniqueKeys: [new UniqueKeyDescriptor { Columns = [0, 1] }])).Message,
            StringComparison.Ordinal);
        Assert.Contains(
            $"column 'contact' {Why} a collation",
            AssertInvalid(Profiles(collations:
                [new CollationDescriptor { Keys = [new KeyOrder(1, SortDirection.AscNullsLast)] }])).Message,
            StringComparison.Ordinal);
        Assert.Contains(
            $"column 'contact' {Why} an index key",
            AssertInvalid(Profiles(indexes:
                [new IndexDescriptor { Name = "ix_contact", Kind = IndexKind.Ordered, Columns = [1] }])).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_clustered_copy_never_carries_a_composite_column()
    {
        var everything = AssertInvalid(Profiles(indexes:
            [new IndexDescriptor { Name = "cx_id", Kind = IndexKind.Clustered, Columns = [0] }]));
        Assert.Contains(
            "index 'cx_id' is CLUSTERED with no covering set, which covers every column and so the "
            + "composite column 'contact'",
            everything.Message,
            StringComparison.Ordinal);

        var named = AssertInvalid(Profiles(indexes:
            [new IndexDescriptor { Name = "cx_id", Kind = IndexKind.Clustered, Columns = [0], Covering = [0, 1] }]));
        Assert.Contains(
            "the covering set of index 'cx_id' names the composite column 'contact'",
            named.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_composite_column_carries_no_statistics()
    {
        var ex = AssertInvalid(Profiles(statistics: new ColumnStatistics { DistinctCount = 6 }));

        Assert.Contains(
            "column 'contact' is a COMPOSITE and declares statistics", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_composite_column_is_disclosed_whole_or_withheld_whole()
    {
        // FULL under a condition over one of its own fields, NONE otherwise: registered.
        CatalogValidator.Validate(Profiles(contact: new ColumnEntitlementDescriptor
        {
            Column = 1,
            Rules = [new DisclosureRule { When = "(contact).tier >= 2", Then = Disclosure.Full }],
            Otherwise = Disclosure.None,
        }));

        var masked = AssertInvalid(Profiles(contact: new ColumnEntitlementDescriptor
        {
            Column = 1,
            Rules = [new DisclosureRule { When = "TRUE", Then = Disclosure.Masked, Mask = "contact" }],
        }));
        Assert.Equal(
            "Invalid catalog at schemas[0] (people).tables[0] (profiles).entitlement.columns[0] "
            + "(contact).rules[0]: column 'contact' "
            + "is a COMPOSITE and is disclosed Masked, but no expression builds a composite value to "
            + "mask it with. A composite column is disclosed Full or None, None's placeholder being the "
            + "NULL composite; a rule's condition may read a field of it",
            masked.Message);

        Assert.Contains(
            "is disclosed AggregateOnly, but no built-in aggregate takes a composite value",
            AssertInvalid(Profiles(contact: new ColumnEntitlementDescriptor
            {
                Column = 1,
                Otherwise = Disclosure.AggregateOnly,
                AggregateOnlyFunctions = ["COUNT"],
            })).Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "is disclosed Test, but a composite value has no equality to test",
            AssertInvalid(Profiles(contact: new ColumnEntitlementDescriptor
            {
                Column = 1,
                Rules = [new DisclosureRule { When = "TRUE", Then = Disclosure.Test }],
            })).Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "is a COMPOSITE and the rule declares a placeholder",
            AssertInvalid(Profiles(contact: new ColumnEntitlementDescriptor
            {
                Column = 1,
                Rules = [new DisclosureRule { When = "TRUE", Then = Disclosure.None, Placeholder = "NULL" }],
            })).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_composite_table_function_column_is_refused()
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
    public void A_nested_composite_declared_as_a_result_is_refused_as_one_level_deep()
    {
        // Built past the factory, which refuses it too: what a catalog read off the wire could say.
        var nested = new ChalkType(TypeKind.Composite, Nullable: false)
        {
            Fields = [new CompositeField("Inner", Classification)],
        };

        var ex = AssertInvalid(Catalog(functions: Classify(new ClientFunctionBody(), nested)));

        Assert.Contains("one level deep", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_scalar_type_carrying_fields_is_refused()
    {
        var odd = ChalkType.String() with { Fields = [new CompositeField("a", ChalkType.Int32())] };

        var ex = AssertInvalid(Catalog(functions: Classify(new ClientFunctionBody(), odd)));

        Assert.Contains("only a COMPOSITE has fields", ex.Message, StringComparison.Ordinal);
    }
}

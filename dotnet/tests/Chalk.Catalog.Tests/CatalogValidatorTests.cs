using Chalk.Ir;

namespace Chalk.Catalog.Tests;

public sealed class CatalogValidatorTests
{
    private static ColumnDescriptor Col(string name, ChalkType type) => new() { Name = name, Type = type };

    private static TableDescriptor Bars(
        IReadOnlyList<UniqueKeyDescriptor>? keys = null,
        IReadOnlyList<CollationDescriptor>? collations = null,
        IReadOnlyList<IndexDescriptor>? indexes = null,
        long rowCount = 100_800) => new()
    {
        Name = "bars",
        RowCount = rowCount,
        Columns =
        [
            Col("symbol", ChalkType.String()),
            Col("ts", ChalkType.Timestamp(9)),
            Col("close", ChalkType.Float64()),
        ],
        UniqueKeys = keys ?? [],
        Collations = collations ?? [],
        Indexes = indexes ?? [],
    };

    private static CatalogContext Catalog(params TableDescriptor[] tables) => new()
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
                Tables = tables.Length == 0 ? [Bars()] : tables,
            },
        ],
    };

    private static CatalogValidationException AssertInvalid(CatalogContext catalog) =>
        Assert.Throws<CatalogValidationException>(() => CatalogValidator.Validate(catalog));

    [Fact]
    public void A_well_formed_catalog_validates()
    {
        var catalog = Catalog(Bars(
            keys: [new UniqueKeyDescriptor { Columns = [1, 0] }],
            collations:
            [
                new CollationDescriptor
                {
                    Keys = [new KeyOrder(1, SortDirection.AscNullsLast), new KeyOrder(0, SortDirection.AscNullsLast)],
                },
            ],
            indexes:
            [
                new IndexDescriptor { Name = "ix_symbol", Kind = IndexKind.Hash, Columns = [0] },
            ]));

        CatalogValidator.Validate(catalog);
    }

    [Fact]
    public void An_empty_context_id_is_rejected()
    {
        var catalog = new CatalogContext { ContextId = " ", Epoch = 1, Schemas = Catalog().Schemas };

        Assert.Equal("context_id", AssertInvalid(catalog).Path);
    }

    [Fact]
    public void A_catalog_with_no_schemas_is_rejected()
    {
        var catalog = new CatalogContext { ContextId = "demo", Epoch = 1, Schemas = [] };

        Assert.Equal("schemas", AssertInvalid(catalog).Path);
    }

    [Fact]
    public void Duplicate_schema_names_are_rejected_case_insensitively()
    {
        var schema = Catalog().Schemas[0];
        var catalog = new CatalogContext
        {
            ContextId = "demo",
            Epoch = 1,
            Schemas =
            [
                schema,
                new SchemaDescriptor
                {
                    SourceId = "other",
                    Name = "MAIN",
                    Kind = SourceKind.Local,
                    Tables = [Bars()],
                },
            ],
        };

        Assert.Contains("used twice", AssertInvalid(catalog).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Duplicate_table_names_are_rejected_case_insensitively()
    {
        var other = new TableDescriptor
        {
            Name = "BARS",
            RowCount = 1,
            Columns = [Col("x", ChalkType.Int64())],
        };

        Assert.Contains("used twice", AssertInvalid(Catalog(Bars(), other)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Duplicate_column_names_are_rejected_case_insensitively()
    {
        var table = new TableDescriptor
        {
            Name = "t",
            RowCount = 1,
            Columns = [Col("a", ChalkType.Int64()), Col("A", ChalkType.String())],
        };

        Assert.Contains("used twice", AssertInvalid(Catalog(table)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_table_with_no_columns_is_rejected()
    {
        var table = new TableDescriptor { Name = "t", RowCount = 0, Columns = [] };

        Assert.Contains("at least one column", AssertInvalid(Catalog(table)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Row_count_of_minus_one_means_unknown_and_is_allowed()
    {
        CatalogValidator.Validate(Catalog(Bars(rowCount: -1)));

        Assert.Contains("it must be >= -1", AssertInvalid(Catalog(Bars(rowCount: -2))).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_unique_key_column_out_of_range_is_rejected()
    {
        var catalog = Catalog(Bars(keys: [new UniqueKeyDescriptor { Columns = [7] }]));

        Assert.Contains("out of range", AssertInvalid(catalog).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_unique_key_that_repeats_a_column_is_rejected()
    {
        var catalog = Catalog(Bars(keys: [new UniqueKeyDescriptor { Columns = [0, 0] }]));

        Assert.Contains("appears twice", AssertInvalid(catalog).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_collation_key_out_of_range_is_rejected()
    {
        var catalog = Catalog(Bars(collations:
            [new CollationDescriptor { Keys = [new KeyOrder(9, SortDirection.AscNullsLast)] }]));

        Assert.Contains("out of range", AssertInvalid(catalog).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unspecified_sort_direction_is_rejected_because_calcite_compares_it()
    {
        var catalog = Catalog(Bars(collations:
            [new CollationDescriptor { Keys = [new KeyOrder(1, SortDirection.Unspecified)] }]));

        var ex = AssertInvalid(catalog);

        Assert.Contains("never satisfies an ORDER BY", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_index_column_out_of_range_is_rejected()
    {
        var catalog = Catalog(Bars(indexes:
            [new IndexDescriptor { Name = "ix", Kind = IndexKind.Ordered, Columns = [5] }]));

        Assert.Contains("out of range", AssertInvalid(catalog).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Duplicate_index_names_on_one_table_are_rejected()
    {
        var catalog = Catalog(Bars(indexes:
        [
            new IndexDescriptor { Name = "ix", Kind = IndexKind.Ordered, Columns = [0] },
            new IndexDescriptor { Name = "IX", Kind = IndexKind.Hash, Columns = [1] },
        ]));

        Assert.Contains("used twice", AssertInvalid(catalog).Message, StringComparison.Ordinal);
    }

    /// <summary>D257: the covering set of a clustered index, and the four ways it can be wrong.</summary>
    [Fact]
    public void A_clustered_index_with_a_covering_set_over_its_key_validates()
    {
        var catalog = Catalog(Bars(indexes:
        [
            new IndexDescriptor
            {
                Name = "cx",
                Kind = IndexKind.Clustered,
                Columns = [0, 1],
                Covering = [0, 1, 2],
            },
        ]));

        CatalogValidator.Validate(catalog);
    }

    [Fact]
    public void A_clustered_index_that_covers_every_column_says_so_with_an_empty_set()
    {
        var catalog = Catalog(Bars(indexes:
            [new IndexDescriptor { Name = "cx", Kind = IndexKind.Clustered, Columns = [0, 1] }]));

        CatalogValidator.Validate(catalog);

        var index = catalog.Schemas[0].Tables[0].Indexes[0];
        Assert.Empty(index.Covering);
        Assert.True(index.Covers([2]));
    }

    [Fact]
    public void A_covering_set_that_leaves_out_a_key_column_is_rejected()
    {
        var catalog = Catalog(Bars(indexes:
        [
            new IndexDescriptor
            {
                Name = "cx",
                Kind = IndexKind.Clustered,
                Columns = [0, 1],
                Covering = [0, 2],
            },
        ]));

        Assert.Contains("leaves out key column", AssertInvalid(catalog).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_covering_column_that_is_not_a_column_is_rejected()
    {
        var catalog = Catalog(Bars(indexes:
        [
            new IndexDescriptor
            {
                Name = "cx",
                Kind = IndexKind.Clustered,
                Columns = [0],
                Covering = [0, 9],
            },
        ]));

        Assert.Contains("out of range", AssertInvalid(catalog).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_covering_set_that_is_not_ascending_is_rejected()
    {
        var catalog = Catalog(Bars(indexes:
        [
            new IndexDescriptor
            {
                Name = "cx",
                Kind = IndexKind.Clustered,
                Columns = [0],
                Covering = [2, 0],
            },
        ]));

        Assert.Contains("not ascending", AssertInvalid(catalog).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_covering_set_on_an_index_that_is_not_clustered_is_rejected()
    {
        var catalog = Catalog(Bars(indexes:
        [
            new IndexDescriptor
            {
                Name = "ix",
                Kind = IndexKind.Ordered,
                Columns = [0],
                Covering = [0, 2],
            },
        ]));

        Assert.Contains(
            "only a CLUSTERED index holds a copy", AssertInvalid(catalog).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Covers_is_answered_by_the_covering_set()
    {
        var index = new IndexDescriptor
        {
            Name = "cx",
            Kind = IndexKind.Clustered,
            Columns = [0, 1],
            Covering = [0, 1],
        };

        Assert.True(index.Covers([1, 0]));
        Assert.True(index.Covers([]));
        Assert.False(index.Covers([0, 2]));
    }

    [Fact]
    public void A_remote_schema_must_name_its_dialect()
    {
        var catalog = new CatalogContext
        {
            ContextId = "demo",
            Epoch = 1,
            Schemas =
            [
                new SchemaDescriptor
                {
                    SourceId = "sql",
                    Name = "warehouse",
                    Kind = SourceKind.Remote,
                    Tables = [Bars()],
                },
            ],
        };

        Assert.Contains("SQL dialect", AssertInvalid(catalog).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(TypeKind.Decimal, 0, 0)]
    [InlineData(TypeKind.Decimal, 39, 0)]
    [InlineData(TypeKind.Decimal, 10, 20)]
    [InlineData(TypeKind.Timestamp, 10, 0)]
    [InlineData(TypeKind.Time, 7, 0)]
    [InlineData(TypeKind.I64, 5, 0)]
    [InlineData(TypeKind.String, 0, 3)]
    public void Malformed_column_types_are_rejected(TypeKind kind, int precision, int scale)
    {
        var table = new TableDescriptor
        {
            Name = "t",
            RowCount = 1,
            Columns = [Col("c", new ChalkType(kind, false, precision, scale))],
        };

        Assert.Throws<CatalogValidationException>(() => CatalogValidator.Validate(Catalog(table)));
    }

    // ---- F14: foreign keys ----

    private static TableDescriptor Symbols() => new()
    {
        Name = "symbols",
        RowCount = 5,
        Columns = [Col("symbol", ChalkType.String()), Col("venue", ChalkType.String())],
        UniqueKeys = [new UniqueKeyDescriptor { Columns = [0] }],
    };

    private static TableDescriptor BarsWith(ForeignKeyDescriptor key)
    {
        var bars = Bars();
        return new TableDescriptor
        {
            Name = bars.Name,
            RowCount = bars.RowCount,
            Columns = bars.Columns,
            ForeignKeys = [key],
        };
    }

    [Fact]
    public void A_well_formed_foreign_key_validates()
    {
        var catalog = Catalog(
            BarsWith(new ForeignKeyDescriptor
            {
                Name = "fk_bars_symbols",
                Columns = [0],
                ParentTable = "SYMBOLS",
                ParentColumns = [0],
            }),
            Symbols());

        CatalogValidator.Validate(catalog);
    }

    [Fact]
    public void A_foreign_key_with_no_columns_is_rejected()
    {
        var error = AssertInvalid(Catalog(
            BarsWith(new ForeignKeyDescriptor { Columns = [], ParentTable = "symbols", ParentColumns = [] }),
            Symbols()));

        Assert.Contains("no columns", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_foreign_key_whose_parent_is_not_registered_is_rejected()
    {
        var error = AssertInvalid(Catalog(
            BarsWith(new ForeignKeyDescriptor
            {
                Columns = [0],
                ParentTable = "tickers",
                ParentColumns = [0],
            }),
            Symbols()));

        Assert.Contains("tickers", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_foreign_key_with_mismatched_arity_is_rejected()
    {
        var error = AssertInvalid(Catalog(
            BarsWith(new ForeignKeyDescriptor
            {
                Columns = [0, 1],
                ParentTable = "symbols",
                ParentColumns = [0],
            }),
            Symbols()));

        Assert.Contains("one for one", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_foreign_key_column_out_of_range_is_rejected()
    {
        var error = AssertInvalid(Catalog(
            BarsWith(new ForeignKeyDescriptor
            {
                Columns = [7],
                ParentTable = "symbols",
                ParentColumns = [0],
            }),
            Symbols()));

        Assert.Contains("out of range", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_parent_column_out_of_range_is_rejected()
    {
        var error = AssertInvalid(Catalog(
            BarsWith(new ForeignKeyDescriptor
            {
                Columns = [0],
                ParentTable = "symbols",
                ParentColumns = [9],
            }),
            Symbols()));

        Assert.Contains("out of range", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_foreign_key_between_different_kinds_is_rejected()
    {
        var error = AssertInvalid(Catalog(
            BarsWith(new ForeignKeyDescriptor
            {
                Columns = [1],
                ParentTable = "symbols",
                ParentColumns = [0],
            }),
            Symbols()));

        Assert.Contains("same kind", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Lookup_is_case_insensitive_and_defaults_to_the_first_schema()
    {
        var catalog = Catalog();

        Assert.NotNull(catalog.FindSchema("MAIN"));
        Assert.NotNull(catalog.FindTable(null, "BARS"));
        Assert.NotNull(catalog.FindTable("main", "bars"));
        Assert.Null(catalog.FindTable("nope", "bars"));
        Assert.Equal(1, catalog.Schemas[0].Tables[0].IndexOfColumn("TS"));
        Assert.Equal(-1, catalog.Schemas[0].Tables[0].IndexOfColumn("nope"));
    }
}

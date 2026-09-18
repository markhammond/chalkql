using Chalk.Catalog;
using KeyOrder = Chalk.Catalog.KeyOrder;
using SortDirection = Chalk.Ir.SortDirection;
using SourceKind = Chalk.Ir.SourceKind;

namespace Chalk.Sources.Poco.Tests;

/// <summary>The CLR → <see cref="ChalkType"/> table of <c>docs/design/04-client.md</c> §5.2 (D16).</summary>
public sealed class PocoInferenceTests
{
    /// <summary>One case per row of the table, at the default decimal scale.</summary>
    public static TheoryData<string, ChalkType> TypeTable() => new()
    {
        { "Flag", ChalkType.Bool() },
        { "Tiny", ChalkType.Int8() },
        { "Small", ChalkType.Int16() },
        { "Medium", ChalkType.Int32() },
        { "Big", ChalkType.Int64() },
        { "UnsignedTiny", ChalkType.Int16() },
        { "UnsignedSmall", ChalkType.Int32() },
        { "UnsignedMedium", ChalkType.Int64() },
        { "UnsignedBig", ChalkType.Decimal(20, 0) },
        { "Single", ChalkType.Float32() },
        { "Real", ChalkType.Float64() },
        { "Money", ChalkType.Decimal(28, 10) },
        { "Text", ChalkType.String() },
        { "Blob", ChalkType.Binary() },
        { "Memory", ChalkType.Binary() },
        { "Moment", ChalkType.Timestamp(9) },
        { "Instant", ChalkType.TimestampTz(9) },
        { "Day", ChalkType.Date() },
        { "Clock", ChalkType.Time(6) },
        { "Span", ChalkType.IntervalDay() },
        { "Key", ChalkType.Uuid() },
        { "Shade", ChalkType.String() },
        { "Letter", ChalkType.String() },
    };

    [Theory]
    [MemberData(nameof(TypeTable))]
    public void Every_clr_row_maps_to_the_type_the_design_names(string column, ChalkType expected)
    {
        var table = Table(new PocoSourceBuilder("mem").AddTable("t", new List<AllTypesRow>()).Build());

        var actual = table.Columns[table.IndexOfColumn(column)].Type;

        Assert.Equal(expected, actual);
    }

    [Theory]
    [MemberData(nameof(TypeTable))]
    public void Nullable_members_map_to_the_same_kind_marked_nullable(string column, ChalkType expected)
    {
        var table = Table(new PocoSourceBuilder("mem").AddTable("t", new List<NullableTypesRow>()).Build());

        var actual = table.Columns[table.IndexOfColumn(column)].Type;

        Assert.Equal(expected.WithNullable(true), actual);
    }

    [Fact]
    public void Non_nullable_members_are_not_nullable()
    {
        var table = Table(new PocoSourceBuilder("mem").AddTable("t", new List<AllTypesRow>()).Build());

        Assert.All(table.Columns, c => Assert.False(c.Type.Nullable));
    }

    [Fact]
    public void A_reference_member_is_nullable_only_when_it_is_annotated_so()
    {
        var table = Table(new PocoSourceBuilder("mem").AddTable("t", new List<BarRow>()).Build());

        Assert.False(table.Columns[table.IndexOfColumn("Symbol")].Type.Nullable);
        Assert.True(table.Columns[table.IndexOfColumn("Volume")].Type.Nullable);
    }

    [Fact]
    public void An_unannotated_reference_member_is_nullable()
    {
        var table = Table(new PocoSourceBuilder("mem").AddTable("t", new List<UnannotatedRow>()).Build());

        Assert.True(table.Columns[table.IndexOfColumn("Text")].Type.Nullable);
        Assert.False(table.Columns[table.IndexOfColumn("Number")].Type.Nullable);
    }

    /// <summary>
    /// Properties and fields live in different metadata tables, so their declaration order can only be
    /// reconstructed within each kind. Chalk puts every property first, each group in source order.
    /// </summary>
    [Fact]
    public void Members_keep_declaration_order_and_skip_static_indexer_and_write_only_ones()
    {
        var table = Table(new PocoSourceBuilder("mem").AddTable("t", new List<MixedMemberRow>()).Build());

        Assert.Equal(["First", "Third", "Second"], table.Columns.Select(c => c.Name));
    }

    [Fact]
    public void Attributes_override_the_name_the_precision_and_the_scale()
    {
        var table = Table(new PocoSourceBuilder("mem").AddTable("t", new List<AttributedRow>()).Build());

        Assert.Equal(ChalkType.Decimal(28, 10), table.Columns[table.IndexOfColumn("px")].Type);
        Assert.Equal(ChalkType.Decimal(18, 4), table.Columns[table.IndexOfColumn("Fee")].Type);
    }

    [Fact]
    public void Chalk_column_as_integer_maps_an_enum_to_its_underlying_kind()
    {
        var table = Table(new PocoSourceBuilder("mem").AddTable("t", new List<AttributedRow>()).Build());

        Assert.Equal(ChalkType.Int32(), table.Columns[table.IndexOfColumn("Shade")].Type);
    }

    [Fact]
    public void Chalk_ignore_and_chalk_column_ignore_both_leave_a_member_out()
    {
        var table = Table(new PocoSourceBuilder("mem").AddTable("t", new List<AttributedRow>()).Build());

        Assert.Equal(-1, table.IndexOfColumn("Skipped"));
        Assert.Equal(-1, table.IndexOfColumn("AlsoSkipped"));
    }

    [Fact]
    public void The_ignore_builder_call_leaves_a_member_out()
    {
        var table = Table(new PocoSourceBuilder("mem")
            .AddTable("t", new List<BarRow>(), t => t.Ignore(r => r.Volume))
            .Build());

        Assert.Equal(-1, table.IndexOfColumn("Volume"));
        Assert.Equal(4, table.Columns.Count);
    }

    [Fact]
    public void Snake_case_naming_renames_every_inferred_column_but_not_an_explicit_name()
    {
        var table = Table(new PocoSourceBuilder("mem")
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .AddTable("t", new List<AttributedRow>())
            .Build());

        Assert.Equal(["px", "fee", "shade", "symbol_id", "http_status"], table.Columns.Select(c => c.Name));
    }

    [Fact]
    public void As_is_naming_is_the_default()
    {
        var table = Table(new PocoSourceBuilder("mem").AddTable("t", new List<AttributedRow>()).Build());

        Assert.Equal(["px", "Fee", "Shade", "SymbolId", "HTTPStatus"], table.Columns.Select(c => c.Name));
    }

    [Fact]
    public void The_default_decimal_scale_is_ten_and_the_builder_can_change_it()
    {
        var table = Table(new PocoSourceBuilder("mem")
            .DefaultDecimalScale(4)
            .AddTable("t", new List<AllTypesRow>())
            .Build());

        Assert.Equal(ChalkType.Decimal(28, 4), table.Columns[table.IndexOfColumn("Money")].Type);
    }

    [Fact]
    public void An_unsupported_member_type_is_a_registration_error_naming_the_member()
    {
        var error = Assert.Throws<CatalogValidationException>(
            () => new PocoSourceBuilder("mem").AddTable("t", new List<UnsupportedRow>()).Build());

        Assert.Contains("Link", error.Message, StringComparison.Ordinal);
        Assert.Contains("Uri", error.Message, StringComparison.Ordinal);
        Assert.Contains("Column(name, row => …)", error.Message, StringComparison.Ordinal);
        Assert.Contains("Ignore", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unsupported_member_can_be_ignored_or_projected_instead()
    {
        var table = Table(new PocoSourceBuilder("mem")
            .AddTable(
                "t",
                new List<UnsupportedRow>(),
                t => t
                    .Ignore(r => r.Link)
                    .Column("host", r => r.Link.Host))
            .Build());

        Assert.Equal(["Id", "host"], table.Columns.Select(c => c.Name));
        Assert.Equal(ChalkType.String(true), table.Columns[1].Type);
    }

    [Fact]
    public void A_column_override_may_change_nullability_precision_and_scale()
    {
        var table = Table(new PocoSourceBuilder("mem")
            .AddTable(
                "t",
                new List<AllTypesRow>(),
                t => t
                    .Column(r => r.Money, "amount", ChalkType.Decimal(12, 2))
                    .Column(r => r.Text, type: ChalkType.String(true)))
            .Build());

        Assert.Equal(ChalkType.Decimal(12, 2), table.Columns[table.IndexOfColumn("amount")].Type);
        Assert.Equal(ChalkType.String(true), table.Columns[table.IndexOfColumn("Text")].Type);
    }

    [Fact]
    public void A_column_override_may_not_change_the_kind()
    {
        var error = Assert.Throws<CatalogValidationException>(() => new PocoSourceBuilder("mem")
            .AddTable("t", new List<AllTypesRow>(), t => t.Column(r => r.Medium, type: ChalkType.String()))
            .Build());

        Assert.Contains("Medium", error.Message, StringComparison.Ordinal);
        Assert.Contains("Column(name, projection)", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_computed_column_is_typed_from_the_projection_and_appended_last()
    {
        var table = Table(new PocoSourceBuilder("mem")
            .AddTable(
                "t",
                new List<BarRow>(),
                t => t.Column("year", r => r.Ts.Year))
            .Build());

        Assert.Equal("year", table.Columns[^1].Name);
        Assert.Equal(ChalkType.Int32(), table.Columns[^1].Type);
    }

    [Fact]
    public void The_schema_is_local_named_and_carries_the_row_count()
    {
        var rows = new List<BarRow> { Bar(1), Bar(2), Bar(3) };
        var source = new PocoSourceBuilder("mem", "trading").AddTable("bars", rows).Build();

        var schema = source.DescribeSchema();

        Assert.Equal("mem", schema.SourceId);
        Assert.Equal("trading", schema.Name);
        Assert.Equal(SourceKind.Local, schema.Kind);
        Assert.Equal(3, schema.Tables[0].RowCount);
    }

    [Fact]
    public void The_schema_name_defaults_to_main()
    {
        var source = new PocoSourceBuilder("mem").AddTable("bars", new List<BarRow>()).Build();

        Assert.Equal("main", source.DescribeSchema().Name);
        Assert.Equal("main", source.SchemaName);
    }

    [Fact]
    public void A_source_with_no_tables_is_a_misuse()
    {
        Assert.Throws<InvalidOperationException>(() => new PocoSourceBuilder("mem").Build());
    }

    /// <summary>
    /// A <c>Func</c> registration is read at <c>Build()</c> and at every refresh, and a describe
    /// answers from the snapshot that read produced (D260 §1). A host that mutates the collection it
    /// handed over is breaking the one rule it was given; what it gets is the count the snapshot
    /// captured, not half of an edit.
    /// </summary>
    [Fact]
    public void A_func_registered_table_describes_the_snapshot_it_captured()
    {
        var rows = new List<BarRow> { Bar(1) };
        var source = new PocoSourceBuilder("mem").AddTable("bars", () => rows).Build();

        Assert.Equal(1, source.DescribeSchema().Tables[0].RowCount);

        rows.Add(Bar(2));

        Assert.Equal(1, source.DescribeSchema().Tables[0].RowCount);
    }

    [Fact]
    public void Declared_collations_and_unique_keys_reach_the_catalog_as_column_indexes()
    {
        var rows = new List<BarRow> { Bar(1), Bar(2) };
        var table = Table(new PocoSourceBuilder("mem")
            .AddTable("bars", rows, t => t
                .OrderedBy(r => r.Ts)
                .ThenBy(r => r.Symbol, SortDirection.DescNullsFirst)
                .UniqueKey(r => r.Ts, r => r.Symbol))
            .Build());

        var collation = Assert.Single(table.Collations);
        Assert.Equal(new KeyOrder(0, SortDirection.AscNullsLast), collation.Keys[0]);
        Assert.Equal(new KeyOrder(1, SortDirection.DescNullsFirst), collation.Keys[1]);
        Assert.Equal([0, 1], Assert.Single(table.UniqueKeys).Columns);
    }

    [Fact]
    public void Then_by_without_ordered_by_is_a_misuse()
    {
        Assert.Throws<InvalidOperationException>(() => new PocoSourceBuilder("mem")
            .AddTable("bars", new List<BarRow>(), t => t.ThenBy(r => r.Ts))
            .Build());
    }

    [Fact]
    public void A_key_that_names_something_that_is_not_a_member_is_a_registration_error()
    {
        var error = Assert.Throws<CatalogValidationException>(() => new PocoSourceBuilder("mem")
            .AddTable("bars", new List<BarRow>(), t => t.OrderedBy(r => r.Ts.Year))
            .Build());

        Assert.Contains("must name a property or field", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_key_over_an_ignored_member_is_a_registration_error()
    {
        var error = Assert.Throws<CatalogValidationException>(() => new PocoSourceBuilder("mem")
            .AddTable("bars", new List<BarRow>(), t => t.Ignore(r => r.Volume).OrderedBy(r => r.Volume))
            .Build());

        Assert.Contains("Volume", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_duplicate_column_name_fails_the_same_validation_the_planner_would_run()
    {
        var error = Assert.Throws<CatalogValidationException>(() => new PocoSourceBuilder("mem")
            .AddTable("bars", new List<BarRow>(), t => t.Column("Symbol", r => r.Ts.Year))
            .Build());

        Assert.Contains("used twice", error.Message, StringComparison.Ordinal);
    }

    private static BarRow Bar(int i) =>
        new(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified).AddMinutes(i), "BTC", i, i, Colour.Red);

    private static TableDescriptor Table(PocoSource source) => source.DescribeSchema().Tables[0];
}

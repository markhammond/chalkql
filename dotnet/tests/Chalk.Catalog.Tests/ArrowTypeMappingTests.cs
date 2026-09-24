using Apache.Arrow.Types;
using Chalk.Ir;
using Chalk.Sources;

namespace Chalk.Catalog.Tests;

public sealed class ArrowTypeMappingTests
{
    /// <summary>Every kind in <c>docs/design/02-ir.md</c> §3 with the precision/scale it is used at.</summary>
    public static TheoryData<TypeKind, ChalkType, string> AllKinds() => new()
    {
        { TypeKind.Bool, ChalkType.Bool(), "bool" },
        { TypeKind.I8, ChalkType.Int8(), "int8" },
        { TypeKind.I16, ChalkType.Int16(), "int16" },
        { TypeKind.I32, ChalkType.Int32(), "int32" },
        { TypeKind.I64, ChalkType.Int64(), "int64" },
        { TypeKind.Fp32, ChalkType.Float32(), "float" },
        { TypeKind.Fp64, ChalkType.Float64(), "double" },
        { TypeKind.String, ChalkType.String(), "utf8" },
        { TypeKind.Binary, ChalkType.Binary(), "binary" },
        { TypeKind.Date, ChalkType.Date(), "date32" },
        { TypeKind.Time, ChalkType.Time(6), "time64[Microsecond]" },
        { TypeKind.Timestamp, ChalkType.Timestamp(9), "timestamp[Nanosecond, tz=none]" },
        { TypeKind.TimestampTz, ChalkType.TimestampTz(9), "timestamp[Nanosecond, tz=UTC]" },
        { TypeKind.Decimal, ChalkType.Decimal(28, 10), "decimal128(28,10)" },
        { TypeKind.Uuid, ChalkType.Uuid(), "fixed_size_binary[16]" },
        { TypeKind.IntervalDay, ChalkType.IntervalDay(), "duration[Microsecond]" },
        { TypeKind.IntervalYear, ChalkType.IntervalYear(), "interval[YearMonth]" },
    };

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void Every_type_kind_maps_to_the_arrow_type_the_design_names(
        TypeKind kind, ChalkType type, string expectedArrow)
    {
        Assert.Equal(kind, type.Kind);

        var arrow = ArrowTypeMapping.ToArrow(type);

        Assert.Equal(expectedArrow, ArrowTypeMapping.DescribeArrow(arrow));
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void Every_type_kind_round_trips_through_arrow(TypeKind kind, ChalkType type, string _)
    {
        foreach (var nullable in new[] { false, true })
        {
            var source = type.WithNullable(nullable);

            var back = ArrowTypeMapping.FromArrow(ArrowTypeMapping.ToArrow(source), nullable);

            Assert.Equal(source, back);
            Assert.Equal(kind, back.Kind);
        }
    }

    [Theory]
    [InlineData(0, TimeUnit.Millisecond)]
    [InlineData(3, TimeUnit.Millisecond)]
    [InlineData(4, TimeUnit.Microsecond)]
    [InlineData(6, TimeUnit.Microsecond)]
    [InlineData(7, TimeUnit.Nanosecond)]
    [InlineData(9, TimeUnit.Nanosecond)]
    public void Timestamp_precision_selects_the_arrow_unit(int precision, TimeUnit expected)
    {
        Assert.Equal(expected, ArrowTypeMapping.TimestampUnit(precision));

        var arrow = (TimestampType)ArrowTypeMapping.ToArrow(ChalkType.Timestamp(precision));
        Assert.Equal(expected, arrow.Unit);
        Assert.True(string.IsNullOrEmpty(arrow.Timezone));
    }

    [Fact]
    public void Timestamp_tz_carries_utc()
    {
        var arrow = (TimestampType)ArrowTypeMapping.ToArrow(ChalkType.TimestampTz(9));

        Assert.Equal("UTC", arrow.Timezone);
    }

    [Fact]
    public void A_row_type_becomes_an_arrow_schema_with_names_verbatim()
    {
        var rowType = new RowType
        {
            Fields =
            {
                new Field { Name = "a", Type = ChalkType.Int64().ToProto() },
                new Field { Name = "a", Type = ChalkType.String(nullable: true).ToProto() },
                new Field { Name = "EXPR$0", Type = ChalkType.Float64().ToProto() },
            },
        };

        var schema = ArrowTypeMapping.ToArrowSchema(rowType);

        Assert.Equal(3, schema.FieldsList.Count);
        Assert.Equal(["a", "a", "EXPR$0"], schema.FieldsList.Select(f => f.Name));
        Assert.False(schema.FieldsList[0].IsNullable);
        Assert.True(schema.FieldsList[1].IsNullable);
    }

    [Fact]
    public void A_projection_of_a_table_becomes_the_scan_output_schema()
    {
        var table = new TableDescriptor
        {
            Name = "bars",
            RowCount = 10,
            Columns =
            [
                new ColumnDescriptor { Name = "symbol", Type = ChalkType.String() },
                new ColumnDescriptor { Name = "ts", Type = ChalkType.Timestamp(9) },
                new ColumnDescriptor { Name = "close", Type = ChalkType.Float64() },
            ],
        };

        var schema = ArrowTypeMapping.ToArrowSchema(table, [2, 0]);

        Assert.Equal(["close", "symbol"], schema.FieldsList.Select(f => f.Name));
    }

    [Fact]
    public void Equivalence_compares_names_types_nullability_and_order()
    {
        var a = ArrowTypeMapping.ToArrowSchema(
            [new ColumnDescriptor { Name = "x", Type = ChalkType.Int64() }]);
        var same = ArrowTypeMapping.ToArrowSchema(
            [new ColumnDescriptor { Name = "x", Type = ChalkType.Int64() }]);
        var nullable = ArrowTypeMapping.ToArrowSchema(
            [new ColumnDescriptor { Name = "x", Type = ChalkType.Int64(nullable: true) }]);
        var renamed = ArrowTypeMapping.ToArrowSchema(
            [new ColumnDescriptor { Name = "y", Type = ChalkType.Int64() }]);
        var retyped = ArrowTypeMapping.ToArrowSchema(
            [new ColumnDescriptor { Name = "x", Type = ChalkType.Int32() }]);

        Assert.True(ArrowTypeMapping.AreEquivalent(a, same));
        Assert.False(ArrowTypeMapping.AreEquivalent(a, nullable));
        Assert.False(ArrowTypeMapping.AreEquivalent(a, renamed));
        Assert.False(ArrowTypeMapping.AreEquivalent(a, retyped));
    }

    [Fact]
    public void Two_decimal_scales_are_not_equivalent()
    {
        var a = ArrowTypeMapping.ToArrowSchema([new ColumnDescriptor { Name = "d", Type = ChalkType.Decimal(28, 10) }]);
        var b = ArrowTypeMapping.ToArrowSchema([new ColumnDescriptor { Name = "d", Type = ChalkType.Decimal(28, 2) }]);

        Assert.False(ArrowTypeMapping.AreEquivalent(a, b));
    }

    [Fact]
    public void An_unmappable_arrow_type_is_unsupported_not_approximated()
    {
        var ex = Assert.Throws<UnsupportedFeatureException>(
            () => ArrowTypeMapping.FromArrow(new MapType(Int32Type.Default, Int32Type.Default), nullable: false));

        Assert.Contains("docs/design/02-ir.md §3", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A COMPOSITE is an Arrow struct (D291): one child field per composite field, named as declared and
    /// nullable as declared, the composite's own nullability on the column's field.
    /// </summary>
    [Fact]
    public void A_composite_round_trips_through_arrow_with_its_fields_named_and_nullable_as_declared()
    {
        var classification = ChalkType.Composite(
            [new CompositeField("Category", ChalkType.String()), new CompositeField("Confidence", ChalkType.Float64(nullable: true))],
            nullable: true);

        var arrow = Assert.IsType<StructType>(ArrowTypeMapping.ToArrow(classification));

        Assert.Equal(["Category", "Confidence"], arrow.Fields.Select(f => f.Name));
        Assert.Equal([false, true], arrow.Fields.Select(f => f.IsNullable));
        Assert.IsType<StringType>(arrow.Fields[0].DataType);
        Assert.IsType<DoubleType>(arrow.Fields[1].DataType);
        Assert.Equal(classification, ArrowTypeMapping.FromArrow(arrow, nullable: true));
        Assert.Equal(classification.WithNullable(false), ArrowTypeMapping.FromArrow(arrow, nullable: false));
    }

    /// <summary>The output string layout reaches a composite value's STRING fields as it reaches a list's element.</summary>
    [Theory]
    [InlineData(StringLayouts.Utf8View, typeof(StringViewType))]
    [InlineData(StringLayouts.Utf8, typeof(StringType))]
    public void The_string_layout_reaches_a_composite_s_fields(StringLayouts layout, System.Type expected)
    {
        var classification = ChalkType.Composite(
            new CompositeField("Category", ChalkType.String()), new CompositeField("Confidence", ChalkType.Float64()));

        var arrow = Assert.IsType<StructType>(ArrowTypeMapping.ToArrow(classification, layout));

        Assert.IsType(expected, arrow.Fields[0].DataType);
        Assert.IsType<DoubleType>(arrow.Fields[1].DataType);
    }

    /// <summary>A LIST does round-trip since D58; it is exactly one level deep.</summary>
    [Fact]
    public void A_list_round_trips_through_arrow()
    {
        var list = ChalkType.List(ChalkType.String(nullable: true), nullable: true);
        var arrow = ArrowTypeMapping.ToArrow(list);

        Assert.IsType<ListType>(arrow);
        Assert.Equal(list, ArrowTypeMapping.FromArrow(arrow, nullable: true));
    }
}

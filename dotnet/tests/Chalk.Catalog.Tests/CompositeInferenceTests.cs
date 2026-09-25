using Chalk.Ir;

namespace Chalk.Catalog.Tests;

/// <summary>
/// D294 (ADR 0077): a function's result typed as a record of the host's own is a COMPOSITE, read off the
/// record's public properties in declaration order, each a Tier 1 type and nullable as its CLR type
/// is — and refused, naming the property, where it cannot be one.
/// </summary>
public sealed class CompositeInferenceTests
{
    /// <summary>The owner's record, exactly as the question put it.</summary>
    public readonly record struct Classification(Utf8String Category, double Confidence);

    /// <summary>What a composite-valued aggregate answers.</summary>
    public readonly record struct Summary(double Total, long Count);

    /// <summary>Every nullability a field can have.</summary>
    public sealed record Mixed(string Label, int? Rank, Utf8String? Code, bool Flag);

    /// <summary>A record with a DECIMAL field, which D298 lets a composite hold.</summary>
    public readonly record struct Priced(Utf8String Name, decimal Amount);

    /// <summary>Every widened kind as a field, and the nullability each CLR form carries (D298).</summary>
    public sealed record Widened(
        decimal Amount,
        DateOnly Day,
        TimeOnly? At,
        DateTime Stamp,
        DateTimeOffset Instant,
        TimeSpan Span,
        Guid Key,
        ReadOnlyMemory<byte> Bytes,
        byte[]? Blob);

    /// <summary>A record with a property outside Tier 1 even as D298 widens it.</summary>
    public readonly record struct Counted(Utf8String Name, ulong Count);

    /// <summary>A record one level too deep.</summary>
    public readonly record struct Nested(Classification Inner, int Rank);

    /// <summary>Two properties that SQL would read as one field.</summary>
    public sealed class Shouting
    {
        public int Name { get; init; }

        public int NAME { get; init; }
    }

    /// <summary>Nothing to read.</summary>
    public sealed class Hollow
    {
        public void Touch()
        {
        }
    }

    /// <summary>A plain class whose constructor order is not its properties': metadata order wins.</summary>
    public sealed class Declared
    {
        public Declared(int a, double b)
        {
            A = a;
            B = b;
        }

        public double B { get; }

        public int A { get; }
    }

    private static FunctionDescriptor Build(Func<FunctionBuilder, FunctionBuilder> declare) =>
        declare(new FunctionBuilder("f")).Client().Build();

    [Fact]
    public void The_owner_s_record_is_a_composite_of_two_non_nullable_fields()
    {
        var descriptor = Build(f => f.Scalar<string, double, Classification>("description", "amount"));

        var type = descriptor.ReturnType!.Value;

        Assert.Equal(
            ChalkType.Composite(
                new CompositeField("Category", ChalkType.String()),
                new CompositeField("Confidence", ChalkType.Float64())),
            type);
        Assert.Equal("COMPOSITE(Category STRING, Confidence FP64)", type.ToString());
    }

    [Fact]
    public void A_nullable_record_is_a_nullable_composite_of_the_same_fields()
    {
        var descriptor = Build(f => f.Scalar().Returns<Classification?>());

        var type = descriptor.ReturnType!.Value;

        Assert.True(type.Nullable);
        Assert.Equal(["Category", "Confidence"], type.Fields.Select(f => f.Name));
        Assert.False(type.Fields[0].Type.Nullable);
    }

    [Fact]
    public void An_aggregate_s_record_result_is_a_nullable_composite()
    {
        var descriptor = Build(f => f.Aggregate<double, Summary>("x"));

        Assert.Equal(
            ChalkType.Composite(
                [new CompositeField("Total", ChalkType.Float64()), new CompositeField("Count", ChalkType.Int64())],
                nullable: true),
            descriptor.ReturnType);
    }

    [Fact]
    public void A_field_is_nullable_exactly_as_its_clr_type_is()
    {
        var type = Build(f => f.Scalar().Returns<Mixed>()).ReturnType!.Value;

        Assert.Equal(["Label", "Rank", "Code", "Flag"], type.Fields.Select(f => f.Name));
        Assert.Equal(ChalkType.String(nullable: true), type.Fields[0].Type);
        Assert.Equal(ChalkType.Int32(nullable: true), type.Fields[1].Type);
        Assert.Equal(ChalkType.String(nullable: true), type.Fields[2].Type);
        Assert.Equal(ChalkType.Bool(), type.Fields[3].Type);
    }

    [Fact]
    public void A_record_without_a_matching_constructor_is_read_in_declaration_order()
    {
        var type = Build(f => f.Scalar().Returns<Declared>()).ReturnType!.Value;

        Assert.Equal(["B", "A"], type.Fields.Select(f => f.Name));
    }

    [Fact]
    public void A_property_outside_tier_1_is_refused_naming_it()
    {
        var error = Assert.Throws<CatalogValidationException>(
            () => Build(f => f.Scalar().Returns<Counted>()));

        Assert.Contains("property 'Count' of Counted is UInt64", error.Message, StringComparison.Ordinal);
        Assert.Contains("functions (f)", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_record_field_is_refused_as_one_level_deep()
    {
        var error = Assert.Throws<CatalogValidationException>(
            () => Build(f => f.Scalar().Returns<Nested>()));

        Assert.Contains("property 'Inner' of Nested is Classification", error.Message, StringComparison.Ordinal);
        Assert.Contains("one level deep", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_names_equal_ignoring_case_are_refused()
    {
        var error = Assert.Throws<CatalogValidationException>(
            () => Build(f => f.Scalar().Returns<Shouting>()));

        Assert.Contains("'Name' and 'NAME'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_record_of_no_fields_is_refused()
    {
        var error = Assert.Throws<CatalogValidationException>(
            () => Build(f => f.Scalar().Returns<Hollow>()));

        Assert.Contains("Hollow has no public readable instance property", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_record_is_never_a_parameter()
    {
        var error = Assert.Throws<CatalogValidationException>(
            () => Build(f => f.Scalar().Parameter<Classification>("c").Returns<double>()));

        Assert.Contains("parameter 'c' is typed Classification", error.Message, StringComparison.Ordinal);
        Assert.Contains("a composite value is never a parameter", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_record_is_never_a_table_function_column()
    {
        var error = Assert.Throws<CatalogValidationException>(
            () => Build(f => f.TableFunction().Column<Classification>("c")));

        Assert.Contains("column 'c' is typed Classification", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_platform_value_type_is_not_read_as_a_record()
    {
        // D298 makes decimal a Tier 1 type; a platform type outside the set is still not a record.
        var error = Assert.Throws<CatalogValidationException>(
            () => Build(f => f.Scalar().Returns<ulong>()));

        Assert.Contains("UInt64 has no inferred declared type", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_widened_clr_type_is_inferred_as_the_poco_source_infers_it()
    {
        // D298: the parameter, the result and a table function's column read a CLR type the same way,
        // and the same way as an unannotated POCO member.
        (Func<FunctionBuilder, FunctionBuilder> Declare, ChalkType Expected)[] cases =
        [
            (f => f.Scalar<decimal, decimal>("x"), ChalkType.Decimal(28, 10)),
            (f => f.Scalar<DateOnly, DateOnly>("x"), ChalkType.Date()),
            (f => f.Scalar<TimeOnly, TimeOnly>("x"), ChalkType.Time(6)),
            (f => f.Scalar<DateTime, DateTime>("x"), ChalkType.Timestamp(9)),
            (f => f.Scalar<DateTimeOffset, DateTimeOffset>("x"), ChalkType.TimestampTz(9)),
            (f => f.Scalar<TimeSpan, TimeSpan>("x"), ChalkType.IntervalDay()),
            (f => f.Scalar<Guid, Guid>("x"), ChalkType.Uuid()),
            (f => f.Scalar<ReadOnlyMemory<byte>, ReadOnlyMemory<byte>>("x"), ChalkType.Binary()),
            (f => f.Scalar<byte[], byte[]>("x"), ChalkType.Binary()),
        ];

        foreach (var (declare, expected) in cases)
        {
            var descriptor = Build(declare);
            Assert.Equal(expected.WithNullable(true), descriptor.Parameters[0].Type);
            Assert.Equal(expected, descriptor.ReturnType);
        }

        var table = Build(f => f.TableFunction().Column<DateOnly>("day").Column<decimal>("amount"));
        Assert.Equal(ChalkType.Date(), table.ReturnsTable[0].Type);
        Assert.Equal(ChalkType.Decimal(28, 10), table.ReturnsTable[1].Type);
    }

    [Fact]
    public void A_composite_s_fields_may_be_widened_kinds_nullable_as_their_clr_forms_are()
    {
        var descriptor = Build(f => f.Scalar().Returns<Widened>());

        Assert.Equal(
            "COMPOSITE(Amount DECIMAL(28,10), Day DATE, At TIME(6)?, Stamp TIMESTAMP(9), "
            + "Instant TIMESTAMP_TZ(9), Span INTERVAL_DAY, Key UUID, Bytes BINARY, Blob BINARY?)",
            IrTypes.Describe(descriptor.ReturnType!.Value.ToProto()));
        Assert.Equal(
            "COMPOSITE(Name STRING, Amount DECIMAL(28,10))",
            IrTypes.Describe(Build(f => f.Scalar().Returns<Priced>()).ReturnType!.Value.ToProto()));
    }

    [Fact]
    public void Utf8String_is_a_string_wherever_a_type_is_inferred()
    {
        var descriptor = Build(f => f.Scalar<Utf8String, Utf8String>("s"));

        Assert.Equal(ChalkType.String(nullable: true), descriptor.Parameters[0].Type);
        Assert.Equal(ChalkType.String(), descriptor.ReturnType);
    }

    /// <summary>
    /// A span is a STRING by inference — the spelling a delegate reads a lane in — and a BINARY only
    /// when the declaration says so, since the bytes themselves cannot tell.
    /// </summary>
    [Fact]
    public void A_span_of_bytes_is_a_string_wherever_a_type_is_inferred()
    {
        var descriptor = Build(f => f.Scalar<ReadOnlySpan<byte>, ReadOnlySpan<byte>>("s"));

        Assert.Equal(ChalkType.String(nullable: true), descriptor.Parameters[0].Type);
        Assert.Equal(ChalkType.String(), descriptor.ReturnType);

        var binary = Build(f => f.Scalar().Parameter("b", ChalkType.Binary(nullable: true)).Returns(ChalkType.Binary()));
        Assert.Equal(ChalkType.Binary(nullable: true), binary.Parameters[0].Type);
    }
}

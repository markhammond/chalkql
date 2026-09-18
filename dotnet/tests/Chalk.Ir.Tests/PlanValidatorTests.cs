using Chalk.TestKit;
using Google.Protobuf;
using static Chalk.TestKit.IrBuilder;
using IrType = Chalk.Ir.Type;

namespace Chalk.Ir.Tests;

/// <summary>
/// Structural invariants I-IR-1 … I-IR-10 (<c>docs/design/02-ir.md</c> §8). Each test names the
/// invariant it exercises so a failure points at the rule, not just the line.
/// </summary>
public sealed class PlanValidatorTests
{
    private static readonly RowType Bars = Row(
        F("symbol", Str()),
        F("ts", Timestamp()),
        F("close", Fp64()),
        F("volume", I64()),
        F("vwap", Dec(28, 10, nullable: true)));

    private static Rel BarsRead(params Collation[] collations) =>
        Read("bars", Bars, rows: 100_800, collations: collations);

    /// <summary>Rebuilds the digest after a mutation so tests fail on the invariant, not on I-IR-9.</summary>
    private static Plan Restamp(Plan plan)
    {
        plan.PlanDigest = 0;
        plan.PlanDigest = PlanDigest.Compute(plan);
        return plan;
    }

    private static InvalidPlanException AssertInvalid(Plan plan) =>
        Assert.Throws<InvalidPlanException>(() => PlanValidator.Validate(plan));

    [Fact]
    public void A_well_formed_plan_validates()
    {
        var plan = IrBuilder.Plan(
            Project(
                Filter(BarsRead(), Call(FunctionId.Eq, Bool(), Ref(Bars, 0), Lit("BTCUSDT"))),
                [("symbol", Ref(Bars, 0)), ("close", Ref(Bars, 2))]));

        PlanValidator.Validate(plan);
    }

    // ---- I-IR-1: version, unknown fields, unset oneofs ----

    [Fact]
    public void Newer_ir_version_is_rejected_loudly()
    {
        var plan = Restamp(new Plan(IrBuilder.Plan(BarsRead())) { IrVersion = IrVersion.Current + 1 });

        var ex = Assert.Throws<IrVersionMismatchException>(() => PlanValidator.Validate(plan));

        Assert.Equal(IrVersion.Current + 1, ex.PlanIrVersion);
        Assert.Equal(IrVersion.Current, ex.ClientIrVersion);
        Assert.Contains("Upgrade the Chalk client packages", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Older_ir_version_is_rejected_as_invalid()
    {
        var plan = Restamp(new Plan(IrBuilder.Plan(BarsRead())) { IrVersion = 0 });

        var ex = AssertInvalid(plan);

        Assert.Equal("I-IR-1", ex.Invariant);
    }

    [Fact]
    public void An_unknown_field_on_a_known_node_is_newer_ir()
    {
        // Field 999 does not exist on Plan. Splice it into the wire bytes and re-parse: this is exactly
        // what a client one version behind the planner would see.
        var plan = IrBuilder.Plan(BarsRead());
        var buffer = new MemoryStream();
        plan.WriteTo(buffer);
        var output = new CodedOutputStream(buffer);
        output.WriteTag(999, WireFormat.WireType.Varint);
        output.WriteInt32(7);
        output.Flush();

        var parsed = Plan.Parser.ParseFrom(buffer.ToArray());

        var ex = Assert.Throws<IrVersionMismatchException>(() => PlanValidator.Validate(parsed));
        Assert.Contains("fields this client does not know", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unset_rel_kind_reads_as_an_unknown_node_kind()
    {
        var plan = Restamp(IrBuilder.Plan(new Rel { RowType = Bars, EstRowCount = 1 }));

        var ex = Assert.Throws<IrVersionMismatchException>(() => PlanValidator.Validate(plan));

        Assert.Contains("node kind this client does not know", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unset_expr_kind_reads_as_an_unknown_expression_kind()
    {
        var plan = Restamp(IrBuilder.Plan(Filter(BarsRead(), new Expr { Type = Bool() })));

        var ex = Assert.Throws<IrVersionMismatchException>(() => PlanValidator.Validate(plan));

        Assert.Contains("kind this client does not know", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_function_id_reads_as_newer_ir()
    {
        var plan = Restamp(IrBuilder.Plan(
            Filter(BarsRead(), Call(FunctionId.Unspecified, Bool(), Ref(Bars, 0)))));

        var ex = Assert.Throws<IrVersionMismatchException>(() => PlanValidator.Validate(plan));

        Assert.Contains("function this client does not know", ex.Message, StringComparison.Ordinal);
    }

    // ---- I-IR-2: operand homogeneity ----

    [Fact]
    public void Comparing_an_i64_to_an_fp64_without_a_cast_is_rejected()
    {
        var plan = Restamp(IrBuilder.Plan(
            Filter(BarsRead(), Call(FunctionId.Gt, Bool(), Ref(Bars, 3), Lit(1.5d)))));

        var ex = AssertInvalid(plan);

        Assert.Equal("I-IR-2", ex.Invariant);
        Assert.Contains("the planner inserts a Cast", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_same_comparison_with_a_cast_validates()
    {
        var plan = IrBuilder.Plan(
            Filter(
                BarsRead(),
                Call(FunctionId.Gt, Bool(), Cast(Ref(Bars, 3), Fp64()), Lit(1.5d))));

        PlanValidator.Validate(plan);
    }

    [Fact]
    public void Decimals_of_different_scales_are_not_operand_compatible()
    {
        var plan = Restamp(IrBuilder.Plan(
            Filter(
                BarsRead(),
                Call(FunctionId.Eq, Bool(), Ref(Bars, 4), LitDecimal(1m, 28, 2)))));

        Assert.Equal("I-IR-2", AssertInvalid(plan).Invariant);
    }

    [Fact]
    public void Temporal_plus_interval_is_exempt_from_homogeneity()
    {
        var plan = IrBuilder.Plan(
            Project(
                BarsRead(),
                [("later", Call(
                    FunctionId.Add,
                    Timestamp(),
                    Ref(Bars, 1),
                    new Expr { Type = IntervalDay(), Literal = new Literal { IntervalDayValue = 86_400_000_000 } }))]));

        PlanValidator.Validate(plan);
    }

    /// <summary>
    /// V52's pre-fix shape (ADR 0024): <c>MOD(volume * 4294967296, 7)</c>, where Calcite typed the
    /// call from its second argument and declared it I32 while both operands had widened to I64. The
    /// operands agree, so homogeneity alone accepted the plan; the executor then read 64-bit lanes as
    /// 32-bit ones and divided by the zero high half. The result half of I-IR-2 rejects it at receipt.
    /// </summary>
    [Fact]
    public void An_arithmetic_call_narrower_than_its_operands_is_rejected()
    {
        var plan = Restamp(IrBuilder.Plan(
            Project(
                BarsRead(),
                [("m", Call(
                    FunctionId.Modulus,
                    I32(),
                    Call(FunctionId.Multiply, I64(), Ref(Bars, 3), Lit(4_294_967_296L)),
                    Lit(7L)))])));

        var ex = AssertInvalid(plan);

        Assert.Equal("I-IR-2", ex.Invariant);
        Assert.Contains("Modulus is declared I32", ex.Message, StringComparison.Ordinal);
        Assert.Contains("operands of kind I64", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>The shape the planner emits instead: the call at its operands' width, cast back.</summary>
    [Fact]
    public void The_same_arithmetic_at_its_operands_width_validates()
    {
        var plan = IrBuilder.Plan(
            Project(
                BarsRead(),
                [("m", Cast(
                    Call(
                        FunctionId.Modulus,
                        I64(),
                        Call(FunctionId.Multiply, I64(), Ref(Bars, 3), Lit(4_294_967_296L)),
                        Lit(7L)),
                    I32()))]));

        PlanValidator.Validate(plan);
    }

    /// <summary>
    /// A product's scale is legitimately the sum of its operands', so the result half checks the kind
    /// and nothing else.
    /// </summary>
    [Fact]
    public void A_decimal_product_may_widen_its_scale()
    {
        var plan = IrBuilder.Plan(
            Project(
                BarsRead(),
                [("p", Call(
                    FunctionId.Multiply,
                    Dec(38, 20, nullable: true),
                    Ref(Bars, 4),
                    LitDecimal(2m, 28, 10)))]));

        PlanValidator.Validate(plan);
    }

    [Fact]
    public void A_filter_condition_must_be_boolean()
    {
        var plan = Restamp(IrBuilder.Plan(Filter(BarsRead(), Ref(Bars, 3))));

        Assert.Equal("I-IR-2", AssertInvalid(plan).Invariant);
    }

    // ---- I-IR-3: index ranges ----

    [Fact]
    public void A_field_reference_past_the_end_of_the_input_row_is_rejected()
    {
        var plan = Restamp(IrBuilder.Plan(
            Filter(BarsRead(), Call(FunctionId.Eq, Bool(), Ref(9, Str()), Lit("BTCUSDT")))));

        var ex = AssertInvalid(plan);

        Assert.Equal("I-IR-3", ex.Invariant);
        Assert.Contains("out of range for an input row of 5 fields", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_grouping_key_past_the_end_of_the_input_row_is_rejected()
    {
        var read = BarsRead();
        var aggregate = new Aggregate { Input = read };
        aggregate.Groupings.Add(new Grouping { Keys = { 42u } });
        var rel = new Rel
        {
            RowType = Row(F("symbol", Str())),
            HashAggregate = new HashAggregate { Aggregate = aggregate },
        };

        Assert.Equal("I-IR-3", AssertInvalid(Restamp(IrBuilder.Plan(rel))).Invariant);
    }

    [Fact]
    public void A_dynamic_parameter_past_the_declared_types_is_rejected()
    {
        var plan = Restamp(IrBuilder.Plan(
            Filter(BarsRead(), Call(FunctionId.Eq, Bool(), Ref(Bars, 0), Param(3, Str(nullable: true)))),
            parameterTypes: [Str(nullable: true)]));

        Assert.Equal("I-IR-3", AssertInvalid(plan).Invariant);
    }

    [Fact]
    public void A_dynamic_parameter_whose_type_disagrees_with_the_declaration_is_rejected()
    {
        var plan = Restamp(IrBuilder.Plan(
            Filter(BarsRead(), Call(FunctionId.Eq, Bool(), Ref(Bars, 0), Param(0, Str()))),
            parameterTypes: [Str(nullable: true)]));

        Assert.Equal("I-IR-4", AssertInvalid(plan).Invariant);
    }

    // ---- I-IR-4: row types and estimates ----

    [Fact]
    public void A_relation_without_a_row_type_is_rejected()
    {
        var plan = Restamp(IrBuilder.Plan(new Rel { RowType = new RowType(), Read = new Read() }));

        Assert.Equal("I-IR-4", AssertInvalid(plan).Invariant);
    }

    [Fact]
    public void A_negative_row_estimate_is_rejected()
    {
        var read = BarsRead();
        read.EstRowCount = -1;

        Assert.Equal("I-IR-4", AssertInvalid(Restamp(IrBuilder.Plan(read))).Invariant);
    }

    [Fact]
    public void Output_type_must_equal_the_root_row_type()
    {
        var plan = IrBuilder.Plan(BarsRead());
        plan.OutputType = Row(F("nope", Str()));

        var ex = AssertInvalid(Restamp(plan));

        Assert.Equal("I-IR-4", ex.Invariant);
        Assert.Contains("differs from the root's row type", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_filter_may_not_change_its_input_row()
    {
        var read = BarsRead();
        var rel = new Rel
        {
            RowType = Row(F("symbol", Str())),
            Filter = new Filter { Input = read, Condition = Lit(true) },
        };

        Assert.Equal("I-IR-4", AssertInvalid(Restamp(IrBuilder.Plan(rel))).Invariant);
    }

    [Theory]
    [InlineData(TypeKind.Decimal, 0u, 0u)]     // precision 0
    [InlineData(TypeKind.Decimal, 10u, 20u)]   // scale > precision
    [InlineData(TypeKind.Decimal, 39u, 0u)]    // precision > 38
    [InlineData(TypeKind.Timestamp, 10u, 0u)]  // temporal precision > 9
    [InlineData(TypeKind.Time, 7u, 0u)]        // TIME precision > 6
    [InlineData(TypeKind.I64, 3u, 0u)]         // precision on a kind that has none
    public void Malformed_types_are_rejected(TypeKind kind, uint precision, uint scale)
    {
        var type = new IrType { Kind = kind, Precision = precision, Scale = scale };
        var plan = Restamp(IrBuilder.Plan(Read("t", Row(F("c", type)))));

        Assert.Equal("I-IR-4", AssertInvalid(plan).Invariant);
    }

    // ---- I-IR-5: sort fields ----

    [Fact]
    public void A_sort_field_that_is_not_a_field_reference_is_rejected()
    {
        var read = BarsRead();
        var sort = new Sort { Input = read };
        sort.Fields.Add(new SortField
        {
            Expr = Call(FunctionId.Upper, Str(), Ref(Bars, 0)),
            Direction = SortDirection.AscNullsLast,
        });

        var ex = AssertInvalid(Restamp(IrBuilder.Plan(new Rel { RowType = Bars, Sort = sort })));

        Assert.Equal("I-IR-5", ex.Invariant);
        Assert.Contains("always a FieldRef", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unspecified_sort_direction_is_rejected()
    {
        var read = BarsRead();
        var sort = new Sort { Input = read };
        sort.Fields.Add(new SortField { Expr = Ref(Bars, 1) });

        Assert.Equal(
            "I-IR-5",
            AssertInvalid(Restamp(IrBuilder.Plan(new Rel { RowType = Bars, Sort = sort }))).Invariant);
    }

    [Fact]
    public void A_negative_top_n_count_is_rejected()
    {
        var rel = TopN(BarsRead(), offset: 0, count: 10, Desc(2, Fp64()));
        rel.TopN.Count = -1;

        Assert.Equal("I-IR-5", AssertInvalid(Restamp(IrBuilder.Plan(rel))).Invariant);
    }

    [Fact]
    public void A_negative_fetch_offset_is_rejected()
    {
        var rel = Fetch(BarsRead(), offset: -5, count: 10);

        Assert.Equal("I-IR-5", AssertInvalid(Restamp(IrBuilder.Plan(rel))).Invariant);
    }

    [Fact]
    public void A_collation_field_out_of_range_of_the_node_s_own_output_is_rejected()
    {
        var read = BarsRead(Collation(Asc(9, Str())));

        Assert.Equal("I-IR-3", AssertInvalid(Restamp(IrBuilder.Plan(read))).Invariant);
    }

    // ---- I-IR-6: Read ----

    [Fact]
    public void An_empty_read_projection_is_rejected()
    {
        var read = BarsRead();
        read.Read.Projection.Clear();

        var ex = AssertInvalid(Restamp(IrBuilder.Plan(read)));

        Assert.Equal("I-IR-6", ex.Invariant);
    }

    [Fact]
    public void A_pushed_read_filter_is_rejected_unless_the_source_declared_it()
    {
        var read = BarsRead();
        read.Read.Filter = Call(FunctionId.Eq, Bool(), Ref(Bars, 0), Lit("BTCUSDT"));
        var plan = Restamp(IrBuilder.Plan(read));

        Assert.Equal("I-IR-6", AssertInvalid(plan).Invariant);

        PlanValidator.Validate(plan, new PlanValidationOptions { AllowReadFilter = true });
    }

    // ---- I-IR-7: aggregates ----

    [Fact]
    public void An_aggregate_row_type_must_be_keys_then_measures()
    {
        var read = BarsRead();
        var aggregate = new Aggregate { Input = read };
        aggregate.Groupings.Add(new Grouping { Keys = { 0u } });
        aggregate.Measures.Add(Agg(AggregateFunctionId.Count, I64()));
        var rel = new Rel
        {
            RowType = Row(F("symbol", Str())),    // the measure is missing
            HashAggregate = new HashAggregate { Aggregate = aggregate },
        };

        Assert.Equal("I-IR-7", AssertInvalid(Restamp(IrBuilder.Plan(rel))).Invariant);
    }

    [Fact]
    public void Grouping_sets_are_reserved_in_v1()
    {
        var read = BarsRead();
        var aggregate = new Aggregate { Input = read };
        aggregate.Groupings.Add(new Grouping { Keys = { 0u } });
        aggregate.Groupings.Add(new Grouping { Keys = { 1u } });
        var rel = new Rel
        {
            RowType = Row(F("symbol", Str())),
            HashAggregate = new HashAggregate { Aggregate = aggregate },
        };

        var ex = AssertInvalid(Restamp(IrBuilder.Plan(rel)));

        Assert.Equal("I-IR-7", ex.Invariant);
        Assert.Contains("grouping sets are reserved", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_valid_aggregate_with_a_filtered_distinct_measure_validates()
    {
        var read = BarsRead();
        var plan = IrBuilder.Plan(HashAggregate(
            read,
            keys: [0],
            measures:
            [
                ("n", Agg(AggregateFunctionId.Count, I64())),
                ("vol", Agg(
                    AggregateFunctionId.Sum,
                    I64(nullable: true),
                    Ref(Bars, 3),
                    distinct: true,
                    filter: Call(FunctionId.IsNotNull, Bool(), Ref(Bars, 4)))),
            ],
            rows: 5));

        PlanValidator.Validate(plan);
    }

    // ---- I-IR-8: literals ----

    [Fact]
    public void A_literal_whose_value_field_disagrees_with_its_type_is_rejected()
    {
        var wrong = new Expr { Type = I64(), Literal = new Literal { StringValue = "nope" } };
        var plan = Restamp(IrBuilder.Plan(
            Filter(BarsRead(), Call(FunctionId.Eq, Bool(), Ref(Bars, 3), wrong))));

        var ex = AssertInvalid(plan);

        Assert.Equal("I-IR-8", ex.Invariant);
        Assert.Contains("carries StringValue, expected I64Value", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_decimal_literal_must_carry_exactly_sixteen_bytes()
    {
        var wrong = new Expr
        {
            Type = Dec(28, 10),
            Literal = new Literal
            {
                DecimalValue = new DecimalValue { Unscaled = ByteString.CopyFrom([1, 2, 3]) },
            },
        };
        var plan = Restamp(IrBuilder.Plan(Project(BarsRead(), [("d", wrong)])));

        Assert.Equal("I-IR-8", AssertInvalid(plan).Invariant);
    }

    [Fact]
    public void A_uuid_literal_must_carry_exactly_sixteen_bytes()
    {
        var wrong = new Expr
        {
            Type = Uuid(),
            Literal = new Literal { UuidValue = ByteString.CopyFrom([1, 2, 3]) },
        };
        var plan = Restamp(IrBuilder.Plan(Project(BarsRead(), [("u", wrong)])));

        Assert.Equal("I-IR-8", AssertInvalid(plan).Invariant);
    }

    [Fact]
    public void A_null_literal_must_have_a_nullable_type()
    {
        var wrong = new Expr { Type = Str(), Literal = new Literal { IsNull = true } };
        var plan = Restamp(IrBuilder.Plan(Project(BarsRead(), [("s", wrong)])));

        Assert.Equal("I-IR-8", AssertInvalid(plan).Invariant);
    }

    [Fact]
    public void An_i8_literal_out_of_range_is_rejected()
    {
        var wrong = new Expr { Type = I8(), Literal = new Literal { I8Value = 300 } };
        var plan = Restamp(IrBuilder.Plan(Project(BarsRead(), [("b", wrong)])));

        Assert.Equal("I-IR-8", AssertInvalid(plan).Invariant);
    }

    // ---- I-IR-9: digest ----

    [Fact]
    public void A_digest_that_does_not_recompute_is_a_contract_bug()
    {
        var plan = IrBuilder.Plan(BarsRead());
        plan.PlanDigest ^= 1;

        var ex = AssertInvalid(plan);

        Assert.Equal("I-IR-9", ex.Invariant);
        Assert.Contains("canonical encoding", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Digest_verification_can_be_turned_off_for_the_recorder()
    {
        var plan = IrBuilder.Plan(BarsRead());
        plan.PlanDigest = 0;

        PlanValidator.Validate(plan, new PlanValidationOptions { VerifyDigest = false });
    }

    // ---- I-IR-10: EnumArg ----

    [Fact]
    public void An_enum_arg_under_extract_is_legal()
    {
        var plan = IrBuilder.Plan(Project(
            BarsRead(),
            [("h", Call(FunctionId.Extract, I64(), EnumArg("HOUR"), Ref(Bars, 1)))]));

        PlanValidator.Validate(plan);
    }

    [Fact]
    public void An_enum_arg_anywhere_else_is_rejected()
    {
        var plan = Restamp(IrBuilder.Plan(Project(
            BarsRead(),
            [("x", Call(FunctionId.Upper, Str(), EnumArg("HOUR")))])));

        var ex = AssertInvalid(plan);

        Assert.Equal("I-IR-10", ex.Invariant);
        Assert.Contains("does not take an EnumArg", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_nested_enum_arg_is_rejected()
    {
        var plan = Restamp(IrBuilder.Plan(Project(
            BarsRead(),
            [("h", Call(
                FunctionId.Extract,
                I64(),
                EnumArg("HOUR"),
                Call(FunctionId.FloorTemporal, Timestamp(), Ref(Bars, 1), EnumArg("HOUR"))))])));

        PlanValidator.Validate(plan);   // both EnumArgs are direct arguments of functions that take one

        var bad = Restamp(IrBuilder.Plan(Project(
            BarsRead(),
            [("h", Call(
                FunctionId.Extract,
                I64(),
                Call(FunctionId.Coalesce, Str(), EnumArg("HOUR")),
                Ref(Bars, 1)))])));

        Assert.Equal("I-IR-10", AssertInvalid(bad).Invariant);
    }

    // ---- IN lists and CASE ----

    [Fact]
    public void An_in_list_of_literals_validates()
    {
        var plan = IrBuilder.Plan(Filter(
            BarsRead(),
            In(Ref(Bars, 0), Bool(), Lit("BTCUSDT"), Lit("ETHUSDT"))));

        PlanValidator.Validate(plan);
    }

    [Fact]
    public void An_in_list_containing_a_computed_option_is_rejected()
    {
        var plan = Restamp(IrBuilder.Plan(Filter(
            BarsRead(),
            In(Ref(Bars, 0), Bool(), Lit("BTCUSDT"), Call(FunctionId.Upper, Str(), Ref(Bars, 0))))));

        var ex = AssertInvalid(plan);

        Assert.Contains("rewritten by the planner to OR", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_case_without_an_else_branch_is_rejected()
    {
        var ifThen = new IfThen();
        ifThen.Clauses.Add(new IfClause { Condition = Lit(true), Result = Lit("up") });
        var expr = new Expr { Type = Str(), IfThen = ifThen };
        var plan = Restamp(IrBuilder.Plan(Project(BarsRead(), [("dir", expr)])));

        var ex = AssertInvalid(plan);

        Assert.Contains("the planner emits a typed NULL literal", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_case_whose_branches_disagree_on_type_is_rejected()
    {
        var plan = Restamp(IrBuilder.Plan(Project(
            BarsRead(),
            [("dir", Case(Str(), Lit(1L), (Lit(true), Lit("up"))))])));

        Assert.Equal("I-IR-2", AssertInvalid(plan).Invariant);
    }

    // ---- VirtualTable ----

    [Fact]
    public void An_empty_virtual_table_validates()
    {
        var plan = IrBuilder.Plan(Values(Row(F("one", I32()))));

        PlanValidator.Validate(plan);
    }

    [Fact]
    public void A_virtual_table_row_of_the_wrong_width_is_rejected()
    {
        var plan = Restamp(IrBuilder.Plan(
            Values(Row(F("one", I32()), F("s", Str())), [Lit(1)])));

        Assert.Equal("I-IR-4", AssertInvalid(plan).Invariant);
    }

    [Fact]
    public void A_virtual_table_value_that_is_not_a_literal_is_rejected()
    {
        var plan = Restamp(IrBuilder.Plan(
            Values(Row(F("one", I32())), [Ref(0, I32())])));

        Assert.Equal("I-IR-4", AssertInvalid(plan).Invariant);
    }

    // ---- error message quality ----

    [Fact]
    public void The_message_names_the_invariant_and_the_path()
    {
        var plan = Restamp(IrBuilder.Plan(
            Filter(BarsRead(), Call(FunctionId.Eq, Bool(), Ref(9, Str()), Lit("x")))));

        var ex = AssertInvalid(plan);

        Assert.Equal("root/Filter.condition.args[0]", ex.Path);
        Assert.Contains("root/Filter.condition.args[0]", ex.Message, StringComparison.Ordinal);
        Assert.Contains("I-IR-3", ex.Message, StringComparison.Ordinal);
        Assert.Contains("docs/design/02-ir.md §8", ex.Message, StringComparison.Ordinal);
    }
}

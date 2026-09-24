using Chalk.TestKit;
using static Chalk.TestKit.IrBuilder;
using IrType = Chalk.Ir.Type;

namespace Chalk.Ir.Tests;

/// <summary>
/// The COMPOSITE rules (D291, ADR 0077): I-IR-21 a composite type is well formed, I-IR-22 a field access
/// reads a field of one and is typed as that field, and I-IR-23 a composite value is never where it would be
/// compared, grouped, sorted, joined on, cast, stored or bound. Each rule has its positive case beside
/// its negatives, so a rule that refuses everything fails here too.
/// </summary>
public sealed class CompositeValidatorTests
{
    private static readonly RowType Transactions = Row(
        F("id", I64()),
        F("description", Str()),
        F("amount", Fp64()));

    /// <summary>What <c>classify_transaction</c> returns: the owner's two fields.</summary>
    private static readonly IrType Classification = Composite(
        F("category", Str()),
        F("confidence", Fp64()));

    private static Rel TransactionsRead() => Read("transactions", Transactions, rows: 1_000);

    private static Expr Classify() =>
        UserCall(
            "main.classify_transaction",
            Classification,
            Ref(Transactions, 1),
            Ref(Transactions, 2));

    /// <summary>`SELECT id, classify_transaction(description, amount) AS c FROM transactions`.</summary>
    private static Rel WithComposite() =>
        Project(TransactionsRead(), [("id", Ref(Transactions, 0)), ("c", Classify())]);

    private static InvalidPlanException AssertInvalid(Plan plan) =>
        Assert.Throws<InvalidPlanException>(() => PlanValidator.Validate(plan));

    private static Plan PlanOverType(IrType type) =>
        IrBuilder.Plan(Project(TransactionsRead(), [("x", UserCall("main.f", type, Ref(Transactions, 1)))]));

    // ---- I-IR-21: a COMPOSITE type is well formed ----

    [Fact]
    public void A_composite_of_scalar_fields_validates()
    {
        PlanValidator.Validate(IrBuilder.Plan(WithComposite()));
    }

    [Fact]
    public void A_nullable_composite_with_fields_of_their_own_nullability_validates()
    {
        PlanValidator.Validate(PlanOverType(Composite(
            nullable: true, F("category", Str()), F("confidence", Fp64(nullable: true)))));
    }

    [Fact]
    public void A_composite_with_no_fields_is_refused()
    {
        var ex = AssertInvalid(PlanOverType(Composite()));

        Assert.Equal("I-IR-21", ex.Invariant);
        Assert.Contains("has no fields", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_field_names_equal_ignoring_case_are_refused()
    {
        var ex = AssertInvalid(PlanOverType(Composite(F("Category", Str()), F("category", Fp64()))));

        Assert.Equal("I-IR-21", ex.Invariant);
        Assert.Contains("'category' is used twice ignoring case", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_field_without_a_name_is_refused()
    {
        var ex = AssertInvalid(PlanOverType(Composite(F(string.Empty, Str()))));

        Assert.Equal("I-IR-21", ex.Invariant);
    }

    [Fact]
    public void A_list_field_is_refused_as_one_level_deep()
    {
        var ex = AssertInvalid(PlanOverType(Composite(F("tags", List(Str())))));

        Assert.Equal("I-IR-21", ex.Invariant);
        Assert.Contains("one level deep", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_composite_field_is_refused_as_one_level_deep()
    {
        var ex = AssertInvalid(PlanOverType(Composite(F("inner", Composite(F("a", I32()))))));

        Assert.Equal("I-IR-21", ex.Invariant);
        Assert.Contains("the field is a COMPOSITE", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_scalar_carrying_fields_is_refused()
    {
        var type = Str();
        type.Fields.Add(F("a", I32()));

        var ex = AssertInvalid(PlanOverType(type));

        Assert.Equal("I-IR-21", ex.Invariant);
        Assert.Contains("only a COMPOSITE has fields", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_list_of_composites_is_refused_by_the_list_s_own_depth_rule()
    {
        var ex = AssertInvalid(PlanOverType(List(Classification)));

        Assert.Equal("I-IR-12", ex.Invariant);
        Assert.Contains("a LIST's element is a COMPOSITE", ex.Message, StringComparison.Ordinal);
    }

    // ---- I-IR-22: a field access ----

    [Fact]
    public void A_field_access_over_a_call_validates()
    {
        var plan = IrBuilder.Plan(Project(
            TransactionsRead(),
            [("category", FieldAccess(Classify(), 0)), ("confidence", FieldAccess(Classify(), 1))]));

        PlanValidator.Validate(plan);
    }

    [Fact]
    public void A_field_access_over_a_composite_column_validates()
    {
        var input = WithComposite();
        var plan = IrBuilder.Plan(Project(input, [("category", FieldAccess(Ref(input.RowType, 1), 0))]));

        PlanValidator.Validate(plan);
    }

    [Fact]
    public void A_field_of_a_nullable_composite_is_typed_nullable()
    {
        var nullable = Composite(nullable: true, F("category", Str()), F("confidence", Fp64()));
        var call = UserCall("main.classify_transaction", nullable, Ref(Transactions, 1), Ref(Transactions, 2));

        var access = FieldAccess(call, 1);

        Assert.True(access.Type.Nullable);
        PlanValidator.Validate(IrBuilder.Plan(Project(TransactionsRead(), [("confidence", access)])));
    }

    [Fact]
    public void A_non_nullable_access_over_a_nullable_composite_is_refused()
    {
        var nullable = Composite(nullable: true, F("category", Str()), F("confidence", Fp64()));
        var call = UserCall("main.classify_transaction", nullable, Ref(Transactions, 1), Ref(Transactions, 2));
        var access = FieldAccess(call, 1);
        access.Type.Nullable = false;

        var ex = AssertInvalid(IrBuilder.Plan(Project(TransactionsRead(), [("confidence", access)])));

        Assert.Equal("I-IR-22", ex.Invariant);
        Assert.Contains("made nullable when the composite is", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_field_access_over_a_scalar_is_refused()
    {
        var access = new Expr
        {
            Type = Str(),
            FieldAccess = new FieldAccess { Input = Ref(Transactions, 1), Index = 0 },
        };

        var ex = AssertInvalid(IrBuilder.Plan(Project(TransactionsRead(), [("x", access)])));

        Assert.Equal("I-IR-22", ex.Invariant);
        Assert.Contains("reads a field of a COMPOSITE", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_field_index_out_of_range_is_refused()
    {
        var access = new Expr
        {
            Type = Str(),
            FieldAccess = new FieldAccess { Input = Classify(), Index = 2 },
        };

        var ex = AssertInvalid(IrBuilder.Plan(Project(TransactionsRead(), [("x", access)])));

        Assert.Equal("I-IR-22", ex.Invariant);
        Assert.Contains("out of range for a COMPOSITE of 2 fields", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_field_access_typed_as_another_field_is_refused()
    {
        var access = FieldAccess(Classify(), 0);
        access.Type = Fp64();

        var ex = AssertInvalid(IrBuilder.Plan(Project(TransactionsRead(), [("x", access)])));

        Assert.Equal("I-IR-22", ex.Invariant);
    }

    [Fact]
    public void A_field_access_prints_as_the_planner_prints_one()
    {
        var input = WithComposite();
        var plan = IrBuilder.Plan(Project(
            input,
            [("category", FieldAccess(Ref(input.RowType, 1), 0)), ("confidence", FieldAccess(Classify(), 1))]));

        var text = plan.ToPlanText();

        Assert.Contains("$1.category", text, StringComparison.Ordinal);
        Assert.Contains("main.classify_transaction($1, $2).confidence", text, StringComparison.Ordinal);
    }

    // ---- I-IR-23: where a COMPOSITE may be, and where it may not ----

    [Fact]
    public void A_composite_is_carried_through_filter_sort_and_union_all_as_a_non_key_column()
    {
        var input = WithComposite();
        var filtered = Filter(input, Call(FunctionId.IsNotNull, Bool(), Ref(input.RowType, 1)));
        var sorted = Sort(filtered, Asc(0, I64()));
        var plan = IrBuilder.Plan(SetOp(SetOpKind.UnionAll, sorted, WithComposite()));

        PlanValidator.Validate(plan);
    }

    /// <summary>
    /// Calcite makes a union's composite nullable by copying it with every field nullable, so the
    /// output composite's fields may be wider than a branch's (ADR 0077): up to nullability means at
    /// every level of a composite, position by position, as it does for a row's columns.
    /// </summary>
    [Fact]
    public void A_union_s_composite_may_widen_its_fields_but_never_narrow_them()
    {
        var widened = Composite(
            nullable: true, F("category", Str(nullable: true)), F("confidence", Fp64(nullable: true)));
        var union = SetOp(SetOpKind.UnionAll, WithComposite(), WithComposite());
        union.RowType = Row(F("id", I64()), F("c", widened));

        PlanValidator.Validate(IrBuilder.Plan(union));

        // A branch whose field is nullable under an output field that is not.
        var narrowed = SetOp(
            SetOpKind.UnionAll,
            Project(TransactionsRead(), [("id", Ref(Transactions, 0)), ("c", UserCall(
                "main.classify_transaction",
                Composite(F("category", Str(nullable: true)), F("confidence", Fp64())),
                Ref(Transactions, 1),
                Ref(Transactions, 2)))]),
            WithComposite());
        narrowed.RowType = Row(F("id", I64()), F("c", Classification));
        var ex = AssertInvalid(IrBuilder.Plan(narrowed));
        Assert.Equal("I-IR-4", ex.Invariant);

        // And a branch with a different number of fields.
        var wider = SetOp(SetOpKind.UnionAll, WithComposite(), WithComposite());
        wider.RowType = Row(
            F("id", I64()), F("c", Composite(F("category", Str()), F("confidence", Fp64()), F("extra", I64()))));
        Assert.Equal("I-IR-4", AssertInvalid(IrBuilder.Plan(wider)).Invariant);
    }

    [Fact]
    public void A_composite_output_column_is_what_the_host_receives()
    {
        var plan = IrBuilder.Plan(WithComposite());

        PlanValidator.Validate(plan);
        Assert.Equal(TypeKind.Composite, plan.OutputType.Fields[1].Type.Kind);
    }

    [Fact]
    public void Sorting_on_a_composite_is_refused()
    {
        var input = WithComposite();

        var ex = AssertInvalid(IrBuilder.Plan(Sort(input, Asc(1, Classification))));

        Assert.Equal("I-IR-23", ex.Invariant);
        Assert.Contains("a COMPOSITE cannot be sorted on", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Grouping_by_a_composite_is_refused()
    {
        var ex = AssertInvalid(IrBuilder.Plan(HashAggregate(WithComposite(), [1], [])));

        Assert.Equal("I-IR-23", ex.Invariant);
        Assert.Contains("a COMPOSITE cannot be grouped by", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Grouping_by_a_list_is_refused_in_the_words_it_always_was()
    {
        var rowType = Row(F("tags", List(Str())));
        var ex = AssertInvalid(IrBuilder.Plan(HashAggregate(Read("symbols", rowType), [0], [])));

        Assert.Equal("I-IR-12", ex.Invariant);
        Assert.Contains(
            "a LIST cannot be grouped by; v1 lists are produced, projected and indexed into only",
            ex.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Joining_on_a_composite_is_refused()
    {
        var ex = AssertInvalid(IrBuilder.Plan(HashJoin(WithComposite(), WithComposite(), [1], [1])));

        Assert.Equal("I-IR-23", ex.Invariant);
        Assert.Contains("joined on", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Partitioning_a_window_by_a_composite_is_refused()
    {
        var input = WithComposite();
        var window = Window(
            input,
            [1],
            [],
            WholePartitionFrame(),
            [("n", WinAgg(AggregateFunctionId.Count, I64()))]);

        var ex = AssertInvalid(IrBuilder.Plan(window));

        Assert.Equal("I-IR-23", ex.Invariant);
        Assert.Contains("partitioned by", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Comparing_two_composites_is_refused()
    {
        var input = WithComposite();
        var condition = Call(FunctionId.Eq, Bool(), Ref(input.RowType, 1), Ref(input.RowType, 1));

        var ex = AssertInvalid(IrBuilder.Plan(Filter(input, condition)));

        Assert.Equal("I-IR-23", ex.Invariant);
        Assert.Contains("a COMPOSITE cannot be compared", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_composite_is_never_an_arithmetic_operand()
    {
        var input = WithComposite();
        var sum = Call(FunctionId.Add, Classification, Ref(input.RowType, 1), Ref(input.RowType, 1));

        var ex = AssertInvalid(IrBuilder.Plan(Project(input, [("x", sum)])));

        Assert.Equal("I-IR-23", ex.Invariant);
        Assert.Contains("an arithmetic operand", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_null_test_over_a_composite_is_allowed()
    {
        var input = WithComposite();
        var plan = IrBuilder.Plan(Filter(input, Call(FunctionId.IsNull, Bool(), Ref(input.RowType, 1))));

        PlanValidator.Validate(plan);
    }

    [Fact]
    public void A_composite_is_never_an_in_value()
    {
        var input = WithComposite();
        var probe = In(Ref(input.RowType, 1), Bool(), Null(Classification));

        var ex = AssertInvalid(IrBuilder.Plan(Filter(input, probe)));

        Assert.Equal("I-IR-23", ex.Invariant);
        Assert.Contains("an IN value", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_composite_is_never_cast()
    {
        var input = WithComposite();

        var ex = AssertInvalid(IrBuilder.Plan(Project(input, [("x", Cast(Ref(input.RowType, 1), Str()))])));

        Assert.Equal("I-IR-23", ex.Invariant);
        Assert.Contains("a COMPOSITE cannot be cast", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_composite_is_never_a_case_result()
    {
        var input = WithComposite();
        var nullable = Classification.Clone();
        nullable.Nullable = true;
        var choice = Case(
            nullable,
            Null(nullable),
            (Call(FunctionId.IsNotNull, Bool(), Ref(input.RowType, 0)), Ref(input.RowType, 1)));

        var ex = AssertInvalid(IrBuilder.Plan(Project(input, [("x", choice)])));

        Assert.Equal("I-IR-23", ex.Invariant);
        Assert.Contains("a CASE result", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_composite_has_no_literal_not_even_a_null_one()
    {
        var nullable = Classification.Clone();
        nullable.Nullable = true;

        var ex = AssertInvalid(IrBuilder.Plan(Project(TransactionsRead(), [("x", Null(nullable))])));

        Assert.Equal("I-IR-23", ex.Invariant);
        Assert.Contains("a COMPOSITE cannot be a literal", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_composite_is_never_a_parameter_type()
    {
        var plan = IrBuilder.Plan(
            Project(TransactionsRead(), [("x", Param(0, Classification))]),
            parameterTypes: Classification);

        var ex = AssertInvalid(plan);

        Assert.Equal("I-IR-23", ex.Invariant);
        Assert.Contains("a parameter's type", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_composite_is_never_a_table_column()
    {
        var rowType = Row(F("id", I64()), F("c", Classification));

        var ex = AssertInvalid(IrBuilder.Plan(Read("transactions", rowType)));

        Assert.Equal("I-IR-23", ex.Invariant);
        Assert.Contains("a table column", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_composite_is_never_a_table_function_s_column()
    {
        var scan = new Rel
        {
            RowType = Row(F("c", Classification)),
            TableFunctionScan = new TableFunctionScan { Function = "main.series" },
        };

        var ex = AssertInvalid(IrBuilder.Plan(scan));

        Assert.Equal("I-IR-23", ex.Invariant);
        Assert.Contains("a table function's column", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_composite_is_never_a_built_in_aggregate_s_argument()
    {
        var input = WithComposite();
        var count = HashAggregate(
            input, [0], [("n", Agg(AggregateFunctionId.Count, I64(), Ref(input.RowType, 1)))]);

        var ex = AssertInvalid(IrBuilder.Plan(count));

        Assert.Equal("I-IR-23", ex.Invariant);
        Assert.Contains("a built-in aggregate's argument", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_user_aggregate_may_answer_a_composite()
    {
        var summary = Composite(F("total", Fp64()), F("n", I64()));
        var input = TransactionsRead();
        var grouped = HashAggregate(
            input, [0], [("s", UserAgg("main.summarize", summary, Ref(Transactions, 2)))]);
        var plan = IrBuilder.Plan(Project(
            grouped,
            [("total", FieldAccess(Ref(grouped.RowType, 1), 0)), ("n", FieldAccess(Ref(grouped.RowType, 1), 1))]));

        PlanValidator.Validate(plan);
    }

    [Fact]
    public void A_composite_is_never_a_built_in_window_function_s_argument()
    {
        var input = WithComposite();
        var window = Window(
            input,
            [0],
            [],
            WholePartitionFrame(),
            [("n", WinAgg(AggregateFunctionId.Count, I64(), Ref(input.RowType, 1)))]);

        var ex = AssertInvalid(IrBuilder.Plan(window));

        Assert.Equal("I-IR-23", ex.Invariant);
        Assert.Contains("a built-in window function's argument", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_set_operation_that_compares_rows_refuses_a_composite_column()
    {
        var ex = AssertInvalid(IrBuilder.Plan(SetOp(SetOpKind.IntersectDistinct, WithComposite(), WithComposite())));

        Assert.Equal("I-IR-23", ex.Invariant);
        Assert.Contains("compared by INTERSECT", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_composite_is_never_a_function_s_argument()
    {
        var input = WithComposite();
        var call = UserCall("main.f", Str(), Ref(input.RowType, 1));

        var ex = AssertInvalid(IrBuilder.Plan(Project(input, [("x", call)])));

        Assert.Equal("I-IR-23", ex.Invariant);
        Assert.Contains("a function's argument", ex.Message, StringComparison.Ordinal);
    }
}

package chalk.planner.ir;

import chalk.ir.v1.Cast;
import chalk.ir.v1.CastFailure;
import chalk.ir.v1.DynamicParam;
import chalk.ir.v1.EnumArg;
import chalk.ir.v1.Expr;
import chalk.ir.v1.FieldRef;
import chalk.ir.v1.FunctionId;
import chalk.ir.v1.IfClause;
import chalk.ir.v1.IfThen;
import chalk.ir.v1.InList;
import chalk.ir.v1.ListValue;
import chalk.ir.v1.Literal;
import chalk.ir.v1.ScalarCall;
import chalk.ir.v1.Type;
import chalk.ir.v1.TypeKind;
import chalk.planner.UnsupportedFeatureException;
import chalk.planner.types.TypeMapper;
import java.util.ArrayList;
import java.util.List;
import org.apache.calcite.rex.RexBuilder;
import org.apache.calcite.rex.RexCall;
import org.apache.calcite.rex.RexCorrelVariable;
import org.apache.calcite.rex.RexDynamicParam;
import org.apache.calcite.rex.RexFieldAccess;
import org.apache.calcite.rex.RexInputRef;
import org.apache.calcite.rex.RexLiteral;
import org.apache.calcite.rex.RexLocalRef;
import org.apache.calcite.rex.RexNode;
import org.apache.calcite.rex.RexOver;
import org.apache.calcite.rex.RexPatternFieldRef;
import org.apache.calcite.rex.RexRangeRef;
import org.apache.calcite.rex.RexSubQuery;
import org.apache.calcite.rex.RexTableInputRef;
import org.apache.calcite.rex.RexUtil;
import org.apache.calcite.rex.RexVisitorImpl;
import org.apache.calcite.sql.SqlKind;
import org.apache.calcite.sql.type.SqlTypeName;

/**
 * {@code RexNode} → IR {@code Expr} (docs/design/03-planner.md §5.2).
 *
 * <p>Extends {@link RexVisitorImpl} rather than implementing {@code RexVisitor}: 1.41 added
 * {@code visitNodeAndFieldIndex} to the interface and 1.42 added lambdas, so the impl class is what
 * insulates this from the next addition (V9).
 */
public final class RexToIr extends RexVisitorImpl<Expr> {
  private final TypeMapper types;
  private final RexBuilder rexBuilder;

  /**
   * How deep inside a pushed subtree the conversion is (D84, step 22). A native user function is
   * refused everywhere but here: inside {@code RemoteQuery.pushed_plan} it names work the source
   * does, which is exactly where it belongs.
   */
  private int pushedDepth;

  public RexToIr(TypeMapper types, RexBuilder rexBuilder) {
    super(true);
    this.types = types;
    this.rexBuilder = rexBuilder;
  }

  /** Marks the start and end of a pushed subtree; see {@link #pushedDepth}. */
  public void enterPushed() {
    pushedDepth++;
  }

  public void exitPushed() {
    pushedDepth--;
  }

  /** Converts one expression. */
  public Expr convert(RexNode node) {
    Expr expr = node.accept(this);
    if (expr == null) {
      throw new UnsupportedFeatureException(
          "expression " + node, "The visitor produced nothing, which is a planner bug.");
    }
    return expr;
  }

  @Override
  public Expr visitInputRef(RexInputRef ref) {
    return Expr.newBuilder()
        .setType(types.toIr(ref.getType()))
        .setFieldRef(FieldRef.newBuilder().setIndex(ref.getIndex()))
        .build();
  }

  @Override
  public Expr visitLiteral(RexLiteral literal) {
    if (literal.getType().getSqlTypeName() == SqlTypeName.SYMBOL) {
      // A SYMBOL literal is a unit flag — EXTRACT(HOUR FROM …), FLOOR(… TO HOUR). It is not a value
      // and only ever appears as a direct argument of the functions that take one (I-IR-10).
      Object value = literal.getValue();
      return Expr.newBuilder()
          .setEnumArg(EnumArg.newBuilder().setValue(String.valueOf(value)))
          .build();
    }
    Type type = types.toIr(literal.getType());
    return Expr.newBuilder().setType(type).setLiteral(LiteralConverter.convert(literal, type)).build();
  }

  @Override
  public Expr visitDynamicParam(RexDynamicParam param) {
    DynamicParam.Builder builder = DynamicParam.newBuilder();
    if (param instanceof chalk.planner.entitlement.BoundParam bound) {
      // A named bound value rather than one of the statement's own (D209): the executor reads it
      // from the request's context, so the plan carries the name and not an index into a list this
      // parameter is not in.
      builder.setBoundKey(bound.key());
    } else {
      builder.setIndex(param.getIndex());
    }
    return Expr.newBuilder().setType(types.toIr(param.getType())).setParam(builder).build();
  }

  @Override
  public Expr visitCall(RexCall call) {
    Type type = types.toIr(call.getType());

    switch (call.getKind()) {
      case CAST, SAFE_CAST -> {
        return Expr.newBuilder()
            .setType(type)
            .setCast(
                Cast.newBuilder()
                    .setInput(convert(call.getOperands().get(0)))
                    .setOnFailure(
                        call.getKind() == SqlKind.SAFE_CAST
                            ? CastFailure.CAST_FAILURE_NULL
                            : CastFailure.CAST_FAILURE_ERROR))
            .build();
      }
      case CASE -> {
        return caseExpr(call, type);
      }
      case SEARCH -> {
        // Never emit SEARCH: expand the Sarg into comparisons the way RexUtil does, then let the
        // OR-folding below turn a set of points back into a single InList.
        return convert(RexUtil.expandSearch(rexBuilder, null, call));
      }
      case IN -> {
        return inList(call, type);
      }
      case OR -> {
        Expr folded = foldOrIntoInList(call, type);
        return folded != null ? folded : scalarCall(call, type);
      }
      case ARRAY_VALUE_CONSTRUCTOR -> {
        return arrayLiteral(call, type);
      }
      case ITEM -> {
        // `arr[i]` on a LIST. Calcite spells a MAP lookup and a ROW field access the same way; the
        // type mapper has already refused those, because neither operand type reaches the IR.
        return scalarCall(call, type);
      }
      // D291: a STRUCT is produced by a client-bodied function and by nothing else, so there is no
      // constructor to lower one from; a MAP is not a type the IR has at all.
      case ROW ->
          throw new UnsupportedFeatureException(
              "the ROW constructor",
              "A struct comes from a function: a client-bodied user function returns one, and SQL "
                  + "takes it apart with .field or carries it whole. Select the values as columns "
                  + "of their own (docs/design/51-structured-function-results.md §1).");
      case MAP_VALUE_CONSTRUCTOR ->
          throw new UnsupportedFeatureException(
              "constructor " + call.getKind(),
              "The IR's composite value types are LIST and STRUCT (docs/design/14-windows-ii.md §5).");
      default -> {
        if (chalk.planner.plan.ChalkKeySet.is(call)) {
          return keySet(call, type);
        }
        return scalarCall(call, type);
      }
    }
  }

  /**
   * A lookup join's key set (M5, D105): {@code column IN (<the keys this call carries>)}. The IR
   * spells it as an {@code InList} whose single option is a {@code KeySetParam}, so every consumer
   * that already understands an {@code IN} list — the validator, the printer, an IR-language source
   * — understands this too, and only the executor has to know that the option stands for a set.
   *
   * <p>A key set over several columns (F50) is a {@code KeySetMatch} instead: the value being
   * matched is a tuple and the IR has no tuple, so the node holds the columns — each with its own
   * type, which is also how the executor knows what to bind into each position of a key row.
   */
  private Expr keySet(RexCall call, Type type) {
    if (call.getOperands().size() > 1) {
      chalk.ir.v1.KeySetMatch.Builder match =
          chalk.ir.v1.KeySetMatch.newBuilder()
              .setKeySet(chalk.ir.v1.KeySetParam.newBuilder().setSlot(0));
      for (RexNode column : call.getOperands()) {
        match.addColumns(convert(column));
      }
      return Expr.newBuilder().setType(type).setKeySetMatch(match).build();
    }

    RexNode column = call.getOperands().get(0);
    Expr key = convert(column);
    return Expr.newBuilder()
        .setType(type)
        .setInList(
            chalk.ir.v1.InList.newBuilder()
                .setValue(key)
                .addOptions(
                    Expr.newBuilder()
                        .setType(key.getType())
                        .setKeySet(chalk.ir.v1.KeySetParam.newBuilder().setSlot(0))))
        .build();
  }

  /**
   * {@code ARRAY[…]} of constants becomes a {@code ListValue} literal (D58). A constructor over
   * anything else is refused: the IR has no node that builds a list per row, and quietly producing
   * one would be a wrong answer rather than a missing feature.
   */
  private Expr arrayLiteral(RexCall call, Type type) {
    if (type.getKind() != TypeKind.TYPE_KIND_LIST) {
      throw new UnsupportedFeatureException(
          "ARRAY constructor of " + call.getType(),
          "docs/design/14-windows-ii.md §5: v1 lists are one level deep and hold scalars.");
    }

    ListValue.Builder elements = ListValue.newBuilder();
    for (RexNode operand : call.getOperands()) {
      if (!(operand instanceof RexLiteral literal)) {
        throw new UnsupportedFeatureException(
            "ARRAY constructor over " + operand,
            "v1 builds a list only from constants (docs/design/14-windows-ii.md §5).");
      }

      elements.addElements(LiteralConverter.convert(literal, type.getElement()));
    }

    return Expr.newBuilder()
        .setType(type)
        .setLiteral(Literal.newBuilder().setListValue(elements))
        .build();
  }

  private Expr scalarCall(RexCall call, Type type) {
    chalk.planner.catalog.UserFunction declared =
        chalk.planner.plan.UserOperators.declarationOf(call.getOperator());
    if (declared != null) {
      return userCall(call, type, declared);
    }

    boolean temporalUnit =
        call.getOperands().stream()
            .anyMatch(operand -> operand.getType().getSqlTypeName() == SqlTypeName.SYMBOL);
    FunctionId function = FunctionMapping.scalar(call.getOperator(), temporalUnit);

    List<Expr> args = new ArrayList<>(call.getOperands().size());
    for (RexNode operand : call.getOperands()) {
      args.add(convert(operand));
    }
    args = OperandCoercion.harmonise(function, args);

    ScalarCall.Builder builder = ScalarCall.newBuilder().setFunction(function);
    builder.addAllArgs(args);
    // V52: harmonising the operands can leave the call's declared type narrower than they are, and
    // the executor takes its kernel's width from the call's type. Compute at the operands' width and
    // cast back to what the row type declares, which is exact.
    Expr computed =
        Expr.newBuilder()
            .setType(OperandCoercion.resultOf(function, type, args))
            .setCall(builder)
            .build();
    return OperandCoercion.narrowed(computed, type);
  }

  /**
   * A call to a function the catalog declared (D78). Only a client body can reach here: a SQL body
   * was inlined before validation and a native one is pushed into its own source, so a native call
   * that arrives is a plan that would have to evaluate it locally — refused with the message §2
   * names, because the source is the only thing that knows what the function means.
   */
  private Expr userCall(RexCall call, Type type, chalk.planner.catalog.UserFunction declared) {
    if (declared.isNative() && pushedDepth == 0) {
      throw new UnsupportedFeatureException(
          "native function "
              + declared.schemaName()
              + "."
              + declared.name()
              + " cannot be evaluated outside source "
              + declared.schemaName(),
          "A native function is implemented by its own source, so a plan that would evaluate it "
              + "anywhere else has nothing to call. This happens when an argument comes from another "
              + "source, or when the request turned pushdown off "
              + "(docs/design/17-user-defined-functions.md §2).");
    }
    if (declared.isSqlBodied()) {
      throw new UnsupportedFeatureException(
          "SQL-bodied function " + declared.qualifiedName() + " survived inlining",
          "A SQL body is substituted into the statement before validation; reaching the IR means "
              + "the inliner missed it, which is a planner bug.");
    }

    ScalarCall.Builder builder = ScalarCall.newBuilder().setUserFunction(declared.qualifiedName());
    for (RexNode operand : call.getOperands()) {
      builder.addArgs(convert(operand));
    }
    return Expr.newBuilder().setType(type).setCall(builder).build();
  }

  private Expr caseExpr(RexCall call, Type type) {
    List<RexNode> operands = call.getOperands();
    IfThen.Builder ifThen = IfThen.newBuilder();
    int i = 0;
    List<Expr> results = new ArrayList<>();
    List<RexNode> conditions = new ArrayList<>();
    for (; i + 1 < operands.size(); i += 2) {
      conditions.add(operands.get(i));
      results.add(convert(operands.get(i + 1)));
    }
    // Calcite's CASE always carries an ELSE as the odd trailing operand.
    Expr elseBranch =
        i < operands.size()
            ? convert(operands.get(i))
            : nullLiteral(type);
    results.add(elseBranch);

    List<Expr> harmonised = OperandCoercion.harmoniseTo(type, results);
    for (int c = 0; c < conditions.size(); c++) {
      ifThen.addClauses(
          IfClause.newBuilder()
              .setCondition(convert(conditions.get(c)))
              .setResult(harmonised.get(c)));
    }
    ifThen.setElseBranch(harmonised.get(harmonised.size() - 1));
    return Expr.newBuilder().setType(type).setIfThen(ifThen).build();
  }

  private Expr inList(RexCall call, Type type) {
    List<RexNode> operands = call.getOperands();
    Expr value = convert(operands.get(0));
    List<Expr> options = new ArrayList<>(operands.size() - 1);
    for (int i = 1; i < operands.size(); i++) {
      Expr option = convert(operands.get(i));
      if (option.getKindCase() != Expr.KindCase.LITERAL
          && option.getKindCase() != Expr.KindCase.PARAM) {
        throw new UnsupportedFeatureException(
            "IN with a computed option",
            "The IR carries only constant IN lists; the planner rewrites the rest to OR.");
      }
      options.add(option);
    }
    List<Expr> harmonised = OperandCoercion.harmoniseTo(value.getType(), options);
    return Expr.newBuilder()
        .setType(type)
        .setInList(InList.newBuilder().setValue(value).addAllOptions(harmonised))
        .build();
  }

  /**
   * {@code OR(x = c1, x = c2, …)} with one shared left operand and constant right operands becomes a
   * single {@code InList}, which the executor evaluates as one hash probe per row rather than n
   * comparisons (D29). Returns null when the shape does not match.
   */
  private Expr foldOrIntoInList(RexCall call, Type type) {
    if (call.getOperands().size() < 2) {
      return null;
    }
    RexNode shared = null;
    List<Expr> options = new ArrayList<>(call.getOperands().size());
    for (RexNode operand : call.getOperands()) {
      if (!(operand instanceof RexCall eq) || eq.getKind() != SqlKind.EQUALS) {
        return null;
      }
      RexNode left = eq.getOperands().get(0);
      RexNode right = eq.getOperands().get(1);
      if (!(right instanceof RexLiteral) && !(right instanceof RexDynamicParam)) {
        return null;
      }
      // Compare by digest, not identity: Calcite rebuilds equivalent RexNodes freely.
      if (shared == null) {
        shared = left;
      } else if (!shared.equals(left)) {
        return null;
      }
      options.add(convert(right));
    }
    Expr value = convert(shared);
    List<Expr> harmonised = OperandCoercion.harmoniseTo(value.getType(), options);
    return Expr.newBuilder()
        .setType(type)
        .setInList(InList.newBuilder().setValue(value).addAllOptions(harmonised))
        .build();
  }

  private static Expr nullLiteral(Type type) {
    return Expr.newBuilder()
        .setType(type.toBuilder().setNullable(true))
        .setLiteral(chalk.ir.v1.Literal.newBuilder().setIsNull(true))
        .build();
  }

  // ---- everything below should not survive the Hep pre-pass in M1 ----

  @Override
  public Expr visitLocalRef(RexLocalRef ref) {
    throw unsupported("RexLocalRef", "Chalk never builds RexPrograms.");
  }

  @Override
  public Expr visitOver(RexOver over) {
    throw unsupported("window function " + over.getAggOperator().getName(), "Window functions are M2+.");
  }

  @Override
  public Expr visitCorrelVariable(RexCorrelVariable variable) {
    throw unsupported("correlation variable", "Correlated subqueries are not supported in M1.");
  }

  @Override
  public Expr visitRangeRef(RexRangeRef ref) {
    throw unsupported("RexRangeRef", "The IR addresses fields individually.");
  }

  /**
   * A field of a STRUCT (D291): a {@code FieldAccess} by position over the struct's expression — a
   * user call, a column that carries one, or another field access's input. Typed by the IR's rule,
   * which is the field's own type made nullable when the struct is: Calcite's builder types it so,
   * and where a rewrite typed one narrower, the IR's rule wins (I-IR-22).
   *
   * <p>A correlation variable's field — {@code $cor0.symbol} — is a correlated reference that survived
   * decorrelation, which is refused as it always was: Chalk plans a correlated sub-query only as a
   * join (D67).
   */
  @Override
  public Expr visitFieldAccess(RexFieldAccess access) {
    RexNode reference = access.getReferenceExpr();
    if (reference instanceof RexCorrelVariable) {
      throw unsupported(
          "field access of a correlation variable",
          "A correlated reference survived decorrelation; Chalk plans a correlated sub-query only "
              + "as a join (docs/design/14-windows-ii.md §8).");
    }

    Expr input = convert(reference);
    Type composite = input.getType();
    if (composite.getKind() != TypeKind.TYPE_KIND_STRUCT) {
      throw unsupported(
          "field access over " + reference.getType(),
          "A field is read from a STRUCT, which only a client-bodied user function produces "
              + "(docs/design/51-structured-function-results.md §1).");
    }

    int index = access.getField().getIndex();
    if (index < 0 || index >= composite.getFieldsCount()) {
      throw unsupported(
          "field " + access.getField().getName() + " of " + reference.getType(),
          "The field is not one of the struct's own, which is a planner bug.");
    }

    Type field = composite.getFields(index).getType();
    Type type = composite.getNullable() ? field.toBuilder().setNullable(true).build() : field;
    return Expr.newBuilder()
        .setType(type)
        .setFieldAccess(
            chalk.ir.v1.FieldAccess.newBuilder().setInput(input).setIndex(index))
        .build();
  }

  @Override
  public Expr visitSubQuery(RexSubQuery subQuery) {
    throw unsupported("subquery", "Subqueries are not supported in M1.");
  }

  @Override
  public Expr visitTableInputRef(RexTableInputRef ref) {
    throw unsupported("RexTableInputRef", "It only appears in materialised-view matching.");
  }

  @Override
  public Expr visitPatternFieldRef(RexPatternFieldRef ref) {
    throw unsupported("MATCH_RECOGNIZE pattern reference", "MATCH_RECOGNIZE is not supported.");
  }

  private static UnsupportedFeatureException unsupported(String feature, String detail) {
    return new UnsupportedFeatureException(feature, detail);
  }

  /** The IR type kind of an expression, for the coercion pass. */
  static TypeKind kindOf(Expr expr) {
    return expr.hasType() ? expr.getType().getKind() : TypeKind.TYPE_KIND_UNSPECIFIED;
  }
}

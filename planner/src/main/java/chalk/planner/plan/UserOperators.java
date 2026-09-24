package chalk.planner.plan;

import chalk.ir.v1.Monotonicity;
import chalk.ir.v1.Parameter;
import chalk.ir.v1.Volatility;
import chalk.planner.catalog.UserFunction;
import chalk.planner.types.TypeMapper;
import com.google.common.collect.ImmutableList;
import java.lang.reflect.Type;
import java.util.ArrayList;
import java.util.List;
import java.util.function.Function;
import java.util.function.IntFunction;
import java.util.function.Predicate;
import org.apache.calcite.rel.type.RelDataType;
import org.apache.calcite.rel.type.RelDataTypeFactory;
import org.apache.calcite.schema.FunctionParameter;
import org.apache.calcite.schema.TableMacro;
import org.apache.calcite.schema.TranslatableTable;
import org.apache.calcite.sql.SqlCall;
import org.apache.calcite.sql.SqlIdentifier;
import org.apache.calcite.sql.SqlKind;
import org.apache.calcite.sql.SqlNode;
import org.apache.calcite.sql.SqlOperatorBinding;
import org.apache.calcite.sql.SqlWriter;
import org.apache.calcite.sql.parser.SqlParserPos;
import org.apache.calcite.sql.type.OperandTypes;
import org.apache.calcite.sql.type.ReturnTypes;
import org.apache.calcite.sql.type.SqlOperandMetadata;
import org.apache.calcite.sql.type.SqlReturnTypeInference;
import org.apache.calcite.sql.type.SqlTypeFamily;
import org.apache.calcite.sql.type.SqlTypeTransforms;
import org.apache.calcite.sql.validate.SqlMonotonicity;
import org.apache.calcite.sql.validate.SqlUserDefinedAggFunction;
import org.apache.calcite.sql.validate.SqlUserDefinedFunction;
import org.apache.calcite.sql.validate.SqlUserDefinedTableFunction;
import org.apache.calcite.sql.validate.SqlUserDefinedTableMacro;
import org.apache.calcite.util.Optionality;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * The Calcite operators a {@link UserFunction} becomes (docs/design/17-user-defined-functions.md §2,
 * D78). One class per Calcite shape, each of them holding its declaration so that every later stage
 * — inlining, pushdown, IR emission, the cost model — can ask an operator what it is rather than
 * looking the name up again.
 *
 * <p>Three facts about Calcite 1.42 shape this file and are recorded in ADR 0021. Calcite's own
 * classes take {@code requiresOrder} and {@code requiresOver} as constructor arguments but expose
 * {@code allowsFraming} and {@code allowsNullTreatment} only as overridable methods, so two of the
 * four aggregate flags are set by overriding. {@code SqlOperandMetadata} — built by
 * {@code OperandTypes.operandMetadata} — is what carries parameter names and optionality, and is
 * therefore what makes {@code f(x => 1.0)} and a call that omits an optional parameter resolve.
 * And a {@code SqlUserDefinedFunction} requires a {@code org.apache.calcite.schema.Function}, whose
 * only job here is to answer with the declared types: nothing in Chalk ever compiles it to Java.
 */
public final class UserOperators {
  private UserOperators() {}

  /** The operator a declaration becomes, by kind and implementation. */
  public static org.apache.calcite.sql.SqlOperator create(UserFunction declaration) {
    return switch (declaration.kind()) {
      case FUNCTION_KIND_AGGREGATE -> new ChalkUserAggFunction(declaration);
      case FUNCTION_KIND_TABLE ->
          declaration.isSqlBodied()
              ? new ChalkUserTableMacro(declaration)
              : new ChalkUserTableFunction(declaration);
      default -> new ChalkUserFunction(declaration);
    };
  }

  /** The declaration behind an operator, or null when it is not one of Chalk's user functions. */
  public static @Nullable UserFunction declarationOf(org.apache.calcite.sql.SqlOperator operator) {
    if (operator instanceof ChalkUserFunction f) {
      return f.declaration();
    }
    if (operator instanceof ChalkUserAggFunction f) {
      return f.declaration();
    }
    if (operator instanceof ChalkUserTableFunction f) {
      return f.declaration();
    }
    if (operator instanceof ChalkUserTableMacro f) {
      return f.declaration();
    }
    return null;
  }

  // ---- the operators ----

  /** A scalar user function: SQL-bodied (inlined later), client-bodied, or source-native. */
  public static final class ChalkUserFunction extends SqlUserDefinedFunction {
    private final UserFunction declaration;

    ChalkUserFunction(UserFunction declaration) {
      super(
          identifier(declaration),
          SqlKind.OTHER_FUNCTION,
          returnType(declaration),
          operandTypes(declaration),
          metadata(declaration),
          new DeclaredFunction(declaration));
      this.declaration = declaration;
    }

    public UserFunction declaration() {
      return declaration;
    }

    @Override
    public boolean isDeterministic() {
      return declaration.volatility() != Volatility.VOLATILITY_VOLATILE;
    }

    @Override
    public boolean isDynamicFunction() {
      return declaration.volatility() != Volatility.VOLATILITY_IMMUTABLE;
    }

    @Override
    public SqlMonotonicity getMonotonicity(SqlOperatorBinding call) {
      return monotonicityOf(declaration, call);
    }

    @Override
    public void unparse(SqlWriter writer, SqlCall call, int leftPrec, int rightPrec) {
      if (!declaration.isNative()) {
        super.unparse(writer, call, leftPrec, rightPrec);
        return;
      }
      unparseNative(declaration, writer, call);
    }
  }

  /** A user aggregate. The three Calcite flags come from the descriptor; see the class comment. */
  public static final class ChalkUserAggFunction extends SqlUserDefinedAggFunction {
    private final UserFunction declaration;

    ChalkUserAggFunction(UserFunction declaration) {
      super(
          identifier(declaration),
          SqlKind.OTHER_FUNCTION,
          returnType(declaration),
          operandTypes(declaration),
          metadata(declaration),
          new DeclaredAggregateFunction(declaration),
          /* requiresOrder= */ false,
          /* requiresOver= */ false,
          declaration.descriptor().getOrdered() ? Optionality.MANDATORY : Optionality.FORBIDDEN);
      this.declaration = declaration;
    }

    public UserFunction declaration() {
      return declaration;
    }

    @Override
    public boolean allowsFraming() {
      return declaration.descriptor().getWindow();
    }

    @Override
    public boolean allowsNullTreatment() {
      return declaration.descriptor().getNullTreatment();
    }

    @Override
    public boolean isDeterministic() {
      return declaration.volatility() != Volatility.VOLATILITY_VOLATILE;
    }

    @Override
    public void unparse(SqlWriter writer, SqlCall call, int leftPrec, int rightPrec) {
      if (!declaration.isNative()) {
        super.unparse(writer, call, leftPrec, rightPrec);
        return;
      }
      unparseNative(declaration, writer, call);
    }
  }

  /**
   * A client-bodied table function: {@code TABLE(f(args))} becomes a {@code LogicalTableFunctionScan}
   * that {@code ChalkTableFunctionScanRule} turns into the IR's {@code TableFunctionScan}.
   */
  public static final class ChalkUserTableFunction extends SqlUserDefinedTableFunction {
    private final UserFunction declaration;

    ChalkUserTableFunction(UserFunction declaration) {
      super(
          identifier(declaration),
          SqlKind.OTHER_FUNCTION,
          ReturnTypes.CURSOR,
          operandTypes(declaration),
          metadata(declaration),
          new DeclaredTableFunction(declaration));
      this.declaration = declaration;
    }

    public UserFunction declaration() {
      return declaration;
    }

    @Override
    public boolean isDeterministic() {
      return declaration.volatility() != Volatility.VOLATILITY_VOLATILE;
    }
  }

  /**
   * A SQL-bodied table function: a Calcite macro, expanded at planning time with its arguments bound,
   * so the call is gone by the time a plan exists. Calcite evaluates a macro's operands to Java
   * values, which is exactly why v1 requires constants (V31).
   */
  public static final class ChalkUserTableMacro extends SqlUserDefinedTableMacro {
    private final UserFunction declaration;

    ChalkUserTableMacro(UserFunction declaration) {
      super(
          identifier(declaration),
          SqlKind.OTHER_FUNCTION,
          ReturnTypes.CURSOR,
          operandTypes(declaration),
          metadata(declaration),
          new DeclaredTableMacro(declaration));
      this.declaration = declaration;
    }

    public UserFunction declaration() {
      return declaration;
    }

    /**
     * The expansion, bound to this call's own operands. Calcite calls this while validating the
     * {@code TABLE(...)} clause and again while converting it, both times with a
     * {@code SqlCallBinding} — which is what gives access to the arguments as written, named
     * arguments permuted and omitted optional ones filled in.
     */
    @Override
    public TranslatableTable getTable(SqlOperatorBinding callBinding) {
      if (callBinding instanceof org.apache.calcite.sql.SqlCallBinding sql) {
        return chalk.planner.ir.SqlBodyInliner.macroTable(declaration, sql.operands());
      }
      throw new chalk.planner.UnsupportedFeatureException(
          "table function " + declaration.qualifiedName() + " called from " + callBinding.getClass(),
          "A SQL-bodied table function is expanded from the syntax of its call "
              + "(docs/design/17-user-defined-functions.md §2, V31).");
    }
  }

  // ---- the pieces they share ----

  private static SqlIdentifier identifier(UserFunction declaration) {
    return new SqlIdentifier(
        ImmutableList.of(declaration.schemaName(), declaration.name()), SqlParserPos.ZERO);
  }

  /**
   * The declared return type, made nullable when {@code strict} and any argument is — which is
   * exactly what {@code RETURNS NULL ON NULL INPUT} means, and what {@code TO_NULLABLE} computes.
   *
   * <p>A COMPOSITE result is widened as a whole (D291): the composite becomes nullable and its fields keep
   * the nullability the record declared. {@code TO_NULLABLE} would widen every field along with it,
   * because Calcite makes a record nullable by copying it with nullable fields.
   */
  private static SqlReturnTypeInference returnType(UserFunction declaration) {
    SqlReturnTypeInference declared =
        binding ->
            new TypeMapper(binding.getTypeFactory())
                .toCalcite(declaration.descriptor().getReturnType());
    if (!declaration.strict()) {
      return declared;
    }
    return declaration.descriptor().getReturnType().getKind() == chalk.ir.v1.TypeKind.TYPE_KIND_COMPOSITE
        ? ReturnTypes.cascade(declared, STRICT_COMPOSITE)
        : ReturnTypes.cascade(declared, SqlTypeTransforms.TO_NULLABLE);
  }

  /** {@code TO_NULLABLE} for a composite value: nullable as a whole when any argument is, fields as declared. */
  private static final org.apache.calcite.sql.type.SqlTypeTransform STRICT_COMPOSITE =
      (binding, type) ->
          org.apache.calcite.sql.type.SqlTypeUtil.containsNullable(binding.collectOperandTypes())
              ? binding.getTypeFactory().enforceTypeWithNullability(type, true)
              : type;

  /**
   * The declared parameter types, told to the validator. Without this an untyped operand — a bare
   * {@code NULL}, a dynamic parameter — is typed {@code ANY}, which the IR has no vocabulary for.
   */
  private static org.apache.calcite.sql.type.SqlOperandTypeInference operandTypes(
      UserFunction declaration) {
    return (callBinding, returnType, operandTypes) -> {
      TypeMapper mapper = new TypeMapper(callBinding.getTypeFactory());
      List<Parameter> parameters = declaration.parameters();
      for (int i = 0; i < operandTypes.length; i++) {
        operandTypes[i] =
            i < parameters.size()
                ? mapper.toCalcite(parameters.get(i).getType())
                : callBinding.getTypeFactory().createSqlType(
                    org.apache.calcite.sql.type.SqlTypeName.ANY);
      }
    };
  }

  /** Parameter names, types and optionality — what makes named and defaulted calls resolve. */
  private static SqlOperandMetadata metadata(UserFunction declaration) {
    List<Parameter> parameters = declaration.parameters();
    List<SqlTypeFamily> families = new ArrayList<>(parameters.size());
    List<String> names = new ArrayList<>(parameters.size());
    List<Boolean> optional = new ArrayList<>(parameters.size());
    for (Parameter parameter : parameters) {
      families.add(familyOf(parameter));
      names.add(parameter.getName());
      optional.add(parameter.getOptional());
    }

    Function<RelDataTypeFactory, List<RelDataType>> types =
        typeFactory -> {
          TypeMapper mapper = new TypeMapper(typeFactory);
          List<RelDataType> resolved = new ArrayList<>(parameters.size());
          for (Parameter parameter : parameters) {
            resolved.add(mapper.toCalcite(parameter.getType()));
          }
          return resolved;
        };
    IntFunction<String> nameOf = names::get;
    Predicate<Integer> isOptional = index -> optional.get(index);
    return OperandTypes.operandMetadata(families, types, nameOf, isOptional);
  }

  /**
   * The family a declared parameter type belongs to, which is what the validator matches a call's
   * arguments against. Deliberately the family and not the exact type: {@code bucket_price(close, 5)}
   * should resolve and have its {@code 5} coerced, exactly as a built-in's would be.
   */
  private static SqlTypeFamily familyOf(Parameter parameter) {
    return switch (parameter.getType().getKind()) {
      case TYPE_KIND_BOOL -> SqlTypeFamily.BOOLEAN;
      case TYPE_KIND_I8, TYPE_KIND_I16, TYPE_KIND_I32, TYPE_KIND_I64 -> SqlTypeFamily.INTEGER;
      case TYPE_KIND_FP32, TYPE_KIND_FP64, TYPE_KIND_DECIMAL -> SqlTypeFamily.NUMERIC;
      case TYPE_KIND_STRING -> SqlTypeFamily.CHARACTER;
      case TYPE_KIND_BINARY -> SqlTypeFamily.BINARY;
      case TYPE_KIND_DATE -> SqlTypeFamily.DATE;
      case TYPE_KIND_TIME -> SqlTypeFamily.TIME;
      case TYPE_KIND_TIMESTAMP, TYPE_KIND_TIMESTAMP_TZ -> SqlTypeFamily.TIMESTAMP;
      case TYPE_KIND_INTERVAL_DAY, TYPE_KIND_INTERVAL_YEAR -> SqlTypeFamily.DATETIME_INTERVAL;
      default -> SqlTypeFamily.ANY;
    };
  }

  /**
   * The declared monotonicity, combined with what the argument itself does: a function that is
   * increasing in a parameter carries that parameter's ordering through, and reverses it when the
   * declaration says decreasing. Anything else is not monotone, which is the safe answer.
   */
  private static SqlMonotonicity monotonicityOf(UserFunction declaration, SqlOperatorBinding call) {
    SqlMonotonicity result = SqlMonotonicity.CONSTANT;
    boolean any = false;
    for (int i = 0; i < call.getOperandCount(); i++) {
      Monotonicity declared = declaration.monotonicityOf(i);
      SqlMonotonicity operand = call.getOperandMonotonicity(i);
      if (operand == SqlMonotonicity.CONSTANT) {
        continue;
      }
      if (declared == Monotonicity.MONOTONICITY_NONE || any) {
        // A second moving argument, or one the declaration says nothing about.
        return SqlMonotonicity.NOT_MONOTONIC;
      }
      any = true;
      result = combine(declared, operand);
      if (result == SqlMonotonicity.NOT_MONOTONIC) {
        return result;
      }
    }
    return result;
  }

  private static SqlMonotonicity combine(Monotonicity declared, SqlMonotonicity operand) {
    boolean strictlyDeclared =
        declared == Monotonicity.MONOTONICITY_STRICTLY_INCREASING
            || declared == Monotonicity.MONOTONICITY_STRICTLY_DECREASING;
    boolean reversing =
        declared == Monotonicity.MONOTONICITY_DECREASING
            || declared == Monotonicity.MONOTONICITY_STRICTLY_DECREASING;

    SqlMonotonicity effective =
        switch (operand) {
          case INCREASING, STRICTLY_INCREASING -> reversing
              ? SqlMonotonicity.DECREASING
              : SqlMonotonicity.INCREASING;
          case DECREASING, STRICTLY_DECREASING -> reversing
              ? SqlMonotonicity.INCREASING
              : SqlMonotonicity.DECREASING;
          default -> SqlMonotonicity.NOT_MONOTONIC;
        };

    // Strictness survives only when both halves are strict; an ordering does not need it, and
    // claiming it where it does not hold would let a DISTINCT be removed that must not be.
    boolean strictOperand =
        operand == SqlMonotonicity.STRICTLY_INCREASING
            || operand == SqlMonotonicity.STRICTLY_DECREASING;
    if (!strictlyDeclared || !strictOperand) {
      return effective;
    }
    return effective == SqlMonotonicity.INCREASING
        ? SqlMonotonicity.STRICTLY_INCREASING
        : SqlMonotonicity.STRICTLY_DECREASING;
  }

  /**
   * A native call spells itself the way its source does, and never carries the schema prefix.
   *
   * <p>Written with {@code print} rather than {@code startFunCall}, because the latter treats the
   * name as a keyword and upper-cases it: the descriptor's {@code dialect_name} is the source's own
   * spelling, and a source that cares about case would get a name it does not have.
   */
  private static void unparseNative(UserFunction declaration, SqlWriter writer, SqlCall call) {
    writer.print(declaration.dialectName());
    writer.setNeedWhitespace(false);
    SqlWriter.Frame frame = writer.startList(SqlWriter.FrameTypeEnum.FUN_CALL, "(", ")");
    for (SqlNode operand : call.getOperandList()) {
      writer.sep(",");
      operand.unparse(writer, 0, 0);
    }
    writer.endList(frame);
  }

  private static List<FunctionParameter> parametersOf(UserFunction declaration) {
    List<Parameter> parameters = declaration.parameters();
    List<FunctionParameter> resolved = new ArrayList<>(parameters.size());
    for (int i = 0; i < parameters.size(); i++) {
      Parameter parameter = parameters.get(i);
      int ordinal = i;
      resolved.add(
          new FunctionParameter() {
            @Override
            public int getOrdinal() {
              return ordinal;
            }

            @Override
            public String getName() {
              return parameter.getName();
            }

            @Override
            public RelDataType getType(RelDataTypeFactory typeFactory) {
              return new TypeMapper(typeFactory).toCalcite(parameter.getType());
            }

            @Override
            public boolean isOptional() {
              return parameter.getOptional();
            }
          });
    }
    return ImmutableList.copyOf(resolved);
  }

  /**
   * The {@code org.apache.calcite.schema.Function} every {@code SqlUserDefined*} needs. It answers
   * with the declared types and nothing else: Chalk never asks Calcite to <em>run</em> a user
   * function, so there is no implementor and no Java method behind one.
   */
  private static class DeclaredFunction implements org.apache.calcite.schema.ScalarFunction {
    final UserFunction declaration;

    DeclaredFunction(UserFunction declaration) {
      this.declaration = declaration;
    }

    @Override
    public RelDataType getReturnType(RelDataTypeFactory typeFactory) {
      return new TypeMapper(typeFactory).toCalcite(declaration.descriptor().getReturnType());
    }

    @Override
    public List<FunctionParameter> getParameters() {
      return parametersOf(declaration);
    }
  }

  private static final class DeclaredAggregateFunction
      implements org.apache.calcite.schema.AggregateFunction {
    private final UserFunction declaration;

    DeclaredAggregateFunction(UserFunction declaration) {
      this.declaration = declaration;
    }

    @Override
    public RelDataType getReturnType(RelDataTypeFactory typeFactory) {
      return new TypeMapper(typeFactory).toCalcite(declaration.descriptor().getReturnType());
    }

    @Override
    public List<FunctionParameter> getParameters() {
      return parametersOf(declaration);
    }
  }

  private static final class DeclaredTableFunction
      implements org.apache.calcite.schema.TableFunction {
    private final UserFunction declaration;

    DeclaredTableFunction(UserFunction declaration) {
      this.declaration = declaration;
    }

    @Override
    public RelDataType getRowType(
        RelDataTypeFactory typeFactory, List<? extends @Nullable Object> arguments) {
      return new TypeMapper(typeFactory).toCalciteRowType(declaration.descriptor().getReturnsTable());
    }

    @Override
    public Type getElementType(List<? extends @Nullable Object> arguments) {
      return Object[].class;
    }

    @Override
    public List<FunctionParameter> getParameters() {
      return parametersOf(declaration);
    }
  }

  /**
   * The macro half. {@link #apply} is where a SQL-bodied table function becomes an ordinary
   * sub-query; the table it returns is built by {@code SqlBodyInliner}, which owns the expansion.
   */
  private static final class DeclaredTableMacro implements TableMacro {
    private final UserFunction declaration;

    DeclaredTableMacro(UserFunction declaration) {
      this.declaration = declaration;
    }

    /**
     * Unreachable: {@link ChalkUserTableMacro#getTable} binds the call's own {@code SqlNode}s
     * instead, so that a literal keeps the type and spelling the query gave it rather than being
     * rebuilt from a Java value Calcite converted it to.
     */
    @Override
    public TranslatableTable apply(List<? extends @Nullable Object> arguments) {
      throw new UnsupportedOperationException(
          "Chalk expands a SQL-bodied table function from its call's syntax, not from evaluated "
              + "arguments; see ChalkUserTableMacro.getTable.");
    }

    @Override
    public List<FunctionParameter> getParameters() {
      return parametersOf(declaration);
    }
  }
}

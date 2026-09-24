package chalk.planner.ir;

import chalk.ir.v1.Parameter;
import chalk.planner.UnsupportedFeatureException;
import chalk.planner.catalog.UserFunction;
import chalk.planner.catalog.UserFunctions;
import chalk.planner.types.ChalkTypeSystem;
import chalk.planner.types.TypeMapper;
import com.google.common.collect.ImmutableList;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Set;
import org.apache.calcite.plan.RelOptTable;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.type.RelDataType;
import org.apache.calcite.rel.type.RelDataTypeFactory;
import org.apache.calcite.schema.Schema;
import org.apache.calcite.schema.Statistic;
import org.apache.calcite.schema.Statistics;
import org.apache.calcite.schema.TranslatableTable;
import org.apache.calcite.schema.impl.AbstractTable;
import org.apache.calcite.sql.SqlBasicCall;
import org.apache.calcite.sql.SqlCall;
import org.apache.calcite.sql.SqlDataTypeSpec;
import org.apache.calcite.sql.SqlDynamicParam;
import org.apache.calcite.sql.SqlIdentifier;
import org.apache.calcite.sql.SqlKind;
import org.apache.calcite.sql.SqlLiteral;
import org.apache.calcite.sql.SqlNode;
import org.apache.calcite.sql.dialect.CalciteSqlDialect;
import org.apache.calcite.sql.fun.SqlStdOperatorTable;
import org.apache.calcite.sql.parser.SqlParseException;
import org.apache.calcite.sql.parser.SqlParser;
import org.apache.calcite.sql.parser.SqlParserPos;
import org.apache.calcite.sql.type.SqlTypeFactoryImpl;
import org.apache.calcite.sql.type.SqlTypeUtil;
import org.apache.calcite.sql.util.SqlShuttle;
import org.apache.calcite.util.Util;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * SQL bodies, inlined (D78, docs/design/17-user-defined-functions.md §2).
 *
 * <p>A scalar or aggregate body is substituted into the statement's own parse tree before
 * validation: each parameter reference becomes {@code CAST(argument AS declared-type)}, so the
 * declared signature stays authoritative, and a {@code STRICT} function gains the NULL guard its
 * declaration promises. The call is gone by the time there is a relational tree at all — which is
 * the property §2 asks for, since it is what makes an inlined body foldable when immutable and
 * pushable wherever its constituents are. An aggregate body written over built-in aggregates
 * (<code>SUM(x * w) / SUM(w)</code>) becomes exactly those aggregates plus a projection, because
 * that is what the validator makes of the substituted text.
 *
 * <p>A table body is a Calcite {@link org.apache.calcite.schema.TableMacro} instead, as §2 says: the
 * macro's arguments are bound at planning time and the body expands into an ordinary sub-query.
 * Calcite evaluates a macro's operands while validating, which is why v1 requires constants and why
 * a dynamic parameter is {@code UNSUPPORTED} naming the constraint (V31).
 */
public final class SqlBodyInliner extends SqlShuttle {
  /**
   * How many rounds of substitution a statement gets. A body may call another SQL-bodied function,
   * so one pass is not enough; a body that (transitively) calls itself would not terminate, and this
   * is what turns that into a message instead of a hang.
   */
  private static final int MAX_ROUNDS = 16;

  /**
   * A type factory used for one thing: turning a declared parameter type into the {@code CAST}'s
   * syntactic type spec. A spec is a parse-tree node and carries no type identity, so it does not
   * matter that this factory is not the request's.
   */
  private static final RelDataTypeFactory SPEC_TYPES =
      new SqlTypeFactoryImpl(ChalkTypeSystem.INSTANCE);

  private final UserFunctions functions;

  /**
   * The calls this pass has already normalised, by identity. Normalisation is not idempotent — it
   * wraps every argument in a cast — so a second round must leave a call it has already rewritten
   * exactly as it is.
   */
  private final java.util.Set<SqlCall> normalised;

  private boolean substituted;

  private SqlBodyInliner(UserFunctions functions, java.util.Set<SqlCall> normalised) {
    this.functions = functions;
    this.normalised = normalised;
  }

  /**
   * Inlines every SQL-bodied scalar and aggregate call in {@code statement}. Returns the statement
   * unchanged — the same object — when the catalog declares none, which is every catalog before
   * step 22 and most catalogs after it.
   */
  public static SqlNode inline(SqlNode statement, UserFunctions functions) {
    if (functions.isEmpty()) {
      return statement;
    }

    java.util.Set<SqlCall> normalised =
        java.util.Collections.newSetFromMap(new java.util.IdentityHashMap<>());
    SqlNode current = statement;
    for (int round = 0; round < MAX_ROUNDS; round++) {
      SqlBodyInliner inliner = new SqlBodyInliner(functions, normalised);
      SqlNode next = current.accept(inliner);
      if (!inliner.substituted) {
        return current;
      }
      current = next == null ? current : next;
    }

    throw new UnsupportedFeatureException(
        "a SQL function body that expands without end",
        "A SQL-bodied function called itself, directly or through another one; Chalk inlines a body "
            + "into its call site and so cannot express recursion "
            + "(docs/design/17-user-defined-functions.md §2).");
  }

  @Override
  public @Nullable SqlNode visit(SqlCall call) {
    SqlNode rewritten = super.visit(call);
    if (!(rewritten instanceof SqlCall rewrittenCall)) {
      return rewritten;
    }

    UserFunction declaration = named(rewrittenCall);
    if (declaration == null || normalised.contains(rewrittenCall)) {
      return rewrittenCall;
    }

    // Every user-function call is normalised here, whatever its body: named arguments permuted into
    // declaration order and omitted optional ones replaced by the constant they stand for. Calcite
    // can do both itself, but only for an operator it has already resolved — and it types the
    // `DEFAULT` placeholder it leaves behind as ANY, which the IR has no vocabulary for. Doing it
    // once, here, means every kind of body sees the same shape.
    // Every argument is cast to its declared type, whatever the body is. That is what makes the
    // declaration authoritative rather than the call site: `bucket_price("close", 5.0)` writes a
    // DECIMAL(2,1) literal, and a delegate declared over DOUBLE must see a DOUBLE. A cast between
    // equal types disappears in RexBuilder.makeCast, so nothing is left in a plan whose arguments
    // already match.
    List<SqlNode> arguments = new ArrayList<>(arguments(declaration, rewrittenCall));
    for (int i = 0; i < arguments.size(); i++) {
      arguments.set(i, cast(arguments.get(i), declaration.parameters().get(i)));
    }

    if (declaration.isSqlBodied()
        && declaration.kind() == chalk.ir.v1.FunctionKind.FUNCTION_KIND_TABLE) {
      // V31, checked here rather than in the macro: Calcite refuses a dynamic parameter in a
      // collection table with "Illegal use of dynamic parameter" before it ever asks the macro to
      // expand, and that message says nothing about why. This one does.
      for (int i = 0; i < arguments.size(); i++) {
        requireConstant(declaration, declaration.parameters().get(i), arguments.get(i));
      }
    }

    if (sqlBodied(rewrittenCall) != null) {
      substituted = true;
      return substitute(declaration, arguments);
    }

    SqlCall rebuilt =
        rewrittenCall.getOperator().createCall(rewrittenCall.getParserPosition(), arguments);
    normalised.add(rebuilt);
    substituted = true;
    return rebuilt;
  }

  /**
   * The SQL-bodied scalar or aggregate this call names, or null. The operator is still unresolved at
   * this point — the statement has not been validated — so the match is by name, which is exactly
   * what makes it safe: a name that clashes with a built-in was refused at registration.
   */
  private @Nullable UserFunction sqlBodied(SqlCall call) {
    UserFunction declaration = named(call);
    if (declaration == null || !declaration.isSqlBodied()) {
      return null;
    }
    return switch (declaration.kind()) {
      case FUNCTION_KIND_SCALAR, FUNCTION_KIND_AGGREGATE -> declaration;
      default -> null;
    };
  }

  /** The declaration a still-unresolved call names, or null. */
  private @Nullable UserFunction named(SqlCall call) {
    if (!(call instanceof SqlBasicCall)) {
      return null;
    }
    if (call.getOperator().getSyntax() != org.apache.calcite.sql.SqlSyntax.FUNCTION) {
      return null;
    }

    UserFunction declaration = functions.byQualifiedName(call.getOperator().getName());
    return declaration != null ? declaration : defaultSchemaFunction(call.getOperator().getName());
  }

  private @Nullable UserFunction defaultSchemaFunction(String name) {
    for (UserFunction declaration : functions.declarations()) {
      if (declaration.name().equalsIgnoreCase(name)) {
        return declaration;
      }
    }
    return null;
  }

  /**
   * The call's operands in parameter order: named arguments permuted, omitted optional ones filled
   * from the declaration. Calcite does this for a resolved operator; a body is substituted before
   * resolution, so it is done here.
   */
  static List<SqlNode> arguments(UserFunction declaration, SqlCall call) {
    List<Parameter> parameters = declaration.parameters();
    List<@Nullable SqlNode> positional = new ArrayList<>(parameters.size());
    for (int i = 0; i < parameters.size(); i++) {
      positional.add(null);
    }

    int next = 0;
    for (SqlNode operand : call.getOperandList()) {
      if (operand instanceof SqlCall assignment
          && assignment.getOperator() == SqlStdOperatorTable.ARGUMENT_ASSIGNMENT) {
        String name = ((SqlIdentifier) assignment.operand(1)).getSimple();
        int index = indexOf(declaration, name);
        if (index < 0) {
          throw new UnsupportedFeatureException(
              "argument " + name + " of " + declaration.qualifiedName(),
              declaration.qualifiedName() + " has no parameter called '" + name + "'.");
        }
        positional.set(index, assignment.operand(0));
        continue;
      }

      if (next >= parameters.size()) {
        throw new UnsupportedFeatureException(
            declaration.qualifiedName() + " called with " + call.getOperandList().size() + " arguments",
            "It declares " + parameters.size() + ".");
      }
      positional.set(next++, isDefault(operand) ? null : operand);
    }

    List<SqlNode> resolved = new ArrayList<>(parameters.size());
    for (int i = 0; i < parameters.size(); i++) {
      SqlNode argument = positional.get(i);
      Parameter parameter = parameters.get(i);
      if (argument != null) {
        resolved.add(argument);
        continue;
      }
      if (!parameter.getOptional()) {
        throw new UnsupportedFeatureException(
            declaration.qualifiedName() + " called without '" + parameter.getName() + "'",
            "That parameter has no default, so every call must supply it.");
      }
      resolved.add(LiteralConverter.toSqlNode(parameter.getDefaultValue()));
    }
    return resolved;
  }

  private static boolean isDefault(SqlNode operand) {
    return operand instanceof SqlCall call && call.getOperator() == SqlStdOperatorTable.DEFAULT;
  }

  private static int indexOf(UserFunction declaration, String name) {
    List<Parameter> parameters = declaration.parameters();
    for (int i = 0; i < parameters.size(); i++) {
      if (parameters.get(i).getName().equalsIgnoreCase(name)) {
        return i;
      }
    }
    return -1;
  }

  /** The body with its already-cast parameters replaced, and a NULL guard when strict. */
  private static SqlNode substitute(UserFunction declaration, List<SqlNode> arguments) {
    Map<String, SqlNode> bound = new HashMap<>();
    List<Parameter> parameters = declaration.parameters();
    for (int i = 0; i < parameters.size(); i++) {
      bound.put(parameters.get(i).getName().toLowerCase(Locale.ROOT), arguments.get(i));
    }

    SqlNode body = parse(declaration).accept(new ParameterBinder(bound));
    if (!declaration.strict() || parameters.isEmpty()) {
      return body;
    }

    // RETURNS NULL ON NULL INPUT. Written as a guard rather than assumed, because a body may well be
    // something like COALESCE(a, 0) that would answer where the declaration says it must not. The
    // guard is a marker call — body first, then the arguments — that {@link #GUARD} turns into a
    // CASE at conversion time, keeping one IS NULL per argument whose validated type is nullable and
    // none at all when they all are not. See STRICT_GUARD for why the decision is made there.
    List<SqlNode> operands = new ArrayList<>(arguments.size() + 1);
    operands.add(body);
    operands.addAll(arguments);
    return STRICT_GUARD.createCall(SqlParserPos.ZERO, operands);
  }

  // ---- the strict guard ----

  /**
   * The marker a strict body's guard is written as: {@code "$chalk$strict_guard"(body, argument…)},
   * which {@link #GUARD} converts into {@code CASE WHEN <nullable argument> IS NULL THEN NULL ELSE
   * body END} — or into the bare body when no argument can be null.
   *
   * <p><b>Why a marker and not the CASE itself.</b> Which arguments need guarding is a question
   * about their <em>types</em>, and a body is substituted before validation (§2), where the inliner
   * has none: an argument is an arbitrary expression over the statement's own FROM clause. Writing
   * the guard over every argument and leaving it to {@code RexSimplify} to fold the impossible
   * disjuncts away is what the code did until this marker, and it made a plan's shape depend on how
   * clever a simplifier was that day — Calcite 1.43 stopped distributing {@code IS NULL} through a
   * {@code CAST} (it can throw, so {@code IS NULL(CAST(x))} is not unconditionally FALSE), and every
   * strict body whose arguments need a cast silently kept a guard and stopped being pushable. The
   * marker moves the decision to the first point where the types exist and Chalk still owns the
   * expression — its convertlet — so it is made once, from the validated types, and not by a
   * simplifier at all.
   *
   * <p>It is in no operator table, so no statement can name it; its name is reserved besides (D223),
   * so a host function of that name is not writable at all; and it never reaches Rex, because the
   * convertlet replaces it while the statement is being converted — matching it by <em>identity</em>,
   * since this instance is the only way the call is ever built.
   */
  public static final org.apache.calcite.sql.SqlFunction STRICT_GUARD =
      new org.apache.calcite.sql.SqlFunction(
          chalk.planner.ReservedNames.STRICT_GUARD,
          SqlKind.OTHER_FUNCTION,
          SqlBodyInliner::guardType,
          null,
          org.apache.calcite.sql.type.OperandTypes.VARIADIC,
          org.apache.calcite.sql.SqlFunctionCategory.SYSTEM) {
        @Override
        public RelDataType deriveType(
            org.apache.calcite.sql.validate.SqlValidator validator,
            org.apache.calcite.sql.validate.SqlValidatorScope scope,
            SqlCall call) {
          // Not through SqlFunction.deriveType, which resolves the call against the operator table
          // first: this operator is deliberately in none.
          return validateOperands(validator, scope, call);
        }
      };

  /** The body's type, made nullable when any argument is — which is what {@code STRICT} promises. */
  private static RelDataType guardType(org.apache.calcite.sql.SqlOperatorBinding binding) {
    RelDataType body = binding.getOperandType(0);
    boolean nullable = body.isNullable();
    for (int i = 1; i < binding.getOperandCount(); i++) {
      nullable |= binding.getOperandType(i).isNullable();
    }
    return binding.getTypeFactory().createTypeWithNullability(body, nullable);
  }

  /**
   * {@link #STRICT_GUARD} at conversion time, where every operand has a validated type: one {@code
   * IS NULL} per argument that can be null, and the body alone when none can.
   */
  private static final org.apache.calcite.sql2rel.SqlRexConvertlet GUARD =
      (cx, call) -> {
        List<org.apache.calcite.rex.RexNode> tests = new ArrayList<>();
        for (int i = 1; i < call.operandCount(); i++) {
          SqlNode argument = call.operand(i);
          if (cx.getValidator().getValidatedNodeType(argument).isNullable()) {
            tests.add(
                cx.getRexBuilder()
                    .makeCall(SqlStdOperatorTable.IS_NULL, cx.convertExpression(argument)));
          }
        }

        org.apache.calcite.rex.RexNode body = cx.convertExpression(call.operand(0));
        if (tests.isEmpty()) {
          // Nothing to guard: a strict function over arguments that cannot be null is its body, and
          // the body's own type is what STRICT_GUARD's return-type inference already gave the call.
          return body;
        }

        // The guard's own validated type: the body's, made nullable, which is what the CASE has to
        // deliver whichever branch answers.
        RelDataType type = cx.getValidator().getValidatedNodeType(call);
        org.apache.calcite.rex.RexBuilder rexBuilder = cx.getRexBuilder();
        return rexBuilder.makeCall(
            type,
            SqlStdOperatorTable.CASE,
            List.of(
                org.apache.calcite.rex.RexUtil.composeDisjunction(rexBuilder, tests),
                rexBuilder.makeNullLiteral(type),
                rexBuilder.ensureType(type, body, false)));
      };

  /**
   * The standard convertlets plus {@link #STRICT_GUARD}'s. Installed on the framework config, so
   * every conversion the pipeline does — including the one behind {@code PlannerPipeline.logical} —
   * sees it.
   */
  public static final org.apache.calcite.sql2rel.SqlRexConvertletTable CONVERTLETS =
      call ->
          call.getOperator() == STRICT_GUARD
              ? GUARD
              : org.apache.calcite.sql2rel.StandardConvertletTable.INSTANCE.get(call);

  /**
   * {@code CAST(argument AS declared-type)}, so a call decides nothing about the body's types that
   * the declaration has not already decided. A cast between equal types disappears in
   * {@code RexBuilder.makeCast}, so this leaves no trace in a plan whose arguments already match.
   *
   * <p>The cast stands where the argument stood, so it takes the argument's position. Calcite
   * builds a call's position as the union of its own and its operands', and one built at {@link
   * SqlParserPos#ZERO} over a positioned operand starts at line 0: the rebuilt call around it, and
   * every call built around that, would then start there too, and a refusal could not quote the
   * statement's text for any of them (D296). Positions reach error messages only, never a plan.
   */
  private static SqlNode cast(SqlNode argument, Parameter parameter) {
    RelDataType target = new TypeMapper(SPEC_TYPES).toCalcite(parameter.getType());
    SqlDataTypeSpec spec = SqlTypeUtil.convertTypeToSpec(target);
    return SqlStdOperatorTable.CAST.createCall(argument.getParserPosition(), argument, spec);
  }

  /** Replaces a bare identifier naming a parameter with the bound argument. */
  private static final class ParameterBinder extends SqlShuttle {
    private final Map<String, SqlNode> bound;

    ParameterBinder(Map<String, SqlNode> bound) {
      this.bound = bound;
    }

    @Override
    public @Nullable SqlNode visit(SqlIdentifier identifier) {
      if (identifier.names.size() != 1) {
        return identifier;
      }
      SqlNode argument = bound.get(identifier.getSimple().toLowerCase(Locale.ROOT));
      return argument == null ? identifier : argument;
    }
  }

  // ---- registration-time checks and parsing ----

  /** One parse of a scalar or aggregate body's expression. Bodies are small; this is not cached. */
  /**
   * Every scalar and aggregate body this inliner could substitute into a statement, parsed exactly
   * as it parses one. What a caller reads off them is their parser positions, which index each
   * body's own text and never the statement's: a refusal that quotes the statement (D296) must not
   * take a span from a substituted body for one of the statement's own.
   */
  public static List<SqlNode> substitutableBodies(UserFunctions functions) {
    List<SqlNode> bodies = new ArrayList<>();
    for (UserFunction declaration : functions.declarations()) {
      if (declaration.isSqlBodied()
          && (declaration.kind() == chalk.ir.v1.FunctionKind.FUNCTION_KIND_SCALAR
              || declaration.kind() == chalk.ir.v1.FunctionKind.FUNCTION_KIND_AGGREGATE)) {
        bodies.add(parse(declaration));
      }
    }
    return bodies;
  }

  private static SqlNode parse(UserFunction declaration) {
    try {
      return SqlParser.create(declaration.sqlText(), parserConfig()).parseExpression();
    } catch (SqlParseException e) {
      throw new UnsupportedFeatureException(
          "the body of " + declaration.qualifiedName(),
          "It does not parse as a SQL expression: " + e.getMessage());
    }
  }

  private static SqlParser.Config parserConfig() {
    return chalk.planner.plan.SqlConfigs.parser(chalk.planner.plan.SqlConfigs.DEFAULT_CONFORMANCE);
  }

  /**
   * What a body has to satisfy to be registered: it parses, and — for a scalar or aggregate body —
   * every bare identifier in it is one of the parameters. That second check is what the design calls
   * "validated at registration in the schema's scope with the parameters as typed placeholders": a
   * body that reaches for a column has nowhere to find one, and saying so at registration is far
   * more useful than saying it at the first call.
   */
  public static void check(UserFunction declaration) {
    // The guard this inliner writes is a reserved name (D223), and a body that spelled it quoted
    // would be indistinguishable from the marker once substituted.
    try {
      chalk.planner.ReservedNames.check(declaration.sqlText(), "the body");
    } catch (chalk.planner.ReservedNames.ReservedNameException reserved) {
      throw new chalk.planner.catalog.InvalidCatalogException(
          "functions (" + declaration.qualifiedName() + ")", reserved.getMessage());
    }
    switch (declaration.kind()) {
      case FUNCTION_KIND_TABLE -> parseQuery(declaration);
      default -> {
        SqlNode body = parse(declaration);
        Set<String> names = new LinkedHashSet<>();
        for (Parameter parameter : declaration.parameters()) {
          names.add(parameter.getName().toLowerCase(Locale.ROOT));
        }
        Set<String> unknown = new LinkedHashSet<>();
        body.accept(
            new SqlShuttle() {
              @Override
              public @Nullable SqlNode visit(SqlIdentifier identifier) {
                if (identifier.names.size() == 1
                    && !names.contains(identifier.getSimple().toLowerCase(Locale.ROOT))) {
                  unknown.add(identifier.getSimple());
                }
                return identifier;
              }
            });
        if (!unknown.isEmpty()) {
          throw new chalk.planner.catalog.InvalidCatalogException(
              "functions (" + declaration.qualifiedName() + ")",
              "the body names " + unknown + ", which "
                  + (declaration.parameters().isEmpty()
                      ? "is not a parameter: it declares none"
                      : "is not one of its parameters " + names));
        }
      }
    }
  }

  private static SqlNode parseQuery(UserFunction declaration) {
    try {
      return SqlParser.create(declaration.sqlText(), parserConfig()).parseQuery();
    } catch (SqlParseException e) {
      throw new chalk.planner.catalog.InvalidCatalogException(
          "functions (" + declaration.qualifiedName() + ")",
          "the body does not parse as a SELECT: " + e.getMessage());
    }
  }

  // ---- table bodies ----

  /**
   * The table a macro call expands to. Its row type is the declared {@code RETURNS TABLE}, so the
   * validator needs no expansion at all; {@link MacroTable#toRel} does the expansion, through the
   * pipeline's own view expander, once there is a cluster to expand into.
   */
  public static TranslatableTable macroTable(UserFunction declaration, List<SqlNode> operands) {
    List<Parameter> parameters = declaration.parameters();
    Map<String, SqlNode> bound = new HashMap<>();
    for (int i = 0; i < parameters.size(); i++) {
      SqlNode operand = i < operands.size() ? operands.get(i) : null;
      if (operand == null || isDefault(operand)) {
        operand = LiteralConverter.toSqlNode(parameters.get(i).getDefaultValue());
      }
      requireConstant(declaration, parameters.get(i), operand);
      bound.put(parameters.get(i).getName().toLowerCase(Locale.ROOT), operand);
    }

    SqlNode body = parseQuery(declaration).accept(new ParameterBinder(bound));
    String expanded = body.toSqlString(CalciteSqlDialect.DEFAULT).getSql();
    return new MacroTable(declaration, expanded);
  }

  /** V31: a macro binds its arguments while the statement is being validated, so they are constants. */
  private static void requireConstant(
      UserFunction declaration, Parameter parameter, SqlNode operand) {
    if (isConstant(operand)) {
      return;
    }

    String what = operand instanceof SqlDynamicParam ? "a dynamic parameter" : "'" + unwrap(operand) + "'";
    throw new UnsupportedFeatureException(
        "table function "
            + declaration.qualifiedName()
            + " called with "
            + what
            + " for '"
            + parameter.getName()
            + "'",
        "A SQL-bodied table function is a Calcite table macro: its arguments are bound while the "
            + "statement is validated, so they must be constants in v1 "
            + "(docs/design/17-user-defined-functions.md §2, V31). Write the value as a literal, or "
            + "declare the function with a client body, which takes any expression.");
  }

  /** A literal, or a cast or negation of one — which is what a normalised argument looks like. */
  private static boolean isConstant(SqlNode operand) {
    return unwrap(operand) instanceof SqlLiteral;
  }

  /** The value inside any number of casts and prefix minuses. */
  private static SqlNode unwrap(SqlNode operand) {
    SqlNode current = operand;
    while (current instanceof SqlCall call
        && (call.getKind() == SqlKind.CAST || call.getKind() == SqlKind.MINUS_PREFIX)
        && call.getOperandList().size() >= 1) {
      current = call.operand(0);
    }
    return current;
  }

  /** The expansion, as a table. Modelled on Calcite's own {@code ViewTable}. */
  private static final class MacroTable extends AbstractTable implements TranslatableTable {
    private final UserFunction declaration;
    private final String expanded;

    MacroTable(UserFunction declaration, String expanded) {
      this.declaration = declaration;
      this.expanded = expanded;
    }

    @Override
    public RelDataType getRowType(RelDataTypeFactory typeFactory) {
      return new TypeMapper(typeFactory)
          .toCalciteRowType(declaration.descriptor().getReturnsTable());
    }

    @Override
    public Statistic getStatistic() {
      long rows = declaration.descriptor().getRows();
      return rows > 0 ? Statistics.of(rows, ImmutableList.of()) : Statistics.UNKNOWN;
    }

    @Override
    public Schema.TableType getJdbcTableType() {
      return Schema.TableType.VIEW;
    }

    @Override
    public RelNode toRel(RelOptTable.ToRelContext context, RelOptTable relOptTable) {
      RelDataType rowType = relOptTable.getRowType();
      RelNode expansion =
          context
              .expandView(
                  rowType,
                  expanded,
                  ImmutableList.of(declaration.schemaName()),
                  ImmutableList.of(declaration.schemaName(), declaration.name()))
              .rel;
      return org.apache.calcite.plan.RelOptUtil.createCastRel(expansion, rowType, true);
    }
  }

  /** Keeps the checker quiet about an import used only by {@link #check}. */
  @SuppressWarnings("unused")
  private static void unused() {
    Util.discard(SqlKind.OTHER);
  }
}

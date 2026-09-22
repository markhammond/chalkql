package chalk.planner.plan;

import chalk.ir.v1.AggregateFunctionId;
import chalk.ir.v1.DialectProfile;
import chalk.ir.v1.FunctionId;
import chalk.ir.v1.NullCollation;
import chalk.ir.v1.PredicateShape;
import chalk.ir.v1.SourceCapabilities;
import chalk.ir.v1.StringCollation;
import chalk.planner.ir.FunctionMapping;
import chalk.planner.rpc.v1.DisabledCapability;
import java.util.EnumSet;
import java.util.List;
import java.util.Set;
import org.apache.calcite.rel.RelCollation;
import org.apache.calcite.rel.RelFieldCollation;
import org.apache.calcite.rel.core.AggregateCall;
import org.apache.calcite.rel.type.RelDataType;
import org.apache.calcite.rex.RexCall;
import org.apache.calcite.rex.RexDynamicParam;
import org.apache.calcite.rex.RexInputRef;
import org.apache.calcite.rex.RexLiteral;
import org.apache.calcite.rex.RexNode;
import org.apache.calcite.sql.SqlKind;
import org.apache.calcite.sql.type.SqlTypeName;
import org.apache.calcite.util.Sarg;

/**
 * The one place that answers "may this be pushed into that source?" (D82, D89). Every rule in {@link
 * chalk.planner.plan.rules.PushdownRules} asks this and nothing else, so a third-party adapter never
 * touches the planner and the drift rules cannot be forgotten in one rule and remembered in another.
 *
 * <p>Two kinds of answer live here and they are deliberately not mixed:
 *
 * <ul>
 *   <li><b>What the descriptor allows.</b> Nothing undeclared is pushed. An empty descriptor pushes
 *       nothing at all, which is every source's default. {@code supports_case} is the one field
 *       whose absence means <em>yes</em> (D273, {@link #supportsCase}), and it is not an exception
 *       to the sentence above: a conditional needs a projection or a predicate to live in, and both
 *       of those still have to be declared before anything is pushed at all.
 *   <li><b>What the semantics allow, whatever the descriptor claims (D89).</b> String equality,
 *       {@code LIKE}, string ranges, string {@code ORDER BY} and {@code DISTINCT}/{@code GROUP BY}
 *       over strings only under a binary collation; a sort only when the source honours the plan's
 *       null placement; a comparison only when the source's precision does not truncate either side;
 *       never a {@code CURRENT_*} or any function whose value depends on where it runs; an
 *       approximation only where the profile permits it. These are refusals the descriptor cannot
 *       override, because getting them wrong is a wrong answer rather than a slow one.
 * </ul>
 */
public final class PushdownGate {
  private final SourceCapabilities capabilities;
  private final DialectProfile profile;
  private final Set<DisabledCapability> disabled;
  private final Set<PredicateShape> shapes;
  private final Set<FunctionId> pushableFunctions;
  private final Set<FunctionId> unsupportedFunctions;
  private final Set<AggregateFunctionId> pushableAggregates;

  /** The schema whose source this gate speaks for; a native function belongs to exactly one (D78). */
  private final String schemaName;

  /** Lower-cased {@code native_functions}, the names this source runs itself. */
  private final Set<String> nativeFunctions;

  /** Whether this profile's dialect writes an {@code OFFSET} (F121); measured on first use. */
  private @org.checkerframework.checker.nullness.qual.Nullable Boolean rendersOffset;

  public PushdownGate(
      SourceCapabilities capabilities, DialectProfile profile, Set<DisabledCapability> disabled) {
    this(capabilities, profile, disabled, "");
  }

  public PushdownGate(
      SourceCapabilities capabilities,
      DialectProfile profile,
      Set<DisabledCapability> disabled,
      String schemaName) {
    this.capabilities = capabilities;
    this.profile = profile;
    this.schemaName = schemaName;
    this.nativeFunctions = new java.util.HashSet<>();
    for (String name : capabilities.getNativeFunctionsList()) {
      this.nativeFunctions.add(name.toLowerCase(java.util.Locale.ROOT));
    }
    this.disabled = disabled.isEmpty() ? EnumSet.noneOf(DisabledCapability.class) : EnumSet.copyOf(disabled);
    this.shapes = setOf(capabilities.getPushablePredicatesList(), PredicateShape.class);
    this.pushableFunctions = setOf(capabilities.getPushableFunctionsList(), FunctionId.class);
    this.unsupportedFunctions = setOf(capabilities.getUnsupportedFunctionsList(), FunctionId.class);
    this.pushableAggregates =
        setOf(capabilities.getPushableAggregatesList(), AggregateFunctionId.class);
  }

  private static <E extends Enum<E>> Set<E> setOf(List<E> values, Class<E> type) {
    Set<E> set = EnumSet.noneOf(type);
    set.addAll(values);
    return set;
  }

  public SourceCapabilities capabilities() {
    return capabilities;
  }

  public DialectProfile profile() {
    return profile;
  }

  private boolean off(DisabledCapability capability) {
    return disabled.contains(capability);
  }

  // ---------------------------------------------------------------- predicates

  /** Whether a whole {@code WHERE} may be pushed: every conjunct's shape and every expression. */
  public boolean canPushFilter(RexNode condition) {
    return !off(DisabledCapability.DISABLED_CAPABILITY_FILTER) && predicate(condition);
  }

  private boolean predicate(RexNode node) {
    return switch (node.getKind()) {
      case AND -> shapes.contains(PredicateShape.PREDICATE_SHAPE_AND) && allPredicates(node);
      case OR -> shapes.contains(PredicateShape.PREDICATE_SHAPE_OR) && allPredicates(node);
      case NOT -> shapes.contains(PredicateShape.PREDICATE_SHAPE_NOT) && allPredicates(node);
      case EQUALS, NOT_EQUALS ->
          shapes.contains(PredicateShape.PREDICATE_SHAPE_EQ) && comparison((RexCall) node);
      case LESS_THAN, LESS_THAN_OR_EQUAL, GREATER_THAN, GREATER_THAN_OR_EQUAL ->
          shapes.contains(PredicateShape.PREDICATE_SHAPE_RANGE) && comparison((RexCall) node);
      case IS_NULL, IS_NOT_NULL ->
          shapes.contains(PredicateShape.PREDICATE_SHAPE_IS_NULL) && allPushable(node);
      case IN -> inList(node);
      case SEARCH -> search((RexCall) node);
      case LIKE -> like((RexCall) node);
      case INPUT_REF -> true;
      case LITERAL -> true;
      default -> ChalkKeySet.is(node) && keySet(node);
    };
  }

  /**
   * A lookup join's key set (M5). Judged as the {@code IN} list it is: the shape must be declared,
   * the ceiling must be non-zero — that ceiling is what sizes a call — and the key column must not
   * be a string the source compares differently from Chalk. The {@code VALUES} spelling additionally
   * needs {@code supports_values_join}, because shipping rows is a different claim from accepting a
   * list of literals, and a <em>composite</em> key set needs
   * {@code supports_row_value_in_list} for the same reason (F50): {@code (a, b) IN ((?, ?), …)} is
   * not a spelling every engine parses, and one that does not would answer a syntax error rather
   * than a wrong number of rows.
   */
  private boolean keySet(RexNode node) {
    if (off(DisabledCapability.DISABLED_CAPABILITY_IN_LIST)
        || !shapes.contains(PredicateShape.PREDICATE_SHAPE_IN)
        || capabilities.getMaxInList() <= 0) {
      return false;
    }
    RexCall call = (RexCall) node;
    if (ChalkKeySet.isRows(call) && !capabilities.getSupportsValuesJoin()) {
      return false;
    }
    if (ChalkKeySet.columns(call) > 1 && !capabilities.getSupportsRowValueInList()) {
      return false;
    }
    return allPushable(call) && !comparesStrings(call);
  }

  /**
   * Whether this source accepts a row-constructor {@code IN} list, and therefore a key set over
   * more than one column (F50).
   */
  public boolean supportsRowValueInList() {
    return !off(DisabledCapability.DISABLED_CAPABILITY_IN_LIST)
        && capabilities.getSupportsRowValueInList();
  }

  // ---------------------------------------------------------------- naming a shape (F46)

  /**
   * The {@link PredicateShape} this predicate needs and this source does not declare — the host's
   * own vocabulary for what a refusal is about (F46).
   *
   * <p>{@code UNSPECIFIED} when every shape the predicate is made of <em>is</em> declared, and what
   * stopped it was something else: a list longer than {@code max_in_list}, a string column under a
   * collation the source does not share, an expression the source cannot spell. A caller that
   * cannot name a missing shape should say so rather than guess.
   *
   * <p>Depth first, and the first one found: a predicate that needs two undeclared shapes needs both,
   * and naming one of them is what a message can usefully say.
   */
  public PredicateShape missingShape(RexNode node) {
    PredicateShape shape = shapeOf(node);
    if (shape != PredicateShape.PREDICATE_SHAPE_UNSPECIFIED && !declares(shape)) {
      return shape;
    }
    if (node instanceof RexCall call) {
      for (RexNode operand : call.getOperands()) {
        PredicateShape inner = missingShape(operand);
        if (inner != PredicateShape.PREDICATE_SHAPE_UNSPECIFIED) {
          return inner;
        }
      }
    }
    return PredicateShape.PREDICATE_SHAPE_UNSPECIFIED;
  }

  /**
   * The shape the gate judges this node as, whether or not the source declares it: the same reading
   * {@link #predicate} makes, written once so a message and a decision cannot drift apart.
   *
   * <p>A {@code SEARCH} is judged as what it stands for, as {@link #search} judges it: one point is
   * an {@code EQ}, several are an {@code IN}, and a range is a {@code RANGE}.
   */
  public PredicateShape shapeOf(RexNode node) {
    return switch (node.getKind()) {
      case AND -> PredicateShape.PREDICATE_SHAPE_AND;
      case OR -> PredicateShape.PREDICATE_SHAPE_OR;
      case NOT -> PredicateShape.PREDICATE_SHAPE_NOT;
      case EQUALS, NOT_EQUALS -> PredicateShape.PREDICATE_SHAPE_EQ;
      case LESS_THAN, LESS_THAN_OR_EQUAL, GREATER_THAN, GREATER_THAN_OR_EQUAL ->
          PredicateShape.PREDICATE_SHAPE_RANGE;
      case IS_NULL, IS_NOT_NULL -> PredicateShape.PREDICATE_SHAPE_IS_NULL;
      case IN -> PredicateShape.PREDICATE_SHAPE_IN;
      case SEARCH -> sargShape((RexCall) node);
      case LIKE -> likeShape((RexCall) node);
      default ->
          ChalkKeySet.is(node)
              ? PredicateShape.PREDICATE_SHAPE_IN
              : PredicateShape.PREDICATE_SHAPE_UNSPECIFIED;
    };
  }

  private PredicateShape sargShape(RexCall call) {
    if (!(call.getOperands().get(1) instanceof RexLiteral literal)) {
      return PredicateShape.PREDICATE_SHAPE_UNSPECIFIED;
    }
    Sarg<?> sarg = literal.getValueAs(Sarg.class);
    if (sarg == null || sarg.isNone() || sarg.isAll()) {
      return PredicateShape.PREDICATE_SHAPE_UNSPECIFIED;
    }
    if (!sarg.isPoints()) {
      return PredicateShape.PREDICATE_SHAPE_RANGE;
    }
    return sarg.pointCount == 1
        ? PredicateShape.PREDICATE_SHAPE_EQ
        : PredicateShape.PREDICATE_SHAPE_IN;
  }

  private PredicateShape likeShape(RexCall call) {
    boolean prefix =
        call.getOperands().size() >= 2
            && call.getOperands().get(1) instanceof RexLiteral literal
            && isPrefixPattern(literal);
    return prefix
        ? PredicateShape.PREDICATE_SHAPE_LIKE_PREFIX
        : PredicateShape.PREDICATE_SHAPE_LIKE;
  }

  /**
   * Whether the source takes this shape at all. {@code IN} is the one with a second half: a source
   * that declares the shape and a ceiling of zero takes no list, and a request may turn it off.
   */
  private boolean declares(PredicateShape shape) {
    if (shape == PredicateShape.PREDICATE_SHAPE_IN) {
      return maxInList() > 0;
    }
    if (shape == PredicateShape.PREDICATE_SHAPE_LIKE_PREFIX) {
      return shapes.contains(PredicateShape.PREDICATE_SHAPE_LIKE_PREFIX)
          || shapes.contains(PredicateShape.PREDICATE_SHAPE_LIKE);
    }
    return shapes.contains(shape);
  }

  /** The longest {@code IN} list this source accepts. Zero means none is ever pushed. */
  public int maxInList() {
    return off(DisabledCapability.DISABLED_CAPABILITY_IN_LIST)
            || !shapes.contains(PredicateShape.PREDICATE_SHAPE_IN)
        ? 0
        : capabilities.getMaxInList();
  }

  /** Whether the source can join a pushed query against inline rows (M5, JOIN_STRATEGY_BROADCAST). */
  public boolean supportsValuesJoin() {
    return !off(DisabledCapability.DISABLED_CAPABILITY_IN_LIST)
        && capabilities.getSupportsValuesJoin();
  }

  private boolean allPredicates(RexNode node) {
    for (RexNode operand : ((RexCall) node).getOperands()) {
      if (!predicate(operand)) {
        return false;
      }
    }
    return true;
  }

  /**
   * An {@code IN} list is pushed when the shape is declared, the list fits {@code max_in_list}, and
   * every member is pushable. A zero ceiling means no list is ever pushed.
   */
  private boolean inList(RexNode node) {
    if (off(DisabledCapability.DISABLED_CAPABILITY_IN_LIST)
        || !shapes.contains(PredicateShape.PREDICATE_SHAPE_IN)) {
      return false;
    }
    RexCall call = (RexCall) node;
    int members = call.getOperands().size() - 1;
    int ceiling = capabilities.getMaxInList();
    if (ceiling <= 0 || members > ceiling) {
      return false;
    }
    return allPushable(node) && !comparesStrings(call);
  }

  /**
   * A {@code SEARCH} — Calcite's compacted form for {@code BETWEEN}, {@code IN} and any run of
   * comparisons on one column. It is judged as the shapes it stands for, because that is what the
   * source will actually be asked to evaluate: {@code RelToSqlConverter} unparses a points-Sarg as
   * an {@code IN} list and a range-Sarg as comparisons, and {@code RexToIr} expands it the same way
   * for {@code pushed_plan}.
   */
  private boolean search(RexCall call) {
    RexNode operand = call.getOperands().get(0);
    if (!pushable(operand)
        || (isCharacter(operand.getType()) && !binaryCollation())
        || truncates(operand.getType())) {
      return false;
    }
    if (!(call.getOperands().get(1) instanceof RexLiteral literal)) {
      return false;
    }
    Sarg<?> sarg = literal.getValueAs(Sarg.class);
    if (sarg == null || sarg.isNone() || sarg.isAll()) {
      return false;
    }
    // A Sarg that also matches NULL unparses with an `IS NULL` disjunct.
    if (sarg.nullAs != org.apache.calcite.rex.RexUnknownAs.UNKNOWN
        && !(shapes.contains(PredicateShape.PREDICATE_SHAPE_IS_NULL)
            && shapes.contains(PredicateShape.PREDICATE_SHAPE_OR))) {
      return false;
    }

    java.util.Set<? extends com.google.common.collect.Range<?>> ranges = sarg.rangeSet.asRanges();
    if (sarg.isPoints()) {
      if (sarg.pointCount == 1) {
        return shapes.contains(PredicateShape.PREDICATE_SHAPE_EQ);
      }
      if (off(DisabledCapability.DISABLED_CAPABILITY_IN_LIST)
          || !shapes.contains(PredicateShape.PREDICATE_SHAPE_IN)) {
        return false;
      }
      int ceiling = capabilities.getMaxInList();
      return ceiling > 0 && sarg.pointCount <= ceiling;
    }

    if (!shapes.contains(PredicateShape.PREDICATE_SHAPE_RANGE)) {
      return false;
    }
    if (ranges.size() > 1 && !shapes.contains(PredicateShape.PREDICATE_SHAPE_OR)) {
      return false;
    }
    // A range bounded on both sides is two comparisons joined by AND.
    for (com.google.common.collect.Range<?> range : ranges) {
      if (range.hasLowerBound()
          && range.hasUpperBound()
          && !shapes.contains(PredicateShape.PREDICATE_SHAPE_AND)) {
        return false;
      }
    }
    return true;
  }

  /**
   * A {@code LIKE} is pushed only under a binary collation (D89), and only for the shape the source
   * declared: a pattern that is a plain prefix needs {@code LIKE_PREFIX}, anything else needs
   * {@code LIKE}. A non-literal pattern is never a prefix, so it needs the general shape.
   */
  private boolean like(RexCall call) {
    if (!binaryCollation() || !allPushable(call)) {
      return false;
    }
    boolean prefix =
        call.getOperands().size() >= 2
            && call.getOperands().get(1) instanceof RexLiteral literal
            && isPrefixPattern(literal);
    return prefix
        ? shapes.contains(PredicateShape.PREDICATE_SHAPE_LIKE_PREFIX)
            || shapes.contains(PredicateShape.PREDICATE_SHAPE_LIKE)
        : shapes.contains(PredicateShape.PREDICATE_SHAPE_LIKE);
  }

  /** {@code 'abc%'}: wildcards nowhere but the very end, and no escape to reason about. */
  private static boolean isPrefixPattern(RexLiteral literal) {
    Object value = literal.getValue2();
    if (!(value instanceof String pattern) || pattern.isEmpty()) {
      return false;
    }
    int percent = pattern.indexOf('%');
    return percent == pattern.length() - 1 && pattern.indexOf('_') < 0;
  }

  /**
   * A comparison is pushed when both sides are pushable, neither side compares strings under a
   * collation that is not Chalk's, and the source's precision cannot truncate either side.
   */
  private boolean comparison(RexCall call) {
    return allPushable(call) && !comparesStrings(call) && !truncates(call);
  }

  /** Whether any operand of this call is a string — the collation question (D89). */
  private boolean comparesStrings(RexCall call) {
    if (binaryCollation()) {
      return false;
    }
    for (RexNode operand : call.getOperands()) {
      if (isCharacter(operand.getType())) {
        return true;
      }
    }
    return false;
  }

  private boolean binaryCollation() {
    return profile.getStringCollation() == StringCollation.STRING_COLLATION_BINARY;
  }

  private static boolean isCharacter(RelDataType type) {
    SqlTypeName name = type.getSqlTypeName();
    return name == SqlTypeName.CHAR || name == SqlTypeName.VARCHAR;
  }

  /**
   * Whether the source's own type system would lose digits on either side of this call (D89). A
   * DECIMAL(28,10) compared inside a source whose decimals are doubles is a different comparison,
   * so it stays local; a TIMESTAMP(9) inside a source that stores milliseconds is the same story.
   */
  private boolean truncates(RexCall call) {
    for (RexNode operand : call.getOperands()) {
      if (truncates(operand.getType())) {
        return true;
      }
    }
    return false;
  }

  private boolean truncates(RelDataType type) {
    SqlTypeName name = type.getSqlTypeName();
    if (name == SqlTypeName.DECIMAL) {
      int max = profile.getMaxNumericPrecision();
      return max > 0 && type.getPrecision() > max;
    }
    if (name == SqlTypeName.TIMESTAMP || name == SqlTypeName.TIMESTAMP_WITH_LOCAL_TIME_ZONE) {
      int max = profile.getMaxTimestampPrecision();
      return max > 0 && type.getPrecision() > max;
    }
    if (name == SqlTypeName.BOOLEAN) {
      return !profile.getHasBoolean();
    }
    return false;
  }

  // ---------------------------------------------------------------- expressions

  /** Whether every scalar expression in {@code nodes} may be evaluated by the source. */
  public boolean allPushable(Iterable<? extends RexNode> nodes) {
    for (RexNode node : nodes) {
      if (!pushable(node)) {
        return false;
      }
    }
    return true;
  }

  private boolean allPushable(RexNode call) {
    return allPushable(((RexCall) call).getOperands());
  }

  /**
   * One scalar expression. A column reference and a literal are always pushable; a dynamic parameter
   * needs {@code supports_parameters}; a call needs its function declared and its operands pushable.
   */
  public boolean pushable(RexNode node) {
    if (node instanceof RexInputRef || node instanceof RexLiteral) {
      return true;
    }
    if (node instanceof RexDynamicParam) {
      // A context scalar bound at execution never travels (D209). `RemoteQuery.parameters` list the
      // generated SQL's placeholders in the order they appear, and a parameter that is not among
      // the statement's own cannot be lined up against them; the conjunct that reads one stays
      // local, which is a residual like any other (§3.7 item 3).
      //
      // A parameterised `LIMIT`/`OFFSET` bound is not asked this question at all and is exempt from
      // `supports_parameters` (D288): it never reaches the provider as a parameter, because the
      // executor writes its value into the query text before the query is sent. The gate it does
      // answer to is {@link #supportsLimit} / {@link #supportsOffset}, the same one a literal bound
      // answers to, and `PushdownRules.SortRule` is where that is asked.
      return !(node instanceof chalk.planner.entitlement.BoundParam) && supportsParameters();
    }
    if (!(node instanceof RexCall call)) {
      return false;
    }
    if (!isDeterministic(call)) {
      return false;
    }
    for (RexNode operand : call.getOperands()) {
      if (!pushable(operand)) {
        return false;
      }
    }
    return switch (call.getKind()) {
      // The logical and comparison shapes are the predicate gate's business; as sub-expressions of
      // a projection they ride on the same declarations.
      case AND, OR, NOT, EQUALS, NOT_EQUALS, LESS_THAN, LESS_THAN_OR_EQUAL, GREATER_THAN,
              GREATER_THAN_OR_EQUAL, IS_NULL, IS_NOT_NULL ->
          predicate(call);
      case CAST -> !truncates(call.getType());
      case CASE -> supportsCase() && !truncates(call.getType());
      default -> function(call);
    };
  }

  /**
   * Whether this source evaluates a conditional: a SQL {@code CASE}, or the IR's {@code IfThen} in
   * a pushed plan (D273, F104).
   *
   * <p><b>Absent means true</b>, which is why {@code supports_case} carries explicit presence on
   * the wire: a {@code CASE} whose operands all push is SQL-92 and every dialect Calcite's
   * {@code RelToSqlConverter} writes spells it the same way, so this is an opt-out for a source
   * that cannot take one rather than an opt-in (the owner's decision of 2026-09-17). A catalog
   * registered before the field existed therefore pushes a conditional from now on.
   *
   * <p>The flag admits the <em>shape</em> and nothing else. Every operand has already been through
   * {@link #pushable} by the time this is asked, so a conditional over a function the source did
   * not declare, over a string under a collation that is not Chalk's, or over a bound context
   * parameter, is refused for the reason it always was. The result type is asked the {@code CAST}
   * question too: a conditional yielding a decimal wider than the source stores, or a boolean in a
   * source that has none, is one the source would answer differently.
   *
   * <p>What it does <em>not</em> decide is whether a <b>sanitiser</b> may travel. That is
   * {@link MaskLocality}'s question and it is unchanged (design 16 §3.8): an expression over a
   * column this leaf does not disclose plainly needs the table's {@code push_masks} and the
   * source's {@code supports_mask_pushdown}, both, whatever this flag says.
   */
  public boolean supportsCase() {
    return !capabilities.hasSupportsCase() || capabilities.getSupportsCase();
  }

  /** Whether the IR function this call maps to is one the source declared. */
  private boolean function(RexCall call) {
    chalk.planner.catalog.UserFunction declared =
        UserOperators.declarationOf(call.getOperator());
    if (declared != null) {
      return canPushNative(declared);
    }
    FunctionId id = FunctionMapping.tryFunctionOf(call);
    if (id == null || id == FunctionId.FUNCTION_ID_UNSPECIFIED) {
      return false;
    }
    return pushableFunctions.contains(id) && !unsupportedFunctions.contains(id);
  }

  /**
   * D89: nothing whose value depends on where or when it runs. {@code CURRENT_TIMESTAMP} in the
   * source is the source's clock and its time zone, which is a different answer from Chalk's;
   * {@code RAND} is a different answer every time.
   */
  private static boolean isDeterministic(RexCall call) {
    if (!call.getOperator().isDeterministic() || call.getOperator().isDynamicFunction()) {
      return false;
    }
    return switch (call.getKind()) {
      case CURRENT_VALUE, NEXT_VALUE, OTHER_FUNCTION ->
          !isSessionFunction(call.getOperator().getName());
      default -> true;
    };
  }

  private static boolean isSessionFunction(String name) {
    String upper = name.toUpperCase(java.util.Locale.ROOT);
    return upper.startsWith("CURRENT_")
        || upper.startsWith("LOCALTIME")
        || upper.equals("NOW")
        || upper.equals("RAND")
        || upper.equals("RAND_INTEGER")
        || upper.equals("USER")
        || upper.equals("SESSION_USER")
        || upper.equals("SYSTEM_USER");
  }

  // ---------------------------------------------------------------- operators

  /**
   * Whether this source may be handed a mask (D153, §3.8). Half of the answer: the table has to
   * declare {@code push_masks} too, and {@link MaskLocality} is where the two meet.
   */
  public boolean supportsMaskPushdown() {
    return capabilities.getSupportsMaskPushdown();
  }

  public boolean supportsProject() {
    return capabilities.getSupportsProject()
        && !off(DisabledCapability.DISABLED_CAPABILITY_PROJECT);
  }

  public boolean supportsParameters() {
    return capabilities.getSupportsParameters()
        && !off(DisabledCapability.DISABLED_CAPABILITY_PARAMETERS);
  }

  public boolean supportsLimit() {
    return capabilities.getSupportsLimit() && !off(DisabledCapability.DISABLED_CAPABILITY_LIMIT);
  }

  /**
   * Whether an {@code OFFSET} may be pushed: the source has to declare one, and this source's
   * dialect has to be able to <em>write</em> one (F121, {@link SourceDialects#rendersOffset}).
   *
   * <p>The second half is a semantic refusal of the D89 kind and not a descriptor question: a
   * dialect whose unparser drops the offset would be handed "the fetch rows after offset" and
   * would answer "the first fetch rows", which is a wrong answer and a quiet one. A source whose
   * dialect cannot spell it keeps the offset local, exactly as one that never declared it does.
   */
  public boolean supportsOffset() {
    return capabilities.getSupportsOffset() && supportsLimit() && dialectWritesAnOffset();
  }

  /** Measured once per gate: building a dialect resolves a product reflectively. */
  private boolean dialectWritesAnOffset() {
    Boolean answer = rendersOffset;
    if (answer == null) {
      answer = SourceDialects.rendersOffset(SourceDialects.of(profile));
      rendersOffset = answer;
    }
    return answer;
  }

  public boolean supportsGroupBy() {
    return capabilities.getSupportsGroupBy()
        && !off(DisabledCapability.DISABLED_CAPABILITY_AGGREGATE);
  }

  public boolean supportsHaving() {
    return capabilities.getSupportsHaving() && supportsGroupBy();
  }

  public boolean supportsDistinct() {
    return capabilities.getSupportsDistinct()
        && !off(DisabledCapability.DISABLED_CAPABILITY_DISTINCT);
  }

  /**
   * A sort is pushed when the source sorts at all, when the plan's null placement is one the source
   * will actually produce, and when no key is a string the source orders differently (D89).
   */
  public boolean canPushSort(RelCollation collation, List<RelDataType> fieldTypes) {
    if (!capabilities.getSupportsSort() || off(DisabledCapability.DISABLED_CAPABILITY_SORT)) {
      return false;
    }
    for (RelFieldCollation field : collation.getFieldCollations()) {
      int index = field.getFieldIndex();
      if (index >= 0 && index < fieldTypes.size()) {
        RelDataType type = fieldTypes.get(index);
        if (isCharacter(type) && !binaryCollation()) {
          return false;
        }
        if (truncates(type)) {
          return false;
        }
      }
      if (!nullPlacementHonoured(field)) {
        return false;
      }
    }
    return true;
  }

  /**
   * Either the source takes an explicit {@code NULLS FIRST}/{@code NULLS LAST}, or its own default
   * already puts NULLs where the plan wants them. Anything else would reorder the NULL rows, which
   * is a different answer for any query that can see them.
   */
  private boolean nullPlacementHonoured(RelFieldCollation field) {
    if (profile.getSupportsNullOrderingClause()) {
      return true;
    }
    boolean ascending = !field.getDirection().isDescending();
    RelFieldCollation.NullDirection wanted = field.nullDirection;
    if (wanted == RelFieldCollation.NullDirection.UNSPECIFIED) {
      return true;
    }
    RelFieldCollation.NullDirection actual =
        switch (profile.getDefaultNullCollation()) {
          case NULL_COLLATION_FIRST -> RelFieldCollation.NullDirection.FIRST;
          case NULL_COLLATION_LAST -> RelFieldCollation.NullDirection.LAST;
          case NULL_COLLATION_LOW ->
              ascending ? RelFieldCollation.NullDirection.FIRST : RelFieldCollation.NullDirection.LAST;
          case NULL_COLLATION_HIGH ->
              ascending ? RelFieldCollation.NullDirection.LAST : RelFieldCollation.NullDirection.FIRST;
          default -> RelFieldCollation.NullDirection.UNSPECIFIED;
        };
    return actual == wanted;
  }

  /**
   * A user function goes into its own source and nowhere else (D78). A client body is never pushed —
   * the implementation is in the host process — and a SQL body has already disappeared, so what is
   * left is a native one, which this source runs only if it is <em>this</em> source and it said so.
   */
  public boolean canPushNative(chalk.planner.catalog.UserFunction declared) {
    return declared.isNative()
        && declared.schemaName().equalsIgnoreCase(schemaName)
        && nativeFunctions.contains(declared.name().toLowerCase(java.util.Locale.ROOT));
  }

  /**
   * An aggregate call: the function is declared, its arguments are not strings the source groups
   * differently, and an approximation is only accepted where the profile permits it (D89).
   */
  public boolean canPushAggregate(AggregateCall call, List<RelDataType> inputTypes) {
    chalk.planner.catalog.UserFunction declaredAggregate =
        UserOperators.declarationOf(call.getAggregation());
    if (declaredAggregate != null && !canPushNative(declaredAggregate)) {
      return false;
    }
    AggregateFunctionId id = FunctionMapping.tryAggregateOf(call);
    if (id == null || id == AggregateFunctionId.AGGREGATE_FUNCTION_ID_UNSPECIFIED) {
      return false;
    }
    if (!pushableAggregates.contains(id)) {
      return false;
    }
    if (call.isApproximate() && !profile.getApproximateDistinctCount()) {
      return false;
    }
    if (call.isDistinct() && !supportsDistinct()) {
      return false;
    }
    for (int argument : call.getArgList()) {
      if (argument < 0 || argument >= inputTypes.size()) {
        return false;
      }
      RelDataType type = inputTypes.get(argument);
      if (call.isDistinct() && isCharacter(type) && !binaryCollation()) {
        return false;
      }
      if (truncates(type)) {
        return false;
      }
      if (type.getSqlTypeName() == SqlTypeName.DECIMAL
          && !profile.getApproximateDecimal()
          && isSummingAggregate(id)
          && profile.getMaxNumericPrecision() > 0
          && type.getPrecision() > profile.getMaxNumericPrecision()) {
        return false;
      }
    }
    return true;
  }

  private static boolean isSummingAggregate(AggregateFunctionId id) {
    return id == AggregateFunctionId.AGGREGATE_FUNCTION_ID_SUM
        || id == AggregateFunctionId.AGGREGATE_FUNCTION_ID_AVG;
  }

  /**
   * Grouping keys: plain columns only (§2), and never a string the source groups under another
   * collation, because two values Chalk sees as different could land in one group (D89).
   */
  public boolean canGroupBy(List<Integer> keys, List<RelDataType> inputTypes) {
    for (int key : keys) {
      if (key < 0 || key >= inputTypes.size()) {
        return false;
      }
      RelDataType type = inputTypes.get(key);
      if (isCharacter(type) && !binaryCollation()) {
        return false;
      }
      if (truncates(type)) {
        return false;
      }
    }
    return true;
  }

  /** Whether a join of this type may be pushed at all. */
  public boolean canPushJoin(org.apache.calcite.rel.core.JoinRelType type) {
    if (off(DisabledCapability.DISABLED_CAPABILITY_JOIN)) {
      return false;
    }
    return switch (type) {
      case INNER -> capabilities.getSupportsInnerJoin();
      case LEFT, RIGHT, FULL -> capabilities.getSupportsOuterJoin();
      case SEMI, ANTI -> capabilities.getSupportsSemiAntiJoin();
      default -> false;
    };
  }

  /**
   * {@code max_pushdown_rows}: a ceiling on the rows a pushed subtree is estimated to hand back.
   * Zero means unlimited. The rules check it against the subtree they would create, which is what
   * makes corpus query 10 push a filter and stop rather than push the aggregate above it.
   */
  public boolean withinRowCeiling(double estimatedRows) {
    long ceiling = capabilities.getMaxPushdownRows();
    return ceiling <= 0 || estimatedRows <= ceiling;
  }

  /** A stable description for plan text and diagnostics. */
  @Override
  public String toString() {
    return "PushdownGate(" + capabilities.getQueryLanguage() + ", " + profile.getDialect() + ")";
  }
}

package chalk.planner.plan.rules;

import chalk.planner.plan.rel.ChalkProject;
import chalk.planner.plan.rel.ChalkUnnest;
import java.util.ArrayList;
import java.util.List;
import org.apache.calcite.plan.RelOptRuleCall;
import org.apache.calcite.plan.RelRule;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.core.Correlate;
import org.apache.calcite.rel.core.JoinRelType;
import org.apache.calcite.rel.core.Project;
import org.apache.calcite.rel.core.Uncollect;
import org.apache.calcite.rel.logical.LogicalCorrelate;
import org.apache.calcite.rel.logical.LogicalProject;
import org.apache.calcite.rel.type.RelDataTypeField;
import org.apache.calcite.rex.RexBuilder;
import org.apache.calcite.rex.RexCorrelVariable;
import org.apache.calcite.rex.RexFieldAccess;
import org.apache.calcite.rex.RexInputRef;
import org.apache.calcite.rex.RexNode;
import org.apache.calcite.sql.type.SqlTypeName;
import org.apache.calcite.util.ImmutableBitSet;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * D66 — every spelling of {@code UNNEST} over a list column becomes a {@link ChalkUnnest}
 * ({@code 14-windows-ii.md} §7).
 *
 * <p>Calcite models {@code FROM t, UNNEST(t.xs)} as a {@code Correlate} whose right side is an
 * {@code Uncollect} over the correlation variable's list — a one-row {@code Values} projected to
 * {@code $cor0.xs}, then flattened. That is the same tree for {@code CROSS JOIN UNNEST}, for the
 * comma form, and for {@code LEFT JOIN LATERAL UNNEST … ON TRUE}; only the correlate's join type
 * differs, and that is exactly {@code keepEmpty} (V27, ADR 0018).
 *
 * <pre>
 * LogicalCorrelate(joinType=[inner|left], requiredColumns=[{n}])
 *   left
 *   [LogicalProject(identity)]
 *     Uncollect[(withOrdinality=[true])]
 *       LogicalProject($cor0.xs)
 *         LogicalValues(tuples=[[{ 0 }]])
 * </pre>
 *
 * <p>The whole right side is named in the operand pattern rather than walked from the correlate,
 * because under Volcano a rel's inputs are {@code RelSubset}s and only what the pattern names is a
 * concrete node. The field trimmer removes the identity projection above the {@code Uncollect} in
 * most queries and keeps it in some, so both shapes are registered.
 *
 * <p>The decorrelator leaves this shape alone and need not do otherwise: it is one flatten operator,
 * not a join. The {@code Correlate}'s own row type — left fields, then the element, then the
 * ordinality — is exactly {@link ChalkUnnest}'s, nullabilities included, so it is carried over rather
 * than rebuilt.
 *
 * <p>{@code UNNEST(a, b)} — PostgreSQL's zip form — has two operands under the {@code Uncollect} and
 * is declined here; {@link chalk.planner.plan.CorrelateSupport} then reports {@code UNSUPPORTED}
 * naming it. {@code UNNEST(ARRAY[…])} of a constant has no {@code Correlate} at all and is handled by
 * {@link ChalkUncollectRule}.
 */
public final class ChalkUnnestRule extends RelRule<ChalkRuleConfig> {

  /** {@code Correlate(left, Uncollect(Project($cor.xs)))} — the shape after field trimming. */
  public static final ChalkUnnestRule DIRECT =
      new ChalkUnnestRule(
          config(
              "ChalkUnnestRule",
              b ->
                  b.operand(LogicalCorrelate.class)
                      .inputs(
                          b1 -> b1.operand(RelNode.class).anyInputs(),
                          b1 ->
                              b1.operand(Uncollect.class)
                                  .oneInput(b2 -> b2.operand(LogicalProject.class).anyInputs())),
              /* projected= */ false),
          /* projected= */ false);

  /** The same with the identity projection Calcite puts above the {@code Uncollect}. */
  public static final ChalkUnnestRule PROJECTED =
      new ChalkUnnestRule(
          config(
              "ChalkUnnestRule:projected",
              b ->
                  b.operand(LogicalCorrelate.class)
                      .inputs(
                          b1 -> b1.operand(RelNode.class).anyInputs(),
                          b1 ->
                              b1.operand(LogicalProject.class)
                                  .oneInput(
                                      b2 ->
                                          b2.operand(Uncollect.class)
                                              .oneInput(
                                                  b3 ->
                                                      b3.operand(LogicalProject.class)
                                                          .anyInputs()))),
              /* projected= */ true),
          /* projected= */ true);

  private final boolean _projected;

  private ChalkUnnestRule(ChalkRuleConfig config, boolean projected) {
    super(config);
    _projected = projected;
  }

  private static ChalkRuleConfig config(
      String description, RelRule.OperandTransform operands, boolean projected) {
    return ChalkRuleConfig.of(
        description, operands, config -> new ChalkUnnestRule(config, projected));
  }

  /** A recognised correlate-over-uncollect: which column is flattened, and how. */
  public record Match(int listColumn, boolean withOrdinality, boolean keepEmpty) {}

  @Override
  public void onMatch(RelOptRuleCall call) {
    Correlate correlate = call.rel(0);
    RelNode left = call.rel(1);
    Project above = _projected ? call.rel(2) : null;
    Uncollect uncollect = call.rel(_projected ? 3 : 2);
    Project inner = call.rel(_projected ? 4 : 3);

    Match match = match(correlate, left, above, uncollect, inner);
    if (match == null) {
      return;
    }

    call.transformTo(
        ChalkUnnest.create(
            ChalkInputs.unordered(left),
            correlate.getRowType(),
            match.listColumn(),
            match.withOrdinality(),
            match.keepEmpty()));
  }

  /**
   * What this correlate flattens, or null when it is not an {@code UNNEST} of one list column. The
   * nodes are passed in rather than walked, because a caller under Volcano only has concrete nodes
   * for what the operand pattern named.
   */
  public static @Nullable Match match(
      Correlate correlate,
      RelNode left,
      @Nullable Project above,
      Uncollect uncollect,
      Project inner) {
    Integer listColumn = onlyRequiredColumn(correlate.getRequiredColumns());
    if (listColumn == null || !isCross(correlate.getJoinType())) {
      return null;
    }

    if (above != null && !isIdentity(above, uncollect.getRowType().getFieldCount())) {
      return null;
    }

    if (!readsCorrelatedColumn(inner, correlate, listColumn)) {
      return null;
    }

    List<RelDataTypeField> leftFields = left.getRowType().getFieldList();
    if (listColumn >= leftFields.size()
        || leftFields.get(listColumn).getType().getSqlTypeName() != SqlTypeName.ARRAY) {
      return null;
    }

    return new Match(
        listColumn, uncollect.withOrdinality, correlate.getJoinType() == JoinRelType.LEFT);
  }

  /**
   * The same question over a tree whose inputs are concrete rels — the logical tree
   * {@code CorrelateSupport} checks before Volcano runs.
   */
  public static @Nullable Match matchLogical(Correlate correlate) {
    RelNode right = correlate.getRight();
    Project above = null;
    if (right instanceof Project project) {
      above = project;
      right = project.getInput();
    }

    if (right instanceof Uncollect uncollect && uncollect.getInput() instanceof Project inner) {
      return match(correlate, correlate.getLeft(), above, uncollect, inner);
    }

    return null;
  }

  /** Only the two join types {@code UNNEST} can carry; anything else is not this shape. */
  private static boolean isCross(JoinRelType type) {
    return type == JoinRelType.INNER || type == JoinRelType.LEFT;
  }

  private static @Nullable Integer onlyRequiredColumn(ImmutableBitSet required) {
    return required.cardinality() == 1 ? required.nth(0) : null;
  }

  /**
   * A projection that renames nothing and reorders nothing. Anything else means the query does
   * something to the element that this node cannot carry, so it is declined.
   */
  private static boolean isIdentity(Project project, int inputFields) {
    List<RexNode> projects = project.getProjects();
    if (projects.size() != inputFields) {
      return false;
    }

    for (int i = 0; i < projects.size(); i++) {
      if (!(projects.get(i) instanceof RexInputRef ref) || ref.getIndex() != i) {
        return false;
      }
    }

    return true;
  }

  /**
   * Whether the {@code Uncollect} flattens exactly {@code $cor.<listColumn>} of this correlate — one
   * operand, a field access on this correlate's own variable, naming the required column. Two
   * operands is {@code UNNEST(a, b)}, which is declined.
   */
  private static boolean readsCorrelatedColumn(
      Project inner, Correlate correlate, int listColumn) {
    if (inner.getProjects().size() != 1) {
      return false;
    }

    if (!(inner.getProjects().get(0) instanceof RexFieldAccess access)
        || !(access.getReferenceExpr() instanceof RexCorrelVariable variable)) {
      return false;
    }

    return variable.id.equals(correlate.getCorrelationId())
        && access.getField().getIndex() == listColumn;
  }

  /**
   * {@code UNNEST(ARRAY[…])} of a constant: no correlation, so Calcite emits a bare
   * {@code Uncollect} over the project that builds the array. The unnest still needs a row to hang
   * off, so that project becomes the input and a {@link ChalkProject} above drops the list column
   * the {@code Uncollect}'s own row type does not have.
   */
  public static final class ChalkUncollectRule extends RelRule<ChalkRuleConfig> {
    public static final ChalkUncollectRule INSTANCE =
        new ChalkUncollectRule(
            ChalkRuleConfig.of(
                "ChalkUncollectRule",
                b ->
                    b.operand(Uncollect.class)
                        .oneInput(b1 -> b1.operand(LogicalProject.class).anyInputs()),
                ChalkUncollectRule::new));

    private ChalkUncollectRule(ChalkRuleConfig config) {
      super(config);
    }

    @Override
    public void onMatch(RelOptRuleCall call) {
      Uncollect uncollect = call.rel(0);
      Project input = call.rel(1);
      if (input.getRowType().getFieldCount() != 1
          || input.getRowType().getFieldList().get(0).getType().getSqlTypeName()
              != SqlTypeName.ARRAY) {
        return;
      }

      // A correlated Uncollect belongs to ChalkUnnestRule; this one must stand on its own.
      if (input.getProjects().get(0) instanceof RexFieldAccess access
          && access.getReferenceExpr() instanceof RexCorrelVariable) {
        return;
      }

      List<RelDataTypeField> unnested = new ArrayList<>(input.getRowType().getFieldList());
      unnested.addAll(uncollect.getRowType().getFieldList());
      ChalkUnnest unnest =
          ChalkUnnest.create(
              ChalkInputs.unordered(input),
              uncollect.getCluster().getTypeFactory().createStructType(unnested),
              0,
              uncollect.withOrdinality,
              false);

      RexBuilder rexBuilder = uncollect.getCluster().getRexBuilder();
      List<RexNode> projects = new ArrayList<>();
      for (int i = 1; i < unnested.size(); i++) {
        projects.add(rexBuilder.makeInputRef(unnest, i));
      }

      call.transformTo(ChalkProject.create(unnest, projects, uncollect.getRowType()));
    }
  }
}

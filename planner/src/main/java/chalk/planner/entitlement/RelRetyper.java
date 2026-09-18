package chalk.planner.entitlement;

import chalk.planner.UnsupportedFeatureException;
import java.util.ArrayList;
import java.util.List;
import org.apache.calcite.plan.RelOptCluster;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.core.Aggregate;
import org.apache.calcite.rel.core.AggregateCall;
import org.apache.calcite.rel.core.Correlate;
import org.apache.calcite.rel.core.Filter;
import org.apache.calcite.rel.core.Intersect;
import org.apache.calcite.rel.core.Join;
import org.apache.calcite.rel.core.Minus;
import org.apache.calcite.rel.core.Project;
import org.apache.calcite.rel.core.SetOp;
import org.apache.calcite.rel.core.Sort;
import org.apache.calcite.rel.core.Union;
import org.apache.calcite.rel.logical.LogicalAggregate;
import org.apache.calcite.rel.logical.LogicalCorrelate;
import org.apache.calcite.rel.logical.LogicalFilter;
import org.apache.calcite.rel.logical.LogicalIntersect;
import org.apache.calcite.rel.logical.LogicalJoin;
import org.apache.calcite.rel.logical.LogicalMinus;
import org.apache.calcite.rel.logical.LogicalProject;
import org.apache.calcite.rel.logical.LogicalSort;
import org.apache.calcite.rel.logical.LogicalUnion;
import org.apache.calcite.rel.type.RelDataType;
import org.apache.calcite.rel.type.RelDataTypeField;
import org.apache.calcite.rex.RexBuilder;
import org.apache.calcite.rex.RexInputRef;
import org.apache.calcite.rex.RexNode;
import org.apache.calcite.rex.RexShuttle;
import org.apache.calcite.sql.type.SqlTypeUtil;

/**
 * Rebuilds a node over inputs whose row types have changed, deriving its own row type again
 * (docs/design/16-entitlements.md §3.11).
 *
 * <p>Why it exists: a redacted NOT NULL column becomes a typed NULL under {@code PlaceholdersAsNull},
 * and a nullable mask over a NOT NULL column is nullable too, so the leaf's row type can come out of
 * the rewrite differing from the scan's in one field's <em>nullability</em>. Calcite compares types
 * by identity — {@code RexChecker} refuses an input reference whose declared type is not the field's
 * own, and {@code Project} stores its row type rather than deriving it — so a parent copied over the
 * widened input would be internally inconsistent and fail its own {@code isValid}.
 *
 * <p>The difference is always nullability alone, and that is checked rather than assumed: a rebuild
 * that changed a type's <em>kind</em> would be changing the statement's meaning, which the pass has
 * no business doing.
 */
final class RelRetyper {
  private RelRetyper() {}

  /**
   * {@code rel} over {@code inputs}, with every reference re-typed and every derived row type taken
   * again from the inputs.
   */
  static RelNode rebuild(RelNode rel, List<RelNode> inputs) {
    RexBuilder rexBuilder = rel.getCluster().getRexBuilder();
    RexShuttle refs = new InputRefRetyper(concatenatedFields(inputs), rexBuilder);

    if (rel instanceof Project project) {
      List<RexNode> exprs = refs.apply(project.getProjects());
      return LogicalProject.create(
          inputs.get(0),
          project.getHints(),
          exprs,
          project.getRowType().getFieldNames(),
          project.getVariablesSet());
    }
    if (rel instanceof Filter filter) {
      return LogicalFilter.create(
          inputs.get(0),
          filter.getCondition().accept(refs),
          com.google.common.collect.ImmutableSet.copyOf(filter.getVariablesSet()));
    }
    if (rel instanceof Join join) {
      return LogicalJoin.create(
          inputs.get(0),
          inputs.get(1),
          join.getHints(),
          join.getCondition().accept(refs),
          join.getVariablesSet(),
          join.getJoinType());
    }
    if (rel instanceof Correlate correlate) {
      return LogicalCorrelate.create(
          inputs.get(0),
          inputs.get(1),
          correlate.getHints(),
          correlate.getCorrelationId(),
          correlate.getRequiredColumns(),
          correlate.getJoinType());
    }
    if (rel instanceof Aggregate aggregate) {
      List<AggregateCall> calls = new ArrayList<>(aggregate.getAggCallList().size());
      for (AggregateCall call : aggregate.getAggCallList()) {
        calls.add(retype(call, inputs.get(0), aggregate.getGroupCount()));
      }
      return LogicalAggregate.create(
          inputs.get(0),
          aggregate.getHints(),
          aggregate.getGroupSet(),
          aggregate.getGroupSets(),
          calls);
    }
    if (rel instanceof Sort sort) {
      return LogicalSort.create(inputs.get(0), sort.getCollation(), sort.offset, sort.fetch);
    }
    if (rel instanceof Union union) {
      return LogicalUnion.create(inputs, union.all);
    }
    if (rel instanceof Intersect intersect) {
      return LogicalIntersect.create(inputs, intersect.all);
    }
    if (rel instanceof Minus minus) {
      return LogicalMinus.create(inputs, minus.all);
    }
    if (rel instanceof SetOp setOp) {
      return setOp.copy(setOp.getTraitSet(), inputs, setOp.all);
    }

    throw new UnsupportedFeatureException(
        "an entitled column that is NOT NULL in the catalog is redacted under "
            + rel.getRelTypeName()
            + ", which the entitlement pass cannot re-type",
        "A redacted column is a typed NULL, so its output type widens to nullable (D161) and every "
            + "node above the leaf has to be rebuilt. This node class is not one the pass knows how "
            + "to rebuild. Declare the column nullable in the catalog, or use "
            + "PlaceholderPolicy.PlaceholdersAsEmpty, which keeps the declared nullability.");
  }

  /**
   * The aggregate call over the new input, with its result type derived again.
   *
   * <p>The {@code groupCount} overload is deprecated in Calcite 1.42 in favour of one whose tenth
   * parameter is a boolean of undocumented meaning; deriving the type from the group count is
   * exactly what is wanted here — {@code MIN(x)} over a widened column becomes nullable and over a
   * grouped one does not — so the deprecated spelling is the one that says what it does.
   */
  @SuppressWarnings("deprecation")
  private static AggregateCall retype(AggregateCall call, RelNode input, int groupCount) {
    return AggregateCall.create(
        call.getAggregation(),
        call.isDistinct(),
        call.isApproximate(),
        call.ignoreNulls(),
        call.rexList,
        call.getArgList(),
        call.filterArg,
        call.distinctKeys,
        call.collation,
        groupCount,
        input,
        null,
        call.name);
  }

  /** Whether two row types differ, and — when they do — that they differ only in nullability. */
  static boolean differs(RelDataType before, RelDataType after) {
    if (before == after) {
      return false;
    }
    if (!sameSansNullability(before, after)) {
      throw new IllegalStateException(
          "the entitlement pass changed a row type's kind, which it must never do: "
              + before.getFullTypeString()
              + " became "
              + after.getFullTypeString());
    }
    return true;
  }

  /**
   * Field for field, type for type, ignoring nullability — and ignoring names.
   *
   * <p>{@code SqlTypeUtil.equalSansNullability} strips the <em>outer</em> nullability only, so on a
   * record type it still compares the fields with theirs, which is precisely the difference the pass
   * introduces. This walks the fields instead.
   *
   * <p>Names are not part of a row type's kind here. Rebuilding a node re-derives its row type, and
   * some nodes derive a name where the statement had supplied one — a {@code Correlate} over an
   * {@code Uncollect} calls the flattened column {@code EXPR$0} rather than the {@code AS u(x)} the
   * writer gave it. Refusing that refused a correct plan, and the pipeline's root projection puts
   * the statement's own validated names back on the output either way; what must never change is a
   * field's <em>type</em>, which is what the executor closes its kernels over.
   */
  private static boolean sameSansNullability(RelDataType before, RelDataType after) {
    if (!before.isStruct() || !after.isStruct()) {
      return SqlTypeUtil.equalSansNullability(before, after);
    }
    List<RelDataTypeField> left = before.getFieldList();
    List<RelDataTypeField> right = after.getFieldList();
    if (left.size() != right.size()) {
      return false;
    }
    for (int i = 0; i < left.size(); i++) {
      if (!sameSansNullability(left.get(i).getType(), right.get(i).getType())) {
        return false;
      }
    }
    return true;
  }

  private static List<RelDataTypeField> concatenatedFields(List<RelNode> inputs) {
    if (inputs.size() == 1) {
      return inputs.get(0).getRowType().getFieldList();
    }
    List<RelDataTypeField> fields = new ArrayList<>();
    for (RelNode input : inputs) {
      fields.addAll(input.getRowType().getFieldList());
    }
    return fields;
  }

  /** Every {@link RexInputRef} re-declared with the type its field now has. */
  private static final class InputRefRetyper extends RexShuttle {
    private final List<RelDataTypeField> fields;
    private final RexBuilder rexBuilder;

    InputRefRetyper(List<RelDataTypeField> fields, RexBuilder rexBuilder) {
      this.fields = fields;
      this.rexBuilder = rexBuilder;
    }

    @Override
    public RexNode visitInputRef(RexInputRef ref) {
      int index = ref.getIndex();
      if (index >= fields.size()) {
        return ref;
      }
      RelDataType type = fields.get(index).getType();
      return type == ref.getType() ? ref : rexBuilder.makeInputRef(type, index);
    }
  }

  /** A cluster's builder, for callers that have only the node. */
  static RexBuilder rexBuilder(RelOptCluster cluster) {
    return cluster.getRexBuilder();
  }
}

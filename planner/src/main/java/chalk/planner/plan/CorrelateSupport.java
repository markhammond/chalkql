package chalk.planner.plan;

import chalk.planner.UnsupportedFeatureException;
import chalk.planner.plan.rules.ChalkUnnestRule;
import java.util.ArrayList;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Set;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.core.Aggregate;
import org.apache.calcite.rel.core.Correlate;
import org.apache.calcite.rel.core.Sort;
import org.apache.calcite.rel.type.RelDataTypeField;
import org.apache.calcite.rex.RexCorrelVariable;
import org.apache.calcite.rex.RexFieldAccess;
import org.apache.calcite.rex.RexNode;
import org.apache.calcite.rex.RexShuttle;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * D67 — <b>decorrelate first, always</b>. A {@code Correlate} that survives decorrelation is
 * reported as {@code UNSUPPORTED} with a message naming the shape, never planned as a per-row
 * re-execution of its right side ({@code 14-windows-ii.md} §8).
 *
 * <p>That policy is federation-safe, which is the reason for it: a correlate operator against a
 * remote source is one round trip per left row, and rev 3 M5 calls that a hang rather than a query.
 *
 * <p>The one correlate that is <em>not</em> a failure is {@code UNNEST}: Calcite models a lateral
 * {@code UNNEST(t.xs)} as a correlate over an {@code Uncollect}, the decorrelator rightly leaves it
 * alone, and {@link ChalkUnnestRule} turns it into one flatten operator (D66). So this check asks
 * that rule first.
 */
public final class CorrelateSupport {
  private CorrelateSupport() {}

  /** Walks the tree and throws for every correlate that is not an {@code UNNEST}. */
  public static void check(RelNode rel) {
    if (rel instanceof Correlate correlate && ChalkUnnestRule.matchLogical(correlate) == null) {
      throw new UnsupportedFeatureException(
          "correlated subquery could not be decorrelated: " + describe(correlate),
          "Chalk decorrelates LATERAL and correlated sub-queries into ordinary joins and never "
              + "re-runs the right side per left row (docs/design/14-windows-ii.md §8). Rewrite the "
              + "sub-query as a join, or as an aggregate joined on the correlation key.");
    }

    for (RelNode input : rel.getInputs()) {
      check(input);
    }
  }

  /**
   * The shape, in the words a reader of the query would use: the join type, the left columns the
   * right side reads, and the operators on the right that made it uncorrelatable.
   *
   * <p>Example: {@code LEFT correlate on symbol with LIMIT and GROUP BY}.
   */
  public static String describe(Correlate correlate) {
    StringBuilder text = new StringBuilder(correlate.getJoinType().name()).append(" correlate");

    List<String> columns = correlatedColumns(correlate);
    if (!columns.isEmpty()) {
      text.append(" on ").append(String.join(", ", columns));
    }

    List<String> features = rightSideFeatures(correlate.getRight());
    if (!features.isEmpty()) {
      text.append(" with ").append(String.join(" and ", features));
    }

    return text.toString();
  }

  /** The names of the left columns the right side reads through the correlation variable. */
  private static List<String> correlatedColumns(Correlate correlate) {
    List<RelDataTypeField> left = correlate.getLeft().getRowType().getFieldList();
    Set<String> names = new LinkedHashSet<>();
    for (int index : correlate.getRequiredColumns()) {
      names.add(index < left.size() ? left.get(index).getName() : "$" + index);
    }

    // requiredColumns is the authority, but a correlate built by hand may leave it empty; fall back
    // to the field accesses the right side actually makes.
    if (names.isEmpty()) {
      collectFieldAccesses(correlate.getRight(), left, names);
    }

    return new ArrayList<>(names);
  }

  private static void collectFieldAccesses(
      RelNode rel, List<RelDataTypeField> left, Set<String> names) {
    rel.accept(
        new RexShuttle() {
          @Override
          public RexNode visitFieldAccess(RexFieldAccess access) {
            if (access.getReferenceExpr() instanceof RexCorrelVariable) {
              int index = access.getField().getIndex();
              names.add(index < left.size() ? left.get(index).getName() : "$" + index);
            }

            return super.visitFieldAccess(access);
          }
        });
    for (RelNode input : rel.getInputs()) {
      collectFieldAccesses(input, left, names);
    }
  }

  /** The operators on the right that a reader would recognise from their own SQL. */
  private static List<String> rightSideFeatures(RelNode right) {
    Set<String> features = new LinkedHashSet<>();
    collectFeatures(right, features);
    return new ArrayList<>(features);
  }

  private static void collectFeatures(@Nullable RelNode rel, Set<String> features) {
    if (rel == null) {
      return;
    }

    if (rel instanceof Sort sort) {
      if (sort.fetch != null) {
        features.add("LIMIT");
      }

      if (sort.offset != null) {
        features.add("OFFSET");
      }

      if (!sort.getCollation().getFieldCollations().isEmpty()) {
        features.add("ORDER BY");
      }
    } else if (rel instanceof Aggregate aggregate) {
      features.add(aggregate.getGroupSet().isEmpty() ? "an aggregate" : "GROUP BY");
    }

    for (RelNode input : rel.getInputs()) {
      collectFeatures(input, features);
    }
  }
}

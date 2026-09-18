package chalk.planner.entitlement;

import chalk.ir.v1.Disclosure;
import com.google.common.collect.ImmutableList;
import java.util.ArrayList;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Set;
import org.apache.calcite.plan.RelOptUtil;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.type.RelDataType;
import org.apache.calcite.rex.RexNode;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * The oracle of docs/design/16-entitlements.md §3.12 (D207): what this principal's policy folded to,
 * table by table and column by column, in the policy's own terms rather than the plan's.
 *
 * <p>It is built by the pass that enforces the policy, from the same folded expressions, so it cannot
 * drift from what runs — which is the whole of its value. A second implementation that agreed with
 * the plan by construction would say nothing; one that disagreed would be a third opinion nobody
 * could adjudicate.
 *
 * <p>Nothing here executes and nothing here needs a plan: a caller asks what would happen and is
 * told, which is what a host reaching for a debugger actually wants when a row is missing.
 */
public final class PolicyExplain {
  private PolicyExplain() {}

  /** One entitled table, as the policy resolved it. */
  public record Table(
      String schema,
      String table,
      String rowPredicate,
      ImmutableList<Column> columns,
      @Nullable RexNode folded,
      ImmutableList<Parent> parents,
      ImmutableList<Path> paths) {}

  /**
   * One declared path this table holds a tenancy along (D265 §6): which perspective answered for a
   * row, the route it took, whether the chain was built at all, and what this principal sees of the
   * key the <em>statement's</em> own join would use.
   */
  public record Path(
      String kind,
      ImmutableList<Step> steps,
      String endpointSchema,
      String endpointTable,
      boolean elided,
      boolean dropped,
      String endpointPredicate,
      String keyDisclosure) {}

  /** One step of a {@link Path}, by the names the policy wrote rather than by ordinal. */
  public record Step(
      String schema, String table, String fromColumn, String toColumn, boolean toChild) {}

  /**
   * One parent this table's visibility derived through (§3.13, D230): which relationship answered
   * for a row, on which key, whether the join was left out of the plan, and what this principal
   * sees of the key the <em>statement's</em> own join would use.
   */
  public record Parent(
      String schema,
      String table,
      String column,
      String parentColumn,
      boolean elided,
      String keyDisclosure) {}

  /** One column of one entitled table, as the policy resolved it. */
  public record Column(
      String name, String disclosure, String mask, String placeholder, int minGroupSize) {}

  /**
   * The explanation for one entitled leaf.
   *
   * @param rowType the table's own row type, for the column names
   */
  static Table of(
      EntitlementPass.LeafFold fold,
      RelDataType rowType,
      PolicyOptions options) {
    List<Column> columns = new ArrayList<>(rowType.getFieldCount());
    for (int i = 0; i < rowType.getFieldCount(); i++) {
      DescriptorExpressions.Column entitled = fold.descriptor().column(i);
      columns.add(
          new Column(
              rowType.getFieldList().get(i).getName(),
              disclosure(fold, i, entitled),
              entitled == null || entitled.mask() == null ? "" : entitled.mask().toString(),
              placeholder(fold, i, entitled),
              entitled == null ? 0 : options.minGroupSize(entitled.minGroupSize())));
    }

    return new Table(
        fold.table().schemaName(),
        fold.table().tableName(),
        fold.predicate() == null ? "" : fold.predicate().toString(),
        ImmutableList.copyOf(columns),
        fold.predicate(),
        parents(fold, rowType),
        paths(fold));
  }

  /** The paths, as the pass resolved and folded them for this principal (D265 §6). */
  private static ImmutableList<Path> paths(EntitlementPass.LeafFold fold) {
    List<EntitlementPass.ExplainedPath> resolved = fold.explainedPaths();
    if (resolved.isEmpty()) {
      return ImmutableList.of();
    }
    ImmutableList.Builder<Path> paths = ImmutableList.builder();
    for (EntitlementPass.ExplainedPath path : resolved) {
      ImmutableList.Builder<Step> steps = ImmutableList.builder();
      for (EntitlementPass.ExplainedStep step : path.steps()) {
        steps.add(
            new Step(
                step.schema(), step.table(), step.fromColumn(), step.toColumn(), step.toChild()));
      }
      paths.add(
          new Path(
              path.kind(),
              steps.build(),
              path.endpointSchema(),
              path.endpointTable(),
              path.elided(),
              path.dropped(),
              path.endpointPredicate(),
              fold.map().of(path.keyColumn()).name()));
    }
    return paths.build();
  }

  /** The parents, as the pass resolved and folded them for this principal (§3.13, D230). */
  private static ImmutableList<Parent> parents(
      EntitlementPass.LeafFold fold, RelDataType rowType) {
    List<EntitlementPass.ExplainedParent> resolved = fold.explainedParents();
    if (resolved.isEmpty()) {
      return ImmutableList.of();
    }
    ImmutableList.Builder<Parent> parents = ImmutableList.builder();
    for (EntitlementPass.ExplainedParent parent : resolved) {
      parents.add(
          new Parent(
              parent.schema(),
              parent.table(),
              rowType.getFieldList().get(parent.column()).getName(),
              parent.parentColumnName(),
              parent.elided(),
              fold.map().of(parent.column()).name()));
    }
    return parents.build();
  }

  /**
   * What this column discloses: the constant name where the fold settled it, and otherwise the
   * folded rule conditions in order — which is the answer to "why is this row masked and that one
   * not", written in the terms the descriptor was.
   */
  private static String disclosure(
      EntitlementPass.LeafFold fold, int column, DescriptorExpressions.@Nullable Column entitled) {
    Disclosed outcome = fold.map().of(column);
    if (fold.statistical().contains(column)) {
      return "AGGREGATE (statistical: raw under the group-size floor)";
    }
    if (outcome != Disclosed.PER_ROW || entitled == null) {
      return outcome.name();
    }

    EntitlementPass.Reachable reachable = fold.reachable().get(column);
    StringBuilder text = new StringBuilder();
    for (int i = 0; i < reachable.rules().size(); i++) {
      int rule = reachable.rules().get(i);
      text.append("WHEN ")
          .append(reachable.conditions().get(i))
          .append(" THEN ")
          .append(name(entitled.rules().get(rule).getThen()))
          .append("; ");
    }
    if (reachable.otherwiseReachable()) {
      text.append("ELSE ").append(name(reachable.otherwise()));
    } else if (text.length() >= 2) {
      text.setLength(text.length() - 2);
    }
    return text.toString();
  }

  /**
   * What stands in where this column is redacted: the column's own placeholder, and — where a
   * reachable rule states one of its own (D224) — those rules in order, in the same {@code WHEN …
   * THEN …} shape the disclosure above uses, so a reader meets one vocabulary.
   *
   * <p>Empty means neither, and the request's {@code PlaceholderPolicy} is then the answer, which is
   * a property of the request rather than of the column and is reported there.
   */
  private static String placeholder(
      EntitlementPass.LeafFold fold, int column, DescriptorExpressions.@Nullable Column entitled) {
    if (entitled == null) {
      return "";
    }
    String columnPlaceholder = entitled.placeholder() == null ? "" : entitled.placeholder().toString();
    EntitlementPass.Reachable reachable = fold.reachable().get(column);
    if (reachable == null) {
      return columnPlaceholder;
    }

    StringBuilder text = new StringBuilder();
    for (int i = 0; i < reachable.rules().size(); i++) {
      int rule = reachable.rules().get(i);
      if (entitled.rules().get(rule).getPlaceholder().isBlank()) {
        continue;
      }
      text.append("WHEN ")
          .append(reachable.conditions().get(i))
          .append(" THEN ")
          .append(entitled.placeholderOf(rule))
          .append("; ");
    }
    if (text.isEmpty()) {
      return columnPlaceholder;
    }
    return columnPlaceholder.isEmpty()
        ? text.substring(0, text.length() - 2)
        : text.append("ELSE ").append(columnPlaceholder).toString();
  }

  private static String name(Disclosure disclosure) {
    return switch (disclosure) {
      case DISCLOSURE_FULL -> "FULL";
      case DISCLOSURE_MASKED -> "MASKED";
      case DISCLOSURE_AGGREGATE_ONLY -> "AGGREGATE";
      case DISCLOSURE_TEST -> "TESTED";
      default -> "REDACTED";
    };
  }

  /**
   * The conjuncts of a table's folded row predicate this plan evaluates <em>locally</em>: the ones
   * no pushed filter over that table carries.
   *
   * <p>Empty where the predicate pushed whole, and the whole predicate where nothing pushed at all —
   * which is what a local table, a {@code LOCAL} table and a source that does not take the shape all
   * look like, and the report's {@code row_predicate_pushed} says which.
   */
  public static List<String> residual(RelNode physical, Table table) {
    if (table.folded() == null) {
      return List.of();
    }
    Set<String> pushed = new LinkedHashSet<>();
    collectPushed(physical, table, pushed);

    List<String> residual = new ArrayList<>();
    for (RexNode conjunct : RelOptUtil.conjunctions(table.folded())) {
      if (!pushed.contains(conjunct.toString())) {
        residual.add(conjunct.toString());
      }
    }
    return residual;
  }

  private static void collectPushed(RelNode rel, Table table, Set<String> into) {
    if (rel instanceof chalk.planner.plan.rel.SourceRels.SourceFilter filter
        && reads(filter.getInput(), table)) {
      for (RexNode conjunct : RelOptUtil.conjunctions(filter.getCondition())) {
        into.add(conjunct.toString());
      }
    }
    for (RelNode input : rel.getInputs()) {
      collectPushed(input, table, into);
    }
  }

  private static boolean reads(RelNode rel, Table table) {
    if (rel instanceof org.apache.calcite.rel.core.TableScan scan) {
      DisclosureMap map = EntitledRelOptTable.disclosureOf(scan.getTable());
      return map != null
          && map.schema().equals(table.schema())
          && map.table().equals(table.table());
    }
    for (RelNode input : rel.getInputs()) {
      if (reads(input, table)) {
        return true;
      }
    }
    return false;
  }
}

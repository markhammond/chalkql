package chalk.planner.entitlement;

import chalk.planner.catalog.ChalkTable;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.core.Aggregate;
import org.apache.calcite.rel.core.Filter;
import org.apache.calcite.rel.core.Join;
import org.apache.calcite.rel.core.Project;
import org.apache.calcite.rel.core.TableScan;
import org.apache.calcite.rex.RexCall;
import org.apache.calcite.rex.RexInputRef;
import org.apache.calcite.rex.RexNode;
import org.apache.calcite.rex.RexOver;
import org.apache.calcite.rex.RexShuttle;
import org.apache.calcite.rex.RexSubQuery;
import org.apache.calcite.rex.RexVisitorImpl;
import org.apache.calcite.sql.SqlKind;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * Whether a statement is one a {@code statistical} column may be read raw in
 * (docs/design/16-entitlements.md §3.4, D203).
 *
 * <p>The opt-in trades the mask for query-set-size control, and that trade only holds under three
 * conditions, which are checked here rather than assumed.
 *
 * <ul>
 *   <li><b>Every output is an aggregate or a group key.</b> One row-level column beside a raw
 *       predicate is a per-row membership test with no k at all: {@code SELECT id FROM members WHERE
 *       first_name = 'Tara'} is the raw value, one row at a time. So no scan may reach the root
 *       except through an {@code Aggregate}.
 *   <li><b>No window.</b> A window's partition can be one row, which is the same oracle wearing a
 *       different operator.
 *   <li><b>No pinning or excluding of an individual.</b> A predicate on a declared unique key —
 *       {@code id = ?}, {@code id &lt;&gt; ?}, {@code id NOT IN (…)} — turns an aggregate over a
 *       population into an aggregate over one person, or over everyone but them, and the difference
 *       of the two is that person.
 * </ul>
 *
 * <p>What remains possible, and is the documented limit rather than a defect: trackers built across
 * statements out of quasi-identifiers. This is query-set-size control, not differential privacy, and
 * the README says so.
 */
final class StatisticalScope {
  private StatisticalScope() {}

  /**
   * Whether every path from the root to a scan passes through an {@code Aggregate} and no window
   * function appears anywhere.
   */
  static boolean qualifies(RelNode root) {
    return !hasWindow(root) && underAggregate(root);
  }

  private static boolean underAggregate(RelNode rel) {
    if (rel instanceof Aggregate) {
      return true;
    }
    if (rel instanceof TableScan) {
      return false;
    }
    for (RelNode input : rel.getInputs()) {
      if (!underAggregate(input)) {
        return false;
      }
    }

    // A sub-query's relation reaches the result through whatever reads it, so it is held to the
    // same rule: a correlated scalar sub-query over a row is a row-level read by another name.
    boolean[] all = {true};
    rel.accept(
        new RexShuttle() {
          @Override
          public RexNode visitSubQuery(RexSubQuery subQuery) {
            all[0] &= underAggregate(subQuery.rel);
            return super.visitSubQuery(subQuery);
          }
        });
    return all[0];
  }

  private static boolean hasWindow(RelNode rel) {
    boolean[] found = {false};
    rel.accept(
        new RexShuttle() {
          @Override
          public RexNode visitOver(RexOver over) {
            found[0] = true;
            return over;
          }

          @Override
          public RexNode visitSubQuery(RexSubQuery subQuery) {
            found[0] |= hasWindow(subQuery.rel);
            return super.visitSubQuery(subQuery);
          }
        });
    if (found[0] || rel instanceof org.apache.calcite.rel.core.Window) {
      return true;
    }
    for (RelNode input : rel.getInputs()) {
      if (hasWindow(input)) {
        return true;
      }
    }
    return false;
  }

  // ------------------------------------------------------------------ no window

  /**
   * Refuses a window function that reads a {@code statistical} column — as an argument, a partition
   * key or an order key — naming the column (D203, F43).
   *
   * <p>D203 forbids three things in a statement that reads a statistical column raw, and a window
   * is one of them: its partition can be one row, so the group suppression the opt-in trades the
   * mask for cannot be enforced on it. A statement holding a window does not qualify as a
   * statistical statement at all, so what would otherwise happen is that the column quietly keeps
   * its mask — safe, and silent, where the design says refuse. This is the refusal.
   *
   * <p>Only a window that actually reads such a column: a window over some other column of the same
   * table is an ordinary window and stays one. The origins are read on the tree the pass received,
   * where every column is still a plain scan column and Calcite's column-origin metadata answers
   * cleanly; after the rewrite a masked column is a {@code CASE} with no single origin at all.
   */
  static void refuseWindow(RelNode root, Map<TableScan, EntitlementPass.LeafFold> folds) {
    Map<String, java.util.Set<Integer>> declared = new LinkedHashMap<>();
    for (EntitlementPass.LeafFold fold : folds.values()) {
      if (!fold.declaredStatistical().isEmpty()) {
        declared
            .computeIfAbsent(
                fold.table().schemaName() + "." + fold.table().tableName(),
                name -> new java.util.LinkedHashSet<>())
            .addAll(fold.declaredStatistical());
      }
    }
    if (declared.isEmpty()) {
      return;
    }
    windows(root, declared, folds);
  }

  private static void windows(
      RelNode rel,
      Map<String, java.util.Set<Integer>> declared,
      Map<TableScan, EntitlementPass.LeafFold> folds) {
    for (RelNode input : rel.getInputs()) {
      windows(input, declared, folds);
    }

    List<RexOver> overs = new java.util.ArrayList<>();
    rel.accept(
        new RexShuttle() {
          @Override
          public RexNode visitOver(RexOver over) {
            overs.add(over);
            return super.visitOver(over);
          }

          @Override
          public RexNode visitSubQuery(RexSubQuery subQuery) {
            windows(subQuery.rel, declared, folds);
            return super.visitSubQuery(subQuery);
          }
        });
    if (overs.isEmpty() || rel.getInputs().size() != 1) {
      return;
    }

    RelNode input = rel.getInput(0);
    org.apache.calcite.rel.metadata.RelMetadataQuery mq = rel.getCluster().getMetadataQuery();
    for (RexOver over : overs) {
      for (int index : reads(over)) {
        if (index >= input.getRowType().getFieldCount()) {
          continue;
        }
        java.util.Set<org.apache.calcite.rel.metadata.RelColumnOrigin> origins =
            mq.getColumnOrigins(input, index);
        if (origins == null) {
          continue;
        }
        for (org.apache.calcite.rel.metadata.RelColumnOrigin origin : origins) {
          String table = String.join(".", origin.getOriginTable().getQualifiedName());
          java.util.Set<Integer> columns = declared.get(table);
          if (columns != null && columns.contains(origin.getOriginColumnOrdinal())) {
            throw windowRefusal(
                table,
                origin
                    .getOriginTable()
                    .getRowType()
                    .getFieldList()
                    .get(origin.getOriginColumnOrdinal())
                    .getName());
          }
        }
      }
    }
  }

  /** Every input column a window function reads: its arguments, its partition and its order keys. */
  private static java.util.Set<Integer> reads(RexOver over) {
    java.util.Set<Integer> indexes = new java.util.LinkedHashSet<>();
    org.apache.calcite.rex.RexVisitorImpl<Void> collect =
        new org.apache.calcite.rex.RexVisitorImpl<Void>(true) {
          @Override
          public Void visitInputRef(RexInputRef ref) {
            indexes.add(ref.getIndex());
            return null;
          }
        };
    for (RexNode operand : over.getOperands()) {
      operand.accept(collect);
    }
    for (RexNode key : over.getWindow().partitionKeys) {
      key.accept(collect);
    }
    for (org.apache.calcite.rex.RexFieldCollation key : over.getWindow().orderKeys) {
      key.left.accept(collect);
    }
    return indexes;
  }

  private static PolicyException windowRefusal(String table, String column) {
    return new PolicyException(
        table
            + "."
            + column
            + " is a statistical column and this statement reads it under a window function."
            + " Statistical access is query-set-size control, and a window's partition can be one"
            + " row, so no suppression can be enforced on it: a window is one of the three things"
            + " the opt-in forbids (docs/design/16-entitlements.md §3.4, D203).");
  }

  // ------------------------------------------------------------------ no pinning

  /**
   * Refuses a predicate that pins or excludes an individual of a table carrying a statistical column
   * — a comparison or membership test on a declared single-column unique key.
   *
   * <p>Run on the tree the pass received, before the leaves are rewritten: afterwards the policy's
   * own row predicate is there too, and a subject grant's {@code id IN (@ctx.…)} reads exactly like
   * the thing this refuses.
   */
  static void refusePinning(RelNode root, Map<TableScan, EntitlementPass.LeafFold> folds) {
    walk(root, folds);
  }

  private static void walk(RelNode rel, Map<TableScan, EntitlementPass.LeafFold> folds) {
    for (RelNode input : rel.getInputs()) {
      walk(input, folds);
    }
    rel.accept(
        new RexShuttle() {
          @Override
          public RexNode visitSubQuery(RexSubQuery subQuery) {
            walk(subQuery.rel, folds);
            return super.visitSubQuery(subQuery);
          }
        });

    if (rel instanceof Filter filter) {
      refuse(filter.getCondition(), keysOf(filter.getInput(), folds));
    } else if (rel instanceof Join join) {
      refuse(join.getCondition(), keysOf(join, folds));
    } else if (rel instanceof Aggregate aggregate) {
      Map<Integer, Key> keys = keysOf(aggregate.getInput(), folds);
      for (org.apache.calcite.rel.core.AggregateCall call : aggregate.getAggCallList()) {
        if (call.filterArg >= 0 && keys.containsKey(call.filterArg)) {
          throw refusal(keys.get(call.filterArg));
        }
      }
      // Grouping by a unique key pins every individual at once: each group is one person, and the
      // aggregate over it is that person's value.
      for (int key : aggregate.getGroupSet()) {
        if (keys.containsKey(key)) {
          throw refusal(keys.get(key));
        }
      }
    }
  }

  /** One unique-key column of a table that carries a statistical column. */
  private record Key(String table, String column) {}

  private static void refuse(@Nullable RexNode condition, Map<Integer, Key> keys) {
    if (condition == null || keys.isEmpty()) {
      return;
    }
    condition.accept(
        new RexVisitorImpl<Void>(true) {
          @Override
          public Void visitCall(RexCall call) {
            SqlKind kind = call.getKind();
            if (kind == SqlKind.EQUALS
                || kind == SqlKind.NOT_EQUALS
                || kind == SqlKind.IN
                || kind == SqlKind.NOT_IN
                || kind == SqlKind.SEARCH) {
              for (RexNode operand : call.getOperands()) {
                if (operand instanceof RexInputRef ref && keys.containsKey(ref.getIndex())) {
                  throw refusal(keys.get(ref.getIndex()));
                }
              }
            }
            return super.visitCall(call);
          }
        });
  }

  private static PolicyException refusal(Key key) {
    return new PolicyException(
        key.table()
            + "."
            + key.column()
            + " is a declared unique key of a table with a statistical column, and this statement"
            + " compares or groups by it. Statistical access is query-set-size control: an aggregate over one"
            + " person, or over everyone but them, is that person, and the difference of two such"
            + " statements is their value. Pinning or excluding an individual is refused in a"
            + " statement that reads a statistical column raw"
            + " (docs/design/16-entitlements.md §3.4, D203).");
  }

  /**
   * Which of {@code rel}'s output columns are single-column unique keys of a table carrying a
   * statistical column. Structural, through the shapes the converted tree has below a filter: a
   * bare reference in a projection, a filter, a join. Anything else stops the walk, which is safe —
   * a key that has been through an aggregate or a set operation is no longer one row's identifier.
   */
  private static Map<Integer, Key> keysOf(
      RelNode rel, Map<TableScan, EntitlementPass.LeafFold> folds) {
    if (rel instanceof TableScan scan) {
      EntitlementPass.LeafFold fold = folds.get(scan);
      if (fold == null || fold.statistical().isEmpty()) {
        return Map.of();
      }
      ChalkTable table = fold.table();
      List<Integer> projection = EntitlementPass.projectionOf(scan);
      Map<Integer, Key> keys = new LinkedHashMap<>();
      for (chalk.ir.v1.UniqueKey key : table.descriptor().getUniqueKeysList()) {
        if (key.getColumnsCount() != 1) {
          continue;
        }
        int column = key.getColumns(0);
        int at = projection.indexOf(column);
        if (at >= 0) {
          keys.put(
              at,
              new Key(
                  table.schemaName() + "." + table.tableName(),
                  table.descriptor().getColumns(column).getName()));
        }
      }
      return keys;
    }

    if (rel instanceof Filter filter) {
      return keysOf(filter.getInput(), folds);
    }

    if (rel instanceof Project project) {
      Map<Integer, Key> in = keysOf(project.getInput(), folds);
      Map<Integer, Key> out = new LinkedHashMap<>();
      List<RexNode> exprs = project.getProjects();
      for (int i = 0; i < exprs.size(); i++) {
        if (exprs.get(i) instanceof RexInputRef ref && in.containsKey(ref.getIndex())) {
          out.put(i, in.get(ref.getIndex()));
        }
      }
      return out;
    }

    if (rel instanceof Join join) {
      Map<Integer, Key> out = new LinkedHashMap<>(keysOf(join.getLeft(), folds));
      int offset = join.getLeft().getRowType().getFieldCount();
      keysOf(join.getRight(), folds).forEach((index, key) -> out.put(offset + index, key));
      return out;
    }

    return Map.of();
  }
}

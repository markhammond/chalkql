package chalk.planner.entitlement;

import chalk.planner.plan.rel.SourceRels;
import chalk.planner.plan.rel.SourceScan;
import chalk.planner.rpc.v1.ReportedDisclosure;
import com.google.common.collect.ImmutableList;
import java.util.ArrayList;
import java.util.HashSet;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Set;
import org.apache.calcite.plan.RelOptTable;
import org.apache.calcite.rel.RelNode;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * What the caller is told, per output column and per entitled table
 * (docs/design/16-entitlements.md §3.12, D202, D207).
 *
 * <p>A column's reported disclosure is the <b>meet</b> over its origins in the disclosure maps —
 * {@code Full} when every origin is {@code FULL} and for every column of an unentitled table,
 * {@code Masked} when any origin is masked and none worse, {@code Undisclosed} when any is redacted,
 * {@code PerRow} when any is decided per row, and {@code Aggregate} for a guarded population
 * aggregate. The meet is conservative by rule: a derived column with any redacted origin is never
 * {@code Full}.
 *
 * <p>Origins come from {@code RelMetadataQuery.getColumnOrigins}, and every scan-derived node
 * carries the table handle the pass wrapped, so a leaf that has become an index lookup or the read
 * inside a pushed plan still answers for itself.
 */
public final class DisclosureReport {
  private DisclosureReport() {}

  /** The reported disclosure of each of {@code columns}' positions, from the flow of §3.12. */
  public static ImmutableList<ReportedDisclosure> columns(
      List<Integer> columns, List<Disclosed> flow) {
    ImmutableList.Builder<ReportedDisclosure> reported = ImmutableList.builder();
    for (int column : columns) {
      reported.add(
          report(column >= 0 && column < flow.size() ? flow.get(column) : Disclosed.FULL));
    }
    return reported.build();
  }

  private static ReportedDisclosure report(Disclosed disclosed) {
    return switch (disclosed) {
      case FULL -> ReportedDisclosure.REPORTED_DISCLOSURE_FULL;
      case MASKED -> ReportedDisclosure.REPORTED_DISCLOSURE_MASKED;
      case AGGREGATE -> ReportedDisclosure.REPORTED_DISCLOSURE_AGGREGATE;
      case PER_ROW -> ReportedDisclosure.REPORTED_DISCLOSURE_PER_ROW;
      case TESTED -> ReportedDisclosure.REPORTED_DISCLOSURE_TESTED;
      case REDACTED -> ReportedDisclosure.REPORTED_DISCLOSURE_REDACTED;
    };
  }

  /**
   * One entry per entitled table the plan touches, in the order the pass met them, merged across a
   * table's several occurrences: the visibility is the widest of them, since a table read twice is
   * one table to the caller.
   */
  public static ImmutableList<DisclosureMap> tables(List<DisclosureMap> leaves) {
    Map<String, DisclosureMap> byName = new LinkedHashMap<>();
    for (DisclosureMap leaf : leaves) {
      byName.merge(
          leaf.qualifiedName(),
          leaf,
          // The widest visibility wins, since a table read twice is one table to the caller — but a
          // contradiction on any occurrence is one the caller has to hear about.
          (a, b) -> {
            DisclosureMap widest = a.visibility().compareTo(b.visibility()) >= 0 ? a : b;
            if (widest.contradiction().isEmpty()) {
              String other = a.contradiction().isEmpty() ? b.contradiction() : a.contradiction();
              if (!other.isEmpty()) {
                widest.contradicting(other);
              }
            }
            // Two occurrences of one table are one table to the caller, and what was tested of it is
            // everything either occurrence tested (D261).
            widest.testing(merged(a.tested(), b.tested()));
            return widest;
          });
    }
    return ImmutableList.copyOf(byName.values());
  }

  /** Two occurrences' tested columns, by column, each column's shapes in the order first met. */
  private static List<DisclosureMap.Tested> merged(
      List<DisclosureMap.Tested> left, List<DisclosureMap.Tested> right) {
    if (left.isEmpty()) {
      return right;
    }
    if (right.isEmpty()) {
      return left;
    }
    Map<String, Set<String>> byColumn = new LinkedHashMap<>();
    for (List<DisclosureMap.Tested> side : List.of(left, right)) {
      for (DisclosureMap.Tested tested : side) {
        byColumn.computeIfAbsent(tested.column(), key -> new java.util.LinkedHashSet<>())
            .addAll(tested.shapes());
      }
    }
    List<DisclosureMap.Tested> out = new ArrayList<>(byColumn.size());
    byColumn.forEach(
        (column, shapes) ->
            out.add(new DisclosureMap.Tested(column, ImmutableList.copyOf(shapes))));
    return out;
  }

  /**
   * The entitled tables whose folded row predicate actually reached their source (§3.7).
   *
   * <p>Honestly empty for a local table, which has no source to push into, and for a source that
   * takes no queries — which is every table of this run's fixture. The question asked of the
   * physical tree is the only one worth asking: did a pushed rel of this table end up carrying a
   * filter.
   */
  public static Set<String> pushedRowPredicates(RelNode physical) {
    Set<String> pushed = new HashSet<>();
    collect(physical, pushed);
    return pushed;
  }

  /**
   * The same, with a {@code through} child counted where its parent's predicate reached the source
   * <em>and it did so inside the same remote query</em> (§3.13, D229).
   *
   * <p>A child entitled through a parent has no predicate of its own to push: what restricts it is
   * the join. So the honest reading of {@code row_predicate_pushed} for such a table is that the
   * join went to the source with the parent's own predicate in it — one remote query, and the rows
   * the source is asked for are the visible ones. Where §3.8 keeps the child's projection local — a
   * mask does that — the join stays local too and the flag is honestly false, exactly as it is for
   * a shape a source cannot take.
   */
  public static Set<String> pushedRowPredicates(
      RelNode physical, java.util.List<TaintCheck.ThroughEvidence> throughJoins) {
    Set<String> pushed = pushedRowPredicates(physical);
    if (throughJoins.isEmpty()) {
      return pushed;
    }

    java.util.Map<String, Boolean> byChild = new java.util.LinkedHashMap<>();
    for (TaintCheck.ThroughEvidence join : throughJoins) {
      // A declared path has no parent predicate to look for: what restricts the target is the
      // marker, and the marker is the whole chain — the joins down to the endpoint and the endpoint
      // predicate — under the key set's own join. So the honest reading of `row_predicate_pushed`
      // is that that join is one the source itself runs, which is only possible when everything
      // below it went with it (D265 §5, D229).
      boolean here =
          join.rawParent()
              ? joinedInsideASource(physical, join, false)
              : pushed.contains(join.parentTable()) && joinedInsideASource(physical, join, false);
      byChild.merge(join.childTable(), here, (a, b) -> a && b);
    }
    for (java.util.Map.Entry<String, Boolean> child : byChild.entrySet()) {
      if (child.getValue()) {
        pushed.add(child.getKey());
      }
    }
    return pushed;
  }

  /** Whether the join establishing this parent is one the source itself runs. */
  private static boolean joinedInsideASource(
      RelNode rel, TaintCheck.ThroughEvidence join, boolean insideSource) {
    boolean here = insideSource || rel instanceof chalk.planner.plan.rel.SourceRel;
    if (here
        && rel instanceof SourceRels.SourceJoin node
        && reads(node.getLeft(), join.childTable(), false)
        && reads(node.getRight(), join.parentTable(), join.rawParent())) {
      return true;
    }
    for (RelNode input : rel.getInputs()) {
      if (joinedInsideASource(input, join, here)) {
        return true;
      }
    }
    return false;
  }

  private static boolean reads(RelNode rel, String table, boolean raw) {
    return table.equals(raw ? rawTableUnder(rel) : entitledTableUnder(rel));
  }

  /**
   * The table a path's chain is built over, which carries no entitled handle at all: the mechanism
   * reads it raw and discloses none of it (D265 §2).
   */
  private static @Nullable String rawTableUnder(RelNode rel) {
    if (rel instanceof org.apache.calcite.rel.core.TableScan scan) {
      chalk.planner.catalog.ChalkTable table =
          scan.getTable().unwrap(chalk.planner.catalog.ChalkTable.class);
      return table == null ? null : table.schemaName() + "." + table.tableName();
    }
    for (RelNode input : rel.getInputs()) {
      String found = rawTableUnder(input);
      if (found != null) {
        return found;
      }
    }
    return null;
  }

  private static void collect(RelNode rel, Set<String> pushed) {
    if (rel instanceof SourceRels.SourceFilter filter) {
      String table = entitledTableUnder(filter.getInput());
      if (table != null) {
        pushed.add(table);
      }
    } else if (rel instanceof SourceScan scan) {
      // A scan the boundary rendered with its own predicate is a filter above it; a bare scan is
      // not a pushed predicate, so nothing is claimed here.
      RelOptTable table = scan.getTable();
      DisclosureMap map = EntitledRelOptTable.disclosureOf(table);
      if (map == null) {
        return;
      }
    }
    for (RelNode input : rel.getInputs()) {
      collect(input, pushed);
    }
  }

  private static String entitledTableUnder(RelNode rel) {
    if (rel instanceof org.apache.calcite.rel.core.TableScan scan) {
      DisclosureMap map = EntitledRelOptTable.disclosureOf(scan.getTable());
      return map == null ? null : map.qualifiedName();
    }
    for (RelNode input : rel.getInputs()) {
      String found = entitledTableUnder(input);
      if (found != null) {
        return found;
      }
    }
    return null;
  }

  /** The names a plan still needs bound at execution: none under prepare-time binding (§2). */
  public static List<String> requiredRelations(RelNode physical) {
    List<String> names = new ArrayList<>();
    collectContextTables(physical, names);
    return names;
  }

  private static void collectContextTables(RelNode rel, List<String> names) {
    if (rel instanceof chalk.planner.plan.rel.ChalkContextScan scan
        && !names.contains(scan.contextName())) {
      names.add(scan.contextName());
    }
    for (RelNode input : rel.getInputs()) {
      collectContextTables(input, names);
    }
  }

  /**
   * The context scalars a plan reads at execution: empty under prepare-time binding, where each of
   * them was folded into a literal, and one per {@link BoundParam} the plan carries otherwise (§2,
   * D209).
   */
  public static List<String> requiredScalars(RelNode physical) {
    List<String> names = new ArrayList<>();
    collectBoundScalars(physical, names);
    return names;
  }

  private static void collectBoundScalars(RelNode rel, List<String> names) {
    rel.accept(
        new org.apache.calcite.rex.RexShuttle() {
          @Override
          public org.apache.calcite.rex.RexNode visitDynamicParam(
              org.apache.calcite.rex.RexDynamicParam param) {
            if (param instanceof BoundParam bound && !names.contains(bound.key())) {
              names.add(bound.key());
            }
            return param;
          }

          @Override
          public org.apache.calcite.rex.RexNode visitSubQuery(
              org.apache.calcite.rex.RexSubQuery subQuery) {
            collectBoundScalars(subQuery.rel, names);
            return super.visitSubQuery(subQuery);
          }
        });
    for (RelNode input : rel.getInputs()) {
      collectBoundScalars(input, names);
    }
  }
}

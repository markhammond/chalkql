package chalk.planner.entitlement;

import chalk.planner.plan.ChalkKeySet;
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
import org.apache.calcite.util.ImmutableIntList;
import org.apache.calcite.plan.RelOptPredicateList;
import org.apache.calcite.plan.RelOptTable;
import org.apache.calcite.plan.RelOptUtil;
import org.apache.calcite.plan.volcano.RelSubset;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.core.TableScan;
import org.apache.calcite.rex.RexBuilder;
import org.apache.calcite.rex.RexCall;
import org.apache.calcite.rex.RexInputRef;
import org.apache.calcite.rex.RexNode;
import org.apache.calcite.rex.RexShuttle;
import org.apache.calcite.rex.RexSimplify;
import org.apache.calcite.rex.RexSubQuery;
import org.apache.calcite.rex.RexUnknownAs;
import org.apache.calcite.rex.RexUtil;
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
   * physical tree is whether a pushed filter over the table's scan carries <em>every conjunct of the
   * table's own row predicate</em>, as the pass recorded it (F146): a statement's conjunct that
   * travels beside a membership kept at home is not the row predicate reaching the source, and the
   * flag this feeds — and {@code PUSHDOWN_REQUIRED}, which trusts it — must not say so. A conjunct
   * that reads a context relation reaches the source as a key set over the same columns. A table the
   * pass recorded no predicate for — folded away, or a trusted source — is judged as before, by any
   * filter the source runs over it.
   */
  public static Set<String> pushedRowPredicates(RelNode physical, Map<String, RexNode> rowPredicates) {
    Set<String> pushed = new HashSet<>();
    collect(physical, rowPredicates, physical.getCluster().getRexBuilder(), pushed);
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
      RelNode physical,
      Map<String, RexNode> rowPredicates,
      java.util.List<TaintCheck.ThroughEvidence> throughJoins) {
    Set<String> pushed = pushedRowPredicates(physical, rowPredicates);
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

  private static void collect(
      RelNode rel, Map<String, RexNode> rowPredicates, RexBuilder rexBuilder, Set<String> pushed) {
    if (rel instanceof SourceRels.SourceFilter filter) {
      TableScan scan = entitledScanUnder(filter.getInput());
      if (scan != null) {
        String table = EntitledRelOptTable.disclosureOf(scan.getTable()).qualifiedName();
        RexNode required = rowPredicates.get(table);
        if (required == null || carries(filter.getCondition(), scan, required, rexBuilder)) {
          pushed.add(table);
        }
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
      collect(input, rowPredicates, rexBuilder, pushed);
    }
  }

  /**
   * Whether a pushed filter's condition carries every conjunct of a table's recorded row predicate
   * (F146). Both sides are read in the table's own column numbering — the filter's references go
   * through the scan's projection — and both are simplified and their {@code SEARCH} arguments
   * expanded the same way before their text is compared, so the shape the optimiser left a conjunct
   * in does not decide the answer. A recorded conjunct that reads a context relation is carried by a
   * key set over the same columns, which is the only way such a list reaches a source.
   */
  private static boolean carries(RexNode condition, TableScan scan, RexNode required, RexBuilder rexBuilder) {
    List<Integer> projection =
        scan instanceof SourceScan source
            ? source.projection()
            : ImmutableIntList.identity(scan.getRowType().getFieldCount());
    List<RexNode> carried = new ArrayList<>();
    for (RexNode conjunct : RelOptUtil.conjunctions(condition)) {
      carried.add(normalise(remap(conjunct, projection, rexBuilder), rexBuilder));
    }
    for (RexNode need : RelOptUtil.conjunctions(required)) {
      if (PushdownRequired.readsContextRelation(need)) {
        Set<Integer> columns = keyColumnsOf(need);
        boolean found = false;
        for (RexNode have : carried) {
          if (ChalkKeySet.is(have) && keyColumnsOf(have).equals(columns)) {
            found = true;
            break;
          }
        }
        if (!found) {
          return false;
        }
        continue;
      }
      String digest = normalise(need, rexBuilder).toString();
      boolean found = false;
      for (RexNode have : carried) {
        if (have.toString().equals(digest)) {
          found = true;
          break;
        }
      }
      if (!found) {
        return false;
      }
    }
    return true;
  }

  /**
   * Whether the source's side of one boundary carries the row predicate of the entitled table it
   * scans (F147): the question the report asks of the physical tree, asked of a subtree that may
   * still hold subsets while cost is being computed. Answers true where the table recorded no
   * predicate, or no entitled scan is under the boundary — nothing is then owed. A subset is read by
   * its best member, or by the member that created it before cost has settled.
   */
  public static boolean boundaryCarriesRowPredicate(RelNode pushed, RexBuilder rexBuilder) {
    TableScan scan = entitledScanUnderResolving(pushed);
    if (scan == null) {
      return true;
    }
    DisclosureMap map = EntitledRelOptTable.disclosureOf(scan.getTable());
    RexNode required = map == null ? null : map.rowPredicate();
    if (required == null) {
      return true;
    }
    return filterCarrying(pushed, scan, required, rexBuilder);
  }

  private static boolean filterCarrying(
      RelNode rel, TableScan scan, RexNode required, RexBuilder rexBuilder) {
    RelNode node = resolve(rel);
    if (node == null) {
      return false;
    }
    if (node instanceof SourceRels.SourceFilter filter
        && carries(filter.getCondition(), scan, required, rexBuilder)) {
      return true;
    }
    for (RelNode input : node.getInputs()) {
      if (filterCarrying(input, scan, required, rexBuilder)) {
        return true;
      }
    }
    return false;
  }

  private static @Nullable TableScan entitledScanUnderResolving(RelNode rel) {
    RelNode node = resolve(rel);
    if (node == null) {
      return null;
    }
    if (node instanceof TableScan scan) {
      return EntitledRelOptTable.disclosureOf(scan.getTable()) == null ? null : scan;
    }
    for (RelNode input : node.getInputs()) {
      TableScan found = entitledScanUnderResolving(input);
      if (found != null) {
        return found;
      }
    }
    return null;
  }

  private static @Nullable RelNode resolve(RelNode rel) {
    if (rel instanceof RelSubset subset) {
      RelNode best = subset.getBest();
      return best != null ? best : subset.getOriginal();
    }
    return rel;
  }

  /** Input references renumbered through the scan's projection into the table's own columns. */
  private static RexNode remap(RexNode node, List<Integer> projection, RexBuilder rexBuilder) {
    return node.accept(
        new RexShuttle() {
          @Override
          public RexNode visitInputRef(RexInputRef ref) {
            int index = ref.getIndex();
            return index < projection.size()
                ? rexBuilder.makeInputRef(ref.getType(), projection.get(index))
                : ref;
          }
        });
  }

  /** One canonical spelling for two conjuncts that mean the same thing: simplified, then no SEARCH. */
  private static RexNode normalise(RexNode node, RexBuilder rexBuilder) {
    RexNode simplified =
        new RexSimplify(rexBuilder, RelOptPredicateList.EMPTY, RexUtil.EXECUTOR)
            .simplifyUnknownAs(node, RexUnknownAs.UNKNOWN);
    return RexUtil.expandSearch(rexBuilder, null, simplified);
  }

  /** The columns a membership is over: the left operands of an {@code IN} sub-query or of a key set. */
  private static Set<Integer> keyColumnsOf(RexNode node) {
    Set<Integer> columns = new java.util.TreeSet<>();
    List<RexNode> operands =
        node instanceof RexSubQuery subQuery
            ? subQuery.getOperands()
            : node instanceof RexCall call ? call.getOperands() : List.of();
    for (RexNode operand : operands) {
      if (operand instanceof RexInputRef ref) {
        columns.add(ref.getIndex());
      }
    }
    return columns;
  }

  private static @Nullable TableScan entitledScanUnder(RelNode rel) {
    if (rel instanceof TableScan scan) {
      return EntitledRelOptTable.disclosureOf(scan.getTable()) == null ? null : scan;
    }
    for (RelNode input : rel.getInputs()) {
      TableScan found = entitledScanUnder(input);
      if (found != null) {
        return found;
      }
    }
    return null;
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

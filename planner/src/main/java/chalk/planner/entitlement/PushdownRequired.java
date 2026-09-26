package chalk.planner.entitlement;

import chalk.ir.v1.Enforcement;
import chalk.ir.v1.PredicateShape;
import chalk.planner.catalog.ChalkTable;
import chalk.planner.plan.PushdownGate;
import java.util.Map;
import java.util.Set;
import java.util.function.Function;
import org.apache.calcite.plan.RelOptTable;
import org.apache.calcite.plan.volcano.RelSubset;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.core.TableScan;
import org.apache.calcite.rex.RexNode;
import org.apache.calcite.rex.RexShuttle;
import org.apache.calcite.rex.RexSubQuery;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * {@code Enforcement.PUSHDOWN_REQUIRED} (D199, docs/design/16-entitlements.md §3.7).
 *
 * <p>An optional constraint a host puts on a table whose source holds every tenancy's rows: a plan
 * in which that table's row predicate would be evaluated <em>locally</em> is refused rather than
 * run. For such a source a silent full fetch is the worse failure — it is correct, and it moves the
 * whole table across the wire to be filtered here — so a host that would rather be told says so once
 * on the descriptor. The default stays {@code PUSHDOWN}, where a local evaluation is honest and the
 * report says {@code row_predicate_pushed = false}.
 *
 * <p>It guards the <b>row predicate only</b>. A residual conjunct on a masked value is evaluated
 * locally by design (§3.8) and is bounded by the tenancy rather than by the table, so it is not what
 * this is about.
 *
 * <p>Checked on the physical tree beside {@code CrossSourceSupport}, and for the same reason: which
 * alternative cost chose is not known until then. A rule that refused early would refuse plans the
 * optimiser was about to improve.
 */
public final class PushdownRequired {
  private PushdownRequired() {}

  /**
   * What decides whether a key set may travel, for a refusal that has to say which of the host's
   * settings kept one here (F139): the join policy's pair rules, and the source a schema's tables
   * belong to, which is what a pair rule names.
   */
  public record Exchanges(
      chalk.planner.plan.JoinPolicy joinPolicy,
      Function<String, @Nullable String> sourceOfSchema) {

    /** No pair rule anywhere: the shipped policy, under which every exchange is allowed. */
    public static final Exchanges DEFAULT =
        new Exchanges(chalk.planner.plan.JoinPolicy.DEFAULT, schema -> null);
  }

  /**
   * Refuses the plan when an entitled table under {@code PUSHDOWN_REQUIRED} kept its row predicate
   * local.
   *
   * @param rowPredicates the folded {@code Filter_R} per entitled table, as the pass recorded it
   * @param pushed the tables whose row predicate reached their source, as the report computed it
   * @param gates the gate for a source id, so the refusal can name the shape the host declared and
   *     did not rather than the predicate Calcite wrote (F46)
   */
  public static void check(
      RelNode physical,
      Map<String, RexNode> rowPredicates,
      Set<String> pushed,
      Function<String, @Nullable PushdownGate> gates) {
    check(physical, rowPredicates, java.util.List.of(), pushed, gates);
  }

  /**
   * The same, with the {@code through} joins the pass emitted (§3.13, D229, F52).
   *
   * <p>A child entitled through a parent has no row predicate of its own — its restriction <em>is</em>
   * the join — so the map this walks has no entry for it and, until F52, the constraint was silently
   * never checked for one. What it means for such a child is what {@code row_predicate_pushed} means
   * for it: the join went to the source with the parent's own predicate in it, or the parent's
   * visible keys reached the source as a key set. Where neither did, the plan fetches the child whole
   * and filters here, which is the thing {@code PUSHDOWN_REQUIRED} exists to refuse.
   */
  public static void check(
      RelNode physical,
      Map<String, RexNode> rowPredicates,
      java.util.List<TaintCheck.ThroughEvidence> throughJoins,
      Set<String> pushed,
      Function<String, @Nullable PushdownGate> gates) {
    check(physical, rowPredicates, throughJoins, pushed, gates, Exchanges.DEFAULT);
  }

  /**
   * The same, with the settings that decide whether a key set may travel, so that a refusal whose
   * cause is one of them names it rather than a shape the source does declare (F139).
   */
  public static void check(
      RelNode physical,
      Map<String, RexNode> rowPredicates,
      java.util.List<TaintCheck.ThroughEvidence> throughJoins,
      Set<String> pushed,
      Function<String, @Nullable PushdownGate> gates,
      Exchanges exchanges) {
    if (rowPredicates.isEmpty() && throughJoins.isEmpty()) {
      return;
    }
    walk(physical, rowPredicates, throughJoins, pushed, gates, exchanges);
  }

  private static void walk(
      RelNode rel,
      Map<String, RexNode> rowPredicates,
      java.util.List<TaintCheck.ThroughEvidence> throughJoins,
      Set<String> pushed,
      Function<String, @Nullable PushdownGate> gates,
      Exchanges exchanges) {
    RelNode node = rel instanceof RelSubset subset ? best(subset) : rel;
    if (node == null) {
      return;
    }
    checkLeaf(node, rowPredicates, throughJoins, pushed, gates, exchanges);
    for (RelNode input : node.getInputs()) {
      walk(input, rowPredicates, throughJoins, pushed, gates, exchanges);
    }
    node.accept(
        new RexShuttle() {
          @Override
          public RexNode visitSubQuery(RexSubQuery subQuery) {
            walk(subQuery.rel, rowPredicates, throughJoins, pushed, gates, exchanges);
            return super.visitSubQuery(subQuery);
          }
        });
  }

  private static void checkLeaf(
      RelNode node,
      Map<String, RexNode> rowPredicates,
      java.util.List<TaintCheck.ThroughEvidence> throughJoins,
      Set<String> pushed,
      Function<String, @Nullable PushdownGate> gates,
      Exchanges exchanges) {
    RelOptTable table = tableOf(node);
    if (table == null) {
      return;
    }
    DisclosureMap map = EntitledRelOptTable.disclosureOf(table);
    ChalkTable chalkTable = table.unwrap(ChalkTable.class);
    if (map == null || chalkTable == null) {
      return;
    }
    if (chalkTable.descriptor().getEntitlement().getEnforcement()
        != Enforcement.ENFORCEMENT_PUSHDOWN_REQUIRED) {
      return;
    }

    RexNode predicate = rowPredicates.get(map.qualifiedName());
    if (predicate == null) {
      // Either there is nothing to push — the predicate folded away, or this source's own row
      // security is trusted — or this is a child whose restriction is a join rather than a
      // predicate, which has a refusal of its own.
      checkThrough(map, chalkTable, throughJoins, pushed, gates, exchanges);
      return;
    }
    if (!chalkTable.takesQueries()) {
      // A table the client scans has no source to push into, so there is nothing here to refuse.
      // The shipped client refuses the declaration itself at registration, naming the table and the
      // source kind, which is where a mistake belongs; this stays as the fail-open half of that for
      // a catalog assembled by some other producer, because refusing a plan for a constraint that
      // could never be satisfied would refuse the catalog rather than the plan.
      return;
    }
    if (pushed.contains(map.qualifiedName())) {
      return;
    }

    // F139: a bound list above the fold ceiling reaches a source only as a key set looked up from
    // the request context, and a pair rule that forbids looking up into this source forbids that.
    // Then the shape is not what stopped it, and the setting that did is the one to name.
    String sourceId = chalkTable.sourceId();
    if (readsContextRelation(predicate) && !exchanges.joinPolicy().allowsLookup("", sourceId)) {
      throw new PolicyException(
          "the entitlement of "
              + map.qualifiedName()
              + " is PUSHDOWN_REQUIRED and this plan would evaluate its row predicate locally: the"
              + " predicate reads a bound list above the fold ceiling, which reaches a source only as"
              + " a key set looked up from the request context, and the join policy forbids a"
              + " LOOKUP into the source '"
              + sourceId
              + "'. Allow LOOKUP into '"
              + sourceId
              + "' — for this statement through PrepareOptions.JoinPolicy, or in the catalog's"
              + " policy — or set the table's enforcement to PUSHDOWN and accept a local filter"
              + " over a full fetch.");
    }

    throw new PolicyException(
        "the entitlement of "
            + map.qualifiedName()
            + " is PUSHDOWN_REQUIRED and this plan would evaluate its row predicate locally: "
            + reason(gates.apply(chalkTable.sourceId()), predicate, chalkTable.sourceId())
            + " Declare it on the source, narrow the statement so the shapes the source does take"
            + " are enough, or set the table's enforcement to PUSHDOWN and accept a local filter"
            + " over a full fetch.");
  }

  /**
   * The same refusal for a child entitled <em>through</em> a parent (§3.13, D229, F52): what had to
   * reach the source is the join, or the parent's visible keys as a key set, and neither did.
   *
   * <p>An entry is in {@code throughJoins} only for a join the plan must still hold: an elided one —
   * the parent is visible in full — and a leaf that folded to no row at all are both absent, and
   * neither is a full fetch to refuse.
   */
  private static void checkThrough(
      DisclosureMap map,
      ChalkTable table,
      java.util.List<TaintCheck.ThroughEvidence> throughJoins,
      Set<String> pushed,
      Function<String, @Nullable PushdownGate> gates,
      Exchanges exchanges) {
    if (!table.takesQueries() || pushed.contains(map.qualifiedName())) {
      return;
    }
    String parent = null;
    for (TaintCheck.ThroughEvidence join : throughJoins) {
      if (join.childTable().equals(map.qualifiedName())) {
        parent = join.parentTable();
        break;
      }
    }
    if (parent == null) {
      return;
    }

    // F139: the parent's visible keys travel as a LOOKUP into the child's source, and a pair rule
    // may forbid that. Where one does, it is what stopped the exchange, and it is what to name.
    String childSource = table.sourceId();
    int dot = parent.indexOf('.');
    String parentSource =
        dot < 0 ? null : exchanges.sourceOfSchema().apply(parent.substring(0, dot));
    if (parentSource != null
        && !parentSource.equals(childSource)
        && !exchanges.joinPolicy().allowsLookup(parentSource, childSource)) {
      throw new PolicyException(
          "the entitlement of "
              + map.qualifiedName()
              + " is PUSHDOWN_REQUIRED and this plan would evaluate its row predicate locally: its"
              + " visibility derives through '"
              + parent
              + "', whose visible keys reach the source only as a key set looked up from '"
              + parentSource
              + "', and the join policy forbids a LOOKUP from '"
              + parentSource
              + "' into '"
              + childSource
              + "'. Allow LOOKUP for that pair — for this statement through"
              + " PrepareOptions.JoinPolicy, or in the catalog's policy — or set the table's"
              + " enforcement to PUSHDOWN and accept a local join over a full fetch.");
    }

    throw new PolicyException(
        "the entitlement of "
            + map.qualifiedName()
            + " is PUSHDOWN_REQUIRED and this plan would evaluate its row predicate locally: its"
            + " visibility derives through '"
            + parent
            + "', and neither that join nor the parent's visible keys as a key set reached the"
            + " source — "
            + throughReason(gates.apply(table.sourceId()), table.sourceId())
            + " Declare it on the source, narrow the statement so the shapes the source does take"
            + " are enough, or set the table's enforcement to PUSHDOWN and accept a local join"
            + " over a full fetch.");
  }

  /**
   * Why the parent's keys stayed here. A key set is an {@code IN} list, so a source that declares
   * none has a name for what is missing; a source that declares it was stopped by something about
   * the plan rather than about the shape, and naming a shape there would be a guess (F46).
   */
  private static String throughReason(@Nullable PushdownGate gate, String sourceId) {
    if (gate == null || gate.maxInList() <= 0) {
      return "the source '"
          + sourceId
          + "' does not declare "
          + PredicateShape.PREDICATE_SHAPE_IN.name()
          + ", so the parent's visible keys have no key set to travel in.";
    }
    return "the source '"
        + sourceId
        + "' does declare "
        + PredicateShape.PREDICATE_SHAPE_IN.name()
        + ", so what stopped it is the plan rather than the shape — the child's own projection"
        + " staying in process, which a mask makes it do, a join this source cannot run, or more"
        + " visible keys than `max_in_list` times the join policy's `lookup_max_calls`.";
  }

  /**
   * Why the predicate stayed here, in the vocabulary the host declared the source in (F46).
   *
   * <p>The message used to quote Calcite's own predicate — "does not take the shape
   * {@code SEARCH($1, Sarg[1, 3])}" — which named the wrong thing twice: {@code SEARCH} is not a
   * word a host writes anywhere, and the {@code Sarg} held this principal's own tenancy identifiers,
   * which §3.12 keeps out of an error message. What the host declared and did not is a
   * {@link PredicateShape}, and that is a name with nothing of the principal in it.
   *
   * <p>Where every shape the predicate is made of <em>is</em> declared, the gate says
   * {@code UNSPECIFIED} and so does this: a list longer than {@code max_in_list}, a string column
   * under a collation the source does not share, an expression the source cannot spell. Naming a
   * shape there would be a guess.
   */
  private static String reason(
      @Nullable PushdownGate gate, RexNode predicate, String sourceId) {
    PredicateShape missing =
        gate == null ? PredicateShape.PREDICATE_SHAPE_UNSPECIFIED : gate.missingShape(predicate);
    if (missing == PredicateShape.PREDICATE_SHAPE_UNSPECIFIED) {
      return "the source '"
          + sourceId
          + "' declares every predicate shape this row predicate is made of and still cannot take"
          + " it, so what stopped it is one of the other limits its descriptor states — the"
          + " `max_in_list` ceiling, `supports_row_value_in_list` for a membership over several"
          + " columns, the collation of a string column, or a function it does not have.";
    }
    return "the source '" + sourceId + "' does not declare " + missing.name() + ".";
  }

  /**
   * Whether this folded predicate reads a context relation: a bound list above the fold ceiling,
   * which the fold leaves a sub-query over the relation rather than a literal list (§2).
   */
  private static boolean readsContextRelation(RexNode predicate) {
    boolean[] found = {false};
    predicate.accept(
        new RexShuttle() {
          @Override
          public RexNode visitSubQuery(RexSubQuery subQuery) {
            if (scansContextTable(subQuery.rel)) {
              found[0] = true;
            }
            return super.visitSubQuery(subQuery);
          }
        });
    return found[0];
  }

  private static boolean scansContextTable(RelNode rel) {
    RelNode node = rel instanceof RelSubset subset ? best(subset) : rel;
    if (node == null) {
      return false;
    }
    if (node instanceof TableScan scan && scan.getTable().unwrap(ContextTable.class) != null) {
      return true;
    }
    for (RelNode input : node.getInputs()) {
      if (scansContextTable(input)) {
        return true;
      }
    }
    return false;
  }

  private static @Nullable RelOptTable tableOf(RelNode node) {
    if (node instanceof TableScan scan) {
      return scan.getTable();
    }
    if (node instanceof chalk.planner.plan.rel.ChalkIndexLookup lookup) {
      return lookup.getTable();
    }
    return null;
  }

  private static @Nullable RelNode best(RelSubset subset) {
    RelNode best = subset.getBest();
    return best != null ? best : subset.getOriginal();
  }
}

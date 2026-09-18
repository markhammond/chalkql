package chalk.planner.entitlement;

import com.google.common.collect.ImmutableList;
import java.util.ArrayList;
import java.util.Collection;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import org.apache.calcite.plan.RelOptUtil;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.core.TableScan;
import org.apache.calcite.rel.type.RelDataTypeField;
import org.apache.calcite.rex.RexBuilder;
import org.apache.calcite.rex.RexCall;
import org.apache.calcite.rex.RexNode;
import org.apache.calcite.rex.RexShuttle;
import org.apache.calcite.rex.RexSubQuery;
import org.apache.calcite.rex.RexUtil;
import org.apache.calcite.rex.RexVisitorImpl;
import org.apache.calcite.sql.SqlKind;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * A membership test over an unfolded context list <em>inside a disjunction</em>, split into branches
 * the IR can express (F36; docs/design/16-entitlements.md §2).
 *
 * <p>A bound list of more than {@code fold_max_rows} rows is not made literal: the membership test
 * becomes {@code IN (SELECT … FROM <context table>)}, which Calcite turns into a semi-join — but only
 * where that {@code IN} is a top-level conjunct of the row predicate, where it may answer two-valued.
 * Inside an {@code OR} it has to answer three-valued, and Calcite's rewrite for that uses
 * {@code LITERAL_AGG}, which the IR does not carry; the statement was refused naming the aggregate,
 * and the tenancy package's created-by fail-safe puts exactly such a disjunct into every row
 * predicate.
 *
 * <p>The rewrite is here rather than in Calcite, and it is Shannon expansion over the memberships: a
 * row satisfies {@code M OR R} exactly when {@code M} holds, or {@code M} does not hold and {@code R}
 * does. So the leaf becomes a {@code UNION ALL} of a <b>semi-join branch</b> — the scan filtered by
 * the membership, which is then the whole condition and therefore a top-level conjunct — and an
 * <b>anti-join branch</b> — the scan anti-joined against the same list, filtered by what is left of
 * the predicate once {@code M} is known not to hold. Every row appears in exactly one branch, and
 * both halves are shapes the IR already has.
 *
 * <p>Three-valued logic is why the two conditions below are checked rather than assumed. A filter
 * reads its condition as <em>unknown as false</em>, so "{@code M} does not hold" is "{@code M} is
 * FALSE or UNKNOWN", which is exactly the anti-join's answer — provided {@code M} never stands under
 * a negation, where FALSE and UNKNOWN part company. A membership whose operands and list are all NOT
 * NULL is two-valued and may stand anywhere; anything else must be monotone, and a predicate that is
 * neither is left alone and refused as before.
 */
final class MembershipSplit {
  private MembershipSplit() {}

  /**
   * How many memberships one leaf may be expanded over. Each doubles the branch count in the worst
   * case; four is more roles than any tenancy the shipped package emits, and it keeps the union
   * small enough to read in a plan.
   */
  private static final int MAX_MEMBERSHIPS = 4;

  /**
   * One branch of the split: the memberships that hold for its rows, the ones that do not, and what
   * is left of the row predicate once both are known.
   */
  record Branch(ImmutableList<RexSubQuery> holds, ImmutableList<RexSubQuery> fails, RexNode predicate) {}

  /**
   * The branches this leaf's row predicate needs, or null when it needs none — because it holds no
   * unfolded membership, because every one of them is already a top-level conjunct Calcite turns
   * into a semi-join on its own, or because the shape is one this rewrite cannot prove itself on and
   * today's refusal is the honest answer.
   *
   * @param predicate the folded row predicate
   * @param others every other expression of the leaf the substitution must also reach — the rule
   *     conditions and masks, whose memberships have to take the branch's own answer or a sub-query
   *     would survive into {@code Project_D}
   */
  static @Nullable ImmutableList<Branch> of(
      RexNode predicate, List<RexNode> others, RexBuilder rexBuilder) {
    Map<String, RexSubQuery> memberships = new LinkedHashMap<>();
    collect(predicate, memberships);
    if (memberships.isEmpty() || memberships.size() > MAX_MEMBERSHIPS) {
      return null;
    }

    // A membership already standing as a top-level conjunct is one Calcite handles itself, and
    // rewriting it would trade a semi-join for a union of one.
    List<String> topLevel = new ArrayList<>();
    for (RexNode conjunct : RelOptUtil.conjunctions(predicate)) {
      topLevel.add(conjunct.toString());
    }
    if (topLevel.containsAll(memberships.keySet())) {
      return null;
    }

    for (RexSubQuery membership : memberships.values()) {
      if (twoValued(membership)) {
        continue;
      }
      String text = membership.toString();
      if (!monotone(predicate, text)) {
        return null;
      }
      for (RexNode other : others) {
        if (!monotone(other, text)) {
          return null;
        }
      }
    }

    List<Branch> branches = new ArrayList<>();
    expand(predicate, List.of(), List.of(), memberships.values(), rexBuilder, branches);
    return branches.isEmpty() ? null : ImmutableList.copyOf(branches);
  }

  /**
   * Shannon expansion, one membership at a time and only over the ones still present: a branch whose
   * predicate no longer mentions a membership does not have to decide it, and stopping there keeps
   * the branches prefix-disjoint, which is what makes {@code UNION ALL} the right operator.
   */
  private static void expand(
      RexNode predicate,
      List<RexSubQuery> holds,
      List<RexSubQuery> fails,
      Collection<RexSubQuery> memberships,
      RexBuilder rexBuilder,
      List<Branch> into) {
    RexSubQuery next = null;
    for (RexSubQuery membership : memberships) {
      if (occurs(predicate, membership.toString())) {
        next = membership;
        break;
      }
    }
    if (next == null) {
      into.add(new Branch(ImmutableList.copyOf(holds), ImmutableList.copyOf(fails), predicate));
      return;
    }

    RexNode whenTrue = substitute(predicate, next.toString(), true, rexBuilder);
    if (!whenTrue.isAlwaysFalse()) {
      expand(whenTrue, append(holds, next), fails, memberships, rexBuilder, into);
    }
    RexNode whenFalse = substitute(predicate, next.toString(), false, rexBuilder);
    if (!whenFalse.isAlwaysFalse()) {
      expand(whenFalse, holds, append(fails, next), memberships, rexBuilder, into);
    }
  }

  private static List<RexSubQuery> append(List<RexSubQuery> list, RexSubQuery one) {
    List<RexSubQuery> next = new ArrayList<>(list);
    next.add(one);
    return next;
  }

  /**
   * The same expression with one membership replaced by a constant, folded through the boolean
   * operators as it goes.
   *
   * <p>Deliberately not {@code RexSimplify}: the expression still holds the <em>other</em>
   * memberships, and handing the simplifier a sub-query makes it walk a relation it has no metadata
   * for. Folding {@code AND} and {@code OR} against the two constants is all this needs.
   */
  static RexNode substitute(RexNode node, String membership, boolean value, RexBuilder rexBuilder) {
    if (node instanceof RexSubQuery && node.toString().equals(membership)) {
      return rexBuilder.makeLiteral(value);
    }
    if (!occurs(node, membership)) {
      return node;
    }
    SqlKind kind = node.getKind();
    if (node instanceof RexCall call && (kind == SqlKind.AND || kind == SqlKind.OR)) {
      boolean and = kind == SqlKind.AND;
      List<RexNode> parts = new ArrayList<>(call.getOperands().size());
      for (RexNode operand : call.getOperands()) {
        RexNode folded = substitute(operand, membership, value, rexBuilder);
        if (folded.isAlwaysTrue()) {
          if (and) {
            continue;
          }
          return rexBuilder.makeLiteral(true);
        }
        if (folded.isAlwaysFalse()) {
          if (!and) {
            continue;
          }
          return rexBuilder.makeLiteral(false);
        }
        parts.add(folded);
      }
      if (parts.isEmpty()) {
        return rexBuilder.makeLiteral(and);
      }
      return parts.size() == 1
          ? parts.get(0)
          : and
              ? RexUtil.composeConjunction(rexBuilder, parts)
              : RexUtil.composeDisjunction(rexBuilder, parts);
    }

    // Anywhere else, which the shape check above only allows for a two-valued membership: the
    // constant goes in where it stands and the call is rebuilt around it.
    return node.accept(
        new RexShuttle() {
          @Override
          public RexNode visitSubQuery(RexSubQuery subQuery) {
            return subQuery.toString().equals(membership)
                ? rexBuilder.makeLiteral(value)
                : super.visitSubQuery(subQuery);
          }
        });
  }

  /** Every membership test over an unfolded context list in {@code node}, in encounter order. */
  static void collect(RexNode node, Map<String, RexSubQuery> into) {
    node.accept(
        new RexVisitorImpl<Void>(true) {
          @Override
          public Void visitSubQuery(RexSubQuery subQuery) {
            if (subQuery.getKind() == SqlKind.IN && readsOnlyContext(subQuery.rel)) {
              into.putIfAbsent(subQuery.toString(), subQuery);
            }
            return super.visitSubQuery(subQuery);
          }
        });
  }

  /**
   * Whether this sub-query's relation reads context relations and nothing else. A membership over a
   * catalog table is not something this rewrite may take apart: its rows are not the host's binding,
   * and an anti-join against a table the principal is entitled on would be a different statement.
   */
  static boolean readsOnlyContext(RelNode rel) {
    boolean[] context = {false};
    boolean[] other = {false};
    scans(rel, context, other);
    return context[0] && !other[0];
  }

  private static void scans(RelNode rel, boolean[] context, boolean[] other) {
    if (rel instanceof TableScan scan) {
      if (scan.getTable().unwrap(ContextTable.class) != null) {
        context[0] = true;
      } else {
        other[0] = true;
      }
    }
    for (RelNode input : rel.getInputs()) {
      scans(input, context, other);
    }
    rel.accept(
        new RexShuttle() {
          @Override
          public RexNode visitSubQuery(RexSubQuery subQuery) {
            scans(subQuery.rel, context, other);
            return super.visitSubQuery(subQuery);
          }
        });
  }

  /** Whether the membership can never answer UNKNOWN, so a FALSE may be substituted anywhere. */
  static boolean twoValued(RexSubQuery membership) {
    for (RexNode operand : membership.getOperands()) {
      if (operand.getType().isNullable()) {
        return false;
      }
    }
    for (RelDataTypeField field : membership.rel.getRowType().getFieldList()) {
      if (field.getType().isNullable()) {
        return false;
      }
    }
    return true;
  }

  /**
   * Whether every occurrence of {@code membership} in {@code node} stands under {@code AND} and
   * {@code OR} alone, so that replacing an UNKNOWN by FALSE cannot change whether the whole
   * expression is TRUE. Kleene {@code AND} and {@code OR} are monotone in the truth ordering; a
   * negation is not.
   */
  static boolean monotone(RexNode node, String membership) {
    if (node instanceof RexSubQuery && node.toString().equals(membership)) {
      return true;
    }
    SqlKind kind = node.getKind();
    if (!(node instanceof RexCall call) || (kind != SqlKind.AND && kind != SqlKind.OR)) {
      return !occurs(node, membership);
    }
    for (RexNode operand : call.getOperands()) {
      if (!monotone(operand, membership)) {
        return false;
      }
    }
    return true;
  }

  private static boolean occurs(RexNode node, String membership) {
    boolean[] found = {false};
    node.accept(
        new RexVisitorImpl<Void>(true) {
          @Override
          public Void visitSubQuery(RexSubQuery subQuery) {
            if (subQuery.toString().equals(membership)) {
              found[0] = true;
            }
            return super.visitSubQuery(subQuery);
          }
        });
    return found[0];
  }
}

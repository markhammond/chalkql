package chalk.planner;

import chalk.ir.v1.Plan;
import chalk.ir.v1.Rel;
import java.util.ArrayList;
import java.util.List;

/** Walks an IR plan, for the tests that assert on the nodes it contains. */
final class IrNodes {
  private IrNodes() {}

  /** Every node of one kind, in pre-order from the root. */
  static List<Rel> of(Plan plan, Rel.KindCase kind) {
    List<Rel> found = new ArrayList<>();
    collect(plan.getRoot(), kind, found);
    return found;
  }

  private static void collect(Rel rel, Rel.KindCase kind, List<Rel> found) {
    if (rel.getKindCase() == kind) {
      found.add(rel);
    }

    for (Rel input : inputs(rel)) {
      collect(input, kind, found);
    }
  }

  /** A node's inputs, whatever kind it is. */
  static List<Rel> inputs(Rel rel) {
    return switch (rel.getKindCase()) {
      case FILTER -> List.of(rel.getFilter().getInput());
      case PROJECT -> List.of(rel.getProject().getInput());
      case SORT -> List.of(rel.getSort().getInput());
      case TOP_N -> List.of(rel.getTopN().getInput());
      case FETCH -> List.of(rel.getFetch().getInput());
      case WINDOW -> List.of(rel.getWindow().getInput());
      case HOP -> List.of(rel.getHop().getInput());
      case SESSION -> List.of(rel.getSession().getInput());
      case UNNEST -> List.of(rel.getUnnest().getInput());
      case AGGREGATE -> List.of(rel.getAggregate().getInput());
      case HASH_AGGREGATE -> List.of(rel.getHashAggregate().getAggregate().getInput());
      case STREAM_AGGREGATE -> List.of(rel.getStreamAggregate().getAggregate().getInput());
      case HASH_JOIN -> List.of(rel.getHashJoin().getLeft(), rel.getHashJoin().getRight());
      case MERGE_JOIN -> List.of(rel.getMergeJoin().getLeft(), rel.getMergeJoin().getRight());
      case NESTED_LOOP_JOIN ->
          List.of(rel.getNestedLoopJoin().getLeft(), rel.getNestedLoopJoin().getRight());
      case AS_OF_JOIN -> List.of(rel.getAsOfJoin().getLeft(), rel.getAsOfJoin().getRight());
      case JOIN -> List.of(rel.getJoin().getLeft(), rel.getJoin().getRight());
      case SET_OP -> rel.getSetOp().getInputsList();
      default -> List.of();
    };
  }
}

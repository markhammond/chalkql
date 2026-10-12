package chalk.planner.ir;

import chalk.ir.PlanDigest;
import chalk.ir.v1.Plan;
import chalk.ir.v1.Rel;
import chalk.ir.v1.SharedPlan;
import chalk.ir.v1.SharedRef;
import com.google.protobuf.ByteString;
import com.google.protobuf.CodedOutputStream;
import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.UncheckedIOException;
import java.util.ArrayList;
import java.util.EnumSet;
import java.util.HashMap;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.function.UnaryOperator;

/**
 * Subtrees a plan repeats, computed once (D338, design 63 §2). Calcite inlines a {@code WITH} block
 * at each reference, and the optimiser's memo holds one set for the equal copies, but the IR is a
 * tree, so each copy ran. This finds the subtrees that occur more than once, writes each once in
 * {@code Plan.shared}, and puts a {@code SharedRef} at every use. It covers any repeated subtree, not
 * only a {@code WITH}: the branches of a {@code UNION ALL} over one block, a self-join of a derived
 * table, a repeated scalar subquery.
 *
 * <p>Not everything repeated is worth holding:
 *
 * <ul>
 *   <li>A scan or {@code VALUES}, under filters, projections and limits, is cheaper to read twice
 *       than to copy and keep, so a subtree is shared only where it does more: a join, an aggregate,
 *       a sort, a window, a set operation, a remote query.
 *   <li>A subtree the entitlement rewrite put a node in is left as it is, so the client's invariant
 *       sees every policy where the planner wrote it.
 *   <li>Nothing under an adaptive join, a lookup join's lookup side, a partitioned scan or a remote
 *       query's pushed plan: those are bound, run at once, or run by something other than the
 *       plan's own pull.
 * </ul>
 *
 * <p>Equal means equal in the IR, estimates aside. The largest repeated subtrees are taken first;
 * one shared plan used only once after that is put back where it was used.
 */
public final class SharedSubtrees {
  private SharedSubtrees() {}

  /** Kinds that are cheap to compute again: a leaf, and what only filters or shapes its rows. */
  private static final EnumSet<Rel.KindCase> CHEAP =
      EnumSet.of(
          Rel.KindCase.READ,
          Rel.KindCase.VIRTUAL_TABLE,
          Rel.KindCase.BOUND_TABLE,
          Rel.KindCase.INDEX_LOOKUP,
          Rel.KindCase.FILTER,
          Rel.KindCase.PROJECT,
          Rel.KindCase.FETCH,
          Rel.KindCase.SHARED_REF);

  /** Kinds a shared subtree never contains: their inputs are bound or chosen by something else. */
  private static final EnumSet<Rel.KindCase> NEVER =
      EnumSet.of(Rel.KindCase.ADAPTIVE_JOIN, Rel.KindCase.MATERIALISED_INPUT);

  /** The plan with each repeated subtree written once, or the plan itself when nothing repeats. */
  public static Plan share(Plan plan) {
    if (!plan.hasRoot()) {
      return plan;
    }

    Map<ByteString, Integer> counts = new HashMap<>();
    count(plan.getRoot(), counts);
    if (counts.values().stream().noneMatch(c -> c > 1)) {
      return plan;
    }

    Sharer sharer = new Sharer(counts);
    Rel root = sharer.replace(plan.getRoot());
    if (sharer.shared.isEmpty()) {
      return plan;
    }

    // One use left: the copy that made it look repeated was inside another shared plan.
    Map<Integer, Rel> bodies = sharer.shared;
    while (true) {
      Map<Integer, Integer> uses = new HashMap<>();
      uses(root, uses);
      for (Rel body : bodies.values()) {
        uses(body, uses);
      }
      List<Integer> single = new ArrayList<>();
      for (Integer id : bodies.keySet()) {
        if (uses.getOrDefault(id, 0) <= 1) {
          single.add(id);
        }
      }
      if (single.isEmpty()) {
        break;
      }
      Map<Integer, Rel> inlined = new HashMap<>();
      for (Integer id : single) {
        inlined.put(id, bodies.remove(id));
      }
      root = inline(root, inlined);
      for (Map.Entry<Integer, Rel> body : bodies.entrySet()) {
        body.setValue(inline(body.getValue(), inlined));
      }
    }

    if (bodies.isEmpty()) {
      return plan;
    }
    Plan.Builder shared = plan.toBuilder().setRoot(root);
    for (Map.Entry<Integer, Rel> body : bodies.entrySet()) {
      shared.addShared(SharedPlan.newBuilder().setId(body.getKey()).setRel(body.getValue()));
    }
    return shared.build();
  }

  private static final class Sharer {
    private final Map<ByteString, Integer> counts;
    private final Map<ByteString, Integer> ids = new HashMap<>();

    /** In the order written, which is dependency order: a body names only earlier ids. */
    private final Map<Integer, Rel> shared = new LinkedHashMap<>();

    Sharer(Map<ByteString, Integer> counts) {
      this.counts = counts;
    }

    Rel replace(Rel rel) {
      ByteString key = key(rel);
      if (counts.getOrDefault(key, 0) > 1 && worthSharing(rel)) {
        Integer id = ids.get(key);
        if (id == null) {
          Rel body = mapChildren(rel, this::replace);
          id = ids.size() + 1;
          ids.put(key, id);
          shared.put(id, body);
        }
        return reference(rel, id);
      }
      return mapChildren(rel, this::replace);
    }
  }

  /** A use of shared plan {@code id}, standing where {@code rel} stood. */
  private static Rel reference(Rel rel, int id) {
    return Rel.newBuilder()
        .setRowType(rel.getRowType())
        .setEstRowCount(rel.getEstRowCount())
        .addAllCollations(rel.getCollationsList())
        .setSharedRef(SharedRef.newBuilder().setId(id))
        .build();
  }

  private static boolean worthSharing(Rel rel) {
    boolean[] costly = {false};
    visit(
        rel,
        node -> {
          if (!CHEAP.contains(node.getKindCase())) {
            costly[0] = true;
          }
        });
    // Refused at any depth, a remote query's pushed plan and a lookup's side included: a policy's
    // nodes travel inside the query a source is sent as much as in the client's own tree.
    return costly[0]
        && !anyRel(rel, node -> node.getPolicyInjected() || NEVER.contains(node.getKindCase()));
  }

  /** Whether {@code test} holds of any relation in {@code message}, at any depth. */
  private static boolean anyRel(
      com.google.protobuf.Message message, java.util.function.Predicate<Rel> test) {
    if (message instanceof Rel rel && test.test(rel)) {
      return true;
    }
    for (Object value : message.getAllFields().values()) {
      if (value instanceof com.google.protobuf.Message nested && anyRel(nested, test)) {
        return true;
      }
      if (value instanceof List<?> items) {
        for (Object item : items) {
          if (item instanceof com.google.protobuf.Message nested && anyRel(nested, test)) {
            return true;
          }
        }
      }
    }
    return false;
  }

  private static void count(Rel rel, Map<ByteString, Integer> counts) {
    counts.merge(key(rel), 1, Integer::sum);
    for (Rel child : children(rel)) {
      count(child, counts);
    }
  }

  private static void uses(Rel rel, Map<Integer, Integer> uses) {
    visit(
        rel,
        node -> {
          if (node.getKindCase() == Rel.KindCase.SHARED_REF) {
            uses.merge(node.getSharedRef().getId(), 1, Integer::sum);
          }
        });
  }

  private static Rel inline(Rel rel, Map<Integer, Rel> bodies) {
    if (rel.getKindCase() == Rel.KindCase.SHARED_REF && bodies.containsKey(rel.getSharedRef().getId())) {
      Rel body = bodies.get(rel.getSharedRef().getId());
      return inline(body.toBuilder().setEstRowCount(rel.getEstRowCount()).build(), bodies);
    }
    return mapChildren(rel, child -> inline(child, bodies));
  }

  private static void visit(Rel rel, java.util.function.Consumer<Rel> action) {
    action.accept(rel);
    for (Rel child : children(rel)) {
      visit(child, action);
    }
  }

  /** The subtree's identity: its deterministic encoding, estimates aside. */
  private static ByteString key(Rel rel) {
    Rel canonical = PlanDigest.stripEstimates(rel);
    ByteArrayOutputStream out = new ByteArrayOutputStream(canonical.getSerializedSize());
    CodedOutputStream coded = CodedOutputStream.newInstance(out);
    coded.useDeterministicSerialization();
    try {
      canonical.writeTo(coded);
      coded.flush();
    } catch (IOException e) {
      throw new UncheckedIOException("serialising a relation to a byte array cannot fail", e);
    }
    return ByteString.copyFrom(out.toByteArray());
  }

  /**
   * The inputs a shared subtree may be found among: every input of every kind, except a lookup
   * join's lookup side, which the executor binds per call, everything under an adaptive join, whose
   * branches replay its own buffer, and a remote query's pushed plan, which its source runs.
   */
  private static List<Rel> children(Rel rel) {
    List<Rel> children = new ArrayList<>(2);
    mapChildren(
        rel,
        child -> {
          children.add(child);
          return child;
        });
    return children;
  }

  /** {@code rel} with each input {@link #children} names replaced by {@code f} of it. */
  private static Rel mapChildren(Rel rel, UnaryOperator<Rel> f) {
    Rel.Builder builder = rel.toBuilder();
    switch (rel.getKindCase()) {
      case FILTER -> builder.getFilterBuilder().setInput(f.apply(rel.getFilter().getInput()));
      case PROJECT -> builder.getProjectBuilder().setInput(f.apply(rel.getProject().getInput()));
      case AGGREGATE ->
          builder.getAggregateBuilder().setInput(f.apply(rel.getAggregate().getInput()));
      case HASH_AGGREGATE ->
          builder
              .getHashAggregateBuilder()
              .getAggregateBuilder()
              .setInput(f.apply(rel.getHashAggregate().getAggregate().getInput()));
      case STREAM_AGGREGATE ->
          builder
              .getStreamAggregateBuilder()
              .getAggregateBuilder()
              .setInput(f.apply(rel.getStreamAggregate().getAggregate().getInput()));
      case SORT -> builder.getSortBuilder().setInput(f.apply(rel.getSort().getInput()));
      case FETCH -> builder.getFetchBuilder().setInput(f.apply(rel.getFetch().getInput()));
      case TOP_N -> builder.getTopNBuilder().setInput(f.apply(rel.getTopN().getInput()));
      case WINDOW -> builder.getWindowBuilder().setInput(f.apply(rel.getWindow().getInput()));
      case HOP -> builder.getHopBuilder().setInput(f.apply(rel.getHop().getInput()));
      case SESSION -> builder.getSessionBuilder().setInput(f.apply(rel.getSession().getInput()));
      case UNNEST -> builder.getUnnestBuilder().setInput(f.apply(rel.getUnnest().getInput()));
      case JOIN -> {
        builder.getJoinBuilder().setLeft(f.apply(rel.getJoin().getLeft()));
        builder.getJoinBuilder().setRight(f.apply(rel.getJoin().getRight()));
      }
      case HASH_JOIN -> {
        builder.getHashJoinBuilder().setLeft(f.apply(rel.getHashJoin().getLeft()));
        builder.getHashJoinBuilder().setRight(f.apply(rel.getHashJoin().getRight()));
      }
      case MERGE_JOIN -> {
        builder.getMergeJoinBuilder().setLeft(f.apply(rel.getMergeJoin().getLeft()));
        builder.getMergeJoinBuilder().setRight(f.apply(rel.getMergeJoin().getRight()));
      }
      case NESTED_LOOP_JOIN -> {
        builder.getNestedLoopJoinBuilder().setLeft(f.apply(rel.getNestedLoopJoin().getLeft()));
        builder.getNestedLoopJoinBuilder().setRight(f.apply(rel.getNestedLoopJoin().getRight()));
      }
      case AS_OF_JOIN -> {
        builder.getAsOfJoinBuilder().setLeft(f.apply(rel.getAsOfJoin().getLeft()));
        builder.getAsOfJoinBuilder().setRight(f.apply(rel.getAsOfJoin().getRight()));
      }
      case AS_OF_LOOKUP_JOIN ->
          builder.getAsOfLookupJoinBuilder().setLeft(f.apply(rel.getAsOfLookupJoin().getLeft()));
      case SET_OP -> {
        var setOp = builder.getSetOpBuilder();
        for (int i = 0; i < setOp.getInputsCount(); i++) {
          setOp.setInputs(i, f.apply(rel.getSetOp().getInputs(i)));
        }
      }
      case LOOKUP_JOIN ->
          builder.getLookupJoinBuilder().setDriving(f.apply(rel.getLookupJoin().getDriving()));
      default -> {
        // A leaf; a remote query, whose pushed plan its source runs; an adaptive join, whose
        // branches replay its own buffer; or a partitioned scan, whose partitions run at once, so
        // two of them would fill one shared result together. Nothing here is found or replaced.
        return rel;
      }
    }
    return builder.build();
  }
}

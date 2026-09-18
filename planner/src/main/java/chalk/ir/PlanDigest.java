package chalk.ir;

import chalk.ir.v1.Aggregate;
import chalk.ir.v1.HashAggregate;
import chalk.ir.v1.Plan;
import chalk.ir.v1.Rel;
import chalk.ir.v1.SetOp;
import chalk.ir.v1.StreamAggregate;
import com.google.protobuf.CodedOutputStream;
import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.UncheckedIOException;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;

/**
 * The plan digest: the golden-plan test key and the telemetry correlation id (docs/design/02-ir.md
 * §7). Twin of {@code Chalk.Ir.PlanDigest} on the client; the two are tested against shared
 * fixtures, and a disagreement fails the build.
 *
 * <pre>
 * canonical(plan) = plan with plan_digest := 0, context_id := "", catalog_epoch := 0,
 *                   and every Rel.est_row_count := 0 (recursively), serialised with
 *                   deterministic protobuf encoding
 * plan_digest     = little-endian uint64 of the first 8 bytes of SHA-256(canonical(plan))
 * </pre>
 */
public final class PlanDigest {
  private PlanDigest() {}

  /** Computes the digest of the plan's canonical form. */
  public static long compute(Plan plan) {
    byte[] bytes = serialize(canonicalise(plan));
    try {
      byte[] hash = MessageDigest.getInstance("SHA-256").digest(bytes);
      return ByteBuffer.wrap(hash, 0, 8).order(ByteOrder.LITTLE_ENDIAN).getLong();
    } catch (NoSuchAlgorithmException e) {
      throw new IllegalStateException("SHA-256 is required by every JRE", e);
    }
  }

  /** The canonical form the digest is taken over. */
  public static Plan canonicalise(Plan plan) {
    Plan.Builder builder = plan.toBuilder();
    builder.setPlanDigest(0L);
    builder.setContextId("");
    builder.setCatalogEpoch(0L);
    if (plan.hasRoot()) {
      builder.setRoot(stripEstimates(plan.getRoot()));
    }
    return builder.build();
  }

  /** Deterministic protobuf encoding: field-number order, no map reordering. */
  public static byte[] serialize(Plan plan) {
    ByteArrayOutputStream out = new ByteArrayOutputStream(plan.getSerializedSize());
    CodedOutputStream coded = CodedOutputStream.newInstance(out);
    coded.useDeterministicSerialization();
    try {
      plan.writeTo(coded);
      coded.flush();
    } catch (IOException e) {
      throw new UncheckedIOException("serialising a plan to a byte array cannot fail", e);
    }
    return out.toByteArray();
  }

  /** Sixteen lowercase hex characters — the form written to {@code corpus/plans/m1/*.digest}. */
  public static String format(long digest) {
    return String.format("%016x", digest);
  }

  private static Rel stripEstimates(Rel rel) {
    Rel.Builder builder = rel.toBuilder();
    builder.setEstRowCount(0.0);
    switch (rel.getKindCase()) {
      case FILTER -> builder.getFilterBuilder().setInput(stripEstimates(rel.getFilter().getInput()));
      case PROJECT ->
          builder.getProjectBuilder().setInput(stripEstimates(rel.getProject().getInput()));
      case AGGREGATE -> builder.setAggregate(stripEstimates(rel.getAggregate()));
      case SORT -> builder.getSortBuilder().setInput(stripEstimates(rel.getSort().getInput()));
      case FETCH -> builder.getFetchBuilder().setInput(stripEstimates(rel.getFetch().getInput()));
      case TOP_N -> builder.getTopNBuilder().setInput(stripEstimates(rel.getTopN().getInput()));
      case WINDOW -> builder.getWindowBuilder().setInput(stripEstimates(rel.getWindow().getInput()));
      case HOP -> builder.getHopBuilder().setInput(stripEstimates(rel.getHop().getInput()));
      case SESSION -> builder.getSessionBuilder().setInput(stripEstimates(rel.getSession().getInput()));
      case UNNEST -> builder.getUnnestBuilder().setInput(stripEstimates(rel.getUnnest().getInput()));
      case HASH_AGGREGATE ->
          builder.setHashAggregate(
              HashAggregate.newBuilder()
                  .setAggregate(stripEstimates(rel.getHashAggregate().getAggregate())));
      case STREAM_AGGREGATE ->
          builder.setStreamAggregate(
              StreamAggregate.newBuilder()
                  .setAggregate(stripEstimates(rel.getStreamAggregate().getAggregate())));
      case JOIN -> {
        builder.getJoinBuilder().setLeft(stripEstimates(rel.getJoin().getLeft()));
        builder.getJoinBuilder().setRight(stripEstimates(rel.getJoin().getRight()));
      }
      case HASH_JOIN -> {
        builder.getHashJoinBuilder().setLeft(stripEstimates(rel.getHashJoin().getLeft()));
        builder.getHashJoinBuilder().setRight(stripEstimates(rel.getHashJoin().getRight()));
      }
      case MERGE_JOIN -> {
        builder.getMergeJoinBuilder().setLeft(stripEstimates(rel.getMergeJoin().getLeft()));
        builder.getMergeJoinBuilder().setRight(stripEstimates(rel.getMergeJoin().getRight()));
      }
      case NESTED_LOOP_JOIN -> {
        builder.getNestedLoopJoinBuilder().setLeft(stripEstimates(rel.getNestedLoopJoin().getLeft()));
        builder
            .getNestedLoopJoinBuilder()
            .setRight(stripEstimates(rel.getNestedLoopJoin().getRight()));
      }
      case AS_OF_JOIN -> {
        builder.getAsOfJoinBuilder().setLeft(stripEstimates(rel.getAsOfJoin().getLeft()));
        builder.getAsOfJoinBuilder().setRight(stripEstimates(rel.getAsOfJoin().getRight()));
      }
      case SET_OP -> {
        SetOp.Builder setOp = builder.getSetOpBuilder();
        for (int i = 0; i < setOp.getInputsCount(); i++) {
          setOp.setInputs(i, stripEstimates(setOp.getInputs(i)));
        }
      }
      case LOOKUP_JOIN -> {
        builder.getLookupJoinBuilder().setDriving(stripEstimates(rel.getLookupJoin().getDriving()));
        builder.getLookupJoinBuilder().setLookup(stripEstimates(rel.getLookupJoin().getLookup()));
      }
      case ADAPTIVE_JOIN -> {
        // Both branches, because the digest covers both (D97): which one runs is a property of the
        // data, not of the plan, and a digest that moved with it would mean nothing.
        chalk.ir.v1.AdaptiveJoin.Builder adaptive = builder.getAdaptiveJoinBuilder();
        adaptive.setSmall(stripEstimates(rel.getAdaptiveJoin().getSmall()));
        adaptive.setLocal(stripEstimates(rel.getAdaptiveJoin().getLocal()));
        chalk.ir.v1.LookupJoin.Builder lookup = adaptive.getLookupBuilder();
        lookup.setDriving(stripEstimates(rel.getAdaptiveJoin().getLookup().getDriving()));
        lookup.setLookup(stripEstimates(rel.getAdaptiveJoin().getLookup().getLookup()));
      }
      case PARTITIONED_SCAN -> {
        chalk.ir.v1.PartitionedScan.Builder scan = builder.getPartitionedScanBuilder();
        for (int i = 0; i < scan.getPartitionsCount(); i++) {
          scan.setPartitions(i, stripEstimates(scan.getPartitions(i)));
        }
      }
      case REMOTE_QUERY -> {
        // The pushed subtree is part of the plan's shape (D84), so its estimates must be stripped
        // too — otherwise a statistics refresh would churn every digest that has a RemoteQuery in
        // it, which is exactly what stripping exists to prevent.
        if (rel.getRemoteQuery().hasPushedPlan()) {
          builder
              .getRemoteQueryBuilder()
              .setPushedPlan(stripEstimates(rel.getRemoteQuery().getPushedPlan()));
        }
      }
      default -> {
        // leaves: READ, VIRTUAL_TABLE, INDEX_LOOKUP, KIND_NOT_SET
      }
    }
    return builder.build();
  }

  private static Aggregate stripEstimates(Aggregate aggregate) {
    return aggregate.toBuilder().setInput(stripEstimates(aggregate.getInput())).build();
  }
}

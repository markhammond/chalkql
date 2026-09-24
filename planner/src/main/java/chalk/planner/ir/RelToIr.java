package chalk.planner.ir;

import chalk.ir.IrVersion;
import chalk.ir.PlanDigest;
import chalk.ir.v1.Aggregate;
import chalk.ir.v1.AsOfJoin;
import chalk.ir.v1.AsOfMatch;
import chalk.ir.v1.Collation;
import chalk.ir.v1.DynamicParam;
import chalk.ir.v1.Expr;
import chalk.ir.v1.FieldRef;
import chalk.ir.v1.Fetch;
import chalk.ir.v1.FrameBound;
import chalk.ir.v1.FrameBoundKind;
import chalk.ir.v1.FrameExclusion;
import chalk.ir.v1.FrameMode;
import chalk.ir.v1.Filter;
import chalk.ir.v1.FunctionId;
import chalk.ir.v1.Grouping;
import chalk.ir.v1.HashAggregate;
import chalk.ir.v1.HashJoin;
import chalk.ir.v1.IndexLookup;
import chalk.ir.v1.IndexRange;
import chalk.ir.v1.JoinType;
import chalk.ir.v1.Measure;
import chalk.ir.v1.MergeJoin;
import chalk.ir.v1.NestedLoopJoin;
import chalk.ir.v1.Plan;
import chalk.ir.v1.Project;
import chalk.ir.v1.Read;
import chalk.ir.v1.RemoteQuery;
import chalk.ir.v1.Rel;
import chalk.ir.v1.RowType;
import chalk.ir.v1.ScalarCall;
import chalk.ir.v1.SetOpKind;
import chalk.ir.v1.Sort;
import chalk.ir.v1.SortField;
import chalk.ir.v1.TableRef;
import chalk.ir.v1.TopN;
import chalk.ir.v1.Type;
import chalk.ir.v1.TypeKind;
import chalk.ir.v1.VirtualRow;
import chalk.ir.v1.VirtualTable;
import chalk.ir.v1.WindowCall;
import chalk.ir.v1.WindowFrame;
import chalk.ir.v1.WindowFunctionId;
import chalk.planner.UnsupportedFeatureException;
import chalk.planner.catalog.ChalkTable;
import chalk.planner.plan.rel.ChalkAsOfJoin;
import chalk.planner.plan.rel.ChalkFilter;
import chalk.planner.plan.rel.ChalkHashAggregate;
import chalk.planner.plan.rel.ChalkHop;
import chalk.planner.plan.rel.ChalkHashJoin;
import chalk.planner.plan.rel.ChalkIndexLookup;
import chalk.planner.plan.rel.ChalkLimit;
import chalk.planner.plan.rel.ChalkMergeJoin;
import chalk.planner.plan.rel.ChalkNestedLoopJoin;
import chalk.planner.plan.rel.ChalkProject;
import chalk.planner.plan.rel.ChalkSort;
import chalk.planner.plan.rel.ChalkTableScan;
import chalk.planner.plan.rel.ChalkSession;
import chalk.planner.plan.rel.ChalkTopN;
import chalk.planner.plan.rel.ChalkUnnest;
import chalk.planner.plan.rel.ChalkIntersect;
import chalk.planner.plan.rel.ChalkMinus;
import chalk.planner.plan.rel.ChalkUnion;
import chalk.planner.plan.rel.ChalkValues;
import chalk.planner.plan.rel.ChalkWindow;
import chalk.planner.plan.SourceConvention;
import chalk.planner.plan.rel.SourceRels;
import chalk.planner.plan.rel.SourceScan;
import chalk.planner.plan.rel.SourceToLocalConverter;
import chalk.planner.types.TypeMapper;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.HashSet;
import java.util.List;
import java.util.Map;
import java.util.Set;
import org.apache.calcite.rel.RelCollation;
import org.apache.calcite.rel.RelCollationTraitDef;
import org.apache.calcite.rel.RelFieldCollation;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.core.AggregateCall;
import org.apache.calcite.rel.core.Join;
import org.apache.calcite.rel.core.JoinInfo;
import org.apache.calcite.rel.core.JoinRelType;
import org.apache.calcite.rel.metadata.RelMetadataQuery;
import org.apache.calcite.rel.type.RelDataType;
import org.apache.calcite.rel.type.RelDataTypeField;
import org.apache.calcite.rex.RexBuilder;
import org.apache.calcite.rex.RexCall;
import org.apache.calcite.rex.RexDynamicParam;
import org.apache.calcite.rex.RexInputRef;
import org.apache.calcite.rex.RexLiteral;
import org.apache.calcite.rex.RexNode;
import org.apache.calcite.rex.RexUtil;
import org.apache.calcite.rex.RexWindowBound;
import org.apache.calcite.rex.RexWindowExclusion;
import org.apache.calcite.sql.SqlAggFunction;
import org.apache.calcite.sql.SqlKind;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * The final {@code ChalkConvention} tree → IR {@code Plan} (docs/design/03-planner.md §5.1).
 *
 * <p>A plain {@code instanceof} switch over the eight Chalk rels rather than a {@code RelShuttle}:
 * 1.42 changed shuttle dispatch, and eight cases read better than the indirection. Any other rel type
 * is a planner bug and becomes {@code UNSUPPORTED} naming the class.
 */
public final class RelToIr {
  private final TypeMapper types;
  private final RexToIr rex;
  private final RelMetadataQuery mq;
  private final IrVersionGate gate;

  public RelToIr(TypeMapper types, RexBuilder rexBuilder, RelMetadataQuery mq, IrVersionGate gate) {
    this.types = types;
    this.rex = new RexToIr(types, rexBuilder);
    this.mq = mq;
    this.gate = gate;
  }

  /**
   * Builds the whole plan.
   *
   * <p>The root's field order and names are the SELECT list's (§5.4), but that is settled before the
   * tree arrives here: {@code PlannerPipeline.rootProject} puts the projection on after optimisation,
   * where it also fixes the ordering bug of ADR 0014. This class maps whatever tree it is given, one
   * rel at a time, and invents nothing.
   */
  public Plan toPlan(
      RelNode physical, RelDataType parameterRowType, String contextId, long catalogEpoch) {
    Rel rel = toRel(physical);
    RowType outputType = rel.getRowType();

    Plan.Builder plan =
        Plan.newBuilder()
            .setIrVersion(IrVersion.CURRENT)
            .setContextId(contextId)
            .setCatalogEpoch(catalogEpoch)
            .setOutputType(outputType)
            .setRoot(rel);
    for (RelDataTypeField field : parameterRowType.getFieldList()) {
      plan.addParameterTypes(types.toIr(field.getType()));
    }
    Plan withoutDigest = plan.build();
    return withoutDigest.toBuilder().setPlanDigest(PlanDigest.compute(withoutDigest)).build();
  }

  /** One relation and everything under it. */
  public Rel toRel(RelNode node) {
    Rel.Builder builder = Rel.newBuilder();

    // The rewrite put this node here rather than the query (step 26, §3.12). It carries no semantics
    // an executor acts on — the node is an ordinary Read — and exists so plan text, an audit log and
    // the client's own validator can tell policy from query.
    if (isEntitled(node) || isCorrelation(node)) {
      builder.setPolicyInjected(true);
    }

    if (node instanceof ChalkTableScan scan) {
      builder.setRead(read(scan));
    } else if (node instanceof ChalkIndexLookup lookup) {
      builder.setIndexLookup(indexLookup(lookup));
    } else if (node instanceof ChalkFilter filter) {
      builder.setFilter(
          Filter.newBuilder()
              .setInput(toRel(filter.getInput()))
              .setCondition(rex.convert(filter.getCondition())));
    } else if (node instanceof ChalkProject project) {
      Project.Builder ir = Project.newBuilder().setInput(toRel(project.getInput()));
      for (RexNode expression : project.getProjects()) {
        ir.addExprs(rex.convert(expression));
      }
      builder.setProject(ir);
    } else if (node instanceof ChalkHashAggregate aggregate) {
      builder.setHashAggregate(HashAggregate.newBuilder().setAggregate(aggregate(aggregate)));
    } else if (node instanceof ChalkSort sort) {
      builder.setSort(
          Sort.newBuilder()
              .setInput(toRel(sort.getInput()))
              .addAllFields(sortFields(sort.getCollation(), sort.getInput().getRowType())));
    } else if (node instanceof ChalkTopN topN) {
      TopN.Builder ir =
          TopN.newBuilder()
              .setInput(toRel(topN.getInput()))
              .addAllFields(sortFields(topN.collation(), topN.getInput().getRowType()));
      // The bound as the statement wrote it, and never the hint that estimated it (D285): a
      // parameter travels as a parameter, and the executor reads what is actually bound.
      DynamicParam offsetParam = boundParam(topN.offset());
      if (offsetParam == null) {
        ir.setOffset(literalBound(topN.offset()));
      } else {
        ir.setOffsetParam(offsetParam);
      }
      DynamicParam countParam = boundParam(topN.fetch());
      if (countParam == null) {
        ir.setCount(literalBound(topN.fetch()));
      } else {
        ir.setCountParam(countParam);
      }
      builder.setTopN(ir);
    } else if (node instanceof ChalkLimit limit) {
      Fetch.Builder fetch = Fetch.newBuilder().setInput(toRel(limit.getInput()));
      DynamicParam offsetParam = boundParam(limit.offset());
      if (offsetParam == null) {
        fetch.setOffset(literalBound(limit.offset()));
      } else {
        fetch.setOffsetParam(offsetParam);
      }
      DynamicParam countParam = boundParam(limit.fetch());
      if (countParam != null) {
        fetch.setCountParam(countParam);
      } else if (limit.fetch() != null) {
        fetch.setCount(literalBound(limit.fetch()));
      }
      builder.setFetch(fetch);
    } else if (node instanceof ChalkValues values) {
      builder.setVirtualTable(virtualTable(values));
    } else if (node instanceof chalk.planner.plan.rel.ChalkContextScan scan) {
      // The binding's name and nothing else: the rows are the host's, and they are not in the plan,
      // the digest or the logs (step 26, 16-entitlements.md §2).
      builder.setBoundTable(
          chalk.ir.v1.BoundTable.newBuilder().setName(scan.contextName()));
    } else if (node instanceof chalk.planner.plan.rel.ChalkTableFunctionScan scan) {
      builder.setTableFunctionScan(tableFunctionScan(scan));
    } else if (node instanceof ChalkHashJoin join) {
      JoinInfo info = join.analyzeCondition();
      refuseStructKeys(join, info.leftKeys, info.rightKeys);
      HashJoin.Builder ir =
          HashJoin.newBuilder()
              .setLeft(toRel(join.getLeft()))
              .setRight(toRel(join.getRight()))
              .addAllLeftKeys(unsigned(info.leftKeys))
              .addAllRightKeys(unsigned(info.rightKeys))
              .setType(joinType(join.getJoinType()));
      Expr hashResidual = residual(info, join);
      if (hashResidual != null) {
        ir.setPostJoinFilter(hashResidual);
      }
      builder.setHashJoin(ir);
    } else if (node instanceof chalk.planner.plan.rel.ChalkLookupJoin join) {
      builder.setLookupJoin(lookupJoin(join, toRel(join.getLeft()), toRel(join.getRight())));
    } else if (node instanceof chalk.planner.plan.rel.ChalkAdaptiveJoin join) {
      builder.setAdaptiveJoin(adaptiveJoin(join));
    } else if (node instanceof chalk.planner.plan.rel.ChalkPartitionedScan scan) {
      builder.setPartitionedScan(partitionedScan(scan));
    } else if (node instanceof ChalkMergeJoin join) {
      refuseStructKeys(join, join.leftKeys(), join.rightKeys());
      MergeJoin.Builder ir =
          MergeJoin.newBuilder()
              .setLeft(toRel(join.getLeft()))
              .setRight(toRel(join.getRight()))
              .addAllLeftKeys(unsigned(join.leftKeys()))
              .addAllRightKeys(unsigned(join.rightKeys()))
              .setType(joinType(join.getJoinType()));
      Expr mergeResidual = residual(join.analyzeCondition(), join);
      if (mergeResidual != null) {
        ir.setPostJoinFilter(mergeResidual);
      }
      builder.setMergeJoin(ir);
    } else if (node instanceof ChalkAsOfJoin join) {
      builder.setAsOfJoin(asOfJoin(join));
    } else if (node instanceof ChalkWindow window) {
      builder.setWindow(window(window));
    } else if (node instanceof ChalkHop hop) {
      builder.setHop(
          chalk.ir.v1.Hop.newBuilder()
              .setInput(toRel(hop.getInput()))
              .setTimeColumn(hop.timeColumn())
              .setSlide(interval(hop.slide()))
              .setSize(interval(hop.size())));
    } else if (node instanceof ChalkSession session) {
      chalk.ir.v1.Session.Builder ir =
          chalk.ir.v1.Session.newBuilder()
              .setInput(toRel(session.getInput()))
              .setTimeColumn(session.timeColumn())
              .setGap(interval(session.gap()));
      for (int key : session.partitionKeys()) {
        ir.addPartitionKeys(key);
      }
      builder.setSession(ir);
    } else if (node instanceof ChalkUnnest unnest) {
      builder.setUnnest(
          chalk.ir.v1.Unnest.newBuilder()
              .setInput(toRel(unnest.getInput()))
              .setListColumn(unnest.listColumn())
              .setWithOrdinality(unnest.withOrdinality())
              .setKeepEmpty(unnest.keepEmpty()));
    } else if (node instanceof ChalkUnion union) {
      builder.setSetOp(setOp(union, union.all
          ? SetOpKind.SET_OP_KIND_UNION_ALL
          : SetOpKind.SET_OP_KIND_UNION_DISTINCT));
    } else if (node instanceof ChalkIntersect intersect) {
      builder.setSetOp(setOp(intersect, intersect.all
          ? SetOpKind.SET_OP_KIND_INTERSECT_ALL
          : SetOpKind.SET_OP_KIND_INTERSECT_DISTINCT));
    } else if (node instanceof ChalkMinus minus) {
      builder.setSetOp(setOp(minus, minus.all
          ? SetOpKind.SET_OP_KIND_EXCEPT_ALL
          : SetOpKind.SET_OP_KIND_EXCEPT_DISTINCT));
    } else if (node instanceof ChalkNestedLoopJoin join) {
      NestedLoopJoin.Builder ir =
          NestedLoopJoin.newBuilder()
              .setLeft(toRel(join.getLeft()))
              .setRight(toRel(join.getRight()))
              .setType(joinType(join.getJoinType()));
      if (!join.getCondition().isAlwaysTrue()) {
        ir.setCondition(rex.convert(join.getCondition()));
      }
      builder.setNestedLoopJoin(ir);
    } else if (node instanceof SourceToLocalConverter boundary) {
      builder.setRemoteQuery(remoteQuery(boundary));

    // The pushed subtree (D84). Every source rel maps onto the *logical* IR node its local twin
    // would produce, because how the source runs it is the source's choice, not the plan's.
    } else if (node instanceof SourceScan scan) {
      builder.setRead(read(scan));
    } else if (node instanceof SourceRels.SourceFilter filter) {
      Rel input = toRel(filter.getInput());
      Expr condition = rex.convert(filter.getCondition());
      if (input.getKindCase() == Rel.KindCase.READ && isUnprojected(filter.getInput())) {
        // `Read.filter` is finally set (§2). Only over an unpruned scan: the field's contract is
        // that the predicate reads the *table's* row, and over a pruned scan the condition's field
        // references index the projected one.
        builder.setRead(input.getRead().toBuilder().setFilter(condition));
        // The merge folds the scan's own node away, and `policy_injected` is a property of the read
        // rather than of the filter: without carrying it the client's I-IR-E sees an entitled table
        // read with no rewrite on it and refuses the plan (§3.10). Reachable only where the scan is
        // unpruned, which is why nothing met it until a key set put a filter over one.
        builder.setPolicyInjected(input.getPolicyInjected());
      } else {
        builder.setFilter(Filter.newBuilder().setInput(input).setCondition(condition));
      }
    } else if (node instanceof SourceRels.SourceProject project) {
      Project.Builder ir = Project.newBuilder().setInput(toRel(project.getInput()));
      for (RexNode expression : project.getProjects()) {
        ir.addExprs(rex.convert(expression));
      }
      builder.setProject(ir);
    } else if (node instanceof SourceRels.SourceAggregate aggregate) {
      builder.setAggregate(aggregate(aggregate));
    } else if (node instanceof SourceRels.SourceSort sort) {
      sourceSort(builder, sort);
    } else if (node instanceof SourceRels.SourceJoin join) {
      chalk.ir.v1.Join.Builder ir =
          chalk.ir.v1.Join.newBuilder()
              .setLeft(toRel(join.getLeft()))
              .setRight(toRel(join.getRight()))
              .setType(joinType(join.getJoinType()));
      if (!join.getCondition().isAlwaysTrue()) {
        ir.setCondition(rex.convert(join.getCondition()));
      }
      builder.setJoin(ir);
    } else {
      throw new UnsupportedFeatureException(
          "relational operator " + node.getClass().getSimpleName(),
          "The optimiser produced a rel this milestone cannot express in the IR.");
    }

    Rel.KindCase kind = builder.getKindCase();
    if (!gate.allows(kind)) {
      throw new UnsupportedFeatureException(
          "IR node " + kind,
          "It was introduced in IR version "
              + IrVersionGate.sinceVersion(kind)
              + " and the client speaks version "
              + gate.clientIrVersion()
              + ".");
    }

    builder.setRowType(types.toIrRow(node.getRowType()));
    builder.setEstRowCount(Math.max(mq.getRowCount(node), 0.0));
    for (Collation collation : collations(node)) {
      builder.addCollations(collation);
    }
    return builder.build();
  }

  /**
   * The part of a join's condition the equality keys do not cover, over the joined row, or null when
   * they cover all of it.
   *
   * <p>It is part of the join <em>condition</em>, not a filter applied afterwards: for an outer join
   * the two differ, because a left row whose only candidate fails the remainder must still be
   * null-padded rather than dropped. The IR field is named {@code post_join_filter} and its comment
   * says so ({@code 12-joins.md} §2, ADR 0016).
   */
  /**
   * A {@code LookupJoin} (M5, D105). Its right input is a {@code RemoteQuery} carrying exactly one
   * key set, which the executor binds once per call; everything else about it is an ordinary
   * equi-join, so the keys and the residual come from the same {@code JoinInfo} a hash join uses.
   */
  private chalk.ir.v1.LookupJoin lookupJoin(
      chalk.planner.plan.rel.ChalkLookupJoin join, Rel driving, Rel lookup) {
    JoinInfo info = join.analyzeCondition();
    refuseStructKeys(join, info.leftKeys, info.rightKeys);
    chalk.ir.v1.LookupJoin.Builder ir =
        chalk.ir.v1.LookupJoin.newBuilder()
            .setDriving(driving)
            .setLookup(lookup)
            .addAllDrivingKeys(unsigned(info.leftKeys))
            .addAllLookupKeys(unsigned(info.rightKeys))
            .setType(joinType(join.getJoinType()))
            .setMaxKeysPerCall(join.maxKeysPerCall())
            .setKeySetRows(join.keySetAsRows());
    Expr remainder = residual(info, join);
    if (remainder != null) {
      ir.setPostJoinFilter(remainder);
    }
    return ir.build();
  }

  /**
   * An {@code AdaptiveJoin} (D97): the small side, both branches over a {@code MaterialisedInput}
   * that replays it, and the threshold the execution applies. The digest covers both branches, so
   * the plan says nothing about which one will run — {@code Stats.AdaptiveDecisions} does.
   */
  private chalk.ir.v1.AdaptiveJoin adaptiveJoin(chalk.planner.plan.rel.ChalkAdaptiveJoin join) {
    JoinInfo info = join.analyzeCondition();
    refuseStructKeys(join, info.leftKeys, info.rightKeys);
    Rel small = toRel(join.getLeft());
    Rel replay =
        Rel.newBuilder()
            .setRowType(small.getRowType())
            .setEstRowCount(small.getEstRowCount())
            .setMaterialisedInput(chalk.ir.v1.MaterialisedInput.newBuilder().setSlot(0))
            .build();

    chalk.ir.v1.LookupJoin.Builder lookup =
        chalk.ir.v1.LookupJoin.newBuilder()
            .setDriving(replay)
            .setLookup(toRel(join.lookupSide()))
            .addAllDrivingKeys(unsigned(info.leftKeys))
            .addAllLookupKeys(unsigned(info.rightKeys))
            .setType(joinType(join.getJoinType()))
            .setMaxKeysPerCall(join.maxKeysPerCall())
            .setKeySetRows(false);
    Expr remainder = residual(info, join);
    if (remainder != null) {
      lookup.setPostJoinFilter(remainder);
    }

    HashJoin.Builder local =
        HashJoin.newBuilder()
            .setLeft(replay)
            .setRight(toRel(join.getRight()))
            .addAllLeftKeys(unsigned(info.leftKeys))
            .addAllRightKeys(unsigned(info.rightKeys))
            .setType(joinType(join.getJoinType()));
    if (remainder != null) {
      local.setPostJoinFilter(remainder);
    }

    return chalk.ir.v1.AdaptiveJoin.newBuilder()
        .setSmall(small)
        .setLookup(lookup)
        .setLocal(
            Rel.newBuilder()
                .setRowType(types.toIrRow(join.getRowType()))
                .setEstRowCount(Math.max(mq.getRowCount(join), 0.0))
                .setHashJoin(local))
        .setMaxKeys(join.maxKeys())
        .setSlot(0)
        .setKey(info.leftKeys.get(0))
        .build();
  }

  /**
   * A {@code PartitionedScan} (D106): the partitions a predicate could not prune, each with the
   * value it holds, so a key set bound at execution can prune the rest.
   */
  private chalk.ir.v1.PartitionedScan partitionedScan(
      chalk.planner.plan.rel.ChalkPartitionedScan scan) {
    chalk.ir.v1.PartitionedScan.Builder ir = chalk.ir.v1.PartitionedScan.newBuilder();
    for (RelNode input : scan.getInputs()) {
      ir.addPartitions(toRel(input));
    }
    for (org.apache.calcite.rex.RexNode value : scan.partitionValues()) {
      chalk.ir.v1.PartitionMatch.Builder match = chalk.ir.v1.PartitionMatch.newBuilder();
      if (value != null) {
        match.setValue(rex.convert(value));
      }
      ir.addMatches(match);
    }
    return ir.build();
  }

  /**
   * D291: a struct has no equality, so it is never a join key. The statement's own comparison of one
   * is refused at validation ({@code StructSupport}); this is the same refusal for a key the join
   * condition reached some other way — a {@code USING} or {@code NATURAL} join on a struct column.
   */
  private void refuseStructKeys(Join join, List<Integer> leftKeys, List<Integer> rightKeys) {
    for (int i = 0; i < leftKeys.size(); i++) {
      RelDataTypeField left = join.getLeft().getRowType().getFieldList().get(leftKeys.get(i));
      RelDataTypeField right = join.getRight().getRowType().getFieldList().get(rightKeys.get(i));
      if (left.getType().isStruct() || right.getType().isStruct()) {
        throw new UnsupportedFeatureException(
            "a join on the struct column '" + left.getName() + "'",
            "A struct has no equality, so it cannot be a join key. Join on one of its fields "
                + "instead (docs/design/51-structured-function-results.md §1).");
      }
    }
  }

  private @Nullable Expr residual(JoinInfo info, Join join) {
    RexNode remainder =
        RexUtil.composeConjunction(join.getCluster().getRexBuilder(), info.nonEquiConditions, true);
    return remainder == null || remainder.isAlwaysTrue() ? null : rex.convert(remainder);
  }

  /**
   * The ASOF join. The keys come from {@code JoinInfo} — the rule has already refused anything the
   * ON clause cannot express as equalities — and the two time columns from the match condition,
   * normalised so that {@code match} always reads {@code left_time <op> right_time}.
   */
  private AsOfJoin asOfJoin(ChalkAsOfJoin join) {
    JoinInfo info = join.analyzeCondition();
    refuseStructKeys(join, info.leftKeys, info.rightKeys);
    int leftFields = join.getLeft().getRowType().getFieldCount();
    RexCall match = (RexCall) join.getMatchCondition();
    RexInputRef first = (RexInputRef) match.getOperands().get(0);
    RexInputRef second = (RexInputRef) match.getOperands().get(1);

    boolean leftFirst = first.getIndex() < leftFields;
    RexInputRef leftRef = leftFirst ? first : second;
    RexInputRef rightRef = leftFirst ? second : first;
    SqlKind kind = leftFirst ? match.getKind() : match.getKind().reverse();

    return AsOfJoin.newBuilder()
        .setLeft(toRel(join.getLeft()))
        .setRight(toRel(join.getRight()))
        .addAllLeftKeys(unsigned(info.leftKeys))
        .addAllRightKeys(unsigned(info.rightKeys))
        .setLeftTime(leftRef.getIndex())
        .setRightTime(rightRef.getIndex() - leftFields)
        .setMatch(asOfMatch(kind))
        .setType(
            join.getJoinType() == JoinRelType.LEFT_ASOF ? JoinType.JOIN_TYPE_LEFT : JoinType.JOIN_TYPE_INNER)
        .build();
  }

  private static AsOfMatch asOfMatch(SqlKind kind) {
    return switch (kind) {
      case LESS_THAN -> AsOfMatch.AS_OF_MATCH_LT;
      case LESS_THAN_OR_EQUAL -> AsOfMatch.AS_OF_MATCH_LE;
      case GREATER_THAN -> AsOfMatch.AS_OF_MATCH_GT;
      case GREATER_THAN_OR_EQUAL -> AsOfMatch.AS_OF_MATCH_GE;
      default ->
          throw new UnsupportedFeatureException(
              "ASOF MATCH_CONDITION operator " + kind, "Only <, <=, > and >= compare two times.");
    };
  }

  private static JoinType joinType(JoinRelType type) {
    return switch (type) {
      case INNER -> JoinType.JOIN_TYPE_INNER;
      case LEFT -> JoinType.JOIN_TYPE_LEFT;
      case RIGHT -> JoinType.JOIN_TYPE_RIGHT;
      case FULL -> JoinType.JOIN_TYPE_FULL;
      case SEMI -> JoinType.JOIN_TYPE_SEMI;
      case ANTI -> JoinType.JOIN_TYPE_ANTI;
      default ->
          throw new UnsupportedFeatureException(
              "join type " + type, "The IR has INNER, LEFT, RIGHT, FULL, SEMI and ANTI.");
    };
  }

  /** Field indexes as the IR's unsigned ones. */
  private static List<Integer> unsigned(List<Integer> indexes) {
    return indexes;
  }

  /**
   * A pushed {@code Sort}: a {@code Sort} when it orders, a {@code Fetch} when it only limits, and a
   * {@code Fetch} over a {@code Sort} when it does both — which is exactly the shape the local
   * planner produces for the same SQL.
   */
  private void sourceSort(Rel.Builder builder, SourceRels.SourceSort sort) {
    Rel input = toRel(sort.getInput());
    boolean orders = !sort.getCollation().getFieldCollations().isEmpty();
    if (orders) {
      Rel sorted =
          Rel.newBuilder()
              .setRowType(types.toIrRow(sort.getInput().getRowType()))
              .setEstRowCount(Math.max(mq.getRowCount(sort.getInput()), 0.0))
              .setSort(
                  Sort.newBuilder()
                      .setInput(input)
                      .addAllFields(
                          sortFields(sort.getCollation(), sort.getInput().getRowType())))
              .build();
      if (sort.fetch == null && sort.offset == null) {
        builder.setSort(sorted.getSort());
        return;
      }
      input = sorted;
    }

    Fetch.Builder fetch = Fetch.newBuilder().setInput(input);
    // A pushed bound is a literal or one of the statement's own parameters (D288). A parameter is
    // written into the pushed plan as the parameter it is, the same way a local `Fetch` writes one,
    // so the algebra the query text came from says what the text's placeholder stands for; the
    // executor renders its value into the text when the execution starts. Anything else — an
    // expression, a context-bound scalar — must not have been pushed, and is refused here rather
    // than dropped, because silently emitting no bound would change the answer.
    requirePushableBound(sort.offset, "OFFSET");
    requirePushableBound(sort.fetch, "LIMIT");
    DynamicParam offsetParam = boundParam(sort.offset);
    if (offsetParam != null) {
      fetch.setOffsetParam(offsetParam);
    } else if (sort.offset instanceof RexLiteral offset) {
      fetch.setOffset(((Number) offset.getValue2()).longValue());
    }
    DynamicParam countParam = boundParam(sort.fetch);
    if (countParam != null) {
      fetch.setCountParam(countParam);
    } else if (sort.fetch instanceof RexLiteral count) {
      fetch.setCount(((Number) count.getValue2()).longValue());
    }
    builder.setFetch(fetch);
  }

  private static void requirePushableBound(@Nullable RexNode bound, String what) {
    if (bound == null || bound instanceof RexLiteral) {
      return;
    }
    if (bound instanceof RexDynamicParam parameter
        && !(parameter instanceof chalk.planner.entitlement.BoundParam)) {
      return;
    }

    throw new IllegalStateException(
        "a pushed " + what + " bound is " + bound + ", which is neither a literal nor one of the"
            + " statement's own parameters; a source is handed a query text, so a bound nothing can"
            + " write a number for must not have been pushed");
  }

  /**
   * A {@code LIMIT} or {@code OFFSET} bound written as the parameter it is, or null when it is a
   * literal or absent (D285).
   *
   * <p>What travels is the statement's own parameter and never the hint that estimated it: a hint
   * changes a cost and nothing the executor reads.
   */
  private static @Nullable DynamicParam boundParam(@Nullable RexNode bound) {
    if (!(bound instanceof RexDynamicParam parameter)) {
      return null;
    }

    if (parameter instanceof chalk.planner.entitlement.BoundParam named) {
      return DynamicParam.newBuilder().setBoundKey(named.key()).build();
    }

    return DynamicParam.newBuilder().setIndex(parameter.getIndex()).build();
  }

  /** The same bound as the literal it is; absent is zero, which is what "no offset" means. */
  private static long literalBound(@Nullable RexNode bound) {
    if (bound == null) {
      return 0L;
    }

    if (bound instanceof RexLiteral literal) {
      return RexLiteral.longValue(literal);
    }

    throw new IllegalStateException(
        "a LIMIT or OFFSET bound is " + bound + ", which is neither a literal nor a parameter");
  }

  /** Whether this scan reads every column of its table, which is what {@code Read.filter} needs. */
  private static boolean isUnprojected(RelNode rel) {
    if (!(rel instanceof SourceScan scan)) {
      return false;
    }
    List<Integer> projection = scan.projection();
    for (int i = 0; i < projection.size(); i++) {
      if (projection.get(i) != i) {
        return false;
      }
    }
    return projection.size() == scan.chalkTable().descriptor().getColumnsCount();
  }

  /** The IR {@code Read} for a pushed scan — the same shape a local one produces. */
  private Read read(SourceScan scan) {
    ChalkTable table = scan.chalkTable();
    Read.Builder read =
        Read.newBuilder()
            .setTable(
                TableRef.newBuilder()
                    .setSourceId(table.sourceId())
                    .setSchema(table.schemaName())
                    .setTable(table.tableName()));
    read.addAllProjection(scan.projection());
    breadcrumbs(read, scan.getTable());
    correlation(read, scan.getTable());
    return read.build();
  }

  /**
   * Every dynamic parameter in a pushed subtree, once per index, in index order. The order an
   * IR source reads, and the fallback for a SQL source whose text is not what says the order —
   * which never happens, because a SQL source always has one.
   */
  private static List<RexDynamicParam> dynamicParams(RelNode rel) {
    List<RexDynamicParam> found = new ArrayList<>();
    rel.accept(
        new org.apache.calcite.rel.RelShuttleImpl() {
          @Override
          public RelNode visit(RelNode other) {
            other.accept(
                new org.apache.calcite.rex.RexShuttle() {
                  @Override
                  public RexNode visitDynamicParam(RexDynamicParam parameter) {
                    found.add(parameter);
                    return parameter;
                  }
                });
            return super.visit(other);
          }
        });
    found.sort(java.util.Comparator.comparingInt(RexDynamicParam::getIndex));
    List<RexDynamicParam> distinct = new ArrayList<>(found.size());
    int last = -1;
    for (RexDynamicParam parameter : found) {
      if (parameter.getIndex() != last) {
        distinct.add(parameter);
        last = parameter.getIndex();
      }
    }
    return distinct;
  }

  /** The indexes of the {@code LIMIT}/{@code OFFSET} bounds a pushed subtree carries as parameters. */
  private static Set<Integer> boundIndexes(RelNode rel) {
    Set<Integer> found = new HashSet<>(2);
    collectBoundIndexes(rel, found);
    return found;
  }

  private static void collectBoundIndexes(RelNode rel, Set<Integer> into) {
    if (rel instanceof SourceRels.SourceSort sort) {
      if (sort.fetch instanceof RexDynamicParam fetch) {
        into.add(fetch.getIndex());
      }
      if (sort.offset instanceof RexDynamicParam offset) {
        into.add(offset.getIndex());
      }
    }
    for (RelNode input : rel.getInputs()) {
      collectBoundIndexes(input, into);
    }
  }

  /**
   * The boundary node, in both flavours (D84). A SQL source gets {@code query_text} <em>and</em>
   * {@code pushed_plan}, so the algebra a query text was generated from is never lost and step 24
   * can reverse it; an IR source gets {@code pushed_plan} alone and interprets it itself.
   *
   * <p>The pushed subtree is converted with the same {@code toRel} the rest of the plan uses, so a
   * source rel maps onto exactly the IR node its local twin would: {@code SourceScan} is a
   * {@code Read} (with its filter and projection), {@code SourceFilter} a {@code Filter}, and so on.
   * That is what makes {@code pushed_plan} a plan and not a second dialect.
   */
  private RemoteQuery remoteQuery(SourceToLocalConverter boundary) {
    SourceConvention convention = boundary.sourceConvention();
    RelNode pushed = boundary.getInput();
    RemoteQuery.Builder ir =
        RemoteQuery.newBuilder()
            .setSourceId(convention.sourceId())
            .setDialect(convention.profile().getDialect());
    rex.enterPushed();
    try {
      ir.setPushedPlan(toRel(pushed));
    } finally {
      rex.exitPushed();
    }
    List<RexNode> keySets = keySets(pushed);
    if (convention.isSql()) {
      SourceSql.Generated generated = SourceSql.generate(pushed, convention);
      ir.setQueryText(generated.sql());
      statementParameters(ir, generated, pushed, !keySets.isEmpty());
    } else {
      for (RexDynamicParam parameter : dynamicParams(pushed)) {
        ir.addParameters(rex.convert(parameter));
      }
    }

    // A lookup subtree's key set is a placeholder like any other, and the last one: the rule appends
    // its conjunct last so the `?` it writes is last in the generated SQL, and refuses a subtree
    // that carries any other parameter, so in practice it is also the only one (ADR 0022). What
    // goes in `parameters` is the *bare* key set — the executor binds a set of values into that one
    // placeholder — while the pushed predicate keeps the `col IN (key set)` shape a source reads.
    for (RexNode key : keySets) {
      List<RexNode> columns = ((org.apache.calcite.rex.RexCall) key).getOperands();
      if (columns.size() > 1) {
        // A composite key set binds a whole *row* into its one placeholder, so the entry is the
        // KeySetMatch itself: its columns carry their own types, in the order a bound row's values
        // go, which is what the executor needs and a single type could not say (F50).
        ir.addParameters(rex.convert(key));
        continue;
      }
      ir.addParameters(
          Expr.newBuilder()
              .setType(types.toIr(columns.get(0).getType()))
              .setKeySet(chalk.ir.v1.KeySetParam.newBuilder().setSlot(0))
              .build());
    }
    return ir.build();
  }

  /**
   * The statement's own parameters, one entry per {@code ?} the generated text writes for one, in
   * the order the text writes them — and {@code rendered_bounds} naming the ones that are a pushed
   * {@code LIMIT} or {@code OFFSET} (D288).
   *
   * <p>Textual order, not index order, is what a positional binding needs and what a dialect that
   * spells a bound at the front of the statement makes visible: for {@code TOP (?) … WHERE x > ?}
   * the bound is placeholder <b>0</b> and the statement's first parameter is placeholder 1, and a
   * list ordered by the numbering Calcite gave the {@code ?}s would have them the other way round.
   * It is also why a parameter a text names twice is listed twice: two placeholders are two things
   * to bind, and the value behind them happens to be the same.
   *
   * <p>A key set writes its placeholder through the same writer method as a parameter and cannot be
   * told apart in the writer's record, so a subtree that carries one is handled the way it always
   * was — its key sets are appended by the caller, in their own order — and a subtree that carries
   * <em>both</em> is refused. The lookup rule already declines that shape (ADR 0022); this is the
   * assertion that it did.
   */
  private void statementParameters(
      RemoteQuery.Builder ir, SourceSql.Generated generated, RelNode pushed, boolean hasKeySets) {
    Map<Integer, RexDynamicParam> byIndex = new HashMap<>();
    for (RexDynamicParam parameter : dynamicParams(pushed)) {
      byIndex.put(parameter.getIndex(), parameter);
    }

    if (hasKeySets) {
      if (!byIndex.isEmpty()) {
        throw new IllegalStateException(
            "a pushed subtree carries both a key set and the statement's own parameters "
                + byIndex.keySet()
                + "; the two write the same placeholder and nothing can say which `?` is which");
      }
      return;
    }

    Set<Integer> bounds = boundIndexes(pushed);
    int position = 0;
    for (int index : generated.placeholders()) {
      RexDynamicParam parameter = byIndex.get(index);
      if (parameter == null) {
        throw new IllegalStateException(
            "the generated SQL writes a placeholder for parameter "
                + index
                + ", which the pushed subtree does not carry");
      }
      ir.addParameters(rex.convert(parameter));
      if (bounds.contains(index)) {
        ir.addRenderedBounds(position);
      }
      position++;
    }

    for (int index : bounds) {
      if (!byIndex.containsKey(index)) {
        throw new IllegalStateException(
            "a pushed bound reads parameter " + index + ", which the generated SQL does not write");
      }
    }
  }

  /** The key-set predicates in a pushed subtree, in the order they were found. */
  private static List<RexNode> keySets(RelNode rel) {
    List<RexNode> found = new ArrayList<>(1);
    rel.accept(
        new org.apache.calcite.rel.RelShuttleImpl() {
          @Override
          public RelNode visit(RelNode other) {
            other.accept(
                new org.apache.calcite.rex.RexShuttle() {
                  @Override
                  public RexNode visitCall(org.apache.calcite.rex.RexCall call) {
                    if (chalk.planner.plan.ChalkKeySet.is(call)) {
                      found.add(call);
                      return call;
                    }
                    return super.visitCall(call);
                  }
                });
            return super.visit(other);
          }
        });
    return found;
  }

  private Read read(ChalkTableScan scan) {
    ChalkTable table = scan.chalkTable();
    Read.Builder read =
        Read.newBuilder()
            .setTable(
                TableRef.newBuilder()
                    .setSourceId(table.sourceId())
                    .setSchema(table.schemaName())
                    .setTable(table.tableName()));
    read.addAllProjection(scan.projection());
    // The row goal a limit above this scan stated (D276). Zero means none and emits no bytes, so a
    // plan the goal rule never touched is byte-identical to one from before this step.
    if (scan.rowGoal() > 0) {
      read.setRowGoal(scan.rowGoal());
    }
    breadcrumbs(read, scan.getTable());
    correlation(read, scan.getTable());
    return read.build();
  }

  /**
   * The entitlement rewrite's breadcrumbs on a read (step 26, 16-entitlements.md §3.10).
   *
   * <p>A node the pass created has no identity after optimisation — filters merge, projections fold,
   * a scan becomes an index lookup — but every scan-derived rel copies the table handle the pass
   * wrapped, so the map is found on whatever the leaf has become. An entitled read carries exactly
   * one entry per column of the table, {@code FULL} included; a table with no entitlement carries
   * none, and an empty repeated field emits no bytes, so an unentitled plan is byte-identical to one
   * from before this step.
   *
   * <p>Beside them, the <b>descriptor hash</b> this read was compiled under (D231): the digest
   * covers the whole plan, so writing it here is what makes §1's "a changed policy is a new plan"
   * true by construction rather than by the folded literals happening to differ. An empty string
   * emits no bytes either, so the same byte-identity holds.
   */
  private static void breadcrumbs(Read.Builder read, org.apache.calcite.plan.RelOptTable table) {
    chalk.planner.entitlement.DisclosureMap map =
        chalk.planner.entitlement.EntitledRelOptTable.disclosureOf(table);
    if (map == null) {
      return;
    }
    read.addAllDisclosures(map.disclosures());
    read.setDescriptorHash(map.descriptorHash());
  }

  /**
   * The mark a path's own chain scan carries (D265 §2, §7): read raw for a correlation, disclosing
   * nothing, and unreachable from the statement. It is what lets the client's validator tell the
   * mechanism's occurrence of a table from the statement's own.
   */
  private static void correlation(
      Read.Builder read, org.apache.calcite.plan.RelOptTable table) {
    if (chalk.planner.entitlement.CorrelationRelOptTable.isCorrelation(table)) {
      read.setCorrelation(true);
    }
  }

  private static boolean isCorrelation(RelNode node) {
    return node instanceof org.apache.calcite.rel.core.TableScan scan
        && chalk.planner.entitlement.CorrelationRelOptTable.isCorrelation(scan.getTable());
  }

  /** Whether this rel's table carries a disclosure map, which is what {@code policy_injected} says. */
  private static boolean isEntitled(RelNode node) {
    return node instanceof org.apache.calcite.rel.core.TableScan scan
        && chalk.planner.entitlement.EntitledRelOptTable.disclosureOf(scan.getTable()) != null;
  }

  /**
   * An index lookup and its ranges (D37). The bounds are literals and parameters the matcher
   * already checked, so this is a straight conversion; {@code residual} is never set, because an M2
   * planner emits {@code Filter(IndexLookup)} and fuses nothing.
   */
  private IndexLookup indexLookup(ChalkIndexLookup lookup) {
    ChalkTable table = lookup.chalkTable();
    IndexLookup.Builder ir =
        IndexLookup.newBuilder()
            .setTable(
                TableRef.newBuilder()
                    .setSourceId(table.sourceId())
                    .setSchema(table.schemaName())
                    .setTable(table.tableName()))
            .setIndex(lookup.index().getName())
            .addAllProjection(lookup.projection());

    // The row goal a limit above this lookup stated (D276), on the same terms as a Read's.
    if (lookup.rowGoal() > 0) {
      ir.setRowGoal(lookup.rowGoal());
    }

    // D283: written only when the lookup reads backwards, so a plan that reads forwards is
    // byte-identical to one from before the field existed.
    if (lookup.reverse()) {
      ir.setReverse(true);
    }

    for (chalk.planner.plan.IndexMatcher.Range range : lookup.ranges()) {
      IndexRange.Builder bounds =
          IndexRange.newBuilder()
              .setLowerInclusive(range.lowerInclusive())
              .setUpperInclusive(range.upperInclusive());

      // D282: the last lower bound is a LIKE pattern rather than a value. Written only when set, so
      // every range that is not one is byte-identical to a range from before the field existed.
      if (range.prefix()) {
        bounds.setPrefix(true);
      }

      for (int i = 0; i < range.lower().size(); i++) {
        bounds.addLower(bound(range.lower().get(i), lookup, i));
      }
      for (int i = 0; i < range.upper().size(); i++) {
        bounds.addUpper(bound(range.upper().get(i), lookup, i));
      }
      ir.addRanges(bounds);
    }

    return ir.build();
  }

  /**
   * One range bound, in the key column's own IR type.
   *
   * <p>Not the Rex node's type: {@code ts >= TIMESTAMP '2026-01-03 00:00:00'} carries a TIMESTAMP(0)
   * literal, and converting it as such would count seconds where the source's keys count
   * nanoseconds. The comparison is legal in SQL and the *bound* has to be expressed in the column's
   * units for the source to compare it with anything (I-IR-2).
   */
  private Expr bound(RexNode node, ChalkIndexLookup lookup, int keyPosition) {
    Type type = lookup.chalkTable().column(lookup.index().getColumns(keyPosition)).getType();
    if (node instanceof RexLiteral literal) {
      return Expr.newBuilder()
          .setType(type)
          .setLiteral(LiteralConverter.convert(literal, type))
          .build();
    }

    // A parameter carries the type Calcite inferred for it, which the matcher already checked
    // against the key column's; the client binds it by that type.
    return rex.convert(node);
  }

  /**
   * One window node (D48). The group's constants have already been resolved into literals by
   * {@code ChalkWindowRule}, so every remaining reference indexes the input row and every offset,
   * bucket count and default is a constant this method can re-express at the type the IR reads it
   * at — exactly as {@link #bound} does for an index range.
   */
  private chalk.ir.v1.Window window(ChalkWindow node) {
    org.apache.calcite.rel.core.Window.Group group = node.group();
    RelDataType inputRow = node.getInput().getRowType();

    chalk.ir.v1.Window.Builder ir =
        chalk.ir.v1.Window.newBuilder().setInput(toRel(node.getInput()));
    for (int key : group.keys) {
      ir.addPartitionKeys(key);
    }
    ir.addAllOrder(sortFields(group.orderKeys, inputRow));
    ir.setFrame(frame(group, inputRow));
    for (org.apache.calcite.rel.core.Window.RexWinAggCall call : group.aggCalls) {
      ir.addCalls(windowCall(call, inputRow));
    }
    return ir.build();
  }

  /**
   * A window function's interval operand: a constant of {@code INTERVAL_DAY} kind, in the IR's
   * microseconds. Calcite hands it over as a millisecond literal, which {@link LiteralConverter}
   * rescales.
   */
  private Expr interval(RexNode operand) {
    return constant(
        operand, Type.newBuilder().setKind(TypeKind.TYPE_KIND_INTERVAL_DAY).build());
  }

  /** The frame, with the bounds the planner resolved — never "default" (D48). */
  private WindowFrame frame(
      org.apache.calcite.rel.core.Window.Group group, RelDataType inputRow) {
    Type offsetType = offsetType(group, inputRow);
    return WindowFrame.newBuilder()
        .setMode(group.isRows ? FrameMode.FRAME_MODE_ROWS : FrameMode.FRAME_MODE_RANGE)
        .setLower(frameBound(group.lowerBound, offsetType))
        .setUpper(frameBound(group.upperBound, offsetType))
        .setExclusion(exclusion(group.exclude))
        .build();
  }

  private FrameBound frameBound(RexWindowBound bound, Type offsetType) {
    FrameBound.Builder ir = FrameBound.newBuilder();
    if (bound.isCurrentRow()) {
      return ir.setKind(FrameBoundKind.FRAME_BOUND_KIND_CURRENT_ROW).build();
    }
    if (bound.isUnbounded()) {
      return ir.setKind(
              bound.isPreceding()
                  ? FrameBoundKind.FRAME_BOUND_KIND_UNBOUNDED_PRECEDING
                  : FrameBoundKind.FRAME_BOUND_KIND_UNBOUNDED_FOLLOWING)
          .build();
    }

    RexNode offset = bound.getOffset();
    if (offset == null) {
      throw new UnsupportedFeatureException(
          "window frame bound " + bound, "A PRECEDING or FOLLOWING bound must carry an offset.");
    }

    return ir.setKind(
            bound.isPreceding()
                ? FrameBoundKind.FRAME_BOUND_KIND_PRECEDING
                : FrameBoundKind.FRAME_BOUND_KIND_FOLLOWING)
        .setOffset(constant(offset, offsetType))
        .build();
  }

  /**
   * What kind a frame offset is read at. {@code ROWS} counts rows, so I64. {@code RANGE} compares the
   * single order key against {@code current ± offset}: an interval for a temporal key, and the key's
   * own kind for a numeric one.
   */
  private Type offsetType(
      org.apache.calcite.rel.core.Window.Group group, RelDataType inputRow) {
    if (group.isRows || group.orderKeys.getFieldCollations().size() != 1) {
      return Type.newBuilder().setKind(TypeKind.TYPE_KIND_I64).build();
    }

    int key = group.orderKeys.getFieldCollations().get(0).getFieldIndex();
    Type keyType = types.toIr(inputRow.getFieldList().get(key).getType());
    return switch (keyType.getKind()) {
      case TYPE_KIND_DATE, TYPE_KIND_TIMESTAMP, TYPE_KIND_TIMESTAMP_TZ ->
          Type.newBuilder().setKind(TypeKind.TYPE_KIND_INTERVAL_DAY).build();
      default -> keyType.toBuilder().setNullable(false).build();
    };
  }

  private static FrameExclusion exclusion(RexWindowExclusion exclude) {
    return switch (exclude) {
      case EXCLUDE_NO_OTHER -> FrameExclusion.FRAME_EXCLUSION_NO_OTHERS;
      case EXCLUDE_CURRENT_ROW -> FrameExclusion.FRAME_EXCLUSION_CURRENT_ROW;
      case EXCLUDE_GROUP -> FrameExclusion.FRAME_EXCLUSION_GROUP;
      case EXCLUDE_TIES -> FrameExclusion.FRAME_EXCLUSION_TIES;
    };
  }

  private WindowCall windowCall(
      org.apache.calcite.rel.core.Window.RexWinAggCall call, RelDataType inputRow) {
    SqlAggFunction function = (SqlAggFunction) call.getOperator();
    Type resultType = types.toIr(call.getType());
    WindowCall.Builder ir =
        WindowCall.newBuilder()
            .setType(resultType)
            .setDistinct(call.distinct)
            .setIgnoreNulls(call.ignoreNulls);

    chalk.planner.catalog.UserFunction declared =
        chalk.planner.plan.UserOperators.declarationOf(function);
    WindowFunctionId windowFunction = declared == null ? FunctionMapping.windowFunction(function) : null;
    if (declared != null) {
      ir.setUserFunction(userAggregateName(declared));
    } else if (windowFunction != null) {
      ir.setWindowFunction(windowFunction);
    } else {
      ir.setAggregate(FunctionMapping.aggregate(function));
    }

    List<RexNode> operands = call.getOperands();
    for (int i = 0; i < operands.size(); i++) {
      Type constantType = constantArgumentType(windowFunction, i, resultType);
      ir.addArgs(
          constantType == null ? rex.convert(operands.get(i)) : constant(operands.get(i), constantType));
    }

    // WindowCall.order_by exists for a holistic aggregate over a frame, and stays empty here:
    // Calcite 1.42's RexWinAggCall carries no collation, because WITHIN GROUP is not an aggregate
    // call and OVER therefore refuses one (V22, ADR 0018).
    return ir.build();
  }

  /**
   * The type a window call's <em>constant</em> argument is read at, or null when the argument is an
   * ordinary value expression. {@code NTILE(4)} counts buckets and {@code LAG(x, 2, d)} counts rows,
   * both I64; a {@code LAG}/{@code LEAD} default is read at the call's own result type, because
   * Calcite types the literal by itself — {@code LEAD(close, 1, 0.0)} carries a DECIMAL(2,1) default
   * for a DOUBLE call.
   */
  private static Type constantArgumentType(
      WindowFunctionId windowFunction, int index, Type resultType) {
    if (windowFunction == null) {
      return null;
    }

    Type i64 = Type.newBuilder().setKind(TypeKind.TYPE_KIND_I64).build();
    return switch (windowFunction) {
      case WINDOW_FUNCTION_ID_NTILE -> index == 0 ? i64 : null;
      case WINDOW_FUNCTION_ID_LAG, WINDOW_FUNCTION_ID_LEAD ->
          switch (index) {
            case 1 -> i64;
            case 2 -> resultType.toBuilder().setNullable(true).build();
            default -> null;
          };
      case WINDOW_FUNCTION_ID_NTH_VALUE -> index == 1 ? i64 : null;
      default -> null;
    };
  }

  /**
   * A constant argument in the type the IR reads it at. A literal is re-expressed (Calcite types
   * {@code 2} as INTEGER where the IR wants I64); a dynamic parameter keeps the type the client will
   * bind it by, and {@code PlanValidator} checks that the two agree.
   */
  private Expr constant(RexNode node, Type type) {
    if (node instanceof RexLiteral literal) {
      return Expr.newBuilder()
          .setType(type)
          .setLiteral(LiteralConverter.convert(literal, type))
          .build();
    }

    return rex.convert(node);
  }

  private Aggregate aggregate(org.apache.calcite.rel.core.Aggregate node) {
    if (node.getGroupSets().size() != 1) {
      throw new UnsupportedFeatureException(
          "grouping sets", "The IR accepts exactly one grouping in v1 (docs/design/02-ir.md §4).");
    }
    Aggregate.Builder aggregate = Aggregate.newBuilder().setInput(toRel(node.getInput()));
    Grouping.Builder grouping = Grouping.newBuilder();
    node.getGroupSet().forEach(grouping::addKeys);
    aggregate.addGroupings(grouping);

    RelDataType inputRow = node.getInput().getRowType();
    for (AggregateCall call : node.getAggCallList()) {
      Measure.Builder measure =
          Measure.newBuilder().setDistinct(call.isDistinct()).setType(types.toIr(call.getType()));
      chalk.planner.catalog.UserFunction declared =
          chalk.planner.plan.UserOperators.declarationOf(call.getAggregation());
      if (declared != null) {
        measure.setUserFunction(userAggregateName(declared));
      } else {
        measure.setFunction(FunctionMapping.aggregate(call.getAggregation()));
      }
      for (int a = 0; a < call.getArgList().size(); a++) {
        int argument = call.getArgList().get(a);
        measure.addArgs(
            isConstantArgument(call.getAggregation().getKind(), a)
                ? constantOrField(node.getInput(), argument, inputRow)
                : fieldRef(argument, inputRow));
      }
      if (call.filterArg >= 0) {
        // Calcite's filterArg names a boolean column; the IR wants a predicate over the input row.
        measure.setFilter(
            Expr.newBuilder()
                .setType(Type.newBuilder().setKind(TypeKind.TYPE_KIND_BOOL))
                .setCall(
                    ScalarCall.newBuilder()
                        .setFunction(FunctionId.FUNCTION_ID_IS_TRUE)
                        .addArgs(fieldRef(call.filterArg, inputRow)))
                .build());
      }
      // WITHIN GROUP (ORDER BY …), and ARRAY_AGG's own ORDER BY, which Calcite carries in the same
      // place (D57). The IR keeps it on the measure rather than requiring a Sort below, because it
      // orders each group's values and not the input.
      if (call.hasCollation()) {
        measure.addAllOrderBy(sortFields(call.getCollation(), inputRow));
      }
      aggregate.addMeasures(measure);
    }
    return aggregate.build();
  }

  /**
   * A client-bodied table function, as a leaf (D78). Its arguments are constants or dynamic
   * parameters over no input row, which is what makes it a leaf in the first place.
   */
  private chalk.ir.v1.TableFunctionScan tableFunctionScan(
      chalk.planner.plan.rel.ChalkTableFunctionScan scan) {
    chalk.planner.catalog.UserFunction declared = scan.declaration();
    if (!declared.isClientBodied()) {
      throw new UnsupportedFeatureException(
          "table function " + declared.qualifiedName(),
          "Only a client-bodied table function reaches the IR; a SQL-bodied one is a macro and has "
              + "already expanded (docs/design/17-user-defined-functions.md §2).");
    }

    chalk.ir.v1.TableFunctionScan.Builder ir =
        chalk.ir.v1.TableFunctionScan.newBuilder().setFunction(declared.qualifiedName());
    for (RexNode operand : ((org.apache.calcite.rex.RexCall) scan.getCall()).getOperands()) {
      ir.addArgs(rex.convert(operand));
    }
    return ir.build();
  }

  /**
   * The name a user aggregate travels under, having first refused the two forms that cannot reach a
   * plan: a native aggregate outside its source, and a SQL body the inliner should have removed.
   */
  private static String userAggregateName(chalk.planner.catalog.UserFunction declared) {
    if (declared.isNative()) {
      throw new UnsupportedFeatureException(
          "native function "
              + declared.qualifiedName()
              + " cannot be evaluated outside source "
              + declared.schemaName(),
          "A native aggregate is computed by its own source inside a pushed Aggregate; evaluating "
              + "it here would mean running somebody else's function "
              + "(docs/design/17-user-defined-functions.md §2).");
    }
    if (declared.isSqlBodied()) {
      throw new UnsupportedFeatureException(
          "SQL-bodied aggregate " + declared.qualifiedName() + " survived inlining",
          "A SQL-bodied aggregate is expanded into the built-in aggregates it is written over "
              + "before validation; reaching the IR means the inliner missed it.");
    }
    return declared.qualifiedName();
  }

  /**
   * Whether a holistic aggregate's argument at this position is a constant rather than a value: a
   * percentile's fraction and {@code LISTAGG}'s separator (D57). Calcite pushes both into the
   * projection below the aggregate and names the column, so they arrive as field references; the IR
   * wants the constant itself, exactly as a window's frame offsets do (ADR 0017).
   */
  private static boolean isConstantArgument(SqlKind aggregate, int position) {
    return switch (aggregate) {
      case PERCENTILE_CONT, PERCENTILE_DISC -> position == 0;
      case LISTAGG -> position == 1;
      default -> false;
    };
  }

  /**
   * The literal the input's projection puts in that column, or a field reference when it is not a
   * literal — in which case the executor refuses it, naming the aggregate.
   */
  private Expr constantOrField(RelNode input, int column, RelDataType inputRow) {
    if (input instanceof org.apache.calcite.rel.core.Project project
        && column < project.getProjects().size()
        && project.getProjects().get(column) instanceof RexLiteral literal) {
      Type type = types.toIr(literal.getType());
      return Expr.newBuilder()
          .setType(type)
          .setLiteral(LiteralConverter.convert(literal, type))
          .build();
    }

    return fieldRef(column, inputRow);
  }

  /**
   * An n-ary set operation and its inputs, in the order the query wrote them (D69). Every form but
   * {@code UNION ALL} compares whole rows, and a v1 LIST has no equality (D58), so a column of one
   * is refused here — where the caller can be told which column it was — rather than answered
   * wrongly downstream.
   */
  private chalk.ir.v1.SetOp setOp(org.apache.calcite.rel.core.SetOp rel, SetOpKind kind) {
    if (kind != SetOpKind.SET_OP_KIND_UNION_ALL) {
      for (org.apache.calcite.rel.type.RelDataTypeField field : rel.getRowType().getFieldList()) {
        if (types.toIr(field.getType()).getKind() == TypeKind.TYPE_KIND_LIST) {
          throw new UnsupportedFeatureException(
              rel.kind + " on the LIST column '" + field.getName() + "'",
              "v1 lists have no ordering or equality, so a set operation that compares rows cannot "
                  + "have one in its row (docs/design/14-windows-ii.md §5). UNION ALL, which "
                  + "compares nothing, is allowed.");
        }
        // D291: the same of a struct, which the statement's validation refuses first.
        if (field.getType().isStruct()) {
          throw new UnsupportedFeatureException(
              rel.kind + " on the struct column '" + field.getName() + "'",
              "A struct has no equality, so a set operation that compares rows cannot have one in "
                  + "its row. UNION ALL, which compares nothing, carries one "
                  + "(docs/design/51-structured-function-results.md §1).");
        }
      }
    }

    chalk.ir.v1.SetOp.Builder ir = chalk.ir.v1.SetOp.newBuilder().setKind(kind);
    for (RelNode input : rel.getInputs()) {
      ir.addInputs(toRel(input));
    }

    return ir.build();
  }

  private VirtualTable virtualTable(ChalkValues values) {
    VirtualTable.Builder table = VirtualTable.newBuilder();
    List<RelDataTypeField> fields = values.getRowType().getFieldList();
    for (List<RexLiteral> tuple : values.getTuples()) {
      VirtualRow.Builder row = VirtualRow.newBuilder();
      for (int i = 0; i < tuple.size(); i++) {
        Type type = types.toIr(fields.get(i).getType());
        row.addValues(
            Expr.newBuilder()
                .setType(type)
                .setLiteral(LiteralConverter.convert(tuple.get(i), type))
                .build());
      }
      table.addRows(row);
    }
    return table.build();
  }

  private Expr fieldRef(int index, RelDataType row) {
    return Expr.newBuilder()
        .setType(types.toIr(row.getFieldList().get(index).getType()))
        .setFieldRef(FieldRef.newBuilder().setIndex(index))
        .build();
  }

  private List<SortField> sortFields(RelCollation collation, RelDataType inputRow) {
    List<SortField> fields = new ArrayList<>(collation.getFieldCollations().size());
    for (RelFieldCollation field : collation.getFieldCollations()) {
      Expr key = fieldRef(field.getFieldIndex(), inputRow);

      // D58: a LIST has no ordering, so ordering by one is refused here rather than left for the
      // client's validator — the planner is where a caller can be told which column it was.
      if (key.getType().getKind() == TypeKind.TYPE_KIND_LIST) {
        throw new UnsupportedFeatureException(
            "ORDER BY on the LIST column '"
                + inputRow.getFieldList().get(field.getFieldIndex()).getName()
                + "'",
            "v1 lists have no ordering or equality; they can be produced, projected and indexed "
                + "into (docs/design/14-windows-ii.md §5).");
      }

      // D291: the same of a struct. The statement's validation refuses ORDER BY one by name first;
      // this is the refusal for an ordering a rule derived rather than the statement wrote.
      if (key.getType().getKind() == TypeKind.TYPE_KIND_STRUCT) {
        throw new UnsupportedFeatureException(
            "ordering on the struct column '"
                + inputRow.getFieldList().get(field.getFieldIndex()).getName()
                + "'",
            "A struct has no ordering: sort by one of its fields instead "
                + "(docs/design/51-structured-function-results.md §1).");
      }

      fields.add(
          SortField.newBuilder()
              .setExpr(key)
              .setDirection(ChalkTable.toIrDirection(field))
              .build());
    }
    return fields;
  }

  /**
   * The orderings this node's output is guaranteed to satisfy.
   *
   * <p>The union of two answers, because neither is complete on its own. The <b>trait set</b> is
   * what Volcano reasoned about, and it is the only answer for rels Calcite has no handler for —
   * {@code ChalkLimit} and {@code ChalkTopN} are plain {@code SingleRel}s, so {@code
   * RelMdCollation} says nothing about them. <b>{@code mq.collations}</b> derives the ordering from
   * the finished tree, and it is the only answer for a rel the top-down optimiser placed in a
   * subset that required no ordering: such a rel carries an empty collation trait while its input
   * still delivers one (D47). Under-claiming is not free — the {@code MergeJoin} validation rule
   * reads these claims back — so both are asked and the answers are merged.
   *
   * <p>Sorted and de-duplicated: {@code Collation} is comparable and the IR's list is a set.
   */
  private List<Collation> collations(RelNode node) {
    // getTraits, not getTrait: a node can satisfy several orderings at once (a one-row Values
    // satisfies every ordering), and getTrait throws when the trait set holds more than one.
    List<RelCollation> traits = node.getTraitSet().getTraits(RelCollationTraitDef.INSTANCE);
    List<RelCollation> derived = mq.collations(node);
    java.util.SortedSet<RelCollation> source = new java.util.TreeSet<>();
    if (traits != null) {
      source.addAll(traits);
    }
    if (derived != null) {
      source.addAll(derived);
    }

    List<Collation> collations = new ArrayList<>(source.size());
    for (RelCollation collation : source) {
      if (collation.getFieldCollations().isEmpty()) {
        continue;
      }
      collations.add(
          Collation.newBuilder()
              .addAllFields(sortFields(collation, node.getRowType()))
              .build());
    }
    return collations;
  }
}

package chalk.planner.entitlement;

import chalk.ir.v1.Disclosure;
import chalk.ir.v1.Type;
import chalk.ir.v1.TypeKind;
import chalk.planner.ErrorCode;
import chalk.planner.types.TypeMapper;
import com.google.common.collect.ImmutableList;
import java.math.BigDecimal;
import java.util.ArrayList;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Map;
import java.util.Set;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.core.Aggregate;
import org.apache.calcite.rel.core.AggregateCall;
import org.apache.calcite.rel.core.CorrelationId;
import org.apache.calcite.rel.core.Filter;
import org.apache.calcite.rel.core.Join;
import org.apache.calcite.rel.core.JoinRelType;
import org.apache.calcite.rel.core.Project;
import org.apache.calcite.rel.core.Sort;
import org.apache.calcite.rel.core.Union;
import org.apache.calcite.rel.hint.RelHint;
import org.apache.calcite.rel.logical.LogicalAggregate;
import org.apache.calcite.rel.logical.LogicalFilter;
import org.apache.calcite.rel.logical.LogicalJoin;
import org.apache.calcite.rel.logical.LogicalProject;
import org.apache.calcite.rel.logical.LogicalSort;
import org.apache.calcite.rel.logical.LogicalUnion;
import org.apache.calcite.rel.type.RelDataType;
import org.apache.calcite.rel.type.RelDataTypeFactory;
import org.apache.calcite.rex.RexBuilder;
import org.apache.calcite.rex.RexInputRef;
import org.apache.calcite.rex.RexNode;
import org.apache.calcite.rex.RexShuttle;
import org.apache.calcite.rex.RexSubQuery;
import org.apache.calcite.sql.fun.SqlStdOperatorTable;
import org.apache.calcite.sql.type.SqlTypeName;
import org.apache.calcite.util.ImmutableBitSet;
import org.apache.calcite.util.mapping.Mappings;

/**
 * The sibling {@code <name>__disclosure} columns (docs/design/16-entitlements.md §3.12, D207).
 *
 * <p>The per-column report says what a column discloses for the whole result. A sibling says it
 * <em>per row</em>, which is what a grid needs to render a masked cell differently from a value —
 * and for a row-dependent disclosure that answer exists nowhere but at the leaf, in the very rule
 * conditions the sanitiser chooses a value by. So the leaf computes it, as an integer of the report's
 * own order, and this class carries it to the root through whatever the statement's shape is.
 *
 * <p>The carry is deliberately narrow. A projection, a filter, a sort, a join and a {@code UNION ALL}
 * all keep a row a row, so a carried column travels through them and the answer stays exact. An
 * aggregate makes a row out of a group, and what that row discloses is the least of what the group's
 * rows did, which it carries as one more aggregate (F224, D363). A set operation that merges rows or
 * a window makes a row out of several in ways no such meet describes, so the carry stops there and
 * the sibling falls back to the report's own constant label for the column, which is the most that
 * position can honestly say. What a sibling never is, is absent: it is emitted
 * for every column with an entitled origin whether the disclosure varies or not, so a consumer has
 * one code path.
 *
 * <p>The meet of §3.12 is why the carried value is a number. A derived column over two entitled
 * origins discloses, for each row, the least of what its origins did, and comparing two ordinals is
 * an expression the executor already has; the names go on at the root, once.
 */
public final class DisclosureColumns {
  private DisclosureColumns() {}

  /** The name a carried column takes inside the plan. Never a caller's; the root renames them. */
  static final String CARRIED_PREFIX = "$disclosure$";

  /** The names a sibling holds, upper-cased as the Arrow metadata spells them (§3.12, D218). */
  private static final String[] NAMES = {
    "", "FULL", "MASKED", "REDACTED", "PER_ROW", "AGGREGATE", "TESTED"
  };

  /** A disclosure as its {@code ReportedDisclosure} ordinal, which is the order the meet takes. */
  static RexNode ordinal(Disclosure name, RexBuilder rexBuilder) {
    return rexBuilder.makeExactLiteral(
        BigDecimal.valueOf(
            switch (name) {
              case DISCLOSURE_FULL -> 1;
              case DISCLOSURE_MASKED -> 2;
              case DISCLOSURE_AGGREGATE_ONLY -> 5;
              case DISCLOSURE_TEST -> 6;
              default -> 3;
            }),
        rexBuilder.getTypeFactory().createSqlType(SqlTypeName.INTEGER));
  }

  private static int ordinalOf(Disclosed disclosed) {
    return switch (disclosed) {
      case FULL -> 1;
      case MASKED -> 2;
      case REDACTED -> 3;
      case PER_ROW -> 4;
      case AGGREGATE -> 5;
      case TESTED -> 6;
    };
  }

  /**
   * One node after the carry: the node itself, where each of its old columns went, and which of its
   * columns decide each old column's disclosure.
   */
  record Carried(RelNode rel, int[] originals, List<Set<Integer>> sources) {}

  /**
   * The tree with every leaf's carried columns pulled up as far as the statement's shape allows.
   *
   * @param carriers the narrowing projection each leaf handed back, and the wide one under it
   * @param emitted what each emitted leaf's columns disclose, for an input's constant labels
   */
  static Carried pullUp(
      RelNode rel,
      Map<RelNode, RelNode> carriers,
      Map<RelNode, Disclosed[]> emitted,
      RexBuilder rexBuilder) {
    RelNode wide = carriers.get(rel);
    if (wide != null) {
      int width = rel.getRowType().getFieldCount();
      List<Set<Integer>> sources = new ArrayList<>(width);
      for (int i = 0; i < width; i++) {
        sources.add(Set.of(width + i));
      }
      return new Carried(wide, identity(width), sources);
    }

    if (holdsSubQuery(rel)) {
      // A sub-query reads the outer row by index, and a correlation variable names a node's row as
      // it stands. Widening under one of those would move what they point at, so the carry stops
      // here and everything below it goes back to the arity it had.
      return barrier(rel, carriers, emitted, rexBuilder);
    }

    if (rel instanceof Project project) {
      Carried in = pullUp(project.getInput(), carriers, emitted, rexBuilder);
      if (isCarrying(in)) {
        return project(project, in, rexBuilder);
      }
      return barrier(rel, carriers, emitted, rexBuilder);
    }
    if (rel instanceof Filter filter) {
      Carried in = pullUp(filter.getInput(), carriers, emitted, rexBuilder);
      if (isCarrying(in)) {
        return new Carried(
            LogicalFilter.create(in.rel(), permute(filter.getCondition(), in.originals(), rexBuilder)),
            in.originals(),
            in.sources());
      }
      return barrier(rel, carriers, emitted, rexBuilder);
    }
    if (rel instanceof Sort sort) {
      Carried in = pullUp(sort.getInput(), carriers, emitted, rexBuilder);
      if (isCarrying(in)) {
        return new Carried(
            LogicalSort.create(
                in.rel(),
                org.apache.calcite.rel.RelCollations.permute(
                    sort.getCollation(),
                    Mappings.target(i -> in.originals()[i], in.originals().length,
                        in.rel().getRowType().getFieldCount())),
                sort.offset,
                sort.fetch),
            in.originals(),
            in.sources());
      }
      return barrier(rel, carriers, emitted, rexBuilder);
    }
    if (rel instanceof Join join && join.getJoinType().projectsRight()) {
      return join(join, carriers, emitted, rexBuilder);
    }
    if (rel instanceof Aggregate aggregate) {
      return aggregate(aggregate, carriers, emitted, rexBuilder);
    }
    if (rel instanceof Union union && union.all) {
      return union(union, carriers, emitted, rexBuilder);
    }
    return barrier(rel, carriers, emitted, rexBuilder);
  }

  private static boolean isCarrying(Carried carried) {
    for (Set<Integer> sources : carried.sources()) {
      if (!sources.isEmpty()) {
        return true;
      }
    }
    return false;
  }

  /** The projection, with its own expressions remapped and the input's carried columns appended. */
  private static Carried project(Project project, Carried in, RexBuilder rexBuilder) {
    List<Integer> carried = new ArrayList<>(distinct(in.sources()));
    List<RexNode> exprs = new ArrayList<>(project.getProjects().size() + carried.size());
    List<String> names = new ArrayList<>(project.getRowType().getFieldNames());
    List<Set<Integer>> sources = new ArrayList<>(project.getProjects().size());

    int at = project.getProjects().size();
    for (RexNode expr : project.getProjects()) {
      RexNode permuted = permute(expr, in.originals(), rexBuilder);
      exprs.add(permuted);
      Set<Integer> of = new LinkedHashSet<>();
      for (int column : reads(expr)) {
        for (int source : in.sources().get(column)) {
          of.add(at + carried.indexOf(source));
        }
      }
      sources.add(of);
    }

    for (int source : carried) {
      exprs.add(rexBuilder.makeInputRef(in.rel(), source));
      names.add(CARRIED_PREFIX + names.size());
    }

    return new Carried(
        LogicalProject.create(
            in.rel(), ImmutableList.<RelHint>of(), exprs, names, Set.<CorrelationId>of()),
        identity(project.getProjects().size()),
        sources);
  }

  /** The join, with both sides carrying and the right's columns moved along by what the left added. */
  private static Carried join(
      Join join,
      Map<RelNode, RelNode> carriers,
      Map<RelNode, Disclosed[]> emitted,
      RexBuilder rexBuilder) {
    Carried left = pullUp(join.getLeft(), carriers, emitted, rexBuilder);
    Carried right = pullUp(join.getRight(), carriers, emitted, rexBuilder);
    if (!isCarrying(left) && !isCarrying(right)) {
      return barrier(join, carriers, emitted, rexBuilder);
    }

    int oldLeft = join.getLeft().getRowType().getFieldCount();
    int newLeft = left.rel().getRowType().getFieldCount();
    int width = join.getRowType().getFieldCount();
    int[] originals = new int[width];
    List<Set<Integer>> sources = new ArrayList<>(width);
    for (int i = 0; i < width; i++) {
      if (i < oldLeft) {
        originals[i] = left.originals()[i];
        sources.add(left.sources().get(i));
      } else {
        originals[i] = newLeft + right.originals()[i - oldLeft];
        Set<Integer> shifted = new LinkedHashSet<>();
        for (int source : right.sources().get(i - oldLeft)) {
          shifted.add(newLeft + source);
        }
        sources.add(shifted);
      }
    }

    RexNode condition = permute(join.getCondition(), originals, rexBuilder);
    // An ASOF join stays one, its match condition carried over the same row (D337): Calcite reads a
    // join of an ASOF type as a LogicalAsofJoin, and a plain join of that type would lose the match.
    RelNode rebuilt =
        join instanceof org.apache.calcite.rel.core.AsofJoin asof
            ? org.apache.calcite.rel.logical.LogicalAsofJoin.create(
                left.rel(),
                right.rel(),
                ImmutableList.<RelHint>of(),
                condition,
                permute(asof.getMatchCondition(), originals, rexBuilder),
                join.getJoinType(),
                ImmutableList.of())
            : LogicalJoin.create(
                left.rel(),
                right.rel(),
                ImmutableList.<RelHint>of(),
                condition,
                join.getVariablesSet(),
                join.getJoinType());
    return new Carried(rebuilt, originals, sources);
  }

  /**
   * An aggregate (F224, D363): each of its rows is made of a group of rows, so what it discloses is
   * the least of what those rows disclosed. A key's disclosure is the meet of its own carried columns
   * over the group, and a measure's the meet of its arguments' and its filter's over the rows its
   * filter keeps. Each is one {@code MAX} of an ordinal beside the aggregate's own calls, over a
   * projection that computes the ordinal row by row.
   *
   * <p>Only a column whose whole-result label is {@code PER_ROW} is carried. A constant label already
   * says what every group discloses, so the plan stays as it was there, and so does a population-only
   * or tested column, whose label the guard and the trace key on. Grouping sets keep the constant
   * label too, as does a call ordered within its group or distinct on keys of its own.
   */
  private static Carried aggregate(
      Aggregate aggregate,
      Map<RelNode, RelNode> carriers,
      Map<RelNode, Disclosed[]> emitted,
      RexBuilder rexBuilder) {
    Carried in = pullUp(aggregate.getInput(), carriers, emitted, rexBuilder);
    if (!isCarrying(in) || aggregate.getGroupType() != Aggregate.Group.SIMPLE) {
      return barrier(aggregate, carriers, emitted, rexBuilder);
    }
    for (AggregateCall call : aggregate.getAggCallList()) {
      if (!call.getCollation().getFieldCollations().isEmpty()
          || call.distinctKeys != null
          || !call.rexList.isEmpty()) {
        return barrier(aggregate, carriers, emitted, rexBuilder);
      }
    }

    Disclosed[] labels = DisclosureFlow.of(aggregate, emitted);
    List<Integer> keys = aggregate.getGroupSet().asList();
    List<AggregateCall> calls = aggregate.getAggCallList();
    int groups = keys.size();
    int width = aggregate.getRowType().getFieldCount();

    // For each output column carried: the meet of its sources, row by row, and the filter it is
    // taken under.
    List<RexNode> ordinals = new ArrayList<>();
    List<Integer> filters = new ArrayList<>();
    int[] carriedAt = new int[width];
    java.util.Arrays.fill(carriedAt, -1);
    for (int out = 0; out < width; out++) {
      if (labels[out] != Disclosed.PER_ROW) {
        continue;
      }
      List<Integer> reads = new ArrayList<>();
      int filter = -1;
      if (out < groups) {
        reads.add(keys.get(out));
      } else {
        AggregateCall call = calls.get(out - groups);
        reads.addAll(call.getArgList());
        if (call.filterArg >= 0) {
          reads.add(call.filterArg);
          filter = in.originals()[call.filterArg];
        }
      }
      Set<Integer> sources = new LinkedHashSet<>();
      for (int column : reads) {
        sources.addAll(in.sources().get(column));
      }
      if (sources.isEmpty()) {
        continue;
      }
      carriedAt[out] = ordinals.size();
      ordinals.add(meet(in, new ArrayList<>(sources), rexBuilder));
      filters.add(filter);
    }
    if (ordinals.isEmpty()) {
      return barrier(aggregate, carriers, emitted, rexBuilder);
    }

    // Below the aggregate: every column of the carrying input, then each ordinal.
    int inWidth = in.rel().getRowType().getFieldCount();
    RelDataType ordinal = rexBuilder.getTypeFactory().createSqlType(SqlTypeName.INTEGER);
    List<RexNode> exprs = new ArrayList<>(inWidth + ordinals.size());
    List<String> names = new ArrayList<>(in.rel().getRowType().getFieldNames());
    for (int i = 0; i < inWidth; i++) {
      exprs.add(rexBuilder.makeInputRef(in.rel(), i));
    }
    for (int j = 0; j < ordinals.size(); j++) {
      exprs.add(rexBuilder.ensureType(ordinal, ordinals.get(j), true));
      names.add(CARRIED_PREFIX + (inWidth + j));
    }
    RelNode below =
        LogicalProject.create(
            in.rel(), ImmutableList.<RelHint>of(), exprs, names, Set.<CorrelationId>of());

    // The aggregate itself over the carrying input's places, and one MAX for each ordinal.
    List<Integer> moved = new ArrayList<>(groups);
    for (int key : keys) {
      moved.add(in.originals()[key]);
    }
    ImmutableBitSet groupSet = ImmutableBitSet.of(moved);
    List<AggregateCall> rebuilt = new ArrayList<>(calls.size() + ordinals.size());
    for (AggregateCall call : calls) {
      rebuilt.add(moved(call, in.originals()));
    }
    for (int j = 0; j < ordinals.size(); j++) {
      rebuilt.add(least(below, inWidth + j, filters.get(j), groupSet.cardinality()));
    }
    RelNode grouped =
        LogicalAggregate.create(below, aggregate.getHints(), groupSet, null, rebuilt);

    // A key's place among the new keys follows its new input place, which may have moved.
    List<Integer> ordered = groupSet.asList();
    int[] originals = new int[width];
    List<Set<Integer>> sources = new ArrayList<>(width);
    for (int out = 0; out < width; out++) {
      originals[out] = out < groups ? ordered.indexOf(moved.get(out)) : out;
      sources.add(
          carriedAt[out] < 0 ? Set.of() : Set.of(groups + calls.size() + carriedAt[out]));
    }
    return new Carried(grouped, originals, sources);
  }

  /** A call over the carrying input: its arguments and its filter at their new places. */
  @SuppressWarnings("deprecation")
  private static AggregateCall moved(AggregateCall call, int[] originals) {
    List<Integer> args = new ArrayList<>(call.getArgList().size());
    for (int arg : call.getArgList()) {
      args.add(originals[arg]);
    }
    int filter = call.filterArg < 0 ? -1 : originals[call.filterArg];
    return call.copy(args, filter, null, call.getCollation());
  }

  /** The least disclosure among a group's rows: the greatest of their ordinals, under a filter. */
  @SuppressWarnings("deprecation")
  private static AggregateCall least(RelNode input, int column, int filter, int groupCount) {
    return AggregateCall.create(
        SqlStdOperatorTable.MAX,
        /* distinct= */ false,
        /* approximate= */ false,
        /* ignoreNulls= */ false,
        List.of(),
        List.of(column),
        filter,
        (ImmutableBitSet) null,
        org.apache.calcite.rel.RelCollations.EMPTY,
        groupCount,
        input,
        null,
        null);
  }

  /**
   * A {@code UNION ALL}: every row is one input's row, so each input appends, for every column, what
   * that row disclosed — the meet of what it carried there or, where it carried nothing, its own
   * constant label for the column — and the union carries one column for each. A leaf split over a
   * membership into a semi-join branch and an anti-join branch is one of these (F36).
   */
  private static Carried union(
      Union union,
      Map<RelNode, RelNode> carriers,
      Map<RelNode, Disclosed[]> emitted,
      RexBuilder rexBuilder) {
    List<Carried> carried = new ArrayList<>(union.getInputs().size());
    boolean carrying = false;
    for (RelNode input : union.getInputs()) {
      Carried in = pullUp(input, carriers, emitted, rexBuilder);
      carried.add(in);
      carrying |= isCarrying(in);
    }
    if (!carrying) {
      return barrier(union, carriers, emitted, rexBuilder);
    }

    int width = union.getRowType().getFieldCount();
    RelDataType ordinal = rexBuilder.getTypeFactory().createSqlType(SqlTypeName.INTEGER);
    List<RelNode> inputs = new ArrayList<>(carried.size());
    for (int i = 0; i < carried.size(); i++) {
      Carried in = carried.get(i);
      Disclosed[] own = DisclosureFlow.of(union.getInputs().get(i), emitted);
      List<RexNode> exprs = new ArrayList<>(width * 2);
      List<String> names = new ArrayList<>(width * 2);
      List<String> produced = in.rel().getRowType().getFieldNames();
      for (int f = 0; f < width; f++) {
        exprs.add(rexBuilder.makeInputRef(in.rel(), in.originals()[f]));
        names.add(produced.get(in.originals()[f]));
      }
      for (int f = 0; f < width; f++) {
        List<Integer> sources = new ArrayList<>(in.sources().get(f));
        RexNode disclosed =
            sources.isEmpty()
                ? rexBuilder.makeExactLiteral(BigDecimal.valueOf(ordinalOf(own[f])), ordinal)
                : meet(in, sources, rexBuilder);
        exprs.add(rexBuilder.ensureType(ordinal, disclosed, true));
        names.add(CARRIED_PREFIX + (width + f));
      }
      inputs.add(
          LogicalProject.create(
              in.rel(), ImmutableList.<RelHint>of(), exprs, names, Set.<CorrelationId>of()));
    }

    List<Set<Integer>> sources = new ArrayList<>(width);
    for (int f = 0; f < width; f++) {
      sources.add(Set.of(width + f));
    }
    return new Carried(LogicalUnion.create(inputs, true), identity(width), sources);
  }

  /**
   * A node the carry cannot pass: its inputs go back to the arity they had, and it is rebuilt over
   * them unchanged. Everything above it falls back to the report's constant label.
   */
  private static Carried barrier(
      RelNode rel,
      Map<RelNode, RelNode> carriers,
      Map<RelNode, Disclosed[]> emitted,
      RexBuilder rexBuilder) {
    List<RelNode> inputs = new ArrayList<>(rel.getInputs().size());
    boolean changed = false;
    for (RelNode input : rel.getInputs()) {
      Carried in = pullUp(input, carriers, emitted, rexBuilder);
      RelNode narrowed = narrow(input, in, rexBuilder);
      changed |= narrowed != input;
      inputs.add(narrowed);
    }
    RelNode rebuilt = changed ? rel.copy(rel.getTraitSet(), inputs) : rel;
    int width = rel.getRowType().getFieldCount();
    List<Set<Integer>> sources = new ArrayList<>(width);
    for (int i = 0; i < width; i++) {
      sources.add(Set.of());
    }
    return new Carried(rebuilt, identity(width), sources);
  }

  /** {@code carried} back to exactly the columns {@code original} had, in their order. */
  private static RelNode narrow(RelNode original, Carried carried, RexBuilder rexBuilder) {
    if (carried.rel() == original) {
      return original;
    }
    int width = original.getRowType().getFieldCount();
    List<RexNode> refs = new ArrayList<>(width);
    for (int i = 0; i < width; i++) {
      refs.add(rexBuilder.makeInputRef(carried.rel(), carried.originals()[i]));
    }
    return LogicalProject.create(
        carried.rel(),
        ImmutableList.<RelHint>of(),
        refs,
        original.getRowType().getFieldNames(),
        Set.<CorrelationId>of());
  }

  /**
   * The root with one sibling column per output column, appended in the root's own order (§3.12).
   *
   * <p>The caller reads column {@code f}'s sibling at {@code width + f}: the pipeline knows which of
   * them the statement's output names, and names them there.
   */
  static RelNode siblings(Carried carried, Disclosed[] flow, RexBuilder rexBuilder) {
    RelDataTypeFactory typeFactory = rexBuilder.getTypeFactory();
    RelDataType stringType =
        new TypeMapper(typeFactory)
            .toCalcite(Type.newBuilder().setKind(TypeKind.TYPE_KIND_STRING).build());

    int width = flow.length;
    List<RexNode> exprs = new ArrayList<>(width * 2);
    List<String> names = new ArrayList<>(width * 2);
    List<String> produced = carried.rel().getRowType().getFieldNames();
    for (int f = 0; f < width; f++) {
      exprs.add(rexBuilder.makeInputRef(carried.rel(), carried.originals()[f]));
      names.add(produced.get(carried.originals()[f]));
    }
    for (int f = 0; f < width; f++) {
      exprs.add(name(carried, flow, f, stringType, rexBuilder));
      names.add(CARRIED_PREFIX + f);
    }
    return LogicalProject.create(
        carried.rel(), ImmutableList.<RelHint>of(), exprs, names, Set.<CorrelationId>of());
  }

  /** One column's sibling: the meet of what reached the root, or the report's constant label. */
  private static RexNode name(
      Carried carried, Disclosed[] flow, int column, RelDataType stringType, RexBuilder rexBuilder) {
    List<Integer> sources = new ArrayList<>(carried.sources().get(column));
    if (sources.isEmpty()) {
      return rexBuilder.makeLiteral(NAMES[ordinalOf(flow[column])], stringType);
    }

    RexNode meet = meet(carried, sources, rexBuilder);

    List<RexNode> operands = new ArrayList<>();
    for (int name = 1; name < NAMES.length; name++) {
      operands.add(
          rexBuilder.makeCall(
              SqlStdOperatorTable.EQUALS,
              meet,
              rexBuilder.makeExactLiteral(
                  BigDecimal.valueOf(name),
                  rexBuilder.getTypeFactory().createSqlType(SqlTypeName.INTEGER))));
      operands.add(rexBuilder.makeLiteral(NAMES[name], stringType));
    }
    operands.add(rexBuilder.makeLiteral(NAMES[1], stringType));
    return rexBuilder.makeCall(SqlStdOperatorTable.CASE, operands);
  }

  /** The meet of carried columns {@code sources}: the greatest ordinal, the least disclosure. */
  private static RexNode meet(Carried carried, List<Integer> sources, RexBuilder rexBuilder) {
    RexNode meet = rexBuilder.makeInputRef(carried.rel(), sources.get(0));
    for (int i = 1; i < sources.size(); i++) {
      RexNode next = rexBuilder.makeInputRef(carried.rel(), sources.get(i));
      meet =
          rexBuilder.makeCall(
              SqlStdOperatorTable.CASE,
              rexBuilder.makeCall(SqlStdOperatorTable.GREATER_THAN_OR_EQUAL, meet, next),
              meet,
              next);
    }
    return meet;
  }

  /**
   * The root's field list with each column's sibling beside it (§3.12, D207).
   *
   * <p>A suffixed name the statement already produces is refused rather than uniquified: a consumer
   * looks a sibling up by name, and a silent rename would be a silent failure.
   */
  public static org.apache.calcite.runtime.ImmutablePairList<Integer, String> extend(
      org.apache.calcite.runtime.ImmutablePairList<Integer, String> fields,
      int at,
      boolean[] present,
      String suffix) {
    Set<String> taken = new LinkedHashSet<>(fields.rightList());
    org.apache.calcite.runtime.PairList<Integer, String> extended =
        org.apache.calcite.runtime.PairList.of();
    for (int i = 0; i < fields.size(); i++) {
      int column = fields.leftList().get(i);
      String name = fields.rightList().get(i);
      extended.add(column, name);
      if (column >= present.length || !present[column]) {
        continue;
      }
      String sibling = name + suffix;
      if (taken.contains(sibling)) {
        throw new PolicyException(
            ErrorCode.DISCLOSURE_NAME_COLLISION,
            "",
            sibling,
            "",
            List.of(),
            "this statement already produces a column called '"
                + sibling
                + "', and PrepareOptions.IncludeDisclosureColumns would name the sibling of '"
                + name
                + "' the same. A consumer looks a sibling up by name, so it is refused rather than"
                + " renamed: choose another DisclosureColumnSuffix, or rename the column.");
      }
      extended.add(at + column, sibling);
    }
    return extended.immutable();
  }

  // ------------------------------------------------------------------ the small parts

  private static int[] identity(int width) {
    int[] map = new int[width];
    for (int i = 0; i < width; i++) {
      map[i] = i;
    }
    return map;
  }

  private static Set<Integer> distinct(List<Set<Integer>> sources) {
    Set<Integer> all = new LinkedHashSet<>();
    for (Set<Integer> of : sources) {
      all.addAll(of);
    }
    return all;
  }

  private static List<Integer> reads(RexNode expr) {
    List<Integer> columns = new ArrayList<>();
    for (int column : org.apache.calcite.plan.RelOptUtil.InputFinder.bits(expr)) {
      columns.add(column);
    }
    return columns;
  }

  private static RexNode permute(RexNode expr, int[] map, RexBuilder rexBuilder) {
    return expr.accept(
        new RexShuttle() {
          @Override
          public RexNode visitInputRef(RexInputRef ref) {
            return ref.getIndex() < map.length && map[ref.getIndex()] != ref.getIndex()
                ? rexBuilder.makeInputRef(ref.getType(), map[ref.getIndex()])
                : ref;
          }
        });
  }

  private static boolean holdsSubQuery(RelNode rel) {
    boolean[] found = {false};
    rel.accept(
        new RexShuttle() {
          @Override
          public RexNode visitSubQuery(RexSubQuery subQuery) {
            found[0] = true;
            return subQuery;
          }
        });
    return found[0] || !rel.getVariablesSet().isEmpty() || holdsCorrelation(rel);
  }

  private static boolean holdsCorrelation(RelNode rel) {
    boolean[] found = {false};
    rel.accept(
        new RexShuttle() {
          @Override
          public RexNode visitCorrelVariable(org.apache.calcite.rex.RexCorrelVariable variable) {
            found[0] = true;
            return variable;
          }
        });
    return found[0];
  }
}

package chalk.planner.entitlement;

import chalk.ir.v1.Disclosure;
import chalk.ir.v1.Type;
import chalk.ir.v1.TypeKind;
import chalk.planner.types.TypeMapper;
import com.google.common.collect.ImmutableList;
import java.math.BigDecimal;
import java.util.ArrayList;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Map;
import java.util.Set;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.core.CorrelationId;
import org.apache.calcite.rel.core.Filter;
import org.apache.calcite.rel.core.Join;
import org.apache.calcite.rel.core.JoinRelType;
import org.apache.calcite.rel.core.Project;
import org.apache.calcite.rel.core.Sort;
import org.apache.calcite.rel.hint.RelHint;
import org.apache.calcite.rel.logical.LogicalFilter;
import org.apache.calcite.rel.logical.LogicalJoin;
import org.apache.calcite.rel.logical.LogicalProject;
import org.apache.calcite.rel.logical.LogicalSort;
import org.apache.calcite.rel.type.RelDataType;
import org.apache.calcite.rel.type.RelDataTypeFactory;
import org.apache.calcite.rex.RexBuilder;
import org.apache.calcite.rex.RexInputRef;
import org.apache.calcite.rex.RexNode;
import org.apache.calcite.rex.RexShuttle;
import org.apache.calcite.rex.RexSubQuery;
import org.apache.calcite.sql.fun.SqlStdOperatorTable;
import org.apache.calcite.sql.type.SqlTypeName;
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
 * <p>The carry is deliberately narrow. A projection, a filter, a sort and a join all keep a row a
 * row, so a carried column travels through them and the answer stays exact; an aggregate, a set
 * operation or a window makes a row out of several and the leaf's per-row answer means nothing there,
 * so the carry stops and the sibling falls back to the report's own constant label for the column,
 * which is the most that position can honestly say. What a sibling never is, is absent: it is emitted
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
   */
  static Carried pullUp(RelNode rel, Map<RelNode, RelNode> carriers, RexBuilder rexBuilder) {
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
      return barrier(rel, carriers, rexBuilder);
    }

    if (rel instanceof Project project) {
      Carried in = pullUp(project.getInput(), carriers, rexBuilder);
      if (isCarrying(in)) {
        return project(project, in, rexBuilder);
      }
      return barrier(rel, carriers, rexBuilder);
    }
    if (rel instanceof Filter filter) {
      Carried in = pullUp(filter.getInput(), carriers, rexBuilder);
      if (isCarrying(in)) {
        return new Carried(
            LogicalFilter.create(in.rel(), permute(filter.getCondition(), in.originals(), rexBuilder)),
            in.originals(),
            in.sources());
      }
      return barrier(rel, carriers, rexBuilder);
    }
    if (rel instanceof Sort sort) {
      Carried in = pullUp(sort.getInput(), carriers, rexBuilder);
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
      return barrier(rel, carriers, rexBuilder);
    }
    if (rel instanceof Join join && join.getJoinType().projectsRight()) {
      return join(join, carriers, rexBuilder);
    }
    return barrier(rel, carriers, rexBuilder);
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
  private static Carried join(Join join, Map<RelNode, RelNode> carriers, RexBuilder rexBuilder) {
    Carried left = pullUp(join.getLeft(), carriers, rexBuilder);
    Carried right = pullUp(join.getRight(), carriers, rexBuilder);
    if (!isCarrying(left) && !isCarrying(right)) {
      return barrier(join, carriers, rexBuilder);
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

    return new Carried(
        LogicalJoin.create(
            left.rel(),
            right.rel(),
            ImmutableList.<RelHint>of(),
            permute(join.getCondition(), originals, rexBuilder),
            join.getVariablesSet(),
            join.getJoinType()),
        originals,
        sources);
  }

  /**
   * A node the carry cannot pass: its inputs go back to the arity they had, and it is rebuilt over
   * them unchanged. Everything above it falls back to the report's constant label.
   */
  private static Carried barrier(RelNode rel, Map<RelNode, RelNode> carriers, RexBuilder rexBuilder) {
    List<RelNode> inputs = new ArrayList<>(rel.getInputs().size());
    boolean changed = false;
    for (RelNode input : rel.getInputs()) {
      Carried in = pullUp(input, carriers, rexBuilder);
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
            "this statement already produces a column called '"
                + sibling
                + "', and PrepareOptions.IncludeDisclosureColumns would name the sibling of '"
                + name
                + "' the same. A consumer looks a sibling up by name, so it is refused rather than"
                + " renamed: choose another DisclosureColumnSuffix, or rename the column"
                + " (docs/design/16-entitlements.md §3.12).");
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

package chalk.planner.entitlement;

import chalk.ir.v1.ColumnEntitlement;
import chalk.ir.v1.DisclosureRule;
import chalk.ir.v1.TableEntitlement;
import chalk.planner.catalog.ChalkTable;
import com.google.common.collect.ImmutableList;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Collections;
import java.util.List;
import org.apache.calcite.plan.RelOptTable;
import org.apache.calcite.rex.RexNode;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * One entitled table's descriptor, converted through the query's own cluster (ADR 0025's second
 * finding; docs/design/16-entitlements.md §3.2).
 *
 * <p>Every {@code RexNode} here indexes the <em>table's</em> row type, so the expressions drop
 * straight onto the leaf's scan. The conversion runs once per entitled table per request — it is the
 * expensive half and it depends on the context but not on which occurrence of the table is being
 * rewritten; simplification under a leaf's own conjuncts happens afterwards, per leaf (§3.3).
 */
public final class DescriptorExpressions {
  private final @Nullable RexNode rowPredicate;
  private final ImmutableList<Column> columns;

  /** One entitled column's converted expressions, in the descriptor's own order. */
  public static final class Column {
    private final int tableColumn;
    private final ColumnEntitlement entitlement;
    private final ImmutableList<RexNode> ruleConditions;

    /** Per rule, its own mask or null; {@link List} rather than {@code ImmutableList} for the nulls. */
    private final List<@Nullable RexNode> ruleMasks;

    /** Per rule, its own placeholder or null (D224). Same shape and same reason as the masks. */
    private final List<@Nullable RexNode> rulePlaceholders;

    private final @Nullable RexNode mask;
    private final @Nullable RexNode placeholder;

    Column(
        int tableColumn,
        ColumnEntitlement entitlement,
        ImmutableList<RexNode> ruleConditions,
        List<@Nullable RexNode> ruleMasks,
        List<@Nullable RexNode> rulePlaceholders,
        @Nullable RexNode mask,
        @Nullable RexNode placeholder) {
      this.tableColumn = tableColumn;
      this.entitlement = entitlement;
      this.ruleConditions = ruleConditions;
      this.ruleMasks = ruleMasks;
      this.rulePlaceholders = rulePlaceholders;
      this.mask = mask;
      this.placeholder = placeholder;
    }

    public int tableColumn() {
      return tableColumn;
    }

    public ColumnEntitlement entitlement() {
      return entitlement;
    }

    public List<DisclosureRule> rules() {
      return entitlement.getRulesList();
    }

    public RexNode condition(int rule) {
      return ruleConditions.get(rule);
    }

    /** The mask this rule uses: its own, or else the column's (§3.2). Null when there is neither. */
    public @Nullable RexNode maskOf(int rule) {
      RexNode own = ruleMasks.get(rule);
      return own == null ? mask : own;
    }

    public @Nullable RexNode mask() {
      return mask;
    }

    public @Nullable RexNode placeholder() {
      return placeholder;
    }

    /**
     * What stands in where this rule redacts the value: the rule's own placeholder, else the
     * column's (D224). Null when there is neither, and the request's {@code PlaceholderPolicy} then
     * decides. {@code rule < 0} is the {@code otherwise} branch, which has no rule to ask.
     */
    public @Nullable RexNode placeholderOf(int rule) {
      if (rule < 0) {
        return placeholder;
      }
      RexNode own = rulePlaceholders.get(rule);
      return own == null ? placeholder : own;
    }

    /** The group-size floor this column's population aggregates are guarded by, or 0 to inherit. */
    public int minGroupSize() {
      return entitlement.getMinGroupSize();
    }

    /** The population aggregates permitted over this column, upper-cased (D190). */
    public List<String> allowList() {
      List<String> names = new ArrayList<>(entitlement.getAggregateOnlyFunctionsCount());
      for (String name : entitlement.getAggregateOnlyFunctionsList()) {
        names.add(name.trim().toUpperCase(java.util.Locale.ROOT));
      }
      return names;
    }
  }

  private DescriptorExpressions(@Nullable RexNode rowPredicate, List<Column> columns) {
    this.rowPredicate = rowPredicate;
    this.columns = ImmutableList.copyOf(columns);
  }

  /** The folded row predicate, or null when the descriptor states none (every row is visible). */
  public @Nullable RexNode rowPredicate() {
    return rowPredicate;
  }

  public ImmutableList<Column> columns() {
    return columns;
  }

  /** The entitled column with this table ordinal, or null when the descriptor does not name it. */
  public @Nullable Column column(int tableColumn) {
    for (Column column : columns) {
      if (column.tableColumn() == tableColumn) {
        return column;
      }
    }
    return null;
  }

  /**
   * The same expressions with {@code rewrite} applied to every one of them.
   *
   * <p>What needs it is the branch split of F36: on a branch where a membership test is known to
   * hold, or known not to, every rule condition that reads it must take that answer too — otherwise
   * the sub-query survives into {@code Project_D}, where it is the shape the split exists to avoid.
   */
  public DescriptorExpressions rewrite(java.util.function.UnaryOperator<RexNode> rewrite) {
    List<Column> rewritten = new ArrayList<>(columns.size());
    for (Column column : columns) {
      ImmutableList.Builder<RexNode> conditions = ImmutableList.builder();
      for (RexNode condition : column.ruleConditions) {
        conditions.add(rewrite.apply(condition));
      }
      List<@Nullable RexNode> masks = new ArrayList<>(column.ruleMasks.size());
      for (RexNode mask : column.ruleMasks) {
        masks.add(mask == null ? null : rewrite.apply(mask));
      }
      List<@Nullable RexNode> placeholders = new ArrayList<>(column.rulePlaceholders.size());
      for (RexNode placeholder : column.rulePlaceholders) {
        placeholders.add(placeholder == null ? null : rewrite.apply(placeholder));
      }
      rewritten.add(
          new Column(
              column.tableColumn,
              column.entitlement,
              conditions.build(),
              Collections.unmodifiableList(masks),
              Collections.unmodifiableList(placeholders),
              column.mask == null ? null : rewrite.apply(column.mask),
              column.placeholder == null ? null : rewrite.apply(column.placeholder)));
    }
    return new DescriptorExpressions(
        rowPredicate == null ? null : rewrite.apply(rowPredicate), rewritten);
  }

  /** Every expression this descriptor holds, for a caller that has to inspect all of them. */
  public List<RexNode> expressions() {
    List<RexNode> all = new ArrayList<>();
    if (rowPredicate != null) {
      all.add(rowPredicate);
    }
    for (Column column : columns) {
      all.addAll(column.ruleConditions);
      for (RexNode mask : column.ruleMasks) {
        if (mask != null) {
          all.add(mask);
        }
      }
      for (RexNode placeholder : column.rulePlaceholders) {
        if (placeholder != null) {
          all.add(placeholder);
        }
      }
      if (column.mask != null) {
        all.add(column.mask);
      }
      if (column.placeholder != null) {
        all.add(column.placeholder);
      }
    }
    return all;
  }

  /**
   * Converts one table's descriptor through {@code converter}.
   *
   * @param relOptTable the table as the query resolved it — its qualified name is what the generated
   *     statement's {@code FROM} names, so name resolution is the query's own
   */
  /**
   * The same conditions with {@code rewrite} applied to each, keyed by the column's table ordinal
   * and the rule's index; anything it answers null for is kept.
   *
   * <p>What needs it is {@code through} (§3.13, D228): a rule condition converted over
   * {@code [child | parent…]} is restated over the joined row, where the role half it held is a
   * verdict column the parent's own side computed.
   */
  public DescriptorExpressions rewriteConditions(ConditionRewriter rewrite) {
    List<Column> rewritten = new ArrayList<>(columns.size());
    for (Column column : columns) {
      ImmutableList.Builder<RexNode> conditions = ImmutableList.builder();
      for (int i = 0; i < column.ruleConditions.size(); i++) {
        RexNode replacement = rewrite.apply(column.tableColumn(), i, column.ruleConditions.get(i));
        conditions.add(replacement == null ? column.ruleConditions.get(i) : replacement);
      }
      rewritten.add(
          new Column(
              column.tableColumn,
              column.entitlement,
              conditions.build(),
              column.ruleMasks,
              column.rulePlaceholders,
              column.mask,
              column.placeholder));
    }
    return new DescriptorExpressions(rowPredicate, rewritten);
  }

  /** One rule condition, restated. */
  @FunctionalInterface
  public interface ConditionRewriter {
    @Nullable RexNode apply(int tableColumn, int rule, RexNode condition);
  }

  /** The same expressions with a different row predicate — what a {@code through} leaf's filter is. */
  public DescriptorExpressions withRowPredicate(@Nullable RexNode predicate) {
    return new DescriptorExpressions(predicate, columns);
  }

  public static DescriptorExpressions of(
      ChalkTable table, RelOptTable relOptTable, DescriptorConverter converter) {
    return of(table, relOptTable, converter, List.of());
  }

  /**
   * @param parents the declared parents, as the generated statement's {@code FROM} names them
   *     (§3.13): with them in scope a rule condition's {@code <parent_table>.<column>} resolves, and
   *     every {@code RexInputRef} at or beyond the child's width indexes a parent's row
   */
  public static DescriptorExpressions of(
      ChalkTable table,
      RelOptTable relOptTable,
      DescriptorConverter converter,
      List<List<String>> parents) {
    TableEntitlement entitlement = table.descriptor().getEntitlement();

    // The select list, in a fixed order this method then unpicks by position: the row predicate
    // first, then per declared column three slots per rule (its condition, its own mask and its own
    // placeholder), its mask and its placeholder. A slot with no text is a typed NULL that is
    // converted and never read, which keeps the positions arithmetic rather than conditional.
    List<String> exprs = new ArrayList<>();
    exprs.add(orNull(entitlement.getRowPredicate()));

    List<ColumnEntitlement> declared = entitlement.getColumnsList();
    for (ColumnEntitlement column : declared) {
      for (DisclosureRule rule : column.getRulesList()) {
        exprs.add(orNull(rule.getWhen()));
        exprs.add(orNull(rule.getMask()));
        exprs.add(orNull(rule.getPlaceholder()));
      }
      exprs.add(orNull(column.getMask()));
      exprs.add(orNull(column.getPlaceholder()));
    }

    List<RexNode> converted =
        converter.convert(
            exprs,
            relOptTable.getQualifiedName(),
            parents,
            table.schemaName() + "." + table.tableName());

    int at = 0;
    RexNode rowPredicate = entitlement.getRowPredicate().isBlank() ? null : converted.get(at);
    at++;

    List<Column> columns = new ArrayList<>(declared.size());
    for (ColumnEntitlement column : declared) {
      ImmutableList.Builder<RexNode> conditions = ImmutableList.builder();
      List<RexNode> masks = new ArrayList<>();
      List<RexNode> placeholders = new ArrayList<>();
      for (DisclosureRule rule : column.getRulesList()) {
        conditions.add(converted.get(at++));
        masks.add(rule.getMask().isBlank() ? null : converted.get(at));
        at++;
        placeholders.add(rule.getPlaceholder().isBlank() ? null : converted.get(at));
        at++;
      }
      RexNode mask = column.getMask().isBlank() ? null : converted.get(at);
      at++;
      RexNode placeholder = column.getPlaceholder().isBlank() ? null : converted.get(at);
      at++;
      columns.add(
          new Column(
              column.getColumn(),
              column,
              conditions.build(),
              Collections.unmodifiableList(Arrays.asList(masks.toArray(new RexNode[0]))),
              Collections.unmodifiableList(Arrays.asList(placeholders.toArray(new RexNode[0]))),
              mask,
              placeholder));
    }

    return new DescriptorExpressions(rowPredicate, columns);
  }

  /** An empty slot: a typed NULL, so the position arithmetic above stays unconditional. */
  private static String orNull(String text) {
    return text == null || text.isBlank() ? "CAST(NULL AS BOOLEAN)" : "(" + text + ")";
  }
}

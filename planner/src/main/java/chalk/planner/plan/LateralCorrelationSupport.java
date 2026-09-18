package chalk.planner.plan;

import chalk.planner.UnsupportedFeatureException;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Set;
import java.util.function.Consumer;
import org.apache.calcite.sql.JoinConditionType;
import org.apache.calcite.sql.SqlCall;
import org.apache.calcite.sql.SqlIdentifier;
import org.apache.calcite.sql.SqlJoin;
import org.apache.calcite.sql.SqlKind;
import org.apache.calcite.sql.SqlNode;
import org.apache.calcite.sql.SqlNodeList;
import org.apache.calcite.sql.SqlSelect;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * The one {@code LATERAL} shape Chalk refuses rather than plans: a sub-query that constrains
 * <em>two</em> of its own tables with the <em>same</em> outer column
 * ({@code docs/design/14-windows-ii.md} §8, {@code docs/adr/0026-lateral-shared-outer-field.md}).
 *
 * <p>D67 decorrelates first, always, and Chalk runs Calcite's general (top-down, Neumann–Kemper)
 * decorrelator for it (V26, ADR 0018). That decorrelator pushes the outer key into each side of a
 * join inside the correlated body but keeps only <em>one</em> of the two equalities, so the other
 * side ranges over every row of its table rather than over the outer row's — and the sub-query
 * answers about rows belonging to somebody else. It is CALCITE-7661's shape in the class that
 * issue's fix did not reach: {@code RelDecorrelator}, the older algorithm, adds the equality for
 * every join type since 1.43, and {@code TopDownGeneralDecorrelator} still does not.
 *
 * <p>Wrong rows are the one outcome a planner may not have, so the shape is {@code UNSUPPORTED}
 * with a message that names the outer column, the two tables and the two ways out. The alternative
 * — routing this shape to the older decorrelator — was measured and rejected; the ADR says why.
 *
 * <p><b>The rule, exactly.</b> Inside a {@code LATERAL} (or {@code APPLY}) sub-query, an outer
 * column is <em>tied to</em> a table of the sub-query when both appear in the same expression: one
 * conjunct of a {@code WHERE}, {@code HAVING} or {@code ON}, or one item of a select, group or
 * order list. A column tied to two different tables is the shape. That is an over-approximation in
 * one direction only — it can name a body whose two references would have reached the same join
 * input by another route — and never in the other, which is the direction that matters.
 */
public final class LateralCorrelationSupport {
  private LateralCorrelationSupport() {}

  /** Throws for the shape above; returns silently for a statement that writes no {@code LATERAL}. */
  public static void check(SqlNode statement) {
    List<SqlCall> laterals = new ArrayList<>();
    walk(
        statement,
        node -> {
          if (node instanceof SqlCall call && call.getKind() == SqlKind.LATERAL) {
            laterals.add(call);
          }
        });

    for (SqlCall lateral : laterals) {
      if (lateral.operandCount() > 0) {
        checkBody(lateral.operand(0));
      }
    }
  }

  /** One lateral sub-query: the outer columns it ties to more than one of its own tables. */
  private static void checkBody(SqlNode body) {
    Set<String> tables = tablesOf(body);
    if (tables.size() < 2) {
      // One table cannot have an outer column pushed into two of its sides.
      return;
    }

    Map<String, Set<String>> tied = new LinkedHashMap<>();
    for (SqlNode unit : units(body)) {
      Set<String> outer = new LinkedHashSet<>();
      Set<String> inner = new LinkedHashSet<>();
      walk(
          unit,
          node -> {
            if (node instanceof SqlIdentifier identifier && identifier.names.size() >= 2) {
              String qualifier = qualifierOf(identifier);
              if (tables.contains(qualifier)) {
                inner.add(qualifier);
              } else {
                outer.add(nameOf(identifier));
              }
            }
          });

      if (inner.isEmpty()) {
        continue;
      }

      for (String column : outer) {
        tied.computeIfAbsent(column, key -> new LinkedHashSet<>()).addAll(inner);
      }
    }

    for (Map.Entry<String, Set<String>> entry : tied.entrySet()) {
      if (entry.getValue().size() >= 2) {
        throw refusal(entry.getKey(), entry.getValue());
      }
    }
  }

  private static UnsupportedFeatureException refusal(String column, Set<String> tables) {
    List<String> named = new ArrayList<>(tables);
    return new UnsupportedFeatureException(
        "a LATERAL sub-query that constrains "
            + named.get(0)
            + " and "
            + named.get(1)
            + " with the same outer column "
            + column,
        "Calcite's general decorrelator rewrites that into a join which keeps only one of the two "
            + "equalities, so one side would range over every row of its table and the sub-query "
            + "would answer about rows belonging to another outer row. Chalk refuses it rather "
            + "than answering wrongly (docs/design/14-windows-ii.md §8). Write the second "
            + "constraint through the first — "
            + named.get(1)
            + " against "
            + named.get(0)
            + " rather than against "
            + column
            + " — or lift the sub-query into the outer statement as a join.");
  }

  /** Every table name or alias bound anywhere inside the body, lower-cased. */
  private static Set<String> tablesOf(SqlNode body) {
    Set<String> tables = new LinkedHashSet<>();
    walk(
        body,
        node -> {
          if (node instanceof SqlSelect select) {
            fromTables(select.getFrom(), tables);
          }
        });
    return tables;
  }

  private static void fromTables(@Nullable SqlNode from, Set<String> tables) {
    if (from == null) {
      return;
    }

    if (from instanceof SqlJoin join) {
      fromTables(join.getLeft(), tables);
      fromTables(join.getRight(), tables);
      return;
    }

    if (from instanceof SqlCall call) {
      switch (call.getKind()) {
        case AS -> {
          if (call.operandCount() >= 2 && call.operand(1) instanceof SqlIdentifier alias) {
            tables.add(alias.getSimple().toLowerCase(Locale.ROOT));
          }
          // The aliased thing may itself be a table with no alias of its own; a derived table's
          // own FROM is reached by the walk above, which visits every SELECT in the body.
          fromTables(call.operand(0), tables);
        }
        // A nested LATERAL, TABLE(…) or TABLESAMPLE with no alias: the thing inside names itself.
        case LATERAL, COLLECTION_TABLE, TABLESAMPLE -> {
          if (call.operandCount() > 0) {
            fromTables(call.operand(0), tables);
          }
        }
        default -> {
          // A SELECT, a set operation or anything else: no name of its own here.
        }
      }
      return;
    }

    if (from instanceof SqlIdentifier identifier) {
      tables.add(
          identifier.names.get(identifier.names.size() - 1).toLowerCase(Locale.ROOT));
    }
  }

  /**
   * The expressions an outer column and a table can be tied together in: one conjunct of each
   * predicate, and one item of each list. A whole sub-query in a select list is one unit and is also
   * walked in its own right, which only widens the tie.
   */
  private static List<SqlNode> units(SqlNode body) {
    List<SqlNode> units = new ArrayList<>();
    walk(
        body,
        node -> {
          if (node instanceof SqlSelect select) {
            conjuncts(select.getWhere(), units);
            conjuncts(select.getHaving(), units);
            items(select.getSelectList(), units);
            items(select.getGroup(), units);
          } else if (node instanceof SqlJoin join
              && join.getConditionType() == JoinConditionType.ON) {
            conjuncts(join.getCondition(), units);
          }
        });
    return units;
  }

  private static void conjuncts(@Nullable SqlNode predicate, List<SqlNode> units) {
    if (predicate == null) {
      return;
    }

    if (predicate instanceof SqlCall call && call.getKind() == SqlKind.AND) {
      for (SqlNode operand : call.getOperandList()) {
        conjuncts(operand, units);
      }
      return;
    }

    units.add(predicate);
  }

  private static void items(@Nullable SqlNodeList list, List<SqlNode> units) {
    if (list == null) {
      return;
    }

    for (SqlNode item : list) {
      if (item != null) {
        units.add(item);
      }
    }
  }

  /** The alias a column reference is qualified by, lower-cased. */
  private static String qualifierOf(SqlIdentifier identifier) {
    return identifier
        .names
        .get(identifier.names.size() - 2)
        .toLowerCase(Locale.ROOT);
  }

  private static String nameOf(SqlIdentifier identifier) {
    List<String> names = identifier.names;
    return (names.get(names.size() - 2) + "." + names.get(names.size() - 1))
        .toLowerCase(Locale.ROOT);
  }

  /** Every node of the tree, parents before children. */
  private static void walk(@Nullable SqlNode node, Consumer<SqlNode> visitor) {
    if (node == null) {
      return;
    }

    visitor.accept(node);
    if (node instanceof SqlNodeList list) {
      for (SqlNode item : list) {
        walk(item, visitor);
      }
      return;
    }

    if (node instanceof SqlCall call) {
      for (SqlNode operand : call.getOperandList()) {
        walk(operand, visitor);
      }
    }
  }
}

package chalk.planner.ir;

import java.util.ArrayList;
import java.util.HashMap;
import java.util.HashSet;
import java.util.List;
import java.util.Map;
import java.util.Set;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.core.TableScan;
import org.apache.calcite.sql.SqlBasicCall;
import org.apache.calcite.sql.SqlCall;
import org.apache.calcite.sql.SqlIdentifier;
import org.apache.calcite.sql.SqlJoin;
import org.apache.calcite.sql.SqlKind;
import org.apache.calcite.sql.SqlNode;
import org.apache.calcite.sql.SqlNodeList;
import org.apache.calcite.sql.SqlOrderBy;
import org.apache.calcite.sql.SqlSelect;
import org.apache.calcite.sql.parser.SqlParserPos;
import org.apache.calcite.sql.util.SqlShuttle;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * No generated query ever says {@code SELECT *} (future work F35, built in step 26 run 3).
 *
 * <p>Calcite writes a full-width read as a star, and a star is the one thing a federated engine must
 * not send: the source answers with its <em>physical</em> columns in its own order, so a descriptor
 * written by hand or a schema that drifted after registration can swap two same-typed columns
 * without anything noticing, and a column nobody registered has its bytes shipped only to be thrown
 * away. Naming the columns makes the contract explicit in the one artefact both sides read.
 *
 * <p>One shuttle over the generated SQL tree, after conversion, replacing a bare star select list
 * with the columns of what it stands for — the registered column list of the table, in the plan's
 * order, or the names of the sub-select the star reads. A star it cannot resolve is an error rather
 * than a shrug: silently leaving one would be the failure this exists to prevent.
 *
 * <p>The same pass also <b>names the query's own output columns</b>, which is the other half of the
 * same contract. The reader maps a result set onto the plan's fields by position and checks the
 * driver's reported names against the plan's; an unaliased {@code COUNT(*)} comes back as whatever
 * the engine calls it — {@code count_star()} in DuckDB — and a plan that could not name what it
 * asked for cannot check what it got.
 */
final class SelectStar {
  private final Map<String, List<String>> columnsOfTable;
  private final Set<String> ambiguous;

  private SelectStar(Map<String, List<String>> columnsOfTable, Set<String> ambiguous) {
    this.columnsOfTable = columnsOfTable;
    this.ambiguous = ambiguous;
  }

  /**
   * {@code statement} with every bare star replaced by the columns it stands for.
   *
   * <p>The tree is rewritten in place, which is what {@link SqlShuttle} does; the returned node is
   * the same node.
   */
  static SqlNode named(SqlNode statement, RelNode pushed) {
    Map<String, List<String>> columns = new HashMap<>();
    Set<String> ambiguous = new HashSet<>();
    scans(pushed, columns, ambiguous);
    SelectStar expansion = new SelectStar(columns, ambiguous);
    SqlNode result = statement.accept(new Shuttle(expansion));
    SqlNode named = result == null ? statement : result;
    alias(named, pushed.getRowType().getFieldNames());
    return named;
  }

  /**
   * Names the outermost select's items after the plan's own fields, where they are not already.
   *
   * <p>Only the outermost: a sub-select's names are this query's business and nobody else's, and
   * renaming them would churn every recorded golden to no purpose. What has to be right is what the
   * driver reports back, which is the outermost list.
   */
  private static void alias(SqlNode statement, List<String> fields) {
    SqlNode node = statement instanceof SqlOrderBy order ? order.query : statement;
    if (!(node instanceof SqlSelect select)) {
      return;
    }
    SqlNodeList list = select.getSelectList();
    if (list == null || list.size() != fields.size()) {
      return;
    }
    List<SqlNode> named = new ArrayList<>(list.size());
    boolean changed = false;
    for (int i = 0; i < list.size(); i++) {
      SqlNode item = list.get(i);
      String field = fields.get(i);
      if (field.isEmpty() || field.equals(nameOf(item))) {
        named.add(item);
        continue;
      }
      named.add(
          org.apache.calcite.sql.fun.SqlStdOperatorTable.AS.createCall(
              SqlParserPos.ZERO, item, new SqlIdentifier(field, SqlParserPos.ZERO)));
      changed = true;
    }
    if (changed) {
      select.setSelectList(new SqlNodeList(named, SqlParserPos.ZERO));
    }
  }

  /** The name a select item already carries, or null when it carries none. */
  private static @Nullable String nameOf(SqlNode item) {
    if (item instanceof SqlBasicCall call && call.getOperator().getKind() == SqlKind.AS) {
      return aliasOf(call);
    }
    if (item instanceof SqlIdentifier identifier && !identifier.isStar()) {
      return identifier.names.get(identifier.names.size() - 1);
    }
    return null;
  }

  /**
   * Every table the subtree scans, by the name the source knows it by, mapped to the scan's own
   * output columns — which is the registered column list already pruned to what the plan reads, in
   * the plan's order. A name two scans disagree about is left unresolved rather than guessed.
   */
  private static void scans(
      RelNode rel, Map<String, List<String>> columns, Set<String> ambiguous) {
    if (rel instanceof TableScan scan) {
      List<String> qualified = scan.getTable().getQualifiedName();
      String name = qualified.get(qualified.size() - 1);
      List<String> fields = scan.getRowType().getFieldNames();
      List<String> seen = columns.putIfAbsent(name, fields);
      if (seen != null && !seen.equals(fields)) {
        ambiguous.add(name);
      }
      return;
    }
    for (RelNode input : rel.getInputs()) {
      scans(input, columns, ambiguous);
    }
  }

  /** Children first, so a star over a sub-select reads a list that has already been named. */
  private static final class Shuttle extends SqlShuttle {
    private final SelectStar expansion;

    Shuttle(SelectStar expansion) {
      this.expansion = expansion;
    }

    @Override
    public @Nullable SqlNode visit(SqlCall call) {
      SqlNode visited = super.visit(call);
      if (visited instanceof SqlSelect select) {
        expansion.name(select);
      }
      return visited;
    }
  }

  /** Replaces {@code select}'s star, if it has one, with the columns it stands for. */
  private void name(SqlSelect select) {
    SqlNodeList list = select.getSelectList();
    if (list != null && !(list.size() == 1 && isStar(list.get(0)))) {
      return;
    }
    List<SqlNode> named = new ArrayList<>();
    if (!columnsOf(select.getFrom(), /* qualify= */ false, named) || named.isEmpty()) {
      throw new IllegalStateException(
          "generated SQL would say SELECT * over a FROM this rewrite cannot name ("
              + select.getFrom()
              + "). A star sends the source's physical columns in the source's own order, which is"
              + " what F35 exists to prevent; teach chalk.planner.ir.SelectStar this shape.");
    }
    select.setSelectList(new SqlNodeList(named, SqlParserPos.ZERO));
  }

  private static boolean isStar(SqlNode node) {
    return node instanceof SqlIdentifier identifier && identifier.isStar();
  }

  /**
   * Appends the columns {@code from} exposes, in order, returning whether they are all known.
   *
   * <p>{@code qualify} is what a join needs and a single table does not: two arms may both hold a
   * column called {@code id}, and the alias is what tells them apart.
   */
  private boolean columnsOf(@Nullable SqlNode from, boolean qualify, List<SqlNode> out) {
    if (from == null) {
      return false;
    }
    if (from instanceof SqlJoin join) {
      return columnsOf(join.getLeft(), /* qualify= */ true, out)
          && columnsOf(join.getRight(), /* qualify= */ true, out);
    }
    if (from instanceof SqlBasicCall call && call.getOperator().getKind() == SqlKind.AS) {
      List<String> names = namesOf(call.operand(0));
      return append(names, qualify ? aliasOf(call) : null, out);
    }
    if (from instanceof SqlIdentifier identifier) {
      String table = identifier.names.get(identifier.names.size() - 1);
      return append(columnsOfTable(table), qualify ? table : null, out);
    }
    return append(namesOf(from), null, out);
  }

  private static @Nullable String aliasOf(SqlBasicCall as) {
    return as.operandCount() > 1 && as.operand(1) instanceof SqlIdentifier alias
        ? alias.getSimple()
        : null;
  }

  /** The column names {@code node} exposes on its own, or null when they cannot be read off it. */
  private @Nullable List<String> namesOf(SqlNode node) {
    if (node instanceof SqlOrderBy order) {
      return namesOf(order.query);
    }
    if (node instanceof SqlIdentifier identifier) {
      return columnsOfTable(identifier.names.get(identifier.names.size() - 1));
    }
    if (!(node instanceof SqlSelect select) || select.getSelectList() == null) {
      return null;
    }
    List<String> names = new ArrayList<>(select.getSelectList().size());
    for (SqlNode item : select.getSelectList()) {
      if (item instanceof SqlBasicCall call && call.getOperator().getKind() == SqlKind.AS) {
        String alias = aliasOf(call);
        if (alias == null) {
          return null;
        }
        names.add(alias);
      } else if (item instanceof SqlIdentifier identifier && !identifier.isStar()) {
        names.add(identifier.names.get(identifier.names.size() - 1));
      } else {
        return null;
      }
    }
    return names;
  }

  private @Nullable List<String> columnsOfTable(String table) {
    return ambiguous.contains(table) ? null : columnsOfTable.get(table);
  }

  private static boolean append(
      @Nullable List<String> names, @Nullable String qualifier, List<SqlNode> out) {
    if (names == null) {
      return false;
    }
    for (String name : names) {
      out.add(
          qualifier == null
              ? new SqlIdentifier(name, SqlParserPos.ZERO)
              : new SqlIdentifier(List.of(qualifier, name), SqlParserPos.ZERO));
    }
    return true;
  }
}

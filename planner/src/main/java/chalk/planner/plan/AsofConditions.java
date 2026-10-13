package chalk.planner.plan;

import chalk.planner.ErrorCode;
import chalk.planner.InvalidArgumentException;
import java.util.ArrayList;
import java.util.HashSet;
import java.util.List;
import java.util.Set;
import org.apache.calcite.sql.JoinType;
import org.apache.calcite.sql.SqlBasicCall;
import org.apache.calcite.sql.SqlCall;
import org.apache.calcite.sql.SqlIdentifier;
import org.apache.calcite.sql.SqlJoin;
import org.apache.calcite.sql.SqlKind;
import org.apache.calcite.sql.SqlLiteral;
import org.apache.calcite.sql.SqlNode;
import org.apache.calcite.sql.SqlNodeList;
import org.apache.calcite.sql.SqlSelect;
import org.apache.calcite.sql.fun.SqlStdOperatorTable;
import org.apache.calcite.sql.parser.SqlParserPos;
import org.apache.calcite.sql.util.SqlBasicVisitor;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * An {@code ASOF JOIN}'s {@code ON} with a condition on one input (D337). Calcite's validator takes
 * only equalities between the two inputs there, so {@code ON r.currency = 'EUR'} is refused, though
 * its meaning is plain: a right row that fails it can never be the match. Before validation, each
 * such conjunct moves into a filter on the input it names, as if the statement had said so:
 *
 * <pre>
 *   … ASOF JOIN fx_rates r MATCH_CONDITION t.ts &gt;= r.ts ON r.currency = 'EUR'
 *   … ASOF JOIN (SELECT * FROM fx_rates AS r WHERE r.currency = 'EUR') AS r
 *       MATCH_CONDITION t.ts &gt;= r.ts ON TRUE
 * </pre>
 *
 * <ul>
 *   <li>An equality between two columns stays in {@code ON}, as does a conjunct that names no
 *       column.
 *   <li>A conjunct whose columns are all qualified by the right input's alias filters the right
 *       input, for {@code ASOF} and {@code LEFT ASOF}.
 *   <li>One whose columns are all qualified by the left input's alias filters the left input for
 *       {@code ASOF}, where a left row that fails it has no match and is dropped either way, and when
 *       the left input is one table or subquery. For {@code LEFT ASOF} it is refused: a left row that
 *       fails it must still appear, with NULLs, which a filter cannot do.
 *   <li>A conjunct that names a column without its input's alias is refused, since which input it
 *       belongs to is not known until the statement is validated.
 * </ul>
 *
 * Anything else stays in {@code ON}, and Calcite refuses it as before. The rewrite runs after the
 * star policy has read the statement as written, so the star it writes is never held against it.
 */
final class AsofConditions {
  private AsofConditions() {}

  /**
   * {@code statement}, every ASOF join in it rewritten in place. A refusal quotes a conjunct in the
   * statement's own words, from {@code text}.
   */
  static SqlNode rewrite(SqlNode statement, StatementText text) {
    statement.accept(
        new SqlBasicVisitor<Void>() {
          @Override
          public @Nullable Void visit(SqlCall call) {
            // Children first, so a nested ASOF join is rewritten before the one around it reads it.
            super.visit(call);
            if (call instanceof SqlJoin join
                && (join.getJoinType() == JoinType.ASOF
                    || join.getJoinType() == JoinType.LEFT_ASOF)) {
              rewrite(join, text);
            }
            return null;
          }
        });
    return statement;
  }

  private static void rewrite(SqlJoin join, StatementText text) {
    SqlNode condition = join.getCondition();
    if (condition == null) {
      return;
    }

    Set<String> left = aliases(join.getLeft());
    String right = alias(join.getRight());
    boolean leftOuter = join.getJoinType() == JoinType.LEFT_ASOF;

    List<SqlNode> kept = new ArrayList<>();
    List<SqlNode> onRight = new ArrayList<>();
    List<SqlNode> onLeft = new ArrayList<>();
    for (SqlNode conjunct : conjuncts(condition)) {
      Names names = names(conjunct);
      if (names.subquery() || (names.qualifiers().isEmpty() && !names.unqualified())) {
        kept.add(conjunct);
      } else if (isColumnEquality(conjunct)) {
        kept.add(conjunct);
      } else if (names.unqualified()) {
        throw new InvalidArgumentException(
            ErrorCode.SQL_VALIDATION,
            "In an ASOF JOIN, ON takes equalities between the two inputs and conditions on one"
                + " input, and '"
                + text.quote(conjunct)
                + "' names a column without its input's alias. Qualify each column with its"
                + " input's alias, so a condition on one input can filter that input.");
      } else if (right != null && names.qualifiers().equals(Set.of(right))) {
        onRight.add(conjunct);
      } else if (left.containsAll(names.qualifiers())) {
        if (leftOuter) {
          throw new InvalidArgumentException(
              ErrorCode.SQL_VALIDATION,
              "In a LEFT ASOF JOIN, a condition on the left input cannot be in ON: a left row that"
                  + " fails '"
                  + text.quote(conjunct)
                  + "' must still appear, with NULLs. Move the condition to WHERE to drop those"
                  + " rows, or into a CASE in the select list to blank their match.");
        }
        if (alias(join.getLeft()) == null) {
          kept.add(conjunct);
        } else {
          onLeft.add(conjunct);
        }
      } else {
        kept.add(conjunct);
      }
    }

    if (onRight.isEmpty() && onLeft.isEmpty()) {
      // A row-value equality expanded into its equalities still changes the ON.
      if (kept.size() != conjunctCount(condition)) {
        join.setOperand(5, and(kept, condition.getParserPosition()));
      }
      return;
    }

    if (!onRight.isEmpty()) {
      join.setRight(filtered(join.getRight(), right, onRight));
    }
    if (!onLeft.isEmpty()) {
      join.setLeft(filtered(join.getLeft(), alias(join.getLeft()), onLeft));
    }
    join.setOperand(5, and(kept, condition.getParserPosition()));
  }

  /** How many conjuncts the condition has as written, a row equality counted once. */
  private static int conjunctCount(SqlNode condition) {
    if (condition.getKind() != SqlKind.AND) {
      return 1;
    }
    int count = 0;
    for (SqlNode operand : ((SqlCall) condition).getOperandList()) {
      count += conjunctCount(operand);
    }
    return count;
  }

  /** {@code (SELECT * FROM input WHERE conditions) AS alias}. */
  private static SqlNode filtered(SqlNode input, @Nullable String alias, List<SqlNode> conditions) {
    SqlParserPos pos = input.getParserPosition();
    SqlSelect select =
        new SqlSelect(
            pos,
            null,
            SqlNodeList.of(SqlIdentifier.star(pos)),
            input,
            and(conditions, pos),
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null);
    return SqlStdOperatorTable.AS.createCall(pos, select, new SqlIdentifier(alias, pos));
  }

  private static SqlNode and(List<SqlNode> conjuncts, SqlParserPos pos) {
    if (conjuncts.isEmpty()) {
      return SqlLiteral.createBoolean(true, pos);
    }
    SqlNode result = conjuncts.get(0);
    for (int i = 1; i < conjuncts.size(); i++) {
      result = SqlStdOperatorTable.AND.createCall(pos, result, conjuncts.get(i));
    }
    return result;
  }

  /**
   * The condition's conjuncts, a row-value equality {@code (a, b) = (c, d)} counted as the
   * equalities it stands for, {@code a = c} and {@code b = d}, which Calcite's ASOF check takes and
   * the row form it does not (D337, design 63 §3).
   */
  private static List<SqlNode> conjuncts(SqlNode condition) {
    List<SqlNode> out = new ArrayList<>();
    if (condition.getKind() == SqlKind.AND) {
      for (SqlNode operand : ((SqlCall) condition).getOperandList()) {
        out.addAll(conjuncts(operand));
      }
    } else if (condition.getKind() == SqlKind.EQUALS
        && ((SqlCall) condition).operand(0) instanceof SqlCall left
        && ((SqlCall) condition).operand(1) instanceof SqlCall right
        && left.getKind() == SqlKind.ROW
        && right.getKind() == SqlKind.ROW
        && left.operandCount() == right.operandCount()) {
      for (int i = 0; i < left.operandCount(); i++) {
        out.add(
            SqlStdOperatorTable.EQUALS.createCall(
                condition.getParserPosition(), left.operand(i), right.operand(i)));
      }
    } else {
      out.add(condition);
    }
    return out;
  }

  /** {@code a = b} between two columns: the form Calcite takes in an ASOF join's ON. */
  private static boolean isColumnEquality(SqlNode conjunct) {
    return conjunct.getKind() == SqlKind.EQUALS
        && ((SqlCall) conjunct).operand(0) instanceof SqlIdentifier
        && ((SqlCall) conjunct).operand(1) instanceof SqlIdentifier;
  }

  /**
   * The aliases a conjunct qualifies its columns by, whether it names one without, and whether it
   * holds a subquery, whose columns are its own.
   */
  private record Names(Set<String> qualifiers, boolean unqualified, boolean subquery) {}

  private static Names names(SqlNode conjunct) {
    Set<String> qualifiers = new HashSet<>();
    boolean[] unqualified = {false};
    boolean[] subquery = {false};
    conjunct.accept(
        new SqlBasicVisitor<Void>() {
          @Override
          public @Nullable Void visit(SqlIdentifier id) {
            if (id.names.size() >= 2) {
              qualifiers.add(id.names.get(id.names.size() - 2));
            } else {
              unqualified[0] = true;
            }
            return null;
          }

          @Override
          public @Nullable Void visit(SqlCall call) {
            // A subquery's columns are its own; a conjunct holding one stays where it is.
            if (call instanceof SqlSelect) {
              subquery[0] = true;
              return null;
            }
            return super.visit(call);
          }
        });
    return new Names(qualifiers, unqualified[0], subquery[0]);
  }

  /**
   * The name an input is referred to by: its alias, or a table's own last name. Null for an input
   * that is itself a join, or has no name to be referred to by.
   */
  private static @Nullable String alias(SqlNode input) {
    if (input instanceof SqlBasicCall call && call.getKind() == SqlKind.AS) {
      return ((SqlIdentifier) call.operand(1)).getSimple();
    }
    if (input instanceof SqlIdentifier table) {
      return table.names.get(table.names.size() - 1);
    }
    return null;
  }

  /** Every name the inputs of {@code input} are referred to by. */
  private static Set<String> aliases(SqlNode input) {
    Set<String> out = new HashSet<>();
    if (input instanceof SqlJoin join) {
      out.addAll(aliases(join.getLeft()));
      out.addAll(aliases(join.getRight()));
    } else {
      String alias = alias(input);
      if (alias != null) {
        out.add(alias);
      }
    }
    return out;
  }
}

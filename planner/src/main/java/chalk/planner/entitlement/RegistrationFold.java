package chalk.planner.entitlement;

import java.util.ArrayList;
import java.util.List;
import org.apache.calcite.sql.SqlBasicCall;
import org.apache.calcite.sql.SqlCall;
import org.apache.calcite.sql.SqlBasicTypeNameSpec;
import org.apache.calcite.sql.SqlDataTypeSpec;
import org.apache.calcite.sql.SqlKind;
import org.apache.calcite.sql.SqlLiteral;
import org.apache.calcite.sql.SqlNode;
import org.apache.calcite.sql.SqlNodeList;
import org.apache.calcite.sql.fun.SqlStdOperatorTable;
import org.apache.calcite.sql.parser.SqlParserPos;
import org.apache.calcite.sql.type.SqlTypeName;
import org.apache.calcite.sql.util.SqlShuttle;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * The fold for the <b>registration</b> check (docs/design/16-entitlements.md §1, F42).
 *
 * <p>A descriptor's SQL is type-checked when the catalog is registered, and at that moment there is
 * no {@code RequestContext}: the host binds one per request, and the same descriptor serves every
 * principal. So the registration validator accepts <em>any</em> {@code @ctx.<name>} and checks the
 * expression's type over the rest of it:
 *
 * <ul>
 *   <li>a name in a value position becomes {@code CAST(NULL AS ANY)}, which type-checks wherever it
 *       stands and contributes nothing to the type of the expression around it;
 *   <li>a name that {@code AND}, {@code OR} or {@code NOT} combines can only be a truth value, and
 *       becomes {@code CAST(NULL AS BOOLEAN)} — so a row predicate of role flags alone, such as
 *       {@code @ctx.global_reader OR @ctx.global}, types as the boolean it is rather than as a
 *       {@code NULL OR NULL} that Calcite reduces to a NULL of type {@code ANY};
 *   <li>a name on the right of an {@code IN} is a <em>list</em>, whose arity and column types are
 *       the host's and not the descriptor's, so the membership becomes an equality against one such
 *       placeholder per key — {@code (id, org_id) IN (@ctx.subject_pairs)} becomes
 *       {@code id = <any> AND org_id = <any>}. That types the left side, which is the half this
 *       check is about, and asks nothing of a shape nobody has yet.
 * </ul>
 *
 * <p>An expression that is <em>nothing but</em> a context reference has no rest to check and is
 * skipped by the caller, which is the one thing registration cannot say anything about.
 */
final class RegistrationFold {
  private RegistrationFold() {}

  /** The parsed statement with every scalar marker replaced as above. */
  static SqlNode fold(SqlNode parsed) {
    SqlNode folded = parsed.accept(new Shuttle());
    return folded == null ? parsed : folded;
  }

  /** Whether the expression is a lone context reference, which registration cannot type. */
  static boolean isBareContextReference(String sql) {
    String text = sql.trim();
    while (text.startsWith("(") && text.endsWith(")")) {
      text = text.substring(1, text.length() - 1).trim();
    }
    if (!text.regionMatches(true, 0, "@ctx.", 0, "@ctx.".length())) {
      return false;
    }
    for (int i = "@ctx.".length(); i < text.length(); i++) {
      char c = text.charAt(i);
      if (!Character.isLetterOrDigit(c) && c != '_') {
        return false;
      }
    }
    return text.length() > "@ctx.".length();
  }

  private static final class Shuttle extends SqlShuttle {
    private int next;

    @Override
    public @Nullable SqlNode visit(SqlCall call) {
      if (isCtxCall(call)) {
        return param(call.getParserPosition());
      }
      if (call.getKind() == SqlKind.AND
          || call.getKind() == SqlKind.OR
          || call.getKind() == SqlKind.NOT) {
        return connective(call);
      }
      if (call.getKind() == SqlKind.IN || call.getKind() == SqlKind.NOT_IN) {
        SqlNode replaced = membership((SqlBasicCall) call);
        if (replaced != null) {
          return replaced;
        }
      }
      return super.visit(call);
    }

    /**
     * {@code x IN (@ctx.list)} as an equality against a placeholder per key, and its negation
     * likewise. Null when the membership has nothing to do with the context.
     */
    private @Nullable SqlNode membership(SqlBasicCall call) {
      if (call.operandCount() != 2 || !(call.operand(1) instanceof SqlNodeList values)) {
        return null;
      }
      if (values.size() != 1 || !(values.get(0) instanceof SqlCall inner) || !isCtxCall(inner)) {
        return null;
      }

      SqlParserPos pos = call.getParserPosition();
      List<SqlNode> keys = new ArrayList<>();
      if (call.operand(0) instanceof SqlCall row && row.getKind() == SqlKind.ROW) {
        keys.addAll(row.getOperandList());
      } else {
        keys.add(call.operand(0));
      }

      SqlNode test = null;
      for (SqlNode key : keys) {
        SqlNode equality =
            SqlStdOperatorTable.EQUALS.createCall(
                pos, key.accept(this) == null ? key : key.accept(this), param(pos));
        test = test == null ? equality : SqlStdOperatorTable.AND.createCall(pos, test, equality);
      }

      // A membership over no key at all is not something the parser can produce; keep the walk
      // total rather than trusting that.
      if (test == null) {
        return SqlLiteral.createBoolean(true, pos);
      }
      return call.getKind() == SqlKind.NOT_IN
          ? SqlStdOperatorTable.NOT.createCall(pos, test)
          : test;
    }

    /**
     * One context value, as something that type-checks wherever it stands and says nothing about
     * what it is: {@code CAST(NULL AS ANY)}.
     *
     * <p>A bare {@code ?} was the obvious choice and does not work. Calcite infers a dynamic
     * parameter's type from its position only where the position has one to give — a comparison,
     * an {@code OR} — and a user function's declared operand type is not one of them, so
     * {@code FINGERPRINT(first_name, ?)} is refused with "Illegal use of dynamic parameter". That
     * is the same wall the shape-only fold meets, and it casts for the same reason; here there is
     * no declared type to cast to, so the cast is to {@code ANY}, which every operand checker
     * admits and which no expression's own type is derived from.
     */
    private SqlNode param(SqlParserPos pos) {
      return placeholder(SqlTypeName.ANY, pos);
    }

    /**
     * {@code AND}, {@code OR} or {@code NOT} with each context value it combines standing as a truth
     * value, and every other operand folded as usual.
     *
     * <p>A declaring policy writes no membership on a table's own row for a role whose combinations
     * are all answered along a path or on a parent (D320), and that row predicate is the role flags
     * alone. As {@code ANY} they convert to {@code NULL OR NULL}, which Calcite reduces to a NULL of
     * type {@code ANY}, and a sound descriptor was refused as not boolean (F164). What the host binds
     * is typed per request, as it always was.
     */
    private SqlNode connective(SqlCall call) {
      List<SqlNode> operands = new ArrayList<>(call.operandCount());
      for (SqlNode operand : call.getOperandList()) {
        if (operand instanceof SqlCall inner && isCtxCall(inner)) {
          operands.add(placeholder(SqlTypeName.BOOLEAN, inner.getParserPosition()));
        } else {
          SqlNode folded = operand == null ? null : operand.accept(this);
          operands.add(folded == null ? operand : folded);
        }
      }
      return call.getOperator().createCall(call.getParserPosition(), operands);
    }

    private SqlNode placeholder(SqlTypeName type, SqlParserPos pos) {
      next++;
      return SqlStdOperatorTable.CAST.createCall(
          pos,
          SqlLiteral.createNull(pos),
          new SqlDataTypeSpec(new SqlBasicTypeNameSpec(type, pos), pos));
    }

    /** The scalar marker, by its reserved name — the same test {@code ContextFold} makes (D223). */
    private static boolean isCtxCall(SqlCall call) {
      return call.getOperator() != null
          && chalk.planner.ReservedNames.CTX.equalsIgnoreCase(call.getOperator().getName())
          && call.operandCount() == 1;
    }
  }
}

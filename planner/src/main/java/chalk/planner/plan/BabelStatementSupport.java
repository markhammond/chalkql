package chalk.planner.plan;

import chalk.planner.UnsupportedFeatureException;
import org.apache.calcite.sql.SqlCall;
import org.apache.calcite.sql.SqlKind;
import org.apache.calcite.sql.SqlNode;
import org.apache.calcite.sql.validate.SqlConformanceEnum;

/**
 * The statements Calcite's Babel parser accepts that Chalk is not a home for — DDL, transaction
 * control, session control — refused by name rather than left to fall over somewhere downstream
 * (D259, ADR 0039 §2).
 *
 * <p>Chalk plans queries. The core parser enforces that for free: it has no production for {@code
 * CREATE TABLE}, {@code BEGIN}, {@code COMMIT}, {@code SHOW} or {@code DISCARD}, so every one of
 * them is a parse failure with a message naming the keyword, and no code downstream has ever had to
 * think about them. Babel's grammar has those productions — {@code SqlBabelCreateTable}, and the
 * {@code org.apache.calcite.sql.babel.postgres} statements — so under {@code BABEL}, and only
 * there, they reach code that assumed a query.
 *
 * <p>What that looked like before this check, measured on the pinned build: {@code CREATE TABLE t
 * (a INTEGER)} threw a raw {@code AssertionError} — <i>"Was not expecting value 'CREATE_TABLE' for
 * enumeration 'org.apache.calcite.sql.SqlKind' in this context"</i> — and {@code BEGIN} an
 * {@code UnsupportedOperationException} naming {@code SqlNodeList} and nothing else. Both become
 * {@code PLAN_ERROR_KIND_INTERNAL} with a correlation id: the sidecar telling a host that Chalk is
 * broken, when what happened is that the host asked for something Chalk does not do. Neither is a
 * wrong answer, and neither says what was unsupported.
 *
 * <p>So: after parsing, a statement whose kind is not a query is {@code UNSUPPORTED}, naming the
 * kind — the same refusal, and the same error kind, a library function without an IR mapping gets
 * ({@code FunctionMapping}).
 *
 * <p><b>Only under {@code BABEL}.</b> Every other conformance keeps the core parser, where these
 * statements never get past parsing, so the check would be unreachable — and running it anyway
 * would change what a caller sees for a statement the core parser <em>does</em> parse and the
 * validator then rejects, such as an {@code INSERT}. That is the one thing D259 may not do.
 */
public final class BabelStatementSupport {

  private BabelStatementSupport() {}

  /**
   * Refuses a non-query statement under {@code BABEL}. A no-op for every other conformance, and for
   * every query.
   */
  public static void check(SqlNode parsed, SqlConformanceEnum conformance) {
    if (conformance != SqlConformanceEnum.BABEL || parsed.getKind().belongsTo(SqlKind.QUERY)) {
      return;
    }
    throw new UnsupportedFeatureException(
        "The " + name(parsed) + " statement",
        "Chalk plans queries: a statement that changes a schema, a transaction or a session has"
            + " nowhere to run, because the sidecar owns no data and holds no session on a source."
            + " The Babel parser accepts it, which is why this is a refusal here rather than a"
            + " parse error as it is under every other conformance.");
  }

  /**
   * What to call the statement in the message. {@code CREATE_TABLE} reads better as {@code CREATE
   * TABLE}, and {@code SqlKind.OTHER} — which is what Babel's {@code BEGIN}, {@code COMMIT},
   * {@code ROLLBACK}, {@code SHOW} and {@code DISCARD} all report — reads as nothing at all, so for
   * those the operator's own name is the only thing in the tree that names the statement a host
   * wrote.
   */
  private static String name(SqlNode parsed) {
    SqlKind kind = parsed.getKind();
    if (kind == SqlKind.OTHER && parsed instanceof SqlCall call) {
      String operator = call.getOperator().getName();
      if (!operator.isBlank()) {
        return operator;
      }
    }

    return kind.name().replace('_', ' ');
  }
}

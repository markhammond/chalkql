package chalk.planner.redact;

import java.io.PrintWriter;
import java.io.StringWriter;
import java.util.ArrayList;
import java.util.List;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.core.Values;
import org.apache.calcite.rel.externalize.RelWriterImpl;
import org.apache.calcite.rex.RexBuilder;
import org.apache.calcite.rex.RexLiteral;
import org.apache.calcite.rex.RexNode;
import org.apache.calcite.rex.RexShuttle;
import org.apache.calcite.rel.rel2sql.SqlImplementor;
import org.apache.calcite.sql.SqlExplainLevel;
import org.apache.calcite.sql.dialect.CalciteSqlDialect;
import org.apache.calcite.util.Pair;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * The plan text with this statement's literals redacted (D262, {@code
 * docs/design/37-redacted-sql.md} §1). A <em>second</em> rendering, selected by the engine: the
 * corpus goldens are recorded through the unredacted one and a request that asks for no redaction
 * goes through {@code RelOptUtil.dumpPlan} exactly as it always has, byte for byte.
 *
 * <p>It redacts where the text is <em>written</em> rather than by copying the tree, and that is the
 * point: a rel that builds its own string out of expressions would print the values straight past a
 * rewritten tree, and there is no rewriting of anything the optimiser produced to get wrong.
 *
 * <p>The pseudonyms are the statement's own — the seed is the seed its redacted SQL was keyed by —
 * so a literal reads the same in the statement and in the plan, wherever the optimiser kept its type
 * and its value. Canonicalising through {@link SqlImplementor#toSql(RexLiteral)} is what makes that
 * hold: it turns a {@code RexLiteral} back into the {@code SqlNode} the parser would have made of
 * it, and the pseudonym is taken over that node's canonical unparse, exactly as the statement's is.
 *
 * <p>{@code fetch} and {@code offset} are the plan's structural positions, and they are kept under
 * {@code keep_structural} for the reason the statement's {@code LIMIT} is. There is nothing else to
 * keep: a {@code GROUP BY} ordinal is a field index by the time there is a plan, and a window's
 * frame bounds are printed inside a string the group builds for itself.
 */
public final class PlanTextRedactor {
  /**
   * A builder of its own, for one thing: the {@code CHAR} literal a marker falls back to when
   * something asks it to be visited rather than printed. That literal never reaches a plan, so it
   * does not matter that this factory is not the request's.
   */
  private static final RexBuilder LITERALS =
      new RexBuilder(
          new org.apache.calcite.sql.type.SqlTypeFactoryImpl(
              chalk.planner.types.ChalkTypeSystem.INSTANCE));

  private final Pseudonyms pseudonyms;
  private final RedactionPolicy policy;

  public PlanTextRedactor(Pseudonyms pseudonyms, RedactionPolicy policy) {
    this.pseudonyms = pseudonyms;
    this.policy = policy;
  }

  /** {@code RelOptUtil.dumpPlan} with this redaction in force. */
  public String explain(RelNode rel, SqlExplainLevel level) {
    StringWriter sw = new StringWriter();
    PrintWriter pw = new PrintWriter(sw);
    rel.explain(new Writer(pw, level));
    pw.flush();
    return sw.toString();
  }

  /** One expression with every literal replaced, for printing and for nothing else. */
  private RexNode redact(RexNode node) {
    return node.accept(
        new RexShuttle() {
          @Override
          public RexNode visitLiteral(RexLiteral literal) {
            return marker(literal);
          }
        });
  }

  private RexNode marker(RexLiteral literal) {
    String type = literal.getTypeName().name();
    if (policy.scope() == RedactionPolicy.Scope.STRINGS && !isString(literal)) {
      return literal;
    }

    String text = pseudonyms.marker(type, canonical(literal));
    return new RedactedRex(literal.getType(), text, LITERALS.makeLiteral(text));
  }

  private static boolean isString(RexLiteral literal) {
    return org.apache.calcite.sql.type.SqlTypeName.CHAR_TYPES.contains(literal.getTypeName())
        || org.apache.calcite.sql.type.SqlTypeName.BINARY_TYPES.contains(literal.getTypeName());
  }

  /**
   * The literal's canonical unparse — the same string the statement's own redaction takes its
   * pseudonym over, so the two texts agree wherever the optimiser kept the literal as it was.
   */
  private static String canonical(RexLiteral literal) {
    try {
      return SqlImplementor.toSql(literal).toSqlString(CalciteSqlDialect.DEFAULT).getSql();
    } catch (RuntimeException notExpressible) {
      // A literal Calcite has no SQL spelling for — a symbol, a sarg, a row. Its digest is a
      // canonical form too; it simply does not line up with the statement's, and a pseudonym that
      // correlates with nothing is still a pseudonym.
      return literal.toString();
    }
  }

  /** {@code RelWriterImpl}, with every value it is about to print redacted first. */
  private final class Writer extends RelWriterImpl {
    Writer(PrintWriter pw, SqlExplainLevel detailLevel) {
      super(pw, detailLevel, false);
    }

    @Override
    protected void explain_(RelNode rel, List<Pair<String, @Nullable Object>> values) {
      List<Pair<String, @Nullable Object>> redacted = new ArrayList<>(values.size());
      for (Pair<String, @Nullable Object> value : values) {
        redacted.add(Pair.of(value.left, item(rel, value.left, value.right)));
      }
      super.explain_(rel, redacted);
    }

    private @Nullable Object item(RelNode rel, String term, @Nullable Object value) {
      if (value == null || value instanceof RelNode) {
        // An input. `explain_` skips these when it prints attributes and recurses into them
        // afterwards, so handing back anything else here would lose a child.
        return value;
      }
      if (policy.keepStructural() && ("fetch".equals(term) || "offset".equals(term))) {
        return value;
      }
      if (rel instanceof Values tupled && "tuples".equals(term)) {
        // Calcite renders a Values' rows itself, into strings, before the writer ever sees them.
        // The rows are literals and nothing else, so they are rendered again here in the same
        // shape — `{ a, b }, { c, d }` — out of the tuples the rel actually holds.
        return tuples(tupled);
      }
      if (value instanceof RexNode expression) {
        return redact(expression).toString();
      }
      if (value instanceof chalk.planner.plan.IndexMatcher.Range range) {
        // An index range's bounds are the statement's own literals, rendered by the range itself.
        return range.describe(PlanTextRedactor.this::redact);
      }
      if (value instanceof List<?> list) {
        StringBuilder text = new StringBuilder("[");
        for (int i = 0; i < list.size(); i++) {
          if (i > 0) {
            text.append(", ");
          }
          text.append(item(rel, term, list.get(i)));
        }
        return text.append(']').toString();
      }
      return value;
    }

    /**
     * Calcite's own rendering of a {@code Values}' rows — one {@code { a, b }} per tuple, in a list
     * the writer's own brackets go round — over the redacted literals.
     */
    private List<String> tuples(Values rel) {
      List<String> rows = new ArrayList<>(rel.tuples.size());
      for (List<RexLiteral> tuple : rel.tuples) {
        StringBuilder text = new StringBuilder("{ ");
        for (int i = 0; i < tuple.size(); i++) {
          if (i > 0) {
            text.append(", ");
          }
          text.append(marker(tuple.get(i)));
        }
        rows.add(text.append(" }").toString());
      }
      return rows;
    }
  }
}

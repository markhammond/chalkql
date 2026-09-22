package chalk.planner.redact;

import chalk.ir.v1.Expr;
import chalk.ir.v1.Field;
import chalk.ir.v1.Literal;
import chalk.ir.v1.VirtualRow;
import chalk.planner.entitlement.BoundContext;
import chalk.planner.entitlement.ContextLiterals;
import java.util.HashSet;
import java.util.LinkedHashMap;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Map;
import java.util.Set;
import org.apache.calcite.sql.SqlLiteral;
import org.apache.calcite.sql.SqlNode;
import org.apache.calcite.sql.parser.SqlParserPos;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * The context names a redacted literal may be labelled with (D286, {@code
 * docs/design/37-redacted-sql.md} §5).
 *
 * <p>Keyed by value, never by node. A context value folded at prepare enters the parse tree as a
 * plain literal and is copied afresh at every stage after that — the conversion to Rex, simplification
 * and constant reduction, the collapse of an {@code IN} list into a {@code Sarg}, the SQL generator of
 * a pushed subtree — and a {@code RexLiteral} carries nothing but its type and its value. What
 * survives all of that is the value, and the value is what a pseudonym is already taken over: so a
 * label is looked up by the same two strings the pseudonym is, the literal's type name and its
 * canonical unparse, and it holds wherever the pseudonym does.
 *
 * <p>A label says "this literal is, in type and value, the one bound as {@code <name>}", and no more.
 * A literal the host wrote that happens to equal a bound value is labelled too, and discloses nothing
 * the identical pseudonym did not. A literal the optimiser coerced is another literal and carries
 * none. A value bound under two names carries both.
 *
 * <p>Never in the structural form and never in a pseudonym's input: a label is rendering only, so no
 * pseudonym a host has logged moves.
 */
public final class Labels {
  /** No labels at all, which is every redaction of a request that bound nothing. */
  public static final Labels NONE = new Labels(Map.of());

  /** How a name is written, the way the policy that folded it wrote it. */
  private static final String PREFIX = "@ctx.";

  private final Map<String, String> byValue;

  private Labels(Map<String, String> byValue) {
    this.byValue = byValue;
  }

  /**
   * The labels of one request: each folded scalar under its own name, each element of a folded list
   * or relation under the list's name. A shape-only name is not here — it stays a dynamic parameter
   * that carries its name already — and neither is a relation above the fold ceiling, whose rows are
   * never in a plan.
   */
  public static Labels of(BoundContext context) {
    if (context.isEmpty()) {
      return NONE;
    }
    Map<String, Set<String>> names = new LinkedHashMap<>();
    for (Map.Entry<String, Expr> scalar : context.scalars().entrySet()) {
      if (context.isShape(scalar.getKey()) || !scalar.getValue().hasLiteral()) {
        continue;
      }
      add(names, scalar.getValue().getLiteral(), scalar.getValue().getType(), scalar.getKey());
    }
    Set<String> unfolded = new HashSet<>();
    for (BoundContext.Relation relation : context.unfolded()) {
      unfolded.add(relation.name());
    }
    for (BoundContext.Relation relation : context.relations().values()) {
      if (unfolded.contains(relation.name())) {
        continue;
      }
      List<Field> fields = relation.rowType().getFieldsList();
      for (VirtualRow row : relation.rows()) {
        for (int c = 0; c < fields.size() && c < row.getValuesCount(); c++) {
          Expr value = row.getValues(c);
          if (!value.hasLiteral()) {
            continue;
          }
          add(
              names,
              value.getLiteral(),
              value.hasType() ? value.getType() : fields.get(c).getType(),
              relation.name());
        }
      }
    }
    if (names.isEmpty()) {
      return NONE;
    }
    Map<String, String> labels = new LinkedHashMap<>();
    names.forEach((key, set) -> labels.put(key, String.join(",", set)));
    return new Labels(labels);
  }

  /**
   * One bound value, spelled exactly as the fold spells it into the statement, so that its type name
   * and canonical unparse are the marker's own. A NULL is never labelled: every NULL in a plan would
   * otherwise carry the name, and a kind with no SQL literal spelling never folds into a plan at all.
   */
  private static void add(
      Map<String, Set<String>> names, Literal literal, chalk.ir.v1.Type type, String name) {
    if (literal.getValueCase() == Literal.ValueCase.IS_NULL
        || literal.getValueCase() == Literal.ValueCase.VALUE_NOT_SET) {
      return;
    }
    SqlNode node;
    try {
      node = ContextLiterals.toSqlNode(literal, type, "context '" + name + "'", SqlParserPos.ZERO);
    } catch (IllegalArgumentException unspellable) {
      return;
    }
    if (!(node instanceof SqlLiteral sqlLiteral)) {
      return;
    }
    names
        .computeIfAbsent(
            key(RedactedMarker.typeOf(sqlLiteral), RedactedMarker.canonicalOf(sqlLiteral)),
            k -> new LinkedHashSet<>())
        .add(PREFIX + name);
  }

  private static String key(String type, String canonical) {
    // The same separator the pseudonym uses between the type and the value, for the same reason.
    return type + '\0' + canonical;
  }

  /**
   * The label for one literal, or null when it is not a bound value.
   *
   * @param type the literal's SQL type name as Calcite names it, as the marker shows it
   * @param canonical the literal's own canonical unparse, as the pseudonym is taken over it
   */
  public @Nullable String of(String type, String canonical) {
    return byValue.get(key(type, canonical));
  }

  public boolean isEmpty() {
    return byValue.isEmpty();
  }
}

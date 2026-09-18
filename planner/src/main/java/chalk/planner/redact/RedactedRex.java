package chalk.planner.redact;

import org.apache.calcite.rel.type.RelDataType;
import org.apache.calcite.rex.RexBiVisitor;
import org.apache.calcite.rex.RexLiteral;
import org.apache.calcite.rex.RexNode;
import org.apache.calcite.rex.RexVisitor;
import org.apache.calcite.sql.SqlKind;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * One literal of a plan, standing in for itself while the plan text is rendered (D262).
 *
 * <p>Its whole job is to print as a marker: {@code RelOptUtil}'s writer renders a {@code RexNode} by
 * its {@code toString}, and a {@code RexCall}'s own digest is built from its operands' — so a marker
 * put where a literal was travels into every expression that contains it.
 *
 * <p>It never reaches a plan. The rendering is made over a copy of each expression, at the moment it
 * is printed, and the tree the optimiser produced is untouched — which is what makes a redacted plan
 * text a second rendering rather than a second plan.
 *
 * <p>{@link #accept} delegates to a {@code CHAR} literal of the marker text rather than to the
 * literal this replaced. Nothing in the rendering path calls it; a future caller that did would get
 * the pseudonym in quotation marks, which is untidy and safe, rather than the value, which would be
 * neither.
 */
final class RedactedRex extends RexNode {
  private final RelDataType type;
  private final String text;
  private final RexLiteral fallback;

  RedactedRex(RelDataType type, String text, RexLiteral fallback) {
    this.type = type;
    this.text = text;
    this.fallback = fallback;
    this.digest = text;
  }

  @Override
  public RelDataType getType() {
    return type;
  }

  @Override
  public SqlKind getKind() {
    return SqlKind.LITERAL;
  }

  @Override
  public String toString() {
    return text;
  }

  @Override
  public <R> R accept(RexVisitor<R> visitor) {
    return fallback.accept(visitor);
  }

  @Override
  public <R, P> R accept(RexBiVisitor<R, P> visitor, P arg) {
    return fallback.accept(visitor, arg);
  }

  @Override
  public boolean equals(@Nullable Object other) {
    return other instanceof RedactedRex redacted
        && text.equals(redacted.text)
        && type.equals(redacted.type);
  }

  @Override
  public int hashCode() {
    return text.hashCode();
  }
}

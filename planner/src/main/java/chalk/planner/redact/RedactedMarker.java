package chalk.planner.redact;

import org.apache.calcite.sql.SqlLiteral;
import org.apache.calcite.sql.SqlNode;
import org.apache.calcite.sql.SqlWriter;
import org.apache.calcite.sql.dialect.CalciteSqlDialect;
import org.apache.calcite.sql.parser.SqlParserPos;
import org.apache.calcite.util.Litmus;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * One literal, standing in for itself while a statement is unparsed twice (D262).
 *
 * <p>It is a {@link SqlLiteral} — carrying the original's own value and type name — and not a bare
 * {@link SqlNode}, and that is deliberate: several of Calcite's operators cast an operand to {@code
 * SqlLiteral} in {@code createCall} and read it while unparsing (a window's {@code ROWS} flag, a
 * join's {@code NATURAL}). A marker that is a literal cannot break any of them, and everything that
 * asks this node for its <em>value</em> gets the truth. Only {@link #unparse} differs, which is the
 * one thing a redaction is about.
 *
 * <p>Two renderings come off one tree, and {@link Mode} is what switches between them: the
 * structural form, where every marker prints its type alone, and the redacted form, where a marker
 * prints its pseudonym — or the original, when the position is one the visitor keeps or the scope
 * does not cover it.
 */
final class RedactedMarker extends SqlLiteral {
  /**
   * Which of the two renderings the markers of one redaction are printing. Shared and mutable
   * because the two forms come off the very same tree, one after the other, inside a single call;
   * nothing outside that call ever sees a marker.
   */
  static final class Mode {
    private boolean structural = true;

    void redacted() {
      structural = false;
    }

    boolean isStructural() {
      return structural;
    }
  }

  private final SqlLiteral original;
  private final Mode mode;
  private final String type;
  private final String canonical;

  /** False when the scope leaves this kind of literal alone, or the visitor keeps this position. */
  private final boolean replaced;

  private @Nullable String marker;

  RedactedMarker(SqlLiteral original, Mode mode, boolean replaced) {
    super(original.getValue(), original.getTypeName(), original.getParserPosition());
    this.original = original;
    this.mode = mode;
    this.replaced = replaced;
    this.type = typeOf(original);
    this.canonical = original.toSqlString(CalciteSqlDialect.DEFAULT).getSql();
  }

  /**
   * The literal's SQL type name as Calcite names it: the enum constant — {@code CHAR}, {@code
   * DECIMAL}, {@code DOUBLE}, {@code BOOLEAN}, {@code NULL}, {@code BINARY}, {@code INTERVAL_DAY} —
   * and not {@code SqlTypeName.getName()}, which is a different thing.
   *
   * <p>{@code DATE '…'}, {@code TIME '…'} and {@code TIMESTAMP '…'} are the exception this build has
   * to know about: since Calcite 1.43 the parser leaves a {@code <typename> 'string'} literal as a
   * {@link org.apache.calcite.sql.SqlUnknownLiteral} whose type name is literally {@code UNKNOWN}
   * until the validator resolves it, and the tag it carries is the name a reader wants to see.
   */
  private static String typeOf(SqlLiteral literal) {
    return literal instanceof org.apache.calcite.sql.SqlUnknownLiteral unknown
        ? unknown.tag
        : literal.getTypeName().name();
  }

  /** The literal's SQL type name as Calcite names it, which is what the marker shows. */
  String type() {
    return type;
  }

  /** The literal's own canonical unparse, which is what the pseudonym is taken over. */
  String canonical() {
    return canonical;
  }

  /** True when this position's value is replaced rather than shown. */
  boolean isReplaced() {
    return replaced;
  }

  /** Fixes the pseudonym this marker prints once the seed is known. */
  void resolve(Pseudonyms pseudonyms) {
    marker = pseudonyms.marker(type, canonical);
  }

  @Override
  public void unparse(SqlWriter writer, int leftPrec, int rightPrec) {
    if (mode.isStructural()) {
      // Every literal, whatever the scope says and whatever position it is in: the structure of a
      // statement is the statement with no values in it at all, and that is what the seed is taken
      // over. What a rendering keeps is a rendering's business.
      writer.literal(Pseudonyms.structuralMarker(type));
      return;
    }
    if (!replaced) {
      original.unparse(writer, leftPrec, rightPrec);
      return;
    }
    writer.literal(marker == null ? Pseudonyms.structuralMarker(type) : marker);
  }

  @Override
  public SqlLiteral clone(SqlParserPos pos) {
    return new RedactedMarker(original.clone(pos), mode, replaced);
  }

  @Override
  public boolean equalsDeep(@Nullable SqlNode node, Litmus litmus) {
    if (node == this) {
      return litmus.succeed();
    }
    if (!(node instanceof RedactedMarker other)) {
      return litmus.fail("{} != {}", this, node);
    }
    return original.equalsDeep(other.original, litmus);
  }
}

package chalk.planner.plan;

import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import java.util.ArrayList;
import java.util.List;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.RelWriter;
import org.apache.calcite.sql.SqlExplainLevel;
import org.apache.calcite.util.Pair;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * The logical digest (D342, {@code docs/design/67-plans-and-statistics.md} §4): an identity of a
 * statement that statistics do not move.
 *
 * <p>It is taken over the tree as the converter and the entitlement rewrite left it, before the
 * first rule. Two of the rewrites after that read statistics: the strategy for a {@code DISTINCT}
 * aggregate (D54) and the heuristic join order of a statement with many joins (D44). So the plan
 * text's own logical section can move with statistics, and this digest cannot.
 *
 * <p>The same construction as {@code chalk.ir.PlanDigest}, over a different text:
 *
 * <pre>
 * text           = every node, depth first: its name, each term the explain writes at
 *                  EXPPLAN_ATTRIBUTES level, and its full row type, each written with its length
 * logical_digest = little-endian uint64 of the first 8 bytes of SHA-256(UTF-8(text))
 * </pre>
 *
 * <p>The row types are there because the terms do not say what a table's columns are: a column
 * whose type changes changes the statement's result, and its identity with it.
 */
public final class LogicalDigest {
  private LogicalDigest() {}

  /** The digest of {@code rel} and everything under it. */
  public static long of(RelNode rel) {
    byte[] bytes = text(rel).getBytes(StandardCharsets.UTF_8);
    try {
      byte[] hash = MessageDigest.getInstance("SHA-256").digest(bytes);
      return ByteBuffer.wrap(hash, 0, 8).order(ByteOrder.LITTLE_ENDIAN).getLong();
    } catch (NoSuchAlgorithmException e) {
      throw new IllegalStateException("SHA-256 is required by every JRE", e);
    }
  }

  /** The text the digest is taken over; for a test that wants to see what moved. */
  public static String text(RelNode rel) {
    StringBuilder text = new StringBuilder();
    write(rel, text);
    return text.toString();
  }

  private static void write(RelNode rel, StringBuilder text) {
    Terms terms = new Terms();
    rel.explain(terms);

    text.append('(');
    field(rel.getRelTypeName(), text);
    for (Pair<String, @Nullable Object> term : terms.values) {
      // The inputs are written below, in order, as the nodes they are.
      if (term.right instanceof RelNode) {
        continue;
      }
      field(term.left, text);
      field(value(term.right), text);
    }
    field(rel.getRowType().getFullTypeString(), text);
    for (RelNode input : rel.getInputs()) {
      write(input, text);
    }
    text.append(')');
  }

  /** One string, its length first, so that no value can read as the structure around it. */
  private static void field(String value, StringBuilder text) {
    text.append(value.length()).append(':').append(value);
  }

  private static String value(@Nullable Object value) {
    if (value == null) {
      return "null";
    }
    if (value.getClass().isArray()) {
      // An array's own string is its identity, which differs on every run.
      int length = java.lang.reflect.Array.getLength(value);
      List<String> elements = new ArrayList<>(length);
      for (int i = 0; i < length; i++) {
        elements.add(value(java.lang.reflect.Array.get(value, i)));
      }
      return elements.toString();
    }
    return value.toString();
  }

  /** What a node's explain says of it, at the level of the plan text's logical section. */
  private static final class Terms implements RelWriter {
    private final List<Pair<String, @Nullable Object>> values = new ArrayList<>();

    @Override
    public void explain(RelNode rel, List<Pair<String, @Nullable Object>> valueList) {
      values.addAll(valueList);
    }

    @Override
    public SqlExplainLevel getDetailLevel() {
      return SqlExplainLevel.EXPPLAN_ATTRIBUTES;
    }

    @Override
    public RelWriter item(String term, @Nullable Object value) {
      values.add(Pair.of(term, value));
      return this;
    }

    @Override
    public RelWriter done(RelNode node) {
      return this;
    }
  }
}

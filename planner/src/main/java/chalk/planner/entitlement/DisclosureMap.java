package chalk.planner.entitlement;

import com.google.common.collect.ImmutableList;
import java.util.List;
import java.util.Map;

/**
 * What one leaf occurrence discloses, column by column (docs/design/16-entitlements.md §3.1).
 *
 * <p>One per <em>occurrence</em>, not per table: the same table read twice in one statement can be
 * read under two different statements of the same policy — the fold simplifies each leaf's rules
 * under that leaf's own conjuncts (§3.3) — so the map belongs to the scan and travels with it.
 *
 * <p>It is indexed by the <em>table's</em> column ordinal and not by the scan's projected position,
 * because that is the numbering the host's catalog uses, the numbering the {@code Read}'s
 * breadcrumbs are written in (§3.10) and the only one that survives column pruning.
 */
public final class DisclosureMap {
  private final String schema;
  private final String table;
  private final String descriptorHash;
  private final ImmutableList<Disclosed> columns;
  private final Visibility visibility;
  private final ImmutableList<Integer> statistical;
  private String contradiction = "";
  private ImmutableList<Tested> tested = ImmutableList.of();
  private Map<Integer, ImmutableList<String>> shapes = Map.of();

  /**
   * One column this statement tested without reading, and by which shapes (D261,
   * docs/design/36-test-verdict.md §3).
   *
   * <p>The shapes are the ones the <em>statement</em> used, not the ones the policy permits: what an
   * audit log records is what was asked. Enumeration by repeated probes is inherent to any
   * comparison oracle and is not defended here; it is defended by the host, where identity and
   * quotas live, and this is what the host is handed to defend it with.
   */
  public record Tested(String column, ImmutableList<String> shapes) {}

  /** How much of the table the folded row predicate leaves visible (§3.12, D207). Never a count. */
  public enum Visibility {
    /** The predicate folded to FALSE: the principal holds no grant on the table. */
    NONE,
    /** Neither constant: some rows, decided per row. */
    SOME,
    /** The predicate folded to TRUE, or there is none. */
    ALL
  }

  public DisclosureMap(
      String schema,
      String table,
      String descriptorHash,
      List<Disclosed> columns,
      Visibility visibility) {
    this(schema, table, descriptorHash, columns, visibility, List.of());
  }

  public DisclosureMap(
      String schema,
      String table,
      String descriptorHash,
      List<Disclosed> columns,
      Visibility visibility,
      java.util.Collection<Integer> statistical) {
    this.schema = schema;
    this.table = table;
    this.descriptorHash = descriptorHash;
    this.columns = ImmutableList.copyOf(columns);
    this.visibility = visibility;
    this.statistical = ImmutableList.copyOf(statistical);
  }

  /**
   * The table ordinals this leaf emits <b>raw under the statistical opt-in</b> (D203): read as
   * {@code AGGREGATE} by everything that judges a value, and additionally permitted in a predicate,
   * a join condition, an aggregate's FILTER and a grouping key, under the group-size guard.
   */
  public ImmutableList<Integer> statisticalColumns() {
    return statistical;
  }

  /** Whether this table ordinal left the leaf raw by the statistical opt-in. */
  public boolean isStatistical(int tableColumn) {
    return statistical.contains(tableColumn);
  }

  /**
   * The column on which the statement's own predicate and this principal's scope are disjoint, or
   * empty when they are not (§3.12, D207).
   *
   * <p>{@code WHERE org_id = 3} against a scope of {1, 2} returns nothing, and "nothing" reads the
   * same as an empty table. Saying which column made it so is an acknowledgement that depends on the
   * principal's own context and the statement and on no hidden row, which is what D207 permits.
   */
  public String contradiction() {
    return contradiction;
  }

  /** The columns this leaf computed a permitted comparison of, and by which shapes (D261). */
  public ImmutableList<Tested> tested() {
    return tested;
  }

  DisclosureMap testing(List<Tested> columns) {
    this.tested = ImmutableList.copyOf(columns);
    return this;
  }

  /**
   * The comparison shapes each column's rules permit over it, by table ordinal (D261). Written on
   * every entitled read, where the client's own invariant walk reads them: what it has to be able to
   * tell apart is the leaf's permitted comparison from every other expression over a raw value, and
   * it must be able to do that without believing the planner (§3.10).
   */
  DisclosureMap permitting(Map<Integer, ImmutableList<String>> permitted) {
    this.shapes = Map.copyOf(permitted);
    return this;
  }

  DisclosureMap contradicting(String column) {
    this.contradiction = column;
    return this;
  }

  public String schema() {
    return schema;
  }

  public String table() {
    return table;
  }

  public String qualifiedName() {
    return schema + "." + table;
  }

  public String descriptorHash() {
    return descriptorHash;
  }

  public Visibility visibility() {
    return visibility;
  }

  public int columnCount() {
    return columns.size();
  }

  public Disclosed of(int tableColumn) {
    return tableColumn >= 0 && tableColumn < columns.size() ? columns.get(tableColumn) : Disclosed.FULL;
  }

  public List<Disclosed> columns() {
    return columns;
  }

  /**
   * A short stable name for <em>what this leaf occurrence discloses</em>: the descriptor it was
   * compiled under, the folded verdict of every column, the statistical opt-ins and the row
   * visibility, as one hexadecimal word.
   *
   * <p>It exists so that a handle can put the map where a {@code TableScan}'s digest can see it
   * without writing the whole map into a qualified name (F95,
   * {@link EntitledRelOptTable#getQualifiedName}). Two occurrences that disclose the same thing share
   * it, which is what makes them the same expression — and they are.
   */
  public String fingerprint() {
    StringBuilder text = new StringBuilder(descriptorHash).append('|').append(visibility);
    for (Disclosed column : columns) {
      text.append('|').append(column);
    }
    for (int statisticalColumn : statistical) {
      text.append('|').append('s').append(statisticalColumn);
    }
    java.util.zip.CRC32 crc = new java.util.zip.CRC32();
    crc.update(text.toString().getBytes(java.nio.charset.StandardCharsets.UTF_8));
    return Long.toHexString(crc.getValue());
  }

  /**
   * The read's per-column outcomes (§3.10): one entry for every column of the table, in ordinal
   * order, {@code FULL} included. The host holds the planner's verdict per column of this
   * occurrence, and the two derived sets below — redacted and tainted — are read off it.
   */
  public ImmutableList<chalk.ir.v1.ColumnDisclosure> disclosures() {
    ImmutableList.Builder<chalk.ir.v1.ColumnDisclosure> out = ImmutableList.builder();
    for (int i = 0; i < columns.size(); i++) {
      out.add(
          chalk.ir.v1.ColumnDisclosure.newBuilder()
              .setColumn(i)
              .setOutcome(columns.get(i).wire())
              .setStatistical(statistical.contains(i))
              .addAllTestShapes(shapes.getOrDefault(i, ImmutableList.of()))
              .build());
    }
    return out.build();
  }

  /**
   * The table ordinals the leaf emits as something other than themselves — {@code MASKED},
   * {@code REDACTED} or {@code PER_ROW} — which is the gate's question (§3.8). A population-only
   * column is raw at the leaf and is not one of them; {@link #taintedColumns} is where it appears.
   */
  public ImmutableList<Integer> withheldColumns() {
    ImmutableList.Builder<Integer> redacted = ImmutableList.builder();
    for (int i = 0; i < columns.size(); i++) {
      if (columns.get(i).isRedactedFromPushdown()) {
        redacted.add(i);
      }
    }
    return redacted.build();
  }

  /** The table ordinals the leaf emits raw and tainted on purpose (§3.4). */
  public ImmutableList<Integer> taintedColumns() {
    ImmutableList.Builder<Integer> tainted = ImmutableList.builder();
    for (int i = 0; i < columns.size(); i++) {
      if (columns.get(i) == Disclosed.AGGREGATE) {
        tainted.add(i);
      }
    }
    return tainted.build();
  }
}

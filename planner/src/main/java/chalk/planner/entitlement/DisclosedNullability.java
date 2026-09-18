package chalk.planner.entitlement;

import chalk.planner.rpc.v1.PlaceholderPolicy;
import com.google.common.collect.ImmutableMap;
import java.util.LinkedHashMap;
import java.util.Map;
import org.apache.calcite.util.ImmutableBitSet;

/**
 * Which columns of an entitled table are <b>nullable in the row type a statement is written over</b>,
 * whatever the catalog declares of the stored value (docs/design/16-entitlements.md §1, §3.5, §3.11;
 * D161, D162, D224; F58).
 *
 * <p>§1 already states the rule — "an entitled column's <em>output</em> type is nullable whenever any
 * of its rules can yield a NULL mask or a placeholder: the row shape is the same for every principal,
 * and that shape is nullable" — and V62 records the consequence at the leaf. What was missing is that
 * the <em>validator</em> and the <em>converter</em> saw the stored column's declaration instead, so a
 * statement's own expressions over a column that is always withheld were simplified under a NOT NULL
 * that the value they would meet does not have: {@code IS NOT NULL} folded to TRUE at conversion,
 * {@code COALESCE} collapsed to its first operand, and {@code CHAR_LENGTH} was typed NOT NULL and
 * reduced to zero over the stand-in (F58).
 *
 * <p>The answer is computed <b>per catalog, not per principal</b> (D161): a column is nullable here
 * when <em>any</em> rule of it can withhold or mask it to NULL, never when this principal's folded
 * rules do — otherwise two principals would be handed two row shapes and the shape itself would say
 * what the policy had decided.
 *
 * <p>Two answers are kept, one per {@link PlaceholderPolicy}, because the stand-in a withheld value
 * takes is the request's choice (D162): under {@code PlaceholdersAsNull} it is a typed NULL and the
 * column widens; under {@code PlaceholdersAsEmpty} it is the type's empty value where Calcite defines
 * one (F47), which <em>has</em> a value, so the declared nullability stays. A rule's own placeholder
 * or the column's (D224) wins over both, and widens only if it is itself nullable.
 *
 * <p>Where the answer cannot be known — a mask or a placeholder that is nothing but a context
 * reference, whose type the host binds per request and registration never sees — the column is taken
 * as nullable. That is the conservative direction: a row type wider than the values need is a column
 * the consumer must null-check, and a narrower one is a lie a NULL later walks through.
 */
public final class DisclosedNullability {

  /** What a catalog with no entitlement has: every column exactly as the catalog declares it. */
  public static final DisclosedNullability NONE =
      new DisclosedNullability(ImmutableMap.of(), ImmutableMap.of());

  private final ImmutableMap<String, ImmutableBitSet> asNull;
  private final ImmutableMap<String, ImmutableBitSet> asEmpty;

  private DisclosedNullability(
      ImmutableMap<String, ImmutableBitSet> asNull,
      ImmutableMap<String, ImmutableBitSet> asEmpty) {
    this.asNull = asNull;
    this.asEmpty = asEmpty;
  }

  /** Whether any table in this catalog widens any column at all, under either policy. */
  public boolean isEmpty() {
    return asNull.isEmpty() && asEmpty.isEmpty();
  }

  /** The columns {@code schema.table} widens under {@code policy}; empty when it widens none. */
  public ImmutableBitSet widened(PlaceholderPolicy policy, String schema, String table) {
    return byPolicy(policy).getOrDefault(key(schema, table), ImmutableBitSet.of());
  }

  /** Every table of this catalog that widens a column under {@code policy}, by qualified name. */
  public Map<String, ImmutableBitSet> byPolicy(PlaceholderPolicy policy) {
    return policy == PlaceholderPolicy.PLACEHOLDER_POLICY_AS_EMPTY ? asEmpty : asNull;
  }

  /** The columns of {@code schema} that widen under {@code policy}, keyed by table name. */
  public Map<String, ImmutableBitSet> ofSchema(PlaceholderPolicy policy, String schema) {
    Map<String, ImmutableBitSet> tables = new LinkedHashMap<>();
    String prefix = schema + ".";
    for (Map.Entry<String, ImmutableBitSet> entry : byPolicy(policy).entrySet()) {
      if (entry.getKey().startsWith(prefix)) {
        tables.put(entry.getKey().substring(prefix.length()), entry.getValue());
      }
    }
    return tables;
  }

  private static String key(String schema, String table) {
    return schema + "." + table;
  }

  /** Collects one table at a time as the registration check walks the catalog. */
  public static final class Builder {
    private final Map<String, ImmutableBitSet> asNull = new LinkedHashMap<>();
    private final Map<String, ImmutableBitSet> asEmpty = new LinkedHashMap<>();

    /** Records one entitled table's widened columns. Empty sets are dropped rather than stored. */
    public void add(String schema, String table, ImmutableBitSet underNull, ImmutableBitSet underEmpty) {
      if (!underNull.isEmpty()) {
        asNull.put(key(schema, table), underNull);
      }
      if (!underEmpty.isEmpty()) {
        asEmpty.put(key(schema, table), underEmpty);
      }
    }

    public DisclosedNullability build() {
      return asNull.isEmpty() && asEmpty.isEmpty()
          ? NONE
          : new DisclosedNullability(ImmutableMap.copyOf(asNull), ImmutableMap.copyOf(asEmpty));
    }
  }
}

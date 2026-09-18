package chalk.planner.entitlement;

import chalk.planner.rpc.v1.StarPolicy;
import java.util.ArrayList;
import java.util.List;
import org.apache.calcite.sql.SqlBasicCall;
import org.apache.calcite.sql.SqlCall;
import org.apache.calcite.sql.SqlIdentifier;
import org.apache.calcite.sql.SqlKind;
import org.apache.calcite.sql.SqlNode;
import org.apache.calcite.sql.SqlNodeList;
import org.apache.calcite.sql.SqlOrderBy;
import org.apache.calcite.sql.SqlSelect;
import org.apache.calcite.sql.SqlWith;

/**
 * Which output columns a star put there, and the {@code StarPolicy} check
 * (docs/design/16-entitlements.md §3.11, D160).
 *
 * <p>Both are read off the <em>outermost parsed</em> {@code SELECT}, before the validator expands
 * anything: after expansion there is no star left to see. What survives is one bit per output
 * column — did the statement name it — and that is what keeps {@code Omit} from making a named
 * column vanish silently.
 *
 * <p>{@code StarPolicy} itself is review discipline and not a safety mechanism: a star is expanded
 * and resolved before the rewrite either way, so each column is entitled individually.
 * {@code default_disclosure = NONE} is the setting that closes the schema-evolution gap.
 */
public final class StarProvenance {
  /** Nothing was a star, which is every statement that names its columns. */
  public static final StarProvenance NONE = new StarProvenance(List.of(), false, false);

  /**
   * Whether a star's expansion could not be read and every column therefore reads as named.
   *
   * <p>{@code Omit} then drops nothing, which is the conservative direction — and the report says
   * the request degraded rather than leaving a caller to notice that a column it expected to be
   * gone is still there.
   */
  private boolean unreadable;

  private final List<Boolean> namedByPosition;
  private final boolean anyStar;

  /** Whether the count this was read with is the parsed one and still has to be widened. */
  private final boolean parsedWidth;

  /** One entry per <em>parsed</em> select item, in order, when the width is still the parsed one. */
  private final List<Item> items;

  /**
   * One parsed select item: whether the statement named it, and — where it is a star — the alias it
   * is qualified by, or null for a bare {@code *}.
   */
  private record Item(boolean named, String alias) {}

  private StarProvenance(List<Boolean> namedByPosition, boolean anyStar, boolean parsedWidth) {
    this(namedByPosition, anyStar, parsedWidth, List.of());
  }

  private StarProvenance(
      List<Boolean> namedByPosition, boolean anyStar, boolean parsedWidth, List<Item> items) {
    this.namedByPosition = namedByPosition;
    this.anyStar = anyStar;
    this.parsedWidth = parsedWidth;
    this.items = items;
  }

  /**
   * The same provenance, read against the validator's own expansion.
   *
   * <p>The provenance has to be read <em>before</em> validation, because the validator expands a
   * select list in place and leaves no star behind; what each star expanded <em>to</em> is only
   * visible afterwards, in the list it expanded into. So this is the second half of that reading,
   * and it reads the expansion rather than inferring it: each {@code t.*} owns the validated items
   * qualified by {@code t} that the statement did not name itself, and a bare {@code *} beside them
   * owns what is left.
   *
   * <p>Two shapes still degrade to "all named", which is the conservative direction — {@code Omit}
   * declines to drop a column rather than dropping one it should not. <b>Two bare stars</b> in one
   * list leave two unknowns and one equation. And the <b>coalesced columns of a {@code NATURAL
   * JOIN} or a {@code JOIN … USING}</b> expand to expressions rather than to qualified identifiers,
   * so no star owns them; a star position with no qualifier is that, and it degrades the reading.
   */
  public StarProvenance widened(SqlNode validated, int validatedColumnCount) {
    if (!parsedWidth || !anyStar) {
      return this;
    }

    SqlNodeList expanded = selectListOf(validated);
    if (expanded == null || expanded.size() != validatedColumnCount) {
      return new StarProvenance(allNamed(validatedColumnCount), true, false).unreadable();
    }

    List<String> qualifier = new ArrayList<>(expanded.size());
    for (SqlNode item : expanded) {
      qualifier.add(qualifierOf(item));
    }

    // A qualified star owns every expanded item carrying its alias that the statement did not name
    // itself, wherever they stand: it is a count, not a scan, so the order of the list is irrelevant
    // to it and only the positions below depend on order.
    int bare = 0;
    int accounted = 0;
    List<Integer> widths = new ArrayList<>(items.size());
    for (Item item : items) {
      if (item.named()) {
        widths.add(1);
        accounted++;
        continue;
      }
      if (item.alias() == null) {
        bare++;
        widths.add(-1);
        continue;
      }
      int width = 0;
      for (String carried : qualifier) {
        if (item.alias().equalsIgnoreCase(carried)) {
          width++;
        }
      }
      for (Item other : items) {
        if (other.named() && item.alias().equalsIgnoreCase(other.alias())) {
          width--;
        }
      }
      if (width < 0) {
        return new StarProvenance(allNamed(validatedColumnCount), true, false).unreadable();
      }
      widths.add(width);
      accounted += width;
    }

    if (bare > 1) {
      return new StarProvenance(allNamed(validatedColumnCount), true, false).unreadable();
    }
    if (bare == 1) {
      int rest = validatedColumnCount - accounted;
      if (rest < 0) {
        return new StarProvenance(allNamed(validatedColumnCount), true, false).unreadable();
      }
      for (int i = 0; i < widths.size(); i++) {
        if (widths.get(i) == -1) {
          widths.set(i, rest);
        }
      }
      accounted += rest;
    }

    if (accounted != validatedColumnCount) {
      return new StarProvenance(allNamed(validatedColumnCount), true, false).unreadable();
    }

    List<Boolean> named = new ArrayList<>(validatedColumnCount);
    for (int i = 0; i < items.size(); i++) {
      for (int c = 0; c < widths.get(i); c++) {
        named.add(items.get(i).named());
      }
    }

    // A star position the validator did not fill with a qualified identifier is a column no star
    // owns — the coalesced column of a NATURAL JOIN or a USING clause is the shape that produces
    // one — and the reading degrades rather than guessing which star it came from.
    for (int i = 0; i < named.size(); i++) {
      if (!named.get(i) && qualifier.get(i) == null) {
        return new StarProvenance(allNamed(validatedColumnCount), true, false).unreadable();
      }
    }

    return new StarProvenance(named, true, false);
  }

  /** Whether a star stood in the list and its expansion could not be read (§3.11). */
  public boolean expansionUnreadable() {
    return unreadable;
  }

  private StarProvenance unreadable() {
    unreadable = true;
    return this;
  }

  /** The alias an expanded select item is qualified by, or null when it carries none. */
  private static String qualifierOf(SqlNode item) {
    if (item instanceof SqlIdentifier identifier) {
      return identifier.names.size() >= 2
          ? identifier.names.get(identifier.names.size() - 2)
          : null;
    }
    if (item instanceof SqlBasicCall call && call.getKind() == SqlKind.AS) {
      return qualifierOf(call.operand(0));
    }
    return null;
  }

  private static List<Boolean> allNamed(int count) {
    List<Boolean> named = new ArrayList<>(count);
    for (int i = 0; i < count; i++) {
      named.add(true);
    }
    return named;
  }

  public boolean anyStar() {
    return anyStar;
  }

  /**
   * Whether the statement named output column {@code index} itself.
   *
   * <p>Unknown provenance answers "named", which is the conservative direction: {@code Omit} never
   * drops a column it is not sure a star produced.
   */
  public boolean isNamed(int index) {
    return index < 0 || index >= namedByPosition.size() || namedByPosition.get(index);
  }

  /**
   * Reads the provenance of the outermost parsed {@code SELECT}'s items: one entry per item, true
   * where the statement named the column itself. {@link #widened} then maps it onto the validated
   * column count.
   */
  public static StarProvenance of(SqlNode parsed) {
    SqlSelect select = outermostSelect(parsed);
    if (select == null || select.getSelectList() == null) {
      return NONE;
    }

    List<SqlNode> parsedItems = select.getSelectList();
    boolean anyStar = false;
    List<Boolean> named = new ArrayList<>(parsedItems.size());
    List<Item> read = new ArrayList<>(parsedItems.size());
    for (SqlNode item : parsedItems) {
      boolean star = isStar(item);
      anyStar |= star;
      named.add(!star);
      read.add(new Item(!star, qualifierOf(item)));
    }
    return anyStar ? new StarProvenance(named, true, true, read) : NONE;
  }

  /**
   * The {@code StarPolicy} check, on the parse tree against the FROM scope and before validation
   * (D160). {@code RefuseWhenEntitled} refuses any star at all while the catalog carries
   * entitlements; {@code RefuseOverEntitled} refuses one whose expansion could touch an entitled
   * table, which on the parse tree means a star over a FROM that names one.
   */
  public static void check(SqlNode parsed, StarPolicy policy, EntitledNames entitled) {
    if (policy == StarPolicy.STAR_POLICY_ALLOW || policy == StarPolicy.STAR_POLICY_UNSPECIFIED) {
      return;
    }
    Refuser refuser = new Refuser(policy, entitled);
    refuser.walk(parsed);
  }

  /** Which table names the catalog entitles, asked by simple name and by qualified name. */
  public interface EntitledNames {
    /** The entitled table this identifier names, or null. */
    String entitledTable(SqlIdentifier identifier);

    /** Any entitled table at all, for {@code RefuseWhenEntitled}. */
    boolean any();
  }

  private static final class Refuser {
    private final StarPolicy policy;
    private final EntitledNames entitled;

    Refuser(StarPolicy policy, EntitledNames entitled) {
      this.policy = policy;
      this.entitled = entitled;
    }

    void walk(SqlNode node) {
      if (node instanceof SqlOrderBy orderBy) {
        walk(orderBy.query);
        return;
      }
      if (node instanceof SqlWith with) {
        walk(with.body);
        for (SqlNode item : with.withList) {
          if (item instanceof org.apache.calcite.sql.SqlWithItem withItem) {
            walk(withItem.query);
          }
        }
        return;
      }
      if (node instanceof SqlSelect select) {
        boolean star = false;
        if (select.getSelectList() != null) {
          for (SqlNode item : select.getSelectList()) {
            star |= isStar(item);
            if (item instanceof SqlCall call && call.getKind() == SqlKind.ROW) {
              refuseRow(select);
            }
          }
        }
        if (star) {
          refuse(select);
        }
        if (select.getFrom() != null) {
          walkFrom(select.getFrom());
        }
        return;
      }
      if (node instanceof SqlCall call) {
        for (SqlNode operand : call.getOperandList()) {
          if (operand != null) {
            walk(operand);
          }
        }
      }
    }

    private void walkFrom(SqlNode from) {
      if (from instanceof SqlSelect || from instanceof SqlWith || from instanceof SqlOrderBy) {
        walk(from);
        return;
      }
      if (from instanceof SqlCall call) {
        for (SqlNode operand : call.getOperandList()) {
          if (operand != null) {
            walkFrom(operand);
          }
        }
      }
    }

    private void refuse(SqlSelect select) {
      if (policy == StarPolicy.STAR_POLICY_REFUSE_WHEN_ENTITLED) {
        if (entitled.any()) {
          throw new PolicyException(
              "this statement uses SELECT *, and StarPolicy.RefuseWhenEntitled refuses any star "
                  + "while the catalog carries entitlements. Name the columns "
                  + "(docs/design/16-entitlements.md §3.11).");
        }
        return;
      }
      String table = entitledIn(select.getFrom());
      if (table != null) {
        throw new PolicyException(
            "this statement uses SELECT * over "
                + table
                + ", which carries an entitlement, and StarPolicy.RefuseOverEntitled refuses that. "
                + "Name the columns (docs/design/16-entitlements.md §3.11).");
      }
    }

    private void refuseRow(SqlSelect select) {
      String table = entitledIn(select.getFrom());
      if (table != null) {
        throw new PolicyException(
            "this statement builds a ROW(…) over "
                + table
                + ", which carries an entitlement, and the star policy refuses that "
                + "(docs/design/16-entitlements.md §3.11).");
      }
    }

    private String entitledIn(SqlNode from) {
      if (from == null) {
        return null;
      }
      if (from instanceof SqlIdentifier identifier) {
        return entitled.entitledTable(identifier);
      }
      if (from instanceof SqlCall call) {
        for (SqlNode operand : call.getOperandList()) {
          String found = entitledIn(operand);
          if (found != null) {
            return found;
          }
        }
      }
      return null;
    }
  }

  private static boolean isStar(SqlNode item) {
    if (item instanceof SqlIdentifier identifier) {
      return identifier.isStar();
    }
    return item instanceof SqlBasicCall call
        && call.getKind() == SqlKind.AS
        && isStar(call.operand(0));
  }

  /** The statement's outermost {@code SELECT}, through an {@code ORDER BY} or a {@code WITH}. */
  private static SqlSelect outermostSelect(SqlNode parsed) {
    SqlNode node = parsed;
    while (true) {
      if (node instanceof SqlOrderBy orderBy) {
        node = orderBy.query;
      } else if (node instanceof SqlWith with) {
        node = with.body;
      } else {
        return node instanceof SqlSelect select ? select : null;
      }
    }
  }

  /** The outermost parsed select list, or null when the statement has none. */
  static SqlNodeList selectListOf(SqlNode parsed) {
    SqlSelect select = outermostSelect(parsed);
    return select == null ? null : select.getSelectList();
  }
}

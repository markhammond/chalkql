package chalk.planner.entitlement;

import chalk.ir.v1.RowType;
import chalk.planner.ErrorCode;
import chalk.planner.rpc.v1.ReportedDisclosure;
import java.util.List;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * Valid SQL the entitlements refuse (the {@code Policy} kind, D143).
 *
 * <p>Distinct from a validation error because the SQL is well formed, and from
 * {@code UNSUPPORTED} because the planner could express it perfectly well and is declining to. Every
 * message names the table, the column and the use, because a refusal a developer cannot act on is a
 * refusal they will work around (docs/design/16-entitlements.md §3.12).
 *
 * <p>Both refusals of §3.5 are decided on the <em>folded</em> rules and never on the descriptor's
 * text: the same statement is legitimate for another principal.
 *
 * <p>A refusal is also structured, for a host that answers it in its own words (D329): its code,
 * the table and column it is about, the use, and what the column does permit. Once the pass has
 * decided the statement's columns, the pipeline attaches the result the statement would have had,
 * so a host can still return the table model beside the error.
 */
public final class PolicyException extends RuntimeException {
  private static final long serialVersionUID = 1L;

  private final ErrorCode code;
  private final String table;
  private final String column;
  private final String use;
  private final List<String> permitted;
  private transient @Nullable RowType output;
  private transient List<ReportedDisclosure> disclosures = List.of();
  private transient org.apache.calcite.rel.@Nullable RelNode tree;
  private transient List<Disclosed> flow = List.of();

  /** A refusal under {@code code} that is about no one column. */
  public PolicyException(ErrorCode code, String message) {
    this(code, "", "", "", List.of(), message);
  }

  /**
   * A refusal about one column of one table.
   *
   * @param table the table, as schema.table, or empty
   * @param column the column, or empty
   * @param use what the statement did with it, in the message's words, or empty
   * @param permitted what the column does permit this principal, or empty
   */
  public PolicyException(
      ErrorCode code,
      String table,
      String column,
      String use,
      List<String> permitted,
      String message) {
    super(message);
    this.code = code;
    this.table = table;
    this.column = column;
    this.use = use;
    this.permitted = List.copyOf(permitted);
  }

  /** The rule refused, as a client reports it: {@code Star}, {@code PopulationOnly}. */
  public ErrorCode code() {
    return code;
  }

  public String table() {
    return table;
  }

  public String column() {
    return column;
  }

  public String use() {
    return use;
  }

  public List<String> permitted() {
    return permitted;
  }

  /** The statement's output columns as the policy would shape them, or null where none is known. */
  public @Nullable RowType output() {
    return output;
  }

  /** Each output column's disclosure, in {@link #output()}'s order; empty where none is known. */
  public List<ReportedDisclosure> disclosures() {
    return disclosures;
  }

  /**
   * The pass's rewrite of a statement it refused before rewriting it, and the flow that labels its
   * columns, from which the pipeline builds {@link #output()}; null where the pass had none.
   */
  public org.apache.calcite.rel.@Nullable RelNode tree() {
    return tree;
  }

  /** The disclosure flow over {@link #tree()}, column by column; empty where there is no tree. */
  public List<Disclosed> flow() {
    return flow;
  }

  /**
   * This refusal with the pass's rewrite of the statement it refused, for the pipeline to name the
   * columns of (D329). The first answer stands, as for {@link #withOutput}.
   */
  public PolicyException withTree(org.apache.calcite.rel.RelNode tree, List<Disclosed> flow) {
    if (this.tree == null) {
      this.tree = tree;
      this.flow = List.copyOf(flow);
    }
    return this;
  }

  /**
   * This refusal with the result the statement would have had. The first answer stands: a refusal
   * that passes two places able to say keeps what the one nearest it said.
   */
  public PolicyException withOutput(RowType output, List<ReportedDisclosure> disclosures) {
    if (this.output == null) {
      this.output = output;
      this.disclosures = List.copyOf(disclosures);
    }
    return this;
  }
}

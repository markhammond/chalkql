package chalk.planner.entitlement;

import chalk.planner.catalog.UserFunction;
import chalk.planner.catalog.UserFunctions;
import com.google.common.collect.ImmutableSet;
import java.util.Locale;
import org.apache.calcite.sql.SqlAggFunction;

/**
 * The aggregates a column may permit over a population-only value (D190,
 * docs/design/16-entitlements.md §1) — the sidecar's copy of the client's own list.
 *
 * <p>Two copies rather than one on the wire, deliberately: the client refuses a bad allow-list at
 * registration, where a host can act on the message, and the planner refuses a bad <em>plan</em> at
 * the taint check, where nothing may be believed. A set the wire carried would be one more thing a
 * caller could get wrong.
 *
 * <p>The set is closed because the question is not "is this a function" but "does this report a
 * population or an individual". {@code SUM0} is in it because
 * {@code AGGREGATE_REDUCE_FUNCTIONS} rewrites {@code AVG} into {@code SUM0} and {@code COUNT}, and
 * refusing it would refuse a correct plan after the optimiser had rewritten it (§3.10); {@code EVERY}
 * and {@code SOME} are Calcite's own spellings of {@code BOOL_AND} and {@code BOOL_OR}.
 *
 * <p>A user-defined aggregate joins the set only when its host declared it {@code Population()},
 * promising that its result reports the group and never one row's value (D295): the registration
 * check asks it of an allow-list's names against the catalog, and the taint check of the operator a
 * plan actually calls.
 */
public final class PopulationAggregates {
  private PopulationAggregates() {}

  /** The permitted names, upper-cased. */
  public static final ImmutableSet<String> PERMITTED =
      ImmutableSet.of(
          "COUNT",
          "SUM",
          "SUM0",
          // Calcite's own spelling of the same operator, which is what the rewrite emits.
          "$SUM0",
          "AVG",
          "STDDEV",
          "STDDEV_POP",
          "STDDEV_SAMP",
          "VARIANCE",
          "VAR_POP",
          "VAR_SAMP",
          "COVAR_POP",
          "COVAR_SAMP",
          "CORR",
          "REGR_COUNT",
          "REGR_AVGX",
          "REGR_AVGY",
          "REGR_INTERCEPT",
          "REGR_R2",
          "REGR_SLOPE",
          "REGR_SXX",
          "REGR_SXY",
          "REGR_SYY",
          "BOOL_AND",
          "BOOL_OR",
          "EVERY",
          "SOME",
          "APPROX_COUNT_DISTINCT");

  /** Whether this function names a population aggregate. Case-insensitive; space is ignored. */
  public static boolean isPermitted(String function) {
    return function != null && PERMITTED.contains(function.trim().toUpperCase(Locale.ROOT));
  }

  /**
   * Whether an allow-list of this catalog may name {@code function} (D295): a population aggregate
   * of the fixed set, or an aggregate the catalog declares {@code Population()}. An allow-list names
   * an aggregate without its schema, so every function of that name must be such an aggregate, and
   * at least one must exist — the client's own twin asks exactly this.
   */
  public static boolean isPermitted(String function, UserFunctions functions) {
    if (isPermitted(function)) {
      return true;
    }
    if (function == null) {
      return false;
    }
    String name = function.trim();
    boolean declared = false;
    for (UserFunction candidate : functions.declarations()) {
      if (!candidate.name().equalsIgnoreCase(name)) {
        continue;
      }
      if (candidate.kind() != chalk.ir.v1.FunctionKind.FUNCTION_KIND_AGGREGATE
          || !candidate.descriptor().getPopulation()) {
        return false;
      }
      declared = true;
    }
    return declared;
  }

  /**
   * Whether the aggregate a plan calls is a population aggregate: a built-in of the fixed set by its
   * name, or a user-defined aggregate whose own declaration says {@code Population()} (D295).
   */
  public static boolean isPermitted(SqlAggFunction aggregation) {
    UserFunction declaration = chalk.planner.plan.UserOperators.declarationOf(aggregation);
    if (declaration != null) {
      return declaration.kind() == chalk.ir.v1.FunctionKind.FUNCTION_KIND_AGGREGATE
          && declaration.descriptor().getPopulation();
    }
    return isPermitted(aggregation.getName());
  }
}

package chalk.planner.entitlement;

import static org.assertj.core.api.Assertions.assertThat;

import chalk.ir.v1.Rel;
import java.util.ArrayList;
import java.util.List;
import org.junit.jupiter.api.Test;

/**
 * The report's walk is exhaustive over the IR's rel kinds (docs/design/16-entitlements.md §3.12).
 *
 * <p>The walk's fallback is conservative rather than wrong — an unmodelled node whose input carried
 * anything other than {@code FULL} reads as {@code PER_ROW} — but a report that says "decided per
 * row" of a column that is plainly masked is a worse answer than the one the caller deserves, and a
 * kind nobody thought about would degrade silently. This test is what stops that: a rel kind added
 * to the IR fails the build until some case of the walk claims it.
 */
class DisclosureFlowCoverageTest {

  @Test
  void every_rel_kind_the_ir_declares_has_a_case_in_the_walk() {
    List<Rel.KindCase> unclaimed = new ArrayList<>();
    for (Rel.KindCase kind : Rel.KindCase.values()) {
      if (kind == Rel.KindCase.KIND_NOT_SET) {
        continue;
      }
      if (!DisclosureFlow.COVERAGE.containsKey(kind)) {
        unclaimed.add(kind);
      }
    }

    assertThat(unclaimed)
        .as(
            "every Rel.kind needs a case in DisclosureFlow: add one, and say in it what the node"
                + " does to a disclosure, rather than letting the fallback answer PER_ROW")
        .isEmpty();
  }

  /** And nothing claims a kind the IR does not declare, which would be a case nobody can reach. */
  @Test
  void the_walk_claims_no_kind_the_ir_does_not_declare() {
    assertThat(DisclosureFlow.COVERAGE.keySet())
        .doesNotContain(Rel.KindCase.KIND_NOT_SET)
        .hasSize(Rel.KindCase.values().length - 1);
  }
}

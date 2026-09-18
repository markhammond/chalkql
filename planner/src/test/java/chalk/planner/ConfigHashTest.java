package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;

import chalk.planner.plan.PushdownPolicy;
import chalk.planner.plan.RuleSets;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import org.apache.calcite.plan.RelOptRule;
import org.junit.jupiter.api.Test;

/**
 * The sidecar's configuration fingerprint (F76, D269 (d), docs/design/03-planner.md §4.1).
 *
 * <p>{@code GetInfoResponse.planner_config_hash} exists so a host can tell that two sidecars plan
 * alike. It covered the Hep pre-pass and Volcano's pinned list and nothing between them, so a rule
 * added to {@code transitivePredicates}, to {@code afterDecorrelation} or to the heuristic join
 * order moved plans without moving the number — and so did a rewrite written as ordinary code,
 * which has no rule to name itself with.
 *
 * <p>{@code PreparedQuery.plan_digest} is the structural fingerprint of one plan and is a different
 * thing; nothing here touches it.
 */
class ConfigHashTest {

  /** The five phases the pipeline runs, by the name it calls each one. */
  @Test
  void the_fingerprint_names_every_phase_of_the_pipeline() {
    assertThat(PlannerConfig.phases().keySet())
        .containsExactly(
            "hep", "transitivePredicates", "afterDecorrelation", "orderJoins", "volcano");
  }

  /** And every rule of every one of them is in what the hash reads. */
  @Test
  void every_rule_of_every_phase_is_in_the_fingerprint() {
    List<String> parts = PlannerConfig.configParts();
    for (Map.Entry<String, List<RelOptRule>> phase : PlannerConfig.phases().entrySet()) {
      assertThat(parts)
          .as("the %s phase is named", phase.getKey())
          .contains("phase=" + phase.getKey());
      assertThat(parts)
          .as("every rule of the %s phase", phase.getKey())
          .containsAll(RuleSets.names(phase.getValue()));
    }
  }

  /**
   * The claim itself: a rule added to <em>any</em> phase moves the hash. The phases are read off
   * the same seam the hash reads, so a phase added later is covered without this test changing.
   */
  @Test
  void adding_a_rule_to_any_phase_moves_the_hash() {
    int before = hashOf(PlannerConfig.configParts());
    for (String phase : PlannerConfig.phases().keySet()) {
      List<String> parts = new ArrayList<>(PlannerConfig.configParts());
      parts.add(parts.indexOf("phase=" + phase) + 1, "ANewRuleInThisPhase");
      assertThat(hashOf(parts))
          .as("a rule added to the %s phase moves the hash", phase)
          .isNotEqualTo(before);
    }
  }

  /**
   * A rule the three middle phases hold is really in there, named rather than inferred: before the
   * fix these were the rules a host could not tell two sidecars apart by.
   */
  @Test
  void the_phases_the_hash_used_to_miss_carry_rules_of_their_own()  {
    List<String> parts = PlannerConfig.configParts();
    for (List<RelOptRule> rules :
        List.of(
            RuleSets.transitivePredicates(),
            RuleSets.afterDecorrelation(),
            RuleSets.joinOrdering())) {
      assertThat(rules).isNotEmpty();
      assertThat(parts).containsAll(RuleSets.names(rules));
    }
  }

  /** The named passes beside the rule lists, and the Calcite pin. */
  @Test
  void the_rewrites_and_the_calcite_pin_are_in_the_fingerprint() {
    List<String> parts = PlannerConfig.configParts();

    assertThat(parts).contains("rewrites=" + chalk.planner.plan.Rewrites.summary());
    assertThat(chalk.planner.plan.Rewrites.summary()).contains("literal-aggregates:");
    assertThat(parts).contains("calcite=" + PlannerConfig.calciteVersion());
  }

  /** A pass that changes what it produces bumps its version, and the hash moves with it. */
  @Test
  void bumping_a_rewrites_version_moves_the_hash() {
    List<String> parts = new ArrayList<>(PlannerConfig.configParts());
    int at = parts.indexOf("rewrites=" + chalk.planner.plan.Rewrites.summary());
    assertThat(at).isNotNegative();
    parts.set(at, "rewrites=literal-aggregates:2");

    assertThat(hashOf(parts)).isNotEqualTo(PlannerConfig.configHash());
  }

  /** And it must not change for anything else: the same configuration is the same number. */
  @Test
  void the_same_configuration_is_the_same_number() {
    assertThat(PlannerConfig.configHash()).isEqualTo(PlannerConfig.configHash());
    assertThat(hashOf(PlannerConfig.configParts())).isEqualTo(PlannerConfig.configHash());
  }

  /**
   * A request option is not configuration: the pushdown policy a request names does not move the
   * sidecar's fingerprint, which is what the proto has always said of it.
   */
  @Test
  void a_requests_pushdown_policy_is_not_part_of_it() {
    assertThat(PlannerConfig.phases().get("volcano"))
        .isEqualTo(RuleSets.volcano(PushdownPolicy.full()));
  }

  private static int hashOf(List<String> parts) {
    java.util.zip.CRC32 crc = new java.util.zip.CRC32();
    for (String part : parts) {
      crc.update(part.getBytes(java.nio.charset.StandardCharsets.UTF_8));
      crc.update((byte) '\n');
    }
    return (int) crc.getValue();
  }
}

package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

import java.util.Map;
import org.junit.jupiter.api.Test;

/**
 * The sidecar's planning-scheduler arguments (docs/design/30-planning-options.md, D243):
 * {@code --planning-workers}, {@code --planning-load-factor} and {@code CHALK_PLANNER_SLICE_MS}.
 */
class PlannerConfigPlanningTest {
  /** Every available processor when neither argument is given. */
  @Test
  void neither_argument_given_is_every_available_processor() {
    PlannerConfig config = PlannerConfig.parse(new String[] {}, Map.of());

    assertThat(config.planningWorkers()).isEqualTo(Runtime.getRuntime().availableProcessors());
  }

  /** {@code --planning-workers} alone names the count directly. */
  @Test
  void planning_workers_alone_is_the_count() {
    PlannerConfig config = PlannerConfig.parse(new String[] {"--planning-workers", "3"}, Map.of());

    assertThat(config.planningWorkers()).isEqualTo(3);
  }

  /** {@code --planning-load-factor} alone is a fraction of the available processors, floored. */
  @Test
  void planning_load_factor_alone_is_a_fraction_of_the_available_processors() {
    int available = Runtime.getRuntime().availableProcessors();
    PlannerConfig config =
        PlannerConfig.parse(new String[] {"--planning-load-factor", "0.5"}, Map.of());

    assertThat(config.planningWorkers()).isEqualTo(Math.max(1, (int) Math.floor(0.5 * available)));
  }

  /** Both given: the minimum of the two, whichever names fewer. */
  @Test
  void both_given_is_the_minimum() {
    int available = Runtime.getRuntime().availableProcessors();
    long fromLoadFactor = Math.max(1, (long) Math.floor(0.5 * available));

    // A huge worker count never wins over a smaller load factor.
    PlannerConfig fewerFromLoadFactor =
        PlannerConfig.parse(
            new String[] {"--planning-workers", "1000000", "--planning-load-factor", "0.5"},
            Map.of());
    assertThat(fewerFromLoadFactor.planningWorkers()).isEqualTo((int) fromLoadFactor);

    // And a small worker count never loses to a generous load factor.
    PlannerConfig fewerFromWorkers =
        PlannerConfig.parse(
            new String[] {"--planning-workers", "1", "--planning-load-factor", "1.0"}, Map.of());
    assertThat(fewerFromWorkers.planningWorkers()).isEqualTo(1);
  }

  /** The floor of one: a load factor small enough to floor to zero still gives one worker. */
  @Test
  void the_floor_of_one_worker_even_for_a_tiny_load_factor() {
    PlannerConfig config =
        PlannerConfig.parse(new String[] {"--planning-load-factor", "0.0001"}, Map.of());

    assertThat(config.planningWorkers()).isEqualTo(1);
  }

  @Test
  void a_load_factor_out_of_range_is_rejected() {
    assertThatThrownBy(
            () -> PlannerConfig.parse(new String[] {"--planning-load-factor", "0"}, Map.of()))
        .isInstanceOf(IllegalArgumentException.class)
        .hasMessageContaining("--planning-load-factor");
    assertThatThrownBy(
            () -> PlannerConfig.parse(new String[] {"--planning-load-factor", "1.5"}, Map.of()))
        .isInstanceOf(IllegalArgumentException.class)
        .hasMessageContaining("--planning-load-factor");
  }

  @Test
  void a_worker_count_under_one_is_rejected() {
    assertThatThrownBy(
            () -> PlannerConfig.parse(new String[] {"--planning-workers", "0"}, Map.of()))
        .isInstanceOf(IllegalArgumentException.class)
        .hasMessageContaining("--planning-workers");
  }

  /** {@code CHALK_PLANNER_SLICE_MS}: the same cadence D239's default period runs on, D243. */
  @Test
  void the_slice_defaults_to_d239s_period_and_is_settable_by_environment() {
    PlannerConfig defaulted = PlannerConfig.parse(new String[] {}, Map.of());
    assertThat(defaulted.sliceMillis())
        .isEqualTo(chalk.planner.diag.PlanningGovernor.DEFAULT_SAMPLE_PERIOD_MILLIS);

    PlannerConfig overridden =
        PlannerConfig.parse(new String[] {}, Map.of("CHALK_PLANNER_SLICE_MS", "5"));
    assertThat(overridden.sliceMillis()).isEqualTo(5L);
  }

  /** {@code --planning-session-limit}: 1024 when unnamed, D245's amendment. */
  @Test
  void the_session_limit_defaults_to_1024_and_is_settable_by_argument() {
    PlannerConfig defaulted = PlannerConfig.parse(new String[] {}, Map.of());
    assertThat(defaulted.planningSessionLimit()).isEqualTo(1024);

    PlannerConfig overridden =
        PlannerConfig.parse(new String[] {"--planning-session-limit", "8"}, Map.of());
    assertThat(overridden.planningSessionLimit()).isEqualTo(8);
  }

  @Test
  void a_session_limit_under_one_is_rejected() {
    assertThatThrownBy(
            () -> PlannerConfig.parse(new String[] {"--planning-session-limit", "0"}, Map.of()))
        .isInstanceOf(IllegalArgumentException.class)
        .hasMessageContaining("--planning-session-limit");
  }
}

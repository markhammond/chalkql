package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;

import java.util.Map;
import org.junit.jupiter.api.Test;

/** {@code --exit-with-host}: a planner a host started for itself goes with the host. */
class PlannerConfigHostTest {
  /** Off unless asked for, so a planner deployed on its own runs on whatever its standard input. */
  @Test
  void a_planner_runs_on_by_default() {
    assertThat(PlannerConfig.parse(new String[] {}, Map.of()).exitWithHost()).isFalse();
  }

  /** Asked for on the command line, beside the transport. */
  @Test
  void exit_with_host_is_a_flag() {
    PlannerConfig config =
        PlannerConfig.parse(new String[] {"--socket", "/tmp/p.sock", "--exit-with-host"}, Map.of());

    assertThat(config.exitWithHost()).isTrue();
    assertThat(config.socketPath()).isEqualTo("/tmp/p.sock");
  }
}

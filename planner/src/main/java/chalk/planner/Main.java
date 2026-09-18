package chalk.planner;

import org.slf4j.Logger;
import org.slf4j.LoggerFactory;

/** Entry point for the Chalk planner sidecar. */
public final class Main {
  private static final Logger LOG = LoggerFactory.getLogger(Main.class);

  private Main() {}

  public static void main(String[] args) throws Exception {
    // Before the first logger exists, or logback will already have read the default config.
    if ("json".equalsIgnoreCase(System.getenv("CHALK_PLANNER_LOG_FORMAT"))) {
      System.setProperty("logback.configurationFile", "logback-json.xml");
    }

    PlannerConfig config;
    try {
      config = PlannerConfig.parse(args);
    } catch (IllegalArgumentException e) {
      System.err.println(e.getMessage());
      System.exit(2);
      return;
    }

    PlannerServer server = PlannerServer.start(config);
    Runtime.getRuntime()
        .addShutdownHook(
            new Thread(
                () -> {
                  LOG.info("shutting down");
                  server.close();
                },
                "chalk-planner-shutdown"));
    server.awaitTermination();
  }
}

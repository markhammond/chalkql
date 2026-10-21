package chalk.planner;

import java.io.IOException;
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
    // A planner a host started for itself goes with the host: its standard input is the host's
    // pipe, which nothing writes to and which the OS closes however the host ends.
    if (config.exitWithHost()) {
      Thread watch =
          new Thread(
              () -> {
                try {
                  while (System.in.read() != -1) {
                    // Nothing is written; anything that is, is read and dropped.
                  }
                } catch (IOException e) {
                  // A pipe that cannot be read is a host that has gone, too.
                }
                LOG.info("its host has gone; shutting down");
                System.exit(0);
              },
              "chalk-planner-host-watch");
      watch.setDaemon(true);
      watch.start();
    }

    server.awaitTermination();
  }
}

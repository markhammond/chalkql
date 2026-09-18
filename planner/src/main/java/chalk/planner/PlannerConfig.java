package chalk.planner;

import chalk.ir.IrVersion;
import chalk.planner.plan.PushdownPolicy;
import chalk.planner.plan.RuleSets;
import chalk.planner.plan.SqlConfigs;
import java.io.IOException;
import java.io.InputStream;
import java.io.UncheckedIOException;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.Properties;
import java.util.zip.CRC32;

/**
 * Process configuration and the planner's identity (docs/design/03-planner.md §1, §6).
 *
 * <p>{@link #configHash()} is part of the client's plan-cache key from M3: it must change whenever
 * anything that could change a plan changes — the rule lists, the cost model, the parser and
 * validator settings, the Calcite version, the IR version. It must *not* change for anything else,
 * or every deploy would invalidate every cached plan.
 */
public final class PlannerConfig {
  /**
   * Bumped by hand whenever a cost function changes without a rule list changing. 9 = a nested
   * loop's charge for a remote inner side reads the whole subtree rather than the boundary chain
   * above it (F55); 8 = that charge itself (F51); 7 = the cross-source join formulas (D103,
   * {@code 20-m5-federation.md} §2); 6 was the user-function constants (D78); 5 was the hop, session
   * and unnest constants (D55, D66); 4 was the window constant and the index-ordered scan.
   */
  public static final int COST_MODEL_VERSION = 9;

  public static final String DEFAULT_HOST = "127.0.0.1";
  public static final int DEFAULT_PORT = 7433;

  /**
   * The longest socket path the planner will accept, in bytes. macOS's {@code sun_path} is 104 bytes
   * including the terminator and Linux's is 108; 100 is the portable budget with room for the
   * terminator on either (09-unix-socket-transport.md §2).
   */
  public static final int MAX_SOCKET_PATH_BYTES = 100;

  /** The sidecar's slice, in milliseconds, when neither the request nor the argument names one. */
  public static final long DEFAULT_SLICE_MILLIS = chalk.planner.diag.PlanningGovernor.DEFAULT_SAMPLE_PERIOD_MILLIS;

  /** The session registry's bound when {@code --planning-session-limit} names none (D245). */
  public static final int DEFAULT_SESSION_LIMIT =
      chalk.planner.rpc.PlanningScheduler.DEFAULT_SESSION_LIMIT;

  /** Shape versions kept per engine instance when {@code --catalog-versions} names none (D271 (e)). */
  public static final int DEFAULT_CATALOG_VERSIONS =
      chalk.planner.catalog.CatalogRegistry.DEFAULT_VERSION_LIMIT;

  /**
   * Minutes an engine instance may go without a request before its catalog is evicted whole, when
   * {@code --catalog-idle-minutes} names none (D271 (e)).
   */
  public static final long DEFAULT_CATALOG_IDLE_MINUTES =
      chalk.planner.catalog.CatalogRegistry.DEFAULT_IDLE.toMinutes();

  private static final String USAGE =
      "Usage: chalk-planner [--host H] [--port P | --socket PATH] [--threads N] "
          + "[--planning-workers N] [--planning-load-factor F] [--planning-session-limit N] "
          + "[--catalog-versions N] [--catalog-idle-minutes N]";

  private final String host;
  private final int port;
  private final String socketPath;
  private final int threads;
  private final int planningWorkers;
  private final long sliceMillis;
  private final int planningSessionLimit;
  private final int catalogVersions;
  private final long catalogIdleMinutes;

  private PlannerConfig(
      String host,
      int port,
      String socketPath,
      int threads,
      int planningWorkers,
      long sliceMillis,
      int planningSessionLimit,
      int catalogVersions,
      long catalogIdleMinutes) {
    this.host = host;
    this.port = port;
    this.socketPath = socketPath;
    this.threads = threads;
    this.planningWorkers = planningWorkers;
    this.sliceMillis = sliceMillis;
    this.planningSessionLimit = planningSessionLimit;
    this.catalogVersions = catalogVersions;
    this.catalogIdleMinutes = catalogIdleMinutes;
  }

  /**
   * Command line over environment over defaults: {@code --host}, {@code --port} ({@code 0} =
   * ephemeral), {@code --socket}, {@code --threads}, {@code --planning-workers},
   * {@code --planning-load-factor}, {@code --planning-session-limit}; {@code CHALK_PLANNER_HOST},
   * {@code CHALK_PLANNER_PORT}, {@code CHALK_PLANNER_SOCKET}, {@code CHALK_PLANNER_THREADS},
   * {@code CHALK_PLANNER_SLICE_MS} (D243).
   *
   * <p>{@code --socket} and {@code --host}/{@code --port} name two different transports, so asking
   * for both at the same level is a startup error rather than a silent preference
   * (09-unix-socket-transport.md §3). Across levels the usual precedence decides, which is what lets
   * {@code --socket} be given to the container image, whose environment already names a port.
   *
   * <p>The planning worker count is command-line only, the way {@code --threads} is not also an
   * environment variable's business at this layer alone — a host that spawns the sidecar picks the
   * worker count for the box it is on, and the two arguments are its whole story: {@code W = max(1,
   * min(n, floor(f × availableProcessors())))} over whichever of {@code --planning-workers <n>} and
   * {@code --planning-load-factor <f>} (0 &lt; f ≤ 1) are given, and every available processor when
   * neither is. The slice, unlike the worker count, is an environment variable alone
   * ({@code CHALK_PLANNER_SLICE_MS}, default {@value #DEFAULT_SLICE_MILLIS}): it is the same cadence
   * D239's default period runs on, just named once for the whole process rather than per request.
   *
   * <p>{@code --planning-session-limit} (default {@value #DEFAULT_SESSION_LIMIT}) is command-line
   * only too, beside the other two planning arguments: how many distinct planning sessions (D245)
   * the sidecar's registry holds before naming a new one evicts the least recently used idle one.
   *
   * <p>{@code --catalog-versions} (default {@value #DEFAULT_CATALOG_VERSIONS}) and
   * {@code --catalog-idle-minutes} (default {@value #DEFAULT_CATALOG_IDLE_MINUTES}) bound the
   * catalog registry the same way (D271 (e)): how many shape versions one engine instance keeps
   * \u2014 the current one and its predecessors \u2014 and how long an instance may go unasked-about
   * before it is evicted whole. Both are memory policy and never correctness: a miss is answered by
   * name and the engine registers the version again and retries once.
   */
  public static PlannerConfig parse(String[] args) {
    return parse(args, System.getenv());
  }

  /** The same, against a given environment, so the precedence rules above are testable. */
  static PlannerConfig parse(String[] args, Map<String, String> environment) {
    String host = env(environment, "CHALK_PLANNER_HOST", DEFAULT_HOST);
    int port =
        Integer.parseInt(env(environment, "CHALK_PLANNER_PORT", Integer.toString(DEFAULT_PORT)));
    String envSocket = env(environment, "CHALK_PLANNER_SOCKET", null);
    boolean envTcp =
        isSet(environment, "CHALK_PLANNER_HOST") || isSet(environment, "CHALK_PLANNER_PORT");
    if (envSocket != null && envTcp) {
      throw new IllegalArgumentException(
          "CHALK_PLANNER_SOCKET and CHALK_PLANNER_HOST/CHALK_PLANNER_PORT are different transports; "
              + "set one or the other, or override both with an option. "
              + USAGE);
    }

    int threads =
        Integer.parseInt(
            env(
                environment,
                "CHALK_PLANNER_THREADS",
                Integer.toString(Runtime.getRuntime().availableProcessors())));
    long sliceMillis =
        Long.parseLong(
            env(environment, "CHALK_PLANNER_SLICE_MS", Long.toString(DEFAULT_SLICE_MILLIS)));

    String argSocket = null;
    boolean argTcp = false;
    Integer planningWorkersArg = null;
    Double planningLoadFactorArg = null;
    Integer planningSessionLimitArg = null;
    Integer catalogVersionsArg = null;
    Long catalogIdleMinutesArg = null;
    for (int i = 0; i < args.length; i++) {
      switch (args[i]) {
        case "--host" -> {
          host = requireValue(args, ++i, "--host");
          argTcp = true;
        }
        case "--port" -> {
          port = Integer.parseInt(requireValue(args, ++i, "--port"));
          argTcp = true;
        }
        case "--socket" -> argSocket = requireValue(args, ++i, "--socket");
        case "--threads" -> threads = Integer.parseInt(requireValue(args, ++i, "--threads"));
        case "--planning-workers" ->
            planningWorkersArg = Integer.parseInt(requireValue(args, ++i, "--planning-workers"));
        case "--planning-load-factor" ->
            planningLoadFactorArg =
                Double.parseDouble(requireValue(args, ++i, "--planning-load-factor"));
        case "--planning-session-limit" ->
            planningSessionLimitArg =
                Integer.parseInt(requireValue(args, ++i, "--planning-session-limit"));
        case "--catalog-versions" ->
            catalogVersionsArg = Integer.parseInt(requireValue(args, ++i, "--catalog-versions"));
        case "--catalog-idle-minutes" ->
            catalogIdleMinutesArg =
                Long.parseLong(requireValue(args, ++i, "--catalog-idle-minutes"));
        default -> throw new IllegalArgumentException("Unknown option '" + args[i] + "'. " + USAGE);
      }
    }
    if (argSocket != null && argTcp) {
      throw new IllegalArgumentException(
          "--socket and --host/--port are different transports; give one or the other. " + USAGE);
    }
    if (threads < 1) {
      throw new IllegalArgumentException("--threads must be at least 1");
    }
    if (planningWorkersArg != null && planningWorkersArg < 1) {
      throw new IllegalArgumentException("--planning-workers must be at least 1");
    }
    if (planningLoadFactorArg != null && (planningLoadFactorArg <= 0 || planningLoadFactorArg > 1)) {
      throw new IllegalArgumentException("--planning-load-factor must be greater than 0 and at most 1");
    }
    if (planningSessionLimitArg != null && planningSessionLimitArg < 1) {
      throw new IllegalArgumentException("--planning-session-limit must be at least 1");
    }
    if (catalogVersionsArg != null && catalogVersionsArg < 1) {
      throw new IllegalArgumentException("--catalog-versions must be at least 1");
    }
    if (catalogIdleMinutesArg != null && catalogIdleMinutesArg < 1) {
      throw new IllegalArgumentException("--catalog-idle-minutes must be at least 1");
    }
    if (sliceMillis < 1) {
      throw new IllegalArgumentException("CHALK_PLANNER_SLICE_MS must be at least 1");
    }
    int planningWorkers = planningWorkers(planningWorkersArg, planningLoadFactorArg);
    int planningSessionLimit =
        planningSessionLimitArg != null ? planningSessionLimitArg : DEFAULT_SESSION_LIMIT;
    int catalogVersions =
        catalogVersionsArg != null ? catalogVersionsArg : DEFAULT_CATALOG_VERSIONS;
    long catalogIdleMinutes =
        catalogIdleMinutesArg != null ? catalogIdleMinutesArg : DEFAULT_CATALOG_IDLE_MINUTES;

    // The command line settles the transport when it says anything about it; otherwise the
    // environment does; otherwise it is TCP on the default port.
    String socketPath = argSocket != null ? argSocket : argTcp ? null : envSocket;
    if (socketPath != null) {
      if (socketPath.isBlank()) {
        throw new IllegalArgumentException("--socket needs a path");
      }
      int bytes = socketPath.getBytes(StandardCharsets.UTF_8).length;
      if (bytes > MAX_SOCKET_PATH_BYTES) {
        throw new IllegalArgumentException(
            "socket path is "
                + bytes
                + " bytes, over the "
                + MAX_SOCKET_PATH_BYTES
                + "-byte limit this platform's sockaddr_un leaves: "
                + socketPath);
      }
    }
    return new PlannerConfig(
        host,
        port,
        socketPath,
        threads,
        planningWorkers,
        sliceMillis,
        planningSessionLimit,
        catalogVersions,
        catalogIdleMinutes);
  }

  /**
   * {@code W = max(1, min(n, floor(f × availableProcessors())))} over whichever of {@code n}
   * (workers) and {@code f} (load factor) were given, and every available processor when neither
   * was (D243).
   */
  private static int planningWorkers(Integer workersArg, Double loadFactorArg) {
    int available = Math.max(1, Runtime.getRuntime().availableProcessors());
    if (workersArg == null && loadFactorArg == null) {
      return available;
    }
    long fromWorkers = workersArg != null ? workersArg : Long.MAX_VALUE;
    long fromLoadFactor =
        loadFactorArg != null ? (long) Math.floor(loadFactorArg * available) : Long.MAX_VALUE;
    return (int) Math.max(1, Math.min(fromWorkers, fromLoadFactor));
  }

  public String host() {
    return host;
  }

  public int port() {
    return port;
  }

  /** The Unix domain socket to bind, or null when the planner listens on TCP. */
  public String socketPath() {
    return socketPath;
  }

  public int threads() {
    return threads;
  }

  /**
   * The planning scheduler's worker count in force (D240, D243): the minimum of
   * {@code --planning-workers} and {@code --planning-load-factor} that were given, floored at one,
   * and every available processor when neither was given.
   */
  public int planningWorkers() {
    return planningWorkers;
  }

  /**
   * The slice, in milliseconds (D239, D243): the sidecar-wide default for
   * {@code PlanningOptions.convergence_sample_period_ms} when a request names none.
   * {@code CHALK_PLANNER_SLICE_MS}, default {@value #DEFAULT_SLICE_MILLIS}.
   */
  public long sliceMillis() {
    return sliceMillis;
  }

  /**
   * The planning session registry's bound (D245): {@code --planning-session-limit}, default
   * {@value #DEFAULT_SESSION_LIMIT}. Naming a distinct session past this many evicts the least
   * recently used one with nothing queued or running.
   */
  public int planningSessionLimit() {
    return planningSessionLimit;
  }

  /**
   * Shape versions the catalog registry keeps per engine instance (D271 (e)): {@code
   * --catalog-versions}, default {@value #DEFAULT_CATALOG_VERSIONS}. Beyond it the least recently
   * used goes, and a miss is answered by name for the client to recover from.
   */
  public int catalogVersions() {
    return catalogVersions;
  }

  /**
   * Minutes an engine instance may go without a request before its catalog is evicted whole (D271
   * (e)): {@code --catalog-idle-minutes}, default {@value #DEFAULT_CATALOG_IDLE_MINUTES}.
   */
  public long catalogIdleMinutes() {
    return catalogIdleMinutes;
  }

  public static String plannerVersion() {
    return BuildInfo.PROPERTIES.getProperty("planner.version", "unknown");
  }

  public static String calciteVersion() {
    return BuildInfo.PROPERTIES.getProperty("calcite.version", "unknown");
  }

  /**
   * A hash of everything that could change a plan. Deliberately built from ordered lists of rule
   * names, never from a set: iteration order must not leak in.
   *
   * <p>It is the <b>sidecar's configuration</b> fingerprint and nothing else (D269 (d)): every rule
   * list of every phase the pipeline runs, every named pass beside them, the Calcite pin, the cost
   * model and the parser's settings, so that two sidecars reporting the same number plan alike. The
   * structural fingerprint of one plan is {@code PreparedQuery.plan_digest}, which is a different
   * thing and is untouched by any of this.
   */
  public static int configHash() {
    CRC32 crc = new CRC32();
    for (String part : configParts()) {
      crc.update(part.getBytes(StandardCharsets.UTF_8));
      crc.update((byte) '\n');
    }
    return (int) crc.getValue();
  }

  /**
   * Everything {@link #configHash} hashes, in order — the seam a test reads, so that "this phase is
   * covered" is an assertion rather than a claim about a number.
   */
  public static List<String> configParts() {
    List<String> parts = new ArrayList<>();
    parts.add("ir=" + IrVersion.CURRENT);
    parts.add("calcite=" + calciteVersion());
    parts.add("cost=" + COST_MODEL_VERSION);
    // The planner's defaults only. A client's CostProfile travels in the catalog and is covered by
    // the catalog epoch, not by this hash (D38).
    parts.add("costs=" + chalk.planner.plan.CostModel.summary());
    parts.add("sql=" + SqlConfigs.summary());
    parts.add("joins=" + chalk.planner.plan.JoinOrdering.summary());
    parts.add(chalk.planner.plan.ChalkOperatorTable.summary());
    parts.add(chalk.planner.plan.DistinctStrategy.summary());
    // The named passes the pipeline runs beside its rule lists: a rewrite written as ordinary code
    // moves plans and has no rule to name itself with (F76).
    parts.add("rewrites=" + chalk.planner.plan.Rewrites.summary());
    // Every phase, in the order the pipeline runs them. Before F76 the hash covered the first and
    // the last of these and nothing between, so a rule added to the middle three moved plans
    // without moving the number a host compares.
    for (Map.Entry<String, List<org.apache.calcite.plan.RelOptRule>> phase : phases().entrySet()) {
      parts.add("phase=" + phase.getKey());
      parts.addAll(RuleSets.names(phase.getValue()));
    }
    return parts;
  }

  /**
   * The rule lists of the pipeline's phases, by the name {@code PlannerPipeline} calls each one —
   * the Hep pre-pass, the transitive-predicate pass, what follows decorrelation, the heuristic join
   * order, and Volcano's own pinned list (docs/design/03-planner.md §4).
   *
   * <p>Volcano's list is taken at full pushdown, which is the configuration rather than the request:
   * {@code PlanRequest.pushdown} is a request option and is deliberately not in this hash.
   */
  public static java.util.LinkedHashMap<String, List<org.apache.calcite.plan.RelOptRule>> phases() {
    java.util.LinkedHashMap<String, List<org.apache.calcite.plan.RelOptRule>> phases =
        new java.util.LinkedHashMap<>();
    phases.put("hep", RuleSets.hep());
    phases.put("transitivePredicates", RuleSets.transitivePredicates());
    phases.put("afterDecorrelation", RuleSets.afterDecorrelation());
    phases.put("orderJoins", RuleSets.joinOrdering());
    phases.put("volcano", RuleSets.volcano(PushdownPolicy.full()));
    return phases;
  }

  private static String env(Map<String, String> environment, String name, String fallback) {
    String value = environment.get(name);
    return value == null || value.isBlank() ? fallback : value;
  }

  /** Set *and* not blank: an exported-but-empty variable is not a request for a transport. */
  private static boolean isSet(Map<String, String> environment, String name) {
    String value = environment.get(name);
    return value != null && !value.isBlank();
  }

  private static String requireValue(String[] args, int index, String option) {
    if (index >= args.length) {
      throw new IllegalArgumentException(option + " needs a value");
    }
    return args[index];
  }

  /** Written by the Gradle {@code generateBuildInfo} task so the jar knows what it was built from. */
  private static final class BuildInfo {
    static final Properties PROPERTIES = load();

    private static Properties load() {
      Properties properties = new Properties();
      try (InputStream in =
          PlannerConfig.class.getClassLoader().getResourceAsStream("chalk/planner/BuildInfo.properties")) {
        if (in != null) {
          properties.load(in);
        }
      } catch (IOException e) {
        throw new UncheckedIOException("reading chalk/planner/BuildInfo.properties", e);
      }
      return properties;
    }
  }
}

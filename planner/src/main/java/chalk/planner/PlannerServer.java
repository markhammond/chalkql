package chalk.planner;

import chalk.planner.catalog.CatalogRegistry;
import chalk.planner.rpc.PlannerServiceImpl;
import chalk.planner.rpc.PlanningScheduler;
import io.grpc.Server;
import io.grpc.netty.shaded.io.grpc.netty.NettyServerBuilder;
import io.grpc.netty.shaded.io.netty.channel.EventLoopGroup;
import io.grpc.netty.shaded.io.netty.channel.MultiThreadIoEventLoopGroup;
import io.grpc.netty.shaded.io.netty.channel.nio.NioIoHandler;
import io.grpc.netty.shaded.io.netty.channel.socket.nio.NioServerDomainSocketChannel;
import io.grpc.protobuf.services.HealthStatusManager;
import io.grpc.protobuf.services.ProtoReflectionServiceV1;
import java.io.IOException;
import java.net.InetSocketAddress;
import java.net.StandardProtocolFamily;
import java.net.UnixDomainSocketAddress;
import java.nio.channels.SocketChannel;
import java.nio.file.Files;
import java.nio.file.LinkOption;
import java.nio.file.Path;
import java.nio.file.attribute.PosixFilePermissions;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.TimeUnit;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;

/**
 * Server lifecycle (docs/design/03-planner.md §1, docs/design/09-unix-socket-transport.md §3). On a
 * successful bind it prints exactly one line to stdout — {@code chalk-planner listening on
 * <host>:<port>} or {@code chalk-planner listening on unix:<path>} — which the test fixture and any
 * launcher parse; everything else goes to stderr through SLF4J.
 */
public final class PlannerServer implements AutoCloseable {
  private static final Logger LOG = LoggerFactory.getLogger(PlannerServer.class);

  /** Catalogs can be large: 64 MiB, per §1. */
  private static final int MAX_INBOUND_MESSAGE_BYTES = 64 * 1024 * 1024;

  private static final int SHUTDOWN_DRAIN_SECONDS = 5;

  private final Server server;
  private final ExecutorService executor;
  private final PlanningScheduler scheduler;
  private final HealthStatusManager health;
  private final String host;
  private final Path socketFile;
  private final EventLoopGroup boss;
  private final EventLoopGroup worker;

  private PlannerServer(
      Server server,
      ExecutorService executor,
      PlanningScheduler scheduler,
      HealthStatusManager health,
      String host,
      Path socketFile,
      EventLoopGroup boss,
      EventLoopGroup worker) {
    this.server = server;
    this.executor = executor;
    this.scheduler = scheduler;
    this.health = health;
    this.host = host;
    this.socketFile = socketFile;
    this.boss = boss;
    this.worker = worker;
  }

  /**
   * Binds and starts. Port 0 asks the OS for an ephemeral port; read it back with {@link #port()}.
   * With {@link PlannerConfig#socketPath()} set it binds a Unix domain socket instead — see
   * {@link #listenAddress()} for what was printed.
   */
  public static PlannerServer start(PlannerConfig config) throws IOException {
    // Before anything is allocated: a path that cannot be bound should cost nothing to find out.
    Path socketFile = config.socketPath() == null ? null : Path.of(config.socketPath());
    if (socketFile != null) {
      clearSocketPath(socketFile);
    }

    // Bounded per engine instance, and evicted whole after an idle period (D271 (e)): memory
    // policy, never correctness — a miss is answered by name and the engine registers again.
    CatalogRegistry registry =
        new CatalogRegistry(
            config.catalogVersions(), java.time.Duration.ofMinutes(config.catalogIdleMinutes()));
    HealthStatusManager health = new HealthStatusManager();
    ExecutorService executor = Executors.newFixedThreadPool(config.threads());
    // The bounded pool every planning time-slices under (D240, D243): the gRPC executor above hands
    // a request here and never plans itself. Its session registry is bounded too (D245).
    PlanningScheduler scheduler =
        new PlanningScheduler(config.planningWorkers(), config.planningSessionLimit());

    EventLoopGroup boss = null;
    EventLoopGroup worker = null;
    NettyServerBuilder builder;
    if (socketFile == null) {
      // An explicit address, not just a port: `--host 127.0.0.1` has to mean loopback only, which is
      // the default outside a container (§1). The image sets CHALK_PLANNER_HOST=0.0.0.0.
      builder = NettyServerBuilder.forAddress(new InetSocketAddress(config.host(), config.port()));
    } else {
      // NioServerDomainSocketChannel is Netty's JDK-16 Unix-socket channel: portable, and the only
      // choice on macOS, where the shaded jar carries no kqueue native (ADR 0013). It hands the
      // address straight to the JDK channel, so it must be a java.net.UnixDomainSocketAddress and
      // not Netty's own DomainSocketAddress, which is for the epoll/kqueue transports. grpc-java
      // will not pick event loop groups for a channel type it does not know, so both are explicit.
      boss = new MultiThreadIoEventLoopGroup(1, NioIoHandler.newFactory());
      worker = new MultiThreadIoEventLoopGroup(NioIoHandler.newFactory());
      builder =
          NettyServerBuilder.forAddress(UnixDomainSocketAddress.of(socketFile))
              .channelType(NioServerDomainSocketChannel.class)
              .bossEventLoopGroup(boss)
              .workerEventLoopGroup(worker);
    }

    Server server;
    try {
      server =
          builder
              .executor(executor)
              .maxInboundMessageSize(MAX_INBOUND_MESSAGE_BYTES)
              .addService(
                  new PlannerServiceImpl(
                      registry,
                      chalk.planner.ext.PlanExtensionRegistry.defaults(),
                      scheduler,
                      config.sliceMillis()))
              .addService(health.getHealthService())
              // Reflection makes grpcurl work against a running sidecar, which is worth the few
              // kilobytes when someone is trying to work out why a plan looks wrong.
              .addService(ProtoReflectionServiceV1.newInstance())
              .build()
              .start();
    } catch (IOException | RuntimeException e) {
      executor.shutdownNow();
      scheduler.close();
      shutdownGroups(boss, worker);
      throw e;
    }

    if (socketFile != null) {
      restrictToOwner(socketFile);
    }

    health.setStatus("", io.grpc.health.v1.HealthCheckResponse.ServingStatus.SERVING);
    health.setStatus(
        "chalk.v1.PlannerService", io.grpc.health.v1.HealthCheckResponse.ServingStatus.SERVING);

    PlannerServer planner =
        new PlannerServer(
            server, executor, scheduler, health, config.host(), socketFile, boss, worker);
    // The one line on stdout. Anything else on stdout would break the fixture's parsing.
    System.out.println("chalk-planner listening on " + planner.listenAddress());
    System.out.flush();
    LOG.info(
        "chalk-planner {} started (calcite {}, ir {}, config hash {})",
        PlannerConfig.plannerVersion(),
        PlannerConfig.calciteVersion(),
        chalk.ir.IrVersion.CURRENT,
        Integer.toUnsignedString(PlannerConfig.configHash()));
    return planner;
  }

  /** The port actually bound, which is what matters when the configured port was 0; -1 on a socket. */
  public int port() {
    return socketFile == null ? server.getPort() : -1;
  }

  /** The socket file this server bound, or null on TCP. */
  public Path socketFile() {
    return socketFile;
  }

  /** What follows {@code chalk-planner listening on } on stdout. */
  public String listenAddress() {
    return socketFile == null ? host + ":" + port() : "unix:" + socketFile;
  }

  public void awaitTermination() throws InterruptedException {
    server.awaitTermination();
  }

  /**
   * Stop accepting, drain briefly, then exit — the SIGTERM path from §1. The socket file goes with
   * the server: neither the JDK's Unix channel nor Netty unlinks it, and a file left behind is what
   * the next start has to recognise as stale.
   */
  @Override
  public void close() {
    health.enterTerminalState();
    server.shutdown();
    try {
      if (!server.awaitTermination(SHUTDOWN_DRAIN_SECONDS, TimeUnit.SECONDS)) {
        server.shutdownNow();
      }
    } catch (InterruptedException e) {
      Thread.currentThread().interrupt();
      server.shutdownNow();
    } finally {
      executor.shutdownNow();
      scheduler.close();
      shutdownGroups(boss, worker);
      deleteSocketFile();
    }
  }

  private void deleteSocketFile() {
    if (socketFile == null) {
      return;
    }
    try {
      Files.deleteIfExists(socketFile);
    } catch (IOException e) {
      LOG.warn("could not remove the socket file {}: {}", socketFile, e.toString());
    }
  }

  /**
   * A socket file left by a crashed planner has to be unlinked before bind, and one a live planner
   * owns must not be (§3). The difference is whether anything answers, so this connects to find out.
   */
  private static void clearSocketPath(Path path) throws IOException {
    if (!Files.exists(path, LinkOption.NOFOLLOW_LINKS)) {
      Path parent = path.toAbsolutePath().getParent();
      if (parent != null && !Files.isDirectory(parent)) {
        throw new IOException("the directory for socket " + path + " does not exist: " + parent);
      }
      return;
    }

    if (Files.isRegularFile(path) || Files.isDirectory(path)) {
      // Never unlink something that is not a socket: the path may be a typo for real data.
      throw new IOException(path + " already exists and is not a socket");
    }

    boolean answered;
    try (SocketChannel probe = SocketChannel.open(StandardProtocolFamily.UNIX)) {
      answered = probe.connect(UnixDomainSocketAddress.of(path));
    } catch (IOException refused) {
      // Nothing answered, so the file is a leftover from a planner that did not shut down cleanly.
      LOG.info("removing the stale socket file {} ({})", path, refused);
      Files.deleteIfExists(path);
      return;
    }

    if (answered) {
      throw new IOException("another chalk-planner is listening on " + path);
    }
    Files.deleteIfExists(path);
  }

  /**
   * The socket carries every query this process will plan, so it is owner-only. There is a window
   * between bind and here in which the process umask governs; ADR 0013 records why it is acceptable.
   */
  private static void restrictToOwner(Path path) {
    try {
      Files.setPosixFilePermissions(path, PosixFilePermissions.fromString("rw-------"));
    } catch (IOException | UnsupportedOperationException e) {
      LOG.warn("could not restrict {} to its owner: {}", path, e.toString());
    }
  }

  private static void shutdownGroups(EventLoopGroup boss, EventLoopGroup worker) {
    if (boss != null) {
      boss.shutdownGracefully(0, SHUTDOWN_DRAIN_SECONDS, TimeUnit.SECONDS);
    }
    if (worker != null) {
      worker.shutdownGracefully(0, SHUTDOWN_DRAIN_SECONDS, TimeUnit.SECONDS);
    }
  }
}

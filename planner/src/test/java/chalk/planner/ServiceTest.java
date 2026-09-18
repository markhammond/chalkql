package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatCode;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

import chalk.ir.IrVersion;
import chalk.planner.catalog.CatalogRegistry;
import chalk.planner.rpc.PlanErrors;
import chalk.planner.rpc.PlannerServiceImpl;
import chalk.planner.rpc.v1.GetInfoRequest;
import chalk.planner.rpc.v1.GetInfoResponse;
import chalk.planner.rpc.v1.PlanError;
import chalk.planner.rpc.v1.PlanErrorKind;
import chalk.planner.rpc.v1.PlanRequest;
import chalk.planner.rpc.v1.PlanResponse;
import chalk.planner.rpc.v1.PlannerOptions;
import chalk.planner.rpc.v1.PlannerServiceGrpc;
import chalk.planner.rpc.v1.PushdownLevel;
import chalk.planner.rpc.v1.RegisterCatalogRequest;
import com.google.protobuf.InvalidProtocolBufferException;
import io.grpc.ManagedChannel;
import io.grpc.Server;
import io.grpc.StatusRuntimeException;
import io.grpc.inprocess.InProcessChannelBuilder;
import io.grpc.inprocess.InProcessServerBuilder;
import io.grpc.netty.shaded.io.grpc.netty.NettyChannelBuilder;
import io.grpc.netty.shaded.io.netty.channel.EventLoopGroup;
import io.grpc.netty.shaded.io.netty.channel.MultiThreadIoEventLoopGroup;
import io.grpc.netty.shaded.io.netty.channel.nio.NioIoHandler;
import io.grpc.netty.shaded.io.netty.channel.socket.nio.NioDomainSocketChannel;
import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.PrintStream;
import java.net.StandardProtocolFamily;
import java.net.UnixDomainSocketAddress;
import java.nio.channels.ServerSocketChannel;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.Comparator;
import java.util.Map;
import java.util.concurrent.TimeUnit;
import org.junit.jupiter.api.AfterAll;
import org.junit.jupiter.api.AfterEach;
import org.junit.jupiter.api.BeforeAll;
import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Nested;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.CsvSource;

/**
 * The RPC surface and the error mapping of docs/design/03-planner.md §7, over a real in-process
 * channel so the trailer encoding is exercised rather than assumed.
 */
class ServiceTest {
  private static Server server;
  private static ManagedChannel channel;
  private static PlannerServiceGrpc.PlannerServiceBlockingStub client;

  @BeforeAll
  static void startServer() throws IOException {
    String name = InProcessServerBuilder.generateName();
    server =
        InProcessServerBuilder.forName(name)
            .directExecutor()
            .addService(new PlannerServiceImpl(new CatalogRegistry()))
            .build()
            .start();
    channel = InProcessChannelBuilder.forName(name).directExecutor().build();
    client = PlannerServiceGrpc.newBlockingStub(channel);

    client.registerCatalog(
        RegisterCatalogRequest.newBuilder().setCatalog(TestCatalogs.corpus()).build());
  }

  @AfterAll
  static void stopServer() throws InterruptedException {
    channel.shutdownNow();
    server.shutdownNow();
    channel.awaitTermination(5, TimeUnit.SECONDS);
    server.awaitTermination(5, TimeUnit.SECONDS);
  }

  private static PlanRequest.Builder request(String sql) {
    return PlanRequest.newBuilder()
        .setSql(sql)
        .setContextId(TestCatalogs.CONTEXT_ID)
        .setCatalogEpoch(TestCatalogs.EPOCH)
        .setClientIrVersion(IrVersion.CURRENT)
        .setOptions(PlannerOptions.newBuilder().setPushdown(PushdownLevel.PUSHDOWN_LEVEL_FULL));
  }

  @Test
  void get_info_reports_the_ir_range_and_a_stable_config_hash() {
    GetInfoResponse info = client.getInfo(GetInfoRequest.getDefaultInstance());

    assertThat(info.getMinIrVersion()).isEqualTo(IrVersion.MINIMUM);
    assertThat(info.getMaxIrVersion()).isEqualTo(IrVersion.CURRENT);
    // The literal is deliberate: it is the one place a change of Calcite has to be *stated*, not
    // inferred. The Calcite 1.43 trial pins a timestamped snapshot build
    // (docs/design/calcite-143-trial.md); this becomes "1.43.0" when the release lands.
    assertThat(info.getCalciteVersion()).isEqualTo("1.43.0-20260915.055006-238");
    assertThat(info.getPlannerVersion()).isNotBlank();
    assertThat(info.getPlannerConfigHash())
        .isEqualTo(client.getInfo(GetInfoRequest.getDefaultInstance()).getPlannerConfigHash());
  }

  /** D248: what this sidecar accepts as a source's dialect, conformance level and library. */
  @Test
  void get_info_lists_the_dialects_conformances_and_libraries_this_sidecar_accepts() {
    GetInfoResponse info = client.getInfo(GetInfoRequest.getDefaultInstance());

    java.util.Map<String, chalk.planner.rpc.v1.DialectInfo> byName = new java.util.HashMap<>();
    info.getDialectsList().forEach(d -> byName.put(d.getName(), d));

    assertThat(byName.keySet()).contains("sqlite", "duckdb", "postgresql", "ansi");
    assertThat(byName.get("sqlite").getTuned()).isTrue();
    assertThat(byName.get("duckdb").getTuned()).isTrue();
    assertThat(byName.get("postgresql").getTuned()).isTrue();
    assertThat(byName.get("postgresql").getAliasesList()).containsExactly("postgres");
    assertThat(byName.get("ansi").getTuned()).isTrue();
    assertThat(byName.get("ansi").getDatabaseProduct()).isEmpty();
    // postgres is an alias, not its own entry.
    assertThat(byName).doesNotContainKey("postgres");

    // Every other product the pinned Calcite ships is untuned — the conformance kit run by the
    // host, not a run here, is what a host learns them from (design 31 §4).
    assertThat(byName.get("oracle").getTuned()).isFalse();
    assertThat(byName.get("oracle").getDatabaseProduct()).isEqualTo("ORACLE");
    assertThat(byName.get("mssql").getTuned()).isFalse();
    assertThat(byName.get("big_query").getTuned()).isFalse();
    assertThat(byName.get("big_query").getDatabaseProduct()).isEqualTo("BIG_QUERY");
    // jethro's own default dialect factory refuses a context-free instance to build a preset from
    // (V196, ADR 0032); the sidecar does not offer it as a name it accepts.
    assertThat(byName).doesNotContainKey("jethro");

    // Every level and library dialect.proto declares, `UNSPECIFIED` (the wire's "nothing said") and
    // protobuf's own `UNRECOGNIZED` excepted — neither is a value the sidecar "accepts".
    assertThat(info.getConformancesList())
        .containsExactlyInAnyOrderElementsOf(
            java.util.Arrays.stream(chalk.ir.v1.SqlConformance.values())
                .filter(
                    c ->
                        c != chalk.ir.v1.SqlConformance.SQL_CONFORMANCE_UNSPECIFIED
                            && c != chalk.ir.v1.SqlConformance.UNRECOGNIZED)
                .toList());
    assertThat(info.getLibrariesList())
        .containsExactlyInAnyOrderElementsOf(
            java.util.Arrays.stream(chalk.ir.v1.SqlLibrary.values())
                .filter(
                    l ->
                        l != chalk.ir.v1.SqlLibrary.SQL_LIBRARY_UNSPECIFIED
                            && l != chalk.ir.v1.SqlLibrary.UNRECOGNIZED)
                .toList());
  }

  @Test
  void register_is_idempotent_and_replaces_the_previous_catalog() {
    client.registerCatalog(
        RegisterCatalogRequest.newBuilder().setCatalog(TestCatalogs.corpus()).build());
    client.registerCatalog(
        RegisterCatalogRequest.newBuilder().setCatalog(TestCatalogs.corpus()).build());

    assertThat(client.plan(request("SELECT symbol FROM bars").build()).getPlan().hasRoot()).isTrue();
  }

  @Test
  void a_plan_comes_back_with_its_digest_and_timings() {
    PlanResponse response = client.plan(request("SELECT symbol, ts FROM bars").build());

    assertThat(response.getPlan().getPlanDigest()).isNotZero();
    assertThat(response.getPlan().getContextId()).isEqualTo(TestCatalogs.CONTEXT_ID);
    assertThat(response.getPlan().getCatalogEpoch()).isEqualTo(TestCatalogs.EPOCH);
    assertThat(response.getStats().getTotalMicros()).isGreaterThanOrEqualTo(0);
    assertThat(response.getPlanText()).isEmpty();
  }

  @Test
  void plan_text_and_rules_fired_arrive_only_when_asked_for() {
    PlanResponse response =
        client.plan(
            request("SELECT symbol, ts FROM bars")
                .setOptions(
                    PlannerOptions.newBuilder()
                        .setPushdown(PushdownLevel.PUSHDOWN_LEVEL_FULL)
                        .setIncludePlanText(true))
                .build());

    assertThat(response.getPlanText()).contains("-- logical").contains("-- physical");
    assertThat(response.getStats().getRulesFiredList()).isNotEmpty();
  }

  @ParameterizedTest(name = "{0} -> {1}")
  @CsvSource({
    "'SELEC * FROM bars', PLAN_ERROR_KIND_PARSE, true",
    "'SELECT nope FROM bars', PLAN_ERROR_KIND_VALIDATION, true",
    "'SELECT * FROM nope', PLAN_ERROR_KIND_VALIDATION, true",
    "'SELECT * FROM bars b RIGHT ASOF JOIN bars q MATCH_CONDITION b.ts >= q.ts ON b.symbol = q.symbol', PLAN_ERROR_KIND_PARSE, true",
    "'SELECT b.symbol FROM bars b ASOF JOIN bars q MATCH_CONDITION b.ts <> q.ts ON b.symbol = q.symbol', PLAN_ERROR_KIND_VALIDATION, true",
    "'SELECT b.symbol FROM bars b JOIN symbols s ON b.ts = s.tick_size', PLAN_ERROR_KIND_VALIDATION, true",
  })
  void the_negative_corpus_maps_to_the_right_error_kind(
      String sql, PlanErrorKind expected, boolean hasPosition) {
    StatusRuntimeException error =
        assertThatThrownBy(() -> client.plan(request(sql).build()))
            .isInstanceOf(StatusRuntimeException.class)
            .extracting(e -> (StatusRuntimeException) e)
            .actual();

    PlanError planError = decode(error);
    assertThat(planError.getKind()).isEqualTo(expected);
    assertThat(planError.getMessage()).isNotBlank();
    if (hasPosition) {
      assertThat(planError.getLine()).as("%s should carry a SQL position", sql).isGreaterThan(0);
      assertThat(planError.getColumn()).isGreaterThan(0);
    }
  }

  @Test
  void an_unknown_instance_is_answered_by_name() {
    StatusRuntimeException error =
        assertThatThrownBy(
                () ->
                    client.plan(
                        request("SELECT symbol FROM bars").setContextId("nope").build()))
            .isInstanceOf(StatusRuntimeException.class)
            .extracting(e -> (StatusRuntimeException) e)
            .actual();

    assertThat(error.getStatus().getCode()).isEqualTo(io.grpc.Status.Code.FAILED_PRECONDITION);
    assertThat(decode(error).getKind())
        .isEqualTo(PlanErrorKind.PLAN_ERROR_KIND_UNKNOWN_CATALOG_VERSION);
    assertThat(decode(error).getMessage()).contains("nope");
  }

  /**
   * D271 (b): the shape version is what a plan is keyed to, so the epoch is carried for the plan's
   * own diagnostics and no longer checked. The stale epoch that used to be an EPOCH_MISMATCH now
   * plans, because nothing about the catalog it names has moved.
   */
  @Test
  void a_stale_epoch_is_no_longer_checked() {
    assertThat(
            client
                .plan(request("SELECT symbol FROM bars").setCatalogEpoch(99).build())
                .getPlan()
                .getCatalogEpoch())
        .isEqualTo(1L);
  }

  /** A shape version this planner does not hold is answered by name, for the client to recover. */
  @Test
  void an_unknown_shape_version_is_answered_by_name() {
    StatusRuntimeException error =
        assertThatThrownBy(
                () ->
                    client.plan(
                        request("SELECT symbol FROM bars")
                            .setShapeVersion("01JCATALOGVERSIONXXXXXXXXXX")
                            .build()))
            .isInstanceOf(StatusRuntimeException.class)
            .extracting(e -> (StatusRuntimeException) e)
            .actual();

    assertThat(error.getStatus().getCode()).isEqualTo(io.grpc.Status.Code.FAILED_PRECONDITION);
    assertThat(decode(error).getKind())
        .isEqualTo(PlanErrorKind.PLAN_ERROR_KIND_UNKNOWN_CATALOG_VERSION);
    assertThat(decode(error).getMessage()).contains("01JCATALOGVERSIONXXXXXXXXXX");
  }

  @Test
  void an_unservable_ir_version_is_rejected_before_any_planning() {
    StatusRuntimeException error =
        assertThatThrownBy(
                () ->
                    client.plan(
                        request("SELECT symbol FROM bars")
                            .setClientIrVersion(IrVersion.CURRENT + 1)
                            .build()))
            .isInstanceOf(StatusRuntimeException.class)
            .extracting(e -> (StatusRuntimeException) e)
            .actual();

    assertThat(decode(error).getKind()).isEqualTo(PlanErrorKind.PLAN_ERROR_KIND_IR_VERSION);
  }

  @Test
  void an_invalid_catalog_is_rejected_at_registration() {
    chalk.ir.v1.CatalogContext broken =
        TestCatalogs.corpus().toBuilder().setContextId("").build();

    StatusRuntimeException error =
        assertThatThrownBy(
                () ->
                    client.registerCatalog(
                        RegisterCatalogRequest.newBuilder().setCatalog(broken).build()))
            .isInstanceOf(StatusRuntimeException.class)
            .extracting(e -> (StatusRuntimeException) e)
            .actual();

    assertThat(decode(error).getKind()).isEqualTo(PlanErrorKind.PLAN_ERROR_KIND_INVALID_CATALOG);
  }

  // -------------------------------------------------------- D256: an unrecognised dialect name

  private static chalk.ir.v1.DialectProfile profileNamed(String dialect) {
    return chalk.ir.v1.DialectProfile.newBuilder()
        .setDialect(dialect)
        .setQuoting(chalk.ir.v1.IdentifierQuoting.IDENTIFIER_QUOTING_DOUBLE_QUOTE)
        .build();
  }

  // Each call names its own context id: TestCatalogs.withRemote builds on the "corpus" context
  // every other test in this class shares, and a successful registration here must not replace
  // it out from under a test that runs after this one (JUnit does not order these).
  private static void register(chalk.ir.v1.DialectProfile profile, String contextId) {
    client.registerCatalog(
        RegisterCatalogRequest.newBuilder()
            .setCatalog(
                TestCatalogs.withRemote("db256", TestCatalogs.fullSqlCapabilities().build(), profile)
                    .toBuilder()
                    .setContextId(contextId)
                    .build())
            .build());
  }

  /** D256: naming the source, the name, and the accepted presets — this is the message verbatim. */
  @Test
  void an_unrecognised_dialect_name_is_refused_at_registration_naming_the_source_the_name_and_the_presets() {
    StatusRuntimeException error =
        assertThatThrownBy(() -> register(profileNamed("not-a-real-dialect"), "d256-refused"))
            .isInstanceOf(StatusRuntimeException.class)
            .extracting(e -> (StatusRuntimeException) e)
            .actual();

    PlanError decoded = decode(error);
    assertThat(decoded.getKind()).isEqualTo(PlanErrorKind.PLAN_ERROR_KIND_INVALID_CATALOG);
    assertThat(decoded.getMessage())
        .contains("db256") // the source
        .contains("not-a-real-dialect") // the name
        .contains("sqlite") // the accepted presets, GetInfo's own list
        .contains("duckdb")
        .contains("postgresql")
        .contains("ansi")
        .contains("oracle")
        .contains("big_query");
  }

  /** jethro is a real DatabaseProduct, but this sidecar does not offer it (V196, ADR 0032). */
  @Test
  void jethro_is_refused_too() {
    StatusRuntimeException error =
        assertThatThrownBy(() -> register(profileNamed("jethro"), "d256-jethro"))
            .isInstanceOf(StatusRuntimeException.class)
            .extracting(e -> (StatusRuntimeException) e)
            .actual();

    assertThat(decode(error).getKind()).isEqualTo(PlanErrorKind.PLAN_ERROR_KIND_INVALID_CATALOG);
  }

  /**
   * Empty is not "a remote schema", so it does not have to name a dialect at all — that is a
   * different, older rule ({@code "a remote schema must name the SQL dialect its source speaks"}).
   * A local source (a POCO collection, say) is where D249's "empty means ANSI" is actually reached,
   * and D256 does not touch it.
   */
  @Test
  void an_empty_dialect_name_is_untouched_and_still_registers() {
    chalk.ir.v1.CatalogContext localWithEmptyDialect =
        chalk.ir.v1.CatalogContext.newBuilder()
            .setContextId("d256-empty")
            .setEpoch(1)
            .addSchemas(
                chalk.ir.v1.Schema.newBuilder()
                    .setSourceId("local256")
                    .setName("local256")
                    .setKind(chalk.ir.v1.SourceKind.SOURCE_KIND_LOCAL)
                    .setCapabilities(
                        chalk.ir.v1.SourceCapabilities.newBuilder()
                            .setQueryLanguage(chalk.ir.v1.QueryLanguage.QUERY_LANGUAGE_NONE)
                            .build())
                    .setDialectProfile(profileNamed(""))
                    // No `symbols` schema here to be a foreign key's parent (F14); this table's
                    // rows are not the point, an empty dialect on a local source is.
                    .addTables(TestCatalogs.bars().toBuilder().clearForeignKeys().build()))
            .build();

    assertThatCode(
            () ->
                client.registerCatalog(
                    RegisterCatalogRequest.newBuilder().setCatalog(localWithEmptyDialect).build()))
        .doesNotThrowAnyException();
  }

  @Test
  void postgres_the_alias_still_registers() {
    assertThatCode(() -> register(profileNamed("postgres"), "d256-alias")).doesNotThrowAnyException();
  }

  @Test
  void a_product_name_still_registers() {
    assertThatCode(() -> register(profileNamed("oracle"), "d256-product")).doesNotThrowAnyException();
  }

  private static PlanError decode(StatusRuntimeException error) {
    byte[] bytes = error.getTrailers() == null ? null : error.getTrailers().get(PlanErrors.TRAILER);
    assertThat(bytes).as("every failure carries a chalk-plan-error-bin trailer").isNotNull();
    try {
      return PlanError.parseFrom(bytes);
    } catch (InvalidProtocolBufferException e) {
      throw new AssertionError("the trailer is not a PlanError", e);
    }
  }

  /**
   * The Unix domain socket transport of docs/design/09-unix-socket-transport.md §3, over a real
   * server and a real Netty client on the NIO domain-socket channel — the transport is the thing
   * under test, so an in-process channel would prove nothing.
   */
  @Nested
  class UnixSocketTransport {
    private Path directory;

    /**
     * Under {@code /tmp}, not the platform temp directory: macOS's is
     * {@code /var/folders/…/T/} and a socket path has about 100 bytes to live in.
     */
    @BeforeEach
    void createDirectory() throws IOException {
      Path root = Path.of("/tmp");
      directory =
          Files.createTempDirectory(Files.isDirectory(root) ? root : Path.of(System.getProperty("java.io.tmpdir")), "chalk-uds");
    }

    @AfterEach
    void removeDirectory() throws IOException {
      try (var walk = Files.walk(directory)) {
        for (Path path : walk.sorted(Comparator.reverseOrder()).toList()) {
          Files.deleteIfExists(path);
        }
      }
    }

    @Test
    void a_catalog_registers_and_a_plan_comes_back_over_a_socket() throws IOException {
      Path socket = directory.resolve("planner.sock");
      ByteArrayOutputStream stdout = new ByteArrayOutputStream();

      try (PlannerServer server = startOn(socket, stdout)) {
        assertThat(stdout.toString(StandardCharsets.UTF_8).lines().toList())
            .as("the one line a launcher parses")
            .containsExactly("chalk-planner listening on unix:" + socket);
        assertThat(server.port()).isEqualTo(-1);
        assertThat(server.socketFile()).isEqualTo(socket);

        withClient(
            socket,
            stub -> {
              stub.registerCatalog(
                  RegisterCatalogRequest.newBuilder().setCatalog(TestCatalogs.corpus()).build());
              PlanResponse response =
                  stub.plan(
                      PlanRequest.newBuilder()
                          .setSql("SELECT symbol, ts FROM bars")
                          .setContextId(TestCatalogs.CONTEXT_ID)
                          .setCatalogEpoch(TestCatalogs.EPOCH)
                          .setClientIrVersion(IrVersion.CURRENT)
                          .setOptions(
                              PlannerOptions.newBuilder().setPushdown(PushdownLevel.PUSHDOWN_LEVEL_FULL))
                          .build());

              assertThat(response.getPlan().getPlanDigest()).isNotZero();
              assertThat(response.getPlan().getContextId()).isEqualTo(TestCatalogs.CONTEXT_ID);
            });
      }
    }

    @Test
    void the_socket_file_is_owner_only_while_the_server_runs_and_gone_after_it_stops()
        throws IOException {
      Path socket = directory.resolve("planner.sock");

      try (PlannerServer server = startOn(socket, new ByteArrayOutputStream())) {
        assertThat(server.socketFile()).isEqualTo(socket);
        assertThat(Files.exists(socket)).isTrue();
        assertThat(Files.getPosixFilePermissions(socket))
            .containsExactlyInAnyOrderElementsOf(
                java.nio.file.attribute.PosixFilePermissions.fromString("rw-------"));
      }

      assertThat(Files.exists(socket, java.nio.file.LinkOption.NOFOLLOW_LINKS)).isFalse();
    }

    @Test
    void a_socket_file_nobody_is_listening_on_is_unlinked_and_rebound() throws IOException {
      Path socket = directory.resolve("planner.sock");
      // Exactly what a SIGKILLed planner leaves behind: the JDK does not unlink on close.
      try (ServerSocketChannel dead = ServerSocketChannel.open(StandardProtocolFamily.UNIX)) {
        dead.bind(UnixDomainSocketAddress.of(socket));
      }
      assertThat(Files.exists(socket, java.nio.file.LinkOption.NOFOLLOW_LINKS)).isTrue();

      try (PlannerServer server = startOn(socket, new ByteArrayOutputStream())) {
        assertThat(server.socketFile()).isEqualTo(socket);
        withClient(socket, stub -> assertThat(stub.getInfo(GetInfoRequest.getDefaultInstance()).getMaxIrVersion())
            .isEqualTo(IrVersion.CURRENT));
      }
    }

    @Test
    void a_socket_a_live_server_owns_is_refused() throws IOException {
      Path socket = directory.resolve("planner.sock");

      try (PlannerServer first = startOn(socket, new ByteArrayOutputStream())) {
        assertThat(first.socketFile()).isEqualTo(socket);
        assertThatThrownBy(() -> startOn(socket, new ByteArrayOutputStream()))
            .isInstanceOf(IOException.class)
            .hasMessage("another chalk-planner is listening on " + socket);

        // And the first server still owns it.
        assertThat(Files.exists(socket)).isTrue();
      }
    }

    @Test
    void something_that_is_not_a_socket_is_never_unlinked() throws IOException {
      Path file = directory.resolve("precious.txt");
      Files.writeString(file, "not a socket");

      assertThatThrownBy(() -> startOn(file, new ByteArrayOutputStream()))
          .isInstanceOf(IOException.class)
          .hasMessage(file + " already exists and is not a socket");
      assertThat(Files.readString(file)).isEqualTo("not a socket");
    }

    @Test
    void a_path_longer_than_the_platform_allows_is_rejected_by_name() {
      String tooLong = "/tmp/" + "x".repeat(PlannerConfig.MAX_SOCKET_PATH_BYTES) + ".sock";

      assertThatThrownBy(() -> PlannerConfig.parse(new String[] {"--socket", tooLong}))
          .isInstanceOf(IllegalArgumentException.class)
          .hasMessageContaining(String.valueOf(PlannerConfig.MAX_SOCKET_PATH_BYTES))
          .hasMessageContaining("sockaddr_un");
    }

    @Test
    void socket_and_port_are_different_transports_and_asking_for_both_is_an_error() {
      assertThatThrownBy(
              () -> PlannerConfig.parse(new String[] {"--socket", "/tmp/p.sock", "--port", "0"}))
          .isInstanceOf(IllegalArgumentException.class)
          .hasMessageContaining("--socket and --host/--port");

      assertThatThrownBy(
              () -> PlannerConfig.parse(new String[] {"--host", "0.0.0.0", "--socket", "/tmp/p.sock"}))
          .isInstanceOf(IllegalArgumentException.class)
          .hasMessageContaining("--socket and --host/--port");

      // On its own it is fine, and it turns off TCP.
      PlannerConfig config = PlannerConfig.parse(new String[] {"--socket", "/tmp/p.sock"});
      assertThat(config.socketPath()).isEqualTo("/tmp/p.sock");
    }

    /**
     * The container image exports {@code CHALK_PLANNER_HOST}/{@code PORT}, so "both together are an
     * error" has to mean "both at the same level"; otherwise {@code --socket} could never be given to
     * the image at all, which §6 says it can be with a mounted volume.
     */
    @Test
    void the_command_line_settles_the_transport_when_the_environment_names_the_other_one() {
      Map<String, String> containerEnv =
          Map.of("CHALK_PLANNER_HOST", "0.0.0.0", "CHALK_PLANNER_PORT", "7433");

      assertThat(PlannerConfig.parse(new String[] {"--socket", "/tmp/p.sock"}, containerEnv).socketPath())
          .isEqualTo("/tmp/p.sock");

      Map<String, String> socketEnv = Map.of("CHALK_PLANNER_SOCKET", "/tmp/p.sock");
      assertThat(PlannerConfig.parse(new String[] {}, socketEnv).socketPath()).isEqualTo("/tmp/p.sock");
      assertThat(PlannerConfig.parse(new String[] {"--port", "0"}, socketEnv).socketPath()).isNull();

      // Two environment variables asking for two transports is nobody's precedence question.
      assertThatThrownBy(
              () ->
                  PlannerConfig.parse(
                      new String[] {}, Map.of("CHALK_PLANNER_SOCKET", "/tmp/p.sock", "CHALK_PLANNER_PORT", "1")))
          .isInstanceOf(IllegalArgumentException.class)
          .hasMessageContaining("CHALK_PLANNER_SOCKET and CHALK_PLANNER_HOST/CHALK_PLANNER_PORT");
    }

    private PlannerServer startOn(Path socket, ByteArrayOutputStream stdout) throws IOException {
      PrintStream original = System.out;
      System.setOut(new PrintStream(stdout, true, StandardCharsets.UTF_8));
      try {
        return PlannerServer.start(PlannerConfig.parse(new String[] {"--socket", socket.toString()}));
      } finally {
        System.setOut(original);
      }
    }

    private void withClient(
        Path socket, java.util.function.Consumer<PlannerServiceGrpc.PlannerServiceBlockingStub> body) {
      EventLoopGroup group = new MultiThreadIoEventLoopGroup(1, NioIoHandler.newFactory());
      ManagedChannel unixChannel =
          NettyChannelBuilder.forAddress(UnixDomainSocketAddress.of(socket))
              .channelType(NioDomainSocketChannel.class, UnixDomainSocketAddress.class)
              .eventLoopGroup(group)
              .usePlaintext()
              .build();
      try {
        body.accept(PlannerServiceGrpc.newBlockingStub(unixChannel));
      } finally {
        unixChannel.shutdownNow();
        group.shutdownGracefully(0, 1, TimeUnit.SECONDS);
      }
    }
  }
}

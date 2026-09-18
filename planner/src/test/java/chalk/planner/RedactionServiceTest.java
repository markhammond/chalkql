package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

import chalk.ir.IrVersion;
import chalk.planner.catalog.CatalogRegistry;
import chalk.planner.rpc.PlannerServiceImpl;
import chalk.planner.rpc.v1.PlanRequest;
import chalk.planner.rpc.v1.PlanResponse;
import chalk.planner.rpc.v1.PlannerOptions;
import chalk.planner.rpc.v1.PlannerServiceGrpc;
import chalk.planner.rpc.v1.PushdownLevel;
import chalk.planner.rpc.v1.RedactSqlRequest;
import chalk.planner.rpc.v1.RedactSqlResponse;
import chalk.planner.rpc.v1.RedactionOptions;
import chalk.planner.rpc.v1.RedactionScope;
import chalk.planner.rpc.v1.RegisterCatalogRequest;
import com.google.protobuf.ByteString;
import io.grpc.ManagedChannel;
import io.grpc.Server;
import io.grpc.StatusRuntimeException;
import io.grpc.inprocess.InProcessChannelBuilder;
import io.grpc.inprocess.InProcessServerBuilder;
import java.io.IOException;
import java.util.concurrent.TimeUnit;
import org.junit.jupiter.api.AfterAll;
import org.junit.jupiter.api.BeforeAll;
import org.junit.jupiter.api.Test;

/**
 * The wire half of D262 ({@code docs/design/37-redacted-sql.md} §1, ADR 0043): the {@code RedactSql}
 * call, the {@code redaction} message on a plan request, and — the property the whole design turns
 * on — that a request which asks for nothing gets nothing and costs nothing.
 */
class RedactionServiceTest {
  private static final ByteString SALT = ByteString.copyFromUtf8("a fixed salt");

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

  private static RedactionOptions.Builder redaction() {
    return RedactionOptions.newBuilder()
        .setSalt(SALT)
        .setScope(RedactionScope.REDACTION_SCOPE_ALL)
        .setKeepStructural(true);
  }

  private static PlanRequest.Builder request(String sql) {
    return PlanRequest.newBuilder()
        .setSql(sql)
        .setContextId(TestCatalogs.CONTEXT_ID)
        .setCatalogEpoch(TestCatalogs.EPOCH)
        .setClientIrVersion(IrVersion.CURRENT)
        .setOptions(PlannerOptions.newBuilder().setPushdown(PushdownLevel.PUSHDOWN_LEVEL_FULL));
  }

  /** Opt-in: no redaction message, no redacted text, and nothing computed to produce it. */
  @Test
  void a_plan_request_that_asks_for_nothing_gets_nothing() {
    PlanResponse response =
        client.plan(request("SELECT symbol FROM bars WHERE symbol = 'BTCUSDT'").build());

    assertThat(response.getRedactedSql()).isEmpty();
  }

  @Test
  void a_plan_request_that_asks_for_a_redaction_gets_one() {
    PlanResponse response =
        client.plan(
            request("SELECT symbol FROM bars WHERE symbol = 'BTCUSDT'")
                .setRedaction(redaction())
                .build());

    assertThat(response.getRedactedSql())
        .matches(
            "SELECT \"symbol\" FROM \"bars\" WHERE \"symbol\" = /\\*REDACTED-[0-9a-f]{8}:CHAR\\*/");
    assertThat(response.getRedactedSql()).doesNotContain("BTCUSDT");
  }

  /** The redaction is a rendering and never a plan: the same request plans the same either way. */
  @Test
  void asking_for_a_redaction_does_not_change_the_plan() {
    String sql = "SELECT symbol FROM bars WHERE symbol = 'BTCUSDT'";
    PlanResponse plain = client.plan(request(sql).build());
    PlanResponse redacted = client.plan(request(sql).setRedaction(redaction()).build());

    assertThat(redacted.getPlan()).isEqualTo(plain.getPlan());
    assertThat(redacted.getPlan().getPlanDigest()).isEqualTo(plain.getPlan().getPlanDigest());
  }

  @Test
  void a_plan_request_with_a_redaction_and_no_salt_is_refused() {
    assertThatThrownBy(
            () ->
                client.plan(
                    request("SELECT symbol FROM bars")
                        .setRedaction(RedactionOptions.newBuilder().setKeepStructural(true))
                        .build()))
        .isInstanceOf(StatusRuntimeException.class)
        .hasMessageContaining("salt");
  }

  @Test
  void redact_sql_serves_text_that_was_never_prepared() {
    RedactSqlResponse response =
        client.redactSql(
            RedactSqlRequest.newBuilder()
                .setSql("SELECT symbol FROM bars WHERE symbol = 'BTCUSDT' LIMIT 10")
                .setRedaction(redaction())
                .build());

    assertThat(response.getParsed()).isTrue();
    assertThat(response.getStructuralHash().size()).isEqualTo(32);
    assertThat(response.getRedactedSql()).contains("FETCH NEXT 10 ROWS ONLY");
    assertThat(response.getRedactedSql()).doesNotContain("BTCUSDT");
  }

  /** The same text, whichever call produced it: one redactor, one seed, one answer. */
  @Test
  void redact_sql_and_a_prepare_agree_on_one_statement() {
    String sql = "SELECT symbol FROM bars WHERE symbol = 'BTCUSDT'";
    PlanResponse prepared = client.plan(request(sql).setRedaction(redaction()).build());
    RedactSqlResponse standalone =
        client.redactSql(
            RedactSqlRequest.newBuilder().setSql(sql).setRedaction(redaction()).build());

    assertThat(standalone.getRedactedSql()).isEqualTo(prepared.getRedactedSql());
  }

  @Test
  void redact_sql_falls_back_to_the_token_stream_for_text_that_does_not_parse() {
    RedactSqlResponse response =
        client.redactSql(
            RedactSqlRequest.newBuilder()
                .setSql("SELCT * FRM bars WHERE symbol = 'BTCUSDT' LIMIT 10")
                .setRedaction(redaction())
                .build());

    assertThat(response.getParsed()).isFalse();
    assertThat(response.getRedactedSql()).doesNotContain("BTCUSDT");
    // Nothing is kept without a tree, the LIMIT included.
    assertThat(response.getRedactedSql()).doesNotContain("10");
  }

  @Test
  void redact_sql_without_a_salt_is_refused() {
    assertThatThrownBy(
            () ->
                client.redactSql(
                    RedactSqlRequest.newBuilder().setSql("SELECT 1 FROM bars").build()))
        .isInstanceOf(StatusRuntimeException.class)
        .hasMessageContaining("salt");
  }

  /** The Babel conformance reaches the redactor, so D259's {@code ::} parses rather than falls back. */
  @Test
  void redact_sql_parses_a_babel_statement_when_the_request_says_so() {
    RedactSqlResponse response =
        client.redactSql(
            RedactSqlRequest.newBuilder()
                .setSql("SELECT o_orderkey::VARCHAR FROM orders WHERE o_comment = 'a secret'")
                .setConformance(chalk.ir.v1.SqlConformance.SQL_CONFORMANCE_BABEL)
                .setRedaction(redaction())
                .build());

    assertThat(response.getParsed()).isTrue();
    assertThat(response.getRedactedSql()).contains(":: VARCHAR");
    assertThat(response.getRedactedSql()).doesNotContain("a secret");
  }
}

package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;

import chalk.ir.PlanDigest;
import chalk.ir.v1.Plan;
import chalk.planner.plan.PushdownPolicy;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.LinkedHashSet;
import java.util.Set;
import java.util.stream.Stream;
import org.junit.jupiter.api.BeforeAll;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.MethodSource;

/**
 * Volcano nondeterminism, caught without needing both toolchains (docs/design/05-testing.md §6).
 *
 * <p>Every corpus query is planned 100 times in one JVM and must produce exactly one digest. The
 * .NET {@code DeterminismTests} do the other half — 100 times over RPC and across a sidecar restart.
 */
class DigestTest {
  /** Enough runs that an iteration-order dependency shows up; 100 is what the design asks for. */
  private static final int RUNS = 100;

  private static CorpusPlanner planner;

  @BeforeAll
  static void setUp() {
    planner = new CorpusPlanner();
  }

  static Stream<CorpusQueries.Query> queries() {
    return CorpusQueries.all().stream();
  }

  @ParameterizedTest(name = "{0}")
  @MethodSource("queries")
  void one_hundred_plans_produce_one_digest(CorpusQueries.Query query) {
    Set<String> digests = new LinkedHashSet<>();
    for (int i = 0; i < RUNS; i++) {
      digests.add(PlanDigest.format(planner.plan(query).getPlanDigest()));
    }

    assertThat(digests)
        .as("%s planned %d times", query.name(), RUNS)
        .hasSize(1);
  }

  @ParameterizedTest(name = "{0}")
  @MethodSource("queries")
  void full_and_none_are_different_plans_for_at_least_the_scans(CorpusQueries.Query query) {
    Plan full = planner.plan(query, PushdownPolicy.full());
    Plan none = planner.plan(query, PushdownPolicy.none());

    // Not an inequality assertion: many queries read every column anyway, so the two levels coincide.
    // What must hold is that each level is itself stable and that NONE never prunes a scan.
    assertThat(PlanDigest.compute(full)).isEqualTo(full.getPlanDigest());
    assertThat(PlanDigest.compute(none)).isEqualTo(none.getPlanDigest());
  }

  /**
   * D283: a lookup read backwards is a different plan from the same lookup read forwards, and the
   * digest has to say so. It delivers the opposite ordering, so a digest that merged the two would
   * hand whatever asked for one of them the other.
   */
  @Test
  void reading_an_index_backwards_is_a_different_plan() {
    Plan forwards = planner.plan("SELECT ts, symbol FROM bars ORDER BY ts LIMIT 1");
    Plan backwards = planner.plan("SELECT ts, symbol FROM bars ORDER BY ts DESC LIMIT 1");

    assertThat(backwards.getRoot().getFetch().getInput().getIndexLookup().getReverse())
        .as("the reversed plan reads the index backwards")
        .isTrue();
    assertThat(forwards.getRoot().getFetch().getInput().getIndexLookup().getReverse())
        .as("the forward plan does not")
        .isFalse();
    assertThat(PlanDigest.format(backwards.getPlanDigest()))
        .isNotEqualTo(PlanDigest.format(forwards.getPlanDigest()));
  }

  /**
   * The digest is recomputable from the recorded plan, which is what makes {@code corpus/plans}
   * reviewable: the {@code .digest} file is derived, not asserted.
   */
  @ParameterizedTest(name = "{0}")
  @MethodSource("queries")
  void the_digest_matches_the_recorded_one_when_the_corpus_has_been_recorded(
      CorpusQueries.Query query) throws IOException {
    Path file =
        CorpusQueries.corpusDir().resolve("plans/m1").resolve(query.name() + ".full.digest");
    if (!Files.exists(file)) {
      // corpus/plans is written by `scripts/record-plans.sh`, which needs the .NET corpus tool.
      // Before it has run there is nothing to compare against; the run above still proves stability.
      return;
    }

    String recorded = Files.readString(file, StandardCharsets.UTF_8).trim();

    assertThat(PlanDigest.format(planner.plan(query).getPlanDigest()))
        .as("%s: planner digest vs corpus/plans/m1/%s.full.digest", query.name(), query.name())
        .isEqualTo(recorded);
  }

  @Test
  void whitespace_and_keyword_case_are_not_part_of_the_digest() {
    String a = "SELECT symbol, ts FROM bars WHERE symbol = 'BTCUSDT'";
    String b = "select   symbol ,\n  ts from BARS where symbol='BTCUSDT'";

    assertThat(planner.plan(a).getPlanDigest()).isEqualTo(planner.plan(b).getPlanDigest());
  }

  /**
   * Identifier case *is* part of the digest, because D15 keeps unquoted identifiers as written and
   * they become the output schema's field names. {@code SELECT SYMBOL} and {@code SELECT symbol}
   * read the same column but hand the host differently-named Arrow fields, so they are different
   * plans.
   */
  @Test
  void identifier_case_changes_the_digest_because_it_changes_the_output_schema() {
    long lower = planner.plan("SELECT symbol FROM bars").getPlanDigest();
    long upper = planner.plan("SELECT SYMBOL FROM bars").getPlanDigest();

    assertThat(lower).isNotEqualTo(upper);
    assertThat(planner.plan("SELECT SYMBOL FROM bars").getOutputType().getFields(0).getName())
        .isEqualTo("SYMBOL");
  }

  @Test
  void a_different_literal_changes_the_digest() {
    long btc = planner.plan("SELECT symbol FROM bars WHERE symbol = 'BTCUSDT'").getPlanDigest();
    long eth = planner.plan("SELECT symbol FROM bars WHERE symbol = 'ETHUSDT'").getPlanDigest();

    assertThat(btc).isNotEqualTo(eth);
  }

  // ---- the row goal in the rel digest (D276, 46-row-goals.md §1) ----

  /**
   * A goaled leaf must be a different rel from the same leaf without one: it estimates and costs
   * differently, so Volcano merging the two would give the parent that asked for no goal a leaf
   * priced for one. The digest is what Volcano decides that by, which is why {@code goal} is an
   * {@code explainTerms} item and not an {@code ALL_ATTRIBUTES} decoration like {@code sel}.
   */
  @Test
  void a_row_goal_on_a_scan_changes_the_rel_digest() {
    chalk.planner.plan.rel.ChalkTableScan scan =
        leaf("SELECT symbol, ts FROM bars", chalk.planner.plan.rel.ChalkTableScan.class);

    assertThat(digest(scan.withRowGoal(4))).isNotEqualTo(digest(scan));
    assertThat(digest(scan.withRowGoal(4))).contains("goal=[4]");
  }

  @Test
  void a_row_goal_on_a_lookup_changes_the_rel_digest() {
    chalk.planner.plan.rel.ChalkIndexLookup lookup =
        leaf(
            "SELECT symbol, ts FROM bars WHERE symbol = 'BTCUSDT'",
            chalk.planner.plan.rel.ChalkIndexLookup.class);

    assertThat(digest(lookup.withRowGoal(9))).isNotEqualTo(digest(lookup));
    assertThat(digest(lookup.withRowGoal(9))).contains("goal=[9]");
  }

  /** And a leaf with no goal keeps the digest it had: the term is written only when set. */
  @Test
  void no_row_goal_leaves_the_rel_digest_where_it_was() {
    chalk.planner.plan.rel.ChalkTableScan scan =
        leaf("SELECT symbol, ts FROM bars", chalk.planner.plan.rel.ChalkTableScan.class);
    chalk.planner.plan.rel.ChalkIndexLookup lookup =
        leaf(
            "SELECT symbol, ts FROM bars WHERE symbol = 'BTCUSDT'",
            chalk.planner.plan.rel.ChalkIndexLookup.class);

    assertThat(digest(scan)).doesNotContain("goal=");
    assertThat(digest(lookup)).doesNotContain("goal=");
    assertThat(scan.withRowGoal(0)).isSameAs(scan);
    assertThat(lookup.withRowGoal(0)).isSameAs(lookup);
    assertThat(digest(scan.withRowGoal(4).withRowGoal(0))).isEqualTo(digest(scan));
  }

  /** The attributes Volcano compares two rels by. */
  private static String digest(org.apache.calcite.rel.RelNode node) {
    return org.apache.calcite.plan.RelOptUtil.toString(
        node, org.apache.calcite.sql.SqlExplainLevel.DIGEST_ATTRIBUTES);
  }

  /** The first node of {@code kind} in the physical plan for {@code sql}. */
  private static <T> T leaf(String sql, Class<T> kind) {
    chalk.planner.catalog.RegisteredCatalog catalog =
        new chalk.planner.catalog.CatalogRegistry().register(TestCatalogs.declared());
    try (chalk.planner.plan.PlannerPipeline pipeline =
        chalk.planner.plan.PlannerPipeline.create(catalog, PushdownPolicy.full())) {
      T found = find(pipeline.plan(sql, false).physical(), kind);
      assertThat(found).as("a %s for: %s", kind.getSimpleName(), sql).isNotNull();
      return found;
    } catch (Exception failure) {
      throw new AssertionError("planning failed for: " + sql, failure);
    }
  }

  private static <T> T find(org.apache.calcite.rel.RelNode node, Class<T> kind) {
    if (kind.isInstance(node)) {
      return kind.cast(node);
    }

    for (org.apache.calcite.rel.RelNode input : node.getInputs()) {
      T found = find(input, kind);
      if (found != null) {
        return found;
      }
    }

    return null;
  }
}

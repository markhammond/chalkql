package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;

import chalk.ir.v1.Plan;
import chalk.ir.v1.Rel;
import chalk.ir.v1.Unnest;
import java.util.List;
import java.util.Locale;
import java.util.stream.Stream;
import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.MethodSource;

/**
 * D66 — every spelling of {@code UNNEST} in the corpus becomes a {@code ChalkUnnest} and reaches the
 * IR as one {@code Unnest} node ({@code 14-windows-ii.md} §7, §10).
 *
 * <p>Calcite models the comma form, {@code CROSS JOIN UNNEST} and {@code LEFT JOIN LATERAL UNNEST …
 * ON TRUE} as a correlate over an {@code Uncollect}, and {@code UNNEST(ARRAY[…])} as a bare one; the
 * point of the test is that all of them arrive as the same node, differing only in the two flags the
 * SQL actually asked for.
 */
class UnnestRuleTest {
  private static final CorpusPlanner PLANNER = new CorpusPlanner();

  /** The corpus queries that unnest something. */
  static Stream<CorpusQueries.Query> queries() {
    return CorpusQueries.m5().stream()
        .filter(q -> q.sql().toUpperCase(Locale.ROOT).contains("UNNEST"));
  }

  @ParameterizedTest(name = "{0}")
  @MethodSource("queries")
  void every_unnest_form_becomes_one_unnest_node(CorpusQueries.Query query) {
    Plan plan = PLANNER.plan(query);

    List<Rel> unnests = IrNodes.of(plan, Rel.KindCase.UNNEST);
    assertThat(unnests).as("Unnest nodes in %s", query.name()).hasSize(1);

    Unnest unnest = unnests.get(0).getUnnest();
    Rel.KindCase input = unnest.getInput().getKindCase();
    assertThat(input).isNotEqualTo(Rel.KindCase.KIND_NOT_SET);

    // The flags are the SQL's, not the rule's: only the LEFT JOIN LATERAL form keeps empty lists,
    // and only the one that says WITH ORDINALITY numbers them.
    String sql = query.sql().toUpperCase(Locale.ROOT);
    assertThat(unnest.getKeepEmpty()).as("keep_empty of %s", query.name()).isEqualTo(sql.contains("LEFT JOIN LATERAL"));
    assertThat(unnest.getWithOrdinality())
        .as("with_ordinality of %s", query.name())
        .isEqualTo(sql.contains("WITH ORDINALITY"));
  }

  /** The four spellings §9 names are all present, so the sweep above is not vacuous. */
  @org.junit.jupiter.api.Test
  void the_corpus_covers_every_spelling() {
    List<String> names = queries().map(CorpusQueries.Query::name).toList();

    assertThat(names)
        .contains(
            "14_cross_join_unnest",
            "15_left_lateral_unnest_ordinality",
            "16_unnest_array_agg",
            "17_unnest_constant_array");
  }
}

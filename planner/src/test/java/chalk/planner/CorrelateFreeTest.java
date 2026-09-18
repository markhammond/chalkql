package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;

import chalk.ir.v1.Plan;
import java.util.stream.Stream;
import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.MethodSource;

/**
 * No {@code Correlate} reaches {@code RelToIr} for any corpus query ({@code 12-joins.md} §7).
 *
 * <p>The IR has no correlated node, so a {@code Correlate} that survived decorrelation would leave
 * {@code RelToIr} throwing {@code UNSUPPORTED} naming the class. Planning every corpus query all the
 * way to IR is therefore the assertion: if one got through, this fails with the rel that did it.
 */
class CorrelateFreeTest {
  private static final CorpusPlanner PLANNER = new CorpusPlanner();

  static Stream<CorpusQueries.Query> queries() {
    return Stream.of(
            CorpusQueries.all(),
            CorpusQueries.m2(),
            CorpusQueries.m3(),
            CorpusQueries.m4(),
            CorpusQueries.m5())
        .flatMap(java.util.List::stream);
  }

  @ParameterizedTest(name = "{0}")
  @MethodSource("queries")
  void every_corpus_query_reaches_the_ir_without_a_correlate(CorpusQueries.Query query) {
    Plan plan = PLANNER.plan(query);

    assertThat(plan.getRoot().getKindCase()).isNotEqualTo(chalk.ir.v1.Rel.KindCase.KIND_NOT_SET);
  }
}

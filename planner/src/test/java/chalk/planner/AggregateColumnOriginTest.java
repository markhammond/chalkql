package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;

import chalk.planner.catalog.CatalogRegistry;
import chalk.planner.plan.ChalkRelMetadata;
import chalk.planner.plan.PlannerPipeline;
import chalk.planner.plan.PushdownPolicy;
import java.util.List;
import java.util.Set;
import org.apache.calcite.plan.hep.HepPlanner;
import org.apache.calcite.plan.hep.HepProgramBuilder;
import org.apache.calcite.rel.RelNode;
import org.apache.calcite.rel.core.Aggregate;
import org.apache.calcite.rel.metadata.RelColumnOrigin;
import org.apache.calcite.rel.metadata.RelMetadataQuery;
import org.apache.calcite.rel.rules.CoreRules;
import org.junit.jupiter.api.Test;

/**
 * CALCITE-4250's shape, and Chalk's own answer to it
 * (docs/design/calcite-open-issues-assessment.md; docs/design/16-entitlements.md §3.10).
 *
 * <p>An aggregate's group key is the input column its <em>group set</em> names. Reading the origin of
 * the output position instead is right only while the group set is a prefix of the input row, and
 * {@code AGGREGATE_PROJECT_MERGE} exists to make it something else: it removes the projection under
 * the aggregate and renumbers the group set to the columns of what is left. The reported symptom is
 * an origin naming a different column of the same table — "the correct column index is 7, but got
 * 5" — and for the taint check that means asking whether the wrong column is population-only or
 * withheld.
 *
 * <p>The statement below groups by <b>the fourth column of the table</b> through a projection that
 * puts it first, and merges the two. The origin must still be that fourth column.
 */
class AggregateColumnOriginTest {

  @Test
  void an_aggregates_group_key_names_the_column_its_group_set_does() throws Exception {
    try (PlannerPipeline pipeline =
        PlannerPipeline.create(
            new CatalogRegistry().register(TestCatalogs.corpus()), PushdownPolicy.full())) {
      RelNode logical =
          pipeline.logical(
              "SELECT o_orderstatus, COUNT(*) FROM (SELECT o_orderstatus, o_custkey FROM orders) t"
                  + " GROUP BY o_orderstatus");

      // The rule the issue is about: the projection goes and the group set is renumbered.
      HepPlanner hep =
          new HepPlanner(
              new HepProgramBuilder().addRuleInstance(CoreRules.AGGREGATE_PROJECT_MERGE).build());
      hep.setRoot(logical);
      RelNode merged = hep.findBestExp();
      merged.getCluster().setMetadataProvider(ChalkRelMetadata.SOURCE);
      merged.getCluster().invalidateMetadataQuery();

      Aggregate aggregate = aggregateOf(merged);
      assertThat(aggregate).as("the aggregate survived the merge:%n%s", merged).isNotNull();
      assertThat(aggregate.getInput()).isNotInstanceOf(org.apache.calcite.rel.core.Project.class);

      RelMetadataQuery mq = merged.getCluster().getMetadataQuery();
      Set<RelColumnOrigin> origins = mq.getColumnOrigins(aggregate, 0);
      assertThat(origins).hasSize(1);

      RelColumnOrigin origin = origins.iterator().next();
      List<String> columns = origin.getOriginTable().getRowType().getFieldNames();
      assertThat(columns.get(origin.getOriginColumnOrdinal()))
          .as("the group key is o_orderstatus, whatever its output position is")
          .isEqualTo("o_orderstatus");
      assertThat(origin.isDerived()).isFalse();

      // And a measure is derived from its arguments: COUNT(*) takes none, so it has no origin at all.
      assertThat(mq.getColumnOrigins(aggregate, 1)).isEmpty();
    }
  }

  private static Aggregate aggregateOf(RelNode rel) {
    if (rel instanceof Aggregate aggregate) {
      return aggregate;
    }
    for (RelNode input : rel.getInputs()) {
      Aggregate found = aggregateOf(input);
      if (found != null) {
        return found;
      }
    }
    return null;
  }
}

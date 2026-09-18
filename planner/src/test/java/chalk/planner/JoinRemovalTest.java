package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;

import chalk.planner.catalog.CatalogRegistry;
import chalk.planner.catalog.RegisteredCatalog;
import chalk.planner.plan.PlannerPipeline;
import chalk.planner.plan.PushdownPolicy;
import org.junit.jupiter.api.BeforeAll;
import org.junit.jupiter.api.Test;

/**
 * F16(d): {@code PROJECT_JOIN_REMOVE} and {@code AGGREGATE_JOIN_REMOVE} are registered and reach the
 * catalog's declared unique keys. A join to a table nothing reads, on a key that table is unique in,
 * produces exactly the rows of the other side, so it is not a join at all.
 *
 * <p>No corpus query loses a join to these rules — every join the corpora write reads a column from
 * both sides — so this is where the rules are shown to work. Both read {@code areColumnsUnique} and
 * neither consults a referential constraint (verified in ADR 0019), so the outer join is what makes
 * the rewrite sound: an inner join to a unique key can still <em>drop</em> rows, and Calcite
 * correctly leaves it alone.
 */
class JoinRemovalTest {
  private static RegisteredCatalog catalog;

  @BeforeAll
  static void registerCatalog() {
    catalog = new CatalogRegistry().register(TestCatalogs.corpus());
  }

  /** A LEFT join whose right side contributes nothing and is unique on the key: gone. */
  @Test
  void a_left_join_to_an_unread_unique_key_is_removed() throws Exception {
    String plan =
        plan(
            "SELECT o.o_orderkey, o.o_totalprice FROM orders o"
                + " LEFT JOIN customer c ON o.o_custkey = c.c_custkey");

    assertThat(plan).doesNotContain("Join");
    assertThat(plan).doesNotContain("customer");
    assertThat(plan).contains("orders");
  }

  /** The same shape under an aggregate is the other rule's. */
  @Test
  void an_aggregate_over_a_left_join_to_an_unread_unique_key_is_removed() throws Exception {
    String plan =
        plan(
            "SELECT o.o_orderstatus, COUNT(*) FROM orders o"
                + " LEFT JOIN customer c ON o.o_custkey = c.c_custkey"
                + " GROUP BY o.o_orderstatus");

    assertThat(plan).doesNotContain("Join");
    assertThat(plan).doesNotContain("customer");
    assertThat(plan).contains("ChalkHashAggregate");
  }

  /** Reading one of the parent's columns is what keeps the join: nothing else can produce it. */
  @Test
  void a_join_whose_parent_column_is_read_stays() throws Exception {
    String plan =
        plan(
            "SELECT o.o_orderkey, c.c_name FROM orders o"
                + " LEFT JOIN customer c ON o.o_custkey = c.c_custkey");

    assertThat(plan).contains("Join");
    assertThat(plan).contains("customer");
  }

  /**
   * An INNER join to the same unique key is not removable, and must not be: a child row whose key
   * matches nothing would disappear. The declared foreign key says it cannot happen, but Calcite
   * 1.42's rules do not read referential constraints, so the join stays — which is the safe answer
   * either way.
   */
  @Test
  void an_inner_join_to_an_unread_unique_key_stays() throws Exception {
    String plan =
        plan(
            "SELECT o.o_orderkey, o.o_totalprice FROM orders o"
                + " JOIN customer c ON o.o_custkey = c.c_custkey");

    assertThat(plan).contains("Join");
  }

  private static String plan(String sql) throws Exception {
    try (PlannerPipeline pipeline = PlannerPipeline.create(catalog, PushdownPolicy.full())) {
      return pipeline.plan(sql, true).physicalPlanText();
    }
  }
}

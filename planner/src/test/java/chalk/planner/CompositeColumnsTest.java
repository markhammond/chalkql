package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

import chalk.ir.v1.CatalogContext;
import chalk.ir.v1.ColumnEntitlement;
import chalk.ir.v1.Disclosure;
import chalk.ir.v1.DisclosureRule;
import chalk.ir.v1.Expr;
import chalk.ir.v1.Plan;
import chalk.ir.v1.Rel;
import chalk.ir.v1.Table;
import chalk.ir.v1.TableCollation;
import chalk.ir.v1.TableEntitlement;
import chalk.ir.v1.TypeKind;
import chalk.ir.v1.UniqueKey;
import chalk.planner.catalog.InvalidCatalogException;
import chalk.planner.catalog.RegisteredCatalog;
import chalk.planner.ir.IrVersionGate;
import chalk.planner.ir.RelToIr;
import chalk.planner.plan.PlannerPipeline;
import chalk.planner.plan.PushdownPolicy;
import chalk.planner.types.TypeMapper;
import java.util.ArrayList;
import java.util.List;
import java.util.function.UnaryOperator;
import org.apache.calcite.rel.RelNode;
import org.junit.jupiter.api.Test;

/**
 * Composite columns in the planner (D302): a table of an in-process source may declare one, and it
 * is read through {@code Read.projection}, whole or by field, by {@code t.c.*} and by
 * {@code SELECT *}; a field filters and orders. No key, index, collation or covering set is over
 * one, no REMOTE source declares one, and a composite column is compared, sorted, grouped and made
 * distinct nowhere, each refused by name. A policy discloses one whole or withholds it whole, and a
 * rule's condition may read a field of it. The table is the corpus's {@code quotes}.
 */
final class CompositeColumnsTest {

  private static Plan plan(String sql) {
    RegisteredCatalog registered = RegisteredCatalog.of(TestCatalogs.corpus());
    try (PlannerPipeline pipeline = PlannerPipeline.create(registered, PushdownPolicy.full())) {
      PlannerPipeline.Result result = pipeline.plan(sql, true);
      RelNode physical = result.physical();
      RelToIr converter =
          new RelToIr(
              new TypeMapper(physical.getCluster().getTypeFactory()),
              physical.getCluster().getRexBuilder(),
              physical.getCluster().getMetadataQuery(),
              IrVersionGate.current());
      return converter.toPlan(
          physical, result.parameterRowType(), registered.contextId(), registered.epoch());
    } catch (RuntimeException e) {
      throw e;
    } catch (Exception e) {
      throw new IllegalStateException(e);
    }
  }

  private static List<Expr> exprs(Object value, List<Expr> into) {
    if (value instanceof Expr expr) {
      into.add(expr);
    }
    if (value instanceof com.google.protobuf.Message message) {
      for (Object child : message.getAllFields().values()) {
        exprs(child, into);
      }
    } else if (value instanceof List<?> list) {
      for (Object item : list) {
        exprs(item, into);
      }
    }
    return into;
  }

  private static long fieldAccesses(Plan plan) {
    return exprs(plan.getRoot(), new ArrayList<>()).stream()
        .filter(e -> e.getKindCase() == Expr.KindCase.FIELD_ACCESS)
        .count();
  }

  private static List<Rel> reads(Rel rel, List<Rel> into) {
    if (rel.getKindCase() == Rel.KindCase.READ || rel.getKindCase() == Rel.KindCase.INDEX_LOOKUP) {
      into.add(rel);
    }
    for (Object child : rel.getAllFields().values()) {
      if (child instanceof Rel input) {
        reads(input, into);
      } else if (child instanceof com.google.protobuf.Message message) {
        for (Object grandchild : message.getAllFields().values()) {
          if (grandchild instanceof Rel input) {
            reads(input, into);
          } else if (grandchild instanceof List<?> list) {
            list.stream().filter(Rel.class::isInstance).map(Rel.class::cast).forEach(r -> reads(r, into));
          }
        }
      }
    }
    return into;
  }

  // ---- reading ----

  @Test
  void select_star_carries_each_composite_column_whole_through_the_read() {
    Plan plan = plan("SELECT * FROM quotes ORDER BY id");

    assertThat(plan.getOutputType().getFieldsList())
        .extracting(f -> f.getName() + ":" + f.getType().getKind())
        .containsExactly(
            "id:TYPE_KIND_I64",
            "symbol:TYPE_KIND_STRING",
            "ts:TYPE_KIND_TIMESTAMP",
            "bid:TYPE_KIND_COMPOSITE",
            "ask:TYPE_KIND_COMPOSITE",
            "venue:TYPE_KIND_COMPOSITE");
    assertThat(plan.getOutputType().getFields(4).getType().getNullable()).isTrue();
    assertThat(fieldAccesses(plan)).isZero();
    Rel read = reads(plan.getRoot(), new ArrayList<>()).get(0);
    assertThat(read.getRead().getProjectionList()).containsExactly(0, 1, 2, 3, 4, 5);
  }

  @Test
  void a_field_is_a_field_access_over_the_column_the_read_projects() {
    Plan plan =
        plan("SELECT q.id, q.ask.price AS ask, q.venue.country AS country FROM quotes q ORDER BY q.id");

    assertThat(fieldAccesses(plan)).isEqualTo(2);
    assertThat(plan.getOutputType().getFields(1).getType().getKind()).isEqualTo(TypeKind.TYPE_KIND_FP64);
    // A field of a nullable composite is nullable.
    assertThat(plan.getOutputType().getFields(1).getType().getNullable()).isTrue();
    Rel read = reads(plan.getRoot(), new ArrayList<>()).get(0);
    assertThat(read.getRead().getProjectionList()).containsExactly(0, 4, 5);
  }

  @Test
  void t_c_star_expands_the_fields_and_a_field_filters_and_orders() {
    assertThat(fieldAccesses(plan("SELECT q.id, q.bid.* FROM quotes q ORDER BY q.id"))).isEqualTo(2);

    Plan filtered =
        plan("SELECT q.id FROM quotes q WHERE q.bid.price > 400 AND q.ask IS NOT NULL ORDER BY q.id");
    assertThat(fieldAccesses(filtered)).isEqualTo(1);

    Plan ordered = plan("SELECT q.id, q.ask FROM quotes q ORDER BY q.ask.price DESC, q.id");
    assertThat(ordered.getOutputType().getFields(1).getType().getKind())
        .isEqualTo(TypeKind.TYPE_KIND_COMPOSITE);
  }

  @Test
  void a_composite_column_is_compared_sorted_grouped_and_made_distinct_nowhere() {
    assertThatThrownBy(() -> plan("SELECT id FROM quotes ORDER BY bid"))
        .isInstanceOf(UnsupportedFeatureException.class)
        .hasMessageContaining("ORDER BY a composite value (bid)");
    assertThatThrownBy(() -> plan("SELECT bid FROM quotes ORDER BY bid"))
        .isInstanceOf(UnsupportedFeatureException.class)
        .hasMessageContaining("ORDER BY a composite value (bid)");
    assertThatThrownBy(() -> plan("SELECT q.id FROM quotes q WHERE q.bid = q.ask"))
        .isInstanceOf(UnsupportedFeatureException.class)
        .hasMessageContaining("a comparison of a composite value (q.bid = q.ask)");
    assertThatThrownBy(() -> plan("SELECT bid, COUNT(*) AS n FROM quotes GROUP BY bid"))
        .isInstanceOf(UnsupportedFeatureException.class)
        .hasMessageContaining("GROUP BY a composite value (bid)");
    assertThatThrownBy(() -> plan("SELECT DISTINCT venue FROM quotes"))
        .isInstanceOf(UnsupportedFeatureException.class)
        .hasMessageContaining("SELECT DISTINCT over the composite column 'venue'");
  }

  // ---- registration ----

  private static CatalogContext withQuotes(UnaryOperator<Table.Builder> change) {
    CatalogContext declared = TestCatalogs.declared();
    chalk.ir.v1.Schema.Builder schema = declared.getSchemas(0).toBuilder();
    for (int t = 0; t < schema.getTablesCount(); t++) {
      if (schema.getTables(t).getName().equals("quotes")) {
        schema.setTables(t, change.apply(schema.getTables(t).toBuilder()));
      }
    }
    return declared.toBuilder().setSchemas(0, schema).build();
  }

  @Test
  void no_key_index_collation_or_covering_set_is_over_a_composite_column() {
    assertThat(RegisteredCatalog.of(withQuotes(t -> t))).isNotNull();

    assertThatThrownBy(
            () ->
                RegisteredCatalog.of(
                    withQuotes(t -> t.addUniqueKeys(UniqueKey.newBuilder().addColumns(3)))))
        .isInstanceOf(InvalidCatalogException.class)
        .hasMessageContaining(
            "column 'bid' is a COMPOSITE and cannot be part of a unique key; a composite value has no"
                + " ordering or equality");
    assertThatThrownBy(
            () ->
                RegisteredCatalog.of(
                    withQuotes(
                        t ->
                            t.addCollations(
                                TableCollation.newBuilder()
                                    .addKeys(
                                        chalk.ir.v1.KeyOrder.newBuilder()
                                            .setColumn(4)
                                            .setDirection(
                                                chalk.ir.v1.SortDirection.SORT_DIRECTION_ASC_NULLS_LAST))))))
        .isInstanceOf(InvalidCatalogException.class)
        .hasMessageContaining("column 'ask' is a COMPOSITE and cannot be part of a collation");
    assertThatThrownBy(
            () ->
                RegisteredCatalog.of(
                    withQuotes(
                        t ->
                            t.addIndexes(
                                chalk.ir.v1.Index.newBuilder()
                                    .setName("ix_venue")
                                    .setKind(chalk.ir.v1.IndexKind.INDEX_KIND_ORDERED)
                                    .addColumns(5)))))
        .isInstanceOf(InvalidCatalogException.class)
        .hasMessageContaining("column 'venue' is a COMPOSITE and cannot be part of an index key");
    assertThatThrownBy(
            () ->
                RegisteredCatalog.of(
                    withQuotes(
                        t ->
                            t.addIndexes(
                                chalk.ir.v1.Index.newBuilder()
                                    .setName("cx_id")
                                    .setKind(chalk.ir.v1.IndexKind.INDEX_KIND_CLUSTERED)
                                    .addColumns(0)))))
        .isInstanceOf(InvalidCatalogException.class)
        .hasMessageContaining(
            "index 'cx_id' is CLUSTERED with no covering set, which covers every column and so the"
                + " composite column 'bid'");
    assertThatThrownBy(
            () ->
                RegisteredCatalog.of(
                    withQuotes(
                        t ->
                            t.addIndexes(
                                chalk.ir.v1.Index.newBuilder()
                                    .setName("cx_id")
                                    .setKind(chalk.ir.v1.IndexKind.INDEX_KIND_CLUSTERED)
                                    .addColumns(0)
                                    .addCovering(0)
                                    .addCovering(3)))))
        .isInstanceOf(InvalidCatalogException.class)
        .hasMessageContaining(
            "the covering set of index 'cx_id' names the composite column 'bid'");
  }

  @Test
  void a_remote_source_declaring_a_composite_column_is_refused_naming_the_table_and_the_column() {
    chalk.ir.v1.DialectProfile profile = TestCatalogs.duckDbProfile();
    CatalogContext remote =
        TestCatalogs.declared().toBuilder()
            .addSchemas(
                chalk.ir.v1.Schema.newBuilder()
                    .setSourceId("warehouse")
                    .setName("warehouse")
                    .setKind(chalk.ir.v1.SourceKind.SOURCE_KIND_REMOTE)
                    .setDialect(profile.getDialect())
                    .setCapabilities(TestCatalogs.fullSqlCapabilities())
                    .setDialectProfile(profile)
                    .addTables(TestCatalogs.quotes()))
            .build();

    assertThatThrownBy(() -> RegisteredCatalog.of(remote))
        .isInstanceOf(InvalidCatalogException.class)
        .hasMessageContaining(
            "column 'bid' of table 'quotes' is a COMPOSITE, and schema 'warehouse' is not an"
                + " in-process source; a composite column is read only from an in-process (LOCAL)"
                + " source. Declare its fields as columns of their own");
  }

  // ---- what a policy may say of a composite column ----

  /** {@code quotes} with a policy on {@code venue}, a nullable composite column. */
  private static CatalogContext withVenuePolicy(UnaryOperator<ColumnEntitlement.Builder> venue) {
    return withQuotes(
        t ->
            t.setEntitlement(
                TableEntitlement.newBuilder()
                    .setDescriptorHash("0123456789abcdef0123456789abcdef")
                    .addColumns(venue.apply(ColumnEntitlement.newBuilder().setColumn(5)))));
  }

  private static DisclosureRule.Builder rule(String when, Disclosure then) {
    return DisclosureRule.newBuilder().setWhen(when).setThen(then);
  }

  @Test
  void a_composite_column_is_disclosed_whole_or_withheld_whole_and_a_condition_may_read_a_field() {
    assertThat(
            RegisteredCatalog.of(
                withVenuePolicy(
                    c ->
                        c.addRules(rule("(venue).country = 'NZ'", Disclosure.DISCLOSURE_FULL))
                            .setOtherwise(Disclosure.DISCLOSURE_NONE))))
        .isNotNull();

    assertThatThrownBy(
            () ->
                RegisteredCatalog.of(
                    withVenuePolicy(
                        c ->
                            c.addRules(
                                rule("TRUE", Disclosure.DISCLOSURE_MASKED).setMask("venue")))))
        .isInstanceOf(InvalidCatalogException.class)
        .hasMessageContaining(
            "on main.quotes, 'venue' is a COMPOSITE column and is disclosed MASKED, but no"
                + " expression builds a composite value to mask it with. A composite column is"
                + " disclosed FULL or NONE, NONE's placeholder being the NULL composite; a rule's"
                + " condition may read a field of it.");
    assertThatThrownBy(
            () ->
                RegisteredCatalog.of(
                    withVenuePolicy(
                        c ->
                            c.setOtherwise(Disclosure.DISCLOSURE_AGGREGATE_ONLY)
                                .addAggregateOnlyFunctions("COUNT"))))
        .isInstanceOf(InvalidCatalogException.class)
        .hasMessageContaining(
            "'venue' is a COMPOSITE column and is disclosed AGGREGATE_ONLY, but no built-in"
                + " aggregate takes a composite value");
    assertThatThrownBy(
            () ->
                RegisteredCatalog.of(
                    withVenuePolicy(c -> c.addRules(rule("TRUE", Disclosure.DISCLOSURE_TEST)))))
        .isInstanceOf(InvalidCatalogException.class)
        .hasMessageContaining(
            "'venue' is a COMPOSITE column and is disclosed TEST, but a composite value has no"
                + " equality to test");
    assertThatThrownBy(
            () ->
                RegisteredCatalog.of(
                    withVenuePolicy(
                        c ->
                            c.addRules(
                                rule("TRUE", Disclosure.DISCLOSURE_NONE).setPlaceholder("NULL")))))
        .isInstanceOf(InvalidCatalogException.class)
        .hasMessageContaining(
            "'venue' is a COMPOSITE column and the rule states a placeholder; no expression builds a"
                + " composite value, and its placeholder is the NULL composite");
  }
}

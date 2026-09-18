package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;

import chalk.ir.v1.DialectProfile;
import chalk.ir.v1.Plan;
import chalk.ir.v1.Rel;
import chalk.planner.catalog.CatalogRegistry;
import chalk.planner.catalog.RegisteredCatalog;
import chalk.planner.ir.IrVersionGate;
import chalk.planner.ir.RelToIr;
import chalk.planner.plan.PlannerPipeline;
import chalk.planner.plan.PushdownPolicy;
import chalk.planner.plan.SqlConfigs;
import java.util.ArrayList;
import java.util.List;
import java.util.Locale;
import org.apache.calcite.rel.metadata.RelMetadataQuery;
import org.junit.jupiter.api.Test;

/**
 * The four defects fixed after Calcite 1.42 that motivated the 1.43 trial
 * (docs/design/calcite-open-issues-assessment.md, docs/design/calcite-143-trial.md): CALCITE-7405,
 * 7663, 7574 and 7661. Each test writes the defect's shape and asserts the <em>corrected</em>
 * behaviour, so the file is a battery whichever version the sidecar is pinned to.
 *
 * <p>Only one of the four reproduces on 1.42 in Chalk's configuration — 7405 — and the trial says
 * so in each test's comment. A test that passes on both versions is not wasted: it pins the Chalk
 * invariant that keeps the defect out of reach, and that invariant can be removed by accident.
 */
class Calcite143DefectsTest {

  private static PlannerPipeline.Result plan(String sql) {
    try (PlannerPipeline pipeline =
        PlannerPipeline.create(
            new CatalogRegistry().register(TestCatalogs.corpus()),
            PushdownPolicy.full(),
            SqlConfigs.DEFAULT_CONFORMANCE,
            List.of())) {
      return pipeline.plan(sql, true);
    } catch (RuntimeException failure) {
      throw failure;
    } catch (Exception failure) {
      throw new IllegalStateException("planning failed for: " + sql, failure);
    }
  }

  // ------------------------------------------------------------------ CALCITE-7405

  /**
   * CALCITE-7405: {@code SqlToRelConverter} builds a projection, {@code RelBuilder}'s bloat setting
   * fuses it into its input, and the converter then re-applies the <em>pre-fusion</em>
   * {@code RexInputRef}s over the <em>post-fusion</em> root, so every reference is off by whatever
   * the fusion removed. The precondition is Chalk's own setting, {@code withExpand(false)}, plus a
   * correlated sub-query in the select list over a sub-query the fusion can absorb. 1.43 builds
   * both projections with {@code LogicalProject.create}, which the fusion cannot touch.
   *
   * <p><b>Reproduces on 1.42.</b> The sidecar refuses the statement: <i>"correlated subquery could
   * not be decorrelated: … RexInputRef index 5 out of range 0..2"</i> — five references applied to
   * a three-column row, which is the defect exactly.
   */
  @Test
  void calcite_7405_a_correlated_subquery_over_a_fusable_projection_plans() {
    PlannerPipeline.Result result =
        plan(
            "SELECT c.c_name, c.bal,"
                + " (SELECT COUNT(*) FROM orders o WHERE o.o_custkey = c.c_custkey) AS n"
                + " FROM (SELECT c_custkey, c_name, c_acctbal + 1 AS bal FROM customer) c");

    assertThat(result.physical().getRowType().getFieldNames())
        .as("the select list, in its own order")
        .containsExactly("c_name", "bal", "n");
    // Nothing may be left addressing the outer row: a surviving $cor is what the mis-applied
    // references would have produced had the plan been built at all.
    assertThat(result.physicalPlanText()).doesNotContain("$cor");
    assertThat(result.logicalPlanText()).doesNotContain("$cor");
  }

  // ------------------------------------------------------------------ CALCITE-7663

  /**
   * CALCITE-7663: for a dialect whose {@code supportGenerateSelectStar} is false — Postgres, when
   * the star is over a join whose inputs share a column name — {@code SqlImplementor} expands the
   * star into bare, <em>un-aliased</em> column references, so a self-join exposes two output
   * columns with the same name. Chalk reads a source's answer by position, and an ambiguous name is
   * either a refusal by the source or a wrong column. 1.43 renames each expanded column to its
   * row-type field name.
   *
   * <p><b>Does not reproduce on 1.42</b> in Chalk's configuration, and this test says why: the
   * pushed text is never a bare star over a join, because {@code PlannerPipeline.rootProject}
   * (ADR 0014) restates the select list's indexes and names over the optimised row and the field
   * trimmer leaves that projection above the join. The assertion is the property the mitigation
   * buys — no output name appears twice in the remote text — so removing the mitigation fails here
   * rather than at a source.
   */
  @Test
  void calcite_7663_a_pushed_self_join_names_no_output_column_twice() {
    DialectProfile postgres =
        DialectProfile.newBuilder()
            .setDialect("postgres")
            .setQuoting(chalk.ir.v1.IdentifierQuoting.IDENTIFIER_QUOTING_DOUBLE_QUOTE)
            .setQuotedCasing(chalk.ir.v1.IdentifierCasing.IDENTIFIER_CASING_UNCHANGED)
            .setUnquotedCasing(chalk.ir.v1.IdentifierCasing.IDENTIFIER_CASING_TO_LOWER)
            .setCaseSensitiveIdentifiers(false)
            .setConformance(chalk.ir.v1.SqlConformance.SQL_CONFORMANCE_LENIENT)
            .setMaxNumericPrecision(38)
            .setMaxTimestampPrecision(6)
            .setHasBoolean(true)
            .setDefaultNullCollation(chalk.ir.v1.NullCollation.NULL_COLLATION_HIGH)
            .setSupportsNullOrderingClause(true)
            .setStringCollation(chalk.ir.v1.StringCollation.STRING_COLLATION_BINARY)
            .build();
    RegisteredCatalog catalog =
        new CatalogRegistry()
            .register(
                TestCatalogs.withRemote("db", TestCatalogs.fullSqlCapabilities().build(), postgres));

    String text =
        remoteText(
            catalog, "SELECT * FROM db.orders a JOIN db.orders b ON a.o_custkey = b.o_orderkey");

    assertThat(text).as("a self-join is pushed whole").containsIgnoringCase("inner join");
    assertThat(outputNames(text))
        .as("every output column of the remote text is named once: " + text)
        .doesNotHaveDuplicates();
  }

  // ------------------------------------------------------------------ CALCITE-7574

  /**
   * CALCITE-7574: {@code RelDecorrelator.isFieldNotNullRecursive} guards an aggregate's group set
   * with {@code groupSet.size()} where {@code agg.getGroupCount()} was meant.
   * {@code ImmutableBitSet.size()} is the bit-vector length — {@code words.length * 64}, so 64 for
   * any group set over a column below 64 — not the cardinality, so the guard is vacuous and
   * {@code groupSet.asList().get(index)} throws {@code IndexOutOfBoundsException} for any index
   * past the group keys. It surfaces as {@code INTERNAL}, not as D67's {@code UNSUPPORTED}.
   *
   * <p><b>Does not reproduce on 1.42</b> in Chalk's configuration: the guard is only reached for a
   * correlation variable propagated <em>past</em> an aggregate's group keys, and every shape tried
   * propagates it <em>as</em> a group key, where the index is below the group count and the
   * vacuous guard does no harm. The assertion is the shape the defect needs on its way in — a
   * correlated sub-query decorrelated over an aggregate whose group set is sparse, so its
   * cardinality (2) and its bit-vector length (64) differ — and that it plans.
   */
  @Test
  void calcite_7574_a_decorrelated_sparse_group_set_plans() {
    PlannerPipeline.Result result =
        plan(
            "SELECT c.c_name FROM customer c WHERE EXISTS ("
                + " SELECT o.o_shippriority, MIN(o.o_totalprice) FROM orders o"
                + " WHERE o.o_custkey = c.c_custkey GROUP BY o.o_shippriority, o.o_comment)");

    assertThat(result.physicalPlanText()).doesNotContain("$cor");
    Plan plan = toIr(result);
    List<Rel> aggregates = new ArrayList<>(IrNodes.of(plan, Rel.KindCase.HASH_AGGREGATE));
    aggregates.addAll(IrNodes.of(plan, Rel.KindCase.STREAM_AGGREGATE));
    assertThat(aggregates)
        .as("the correlated aggregate survived decorrelation")
        .isNotEmpty();
    assertThat(
            aggregates.stream()
                .map(
                    rel ->
                        rel.getKindCase() == Rel.KindCase.HASH_AGGREGATE
                            ? rel.getHashAggregate().getAggregate()
                            : rel.getStreamAggregate().getAggregate())
                .anyMatch(
                    aggregate ->
                        aggregate.getGroupingsCount() == 1
                            && aggregate.getGroupings(0).getKeysCount() == 3))
        .as("the group set is the sub-query's two keys plus the correlation key: " + plan)
        .isTrue();
  }

  // ------------------------------------------------------------------ CALCITE-7661

  /**
   * CALCITE-7661: when both inputs of a join inside a correlated sub-query carry a copy of the same
   * outer field, {@code RelDecorrelator} used to add the equality between the two copies only for a
   * join that generates nulls, so an inner join left them free and rows belonging to different
   * outer values joined. Under an entitlement that crosses tenancies. 1.43 adds the condition for
   * every join type.
   *
   * <p><b>Does not reproduce on 1.42</b> for this shape: a correlated sub-query in a projection
   * goes through {@code PROJECT_SUB_QUERY_TO_CORRELATE} and then the classic
   * {@code RelDecorrelator}, and on both versions the decorrelated join keeps both equalities. The
   * assertion is that property, over a join key that is deliberately <b>not</b> unique
   * ({@code o_orderstatus}), so no functional dependency can stand in for the lost one.
   *
   * <p>See docs/design/calcite-143-trial.md for the shape that <em>does</em> lose the equality on
   * both versions — a {@code LATERAL} sub-query, which Chalk decorrelates with the general
   * (top-down) decorrelator, not the class this issue fixed.
   */
  @Test
  void calcite_7661_both_copies_of_the_outer_field_stay_tied_together() {
    PlannerPipeline.Result result =
        plan(
            "SELECT c.c_name,"
                + " (SELECT COUNT(*) FROM orders o1 JOIN orders o2"
                + "    ON o1.o_orderstatus = o2.o_orderstatus"
                + "  WHERE o1.o_custkey = c.c_custkey AND o2.o_custkey = c.c_custkey) AS n"
                + " FROM customer c");

    assertThat(result.physicalPlanText()).doesNotContain("$cor");
    Plan plan = toIr(result);
    List<Rel> joins = IrNodes.of(plan, Rel.KindCase.HASH_JOIN);
    assertThat(
            joins.stream()
                .anyMatch(rel -> rel.getHashJoin().getLeftKeysCount() == 2))
        .as(
            "the join inside the sub-query keeps both the status equality and the equality between"
                + " the two copies of o_custkey: "
                + result.physicalPlanText())
        .isTrue();
  }

  // ------------------------------------------------------------------ helpers

  private static Plan toIr(PlannerPipeline.Result result) {
    return new RelToIr(
            new chalk.planner.types.TypeMapper(result.physical().getCluster().getTypeFactory()),
            result.physical().getCluster().getRexBuilder(),
            RelMetadataQuery.instance(),
            IrVersionGate.current())
        .toPlan(result.physical(), result.parameterRowType(), "test", 1L);
  }

  private static String remoteText(RegisteredCatalog catalog, String sql) {
    try (PlannerPipeline pipeline = PlannerPipeline.create(catalog, PushdownPolicy.full())) {
      PlannerPipeline.Result result = pipeline.plan(sql, false);
      List<Rel> remote = IrNodes.of(toIr(result), Rel.KindCase.REMOTE_QUERY);
      assertThat(remote).as("one pushed subtree for: " + sql).hasSize(1);
      return remote.get(0).getRemoteQuery().getQueryText();
    } catch (RuntimeException failure) {
      throw failure;
    } catch (Exception failure) {
      throw new IllegalStateException("planning failed for: " + sql, failure);
    }
  }

  /**
   * The output name of every item in the outermost select list of a generated statement: the text
   * after {@code AS}, or the last identifier when there is no alias — which is exactly what a
   * source would call the column and what Chalk would read by position.
   */
  private static List<String> outputNames(String text) {
    int from = text.toUpperCase(Locale.ROOT).indexOf(" FROM ");
    String selectList = text.substring("SELECT ".length(), from < 0 ? text.length() : from);
    List<String> names = new ArrayList<>();
    int depth = 0;
    StringBuilder item = new StringBuilder();
    for (int i = 0; i < selectList.length(); i++) {
      char c = selectList.charAt(i);
      if (c == '(') {
        depth++;
      } else if (c == ')') {
        depth--;
      }
      if (c == ',' && depth == 0) {
        names.add(lastIdentifier(item.toString()));
        item.setLength(0);
      } else {
        item.append(c);
      }
    }
    if (!item.toString().isBlank()) {
      names.add(lastIdentifier(item.toString()));
    }
    return names;
  }

  private static String lastIdentifier(String item) {
    List<String> quoted = new ArrayList<>();
    java.util.regex.Matcher matcher = java.util.regex.Pattern.compile("\"([^\"]*)\"").matcher(item);
    while (matcher.find()) {
      quoted.add(matcher.group(1));
    }
    return quoted.isEmpty() ? item.trim() : quoted.get(quoted.size() - 1);
  }
}

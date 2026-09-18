package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;

import chalk.ir.v1.DialectProfile;
import chalk.ir.v1.Plan;
import chalk.ir.v1.Rel;
import chalk.ir.v1.RemoteQuery;
import chalk.ir.v1.SourceCapabilities;
import chalk.planner.catalog.CatalogRegistry;
import chalk.planner.catalog.RegisteredCatalog;
import chalk.planner.ir.IrVersionGate;
import chalk.planner.ir.RelToIr;
import chalk.planner.plan.PlannerPipeline;
import chalk.planner.plan.PushdownPolicy;
import java.util.ArrayList;
import java.util.List;
import org.junit.jupiter.api.Test;

/**
 * The SQL a pushed subtree becomes, per dialect (§2). The corpus records the same text as golden
 * files; this checks the properties that must hold whatever the query is, where a failure names the
 * reason rather than a diff.
 */
class GeneratedSqlTest {

  private static RemoteQuery remoteQuery(DialectProfile profile, String sql) {
    return remoteQuery(TestCatalogs.fullSqlCapabilities().build(), profile, sql);
  }

  private static RemoteQuery remoteQuery(
      SourceCapabilities capabilities, DialectProfile profile, String sql) {
    RegisteredCatalog catalog =
        new CatalogRegistry().register(TestCatalogs.withRemote("db", capabilities, profile));
    try (PlannerPipeline pipeline = PlannerPipeline.create(catalog, PushdownPolicy.full())) {
      PlannerPipeline.Result result = pipeline.plan(sql, false);
      Plan plan =
          new RelToIr(
                  new chalk.planner.types.TypeMapper(
                      result.physical().getCluster().getTypeFactory()),
                  result.physical().getCluster().getRexBuilder(),
                  org.apache.calcite.rel.metadata.RelMetadataQuery.instance(),
                  IrVersionGate.current())
              .toPlan(result.physical(), result.parameterRowType(), "test", 1L);
      List<RemoteQuery> found = new ArrayList<>();
      collect(plan.getRoot(), found);
      assertThat(found).as("a RemoteQuery for: " + sql).hasSize(1);
      return found.get(0);
    } catch (Exception failure) {
      throw new AssertionError("planning failed for: " + sql, failure);
    }
  }

  private static void collect(Rel rel, List<RemoteQuery> into) {
    if (rel.getKindCase() == Rel.KindCase.REMOTE_QUERY) {
      into.add(rel.getRemoteQuery());
      return;
    }
    for (Rel input : chalk.planner.IrNodes.inputs(rel)) {
      collect(input, into);
    }
  }

  /** D84: both flavours travel for a SQL source, so step 24 can reverse the query text. */
  @Test
  void a_sql_source_carries_both_the_query_text_and_the_pushed_plan() {
    RemoteQuery query =
        remoteQuery(
            TestCatalogs.duckDbProfile(),
            "SELECT l_orderkey FROM db.lineitem WHERE l_orderkey > 10");

    assertThat(query.getSourceId()).isEqualTo("db");
    assertThat(query.getDialect()).isEqualTo("duckdb");
    assertThat(query.getQueryText()).isNotBlank();
    assertThat(query.hasPushedPlan()).isTrue();
  }

  /** An IR source never generates SQL: the subtree itself is what it is handed. */
  @Test
  void an_ir_source_carries_only_the_pushed_plan() {
    SourceCapabilities ir =
        TestCatalogs.fullSqlCapabilities()
            .setQueryLanguage(chalk.ir.v1.QueryLanguage.QUERY_LANGUAGE_IR)
            .build();
    RemoteQuery query =
        remoteQuery(
            ir,
            TestCatalogs.duckDbProfile(),
            "SELECT l_orderkey FROM db.lineitem WHERE l_orderkey > 10");

    assertThat(query.getQueryText()).isEmpty();
    assertThat(query.hasPushedPlan()).isTrue();
  }

  /** Identifiers are quoted the way the profile says, and the table is named as the source knows it. */
  @Test
  void identifiers_are_quoted_and_the_table_is_not_schema_qualified() {
    String sql =
        remoteQuery(
                TestCatalogs.duckDbProfile(),
                "SELECT l_orderkey FROM db.lineitem WHERE l_orderkey > 10")
            .getQueryText();

    assertThat(sql).contains("\"lineitem\"");
    assertThat(sql).doesNotContain("\"db\".");
    assertThat(sql).contains("\"l_orderkey\"");
  }

  /** The two dialects spell a fetch differently, which is the whole point of a per-source dialect. */
  @Test
  void sqlite_and_duckdb_spell_limit_the_same_way_and_neither_uses_fetch() {
    String sqlite =
        remoteQuery(
                TestCatalogs.sqliteProfile(),
                "SELECT l_orderkey FROM db.lineitem WHERE l_orderkey > 10 LIMIT 5")
            .getQueryText();
    String duck =
        remoteQuery(
                TestCatalogs.duckDbProfile(),
                "SELECT l_orderkey FROM db.lineitem WHERE l_orderkey > 10 LIMIT 5")
            .getQueryText();

    assertThat(sqlite).containsIgnoringCase("LIMIT 5").doesNotContainIgnoringCase("FETCH");
    assertThat(duck).containsIgnoringCase("LIMIT 5").doesNotContainIgnoringCase("FETCH");
  }

  // -------------------------------------------------------- any Calcite product is a preset (D249)

  /**
   * D249: a source whose profile names a plain Calcite {@code DatabaseProduct} — not one of the
   * four tuned presets — renders in that product's own spelling, over the same per-profile quoting
   * the tuned ones get. Oracle spells a fetch {@code FETCH NEXT n ROWS ONLY} rather than SQLite's
   * and DuckDB's {@code LIMIT n}, which is enough to show the dialect took.
   */
  @Test
  void an_untuned_product_dialect_renders_in_its_own_spelling() {
    DialectProfile oracle =
        DialectProfile.newBuilder()
            .setDialect("oracle")
            .setQuoting(chalk.ir.v1.IdentifierQuoting.IDENTIFIER_QUOTING_DOUBLE_QUOTE)
            .setQuotedCasing(chalk.ir.v1.IdentifierCasing.IDENTIFIER_CASING_UNCHANGED)
            .setCaseSensitiveIdentifiers(true)
            .build();

    String sql =
        remoteQuery(oracle, "SELECT l_orderkey FROM db.lineitem WHERE l_orderkey > 10 LIMIT 5")
            .getQueryText();

    assertThat(sql).containsIgnoringCase("FETCH NEXT 5 ROWS ONLY").doesNotContainIgnoringCase("LIMIT");
    assertThat(sql).contains("\"l_orderkey\"").contains("\"lineitem\"");
  }

  // -------------------------------------------------------- integer division (F27, ADR 0027)

  /**
   * F27: {@code /} over two integers is real division in DuckDB and integer division everywhere
   * else Chalk pushes to, so an integer-typed {@code DIVIDE} is spelled {@code //} there and
   * {@code /} in the other two dialects.
   *
   * <p>Measured on 2026-09-11: DuckDB 1.5.1 answers {@code -7 / 3} as {@code -2.3333333333333335}
   * (DOUBLE) and {@code -7 // 3} as {@code -2} (INTEGER); PostgreSQL 16 and SQLite 3.53.3 answer
   * {@code -2} for {@code /}, and PostgreSQL has no {@code //} operator at all. The projection and
   * the predicate are in the same query on purpose: the projection's DOUBLE would have failed the
   * scan contract loudly, and the predicate's would have been a silent wrong answer.
   */
  /**
   * A dialect that sums an integer into a wider type answers with a column the scan contract cannot
   * read as the plan's own (F57): DuckDB's {@code SUM(BIGINT)} is a {@code HUGEINT}. The writer casts
   * the measure back to the type the plan declares; SQLite keeps the argument's type and gets no cast.
   */
  @Test
  void an_integer_sum_is_cast_back_to_its_own_type_where_the_dialect_widens_it() {
    String sql = "SELECT symbol, SUM(volume) AS v FROM db.bars GROUP BY symbol";
    // The pre-pass reduces SUM to SUM0, which the writer spells COALESCE(SUM(x), 0); the cast wraps
    // the whole measure.
    assertThat(remoteQuery(TestCatalogs.duckDbProfile(), sql).getQueryText())
        .contains("CAST(COALESCE(SUM(\"volume\"), 0) AS BIGINT) AS \"v\"");
    assertThat(remoteQuery(TestCatalogs.sqliteProfile(), sql).getQueryText())
        .contains("COALESCE(SUM(\"volume\"), 0) AS \"v\"")
        .doesNotContain("CAST(COALESCE");
  }

  @Test
  void an_integer_divide_is_duckdbs_own_operator_and_every_other_dialect_keeps_the_slash() {
    String integers = "SELECT volume / 3 AS q FROM db.bars WHERE volume / 3 = 2";

    assertThat(inDialect(TestCatalogs.duckDbProfile(), integers))
        .contains("(\"volume\" // 3) AS \"q\"")
        .contains("WHERE (\"volume\" // 3) = 2")
        .doesNotContain("\"volume\" / 3");
    assertThat(inDialect(TestCatalogs.sqliteProfile(), integers))
        .contains("\"volume\" / 3 AS \"q\"")
        .contains("WHERE \"volume\" / 3 = 2")
        .doesNotContain("//");
    assertThat(inDialect(TestCatalogs.postgresProfile(), integers))
        .contains("\"volume\" / 3 AS \"q\"")
        .contains("WHERE \"volume\" / 3 = 2")
        .doesNotContain("//");
  }

  /**
   * The other half of F27: only an <em>integer</em> division is substituted. A division whose
   * result is a real is real division in every one of the three dialects and in Chalk, so it keeps
   * the operator it had — including on DuckDB, where {@code //} would truncate an answer that must
   * not be truncated.
   */
  @Test
  void a_division_that_answers_in_a_real_keeps_the_slash_in_every_dialect() {
    String reals = "SELECT high / 3 AS q FROM db.bars WHERE high / 3 > 2";

    for (DialectProfile profile :
        List.of(
            TestCatalogs.duckDbProfile(),
            TestCatalogs.sqliteProfile(),
            TestCatalogs.postgresProfile())) {
      assertThat(inDialect(profile, reals))
          .as(profile.getDialect())
          .contains("\"high\" / 3 AS \"q\"")
          .contains("WHERE \"high\" / 3 > 2")
          .doesNotContain("//");
    }
  }

  /**
   * The generated SQL for one statement in one dialect, with the capabilities a source under that
   * profile could honestly declare: the catalog validator refuses a LIKE shape where the profile's
   * collation is not Chalk's, which is what the PostgreSQL preset says of a cluster with a locale
   * (D89).
   */
  private static String inDialect(DialectProfile profile, String sql) {
    SourceCapabilities.Builder capabilities = TestCatalogs.fullSqlCapabilities();
    if (profile.getStringCollation() != chalk.ir.v1.StringCollation.STRING_COLLATION_BINARY) {
      List<chalk.ir.v1.PredicateShape> shapes =
          new ArrayList<>(capabilities.getPushablePredicatesList());
      shapes.remove(chalk.ir.v1.PredicateShape.PREDICATE_SHAPE_LIKE);
      shapes.remove(chalk.ir.v1.PredicateShape.PREDICATE_SHAPE_LIKE_PREFIX);
      capabilities.clearPushablePredicates().addAllPushablePredicates(shapes);
    }

    return remoteQuery(capabilities.build(), profile, sql).getQueryText();
  }

  /** A dynamic parameter becomes a positional placeholder, and the plan records it. */
  @Test
  void a_parameter_becomes_a_placeholder_and_is_carried_on_the_node() {
    RemoteQuery query =
        remoteQuery(
            TestCatalogs.duckDbProfile(), "SELECT l_orderkey FROM db.lineitem WHERE l_orderkey > ?");

    assertThat(query.getQueryText()).contains("?");
    assertThat(query.getParametersCount()).isEqualTo(1);
  }

  /**
   * A source that does not take parameters never sees one: the gate refuses to push an expression
   * containing a {@code RexDynamicParam}, so the filter stays local and the remote query is a plain
   * scan.
   */
  @Test
  void a_source_without_parameters_keeps_the_parameterised_filter_local() {
    SourceCapabilities noParameters =
        TestCatalogs.fullSqlCapabilities().setSupportsParameters(false).build();
    RemoteQuery query =
        remoteQuery(
            noParameters,
            TestCatalogs.duckDbProfile(),
            "SELECT l_orderkey FROM db.lineitem WHERE l_orderkey > ?");

    assertThat(query.getQueryText()).doesNotContain("?");
    assertThat(query.getParametersCount()).isZero();
  }

  /** A pushed aggregate is a GROUP BY in the generated text, not something Chalk runs. */
  @Test
  void a_pushed_aggregate_becomes_a_group_by() {
    String sql =
        remoteQuery(
                TestCatalogs.duckDbProfile(),
                "SELECT l_returnflag, SUM(l_quantity) FROM db.lineitem GROUP BY l_returnflag")
            .getQueryText();

    assertThat(sql).containsIgnoringCase("GROUP BY");
    assertThat(sql).containsIgnoringCase("SUM");
  }

  /** A same-source join is one query with a JOIN in it. */
  @Test
  void a_pushed_join_becomes_one_query() {
    String sql =
        remoteQuery(
                TestCatalogs.duckDbProfile(),
                "SELECT o.o_orderkey, c.c_name FROM db.orders o"
                    + " JOIN db.customer c ON o.o_custkey = c.c_custkey"
                    + " WHERE c.c_mktsegment = 'BUILDING'")
            .getQueryText();

    assertThat(sql).containsIgnoringCase("JOIN");
    assertThat(sql).contains("\"orders\"");
    assertThat(sql).contains("\"customer\"");
  }

  /** The generated SQL is one line, so a golden file is a diff a person can read. */
  @Test
  void the_generated_sql_is_a_single_line() {
    String sql =
        remoteQuery(
                TestCatalogs.duckDbProfile(),
                "SELECT l_returnflag, SUM(l_quantity) FROM db.lineitem"
                    + " WHERE l_orderkey > 10 GROUP BY l_returnflag")
            .getQueryText();

    assertThat(sql).doesNotContain("\n");
  }

  /**
   * The one-line normaliser must not touch what is inside a literal: collapsing whitespace there
   * would change which rows the predicate matches.
   */
  @Test
  void making_the_sql_one_line_leaves_string_literals_alone() {
    String sql =
        remoteQuery(
                TestCatalogs.duckDbProfile(),
                "SELECT c_name FROM db.customer WHERE c_mktsegment = 'A  B'")
            .getQueryText();

    assertThat(sql).contains("'A  B'");
    assertThat(sql).doesNotContain("\n");
  }

  // ------------------------------------------------------------------ never a star (F35)

  /**
   * A full-width read names its columns. Calcite writes it as {@code SELECT *}, under which the
   * source answers with its own physical columns in its own order and the reader maps them by
   * position, so a schema that drifted after registration swaps two same-typed columns silently.
   */
  @Test
  void a_full_width_read_names_the_registered_columns_in_the_plans_order() {
    String sql =
        remoteQuery(TestCatalogs.duckDbProfile(), "SELECT * FROM db.customer").getQueryText();

    assertThat(sql).doesNotContain("*");
    assertThat(sql)
        .startsWith(
            "SELECT \"c_custkey\", \"c_name\", \"c_address\", \"c_nationkey\", \"c_phone\","
                + " \"c_acctbal\", \"c_mktsegment\", \"c_comment\" FROM \"customer\"");
  }

  /** A star over a sub-select takes that select's own names, which the plan already ordered. */
  @Test
  void a_star_over_a_sub_select_takes_its_names() {
    String sql =
        remoteQuery(
                TestCatalogs.duckDbProfile(),
                "SELECT c_custkey, c_name FROM db.customer WHERE c_mktsegment = 'BUILDING'")
            .getQueryText();

    assertThat(sql).doesNotContain("*");
    assertThat(sql)
        .isEqualTo(
            "SELECT \"c_custkey\", \"c_name\" FROM (SELECT \"c_custkey\", \"c_name\","
                + " \"c_mktsegment\" FROM \"customer\") AS \"t\""
                + " WHERE \"c_mktsegment\" = 'BUILDING'");
  }

  /** Every shape the corpus can push, with no star left anywhere in any of them. */
  @Test
  void no_generated_query_of_any_shape_says_star() {
    List<String> statements =
        List.of(
            "SELECT * FROM db.customer",
            "SELECT * FROM db.customer WHERE c_custkey < 10",
            "SELECT c_name FROM db.customer WHERE c_mktsegment = 'BUILDING'",
            "SELECT l_returnflag, SUM(l_quantity) FROM db.lineitem GROUP BY l_returnflag",
            "SELECT o.o_orderkey, c.c_name FROM db.orders o"
                + " JOIN db.customer c ON o.o_custkey = c.c_custkey",
            "SELECT * FROM db.customer ORDER BY c_custkey LIMIT 5",
            "SELECT COUNT(*) FROM db.customer");

    for (String statement : statements) {
      String sql = remoteQuery(TestCatalogs.duckDbProfile(), statement).getQueryText();
      // COUNT(*) is a star inside a call, not a select list, and stays as it is.
      assertThat(sql.replace("COUNT(*)", "COUNT(x)")).as(statement).doesNotContain("*");
    }
  }
}

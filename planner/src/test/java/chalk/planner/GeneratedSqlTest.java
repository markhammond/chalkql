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

  /**
   * Every {@code RemoteQuery} a plan holds, with the physical plan text beside them. A query whose
   * branches are pushed separately — a {@code UNION ALL}, a partitioned scan — has more than one,
   * and what stayed local is exactly what the plan text says.
   */
  private record Pushed(List<RemoteQuery> queries, String planText) {}

  private static Pushed pushed(DialectProfile profile, String sql) {
    RegisteredCatalog catalog =
        new CatalogRegistry()
            .register(TestCatalogs.withRemote("db", TestCatalogs.fullSqlCapabilities().build(), profile));
    try (PlannerPipeline pipeline = PlannerPipeline.create(catalog, PushdownPolicy.full())) {
      PlannerPipeline.Result result = pipeline.plan(sql, true);
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
      return new Pushed(found, result.physicalPlanText());
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

  // ------------------------------------------------------------ a copied limit, per branch (D276)

  /**
   * D276 §3 and §4: a bound above a {@code UNION ALL} is copied into every branch, and a branch that
   * is one source's subtree carries it away as that source's own {@code ORDER BY … LIMIT}. The copy
   * carries {@code offset + fetch} and <b>no</b> {@code OFFSET}: each branch must offer its first
   * {@code o + n} candidates and the global sort applies the offset once, so a source never needs
   * {@code supports_offset} for a copy.
   */
  @Test
  void a_copied_bound_becomes_each_remote_branchs_own_order_by_and_limit() {
    Pushed pushed =
        pushed(
            TestCatalogs.duckDbProfile(),
            "SELECT symbol, volume FROM db.bars WHERE volume > 1"
                + " UNION ALL SELECT symbol, volume FROM db.bars WHERE volume > 2"
                + " ORDER BY symbol OFFSET 2 ROWS FETCH NEXT 3 ROWS ONLY");

    assertThat(pushed.queries()).hasSize(2);
    for (RemoteQuery branch : pushed.queries()) {
      assertThat(branch.getQueryText())
          .containsIgnoringCase("ORDER BY")
          .containsIgnoringCase("LIMIT 5")
          .doesNotContainIgnoringCase("OFFSET");
    }
  }

  /**
   * And where the gate refuses the collation the copy still pays: it stays a local top-N over the
   * branch rather than being pushed or abandoned, so the global node still picks from at most
   * {@code branches × (o + n)} rows. SQLite's profile caps a timestamp at millisecond precision, and
   * {@code bars.ts} is a nanosecond one, so sorting there would not be sorting by the same key.
   */
  @Test
  void a_branch_whose_collation_the_gate_refuses_keeps_its_bound_locally() {
    Pushed pushed =
        pushed(
            TestCatalogs.sqliteProfile(),
            "SELECT symbol, ts FROM db.bars WHERE volume > 1"
                + " UNION ALL SELECT symbol, ts FROM db.bars WHERE volume > 2"
                + " ORDER BY ts LIMIT 5");

    assertThat(pushed.queries()).hasSize(2);
    for (RemoteQuery branch : pushed.queries()) {
      assertThat(branch.getQueryText())
          .doesNotContainIgnoringCase("ORDER BY")
          .doesNotContainIgnoringCase("LIMIT");
    }

    // The global bound and one per branch.
    assertThat(pushed.planText().lines().filter(line -> line.contains("ChalkTopN(")).count())
        .isEqualTo(3);
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

  // ---------------------------------------- a bound that is a parameter, per dialect (D288)

  /**
   * The five spellings, pinned: whatever the dialect writes where the count goes, it writes a
   * {@code ?} there, and every one of these unparsers accepts a {@code SqlDynamicParam} in that
   * position. The tuned SQLite and DuckDB dialects spell it {@code LIMIT ?}; the tuned PostgreSQL
   * dialect and the ANSI rendering spell it {@code FETCH NEXT ? ROWS ONLY} — PostgreSQL has taken
   * that spelling since 8.4 and it is what Calcite's dialect writes; and the SQL Server product
   * {@code SourceDialects} resolves writes {@code TOP (?)}, at the <em>front</em> of the statement.
   */
  @Test
  void every_dialect_writes_a_placeholder_where_the_count_goes() {
    String bounded = "SELECT l_orderkey FROM db.lineitem WHERE l_orderkey > 10 LIMIT ?";

    assertThat(inDialect(TestCatalogs.sqliteProfile(), bounded))
        .isEqualTo(
            "SELECT \"l_orderkey\" FROM (SELECT \"l_orderkey\" FROM \"lineitem\") AS \"t\""
                + " WHERE \"l_orderkey\" > 10 LIMIT ?");
    assertThat(inDialect(TestCatalogs.duckDbProfile(), bounded))
        .isEqualTo(
            "SELECT \"l_orderkey\" FROM (SELECT \"l_orderkey\" FROM \"lineitem\") AS \"t\""
                + " WHERE \"l_orderkey\" > 10 LIMIT ?");
    assertThat(inDialect(TestCatalogs.postgresProfile(), bounded))
        .isEqualTo(
            "SELECT \"l_orderkey\" FROM (SELECT \"l_orderkey\" FROM \"lineitem\") AS \"t\""
                + " WHERE \"l_orderkey\" > 10 FETCH NEXT ? ROWS ONLY");
    assertThat(inDialect(ansiProfile(), bounded))
        .isEqualTo(
            "SELECT \"l_orderkey\" FROM (SELECT \"l_orderkey\" FROM \"lineitem\") AS \"t\""
                + " WHERE \"l_orderkey\" > 10 FETCH NEXT ? ROWS ONLY");
    assertThat(inDialect(sqlServerProfile(), bounded))
        .isEqualTo(
            "SELECT TOP (?) \"l_orderkey\" FROM (SELECT \"l_orderkey\" FROM \"lineitem\") AS \"t\""
                + " WHERE \"l_orderkey\" > 10");
  }

  /** And where a dialect takes an offset, that placeholder is written beside it. */
  @Test
  void an_offset_placeholder_is_written_where_the_dialect_puts_one() {
    String both =
        "SELECT l_orderkey FROM db.lineitem WHERE l_orderkey > 10 ORDER BY l_orderkey"
            + " OFFSET ? ROWS FETCH NEXT ? ROWS ONLY";

    assertThat(inDialect(ansiProfile(), both))
        .isEqualTo(
            "SELECT \"l_orderkey\" FROM (SELECT \"l_orderkey\" FROM \"lineitem\") AS \"t\""
                + " WHERE \"l_orderkey\" > 10 ORDER BY \"l_orderkey\""
                + " OFFSET ? ROWS FETCH NEXT ? ROWS ONLY");
    assertThat(inDialect(TestCatalogs.duckDbProfile(), both))
        .isEqualTo(
            "SELECT \"l_orderkey\" FROM (SELECT \"l_orderkey\" FROM \"lineitem\") AS \"t\""
                + " WHERE \"l_orderkey\" > 10 ORDER BY \"l_orderkey\" LIMIT ? OFFSET ?");
  }

  /**
   * F121: the SQL Server dialect writes {@code TOP (n)} and <b>nothing</b> for an offset, so it is
   * never handed one — the offset stays local and the query it is sent carries neither. Measured
   * rather than listed: {@link chalk.planner.plan.SourceDialects#rendersOffset} puts the question
   * to the dialect, so a dialect Calcite adds or changes is answered on its own behaviour.
   */
  @Test
  void a_dialect_that_writes_no_offset_is_sent_a_query_with_neither_bound() {
    String sql =
        inDialect(
            sqlServerProfile(),
            "SELECT l_orderkey FROM db.lineitem WHERE l_orderkey > 10 ORDER BY l_orderkey"
                + " OFFSET 2 ROWS FETCH NEXT 3 ROWS ONLY");

    assertThat(sql).doesNotContainIgnoringCase("TOP").doesNotContainIgnoringCase("OFFSET");
  }

  /**
   * {@code rendered_bounds} names positions among {@code parameters}, and {@code parameters} are
   * the text's placeholders in the order the text writes them. For {@code LIMIT ?} at the end of a
   * statement whose predicate also binds one, the bound is the <b>second</b> placeholder; for
   * {@code TOP (?)} it is the <b>first</b>, although its parameter is the one the statement
   * numbered last. That reversal is the whole reason the field names a position.
   */
  @Test
  void the_rendered_bound_is_named_by_its_position_in_the_text() {
    String statement = "SELECT l_orderkey FROM db.lineitem WHERE l_orderkey > ? LIMIT ?";

    RemoteQuery duck = remoteQuery(TestCatalogs.duckDbProfile(), statement);
    assertThat(duck.getQueryText()).endsWith("WHERE \"l_orderkey\" > ? LIMIT ?");
    assertThat(duck.getParametersCount()).isEqualTo(2);
    assertThat(duck.getRenderedBoundsList()).containsExactly(1);
    assertThat(duck.getParameters(0).getParam().getIndex()).isEqualTo(0);
    assertThat(duck.getParameters(1).getParam().getIndex()).isEqualTo(1);

    RemoteQuery sqlServer = remoteQuery(sqlServerProfile(), statement);
    assertThat(sqlServer.getQueryText()).startsWith("SELECT TOP (?) ");
    assertThat(sqlServer.getParametersCount()).isEqualTo(2);
    assertThat(sqlServer.getRenderedBoundsList()).containsExactly(0);
    // The bound first, the predicate's parameter second — the reverse of how they are numbered.
    assertThat(sqlServer.getParameters(0).getParam().getIndex()).isEqualTo(1);
    assertThat(sqlServer.getParameters(1).getParam().getIndex()).isEqualTo(0);
  }

  /** Both bounds of an {@code OFFSET … FETCH} are named, in the order the text writes them. */
  @Test
  void an_offset_and_a_fetch_are_both_named() {
    RemoteQuery query =
        remoteQuery(
            ansiCapabilities(),
            ansiProfile(),
            "SELECT l_orderkey FROM db.lineitem ORDER BY l_orderkey"
                + " OFFSET ? ROWS FETCH NEXT ? ROWS ONLY");

    assertThat(query.getParametersCount()).isEqualTo(2);
    assertThat(query.getRenderedBoundsList()).containsExactly(0, 1);
  }

  /** A query with no parameterised bound names none, and carries no bytes for the field. */
  @Test
  void a_literal_bound_names_no_rendered_position() {
    RemoteQuery query =
        remoteQuery(
            TestCatalogs.duckDbProfile(),
            "SELECT l_orderkey FROM db.lineitem WHERE l_orderkey > ? LIMIT 5");

    assertThat(query.getRenderedBoundsList()).isEmpty();
    assertThat(query.getParametersCount()).isEqualTo(1);
  }

  /** The pushed plan keeps the bound as the parameter it is, beside the text that renders it. */
  @Test
  void the_pushed_plan_carries_the_bound_as_a_parameter() {
    RemoteQuery query =
        remoteQuery(TestCatalogs.duckDbProfile(), "SELECT l_orderkey FROM db.lineitem LIMIT ?");

    chalk.ir.v1.Fetch fetch = onlyFetch(query.getPushedPlan());
    assertThat(fetch.hasCountParam()).isTrue();
    assertThat(fetch.getCountParam().getIndex()).isEqualTo(0);
    assertThat(fetch.hasCount()).isFalse();
  }

  private static chalk.ir.v1.Fetch onlyFetch(Rel rel) {
    if (rel.getKindCase() == Rel.KindCase.FETCH) {
      return rel.getFetch();
    }
    for (Rel input : chalk.planner.IrNodes.inputs(rel)) {
      chalk.ir.v1.Fetch found = onlyFetch(input);
      if (found != null) {
        return found;
      }
    }
    return null;
  }

  /**
   * A copied bound is rendered in every branch's own text (D288 with D276): each partition renders
   * the same number when the execution starts, and the global top-N above them still decides the
   * answer.
   */
  @Test
  void a_copied_parameterised_bound_is_rendered_in_every_branch() {
    Pushed pushed =
        pushed(
            TestCatalogs.duckDbProfile(),
            "SELECT symbol, volume FROM db.bars WHERE volume > 1"
                + " UNION ALL SELECT symbol, volume FROM db.bars WHERE volume > 2"
                + " ORDER BY symbol LIMIT ?");

    assertThat(pushed.queries()).hasSize(2);
    for (RemoteQuery branch : pushed.queries()) {
      assertThat(branch.getQueryText()).endsWith("ORDER BY \"symbol\" LIMIT ?");
      assertThat(branch.getRenderedBoundsList()).containsExactly(0);
    }
  }

  private static DialectProfile ansiProfile() {
    return TestCatalogs.duckDbProfile().toBuilder().setDialect("ansi").build();
  }

  /** The SQL Server product {@code SourceDialects.parseProduct} resolves, over a tuned profile. */
  private static DialectProfile sqlServerProfile() {
    return TestCatalogs.duckDbProfile().toBuilder().setDialect("mssql").build();
  }

  private static SourceCapabilities ansiCapabilities() {
    return TestCatalogs.fullSqlCapabilities().build();
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

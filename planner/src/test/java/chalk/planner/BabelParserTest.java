package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

import chalk.ir.v1.Plan;
import chalk.planner.plan.PushdownPolicy;
import java.util.List;
import org.apache.calcite.sql.fun.SqlLibrary;
import org.apache.calcite.sql.parser.SqlParseException;
import org.apache.calcite.sql.validate.SqlConformanceEnum;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.CsvSource;
import org.junit.jupiter.params.provider.ValueSource;

/**
 * What Calcite's Babel parser accepts under {@code SqlConformance.BABEL}, and what it does not
 * (D259, ADR 0039 §2). Every statement here was <em>measured</em> against the pinned build rather
 * than taken from the reference: §2 of the ADR lists what was tried and what each did, including
 * the two constructs D259 expected to be Babel-only and which turned out not to be.
 *
 * <p>The shape of each test is the same as the corpus's own conversion assertions: plan the
 * statement to IR through {@link CorpusPlanner} over the fixture catalog, and assert the plan the
 * client would carry. A statement that plans but cannot be converted fails in {@code plan}, so
 * reaching a {@link Plan} at all is the assertion that the IR can carry it.
 */
class BabelParserTest {

  private static final CorpusPlanner PLANNER = new CorpusPlanner();

  private static Plan plan(String sql, SqlConformanceEnum conformance, List<SqlLibrary> libraries) {
    return PLANNER.plan(sql, PushdownPolicy.full(), conformance, libraries);
  }

  private static Plan babel(String sql) {
    return plan(sql, SqlConformanceEnum.BABEL, List.of());
  }

  // ------------------------------------------------------------ PostgreSQL's :: cast

  /**
   * The headline of D259. {@code ::} is a parse-level construct: the core parser has no production
   * for it at <em>any</em> conformance, and Babel's does.
   *
   * <p>It takes two things, which is the measurement that matters and is not in the design: the
   * Babel parser <b>and</b> {@code SqlLibrary.POSTGRESQL}. Babel parses {@code a::T} into a call to
   * an operator named {@code ::}, and that operator lives in the PostgreSQL library — so
   * {@code BABEL} alone parses the statement and then fails validation with <i>"No match found for
   * function signature ::"</i>. D60's per-request libraries are what complete it.
   */
  @Test
  void a_postgres_cast_plans_under_babel_with_the_postgres_library() {
    Plan plan =
        plan(
            "SELECT o_orderkey::VARCHAR AS id_text FROM orders",
            SqlConformanceEnum.BABEL,
            List.of(SqlLibrary.POSTGRESQL));

    assertThat(plan.getOutputType().getFieldsList())
        .singleElement()
        .extracting(field -> field.getName())
        .isEqualTo("id_text");
  }

  /** The same cast, spelled the way the corpus spells it, is the same plan. */
  @Test
  void a_postgres_cast_carries_the_same_ir_as_the_default_spelling() {
    Plan babel =
        plan(
            "SELECT o_orderkey::VARCHAR AS id_text FROM orders",
            SqlConformanceEnum.BABEL,
            List.of(SqlLibrary.POSTGRESQL));
    Plan standard =
        plan(
            "SELECT CAST(o_orderkey AS VARCHAR) AS id_text FROM orders",
            SqlConformanceEnum.DEFAULT,
            List.of());

    assertThat(babel.getRoot()).isEqualTo(standard.getRoot());
    assertThat(babel.getOutputType()).isEqualTo(standard.getOutputType());
  }

  /**
   * Without the library the statement still parses — it fails at <em>validation</em>, not at
   * parsing, which is what says the parser is the Babel one and the gap is the operator table.
   */
  @Test
  void a_postgres_cast_under_babel_without_the_library_fails_after_parsing() {
    assertThatThrownBy(() -> babel("SELECT o_orderkey::VARCHAR AS id_text FROM orders"))
        .rootCause()
        .hasMessageContaining("No match found for function signature ::");
  }

  /** And under every other conformance it is a parse failure, library or no library. */
  @ParameterizedTest
  @ValueSource(strings = {"DEFAULT", "LENIENT", "MYSQL_5", "PRESTO"})
  void a_postgres_cast_is_a_parse_failure_under_every_other_conformance(String level) {
    assertThatThrownBy(
            () ->
                plan(
                    "SELECT o_orderkey::VARCHAR AS id_text FROM orders",
                    SqlConformanceEnum.valueOf(level),
                    List.of(SqlLibrary.POSTGRESQL)))
        .hasCauseInstanceOf(SqlParseException.class);
  }

  // ------------------------------------------------- Babel's smaller set of reserved words

  /**
   * The other half of what Babel's grammar buys, and the half the design did not name: it reserves
   * far fewer keywords, so words that are ordinary column names in most databases can be written
   * unquoted. Each of these is a parse failure under the core parser at any conformance.
   */
  @ParameterizedTest
  @ValueSource(strings = {"value", "year", "user", "start", "language", "system", "position"})
  void a_non_reserved_keyword_is_an_identifier_under_babel_and_a_parse_failure_otherwise(
      String word) {
    Plan plan = babel("SELECT o_orderkey AS " + word + " FROM orders");
    assertThat(plan.getOutputType().getFieldsList())
        .singleElement()
        .extracting(field -> field.getName())
        .isEqualTo(word);

    assertThatThrownBy(
            () ->
                plan(
                    "SELECT o_orderkey AS " + word + " FROM orders",
                    SqlConformanceEnum.DEFAULT,
                    List.of()))
        .hasCauseInstanceOf(SqlParseException.class);
  }

  /** Babel is not a free-for-all: a fully reserved word is still reserved. */
  @ParameterizedTest
  @ValueSource(strings = {"table", "select"})
  void a_fully_reserved_word_is_still_a_parse_failure_under_babel(String word) {
    assertThatThrownBy(() -> babel("SELECT o_orderkey AS " + word + " FROM orders"))
        .hasCauseInstanceOf(SqlParseException.class);
  }

  // ------------------------------------------------- what D259 expected to be Babel-only

  /**
   * <b>Measured, and not what D259 assumed.</b> {@code SELECT * EXCLUDE}, {@code EXCEPT} and
   * {@code REPLACE} are in the <em>core</em> parser on the pinned build, at every conformance,
   * including {@code DEFAULT}. Nothing had to be done to get them and nothing in D259 gives them to
   * a caller who did not already have them.
   *
   * <p>The assertion is deliberately on both levels: it is the fact that the design is corrected
   * against, so if a later Calcite moved these productions into Babel this test is where that
   * shows, rather than in a host's query.
   */
  @ParameterizedTest
  @ValueSource(
      strings = {
        "SELECT * EXCLUDE (o_comment) FROM orders",
        "SELECT * EXCEPT (o_comment) FROM orders",
        "SELECT o.* EXCLUDE (o_comment) FROM orders o"
      })
  void star_exclude_is_the_core_parser_not_babel(String sql) {
    assertThat(babel(sql).getOutputType().getFieldsCount()).isEqualTo(8);
    assertThat(plan(sql, SqlConformanceEnum.DEFAULT, List.of()).getOutputType().getFieldsCount())
        .isEqualTo(8);
  }

  /** {@code REPLACE} keeps the column and changes its value, so the width is unchanged. */
  @Test
  void star_replace_is_the_core_parser_not_babel() {
    String sql = "SELECT * REPLACE (UPPER(o_orderstatus) AS o_orderstatus) FROM orders";

    assertThat(babel(sql).getOutputType().getFieldsCount()).isEqualTo(9);
    assertThat(plan(sql, SqlConformanceEnum.DEFAULT, List.of()).getOutputType().getFieldsCount())
        .isEqualTo(9);
  }

  // ------------------------------------------------- identifiers are not conformance's business

  /**
   * D15 pins {@link org.apache.calcite.config.Lex#MYSQL_ANSI} for every dialect, and the Babel
   * parser is given the same one rather than a Lex of its own. This is the measurement behind that
   * choice: quoting, case preservation and case-insensitive matching resolve identically under
   * {@code BABEL} and {@code DEFAULT}, so a host does not get different column resolution by asking
   * for a different dialect.
   */
  @ParameterizedTest
  @ValueSource(
      strings = {
        "SELECT symbol FROM bars",
        "SELECT SYMBOL FROM bars",
        "SELECT \"symbol\" FROM bars",
        "SELECT \"SYMBOL\" FROM bars",
        "SELECT symbol AS \"MixedCase\" FROM bars"
      })
  void identifiers_resolve_the_same_under_babel_as_under_default(String sql) {
    Plan babel = babel(sql);
    Plan standard = plan(sql, SqlConformanceEnum.DEFAULT, List.of());

    assertThat(babel.getOutputType()).isEqualTo(standard.getOutputType());
    assertThat(babel.getRoot()).isEqualTo(standard.getRoot());
  }

  // ------------------------------------------------- accepted by Babel, not carryable

  /**
   * The statements Babel parses and Chalk is not a home for. Before the guard in
   * {@code BabelStatementSupport} these were a raw {@code AssertionError} ({@code CREATE TABLE}:
   * <i>"Was not expecting value 'CREATE_TABLE' for enumeration … SqlKind"</i>) and an
   * {@code UnsupportedOperationException} naming {@code SqlNodeList} ({@code BEGIN}) — an internal
   * error with a correlation id, which tells a host that Chalk is broken rather than that Chalk
   * does not do this.
   *
   * <p>Now each is {@link UnsupportedFeatureException}, the error a library function without an IR
   * mapping gets, carrying {@code PLAN_ERROR_KIND_UNSUPPORTED} and naming the construct.
   */
  @ParameterizedTest
  @ValueSource(
      strings = {
        "CREATE TABLE t (a INTEGER)",
        "BEGIN",
        "COMMIT",
        "ROLLBACK",
        "SHOW ALL",
        "DISCARD ALL",
        "SET search_path = 'public'"
      })
  void a_babel_statement_that_is_not_a_query_is_refused_by_name(String sql) {
    assertThatThrownBy(() -> babel(sql))
        .isInstanceOf(UnsupportedFeatureException.class)
        .hasMessageContaining("is not supported by this planner")
        .hasMessageContaining("Chalk plans queries");
  }

  /**
   * The construct is named, so a host can tell which of its statements was the problem. Five of
   * these report {@code SqlKind.OTHER}, which names nothing, so the refusal falls back to the
   * operator's own name — the only thing in the tree that says what the host wrote.
   */
  @ParameterizedTest
  @CsvSource({
    "CREATE TABLE t (a INTEGER), The CREATE TABLE statement",
    "BEGIN, The BEGIN statement",
    "COMMIT, The COMMIT statement",
    "ROLLBACK, The ROLLBACK statement",
    "SHOW ALL, The SHOW statement",
    "DISCARD ALL, The DISCARD statement",
  })
  void the_refusal_names_the_statement(String sql, String expected) {
    assertThatThrownBy(() -> babel(sql)).hasMessageContaining(expected);
  }

  /** Under every other conformance the same statements never reach the check: the core parser
   * refuses them, exactly as it did before D259. */
  @ParameterizedTest
  @ValueSource(strings = {"CREATE TABLE t (a INTEGER)", "BEGIN", "COMMIT", "ROLLBACK", "SHOW ALL"})
  void the_same_statements_are_parse_failures_under_default(String sql) {
    assertThatThrownBy(() -> plan(sql, SqlConformanceEnum.DEFAULT, List.of()))
        .hasCauseInstanceOf(SqlParseException.class);
  }

  /**
   * And the guard does not reach a statement the <em>core</em> parser accepts and the validator
   * then rejects. An {@code INSERT} is not a query either, but it parses at every conformance and
   * has always failed in the validator; D259 may not change that, so the check is scoped to
   * {@code BABEL} and this is what says so.
   */
  @Test
  void an_insert_still_fails_in_the_validator_under_default() {
    assertThatThrownBy(
            () -> plan("INSERT INTO orders VALUES (1)", SqlConformanceEnum.DEFAULT, List.of()))
        .isNotInstanceOf(UnsupportedFeatureException.class);
  }
}

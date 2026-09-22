package chalk.planner.redact;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.List;
import java.util.regex.Matcher;
import java.util.regex.Pattern;
import java.util.stream.Stream;
import org.apache.calcite.sql.validate.SqlConformanceEnum;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.Arguments;
import org.junit.jupiter.params.provider.MethodSource;
import org.junit.jupiter.params.provider.ValueSource;

/**
 * What a redaction does to a statement (D262, {@code docs/design/37-redacted-sql.md}, ADR 0043).
 *
 * <p>Every salt here is fixed, exactly as §2 requires of the tests and the recorded fixtures, so
 * nothing asserted depends on randomness. Where a whole statement is asserted it is asserted with
 * the pseudonyms <em>masked</em>: what the shape of the output is, is this test's business, and what
 * a particular eight hex characters are is the business of the tests below that compare two of them.
 */
class SqlRedactorTest {
  private static final byte[] SALT = "a fixed salt".getBytes(StandardCharsets.UTF_8);
  private static final byte[] OTHER_SALT = "another fixed salt".getBytes(StandardCharsets.UTF_8);

  /** The marker's shape, which is the same in every one of these assertions. */
  private static final Pattern MARKER =
      Pattern.compile("/\\*REDACTED-([0-9a-f]{8}):([A-Z0-9_]+)\\*/");

  private static RedactionPolicy policy() {
    return RedactionPolicy.of(SALT, RedactionPolicy.Scope.ALL, true);
  }

  private static String redact(String sql) {
    return SqlRedactor.redact(sql, SqlConformanceEnum.DEFAULT, policy()).redactedSql();
  }

  private static SqlRedactor.Result result(String sql) {
    return SqlRedactor.redact(sql, SqlConformanceEnum.DEFAULT, policy());
  }

  /** The same text with every pseudonym masked, so a shape can be asserted whole. */
  private static String masked(String redacted) {
    return MARKER.matcher(redacted).replaceAll("/*REDACTED-xxxxxxxx:$2*/");
  }

  /** A marker with the label it may carry after its type (D286). */
  private static final Pattern LABELLED =
      Pattern.compile("/\\*REDACTED-([0-9a-f]{8}):([A-Z0-9_]+)((?: [^*]+)?)\\*/");

  /** The same text with every pseudonym masked and the labels left as they are. */
  private static String maskedLabelled(String redacted) {
    return LABELLED.matcher(redacted).replaceAll("/*REDACTED-xxxxxxxx:$2$3*/");
  }

  /** The labels a redaction's markers carry, in order, "" for none. */
  private static List<String> labels(String redacted) {
    List<String> labels = new ArrayList<>();
    Matcher matcher = LABELLED.matcher(redacted);
    while (matcher.find()) {
      labels.add(matcher.group(3).strip());
    }
    return labels;
  }

  /** The pseudonyms of a redaction's markers, labelled or not, in order. */
  private static List<String> hexes(String redacted) {
    List<String> hexes = new ArrayList<>();
    Matcher matcher = LABELLED.matcher(redacted);
    while (matcher.find()) {
      hexes.add(matcher.group(1));
    }
    return hexes;
  }

  private static Labels boundValues() {
    return LabelsTest.labels(
        chalk.planner.rpc.v1.RequestContext.newBuilder()
            .addScalars(LabelsTest.scalar("org", LabelsTest.literal(7)))
            .addScalars(LabelsTest.scalar("sym", LabelsTest.literal("BTCUSDT")))
            .addRelations(LabelsTest.list("orgs", 1, 2)));
  }

  /** The types the markers in {@code redacted} name, in order. */
  private static List<String> types(String redacted) {
    List<String> types = new ArrayList<>();
    Matcher matcher = MARKER.matcher(redacted);
    while (matcher.find()) {
      types.add(matcher.group(2));
    }
    return types;
  }

  /** The pseudonyms in {@code redacted}, in order. */
  private static List<String> pseudonyms(String redacted) {
    List<String> hexes = new ArrayList<>();
    Matcher matcher = MARKER.matcher(redacted);
    while (matcher.find()) {
      hexes.add(matcher.group(1));
    }
    return hexes;
  }

  // ------------------------------------------------------------ every literal kind

  static Stream<Arguments> literalKinds() {
    return Stream.of(
        Arguments.of("'BTCUSDT'", "CHAR"),
        Arguments.of("_UTF8'a string'", "CHAR"),
        Arguments.of("U&'\\0041'", "CHAR"),
        Arguments.of("X'0A1B'", "BINARY"),
        Arguments.of("42", "DECIMAL"),
        Arguments.of("1.5", "DECIMAL"),
        Arguments.of("1e3", "DOUBLE"),
        Arguments.of("DATE '2026-01-01'", "DATE"),
        Arguments.of("TIME '12:00:00'", "TIME"),
        Arguments.of("TIMESTAMP '2026-01-01 00:00:00'", "TIMESTAMP"),
        Arguments.of("INTERVAL '3' DAY", "INTERVAL_DAY"),
        Arguments.of("TRUE", "BOOLEAN"),
        Arguments.of("NULL", "NULL"));
  }

  /**
   * One case per literal kind the design names: the whole select item becomes one marker, and the
   * marker names the type Calcite gives the kind, so a reader still sees the shape.
   */
  @ParameterizedTest
  @MethodSource("literalKinds")
  void every_literal_kind_is_replaced_and_names_its_type(String literal, String type) {
    assertThat(masked(redact("SELECT " + literal + " FROM bars")))
        .isEqualTo("SELECT /*REDACTED-xxxxxxxx:" + type + "*/ FROM \"bars\"");
  }

  /** The whole of the design's list in one statement, so nothing is left behind by a scope slip. */
  @Test
  void a_statement_of_every_kind_keeps_no_value_at_all() {
    String redacted =
        redact(
            "SELECT 42, 1.5, 1e3, TRUE, NULL, X'0A1B', _UTF8'ü', U&'\\0041', "
                + "DATE '2026-01-01', TIME '12:00:00', TIMESTAMP '2026-01-01 00:00:00', "
                + "INTERVAL '3' DAY FROM bars WHERE symbol = 'BTCUSDT'");

    assertThat(types(redacted))
        .containsExactly(
            "DECIMAL",
            "DECIMAL",
            "DOUBLE",
            "BOOLEAN",
            "NULL",
            "BINARY",
            "CHAR",
            "CHAR",
            "DATE",
            "TIME",
            "TIMESTAMP",
            "INTERVAL_DAY",
            "CHAR");
    assertThat(masked(redacted))
        .doesNotContain("42", "1.5", "1e3", "0A1B", "ü", "0041", "2026-01-01", "12:00:00", "BTCUSDT");
  }

  // ------------------------------------------------------------ the structural positions

  @Test
  void a_fetch_is_kept() {
    assertThat(redact("SELECT symbol FROM bars LIMIT 10"))
        .isEqualTo("SELECT \"symbol\" FROM \"bars\" FETCH NEXT 10 ROWS ONLY");
  }

  @Test
  void an_offset_is_kept() {
    assertThat(redact("SELECT symbol FROM bars ORDER BY ts OFFSET 7 ROWS FETCH NEXT 5 ROWS ONLY"))
        .isEqualTo(
            "SELECT \"symbol\" FROM \"bars\" ORDER BY \"ts\" OFFSET 7 ROWS FETCH NEXT 5 ROWS ONLY");
  }

  @Test
  void a_window_frame_bound_is_kept() {
    assertThat(
            redact(
                "SELECT AVG(\"close\") OVER (PARTITION BY symbol ORDER BY ts ROWS 19 PRECEDING) "
                    + "FROM bars"))
        .contains("ROWS 19 PRECEDING");
  }

  @Test
  void both_bounds_of_a_between_frame_are_kept() {
    assertThat(
            redact(
                "SELECT AVG(\"close\") OVER (ORDER BY ts ROWS BETWEEN 3 PRECEDING AND 2 FOLLOWING) "
                    + "FROM bars"))
        .contains("ROWS BETWEEN 3 PRECEDING AND 2 FOLLOWING");
  }

  @Test
  void a_group_by_ordinal_is_kept() {
    assertThat(redact("SELECT symbol, COUNT(*) FROM bars GROUP BY 1")).contains("GROUP BY 1");
  }

  @Test
  void an_order_by_ordinal_is_kept_through_its_direction() {
    assertThat(redact("SELECT symbol, COUNT(*) FROM bars GROUP BY 1 ORDER BY 2 DESC"))
        .contains("ORDER BY 2 DESC");
  }

  /**
   * A frame bound written as an interval is a quantity in the vocabulary of the data, not a count of
   * rows, so it is redacted like any other literal (ADR 0043 §2).
   */
  @Test
  void an_interval_frame_bound_is_not_kept() {
    String redacted =
        redact("SELECT SUM(n) OVER (ORDER BY ts RANGE INTERVAL '1' HOUR PRECEDING) FROM bars");

    assertThat(types(redacted)).containsExactly("INTERVAL_HOUR");
    assertThat(redacted).doesNotContain("'1'");
  }

  @Test
  void keep_structural_off_replaces_the_positions_too() {
    RedactionPolicy nothingKept = RedactionPolicy.of(SALT, RedactionPolicy.Scope.ALL, false);
    String redacted =
        SqlRedactor.redact(
                "SELECT symbol FROM bars GROUP BY 1 ORDER BY 1 LIMIT 10",
                SqlConformanceEnum.DEFAULT,
                nothingKept)
            .redactedSql();

    assertThat(types(redacted)).containsExactly("DECIMAL", "DECIMAL", "DECIMAL");
  }

  /** The keywords the grammar carries as literals are syntax, and stay. */
  @Test
  void the_keywords_a_grammar_carries_as_literals_survive() {
    assertThat(masked(redact("SELECT DISTINCT a FROM t NATURAL JOIN u WHERE a = 'x'")))
        .isEqualTo(
            "SELECT DISTINCT \"a\" FROM \"t\" NATURAL INNER JOIN \"u\" "
                + "WHERE \"a\" = /*REDACTED-xxxxxxxx:CHAR*/");
  }

  // ------------------------------------------------------------ the structural hash

  @Test
  void spacing_case_and_quoting_do_not_change_the_structure() {
    assertThat(result("SELECT symbol FROM bars WHERE symbol = 'BTCUSDT' LIMIT 10").structuralHash())
        .isEqualTo(
            result("select symbol from bars where symbol='ETHUSDT' limit 10").structuralHash());
  }

  /** A kept position is a rendering choice and never a seeding one, so it is not in the hash. */
  @Test
  void a_kept_position_is_not_part_of_the_structure() {
    assertThat(result("SELECT symbol FROM bars LIMIT 10").structuralHash())
        .isEqualTo(result("SELECT symbol FROM bars LIMIT 25").structuralHash());
  }

  @Test
  void a_different_shape_is_a_different_structure() {
    assertThat(result("SELECT symbol FROM bars WHERE symbol = 'BTCUSDT'").structuralHash())
        .isNotEqualTo(result("SELECT symbol FROM bars WHERE 'BTCUSDT' = symbol").structuralHash());
  }

  // ------------------------------------------------------------ what correlates and what does not

  @Test
  void one_salt_and_one_shape_give_one_pseudonym() {
    String first = redact("SELECT symbol FROM bars WHERE symbol = 'BTCUSDT'");
    String second = redact("select symbol from bars where symbol = 'BTCUSDT'");

    assertThat(first).isEqualTo(second);
    assertThat(pseudonyms(first)).hasSize(1);
  }

  @Test
  void the_same_value_twice_in_one_statement_is_one_pseudonym() {
    List<String> hexes =
        pseudonyms(redact("SELECT symbol FROM bars WHERE symbol = 'BTCUSDT' OR alias = 'BTCUSDT'"));

    assertThat(hexes).hasSize(2);
    assertThat(hexes.get(0)).isEqualTo(hexes.get(1));
  }

  /** The structure is in the seed, so the same value in another shape does not correlate (§2). */
  @Test
  void the_same_value_in_two_shapes_differs_under_one_salt() {
    String left = redact("SELECT symbol FROM bars WHERE symbol = 'BTCUSDT'");
    String right = redact("SELECT symbol FROM bars WHERE 'BTCUSDT' = symbol");

    assertThat(pseudonyms(left)).isNotEqualTo(pseudonyms(right));
  }

  /** Two salts are two hosts, or two runs of one host that supplied none: nothing correlates. */
  @Test
  void two_salts_give_two_pseudonyms_for_one_statement() {
    String sql = "SELECT symbol FROM bars WHERE symbol = 'BTCUSDT'";
    SqlRedactor.Result left = SqlRedactor.redact(sql, SqlConformanceEnum.DEFAULT, policy());
    SqlRedactor.Result right =
        SqlRedactor.redact(
            sql,
            SqlConformanceEnum.DEFAULT,
            RedactionPolicy.of(OTHER_SALT, RedactionPolicy.Scope.ALL, true));

    assertThat(left.structuralHash()).isEqualTo(right.structuralHash());
    assertThat(pseudonyms(left.redactedSql())).isNotEqualTo(pseudonyms(right.redactedSql()));
  }

  @Test
  void a_redaction_with_no_salt_is_refused() {
    assertThatThrownBy(() -> RedactionPolicy.of(new byte[0], RedactionPolicy.Scope.ALL, true))
        .isInstanceOf(IllegalArgumentException.class)
        .hasMessageContaining("salt");
  }

  // ------------------------------------------------------------ the scope

  @Test
  void the_strings_scope_leaves_the_other_kinds_as_written() {
    String sql = "SELECT symbol FROM bars WHERE symbol = 'BTCUSDT' AND n = 42";
    String strings =
        SqlRedactor.redact(
                sql,
                SqlConformanceEnum.DEFAULT,
                RedactionPolicy.of(SALT, RedactionPolicy.Scope.STRINGS, true))
            .redactedSql();

    assertThat(strings).contains("\"n\" = 42");
    assertThat(strings).doesNotContain("BTCUSDT");
    // Turning the scope down never moves a pseudonym a host has already logged: the structure — and
    // therefore the seed — is over every literal whatever the scope says.
    assertThat(pseudonyms(strings)).isEqualTo(List.of(pseudonyms(redact(sql)).get(0)));
  }

  // ------------------------------------------------------------ Babel

  /** D259's headline construct, redacted: the parser is the one the conformance asks for. */
  @Test
  void a_babel_statement_is_redacted_by_the_babel_parser() {
    SqlRedactor.Result result =
        SqlRedactor.redact(
            "SELECT o_orderkey::VARCHAR FROM orders WHERE o_comment = 'a secret'",
            SqlConformanceEnum.BABEL,
            policy());

    assertThat(result.parsed()).isTrue();
    assertThat(masked(result.redactedSql()))
        .isEqualTo(
            "SELECT (\"o_orderkey\" :: VARCHAR) FROM \"orders\" "
                + "WHERE \"o_comment\" = /*REDACTED-xxxxxxxx:CHAR*/");
  }

  // ------------------------------------------------------------ the token fallback

  @Test
  void text_that_does_not_parse_is_served_by_the_token_fallback() {
    SqlRedactor.Result result =
        SqlRedactor.redact(
            "SELCT * FRM bars WHERE symbol = 'BTCUSDT' AND n = 42",
            SqlConformanceEnum.DEFAULT,
            policy());

    assertThat(result.parsed()).isFalse();
    assertThat(masked(result.redactedSql()))
        .isEqualTo(
            "SELCT * FRM bars WHERE symbol = /*REDACTED-xxxxxxxx:CHAR*/ "
                + "AND n = /*REDACTED-xxxxxxxx:DECIMAL*/");
  }

  /** Without a tree there is no position to trust, so the fallback keeps nothing at all. */
  @Test
  void the_token_fallback_keeps_no_structural_position() {
    String redacted =
        SqlRedactor.redact(
                "SELCT symbol FRM bars GROUP BY 1 ORDER BY 1 LIMIT 10",
                SqlConformanceEnum.DEFAULT,
                policy())
            .redactedSql();

    assertThat(types(redacted)).containsExactly("DECIMAL", "DECIMAL", "DECIMAL");
  }

  static Stream<Arguments> literalTokens() {
    return Stream.of(
        Arguments.of("'a string'", "CHAR"),
        Arguments.of("_UTF8'a string'", "CHAR"),
        Arguments.of("U&'\\0041'", "CHAR"),
        Arguments.of("X'0A1B'", "BINARY"),
        Arguments.of("42", "DECIMAL"),
        Arguments.of("1.5", "DECIMAL"),
        Arguments.of("1e3", "DOUBLE"));
  }

  /** Every literal token kind the design names, through the fallback. */
  @ParameterizedTest
  @MethodSource("literalTokens")
  void the_token_fallback_replaces_every_literal_token(String literal, String type) {
    SqlRedactor.Result result =
        SqlRedactor.redact("SELCT " + literal + " FRM t", SqlConformanceEnum.DEFAULT, policy());

    assertThat(result.parsed()).isFalse();
    assertThat(masked(result.redactedSql()))
        .isEqualTo("SELCT /*REDACTED-xxxxxxxx:" + type + "*/ FRM t");
  }

  /** The keyword of a temporal literal is a keyword; the string beside it is the value. */
  @ParameterizedTest
  @ValueSource(strings = {"DATE", "TIME", "TIMESTAMP", "INTERVAL"})
  void the_token_fallback_replaces_the_string_of_a_temporal_literal(String keyword) {
    String redacted =
        SqlRedactor.redact(
                "SELCT " + keyword + " '2026-01-01' FRM t", SqlConformanceEnum.DEFAULT, policy())
            .redactedSql();

    assertThat(masked(redacted))
        .isEqualTo("SELCT " + keyword + " /*REDACTED-xxxxxxxx:CHAR*/ FRM t");
  }

  /**
   * A comment can hold anything a host wrote into it and is not a token, so the fallback's forms are
   * the token images and nothing between them.
   */
  @Test
  void the_token_fallback_drops_the_comments_between_the_tokens() {
    String redacted =
        SqlRedactor.redact(
                "SELCT a /* ticket 4711 for jane@example.com */ FRM t",
                SqlConformanceEnum.DEFAULT,
                policy())
            .redactedSql();

    assertThat(redacted).isEqualTo("SELCT a FRM t");
  }

  /**
   * The one way a token stream can leak a value: the tail of a string nobody closed lexes as a run
   * of identifiers. Half a token stream is not a boundary anyone can reason about, so nothing of the
   * text is shown (ADR 0043 §4).
   */
  @Test
  void an_unterminated_string_shows_nothing_of_the_text() {
    SqlRedactor.Result result =
        SqlRedactor.redact(
            "SELCT * FRM t WHERE a = 'never closed", SqlConformanceEnum.DEFAULT, policy());

    assertThat(result.parsed()).isFalse();
    assertThat(types(result.redactedSql())).containsExactly("UNKNOWN");
    assertThat(MARKER.matcher(result.redactedSql()).matches()).isTrue();
  }

  /** The same for a block comment the lexer never gets to the end of, which makes it throw. */
  @Test
  void a_text_the_lexer_cannot_finish_becomes_one_marker() {
    SqlRedactor.Result result =
        SqlRedactor.redact("SELCT * FRM t /* never closed", SqlConformanceEnum.DEFAULT, policy());

    assertThat(result.parsed()).isFalse();
    assertThat(types(result.redactedSql())).containsExactly("UNKNOWN");
    assertThat(MARKER.matcher(result.redactedSql()).matches()).isTrue();
  }

  /** Babel's grammar has tokens the core one does not; the fallback lexes with the right one. */
  @Test
  void the_token_fallback_lexes_a_babel_statement_with_babels_own_lexer() {
    SqlRedactor.Result result =
        SqlRedactor.redact(
            "SELCT a::VARCHAR FRM t WHERE b = 'x'", SqlConformanceEnum.BABEL, policy());

    assertThat(result.parsed()).isFalse();
    assertThat(result.redactedSql()).contains(":: VARCHAR");
    assertThat(types(result.redactedSql())).containsExactly("CHAR");
  }

  // ------------------------------------------------------------ determinism

  @Test
  void two_redactions_under_one_salt_are_identical() {
    String sql = "SELECT symbol FROM bars WHERE symbol = 'BTCUSDT' AND ts >= DATE '2026-01-01'";

    assertThat(redact(sql)).isEqualTo(redact(sql));
    assertThat(result(sql).structuralHash()).isEqualTo(result(sql).structuralHash());
  }

  // ---- the labels (D286) ----

  /**
   * A literal that is, in type and value, one of the request's bound values carries the name it was
   * bound under beside its type; every other literal carries none.
   */
  @Test
  void a_literal_that_is_a_bound_value_is_labelled_with_its_name() {
    SqlRedactor.Result result =
        SqlRedactor.redact(
            "SELECT id FROM t WHERE org = 7 AND sym = 'BTCUSDT' AND other = 8 AND note = '7'",
            SqlConformanceEnum.DEFAULT,
            policy(),
            boundValues());

    assertThat(maskedLabelled(result.redactedSql()))
        .isEqualTo(
            "SELECT \"id\" FROM \"t\" WHERE \"org\" = /*REDACTED-xxxxxxxx:DECIMAL @ctx.org*/"
                + " AND \"sym\" = /*REDACTED-xxxxxxxx:CHAR @ctx.sym*/"
                + " AND \"other\" = /*REDACTED-xxxxxxxx:DECIMAL*/"
                + " AND \"note\" = /*REDACTED-xxxxxxxx:CHAR*/");
  }

  /** An element of a folded list is labelled with the list's name — the pushed-query form of a fold. */
  @Test
  void an_in_list_element_is_labelled_with_the_lists_name() {
    SqlRedactor.Result result =
        SqlRedactor.redact(
            "SELECT id FROM t WHERE org_id IN (1, 2, 3)",
            SqlConformanceEnum.DEFAULT,
            policy(),
            boundValues());

    assertThat(labels(result.redactedSql())).containsExactly("@ctx.orgs", "@ctx.orgs", "");
  }

  /** A label is a rendering: the structural hash and every pseudonym are what they are without it. */
  @Test
  void a_label_changes_neither_the_structural_hash_nor_a_pseudonym() {
    String sql = "SELECT id FROM t WHERE org = 7 AND sym = 'BTCUSDT'";
    SqlRedactor.Result plain = SqlRedactor.redact(sql, SqlConformanceEnum.DEFAULT, policy());
    SqlRedactor.Result labelled =
        SqlRedactor.redact(sql, SqlConformanceEnum.DEFAULT, policy(), boundValues());

    assertThat(labelled.structuralHash()).isEqualTo(plain.structuralHash());
    assertThat(hexes(labelled.redactedSql())).isEqualTo(hexes(plain.redactedSql()));
    assertThat(labels(plain.redactedSql())).containsExactly("", "");
    assertThat(labels(labelled.redactedSql())).containsExactly("@ctx.org", "@ctx.sym");
  }

  /** The token fallback labels nothing: without a tree a token has no type to look a value up by. */
  @Test
  void the_token_fallback_labels_nothing() {
    SqlRedactor.Result result =
        SqlRedactor.redact(
            "SELECT id FROM t WHERE org = 7 AND AND", SqlConformanceEnum.DEFAULT, policy(), boundValues());

    assertThat(result.parsed()).isFalse();
    assertThat(labels(result.redactedSql())).allSatisfy(label -> assertThat(label).isEmpty());
  }
}

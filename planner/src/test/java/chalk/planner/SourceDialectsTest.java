package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;

import chalk.ir.v1.DialectProfile;
import chalk.ir.v1.IdentifierQuoting;
import chalk.planner.plan.SourceDialects;
import java.util.List;
import java.util.Set;
import org.apache.calcite.sql.dialect.AnsiSqlDialect;
import org.apache.calcite.sql.dialect.BigQuerySqlDialect;
import org.apache.calcite.sql.dialect.OracleSqlDialect;
import org.junit.jupiter.api.Test;

/**
 * D249 (design 31, ADR 0032): a name that is a Calcite {@code SqlDialect.DatabaseProduct} constant
 * is a preset too, built over the profile's own context exactly as the four tuned ones are; a name
 * that is neither a tuned preset, {@code ansi} nor a product keeps today's behaviour, ANSI.
 */
class SourceDialectsTest {

  private static DialectProfile profileNamed(String dialect) {
    return DialectProfile.newBuilder()
        .setDialect(dialect)
        .setQuoting(IdentifierQuoting.IDENTIFIER_QUOTING_DOUBLE_QUOTE)
        .build();
  }

  @Test
  void oracle_is_calcites_stock_dialect_carrying_the_profiles_quoting() {
    var dialect = SourceDialects.of(profileNamed("oracle"));

    assertThat(dialect).isInstanceOf(OracleSqlDialect.class);
    // Built over contextFor(profile), not Calcite's own default: the profile's quoting took.
    assertThat(dialect.quoteIdentifier("col")).isEqualTo("\"col\"");
  }

  /** D249: compared case-insensitively, and `-` and `_` both reach {@code BIG_QUERY}. */
  @Test
  void a_product_name_is_matched_case_and_separator_insensitively() {
    assertThat(SourceDialects.of(profileNamed("BIG-QUERY"))).isInstanceOf(BigQuerySqlDialect.class);
    assertThat(SourceDialects.of(profileNamed("big_query"))).isInstanceOf(BigQuerySqlDialect.class);
    assertThat(SourceDialects.of(profileNamed("Big-Query"))).isInstanceOf(BigQuerySqlDialect.class);
  }

  @Test
  void ansi_and_an_unmatched_name_are_both_the_plain_ansi_dialect() {
    assertThat(SourceDialects.of(profileNamed("ansi"))).isInstanceOf(AnsiSqlDialect.class);
    assertThat(SourceDialects.of(profileNamed("no-such-dialect"))).isInstanceOf(AnsiSqlDialect.class);
  }

  /**
   * {@code jethro} names a real {@code DatabaseProduct}, but Calcite's own default factory for it
   * throws rather than returning something to build a preset from (V196, ADR 0032) — there is no
   * live connection here for the {@code JethroInfoCache} a real one needs. This sidecar falls back
   * exactly as it does for a name it does not recognise at all, rather than construct a dialect with
   * no operator support behind it.
   */
  @Test
  void jethro_falls_back_to_ansi_because_calcite_itself_refuses_a_context_free_one() {
    assertThat(SourceDialects.of(profileNamed("jethro"))).isInstanceOf(AnsiSqlDialect.class);
  }

  @Test
  void presets_list_the_four_tuned_ones_with_postgres_as_postgresqls_alias() {
    List<SourceDialects.DialectPreset> presets = SourceDialects.presets();

    assertThat(presets)
        .filteredOn(SourceDialects.DialectPreset::tuned)
        .extracting(SourceDialects.DialectPreset::name)
        .containsExactlyInAnyOrder("sqlite", "duckdb", "postgresql", "ansi");
    assertThat(presets)
        .filteredOn(preset -> preset.name().equals("postgresql"))
        .extracting(SourceDialects.DialectPreset::aliases)
        .containsExactly(List.of("postgres"));
  }

  // ------------------------------------------- can the dialect write an offset (F121)

  /**
   * Measured rather than listed: the question is put to the dialect by unparsing a select that
   * carries one and looking for the number. Calcite's SQL Server dialect spells a bound
   * {@code TOP (n)}, which has no skip, and writes nothing at all where the offset was — so a
   * pushed {@code OFFSET … FETCH} would come back as the first n rows rather than the n after the
   * offset. {@code PushdownGate.supportsOffset} is false for it and the offset stays local.
   */
  @Test
  void the_sql_server_dialect_writes_no_offset_and_every_other_one_does() {
    assertThat(SourceDialects.rendersOffset(SourceDialects.of(profileNamed("mssql")))).isFalse();

    for (String named : List.of("sqlite", "duckdb", "postgresql", "ansi", "oracle", "mysql", "big_query")) {
      assertThat(SourceDialects.rendersOffset(SourceDialects.of(profileNamed(named))))
          .as(named)
          .isTrue();
    }
  }

  @Test
  void presets_list_every_other_product_as_untuned_and_never_jethro() {
    List<SourceDialects.DialectPreset> presets = SourceDialects.presets();
    List<String> names = presets.stream().map(SourceDialects.DialectPreset::name).toList();

    assertThat(names).contains("oracle", "mssql", "big_query", "unknown");
    assertThat(names).doesNotContain("jethro");
    assertThat(presets)
        .filteredOn(preset -> Set.of("oracle", "mssql", "big_query").contains(preset.name()))
        .hasSize(3)
        .allMatch(preset -> !preset.tuned())
        .allMatch(preset -> preset.databaseProduct().equals(preset.name().toUpperCase(java.util.Locale.ROOT)));
  }
}

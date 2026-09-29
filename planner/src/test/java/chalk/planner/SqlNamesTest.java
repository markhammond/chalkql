package chalk.planner;

import static org.assertj.core.api.Assertions.assertThatCode;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

import chalk.ir.v1.DialectProfile;
import chalk.planner.catalog.CatalogRegistry;
import org.junit.jupiter.api.Test;

/**
 * D318: a source's conformance and libraries are Calcite's constant names, and a name this build of
 * Calcite does not have is refused at registration rather than read as the dialect's own default.
 */
class SqlNamesTest {
  @Test
  void a_profile_naming_calcites_levels_and_libraries_registers() {
    DialectProfile profile =
        TestCatalogs.postgresProfile().toBuilder()
            .setConformance("LENIENT")
            .addLibraries("POSTGRESQL")
            .addLibraries("STANDARD")
            .build();

    assertThatCode(() -> register(profile)).doesNotThrowAnyException();
  }

  @Test
  void an_unknown_conformance_on_a_profile_is_refused_with_the_known_ones() {
    DialectProfile profile = TestCatalogs.postgresProfile().toBuilder().setConformance("POSTGRES_16").build();

    assertThatThrownBy(() -> register(profile))
        .hasMessageContaining("dialect_profile")
        .hasMessageContaining("'POSTGRES_16' is not a conformance level this planner knows")
        .hasMessageContaining("LENIENT");
  }

  @Test
  void an_unknown_library_on_a_profile_is_refused_with_the_known_ones() {
    DialectProfile profile = TestCatalogs.postgresProfile().toBuilder().addLibraries("postgresql").build();

    assertThatThrownBy(() -> register(profile))
        .hasMessageContaining("'postgresql' is not a library this planner knows")
        .hasMessageContaining("POSTGRESQL");
  }

  private static void register(DialectProfile profile) {
    new CatalogRegistry()
        .register(TestCatalogs.withRemote("db", TestCatalogs.fullSqlCapabilities().build(), profile));
  }
}

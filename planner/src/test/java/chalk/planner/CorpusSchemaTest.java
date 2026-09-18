package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;
import static org.junit.jupiter.api.Assumptions.assumeTrue;

import chalk.ir.v1.CatalogContext;
import chalk.ir.v1.Column;
import chalk.ir.v1.Schema;
import chalk.ir.v1.Table;
import java.io.IOException;
import java.nio.file.Files;
import java.nio.file.Path;
import org.junit.jupiter.api.Test;

/**
 * The Java fixture catalog and the one the .NET client actually pushes must be the same catalog,
 * column for column and index for index. If they drift, the Java golden plans and the .NET recorded
 * digests silently stop describing the same thing, and every cross-toolchain comparison downstream
 * becomes meaningless.
 *
 * <p>{@code Chalk.CorpusTool record} writes {@code corpus/schemas/corpus.binpb} from
 * {@code Chalk.TestKit.CorpusFixture} (docs/design/05-testing.md §4). This test is what keeps the
 * Java copy honest without needing .NET to run.
 *
 * <p>Statistics are compared for their <em>presence</em>, not their values: since M2 the client
 * computes them from the generated fixture data (exact distinct counts, null counts, a min and a max
 * per column), and a hand-written Java copy of numbers derived from a random generator would be a
 * transcription nobody could maintain. {@code TestCatalogs.corpus()} therefore plans against the
 * recorded catalog itself, and what this test protects is the part a person writes down.
 */
class CorpusSchemaTest {

  @Test
  void the_java_fixture_matches_the_catalog_the_client_pushes() throws IOException {
    Path recorded = CorpusQueries.corpusDir().resolve("schemas/corpus.binpb");
    assumeTrue(
        Files.exists(recorded),
        "corpus/schemas/corpus.binpb is written by scripts/record-plans.sh; nothing to compare yet");

    CatalogContext fromClient = CatalogContext.parseFrom(Files.readAllBytes(recorded));

    assertThat(withoutStatistics(fromClient))
        .as("chalk.planner.TestCatalogs.declared() vs Chalk.TestKit.CorpusFixture")
        .isEqualTo(withoutStatistics(TestCatalogs.declared()));
  }

  @Test
  void the_client_computes_statistics_for_every_column() throws IOException {
    Path recorded = CorpusQueries.corpusDir().resolve("schemas/corpus.binpb");
    assumeTrue(Files.exists(recorded), "nothing recorded yet");

    CatalogContext fromClient = CatalogContext.parseFrom(Files.readAllBytes(recorded));
    for (Schema schema : fromClient.getSchemasList()) {
      for (Table table : schema.getTablesList()) {
        for (Column column : table.getColumnsList()) {
          assertThat(column.hasStatistics())
              .as("%s.%s has statistics", table.getName(), column.getName())
              .isTrue();
        }
      }
    }
  }

  /** The declared shape: everything but the computed per-column statistics. */
  private static CatalogContext withoutStatistics(CatalogContext catalog) {
    CatalogContext.Builder stripped = catalog.toBuilder();
    for (Schema.Builder schema : stripped.getSchemasBuilderList()) {
      for (Table.Builder table : schema.getTablesBuilderList()) {
        for (Column.Builder column : table.getColumnsBuilderList()) {
          column.clearStatistics();
        }
      }
    }

    return stripped.build();
  }
}

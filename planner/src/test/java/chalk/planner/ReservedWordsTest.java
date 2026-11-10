package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;

import chalk.planner.plan.SqlConfigs;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.TreeSet;
import org.apache.calcite.sql.parser.SqlAbstractParserImpl;
import org.apache.calcite.sql.validate.SqlConformanceEnum;
import org.junit.jupiter.api.Test;

/**
 * The list of reserved words the policy compiler quotes names by, and the guide links to, is the
 * one the planner's parser reserves under the default conformance: a word missing from it would be
 * written unquoted and fail to parse, and one too many would only quote a name that did not need it.
 */
class ReservedWordsTest {

  private static final Path LIST =
      Path.of(System.getProperty("chalk.projectDir", "."))
          .toAbsolutePath()
          .normalize()
          .getParent()
          .resolve("dotnet/src/Chalk.Entitlements.Tenancy/ReservedWords.txt");

  @Test
  void the_checked_in_list_is_the_parsers() throws Exception {
    SqlAbstractParserImpl.Metadata metadata =
        SqlConfigs.parser(SqlConformanceEnum.DEFAULT)
            .parserFactory()
            .getParser(new java.io.StringReader(""))
            .getMetadata();
    TreeSet<String> reserved = new TreeSet<>();
    for (String token : metadata.getTokens()) {
      if (metadata.isReservedWord(token) && token.matches("[A-Z_][A-Z0-9_]*")) {
        reserved.add(token);
      }
    }

    TreeSet<String> listed = new TreeSet<>();
    for (String line : Files.readAllLines(LIST)) {
      if (!line.isBlank() && !line.startsWith("#")) {
        listed.add(line.strip());
      }
    }

    assertThat(listed).containsExactlyElementsOf(reserved);
  }
}

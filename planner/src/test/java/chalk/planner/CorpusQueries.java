package chalk.planner;

import chalk.planner.plan.SqlConfigs;
import java.io.IOException;
import java.io.UncheckedIOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;
import java.util.stream.Stream;
import java.util.Locale;
import org.apache.calcite.sql.fun.SqlLibrary;
import org.apache.calcite.sql.validate.SqlConformanceEnum;

/**
 * Reads {@code corpus/queries/m1}. The Java tests plan exactly what the planner sees, which for the
 * `@name` and `$n` styles is the rewritten twin in {@code corpus/queries/m1-rewritten} (D27, V13).
 */
public final class CorpusQueries {
  private CorpusQueries() {}

  /**
   * One corpus query: its file name, the SQL the planner sees, the {@code -- expect:} lines and the
   * SQL dialect its {@code -- conformance:} header asks for (D34; {@code DEFAULT} when it has none).
   */
  public record Query(
      String name,
      String sql,
      List<String> expectations,
      SqlConformanceEnum conformance,
      List<SqlLibrary> libraries) {}

  private static final Path REPO_ROOT =
      Path.of(System.getProperty("chalk.projectDir", ".")).toAbsolutePath().normalize().getParent();

  public static Path corpusDir() {
    return REPO_ROOT.resolve("corpus");
  }

  /** Every query in {@code corpus/queries/m1}, ordered by file name. */
  public static List<Query> all() {
    return load(corpusDir().resolve("queries/m1"), corpusDir().resolve("queries/m1-rewritten"));
  }

  /** Every query in {@code corpus/queries/m2} — the index corpus (D40). */
  public static List<Query> m2() {
    return load(corpusDir().resolve("queries/m2"), null);
  }

  /** Every query in {@code corpus/queries/m3-joins} — the join corpus (D46). */
  public static List<Query> m3() {
    return load(corpusDir().resolve("queries/m3-joins"), null);
  }

  /** Every query in {@code corpus/queries/m1-errors}. */
  /** The window corpus (D53). */
  public static List<Query> m4() {
    return load(corpusDir().resolve("queries/m4-window"), null);
  }

  /** The windows-II corpus (D55–D60, D66–D68). */
  public static List<Query> m5() {
    return load(corpusDir().resolve("queries/m5-windows-ii"), null);
  }

  /** The set-operation corpus (D69). */
  public static List<Query> m6() {
    return load(corpusDir().resolve("queries/m6-setop"), null);
  }

  /** The user-defined-function corpus (D77–D81). */
  public static List<Query> m6udf() {
    return load(corpusDir().resolve("queries/m6-udf"), null);
  }

  public static List<Query> errors() {
    return load(corpusDir().resolve("queries/m1-errors"), null);
  }

  private static List<Query> load(Path dir, Path rewrittenDir) {
    List<Query> queries = new ArrayList<>();
    try (Stream<Path> files = Files.list(dir)) {
      for (Path file : files.filter(p -> p.getFileName().toString().endsWith(".sql")).sorted().toList()) {
        String name = file.getFileName().toString().replace(".sql", "");
        String text = Files.readString(file, StandardCharsets.UTF_8);
        Path rewritten = rewrittenDir == null ? null : rewrittenDir.resolve(name + ".sql");
        String sql =
            rewritten != null && Files.exists(rewritten)
                ? Files.readString(rewritten, StandardCharsets.UTF_8)
                : stripHeaders(text);
        queries.add(
            new Query(
                name,
                sql.strip(),
                expectationsOf(text),
                conformanceOf(name, text),
                librariesOf(name, text)));
      }
    } catch (IOException e) {
      throw new UncheckedIOException("reading " + dir, e);
    }
    return queries;
  }

  /** Every header line is a comment; only the statement is left. */
  private static String stripHeaders(String text) {
    StringBuilder sql = new StringBuilder();
    for (String line : text.split("\n", -1)) {
      if (!line.startsWith("--")) {
        sql.append(line).append('\n');
      }
    }
    return sql.toString();
  }

  /** The {@code -- conformance: <NAME>} header, naming a {@link SqlConformanceEnum} constant. */
  private static SqlConformanceEnum conformanceOf(String name, String text) {
    for (String line : text.split("\n", -1)) {
      if (line.startsWith("-- conformance:")) {
        String wanted = line.substring("-- conformance:".length()).strip();
        try {
          return SqlConformanceEnum.valueOf(wanted);
        } catch (IllegalArgumentException e) {
          throw new IllegalArgumentException(
              name + ": unknown '-- conformance:' level '" + wanted + "'", e);
        }
      }
    }
    return SqlConfigs.DEFAULT_CONFORMANCE;
  }

  /**
   * The {@code -- libraries: <NAME>[, <NAME>]} header, naming Calcite's dialect function libraries
   * (D60). Absent means the standard operators alone.
   */
  private static List<SqlLibrary> librariesOf(String name, String text) {
    for (String line : text.split("\n", -1)) {
      if (!line.startsWith("-- libraries:")) {
        continue;
      }

      List<SqlLibrary> libraries = new ArrayList<>();
      for (String part : line.substring("-- libraries:".length()).split(",")) {
        String wanted = part.strip().toUpperCase(Locale.ROOT);
        if (wanted.isEmpty()) {
          continue;
        }

        try {
          libraries.add(SqlLibrary.valueOf(wanted));
        } catch (IllegalArgumentException e) {
          throw new IllegalArgumentException(
              name + ": unknown '-- libraries:' entry '" + wanted + "'", e);
        }
      }

      return List.copyOf(libraries);
    }

    return List.of();
  }

  private static List<String> expectationsOf(String text) {
    List<String> expectations = new ArrayList<>();
    for (String line : text.split("\n", -1)) {
      if (line.startsWith("-- expect:")) {
        expectations.add(line.substring("-- expect:".length()).strip());
      }
    }
    return expectations;
  }
}

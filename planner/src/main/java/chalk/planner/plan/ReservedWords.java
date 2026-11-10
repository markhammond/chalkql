package chalk.planner.plan;

import java.util.ArrayList;
import java.util.List;
import java.util.Locale;
import java.util.Set;
import java.util.function.Function;
import java.util.regex.Matcher;
import java.util.regex.Pattern;
import org.apache.calcite.sql.SqlCall;
import org.apache.calcite.sql.SqlIdentifier;
import org.apache.calcite.sql.SqlNode;
import org.apache.calcite.sql.SqlSyntax;
import org.apache.calcite.sql.parser.SqlAbstractParserImpl;
import org.apache.calcite.sql.parser.SqlParseException;
import org.apache.calcite.sql.parser.SqlParser;
import org.apache.calcite.sql.parser.SqlParserPos;
import org.apache.calcite.sql.util.SqlBasicVisitor;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * Names that SQL reserves, used by a host as names. A host's schema comes from types and databases
 * it does not control, so a reserved word such as {@code Open}, {@code At} or {@code national} is
 * taken as the name it is wherever that is unambiguous, and the host never renames anything:
 *
 * <ul>
 *   <li>A parse that stops at a reserved word naming a schema, table or column is given the word
 *       quoted, in the catalog's spelling, and parsed again. Text that parses today is never
 *       touched, so nothing that runs changes meaning.
 *   <li>A reserved word that names nothing is refused saying so and how to quote it, ahead of the
 *       parser's own message, whose list of expected tokens is cut short.
 *   <li>A word SQL reads as a function without parentheses, such as {@code USER} or {@code
 *       CURRENT_DATE}, parses wherever it is written, so it never reaches a column of that name.
 *       Where such a column is in scope the text is refused as ambiguous rather than read either
 *       way.
 * </ul>
 */
public final class ReservedWords {

  /** The words SQL reads as a call without parentheses, which parse whatever the scope holds. */
  private static final Set<String> NILADIC =
      Set.of(
          "USER",
          "CURRENT_USER",
          "SESSION_USER",
          "SYSTEM_USER",
          "CURRENT_ROLE",
          "CURRENT_PATH",
          "CURRENT_CATALOG",
          "CURRENT_SCHEMA",
          "CURRENT_DEFAULT_TRANSFORM_GROUP",
          "CURRENT_DATE",
          "CURRENT_TIME",
          "CURRENT_TIMESTAMP",
          "LOCALTIME",
          "LOCALTIMESTAMP");

  /** At most this many names are quoted in one text: each is one parse more. */
  private static final int MAX_QUOTED = 64;

  /** How many of the parser's expected tokens a message keeps. */
  private static final int EXPECTED_SHOWN = 8;

  private static final String EXPECTING = "Was expecting one of:";

  private static final Pattern ENCOUNTERED =
      Pattern.compile("Encountered \"(?:[^\"]*\\s)?([A-Za-z_][A-Za-z0-9_]*)\"");

  private static final Pattern NEAR_KEYWORD = Pattern.compile("near the keyword '[A-Za-z_]+'");

  private ReservedWords() {}

  /** One parse of a text. */
  @FunctionalInterface
  public interface Parse<T> {
    T parse(String text) throws SqlParseException;
  }

  /** What a parse gave, and the text it gave it for: the host's, with any reserved name quoted. */
  public record Parsed<T>(T node, String text) {}

  /** A word of the text, at {@code [start, end)}. */
  private record Word(String text, int start, int end) {}

  /**
   * Parses {@code text}, quoting each reserved word the parse stops at that {@code names} knows.
   *
   * @param names a name's spelling in the catalog, matched without regard to case, or null where
   *     the catalog has no such name
   * @throws SqlParseException where the text does not parse for any other reason: with the reserved
   *     word it stopped at named, if any, and the expected tokens cut short
   */
  public static <T> Parsed<T> parse(
      String text, SqlParser.Config config, Function<String, @Nullable String> names, Parse<T> parse)
      throws SqlParseException {
    String current = text;
    for (int quoted = 0; ; quoted++) {
      try {
        return new Parsed<>(parse.parse(current), current);
      } catch (SqlParseException e) {
        List<Word> reserved = reservedNear(current, e, config);
        String next = quoted < MAX_QUOTED ? quoteNamed(current, reserved, names) : null;
        if (next == null) {
          throw explained(e, reserved.isEmpty() ? null : reserved.get(0).text());
        }
        current = next;
      }
    }
  }

  /** The text with the first of these words the catalog names quoted, or null where it names none. */
  private static @Nullable String quoteNamed(
      String text, List<Word> reserved, Function<String, @Nullable String> names) {
    for (Word word : reserved) {
      String spelling = names.apply(word.text());
      if (spelling != null) {
        return text.substring(0, word.start()) + quote(spelling) + text.substring(word.end());
      }
    }
    return null;
  }

  /**
   * Refuses a text in which a word SQL reads as a call without parentheses also names a column in
   * scope: the parse took the call, so the column would never be read.
   *
   * @param columns a column's spelling in scope, matched without regard to case, or null
   * @return the refusal, or null where there is none
   */
  public static @Nullable String ambiguity(SqlNode node, Function<String, @Nullable String> columns) {
    String[] found = new String[1];
    node.accept(
        new SqlBasicVisitor<Void>() {
          @Override
          public Void visit(SqlIdentifier id) {
            // Calcite reads an unquoted one-part name such as `user` as the function before it
            // tries the scope's columns, so the column of that name is never what it reads.
            if (found[0] == null && id.names.size() == 1 && !id.isComponentQuoted(0)) {
              found[0] = refusal(id.names.get(0), id.getParserPosition(), columns);
            }
            return null;
          }

          @Override
          public Void visit(SqlCall call) {
            if (found[0] == null
                && call.getOperator().getSyntax() == SqlSyntax.FUNCTION_ID
                && call.operandCount() == 0) {
              found[0] = refusal(call.getOperator().getName(), call.getParserPosition(), columns);
            }
            return found[0] == null ? super.visit(call) : null;
          }
        });
    return found[0];
  }

  private static @Nullable String refusal(
      String word, SqlParserPos pos, Function<String, @Nullable String> columns) {
    String function = word.toUpperCase(Locale.ROOT);
    String column = NILADIC.contains(function) ? columns.apply(word) : null;
    if (column == null) {
      return null;
    }
    return "'"
        + column
        + "' at line "
        + pos.getLineNum()
        + ", column "
        + pos.getColumnNum()
        + " is a column here and also SQL's "
        + function
        + ", which is read as the function wherever it is written unquoted: quote it as \""
        + column
        + "\" to read the column";
  }

  /** The parser's message for {@code e}, naming the reserved word it stopped at, expected tokens cut short. */
  public static SqlParseException explained(SqlParseException e, @Nullable String reserved) {
    String message = shortened(e.getMessage() == null ? "" : e.getMessage());
    if (reserved != null) {
      message =
          "'" + reserved + "' is a reserved word: to use it as a name, quote it as \"" + reserved
              + "\". " + message;
    }
    return new SqlParseException(
        message, e.getPos(), e.getExpectedTokenSequences(), e.getTokenImages(), e.getCause());
  }

  /** {@code name} as a quoted identifier. */
  public static String quote(String name) {
    return "\"" + name.replace("\"", "\"\"") + "\"";
  }

  /**
   * The reserved word the parse stopped at, if it stopped at one: the word it says it encountered,
   * or, where it says only that the syntax is wrong near a keyword, the word after that keyword,
   * past any opening parenthesis, as with {@code Open} in {@code x AND (Open)}.
   */
  private static List<Word> reservedNear(String text, SqlParseException e, SqlParser.Config config) {
    SqlParserPos pos = e.getPos();
    String message = e.getMessage() == null ? "" : e.getMessage();
    if (pos == null || pos.getLineNum() < 1 || pos.getColumnNum() < 1) {
      return List.of();
    }
    int at = offset(text, pos.getLineNum(), pos.getColumnNum());
    if (at < 0 || at >= text.length()) {
      return List.of();
    }

    Word word = null;
    Matcher encountered = ENCOUNTERED.matcher(message);
    if (encountered.find()) {
      word = find(text, encountered.group(1), at);
    } else if (NEAR_KEYWORD.matcher(message).find()) {
      Word keyword = wordAt(text, at);
      int next = keyword == null ? at : keyword.end();
      while (next < text.length()
          && (Character.isWhitespace(text.charAt(next)) || text.charAt(next) == '(')) {
        next++;
      }
      word = next < text.length() ? wordAt(text, next) : null;
    }

    SqlAbstractParserImpl.Metadata metadata =
        config.parserFactory().getParser(new java.io.StringReader("")).getMetadata();
    return word != null && metadata.isReservedWord(word.text().toUpperCase(Locale.ROOT))
        ? List.of(word)
        : List.of();
  }

  /** {@code word} as a whole word at or after {@code from}, matched without regard to case. */
  private static @Nullable Word find(String text, String word, int from) {
    Matcher matcher =
        Pattern.compile("(?<![A-Za-z0-9_])" + Pattern.quote(word) + "(?![A-Za-z0-9_])", Pattern.CASE_INSENSITIVE)
            .matcher(text);
    return matcher.find(from) ? new Word(matcher.group(), matcher.start(), matcher.end()) : null;
  }

  /** The word that starts at {@code at}, or null where none does. */
  private static @Nullable Word wordAt(String text, int at) {
    char c = text.charAt(at);
    if (!(Character.isLetter(c) || c == '_')) {
      return null;
    }
    int end = at + 1;
    while (end < text.length() && (Character.isLetterOrDigit(text.charAt(end)) || text.charAt(end) == '_')) {
      end++;
    }
    return new Word(text.substring(at, end), at, end);
  }

  /** The offset of a 1-based line and column, or -1. */
  private static int offset(String text, int line, int column) {
    int at = 0;
    for (int l = 1; l < line; l++) {
      at = text.indexOf('\n', at);
      if (at < 0) {
        return -1;
      }
      at++;
    }
    return at + column - 1;
  }

  /** The message with the parser's list of expected tokens cut to its first few. */
  static String shortened(String message) {
    int expecting = message.indexOf(EXPECTING);
    if (expecting < 0) {
      return message;
    }
    String[] lines = message.substring(expecting + EXPECTING.length()).split("\n");
    List<String> tokens = new ArrayList<>();
    for (String line : lines) {
      if (!line.isBlank()) {
        tokens.add(line.strip());
      }
    }
    if (tokens.size() <= EXPECTED_SHOWN) {
      return message;
    }
    StringBuilder text = new StringBuilder(message.substring(0, expecting)).append(EXPECTING);
    for (String token : tokens.subList(0, EXPECTED_SHOWN)) {
      text.append("\n    ").append(token);
    }
    return text.append("\n    and ").append(tokens.size() - EXPECTED_SHOWN).append(" more").toString();
  }
}

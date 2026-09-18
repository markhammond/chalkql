package chalk.planner.redact;

import java.io.StringReader;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import org.apache.calcite.sql.validate.SqlConformanceEnum;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * The fallback for text that does not parse (D262, {@code docs/design/37-redacted-sql.md} §1).
 *
 * <p>Without a tree there is no position to trust, so this keeps <b>nothing</b>: every literal token
 * — quoted, prefixed, unicode and binary strings, unsigned, decimal and approximate numerics — is
 * replaced, and a {@code DATE}/{@code TIME}/{@code TIMESTAMP}/{@code INTERVAL} literal is covered by
 * the same rule, because its keyword is a keyword and its value is a quoted string token.
 *
 * <p>It tokenises with <b>the parser's own token manager</b>, and with the one that matches the
 * request's conformance: Babel's grammar has tokens the core grammar does not, and lexing a Babel
 * statement with the core lexer would stop at the first of them.
 *
 * <p>The forms are built by joining the token images with a single space rather than by keeping the
 * original spacing, and that is a safety property and not a formatting one: the text between two
 * tokens is where a <em>comment</em> lives, a comment is not a token, and a comment can hold
 * anything a host wrote into it.
 *
 * <p>If the lexer cannot finish, nothing of the text is shown at all: the whole statement becomes
 * one {@code UNKNOWN} marker. Half a token stream is not a boundary anyone can reason about, and the
 * conservative direction is the only safe one here. Two things count as "cannot finish": the token
 * manager throwing (an unterminated block comment), and a <em>stray quote</em> — a {@code '} the
 * lexer emitted on its own, which happens when a string literal was never closed and whose tail
 * would otherwise be lexed as a run of identifiers and printed verbatim. That is the one way a
 * token stream can leak a value, and it is measured rather than assumed (ADR 0043 §4).
 */
final class TokenRedactor {
  /** Calcite's own name for the type each literal token stands for, so one vocabulary is used. */
  private static final Map<String, String> LITERAL_TOKENS =
      Map.ofEntries(
          Map.entry("QUOTED_STRING", "CHAR"),
          Map.entry("PREFIXED_STRING_LITERAL", "CHAR"),
          Map.entry("UNICODE_STRING_LITERAL", "CHAR"),
          Map.entry("C_STYLE_ESCAPED_STRING_LITERAL", "CHAR"),
          Map.entry("BIG_QUERY_QUOTED_STRING", "CHAR"),
          Map.entry("BIG_QUERY_DOUBLE_QUOTED_STRING", "CHAR"),
          Map.entry("UNICODE_QUOTED_ESCAPE_CHAR", "CHAR"),
          Map.entry("BINARY_STRING_LITERAL", "BINARY"),
          Map.entry("UNSIGNED_INTEGER_LITERAL", "DECIMAL"),
          Map.entry("DECIMAL_NUMERIC_LITERAL", "DECIMAL"),
          Map.entry("APPROX_NUMERIC_LITERAL", "DOUBLE"));

  /** What a text this lexer could not finish becomes. */
  private static final String UNKNOWN = "UNKNOWN";

  private TokenRedactor() {}

  /** One token, and the type its marker names when it is a literal. */
  private record Piece(String image, @Nullable String type) {}

  static SqlRedactor.Result redact(String sql, SqlConformanceEnum conformance, RedactionPolicy policy) {
    List<Piece> pieces = lex(sql, conformance);

    StringBuilder structural = new StringBuilder();
    for (Piece piece : pieces) {
      append(
          structural,
          piece.type() == null ? piece.image() : Pseudonyms.structuralMarker(piece.type()));
    }

    byte[] structuralHash = Pseudonyms.structuralHash(structural.toString());
    Pseudonyms pseudonyms = Pseudonyms.forStatement(structuralHash, policy);

    StringBuilder redacted = new StringBuilder();
    for (Piece piece : pieces) {
      String type = piece.type();
      boolean replaced =
          type != null
              && (policy.scope() == RedactionPolicy.Scope.ALL
                  || "CHAR".equals(type)
                  || "BINARY".equals(type)
                  || UNKNOWN.equals(type));
      append(redacted, replaced ? pseudonyms.marker(type, piece.image()) : piece.image());
    }

    return new SqlRedactor.Result(redacted.toString(), structuralHash, false);
  }

  private static void append(StringBuilder out, String piece) {
    if (out.length() > 0) {
      out.append(' ');
    }
    out.append(piece);
  }

  /**
   * A quote the lexer emitted on its own, which it only ever does when a quoted run was never
   * closed — and the tail of an unterminated string is a value, lexed as a run of identifiers. The
   * whole text becomes one marker when one of these appears, for the reason the class comment gives.
   */
  private static boolean isStrayQuote(String image) {
    return "'".equals(image) || "\"".equals(image) || "`".equals(image);
  }

  private static List<Piece> lex(String sql, SqlConformanceEnum conformance) {
    List<Piece> pieces = new ArrayList<>();
    Lexer lexer =
        conformance == SqlConformanceEnum.BABEL ? new BabelLexer(sql) : new CoreLexer(sql);
    try {
      for (Lexeme token = lexer.next(); token != null; token = lexer.next()) {
        if (isStrayQuote(token.image())) {
          return List.of(new Piece(sql, UNKNOWN));
        }
        pieces.add(new Piece(token.image(), LITERAL_TOKENS.get(lexer.nameOf(token.kind()))));
      }
    } catch (Error lexical) {
      // TokenMgrError, whose class differs between the two generated parsers. Anything else — an
      // OutOfMemoryError, a StackOverflowError — is not this code's to swallow.
      if (!"TokenMgrError".equals(lexical.getClass().getSimpleName())) {
        throw lexical;
      }
      return List.of(new Piece(sql, UNKNOWN));
    }
    return pieces;
  }

  // ---- the two token managers ----

  private record Lexeme(int kind, String image) {}

  private interface Lexer {
    /** The next token, or null at the end of the input. */
    @Nullable Lexeme next();

    /** The grammar's own name for a token kind, e.g. {@code QUOTED_STRING}. */
    String nameOf(int kind);
  }

  /**
   * Token kinds by the grammar's name for them, read out of the generated {@code tokenImage} array.
   * The numbers differ between the core and Babel grammars and the names do not, so the names are
   * what this code is written against.
   */
  private static Map<Integer, String> namesOf(String[] tokenImage) {
    Map<Integer, String> names = new HashMap<>();
    for (int kind = 0; kind < tokenImage.length; kind++) {
      String image = tokenImage[kind];
      if (image.length() > 2 && image.charAt(0) == '<' && image.charAt(image.length() - 1) == '>') {
        names.put(kind, image.substring(1, image.length() - 1));
      }
    }
    return names;
  }

  private static final class CoreLexer implements Lexer {
    private static final Map<Integer, String> NAMES =
        namesOf(org.apache.calcite.sql.parser.impl.SqlParserImplConstants.tokenImage);

    private final org.apache.calcite.sql.parser.impl.SqlParserImplTokenManager tokens;

    CoreLexer(String sql) {
      tokens =
          new org.apache.calcite.sql.parser.impl.SqlParserImplTokenManager(
              new org.apache.calcite.sql.parser.impl.SimpleCharStream(new StringReader(sql)));
    }

    @Override
    public @Nullable Lexeme next() {
      org.apache.calcite.sql.parser.impl.Token token = tokens.getNextToken();
      return token == null
              || token.kind == org.apache.calcite.sql.parser.impl.SqlParserImplConstants.EOF
          ? null
          : new Lexeme(token.kind, token.image);
    }

    @Override
    public String nameOf(int kind) {
      return NAMES.getOrDefault(kind, "");
    }
  }

  private static final class BabelLexer implements Lexer {
    private static final Map<Integer, String> NAMES =
        namesOf(org.apache.calcite.sql.parser.babel.SqlBabelParserImplConstants.tokenImage);

    private final org.apache.calcite.sql.parser.babel.SqlBabelParserImplTokenManager tokens;

    BabelLexer(String sql) {
      tokens =
          new org.apache.calcite.sql.parser.babel.SqlBabelParserImplTokenManager(
              new org.apache.calcite.sql.parser.babel.SimpleCharStream(new StringReader(sql)));
    }

    @Override
    public @Nullable Lexeme next() {
      org.apache.calcite.sql.parser.babel.Token token = tokens.getNextToken();
      return token == null
              || token.kind
                  == org.apache.calcite.sql.parser.babel.SqlBabelParserImplConstants.EOF
          ? null
          : new Lexeme(token.kind, token.image);
    }

    @Override
    public String nameOf(int kind) {
      return NAMES.getOrDefault(kind, "");
    }
  }
}

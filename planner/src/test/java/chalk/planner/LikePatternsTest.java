package chalk.planner;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

import chalk.planner.plan.LikePatterns;
import org.junit.jupiter.api.Test;

/**
 * D312, D313 — the escape rules, which are Calcite's runtime's and the SQL standard's, and what a
 * pattern's literal start is under them. The .NET client's {@code LikePattern} states the same rules
 * for a parameter's value, and {@code LikePatternTests} there holds it to the same table.
 */
class LikePatternsTest {

  @Test
  void no_escape_clause_makes_every_pattern_well_formed() {
    assertThat(LikePatterns.escapeDefect(null)).isNull();
    assertThat(LikePatterns.patternDefect("KB\\_%", null)).isNull();
    assertThat(LikePatterns.patternDefect("ab!", null)).isNull();
  }

  @Test
  void an_escape_is_exactly_one_character() {
    assertThat(LikePatterns.escapeDefect("!")).isNull();
    assertThat(LikePatterns.escapeDefect("😀")).as("one code point, two UTF-16 units").isNull();
    assertThat(LikePatterns.escapeDefect("")).contains("not the empty string");
    assertThat(LikePatterns.escapeDefect("!!")).contains("not 2 characters");
  }

  @Test
  void an_escape_must_be_followed_by_a_wildcard_or_itself() {
    assertThat(LikePatterns.patternDefect("KB!_%", "!")).isNull();
    assertThat(LikePatterns.patternDefect("100!%", "!")).isNull();
    assertThat(LikePatterns.patternDefect("a!!b", "!")).isNull();
    assertThat(LikePatterns.patternDefect("ab!", "!"))
        .contains("ends with its escape character '!' at position 3");
    assertThat(LikePatterns.patternDefect("a!b", "!"))
        .contains("before an ordinary character at position 2");
  }

  @Test
  void a_refusal_never_quotes_the_pattern() {
    assertThat(LikePatterns.patternDefect("secret!", "!")).doesNotContain("secret");
    assertThatThrownBy(() -> LikePatterns.check("secret!x", "!"))
        .isInstanceOf(IllegalArgumentException.class)
        .hasMessageNotContaining("secret");
  }

  @Test
  void the_literal_start_reads_through_the_escape() {
    assertThat(LikePatterns.prefix("KB%", null)).isEqualTo(new LikePatterns.Prefix("KB", true));
    assertThat(LikePatterns.prefix("KB\\_%", null))
        .as("a backslash is ordinary without an ESCAPE")
        .isEqualTo(new LikePatterns.Prefix("KB\\", false));
    assertThat(LikePatterns.prefix("KB\\_%", "\\")).isEqualTo(new LikePatterns.Prefix("KB_", true));
    assertThat(LikePatterns.prefix("KB!%%", "!")).isEqualTo(new LikePatterns.Prefix("KB%", true));
    assertThat(LikePatterns.prefix("KB!!%", "!")).isEqualTo(new LikePatterns.Prefix("KB!", true));
    assertThat(LikePatterns.prefix("100!%", "!"))
        .as("an equality: covered by the prefix, not decided by it")
        .isEqualTo(new LikePatterns.Prefix("100%", false));
    assertThat(LikePatterns.prefix("%BLK", null)).isEqualTo(new LikePatterns.Prefix("", false));
    assertThat(LikePatterns.prefix("%", null)).isEqualTo(new LikePatterns.Prefix("", true));
    assertThat(LikePatterns.prefix("KB-%-BLK", null)).isEqualTo(new LikePatterns.Prefix("KB-", false));
    assertThat(LikePatterns.prefix("KB%%", null)).isEqualTo(new LikePatterns.Prefix("KB", false));
    assertThat(LikePatterns.prefix("", null)).isEqualTo(new LikePatterns.Prefix("", false));
  }
}

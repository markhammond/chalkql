package chalk.planner.plan;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.within;

import org.junit.jupiter.api.Test;

/**
 * Text placed on a line for a prefix's estimate: ordered by code point, interpolated between two
 * bounds past the start they share, and closed above by the first text after every text that starts
 * with the prefix (ADR 0072 §3.3).
 */
class TextLineTest {
  private static final String LAST = Character.toString(Character.MAX_CODE_POINT);

  @Test
  void the_successor_increments_the_last_code_point() {
    assertThat(ChalkSelectivity.TextLine.successor("BTC")).isEqualTo("BTD");
  }

  @Test
  void the_successor_carries_past_the_last_code_point_and_steps_over_the_surrogates() {
    assertThat(ChalkSelectivity.TextLine.successor("a" + LAST)).isEqualTo("b");
    assertThat(ChalkSelectivity.TextLine.successor(Character.toString(0xD7FF)))
        .isEqualTo(Character.toString(0xE000));
  }

  @Test
  void a_prefix_of_only_the_last_code_point_has_no_successor() {
    assertThat(ChalkSelectivity.TextLine.successor(LAST + LAST)).isNull();
  }

  /** By code point: a character beyond U+FFFF sorts after U+FFFD, where UTF-16 puts it before. */
  @Test
  void texts_compare_by_code_point() {
    String supplementary = Character.toString(0x10000);
    String specials = Character.toString(0xFFFD);

    assertThat(supplementary.compareTo(specials)).isNegative();
    assertThat(ChalkSelectivity.TextLine.compare(supplementary, specials)).isPositive();
    assertThat(ChalkSelectivity.TextLine.compare("ab", "abc")).isNegative();
    assertThat(ChalkSelectivity.TextLine.compare("abc", "abc")).isZero();
  }

  @Test
  void a_text_is_placed_between_its_bounds_past_the_start_they_share() {
    assertThat(ChalkSelectivity.TextLine.fraction("A", "C", "B")).isCloseTo(0.5, within(0.01));
    assertThat(ChalkSelectivity.TextLine.fraction("BTCA", "BTCZ", "BTCM"))
        .isCloseTo(12.0 / 25.0, within(0.01));
  }

  @Test
  void a_text_outside_its_bounds_is_placed_at_the_nearer_one() {
    assertThat(ChalkSelectivity.TextLine.fraction("B", "D", "A")).isZero();
    assertThat(ChalkSelectivity.TextLine.fraction("B", "D", "E")).isEqualTo(1.0);
    assertThat(ChalkSelectivity.TextLine.fraction("B", "D", "D")).isEqualTo(1.0);
  }
}

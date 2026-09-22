package chalk.planner.plan;

/**
 * What a limit states about its input (D276, {@code docs/design/46-row-goals.md} §1): the largest
 * number of rows an ancestor will pull from a node before it stops.
 *
 * <p>A planning fact and a physical hint, never a semantic. A node under a goal must still serve
 * every row it is asked for, and the {@code Fetch} or {@code TopN} above it stays authoritative and
 * unchanged — a source stops producing because its consumer stopped pulling, never because it
 * counted to the goal. A hard bound is always a real {@code Fetch}.
 *
 * @param offset rows the limit skips
 * @param fetch rows the limit emits
 */
public record RowGoal(long offset, long fetch) {

  /**
   * {@code offset + fetch}, saturating rather than wrapping: a statement may name two numbers whose
   * sum does not fit, and a goal that wrapped negative would read as "no goal" three layers down.
   */
  public long required() {
    long sum = offset + fetch;
    // The textbook overflow test: the sum has a different sign from both addends.
    if (((offset ^ sum) & (fetch ^ sum)) < 0) {
      return Long.MAX_VALUE;
    }

    return Math.max(0L, sum);
  }
}

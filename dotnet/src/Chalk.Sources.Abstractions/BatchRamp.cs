namespace Chalk.Sources;

/// <summary>
/// How a source sizes its batches when the plan told it a row goal (D276,
/// <c>docs/design/46-row-goals.md</c> §5).
/// </summary>
/// <remarks>
/// <para>
/// One function, shared, so every source that honours a goal ramps the same way: the first batch
/// holds about as many rows as the goal asks for, and each one after it doubles up to the
/// configured batch size. A query that stops after the first row then reads a handful of rows
/// rather than a full batch, and one whose goal was an under-estimate pays a few small batches
/// before it is back at the usual size — the number of extra batches is the logarithm of how far
/// the estimate was off.
/// </para>
/// <para>
/// Why a ramp and not a flat clamp to the goal: the goal a leaf receives is the rows it expects to
/// be <em>examined</em> for, which is the statement's limit divided by the selectivity of every
/// filter between them. Under a <c>LIMIT 1</c> over a filter estimated at one percent the leaf's
/// goal is a hundred rows and the common case is one batch; a flat clamp would make that same query
/// a hundred batches of one row, each paying a batch's own cost.
/// </para>
/// <para>
/// The ramp allocates per batch and never per row, and it changes no answer: a source still serves
/// every row it is asked for, and the goal only decides how many of them travel together.
/// </para>
/// </remarks>
public static class BatchRamp
{
    /// <summary>
    /// How many rows the next batch may hold.
    /// </summary>
    /// <param name="previous">
    /// The size of the batch before this one, or zero for the first batch of a scan.
    /// </param>
    /// <param name="batchSize">The batch size the request states; the ramp never exceeds it.</param>
    /// <param name="rowGoal">
    /// The request's row goal, or null when the plan stated none. A goal of zero or less is read as
    /// none, which is what a plan says when no limit reaches the leaf.
    /// </param>
    /// <returns>
    /// A count between one and <paramref name="batchSize"/>: <c>min(batchSize, rowGoal)</c> for the
    /// first batch of a goaled scan, <paramref name="batchSize"/> for the first batch of any other,
    /// and twice the previous batch — capped — for every batch after it.
    /// </returns>
    public static int Next(int previous, int batchSize, long? rowGoal)
    {
        if (batchSize < 1)
        {
            return 1;
        }

        if (previous > 0)
        {
            // Doubling, expressed so the multiplication cannot overflow an int.
            return previous > batchSize / 2 ? batchSize : previous * 2;
        }

        if (rowGoal is not { } goal || goal <= 0 || goal >= batchSize)
        {
            return batchSize;
        }

        return (int)goal;
    }
}

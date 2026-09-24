using Chalk.Catalog;
using Chalk.Execution.Vectors;

namespace Chalk.Execution.Operators;

/// <summary>
/// What the plan's <c>est_row_count</c> is worth to a blocking operator: the capacity its buffers
/// start at instead of one batch, so that growth by doubling starts at the estimate rather than
/// climbing a ladder of rentals to reach it (D258.3,
/// <c>docs/design/08-execution-arena.md</c> §2.3).
/// </summary>
/// <remarks>
/// <para>
/// A hint is only ever a <em>first size</em>. Every buffer still grows on demand, so an estimate that
/// is too small costs a ladder that starts higher and an estimate that is too large costs one
/// over-sized rental; neither can change an answer. An absent, zero or negative estimate — every plan
/// from a source with no statistics — leaves growth exactly as it was.
/// </para>
/// <para>
/// The one thing a hint must never do is break a budget on its own. An estimate whose reservation
/// <see cref="ArenaOptions.MaxBytes"/> could not hold is clamped to a batch, so that
/// <see cref="ArenaBudgetExceededException"/> is raised by a rental the data really asked for.
/// </para>
/// </remarks>
internal static class PlanCapacity
{
    /// <summary>
    /// Bytes a row is assumed to occupy in a buffer, for the budget test alone. A variable-length
    /// column counts at its widest — a sixteen-byte string view and a four-byte offset — because the
    /// test exists to clamp an estimate that is too big, and under-counting would fail to clamp one.
    /// </summary>
    private const int VariableRowBytes = 20;

    /// <summary>
    /// The plan's estimate as a row count for <paramref name="columns"/>, or zero for "no estimate:
    /// grow as before".
    /// </summary>
    public static int Rows(
        OperatorContext context, double estimate, IReadOnlyList<ChalkType> columns)
    {
        // Not `estimate < 1`: NaN fails both comparisons and must land here rather than be cast.
        if (!(estimate >= 1))
        {
            return 0;
        }

        var rows = estimate >= int.MaxValue ? int.MaxValue : (int)estimate;
        if (context.Arena.Options.MaxBytes is not { } budget)
        {
            return rows;
        }

        var perRow = BytesPerRow(columns);
        return perRow > 0 && (long)rows * perRow > budget
            ? Math.Min(rows, context.Settings.BatchSize)
            : rows;
    }

    private static int BytesPerRow(IReadOnlyList<ChalkType> columns)
    {
        long total = 0;
        for (var i = 0; i < columns.Count; i++)
        {
            total += BytesPerRow(columns[i]);
        }

        return total >= int.MaxValue ? int.MaxValue : (int)total;
    }

    private static long BytesPerRow(ChalkType column)
    {
        var kind = ColumnKinds.Of(column);
        var width = ColumnKinds.Width(kind);

        // A COMPOSITE is one row of every field per row (D291), each at its own width, and a bit of
        // validity this test rounds away.
        if (kind == ColumnKind.Composite)
        {
            long fields = 0;
            foreach (var field in column.Fields)
            {
                fields += BytesPerRow(field.Type);
            }

            return fields;
        }

        // A LIST is an offset per row plus a child column whose length is not a row count at
        // all; four bytes is the part of it this test can honestly name.
        return width != 0 ? width : kind == ColumnKind.List ? sizeof(int) : VariableRowBytes;
    }
}

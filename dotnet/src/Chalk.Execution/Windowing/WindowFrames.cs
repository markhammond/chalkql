using System.Numerics;
using Chalk.Catalog;
using Chalk.Execution.Vectors;
using Chalk.Ir;
using Chalk.Sources;

namespace Chalk.Execution.Windowing;

/// <summary>
/// Where each row's frame starts and ends, per <c>13-window-functions.md</c> §1 and §4.
///
/// <para>
/// Every method here writes an inclusive <c>[lo, hi]</c> pair per row into arrays the operator owns;
/// an empty frame is <c>lo &gt; hi</c>. Both bounds are non-decreasing in the row index for every
/// supported frame, which is exactly what lets the accumulators slide rather than recompute.
/// </para>
/// </summary>
internal static class WindowFrames
{
    /// <summary>
    /// Makes every frame take the segment tree of D59 rather than the sliding fast paths, so a
    /// property test can prove the two agree (<c>14-windows-ii.md</c> §4). Off in production: the
    /// fast paths are cheaper, which is why they were kept.
    /// </summary>
    internal static bool ForceSegmentTree { get; set; }

    /// <summary>
    /// The peer group of every row of one partition: rows that agree on all the order keys. With no
    /// order keys the whole partition is one peer group, which is what makes a frame of
    /// <c>RANGE UNBOUNDED PRECEDING AND CURRENT ROW</c> the whole partition when nothing is ordered.
    /// </summary>
    public static void Peers(
        WindowRowComparer order, int start, int end, int[] peerStart, int[] peerEnd)
    {
        if (order.ColumnCount == 0)
        {
            for (var i = start; i < end; i++)
            {
                peerStart[i] = start;
                peerEnd[i] = end;
            }

            return;
        }

        var groupStart = start;
        for (var i = start; i < end; i++)
        {
            if (i > start && !order.SameGroup(i - 1, i))
            {
                for (var r = groupStart; r < i; r++)
                {
                    peerEnd[r] = i;
                }

                groupStart = i;
            }

            peerStart[i] = groupStart;
        }

        for (var r = groupStart; r < end; r++)
        {
            peerEnd[r] = end;
        }
    }

    /// <summary>
    /// A <c>ROWS</c> frame: physical offsets from the current row, clamped to the partition.
    /// </summary>
    public static void Rows(
        int start,
        int end,
        FrameBoundKind lowerKind,
        long lowerOffset,
        FrameBoundKind upperKind,
        long upperOffset,
        int[] lo,
        int[] hi)
    {
        for (var i = start; i < end; i++)
        {
            long low = lowerKind switch
            {
                FrameBoundKind.UnboundedPreceding => start,
                FrameBoundKind.Preceding => (long)i - lowerOffset,
                FrameBoundKind.Following => (long)i + lowerOffset,
                _ => i,
            };
            long high = upperKind switch
            {
                FrameBoundKind.UnboundedFollowing => end - 1L,
                FrameBoundKind.Preceding => (long)i - upperOffset,
                FrameBoundKind.Following => (long)i + upperOffset,
                _ => i,
            };

            lo[i] = (int)Math.Clamp(low, start, end);
            hi[i] = (int)Math.Clamp(high, start - 1L, end - 1L);
        }
    }

    /// <summary>
    /// A <c>RANGE</c> frame with no offset: only UNBOUNDED and CURRENT ROW, where CURRENT ROW means
    /// the whole peer group — the rule that makes a running total include every row with the same
    /// timestamp.
    /// </summary>
    public static void RangePeers(
        int start,
        int end,
        FrameBoundKind lowerKind,
        FrameBoundKind upperKind,
        int[] peerStart,
        int[] peerEnd,
        int[] lo,
        int[] hi)
    {
        for (var i = start; i < end; i++)
        {
            lo[i] = lowerKind == FrameBoundKind.UnboundedPreceding ? start : peerStart[i];
            hi[i] = upperKind == FrameBoundKind.UnboundedFollowing ? end - 1 : peerEnd[i] - 1;
        }
    }

    /// <summary>
    /// A <c>RANGE</c> frame with an offset, over one order key.
    ///
    /// <para>
    /// The frame is <c>[first row whose key is at or after current − preceding, last row whose key is
    /// at or before current + following]</c>, read in the ordering's own direction — for a DESC key
    /// "preceding" means a larger value. Both ends only move forward as the row index grows, so the
    /// two cursors never rewind and the whole partition costs one pass.
    /// </para>
    /// <para>
    /// NULL keys sort together at one end and are peers of each other and of nothing else. A NULL
    /// current row's offset bound therefore resolves to its own NULL run, which is PostgreSQL's rule
    /// and the one §1 fixes; a non-NULL current row never has a NULL in its frame.
    /// </para>
    /// </summary>
    public static void RangeOffsets<T>(
        ReadOnlySpan<T> keys,
        int start,
        int end,
        int nonNullStart,
        int nonNullEnd,
        bool descending,
        FrameBoundKind lowerKind,
        T lowerOffset,
        FrameBoundKind upperKind,
        T upperOffset,
        int[] peerStart,
        int[] peerEnd,
        int[] lo,
        int[] hi)
        where T : unmanaged, INumber<T>
    {
        var lower = nonNullStart;
        var upper = nonNullStart;

        for (var i = start; i < end; i++)
        {
            var nullKey = i < nonNullStart || i >= nonNullEnd;

            lo[i] = lowerKind switch
            {
                FrameBoundKind.UnboundedPreceding => start,
                FrameBoundKind.CurrentRow => peerStart[i],
                _ when nullKey => peerStart[i],
                _ => LowerCursor(keys, ref lower, nonNullEnd, Target(keys[i], lowerKind, lowerOffset, descending), descending),
            };

            hi[i] = upperKind switch
            {
                FrameBoundKind.UnboundedFollowing => end - 1,
                FrameBoundKind.CurrentRow => peerEnd[i] - 1,
                _ when nullKey => peerEnd[i] - 1,
                _ => UpperCursor(keys, ref upper, nonNullEnd, Target(keys[i], upperKind, upperOffset, descending), descending) - 1,
            };
        }
    }

    /// <summary>
    /// The key value a bound compares against. PRECEDING walks backwards along the ordering, which
    /// for a descending key means <em>adding</em> the offset.
    /// </summary>
    private static T Target<T>(T key, FrameBoundKind kind, T offset, bool descending)
        where T : unmanaged, INumber<T>
    {
        var back = kind == FrameBoundKind.Preceding;
        return back == descending ? key + offset : key - offset;
    }

    /// <summary>The first row at or after <paramref name="target"/> in the ordering's direction.</summary>
    private static int LowerCursor<T>(
        ReadOnlySpan<T> keys, ref int cursor, int end, T target, bool descending)
        where T : unmanaged, INumber<T>
    {
        while (cursor < end && (descending ? keys[cursor] > target : keys[cursor] < target))
        {
            cursor++;
        }

        return cursor;
    }

    /// <summary>One past the last row at or before <paramref name="target"/>.</summary>
    private static int UpperCursor<T>(
        ReadOnlySpan<T> keys, ref int cursor, int end, T target, bool descending)
        where T : unmanaged, INumber<T>
    {
        while (cursor < end && (descending ? keys[cursor] >= target : keys[cursor] <= target))
        {
            cursor++;
        }

        return cursor;
    }

    /// <summary>
    /// An interval offset, in the units the order key is stored in. A day-time interval is
    /// microseconds (<c>02-ir.md</c> §3); a TIMESTAMP counts units of its own precision and a DATE
    /// counts days.
    /// </summary>
    public static long IntervalToKeyUnits(long microseconds, ChalkType key)
    {
        if (key.Kind == TypeKind.Date)
        {
            return microseconds / 86_400_000_000L;
        }

        var perSecond = (long)IrTypes.TimestampUnitsPerSecond((uint)key.Precision);
        return perSecond >= 1_000_000L
            ? microseconds * (perSecond / 1_000_000L)
            : microseconds / (1_000_000L / perSecond);
    }

    /// <summary>
    /// The lane kind a RANGE offset is compared in: 64-bit integers for the integral and temporal
    /// keys, double for the approximate ones, decimal for DECIMAL. Anything else has no ordering an
    /// offset could be added to.
    /// </summary>
    /// <summary>
    /// Whether <see cref="RangeLane"/> would answer rather than refuse. The streaming path of D258.4
    /// asks at compilation, and a compiler that threw here would move an execution-time refusal to
    /// <c>PrepareAsync</c>; the buffered path is where that message still comes from.
    /// </summary>
    public static bool HasRangeLane(ChalkType key) => ColumnKinds.Of(key)
        is ColumnKind.Int8 or ColumnKind.Int16 or ColumnKind.Int32 or ColumnKind.Int64
        or ColumnKind.Float or ColumnKind.Double or ColumnKind.Decimal128;

    public static ColumnKind RangeLane(ChalkType key) => ColumnKinds.Of(key) switch
    {
        ColumnKind.Int8 or ColumnKind.Int16 or ColumnKind.Int32 or ColumnKind.Int64 => ColumnKind.Int64,
        ColumnKind.Float or ColumnKind.Double => ColumnKind.Double,
        ColumnKind.Decimal128 => ColumnKind.Decimal128,
        _ => throw new UnsupportedFeatureException(
            $"RANGE offset over an order key of type {key}",
            "A RANGE frame with an offset needs a numeric or temporal order key "
            + "(docs/design/13-window-functions.md §1)."),
    };
}

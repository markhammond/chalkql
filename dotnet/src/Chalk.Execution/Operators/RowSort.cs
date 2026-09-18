using System.Numerics;
using System.Runtime.CompilerServices;

namespace Chalk.Execution.Operators;

/// <summary>
/// An introsort over row indexes with a <em>struct</em> order, for the two places the blocking sort
/// still compares rows: the ordering nothing could encode, and the runs an encoded prefix left equal
/// (<c>docs/design/41-blocking-sort.md</c> §2).
/// </summary>
/// <remarks>
/// <para>
/// Its reason for existing is allocation, not speed. <c>Array.Sort(array, index, length, comparer)</c>
/// and <c>Span&lt;T&gt;.Sort(comparer)</c> both take an <see cref="IComparer{T}"/>, so a struct order
/// is boxed and a <c>Comparison&lt;T&gt;</c> is built on every call — nothing per row, but a tie-break
/// makes one call per run, and a sort with two million runs allocated 190 MB of them before this
/// existed. A <c>TOrder</c> constrained to <c>struct</c> is monomorphised and inlined instead, and
/// the whole sort allocates nothing (<c>15-zero-allocation-execution.md</c> §5).
/// </para>
/// <para>
/// The algorithm is the one the framework's own <c>ArraySortHelper</c> uses: quicksort on a
/// median-of-three pivot, heapsort once the recursion is twice the log of the length, insertion sort
/// for the short ranges at the bottom. Not stable, which is what <c>02-ir.md</c> §4 allows and what
/// the encoded paths are anyway.
/// </para>
/// </remarks>
internal static class RowSort
{
    /// <summary>Ranges this short cost less to insertion-sort than to partition.</summary>
    private const int InsertionThreshold = 16;

    public static void Sort<TOrder>(Span<int> rows, TOrder order)
        where TOrder : struct, IComparer<int>
    {
        if (rows.Length < 2)
        {
            return;
        }

        Introspective(rows, (2 * BitOperations.Log2((uint)rows.Length)) + 1, order);
    }

    private static void Introspective<TOrder>(Span<int> rows, int depth, TOrder order)
        where TOrder : struct, IComparer<int>
    {
        while (rows.Length > InsertionThreshold)
        {
            if (depth == 0)
            {
                HeapSort(rows, order);
                return;
            }

            depth--;
            var pivot = Partition(rows, order);

            // The right half recurses and the left half loops, so the stack is log of the length.
            Introspective(rows[(pivot + 1)..], depth, order);
            rows = rows[..pivot];
        }

        Insertion(rows, order);
    }

    /// <summary>
    /// Hoare's partition on a median-of-three pivot parked at <c>last - 1</c>. The three-way
    /// arrangement leaves a value no greater than the pivot at the bottom and one no smaller at the
    /// top, which is what lets the two scans below run without bounds checks of their own.
    /// </summary>
    private static int Partition<TOrder>(Span<int> rows, TOrder order)
        where TOrder : struct, IComparer<int>
    {
        var last = rows.Length - 1;
        var middle = rows.Length >> 1;
        SwapIfGreater(rows, order, 0, middle);
        SwapIfGreater(rows, order, 0, last);
        SwapIfGreater(rows, order, middle, last);

        var pivot = rows[middle];
        Swap(rows, middle, last - 1);

        var left = 0;
        var right = last - 1;
        while (left < right)
        {
            while (order.Compare(rows[++left], pivot) < 0)
            {
            }

            while (order.Compare(pivot, rows[--right]) < 0)
            {
            }

            if (left >= right)
            {
                break;
            }

            Swap(rows, left, right);
        }

        Swap(rows, left, last - 1);
        return left;
    }

    private static void HeapSort<TOrder>(Span<int> rows, TOrder order)
        where TOrder : struct, IComparer<int>
    {
        var count = rows.Length;
        for (var i = count >> 1; i >= 1; i--)
        {
            SiftDown(rows, order, i, count);
        }

        for (var i = count; i > 1; i--)
        {
            Swap(rows, 0, i - 1);
            SiftDown(rows, order, 1, i - 1);
        }
    }

    /// <summary>One-based indexes inside, because that is what makes a binary heap's arithmetic read.</summary>
    private static void SiftDown<TOrder>(Span<int> rows, TOrder order, int node, int count)
        where TOrder : struct, IComparer<int>
    {
        var value = rows[node - 1];
        while (node <= count >> 1)
        {
            var child = 2 * node;
            if (child < count && order.Compare(rows[child - 1], rows[child]) < 0)
            {
                child++;
            }

            if (order.Compare(value, rows[child - 1]) >= 0)
            {
                break;
            }

            rows[node - 1] = rows[child - 1];
            node = child;
        }

        rows[node - 1] = value;
    }

    private static void Insertion<TOrder>(Span<int> rows, TOrder order)
        where TOrder : struct, IComparer<int>
    {
        for (var i = 1; i < rows.Length; i++)
        {
            var value = rows[i];
            var at = i - 1;
            while (at >= 0 && order.Compare(value, rows[at]) < 0)
            {
                rows[at + 1] = rows[at];
                at--;
            }

            rows[at + 1] = value;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SwapIfGreater<TOrder>(Span<int> rows, TOrder order, int left, int right)
        where TOrder : struct, IComparer<int>
    {
        if (order.Compare(rows[left], rows[right]) > 0)
        {
            Swap(rows, left, right);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Swap(Span<int> rows, int left, int right) =>
        (rows[left], rows[right]) = (rows[right], rows[left]);
}

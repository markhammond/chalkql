using System.Runtime.CompilerServices;
using Apache.Arrow;
using Chalk.Execution.Numeric;
using Chalk.Execution.Operators;

namespace Chalk.Execution.Aggregation;

/// <summary>
/// One batch as the accumulate kernels read it (D255): the group each selected row folds into, the
/// selection vector that says which physical row that is, the measure's <c>FILTER</c> as a mask
/// computed once before the fold, and the argument column's validity.
/// </summary>
/// <remarks>
/// The three narrowing tests are loop-invariant in shape — a mask is either there for the whole batch
/// or not at all — so <see cref="Selected"/> is a handful of well-predicted branches rather than the
/// per-row lane read and virtual call the generic fold pays.
/// </remarks>
internal readonly ref struct GroupedBatch
{
    public GroupedBatch(
        ReadOnlySpan<int> groups,
        ReadOnlySpan<int> selection,
        ReadOnlySpan<byte> mask,
        ReadOnlySpan<byte> validity,
        int offset)
    {
        Groups = groups;
        Selection = selection;
        Mask = mask;
        Validity = validity;
        Offset = offset;
    }

    /// <summary>The dense group id of each selected row, in selection order.</summary>
    public ReadOnlySpan<int> Groups { get; }

    /// <summary>The batch's selection vector, or empty when every row is selected in order.</summary>
    public ReadOnlySpan<int> Selection { get; }

    /// <summary>The measure's filter as one byte per selected row, or empty when it has none.</summary>
    public ReadOnlySpan<byte> Mask { get; }

    /// <summary>The argument column's validity bitmap, or empty when no row is NULL.</summary>
    public ReadOnlySpan<byte> Validity { get; }

    /// <summary>The argument column's first row inside its buffers, which validity is indexed by.</summary>
    public int Offset { get; }

    public int Count => Groups.Length;

    /// <summary>
    /// Whether selected row <paramref name="i"/> folds in, and if so which physical row it reads and
    /// which group it lands in.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Selected(int i, out int row, out int group)
    {
        if (Mask.Length != 0 && Mask[i] == 0)
        {
            row = 0;
            group = 0;
            return false;
        }

        row = Selection.Length == 0 ? i : Selection[i];
        if (Validity.Length != 0 && !BitUtility.GetBit(Validity, Offset + row))
        {
            group = 0;
            return false;
        }

        group = Groups[i];
        return true;
    }
}

/// <summary>
/// The typed accumulate kernels of D255: one loop over a batch's lanes scattering into the group
/// accumulators by row group, with no lane read and no virtual call per row.
/// </summary>
/// <remarks>
/// Each kernel is the batch form of the <c>Add</c> beside it and must stay its exact equal — the same
/// widening, the same <c>checked</c> arithmetic, the same order of addition, the same comparison — so
/// that a plan reading the fast path and one reading the generic path cannot disagree. The corpus and
/// the reference executor are the oracle for that (<c>05-testing.md</c>).
/// </remarks>
internal static class AccumulateKernels
{
    /// <summary>COUNT: one increment per selected row. A NULL argument is filtered by the validity.</summary>
    public static void Count(in GroupedBatch batch, long[] counts)
    {
        for (var i = 0; i < batch.Count; i++)
        {
            if (batch.Selected(i, out _, out var group))
            {
                counts[group]++;
            }
        }
    }

    /// <summary>SUM into an exact integer state from an <c>I32</c> lane.</summary>
    public static void SumInt32(
        in GroupedBatch batch, ReadOnlySpan<int> lanes, long[] sums, bool[] seen)
    {
        for (var i = 0; i < batch.Count; i++)
        {
            if (!batch.Selected(i, out var row, out var group))
            {
                continue;
            }

            seen[group] = true;
            sums[group] = checked(sums[group] + lanes[row]);
        }
    }

    /// <summary>SUM into an exact integer state from an <c>I64</c> lane.</summary>
    public static void SumInt64(
        in GroupedBatch batch, ReadOnlySpan<long> lanes, long[] sums, bool[] seen)
    {
        for (var i = 0; i < batch.Count; i++)
        {
            if (!batch.Selected(i, out var row, out var group))
            {
                continue;
            }

            seen[group] = true;
            sums[group] = checked(sums[group] + lanes[row]);
        }
    }

    /// <summary>SUM into a double state from an <c>FP64</c> lane, summed in input order (A6).</summary>
    public static void SumDouble(
        in GroupedBatch batch, ReadOnlySpan<double> lanes, double[] sums, bool[] seen)
    {
        for (var i = 0; i < batch.Count; i++)
        {
            if (!batch.Selected(i, out var row, out var group))
            {
                continue;
            }

            seen[group] = true;
            sums[group] += Normalised(lanes[row]);
        }
    }

    /// <summary>
    /// A real as the generic path sees it. <c>LaneAccess.Read</c> normalises every FP64 lane it
    /// hands to an accumulator — one NaN and one zero — so a kernel that summed or compared the raw
    /// bits could emit a different sign of zero or a different NaN payload from the same rows. The
    /// two paths have to be indistinguishable, so the kernels normalise too.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double Normalised(double value) =>
        double.IsNaN(value) ? double.NaN : value == 0d ? 0d : value;

    /// <summary>SUM into a double state from an <c>I32</c> lane.</summary>
    public static void SumDoubleFromInt32(
        in GroupedBatch batch, ReadOnlySpan<int> lanes, double[] sums, bool[] seen)
    {
        for (var i = 0; i < batch.Count; i++)
        {
            if (!batch.Selected(i, out var row, out var group))
            {
                continue;
            }

            seen[group] = true;
            sums[group] += (long)lanes[row];
        }
    }

    /// <summary>SUM into a double state from an <c>I64</c> lane.</summary>
    public static void SumDoubleFromInt64(
        in GroupedBatch batch, ReadOnlySpan<long> lanes, double[] sums, bool[] seen)
    {
        for (var i = 0; i < batch.Count; i++)
        {
            if (!batch.Selected(i, out var row, out var group))
            {
                continue;
            }

            seen[group] = true;
            sums[group] += lanes[row];
        }
    }

    /// <summary>SUM into a decimal state from a DECIMAL lane of the same scale.</summary>
    public static void SumDecimal(
        in GroupedBatch batch, ReadOnlySpan<byte> lanes, decimal[] sums, bool[] seen, int scale)
    {
        for (var i = 0; i < batch.Count; i++)
        {
            if (!batch.Selected(i, out var row, out var group))
            {
                continue;
            }

            seen[group] = true;
            sums[group] += Decimals.Read(lanes.Slice(row * 16, 16), scale);
        }
    }

    /// <summary>MIN or MAX over an <c>I32</c> lane.</summary>
    public static void MinMaxInt32(
        in GroupedBatch batch, ReadOnlySpan<int> lanes, Span<int> state, bool[] seen, bool max)
    {
        for (var i = 0; i < batch.Count; i++)
        {
            if (!batch.Selected(i, out var row, out var group))
            {
                continue;
            }

            var value = lanes[row];
            if (!seen[group])
            {
                seen[group] = true;
                state[group] = value;
            }
            else if (max ? value > state[group] : value < state[group])
            {
                state[group] = value;
            }
        }
    }

    /// <summary>MIN or MAX over an <c>I64</c> lane.</summary>
    public static void MinMaxInt64(
        in GroupedBatch batch, ReadOnlySpan<long> lanes, Span<long> state, bool[] seen, bool max)
    {
        for (var i = 0; i < batch.Count; i++)
        {
            if (!batch.Selected(i, out var row, out var group))
            {
                continue;
            }

            var value = lanes[row];
            if (!seen[group])
            {
                seen[group] = true;
                state[group] = value;
            }
            else if (max ? value > state[group] : value < state[group])
            {
                state[group] = value;
            }
        }
    }

    /// <summary>
    /// MIN or MAX over an <c>FP64</c> lane, ordered the way <c>02-ir.md</c> §3 orders reals: NaN
    /// above every number, and -0.0 neither above nor below 0.0, which is
    /// <see cref="DoubleComparer.Compare(double, double)"/>.
    /// </summary>
    public static void MinMaxDouble(
        in GroupedBatch batch, ReadOnlySpan<double> lanes, Span<double> state, bool[] seen, bool max)
    {
        for (var i = 0; i < batch.Count; i++)
        {
            if (!batch.Selected(i, out var row, out var group))
            {
                continue;
            }

            var value = Normalised(lanes[row]);
            if (!seen[group])
            {
                seen[group] = true;
                state[group] = value;
                continue;
            }

            var comparison = DoubleComparer.Compare(value, state[group]);
            if (max ? comparison > 0 : comparison < 0)
            {
                state[group] = value;
            }
        }
    }

    /// <summary>MIN or MAX over a DECIMAL lane, compared as the unscaled magnitude it is stored as.</summary>
    public static void MinMaxDecimal(
        in GroupedBatch batch, ReadOnlySpan<byte> lanes, Span<byte> state, bool[] seen, bool max)
    {
        for (var i = 0; i < batch.Count; i++)
        {
            if (!batch.Selected(i, out var row, out var group))
            {
                continue;
            }

            var lane = lanes.Slice(row * 16, 16);
            var slot = state.Slice(group * 16, 16);
            if (!seen[group])
            {
                seen[group] = true;
                lane.CopyTo(slot);
                continue;
            }

            var comparison = Decimals.Compare(lane, slot);
            if (max ? comparison > 0 : comparison < 0)
            {
                lane.CopyTo(slot);
            }
        }
    }
}

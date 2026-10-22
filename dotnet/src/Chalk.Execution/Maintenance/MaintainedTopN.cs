using System.Runtime.InteropServices;
using Chalk.Catalog;
using Chalk.Execution.Numeric;
using Chalk.Execution.Operators;
using Chalk.Execution.Vectors;
using Chalk.Sources;
using ArrowSchema = Apache.Arrow.Schema;

namespace Chalk.Execution.Maintenance;

/// <summary>
/// The <c>LIMIT</c> a statement ends in, kept by incremental maintenance (D360,
/// <c>docs/design/73-what-a-limit-keeps.md</c> §4): the rows of its input it holds, in the order of the
/// <c>LIMIT</c>'s <c>ORDER BY</c>, and the window from <c>offset</c> to <c>offset + count</c> of them,
/// whose changes it gives.
/// </summary>
/// <remarks>
/// <para>
/// Over an aggregate, a join or an as-of join it holds every row its input gives: over an aggregate,
/// one row per group, about what the aggregate itself keeps. Over a table read through filters and
/// projections, holding every row would copy the table, so it holds the first <c>offset + count</c>
/// rows and a margin of as many again. A row added below what it holds is dropped; a row removed below
/// it changes nothing; and when removals leave the window short while the input has rows it does not
/// hold, the plan is computed again from nothing in the same update, as a restart is (design 68 §7).
/// </para>
/// <para>
/// Rows are compared as the executor's sort compares them (<see cref="SortOrdering"/>), so NULLs, NaN,
/// -0.0 and text sort as a recompute sorts them. The order includes every key column, so two rows
/// equal under it have one key: the key does not hold before the <c>LIMIT</c>, and the view is faulted
/// as it is for a key its result gives twice.
/// </para>
/// </remarks>
internal sealed class TopNState : IMaintainedState
{
    private readonly RowBound _offset;
    private readonly RowBound? _count;
    private bool _resolved;

    public TopNState(
        IReadOnlyList<ChalkType> types, SortOrdering ordering, RowBound offset, RowBound? count, bool bounded, int batchSize)
    {
        Ordering = ordering;
        _offset = offset;
        _count = count;
        Bounded = bounded;
        Rows = new RowStore(types);
        Shown = new RowStore(types);
        Changes = new ChangeSet(types);
        Output = new ChangeWriter(types, batchSize);
    }

    public SortOrdering Ordering { get; }

    /// <summary>Whether the input is a table read through filters and projections: the window and a margin are held.</summary>
    public bool Bounded { get; }

    /// <summary>The rows held, by slot.</summary>
    public RowStore Rows { get; }

    /// <summary>The held rows' slots, in the <c>LIMIT</c>'s order.</summary>
    public List<int> Held { get; } = [];

    /// <summary>Whether the input has rows ranked after every row held, which were not kept.</summary>
    public bool Dropped { get; set; }

    /// <summary>The window as last given, a copy of its own: a row that leaves the input leaves <see cref="Rows"/>.</summary>
    public RowStore Shown { get; }

    /// <summary>The window's slots in <see cref="Shown"/>, in order.</summary>
    public List<int> ShownSlots { get; } = [];

    public ChangeSet Changes { get; }

    public ChangeWriter Output { get; }

    public long Offset { get; private set; }

    /// <summary>The window's count; <see cref="long.MaxValue"/> for an <c>OFFSET</c> with no <c>LIMIT</c>.</summary>
    public long Count { get; private set; }

    /// <summary>Where the window ends.</summary>
    public long End => Count == long.MaxValue ? long.MaxValue : Offset + Count;

    /// <summary>How many rows a bounded step holds: the window, the rows before it, and a margin of the count again.</summary>
    public long Holding => Count == long.MaxValue || Offset + Count > long.MaxValue - Count ? long.MaxValue : Offset + (2 * Count);

    /// <summary>The rows held and shown, their lists, and what an update's changes pass through, at their size (F223).</summary>
    public long Bytes =>
        Rows.Bytes + Shown.Bytes + HeapSizes.Of(Held, sizeof(int)) + HeapSizes.Of(ShownSlots, sizeof(int))
        + Changes.Bytes + Output.Bytes;

    /// <summary>The bounds this view runs under: its parameters bind once (design 64 §6 d).</summary>
    public void Resolve(IReadOnlyList<ScalarValue> parameters)
    {
        if (_resolved)
        {
            return;
        }

        Offset = _offset.Resolve(parameters, "OFFSET");
        Count = _count is { } count ? count.Resolve(parameters, "LIMIT") : long.MaxValue;
        _resolved = true;
    }

    public (long Live, long Slots) Occupancy => (Rows.Count, Rows.Slots);

    public void Discard()
    {
        Rows.Clear();
        Held.Clear();
        Dropped = false;
        Shown.Clear();
        ShownSlots.Clear();
        Changes.Clear();
        Output.Clear();
    }

    public void Trim()
    {
        Changes.Trim();
        Output.Trim();
    }
}

/// <summary>A bounded top-N short of rows it no longer holds: the plan computes again from nothing (D360).</summary>
internal sealed class TopNRefillException()
    : InvalidOperationException("the LIMIT's window is short of rows it no longer holds, so the plan reads its input again.");

/// <summary>The maintained top-N step of a plan: one update's changes in, the window's changes out.</summary>
internal sealed class TopNChanges : MaintainedOperator
{
    private readonly TopNState _state;
    private readonly IBatchOperator _input;

    public TopNChanges(
        OperatorContext context, string path, ArrowSchema schema, IReadOnlyList<ChalkType> types, TopNState state, IBatchOperator input)
        : base(context, path, schema, types)
    {
        _state = state;
        _input = input;
    }

    protected override ValueTask DisposeCoreAsync() => _input.DisposeAsync();

    protected override async ValueTask<ChangeWriter> ComputeAsync(CancellationToken ct)
    {
        var state = _state;
        await DrainAsync(_input, state.Changes, ct).ConfigureAwait(false);
        state.Output.Clear();
        state.Resolve(Context.Parameters);

        var changes = state.Changes;
        var columns = changes.Columns;
        var touched = false;
        var bulk = state.Held.Count == 0;

        // Removals first: a key's old row leaves before its new row comes, or the two would tie.
        for (var row = 0; row < changes.Count; row++)
        {
            if (changes.Weight(row) < 0)
            {
                touched |= Remove(state, columns, row, -changes.Weight(row));
            }
        }

        if (bulk && !state.Bounded)
        {
            // From nothing, the rows are sorted once rather than placed one at a time.
            touched |= Load(state, columns, changes);
        }
        else
        {
            for (var row = 0; row < changes.Count; row++)
            {
                if (changes.Weight(row) > 0)
                {
                    touched |= Insert(state, columns, row, changes.Weight(row));
                }
            }
        }

        if (state.Bounded)
        {
            Trim(state);
        }

        if (touched)
        {
            Give(state);
        }

        return state.Output;
    }

    private static bool Remove(TopNState state, ReadOnlySpan<ColumnView> columns, int row, long weight)
    {
        var at = LowerBound(state, columns, row);
        if (at < state.Held.Count && HeldOrder.Compare(state.Ordering, state.Rows, state.Held[at], columns, row) == 0)
        {
            if (weight > 1)
            {
                throw new InvalidOperationException("the LIMIT was told to remove a row it holds once, twice.");
            }

            state.Rows.Remove(state.Held[at]);
            state.Held.RemoveAt(at);
            return at < state.End;
        }

        // Ranked after every row held, by a step that has dropped rows: one of those, so nothing moves.
        if (state.Bounded && state.Dropped && at == state.Held.Count)
        {
            return false;
        }

        throw new InvalidOperationException("the LIMIT was told to remove a row it does not hold.");
    }

    private static bool Insert(TopNState state, ReadOnlySpan<ColumnView> columns, int row, long weight)
    {
        var at = LowerBound(state, columns, row);
        if (weight > 1 || (at < state.Held.Count && HeldOrder.Compare(state.Ordering, state.Rows, state.Held[at], columns, row) == 0))
        {
            throw new MaintainedKeyException(Render(state, columns, row), "before the statement's LIMIT");
        }

        // Ranked after every row held, where rows were dropped: somewhere among those, so not held.
        if (state.Bounded && state.Dropped && at == state.Held.Count)
        {
            return false;
        }

        // A bounded step holds no more than it keeps, row by row, so a first read of a large table never
        // holds the table: a row past the holding is dropped, and one placed before it pushes the last out.
        if (state.Bounded && at >= state.Holding)
        {
            state.Dropped = true;
            return false;
        }

        state.Held.Insert(at, state.Rows.Add(columns, row));
        if (state.Bounded && state.Held.Count > state.Holding)
        {
            var last = state.Held.Count - 1;
            state.Rows.Remove(state.Held[last]);
            state.Held.RemoveAt(last);
            state.Dropped = true;
        }

        return at < state.End;
    }

    /// <summary>Every added row of a step holding nothing yet, sorted once: the first update, or one after a restart.</summary>
    private static bool Load(TopNState state, ReadOnlySpan<ColumnView> columns, ChangeSet changes)
    {
        for (var row = 0; row < changes.Count; row++)
        {
            var weight = changes.Weight(row);
            if (weight > 1)
            {
                throw new MaintainedKeyException(Render(state, columns, row), "before the statement's LIMIT");
            }

            if (weight > 0)
            {
                state.Held.Add(state.Rows.Add(columns, row));
            }
        }

        var ordering = state.Ordering;
        var rows = state.Rows;
        state.Held.Sort((a, b) => HeldOrder.Compare(ordering, rows, a, rows, b));
        for (var at = 1; at < state.Held.Count; at++)
        {
            if (HeldOrder.Compare(ordering, rows, state.Held[at - 1], rows, state.Held[at]) == 0)
            {
                throw new MaintainedKeyException(Render(rows, ordering, state.Held[at]), "before the statement's LIMIT");
            }
        }

        return state.Held.Count > 0;
    }

    /// <summary>A bounded step keeps the window, the rows before it, and a margin; short of rows it dropped, it reads again.</summary>
    private static void Trim(TopNState state)
    {
        var holding = state.Holding;
        while (state.Held.Count > holding)
        {
            var last = state.Held.Count - 1;
            state.Rows.Remove(state.Held[last]);
            state.Held.RemoveAt(last);
            state.Dropped = true;
        }

        if (state.Dropped && state.Held.Count < state.End)
        {
            throw new TopNRefillException();
        }
    }

    /// <summary>
    /// The window's changes since it was last given: both in the <c>LIMIT</c>'s order, so one pass
    /// over the two says which rows left, which came, and which kept their key with other values.
    /// </summary>
    private static void Give(TopNState state)
    {
        var ordering = state.Ordering;
        var start = (int)Math.Min(state.Offset, state.Held.Count);
        var end = (int)Math.Min(state.End, state.Held.Count);
        var shown = state.ShownSlots;
        int i = 0, j = start;
        while (i < shown.Count || j < end)
        {
            var comparison = i == shown.Count ? 1
                : j == end ? -1
                : HeldOrder.Compare(ordering, state.Shown, shown[i], state.Rows, state.Held[j]);
            if (comparison < 0)
            {
                state.Output.Write(state.Shown, shown[i++], -1);
            }
            else if (comparison > 0)
            {
                state.Output.Write(state.Rows, state.Held[j++], 1);
            }
            else
            {
                if (!HeldOrder.Same(state.Shown, shown[i], state.Rows, state.Held[j]))
                {
                    state.Output.Write(state.Shown, shown[i], -1);
                    state.Output.Write(state.Rows, state.Held[j], 1);
                }

                i++;
                j++;
            }
        }

        state.Shown.Clear();
        shown.Clear();
        for (var at = start; at < end; at++)
        {
            shown.Add(state.Shown.Add(state.Rows, state.Held[at]));
        }
    }

    /// <summary>The first held position not ranked before the row.</summary>
    private static int LowerBound(TopNState state, ReadOnlySpan<ColumnView> columns, int row)
    {
        int low = 0, high = state.Held.Count;
        while (low < high)
        {
            var middle = (low + high) >>> 1;
            if (HeldOrder.Compare(state.Ordering, state.Rows, state.Held[middle], columns, row) < 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    private static string Render(TopNState state, ReadOnlySpan<ColumnView> columns, int row)
    {
        var slot = state.Rows.Add(columns, row);
        try
        {
            return Render(state.Rows, state.Ordering, slot);
        }
        finally
        {
            state.Rows.Remove(slot);
        }
    }

    private static string Render(RowStore rows, SortOrdering ordering, int slot)
    {
        var parts = new List<string>(ordering.KeyCount);
        for (var k = 0; k < ordering.KeyCount; k++)
        {
            var stored = rows.Column(ordering.ColumnOf(k));
            parts.Add(!stored.IsValid(slot)
                ? "NULL"
                : stored.Variable
                    ? System.Text.Encoding.UTF8.GetString(stored.Bytes(slot))
                    : Convert.ToHexString(stored.Lane(slot)));
        }

        return string.Join(", ", parts);
    }
}

/// <summary>
/// Rows compared as <see cref="SortOrdering.Compare"/> compares them — the same keys, directions and
/// NULL placement, and each value by its column kind's own comparer — where one row, or both, is held
/// in a <see cref="RowStore"/> (D360).
/// </summary>
internal static class HeldOrder
{
    public static int Compare(SortOrdering ordering, RowStore held, int slot, ReadOnlySpan<ColumnView> columns, int row)
    {
        for (var k = 0; k < ordering.KeyCount; k++)
        {
            var column = ordering.ColumnOf(k);
            var stored = held.Column(column);
            ref readonly var view = ref columns[column];
            var heldNull = !stored.IsValid(slot);
            var rowNull = !view.IsValid(row);
            if (heldNull || rowNull)
            {
                if (heldNull && rowNull)
                {
                    continue;
                }

                return heldNull == ordering.NullsFirst(k) ? -1 : 1;
            }

            var comparison = Value(stored, slot, view, row);
            if (comparison != 0)
            {
                return ordering.IsDescending(k) ? -comparison : comparison;
            }
        }

        return 0;
    }

    public static int Compare(SortOrdering ordering, RowStore left, int leftSlot, RowStore right, int rightSlot)
    {
        for (var k = 0; k < ordering.KeyCount; k++)
        {
            var column = ordering.ColumnOf(k);
            var a = left.Column(column);
            var b = right.Column(column);
            var aNull = !a.IsValid(leftSlot);
            var bNull = !b.IsValid(rightSlot);
            if (aNull || bNull)
            {
                if (aNull && bNull)
                {
                    continue;
                }

                return aNull == ordering.NullsFirst(k) ? -1 : 1;
            }

            var comparison = Value(a, leftSlot, b, rightSlot);
            if (comparison != 0)
            {
                return ordering.IsDescending(k) ? -comparison : comparison;
            }
        }

        return 0;
    }

    /// <summary>Whether two held rows are the same row, every column byte for byte.</summary>
    public static bool Same(RowStore left, int leftSlot, RowStore right, int rightSlot)
    {
        for (var column = 0; column < left.Types.Count; column++)
        {
            var a = left.Column(column);
            var b = right.Column(column);
            if (a.IsValid(leftSlot) != b.IsValid(rightSlot))
            {
                return false;
            }

            if (a.IsValid(leftSlot)
                && !(a.Variable
                    ? a.Bytes(leftSlot).SequenceEqual(b.Bytes(rightSlot))
                    : a.Lane(leftSlot).SequenceEqual(b.Lane(rightSlot))))
            {
                return false;
            }
        }

        return true;
    }

    private static int Value(StoredColumn stored, int slot, in ColumnView view, int row) => stored.Kind switch
    {
        ColumnKind.Boolean => stored.Lane(slot)[0].CompareTo((byte)(view.BoolAt(row) ? 1 : 0)),
        ColumnKind.Int8 => ((sbyte)stored.Lane(slot)[0]).CompareTo(view.Lanes<sbyte>()[row]),
        ColumnKind.Int16 => Read<short>(stored, slot).CompareTo(view.Lanes<short>()[row]),
        ColumnKind.Int32 => Read<int>(stored, slot).CompareTo(view.Lanes<int>()[row]),
        ColumnKind.Int64 => Read<long>(stored, slot).CompareTo(view.Lanes<long>()[row]),
        ColumnKind.Float => DoubleComparer.Compare(Read<float>(stored, slot), view.Lanes<float>()[row]),
        ColumnKind.Double => DoubleComparer.Compare(Read<double>(stored, slot), view.Lanes<double>()[row]),
        ColumnKind.Utf8 or ColumnKind.Binary => stored.Bytes(slot).SequenceCompareTo(view.VarValue(row)),
        ColumnKind.Decimal128 => Decimals.Compare(stored.Lane(slot), view.RawLanes(16).Slice(row * 16, 16)),
        _ => stored.Lane(slot).SequenceCompareTo(view.RawLanes(stored.Width).Slice(row * stored.Width, stored.Width)),
    };

    private static int Value(StoredColumn a, int aSlot, StoredColumn b, int bSlot) => a.Kind switch
    {
        ColumnKind.Boolean => a.Lane(aSlot)[0].CompareTo(b.Lane(bSlot)[0]),
        ColumnKind.Int8 => ((sbyte)a.Lane(aSlot)[0]).CompareTo((sbyte)b.Lane(bSlot)[0]),
        ColumnKind.Int16 => Read<short>(a, aSlot).CompareTo(Read<short>(b, bSlot)),
        ColumnKind.Int32 => Read<int>(a, aSlot).CompareTo(Read<int>(b, bSlot)),
        ColumnKind.Int64 => Read<long>(a, aSlot).CompareTo(Read<long>(b, bSlot)),
        ColumnKind.Float => DoubleComparer.Compare(Read<float>(a, aSlot), Read<float>(b, bSlot)),
        ColumnKind.Double => DoubleComparer.Compare(Read<double>(a, aSlot), Read<double>(b, bSlot)),
        ColumnKind.Utf8 or ColumnKind.Binary => a.Bytes(aSlot).SequenceCompareTo(b.Bytes(bSlot)),
        ColumnKind.Decimal128 => Decimals.Compare(a.Lane(aSlot), b.Lane(bSlot)),
        _ => a.Lane(aSlot).SequenceCompareTo(b.Lane(bSlot)),
    };

    private static T Read<T>(StoredColumn stored, int slot)
        where T : unmanaged => MemoryMarshal.Read<T>(stored.Lane(slot));
}

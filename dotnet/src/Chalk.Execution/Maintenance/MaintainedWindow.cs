using System.Buffers.Binary;
using Chalk.Catalog;
using Chalk.Execution.Expressions;
using Chalk.Execution.Operators;
using Chalk.Execution.Vectors;
using ArrowSchema = Apache.Arrow.Schema;

namespace Chalk.Execution.Maintenance;

/// <summary>
/// A filter whose every read of the clock is a cut-off an input column is compared with, such as
/// <c>ts &gt; CURRENT_TIMESTAMP - INTERVAL '2' HOUR</c> (design 68 §6). It keeps its input rows, whether
/// each passed, and each compared column's values in order.
/// </summary>
/// <remarks>
/// <para>
/// When the clock moves, a comparison of a column with a cut-off changes its answer only for a value
/// between its old cut-off and its new one, whatever the cut-off is computed from. The condition is
/// made of such comparisons and of terms the clock does not reach, so a row's answer can change only
/// where one of its comparisons does. The filter therefore reads again only the rows between each
/// comparison's two cut-offs, found through its column's order, where a filter that reads the clock in
/// any other way reads every row it holds.
/// </para>
/// <para>
/// A cut-off that is NULL, before or after, leaves every comparison of it unknown and is not a range:
/// that update reads every row again, as does the first.
/// </para>
/// </remarks>
internal sealed class WindowState : IMaintainedState
{
    /// <summary>What one entry of an order costs, beside the row it points at: a node of a red-black tree.</summary>
    private static readonly long EntryBytes = HeapSizes.SortedSetNode(16);

    private readonly int[] _columns;
    private readonly SortedSet<(long Value, int Slot)>[] _orders;
    private bool[] _passing = [];

    /// <param name="inputTypes">The filter's input columns.</param>
    /// <param name="cuts">For each comparison with a cut-off, the input column it compares.</param>
    /// <param name="batchSize">How many rows a batch of changes holds.</param>
    public WindowState(IReadOnlyList<ChalkType> inputTypes, IReadOnlyList<int> cuts, int batchSize)
    {
        Input = new HeldRows(inputTypes);
        _columns = [.. cuts.Distinct()];
        CutOrder = [.. cuts.Select(column => Array.IndexOf(_columns, column))];
        _orders = [.. _columns.Select(_ => new SortedSet<(long Value, int Slot)>())];
        Input.Changed = Changed;
        InputChanges = new ChangeSet(inputTypes);
        Replay = new ChangeWriter(inputTypes, batchSize);
        Output = new ChangeWriter(inputTypes, batchSize);
    }

    /// <summary>Every input row held, with its count.</summary>
    public HeldRows Input { get; }

    public ChangeSet InputChanges { get; }

    /// <summary>The rows this update reads: those the clock may have moved past, then the changes.</summary>
    public ChangeWriter Replay { get; }

    public ChangeWriter Output { get; }

    /// <summary>For each comparison, which order holds its column's values.</summary>
    public int[] CutOrder { get; }

    /// <summary>The clock the filter last ran at, or null before its first update.</summary>
    public DateTimeOffset? RanAt { get; set; }

    /// <summary>The cut-offs it last ran with, one per comparison, or null where one of them was NULL.</summary>
    public long[]? Cuts { get; set; }

    /// <summary>How many rows the last update read: those the clock may have moved past, and the changes.</summary>
    public int Read { get; set; }

    /// <summary>The rows held, each order of them, and what an update's changes pass through, at their size (F223).</summary>
    public long Bytes =>
        Input.Bytes + HeapSizes.Array(_passing.Length, sizeof(bool))
        + _orders.Sum(order => HeapSizes.SortedSet + (order.Count * EntryBytes))
        + InputChanges.Bytes + Replay.Bytes + Output.Bytes;

    /// <summary>Whether the row held in <paramref name="slot"/> passed when the filter last read it.</summary>
    public bool Passing(int slot) => _passing[slot];

    public void Pass(int slot, bool passing) => _passing[slot] = passing;

    /// <summary>The rows held whose value in comparison <paramref name="cut"/>'s column lies from <paramref name="low"/> to <paramref name="high"/>.</summary>
    public SortedSet<(long Value, int Slot)> Between(int cut, long low, long high) =>
        _orders[CutOrder[cut]].GetViewBetween((low, int.MinValue), (high, int.MaxValue));

    public void Discard()
    {
        Input.Clear();
        foreach (var order in _orders)
        {
            order.Clear();
        }

        InputChanges.Clear();
        Replay.Clear();
        Output.Clear();
        RanAt = null;
        Cuts = null;
        Read = 0;
    }

    public void Trim()
    {
        InputChanges.Trim();
        Replay.Trim();
        Output.Trim();
    }

    /// <summary>Keeps each order in step with the rows held: a slot filled is entered, one emptied taken out.</summary>
    private void Changed(int slot, bool filled)
    {
        if (filled && _passing.Length <= slot)
        {
            Array.Resize(ref _passing, Math.Max(slot + 1, Math.Max(16, _passing.Length * 2)));
        }

        for (var i = 0; i < _columns.Length; i++)
        {
            // A NULL compares as unknown with any cut-off, so no move of the clock changes its answer.
            if (Value(Input.Rows.Column(_columns[i]), slot) is not { } value)
            {
                continue;
            }

            if (filled)
            {
                _orders[i].Add((value, slot));
            }
            else
            {
                _orders[i].Remove((value, slot));
            }
        }
    }

    /// <summary>A whole-number value as the column holds it: days, units of time, or the number itself.</summary>
    private static long? Value(StoredColumn column, int slot)
    {
        if (!column.IsValid(slot))
        {
            return null;
        }

        var lane = column.Lane(slot);
        return lane.Length switch
        {
            1 => (sbyte)lane[0],
            2 => BinaryPrimitives.ReadInt16LittleEndian(lane),
            4 => BinaryPrimitives.ReadInt32LittleEndian(lane),
            8 => BinaryPrimitives.ReadInt64LittleEndian(lane),
            _ => throw new InvalidOperationException($"a column of {lane.Length}-byte values was taken for a whole number."),
        };
    }
}

/// <summary>
/// The windowed filter's update: the rows the clock may have moved past, read again against the new
/// cut-offs, and the input's changes, read once.
/// </summary>
internal sealed class WindowChanges : MaintainedOperator, IKeepsScratch
{
    private readonly WindowState _state;
    private readonly IBatchOperator _input;
    private readonly IVectorExpr _condition;
    private readonly IVectorExpr[] _cuts;
    private readonly EvalContext _eval;
    private readonly List<int> _slots = [];
    private readonly HashSet<int> _seen = [];
    private readonly List<int> _rows = [];
    private bool[] _pass = [];

    public WindowChanges(
        OperatorContext context,
        string path,
        ArrowSchema schema,
        IReadOnlyList<ChalkType> types,
        WindowState state,
        IBatchOperator input,
        IVectorExpr condition,
        IVectorExpr[] cuts)
        : base(context, path, schema, types)
    {
        _state = state;
        _input = input;
        _condition = condition;
        _cuts = cuts;
        _eval = context.NewEvalContext();
        context.Scratch.Add(this);
    }

    /// <summary>The rows the last update read and their pass marks, back to none.</summary>
    public void Trim()
    {
        _slots.Clear();
        _slots.TrimExcess();
        _seen.Clear();
        _seen.TrimExcess();
        _rows.Clear();
        _rows.TrimExcess();
        _pass = [];
    }

    protected override ValueTask DisposeCoreAsync() => _input.DisposeAsync();

    protected override async ValueTask<ChangeWriter> ComputeAsync(CancellationToken ct)
    {
        var state = _state;
        await DrainAsync(_input, state.InputChanges, ct).ConfigureAwait(false);
        var changes = state.InputChanges;
        var moved = state.RanAt != Context.Now;
        var cuts = moved ? Cuts() : state.Cuts;

        // The rows held whose answer the clock may have changed: between each comparison's two
        // cut-offs, or every row when there are not two to compare.
        _slots.Clear();
        _seen.Clear();
        if (moved && state.RanAt is not null && state.Cuts is { } before && cuts is not null)
        {
            for (var cut = 0; cut < cuts.Length; cut++)
            {
                var (low, high) = before[cut] <= cuts[cut] ? (before[cut], cuts[cut]) : (cuts[cut], before[cut]);
                foreach (var (_, slot) in state.Between(cut, low, high))
                {
                    if (_seen.Add(slot))
                    {
                        _slots.Add(slot);
                    }
                }
            }
        }
        else if (moved)
        {
            foreach (var (slot, _) in state.Input.All())
            {
                _slots.Add(slot);
            }
        }

        // Each read once, in this order: the rows held, then the changes.
        state.Replay.Clear();
        foreach (var slot in _slots)
        {
            state.Replay.Write(state.Input.Rows, slot, state.Input.Count(slot));
        }

        _rows.Clear();
        for (var row = 0; row < changes.Count; row++)
        {
            if (changes.Weight(row) != 0)
            {
                state.Replay.Write(changes.Columns, row, changes.Weight(row));
                _rows.Add(row);
            }
        }

        var read = _slots.Count + _rows.Count;
        if (_pass.Length < read)
        {
            _pass = new bool[Math.Max(read, _pass.Length * 2)];
        }

        var at = 0;
        for (var i = 0; i < state.Replay.BatchCount; i++)
        {
            var batch = state.Replay.Batch(i);
            Evaluate(_condition, _eval, batch, _pass.AsSpan(at, batch.RowCount));
            at += batch.RowCount;
        }

        // A row held gives the difference between its answer now and its answer before, for every copy
        // of it; a change gives its own weight where it passes.
        var output = state.Output;
        output.Clear();
        at = 0;
        foreach (var slot in _slots)
        {
            var pass = _pass[at++];
            if (pass != state.Passing(slot))
            {
                var count = state.Input.Count(slot);
                output.Write(state.Input.Rows, slot, pass ? count : -count);
                state.Pass(slot, pass);
            }
        }

        foreach (var row in _rows)
        {
            var pass = _pass[at++];
            var weight = changes.Weight(row);
            if (pass)
            {
                output.Write(changes.Columns, row, weight);
            }

            state.Input.Add(changes.Columns, row, weight);
            var slot = state.Input.Find(changes.Columns, row);
            if (slot >= 0)
            {
                state.Pass(slot, pass);
            }
        }

        if (moved)
        {
            state.RanAt = Context.Now;
            state.Cuts = cuts;
        }

        state.Read = read;
        return output;
    }

    /// <summary>Each comparison's cut-off at the update's clock, or null when one of them is NULL.</summary>
    private long[]? Cuts()
    {
        var cuts = new long[_cuts.Length];
        for (var i = 0; i < _cuts.Length; i++)
        {
            var value = _eval.EvaluateOnce(_cuts[i]);
            var scalar = value.IsScalar ? value.Scalar : ScalarValue.FromLane(value.View, 0, _cuts[i].Type);
            if (scalar.IsNull)
            {
                return null;
            }

            cuts[i] = scalar.Integer;
        }

        return cuts;
    }
}

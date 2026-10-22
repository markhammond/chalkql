using Chalk.Catalog;
using Chalk.Execution.Aggregation;
using Chalk.Execution.Expressions;
using Chalk.Execution.Operators;
using Chalk.Execution.Vectors;
using Chalk.Ir;
using Chalk.Sources;
using ArrowSchema = Apache.Arrow.Schema;

namespace Chalk.Execution.Maintenance;

/// <summary>
/// One grouping set of a maintained aggregate (D343, design 68 §2): its groups by key, each with its
/// row count, its key's values and its measures' state.
/// </summary>
internal sealed class GroupSet
{
    public GroupSet(
        IReadOnlyList<ChalkType> inputTypes, int[] outputKeys, Grouping grouping, Measure[] measures, ChalkType[] measureTypes)
    {
        var named = grouping.Keys.Select(k => (int)k).ToHashSet();
        Keys = [.. outputKeys.Where(named.Contains)];
        OutputKeys = [.. outputKeys.Select(k => Array.IndexOf(Keys, k))];
        KeyColumns = new KeyColumns(inputTypes, Keys);
        Values = new RowStore([.. Keys.Select(k => inputTypes[k])]);
        Measures = new MaintainedMeasure?[measures.Length];
        Groupings = new long[measures.Length];
        for (var m = 0; m < measures.Length; m++)
        {
            var measure = measures[m];
            if (measure.UserFunction.Length == 0 && measure.Function == AggregateFunctionId.Grouping)
            {
                // A bit for each argument, the first the most significant, set where the set does not
                // name that key — as the executor's grouping sets compute it.
                var bits = 0L;
                foreach (var argument in measure.Args)
                {
                    bits = (bits << 1) | (named.Contains((int)argument.FieldRef.Index) ? 0L : 1L);
                }

                Groupings[m] = bits;
                continue;
            }

            ChalkType? argumentType = measure.Args.Count > 0 ? inputTypes[(int)measure.Args[0].FieldRef.Index] : null;
            Measures[m] = MaintainedMeasure.Create(measure, measureTypes[m], argumentType);
        }

        if (Keys.Length == 0)
        {
            // The grand total: its one group always exists, as the executor gives one row over no input.
            var slot = Values.Add([], 0);
            Ensure(slot + 1);
        }
    }

    /// <summary>The input fields the set groups by, in the output's key order.</summary>
    public int[] Keys { get; }

    /// <summary>For each of the output's keys, its place among <see cref="Keys"/>, or -1 when the set leaves it NULL.</summary>
    public int[] OutputKeys { get; }

    public KeyColumns KeyColumns { get; }

    /// <summary>Each group's key values, by group.</summary>
    public RowStore Values { get; }

    /// <summary>Each measure's state, or null for a <c>GROUPING</c> measure, whose value is <see cref="Groupings"/>'.</summary>
    public MaintainedMeasure?[] Measures { get; }

    public long[] Groupings { get; }

    /// <summary>Each group's id, by its key normalised as grouping compares keys.</summary>
    public ByteMap Groups { get; } = new();

    public long[] Counts { get; private set; } = [];

    /// <summary>The groups this update touched, in the order it touched them.</summary>
    public List<int> Touched { get; } = [];

    /// <summary>For each group, whether this update touched it, and whether it was live before it did.</summary>
    public bool[] IsTouched { get; private set; } = [];

    public bool[] WasLive { get; private set; } = [];

    /// <summary>The store's column of each of the set's keys: 0, 1, … — what a stored key is encoded from.</summary>
    public int[] StoredKeys => _storedKeys ??= [.. Enumerable.Range(0, Keys.Length)];

    private int[]? _storedKeys;

    /// <summary>Marks a group touched by this update, and whether it was live before; false when it already was.</summary>
    public bool Touch(int group, bool live)
    {
        if (IsTouched[group])
        {
            return false;
        }

        IsTouched[group] = true;
        WasLive[group] = live;
        Touched.Add(group);
        return true;
    }

    /// <summary>Forgets what this update touched.</summary>
    public void Untouch()
    {
        foreach (var group in Touched)
        {
            IsTouched[group] = false;
        }

        Touched.Clear();
    }

    public bool IsGrandTotal => Keys.Length == 0;

    /// <summary>The groups' keys, ids, counts and measures, and what an update marks of them, at their size (F223).</summary>
    public long Bytes
    {
        get
        {
            var bytes = Values.Bytes + Groups.Bytes + HeapSizes.Array(Counts.Length, sizeof(long))
                + HeapSizes.Array(IsTouched.Length, sizeof(bool)) + HeapSizes.Array(WasLive.Length, sizeof(bool))
                + HeapSizes.Of(Touched, sizeof(int));
            foreach (var measure in Measures)
            {
                bytes += measure?.Bytes ?? 0;
            }

            return bytes;
        }
    }

    public void Ensure(int groups)
    {
        if (Counts.Length < groups)
        {
            var size = StateGrowth.Next(Counts.Length, groups);
            var counts = Counts;
            var touched = IsTouched;
            var live = WasLive;
            Array.Resize(ref counts, size);
            Array.Resize(ref touched, size);
            Array.Resize(ref live, size);
            Counts = counts;
            IsTouched = touched;
            WasLive = live;
        }

        foreach (var measure in Measures)
        {
            measure?.Ensure(groups);
        }
    }

    public void Clear()
    {
        Groups.Clear();
        Touched.Clear();
        Array.Clear(IsTouched);
        Array.Clear(Counts);
        Values.Clear();
        for (var m = 0; m < Measures.Length; m++)
        {
            for (var g = 0; g < Counts.Length; g++)
            {
                Measures[m]?.Clear(g);
            }
        }

        if (IsGrandTotal)
        {
            Values.Add([], 0);
        }
    }
}

/// <summary>
/// A maintained aggregate, grouping sets included (design 68 §2): each change row is folded into its
/// group in every set, and each group it touched gives its old row out and its new one in. A group
/// whose rows are all taken out goes; the grand total stays, as the executor's always gives it.
/// </summary>
internal sealed class AggregateState : IMaintainedState
{
    public AggregateState(
        IReadOnlyList<ChalkType> inputTypes,
        IReadOnlyList<ChalkType> outputTypes,
        Aggregate aggregate,
        int batchSize)
    {
        OutputKeys = GroupingKeys.Of(aggregate);
        Measures = [.. aggregate.Measures];
        var measureTypes = Measures.Select(m => ChalkType.FromProto(m.Type)).ToArray();
        foreach (var measure in Measures)
        {
            MaintainedMeasure.Admit(measure);
            if (measure.Args.Any(a => a.KindCase != Expr.KindOneofCase.FieldRef))
            {
                throw new NotMaintainedException("an aggregate's argument is an expression, which is not maintained.");
            }

            if (measure.Filter is { } filter && FilterColumn(filter) < 0)
            {
                throw new NotMaintainedException("an aggregate's FILTER is an expression, which is not maintained.");
            }
        }

        Sets = [.. aggregate.Groupings.Select(g => new GroupSet(inputTypes, OutputKeys, g, Measures, measureTypes))];

        // An update's rows are added up by every key any set names before they reach the sets: each set
        // then folds in a group's changes once, rather than every row of them.
        Finest = new KeyColumns(inputTypes, OutputKeys);
        Changed = [.. Sets[0].Measures.Select(m => m?.Twin())];
        MeasureTypes = measureTypes;
        Arguments = [.. Measures.Select(m => m.Args.Count > 0 ? (int)m.Args[0].FieldRef.Index : -1)];
        ArgumentKinds = [.. Arguments.Select(a => a < 0 ? ColumnKind.Int64 : ColumnKinds.Of(inputTypes[a]))];
        Filters = [.. Measures.Select(m => m.Filter is { } filter ? FilterColumn(filter) : -1)];
        Changes = new ChangeSet(inputTypes);
        Output = new ChangeWriter(outputTypes, batchSize);
    }

    /// <summary>
    /// The input column a measure's <c>FILTER</c> reads, or -1 where the filter is any other expression:
    /// a bare boolean column, or <c>IS TRUE</c> of one, which is how the planner writes every filter —
    /// one written as <c>FILTER (WHERE …)</c>, and one it made of a <c>CASE</c> inside the aggregate.
    /// </summary>
    /// <remarks>
    /// The fold keeps a row for the measure only where the column is valid and true, which is
    /// <c>IS TRUE</c> of it: so the two forms are maintained alike, and as the executors evaluate them.
    /// </remarks>
    private static int FilterColumn(Expr filter) =>
        filter.KindCase switch
        {
            Expr.KindOneofCase.FieldRef => (int)filter.FieldRef.Index,
            Expr.KindOneofCase.Call when filter.Call.Function == FunctionId.IsTrue
                && filter.Call.UserFunction.Length == 0
                && filter.Call.Args.Count == 1
                && filter.Call.Args[0].KindCase == Expr.KindOneofCase.FieldRef =>
                (int)filter.Call.Args[0].FieldRef.Index,
            _ => -1,
        };

    public int[] OutputKeys { get; }

    public Measure[] Measures { get; }

    public ChalkType[] MeasureTypes { get; }

    /// <summary>Each measure's argument column, or -1 for <c>COUNT(*)</c>; its layout; its <c>FILTER</c> column, or -1.</summary>
    public int[] Arguments { get; }

    public ColumnKind[] ArgumentKinds { get; }

    public int[] Filters { get; }

    public GroupSet[] Sets { get; }

    public ChangeSet Changes { get; }

    /// <summary>Every key any set names: an update's rows are added up by it first.</summary>
    public KeyColumns Finest { get; }

    /// <summary>Each measure's changes in this update, by finest group; null for <c>GROUPING</c>.</summary>
    public MaintainedMeasure?[] Changed { get; }

    /// <summary>This update's finest groups: by key, and each one's first row, row count and measures.</summary>
    public ByteMap FinestGroups { get; } = new();

    public List<int> FinestRows { get; } = [];

    public List<long> FinestCounts { get; } = [];

    public ChangeWriter Output { get; }

    public KeyEncoder Encoder { get; } = new();

    /// <summary>Whether an update has run since the state was made or discarded.</summary>
    public bool Started { get; set; }

    /// <summary>
    /// Every set's groups, and what an update's changes pass through: the changes, the finest groups
    /// they add up in and each one's measures, and the rows written (F223).
    /// </summary>
    public long Bytes
    {
        get
        {
            var bytes = Changes.Bytes + Output.Bytes + FinestGroups.Bytes
                + HeapSizes.Of(FinestRows, sizeof(int)) + HeapSizes.Of(FinestCounts, sizeof(long));
            foreach (var set in Sets)
            {
                bytes += set.Bytes;
            }

            foreach (var measure in Changed)
            {
                bytes += measure?.Bytes ?? 0;
            }

            return bytes;
        }
    }

    public (long Live, long Slots) Occupancy
    {
        get
        {
            var (live, slots) = (0L, 0L);
            foreach (var set in Sets)
            {
                live += set.Values.Count;
                slots += set.Values.Slots;
            }

            return (live, slots);
        }
    }

    public void Discard()
    {
        foreach (var set in Sets)
        {
            set.Clear();
        }

        Changes.Clear();
        Output.Clear();
        Started = false;
    }

    public void Trim()
    {
        Changes.Trim();
        Output.Trim();
        FinestGroups.Trim();
        FinestRows.Clear();
        FinestRows.TrimExcess();
        FinestCounts.Clear();
        FinestCounts.TrimExcess();

        // Each measure's changes were kept by finest group, and an update from nothing touches them
        // all: fresh ones hold nothing until the next update's groups.
        for (var m = 0; m < Changed.Length; m++)
        {
            Changed[m] = Changed[m]?.Twin();
        }

        foreach (var set in Sets)
        {
            set.Untouch();
            set.Touched.TrimExcess();
        }
    }
}

internal sealed class AggregateChanges : MaintainedOperator
{
    private readonly AggregateState _state;
    private readonly IBatchOperator _input;
    private readonly ReadState? _rebuildsOn;
    private readonly byte[] _scratch = new byte[16];
    private readonly byte[] _keyLane = new byte[16];
    private Vector[] _arguments = [];

    public AggregateChanges(
        OperatorContext context,
        string path,
        ArrowSchema schema,
        IReadOnlyList<ChalkType> types,
        AggregateState state,
        IBatchOperator input,
        ReadState? rebuildsOn = null)
        : base(context, path, schema, types)
    {
        _state = state;
        _input = input;
        _rebuildsOn = rebuildsOn;
    }

    protected override ValueTask DisposeCoreAsync() => _input.DisposeAsync();

    protected override async ValueTask<ChangeWriter> ComputeAsync(CancellationToken ct)
    {
        var state = _state;
        await DrainAsync(_input, state.Changes, ct).ConfigureAwait(false);
        state.Output.Clear();
        if (_rebuildsOn is { Rewritten: true })
        {
            Rebuild();
        }

        var changes = state.Changes;
        var columns = changes.Columns;
        var measures = state.Measures;
        if (_arguments.Length < measures.Length)
        {
            _arguments = new Vector[measures.Length];
        }

        for (var m = 0; m < measures.Length; m++)
        {
            _arguments[m] = state.Arguments[m] >= 0 ? Vector.FromView(columns[state.Arguments[m]], changes.Count) : default;
        }

        foreach (var set in state.Sets)
        {
            set.Untouch();
            if (set.IsGrandTotal && !state.Started)
            {
                // The first update gives the grand total's row whatever reaches it.
                set.Touch(0, live: false);
            }
        }

        state.FinestGroups.Clear();
        state.FinestRows.Clear();
        state.FinestCounts.Clear();
        for (var row = 0; row < changes.Count; row++)
        {
            var weight = changes.Weight(row);
            if (weight == 0)
            {
                continue;
            }

            state.Finest.Encode(state.Encoder, columns, row, normalise: true);
            ref var finest = ref state.FinestGroups.GetOrAdd(state.Encoder.Span, out var added);
            if (added)
            {
                finest = state.FinestRows.Count;
                state.FinestRows.Add(row);
                state.FinestCounts.Add(0);
                foreach (var measure in state.Changed)
                {
                    measure?.Ensure(finest + 1);
                    measure?.Clear(finest);
                }
            }

            var at = finest;
            state.FinestCounts[at] += weight;
            for (var m = 0; m < measures.Length; m++)
            {
                if (state.Changed[m] is not { } measure)
                {
                    continue;
                }

                if (state.Filters[m] >= 0
                    && !(columns[state.Filters[m]].IsValid(row) && columns[state.Filters[m]].BoolAt(row)))
                {
                    continue;
                }

                if (state.Arguments[m] < 0)
                {
                    measure.Add(at, [], valid: true, weight);
                    continue;
                }

                var kind = state.ArgumentKinds[m];
                var valid = LaneAccess.IsValid(_arguments[m], row);
                measure.Add(
                    at,
                    valid ? LaneAccess.Read(_arguments[m], row, kind, ColumnKinds.Width(kind), _scratch) : [],
                    valid,
                    weight);
            }
        }

        // Each finest group's changes, into its group in every set.
        for (var finest = 0; finest < state.FinestRows.Count; finest++)
        {
            var row = state.FinestRows[finest];
            foreach (var set in state.Sets)
            {
                var group = Group(set, columns, row);
                set.Counts[group] += state.FinestCounts[finest];
                for (var m = 0; m < measures.Length; m++)
                {
                    if (set.Measures[m] is { } measure)
                    {
                        measure.Merge(group, state.Changed[m]!, finest);
                    }
                }
            }
        }

        foreach (var set in state.Sets)
        {
            foreach (var group in set.Touched)
            {
                var count = set.Counts[group];
                if (count < 0)
                {
                    throw new InvalidOperationException("a maintained aggregate took out more rows than a group held.");
                }

                if (count > 0 || set.IsGrandTotal)
                {
                    WriteRow(set, group, +1);
                    continue;
                }

                set.KeyColumns.Encode(state.Encoder, set.Values, set.StoredKeys, group, normalise: true);
                set.Groups.Remove(state.Encoder.Span, out _);
                foreach (var measure in set.Measures)
                {
                    measure?.Clear(group);
                }

                set.Values.Remove(group);
            }
        }

        state.Started = true;
        return state.Output;
    }

    /// <summary>
    /// The table under the aggregate was rewritten, and its read gives only the new rows (design 68 §14):
    /// every group's row goes out, and the groups are built again from the new rows as at a first
    /// update. A group whose row comes back the same is taken out and put in again, which what reads
    /// the aggregate adds up to nothing.
    /// </summary>
    private void Rebuild()
    {
        var state = _state;
        foreach (var set in state.Sets)
        {
            if (set.IsGrandTotal)
            {
                if (state.Started)
                {
                    WriteRow(set, 0, -1);
                }
            }
            else
            {
                for (var entry = 0; entry < set.Groups.Entries; entry++)
                {
                    if (set.Groups.IsLive(entry) && set.Counts[set.Groups.Value(entry)] > 0)
                    {
                        WriteRow(set, set.Groups.Value(entry), -1);
                    }
                }
            }

            set.Clear();
        }

        state.Started = false;
    }

    /// <summary>
    /// The row's group in <paramref name="set"/>, made if it is new. A group touched for the first time
    /// in this update gives its old row out first, if it had one.
    /// </summary>
    private int Group(GroupSet set, ReadOnlySpan<ColumnView> columns, int row)
    {
        int group;
        if (set.IsGrandTotal)
        {
            group = 0;
        }
        else
        {
            set.KeyColumns.Encode(_state.Encoder, columns, row, normalise: true);
            ref var held = ref set.Groups.GetOrAdd(_state.Encoder.Span, out var added);
            if (added)
            {
                group = set.Values.Add(columns, row, set.Keys);
                held = group;
                set.Ensure(group + 1);
                set.Counts[group] = 0;
                foreach (var measure in set.Measures)
                {
                    measure?.Clear(group);
                }

                set.Touch(group, live: false);
                return group;
            }

            group = held;
        }

        if (!set.IsTouched[group])
        {
            var live = set.Counts[group] > 0 || (set.IsGrandTotal && _state.Started);
            set.Touch(group, live);
            if (live)
            {
                WriteRow(set, group, -1);
            }
        }

        return group;
    }

    /// <summary>The group's row: the output's keys, NULL where the set does not name one, then the measures.</summary>
    private void WriteRow(GroupSet set, int group, long weight)
    {
        var state = _state;
        var into = state.Output;
        into.Begin();
        var column = 0;
        for (var k = 0; k < state.OutputKeys.Length; k++, column++)
        {
            var at = set.OutputKeys[k];
            var stored = at < 0 ? null : set.Values.Column(at);
            if (stored is null || !stored.IsValid(group))
            {
                into.Column(column).AppendNull();
            }
            else if (stored.Kind is ColumnKind.Float or ColumnKind.Double)
            {
                // A key is as grouping compares it: one NaN, and 0.0 for -0.0, as the executor gives it.
                stored.Lane(group).CopyTo(_keyLane);
                into.Column(column).AppendLane(LaneAccess.Normalise(stored.Kind, _keyLane.AsSpan(0, stored.Width)));
            }
            else
            {
                into.Column(column).Append(stored, group);
            }
        }

        Span<byte> lane = stackalloc byte[8];
        for (var m = 0; m < state.Measures.Length; m++, column++)
        {
            if (set.Measures[m] is { } measure)
            {
                measure.Write(group, into.Column(column));
                continue;
            }

            var type = state.MeasureTypes[m];
            var kind = ColumnKinds.Of(type);
            var width = ColumnKinds.Width(kind);
            NumberLanes.WriteInteger(kind, type, set.Groupings[m], lane[..width]);
            into.Column(column).AppendLane(lane[..width]);
        }

        into.End(weight);
    }
}

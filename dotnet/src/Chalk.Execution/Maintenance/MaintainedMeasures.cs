using System.Runtime.InteropServices;
using Chalk.Catalog;
using Chalk.Execution.Aggregation;
using Chalk.Execution.Expressions;
using Chalk.Execution.Numeric;
using Chalk.Execution.Vectors;
using Chalk.Ir;
using Chalk.Sources;

namespace Chalk.Execution.Maintenance;

/// <summary>
/// One measure of a maintained aggregate (D343, design 68 §2): its state for every group, kept so
/// that a row can be taken out as well as put in, and its value written as the executor's aggregate
/// writes it.
/// </summary>
/// <remarks>
/// A value reaches a measure as the executor's aggregate reads it, through <see cref="LaneAccess.Read"/>:
/// floating point normalised, a boolean a byte. A weight is a row's multiplicity, negative for rows
/// taken out.
/// </remarks>
internal abstract class MaintainedMeasure
{
    protected MaintainedMeasure(ChalkType resultType)
    {
        ResultType = resultType;
        ResultKind = ColumnKinds.Of(resultType);
        ResultWidth = ColumnKinds.Width(ResultKind);
    }

    protected ChalkType ResultType { get; }

    protected ColumnKind ResultKind { get; }

    protected int ResultWidth { get; }

    /// <summary>Bytes held for every group.</summary>
    public abstract long Bytes { get; }

    /// <summary>Room for <paramref name="groups"/> groups.</summary>
    public abstract void Ensure(int groups);

    /// <summary>Folds a value in <paramref name="weight"/> times; <paramref name="valid"/> is false for NULL.</summary>
    public abstract void Add(int group, ReadOnlySpan<byte> lane, bool valid, long weight);

    /// <summary>Forgets a group, for its id to be reused.</summary>
    public abstract void Clear(int group);

    /// <summary>Writes the group's value, which for an empty group is what the executor writes.</summary>
    public abstract void Write(int group, ChangeColumn into);

    /// <summary>
    /// Folds in what group <paramref name="from"/> of <paramref name="other"/>, a measure of the same
    /// kind, holds: an update's changes added up by the finest key, merged into a coarser set's group.
    /// </summary>
    public abstract void Merge(int group, MaintainedMeasure other, int from);

    /// <summary>A measure of the same kind and types, empty, to add an update's changes up in.</summary>
    public abstract MaintainedMeasure Twin();

    /// <summary>
    /// The state for <paramref name="measure"/>, or a refusal naming it: <c>COUNT</c>, <c>SUM</c>,
    /// <c>SUM0</c>, <c>MIN</c> and <c>MAX</c>, without <c>DISTINCT</c> or an ordering (design 68 §8).
    /// </summary>
    public static MaintainedMeasure Create(Measure measure, ChalkType resultType, ChalkType? argumentType)
    {
        if (measure.UserFunction.Length > 0)
        {
            throw new NotMaintainedException(
                $"the host aggregate '{measure.UserFunction}' is not maintained: a maintained view keeps "
                + "COUNT, SUM, MIN and MAX.");
        }

        if (measure.Distinct)
        {
            throw new NotMaintainedException(
                $"{measure.Function.ToString().ToUpperInvariant()}(DISTINCT …) is not maintained.");
        }

        if (measure.OrderBy.Count > 0)
        {
            throw new NotMaintainedException(
                $"{measure.Function.ToString().ToUpperInvariant()} with an ordering is not maintained.");
        }

        return measure.Function switch
        {
            AggregateFunctionId.Count => new MaintainedCount(resultType),
            AggregateFunctionId.Sum or AggregateFunctionId.Sum0 =>
                SumOf(resultType, argumentType ?? resultType, measure.Function == AggregateFunctionId.Sum0),
            AggregateFunctionId.Min or AggregateFunctionId.Max =>
                new MaintainedExtremum(resultType, measure.Function == AggregateFunctionId.Max),
            _ => throw new NotMaintainedException(
                $"the aggregate {measure.Function.ToString().ToUpperInvariant()} is not maintained: a "
                + "maintained view keeps COUNT, SUM, MIN and MAX."),
        };
    }

    private static MaintainedMeasure SumOf(ChalkType resultType, ChalkType argumentType, bool zeroWhenEmpty) =>
        ColumnKinds.Of(resultType) switch
        {
            ColumnKind.Double or ColumnKind.Float => new MaintainedFloatSum(resultType, argumentType, zeroWhenEmpty),
            ColumnKind.Decimal128 => new MaintainedDecimalSum(resultType, argumentType, zeroWhenEmpty),
            _ => new MaintainedIntegerSum(resultType, argumentType, zeroWhenEmpty),
        };

    protected static void Grow<T>(ref T[] array, int size)
    {
        if (array.Length < size)
        {
            Array.Resize(ref array, Math.Max(size, Math.Max(16, array.Length * 2)));
        }
    }
}

/// <summary><c>COUNT(*)</c> and <c>COUNT(x)</c>: a count, which a NULL argument does not add to.</summary>
internal sealed class MaintainedCount(ChalkType resultType) : MaintainedMeasure(resultType)
{
    private long[] _counts = [];

    public override long Bytes => _counts.Length * sizeof(long);

    public override void Ensure(int groups) => Grow(ref _counts, groups);

    public override void Add(int group, ReadOnlySpan<byte> lane, bool valid, long weight)
    {
        if (valid)
        {
            _counts[group] += weight;
        }
    }

    public override void Clear(int group) => _counts[group] = 0;

    public override void Write(int group, ChangeColumn into)
    {
        Span<byte> lane = stackalloc byte[ResultWidth];
        NumberLanes.WriteInteger(ResultKind, ResultType, _counts[group], lane);
        into.AppendLane(lane);
    }

    public override void Merge(int group, MaintainedMeasure other, int from) =>
        _counts[group] += ((MaintainedCount)other)._counts[from];

    public override MaintainedMeasure Twin() => new MaintainedCount(ResultType);
}

/// <summary>
/// The parts every sum shares: how many non-NULL values a group holds, which decides between NULL
/// and a value, and reading an argument as the executor's <c>SumAccumulator</c> reads it.
/// </summary>
internal abstract class MaintainedSum : MaintainedMeasure
{
    private readonly ColumnKind _argumentKind;
    private readonly int _argumentScale;
    private readonly ChalkType _argumentType;
    private long[] _seen = [];

    protected MaintainedSum(ChalkType resultType, ChalkType argumentType, bool zeroWhenEmpty)
        : base(resultType)
    {
        _argumentKind = ColumnKinds.Of(argumentType);
        _argumentScale = argumentType.Scale;
        _argumentType = argumentType;
        ZeroWhenEmpty = zeroWhenEmpty;
    }

    protected bool ZeroWhenEmpty { get; }

    protected ChalkType ArgumentType => _argumentType;

    public override long Bytes => _seen.Length * sizeof(long);

    public override void Ensure(int groups) => Grow(ref _seen, groups);

    public override void Add(int group, ReadOnlySpan<byte> lane, bool valid, long weight)
    {
        if (!valid)
        {
            return;
        }

        _seen[group] += weight;
        Fold(group, lane, weight);
    }

    public override void Clear(int group) => _seen[group] = 0;

    public override void Write(int group, ChangeColumn into)
    {
        if (_seen[group] == 0 && !ZeroWhenEmpty)
        {
            into.AppendNull();
            return;
        }

        Span<byte> lane = stackalloc byte[ResultWidth];
        WriteValue(group, lane);
        into.AppendLane(lane);
    }

    protected abstract void Fold(int group, ReadOnlySpan<byte> lane, long weight);

    public override void Merge(int group, MaintainedMeasure other, int from)
    {
        var sum = (MaintainedSum)other;
        _seen[group] += sum._seen[from];
        MergeSum(group, sum, from);
    }

    protected abstract void MergeSum(int group, MaintainedSum other, int from);

    /// <summary>The group's sum, or zero when it holds no value, into a lane of the result's type.</summary>
    protected abstract void WriteValue(int group, Span<byte> lane);

    protected long ReadInteger(ReadOnlySpan<byte> lane) => _argumentKind switch
    {
        ColumnKind.Int8 => (sbyte)lane[0],
        ColumnKind.Int16 => MemoryMarshal.Read<short>(lane),
        ColumnKind.Int32 => MemoryMarshal.Read<int>(lane),
        ColumnKind.Int64 => MemoryMarshal.Read<long>(lane),
        ColumnKind.Float => checked((long)MemoryMarshal.Read<float>(lane)),
        ColumnKind.Double => checked((long)MemoryMarshal.Read<double>(lane)),
        _ => checked((long)Decimals.Read(lane, _argumentScale)),
    };

    protected double ReadDouble(ReadOnlySpan<byte> lane) => _argumentKind switch
    {
        ColumnKind.Float => MemoryMarshal.Read<float>(lane),
        ColumnKind.Double => MemoryMarshal.Read<double>(lane),
        ColumnKind.Decimal128 => (double)Decimals.Read(lane, _argumentScale),
        _ => ReadInteger(lane),
    };

    protected decimal ReadDecimal(ReadOnlySpan<byte> lane) => _argumentKind switch
    {
        ColumnKind.Decimal128 => Decimals.Read(lane, _argumentScale),
        ColumnKind.Float => (decimal)MemoryMarshal.Read<float>(lane),
        ColumnKind.Double => (decimal)MemoryMarshal.Read<double>(lane),
        _ => ReadInteger(lane),
    };
}

/// <summary>
/// A sum over integers: 128 bits, so rows put in and taken out in any order never overflow on the
/// way, and checked against the result's type when written (design 68 §4).
/// </summary>
internal sealed class MaintainedIntegerSum(ChalkType resultType, ChalkType argumentType, bool zeroWhenEmpty)
    : MaintainedSum(resultType, argumentType, zeroWhenEmpty)
{
    private Int128[] _sums = [];

    public override long Bytes => base.Bytes + (_sums.Length * 16L);

    public override void Ensure(int groups)
    {
        base.Ensure(groups);
        Grow(ref _sums, groups);
    }

    public override void Clear(int group)
    {
        base.Clear(group);
        _sums[group] = 0;
    }

    protected override void Fold(int group, ReadOnlySpan<byte> lane, long weight) =>
        _sums[group] += (Int128)ReadInteger(lane) * weight;

    protected override void WriteValue(int group, Span<byte> lane) =>
        NumberLanes.WriteInteger(ResultKind, ResultType, checked((long)_sums[group]), lane);

    protected override void MergeSum(int group, MaintainedSum other, int from) =>
        _sums[group] += ((MaintainedIntegerSum)other)._sums[from];

    public override MaintainedMeasure Twin() => new MaintainedIntegerSum(ResultType, ArgumentType, ZeroWhenEmpty);
}

/// <summary>A sum over <c>DECIMAL</c>: exact, as the executor's is, in a CLR decimal.</summary>
internal sealed class MaintainedDecimalSum(ChalkType resultType, ChalkType argumentType, bool zeroWhenEmpty)
    : MaintainedSum(resultType, argumentType, zeroWhenEmpty)
{
    private decimal[] _sums = [];

    public override long Bytes => base.Bytes + (_sums.Length * 16L);

    public override void Ensure(int groups)
    {
        base.Ensure(groups);
        Grow(ref _sums, groups);
    }

    public override void Clear(int group)
    {
        base.Clear(group);
        _sums[group] = 0;
    }

    protected override void Fold(int group, ReadOnlySpan<byte> lane, long weight) =>
        _sums[group] += ReadDecimal(lane) * weight;

    protected override void WriteValue(int group, Span<byte> lane) =>
        Decimals.Write(lane, _sums[group], ResultType.Precision, ResultType.Scale);

    protected override void MergeSum(int group, MaintainedSum other, int from) =>
        _sums[group] += ((MaintainedDecimalSum)other)._sums[from];

    public override MaintainedMeasure Twin() => new MaintainedDecimalSum(ResultType, ArgumentType, ZeroWhenEmpty);
}

/// <summary>
/// A sum over <c>DOUBLE</c> or <c>REAL</c>: exact, rounded once when written (design 68 §4), so it is
/// the correctly rounded sum of the group's rows whatever order they arrived and left in.
/// </summary>
internal sealed class MaintainedFloatSum(ChalkType resultType, ChalkType argumentType, bool zeroWhenEmpty)
    : MaintainedSum(resultType, argumentType, zeroWhenEmpty)
{
    private ExactSum?[] _sums = [];

    public override long Bytes
    {
        get
        {
            var bytes = base.Bytes + (_sums.Length * (long)IntPtr.Size);
            foreach (var sum in _sums)
            {
                bytes += sum?.Bytes ?? 0;
            }

            return bytes;
        }
    }

    public override void Ensure(int groups)
    {
        base.Ensure(groups);
        Grow(ref _sums, groups);
    }

    public override void Clear(int group)
    {
        base.Clear(group);
        _sums[group]?.Clear();
    }

    protected override void Fold(int group, ReadOnlySpan<byte> lane, long weight)
    {
        // A REAL argument summed into a REAL is added as the executor adds it: as a float.
        var value = ReadDouble(lane);
        if (ResultKind == ColumnKind.Float)
        {
            value = (float)value;
        }

        (_sums[group] ??= new ExactSum()).Add(value, weight);
    }

    protected override void MergeSum(int group, MaintainedSum other, int from)
    {
        if (((MaintainedFloatSum)other)._sums[from] is { } changes)
        {
            (_sums[group] ??= new ExactSum()).Merge(changes);
        }
    }

    public override MaintainedMeasure Twin() => new MaintainedFloatSum(ResultType, ArgumentType, ZeroWhenEmpty);

    protected override void WriteValue(int group, Span<byte> lane)
    {
        var sum = _sums[group];
        if (ResultKind == ColumnKind.Float)
        {
            MemoryMarshal.Write(lane, sum?.RoundSingle() ?? 0f);
        }
        else
        {
            MemoryMarshal.Write(lane, sum?.Round() ?? 0d);
        }
    }
}

/// <summary>
/// <c>MIN</c> and <c>MAX</c>: a count for each value a group holds, so that taking the extreme out
/// leaves the next one (design 68 §2). NULL for a group with no value.
/// </summary>
internal sealed class MaintainedExtremum : MaintainedMeasure
{
    private readonly bool _max;
    private readonly bool _signed;
    private readonly LaneOrder _order;
    private Values?[] _values = [];

    /// <param name="resultType">The value's type.</param>
    /// <param name="max">MAX rather than MIN.</param>
    /// <param name="signed">
    /// Counts may go below zero: an update's changes added up, where a value can leave that did not
    /// arrive in the same update.
    /// </param>
    public MaintainedExtremum(ChalkType resultType, bool max, bool signed = false)
        : base(resultType)
    {
        _max = max;
        _signed = signed;
        _order = new LaneOrder(ResultKind);
        Kinds.Of(resultType, $"{(max ? "MAX" : "MIN")}'s value");
    }

    public override long Bytes
    {
        get
        {
            var bytes = _values.Length * (long)IntPtr.Size;
            foreach (var values in _values)
            {
                bytes += values?.Bytes ?? 0;
            }

            return bytes;
        }
    }

    public override void Ensure(int groups) => Grow(ref _values, groups);

    public override void Add(int group, ReadOnlySpan<byte> lane, bool valid, long weight)
    {
        if (!valid)
        {
            return;
        }

        (_values[group] ??= new Values(_order, _signed)).Add(lane, weight);
    }

    public override void Clear(int group) => _values[group]?.Clear();

    public override void Merge(int group, MaintainedMeasure other, int from)
    {
        if (((MaintainedExtremum)other)._values[from] is not { } changes)
        {
            return;
        }

        foreach (var (value, count) in changes.Counted())
        {
            Add(group, value, valid: true, count);
        }
    }

    public override MaintainedMeasure Twin() => new MaintainedExtremum(ResultType, _max, signed: true);

    public override void Write(int group, ChangeColumn into)
    {
        if (_values[group] is not { Count: > 0 } values)
        {
            into.AppendNull();
            return;
        }

        var extreme = _max ? values.Max : values.Min;
        if (ColumnKinds.IsVariableLength(ResultKind))
        {
            into.AppendBytes(extreme);
        }
        else
        {
            into.AppendLane(extreme);
        }
    }

    /// <summary>One group's values, ordered, with how many times each is held.</summary>
    private sealed class Values(LaneOrder order, bool signed)
    {
        private readonly SortedSet<byte[]> _ordered = new(order);
        private readonly ByteMap _ids = new();
        private long[] _counts = new long[4];
        private int[] _free = new int[4];
        private int _freeCount;
        private int _used;

        public int Count => _ordered.Count;

        public byte[] Min => _ordered.Min!;

        public byte[] Max => _ordered.Max!;

        public long Bytes => _ids.Bytes + (_counts.Length * sizeof(long)) + (_ordered.Count * 48L);

        /// <summary>Each value held, with its count: for a merge, so the order is the set's.</summary>
        public IEnumerable<(byte[] Value, long Count)> Counted()
        {
            foreach (var value in _ordered)
            {
                _ids.TryGet(value, out var id);
                yield return (value, _counts[id]);
            }
        }

        public void Add(ReadOnlySpan<byte> lane, long weight)
        {
            ref var id = ref _ids.GetOrAdd(lane, out var added);
            if (!added)
            {
                var held = id;
                _counts[held] += weight;
                if (_counts[held] != 0)
                {
                    return;
                }

                // Gone: out of the order, and its id free for another value.
                _ids.Remove(lane, out _);
                _ordered.Remove(lane.ToArray());
                if (_freeCount == _free.Length)
                {
                    Array.Resize(ref _free, _free.Length * 2);
                }

                _free[_freeCount++] = held;
                return;
            }

            if (weight < 0 && !signed)
            {
                throw new InvalidOperationException(
                    "a maintained MIN or MAX was asked to take out a value it does not hold.");
            }

            var fresh = _freeCount > 0 ? _free[--_freeCount] : _used++;
            if (fresh >= _counts.Length)
            {
                Array.Resize(ref _counts, _counts.Length * 2);
            }

            id = fresh;
            _counts[fresh] = weight;
            _ordered.Add(lane.ToArray());
        }

        public void Clear()
        {
            _ordered.Clear();
            _ids.Clear();
            _freeCount = 0;
            _used = 0;
        }
    }
}
